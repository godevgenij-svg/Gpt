from __future__ import annotations

import json
import os
import re
import shutil
import sys
import tempfile
import threading
import time
from dataclasses import dataclass
from datetime import datetime
from pathlib import Path
from typing import Callable, Optional

import xlsxwriter
from playwright.sync_api import Page, TimeoutError as PlaywrightTimeoutError, sync_playwright

APP_VERSION = "0.1.0"
CATALOG_URL = "https://b2b-mems.ru/catalog"
CARD = "div.card"
PRICE_RE = re.compile(r"([0-9][0-9\s\u00a0]*)\s*₽")
COUNT_RE = re.compile(r"([0-9\s\u00a0]+)\s+товар", re.I)


@dataclass
class Product:
    sku: str
    name: str
    brand: str
    availability: str
    price: Optional[int]
    url: str


def writable_dir(name: str) -> Path:
    base = Path(sys.executable if getattr(sys, "frozen", False) else __file__).resolve().parent
    candidates = [
        base / name,
        Path.home() / "Documents" / "MEMS Catalog Parser" / name,
        Path(os.environ.get("LOCALAPPDATA", tempfile.gettempdir())) / "MEMS Catalog Parser" / name,
    ]
    for p in candidates:
        try:
            p.mkdir(parents=True, exist_ok=True)
            t = p / f".write_{os.getpid()}.tmp"
            t.write_text("ok", encoding="utf-8")
            t.unlink(missing_ok=True)
            return p
        except Exception:
            pass
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
            with self.path.open("a", encoding="utf-8") as f:
                f.write(line)
            try:
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


def chrome_profile_source() -> tuple[Optional[Path], str]:
    local = os.environ.get("LOCALAPPDATA")
    if not local:
        return None, "Default"
    root = Path(local) / "Google" / "Chrome" / "User Data"
    if not root.exists():
        return None, "Default"
    profile = "Default"
    try:
        state = json.loads((root / "Local State").read_text(encoding="utf-8"))
        profile = state.get("profile", {}).get("last_used") or "Default"
    except Exception:
        pass
    return root, profile


def _ignore_profile(_path: str, names: list[str]) -> set[str]:
    drop = {"Cache", "Code Cache", "GPUCache", "DawnCache", "GrShaderCache", "ShaderCache", "Crashpad", "BrowserMetrics", "Safe Browsing"}
    return {n for n in names if n in drop}


def parser_profile(log: Log, refresh: bool = False) -> tuple[Path, str]:
    root = Path(os.environ.get("LOCALAPPDATA", tempfile.gettempdir())) / "MEMS Catalog Parser" / "BrowserProfile"
    marker = root / ".ready.json"
    src, profile = chrome_profile_source()
    if refresh and root.exists():
        try:
            shutil.rmtree(root)
        except Exception:
            pass
    if marker.exists():
        try:
            profile = json.loads(marker.read_text(encoding="utf-8")).get("profile") or profile
        except Exception:
            pass
        return root, profile
    root.mkdir(parents=True, exist_ok=True)
    copied = False
    if src and (src / profile).exists():
        try:
            if (src / "Local State").exists():
                shutil.copy2(src / "Local State", root / "Local State")
            shutil.copytree(src / profile, root / profile, dirs_exist_ok=True, ignore=_ignore_profile)
            copied = True
        except Exception as e:
            log.write(f"Chrome profile copy incomplete: {type(e).__name__}: {e}")
    marker.write_text(json.dumps({"profile": profile, "copied": copied}), encoding="utf-8")
    log.write(f"Browser profile ready; profile={profile}; copied={copied}")
    return root, profile


def visible(locator):
    for i in range(locator.count()):
        x = locator.nth(i)
        try:
            if x.is_visible():
                return x
        except Exception:
            pass
    return None


def force_grid(page: Page):
    try:
        b = visible(page.locator("button:has(svg.lucide-layout-grid)"))
        if b:
            b.click(timeout=2000)
            page.wait_for_timeout(300)
    except Exception:
        pass


def wait_auth_catalog(page: Page, stop: threading.Event, status: Callable[[str], None], log: Log):
    until = time.monotonic() + 600
    while time.monotonic() < until:
        if stop.is_set():
            raise RuntimeError("Остановлено пользователем")
        try:
            if "b2b-mems.ru" in page.url and page.locator(CARD).count() > 0:
                return
        except Exception:
            pass
        status("Если MEMS просит вход — авторизуйтесь в открытом Chrome. Продолжение автоматическое.")
        time.sleep(1)
    raise TimeoutError("Каталог не открылся за 10 минут")


def set_checkbox_filter(page: Page, text: str, enabled: bool, log: Log):
    labels = page.locator("label").filter(has_text=text)
    for i in range(labels.count()):
        lab = labels.nth(i)
        try:
            if not lab.is_visible():
                continue
            cb = lab.locator('input[type="checkbox"]')
            if not cb.count():
                continue
            cb = cb.first
            if cb.is_checked() != enabled:
                lab.click(timeout=4000)
                try:
                    page.wait_for_load_state("networkidle", timeout=5000)
                except PlaywrightTimeoutError:
                    page.wait_for_timeout(1000)
            log.write(f"Filter {text}={enabled}")
            return
        except Exception:
            continue
    if enabled:
        raise RuntimeError(f"Не найден фильтр «{text}»")


