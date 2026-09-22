#!/usr/bin/env python3
"""Barrido de TODOS los ejemplos por el canal --ctl (UNA ventana viva).

Por que asi y no lanzando el CLI por ejemplo: eso abria un proceso y escribia un
.html por archivo (cientos de reportes sueltos). Aqui la ventana se arranca UNA vez
por carpeta, se le mete cada ejemplo con `settext` y se lee el panel de salida con
`getoutput`. Ningun archivo nuevo en el repo.

Se arranca una ventana POR CARPETA (pasandole un .m de esa carpeta) para que la ruta
del documento sea la correcta: los ejemplos que llaman funciones o leen csv de su
propia carpeta necesitan ese DocPath.

Uso:
    python sweep_examples_ctl.py                 # todo Examples-Lab
    python sweep_examples_ctl.py "04 Tests"      # solo carpetas que contengan eso
    python sweep_examples_ctl.py -v              # ademas, las primeras lineas de salida
"""
import argparse
import json
import os
import re
import shutil
import subprocess
import sys
import tempfile
import time

HERE = os.path.dirname(os.path.abspath(__file__))
ROOT = os.path.abspath(os.path.join(HERE, "..", ".."))
EXE = os.path.join(ROOT, "Symbolic.Wpf", "bin", "Release", "net10.0-windows", "HekatanLab.exe")
EXAMPLES = os.path.join(ROOT, "Examples-Lab")

# Como se ve un fallo en el panel de salida (el motor los pinta como <p class="err">).
ERR_RE = re.compile(r"(Error on line \d+.*|Error en la l[ií]nea \d+.*)", re.I)


def ascii_(s):
    """La consola de Windows es cp1252: los primos/griegas del reporte la rompen."""
    return s.encode("ascii", "replace").decode("ascii")


class Ventana:
    """La ventana viva, hablada por carpeta de comandos."""

    def __init__(self, doc, espera=240):
        self.dir = tempfile.mkdtemp(prefix="hklab-sweep-")
        self.n = 0
        self.espera = espera
        self.proc = subprocess.Popen([EXE, "--theme", "dark", "--ctl", self.dir, doc])
        listo = os.path.join(self.dir, "ready.txt")
        for _ in range(600):                       # hasta 60 s: el 1er arranque carga MKL + JIT
            if os.path.exists(listo):
                break
            time.sleep(0.1)
        else:
            raise RuntimeError("la ventana no llego a estar lista")
        time.sleep(1.5)

    def cmd(self, **kw):
        self.n += 1
        cid = "%04d" % self.n
        tmp = os.path.join(self.dir, "tmp-" + cid)
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(kw, f)
        os.rename(tmp, os.path.join(self.dir, "cmd-%s.json" % cid))   # atomico
        resp = os.path.join(self.dir, "resp-%s.json" % cid)
        for _ in range(self.espera * 10):
            if os.path.exists(resp):
                with open(resp, encoding="utf-8") as f:
                    return json.load(f)
            if self.proc.poll() is not None:
                raise RuntimeError("la ventana se murio")
            time.sleep(0.1)
        raise RuntimeError("sin respuesta (%s s) a %s" % (self.espera, kw.get("op")))

    def salida(self, texto):
        """Mete el codigo, espera el calculo y devuelve lo que se VE en el panel."""
        self.cmd(op="settext", text=texto)
        r = self.cmd(op="getoutput")
        return r.get("output", "") or ""

    def cerrar(self):
        try:
            self.cmd(op="quit")
        except Exception:
            pass
        try:
            self.proc.wait(10)
        except Exception:
            self.proc.kill()
        shutil.rmtree(self.dir, ignore_errors=True)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("filtro", nargs="?", default="")
    ap.add_argument("-v", "--verbose", action="store_true")
    args = ap.parse_args()

    carpetas = {}
    for dirpath, _, names in os.walk(EXAMPLES):
        ms = sorted(n for n in names if n.lower().endswith(".m"))
        if ms and (not args.filtro or args.filtro.lower() in dirpath.lower()):
            carpetas[dirpath] = ms
    total = sum(len(v) for v in carpetas.values())
    print("Ventana: %s" % EXE)
    print("%d ejemplos en %d carpetas\n" % (total, len(carpetas)))

    ok, malos = 0, []
    for dirpath in sorted(carpetas):
        ms = carpetas[dirpath]
        rel = os.path.relpath(dirpath, EXAMPLES)
        print("== %s (%d)" % (rel, len(ms)), flush=True)
        try:
            app = Ventana(os.path.join(dirpath, ms[0]))
        except Exception as e:
            malos.append((rel, "*", "no arranco: %s" % e))
            print("   [NO ARRANCA] %s" % e, flush=True)
            continue
        try:
            for m in ms:
                ruta = os.path.join(dirpath, m)
                try:
                    with open(ruta, encoding="utf-8", errors="replace") as f:
                        codigo = f.read()
                except Exception as e:
                    malos.append((rel, m, "ilegible: %s" % e))
                    continue
                if not codigo.strip():
                    malos.append((rel, m, "archivo VACIO"))
                    print("   [VACIO] %s" % m, flush=True)
                    continue
                try:
                    out = app.salida(codigo)
                except Exception as e:
                    malos.append((rel, m, str(e)))
                    print("   [CUELGA] %s :: %s" % (m, ascii_(str(e))), flush=True)
                    try:
                        app.cerrar()
                        app = Ventana(os.path.join(dirpath, ms[0]))
                    except Exception as e2:
                        print("   [NO REARRANCA] %s" % e2, flush=True)
                        break
                    continue
                errs = ERR_RE.findall(out)
                if errs:
                    malos.append((rel, m, errs[0][:160]))
                    print("   [ERROR] %s :: %s" % (m, ascii_(errs[0][:160])), flush=True)
                else:
                    ok += 1
                    if args.verbose:
                        print("   ok  %-45s %s" % (m, ascii_(out.strip().replace("\n", " / ")[:90])))
        finally:
            app.cerrar()

    print("\n%d ok, %d con problema (de %d)" % (ok, len(malos), total))
    for rel, m, msg in malos:
        print("  %s\\%s :: %s" % (rel, m, ascii_(msg)))
    return 0 if not malos else 1


if __name__ == "__main__":
    sys.exit(main())
