// =============================================================================
// Calcpad Lab — MATLAB Pipeline: facade Tokenizer + Parser + Evaluator + HtmlWriter
// =============================================================================
//   Entry-point único para ejecutar un fragmento MATLAB y obtener HTML output.
//   ESTE pipeline NO usa MathParser/ExpressionParser de Calcpad — es 100% propio.
//
//   Uso desde ExpressionParser para líneas detectadas como MATLAB-puro:
//
//     var html = MatlabPipeline.Run(line, scope, out var result);
//     if (html != null) _sb.Append(html);
// =============================================================================
using System;
using System.Collections.Generic;
using System.Text;

namespace Calcpad.Core.Matlab
{
    public sealed class MatlabPipeline
    {
        // Pre-calienta el solver LAPACK/MKL UNA sola vez al crear el primer pipeline,
        // fuera de toda medicion, para que el primer K\f del usuario no pague ~70ms de
        // init de MKL (dpbsv). Idempotente (flag interno). Ver LapackInterop.Warmup.
        static MatlabPipeline()
        {
            try { Calcpad.Core.LapackInterop.Warmup(); } catch { }
        }

        private readonly MatlabEvaluator _evaluator = new();
        public MatlabScope GlobalScope => _evaluator.Globals;

        private readonly System.Collections.Generic.HashSet<string> _loadedFnFiles =
            new(System.StringComparer.OrdinalIgnoreCase);

        /// <summary>Fija el directorio del script en ejecución para poder cargar funciones
        /// hermanas (.m del mismo directorio) y resolver addpath relativos — como MATLAB.
        /// Debe llamarse tras construir el pipeline, antes de Run.</summary>
        public void SetScriptDirectory(string dir, string scriptPath = null)
        {
            if (string.IsNullOrEmpty(dir)) return;
            _evaluator.PrimaryScriptDir = dir;
            if (!string.IsNullOrEmpty(scriptPath)) _evaluator.PrimaryScriptPath = scriptPath;
            if (!_evaluator.FunctionSearchDirs.Contains(dir))
                _evaluator.FunctionSearchDirs.Insert(0, dir);
            _evaluator.ExternalFunctionLoader = LoadFunctionFile;
            _evaluator.ExternalScriptRunner = RunScriptFile;
        }

        /// <summary>Ejecuta inline un script `.m` hermano (script-calls-script de MATLAB:
        /// `Muro_Acople_ITW;` corre ese archivo en el workspace actual). Pre-registra sus
        /// funciones y ejecuta el resto de statements en el scope global. Cada script una vez.</summary>
        private bool RunScriptFile(string name)
        {
            foreach (var dir in _evaluator.FunctionSearchDirs)
            {
                string path;
                try { path = System.IO.Path.Combine(dir, name + ".m"); }
                catch { continue; }
                if (!System.IO.File.Exists(path)) continue;
                var key = "script:" + path;
                if (_loadedFnFiles.Contains(key)) return true;   // ya corrido → no re-ejecutar
                _loadedFnFiles.Add(key);
                var src = System.IO.File.ReadAllText(path);
                var toks = MatlabTokenizer.Tokenize(src);
                var sts = new MatlabParser(toks).ParseAllStatements();
                // pre-pass: registrar funciones/clases del script hermano
                foreach (var st in sts)
                {
                    if (st is FunctionDef fd) _evaluator.RegisterFunction(fd);
                    else if (st is ClassDef cd) _evaluator.RegisterClass(cd);
                }
                // ejecutar los statements top-level (no-función) en el workspace actual
                foreach (var st in sts)
                    if (!(st is FunctionDef) && !(st is ClassDef))
                        _evaluator.ExecuteOne(st, _evaluator.Globals);
                return true;
            }
            return false;
        }

        /// <summary>Busca `name.m` en los directorios de búsqueda (script dir + addpath),
        /// lo tokeniza/parsea y registra TODAS sus funciones. Devuelve true si registró
        /// una función llamada `name`. Cada archivo se carga una sola vez.</summary>
        private bool LoadFunctionFile(string name)
        {
            if (_evaluator.HasUserFunction(name) || _evaluator.HasClass(name)) return true;
            foreach (var dir in _evaluator.FunctionSearchDirs)
            {
                string path;
                try { path = System.IO.Path.Combine(dir, name + ".m"); }
                catch { continue; }
                if (_loadedFnFiles.Contains(path) || !System.IO.File.Exists(path)) continue;
                _loadedFnFiles.Add(path);
                try
                {
                    var src = System.IO.File.ReadAllText(path);
                    var toks = MatlabTokenizer.Tokenize(src);
                    var sts = new MatlabParser(toks).ParseAllStatements();
                    foreach (var st in sts)
                    {
                        if (st is FunctionDef fd) _evaluator.RegisterFunction(fd);
                        else if (st is ClassDef cd) _evaluator.RegisterClass(cd);
                    }
                }
                catch { /* archivo con error de parseo → se ignora, sigue buscando */ }
                if (_evaluator.HasUserFunction(name) || _evaluator.HasClass(name)) return true;
            }
            return false;
        }

        /// <summary>Export de gráficas a PNG (CLI sin navegador). true → cada figura se
        /// rasteriza y acumula en ExportedPngs. Proxy público de MatlabPlots (internal).</summary>
        public static bool PngExportMode
        {
            get => MatlabPlots.PngExportMode;
            set => MatlabPlots.PngExportMode = value;
        }
        public static System.Collections.Generic.List<byte[]> ExportedPngs => MatlabPlots.ExportedPngs;

        /// <summary>Puesto por el builtin `cd` cuando su destino es un ARCHIVO: la WPF lo abre
        /// tras terminar el render (cd 'ruta\archivo.m' -> abre ese archivo).</summary>
        public static string RequestedOpenFile;
        /// <summary>Directorio de trabajo puesto por `cd 'carpeta'`: los dialogos Abrir/Guardar
        /// de la WPF lo usan como carpeta inicial (en vez de Examples) si no hay archivo abierto.</summary>
        public static string UserWorkingDir;


        // ── Directivas Calcpad embebidas en comentarios MATLAB ──────────────
        // Un `.m` corre idéntico en MATLAB 2017a (que ve `% #deq ...` como un
        // comentario mudo) y en Calcpad-Lab (que aquí lo typografía). El motor
        // MATLAB calcula los números/plots REALES; estas directivas son SOLO
        // tipografía decorativa que enriquece el reporte de Lab sin alterar
        // ningún resultado. Reusa el ExpressionParser de Calcpad-puro (mismo
        // assembly) en vez de reimplementar el render.
        private Settings _calcpadSettings;

        /// <summary>Si está en true, evita las mutaciones retroactivas del StringBuilder
        /// (inline comments / multi-stmt same-line) que rompen el streaming chunk-based,
        /// ya que el chunk previo ya fue enviado al UI. En modo streaming los inline
        /// comments y multi-stmts se renderean como `<p>` standalone.</summary>
        public bool StreamingMode { get; set; }

        /// <summary>Valores vivos de los controles interactivos (Piso 3). Proxy al evaluador,
        /// que existe desde el constructor. La WPF lo setea antes de correr (el dict vive en la
        /// WPF y sobrevive a re-runs; el pipeline/evaluador es nuevo cada cálculo).</summary>
        public System.Collections.Generic.Dictionary<string, double> ControlValues
        {
            get => _evaluator.ControlValues;
            set => _evaluator.ControlValues = value;
        }
        /// <summary>Geometría dibujada con el cursor (canal 'geom' del Piso 3): por Tag, la matriz de
        /// vértices que ginput devuelve. Vive en la WPF y sobrevive a re-runs, igual que ControlValues.</summary>
        public System.Collections.Generic.Dictionary<string, double[][]> GeomValues
        {
            get => _evaluator.GeomValues;
            set => _evaluator.GeomValues = value;
        }

        /// <summary>Modo Octave: habilita las extensiones de sintaxis Octave sobre el motor
        /// MATLAB (comentarios <c>#</c>, <c>+= ++ --</c>, <c>endfor/endif/...</c>, <c>!</c>,
        /// <c>do…until</c>, <c>printf</c>, continuación con <c>\</c>). Calcpad-Octave lo pone
        /// en true; Calcpad-Lab lo deja en false (MATLAB estricto). Por defecto se toma de
        /// la variable de entorno <c>CALCPAD_OCTAVE=1</c> (gancho de pruebas).</summary>
        public bool OctaveMode { get; set; }
            = Environment.GetEnvironmentVariable("CALCPAD_OCTAVE") == "1";

        /// <summary>Nombre de la función de ENTRADA (= nombre del archivo .m sin extensión).
        /// Cuando el archivo es solo-funciones (function file), correrlo en MATLAB invoca la
        /// función primaria — la que se llama igual que el archivo. El CLI/WPF lo setea con el
        /// basename del archivo; así el auto-run llama a la función correcta aunque el
        /// MatlabFolderLoader haya antepuesto funciones de OTROS .m de la carpeta.</summary>
        public string EntryFunctionHint { get; set; }

        /// <summary>Fires antes de ejecutar cada statement top-level (line, sourceText).
        /// La UI lo usa para mostrar "Calculando línea N..." progresivamente.</summary>
        public event Action<int> StatementStarting;
        /// <summary>Fires después de ejecutar cada statement top-level. `chunkHtml`
        /// es el HTML emitido por ese statement (incluyendo disp/plot flushes).
        /// La UI lo appendea al output panel sin esperar a que termine el script.</summary>
        public event Action<int, string> StatementCompleted;
        /// <summary>Fires una vez al finalizar el script (después del foreach principal,
        /// incluye la figura final si quedó abierta).</summary>
        public event Action<string> ScriptFinished;

