using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Xml;
using ICSharpCode.AvalonEdit.CodeCompletion;
using ICSharpCode.AvalonEdit.Folding;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace Calcpad.Wpf
{
    /// <summary>
    /// EL EDITOR DE CODIGO DE HEKATAN LAB, sobre AvalonEdit.
    ///
    /// El RichTextBox de siempre SIGUE AHI, oculto, y se le escribe con
    /// <c>SetInputText</c> en cada tecla. Asi todo lo demas de la app —autorun, calcular,
    /// guardar, los botones de insertar, el teclado, MathCanvas— funciona igual que
    /// siempre, sin tocar sus 158 llamadas al RichTextBox. Es el mismo patron que ya usa
    /// MathCanvas para ser un segundo editor (OnMathCanvasTextChanged/_syncingFromMathCanvas).
    ///
    /// Lo que AvalonEdit aporta: CARPETAS PLEGABLES (+/-) en %% , function, for, if,
    /// while, switch, try... como MATLAB. Ver <see cref="MatlabFoldingStrategy"/>.
    ///
    /// La casilla "Plegado" alterna entre este editor y el clasico, para compararlos.
    /// </summary>
    public partial class MainWindow
    {
        private FoldingManager _foldingManager;
        private CompletionWindow _avalonCompletion;
        private readonly MatlabSemanticColorizer _colorizadorSemantico = new();
        private readonly ImagenIncrustadaGenerator _imagenes = new();
        private bool _desdeAvalon;      // el cambio de texto lo origino AvalonEdit
        private bool _haciaAvalon;      // estamos escribiendo EN AvalonEdit desde la app
        private bool _avalonListo;

        /// <summary>Llamar una vez, cuando la ventana ya cargo.</summary>
        private void PrepararAvalon()
        {
            if (_avalonListo) return;
            _avalonListo = true;

            CargarResaltadoMatlab();
            _foldingManager = FoldingManager.Install(AvalonEditor.TextArea);
            AvalonEditor.TextArea.TextView.LineTransformers.Add(_colorizadorSemantico);
            AvalonEditor.TextArea.TextView.ElementGenerators.Add(_imagenes);

            AvalonEditor.TextChanged += AvalonEditor_TextChanged;
            AvalonEditor.TextArea.TextEntered += AvalonEditor_TextEntered;
            AvalonEditor.PreviewKeyDown += AvalonEditor_PreviewKeyDown;
            // Doble clic en el codigo -> el reporte se mueve a esa linea (lo hacia
            // RichTextBox_MouseDoubleClick, que con el editor plegable delante ya no llega).
            AvalonEditor.MouseDoubleClick += AvalonEditor_MouseDoubleClick;
            // La linea del cursor se pinta: asi se VE donde cayo el salto desde el reporte.
            AvalonEditor.Options.HighlightCurrentLine = true;

            AplicarModoEditor();
            SincronizarHaciaAvalon();

            // --plegar: arrancar con todo plegado (para capturar el PNG del plegado cerrado).
            // Con retardo: el archivo entra al editor DESPUES, al final de GetInputTextFromFile.
            if (Environment.GetCommandLineArgs().Any(a => a == "--plegar"))
            {
                var t = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(900) };
                t.Tick += (_, _) => { t.Stop(); PlegarTodo_Click(null, null); };
                t.Start();
            }

            PrepararCapturaAutocompletado();
            PrepararCapturaBusqueda();
            PrepararCapturaInsercion();
            PrepararCapturaMarcado();
            PrepararCapturaSalto();
        }

        /// <summary><c>--insertar &lt;texto&gt; [--cshot &lt;png&gt;]</c>: mete texto por el MISMO camino
        /// que los botones de la barra (InsertManager), para comprobar en PNG que acaba en el
        /// editor plegable y no en el RichTextBox oculto.</summary>
        private void PrepararCapturaInsercion()
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, "--insertar");
            if (i < 0 || i + 1 >= args.Length) return;
            var texto = args[i + 1];
            var iPng = Array.IndexOf(args, "--cshot");
            var png = iPng >= 0 && iPng + 1 < args.Length ? args[iPng + 1] : null;

            TrasUnRato(1400, () =>
            {
                try
                {
                    AvalonEditor.Focus();
                    AvalonEditor.CaretOffset = AvalonEditor.Document.TextLength;
                    _insertManager.InsertLine();
                    _insertManager.InsertText(texto);
                }
                catch { }
                if (png is null) return;
                TrasUnRato(900, () => { CapturarPantalla(png); Environment.Exit(0); });
            });
        }

        /// <summary><c>--buscar &lt;texto&gt; [--cshot &lt;png&gt;]</c>: abre el buscador del editor con
        /// ese texto, para revisar en PNG que resalta las coincidencias.</summary>
        private void PrepararCapturaBusqueda()
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, "--buscar");
            if (i < 0 || i + 1 >= args.Length) return;
            var texto = args[i + 1];
            var iPng = Array.IndexOf(args, "--cshot");
            var png = iPng >= 0 && iPng + 1 < args.Length ? args[iPng + 1] : null;

            TrasUnRato(1400, () =>
            {
                try
                {
                    AvalonEditor.Focus();
                    BuscarEnAvalon(false);
                    if (_panelBuscar is not null) _panelBuscar.SearchPattern = texto;
                }
                catch { }
                if (png is null) return;
                TrasUnRato(900, () => { CapturarPantalla(png); Environment.Exit(0); });
            });
        }

        /// <summary>Ejecutar algo cuando la ventana ya termino de montarse.</summary>
        private void TrasUnRato(int ms, Action accion)
        {
            var t = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(ms) };
            t.Tick += (_, _) => { t.Stop(); accion(); };
            t.Start();
        }

        /// <summary>
        /// <c>--completar &lt;prefijo&gt; [--cshot &lt;png&gt;]</c>: escribe el prefijo al final del
        /// codigo y abre el popup de autocompletado, para poder REVISARLO en un PNG.
        ///
        /// Por que no vale <c>--wshot</c>: el popup de AvalonEdit es OTRA ventana, y --wshot
        /// dibuja solo esta (RenderTargetBitmap/PrintWindow). Para que salga el popup hay que
        /// copiar de la PANTALLA, que es lo que hace <see cref="CapturarPantalla"/>.
        /// </summary>
        private void PrepararCapturaAutocompletado()
        {
            var args = Environment.GetCommandLineArgs();
            var iPre = Array.IndexOf(args, "--completar");
            if (iPre < 0 || iPre + 1 >= args.Length) return;
            var prefijo = args[iPre + 1];
            var iPng = Array.IndexOf(args, "--cshot");
            var png = iPng >= 0 && iPng + 1 < args.Length ? args[iPng + 1] : null;

            var t = new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(1400) };
            t.Tick += (_, _) =>
            {
                t.Stop();
                try
                {
                    WindowState = WindowState.Normal;
                    AvalonEditor.Focus();
                    AvalonEditor.AppendText("\n" + prefijo);
                    AvalonEditor.CaretOffset = AvalonEditor.Document.TextLength;
                    MostrarAutocompletado(prefijo);
                    // --aceptar: ademas, mete el item seleccionado (para ver el SNIPPET ya
                    // insertado con sus huecos, que es lo que hace Tab).
                    if (args.Any(a => a == "--aceptar"))
                        _avalonCompletion?.CompletionList.RequestInsertion(EventArgs.Empty);
                }
                catch { }

                // Junto al PNG, un .defs.txt con lo que UserDefined detecto: si el popup sale
                // vacio hay que poder VER si el problema es la deteccion o el filtro.
                if (png is not null) VolcarDeclaradas(png + ".defs.txt");

                if (png is null) return;
                var t2 = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(900) };
                t2.Tick += (_, _) =>
                {
                    t2.Stop();
                    CapturarPantalla(png);
                    // Salida DURA: Shutdown() dispara el "File not saved. Save?" (el snippet
                    // ensucio el documento) y en headless ese dialogo se queda ahi para siempre.
                    Environment.Exit(0);
                };
                t2.Start();
            };
            t.Start();
        }

        /// <summary>Dibuja esta ventana Y las ventanas hijas que tenga encima (el popup del
        /// autocompletado), pegando cada una en su sitio.
        ///
        /// Se dibuja el ARBOL VISUAL de WPF (RenderTargetBitmap), no la pantalla ni el handle:
        ///   - <c>CopyFromScreen</c> traia lo que hubiera delante en el monitor (otra terminal).
        ///   - <c>PrintWindow</c> depende del escritorio: con la ventana tapada salio en BLANCO.
        /// El arbol visual siempre esta ahi. Lo unico que no sale es el WebView2 (superficie
        /// nativa): el panel Output queda vacio, que es lo esperado en estas capturas.</summary>
        private void CapturarPantalla(string ruta)
        {
            try
            {
                UpdateLayout();
                int w = (int)Math.Ceiling(ActualWidth), h = (int)Math.Ceiling(ActualHeight);
                if (w <= 0 || h <= 0) return;

                var dv = new System.Windows.Media.DrawingVisual();
                using (var dc = dv.RenderOpen())
                {
                    dc.DrawImage(Render(this), new Rect(0, 0, w, h));

                    foreach (Window otra in Application.Current.Windows)
                    {
                        if (ReferenceEquals(otra, this) || !otra.IsVisible) continue;
                        // De pantalla a MIS coordenadas en una sola operacion: mezclar pixeles
                        // fisicos con unidades WPF descoloca el popup con la pantalla al 150 %.
                        var p = PointFromScreen(otra.PointToScreen(new Point(0, 0)));
                        dc.DrawImage(Render(otra), new Rect(p.X, p.Y, otra.ActualWidth, otra.ActualHeight));
                    }
                }

                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(dv);

                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                var dir = System.IO.Path.GetDirectoryName(ruta);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                using var fs = System.IO.File.Create(ruta);
                enc.Save(fs);
            }
            catch { }

            static System.Windows.Media.Imaging.RenderTargetBitmap Render(Window v)
            {
                v.UpdateLayout();
                var b = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    Math.Max(1, (int)Math.Ceiling(v.ActualWidth)),
                    Math.Max(1, (int)Math.Ceiling(v.ActualHeight)),
                    96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                b.Render(v);
                return b;
            }
        }

        // ---------- alternar editor plegable / clasico ----------

        private bool EditorPlegableActivo => EditorPlegableChk?.IsChecked == true;

        private void EditorPlegable_Changed(object sender, RoutedEventArgs e)
        {
            if (!_avalonListo) return;
            AplicarModoEditor();
            if (EditorPlegableActivo) SincronizarHaciaAvalon();
        }

        private void AplicarModoEditor()
        {
            var plegable = EditorPlegableActivo;
            AvalonEditor.Visibility = plegable ? Visibility.Visible : Visibility.Collapsed;
            PlegarTodoBtn.Visibility = plegable ? Visibility.Visible : Visibility.Collapsed;
            DesplegarTodoBtn.Visibility = plegable ? Visibility.Visible : Visibility.Collapsed;
            // El canal de numeros de linea del Lab estorba: AvalonEdit trae el suyo.
            LineNumbers.Visibility = plegable ? Visibility.Collapsed : Visibility.Visible;
            LineNumbersBorder.Visibility = plegable ? Visibility.Collapsed : Visibility.Visible;
            if (plegable) AvalonEditor.Focus();
        }

        // ---------- sincronizacion en los dos sentidos ----------

        /// <summary>AvalonEdit -> RichTextBox oculto. Dispara la cadena normal de la app
        /// (RichTextBox_TextChanged: autorun, resaltado, guardado sucio...).</summary>
        private void AvalonEditor_TextChanged(object sender, EventArgs e)
        {
            if (_haciaAvalon || !EditorPlegableActivo) return;
            _desdeAvalon = true;
            try { SetInputText(AvalonEditor.Text); }
            catch { }
            finally { _desdeAvalon = false; }
            ActualizarPlegado();
            ActualizarSemantica();
            ProgramarAutoRunAvalon();
        }

        // ---------- AutoRun al escribir ----------
        // El AutoRun clasico lo disparaba el RichTextBox al CAMBIAR DE LINEA
        // (RichTextBox_SelectionChanged) o al PERDER EL FOCO. Con AvalonEdit delante, el
        // RichTextBox oculto nunca recibe cursor ni foco: _autoRun quedaba en true y nadie
        // calculaba (instalador 1.3.21: se escribia y el Output se quedaba quieto).
        // Ahora, como Hekatan Fortran y C++: 700 ms despues de la ultima tecla, se calcula.
        private System.Windows.Threading.DispatcherTimer _autoRunAvalonTimer;

        private void ProgramarAutoRunAvalon()
        {
            if (!IsAutoRun) return;
            if (_autoRunAvalonTimer is null)
            {
                _autoRunAvalonTimer = new System.Windows.Threading.DispatcherTimer
                { Interval = TimeSpan.FromMilliseconds(700) };
                _autoRunAvalonTimer.Tick += async (_, _) =>
                {
                    _autoRunAvalonTimer.Stop();
                    if (!IsAutoRun || !EditorPlegableActivo) return;
                    _autoRun = false;
                    await AutoRun();
                };
            }
            _autoRunAvalonTimer.Stop();      // cada tecla reinicia la cuenta
            _autoRunAvalonTimer.Start();
        }

        /// <summary>RichTextBox -> AvalonEdit. Se llama al abrir archivo, al limpiar, y
        /// tras cualquier cambio que NO haya salido de AvalonEdit (botones de insertar,
        /// teclado de simbolos, MathCanvas).</summary>
        private void SincronizarHaciaAvalon()
        {
            if (!_avalonListo || _desdeAvalon || AvalonEditor is null) return;
            string codigo;
            try { codigo = GetInputText(); }
            catch { return; }
            if (codigo == AvalonEditor.Text) return;

            var caret = AvalonEditor.CaretOffset;
            _haciaAvalon = true;
            try
            {
                AvalonEditor.Text = codigo;
                AvalonEditor.CaretOffset = Math.Min(caret, AvalonEditor.Document.TextLength);
            }
            finally { _haciaAvalon = false; }
            ActualizarPlegado();
            ActualizarSemantica();
        }

        // ---------- botones de insertar ----------

        /// <summary>Lo que insertan los botones y el teclado de simbolos va al editor que el
        /// usuario esta VIENDO. Antes iba al RichTextBox oculto: el texto aparecia donde estaba
        /// SU cursor, no donde el usuario tenia el suyo.
        /// Devuelve false si el editor plegable no esta activo (entonces sigue el de siempre).</summary>
        private bool InsertarEnAvalon(string texto)
        {
            if (!EditorPlegableActivo || !_avalonListo || AvalonEditor is null) return false;

            var doc = AvalonEditor.Document;
            using (doc.RunUpdate())
            {
                if (texto == "\b")                                  // borrar un caracter
                {
                    if (AvalonEditor.SelectionLength > 0)
                        doc.Remove(AvalonEditor.SelectionStart, AvalonEditor.SelectionLength);
                    else if (AvalonEditor.CaretOffset > 0)
                        doc.Remove(AvalonEditor.CaretOffset - 1, 1);
                    AvalonEditor.Focus();
                    return true;
                }

                if (AvalonEditor.SelectionLength > 0)
                    doc.Remove(AvalonEditor.SelectionStart, AvalonEditor.SelectionLength);

                var inicio = AvalonEditor.CaretOffset;
                var t = texto == "\n" ? Environment.NewLine : texto;
                doc.Insert(inicio, t);
                SeleccionarHueco(inicio, t);
            }
            AvalonEditor.Focus();
            return true;
        }

        /// <summary>Deja seleccionado el hueco de la plantilla recien insertada, igual que hacia
        /// <c>SelectInsertedText</c>: lo de dentro de <c>{ }</c> (o hasta el <c>@</c>) y, si no
        /// hay llaves, el primer argumento entre <c>( )</c>.</summary>
        private void SeleccionarHueco(int inicio, string texto)
        {
            var i1 = texto.IndexOf('{') + 1;
            var i2 = i1 > 0 ? texto.IndexOfAny(['@', '}'], i1) : -1;
            if (i1 <= 0)
            {
                i1 = texto.IndexOf('(') + 1;
                i2 = i1 > 0 ? texto.IndexOfAny([';', ')'], i1) : -1;
            }
            if (i1 > 0 && i2 > 0)
                AvalonEditor.Select(inicio + i1, i2 - i1);
            else
                AvalonEditor.CaretOffset = inicio + texto.Length;
        }

        // ---------- ir y volver entre el reporte y el codigo ----------

        /// <summary>Clic en una linea del REPORTE (WebView2) -> esa linea en el editor que se VE.
        /// Antes movia el cursor del RichTextBox oculto, asi que no pasaba nada a la vista.
        /// Devuelve false si el editor plegable no esta activo (sigue el camino clasico).</summary>
        private bool IrALineaEnAvalon(int linea)
        {
            if (!EditorPlegableActivo || !_avalonListo || AvalonEditor is null) return false;

            var doc = AvalonEditor.Document;
            if (linea < 1 || doc.LineCount == 0) return false;
            if (linea > doc.LineCount) linea = doc.LineCount;

            var l = doc.GetLineByNumber(linea);
            // Si la linea esta DENTRO de un pliegue cerrado hay que abrirlo: si no, el cursor
            // caeria en un sitio que no se ve y el salto parece que no hizo nada.
            if (_foldingManager is not null)
                foreach (var f in _foldingManager.GetFoldingsContaining(l.Offset))
                    f.IsFolded = false;

            AvalonEditor.CaretOffset = l.EndOffset;
            AvalonEditor.ScrollToLine(linea);
            AvalonEditor.Focus();
            return true;
        }

        /// <summary>Doble clic en el codigo -> el reporte se mueve a la linea del cursor.
        /// Es el camino de vuelta: lo hacia <c>RichTextBox_MouseDoubleClick</c>, que ya no se
        /// dispara porque el que recibe el raton es AvalonEdit.</summary>
        private async void AvalonEditor_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (!IsCalculated || !EditorPlegableActivo) return;
            try
            {
                _scrollY = await _wv2Warper.GetScrollYAsync();
                var linea = LineaDelCursor();
                await ScrollOutputToLine(
                    _highlighter.Defined.HasMacros
                        ? _macroParser.GetUnwarpedLineNumber(linea)
                        : linea,
                    YDelCursorAvalon());
            }
            catch { }
        }

        /// <summary>A que altura de la ventana esta el cursor del editor plegable. Es el mismo
        /// dato que <c>ScrollOutput</c> sacaba del RichTextBox: sirve para dejar la linea del
        /// reporte a la MISMA altura que la del codigo, no pegada arriba.</summary>
        private double YDelCursorAvalon()
        {
            try
            {
                var tv = AvalonEditor.TextArea.TextView;
                var p = tv.GetVisualPosition(AvalonEditor.TextArea.Caret.Position,
                    ICSharpCode.AvalonEdit.Rendering.VisualYPosition.LineTop);
                return p.Y - tv.ScrollOffset.Y + AvalonEditor.Margin.Top - WebViewer.Margin.Top;
            }
            catch { return 0; }
        }

        /// <summary><c>--irlinea N [--cshot &lt;png&gt;]</c>: hace el mismo salto que un clic en la
        /// linea N del reporte, para comprobar en PNG que el cursor cae en esa linea del editor
        /// que se ve (la linea del cursor va pintada).</summary>
        private void PrepararCapturaSalto()
        {
            var args = Environment.GetCommandLineArgs();
            var i = Array.IndexOf(args, "--irlinea");
            if (i < 0 || i + 1 >= args.Length || !int.TryParse(args[i + 1], out var linea)) return;
            var iPng = Array.IndexOf(args, "--cshot");
            var png = iPng >= 0 && iPng + 1 < args.Length ? args[iPng + 1] : null;

            TrasUnRato(1400, () =>
            {
                try { LineClicked(linea.ToString()); } catch { }
                if (png is null) return;
                TrasUnRato(900, () => { CapturarPantalla(png); Environment.Exit(0); });
            });
        }

        // ---------- buscar y deshacer ----------

        /// <summary>Abre el buscador propio de AvalonEdit (resalta TODAS las coincidencias en
        /// el margen y en el texto). Devuelve false si el editor plegable no esta activo, para
        /// que siga funcionando el buscador clasico del RichTextBox.</summary>
        private bool BuscarEnAvalon(bool conReemplazo)
        {
            if (!EditorPlegableActivo || !_avalonListo) return false;

            if (_panelBuscar is null)
            {
                _panelBuscar = ICSharpCode.AvalonEdit.Search.SearchPanel.Install(AvalonEditor);
                VestirBuscador();
            }

            var sel = AvalonEditor.SelectedText;
            if (!string.IsNullOrEmpty(sel) && !sel.Contains('\n'))
                _panelBuscar.SearchPattern = sel;

            _panelBuscar.Open();
            // El panel de AvalonEdit sale sin caja de reemplazo; con Ctrl+H se pide igual, asi
            // que al menos se deja el foco puesto para escribir la busqueda.
            Dispatcher.InvokeAsync(() => _panelBuscar.Reactivate(),
                System.Windows.Threading.DispatcherPriority.Input);
            return true;
        }
        private ICSharpCode.AvalonEdit.Search.SearchPanel _panelBuscar;

        /// <summary>El buscador de AvalonEdit tambien nace claro; se le pasan los brushes del
        /// Lab, como al popup del autocompletado.</summary>
        private void VestirBuscador()
        {
            try
            {
                _panelBuscar.Background = (Brush)FindResource("ThemePanelBg");
                _panelBuscar.Foreground = (Brush)FindResource("ThemeText");
                _panelBuscar.BorderBrush = (Brush)FindResource("ThemeButtonBorder");
                _panelBuscar.BorderThickness = new Thickness(1);
                _panelBuscar.MarkerBrush = (Brush)FindResource("ThemeAccentGold");
            }
            catch { }
        }

        /// <summary>Deshacer/rehacer en el editor plegable. false = no esta activo.</summary>
        private bool DeshacerEnAvalon(bool rehacer)
        {
            if (!EditorPlegableActivo || !_avalonListo) return false;
            if (rehacer) { if (AvalonEditor.CanRedo) AvalonEditor.Redo(); }
            else if (AvalonEditor.CanUndo) AvalonEditor.Undo();
            return true;
        }

        // ---------- plegado ----------

        /// <summary>Quien SABE donde estan los bloques es el motor
        /// (<see cref="Calcpad.Core.Matlab.MatlabBlocks"/>); aqui solo se traducen sus
        /// tramos al +/- de AvalonEdit. Asi otra piel (Avalonia) pliega identico.
        /// UpdateFoldings conserva los que ya estaban cerrados: plegar no se deshace al escribir.</summary>
        /// <summary>Relee que declaro el usuario y repinta. Va con retardo: al escribir no hace
        /// falta rehacerlo en cada tecla, y en un archivo largo recorrerlo entero si se nota.</summary>
        private void ActualizarSemantica()
        {
            _repintado ??= new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromMilliseconds(350) };
            _repintado.Tick -= Repintar;
            _repintado.Tick += Repintar;
            _repintado.Stop();
            _repintado.Start();

            void Repintar(object s, EventArgs e)
            {
                _repintado.Stop();
                _colorizadorSemantico.Actualizar(LoDeclarado());
                AvalonEditor.TextArea.TextView.Redraw();
            }
        }
        private System.Windows.Threading.DispatcherTimer _repintado;

        private void ActualizarPlegado()
        {
            if (_foldingManager is null) return;
            try
            {
                var tramos = Calcpad.Core.Matlab.MatlabBlocks.Find(AvalonEditor.Text);
                var pliegues = tramos
                    .Select(t => new ICSharpCode.AvalonEdit.Folding.NewFolding(t.Start, t.End) { Name = t.Label })
                    .ToList();
                _foldingManager.UpdateFoldings(pliegues, -1);
            }
            catch { /* con un bloque a medio escribir puede no cuadrar: no es critico */ }
        }

        private void PlegarTodo_Click(object sender, RoutedEventArgs e)
        {
            ActualizarPlegado();
            if (_foldingManager is null) return;
            foreach (var f in _foldingManager.AllFoldings) f.IsFolded = true;
        }

        private void DesplegarTodo_Click(object sender, RoutedEventArgs e)
        {
            if (_foldingManager is null) return;
            foreach (var f in _foldingManager.AllFoldings) f.IsFolded = false;
        }

        // ---------- resaltado ----------

        private void CargarResaltadoMatlab()
        {
            try
            {
                using var stream = typeof(MainWindow).Assembly
                    .GetManifestResourceStream("Calcpad.Wpf.Matlab.xshd");
                if (stream is null) return;
                using var reader = XmlReader.Create(stream);
                AvalonEditor.SyntaxHighlighting = HighlightingLoader.Load(reader, HighlightingManager.Instance);
            }
            catch { /* sin resaltado no es critico */ }
        }

        /// <summary>Re-tine el resaltado segun el tema del Lab (Oscuro / Oro).</summary>
        private void AplicarColoresAvalon(bool oscuro)
        {
            // Lo que NO viene del .xshd tambien sigue el tema: nombres del usuario e imagenes.
            _colorizadorSemantico.AplicarTema(oscuro);
            _imagenes.AplicarTema(oscuro);

            // La linea del cursor: un velo de oro, mas suave en el tema claro. Sin borde (el de
            // fabrica es una caja de 1 px que en oscuro parece un error de dibujo).
            if (AvalonEditor is not null)
            {
                var tv = AvalonEditor.TextArea.TextView;
                tv.CurrentLineBackground = new SolidColorBrush(
                    Color.FromArgb((byte)(oscuro ? 38 : 26), 245, 192, 67));
                tv.CurrentLineBorder = new Pen(Brushes.Transparent, 0);
            }

            var hl = AvalonEditor?.SyntaxHighlighting;
            if (hl is null) return;

            void C(string nombre, string hexOscuro, string hexClaro)
            {
                var col = hl.GetNamedColor(nombre);
                if (col is not null)
                    col.Foreground = new SimpleHighlightingBrush(
                        (Color)ColorConverter.ConvertFromString(oscuro ? hexOscuro : hexClaro));
            }

            C("Comment",   "#5AC37E", "#0A7F3F");
            C("Section",   "#7BD79A", "#046A38");
            C("TituloDoc", "#F5C043", "#8A5A00");   // %" titulo visible en el reporte
            C("TextoDoc",  "#9DB8D2", "#31506E");   // %' texto visible en el reporte
            C("String",    "#E5C07B", "#A15C00");
            C("Number",    "#9ECBFF", "#0B66C3");
            C("Keyword",   "#C678DD", "#A626A4");
            C("Constant",  "#56B6C2", "#0B7285");
            C("Builtin",   "#61AFEF", "#1A56C4");
            AvalonEditor.TextArea.TextView.Redraw();
        }

        // ---------- autocompletado ----------

        private void AvalonEditor_TextEntered(object sender, TextCompositionEventArgs e)
        {
            if (_avalonCompletion is not null) return;    // ya abierto: AvalonEdit filtra solo
            if (e.Text.Length == 1 && (char.IsLetter(e.Text[0]) || e.Text[0] == '_'))
            {
                var prefijo = PalabraActual();
                if (prefijo.Length >= 2) MostrarAutocompletado(prefijo);
            }
        }

        private void AvalonEditor_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Space && (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control)
            {
                MostrarAutocompletado(PalabraActual());
                e.Handled = true;
            }
        }

        private string PalabraActual()
        {
            var doc = AvalonEditor.Document;
            int caret = AvalonEditor.CaretOffset, ini = caret;
            while (ini > 0)
            {
                var c = doc.GetCharAt(ini - 1);
                if (char.IsLetterOrDigit(c) || c == '_') ini--;
                else break;
            }
            return doc.GetText(ini, caret - ini);
        }

        private void MostrarAutocompletado(string prefijo)
        {
            var items = MatlabLang.Items(prefijo, LoDeclarado(), LineaDelCursor(), _isDarkTheme).ToList();
            if (items.Count == 0) return;

            _avalonCompletion = new CompletionWindow(AvalonEditor.TextArea) { CloseWhenCaretAtBeginning = true };
            _avalonCompletion.StartOffset = AvalonEditor.CaretOffset - prefijo.Length;
            VestirPopup(_avalonCompletion);
            foreach (var it in items) _avalonCompletion.CompletionList.CompletionData.Add(it);
            if (!string.IsNullOrEmpty(prefijo)) _avalonCompletion.CompletionList.SelectItem(prefijo);
            _avalonCompletion.Closed += (_, _) => _avalonCompletion = null;
            _avalonCompletion.Show();
        }

        /// <summary>Diagnostico de <c>--completar</c>: que simbolos vio el motor en el archivo.</summary>
        private void VolcarDeclaradas(string ruta)
        {
            try
            {
                var sb = new System.Text.StringBuilder();
                sb.AppendLine($"linea del cursor: {LineaDelCursor()}");
                var d = LoDeclarado();
                if (d is null) sb.AppendLine("MatlabSymbols = null (excepcion al leer)");
                else
                    foreach (var s in d)
                        sb.AppendLine($"   {s.Tipo,-9} {s.Nombre}  (linea {s.Linea}) {s.Firma}");
                System.IO.File.WriteAllText(ruta, sb.ToString());
            }
            catch { }
        }

        private int LineaDelCursor() =>
            AvalonEditor.Document.GetLineByOffset(AvalonEditor.CaretOffset).LineNumber;

        /// <summary>Que declaro el usuario (variables, funciones y sus argumentos) y en que
        /// linea, segun el MOTOR. No se usa el <c>UserDefined</c> de Calcpad: alli el <c>;</c>
        /// final significa "la linea sigue", asi que en MATLAB pegaba las lineas y solo veia la
        /// PRIMERA variable de cada bloque.</summary>
        private System.Collections.Generic.IReadOnlyList<Calcpad.Core.Matlab.Simbolo> LoDeclarado()
        {
            try { return Calcpad.Core.Matlab.MatlabSymbols.Find(AvalonEditor.Text); }
            catch { return null; }
        }

        /// <summary>El popup de AvalonEdit nace BLANCO (sus colores son fijos, no heredan del
        /// tema): en el tema Oscuro daba un cuadro blanco en medio del editor negro. Se le
        /// pasan los mismos brushes del Lab, que ya cambian solos al alternar Oscuro/Oro.</summary>
        private void VestirPopup(CompletionWindow w)
        {
            try
            {
                var fondo = (System.Windows.Media.Brush)FindResource("ThemeEditorBg");
                var texto = (System.Windows.Media.Brush)FindResource("ThemeText");
                var borde = (System.Windows.Media.Brush)FindResource("ThemeButtonBorder");

                w.Background = fondo;
                w.Foreground = texto;
                w.BorderBrush = borde;
                w.CompletionList.Background = fondo;
                w.CompletionList.Foreground = texto;
                w.CompletionList.ListBox.Background = fondo;
                w.CompletionList.ListBox.Foreground = texto;
                w.CompletionList.ListBox.BorderBrush = borde;
                w.FontFamily = AvalonEditor.FontFamily;
                w.FontSize = AvalonEditor.FontSize;
            }
            catch { /* si falta un brush, el popup se queda con su look de fabrica */ }
        }
    }
}
