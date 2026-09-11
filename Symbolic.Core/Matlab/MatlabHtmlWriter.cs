// =============================================================================
// Calcpad Lab — MATLAB HTML Writer (output MATLAB puro, sin keywords Calcpad)
// =============================================================================
//   Toma un AST + valor evaluado y produce HTML que muestra:
//
//     Z = sin(X) .* cos(Y) = [matrix]
//
//   Sin `hprod`, sin `;` como separador de args, sin keywords de Calcpad.
//   El usuario nunca ve un nombre interno de Calcpad en su pantalla.
// =============================================================================
using System.Globalization;
using System.Text;
using System.Web;

namespace Calcpad.Core.Matlab
{
    public static class MatlabHtmlWriter
    {
        /// <summary>Provee el valor actual de una variable (lo fija el pipeline apuntando a Globals).
        /// Usado por el paso de sustitucion Calcpad-style (n_e = n_a·n_b = 6·4 = 24).</summary>
        public static System.Func<string, MValue> VarValueProvider;

        /// <summary>Clona el AST reemplazando cada variable ESCALAR conocida por su valor numerico
        /// (NumberLit). Reusa RenderExpression sobre el resultado para el "paso de sustitucion".</summary>
        private static MatlabNode SubstituteValues(MatlabNode n)
        {
            switch (n)
            {
                case IdentRef id:
                    var v = VarValueProvider?.Invoke(id.Name);
                    if (v != null && v.IsScalar && !v.IsString) return new NumberLit { Value = v.Scalar };
                    return n;
                case BinaryOp b:
                    return new BinaryOp { Op = b.Op, Left = SubstituteValues(b.Left), Right = SubstituteValues(b.Right), Line = b.Line };
                case UnaryOp u:
                    return new UnaryOp { Op = u.Op, Operand = SubstituteValues(u.Operand), IsPrefix = u.IsPrefix, Line = u.Line };
                case CallOrIndex c:
                {
                    var args = new System.Collections.Generic.List<MatlabNode>(c.Args.Count);
                    foreach (var a in c.Args) args.Add(SubstituteValues(a));
                    return new CallOrIndex { Target = c.Target, Args = args, Line = c.Line };
                }
                default:
                    return n;
            }
        }

        /// <summary>Render statement (formula + result) en HTML MATLAB-style.</summary>
        public static string RenderStatement(MatlabNode stmt, StatementResult result)
        {
            var sb = new StringBuilder();
            switch (stmt)
            {
                case CommentStmt cs:
                    if (cs.IsHeading)
                        // Encabezado limpio que usa el CSS del template (como Calcpad),
                        // sin estilos inline ni color forzado.
                        sb.Append($"<h3>{HttpUtility.HtmlEncode(cs.Text)}</h3>");
                    // El texto de comentario en Calcpad-Lab admite HTML enriquecido — igual
                    // que el texto `'...` de Calcpad puro (headings, <p>, <table>, <svg>...).
                    // Si contiene tags HTML, se emite RAW (se renderiza).
                    else if (System.Text.RegularExpressions.Regex.IsMatch(cs.Text, "<[a-zA-Z/!]"))
                        sb.Append(cs.Text);
                    else
                        // Comentario visible = texto normal (como Calcpad `'...`), NUNCA verde.
                        // Para ocultar: usar `%--` (filtrado en MatlabPipeline).
                        sb.Append(HttpUtility.HtmlEncode(cs.Text));
                    break;
                case Assignment asg:
                    sb.Append("<span class=\"eq\">");
                    RenderAssignmentLhs(sb, asg);
                    sb.Append(" = ");
                    {
                        // Reglas (en orden):
                        //  1. Si RHS es llamada simbólica (int/diff/limit/taylor/subs/...),
                        //     SIEMPRE mostrar la notación pretty (∫ … dx, d/dx …, lim, etc.)
                        //     seguida del valor — esa es la informacion mas util.
                        //  2. Si el resultado es Symbolic puro (RHS = polinomio/expresion sin
                        //     funcion simbolica wrapper), mostrar solo el valor normalizado —
                        //     evita duplicacion tipo `Φ₁ = 1 - 3·ξ² + 2·ξ³ = -3·ξ² + 2·ξ³ + 1`.
                        //  3. Otros casos: rhs source = value, con short-circuit si ambos
                        //     renderean identico (literales, matrices puras, etc).
                        bool symbolicCall = IsSymbolicFunctionCall(asg.Rhs);
                        // Escalar simbólico O MATRIZ simbólica: mostrar solo el VALOR (una vez).
                        // Antes solo cubría IsSymbolic (escalar) → una matriz simbólica como
                        // A = [a b; b a] caía al caso "expr = valor" y se duplicaba.
                        bool symbolicResult = result.Value != null
                                              && (result.Value.IsSymbolic || result.Value.IsSymMatrix);
                        if (symbolicCall)
                        {
                            // (1) Pretty notation + value
                            sb.Append(RenderExpression(asg.Rhs));
                            sb.Append(" = ");
                            sb.Append(RenderValue(result.Value));
                        }
                        else if (asg.Rhs is CallOrIndex && PrettyMath && IsPrettyMathCall(asg.Rhs))
                        {
                            // (3a') RHS = función MATEMÁTICA con notación pretty (√, ∑, ∏, ‖·‖,
                            // |·|, ⁻¹, ᵀ, ∛): SÍ mostrar la fórmula seguida del valor — igual que
                            // Calcpad muestra `a = √2 = 1.41`. Es la info útil (qué se calculó).
                            // Va ANTES de symbolicResult: así det(M)->|M|=x²-y² e inv(M)->M⁻¹=[...]
                            // muestran la NOTACIÓN aunque el resultado sea simbólico.
                            var prettyLhs = RenderExpression(asg.Rhs);
                            var prettyVal = RenderValue(result.Value);
                            // Guarda anti-duplicación: si la fórmula y el valor se renderean IGUAL
                            // (p.ej. s = sqrt(x) -> √x = √x), mostrar solo una vez.
                            if (prettyLhs == prettyVal)
                                sb.Append(prettyVal);
                            else
                            {
                                sb.Append(prettyLhs);
                                sb.Append(" = ");
                                sb.Append(prettyVal);
                            }
                        }
                        else if (symbolicResult)
                        {
                            // (2) Solo valor (evita duplicacion polinomica)
                            sb.Append(RenderValue(result.Value));
                        }
                        else if (asg.Rhs is CallOrIndex)
                        {
                            // (3b) RHS = LLAMADA o INDEXADO (v(2), K(1,1), double(L), isUnit(x)…):
                            // NO re-echar la llamada como texto — eso es código y ya está en el
                            // script. El render muestra SOLO el resultado (nombre = valor).
                            sb.Append(RenderValue(result.Value));
                        }
                        else if (asg.Rhs is MatrixLit && result.Value != null
                                 && result.Value.IsString && !result.Value.IsStringArray)
                        {
                            // (3c) RHS = [ '...' num2str(x) '...' ] -> CONCATENACION de char que
                            // da un STRING (no una matriz). Mostrar SOLO el string resultante, no
                            // los corchetes de matriz con los trozos sueltos (se veia mal).
                            sb.Append(RenderValue(result.Value));
                        }
                        else
                        {
                            // (3) Default: source [= SUSTITUIDO] = value (estilo Calcpad).
                            // Paso de sustitucion: el RHS con las variables reemplazadas por su
                            // valor numerico (n_e = n_a·n_b = 6·4 = 24). Solo escalares.
                            var rhsHtml = RenderExpression(asg.Rhs);
                            sb.Append(rhsHtml);
                            if (!IsTrivialAssignment(asg))
                            {
                                var valHtml = RenderValue(result.Value);
                                string subHtml = null;
                                if (result.Value != null && result.Value.IsScalar && VarValueProvider != null)
                                {
                                    var s = RenderExpression(SubstituteValues(asg.Rhs));
                                    if (s != rhsHtml && s != valHtml) subHtml = s;
                                }
                                if (subHtml != null) { sb.Append(" = "); sb.Append(subHtml); }
                                if (valHtml != rhsHtml && valHtml != subHtml)
                                {
                                    sb.Append(" = ");
                                    sb.Append(valHtml);
                                }
                            }
                        }
                    }
                    sb.Append("</span>");
                    break;
                case ExprStmt es:
                    sb.Append("<span class=\"eq\">");
                    {
                        var lhsHtml = RenderExpression(es.Expr);
                        var rhsHtml = RenderValue(result.Value);
                        // Si la expresion es literal/identica al valor (ej. `2`, `x^2+1`
                        // simbolico sin variables a sustituir), no mostrar `expr = expr`
                        // redundante. Solo mostrar el lado derecho.
                        if (lhsHtml == rhsHtml)
                            sb.Append(rhsHtml);
                        else
                        { sb.Append(lhsHtml); sb.Append(" = "); sb.Append(rhsHtml); }
                    }
                    sb.Append("</span>");
                    break;
                case ForLoop fl:
                    sb.Append($"<span class=\"eq\"><b>for</b> {RenderIdentName(fl.VarName)} = ");
                    sb.Append(RenderExpression(fl.Iter));
                    sb.Append($" … <b>end</b>  <span style=\"color:#888\">(loop executed)</span></span>");
                    break;
                case WhileLoop wl:
                    sb.Append("<span class=\"eq\"><b>while</b> ");
                    sb.Append(RenderExpression(wl.Cond));
                    sb.Append(" … <b>end</b>  <span style=\"color:#888\">(loop executed)</span></span>");
                    break;
                case IfBlock ib:
                    sb.Append("<span class=\"eq\"><b>if</b> ");
                    sb.Append(RenderExpression(ib.Branches[0].Cond));
                    sb.Append(" … <b>end</b>  <span style=\"color:#888\">(branch executed)</span></span>");
                    break;
                case ClassDef classd:
                    sb.Append("<span class=\"eq\"><b>classdef</b> ");
                    sb.Append($"<var>{HttpUtility.HtmlEncode(classd.Name)}</var>");
                    if (!string.IsNullOrEmpty(classd.ParentName))
                        sb.Append($" &lt; <var>{HttpUtility.HtmlEncode(classd.ParentName)}</var>");
                    sb.Append($" … <b>end</b>  <span style=\"color:#888\">(class registered: {classd.Properties.Count} props, {classd.Methods.Count} methods)</span></span>");
                    break;
                case FunctionDef fd:
                    sb.Append("<span class=\"eq\"><b>function</b> ");
                    if (fd.OutputNames.Count == 1) sb.Append($"<var>{HttpUtility.HtmlEncode(fd.OutputNames[0])}</var> = ");
                    else if (fd.OutputNames.Count > 1)
                    {
                        sb.Append("[");
                        for (int i = 0; i < fd.OutputNames.Count; i++)
                        {
                            if (i > 0) sb.Append(", ");
                            sb.Append($"<var>{HttpUtility.HtmlEncode(fd.OutputNames[i])}</var>");
                        }
                        sb.Append("] = ");
                    }
                    sb.Append($"<var>{HttpUtility.HtmlEncode(fd.Name)}</var>(");
                    for (int i = 0; i < fd.ParamNames.Count; i++)
                    {
                        if (i > 0) sb.Append(", ");
                        sb.Append($"<var>{HttpUtility.HtmlEncode(fd.ParamNames[i])}</var>");
                    }
                    sb.Append($") … <b>end</b>  <span style=\"color:#888\">(defined)</span></span>");
                    break;
            }
            return sb.ToString();
        }

