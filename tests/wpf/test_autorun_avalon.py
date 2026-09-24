#!/usr/bin/env python3
"""AutoRun AL ESCRIBIR en el editor plegable (AvalonEdit) de Hekatan Lab.

Fallo (instalador 1.3.21): se escribia y el Output se quedaba quieto. El AutoRun clasico
lo disparaba el RichTextBox oculto al cambiar de linea o perder el foco, y con AvalonEdit
delante eso no pasa nunca. Aqui se escribe en AvalonEdit (op setcode = AvalonEditor.Text),
NO se pulsa Calcular, se espera y se lee el Output: tiene que haber cambiado solo.

Uso: python tests/wpf/test_autorun_avalon.py [--exe ruta] [--png salida.png]
"""
import argparse, json, os, shutil, subprocess, sys, tempfile, time

HERE = os.path.dirname(os.path.abspath(__file__))
EXE = os.path.abspath(os.path.join(HERE, "..", "..", "Symbolic.Wpf", "bin", "Release",
                                   "net10.0-windows", "HekatanLab.exe"))


class Ctl:
    def __init__(self, exe):
        self.dir = tempfile.mkdtemp(prefix="hk_ctl_autorun_")
        self.n = 0
        self.p = subprocess.Popen([exe, "--ctl", self.dir])

    def __call__(self, cmd, timeout=60):
        self.n += 1
        resp = os.path.join(self.dir, f"resp-{self.n:04d}.json")
        tmp = os.path.join(self.dir, f"cmd-{self.n:04d}.tmp")
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(cmd, f)
        os.replace(tmp, os.path.join(self.dir, f"cmd-{self.n:04d}.json"))
        t0 = time.time()
        while time.time() - t0 < timeout:
            if os.path.exists(resp):
                time.sleep(0.1)
                with open(resp, encoding="utf-8") as f:
                    return json.load(f)
            time.sleep(0.1)
        raise TimeoutError(f"sin respuesta a {cmd}")

    def cerrar(self):
        try: self({"op": "quit"}, timeout=10)
        except Exception: pass
        try: self.p.wait(10)
        except Exception: self.p.kill()
        shutil.rmtree(self.dir, ignore_errors=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=EXE)
    ap.add_argument("--png", default=None)
    a = ap.parse_args()

    ctl = Ctl(a.exe)
    fallos = 0
    try:
        time.sleep(4)
        est = ctl({"op": "state"})
        print("state:", {k: est.get(k) for k in ("ok", "motor", "autorun", "plegado") if k in est} or est)

        def caso(nombre, codigo, debe):
            nonlocal fallos
            ctl({"op": "setcode", "text": codigo})
            time.sleep(3.0)                       # 700 ms de espera + calculo; SIN pulsar Calcular
            out = ctl({"op": "getoutput"}).get("output", "")
            ok = all(d in out.replace(" ", " ") for d in debe)
            fallos += not ok
            print(f"  [{'OK ' if ok else 'MAL'}] {nombre}: {out.strip()[:120]!r}")

        caso("primera linea", "a = 2", ["a = 2"])
        caso("segunda linea sin pulsar nada", "a = 2\nb = a*3", ["b = a·3 = 2·3 = 6"])
        caso("editar la primera", "a = 5\nb = a*3", ["a = 5", "= 5·3 = 15"])
        if a.png:
            ctl({"op": "capture", "path": os.path.abspath(a.png)})
            print("png:", os.path.abspath(a.png))
    finally:
        ctl.cerrar()
    print("TODO OK" if not fallos else f"FALLAN {fallos}")
    sys.exit(1 if fallos else 0)


if __name__ == "__main__":
    main()
