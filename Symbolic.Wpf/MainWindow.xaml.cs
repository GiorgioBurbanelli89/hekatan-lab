using Calcpad.Core;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Web;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Calcpad.Wpf
{
    public partial class MainWindow : Window
    {
        // Plugins
        private Calcpad.Core.Plugins.ICalcpadPlugin? _plugin;

        // Culture
        private static readonly string _currentCultureName = "en"; // en, bg or zh

        // Static resources
        private static readonly char[] GreekLetters = ['α', 'β', 'χ', 'δ', 'ε', 'φ', 'γ', 'η', 'ι', 'ø', 'κ', 'λ', 'μ', 'ν', 'ο', 'π', 'θ', 'ρ', 'σ', 'τ', 'υ', 'ϑ', 'ω', 'ξ', 'ψ', 'ζ'];
        private static readonly char[] LatinLetters = ['a', 'b', 'g', 'd', 'e', 'z', 'h', 'q', 'i', 'k', 'l', 'm', 'n', 'x', 'o', 'p', 'r', 's', 's', 't', 'u', 'f', 'c', 'y', 'w'];
        private static readonly Regex HtmlAnchorHrefRegex = new(@"(?<=<a\b[^>]*?\bhref\s*=\s*"")(?!#)", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HtmlAnchorTargetRegex = new(@"\s+\btarget\b\s*=\s*""\s*_\w+\s*""", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HtmlImgPrevRegex = new(@"src\s*=\s*""\s*\.\.", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HtmlImgCurRegex = new(@"src\s*=\s*""\s*\.", RegexOptions.IgnoreCase | RegexOptions.Compiled);
        private static readonly Regex HtmlImgAnyRegex = new(@"src\s*=\s*""\s*\.\.?(.+?)""", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        internal readonly struct AppInfo
        {
            static AppInfo()
            {
                Path = AppDomain.CurrentDomain.BaseDirectory;
                Name = AppDomain.CurrentDomain.FriendlyName + ".exe";
                FullName = System.IO.Path.Combine(Path, Name);
                Version = Assembly.GetExecutingAssembly().GetName().Version.ToString();
                Title = " Hekatan Lab " + Version[0..(Version.LastIndexOf('.'))];
                DocPath = Path + "doc";
                if (!Directory.Exists(DocPath))
                    DocPath = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) + "\\CalcpadLab";
            }
            internal static readonly string Path;
            internal static readonly string Name;
            internal static readonly string FullName;
            internal static readonly string Version;
            internal static readonly string Title;
            internal static readonly string DocPath;
        }
        private const double AutoIndentStep = 28.0;

        // Find and Replace
        private readonly FindReplace _findReplace = new();
        private FindReplaceWindow _findReplaceWindow;

        // Parsers
        private readonly ExpressionParser _parser;
        private readonly MacroParser _macroParser;
        private readonly HighLighter _highlighter;

        // Html strings
        private readonly string _htmlWorksheet;
        private readonly string _htmlParsingPath;
        private readonly string _htmlParsingUrl;
        private readonly string _htmlHelpPath;
        private readonly string _htmlSource;
        private string _htmlUnwarpedCode;

        // RichTextBox Document
        private readonly FlowDocument _document;
        private Paragraph _currentParagraph;
        private Paragraph _lastModifiedParagraph;

        private readonly StringBuilder _stringBuilder = new(10000);
        private readonly UndoManager _undoMan;
        private readonly WebView2Wrapper _wv2Warper;
        private readonly InsertManager _insertManager;
        private readonly AutoCompleteManager _autoCompleteManager;

        private readonly string _readmeFileName;
        private string DocumentPath { get; set; }
        private string _cfn;
        private string _tempDir;
        private string CurrentFileName
        {
            get => _cfn;
            set
            {
                _cfn = value;
                if (string.IsNullOrEmpty(value))
                {
                    _tempDir = Path.GetRandomFileName() + '\\';
                    Title = AppInfo.Title;
                }
                else
                {
                    var path = Path.GetDirectoryName(value);
                    if (string.IsNullOrWhiteSpace(path))
                        _cfn = Path.Combine(DocumentPath, value);
                    else
                        SetCurrentDirectory(path);
                    Title = AppInfo.Title + " - " + Path.GetFileName(value);
                    _tempDir = Path.GetFileNameWithoutExtension(value) + '\\';
                }
            }
        }
        // State variables
        private readonly string _svgTyping;
        private bool _isSaving;
        private bool _isSaved;
        private bool _isParsing;
        // El pipeline MATLAB sigue corriendo. Hace falta APARTE de _isParsing porque el
        // streaming navega a la página en blanco NADA MÁS empezar, y ese
        // NavigationCompleted ponía _isParsing = false a los ~200 ms: la ventana se creía
        // libre y aceptaba OTRO cálculo encima del que estaba corriendo (dos pipelines a la
        // vez → el banner "Calculando…" de uno tapando el resultado del otro).
        private bool _matlabBusy;
        private bool _isPasting;
        private bool _isTextChangedEnabled;
        // Round-trip protection: keep an exact copy of the file text loaded
        // from disk. The HighLighter occasionally reformats whitespace and
        // apostrophes during re-tokenization (e.g. `' #deqξ '` → `'#deqξ'`,
        // `#blk` cell-2 leading `'` dropped). If the user has not actually
        // typed anything, we prefer rewriting the original bytes verbatim
        // over re-emitting the highlighter's reconstruction.
        private string _loadedFileText;
        private bool _userTypedSinceLoad;
        private readonly double _inputHeight;
        private bool _mustPromptUnlock;
        private bool _forceHighlight;
        private int _countKeys = int.MinValue;
        private bool _forceBackSpace;
        private int _pasteOffset;
        private int _currentLineNumber;
        private int _currentOffset;
        private TextPointer _pasteEnd;
        private bool _scrollOutput;
        private double _scrollY;
        private bool _autoRun;
        private double _screenScaleFactor;
        private bool _calculateOnActivate;
        private bool _isWebView2Focused;
        private readonly Brush _borderBrush;

        // Private properites
        private bool IsComplex => _parser.Settings.Math.IsComplex;
        internal bool IsSaved
        {
            get => _isSaved;
            private set
            {
                SaveButton.IsEnabled = !value;
                MenuSave.IsEnabled = !value;
                _isSaved = value;
            }
        }

        private bool IsWebForm
        {
            get => WebFormButton.Tag.ToString() == "T";
            set => SetWebForm(value);
        }
        // Un párrafo cuenta como "imagen" (miniatura) SOLO si su Tag es '% #img ...' Y
        // realmente contiene la imagen (InlineUIContainer). Al pulsar Enter, WPF parte el
        // párrafo y COPIA el Tag al nuevo párrafo vacío (sin la imagen); sin este chequeo
        // ese fantasma emitía la línea '% #img' otra vez → imagen DUPLICADA en el WebView2.
        private static bool IsImageParagraph(Block b) =>
            b is Paragraph p
            && p.Tag is string t
            && t.StartsWith("% #img", StringComparison.OrdinalIgnoreCase)
            && p.Inlines.FirstInline is InlineUIContainer;

        // Texto del script. FAST-PATH: si NO hay párrafos-imagen (miniatura), devuelve el TextRange
        // plano igual que siempre (comportamiento 100% original → cero riesgo en scripts normales).
        // Con miniaturas: itera y sustituye cada párrafo-imagen por su Tag ('% #img data:...base64').
        private string InputText
        {
            get
            {
                bool hasImg = false;
                for (var bb = _document.Blocks.FirstBlock; bb != null; bb = bb.NextBlock)
                    if (IsImageParagraph(bb))
                    { hasImg = true; break; }
                if (!hasImg)
                    return new TextRange(_document.ContentStart, _document.ContentEnd).Text;
                var sb = new StringBuilder();
                bool first = true;
                for (var bb = _document.Blocks.FirstBlock; bb != null; bb = bb.NextBlock)
                {
                    if (!first) sb.Append("\r\n");
                    first = false;
                    sb.Append(IsImageParagraph(bb)
                        ? ((Paragraph)bb).Tag as string
                        : new TextRange(bb.ContentStart, bb.ContentEnd).Text);
                }
                return sb.ToString();
            }
        }
        private int InputTextLength => _document.ContentEnd.GetOffsetToPosition(_document.ContentStart);
        private SpanLineEnumerator InputTextLines => InputText.EnumerateLines();
        private bool IsCalculated
        {
            get => CalcButton.Tag.ToString() == "T";
            set
            {
                SetButton(CalcButton, value && InputTextLength != 0);
                if (IsWebForm)
                {
                    WebFormButton.IsEnabled = !IsCalculated;
                    MenuWebForm.IsEnabled = WebFormButton.IsEnabled;
                }
                MenuCalculate.Icon = IsCalculated ? "  ✓" : null;
            }
        }

        private bool IsWebView2Focused
        {
            get => _isWebView2Focused;
            set
            {
                if (value == _isWebView2Focused) return;
                _isWebView2Focused = value;
                _findReplace.IsWebView2Focused = value;
                InputFrame.BorderBrush = value ? _borderBrush : SystemColors.ActiveBorderBrush;
                OutputFrame.BorderBrush = value ? SystemColors.ActiveBorderBrush : _borderBrush;
            }
        }
        private bool DisplayUnwarpedCode => CodeCheckBorder.Visibility == Visibility.Visible && CodeCheckBox.IsChecked.Value;
        private bool IsUnwarpedCode => WebViewer.Tag is bool b && b;
        // ── Startup profiling (instance fields para acceso desde otros métodos) ──
        private System.Diagnostics.Stopwatch _startupSw;
        private System.Text.StringBuilder _startupLog;
        private void StartupMark(string what)
        {
            if (_startupSw == null) return;
            _startupLog?.AppendLine($"  {_startupSw.ElapsedMilliseconds,6} ms  {what}");
            try
            {
                string logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "calcpad_lab_startup.log");
                System.IO.File.WriteAllText(logPath, _startupLog.ToString());
            } catch { }
        }
        // Warmup del pipeline MATLAB: paga en un hilo de fondo el static ctor (MKL/LAPACK) y el
        // JIT de primera llamada, ANTES de que el usuario corra nada. El primer cálculo real
        // hace `await _warmupTask` (serializa: los caches del JIT no son thread-safe). Script
        // sin side-effects (sin archivos ni plots a disco).
        // Se lanza en Window_ContentRendered, NO en el ctor: arrancando a la vez que la ventana
        // competía por CPU con el JIT de todo WPF (Ribbon, WebView2, WinForms…).
        // Cuánto cuesta, medido con tests/wpf/bench.py: 1.2 s en total. Antes eran 7-19 s, pero
        // eso era la bandera TieredCompilation=false del .csproj (ver allí), no el warmup.
        private static System.Threading.Tasks.Task _warmupTask = System.Threading.Tasks.Task.CompletedTask;

        private static System.Threading.Tasks.Task StartPipelineWarmup()
        {
            // Dos tandas. La BASE es la que de verdad importa: statements SIN punto y coma,
            // que es la ruta cara (mostrar el resultado sustituyendo valores,
            // "M = 2·L = 2·3.5 = 7"). El warmup viejo solo tenía statements mudos, así que
            // esa ruta la pagaba el usuario en su primer cálculo: 8 s por un escalar.
            const string warmupBase =
                "x = 5;\n" +
                "disp(x);\n" +
                "wLit = 5\n" +
                "yWarm = 2*x + 1\n" +
                "zWarm = sqrt(yWarm)/3\n" +
                "vWarm = [1 2 3]*2\n" +
                "A = [1 2; 3 4];\n" +
                "B = A * A';\n";
            const string warmupExtra =
                "f = @(t) exp(-t.^2);\n" +
                "q1 = quadgk(f, 0, 1);\n" +
                "q2 = quadl(f, 0, 1);\n" +
                "q3 = integral2(@(x, y) x.*y, 0, 1, 0, 1);\n" +
                "% #md\n" +
                "% ## Warmup\n" +
                "% - a\n" +
                "% - b\n" +
                "% #endmd\n" +
                "disp(q1 + q2 + q3);\n";
            return System.Threading.Tasks.Task.Run(() =>
            {
                var sw = System.Diagnostics.Stopwatch.StartNew();
                long tBase = 0;
                try
                {
                    var warm = new Calcpad.Core.Matlab.MatlabPipeline();
                    warm.Run(warmupBase);
                    tBase = sw.ElapsedMilliseconds;
                    warm.Run(warmupExtra);
                }
                catch { /* el run real no depende del warmup */ }
                // Log en %TEMP%: si el arranque vuelve a sentirse lento, aquí se ve en qué tanda.
                try
                {
                    System.IO.File.WriteAllText(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "calcpad_lab_warmup.log"),
                        $"base (statements que imprimen): {tBase} ms\n" +
                        $"extra (integrales + md):        {sw.ElapsedMilliseconds - tBase} ms\n" +
                        $"total:                          {sw.ElapsedMilliseconds} ms\n");
                }
                catch { }
            });
        }

        public MainWindow()
        {
            // ── Startup profiling ──
            _startupSw = System.Diagnostics.Stopwatch.StartNew();
            _startupLog = new System.Text.StringBuilder();
            var sw = _startupSw;
            var log = _startupLog;
            void Mark(string what) { log.AppendLine($"  {sw.ElapsedMilliseconds,6} ms  {what}"); }
            log.AppendLine($"═══ Calcpad-Lab WPF startup ═══ {DateTime.Now:HH:mm:ss}");
            // JIT HABILITADO en WPF — con libopenblas v0.3.33 (bundled BLAS+LAPACK
            // sin dep externa rota) el crash AV en indexado post-K\F que motivo
            // deshabilitar JIT ya no ocurre. Loops FEM en WPF ahora corren al
            // ritmo CLI (~5-10ms ensamblaje, ~60ms solve).
            Calcpad.Core.Matlab.MatlabJit.Enabled = true;
            Mark("MatlabJit enabled (WPF)");
            // Pre-load LAPACK/BLAS DLLs antes de WebView2
            _ = Calcpad.Core.BlasInterop.Available;
            _ = Calcpad.Core.LapackInterop.Available;
            Mark($"Native DLLs pre-loaded (BLAS={Calcpad.Core.BlasInterop.Available}, LAPACK={Calcpad.Core.LapackInterop.Available})");
            Mark("Native DLLs listas (el warmup MATLAB se lanza al final del arranque)");
            _parser = new();
            Mark("ExpressionParser ctor");
            _highlighter = new();
            // Tema persistido (Dark por defecto): recolorea la paleta de sintaxis.
            _isDarkTheme = Properties.Settings.Default.DarkTheme;
            // Override headless: --theme <dark|gold> (para revisar el tema con --wshot).
            var _av = Environment.GetCommandLineArgs();
            for (int _i = 1; _i < _av.Length; _i++)
                if (_av[_i] == "--theme" && _i + 1 < _av.Length) _isDarkTheme = _av[_i + 1] != "gold";
            HighLighter.ApplyTheme(_isDarkTheme);
            Calcpad.Core.Matlab.MatlabPlots.DarkTheme = _isDarkTheme;   // gráficas oscuras en dark desde el arranque
            Mark("HighLighter ctor + theme");
            ExpressionParser.PipProgressChanged += OnPipProgressChanged;
            Thread.CurrentThread.CurrentUICulture = new CultureInfo(_currentCultureName);
            Mark("Culture set");
            InitializeComponent();
            Mark("InitializeComponent (XAML, RichTextBox, AutoCompleteListBox, OutputFrame, etc.)");
            _borderBrush = OutputFrame.BorderBrush;
            LoadPlugins();
            Mark("LoadPlugins");
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            _inputHeight = InputGrid.RowDefinitions[1].Height.Value;
            ToolTipService.InitialShowDelayProperty.OverrideMetadata(
                typeof(DependencyObject),
                new FrameworkPropertyMetadata(500));
            HighLighter.IncludeClickEventHandler = Include_Click;
            UserDefined.Include = Include;
            LineNumbers.ClipToBounds = true;
            SetCurrentDirectory();
            var docPath = AppInfo.DocPath;
            var docUrl = $"file:///{docPath.Replace("\\", "/")}";
            var htmlExt = AddCultureExt("html");
            // Embed jQuery and calcpad-viz inline (avoids cross-origin warnings in WebView2/DevTools).
            // Falls back to file:/// only if the JS file isn't found alongside the .exe.
            var rawTemplate = ReadTextFromFile($"{docPath}\\template{htmlExt}");
            rawTemplate = EmbedScriptInline(rawTemplate, "jquery-3.6.3.min.js", docPath);
            rawTemplate = EmbedScriptInline(rawTemplate, "calcpad-viz.umd.js", docPath);
            // Any remaining calcpad.local references → resolve to local doc URL.
            // (Fixed typo: there used to be an erroneous space "https:// calcpad.local".)
            _htmlWorksheet = rawTemplate.Replace("https://calcpad.local", docUrl);
            _htmlParsingPath = $"{docPath}\\parsing{htmlExt}";
            _htmlParsingUrl = $"{docUrl}/parsing{htmlExt}";
            _htmlHelpPath = GetHelp(MainWindowResources.calcpad_download_help_html);
            _htmlSource = ReadTextFromFile($"{docPath}\\source.html");
            _svgTyping = $"<img style=\"height:1em;\" src=\"{docUrl}/typing.gif\" alt=\"...\">";
            _readmeFileName = $"{docPath}\\readme{htmlExt}";
            Mark("HTML templates loaded (template.html, jquery, calcpad-viz, source.html)");
            InvButton.Tag = false;
            HypButton.Tag = false;
            // Ctrl+Z global: si hay un canvas de dibujo (talud/ginput) en el WebView2, deshace el
            // ultimo vertice pase lo que pase con el foco del teclado (que no siempre esta en el WebView2).
            // __hktUndo no existe si no hay dibujo -> no-op inofensivo, el editor hace su propio undo.
            this.AddHandler(Keyboard.PreviewKeyDownEvent, new KeyEventHandler((s, e) => {
                if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
                {
                    try { WebViewer?.CoreWebView2?.ExecuteScriptAsync("window.__hktUndo&&window.__hktUndo()"); } catch { }
                }
            }), true);
            RichTextBox.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(RichTextBox_Scroll));
            DataObject.AddPastingHandler(RichTextBox, RichTextBox_Paste);
            _document = RichTextBox.Document;
            _currentParagraph = _document.Blocks.FirstBlock as Paragraph;
            _currentLineNumber = 1;
            HighLighter.Clear(_currentParagraph);
            Mark("RichTextBox setup (Document, handlers, HighLighter.Clear)");
            _undoMan = new UndoManager();
            Record();
            _wv2Warper = new WebView2Wrapper(WebViewer, $"{docPath}\\blank.html");
            ApplyWebViewBackground(_isDarkTheme);   // fondo oscuro desde el arranque (evita flash blanco)
            // Cuando el CoreWebView2 termine de inicializar, re-aplica (PreferredColorScheme requiere Core listo).
            WebViewer.CoreWebView2InitializationCompleted += (s, e) => { if (e.IsSuccess) ApplyWebViewBackground(_isDarkTheme); };
            Mark("WebView2 wrapper (Output panel)");
            _macroParser = new MacroParser
            {
                Include = Include
            };
            _insertManager = new(RichTextBox);
            // Con el editor plegable delante, lo que insertan los botones va A EL (a donde el
            // usuario ve el cursor), no al RichTextBox oculto.
            _insertManager.Desvio = InsertarEnAvalon;
            _autoCompleteManager = new(RichTextBox, AutoCompleteListBox, Dispatcher, _insertManager);
            Mark("AutoCompleteManager (AutoList)");
            try { PopulateLoopForms(); UpdateLoopPreview(); } catch { }  // ventana-loop siempre inicializada
            _autoCompleteManager.LoopTrigger = ShowLoopBuilder;          // item "loop" de la lista abre el panel
            _cfn = string.Empty;
            _isTextChangedEnabled = false;
            IsSaved = true;
            _findReplace.RichTextBox = RichTextBox;
            _findReplace.WebViewer = WebViewer;
            // Escribir log al final del ctor (después de los demás campos)
            try
            {
                string logPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "calcpad_lab_startup.log");
                log.AppendLine($"  ──────────────");
                log.AppendLine($"  TOTAL ctor: {sw.ElapsedMilliseconds} ms");
                System.IO.File.WriteAllText(logPath, log.ToString());
            } catch { }
            _findReplace.BeginSearch += FindReplace_BeginSearch;
            _findReplace.EndSearch += FindReplace_EndSearch;
            _findReplace.EndReplace += FindReplace_EndReplace;
            _isTextChangedEnabled = true;
        }

        private static string AddCultureExt(string ext) => string.Equals(_currentCultureName, "en", StringComparison.Ordinal) ?
                $".{ext}" :
                $".{_currentCultureName}.{ext}";

        public bool SaveStateAndRestart(string tempFile)
        {
            var text = InputText;
            Clipboard.SetText(text);
            File.WriteAllText(tempFile, text);
            Properties.Settings.Default.TempFile = tempFile;
            Properties.Settings.Default.FileName = CurrentFileName;
            Properties.Settings.Default.Save();
            _isSaved = true;
            Execute(AppInfo.FullName);
            return true;
        }

        private void TryRestoreState()
        {
            if (IsHeadless || IsControlMode) return;   // headless/control: nada de MessageBox/FileOpen (bloquearia)
            var tempFile = Properties.Settings.Default.TempFile;
            if (string.IsNullOrEmpty(tempFile)) return;
            var fileName = Properties.Settings.Default.FileName;
            Properties.Settings.Default.TempFile = null;
            Properties.Settings.Default.FileName = null;
            Properties.Settings.Default.Save();
            var message = MainWindowResources.TryRestoreState_Recovered_SavePrompt;
            var result = MessageBox.Show(
                message,
                "Hekatan Lab",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question);
            if (result != MessageBoxResult.Yes) return;
            try
            {
                FileOpen(tempFile);
                CurrentFileName = fileName;
            }
            catch (Exception ex)
            {
                ShowErrorMessage(
                    string.Format(MainWindowResources.TryRestoreState_Failed, ex.Message, tempFile)
                );
                IsSaved = true;
                Command_New(this, null);
            }
        }

        private void SetCurrentDirectory(string path = null)
        {
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
            {
                DocumentPath = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) + "\\Hekatan-Lab";
                if (!Directory.Exists(DocumentPath))
                    Directory.CreateDirectory(DocumentPath);

                // Sync de Examples: copia los Examples bundleados (en {app}\Examples)
                // al perfil del usuario. La copia es ADITIVA — overwrite:false en
                // CopyDirectoryRecursive — así los archivos que el usuario haya
                // editado o agregado se conservan, pero los faltantes (ej. nuevas
                // carpetas de un upgrade) se agregan.
                // Anteriormente solo copiaba si la carpeta destino estaba vacía,
                // pero eso fallaba cuando había subcarpetas vacías de un install
                // previo (ej. 01 Demos creada pero sin archivos).
                try
                {
                    var userExamples = Path.Combine(DocumentPath, "Examples");
                    var bundledExamples = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Examples");
                    if (Directory.Exists(bundledExamples))
                        CopyDirectoryRecursive(bundledExamples, userExamples);
                }
                catch { /* silencioso: si no se puede copiar, el usuario puede abrir desde {app}\Examples */ }
            }
            else
                DocumentPath = path;

            Directory.SetCurrentDirectory(DocumentPath);
        }

        private static void CopyDirectoryRecursive(string source, string dest)
        {
            Directory.CreateDirectory(dest);
            foreach (var f in Directory.EnumerateFiles(source))
                File.Copy(f, Path.Combine(dest, Path.GetFileName(f)), overwrite: false);
            foreach (var d in Directory.EnumerateDirectories(source))
                CopyDirectoryRecursive(d, Path.Combine(dest, Path.GetFileName(d)));
        }

        private void ForceHighlight()
        {
            if (_forceHighlight)
            {
                RichTextBox.CaretPosition = _document.ContentStart;
                HighLightAll();
                SetAutoIndent();
                _forceHighlight = false;
            }
        }

        private static void SetButton(Control b, bool on)
        {
            if (on)
            {
                b.Tag = "T";
                b.BorderBrush = Brushes.SteelBlue;
                b.Background = Brushes.LightBlue;
            }
            else
            {
                b.Tag = "F";
                b.BorderBrush = Brushes.Transparent;
                b.Background = Brushes.Transparent;
            }
        }

        private void SetUILock(bool locked)
        {
            var enabled = !locked;
            CopyButton.IsEnabled = enabled;
            PasteButton.IsEnabled = enabled;
            UndoButton.IsEnabled = enabled;
            RedoButton.IsEnabled = enabled;
            ImageButton.IsEnabled = enabled;
            KeyPadButton.IsEnabled = enabled;
            MenuEdit.IsEnabled = enabled;
            MenuInsert.IsEnabled = enabled;
            FindButton.IsEnabled = enabled;
        }

        private void SetOutputFrameHeader(bool isWebForm)
        {
            OutputFrame.Header = isWebForm ? MainWindowResources.Input : MainWindowResources.Output;
        }
        private void RichTextBox_Scroll(object sender, ScrollChangedEventArgs e)
        {
            if (e.VerticalChange != 0 && !_sizeChanged && !IsWebForm)
            {
                _autoCompleteManager.MoveAutoComplete();
                DispatchLineNumbers();
                if (e.VerticalChange > 0 && _lastModifiedParagraph is not null)
                {
                    Rect r = _lastModifiedParagraph.ContentStart.GetCharacterRect(LogicalDirection.Forward);
                    if (r.Top < 0.8 * RichTextBox.ActualHeight)
                        DispatchHighLightFromCurrent();
                }
            }
        }

        private void Button_Click(object sender, RoutedEventArgs e)
        {
            var element = (FrameworkElement)sender;
            var tag = element.Tag.ToString();
            var index = tag.IndexOf('␣') + 1;
            if (index > 0)
            {
                if (MarkdownCheckBox.IsChecked.Value == true)
                    tag = tag[index..];
                else
                    tag = tag[..(index - 1)];
            }
            // Con el editor plegable delante, el marcado y las plantillas de varias lineas van
            // AHI, no al RichTextBox oculto (donde caerian en el cursor equivocado).
            if (tag.Contains('‖') && MarcarEnAvalon(tag)) return;
            if (tag.Contains('§') && InsertarLineasEnAvalon(tag)) return;

            RichTextBox.BeginChange();
            if (tag.Contains('‖'))
            {
                if (tag.StartsWith("‖#"))
                    _insertManager.InsertMarkdownHeading(tag);
                else if (tag.StartsWith("<p>", StringComparison.OrdinalIgnoreCase) ||
                    tag.StartsWith("<h", StringComparison.OrdinalIgnoreCase) &&
                    !tag.Equals("<hr/>‖", StringComparison.OrdinalIgnoreCase))
                    _insertManager.InsertHtmlHeading(tag);
                else if (!_insertManager.InsertInline(tag))
                    Dispatcher.InvokeAsync(() =>
                    MessageBox.Show(
                        MainWindowResources.Inline_Html_elements_must_not_cross_text_lines,
                        "Hekatan Lab", MessageBoxButton.OK, MessageBoxImage.Stop));
            }
            else if (tag.Contains('§'))
                InsertLines(tag, "§", false);
            else switch (tag)
                {
                    case null or "": break;
                    case "AC": RemoveLine(); break;
                    case "C": _insertManager.RemoveChar(); break;
                    case "Enter": _insertManager.InsertLine(); break;
                    default:
                        if (tag[0] == '#' ||
                            tag[0] == '$' && (
                                tag.StartsWith("$plot", StringComparison.OrdinalIgnoreCase) ||
                                tag.StartsWith("$map", StringComparison.OrdinalIgnoreCase)
                            ))
                        {
                            var p = RichTextBox.Selection.End.Paragraph;
                            if (p is not null && p.ContentStart?.GetOffsetToPosition(p.ContentEnd) > 0)
                            {
                                var tp = p.ContentEnd.InsertParagraphBreak();
                                tp.InsertTextInRun(tag);
                                p = tp.Paragraph;
                                var lineNumber = GetLineNumber(p);
                                _highlighter.Parse(p, IsComplex, lineNumber, true);
                                SetAutoIndent();
                                tp = p.ContentEnd;
                                RichTextBox.Selection.Select(tp, tp);
                            }
                            else
                                _insertManager.InsertText(tag);
                        }
                        else
                            _insertManager.InsertText(tag);
                        break;
                }
            if (tag == "Enter")
                CalculateAsync();

            RichTextBox.EndChange();
            RichTextBox.Focus();
            Keyboard.Focus(RichTextBox);
        }

        private void InsertLines(string tag, string delimiter, bool comment)
        {
            var parts = tag.Split(delimiter);
            var p = RichTextBox.Selection.Start.Paragraph;
            var selLength = RichTextBox.Selection.Text.Length;
            TextPointer tp = selLength > 0 ? p.ContentStart : p.ContentEnd;
            var pararaphLength = new TextRange(p.ContentStart, p.ContentEnd).Text.Length;
            if (pararaphLength > 0)
            {
                tp = tp.InsertParagraphBreak();
                if (selLength > 0)
                {
                    p = tp.Paragraph;
                    if (tp  is not null)
                        tp = p.PreviousBlock.ContentEnd;
                }
            }
            p = tp.Paragraph;
            var lineNumber = GetLineNumber(p);
            InsertPart(0);
            if (selLength > 0)
                tp = RichTextBox.Selection.End;

            for (int i = 1, len = parts.Length; i < len; ++i)
            {
                p = tp.Paragraph;
                if (p is not null) tp = p.ContentEnd;
                tp = tp.InsertParagraphBreak();
                ++lineNumber;
                InsertPart(i);
            }
            SetAutoIndent();
            p = tp.Paragraph;
            if (p is not null)
            {
                tp = tp.Paragraph.ContentEnd;
                RichTextBox.Selection.Select(tp, tp);
            }

            void InsertPart(int i)
            {
                var s = parts[i];
                if (comment && !s.StartsWith('\''))
                    s = '\'' + s;

                tp.InsertTextInRun(s);
                _highlighter.Defined.Get(s, lineNumber);
                _highlighter.Parse(p, IsComplex, lineNumber, i == 1);
            }
        }

        private async Task AutoRun(bool syncScroll = false)
        {
            if (_isParsing)
                return;

            IsCalculated = true;
            if (syncScroll)
                _scrollOutput = true;

            _scrollY = await _wv2Warper.GetScrollYAsync();
            CalculateAsync();
        }

        private void RemoveLine()
        {
            _isTextChangedEnabled = false;
            RichTextBox.BeginChange();
            if (_document.Blocks.Count <= 1)
            {
                _currentParagraph = _document.Blocks.FirstBlock as Paragraph;
                _currentParagraph.Inlines.Clear();
            }
            else
            {
                _document.Blocks.Remove(RichTextBox.Selection.Start.Paragraph);
                _currentParagraph = RichTextBox.Selection.Start.Paragraph;
            }
            _currentLineNumber = GetLineNumber(_currentParagraph);
            HighLighter.Clear(_currentParagraph);
            RichTextBox.EndChange();
            _isTextChangedEnabled = true;
            if (IsAutoRun)
                AutoRun();
        }

        private int _scrollOutputToLine;
        private double _scrollOffset;
        private async void LineClicked(string data)
        {
            if (int.TryParse(data, out var line) && line > 0)
            {
                if (_highlighter.Defined.HasMacros && !IsUnwarpedCode)
                {
                    _scrollOffset = await _wv2Warper.GetVerticalPositionAsync(line);
                    _scrollOutputToLine = line;
                    await _wv2Warper.NavigateToStringAsync(WithThemeClass(_htmlUnwarpedCode));
                    WebViewer.Tag = true;
                    CodeCheckBox.IsChecked = true;
                }
                // Con el editor plegable delante, el salto va AHI: mover el cursor del
                // RichTextBox oculto no se ve, y ademas robaria el foco al editor visible.
                else if (IrALineaEnAvalon(line))
                    return;
                else if (line <= _document.Blocks.Count)
                {
                    var block = _document.Blocks.ElementAt(line - 1);
                    if (!ReferenceEquals(block, _currentParagraph))
                    {
                        var y = block.ContentEnd.GetCharacterRect(LogicalDirection.Forward).Y -
                            _document.ContentStart.GetCharacterRect(LogicalDirection.Forward).Y -
                            await _wv2Warper.GetVerticalPositionAsync(line) +
                            (RichTextBox.Margin.Top - WebViewer.Margin.Top);
                        RichTextBox.ScrollToVerticalOffset(y);
                        RichTextBox.CaretPosition = block.ContentEnd;
                    }
                }
            }
            RichTextBox.Focus();
            Keyboard.Focus(RichTextBox);
        }

        private void LinkClicked(string data)
        {
            RichTextBox.Selection.Text = string.Empty;
            var lines = data.Split(Environment.NewLine);
            var p = RichTextBox.Selection.Start.Paragraph;
            if (lines.Length == 1)
            {
                if ((data[0] == '#' || data[0] == '$') && !p.ContentEnd.IsAtLineStartPosition)
                {
                    var tp = p.ContentEnd.InsertParagraphBreak();
                    RichTextBox.Selection.Select(tp, tp);
                }
                _insertManager.InsertText(data);
            }
            else
            {
                var tp = p.ContentStart;
                _isTextChangedEnabled = false;
                RichTextBox.BeginChange();
                var start = true;
                foreach (var line in lines)
                {
                    if (!p.ContentEnd.IsAtLineStartPosition)
                        p = p.ContentEnd.InsertParagraphBreak().Paragraph;
                    p.Inlines.Add(line);
                    _highlighter.Parse(p, IsComplex, GetLineNumber(p), start);
                    start = false;
                }
                RichTextBox.Selection.Select(tp, tp);
                RichTextBox.EndChange();
                _isTextChangedEnabled = true;
                DispatchAutoIndent();
                Record();
            }
            RichTextBox.Focus();
            Keyboard.Focus(RichTextBox);
        }
        private void CalcButton_Click(object sender, RoutedEventArgs e) => Command_Calculate(null, null);
        private async void Command_Calculate(object sender, ExecutedRoutedEventArgs e)
        {
            if (IsCalculated)
                _scrollY = await _wv2Warper.GetScrollYAsync();

            Calculate();
            if (IsCalculated)
                await _wv2Warper.SetScrollYAsync(_scrollY);
        }

        private void Calculate()
        {
            if (_parser.IsPaused)
                AutoRun();
            else
            {
                IsCalculated = !IsCalculated;
                if (IsWebForm)
                    CalculateAsync(!IsCalculated);
                else if (IsCalculated)
                    CalculateAsync();
                else
                    ShowHelp();
            }
        }

        private void Command_New(object senter, ExecutedRoutedEventArgs e)
        {
            var r = PromptSave();
            if (r == MessageBoxResult.Cancel)
                return;

            if (_isParsing)
                _parser.Cancel();

            _parser.ShowWarnings = true;
            CurrentFileName = string.Empty;
            _document.Blocks.Clear();
            _highlighter.Defined.Clear(IsComplex);
            RichTextBox.CaretPosition = _document.ContentStart;
            if (IsWebForm)
            {
                _mustPromptUnlock = false;
                IsWebForm = false;
                RichTextBox.Focus();
                // Web Form (input form) es de Calcpad — Hekatan no lo usa: mantener oculto.
                WebFormButton.Visibility = Visibility.Collapsed;
                MenuWebForm.Visibility = Visibility.Collapsed;
            }
            ShowHelp();
            SaveButton.Tag = null;
            _undoMan.Reset();
            Record();
            SyncToMathCanvas();
        }

        private void Command_Open(object sender, ExecutedRoutedEventArgs e)
        {
            var r = PromptSave();
            if (r == MessageBoxResult.Cancel)
                return;

            // Calcpad Lab: default extension '.m' (MATLAB script)
            var s = ".m";
            if (!string.IsNullOrWhiteSpace(CurrentFileName))
                s = Path.GetExtension(CurrentFileName).ToLowerInvariant();

            var dlg = new OpenFileDialog
            {
                // Calcpad Lab: MATLAB-only. Default y filtro: .m
                DefaultExt = ".m",
                InitialDirectory = DialogDir,
                CheckFileExists = true,
                Multiselect = false,
                Filter = "MATLAB Script (*.m)|*.m"
            };

            var result = (bool)dlg.ShowDialog();
            if (result)
                FileOpen(dlg.FileName);
        }

        private void Command_Save(object sender, ExecutedRoutedEventArgs e)
        {
            if ((string)SaveButton.Tag == "S" || string.IsNullOrWhiteSpace(CurrentFileName))
                FileSaveAs();
            else
                FileSave(CurrentFileName);
        }

        /// <summary>Carpeta inicial de los dialogos Abrir/Guardar: (1) el dir de trabajo si se uso
        /// `cd 'carpeta'`; (2) la carpeta del archivo abierto; (3) Examples por defecto.</summary>
        private string DialogDir
        {
            get
            {
                var wd = Calcpad.Core.Matlab.MatlabPipeline.UserWorkingDir;
                if (!string.IsNullOrEmpty(wd) && Directory.Exists(wd)) return wd;
                if (File.Exists(CurrentFileName)) return Path.GetDirectoryName(CurrentFileName);
                return DocumentPath;
            }
        }

        /// <summary>Tras correr un script: si el usuario hizo `cd 'ruta\archivo'`, abre ese archivo
        /// (solo interactivo; en --shot no aplica).</summary>
        private void TryOpenRequestedFile()
        {
            var req = Calcpad.Core.Matlab.MatlabPipeline.RequestedOpenFile;
            Calcpad.Core.Matlab.MatlabPipeline.RequestedOpenFile = null;
            if (!string.IsNullOrEmpty(req) && File.Exists(req)
                && !string.Equals(Path.GetFullPath(req),
                                  Path.GetFullPath(string.IsNullOrEmpty(CurrentFileName) ? "." : CurrentFileName),
                                  StringComparison.OrdinalIgnoreCase))
                _ = Dispatcher.InvokeAsync(() => FileOpen(req),
                                           System.Windows.Threading.DispatcherPriority.Background);
        }

        private void ReadSettings()
        {
            ReadRecentFiles();
            var settings = Properties.Settings.Default;
            Real.IsChecked = settings.Numbers == 'R';
            Complex.IsChecked = settings.Numbers == 'C';
            AutoRunCheckBox.IsChecked = settings.AutoRun;
            Deg.IsChecked = settings.Angles == 'D';
            Rad.IsChecked = settings.Angles == 'R';
            Gra.IsChecked = settings.Angles == 'G';
            UK.IsChecked = settings.Units == 'K';
            US.IsChecked = settings.Units == 'S';
            Professional.IsChecked = settings.Equations == 'P';
            Inline.IsChecked = settings.Equations == 'I';
            DecimalsTextBox.Text = settings.Decimals.ToString();
            SubstituteCheckBox.IsChecked = settings.Substitute;
            ExternalBrowserComboBox.SelectedIndex = settings.Browser;
            ZeroSmallMatrixElementsCheckBox.IsChecked = settings.ZeroSmallMatrixElements;
            MaxOutputCountTextBox.Text = settings.MaxOutputCount.ToString();
            if (settings.WindowLeft > 0) Left = settings.WindowLeft;
            if (settings.WindowTop > 0) Top = settings.WindowTop;
            if (settings.WindowWidth > 0) Width = settings.WindowWidth;
            if (settings.WindowHeight > 0) Height = settings.WindowHeight;
            this.WindowState = (WindowState)settings.WindowState;

            ExpressionParser.IsUs = US.IsChecked ?? false;
            var math = _parser.Settings.Math;
            math.FormatEquations = Professional.IsChecked ?? false;
            math.IsComplex = Complex.IsChecked ?? false;
            math.Degrees = Deg.IsChecked ?? false ? 0 :
                           Rad.IsChecked ?? false ? 1 : 2;
            math.Substitute = SubstituteCheckBox.IsChecked ?? false;
            math.ZeroSmallMatrixElements = ZeroSmallMatrixElementsCheckBox.IsChecked ?? false;
            math.MaxOutputCount = int.TryParse(MaxOutputCountTextBox.Text, out int i) ? i : 20;
            var plot = _parser.Settings.Plot;
            plot.ImagePath = string.Empty;
            plot.ImageUri = string.Empty;
            plot.VectorGraphics = false;
            plot.ScreenScaleFactor = _screenScaleFactor;
        }

        private void ReadRecentFiles()
        {
            MenuRecent.Items.Clear();
            var list = Properties.Settings.Default.RecentFileList;
            var j = 0;
            if (list is not null)
            {
                foreach (var fileName in list)
                {
                    if (string.IsNullOrWhiteSpace(fileName) || !File.Exists(fileName))
                        continue;

                    ++j;
                    var menu = new MenuItem()
                    {
                        ToolTip = fileName,
                        Icon = $"   {j}",
                        Header = GetRecentFileName(fileName),
                    };
                    menu.Click += RecentFileList_Click;
                    MenuRecent.Items.Add(menu);
                }
                if (MenuRecent.Items.Count > 0 && (string.IsNullOrEmpty(CurrentFileName) || !File.Exists(CurrentFileName)))
                {
                    var firstMenu = (MenuItem)MenuRecent.Items[0];
                    var path = Path.GetDirectoryName((string)firstMenu.ToolTip);
                    SetCurrentDirectory(path);
                }
            }
            MenuRecent.IsEnabled = j > 0;
            CloneRecentFilesList();
        }

        private static string GetRecentFileName(string fileName) => Path.GetFileName(fileName).Replace("_", "__");

        private void WriteSettings()
        {
            WriteRecentFiles();
            var settings = Properties.Settings.Default;
            settings.Numbers = Real.IsChecked ?? false ? 'R' : 'C';
            settings.AutoRun = AutoRunCheckBox.IsChecked ?? false;
            settings.Angles = Deg.IsChecked ?? false ? 'D' :
                              Rad.IsChecked ?? false ? 'R' : 'G';
            settings.Units = UK.IsChecked ?? false ? 'K' : 'S';
            settings.Equations = Professional.IsChecked ?? false ? 'P' : 'I';
            settings.Decimals = byte.TryParse(DecimalsTextBox.Text, out byte b) ? b : (byte)2;
            settings.Substitute = SubstituteCheckBox.IsChecked ?? false;
            settings.Browser = (byte)ExternalBrowserComboBox.SelectedIndex;
            settings.ZeroSmallMatrixElements = ZeroSmallMatrixElementsCheckBox.IsChecked ?? false;
            settings.MaxOutputCount = int.TryParse(MaxOutputCountTextBox.Text, out int i) ? i : (int)20;
            settings.WindowLeft = Left;
            settings.WindowTop = Top;
            settings.WindowWidth = Width;
            settings.WindowHeight = Height;
            settings.WindowState = (byte)this.WindowState;
            settings.Save();
        }


        private void WriteRecentFiles()
        {
            var n = MenuRecent.Items.Count;
            if (n == 0)
                return;

            var list =
                Properties.Settings.Default.RecentFileList ??
                [];

            list.Clear();
            for (int i = 0; i < n; ++i)
            {
                var menu = (MenuItem)MenuRecent.Items[i];
                var value = (string)menu.ToolTip;
                list.Add(value);
            }

            Properties.Settings.Default.RecentFileList = list;
        }

        private void AddRecentFile(string fileName)
        {
            if (!File.Exists(fileName))
                return;

            var n = MenuRecent.Items.Count;
            for (int i = 0; i < n; ++i)
            {
                var menu = (MenuItem)MenuRecent.Items[i];
                if (!fileName.Equals((string)menu.ToolTip))
                    continue;

                for (int j = i; j > 0; --j)
                {
                    menu = (MenuItem)MenuRecent.Items[j];
                    var previousMenu = (MenuItem)MenuRecent.Items[j - 1];
                    menu.Header = previousMenu.Header;
                    menu.ToolTip = previousMenu.ToolTip;
                }
                var first = (MenuItem)MenuRecent.Items[0];
                first.Header = GetRecentFileName(fileName);
                first.ToolTip = fileName;
                CloneRecentFilesList();
                return;
            }
            if (n >= 9)
            {
                MenuRecent.Items.RemoveAt(n - 1);
                --n;
            }
            var newMenu = new MenuItem()
            {
                ToolTip = fileName,
                Icon = "   1",
                Header = GetRecentFileName(fileName),
            };
            newMenu.Click += RecentFileList_Click;
            MenuRecent.Items.Insert(0, newMenu);
            for (int i = 1; i <= n; ++i)
            {
                var menu = (MenuItem)MenuRecent.Items[i];
                menu.Icon = $"   {i + 1}";
            }
            MenuRecent.IsEnabled = n >= 0;
            CloneRecentFilesList();
        }


        private void CloneRecentFilesList()
        {
            RecentFliesListButton.IsEnabled = MenuRecent.IsEnabled;
            if (!RecentFliesListButton.IsEnabled)
                return;

            RecentFilesListContextMenu.Items.Clear();
            foreach (MenuItem menu in MenuRecent.Items)
            {
                var contextMenuItem = new MenuItem()
                {
                    Header = menu.Header,
                    Icon = menu.Icon,
                    ToolTip = menu.ToolTip,
                };
                contextMenuItem.Click += RecentFileList_Click;
                RecentFilesListContextMenu.Items.Add(contextMenuItem);
            }
        }

        private void RecentFliesListButton_Click(object sender, RoutedEventArgs e)
        {
            RecentFilesListContextMenu.PlacementTarget = RecentFliesListButton;
            RecentFilesListContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Relative;
            var margin = RecentFilesListContextMenu.Margin;
            margin.Left = RecentFliesListButton.Margin.Left;
            RecentFilesListContextMenu.Margin = margin;
            RecentFilesListContextMenu.StaysOpen = true;
            RecentFilesListContextMenu.IsOpen = true;
        }

        private void RecentFileList_Click(object sender, RoutedEventArgs e)
        {
            RecentFilesListContextMenu.IsOpen = false;
            var r = PromptSave();
            if (r == MessageBoxResult.Cancel)
                return;

            var fileName = (string)((MenuItem)sender).ToolTip;
            if (File.Exists(fileName))
                FileOpen(fileName);
        }

        private void Command_SaveAs(object sender, ExecutedRoutedEventArgs e) => FileSaveAs();

        private bool FileSaveAs()
        {
            // Calcpad Lab: MATLAB-only. Default y filtro: .m
            var dlg = new SaveFileDialog
            {
                FileName = Path.GetFileName(CurrentFileName),
                InitialDirectory = DialogDir,
                DefaultExt = ".m",
                OverwritePrompt = true,
                Filter = "MATLAB Script (*.m)|*.m"
            };

            var result = (bool)dlg.ShowDialog();
            if (!result)
                return false;

            var fileName = dlg.FileName;
            _parser.ShowWarnings = true;
            CopyLocalImages(fileName);
            FileSave(fileName);
            AddRecentFile(fileName);
            return true;
        }

        private void CopyLocalImages(string newFileName)
        {
            var images = GetLocalImages(InputText);
            if (images is not null)
            {
                var sourcePath = Path.GetDirectoryName(CurrentFileName);
                var targetPath = Path.GetDirectoryName(newFileName);
                if (sourcePath != targetPath && Directory.Exists(targetPath))
                {
                    var sourceParent = Directory.GetDirectoryRoot(sourcePath);
                    var targetParent = Directory.GetDirectoryRoot(targetPath);
                    if (!string.Equals(sourceParent, sourcePath, StringComparison.OrdinalIgnoreCase))
                        sourceParent = Directory.GetParent(sourcePath).FullName;
                    if (!string.Equals(targetParent, targetPath, StringComparison.OrdinalIgnoreCase))
                        targetParent = Directory.GetParent(targetPath).FullName;
                    var regexString = @"src\s*=\s*""\s*\.\./";
                    for (int i = 0; i < 2; ++i)
                    {
                        foreach (var image in images)
                        {
                            var m = Regex.Match(image, regexString, RegexOptions.IgnoreCase);
                            if (m.Success)
                            {
                                var n = m.Length;
                                var imageFileName = image[n..^1];
                                var imageSourceFile = Path.Combine(sourceParent, imageFileName);
                                if (File.Exists(imageSourceFile))
                                {
                                    var imageTargetFile = Path.Combine(targetParent, imageFileName);
                                    var imageTargetPath = Path.GetDirectoryName(imageTargetFile);
                                    Directory.CreateDirectory(imageTargetPath);
                                    try
                                    {
                                        File.Copy(imageSourceFile, imageTargetFile, true);
                                    }
                                    catch (Exception e)
                                    {
                                        ShowErrorMessage(e.Message);
                                        break;
                                    }
                                }
                            }
                        }
                        regexString = @"src\s*=\s*""\s*\./";
                        if (string.Equals(sourceParent, sourcePath, StringComparison.OrdinalIgnoreCase)
                        && string.Equals(targetParent, targetPath, StringComparison.OrdinalIgnoreCase))
                            return;

                        sourceParent = sourcePath;
                        targetParent = targetPath;
                    }
                }
            }
        }

        private async void FileSave(string fileName)
        {
            if (IsWebForm)
                SetAutoIndent();

            _macroParser.Parse(InputText, out var outputText, null, 0, false);
            var hasInputFields = MacroParser.HasInputFields(outputText);
            if (hasInputFields && IsWebForm)
            {
                if (IsCalculated)
                {
                    CalculateAsync(true);
                    IsCalculated = false;
                    _isSaving = true;
                    return;
                }
                if (!await GetAndSetInputFieldsAsync())
                    return;
            }
            var isZip = string.Equals(Path.GetExtension(fileName), ".cpdz", StringComparison.OrdinalIgnoreCase);
            if (isZip)
            {
                if (hasInputFields)
                    _macroParser.Parse(InputText, out outputText, null, 0, false);

                WriteFile(fileName, outputText, true);
                FileOpen(fileName);
            }
            else
            {
                var newText = GetInputText();
                // Round-trip protection: if the user has not actually typed
                // anything since load, write the ORIGINAL bytes from disk
                // verbatim. Otherwise the highlighter's reconstruction may
                // strip whitespace around inline #deq directives or drop the
                // leading apostrophe of a #blk cell — corrupting the file
                // even though the user just opened it.
                if (!_userTypedSinceLoad && _loadedFileText != null
                    && string.Equals(fileName, CurrentFileName, StringComparison.OrdinalIgnoreCase))
                {
                    WriteFile(fileName, _loadedFileText);
                }
                else
                {
                    WriteFile(fileName, newText);
                    // After an explicit save the in-memory state IS the new disk
                    // contents — refresh the snapshot so a second save without
                    // edits stays a no-op.
                    _loadedFileText = newText;
                    _userTypedSinceLoad = false;
                }
                CurrentFileName = fileName;
            }
            SaveButton.Tag = null;
            IsSaved = true;
        }

        private void Command_Help(object sender, ExecutedRoutedEventArgs e)
        {
            // Antes intentaba abrir readme.html en un programa EXTERNO (ShellExecute) → daba error
            // si la asociación de archivo fallaba. Ahora muestra la guía DENTRO del panel Output
            // (help.html, ya tematizada dark/gold), que es el comportamiento esperado del botón Help.
            ShowHelp();
        }

        private void Command_Close(object sender, ExecutedRoutedEventArgs e) => Application.Current.Shutdown();

        private void Command_Copy(object sender, ExecutedRoutedEventArgs e)
        {
            if (_isWebView2Focused)
                WebViewer.ExecuteScriptAsync("document.execCommand('copy');");
            else
                RichTextBox.Copy();
        }

        private void Command_Paste(object sender, ExecutedRoutedEventArgs e)
        {
            if (_isWebView2Focused)
                WebViewer.CoreWebView2.ExecuteScriptAsync($"var input = document.activeElement; input.setRangeText('{Clipboard.GetText()}', input.selectionStart, input.selectionEnd, 'end');");
            else if(InputFrame.Visibility == Visibility.Visible)
            {
                RichTextBox.Paste();
                RichTextBox.Focus();
                Keyboard.Focus(RichTextBox);
            }
        }

        private void Command_Undo(object sender, ExecutedRoutedEventArgs e)
        {
            // Con el editor plegable, deshace EL: tiene su propia pila y es la que conoce lo
            // que acabas de escribir. La del Lab (_undoMan) guarda el documento entero por
            // pasos gruesos; mezclarlas deshace de mas.
            if (DeshacerEnAvalon(rehacer: false)) return;
            if (_undoMan.Undo())
                RestoreUndoData();
        }

        private void Command_Redo(object sender, ExecutedRoutedEventArgs e)
        {
            if (DeshacerEnAvalon(rehacer: true)) return;
            if (_undoMan.Redo())
                RestoreUndoData();
        }

        private void Command_Print(object sender, ExecutedRoutedEventArgs e)
        {
            if (!_isParsing)
                _wv2Warper.PrintPreviewAsync();
        }

        private void Command_Find(object sender, ExecutedRoutedEventArgs e) =>
            CommandFindReplace(FindReplace.Modes.Find);

        private void Command_Replace(object sender, ExecutedRoutedEventArgs e) =>
            CommandFindReplace(FindReplace.Modes.Replace);

        private async void CommandFindReplace(FindReplace.Modes mode)
        {
            // BUSCAR (Ctrl+F): el buscador clasico trabaja sobre el RichTextBox, que con el
            // editor plegable delante no se ve; AvalonEdit trae el suyo y resalta TODAS las
            // coincidencias. REEMPLAZAR (Ctrl+H) sigue con el dialogo clasico: el panel de
            // AvalonEdit no reemplaza, y el clasico funciona porque escribe en el RichTextBox
            // oculto y el texto vuelve al editor por SincronizarHaciaAvalon.
            if (!_isWebView2Focused && mode == FindReplace.Modes.Find && BuscarEnAvalon(false)) return;

            if (_isWebView2Focused)
                _findReplace.Mode = FindReplace.Modes.Find;
            else
                _findReplace.Mode = mode;

            string s = _isWebView2Focused ?
                await _wv2Warper.GetSelectedTextAsync() :
                RichTextBox.Selection.Text;

            if (!(string.IsNullOrEmpty(s) || s.Contains(Environment.NewLine)))
                _findReplace.SearchString = s;

            if (_findReplaceWindow is null || !_findReplaceWindow.IsVisible)
                _findReplaceWindow = new()
                {
                    Owner = this,
                    FindReplace = _findReplace
                };
            else
                _findReplaceWindow.Hide();

            bool isSelection = s is not null && s.Length > 5;
            _findReplaceWindow.SelectionCheckbox.IsEnabled = isSelection;
            _isTextChangedEnabled = false;
            _findReplaceWindow.Show();
        }

        private void Command_FindNext(object sender, ExecutedRoutedEventArgs e) =>
            _findReplace.Find();

        private void FileOpen(string fileName)
        {
            if (_isParsing)
                _parser.Cancel();

            var ext = Path.GetExtension(fileName).ToLowerInvariant();
            CurrentFileName = fileName;

            var hasForm = GetInputTextFromFile();
            // .m (MATLAB) siempre se abre en layout Code+Output split, nunca en input-form.
            // MacroParser.HasInputFields detecta cualquier '?' como input field, pero en MATLAB
            // '?' aparece legítimamente en strings/comentarios — debe ignorarse.
            if (ext == ".m")
                hasForm = false;
            _parser.ShowWarnings = ext != ".cpdz";
            if (ext == ".cpdz")
            {
                if (IsWebForm)
                    CalculateAsync(true);
                else
                    RunWebForm();
                WebFormButton.Visibility = Visibility.Hidden;
                MenuWebForm.Visibility = Visibility.Collapsed;
                SaveButton.Tag = "S";
            }
            else
            {
                // Web Form es de Calcpad — Hekatan no lo usa: mantener oculto.
                WebFormButton.Visibility = Visibility.Collapsed;
                MenuWebForm.Visibility = Visibility.Collapsed;
                if (hasForm)
                {
                    if (!IsWebForm)
                        RunWebForm();
                    else
                    {
                        IsCalculated = false;
                        CalculateAsync(true);
                    }
                    SaveButton.Tag = "S";
                }
                else
                {
                    if (IsWebForm)
                        IsWebForm = false;
                    else
                    {
                        DispatchLineNumbers();
                        ForceHighlight();
                    }
                    SaveButton.Tag = null;
                    if (IsAutoRun)
                    {
                        IsCalculated = true;
                        CalculateAsync();
                    }
                    else
                    {
                        IsCalculated = false;
                        ShowHelp();
                    }
                }
            }
            _mustPromptUnlock = IsWebForm;
            if (ext != ".tmp")
            {
                IsSaved = true;
                AddRecentFile(CurrentFileName);
            }
            SyncToMathCanvas();
        }

        private MessageBoxResult PromptSave()
        {
            var result = MessageBoxResult.No;
            if (!IsSaved)
                result = MessageBox.Show(MainWindowResources.SavePrompt, "Hekatan Lab", MessageBoxButton.YesNoCancel);
            if (result == MessageBoxResult.Yes)
            {
                if (string.IsNullOrWhiteSpace(CurrentFileName))
                {
                    var success = FileSaveAs();
                    if (!success)
                        return MessageBoxResult.Cancel;
                }
                else
                    FileSave(CurrentFileName);
            }
            return result;
        }

        private void GetMathSettings()
        {
            var mathSettings = _parser.Settings.Math;   
            if (double.TryParse(DecimalsTextBox.Text, out var d))
            {
                var i = (int)Math.Floor(d);
                mathSettings.Decimals = i;
                // "Round" ahora SÍ controla la salida MATLAB (cifras significativas del G-format).
                Calcpad.Core.Matlab.MatlabHtmlWriter.SignificantDigits = i;
                DecimalsTextBox.Text = mathSettings.Decimals.ToString();
                DecimalsTextBox.Foreground = Brushes.Black;
            }
            else
                DecimalsTextBox.Foreground = Brushes.Red;

            if (double.TryParse(MaxOutputCountTextBox.Text, out var m))
            {
                var i = (int)Math.Floor(m);
                mathSettings.MaxOutputCount = i;
                MaxOutputCountTextBox.Text = mathSettings.MaxOutputCount.ToString();
                MaxOutputCountTextBox.Foreground = Brushes.Black;
            }
            else
                MaxOutputCountTextBox.Foreground = Brushes.Red;

            mathSettings.Substitute = SubstituteCheckBox.IsChecked ?? false;
            mathSettings.ZeroSmallMatrixElements = ZeroSmallMatrixElementsCheckBox.IsChecked ?? false;
        }

        private void GetPlotSettings()
        {
            // Hekatan Lab grafica por MatlabPlots (imágenes base64 embebidas), NO por el
            // $Plot nativo de Calcpad. Solo se dejan las rutas de imagen vacías para el
            // ExpressionParser que tipografía las directivas #noc/#val. Los controles de
            // render de Calcpad (paleta/sombras/luz/adaptativo) se eliminaron.
            var plotSettings = _parser.Settings.Plot;
            plotSettings.ImagePath = string.Empty;
            plotSettings.ImageUri = string.Empty;
        }

        private static void ClearTempFolder(string path)
        {
            try
            {
                var dir = new DirectoryInfo(path);
                foreach (var f in dir.GetFiles())
                    f.Delete();
            }
            catch (Exception e)
            {
                ShowErrorMessage(e.Message);
            }
        }

        private async void CalculateAsync(bool toWebForm = false)
        {
            if (_isParsing || _matlabBusy)
                return;
            StartupMark("CalculateAsync: enter");
            GetMathSettings();
            GetPlotSettings();
            if (IsWebForm && !toWebForm && !await GetAndSetInputFieldsAsync())
                return;

            string outputText;
            if (_highlighter.Defined.HasMacros)
            {
                var hasErrors = _macroParser.Parse(InputText, out outputText, null, 0, true);
                outputText = SetImageLocalPath(outputText);
                _htmlUnwarpedCode = hasErrors || DisplayUnwarpedCode ?
                    CodeToHtml(outputText) :
                    string.Empty;
            }
            else
            {
                outputText = SetImageLocalPath(InputText);
                _htmlUnwarpedCode = string.Empty;
            }

            // Pipeline MATLAB default para Calcpad-Lab: buffers nuevos sin guardar
            // y archivos .m usan motor MATLAB. Solo .cpd cae al parser Calcpad-puro.
            bool isMatlabFile = string.IsNullOrEmpty(CurrentFileName) ||
                CurrentFileName.EndsWith(".m", StringComparison.OrdinalIgnoreCase);
            // Hekatan Fortran: los .f90/.f95 corren con el motor Fortran embebido (C# puro,
            // sin compilador externo). Reusa toda la interfaz: editor, autorun y --shot.
            bool isFortranFile = !string.IsNullOrEmpty(CurrentFileName) &&
                (CurrentFileName.EndsWith(".f90", StringComparison.OrdinalIgnoreCase) ||
                 CurrentFileName.EndsWith(".f95", StringComparison.OrdinalIgnoreCase));
            if (isMatlabFile)
            {
                if (_parser.Settings != null && _parser.Settings.Math.Degrees == 0)
                    _parser.Settings.Math.Degrees = 1;
                outputText = MatlabFolderLoader.Load(outputText, CurrentFileName);
            }
            // ── GUARD incremental: si el código NO cambió en nada que afecte el resultado
            //    (solo espacios de más, indentación o líneas en blanco), NO recalcular — el
            //    output actual ya es correcto. Evita recalcular las integrales al tocar un espacio.
            bool ctlRerun = _recalcFromControl;   // Piso 3: este cálculo lo disparó un control
            if (!toWebForm && !IsWebForm && IsCalculated && _lastReportHtml != null &&
                _lastCalcSourceNorm != null && !ctlRerun &&
                NormalizeForCompare(outputText) == _lastCalcSourceNorm)
            {
                StartupMark("Skip recalc: sin cambio semántico (solo whitespace)");
                return;
            }
            _recalcFromControl = false;   // consumir el trigger de control (Piso 3)
            string htmlResult;
            // ── PURE MATLAB pipeline para archivos .m: usar motor MATLAB nativo,
            //    no MatlabPreprocessor (que rompe sintaxis tic, transpose, slicing).
            //    STREAMING: cada statement emite su HTML al WebView2 apenas se computa
            //    via ExecuteScriptAsync(__appendChunk(...)). Mientras se computa una
            //    línea larga, un banner sticky muestra "Calculando línea N..." ──
            if ((isMatlabFile || isFortranFile) && !IsWebForm && !toWebForm)
            {
                StartupMark("MATLAB pure pipeline: start (streaming)");
                _ctlUltimoMotor = isFortranFile ? "fortran" : "matlab";
                _isParsing = true;
                _matlabBusy = true;
                FreezeOutputButtons(true);
                // Construir página de streaming: worksheet header + status banner +
                // output div + JS helpers + footer. Se navega ANTES de arrancar el
                // pipeline, así los chunks van apareciendo en vivo.
                var streamingPage = BuildStreamingPage();
                try
                {
                    if (ctlRerun)
                    {
                        // Piso 3 EN VIVO: re-run por control → NO re-navegar NI limpiar todavía.
                        // Se mantiene el contenido viejo visible y al final se hace UN swap
                        // atómico (__matlabSwap) → sin parpadeo ni banner. Si la página aún no
                        // existe (primer render fue por control), caer a run normal.
                        string ok = "0";
                        try { ok = await WebViewer.ExecuteScriptAsync("(document.getElementById('matlab-output')?1:0)"); }
                        catch { }
                        if (ok != "1")
                        {
                            ctlRerun = false;   // página no lista → streamear normal
                            await _wv2Warper.NavigateToStringAsync(WithThemeClass(streamingPage));
                        }
                    }
                    else
                    {
                        await _wv2Warper.NavigateToStringAsync(WithThemeClass(streamingPage));
                    }
                }
                catch
                {
                    _wv2Warper.Navigate(_htmlParsingPath);
                }
                string pureHtml = null;
                string pureErr = null;
                int pureErrLine = 0;
                var sourceCapture = outputText;
                // === DIAG LOG: registra cada stmt en %TEMP%\calcpad_lab_diag.log
                // para diagnosticar crashes nativos. Usar con `Get-Content` si el WPF
                // se cierra inesperadamente — la ultima linea logueada apunta al stmt
                // que fallo. Se sobreescribe en cada parse. Costo minimo (1 fila/stmt).
                var diagLogPath = Path.Combine(Path.GetTempPath(), "calcpad_lab_diag.log");
                try { File.WriteAllText(diagLogPath, $"=== PARSE START {DateTime.Now:HH:mm:ss.fff} ===\n"); } catch { }
                void DiagLog(string s)
                {
                    try { File.AppendAllText(diagLogPath, $"{DateTime.Now:HH:mm:ss.fff} {s}\n"); } catch { }
                }
                // Pre-parse cleanup: forzar GC + compact LOH para que MatlabPipeline
                // arranque con heap limpio. Reduce el riesgo de corrupcion bajo WPF+
                // WebView2 (FEM scripts grandes generan muchas allocaciones managed).
                // CONDICIONAL (perf): la compactación completa (bloqueante + LOH) solo cuando el heap
                // ya está grande — típico tras un FEM pesado, que es el caso que puede corromper memoria
                // nativa bajo WPF+WebView2. En scripts chicos / edición interactiva se hace un GC LIGERO
                // no bloqueante, para no añadir latencia por tecla (antes se compactaba en CADA re-run).
                long _heapNow = GC.GetTotalMemory(false);
                if (_heapNow > 200L * 1024 * 1024)     // > 200 MB → FEM grande: compactar como antes
                {
                    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    System.Runtime.GCSettings.LargeObjectHeapCompactionMode =
                        System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                    GC.Collect(2, GCCollectionMode.Forced, blocking: true);
                    GC.WaitForPendingFinalizers();
                    DiagLog($"Pre-parse GC done (full compaction, heap={_heapNow / (1024 * 1024)} MB)");
                }
                else
                {
                    GC.Collect(0, GCCollectionMode.Optimized, blocking: false);   // ligero, no bloquea
                    DiagLog($"Pre-parse GC done (light, heap={_heapNow / (1024 * 1024)} MB)");
                }
                // Hint de auto-run: si el .m es solo funciones, invocar la que se llama igual
                // que el archivo (la primaria en MATLAB). Se lee en el hilo UI antes del Task.Run.
                var entryHint = string.IsNullOrWhiteSpace(CurrentFileName)
                    ? null : Path.GetFileNameWithoutExtension(CurrentFileName);
                // Directorio del script → cargar funciones .m hermanas (como MATLAB).
                var scriptDir = string.IsNullOrWhiteSpace(CurrentFileName)
                    ? null : Path.GetDirectoryName(CurrentFileName);
                if (isFortranFile)
                {
                    // El motor Fortran corre en milisegundos: no necesita streaming por statement.
                    await Task.Run(() =>
                    {
                        var fortran = new Calcpad.Core.Fortran.FortranPipeline();
                        var (fh, fe, fel) = fortran.RunLine(sourceCapture);
                        pureHtml = fh; pureErr = fe; pureErrLine = fel;
                        DiagLog($"Fortran pipeline: htmlLen={fh?.Length ?? 0}, err={fe}");
                    });
                    // El WebView2 se pinta con los chunks (igual que MATLAB), NO con el html
                    // final: hay que enviarlo explicitamente o la pagina queda en blanco.
                    if (!string.IsNullOrEmpty(pureHtml))
                    {
                        try
                        {
                            var escFortran = System.Text.Json.JsonSerializer.Serialize(pureHtml);
                            await WebViewer.ExecuteScriptAsync(
                                $"window.__matlabAppendChunk && window.__matlabAppendChunk({escFortran});");
                        }
                        catch { /* WebView2 cerrandose */ }
                    }
                }
                else
                {
                    // Esperar el warmup de arranque (ya completado casi siempre): serializa el
                    // acceso a los caches del JIT (no thread-safe) y deja el primer run real
                    // en ~1-10ms en vez de ~50-80ms.
                    try { await _warmupTask; } catch { }
                    await Task.Run(() =>
                    {
                        var pipeline = new Calcpad.Core.Matlab.MatlabPipeline();
                    pipeline.EntryFunctionHint = entryHint;
                    if (!string.IsNullOrEmpty(scriptDir)) pipeline.SetScriptDirectory(scriptDir, CurrentFileName);
                    pipeline.StreamingMode = true;  // chunks vivos al WebView2
                    pipeline.ControlValues = _controlValues;  // Piso 3: valores vivos de sliders/etc.
                    pipeline.GeomValues = _geomValues;        // Piso 3: geometría dibujada con el cursor (ginput)
                    // Pre-split del source en lineas para mostrar la linea actual en el banner.
                    var sourceLines = sourceCapture.Replace("\r\n", "\n").Split('\n');
                    var parseStart = DateTime.UtcNow;
                    pipeline.StatementStarting += line => DiagLog($"START L{line}");
                    pipeline.StatementCompleted += (line, html) => DiagLog($"DONE  L{line} chunk={html.Length}b");
                    // Wire up streaming events: chunks → WebView2 vía Dispatcher
                    // (ExecuteScriptAsync requiere UI thread).
                    pipeline.StatementStarting += line =>
                        Dispatcher.InvokeAsync(async () =>
                        {
                            if (ctlRerun) return;   // re-run por control: sin banner "Calculando…" (evita el destello)
                            try {
                                var elapsed = (DateTime.UtcNow - parseStart).TotalSeconds;
                                string preview = "";
                                if (line >= 1 && line <= sourceLines.Length)
                                {
                                    preview = sourceLines[line - 1].Trim();
                                    if (preview.Length > 70) preview = preview.Substring(0, 67) + "...";
                                }
                                // Tiempo acumulado solo para runs lentos (≥0.5s): en runs rápidos
                                // el banner no muestra "X ms" para no parecer lento sin serlo.
                                var label = elapsed < 0.5
                                    ? $"L{line} — {preview}"
                                    : elapsed < 60.0
                                        ? $"L{line} ({elapsed:F1}s) — {preview}"
                                        : $"L{line} ({elapsed / 60.0:F1}m) — {preview}";
                                var escapedLabel = System.Text.Json.JsonSerializer.Serialize(label);
                                await WebViewer.ExecuteScriptAsync(
                                    $"window.__matlabSetStatus && window.__matlabSetStatus({escapedLabel});");
                            }
                            catch { /* WebView2 cerrándose, ignorar */ }
                        }, System.Windows.Threading.DispatcherPriority.Background);
                    pipeline.StatementCompleted += (line, html) =>
                        Dispatcher.InvokeAsync(async () =>
                        {
                            if (ctlRerun) return;   // re-run por control: NO streamear por statement; swap único al final
                            try
                            {
                                // FRAME de animación (drawnow, marcado con \x01FRAME\x01) → se repinta en
                                // el MISMO lienzo (__matlabReplaceFrame). El resto se agrega normal.
                                bool isFrame = html != null && html.StartsWith("@@LABFRAME@@", StringComparison.Ordinal);
                                var payload = isFrame ? html.Substring(12) : html;
                                // Animacion WebGL retenida: los frames \x02GLDATA\x02+json solo actualizan
                                // los buffers de la GPU (window.__hkGL), SIN reemplazar el HTML (que
                                // destruiria el contexto WebGL). El primer frame es HTML normal (init).
                                if (isFrame && payload.StartsWith("\x02GLDATA\x02", StringComparison.Ordinal))
                                {
                                    var gljson = System.Text.Json.JsonSerializer.Serialize(payload.Substring(8));
                                    await WebViewer.ExecuteScriptAsync($"window.__hkGL && window.__hkGL({gljson});");
                                    return;
                                }
                                var escaped = System.Text.Json.JsonSerializer.Serialize(payload);
                                var fn = isFrame ? "__matlabReplaceFrame" : "__matlabAppendChunk";
                                await WebViewer.ExecuteScriptAsync(
                                    $"window.{fn} && window.{fn}({escaped});");
                            }
                            catch { /* idem */ }
                        }, System.Windows.Threading.DispatcherPriority.Background);
                    try
                    {
                        DiagLog("Calling pipeline.RunLine");
                        var (h, e, el) = pipeline.RunLine(sourceCapture);
                        DiagLog($"pipeline.RunLine returned: htmlLen={h?.Length ?? 0}, err={e}");
                        pureHtml = h; pureErr = e; pureErrLine = el;
                    }
                    catch (Exception runEx)
                    {
                        DiagLog($"EXCEPTION in pipeline.RunLine: {runEx.GetType().Name}: {runEx.Message}\n{runEx.StackTrace}");
                        pureErr = $"Internal: {runEx.GetType().Name}: {runEx.Message}";
                        pureErrLine = 0;
                    }
                    DiagLog("After Task.Run body");
                });
                }
                DiagLog("After await Task.Run");
                StartupMark($"MATLAB pure pipeline: done (HTML {(pureHtml?.Length ?? 0) / 1024} KB)");
                // Limpiar el banner "Calculando..." y mostrar errores top-level si hay
                try
                {
                    if (pureErr != null)
                    {
                        var errHtml = $"<p class=\"err\">Error on line {pureErrLine}: " +
                            $"{System.Net.WebUtility.HtmlEncode(pureErr)}</p>";
                        var escErr = System.Text.Json.JsonSerializer.Serialize(errHtml);
                        await WebViewer.ExecuteScriptAsync(
                            $"window.__matlabAppendChunk && window.__matlabAppendChunk({escErr});");
                    }
                    await WebViewer.ExecuteScriptAsync(
                        "window.__matlabClearStatus && window.__matlabClearStatus();");
                }
                catch { /* WebView2 cerrándose */ }
                // Piso 3 EN VIVO: re-run por control → UN swap atómico del #matlab-output
                // (no se streameó por statement ni se limpió antes). Construye el HTML nuevo y
                // reemplaza el contenido en una sola operación (re-ejecuta Plotly/__hkt) → sin
                // parpadeo ni banner. El plot se recrea, pero el output no queda en blanco.
                if (ctlRerun && pureErr == null)
                {
                    try
                    {
                        var escSwap = System.Text.Json.JsonSerializer.Serialize(pureHtml ?? "");
                        await WebViewer.ExecuteScriptAsync($"window.__matlabSwap && window.__matlabSwap({escSwap});");
                    }
                    catch { }
                }
                // Persistir HTML final a log + sidecar (mismo comportamiento que antes)
                htmlResult = pureErr != null
                    ? HtmlApplyWorksheet($"<p class=\"err\">Error on line {pureErrLine}: " +
                        $"{System.Net.WebUtility.HtmlEncode(pureErr)}</p>")
                    : HtmlApplyWorksheet(pureHtml ?? "");
                try
                {
                    var logPath = Path.Combine(Path.GetTempPath(), "calcpad_lab_log.html");
                    File.WriteAllText(logPath, htmlResult, Encoding.UTF8);
                    // NO se escribe un .html junto al .m: cada calculo dejaba ~5 MB al
                    // lado del ejemplo (143 MB solo en Examples-Lab). El log de arriba,
                    // en %TEMP%, ya sirve para depurar el render.
                }
                catch { /* log secundario */ }
                _isParsing = false;
                _matlabBusy = false;
                FreezeOutputButtons(false);
                IsCalculated = true;
                _autoRun = false;
                // Modo headless --shot: capturar CUANDO el cálculo terminó de verdad (no a
                // los 7s fijos, que para FEM pesado capturaba en blanco). +settle para que
                // el último frame de animación (drawnow) acabe de pintarse en el WebView2.
                if (_shotPng != null)
                {
                    await Task.Delay(1200);
                    await WaitForPlotsAsync();
                    // Blindaje: si la captura no retorna en 15s (animación viva que nunca
                    // vacía __plotQueue, o WebView2 colgado), salir DURO en vez de colgar 2min.
                    var cap = CaptureWebViewerAndExit(_shotPng);
                    if (await Task.WhenAny(cap, Task.Delay(15000)) != cap)
                    {
                        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "calcpad_lab_shot_timeout.txt"), System.DateTime.Now.ToString()); }
                        catch { }
                        System.Environment.Exit(2);
                    }
                }
                else if (_pdfOut != null)
                {
                    await Task.Delay(1000);
                    try { await WebViewer.CoreWebView2.PrintToPdfAsync(_pdfOut, _wv2Warper.CreatePrintSettings()); } catch { }
                    try { Application.Current.Shutdown(); } catch { }
                }
                else if (_gifDir != null) { await Task.Delay(600); await CaptureFramesAndExit(_gifDir); }
                else TryOpenRequestedFile();   // cd 'ruta\archivo' -> abrir ese archivo
                return; // skip RENDER_OUTPUT — el WebView2 ya tiene todo
            }
            if (!string.IsNullOrEmpty(_htmlUnwarpedCode) && !(IsWebForm || toWebForm))
            {
                WebViewer.Tag = true;
                htmlResult = _htmlUnwarpedCode;
                if (toWebForm)
                    IsWebForm = false;
                OutputFrame.Header = MainWindowResources.Unwarped_code;
                CodeCheckBox.IsChecked = true;
            }
            else
            {
                _parser.Debug = !IsWebForm;
                WebViewer.Tag = false;
                if (toWebForm)
                    _parser.Parse(outputText, false);
                else
                {
                    _isParsing = true;
                    WebFormButton.IsEnabled = false;
                    MenuWebForm.IsEnabled = false;
                    FreezeOutputButtons(true);
                    try
                    {
                        var delayScript = $"setTimeout(function(){{window.location.replace(\"{_htmlParsingUrl}\");}},1000);";
                        await WebViewer.ExecuteScriptAsync(delayScript);
                    }
                    catch
                    {
                        _wv2Warper.Navigate(_htmlParsingPath);
                    }
                    StartupMark("Parse: start (Calcpad-Lab engine)");
                    _ctlUltimoMotor = "calcpad";
                    void parse() => _parser.Parse(outputText);
                    await Task.Run(parse);
                    StartupMark("Parse: done (FEM solve + output HTML)");
                    if (!IsWebForm)
                    {
                        MenuWebForm.IsEnabled = true;
                        WebFormButton.IsEnabled = true;
                    }
                    FreezeOutputButtons(false);
                    IsCalculated = !_parser.IsPaused;
                }
                htmlResult = HtmlApplyWorksheet(FixHref(_parser.HtmlResult));
                SetOutputFrameHeader(IsWebForm);
            }
            RENDER_OUTPUT:
            _autoRun = false;
            try
            {
                if (!string.IsNullOrEmpty(htmlResult))
                {
                    // SIEMPRE escribir el HTML log a un sitio predecible para inspección.
                    // Path: %TEMP%\calcpad_lab_log.html (último render — sobreescribe).
                    // Más: si hay CurrentFileName, también copia a <fileDir>\<filename>.html
                    try
                    {
                        var logPath = Path.Combine(Path.GetTempPath(), "calcpad_lab_log.html");
                        File.WriteAllText(logPath, htmlResult, Encoding.UTF8);
                        // sin sidecar .html junto al .m (ver nota mas arriba)
                    }
                    catch { /* ignore — log es secundario */ }

                    _lastReportHtml = htmlResult;   // cache: re-teñir por tema SIN recalcular el motor
                    _lastCalcSourceNorm = NormalizeForCompare(outputText);  // para saltar recalcs sin cambio
                    await RenderReportHtmlAsync(htmlResult);
                    StartupMark($"Output rendered (HTML: {htmlResult.Length / 1024} KB)");
                }
            }
            catch (Exception e)
            {
                ShowErrorMessage(e.Message);
            }
            if (IsWebForm)
                OutputFrame.Header = toWebForm ? MainWindowResources.Input : MainWindowResources.Output;
            if (_highlighter.Defined.HasMacros && string.IsNullOrEmpty(_htmlUnwarpedCode))
                _htmlUnwarpedCode = CodeToHtml(outputText);
        }

        private void OnPipProgressChanged(string message)
        {
            Dispatcher.InvokeAsync(async () =>
            {
                if (message is not null)
                {
                    Title = $" Installing: {message}";
                    try
                    {
                        var js = $"document.querySelector('p').innerText = 'Installing: {message.Replace("'", "\\'")}';";
                        await WebViewer.ExecuteScriptAsync(js);
                    }
                    catch { }
                }
                else
                    Title = AppInfo.Title;
            });
        }

        private void FreezeOutputButtons(bool freeze)
        {
            var isEnabled = !freeze;
            MenuOutput.IsEnabled = isEnabled;
            CalcButton.IsEnabled = isEnabled;
            PdfButton.IsEnabled = isEnabled;
            WordButton.IsEnabled = isEnabled;
            CopyOutputButton.IsEnabled = isEnabled;
            SaveOutputButton.IsEnabled = isEnabled;
            PrintButton.IsEnabled = isEnabled;
            if (freeze)
                Cursor = Cursors.Wait;
            else
                Cursor = Cursors.Arrow;
        }

        private static string FixHref(string text)
        {
            var s = HtmlAnchorHrefRegex.Replace(text, @"#0"" data-text=""");
            s = HtmlAnchorTargetRegex.Replace(s, "");
            return s;
        }

        private string SetImageLocalPath(string s)
        {
            if (string.IsNullOrWhiteSpace(CurrentFileName))
                return s;

            var path = Path.GetDirectoryName(CurrentFileName);
            var s1 = s;
            var parent = Directory.GetDirectoryRoot(path);
            if (!string.Equals(parent, path, StringComparison.OrdinalIgnoreCase))
            {
                parent = Directory.GetParent(path).FullName;
                parent = "file:///" + parent.Replace('\\', '/');
                s1 = HtmlImgPrevRegex.Replace(s, @"src=""" + parent);
            }
            path = "file:///" + path.Replace('\\', '/');
            var s2 = HtmlImgCurRegex.Replace(s1, @"src=""" + path);
            return s2;
        }

        private string CodeToHtml(string code)
        {
            var ErrorString = AppMessages.ErrorString;
            var highlighter = new HighLighter();
            var errors = new Queue<int>();
            _stringBuilder.Clear();
            _stringBuilder.Append(_htmlSource);
            var lines = code.EnumerateLines();
            _stringBuilder.AppendLine("<div class=\"code\">");
            highlighter.Defined.Get(lines, IsComplex);
            var indent = 0.0;
            var lineNumber = 0;
            foreach (var line in lines)
            {
                ++lineNumber;
                var i = line.IndexOf('\v');
                var lineText = i < 0 ? line : line[..i];
                var sourceLine = i < 0 ? lineNumber.ToString() : line[(i + 1)..];
                _stringBuilder.Append($"<p class=\"line-text\" id=\"line-{lineNumber}\"><a class=\"line-num\" href=\"#0\" data-text=\"{sourceLine}\" title=\"Source line {sourceLine}\">{lineNumber}</a>");
                if (line.StartsWith(ErrorString))
                {
                    errors.Enqueue(lineNumber);
                    _stringBuilder.Append($"<span class=\"error\">{lineText}</span>");
                }
                else
                {
                    var p = new Paragraph();
                    highlighter.Parse(p, IsComplex, lineNumber, true, lineText.ToString());
                    if (!UpdateIndent(p, ref indent))
                        p.TextIndent = indent;

                    var steps = 4 * p.TextIndent / AutoIndentStep;
                    for (int j = 0; j < steps; ++j)
                        _stringBuilder.Append("&nbsp;");

                    foreach (var inline in p.Inlines)
                    {
                        if (inline is not Run r)
                            continue;

                        var cls = HighLighter.GetCSSClassFromColor(r.Foreground);
                        if (r.Background is SolidColorBrush brush && 
                            brush.Color.R > brush.Color.G)
                                cls = "error";

                        var htmlEncodedText = HttpUtility.HtmlEncode(r.Text);
                        if (string.IsNullOrEmpty(cls))
                            _stringBuilder.Append(htmlEncodedText);
                        else
                            _stringBuilder.Append($"<span class=\"{cls}\">{htmlEncodedText}</span>");
                    }
                }
                _stringBuilder.Append("</p>");
            }
            _stringBuilder.Append("</div>");
            if (errors.Count != 0 && lineNumber > 30)
            {
                _stringBuilder.AppendLine(string.Format(MainWindowResources.Found_Errors_In_Modules_And_Macros, errors.Count));
                var count = 0;
                while (errors.Count != 0 && ++count < 20)
                {
                    var line = errors.Dequeue();
                    _stringBuilder.Append($" <span class=\"roundBox\" data-line=\"{line}\">{line}</span>");
                }
                if (errors.Count > 0)
                    _stringBuilder.Append(" ...");

                _stringBuilder.Append("</div>");
                _stringBuilder.AppendLine("<style>body {padding-top:1.1em;}</style>");
            }
            _stringBuilder.Append("</body></html>");
            return _stringBuilder.ToString();
        }

        private static string[] GetLocalImages(string s)
        {
            MatchCollection matches = HtmlImgAnyRegex.Matches(s);
            var n = matches.Count;
            if (n == 0)
                return null;

            string[] images = new string[n];
            for (int i = 0; i < n; ++i)
                images[i] = matches[i].Value;

            return images;
        }

        /// <summary>Replace `<script src="https://calcpad.local/{file}">` with inline content.
        /// Mirrors the CLI Converter.EmbedScript so both binaries produce browser-portable HTML.</summary>
        private static string EmbedScriptInline(string html, string fileName, string docPath)
        {
            var scriptTag = $"<script src=\"https://calcpad.local/{fileName}\"></script>";
            var filePath = Path.Combine(docPath, fileName);
            if (File.Exists(filePath))
            {
                var content = File.ReadAllText(filePath);
                return html.Replace(scriptTag, $"<script>{content}</script>");
            }
            return html;
        }

        private string HtmlApplyWorksheet(string s)
        {
            _stringBuilder.Clear();
            var ssf = Math.Round(0.9 * Math.Sqrt(_screenScaleFactor), 2).ToString(CultureInfo.InvariantCulture);
            _stringBuilder.Append(_htmlWorksheet.Replace("var(--screen-scale-factor)", ssf));
            _stringBuilder.Append(s);
            if (_scrollY > 0)
            {
                _stringBuilder.Append($"<script>window.onload = function() {{ window.scrollTo(0, {_scrollY}); }};</script>");
                _scrollY = 0;
            }
            _stringBuilder.Append(" </body></html>");
            return _stringBuilder.ToString();
        }

        // Construye la página inicial para streaming progresivo del pipeline MATLAB.
        // Estructura: worksheet header + <div id="matlab-status"> (banner sticky) +
        // <div id="matlab-output"> (donde se appendean los chunks) + JS helpers.
        // Los chunks son inyectados desde C# via WebViewer.ExecuteScriptAsync.
        private string BuildStreamingPage()
        {
            var ssf = Math.Round(0.9 * Math.Sqrt(_screenScaleFactor), 2).ToString(CultureInfo.InvariantCulture);
            var sb = new StringBuilder(_htmlWorksheet.Length + 2048);
            sb.Append(_htmlWorksheet.Replace("var(--screen-scale-factor)", ssf));
            sb.Append(@"
<div id=""matlab-status"" style=""position:sticky;top:0;z-index:100;background:#fff3cd;border-bottom:1px solid #ffc107;padding:6px 12px;font-size:0.9em;color:#664d03;font-family:sans-serif"">
  <span class=""spinner"" style=""display:inline-block;width:0.8em;height:0.8em;border:2px solid #664d03;border-top-color:transparent;border-radius:50%;animation:spin 0.8s linear infinite;vertical-align:middle;margin-right:6px""></span>
  <span id=""matlab-status-text"">Iniciando…</span>
</div>
<style>@keyframes spin{from{transform:rotate(0)}to{transform:rotate(360deg)}}</style>
<div id=""matlab-output""></div>
<script>
  (function(){
    var status = document.getElementById('matlab-status');
    var statusText = document.getElementById('matlab-status-text');
    var output = document.getElementById('matlab-output');
    // ── Piso 3: controles interactivos en una barra PERSISTENTE (#hkt-controls),
    //    fuera de #matlab-output (que se limpia en cada re-run por control). El
    //    slider usa 'input' → recalcula EN VIVO mientras se arrastra (como MATLAB).
    //    Si el control ya existe no se recrea (no corta el arrastre); solo se
    //    actualiza su valor cuando NO está enfocado (cambios programáticos/CLI).
    window.__hkt = function(spec){
      var bar=document.getElementById('hkt-controls');
      if(!bar){bar=document.createElement('div');bar.id='hkt-controls';bar.style.cssText='padding:6px 4px;margin-bottom:6px;border-bottom:1px solid rgba(0,0,0,.12)';var host=document.getElementById('matlab-output');host.parentNode.insertBefore(bar,host);}
      if(!document.getElementById('hktstyle')){var st=document.createElement('style');st.id='hktstyle';st.textContent='input.hktrange{-webkit-appearance:none;appearance:none;height:16px;background:transparent;cursor:pointer}input.hktrange::-webkit-slider-runnable-track{height:15px;background:#d8d8d8;border:1px solid #9a9a9a;border-radius:2px}input.hktrange::-webkit-slider-thumb{-webkit-appearance:none;appearance:none;width:32px;height:13px;margin-top:0px;background:linear-gradient(#fbfbfb,#e2e2e2);border:1px solid #7a7a7a;border-radius:2px}';document.head.appendChild(st);}
      var el=document.getElementById(spec.id);
      if(el){var q=el.querySelector('input,select');if(q&&document.activeElement!==q){if(spec.type==='checkbox')q.checked=spec.value!=0;else if(spec.type==='select')q.selectedIndex=spec.value-1;else{q.value=spec.value;var s0=el.querySelector('.hktv');if(s0)s0.textContent=spec.value;}}return;}
      el=document.createElement('div');el.id=spec.id;el.style.cssText='display:inline-block;margin:4px 14px 4px 0;font:14px sans-serif;vertical-align:middle';
      function post(v){if(window.chrome&&window.chrome.webview)window.chrome.webview.postMessage(JSON.stringify({type:'ctrl',name:spec.key,value:v}));}
      if(spec.label&&spec.type!=='checkbox'&&spec.type!=='button'){var lb=document.createElement('span');lb.textContent=spec.label+' ';lb.style.fontWeight='600';el.appendChild(lb);}
      if(spec.type==='range'){var i=document.createElement('input');i.type='range';i.min=spec.min;i.max=spec.max;i.step=spec.step;i.value=spec.value;i.style.cssText='width:240px;vertical-align:middle';var sp=document.createElement('span');sp.className='hktv';sp.textContent=spec.value;sp.style.marginLeft='8px';i.addEventListener('input',function(){sp.textContent=this.value;post(parseFloat(this.value));});el.appendChild(i);el.appendChild(sp);}
      else if(spec.type==='number'){var i=document.createElement('input');i.type='number';i.value=spec.value;i.style.width='110px';i.addEventListener('change',function(){post(parseFloat(this.value)||0);});el.appendChild(i);}
      else if(spec.type==='checkbox'){var l=document.createElement('label');l.style.fontWeight='600';var c=document.createElement('input');c.type='checkbox';c.checked=spec.value!=0;c.addEventListener('change',function(){post(this.checked?1:0);});l.appendChild(c);l.appendChild(document.createTextNode(' '+(spec.label||spec.key)));el.appendChild(l);}
      else if(spec.type==='button'){var b=document.createElement('button');b.textContent=spec.label||spec.key;b.style.cssText='padding:4px 12px;cursor:pointer';b.addEventListener('click',function(){post(1);});el.appendChild(b);}
      else if(spec.type==='select'){var s=document.createElement('select');(spec.options||[]).forEach(function(o,k){var op=document.createElement('option');op.textContent=o;if(k+1==spec.value)op.selected=true;s.appendChild(op);});s.addEventListener('change',function(){post(this.selectedIndex+1);});el.appendChild(s);}
      else{var t=document.createElement('span');t.textContent=spec.label||'';el.appendChild(t);}
      bar.appendChild(el);
    };
    window.__matlabSetStatus = function(label){
      if (!statusText) return;
      // `label` ahora viene formateado desde C# (`L289 (1.2s) — Z = K \ F`)
      // con line#, tiempo transcurrido (ms o s), y preview del source.
      statusText.textContent = 'Calculando ' + label + '…';
    };
    window.__matlabAppendChunk = function(html){
      if (!output) return;
      // insertAdjacentHTML NO ejecuta <script> tags inyectados (seguridad
      // browser). Para que Plotly.newPlot, MathJax y otros runtimes corran,
      // parseamos en un container tmp y re-creamos cada <script> via document
      // .createElement('script') — esos SÍ ejecutan al appendear.
      var tmp = document.createElement('div');
      tmp.innerHTML = html;
      var scripts = tmp.querySelectorAll('script');
      var executables = [];
      scripts.forEach(function(oldScript){
        var newScript = document.createElement('script');
        for (var i = 0; i < oldScript.attributes.length; i++) {
          newScript.setAttribute(oldScript.attributes[i].name, oldScript.attributes[i].value);
        }
        newScript.textContent = oldScript.textContent;
        oldScript.parentNode.replaceChild(newScript, oldScript);
        executables.push(newScript);
      });
      // Re-bindear los lineLinks SOLO en las lineas NUEVAS de este trozo (antes de
      // moverlas). CRITICO: antes se re-escaneaba TODO el documento en cada trozo →
      // O(N²) con muchas iteraciones (el motor se colgaba). Ahora es O(nuevas).
      window.__matlabBindLineLinks && window.__matlabBindLineLinks(tmp);
      // Mover todos los hijos del tmp al output (los <script> re-creados se
      // ejecutan apenas se appendean al DOM).
      while (tmp.firstChild) output.appendChild(tmp.firstChild);
      // Auto-scroll al final si el usuario no scrolleó manualmente arriba
      var nearBottom = (window.innerHeight + window.scrollY) >= (document.body.scrollHeight - 200);
      if (nearBottom) window.scrollTo(0, document.body.scrollHeight);
    };
    // Piso 3 EN VIVO: reemplaza TODO el #matlab-output en UNA operación (clear+fill en la
    // misma ejecución JS → el browser repinta una sola vez, sin blanco intermedio). Re-crea
    // los <script> para que Plotly/__hkt se ejecuten. Usado en re-runs por control (slider).
    window.__matlabSwap = function(html){
      if (!output) return;
      var tmp = document.createElement('div');
      tmp.innerHTML = html;
      tmp.querySelectorAll('script').forEach(function(oldScript){
        var s = document.createElement('script');
        for (var i=0;i<oldScript.attributes.length;i++) s.setAttribute(oldScript.attributes[i].name, oldScript.attributes[i].value);
        s.textContent = oldScript.textContent;
        oldScript.parentNode.replaceChild(s, oldScript);
      });
      window.__matlabBindLineLinks && window.__matlabBindLineLinks(tmp);
      output.innerHTML = '';
      while (tmp.firstChild) output.appendChild(tmp.firstChild);
    };
    // ANIMACIÓN (drawnow): repinta el frame en el MISMO contenedor #labAnimFrame
    // (lo crea si no existe) → se ve la carga subir y la grieta crecer en vivo.
    window.__matlabReplaceFrame = function(html){
      if (!output) return;
      var host = document.getElementById('labAnimFrame');
      if (!host) { host = document.createElement('div'); host.id = 'labAnimFrame'; output.appendChild(host); }
      host.innerHTML = '';
      var tmp = document.createElement('div');
      tmp.innerHTML = html;
      tmp.querySelectorAll('script').forEach(function(oldScript){
        var newScript = document.createElement('script');
        for (var i=0;i<oldScript.attributes.length;i++) newScript.setAttribute(oldScript.attributes[i].name, oldScript.attributes[i].value);
        newScript.textContent = oldScript.textContent;
        oldScript.parentNode.replaceChild(newScript, oldScript);
      });
      while (tmp.firstChild) host.appendChild(tmp.firstChild);
    };
    // Bindea lineLinks en cualquier `<p class=""line"">` que aún no tenga uno.
    // Replica el patrón del template.html (line 1126) pero ejecuta on-demand
    // después de cada chunk inyectado, no solo en $(document).ready.
    window.__matlabBindLineLinks = function(){
      if (typeof jQuery === 'undefined' && typeof $ === 'undefined') return;
      var jq = typeof jQuery !== 'undefined' ? jQuery : $;
      jq('#matlab-output .line:not(style, script)').each(function(){
        var $p = jq(this);
        if ($p.find('> .lineLink').length > 0) return; // ya tiene link
        var idStr = $p.prop('id') || '';
        var idx = idStr.indexOf('-');
        if (idx < 0) return;
        var line = idStr.substring(idx + 1);
        if (!line) return;
        var $lineLink = jq('<a class=""lineLink"" href=""#0"" data-text=""' + line +
          '"" title=""Code line ' + line + '"">&larr;</a>');
        $p.append($lineLink);
        $lineLink.hide();
        $p.hover(function(){
          jq('.lineLink').hide();
          $lineLink.show();
        });
        jq(window).scroll(function(){ $lineLink.hide(); });
      });
    };
    // Click delegation: chrome.webview.postMessage('clicked') para que C# detecte
    // el click sobre cualquier <a> (incluyendo los inyectados luego del ready).
    // El handler original en template.html bindea con $(""a"").click(...) y solo
    // captura los presentes al cargar la página.
    if (typeof chrome !== 'undefined' && chrome.webview && chrome.webview.postMessage) {
      document.body.addEventListener('click', function(ev){
        var t = ev.target;
        while (t && t !== document.body) {
          if (t.tagName === 'A') {
            try { chrome.webview.postMessage('clicked'); } catch(e){}
            return;
          }
          t = t.parentNode;
        }
      }, true);
    }
    window.__matlabClearStatus = function(){
      if (status) status.style.display = 'none';
    };
  })();
</script>
 </body></html>");
            return sb.ToString();
        }

        private void ShowHelp()
        {
            if (!_isParsing)
                _wv2Warper.Navigate(_htmlHelpPath);
        }

        private static string GetHelp(string helpURL)
        {
            var fileName = $"{AppInfo.DocPath}\\help.{_currentCultureName}.html";
            if (!File.Exists(fileName))
                fileName = $"{AppInfo.DocPath}\\help.html";

            return fileName;
        }

        private static string ReadTextFromFile(string fileName)
        {
            try
            {
                if (string.Equals(Path.GetExtension(fileName), ".cpdz", StringComparison.OrdinalIgnoreCase))
                {
                    if (Zip.IsComposite(fileName))
                        return Zip.DecompressWithImages(fileName);

                    var f = new FileInfo(fileName)
                    {
                        IsReadOnly = false
                    };
                    using var fs = f.OpenRead();
                    return Zip.DecompressToString(fs);
                }
                else
                {
                    using var sr = new StreamReader(fileName);
                    return sr.ReadToEnd();
                }
            }
            catch (Exception ex)
            {
                ShowErrorMessage(ex.Message);
                return string.Empty;
            }
        }

        private static SpanLineEnumerator ReadLines(string fileName)
        {
            var lines = new SpanLineEnumerator();
            try
            {
                if (string.Equals(Path.GetExtension(fileName), ".cpdz", StringComparison.OrdinalIgnoreCase))
                {
                    if (Zip.IsComposite(fileName))
                        lines = Zip.DecompressWithImages(fileName).EnumerateLines();
                    else
                    {
                        var f = new FileInfo(fileName)
                        {
                            IsReadOnly = false
                        };
                        using var fs = f.OpenRead();
                        lines = Zip.Decompress(fs);
                    }
                }
                else
                {
                    return ReadTextSmart(fileName).EnumerateLines();
                }
            }
            catch (Exception ex)
            {
                ShowErrorMessage(ex.Message);
            }
            return lines;
        }

        // Detecta encoding del archivo: BOM (UTF-8/UTF-16) → UTF-8 estricto →
        // fallback Windows-1252. Match con el comportamiento de MATLAB R2017a
        // en Windows, que guarda .m en codepage del sistema (CP1252 en ES/EN).
        // Sin esto los bytes legacy (0x97 em-dash, 0xE1 á, 0xF1 ñ, etc.) se
        // mostraban como `�` en el editor.
        private static bool _cp1252Registered;
        private static string ReadTextSmart(string fileName)
        {
            var bytes = File.ReadAllBytes(fileName);
            // 1) BOM detection
            if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
                return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
            if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
                return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
            if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
                return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
            // 2) Probar UTF-8 estricto
            try
            {
                var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false,
                                              throwOnInvalidBytes: true);
                return strict.GetString(bytes);
            }
            catch (DecoderFallbackException)
            {
                // 3) Caer a Windows-1252 (codepage MATLAB R2017a por defecto en Windows)
                if (!_cp1252Registered)
                {
                    Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
                    _cp1252Registered = true;
                }
                return Encoding.GetEncoding(1252).GetString(bytes);
            }
        }

        private static void WriteFile(string fileName, string s, bool zip = false)
        {
            try
            {
                if (zip)
                {
                    var images = GetLocalImages(s);
                    Zip.CompressWithImages(s, images, fileName);
                }
                else
                {
                    using var sw = new StreamWriter(fileName);
                    sw.Write(s);
                }
            }
            catch (Exception ex)
            {
                ShowErrorMessage(ex.Message);
            }
        }

        private bool GetInputTextFromFile()
        {
            var lines = ReadLines(CurrentFileName);
            // Snapshot exact disk text BEFORE any tokenization side-effect so
            // we can detect "no real edit" later and avoid corrupting the file
            // on save.
            try { _loadedFileText = System.IO.File.ReadAllText(CurrentFileName); }
            catch { _loadedFileText = null; }
            // ATAJO Calcpad $Op{} ($Area, $Sum, …): al TECLEAR se transpila con Enter,
            // pero al ABRIR un .m con $Op{} ya escrito el editor lo mostraría crudo.
            // Aquí lo convierto a MATLAB puro al cargar, para que la ventana de edición
            // quede en código MATLAB (integral(...)/arrayfun(...)). El caret aún no toca.
            if (_loadedFileText != null && Calcpad.Core.DollarTranspiler.ContainsMathOp(_loadedFileText))
            {
                var conv = Calcpad.Core.DollarTranspiler.Transpile(_loadedFileText);
                if (!string.Equals(conv, _loadedFileText, StringComparison.Ordinal))
                {
                    _loadedFileText = conv;          // el snapshot pasa a ser el MATLAB mostrado
                    lines = conv.AsSpan().EnumerateLines();
                }
            }
            _userTypedSinceLoad = false;
            _isTextChangedEnabled = false;
            RichTextBox.BeginChange();
            _document.Blocks.Clear();
            SetCodeCheckBoxVisibility();
            _highlighter.Defined.Get(lines, IsComplex);
            var hasForm = false;
            var insideCodeBlock = false;
            foreach (var line in lines)
            {
                ReadOnlySpan<char> s;
                if (line.Contains('\v'))
                {
                    hasForm = true;
                    var n = line.IndexOf('\v');
                    if (n == 0)
                    {
                        SetInputFieldsFromFile(line[1..].EnumerateSplits('\t'));
                        break;
                    }
                    else
                    {
                        SetInputFieldsFromFile(line[(n + 1)..].EnumerateSplits('\t'));
                        s = line[..n];
                    }
                }
                else
                {
                    var trimmed = line.TrimStart('\t').TrimStart();
                    // Track #python/#maxima blocks — don't replace operators inside them
                    if (trimmed.StartsWith("#python", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("#maxima", StringComparison.OrdinalIgnoreCase))
                        insideCodeBlock = true;
                    else if (trimmed.StartsWith("#end python", StringComparison.OrdinalIgnoreCase) ||
                             trimmed.StartsWith("#end maxima", StringComparison.OrdinalIgnoreCase))
                        insideCodeBlock = false;

                    // Skip ReplaceCStyleOperators para .m y buffers nuevos (MATLAB
                    // necesita >=, <=, ~= literales — los chars Unicode ≥/≤/≠ no son
                    // válidos en MATLAB real). Solo .cpd usa Calcpad-puro.
                    bool isMatlabFile = string.IsNullOrEmpty(CurrentFileName) ||
                        CurrentFileName.EndsWith(".m", StringComparison.OrdinalIgnoreCase);
                    if (isMatlabFile ||
                        insideCodeBlock ||
                        trimmed.StartsWith("$Chart", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("$Fem2D", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("$Fem3D", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("$Frame", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("$Struct", StringComparison.OrdinalIgnoreCase) ||
                        trimmed.StartsWith("$Draw", StringComparison.OrdinalIgnoreCase))
                        s = line.TrimStart('\t');
                    else
                        s = ReplaceCStyleOperators(line.TrimStart('\t'));

                    if (!hasForm)
                        hasForm = MacroParser.HasInputFields(s);
                }
                _document.Blocks.Add(new Paragraph(new Run(s.ToString())));
            }
            if (_document.Blocks.Count == 0)
                _document.Blocks.Add(new Paragraph(new Run()));

            var b = _document.Blocks.LastBlock;
            if (b.ContentStart.GetOffsetToPosition(b.ContentEnd) == 0)
                _document.Blocks.Remove(b);

            _currentParagraph = RichTextBox.Selection.Start.Paragraph;
            _currentLineNumber = GetLineNumber(_currentParagraph);
            _undoMan.Reset();
            Record();
            RichTextBox.EndChange();
            _isTextChangedEnabled = true;
            _forceHighlight = true;
            SincronizarHaciaAvalon();          // EDITOR PLEGABLE: el archivo abierto pasa al editor plegable
            return hasForm;
        }

        private string ReplaceCStyleOperators(ReadOnlySpan<char> s)
        {
            if (s.IsEmpty)
                return string.Empty;

            _stringBuilder.Clear();
            var commentEnumerator = s.EnumerateComments();
            foreach (var item in commentEnumerator)
                if (!item.IsEmpty && item[0] != '"' && item[0] != '\'')
                {
                    foreach (var c in item)
                    {
                        var n = _stringBuilder.Length - 1;
                       switch (c)
                        {
                            case '=':
                                if (n < 0)
                                    _stringBuilder.Append(c);
                                else
                                    switch (_stringBuilder[n])
                                    {
                                        case '=': _stringBuilder[n] = '≡'; break;
                                        case '!': _stringBuilder[n] = '≠'; break;
                                        case '>': _stringBuilder[n] = '≥'; break;
                                        case '<': _stringBuilder[n] = '≤'; break;
                                        default: _stringBuilder.Append(c); break;
                                    }
                                break;
                            case '%':
                                ReplaceShortcut('%', '⦼');
                                break;
                            case '&':
                                ReplaceShortcut('&', '∧');
                                break;
                            case '|':
                                ReplaceShortcut('|', '∨');
                                break;
                            case '<':
                                ReplaceShortcut('<', '∠');
                                break;
                            case '*':
                                if (n >= 0 && _stringBuilder[n] == '<')
                                    _stringBuilder[n] = '←';
                                else
                                    _stringBuilder.Append('*');
                                break;
                            default:
                                _stringBuilder.Append(c);
                                break;
                        }
                    }
                }
                else
                    _stringBuilder.Append(item);

            return _stringBuilder.ToString();

            void ReplaceShortcut(char search, char replace)
            {
                var n = _stringBuilder.Length - 1;
                if (n >= 0 && _stringBuilder[n] == search)
                    _stringBuilder[n] = replace;
                else
                    _stringBuilder.Append(search);
            }
        }

        private void Button_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            ResetText();
            DispatchLineNumbers();
            if (IsAutoRun)
                AutoRun();
        }

        private void ResetText()
        {
            _isTextChangedEnabled = false;
            RichTextBox.BeginChange();
            _document.Blocks.Clear();
            _currentParagraph = new Paragraph();
            _currentLineNumber = 1;
            _document.Blocks.Add(_currentParagraph);
            HighLighter.Clear(_currentParagraph);
            RichTextBox.EndChange();
            _isTextChangedEnabled = true;
            SincronizarHaciaAvalon();          // EDITOR PLEGABLE
        }

        const string Tabs = "\t\t\t\t\t\t\t\t\t\t\t\t";
        private string GetInputText()
        {
            _stringBuilder.Clear();
            var b = _document.Blocks.FirstBlock;
            while (b is not null)
            {
                var n = (int)((b as Paragraph).TextIndent / AutoIndentStep);
                if (n > 12)
                    n = 12;
                // Párrafo-imagen (miniatura): su Tag guarda la línea real '% #img data:...base64'.
                // Solo si REALMENTE contiene la imagen (evita el fantasma con Tag heredado al pulsar Enter).
                var line = IsImageParagraph(b)
                    ? (b as Paragraph).Tag as string
                    : new TextRange(b.ContentStart, b.ContentEnd).Text;
                // Convert NO-BREAK SPACE (U+00A0) back to regular space.
                // The highlighter injects nbsp into comment runs so WPF
                // doesn't collapse consecutive spaces in the editor, but
                // the on-disk .cpd / the parser input should use plain ' '.
                if (line.IndexOf(' ') >= 0)
                    line = line.Replace(' ', ' ');
                if (n == 0)
                    _stringBuilder.AppendLine(line);
                else
                    _stringBuilder.AppendLine(Tabs[..n] + line);
                b = b.NextBlock;
            }
            _stringBuilder.RemoveLastLineIfEmpty();
            return _stringBuilder.ToString();
        }

        private async void HtmlFileSave()
        {
            var dlg = new SaveFileDialog
            {
                DefaultExt = ".html",
                Filter = "Html Files (*.html)|*.html",
                FileName = Path.ChangeExtension(Path.GetFileName(CurrentFileName), "html"),
                InitialDirectory = DialogDir,
                OverwritePrompt = true
            };
            var result = (bool)dlg.ShowDialog();
            if (result)
            {
                string html = await _wv2Warper.GetContentsAsync();
                WriteFile(dlg.FileName, html);
                new Process
                {
                    StartInfo = new ProcessStartInfo(dlg.FileName)
                    {
                        UseShellExecute = true
                    }
                }.Start();
            }
        }

        private void CopyOutputButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isParsing)
                _wv2Warper.ClipboardCopyAsync();
        }

        private async void WordButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isParsing) return;
            var isOutput = IsCalculated || IsWebForm || _parser.IsPaused;
            var isDoc = (Professional.IsChecked ?? false) && isOutput;
            var fileExt = isDoc ? "docx" : "html";
            string fileName;
            if (isOutput)
            {
                if (string.IsNullOrEmpty(CurrentFileName))
                    fileName = Path.GetTempPath() + "Calcpad\\Output." + fileExt;
                else
                    fileName = Path.ChangeExtension(CurrentFileName, fileExt);
            }
            else
            {
                fileName = $"{AppInfo.DocPath}\\help.{_currentCultureName}.docx";
                if (!File.Exists(fileName))
                    fileName = $"{AppInfo.DocPath}\\help.docx";
            }
            try
            {
                if (isOutput)
                {
                    if (isDoc)
                    {
                        fileName = PromtSaveDoc(fileName);
                        var logString = await _wv2Warper.ExportOpenXmlAsync(fileName, _parser.OpenXmlExpressions);
                        if (logString.Length > 0)
                        {
                            string message = MainWindowResources.Error_Exporting_Docx_File;
                            if (MessageBox.Show(message, "Hekatan Lab", MessageBoxButton.YesNo, MessageBoxImage.Error) == MessageBoxResult.Yes)
                            {
                                var logFile = fileName + "_validation.log";
                                WriteFile(logFile, logString);
                                RunExternalApp("NOTEPAD", logFile);
                            }
                        }
                    }
                    else
                    {
                        var html = await _wv2Warper.GetContentsAsync();
                        WriteFile(fileName, html);
                    }
                }
                if (RunExternalApp("WINWORD", fileName) is null)
                    RunExternalApp("SOFFICE", fileName);
            }
            catch (Exception ex)
            {
                ShowErrorMessage(ex.Message);
            }
        }

        private static Process RunExternalApp(string appName, string fileName)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = appName
            };
            if (fileName is not null)
                startInfo.Arguments =
                    fileName.Contains(' ') ?
                    '\"' + fileName + '\"' :
                    fileName;

            startInfo.UseShellExecute = true;
            if (appName != "NOTEPAD")
                startInfo.WindowStyle = ProcessWindowStyle.Maximized;

            try
            {
                return Process.Start(startInfo);
            }
            catch
            {
                return null;
            }
        }

        private string PromtSaveDoc(string fileName)
        {
            var dlg = new SaveFileDialog
            {
                FileName = Path.GetFileName(fileName),
                InitialDirectory =
                    File.Exists(CurrentFileName) ? Path.GetDirectoryName(CurrentFileName) : DocumentPath,
                DefaultExt = "docx",
                OverwritePrompt = true,
                Filter = "Microsoft Word Document (*.docx)|*.docx"
            };

            var result = (bool)dlg.ShowDialog();
            return result ? dlg.FileName : fileName;
        }

        private void RestoreUndoData()
        {
            var offset = _undoMan.RestoreOffset;
            var currentLine = _undoMan.RestoreLine;
            var lines = _undoMan.RestoreText.AsSpan().EnumerateLines();
            _highlighter.Defined.Get(lines, IsComplex);
            SetCodeCheckBoxVisibility();
            _isTextChangedEnabled = false;
            RichTextBox.BeginChange();
            var blocks = _document.Blocks;
            int j = 1, n = blocks.Count;
            var indent = 0d;
            var b = blocks.FirstBlock;
            foreach (var line in lines)
            {
                if (j < n)
                {
                    var s = new TextRange(b.ContentStart, b.ContentEnd).Text;
                    if (line.SequenceEqual(s))
                    {
                        if (_currentParagraph == b)
                            _highlighter.Parse(_currentParagraph, IsComplex, j,false);

                        var bp = b as Paragraph;
                        if (!UpdateIndent(bp, ref indent))
                            bp.TextIndent = indent;

                        b = b.NextBlock;
                        ++j;
                        continue;
                    }
                }
                var p = b is not null ? b as Paragraph : new Paragraph();
                _highlighter.Parse(p, IsComplex, j, true, line.ToString());
                if (!UpdateIndent(p, ref indent))
                    p.TextIndent = indent;

                if (b is null)
                    blocks.Add(p);
                else
                    b = b.NextBlock;
                ++j;
            }

            blocks.Remove(blocks.LastBlock);
            while (j < n)
            {
                blocks.Remove(blocks.LastBlock);
                --n;
            }
            n = blocks.Count;
            // Defensa: si la reconstruccion dejo el documento vacio (p.ej. deshacer un
            // pegado de imagen cuyo estado previo era una sola linea vacia), anadir un
            // parrafo para no indexar fuera de rango (ElementAt(-1) -> crash).
            if (n == 0)
            {
                blocks.Add(new Paragraph());
                n = 1;
            }
            if (currentLine < 1)
                currentLine = 1;
            else if (currentLine > n)
                currentLine = n;
            _currentParagraph = blocks.ElementAt(currentLine - 1) as Paragraph;
            _currentLineNumber = currentLine;
            var pointer = HighLighter.FindPositionAtOffset(_currentParagraph, offset);
            RichTextBox.Selection.Select(pointer, pointer);
            HighLighter.Clear(_currentParagraph);
            RichTextBox.EndChange();
            _isTextChangedEnabled = true;
            DispatchLineNumbers();
            if (IsAutoRun)
                AutoRun();
        }

        private void WebFormButton_Click(object sender, RoutedEventArgs e) => RunWebForm();

        private void Command_WebForm(object sender, ExecutedRoutedEventArgs e)
        {
            if (WebFormButton.IsEnabled)
                RunWebForm();
        }

        private void RunWebForm()
        {
            if (IsWebForm && WebFormButton.Visibility != Visibility.Visible)
                return;

            if (_mustPromptUnlock && IsWebForm)
            {
                string message = MainWindowResources.Are_you_sure_you_want_to_unlock_the_source_code_for_editing;
                if (MessageBox.Show(message, "Hekatan Lab", MessageBoxButton.YesNo) == MessageBoxResult.No)
                    return;

                _mustPromptUnlock = false;
            }
            IsWebForm = !IsWebForm;
            IsCalculated = false;
            if (IsWebForm)
                CalculateAsync(true);
            else
            {
                // GetAndSetInputFields();
                RichTextBox.Focus();
                if (IsAutoRun)
                {
                    CalculateAsync();
                    IsCalculated = true;
                }
                else
                    ShowHelp();
            }
        }

        private void SetWebForm(bool value)
        {
            SetButton(WebFormButton, value);
            SetUILock(value);
            if (value)
            {
                InputFrame.Visibility = Visibility.Hidden;
                FramesGrid.ColumnDefinitions[0].Width = new GridLength(0);
                FramesGrid.ColumnDefinitions[1].Width = new GridLength(0);
                WebFormButton.ToolTip = MainWindowResources.Open_source_code_for_editing__F4;
                MenuWebForm.Icon = "  ✓";
                AutoRunCheckBox.Visibility = Visibility.Hidden;
                _findReplaceWindow?.Close();
                IsWebView2Focused = true;
            }
            else
            {
                var cursor = WebViewer.Cursor;
                WebViewer.Cursor = Cursors.Wait;
                DispatchLineNumbers();
                ForceHighlight();
                InputFrame.Visibility = Visibility.Visible;
                FramesGrid.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
                FramesGrid.ColumnDefinitions[1].Width = new GridLength(5);
                FramesGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
                WebFormButton.ToolTip = MainWindowResources.Compile_to_input_form_F4;
                MenuWebForm.Icon = null;
                WebViewer.Cursor = cursor;
                AutoRunCheckBox.Visibility = Visibility.Visible;
                SetOutputFrameHeader(false);
                IsWebView2Focused = false;
            }
        }

        private async Task<bool> GetAndSetInputFieldsAsync()
        {
            if (InputText.Contains("%u", StringComparison.Ordinal))
            {
                try
                {
                    _parser.Settings.Units = await _wv2Warper.GetUnitsAsync();
                }
                catch
                {
                    ShowErrorMessage(MainWindowResources.Error_getting_units);
                }
            }
            else
                _parser.Settings.Units = "m";

            if (!SetInputFields(await _wv2Warper.GetInputFieldsAsync()))
            {
                ShowErrorMessage(MainWindowResources.Error_Invalid_number_Please_correct_and_then_try_again);
                IsCalculated = false;
                WebViewer.Focus();
                return false;
            }
            return true;
        }

        private void SetUnits()
        {
            if (InputText.Contains("%u", StringComparison.OrdinalIgnoreCase))
            {
                try
                {
                    _wv2Warper.SetUnitsAsync(_parser.Settings.Units);
                }
                catch
                {
                    ShowErrorMessage(MainWindowResources.Error_setting_units);
                }
            }
        }

        private void SubstituteCheckBox_Click(object sender, RoutedEventArgs e) => ClearOutput();
        private void DecimalsTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            ClearOutput(false);
            if (IsInitialized && int.TryParse(DecimalsTextBox.Text, out int n))
                DecimalScrollBar.Value = 15 - n;
        }

        private async void ClearOutput(bool focus = true)
        {
            if (IsInitialized)
            {
                if (IsCalculated)
                {
                    IsCalculated = false;
                    if (IsWebForm)
                        CalculateAsync(true);
                    else if (IsAutoRun)
                    {
                        _scrollY = await _wv2Warper.GetScrollYAsync();
                        Calculate();
                    }
                    else
                        ShowHelp();
                }
                if (focus)
                {
                    RichTextBox.Focus();
                    Keyboard.Focus(RichTextBox);
                }
            }
        }

        private void ImageButton_Click(object sender, RoutedEventArgs e)
        {
            var dlg = new OpenFileDialog
            {
                DefaultExt = ".png",
                Filter = "Image Files (*.bmp, *.png, *.gif, *.jpeg *.jpg)|*.bmp; *.png; *.gif; *.jpeg; *.jpg",
                CheckFileExists = true,
                Multiselect = false
            };
            var result = (bool)dlg.ShowDialog();
            if (result)
                InsertImage(dlg.FileName);
        }

        private void InsertImage(string filePath)
        {
            var fileName = Path.GetFileName(filePath);
            var fileDir = Path.GetDirectoryName(filePath);
            string src;
            if (!string.IsNullOrEmpty(CurrentFileName) &&
                string.Equals(Path.GetDirectoryName(CurrentFileName), fileDir, StringComparison.OrdinalIgnoreCase))
                src = fileName;                    // relativo al script (imshow lo resuelve con PrimaryScriptDir)
            else
                src = filePath.Replace('\\', '/'); // absoluto
            var p = new Paragraph();
            // Forma de ARCHIVO: una sola línea, MATLAB puro. (La otra forma, base64 autocontenido con
            // miniatura, es al pegar Ctrl+V.) Antes insertaba '<img> de Calcpad, que daba error en MATLAB.
            p.Inlines.Add(new Run($"imshow('{src}');"));
            _highlighter.Parse(p, IsComplex, GetLineNumber(p), true);
            _document.Blocks.InsertBefore(_currentParagraph ?? _document.Blocks.FirstBlock, p);
        }

        private static Size GetImageSize(string fileName)
        {
            using var imageStream = File.OpenRead(fileName);
            var decoder = BitmapDecoder.Create(imageStream,
                BitmapCreateOptions.IgnoreColorProfile,
                BitmapCacheOption.Default);
            return new Size
            {
                Height = Math.Round(0.75 * decoder.Frames[0].Height),
                Width = Math.Round(0.75 * decoder.Frames[0].Width)
            };
        }

        private void KeyPadButton_Click(object sender, RoutedEventArgs e)
        {
            if (KeyPadGrid.Visibility == Visibility.Hidden)
            {
                KeyPadGrid.Visibility = Visibility.Visible;
                InputGrid.RowDefinitions[1].Height = new GridLength(_inputHeight);
            }
            else
            {
                KeyPadGrid.Visibility = Visibility.Hidden;
                InputGrid.RowDefinitions[1].Height = new GridLength(0);
            }
            SetButton(KeyPadButton, KeyPadGrid.Visibility == Visibility.Visible);
        }

        private void GreekLetter_MouseUp(object sender, MouseButtonEventArgs e)
        {
            var tb = (TextBlock)sender;
            _insertManager.InsertText(tb.Text);
        }

        private void EquationRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            if (IsInitialized)
            {
                var pro = ReferenceEquals(sender, Professional);
                _parser.Settings.Math.FormatEquations = pro;
                Professional.IsChecked = pro;
                Inline.IsChecked = !pro;
            }
            ClearOutput();
        }

        private void AngleRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            if (IsInitialized)
            {
                var deg = ReferenceEquals(sender, Deg) ? 0 :
                          ReferenceEquals(sender, Rad) ? 1 : 2;
                _parser.Settings.Math.Degrees = deg;
                Deg.IsChecked = deg == 0;
                Rad.IsChecked = deg == 1;
                Gra.IsChecked = deg == 2;
            }
            ClearOutput();
        }

        private void ModeRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            if (IsInitialized)
            {
                var complex = ReferenceEquals(sender, Complex);
                _parser.Settings.Math.IsComplex = complex;
                Complex.IsChecked = complex;
                Real.IsChecked = !complex;
                _highlighter.Defined.Get(InputText.AsSpan().EnumerateLines(), IsComplex);
                if (!IsWebForm)
                    Task.Run(() => Dispatcher.InvokeAsync(HighLightAll, DispatcherPriority.Send));
            }
            ClearOutput();
        }

        private void SaveOutputButton_Click(object sender, RoutedEventArgs e)
        {
            if (!_isParsing)
                HtmlFileSave();
        }

        private bool _isDarkTheme = true;   // tema activo (Dark por defecto; Gold = claro cálido)
        // Color de texto por defecto del editor SEGÚN el tema. Reemplaza a
        // Brushes.Black hardcodeado (que en dark dejaba la letra negra/invisible).
        private static readonly Brush _editorTextDark  = FrzBrush(0xE8, 0xE2, 0xD4);  // off-white (ThemeText dark)
        private static readonly Brush _editorTextLight = FrzBrush(0x2B, 0x24, 0x16);  // marrón oscuro (ThemeText gold)
        private Brush EditorDefaultBrush => _isDarkTheme ? _editorTextDark : _editorTextLight;
        private static Brush FrzBrush(byte r, byte g, byte b)
        {
            var br = new SolidColorBrush(Color.FromRgb(r, g, b));
            br.Freeze();
            return br;
        }
        private bool _textMode;             // Modo texto: cada línea nueva arranca con %' (texto visible)
        /// <summary>Espera a que la cola de rasterización de gráficas termine (window.__plotQueue
        /// vacía) antes de capturar con --shot. Máx ~40s.</summary>
        private async Task WaitForPlotsAsync()
        {
            try
            {
                for (int i = 0; i < 200; i++)   // 200 × 200ms = 40s máx
                {
                    var r = await WebViewer.CoreWebView2.ExecuteScriptAsync(
                        "(window.__plotQueue ? window.__plotQueue.length : 0)");
                    if (r == "0" || r == "null" || string.IsNullOrEmpty(r)) { await Task.Delay(300); return; }
                    await Task.Delay(200);
                }
            }
            catch { }
        }

        // Modo headless (--shot/--gif/--wshot/--pdf): sin interacción, sin respawn, sin popups.
        // Evita que el crash-handler re-lance la app y que TryRestoreState abra un MessageBox
        // (que en headless bloquea para siempre) -> huérfanos msedgewebview2 + cuelgues.
        internal static readonly bool IsHeadless =
            System.Environment.GetCommandLineArgs().Any(a =>
                a == "--shot" || a == "--gif" || a == "--wshot" || a == "--pdf" || a == "--tex"
                || a == "--cshot" || a == "--buscar" || a == "--insertar");

        // Piso 3: valores VIVOS de los controles interactivos (slider/numbox/checkbox). Vive en la
        // WPF y sobrevive a re-runs (el motor/pipeline es NUEVO cada cálculo). Se inyecta por run.
        private readonly Dictionary<string, double> _controlValues = new();
        // Piso 3, canal 'geom': geometría dibujada con el cursor (ginput). Por Tag, la matriz de
        // vértices [x,z]. Sobrevive a re-runs (el motor es nuevo cada cálculo) igual que _controlValues.
        private readonly Dictionary<string, double[][]> _geomValues = new();
        private bool _recalcFromControl;              // el próximo cálculo viene de un control → saltar el guard
        private System.Windows.Threading.DispatcherTimer _ctrlDebounce;   // debounce de re-runs por arrastre

        private string _shotPng;   // ruta PNG a capturar si se lanzó con --shot (headless, para tests)
        private string _wshotPng;  // ruta PNG de la VENTANA COMPLETA (chrome+editor) para revisar el tema
        private string _pdfOut;    // ruta PDF headless (--pdf) para verificar que el export sale en BLANCO
        private string _texOut;    // ruta .tex headless (--tex): exporta el .m a LaTeX + PNG externos
        private string _texAssets; // carpeta opcional para los PNG del --tex (default: la del .tex)
        private string _gifDir;    // carpeta donde volcar frames PNG si se lanzó con --gif (animaciones headless)
        private int _gifFrames = 48;
        private int _gifIntervalMs = 100;

        // Renderiza la VENTANA COMPLETA (chrome + menús + editor) a PNG. Para revisar el tema
        // sin abrir la app a mano. El WebView2 (airspace propio) puede salir vacío: da igual,
        // aquí interesa el cromado. Se dispara con --wshot <png>.
        // ── Tema Dark/Gold: cada brush con su valor (dark, gold) ──
        private static readonly (string key, string dark, string gold)[] _themeBrushes = new[]
        {
            ("ThemeWindowBg",     "#141109", "#E9DFC6"),
            ("ThemePanelBg",      "#1C1810", "#F1E9D6"),
            ("ThemeEditorBg",     "#1A1712", "#F8F2E4"),
            ("ThemeText",         "#E8E2D4", "#2B2416"),
            ("ThemeTextMuted",    "#A89F8C", "#6B5E45"),
            ("ThemeAccentRed",    "#E5382B", "#C0392B"),
            ("ThemeAccentGold",   "#E6C463", "#A9820C"),
            ("ThemeButtonBg",     "#262016", "#EFE7D2"),
            ("ThemeButtonBorder", "#3A3226", "#CBBD98"),
            ("ThemeGutterBg",     "#211D16", "#E4DAC0"),
            ("ThemeHoverBg",      "#33E5382B", "#33C0392B"),
        };

        /// <summary>Reasigna los brushes del tema (DynamicResource → se actualiza en vivo).</summary>
        private void SwapThemeBrushes(bool dark)
        {
            var conv = new System.Windows.Media.BrushConverter();
            foreach (var (key, d, g) in _themeBrushes)
            {
                var b = (SolidColorBrush)conv.ConvertFromString(dark ? d : g);
                b.Freeze();
                Resources[key] = b;
            }
        }

        /// <summary>Pone/quita la clase 'dark' en el reporte del WebView2 (el CSS oscuro va
        /// bajo html.dark y SOLO en @media screen → print/PDF siguen en blanco).</summary>
        private void ApplyReportTheme(bool dark)
        {
            try
            {
                string cls = dark ? "dark" : "gold";
                // Los colores de CADA gráfica van INCRUSTADOS en su layout (paper_bgcolor…), no en
                // el CSS: con el .m el reporte se pinta por streaming en el DOM y no hay copia que
                // re-teñir, así que en Oro la gráfica se quedaba con el fondo oscuro. Se re-tiñe EN
                // VIVO: Plotly.relayout en cada gráfica y fill/stroke en los SVG del motor.
                string bg = dark ? "#1a1712" : "#ede4ce";
                string fg = dark ? "#e8e2d4" : "#2b2416";
                string gr = dark ? "#3a3226" : "#cdbf9c";
                string lbg = dark ? "rgba(26,23,18,0.85)" : "rgba(237,228,206,0.85)";
                string js =
                    $"var h=document.documentElement; h.classList.remove('dark','gold'); h.classList.add('{cls}');" +
                    "(function(){try{" +
                    $"var BG='{bg}', FG='{fg}', GR='{gr}', LBG='{lbg}';" +
                    "if (window.Plotly) document.querySelectorAll('.js-plotly-plot').forEach(function(d){" +
                    "  try{ Plotly.relayout(d, {'paper_bgcolor':BG,'plot_bgcolor':BG,'font.color':FG," +
                    "    'xaxis.color':FG,'xaxis.gridcolor':GR,'xaxis.zerolinecolor':GR,'xaxis.linecolor':FG,'xaxis.tickcolor':FG," +
                    "    'yaxis.color':FG,'yaxis.gridcolor':GR,'yaxis.zerolinecolor':GR,'yaxis.linecolor':FG,'yaxis.tickcolor':FG," +
                    "    'legend.bgcolor':LBG,'legend.bordercolor':GR,'legend.font.color':FG," +
                    "    'scene.bgcolor':BG,'scene.xaxis.color':FG,'scene.yaxis.color':FG,'scene.zaxis.color':FG}); }catch(e){}" +
                    "});" +
                    // las gráficas SVG del motor (y el lienzo 3D): fondo y texto
                    "document.querySelectorAll('svg.hk-plot, svg[data-hkplot]').forEach(function(s){" +
                    "  try{ s.style.background=BG; s.querySelectorAll('text').forEach(function(t){t.style.fill=FG;}); }catch(e){}" +
                    "});" +
                    "document.querySelectorAll('canvas[id^=lab3d_]').forEach(function(c){ try{ c.style.background=BG; }catch(e){} });" +
                    "}catch(e){}})();";
                WebViewer?.CoreWebView2?.ExecuteScriptAsync(js);
            }
            catch { }
        }

        // Script de lazy-load: cada gráfica Plotly se renderiza SOLO cuando entra en vista
        // (IntersectionObserver) y se purga al salir → contextos WebGL acotados. Sin esto,
        // 6+ gráficas 3D saturan el WebView2 (solo se pintaban 2-3).
        private const string LazyPlotScript = @"<script>