        /// <summary>Render únicamente el valor (sin formula). Útil para errores o displays simples.</summary>
        public static string RenderValue(MValue v)
        {
            if (v == null) return "<i>(undefined)</i>";
            if (v.IsCallable) return $"<i style=\"color:#666\">{HttpUtility.HtmlEncode(v.CallableName ?? "@function_handle")}</i>";
            if (v.IsSymbolic) return v.Symbolic.ToHtml();
            // Matriz numerica VACIA ([] o zeros(0,n)): MATLAB imprime "[]"; antes salia la etiqueta vacia
            // o "[0x0 matrix]" dentro de una celda (2026-09-06).
            if (!v.IsString && !v.IsStringArray && !v.IsCell && !v.IsStruct && !v.IsStructArray && !v.IsMap
                && !v.IsCallable && !v.IsSparseReal && !v.IsSymMatrix && !v.IsDecomposition && (v.Rows == 0 || v.Cols == 0))
                return "<span class=\"matrix\">[]</span>";
            if (v.IsStringArray)
            {
                int srA = v.StringArrayData.GetLength(0), scA = v.StringArrayData.GetLength(1);
                var sbStr = new StringBuilder();
                sbStr.Append("<span class=\"matrix\" style=\"color:#0a6e3a\">");
                for (int i = 0; i < srA; i++)
                {
                    sbStr.Append("<span class=\"tr\"><span class=\"td\"></span>");
                    for (int j = 0; j < scA; j++)
                    {
                        sbStr.Append("<span class=\"td\">\"");
                        sbStr.Append(HttpUtility.HtmlEncode(v.StringArrayData[i, j] ?? ""));
                        sbStr.Append("\"</span>");
                    }
                    sbStr.Append("<span class=\"td\"></span></span>");
                }
                sbStr.Append("</span>");
                return sbStr.ToString();
            }
            if (v.IsString)
            {
                if (v.IsDoubleQuoted)
                    // string type ("...") -> MATLAB muestra CON comillas dobles.
                    return "<span style=\"color:#0a6e3a\">\"" + HttpUtility.HtmlEncode(v.StringValue ?? "") + "\"</span>";
                // char array ('...') -> MATLAB 2017a lo muestra SIN comillas (c = hola mundo).
                return HttpUtility.HtmlEncode(v.StringValue ?? "");
            }
            if (v.IsInstance)
            {
                var sbi = new StringBuilder();
                sbi.Append($"<span style=\"font-family:monospace;color:#2050a0\">{HttpUtility.HtmlEncode(v.ClassName)}{{");
                bool firstI = true;
                foreach (var kv in v.Fields)
                {
                    if (!firstI) sbi.Append(", ");
                    sbi.Append($"<var>{HttpUtility.HtmlEncode(kv.Key)}</var>: ");
                    sbi.Append(RenderValueInline(kv.Value));
                    firstI = false;
                }
                sbi.Append("}</span>");
                return sbi.ToString();
            }
            if (v.IsStruct)
            {
                var sbs = new StringBuilder();
                sbs.Append("<span style=\"font-family:monospace;color:#444\">struct{");
                bool first = true;
                foreach (var kv in v.Fields)
                {
                    if (!first) sbs.Append(", ");
                    sbs.Append($"<var>{HttpUtility.HtmlEncode(kv.Key)}</var>: ");
                    sbs.Append(RenderValueInline(kv.Value));
                    first = false;
                }
                sbs.Append("}</span>");
                return sbs.ToString();
            }
            if (v.IsCell)
            {
                int cnr = v.CellData.GetLength(0), cnc = v.CellData.GetLength(1);
                var sbc = new StringBuilder();
                sbc.Append("<span class=\"matrix\" style=\"border-left:2px solid #aaa;border-right:2px solid #aaa;padding:0 .2em\">");
                for (int i = 0; i < cnr; i++)
                {
                    sbc.Append("<span class=\"tr\"><span class=\"td\"></span>");
                    for (int j = 0; j < cnc; j++)
                    {
                        sbc.Append("<span class=\"td\">");
                        sbc.Append(RenderValueInline(v.CellData[i, j]));
                        sbc.Append("</span>");
                    }
                    sbc.Append("<span class=\"td\"></span></span>");
                }
                sbc.Append("</span>");
                return sbc.ToString();
            }
            // Complex scalar tiene prioridad sobre IsScalar real
            if (v.IsComplex && v.Rows == 1 && v.Cols == 1)
                return FormatComplex(v.Data[0], v.Imag[0]);
            if (v.IsScalar)
            {
                var numStr = FormatNumber(v.Scalar);
                // symunit: escalar con unidad → "6 m" con la unidad en VERDE y recta.
                return v.HasUnit ? numStr + " " + UnitToHtml(v.Unit.Text) : numStr;
            }
            if (v.Is3D)
            {
                var sb3 = new StringBuilder();
                int nPages = v.Pages.Length;
                var p0 = v.Pages[0];
                sb3.Append($"<span style=\"font-family:monospace;color:#444\">array3d({p0.Rows}×{p0.Cols}×{nPages}):</span><br>");
                int showPages = System.Math.Min(nPages, 3);
                for (int p = 0; p < showPages; p++)
                {
                    sb3.Append($"<span style=\"color:#888;font-style:italic\">(:,:,{p + 1}) =</span><br>");
                    sb3.Append(RenderValue(v.Pages[p]));
                    sb3.Append("<br>");
                }
                if (nPages > showPages) sb3.Append($"<span style=\"color:#888\">… (+{nPages - showPages} more pages)</span>");
                return sb3.ToString();
            }
            if (v.IsSymMatrix)
            {
                int snr = v.SymCells.GetLength(0), snc = v.SymCells.GetLength(1);
                // Pre-render cada celda (ToHtml = typeset crudo) y mide su largo (ToInfix) para
                // decidir el layout — las expresiones simbolicas largas se amontonan en horizontal.
                var cells = new string[snr, snc];
                int maxLen = 0;
                for (int i = 0; i < snr; i++)
                    for (int j = 0; j < snc; j++)
                    {
                        var cs = (v.SymCells[i, j] ?? new SymConst(0)).Simplify();
                        cells[i, j] = cs.ToHtml();
                        int len = (cs.ToInfix() ?? "").Length;
                        if (len > maxLen) maxLen = len;
                    }
                // Separadores | entre columnas cuando los elementos son EXPRESIONES (multi-termino):
                // en vectores fila Y matrices 2D, para que se distinga donde termina cada elemento.
                // Elementos cortos/atomicos ([0 0 0 0], [1 2 3]) NO llevan separador.
                bool sep = snc > 1 && maxLen > 3;
                var sbSym = new StringBuilder();
                sbSym.Append("<span class=\"matrix sym\" style=\"color:#5d2b8a;font-style:italic\">");   // .sym: el tema oscuro lo aclara (template.html)
                for (int i = 0; i < snr; i++)
                {
                    sbSym.Append("<span class=\"tr\"><span class=\"td\"></span>");
                    for (int j = 0; j < snc; j++)
                    {
                        string tdStyle = sep ? "padding:.12em .7em" : "padding:0 .5em";
                        if (sep && j > 0) tdStyle += ";border-left:1px solid #c9b8dd";
                        sbSym.Append($"<span class=\"td\" style=\"{tdStyle}\">");
                        sbSym.Append(cells[i, j]);
                        sbSym.Append("</span>");
                    }
                    sbSym.Append("<span class=\"td\"></span></span>");
                }
                sbSym.Append("</span>");
                return sbSym.ToString();
            }
            if (v.IsSparseReal)
            {
                int snr = v.Rows, snc = v.Cols;
                int nnz = v.SparseVals.Length;
                // Mapa rápido (i,j)→val para acceso O(log n) en filas/cols pequeños
                double SparseAt(int i, int j)
                {
                    for (int k = v.SparseRowPtr[i]; k < v.SparseRowPtr[i + 1]; k++)
                        if (v.SparseCols[k] == j) return v.SparseVals[k];
                    return 0.0;
                }
                bool HasEntry(int i, int j)
                {
                    for (int k = v.SparseRowPtr[i]; k < v.SparseRowPtr[i + 1]; k++)
                        if (v.SparseCols[k] == j) return true;
                    return false;
                }
                // Tabla bordeada con valores; ceros en gris claro; truncación si N o M > 8
                const int spMax = 8;
                bool truncR = snr > spMax;
                bool truncC = snc > spMax;
                int spShowR = truncR ? spMax : snr;
                int spShowC = truncC ? spMax : snc;
                var sbSp = new StringBuilder();
                // Header: tamaño + nnz + density
                double density = snr * snc > 0 ? (double)nnz / (snr * snc) * 100.0 : 0.0;
                sbSp.Append($"<span style=\"font:italic 11px sans-serif;color:#888\">sparse({snr}×{snc}, nnz={nnz}, density={density.ToString("F2", System.Globalization.CultureInfo.InvariantCulture)}%)</span><br>");
                sbSp.Append("<span class=\"matrix\" style=\"border-left:2px solid #888;border-right:2px solid #888;padding:0 .25em\">");
                for (int i = 0; i < spShowR; i++)
                {
                    int ri = (truncR && i == spShowR - 1) ? snr - 1 : i;
                    sbSp.Append("<span class=\"tr\"><span class=\"td\"></span>");
                    for (int j = 0; j < spShowC; j++)
                    {
                        int cj = (truncC && j == spShowC - 1) ? snc - 1 : j;
                        sbSp.Append("<span class=\"td\">");
                        if (truncR && i == spShowR - 2 && truncC && j == spShowC - 2)
                            sbSp.Append("<span style=\"color:#bbb\">⋱</span>");
                        else if (truncC && j == spShowC - 2)
                            sbSp.Append("<span style=\"color:#bbb\">⋯</span>");
                        else if (truncR && i == spShowR - 2)
                            sbSp.Append("<span style=\"color:#bbb\">⋮</span>");
                        else if (HasEntry(ri, cj))
                            sbSp.Append(FormatNumber(SparseAt(ri, cj)));
                        else
                            sbSp.Append("<span style=\"color:#ddd\">·</span>");
                        sbSp.Append("</span>");
                    }
                    sbSp.Append("<span class=\"td\"></span></span>");
                }
                sbSp.Append("</span>");
                return sbSp.ToString();
            }
            // Matrix display — con truncación tipo MATLAB para matrices grandes
            const int maxCount = 6;
            int nr = v.Rows, nc = v.Cols;
            bool truncRow = nr > maxCount;
            bool truncCol = nc > maxCount;
            var sb = new StringBuilder();
            sb.Append("<span class=\"matrix\">");
            int showR = truncRow ? maxCount : nr;
            int showC = truncCol ? maxCount : nc;
            for (int i = 0; i < showR; i++)
            {
                sb.Append("<span class=\"tr\">");
                int ri = (truncRow && i == showR - 1) ? nr - 1 : i;
                // Bracket izquierdo (celda vacía con border-left por CSS Calcpad)
                sb.Append("<span class=\"td\"></span>");
                for (int j = 0; j < showC; j++)
                {
                    int cj = (truncCol && j == showC - 1) ? nc - 1 : j;
                    sb.Append("<span class=\"td\">");
                    if (truncRow && i == showR - 1 && truncCol && j == showC - 2)
                        sb.Append("⋱");
                    else if (truncCol && j == showC - 2)
                        sb.Append("⋯");
                    else if (truncRow && i == showR - 2)
                        sb.Append("⋮");
                    else if (v.IsComplex)
                    {
                        int idx = ri * v.Cols + cj;
                        sb.Append(FormatComplex(v.Data[idx], v.Imag[idx]));
                    }
                    else
                    {
                        sb.Append(FormatNumber(v.At(ri, cj)));
                        // symunit por elemento: unidad verde tras el número (como MATLAB 10*[kN]).
                        if (v.HasUnitData)
                        {
                            var cu = v.UnitAt(ri * v.Cols + cj);
                            if (cu != null) sb.Append(" " + UnitToHtml(cu.Text));
                        }
                    }
                    sb.Append("</span>");
                }
                // Bracket derecho
                sb.Append("<span class=\"td\"></span>");
                sb.Append("</span>");
            }
            sb.Append("</span>");
            // Etiqueta de tamaño cuando truncado
            if (truncRow || truncCol)
                sb.Append($"<span style=\"font:italic 11px sans-serif;color:#888;margin-left:.5em\">[{nr}×{nc} matrix]</span>");
            return sb.ToString();
        }

