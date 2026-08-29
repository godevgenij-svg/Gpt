from __future__ import annotations

import os
import queue
import sys
import threading
import traceback
from pathlib import Path

import tkinter as tk
from tkinter import filedialog, messagebox, ttk

from parser_core import APP_VERSION, Log, parser_profile, run, self_test, writable_dir

APP_NAME = "MEMS Catalog Parser"
LOGGER: Log | None = None


class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title(f"{APP_NAME} {APP_VERSION}")
        self.geometry("760x470")
        self.minsize(700, 430)
        self.stop = threading.Event()
        self.worker: threading.Thread | None = None
        self.events: queue.Queue = queue.Queue()
        self.output = tk.StringVar(value=str(writable_dir("Output")))
        self.warehouse = tk.BooleanVar(value=False)
        self.delay = tk.IntVar(value=500)
        self.status = tk.StringVar(value="Готово. Нажмите «Начать выгрузку».")
        self.pct = tk.DoubleVar(value=0)
        self.counter = tk.StringVar(value="0 / 0")
        self._ui()
        self.after(150, self._poll)

    def _ui(self):
        f = ttk.Frame(self)
        f.pack(fill="both", expand=True, padx=12, pady=12)
        pad = {"padx": 12, "pady": 8}
        ttk.Label(f, text="MEMS B2B → Excel", font=("Segoe UI", 16, "bold")).grid(row=0, column=0, columnspan=3, sticky="w", **pad)
        ttk.Label(f, text="Парсер использует отдельный Chrome-профиль. Если авторизация не перенеслась автоматически, войдите в MEMS один раз в открытом Chrome — пароль программа не читает и не сохраняет.", wraplength=700).grid(row=1, column=0, columnspan=3, sticky="w", **pad)
        ttk.Label(f, text="Папка для Excel:").grid(row=2, column=0, sticky="w", **pad)
        ttk.Entry(f, textvariable=self.output).grid(row=2, column=1, sticky="ew", **pad)
        ttk.Button(f, text="Выбрать…", command=self._choose).grid(row=2, column=2, **pad)
        ttk.Checkbutton(f, text="Только основной склад Рязань", variable=self.warehouse).grid(row=3, column=0, columnspan=2, sticky="w", **pad)
        ttk.Label(f, text="Пауза между страницами, мс:").grid(row=4, column=0, sticky="w", **pad)
        ttk.Spinbox(f, from_=200, to=5000, increment=100, textvariable=self.delay, width=10).grid(row=4, column=1, sticky="w", **pad)
        b = ttk.Frame(f)
        b.grid(row=5, column=0, columnspan=3, sticky="ew", **pad)
        self.start_btn = ttk.Button(b, text="Начать выгрузку", command=self._start)
        self.start_btn.pack(side="left", padx=(0, 8))
        self.stop_btn = ttk.Button(b, text="Стоп", command=self._stop, state="disabled")
        self.stop_btn.pack(side="left", padx=(0, 8))
        ttk.Button(b, text="Обновить сессию из Chrome", command=self._refresh_session).pack(side="left", padx=(0, 8))
        ttk.Button(b, text="Открыть папку", command=self._open).pack(side="left")
        ttk.Progressbar(f, variable=self.pct, maximum=100).grid(row=6, column=0, columnspan=3, sticky="ew", padx=12, pady=(16, 4))
        ttk.Label(f, textvariable=self.counter).grid(row=7, column=0, sticky="w", padx=12)
        ttk.Label(f, textvariable=self.status, wraplength=700).grid(row=8, column=0, columnspan=3, sticky="nw", padx=12, pady=12)
        ttk.Separator(f).grid(row=9, column=0, columnspan=3, sticky="ew", padx=12, pady=8)
        ttk.Label(f, text="Результат: MEMS_в_наличии_YYYYMMDD_HHMMSS.xlsx\nЛог: Logs\\MEMS_Parser_LATEST.log\nПри ошибке: Debug\\page_*.html и .png", foreground="#555").grid(row=10, column=0, columnspan=3, sticky="w", padx=12, pady=8)
        f.columnconfigure(1, weight=1)

    def _choose(self):
        d = filedialog.askdirectory(initialdir=self.output.get())
        if d:
            self.output.set(d)

    def _open(self):
        p = Path(self.output.get())
        p.mkdir(parents=True, exist_ok=True)
        os.startfile(p)  # type: ignore[attr-defined]

    def _refresh_session(self):
        if self.worker and self.worker.is_alive():
            messagebox.showinfo(APP_NAME, "Сначала остановите текущую выгрузку")
            return
        try:
            parser_profile(LOGGER or Log(), refresh=True)
            messagebox.showinfo(APP_NAME, "Сессия Chrome обновлена. Если MEMS всё равно попросит вход, авторизуйтесь один раз в открытом окне Chrome.")
        except Exception as e:
            messagebox.showerror(APP_NAME, str(e))

    def _start(self):
        if self.worker and self.worker.is_alive():
            return
        try:
            Path(self.output.get()).mkdir(parents=True, exist_ok=True)
        except Exception as e:
            messagebox.showerror(APP_NAME, f"Не удаётся записывать в папку: {e}")
            return
        self.stop.clear()
        self.start_btn.config(state="disabled")
        self.stop_btn.config(state="normal")
        self.pct.set(0)
        self.counter.set("0 / 0")
        self.worker = threading.Thread(target=self._worker, daemon=True)
        self.worker.start()

    def _stop(self):
        self.stop.set()
        self.status.set("Останавливаю после текущей операции…")

    def _worker(self):
        try:
            result = run(
                Path(self.output.get()), bool(self.warehouse.get()), max(200, int(self.delay.get())), self.stop,
                lambda s: self.events.put(("status", s)),
                lambda a, b: self.events.put(("progress", a, b)),
                LOGGER or Log())
            self.events.put(("done", str(result)))
        except Exception as e:
            if LOGGER:
                LOGGER.write(f"ERROR {type(e).__name__}: {e}\n{traceback.format_exc()}")
            self.events.put(("error", f"{type(e).__name__}: {e}"))

    def _poll(self):
        try:
            while True:
                e = self.events.get_nowait()
                if e[0] == "status":
                    self.status.set(e[1])
                elif e[0] == "progress":
                    a, b = int(e[1]), max(1, int(e[2]))
                    self.pct.set(min(100, a * 100 / b))
                    self.counter.set(f"{a} / {b}")
                elif e[0] == "done":
                    self.start_btn.config(state="normal")
                    self.stop_btn.config(state="disabled")
                    self.status.set("Готово")
                    messagebox.showinfo(APP_NAME, f"Готово.\n\n{e[1]}")
                elif e[0] == "error":
                    self.start_btn.config(state="normal")
                    self.stop_btn.config(state="disabled")
                    self.status.set(e[1])
                    messagebox.showerror(APP_NAME, e[1] + "\n\nПришлите Logs\\MEMS_Parser_LATEST.log и, если появились, файлы из Debug.")
        except queue.Empty:
            pass
        self.after(150, self._poll)


if __name__ == "__main__":
    if "--self-test" in sys.argv:
        raise SystemExit(self_test())
    LOGGER = Log()
    App().mainloop()