        /// <summary>
        /// Procesa un fragmento de código MATLAB. Devuelve HTML concatenado de
        /// todos los statements. Lanza <see cref="MatlabParseException"/> o
        /// <see cref="MatlabRuntimeException"/> en caso de error.
        /// </summary>
        public string Run(string source)
        {
            // Notación Calcpad $Op{} ($Area, $Sum, …): al TECLEAR se transpila por
            // párrafo (Enter), pero al ABRIR un .m o en headless (--shot) el texto
            // llega crudo. Transpilar aquí cubre TODOS los caminos. Idempotente.
            if (Calcpad.Core.DollarTranspiler.ContainsMathOp(source))
                source = Calcpad.Core.DollarTranspiler.Transpile(source);
            MatlabTokenizer.OctaveMode = OctaveMode;
            _evaluator.OctaveMode = OctaveMode;
            var tokens = MatlabTokenizer.Tokenize(source);
            var parser = new MatlabParser(tokens);
            var stmts = parser.ParseAllStatements();
            // PRE-PASS: registrar todas las function/classdef ANTES de ejecutar
            // (MATLAB permite usar helpers definidos al final del script)
            foreach (var stmt in stmts)
            {
                if (stmt is FunctionDef fd2)
                    _evaluator.RegisterFunction(fd2);
                else if (stmt is ClassDef cd2)
                    _evaluator.RegisterClass(cd2);
            }
            // AUTO-RUN de archivo-función (comportamiento MATLAB): un archivo cuyo primer
            // código es `function` y que NO tiene sentencias top-level ejecutables NO corre
            // solo con el pre-pass (solo registra). En MATLAB, pulsar Run sobre ese archivo
            // INVOCA la función primaria (la primera) sin argumentos. Replicamos eso: si no
            // hay código top-level ejecutable y la función primaria no requiere argumentos,
            // añadimos una llamada sintética `primaria()` para que el loop la ejecute (y sus
            // side-effects — figure/surf/light/... — emitan HTML como cualquier statement).
            {
                bool hasTopLevelExec = false;
                FunctionDef firstFn = null;
                FunctionDef entryFn = null;
                foreach (var stmt in stmts)
                {
                    if (stmt is FunctionDef fd)
                    {
                        if (firstFn == null) firstFn = fd;
                        if (EntryFunctionHint != null && string.Equals(fd.Name, EntryFunctionHint, System.StringComparison.Ordinal))
                            entryFn = fd;
                    }
                    else if (stmt is ClassDef) { /* solo registro */ }
                    else if (stmt is CommentStmt) { /* comentario, no ejecutable */ }
                    else { hasTopLevelExec = true; }
                }
                // Preferir la función que coincide con el nombre del archivo (la primaria en
                // MATLAB). Si no hay hint o no matchea, caer a la PRIMERA función definida.
                // Con MatlabFolderLoader anteponiendo otros .m, "la primera" ya no es la del
                // archivo abierto — por eso el hint es lo correcto.
                FunctionDef primaryFn = entryFn ?? firstFn;
                // Invocable con CERO args = sin parámetros, o `varargin`, o una función que
                // usa `nargin` en su cuerpo (maneja args faltantes con defaults, p.ej.
                // `function M=muroFEM(S)` con `if nargin<1, S=...; end`). Así abrir la
                // librería sola y pulsar Run corre el caso por defecto y dibuja.
                bool primaryUsesNargin = false;
                if (primaryFn != null && primaryFn.ParamNames.Count >= 1)
                {
                    int startLine = primaryFn.Line, endLine = int.MaxValue;
                    foreach (var s in stmts)
                        if (s is FunctionDef fd3 && fd3.Line > startLine && fd3.Line < endLine) endLine = fd3.Line;
                    var srcLines = source.Split('\n');
                    for (int li = startLine - 1; li >= 0 && li < srcLines.Length && li < endLine - 1; li++)
                        if (System.Text.RegularExpressions.Regex.IsMatch(srcLines[li], @"\bnargin\b")) { primaryUsesNargin = true; break; }
                }
                bool callableZeroArgs = primaryFn != null &&
                    (primaryFn.ParamNames.Count == 0 ||
                     (primaryFn.ParamNames.Count == 1 && primaryFn.ParamNames[0] == "varargin") ||
                     primaryUsesNargin);
                if (!hasTopLevelExec && callableZeroArgs)
                {
                    var call = new CallOrIndex
                    {
                        Target = new IdentRef { Name = primaryFn.Name, Line = primaryFn.Line },
                        Args = new System.Collections.Generic.List<MatlabNode>(),
                        Line = primaryFn.Line
                    };
                    stmts.Add(new ExprStmt { Expr = call, Suppressed = true, Line = primaryFn.Line });
                }
            }
            var sb = new StringBuilder();
            // Re-route stdout (disp) y HTML inline (plots) al output
            var dispBuffer = new StringBuilder();
            var htmlBuffer = new StringBuilder();
            // Bloque  % #hide … % #show  (estilo Calcpad): ejecuta TODO pero no renderiza nada
            // en Lab (oculta fprintf/disp/resultados). En MATLAB el codigo corre igual.
            bool hidden = false;
            // Transliteración de nombres griegos (nu→ν, phi→φ) en el output: ON por defecto.
            // Estado por-corrida (se restaura aquí para que una corrida no herede el
            // #nogreek de otra). Se togglea con % #nogreek … % #greek (ver bucle).
            MatlabHtmlWriter.GreekAutoRender = true;
            // Salida de fprintf/disp: por defecto RENDERIZADO (variables itálicas, griegas,
            // unidades verdes; los segmentos de char() se respetan). Con % #plain … % #render
            // se puede pasar a texto plano y volver. Pedido de Jorge: "por defecto renderice
            // todo; si quiero texto plano, un % que lo indique". Estado por-corrida.
            bool renderDisp = true;
            _evaluator.Output = msg => { if (!hidden) dispBuffer.AppendLine(msg); };
            _evaluator.HtmlOut = html => { if (!hidden) htmlBuffer.Append(html); };
            // FRAMES de animación (drawnow): en StreamingMode (WPF) se emiten EN VIVO con la marca
            // \x01FRAME\x01 → el host los repinta en el mismo lienzo. En batch (CLI) se ignoran los
            // intermedios (solo importa la figura final que emite FinishFigure).
            // ¿se streameó al menos un frame de animación en vivo? Si sí, la figura ya se ve
            // animada en #labAnimFrame y NO hay que volcar otra figura final (evita el plot DUPLICADO).
            bool animFrameStreamed = false;
            _evaluator.FrameOut = html => { if (!hidden && html != null && StreamingMode && StatementCompleted != null) { animFrameStreamed = true; StatementCompleted.Invoke(-1, "@@LABFRAME@@" + html); } };
            // Marca de streaming: hasta dónde de `sb` ya se emitió en vivo. Declarado
            // ACÁ (antes de InnerStmtOut) para que el lambda pueda avanzarlo al emitir
            // chunks por iteración y el flush top-level no los reenvíe (evita duplicados).
            int pendingChunkStart = 0;
            int pendingChunkLine = -1;
            // Helper para generar anchor de línea clickeable (formato Calcpad-compatible)
            // El template Calcpad captura <a href="#0">, lee data-text, y dispara LineClicked(N)
            string LineLink(int line) => $"[<a href=\"#0\" data-text=\"{line}\">{line}</a>]";

            // Funciones "void" (side-effect only): cuando se invocan como statement,
            // MATLAB NO muestra eco del call. Solo se muestra su side effect.
            // Si el statement es uno de estos calls, suprimimos el render.
            var voidFuncs = new System.Collections.Generic.HashSet<string>(System.StringComparer.Ordinal)
            {
                // I/O
                "fprintf", "printf", "disp", "display", "warning", "error",
                "puts", "fputs", "fdisp", "fflush",   // Octave
                // Plot management
                "figure", "clf", "close", "hold", "axis", "grid", "legend", "box", "colormap",
                "title", "xlabel", "ylabel", "zlabel", "colorbar", "sgtitle", "caxis", "clim",
                "shading", "view", "light", "lighting", "material", "camlight", "drawnow", "snapnow",
                // Plot primitives (efecto sobre figura, no return value útil)
                "plot", "plot3", "scatter", "scatter3", "bar", "barh", "stem", "stairs",
                "polar", "polarplot", "fill", "fill3", "patch", "line", "text",
                "histogram", "histogram2", "heatmap", "contour", "contourf", "imagesc",
                "surf", "mesh", "surfc", "meshc", "quiver", "quiver3", "streamslice",
                "trisurf", "trimesh", "triplot", "tetramesh", "solidmesh", "spy", "loglog", "semilogx", "semilogy",
                "area", "errorbar", "boxplot", "pie", "fplot",
                // System / file
                "mkdir", "save", "saveas", "load", "clear", "format", "echo", "pkg",
                "tic", "toc",
                "syms", "global", "persistent",
                "subplot", "tight_layout",
            };
            bool IsVoidStatement(MatlabNode s)
            {
                if (s is ExprStmt es)
                {
                    if (es.Expr is CallOrIndex ci && ci.Target is IdentRef ir)
                        return voidFuncs.Contains(ir.Name);
                    // Sintaxis de comando (sin parentesis): `tic`, `hold`, `clf`, `grid`… se
                    // parsean como IdentRef pelado. En MATLAB no muestran nada -> tambien void.
                    // (una variable suelta `x` NO esta en voidFuncs, asi que sigue mostrandose.)
                    if (es.Expr is IdentRef ir2)
                        return voidFuncs.Contains(ir2.Name);
                }
                return false;
            }

            // Bloque de atributos de formato de texto: `%'{color,align,estilo} texto` (y en
            // titulos `%"{...}`). Traduce tokens (ES/EN) a CSS. Es el "codigo dentro de %" para
            // pedir TODO el formato facil. Tokens separados por coma/espacio/;.
            static string AttrsToCss(string attrs)
            {
                var css = new System.Text.StringBuilder();
                foreach (var raw in attrs.Split(new[] { ',', ';', ' ' }, System.StringSplitOptions.RemoveEmptyEntries))
                {
                    switch (raw.Trim().ToLowerInvariant())
                    {
                        case "left": case "izquierda": case "izq": css.Append("text-align:left;"); break;
                        case "center": case "centro": case "centrado": css.Append("text-align:center;"); break;
                        case "right": case "derecha": case "der": css.Append("text-align:right;"); break;
                        case "bold": case "negrita": case "b": css.Append("font-weight:600;"); break;
                        case "italic": case "italica": case "cursiva": case "i": css.Append("font-style:italic;"); break;
                        case "underline": case "subrayado": case "u": css.Append("text-decoration:underline;"); break;
                        case "big": case "grande": css.Append("font-size:1.3em;"); break;
                        case "small": case "pequeno": case "chico": css.Append("font-size:.85em;"); break;
                        case "black": case "negro": css.Append("color:#000;"); break;
                        case "red": case "rojo": css.Append("color:#c0392b;"); break;
                        case "blue": case "azul": css.Append("color:#2c6fbb;"); break;
                        case "green": case "verde": css.Append("color:#1a7a4c;"); break;
                        case "gray": case "grey": case "gris": css.Append("color:#777;"); break;
                        case "orange": case "naranja": css.Append("color:#d35400;"); break;
                        case "purple": case "morado": css.Append("color:#7c2bb2;"); break;
                        case "white": case "blanco": css.Append("color:#fff;"); break;
                        default:
                            var h = raw.Trim();
                            if (h.StartsWith("#") && (h.Length == 4 || h.Length == 7)) css.Append($"color:{h};");
                            break;
                    }
                }
                return css.ToString();
            }

            // Inner statements (dentro de for/while/if/switch/try): emitir como
            // <p class="line indent"> con leve indentación visual y data-line para click→nav
            _evaluator.InnerStmtOut = (innerStmt, innerRes) =>
            {
                int innerLine = innerStmt?.Line ?? 0;
                // Se construye el chunk de ESTE statement interno y se emite enseguida:
                //   * StreamingMode (WPF) -> StatementCompleted EN VIVO, por iteracion
                //     (el usuario ve iter 1, iter 2... a medida que calcula).
                //   * Sino (CLI/export) -> a htmlBuffer (batch, se vuelca tras el loop).
                var chunk = new StringBuilder();
                // (A) Salida de I/O (fprintf/disp) producida por ESTE statement.
                if (dispBuffer.Length > 0)
                {
                    var dispRawI = dispBuffer.ToString().TrimEnd();
                    dispRawI = ConvertPrimes(dispRawI);   // N'' -> N″ antes del typeset
                    if (renderDisp) dispRawI = RenderDispInline(dispRawI);
                    var dispProcessedI = RenderDispWithMatrices(dispRawI);
                    var encodedI = EncodeWithHtmlSegments(dispProcessedI);
                    var stretchedI = StretchInlineBrackets(encodedI);
                    chunk.Append($"<p class=\"line\" id=\"line-{innerLine}\" style=\"margin-left:1.5em;color:#555\"><span class=\"eq\"><span style=\"white-space:pre-wrap\">{stretchedI}</span></span></p>\n");
                    dispBuffer.Clear();
                }
                // (B) Echo del statement interno (NO suprimido, NO comentario, NO void).
                //     Los comentarios dentro de loops se omiten (sino se repiten por iter).
                if (!innerRes.Suppressed && !(innerStmt is CommentStmt) && !IsVoidStatement(innerStmt))
                {
                    chunk.Append($"<p class=\"line\" id=\"line-{innerLine}\" style=\"margin-left:1.5em;color:#555\">");
                    chunk.Append(MatlabHtmlWriter.RenderStatement(innerStmt, innerRes));
                    chunk.Append("</p>\n");
                }
                if (chunk.Length == 0) return;
                if (StreamingMode && StatementCompleted != null)
                {
                    sb.Append(chunk);                                  // persistir en el doc final
                    StatementCompleted.Invoke(innerLine, chunk.ToString()); // emitir EN VIVO (por iteración)
                    pendingChunkStart = sb.Length;                     // ya emitido: el flush top-level no lo repite
                }
                else
                {
                    htmlBuffer.Append(chunk);                          // batch (CLI/export)
                }
            };
            // Pre-pass: regla Calcpad-Lab para multi-stmt en una linea fuente.
            // Si `a=1; b=2; c=3` esta todo en una linea, el `;` FINAL (despues de c)
            // determina si TODOS los stmts de esa linea se muestran. Es decir,
            // override del Suppressed individual: todos heredan el Suppressed del
            // ULTIMO stmt no-comment de la linea. Esto desvia de MATLAB (que aplica
            // `;` per-stmt) pero matchea la expectativa del usuario.
            {
                var byLine = new System.Collections.Generic.Dictionary<int, System.Collections.Generic.List<int>>();
                for (int i = 0; i < stmts.Count; i++)
                {
                    if (stmts[i] is CommentStmt) continue;
                    int ln = stmts[i]?.Line ?? 0;
                    if (ln <= 0) continue;
                    if (!byLine.TryGetValue(ln, out var lst)) byLine[ln] = lst = new System.Collections.Generic.List<int>();
                    lst.Add(i);
                }
                foreach (var kv in byLine)
                {
                    if (kv.Value.Count < 2) continue;
                    // El "ultimo" es el ultimo Assignment/ExprStmt de la linea. Un bloque (for/if/while) al
                    // final de la linea NO cuenta: `c=cell(1,2); for k=1:2; c{k}=k; end` des-suprimia el
                    // `c=cell(1,2);` y echaba "c = [0x0 matrix] [0x0 matrix]" (2026-09-05).
                    int lastIdx = -1;
                    foreach (var idx in kv.Value) if (stmts[idx] is Assignment || stmts[idx] is ExprStmt) lastIdx = idx;
                    if (lastIdx < 0) continue;
                    bool lastSup = GetSuppressed(stmts[lastIdx]);
                    foreach (var idx in kv.Value)
                        SetSuppressed(stmts[idx], lastSup);
                }
                static bool GetSuppressed(MatlabNode n) =>
                    n is Assignment a ? a.Suppressed : (n is ExprStmt e ? e.Suppressed : false);
                static void SetSuppressed(MatlabNode n, bool v)
                { if (n is Assignment a) a.Suppressed = v; else if (n is ExprStmt e) e.Suppressed = v; }
            }

            // Tracking para comentarios inline (mismo line# que stmt previo no-comment):
            //   - Si el stmt previo NO fue suprimido por `;` → comentario se rendea
            //     como caption SIN `%` al frente.
            //   - Si el stmt previo SI fue suprimido (`;`) → el comentario tambien
            //     se suprime (no hay output al que adjuntarlo).
            //   - Si el comentario esta en su propia linea → comportamiento default
            //     (con `%`).
            int prevNonCommentLine = -1;
            bool prevWasSuppressed = false;
            // Tracking del line# del ultimo <p> emitido a `sb`. Usado por las
            // captions inline para decidir si pegar dentro del mismo <p> (cuando
            // matchea la linea) o emit standalone.
            int lastEmittedPLine = -1;
            // Streaming buffering por linea-fuente: acumulamos todos los stmts
            // que comparten line# en un solo chunk para que la logica de merge
            // (inline-comment / multi-stmt) pueda mutar `sb` antes de enviar al
            // UI. (pendingChunkStart/pendingChunkLine se declararon arriba para que
            // InnerStmtOut pueda avanzar la marca al emitir chunks por iteración.)
            pendingChunkStart = sb.Length;
            pendingChunkLine = -1;

            // Bloque Markdown  % #md … % #endmd : acumula los comentarios intermedios
            // y los renderiza como Markdown (encabezados #/##, **negrita**, *cursiva*,
            // tablas |...|, listas -). El codigo entre medias se ejecuta igual.
            bool mdMode = false;
            bool insideDeqBlock = false;   // bloque de ecuaciones: %#deq … %#endeq
            bool insideColsBlock = false;  // bloque de columnas: %#cols … %#endcols
            var colsRows = new System.Collections.Generic.List<System.Collections.Generic.List<string>>();
            int colsCurLine = -1;
            string colsHeaders = null;
            // Lineas del fuente (para el bloque %#deq…%#endeq con CODIGO REAL adentro).
            var deqSrcLines = source.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n');
            // Contadores para elementos tipo LIBRO (auto-numerados): citas [N], figuras, tablas, notas.
            int refCounter = 0, figCounter = 0, tabCounter = 0, notaCounter = 0;
            // Emite UNA ecuacion #deq (usado por la forma de una linea y por el bloque %#deq…%#endeq).
            void EmitDeqLine(string body, int line)
            {
                string label = null;
                int at = body.IndexOf("@@", System.StringComparison.Ordinal);
                if (at >= 0)
                {
                    label = body[(at + 2)..].Trim();
                    if (label.StartsWith("(") && label.EndsWith(")")) label = label[1..^1].Trim();
                    body = body[..at].Trim();
                }
                var eqHtml = MatlabHtmlWriter.RenderEquation(body);
                if (label != null)
                    sb.Append($"<p class=\"line\" id=\"line-{line}\" style=\"display:flex;justify-content:space-between;align-items:center;margin:.45em 0\"><span class=\"eq\">{eqHtml}</span><span style=\"font-family:Georgia,serif;color:#555;margin-left:2em;white-space:nowrap\">({System.Net.WebUtility.HtmlEncode(label)})</span></p>\n");
                else
                    sb.Append($"<p class=\"line\" id=\"line-{line}\" style=\"text-align:center;margin:.45em 0\"><span class=\"eq\">{eqHtml}</span></p>\n");
            }
            var mdBuf = new System.Collections.Generic.List<string>();
            void FlushMd()
            {
                // Un bloque Markdown es salida visible: la figura que siga abierta va ANTES
                // (misma regla que el texto %' y los resultados, más abajo en el bucle).
                if (mdBuf.Count > 0 && !hidden && !animFrameStreamed
                    && MatlabPlots.HasOpenFigure && !MatlabPlots.SubplotActive)
                {
                    var figHtml = MatlabPlots.FinishFigure();
                    if (!string.IsNullOrEmpty(figHtml)) sb.Append(figHtml);
                }
                if (mdBuf.Count > 0) sb.Append(MarkdownToHtml(mdBuf)).Append('\n');
                mdBuf.Clear();
                mdMode = false;
            }
            // Emite el bloque de COLUMNAS acumulado (%#cols…%#endcols): filas col-blk con
            // celdas col-cell (mecanismo nativo de Calcpad = una fila line-tracked, columnas
            // inline; NO párrafos por ecuación, así no se apilan). Encabezados opcionales.
            void EmitColsBlock()
            {
                if (!string.IsNullOrEmpty(colsHeaders))
                {
                    sb.Append("<div class=\"col-blk\">");
                    foreach (var h in colsHeaders.Split('|', ';'))
                        sb.Append($"<div class=\"col-cell\"><b>{System.Net.WebUtility.HtmlEncode(h.Trim())}</b></div>");
                    sb.Append("</div>\n");
                }
                foreach (var row in colsRows)
                {
                    sb.Append("<div class=\"col-blk\">");
                    foreach (var cell in row) sb.Append($"<div class=\"col-cell\">{cell}</div>");
                    sb.Append("</div>\n");
                }
                colsRows.Clear(); colsCurLine = -1; colsHeaders = null;
            }

            // Pre-calculo de escalares (datos de entrada) para @nombre/@{expr} en lineas %' que van
            // ANTES de la definicion. Seguro: no ejecuta graficas/FEM (ver BuildPreviewVars).
            BuildPreviewVars(stmts);

            // Paso de sustitucion Calcpad-style (n_e = n_a·n_b = 6·4 = 24): el HtmlWriter consulta
            // el valor ACTUAL de cada variable desde Globals (se actualiza al ejecutar cada stmt).
            MatlabHtmlWriter.VarValueProvider = name =>
                _evaluator.Globals.Vars.TryGetValue(name, out var vv) ? vv : null;

            foreach (var stmt in stmts)
            {
                int stmtLine = stmt?.Line ?? 0;

                // Directiva de bloque  % #hide … % #show  (estilo Calcpad puro):
                // oculta el RENDER de todo lo que sigue (resultados, fprintf, disp, plots)
                // pero lo sigue EJECUTANDO. La directiva misma no se muestra.
                if (stmt is CommentStmt cdir0 && !cdir0.IsHeading)
                {
                    var dt0 = cdir0.Text.Trim();
                    if (dt0 == "#hide") { hidden = true; continue; }
                    if (dt0 == "#show") { hidden = false; continue; }
                    // Toggle de bloque para la transliteración de griegas (default ON).
                    // % #nogreek → los nombres (nu, phi…) quedan como texto literal.
                    // % #greek   → vuelve a mostrarlos como símbolo (ν, φ…).
                    if (dt0 == "#nogreek") { MatlabHtmlWriter.GreekAutoRender = false; continue; }
                    if (dt0 == "#greek")   { MatlabHtmlWriter.GreekAutoRender = true;  continue; }
                    // Toggle de bloque para el render de fprintf/disp (default plano).
                    // % #render → el texto impreso se renderiza (griegas). % #plain → texto plano.
                    if (dt0 == "#render") { renderDisp = true;  continue; }
                    if (dt0 == "#plain")  { renderDisp = false; continue; }
                    // Bloque Markdown: #md abre (o cierra si ya estaba), #endmd cierra.
                    if (dt0 == "#md")    { if (mdMode) FlushMd(); else { mdMode = true; mdBuf.Clear(); } continue; }
                    if (dt0 == "#endmd") { FlushMd(); continue; }
                    // Bloque de ECUACIONES: `%#deq` solo (sin ecuacion) abre; `%#endeq`/`%#end deq` cierra.
                    // Adentro, cada linea de comentario `%ecuacion @@(n)` se renderiza como #deq.
                    if (dt0 == "#deq") { insideDeqBlock = true; continue; }
                    if (dt0 == "#endeq" || dt0 == "#end deq") { insideDeqBlock = false; continue; }
                    // Bloque de COLUMNAS: `%#cols [Enc1 | Enc2 | …]` abre; `%#endcols` cierra.
                    // Adentro, cada linea son referencias a variables ya calculadas separadas por `;`
                    // (MATLAB las evalua sin error); Hekatan las pinta en columnas (Base | 1a | 2a).
                    if (dt0 == "#endcols" || dt0 == "#end cols") { EmitColsBlock(); insideColsBlock = false; continue; }
                    if (dt0.StartsWith("#cols", System.StringComparison.Ordinal))
                    {
                        insideColsBlock = true; colsRows.Clear(); colsCurLine = -1;
                        var hh = dt0.Length > 5 ? dt0.Substring(5).Trim() : "";
                        colsHeaders = hh.Length > 0 ? hh : null;
                        continue;
                    }
                }

                // Dentro del bloque de ecuaciones %#deq … %#endeq:
                if (insideDeqBlock)
                {
                    // (a) comentario de ecuacion: `%sigma = ... @@(n)` (sin repetir #deq)
                    if (stmt is CommentStmt cdeqBlk && !cdeqBlk.IsHeading)
                    {
                        // Si es el comentario INLINE (@@) que sigue a una linea de codigo real ya
                        // renderizada en este bloque (misma linea), saltarlo (ya se numero).
                        if (stmtLine == lastEmittedPLine) continue;
                        var eqLine = cdeqBlk.Text.Trim();
                        if (eqLine.Length > 0) { EmitDeqLine(eqLine, stmtLine); lastEmittedPLine = stmtLine; }
                        continue;
                    }
                    // (b) CODIGO REAL (variable fuera de %): se EJECUTA (queda definida) y se
                    //     muestra como ECUACION display. El numero opcional viene del %@@(n) inline.
                    if (!(stmt is CommentStmt))
                    {
                        try { _evaluator.ExecuteOne(stmt, _evaluator.Globals); } catch { }
                        string raw = (stmtLine >= 1 && stmtLine <= deqSrcLines.Length) ? deqSrcLines[stmtLine - 1] : "";
                        // separar codigo del comentario inline (que puede llevar @@(n))
                        int pct = raw.IndexOf('%');
                        string codePart = pct >= 0 ? raw[..pct] : raw;
                        string cmtPart = pct >= 0 ? raw[(pct + 1)..].Trim() : "";
                        string lbl = null;
                        if (cmtPart.StartsWith("#deq")) cmtPart = cmtPart.Substring(4).Trim();
                        if (cmtPart.StartsWith("@@"))
                        {
                            lbl = cmtPart.Substring(2).Trim();
                            if (lbl.StartsWith("(") && lbl.EndsWith(")")) lbl = lbl[1..^1].Trim();
                        }
                        var body = codePart.Trim().TrimEnd(';').Trim();
                        if (lbl != null) body += " @@(" + lbl + ")";
                        if (body.Length > 0) { EmitDeqLine(body, stmtLine); lastEmittedPLine = stmtLine; }
                        prevNonCommentLine = stmtLine;
                        continue;
                    }
                }

                // Dentro del bloque de COLUMNAS %#cols … %#endcols: cada sentencia es una
                // referencia a una variable ya calculada; se ejecuta (queda consistente) y su
                // render (name = value) va a una CELDA. Sentencias de la MISMA linea del fuente
                // = misma FILA (col-blk). Comentarios sueltos adentro se ignoran.
                if (insideColsBlock)
                {
                    if (stmt is CommentStmt) continue;
                    StatementResult resC = default;
                    try { resC = _evaluator.ExecuteOne(stmt, _evaluator.Globals); } catch { }
                    var cellHtml = MatlabHtmlWriter.RenderStatement(stmt, resC) ?? "";
                    cellHtml = System.Text.RegularExpressions.Regex.Replace(cellHtml, @"^\s*<p[^>]*>", "");
                    int endp = cellHtml.LastIndexOf("</p>", System.StringComparison.Ordinal);
                    if (endp >= 0) cellHtml = cellHtml.Substring(0, endp);
                    cellHtml = cellHtml.Trim();
                    if (cellHtml.Length == 0) continue;
                    if (stmtLine != colsCurLine) { colsRows.Add(new System.Collections.Generic.List<string>()); colsCurLine = stmtLine; }
                    colsRows[colsRows.Count - 1].Add(cellHtml);
                    continue;
                }

                // En modo Markdown: los comentarios se acumulan; cualquier statement
                // no-comentario cierra el bloque y se procesa normal.
                if (mdMode)
                {
                    if (stmt is CommentStmt csMd && !csMd.IsHeading) { mdBuf.Add(SubstituteMdVarsPlain(csMd.Text)); continue; }
                    FlushMd();
                }

                // Decision temprana sobre inline-comment: necesita conocer el
                // stmt previo no-comment.
                bool isInlineComment = stmt is CommentStmt csInline
                                       && !csInline.IsHeading
                                       && !csInline.Text.StartsWith("--")
                                       && prevNonCommentLine >= 0
                                       && stmtLine == prevNonCommentLine;
                // Regla Calcpad para comentarios INLINE (mismo renglón que un stmt):
                //   `x = 4 %texto`   → comentario de código OCULTO (no se muestra).
                //   `x = 4 %'texto`  → anotación VISIBLE (el `'` = marcador de texto
                //                       Calcpad; se muestra sin el apóstrofo).
                // Los comentarios en su PROPIA línea no se ven afectados por esto.
                bool inlineShown = isInlineComment
                                   && (((CommentStmt)stmt).Text.TrimStart().StartsWith("'")
                                       // Numeración de ecuación sobre la línea REAL:  x = k*u  %#deq@@(2.1)
                                       // (o simplemente %@@(2.1)). La ecuación se calcula normal y el
                                       // número se pega a la derecha, sin re-escribir la ecuación.
                                       || ((CommentStmt)stmt).Text.TrimStart().StartsWith("#deq")
                                       || ((CommentStmt)stmt).Text.TrimStart().StartsWith("@@"));
                if (isInlineComment && (prevWasSuppressed || !inlineShown))
                {
                    // Oculto: skipear sin ejecutar ni alterar el tracking
                    continue;
                }

                // Streaming: si cambiamos de linea-fuente, flushear chunk pendiente
                // (todos los stmts de la linea anterior ya rendearon a `sb`).
                if (StreamingMode && pendingChunkLine != -1
                    && pendingChunkLine != stmtLine
                    && sb.Length > pendingChunkStart
                    && StatementCompleted != null)
                {
                    var pending = sb.ToString(pendingChunkStart, sb.Length - pendingChunkStart);
                    StatementCompleted.Invoke(pendingChunkLine, pending);
                    pendingChunkStart = sb.Length;
                }
                pendingChunkLine = stmtLine;
                StatementStarting?.Invoke(stmtLine);
                // Figura abierta ANTES de esta sentencia (para volcarla en su sitio, ver abajo).
                int sbAntesFig = sb.Length;
                object figAntes = MatlabPlots.HasOpenFigure && !MatlabPlots.SubplotActive ? MatlabPlots.FigureToken : null;
                try {

                StatementResult result;
                try { result = _evaluator.ExecuteOne(stmt, _evaluator.Globals); }
                catch (MatlabRuntimeException ex)
                {
                    sb.Append($"<p class=\"err\" id=\"line-{stmtLine}\">Error: {System.Net.WebUtility.HtmlEncode(ex.Message)} (line {LineLink(stmtLine)})</p>\n");
                    continue;
                }
                catch (Exception ex)
                {
                    sb.Append($"<p class=\"err\" id=\"line-{stmtLine}\">Internal error: {System.Net.WebUtility.HtmlEncode(ex.Message)} (line {LineLink(stmtLine)})</p>\n");
                    continue;
                }
                // Flush disp buffer.
                // Importante: NO usar <pre> (fuerza monospace del navegador y rompe
                // el Georgia Pro del template, además anula los colores `.eq var`,
                // `.eq i`, `.eq sub` definidos en template.html). Usar un <span>
                // con white-space:pre-wrap para preservar los espacios sin perder
                // la familia tipográfica heredada de .eq.
                if (dispBuffer.Length > 0)
                {
                    var dispRaw = dispBuffer.ToString().TrimEnd();
                    dispRaw = ConvertPrimes(dispRaw);   // N'' -> N″ ANTES del typeset (si no, la ' queda tras un tag)
                    if (renderDisp) dispRaw = RenderDispInline(dispRaw);
                    var dispProcessed = RenderDispWithMatrices(dispRaw);
                    var encoded = EncodeWithHtmlSegments(dispProcessed);
                    var stretched = StretchInlineBrackets(encoded);
                    sb.Append($"<p class=\"line\" id=\"line-{stmtLine}\"><span class=\"eq\"><span style=\"white-space:pre-wrap\">{stretched}</span></span></p>\n");
                    lastEmittedPLine = stmtLine;
                    dispBuffer.Clear();
                }
                // Render del statement (incluye el comando como fórmula)
                // NO renderizar void functions (fprintf/disp/figure/plot/...) — solo su side effect.
                // Comentarios en LINEA PROPIA: OCULTOS por defecto (como MATLAB). Visibles solo si:
                //   %% encabezado · % 'texto (caption) · % #noc/#val/#equ (formula tipografiada).
                // (#md/#hide/#plain/#greek ya se consumieron antes; los inline visibles llevan
                //  isInlineComment y no pasan por aqui.)
                bool isHiddenComment = false;
                if (stmt is CommentStmt csHide && !isInlineComment)
                {
                    var ct = csHide.Text.TrimStart();
                    // `%%` (SectionHeading) es como en MATLAB: comentario de sección OCULTO,
                    // no produce salida. Texto/línea VISIBLE = `%' …`; TÍTULO VISIBLE = `%" …`.
                    bool visible = ct.StartsWith("'") || ct.StartsWith("\"")
                                   || ct.StartsWith("#noc") || ct.StartsWith("#val") || ct.StartsWith("#equ")
                                   || ct.StartsWith("#img")   // % #img <data-uri|ruta> → imagen incrustada (recorte pegado)
                                   || ct.StartsWith("#deq")   // % #deq ecuacion @@(numero) → ecuacion numerada tipo libro
                                   || ct.StartsWith("#col") || ct.StartsWith("#inl")  // % #col a ; b ; c → columnas
                                   || ct.StartsWith("#cen")   // % #cen texto/ecuacion → centrado
                                   || ct.StartsWith("#pgb")   // % #pgb → salto de pagina (impresion/PDF)
                                   || ct.StartsWith("#margen")// % #margen [N] / #endmargen → bloque con margenes justificado
                                   || ct.StartsWith("#endmargen")
                                   || ct.StartsWith("#cita")  // % #cita ... → entrada de bibliografia [N] auto-numerada
                                   || ct.StartsWith("#fig")   // % #fig ... → pie de figura "Figura N. ..."
                                   || ct.StartsWith("#tab")   // % #tab ... → pie de tabla "Tabla N. ..."
                                   || ct.StartsWith("#nota")  // % #nota ... → nota al pie (pequena)
                                   || ct.StartsWith("#ref")   // % #ref ... → referencia cruzada "(ver ...)"
                                   // Operador Calcpad directo `% $Plot/$Sum/$Area/...` → visible (se
                                   // renderiza en modo #equ). Antes quedaba oculto pese a que
                                   // ParseDirective lo soporta; así funcionan con solo `% $Op{...}`.
                                   || ct.StartsWith("$");
                    isHiddenComment = !visible;
                }
                if (!result.Suppressed && !IsVoidStatement(stmt) && !isHiddenComment && !hidden)
                {
                    try
                    {
                        // Directiva Calcpad escondida en `% #deq ...` — typeset via
                        // el ExpressionParser de Calcpad-puro. MATLAB 2017a la ignora
                        // (comentario); acá enriquece el reporte sin tocar números.
                        if (stmt is CommentStmt csCap && !csCap.IsHeading && !isInlineComment
                            && (csCap.Text.TrimStart().StartsWith("'") || csCap.Text.TrimStart().StartsWith("\"")))
                        {
                            // Marcadores de comentario visible + FORMATO DE TEXTO (esquema Jorge):
                            //   %"  Titulo   -> TITULO centrado (negrita, acento)
                            //   %'-----      -> LINEA divisoria (SOLO guiones/iguales >=2)
                            //   %'< texto  -> izquierda   %'| texto -> centrado   %'> texto -> derecha
                            //   %'* texto  -> negrita     %'/ texto -> italica    %'_ texto -> subrayado
                            //   %'\ texto  -> ESCAPE (literal, para usar < > | * / _ o ----- como texto)
                            //   %'{color,align,estilo} texto  -> BLOQUE de atributos (el "codigo
                            //       dentro de %"): color (rojo/azul/verde/negro/gris/naranja/morado
                            //       o #hex), alineacion, negrita/italica/subrayado, grande/pequeno.
                            //       Vale en titulos: %"{negro} Titulo -> titulo negro.
                            //   %'  texto  -> texto normal (izquierda)   (combinables: %'>* = derecha+negrita)
                            // El apostrofo/comilla inicial NO se muestra (marcador, como en Calcpad).
                            var capRaw = csCap.Text.TrimStart();
                            char marker = capRaw[0];                       // ' o "
                            var capText = capRaw.Length > 1 ? capRaw.Substring(1) : "";
                            var capTrim = capText.Trim();
                            var t0 = capText.TrimStart();
                            if (t0.StartsWith("<") && t0.Contains(">"))
                            {
                                // HTML CRUDO: %'<table>…</table>, %'<svg>…, %'<b>…  -> se renderiza
                                // tal cual (tablas, figuras, etc.). Va dentro de un comentario, asi
                                // que MATLAB lo ignora y el .m no da error de char-array.
                                // Si ADEMAS lleva @nombre/@{expr}, se sustituyen los valores SIN escapar
                                // las etiquetas (asi "<b>γ =</b> @{g1}" muestra el valor y conserva el HTML).
                                sb.Append(t0.IndexOf('@') >= 0 ? RenderInlineVarRefsRaw(t0) : t0);
                                sb.Append('\n');
                                lastEmittedPLine = stmtLine;
                            }
                            else if (t0.StartsWith("{") && t0.Contains("}"))
                            {
                                // BLOQUE DE ATRIBUTOS: %'{color,align,estilo} texto  (y %"{...}).
                                // El "codigo dentro de %" para pedir todo el formato facil.
                                int close = t0.IndexOf('}');
                                var css = AttrsToCss(t0.Substring(1, close - 1));
                                var rest = t0.Substring(close + 1).Trim();
                                // %" da base de titulo (centrado+negrita+acento); los attrs la
                                // sobreescriben (ej. {negro} -> color negro; {left} -> izquierda).
                                string baseCss = marker == '"'
                                    ? "text-align:center;font-weight:600;color:#111;letter-spacing:.3px;" : "";
                                var enc = System.Net.WebUtility.HtmlEncode(rest);
                                sb.Append($"<p class=\"line\" id=\"line-{stmtLine}\"><span class=\"eq\" style=\"display:block;{baseCss}{css}\">{enc}</span></p>\n");
                                lastEmittedPLine = stmtLine;
                            }
                            else if (capText.TrimStart().StartsWith("\\"))
                            {
                                // ESCAPE: `%'\...` -> mostrar el resto LITERAL, sin interpretar
                                // formato ni linea. Para usar < > | * / o ----- como texto.
                                var lit = capText.TrimStart().Substring(1);
                                var enc = System.Net.WebUtility.HtmlEncode(lit);
                                sb.Append($"<p class=\"line\" id=\"line-{stmtLine}\"><span class=\"eq\">{enc}</span></p>\n");
                                lastEmittedPLine = stmtLine;
                            }
                            else if (System.Text.RegularExpressions.Regex.IsMatch(capTrim, @"^[-=]{2,}$"))
                            {
                                // LINEA: %'-----  (SOLO guiones/iguales) -> divisor a lo ancho.
                                sb.Append($"<p class=\"line\" id=\"line-{stmtLine}\"><span style=\"display:block;border-top:1.5px solid #bbb;height:0;margin:7px 0;\"></span></p>\n");
                                lastEmittedPLine = stmtLine;
                            }
                            else
                            {
                                // FORMATO: BASE segun marcador + prefijos < > | * / _ (combinables) que la sobreescriben.
                                //   %"  -> TITULO por defecto: centrado, negrita, NEGRO.
                                //   %"< -> SUBTITULO a la izquierda (misma negrita/negro, sin centrar).  %"> derecha.
                                //   %'  -> texto normal (izquierda).
                                //   Prefijos: < izq · > der · | centro · * negrita · / italica · _ subrayado.
                                var t = capText.TrimStart();
                                string baseStyle = marker == '"'
                                    ? "text-align:center;font-weight:600;color:#111;letter-spacing:.3px;margin:4px 0;" : "";
                                string style = "";
                                bool more = true;
                                while (more && t.Length > 0)
                                {
                                    switch (t[0])
                                    {
                                        case '<': style += "text-align:left;"; break;
                                        case '>': style += "text-align:right;"; break;
                                        case '|': style += "text-align:center;"; break;
                                        case '*': style += "font-weight:600;"; break;
                                        case '/': style += "font-style:italic;"; break;
                                        case '_': style += "text-decoration:underline;"; break;
                                        default: more = false; break;
                                    }
                                    if (more) t = t.Substring(1);
                                }
                                // El prefijo va DESPUES de baseStyle -> en text-align gana el prefijo (subtitulo izq).
                                string finalStyle = baseStyle + style;
                                // @nombre / @{expr} → ecuación inline (combinar variables en la línea visible).
                                // Texto con HTML inline (<b>…</b>, entidades &xi;) + tokens (N_1→N₁, N''→N″):
                                // se aplican subíndices/primas y luego se sustituye @ SIN escapar las etiquetas,
                                // para que el HTML incrustado a mitad de línea se renderice (no salga literal).
                                // Subíndices SOLO en el texto: antes se convertía la línea entera y
                                // «@v_max» llegaba como «vₘₐₓ» → no era variable → salía el nombre.
                                var enc = RenderInlineVarRefsRaw(t.Trim(), s => ConvertSubscriptsHtml(ConvertPrimes(s), encode: false));
                                if (finalStyle.Length > 0)
                                    sb.Append($"<p class=\"line\" id=\"line-{stmtLine}\"><span class=\"eq\" style=\"display:block;{finalStyle}\">{enc}</span></p>\n");
                                else
                                    sb.Append($"<p class=\"line\" id=\"line-{stmtLine}\"><span class=\"eq\">{enc}</span></p>\n");
                                lastEmittedPLine = stmtLine;
                            }
                        }
                        // % #img <data-uri | ruta> → imagen incrustada (recorte pegado). MATLAB la ignora
                        // (es comentario) y el .m queda autocontenido cuando el src es un data:...base64.
                        else if (stmt is CommentStmt cimg && !cimg.IsHeading && !isInlineComment
                            && cimg.Text.TrimStart().StartsWith("#img", System.StringComparison.OrdinalIgnoreCase))
                        {
                            var isrc = cimg.Text.TrimStart().Substring(4).Trim();
                            if (isrc.Length > 0)
                            {
                                sb.Append($"<div style=\"text-align:center;margin:6px 0;\"><img src=\"{isrc}\" style=\"max-width:100%;height:auto;\" alt=\"imagen\"></div>\n");
                                lastEmittedPLine = stmtLine;
                            }
                        }
                        // % #deq  ECUACION @@(numero)  → ecuacion numerada tipo LIBRO: la
                        // ecuacion renderizada como math a la izquierda y el (numero/enunciado)
                        // a la derecha. Display-only (MATLAB la ignora, es comentario).
                        //   % #deq F = k*u @@(2.1)      % #deq sigma = P/A_g + M*v/I @@(flexo-compresion)
                        else if (stmt is CommentStmt cdeq && !cdeq.IsHeading && !isInlineComment
                            && cdeq.Text.TrimStart().StartsWith("#deq", System.StringComparison.OrdinalIgnoreCase))
                        {
                            EmitDeqLine(cdeq.Text.TrimStart().Substring(4).Trim(), stmtLine);
                            lastEmittedPLine = stmtLine;
                        }
                        // % #col a ; b ; c   (o #inl) → COLUMNAS: fila flex de celdas iguales
                        // separadas por ';'. Celda con `'` = texto; sin `'` = ecuacion/expresion.
                        else if (stmt is CommentStmt ccol && !ccol.IsHeading && !isInlineComment
                            && (ccol.Text.TrimStart().StartsWith("#col", System.StringComparison.OrdinalIgnoreCase)
                                || ccol.Text.TrimStart().StartsWith("#inl", System.StringComparison.OrdinalIgnoreCase)))
                        {
                            var body = ccol.Text.TrimStart().Substring(4).Trim();
                            var cells = SplitTopLevel(body, ';');
                            var cb = new System.Text.StringBuilder();
                            cb.Append($"<div class=\"line\" id=\"line-{stmtLine}\" style=\"display:flex;gap:1.6em;align-items:baseline;margin:.4em 0\">");
                            foreach (var cell in cells)
                            {
                                var cc = cell.Trim();
                                string inner;
                                if (cc.StartsWith("'"))
                                    inner = System.Net.WebUtility.HtmlEncode(cc[1..].Trim());   // texto literal
                                else if (cc.Length == 0)
                                    inner = "&nbsp;";
                                else
                                    inner = $"<span class=\"eq\">{MatlabHtmlWriter.RenderEquation(cc)}</span>";  // ecuacion/expresion
                                cb.Append($"<div style=\"flex:1\">{inner}</div>");
                            }
                            cb.Append("</div>\n");
                            sb.Append(cb);
                            lastEmittedPLine = stmtLine;
                        }
                        // % #cen texto/ecuacion → CENTRADO. Celda con `'` = texto; sin `'` = ecuacion.
                        else if (stmt is CommentStmt ccen && !ccen.IsHeading && !isInlineComment
                            && ccen.Text.TrimStart().StartsWith("#cen", System.StringComparison.OrdinalIgnoreCase))
                        {
                            var body = ccen.Text.TrimStart().Substring(4).Trim();
                            string inner = body.StartsWith("'")
                                ? System.Net.WebUtility.HtmlEncode(body[1..].Trim())
                                : $"<span class=\"eq\">{MatlabHtmlWriter.RenderEquation(body)}</span>";
                            sb.Append($"<p class=\"line\" id=\"line-{stmtLine}\" style=\"text-align:center;margin:.45em 0\">{inner}</p>\n");
                            lastEmittedPLine = stmtLine;
                        }
                        // % #pgb → SALTO DE PAGINA (para impresion / PDF).
                        else if (stmt is CommentStmt cpgb && !cpgb.IsHeading && !isInlineComment
                            && cpgb.Text.TrimStart().StartsWith("#pgb", System.StringComparison.OrdinalIgnoreCase))
                        {
                            sb.Append("<div class=\"pgb\" style=\"page-break-after:always;break-after:page;height:0\"></div>\n");
                            lastEmittedPLine = stmtLine;
                        }
                        // % #endmargen → cierra el bloque de margenes.
                        else if (stmt is CommentStmt cendm && !cendm.IsHeading && !isInlineComment
                            && cendm.Text.TrimStart().StartsWith("#endmargen", System.StringComparison.OrdinalIgnoreCase))
                        {
                            sb.Append("</div>\n");
                            lastEmittedPLine = stmtLine;
                        }
                        // % #margen [N] → abre bloque con margenes de N mm (default 15), texto justificado.
                        else if (stmt is CommentStmt cmar && !cmar.IsHeading && !isInlineComment
                            && cmar.Text.TrimStart().StartsWith("#margen", System.StringComparison.OrdinalIgnoreCase))
                        {
                            var mbody = cmar.Text.TrimStart().Substring(7).Trim();
                            var mv = string.IsNullOrEmpty(mbody) ? "15" : mbody;
                            sb.Append($"<div style=\"margin:0 auto;padding:5mm {mv}mm;max-width:calc(190mm - {mv}mm - {mv}mm);text-align:justify;line-height:160%\">\n");
                            lastEmittedPLine = stmtLine;
                        }
                        // % #cita TEXTO → entrada de bibliografia [N] (auto-numerada, sangria colgante).
                        else if (stmt is CommentStmt ccita && !ccita.IsHeading && !isInlineComment
                            && ccita.Text.TrimStart().StartsWith("#cita", System.StringComparison.OrdinalIgnoreCase))
                        {
                            var txt = System.Net.WebUtility.HtmlEncode(ccita.Text.TrimStart().Substring(5).Trim());
                            refCounter++;
                            sb.Append($"<p class=\"line\" id=\"line-{stmtLine}\" style=\"text-indent:-2.2em;margin-left:2.2em;font-family:Georgia,serif;font-size:.93em;color:#333\">[{refCounter}]&ensp;{txt}</p>\n");
                            lastEmittedPLine = stmtLine;
                        }
                        // % #fig TEXTO → pie de figura "Figura N. TEXTO" (centrado, auto-numerado).
                        else if (stmt is CommentStmt cfig && !cfig.IsHeading && !isInlineComment
                            && cfig.Text.TrimStart().StartsWith("#fig", System.StringComparison.OrdinalIgnoreCase))
                        {
                            var txt = System.Net.WebUtility.HtmlEncode(cfig.Text.TrimStart().Substring(4).Trim());
                            figCounter++;
                            sb.Append($"<p class=\"line\" id=\"line-{stmtLine}\" style=\"text-align:center;font-size:.9em;color:#444;margin:.3em 0\"><b>Figura {figCounter}.</b>&ensp;{txt}</p>\n");
                            lastEmittedPLine = stmtLine;
                        }
                        // % #tab TEXTO → pie de tabla "Tabla N. TEXTO" (auto-numerado).
                        else if (stmt is CommentStmt ctab && !ctab.IsHeading && !isInlineComment
                            && ctab.Text.TrimStart().StartsWith("#tab", System.StringComparison.OrdinalIgnoreCase))
                        {
                            var txt = System.Net.WebUtility.HtmlEncode(ctab.Text.TrimStart().Substring(4).Trim());
                            tabCounter++;
                            sb.Append($"<p class=\"line\" id=\"line-{stmtLine}\" style=\"text-align:center;font-size:.9em;color:#444;margin:.3em 0\"><b>Tabla {tabCounter}.</b>&ensp;{txt}</p>\n");
                            lastEmittedPLine = stmtLine;
                        }
                        // % #nota TEXTO → nota al pie (pequena, con marcador superindice N).
                        else if (stmt is CommentStmt cnota && !cnota.IsHeading && !isInlineComment
                            && cnota.Text.TrimStart().StartsWith("#nota", System.StringComparison.OrdinalIgnoreCase))
                        {
                            var txt = System.Net.WebUtility.HtmlEncode(cnota.Text.TrimStart().Substring(5).Trim());
                            notaCounter++;
                            sb.Append($"<p class=\"line\" id=\"line-{stmtLine}\" style=\"font-size:.82em;color:#555;margin:.15em 0\"><sup>{notaCounter}</sup>&thinsp;{txt}</p>\n");
                            lastEmittedPLine = stmtLine;
                        }
                        // % #ref TEXTO → referencia cruzada "(ver TEXTO)" (ej. ver Ec. (2.1), pag. 5).
                        else if (stmt is CommentStmt cref && !cref.IsHeading && !isInlineComment
                            && cref.Text.TrimStart().StartsWith("#ref", System.StringComparison.OrdinalIgnoreCase))
                        {
                            var txt = System.Net.WebUtility.HtmlEncode(cref.Text.TrimStart().Substring(4).Trim());
                            sb.Append($"<p class=\"line\" id=\"line-{stmtLine}\" style=\"font-style:italic;color:#555\">(ver {txt})</p>\n");
                            lastEmittedPLine = stmtLine;
                        }
                        else if (stmt is CommentStmt cdir && !cdir.IsHeading && !isInlineComment
                            && TryRenderCalcpadDirective(cdir.Text, stmtLine, out var directiveHtml))
                        {
                            sb.Append(directiveHtml);
                            sb.Append('\n');
                            lastEmittedPLine = stmtLine;
                        }
                        else if (stmt is CommentStmt cs && cs.IsHeading)
                        {
                            sb.Append(MatlabHtmlWriter.RenderStatement(stmt, result));
                            sb.Append("\n");
                        }
                        else if (isInlineComment)
                        {
                            // Comentario inline: render como caption SIN `%`, en la
                            // MISMA linea visual que el assignment previo. Verifica
                            // que el ultimo <p> emitido sea de la misma linea fuente
                            // (caso assignment renderizado). Si no (void stmt
                            // intermedio, etc.), emit standalone.
                            var csInline2 = (CommentStmt)stmt;
                            // Quitar el marcador `'` inicial (Calcpad: `'` = texto, no se muestra).
                            var inlineText = csInline2.Text.TrimStart();
                            // NÚMERO DE ECUACIÓN inline: `x = k*u  %#deq@@(2.1)` (o `%@@(2.1)`).
                            // La ecuación ya se calculó/renderizó; aquí solo se pega el número a
                            // la DERECHA de esa misma línea (float:right), sin re-escribirla.
                            var inlNum = inlineText;
                            if (inlNum.StartsWith("#deq")) inlNum = inlNum.Substring(4).Trim();
                            string eqNumInline = null;
                            if (inlNum.StartsWith("@@"))
                            {
                                eqNumInline = inlNum.Substring(2).Trim();
                                if (eqNumInline.StartsWith("(") && eqNumInline.EndsWith(")")) eqNumInline = eqNumInline[1..^1].Trim();
                            }
                            if (inlineText.StartsWith("'")) inlineText = inlineText[1..];
                            // PLACEHOLDER @ : marca DÓNDE va la ecuación dentro del texto → texto ANTES/DESPUÉS/mezclado.
                            //   a = 6 %' Lado de la losa: @ m   → "Lado de la losa: a = 6 m"
                            //   a = 6 %' El lado @              → "El lado  a = 6"   (texto ANTES de la variable)
                            //   (sin @ = texto DESPUÉS, como siempre). REGLA: en el código la VARIABLE va primero;
                            //   las reglas de colocación del texto viven en el %  (MATLAB lo ignora, el .m sigue corriendo).
                            if (eqNumInline == null && inlineText.IndexOf('@') >= 0)
                            {
                                int atPos = inlineText.IndexOf('@');
                                var preT = System.Net.WebUtility.HtmlEncode(inlineText.Substring(0, atPos).Trim());
                                var postT = System.Net.WebUtility.HtmlEncode(inlineText.Substring(atPos + 1).Trim());
                                var curH = sb.ToString();
                                string idMark = $"id=\"line-{stmtLine}\">";
                                int idPos = curH.LastIndexOf(idMark, System.StringComparison.Ordinal);
                                int clPos = curH.LastIndexOf("</p>", System.StringComparison.Ordinal);
                                if (lastEmittedPLine == stmtLine && idPos >= 0 && clPos > idPos)
                                {
                                    int insAfter = idPos + idMark.Length;
                                    var eqInner = curH.Substring(insAfter, clPos - insAfter);
                                    sb.Length = insAfter;
                                    if (preT.Length > 0) sb.Append($"<span style=\"margin-right:.45em\">{preT}</span>");
                                    sb.Append(eqInner);
                                    if (postT.Length > 0) sb.Append($"<span style=\"margin-left:.45em\">{postT}</span>");
                                    sb.Append("</p>\n");
                                }
                                else
                                {
                                    var join = (preT.Length > 0 && postT.Length > 0) ? " " : "";
                                    sb.Append($"<p class=\"line\" id=\"line-{stmtLine}\"><span class=\"eq\">{preT}{join}{postT}</span></p>\n");
                                    lastEmittedPLine = stmtLine;
                                }
                            }
                            else
                            {
                            var encodedText = System.Net.WebUtility.HtmlEncode(inlineText);
                            // Comentario en NEGRO como el texto `'...` de Calcpad puro (no verde).
                            // Si es número de ecuación → span flotado a la derecha; si no → caption normal.
                            var captionSpan = eqNumInline != null
                                ? $"<span style=\"float:right;font-family:Georgia,serif;color:#555;margin-left:2em\">({System.Net.WebUtility.HtmlEncode(eqNumInline)})</span>"
                                : $"<span style=\"margin-left:1.5em\">{encodedText}</span>";
                            const string closeTag = "</p>\n";
                            // Streaming mode tambien permite esta mutacion porque el chunk
                            // se difiere hasta el cambio de linea-fuente (ver loop principal):
                            // todos los stmts de la misma linea acumulan en `sb` antes de
                            // enviarse como un solo chunk al UI.
                            bool sameLinePreviousP = lastEmittedPLine == stmtLine
                                && sb.Length >= closeTag.Length
                                && sb.ToString(sb.Length - closeTag.Length, closeTag.Length) == closeTag;
                            if (sameLinePreviousP)
                            {
                                sb.Length -= closeTag.Length;
                                sb.Append(captionSpan);
                                sb.Append(closeTag);
                            }
                            else
                            {
                                // Fallback: void stmt o gap. Standalone.
                                sb.Append($"<p class=\"line\" id=\"line-{stmtLine}\">{captionSpan}</p>\n");
                                lastEmittedPLine = stmtLine;
                            }
                            }  // fin else (sin placeholder @)
                        }
                        else
                        {
                            // Si el stmt anterior emitido pertenece a la MISMA linea
                            // fuente (caso multi-stmt `a=1; b=2`), appendear al mismo
                            // <p> con separador inline en vez de abrir uno nuevo.
                            var stmtHtml = MatlabHtmlWriter.RenderStatement(stmt, result);
                            const string closeTag2 = "</p>\n";
                            // Streaming mode tambien permite esta mutacion (chunk diferido).
                            bool appendSameLine = lastEmittedPLine == stmtLine
                                && sb.Length >= closeTag2.Length
                                && sb.ToString(sb.Length - closeTag2.Length, closeTag2.Length) == closeTag2;
                            if (appendSameLine)
                            {
                                sb.Length -= closeTag2.Length;
                                sb.Append("<span style=\"display:inline-block;width:2em\"></span>");
                                sb.Append(stmtHtml);
                                sb.Append(closeTag2);
                            }
                            else
                            {
                                sb.Append($"<p class=\"line\" id=\"line-{stmtLine}\">");
                                sb.Append(stmtHtml);
                                sb.Append("</p>\n");
                                lastEmittedPLine = stmtLine;
                            }
                        }
                    }
                    catch (Exception renderEx)
                    {
                        sb.Append($"<p class=\"err\" id=\"line-{stmtLine}\">Render error: {System.Net.WebUtility.HtmlEncode(renderEx.GetType().Name + ": " + renderEx.Message)} (line {LineLink(stmtLine)})</p>\n");
                    }
                }

                // Actualizar tracking para el proximo statement
                if (!(stmt is CommentStmt))
                {
                    prevNonCommentLine = stmtLine;
                    prevWasSuppressed = result.Suppressed;
                }
                // Flush plot HTML buffer DESPUÉS de la línea del statement
                if (htmlBuffer.Length > 0)
                {
                    sb.Append(htmlBuffer);
                    htmlBuffer.Clear();
                }
                } finally {
                    // La figura sigue abierta (MATLAB la cierra en el siguiente figure) y ESTA sentencia
                    // sacó algo visible (texto %' o un resultado): la figura va ANTES de esa salida, como
                    // en la hoja de Calcpad. Antes se volcaba en el siguiente figure() → cada gráfica
                    // salía un bloque tarde (la malla después de W_z, el mapa de w después de M_x…).
                    // title/colorbar/axis no sacan nada → siguen cayendo en su figura.
                    // «Visible» = algo que no sea <script>: colorbar/colormap/title/axis emiten scripts
                    // de ajuste (Plotly.restyle/relayout) que NO son salida y deben seguir sobre la figura.
                    if (figAntes != null && !hidden && !animFrameStreamed && sb.Length > sbAntesFig
                        && MatlabPlots.HasOpenFigure && !MatlabPlots.SubplotActive
                        && ReferenceEquals(figAntes, MatlabPlots.FigureToken)
                        && System.Text.RegularExpressions.Regex.Replace(
                               sb.ToString(sbAntesFig, sb.Length - sbAntesFig),
                               @"<script\b.*?</script>", "",
                               System.Text.RegularExpressions.RegexOptions.Singleline).Trim().Length > 0)
                    {
                        var figHtml = MatlabPlots.FinishFigure();
                        if (!string.IsNullOrEmpty(figHtml)) sb.Insert(sbAntesFig, figHtml);
                    }
                    // Streaming: NO emitir aquí — diferimos hasta el cambio de
                    // linea-fuente (siguiente iteracion) o el final del script,
                    // para que la logica de merge mismo-renglón pueda mutar `sb`
                    // antes de que el chunk se envie al UI.
                }
            }
            // Streaming: flushear el chunk pendiente de la ultima linea procesada.
            if (StreamingMode && pendingChunkLine != -1
                && sb.Length > pendingChunkStart
                && StatementCompleted != null)
            {
                var pending = sb.ToString(pendingChunkStart, sb.Length - pendingChunkStart);
                StatementCompleted.Invoke(pendingChunkLine, pending);
                pendingChunkStart = sb.Length;
            }
            if (mdMode) FlushMd();   // bloque #md sin #endmd al final del script
            // Al final del script: cerrar figura abierta (patch/line acumulados sin saveas)
            int finalChunkStart = sb.Length;
            if (MatlabPlots.SubplotActive)
            {
                // cierra el último panel + el contenedor grid de subplot
                var gridEnd = MatlabPlots.CloseSubplotGrid();
                if (!string.IsNullOrEmpty(gridEnd)) sb.Append(gridEnd);
            }
            else if (MatlabPlots.HasOpenFigure && !animFrameStreamed)
            {
                // Solo si NO hubo animación en vivo: si drawnow ya streameó frames, la figura
                // final ya está en #labAnimFrame — volver a volcarla duplicaría el plot.
                var finalFig = MatlabPlots.FinishFigure();
                if (!string.IsNullOrEmpty(finalFig)) sb.Append(finalFig);
            }
            if (sb.Length > finalChunkStart && StatementCompleted != null)
            {
                var chunk = sb.ToString(finalChunkStart, sb.Length - finalChunkStart);
                StatementCompleted.Invoke(0, chunk);
            }
            var fullHtml = sb.ToString();
            // Auto-contenido: si la salida usa Plotly (plot/surf/contour de Lab),
            // anteponer la libreria UNA vez. Script bloqueante en document.write =>
            // queda definida antes de los Plotly.newPlot del cuerpo. Asi el HTML
            // sirve en web, WPF/CLI y exportado, sin inyeccion del host.
            // Plotly EMBEBIDO (calcpad.local -> doc/, sin CDN externo; funciona offline).
            if (fullHtml.Contains("Plotly.newPlot") && !fullHtml.Contains("plotly-2.35.2"))
                fullHtml = "<script src=\"https://calcpad.local/plotly-2.35.2.min.js\" charset=\"utf-8\"></script>\n" + fullHtml;
            ScriptFinished?.Invoke(fullHtml);
            return fullHtml;
        }

