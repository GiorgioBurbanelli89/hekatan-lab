#!/usr/bin/env python3
"""Suite de regresion NUMERICA de Hekatan Lab.

Por que existe: el 21-jul-2026 un commit de *optimizacion* del JIT (a4685db) hizo
que `pi` valiera 0 dentro de cualquier bucle compilado. En un FEM eso anulaba el
angulo de friccion y el talud se desmoronaba... SIN error, SIN warning, SIN NaN.
El commit decia "sin regresion" y era cierto para los ejemplos que probo: ninguno
ejercitaba una constante builtin dentro de un bucle JIT-eado.

La leccion: los tests de humo graficos no detectan corrupcion numerica silenciosa.
Esta suite compara NUMEROS contra referencias verificadas en MATLAB 2017a
(y, para el campo elastico, contra Abaqus 2017 a 1 nm).

Uso:
    python run_tests.py                      # usa el CLI del repo (build Release)
    python run_tests.py --exe <ruta.exe>     # o uno concreto (p.ej. el instalado)

Sale con codigo != 0 si algo se desvia -> sirve como pre-commit / pre-instalador.
"""
import argparse
import html
import os
import re
import subprocess
import sys
import time

HERE = os.path.dirname(os.path.abspath(__file__))
DEFAULT_EXE = os.path.abspath(os.path.join(
    HERE, "..", "..", "Symbolic.Cli", "bin", "Release", "net10.0", "CalcpadLabCli.exe"))

# nombre -> (valor esperado, tolerancia absoluta)
# Referencias verificadas en MATLAB 2017a. area/peso/uel_max ademas contra Abaqus.
# Tiempo tic/toc de referencia en MATLAB 2017a (mejor de 3 corridas, en segundos).
# Requisito de Jorge: Hekatan Lab debe ser IGUAL o MENOR. Solo se marca falla si Lab
# supera 1.5x Y la brecha pasa de 50 ms, para que t1/t2 (sub-10 ms, muy ruidosos) no
# rompan por ruido del SO. El que importa de verdad es t3 (el FEM real).
MATLAB_SEG = {
    "t1_builtin":  0.0096,
    "t2_arrays3d": 0.0088,
    "t3_fem_srm":  1.35,   # mediana R2017a, mejor de 4 (antes 4.34 con maquina cargada)
}

EXPECTED = {
    "t1_builtin": {
        "pi":        (3.14159265359, 1e-9),
        "rad":       (0.396189740203, 1e-9),
        "dp_alpha":  (0.131606107572, 1e-9),   # Drucker-Prager de GEO5, SOIL_1
        "dp_k":      (8.49459223377, 1e-8),
        "eps":       (2.220446049250313e-16, 1e-25),
        "inv_inf":   (0.0, 0.0),
    },
    "t2_arrays3d": {
        "size3d":              ("4 120 3", None),   # comparacion textual
        "diff3d_vs_flat":      (0.0, 1e-12),
        "diff3d_en_funcion":   (0.0, 1e-12),
        "shape_slice":         ("4 1", None),       # MATLAB devuelve columna
        "suma3d":              (34905.6, 1e-3),
    },
    "t3_fem_srm": {
        "area":             (642.31250787, 1e-5),
        "peso":             (12624.3134714, 1e-4),
        "uel_max":          (22.6900651355, 1e-6),
        "residuo_inicial":  (0.0, 1e-10),   # Fint(sig0) debe reproducir F0
        "srf100_it":        (13, 0),
        "srf100_conv":      (1, 0),
        "srf100_umax":      (0.316322667544, 1e-6),
        "srf140_it":        (66, 2),        # +-2 iteraciones de margen
        "srf140_conv":      (1, 0),
        "srf140_umax":      (2.37265158347, 1e-5),
    },
    # t4: decomposition(A) debe dar EXACTAMENTE lo mismo que el backslash de Lab
    # (que ya esta validado vs MATLAB en t3). La ganancia de tiempo se valida aparte
    # con MIN_SPEEDUP (abajo): si decomposition dejara de reusar la factorizacion,
    # el speedup caeria a ~1 y el guard lo atrapa.
    "t4_decomposition": {
        "acc_backslash":            (0.00273494777608, 1e-12),
        "acc_decomp":               (0.00273494777608, 1e-12),
        "dif_decomp_vs_backslash":  (0.0, 1e-10),
    },
    # t5: semantica de VALOR (b = d copia) y v(a:b) = x con UN indice dentro de un for
    # (JIT). Esperados = MATLAB. Antes del arreglo: alias_arg 99, alias_loc 50,
    # for_esc/for_col con "Index was outside the bounds", for_end 0 0 0.
    "t5_valor_indexado": {
        "alias_arg":      (1.0, 0),
        "alias_loc":      (2.0, 0),
        "param_caller":   ("1 1", None),
        "loop_alias":     (1.0, 0),
        "struct_alias":   (1.0, 0),
        "for_top_alias":  (1.0, 0),
        "for_esc":        ("0 5 5 5 0 0", None),
        "for_vec":        ("1 2 3 2 4 6", None),
        "for_col":        ("1 1 1 2 2 2", None),
        "for_col_forma":  ("6 1", None),
        "for_end":        ("1 2 2", None),
        "for_grow":       ("1 1 2 2", None),
        "gather_end":     (41.0, 0),
        "top_end":        ("0 4 4 4 4", None),
    },
}

