#!/usr/bin/env python3
"""Ctrl + rueda = zoom del CODIGO en el editor plegable (AvalonEdit).

Fallo: el Output hacia zoom y el codigo no (RichTextBox_PreviewMouseWheel quedo en el
editor oculto). La op «zoom» llama a lo mismo que la rueda (ZoomEditor) y devuelve el
tamaño de letra; ademas se captura el PNG con la letra agrandada.

Uso: python tests/wpf/test_zoom_editor.py [--exe ruta] [--png salida.png]
"""
import argparse, os, sys, time
from test_autorun_avalon import Ctl, EXE


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=EXE)
    ap.add_argument("--png", default=None)
    a = ap.parse_args()
    ctl = Ctl(a.exe)
    fallos = 0

    def check(nombre, ok, dato):
        nonlocal fallos
        fallos += not ok
        print(f"  [{'OK ' if ok else 'MAL'}] {nombre}: {dato}")

    try:
        time.sleep(4)
        ctl({"op": "setcode", "text": "a = 2\nb = a*3"})
        f0 = ctl({"op": "zoom", "steps": 0})["fontSize"]
        f1 = ctl({"op": "zoom", "steps": 3})["fontSize"]
        check("Ctrl+rueda arriba x3 agranda +6", f1 == f0 + 6, f"{f0} -> {f1}")
        if a.png:
            ctl({"op": "capture", "path": os.path.abspath(a.png)})
            print("png:", os.path.abspath(a.png))
        f2 = ctl({"op": "zoom", "steps": -3})["fontSize"]
        check("Ctrl+rueda abajo x3 vuelve", f2 == f0, f"{f1} -> {f2}")
        ftop = ctl({"op": "zoom", "steps": 50})["fontSize"]
        check("tope maximo 40", ftop == 40, ftop)
        fmin = ctl({"op": "zoom", "steps": -50})["fontSize"]
        check("tope minimo 6", fmin == 6, fmin)
        ctl({"op": "zoom", "steps": int((f0 - 6) / 2)})
    finally:
        ctl.cerrar()
    print("TODO OK" if not fallos else f"FALLAN {fallos}")
    sys.exit(1 if fallos else 0)


if __name__ == "__main__":
    main()