        /// <summary>
        /// Exporta el `.m` a un `.tex` self-contained (texto + ecuaciones LaTeX reales),
        /// con las figuras como PNG externos junto al `.tex`. Reusa este pipeline: la carga
        /// de funciones hermanas (SetScriptDirectory) y el mismo evaluador que Run. Headless
        /// (no requiere navegador). Devuelve la ruta del `.tex` escrito.
        /// </summary>
        public string RunLatex(string source, string texPath, string assetsDir = null)
        {
            if (Calcpad.Core.DollarTranspiler.ContainsMathOp(source))
                source = Calcpad.Core.DollarTranspiler.Transpile(source);
            MatlabTokenizer.OctaveMode = OctaveMode;
            _evaluator.OctaveMode = OctaveMode;
            var tokens = MatlabTokenizer.Tokenize(source);
            var parser = new MatlabParser(tokens);
            var stmts = parser.ParseAllStatements();
            // PRE-PASS: registrar todas las function/classdef antes de ejecutar (igual que Run).
            foreach (var stmt in stmts)
            {
                if (stmt is FunctionDef fd) _evaluator.RegisterFunction(fd);
                else if (stmt is ClassDef cd) _evaluator.RegisterClass(cd);
            }
            MatlabHtmlWriter.GreekAutoRender = true;
            return MatlabLatexWriter.ExportLatex(stmts, _evaluator, texPath, assetsDir);
        }