        /// <summary>Versión compacta para mostrar dentro de structs (sin grandes matrices).</summary>
        private static string RenderValueInline(MValue v)
        {
            if (v == null) return "(undefined)";
            if (v.IsString)
            {
                if (v.IsDoubleQuoted)
                    return $"\"<span class=\"str\" style=\"color:#0a6e3a\">{HttpUtility.HtmlEncode(v.StringValue ?? "")}</span>\"";
                return $"'<span class=\"str\">{HttpUtility.HtmlEncode(v.StringValue)}</span>'";
            }
            if (v.IsCallable) return HttpUtility.HtmlEncode(v.CallableName ?? "@fn");
            if (v.IsStruct) return $"<i>struct({v.Fields.Count} fields)</i>";
            if (v.IsScalar) return FormatNumber(v.Scalar);
            if (v.Rows == 0 || v.Cols == 0) return "[]";   // elemento vacio de una celda: como MATLAB
            return $"<i>[{v.Rows}×{v.Cols} matrix]</i>";
        }

        private static bool IsTrivialAssignment(Assignment asg)
        {
            // Si RHS es literal puro (número, string, matrix de números), no duplicar valor.
            return IsTrivialLiteral(asg.Rhs);
        }
        /// <summary>True si el RHS es una llamada a funcion simbolica builtin (diff, int,
        /// expand, factor, simplify, solve, taylor, limit, subs, dsolve, laplace, fourier).
        /// En ese caso preferimos mostrar SOLO el resultado simbolico, sin repetir la
        /// llamada de la funcion — comportamiento MATLAB Symbolic Toolbox.</summary>
        private static bool IsSymbolicFunctionCall(MatlabNode n)
        {
            if (n is CallOrIndex c && c.Target is IdentRef id)
            {
                return id.Name is "diff" or "int" or "expand" or "factor"
                    or "simplify" or "solve" or "taylor" or "limit" or "subs"
                    or "dsolve" or "laplace" or "fourier" or "trigsimplify"
                    or "ilaplace" or "ifourier" or "ztrans" or "iztrans"
                    or "collect" or "coeffs" or "symsum" or "symprod"
                    or "jacobian" or "hessian" or "curl" or "divergence" or "laplacian";
            }
            return false;
        }
        /// <summary>Render n-ario indexado estilo Calcpad $Sum/$Product:
        ///   n
        ///   ∑   vᵢ        (n arriba, i=1 abajo, sumando con subíndice i).
        ///  i=1
        /// Estructura EXACTA de FormatNary de Calcpad: dvr[ small(sup) nary(∑) small(sub) ] expr.
        /// Sumando = operando con subíndice i (vᵢ para identificador, (expr)ᵢ para expresión).</summary>
        private static string NaryIndexed(string sym, MatlabNode operand)
        {
            var op = RenderExpression(operand);
            string body = operand is IdentRef
                ? $"{op}<sub><var>i</var></sub>"
                : $"(&hairsp;{op}&hairsp;)<sub><var>i</var></sub>";
            return $"<span class=\"dvr\"><small><var>n</var></small>"
                 + $"<span class=\"nary\">{sym}</span>"
                 + $"<small><var>i</var>&hairsp;=&hairsp;1</small></span>{body}";
        }
        /// <summary>True si el RHS es una llamada a función MATEMÁTICA que RenderCall dibuja
        /// como SÍMBOLO pretty (√, ∑, ∏, ‖·‖, |·|, ⁻¹, ᵀ, ⁿ√, ·, ×). Para éstas SÍ mostramos
        /// `fórmula = valor` (como Calcpad); para indexado/llamadas ordinarias, solo el valor.
        /// Lista alineada 1:1 con las ramas pretty de RenderCall (gated por PrettyMath).</summary>
        private static bool IsPrettyMathCall(MatlabNode n)
        {
            if (n is CallOrIndex c && c.Target is IdentRef id && c.Args != null)
            {
                var nm = id.Name; int k = c.Args.Count;
                // ∫/∬/∭/Δ/∇ numericas: cualquier aridad valida -> siempre formula = valor
                if (nm is "integral" or "integral2" or "integral3" or "trapz" or "gradient"
                    or "quad" or "quadgk" or "quadl" or "quadv"
                    or "dblquad" or "triplequad" or "gaussint" or "gaussint2") return true;
                if (nm == "diff" && k == 1) return true;   // Δv (diff >=2 args = simbolico d/dx)
                if (k == 1)
                    return nm is "sqrt" or "abs" or "norm" or "det" or "inv"
                        or "transpose" or "trace" or "sum" or "prod";
                if (k == 2)
                    return nm is "nthroot" or "dot" or "cross";
            }
            return false;
        }
        /// <summary>True si el nodo es un literal cuyo render visual coincidirá exactamente
        /// con el render del valor evaluado (i.e., no hay nada que "calcular").</summary>
        private static bool IsTrivialLiteral(MatlabNode n)
        {
            if (n is NumberLit || n is StringLit || n is ImaginaryLit) return true;
            if (n is UnaryOp u && u.Op == "-" && u.IsPrefix)
                return IsTrivialLiteral(u.Operand);   // -5 también es literal
            if (n is MatrixLit m)
            {
                // Matriz literal con TODOS elementos literales puros → trivial
                foreach (var row in m.Rows)
                    foreach (var elem in row)
                        if (!IsTrivialLiteral(elem)) return false;
                return true;
            }
            if (n is CellLit cl)
            {
                foreach (var row in cl.Rows)
                    foreach (var elem in row)
                        if (!IsTrivialLiteral(elem)) return false;
                return true;
            }
            return false;
        }

        private static void RenderAssignmentLhs(StringBuilder sb, Assignment asg)
        {
            if (asg.Targets.Count == 1)
            {
                sb.Append(RenderExpression(asg.Targets[0]));
                return;
            }
            sb.Append("[");
            for (int i = 0; i < asg.Targets.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(RenderExpression(asg.Targets[i]));
            }
            sb.Append("]");
        }

        /// <summary>Render una expresión AST como HTML MATLAB.</summary>
        /// <summary>Renderiza una ECUACIÓN dada como string (para #deq y celdas #col):
        /// divide por '=' de nivel superior (soporta a=b=c), parsea cada lado y lo
        /// renderiza como math bonito (fracciones, subíndices, ·, potencias). Si un lado
        /// no parsea, cae a texto. Es display-only — no evalúa nada.</summary>
        public static string RenderEquation(string src)
        {
            if (string.IsNullOrWhiteSpace(src)) return "";
            var parts = SplitTopEquals(src);
            var sb = new System.Text.StringBuilder();
            bool first = true;
            foreach (var p in parts)
            {
                var s = p.Trim();
                if (s.Length == 0) continue;
                if (!first) sb.Append(" = ");
                first = false;
                try
                {
                    var toks = MatlabTokenizer.Tokenize(s);
                    var node = new MatlabParser(toks).ParseExpression();
                    sb.Append(RenderExpression(node));
                }
                catch { sb.Append(System.Net.WebUtility.HtmlEncode(s)); }
            }
            return sb.ToString();
        }