def total_count(page: Page) -> Optional[int]:
    spans = page.locator("span")
    for i in range(spans.count()):
        try:
            if not spans.nth(i).is_visible():
                continue
            m = COUNT_RE.search(norm(spans.nth(i).inner_text(timeout=200)))
            if m:
                d = re.sub(r"\D", "", m.group(1))
                return int(d) if d else None
        except Exception:
            pass
    return None


def current_page(page: Page) -> int:
    active = page.locator("button.bg-brand-600")
    for i in range(active.count()):
        try:
            t = norm(active.nth(i).inner_text(timeout=200))
            if t.isdigit():
                return int(t)
        except Exception:
            pass
    return 1


def next_button(page: Page):
    loc = page.locator("button:has(svg.lucide-chevron-right)")
    found = []
    for i in range(loc.count()):
        try:
            if loc.nth(i).is_visible():
                found.append(loc.nth(i))
        except Exception:
            pass
    return found[-1] if found else None


def parse_price(text: str) -> Optional[int]:
    m = PRICE_RE.search(text or "")
    if not m:
        return None
    d = re.sub(r"\D", "", m.group(1))
    return int(d) if d else None


def parse_card(card) -> Optional[Product]:
    try:
        url = name = ""
        links = card.locator('a[href*="/catalog/"]')
        for i in range(links.count()):
            a = links.nth(i)
            href = a.get_attribute("href") or ""
            if not href or href.rstrip("/") == CATALOG_URL:
                continue
            if not url:
                url = href
            txt = norm(a.inner_text(timeout=500))
            if txt:
                name, url = txt, href
                break
            img = a.locator("img")
            if img.count() and not name:
                name = norm(img.first.get_attribute("alt") or "")
        if not url:
            return None
        sku = ""
        mono = card.locator("div.font-mono")
        if mono.count():
            sku = norm(mono.first.inner_text(timeout=500))
        txt = norm(card.inner_text(timeout=1000))
        availability = "Нет в наличии" if "Нет в наличии" in txt else ("В наличии" if "В наличии" in txt else ("На складе" if "На складе" in txt else ""))
        brand = ""
        small = card.locator('div[class*="text-[10px]"]')
        for i in range(small.count()):
            s = norm(small.nth(i).inner_text(timeout=300))
            if s and s != sku and s not in name and len(s) < 100:
                brand = s
        return Product(sku, name, brand, availability, parse_price(txt), url)
    except Exception:
        return None


def page_products(page: Page) -> list[Product]:
    force_grid(page)
    cards = page.locator(CARD)
    result = []
    for i in range(cards.count()):
        p = parse_card(cards.nth(i))
        if p:
            result.append(p)
    return result


def save_debug(page: Page, n: int, log: Log):
    root = writable_dir("Debug")
    stamp = datetime.now().strftime("%Y%m%d_%H%M%S")
    try:
        (root / f"page_{n}_{stamp}.html").write_text(page.content(), encoding="utf-8")
        page.screenshot(path=str(root / f"page_{n}_{stamp}.png"), full_page=True)
    except Exception as e:
        log.write(f"Debug save failed: {e}")


def export_xlsx(rows: list[Product], path: Path, reported: Optional[int], pages: int, warehouse: bool):
    wb = xlsxwriter.Workbook(str(path))
    ws = wb.add_worksheet("Товары в наличии")
    info = wb.add_worksheet("Инфо")
    head = wb.add_format({"bold": True, "bg_color": "#1F4E78", "font_color": "#FFFFFF", "border": 1, "align": "center"})
    money = wb.add_format({"num_format": '#,##0 "₽"'})
    headers = ["Артикул", "Название", "Бренд / метка", "Наличие", "Цена", "Ссылка"]
    for c, h in enumerate(headers):
        ws.write(0, c, h, head)
    for r, p in enumerate(rows, 1):
        ws.write(r, 0, p.sku)
        ws.write(r, 1, p.name)
        ws.write(r, 2, p.brand)
        ws.write(r, 3, p.availability)
        if p.price is not None:
            ws.write_number(r, 4, p.price, money)
        ws.write_url(r, 5, p.url, string=p.url)
    ws.freeze_panes(1, 0)
    ws.autofilter(0, 0, max(1, len(rows)), 5)
    for col, width in enumerate((18, 58, 22, 16, 14, 70)):
        ws.set_column(col, col, width)
    meta = [
        ("Источник", CATALOG_URL),
        ("Дата", datetime.now().strftime("%Y-%m-%d %H:%M:%S")),
        ("Версия", APP_VERSION),
        ("Фильтр", "В наличии" + (" + Основной склад Рязань" if warehouse else "")),
        ("Строк", len(rows)),
        ("Счётчик сайта", reported if reported is not None else "не определён"),
        ("Страниц", pages),
    ]
    info.set_column(0, 0, 25)
    info.set_column(1, 1, 70)
    for r, (k, v) in enumerate(meta):
        info.write(r, 0, k)
        info.write(r, 1, v)
    wb.close()