        /// <summary>
        /// Procesa una línea sola. Devuelve (html, errMsg, errLine) — exactamente
        /// uno de html/errMsg será no-null. Usar desde ExpressionParser para
        /// integración fina.
        /// </summary>
        public (string Html, string Error, int ErrorLine) RunLine(string source, int lineOffset = 0)
        {
            try
            {
                var html = Run(source);
                return (html, null, 0);
            }
            catch (MatlabParseException pe)
            {
                return (null, pe.Message, pe.Line + lineOffset);
            }
            catch (MatlabRuntimeException re)
            {
                return (null, re.Message, lineOffset);
            }
            catch (Exception ex)
            {
                // Internal .NET error (IndexOutOfRange, ArgumentException, NullRef, etc.)
                // Devolverlo formateado para que aparezca como <p class="err">.
                return (null, $"Internal: {ex.GetType().Name}: {ex.Message}", lineOffset);
            }
        }

        /// <summary>Limpia el scope global (útil entre Parse() calls).</summary>
        public void Reset()
        {
            _evaluator.Globals.Vars.Clear();
        }

        /// <summary>
        /// Intenta renderizar una directiva Calcpad escondida en un comentario
        /// MATLAB (<c>% #deq ...</c>). Devuelve true y el HTML typeset si el
        /// texto del comentario empieza por una directiva soportada; false si es
        /// un comentario normal (el caller lo rendea como texto). Cualquier fallo
        /// del render Calcpad cae a false (fallback seguro a comentario normal).
        /// </summary>
        /// <summary>Divide `s` por el separador `sep` a nivel superior (fuera de ()/[]/{}).
        /// Para celdas de #col: el `'` inicial de cada celda es prefijo de TEXTO (no
        /// delimitador), así que NO se rastrean comillas — el ';' siempre separa celdas.</summary>
        private static System.Collections.Generic.List<string> SplitTopLevel(string s, char sep)
        {
            var res = new System.Collections.Generic.List<string>();
            int depth = 0, last = 0;
            for (int i = 0; i < s.Length; i++)
            {
                char c = s[i];
                if (c == '(' || c == '[' || c == '{') depth++;
                else if (c == ')' || c == ']' || c == '}') depth--;
                else if (c == sep && depth == 0)
                {
                    res.Add(s[last..i]);
                    last = i + 1;
                }
            }
            res.Add(s[last..]);
            return res;
        }

