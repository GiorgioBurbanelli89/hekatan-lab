using System;
using System.Text;
using Calcpad.Core.Matlab;

namespace Calcpad.Core
{
    /// <summary>
    /// Genera código MATLAB PORTABLE (2017a) para operaciones de acumulación
    /// escritas en notación matemática: sumatoria (Σ), productoria (∏) e
    /// integrales simple (∫), doble (∬) y triple (∭). El usuario elige la FORMA:
    /// por bucle `for` explícito (se ve cada iteración) o por función builtin
    /// (compacto). Alimenta a la ventana-loop de Hekatan Lab. Todo lo que emite
    /// corre igual en MATLAB real.
    /// </summary>
    public static class LoopBuilder
    {
        public enum Op { Sum, Product, Integral, DoubleIntegral, TripleIntegral }

        /// <summary>Formas disponibles. Suma/Producto: Loop | Function. Integral
        /// simple: Function(integral) | FunctionTrapz | LoopTrapezoid | LoopSimpson
        /// | FunctionGauss (gaussint) | LoopGauss (loop con nodos/pesos inline).
        /// Doble/Triple: Function(integral2/3) | FunctionArrayValued(anidado,
        /// matriz-seguro) | Loop(trapecio anidado).</summary>
        public enum Form { Loop, Function, FunctionTrapz, LoopTrapezoid, LoopSimpson, FunctionArrayValued, FunctionGauss, LoopGauss }

        /// <summary>¿Cuántas variables (dimensiones) usa el operador?</summary>
        public static int Dims(Op op) => op switch
        {
            Op.DoubleIntegral => 2,
            Op.TripleIntegral => 3,
            _ => 1
        };

        /// <summary>Símbolo matemático del operador.</summary>
        public static string Symbol(Op op) => op switch
        {
            Op.Sum => "∑",
            Op.Product => "∏",
            Op.Integral => "∫",
            Op.DoubleIntegral => "∬",
            Op.TripleIntegral => "∭",
            _ => "?"
        };

        // Conveniencia de 1 variable (suma/producto/integral simple).
        public static string Build(Op op, string expr, string var, string from, string to, Form form, string result = null)
            => Build(op, expr, new[] { var }, new[] { from }, new[] { to }, form, result);

        /// <summary>Genera el MATLAB. `vars/froms/tos` traen 1, 2 o 3 elementos
        /// según el operador.</summary>
        public static string Build(Op op, string expr, string[] vars, string[] froms, string[] tos, Form form, string result = null)
        {
            expr = (expr ?? "").Trim();
            string[] v = Clean(vars, op == Op.Integral || op >= Op.DoubleIntegral ? "x" : "k");
            string[] a = Clean(froms, op == Op.Sum || op == Op.Product ? "1" : "0");
            string[] b = Clean(tos, "1");
            string res = string.IsNullOrWhiteSpace(result)
                ? (op == Op.Sum ? "s" : op == Op.Product ? "p" : "I")
                : result.Trim();
            const string nl = "\n";

            switch (op)
            {
                case Op.Sum:
                    return form == Form.Function
                        ? $"{res} = sum(arrayfun(@({v[0]}) ({expr}), {a[0]}:{b[0]}));"
                        : $"{res} = 0;{nl}for {v[0]} = {a[0]}:{b[0]}{nl}    {res} = {res} + ({expr});{nl}end";

                case Op.Product:
                    return form == Form.Function
                        ? $"{res} = prod(arrayfun(@({v[0]}) ({expr}), {a[0]}:{b[0]}));"
                        : $"{res} = 1;{nl}for {v[0]} = {a[0]}:{b[0]}{nl}    {res} = {res} * ({expr});{nl}end";

                case Op.Integral:
                    return BuildIntegral1(expr, v[0], a[0], b[0], form, res);

                case Op.DoubleIntegral:
                case Op.TripleIntegral:
                    return BuildIntegralN(op, expr, v, a, b, form, res);
            }
            return "";
        }

