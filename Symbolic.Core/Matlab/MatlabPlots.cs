// =============================================================================
// Calcpad Lab — MATLAB Plot functions (Plotly.js HTML embed, sin Calcpad)
// =============================================================================
//   surf / contourf / contour / imagesc / mesh / pcolor / plot / plot3
//   Emiten HTML con un <div> Plotly inline. No usan EmitSvgHeatmap de Calcpad.
//   El usuario sólo ve sintaxis MATLAB y HTML/Plotly final.
// =============================================================================
using System;
using System.Globalization;
using System.Linq;
using System.Text;
using SkiaSharp;

namespace Calcpad.Core.Matlab
{
    public static class MatlabPlots
    {
        private static int _plotCounter = 0;
        /// <summary>ID del último plot emitido (para title/xlabel/etc. post-hoc).</summary>
        public static int LastPlotId => _plotCounter;
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        /// <summary>El ultimo plot emitido colorea por VALOR en el marker (scatter con c-vector).
        /// Si es true, colormap()/colorbar() deben re-estilar marker.colorscale/marker.showscale,
        /// no el colorscale de la traza (que en un scatter no hace nada).</summary>
        private static bool _lastIsMarkerColored = false;

        // ── Acumulador de figura ─────────────────────────────────────────────
        // Permite acumular múltiples patch/line/text en UN solo plot Plotly,
        // hasta que se cierre la figura (con figure() nuevo, saveas o end).
        // Además mantiene representación intermedia para export SVG.
        public sealed class FigPrim
        {
            public string Kind;          // "patch2d", "line2d", "text2d", "mesh3d"
            public double[] Xs, Ys, Zs;
            public string FaceColor, EdgeColor, Color, Text;
            public double FaceAlpha = 1, LineWidth = 1, FontSize = 11;
            public string Align = "left";   // HorizontalAlignment de text(): left|center|right (MATLAB def = left)
            public string VAlign = "middle"; // VerticalAlignment de text(): top|middle|bottom (MATLAB def = middle)
            public int[] FaceI, FaceJ, FaceK;
            public bool IsRgb;
            public int Rgb_R, Rgb_G, Rgb_B;
            public double Val = double.NaN;   // valor por-cara (para hover interactivo)
            public string Name;               // DisplayName (para la leyenda del SVG)
            public string Dash = "solid";     // estilo de linea: solid/dash/dot/dashdot
            // --- fieldfill (contourf por-píxel, liso como imagesc) ---
            public int GridNx, GridNy, NLevels;  // dims de la malla y # de bandas
            public double Vmin = double.NaN, Vmax = double.NaN;  // rango de color (caxis = datos)
            public double LevLo, LevStep;        // rejilla de niveles "redondos" (como MATLAB)
            public bool Curvi;                   // malla curvilínea (deformada): Xs/Ys = X,Y completos
            public double[] GX, GY;              // coords de nodo completas (curvilínea): ny*nx cada una
            public float[] VertVals;             // valor NORMALIZADO [0,1] por vértice (para mapear valor->color
                                                 // por-píxel via shader de colormap = matplotlib, bandas nítidas).
            public string[] VertCols;            // FaceColor='interp': color "r,g,b" POR VÉRTICE (Gouraud real,
                                                 // como MATLAB) — el canvas interpola dentro del triángulo en vez
                                                 // de subdividirlo. null = relleno plano (FaceColor).
        }
        /// <summary>Color CSS 'rgb(r,g,b)' del colormap jet para t en [0,1].</summary>
        public static string JetCss(double t)
        {
            if (t < 0) t = 0; else if (t > 1) t = 1;
            double r = System.Math.Min(4 * t - 1.5, -4 * t + 4.5);
            double g = System.Math.Min(4 * t - 0.5, -4 * t + 3.5);
            double b = System.Math.Min(4 * t + 0.5, -4 * t + 2.5);
            int R = (int)System.Math.Round(255 * System.Math.Max(0, System.Math.Min(1, r)));
            int G = (int)System.Math.Round(255 * System.Math.Max(0, System.Math.Min(1, g)));
            int B = (int)System.Math.Round(255 * System.Math.Max(0, System.Math.Min(1, b)));
            return $"rgb({R},{G},{B})";
        }
        private static System.Collections.Generic.List<FigPrim> _figPrims = null;
        // Animacion EN VIVO: cuando esta activo, la reconstruccion de la malla dibuja PLANO por
        // cara (sin subdividir el triangulo en 256 sub-caras) -> ~256x mas rapido por frame, como
        // MATLAB que dibuja plano en cada drawnow. La subdivision suave (Gouraud) se reserva para
        // el render FINAL. Ver RenderFrame() y BuildRetainedFaces().
        private static bool _liveFast = false;
        // Bandas de color estilo GEO5 (isosuperficie / contourf): >0 = nº de bandas discretas.
        // Cuando está activo, un patch FaceColor='interp' se dibuja como bandas rellenas (marching
        // triangles: cada triángulo se corta por las isolíneas de nivel → polígonos de banda con
        // borde suave), NO como degradado Gouraud. Es lo que hace GEO5. 0 = degradado suave.
        private static int _figBandN = 0;
        private static double[] _figBandLevels = null;   // niveles EXACTOS (p.ej. GEO5: -0.4,0,0.5,...,5); null = equiespaciado
        public static void SetBandLevels(int n) { _figBandN = n < 0 ? 0 : n; _figBandLevels = null; }
        public static void SetBandLevels(double[] levels)
        {
            if (levels == null || levels.Length < 2) { _figBandN = 0; _figBandLevels = null; return; }
            _figBandLevels = (double[])levels.Clone();
            _figBandN = levels.Length - 1;   // nº de bandas
        }
        // Datos extra por-cara para el hover (esfuerzo, deformacion, ...): filas alineadas con las caras.
        private static double[][] _hoverVals = null;
        private static string[] _hoverLabels = null;
        public static void SetHoverData(double[][] vals, string[] labels) { _hoverVals = vals; _hoverLabels = labels; }
        /// <summary>Modo export PNG (CLI sin navegador): cada figura que se finaliza se
        /// rasteriza a PNG y se acumula en ExportedPngs. Regla de Jorge: gráficas → PNG.</summary>
        public static bool PngExportMode = false;
        public static readonly System.Collections.Generic.List<byte[]> ExportedPngs = new();

        private static System.Collections.Generic.List<string> _figTraces = null;
        private static System.Collections.Generic.List<string> _figAnnotations = null;
        private static int _figId = 0;
        private static bool _figIs3D = false;
        private static string _figTitle = "";
        // Captura headless (--shot/--gif/--pdf): sin tooltip de hover (imagen limpia = saveas de MATLAB).
        public static bool HeadlessNoHover = false;
        // Fondo para animacion WebGL: rasteriza ejes/colorbar/lineas/texto SIN el relleno del campo
        // (la malla la dibuja la GPU por encima). Ver BuildGlInit / RenderFrame.
        public static bool SkipFieldFill = false;
        // Animacion WebGL retenida (=HG2+ANGLE de MATLAB): la malla vive en GPU; cada drawnow solo
        // actualiza el buffer de vertices+valor (bufferSubData) y redibuja. _glInited: primer frame
        // emite el canvas+shaders; los siguientes emiten solo datos (\x02GLDATA\x02+json).
        public static bool UseGlAnim = true;
        private static bool _glInited = false;
        public static void ResetGlAnim() { _glInited = false; }
        private static string _figXLabel = null, _figYLabel = null, _figZLabel = null;
        /// <summary>figure('Position',[l b w h]) -> tamano en px del PNG de print (2026-09-04). null = 960x700.</summary>
        public static int? FigPxW = null, FigPxH = null;

        /// <summary>Tema oscuro para las gráficas (lo fija la app según Dark/Gold). En dark
        /// el fondo de la figura, texto y ejes van claros; en print/PDF se ignora (hoja blanca).</summary>
        public static bool DarkTheme = false;
        // Colores de tema para las figuras.
        // Gold (no-dark) = parchment, para que la gráfica combine con el reporte oro
        // (antes salía blanca). Dark = oscuro.
        internal static string PlotBg   => DarkTheme ? "#1a1712" : "#ede4ce";
        internal static string PlotFg   => DarkTheme ? "#e8e2d4" : "#2b2416";
        internal static string PlotGrid => DarkTheme ? "#3a3226" : "#cdbf9c";
        // Fragmentos de layout Plotly tematizados (reutilizados por todos los emisores).
        private static string Ax3(string t) => $"{{title:{{text:'{t}'}}, color:'{PlotFg}', gridcolor:'{PlotGrid}', backgroundcolor:'{PlotBg}', showbackground:true}}";
        /// <summary>Bloque paper+font+scene 3D tematizado (dark: oscuro; gold/claro: blanco).</summary>
        internal static string Scene3DJs(string xt = "X", string yt = "Y", string zt = "Z") =>
            // aspectmode:'cube' = estira cada eje para llenar la caja (como MATLAB "stretch-to-fill",
            // así el surf se ve peaked, no aplanado). camera = vista 3D clásica de MATLAB (az≈-37.5, el≈30).
            $"paper_bgcolor:'{PlotBg}', font:{{color:'{PlotFg}'}}, scene:{{bgcolor:'{PlotBg}', aspectmode:'cube', " +
            $"camera:{{eye:{{x:1.0, y:-1.0, z:0.62}}}}, xaxis:{Ax3(xt)}, yaxis:{Ax3(yt)}, zaxis:{Ax3(zt)}}}";
        /// <summary>Bloque paper+plot bg+font 2D tematizado.</summary>
        internal static string Paper2DJs() => $"paper_bgcolor:'{PlotBg}', plot_bgcolor:'{PlotBg}', font:{{color:'{PlotFg}'}}";
        private static bool _figShowLegend = false;
        private static string _figLegendLoc = null;
        /// <summary>Nombres de legend('a','b','c') — se aplican a las trazas en orden al cerrar la
        /// figura. Necesario porque la figura se difiere (compone) y no hay DOM al llamar legend().</summary>
        private static string[] _figLegendNames = null;
        private static double? _figXMin, _figXMax, _figYMin, _figYMax;
        private static bool _figAxisEqual = false;   // aspecto cuadrado en 2D SOLO si el script llama axis('equal') (MATLAB default=independiente)
        // axis off: figura LIMPIA (sin línea de eje, ticks, etiquetas ni grilla) — como los
        // esquemas SkiaSharp de Hekatan LISP. Se aplica al CONSTRUIR el layout (no por relayout
        // inline), porque la figura se descarga en el siguiente figure(): un relayout emitido
        // antes de que exista el div no haría nada.
        private static bool _figAxisOff = false;
        public static bool HasOpenFigure => _figTraces != null;
        public static bool FigureIs3D => _figIs3D;
        /// <summary>plot3() COMPUESTO: agrega una polilínea 3D a la figura abierta (misma escena LAB3D
        /// que patch/surf), para componer solido + jaula de acero (rebar) en UNA escena. Emite tanto la
        /// traza Plotly (scatter3d lines) como la geometría del canvas (CvLine).</summary>
        public static void AddLine3D(double[] xs, double[] ys, double[] zs, string colorCss, double lw)
        {
            if (_figTraces == null) BeginFigure();
            int n = System.Math.Min(xs.Length, System.Math.Min(ys.Length, zs.Length));
            if (n < 2) return;
            var sb = new StringBuilder();
            sb.Append("{type:'scatter3d',mode:'lines'");
            sb.Append($", x:[{Csv(xs)}], y:[{Csv(ys)}], z:[{Csv(zs)}]");
            sb.Append($", line:{{color:'{colorCss}', width:{lw.ToString(Inv)}}}, showlegend:false}}");
            AddTrace(sb.ToString());
            float[] c = CssToRgbF(colorCss);
            for (int i = 0; i + 1 < n; i++) CvLine(xs[i], ys[i], zs[i], xs[i + 1], ys[i + 1], zs[i + 1], c);
            _figIs3D = true;
        }

        // ============ Renderer CANVAS/WebGL (alternativa RÁPIDA a Plotly, sin CDN) ============
        // Se captura la MISMA geometría 3D que las trazas Plotly y, en FinishFigure, se emite un
        // <canvas> con un mini-motor WebGL embebido (órbita con mouse). Instantáneo (no carga CDN).
        // Plotly sigue disponible con CALCPAD_LAB_PLOTLY=1.
        // Por defecto FALSE: todo 3D (surf/plot3) usa Plotly → rasteriza consistente y
        // convive con muchas gráficas en grid. El canvas WebGL (rápido pero inestable en
        // multi-plot/headless) queda como opt-in con CALCPAD_LAB_CANVAS3D=1.
        public static bool Use3DCanvas =
            System.Environment.GetEnvironmentVariable("CALCPAD_LAB_CANVAS3D") == "1";
        private static System.Collections.Generic.List<float> _cvOpaque, _cvAlpha, _cvLines;
        private static double _cvXmin, _cvXmax, _cvYmin, _cvYmax, _cvZmin, _cvZmax;
        private static bool _cvAny;
        // Aspecto del canvas 3D. true = 'axis equal' (proporciones reales, escena sólida IDEA);
        // false = 'axis tight'/'normal' (cada eje se estira para llenar la vista, como el surf de
        // resultados MATLAB). Default true = comportamiento previo (seguro para escenas existentes).
        /// <summary>grid on/off del script. MATLAB lo respeta en cualquier figura;
        /// Lab solo lo aplicaba a las de Plotly, no al renderizador SVG de primitivas.</summary>
        private static bool _figGrid = false;
        /// <summary>colorbar y ejes rotulados del canvas 3D: MATLAB los dibuja y Lab los
        /// ignoraba, asi que no se podia leer la escala ni saber que valor era cada color.</summary>
        private static bool _figColorbar = false;
        private static double _figCLo = 0, _figCHi = 1;
        public static void SetColorbar(bool on) { _figColorbar = on; }
        public static bool ColorbarState => _figColorbar;
        // colorbar('Direction','reverse') -> min arriba (estilo GEO5); 'Ticks' -> valores custom.
        private static bool _cbReverse = false;
        private static double[] _cbTicks = null;
        private static string[] _cbTickLabels = null;   // set(cb,'TickLabels',{...}) -> texto por tick (MATLAB)
        public static void SetColorbarOptions(bool reverse, double[] ticks) { _cbReverse = reverse; _cbTicks = ticks; }
        /// <summary>set(cb,'Ticks',v[,'TickLabels',{...}]) / set(cb,'Direction','reverse'): igual que MATLAB.</summary>
        public static void SetColorbarTicks(double[] ticks, string[] labels, bool? reverse)
        {
            if (ticks != null) _cbTicks = ticks;
            if (labels != null) _cbTickLabels = labels;
            if (reverse.HasValue) _cbReverse = reverse.Value;
        }
        // view(2): cámara CENITAL (top-down) sobre el canvas 3D → aspecto 2D como MATLAB.
        private static bool _figView2 = false;
        public static void SetView2(bool on) { _figView2 = on; }
        // subplot(m,n,p): cada panel = su PROPIA figura, colocada en una celda de un grid CSS
        // (antes se apilaban en una sola figura → solo se veía el último + colores cruzados).
        private static bool _subplotActive = false;
        // El grid de subplot se BUFFERIZA y se emite como UN SOLO chunk al cerrar. Si se emitiera
        // por partes (grid-open, celda, celda), el streaming del WebView2 auto-cerraría el <div>
        // contenedor vacío y los paneles quedarían apilados como bloques hermanos.
        private static System.Text.StringBuilder _subplotBuf = null;
        // HTML del panel ACTUAL que no pasa por el sistema de figura 2D: surf/plot3/scatter3 y sus
        // relayout de título/ejes. Se acumula aquí y se cierra dentro del <div class="sp-cell"> del
        // panel, para que las gráficas 3D caigan en su celda del grid igual que las 2D.
        private static readonly System.Text.StringBuilder _panelHtml = new System.Text.StringBuilder();
        /// <summary>Si hay subplot activo, acumula html en la celda del panel actual y devuelve true
        /// (el llamador NO debe emitirlo suelto). Fuera de subplot devuelve false → emisión normal.</summary>
        public static bool TryBufferPanel(string html)
        {
            if (!_subplotActive) return false;
            _panelHtml.Append(html);
            return true;
        }
        // Dimensiones del <div> de un plot 3D: reducidas en subplot para igualar el aspecto de los
        // paneles 2D y que TODAS las celdas del grid queden del mismo tamaño (como MATLAB).
        private static string Plot3dDivStyle() => _subplotActive ? "width:430px;height:320px" : "width:640px;height:480px";
        /// <summary>title() sobre un panel 3D en subplot: reemplaza el título hardcodeado (surf/plot3)
        /// por el del usuario DENTRO del layout, antes de rasterizar (el relayout diferido no aplica).</summary>
        public static bool SetPanelTitle(string t)
        {
            if (!_subplotActive || _panelHtml.Length == 0) return false;
            string esc = (t ?? "").Replace("\\", "\\\\").Replace("'", "\\'");
            string s = _panelHtml.ToString();
            // SOLO el texto-placeholder del layout (surf/plot3/scatter3/…); NUNCA los títulos de
            // eje X/Y/Z de la escena ('X'/'Y'/'Z') → antes se corrompían. Reemplaza el text:'…'
            // interno y preserva el font:{size} que lo acompaña, para tamaño de título uniforme.
            foreach (var ph in new[] { "plot3", "surf", "scatter3", "mesh", "contour3", "heatmap", "imagesc" })
            {
                s = s.Replace("text:'" + ph + "'", "text:'" + esc + "'");
                s = s.Replace("title:'" + ph + "'", "title:'" + esc + "'");
                s = s.Replace("title: '" + ph + "'", "title: '" + esc + "'");
            }
            _panelHtml.Clear(); _panelHtml.Append(s);
            return true;
        }
        // Geometría del subplot ACTUAL: n columnas, m filas, y p (posición row-major 1..m*n) del
        // panel que se está dibujando ahora. Se usan al CERRAR el panel para colocarlo en su celda.
        private static int _spCols = 1, _spRows = 1, _spCurP = 1;
        /// <summary>Cierra el panel actual en su celda exacta del grid (grid-column/row desde p).</summary>
        private static void AppendSubplotCell(string figHtml)
        {
            int cols = _spCols < 1 ? 1 : _spCols;
            int p = _spCurP < 1 ? 1 : _spCurP;
            int row = (p - 1) / cols + 1;      // MATLAB subplot: p es row-major (p=1 → fila1,col1)
            int col = (p - 1) % cols + 1;
            _subplotBuf.Append("<div class=\"sp-cell\" style=\"grid-column:").Append(col)
                       .Append(";grid-row:").Append(row).Append("\">")
                       .Append(figHtml).Append(_panelHtml).Append("</div>");
            _panelHtml.Clear();
        }
        /// <summary>subplot(m,n,p): cierra el panel anterior en SU celda, abre el grid en el primer
        /// subplot, arranca una figura nueva para el panel actual. Respeta CUALQUIER posición p
        /// (salteada o desordenada), no solo el orden 1,2,3… NO emite (todo va al buffer).</summary>
        public static string SubplotCell(int m, int n, int p)
        {
            if (_subplotActive)
            {
                string figPrev = FinishFigure();   // _subplotActive sigue true → panel encogido
                AppendSubplotCell(figPrev);        // coloca el panel ANTERIOR en su celda (su _spCurP)
            }
            else
            {
                _subplotBuf = new System.Text.StringBuilder();
                _panelHtml.Clear();
                // GRID real de n columnas × m filas; cada panel va a su celda p (grid-column/row).
                _subplotBuf.Append("<div class=\"matlab-subplot-grid\" style=\"--sp-cols:").Append(n)
                           .Append(";--sp-rows:").Append(m).Append("\">");
                _subplotActive = true;
            }
            _spCols = n; _spRows = m; _spCurP = p;   // geometría del panel ACTUAL
            BeginFigure();   // ejes NUEVOS para el panel actual (colormap/title/etc. propios)
            return "";       // nada se emite hasta CloseSubplotGrid (chunk único)
        }
        /// <summary>Cierra el último panel + el contenedor y devuelve TODO el grid como un chunk.</summary>
        public static string CloseSubplotGrid()
        {
            if (!_subplotActive) return "";
            string figLast = FinishFigure();   // FinishFigure ANTES de bajar el flag (panel encogido)
            AppendSubplotCell(figLast);         // último panel en su celda (su _spCurP)
            _subplotBuf.Append("</div>\n");
            _panelHtml.Clear();
            _subplotActive = false;
            string html = _subplotBuf.ToString();
            _subplotBuf = null;
            return html;
        }
        public static bool SubplotActive => _subplotActive;
        private static string _figCmapName = "parula";
        public static void SetCmapName(string n) { _figCmapName = n ?? "parula"; }
        public static void SetColorRange(double lo, double hi) { _figCLo = lo; _figCHi = hi; }
        public static void SetGrid(bool on) { _figGrid = on; }
        private static bool _cvAxisEqual = true;
        public static void SetAxisEqual(bool eq) { _cvAxisEqual = eq; _figAxisEqual = eq; }
        public static void SetAxisOff(bool off) { _figAxisOff = off; }
        /// <summary>Límites de eje fijados por el script con xlim/ylim/zlim. MATLAB los
        /// respeta al pie de la letra (por eso un zoom recorta de verdad); Lab los
        /// ignoraba y auto-escalaba siempre, así que una figura con zoom salía idéntica
        /// a la vista completa. Aplican a los DOS motores de dibujo: Plotly (plot) y
        /// canvas (line).</summary>
        public static void SetXLim(double lo, double hi) { _figXMin = lo; _figXMax = hi; _cvXlim = (lo, hi); }
        public static void SetYLim(double lo, double hi) { _figYMin = lo; _figYMax = hi; _cvYlim = (lo, hi); }
        public static void SetZLim(double lo, double hi) { _cvZlim = (lo, hi); }
        /// <summary>Límites del marco actual (los que fijó axis([x0 x1 y0 y1]) o el auto-escalado del
        /// último dibujo). Los usa ginput como sistema de coordenadas del canvas, igual que el ginput
        /// real usa los límites de la figura. Devuelve false si no hay nada dibujado (usa defaults).</summary>
        public static bool TryGetAxisLimits(out double x0, out double x1, out double y0, out double y1)
        {
            if (_cvXlim.HasValue && _cvYlim.HasValue)
            { x0 = _cvXlim.Value.lo; x1 = _cvXlim.Value.hi; y0 = _cvYlim.Value.lo; y1 = _cvYlim.Value.hi; return true; }
            if (_cvXmax >= _cvXmin && _cvYmax >= _cvYmin && _cvXmax > double.MinValue)
            { x0 = _cvXmin; x1 = _cvXmax; y0 = _cvYmin; y1 = _cvYmax; return true; }
            x0 = 0; x1 = 40; y0 = -22; y1 = 0; return false;
        }
        private static (double lo, double hi)? _cvXlim, _cvYlim, _cvZlim;

        private static double? _caxisMin, _caxisMax;
        public static void SetCAxis(double lo, double hi) { _caxisMin = lo; _caxisMax = hi; }
        public static bool TryGetCAxis(out double lo, out double hi) { lo = _caxisMin ?? 0; hi = _caxisMax ?? 1; return _caxisMin.HasValue; }
        private static void CvReset()
        {
            _cvOpaque = new(); _cvAlpha = new(); _cvLines = new(); _cvAny = false; _cvAxisEqual = true;
            _cvXlim = null; _cvYlim = null; _cvZlim = null;
            _cvXmin = _cvYmin = _cvZmin = double.MaxValue;
            _cvXmax = _cvYmax = _cvZmax = double.MinValue;
            // Sin caxis explícito -> AUTO-escala al rango de los datos (como MATLAB). Solo un
            // caxis([lo hi]) explícito fija la escala. (Antes default [0,1] pintaba mal un surf
            // cuyos datos no estaban normalizados, p.ej. la deformada w en mm.)
            _caxisMin = null; _caxisMax = null;
        }
        private static void CvBound(double x, double y, double z)
        {
            if (x < _cvXmin) _cvXmin = x; if (x > _cvXmax) _cvXmax = x;
            if (y < _cvYmin) _cvYmin = y; if (y > _cvYmax) _cvYmax = y;
            if (z < _cvZmin) _cvZmin = z; if (z > _cvZmax) _cvZmax = z; _cvAny = true;
        }
        private static void CvV(System.Collections.Generic.List<float> L, double x, double y, double z, float[] c, float? a)
        {
            L.Add((float)x); L.Add((float)y); L.Add((float)z); L.Add(c[0]); L.Add(c[1]); L.Add(c[2]);
            if (a.HasValue) L.Add(a.Value); CvBound(x, y, z);
        }
        // Triángulo: opaco si alpha≈1 (stride 6), si no a _cvAlpha (stride 7).
        private static void CvTri(double x0, double y0, double z0, float[] c0,
                                  double x1, double y1, double z1, float[] c1,
                                  double x2, double y2, double z2, float[] c2, double alpha)
        {
            if (_cvOpaque == null) return;
            if (alpha >= 0.995)
            { CvV(_cvOpaque, x0, y0, z0, c0, null); CvV(_cvOpaque, x1, y1, z1, c1, null); CvV(_cvOpaque, x2, y2, z2, c2, null); }
            else
            { float a = (float)alpha; CvV(_cvAlpha, x0, y0, z0, c0, a); CvV(_cvAlpha, x1, y1, z1, c1, a); CvV(_cvAlpha, x2, y2, z2, c2, a); }
        }
        private static void CvLine(double x0, double y0, double z0, double x1, double y1, double z1, float[] c)
        { if (_cvLines == null) return; CvV(_cvLines, x0, y0, z0, c, null); CvV(_cvLines, x1, y1, z1, c, null); }
        private static float[] JetF(double t)
        { var c = JetRgb(Math.Max(0, Math.Min(1, t))); return new[] { c.Item1 / 255f, c.Item2 / 255f, c.Item3 / 255f }; }
        // Colormap CUSTOM: cuando el script hace colormap(gca, M) con M una matriz Nx3,
        // se guarda aqui (JSON Plotly compacto + filas RGB para el canvas). null = sin custom.
        private static string _customColorscaleJson;
        private static float[][] _customCmapRgb;
        // Mapeo dato->pixel del ÚLTIMO PNG rasterizado (coords lógicas), para alinear el hover
        // interactivo que dibuja ese mismo PNG de fondo: px = _pmOX + (x-_pmX0)*_pmSX ;
        // py = _pmH - _pmOY - (y-_pmY0)*_pmSY.
        private static double _pmOX, _pmOY, _pmSX, _pmSY, _pmX0, _pmY0, _pmH;
        private static double Clamp01(double v) => v < 0 ? 0 : (v > 1 ? 1 : v);

        /// <summary>Registra un colormap a partir de una matriz Nx3 (filas RGB 0..1), como
        /// <c>colormap(gca, jet(256))</c>. Construye el colorscale de Plotly (compacto) y las
        /// filas para el renderer canvas.</summary>
        public static void SetCustomColormap(double[][] rows)
        {
            if (rows == null || rows.Length < 2) return;
            int n = rows.Length;
            _customCmapRgb = new float[n][];
            for (int i = 0; i < n; i++)
                _customCmapRgb[i] = new[] { (float)Clamp01(rows[i][0]), (float)Clamp01(rows[i][1]), (float)Clamp01(rows[i][2]) };
            // Colorscale Plotly: muestrear <=17 paradas para no inflar el HTML.
            int m = Math.Min(n, 17);
            var sb = new StringBuilder("[");
            for (int k = 0; k < m; k++)
            {
                int i = (int)Math.Round((double)k / (m - 1) * (n - 1));
                double t = (double)k / (m - 1);
                if (k > 0) sb.Append(',');
                sb.Append('[').Append(t.ToString("0.####", Inv)).Append(",\"rgb(")
                  .Append((int)Math.Round(Clamp01(rows[i][0]) * 255)).Append(',')
                  .Append((int)Math.Round(Clamp01(rows[i][1]) * 255)).Append(',')
                  .Append((int)Math.Round(Clamp01(rows[i][2]) * 255)).Append(")\"]");
            }
            sb.Append(']');
            _customColorscaleJson = sb.ToString();
        }

        /// <summary>Valor de <c>colorscale:</c> para Plotly: nombre entre comillas ('Jet') o,
        /// si el colormap activo es "custom", el array de paradas (sin comillas).</summary>
        private static string ColorscaleJs(string colormap)
        {
            if (string.Equals(colormap, "custom", StringComparison.OrdinalIgnoreCase) && _customColorscaleJson != null)
                return _customColorscaleJson;
            // parula/jet no existen en Plotly (mapean a Viridis) → emitir el colorscale
            // REAL muestreando CmapF, para que el surf salga como MATLAB (parula de verdad).
            var nm = (colormap ?? "parula").ToLowerInvariant();
            if (nm == "parula" || nm == "jet" || nm == "jet_r" || nm == "hot" || nm == "cool")
            {
                var sb = new StringBuilder("[");
                for (int q = 0; q <= 10; q++)
                {
                    double t = q / 10.0;
                    var f = CmapF(nm, t);
                    int r = (int)(f[0] * 255), g = (int)(f[1] * 255), b = (int)(f[2] * 255);
                    if (q > 0) sb.Append(',');
                    sb.Append($"[{t.ToString(Inv)},'rgb({r},{g},{b})']");
                }
                sb.Append(']');
                return sb.ToString();
            }
            return "'" + ColormapToPlotly(colormap) + "'";
        }

        /// <summary>MATLAB permite <c>contourf(...); colormap(gca, cm);</c> — el colormap se
        /// aplica al plot YA dibujado. Como Lab emite cada plot de inmediato, este script
        /// re-estiliza el ULTIMO plot Plotly con el colormap dado (Plotly.restyle). Devuelve
        /// null si aun no hay ningun plot.</summary>
        public static string RestyleLastColormap(string colormap)
        {
            if (_plotCounter <= 0) return null;
            string rev = ColormapReversed(colormap) ? "true" : "false";
            // Un scatter colorea bajo marker.* ; una superficie/heatmap a nivel de traza.
            string body = _lastIsMarkerColored
                ? $"{{'marker.colorscale':[{ColorscaleJs(colormap)}],'marker.reversescale':[{rev}]}}"
                : $"{{colorscale:[{ColorscaleJs(colormap)}],reversescale:[{rev}]}}";
            return $"<script>setTimeout(function(){{try{{Plotly.restyle('matlab_plot_{_plotCounter}'," +
                   $"{body});}}catch(e){{}}}},60);</script>\n";
        }
        /// <summary>colorbar tras el plot: enciende la barra de escala en el ULTIMO plot.
        /// En un scatter la escala vive en marker.showscale; en superficie/heatmap en showscale.</summary>
        public static string RestyleLastColorbar(bool on)
        {
            if (_plotCounter <= 0) return null;
            string v = on ? "true" : "false";
            string body = _lastIsMarkerColored
                ? $"{{'marker.showscale':[{v}]}}"
                : $"{{showscale:[{v}]}}";
            return $"<script>setTimeout(function(){{try{{Plotly.restyle('matlab_plot_{_plotCounter}'," +
                   $"{body});}}catch(e){{}}}},60);</script>\n";
        }

        // etiqueta del colorbar: cb.Label.String='d_x [mm]' (MATLAB). Se renderiza como titulo de
        // la barra de escala (colorbar.title), lado derecho, igual que MATLAB.
        private static string _cbLabel = null;
        public static string ColorbarLabel => _cbLabel;
        public static string SetColorbarLabelRestyle(string label)
        {
            _cbLabel = label;
            if (_plotCounter <= 0 || string.IsNullOrEmpty(label)) return null;
            string esc = label.Replace("\\", "\\\\").Replace("'", "\\'");
            string pfx = _lastIsMarkerColored ? "marker.colorbar" : "colorbar";
            string body = $"{{'{pfx}.title.text':['{esc}'],'{pfx}.title.side':['right']}}";
            return $"<script>setTimeout(function(){{try{{Plotly.restyle('matlab_plot_{_plotCounter}'," +
                   $"{body});}}catch(e){{}}}},80);</script>\n";
        }

        private static float[] SampleCustom(double t)
        {
            var mm = _customCmapRgb; int n = mm.Length;
            // MATLAB mapea un CData escalar a un colormap DISCRETO ajustando a la banda:
            // index = floor((C-Cmin)/(Cmax-Cmin)*m), recortado a [0,m-1]. NO interpola entre
            // colores del colormap; luego FaceColor='interp' mezcla (Gouraud) ENTRE vertices.
            // Antes interpolabamos aqui -> la fundacion (t~0.056) caia 61% al azul oscuro (banda 1)
            // en vez de la banda 0 (azul claro) que da MATLAB. Ahora coincide banda por banda.
            int band = (int)Math.Floor(Math.Max(0, Math.Min(1, t)) * n);
            if (band < 0) band = 0; if (band >= n) band = n - 1;
            return mm[band];
        }

        /// <summary>parula: el colormap por DEFECTO de MATLAB. Sin esto Lab pintaba
        /// todo con jet_r y una misma malla salia roja arriba en Lab y amarilla en
        /// MATLAB. Anclas muestreadas de la parula de MATLAB.</summary>
        private static (int, int, int) ParulaRgb(double t)
        {
            double[][] A = {
                new double[]{ 53, 42,135}, new double[]{ 15, 92,221}, new double[]{ 18,125,216},
                new double[]{  7,156,207}, new double[]{ 21,177,180}, new double[]{ 89,189,140},
                new double[]{165,190,107}, new double[]{225,185, 82}, new double[]{248,230, 33}
            };
            t = Math.Max(0, Math.Min(1, t));
            double f = t * (A.Length - 1);
            int i = (int)Math.Floor(f); if (i >= A.Length - 1) i = A.Length - 2;
            double u = f - i;
            return ((int)Math.Round(A[i][0] + u * (A[i + 1][0] - A[i][0])),
                    (int)Math.Round(A[i][1] + u * (A[i + 1][1] - A[i][1])),
                    (int)Math.Round(A[i][2] + u * (A[i + 1][2] - A[i][2])));
        }

        private static float[] CmapF(string name, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            var nm = (name ?? "parula").ToLowerInvariant();   // MATLAB: parula por defecto
            if (nm == "custom" && _customCmapRgb != null) return SampleCustom(t);
            if (nm.EndsWith("_r")) { nm = nm.Substring(0, nm.Length - 2); t = 1 - t; }   // jet_r etc.
            switch (nm)
            {
                case "parula": { var cp = ParulaRgb(t); return new[] { cp.Item1 / 255f, cp.Item2 / 255f, cp.Item3 / 255f }; }
                case "jet": { var c = JetRgb(t); return new[] { c.Item1 / 255f, c.Item2 / 255f, c.Item3 / 255f }; }
                default: { var c = ViridisRgb(t); return new[] { c.Item1 / 255f, c.Item2 / 255f, c.Item3 / 255f }; }
            }
        }
        // Convierte "rgb(r,g,b)" | "#rrggbb" | nombre → floats 0..1.
        private static float[] CssToRgbF(string css)
        {
            if (string.IsNullOrEmpty(css)) return new[] { 0.6f, 0.6f, 0.65f };
            css = css.Trim();
            if (css.StartsWith("rgb", System.StringComparison.OrdinalIgnoreCase))
            {
                int p = css.IndexOf('('), q = css.IndexOf(')');
                if (p >= 0 && q > p)
                {
                    var parts = css.Substring(p + 1, q - p - 1).Split(',');
                    if (parts.Length >= 3 &&
                        int.TryParse(parts[0].Trim(), out int r) &&
                        int.TryParse(parts[1].Trim(), out int g) &&
                        int.TryParse(parts[2].Trim(), out int b))
                        return new[] { r / 255f, g / 255f, b / 255f };
                }
            }
            if (css.StartsWith("#") && css.Length >= 7)
            {
                try
                {
                    int r = System.Convert.ToInt32(css.Substring(1, 2), 16);
                    int g = System.Convert.ToInt32(css.Substring(3, 2), 16);
                    int b = System.Convert.ToInt32(css.Substring(5, 2), 16);
                    return new[] { r / 255f, g / 255f, b / 255f };
                }
                catch { }
            }
            switch (css.ToLowerInvariant())
            {
                case "red": return new[] { 0.85f, 0.15f, 0.15f };
                case "green": return new[] { 0.15f, 0.6f, 0.15f };
                case "blue": return new[] { 0.15f, 0.2f, 0.85f };
                case "black": case "k": return new[] { 0.1f, 0.1f, 0.1f };
                case "white": return new[] { 0.95f, 0.95f, 0.95f };
                case "gray": case "grey": return new[] { 0.5f, 0.5f, 0.5f };
                default: return new[] { 0.6f, 0.6f, 0.65f };
            }
        }

