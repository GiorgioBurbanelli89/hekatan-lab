// Calcpad Lab — Tests de gaussint / gaussint2 (cuadratura de Gauss-Legendre)
// y de las 5 operaciones simbólicas sin disp/fprintf. Los valores se verifican
// DENTRO del script con assert() (motor real, sin depender del formato HTML).
namespace Calcpad.Lab.Tests
{
    public class TestGaussInt
    {
        private static int RunAndCountErrors(string script)
            => new TestLab().RunAndCountErrors(script);

        // gaussint(expr, x, a, b) con variable simbólica: ∫₀¹ x² dx = 1/3
        [Fact]
        public void GaussInt_SymbolicExpr_X2_ReturnsThird()
            => Assert.Equal(0, RunAndCountErrors(
                "syms x\n" +
                "assert(abs(gaussint(x^2, x, 0, 1) - 1/3) < 1e-12)"));

        // n=3 ya es exacto para polinomios de grado ≤ 2n-1
        [Fact]
        public void GaussInt_ThreePoints_ExactForQuadratic()
            => Assert.Equal(0, RunAndCountErrors(
                "syms x\n" +
                "assert(abs(gaussint(x^2, x, 0, 1, 3) - 1/3) < 1e-12)"));

        // gaussint con function handle: ∫₀¹ e^(−t²) dt ≈ 0.746824132812
        [Fact]
        public void GaussInt_FunctionHandle_Gaussian()
            => Assert.Equal(0, RunAndCountErrors(
                "assert(abs(gaussint(@(t) exp(-t.^2), 0, 1) - 0.746824132812) < 1e-9)"));

        // gaussint2 (producto tensorial 2D): ∬ x·y dx dy sobre [0,1]² = 0.25
        [Fact]
        public void GaussInt2_Product_ReturnsQuarter()
            => Assert.Equal(0, RunAndCountErrors(
                "assert(abs(gaussint2(@(u, v) u.*v, 0, 1, 0, 1) - 0.25) < 1e-12)"));

        // gaussint2 debe dar lo mismo que integral2 / int(int(...))
        [Fact]
        public void GaussInt2_MatchesIntegral2()
            => Assert.Equal(0, RunAndCountErrors(
                "assert(abs(gaussint2(@(x, y) x.^2.*y, 0, 1, 0, 2) - integral2(@(x, y) x.^2.*y, 0, 1, 0, 2)) < 1e-10)"));

        // Variable distinta de x: gaussint(t^3, t, 0, 2) = 4
        [Fact]
        public void GaussInt_OtherVariable()
            => Assert.Equal(0, RunAndCountErrors(
                "syms t\n" +
                "assert(abs(gaussint(t^3, t, 0, 2) - 4) < 1e-12)"));

        // Script completo de las 5 operaciones sin disp/fprintf, SOLO funciones
        // nativas de MATLAB 2017a (gaussint NO se usa aquí): 0 errores.
        [Fact]
        public void Script_Las5Operaciones_NoErrors()
        {
            const string script = """
                syms x y k n
                symsum(k^2, k, 1, n)
                symsum(k, k, 1, 100)
                int(x^2, x)
                int(x^2, x, 0, 1)
                int(int(x*y, x, 0, 1), y, 0, 1)
                integral2(@(x, y) x.*y, 0, 1, 0, 1)
                quadgk(@(t) exp(-t.^2), 0, 1)
                quadl(@(t) exp(-t.^2), 0, 1)
                S = 0;
                for k = 1:100
                    S = S + k^2;
                end
                S
                """;
            Assert.Equal(0, RunAndCountErrors(script));
        }

        // La forma LOOP de Gauss-Legendre (del ejemplo) debe dar ⅓.
        [Fact]
        public void Script_GaussLoop_ReturnsThird()
        {
            const string script = """
                gp = [-0.774596669241; 0; 0.774596669241];
                gw = [5/9; 8/9; 5/9];
                a = 0; b = 1; G = 0;
                for k = 1:3
                    xk = (a+b)/2 + (b-a)/2*gp(k);
                    G = G + gw(k)*xk^2;
                end
                G = (b-a)/2*G;
                assert(abs(G - 1/3) < 1e-12)
                """;
            Assert.Equal(0, RunAndCountErrors(script));
        }

        // El RENDER (pipeline real de la app) usa el MISMO CSS que int: integral2 →
        // ∬ … dx dy, quadgk/quadl/gaussint → ∫ … dx. Nada de "nombre de función" en texto.
        [Fact]
        public void Render_IntegralesNumericas_UsanCssPretty()
        {
            var pipe = new Calcpad.Core.Matlab.MatlabPipeline();
            string html = pipe.Run("""
                syms x y
                I2 = integral2(@(x, y) x.*y, 0, 1, 0, 1)
                QG = quadgk(@(t) exp(-t.^2), 0, 1)
                GI = gaussint(@(x) x.^2, 0, 1, 5)
                """);
            Assert.DoesNotContain("integral2", html);
            Assert.DoesNotContain("quadgk", html);
            Assert.DoesNotContain("gaussint", html);
            Assert.Contains("\u222c", html);   // ∬  doble integral
            Assert.Contains("\u222b", html);   // ∫  integral simple
            Assert.Contains("d<var>x</var>", html);
            Assert.Contains("d<var>t</var>", html);
            Assert.Contains("= 0.25", html);
            Assert.Contains("= 0.746824", html);
            Assert.Contains("= 0.333333", html);
        }
    }
}