def run(output: Path, warehouse: bool, delay_ms: int, stop: threading.Event,
        status: Callable[[str], None], progress: Callable[[int, int], None], log: Log) -> Path:
    chrome = find_chrome()
    profile_root, profile_name = parser_profile(log)
    unique: dict[str, Product] = {}
    pages = 0
    reported = None
    with sync_playwright() as p:
        ctx = p.chromium.launch_persistent_context(
            user_data_dir=str(profile_root), executable_path=str(chrome), headless=False,
            viewport=None, args=[f"--profile-directory={profile_name}", "--start-maximized"])
        page = ctx.pages[0] if ctx.pages else ctx.new_page()
        page.set_default_timeout(8000)
        try:
            status("Открываю каталог MEMS…")
            page.goto(CATALOG_URL, wait_until="domcontentloaded", timeout=30000)
            wait_auth_catalog(page, stop, status, log)
            force_grid(page)
            set_checkbox_filter(page, "В наличии", True, log)
            if warehouse:
                set_checkbox_filter(page, "Основной склад Рязань", True, log)
            else:
                try:
                    set_checkbox_filter(page, "Основной склад Рязань", False, log)
                except Exception:
                    pass
            page.wait_for_timeout(800)
            reported = total_count(page)
            log.write(f"Filtered count={reported}")
            first_rows = page_products(page)
            if first_rows and sum(x.availability == "Нет в наличии" for x in first_rows) > max(1, len(first_rows)//5):
                save_debug(page, 1, log)
                raise RuntimeError("Фильтр «В наличии» не применился")
            while True:
                if stop.is_set():
                    raise RuntimeError("Остановлено пользователем")
                n = current_page(page)
                rows = first_rows if pages == 0 else page_products(page)
                if not rows:
                    page.wait_for_timeout(1000)
                    rows = page_products(page)
                if not rows:
                    save_debug(page, n, log)
                    raise RuntimeError(f"Страница {n}: карточки не найдены")
                added = 0
                for item in rows:
                    if item.availability == "Нет в наличии":
                        continue
                    key = (item.sku or item.url).strip().lower()
                    if key not in unique:
                        unique[key] = item
                        added += 1
                pages += 1
                status(f"Страница {n}: +{added}; собрано {len(unique)}" + (f" из {reported}" if reported else ""))
                progress(len(unique), reported or max(1, len(unique)))
                log.write(f"Page={n}; cards={len(rows)}; added={added}; collected={len(unique)}")
                b = next_button(page)
                if b is None:
                    break
                try:
                    if b.is_disabled():
                        break
                except Exception:
                    pass
                before = rows[0].url
                b.click(timeout=5000)
                moved = False
                for _ in range(40):
                    if stop.is_set():
                        raise RuntimeError("Остановлено пользователем")
                    page.wait_for_timeout(150)
                    try:
                        href = page.locator(f'{CARD} a[href*="/catalog/"]').first.get_attribute("href") or ""
                        if current_page(page) != n or (href and href != before):
                            moved = True
                            break
                    except Exception:
                        pass
                if not moved:
                    save_debug(page, n, log)
                    raise RuntimeError(f"Не удалось перейти со страницы {n}")
                first_rows = []
                page.wait_for_timeout(max(200, delay_ms))
            out = output / f"MEMS_в_наличии_{datetime.now().strftime('%Y%m%d_%H%M%S')}.xlsx"
            export_xlsx(list(unique.values()), out, reported, pages, warehouse)
            log.write(f"Done: {out}; rows={len(unique)}; pages={pages}")
            return out
        except Exception:
            try:
                save_debug(page, current_page(page), log)
            except Exception:
                pass
            raise
        finally:
            try:
                ctx.close()
            except Exception:
                pass


def self_test() -> int:
    chrome = find_chrome()
    html = '<div class="card"><a href="https://b2b-mems.ru/catalog/test"><img alt="Тестовый товар"></a><div class="font-mono">МТ-00000001</div><a href="https://b2b-mems.ru/catalog/test"><div>Тестовый товар</div></a><div class="text-[10px]">TestBrand</div><span>В наличии</span><span>12&nbsp;345 ₽</span></div>'
    with sync_playwright() as p:
        b = p.chromium.launch(executable_path=str(chrome), headless=True)
        page = b.new_page()
        page.set_content(html)
        rows = page_products(page)
        assert len(rows) == 1 and rows[0].sku == "МТ-00000001" and rows[0].price == 12345
        b.close()
    with tempfile.TemporaryDirectory() as td:
        f = Path(td) / "test.xlsx"
        export_xlsx(rows, f, 1, 1, False)
        assert f.exists() and f.stat().st_size > 1000
    print("SELF-TEST OK")
    return 0
