#!/usr/bin/env python3
"""Cuanto tarda la VENTANA en mostrar un resultado (no el motor: la ventana).

Por que existe: el CLI dice que un escalar se calcula en milisegundos, pero en la
ventana el primer `M = 2*L` se sentia "una eternidad". Esto lo mide por el mismo canal
--ctl que usa run_tests.py, caso por caso, con el reloj de la terminal.

    python bench.py            # todos los casos
    python bench.py -v         # ademas el diag por statement (%TEMP%\\calcpad_lab_diag.log)

Lo que se aprendio midiendo (agosto 2026):
  * el motor NO es el problema: warm, un escalar = 1 ms dentro de RunLine;
  * lo caro es el JIT de la ruta que IMPRIME el resultado sustituyendo valores
    ("M = 2*L = 2*3.5 = 7"). El warmup de arranque no la tocaba (solo statements
    con punto y coma) -> esos ~8 s los pagaba el usuario en su primer calculo;
  * el resto (~1 s fijo por comando) es la espera de settle del canal --ctl, no la app.
"""
import argparse
import io
import json
import os
import sys
import time

sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from run_tests import App, DEFAULT_EXE          # noqa: E402

DIAG = os.path.join(os.environ.get("TEMP", "."), "calcpad_lab_diag.log")
WARMUP = os.path.join(os.environ.get("TEMP", "."), "calcpad_lab_warmup.log")

CASOS = [
    ("escalar 1 (frio)", "L = 3.5;\nM = 2*L"),
    ("escalar 2",        "L = 4;\nM = 3*L"),
    ("escalar 3",        "a = 7;\nb = a + 1"),
    ("vector 1e3",       "v = 1:1000;\ns = sum(v)"),
    ("matriz 200x200",   "A = rand(200);\nd = trace(A*A')"),
    ("loop 1e4",         "s = 0;\nfor k = 1:10000\n    s = s + k;\nend\ns"),
    ("funcion",          "f = @(t) exp(-t.^2);\nq = quadgk(f, 0, 1)"),
]


def reporte(app):
    return json.loads(app.cmd(op="js", code="JSON.stringify(document.body.innerText)")["result"])


def motor_ms():
    """Milisegundos DENTRO del motor, leidos del diag por statement."""
    try:
        filas = [l for l in open(DIAG, encoding="utf-8").read().split("\n") if " START L" in l or " DONE  L" in l]
        if len(filas) < 2:
            return None
        def t(l):
            h, m, s = l.split(" ")[0].split(":")
            return (int(h) * 60 + int(m)) * 60 + float(s)
        return int((t(filas[-1]) - t(filas[0])) * 1000)
    except Exception:
        return None


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("-v", "--verbose", action="store_true")
    a = ap.parse_args()
    app = App(a.exe)
    try:
        print("%-18s %8s %8s   %s" % ("caso", "ventana", "motor", "reporte"))
        for nombre, code in CASOS:
            app.code(code)
            t = time.time()
            app.cmd(op="run")
            dt = time.time() - t
            ms = motor_ms()
            print("%-18s %7.1fs %8s   %s" % (
                nombre, dt, ("%d ms" % ms) if ms is not None else "?",
                reporte(app).replace("\n", " | ")[:60]))
    finally:
        app.close()
    if os.path.exists(WARMUP):
        print("\nwarmup de arranque (hilo de fondo):")
        print("  " + open(WARMUP, encoding="utf-8").read().strip().replace("\n", "\n  "))


if __name__ == "__main__":
    sys.exit(main())