        private bool TryRenderCalcpadDirective(string commentText, int matlabLine, out string html)
        {
            html = null;
            if (string.IsNullOrEmpty(commentText)) return false;
            var t = commentText.Trim();
            // Modificador INLINE de griegas: prefijo opcional sobre UNA directiva, que
            // fuerza texto plano (o griego) SOLO en esa línea, sin tocar el estado de
            // bloque. Ej:  % #nogreek #noc D = ...(nu queda literal en esta línea)
            //             % #greek   #noc D = ...(fuerza símbolo aunque el bloque esté OFF)
            bool? greekOverride = null;
            if (TryMarker(t, "#nogreek", out var afterNg)) { greekOverride = false; t = afterNg; }
            else if (TryMarker(t, "#greek", out var afterG)) { greekOverride = true; t = afterG; }
            var expr = ParseDirective(t, out string mode);
            if (expr == null || expr.Length == 0) return false;
            expr = ConvertPrimes(expr);   // N'' -> N″ antes del parser (el parser de Calcpad maltrata la ')
            // Transliterar nombres griegos ASCII → símbolo Unicode (nu→ν, phi→φ) para que
            // el .m se mantenga MATLAB-válido y el output muestre las griegas. El override
            // inline gana sobre el estado de bloque; sin override, respeta el bloque
            // (% #nogreek … % #greek). Así el usuario NO teclea Unicode en el script.
            bool doGreek = greekOverride ?? MatlabHtmlWriter.GreekAutoRender;
            if (doGreek)
            {
                bool prev = MatlabHtmlWriter.GreekAutoRender;
                MatlabHtmlWriter.GreekAutoRender = true;
                expr = MatlabHtmlWriter.TransliterateGreek(expr);
                MatlabHtmlWriter.GreekAutoRender = prev;
            }
            try
            {
                _calcpadSettings ??= new Settings();
                // Instancia FRESCA por directiva: evita que definiciones (`w(x)=…`)
                // de una directiva contaminen la siguiente. El costo es mínimo
                // (pocas directivas por documento; el init estático ya se hizo).
                var renderer = new ExpressionParser { Settings = _calcpadSettings };
                // Dos modos, según el marcador (semántica Calcpad real):
                //   #noc → SIMBÓLICO: MathParser real, sin calcular. Muestra la
                //          FÓRMULA (ecuaciones y hasta `$Area{…}` como notación
                //          integral). Es lo que usa el .cpd para las derivaciones.
                //   #val / $Op directo → CÁLCULO: modo por defecto (ecuación +
                //          valor) con los escalares MATLAB inyectados, para que un
                //          operador numérico ($Area/$Slope/…) evalúe con los
                //          valores reales que calculó MATLAB.
                // mode = directiva de salida Calcpad REAL: #noc (solo fórmula),
                // #val (solo valor), #equ (ecuación + valor). Solo #noc es
                // simbólico (no inyecta los escalares MATLAB); #val/#equ calculan.
                bool symbolic = mode == "#noc";
                string source = mode + "\n" +
                    (symbolic ? "" : BuildScalarInjection(expr)) + expr;
                renderer.Parse(source, calculate: true, getXml: false);
                var result = renderer.HtmlResult;
                if (string.IsNullOrEmpty(result)) return false;
                // Degradación elegante: si Calcpad no pudo parsear la NOTACIÓN (integral
                // ∫∫, matriz con griegas [1 ν 0;…], etc.) y devolvió error, un #noc
                // DECORATIVO debe mostrar la fórmula como texto tipografiado (griegas ya
                // transliteradas) en vez de un error rojo — el objetivo es ENSEÑAR la
                // fórmula, no calcularla. Aplica solo a #noc (símbolo/notación libre).
                if (mode == "#noc" && result.Contains("class=\"err"))
                {
                    string typeset = System.Web.HttpUtility.HtmlEncode(expr);
                    html = $"<p id=\"line-{matlabLine}\" class=\"eq\"><span class=\"eq\">{typeset}</span></p>";
                    return true;
                }
                // El ExpressionParser numera desde su propia línea 1 y puede dejar
                // comentarios HTML residuales: limpiar y reescribir el id de línea
                // al número real del .m para que el click→navegación funcione.
                result = System.Text.RegularExpressions.Regex.Replace(result, "<!--.*?-->", "");
                if (result.Contains("id=\"line-"))
                    result = System.Text.RegularExpressions.Regex.Replace(
                        result, "id=\"line-\\d+\"", $"id=\"line-{matlabLine}\"");
                else
                    // El ExpressionParser no emitió id: inyectarlo en el primer <p>
                    // para que el click→navegación llegue a la línea real del .m.
                    result = new System.Text.RegularExpressions.Regex("<p\\b").Replace(
                        result, $"<p id=\"line-{matlabLine}\"", 1);
                html = ConvertPrimes(result.Trim());   // N'' -> N″ tambien en #noc (tras el cierre del sub)
                return html.Length > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// Reconoce el marcador de directiva Calcpad al inicio de un comentario
        /// MATLAB y devuelve la expresión que sigue (o null si no es directiva).
        /// <paramref name="mode"/> = directiva de salida Calcpad real:
        /// <c>#noc</c> (No calculation: solo la fórmula),
        /// <c>#val</c> (Values only: solo el resultado),
        /// <c>#equ</c> (Equations: fórmula = sustitución = resultado).
        /// Un operador <c>$Op</c> directo sin marcador se muestra en modo <c>#equ</c>.
        /// </summary>
        private static string ParseDirective(string t, out string mode)
        {
            if (TryMarker(t, "#noc", out var rest)) { mode = "#noc"; return rest; }
            if (TryMarker(t, "#val", out rest))     { mode = "#val"; return rest; }
            if (TryMarker(t, "#equ", out rest))     { mode = "#equ"; return rest; }
            // Operador Calcpad directo (`% $Area{…}`): ecuación + valor.
            if (t.StartsWith("$", StringComparison.Ordinal)) { mode = "#equ"; return t; }
            mode = null;
            return null;
        }

        private static bool TryMarker(string t, string p, out string rest)
        {
            rest = null;
            if (t.StartsWith(p, StringComparison.Ordinal) &&
                (t.Length == p.Length || t[p.Length] == ' ' || t[p.Length] == '\t'))
            { rest = t[p.Length..].Trim(); return true; }
            return false;
        }

        /// <summary>
        /// Construye un preámbulo Calcpad <c>#hide … #show</c> que define, como
        /// escalares Calcpad, las variables MATLAB que aparecen en la expresión de
        /// un operador (<c>$Area</c>/<c>$Slope</c>/…). Así el operador numérico
        /// puede evaluar usando los valores REALES calculados por MATLAB (p.ej.
        /// <c>w0</c>, <c>L</c>) sin que el usuario los redefina. Solo escalares:
        /// vectores/matrices/funciones se omiten (el usuario escribe el integrando
        /// inline, no como handle <c>f(x)</c>). El <c>#hide</c> evita que las
        /// definiciones inyectadas aparezcan en el reporte.
        /// </summary>
        private string BuildScalarInjection(string expr)
        {
            var ids = new HashSet<string>(StringComparer.Ordinal);
            foreach (System.Text.RegularExpressions.Match m in
                     System.Text.RegularExpressions.Regex.Matches(expr, @"[A-Za-z_]\w*"))
                ids.Add(m.Value);
            if (ids.Count == 0) return string.Empty;
            var defs = new StringBuilder();
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            foreach (var name in ids)
                if (_evaluator.Globals.Vars.TryGetValue(name, out var v) && v.IsScalar)
                    // Formato decimal plano (sin notación científica, que el parser
                    // de Calcpad interpreta mal — 1e-4 lee 'e' como unidad).
                    defs.Append($"{name} = {v.Scalar.ToString("0.###############", ci)}\n");
            if (defs.Length == 0) return string.Empty;
            return "#hide\n" + defs + "#show\n";
        }

        /// <summary>
        /// En una línea de TEXTO visible (`%' …`), sustituye <c>@nombre</c> y <c>@{expr}</c> por la
        /// ecuación «nombre = valor» renderizada inline. Permite combinar varias variables definidas
        /// en líneas de código distintas (con comentarios OCULTOS) en UNA sola línea visible, estilo
        /// Calcpad:  <c>%' Slab dimensions - @a m, @b m</c>  →  «Slab dimensions - a = 6 m, b = 4 m».
        /// El resto del texto se escapa normal. Un <c>@</c> suelto (sin nombre/llave) queda literal.
        /// </summary>
        private string RenderInlineVarRefs(string text)
        {
            // N''->N″, N_1->N₁ en el texto %' (captions). SOLO en el texto de alrededor, NUNCA en
            // el nombre que sigue a @: antes se convertia la linea entera primero y «@M_x_max»
            // llegaba como «M_xₘₐₓ», que no es ninguna variable → se imprimia el nombre (wₘₘ).
            string lit(string s) => ConvertSubscriptsHtml(ConvertPrimes(s), encode: true);
            if (string.IsNullOrEmpty(text) || text.IndexOf('@') < 0)
                return lit(text ?? "");
            var outSb = new StringBuilder();
            int i = 0;
            while (i < text.Length)
            {
                int at = text.IndexOf('@', i);
                if (at < 0) { outSb.Append(lit(text.Substring(i))); break; }
                if (at > i) outSb.Append(lit(text.Substring(i, at - i)));
                int k = at + 1;
                string expr = null;
                bool brace = false;
                if (k < text.Length && text[k] == '{')                       // @{expr} → SOLO valor
                {
                    int close = text.IndexOf('}', k);
                    if (close > k) { expr = text.Substring(k + 1, close - k - 1); k = close + 1; brace = true; }
                }
                else                                                          // @nombre → "nombre = valor"
                {
                    int s = k;
                    while (k < text.Length && (char.IsLetterOrDigit(text[k]) || text[k] == '_')) k++;
                    if (k > s) expr = text.Substring(s, k - s);
                }
                if (string.IsNullOrEmpty(expr)) { outSb.Append('@'); i = at + 1; continue; }   // @ suelto = literal
                outSb.Append(RenderVarRefEq(expr, brace));
                i = k;
            }
            return outSb.ToString();
        }

        /// <summary>Igual que RenderInlineVarRefs pero para líneas de HTML CRUDO: sustituye
        /// @nombre/@{expr} conservando el resto VERBATIM (no escapa &lt; &gt; ni aplica subíndices),
        /// para poder mezclar etiquetas (&lt;b&gt;…&lt;/b&gt;, entidades) con valores de variables.</summary>
        private string RenderInlineVarRefsRaw(string text, Func<string, string> lit = null)
        {
            lit ??= s => s;   // transformación del TEXTO (subíndices/primas); nunca de los nombres tras @
            if (string.IsNullOrEmpty(text) || text.IndexOf('@') < 0) return lit(text ?? "");
            var outSb = new StringBuilder();
            int i = 0;
            while (i < text.Length)
            {
                int at = text.IndexOf('@', i);
                if (at < 0) { outSb.Append(lit(text.Substring(i))); break; }
                if (at > i) outSb.Append(lit(text.Substring(i, at - i)));     // VERBATIM (conserva etiquetas)
                int k = at + 1;
                string expr = null;
                bool brace = false;
                if (k < text.Length && text[k] == '{')                       // @{expr} → SOLO valor
                {
                    int close = text.IndexOf('}', k);
                    if (close > k) { expr = text.Substring(k + 1, close - k - 1); k = close + 1; brace = true; }
                }
                else                                                          // @nombre → "nombre = valor"
                {
                    int s = k;
                    while (k < text.Length && (char.IsLetterOrDigit(text[k]) || text[k] == '_')) k++;
                    if (k > s) expr = text.Substring(s, k - s);
                }
                if (string.IsNullOrEmpty(expr)) { outSb.Append('@'); i = at + 1; continue; }
                outSb.Append(RenderVarRefEq(expr, brace));
                i = k;
            }
            return outSb.ToString();
        }

        /// <summary>Sustituye SOLO @{expr} por su valor escalar como TEXTO PLANO (sin HTML), para
        /// usar dentro de bloques Markdown (% #md … % #endmd): permite tablas/listas con valores en
        /// vivo. Deja @ suelto (sin llave) intacto para no corromper texto con arrobas.</summary>
        private string SubstituteMdVarsPlain(string text)
        {
            if (string.IsNullOrEmpty(text) || text.IndexOf('@') < 0) return text ?? "";
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string fmt(double d) => d.ToString("0.###############", ci);
            var outSb = new StringBuilder();
            int i = 0;
            while (i < text.Length)
            {
                int at = text.IndexOf('@', i);
                if (at < 0) { outSb.Append(text.Substring(i)); break; }
                if (at > i) outSb.Append(text.Substring(i, at - i));
                int k = at + 1;
                if (k >= text.Length || text[k] != '{') { outSb.Append('@'); i = at + 1; continue; }
                int close = text.IndexOf('}', k);
                if (close <= k) { outSb.Append('@'); i = at + 1; continue; }
                string expr = text.Substring(k + 1, close - k - 1);
                string valStr = null;
                try
                {
                    if (System.Text.RegularExpressions.Regex.IsMatch(expr, @"^[A-Za-z_]\w*$"))
                    {
                        if (_evaluator.Globals.Vars.TryGetValue(expr, out var v0) && v0.IsScalar) valStr = fmt(v0.Scalar);
                        else if (_previewScope != null && _previewScope.Vars.TryGetValue(expr, out var vp) && vp.IsScalar) valStr = fmt(vp.Scalar);
                    }
                    else
                    {
                        var toks = MatlabTokenizer.Tokenize(expr);
                        var node = new MatlabParser(toks).ParseExpression();
                        MValue res = null;
                        try { res = _evaluator.Eval(node, _evaluator.Globals); } catch { }
                        if ((res == null || !res.IsScalar) && _previewScope != null) try { res = _evaluator.Eval(node, _previewScope); } catch { }
                        if (res != null && res.IsScalar) valStr = fmt(res.Scalar);
                    }
                }
                catch { }
                outSb.Append(valStr ?? expr);
                i = close + 1;
            }
            return outSb.ToString();
        }

        /// <summary>Renderiza «expr = valor» como ecuación inline. `expr` = identificador (lookup
        /// directo) o expresión (se evalúa con el motor). Si no hay valor escalar, muestra solo el símbolo.</summary>
        private string RenderVarRefEq(string expr, bool valueOnly = false)
        {
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            string fmt(double d) => d.ToString("0.###############", ci);
            string valStr = null;
            try
            {
                if (System.Text.RegularExpressions.Regex.IsMatch(expr, @"^[A-Za-z_]\w*$"))
                {
                    // 1) valor ACTUAL (ya definido en este punto del script)
                    if (_evaluator.Globals.Vars.TryGetValue(expr, out var v0) && v0.IsScalar)
                        valStr = fmt(v0.Scalar);
                    // 2) fallback: valor PRE-CALCULADO (permite escribir la linea %' ANTES de definir)
                    else if (_previewScope != null && _previewScope.Vars.TryGetValue(expr, out var vp) && vp.IsScalar)
                        valStr = fmt(vp.Scalar);
                }
                else
                {
                    var toks = MatlabTokenizer.Tokenize(expr);
                    var node = new MatlabParser(toks).ParseExpression();
                    MValue res = null;
                    try { res = _evaluator.Eval(node, _evaluator.Globals); } catch { }
                    if ((res == null || !res.IsScalar) && _previewScope != null)
                        try { res = _evaluator.Eval(node, _previewScope); } catch { }
                    if (res != null && res.IsScalar) valStr = fmt(res.Scalar);
                }
            }
            catch { }
            string eqSrc = valStr != null ? (valueOnly ? valStr : $"{expr} = {valStr}") : expr;
            return $"<span class=\"eq\">{MatlabHtmlWriter.RenderEquation(eqSrc)}</span>";
        }

        // Pre-calculo de escalares para que @nombre/@{expr} en una linea %' funcionen AUNQUE la
        // linea vaya ANTES de definir la variable. SEGURO: solo evalua asignaciones escalares cuyo
        // RHS es aritmetica pura (literales, variables ya pre-calculadas, +-*/^ y funciones math
        // puras). NO ejecuta graficas, fprintf, FEM ni funciones de usuario (sin efectos secundarios).
        private MatlabScope _previewScope;
        private static readonly System.Collections.Generic.HashSet<string> _pureMath =
            new(System.StringComparer.Ordinal) {
                "sqrt","abs","sin","cos","tan","asin","acos","atan","atan2","sinh","cosh","tanh",
                "exp","log","log10","log2","floor","ceil","round","fix","mod","rem","min","max",
                "sign","hypot","deg2rad","rad2deg","factorial","nchoosek","pow2","power" };
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
                        foreach (var arg in c.Args) if (!IsSafeScalarRhs(arg)) return false;
                        return true;
                    }
                    return false;
                default: return false;
            }
        }
        private void BuildPreviewVars(System.Collections.Generic.List<MatlabNode> stmts)
        {
            _previewScope = new MatlabScope();
            foreach (var st in stmts)
            {
                if (st is Assignment asg && asg.Targets.Count == 1 && asg.Targets[0] is IdentRef tgt
                    && IsSafeScalarRhs(asg.Rhs))
                {
                    try
                    {
                        var val = _evaluator.Eval(asg.Rhs, _previewScope);
                        if (val != null && val.IsScalar) _previewScope.Vars[tgt.Name] = val;
                    }
                    catch { }
                }
            }
        }

