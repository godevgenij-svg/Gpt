from __future__ import annotations

import os
import queue
import sys
import threading
import traceback
from pathlib import Path

import tkinter as tk
from tkinter import filedialog, messagebox, ttk

from parser_core import APP_VERSION, Log, run, self_test, writable_dir

APP_NAME = "MEMS Catalog Parser"
LOGGER: Log | None = None


class App(tk.Tk):
    def __init__(self):
        super().__init__()
        self.title(f"{APP_NAME} {APP_VERSION}")
        self.geometry("720x390")
        self.minsize(650, 360)
        self.stop_event = threading.Event()
        self.worker: threading.Thread | None = None
        self.events: queue.Queue = queue.Queue()
        self.output = tk.StringVar(value=str(writable_dir("Output")))
        self.delay = tk.IntVar(value=300)
        self.status = tk.StringVar(value="Нажмите «Начать». Первый раз войдите в MEMS в открывшемся Chrome.")
        self.pct = tk.DoubleVar(value=0)
        self.counter = tk.StringVar(value="0 / ?")
        self._ui()
        self.after(150, self._poll)

    def _ui(self):
        f = ttk.Frame(self)
        f.pack(fill="both", expand=True, padx=14, pady=14)
        ttk.Label(f, text="MEMS B2B → XLSX", font=("Segoe UI", 16, "bold")).grid(row=0, column=0, columnspan=3, sticky="w", pady=(0, 12))
        ttk.Label(f, text="Простой режим: отдельный Chrome-профиль, ручной вход один раз. Данные сохраняются после каждой страницы. Ошибка в конце не удалит уже собранное.", wraplength=670).grid(row=1, column=0, columnspan=3, sticky="w", pady=(0, 14))
        ttk.Label(f, text="Папка результата:").grid(row=2, column=0, sticky="w", pady=6)
        ttk.Entry(f, textvariable=self.output).grid(row=2, column=1, sticky="ew", padx=8, pady=6)
        ttk.Button(f, text="Выбрать…", command=self._choose).grid(row=2, column=2, pady=6)
        ttk.Label(f, text="Пауза между страницами, мс:").grid(row=3, column=0, sticky="w", pady=6)
        ttk.Spinbox(f, from_=150, to=3000, increment=50, textvariable=self.delay, width=10).grid(row=3, column=1, sticky="w", padx=8, pady=6)
        bar = ttk.Frame(f)
        bar.grid(row=4, column=0, columnspan=3, sticky="ew", pady=12)
        self.start_btn = ttk.Button(bar, text="Начать", command=self._start)
        self.start_btn.pack(side="left", padx=(0, 8))
        self.stop_btn = ttk.Button(bar, text="Стоп", command=self._stop, state="disabled")
        self.stop_btn.pack(side="left", padx=(0, 8))
        ttk.Button(bar, text="Открыть результаты", command=self._open).pack(side="left")
        ttk.Progressbar(f, variable=self.pct, maximum=100).grid(row=5, column=0, columnspan=3, sticky="ew", pady=(8, 4))
        ttk.Label(f, textvariable=self.counter).grid(row=6, column=0, sticky="w")
        ttk.Label(f, textvariable=self.status, wraplength=670).grid(row=7, column=0, columnspan=3, sticky="nw", pady=10)
        ttk.Separator(f).grid(row=8, column=0, columnspan=3, sticky="ew", pady=8)
        ttk.Label(f, text="Всегда остаются: MEMS_В_НАЛИЧИИ_LIVE.csv, MEMS_ВСЕ_ПРОСМОТРЕННЫЕ_LIVE.csv, MEMS_CHECKPOINT.json.\nExcel: MEMS_В_НАЛИЧИИ_LIVE.xlsx и финальный MEMS_в_наличии_*.xlsx", foreground="#555").grid(row=9, column=0, columnspan=3, sticky="w")
        f.columnconfigure(1, weight=1)

    def _choose(self):
        d = filedialog.askdirectory(initialdir=self.output.get())
        if d:
            self.output.set(d)

    def _open(self):
        p = Path(self.output.get())
        p.mkdir(parents=True, exist_ok=True)
        os.startfile(p)  # type: ignore[attr-defined]

    def _start(self):
        if self.worker and self.worker.is_alive():
            return
        try:
            Path(self.output.get()).mkdir(parents=True, exist_ok=True)
        except Exception as e:
            messagebox.showerror(APP_NAME, f"Не удаётся создать папку: {e}")
            return
        self.stop_event.clear()
        self.start_btn.config(state="disabled")
        self.stop_btn.config(state="normal")
        self.pct.set(0)
        self.counter.set("0 / ?")
        self.worker = threading.Thread(target=self._worker, daemon=True)
        self.worker.start()

    def _stop(self):
        self.stop_event.set()
        self.status.set("Останавливаю. Уже собранные данные остаются на диске…")

    def _worker(self):
        try:
            result, warnings = run(
                Path(self.output.get()),
                max(150, int(self.delay.get())),
                self.stop_event,
                lambda s: self.events.put(("status", s)),
                lambda a, b: self.events.put(("progress", a, b)),
                LOGGER or Log(),
            )
            self.events.put(("done", str(result), warnings))
        except Exception as e:
            if LOGGER:
                LOGGER.write(f"UNEXPECTED ERROR {type(e).__name__}: {e}\n{traceback.format_exc()}")
            self.events.put(("error", f"{type(e).__name__}: {e}"))

    def _poll(self):
        try:
            while True:
                e = self.events.get_nowait()
                if e[0] == "status":
                    self.status.set(e[1])
                elif e[0] == "progress":
                    a, b = int(e[1]), int(e[2])
                    if b > 0:
                        self.pct.set(min(100, a * 100 / b))
                        self.counter.set(f"{a} / {b}")
                    else:
                        self.counter.set(f"{a} / ?")
                elif e[0] == "done":
                    self.start_btn.config(state="normal")
                    self.stop_btn.config(state="disabled")
                    warnings = e[2]
                    if warnings:
                        self.status.set("Завершено с предупреждениями. Данные сохранены.")
                        messagebox.showwarning(APP_NAME, "Парсинг завершён. Данные сохранены.\n\nПредупреждений: %d\nСмотрите MEMS_WARNINGS.txt\n\n%s" % (len(warnings), e[1]))
                    else:
                        self.status.set("Готово. Данные сохранены.")
                        messagebox.showinfo(APP_NAME, f"Готово.\n\n{e[1]}")
                elif e[0] == "error":
                    self.start_btn.config(state="normal")
                    self.stop_btn.config(state="disabled")
                    self.status.set("Непредвиденная ошибка. Проверьте LIVE.csv — всё сохранённое до ошибки осталось.")
                    messagebox.showerror(APP_NAME, e[1] + "\n\nПроверьте LIVE.csv и Logs\\MEMS_Parser_LATEST.log")
        except queue.Empty:
            pass
        self.after(150, self._poll)


if __name__ == "__main__":
    if "--self-test" in sys.argv:
        raise SystemExit(self_test())
    LOGGER = Log()
    App().mainloop()
