from __future__ import annotations

import csv
import json
import os
import re
import shutil
import sys
import tempfile
import threading
import time
from dataclasses import dataclass, asdict
from datetime import datetime
from pathlib import Path
from typing import Callable, Optional
from urllib.parse import urljoin

import xlsxwriter
from playwright.sync_api import Page, TimeoutError as PlaywrightTimeoutError, sync_playwright

APP_VERSION = "0.2.0"
CATALOG_URL = "https://b2b-mems.ru/catalog"
CARD = 'div.card:has(div.font-mono):has(a[href*="/catalog/"])'
PRICE_RE = re.compile(r"([0-9][0-9\s\u00a0]*)\s*₽")
COUNT_RE = re.compile(r"([0-9\s\u00a0]+)\s+товар", re.I)
PAGE_RE = re.compile(r"^\d+$")


@dataclass
class Product:
    sku: str = ""
    name: str = ""
    brand: str = ""
    availability: str = ""
    price: Optional[int] = None
    url: str = ""
    source_page: int = 0
    seen_at: str = ""


def app_base() -> Path:
    return Path(sys.executable if getattr(sys, "frozen", False) else __file__).resolve().parent


def writable_dir(name: str) -> Path:
    candidates = [
        app_base() / name,
        Path.home() / "Documents" / "MEMS Catalog Parser" / name,
        Path(os.environ.get("LOCALAPPDATA", tempfile.gettempdir())) / "MEMS Catalog Parser" / name,
    ]
    for p in candidates:
        try:
            p.mkdir(parents=True, exist_ok=True)
            test = p / f".write_{os.getpid()}.tmp"
            test.write_text("ok", encoding="utf-8")
            test.unlink(missing_ok=True)
            return p
        except Exception:
            continue
    raise OSError("Не удалось найти доступную папку для записи")


class Log:
    def __init__(self):
        self.dir = writable_dir("Logs")
        stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
        self.path = self.dir / f"MEMS_Parser_{stamp}.log"
        self.latest = self.dir / "MEMS_Parser_LATEST.log"
        self.lock = threading.Lock()
        self.write(f"MEMS Catalog Parser {APP_VERSION} started")

    def write(self, msg: str):
        line = f"{datetime.now().isoformat(timespec='seconds')} | {msg}\n"
        with self.lock:
            try:
                with self.path.open("a", encoding="utf-8") as f:
                    f.write(line)
                shutil.copyfile(self.path, self.latest)
            except Exception:
                pass


def norm(s: str) -> str:
    return re.sub(r"\s+", " ", (s or "").replace("\u00a0", " ")).strip()


def find_chrome() -> Path:
    for env in ("PROGRAMFILES", "PROGRAMFILES(X86)", "LOCALAPPDATA"):
        root = os.environ.get(env)
        if root:
            p = Path(root) / "Google" / "Chrome" / "Application" / "chrome.exe"
            if p.exists():
                return p
    raise FileNotFoundError("Google Chrome не найден")


def parser_profile() -> Path:
    p = Path(os.environ.get("LOCALAPPDATA", tempfile.gettempdir())) / "MEMS Catalog Parser" / "BrowserProfileSimple"
    p.mkdir(parents=True, exist_ok=True)
    return p


def product_cards(page: Page):
    return page.locator(CARD)


def catalog_ready(page: Page) -> bool:
    try:
        return "/catalog" in (page.url or "") and product_cards(page).count() > 0
    except Exception:
        return False


def wait_for_login_and_catalog(page: Page, stop: threading.Event, status: Callable[[str], None], log: Log):
    deadline = time.monotonic() + 900
    next_catalog_try = 0.0
    while time.monotonic() < deadline:
        if stop.is_set():
            raise RuntimeError("Остановлено пользователем")
        if catalog_ready(page):
            log.write(f"Catalog ready: {page.url}")
            return
        url = page.url or ""
        if "/login" in url:
            status("Войдите в MEMS в открытом Chrome. После входа всё продолжится автоматически.")
        else:
            status("Жду каталог MEMS… Если открылась главная — можно нажать «Каталог».")
            if time.monotonic() >= next_catalog_try:
                next_catalog_try = time.monotonic() + 5
                try:
                    page.goto(CATALOG_URL, wait_until="domcontentloaded", timeout=15000)
                except Exception:
                    pass
        page.wait_for_timeout(500)
    raise TimeoutError("Не дождался авторизации и каталога за 15 минут")