        // Sentinels PUA (Private Use Area) que char(symbolic) usa para marcar
        // segmentos HTML pre-renderizados que NO deben ser escapados al flush.
        private const char HtmlStart = '';
        private const char HtmlEnd   = '';

        /// <summary>
        /// HtmlEncode selectivo: escapa todo el texto SALVO los segmentos
        /// delimitados por ... (HTML pre-renderizado del simbólico).
        /// </summary>
        /// <summary>Render inline de una cadena de fprintf/disp con el typeset real de
        /// Hekatan Lab (modo % #render): UNIDADES en verde y rectas, VARIABLES en itálica
        /// (con subíndice y griega), prosa como texto. Todo lo estilizado va entre sentinels
        /// para que EncodeWithHtmlSegments no lo escape.</summary>
        /// <summary>Convierte un bloque de lineas Markdown (de % #md … % #endmd) a HTML:
        /// encabezados #/##/###, **negrita**, *cursiva*, `codigo`, tablas |...|, listas -/*,
        /// regla ---. Pensado para documentar ejemplos en el output de Hekatan Lab.</summary>
        private static string MarkdownToHtml(System.Collections.Generic.List<string> lines)
        {
            var sb = new StringBuilder();
            sb.Append("<div class=\"md-block\" style=\"font-family:'Segoe UI',Segoe,Tahoma,sans-serif;line-height:1.5\">");
            int i = 0;
            while (i < lines.Count)
            {
                string line = (lines[i] ?? "").Trim();
                if (line.Length == 0) { i++; continue; }
                // Alineacion:  -> texto <-  (centrado).  Aplica a encabezado y parrafo.
                string al = "left";
                if (line.StartsWith("->") && line.EndsWith("<-") && line.Length >= 4)
                { line = line.Substring(2, line.Length - 4).Trim(); al = "center"; }
                // Tabla: bloque de lineas que empiezan por '|'
                if (line.StartsWith("|"))
                {
                    var rows = new System.Collections.Generic.List<string>();
                    while (i < lines.Count && (lines[i] ?? "").Trim().StartsWith("|")) { rows.Add((lines[i] ?? "").Trim()); i++; }
                    sb.Append(MdTable(rows));
                    continue;
                }
                // Encabezados  # / ## / ###
                if (line.StartsWith("#"))
                {
                    int lvl = 0; while (lvl < line.Length && line[lvl] == '#') lvl++;
                    string txt = line.Substring(lvl).Trim();
                    int h = Math.Min(2 + lvl, 6);   // # -> h3, ## -> h4, ### -> h5
                    sb.Append($"<h{h} style=\"margin:0.5em 0 0.3em;text-align:{al}\">{MdInline(txt)}</h{h}>");
                    i++; continue;
                }
                // Regla horizontal
                if (line == "---" || line == "***" || line == "___") { sb.Append("<hr>"); i++; continue; }
                // Listas  - / *
                if (line.StartsWith("- ") || line.StartsWith("* "))
                {
                    sb.Append("<ul style=\"margin:0.3em 0 0.3em 1.2em\">");
                    while (i < lines.Count)
                    {
                        var l = (lines[i] ?? "").Trim();
                        if (!(l.StartsWith("- ") || l.StartsWith("* "))) break;
                        sb.Append($"<li>{MdInline(l.Substring(2))}</li>"); i++;
                    }
                    sb.Append("</ul>");
                    continue;
                }
                // Parrafo: agrupa lineas consecutivas no-especiales (empieza en la ya
                // des-marcada `line` para respetar la alineacion -> ... <-)
                var para = new StringBuilder(line);
                i++;
                while (i < lines.Count)
                {
                    var l = (lines[i] ?? "").Trim();
                    if (l.Length == 0 || l.StartsWith("#") || l.StartsWith("|") ||
                        l.StartsWith("- ") || l.StartsWith("* ") || l == "---") break;
                    para.Append(' ').Append(l); i++;
                }
                sb.Append($"<p style=\"margin:0.3em 0;text-align:{al}\">{MdInline(para.ToString())}</p>");
            }
            sb.Append("</div>");
            return sb.ToString();
        }