        private static string FloatCsv(System.Collections.Generic.List<float> L)
        {
            var sb = new StringBuilder(L.Count * 5);
            for (int i = 0; i < L.Count; i++) { if (i > 0) sb.Append(','); sb.Append(L[i].ToString("0.####", Inv)); }
            return sb.ToString();
        }
        private static string FinishFigureCanvas()
        {
            int id = _figId;
            // Tamaños: en modo subplot los paneles se encogen para caber lado a lado.
            int cw = _subplotActive ? 430 : 720;
            int ch = _subplotActive ? 340 : 560;
            int cbH = _subplotActive ? 320 : 500;
            int contW = cw + 95;
            double x0 = _cvXmin, x1 = _cvXmax, y0 = _cvYmin, y1 = _cvYmax, z0 = _cvZmin, z1 = _cvZmax;
            // xlim/ylim/zlim del script MANDAN sobre el auto-escalado (como MATLAB)
            if (_cvXlim.HasValue) { x0 = _cvXlim.Value.lo; x1 = _cvXlim.Value.hi; }
            if (_cvYlim.HasValue) { y0 = _cvYlim.Value.lo; y1 = _cvYlim.Value.hi; }
            if (_cvZlim.HasValue) { z0 = _cvZlim.Value.lo; z1 = _cvZlim.Value.hi; }
            if (x1 - x0 < 1e-9) { x0 -= 0.5; x1 += 0.5; }
            if (y1 - y0 < 1e-9) { y0 -= 0.5; y1 += 0.5; }
            if (z1 - z0 < 1e-9) { z0 -= 0.5; z1 += 0.5; }
            var sb = new StringBuilder();
            // Tema oscuro: WebGL limpia en oscuro y los ticks (HTML) van claros.
            if (DarkTheme)
                sb.Append(Lab3dRenderer
                    .Replace("gl.clearColor(1.0,1.0,1.0,1.0)", "gl.clearColor(0.102,0.090,0.071,1.0)")
                    .Replace("color:#333;pointer-events:none", "color:#e8e2d4;pointer-events:none"));
            else
                sb.Append(Lab3dRenderer);
            // Ejes rotulados y colorbar como capa HTML junto al canvas: dentro de un
            // canvas WebGL no se puede dibujar texto 2D, y MATLAB si los muestra.
            // si el script puso xlabel/ylabel/zlabel se usa esa etiqueta; si no, la letra
            string ejes = $"<b>{_figXLabel ?? "X"}</b>: {x0.ToString("G3", Inv)} … {x1.ToString("G3", Inv)}"
                        + $" &nbsp;&nbsp; <b>{_figYLabel ?? "Y"}</b>: {y0.ToString("G3", Inv)} … {y1.ToString("G3", Inv)}"
                        + $" &nbsp;&nbsp; <b>{_figZLabel ?? "Z"}</b>: {z0.ToString("G3", Inv)} … {z1.ToString("G3", Inv)}";
            string cbar = "";
            if (_figColorbar)
            {
                var stops = new StringBuilder();
                for (int q = 0; q <= 10; q++)
                {
                    var f3 = CmapF(_figCmapName, 1.0 - q / 10.0);   // el MISMO colormap del solido
                    var c = ((int)(f3[0] * 255), (int)(f3[1] * 255), (int)(f3[2] * 255));
                    if (q > 0) stops.Append(", ");
                    stops.Append($"rgb({c.Item1},{c.Item2},{c.Item3})");
                }
                // marcas intermedias: MATLAB rotula toda la barra, no solo los extremos
                var ticks = new StringBuilder();
                const int NT = 8;
                for (int q = 0; q <= NT; q++)
                {
                    double val = _figCHi - (_figCHi - _figCLo) * q / (double)NT;
                    double top = cbH * q / (double)NT;
                    ticks.Append($"<div style=\"position:absolute;left:24px;top:{(top - 7).ToString("F0", Inv)}px;"
                               + $"font:10px sans-serif;color:#333;white-space:nowrap\">&ndash; {val.ToString("G3", Inv)}</div>");
                }
                cbar = $"<div style=\"display:inline-block;vertical-align:top;margin-left:12px;position:relative;height:{cbH}px;width:64px\">"
                     + $"<div style=\"width:18px;height:{cbH}px;border:1px solid #888;background:linear-gradient(to bottom, {stops})\"></div>"
                     + ticks + "</div>";
            }
            sb.Append($"<div class=\"matlab-plot\" style=\"width:{contW}px;display:inline-block;vertical-align:top\">");
            if (!string.IsNullOrEmpty(_figTitle))   // MATLAB muestra el title() sobre la figura
                sb.Append($"<div style=\"font:bold 13px sans-serif;text-align:center;width:{cw}px;margin-bottom:4px\">{TexToHtml(_figTitle)}</div>");
            sb.Append($"<canvas id=\"lab3d_{id}\" width=\"1440\" height=\"1120\" style=\"width:{cw}px;height:{ch}px;border:1px solid {PlotGrid};background:{PlotBg};cursor:grab;display:inline-block\"></canvas>");
            sb.Append(cbar);
            sb.Append($"<div style=\"font:11px sans-serif;color:{PlotFg};margin-top:4px\">{ejes}</div>");
            sb.Append("</div>\n");
            sb.Append("<script>(function(){LAB3D.make(document.getElementById('lab3d_").Append(id).Append("'),[");
            sb.Append(FloatCsv(_cvOpaque)).Append("],[").Append(FloatCsv(_cvAlpha)).Append("],[").Append(FloatCsv(_cvLines)).Append("],[");
            sb.Append(x0.ToString(Inv)).Append(',').Append(x1.ToString(Inv)).Append(',')
              .Append(y0.ToString(Inv)).Append(',').Append(y1.ToString(Inv)).Append(',')
              .Append(z0.ToString(Inv)).Append(',').Append(z1.ToString(Inv)).Append("],")
              .Append(_cvAxisEqual ? "1" : "0").Append(',')
              .Append(_figView2 ? "1" : "0").Append(");})();</script>\n");
            return sb.ToString();
        }
        // Mini-motor WebGL embebido (una sola vez por página, guard window.LAB3D). Órbita con mouse.
        private const string Lab3dRenderer = @"<script>window.LAB3D=window.LAB3D||(function(){
function mul(a,b){var r=new Float32Array(16),i,j,k;for(i=0;i<4;i++)for(j=0;j<4;j++){var s=0;for(k=0;k<4;k++)s+=a[k*4+i]*b[j*4+k];r[j*4+i]=s;}return r;}
function persp(fy,as,n,f){var t=1/Math.tan(fy/2);return new Float32Array([t/as,0,0,0,0,t,0,0,0,0,(f+n)/(n-f),-1,0,0,2*f*n/(n-f),0]);}
function tr(x,y,z){return new Float32Array([1,0,0,0,0,1,0,0,0,0,1,0,x,y,z,1]);}
function rx(a){var c=Math.cos(a),s=Math.sin(a);return new Float32Array([1,0,0,0,0,c,s,0,0,-s,c,0,0,0,0,1]);}
function rz(a){var c=Math.cos(a),s=Math.sin(a);return new Float32Array([c,s,0,0,-s,c,0,0,0,0,1,0,0,0,0,1]);}
function scl(x,y,z){return new Float32Array([x,0,0,0,0,y,0,0,0,0,z,0,0,0,0,1]);}
function sh(gl,t,src){var o=gl.createShader(t);gl.shaderSource(o,src);gl.compileShader(o);return o;}
function make(cv,op,al,ln,bb,eq,v2){
if(eq===undefined)eq=1;
if(v2===undefined)v2=0;
var gl=cv.getContext('webgl',{antialias:true,preserveDrawingBuffer:true});if(!gl){cv.parentNode.innerHTML='<div style=color:#a00>WebGL no disponible</div>';return;}
var hasDeriv=!!gl.getExtension('OES_standard_derivatives');
var vs='attribute vec3 p;attribute vec4 c;uniform mat4 m;varying vec4 v;varying vec3 w;void main(){gl_Position=m*vec4(p,1.0);v=c;w=p;}';
var fs='#extension GL_OES_standard_derivatives:enable\nprecision mediump float;varying vec4 v;varying vec3 w;uniform float lit;void main(){vec3 col=v.rgb;if(lit>0.5){vec3 N=normalize(cross(dFdx(w),dFdy(w)));float d=0.5+0.5*abs(dot(N,normalize(vec3(0.4,0.5,0.85))));col=col*d;}gl_FragColor=vec4(col,v.a);}';
var pr=gl.createProgram();gl.attachShader(pr,sh(gl,gl.VERTEX_SHADER,vs));gl.attachShader(pr,sh(gl,gl.FRAGMENT_SHADER,fs));gl.linkProgram(pr);gl.useProgram(pr);
var lp=gl.getAttribLocation(pr,'p'),lc=gl.getAttribLocation(pr,'c'),lm=gl.getUniformLocation(pr,'m'),ll=gl.getUniformLocation(pr,'lit');
function B(a){var b=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,b);gl.bufferData(gl.ARRAY_BUFFER,new Float32Array(a),gl.STATIC_DRAW);return b;}
var ob=B(op),ab=B(al),lb=B(ln),nO=op.length/6,nA=al.length/7,nL=ln.length/6;
var box=[],e=[[0,0,0,1,0,0],[1,0,0,1,1,0],[1,1,0,0,1,0],[0,1,0,0,0,0],[0,0,1,1,0,1],[1,0,1,1,1,1],[1,1,1,0,1,1],[0,1,1,0,0,1],[0,0,0,0,0,1],[1,0,0,1,0,1],[1,1,0,1,1,1],[0,1,0,0,1,1]];
for(var q=0;q<e.length;q++){var s=e[q];box.push(bb[0]+s[0]*(bb[1]-bb[0]),bb[2]+s[1]*(bb[3]-bb[2]),bb[4]+s[2]*(bb[5]-bb[4]),0.20,0.20,0.20,bb[0]+s[3]*(bb[1]-bb[0]),bb[2]+s[4]*(bb[3]-bb[2]),bb[4]+s[5]*(bb[5]-bb[4]),0.20,0.20,0.20);}
var bxb=B(box),nB=box.length/6;
var cx=(bb[0]+bb[1])/2,cy=(bb[2]+bb[3])/2,cz=(bb[4]+bb[5])/2,dx=bb[1]-bb[0],dy=bb[3]-bb[2],dz=bb[5]-bb[4];
dx=dx||1;dy=dy||1;dz=dz||1;
var sx,sy,sz;
if(eq){var mxs=Math.max(dx,dy,dz);sx=sy=sz=2/mxs;}else{sx=2/dx;sy=2/dy;sz=2/dz;}
var ex=sx*dx,ey=sy*dy,ez=sz*dz;
var d0=1.7*Math.sqrt(ex*ex+ey*ey+ez*ez)||3;
// zn = pasos ENTEROS de rueda. dist se recalcula siempre desde d0 con pow(), asi
// acercar y alejar el mismo numero de pasos devuelve el valor EXACTO de partida;
// multiplicando de forma acumulada el error de coma flotante hacia que no regresara.
var st={az:v2?0:-0.7,el:v2?1.5708:0.35,d0:d0,zn:0,dist:d0};
gl.enable(gl.DEPTH_TEST);
function bind(buf,stride,csz){gl.bindBuffer(gl.ARRAY_BUFFER,buf);gl.enableVertexAttribArray(lp);gl.vertexAttribPointer(lp,3,gl.FLOAT,false,stride,0);gl.enableVertexAttribArray(lc);gl.vertexAttribPointer(lc,csz,gl.FLOAT,false,stride,12);}
// --- marcas numericas de los ejes, como MATLAB: se proyectan en cada draw ---
var host=cv.parentNode; if(getComputedStyle(host).position==='static')host.style.position='relative';
var TK=[],NT=4;
function fmt(v){return Math.abs(v)<1e-9?'0':(Math.abs(v)>=100||Math.abs(v)<0.01?v.toExponential(1):(''+(Math.round(v*100)/100)));}
for(var ax=0;ax<3;ax++){if(v2&&ax===2)continue;for(var q=0;q<=NT;q++){
  var f=q/NT,P=[bb[0],bb[2],bb[4]];
  // se separan HACIA AFUERA de la caja para que el solido no los tape,
  // igual que MATLAB, que rotula por fuera de los ejes
  var ox=0.22*(bb[1]-bb[0]),oy=0.22*(bb[3]-bb[2]),oz=0.22*(bb[5]-bb[4]);
  if(ax===0){P[0]=bb[0]+f*(bb[1]-bb[0]); P[1]-=oy; P[2]-=oz;}
  else if(ax===1){P[1]=bb[2]+f*(bb[3]-bb[2]); P[0]-=ox; P[2]-=oz;}
  else {P[2]=bb[4]+f*(bb[5]-bb[4]); P[0]-=ox; P[1]-=oy;}
  var d=document.createElement('div');
  d.style.cssText='position:absolute;font:10px sans-serif;color:#333;pointer-events:none;transform:translate(-50%,-50%)';
  d.textContent=fmt(ax===0?(bb[0]+f*(bb[1]-bb[0])):(ax===1?(bb[2]+f*(bb[3]-bb[2])):(bb[4]+f*(bb[5]-bb[4]))));
  host.appendChild(d); TK.push({el:d,p:P});
}}
function ticks(M){try{
  var W=cv.clientWidth||cv.width, H=cv.clientHeight||cv.height;
  for(var i=0;i<TK.length;i++){
    var p=TK[i].p, w=M[3]*p[0]+M[7]*p[1]+M[11]*p[2]+M[15];
    if(w<=0){TK[i].el.style.display='none';continue;}
    var xc=(M[0]*p[0]+M[4]*p[1]+M[8]*p[2]+M[12])/w;
    var yc=(M[1]*p[0]+M[5]*p[1]+M[9]*p[2]+M[13])/w;
    TK[i].el.style.display='';
    TK[i].el.style.left=((xc*0.5+0.5)*W)+'px';
    TK[i].el.style.top=((1-(yc*0.5+0.5))*H)+'px';
  }
}catch(err){}}
function draw(){
/* fondo BLANCO como MATLAB. OJO: aqui NO se puede usar comentario de linea (//),
   porque todo esto se emite en UNA sola linea de JavaScript y se comeria el
   gl.clear() de abajo; sin limpiar el buffer de profundidad la escena se queda
   congelada al girar (la geometria nueva queda detras y la descarta el depth test). */
gl.viewport(0,0,cv.width,cv.height);gl.clearColor(1.0,1.0,1.0,1.0);gl.clear(gl.COLOR_BUFFER_BIT|gl.DEPTH_BUFFER_BIT);
var M=mul(persp(0.6,cv.width/cv.height,0.01,st.dist*12),mul(tr(0,0,-st.dist),mul(mul(rx(st.el-1.5708),rz(st.az)),mul(scl(sx,sy,sz),tr(-cx,-cy,-cz)))));
gl.uniformMatrix4fv(lm,false,M);
gl.uniform1f(ll,0.0);
if(nL>0){bind(lb,24,3);gl.drawArrays(gl.LINES,0,nL);}
gl.uniform1f(ll,hasDeriv?1.0:0.0);gl.disable(gl.BLEND);gl.depthMask(true);
if(nO>0){bind(ob,24,3);gl.drawArrays(gl.TRIANGLES,0,nO);}
if(nA>0){bind(ab,28,4);gl.enable(gl.BLEND);gl.blendFunc(gl.SRC_ALPHA,gl.ONE_MINUS_SRC_ALPHA);gl.depthMask(false);gl.drawArrays(gl.TRIANGLES,0,nA);gl.depthMask(true);gl.disable(gl.BLEND);}ticks(M);
}
draw();
var dr=false,mx=0,my=0;
cv.addEventListener('mousedown',function(ev){dr=true;mx=ev.clientX;my=ev.clientY;cv.style.cursor='grabbing';});
window.addEventListener('mouseup',function(){dr=false;cv.style.cursor='grab';});
window.addEventListener('mousemove',function(ev){if(!dr)return;st.az+=(ev.clientX-mx)*0.01;st.el+=(ev.clientY-my)*0.01;if(st.el>1.55)st.el=1.55;if(st.el<-1.55)st.el=-1.55;mx=ev.clientX;my=ev.clientY;draw();});
// {passive:false} es OBLIGATORIO: en WebView2/Chromium los listeners de 'wheel' son
// PASIVOS por defecto, se ignora el preventDefault() y la PAGINA hace scroll en vez
// de que el modelo haga zoom. Se toca la escena tambien con touch (pinch no, pero si arrastre).
cv.addEventListener('wheel',function(ev){ev.preventDefault();ev.stopPropagation();st.zn+=ev.deltaY>0?1:-1;if(st.zn>60)st.zn=60;if(st.zn<-60)st.zn=-60;st.dist=st.d0*Math.pow(1.1,st.zn);if(st.dist<0.05)st.dist=0.05;draw();},{passive:false});
}
return {make:make};
})();</script>
";

        /// <summary>Comienza nueva figura. Devuelve el HTML del anterior figura (si la había) para emitirlo.</summary>
        public static string BeginFigure()
        {
            string prev = FinishFigure();
            ResetRetainedMesh();   // figura nueva → olvida la malla retenida anterior
            FigPxW = null; FigPxH = null;   // cada figure() arranca con el tamano por defecto (como MATLAB)
            _figBandN = 0;          // figura nueva → degradado por defecto (el script pide colorbands)
            _figTraces = new System.Collections.Generic.List<string>();
            _figAnnotations = new System.Collections.Generic.List<string>();
            _figPrims = new System.Collections.Generic.List<FigPrim>();
            _figId = ++_plotCounter;
            _figIs3D = false;
            _figView2 = false;
            CvReset();
            _figTitle = "";
            _figXLabel = null; _figYLabel = null; _figZLabel = null;
            _figXMin = null; _figXMax = null; _figYMin = null; _figYMax = null;
            _figGrid = false;
            _figColorbar = false; _cbReverse = false; _cbTicks = null; _cbTickLabels = null; _cbLabel = null;
            _figAxisEqual = false;
            _figAxisOff = false;
            _figShowLegend = false; _figLegendLoc = null; _figLegendNames = null;
            return prev;
        }
        private static int _imgZoomId = 0;
        /// <summary>Envuelve un PNG base64 en un contenedor con zoom/pan por JS (rueda=zoom,
        /// arrastrar=pan, doble-clic=reset) — conserva la gráfica nítida (=MATLAB) e interactiva.</summary>
        private static string ZoomableImgHtml(string b64, int maxW = 1120)
        {
            // 2026-09-05 (Jorge): IGUAL que las graficas de Hekatan Python. Antes la figura tenia su propio
            // zoom con la rueda (preventDefault) + barras de desplazamiento y ancho fijo de 560 px: se tragaba
            // el Ctrl+rueda del WebView2 y no crecia con la pagina. Ahora es una imagen normal
            // (max-width:100%, alto automatico): Ctrl+rueda = zoom de toda la hoja, como en Python.
            int k = ++_imgZoomId;
            var sb = new StringBuilder();
            sb.Append("<div id=\"zw").Append(k).Append("\" class=\"matlab-plot\" style=\"max-width:100%\">");
            sb.Append("<img id=\"zi").Append(k).Append("\" src=\"data:image/png;base64,").Append(b64);
            sb.Append("\" style=\"width:100%;max-width:").Append(maxW).Append("px;height:auto;display:block\"/></div>\n");
            return sb.ToString();
        }
        /// <summary>Cierra figura abierta y devuelve su HTML.</summary>
        public static string FinishFigure()
        {
            bool noTraces = _figTraces == null || _figTraces.Count == 0;
            bool noPrims  = _figPrims  == null || _figPrims.Count  == 0;
            if (noTraces && noPrims)
            {
                _figTraces = null; _figAnnotations = null;
                return "";
            }
            // Captura PNG (modo export sin navegador) ANTES de consumir la figura.
            // 560×420 = tamaño de figura por defecto de MATLAB (para paridad pixel a pixel).
            if (PngExportMode)
            {
                if (RetainedActive) BuildRetainedFaces();
                try { var _png = RasterizeFigurePng(560, 420, 2); if (_png != null && _png.Length > 0) ExportedPngs.Add(_png); }   // ss=2: PNG nítido (1120×840), layout lógico 560×420 (paridad MATLAB)
                catch { /* figura no rasterizable (p.ej. 3D webgl) → se omite */ }
            }
            // Malla 2D CON valor por-cara (patch FaceVertexCData) → CANVAS interactivo con hover.
            if (RetainedActive) BuildRetainedFaces();   // figura FINAL desde el estado retenido (última mutación de set)
            if (!_figIs3D && HasFaceValues())
            {
                string iv = RenderInteractiveMesh(560, 420);   // mismas dims que el export estático -> hover idéntico
                if (iv != null) { ResetRetainedMesh(); return iv; }
            }
            // contourf (fieldfill): render CANVAS PNG (idéntico al del CLI, alineado a MATLAB),
            // no Plotly (que salía parula y sin deformar en el WPF).
            if (!_figIs3D && _figPrims != null && _figPrims.Exists(p => p.Kind == "fieldfill"))
            {
                try
                {
                    // Render a 2× (1120×840, misma proporcion que MATLAB 560×420) y mostrar a 560px
                    // -> NÍTIDO en high-DPI. Envuelto en zoom/pan JS (rueda=zoom, arrastrar=pan,
                    // doble-clic=reset) para no perder la interactividad del embebido.
                    // figure('Position',[l b w h]) manda tambien AQUI (no solo en print): layout logico w x h
                    // a 2x y mostrado a w px, como MATLAB. Sin Position: 1120x840 (= 560x420 de MATLAB a 2x).
                    // Antes el talud (990x594 pedidos) salia en un lienzo 4:3 con medio panel en blanco (2026-09-05).
                    byte[] png; int shownW = 1120;
                    if (FigPxW.HasValue && FigPxH.HasValue) { png = RasterizeFigurePng(FigPxW.Value, FigPxH.Value, 2); shownW = FigPxW.Value; }
                    else png = RasterizeFigurePng(1120, 840);
                    if (png != null && png.Length > 0)
                    {
                        string b64 = System.Convert.ToBase64String(png);
                        _figTraces = null; _figAnnotations = null; _figPrims = null;
                        return ZoomableImgHtml(b64, shownW);
                    }
                }
                catch { }
            }
            // DIBUJO 2D estructural (malla: patches/texto/markers) → SVG inline (nítido,
            // numeración fiable). Plotly se reserva para resultados (surf/contour/datos).
            // OJO: los markers NO disparan esta ruta por si solos. Un plot(x,y,'o')
            // es una grafica de DATOS normal, y mandarla al SVG estructural le
            // quitaba la leyenda entera (el SVG no la dibuja) y el tamano del
            // marcador. Bastaba un solo marcador para perder el legend() de toda
            // la figura. Con patches o texto SI: eso es un dibujo de malla, que es
            // para lo que se hizo esta ruta; ahi los markers siguen yendo con ella.
            if (!_figIs3D && _figPrims != null &&
                _figPrims.Exists(p => p.Kind == "patch2d" || p.Kind == "text2d"))
            {
                string svgInner = ExportSvg(760, 580);
                _figTraces = null; _figAnnotations = null; _figPrims = null;
                return svgInner == null ? "" : $"<div class=\"matlab-plot matlab-svg\">{svgInner}</div>\n";
            }
            // RENDER 3D vía CANVAS/WebGL (rápido, sin CDN) si está habilitado y hay geometría.
            if (_figIs3D && Use3DCanvas && _cvAny)
            {
                string canvasHtml = FinishFigureCanvas();
                _figTraces = null; _figAnnotations = null; _figPrims = null;
                return canvasHtml;
            }
            // Guarda defensiva: si llegamos aquí sin trazas (doble-finish, prims ya
            // consumidos, etc.) no hay nada que serializar en Plotly → evitar el
            // NullReferenceException en _figTraces.Count.
            if (_figTraces == null) { _figAnnotations = null; _figPrims = null; return ""; }
            var sb = new StringBuilder();
            sb.Append($"<div id=\"matlab_plot_{_figId}\" class=\"matlab-plot\" style=\"width:720px;height:560px\"></div>\n");
            sb.Append("<script>(function() {\n  var data = [\n");
            for (int i = 0; i < _figTraces.Count; i++)
            {
                sb.Append("    ").Append(_figTraces[i]);
                if (i < _figTraces.Count - 1) sb.Append(",");
                sb.Append("\n");
            }
            sb.Append("  ];\n  var layout = { ");
            sb.Append($"title:{{text:'{EscapeJs(_figTitle)}'}}, margin:{{l:70,r:30,t:45,b:65}}");
            // Tema oscuro: fondo/texto/ejes claros (en dark no puede haber blanco).
            sb.Append($", paper_bgcolor:'{PlotBg}', plot_bgcolor:'{PlotBg}', font:{{color:'{PlotFg}'}}");
            if (_figShowLegend) sb.Append($", showlegend:true, legend:{LegendPosJson(_figLegendLoc)}");
            if (_figIs3D)
            {
                // Escena 3D tematizada: en dark el cubo/ejes/fondo van oscuros (no blanco).
                string axExtra = $"color:'{PlotFg}', gridcolor:'{PlotGrid}', backgroundcolor:'{PlotBg}', showbackground:true";
                sb.Append($", scene: {{ bgcolor:'{PlotBg}', ");
                sb.Append($"xaxis:{{title:{{text:'{EscapeJs(_figXLabel ?? "x")}'}}, {axExtra}}},");
                sb.Append($"yaxis:{{title:{{text:'{EscapeJs(_figYLabel ?? "y")}'}}, {axExtra}}},");
                sb.Append($"zaxis:{{title:{{text:'{EscapeJs(_figZLabel ?? "z")}'}}, {axExtra}}}");
                sb.Append(" }");
            }
            else if (_figAxisOff)
            {
                // axis off: ejes INVISIBLES (sin línea, ticks, etiquetas ni grilla) — figura
                // limpia como Hekatan LISP. Mantengo el rango fijado y, si hay axis equal, el
                // scaleanchor, para que el dibujo no se distorsione.
                var xoff = new System.Collections.Generic.List<string> { "visible:false" };
                if (_figXMin.HasValue) xoff.Add($"range:[{_figXMin.Value.ToString(Inv)}, {_figXMax.Value.ToString(Inv)}]");
                sb.Append(", xaxis:{").Append(string.Join(", ", xoff)).Append("}");
                var yoff = new System.Collections.Generic.List<string> { "visible:false" };
                if (_figYMin.HasValue) yoff.Add($"range:[{_figYMin.Value.ToString(Inv)}, {_figYMax.Value.ToString(Inv)}]");
                if (_figAxisEqual) { yoff.Add("scaleanchor:'x'"); yoff.Add("scaleratio:1"); }
                sb.Append(", yaxis:{").Append(string.Join(", ", yoff)).Append("}");
            }
            else
            {
                // xaxis: unir partes presentes con coma (evita '{,' inicial inválido)
                var xparts = new System.Collections.Generic.List<string>();
                if (_figXLabel != null) xparts.Add($"title:{{text:'{EscapeJs(_figXLabel)}'}}");
                if (_figXMin.HasValue) xparts.Add($"range:[{_figXMin.Value.ToString(Inv)}, {_figXMax.Value.ToString(Inv)}]");
                xparts.Add($"color:'{PlotFg}'"); xparts.Add($"gridcolor:'{PlotGrid}'"); xparts.Add($"zerolinecolor:'{PlotGrid}'"); xparts.Add("automargin:true");
                // "Cuadro" estilo MATLAB: caja de ejes en los 4 lados (mirror) + ticks HACIA ADENTRO.
                xparts.Add($"showline:true"); xparts.Add($"mirror:true"); xparts.Add($"linecolor:'{PlotFg}'"); xparts.Add("linewidth:1");
                xparts.Add("ticks:'inside'"); xparts.Add($"tickcolor:'{PlotFg}'"); xparts.Add("ticklen:5");
                sb.Append(", xaxis:{").Append(string.Join(", ", xparts)).Append("}");
                // yaxis: igual + aspecto cuadrado (scaleanchor)
                var yparts = new System.Collections.Generic.List<string>();
                if (_figYLabel != null) yparts.Add($"title:{{text:'{EscapeJs(_figYLabel)}'}}");
                if (_figYMin.HasValue) yparts.Add($"range:[{_figYMin.Value.ToString(Inv)}, {_figYMax.Value.ToString(Inv)}]");
                yparts.Add($"color:'{PlotFg}'"); yparts.Add($"gridcolor:'{PlotGrid}'"); yparts.Add($"zerolinecolor:'{PlotGrid}'"); yparts.Add("automargin:true");
                yparts.Add($"showline:true"); yparts.Add($"mirror:true"); yparts.Add($"linecolor:'{PlotFg}'"); yparts.Add("linewidth:1");
                yparts.Add("ticks:'inside'"); yparts.Add($"tickcolor:'{PlotFg}'"); yparts.Add("ticklen:5");
                if (_figAxisEqual) { yparts.Add("scaleanchor:'x'"); yparts.Add("scaleratio:1"); }  // solo si axis('equal')
                sb.Append(", yaxis:{").Append(string.Join(", ", yparts)).Append("}");
            }
            // annotations
            if (_figAnnotations.Count > 0)
            {
                sb.Append(", annotations:[");
                for (int i = 0; i < _figAnnotations.Count; i++)
                {
                    sb.Append(_figAnnotations[i]);
                    if (i < _figAnnotations.Count - 1) sb.Append(",");
                }
                sb.Append("]");
            }
            sb.Append(" };\n  Plotly.newPlot('matlab_plot_").Append(_figId)
              .Append("', data, layout, {responsive:true});\n");
            // legend('a','b','c'): asigna los nombres a las primeras trazas y las muestra
            // (las trazas se crean sin nombre; los nombres llegan después con legend()).
            if (_figLegendNames != null && _figLegendNames.Length > 0)
            {
                int nn = Math.Min(_figLegendNames.Length, _figTraces.Count);
                var names = new StringBuilder();
                var idxs = new StringBuilder();
                for (int i = 0; i < nn; i++)
                {
                    if (i > 0) { names.Append(","); idxs.Append(","); }
                    names.Append($"'{EscapeJs(_figLegendNames[i])}'");
                    idxs.Append(i);
                }
                sb.Append($"  Plotly.restyle('matlab_plot_{_figId}', {{name:[{names}], showlegend:[")
                  .Append(string.Join(",", System.Linq.Enumerable.Repeat("true", nn)))
                  .Append($"]}}, [{idxs}]);\n");
            }
            sb.Append("})();</script>\n");
            // Limpiar _figPrims TAMBIÉN (las otras rutas de retorno ya lo hacían). Antes se
            // dejaba con los line2d de este panel → en subplot, el siguiente FinishFigure
            // veía traces=NULL + un prim rancio, no retornaba temprano, y crasheaba en la
            // serialización Plotly (_figTraces.Count sobre null).
            _figTraces = null; _figAnnotations = null; _figPrims = null;
            return sb.ToString();
        }
        public static void AddTrace(string traceJson) {
            if (_figTraces == null) BeginFigure();
            _figTraces.Add(traceJson);
        }
        public static void AddAnnotation(string annJson) {
            if (_figAnnotations == null) BeginFigure();
            _figAnnotations.Add(annJson);
        }
        public static void SetFigure3D(bool is3d) { if (_figTraces != null) _figIs3D = is3d; }
        public static void SetLegend(string loc, string[] names = null) {
            _figShowLegend = true;
            if (loc != null) _figLegendLoc = loc;
            if (names != null && names.Length > 0) _figLegendNames = names;
        }
        /// <summary>Ubicación MATLAB de la leyenda -> posición Plotly (dentro de los ejes).</summary>
        public static string LegendPosJson(string loc)
        {
            // La leyenda SIGUE AL TEMA. Con el blanco fijo, en oscuro salía una caja clara con
            // el texto claro encima (PlotFg) y no se leían los nombres de las curvas.
            string bg = DarkTheme
                ? $"bgcolor:'rgba(26,23,18,0.85)',bordercolor:'{PlotGrid}',borderwidth:1,font:{{color:'{PlotFg}'}}"
                : $"bgcolor:'rgba(237,228,206,0.85)',bordercolor:'{PlotGrid}',borderwidth:1,font:{{color:'{PlotFg}'}}";
            double x = 0.98, y = 0.98; string xa = "right", ya = "top";
            switch ((loc ?? "northeast").ToLowerInvariant().Replace("outside", ""))
            {
                case "northwest": x=0.02; y=0.98; xa="left";  ya="top"; break;
                case "southeast": x=0.98; y=0.02; xa="right"; ya="bottom"; break;
                case "southwest": x=0.02; y=0.02; xa="left";  ya="bottom"; break;
                case "north": x=0.5; y=0.98; xa="center"; ya="top"; break;
                case "south": x=0.5; y=0.02; xa="center"; ya="bottom"; break;
                case "east":  x=0.98; y=0.5; xa="right"; ya="middle"; break;
                case "west":  x=0.02; y=0.5; xa="left";  ya="middle"; break;
                default: x=0.98; y=0.98; xa="right"; ya="top"; break;
            }
            return $"{{x:{x.ToString(Inv)},y:{y.ToString(Inv)},xanchor:'{xa}',yanchor:'{ya}',{bg}}}";
        }
        public static void SetFigTitle(string t) { _figTitle = t ?? ""; }
        public static void SetFigXLabel(string s) { _figXLabel = s; }
        public static void SetFigYLabel(string s) { _figYLabel = s; }
        public static void SetFigZLabel(string s) { _figZLabel = s; }

        // ── Intérprete TeX de MATLAB para title()/xlabel()/ylabel(): _{...}/^{...} (subíndice/
        //    superíndice), _c/^c (un token), y \alpha \Delta \leq ... (griegas y símbolos). ──
        private static readonly System.Collections.Generic.Dictionary<string,string> _texSym =
            new System.Collections.Generic.Dictionary<string,string>(System.StringComparer.Ordinal) {
            {"alpha","α"},{"beta","β"},{"gamma","γ"},{"delta","δ"},{"epsilon","ε"},{"varepsilon","ε"},
            {"zeta","ζ"},{"eta","η"},{"theta","θ"},{"vartheta","ϑ"},{"iota","ι"},{"kappa","κ"},
            {"lambda","λ"},{"mu","μ"},{"nu","ν"},{"xi","ξ"},{"pi","π"},{"rho","ρ"},{"sigma","σ"},
            {"tau","τ"},{"upsilon","υ"},{"phi","φ"},{"varphi","φ"},{"chi","χ"},{"psi","ψ"},{"omega","ω"},
            {"Gamma","Γ"},{"Delta","Δ"},{"Theta","Θ"},{"Lambda","Λ"},{"Xi","Ξ"},{"Pi","Π"},
            {"Sigma","Σ"},{"Phi","Φ"},{"Psi","Ψ"},{"Omega","Ω"},
            {"leq","≤"},{"geq","≥"},{"neq","≠"},{"times","×"},{"cdot","·"},{"pm","±"},{"mp","∓"},
            {"infty","∞"},{"partial","∂"},{"nabla","∇"},{"rightarrow","→"},{"leftarrow","←"},
            {"Rightarrow","⇒"},{"approx","≈"},{"propto","∝"},{"circ","°"},{"degree","°"},
            {"sqrt","√"},{"sum","∑"},{"int","∫"},{"in","∈"},{"cdots","⋯"},{"ldots","…"},
        };
        private struct TexRun { public string Text; public int Lvl; }   // -1 sub, 0 normal, +1 sup
        /// <summary>Reemplaza \nombre por su símbolo (griegas/símbolos), sin manejar sub/sup.</summary>
        private static string TexPlain(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0) return s;
            var sb = new StringBuilder(s.Length);
            for (int i = 0; i < s.Length; )
            {
                if (s[i] == '\\')
                {
                    int j = i + 1; while (j < s.Length && char.IsLetter(s[j])) j++;
                    string name = s.Substring(i + 1, j - i - 1);
                    if (name.Length > 0 && _texSym.TryGetValue(name, out var sym))
                    { sb.Append(sym); i = j; if (i < s.Length && s[i] == ' ') i++; continue; }
                    if (j < s.Length) { sb.Append(s[i + 1]); i += 2; } else i++;
                }
                else { sb.Append(s[i]); i++; }
            }
            return sb.ToString();
        }
        /// <summary>Parte una cadena TeX en runs normal/subíndice/superíndice.</summary>
        private static System.Collections.Generic.List<TexRun> TexParse(string s)
        {
            var runs = new System.Collections.Generic.List<TexRun>();
            if (string.IsNullOrEmpty(s)) return runs;
            var buf = new StringBuilder();
            void Flush() { if (buf.Length > 0) { runs.Add(new TexRun { Text = buf.ToString(), Lvl = 0 }); buf.Clear(); } }
            for (int i = 0; i < s.Length; )
            {
                char c = s[i];
                if (c == '\\')
                {
                    int j = i + 1; while (j < s.Length && char.IsLetter(s[j])) j++;
                    string name = s.Substring(i + 1, j - i - 1);
                    if (name.Length > 0 && _texSym.TryGetValue(name, out var sym))
                    { buf.Append(sym); i = j; if (i < s.Length && s[i] == ' ') i++; continue; }
                    if (j < s.Length) { buf.Append(s[i + 1]); i += 2; } else i++;
                    continue;
                }
                if (c == '_' || c == '^')
                {
                    int lvl = c == '_' ? -1 : 1; Flush(); i++;
                    if (i < s.Length && s[i] == '{')
                    {
                        int depth = 1, k = i + 1; var inner = new StringBuilder();
                        while (k < s.Length && depth > 0)
                        { if (s[k] == '{') depth++; else if (s[k] == '}') { depth--; if (depth == 0) break; } inner.Append(s[k]); k++; }
                        runs.Add(new TexRun { Text = TexPlain(inner.ToString()), Lvl = lvl }); i = k + 1;
                    }
                    else if (i < s.Length) { runs.Add(new TexRun { Text = TexPlain(s[i].ToString()), Lvl = lvl }); i++; }
                    continue;
                }
                buf.Append(c); i++;
            }
            Flush();
            return runs;
        }
        /// <summary>¿La cadena contiene marcas TeX que requieran interpretación?</summary>
        private static bool HasTex(string s) => !string.IsNullOrEmpty(s) &&
            (s.IndexOf('_') >= 0 || s.IndexOf('^') >= 0 || s.IndexOf('\\') >= 0);
        /// <summary>TeX → HTML (con &lt;sub&gt;/&lt;sup&gt;), ya escapado para insertar en el div.</summary>
        /// <summary>Griegas y simbolos TeX de MATLAB a Unicode: \\phi -> φ, \\gamma -> γ, \\circ -> °.</summary>
        public static string TexGreek(string s)
        {
            if (string.IsNullOrEmpty(s) || s.IndexOf('\\') < 0) return s ?? "";
            string[,] G = {
                {"alpha","α"},{"beta","β"},{"gamma","γ"},{"delta","δ"},{"epsilon","ε"},{"varepsilon","ε"},{"zeta","ζ"},{"eta","η"},
                {"theta","θ"},{"vartheta","ϑ"},{"iota","ι"},{"kappa","κ"},{"lambda","λ"},{"mu","μ"},{"nu","ν"},{"xi","ξ"},
                {"pi","π"},{"rho","ρ"},{"sigma","σ"},{"tau","τ"},{"upsilon","υ"},{"phi","φ"},{"varphi","φ"},{"chi","χ"},
                {"psi","ψ"},{"omega","ω"},{"Gamma","Γ"},{"Delta","Δ"},{"Theta","Θ"},{"Lambda","Λ"},{"Xi","Ξ"},{"Pi","Π"},
                {"Sigma","Σ"},{"Phi","Φ"},{"Psi","Ψ"},{"Omega","Ω"},{"circ","°"},{"pm","±"},{"infty","∞"},{"leq","≤"},{"geq","≥"},
                {"neq","≠"},{"approx","≈"},{"times","×"},{"cdot","·"},{"rightarrow","→"},{"leftarrow","←"},{"partial","∂"},{"nabla","∇"} };
            var sb = new StringBuilder(s);
            for (int i = 0; i < G.GetLength(0); i++) sb.Replace("\\" + G[i, 0], G[i, 1]);
            return sb.ToString();
        }
        /// <summary>Texto TeX -> plano (griegas a Unicode, sin marcas de sub/superindice) para medir/dibujar simple.</summary>
        private static string TexPlainG(string s)
        {
            s = TexGreek(s);
            if (string.IsNullOrEmpty(s)) return "";
            if (!HasTex(s)) return s;
            var sb = new StringBuilder();
            foreach (var r in TexParse(s)) sb.Append(r.Text);
            return sb.ToString();
        }
        private static string TexToHtml(string s)
        {
            if (!HasTex(s)) return EscapeXml(s ?? "");
            var sb = new StringBuilder();
            foreach (var r in TexParse(s))
            {
                if (r.Lvl == -1) { sb.Append("<sub>").Append(EscapeXml(r.Text)).Append("</sub>"); }
                else if (r.Lvl == 1) { sb.Append("<sup>").Append(EscapeXml(r.Text)).Append("</sup>"); }
                else sb.Append(EscapeXml(r.Text));
            }
            return sb.ToString();
        }
        /// <summary>TeX → SVG con &lt;tspan&gt; (baseline-shift) para sub/superíndices.</summary>
        private static string TexToSvg(string s)
        {
            if (!HasTex(s)) return EscapeXml(s ?? "");
            var sb = new StringBuilder();
            foreach (var r in TexParse(s))
            {
                if (r.Lvl == -1) sb.Append("<tspan baseline-shift='-25%' font-size='70%'>").Append(EscapeXml(r.Text)).Append("</tspan>");
                else if (r.Lvl == 1) sb.Append("<tspan baseline-shift='super' font-size='70%'>").Append(EscapeXml(r.Text)).Append("</tspan>");
                else sb.Append(EscapeXml(r.Text));
            }
            return sb.ToString();
        }
        /// <summary>Dibuja texto TeX centrado en (cx, baseY) con sub/superíndices (SkiaSharp).</summary>
        private static void DrawTexCentered(SkiaSharp.SKCanvas canvas, string s, float cx, float baseY,
                                            SkiaSharp.SKFont font, SkiaSharp.SKPaint paint, SkiaSharp.SKTypeface tface)
        {
            if (!HasTex(s)) { canvas.DrawText(s, cx, baseY, SkiaSharp.SKTextAlign.Center, font, paint); return; }
            var runs = TexParse(s);
            using var subFont = new SkiaSharp.SKFont(tface, font.Size * 0.72f) { Embolden = font.Embolden };
            float total = 0;
            foreach (var r in runs) { var f = r.Lvl == 0 ? font : subFont; total += f.MeasureText(r.Text); }
            float x = cx - total / 2f;
            foreach (var r in runs)
            {
                var f = r.Lvl == 0 ? font : subFont;
                float dy = r.Lvl == -1 ? font.Size * 0.28f : (r.Lvl == 1 ? -font.Size * 0.34f : 0f);
                canvas.DrawText(r.Text, x, baseY + dy, SkiaSharp.SKTextAlign.Left, f, paint);
                x += f.MeasureText(r.Text);
            }
        }
        private static string EscapeJs(string s)
            => (s ?? "").Replace("\\", "\\\\").Replace("'", "\\'");
        public static string Csv(MValue v)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < v.Data.Length; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(v.Data[i].ToString("G", Inv));
            }
            return sb.ToString();
        }
        public static string Csv(double[] arr)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < arr.Length; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(arr[i].ToString("G", Inv));
            }
            return sb.ToString();
        }

        /// <summary>Mesh 2D/3D con conectividad triangular + CData nodal o por elemento.</summary>
        /// <param name="faces">Conectividad nF×3 (índices 1-based).</param>
        /// <param name="verts">Coordenadas nV×2 ó nV×3.</param>
        /// <param name="cdata">CData por nodo (length nV) o por elemento (length nF) o null.</param>
        /// <param name="colorMode">"interp" (nodal), "flat" (por elemento), o "uniform" (color sólido).</param>
        public static void PatchMesh(MValue faces, MValue verts, MValue cdata, string colorMode,
                                      string faceColor, string edgeColor, double faceAlpha, double lineWidth,
                                      string colormap, bool quadSplit = false)
        {
            SetCmapName(colormap);   // la colorbar debe usar el MISMO colormap
            int nF = faces.Rows;
            int nV = verts.Rows;
            bool is3D = verts.Cols >= 3;
            // Construir arrays x, y, z
            var xArr = new double[nV];
            var yArr = new double[nV];
            var zArr = new double[nV];
            for (int i = 0; i < nV; i++)
            {
                xArr[i] = verts.At(i, 0);
                yArr[i] = verts.At(i, 1);
                zArr[i] = is3D ? verts.At(i, 2) : 0.0;
            }
            // Construir índices i, j, k (0-based para Plotly)
            var iArr = new int[nF];
            var jArr = new int[nF];
            var kArr = new int[nF];
            for (int f = 0; f < nF; f++)
            {
                iArr[f] = (int)faces.At(f, 0) - 1;
                jArr[f] = (int)faces.At(f, 1) - 1;
                kArr[f] = (int)faces.At(f, 2) - 1;
            }
            var sb = new StringBuilder();
            sb.Append("{type:'mesh3d'");
            sb.Append($", x:[{Csv(xArr)}]");
            sb.Append($", y:[{Csv(yArr)}]");
            sb.Append($", z:[{Csv(zArr)}]");
            sb.Append($", i:[{IntCsv(iArr)}]");
            sb.Append($", j:[{IntCsv(jArr)}]");
            sb.Append($", k:[{IntCsv(kArr)}]");
            sb.Append($", opacity:{faceAlpha.ToString(Inv)}");
            sb.Append($", flatshading:{(colorMode == "flat" ? "true" : "false")}");
            // Color: 3 modos
            if (cdata != null && (colorMode == "interp" || colorMode == "flat"))
            {
                if (colorMode == "flat" && cdata.Data.Length == nF)
                {
                    // Color por elemento: usar facecolor con rgb strings derivados de cdata
                    sb.Append(", facecolor:[");
                    double cmin = double.MaxValue, cmax = double.MinValue;
                    for (int f = 0; f < nF; f++)
                    {
                        if (cdata.Data[f] < cmin) cmin = cdata.Data[f];
                        if (cdata.Data[f] > cmax) cmax = cdata.Data[f];
                    }
                    double rng = (cmax - cmin) > 1e-12 ? cmax - cmin : 1;
                    for (int f = 0; f < nF; f++)
                    {
                        if (f > 0) sb.Append(",");
                        double tc = (cdata.Data[f] - cmin) / rng;   // [0,1]
                        sb.Append("'").Append(ColorscaleSampleRgb(colormap, tc)).Append("'");
                    }
                    sb.Append("]");
                }
                else
                {
                    sb.Append($", intensity:[{Csv(cdata)}]");
                    sb.Append($", intensitymode:'{(colorMode == "flat" ? "cell" : "vertex")}'");
                    sb.Append($", colorscale:{ColorscaleJs(colormap)}");
                    sb.Append($", reversescale:{(ColormapReversed(colormap) ? "true" : "false")}");
                    sb.Append(", showscale:true");
                }
            }
            else
            {
                sb.Append($", color:'{faceColor}'");
            }
            // Lighting realista (Plotly mesh3d acepta esto)
            sb.Append(", lighting:{ambient:0.6, diffuse:0.8, specular:0.2, roughness:0.5, fresnel:0.2}");
            sb.Append(", lightposition:{x:200, y:200, z:200}");
            sb.Append("}");
            AddTrace(sb.ToString());
            // --- geometría CANVAS: un triángulo por cara, coloreado como en Plotly ---
            if (is3D)
            {
                float[] solid = CssToRgbF(faceColor);
                bool useC = cdata != null && (colorMode == "interp" || colorMode == "flat");
                double cmin = 0, cmax = 1, rng = 1;
                if (useC)
                {
                    cmin = double.MaxValue; cmax = double.MinValue;
                    for (int t = 0; t < cdata.Data.Length; t++) { if (cdata.Data[t] < cmin) cmin = cdata.Data[t]; if (cdata.Data[t] > cmax) cmax = cdata.Data[t]; }
                    rng = (cmax - cmin) > 1e-12 ? cmax - cmin : 1;
                    // propagar el rango de CData a la COLORBAR (antes mostraba 0..1 fijo)
                    SetColorRange(cmin, cmax);
                }
                float[] VC(int vi, int fi)
                {
                    if (!useC) return solid;
                    if (colorMode == "flat" && cdata.Data.Length == nF) return CmapF(colormap, (cdata.Data[fi] - cmin) / rng);
                    if (vi < cdata.Data.Length) return CmapF(colormap, (cdata.Data[vi] - cmin) / rng);
                    return solid;
                }
                for (int f = 0; f < nF; f++)
                {
                    int a = iArr[f], b = jArr[f], c = kArr[f];
                    CvTri(xArr[a], yArr[a], zArr[a], VC(a, f), xArr[b], yArr[b], zArr[b], VC(b, f), xArr[c], yArr[c], zArr[c], VC(c, f), faceAlpha);
                }
            }
            // Edges (wireframe) si edgeColor distinto a 'none'
            if (edgeColor != "none" && lineWidth > 0)
            {
                // Emitir como scatter3d/scatter de aristas
                EmitMeshEdges(xArr, yArr, zArr, iArr, jArr, kArr, edgeColor, lineWidth, is3D);
                // --- geometría CANVAS: aristas de la malla ---
                if (is3D)
                {
                    float[] ec = CssToRgbF(edgeColor);
                    for (int f = 0; f < nF; f++)
                    {
                        int a = iArr[f], b = jArr[f], c = kArr[f];
                        // Q4 dividido en 2 T3 → pares (v1,v2,v3),(v1,v3,v4): omitir la diagonal
                        // compartida (v1,v3) para que el pedestal muestre rectángulos, no X.
                        if (quadSplit && (f % 2) == 0)
                        { CvLine(xArr[a], yArr[a], zArr[a], xArr[b], yArr[b], zArr[b], ec); CvLine(xArr[b], yArr[b], zArr[b], xArr[c], yArr[c], zArr[c], ec); }
                        else if (quadSplit)
                        { CvLine(xArr[b], yArr[b], zArr[b], xArr[c], yArr[c], zArr[c], ec); CvLine(xArr[c], yArr[c], zArr[c], xArr[a], yArr[a], zArr[a], ec); }
                        else
                        {
                            CvLine(xArr[a], yArr[a], zArr[a], xArr[b], yArr[b], zArr[b], ec);
                            CvLine(xArr[b], yArr[b], zArr[b], xArr[c], yArr[c], zArr[c], ec);
                            CvLine(xArr[c], yArr[c], zArr[c], xArr[a], yArr[a], zArr[a], ec);
                        }
                    }
                }
            }
            if (is3D) _figIs3D = true;
        }
        private static string IntCsv(int[] arr)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < arr.Length; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(arr[i]);
            }
            return sb.ToString();
        }
        private static void EmitMeshEdges(double[] x, double[] y, double[] z,
                                           int[] iIdx, int[] jIdx, int[] kIdx,
                                           string color, double lw, bool is3D)
        {
            // Construir lista de aristas únicas
            var edges = new System.Collections.Generic.HashSet<(int, int)>();
            for (int f = 0; f < iIdx.Length; f++)
            {
                AddEdge(edges, iIdx[f], jIdx[f]);
                AddEdge(edges, jIdx[f], kIdx[f]);
                AddEdge(edges, kIdx[f], iIdx[f]);
            }
            // Para Plotly: emitir como scatter3d con cada arista separada por NaN
            var xLines = new System.Collections.Generic.List<double>();
            var yLines = new System.Collections.Generic.List<double>();
            var zLines = new System.Collections.Generic.List<double>();
            foreach (var (u, v) in edges)
            {
                xLines.Add(x[u]); xLines.Add(x[v]); xLines.Add(double.NaN);
                yLines.Add(y[u]); yLines.Add(y[v]); yLines.Add(double.NaN);
                zLines.Add(z[u]); zLines.Add(z[v]); zLines.Add(double.NaN);
            }
            var sb = new StringBuilder();
            sb.Append(is3D ? "{type:'scatter3d', mode:'lines'" : "{type:'scatter', mode:'lines'");
            sb.Append($", line:{{color:'{color}', width:{lw.ToString(Inv)}}}");
            sb.Append($", x:[{CsvNaN(xLines)}]");
            sb.Append($", y:[{CsvNaN(yLines)}]");
            if (is3D) sb.Append($", z:[{CsvNaN(zLines)}]");
            sb.Append(", showlegend:false, hoverinfo:'skip'}");
            AddTrace(sb.ToString());
        }
        private static void AddEdge(System.Collections.Generic.HashSet<(int, int)> edges, int u, int v)
        {
            if (u > v) (u, v) = (v, u);
            edges.Add((u, v));
        }
        private static string CsvNaN(System.Collections.Generic.List<double> arr)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < arr.Count; i++)
            {
                if (i > 0) sb.Append(",");
                if (double.IsNaN(arr[i])) sb.Append("null");
                else sb.Append(arr[i].ToString("G", Inv));
            }
            return sb.ToString();
        }

        /// <summary>Patch 2D simple — polígono cerrado con relleno.</summary>
        public static void Patch2D(double[] xs, double[] ys, string faceColor, string edgeColor,
                                    double faceAlpha, double lineWidth, double val = double.NaN)
        {
            var sb = new StringBuilder();
            sb.Append("{type:'scatter', mode:'lines', fill:'toself'");
            sb.Append($", fillcolor:'{faceColor}'");
            sb.Append($", opacity:{faceAlpha.ToString(Inv)}");
            sb.Append($", line:{{color:'{edgeColor}', width:{lineWidth.ToString(Inv)}}}");
            sb.Append($", x:[{Csv(xs)},{xs[0].ToString("G",Inv)}]");
            sb.Append($", y:[{Csv(ys)},{ys[0].ToString("G",Inv)}]");
            sb.Append(", showlegend:false, hoverinfo:'skip'}");
            AddTrace(sb.ToString());
            if (_figPrims != null) _figPrims.Add(new FigPrim{
                Kind="patch2d", Xs=(double[])xs.Clone(), Ys=(double[])ys.Clone(),
                FaceColor=faceColor, EdgeColor=edgeColor, FaceAlpha=faceAlpha, LineWidth=lineWidth, Val=val
            });
        }
        /// <summary>Niveles de contorno "redondos" estilo MATLAB: paso ∈ {1,2,2.5,5}×10^k
        /// tal que caben ~n bandas en [zmin,zmax], alineados a múltiplos del paso.</summary>
        private static void NiceContourLevels(double zmin, double zmax, int n, out double lo, out double hi, out double step)
        {
            double range = zmax - zmin;
            double raw = range / System.Math.Max(1, n);
            double mag = System.Math.Pow(10, System.Math.Floor(System.Math.Log10(raw)));
            double norm = raw / mag;
            double nice = norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10;
            step = nice * mag;
            lo = System.Math.Floor(zmin / step) * step;
            hi = System.Math.Ceiling(zmax / step) * step;
            if (hi <= lo) hi = lo + step;
        }
        /// <summary>contourf / contour como PRIMITIVAS de canvas (PNG sin navegador).
        /// filled = bandas rellenas (patch2d cuantizado por nivel, coloreado por colormap);
        /// lines  = isolíneas negras (marching squares). Se agregan directo a _figPrims
        /// (sin AddTrace) para no generar miles de trazas Plotly en mallas finas.</summary>
        public static void Contourf2D(MValue X, MValue Y, MValue Z, int nLevels, string colormap, bool filled, bool lines, double[] levels = null, string lineColor = null)
        {
            if (_figTraces == null) BeginFigure();
            if (_figPrims == null) _figPrims = new System.Collections.Generic.List<FigPrim>();
            SetCmapName(colormap);
            int nx = Z.Cols, ny = Z.Rows;
            if (nx < 2 || ny < 2) return;
            var xv = new double[nx];
            var yv = new double[ny];
            // X: matriz meshgrid (primera fila) o vector; Y: primera columna o vector.
            for (int j = 0; j < nx; j++) xv[j] = (j < X.Data.Length) ? X.Data[j] : j;
            bool yIsVec = (Y.Rows == 1 || Y.Cols == 1);
            for (int i = 0; i < ny; i++) yv[i] = yIsVec ? (i < Y.Data.Length ? Y.Data[i] : i) : Y.Data[i * Y.Cols];
            // Coords de nodo COMPLETAS + deteccion de malla curvilinea (deformada):
            // si X,Y son matrices ny×nx y X varia por fila (o Y por columna) → curvilinea.
            bool xFull = X.Rows == ny && X.Cols == nx && X.Data.Length >= nx * ny;
            bool yFull = Y.Rows == ny && Y.Cols == nx && Y.Data.Length >= nx * ny;
            var GX = new double[nx * ny];
            var GY = new double[nx * ny];
            bool curvi = false;
            for (int i = 0; i < ny; i++)
                for (int j = 0; j < nx; j++)
                {
                    GX[i * nx + j] = xFull ? X.Data[i * nx + j] : xv[j];
                    GY[i * nx + j] = yFull ? Y.Data[i * nx + j] : yv[i];
                    if (xFull && System.Math.Abs(X.Data[i * nx + j] - X.Data[j]) > 1e-12) curvi = true;
                    if (yFull && System.Math.Abs(Y.Data[i * nx + j] - Y.Data[i * nx]) > 1e-12) curvi = true;
                }
            double NX_(int i, int j) => GX[i * nx + j];
            double NY_(int i, int j) => GY[i * nx + j];
            if (nLevels < 1) nLevels = 10;
            double zmin = double.MaxValue, zmax = double.MinValue;
            foreach (var v in Z.Data) { if (double.IsNaN(v)) continue; if (v < zmin) zmin = v; if (v > zmax) zmax = v; }
            if (zmax <= zmin) zmax = zmin + 1;
            // Niveles "redondos" como MATLAB (paso 1/2/2.5/5 × 10^k); el COLOR se
            // normaliza al rango de datos [zmin,zmax] (caxis por defecto de MATLAB).
            double lo, hi, levStep;
            if (levels != null && levels.Length >= 2)
            {
                // MATLAB: contourf(X,Y,Z,v) con v VECTOR = niveles EXPLICITOS. Se toman como
                // equiespaciados entre v(1) y v(end) (un vector no uniforme se aproxima con su
                // primer/ultimo nivel y su numero de bandas) y el color se normaliza a ese rango,
                // que es lo que hace `caxis([v(1) v(end)])` en el .m del talud.
                lo = levels[0]; hi = levels[levels.Length - 1];
                if (hi <= lo) hi = lo + 1;
                levStep = (hi - lo) / (levels.Length - 1);
                zmin = lo; zmax = hi;
            }
            else NiceContourLevels(zmin, zmax, nLevels, out lo, out hi, out levStep);
            int nBands = (int)System.Math.Round((hi - lo) / levStep);
            double Zv(int i, int j) => Z.Data[i * nx + j];

            if (filled)
            {
                // UNA primitiva con toda la malla; el rasterizador rellena POR PÍXEL
                // (muestreo bilineal + cuantización a banda) → liso como imagesc de MATLAB,
                // sin costuras de antialiasing entre celditas.
                _figPrims.Add(new FigPrim {
                    Kind = "fieldfill",
                    Xs = curvi ? (double[])GX.Clone() : (double[])xv.Clone(),
                    Ys = curvi ? (double[])GY.Clone() : (double[])yv.Clone(),
                    Zs = (double[])Z.Data.Clone(),
                    GridNx = nx, GridNy = ny, NLevels = nBands, Vmin = zmin, Vmax = zmax,
                    LevLo = lo, LevStep = levStep,
                    Curvi = curvi, GX = curvi ? GX : null, GY = curvi ? GY : null
                });
            }
            if (lines)
            {
                bool cmapRev = colormap != null && (colormap.EndsWith("_r") || colormap.Contains("reverse"));
                for (int L = 1; L < nBands; L++)
                {
                    double c = lo + L * levStep;
                    if (c <= zmin || c >= zmax) continue;
                    double tL = (c - zmin) / (zmax - zmin);
                    if (cmapRev) tL = 1 - tL;
                    string lineCol = lineColor ?? JetCss(tL);   // isolínea del color del colormap (nivel), o 'LineColor' fijo (MATLAB)
                    for (int i = 0; i < ny - 1; i++)
                        for (int j = 0; j < nx - 1; j++)
                        {
                            double vA = Zv(i, j), vB = Zv(i, j + 1), vC = Zv(i + 1, j + 1), vD = Zv(i + 1, j);
                            if (double.IsNaN(vA) || double.IsNaN(vB) || double.IsNaN(vC) || double.IsNaN(vD)) continue;
                            double ax = NX_(i, j),     ay = NY_(i, j);
                            double bx = NX_(i, j + 1), by = NY_(i, j + 1);
                            double cx = NX_(i + 1, j + 1), cy = NY_(i + 1, j + 1);
                            double dx = NX_(i + 1, j), dy = NY_(i + 1, j);
                            var px = new System.Collections.Generic.List<double>(4);
                            var py = new System.Collections.Generic.List<double>(4);
                            void Cross(double p1x, double p1y, double v1, double p2x, double p2y, double v2)
                            {
                                if ((v1 - c) * (v2 - c) < 0)
                                {
                                    double tt = (c - v1) / (v2 - v1);
                                    px.Add(p1x + tt * (p2x - p1x)); py.Add(p1y + tt * (p2y - p1y));
                                }
                            }
                            Cross(ax, ay, vA, bx, by, vB);
                            Cross(bx, by, vB, cx, cy, vC);
                            Cross(cx, cy, vC, dx, dy, vD);
                            Cross(dx, dy, vD, ax, ay, vA);
                            if (px.Count == 2)
                                _figPrims.Add(new FigPrim { Kind = "line2d", Xs = new[] { px[0], px[1] }, Ys = new[] { py[0], py[1] }, Color = lineCol, LineWidth = 0.7 });
                            else if (px.Count == 4)
                            {
                                _figPrims.Add(new FigPrim { Kind = "line2d", Xs = new[] { px[0], px[1] }, Ys = new[] { py[0], py[1] }, Color = lineCol, LineWidth = 0.7 });
                                _figPrims.Add(new FigPrim { Kind = "line2d", Xs = new[] { px[2], px[3] }, Ys = new[] { py[2], py[3] }, Color = lineCol, LineWidth = 0.7 });
                            }
                        }
                }
            }
            SetColorRange(zmin, zmax);
        }
        public static void Line2D(double[] xs, double[] ys, string color, double lineWidth, string name = null, string dash = "solid")
        {
            var sb = new StringBuilder();
            sb.Append("{type:'scatter', mode:'lines'");
            string dashJs = (dash != null && dash != "solid") ? $", dash:'{dash}'" : "";
            sb.Append($", line:{{color:'{color}', width:{lineWidth.ToString(Inv)}{dashJs}}}");
            sb.Append($", x:[{Csv(xs)}], y:[{Csv(ys)}]");
            if (!string.IsNullOrEmpty(name))
                sb.Append($", name:'{EscapeJs(name)}', showlegend:true, hoverinfo:'skip'}}");
            else
                sb.Append(", showlegend:false, hoverinfo:'skip'}");
            AddTrace(sb.ToString());
            if (_figPrims != null) _figPrims.Add(new FigPrim{
                Kind="line2d", Xs=(double[])xs.Clone(), Ys=(double[])ys.Clone(),
                Color=color, LineWidth=lineWidth, Name=name, Dash=dash
            });
        }
        /// <summary>quiver COMPONIENDO en la figura actual (2D): cada flecha = tallo + 2 líneas de
        /// punta, agregadas como Line2D → compone con patch/plot/text (respeta hold). Antes quiver
        /// emitía una figura Plotly standalone → cada flecha salía en su propio gráfico.</summary>
        public static void QuiverAdd(double[] xs, double[] ys, double[] us, double[] vs,
                                     double scale, string color, double lw, double headFrac)
        {
            if (_figTraces == null) BeginFigure();
            int n = System.Math.Min(System.Math.Min(xs.Length, ys.Length), System.Math.Min(us.Length, vs.Length));
            double ca = System.Math.Cos(0.42), sa = System.Math.Sin(0.42);   // ~24° para la punta
            for (int i = 0; i < n; i++)
            {
                double x0 = xs[i], y0 = ys[i], dx = us[i] * scale, dy = vs[i] * scale;
                double x1 = x0 + dx, y1 = y0 + dy;
                Line2D(new[] { x0, x1 }, new[] { y0, y1 }, color, lw);        // tallo
                double len = System.Math.Sqrt(dx * dx + dy * dy);
                if (len < 1e-12) continue;
                double ux = dx / len, uy = dy / len, hl = headFrac * len;
                double h1x = x1 - hl * (ux * ca - uy * sa), h1y = y1 - hl * (ux * sa + uy * ca);
                double h2x = x1 - hl * (ux * ca + uy * sa), h2y = y1 - hl * (uy * ca - ux * sa);
                Line2D(new[] { x1, h1x }, new[] { y1, h1y }, color, lw);      // punta 1
                Line2D(new[] { x1, h2x }, new[] { y1, h2y }, color, lw);      // punta 2
            }
        }
        /// <summary>triplot/trimesh 2D — dibuja las ARISTAS de la triangulación (tri Mx3, índices
        /// 1-based) como líneas, cada arista separada por NaN. Se acumula en la figura (compone
        /// con plot/patch). Compatible MATLAB triplot(TRI,x,y).</summary>
        public static void TriPlot2D(MValue tri, double[] x, double[] y, string color, double lw)
        {
            var edges = new System.Collections.Generic.HashSet<(int, int)>();
            for (int k = 0; k < tri.Rows; k++)
            {
                int a = (int)System.Math.Round(tri.At(k, 0)) - 1;
                int b = (int)System.Math.Round(tri.At(k, 1)) - 1;
                int c = (int)System.Math.Round(tri.At(k, 2)) - 1;
                AddEdge(edges, a, b); AddEdge(edges, b, c); AddEdge(edges, c, a);
            }
            var xL = new System.Collections.Generic.List<double>();
            var yL = new System.Collections.Generic.List<double>();
            foreach (var (u, v) in edges)
            {
                xL.Add(x[u]); xL.Add(x[v]); xL.Add(double.NaN);
                yL.Add(y[u]); yL.Add(y[v]); yL.Add(double.NaN);
            }
            // ruta Plotly: una sola traza con aristas separadas por NaN (eficiente)
            var sb = new StringBuilder();
            sb.Append("{type:'scatter', mode:'lines'");
            sb.Append($", line:{{color:'{color}', width:{lw.ToString(Inv)}}}");
            sb.Append($", x:[{CsvNaN(xL)}], y:[{CsvNaN(yL)}]");
            sb.Append(", showlegend:false, hoverinfo:'skip'}");
            AddTrace(sb.ToString());
            // ruta CANVAS/SVG (cuando la figura tiene markers/patch): el renderer de line2d hace
            // UNA polyline sin cortes por NaN → hay que dar UN FigPrim por arista (2 puntos c/u).
            if (_figPrims != null)
                foreach (var (u, v) in edges)
                    _figPrims.Add(new FigPrim {
                        Kind = "line2d", Xs = new[] { x[u], x[v] }, Ys = new[] { y[u], y[v] },
                        Color = color, LineWidth = lw, Dash = "solid"
                    });
        }
        /// <summary>trimesh 3D — MALLA DE ALAMBRE: aristas coloreadas por z, SIN relleno de caras
        /// (a diferencia de trisurf, que rellena). Igual que MATLAB trimesh(TRI,x,y,z). Emite la
        /// escena canvas/WebGL (CvLine con gradiente por z) y la traza Plotly (scatter3d lines
        /// con line.color por z + colorscale).</summary>
        public static void TriMesh3D(MValue faces, double[] x, double[] y, double[] z, string colormap)
        {
            if (_figTraces == null) BeginFigure();
            SetAxisEqual(false);   // superficie: caja estirada por eje (como MATLAB), NO proporción real
            SetCmapName(colormap);
            int nF = faces.Rows;
            double zmin = double.MaxValue, zmax = double.MinValue;
            for (int i = 0; i < z.Length; i++) { if (z[i] < zmin) zmin = z[i]; if (z[i] > zmax) zmax = z[i]; }
            double rng = (zmax - zmin) > 1e-12 ? zmax - zmin : 1;
            var edges = new System.Collections.Generic.HashSet<(int, int)>();
            for (int f = 0; f < nF; f++)
            {
                int a = (int)faces.At(f, 0) - 1, b = (int)faces.At(f, 1) - 1, c = (int)faces.At(f, 2) - 1;
                AddEdge(edges, a, b); AddEdge(edges, b, c); AddEdge(edges, c, a);
            }
            // CANVAS: cada arista con gradiente de color (dos medios-segmentos por los extremos)
            foreach (var (u, v) in edges)
            {
                float[] cu = CmapF(colormap, (z[u] - zmin) / rng), cv = CmapF(colormap, (z[v] - zmin) / rng);
                double mx = (x[u] + x[v]) / 2, my = (y[u] + y[v]) / 2, mz = (z[u] + z[v]) / 2;
                CvLine(x[u], y[u], z[u], mx, my, mz, cu);
                CvLine(mx, my, mz, x[v], y[v], z[v], cv);
            }
            // PLOTLY: scatter3d de aristas, color por z (line.color array + colorscale), cortes por NaN
            var xs = new System.Collections.Generic.List<double>();
            var ys = new System.Collections.Generic.List<double>();
            var zs = new System.Collections.Generic.List<double>();
            var cs = new System.Collections.Generic.List<double>();
            foreach (var (u, v) in edges)
            {
                xs.Add(x[u]); xs.Add(x[v]); xs.Add(double.NaN);
                ys.Add(y[u]); ys.Add(y[v]); ys.Add(double.NaN);
                zs.Add(z[u]); zs.Add(z[v]); zs.Add(double.NaN);
                cs.Add(z[u]); cs.Add(z[v]); cs.Add(double.NaN);
            }
            var sb = new StringBuilder();
            sb.Append("{type:'scatter3d',mode:'lines'");
            sb.Append($", x:[{CsvNaN(xs)}], y:[{CsvNaN(ys)}], z:[{CsvNaN(zs)}]");
            sb.Append($", line:{{width:2.5, color:[{CsvNaN(cs)}], colorscale:{ColorscaleJs(colormap)}, reversescale:{(ColormapReversed(colormap) ? "true" : "false")}}}");
            sb.Append(", showlegend:false, hoverinfo:'skip'}");
            AddTrace(sb.ToString());
            _figIs3D = true;
        }
        /// <summary>Markers 2D — puntos (scatter mode:markers) que se ACUMULAN en la figura.
        /// Permite que plot(x,y,'o') componga con patch/line/text en los mismos ejes.</summary>
        public static void Markers2D(double[] xs, double[] ys, string fillColor, string edgeColor,
                                      string symbol, double size, string name = null)
        {
            var sb = new StringBuilder();
            sb.Append("{type:'scatter', mode:'markers'");
            // MATLAB dibuja el marcador HUECO salvo que se pida MarkerFaceColor:
            // sin relleno explicito va transparente y solo se ve el borde.
            string mfP = string.IsNullOrEmpty(fillColor) || fillColor == "none"
                         ? "rgba(0,0,0,0)" : fillColor;
            string meP = string.IsNullOrEmpty(edgeColor) ? "#1f77b4" : edgeColor;
            sb.Append($", marker:{{symbol:'{symbol}', size:{size.ToString(Inv)}, color:'{mfP}'");
            sb.Append($", line:{{color:'{meP}', width:1.2}}}}");
            sb.Append($", x:[{Csv(xs)}], y:[{Csv(ys)}]");
            if (!string.IsNullOrEmpty(name))
                sb.Append($", name:'{EscapeJs(name)}', showlegend:true, hoverinfo:'skip'}}");
            else
                sb.Append(", showlegend:false, hoverinfo:'skip'}");
            AddTrace(sb.ToString());
            if (_figPrims != null) _figPrims.Add(new FigPrim{
                Kind="markers2d", Xs=(double[])xs.Clone(), Ys=(double[])ys.Clone(),
                FaceColor=fillColor, EdgeColor=edgeColor, FontSize=size, Text=symbol, Name=name
            });
        }
        public static void Text2D(double x, double y, string text, string color, double fontSize, string align = "left",
                                  string bgColor = null, string edgeColor = null, string valign = "middle")
        {
            string xanchor = align == "center" ? "center" : (align == "right" ? "right" : "left");
            var sb = new StringBuilder();
            sb.Append("{");
            sb.Append($"x:{x.ToString(Inv)}, y:{y.ToString(Inv)}, ");
            sb.Append("xref:'x', yref:'y', ");   // posicionar en coords de DATOS, no de papel
            sb.Append($"text:'{EscapeJs(text)}', ");
            sb.Append($"font:{{color:'{color}', size:{fontSize.ToString(Inv)}}}, ");
            sb.Append($"xanchor:'{xanchor}', ");  // honra HorizontalAlignment de MATLAB
            sb.Append("showarrow:false");
            sb.Append("}");
            AddAnnotation(sb.ToString());
            if (_figPrims != null) _figPrims.Add(new FigPrim{
                Kind="text2d", Xs=new[]{x}, Ys=new[]{y}, Text=text, Color=color, FontSize=fontSize, Align=align,
                FaceColor=bgColor, EdgeColor=edgeColor, VAlign=valign
            });
        }

        /// <summary>Exporta la figura actual como SVG standalone (sólo primitives 2D).</summary>
        /// <summary>Marcas de eje "bonitas": múltiplos redondos de 1/2/5·10^k dentro de [lo,hi].</summary>
        private static System.Collections.Generic.List<double> NiceTicks(double lo, double hi, int target = 8)
        {
            var outp = new System.Collections.Generic.List<double>();
            double range = hi - lo;
            if (range <= 1e-12) { outp.Add(lo); return outp; }
            double raw = range / target;
            double mag = System.Math.Pow(10, System.Math.Floor(System.Math.Log10(raw)));
            double norm = raw / mag;
            double step = (norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10) * mag;
            double first = System.Math.Ceiling(lo / step - 1e-9) * step;
            for (double v = first; v <= hi + 1e-9; v += step)
                outp.Add(System.Math.Abs(v) < step * 1e-6 ? 0.0 : v);
            return outp;
        }
        private static string FmtTick(double v)
        {
            if (System.Math.Abs(v) < 1e-9) return "0";
            double a = System.Math.Abs(v);
            if (a >= 1e5 || a < 1e-3) return v.ToString("G3", Inv);
            string s = v.ToString(a >= 100 ? "0.#" : "0.##", Inv);
            return s;
        }
        /// <summary>Exponente en superíndice unicode (p.ej. -4 → "⁻⁴") para el multiplicador ×10ⁿ.</summary>
        private static string SupExp(int e)
        {
            const string sup = "⁰¹²³⁴⁵⁶⁷⁸⁹";
            var sb = new StringBuilder();
            if (e < 0) sb.Append('⁻');
            foreach (char c in System.Math.Abs(e).ToString()) sb.Append(sup[c - '0']);
            return sb.ToString();
        }
        public static string ExportSvg(int width = 800, int height = 800)
        {
            if (_figPrims == null || _figPrims.Count == 0) return null;
            // Calcular bounding box
            double xmin = double.MaxValue, xmax = double.MinValue;
            double ymin = double.MaxValue, ymax = double.MinValue;
            foreach (var p in _figPrims)
            {
                if (p.Xs == null) continue;
                foreach (var x in p.Xs) { if (x < xmin) xmin = x; if (x > xmax) xmax = x; }
                foreach (var y in p.Ys) { if (y < ymin) ymin = y; if (y > ymax) ymax = y; }
            }
            if (xmax - xmin < 1e-9) { xmax = xmin + 1; }
            if (ymax - ymin < 1e-9) { ymax = ymin + 1; }
            double pad = 0.05;
            double dx = xmax - xmin, dy = ymax - ymin;
            xmin -= dx * pad; xmax += dx * pad;
            ymin -= dy * pad; ymax += dy * pad;
            // xlim/ylim del script MANDAN sobre el bounding box calculado (como MATLAB).
            // Sin esto una figura con zoom salia igual que la vista completa, porque el
            // encuadre se sacaba siempre de TODAS las primitivas dibujadas.
            // Se aplican SIN padding: el rango pedido es el rango, tal cual.
            if (_figXMin.HasValue) { xmin = _figXMin.Value; xmax = _figXMax.Value; }
            if (_figYMin.HasValue) { ymin = _figYMin.Value; ymax = _figYMax.Value; }
            dx = xmax - xmin; dy = ymax - ymin;
            // Margenes para ejes/labels (mas a la derecha si hay colorbar)
            int marginL = 60, marginR = _figColorbar ? 96 : 30, marginT = 50, marginB = 60;
            // axis off (esquema tipo Hekatan LISP): sin ticks ni title arriba → margen superior
            // chico y aire abajo para el caption. NO achico L/R (mantener el ancho del SVG evita
            // que la captura del WebView tesele el dibujo).
            if (_figAxisOff)
            {
                marginT = 30;
                marginB = string.IsNullOrEmpty(_figTitle) ? 30 : 72;
            }
            int plotW = width - marginL - marginR;
            int plotH = height - marginT - marginB;
            // axis equal: MATLAB usa la MISMA escala en los dos ejes (un metro en X mide
            // igual que un metro en Y). Se expande el rango del eje que sobra, centrado,
            // para no recortar nada de lo dibujado.
            if (_figAxisEqual && dx > 0 && dy > 0)
            {
                // Se achica la CAJA, no el rango: MATLAB respeta los xlim/ylim pedidos y
                // deja el recuadro con la proporcion real. Expandir el rango en su lugar
                // pisaria el ylim del script.
                double s = System.Math.Min(plotW / dx, plotH / dy);
                plotW = (int)System.Math.Round(dx * s);
                plotH = (int)System.Math.Round(dy * s);
                height = marginT + plotH + marginB;    // el SVG se ajusta a la caja
                width  = marginL + plotW + marginR;
            }
            double sx = plotW / dx, sy = plotH / dy;
            double TX(double x) => marginL + (x - xmin) * sx;
            double TY(double y) => height - marginB - (y - ymin) * sy;   // Y invertida (SVG top-left)

            var svg = new StringBuilder();
            svg.AppendLine($"<svg xmlns='http://www.w3.org/2000/svg' width='{width}' height='{height}' viewBox='0 0 {width} {height}'>");
            // Tema: en dark el fondo del SVG va oscuro y los textos claros (no más blanco).
            svg.AppendLine($"  <style>text{{fill:{PlotFg};}}</style>");
            svg.AppendLine($"  <rect x='0' y='0' width='{width}' height='{height}' fill='{PlotBg}'/>");
            // Plot area (marco). axis off: figura LIMPIA (esquema tipo Hekatan LISP) → sin marco.
            if (!_figAxisOff)
                svg.AppendLine($"  <rect x='{marginL}' y='{marginT}' width='{plotW}' height='{plotH}' fill='none' stroke='#ccc'/>");
            // Title. En subplot el SVG (760px) se escala mucho al meterlo en la celda del grid → el
            // título de 14px queda diminuto; se agranda para igualar el de los paneles 3D (~430px).
            // axis off: el título va ABAJO como CAPTION (muted, sin negrita), igual que los esquemas
            // de Hekatan LISP (SkiaSharp) — no arriba como el title() normal de MATLAB.
            if (!string.IsNullOrEmpty(_figTitle))
            {
                if (_figAxisOff)
                    svg.AppendLine($"  <text x='{width/2}' y='{height-18}' text-anchor='middle' font-family='sans-serif' font-size='14' fill='{PlotFg}' opacity='0.6'>{TexToSvg(_figTitle)}</text>");
                else
                {
                    int titleFont = _subplotActive ? 26 : 14;
                    svg.AppendLine($"  <text x='{width/2}' y='{(_subplotActive ? 30 : 25)}' text-anchor='middle' font-family='sans-serif' font-size='{titleFont}' font-weight='bold'>{TexToSvg(_figTitle)}</text>");
                }
            }
            // X label
            if (!string.IsNullOrEmpty(_figXLabel))
                svg.AppendLine($"  <text x='{marginL + plotW/2}' y='{height-15}' text-anchor='middle' font-family='sans-serif' font-size='12'>{EscapeXml(_figXLabel)}</text>");
            // Y label
            if (!string.IsNullOrEmpty(_figYLabel))
                svg.AppendLine($"  <text x='15' y='{marginT + plotH/2}' text-anchor='middle' font-family='sans-serif' font-size='12' transform='rotate(-90 15 {marginT + plotH/2})'>{EscapeXml(_figYLabel)}</text>");
            // Marcas de eje en números REDONDOS (nice ticks), como MATLAB — antes eran
            // xmin+dx*t/5 → valores feos (6.94, 0.978…).
            // axis off: sin ticks, etiquetas ni grilla (figura limpia, esquema tipo Hekatan LISP).
            if (!_figAxisOff)
            {
            foreach (var xv in NiceTicks(xmin, xmax))
            {
                double tx = TX(xv);
                if (tx < marginL - 1 || tx > width - marginR + 1) continue;
                svg.AppendLine($"  <text x='{tx.ToString(Inv)}' y='{height-marginB+15}' text-anchor='middle' font-family='sans-serif' font-size='10'>{FmtTick(xv)}</text>");
                svg.AppendLine($"  <line x1='{tx.ToString(Inv)}' y1='{height-marginB}' x2='{tx.ToString(Inv)}' y2='{height-marginB+4}' stroke='#333' stroke-width='0.8'/>");
                if (_figGrid)
                    svg.AppendLine($"  <line x1='{tx.ToString(Inv)}' y1='{marginT}' x2='{tx.ToString(Inv)}' y2='{height-marginB}' stroke='#d0d0d0' stroke-width='0.7'/>");
            }
            foreach (var yv in NiceTicks(ymin, ymax))
            {
                double ty = TY(yv);
                if (ty < marginT - 1 || ty > height - marginB + 1) continue;
                svg.AppendLine($"  <text x='{marginL-7}' y='{(ty+4).ToString(Inv)}' text-anchor='end' font-family='sans-serif' font-size='10'>{FmtTick(yv)}</text>");
                svg.AppendLine($"  <line x1='{marginL-4}' y1='{ty.ToString(Inv)}' x2='{marginL}' y2='{ty.ToString(Inv)}' stroke='#333' stroke-width='0.8'/>");
                if (_figGrid)
                    svg.AppendLine($"  <line x1='{marginL}' y1='{ty.ToString(Inv)}' x2='{width-marginR}' y2='{ty.ToString(Inv)}' stroke='#d0d0d0' stroke-width='0.7'/>");
            }
            }
            // Clip path para plot area
            svg.AppendLine($"  <defs><clipPath id='plot'><rect x='{marginL}' y='{marginT}' width='{plotW}' height='{plotH}'/></clipPath></defs>");
            svg.AppendLine($"  <g clip-path='url(#plot)'>");
            // Render primitives
            foreach (var p in _figPrims)
            {
                if (p.Kind == "patch2d" && p.Xs.Length > 0)
                {
                    var pts = new StringBuilder();
                    for (int i = 0; i < p.Xs.Length; i++)
                    {
                        if (i > 0) pts.Append(" ");
                        pts.Append(TX(p.Xs[i]).ToString("F2", Inv));
                        pts.Append(",");
                        pts.Append(TY(p.Ys[i]).ToString("F2", Inv));
                    }
                    // EdgeColor='none' en un campo de patches deja COSTURAS claras de
                    // anti-aliasing entre triangulos vecinos; se pinta el borde del MISMO
                    // color del relleno (fino) para que la superficie quede continua como MATLAB.
                    bool noEdge = string.IsNullOrEmpty(p.EdgeColor) || p.EdgeColor == "none";
                    string estroke = noEdge ? p.FaceColor : p.EdgeColor;
                    string ewid = noEdge ? "0.7" : p.LineWidth.ToString(Inv);
                    svg.AppendLine($"    <polygon points='{pts}' fill='{p.FaceColor}' fill-opacity='{p.FaceAlpha.ToString(Inv)}' stroke='{estroke}' stroke-width='{ewid}'/>");
                }
                else if (p.Kind == "line2d" && p.Xs.Length >= 2)
                {
                    var pts = new StringBuilder();
                    for (int i = 0; i < p.Xs.Length; i++)
                    {
                        if (i > 0) pts.Append(" ");
                        pts.Append(TX(p.Xs[i]).ToString("F2", Inv));
                        pts.Append(",");
                        pts.Append(TY(p.Ys[i]).ToString("F2", Inv));
                    }
                    string da = p.Dash switch {
                        "dash" => " stroke-dasharray='8,4'",
                        "dot" => " stroke-dasharray='2,3'",
                        "dashdot" => " stroke-dasharray='8,3,2,3'",
                        _ => "" };
                    svg.AppendLine($"    <polyline points='{pts}' fill='none' stroke='{p.Color}' stroke-width='{p.LineWidth.ToString(Inv)}'{da}/>");
                }
                else if (p.Kind == "markers2d" && p.Xs.Length > 0)
                {
                    double r = Math.Max(2.0, p.FontSize / 2.0);
                    string sym = p.Text ?? "circle";
                    // MATLAB dibuja los marcadores HUECOS salvo que se pida
                    // MarkerFaceColor. Sin esto salian macizos.
                    string mf = string.IsNullOrEmpty(p.FaceColor) ? "none" : p.FaceColor;
                    string me = string.IsNullOrEmpty(p.EdgeColor) ? "#1f77b4" : p.EdgeColor;
                    for (int i = 0; i < p.Xs.Length; i++)
                    {
                        double cx = TX(p.Xs[i]); double cy = TY(p.Ys[i]);
                        // MATLAB dibuja el punto '.' MUCHO mas chico que el circulo 'o'
                        // para el mismo MarkerSize; por eso se separa como simbolo propio.
                        if (sym == "point")
                        {
                            double rp = Math.Max(1.5, p.FontSize / 6.0);
                            svg.AppendLine($"    <circle cx='{cx.ToString("F2", Inv)}' cy='{cy.ToString("F2", Inv)}' r='{rp.ToString("F2", Inv)}' fill='{mf}' stroke='none'/>");
                        }
                        else if (sym == "square")
                        {
                            svg.AppendLine($"    <rect x='{(cx-r).ToString("F2",Inv)}' y='{(cy-r).ToString("F2",Inv)}' width='{(2*r).ToString("F2",Inv)}' height='{(2*r).ToString("F2",Inv)}' fill='{mf}' stroke='{me}' stroke-width='1'/>");
                        }
                        else if (sym == "diamond")
                        {
                            string dpts = $"{cx.ToString("F2",Inv)},{(cy-r).ToString("F2",Inv)} " +
                                          $"{(cx+r).ToString("F2",Inv)},{cy.ToString("F2",Inv)} " +
                                          $"{cx.ToString("F2",Inv)},{(cy+r).ToString("F2",Inv)} " +
                                          $"{(cx-r).ToString("F2",Inv)},{cy.ToString("F2",Inv)}";
                            svg.AppendLine($"    <polygon points='{dpts}' fill='{mf}' stroke='{me}' stroke-width='1'/>");
                        }
                        else if (sym == "cross" || sym == "x" || sym == "star")
                        {
                            double d = sym == "cross" ? 0 : r * 0.7071;   // '+' recto, 'x' girado
                            if (sym == "cross")
                            {
                                svg.AppendLine($"    <line x1='{(cx-r).ToString("F2",Inv)}' y1='{cy.ToString("F2",Inv)}' x2='{(cx+r).ToString("F2",Inv)}' y2='{cy.ToString("F2",Inv)}' stroke='{me}' stroke-width='1.2'/>");
                                svg.AppendLine($"    <line x1='{cx.ToString("F2",Inv)}' y1='{(cy-r).ToString("F2",Inv)}' x2='{cx.ToString("F2",Inv)}' y2='{(cy+r).ToString("F2",Inv)}' stroke='{me}' stroke-width='1.2'/>");
                            }
                            else
                            {
                                svg.AppendLine($"    <line x1='{(cx-d).ToString("F2",Inv)}' y1='{(cy-d).ToString("F2",Inv)}' x2='{(cx+d).ToString("F2",Inv)}' y2='{(cy+d).ToString("F2",Inv)}' stroke='{me}' stroke-width='1.2'/>");
                                svg.AppendLine($"    <line x1='{(cx-d).ToString("F2",Inv)}' y1='{(cy+d).ToString("F2",Inv)}' x2='{(cx+d).ToString("F2",Inv)}' y2='{(cy-d).ToString("F2",Inv)}' stroke='{me}' stroke-width='1.2'/>");
                            }
                        }
                        else if (sym.StartsWith("triangle"))
                        {
                            // triángulo equilátero (apuntando arriba)
                            double h = r * 1.3;
                            string pts = $"{cx.ToString("F2",Inv)},{(cy-h).ToString("F2",Inv)} " +
                                         $"{(cx-h).ToString("F2",Inv)},{(cy+h*0.7).ToString("F2",Inv)} " +
                                         $"{(cx+h).ToString("F2",Inv)},{(cy+h*0.7).ToString("F2",Inv)}";
                            svg.AppendLine($"    <polygon points='{pts}' fill='{mf}' stroke='{me}' stroke-width='1'/>");
                        }
                        else
                        {
                            svg.AppendLine($"    <circle cx='{cx.ToString("F2", Inv)}' cy='{cy.ToString("F2", Inv)}' r='{r.ToString("F2", Inv)}' fill='{mf}' stroke='{me}' stroke-width='1'/>");
                        }
                    }
                }
                else if (p.Kind == "text2d")
                {
                    double tx = TX(p.Xs[0]); double ty = TY(p.Ys[0]);
                    string anchor = p.Align == "center" ? "middle" : (p.Align == "right" ? "end" : "start");
                    svg.AppendLine($"    <text x='{tx.ToString("F2", Inv)}' y='{ty.ToString("F2", Inv)}' fill='{p.Color}' font-family='sans-serif' font-size='{p.FontSize.ToString(Inv)}' text-anchor='{anchor}' dominant-baseline='central'>{EscapeXml(p.Text)}</text>");
                }
            }
            svg.AppendLine($"  </g>");
            // ---- LEYENDA (si se llamó legend() y hay curvas con DisplayName) ----
            if (_figShowLegend)
            {
                var leg = new System.Collections.Generic.List<FigPrim>();
                foreach (var p in _figPrims)
                    if (!string.IsNullOrEmpty(p.Name) && (p.Kind == "line2d" || p.Kind == "markers2d")) leg.Add(p);
                if (leg.Count > 0)
                {
                    int rowH = 18, padx = 8; int boxW = 40;
                    foreach (var it in leg) boxW = Math.Max(boxW, 44 + (int)(it.Name.Length * 6.6));
                    int boxH = leg.Count * rowH + 8;
                    string loc = (_figLegendLoc ?? "northeast").ToLowerInvariant().Replace("outside", "");
                    int rgt = marginL + plotW - boxW - 8, lft = marginL + 8;
                    int top = marginT + 8, bot = marginT + plotH - boxH - 8;
                    int lx, ly;
                    switch (loc) {
                        case "northwest": lx=lft; ly=top; break;
                        case "southeast": lx=rgt; ly=bot; break;
                        case "southwest": lx=lft; ly=bot; break;
                        case "north": lx=marginL+plotW/2-boxW/2; ly=top; break;
                        case "south": lx=marginL+plotW/2-boxW/2; ly=bot; break;
                        default: lx=rgt; ly=top; break;
                    }
                    svg.AppendLine($"  <rect x='{lx}' y='{ly}' width='{boxW}' height='{boxH}' fill='white' fill-opacity='0.82' stroke='#bbb' stroke-width='1' rx='3'/>");
                    for (int i = 0; i < leg.Count; i++)
                    {
                        var it = leg[i]; int cy = ly + 4 + i*rowH + rowH/2;
                        string col = it.Kind == "line2d" ? (it.Color ?? "#333") : (it.FaceColor ?? "#333");
                        svg.AppendLine($"    <line x1='{lx+padx}' y1='{cy}' x2='{lx+padx+24}' y2='{cy}' stroke='{col}' stroke-width='3'/>");
                        if (it.Kind == "markers2d")
                            svg.AppendLine($"    <circle cx='{lx+padx+12}' cy='{cy}' r='3' fill='{col}' stroke='{col}'/>");
                        svg.AppendLine($"    <text x='{lx+padx+30}' y='{cy+4}' font-family='sans-serif' font-size='11' fill='#222'>{EscapeXml(it.Name)}</text>");
                    }
                }
            }
            // ---- COLORBAR (si se llamó colorbar() y hay caxis definido) ----
            if (_figColorbar && TryGetCAxis(out double cbLo, out double cbHi) && cbHi > cbLo)
            {
                int cbx = width - marginR + 20, cbw = 16, cbtop = marginT, cbh = plotH, NB = 64;
                for (int s = 0; s < NB; s++)
                {
                    var f3 = CmapF(_figCmapName, 1.0 - (double)s / (NB - 1));   // arriba = valor alto
                    int R = (int)(f3[0]*255), G = (int)(f3[1]*255), B = (int)(f3[2]*255);
                    double yy = cbtop + (double)cbh * s / NB, hh = (double)cbh / NB + 1;
                    svg.AppendLine($"  <rect x='{cbx}' y='{yy.ToString("F1", Inv)}' width='{cbw}' height='{hh.ToString("F1", Inv)}' fill='rgb({R},{G},{B})'/>");
                }
                svg.AppendLine($"  <rect x='{cbx}' y='{cbtop}' width='{cbw}' height='{cbh}' fill='none' stroke='#333' stroke-width='0.8'/>");
                var svgTicks = _cbTicks != null && _cbTicks.Length > 0 ? new System.Collections.Generic.List<double>(_cbTicks) : NiceTicks(cbLo, cbHi);
                bool svgLabels = _cbTickLabels != null && _cbTicks != null && _cbTickLabels.Length == _cbTicks.Length;
                for (int ti = 0; ti < svgTicks.Count; ti++)
                {
                    double tv = svgTicks[ti];
                    double fr = (tv - cbLo) / (cbHi - cbLo); if (fr < -1e-6 || fr > 1.0001) continue;
                    double yy = _cbReverse ? cbtop + cbh * fr : cbtop + cbh * (1 - fr);   // Direction reverse: min arriba
                    svg.AppendLine($"  <line x1='{cbx+cbw}' y1='{yy.ToString("F1", Inv)}' x2='{cbx+cbw+3}' y2='{yy.ToString("F1", Inv)}' stroke='#333' stroke-width='0.8'/>");
                    svg.AppendLine($"  <text x='{cbx+cbw+5}' y='{(yy+3).ToString("F1", Inv)}' font-family='sans-serif' font-size='10' fill='#222'>{EscapeXml(svgLabels ? _cbTickLabels[ti] : FmtTick(tv))}</text>");
                }
            }
            svg.AppendLine("</svg>");
            return svg.ToString();
        }

        /// <summary>Rasteriza la figura 2D actual (primitives line/patch/marker/text)
        /// a RGB row-major (byte[h*w*3]) vía SkiaSharp. Para getframe → GIF.</summary>
        /// <summary>Límites del eje extendidos al tick "bonito" que abarca los datos
        /// (look MATLAB: sin(x) en [0,6.28] hace que el eje llegue a 7).</summary>
        private static (double lo, double hi) NiceLimits(double min, double max, int target)
        {
            double range = max - min; if (range < 1e-12) range = Math.Max(1, Math.Abs(max));
            double raw = range / Math.Max(1, target);
            double mag = Math.Pow(10, Math.Floor(Math.Log10(raw)));
            double norm = raw / mag;
            double step = (norm < 1.5 ? 1 : norm < 3 ? 2 : norm < 7 ? 5 : 10) * mag;
            return (Math.Floor(min / step) * step, Math.Ceiling(max / step) * step);
        }

        private static SKBitmap BuildFigureBitmap(int width, int height, int ss = 1)
        {
            if (_figPrims == null || _figPrims.Count == 0) return null;
            double xmin = double.MaxValue, xmax = double.MinValue, ymin = double.MaxValue, ymax = double.MinValue;
            foreach (var p in _figPrims)
            {
                if (p.Xs == null) continue;
                foreach (var x in p.Xs) { if (x < xmin) xmin = x; if (x > xmax) xmax = x; }
                foreach (var y in p.Ys) { if (y < ymin) ymin = y; if (y > ymax) ymax = y; }
            }
            if (xmax - xmin < 1e-9) xmax = xmin + 1;
            if (ymax - ymin < 1e-9) ymax = ymin + 1;
            // Rango de valores por-cara (malla FEM coloreada por colormap).
            double vmin = double.MaxValue, vmax = double.MinValue;
            foreach (var p in _figPrims)
            {
                if (p.Kind == "patch2d" && !double.IsNaN(p.Val)) { if (p.Val < vmin) vmin = p.Val; if (p.Val > vmax) vmax = p.Val; }
                else if (p.Kind == "fieldfill" && !double.IsNaN(p.Vmin)) { if (p.Vmin < vmin) vmin = p.Vmin; if (p.Vmax > vmax) vmax = p.Vmax; }
            }
            bool hasVals = vmax >= vmin;
            // Si el script fijó caxis/clim, el colorbar Y el relleno usan ESE rango (como MATLAB),
            // no el min/max del valor por-cara. Antes: colorbar mostraba el max del promedio por-cara
            // (p.ej. 15) mientras el campo se normalizaba con caxis (p.ej. 18) → escala inconsistente.
            if (hasVals && TryGetCAxis(out double _cax0, out double _cax1) && _cax1 > _cax0) { vmin = _cax0; vmax = _cax1; }
            if (hasVals && vmax - vmin < 1e-12) vmax = vmin + 1;
            bool cmapRev = _figCmapName != null && (_figCmapName.EndsWith("_r") || _figCmapName.Contains("reverse"));
            SKColor ValColor(double v)
            {
                double t = (v - vmin) / (vmax - vmin);
                if (double.IsNaN(t) || double.IsInfinity(t)) t = 0;
                // Usar el colormap ACTIVO (incl. custom de colormap(matriz), p.ej. paleta GEO5)
                // y su reversión "_r" via CmapF -> el relleno coincide con MATLAB, no jet fijo.
                var rgb = CmapF(_figCmapName, Math.Max(0, Math.Min(1, t)));
                return new SKColor((byte)Math.Round(255 * Math.Max(0, Math.Min(1, rgb[0]))),
                                   (byte)Math.Round(255 * Math.Max(0, Math.Min(1, rgb[1]))),
                                   (byte)Math.Round(255 * Math.Max(0, Math.Min(1, rgb[2]))));
            }
            // Limites y ticks "bonitos" (estilo MATLAB: el eje abarca hasta el tick).
            // target de ticks proporcional al tamaño del eje (~1 cada 70px en X, 48px en Y),
            // como MATLAB (más ticks en ejes grandes).
            // target calibrado para que en el tamaño MATLAB por defecto (560×420) den los
            // mismos ticks: X paso 1 (~7-8 ticks), Y paso 0.2 (~11 ticks).
            // El nº de ticks debe ser como MATLAB (figura de referencia ~560×420),
            // INDEPENDIENTE de la resolución de rasterizado (el contourf va a 960×700). Antes
            // escalaba con width/height → demasiados ticks en alta res (x cada 0.5, y cada 0.2).
            int tgtX = Math.Max(4, (int)Math.Round(560.0 * 0.775 / 62.0));   // ~7 (x: 0,1,…,6)
            int tgtY = Math.Max(4, (int)Math.Round(420.0 * 0.815 / 34.0));   // ~10 (y: 0,0.5,…,4)
            // Si el script fijó límites explícitos con axis([xmin xmax ymin ymax]) los usamos
            // EXACTOS (como MATLAB), sin extender con NiceLimits.
            var (axXmin, axXmax) = _figXMin.HasValue ? (_figXMin.Value, _figXMax.Value) : NiceLimits(xmin, xmax, tgtX);
            var (axYmin, axYmax) = _figYMin.HasValue ? (_figYMin.Value, _figYMax.Value) : NiceLimits(ymin, ymax, tgtY);
            var ticksX = NiceTicks(axXmin, axXmax, tgtX);
            var ticksY = NiceTicks(axYmin, axYmax, tgtY);
            double ddx = axXmax - axXmin, ddy = axYmax - axYmin;
            if (ddx < 1e-12) ddx = 1; if (ddy < 1e-12) ddy = 1;
            // Margenes = posicion del axes por defecto de MATLAB [0.13 0.11 0.775 0.815]
            // (para paridad pixel a pixel): left=0.13, bottom=0.11, right=0.095, top=0.075.
            // contourf/fieldfill: MATLAB deja el axes MÁS ANCHO y la colorbar más fina/a la
            // derecha (medido: axes L=0.116, R=0.879) que en las mallas FEM patch (mR=0.205).
            // contourf/fieldfill: en MATLAB, un `axis([...])` DESPUÉS de `axis equal` cancela la
            // igualdad de escala → el axes ESTIRA-RELLENA su rectángulo Position (medido en el PNG
            // de MATLAB: x∈[0.116,0.879], y∈[0.250 arriba, 0.286 abajo]). Fijamos esa caja exacta.
            int mL = (int)Math.Round(0.130 * width);
            int mB = (int)Math.Round(0.110 * height);
            int mR = (int)Math.Round(hasVals ? 0.205 * width : 0.095 * width);
            int mT = (int)Math.Round(0.075 * height);
            double sx = (width - mL - mR) / ddx, sy = (height - mT - mB) / ddy;
            double offX = 0, offY = 0;
            if (_figAxisEqual)   // misma escala en ambos ejes (geometria, mallas) -> centrar
            {
                double s = Math.Min(sx, sy);
                offX = ((width - mL - mR) - s * ddx) / 2;
                offY = ((height - mT - mB) - s * ddy) / 2;
                sx = s; sy = s;
            }
            float TX(double x) => (float)(mL + offX + (x - axXmin) * sx);
            float TY(double y) => (float)(height - mB - offY - (y - axYmin) * sy);
            _pmOX = mL + offX; _pmOY = mB + offY; _pmSX = sx; _pmSY = sy;
            _pmX0 = axXmin; _pmY0 = axYmin; _pmH = height;   // mapeo para el hover interactivo
            float plotL = (float)(mL + offX), plotR = (float)(width - mR - offX);
            float plotT = (float)(mT + offY), plotB = (float)(height - mB - offY);
            // Con axis-equal la caja puede quedar mucho mas angosta/baja que la figura
            // (p.ej. un vastago 1:16) -> el # de ticks debe ir por el ancho de la CAJA, no de
            // la figura, si no se amontonan los numeros (como MATLAB, que pone pocos).
            if (_figAxisEqual)
            {
                int tX2 = Math.Max(2, (int)Math.Round((plotR - plotL) / (62.0 * width / 560.0)));
                int tY2 = Math.Max(2, (int)Math.Round((plotB - plotT) / (34.0 * height / 420.0)));
                ticksX = NiceTicks(axXmin, axXmax, tX2);
                ticksY = NiceTicks(axYmin, axYmax, tY2);
            }

            // Supersampling: rasterizar a ss× y escalar TODO el dibujo (fuentes, líneas, campo
            // Gouraud) por ss -> PNG más NÍTIDO. El layout sigue en coords lógicas (width×height),
            // así que ticks/márgenes/paridad con MATLAB no cambian, solo la densidad de píxeles.
            if (ss < 1) ss = 1;
            var bmp = new SKBitmap(width * ss, height * ss);
            using (var canvas = new SKCanvas(bmp))
            {
                canvas.Clear(SKColors.White);
                if (ss != 1) canvas.Scale(ss, ss);
                using var fill = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill };
                using var stroke = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke };
                // MATLAB usa Helvetica; Arial es el equivalente mas cercano (vs Segoe UI default).
                var tface = SKTypeface.FromFamilyName("Arial") ?? SKTypeface.Default;
                // Fuentes PROPORCIONALES a la altura (paridad MATLAB): en 700px dan ~13/16/18,
                // no 10/11/11 que se veian diminutas frente al plot. Piso para figuras chicas.
                float fTick  = System.Math.Max(10f, height * 0.019f);
                float fLabel = System.Math.Max(11f, height * 0.023f);
                float fTitle = System.Math.Max(12f, height * 0.026f);
                using var font = new SKFont(tface, fTick);
                using var txt = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SKColors.Black };
                var axisCol = new SKColor(0x26, 0x26, 0x26);
                // Textura 1D del colormap ACTIVO (256×1) + shader: para mapear VALOR->COLOR por-píxel
                // en el campo (como matplotlib). DrawVertices interpola la coord de textura (=valor
                // normalizado) y el shader samplea el colormap -> bandas nítidas, sin costuras.
                using var cmapBmp = new SKBitmap(256, 1);
                for (int ci = 0; ci < 256; ci++)
                {
                    var crgb = CmapF(_figCmapName, ci / 255.0);
                    cmapBmp.SetPixel(ci, 0, new SKColor(
                        (byte)Math.Round(255 * Math.Max(0, Math.Min(1, crgb[0]))),
                        (byte)Math.Round(255 * Math.Max(0, Math.Min(1, crgb[1]))),
                        (byte)Math.Round(255 * Math.Max(0, Math.Min(1, crgb[2])))));
                }
                using var cmapShader = SKShader.CreateBitmap(cmapBmp, SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
                using var cmapPaint = new SKPaint { Shader = cmapShader, IsAntialias = true };
                // ── GRID (gris claro punteado, como MATLAB con grid on) ──
                if (_figGrid)
                {
                    using var grid = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke,
                        Color = new SKColor(0x1a, 0x1a, 0x1a, 38), StrokeWidth = 1,
                        PathEffect = SKPathEffect.CreateDash(new float[] { 2, 2 }, 0) };
                    foreach (var t in ticksX) { if (t < axXmin - 1e-9 || t > axXmax + 1e-9) continue; float px = TX(t); canvas.DrawLine(px, plotT, px, plotB, grid); }
                    foreach (var t in ticksY) { if (t < axYmin - 1e-9 || t > axYmax + 1e-9) continue; float py = TY(t); canvas.DrawLine(plotL, py, plotR, py, grid); }
                }
                // ── Ticks + numeros ──
                using var axis = new SKPaint { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = axisCol, StrokeWidth = 1 };
                foreach (var t in ticksX)
                {
                    if (t < axXmin - 1e-9 || t > axXmax + 1e-9) continue; float px = TX(t);
                    canvas.DrawLine(px, plotB, px, plotB - 5, axis);
                    canvas.DrawText(FmtTick(t), px, plotB + fTick + 4, SKTextAlign.Center, font, txt);
                }
                foreach (var t in ticksY)
                {
                    if (t < axYmin - 1e-9 || t > axYmax + 1e-9) continue; float py = TY(t);
                    canvas.DrawLine(plotL, py, plotL + 5, py, axis);
                    canvas.DrawText(FmtTick(t), plotL - 7, py + fTick * 0.36f, SKTextAlign.Right, font, txt);
                }
                // ── BOX (marco) ──
                canvas.DrawRect(plotL, plotT, plotR - plotL, plotB - plotT, axis);
                // ── xlabel / ylabel / titulo ──
                // xlabel/ylabel/titulo se anclan a la CAJA (plotB/plotT), no al borde de la
                // figura -> con axis-equal (caja centrada y encogida) siguen a la caja como MATLAB.
                using var lblFont = new SKFont(tface, fLabel);
                if (!string.IsNullOrEmpty(_figXLabel))
                    DrawTexCentered(canvas, _figXLabel, (plotL + plotR) / 2f, Math.Min(height - 6, plotB + fTick + fLabel + 10), lblFont, txt, tface);
                if (!string.IsNullOrEmpty(_figYLabel))
                {
                    // MATLAB pega el ylabel a los numeros del eje (no al borde de la figura): a la
                    // izquierda del tick mas ancho. Antes iba fijo en x=13 y con axis equal quedaba lejisimos.
                    float maxTickW = 0f;
                    foreach (var t in ticksY) { if (t < axYmin - 1e-9 || t > axYmax + 1e-9) continue; maxTickW = Math.Max(maxTickW, font.MeasureText(FmtTick(t))); }
                    float ylx = Math.Max(fLabel * 0.9f, plotL - 9 - maxTickW - fLabel * 0.5f);
                    canvas.Save(); canvas.RotateDegrees(-90, ylx, (plotT + plotB) / 2f);
                    DrawTexCentered(canvas, _figYLabel, ylx, (plotT + plotB) / 2f, lblFont, txt, tface);
                    canvas.Restore();
                }
                if (!string.IsNullOrEmpty(_figTitle))
                    using (var tfont = new SKFont(tface, fTitle) { Embolden = true })
                        DrawTexCentered(canvas, _figTitle, (plotL + plotR) / 2f, Math.Max(fTitle + 2, plotT - fTitle * 0.5f - 3), tfont, txt, tface);
                // ── Curvas / primitivas: CLIPeadas al area de plot ──
                // Las anotaciones de texto (text2d) NO se clipean (MATLAB las dibuja fuera del box) → diferidas.
                var pendingText = new System.Collections.Generic.List<FigPrim>();
                canvas.Save();
                canvas.ClipRect(new SKRect(plotL, plotT, plotR, plotB));
                foreach (var p in _figPrims)
                {
                    if (p.Kind == "fieldfill" && p.Zs != null && p.Curvi && p.GX != null)
                    {
                        // Malla CURVILINEA (deformada): rasterizacion por TRIANGULOS.
                        // Cada celda -> 2 triangulos; se rellena cada pixel con Z interpolado
                        // (baricentrico) -> banda -> color. Sin costuras, sigue la forma deformada.
                        int gnx = p.GridNx, gny = p.GridNy;
                        double bLo = p.LevLo, stp = p.LevStep;
                        double[] GXa = p.GX, GYa = p.GY, zg = p.Zs;
                        void FillTri(double sx0,double sy0,double z0, double sx1,double sy1,double z1, double sx2,double sy2,double z2)
                        {
                            int minx = Math.Max((int)Math.Floor(plotL), (int)Math.Floor(Math.Min(sx0,Math.Min(sx1,sx2))));
                            int maxx = Math.Min((int)Math.Ceiling(plotR), (int)Math.Ceiling(Math.Max(sx0,Math.Max(sx1,sx2))));
                            int miny = Math.Max((int)Math.Floor(plotT), (int)Math.Floor(Math.Min(sy0,Math.Min(sy1,sy2))));
                            int maxy = Math.Min((int)Math.Ceiling(plotB), (int)Math.Ceiling(Math.Max(sy0,Math.Max(sy1,sy2))));
                            double den = (sy1-sy2)*(sx0-sx2) + (sx2-sx1)*(sy0-sy2);
                            if (Math.Abs(den) < 1e-9) return;
                            for (int py = miny; py <= maxy; py++)
                                for (int px = minx; px <= maxx; px++)
                                {
                                    double fx = px + 0.5, fy = py + 0.5;
                                    double w0 = ((sy1-sy2)*(fx-sx2) + (sx2-sx1)*(fy-sy2)) / den;
                                    double w1 = ((sy2-sy0)*(fx-sx2) + (sx0-sx2)*(fy-sy2)) / den;
                                    double w2 = 1 - w0 - w1;
                                    if (w0 < -1e-6 || w1 < -1e-6 || w2 < -1e-6) continue;
                                    double vv = w0*z0 + w1*z1 + w2*z2;
                                    if (double.IsNaN(vv)) continue;
                                    int band = (int)Math.Floor((vv - bLo) / stp);
                                    bmp.SetPixel(px, py, ValColor(bLo + (band + 0.5) * stp));
                                }
                        }
                        for (int i = 0; i < gny - 1; i++)
                            for (int j = 0; j < gnx - 1; j++)
                            {
                                int a = i*gnx+j, b = i*gnx+j+1, c = (i+1)*gnx+j+1, d = (i+1)*gnx+j;
                                if (double.IsNaN(zg[a])||double.IsNaN(zg[b])||double.IsNaN(zg[c])||double.IsNaN(zg[d])) continue;
                                float Ax=TX(GXa[a]),Ay=TY(GYa[a]), Bx=TX(GXa[b]),By=TY(GYa[b]);
                                float Cx=TX(GXa[c]),Cy=TY(GYa[c]), Dx=TX(GXa[d]),Dy=TY(GYa[d]);
                                FillTri(Ax,Ay,zg[a], Bx,By,zg[b], Cx,Cy,zg[c]);
                                FillTri(Ax,Ay,zg[a], Cx,Cy,zg[c], Dx,Dy,zg[d]);
                            }
                        continue;
                    }
                    if (p.Kind == "fieldfill" && p.Zs != null)
                    {
                        // Relleno POR PÍXEL (contourf liso, sin costuras): muestreo bilineal
                        // del campo + cuantización a banda → color del colormap. Como imagesc.
                        int gnx = p.GridNx, gny = p.GridNy;
                        double zmn = p.Vmin, zmx = p.Vmax;         // caxis (rango de datos) → color
                        double bLo = p.LevLo, stp = p.LevStep;     // rejilla de niveles redondos → banda
                        double[] xg = p.Xs, yg = p.Ys, zg = p.Zs;
                        // Pixeles del BITMAP (width*ss): con supersampling el relleno iba en coords logicas y salia a
                        // mitad de tamano en la esquina (2026-09-05, talud con figure Position a 2x).
                        int x0 = Math.Max(0, (int)Math.Floor(plotL)) * ss, x1 = Math.Min(width * ss - 1, (int)Math.Ceiling(plotR) * ss + ss - 1);
                        int y0 = Math.Max(0, (int)Math.Floor(plotT)) * ss, y1 = Math.Min(height * ss - 1, (int)Math.Ceiling(plotB) * ss + ss - 1);
                        for (int py = y0; py <= y1; py++)
                        {
                            double y = axYmin + (height - mB - offY - ((py + 0.5) / ss)) / sy;
                            if (y < yg[0] || y > yg[gny - 1]) continue;
                            int iy = 0; while (iy < gny - 2 && yg[iy + 1] < y) iy++;
                            double ty = (yg[iy + 1] == yg[iy]) ? 0 : (y - yg[iy]) / (yg[iy + 1] - yg[iy]);
                            for (int px = x0; px <= x1; px++)
                            {
                                double x = axXmin + (((px + 0.5) / ss) - mL - offX) / sx;
                                if (x < xg[0] || x > xg[gnx - 1]) continue;
                                int ix = 0; while (ix < gnx - 2 && xg[ix + 1] < x) ix++;
                                double tx = (xg[ix + 1] == xg[ix]) ? 0 : (x - xg[ix]) / (xg[ix + 1] - xg[ix]);
                                double z00 = zg[iy * gnx + ix], z01 = zg[iy * gnx + ix + 1];
                                double z10 = zg[(iy + 1) * gnx + ix], z11 = zg[(iy + 1) * gnx + ix + 1];
                                // Celda con NaN PARCIAL (1-3 esquinas): interpolar con las esquinas
                                // VÁLIDAS renormalizando pesos (como MATLAB contourf) -> sin "franjas"
                                // blancas en bordes/escalones de la máscara. Solo se salta si las 4 son NaN.
                                double w00 = (1 - tx) * (1 - ty), w01 = tx * (1 - ty), w10 = (1 - tx) * ty, w11 = tx * ty;
                                double vs = 0, ws = 0;
                                if (!double.IsNaN(z00)) { vs += w00 * z00; ws += w00; }
                                if (!double.IsNaN(z01)) { vs += w01 * z01; ws += w01; }
                                if (!double.IsNaN(z10)) { vs += w10 * z10; ws += w10; }
                                if (!double.IsNaN(z11)) { vs += w11 * z11; ws += w11; }
                                if (ws <= 1e-12) continue;                       // 4 esquinas NaN -> hueco real
                                double vv = vs / ws;
                                int band = (int)Math.Floor((vv - bLo) / stp);   // banda en rejilla redonda
                                double vq = bLo + (band + 0.5) * stp;           // valor medio de la banda
                                bmp.SetPixel(px, py, ValColor(vq));             // color por caxis de datos
                            }
                        }
                        continue;
                    }
                    if ((p.Kind == "patch2d" || p.Kind == "line2d") && p.Xs != null && p.Xs.Length >= 2)
                    {
                        var path = new SKPath();
                        // MATLAB: un NaN en x/y CORTA la polilinea (plot([1 2 NaN 3 4],...) = 2 trozos).
                        // Antes el NaN entraba en el SKPath y Skia no dibujaba NADA de esa linea
                        // (la malla/contorno del talud desaparecian encima del contourf).
                        bool pen = false;
                        for (int i = 0; i < p.Xs.Length; i++)
                        {
                            double xx = p.Xs[i], yy = i < p.Ys.Length ? p.Ys[i] : double.NaN;
                            if (double.IsNaN(xx) || double.IsNaN(yy) || double.IsInfinity(xx) || double.IsInfinity(yy)) { pen = false; continue; }
                            if (!pen) { path.MoveTo(TX(xx), TY(yy)); pen = true; }
                            else path.LineTo(TX(xx), TY(yy));
                        }
                        if (p.Kind == "patch2d")
                        {
                            path.Close();
                            bool edgeOn = !string.IsNullOrEmpty(p.EdgeColor) && p.EdgeColor != "none";
                            // Fondo para animacion WebGL: la GPU dibuja el campo, aqui se omite su relleno.
                            if (SkipFieldFill && (p.VertVals != null || p.VertCols != null)) { continue; }
                            if (p.VertVals != null && p.VertVals.Length == 3 && p.Xs.Length >= 3)
                            {
                                // INTERPOLACIÓN POR VALOR (idéntica a MATLAB 'FaceColor','interp' con
                                // CData indexado): MATLAB interpola el VALOR del colormap a través del
                                // triángulo y RECIÉN AHÍ lo mapea a jet -> el degradado recorre TODO el
                                // espectro (rojo→amarillo→verde→cian→azul). Interpolar RGB entre vértices
                                // (rojo+azul=morado) colapsa los tonos medios y encoge la mancha caliente.
                                // Subdividimos el triángulo LxL: en cada sub-vértice interpolamos el VALOR
                                // por baricéntricas y lo mapeamos a jet; el error dentro de cada sub-tri es
                                // despreciable -> value-interp exacto, sin costuras, y usa el mismo
                                // DrawVertices(colors) que sí renderiza (la ruta textura+shader salía en blanco).
                                var P0 = new SKPoint(TX(p.Xs[0]), TY(p.Ys[0]));
                                var P1 = new SKPoint(TX(p.Xs[1]), TY(p.Ys[1]));
                                var P2 = new SKPoint(TX(p.Xs[2]), TY(p.Ys[2]));
                                float v0 = p.VertVals[0], v1 = p.VertVals[1], v2 = p.VertVals[2];
                                const int L = 8;                       // 8x8 = 64 sub-tri por triángulo
                                int rows = L + 1;
                                var gp = new SKPoint[rows * (rows + 1) / 2];
                                var gc = new SKColor[gp.Length];
                                int gi = 0;
                                for (int jj = 0; jj <= L; jj++)
                                    for (int ii = 0; ii <= L - jj; ii++)
                                    {
                                        float a = (L - ii - jj) / (float)L, b = ii / (float)L, c = jj / (float)L;
                                        gp[gi] = new SKPoint(a * P0.X + b * P1.X + c * P2.X, a * P0.Y + b * P1.Y + c * P2.Y);
                                        double t = a * v0 + b * v1 + c * v2;
                                        var cc = CmapF(_figCmapName, Math.Max(0.0, Math.Min(1.0, t)));
                                        gc[gi] = new SKColor(
                                            (byte)Math.Round(255 * Math.Max(0, Math.Min(1, cc[0]))),
                                            (byte)Math.Round(255 * Math.Max(0, Math.Min(1, cc[1]))),
                                            (byte)Math.Round(255 * Math.Max(0, Math.Min(1, cc[2]))));
                                        gi++;
                                    }
                                // índice del sub-vértice (i,j) en el arreglo triangular por filas j
                                int Idx(int i, int j) { int off = 0; for (int r = 0; r < j; r++) off += (L - r + 1); return off + i; }
                                var tri = new System.Collections.Generic.List<SKPoint>(L * L * 3);
                                var tcol = new System.Collections.Generic.List<SKColor>(L * L * 3);
                                for (int jj = 0; jj < L; jj++)
                                    for (int ii = 0; ii < L - jj; ii++)
                                    {
                                        int A = Idx(ii, jj), B = Idx(ii + 1, jj), C = Idx(ii, jj + 1);
                                        tri.Add(gp[A]); tri.Add(gp[B]); tri.Add(gp[C]);
                                        tcol.Add(gc[A]); tcol.Add(gc[B]); tcol.Add(gc[C]);
                                        if (ii + jj < L - 1)
                                        {
                                            int D = Idx(ii + 1, jj + 1);
                                            tri.Add(gp[B]); tri.Add(gp[D]); tri.Add(gp[C]);
                                            tcol.Add(gc[B]); tcol.Add(gc[D]); tcol.Add(gc[C]);
                                        }
                                    }
                                bool prevAA2 = fill.IsAntialias; var prevCol2 = fill.Color;
                                fill.IsAntialias = false; fill.Color = SKColors.White;
                                canvas.DrawVertices(SKVertexMode.Triangles, tri.ToArray(), tcol.ToArray(), fill);
                                fill.IsAntialias = prevAA2; fill.Color = prevCol2;
                                if (edgeOn) { stroke.Color = ParseColor(p.EdgeColor); stroke.StrokeWidth = (float)Math.Max(0.5, p.LineWidth); canvas.DrawPath(path, stroke); }
                            }
                            else if (p.VertCols != null && p.VertCols.Length == 3 && p.Xs.Length >= 3)
                            {
                                // Gouraud REAL (FaceColor='interp' de MATLAB): color interpolado por-pixel
                                // entre los 3 vertices via DrawVertices. SIN anti-alias -> triangulos
                                // adyacentes abutan exacto, sin costuras (antes: relleno plano -> malla visible).
                                var pts = new[] {
                                    new SKPoint(TX(p.Xs[0]), TY(p.Ys[0])),
                                    new SKPoint(TX(p.Xs[1]), TY(p.Ys[1])),
                                    new SKPoint(TX(p.Xs[2]), TY(p.Ys[2])) };
                                var cols = new SKColor[3];
                                for (int vi = 0; vi < 3; vi++)
                                {
                                    var q = p.VertCols[vi].Split(',');
                                    cols[vi] = new SKColor((byte)int.Parse(q[0].Trim()), (byte)int.Parse(q[1].Trim()), (byte)int.Parse(q[2].Trim()));
                                }
                                // DrawVertices MODULA el color del paint con los del vertice; con paint
                                // negro daba todo negro -> ponerlo BLANCO (blanco*color = color).
                                bool prevAA = fill.IsAntialias; var prevCol = fill.Color;
                                fill.IsAntialias = false; fill.Color = SKColors.White;
                                canvas.DrawVertices(SKVertexMode.Triangles, pts, cols, fill);
                                fill.IsAntialias = prevAA; fill.Color = prevCol;
                                if (edgeOn) { stroke.Color = ParseColor(p.EdgeColor); stroke.StrokeWidth = (float)Math.Max(0.5, p.LineWidth); canvas.DrawPath(path, stroke); }
                            }
                            else
                            {
                                // Color por VALOR (colormap) para mallas FEM; si no, FaceColor fijo.
                                var fc = !double.IsNaN(p.Val) ? ValColor(p.Val) : ParseColor(p.FaceColor);
                                if (fc.Alpha > 0) { fill.Color = fc.WithAlpha((byte)(255 * p.FaceAlpha)); canvas.DrawPath(path, fill); }
                                stroke.Color = ParseColor(p.EdgeColor); stroke.StrokeWidth = (float)Math.Max(0.5, p.LineWidth); canvas.DrawPath(path, stroke);
                            }
                        }
                        else
                        {
                            stroke.Color = ParseColor(p.Color);
                            stroke.StrokeWidth = (float)Math.Max(0.8, p.LineWidth);
                            // Estilo de linea como MATLAB: '--' dash, ':' dot, '-.' dashdot.
                            float lw = stroke.StrokeWidth;
                            stroke.PathEffect = p.Dash switch
                            {
                                "dash" => SKPathEffect.CreateDash(new[] { 6f * lw / 1.5f, 4f * lw / 1.5f }, 0),
                                "dot" => SKPathEffect.CreateDash(new[] { 1.5f, 3f * lw / 1.5f }, 0),
                                "dashdot" => SKPathEffect.CreateDash(new[] { 6f, 3f, 1.5f, 3f }, 0),
                                _ => null
                            };
                            canvas.DrawPath(path, stroke);
                            stroke.PathEffect = null;
                        }
                        path.Dispose();
                    }
                    else if (p.Kind == "markers2d" && p.Xs != null)
                    {
                        // MATLAB: marcador HUECO salvo que se pida MarkerFaceColor, y con
                        // la forma que pidio el script (no siempre circulo).
                        string symK = p.Text ?? "circle";
                        bool relleno = !string.IsNullOrEmpty(p.FaceColor) && p.FaceColor != "none";
                        float r = (float)Math.Max(2.0, p.FontSize / 2.0);
                        if (symK == "point") { r = (float)Math.Max(1.5, p.FontSize / 6.0); relleno = true; }
                        var mk = new SKPaint {
                            IsAntialias = true,
                            Style = relleno ? SKPaintStyle.Fill : SKPaintStyle.Stroke,
                            StrokeWidth = 1.2f,
                            Color = ParseColor(relleno ? p.FaceColor : (string.IsNullOrEmpty(p.EdgeColor) ? "#1f77b4" : p.EdgeColor))
                        };
                        for (int i = 0; i < p.Xs.Length; i++)
                        {
                            float cxK = TX(p.Xs[i]), cyK = TY(p.Ys[i]);
                            if (symK == "square")
                                canvas.DrawRect(cxK - r, cyK - r, 2 * r, 2 * r, mk);
                            else if (symK == "diamond")
                            {
                                using var pa = new SKPath();
                                pa.MoveTo(cxK, cyK - r); pa.LineTo(cxK + r, cyK);
                                pa.LineTo(cxK, cyK + r); pa.LineTo(cxK - r, cyK); pa.Close();
                                canvas.DrawPath(pa, mk);
                            }
                            else if (symK == "cross")
                            { canvas.DrawLine(cxK - r, cyK, cxK + r, cyK, mk); canvas.DrawLine(cxK, cyK - r, cxK, cyK + r, mk); }
                            else if (symK == "x")
                            { float d = r * 0.7071f; canvas.DrawLine(cxK - d, cyK - d, cxK + d, cyK + d, mk); canvas.DrawLine(cxK - d, cyK + d, cxK + d, cyK - d, mk); }
                            else
                                canvas.DrawCircle(cxK, cyK, r, mk);
                        }
                        mk.Dispose();
                    }
                    else if (p.Kind == "text2d" && p.Xs != null && p.Xs.Length > 0)
                    {
                        pendingText.Add(p);   // diferido: se dibuja sin clip (como MATLAB)
                    }
                }
                canvas.Restore();   // fin del clip del area de plot (curvas dentro del box)
                // Re-dibujar el marco encima (fieldfill por-píxel lo pudo cubrir), como MATLAB.
                canvas.DrawRect(plotL, plotT, plotR - plotL, plotB - plotT, axis);
                // Anotaciones de texto SIN clip (MATLAB dibuja text() fuera del box si se sale).
                foreach (var p in pendingText)
                {
                    // text() como MATLAB: '\n' = varias lineas, \phi \gamma ... = griegas, sub/superindices,
                    // VerticalAlignment (def 'middle'), BackgroundColor/EdgeColor = caja detras del texto.
                    txt.Color = ParseColor(p.Color);
                    var al = p.Align == "center" ? SKTextAlign.Center : (p.Align == "right" ? SKTextAlign.Right : SKTextAlign.Left);
                    string[] lines = (p.Text ?? "").Replace("\r", "").Split('\n');
                    using var tfont = new SKFont(font.Typeface, (float)(p.FontSize > 0 ? p.FontSize * 1.33 : font.Size));
                    float lh = tfont.Size * 1.25f;
                    float wmax = 0;
                    foreach (var ln in lines) wmax = Math.Max(wmax, tfont.MeasureText(TexPlainG(ln)));
                    float x0 = TX(p.Xs[0]), y0 = TY(p.Ys[0]);
                    float totalH = lh * lines.Length;
                    float top = p.VAlign == "top" ? y0 : (p.VAlign == "bottom" ? y0 - totalH : y0 - totalH / 2f);
                    float left = al == SKTextAlign.Center ? x0 - wmax / 2f : (al == SKTextAlign.Right ? x0 - wmax : x0);
                    bool hasBg = !string.IsNullOrEmpty(p.FaceColor) && p.FaceColor != "none";
                    bool hasEdge = !string.IsNullOrEmpty(p.EdgeColor) && p.EdgeColor != "none";
                    if (hasBg || hasEdge)
                    {
                        float pad = 3f;
                        var rect = new SKRect(left - pad, top - pad, left + wmax + pad, top + totalH + pad);
                        if (hasBg) { using var bg = new SKPaint { Color = ParseColor(p.FaceColor), Style = SKPaintStyle.Fill, IsAntialias = true }; canvas.DrawRect(rect, bg); }
                        if (hasEdge) { using var ed = new SKPaint { Color = ParseColor(p.EdgeColor), Style = SKPaintStyle.Stroke, StrokeWidth = 1f, IsAntialias = true }; canvas.DrawRect(rect, ed); }
                    }
                    for (int li = 0; li < lines.Length; li++)
                    {
                        float by = top + lh * li + tfont.Size * 0.95f;   // baseline de la linea li
                        string ln = TexGreek(lines[li]);
                        if (al == SKTextAlign.Center) DrawTexCentered(canvas, ln, x0, by, tfont, txt, font.Typeface);
                        else canvas.DrawText(TexPlainG(ln), x0, by, al, tfont, txt);
                    }
                }

                // ── COLORBAR (malla FEM coloreada por valor), como MATLAB ──
                if (hasVals)
                {
                    // posicion/ancho del colorbar = los de MATLAB (medidos: x≈0.818·W, ancho≈0.046·W).
                    // colorbar: contourf → fina y a la derecha del axes ancho (MATLAB); mallas → como estaba.
                    float cbW = (float)(0.046 * width);
                    float cbX = (float)(0.818 * width), cbH = plotB - plotT;
                    // Colormap custom pequeño (p.ej. 12 colores GEO5) -> barra DISCRETA (bloques),
                    // no gradiente. _cbReverse -> vmin arriba (GEO5: -0.4 azul arriba, 5 rojo abajo).
                    bool cbDiscrete = _figCmapName == "custom" && _customCmapRgb != null && _customCmapRgb.Length <= 24;
                    int cbN = cbDiscrete ? _customCmapRgb.Length : 0;
                    for (int i = 0; i < (int)cbH; i++)
                    {
                        double t = _cbReverse ? (i / cbH) : (1.0 - i / cbH);   // t: 0=vmin, 1=vmax
                        float[] cbr;
                        if (cbDiscrete) { int band = (int)(t * cbN); if (band < 0) band = 0; if (band >= cbN) band = cbN - 1; cbr = _customCmapRgb[band]; }
                        else cbr = CmapF(_figCmapName, t);
                        using var cbp = new SKPaint { Style = SKPaintStyle.Fill, Color = new SKColor(
                            (byte)Math.Round(255 * Math.Max(0, Math.Min(1, cbr[0]))),
                            (byte)Math.Round(255 * Math.Max(0, Math.Min(1, cbr[1]))),
                            (byte)Math.Round(255 * Math.Max(0, Math.Min(1, cbr[2]))) ) };
                        canvas.DrawRect(cbX, plotT + i, cbW, 1.2f, cbp);
                    }
                    canvas.DrawRect(cbX, plotT, cbW, cbH, axis);
                    // densidad de ticks del colorbar ∝ altura (~1 cada 55px, como MATLAB: ~5-6 ticks).
                    int cbTgt = Math.Max(4, (int)(cbH / 55));
                    var cbTicks = _cbTicks != null && _cbTicks.Length > 0 ? new System.Collections.Generic.List<double>(_cbTicks) : NiceTicks(vmin, vmax, cbTgt);
                    // Factor común ×10ⁿ como MATLAB cuando los valores son muy chicos/grandes.
                    double cbMax = 0; foreach (var tv in cbTicks) if (Math.Abs(tv) > cbMax) cbMax = Math.Abs(tv);
                    int cbExp = 0; double cbFac = 1;
                    bool cbLabels = _cbTickLabels != null && _cbTicks != null && _cbTickLabels.Length == _cbTicks.Length;
                    if (cbMax > 0 && !cbLabels) { int e = (int)Math.Floor(Math.Log10(cbMax)); if (e <= -3 || e >= 5) { cbExp = e; cbFac = Math.Pow(10, e); } }
                    for (int ti = 0; ti < cbTicks.Count; ti++)
                    {
                        double tv = cbTicks[ti];
                        if (tv < vmin - 1e-9 || tv > vmax + 1e-9) continue;
                        float frac = (float)((tv - vmin) / (vmax - vmin) * cbH);
                        float ty = _cbReverse ? (plotT + frac) : (plotB - frac);   // reverse: vmin arriba
                        canvas.DrawLine(cbX + cbW, ty, cbX + cbW + 3, ty, axis);
                        string lab = cbLabels ? _cbTickLabels[ti] : FmtTick(tv / cbFac);   // TickLabels de MATLAB
                        canvas.DrawText(lab, cbX + cbW + 5, ty + 3, SKTextAlign.Left, font, txt);
                    }
                    if (cbExp != 0)   // etiqueta del multiplicador ×10ⁿ arriba de la barra (como MATLAB)
                    {
                        float ex = cbX - 3, ey = plotT - 5;
                        canvas.DrawText("×10", ex, ey, SKTextAlign.Left, font, txt);
                        float w10 = font.MeasureText("×10");
                        using var sfont = new SKFont(tface, fTick * 0.8f);
                        canvas.DrawText(cbExp.ToString(), ex + w10 + 1, ey - 5, SKTextAlign.Left, sfont, txt);
                    }
                    // ── ETIQUETA del colorbar (cb.Label.String), vertical a la derecha (=MATLAB) ──
                    if (!string.IsNullOrEmpty(_cbLabel))
                    {
                        float lx = cbX + cbW + 42, ly = plotT + cbH / 2f;
                        canvas.Save();
                        canvas.RotateDegrees(-90, lx, ly);
                        DrawTexCentered(canvas, _cbLabel, lx, ly, font, txt, tface);   // TeX (d_x -> subindice) como MATLAB
                        canvas.Restore();
                    }
                }

                // ── LEGEND (caja con muestras de linea + nombres), como MATLAB ──
                if (_figShowLegend)
                {
                    var items = new System.Collections.Generic.List<(SKColor col, string dash, string name, float lw, bool isMarker, SKColor mFill, SKColor mEdge, string sym)>();
                    int li = 0;
                    foreach (var p in _figPrims)
                    {
                        // Las series con marcador (plot(x,y,'o')) TAMBIÉN entran en la leyenda,
                        // como en MATLAB: se dibuja el glifo del marcador, no una línea.
                        bool isMk = p.Kind == "markers2d";
                        if (p.Kind != "line2d" && p.Kind != "patch2d" && !isMk) continue;
                        string nm = (_figLegendNames != null && li < _figLegendNames.Length) ? _figLegendNames[li] : p.Name;
                        li++;
                        if (string.IsNullOrEmpty(nm)) continue;
                        if (isMk)
                            items.Add((ParseColor(p.FaceColor), "solid", nm, 1f, true,
                                       ParseColor(p.FaceColor),
                                       ParseColor(string.IsNullOrEmpty(p.EdgeColor) ? "black" : p.EdgeColor),
                                       p.Text ?? "circle"));
                        else
                            items.Add((ParseColor(p.Color), p.Dash, nm, (float)Math.Max(0.8, p.LineWidth), false, default, default, null));
                    }
                    if (items.Count > 0)
                    {
                        float lh = 15, pad = 6, sample = 22, gap = 5, tw = 0;
                        foreach (var it in items) { font.MeasureText(it.name, out var rr); tw = Math.Max(tw, rr.Width); }
                        float boxW = pad * 2 + sample + gap + tw, boxH = pad * 2 + items.Count * lh;
                        // Ubicacion (default northeast); soporta las esquinas.
                        string loc = _figLegendLoc ?? "northeast";
                        float bx = loc.Contains("west") ? plotL + 6 : plotR - boxW - 6;
                        float by = loc.Contains("south") ? plotB - boxH - 6 : plotT + 6;
                        using var legBg = new SKPaint { Style = SKPaintStyle.Fill, Color = SKColors.White, IsAntialias = true };
                        canvas.DrawRect(bx, by, boxW, boxH, legBg);
                        canvas.DrawRect(bx, by, boxW, boxH, axis);
                        for (int k = 0; k < items.Count; k++)
                        {
                            var it = items[k];
                            float cy = by + pad + k * lh + lh / 2f;
                            if (it.isMarker)
                            {
                                // Glifo del marcador centrado en la muestra (círculo o cuadrado).
                                float mx = bx + pad + sample / 2f, r = 5f;
                                using var mf = new SKPaint { Style = SKPaintStyle.Fill, Color = it.mFill, IsAntialias = true };
                                using var me = new SKPaint { Style = SKPaintStyle.Stroke, Color = it.mEdge, StrokeWidth = 1.2f, IsAntialias = true };
                                if (it.sym != null && (it.sym.Contains("square") || it.sym == "s"))
                                {
                                    var rect = new SKRect(mx - r, cy - r, mx + r, cy + r);
                                    canvas.DrawRect(rect, mf); canvas.DrawRect(rect, me);
                                }
                                else { canvas.DrawCircle(mx, cy, r, mf); canvas.DrawCircle(mx, cy, r, me); }
                                txt.Color = SKColors.Black;
                                canvas.DrawText(it.name, bx + pad + sample + gap, cy + 4, SKTextAlign.Left, font, txt);
                                continue;
                            }
                            stroke.Color = it.col; stroke.StrokeWidth = it.lw;
                            stroke.PathEffect = it.dash switch
                            {
                                "dash" => SKPathEffect.CreateDash(new[] { 6f, 4f }, 0),
                                "dot" => SKPathEffect.CreateDash(new[] { 1.5f, 3f }, 0),
                                "dashdot" => SKPathEffect.CreateDash(new[] { 6f, 3f, 1.5f, 3f }, 0),
                                _ => null
                            };
                            canvas.DrawLine(bx + pad, cy, bx + pad + sample, cy, stroke);
                            stroke.PathEffect = null;
                            txt.Color = SKColors.Black;
                            canvas.DrawText(it.name, bx + pad + sample + gap, cy + 4, SKTextAlign.Left, font, txt);
                        }
                    }
                }
            }
            return bmp;
        }
        /// <summary>Rasteriza la figura 2D a RGB row-major (para getframe -> GIF).</summary>
        public static byte[] RasterizeFigure(int width, int height)
        {
            var bmp = BuildFigureBitmap(width, height);
            if (bmp == null) return null;
            using (bmp)
            {
                var px = bmp.Pixels;   // SKColor[]
                var rgb = new byte[width * height * 3];
                for (int i = 0; i < px.Length; i++) { rgb[i * 3] = px[i].Red; rgb[i * 3 + 1] = px[i].Green; rgb[i * 3 + 2] = px[i].Blue; }
                return rgb;
            }
        }
        /// <summary>Rasteriza la figura 2D a PNG (bytes). SIN JS — para embeber como &lt;img&gt; en WebView2.</summary>
        public static byte[] RasterizeFigurePng(int width, int height, int ss = 1)
        {
            var bmp = BuildFigureBitmap(width, height, ss);
            if (bmp == null) return null;
            using (bmp)
            using (var img = SKImage.FromBitmap(bmp))
            using (var data = img.Encode(SKEncodedImageFormat.Png, 100))
                return data.ToArray();
        }
        /// <summary>Descarta la figura actual (para que FinishFigure no la re-emita tras un print/PNG,
        /// y para clf/cla = limpiar la figura en su sitio como MATLAB). Olvida primitivas, traces,
        /// anotaciones, HOVER y la malla RETENIDA. Resetear la malla retenida es CRITICO para
        /// animaciones: drawframe hace `clf; patch; patch; drawnow` por iteracion — sin esto la
        /// lista retenida creceria 2 mallas por frame (O(N^2): picos de 58s/103s y "Collection was
        /// modified"). Antes clf/cla eran no-op y esta funcion no tocaba _retList.</summary>
        public static void ClearFigure()
        {
            _figTraces = null; _figAnnotations = null; _figPrims = null;
            _hoverVals = null; _hoverLabels = null;
            ResetRetainedMesh();
        }

        /// <summary>¿La figura 2D tiene parches con valor por-cara? (patch FaceVertexCData) -> hover interactivo.</summary>
        public static bool HasFaceValues() =>
            _figPrims != null && _figPrims.Exists(p => p.Kind == "patch2d" && !double.IsNaN(p.Val));

        /// <summary>Emite un CANVAS interactivo (JS inline, SIN librerias externas): dibuja la malla
        /// coloreada + tooltip que sigue al cursor mostrando el valor bajo el cursor y la COLUMNA
        /// vertical de valores en esa x. Consume y limpia la figura.</summary>
        // idOverride: id fijo del canvas (para ANIMAR: cada frame reemplaza el mismo lienzo).
        // keepState=true: NO nulifica _figPrims/_hoverVals (para poder seguir actualizando la figura).

        // ── Visor 3D de SÓLIDOS con CORTE INTERACTIVO (Lab): recibe elementos de VOLUMEN
        //    (tetraedros/hexaedros), extrae la piel en JS y un SLIDER corta el solido en vivo;
        //    la seccion cortada queda RELLENA (sin huecos). THREE.js OrbitControls + hover, jet_r. ──
        private const string SolidClipTemplate = @"<div id=""__ID__"" style=""font:13px Segoe UI;color:#222""><b>__TITLE__</b> &nbsp; Campo: <select id=""__ID__s"" style=""font:13px Segoe UI;padding:2px 6px"">__OPTS__</select> &nbsp;|&nbsp; Corte: <select id=""__ID__ax"" style=""font:13px Segoe UI;padding:2px 6px""><option value=""-1"">(sin corte)</option><option value=""0"">X</option><option value=""1"">Y</option><option value=""2"">Z</option></select> <input id=""__ID__sl"" type=""range"" min=""0"" max=""1000"" value=""1000"" style=""width:170px;vertical-align:middle""> <label style=""font-size:12px""><input id=""__ID__op"" type=""checkbox""> lado opuesto</label><div id=""__ID__3"" style=""margin-top:6px""></div><div id=""__ID__h"" style=""font:12px Consolas;color:#555;margin-top:4px""></div></div><script>(function(){