def try_enable_in_stock(page: Page, log: Log) -> bool:
    try:
        labels = page.locator("label").filter(has_text="В наличии")
        for i in range(labels.count()):
            lab = labels.nth(i)
            if not lab.is_visible():
                continue
            cb = lab.locator('input[type="checkbox"]')
            if not cb.count():
                continue
            cb = cb.first
            if not cb.is_checked():
                lab.click(timeout=4000)
                try:
                    page.wait_for_timeout(400)
                    page.wait_for_load_state("networkidle", timeout=5000)
                except PlaywrightTimeoutError:
                    page.wait_for_timeout(800)
            ok = cb.is_checked()
            log.write(f"In-stock filter attempted; checked={ok}")
            return ok
    except Exception as e:
        log.write(f"In-stock filter error (non-fatal): {type(e).__name__}: {e}")
    log.write("In-stock filter not found; using local filtering")
    return False


def total_count(page: Page) -> Optional[int]:
    try:
        text = norm(page.locator("body").inner_text(timeout=3000))
        m = COUNT_RE.search(text)
        if m:
            d = re.sub(r"\D", "", m.group(1))
            return int(d) if d else None
    except Exception:
        pass
    return None


def parse_price(text: str) -> Optional[int]:
    m = PRICE_RE.search(text or "")
    if not m:
        return None
    d = re.sub(r"\D", "", m.group(1))
    return int(d) if d else None


def parse_card(card, page_no: int) -> Optional[Product]:
    try:
        sku = ""
        mono = card.locator("div.font-mono")
        if mono.count():
            sku = norm(mono.first.inner_text(timeout=500))
        name = ""
        href = ""
        links = card.locator('a[href*="/catalog/"]')
        for i in range(links.count()):
            a = links.nth(i)
            h = a.get_attribute("href") or ""
            if not h:
                continue
            txt = norm(a.inner_text(timeout=500))
            if txt:
                name = txt
                href = h
                break
            if not href:
                href = h
        if not name:
            img = card.locator("img[alt]")
            if img.count():
                name = norm(img.first.get_attribute("alt") or "")
        if not href:
            return None
        href = urljoin(CATALOG_URL, href)
        txt = norm(card.inner_text(timeout=1200))
        if "Нет в наличии" in txt or "Предзаказ" in txt:
            availability = "Нет в наличии"
        elif "В наличии" in txt:
            availability = "В наличии"
        elif "На складе" in txt:
            availability = "На складе"
        else:
            availability = "Доступность не указана"
        brand = ""
        smalls = card.locator('div[class*="text-[10px]"]')
        for i in range(smalls.count()):
            s = norm(smalls.nth(i).inner_text(timeout=300))
            if s and s != sku and s not in name and len(s) <= 100 and not s.lower().startswith("новин"):
                brand = s
        return Product(sku=sku, name=name, brand=brand, availability=availability, price=parse_price(txt), url=href, source_page=page_no, seen_at=datetime.now().isoformat(timespec="seconds"))
    except Exception:
        return None


def page_rows(page: Page, page_no: int) -> list[Product]:
    cards = product_cards(page)
    rows = []
    for i in range(cards.count()):
        p = parse_card(cards.nth(i), page_no)
        if p:
            rows.append(p)
    return rows


def product_key(p: Product) -> str:
    return (p.sku or p.url or p.name).strip().lower()


def is_in_stock(p: Product, site_filter_active: bool) -> bool:
    if p.availability == "Нет в наличии":
        return False
    if site_filter_active:
        return True
    return p.availability in {"В наличии", "На складе", "Доступность не указана"}


def current_page_number(page: Page, fallback: int) -> int:
    try:
        buttons = page.locator("button")
        for i in range(buttons.count()):
            b = buttons.nth(i)
            try:
                cls = b.get_attribute("class") or ""
                t = norm(b.inner_text(timeout=150))
                if "bg-brand-600" in cls and PAGE_RE.match(t):
                    return int(t)
            except Exception:
                pass
    except Exception:
        pass
    return fallback