        // ── Integral simple ────────────────────────────────────────────────
        private static string BuildIntegral1(string expr, string x, string a, string b, Form form, string res)
        {
            const string nl = "\n";
            switch (form)
            {
                case Form.Function:
                    return $"{res} = integral(@({x}) ({expr}), {a}, {b});";
                case Form.FunctionArrayValued:
                    return $"{res} = integral(@({x}) ({expr}), {a}, {b}, 'ArrayValued', true);";
                case Form.FunctionTrapz:
                    return $"{x}_ = linspace({a}, {b}, 101);{nl}{res} = trapz({x}_, arrayfun(@({x}) ({expr}), {x}_));";
                case Form.FunctionGauss:
                    // gaussint es un builtin de Hekatan Lab (cuadratura de Gauss-Legendre).
                    return $"{res} = gaussint(@({x}) ({expr}), {a}, {b}, 5);";
                case Form.LoopGauss:
                    // Loop portable con nodos/pesos de Gauss-Legendre inline (MATLAB real).
                    MatlabEvaluator.GaussLegendre(3, out var gp, out var gw);
                    var sb = new StringBuilder();
                    sb.Append($"{res} = 0; c = (({b}) + ({a}))/2; h = (({b}) - ({a}))/2;");
                    for (int i = 0; i < 3; i++)
                        sb.Append(nl).Append($"{x} = c + h*({gp[i].ToString("G17")});   % x{i + 1} (Gauss, w = {gw[i]:G6})" + nl +
                                             $"    {res} = {res} + ({gw[i].ToString("G17")})*({expr});");
                    sb.Append(nl).Append($"{res} = h*{res};");
                    return sb.ToString();
                case Form.LoopSimpson:
                    return
                        $"n = 100; h = ({b} - ({a}))/n; {res} = 0;{nl}" +
                        $"for i = 0:n{nl}    {x} = {a} + i*h;{nl}" +
                        $"    if i == 0 || i == n{nl}        w = 1;{nl}    elseif mod(i,2) == 1{nl}        w = 4;{nl}    else{nl}        w = 2;{nl}    end{nl}" +
                        $"    {res} = {res} + w*({expr});{nl}end{nl}{res} = {res}*h/3;";
                default: // LoopTrapezoid
                    return
                        $"n = 100; h = ({b} - ({a}))/n; {res} = 0;{nl}" +
                        $"for k = 0:n{nl}    {x} = {a} + k*h;{nl}" +
                        $"    if k == 0 || k == n{nl}        {res} = {res} + 0.5*({expr});{nl}    else{nl}        {res} = {res} + ({expr});{nl}    end{nl}end{nl}{res} = {res}*h;";
            }
        }

        // ── Integral doble/triple ──────────────────────────────────────────
        private static string BuildIntegralN(Op op, string expr, string[] v, string[] a, string[] b, Form form, string res)
        {
            int n = Dims(op);
            const string nl = "\n";

            // Función builtin real de MATLAB: integral2 / integral3
            if (form == Form.Function)
            {
                string vlist = string.Join(",", v[..n]);
                string bounds = n == 2
                    ? $"{a[0]}, {b[0]}, {a[1]}, {b[1]}"
                    : $"{a[0]}, {b[0]}, {a[1]}, {b[1]}, {a[2]}, {b[2]}";
                return $"{res} = integral{n}(@({vlist}) ({expr}), {bounds});";
            }

            // Matriz-segura: integral anidado con 'ArrayValued' (FEM: ∬ Bᵀ·D·B)
            if (form == Form.FunctionArrayValued)
            {
                // Anidamos de la variable interna a la externa.
                string inner = $"({expr})";
                for (int k = 0; k < n; k++)
                    inner = $"integral(@({v[k]}) ({inner}), {a[k]}, {b[k]}, 'ArrayValued', true)";
                return $"{res} = {inner};";
            }

            // Bucle anidado (trapecio en cada dimensión).
            var sb = new StringBuilder();
            sb.Append($"{res} = 0;").Append(nl);
            string indent = "";
            for (int k = 0; k < n; k++)
            {
                sb.Append(indent).Append($"n{k} = 50; h{k} = ({b[k]} - ({a[k]}))/n{k};").Append(nl);
                sb.Append(indent).Append($"for i{k} = 0:n{k}").Append(nl);
                indent += "    ";
                sb.Append(indent).Append($"{v[k]} = {a[k]} + i{k}*h{k};").Append(nl);
                sb.Append(indent).Append($"if i{k} == 0 || i{k} == n{k}, w{k} = 0.5; else, w{k} = 1; end").Append(nl);
            }
            string wprod = "";
            string hprod = "";
            for (int k = 0; k < n; k++) { wprod += (k > 0 ? "*" : "") + $"w{k}"; hprod += (k > 0 ? "*" : "") + $"h{k}"; }
            sb.Append(indent).Append($"{res} = {res} + {wprod}*({expr});").Append(nl);
            for (int k = n - 1; k >= 0; k--)
            {
                indent = new string(' ', 4 * k);
                sb.Append(indent).Append("end").Append(nl);
            }
            sb.Append($"{res} = {res}*{hprod};");
            return sb.ToString();
        }

        private static string[] Clean(string[] arr, string def)
        {
            var r = new string[3];
            for (int i = 0; i < 3; i++)
            {
                string s = (arr != null && i < arr.Length) ? (arr[i] ?? "").Trim() : "";
                r[i] = string.IsNullOrWhiteSpace(s) ? (i == 0 ? def : (def == "x" ? new[] { "x", "y", "z" }[i] : def)) : s;
            }
            return r;
        }
    }
}