// Renderiza las gráficas UNA por una: newPlot -> aplica restyle/relayout -> toImage (PNG
// estático) -> purga el contexto WebGL -> siguiente. Así CUALQUIER cantidad de gráficas 3D
// rinde (solo 1 contexto WebGL vivo a la vez); antes 6+ saturaban el WebView2 (salían 2-3).
window.__plotDefs = window.__plotDefs || {};
window.__plotQueue = window.__plotQueue || [];
window.__lazyPlot = function(id, data, layout, config){
  var d = {id:id, data:data, layout:layout, config:config, ops:[]};
  window.__plotDefs[id] = d;
  window.__plotQueue.push(d);
  if(window.__plotQueue.length === 1) setTimeout(window.__renderNext, 0);
};
window.__renderNext = function(){
  if(!window.__plotQueue.length) return;
  var q = window.__plotQueue[0];
  var el = document.getElementById(q.id);
  var next = function(){ window.__plotQueue.shift(); if(window.__plotQueue.length) window.__renderNext(); };
  if(!el || typeof Plotly === 'undefined'){ next(); return; }
  var w = el.style.width || '640px', h = el.style.height || '480px';
  var pw = parseInt(w)||640, ph = parseInt(h)||480;
  var done = false;
  // En timeout NO purgamos: dejamos la gráfica VIVA (se ve igual). Así ninguna
  // queda en blanco; las que sí convierten a imagen liberan su contexto WebGL.
  var finish = function(){ if(done) return; done = true; next(); };
  var timer = setTimeout(finish, 9000);
  Plotly.newPlot(q.id, q.data, q.layout, q.config).then(function(gd){
    q.ops.forEach(function(op){ try{ Plotly[op.fn](q.id, op.a, op.b); }catch(_){} });
    return Plotly.toImage(gd, {format:'png', width:pw*2, height:ph*2});
  }).then(function(url){
    if(done) return; done = true; clearTimeout(timer);
    try{ Plotly.purge(q.id); }catch(_){}
    el.innerHTML = '<img src=""'+url+'"" style=""width:'+pw+'px;height:'+ph+'px;display:block"">';
    next();
  }).catch(function(){ clearTimeout(timer); finish(); });
};
window.__lazyRestyle = function(id,a,b){ var d=window.__plotDefs[id]; if(d){d.ops.push({fn:'restyle',a:a,b:b});} };
window.__lazyRelayout = function(id,a,b){ var d=window.__plotDefs[id]; if(d){d.ops.push({fn:'relayout',a:a,b:b});} };
</script>";

        /// <summary>Convierte las gráficas Plotly del reporte a lazy-load (renderizado al hacer
        /// scroll) para que CUALQUIER cantidad de gráficas 3D rinda sin saturar el WebView2.</summary>
        private static string InjectLazyPlots(string html)
        {
            if (string.IsNullOrEmpty(html) || !html.Contains("Plotly.newPlot(")) return html;
            html = html.Replace("Plotly.newPlot(", "window.__lazyPlot(")
                       .Replace("Plotly.restyle(", "window.__lazyRestyle(")
                       .Replace("Plotly.relayout(", "window.__lazyRelayout(");
            int b = html.LastIndexOf("</body>", StringComparison.OrdinalIgnoreCase);
            if (b >= 0) return html.Substring(0, b) + LazyPlotScript + html.Substring(b);
            return html + LazyPlotScript;
        }

        // HTML crudo del último render (del motor). Permite re-teñir por tema SIN recalcular.
        private string _lastReportHtml = null;

        // Forma NORMALIZADA del código de la última corrida. Si el código nuevo normaliza igual,
        // el resultado es idéntico → no se recalcula. Ver NormalizeForCompare.
        private string _lastCalcSourceNorm = null;

        /// <summary>Reduce el código a una forma canónica que IGNORA lo que NO cambia el resultado:
        /// indentación, líneas en blanco y espacios repetidos ENTRE tokens de código puro. Las
        /// líneas con comillas (strings) o '%' (comentarios, que pueden ser texto visible con %')
        /// se comparan EXACTAS — así nunca se salta un recálculo que sí cambia la salida.
        /// NO borra espacios sueltos: en MATLAB el espacio dentro de [ ] es significativo
        /// ([1 2 3] ≠ [123]); por eso solo COLAPSA runs de espacios, no los elimina.</summary>
        private static string NormalizeForCompare(string src)
        {
            if (string.IsNullOrEmpty(src)) return "";
            var sb = new StringBuilder(src.Length);
            foreach (var raw in src.Split('\n'))
            {
                string line = raw.TrimEnd('\r');
                bool sensitive = line.IndexOf('\'') >= 0 || line.IndexOf('"') >= 0 || line.IndexOf('%') >= 0;
                string canon = sensitive
                    ? line.TrimEnd()                                                   // exacta (solo trim de fin)
                    : System.Text.RegularExpressions.Regex.Replace(line, @"\s+", " ").Trim();  // colapsa espacios
                if (canon.Length == 0) continue;                                       // ignora líneas en blanco
                sb.Append(canon).Append('\n');
            }
            return sb.ToString();
        }

        /// <summary>Navega el WebView al HTML del reporte (string chico o file:/// para grande/SVG).</summary>
        private async System.Threading.Tasks.Task RenderReportHtmlAsync(string htmlResult)
        {
            const int NAV_STRING_LIMIT = 1_000_000; // 1 MB margin (UTF-16 en C# = 2 B/char)
            bool hasLargeSvg = htmlResult.Length > 200_000 &&
                System.Text.RegularExpressions.Regex.IsMatch(htmlResult,
                    @"<svg[^>]*>.{0,500}<line", System.Text.RegularExpressions.RegexOptions.Singleline);
            if (htmlResult.Length > NAV_STRING_LIMIT || hasLargeSvg)
            {
                var tempHtml = Path.Combine(Path.GetTempPath(), $"calcpad_render_{Guid.NewGuid():N}.html");
                File.WriteAllText(tempHtml, htmlResult, Encoding.UTF8);
                var fileUri = "file:///" + tempHtml.Replace("\\", "/");
                _wv2Warper.Navigate(fileUri);
            }
            else
            {
                await _wv2Warper.NavigateToStringAsync(WithThemeClass(InjectLazyPlots(htmlResult)));
            }
        }

        /// <summary>Cambio de tema SIN recalcular: sólo intercambia los colores de tema (fondo/texto/
        /// grid de las gráficas, incrustados en su HTML) y re-navega. El cómputo numérico no se toca.</summary>
        private async void RetintReportForTheme(bool dark)
        {
            if (string.IsNullOrEmpty(_lastReportHtml)) return;
            string h = _lastReportHtml;
            if (dark)   // gold → dark
                h = h.Replace("#ede4ce", "#1a1712").Replace("#2b2416", "#e8e2d4").Replace("#cdbf9c", "#3a3226")
                     .Replace("rgba(237,228,206,0.85)", "rgba(26,23,18,0.85)");   // el fondo de la leyenda
            else        // dark → gold
                h = h.Replace("#1a1712", "#ede4ce").Replace("#e8e2d4", "#2b2416").Replace("#3a3226", "#cdbf9c")
                     .Replace("rgba(26,23,18,0.85)", "rgba(237,228,206,0.85)");
            _lastReportHtml = h;
            try { await RenderReportHtmlAsync(h); } catch { }
        }

        /// <summary>Incrusta la clase de tema (dark/gold) en el &lt;html&gt; del reporte ANTES
        /// de escribirlo. Necesario porque NavigateToStringAsync hace document.write y
        /// reemplaza el documento — la clase puesta por ApplyReportTheme se perdía → reporte
        /// en blanco. Con la clase incrustada, el tema se aplica desde el primer frame.</summary>
        private string WithThemeClass(string html)
        {
            if (string.IsNullOrEmpty(html)) return html;
            string cls = _isDarkTheme ? "dark" : "gold";
            int i = html.IndexOf("<html", StringComparison.OrdinalIgnoreCase);
            if (i >= 0)
                return html.Substring(0, i + 5) + $" class=\"{cls}\"" + html.Substring(i + 5);
            return $"<html class=\"{cls}\">" + html;
        }

        /// <summary>Cambia el tema completo (chrome + sintaxis + reporte) y lo persiste.</summary>
        // Fondo por defecto del WebView2 SEGÚN el tema. Sin esto, al calcular/re-navegar el WebView
        // pinta BLANCO un instante antes de cargar el HTML oscuro → flash blanco molesto en dark.
        private void ApplyWebViewBackground(bool dark)
        {
            try
            {
                WebViewer.DefaultBackgroundColor = dark
                    ? System.Drawing.Color.FromArgb(0x1A, 0x17, 0x12)   // fondo oscuro del reporte
                    : System.Drawing.Color.FromArgb(0xED, 0xE4, 0xCE);  // crema del tema gold
                // help.html (la guía de bienvenida) se tematiza por @media (prefers-color-scheme).
                // Como se navega directo (rutas relativas) NO recibe la clase del tema, así que en gold
                // salía OSCURO. Fijamos el color scheme del WebView para que la media query responda.
                // El reporte usa clase html.dark (no media query) → esto NO lo afecta.
                var core = WebViewer.CoreWebView2;
                if (core?.Profile != null)
                    core.Profile.PreferredColorScheme = dark
                        ? CoreWebView2PreferredColorScheme.Dark
                        : CoreWebView2PreferredColorScheme.Light;
            }
            catch { }
        }

        private void SetTheme(bool dark)
        {
            _isDarkTheme = dark;
            Calcpad.Core.Matlab.MatlabPlots.DarkTheme = dark;   // gráficas oscuras en dark
            SwapThemeBrushes(dark);
            ApplyWebViewBackground(dark);
            HighLighter.ApplyTheme(dark);
            AplicarColoresAvalon(dark);            // EDITOR PLEGABLE: el editor plegable sigue el tema
            // FORZAR re-resaltado de TODO el documento: los Run existentes conservan el Foreground
            // del tema anterior (en gold Const/Function son NEGROS → invisibles al pasar a dark).
            try { _forceHighlight = true; ForceHighlight(); } catch { }
            ApplyReportTheme(dark);
            Properties.Settings.Default.DarkTheme = dark;
            try { Properties.Settings.Default.Save(); } catch { }
            if (ThemeToggleMenuItem != null)
                ThemeToggleMenuItem.Header = dark ? "Theme: Dark  →  Gold" : "Theme: Gold  →  Dark";
            UpdateThemeToggleVisual();
            // El fondo de cada gráfica (paper_bgcolor de Plotly / fill del SVG) va INCRUSTADO en su
            // HTML, no en el CSS del reporte. Se RE-TIÑE el HTML cacheado y se re-navega SIN recalcular
            // el motor (las integrales/meshgrid no cambian con el tema). Mucho más rápido.
            // OJO: antes exigía IsCalculated (el Tag del botón Calcular). Con AutoRun ese Tag no
            // siempre queda en "T", así que al pulsar Oro/Oscuro el reporte NO se re-teñía y las
            // GRÁFICAS se quedaban con los colores del tema anterior (fondo negro en Oro).
            // Basta con tener HTML cacheado y no estar en formulario ni a media compilación.
            if (IsInitialized && !IsWebForm && !_isParsing && !string.IsNullOrEmpty(_lastReportHtml))
            {
                RetintReportForTheme(dark);
            }
        }

        private void ThemeToggle_Click(object sender, RoutedEventArgs e) => SetTheme(!_isDarkTheme);

        // Toggle visible Oscuro | Oro (maqueta)
        private void ThemeSetDark_Click(object sender, RoutedEventArgs e) => SetTheme(true);
        private void ThemeSetGold_Click(object sender, RoutedEventArgs e) => SetTheme(false);

        private static readonly Brush _pillActiveBg = FrzBrush(0xE6, 0xC4, 0x63);   // oro
        private static readonly Brush _pillActiveFg = FrzBrush(0x14, 0x11, 0x09);   // texto oscuro sobre oro
        /// <summary>Resalta el segmento activo del toggle Oscuro|Oro.</summary>
        private void UpdateThemeToggleVisual()
        {
            if (ThemeDarkBtn == null || ThemeGoldBtn == null) return;
            ThemeDarkBtn.Background = _isDarkTheme ? _pillActiveBg : System.Windows.Media.Brushes.Transparent;
            ThemeDarkBtn.Foreground = _isDarkTheme ? _pillActiveFg : (Brush)FindResource("ThemeTextMuted");
            ThemeGoldBtn.Background = !_isDarkTheme ? _pillActiveBg : System.Windows.Media.Brushes.Transparent;
            ThemeGoldBtn.Foreground = !_isDarkTheme ? _pillActiveFg : (Brush)FindResource("ThemeTextMuted");
        }

        // Modo texto: mientras está activo, cada Enter arranca una línea `%'` (texto visible).
        private void TextModeToggle_Click(object sender, RoutedEventArgs e)
        {
            _textMode = (sender as System.Windows.Controls.Primitives.ToggleButton)?.IsChecked ?? !_textMode;
            if (_textMode)
            {
                // Si la línea actual está vacía, prefijarla ya con %'.
                try
                {
                    var p = RichTextBox.Selection?.End.Paragraph;
                    if (p != null && p.ContentStart.GetOffsetToPosition(p.ContentEnd) == 0)
                    {
                        RichTextBox.BeginChange();
                        try { _insertManager.InsertText("%'"); }
                        finally { RichTextBox.EndChange(); }
                    }
                }
                catch { }
            }
            RichTextBox.Focus();
            Keyboard.Focus(RichTextBox);
        }

        // ══ Ventana-loop (Fase 1): notación matemática Σ/∏/∫ → MATLAB ══════
        // Se dispara escribiendo ".loop" en el editor. Muestra un panel en el
        // slot del autocomplete: eliges operador, escribes la notación, eliges
        // la forma (por bucle for / por función), y al Enter inserta MATLAB
        // portable (generado por Calcpad.Core.LoopBuilder).
        private Calcpad.Core.LoopBuilder.Op CurrentLoopOp()
            => LbInt.IsChecked == true ? Calcpad.Core.LoopBuilder.Op.Integral
             : LbProd.IsChecked == true ? Calcpad.Core.LoopBuilder.Op.Product
             : Calcpad.Core.LoopBuilder.Op.Sum;

        private void ShowLoopBuilder()
        {
            try
            {
                var tp = RichTextBox.Selection.Start;
                var rect = tp.GetCharacterRect(LogicalDirection.Forward);
                double x = RichTextBox.Margin.Left + rect.Left - 2;
                double y = RichTextBox.Margin.Top + rect.Bottom;
                if (double.IsInfinity(x) || double.IsInfinity(y)) { x = 160; y = 120; }
                LoopBuilderPanel.Margin = new Thickness(x, y, 0, 0);
            }
            catch { LoopBuilderPanel.Margin = new Thickness(160, 120, 0, 0); }
            AutoCompleteListBox.Visibility = Visibility.Hidden;
            PopulateLoopForms();
            LoopBuilder_OpChanged(null, null);
            LoopBuilderPanel.Visibility = Visibility.Visible;
            LbExpr.Focus();
            LbExpr.SelectAll();
        }

        private void PopulateLoopForms()
        {
            if (LbForm == null) return;
            LbForm.Items.Clear();
            void Add(string label, Calcpad.Core.LoopBuilder.Form f)
                => LbForm.Items.Add(new System.Windows.Controls.ComboBoxItem { Content = label, Tag = f });
            if (CurrentLoopOp() == Calcpad.Core.LoopBuilder.Op.Integral)
            {
                Add("Por función: integral()", Calcpad.Core.LoopBuilder.Form.Function);
                Add("Por función: trapz", Calcpad.Core.LoopBuilder.Form.FunctionTrapz);
                Add("Por función: gaussint", Calcpad.Core.LoopBuilder.Form.FunctionGauss);
                Add("Por bucle: trapecio", Calcpad.Core.LoopBuilder.Form.LoopTrapezoid);
                Add("Por bucle: Simpson", Calcpad.Core.LoopBuilder.Form.LoopSimpson);
                Add("Por bucle: Gauss-Legendre", Calcpad.Core.LoopBuilder.Form.LoopGauss);
            }
            else
            {
                Add("Por bucle for (ves cada iteración)", Calcpad.Core.LoopBuilder.Form.Loop);
                Add("Por función (compacto)", Calcpad.Core.LoopBuilder.Form.Function);
            }
            LbForm.SelectedIndex = 0;
        }

        private void LoopBuilder_OpChanged(object sender, RoutedEventArgs e)
        {
            if (LbSymbol == null) return;
            var op = CurrentLoopOp();
            LbSymbol.Text = Calcpad.Core.LoopBuilder.Symbol(op);
            bool isInt = op == Calcpad.Core.LoopBuilder.Op.Integral;
            if (isInt)
            {
                if (LbVar.Text == "k") LbVar.Text = "x";
                if (LbFrom.Text == "1") LbFrom.Text = "0";
                // La expresión por defecto de suma ("k") no existe en ∫ sobre x → usar x^2.
                if (LbExpr.Text == "k" || LbExpr.Text.Trim().Length == 0) LbExpr.Text = "x^2";
            }
            else
            {
                if (LbVar.Text == "x") LbVar.Text = "k";
                if (LbFrom.Text == "0") LbFrom.Text = "1";
                if (LbExpr.Text == "x^2" || LbExpr.Text.Trim().Length == 0) LbExpr.Text = "k";
            }
            if (sender != null) PopulateLoopForms();  // en init lo llama ShowLoopBuilder
            UpdateLoopPreview();
        }

        private void LoopBuilder_InputChanged(object sender, System.Windows.Controls.TextChangedEventArgs e) => UpdateLoopPreview();
        private void LoopBuilder_FormChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e) => UpdateLoopPreview();

        private void UpdateLoopPreview()
        {
            if (LbPreview == null || LbForm?.SelectedItem == null) return;
            var op = CurrentLoopOp();
            var form = (Calcpad.Core.LoopBuilder.Form)((System.Windows.Controls.ComboBoxItem)LbForm.SelectedItem).Tag;
            LbDx.Text = op == Calcpad.Core.LoopBuilder.Op.Integral ? (" d" + LbVar.Text) : "";
            LbPreview.Text = Calcpad.Core.LoopBuilder.Build(op, LbExpr.Text, LbVar.Text, LbFrom.Text, LbTo.Text, form, null);
        }

        private void LoopBuilder_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter) { LoopBuilder_Insert(null, null); e.Handled = true; }
            else if (e.Key == Key.Escape) { HideLoopBuilder(); e.Handled = true; }
        }

        private void LoopBuilderMenu_Click(object sender, RoutedEventArgs e)
        {
            RichTextBox.Focus();
            ShowLoopBuilder();
        }

        private void LoopBuilder_Cancel(object sender, RoutedEventArgs e) => HideLoopBuilder();
        private void HideLoopBuilder()
        {
            LoopBuilderPanel.Visibility = Visibility.Collapsed;
            RichTextBox.Focus();
            Keyboard.Focus(RichTextBox);
        }

        private void LoopBuilder_Insert(object sender, RoutedEventArgs e)
        {
            string code = LbPreview?.Text ?? "";
            HideLoopBuilder();
            if (string.IsNullOrWhiteSpace(code)) return;
            RichTextBox.Focus();
            RichTextBox.BeginChange();
            try
            {
                var lines = code.Replace("\r\n", "\n").Split('\n');
                for (int i = 0; i < lines.Length; i++)
                {
                    _insertManager.InsertText(lines[i]);
                    if (i < lines.Length - 1) _insertManager.InsertLine();
                }
            }
            finally { RichTextBox.EndChange(); }
        }

        /// <summary>Detecta la palabra "loop" (o ".loop") recién escrita, la borra
        /// y abre la ventana-loop. Exige frontera de palabra antes de "loop" para no
        /// dispararse dentro de identificadores (p.ej. "myloop").</summary>
        private void CheckLoopTrigger()
        {
            try
            {
                var tp = RichTextBox.Selection.Start;
                var p = tp.Paragraph;
                if (p is null) return;
                var before = new TextRange(p.ContentStart, tp).Text;
                string tok = null;
                if (before.EndsWith(".loop", StringComparison.OrdinalIgnoreCase))
                    tok = ".loop";
                else if (before.EndsWith("loop", StringComparison.OrdinalIgnoreCase))
                {
                    int idx = before.Length - 4;          // inicio de "loop"
                    char prev = idx > 0 ? before[idx - 1] : '\0';
                    if (!char.IsLetterOrDigit(prev) && prev != '_') tok = "loop";
                }
                if (tok == null) return;
                var start = tp.GetPositionAtOffset(-tok.Length);
                if (start != null)
                {
                    RichTextBox.BeginChange();
                    try { new TextRange(start, tp).Text = ""; }
                    finally { RichTextBox.EndChange(); }
                }
                ShowLoopBuilder();
            }
            catch { }
        }

        // ── Transpilador $Op{} → MATLAB ────────────────────────────────────
        // Al terminar una línea con notación Calcpad ($Area, $Sum, …) la
        // reescribe EN EL SCRIPT a MATLAB puro (gaussint/arrayfun/fzero…).
        // Hekatan Lab es MATLAB; el $Op es solo un atajo de entrada.
        private bool TranspileParagraph(Paragraph p)
        {
            if (p is null) return false;
            var range = new TextRange(p.ContentStart, p.ContentEnd);
            string text = range.Text;
            if (!DollarTranspiler.ContainsMathOp(text)) return false;
            string outText = DollarTranspiler.Transpile(text);
            if (string.Equals(outText, text, StringComparison.Ordinal)) return false;
            RichTextBox.BeginChange();
            try
            {
                p.Inlines.Clear();
                p.Inlines.Add(new Run(outText) { Foreground = EditorDefaultBrush });
                HighLighter.Clear(p);
                _highlighter.Parse(p, IsComplex, GetLineNumber(p), false);
            }
            finally { RichTextBox.EndChange(); }
            return true;
        }

        /// <summary>Transpila el párrafo que el cursor acaba de dejar al pulsar Enter.
        /// Se agenda tras el split para no interferir con el caret.</summary>
        private void ScheduleTranspileOnEnter(Paragraph leftParagraph)
        {
            if (leftParagraph is null) return;
            Dispatcher.InvokeAsync(() =>
            {
                try { TranspileParagraph(leftParagraph); } catch { }
            }, DispatcherPriority.Background);
        }

        private void CaptureWindowToPng(string path)
        {
            try
            {
                UpdateLayout();
                int w = (int)System.Math.Ceiling(ActualWidth > 0 ? ActualWidth : Width);
                int h = (int)System.Math.Ceiling(ActualHeight > 0 ? ActualHeight : Height);
                if (w <= 0 || h <= 0) return;
                var rtb = new System.Windows.Media.Imaging.RenderTargetBitmap(
                    w, h, 96, 96, System.Windows.Media.PixelFormats.Pbgra32);
                rtb.Render(this);
                var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(rtb));
                using var fs = System.IO.File.Create(path);
                enc.Save(fs);
            }
            catch { }
        }

        // ═══════════════════════════════════════════════════════════════════
        //  CANAL DE CONTROL (--ctl <dir>): servidor por cola de archivos para
        //  manejar el WPF VIVO desde el CLI (Claude). Yo escribo cmd-N.json,
        //  el WPF lo ejecuta y escribe resp-N.json (+ PNG capturado con
        //  PrintWindow, que SÍ captura la superficie nativa del WebView2).
        //  Ops: setctrl{name,value} · run · capture{path} · getoutput · quit.
        // ═══════════════════════════════════════════════════════════════════
        internal static bool IsControlMode =
            System.Environment.GetCommandLineArgs().Any(a => a == "--ctl");
        private string _ctlDir;
        private System.Windows.Threading.DispatcherTimer _ctlTimer;
        private readonly System.Collections.Generic.HashSet<string> _ctlDone =
            new(System.StringComparer.OrdinalIgnoreCase);
        private bool _ctlBusy;
        /// <summary>Con QUE motor se calculo lo ultimo: "matlab" · "fortran" · "calcpad".
        /// Lo lee la op {"op":"state"} de las pruebas. Es la comprobacion de "¿esto es
        /// MATLAB de verdad?": un .m que salga por "calcpad" esta cayendo al parser
        /// heredado (ve el ' de una cadena como comentario y pide un #end).</summary>
        private string _ctlUltimoMotor;

        private const uint PW_RENDERFULLCONTENT = 0x00000002;
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdcBlt, uint nFlags);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern bool GetWindowRect(IntPtr hWnd, out NRECT lpRect);
        [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
        private struct NRECT { public int Left, Top, Right, Bottom; }

        // Captura la VENTANA COMPLETA incluida la superficie nativa del WebView2
        // (RenderTargetBitmap NO puede; PrintWindow con PW_RENDERFULLCONTENT sí).
        private void CaptureWindowNative(string path)
        {
            try
            {
                UpdateLayout();
                var hwnd = new System.Windows.Interop.WindowInteropHelper(this).EnsureHandle();
                if (!GetWindowRect(hwnd, out var r)) return;
                int w = r.Right - r.Left, h = r.Bottom - r.Top;
                if (w <= 0 || h <= 0) return;
                using var bmp = new System.Drawing.Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                using (var g = System.Drawing.Graphics.FromImage(bmp))
                {
                    var hdc = g.GetHdc();
                    try { PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT); }
                    finally { g.ReleaseHdc(hdc); }
                }
                var dir = System.IO.Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(dir)) System.IO.Directory.CreateDirectory(dir);
                bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
            }
            catch { }
        }

        private void StartControlServer()
        {
            // Modo control: forzar ventana a tamaño NORMAL (no minimizada) para que
            // CaptureWindowNative/PrintWindow capture bien (una minimizada da PNG minúsculo).
            try
            {
                this.WindowState = System.Windows.WindowState.Normal;
                if (this.Width < 1200) this.Width = 1400;
                if (this.Height < 700) this.Height = 900;
                this.Activate();
            }
            catch { }
            try { System.IO.Directory.CreateDirectory(_ctlDir); } catch { }
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(_ctlDir, "ready.txt"), System.Environment.ProcessId.ToString()); } catch { }
            _ctlTimer = new System.Windows.Threading.DispatcherTimer { Interval = System.TimeSpan.FromMilliseconds(150) };
            _ctlTimer.Tick += async (s, e) => await CtlPoll();
            _ctlTimer.Start();
        }

        private async System.Threading.Tasks.Task CtlPoll()
        {
            if (_ctlBusy) return;
            string[] cmds;
            try { cmds = System.IO.Directory.GetFiles(_ctlDir, "cmd-*.json"); }
            catch { return; }
            System.Array.Sort(cmds, System.StringComparer.Ordinal);
            foreach (var f in cmds)
            {
                if (_ctlDone.Contains(f)) continue;
                _ctlDone.Add(f);
                _ctlBusy = true;
                try { await CtlExecute(f); } catch { }
                _ctlBusy = false;
            }
        }

        /// <summary>Esperar a que el cálculo TERMINE de verdad antes de contestar el comando.
        /// Dos esperas, no una: primero que ARRANQUE (si se mira _isParsing demasiado pronto
        /// aún vale false y se contestaba al instante), luego que acabe. El tope es largo
        /// porque el PRIMER cálculo de la sesión carga MKL y compila el JIT: ~18 s en frío.
        /// Con el tope viejo (16 s) el `run` contestaba con el banner "Calculando..." puesto,
        /// y el test leía un reporte a medias.</summary>
        private int _ctlPlotBefore = 0;   // foto del contador de gráficas antes del cálculo
        private async System.Threading.Tasks.Task CtlWaitCalc()
        {
            for (int t = 0; t < 20 && !(_isParsing || _matlabBusy); t++) await System.Threading.Tasks.Task.Delay(50);
            for (int t = 0; t < 1500 && (_isParsing || _matlabBusy); t++) await System.Threading.Tasks.Task.Delay(80);
            // settle del render SOLO si el cálculo produjo una gráfica nueva; si no (cells,
            // escalares, matrices sin plot) → 0 ms de espera → run rápido como MATLAB.
            if (Calcpad.Core.Matlab.MatlabPlots.LastPlotId != _ctlPlotBefore)
                await System.Threading.Tasks.Task.Delay(700);
        }

        private async System.Threading.Tasks.Task CtlExecute(string cmdFile)
        {
            string json; try { json = System.IO.File.ReadAllText(cmdFile); } catch { return; }
            string id = System.IO.Path.GetFileNameWithoutExtension(cmdFile);
            if (id.StartsWith("cmd-")) id = id.Substring(4);
            string resp = "{\"ok\":true}";
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                var op = root.GetProperty("op").GetString();
                switch (op)
                {
                    case "setctrl":
                        _controlValues[root.GetProperty("name").GetString()] = root.GetProperty("value").GetDouble();
                        _ctlPlotBefore = Calcpad.Core.Matlab.MatlabPlots.LastPlotId; _recalcFromControl = true; CalculateAsync(); await CtlWaitCalc();
                        break;
                    case "run":
                        _ctlPlotBefore = Calcpad.Core.Matlab.MatlabPlots.LastPlotId; _recalcFromControl = true; CalculateAsync(); await CtlWaitCalc();
                        break;
                    case "settext":   // escribir en el editor de script y recalcular
                        SetInputText(root.GetProperty("text").GetString());
                        ForceHighlight();
                        _ctlPlotBefore = Calcpad.Core.Matlab.MatlabPlots.LastPlotId; _recalcFromControl = true; CalculateAsync(); await CtlWaitCalc();
                        break;
                    case "temaestado":   // diagnóstico: por qué no se re-tiñe el reporte
                        resp = "{\"ok\":true,\"init\":" + (IsInitialized ? "true" : "false")
                             + ",\"calc\":" + (IsCalculated ? "true" : "false")
                             + ",\"webform\":" + (IsWebForm ? "true" : "false")
                             + ",\"parsing\":" + (_isParsing ? "true" : "false")
                             + ",\"html\":" + (_lastReportHtml == null ? "0" : _lastReportHtml.Length.ToString()) + "}";
                        break;
                    case "theme":   // cambiar de tema EN CALIENTE (Oscuro/Oro), como el botón
                        SetTheme(root.GetProperty("dark").GetBoolean());
                        await System.Threading.Tasks.Task.Delay(1200);   // que acabe de re-navegar
                        break;
                    case "capture":
                        await System.Threading.Tasks.Task.Delay(300);
                        CaptureWindowNative(root.GetProperty("path").GetString());
                        break;
                    case "js":   // ejecutar JS en el WebView2 (p.ej. simular arrastre del slider)
                        string jr = "null";
                        try { jr = await WebViewer.ExecuteScriptAsync(root.GetProperty("code").GetString()); }
                        catch { }
                        await CtlWaitCalc();   // por si el JS disparó un re-run (onchange del slider)
                        resp = "{\"ok\":true,\"result\":" + (string.IsNullOrEmpty(jr) ? "null" : jr) + "}";
                        break;
                    case "getoutput":
                        string outText = "\"\"";
                        try { outText = await WebViewer.ExecuteScriptAsync("(document.getElementById('matlab-output')||document.body).innerText"); }
                        catch { }
                        resp = "{\"ok\":true,\"output\":" + (string.IsNullOrEmpty(outText) ? "\"\"" : outText) + "}";
                        break;
                    case "quit":
                        try { System.IO.File.WriteAllText(System.IO.Path.Combine(_ctlDir, "resp-" + id + ".json"), resp); } catch { }
                        System.Environment.Exit(0);
                        break;
                    default:
                        // Operaciones de prueba del EDITOR (MainWindow.Pruebas.cs): null = no es de alli.
                        resp = CtlEditorOp(op, root) ?? "{\"ok\":false,\"error\":\"op desconocida\"}";
                        break;
                }
            }
            catch (System.Exception ex)
            {
                resp = "{\"ok\":false,\"error\":" + System.Text.Json.JsonSerializer.Serialize(ex.Message) + "}";
            }
            try { System.IO.File.WriteAllText(System.IO.Path.Combine(_ctlDir, "resp-" + id + ".json"), resp); } catch { }
        }

        private void TryOpenOnStartup()
        {
            StartupMark("TryOpenOnStartup: start");
            var argv = Environment.GetCommandLineArgs();
            // Extraer "--shot <png>" (captura headless del WebViewer a PNG, sin abrir la
            // ventana visualmente / sin navegador) — el resto de args = el archivo.
            // "--gif <dir> [frames] [intervalMs]" — vuelca N frames PNG numerados a <dir>
            // (tras terminar el cálculo) para ensamblar un GIF de animaciones que corren
            // en vivo en el WebView2 (requestAnimationFrame/setInterval).
            _shotPng = null;
            _wshotPng = null;
            _gifDir = null;
            var fileParts = new System.Collections.Generic.List<string>();
            for (int i = 1; i < argv.Length; i++)
            {
                if (argv[i] == "--shot" && i + 1 < argv.Length) _shotPng = argv[++i];
                else if (argv[i] == "--theme" && i + 1 < argv.Length) i++;   // ya consumido en el ctor
                else if (argv[i] == "--pdf" && i + 1 < argv.Length) _pdfOut = argv[++i];
                else if (argv[i] == "--tex" && i + 1 < argv.Length)
                {
                    _texOut = argv[++i];
                    // Directorio opcional de assets: siguiente token que NO sea otra flag ni un
                    // archivo de script (para no confundirlo con el .m posicional).
                    if (i + 1 < argv.Length && !argv[i + 1].StartsWith("--"))
                    {
                        var nxt = argv[i + 1];
                        var nex = Path.GetExtension(nxt).ToLowerInvariant();
                        if (nex != ".m" && nex != ".cpd" && nex != ".cpdz" && nex != ".f90" && nex != ".f95")
                            _texAssets = argv[++i];
                    }
                }
                else if (argv[i] == "--wshot" && i + 1 < argv.Length)
                {
                    _wshotPng = argv[++i];
                    // Capturar la ventana completa (chrome + editor) tras pintar y salir.
                    Dispatcher.InvokeAsync(async () =>
                    {
                        await Task.Delay(1600);
                        CaptureWindowToPng(_wshotPng);
                        try { Application.Current.Shutdown(); } catch { }
                    }, DispatcherPriority.Background);
                }
                else if (argv[i] == "--gif" && i + 1 < argv.Length)
                {
                    _gifDir = argv[++i];
                    if (i + 1 < argv.Length && int.TryParse(argv[i + 1], out var nf)) { _gifFrames = nf; i++; }
                    if (i + 1 < argv.Length && int.TryParse(argv[i + 1], out var iv)) { _gifIntervalMs = iv; i++; }
                }
                else if (argv[i] == "--ctl" && i + 1 < argv.Length) _ctlDir = argv[++i];
                else if (argv[i] == "--plegar") { /* lo lee PrepararAvalon */ }
                // --completar <prefijo> [--cshot <png>]: escribe el prefijo en el editor y
                // abre el autocompletado, para poder REVISARLO en un PNG. Lo lee PrepararAvalon.
                else if (argv[i] == "--completar" && i + 1 < argv.Length) i++;
                else if (argv[i] == "--cshot" && i + 1 < argv.Length) i++;
                else if (argv[i] == "--aceptar") { /* lo lee PrepararAvalon */ }
                else if (argv[i] == "--buscar" && i + 1 < argv.Length) i++;
                else if (argv[i] == "--insertar" && i + 1 < argv.Length) i++;
                // --marcar <etiqueta> [--doble]: pulsa un boton de marcado (B, I, p...).
                // Lo lee PrepararCapturaMarcado. Si no se saltan aqui, la etiqueta y --doble
                // acaban en fileParts y el archivo NO se abre (se busca "ruta.m --doble ...").
                else if (argv[i] == "--marcar" && i + 1 < argv.Length) i++;
                else if (argv[i] == "--enlinea" && i + 1 < argv.Length) i++;
                else if (argv[i] == "--irlinea" && i + 1 < argv.Length) i++;
                else if (argv[i] == "--doble") { /* lo lee PrepararCapturaMarcado */ }
                else fileParts.Add(argv[i]);
            }
            // Captura headless (--shot/--gif/--pdf): imagen LIMPIA sin datatip de hover (= saveas de MATLAB).
            if (_shotPng != null || _gifDir != null || _pdfOut != null)
                Calcpad.Core.Matlab.MatlabPlots.HeadlessNoHover = true;
            if (_ctlDir != null) StartControlServer();   // canal de control CLI (Piso 3 / pruebas)
            if (fileParts.Count > 0)
            {
                var s = string.Join(" ", fileParts);
                StartupMark($"TryOpenOnStartup: file = {System.IO.Path.GetFileName(s)}");
                if (File.Exists(s))
                {
                    var ex = Path.GetExtension(s).ToLowerInvariant();
                    if (ex == ".cpd" || ex == ".cpdz" || ex == ".m" || ex == ".f90" || ex == ".f95")
                    {
                        _parser.ShowWarnings = ex != ".cpdz";
                        CurrentFileName = s;
                        // Los lenguajes de script (.m MATLAB, .f90 Fortran) SIEMPRE abren en modo
                        // Code+Output split, nunca como input-form.
                        // Para .cpd: respetar header `\v` (form) si existe.
                        var isScript = ex == ".m" || ex == ".f90" || ex == ".f95";
                        // Export headless a LaTeX (--tex): NO necesita WebView. Genera el .tex
                        // (texto + ecuaciones LaTeX + PNG externos) y sale. Solo para .m.
                        if (_texOut != null && ex == ".m")
                        {
                            ExportTexAndExit(s);
                            return;
                        }
                        var hasForm = !isScript && (GetInputTextFromFile() || ex == ".cpdz");
                        if (isScript)
                            GetInputTextFromFile();   // cargar contenido al RichTextBox sin importar header
                        StartupMark("File loaded into RichTextBox");
                        if (hasForm)
                        {
                            RunWebForm();
                            _mustPromptUnlock = true;
                            if (ex == ".cpdz")
                                WebFormButton.Visibility = Visibility.Hidden;
                        }
                        else
                        {
                            // Forzar layout split: Code + Output (no input-form pane).
                            IsWebForm = false;
                            ForceHighlight();
                            StartupMark("ForceHighlight done (syntax highlighter)");
                            IsCalculated = true;
                            // No bloquear: ejecutar FEM directamente; Navigate ocurrirá cuando WebView2 esté listo.
                            Dispatcher.InvokeAsync(async () =>
                            {
                                StartupMark("CalculateAsync: dispatcher fired");
                                // Esperar WebView2 si aún no terminó (idempotente)
                                if (_webViewInitTask != null && !_webViewInitTask.IsCompleted)
                                {
                                    StartupMark("CalculateAsync: awaiting WebView2 init");
                                    await _webViewInitTask;
                                    StartupMark("CalculateAsync: WebView2 ready");
                                }
                                _wv2Warper.NavigateToBlank();
                                // La captura --shot la dispara CalculateAsync al TERMINAR el
                                // cálculo (ver rama streaming), no aquí con un delay fijo.
                                CalculateAsync();
                            }, DispatcherPriority.Background);
                        }
                        AddRecentFile(CurrentFileName);
                        return;
                    }
                }
            }
            ShowHelp();
            DispatchLineNumbers();
        }

        // Export headless a LaTeX (--tex): parsea/ejecuta el .m con el motor MATLAB
        // (para tener los valores calculados) y escribe un .tex self-contained con las
        // figuras como PNG externos junto al .tex. No usa el WebView. Luego cierra.
        private void ExportTexAndExit(string mFile)
        {
            try
            {
                var src = File.ReadAllText(mFile);
                var scriptDir = Path.GetDirectoryName(Path.GetFullPath(mFile));
                var entry = Path.GetFileNameWithoutExtension(mFile);
                var pipeline = new Calcpad.Core.Matlab.MatlabPipeline();
                pipeline.EntryFunctionHint = entry;
                if (!string.IsNullOrEmpty(scriptDir)) pipeline.SetScriptDirectory(scriptDir, mFile);
                pipeline.RunLatex(src, _texOut, _texAssets);
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(Path.Combine(Path.GetTempPath(), "hekatan_tex_error.txt"), ex.ToString()); }
                catch { }
            }
            try { Application.Current.Shutdown(); } catch { }
        }

        // Captura de PÁGINA COMPLETA del WebViewer (WebView2) a PNG vía DevTools
        // Page.captureScreenshot y cierra la app. Modo headless --shot: permite testear
        // el render REAL del WPF sin abrir la ventana visualmente ni usar un navegador.
        private async Task CaptureWebViewerAndExit(string png)
        {
            try
            {
                await WebViewer.EnsureCoreWebView2Async();
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                var wStr = await WebViewer.CoreWebView2.ExecuteScriptAsync("Math.max(document.body.scrollWidth,document.documentElement.scrollWidth)");
                var hStr = await WebViewer.CoreWebView2.ExecuteScriptAsync("Math.max(document.body.scrollHeight,document.documentElement.scrollHeight)");
                int w = (int)double.Parse(wStr, ci), h = (int)double.Parse(hStr, ci);
                var prm = "{\"format\":\"png\",\"captureBeyondViewport\":true,\"clip\":{\"x\":0,\"y\":0,\"width\":" + w + ",\"height\":" + h + ",\"scale\":1}}";
                var res = await WebViewer.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", prm);
                using var jd = System.Text.Json.JsonDocument.Parse(res);
                File.WriteAllBytes(png, Convert.FromBase64String(jd.RootElement.GetProperty("data").GetString()));
            }
            catch { }
            Application.Current.Shutdown();
        }

        // Boton interactivo: exporta el render ACTUAL del WebView2 a un PNG que el usuario
        // elige con un dialogo. Reusa la misma captura de --shot (Page.captureScreenshot,
        // pagina completa), pero SIN cerrar la app. Guarda la imagen limpia del Output.
        private async void ExportPngButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (WebViewer?.CoreWebView2 == null) { await WebViewer.EnsureCoreWebView2Async(); }
                string suggested = "salida.png";
                if (!string.IsNullOrEmpty(CurrentFileName))
                    suggested = Path.GetFileNameWithoutExtension(CurrentFileName) + ".png";
                var dlg = new SaveFileDialog
                {
                    FileName = suggested,
                    DefaultExt = ".png",
                    Filter = "PNG (*.png)|*.png",
                    Title = "Exportar el Output como PNG"
                };
                if (dlg.ShowDialog() != true) return;
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                var wStr = await WebViewer.CoreWebView2.ExecuteScriptAsync("Math.max(document.body.scrollWidth,document.documentElement.scrollWidth)");
                var hStr = await WebViewer.CoreWebView2.ExecuteScriptAsync("Math.max(document.body.scrollHeight,document.documentElement.scrollHeight)");
                int w = (int)double.Parse(wStr, ci), h = (int)double.Parse(hStr, ci);
                var prm = "{\"format\":\"png\",\"captureBeyondViewport\":true,\"clip\":{\"x\":0,\"y\":0,\"width\":" + w + ",\"height\":" + h + ",\"scale\":1}}";
                var res = await WebViewer.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", prm);
                using var jd = System.Text.Json.JsonDocument.Parse(res);
                File.WriteAllBytes(dlg.FileName, Convert.FromBase64String(jd.RootElement.GetProperty("data").GetString()));
                try { Title = AppInfo.Title + "  [PNG guardado: " + Path.GetFileName(dlg.FileName) + "]"; } catch { }
            }
            catch (Exception ex)
            {
                MessageBox.Show("No se pudo exportar el PNG:\n" + ex.Message, "Exportar PNG",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // Vuelca _gifFrames capturas PNG numeradas a <dir> cada _gifIntervalMs ms, para
        // ensamblar un GIF de animaciones que corren en vivo en el WebView2. Headless --gif.
        private async Task CaptureFramesAndExit(string dir)
        {
            try
            {
                await WebViewer.EnsureCoreWebView2Async();
                System.IO.Directory.CreateDirectory(dir);
                var ci = System.Globalization.CultureInfo.InvariantCulture;
                var wStr = await WebViewer.CoreWebView2.ExecuteScriptAsync("Math.max(document.body.scrollWidth,document.documentElement.scrollWidth)");
                var hStr = await WebViewer.CoreWebView2.ExecuteScriptAsync("Math.max(document.body.scrollHeight,document.documentElement.scrollHeight)");
                int w = (int)double.Parse(wStr, ci), h = (int)double.Parse(hStr, ci);
                var prm = "{\"format\":\"png\",\"captureBeyondViewport\":true,\"clip\":{\"x\":0,\"y\":0,\"width\":" + w + ",\"height\":" + h + ",\"scale\":1}}";
                for (int k = 0; k < _gifFrames; k++)
                {
                    var res = await WebViewer.CoreWebView2.CallDevToolsProtocolMethodAsync("Page.captureScreenshot", prm);
                    using var jd = System.Text.Json.JsonDocument.Parse(res);
                    File.WriteAllBytes(System.IO.Path.Combine(dir, k.ToString("D3") + ".png"),
                        Convert.FromBase64String(jd.RootElement.GetProperty("data").GetString()));
                    await Task.Delay(_gifIntervalMs);
                }
            }
            catch { }
            Application.Current.Shutdown();
        }

        private void Window_Closing(object sender, CancelEventArgs e)
        {
            var r = PromptSave();
            if (r == MessageBoxResult.Cancel)
                e.Cancel = true;

            WriteSettings();
        }

        private async Task ScrollOutput()
        {   
            var offset = RichTextBox.CaretPosition.GetCharacterRect(LogicalDirection.Forward).Top +
                RichTextBox.Margin.Top - WebViewer.Margin.Top;
            await ScrollOutputToLine(
                _highlighter.Defined.HasMacros
                    ? _macroParser.GetUnwarpedLineNumber(_currentLineNumber)
                    : _currentLineNumber, offset);

            _scrollOutput = false;
        }

        private async Task ScrollOutputToLine(int lineNumber, double offset)
        {
            var tempScrollY = await _wv2Warper.GetScrollYAsync();
            await _wv2Warper.ScrollAsync(lineNumber, offset);
            if (tempScrollY == await _wv2Warper.GetScrollYAsync())
                await _wv2Warper.SetScrollYAsync(_scrollY);
        }

        private bool IsAutoRun =>
            AutoRunCheckBox.Visibility == Visibility.Visible &&
            (AutoRunCheckBox.IsChecked ?? false);

        private void RichTextBox_KeyUp(object sender, KeyEventArgs e)
        {
            if (_forceBackSpace && RichTextBox.CaretPosition.IsAtLineStartPosition)
            {
                _forceBackSpace = false;
                var p = RichTextBox.CaretPosition.Paragraph;
                if (p is not null)
                {
                    var pp = p.PreviousBlock as Paragraph;
                    if (pp is not null)
                    {
                        _isTextChangedEnabled = false;
                        RichTextBox.CaretPosition = pp.ContentEnd;
                        var s = new TextRange(p.ContentStart, p.ContentEnd).Text;
                        pp.Inlines.Add(s);
                        _document.Blocks.Remove(p);
                        _isTextChangedEnabled = true;
                    }
                }
            }
            else if (e.Key == Key.G && e.KeyboardDevice.Modifiers == ModifierKeys.Control)
            {
                var cp = RichTextBox.Selection.End;
                if (!cp.IsAtLineStartPosition)
                {
                    var sel = RichTextBox.Selection;
                    sel.Select(cp.GetPositionAtOffset(-1), cp);
                    string s = sel.Text;
                    if (s.Length == 1)
                    {
                        char c = LatinGreekChar(s[0]);
                        if (c != s[0])
                            _insertManager.InsertText(c.ToString());
                        else
                            sel.Select(cp, cp);
                    }
                }
            }
            else if (e.Key == Key.Back && !_autoCompleteManager.IsInComment())
                Task.Run(() => Dispatcher.InvokeAsync(_autoCompleteManager.RestoreAutoComplete));
        }

        private int GetLineNumber(Block block)
        {
            var blocks = _document.Blocks;
            var i = blocks.Count;
            if (_currentLineNumber > i / 2)
            {
                var b = blocks.LastBlock;
                while (b is not null)
                {
                    if (ReferenceEquals(b, block))
                        return i;
                    --i;
                    b = b.PreviousBlock;
                }
            }
            else
            {
                i = 1;
                var b = blocks.FirstBlock;
                while (b is not null)
                {
                    if (ReferenceEquals(b, block))
                        return i;
                    ++i;
                    b = b.NextBlock;
                }
            }
            return -1;
        }

        private async void RichTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            // EDITOR PLEGABLE: si el cambio NO salio del editor plegable (botones de insertar,
            // teclado de simbolos, MathCanvas...), hay que devolverlo alli al terminar.
            var vinoDeAvalon = _desdeAvalon;
            if (_isTextChangedEnabled)
            {
                if (_document.Blocks.Count == 0)
                    ResetText();

                if (IsAutoRun)
                {
                    var p = RichTextBox.Selection.End.Paragraph;
                    if (p is not null)
                    {
                        var len = p.ContentStart.GetOffsetToPosition(p.ContentEnd);
                        if (IsCalculated && len > 2 && !_highlighter.Defined.HasMacros)
                            _wv2Warper.SetContentAsync(_currentLineNumber, _svgTyping);
                    }
                    _autoRun = true;
                }

                if (_isPasting)
                {
                    _highlighter.Defined.Get(InputTextLines, IsComplex);
                    SetCodeCheckBoxVisibility();
                    await Dispatcher.InvokeAsync(HighLightPastedText, DispatcherPriority.Background);
                    SetAutoIndent();
                    var p = RichTextBox.Selection.End.Paragraph;
                    if (p is not null)
                        RichTextBox.CaretPosition = HighLighter.FindPositionAtOffset(p, _pasteOffset);
                    _isPasting = false;
                }
                Record();
                IsSaved = false;
                if (IsCalculated)
                {
                    if (!IsAutoRun)
                    {
                        IsCalculated = false;
                        ShowHelp();
                    }
                }
                if (!_isPasting)
                {
                    _highlighter.Defined.Get(InputTextLines, IsComplex);
                    SetCodeCheckBoxVisibility();
                    await Task.Run(DispatchAutoIndent);
                }
                await Task.Run(DispatchLineNumbers);
                _lastModifiedParagraph = _currentParagraph;

                // Sync Code → MathCanvas (if MC is visible and change didn't come FROM MC)
                if (_isMathCanvasMode && !_syncingFromMathCanvas)
                {
                    try
                    {
                        var code = GetInputText();
                        MathCanvasView.LoadFromText(code);
                    }
                    catch { }
                }

                // EDITOR PLEGABLE: Code → editor plegable (mismo patron que MathCanvas de arriba)
                if (!vinoDeAvalon)
                    SincronizarHaciaAvalon();
            }
        }

        private async void RichTextBox_SelectionChanged(object sender, RoutedEventArgs e)
        {
            var tps = RichTextBox.Selection.Start;
            var tpe = RichTextBox.Selection.End;

            var p = tps.Paragraph;
            p ??= tpe.Paragraph;
            if (p is null)
                return;

            if (!ReferenceEquals(_currentParagraph, tps.Paragraph) &&
                !ReferenceEquals(_currentParagraph, tpe.Paragraph))
            {
                _isTextChangedEnabled = false;
                RichTextBox.BeginChange();
                _highlighter.Parse(_currentParagraph, IsComplex, _currentLineNumber, true, null, p);
                if (p is not null)
                {
                    _currentParagraph = p;
                    _currentLineNumber = GetLineNumber(_currentParagraph);
                    HighLighter.Clear(_currentParagraph);
                    _autoCompleteManager.FillAutoComplete(_highlighter.Defined, _currentLineNumber);
                }
                e.Handled = true;
                RichTextBox.EndChange();
                _isTextChangedEnabled = true;
                if (_autoRun)
                {
                    var offset = RichTextBox.CaretPosition.GetOffsetToPosition(_document.ContentEnd);
                    await AutoRun(offset <= 2);
                }
                DispatchHighLightFromCurrent();
            }
            if (tps.Paragraph is null)
                return;

            _currentOffset = new TextRange(tps, tps.Paragraph.ContentEnd).Text.Length;
            if (p is not null && tpe.GetOffsetToPosition(tps) == 0)
            {
                _isTextChangedEnabled = false;
                RichTextBox.BeginChange();
                var tr = new TextRange(p.ContentStart, p.ContentEnd);
                tr.ApplyPropertyValue(TextElement.FontWeightProperty, FontWeights.Normal);
                tr.ApplyPropertyValue(TextElement.ForegroundProperty, EditorDefaultBrush);
                tr = new TextRange(p.ContentStart, tpe);
                var len = tr.Text.Length;
                HighLighter.HighlightBrackets(p, len);
                RichTextBox.EndChange();
                _isTextChangedEnabled = true;
            }
        }

        private void RichTextBox_LostKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e) =>
            Dispatcher.InvokeAsync(DisableInputWindowAsync, DispatcherPriority.ApplicationIdle);

        private async void DisableInputWindowAsync()
        {
            await Task.Delay(200);
            if (RichTextBox.IsKeyboardFocused ||
                AutoCompleteListBox.Visibility == Visibility.Visible ||
                await _wv2Warper.CheckIsContextMenuAsync())
                return;

            if (_autoRun && IsCalculated)
                AutoRun();
        }

        private void RichTextBox_Paste(object sender, DataObjectPastingEventArgs e)
        {
            var formats = e.DataObject.GetFormats();
            var hasImage = formats.Any(x => x.Contains("Bitmap"));
            if (formats.Contains("UnicodeText") && !hasImage)
            {
                e.FormatToApply = "UnicodeText";
                _isPasting = true;
                GetPasteOffset();
            }
            else
            {
                e.CancelCommand();
                if (hasImage && Clipboard.ContainsImage())
                {
                    string name = null;
                    if (formats.Contains("FileName"))
                    {
                        string[] fn = (string[])e.DataObject.GetData("FileName");
                        name = fn[0];
                        name = Path.GetFileNameWithoutExtension(name) + ".png";
                    }
                    Dispatcher.InvokeAsync(() => PasteImage(name), DispatcherPriority.ApplicationIdle);
                }
            }
        }

        // Pegar un recorte (Ctrl+V): se codifica el portapapeles como PNG base64 y se inserta
        // imshow('data:image/png;base64,...'); en el script → el código queda incrustado (sin
        // archivo externo) y la imagen se muestra en el WebView2 al ejecutar. Antes guardaba un
        // archivo e insertaba '<img> (sintaxis Calcpad que da error en MATLAB).
        private void PasteImage(string name)
        {
            try
            {
                var bmp = Clipboard.GetImage();
                if (bmp == null) return;
                // Un solo .m autocontenido (lo que pide el usuario, sin .cpdz ni archivo externo):
                // la imagen va DENTRO del .m como base64 en un COMENTARIO % #img → MATLAB lo ignora
                // (portable, no rompe) y Hekatan la renderiza. Copias solo el .m y la imagen viaja.
                string b64;
                using (var ms = new MemoryStream())
                {
                    var enc = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    enc.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(bmp));
                    enc.Save(ms);
                    b64 = Convert.ToBase64String(ms.ToArray());
                }
                // Deshabilitar TextChanged durante insert+parse: si no, el insert Y el parse (que
                // modifica el documento) disparaban cada uno un auto-run → salida DUPLICADA.
                _isTextChangedEnabled = false;
                RichTextBox.BeginChange();
                try
                {
                    _insertManager.InsertText($"% #img data:image/png;base64,{b64}");
                    // Convertir de inmediato el párrafo a MINIATURA (el resaltador reemplaza el base64
                    // por la foto y guarda la línea en Tag). Así nunca se ve el blob.
                    var imgPar = RichTextBox.Selection.End.Paragraph;
                    if (imgPar != null)
                        _highlighter.Parse(imgPar, IsComplex, GetLineNumber(imgPar), true);
                }
                finally { RichTextBox.EndChange(); }   // NO re-habilitar aún (evita 2º auto-run diferido)
                Record();
                IsSaved = false;
                DispatchLineNumbers();
                _autoRun = false;
                if (IsAutoRun)
                    CalculateAsync();   // UN solo render
                // Re-habilitar TextChanged DESPUÉS de que se procesen los eventos pendientes,
                // así ningún TextChanged diferido (del EndChange) dispara un segundo render.
                Dispatcher.InvokeAsync(() => { _isTextChangedEnabled = true; },
                    System.Windows.Threading.DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                ShowErrorMessage(ex.Message);
            }
        }

        private void GetPasteOffset()
        {
            _pasteEnd = RichTextBox.Selection.End;
            var p = _pasteEnd.Paragraph;
            _pasteOffset = p is not null ? new TextRange(_pasteEnd, p.ContentEnd).Text.Length : 0;
        }

        private DispatcherOperation _lineNumbersDispatcherOperation;
        private void DispatchLineNumbers()
        {
            _lineNumbersDispatcherOperation?.Abort();
            if (_lineNumbersDispatcherOperation?.Status != DispatcherOperationStatus.Executing)
                _lineNumbersDispatcherOperation =
                    Dispatcher.InvokeAsync(DrawLineNumbers, DispatcherPriority.Render);
        }

        private void DrawLineNumbers()
        {
            if (_document.Blocks.Count == 0)
            {
                LineNumbers.Children.Clear();
                return;
            }
            int j = 0, n = LineNumbers.Children.Count;
            var ff = _document.FontFamily;
            var sz = _document.FontSize - 1;
            var topMax = -sz;
            var tp = RichTextBox.GetPositionFromPoint(new Point(sz, sz), true);
            var b = (Block)tp.Paragraph;
            var i = 0;
            foreach (var block in _document.Blocks)
            {
                ++i;
                if (ReferenceEquals(block, b))
                    break;
            }
            while (b is not null)
            {
                var top = b.ElementStart.GetCharacterRect(LogicalDirection.Forward).Top + 1;
                if (top >= topMax)
                {
                    if (top > LineNumbers.ActualHeight)
                        break;
                    if (j < n)
                    {
                        var tb = (TextBlock)LineNumbers.Children[j];
                        tb.FontSize = sz;
                        tb.Margin = new Thickness(0, top, 0, 0);
                        tb.Text = (i).ToString();
                    }
                    else
                    {
                        var tb = new TextBlock
                        {
                            TextAlignment = TextAlignment.Right,
                            Width = 35,
                            FontSize = sz,
                            FontFamily = ff,
                            Foreground = Brushes.DarkCyan,
                            Margin = new Thickness(0, top, 0, 0),
                            Text = (i).ToString()
                        };
                        LineNumbers.Children.Add(tb);
                    }
                    ++j;
                }
                b = b.NextBlock;
                ++i;
            }
            if (j < n)
                LineNumbers.Children.RemoveRange(j, n - j);
            _sizeChanged = false;
        }

        private void RichTextBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            IsWebView2Focused = false;
            var modifiers = e.KeyboardDevice.Modifiers;
            var isCtrl = modifiers == ModifierKeys.Control;
            var isCtrlShift = modifiers == (ModifierKeys.Control | ModifierKeys.Shift);
            // Detect "real user edit" vs autoformatting/highlight-induced change.
            // Anything that mutates content (text key, backspace, delete, enter,
            // tab, paste, cut) flips the flag; pure navigation / modifiers do not.
            if (!_userTypedSinceLoad)
            {
                bool isMutating =
                    e.Key == Key.Back || e.Key == Key.Delete ||
                    e.Key == Key.Return || e.Key == Key.Enter || e.Key == Key.Tab ||
                    (isCtrl && (e.Key == Key.V || e.Key == Key.X)) ||
                    // Any printable key without Ctrl-only modifier
                    (modifiers != ModifierKeys.Control &&
                     modifiers != ModifierKeys.Alt &&
                     ((e.Key >= Key.A && e.Key <= Key.Z) ||
                      (e.Key >= Key.D0 && e.Key <= Key.D9) ||
                      (e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9) ||
                      e.Key == Key.Space || e.Key == Key.OemPeriod ||
                      e.Key == Key.OemComma || e.Key == Key.OemMinus ||
                      e.Key == Key.OemPlus || e.Key == Key.OemQuestion ||
                      e.Key == Key.OemSemicolon || e.Key == Key.OemQuotes ||
                      e.Key == Key.OemOpenBrackets || e.Key == Key.OemCloseBrackets ||
                      e.Key == Key.OemBackslash || e.Key == Key.OemTilde));
                if (isMutating)
                    _userTypedSinceLoad = true;
            }
            // MODO TEXTO: cada Enter arranca una nueva línea de texto visible con `%'`,
            // para escribir prosa sin teclear el prefijo a mano en cada línea.
            if (_textMode && (e.Key == Key.Return || e.Key == Key.Enter)
                && modifiers == ModifierKeys.None)
            {
                e.Handled = true;
                RichTextBox.BeginChange();
                try { _insertManager.InsertLine(); _insertManager.InsertText("%'"); }
                finally { RichTextBox.EndChange(); }
                RichTextBox.Focus();
                return;
            }
            if (e.Key == Key.V && isCtrlShift)
            {
                PasteAsCommentMenu_Click(PasteAsCommentMenu, e);
                e.Handled = true;
            }
            if (e.Key == Key.Q && isCtrl)
            {
                CommentUncomment(true);
                e.Handled = true;
            }
            if (e.Key == Key.Q && isCtrlShift)
            {
                CommentUncomment(false);
                e.Handled = true;
            }
            else if ((e.Key == Key.D1 || e.Key == Key.NumPad1) && isCtrl)
            {
                Button_Click(H3Button, e);   // H3Button ahora es el botón "H1"
                e.Handled = true;
            }
            else if ((e.Key == Key.D2 || e.Key == Key.NumPad2) && isCtrl)
            {
                Button_Click(H4Button, e);   // "H2"
                e.Handled = true;
            }
            else if ((e.Key == Key.D3 || e.Key == Key.NumPad3) && isCtrl)
            {
                Button_Click(H5Button, e);   // "H3"
                e.Handled = true;
            }
            else if ((e.Key == Key.D4 || e.Key == Key.NumPad4) && isCtrl)
            {
                Button_Click(H6Button, e);   // "H4"
                e.Handled = true;
            }
            else if (e.Key == Key.B && isCtrl)
            {
                Button_Click(BoldButton, e);
                e.Handled = true;
            }
            else if (e.Key == Key.I && isCtrl)
            {
                Button_Click(ItalicButton, e);
                e.Handled = true;
            }
            else if (e.Key == Key.U && isCtrl)
            {
                Button_Click(UnderlineButton, e);
                e.Handled = true;
            }
            else if (e.Key == Key.OemPlus)
            {
                if (isCtrl)
                {
                    Button_Click(SubscriptButton, e);
                    e.Handled = true;
                }
                else if (isCtrlShift)
                {
                    Button_Click(SuperscriptButton, e);
                    e.Handled = true;
                }
            }
            else if (e.Key == Key.Enter)
            {
                if (isCtrl)
                {
                    AutoRun(true);
                    e.Handled = true;
                }
                else
                {
                    // Reescribe la notación $Op{} de la línea que dejamos → MATLAB.
                    ScheduleTranspileOnEnter(RichTextBox.Selection.Start.Paragraph);
                    RichTextBox.Selection.ApplyPropertyValue(TextElement.ForegroundProperty, EditorDefaultBrush);
                }
            }
            else if (e.Key == Key.Back)
            {
                var tp = RichTextBox.Selection.Start;
                var selLength = tp.GetOffsetToPosition(RichTextBox.Selection.End);
                _forceBackSpace = tp.IsAtLineStartPosition && tp.Paragraph?.TextIndent > 0 && selLength == 0;
            }
            else
                _forceBackSpace = false;

            if (AutoCompleteListBox.Visibility == Visibility.Visible)
                _autoCompleteManager.PreviewKeyDown(e);
        }

        private DispatcherOperation _autoIndentDispatcherOperation;

        private void DispatchAutoIndent()
        {
            _autoIndentDispatcherOperation?.Abort();
            if (_autoIndentDispatcherOperation?.Status != DispatcherOperationStatus.Executing)
                _autoIndentDispatcherOperation =
                    Dispatcher.InvokeAsync(AutoIndent, DispatcherPriority.ApplicationIdle);
        }

        private void AutoIndent()
        {
            var p = RichTextBox.Selection.End.Paragraph;
            if (p is null)
                p = _document.Blocks.FirstBlock as Paragraph;
            else if (p.PreviousBlock is not null)
                p = p.PreviousBlock as Paragraph;

            if (p is null)
            {
                p = new Paragraph(new Run());
                _document.Blocks.Add(p);
            }
            var indent = 0.0;
            var i = 0;
            var pp = (p.PreviousBlock as Paragraph);
            if (pp is not null)
            {
                indent = pp.TextIndent;
                var s = new TextRange(pp.ContentStart, pp.ContentEnd).Text.Trim().ToLowerInvariant();
                if (s.Length > 3 && s[0] == '#')
                {
                    var span = s.AsSpan(1);
                    if (IsIndentStart(span) || span.StartsWith("else"))
                        indent += AutoIndentStep;
                }
            }
            _isTextChangedEnabled = false;
            RichTextBox.BeginChange();
            while (p is not null)
            {
                if (!UpdateIndent(p, ref indent))
                {
                    if (p.TextIndent == indent)
                    {
                        ++i;
                        if (i > 5)
                            break;
                    }
                    else
                    {
                        p.TextIndent = indent;
                        i = 0;
                    }
                }
                p = p.NextBlock as Paragraph;
            }
            RichTextBox.EndChange();
            _isTextChangedEnabled = true;
        }

        private void SetAutoIndent()
        {
            var indent = 0.0;
            var p = _document.Blocks.FirstBlock as Paragraph;

            _isTextChangedEnabled = false;
            RichTextBox.BeginChange();
            while (p is not null)
            {
                if (!UpdateIndent(p, ref indent))
                    p.TextIndent = indent;

                p = p.NextBlock as Paragraph;
            }
            RichTextBox.EndChange();
            _isTextChangedEnabled = true;
        }

        private static bool UpdateIndent(Paragraph p, ref double indent)
        {
            var s = new TextRange(p.ContentStart, p.ContentEnd).Text.ToLowerInvariant().Trim();
            if (s.Length > 3 && s[0] == '#')
            {
                var span = s.AsSpan(1);
                if (!IsIndent(span))
                    return false;
                else if (IsIndentStart(span))
                {
                    p.TextIndent = indent;
                    indent += AutoIndentStep;
                }
                else if (IsIndentEnd(span))
                {
                    indent -= AutoIndentStep;
                    if (indent < 0)
                        indent = 0;
                    p.TextIndent = indent;
                }
                else
                    p.TextIndent = Math.Max(indent - AutoIndentStep, 0);

                return true;
            }
            return false;
        }

        private static bool IsIndent(ReadOnlySpan<char> s) =>
            s.StartsWith("if") ||
            s.StartsWith("el") ||
            s.StartsWith("en") ||
            s.StartsWith("re") ||
            s.StartsWith("fo") ||
            s.StartsWith("wh") ||
            s.StartsWith("lo") ||
            s.StartsWith("def") &&
            !s.Contains('=');

        private static bool IsIndentStart(ReadOnlySpan<char> s) =>
            s.StartsWith("if") ||
            s.StartsWith("repeat") ||
            s.StartsWith("for ") ||
            s.StartsWith("while") ||
            s.StartsWith("def") &&
            !s.Contains('=');

        private static bool IsIndentEnd(ReadOnlySpan<char> s) =>
            s.StartsWith("end") || s.StartsWith("loop");

        private void HighLightAll()
        {
            _isTextChangedEnabled = false;
            Cursor = Cursors.Wait;
            RichTextBox.BeginChange();
            _highlighter.Defined.Get(InputTextLines, IsComplex);
            SetCodeCheckBoxVisibility();
            var p = _document.Blocks.FirstBlock as Paragraph;
            var i = 1;
            while (p is not null)
            {
                if (_forceHighlight)
                    _highlighter.Parse(p, IsComplex, i, false, new TextRange(p.ContentStart, p.ContentEnd).Text.TrimStart('\t'));
                else
                    _highlighter.Parse(p, IsComplex, i, false);
                p = p.NextBlock as Paragraph;
                ++i;
            }
            _currentParagraph = RichTextBox.Selection.Start.Paragraph;
            _currentLineNumber = GetLineNumber(_currentParagraph);
            HighLighter.Clear(_currentParagraph);
            RichTextBox.EndChange();
            Cursor = Cursors.Arrow;
            _isTextChangedEnabled = true;
        }

        private DispatcherOperation _highLightFromCurrentDispatcherOperation;

        private async void DispatchHighLightFromCurrent()
        {
            _highLightFromCurrentDispatcherOperation?.Abort();
            var currentkeyDownCount = _countKeys;
            await Task.Delay(250).ContinueWith(delegate
            {
                if (currentkeyDownCount == _countKeys &&
                    _highLightFromCurrentDispatcherOperation?.Status != DispatcherOperationStatus.Executing)
                    _highLightFromCurrentDispatcherOperation =
                        Dispatcher.BeginInvoke(HighLightFromCurrent, DispatcherPriority.ApplicationIdle);
            });
        }

        private void HighLightFromCurrent()
        {
            if (_lastModifiedParagraph is null)
                return;

            _isTextChangedEnabled = false;
            RichTextBox.BeginChange();
            var p = _lastModifiedParagraph.NextBlock as Paragraph;
            var lineNumber = GetLineNumber(p);
            var maxNumber = lineNumber + 35;
            while (p is not null)
            {
                if (!ReferenceEquals(p, _currentParagraph))
                    p = _highlighter.CheckHighlight(p, ref lineNumber);

                if (p is null)
                    break;

                p = p.NextBlock as Paragraph;
                lineNumber++;
                if (lineNumber >= maxNumber)
                    break;
            }
            _lastModifiedParagraph = p;
            RichTextBox.EndChange();
            _isTextChangedEnabled = true;
        }

        private void HighLightPastedText()
        {
            _isTextChangedEnabled = false;
            RichTextBox.BeginChange();
            var p = _pasteEnd.Paragraph;
            _currentParagraph = RichTextBox.Selection.Start.Paragraph;
            p ??= _document.Blocks.FirstBlock as Paragraph;

            var lineNumber = GetLineNumber(p);
            while (p != _currentParagraph && p != null)
            {
                _highlighter.Parse(p, IsComplex, lineNumber++, false);
                p = p.NextBlock as Paragraph;
            }
            _currentLineNumber = GetLineNumber(_currentParagraph);
            HighLighter.Clear(_currentParagraph);
            RichTextBox.EndChange();
            _isTextChangedEnabled = true;
        }

        private void RichTextBox_PreviewDrop(object sender, DragEventArgs e)
        {
            _isPasting = true;
            GetPasteOffset();
        }

        private void RichTextBox_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
        {
            if (IsControlDown)
            {
                e.Handled = true;
                var d = RichTextBox.FontSize + Math.CopySign(2, e.Delta);
                if (d > 4 && d < 42)
                {
                    RichTextBox.FontSize = d;
                    DispatchLineNumbers();
                }
            }
        }

        private static bool IsControlDown => (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        private static bool IsAltDown => (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;

        private void InvHypButton_Click(object sender, RoutedEventArgs e)
        {
            var b = (Button)sender;
            b.Tag = !(bool)b.Tag;
            if ((bool)b.Tag)
                b.Foreground = Brushes.Red;
            else
                b.Foreground = SystemColors.ControlTextBrush;

            bool inv = (bool)InvButton.Tag, hyp = (bool)HypButton.Tag;
            string pref = string.Empty, post = string.Empty;
            if (inv)
                pref = "a";

            if (hyp)
                post = "h";

            double fs = inv && hyp ? 14d : 15d;
            FontFamily ff;
            if (inv || hyp)
                ff = new FontFamily("Arial Nova Cond");
            else
                ff = new FontFamily("Roboto");

            SetTrigButton(SinButton, pref + "sin" + post, fs, ff);
            SetTrigButton(CosButton, pref + "cos" + post, fs, ff);
            SetTrigButton(TanButton, pref + "tan" + post, fs, ff);
            SetTrigButton(CscButton, pref + "csc" + post, fs, ff);
            SetTrigButton(SecButton, pref + "sec" + post, fs, ff);
            SetTrigButton(CotButton, pref + "cot" + post, fs, ff);
            PowButton.Visibility = inv ? Visibility.Hidden : Visibility.Visible;
            SqrButton.Visibility = inv ? Visibility.Hidden : Visibility.Visible;
            CubeButton.Visibility = inv ? Visibility.Hidden : Visibility.Visible;
            ExpButton.Visibility = inv ? Visibility.Hidden : Visibility.Visible;
            RootButton.Visibility = inv ? Visibility.Visible : Visibility.Hidden;
            SqrtButton.Visibility = inv ? Visibility.Visible : Visibility.Hidden;
            CbrtButton.Visibility = inv ? Visibility.Visible : Visibility.Hidden;
            LnButton.Visibility = inv ? Visibility.Visible : Visibility.Hidden;
        }

        private static void SetTrigButton(Button btn, string s, double fontSize, FontFamily fontFamily)
        {
            btn.Content = s;
            btn.Tag = s + "(x)";
            btn.FontSize = fontSize;
            btn.FontFamily = fontFamily;
            btn.FontStretch = fontFamily.Source.Contains("Cond") ?
                FontStretches.Condensed :
                FontStretches.Normal;

            btn.FontWeight = fontFamily.Source.Contains("Light") ?
                FontWeights.Light :
                FontWeights.Normal;

            btn.ToolTip = s switch
            {
                "sin" => MathResources.Sine,
                "cos" => MathResources.Cosine,
                "tan" => MathResources.Tangent,
                "csc" => MathResources.Cosecant,
                "sec" => MathResources.Secant,
                "cot" => MathResources.Cotangent,

                "asin" => MathResources.InverseSine,
                "acos" => MathResources.InverseCosine,
                "atan" => MathResources.InverseTangent,
                "acsc" => MathResources.InverseCosecant,
                "asec" => MathResources.InverseSecant,
                "acot" => MathResources.InverseCotangent,

                "sinh" => MathResources.HyperbolicSine,
                "cosh" => MathResources.HyperbolicCosine,
                "tanh" => MathResources.HyperbolicTangent,
                "csch" => MathResources.HyperbolicCosecant,
                "sech" => MathResources.HyperbolicSecant,
                "coth" => MathResources.HyperbolicCotangent,

                "asinh" => MathResources.InverseHyperbolicSine,
                "acosh" => MathResources.InverseHyperbolicCosine,
                "atanh" => MathResources.InverseHyperbolicTangent,
                "acsch" => MathResources.InverseHyperbolicCosecant,
                "asech" => MathResources.InverseHyperbolicSecant,
                "acoth" => MathResources.InverseHyperbolicCotangent,
                _ => null
            };
        }


        // ── Modo "solo WebView" (F11): maximiza las gráficas/reporte ocultando el editor y el
        //    splitter, y maximiza la ventana. F11 o Escape vuelve a la vista normal. ──
        private bool _webOnlyMode = false;
        private WindowState _savedWinState = WindowState.Normal;
        private void MaximizeOutput_Click(object sender, RoutedEventArgs e) => ToggleWebOnlyMode();
        private void ToggleWebOnlyMode()
        {
            _webOnlyMode = !_webOnlyMode;
            if (_webOnlyMode)
            {
                _savedWinState = WindowState;
                InputFrame.Visibility = Visibility.Collapsed;
                MainSplitter.Visibility = Visibility.Collapsed;
                EditorCol.Width = new GridLength(0);
                SplitterCol.Width = new GridLength(0);
                WebCol.Width = new GridLength(1, GridUnitType.Star);
                WindowState = WindowState.Maximized;
                if (MaximizeOutputBtn != null) MaximizeOutputBtn.Content = "⛶ Editor";
            }
            else
            {
                // Restaurar SIEMPRE al 50/50 limpio (layout por defecto). No se restaura un ancho
                // guardado porque podía quedar en px absolutos → aplastaba el Output.
                InputFrame.Visibility = Visibility.Visible;
                MainSplitter.Visibility = Visibility.Visible;
                EditorCol.Width = new GridLength(120, GridUnitType.Star);
                SplitterCol.Width = GridLength.Auto;
                WebCol.Width = new GridLength(120, GridUnitType.Star);
                WindowState = _savedWinState;
                if (MaximizeOutputBtn != null) MaximizeOutputBtn.Content = "⛶ Output";
            }
        }

        private void Window_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F11)
            {
                ToggleWebOnlyMode();
                e.Handled = true;
                return;
            }
            if (e.Key == Key.Escape)
            {
                if (_webOnlyMode)   // salir del modo "solo gráficas" antes que cancelar el cálculo
                {
                    ToggleWebOnlyMode();
                    return;
                }
                if (_isParsing)
                {
                    _autoRun = false;
                    Cancel();
                }
                else if (_parser.IsPaused)
                    Cancel();
            }
            else if (e.Key == Key.Pause || e.Key == Key.P && IsControlDown && IsAltDown)
            {
                if (_isParsing)
                    Pause();
            }
        }

        private void Cancel()
        {
            bool isPaused = _parser.IsPaused;
            _parser.Cancel();
            if (isPaused)
            {
                if (IsWebForm)
                    CalculateAsync(true);
                else
                    ShowHelp();
            }
        }

        private void Pause() => _parser.Pause();

        bool _sizeChanged;
        private void RichTextBox_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            _sizeChanged = true;
            _autoCompleteManager.MoveAutoComplete();
            _lineNumbersDispatcherOperation?.Abort();
            _lineNumbersDispatcherOperation = Dispatcher.InvokeAsync(DrawLineNumbers, DispatcherPriority.ApplicationIdle);
        }

        private void Window_Unloaded(object sender, RoutedEventArgs e)
        {
            Application.Current.Shutdown();
        }

        private void Window_Loaded(object sender, RoutedEventArgs e)
        {
            _screenScaleFactor = ScreenMetrics.GetWindowsScreenScalingFactor();
            ReadSettings();
            // Aplicar el tema persistido a los brushes del chrome (XAML default = Dark).
            SwapThemeBrushes(_isDarkTheme);
            if (ThemeToggleMenuItem != null)
                ThemeToggleMenuItem.Header = _isDarkTheme ? "Theme: Dark  →  Gold" : "Theme: Gold  →  Dark";
            UpdateThemeToggleVisual();
            if (Top < 0)
                Top = 0;

            var h = SystemParameters.PrimaryScreenHeight;
            if (Height > h)
                Height = h;

            // EDITOR PLEGABLE: montar el editor con plegado (AvalonEdit) encima del clasico.
            PrepararAvalon();
            AplicarColoresAvalon(_isDarkTheme);
        }

        private async void Include_Click(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed)
            {
                var r = (Run)sender;
                var fileName = r?.Text.Trim();
                if (File.Exists(fileName))
                {
                    Mouse.SetCursor(Cursors.Wait);
                    var tt = (ToolTip)r.ToolTip;
                    tt?.Visibility = Visibility.Hidden;
                    var ext = Path.GetExtension(fileName).ToLowerInvariant();
                    var path = Path.GetFullPath(fileName);
                    Process process;
                    if (ext == ".txt")
                        process = RunExternalApp("NOTEPAD++", path);
                    else
                    {
                        process = RunExternalApp(AppInfo.FullName, path);
                        process ??= RunExternalApp("NOTEPAD++", path);
                    }
                    process ??= RunExternalApp("NOTEPAD", path);
                    tt?.Visibility = Visibility.Visible;
                    if (process is not null)
                    {
                        _calculateOnActivate = true;
                        if (tt is not null)
                        {
                            string s = Include(fileName, null);
                            tt.Content = HighLighter.GetPartialSource(s);
                        }
                        if (IsCalculated)
                        {
                            if (IsAutoRun)
                            {
                                _isTextChangedEnabled = false;
                                await AutoRun();
                                _isTextChangedEnabled = true;
                            }
                            else
                            {
                                ShowHelp();
                                IsCalculated = false;
                            }
                        }
                        e.Handled = true;
                    }
                }
            }
        }

        private string Include(string fileName, Queue<string> fields)
        {
            var isLocal = false;
            var s = ReadTextFromFile(fileName);
            var j = s.IndexOf('\v');
            var hasForm = j > 0;
            var lines = (hasForm ? s[..j] : s).EnumerateLines();
            var getLines = new List<string>();
            var sf = hasForm ? s[(j + 1)..] : default;
            Queue<string> getFields = GetFields(sf, fields);
            foreach (var line in lines)
            {
                if (Validator.IsKeyword(line, "#local"))
                    isLocal = true;
                else if (Validator.IsKeyword(line, "#global"))
                    isLocal = false;
                else
                {
                    if (!isLocal)
                    {
                        if (Validator.IsKeyword(line, "#include"))
                        {
                            var includeFileName = UserDefined.GetFileName(line);
                            var includeFilePath = Path.GetFullPath(Environment.ExpandEnvironmentVariables(includeFileName));
                            if (!File.Exists(includeFilePath))
                                throw new FileNotFoundException($"{Core.Messages.File_not_found}: {includeFileName}.");

                            getLines.Add(fields is null
                                    ? Include(includeFilePath, null)
                                    : Include(includeFilePath, new()));
                        }
                        else
                            getLines.Add(line.ToString());
                    }
                }
            }
            if (hasForm && string.IsNullOrWhiteSpace(getLines[^1]))
                getLines.RemoveAt(getLines.Count - 1);

            var len = getLines.Count;
            if (len > 0)
            {
                _stringBuilder.Clear();
                for (int i = 0; i < len; ++i)
                {
                    if (getFields is not null && getFields.Count != 0)
                    {
                        if (MacroParser.SetLineInputFields(getLines[i].TrimEnd(), _stringBuilder, getFields, false))
                            getLines[i] = _stringBuilder.ToString();

                        _stringBuilder.Clear();
                    }
                }
            }
            return string.Join(Environment.NewLine, getLines);
        }

        private static Queue<string> GetFields(ReadOnlySpan<char> s, Queue<string> fields)
        {
            if (fields is null)
                return null;

            if (fields.Count != 0)
            {
                if (!s.IsEmpty)
                {
                    var getFields = MacroParser.GetFields(s, '\t');
                    if (fields.Count < getFields.Count)
                    {
                        for (int i = 0; i < fields.Count; ++i)
                            getFields.Dequeue();

                        while (getFields.Count != 0)
                            fields.Enqueue(getFields.Dequeue());
                    }
                }
                return fields;
            }
            else if (!s.IsEmpty)
                return MacroParser.GetFields(s, '\t');
            else
                return null;
        }

        private bool ValidateInputFields(string[] fields)
        {
            for (int i = 0, len = fields.Length; i < len; ++i)
            {
                var s = fields[i].AsSpan();
                if (s.Length > 0)
                {
                    var j = s.IndexOf(':');
                    if (j > 0)
                        s = s[(j + 1)..];
                }
                if (s.Length == 0 || s[0] == '+' || !double.TryParse(s, NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var _))
                {
                    _wv2Warper.ReportInputFieldError(i);
                    return false;
                }
            }
            return true;
        }


        private bool SetInputFields(string[] fields)
        {
            if (fields is null ||
                fields.Length == 0 ||
                fields.Length == 1 && string.IsNullOrEmpty(fields[0]))
                return true;

            if (!ValidateInputFields(fields))
                return false;

            var p = _document.Blocks.FirstBlock;
            var i = 0;
            var line = 0;
            var fline = 0;
            _stringBuilder.Clear();
            var values = new Queue<string>();
            _isTextChangedEnabled = false;
            RichTextBox.BeginChange();
            while (p is not null && i < fields.Length)
            {
                ++line;
                values.Clear();
                while (i < fields.Length)
                {
                    var s = fields[i].AsSpan();
                    if (s.Length > 0)
                    {
                        var j = s.IndexOf(':');
                        if (j < 0 || !int.TryParse(s[..j], out fline))
                            fline = 0;

                        if (fline > line)
                            break;

                        values.Enqueue(s[(j + 1)..].ToString().Trim());
                    }
                    ++i;
                }
                if (values.Count != 0)
                {
                    var r = new TextRange(p.ContentStart, p.ContentEnd);
                    if (MacroParser.SetLineInputFields(r.Text.TrimEnd(), _stringBuilder, values, true))
                    {
                        if (_forceHighlight)
                            r.Text = _stringBuilder.ToString();
                        else
                            _highlighter.Parse(p as Paragraph, IsComplex, line, true, _stringBuilder.ToString());
                    }
                    _stringBuilder.Clear();
                }
                if (fline > line)
                {
                    line = fline - 1;
                    p = _document.Blocks.ElementAt(line);
                }
                else
                    p = p.NextBlock;
            }
            RichTextBox.EndChange();
            _isTextChangedEnabled = true;
            return true;
        }

        private void SetInputFieldsFromFile(SplitEnumerator fields)
        {
            if (fields.IsEmpty)
                return;

            var p = _document.Blocks.FirstOrDefault();
            _stringBuilder.Clear();
            var values = new Queue<string>();
            foreach (var s in fields)
                values.Enqueue(s.ToString());

            while (p is not null && values.Count != 0)
            {
                var r = new TextRange(p.ContentStart, p.ContentEnd);
                if (MacroParser.SetLineInputFields(r.Text.TrimEnd(), _stringBuilder, values, false))
                    r.Text = _stringBuilder.ToString();

                _stringBuilder.Clear();
                p = p.NextBlock;
            }
        }

        private void Logo_MouseUp(object sender, MouseButtonEventArgs e)
        {
            var info = new ProcessStartInfo
            {
                FileName = "https://github.com/GiorgioBurbanelli89/hekatan-lab",
                UseShellExecute = true
            };
            Process.Start(info);
        }

        // Abre el menú de plantillas disp/fprintf al hacer clic (no en clic derecho).
        private void DispMenuButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is System.Windows.Controls.Button b && b.ContextMenu is not null)
            {
                b.ContextMenu.PlacementTarget = b;
                b.ContextMenu.Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom;
                b.ContextMenu.IsOpen = true;
            }
        }

        private bool _isMathCanvasMode = false;
        private bool _syncingFromMathCanvas = false;

        private bool _mcToggling = false;

        private void MathCanvasToggle_Click(object sender, RoutedEventArgs e)
        {
            // Click always fires for manual user clicks
            ApplyMathCanvasToggle();
        }

        private void MathCanvasToggle_StateChanged(object sender, RoutedEventArgs e)
        {
            // Checked/Unchecked fires for UI Automation TogglePattern
            // Avoid double-fire when Click also triggers state change
            if (!_mcToggling)
                ApplyMathCanvasToggle();
        }

        private void ApplyMathCanvasToggle()
        {
            if (_mcToggling) return;
            _mcToggling = true;
            try
            {
                if (MathCanvasToggle.IsChecked == true)
                    ActivateMathCanvas();
                else
                    DeactivateMathCanvas();
            }
            finally
            {
                // Delay reset to avoid re-entry from state change events
                Dispatcher.BeginInvoke(new Action(() => _mcToggling = false),
                    System.Windows.Threading.DispatcherPriority.Background);
            }
        }

        private void ActivateMathCanvas()
        {
            try
            {
                _isMathCanvasMode = true;
                WebViewer.Visibility = Visibility.Collapsed;
                MathCanvasView.Visibility = Visibility.Visible;
                OutputFrame.Header = "MathCanvas";

                // Pass parser to MathCanvas
                MathCanvasView.SetParser(_parser);

                // Sync Code → MathCanvas
                var code = GetInputText();
                MathCanvasView.LoadFromText(code);

                // Subscribe to MathCanvas → Code sync
                MathCanvasView.TextChanged -= OnMathCanvasTextChanged;
                MathCanvasView.TextChanged += OnMathCanvasTextChanged;
            }
            catch (Exception ex)
            {
                System.Windows.MessageBox.Show($"MathCanvas error: {ex.Message}", "Error");
            }
        }

        private void DeactivateMathCanvas()
        {
            try
            {
                _isMathCanvasMode = false;

                // Sync MathCanvas → Code before switching back
                var mcText = MathCanvasView.GetText();
                if (!string.IsNullOrEmpty(mcText))
                {
                    _syncingFromMathCanvas = true;
                    SetInputText(mcText);
                    _syncingFromMathCanvas = false;
                }

                MathCanvasView.TextChanged -= OnMathCanvasTextChanged;
                MathCanvasView.Visibility = Visibility.Collapsed;
                WebViewer.Visibility = Visibility.Visible;
                OutputFrame.Header = MainWindowResources.Output;
            }
            catch (Exception ex)
            {
                _syncingFromMathCanvas = false;
                System.Windows.MessageBox.Show($"MathCanvas error: {ex.Message}", "Error");
            }
        }

        private void OnMathCanvasTextChanged(string text)
        {
            if (_syncingFromMathCanvas) return;
            _syncingFromMathCanvas = true;
            try
            {
                // Disable RichTextBox_TextChanged to prevent loop and avoid
                // highlighter/auto-indent running on every keystroke from MC
                _isTextChangedEnabled = false;
                SetInputText(text);
                _isTextChangedEnabled = true;
            }
            finally
            {
                _syncingFromMathCanvas = false;
            }
        }

        /// <summary>
        /// Sync Code panel content TO MathCanvas.
        /// Call this whenever the Code panel content changes and MC is visible.
        /// </summary>
        private void SyncToMathCanvas()
        {
            if (!_isMathCanvasMode || _syncingFromMathCanvas) return;
            try
            {
                MathCanvasView.SetParser(_parser);
                var code = GetInputText();
                MathCanvasView.LoadFromText(code);
            }
            catch { }
        }

        private void SetInputText(string text)
        {
            var document = RichTextBox.Document;
            document.Blocks.Clear();
            foreach (var line in text.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None))
            {
                document.Blocks.Add(new System.Windows.Documents.Paragraph(
                    new System.Windows.Documents.Run(line)));
            }
        }

        private void PdfButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isParsing)
                return;

            if (IsCalculated || IsWebForm || _parser.IsPaused)
            {
                var fileName = PromtSavePdf();
                if (fileName is not null)
                    SavePdf(fileName);
            }
            else
            {
                var fileName = _currentCultureName == "en" ?
                    $"{AppInfo.DocPath}\\help.pdf" :
                    $"{AppInfo.DocPath}\\help.{_currentCultureName}.pdf";
                if (!File.Exists(fileName))
                    fileName = $"{AppInfo.DocPath}doc\\help.pdf";

                StartPdf(fileName);
            }
        }

        private string PromtSavePdf()
        {
            var dlg = new SaveFileDialog
            {
                DefaultExt = ".pdf",
                Filter = "Pdf File (*.pdf)|*.pdf",
                FileName = Path.ChangeExtension(Path.GetFileName(CurrentFileName), "pdf"),
                InitialDirectory = DialogDir,
                OverwritePrompt = true
            };
            var result = (bool)dlg.ShowDialog();
            return result ? dlg.FileName : null;
        }

        private async void SavePdf(string pdfFileName)
        {
            var settings = _wv2Warper.CreatePrintSettings();
            await WebViewer.CoreWebView2.PrintToPdfAsync(pdfFileName, settings);
            StartPdf(pdfFileName);
        }

        private static void StartPdf(string pdfFileName)
        {
            var process = new Process()
            {
                StartInfo = new ProcessStartInfo(pdfFileName)
                {
                    UseShellExecute = true
                }
            };
            process.Start();
        }

        private void UnitsRadioButton_Checked(object sender, RoutedEventArgs e)
        {
            ExpressionParser.IsUs = ReferenceEquals(sender, US);
            ClearOutput();
        }
        private async void WebViewer_NavigationCompleted(object sender, Microsoft.Web.WebView2.Core.CoreWebView2NavigationCompletedEventArgs e)
        {
          try
          {
           if (!await _wv2Warper.CheckIsReportAsync())
                return;

            // Aplicar el tema del reporte (clase 'dark' → CSS oscuro solo-pantalla; print/PDF blanco).
            ApplyReportTheme(_isDarkTheme);
            // Ojo: en el pipeline MATLAB esta navegación es la página VACÍA de streaming, que
            // carga al principio. Dar por terminado el cálculo aquí dejaba la ventana libre
            // mientras el motor seguía → dos cálculos encima. El que manda ahí es _matlabBusy.
            if (!_matlabBusy) _isParsing = false;
            if (_isSaving)
            {
                var zip = string.Equals(Path.GetExtension(CurrentFileName), ".cpdz", StringComparison.OrdinalIgnoreCase);
                if (zip)
                {
                    _macroParser.Parse(InputText, out var outputText, null, 0, false);
                    WriteFile(CurrentFileName, outputText, true);
                }
                else
                    WriteFile(CurrentFileName, GetInputText());

                _isSaving = false;
                IsSaved = true;
            }
            else if (IsWebForm || IsCalculated || _parser.IsPaused)
            {
                SetUnits();
                if (IsCalculated || _parser.IsPaused)
                {
                    if (_scrollOutput)
                        await ScrollOutput();
                    else if (_scrollY > 0)
                    {
                        await _wv2Warper.SetScrollYAsync(_scrollY);
                        _scrollY = 0;
                    }
                }
            }
            if (_scrollOutputToLine > 0)
            {
                await ScrollOutputToLine(_scrollOutputToLine, _scrollOffset);
                _scrollOutputToLine = 0;
            }
          }
          catch (Exception ex)
          {
                // Nunca dejar que una excepción del handler de navegación (async void) escale a
                // CriticalShutdown (que además dispara el FileNotFound de System.Diagnostics.Tracing
                // del logger de telemetría de WPF al cerrar). Se traga y se sigue.
                System.Diagnostics.Debug.WriteLine("WebViewer_NavigationCompleted swallowed: " + ex.Message);
          }
        }

        private void WebViewer_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key >= Key.D0 && e.Key <= Key.D9 || e.Key >= Key.NumPad0 && e.Key <= Key.NumPad9)
                IsSaved = false;
        }
        internal static bool Execute(string fileName, string args = "")
        {
            var proc = new Process();
            var psi = new ProcessStartInfo
            {
                UseShellExecute = true,
                FileName = fileName,
                Arguments = args
            };
            proc.StartInfo = psi;
            try
            {
                return proc.Start();
            }
            catch (Exception ex)
            {
                ShowErrorMessage(ex.Message);
                return false;
            }
        }

        private void DecimalScrollBar_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e) =>
            DecimalsTextBox.Text = (15 - e.NewValue).ToString(CultureInfo.InvariantCulture);

        private void Record() =>
            _undoMan.Record(
                InputText,
                _currentLineNumber,
                _currentOffset
            );

        private void ChangeCaseButton_Click(object sender, RoutedEventArgs e)
        {
            foreach (FrameworkElement element in GreekLettersWarpPanel.Children)
            {
                if (element is TextBlock tb)
                {
                    char c = tb.Text[0];
                    const int delta = 'Α' - 'α';
                    if (c == 'ς')
                        c = 'Σ';
                    else if (c == 'ϑ')
                        c = '∡';
                    else if (c == '∡')
                        c = 'ϑ';
                    else if (c == 'ø')
                        c = 'Ø';
                    else if (c == 'Ø')
                        c = 'ø';
                    else if (c >= 'α' && c <= 'ω')
                        c = (char)(c + delta);
                    else if ((c == 'Σ') && tb.Tag is string s)
                        c = s[0];
                    else if (c >= 'Α' && c <= 'Ω')
                        c = (char)(c - delta);
                    else if (c == '′')
                        c = '‴';
                    else if (c == '″')
                        c = '⁗';
                    else if (c == '‴')
                        c = '′';
                    else if (c == '⁗')
                        c = '″';
                    else if (c == '‰')
                        c = '‱';
                    else if (c == '‱')
                        c = '‰';
                    tb.Text = c.ToString();
                }
            }
        }

        private static char LatinGreekChar(char c) => c switch
        {
            >= 'a' and <= 'z' => GreekLetters[c - 'a'],
            'V' => '∡',
            'J' => 'Ø',
            >= 'A' and <= 'Z' => (char)(GreekLetters[c - 'A'] + 'Α' - 'α'),
            >= 'α' and <= 'ω' => LatinLetters[c - 'α'],
            >= 'Α' and <= 'Ω' => (char)(LatinLetters[c - 'Α'] + 'A' - 'a'),
            'ϑ' => 'v',
            'ø' => 'j',
            'Ø' => 'J',
            '∡' => 'V',
            '@' => '°',
            '\'' => '′',
            '"' => '″',
            '°' => '@',
            '′' => '\'',
            '″' => '"',
            _ => c
        };

        private async void RichTextBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (IsCalculated)
            {
                _scrollY = await _wv2Warper.GetScrollYAsync();
                await ScrollOutput();
            }
        }

        private void WebViewer_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.F5)
            {
                Calculate();
                e.Handled = true;
            }
            else if (e.Key == Key.O && Keyboard.Modifiers == ModifierKeys.Control)
            {
                Command_Open(this, null);
                e.Handled = true;
            }
            else if (e.Key == Key.F11)   // modo "solo gráficas" también con foco en el WebView
            {
                ToggleWebOnlyMode();
                e.Handled = true;
            }
            else if (e.Key == Key.Escape && _webOnlyMode)
            {
                ToggleWebOnlyMode();
                e.Handled = true;
            }
            else if (e.Key == Key.Z && Keyboard.Modifiers == ModifierKeys.Control)
            {
                // Ctrl+Z con foco en el WebView2 → deshacer del canvas de dibujo (talud/ginput),
                // si existe. Evita que el comando Undo del editor de código se lo lleve.
                try { WebViewer?.CoreWebView2?.ExecuteScriptAsync("window.__hktUndo&&window.__hktUndo()"); } catch { }
                e.Handled = true;
            }
        }

        private void AutoRunCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            if (IsInitialized)
            {
                if (IsAutoRun && !IsCalculated)
                    Calculate();

                RichTextBox.Focus();
                Keyboard.Focus(RichTextBox);
            }
        }

        private void AutoRunCheckBox_Unchecked(object sender, RoutedEventArgs e)
        {
            RichTextBox.Focus();
            Keyboard.Focus(RichTextBox);
        }

        private void RichTextBox_PreviewMouseDown(object sender, MouseButtonEventArgs e)
        {
            IsWebView2Focused = false;
            _isTextChangedEnabled = false;
            RichTextBox.Selection.ApplyPropertyValue(TextElement.BackgroundProperty, null);
            AutoCompleteListBox.Visibility = Visibility.Hidden;
            _isTextChangedEnabled = true;
        }

        private void FindReplace_BeginSearch(object sender, EventArgs e)
        {
            _autoRun = false;
            _isTextChangedEnabled = false;
        }

        private void FindReplace_EndSearch(object sender, EventArgs e)
        {
            _isTextChangedEnabled = true;
        }

        private void FindReplace_EndReplace(object sender, EventArgs e)
        {
            Task.Run(() => Dispatcher.InvokeAsync(
                HighLightAll,
                DispatcherPriority.Send));
            Task.Run(() => Dispatcher.InvokeAsync(SetAutoIndent, DispatcherPriority.Normal));
        }

        private void RichTextBox_PreviewTextInput(object sender, TextCompositionEventArgs e)
        {
            if (_countKeys == int.MaxValue)
                _countKeys = int.MinValue;

            ++_countKeys;
            if (!_autoCompleteManager.IsInComment())
            {
                Task.Run(() => Dispatcher.InvokeAsync(() => _autoCompleteManager.InitAutoComplete(e.Text, _currentParagraph), DispatcherPriority.Send));
            }
            // Disparo de la ventana-loop: ".loop" termina en 'p'. Task.Run para
            // que corra DESPUÉS de que la 'p' se inserte (igual que autocomplete).
            if (e.Text == "p")
                Task.Run(() => Dispatcher.InvokeAsync(CheckLoopTrigger, DispatcherPriority.Send));
        }

        private void Window_Activated(object sender, EventArgs e)
        {
            if (_calculateOnActivate)
            {
                if (IsAutoRun)
                    CalculateAsync();
                else
                    Calculate();
                _calculateOnActivate = false;
            }
        }

        private void CodeCheckBox_Click(object sender, RoutedEventArgs e)
        {
            ClearOutput();
        }

        private void SetCodeCheckBoxVisibility() =>
            CodeCheckBorder.Visibility = _highlighter.Defined.HasMacros ? Visibility.Visible : Visibility.Hidden;


        private static void ShowErrorMessage(string message) =>
            MessageBox.Show(message, "Hekatan Lab", MessageBoxButton.OK, MessageBoxImage.Error);

        private async void Window_ContentRendered(object sender, EventArgs e)
        {
            try
            {
                StartupMark("Window_ContentRendered: start");
                // Iniciar WebView2 init y ESPERARLO antes de cualquier Navigate.
                // Si no, TryOpenOnStartup → ShowHelp → WebView2Wrapper.Navigate
                // tira NullReferenceException porque _wv2.CoreWebView2 todavía es null.
                var webViewInitTask = InitializeWebViewer();
                StartupMark("Window_ContentRendered: WebView2 init started");
                _webViewInitTask = webViewInitTask;
                await webViewInitTask;
                StartupMark("Window_ContentRendered: WebView2 init awaited");
                TryOpenOnStartup();
                StartupMark("Window_ContentRendered: TryOpenOnStartup done");
                TryRestoreState();
                StartupMark("Window_ContentRendered: TryRestoreState done");
                RichTextBox.Focus();
                Keyboard.Focus(RichTextBox);
                _warmupTask = StartPipelineWarmup();   // ahora sí: la ventana ya está montada
                StartupMark("Window_ContentRendered: complete (warmup MATLAB lanzado)");
            }
            catch (Exception ex)
            {
                StartupMark($"Window_ContentRendered: EXCEPTION {ex.GetType().Name}: {ex.Message}");
                try
                {
                    var dump = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "calcpad_lab_crash.txt");
                    System.IO.File.WriteAllText(dump, $"{ex.GetType().FullName}\n{ex.Message}\n\nStack:\n{ex.StackTrace}\n\nInner: {ex.InnerException}");
                }
                catch { }
                System.Windows.MessageBox.Show($"Hekatan Lab error:\n\n{ex.GetType().Name}: {ex.Message}", "Hekatan Lab", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
            }
        }
        private Task _webViewInitTask;

        private async Task InitializeWebViewer()
        {
            StartupMark("InitializeWebViewer: start");
            var options = new CoreWebView2EnvironmentOptions("--allow-file-access-from-files");
            // WebView2 bloquea la carpeta de datos con un único escritor: en modo headless
            // (--shot/--gif) usamos una carpeta temporal ÚNICA por proceso para que la captura
            // funcione aunque el usuario tenga otra instancia abierta (si no, CreateAsync falla
            // sobre la carpeta compartida y sale ventana en blanco sin PNG).
            bool headlessShot = Environment.GetCommandLineArgs().Any(a => a == "--shot" || a == "--gif");
            var userDataFolder = headlessShot
                ? Path.Combine(Path.GetTempPath(), "CalcpadLabWebView2_shot_" + Environment.ProcessId)
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CalcpadLabWebView2");
            var env = await CoreWebView2Environment.CreateAsync(
                null,
                userDataFolder,
                options
            );
            StartupMark("InitializeWebViewer: CoreWebView2Environment created");
            await WebViewer.EnsureCoreWebView2Async(env);
            StartupMark("InitializeWebViewer: EnsureCoreWebView2 done");
            // DefaultBackgroundColor DESPUES de EnsureCoreWebView2Async — antes
            // de inicializarse, setearlo tira COMException 0x8007139F (ERROR_INVALID_STATE).
            WebViewer.DefaultBackgroundColor = System.Drawing.Color.White;
            RichTextBox.IsEnabled = true;
            WebViewer.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "calcpad.local",
                 AppInfo.DocPath,
                CoreWebView2HostResourceAccessKind.Allow);

            WebViewer.CoreWebView2.Settings.AreDevToolsEnabled = true;
            WebViewer.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
            WebViewer.CoreWebView2.Settings.AreBrowserAcceleratorKeysEnabled = true;

        }

        private void MenuCli_Click(object sender, RoutedEventArgs e)
        {
            Execute(AppInfo.Path + "CalcpadLabCli.exe");   // antes "Cli.exe": no existia (2026-09-05)
        }

        private ExcelViewerWindow _excelViewerWindow;

        private void MenuExcelViewer_Click(object sender, RoutedEventArgs e)
        {
            if (_excelViewerWindow == null || !_excelViewerWindow.IsLoaded)
            {
                _excelViewerWindow = new ExcelViewerWindow { Owner = this };
                _excelViewerWindow.Closed += (_, _) => _excelViewerWindow = null;
                _excelViewerWindow.Show();
            }
            else
            {
                _excelViewerWindow.Activate();
            }
        }

        private void ZeroSmallMatrixElementsCheckBox_Click(object sender, RoutedEventArgs e) => ClearOutput();

        private void MaxOutputCountTextBox_KeyUp(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
                ClearOutput(false);
        }

        private void MaxOutputCountTextBox_LostFocus(object sender, RoutedEventArgs e) => ClearOutput(false);

        private void PasteAsCommentMenu_Click(object sender, RoutedEventArgs e)
        {
            RichTextBox.BeginChange();
            RichTextBox.Selection.Text = string.Empty;
            InsertLines(Clipboard.GetText(), Environment.NewLine, true);
            RichTextBox.EndChange();
            RichTextBox.Focus();
        }

        private void CommentUncomment(bool comment)
        {
            var ss = RichTextBox.Selection.Start;
            var ps = ss.Paragraph;
            var se = RichTextBox.Selection.End;
            var pe = se.Paragraph;
            var lineNumber = GetLineNumber(ps);
            bool matches;
            RichTextBox.BeginChange();
            var start = true;
            do
            {
                if (ps is null)
                    break;
                var tr = new TextRange(ps.ContentStart, ps.ContentEnd);
                var text = tr.Text;
                // Hekatan Lab usa el comentario de MATLAB '%' (no el "'" de Calcpad puro).
                // Al descomentar reconocemos tambien "'" y '"' por compatibilidad con documentos viejos.
                var isComment = text.StartsWith('%') ||
                    text.StartsWith('\'') ||
                    text.StartsWith('"');
                if (comment != isComment)
                {
                    if (comment)
                        tr.Text = "%" + text;
                    else
                        tr.Text = text[1..];
                }
                _highlighter.Defined.Get(tr.Text, lineNumber);
                _highlighter.Parse(ps, IsComplex, lineNumber, start);
                start = false;
                matches = ReferenceEquals(ps, pe);
                ps = ps.NextBlock as Paragraph;
            } while (!matches);
            _currentParagraph = pe;
            HighLighter.Clear(_currentParagraph);
            SetAutoIndent();
            RichTextBox.Selection.Select(ss, se);
            RichTextBox.EndChange();
            RichTextBox.Focus();
        }

        private void CommentMenu_Click(object sender, RoutedEventArgs e) =>
            CommentUncomment(true);


        private void WebViewer_WebMessageReceived(object sender, CoreWebView2WebMessageReceivedEventArgs e)
        {
            var message = e.TryGetWebMessageAsString();
            if (message == "clicked")
                WebViewer_LinkClicked();
            else if (message == "focused")
                IsWebView2Focused = true;
            else if (message == "focusweb")   // el canvas de dibujo pide foco de teclado (para Ctrl+Z)
            {
                IsWebView2Focused = true;
                try { WebViewer.Focus(); Keyboard.Focus(WebViewer); } catch { }
            }
            else if (message != null && message.StartsWith("{"))
                OnControlMessage(message);   // Piso 3: {type:'ctrl',name,value}
        }

        // Piso 3: un control interactivo (slider/numbox/checkbox) cambió en el WebView2.
        // Guardamos su valor y re-ejecutamos el script (con debounce para arrastres rápidos).
        private void OnControlMessage(string json)
        {
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var t)) return;
                var ty = t.GetString();
                if (ty == "ctrl")
                {
                    var name = root.GetProperty("name").GetString();
                    var val  = root.GetProperty("value").GetDouble();
                    if (string.IsNullOrEmpty(name)) return;
                    _controlValues[name] = val;
                }
                else if (ty == "geom")   // Piso 3: geometría dibujada con el cursor (ginput)
                {
                    var name = root.GetProperty("name").GetString();
                    if (string.IsNullOrEmpty(name)) return;
                    var list = new System.Collections.Generic.List<double[]>();
                    foreach (var pt in root.GetProperty("points").EnumerateArray())
                    {
                        double px = 0, pz = 0; int k = 0;
                        foreach (var c in pt.EnumerateArray()) { if (k == 0) px = c.GetDouble(); else if (k == 1) pz = c.GetDouble(); k++; }
                        list.Add(new[] { px, pz });
                    }
                    _geomValues[name] = list.ToArray();
                }
                else return;
            }
            catch { return; }
            // THROTTLE (no debounce): mientras se ARRASTRA el slider llegan mensajes sin parar;
            // un debounce (reiniciar el timer con cada uno) solo dispararía AL SOLTAR. Con un
            // throttle repetitivo re-ejecutamos cada ~90 ms DURANTE el arrastre → EN VIVO.
            _ctrlPending = true;
            if (_ctrlDebounce == null)
            {
                _ctrlDebounce = new System.Windows.Threading.DispatcherTimer
                { Interval = System.TimeSpan.FromMilliseconds(90) };
                _ctrlDebounce.Tick += CtrlThrottleTick;
            }
            if (!_ctrlDebounce.IsEnabled) _ctrlDebounce.Start();
        }

        private bool _ctrlPending;
        private int _ctrlIdleTicks;
        private void CtrlThrottleTick(object sender, System.EventArgs e)
        {
            if (_isParsing) return;              // cálculo en curso → esperar al próximo tick
            if (_ctrlPending)
            {
                _ctrlPending = false; _ctrlIdleTicks = 0;
                _recalcFromControl = true;       // saltar el guard de "source sin cambios"
                CalculateAsync();
            }
            else if (++_ctrlIdleTicks > 8)       // ~0.7 s sin cambios → parar el timer
            {
                _ctrlDebounce.Stop();
            }
        }

        private async void WebViewer_LinkClicked()
        {
            var s = await _wv2Warper.GetLinkDataAsync();
            if (s is null)
                return;

            if (Uri.IsWellFormedUriString(s, UriKind.Absolute))
                Execute(ExternalBrowserComboBox.Text.ToLower() + ".exe", s);
            else
            {
                var fileName = s.Replace('/', '\\');
                var path = Path.GetFullPath(fileName);
                if (File.Exists(path))
                {
                    fileName = path;
                    var ext = Path.GetExtension(fileName).ToLowerInvariant();
                    if (ext == ".cpd" || ext == ".cpdz" || ext == ".txt")
                    {
                        var r = PromptSave();
                        if (r != MessageBoxResult.Cancel)
                            FileOpen(fileName);
                    }
                    else if (ext == ".htm" ||
                        ext == ".html" ||
                        ext == ".png" ||
                        ext == ".jpg" ||
                        ext == ".jpeg" ||
                        ext == ".gif" ||
                        ext == ".bmp")
                        Execute(ExternalBrowserComboBox.Text.ToLower() + ".exe", s);
                }
                else if (s == "continue")
                    await AutoRun();
                else if (s == "cancel")
                    Cancel();
                else if (IsCalculated || _parser.IsPaused)
                    LineClicked(s);
                else if (!IsWebForm)
                    LinkClicked(s);
            }
        }

        private void MarkdownCheckBox_Checked(object sender, RoutedEventArgs e)
        {
            var blocks = _document.Blocks;
            if (MarkdownCheckBox.IsChecked == true)
            {
                var n1 = LastIndexOfParagraphContaining("#md");
                var n2 = LastIndexOfParagraphContaining("#md off");
                var n3 = LastIndexOfParagraphContaining("#md on");
                n1 = Math.Max(n1, n3);
                if (n1 < 0)
                {
                    n2 = n2 < 0 ? 0 : _currentLineNumber;
                    var p = new Paragraph(new Run("#md on") { Foreground = HighLighter.KeywordBrush });
                    var b = blocks.ElementAt(n2);
                    if (b is not null)
                        blocks.InsertBefore(b, p);
                }
                else if (n2 > n1)
                {
                    var p = new Paragraph(new Run("#md on") { Foreground = HighLighter.KeywordBrush });
                    if (_currentParagraph is not null)
                        blocks.InsertBefore(_currentParagraph, p);
                }
            }

            int LastIndexOfParagraphContaining(string s)
            {
                var i = _currentLineNumber;
                var p = _currentParagraph;
                while (p is not null && i >= 0)
                {
                    var text = new TextRange(p.ContentStart, p.ContentEnd).Text;
                    if (text.Trim() == s)
                        return i;
                    --i;
                    p = p.PreviousBlock as Paragraph;
                }
                return -1;
            }
        }

        private void UncommentMenu_Click(object sender, RoutedEventArgs e) =>
            CommentUncomment(false);

        private void RichTextBox_GotKeyboardFocus(object sender, KeyboardFocusChangedEventArgs e)
        {
            IsWebView2Focused = false;
        }

        // ===================== PLUGIN SYSTEM =====================

        private void LoadPlugins()
        {
            var pluginsDir = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Plugins");
            if (!Directory.Exists(pluginsDir)) return;
            try
            {
                foreach (var dll in Directory.GetFiles(pluginsDir, "*.dll"))
                {
                    var asm = System.Reflection.Assembly.LoadFrom(dll);
                    foreach (var type in asm.GetExportedTypes())
                    {
                        if (typeof(Calcpad.Core.Plugins.ICalcpadPlugin).IsAssignableFrom(type) && !type.IsInterface)
                        {
                            _plugin = (Calcpad.Core.Plugins.ICalcpadPlugin)Activator.CreateInstance(type)!;
                            MenuExportPlugin.Header = _plugin.ExportMenuText;
                            MenuImportPlugin.Header = _plugin.ImportMenuText;
                            MenuExportPlugin.Visibility = _plugin.CanExport ? Visibility.Visible : Visibility.Collapsed;
                            MenuImportPlugin.Visibility = _plugin.CanImport ? Visibility.Visible : Visibility.Collapsed;
                            return;
                        }
                    }
                }
            }
            catch { }
        }

        private void MenuExportPlugin_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var dlg = new Microsoft.Win32.SaveFileDialog
            {
                Filter = _plugin.FileFilter,
                DefaultExt = _plugin.DefaultExtension,
                FileName = Path.GetFileNameWithoutExtension(Title)
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var result = _plugin.Export(InputText, dlg.FileName);
                    MessageBox.Show(result, _plugin.Name, MessageBoxButton.OK, MessageBoxImage.Information);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Plugin Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }

        private void MenuImportPlugin_Click(object sender, RoutedEventArgs e)
        {
            if (_plugin == null) return;
            var dlg = new Microsoft.Win32.OpenFileDialog
            {
                Filter = _plugin.FileFilter,
                DefaultExt = _plugin.DefaultExtension
            };
            if (dlg.ShowDialog() == true)
            {
                try
                {
                    var cpd = _plugin.Import(dlg.FileName);
                    // Save imported CPD and open it
                    var cpdPath = Path.ChangeExtension(dlg.FileName, ".cpd");
                    File.WriteAllText(cpdPath, cpd);
                    CurrentFileName = cpdPath;
                    GetInputTextFromFile();
                    Title = AppInfo.Title + " - " + Path.GetFileName(cpdPath);
                }
                catch (Exception ex)
                {
                    MessageBox.Show(ex.Message, "Plugin Error", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
        }
    }
}