def next_button(page: Page):
    try:
        loc = page.locator("button:has(svg.lucide-chevron-right)")
        visible = []
        for i in range(loc.count()):
            b = loc.nth(i)
            if b.is_visible():
                visible.append(b)
        return visible[-1] if visible else None
    except Exception:
        return None


def atomic_csv(rows: list[Product], path: Path):
    tmp = path.with_suffix(path.suffix + ".tmp")
    fields = ["sku", "name", "brand", "availability", "price", "url", "source_page", "seen_at"]
    with tmp.open("w", newline="", encoding="utf-8-sig") as f:
        w = csv.DictWriter(f, fieldnames=fields, delimiter=";")
        w.writeheader()
        for p in sorted(rows, key=lambda x: (x.brand.lower(), x.name.lower(), x.sku.lower())):
            w.writerow(asdict(p))
    os.replace(tmp, path)


def export_xlsx(rows: list[Product], path: Path, info: dict):
    tmp = path.with_name(path.stem + ".tmp.xlsx")
    wb = xlsxwriter.Workbook(str(tmp))
    ws = wb.add_worksheet("Товары в наличии")
    meta = wb.add_worksheet("Инфо")
    head = wb.add_format({"bold": True, "bg_color": "#1F4E78", "font_color": "#FFFFFF", "border": 1})
    money = wb.add_format({"num_format": '#,##0 "₽"'})
    warn = wb.add_format({"bg_color": "#FFF2CC"})
    headers = ["Артикул", "Название", "Бренд / метка", "Наличие", "Цена", "Ссылка", "Страница"]
    for c, h in enumerate(headers):
        ws.write(0, c, h, head)
    for r, p in enumerate(sorted(rows, key=lambda x: (x.brand.lower(), x.name.lower(), x.sku.lower())), 1):
        ws.write(r, 0, p.sku)
        ws.write(r, 1, p.name)
        ws.write(r, 2, p.brand)
        ws.write(r, 3, p.availability, warn if p.availability == "Доступность не указана" else None)
        if p.price is not None:
            ws.write_number(r, 4, p.price, money)
        ws.write_url(r, 5, p.url, string=p.url)
        ws.write_number(r, 6, p.source_page)
    ws.freeze_panes(1, 0)
    ws.autofilter(0, 0, max(1, len(rows)), len(headers) - 1)
    for col, width in enumerate((18, 64, 24, 24, 14, 70, 10)):
        ws.set_column(col, col, width)
    meta.set_column(0, 0, 28)
    meta.set_column(1, 1, 90)
    for r, (k, v) in enumerate(info.items()):
        meta.write(r, 0, str(k))
        meta.write(r, 1, str(v))
    wb.close()
    os.replace(tmp, path)


def save_checkpoint(output: Path, in_stock: dict[str, Product], all_seen: dict[str, Product], state: dict, log: Log, make_xlsx: bool):
    output.mkdir(parents=True, exist_ok=True)
    atomic_csv(list(in_stock.values()), output / "MEMS_В_НАЛИЧИИ_LIVE.csv")
    atomic_csv(list(all_seen.values()), output / "MEMS_ВСЕ_ПРОСМОТРЕННЫЕ_LIVE.csv")
    state_path = output / "MEMS_CHECKPOINT.json"
    tmp = state_path.with_suffix(".json.tmp")
    tmp.write_text(json.dumps(state, ensure_ascii=False, indent=2), encoding="utf-8")
    os.replace(tmp, state_path)
    if make_xlsx:
        try:
            export_xlsx(list(in_stock.values()), output / "MEMS_В_НАЛИЧИИ_LIVE.xlsx", state)
        except Exception as e:
            log.write(f"LIVE XLSX warning: {type(e).__name__}: {e}")


