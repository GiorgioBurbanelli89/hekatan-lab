"""Compara la velocidad en BUCLES (lo que motivo la bandera del JIT) con tiered ON/OFF.

    python fem_bench.py          # tal como esta compilado (tiered ON + QuickJitForLoops=false)
    DOTNET_TieredCompilation=0 python fem_bench.py    # como estaba antes
"""
import io, os, sys, time
sys.stdout = io.TextIOWrapper(sys.stdout.buffer, encoding="utf-8", errors="replace")
sys.path.insert(0, r"C:\Users\j-b-j\Documents\Hekatan Calc 1.0.0\hekatan-lab\tests\wpf")
from run_tests import App, DEFAULT_EXE

DIAG = os.path.join(os.environ["TEMP"], "calcpad_lab_diag.log")

FEM = """n = 400;
K = zeros(n);
for e = 1:2000
    i = mod(e*7, n-1) + 1;
    ke = [1 -1; -1 1]*(1 + mod(e,5));
    K(i:i+1, i:i+1) = K(i:i+1, i:i+1) + ke;
end
f = zeros(n,1); f(n) = 1;
K(1,:) = 0; K(:,1) = 0; K(1,1) = 1;
u = K\\f;
umax = max(abs(u))
"""

LOOP = """s = 0;
for k = 1:200000
    s = s + sqrt(k);
end
s
"""


def motor_ms():
    filas = [l for l in open(DIAG, encoding="utf-8").read().split("\n") if " START L" in l or " DONE  L" in l]
    def t(l):
        h, m, s = l.split(" ")[0].split(":")
        return (int(h) * 60 + int(m)) * 60 + float(s)
    return int((t(filas[-1]) - t(filas[0])) * 1000)


app = App(DEFAULT_EXE)
try:
    for nombre, src in [("FEM 400x400", FEM), ("loop 2e5", LOOP), ("FEM otra vez", FEM), ("loop otra vez", LOOP)]:
        app.code(src)
        t = time.time()
        app.cmd(op="run")
        print("%-15s ventana %5.1fs   motor %6d ms" % (nombre, time.time() - t, motor_ms()))
finally:
    app.close()
