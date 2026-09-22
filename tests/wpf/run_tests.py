#!/usr/bin/env python3
"""Suite de regresion de la VENTANA (XAML + editor) de Hekatan Lab.

Por que existe: el motor se prueba con el CLI (entra un .m, sale un numero), pero la
ventana no. Y ahi vivian fallos que ningun test de motor podia ver:

  * los botones de marcado escribian en el RichTextBox OCULTO -> el texto salia donde
    estaba SU cursor, no donde el usuario tenia el suyo;
  * los botones div/listas/tabla insertaban comentarios de CALCPAD ('<div>), que en
    MATLAB son "Unterminated char-array";
  * pulsar una linea del reporte movia el cursor del control oculto: no pasaba nada.

Mirar el PNG dice COMO queda, no si esta BIEN. Esto compara TEXTO exacto.

Como se conecta la terminal con la ventana (no hay que adivinar coordenadas ni mover
el raton): la app arranca con `--ctl <carpeta>` y se queda escuchando esa carpeta.
Aqui se deja `cmd-0001.json` y la app contesta `resp-0001.json`. Un solo arranque
(~2 s) sirve para todos los casos. Ver MainWindow.Pruebas.cs.

Los botones se pulsan POR SU x:Name de la XAML, asi que el Tag que se prueba es el que
esta escrito en MainWindow.xaml: si alguien lo cambia mal, el test lo caza.

Uso:
    python run_tests.py                    # usa el build Release del repo
    python run_tests.py --exe <ruta.exe>   # o uno concreto (p.ej. el instalado)
    python run_tests.py -v                 # ademas, el texto que devolvio cada caso

Sale con codigo != 0 si algo falla -> sirve de pre-commit / pre-instalador.
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

# "nombre(" = una llamada a funcion dentro del Tag de un boton. Se descarta lo que
# viene detras de un punto (a.foo(), que es un metodo, no una funcion del motor).
IDENT = re.compile(r"(?<![\w.])([A-Za-z_]\w*)\s*\(")

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_EXE = os.path.abspath(os.path.join(
    HERE, "..", "..", "Symbolic.Wpf", "bin", "Release", "net10.0-windows", "HekatanLab.exe"))

# El .m de partida de casi todos los casos. Linea 1 titulo, 2 texto, 3 codigo con
# comentario al final, 4 codigo pelado.
BASE = "\n".join([
    "%\" Titulo de prueba",
    "%' Esta linea es de documento.",
    "L = 3.50;   % luz de la viga en m",
    "M = 5",
])

PLEGABLE = "\n".join([
    "s = 0;",
    "for k = 1:10",
    "    a = k^2;",
    "    b = a/2;",
    "    s = s + b;",
    "end",
    "s",
])


class App:
    """La ventana viva, hablada por carpeta de comandos."""

    def __init__(self, exe, verbose=False):
        self.dir = tempfile.mkdtemp(prefix="hklab-ctl-")
        self.n = 0
        self.verbose = verbose
        self.proc = subprocess.Popen([exe, "--theme", "dark", "--ctl", self.dir])
        ready = os.path.join(self.dir, "ready.txt")
        for _ in range(300):                      # hasta 30 s: el primer arranque carga MKL
            if os.path.exists(ready):
                break
            time.sleep(0.1)
        else:
            raise RuntimeError("la ventana no llego a estar lista (sin ready.txt)")
        time.sleep(1.2)                           # que termine de montarse el editor

    def cmd(self, **kw):
        self.n += 1
        cid = "%04d" % self.n
        tmp = os.path.join(self.dir, "tmp-" + cid)
        with open(tmp, "w", encoding="utf-8") as f:
            json.dump(kw, f)
        os.rename(tmp, os.path.join(self.dir, "cmd-%s.json" % cid))   # atomico: no se lee a medias
        resp = os.path.join(self.dir, "resp-%s.json" % cid)
        for _ in range(400):                      # 40 s (un run con plots puede tardar)
            if os.path.exists(resp):
                with open(resp, encoding="utf-8") as f:
                    r = json.load(f)
                if not r.get("ok", False):
                    raise RuntimeError("%s -> %s" % (kw, r.get("error")))
                return r
            time.sleep(0.1)
        raise RuntimeError("sin respuesta a %s" % (kw,))

    def code(self, text):
        self.cmd(op="setcode", text=text)

    def text(self):
        return self.cmd(op="gettext")["text"].replace("\r\n", "\n")

    def reporte(self):
        """El texto que se VE en el panel de salida (WebView2)."""
        r = self.cmd(op="js", code="JSON.stringify(document.body.innerText)")
        return json.loads(r["result"]) if r.get("result") not in (None, "null") else ""

    def close(self):
        try:
            self.cmd(op="quit")
        except Exception:
            pass
        try:
            self.proc.wait(10)
        except Exception:
            self.proc.kill()
        shutil.rmtree(self.dir, ignore_errors=True)


FALLOS = []
OK = 0


def check(nombre, obtenido, esperado):
    global OK
    if obtenido == esperado:
        OK += 1
        print("  ok   %s" % nombre)
    else:
        FALLOS.append(nombre)
        print("  FALLA %s" % nombre)
        print("        esperado: %r" % (esperado,))
        print("        obtenido: %r" % (obtenido,))


def linea(app, n):
    return app.text().split("\n")[n - 1]


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=DEFAULT_EXE)
    ap.add_argument("-v", "--verbose", action="store_true")
    a = ap.parse_args()
    if not os.path.exists(a.exe):
        print("No esta el exe: %s\n(compila Symbolic.Wpf en Release)" % a.exe)
        return 2

    print("Ventana: %s" % a.exe)
    app = App(a.exe, a.verbose)
    try:
        st = app.cmd(op="state")
        if st.get("editor") != "avalon":
            print("El editor plegable no esta activo: la suite prueba ESE editor.")
            return 2

        # --- 1. marcado en una linea de documento -------------------------------
        app.code(BASE)
        app.cmd(op="setcaret", line=2, col=24)          # dentro de "documento"
        app.cmd(op="button", name="BoldButton")
        check("negrita envuelve la palabra del cursor", linea(app, 2),
              "%' Esta linea es de <strong>documento</strong>.")

        # --- 2. el boton es un interruptor --------------------------------------
        app.cmd(op="button", name="BoldButton")
        check("negrita otra vez la quita", linea(app, 2),
              "%' Esta linea es de documento.")

        # --- 3. con seleccion, marca lo seleccionado ----------------------------
        app.code(BASE)
        app.cmd(op="select", line=2, col=4, len=4)       # "Esta"
        app.cmd(op="button", name="ItalicButton")
        check("cursiva sobre la seleccion", linea(app, 2),
              "%' <em>Esta</em> linea es de documento.")

        # --- 4. linea de CODIGO: no se toca ------------------------------------
        app.code(BASE)
        app.cmd(op="setcaret", line=4, col=2)            # dentro de "M = 5"
        app.cmd(op="button", name="BoldButton")
        t = app.text().split("\n")
        check("el codigo no se toca", t[3], "M = 5")
        check("la marca baja a una linea %' nueva", t[4], "%'<strong></strong>")

        # --- 5. dentro del comentario de una linea de codigo --------------------
        app.code(BASE)
        app.cmd(op="setcaret", line=3, col=26)           # dentro de "viga"
        app.cmd(op="button", name="SuperscriptButton")
        check("marca dentro del comentario, sin tocar el codigo", linea(app, 3),
              "L = 3.50;   % luz de la <sup>viga</sup> en m")

        # --- 6. linea entera: <p> ----------------------------------------------
        app.code(BASE)
        app.cmd(op="setcaret", line=2)
        app.cmd(op="button", name="PButton")
        check("<p> envuelve la linea entera", linea(app, 2),
              "%' <p>Esta linea es de documento.</p>")
        app.cmd(op="button", name="PButton")
        check("<p> otra vez lo quita", linea(app, 2),
              "%' Esta linea es de documento.")

        # --- 7. encabezados = MATLAB puro (no <h3> de Calcpad) ------------------
        app.code(BASE + "\n")
        app.cmd(op="setcaret", line=5)                   # linea vacia del final
        app.cmd(op="button", name="H3Button")
        check("H1 mete disp() de MATLAB puro", linea(app, 5),
              "disp('==== TÍTULO ====')")

        # --- 8. bloques de varias lineas ----------------------------------------
        app.code(BASE)
        app.cmd(op="setcaret", line=2)
        app.cmd(op="button", name="DivButton")
        t = app.text().split("\n")
        check("div: dos lineas debajo, sin tocar la actual", t[1:4],
              ["%' Esta linea es de documento.", "%'<div>", "%'</div>"])

        app.code(BASE)
        app.cmd(op="setcaret", line=2)
        app.cmd(op="button", name="TableButton")
        tabla = [l for l in app.text().split("\n") if "<t" in l or "table" in l]
        check("tabla: TODAS sus lineas son comentario MATLAB %'",
              all(l.startswith("%'") for l in tabla) and len(tabla) >= 6, True)

        # --- 9. NINGUN boton puede escribir comentarios de Calcpad -------------
        # Es el fallo que ya paso dos veces ('<h3>, '<div>): en MATLAB, una comilla
        # suelta abre una cadena que NO cierra -> "Unterminated char-array". Esa es
        # justo la firma que se busca: la linea empieza por ' y no termina en '.
        # (Se deja fuera el Tag de un solo caracter: es la tecla ' del teclado de
        # simbolos, que escribe una comilla y nada mas.)
        malos = []
        for b in app.cmd(op="buttons")["buttons"]:
            for l in b["tag"].split("§"):
                s = l.strip()
                if s.startswith("'") and len(s) > 1 and not s.endswith("'"):
                    malos.append((b["name"] or "(sin nombre)", l))
        check("ningun Tag de la XAML escribe comentarios de Calcpad", malos, [])

        # --- 10. el salto desde el reporte -------------------------------------
        app.code(PLEGABLE)
        app.cmd(op="fold", all=True)
        st = app.cmd(op="state")
        check("el for se plego", st["folded"] >= 1, True)
        app.cmd(op="gotoline", line=4)                   # "b = a/2;" (dentro del pliegue)
        st = app.cmd(op="state")
        check("el clic en el reporte cae en la linea 4", st["line"], 4)
        check("...y abre el pliegue que la tapaba", st["folded"], 0)
        check("...y el foco se queda en el editor que se ve", st["focus"], True)

        # --- 11. lo que escriben los botones, ¿lo conoce el MOTOR? --------------
        # El caso 9 mira la FORMA (comillas de Calcpad). Este mira el CONTENIDO: se
        # sacan los nombres que los Tag llaman como funcion —  nombre(  — y se le
        # pregunta al motor si existen. Asi se caza la herencia de Calcpad (una funcion
        # que aqui no existe sale "Undefined function" en la hoja, no antes).
        # Fuera los de UNA letra: en las plantillas son variables de ejemplo (A(i,j), f(x)).
        llamados = {}
        for b in app.cmd(op="buttons")["buttons"]:
            for n in IDENT.findall(b["tag"]):
                if len(n) > 1:
                    llamados.setdefault(n, set()).add(b["name"] or "(sin nombre)")
        desconocidos = app.cmd(op="knows", names=sorted(llamados))["nadie"]
        check("todo lo que llaman los Tag existe en el motor",
              [(n, sorted(llamados[n])) for n in desconocidos], [])

        # --- 12bis. el AUTOCOMPLETADO ofrece funciones del motor -----------------
        # Jorge (17-sep-2026): «cuando escriba debe salir una autoayuda». El popup SI
        # existia, pero no habia forma de comprobarlo: es OTRA ventana y no sale en las
        # capturas, asi que «no funciona» parecia cierto. La op `complete` devuelve lo
        # que el popup ofreceria, y ademas si el editor que lo tiene esta activo: con el
        # editor clasico (Plegado desmarcado) NO hay autocompletado y nada lo avisa.
        ac = app.cmd(op="complete", prefix="pl")
        check("el autocompletado ofrece las funciones de MATLAB",
              "plot" in (ac.get("primeros") or []), True)
        check("...y solo funciona con el editor plegable activo", ac.get("plegable"), True)
        ac2 = app.cmd(op="complete", prefix="ze")
        check("...y filtra por el prefijo que escribes",
              all(t.lower().startswith("ze") for t in (ac2.get("primeros") or ["x"])), True)

        # --- 12. el .m se calcula con el motor de MATLAB, no con el heredado ----
        # Es el fallo que se vio en Hekatan Python3: el archivo caia al parser de
        # Calcpad y la hoja se llenaba de errores raros ("Missing end", el % de una
        # cadena tomado por comentario). Aqui se pregunta CON QUE motor se calculo.
        app.code("x = 2 + 3")
        app.cmd(op="run")
        st = app.cmd(op="state")
        check("un buffer .m lo calcula el motor MATLAB", st["motor"], "matlab")
        check("...y el resultado sale en la hoja", "5" in app.reporte(), True)

    finally:
        app.close()

    print("\n%d ok, %d fallan" % (OK, len(FALLOS)))
    for f in FALLOS:
        print("  - %s" % f)
    return 1 if FALLOS else 0


if __name__ == "__main__":
    sys.exit(main())
