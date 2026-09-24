#!/usr/bin/env python3
"""El divisor Codigo | Output se puede ARRASTRAR con el raton.

Fallo: la columna del GridSplitter era Auto y el divisor no tenia Width -> medía ~2 px
y no habia de donde agarrarlo. Aqui se pide su posicion por --ctl («layout»), se
arrastra con el raton DE VERDAD (SetCursorPos + mouse_event) y se vuelve a medir.

Antes de tocar el raton se comprueba que la ventana al frente es ESTA app: si no, no se
arrastra nada (un clic a ciegas cae en otra ventana del usuario).

Uso: python tests/wpf/test_splitter.py [--exe ruta] [--png salida.png]
"""
import argparse, ctypes, os, sys, time
from ctypes import wintypes
from test_autorun_avalon import Ctl, EXE

u32 = ctypes.windll.user32
u32.SetProcessDPIAware()


def al_frente_es(pid):
    h = u32.GetForegroundWindow()
    p = wintypes.DWORD()
    u32.GetWindowThreadProcessId(h, ctypes.byref(p))
    return p.value == pid


def traer(pid):
    # ALT suelto: Windows deja a un proceso de fondo robar el foco tras una tecla
    u32.keybd_event(0x12, 0, 0, 0); u32.keybd_event(0x12, 0, 2, 0)
    hs = []
    def cb(h, _):
        p = wintypes.DWORD(); u32.GetWindowThreadProcessId(h, ctypes.byref(p))
        if p.value == pid and u32.IsWindowVisible(h): hs.append(h)
        return True
    u32.EnumWindows(ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)(cb), 0)
    for h in hs:
        u32.ShowWindow(h, 9); u32.SetForegroundWindow(h)
    time.sleep(0.8)


def arrastrar(x0, y, x1):
    u32.SetCursorPos(x0, y); time.sleep(0.2)
    u32.mouse_event(0x0002, 0, 0, 0, 0); time.sleep(0.15)          # boton abajo
    for k in range(1, 21):
        u32.SetCursorPos(int(x0 + (x1 - x0) * k / 20), y); time.sleep(0.03)
    time.sleep(0.15)
    u32.mouse_event(0x0004, 0, 0, 0, 0); time.sleep(0.5)            # boton arriba


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=EXE)
    ap.add_argument("--png", default=None)
    a = ap.parse_args()
    ctl = Ctl(a.exe)
    fallos = 0
    try:
        time.sleep(4)
        l0 = ctl({"op": "layout"})
        print("antes:", {k: l0[k] for k in ("editor", "splitter", "web")})
        ok = l0["splitter"] >= 4
        fallos += not ok
        print(f"  [{'OK ' if ok else 'MAL'}] el divisor tiene ancho para agarrarlo: {l0['splitter']:.1f} px")

        traer(ctl.p.pid)
        if not al_frente_es(ctl.p.pid):
            print("  [MAL] la ventana de prueba NO quedo al frente: no se toca el raton")
            fallos += 1
        else:
            cx = (l0["sx0"] + l0["sx1"]) // 2
            cy = (l0["sy0"] + l0["sy1"]) // 2
            arrastrar(cx, cy, cx - 300)
            l1 = ctl({"op": "layout"})
            print("despues:", {k: l1[k] for k in ("editor", "splitter", "web")})
            ok = l1["editor"] < l0["editor"] - 100 and l1["web"] > l0["web"] + 100
            fallos += not ok
            print(f"  [{'OK ' if ok else 'MAL'}] arrastrado 300 px a la izquierda: "
                  f"codigo {l0['editor']:.0f} -> {l1['editor']:.0f}, output {l0['web']:.0f} -> {l1['web']:.0f}")
            if a.png:
                ctl({"op": "capture", "path": os.path.abspath(a.png)})
                print("png:", os.path.abspath(a.png))
    finally:
        ctl.cerrar()
    print("TODO OK" if not fallos else f"FALLAN {fallos}")
    sys.exit(1 if fallos else 0)


if __name__ == "__main__":
    main()