        /// <summary>Divide por '=' de nivel superior (fuera de ()/[]/{}), ignorando
        /// ==, &lt;=, &gt;=, ~=, != y =>.</summary>
        private static System.Collections.Generic.List<string> SplitTopEquals(string s)
        {
            var res = new System.Collections.Generic.List<string>();
            int depth = 0, last = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c is '(' or '[' or '{') depth++;
                else if (c is ')' or ']' or '}') depth--;
                else if (c == '=' && depth == 0)
                {
                    char prev = i > 0 ? s[i - 1] : ' ';
                    char next = i + 1 < s.Length ? s[i + 1] : ' ';
                    if (next == '=' || prev is '=' or '<' or '>' or '~' or '!') continue;
                    res.Add(s[last..i]);
                    last = i + 1;
                }
            }
            res.Add(s[last..]);
            return res;
        }

        public static string RenderExpression(MatlabNode node)
        {
            return node switch
            {
                NumberLit n => FormatNumber(n.Value),
                ImaginaryLit im => im.Value == 1 ? "i" : (im.Value == -1 ? "-i" : FormatNumber(im.Value) + "i"),
                StringLit s => s.Quote == '"'
                    ? $"\"<span class=\"str\" style=\"color:#0a6e3a\">{HttpUtility.HtmlEncode(s.Value)}</span>\""
                    : $"'<span class=\"str\">{HttpUtility.HtmlEncode(s.Value)}</span>'",
                IdentRef id => IsCommonBuiltin(id.Name)
                    ? $"<span style=\"font-family:'Segoe UI',sans-serif;font-weight:600;font-style:normal;color:#7c2bb2\">{HttpUtility.HtmlEncode(id.Name)}</span>"
                    : RenderIdentName(id.Name),
                UnaryOp u => RenderUnary(u),
                BinaryOp b => RenderBinary(b),
                CallOrIndex c => RenderCall(c),
                Range r => RenderRange(r),
                MatrixLit m => RenderMatrixLit(m),
                ColonAll => ":",
                AnonFunction af => RenderAnonFunction(af),
                FieldAccess fa => RenderFieldAccess(fa),
                CellLit cl => RenderCellLit(cl),
                CellIndex ci => RenderCellIndex(ci),
                _ => HttpUtility.HtmlEncode("[" + node?.GetType().Name + "]")
            };
        }
        /// <summary>Render de acceso a campo. Caso symunit: `u.kN`, `u.m`, `u.MPa` (target
        /// identificador simple, campo = nombre de unidad Calcpad) → se muestra SOLO la unidad
        /// en verde, sin el `u.` (como MATLAB muestra [kN]). Es cosmético (no afecta el cálculo).</summary>
        private static string RenderFieldAccess(FieldAccess fa)
        {
            if (fa.Target is IdentRef && Calcpad.Core.Unit.TryGet(fa.FieldName, out _))
                return UnitToHtml(fa.FieldName);
            return RenderExpression(fa.Target) + "." + RenderIdentName(fa.FieldName);
        }
        /// <summary>Renderiza un identificador con underscore como subíndice HTML.
        /// Ej: "a_2" → "a<sub>2</sub>", "M_xx" → "M<sub>xx</sub>", "x_max" → "x<sub>max</sub>".
        /// Múltiples underscore: "sigma_x_max" → "sigma<sub>x,max</sub>" (notación Calcpad).
        /// Si el nombre empieza con _ o es solo _, lo deja literal.</summary>
        public static string RenderIdentName(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            // PRIMA: la maneja DecorateBase via _decos (sufijos prime/pprime/tprime -> ′/″/‴),
            // DESPUES de mapear el griego del base -> asi `Phiprime_1a` sale Φ′₁ₐ (no "Phi′₁ₐ").
            // (Antes habia aqui un `name.Replace("prime","′")` que metia el ′ en el base y rompia
            //  el griego + pisaba pprime/tprime; se quito.)
            // DOBLE guion bajo `a__b` -> FRACCION a/b (valido en MATLAB, se ve como quebrado).
            // Ej: df__dx -> df/dx,  dy__dx -> dy/dx,  d__x -> d/x. Un solo `_` sigue = subindice.
            int dbl = name.IndexOf("__", System.StringComparison.Ordinal);
            if (dbl > 0 && dbl + 2 < name.Length)
            {
                string num = name.Substring(0, dbl);
                string den = name.Substring(dbl + 2);
                // Estructura IDENTICA a FormatDivision de Calcpad: dvc[ num · dvl · den ]
                // (num/den como hijos DIRECTOS, sin wrappers). CSS .dvc/.dvl = byte-identico.
                return $"<span class=\"dvc\">{RenderIdentName(num)}<span class=\"dvl\"></span>{RenderIdentName(den)}</span>";
            }
            int idx = name.IndexOf('_');
            if (idx <= 0 || idx == name.Length - 1)
                return $"<var>{DecorateBase(name)}</var>";
            string baseName = name.Substring(0, idx);
            string sub = name.Substring(idx + 1).Replace("_", ",");
            return $"<var>{DecorateBase(baseName)}<sub>{Superscriptify(HttpUtility.HtmlEncode(sub))}</sub></var>";
        }
        /// <summary>Decora la BASE de un identificador con tokens tipo LaTeX (todos nombres MATLAB
        /// validos): sqrt/raiz → √ , bar → x̄ , hat → x̂ , tilde → x̃ , dot → ẋ , ddot → ẍ ,
        /// vec → x⃗ , deg → x° , sup+digitos → superindice , y letras griegas (alpha→α…).
        /// Recursivo para combinar (p.ej. sqrt de algo, o vbar). Sobre texto HTML-encodeado.</summary>
        // Acento centrado ARRIBA del contenido (robusto con cursiva; no depende de que la fuente
        // tenga el carácter combinante). Ver bar (overline aparte).
        // Acento centrado ARRIBA. El translateX(.09em) compensa la itálica de <var> (la cima de
        // la letra queda a la derecha del centro de la caja → sin esto el acento sale corrido a la izq).
        private static string Over(string acc, string inner) => Over(acc, inner, "-.22em");
        // `top` por-glifo: caron/breve/tilde se dibujan mas arriba en su caja -> top menos negativo
        // (mas abajo); ^, punto, flecha usan el default. Asi TODOS quedan pegados a la letra.
        private static string Over(string acc, string inner, string top) =>
            $"<span style=\"display:inline-block;position:relative;text-align:center;\">{inner}"
          + $"<span style=\"position:absolute;left:0;right:0;top:{top};font-size:.72em;font-style:normal;font-weight:400;line-height:1;transform:translateX(.09em);\">{acc}</span></span>";
        // token de decoracion-sufijo -> función que envuelve el inner ya renderizado.
        private static readonly (string tok, System.Func<string, string> wrap)[] _decos =
        {
            // PRIMA como sufijo (nombre MATLAB-valido): Phiprime_1a -> Φ′₁ₐ, Phipprime -> Φ″, Phitprime -> Φ‴.
            // Mas especificos primero (tprime/pprime antes que prime), igual que ddot antes de dot.
            ("tprime", inner => inner + "<span style=\"font-style:normal\">&#8244;</span>"), // ‴ triple prima
            ("pprime", inner => inner + "<span style=\"font-style:normal\">&#8243;</span>"), // ″ doble prima
            ("prime",  inner => inner + "<span style=\"font-style:normal\">&#8242;</span>"), // ′ prima
            ("ddot", inner => Over("&#183;&#183;", inner, "-.30em")), // ẍ  (doble punto, glifo bajo -> subir)
            ("dot",  inner => Over("&#183;", inner, "-.30em")),        // ẋ  (punto, glifo bajo -> subir)
            ("hat",  inner => Over("^", inner, "-.14em")),             // x̂
            ("check",inner => Over("&#711;", inner, "-.02em")),        // x̌  (caron, glifo alto -> bajar)
            ("breve",inner => Over("&#728;", inner, "-.02em")),        // x̆  (breve, glifo alto -> bajar)
            ("tilde",inner => Over("~", inner, "-.10em")),             // x̃
            ("vec",  inner => Over("&#8594;", inner, "-.18em")),        // x⃗  (flecha → arriba)
            ("bar",  inner => $"<span style=\"display:inline-block;border-top:.08em solid currentColor;line-height:1.05;padding:.02em .06em 0 .02em;\">{inner}</span>"), // x̄ overline
        };
        private static string DecorateBase(string b)
        {
            if (string.IsNullOrEmpty(b)) return "";
            // Prefijo raiz: sqrtD / raizD → √D (con vinculum, como sqrt()).
            if (b.Length > 4 && (b.StartsWith("sqrt") || b.StartsWith("raiz")))
                return $"&ensp;&hairsp;<span class=\"o0\"><span class=\"r\">√</span>&hairsp;{DecorateBase(b.Substring(4))}</span>";
            // Valor absoluto / norma / angulo (prefijos que envuelven): absF→|F|, normv→‖v‖, angleAB→∠AB.
            if (b.Length > 4 && b.StartsWith("norm"))
                return $"<b class=\"b0\">‖</b>&hairsp;{DecorateBase(b.Substring(4))}&hairsp;<b class=\"b0\">‖</b>";
            if (b.Length > 5 && b.StartsWith("angle"))
                return $"∠&hairsp;{DecorateBase(b.Substring(5))}";
            if (b.Length > 3 && b.StartsWith("abs"))
                return $"<b class=\"b0\">|</b>&hairsp;{DecorateBase(b.Substring(3))}&hairsp;<b class=\"b0\">|</b>";
            // Grados como sufijo: Tdeg → T°.
            if (b.Length > 3 && b.EndsWith("deg"))
                return DecorateBase(b.Substring(0, b.Length - 3)) + "&deg;";
            // Sufijos de decoracion (van sobre el ultimo caracter/base): bar/hat/tilde/dot/ddot/vec.
            foreach (var (tok, wrap) in _decos)
                if (b.Length > tok.Length && b.EndsWith(tok))
                    return wrap(DecorateBase(b.Substring(0, b.Length - tok.Length)));
            // Base simple: griega o texto con superindice (sup).
            string greek = GreekAutoRender ? GreekLetterMap(b) : null;
            return greek ?? Superscriptify(HttpUtility.HtmlEncode(b));
        }
        /// <summary>SUPERINDICE con el token `sup` (valido en MATLAB, ya que `^` no lo es en
        /// nombres — verificado en MATLAB 2017a). `sup` + digitos -> esos digitos en superindice.
        /// Ej: xsup2 -> x² , dsup2f -> d²f , dxsup2 -> dx². Combinable con `_` (sub) y `__` (frac):
        /// dsup2f__dxsup2 -> d²f/dx². Se aplica sobre texto YA HTML-encodeado.</summary>
        private static string Superscriptify(string enc) =>
            string.IsNullOrEmpty(enc) ? enc
            : System.Text.RegularExpressions.Regex.Replace(enc, "sup([0-9]+)", "<sup>$1</sup>");
        /// <summary>Transliteración de nombres griegos → símbolo Unicode en el OUTPUT.
        /// Por defecto ACTIVADA (los nombres ASCII `nu`, `phi`, `xi`… se muestran ν, φ, ξ,
        /// manteniendo el código MATLAB-válido). Se apaga/enciende por bloque con las
        /// directivas de comentario <c>% #nogreek</c> … <c>% #greek</c>. Estado por-corrida:
        /// el pipeline lo restaura a true al iniciar cada ejecución.</summary>
        public static bool GreekAutoRender = true;
        /// <summary>Transliteración pública para expresiones simbólicas (#noc/#val/#equ):
        /// reemplaza los identificadores que son nombres de letra griega por su símbolo
        /// Unicode, respetando límites de palabra (no toca `phin` ni `nu_x` parcialmente).
        /// Devuelve la cadena intacta si GreekAutoRender está OFF.</summary>
        public static string TransliterateGreek(string expr)
        {
            if (!GreekAutoRender || string.IsNullOrEmpty(expr)) return expr;
            return System.Text.RegularExpressions.Regex.Replace(
                expr, "[A-Za-z]+",
                m => GreekLetterMap(m.Value) ?? m.Value);
        }
        /// <summary>¿Este identificador merece typeset de variable (itálica/subíndice/griega)
        /// dentro de una cadena de fprintf/disp? Sí si es letra griega, si tiene subíndice
        /// (underscore interno), o si es de una sola letra (E, t, q, x…). Las palabras
        /// latinas de varias letras (grados, plano) se dejan como PROSA.</summary>
        public static bool IsRenderableIdent(string name)
        {
            if (string.IsNullOrEmpty(name)) return false;
            int idx = name.IndexOf('_');
            if (idx > 0 && idx < name.Length - 1) return true;              // subíndice a_b
            string bas = idx > 0 ? name.Substring(0, idx) : name;
            if (GreekLetterMap(bas) != null) return true;                   // griega
            return bas.Length == 1 && char.IsLetter(bas[0]);                // 1 letra
        }
        /// <summary>Formatea el texto de una unidad Calcpad (symunit) como HTML verde y recto:
        /// `*`→`·`, `^N`→superíndice. Ej "kN/m^2" → &lt;i class="unit"&gt;kN/m&lt;sup&gt;2&lt;/sup&gt;&lt;/i&gt;.</summary>
        private static string UnitToHtml(string unitText)
        {
            if (string.IsNullOrEmpty(unitText)) return "";
            var u = HttpUtility.HtmlEncode(unitText);
            // Markup IDÉNTICO a Calcpad dentro de %: <i> plano (→ .eq i, verde #086, recto,
            // SIN vertical-align) y <sup class="unit"> para exponentes. Antes usaba
            // <i class="unit"> que lleva vertical-align:-1pt → se veía una posición más abajo.
            u = System.Text.RegularExpressions.Regex.Replace(u, @"\^(-?\d+)", "<sup class=\"unit\">$1</sup>");
            u = u.Replace("*", "·");
            return "<i>" + u + "</i>";
        }
        /// <summary>Mapea nombres de letras griegas a su unicode. Null si no es griega.</summary>
        private static string GreekLetterMap(string name) => name switch
        {
            "alpha" => "α", "beta" => "β", "gamma" => "γ", "delta" => "δ",
            "epsilon" => "ε", "zeta" => "ζ", "eta" => "η", "theta" => "θ",
            "iota" => "ι", "kappa" => "κ", "lambda" => "λ", "mu" => "μ",
            "nu" => "ν", "xi" => "ξ", "omicron" => "ο", "pi" => "π",
            "rho" => "ρ", "sigma" => "σ", "tau" => "τ", "upsilon" => "υ",
            "phi" => "φ", "chi" => "χ", "psi" => "ψ", "omega" => "ω",
            "Alpha" => "Α", "Beta" => "Β", "Gamma" => "Γ", "Delta" => "Δ",
            "Theta" => "Θ", "Lambda" => "Λ", "Pi" => "Π", "Sigma" => "Σ",
            "Phi" => "Φ", "Psi" => "Ψ", "Omega" => "Ω",
            _ => null
        };
        private static string RenderCellLit(CellLit cl)
        {
            var sb = new StringBuilder("{");
            for (int i = 0; i < cl.Rows.Count; i++)
            {
                if (i > 0) sb.Append("; ");
                for (int j = 0; j < cl.Rows[i].Count; j++)
                {
                    if (j > 0) sb.Append(", ");
                    sb.Append(RenderExpression(cl.Rows[i][j]));
                }
            }
            sb.Append("}");
            return sb.ToString();
        }
        private static string RenderCellIndex(CellIndex ci)
        {
            var sb = new StringBuilder(RenderExpression(ci.Target));
            sb.Append("{");
            for (int i = 0; i < ci.Args.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(RenderExpression(ci.Args[i]));
            }
            sb.Append("}");
            return sb.ToString();
        }
        private static string RenderAnonFunction(AnonFunction af)
        {
            // @name (function handle por nombre) — caso especial: 1 param "__handle__" + body IdentRef
            if (af.ParamNames.Count == 1 && af.ParamNames[0] == "__handle__" && af.Body is IdentRef nameRef)
                return "@" + HttpUtility.HtmlEncode(nameRef.Name);
            var sb = new StringBuilder("@(");
            for (int i = 0; i < af.ParamNames.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append($"<var>{HttpUtility.HtmlEncode(af.ParamNames[i])}</var>");
            }
            sb.Append(") ");
            sb.Append(RenderExpression(af.Body));
            return sb.ToString();
        }
        private static string RenderUnary(UnaryOp u)
        {
            if (u.IsPrefix)
                return u.Op + RenderExpression(u.Operand);
            // postfix transpose ' o .' → superíndice T (notación matematica Aᵀ, no A')
            if (PrettyMath && (u.Op == "'" || u.Op == ".'"))
                return RenderExpression(u.Operand) + "<sup>T</sup>";
            return RenderExpression(u.Operand) + u.Op;
        }
        /// <summary>Activar/desactivar rendering pretty-print (fracciones, raíces, exponentes).</summary>
        public static bool PrettyMath = true;

        /// <summary>Envuelve `inner` en paréntesis. Si el contenido es ALTO (contiene una
        /// fracción vertical .dvc), los paréntesis se ESCALAN verticalmente para envolver toda
        /// la fracción (como en un libro), en vez de quedar chiquitos al lado.</summary>
        private static string ParenWrap(string inner)
        {
            if (!inner.Contains("class=\"dvc\"")) return "(" + inner + ")";
            static string P(string ch) =>
                $"<span style=\"display:inline-block;transform:scaleY(1.7);vertical-align:middle;font-weight:400\">{ch}</span>";
            return P("(") + inner + P(")");
        }

        /// <summary>Barras verticales (|·| det/abs, ‖·‖ norma) que ESCALAN a la altura del
        /// contenido. Si el contenido es una MATRIZ o FRACCIÓN (alto), usa barras de borde
        /// en un flex-container que se estira a su altura (sin contar filas); si es un
        /// escalar, usa el glifo simple. bars=1 → |·|, bars=2 → ‖·‖.</summary>
        private static string ScaleBars(string inner, int bars)
        {
            bool tall = inner.Contains("class=\"matrix\"") || inner.Contains("class=\"dvc\"");
            if (!tall)
            {
                string g = bars == 2 ? "‖" : "|";
                return $"<b class=\"b0\">{g}</b>&hairsp;{inner}&hairsp;<b class=\"b0\">{g}</b>";
            }
            string barCss = bars == 2
                ? "border-left:1.4px solid currentColor;border-right:1.4px solid currentColor;width:3px"
                : "border-left:1.4px solid currentColor;width:1px";
            string L = $"<span style=\"align-self:stretch;{barCss};margin-right:3px\"></span>";
            string R = $"<span style=\"align-self:stretch;{barCss};margin-left:3px\"></span>";
            return $"<span style=\"display:inline-flex;align-items:stretch;vertical-align:middle\">{L}"
                 + $"<span style=\"display:flex;align-items:center\">{inner}</span>{R}</span>";
        }

        private static string RenderBinary(BinaryOp b)
        {
            // Pretty-print: a/b → fracción vertical; a^b → superíndice; sqrt → raíz
            if (PrettyMath)
            {
                if (b.Op == "/" || b.Op == "./")
                {
                    var num = RenderExpression(b.Left);
                    var den = RenderExpression(b.Right);
                    // Estructura IDENTICA a FormatDivision de Calcpad: dvc[ num · dvl · den ].
                    return $"<span class=\"dvc\">{num}<span class=\"dvl\"></span>{den}</span>";
                }
                if (b.Op == "^" || b.Op == ".^")
                {
                    string baseStr;
                    if (b.Left is BinaryOp || (b.Left is UnaryOp u_l && u_l.IsPrefix))
                        baseStr = ParenWrap(RenderExpression(b.Left));
                    else baseStr = RenderExpression(b.Left);
                    var expStr = RenderExpression(b.Right);
                    return $"{baseStr}<sup>{expStr}</sup>";
                }
                if (b.Op == "\\" || b.Op == ".\\")
                {
                    // División por la izquierda A\b = resolver A·x = b  →  se muestra como A⁻¹·b
                    // (expresión matemática del sistema de varias incógnitas, no "A/b" ni "A\b").
                    string A = b.Left is BinaryOp ? ParenWrap(RenderExpression(b.Left)) : RenderExpression(b.Left);
                    string rhs = b.Right is BinaryOp ? ParenWrap(RenderExpression(b.Right)) : RenderExpression(b.Right);
                    return $"{A}<sup>&minus;1</sup>&middot;{rhs}";
                }
            }
            // Default: estilo plano con paréntesis por precedencia
            int myPrec = OpPrecedence(b.Op);
            string Render(MatlabNode child, bool rightAssoc)
            {
                if (child is BinaryOp cb)
                {
                    int childPrec = OpPrecedence(cb.Op);
                    bool needParens = childPrec < myPrec ||
                                       (childPrec == myPrec && rightAssoc && b.Op != "^" && b.Op != ".^");
                    var s = RenderExpression(cb);
                    return needParens ? ParenWrap(s) : s;
                }
                if (child is UnaryOp u && u.IsPrefix && u.Op == "-" && (b.Op == "^" || b.Op == ".^"))
                    return ParenWrap(RenderExpression(u));
                return RenderExpression(child);
            }
            var l = Render(b.Left, rightAssoc: false);
            var r = Render(b.Right, rightAssoc: true);
            // Render bonito: * y .* como middle dot (Calcpad style)
            string opRender = b.Op switch
            {
                "*"  => "&middot;",
                ".*" => "&middot;",
                _    => b.Op,
            };
            string sep = (b.Op == "*" || b.Op == ".*" || b.Op == "/" || b.Op == "^") ? "" : " ";
            return $"{l}{sep}{opRender}{sep}{r}";
        }
        /// <summary>Precedencia MATLAB para rendering — más alto = más fuerte.</summary>
        private static int OpPrecedence(string op) => op switch
        {
            "||" or "|" => 1,
            "&&" or "&" => 2,
            "==" or "~=" or "<" or ">" or "<=" or ">=" => 3,
            "+" or "-" => 4,
            "*" or "/" or "\\" or ".*" or "./" or ".\\" => 5,
            "^" or ".^" => 6,
            _ => 10
        };
        /// <summary>Recolecta los nombres de VARIABLES distintas en una expresion (para decidir
        /// derivada parcial ∂ vs ordinaria d). El Target de una llamada f(...) es funcion, NO
        /// variable (no se cuenta); sus args SÍ. Asi f(x,y)→{x,y}, x^2*y→{x,y}, f(x)→{x}.</summary>
        private static void CollectVars(MatlabNode n, System.Collections.Generic.HashSet<string> vars)
        {
            switch (n)
            {
                case IdentRef id: vars.Add(id.Name); break;
                case BinaryOp b: CollectVars(b.Left, vars); CollectVars(b.Right, vars); break;
                case UnaryOp u: CollectVars(u.Operand, vars); break;
                case CallOrIndex c:
                    // Target = nombre de funcion (no variable). Solo los argumentos son variables.
                    if (c.Args != null) foreach (var a in c.Args) CollectVars(a, vars);
                    break;
                case Range r:
                    if (r.Start != null) CollectVars(r.Start, vars);
                    if (r.End != null) CollectVars(r.End, vars);
                    break;
                case MatrixLit m:
                    foreach (var row in m.Rows) foreach (var e in row) CollectVars(e, vars);
                    break;
                // NumberLit / StringLit / etc. -> no aportan variables.
            }
        }
        /// <summary>Clona la expresion sustituyendo el identificador `name` por `repl` (para
        /// expandir progresiones: symsum(k^2,k,1,n) -> k→1, k→2, … -> 1²+2²+…). Solo los nodos
        /// que aparecen en un sumando tipico.</summary>
        private static MatlabNode SubstIdent(MatlabNode n, string name, MatlabNode repl)
        {
            switch (n)
            {
                case IdentRef id: return id.Name == name ? repl : id;
                case BinaryOp b: return new BinaryOp { Op = b.Op, Left = SubstIdent(b.Left, name, repl), Right = SubstIdent(b.Right, name, repl) };
                case UnaryOp u: return new UnaryOp { Op = u.Op, IsPrefix = u.IsPrefix, Operand = SubstIdent(u.Operand, name, repl) };
                case CallOrIndex c2:
                    var na = new System.Collections.Generic.List<MatlabNode>();
                    if (c2.Args != null) foreach (var a in c2.Args) na.Add(SubstIdent(a, name, repl));
                    return new CallOrIndex { Target = c2.Target, Args = na };
                default: return n;
            }
        }
        private static string RenderCall(CallOrIndex c)
        {
            // ── Notacion matematica nativa Calcpad para funciones simbolicas ──
            // diff(f, x)     -> d/dx · f      (Leibniz fraction)
            // diff(f, x, n)  -> d^n/dx^n · f
            // int(f, x)      -> ∫ f dx        (nary symbol)
            // int(f, x, a, b)-> ∫_a^b f dx
            // limit(f, x, c) -> lim_{x→c} f
            if (PrettyMath && c.Target is IdentRef symFn)
            {
                string fname = symFn.Name;
                // diff
                if (fname == "diff" && c.Args.Count >= 2)
                {
                    var fExpr = RenderExpression(c.Args[0]);
                    var vExpr = RenderExpression(c.Args[1]);
                    // PARCIAL vs ORDINARIA: si la funcion depende de >=2 variables, la derivada
                    // es PARCIAL -> se dibuja con ∂ (curly d) en vez de d recta. Se cuentan las
                    // variables distintas en el argumento f: diff(f(x,y),x) o diff(x^2*y,x) -> ∂;
                    // diff(f(x),x) o diff(x^3+2x,x) -> d.
                    var vars = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal);
                    CollectVars(c.Args[0], vars);
                    string dd = vars.Count >= 2 ? "∂" : "d";
                    string num, den;
                    if (c.Args.Count >= 3 && c.Args[2] is NumberLit nlit && nlit.Value >= 2)
                    {
                        int n = (int)nlit.Value;
                        num = $"{dd}<sup>{n}</sup>";
                        den = $"{dd}{vExpr}<sup>{n}</sup>";
                    }
                    else { num = dd; den = $"{dd}{vExpr}"; }
                    return $"<span class=\"dvc\"><span class=\"dvc-num\">{num}</span><span class=\"dvl\"></span><span class=\"dvc-den\">{den}</span></span>&thinsp;{fExpr}";
                }
                // jacobian(f, v) -> ∂f/∂v  (matriz Jacobiana como notación de cálculo, NO
                // el nombre "jacobian" en texto plano). Igual mecánica que diff pero con ∂ y
                // la lista de variables compacta:  ∂/∂(x, y) f.
                if (fname == "jacobian" && c.Args.Count >= 2)
                {
                    var fExpr = RenderExpression(c.Args[0]);
                    var vExpr = RenderVarListCompact(c.Args[1]);
                    return $"<span class=\"dvc\"><span class=\"dvc-num\">∂</span><span class=\"dvl\"></span><span class=\"dvc-den\">∂{vExpr}</span></span>&thinsp;{fExpr}";
                }
                // hessian(f, v) -> ∂²f/∂v²  (matriz Hessiana). Segundas derivadas.
                if (fname == "hessian" && c.Args.Count >= 2)
                {
                    var fExpr = RenderExpression(c.Args[0]);
                    var vExpr = RenderVarListCompact(c.Args[1]);
                    return $"<span class=\"dvc\"><span class=\"dvc-num\">∂<sup>2</sup></span><span class=\"dvl\"></span><span class=\"dvc-den\">∂{vExpr}<sup>2</sup></span></span>&thinsp;{fExpr}";
                }
                // curl(F) -> ∇×F ,  divergence(F) -> ∇·F ,  laplacian(f) -> ∇²f  (operadores
                // vectoriales como notación, no el nombre en texto).
                if ((fname == "curl" || fname == "divergence" || fname == "laplacian") && c.Args.Count >= 1)
                {
                    var fExpr = RenderExpression(c.Args[0]);
                    string nabla = "<span style=\"font-family:'Cambria Math',serif;font-style:normal\">∇</span>";
                    string op = fname == "curl" ? nabla + "&hairsp;×&hairsp;"
                              : fname == "divergence" ? nabla + "&hairsp;·&hairsp;"
                              : nabla + "<sup>2</sup>&hairsp;";
                    return $"{op}{fExpr}";
                }
                // int (indefinida o definida) — formato identico al HtmWriter de Calcpad:
                //   <span class="dvr"><small>SUP</small><span class="nary">∫</span><small>SUB</small></span>
                //   {f}&thinsp;<var>d{x}</var>
                // El diferencial va dentro de <var>...</var> para que se rendea italica como
                // las demas variables (Georgia Pro italic via .eq var en template.html).
                // symsum(f, k, a, b) -> ∑_{k=a}^{b} f  (suma simbolica con limites, IDENTICO a
                // $Sum de Calcpad: FormatNary(∑, sub="k=a", sup="b", expr=f)). symprod -> ∏.
                if ((fname == "symsum" || fname == "symprod") && c.Args.Count >= 4)
                {
                    var sym = fname == "symprod" ? "∏" : "∑";
                    var body = RenderExpression(c.Args[0]);
                    var kk = RenderExpression(c.Args[1]);
                    var lo = RenderExpression(c.Args[2]);
                    var hi = RenderExpression(c.Args[3]);
                    var nary = $"<span class=\"dvr\"><small>{hi}</small><span class=\"nary\">{sym}</span>"
                             + $"<small>{kk}=&hairsp;{lo}</small></span>{body}";
                    // PROGRESION expandida: si el limite inferior es literal, mostrar
                    //   f(a) + f(a+1) + f(a+2) + … + f(b)   (o · para productos). Ej:
                    //   symsum(k,k,1,n) -> 1 + 2 + 3 + … + n ;  k^2 -> 1² + 2² + 3² + … + n²
                    if (c.Args[1] is IdentRef kv && c.Args[2] is NumberLit a0 && a0.Value == System.Math.Floor(a0.Value))
                    {
                        int a = (int)a0.Value;
                        string op = fname == "symprod" ? " &middot; " : " + ";
                        var sbp = new System.Text.StringBuilder();
                        for (int j = a; j < a + 3; j++)
                        {
                            if (j > a) sbp.Append(op);
                            sbp.Append(RenderExpression(SubstIdent(c.Args[0], kv.Name,
                                new NumberLit { Value = j, OrigText = j.ToString(System.Globalization.CultureInfo.InvariantCulture) })));
                        }
                        sbp.Append(op).Append("&hellip;").Append(op);
                        sbp.Append(RenderExpression(SubstIdent(c.Args[0], kv.Name, c.Args[3])));
                        return $"{nary} = {sbp}";
                    }
                    return nary;
                }
                if (fname == "int" && c.Args.Count >= 1)
                {
                    // int(int(f,...),...) anidada (integral DOBLE/triple) → UN solo glifo
                    // ∬/∭ con los limites combinados (1,1 / 0,0), identico a integral2.
                    // Antes se emitian dos .dvr separados con espaciado &emsp;/&nbsp; de
                    // Calcpad -> parecia el estilo CSS viejo en vez del nuevo del tema.
                    (string v, string sub, string sup) ParseIntParts(CallOrIndex ic)
                    {
                        string vv = "x", ss = "", pp = "";
                        if (ic.Args.Count >= 2)
                        {
                            if (ic.Args[1] is IdentRef vId)
                            {
                                vv = GreekLetterMap(vId.Name) ?? System.Web.HttpUtility.HtmlEncode(vId.Name);
                                if (ic.Args.Count >= 4) { ss = RenderExpression(ic.Args[2]); pp = RenderExpression(ic.Args[3]); }
                            }
                            else if (ic.Args.Count >= 3) { ss = RenderExpression(ic.Args[1]); pp = RenderExpression(ic.Args[2]); }
                            else vv = RenderExpression(ic.Args[1]);
                        }
                        return (vv, ss, pp);
                    }
                    var ints2 = new System.Collections.Generic.List<(string v, string sub, string sup)>();
                    CallOrIndex cur = c;
                    while (cur.Args[0] is CallOrIndex nxt && nxt.Target is IdentRef nf && nf.Name == "int")
                    {
                        ints2.Add(ParseIntParts(cur));
                        cur = nxt;
                    }
                    if (ints2.Count > 0)
                    {
                        ints2.Add(ParseIntParts(cur));
                        ints2.Reverse(); // [mas interno (dx) … mas externo (dy)]
                        var body = RenderExpression(cur.Args[0]);
                        var sups = new System.Collections.Generic.List<string>();
                        var subs = new System.Collections.Generic.List<string>();
                        var dvs = new System.Collections.Generic.List<string>();
                        foreach (var p in ints2)
                        {
                            if (!string.IsNullOrEmpty(p.sup)) sups.Add(p.sup);
                            if (!string.IsNullOrEmpty(p.sub)) subs.Add(p.sub);
                            dvs.Add("d<var>" + p.v + "</var>");
                        }
                        string glyph = ints2.Count == 2 ? "∬" : "∭";
                        string dsup = sups.Count > 0 ? "&emsp; " + string.Join(",&nbsp;", sups) : "";
                        string dsub = subs.Count > 0 ? string.Join(",&nbsp;", subs) : "";
                        return $"<span class=\"dvr\"><small>{dsup}</small><span class=\"nary\"><em>{glyph}</em></span><small>{dsub}</small></span>{body}&thinsp;{string.Join("&thinsp;", dvs)}";
                    }
                    // int simple (una sola integral) — comportamiento original.
                    var fExpr = RenderExpression(c.Args[0]);
                    // Diferencial crudo (NO <var> wrapper, sino quedaria anidado). El 2o arg
                    // es la VARIABLE solo si es identificador; si es numero/expr son limites
                    // (int(f,a,b)) y la variable es la de f (por defecto x). int(f) [1 arg] =
                    // indefinida, variable por defecto x → tambien usa el ∫ pretty (antes texto).
                    string vName = "x", sup = "", sub = "";
                    if (c.Args.Count >= 2)
                    {
                        if (c.Args[1] is IdentRef vId)
                        {
                            vName = GreekLetterMap(vId.Name) ?? System.Web.HttpUtility.HtmlEncode(vId.Name);
                            if (c.Args.Count >= 4) { sub = RenderExpression(c.Args[2]); sup = RenderExpression(c.Args[3]); }
                        }
                        else if (c.Args.Count >= 3)
                        {
                            sub = RenderExpression(c.Args[1]);
                            sup = RenderExpression(c.Args[2]);
                        }
                        else vName = RenderExpression(c.Args[1]);
                    }
                    // Espaciado IDENTICO a FormatNary de Calcpad: &emsp; antes del sup, &nbsp;
                    // tras el sub -> empujan los limites en diagonal alrededor del ∫ inclinado.
                    if (!string.IsNullOrEmpty(sup)) sup = "&emsp; " + sup;
                    if (!string.IsNullOrEmpty(sub)) sub = sub + "&nbsp;";
                    return $"<span class=\"dvr\"><small>{sup}</small><span class=\"nary\"><em>∫</em></span><small>{sub}</small></span>{fExpr} d<var>{vName}</var>";
                }
                // integral(fun, a, b) / quad(fun,a,b) / quadgk / quadl / quadv /
                // gaussint(fun, a, b[, n]) NUMERICAS 1D -> ∫_a^b fun dx. IDENTICO a
                // $Integral/$Area de Calcpad: FormatNary("<em>∫</em>", sub=a+"&nbsp;",
                // sup="&emsp; "+b, f+" d"+x). El &emsp;/&nbsp; empujan los limites en
                // diagonal alrededor del ∫ inclinado (.nary em rota el glifo). Si fun es un
                // handle @(t)..., el integrando es su CUERPO y la variable es su parametro
                // (no "@(t) ..." crudo).
                if (fname is "integral" or "quad" or "quadgk" or "quadl" or "quadv" or "gaussint"
                    && c.Args.Count >= 1)
                {
                    string integrand, dvar = "x";
                    if (c.Args[0] is AnonFunction iaf && iaf.ParamNames.Count >= 1)
                    {
                        integrand = RenderExpression(iaf.Body);
                        dvar = GreekLetterMap(iaf.ParamNames[0]) ?? System.Web.HttpUtility.HtmlEncode(iaf.ParamNames[0]);
                    }
                    else integrand = RenderExpression(c.Args[0]);
                    string isub = c.Args.Count >= 2 ? RenderExpression(c.Args[1]) + "&nbsp;" : "";
                    string isup = c.Args.Count >= 3 ? "&emsp; " + RenderExpression(c.Args[2]) : "";
                    return $"<span class=\"dvr\"><small>{isup}</small><span class=\"nary\"><em>∫</em></span><small>{isub}</small></span>{integrand} d<var>{dvar}</var>";
                }
                // integral2(fun, xmin, xmax, ymin, ymax) / dblquad / gaussint2(fun, a, b, c, d[, n])
                // NUMERICAS 2D -> ∬_xmin,ymin ^xmax,ymax fun dx dy. Misma estructura dvr/nary,
                // con el glifo ∬ (U+222C) y los limites x e y en diagonal (como la 1D).
                if (fname is "integral2" or "dblquad" or "gaussint2"
                    && c.Args.Count >= 5)
                {
                    string integrand, ddx = "x", ddy = "y";
                    if (c.Args[0] is AnonFunction iaf2 && iaf2.ParamNames.Count >= 2)
                    {
                        integrand = RenderExpression(iaf2.Body);
                        ddx = GreekLetterMap(iaf2.ParamNames[0]) ?? System.Web.HttpUtility.HtmlEncode(iaf2.ParamNames[0]);
                        ddy = GreekLetterMap(iaf2.ParamNames[1]) ?? System.Web.HttpUtility.HtmlEncode(iaf2.ParamNames[1]);
                    }
                    else integrand = RenderExpression(c.Args[0]);
                    string isub2 = RenderExpression(c.Args[1]) + ",&nbsp;" + RenderExpression(c.Args[3]);
                    string isup2 = "&emsp; " + RenderExpression(c.Args[2]) + ",&nbsp;" + RenderExpression(c.Args[4]);
                    return $"<span class=\"dvr\"><small>{isup2}</small><span class=\"nary\"><em>∬</em></span><small>{isub2}</small></span>{integrand}&thinsp;d<var>{ddx}</var>&thinsp;d<var>{ddy}</var>";
                }
                // integral3(fun, xmin, xmax, ymin, ymax, zmin, zmax) / triplequad ->
                // ∭_... ^... fun dx dy dz  (glifo U+222D).
                if (fname is "integral3" or "triplequad"
                    && c.Args.Count >= 7)
                {
                    string integrand, tdx = "x", tdy = "y", tdz = "z";
                    if (c.Args[0] is AnonFunction iaf3 && iaf3.ParamNames.Count >= 3)
                    {
                        integrand = RenderExpression(iaf3.Body);
                        tdx = GreekLetterMap(iaf3.ParamNames[0]) ?? System.Web.HttpUtility.HtmlEncode(iaf3.ParamNames[0]);
                        tdy = GreekLetterMap(iaf3.ParamNames[1]) ?? System.Web.HttpUtility.HtmlEncode(iaf3.ParamNames[1]);
                        tdz = GreekLetterMap(iaf3.ParamNames[2]) ?? System.Web.HttpUtility.HtmlEncode(iaf3.ParamNames[2]);
                    }
                    else integrand = RenderExpression(c.Args[0]);
                    string isub3 = RenderExpression(c.Args[1]) + ",&nbsp;" + RenderExpression(c.Args[3]) + ",&nbsp;" + RenderExpression(c.Args[5]);
                    string isup3 = "&emsp; " + RenderExpression(c.Args[2]) + ",&nbsp;" + RenderExpression(c.Args[4]) + ",&nbsp;" + RenderExpression(c.Args[6]);
                    return $"<span class=\"dvr\"><small>{isup3}</small><span class=\"nary\"><em>∭</em></span><small>{isub3}</small></span>{integrand}&thinsp;d<var>{tdx}</var>&thinsp;d<var>{tdy}</var>&thinsp;d<var>{tdz}</var>";
                }
                // trapz(y) o trapz(x,y) -> ∫ y dx (trapezoidal, sin limites explicitos). El
                // operando integrado es el ULTIMO arg; trapz(x,y) integra y respecto de x.
                if (fname == "trapz" && c.Args.Count >= 1)
                {
                    var ty = RenderExpression(c.Args[c.Args.Count - 1]);
                    string tdvar = c.Args.Count >= 2 ? RenderExpression(c.Args[0]) : "x";
                    return $"<span class=\"dvr\"><small></small><span class=\"nary\"><em>∫</em></span><small></small></span>{ty}&thinsp;d<var>{tdvar}</var>";
                }
                // diff(v) NUMERICA de 1 arg -> Δv (diferencia discreta de MATLAB). diff con
                // >=2 args es la derivada simbolica d/dx (bloque de arriba).
                if (fname == "diff" && c.Args.Count == 1)
                    return $"<span style=\"font-family:'Cambria Math',serif;font-style:normal;font-size:1.05em\">Δ</span>&hairsp;{RenderExpression(c.Args[0])}";
                // gradient(F[,h]) -> ∇F (gradiente numerico). El operando es arg[0].
                if (fname == "gradient" && c.Args.Count >= 1)
                    return $"<span style=\"font-family:'Cambria Math',serif;font-style:normal;font-size:1.05em\">∇</span>&hairsp;{RenderExpression(c.Args[0])}";
                // limit(f, x, c)
                if (fname == "limit" && c.Args.Count >= 3)
                {
                    var fExpr = RenderExpression(c.Args[0]);
                    var vExpr = RenderExpression(c.Args[1]);
                    var cExpr = RenderExpression(c.Args[2]);
                    // Inf/inf → ∞ , -Inf → −∞ (detectar en el ARG; cExpr trae markup HTML)
                    if (c.Args[2] is IdentRef cInf && (cInf.Name == "Inf" || cInf.Name == "inf")) cExpr = "∞";
                    else if (c.Args[2] is NumberLit cNum && double.IsInfinity(cNum.Value)) cExpr = cNum.Value < 0 ? "−∞" : "∞";
                    else if (c.Args[2] is UnaryOp cU && cU.Op == "-" && cU.Operand is IdentRef cI2 && (cI2.Name == "Inf" || cI2.Name == "inf")) cExpr = "−∞";
                    // Estilo LIBRO: la condición x→c va DEBAJO de "lim" (como el índice de la ∑),
                    // no como subíndice al lado.
                    return $"<span style=\"display:inline-block;text-align:center;vertical-align:middle\">" +
                           $"<span style=\"display:block;font-family:'Segoe UI',sans-serif;font-weight:600;color:#7c2bb2;line-height:1\">lim</span>" +
                           $"<span style=\"display:block;font-size:.68em;line-height:1;margin-top:.05em\">{vExpr}&rarr;{cExpr}</span>" +
                           $"</span>&thinsp;{fExpr}";
                }
                // taylor(f, ...) — display como T_n(f) con subscript del orden
                if (fname == "taylor" && c.Args.Count >= 1)
                {
                    var fExpr = RenderExpression(c.Args[0]);
                    // Buscar 'Order' keyword o tercer arg numerico
                    string orderHtml = "";
                    for (int i = 1; i < c.Args.Count - 1; i++)
                    {
                        if (c.Args[i] is StringLit sl && sl.Value.Equals("Order", System.StringComparison.OrdinalIgnoreCase))
                        {
                            orderHtml = RenderExpression(c.Args[i + 1]);
                            break;
                        }
                    }
                    if (string.IsNullOrEmpty(orderHtml) && c.Args.Count >= 4)
                        orderHtml = RenderExpression(c.Args[3]);
                    string sub = string.IsNullOrEmpty(orderHtml) ? "" : $"<sub>{orderHtml}</sub>";
                    return $"<span style=\"font-family:'Cambria Math','Times New Roman',serif;font-style:italic;font-weight:600;color:#7c2bb2\">T</span>{sub}({fExpr})";
                }
                // Transformadas integrales: notación de operador con letra caligráfica + llaves,
                // como en un libro (no texto plano "laplace(...)"):
                //   laplace(f)→ℒ{f}, ilaplace(F)→ℒ⁻¹{F}, fourier(f)→ℱ{f}, ifourier→ℱ⁻¹,
                //   ztrans→𝒵{f}, iztrans→𝒵⁻¹.
                {
                    string top = fname switch
                    {
                        "laplace" => "&#8466;", "ilaplace" => "&#8466;<sup>-1</sup>",   // ℒ
                        "fourier" => "&#8497;", "ifourier" => "&#8497;<sup>-1</sup>",   // ℱ
                        "ztrans"  => "&#x1D4B5;", "iztrans" => "&#x1D4B5;<sup>-1</sup>",// 𝒵
                        _ => null
                    };
                    if (top != null && c.Args.Count >= 1)
                    {
                        var fExpr = RenderExpression(c.Args[0]);
                        // Llaves que ESCALAN si el contenido es una fracción (igual que ParenWrap).
                        bool tall = fExpr.Contains("class=\"dvc\"");
                        string Br(string ch) => tall
                            ? $"<span style=\"display:inline-block;transform:scaleY(1.7);vertical-align:middle;font-size:1.15em\">{ch}</span>"
                            : $"<span style=\"font-size:1.15em\">{ch}</span>";
                        return $"<span style=\"font-family:'Cambria Math','Times New Roman',serif;font-style:normal;font-size:1.15em;color:#7c2bb2\">{top}</span>" +
                               Br("&#123;") + fExpr + Br("&#125;");
                    }
                }
                // subs(f, x, val) -> f|_{x=val}
                if (fname == "subs" && c.Args.Count >= 3)
                {
                    var fExpr = RenderExpression(c.Args[0]);
                    var vExpr = RenderExpression(c.Args[1]);
                    var valExpr = RenderExpression(c.Args[2]);
                    return $"{fExpr}<sub>|&thinsp;{vExpr}={valExpr}</sub>";
                }
                // solve(expr, x) -> notacion de LIBRO (sin la palabra "solve"): muestra la
                // ecuacion normal y una flecha ⟹ hacia la variable despejada. El " = valor" lo
                // agrega la rama symbolicCall despues. Ej:
                //   ρ·b·d = As  ⟹  As   (+ " = b·d·ρ")
                //   0.85·fc·Ag + As·(fy-0.85·fc) = Pn  ⟹  Ag   (+ " = ...")
                if (fname == "solve" && c.Args.Count >= 2)
                {
                    string eq;
                    if (c.Args[0] is BinaryOp beq && beq.Op == "==")
                        eq = $"{RenderExpression(beq.Left)} = {RenderExpression(beq.Right)}";
                    else if (c.Args[0] is BinaryOp bsub && bsub.Op == "-")
                        // expr = A - B = 0  ->  mostrar  A = B  (ecuacion normal del libro)
                        eq = $"{RenderExpression(bsub.Left)} = {RenderExpression(bsub.Right)}";
                    else
                        eq = $"{RenderExpression(c.Args[0])} = 0";
                    var vr = RenderExpression(c.Args[1]);
                    return $"{eq}&emsp;<span style=\"color:#7c2bb2;font-weight:600\">⟹</span>&ensp;{vr}";
                }
                // simplify/expand/factor/collect/trigsimplify: NO se escribe la palabra —
                // se muestra solo la EXPRESIÓN, y el "= resultado" (que agrega la rama
                // symbolicCall) da la forma simplificada. Como en un libro: expr = forma.
                if ((fname == "simplify" || fname == "expand" || fname == "factor"
                     || fname == "collect" || fname == "trigsimplify") && c.Args.Count >= 1)
                    return RenderExpression(c.Args[0]);
            }
            // Pretty-print de funciones especiales: sqrt → símbolo √ con vinculum
            if (PrettyMath && c.Target is IdentRef pid && c.Args.Count == 1)
            {
                if (pid.Name == "sqrt")
                {
                    var inner = RenderExpression(c.Args[0]);
                    // SqrPad antes del √ (como el HtmWriter de Calcpad): separa el √ del
                    // operando anterior — antes el "+" (o cualquier op) se SOLAPABA con el √.
                    return $"&ensp;&hairsp;&hairsp;<span class=\"o0\"><span class=\"r\">√</span>&hairsp;{inner}</span>";
                }
                if (pid.Name == "abs")
                    return ScaleBars(RenderExpression(c.Args[0]), 1);
                if (pid.Name == "exp")
                    return $"<span style=\"font-style:italic;font-family:'Cambria Math','Times New Roman',serif\">e</span><sup>{RenderExpression(c.Args[0])}</sup>";
            }
            // Pretty: nthroot(x, n) → ⁿ√x
            if (PrettyMath && c.Target is IdentRef pid2 && pid2.Name == "nthroot" && c.Args.Count == 2)
            {
                var n = RenderExpression(c.Args[1]);
                var x = RenderExpression(c.Args[0]);
                return $"<span class=\"o0\"><sup style=\"font-size:.7em\">{n}</sup><span class=\"r\">√</span>&hairsp;{x}</span>";
            }
            // Pretty: ALGEBRA LINEAL — notacion matematica en vez del nombre de funcion.
            //   norm(v)→‖v‖  dot(a,b)→a·b  cross(a,b)→a×b  det(A)→|A|  inv(A)→A⁻¹  transpose(A)→Aᵀ
            if (PrettyMath && c.Target is IdentRef laId)
            {
                if (laId.Name == "norm" && c.Args.Count == 1)
                    return ScaleBars(RenderExpression(c.Args[0]), 2);
                if (laId.Name == "dot" && c.Args.Count == 2)
                    return $"{RenderExpression(c.Args[0])} · {RenderExpression(c.Args[1])}";
                if (laId.Name == "cross" && c.Args.Count == 2)
                    return $"{RenderExpression(c.Args[0])} × {RenderExpression(c.Args[1])}";
                if (laId.Name == "det" && c.Args.Count == 1)
                    return ScaleBars(RenderExpression(c.Args[0]), 1);
                if (laId.Name == "trace" && c.Args.Count == 1)
                    return $"<span style=\"font-family:'Cambria Math','Times New Roman',serif;font-style:normal\">tr</span>({RenderExpression(c.Args[0])})";
                if (laId.Name == "inv" && c.Args.Count == 1)
                    return $"{RenderExpression(c.Args[0])}<sup>−1</sup>";
                if (laId.Name == "transpose" && c.Args.Count == 1)
                    return $"{RenderExpression(c.Args[0])}<sup>T</sup>";
                // sum(v)→∑ , prod(v)→∏ : render IDENTICO a $Sum/$Product de Calcpad. Calcpad
                // llama FormatNary(∑, sub="i=1", sup="n", expr="f(i)") ->
                //   <span class="dvr"><small>n</small><nary>∑</nary><small>i=1</small></span>f(i)
                // O sea: n ARRIBA, i=1 ABAJO, y el sumando INDEXADO. sum(v) suma los n=numel(v)
                // elementos -> sumando = vᵢ (v con subindice i). Solo 1 arg (sum(A,dim)->call).
                if (laId.Name == "sum" && c.Args.Count == 1)
                    return NaryIndexed("∑", c.Args[0]);
                if (laId.Name == "prod" && c.Args.Count == 1)
                    return NaryIndexed("∏", c.Args[0]);
            }
            // Pretty: funciones matemáticas comunes en ROMANO de libro (no el sans-serif
            // morado de "código"). log→ln, log2/log10 con subíndice, sign→sgn, trig y
            // hiperbólicas en romano. Igual criterio que el render SIMBÓLICO (SymFunc.ToHtml).
            if (PrettyMath && c.Target is IdentRef mfId && c.Args.Count == 1 && IsMathFunc(mfId.Name))
                return MathFuncNameHtml(mfId.Name) + "(" + RenderExpression(c.Args[0]) + ")";
            var sb = new StringBuilder();
            // Funciones builtin: sans-serif bold morado para diferenciacion clara
            // de variables (que estan en italic serif).
            if (c.Target is IdentRef id)
            {
                if (IsCommonBuiltin(id.Name))
                    sb.Append($"<span style=\"font-family:'Segoe UI',sans-serif;font-weight:600;font-style:normal;color:#7c2bb2\">{HttpUtility.HtmlEncode(id.Name)}</span>");
                else
                    // Aplicar Greek mapping y underscore-subscript a nombres de funciones
                    // de usuario tambien: phi(u) -> φ(u), phi_d(u) -> φ_d(u), Phi(x) -> Φ(x).
                    sb.Append(RenderIdentName(id.Name));
            }
            else
                sb.Append(RenderExpression(c.Target));
            sb.Append("(");
            for (int i = 0; i < c.Args.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(RenderExpression(c.Args[i]));
            }
            sb.Append(")");
            return sb.ToString();
        }
        // Lista de variables compacta para jacobian/hessian: [x, y] o [x; y] -> (x, y).
        private static string RenderVarListCompact(MatlabNode e)
        {
            if (e is MatrixLit m)
            {
                var items = new System.Collections.Generic.List<string>();
                foreach (var row in m.Rows)
                    foreach (var el in row)
                        items.Add(RenderExpression(el));
                return "(" + string.Join(", ", items) + ")";
            }
            return RenderExpression(e);
        }
        // ¿Es una función matemática con notación de libro? (unaria)
        private static bool IsMathFunc(string n) => n.ToLowerInvariant() is
            "sin" or "cos" or "tan" or "cot" or "sec" or "csc"
            or "sinh" or "cosh" or "tanh" or "coth" or "sech" or "csch"
            or "asin" or "acos" or "atan" or "acot" or "asec" or "acsc"
            or "asinh" or "acosh" or "atanh" or "acoth" or "asech" or "acsch"
            or "log" or "log2" or "log10" or "sign";
        // Nombre de la función en ROMANO matemático (con ln / log₂ / log₁₀ / sgn).
        private static string MathFuncNameHtml(string name)
        {
            static string Roman(string s) => $"<span style=\"font-family:'Cambria Math','Times New Roman',serif;font-style:normal\">{HttpUtility.HtmlEncode(s)}</span>";
            return name.ToLowerInvariant() switch
            {
                "log"   => Roman("ln"),
                "log2"  => Roman("log") + "<sub>2</sub>",
                "log10" => Roman("log") + "<sub>10</sub>",
                "sign"  => Roman("sgn"),
                _       => Roman(name.ToLowerInvariant())
            };
        }
        private static string RenderRange(Range r)
        {
            if (r.Step != null)
                return $"{RenderExpression(r.Start)}:{RenderExpression(r.Step)}:{RenderExpression(r.End)}";
            return $"{RenderExpression(r.Start)}:{RenderExpression(r.End)}";
        }
        private static string RenderMatrixLit(MatrixLit m)
        {
            // Renderiza como matriz visual con corchetes CSS (Calcpad-style)
            var sb = new StringBuilder();
            sb.Append("<span class=\"matrix\">");
            for (int i = 0; i < m.Rows.Count; i++)
            {
                sb.Append("<span class=\"tr\"><span class=\"td\"></span>");
                for (int j = 0; j < m.Rows[i].Count; j++)
                {
                    sb.Append("<span class=\"td\">");
                    sb.Append(RenderExpression(m.Rows[i][j]));
                    sb.Append("</span>");
                }
                sb.Append("<span class=\"td\"></span></span>");
            }
            sb.Append("</span>");
            return sb.ToString();
        }
        private static string FormatComplex(double re, double im)
        {
            if (im == 0) return FormatNumber(re);
            if (re == 0)
            {
                if (im == 1) return "i";
                if (im == -1) return "-i";
                return FormatNumber(im) + "i";
            }
            string sign = im < 0 ? "-" : "+";
            double absIm = System.Math.Abs(im);
            string imStr = absIm == 1 ? "" : FormatNumber(absIm);
            return $"{FormatNumber(re)} {sign} {imStr}i";
        }

        /// <summary>Cifras significativas de la salida numérica MATLAB. La controla el
        /// selector "Round" de la barra inferior (6 por defecto ≈ MATLAB 'format short';
        /// 15 ≈ 'format long'). Antes estaba fija en 6 e ignoraba el control.</summary>
        public static int SignificantDigits = 6;

        private static string FormatNumber(double v)
        {
            if (double.IsNaN(v)) return "NaN";
            if (double.IsPositiveInfinity(v)) return "Inf";
            if (double.IsNegativeInfinity(v)) return "-Inf";
            if (v == 0) return "0";
            // Enteros exactos: mostrarlos COMPLETOS, sin notación científica ni redondeo
            // de cifras significativas (200000, no 2E+05; 375, no 3.8E+02). MATLAB hace
            // igual — un literal/valor entero es exacto y la precisión de sig-figs solo
            // aplica a los no-enteros. Se limita a |v|<1e15 (doble exacto) para no imprimir
            // monstruos de 20 dígitos.
            if (v == System.Math.Floor(v) && System.Math.Abs(v) < 1e15)
                return v.ToString("0", CultureInfo.InvariantCulture);
            // Formato DECIMAL fijo (no cifras significativas): el selector "Round" = nº de
            // DECIMALES. 490.12 se ve 490.12 (no 4.9E+02). Notación científica SOLO para
            // muy chicos (|v|<1e-4, ej. 0.0000123 -> 1.23E-05) o muy grandes (|v|>=1e15).
            var dec = SignificantDigits < 0 ? 0 : (SignificantDigits > 17 ? 17 : SignificantDigits);
            double av = System.Math.Abs(v);
            if (av < 1e-4 || av >= 1e15)
                return v.ToString("0.#####E+00", CultureInfo.InvariantCulture);
            var s = v.ToString("F" + dec.ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture);
            if (s.Contains('.')) s = s.TrimEnd('0').TrimEnd('.');   // 490.10 -> 490.1 ; 5.00 -> 5
            return s;
        }
        private static bool IsCommonBuiltin(string name) =>
            name is "sin" or "cos" or "tan" or "exp" or "log" or "log2" or "log10"
            or "sqrt" or "abs" or "sign" or "floor" or "ceil" or "round" or "fix"
            or "sum" or "prod" or "mean" or "min" or "max" or "cumsum" or "cumprod" or "diff"
            or "length" or "numel" or "size" or "zeros" or "ones" or "eye"
            or "linspace" or "logspace" or "meshgrid" or "transpose"
            or "atan2" or "mod" or "rem" or "power"
            or "asin" or "acos" or "atan" or "sinh" or "cosh" or "tanh"
            or "sind" or "cosd" or "tand" or "deg2rad" or "rad2deg"
            or "sort" or "sortrows" or "unique" or "any" or "all" or "isempty" or "isscalar" or "isvector"
            or "reshape" or "repmat" or "disp"
            // Plots
            or "plot" or "plot3" or "scatter" or "scatter3" or "surf" or "mesh" or "imagesc"
            or "contour" or "contourf" or "pcolor" or "bar" or "barh" or "hist" or "histogram"
            or "stem" or "stairs" or "polar" or "compass" or "loglog" or "semilogx" or "semilogy"
            or "errorbar" or "quiver" or "peaks" or "colormap" or "colorbar" or "title"
            or "xlabel" or "ylabel" or "zlabel" or "legend" or "shading" or "axis" or "view"
            or "grid" or "hold" or "box" or "figure" or "clf" or "subplot"
            // Linear algebra
            or "det" or "inv" or "inverse" or "norm" or "dot" or "cross" or "trace" or "find"
            // String / IO
            or "sprintf" or "fprintf" or "num2str" or "str2num" or "strcat" or "strlen"
            or "upper" or "lower"
            // Higher-order
            or "feval" or "arrayfun" or "cellfun" or "structfun" or "map"
            // Simbolicos
            or "syms" or "expand" or "simplify" or "factor" or "solve" or "subs"
            or "int" or "taylor" or "limit" or "dsolve" or "laplace" or "fourier"
            or "trigsimplify" or "collect" or "coeffs"
            // Optim/interp/sigproc
            or "fzero" or "fminbnd" or "fminsearch" or "spline" or "pchip" or "polyfit" or "polyval" or "roots"
            or "trapz" or "cumtrapz" or "gradient" or "conv" or "filter"
            or "var" or "std" or "median"
            // FFT
            or "fft" or "ifft" or "fft2" or "ifft2" or "fftshift"
            // LinAlg/Sparse
            or "linsolve" or "mldivide" or "gauss_seidel" or "pcg"
            or "sparse" or "full" or "issparse" or "nnz" or "spdiags" or "speye" or "spones"
            // ODE
            or "ode45" or "ode23" or "ode4" or "ode_euler"
            // Complex
            or "real" or "imag" or "conj" or "angle" or "complex"
            // Strings extra
            or "strsplit" or "strjoin" or "strrep" or "strfind" or "contains"
            or "startsWith" or "endsWith" or "strtrim"
            or "str2double" or "int2str" or "erase" or "deblank" or "blanks" or "pad"
            // Workspace
            or "who" or "whos" or "clear" or "exist" or "tic" or "toc" or "assignin" or "evalin"
            // I/O
            or "csvread" or "csvwrite" or "dlmread" or "dlmwrite"
            or "readmatrix" or "writematrix" or "readtable" or "writetable"
            or "importdata" or "textread" or "xlsread" or "xlswrite"
            // Optim
            or "fsolve" or "lsqnonlin" or "lsqcurvefit"
            // Integration
            or "integral" or "quad" or "quadl" or "quadgk" or "dblquad" or "triplequad"
            // Regex
            or "regexp" or "regexprep" or "regexpi"
            // Broadcasting / sparse viz
            or "bsxfun" or "spy" or "magic"
            // Random
            or "rand" or "randn" or "randi" or "randperm"
            // LinAlg avanzado
            or "expm" or "logm" or "sqrtm" or "lu" or "qr" or "chol" or "schur"
            or "bicg" or "gmres"
            // PDE / BVP
            or "pdepe" or "nthroot" or "bvp4c"
            // Symbolic
            or "sym" or "syms" or "diff" or "subs" or "simplify" or "expand" or "double" or "latex"
            // I/O extra
            or "imread" or "imwrite"
            // Modern integrals
            or "integral2" or "integral3"
            // Symbolic v10
            or "int" or "taylor" or "solve" or "pretty" or "laplace" or "fourier"
            // v11: ilaplace, limit, conv2/imfilter, quiver3/slice, text/annotation
            or "ilaplace" or "limit" or "conv2" or "imfilter" or "imresize"
            or "fspecial" or "rgb2gray"
            or "quiver3" or "slice" or "streamslice"
            or "text" or "annotation" or "sgtitle"
            // v12: signal, z-transform, coeffs
            or "heaviside" or "dirac" or "rectpuls" or "sinc"
            or "ztrans" or "iztrans"
            or "coeffs" or "sym2poly" or "poly2sym" or "collect"
            // v13: control systems + symbolic extras
            or "tf" or "zpk" or "step" or "impulse" or "bode" or "nyquist"
            or "series" or "parallel" or "feedback"
            or "pole" or "zero" or "dcgain" or "damp"
            or "symsum" or "piecewise" or "assume" or "assumeAlso"
            // v14: state-space, SVD, manipulation
            or "ss" or "tf2ss" or "ss2tf" or "lsim" or "c2d" or "d2c"
            or "margin" or "rlocus"
            or "svd" or "rank" or "pinv"
            or "vertcat" or "horzcat" or "cat" or "permute" or "squeeze"
            or "flipud" or "fliplr" or "rot90" or "ipermute"
            or "logspace"
            // v15: lqr/kalman, stats, signal filters, JSON
            or "lqr" or "care" or "lqe" or "stepinfo"
            or "normpdf" or "normcdf" or "norminv" or "tpdf" or "tcdf"
            or "chi2pdf" or "chi2cdf" or "fpdf" or "binopdf" or "poisspdf" or "gampdf"
            or "erf" or "erfc" or "erfinv" or "gamma" or "beta" or "factorial" or "nchoosek"
            or "butter" or "freqz" or "hilbert" or "xcorr" or "xcov"
            or "jsonencode" or "jsondecode"
            // v16: trig advanced + filtros extra + viz 2D + fmincon
            or "trigexpand" or "trigsimplify"
            or "cheby1" or "cheby2" or "ellip"
            or "histogram2" or "heatmap" or "stem"
            or "fmincon" or "linprog" or "quadprog"
            // v17: sparse real + linalg fundamentals + string arrays
            or "density" or "nonzeros"
            or "kron" or "null" or "orth" or "colspace" or "rowspace"
            or "string" or "strlength"
            // v18: 3D arrays + utilities + .mat I/O
            or "zeros3" or "ones3" or "ndims" or "cat3"
            or "mat2str" or "accumarray" or "tabulate" or "histcounts"
            or "save" or "load";
    }
}