        private static string MdTable(System.Collections.Generic.List<string> rows)
        {
            var sb = new StringBuilder();
            sb.Append("<table style=\"border-collapse:collapse;margin:0.4em 0;font-size:95%\">");
            int cr = 0;
            foreach (var row in rows)
            {
                var cells = row.Trim().Trim('|').Split('|');
                // fila separadora |---|:--:|
                bool sep = cells.Length > 0;
                foreach (var c in cells)
                {
                    var cc = c.Trim();
                    if (cc.Length == 0 || cc.Trim('-', ':', ' ').Length != 0) { sep = false; break; }
                }
                if (sep) continue;
                bool header = cr == 0;
                sb.Append("<tr>");
                foreach (var c in cells)
                {
                    string tag = header ? "th" : "td";
                    string extra = header ? "background:#eef3fa;font-weight:600;" : "";
                    sb.Append($"<{tag} style=\"border:1px solid #bbb;padding:2px 9px;text-align:left;{extra}\">{MdInline(c.Trim())}</{tag}>");
                }
                sb.Append("</tr>");
                cr++;
            }
            sb.Append("</table>");
            return sb.ToString();
        }

        private static string MdInline(string s)
        {
            s = System.Net.WebUtility.HtmlEncode(s ?? "");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"\*\*(.+?)\*\*", "<b>$1</b>");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"`(.+?)`", "<code style=\"background:#f2f2f2;padding:0 3px;border-radius:3px\">$1</code>");
            s = System.Text.RegularExpressions.Regex.Replace(s, @"(?<![\*\w])\*(?!\*)([^*]+?)\*(?![\*\w])", "<i>$1</i>");
            return s;
        }

        private static string RenderDispInline(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw;
            // Si hay segmentos HTML pre-renderizados (char() del simbólico, entre sentinels),
            // NO tocarlos — solo transliterar el TEXTO PLANO fuera de ellos (evita corromper
            // el HTML de char()).
            if (raw.IndexOf(HtmlStart) < 0)
                return RenderPlainSegment(raw);
            var sb = new StringBuilder(raw.Length + 32);
            int i = 0;
            while (i < raw.Length)
            {
                int s = raw.IndexOf(HtmlStart, i);
                if (s < 0) { sb.Append(RenderPlainSegment(raw.Substring(i))); break; }
                if (s > i) sb.Append(RenderPlainSegment(raw.Substring(i, s - i)));
                int e = raw.IndexOf(HtmlEnd, s + 1);
                if (e < 0) { sb.Append(raw.Substring(s)); break; }
                sb.Append(raw, s, e - s + 1);   // segmento HTML pre-renderizado: tal cual
                i = e + 1;
            }
            return sb.ToString();
        }
        /// <summary>Renderiza un segmento de TEXTO PLANO de disp/fprintf: nombres→variables/griegas
        /// (DispTokenRegex) y ademas `^N`→superindice (por defecto, como el resto del render).
        /// El superindice se envuelve en sentinels para pasar como HTML crudo (no re-escapado).</summary>
        private static string RenderPlainSegment(string text)
        {
            var r = DispTokenRegex.Replace(text, RenderDispToken);
            r = System.Text.RegularExpressions.Regex.Replace(
                    r, @"\^(-?\d+)", HtmlStart + "<sup>$1</sup>" + HtmlEnd);
            return r;
        }
        private static string RenderDispToken(System.Text.RegularExpressions.Match m)
        {
            if (m.Groups["u"].Success)
                return HtmlStart + RenderUnitToken(m.Groups["u"].Value) + HtmlEnd;
            var name = m.Groups["v"].Value;
            var idx = m.Groups["idx"].Value;   // "(1,1)" para A(i,j), "(3)" para v(i), o ""
            if (!MatlabHtmlWriter.IsRenderableIdent(name))
                return name + idx;             // prosa / funcion: tal cual (no perder parentesis)
            string baseHtml = MatlabHtmlWriter.RenderIdentName(name);
            if (string.IsNullOrEmpty(idx))
                return HtmlStart + baseHtml + HtmlEnd;
            // Elemento de vector/matriz: los indices van como SUBINDICE (clase .idx:
            // cursiva + color de la paleta), NO como llamada a funcion A(i,j).
            string inner = System.Text.RegularExpressions.Regex.Replace(
                               idx.Substring(1, idx.Length - 2), @"\s+", "");
            return HtmlStart + baseHtml + "<sub class=\"idx\">" + inner + "</sub>" + HtmlEnd;
        }

        /// <summary>Formatea un token de unidad como Calcpad: verde + recto, `*`→`·`,
        /// `^N`→superíndice. Ej: "kN/m^2" → &lt;i class="unit"&gt;kN/m&lt;sup&gt;2&lt;/sup&gt;&lt;/i&gt;.</summary>
        private static string RenderUnitToken(string u)
        {
            u = System.Text.RegularExpressions.Regex.Replace(u, @"\^(\d)", "<sup>$1</sup>");
            u = u.Replace("*", "·");
            return "<i class=\"unit\">" + u + "</i>";
        }

        /// <summary>Convierte apostrofos de PRIMA tras un identificador en glifos: N'->N′, N''->N″,
        /// N'''->N‴ (como Calcpad). El lookahead evita "Poisson's", "d'Alembert" y la transpuesta A' de
        /// codigo (solo aplica cuando los ' NO van seguidos de letra/digito). Para etiquetas/#noc/texto.</summary>
        internal static string ConvertPrimes(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\'') < 0) return s;
            return System.Text.RegularExpressions.Regex.Replace(
                s, @"(>|[A-Za-z0-9_}\])])('{1,4})(?![A-Za-z0-9'])",
                m =>
                {
                    int n = m.Groups[2].Value.Length;
                    string p = n == 1 ? "′" : n == 2 ? "″" : n == 3 ? "‴" : "⁗";
                    return m.Groups[1].Value + p;
                });
        }

        // Subíndices Unicode para prosa: letra/griega SOLA + '_' + 1-3 alfanum. -> N_1→N₁, K_e→Kₑ,
        // σ_v→σᵥ, J_2→J₂. Si algún char del subíndice no tiene forma Unicode, se deja igual (no rompe
        // snake_case ni nombres largos: la base es 1 char y el subíndice ≤3).
        private static readonly System.Collections.Generic.Dictionary<char,char> _subMap =
            new() {
                {'0','₀'},{'1','₁'},{'2','₂'},{'3','₃'},{'4','₄'},{'5','₅'},{'6','₆'},{'7','₇'},{'8','₈'},{'9','₉'},
                {'a','ₐ'},{'e','ₑ'},{'h','ₕ'},{'i','ᵢ'},{'j','ⱼ'},{'k','ₖ'},{'l','ₗ'},{'m','ₘ'},{'n','ₙ'},
                {'o','ₒ'},{'p','ₚ'},{'r','ᵣ'},{'s','ₛ'},{'t','ₜ'},{'u','ᵤ'},{'v','ᵥ'},{'x','ₓ'} };
        internal static string ConvertSubscripts(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('_') < 0) return s;
            return System.Text.RegularExpressions.Regex.Replace(
                s, "(?<![A-Za-z0-9\\u0370-\\u03FF])([A-Za-z\\u0370-\\u03FF])_([A-Za-z0-9]{1,3})(?![A-Za-z0-9_])",
                m =>
                {
                    var sb = new StringBuilder();
                    foreach (var ch in m.Groups[2].Value)
                        if (_subMap.TryGetValue(char.ToLowerInvariant(ch), out var sc)) sb.Append(sc);
                        else return m.Value;   // algún char sin subíndice Unicode -> dejar igual
                    return m.Groups[1].Value + sb;
                });
        }

        /// <summary>Texto %' → HTML con subíndices: Unicode cuando existe (a_1 → a₁) y, cuando
        /// alguna letra no tiene subíndice Unicode (M_xy, θ_y, Φ_ib), &lt;sub&gt; generado aquí —
        /// antes quedaba «M_xy» literal. El usuario escribe M_xy; el HTML lo pone el motor.
        /// <paramref name="encode"/>: escapar el texto (línea sin HTML propio).</summary>
        internal static string ConvertSubscriptsHtml(string s, bool encode)
        {
            string enc(string t) => encode ? System.Net.WebUtility.HtmlEncode(t) : t;
            if (string.IsNullOrEmpty(s) || s.IndexOf('_') < 0) return enc(s ?? "");
            var rx = new System.Text.RegularExpressions.Regex(
                "(?<![A-Za-z0-9\\u0370-\\u03FF])([A-Za-z\\u0370-\\u03FF][\\u2032\\u2033\\u2034]?)_([A-Za-z0-9]{1,3})(?![A-Za-z0-9_])");
            var outSb = new StringBuilder();
            int last = 0;
            foreach (System.Text.RegularExpressions.Match m in rx.Matches(s))
            {
                outSb.Append(enc(s.Substring(last, m.Index - last)));
                // SIEMPRE <sub> (como Calcpad): mezclar Unicode diminuto (Φᵢₐ) con <sub> (Φ<sub>ib</sub>)
                // en la misma línea se veía desparejo. Admite prima antes del _ (Φ″_ia).
                outSb.Append(enc(m.Groups[1].Value));
                outSb.Append("<sub>" + enc(m.Groups[2].Value) + "</sub>");
                last = m.Index + m.Length;
            }
            outSb.Append(enc(s.Substring(last)));
            return outSb.ToString();
        }

        private static string RenderDispWithMatrices(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw ?? string.Empty;
            if (raw.IndexOf('[') < 0) return raw;

            var lines = raw.Split('\n');
            var outSb = new StringBuilder(raw.Length + 64);
            var matRows = new System.Collections.Generic.List<string>();

            void FlushMatrix()
            {
                if (matRows.Count == 0) return;
                // Parsear celdas de cada fila y medir el largo VISIBLE (sin tags/sentinels).
                var grid = new System.Collections.Generic.List<string[]>();
                int maxLen = 0, maxCols = 0;
                foreach (var rc in matRows)
                {
                    var raw = System.Text.RegularExpressions.Regex.Split(rc, @"[ \t]{2,}");
                    var cs = new System.Collections.Generic.List<string>();
                    foreach (var c in raw) if (!string.IsNullOrWhiteSpace(c)) cs.Add(c);
                    grid.Add(cs.ToArray());
                    if (cs.Count > maxCols) maxCols = cs.Count;
                    foreach (var c in cs)
                    {
                        var vis = System.Text.RegularExpressions.Regex.Replace(c, "<[^>]*>", "")
                                    .Replace("", "").Replace("", "");
                        if (vis.Length > maxLen) maxLen = vis.Length;
                    }
                }
                // Separadores | entre columnas cuando los elementos son EXPRESIONES (multi-termino).
                bool sep = maxCols > 1 && maxLen > 3;
                outSb.Append(HtmlStart);
                outSb.Append("<span class=\"matrix\">");   // clásico AJUSTADO (corchetes = borde de celda vacía)
                foreach (var cells in grid)
                {
                    outSb.Append("<span class=\"tr\"><span class=\"td\"></span>");
                    for (int j = 0; j < cells.Length; j++)
                    {
                        string tdStyle = sep ? "padding:.12em .7em" : "padding:0 .5em";
                        if (sep && j > 0) tdStyle += ";border-left:1px solid #bbb";
                        outSb.Append($"<span class=\"td\" style=\"{tdStyle}\">");
                        outSb.Append(EncodeWithHtmlSegments(cells[j]));
                        outSb.Append("</span>");
                    }
                    outSb.Append("<span class=\"td\"></span></span>");
                }
                outSb.Append("</span>");
                outSb.Append(HtmlEnd);
                matRows.Clear();
            }

            for (int idx = 0; idx < lines.Length; idx++)
            {
                var line = lines[idx];
                if (TryParseMatrixRow(line, out var content))
                {
                    matRows.Add(content);
                    bool isLast = idx == lines.Length - 1;
                    bool nextIsRow = !isLast && TryParseMatrixRow(lines[idx + 1], out _);
                    if (isLast || !nextIsRow)
                    {
                        FlushMatrix();
                        if (!isLast) outSb.Append('\n');
                    }
                }
                else
                {
                    outSb.Append(ConvertPrimes(line));   // N'' -> N″ en etiquetas de disp
                    if (idx != lines.Length - 1) outSb.Append('\n');
                }
            }
            return outSb.ToString();
        }

        /// <summary>
        /// Post-procesa HTML ya encoded: busca patrones `[ ... ]` inline donde
        /// el contenido tiene fracciones (<span class="dvc">) y reemplaza los
        /// corchetes chicos por la estructura `.mat` con corchetes flex-stretch.
        /// Cubre el caso `LABEL = [ frac1  frac2 ]` que RenderDispWithMatrices
        /// no agarra (porque la linea no empieza con `[`).
        /// </summary>
        private static readonly System.Text.RegularExpressions.Regex InlineMatrixBracketRegex =
            new(@"\[\s+([^\[\]]*?<span class=""dvc""[^\[\]]*?)\s+\]",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static string StretchInlineBrackets(string html)
        {
            if (string.IsNullOrEmpty(html)) return html ?? string.Empty;
            return InlineMatrixBracketRegex.Replace(html, m =>
            {
                var content = m.Groups[1].Value;
                var cells = System.Text.RegularExpressions.Regex.Split(content, @"[ \t]{2,}");
                var sb = new StringBuilder();
                sb.Append("<span class=\"matrix\"><span class=\"tr\"><span class=\"td\"></span>");   // clásico AJUSTADO
                foreach (var cellRaw in cells)
                {
                    if (string.IsNullOrWhiteSpace(cellRaw)) continue;
                    sb.Append("<span class=\"td\">");
                    sb.Append(cellRaw.Trim());
                    sb.Append("</span>");
                }
                sb.Append("<span class=\"td\"></span></span></span>");
                return sb.ToString();
            });
        }

        private static bool TryParseMatrixRow(string line, out string content)
        {
            content = null;
            if (string.IsNullOrEmpty(line)) return false;
            int i = 0;
            while (i < line.Length && (line[i] == ' ' || line[i] == '\t')) i++;
            if (i >= line.Length || line[i] != '[') return false;
            i++;
            int j = line.Length - 1;
            while (j > i && (line[j] == ' ' || line[j] == '\t')) j--;
            if (j <= i || line[j] != ']') return false;
            var inner = line.Substring(i, j - i).Trim();
            if (inner.Length == 0) return false;
            int depth = 0;
            foreach (var ch in inner)
            {
                if (ch == '[') depth++;
                else if (ch == ']') { depth--; if (depth < 0) return false; }
            }
            if (depth != 0) return false;
            content = inner;
            return true;
        }

        private static string EncodeWithHtmlSegments(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return raw ?? string.Empty;
            if (raw.IndexOf(HtmlStart) < 0)
                return BeautifyMath(System.Net.WebUtility.HtmlEncode(raw));

            var outSb = new StringBuilder(raw.Length + 32);
            int i = 0;
            while (i < raw.Length)
            {
                int start = raw.IndexOf(HtmlStart, i);
                if (start < 0)
                {
                    outSb.Append(BeautifyMath(System.Net.WebUtility.HtmlEncode(raw.Substring(i))));
                    break;
                }
                if (start > i)
                    outSb.Append(BeautifyMath(System.Net.WebUtility.HtmlEncode(raw.Substring(i, start - i))));
                int end = raw.IndexOf(HtmlEnd, start + 1);
                if (end < 0)
                {
                    // Sentinel sin cierre: tratar como texto literal
                    outSb.Append(BeautifyMath(System.Net.WebUtility.HtmlEncode(raw.Substring(start))));
                    break;
                }
                // Insertar HTML crudo entre sentinels (sin escapar)
                outSb.Append(raw.AsSpan(start + 1, end - start - 1));
                i = end + 1;
            }
            return outSb.ToString();
        }

        // Unidades comunes (orden importa: compuestas primero)
        private static readonly string[] UnitTokens = new[] {
            "kN\\*m", "N\\*m", "kN/m", "N/m",
            "mm\\^4", "cm\\^4", "m\\^4", "cm\\^3", "m\\^3", "mm\\^3", "cm\\^2", "m\\^2", "mm\\^2",
            "GPa", "MPa", "kPa", "Pa", "kN", "kg", "kJ", "J", "Hz",
            "mm", "cm", "km", "m", "s", "rad", "deg",
            "N"
        };

        // Tokenizador del modo % #render (definido DESPUÉS de UnitTokens por el orden de
        // inicialización estática): en UNA pasada reconoce, en este orden,
        //   (u) UNIDAD  → verde + recta (<i class="unit">), superíndice para ^N y · para *.
        //   (v) VARIABLE → itálica (<var>), con subíndice y símbolo griego.
        // Las unidades van PRIMERO (m, kN, MPa… con prioridad sobre "variable de 1 letra"),
        // como Calcpad dentro de %. La prosa (grados, plano) no matchea. Se permite un
        // exponente colgante (kN/m + ^2 → kN/m^2). El lookbehind evita cazar la 'e' de 1e-4.
        private static readonly System.Text.RegularExpressions.Regex DispTokenRegex =
            new(@"(?<![0-9A-Za-z_\\/.])(?<u>(?:" + string.Join("|", UnitTokens) + @")(?:\^\d)?)(?![0-9A-Za-z_.])"
              // Un nombre de FICHERO/ruta no es una variable: ni lo que sigue a una barra (C:/ruta/talud_m_e3)
              // ni lo que lleva extension detras (talud_m_e3.png): antes salia talud con subindice m,e3 (2026-09-05).
              + @"|(?<![0-9A-Za-z_\\/.])(?<v>[A-Za-z_][A-Za-z0-9_]*)(?![A-Za-z0-9_]|:[\\/]|\.[A-Za-z][A-Za-z0-9]{0,4}(?![A-Za-z0-9_]))(?<idx>\(\d+(?:\s*,\s*\d+)*\))?",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static readonly System.Text.RegularExpressions.Regex UnitRegex =
            new(@"(?<![A-Za-z])(" + string.Join("|", UnitTokens) + @")(?![A-Za-z])",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // Subíndice estilo MATLAB: ident_word — subscript puede empezar con dígito
        // (Phi_1, Phi_2, K_3 etc) o letra (M_xx, sigma_max).
        // Lookbehind/ahead excluye: letras Unicode (acentos á é í ó ú ñ),
        // dígitos, y `;` (cierre de HTML entity `&#243;` que rodea acentos).
        private static readonly System.Text.RegularExpressions.Regex SubscriptRegex =
            new(@"(?<![\p{L}\p{N};])([A-Za-z][A-Za-z]{0,9})_([A-Za-z0-9][A-Za-z0-9]{0,9})(?![\p{L}\p{N}])",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // Letra suelta o palabra griega corta (variable matemática).
        // Excluye `a, y, o` para evitar capturar conjunciones/artículos españoles.
        // Lookbehind excluye letras Unicode + `;` (cierre de HTML entity `&#243;`
        // que rodea acentos), para no romper palabras como "Verificación".
        private static readonly System.Text.RegularExpressions.Regex LooseVarRegex =
            new(@"(?<![\p{L}\p{N}<>/""=;])(alpha|beta|gamma|delta|epsilon|zeta|eta|theta|kappa|lambda|mu|nu|xi|pi|rho|sigma|tau|phi|chi|psi|omega|[B-DF-NP-XZb-df-np-xz]|[Ee]|[Ii]|[Uu])(?![\p{L}\p{N}])",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // ^N (exponente entero)
        private static readonly System.Text.RegularExpressions.Regex PowerRegex =
            new(@"(?<=</var>|</sub>|\)|\d)\^(\d+)",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // `*` entre tokens math → middle dot
        private static readonly System.Text.RegularExpressions.Regex MulRegex =
            new(@"(?<=</var>|</sub>|</sup>|\d)\*(?=<var\b|<i\b|<sup\b|\d)",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // Operador derivada en texto plano de fprintf/disp: d/dx, d/dt, d^2/dx^2,
        // d^n/dx^n. Sin este paso, BeautifyMath italiza SOLO la `d` inicial (la `d`
        // del denominador queda excluida por ir tras `/`), dejando un `d/dx` a
        // medias — ni fraccion ni texto limpio. Aqui se detecta el operador y se
        // rende como la MISMA fraccion Leibniz que produce diff(f,x) en una
        // expresion real (clase .dvc del template). El exponente n admite digitos
        // o una sola letra (d^n/dx^n).
        private static readonly System.Text.RegularExpressions.Regex DerivativeOpRegex =
            new(@"(?<![A-Za-z0-9])d(?:\^([0-9A-Za-z]))?/d([A-Za-z])(?:\^([0-9A-Za-z]))?(?![A-Za-z0-9])",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // Integral: `int_a^b` o `int_a` o `int` standalone → ∫ con limites como
        // sub/sup. Excluye `int(...)` (call de funcion sym).
        private static readonly System.Text.RegularExpressions.Regex IntegralRegex =
            new(@"\bint(?!\()(?:_([A-Za-z0-9]+))?(?:\^([A-Za-z0-9]+))?\b",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        // Sumatoria/productoria n-ary con limites: sum_a^b, prod_a^b, lim_x
        private static readonly System.Text.RegularExpressions.Regex NaryRegex =
            new(@"\b(sum|prod|lim)(?!\()(?:_([A-Za-z0-9]+))?(?:\^([A-Za-z0-9]+))?\b",
                System.Text.RegularExpressions.RegexOptions.Compiled);

        private static readonly System.Collections.Generic.Dictionary<string, string> NarySym =
            new(System.StringComparer.Ordinal)
        {
            { "sum",  "∑" },
            { "prod", "∏" },
            { "lim",  "lim" },
        };

        // Mapa de palabras griegas → símbolo Unicode. Solo aplica DENTRO de
        // contextos matemáticos (variable suelta o identificador con subíndice)
        // para no transformar "alpha" o "pi" cuando aparecen en texto natural.
        // Calcpad-Lab vs MATLAB: en MATLAB R2017a las palabras se imprimen tal
        // cual ("alpha"), en Calcpad-Lab se renderizan con el glyph griego
        // gracias a este mapping aplicado en el render HTML.
        private static readonly System.Collections.Generic.Dictionary<string, string> GreekMap =
            new(System.StringComparer.Ordinal)
        {
            { "alpha", "α" }, { "beta", "β" }, { "gamma", "γ" }, { "delta", "δ" },
            { "epsilon", "ε" }, { "zeta", "ζ" }, { "eta", "η" }, { "theta", "θ" },
            { "kappa", "κ" }, { "lambda", "λ" }, { "mu", "μ" }, { "nu", "ν" },
            { "xi", "ξ" }, { "pi", "π" }, { "rho", "ρ" }, { "sigma", "σ" },
            { "tau", "τ" }, { "phi", "φ" }, { "chi", "χ" }, { "psi", "ψ" },
            { "omega", "ω" },
            // Mayúsculas más usadas
            { "Alpha", "Α" }, { "Beta", "Β" }, { "Gamma", "Γ" }, { "Delta", "Δ" },
            { "Theta", "Θ" }, { "Lambda", "Λ" }, { "Xi", "Ξ" }, { "Pi", "Π" },
            { "Sigma", "Σ" }, { "Phi", "Φ" }, { "Psi", "Ψ" }, { "Omega", "Ω" }
        };

        /// <summary>Si el token es nombre de letra griega, devuelve el glyph; si no, devuelve el token original.</summary>
        private static string ToGreekIfMatch(string name)
            => GreekMap.TryGetValue(name, out var glyph) ? glyph : name;

        /// <summary>
        /// Post-procesa texto HTML-escapado para detectar patrones matemáticos
        /// (subíndices, variables, unidades, exponentes) y aplicarles el CSS
        /// Calcpad. Conservador: solo transforma patrones inequívocos.
        /// </summary>
        private static string BeautifyMath(string s)
        {
            // PARIDAD MATLAB: la salida de consola (fprintf/disp) se muestra TAL CUAL,
            // en texto plano monoespaciado — igual que el Command Window de MATLAB. NO se
            // italizan letras sueltas ni se estilizan unidades/subíndices/exponentes (eso
            // divergía de MATLAB: "V=467 tonf" salía con la V en cursiva). El typeset
            // matemático solo aplica al render de ASIGNACIONES (MatlabHtmlWriter), no acá.
            return s ?? string.Empty;
#pragma warning disable CS0162 // código de embellecido desactivado (se conserva por referencia)
            if (string.IsNullOrEmpty(s)) return s ?? string.Empty;

            // 1) Unidades primero — antes de que cualquier `^N` o variable interfiera
            s = UnitRegex.Replace(s, m =>
            {
                var u = m.Value
                    .Replace("*", "&middot;")
                    .Replace("^4", "<sup>4</sup>")
                    .Replace("^3", "<sup>3</sup>")
                    .Replace("^2", "<sup>2</sup>");
                return $"<i class=\"unit\">{u}</i>";
            });

            // 1.4) Operador derivada `d/dx` (texto plano de fprintf/disp) → fraccion
            // Leibniz .dvc, identica a la que genera diff(f,x). Debe ir ANTES de
            // LooseVar/Power/Mul para que esos pasos no italicen a medias la `d/dx`.
            s = DerivativeOpRegex.Replace(s, m =>
            {
                var numPow = m.Groups[1].Success ? $"<sup>{m.Groups[1].Value}</sup>" : "";
                var v      = m.Groups[2].Value;
                var denPow = m.Groups[3].Success ? $"<sup>{m.Groups[3].Value}</sup>" : "";
                var num = $"d{numPow}";
                var den = $"d<var>{v}</var>{denPow}";
                return $"<span class=\"dvc\"><span class=\"dvc-num\">{num}</span>" +
                       $"<span class=\"dvl\"></span><span class=\"dvc-den\">{den}</span></span>";
            });

            // 1.5) Integrales: int_a^b → ∫_a^b con estilo template `.dvr > .nary`
            // (BIG integral, sub/sup stacked vertical) — mismo que el HtmWriter
            // produce para int(f,x,a,b) en expresiones reales.
            s = IntegralRegex.Replace(s, m =>
            {
                var sub = m.Groups[1].Success ? m.Groups[1].Value : "";
                var sup = m.Groups[2].Success ? m.Groups[2].Value : "";
                return $"<span class=\"dvr\"><small>{sup}</small><span class=\"nary\">&int;</span><small>{sub}</small></span>";
            });

            // 1.6) Sumatoria/productoria: sum_a^b → ∑_a^b, prod_a^b → ∏_a^b
            s = NaryRegex.Replace(s, m =>
            {
                var sym = NarySym.TryGetValue(m.Groups[1].Value, out var g) ? g : m.Groups[1].Value;
                var sub = m.Groups[2].Success ? $"<sub>{m.Groups[2].Value}</sub>" : "";
                var sup = m.Groups[3].Success ? $"<sup>{m.Groups[3].Value}</sup>" : "";
                return $"<span class=\"narysym\">{sym}</span>{sub}{sup}";
            });

            // 2) Subíndices: ident_word → <var>ident<sub>word</sub></var>
            //    Si "ident" es nombre griego (sigma, theta...), lo reemplaza por
            //    su glyph Unicode (σ, θ...). Mismo tratamiento al subíndice.
            s = SubscriptRegex.Replace(s, m =>
            {
                var ident = ToGreekIfMatch(m.Groups[1].Value);
                var sub = ToGreekIfMatch(m.Groups[2].Value);
                return $"<var>{ident}<sub>{sub}</sub></var>";
            });

            // 3) Variables sueltas: letra única o nombre griego corto → <var>X</var>
            //    Si es nombre griego, se sustituye por el glyph Unicode.
            s = LooseVarRegex.Replace(s, m =>
            {
                var name = m.Groups[1].Value;
                var glyph = ToGreekIfMatch(name);
                return $"<var>{glyph}</var>";
            });

            // 4) Exponentes: ^N tras var/sub/sup/dígito/)
            s = PowerRegex.Replace(s, "<sup>$1</sup>");

            // 5) `*` entre tokens math → &middot;
            s = MulRegex.Replace(s, "&middot;");

            return s;
#pragma warning restore CS0162
        }
    }
}