var D=__DATA__;var ND=D.nd,EL=D.el,FS=D.fs;
var SEL=document.getElementById(""__ID__s""),AX=document.getElementById(""__ID__ax""),SL=document.getElementById(""__ID__sl""),OP=document.getElementById(""__ID__op""),P3=document.getElementById(""__ID__3""),HD=document.getElementById(""__ID__h"");
var TetF=[[0,1,2],[0,1,3],[0,2,3],[1,2,3]],HexF=[[0,1,2,3],[4,5,6,7],[0,1,5,4],[1,2,6,5],[2,3,7,6],[3,0,4,7]];
function boundary(els){var cnt={},rep={};for(var e=0;e<els.length;e++){var el=els[e],F=el.length===4?TetF:(el.length===8?HexF:null);if(!F)continue;for(var f=0;f<F.length;f++){var fd=F[f],nds=[];for(var k=0;k<fd.length;k++)nds.push(el[fd[k]]);var key=nds.slice().sort(function(x,y){return x-y;}).join(""_"");if(cnt[key]){cnt[key]++;}else{cnt[key]=1;rep[key]=nds;}}}var tris=[];for(var key in cnt)if(cnt[key]===1){var nd=rep[key];tris.push([nd[0],nd[1],nd[2]]);if(nd.length===4)tris.push([nd[0],nd[2],nd[3]]);}return tris;}
var mn=[1e30,1e30,1e30],mx=[-1e30,-1e30,-1e30];for(var i=0;i<ND.length;i++)for(var d=0;d<3;d++){if(ND[i][d]<mn[d])mn[d]=ND[i][d];if(ND[i][d]>mx[d])mx[d]=ND[i][d];}
var cx=(mn[0]+mx[0])/2,cy=(mn[1]+mx[1])/2,cz=(mn[2]+mx[2])/2,diag=Math.hypot(mx[0]-mn[0],mx[1]-mn[1],mx[2]-mn[2])||1;
function kept(){var ax=+AX.value;if(ax<0)return EL;var t=+SL.value/1000,pos=mn[ax]+t*(mx[ax]-mn[ax]),side=OP.checked?-1:1,out=[];for(var e=0;e<EL.length;e++){var el=EL[e],c=0;for(var k=0;k<el.length;k++)c+=ND[el[k]][ax];c/=el.length;if(side*(c-pos)<=0)out.push(el);}return out;}
function jt(t){t=Math.max(0,Math.min(1,t));return[Math.max(0,Math.min(1,Math.min(4*t-1.5,-4*t+4.5))),Math.max(0,Math.min(1,Math.min(4*t-0.5,-4*t+3.5))),Math.max(0,Math.min(1,Math.min(4*t+0.5,-4*t+2.5)))];}
var scn,cam,ren,ctrl,grp,mesh,geo,vv,tip,rdy=false;
function init3D(){var W=580,H=500;scn=new THREE.Scene();scn.background=new THREE.Color(0xeef0f4);cam=new THREE.PerspectiveCamera(45,W/H,diag*0.001,diag*50);ren=new THREE.WebGLRenderer({antialias:true,preserveDrawingBuffer:true});ren.setSize(W,H);P3.appendChild(ren.domElement);cam.up.set(0,0,1);cam.position.set(cx+diag*1.1,cy-diag*1.5,cz+diag*1.0);cam.lookAt(cx,cy,cz);ctrl=new THREE.OrbitControls(cam,ren.domElement);ctrl.target.set(cx,cy,cz);ctrl.update();scn.add(new THREE.AmbientLight(0xffffff,.95));var dl=new THREE.DirectionalLight(0xffffff,.45);dl.position.set(diag,-diag*1.2,diag*1.5);scn.add(dl);tip=document.createElement(""div"");tip.style.cssText=""position:fixed;pointer-events:none;background:rgba(20,20,28,.9);color:#fff;font:12px Consolas;padding:3px 7px;border-radius:4px;display:none;z-index:99999"";document.body.appendChild(tip);var ray=new THREE.Raycaster(),mo=new THREE.Vector2();ren.domElement.addEventListener(""mousemove"",function(ev){if(!mesh)return;var r=ren.domElement.getBoundingClientRect();mo.x=((ev.clientX-r.left)/r.width)*2-1;mo.y=-((ev.clientY-r.top)/r.height)*2+1;ray.setFromCamera(mo,cam);var h=ray.intersectObject(mesh,false);if(h.length){var f=h[0].face,ap=geo.attributes.position,p0=new THREE.Vector3().fromBufferAttribute(ap,f.a),p1=new THREE.Vector3().fromBufferAttribute(ap,f.b),p2=new THREE.Vector3().fromBufferAttribute(ap,f.c),bc=new THREE.Vector3();new THREE.Triangle(p0,p1,p2).getBarycoord(h[0].point,bc);var val=bc.x*vv[f.a]+bc.y*vv[f.b]+bc.z*vv[f.c];tip.style.display=""block"";tip.style.left=(ev.clientX+13)+""px"";tip.style.top=(ev.clientY+8)+""px"";tip.innerHTML=val.toFixed(4);}else tip.style.display=""none"";});ren.domElement.addEventListener(""mouseleave"",function(){tip.style.display=""none"";});function anim(){requestAnimationFrame(anim);ctrl.update();ren.render(scn,cam);}anim();rdy=true;}
function build(){if(!rdy)return;if(grp)scn.remove(grp);grp=new THREE.Group();var tris=boundary(kept());var name=SEL.value,V=FS[name];var vn=1e30,vx=-1e30;for(var q=0;q<V.length;q++){if(V[q]<vn)vn=V[q];if(V[q]>vx)vx=V[q];}if(vx-vn<1e-9)vx=vn+1;var pos=[],col=[];vv=[];for(var t=0;t<tris.length;t++){var tr=tris[t];for(var k=0;k<3;k++){var ni=tr[k],p=ND[ni];pos.push(p[0],p[1],p[2]);var c=jt(1-(V[ni]-vn)/(vx-vn));col.push(c[0],c[1],c[2]);vv.push(V[ni]);}}geo=new THREE.BufferGeometry();geo.setAttribute(""position"",new THREE.Float32BufferAttribute(pos,3));geo.setAttribute(""color"",new THREE.Float32BufferAttribute(col,3));geo.computeVertexNormals();mesh=new THREE.Mesh(geo,new THREE.MeshBasicMaterial({vertexColors:true,side:THREE.DoubleSide}));grp.add(mesh);scn.add(grp);var axn=[""X"",""Y"",""Z""],a=+AX.value;HD.innerHTML=""max=""+vx.toFixed(4)+""  min=""+vn.toFixed(4)+""  ·  ""+tris.length+"" triangulos""+(a<0?""  (sin corte)"":""  ·  corte ""+axn[a]+"" @ ""+(mn[a]+(+SL.value/1000)*(mx[a]-mn[a])).toFixed(3))+""  (arrastra=orbita · corte=slider)"";}
SEL.onchange=build;AX.onchange=build;SL.oninput=build;OP.onchange=build;
function go(){init3D();build();}
if(window.THREE&&THREE.OrbitControls){go();}else{var s1=document.createElement(""script"");s1.src=""https://cdn.jsdelivr.net/npm/three@0.145.0/build/three.min.js"";s1.onload=function(){var s2=document.createElement(""script"");s2.src=""https://cdn.jsdelivr.net/npm/three@0.145.0/examples/js/controls/OrbitControls.js"";s2.onload=go;document.head.appendChild(s2);};document.head.appendChild(s1);}
})();</script>";

        public static string SolidClipViewer(double[][] nodes3, int[][] elems,
                                             string[] names, double[][] vals, string title, string id)
        {
            var nd = new System.Text.StringBuilder("[");
            for (int i = 0; i < nodes3.Length; i++)
            {
                if (i > 0) nd.Append(',');
                nd.Append('[').Append(nodes3[i][0].ToString(Inv)).Append(',')
                  .Append(nodes3[i][1].ToString(Inv)).Append(',').Append(nodes3[i][2].ToString(Inv)).Append(']');
            }
            nd.Append(']');
            var el = new System.Text.StringBuilder("[");
            for (int i = 0; i < elems.Length; i++)
            {
                if (i > 0) el.Append(',');
                el.Append('[');
                for (int k = 0; k < elems[i].Length; k++) { if (k > 0) el.Append(','); el.Append(elems[i][k]); }
                el.Append(']');
            }
            el.Append(']');
            var fs = new System.Text.StringBuilder("{");
            for (int i = 0; i < names.Length; i++)
            {
                if (i > 0) fs.Append(',');
                fs.Append('"').Append(names[i]).Append("\":[");
                var v = vals[i];
                for (int k = 0; k < v.Length; k++) { if (k > 0) fs.Append(','); fs.Append(v[k].ToString(Inv)); }
                fs.Append(']');
            }
            fs.Append('}');
            string data = "{nd:" + nd + ",el:" + el + ",fs:" + fs + "}";
            var opts = new System.Text.StringBuilder();
            foreach (var n in names) opts.Append("<option value=\"").Append(n).Append("\">").Append(n).Append("</option>");
            return SolidClipTemplate.Replace("__ID__", id).Replace("__TITLE__", title)
                                    .Replace("__OPTS__", opts.ToString()).Replace("__DATA__", data);
        }

        public static string RenderInteractiveMesh(int width, int height, string idOverride = null, bool keepState = false)
        {
            if (_figPrims == null) { return null; }
            var faces = _figPrims.FindAll(p => p.Kind == "patch2d" && p.Xs != null && !double.IsNaN(p.Val));
            if (faces.Count == 0) return null;
            double xmin = double.MaxValue, xmax = double.MinValue, ymin = double.MaxValue, ymax = double.MinValue;
            foreach (var p in _figPrims)
            {
                if (p.Xs == null) continue;
                foreach (var x in p.Xs) { if (x < xmin) xmin = x; if (x > xmax) xmax = x; }
                foreach (var y in p.Ys) { if (y < ymin) ymin = y; if (y > ymax) ymax = y; }
            }
            string id = idOverride ?? ("m" + (++_plotCounter));
            var pj = new StringBuilder(); pj.Append('[');
            var vcb = new StringBuilder(); vcb.Append('[');   // VC[k]: 3 colores de vértice (Gouraud) o null (plano)
            for (int k = 0; k < faces.Count; k++)
            {
                var p = faces[k]; if (k > 0) pj.Append(',');
                pj.Append("[[");
                for (int i = 0; i < p.Xs.Length; i++) { if (i > 0) pj.Append(','); pj.Append(p.Xs[i].ToString("0.##", Inv)); pj.Append(','); pj.Append(p.Ys[i].ToString("0.##", Inv)); }
                pj.Append("],\"").Append(p.FaceColor).Append("\",").Append(p.Val.ToString("0.####", Inv));
                if (_hoverVals != null && k < _hoverVals.Length && _hoverVals[k] != null)
                {
                    pj.Append(",[");
                    for (int c = 0; c < _hoverVals[k].Length; c++) { if (c > 0) pj.Append(','); pj.Append(_hoverVals[k][c].ToString("0.#####", Inv)); }
                    pj.Append("]");
                }
                pj.Append(']');
                if (k > 0) vcb.Append(',');
                if (p.VertCols != null && p.VertCols.Length == 3)
                    vcb.Append("[\"").Append(p.VertCols[0]).Append("\",\"").Append(p.VertCols[1]).Append("\",\"").Append(p.VertCols[2]).Append("\"]");
                else vcb.Append("null");
            }
            pj.Append(']');
            vcb.Append(']');
            string hlJs = "null";
            if (_hoverLabels != null)
            {
                var lb = new StringBuilder("[");
                for (int i = 0; i < _hoverLabels.Length; i++) { if (i > 0) lb.Append(','); lb.Append("\"").Append(EscapeJs(_hoverLabels[i])).Append("\""); }
                lb.Append("]"); hlJs = lb.ToString();
            }
            var lj = new StringBuilder(); lj.Append('['); bool first = true;
            foreach (var p in _figPrims.FindAll(q => q.Kind == "line2d" && q.Xs != null))
            {
                if (!first) lj.Append(','); first = false; lj.Append("[[");
                for (int i = 0; i < p.Xs.Length; i++) { if (i > 0) lj.Append(','); lj.Append(p.Xs[i].ToString("0.##", Inv)); lj.Append(','); lj.Append(p.Ys[i].ToString("0.##", Inv)); }
                lj.Append("],\"").Append(p.Color ?? "#555").Append("\"]");
            }
            lj.Append(']');
            // El CAMPO del hover = el MISMO PNG del estático (SkiaSharp, Gouraud real por DrawVertices,
            // nítido 2×) dibujado de FONDO; encima solo el crosshair+valor. Antes el canvas usaba un
            // Gouraud aproximado en JS (colorbar jet, etiquetas hardcodeadas) que NO se veía igual al
            // estático. RasterizeFigurePng también fija el mapeo dato->pixel (_pmOX...) para alinear.
            // ss=2: PNG a 2× (supersampled = texto/campo nítidos). En JS se muestra a tamaño 1:1
            // FÍSICO (naturalWidth/devicePixelRatio) para que el WebView2 NO lo re-escale (borroso).
            string fieldB64 = "";
            try { var _pb = RasterizeFigurePng(width, height, 2); if (_pb != null && _pb.Length > 0) fieldB64 = System.Convert.ToBase64String(_pb); } catch { }
            double mOX=_pmOX, mOY=_pmOY, mSX=_pmSX, mSY=_pmSY, mX0=_pmX0, mY0=_pmY0, mH=_pmH;
            // Hover SOLO si el script lo pidió con hoverdata() — igual que MATLAB, donde un
            // patch NO tiene hover salvo que se active WindowButtonMotionFcn/datacursormode.
            // Sin hoverdata: sin tooltip, sin listener, cursor normal. Mismo script, mismo
            // comportamiento (el Lab ya NO inventa hover en mallas coloreadas).
            bool hoverOn = (_hoverVals != null || faces.Count > 0) && !HeadlessNoHover;   // malla coloreada -> hover automático; en captura headless SIN tooltip (=saveas MATLAB, imagen limpia)
            var sb = new StringBuilder();
            sb.Append("<div style=\"display:inline-block;font-family:sans-serif\">");
            sb.Append($"<div style=\"text-align:center;font-size:14px;margin:3px\">{TexToHtml(_figTitle)}</div>");
            // El CAMPO = el MISMO PNG del estático (SkiaSharp, Gouraud real, nítido 2×) como <img>
            // NATIVO -> idéntico al estático, sin re-render en JS. El canvas va TRANSPARENTE encima,
            // solo para el crosshair del cursor. Antes el canvas re-dibujaba un Gouraud aproximado
            // (colorbar jet, etiquetas hardcodeadas) que no se parecía al estático.
            sb.Append($"<div style=\"position:relative;width:{width}px;height:{height}px\">");
            sb.Append($"<img src=\"data:image/png;base64,{fieldB64}\" style=\"width:{width}px;height:{height}px;display:block;border:1px solid #ccc\"/>");
            sb.Append($"<canvas id=\"cv{id}\" width=\"{width * 2}\" height=\"{height * 2}\" style=\"position:absolute;top:0;left:0;width:{width}px;height:{height}px;cursor:{(hoverOn ? "crosshair" : "default")}\"></canvas>");
            if (hoverOn)
                sb.Append($"<div id=\"tt{id}\" style=\"position:absolute;pointer-events:none;display:none;background:rgba(15,15,22,.92);color:#fff;font:11px monospace;padding:5px 8px;border-radius:4px;white-space:pre;z-index:20\"></div>");
            sb.Append("</div></div>\n<script>(function(){\n");
            double cmin, cmax;
            if (!TryGetCAxis(out cmin, out cmax))
            {
                cmin = double.MaxValue; cmax = double.MinValue;
                foreach (var p in faces) { if (p.Val < cmin) cmin = p.Val; if (p.Val > cmax) cmax = p.Val; }
                if (cmax <= cmin) cmax = cmin + 1;
            }
            sb.Append($"var P={pj},VC={vcb},L={lj},HL={hlJs},SHOWMESH={(_figBandN > 0 ? "false" : "true")},bb=[{xmin.ToString("0.##", Inv)},{ymin.ToString("0.##", Inv)},{xmax.ToString("0.##", Inv)},{ymax.ToString("0.##", Inv)}],cmin={cmin.ToString("0.####", Inv)},cmax={cmax.ToString("0.####", Inv)};\n");
            sb.Append($"var cv=document.getElementById('cv{id}'),ctx=cv.getContext('2d'),tt=document.getElementById('tt{id}');\n");
            sb.Append("ctx.setTransform(2,0,0,2,0,0);\n");   // high-DPI: backing 2×, contexto escalado -> dibujo en coords lógicas sale nítido
            sb.Append($"var W={width},H={height};\n");
            // Mapeo dato->pixel EXACTO del PNG (para alinear crosshair y lectura de valor con el campo).
            sb.Append($"var FB='data:image/png;base64,{fieldB64}';\n");
            sb.Append($"var MOX={mOX.ToString("0.###", Inv)},MOY={mOY.ToString("0.###", Inv)},MSX={mSX.ToString("0.#####", Inv)},MSY={mSY.ToString("0.#####", Inv)},MX0={mX0.ToString("0.#####", Inv)},MY0={mY0.ToString("0.#####", Inv)},MH={mH.ToString("0.###", Inv)};\n");
            sb.Append(@"function TX(x){return MOX+(x-MX0)*MSX;}function TY(y){return MH-MOY-(y-MY0)*MSY;}
function jet(t){t=Math.max(0,Math.min(1,t));var r=Math.min(4*t-1.5,-4*t+4.5),g=Math.min(4*t-0.5,-4*t+3.5),b=Math.min(4*t+0.5,-4*t+2.5);return 'rgb('+((255*Math.max(0,Math.min(1,r)))|0)+','+((255*Math.max(0,Math.min(1,g)))|0)+','+((255*Math.max(0,Math.min(1,b)))|0)+')';}
function ticks(lo,hi){var r=(hi-lo)/6,p=Math.pow(10,Math.floor(Math.log(r)/Math.LN10)),n=r/p,st=(n<1.5?1:n<3?2:n<7?5:10)*p,t=[],v=Math.ceil(lo/st-1e-9)*st;for(;v<=hi+1e-9;v+=st)t.push(v);return t;}
// Gouraud REAL por triangulo (FaceColor='interp' de MATLAB): color interpolado por
// coordenadas baricentricas = suma de 3 gradientes lineales (uno por vertice, del pie de su
// altura -negro- al vertice -su color-) compuestos con 'lighter'. UN relleno interpolado,
// NO subdividir. vgrad: eje del gradiente = altura del vertice A sobre la arista opuesta BC.
function vgrad(ax,ay,ac,bx,by,cx,cy,mx,my,mw,mh){
 var dx=cx-bx,dy=cy-by,L=dx*dx+dy*dy||1,t=((ax-bx)*dx+(ay-by)*dy)/L,fx=bx+t*dx,fy=by+t*dy;
 var g=ctx.createLinearGradient(fx,fy,ax,ay);g.addColorStop(0,'rgb(0,0,0)');g.addColorStop(1,'rgb('+ac+')');
 ctx.fillStyle=g;ctx.fillRect(mx,my,mw,mh);}
function gour(p,vc){
 var x0=TX(p[0]),y0=TY(p[1]),x1=TX(p[2]),y1=TY(p[3]),x2=TX(p[4]),y2=TY(p[5]);
 var mnx=Math.min(x0,x1,x2),mny=Math.min(y0,y1,y2),mw=Math.max(x0,x1,x2)-mnx,mh=Math.max(y0,y1,y2)-mny;
 ctx.save();ctx.beginPath();ctx.moveTo(x0,y0);ctx.lineTo(x1,y1);ctx.lineTo(x2,y2);ctx.closePath();ctx.clip();
 ctx.fillStyle='#000';ctx.fillRect(mnx,mny,mw,mh);
 var pc=ctx.globalCompositeOperation;ctx.globalCompositeOperation='lighter';
 vgrad(x0,y0,vc[0],x1,y1,x2,y2,mnx,mny,mw,mh);
 vgrad(x1,y1,vc[1],x2,y2,x0,y0,mnx,mny,mw,mh);
 vgrad(x2,y2,vc[2],x0,y0,x1,y1,mnx,mny,mw,mh);
 ctx.globalCompositeOperation=pc;ctx.restore();}
function draw(cx,cy){ctx.clearRect(0,0,W,H);if(cx!=null){ctx.strokeStyle='rgba(0,0,0,.5)';ctx.lineWidth=.6;ctx.beginPath();ctx.moveTo(cx,0);ctx.lineTo(cx,H);ctx.moveTo(0,cy);ctx.lineTo(W,cy);ctx.stroke();}}
draw();
function pip(px,py,p){var ins=false,n=p.length/2;for(var i=0,j=n-1;i<n;j=i++){var xi=p[2*i],yi=p[2*i+1],xj=p[2*j],yj=p[2*j+1];if(((yi>py)!=(yj>py))&&(px<(xj-xi)*(py-yi)/(yj-yi)+xi))ins=!ins;}return ins;}
");
            if (hoverOn)
            {
                // Nombre+unidad del eje desde xlabel/ylabel reales (antes hardcodeado 'x…mm / y…mm').
                (string, string) AxisTip(string lbl, string def)
                {
                    if (string.IsNullOrWhiteSpace(lbl)) return (def, "");
                    int b = lbl.IndexOf('['); if (b < 0) return (lbl.Trim(), "");
                    int e = lbl.IndexOf(']', b);
                    return (lbl.Substring(0, b).Trim(), e > b ? lbl.Substring(b + 1, e - b - 1).Trim() : "");
                }
                var (xtn, xtu) = AxisTip(_figXLabel, "x"); var (ytn, ytu) = AxisTip(_figYLabel, "y");
                sb.Append($"var XTN='{EscapeJs(xtn)}',XTU='{EscapeJs(xtu)}',YTN='{EscapeJs(ytn)}',YTU='{EscapeJs(ytu)}';\n");
                sb.Append(@"cv.addEventListener('mousemove',function(ev){var r=cv.getBoundingClientRect();var mx=ev.clientX-r.left,my=ev.clientY-r.top;
 draw(mx,my);
 var dx=MX0+(mx-MOX)/MSX,dy=MY0+(MH-MOY-my)/MSY,hit=-1;
 for(var k=0;k<P.length;k++){if(pip(dx,dy,P[k][0])){hit=k;break;}}
 if(hit<0){tt.style.display='none';return;}
 var e=P[hit],t=XTN+' = '+dx.toFixed(2)+(XTU?' '+XTU:'')+'\n'+YTN+' = '+dy.toFixed(2)+(YTU?' '+YTU:'');
 if(HL&&e.length>3){for(var q=0;q<HL.length;q++)t+='\n'+HL[q]+' = '+e[3][q].toPrecision(4);}else t+='\nvalor = '+e[2].toFixed(3);
 tt.textContent=t;
 tt.style.display='block';var tx=mx+14;if(tx+120>W)tx=mx-125;tt.style.left=tx+'px';tt.style.top=Math.max(2,my-10)+'px';});
cv.addEventListener('mouseleave',function(){draw();tt.style.display='none';});
");
            }
            sb.Append("})();</script>\n");
            if (!keepState)
            {
                _figTraces = null; _figAnnotations = null; _figPrims = null;
                _hoverVals = null; _hoverLabels = null;
            }
            return sb.ToString();
        }

        // ── ANIMACIÓN: render del frame ACTUAL de la malla (id de canvas fijo "anim", sin
        // resetear el estado) para que drawnow lo emita y el WebView2 lo repinte en sitio. ──
        public static string RenderFrame()
        {
            // _liveFast: la malla se dibuja PLANA por cara (sin subdividir) -> frame barato.
            // try/catch: el estado de figura es estatico; si una re-ejecucion (AutoRun) toca
            // _figPrims a la vez, la enumeracion podria fallar ("Collection was modified") — en
            // ese caso SALTAMOS el frame (return null) en vez de tumbar el solver entero.
            bool prev = _liveFast;
            try
            {
                _liveFast = true;
                if (RetainedActive) BuildRetainedFaces();   // reconstruye la malla desde el estado RETENIDO (mutado por set) = modo MATLAB
                if (_figPrims == null) return null;
                // ── Ruta WebGL retenida (=HG2+ANGLE de MATLAB): la malla vive en GPU; el primer
                //    frame emite canvas+shaders, los siguientes solo datos (bufferSubData+draw). ──
                if (UseGlAnim && (!HeadlessNoHover || System.Environment.GetEnvironmentVariable("HK_GL_HEADLESS") == "1"))
                {
                    var gm = GlFieldMesh();
                    if (gm != null)
                    {
                        if (!_glInited) { _glInited = true; return BuildGlInit(760, 560, gm); }
                        return "\x02GLDATA\x02" + BuildGlData(gm);
                    }
                }
                return RenderInteractiveMesh(760, 560, "anim", keepState: true);
            }
            catch { return null; }
            finally { _liveFast = prev; }
        }

        // Malla retenida coloreada (el campo animado): la ultima con CData/Verts/Faces validos.
        private static RetMesh GlFieldMesh()
        {
            for (int i = _retList.Count - 1; i >= 0; i--)
            {
                var m = _retList[i];
                if (m.CData != null && m.Verts != null && m.Faces != null &&
                    m.Verts.Length >= 3 && m.Faces.Length >= 1 && m.Verts[0].Length >= 2) return m;
            }
            return null;
        }

        // Rango de color (caxis o datos) para normalizar CData -> [0,1].
        private static void GlColorRange(RetMesh gm, out double clo, out double chi)
        {
            if (TryGetCAxis(out clo, out chi) && chi > clo) return;
            clo = double.MaxValue; chi = double.MinValue;
            foreach (var v in gm.CData) { if (v < clo) clo = v; if (v > chi) chi = v; }
            if (chi <= clo) chi = clo + 1;
        }

        // Datos por-frame: posiciones (deformadas) y valores normalizados de los nodos + titulo.
        private static string BuildGlData(RetMesh gm)
        {
            GlColorRange(gm, out double clo, out double chi); double rng = chi - clo;
            var p = new StringBuilder("["); var v = new StringBuilder("["); var vr = new StringBuilder("[");
            for (int i = 0; i < gm.Verts.Length; i++)
            {
                if (i > 0) { p.Append(','); v.Append(','); vr.Append(','); }
                p.Append(gm.Verts[i][0].ToString("0.####", Inv)).Append(',').Append(gm.Verts[i][1].ToString("0.####", Inv));
                double raw = (i < gm.CData.Length) ? gm.CData[i] : 0;
                double val = (i < gm.CData.Length) ? (raw - clo) / rng : 0;
                v.Append(val.ToString("0.####", Inv));
                vr.Append(raw.ToString("0.####", Inv));   // valor CRUDO (magnitud real) para el hover
            }
            p.Append(']'); v.Append(']'); vr.Append(']');
            // multi-campo por frame (hoverdata): hv[vertice][campo], para el hover d_x/d_z/d_res/...
            string hvJs = "null";
            if (_hoverVals != null && _hoverVals.Length >= gm.Verts.Length)
            {
                var hv = new StringBuilder("[");
                for (int i = 0; i < gm.Verts.Length; i++)
                {
                    if (i > 0) hv.Append(',');
                    hv.Append('[');
                    var row = _hoverVals[i] ?? System.Array.Empty<double>();
                    for (int c = 0; c < row.Length; c++) { if (c > 0) hv.Append(','); hv.Append(row[c].ToString("0.####", Inv)); }
                    hv.Append(']');
                }
                hv.Append(']'); hvJs = hv.ToString();
            }
            return "{\"p\":" + p + ",\"v\":" + v + ",\"vr\":" + vr + ",\"hv\":" + hvJs + ",\"t\":\"" + EscapeJs(_figTitle) + "\"}";
        }

        // Primer frame: canvas WebGL + shaders + malla (indices, posiciones, valores) + textura jet.
        // Fondo = PNG SIN el relleno del campo (ejes/colorbar/lineas/carga/ancla); la GPU pinta el campo.
        private static string BuildGlInit(int width, int height, RetMesh gm)
        {
            // 1) fondo (ejes+colorbar+lineas+texto, SIN campo) y transform dato->pixel (_pm...).
            //    El titulo NO se hornea en el fondo (quedaria congelado en el 1er frame); lo muestra
            //    el div 'gltitle' de arriba, que se actualiza en cada __hkGL.
            string bg = ""; string savedTitle = _figTitle;
            SkipFieldFill = true; _figTitle = "";
            try { var b = RasterizeFigurePng(width, height, 2); if (b != null && b.Length > 0) bg = System.Convert.ToBase64String(b); }
            catch { }
            finally { SkipFieldFill = false; _figTitle = savedTitle; }
            double MOX = _pmOX, MOY = _pmOY, MSX = _pmSX, MSY = _pmSY, MX0 = _pmX0, MY0 = _pmY0, MH = _pmH;
            // 2) indices de triangulos (0-based) desde Faces
            var idx = new StringBuilder("[");
            int ntri = 0;
            for (int f = 0; f < gm.Faces.Length; f++)
            {
                var fc = gm.Faces[f]; if (fc.Length < 3) continue;
                if (ntri > 0) idx.Append(',');
                idx.Append((int)fc[0] - 1).Append(',').Append((int)fc[1] - 1).Append(',').Append((int)fc[2] - 1);
                ntri++;
            }
            idx.Append(']');
            // 3) posiciones y valores iniciales
            GlColorRange(gm, out double clo, out double chi); double rng = chi - clo;
            var p0 = new StringBuilder("["); var v0 = new StringBuilder("["); var vr0 = new StringBuilder("[");
            for (int i = 0; i < gm.Verts.Length; i++)
            {
                if (i > 0) { p0.Append(','); v0.Append(','); vr0.Append(','); }
                p0.Append(gm.Verts[i][0].ToString("0.####", Inv)).Append(',').Append(gm.Verts[i][1].ToString("0.####", Inv));
                double raw = (i < gm.CData.Length) ? gm.CData[i] : 0;
                double val = (i < gm.CData.Length) ? (raw - clo) / rng : 0;
                v0.Append(val.ToString("0.####", Inv));
                vr0.Append(raw.ToString("0.####", Inv));   // valor CRUDO por nodo -> hover muestra magnitud real
            }
            p0.Append(']'); v0.Append(']'); vr0.Append(']');
            // 4) textura jet (256 RGB) del colormap activo
            var jet = new StringBuilder("[");
            for (int i = 0; i < 256; i++)
            {
                var c = CmapF(_figCmapName, i / 255.0);
                if (i > 0) jet.Append(',');
                jet.Append((int)Math.Round(255 * Math.Max(0, Math.Min(1, c[0])))).Append(',')
                   .Append((int)Math.Round(255 * Math.Max(0, Math.Min(1, c[1])))).Append(',')
                   .Append((int)Math.Round(255 * Math.Max(0, Math.Min(1, c[2]))));
            }
            jet.Append(']');
            bool edgeOn = gm.Edge != null && gm.Edge != "none";
            var sb = new StringBuilder();
            sb.Append("<div style=\"display:inline-block;font-family:sans-serif\">");
            sb.Append($"<div id=\"gltitle\" style=\"text-align:center;font-size:14px;margin:3px\">{TexToHtml(_figTitle)}</div>");
            sb.Append($"<div style=\"position:relative;width:{width}px;height:{height}px\">");
            sb.Append($"<img src=\"data:image/png;base64,{bg}\" style=\"width:{width}px;height:{height}px;display:block;border:1px solid #ccc\"/>");
            sb.Append($"<canvas id=\"glc\" width=\"{width * 2}\" height=\"{height * 2}\" style=\"position:absolute;top:0;left:0;width:{width}px;height:{height}px;pointer-events:none\"></canvas>");
            // Overlay TRANSPARENTE que SÍ recibe el mouse (glc esta en pointer-events:none) -> hover.
            sb.Append($"<canvas id=\"glh\" width=\"{width}\" height=\"{height}\" style=\"position:absolute;top:0;left:0;width:{width}px;height:{height}px;pointer-events:auto;cursor:crosshair\"></canvas>");
            sb.Append("<div id=\"gltip\" style=\"position:fixed;pointer-events:none;background:rgba(20,20,28,.9);color:#fff;font:12px Consolas;padding:3px 7px;border-radius:4px;display:none;z-index:99999;white-space:pre\"></div>");
            // MULTI-CAMPO para el hover (hoverdata): HV[vertice][campo] + HLBLS. Si el script llamo
            // hoverdata([d_x d_z d_res ...], 'd_x|d_z|d_res|...') se muestran TODOS los campos por
            // punto; si no, HV=null y el hover cae al campo unico coloreado (VR).
            string hvJs = "null", hlblsJs = "null";
            if (_hoverVals != null && _hoverLabels != null && _hoverVals.Length >= gm.Verts.Length)
            {
                var hv = new StringBuilder("[");
                for (int i = 0; i < gm.Verts.Length; i++)
                {
                    if (i > 0) hv.Append(',');
                    hv.Append('[');
                    var row = _hoverVals[i] ?? System.Array.Empty<double>();
                    for (int c = 0; c < row.Length; c++) { if (c > 0) hv.Append(','); hv.Append(row[c].ToString("0.####", Inv)); }
                    hv.Append(']');
                }
                hv.Append(']'); hvJs = hv.ToString();
                var lb = new StringBuilder("[");
                for (int i = 0; i < _hoverLabels.Length; i++) { if (i > 0) lb.Append(','); lb.Append('"').Append(EscapeJs(_hoverLabels[i])).Append('"'); }
                lb.Append(']'); hlblsJs = lb.ToString();
            }
            sb.Append("</div></div>\n<script>(function(){\n");
            sb.Append($"var W={width},H={height},NT={ntri};\n");
            sb.Append($"var IDX={idx},P={p0},V={v0},VR={vr0},JET={jet};\n");
            sb.Append($"var HLBL=\"{EscapeJs(_cbLabel ?? "valor")}\",HV={hvJs},HLBLS={hlblsJs};\n");   // HLBL=campo unico; HV/HLBLS=multi-campo (hoverdata)
            sb.Append($"var UT=[{MOX.ToString("0.#####", Inv)},{MOY.ToString("0.#####", Inv)},{MSX.ToString("0.######", Inv)},{MSY.ToString("0.######", Inv)},{MX0.ToString("0.#####", Inv)},{MY0.ToString("0.#####", Inv)},{MH.ToString("0.###", Inv)}];\n");
            sb.Append(@"var cv=document.getElementById('glc');var gl=cv.getContext('webgl')||cv.getContext('experimental-webgl');
if(!gl){window.__hkGL=function(){};return;}
function sh(t,s){var o=gl.createShader(t);gl.shaderSource(o,s);gl.compileShader(o);return o;}
var vs=sh(gl.VERTEX_SHADER,'attribute vec2 aPos;attribute float aVal;uniform vec2 uRes;uniform float uT[7];varying float vV;void main(){float px=uT[0]+(aPos.x-uT[4])*uT[2];float py=uT[6]-uT[1]-(aPos.y-uT[5])*uT[3];gl_Position=vec4(2.0*px/uRes.x-1.0,1.0-2.0*py/uRes.y,0.0,1.0);vV=aVal;}');
var fs=sh(gl.FRAGMENT_SHADER,'precision mediump float;varying float vV;uniform sampler2D uJet;void main(){gl_FragColor=texture2D(uJet,vec2(clamp(vV,0.0,1.0),0.5));}');
var pr=gl.createProgram();gl.attachShader(pr,vs);gl.attachShader(pr,fs);gl.linkProgram(pr);gl.useProgram(pr);
var aPos=gl.getAttribLocation(pr,'aPos'),aVal=gl.getAttribLocation(pr,'aVal');
var uRes=gl.getUniformLocation(pr,'uRes'),uT=gl.getUniformLocation(pr,'uT'),uJet=gl.getUniformLocation(pr,'uJet');
var pB=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,pB);gl.bufferData(gl.ARRAY_BUFFER,new Float32Array(P),gl.DYNAMIC_DRAW);
var vB=gl.createBuffer();gl.bindBuffer(gl.ARRAY_BUFFER,vB);gl.bufferData(gl.ARRAY_BUFFER,new Float32Array(V),gl.DYNAMIC_DRAW);
var iB=gl.createBuffer();gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER,iB);gl.bufferData(gl.ELEMENT_ARRAY_BUFFER,new Uint16Array(IDX),gl.STATIC_DRAW);
var tex=gl.createTexture();gl.bindTexture(gl.TEXTURE_2D,tex);gl.texImage2D(gl.TEXTURE_2D,0,gl.RGB,256,1,0,gl.RGB,gl.UNSIGNED_BYTE,new Uint8Array(JET));
gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MIN_FILTER,gl.LINEAR);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_MAG_FILTER,gl.LINEAR);
gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_S,gl.CLAMP_TO_EDGE);gl.texParameteri(gl.TEXTURE_2D,gl.TEXTURE_WRAP_T,gl.CLAMP_TO_EDGE);
function draw(){gl.viewport(0,0,cv.width,cv.height);gl.clearColor(0,0,0,0);gl.clear(gl.COLOR_BUFFER_BIT);gl.useProgram(pr);
gl.uniform2f(uRes,W,H);gl.uniform1fv(uT,UT);gl.activeTexture(gl.TEXTURE0);gl.bindTexture(gl.TEXTURE_2D,tex);gl.uniform1i(uJet,0);
gl.bindBuffer(gl.ARRAY_BUFFER,pB);gl.enableVertexAttribArray(aPos);gl.vertexAttribPointer(aPos,2,gl.FLOAT,false,0,0);
gl.bindBuffer(gl.ARRAY_BUFFER,vB);gl.enableVertexAttribArray(aVal);gl.vertexAttribPointer(aVal,1,gl.FLOAT,false,0,0);
gl.bindBuffer(gl.ELEMENT_ARRAY_BUFFER,iB);gl.drawElements(gl.TRIANGLES,NT*3,gl.UNSIGNED_SHORT,0);}
window.__hkGL=function(js){var d=(typeof js==='string')?JSON.parse(js):js;
gl.bindBuffer(gl.ARRAY_BUFFER,pB);gl.bufferSubData(gl.ARRAY_BUFFER,0,new Float32Array(d.p));
gl.bindBuffer(gl.ARRAY_BUFFER,vB);gl.bufferSubData(gl.ARRAY_BUFFER,0,new Float32Array(d.v));
if(d.p)P=d.p;if(d.vr)VR=d.vr;if(d.hv)HV=d.hv;
if(d.t){var e=document.getElementById('gltitle');if(e)e.textContent=d.t;}draw();};
draw();
// ── HOVER: overlay 'glh' recibe el mouse; invierte el transform dato->pixel (UT), busca el
//    triangulo bajo el cursor e interpola el valor CRUDO (VR) -> muestra x, z y el campo. ──
var hc=document.getElementById('glh'),htx=hc.getContext('2d'),htip=document.getElementById('gltip');
hc.addEventListener('mousemove',function(ev){
 var r=hc.getBoundingClientRect();var mx=(ev.clientX-r.left)*W/r.width,my=(ev.clientY-r.top)*H/r.height;
 var dx=(mx-UT[0])/UT[2]+UT[4],dy=(UT[6]-UT[1]-my)/UT[3]+UT[5];var hit=null;
 for(var t=0;t<NT;t++){var a=IDX[3*t],b=IDX[3*t+1],c=IDX[3*t+2];
  var ax=P[2*a],ay=P[2*a+1],bx=P[2*b],by=P[2*b+1],cx=P[2*c],cy=P[2*c+1];
  var d0=(by-cy)*(ax-cx)+(cx-bx)*(ay-cy);if(Math.abs(d0)<1e-12)continue;
  var l1=((by-cy)*(dx-cx)+(cx-bx)*(dy-cy))/d0,l2=((cy-ay)*(dx-cx)+(ax-cx)*(dy-cy))/d0,l3=1-l1-l2;
  if(l1>=-0.02&&l2>=-0.02&&l3>=-0.02){hit={a:a,b:b,c:c,l1:l1,l2:l2,l3:l3};break;}}
 htx.clearRect(0,0,W,H);
 if(!hit){htip.style.display='none';return;}
 htx.strokeStyle='rgba(0,0,0,.45)';htx.lineWidth=1;htx.beginPath();htx.moveTo(mx,0);htx.lineTo(mx,H);htx.moveTo(0,my);htx.lineTo(W,my);htx.stroke();
 var txt='x = '+dx.toFixed(2)+' m\nz = '+dy.toFixed(2)+' m';
 if(HV){for(var j=0;j<HLBLS.length;j++){var vj=hit.l1*HV[hit.a][j]+hit.l2*HV[hit.b][j]+hit.l3*HV[hit.c][j];txt+='\n'+HLBLS[j]+' = '+vj.toFixed(2);}}
 else{var val=hit.l1*VR[hit.a]+hit.l2*VR[hit.b]+hit.l3*VR[hit.c];txt+='\n'+HLBL+' = '+val.toFixed(2);}
 htip.style.display='block';htip.style.left=(ev.clientX+13)+'px';htip.style.top=(ev.clientY+8)+'px';
 htip.textContent=txt;});