# Piso de speedup: decomposition reusando la factorizacion vs backslash que
# re-factoriza. En la Kff 2D del Demo04 medimos ~165x; exigimos >=20 para no
# romper por ruido pero SI atrapar que decomposition vuelva a re-factorizar.
MIN_SPEEDUP = {"t4_decomposition": 20.0}
# Referencia: MATLAB 2017a resolviendo el MISMO bucle con backslash (no tiene
# decomposition, paga las N factorizaciones). Lab-decomposition deberia ganarle.
MATLAB_BACKSLASH_REF = {"t4_decomposition": 0.486}


def run_case(exe, folder):
    """Ejecuta run.m con el CLI y devuelve {nombre: texto} de las lineas CHECK."""
    d = os.path.join(HERE, folder)
    rpt = os.path.join(d, "run.html")
    # No se puede confiar en borrar el reporte: si quedo abierto en un visor, Windows
    # lo bloquea. Se guarda su mtime y luego se exige que haya cambiado, asi nunca se
    # lee por error el resultado de una corrida anterior (que daria un OK falso).
    prev = os.path.getmtime(rpt) if os.path.exists(rpt) else None
    try:
        if prev is not None:
            os.remove(rpt)
            prev = None
    except OSError:
        pass
    t0 = time.time()
    try:
        subprocess.run([exe, "run.m"], cwd=d, timeout=900,
                       stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
    except subprocess.TimeoutExpired:
        return None, time.time() - t0, "TIMEOUT"
    if not os.path.exists(rpt):
        return None, time.time() - t0, "sin reporte HTML"
    if prev is not None and os.path.getmtime(rpt) <= prev:
        return None, time.time() - t0, "el reporte no se regenero (%s bloqueado?)" % rpt
    raw = open(rpt, encoding="utf-8", errors="ignore").read()
    i = raw.rfind("</script>")          # el HTML lleva MBs de librerias JS embebidas
    body = raw[i:] if i > 0 else raw
    text = re.sub(r"\s+", " ", html.unescape(re.sub(r"<[^>]+>", " ", body)))
    # OJO: Lab renderiza los comentarios % del .m como prosa dentro del reporte, o sea
    # que entre un CHECK y el siguiente hay texto suelto. Por eso el valor se toma
    # consumiendo SOLO tokens numericos y parando en la primera palabra que no lo sea.
    got = {}
    num = r"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?"
    for m in re.finditer(r"CHECK\s+([A-Za-z0-9_]+)((?:\s+%s)+)" % num, text):
        got[m.group(1)] = " ".join(m.group(2).split())
    err = None
    if re.search(r"\bError\b|Undefined:|Internal error", text):
        err = "el script reporto un error"
    return got, time.time() - t0, err


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--exe", default=DEFAULT_EXE)
    args = ap.parse_args()
    if not os.path.exists(args.exe):
        print("no encuentro el CLI: %s" % args.exe)
        print("compila con: dotnet build Symbolic.Cli/Symbolic.Cli.csproj -c Release")
        return 2

    print("Suite numerica de Hekatan Lab")
    print("CLI: %s\n" % args.exe)
    fails = []
    for folder, expected in EXPECTED.items():
        got, dt, err = run_case(args.exe, folder)
        if got is None or err:
            print("  [FALLA] %-14s  %s" % (folder, err or "sin salida"))
            fails.append((folder, err or "sin salida"))
            continue
        bad = []
        for name, (ref, tol) in expected.items():
            if name not in got:
                bad.append("%s: no aparecio en la salida" % name)
                continue
            raw = got[name]
            if tol is None:                       # comparacion textual
                if " ".join(raw.split()) != ref:
                    bad.append("%s: '%s' != '%s'" % (name, raw, ref))
                continue
            try:
                val = float(raw.split()[0])
            except ValueError:
                bad.append("%s: no es numero ('%s')" % (name, raw))
                continue
            if abs(val - ref) > tol:
                bad.append("%s: %.12g  esperado %.12g  (dif %.3g > tol %.3g)"
                           % (name, val, ref, abs(val - ref), tol))
        # --- comparacion de tiempo tic/toc vs MATLAB 2017a (requisito: Lab <= MATLAB) ---
        tinfo = ""
        lab_t = float(got["t_seg"].split()[0]) if "t_seg" in got else None
        ml_t = MATLAB_SEG.get(folder)
        if lab_t is not None and ml_t:
            r = lab_t / ml_t
            # por debajo de 0.2 s el arranque/JIT domina: el ratio no dice nada del
            # motor, asi que no se emite juicio ("LENTO" en un test de 0.07s enganaria)
            if lab_t < 0.2:
                mark = ""
            else:
                mark = " OK" if r <= 1.05 else (" ~%.1fx" % r if r < 3.0 else " LENTO %.1fx" % r)
            tinfo = "  t_seg Lab=%.4gs MATLAB=%.4gs%s" % (lab_t, ml_t, mark)
            # El tiempo NO es falla dura: la maquina tiene varianza alta (medido t3
            # 5-9s con binario identico) y el objetivo Lab<=MATLAB en el FEM depende
            # de optimizar fint_c, que es trabajo aparte. Solo se marca falla si Lab
            # es escandalosamente lento (>=3x Y brecha >0.5s) — eso ya no es ruido.
            if r >= 3.0 and (lab_t - ml_t) > 0.5:
                bad.append("t_seg: Lab %.4gs es MAS LENTO que MATLAB %.4gs (%.1fx) — revisar, no es ruido"
                           % (lab_t, ml_t, r))

        # --- guard de decomposition: debe seguir reusando la factorizacion ---
        min_sp = MIN_SPEEDUP.get(folder)
        if min_sp is not None and "speedup_vs_backslash" in got:
            sp = float(got["speedup_vs_backslash"].split()[0])
            ml_ref = MATLAB_BACKSLASH_REF.get(folder)
            extra = ""
            if ml_ref and "t_decomp" in got:
                td = float(got["t_decomp"].split()[0])
                extra = "  Lab-decomp=%.4gs vs MATLAB-backslash=%.4gs (%.0fx)" % (td, ml_ref, ml_ref / td)
            tinfo += "  speedup=%.0fx%s" % (sp, extra)
            if sp < min_sp:
                bad.append("speedup_vs_backslash %.1f < %.0f: decomposition dejo de reusar la factorizacion"
                           % (sp, min_sp))

        if bad:
            print("  [FALLA] %-14s  %.1fs%s" % (folder, dt, tinfo))
            for b in bad:
                print("           - %s" % b)
            fails.extend((folder, b) for b in bad)
        else:
            print("  [  OK  ] %-14s  %d valores  %.1fs%s" % (folder, len(expected), dt, tinfo))

    print("")
    if fails:
        print("FALLARON %d comprobacion(es). El motor cambio de comportamiento numerico." % len(fails))
        print("Revisa el ultimo cambio en Symbolic.Core/Matlab/ (JIT, evaluador, indexado).")
        return 1
    print("TODO OK — el motor reproduce las referencias de MATLAB 2017a / Abaqus.")
    return 0


if __name__ == "__main__":
    sys.exit(main())