def save_debug(page: Page, output: Path, n: int, log: Log):
    root = output / "Debug"
    root.mkdir(parents=True, exist_ok=True)
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    try:
        (root / f"page_{n}_{stamp}.html").write_text(page.content(), encoding="utf-8")
    except Exception as e:
        log.write(f"Debug HTML warning: {e}")
    try:
        page.screenshot(path=str(root / f"page_{n}_{stamp}.png"), full_page=True)
    except Exception as e:
        log.write(f"Debug screenshot warning: {e}")


def run(output: Path, delay_ms: int, stop: threading.Event, status: Callable[[str], None], progress: Callable[[int, int], None], log: Log) -> tuple[Path, list[str]]:
    chrome = find_chrome()
    profile = parser_profile()
    warnings: list[str] = []
    all_seen: dict[str, Product] = {}
    in_stock: dict[str, Product] = {}
    pages_done = 0
    site_total: Optional[int] = None
    site_filter_active = False
    last_page = 0
    final_path = output / f"MEMS_в_наличии_{datetime.now().strftime('%Y%m%d_%H%M%S')}.xlsx"
    output.mkdir(parents=True, exist_ok=True)
    ctx = None
    page = None
    try:
        with sync_playwright() as pw:
            ctx = pw.chromium.launch_persistent_context(user_data_dir=str(profile), executable_path=str(chrome), headless=False, viewport=None, args=["--start-maximized"])
            page = ctx.pages[0] if ctx.pages else ctx.new_page()
            page.set_default_timeout(8000)
            status("Открываю MEMS…")
            page.goto(CATALOG_URL, wait_until="domcontentloaded", timeout=30000)
            wait_for_login_and_catalog(page, stop, status, log)
            site_filter_active = try_enable_in_stock(page, log)
            page.wait_for_timeout(800)
            site_total = total_count(page)
            log.write(f"Start crawl; site_filter_active={site_filter_active}; site_total={site_total}")
            expected_pages = ((site_total + 39) // 40) if site_total else 0
            fallback_page = 1
            visited_signatures: set[str] = set()
            while True:
                if stop.is_set():
                    warnings.append("Остановлено пользователем — сохранён накопленный результат")
                    break
                page_no = current_page_number(page, fallback_page)
                last_page = page_no
                rows = page_rows(page, page_no)
                if not rows:
                    page.wait_for_timeout(1000)
                    rows = page_rows(page, page_no)
                if not rows:
                    warnings.append(f"Страница {page_no}: карточки не найдены; остановлено с сохранением")
                    save_debug(page, output, page_no, log)
                    break
                sig = "|".join((x.sku or x.url) for x in rows[:5])
                if sig in visited_signatures:
                    warnings.append(f"Повторилась страница {page_no}; обход остановлен, чтобы не зациклиться")
                    break
                visited_signatures.add(sig)
                added_all = added_stock = 0
                for item in rows:
                    key = product_key(item)
                    if not key:
                        continue
                    if key not in all_seen:
                        all_seen[key] = item
                        added_all += 1
                    if is_in_stock(item, site_filter_active) and key not in in_stock:
                        in_stock[key] = item
                        added_stock += 1
                pages_done += 1
                state = {"version": APP_VERSION, "updated": datetime.now().isoformat(timespec="seconds"), "last_page": page_no, "pages_done": pages_done, "site_total": site_total if site_total is not None else "unknown", "site_filter_active": site_filter_active, "all_seen": len(all_seen), "in_stock": len(in_stock), "warnings": len(warnings)}
                save_checkpoint(output, in_stock, all_seen, state, log, make_xlsx=(pages_done == 1 or pages_done % 10 == 0))
                status(f"Страница {page_no}: +{added_stock} в наличии; всего {len(in_stock)}. Сохранено на диск.")
                progress(pages_done, expected_pages or max(pages_done, 1))
                log.write(f"Page={page_no}; cards={len(rows)}; +all={added_all}; +stock={added_stock}; all={len(all_seen)}; stock={len(in_stock)}")
                b = next_button(page)
                if b is None:
                    break
                try:
                    if b.is_disabled():
                        break
                except Exception:
                    pass
                old_sig = sig
                try:
                    b.click(timeout=5000)
                except Exception as e:
                    warnings.append(f"Не удалось нажать следующую страницу после {page_no}: {type(e).__name__}")
                    save_debug(page, output, page_no, log)
                    break
                moved = False
                for _ in range(50):
                    if stop.is_set():
                        break
                    page.wait_for_timeout(150)
                    new_rows = page_rows(page, page_no + 1)
                    new_sig = "|".join((x.sku or x.url) for x in new_rows[:5])
                    if new_sig and new_sig != old_sig:
                        moved = True
                        break
                if not moved:
                    warnings.append(f"После страницы {page_no} содержимое не изменилось; обход завершён с сохранением")
                    save_debug(page, output, page_no, log)
                    break
                fallback_page = page_no + 1
                page.wait_for_timeout(max(150, delay_ms))
    except Exception as e:
        warnings.append(f"{type(e).__name__}: {e}")
        log.write(f"CRAWL WARNING: {type(e).__name__}: {e}")
        if page is not None:
            try:
                save_debug(page, output, last_page or 1, log)
            except Exception:
                pass
    finally:
        state = {"version": APP_VERSION, "finished": datetime.now().isoformat(timespec="seconds"), "last_page": last_page, "pages_done": pages_done, "site_total": site_total if site_total is not None else "unknown", "site_filter_active": site_filter_active, "all_seen": len(all_seen), "in_stock": len(in_stock), "warnings": " | ".join(warnings) if warnings else "нет"}
        try:
            save_checkpoint(output, in_stock, all_seen, state, log, make_xlsx=True)
        except Exception as e:
            warnings.append(f"Финальный checkpoint: {type(e).__name__}: {e}")
            log.write(warnings[-1])
        try:
            export_xlsx(list(in_stock.values()), final_path, state)
            shutil.copy2(final_path, output / "MEMS_В_НАЛИЧИИ_LATEST.xlsx")
        except Exception as e:
            warnings.append(f"Финальный XLSX: {type(e).__name__}: {e}; используйте LIVE.csv")
            log.write(warnings[-1])
        try:
            (output / "MEMS_WARNINGS.txt").write_text("\n".join(warnings) if warnings else "Ошибок и предупреждений нет.\n", encoding="utf-8")
        except Exception:
            pass
        if ctx is not None:
            try:
                ctx.close()
            except Exception as e:
                log.write(f"Browser close warning ignored: {type(e).__name__}: {e}")
        log.write(f"FINAL pages={pages_done}; all={len(all_seen)}; stock={len(in_stock)}; warnings={len(warnings)}; final={final_path}")
    return final_path, warnings


def self_test() -> int:
    chrome = find_chrome()
    html = '''<div class="card"><div class="font-mono">A-1</div><a href="/catalog/test-1"><div>Рабочий товар</div></a><div class="text-[10px]">Brand A</div><span>В наличии</span><span>12&nbsp;345 ₽</span></div><div class="card"><div class="font-mono">A-2</div><a href="/catalog/test-2"><div>Нет товара</div></a><div class="text-[10px]">Brand B</div><span>Нет в наличии</span><span>2&nbsp;000 ₽</span><button>Предзаказ</button></div><div class="card"><h2>Вход для клиентов</h2><input type="tel"></div>'''
    with sync_playwright() as pw:
        b = pw.chromium.launch(executable_path=str(chrome), headless=True)
        page = b.new_page()
        page.set_content(html)
        rows = page_rows(page, 1)
        assert len(rows) == 2, len(rows)
        assert rows[0].sku == "A-1" and rows[0].price == 12345
        assert is_in_stock(rows[0], False)
        assert not is_in_stock(rows[1], False)
        b.close()
    with tempfile.TemporaryDirectory() as td:
        out = Path(td)
        log = Log()
        stock = {product_key(rows[0]): rows[0]}
        all_seen = {product_key(x): x for x in rows}
        state = {"pages_done": 1, "in_stock": 1}
        save_checkpoint(out, stock, all_seen, state, log, True)
        assert (out / "MEMS_В_НАЛИЧИИ_LIVE.csv").exists()
        assert (out / "MEMS_В_НАЛИЧИИ_LIVE.xlsx").exists()
        assert (out / "MEMS_ВСЕ_ПРОСМОТРЕННЫЕ_LIVE.csv").exists()
    print("SELF-TEST OK")
    return 0