hc.addEventListener('mouseleave',function(){htip.style.display='none';htx.clearRect(0,0,W,H);});
");
            sb.Append("})();</script>\n");
            return sb.ToString();
        }

        // Quita las caras de la malla (patch2d con Val) de _figPrims, para que set(...) la reconstruya.
        public static void RemoveMeshFaces()
        {
            if (_figPrims != null) _figPrims.RemoveAll(p => p.Kind == "patch2d" && !double.IsNaN(p.Val));
        }
        // ── MALLA RETENIDA (modo MATLAB): fuente de verdad. patch la fija, set la muta,
        // y el renderer RECONSTRUYE las caras desde ella en cada frame (como el Handle Graphics
        // de MATLAB: el objeto patch guarda Faces/Vertices/CData y el dibujo se regenera).
        // Es una LISTA: cada patch('Faces','Vertices',...) con hold on AÑADE una malla (p.ej.
        // 2 estratos de suelo con colores distintos) en vez de sobrescribir la anterior. ──
        private class RetMesh
        {
            public double[][] Faces, Verts;   // faces: idx de vértice (1-based) por cara; verts: [x,y]
            public double[] CData;            // valor por cara (null = color sólido)
            public string Edge = "black", Face = "lightblue";
            public double Alpha = 1, Lw = 1;
        }
        private static readonly System.Collections.Generic.List<RetMesh> _retList = new System.Collections.Generic.List<RetMesh>();
        // Devuelve el índice de la malla añadida (para que el handle de patch pueda mutar ESA malla con set()).
        public static int SetRetainedMesh(double[][] faces, double[][] verts, double[] cdata, string edge, string face, double alpha, double lw)
        {
            _retList.Add(new RetMesh { Faces = faces, Verts = verts, CData = cdata, Edge = edge, Face = face, Alpha = alpha, Lw = lw });
            _glInited = false;   // malla nueva -> re-inicializar la escena WebGL en el proximo frame
            return _retList.Count - 1;
        }
        public static bool RetainedActive => _retList.Count > 0;
        // Muta por índice de handle (set(h,'Vertices',...)); idx<0 = la más reciente (animación de 1 mesh).
        public static void UpdateRetainedVerts(int idx, double[][] verts)
        {
            if (verts == null || _retList.Count == 0) return;
            if (idx < 0 || idx >= _retList.Count) idx = _retList.Count - 1;
            _retList[idx].Verts = verts;
        }
        public static void UpdateRetainedCData(int idx, double[] cdata)
        {
            if (cdata == null || _retList.Count == 0) return;
            if (idx < 0 || idx >= _retList.Count) idx = _retList.Count - 1;
            _retList[idx].CData = cdata;
        }
        public static void UpdateRetainedVerts(double[][] verts) => UpdateRetainedVerts(-1, verts);
        public static void UpdateRetainedCData(double[] cdata) => UpdateRetainedCData(-1, cdata);
        public static void ResetRetainedMesh() { _retList.Clear(); }
        // Reconstruye las caras de TODAS las mallas retenidas en _figPrims (llamado antes de renderizar).
        public static void BuildRetainedFaces()
        {
            if (_figPrims == null || _retList.Count == 0) return;
            RemoveMeshFaces();
            int firstNew = _figPrims.Count;   // z-order: las caras se re-dibujan al fondo (como MATLAB:
                                              // el patch va DETRÁS de líneas/texto graficados después)
            foreach (var m in _retList)
            {
                if (m.Faces == null || m.Verts == null) continue;
                // CData válido si es POR-VÉRTICE (length = nVerts) o POR-CARA (length = nFaces).
                // BUG antiguo: `>= nFaces` fallaba cuando hay MÁS caras que vértices (p.ej. un T6
                // sub-triangulado en 4 -> 1148 caras vs 815 vértices) -> se iba a NEGRO. MATLAB
                // acepta FaceVertexCData por-vértice sin importar el nº de caras.
                bool hasC = m.CData != null &&
                            (m.CData.Length == m.Verts.Length || m.CData.Length == m.Faces.Length);
                double clo = 0, chi = 1;
                if (hasC && !TryGetCAxis(out clo, out chi))
                {
                    clo = double.MaxValue; chi = double.MinValue;
                    foreach (var v in m.CData) { if (v < clo) clo = v; if (v > chi) chi = v; }
                    if (chi <= clo) chi = clo + 1;
                }
                // CData por VERTICE (length = nVerts) o por CARA (length = nFaces).
                bool perVertex = m.CData != null && m.CData.Length == m.Verts.Length;
                for (int f = 0; f < m.Faces.Length; f++)
                {
                    var face = m.Faces[f]; int nv = face.Length;
                    var xs = new double[nv]; var ys = new double[nv]; var cv = new double[nv];
                    for (int k = 0; k < nv; k++)
                    {
                        int vi = (int)System.Math.Round(face[k]) - 1;
                        if (vi < 0) vi = 0; else if (vi >= m.Verts.Length) vi = m.Verts.Length - 1;
                        xs[k] = m.Verts[vi][0]; ys[k] = m.Verts[vi][1];
                        if (perVertex) cv[k] = m.CData[vi];
                    }
                    if (!hasC) { Patch2D(xs, ys, m.Face, m.Edge, m.Alpha, m.Lw, double.NaN); continue; }
                    if (perVertex && nv == 3 && _figBandN > 0)
                        // BANDAS estilo GEO5 (isosuperficie): cortar el triangulo por los niveles.
                        BandFillTri(xs, ys, cv, clo, chi, _figBandN, m.Alpha, m.Edge);
                    else if (perVertex && nv == 3)
                    {
                        // FaceColor='interp' de MATLAB: color POR VÉRTICE, UNA cara que el canvas
                        // interpola (Gouraud REAL). NO subdividimos (subdividir = "legos" o 73k caras).
                        var vcols = new[] {
                            CmapRgb((cv[0]-clo)/(chi-clo)),
                            CmapRgb((cv[1]-clo)/(chi-clo)),
                            CmapRgb((cv[2]-clo)/(chi-clo))
                        };
                        var vvals = new[] {                          // valor normalizado por vértice
                            (float)((cv[0]-clo)/(chi-clo)),
                            (float)((cv[1]-clo)/(chi-clo)),
                            (float)((cv[2]-clo)/(chi-clo))
                        };
                        double avg = (cv[0]+cv[1]+cv[2]) / 3.0;
                        Patch2DGouraud(xs, ys, vcols, vvals, CmapCss((avg-clo)/(chi-clo)), m.Edge, m.Alpha, m.Lw, avg);
                    }
                    else
                    {
                        double val = perVertex ? (cv[0] + cv[1] + cv[2]) / nv : m.CData[f];
                        string fc = CmapCss((val - clo) / (chi - clo));   // colormap del usuario, no jet
                        Patch2D(xs, ys, fc, m.Edge, m.Alpha, m.Lw, val);
                    }
                }
            }
            // Mover las caras recién dibujadas al FONDO (índice 0) para que líneas/texto que el
            // script graficó DESPUÉS del patch (p.ej. el contorno rojo del talud) queden ENCIMA,
            // como en MATLAB. Sin esto, al reconstruir la malla retenida en FinishFigure se
            // re-añaden al final y taparían la línea.
            int added = _figPrims.Count - firstNew;
            if (added > 0 && firstNew > 0)
            {
                var faces = _figPrims.GetRange(firstNew, added);
                _figPrims.RemoveRange(firstNew, added);
                _figPrims.InsertRange(0, faces);
            }
        }

        /// <summary>Color CSS del colormap ACTIVO (custom/parula/jet...) para t en [0,1].</summary>
        private static string CmapCss(double t)
        {
            var rgb = CmapF(_figCmapName, t);
            return $"rgb({(int)(rgb[0] * 255)},{(int)(rgb[1] * 255)},{(int)(rgb[2] * 255)})";
        }
        /// <summary>Color "r,g,b" (sin 'rgb(...)') del colormap ACTIVO para t en [0,1] — para los
        /// gradientes de Gouraud en el canvas.</summary>
        private static string CmapRgb(double t)
        {
            var rgb = CmapF(_figCmapName, t);
            return $"{(int)(rgb[0] * 255)},{(int)(rgb[1] * 255)},{(int)(rgb[2] * 255)}";
        }
        /// <summary>Bandas de color estilo GEO5 (isosuperficie) sobre UN triángulo: lo corta por
        /// las isolíneas de los N niveles (marching triangle) y rellena cada franja [La,Lb) con el
        /// color del nivel. Bordes = isolíneas rectas por triángulo → contorno suave como GEO5.</summary>
        private static void BandFillTri(double[] xs, double[] ys, double[] cv,
                                        double clo, double chi, int nb, double alpha, string edge)
        {
            if (chi <= clo) chi = clo + 1;
            // niveles: explícitos (GEO5) o equiespaciados en [clo,chi]
            double[] lev;
            if (_figBandLevels != null && _figBandLevels.Length >= 2) lev = _figBandLevels;
            else { if (nb < 1) nb = 1; lev = new double[nb + 1]; for (int k = 0; k <= nb; k++) lev[k] = clo + (chi - clo) * k / nb; }
            for (int b = 0; b < lev.Length - 1; b++)
            {
                double La = lev[b], Lb = lev[b + 1];
                bool top = (b == lev.Length - 2);
                double[] tx = (double[])xs.Clone(), ty = (double[])ys.Clone(), tv = (double[])cv.Clone();
                ClipHalf(ref tx, ref ty, ref tv, La, true);                 // v >= La
                if (tx.Length < 3) continue;
                if (!top) { ClipHalf(ref tx, ref ty, ref tv, Lb, false); if (tx.Length < 3) continue; }  // v <= Lb
                double mid = (La + Lb) / 2.0;                               // color del nivel (colormap por caxis)
                string fc = CmapCss((mid - clo) / (chi - clo));
                Patch2D(tx, ty, fc, edge == "none" ? fc : edge, alpha, 0.0, mid);
            }
        }
        /// <summary>Recorte de un polígono convexo por el semiplano {v>=t} (ge=true) o {v<=t}
        /// (Sutherland-Hodgman sobre el campo escalar lineal del triángulo).</summary>
        private static void ClipHalf(ref double[] X, ref double[] Y, ref double[] V, double t, bool ge)
        {
            int n = X.Length;
            var ox = new System.Collections.Generic.List<double>(n + 2);
            var oy = new System.Collections.Generic.List<double>(n + 2);
            var ov = new System.Collections.Generic.List<double>(n + 2);
            for (int i = 0; i < n; i++)
            {
                int j = (i + 1) % n;
                double vi = V[i], vj = V[j];
                bool ini = ge ? vi >= t : vi <= t;
                bool inj = ge ? vj >= t : vj <= t;
                if (ini) { ox.Add(X[i]); oy.Add(Y[i]); ov.Add(vi); }
                if (ini != inj)
                {
                    double a = (t - vi) / (vj - vi);
                    ox.Add(X[i] + a * (X[j] - X[i]));
                    oy.Add(Y[i] + a * (Y[j] - Y[i]));
                    ov.Add(t);
                }
            }
            X = ox.ToArray(); Y = oy.ToArray(); V = ov.ToArray();
        }
        /// <summary>Triángulo con color POR VÉRTICE (FaceColor='interp' de MATLAB): guarda los 3
        /// colores para que el canvas los interpole (Gouraud real), en vez de subdividir. FaceColor
        /// = promedio (fallback plano para el PNG/SVG). No añade traza Plotly (287 trazas sería pesado
        /// y estas caras se dibujan por el canvas interactivo / SkiaSharp desde _figPrims).</summary>
        private static void Patch2DGouraud(double[] xs, double[] ys, string[] vertCols,
                                           float[] vertVals, string avgColor, string edge, double alpha, double lw, double val)
        {
            if (_figPrims == null) _figPrims = new System.Collections.Generic.List<FigPrim>();
            _figPrims.Add(new FigPrim {
                Kind = "patch2d", Xs = (double[])xs.Clone(), Ys = (double[])ys.Clone(),
                FaceColor = avgColor, EdgeColor = edge, FaceAlpha = alpha, LineWidth = lw, Val = val,
                VertCols = vertCols, VertVals = vertVals
            });
        }
        /// <summary>Gouraud aproximado en SVG: subdivide el triangulo (a,b,c) con valores nodales
        /// en 4 hasta `depth` niveles; cada sub-triangulo se rellena plano con el color del
        /// PROMEDIO de sus 3 nodos via el colormap activo. depth=3 → 64 sub-caras (suave).</summary>
        private static void SubTri(double xa,double ya,double ca, double xb,double yb,double cb,
                                   double xc,double yc,double cc, double clo,double chi,
                                   string edge,double alpha,double lw,int depth)
        {
            if (depth <= 0)
            {
                double val=(ca+cb+cc)/3.0;
                string fc = CmapCss((val-clo)/(chi-clo));
                // relleno PURO: borde del MISMO color (cubre la costura anti-alias) y SIN linea
                // gruesa -> superficie continua sin textura de sub-triangulos (Gouraud aprox).
                Patch2D(new[]{xa,xb,xc}, new[]{ya,yb,yc}, fc, fc, alpha, 0.0, val);
                return;
            }
            double xab=(xa+xb)/2,yab=(ya+yb)/2,cab=(ca+cb)/2;
            double xbc=(xb+xc)/2,ybc=(yb+yc)/2,cbc=(cb+cc)/2;
            double xca=(xc+xa)/2,yca=(yc+ya)/2,cca=(cc+ca)/2;
            SubTri(xa,ya,ca, xab,yab,cab, xca,yca,cca, clo,chi, edge,alpha,lw, depth-1);
            SubTri(xab,yab,cab, xb,yb,cb, xbc,ybc,cbc, clo,chi, edge,alpha,lw, depth-1);
            SubTri(xca,yca,cca, xbc,ybc,cbc, xc,yc,cc, clo,chi, edge,alpha,lw, depth-1);
            SubTri(xab,yab,cab, xbc,ybc,cbc, xca,yca,cca, clo,chi, edge,alpha,lw, depth-1);
        }

        private static SKColor ParseColor(string s)
        {
            if (string.IsNullOrEmpty(s) || s == "none") return SKColors.Transparent;
            if (s[0] == '#' && s.Length >= 7)
            {
                try { return new SKColor(Convert.ToByte(s.Substring(1, 2), 16), Convert.ToByte(s.Substring(3, 2), 16), Convert.ToByte(s.Substring(5, 2), 16)); }
                catch { return SKColors.Black; }
            }
            if (s.StartsWith("rgb", System.StringComparison.OrdinalIgnoreCase))
            {
                int p = s.IndexOf('('), q = s.IndexOf(')');
                if (p >= 0 && q > p)
                {
                    var parts = s.Substring(p + 1, q - p - 1).Split(',');
                    if (parts.Length >= 3 &&
                        byte.TryParse(parts[0].Trim(), out byte r) &&
                        byte.TryParse(parts[1].Trim(), out byte g) &&
                        byte.TryParse(parts[2].Trim(), out byte b))
                        return new SKColor(r, g, b);
                }
            }
            switch (s.ToLowerInvariant())
            {
                case "r": case "red": return SKColors.Red;
                case "g": case "green": return new SKColor(0, 128, 0);
                case "b": case "blue": return SKColors.Blue;
                case "k": case "black": return SKColors.Black;
                case "w": case "white": return SKColors.White;
                case "y": case "yellow": return SKColors.Gold;
                case "m": case "magenta": return SKColors.Magenta;
                case "c": case "cyan": return SKColors.DarkCyan;
                default: return SKColors.Black;
            }
        }

        private static string EscapeXml(string s)
        {
            if (string.IsNullOrEmpty(s)) return "";
            return s.Replace("&","&amp;").Replace("<","&lt;").Replace(">","&gt;").Replace("'","&apos;").Replace("\"","&quot;");
        }

        /// <summary>Genera el <div> Plotly para una superficie 3D.</summary>
        public static string Surf(MValue X, MValue Y, MValue Z, string colormap = "parula", string title = "surf")
        {
            ValidateGrid(X, Y, Z);
            int id = ++_plotCounter;
            var sb = new StringBuilder();
            sb.Append($"<div id=\"matlab_plot_{id}\" class=\"matlab-plot\" style=\"{Plot3dDivStyle()}\"></div>\n");
            sb.Append("<script>(function() {\n");
            sb.Append($"  var data = [{{\n");
            sb.Append($"    type: 'surface', colorscale: {ColorscaleJs(colormap)}, reversescale: {(ColormapReversed(colormap) ? "true" : "false")}, showscale: false,\n");
            sb.Append($"    contours: {{ x:{{show:true,color:'rgba(120,120,120,0.35)',width:1}}, y:{{show:true,color:'rgba(120,120,120,0.35)',width:1}} }},\n");
            sb.Append($"    x: {EmitMatrixJs(X)},\n");
            sb.Append($"    y: {EmitMatrixJs(Y)},\n");
            sb.Append($"    z: {EmitMatrixJs(Z)}\n");
            sb.Append($"  }}];\n");
            sb.Append($"  var layout = {{ title:{{text:'{title}',font:{{size:14}}}}, margin: {{l:18,r:14,t:34,b:24}}, {Scene3DJs()} }};\n");
            sb.Append($"  Plotly.newPlot('matlab_plot_{id}', data, layout, {{responsive:true}});\n");
            sb.Append("})();</script>\n");
            if (TryBufferPanel(sb.ToString())) return "";   // subplot → cae en su celda del grid
            return sb.ToString();
        }

        /// <summary>surf() COMPUESTO: agrega un trace 'surface' a la figura abierta (misma escena 3D
        /// que los patch/mesh3d), para que un script con hold on componga columna+placa+pernos+... en
        /// UNA escena (como MATLAB). Soporta color por C (FaceColor interp), color sólido (FaceColor rgb),
        /// transparencia (FaceAlpha) y sin aristas (EdgeColor none). Si no hay figura, abre una.</summary>
        public static void AddSurf3D(MValue X, MValue Y, MValue Z, MValue C, string colormap,
            string faceColorMode, string faceColorCss, double faceAlpha, string edgeColor)
        {
            if (_figTraces == null) BeginFigure();
            ValidateGrid(X, Y, Z);
            var sb = new StringBuilder();
            sb.Append("{type:'surface'");
            sb.Append($", x:{EmitMatrixJs(X)}, y:{EmitMatrixJs(Y)}, z:{EmitMatrixJs(Z)}");
            sb.Append($", opacity:{faceAlpha.ToString(Inv)}");
            sb.Append(", showscale:false");
            if (faceColorMode == "uniform" && faceColorCss != null)
            {
                // Color SÓLIDO (FaceColor [r g b]): surfacecolor constante + colorscale de un color.
                sb.Append($", surfacecolor:{ConstMatrixJs(Z.Rows, Z.Cols)}");
                sb.Append($", cmin:0, cmax:1, colorscale:[[0,'{faceColorCss}'],[1,'{faceColorCss}']]");
            }
            else if (C != null)
            {
                // Color por C (FaceColor 'interp' con datos, p.ej. von Mises de la columna).
                sb.Append($", surfacecolor:{EmitMatrixJs(C)}");
                sb.Append($", colorscale:{ColorscaleJs(colormap)}, reversescale:{(ColormapReversed(colormap) ? "true" : "false")}");
            }
            else
            {
                // Color por Z (default).
                sb.Append($", colorscale:{ColorscaleJs(colormap)}, reversescale:{(ColormapReversed(colormap) ? "true" : "false")}");
            }
            // EdgeColor none → sin retícula de contorno sobre la superficie.
            if (edgeColor == "none")
                sb.Append(", contours:{x:{highlight:false},y:{highlight:false},z:{highlight:false}}");
            sb.Append(", lighting:{ambient:0.6, diffuse:0.8, specular:0.2, roughness:0.5}");
            sb.Append(", lightposition:{x:200, y:200, z:200}");
            sb.Append("}");
            AddTrace(sb.ToString());
            _figIs3D = true;
            // --- geometría para el renderer CANVAS (mismos quads, coloreados en C#) ---
            int R = Z.Rows, Cc = Z.Cols;
            if (R > 1 && Cc > 1 && X.Rows == R && X.Cols == Cc && Y.Rows == R && Y.Cols == Cc)
            {
                float[] solid = (faceColorMode == "uniform" && faceColorCss != null) ? CssToRgbF(faceColorCss) : null;
                MValue col = C ?? Z;
                double cmin, cmax;
                if (solid == null && _caxisMin.HasValue) { cmin = _caxisMin.Value; cmax = _caxisMax.Value; }
                else { cmin = double.MaxValue; cmax = double.MinValue; for (int t = 0; t < col.Data.Length; t++) { if (col.Data[t] < cmin) cmin = col.Data[t]; if (col.Data[t] > cmax) cmax = col.Data[t]; } }
                double rng = (cmax - cmin) > 1e-12 ? cmax - cmin : 1;
                float[] Col(int i, int j) => solid ?? CmapF(colormap, (col.At(i, j) - cmin) / rng);
                for (int i = 0; i < R - 1; i++)
                    for (int j = 0; j < Cc - 1; j++)
                    {
                        float[] ca = Col(i, j), cb = Col(i, j + 1), cc = Col(i + 1, j + 1), cd = Col(i + 1, j);
                        CvTri(X.At(i, j), Y.At(i, j), Z.At(i, j), ca,
                              X.At(i, j + 1), Y.At(i, j + 1), Z.At(i, j + 1), cb,
                              X.At(i + 1, j + 1), Y.At(i + 1, j + 1), Z.At(i + 1, j + 1), cc, faceAlpha);
                        CvTri(X.At(i, j), Y.At(i, j), Z.At(i, j), ca,
                              X.At(i + 1, j + 1), Y.At(i + 1, j + 1), Z.At(i + 1, j + 1), cc,
                              X.At(i + 1, j), Y.At(i + 1, j), Z.At(i + 1, j), cd, faceAlpha);
                    }
            }
        }

        /// <summary>Polígono 3D plano (patch con XData/YData/ZData) compuesto en la escena — arandelas,
        /// tuercas y discos de los pernos. Triangula en abanico desde el vértice 0.</summary>
        public static void Patch3DPolygon(double[] x, double[] y, double[] z, string faceColor, string edgeColor, double alpha)
        {
            if (_figTraces == null) BeginFigure();
            int n = x.Length;
            if (n < 3) return;
            // Broadcast de ZData escalar (p.ej. 'ZData', zTop) a la longitud del polígono.
            if (z.Length == 1 && n > 1) { var zb = new double[n]; for (int t = 0; t < n; t++) zb[t] = z[0]; z = zb; }
            if (z.Length < n) return;
            var iArr = new int[n - 2]; var jArr = new int[n - 2]; var kArr = new int[n - 2];
            for (int f = 0; f < n - 2; f++) { iArr[f] = 0; jArr[f] = f + 1; kArr[f] = f + 2; }
            var sb = new StringBuilder();
            sb.Append("{type:'mesh3d'");
            sb.Append($", x:[{Csv(x)}], y:[{Csv(y)}], z:[{Csv(z)}]");
            sb.Append($", i:[{IntCsv(iArr)}], j:[{IntCsv(jArr)}], k:[{IntCsv(kArr)}]");
            sb.Append($", opacity:{alpha.ToString(Inv)}");
            sb.Append($", color:'{faceColor}'");
            sb.Append(", lighting:{ambient:0.6, diffuse:0.8, specular:0.2, roughness:0.5}");
            sb.Append("}");
            AddTrace(sb.ToString());
            _figIs3D = true;
            // --- geometría CANVAS: abanico de triángulos de color sólido ---
            float[] fc = CssToRgbF(faceColor);
            for (int f = 0; f < n - 2; f++)
                CvTri(x[0], y[0], z[0], fc, x[f + 1], y[f + 1], z[f + 1], fc, x[f + 2], y[f + 2], z[f + 2], fc, alpha);
        }

        /// <summary>Matriz JS rows×cols de ceros (para surfacecolor de color sólido).</summary>
        private static string ConstMatrixJs(int rows, int cols)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < rows; i++)
            {
                if (i > 0) sb.Append(',');
                sb.Append('[');
                for (int j = 0; j < cols; j++) { if (j > 0) sb.Append(','); sb.Append('0'); }
                sb.Append(']');
            }
            sb.Append(']');
            return sb.ToString();
        }

        /// <summary>Contour filled — heatmap 2D con isolíneas.</summary>
        public static string Contourf(MValue X, MValue Y, MValue Z, int nLevels = 10, string colormap = "viridis")
        {
            ValidateGrid(X, Y, Z);
            int id = ++_plotCounter;
            // Niveles EXACTOS entre min y max (MATLAB reparte nLevels uniformemente;
            // Plotly con solo 'ncontours' elige numeros redondos y sale mas grueso).
            double zmin = double.PositiveInfinity, zmax = double.NegativeInfinity;
            foreach (var v in Z.Data) { if (double.IsNaN(v)) continue; if (v < zmin) zmin = v; if (v > zmax) zmax = v; }
            if (_caxisMin.HasValue) { zmin = _caxisMin.Value; zmax = _caxisMax.Value; }   // honrar caxis([lo hi])
            if (!(zmax > zmin)) zmax = zmin + 1;
            int nl = Math.Max(2, nLevels);
            double step = (zmax - zmin) / nl;
            string cs(double d) => d.ToString("R", Inv);
            var sb = new StringBuilder();
            sb.Append($"<div id=\"matlab_plot_{id}\" class=\"matlab-plot\" style=\"{Plot3dDivStyle()}\"></div>\n");
            sb.Append("<script>(function() {\n");
            sb.Append($"  var data = [{{\n");
            sb.Append($"    type: 'contour', colorscale: {ColorscaleJs(colormap)}, reversescale: {(ColormapReversed(colormap) ? "true" : "false")}, autocontour: false, contours: {{coloring: 'fill', start: {cs(zmin)}, end: {cs(zmax)}, size: {cs(step)}}}, line: {{width: 0.4, color: 'rgba(0,0,0,0.12)'}},\n");
            sb.Append($"    x: {EmitRowJs(X, true)},\n");
            sb.Append($"    y: {EmitColJs(Y)},\n");
            sb.Append($"    z: {EmitMatrixJs(Z)}\n");
            sb.Append($"  }}];\n");
            sb.Append($"  var layout = {{ title: 'contourf', margin:{{l:40,r:40,t:40,b:40}} }};\n");
            sb.Append($"  Plotly.newPlot('matlab_plot_{id}', data, layout, {{responsive:true}});\n");
            sb.Append("})();</script>\n");
            return sb.ToString();
        }

        public static string Imagesc(MValue Z, string colormap = "viridis")
        {
            int id = ++_plotCounter;
            var sb = new StringBuilder();
            sb.Append($"<div id=\"matlab_plot_{id}\" class=\"matlab-plot\" style=\"{Plot3dDivStyle()}\"></div>\n");
            sb.Append("<script>(function() {\n");
            sb.Append($"  var data = [{{ type: 'heatmap', colorscale: {ColorscaleJs(colormap)}, reversescale: {(ColormapReversed(colormap) ? "true" : "false")}, z: {EmitMatrixJs(Z)} }}];\n");
            sb.Append($"  var layout = {{ title: 'imagesc', margin:{{l:40,r:40,t:40,b:40}}, yaxis: {{autorange:'reversed'}} }};\n");
            sb.Append($"  Plotly.newPlot('matlab_plot_{id}', data, layout, {{responsive:true}});\n");
            sb.Append("})();</script>\n");
            return sb.ToString();
        }

        public static string Plot(MValue X, MValue Y, string label = null)
        {
            int id = ++_plotCounter;
            var sb = new StringBuilder();
            sb.Append($"<div id=\"matlab_plot_{id}\" class=\"matlab-plot\" style=\"width:640px;height:400px\"></div>\n");
            sb.Append("<script>(function() {\n");
            sb.Append($"  var data = [{{ type: 'scatter', mode: 'lines',\n");
            sb.Append($"    x: {EmitVecJs(X)}, y: {EmitVecJs(Y)} }}];\n");
            sb.Append($"  var layout = {{ title: '{label ?? "plot"}', margin:{{l:50,r:30,t:40,b:50}} }};\n");
            sb.Append($"  Plotly.newPlot('matlab_plot_{id}', data, layout, {{responsive:true}});\n");
            sb.Append("})();</script>\n");
            return sb.ToString();
        }

        /// <summary>Viewer interactivo de campo sobre losa: 2D (canvas jet + crosshair hover)
        /// + 3D (Three.js, malla deformada coloreada + hover) con selector de resultado.
        /// Lo GENERA el script Lab (no HTML crudo): los campos vienen del .m.</summary>
        public static string SlabView3D(double A, double B, double H, int na, int nb,
            System.Collections.Generic.List<(string key, string label, string unit, double[] data)> fields)
        {
            int id = ++_plotCounter;
            var ci = System.Globalization.CultureInfo.InvariantCulture;
            var opts = new StringBuilder(); var dinit = new StringBuilder();
            var uinit = new StringBuilder(); var ddata = new StringBuilder();
            for (int f = 0; f < fields.Count; f++)
            {
                var fl = fields[f];
                opts.Append($"<option value=\"{fl.key}\">{fl.label}</option>");
                if (f > 0) { dinit.Append(','); uinit.Append(','); }
                dinit.Append(fl.key).Append(":[]");
                uinit.Append(fl.key).Append(":'").Append(fl.unit).Append('\'');
                ddata.Append("D.").Append(fl.key).Append("=[");
                for (int k = 0; k < fl.data.Length; k++) { if (k > 0) ddata.Append(','); ddata.Append(fl.data[k].ToString("0.#####", ci)); }
                ddata.Append("];");
            }
            return SlabViewerTemplate
                .Replace("__ID__", id.ToString(ci))
                .Replace("__NA__", na.ToString(ci)).Replace("__NB__", nb.ToString(ci))
                .Replace("__A__", A.ToString(ci)).Replace("__BB__", B.ToString(ci)).Replace("__HC__", H.ToString(ci))
                .Replace("__OPTIONS__", opts.ToString())
                .Replace("__DINIT__", dinit.ToString()).Replace("__UINIT__", uinit.ToString())
                .Replace("__DDATA__", ddata.ToString())
                .Replace("__FIRSTKEY__", fields.Count > 0 ? fields[0].key : "w");
        }

        // Template del viewer (JS interactivo 2D canvas jet + 3D Three.js, con hover y selector).
        // Placeholders: __ID__ __NA__ __NB__ __A__ __BB__ __HC__ __OPTIONS__ __DINIT__ __UINIT__ __DDATA__ __FIRSTKEY__
        private const string SlabViewerTemplate =
