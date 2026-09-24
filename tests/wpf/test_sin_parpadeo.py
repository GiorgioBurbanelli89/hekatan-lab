#!/usr/bin/env python3
"""El AutoRun al escribir NO hace parpadear el Output.

Fallo: cada calculo navegaba el WebView2 a la pagina de streaming (pagina vacia +
banner = 1.er destello) y luego pintaba el resultado (2.º). Ademas, en cada tecla se
metia el GIF «escribiendo» en el Output. Ahora el AutoRun deja el resultado anterior a
la vista y hace UN swap atomico (__matlabSwap): 0 navegaciones.

Se mide con la op «navs» (NavigationStarting del WebView2) y se lee el Output.

Uso: python tests/wpf/test_sin_parpadeo.py [--exe ruta]
"""
import argparse, sys, time
from test_autorun_avalon import Ctl, EXE


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=EXE)
    a = ap.parse_args()
    ctl = Ctl(a.exe)
    fallos = 0

    def check(nombre, ok, dato):
        nonlocal fallos
        fallos += not ok
        print(f"  [{'OK ' if ok else 'MAL'}] {nombre}: {dato}")

    try:
        time.sleep(4)
        ctl({"op": "setcode", "text": "a = 2"})        # primer calculo: crea la pagina
        time.sleep(3)
        ctl({"op": "navs", "reset": True})

        ctl({"op": "setcode", "text": "a = 2\nb = a*3"})
        time.sleep(3)
        n = ctl({"op": "navs"})["navs"]
        out = ctl({"op": "getoutput"})["output"]
        check("AutoRun sin navegar (sin pagina en blanco)", n == 0, f"{n} navegaciones")
        check("...y el resultado nuevo esta", "= 6" in out, repr(out.strip()[:80]))

        ctl({"op": "setcode", "text": "a = 2\nb = a*3\nc = zz + 1"})
        time.sleep(3)
        out = ctl({"op": "getoutput"})["output"]
        check("con error: se ve el error", "Error" in out, repr(out.strip()[-80:]))
        check("...una sola vez (no duplicado)", out.count("Error") == 1, f"{out.count('Error')} veces")
        check("...y lo anterior sigue", "= 6" in out and out.count("a = 2") == 1, f"a = 2 x{out.count('a = 2')}")

        ctl({"op": "setcode", "text": "a = 3\nb = a*3"})
        time.sleep(3)
        out = ctl({"op": "getoutput"})["output"]
        check("corregido: el error desaparece", "Error" not in out and "= 9" in out, repr(out.strip()[:80]))
        n = ctl({"op": "navs"})["navs"]
        check("tres AutoRun seguidos: 0 navegaciones", n == 0, f"{n}")
    finally:
        ctl.cerrar()
    print("TODO OK" if not fallos else f"FALLAN {fallos}")
    sys.exit(1 if fallos else 0)


if __name__ == "__main__":
    main()
