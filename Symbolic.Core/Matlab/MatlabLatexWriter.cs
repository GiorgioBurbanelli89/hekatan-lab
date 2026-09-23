// =============================================================================
// Hekatan Lab — MATLAB → LaTeX Writer (export .tex self-contained)
// =============================================================================
//   Toma un AST MATLAB + valores evaluados y produce un .tex compilable:
//     - texto (%% %" %') como \section*/\textbf/parrafos
//     - ecuaciones (asignaciones/expresiones) como LaTeX real (\frac, ^{}, \cdot…)
//     - figuras → PNG externos junto al .tex, referenciados con \includegraphics
//
//   Espeja la lógica de tokens de MatlabHtmlWriter (subíndice `_`, superíndice
//   `sup`, griegas nu→\nu…, fracción `__`) pero emite código LaTeX en vez de HTML.
//   Es display-only: NO altera ningún número (el motor ya calculó los valores).
// =============================================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace Calcpad.Core.Matlab
{
    public static class MatlabLatexWriter
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

        /// <summary>Activo mientras se exporta un .tex. Lo consulta el evaluador para que
        /// `char(symExpr)` devuelva LaTeX en vez de HTML (misma marca PUA en ambos casos).</summary>
        public static bool LatexExportMode { get; private set; }

        // Marcas PUA que el evaluador pone alrededor del render de una expresión simbólica.
        private const char SymOpen = '';
        private const char SymClose = '';

        // ─────────────────────────────────────────────────────────────────────
        //  Punto de entrada: ejecuta los statements con el evaluador (para tener
        //  los valores calculados) y escribe el documento .tex. Las figuras se
        //  rasterizan a PNG (PngExportMode) y se guardan junto al .tex.
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>Genera un .tex a partir de los statements ya parseados. `ev` debe tener
        /// las funciones/clases pre-registradas. `assetsDir` = carpeta para los PNG (por
        /// defecto la del .tex). Devuelve la ruta del .tex escrito.</summary>
        public static string ExportLatex(List<MatlabNode> stmts, MatlabEvaluator ev,
                                         string texPath, string assetsDir = null)
        {
            if (stmts == null) throw new ArgumentNullException(nameof(stmts));
            if (ev == null) throw new ArgumentNullException(nameof(ev));

            string texDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(texPath));
            string baseName = System.IO.Path.GetFileNameWithoutExtension(texPath);
            string pngDir = string.IsNullOrEmpty(assetsDir) ? texDir : assetsDir;
            try { if (!System.IO.Directory.Exists(pngDir)) System.IO.Directory.CreateDirectory(pngDir); } catch { }
            // Prefijo relativo para el \includegraphics (si los PNG van a otra carpeta).
            string relPrefix = "";
            if (!string.IsNullOrEmpty(assetsDir))
            {
                relPrefix = assetsDir.Replace('\\', '/');
                if (!relPrefix.EndsWith("/")) relPrefix += "/";
            }

            var body = new StringBuilder();
            int imgCounter = 0;

            // Escribe un PNG (bytes) junto al .tex y devuelve el bloque \includegraphics.
            string EmitPng(byte[] png)
            {
                if (png == null || png.Length == 0) return "";
                imgCounter++;
                string file = $"{baseName}_img{imgCounter:00}.png";
                try { System.IO.File.WriteAllBytes(System.IO.Path.Combine(pngDir, file), png); }
                catch { return ""; }
                return $"\\begin{{center}}\\includegraphics[width=0.8\\linewidth]{{{relPrefix}{file}}}\\end{{center}}\n";
            }
            // Igual pero desde una data-URI base64 (`% #img data:image/png;base64,…`).
            string EmitDataUri(string src)
            {
                int comma = src.IndexOf("base64,", StringComparison.OrdinalIgnoreCase);
                if (comma < 0) return "";
                try { return EmitPng(Convert.FromBase64String(src.Substring(comma + 7).Trim())); }
                catch { return ""; }
            }

            // ── Rasterización de figuras a PNG (sin navegador) ──
            bool prevPng = MatlabPlots.PngExportMode;
            var exported = MatlabPlots.ExportedPngs;
            int lastPngCount = exported.Count;
            MatlabPlots.PngExportMode = true;
            // disp/fprintf: se CAPTURAN. Hay memorias enteras escritas con fprintf
            // (prosa + `char(expr)` simbolico): silenciarlas dejaba el .tex vacio.
            // `char(sym)` emite LaTeX (no HTML) mientras dure la exportación.
            LatexExportMode = true;
            var dispBuf = new StringBuilder();
            // fprintf ya trae sus propios '\n' (y el '\n\n' con el que el autor separa
            // párrafos). AppendLine añadiría uno de más y partiría cada línea en un
            // párrafo suelto: sólo se completa la última línea si venía sin salto.
            ev.Output = msg =>
            {
                if (string.IsNullOrEmpty(msg)) return;
                dispBuf.Append(msg);
                if (!msg.EndsWith('\n')) dispBuf.Append('\n');
            };
            ev.HtmlOut = _ => { };     // HTML inline: silenciado (las figuras van por ExportedPngs)

            // Pre-cálculo de escalares para @nombre que aparecen ANTES de su definición.
            var preview = BuildPreview(ev, stmts);

            // Vuelca los PNG nuevos acumulados desde la última comprobación.
            void FlushNewPngs()
            {
                if (lastPngCount == exported.Count) return;
                FlushDisp();   // el texto impreso va ANTES de la figura que lo acompaña
                for (int i = lastPngCount; i < exported.Count; i++)
                    body.Append(EmitPng(exported[i]));
                lastPngCount = exported.Count;
            }

            // Vuelca lo que disp/fprintf hayan impreso desde el statement anterior.
            // Prosa → párrafo LaTeX normal (reflow). Salida ALINEADA (matrices, tablas:
            // líneas con 2+ espacios o tabs) → verbatim, que es donde el alineado ES el dato.
            void FlushDisp()
            {
                if (dispBuf.Length == 0) return;
                var text = dispBuf.ToString();
                dispBuf.Clear();
                foreach (var block in SplitParagraphs(text))
                    body.Append(BlockToLatex(block));
            }

            try
            {
                for (int si = 0; si < stmts.Count; si++)
                {
                    var stmt = stmts[si];
                    // ── Comentarios / directivas de texto ──
                    if (stmt is CommentStmt cs)
                    {
                        // Volcar lo impreso hasta aquí: fprintf consecutivos forman UN
                        // párrafo; sólo se corta cuando aparece otra cosa (texto del
                        // autor, ecuación o figura).
                        FlushDisp();
                        EmitComment(cs, body, ev, preview, EmitDataUri);
                        continue;
                    }
                    // ── function/classdef: ya registradas; no ejecutar como statement ──
                    if (stmt is FunctionDef || stmt is ClassDef) continue;

                    // ── Ejecutar (para obtener el valor) ──
                    StatementResult result;
                    try { result = ev.ExecuteOne(stmt, ev.Globals); }
                    catch { continue; }   // v1: un statement que falla no aborta el reporte

                    // Figuras generadas por este statement (plot/surf/…): PNG externos.
                    FlushNewPngs();

                    // Void (fprintf/plot/figure/…) o suprimido → sin ecuación.
                    if (IsVoid(stmt) || GetSuppressed(stmt)) continue;

                    // Va a salir una ecuación: primero el texto impreso que la precede.
                    FlushDisp();

                    // ── @ PEGADO: si el SIGUIENTE stmt es un comentario inline (MISMA linea) con un
                    //    @ suelto (placeholder), fusionar: el @ = ESTA ecuacion, y NO emitirla aparte.
                    //    Espeja el render HTML (a = 6  %' Lado: @ m  ->  "Lado: a = 6 m").
                    if (si + 1 < stmts.Count && stmts[si + 1] is CommentStmt nc && !nc.IsHeading
                        && nc.Line == stmt.Line)
                    {
                        var ctext = nc.Text.TrimStart();
                        if (ctext.StartsWith("'")) ctext = ctext.Substring(1);
                        var ctrim = ctext.TrimStart();
                        bool isEqNum = ctrim.StartsWith("@@") || ctrim.StartsWith("#deq");
                        if (!isEqNum && HasBareAt(ctext))
                        {
                            string interp = InterpolateVarRefs(ctext, ev, preview);   // @name→$..$, @ suelto queda literal
                            string eqInline = InlineEqLatex(stmt, result);
                            int at = interp.IndexOf('@');
                            string merged = at >= 0
                                ? interp.Substring(0, at) + eqInline + interp.Substring(at + 1)
                                : interp + " " + eqInline;
                            body.Append(merged.Trim()).Append("\n\n");
                            si++;   // consumir el comentario inline
                            continue;
                        }
                    }

                    EmitStatement(stmt, result, body);
                }

                FlushDisp();   // lo que imprimió el último statement

                // Cerrar la figura que quedó abierta (patch/line sin saveas) → último PNG.
                try
                {
                    if (MatlabPlots.HasOpenFigure)
                    {
                        MatlabPlots.FinishFigure();   // rasteriza y añade a ExportedPngs
                        FlushNewPngs();
                    }
                }
                catch { }
            }
            finally
            {
                MatlabPlots.PngExportMode = prevPng;
                LatexExportMode = false;
            }

            // ── Ensamblar el documento ──
            var doc = new StringBuilder();
            doc.Append("\\documentclass[11pt]{article}\n");
            doc.Append("\\usepackage{amsmath,graphicx,amssymb}\n");
            doc.Append("\\usepackage[T1]{fontenc}\n");
            doc.Append("\\usepackage[utf8]{inputenc}\n");
            doc.Append("\\usepackage[margin=2.5cm]{geometry}\n");
            doc.Append("\\setlength{\\parindent}{0pt}\n");
            doc.Append("\\begin{document}\n");
            doc.Append(body);
            doc.Append("\\end{document}\n");

            System.IO.File.WriteAllText(texPath, doc.ToString(), new UTF8Encoding(false));
            return texPath;
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Comentarios de texto: %% (encabezado), %" (título), %' (párrafo),
        //  % #img (imagen), % #deq (ecuación numerada). El resto se oculta.
        // ─────────────────────────────────────────────────────────────────────
        private static void EmitComment(CommentStmt cs, StringBuilder body, MatlabEvaluator ev,
                                        Dictionary<string, double> preview,
                                        Func<string, string> emitDataUri)
        {
            if (cs.IsHeading)   // %%  → sección
            {
                body.Append($"\\section*{{{EscapeText(cs.Text.Trim())}}}\n");
                return;
            }
            var t = cs.Text.TrimStart();
            if (t.Length == 0) return;
            char marker = t[0];

            // % #img <data-uri | ruta> → figura incrustada
            if (t.StartsWith("#img", StringComparison.OrdinalIgnoreCase))
            {
                var src = t.Substring(4).Trim();
                if (src.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                    body.Append(emitDataUri(src));
                else if (src.Length > 0)
                    body.Append($"\\begin{{center}}\\includegraphics[width=0.8\\linewidth]{{{src.Replace('\\', '/')}}}\\end{{center}}\n");
                return;
            }
            // % #deq ecuacion @@(numero) → ecuación centrada (best-effort)
            if (t.StartsWith("#deq", StringComparison.OrdinalIgnoreCase))
            {
                var eq = t.Substring(4).Trim();
                int at = eq.IndexOf("@@", StringComparison.Ordinal);
                if (at >= 0) eq = eq.Substring(0, at).Trim();
                body.Append($"\\[{EquationToLatex(eq)}\\]\n");
                return;
            }
            // Otras directivas #… no soportadas en v1 → ocultar (como MATLAB).
            if (marker == '#' || marker == '$') return;

            if (marker == '"' || marker == '\'')
            {
                var rest = t.Length > 1 ? t.Substring(1) : "";
                EmitTextLine(marker, rest, body, ev, preview);
                return;
            }
            // Comentario normal `% …` (o `%--`): oculto, igual que en MATLAB.
        }

        /// <summary>Emite una línea de texto %"/%' con prefijos de formato (&lt; &gt; | * / _)
        /// e interpola @nombre/@{expr} como $nombre = valor$.</summary>
        private static void EmitTextLine(char marker, string rest, StringBuilder body,
                                        MatlabEvaluator ev, Dictionary<string, double> preview)
        {
            var trimmed = rest.Trim();
            // Escape literal: %'\...  → texto crudo
            if (trimmed.StartsWith("\\"))
            {
                body.Append(EscapeText(trimmed.Substring(1)) + "\n\n");
                return;
            }
            // Línea divisoria: %'----- (solo guiones/iguales)
            if (System.Text.RegularExpressions.Regex.IsMatch(trimmed, @"^[-=]{2,}$"))
            {
                body.Append("\\begin{center}\\rule{0.9\\linewidth}{0.4pt}\\end{center}\n");
                return;
            }
            // HTML crudo (%'<table>…) no se traduce en v1 → se omite.
            if (trimmed.StartsWith("<") && trimmed.Contains(">") &&
                System.Text.RegularExpressions.Regex.IsMatch(trimmed, "^<[a-zA-Z/!]"))
                return;

            // Prefijos de formato combinables (< > | * / _).
            string align = null; bool bold = false, ital = false, under = false;
            var s = rest.TrimStart();
            bool more = true;
            while (more && s.Length > 0)
            {
                switch (s[0])
                {
                    case '<': align = "left"; break;
                    case '>': align = "right"; break;
                    case '|': align = "center"; break;
                    case '*': bold = true; break;
                    case '/': ital = true; break;
                    case '_': under = true; break;
                    default: more = false; break;
                }
                if (more) s = s.Substring(1);
            }
            // %" da base de título: centrado + negrita (salvo que un prefijo lo cambie).
            if (marker == '"')
            {
                bold = true;
                if (align == null) align = "center";
            }
            string inner = InterpolateVarRefs(s.Trim(), ev, preview);
            if (bold) inner = $"\\textbf{{{inner}}}";
            if (ital) inner = $"\\textit{{{inner}}}";
            if (under) inner = $"\\underline{{{inner}}}";

            if (marker == '"' && align == "center")
                body.Append($"\\begin{{center}}{inner}\\end{{center}}\n");
            else if (align == "center")
                body.Append($"\\begin{{center}}{inner}\\end{{center}}\n");
            else if (align == "right")
                body.Append($"\\begin{{flushright}}{inner}\\end{{flushright}}\n");
            else if (marker == '"' && align == "left")
                body.Append($"\\noindent {inner}\n\n");
            else
                body.Append(inner + "\n\n");
        }

        /// <summary>Sustituye @nombre / @{expr} por $nombre = valor$ dentro del texto,
        /// escapando el resto. Resuelve el valor con el evaluador (valor actual o preview).</summary>
        private static string InterpolateVarRefs(string text, MatlabEvaluator ev, Dictionary<string, double> preview)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('@') < 0)
                return EscapeText(text ?? "");
            var sb = new StringBuilder();
            int i = 0;
            while (i < text.Length)
            {
                int at = text.IndexOf('@', i);
                if (at < 0) { sb.Append(EscapeText(text.Substring(i))); break; }
                if (at > i) sb.Append(EscapeText(text.Substring(i, at - i)));
                int k = at + 1;
                string expr = null;
                if (k < text.Length && text[k] == '{')   // @{expr}
                {
                    int close = text.IndexOf('}', k);
                    if (close > k) { expr = text.Substring(k + 1, close - k - 1); k = close + 1; }
                }
                else                                      // @nombre
                {
                    int st = k;
                    while (k < text.Length && (char.IsLetterOrDigit(text[k]) || text[k] == '_')) k++;
                    if (k > st) expr = text.Substring(st, k - st);
                }
                if (string.IsNullOrEmpty(expr)) { sb.Append('@'); i = at + 1; continue; }
                sb.Append(VarRefToLatex(expr, ev, preview));
                i = k;
            }
            return sb.ToString();
        }

        /// <summary>«expr = valor» como ecuación inline $…$. expr = identificador o expresión.</summary>
        private static string VarRefToLatex(string expr, MatlabEvaluator ev, Dictionary<string, double> preview)
        {
            string valStr = null;
            try
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(expr, @"^[A-Za-z_]\w*$"))
                {
                    if (ev.Globals.Vars.TryGetValue(expr, out var v0) && v0.IsScalar)
                        valStr = FormatNumber(v0.Scalar);
                    else if (preview != null && preview.TryGetValue(expr, out var vp))
                        valStr = FormatNumber(vp);
                }
                else
                {
                    var toks = MatlabTokenizer.Tokenize(expr);
                    var node = new MatlabParser(toks).ParseExpression();
                    MValue res = null;
                    try { res = ev.Eval(node, ev.Globals); } catch { }
                    if (res != null && res.IsScalar) valStr = FormatNumber(res.Scalar);
                }
            }
            catch { }
            // El símbolo (LHS) se tipografía como expresión LaTeX.
            string lhs;
            try
            {
                var toks = MatlabTokenizer.Tokenize(expr);
                lhs = ExprToLatex(new MatlabParser(toks).ParseExpression());
            }
            catch { lhs = IdentToLatex(expr); }
            return valStr != null ? $"${lhs} = {valStr}$" : $"${lhs}$";
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Asignaciones / expresiones → ecuación display \[ … \]
        // ─────────────────────────────────────────────────────────────────────
        private static void EmitStatement(MatlabNode stmt, StatementResult result, StringBuilder body)
        {
            if (stmt is Assignment asg)
            {
                string lhs = LhsToLatex(asg);
                string valStr = ValueToLatex(result.Value);
                // Casos (espejo de MatlabHtmlWriter):
                //  - literal trivial (número/matriz/‘…’) → nombre = valor
                //  - llamada/indexado no-matemático → nombre = valor (no re-echar la llamada)
                //  - otro → nombre = expr = valor (si valor difiere de la expr)
                if (IsTrivialLiteral(asg.Rhs) || (asg.Rhs is CallOrIndex && !IsKnownMathCall(asg.Rhs)))
                {
                    body.Append($"\\[{lhs} = {valStr}\\]\n");
                }
                else
                {
                    string rhs = ExprToLatex(asg.Rhs);
                    if (valStr == rhs || string.IsNullOrEmpty(valStr))
                        body.Append($"\\[{lhs} = {rhs}\\]\n");
                    else
                        body.Append($"\\[{lhs} = {rhs} = {valStr}\\]\n");
                }
                return;
            }
            if (stmt is ExprStmt es)
            {
                string lhs = ExprToLatex(es.Expr);
                string valStr = ValueToLatex(result.Value);
                if (valStr == lhs || string.IsNullOrEmpty(valStr))
                    body.Append($"\\[{lhs}\\]\n");
                else
                    body.Append($"\\[{lhs} = {valStr}\\]\n");
            }
        }

        /// <summary>Version INLINE ($…$) de una ecuacion (para fusionar con el texto del @ pegado).</summary>
        private static string InlineEqLatex(MatlabNode stmt, StatementResult result)
        {
            if (stmt is Assignment asg)
            {
                string lhs = LhsToLatex(asg);
                string valStr = ValueToLatex(result.Value);
                if (IsTrivialLiteral(asg.Rhs) || (asg.Rhs is CallOrIndex && !IsKnownMathCall(asg.Rhs)))
                    return $"${lhs} = {valStr}$";
                string rhs = ExprToLatex(asg.Rhs);
                if (valStr == rhs || string.IsNullOrEmpty(valStr)) return $"${lhs} = {rhs}$";
                return $"${lhs} = {rhs} = {valStr}$";
            }
            if (stmt is ExprStmt es)
            {
                string lhs = ExprToLatex(es.Expr);
                string valStr = ValueToLatex(result.Value);
                if (valStr == lhs || string.IsNullOrEmpty(valStr)) return $"${lhs}$";
                return $"${lhs} = {valStr}$";
            }
            return "";
        }

        /// <summary>true si hay un @ SUELTO (placeholder): un @ no seguido de letra/_/{ (que serian @nombre/@{}).</summary>
        private static bool HasBareAt(string text)
        {
            for (int i = 0; i < text.Length; i++)
                if (text[i] == '@')
                {
                    char nx = i + 1 < text.Length ? text[i + 1] : ' ';
                    if (!(char.IsLetter(nx) || nx == '_' || nx == '{')) return true;
                }
            return false;
        }

        private static string LhsToLatex(Assignment asg)
        {
            if (asg.Targets.Count == 1) return ExprToLatex(asg.Targets[0]);
            var sb = new StringBuilder("[");
            for (int i = 0; i < asg.Targets.Count; i++)
            {
                if (i > 0) sb.Append(",\\ ");
                sb.Append(ExprToLatex(asg.Targets[i]));
            }
            sb.Append("]");
            return sb.ToString();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  ExprToLatex — AST → código LaTeX
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>Traduce una expresión AST MATLAB a código LaTeX (\frac, ^{}, \cdot,
        /// \sqrt, funciones \sin…). Sin evaluación — es puramente sintáctico.</summary>
        public static string ExprToLatex(MatlabNode node)
        {
            switch (node)
            {
                case null: return "";
                case NumberLit n: return FormatNumber(n.Value);
                case ImaginaryLit im:
                    return im.Value == 1 ? "i" : (im.Value == -1 ? "-i" : FormatNumber(im.Value) + "i");
                case StringLit s: return $"\\text{{{EscapeText(s.Value)}}}";
                case IdentRef id: return IdentToLatex(id.Name);
                case UnaryOp u: return RenderUnary(u);
                case BinaryOp b: return RenderBinary(b);
                case CallOrIndex c: return RenderCall(c);
                case Range r: return RenderRange(r);
                case MatrixLit m: return RenderMatrix(m);
                case FieldAccess fa: return ExprToLatex(fa.Target) + "." + IdentToLatex(fa.FieldName);
                case ColonAll: return ":";
                default: return $"\\text{{{EscapeText(RawText(node))}}}";
            }
        }

        private static string RenderUnary(UnaryOp u)
        {
            if (u.IsPrefix)
            {
                string op = u.Op == "~" || u.Op == "!" ? "\\neg " : u.Op;
                return op + Wrap(u.Operand, 7, false);
            }
            // postfix transpose ' o .' → superíndice T
            if (u.Op == "'" || u.Op == ".'") return Wrap(u.Operand, 8, false) + "^{T}";
            return ExprToLatex(u.Operand) + EscapeText(u.Op);
        }

        private static string RenderBinary(BinaryOp b)
        {
            switch (b.Op)
            {
                case "/":
                case "./":
                    return $"\\frac{{{ExprToLatex(b.Left)}}}{{{ExprToLatex(b.Right)}}}";
                case "\\":
                case ".\\":
                    return $"{Wrap(b.Left, 5, false)}^{{-1}} \\cdot {Wrap(b.Right, 5, true)}";
                case "^":
                case ".^":
                {
                    string baseStr = (b.Left is BinaryOp || (b.Left is UnaryOp ul && ul.IsPrefix))
                        ? $"\\left({ExprToLatex(b.Left)}\\right)"
                        : ExprToLatex(b.Left);
                    return $"{baseStr}^{{{ExprToLatex(b.Right)}}}";
                }
                case "*":
                case ".*":
                    return $"{Wrap(b.Left, 5, false)} \\cdot {Wrap(b.Right, 5, true)}";
                case "+":
                    return $"{Wrap(b.Left, 4, false)} + {Wrap(b.Right, 4, true)}";
                case "-":
                    return $"{Wrap(b.Left, 4, false)} - {Wrap(b.Right, 4, true)}";
                default:
                {
                    string op = b.Op switch
                    {
                        "==" => " = ", "~=" => " \\neq ", "<=" => " \\leq ", ">=" => " \\geq ",
                        "<" => " < ", ">" => " > ", "&&" => " \\land ", "&" => " \\land ",
                        "||" => " \\lor ", "|" => " \\lor ",
                        _ => " " + EscapeText(b.Op) + " "
                    };
                    return $"{Wrap(b.Left, OpPrec(b.Op), false)}{op}{Wrap(b.Right, OpPrec(b.Op), true)}";
                }
            }
        }

        /// <summary>Envuelve el hijo en \left(\right) si su precedencia lo exige.</summary>
        private static string Wrap(MatlabNode child, int parentPrec, bool rightSide)
        {
            string s = ExprToLatex(child);
            if (child is BinaryOp cb)
            {
                int cp = OpPrec(cb.Op);
                bool need = cp < parentPrec || (cp == parentPrec && rightSide && cb.Op != "^" && cb.Op != ".^");
                // Las fracciones (`/`) no necesitan paréntesis: la barra ya agrupa.
                if (cb.Op == "/" || cb.Op == "./") need = false;
                return need ? $"\\left({s}\\right)" : s;
            }
            if (child is UnaryOp u && u.IsPrefix && u.Op == "-" && parentPrec >= 5)
                return $"\\left({s}\\right)";
            return s;
        }

        private static int OpPrec(string op) => op switch
        {
            "||" or "|" => 1,
            "&&" or "&" => 2,
            "==" or "~=" or "<" or ">" or "<=" or ">=" => 3,
            "+" or "-" => 4,
            "*" or "/" or "\\" or ".*" or "./" or ".\\" => 5,
            "^" or ".^" => 6,
            _ => 10
        };

        // Funciones conocidas → notación LaTeX propia.
        private static readonly HashSet<string> _trig = new(StringComparer.Ordinal)
        {
            "sin","cos","tan","cot","sec","csc","sinh","cosh","tanh",
            "arcsin","arccos","arctan","asin","acos","atan","atan2",
            "ln","lg","min","max","gcd","dim","det","exp","log"
        };

        private static string RenderCall(CallOrIndex c)
        {
            if (c.Target is IdentRef id && c.Args != null)
            {
                string nm = id.Name; int k = c.Args.Count;
                string A0 = k >= 1 ? ExprToLatex(c.Args[0]) : "";
                switch (nm)
                {
                    case "sqrt": return k == 1 ? $"\\sqrt{{{A0}}}" : $"\\mathrm{{sqrt}}({JoinArgs(c.Args)})";
                    case "nthroot": return k == 2 ? $"\\sqrt[{ExprToLatex(c.Args[1])}]{{{A0}}}" : $"\\mathrm{{nthroot}}({JoinArgs(c.Args)})";
                    case "abs": return k == 1 ? $"\\left|{A0}\\right|" : $"\\mathrm{{abs}}({JoinArgs(c.Args)})";
                    case "norm": return k >= 1 ? $"\\left\\|{A0}\\right\\|" : "\\mathrm{norm}()";
                    case "exp": return k == 1 ? $"e^{{{A0}}}" : $"\\exp({JoinArgs(c.Args)})";
                    case "log": return k == 1 ? $"\\ln\\left({A0}\\right)" : $"\\log_{{{ExprToLatex(c.Args[1])}}}\\left({A0}\\right)";
                    case "log2": return $"\\log_{{2}}\\left({A0}\\right)";
                    case "log10": return $"\\log_{{10}}\\left({A0}\\right)";
                    case "sin": case "cos": case "tan": case "cot": case "sec": case "csc":
                    case "sinh": case "cosh": case "tanh":
                        return $"\\{nm}\\left({A0}\\right)";
                    case "asin": return $"\\arcsin\\left({A0}\\right)";
                    case "acos": return $"\\arccos\\left({A0}\\right)";
                    case "atan": return $"\\arctan\\left({A0}\\right)";
                    case "transpose": return $"{A0}^{{T}}";
                    case "inv": return $"{A0}^{{-1}}";
                    case "det": return $"\\det\\left({A0}\\right)";
                }
                if (_trig.Contains(nm))
                    return $"\\{nm}\\left({JoinArgs(c.Args)}\\right)";
                // No es función matemática conocida: puede ser indexado a(i) → a_{i}
                // (heurística v1: 1+ args simples y el target es identificador simple).
                if (AllSimpleIndices(c.Args))
                    return $"{IdentToLatex(nm)}_{{{JoinArgs(c.Args)}}}";
                return $"\\mathrm{{{EscapeText(nm)}}}\\left({JoinArgs(c.Args)}\\right)";
            }
            // Target complejo (indexado encadenado, etc.) → subíndice.
            return $"{ExprToLatex(c.Target)}_{{{JoinArgs(c.Args)}}}";
        }

        private static bool AllSimpleIndices(List<MatlabNode> args)
        {
            if (args == null || args.Count == 0) return false;
            foreach (var a in args)
                if (!(a is NumberLit || a is IdentRef || a is ColonAll)) return false;
            return true;
        }

        private static string JoinArgs(List<MatlabNode> args)
        {
            if (args == null || args.Count == 0) return "";
            var sb = new StringBuilder();
            for (int i = 0; i < args.Count; i++)
            {
                if (i > 0) sb.Append(",\\ ");
                sb.Append(ExprToLatex(args[i]));
            }
            return sb.ToString();
        }

        private static string RenderRange(Range r)
        {
            string a = ExprToLatex(r.Start), e = ExprToLatex(r.End);
            return r.Step != null ? $"{a}\\!:\\!{ExprToLatex(r.Step)}\\!:\\!{e}" : $"{a}\\!:\\!{e}";
        }

        private static string RenderMatrix(MatrixLit m)
        {
            var sb = new StringBuilder("\\begin{bmatrix}");
            for (int i = 0; i < m.Rows.Count; i++)
            {
                if (i > 0) sb.Append(" \\\\ ");
                for (int j = 0; j < m.Rows[i].Count; j++)
                {
                    if (j > 0) sb.Append(" & ");
                    sb.Append(ExprToLatex(m.Rows[i][j]));
                }
            }
            sb.Append("\\end{bmatrix}");
            return sb.ToString();
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Identificadores: griegas (nu→\nu), fracción `__`, subíndice `_`,
        //  superíndice `sup`. Espeja RenderIdentName de MatlabHtmlWriter.
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>Nombre MATLAB → LaTeX: griega + subíndice + superíndice + fracción `__`.</summary>
        public static string IdentToLatex(string name)
        {
            if (string.IsNullOrEmpty(name)) return "";
            // Fracción `a__b` → \frac{a}{b}
            int dbl = name.IndexOf("__", StringComparison.Ordinal);
            if (dbl > 0 && dbl + 2 < name.Length)
                return $"\\frac{{{IdentToLatex(name.Substring(0, dbl))}}}{{{IdentToLatex(name.Substring(dbl + 2))}}}";
            int idx = name.IndexOf('_');
            if (idx <= 0 || idx == name.Length - 1)
                return DecorateBase(name);
            string baseName = name.Substring(0, idx);
            string sub = name.Substring(idx + 1).Replace("_", ",");
            return $"{DecorateBase(baseName)}_{{{DecorateBase(sub)}}}";
        }

        /// <summary>Decora la base: griega (alpha→\alpha), superíndice `sup2`→^{2}.</summary>
        private static string DecorateBase(string b)
        {
            if (string.IsNullOrEmpty(b)) return "";
            // Superíndice con token `sup`: xsup2 → x^{2}
            var withSup = System.Text.RegularExpressions.Regex.Replace(
                b, "sup([0-9]+)", "\u0001$1\u0002");   // marca temporal
            string greek = GreekToLatex(b);
            if (greek != null) return greek;
            // Aplica el marcador de superíndice tras descartar la griega.
            if (withSup.IndexOf('\u0001') >= 0)
                return withSup.Replace("\u0001", "^{").Replace("\u0002", "}");
            return EscapeText(b);
        }

        /// <summary>Nombre de letra griega → comando LaTeX (o null).</summary>
        private static string GreekToLatex(string name) => name switch
        {
            "alpha" => "\\alpha", "beta" => "\\beta", "gamma" => "\\gamma", "delta" => "\\delta",
            "epsilon" => "\\epsilon", "zeta" => "\\zeta", "eta" => "\\eta", "theta" => "\\theta",
            "iota" => "\\iota", "kappa" => "\\kappa", "lambda" => "\\lambda", "mu" => "\\mu",
            "nu" => "\\nu", "xi" => "\\xi", "omicron" => "o", "pi" => "\\pi",
            "rho" => "\\rho", "sigma" => "\\sigma", "tau" => "\\tau", "upsilon" => "\\upsilon",
            "phi" => "\\phi", "chi" => "\\chi", "psi" => "\\psi", "omega" => "\\omega",
            "Alpha" => "A", "Beta" => "B", "Gamma" => "\\Gamma", "Delta" => "\\Delta",
            "Theta" => "\\Theta", "Lambda" => "\\Lambda", "Pi" => "\\Pi", "Sigma" => "\\Sigma",
            "Phi" => "\\Phi", "Psi" => "\\Psi", "Omega" => "\\Omega",
            _ => null
        };

        // ─────────────────────────────────────────────────────────────────────
        //  Valores → LaTeX
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>Valor evaluado → LaTeX (escalar con unidad, string, matriz bmatrix).</summary>
        public static string ValueToLatex(MValue v)
        {
            if (v == null) return "";
            if (v.IsString) return $"\\text{{{EscapeText(v.StringValue ?? "")}}}";
            if (v.IsScalar)
            {
                string num = FormatNumber(v.Scalar);
                return v.HasUnit ? num + "\\," + UnitToLatex(v.Unit.Text) : num;
            }
            if (v.IsComplex && v.Rows == 1 && v.Cols == 1)
            {
                double re = v.Data[0], im = v.Imag[0];
                string sign = im < 0 ? " - " : " + ";
                return FormatNumber(re) + sign + FormatNumber(Math.Abs(im)) + "i";
            }
            // Matriz real (con truncación para matrices grandes).
            if (v.Data != null && !v.IsSymbolic && !v.IsCell && !v.IsStruct && v.Rows >= 1 && v.Cols >= 1)
            {
                const int maxN = 8;
                int nr = Math.Min(v.Rows, maxN), nc = Math.Min(v.Cols, maxN);
                var sb = new StringBuilder("\\begin{bmatrix}");
                for (int i = 0; i < nr; i++)
                {
                    if (i > 0) sb.Append(" \\\\ ");
                    for (int j = 0; j < nc; j++)
                    {
                        if (j > 0) sb.Append(" & ");
                        if (v.IsComplex)
                        {
                            int lin = i * v.Cols + j;
                            double re = v.Data[lin], im = v.Imag[lin];
                            sb.Append(FormatNumber(re) + (im < 0 ? "-" : "+") + FormatNumber(Math.Abs(im)) + "i");
                        }
                        else sb.Append(FormatNumber(v.At(i, j)));
                    }
                    if (nc < v.Cols) sb.Append(" & \\cdots");
                }
                if (nr < v.Rows) sb.Append(" \\\\ \\vdots ");
                sb.Append("\\end{bmatrix}");
                return sb.ToString();
            }
            return "";   // v1: struct/cell/símbolo se omiten (se muestra solo la fórmula)
        }

        /// <summary>Texto de unidad Calcpad → \mathrm{…} (superíndices `^n`).</summary>
        private static string UnitToLatex(string unitText)
        {
            if (string.IsNullOrEmpty(unitText)) return "";
            var u = unitText.Replace("*", "\\cdot ");
            u = System.Text.RegularExpressions.Regex.Replace(u, @"\^(-?\d+)", "^{$1}");
            return $"\\mathrm{{{u}}}";
        }

        // ─────────────────────────────────────────────────────────────────────
        //  Helpers
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>Formatea un número al estilo MATLAB corto para LaTeX.</summary>
        public static string FormatNumber(double d)
        {
            if (double.IsNaN(d)) return "\\mathrm{NaN}";
            if (double.IsPositiveInfinity(d)) return "\\infty";
            if (double.IsNegativeInfinity(d)) return "-\\infty";
            if (d == 0) return "0";
            double abs = Math.Abs(d);
            // Entero exacto en rango razonable.
            if (d == Math.Floor(d) && abs < 1e15)
                return ((long)d).ToString(Inv);
            // Notación científica para magnitudes extremas.
            if (abs >= 1e6 || abs < 1e-4)
            {
                string sci = d.ToString("0.#####e+0", Inv);
                int ei = sci.IndexOf('e');
                if (ei > 0)
                {
                    string mant = sci.Substring(0, ei);
                    int exp = int.Parse(sci.Substring(ei + 1), Inv);
                    return $"{mant} \\times 10^{{{exp}}}";
                }
                return sci;
            }
            return d.ToString("0.#####", Inv);
        }

        /// <summary>Escapa los caracteres especiales de LaTeX en TEXTO plano.</summary>
        public static string EscapeText(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            var sb = new StringBuilder(s.Length + 8);
            foreach (char c in s)
            {
                switch (c)
                {
                    case '\\': sb.Append("\\textbackslash{}"); break;
                    case '&': sb.Append("\\&"); break;
                    case '%': sb.Append("\\%"); break;
                    case '$': sb.Append("\\$"); break;
                    case '#': sb.Append("\\#"); break;
                    case '_': sb.Append("\\_"); break;
                    case '{': sb.Append("\\{"); break;
                    case '}': sb.Append("\\}"); break;
                    case '~': sb.Append("\\textasciitilde{}"); break;
                    case '^': sb.Append("\\textasciicircum{}"); break;
                    default: sb.Append(c); break;
                }
            }
            return sb.ToString();
        }

        /// <summary>Renderiza una ECUACIÓN dada como string (para #deq): parte por '=' de
        /// nivel superior, parsea cada lado y lo tipografía. Si no parsea, cae a texto.</summary>
        private static string EquationToLatex(string src)
        {
            if (string.IsNullOrWhiteSpace(src)) return "";
            var parts = SplitTopEquals(src);
            var sb = new StringBuilder();
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
                    sb.Append(ExprToLatex(new MatlabParser(toks).ParseExpression()));
                }
                catch { sb.Append(EscapeText(s)); }
            }
            return sb.ToString();
        }

        private static List<string> SplitTopEquals(string s)
        {
            var res = new List<string>();
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

        private static bool GetSuppressed(MatlabNode n) =>
            n is Assignment a ? a.Suppressed : (n is ExprStmt e && e.Suppressed);

        private static bool IsTrivialLiteral(MatlabNode n)
        {
            if (n is NumberLit || n is StringLit || n is ImaginaryLit) return true;
            if (n is UnaryOp u && u.Op == "-" && u.IsPrefix) return IsTrivialLiteral(u.Operand);
            if (n is MatrixLit m)
            {
                foreach (var row in m.Rows)
                    foreach (var e in row)
                        if (!IsTrivialLiteral(e)) return false;
                return true;
            }
            return false;
        }

        private static bool IsKnownMathCall(MatlabNode n)
        {
            if (n is CallOrIndex c && c.Target is IdentRef id)
                return id.Name is "sqrt" or "abs" or "norm" or "exp" or "log" or "log2" or "log10"
                    or "nthroot" or "det" or "inv" or "transpose"
                    || _trig.Contains(id.Name);
            return false;
        }

        // Funciones "void" (side-effect only): no producen ecuación.
        private static readonly HashSet<string> _void = new(StringComparer.Ordinal)
        {
            "fprintf","printf","disp","display","warning","error","puts","fputs","fdisp","fflush",
            "figure","clf","close","hold","axis","grid","legend","box","colormap",
            "title","xlabel","ylabel","zlabel","colorbar","sgtitle","caxis","clim",
            "shading","view","light","lighting","material","camlight","drawnow","snapnow",
            "plot","plot3","scatter","scatter3","bar","barh","stem","stairs",
            "polar","polarplot","fill","fill3","patch","line","text",
            "histogram","histogram2","heatmap","contour","contourf","imagesc",
            "surf","mesh","surfc","meshc","quiver","quiver3","streamslice",
            "trisurf","trimesh","triplot","tetramesh","solidmesh","spy","loglog","semilogx","semilogy",
            "area","errorbar","boxplot","pie","fplot",
            "mkdir","save","saveas","load","clear","format","echo","pkg","tic","toc",
            "syms","global","persistent","subplot","tight_layout",
        };

        // ─────────────────────────────────────────────────────────────────────
        //  Salida de disp/fprintf → LaTeX
        // ─────────────────────────────────────────────────────────────────────
        /// <summary>Parte el texto impreso en bloques separados por líneas en blanco
        /// (= párrafos en MATLAB, donde `\n\n` es el separador habitual).</summary>
        private static List<string> SplitParagraphs(string text)
        {
            var blocks = new List<string>();
            var current = new List<string>();
            foreach (var raw in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
            {
                if (raw.Trim().Length == 0)
                {
                    if (current.Count > 0) { blocks.Add(string.Join("\n", current)); current.Clear(); }
                }
                else
                    current.Add(raw);
            }
            if (current.Count > 0) blocks.Add(string.Join("\n", current));
            return blocks;
        }

        /// <summary>Un bloque impreso a LaTeX. Se decide LÍNEA A LÍNEA: una línea
        /// ALINEADA (matriz/tabla: 2+ espacios seguidos o tabs, y sin ecuación dentro)
        /// va a `verbatim`, porque ahí el alineado ES el dato. Las de prosa se juntan en
        /// un párrafo y LaTeX las reajusta. Un `disp(A)` seguido de un `fprintf` en el
        /// mismo bloque conserva cada uno su forma.</summary>
        private static string BlockToLatex(string block)
        {
            var lines = block.Split('\n');
            var outSb = new StringBuilder();
            var run = new List<string>();
            bool runAligned = false;

            void FlushRun()
            {
                if (run.Count == 0) return;
                if (runAligned)
                {
                    outSb.Append("\\begin{verbatim}\n");
                    foreach (var l in run) outSb.Append(l.TrimEnd()).Append('\n');
                    outSb.Append("\\end{verbatim}\n\n");
                }
                else
                {
                    for (int i = 0; i < run.Count; i++)
                    {
                        if (i > 0) outSb.Append(' ');
                        outSb.Append(EscapeTextKeepingMath(run[i].Trim()));
                    }
                    outSb.Append("\n\n");
                }
                run.Clear();
            }

            foreach (var l in lines)
            {
                bool aligned = l.IndexOf(SymOpen) < 0 &&
                               (l.Contains('\t') || l.TrimStart().Contains("  ", StringComparison.Ordinal));
                if (run.Count > 0 && aligned != runAligned) FlushRun();
                runAligned = aligned;
                run.Add(l);
            }
            FlushRun();
            return outSb.ToString();
        }

        /// <summary>Escapa el texto impreso, pero lo que va entre las marcas PUA es LaTeX
        /// que puso `char(symExpr)`: pasa tal cual dentro de `$…$`.</summary>
        private static string EscapeTextKeepingMath(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            if (s.IndexOf(SymOpen) < 0) return EscapeText(s);

            var sb = new StringBuilder(s.Length + 16);
            int pos = 0;
            while (pos < s.Length)
            {
                int open = s.IndexOf(SymOpen, pos);
                if (open < 0) { sb.Append(EscapeText(s[pos..])); break; }
                sb.Append(EscapeText(s[pos..open]));
                int close = s.IndexOf(SymClose, open + 1);
                if (close < 0)   // marca sin cerrar: no inventar, escapar el resto
                { sb.Append(EscapeText(s[(open + 1)..])); break; }
                var math = s[(open + 1)..close].Trim();
                if (math.Length > 0)
                    sb.Append('$').Append(math).Append('$');
                pos = close + 1;
            }
            return sb.ToString();
        }

        private static bool IsVoid(MatlabNode s)
        {
            if (s is ExprStmt es)
            {
                if (es.Expr is CallOrIndex ci && ci.Target is IdentRef ir) return _void.Contains(ir.Name);
                if (es.Expr is IdentRef ir2) return _void.Contains(ir2.Name);
            }
            return false;
        }

        // Pre-cálculo de escalares (para @nombre que aparece antes de su definición).
        private static readonly HashSet<string> _pureMath = new(StringComparer.Ordinal)
        {
            "sqrt","abs","sin","cos","tan","asin","acos","atan","atan2","sinh","cosh","tanh",
            "exp","log","log10","log2","floor","ceil","round","fix","mod","rem","min","max",
            "sign","hypot","deg2rad","rad2deg","factorial","nchoosek","pow2","power"
        };
        private static bool IsSafeScalarRhs(MatlabNode n)
        {
            switch (n)
            {
                case NumberLit: case ImaginaryLit: case IdentRef: return true;
                case UnaryOp u: return IsSafeScalarRhs(u.Operand);
                case BinaryOp b: return IsSafeScalarRhs(b.Left) && IsSafeScalarRhs(b.Right);
                case CallOrIndex c:
                    if (c.Target is IdentRef fn && _pureMath.Contains(fn.Name))
                    {
                        foreach (var a in c.Args) if (!IsSafeScalarRhs(a)) return false;
                        return true;
                    }
                    return false;
                default: return false;
            }
        }
        private static Dictionary<string, double> BuildPreview(MatlabEvaluator ev, List<MatlabNode> stmts)
        {
            var dict = new Dictionary<string, double>(StringComparer.Ordinal);
            var scope = new MatlabScope();
            foreach (var st in stmts)
            {
                if (st is Assignment asg && asg.Targets.Count == 1 && asg.Targets[0] is IdentRef tgt
                    && IsSafeScalarRhs(asg.Rhs))
                {
                    try
                    {
                        var val = ev.Eval(asg.Rhs, scope);
                        if (val != null && val.IsScalar) { scope.Vars[tgt.Name] = val; dict[tgt.Name] = val.Scalar; }
                    }
                    catch { }
                }
            }
            return dict;
        }

        private static string RawText(MatlabNode n) => n?.GetType().Name ?? "";
    }
}