@"<div id=""rv__ID__"" style=""font:13px Segoe UI""> <b>Resultado:</b> <select id=""rvs__ID__"" style=""font:13px Segoe UI;padding:2px 6px;margin:4px 0"">__OPTIONS__</select> <div style=""display:flex;flex-wrap:wrap;gap:10px""><div id=""rv2__ID__""></div><div id=""rv3__ID__""></div></div></div> <script>(function(){ var na=__NA__,nb=__NB__,A=__A__,Bb=__BB__,Hc=__HC__; var D={__DINIT__},U={__UINIT__}; __DDATA__ var nx=na+1,ny=nb+1; function jt(t){t=Math.max(0,Math.min(1,t));return[Math.max(0,Math.min(1,Math.min(4*t-1.5,-4*t+4.5)))*255|0,Math.max(0,Math.min(1,Math.min(4*t-0.5,-4*t+3.5)))*255|0,Math.max(0,Math.min(1,Math.min(4*t+0.5,-4*t+2.5)))*255|0];} function jc(t){var c=jt(t);return new THREE.Color(c[0]/255,c[1]/255,c[2]/255);} var P2=document.getElementById(""rv2__ID__""),P3=document.getElementById(""rv3__ID__""),SEL=document.getElementById(""rvs__ID__""); var tip=document.createElement(""div"");tip.style.cssText=""position:fixed;pointer-events:none;background:rgba(20,20,28,.9);color:#fff;font:12px Consolas;padding:3px 7px;border-radius:4px;display:none;z-index:99999"";document.body.appendChild(tip); function draw2D(g,uni){var W=420,H=380,ml=38,mr=64,mt=16,pw=W-ml-mr,ph=H-mt-26;P2.innerHTML='';var hd=document.createElement(""div"");var wr=document.createElement(""div"");wr.style.cssText=""position:relative;width:""+W+""px;height:""+H+""px;flex:0 0 auto"";var bs=document.createElement(""canvas"");bs.width=W;bs.height=H;bs.style.cssText=""position:absolute;border:1px solid #ddd"";var ov=document.createElement(""canvas"");ov.width=W;ov.height=H;ov.style.cssText=""position:absolute;pointer-events:none"";wr.appendChild(bs);wr.appendChild(ov);P2.appendChild(hd);P2.appendChild(wr);var cx=bs.getContext(""2d""),ox=ov.getContext(""2d""); var xs=[];for(var i=0;i<nx;i++)xs.push(i*A/na);var ys=[];for(var j=0;j<ny;j++)ys.push(j*Bb/nb);function gv(i,j){return g[i*ny+j];} function SX(x){return ml+x/A*pw;}function SY(y){return mt+(Bb-y)/Bb*ph;}function wX(p){return(p-ml)/pw*A;}function wY(p){return Bb-(p-mt)/ph*Bb;} function bl(x,y){if(x<0||x>A||y<0||y>Bb)return null;var i=0;while(i<nx-2&&xs[i+1]<x)i++;var j=0;while(j<ny-2&&ys[j+1]<y)j++;var u=(x-xs[i])/(xs[i+1]-xs[i]),v=(y-ys[j])/(ys[j+1]-ys[j]);return gv(i,j)*(1-u)*(1-v)+gv(i+1,j)*u*(1-v)+gv(i,j+1)*(1-u)*v+gv(i+1,j+1)*u*v;} var vn=1e30,vx=-1e30;for(var k=0;k<g.length;k++){if(g[k]<vn)vn=g[k];if(g[k]>vx)vx=g[k];}if(vx-vn<1e-9)vx=vn+1; var im=cx.createImageData(pw,ph),dd=im.data;for(var py=0;py<ph;py++)for(var px=0;px<pw;px++){var v=bl(wX(ml+px),wY(mt+py)),qq=(py*pw+px)*4;if(v==null){dd[qq+3]=0;}else{var c=jt((v-vn)/(vx-vn));dd[qq]=c[0];dd[qq+1]=c[1];dd[qq+2]=c[2];dd[qq+3]=255;}}cx.putImageData(im,ml,mt); cx.strokeStyle=""rgba(40,40,40,.25)"";for(var i=0;i<nx;i++){cx.beginPath();cx.moveTo(SX(xs[i]),mt);cx.lineTo(SX(xs[i]),mt+ph);cx.stroke();}for(var j=0;j<ny;j++){cx.beginPath();cx.moveTo(ml,SY(ys[j]));cx.lineTo(ml+pw,SY(ys[j]));cx.stroke();}cx.strokeStyle=""#888"";cx.strokeRect(ml,mt,pw,ph); var cbx=W-mr+20;cx.font=""10px Consolas"";for(var k=0;k<ph;k++){var c=jt(1-k/ph);cx.fillStyle=""rgb(""+c[0]+"",""+c[1]+"",""+c[2]+"")"";cx.fillRect(cbx,mt+k,13,1);}cx.fillStyle=""#333"";cx.fillText(vx.toFixed(2),cbx-2,mt-3);cx.fillText(vn.toFixed(2),cbx-2,mt+ph+10); hd.innerHTML=""<b>2D (planta)</b> max=""+vx.toFixed(2)+uni+"" min=""+vn.toFixed(2)+uni; bs.onmousemove=function(ev){var rc=bs.getBoundingClientRect();var px=ev.clientX-rc.left,py=ev.clientY-rc.top,x=wX(px),y=wY(py);var v=(px>=ml&&px<=ml+pw&&py>=mt&&py<=mt+ph)?bl(x,y):null;ox.clearRect(0,0,W,H);if(v==null)return;ox.strokeStyle=""#000"";ox.beginPath();ox.moveTo(px,mt);ox.lineTo(px,mt+ph);ox.moveTo(ml,py);ox.lineTo(ml+pw,py);ox.stroke();ox.fillStyle=""rgba(20,20,28,.9)"";ox.fillRect(px+8,py-15,140,15);ox.fillStyle=""#fff"";ox.font=""11px Consolas"";ox.fillText(v.toFixed(2)+uni+"" @(""+x.toFixed(1)+"",""+y.toFixed(1)+"")"",px+11,py-4);};bs.onmouseleave=function(){ox.clearRect(0,0,W,H);};} var scn,cam,ren,ctrl,grp,mesh,geo,vv,rdy=false; function init3D(){var W=440,H=400;scn=new THREE.Scene();scn.background=new THREE.Color(0xeef0f4);cam=new THREE.PerspectiveCamera(45,W/H,.001,9000);ren=new THREE.WebGLRenderer({antialias:true,preserveDrawingBuffer:true});ren.setSize(W,H);var hd=document.createElement(""div"");hd.id=""rv3h__ID__"";P3.appendChild(hd);P3.appendChild(ren.domElement);cam.up.set(0,0,1);var dg0=Math.hypot(A,Bb,Hc)||1;cam.position.set(A/2+dg0,Bb/2-dg0*1.4,Hc/2+dg0);cam.lookAt(A/2,Bb/2,Hc/2);ctrl=new THREE.OrbitControls(cam,ren.domElement);ctrl.target.set(A/2,Bb/2,Hc/2);ctrl.update();scn.add(new THREE.AmbientLight(0xffffff,.9));var dl=new THREE.DirectionalLight(0xffffff,.5);dl.position.set(8,-12,18);scn.add(dl);var ray=new THREE.Raycaster(),mo=new THREE.Vector2();ren.domElement.addEventListener(""mousemove"",function(ev){if(!mesh)return;var r=ren.domElement.getBoundingClientRect();mo.x=((ev.clientX-r.left)/r.width)*2-1;mo.y=-((ev.clientY-r.top)/r.height)*2+1;ray.setFromCamera(mo,cam);var h=ray.intersectObject(mesh,false);if(h.length){var f=h[0].face,ap=geo.attributes.position,p0=new THREE.Vector3().fromBufferAttribute(ap,f.a),p1=new THREE.Vector3().fromBufferAttribute(ap,f.b),p2=new THREE.Vector3().fromBufferAttribute(ap,f.c),bc=new THREE.Vector3();new THREE.Triangle(p0,p1,p2).getBarycoord(h[0].point,bc);var val=bc.x*vv[f.a]+bc.y*vv[f.b]+bc.z*vv[f.c];tip.style.display=""block"";tip.style.left=(ev.clientX+13)+""px"";tip.style.top=(ev.clientY+8)+""px"";tip.innerHTML=val.toFixed(2);}else tip.style.display=""none"";});ren.domElement.addEventListener(""mouseleave"",function(){tip.style.display=""none"";});function anim(){requestAnimationFrame(anim);ctrl.update();ren.render(scn,cam);}anim();rdy=true;} function build3D(colorG,uni){if(!rdy)return;if(grp)scn.remove(grp);grp=new THREE.Group(); var wG=D.w;function wv(i,j){return wG[i*ny+j];}function cg(i,j){return colorG[i*ny+j];} var wn=Math.min.apply(null,wG),wx=Math.max.apply(null,wG);var wa=Math.max(Math.abs(wn),Math.abs(wx),1e-9);var ampw=(.40*Hc)/wa; function Pt(i,j){return new THREE.Vector3(i*A/na,j*Bb/nb,Hc+wv(i,j)*ampw);} var cn=Math.min.apply(null,colorG),cx2=Math.max.apply(null,colorG);if(cx2-cn<1e-9)cx2=cn+1; var pos=[],col=[];vv=[];function pv(p,t){pos.push(p.x,p.y,p.z);var c=jc(t);col.push(c.r,c.g,c.b);} for(var i=0;i<nx-1;i++)for(var j=0;j<ny-1;j++){var pa=Pt(i,j),pb=Pt(i+1,j),pc=Pt(i+1,j+1),pd=Pt(i,j+1),ta=(cg(i,j)-cn)/(cx2-cn),tb=(cg(i+1,j)-cn)/(cx2-cn),tc=(cg(i+1,j+1)-cn)/(cx2-cn),td=(cg(i,j+1)-cn)/(cx2-cn);pv(pa,ta);pv(pb,tb);pv(pc,tc);vv.push(cg(i,j),cg(i+1,j),cg(i+1,j+1));pv(pa,ta);pv(pc,tc);pv(pd,td);vv.push(cg(i,j),cg(i+1,j+1),cg(i,j+1));} geo=new THREE.BufferGeometry();geo.setAttribute(""position"",new THREE.Float32BufferAttribute(pos,3));geo.setAttribute(""color"",new THREE.Float32BufferAttribute(col,3));geo.computeVertexNormals();mesh=new THREE.Mesh(geo,new THREE.MeshBasicMaterial({vertexColors:true,side:THREE.DoubleSide}));grp.add(mesh); var wp=[];for(var i=0;i<nx;i++)for(var j=0;j<ny-1;j++){var aa=Pt(i,j),bb=Pt(i,j+1);wp.push(aa.x,aa.y,aa.z,bb.x,bb.y,bb.z);}for(var j=0;j<ny;j++)for(var i=0;i<nx-1;i++){var aa=Pt(i,j),bb=Pt(i+1,j);wp.push(aa.x,aa.y,aa.z,bb.x,bb.y,bb.z);}var wg=new THREE.BufferGeometry();wg.setAttribute(""position"",new THREE.Float32BufferAttribute(wp,3));grp.add(new THREE.LineSegments(wg,new THREE.LineBasicMaterial({color:0x556677}))); var corn=[[0,0],[nx-1,0],[nx-1,ny-1],[0,ny-1]]; var cp=[];for(var k=0;k<4;k++){var ci=corn[k][0],cj=corn[k][1],ptop=Pt(ci,cj);cp.push(ci*A/na,cj*Bb/nb,0,ptop.x,ptop.y,ptop.z);}var cgeo=new THREE.BufferGeometry();cgeo.setAttribute(""position"",new THREE.Float32BufferAttribute(cp,3));grp.add(new THREE.LineSegments(cgeo,new THREE.LineBasicMaterial({color:0x222222}))); var bp=[];function edge(ii,jj,di,dj,n){for(var s=0;s<n;s++){var a1=Pt(ii+di*s,jj+dj*s),a2=Pt(ii+di*(s+1),jj+dj*(s+1));bp.push(a1.x,a1.y,a1.z,a2.x,a2.y,a2.z);}} edge(0,0,1,0,nx-1);edge(0,ny-1,1,0,nx-1);edge(0,0,0,1,ny-1);edge(nx-1,0,0,1,ny-1);var bgeo=new THREE.BufferGeometry();bgeo.setAttribute(""position"",new THREE.Float32BufferAttribute(bp,3));grp.add(new THREE.LineSegments(bgeo,new THREE.LineBasicMaterial({color:0x8d6e63,linewidth:2}))); for(var k=0;k<4;k++){var ci=corn[k][0]*A/na,cj=corn[k][1]*Bb/nb,cm=new THREE.Mesh(new THREE.ConeGeometry(.05*Math.max(A,Bb),.10*Math.max(A,Bb),4),new THREE.MeshBasicMaterial({color:0x2244aa}));cm.position.set(ci,cj,-.05*Math.max(A,Bb));cm.rotation.x=Math.PI/2;grp.add(cm);} scn.add(grp); var cxx=A/2,cyy=Bb/2,czz=Hc/2,diag=Math.hypot(A,Bb,Hc)||1;cam.up.set(0,0,1);cam.position.set(cxx+diag*1.0,cyy-diag*1.4,czz+diag*.9);cam.lookAt(cxx,cyy,czz);ctrl.target.set(cxx,cyy,czz);ctrl.update(); document.getElementById(""rv3h__ID__"").innerHTML=""<b>Mesa 3D - ""+SEL.options[SEL.selectedIndex].text+""</b> max=""+cx2.toFixed(2)+uni+"" min=""+cn.toFixed(2)+uni+"" (arrastra/zoom/hover)"";} function render(){var k=SEL.value,g=D[k],uni=U[k];draw2D(g,uni);build3D(g,uni);} SEL.onchange=render; var s1=document.createElement(""script"");s1.src=""https://calcpad.local/three-0.145.0.min.js"";s1.onload=function(){var s2=document.createElement(""script"");s2.src=""https://calcpad.local/OrbitControls-0.145.0.js"";s2.onload=function(){init3D();render();};document.head.appendChild(s2);};document.head.appendChild(s1); draw2D(D.__FIRSTKEY__,U.__FIRSTKEY__); })();</script>";

        public static string Plot3(MValue X, MValue Y, MValue Z)
        {
            int id = ++_plotCounter;
            var sb = new StringBuilder();
            sb.Append($"<div id=\"matlab_plot_{id}\" class=\"matlab-plot\" style=\"{Plot3dDivStyle()}\"></div>\n");
            sb.Append("<script>(function() {\n");
            sb.Append($"  var data = [{{ type: 'scatter3d', mode: 'lines',\n");
            sb.Append($"    x: {EmitVecJs(X)}, y: {EmitVecJs(Y)}, z: {EmitVecJs(Z)} }}];\n");
            sb.Append($"  var layout = {{ title:{{text:'plot3',font:{{size:14}}}}, margin:{{l:18,r:14,t:34,b:24}}, {Scene3DJs()} }};\n");
            sb.Append($"  Plotly.newPlot('matlab_plot_{id}', data, layout, {{responsive:true}});\n");
            sb.Append("})();</script>\n");
            if (TryBufferPanel(sb.ToString())) return "";   // subplot → cae en su celda del grid
            return sb.ToString();
        }

        /// <summary>Spy plot — visualiza el pattern de sparsity de una matriz.</summary>
        public static string Spy(MValue M)
        {
            int id = ++_plotCounter;
            int nr = M.Rows, nc = M.Cols;
            var xs = new System.Collections.Generic.List<int>();
            var ys = new System.Collections.Generic.List<int>();
            int nzCount = 0;
            for (int i = 0; i < nr; i++)
                for (int j = 0; j < nc; j++)
                    if (M.At(i, j) != 0) { xs.Add(j + 1); ys.Add(nr - i); nzCount++; }
            var sb = new StringBuilder();
            sb.Append($"<div id=\"matlab_plot_{id}\" class=\"matlab-plot\" style=\"width:520px;height:520px\"></div>\n");
            sb.Append("<script>(function() {\n");
            sb.Append($"  var data = [{{ type: 'scatter', mode: 'markers',\n");
            sb.Append($"    x: [{string.Join(",", xs)}],\n");
            sb.Append($"    y: [{string.Join(",", ys)}],\n");
            sb.Append($"    marker: {{ symbol: 'square', size: 8, color: '#1a4f8a' }} }}];\n");
            sb.Append($"  var layout = {{ title: 'spy ({nr}×{nc}, nnz={nzCount})',\n");
            sb.Append($"    xaxis: {{ range:[0, {nc + 1}], title:'col' }},\n");
            sb.Append($"    yaxis: {{ range:[0, {nr + 1}], title:'row', scaleanchor:'x', scaleratio:1 }},\n");
            sb.Append($"    margin: {{l:50, r:30, t:40, b:50}} }};\n");
            sb.Append($"  Plotly.newPlot('matlab_plot_{id}', data, layout, {{responsive:true}});\n");
            sb.Append("})();</script>\n");
            return sb.ToString();
        }

        public static string Bar(MValue X, MValue Y, bool horizontal = false)
        {
            int id = ++_plotCounter;
            var sb = new StringBuilder();
            sb.Append($"<div id=\"matlab_plot_{id}\" class=\"matlab-plot\" style=\"width:640px;height:400px\"></div>\n");
            sb.Append("<script>(function() {\n");
            sb.Append($"  var data = [{{ type: 'bar', orientation: '{(horizontal?'h':'v')}',\n");
            sb.Append($"    x: {EmitVecJs(horizontal ? Y : X)}, y: {EmitVecJs(horizontal ? X : Y)} }}];\n");
            sb.Append($"  var layout = {{ title: 'bar', margin:{{l:50,r:30,t:40,b:50}} }};\n");
            sb.Append($"  Plotly.newPlot('matlab_plot_{id}', data, layout, {{responsive:true}});\n");
            sb.Append("})();</script>\n");
            return sb.ToString();
        }
        public static string Scatter(MValue X, MValue Y, MValue Size = null, MValue C = null,
                                     string colormap = "parula", bool filled = false, bool showColorbar = false)
        {
            int id = ++_plotCounter;
            int n = X.Data?.Length ?? 0;
            _lastIsMarkerColored = false;
            var sb = new StringBuilder();
            sb.Append($"<div id=\"matlab_plot_{id}\" class=\"matlab-plot\" style=\"width:640px;height:400px\"></div>\n");
            sb.Append("<script>(function() {\n");

            // --- marker ---
            var mk = new StringBuilder("{ ");
            // tamaño: MATLAB da el AREA en puntos^2 -> plotly usa diametro en px (aprox sqrt).
            if (Size != null && Size.Data != null && Size.Data.Length > 1)
                mk.Append($"size: {EmitVecJs(Size)}, ");
            else
            {
                double sArea = (Size != null && Size.Data != null && Size.Data.Length >= 1) ? Size.Data[0] : 36;
                double px = Math.Max(4, Math.Sqrt(Math.Max(1, sArea)));
                mk.Append($"size: {px.ToString("0.##", Inv)}, ");
            }
            // color
            if (C != null && C.Data != null && C.Data.Length == n && n > 1)
            {
                // color POR VALOR -> colorscale + (opcional) barra
                mk.Append($"color: {EmitVecJs(C)}, colorscale: {ColorscaleJs(colormap)}, " +
                          $"reversescale: {(ColormapReversed(colormap) ? "true" : "false")}, " +
                          $"showscale: {(showColorbar ? "true" : "false")}, ");
                _lastIsMarkerColored = true;
            }
            else if (C != null && C.IsString)
                mk.Append($"color: '{JsColor(C.StringValue)}', ");
            else if (C != null && C.Data != null && C.Data.Length == 3)
            {
                int r = (int)Math.Round(Math.Max(0, Math.Min(1, C.Data[0])) * 255);
                int g = (int)Math.Round(Math.Max(0, Math.Min(1, C.Data[1])) * 255);
                int b = (int)Math.Round(Math.Max(0, Math.Min(1, C.Data[2])) * 255);
                mk.Append($"color: 'rgb({r},{g},{b})', ");
            }
            if (!filled) mk.Append("symbol: 'circle-open', ");
            mk.Append("}");

            sb.Append($"  var data = [{{ type: 'scatter', mode: 'markers',\n");
            sb.Append($"    x: {EmitVecJs(X)}, y: {EmitVecJs(Y)}, marker: {mk} }}];\n");
            sb.Append($"  var layout = {{ title: 'scatter', margin:{{l:50,r:30,t:40,b:50}} }};\n");
            sb.Append($"  Plotly.newPlot('matlab_plot_{id}', data, layout, {{responsive:true}});\n");
            sb.Append("})();</script>\n");
            return sb.ToString();
        }
        /// <summary>Colores MATLAB de una letra ('r','g','b','k','y','m','c','w') o nombre a CSS.</summary>
        private static string JsColor(string s)
        {
            switch ((s ?? "").Trim().ToLowerInvariant())
            {
                // RGB EXACTO de MATLAB (igual que MatlabColorToJs del evaluador).
                case "r": case "red":     return "#FF0000";
                case "g": case "green":   return "#00FF00";   // MATLAB 'g' = [0 1 0], no CSS green
                case "b": case "blue":    return "#0000FF";
                case "k": case "black":   return "#000000";
                case "y": case "yellow":  return "#FFFF00";
                case "m": case "magenta": return "#FF00FF";
                case "c": case "cyan":    return "#00FFFF";
                case "w": case "white":   return "#FFFFFF";
                default: return s;
            }
        }
        public static string Scatter3(MValue X, MValue Y, MValue Z)
        {
            int id = ++_plotCounter;
            var sb = new StringBuilder();
            sb.Append($"<div id=\"matlab_plot_{id}\" class=\"matlab-plot\" style=\"{Plot3dDivStyle()}\"></div>\n");
            sb.Append("<script>(function() {\n");
            sb.Append($"  var data = [{{ type: 'scatter3d', mode: 'markers',\n");
            sb.Append($"    x: {EmitVecJs(X)}, y: {EmitVecJs(Y)}, z: {EmitVecJs(Z)} }}];\n");
            sb.Append($"  var layout = {{ title: 'scatter3', margin:{{l:0,r:0,t:40,b:0}} }};\n");
            sb.Append($"  Plotly.newPlot('matlab_plot_{id}', data, layout, {{responsive:true}});\n");
            sb.Append("})();</script>\n");
            if (TryBufferPanel(sb.ToString())) return "";   // subplot → cae en su celda del grid
            return sb.ToString();
        }
        public static string Histogram2D(MValue X, MValue Y, int nBins = 20)
        {
            int id = ++_plotCounter;
            var sb = new StringBuilder();
            sb.Append($"<div id=\"matlab_plot_{id}\" class=\"matlab-plot\" style=\"{Plot3dDivStyle()}\"></div>\n");
            sb.Append("<script>(function() {\n");
            sb.Append($"  var data = [{{ type: 'histogram2d', colorscale: 'Viridis',\n");
            sb.Append($"    x: {EmitVecJs(X)}, y: {EmitVecJs(Y)}, nbinsx: {nBins}, nbinsy: {nBins} }}];\n");
            sb.Append($"  var layout = {{ title: 'histogram2', margin:{{l:50,r:30,t:40,b:50}} }};\n");
            sb.Append($"  Plotly.newPlot('matlab_plot_{id}', data, layout, {{responsive:true}});\n");
            sb.Append("})();</script>\n");
            return sb.ToString();
        }
        public static string Heatmap(MValue Z, string colormap = "viridis")
        {
            int id = ++_plotCounter;
            var sb = new StringBuilder();
            sb.Append($"<div id=\"matlab_plot_{id}\" class=\"matlab-plot\" style=\"{Plot3dDivStyle()}\"></div>\n");
            sb.Append("<script>(function() {\n");
            sb.Append($"  var data = [{{ type: 'heatmap', colorscale: {ColorscaleJs(colormap)}, reversescale: {(ColormapReversed(colormap) ? "true" : "false")}, z: {EmitMatrixJs(Z)} }}];\n");
            sb.Append($"  var layout = {{ title: 'heatmap', margin:{{l:50,r:30,t:40,b:50}} }};\n");
            sb.Append($"  Plotly.newPlot('matlab_plot_{id}', data, layout, {{responsive:true}});\n");
            sb.Append("})();</script>\n");
            return sb.ToString();
        }
        public static string Stem(MValue X, MValue Y)
        {
            int id = ++_plotCounter;
            int n = X.Data.Length;
            var sb = new StringBuilder();
            sb.Append($"<div id=\"matlab_plot_{id}\" class=\"matlab-plot\" style=\"width:640px;height:400px\"></div>\n");
            sb.Append("<script>(function() {\n");
            sb.Append("  var data = [\n");
            // Líneas verticales como segmentos
            sb.Append("    {type:'scatter', mode:'lines', x:[],y:[],line:{color:'#333',width:1},showlegend:false,name:''}");
            for (int i = 0; i < n; i++) { /* puntos uno por uno */ }
            // En vez de bucles complejos, usamos 'stem' approach: scatter + bar
            sb.Append($",\n    {{type:'bar', x: {EmitVecJs(X)}, y: {EmitVecJs(Y)}, width: 0.05, marker:{{color:'#333'}}, name:'stem'}},");
            sb.Append($"\n    {{type:'scatter', mode:'markers', x: {EmitVecJs(X)}, y: {EmitVecJs(Y)}, marker:{{size:8, color:'#1a4f8a'}}, name:'samples'}}");
            sb.Append("\n  ];\n");
            sb.Append("  var layout = { title: 'stem', margin:{l:50,r:30,t:40,b:50} };\n");
            sb.Append($"  Plotly.newPlot('matlab_plot_{id}', data, layout, {{responsive:true}});\n");
            sb.Append("})();</script>\n");
            return sb.ToString();
        }

        public static string Histogram(MValue X, int nBins = 20)
        {
            int id = ++_plotCounter;
            var sb = new StringBuilder();
            sb.Append($"<div id=\"matlab_plot_{id}\" class=\"matlab-plot\" style=\"width:640px;height:400px\"></div>\n");
            sb.Append("<script>(function() {\n");
            sb.Append($"  var data = [{{ type: 'histogram', x: {EmitVecJs(X)}, nbinsx: {nBins} }}];\n");
            sb.Append($"  var layout = {{ title: 'histogram', margin:{{l:50,r:30,t:40,b:50}} }};\n");
            sb.Append($"  Plotly.newPlot('matlab_plot_{id}', data, layout, {{responsive:true}});\n");
            sb.Append("})();</script>\n");
            return sb.ToString();
        }
        public static string Polar(MValue Theta, MValue R)
        {
            int id = ++_plotCounter;
            var sb = new StringBuilder();
            sb.Append($"<div id=\"matlab_plot_{id}\" class=\"matlab-plot\" style=\"width:500px;height:500px\"></div>\n");
            sb.Append("<script>(function() {\n");
            sb.Append($"  var data = [{{ type: 'scatterpolar', mode: 'lines',\n");
            sb.Append($"    theta: [");
            for (int i = 0; i < Theta.Data.Length; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append((Theta.Data[i] * 180.0 / Math.PI).ToString("G6", Inv));
            }
            sb.Append("],\n");
            sb.Append($"    r: {EmitVecJs(R)} }}];\n");
            sb.Append($"  var layout = {{ title: 'polar', margin:{{l:40,r:40,t:40,b:40}} }};\n");
            sb.Append($"  Plotly.newPlot('matlab_plot_{id}', data, layout, {{responsive:true}});\n");
            sb.Append("})();</script>\n");
            return sb.ToString();
        }
        public static string Quiver(MValue X, MValue Y, MValue U, MValue V)
        {
            int id = ++_plotCounter;
            var sb = new StringBuilder();
            sb.Append($"<div id=\"matlab_plot_{id}\" class=\"matlab-plot\" style=\"{Plot3dDivStyle()}\"></div>\n");
            sb.Append("<script>(function() {\n");
            // Plotly no tiene quiver nativo — emulamos con líneas + arrowheads
            sb.Append("  var arrows = []; var lines = [];\n");
            sb.Append($"  var xs = {EmitVecJs(X)}, ys = {EmitVecJs(Y)}, us = {EmitVecJs(U)}, vs = {EmitVecJs(V)};\n");
            sb.Append("  var scale = 0.5;\n");
            sb.Append("  for (var i = 0; i < xs.length; i++) {\n");
            sb.Append("    arrows.push({ x: xs[i]+us[i]*scale, y: ys[i]+vs[i]*scale, ax: xs[i], ay: ys[i],\n");
            sb.Append("                  xref:'x', yref:'y', axref:'x', ayref:'y', showarrow:true, arrowhead:2, arrowsize:1 });\n");
            sb.Append("  }\n");
            sb.Append($"  Plotly.newPlot('matlab_plot_{id}', [{{x:xs, y:ys, mode:'markers', type:'scatter', marker:{{size:2}}}}],\n");
            sb.Append("    { title: 'quiver', annotations: arrows, margin:{l:40,r:40,t:40,b:40} }, {responsive:true});\n");
            sb.Append("})();</script>\n");
            return sb.ToString();
        }

        /// <summary>Bode plot — dos paneles: magnitud (dB) y fase (deg) vs ω en escala log.</summary>
        public static string BodeDual(double[] w, double[] magDb, double[] phaseDeg)
        {
            int id = ++_plotCounter;
            var sb = new StringBuilder();
            sb.Append($"<div id=\"matlab_plot_{id}\" class=\"matlab-plot\" style=\"width:680px;height:560px\"></div>\n");
            sb.Append("<script>(function() {\n");
            sb.Append("  var data = [\n");
            sb.Append($"    {{x: [{string.Join(",", w.Select(x => x.ToString("G6", Inv)))}], y: [{string.Join(",", magDb.Select(x => x.ToString("G6", Inv)))}], type:'scatter', mode:'lines', name:'Magnitude (dB)', xaxis:'x1', yaxis:'y1'}},\n");
            sb.Append($"    {{x: [{string.Join(",", w.Select(x => x.ToString("G6", Inv)))}], y: [{string.Join(",", phaseDeg.Select(x => x.ToString("G6", Inv)))}], type:'scatter', mode:'lines', name:'Phase (deg)', xaxis:'x2', yaxis:'y2'}}\n");
            sb.Append("  ];\n");
            sb.Append("  var layout = {\n");
            sb.Append("    title: 'Bode Diagram',\n");
            sb.Append("    grid: {rows:2, columns:1, pattern:'independent'},\n");
            sb.Append("    xaxis:  {type:'log', title:'ω [rad/s]'},\n");
            sb.Append("    yaxis:  {title:'Magnitude (dB)'},\n");
            sb.Append("    xaxis2: {type:'log', title:'ω [rad/s]'},\n");
            sb.Append("    yaxis2: {title:'Phase (deg)'},\n");
            sb.Append("    margin: {l:60, r:30, t:50, b:50}\n");
            sb.Append("  };\n");
            sb.Append($"  Plotly.newPlot('matlab_plot_{id}', data, layout, {{responsive:true}});\n");
            sb.Append("})();</script>\n");
            return sb.ToString();
        }

        public static string Quiver3(MValue X, MValue Y, MValue Z, MValue U, MValue V, MValue W)
        {
            int id = ++_plotCounter;
            int n = X.Data.Length;
            var sb = new StringBuilder();
            sb.Append($"<div id=\"matlab_plot_{id}\" class=\"matlab-plot\" style=\"{Plot3dDivStyle()}\"></div>\n");
            sb.Append("<script>(function() {\n");
            // Plotly cone trace
            sb.Append($"  var data = [{{ type: 'cone', sizemode: 'scaled', sizeref: 0.5,\n");
            sb.Append($"    x: {EmitVecJs(X)}, y: {EmitVecJs(Y)}, z: {EmitVecJs(Z)},\n");
            sb.Append($"    u: {EmitVecJs(U)}, v: {EmitVecJs(V)}, w: {EmitVecJs(W)} }}];\n");
            sb.Append($"  var layout = {{ title: 'quiver3', margin: {{l:0,r:0,t:40,b:0}} }};\n");
            sb.Append($"  Plotly.newPlot('matlab_plot_{id}', data, layout, {{responsive:true}});\n");
            sb.Append("})();</script>\n");
            return sb.ToString();
        }
        public static string Slice(MValue X, MValue Y, MValue Z, MValue V, double[] xPlanes, double[] yPlanes, double[] zPlanes)
        {
            int id = ++_plotCounter;
            var sb = new StringBuilder();
            sb.Append($"<div id=\"matlab_plot_{id}\" class=\"matlab-plot\" style=\"{Plot3dDivStyle()}\"></div>\n");
            sb.Append("<script>(function() {\n");
            // Plotly isosurface o volume — usamos volume con planos como slices
            sb.Append($"  var data = [{{ type: 'volume',\n");
            sb.Append($"    x: {EmitVecJs(X)}, y: {EmitVecJs(Y)}, z: {EmitVecJs(Z)},\n");
            sb.Append($"    value: {EmitVecJs(V)},\n");
            sb.Append($"    isomin: {V.Data[0].ToString("G", Inv)}, isomax: {V.Data[V.Data.Length - 1].ToString("G", Inv)},\n");
            sb.Append($"    opacity: 0.4, surface_count: 5 }}];\n");
            sb.Append($"  var layout = {{ title: 'slice', margin: {{l:0,r:0,t:40,b:0}} }};\n");
            sb.Append($"  Plotly.newPlot('matlab_plot_{id}', data, layout, {{responsive:true}});\n");
            sb.Append("})();</script>\n");
            return sb.ToString();
        }

        public static MValue Peaks(double[] xVec, double[] yVec)
        {
            int Nx = xVec.Length, Ny = yVec.Length;
            var Z = new MValue(Ny, Nx);
            for (int i = 0; i < Ny; i++)
            {
                double y = yVec[i];
                for (int j = 0; j < Nx; j++)
                {
                    double x = xVec[j];
                    double t1 = 3.0 * (1 - x) * (1 - x) * Math.Exp(-x*x - (y+1)*(y+1));
                    double t2 = -10.0 * (x/5 - x*x*x - Math.Pow(y, 5)) * Math.Exp(-x*x - y*y);
                    double t3 = -(1.0/3.0) * Math.Exp(-(x+1)*(x+1) - y*y);
                    Z.Set(i, j, t1 + t2 + t3);
                }
            }
            return Z;
        }

        public static MValue PeaksFromGrid(MValue X, MValue Y)
        {
            if (X.Rows != Y.Rows || X.Cols != Y.Cols)
                throw new MatlabRuntimeException($"peaks(X,Y): X {X.Rows}×{X.Cols} ≠ Y {Y.Rows}×{Y.Cols}");
            int rows = X.Rows, cols = X.Cols;
            var Z = new MValue(rows, cols);
            for (int i = 0; i < rows; i++)
                for (int j = 0; j < cols; j++)
                {
                    double x = X.At(i, j), y = Y.At(i, j);
                    double t1 = 3.0 * (1 - x) * (1 - x) * Math.Exp(-x*x - (y+1)*(y+1));
                    double t2 = -10.0 * (x/5 - x*x*x - Math.Pow(y, 5)) * Math.Exp(-x*x - y*y);
                    double t3 = -(1.0/3.0) * Math.Exp(-(x+1)*(x+1) - y*y);
                    Z.Set(i, j, t1 + t2 + t3);
                }
            return Z;
        }

        // ─── Helpers ────────────────────────────────────────────────────────
        private static void ValidateGrid(MValue X, MValue Y, MValue Z)
        {
            // MATLAB acepta X/Y como GRID (igual que Z) o como VECTOR de coordenadas
            // (longitud = Cols para X, = Rows para Y). Solo error si no encaja ninguno.
            int xn = X.Rows * X.Cols, yn = Y.Rows * Y.Cols;
            bool xOk = (X.Rows == Z.Rows && X.Cols == Z.Cols) || xn == Z.Cols || xn == Z.Rows;
            bool yOk = (Y.Rows == Z.Rows && Y.Cols == Z.Cols) || yn == Z.Rows || yn == Z.Cols;
            if (!xOk)
                throw new MatlabRuntimeException($"surf/contourf: X {X.Rows}×{X.Cols} ≠ Z {Z.Rows}×{Z.Cols}");
            if (!yOk)
                throw new MatlabRuntimeException($"surf/contourf: Y {Y.Rows}×{Y.Cols} ≠ Z {Z.Rows}×{Z.Cols}");
        }
        private static string EmitMatrixJs(MValue m)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < m.Rows; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append("[");
                for (int j = 0; j < m.Cols; j++)
                {
                    if (j > 0) sb.Append(",");
                    sb.Append(m.At(i, j).ToString("G6", Inv));
                }
                sb.Append("]");
            }
            sb.Append("]");
            return sb.ToString();
        }
        private static string EmitVecJs(MValue v)
        {
            var sb = new StringBuilder("[");
            for (int i = 0; i < v.Data.Length; i++)
            {
                if (i > 0) sb.Append(",");
                sb.Append(v.Data[i].ToString("G6", Inv));
            }
            sb.Append("]");
            return sb.ToString();
        }
        private static string EmitRowJs(MValue m, bool firstRowOnly)
        {
            // Eje x de contourf: primera fila del grid X, o el vector si X es columna.
            var sb = new StringBuilder("[");
            if (m.Cols == 1 && m.Rows > 1)
                for (int i = 0; i < m.Rows; i++) { if (i > 0) sb.Append(","); sb.Append(m.At(i, 0).ToString("G6", Inv)); }
            else
                for (int j = 0; j < m.Cols; j++) { if (j > 0) sb.Append(","); sb.Append(m.At(0, j).ToString("G6", Inv)); }
            sb.Append("]");
            return sb.ToString();
        }
        private static string EmitColJs(MValue m)
        {
            // Eje y de contourf: primera columna, o el vector si Y es fila.
            var sb = new StringBuilder("[");
            if (m.Rows == 1 && m.Cols > 1)
                for (int j = 0; j < m.Cols; j++) { if (j > 0) sb.Append(","); sb.Append(m.At(0, j).ToString("G6", Inv)); }
            else
                for (int i = 0; i < m.Rows; i++) { if (i > 0) sb.Append(","); sb.Append(m.At(i, 0).ToString("G6", Inv)); }
            sb.Append("]");
            return sb.ToString();
        }
        /// <summary>True si el nombre pide colormap INVERTIDO (sufijo `_r`, ej. jet_r — estilo SAP2000/PyVista).</summary>
        private static bool ColormapReversed(string name) =>
            (name ?? "").ToLowerInvariant().EndsWith("_r");

        private static string ColormapToPlotly(string nameRaw)
        {
            var name = (nameRaw ?? "").ToLowerInvariant();
            if (name.EndsWith("_r")) name = name.Substring(0, name.Length - 2);   // jet_r -> jet (+reversescale)
            return ColormapToPlotlyBase(name);
        }

        private static string ColormapToPlotlyBase(string name) => name switch
        {
            "jet" => "Jet",
            "parula" or "viridis" => "Viridis",
            "hot" => "Hot",
            "cool" => "Bluered",
            "gray" or "grey" => "Greys",
            "hsv" => "HSV",
            "bone" => "Greys",
            "spring" => "YlOrRd",
            "summer" => "YlGn",
            "autumn" => "YlOrRd",
            "winter" => "Blues",
            "copper" => "YlOrBr",
            _ => "Viridis"
        };
        /// <summary>Muestra un colormap en t∈[0,1] devolviendo rgb(r,g,b).</summary>
        public static string ColorscaleSampleRgb(string name, double t)
        {
            t = Math.Max(0, Math.Min(1, t));
            (int r, int g, int b) c;
            switch (name?.ToLowerInvariant())
            {
                case "jet":
                    c = JetRgb(t);
                    break;
                case "hot":
                    c = HotRgb(t);
                    break;
                case "gray":
                case "grey":
                    int g0 = (int)(t * 255);
                    c = (g0, g0, g0);
                    break;
                case "parula":
                case "viridis":
                default:
                    c = ViridisRgb(t);
                    break;
            }
            return $"rgb({c.r},{c.g},{c.b})";
        }
        // jet(256) EXACTO de MATLAB (portado de toolbox/matlab/graph3d/jet.m): n=ceil(m/4),
        // rampa u=[(1:n)/n, ones(1,n-1), (n:-1:1)/n], canales R/G/B desplazados ±n. Da
        // azul[0]=0.5156 (no 0.5) — byte-idéntico a MATLAB jet(256), no la fórmula aproximada.
        private static readonly double[][] _jet256 = BuildJetMatlab(256);
        private static double[][] BuildJetMatlab(int m)
        {
            int n = (int)Math.Ceiling(m / 4.0);
            var u = new System.Collections.Generic.List<double>(3 * n);
            for (int i = 1; i <= n; i++) u.Add((double)i / n);
            for (int i = 0; i < n - 1; i++) u.Add(1.0);
            for (int i = n; i >= 1; i--) u.Add((double)i / n);
            int L = u.Count;
            int gbase = (int)Math.Ceiling(n / 2.0) - (m % 4 == 1 ? 1 : 0);
            var J = new double[m][];
            for (int i = 0; i < m; i++) J[i] = new double[3];
            int cg = 0; for (int k = 0; k < L; k++) { int gi = gbase + (k + 1); if (gi >= 1 && gi <= m) { J[gi - 1][1] = u[cg]; cg++; } }
            int cr = 0; for (int k = 0; k < L; k++) { int ri = gbase + (k + 1) + n; if (ri >= 1 && ri <= m) { J[ri - 1][0] = u[cr]; cr++; } }
            var bidx = new System.Collections.Generic.List<int>();
            for (int k = 0; k < L; k++) { int bi = gbase + (k + 1) - n; if (bi >= 1 && bi <= m) bidx.Add(bi); }
            int lb = bidx.Count; for (int k = 0; k < lb; k++) J[bidx[k] - 1][2] = u[L - lb + k];
            return J;
        }
        private static (int, int, int) JetRgb(double t)
        {
            if (t < 0) t = 0; else if (t > 1) t = 1;
            int idx = (int)Math.Round(t * (_jet256.Length - 1));
            if (idx < 0) idx = 0; else if (idx >= _jet256.Length) idx = _jet256.Length - 1;
            var c = _jet256[idx];
            return ((int)Math.Round(c[0] * 255), (int)Math.Round(c[1] * 255), (int)Math.Round(c[2] * 255));
        }
        private static (int, int, int) HotRgb(double t)
        {
            // Hot: black → red → yellow → white
            double r = t < 0.4 ? t / 0.4 : 1;
            double g = t < 0.4 ? 0 : (t < 0.8 ? (t - 0.4) / 0.4 : 1);
            double b = t < 0.8 ? 0 : (t - 0.8) / 0.2;
            return ((int)(r*255), (int)(g*255), (int)(b*255));
        }
        private static (int, int, int) ViridisRgb(double t)
        {
            // Aproximación lineal a Viridis con 4 puntos clave
            var stops = new (double Pos, int R, int G, int B)[]
            {
                (0.0,  68,   1,  84),
                (0.33, 59,  82, 139),
                (0.66, 33, 144, 141),
                (1.0, 253, 231,  37)
            };
            for (int i = 0; i < stops.Length - 1; i++)
            {
                if (t >= stops[i].Pos && t <= stops[i+1].Pos)
                {
                    double tt = (t - stops[i].Pos) / (stops[i+1].Pos - stops[i].Pos);
                    return ((int)(stops[i].R + tt * (stops[i+1].R - stops[i].R)),
                            (int)(stops[i].G + tt * (stops[i+1].G - stops[i].G)),
                            (int)(stops[i].B + tt * (stops[i+1].B - stops[i].B)));
                }
            }
            return (stops[stops.Length-1].R, stops[stops.Length-1].G, stops[stops.Length-1].B);
        }
    }
}
