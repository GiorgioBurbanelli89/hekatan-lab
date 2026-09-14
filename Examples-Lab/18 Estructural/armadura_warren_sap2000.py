# -*- coding: utf-8 -*-
"""La MISMA armadura Warren de Armadura_Warren_SAP2000.m en SAP2000 por OAPI (el juez).
Barras con momentos liberados en los dos extremos (armadura), E = 200e6 kN/m2, A = 0.01 m2, sin peso propio,
300 kN en el nudo 2. Vuelca fuerzas axiales P, reacciones y desplazamientos a Armadura_Warren_SAP2000_sap.json.
    python armadura_warren_sap2000.py"""
import sys, os, json
sys.path.insert(0, r"C:\Users\j-b-j\Documents\Hekatan Calc 1.0.0\csi-cli\hekatan-csi-cli")
import csi_cli as c
sys.stdout.reconfigure(encoding="utf-8")
AQUI = os.path.dirname(os.path.abspath(__file__))
b, h, E, A = 8.0, 4.0, 200e6, 0.01
NUDOS = [(0, 0), (b, 0), (2 * b, 0), (b / 2, h), (3 * b / 2, h)]
BARRAS = [(1, 2), (2, 3), (4, 5), (1, 4), (4, 2), (2, 5), (5, 3)]
_, S, _ = c.start_engine("sap", 6, True)
S.InitializeNewModel(6); S.File.NewBlank()
S.PropMaterial.SetMaterial("ACERO", 1); S.PropMaterial.SetMPIsotropic("ACERO", E, 0.3, 1.2e-5)
S.PropFrame.SetGeneral("BARRA", "ACERO", 0.1, 0.1, A, A, A, 1e-6, 1e-6, 1e-6, 1e-6, 1e-6, 1e-6, 1e-6, 0.01, 0.01)
p = [S.PointObj.AddCartesian(x, 0, y, "", "N%d" % (k + 1))[0] for k, (x, y) in enumerate(NUDOS)]
fr = []
for k, (i, j) in enumerate(BARRAS):
    nm = S.FrameObj.AddByPoint(p[i - 1], p[j - 1], "", "BARRA", "F%d" % (k + 1))[0]
    S.FrameObj.SetReleases(nm, [False, False, False, False, True, True], [False, False, False, False, True, True], [0] * 6, [0] * 6)
    fr.append(nm)
# plano XZ: se restringe todo lo que no es de la armadura plana
for k, nm in enumerate(p):
    r = [False, True, False, True, False, True]
    if k == 0: r = [True, True, True, True, False, True]
    if k == 2: r = [False, True, True, True, False, True]
    S.PointObj.SetRestraint(nm, r)
S.LoadPatterns.SetSelfWTMultiplier("DEAD", 0)
S.PointObj.SetLoadForce(p[1], "DEAD", [0, 0, -300, 0, 0, 0])
S.File.Save(os.path.join(AQUI, "Armadura_Warren_SAP2000_sap.sdb"))
S.Analyze.SetRunCaseFlag("MODAL", False)
print("run", S.Analyze.RunAnalysis(), flush=True)
S.Results.Setup.DeselectAllCasesAndCombosForOutput(); S.Results.Setup.SetCaseSelectedForOutput("DEAD")
out = {"P": [], "reacciones": {}, "desplazamientos": {}}
for k, nm in enumerate(fr):
    r = S.Results.FrameForce(nm, 0)
    out["P"].append({"barra": k + 1, "IJ": "%d-%d" % BARRAS[k], "P": float(r[8][0])})
for k, nm in enumerate(p):
    r = S.Results.JointDispl(nm, 0)
    out["desplazamientos"][k + 1] = {"U1": float(r[6][0]), "U3": float(r[8][0])}
    if k in (0, 2):
        q = S.Results.JointReact(nm, 0)
        out["reacciones"][k + 1] = {"F1": float(q[6][0]), "F3": float(q[8][0])}
json.dump(out, open(os.path.join(AQUI, "Armadura_Warren_SAP2000_sap.json"), "w"), indent=1)
print(json.dumps(out, indent=1), flush=True)
os._exit(0)
