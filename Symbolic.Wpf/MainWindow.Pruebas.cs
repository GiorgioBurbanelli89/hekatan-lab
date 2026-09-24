using System.Linq;
using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;

namespace Calcpad.Wpf
{
    /// <summary>
    /// EL CANAL DE PRUEBAS DEL EDITOR (se engancha al canal de control <c>--ctl &lt;carpeta&gt;</c>).
    ///
    /// Por que hace falta: el editor no se puede probar como el motor. El motor se prueba con
    /// el CLI (entra un .m, sale un numero). Aqui lo que hay que comprobar es que un BOTON DE
    /// LA XAML, con el cursor en cierto sitio, deja el texto EXACTO que toca — y eso solo lo
    /// sabe la ventana viva. Mirar el PNG sirve para ver COMO queda, no para que una suite
    /// diga si/no sola.
    ///
    /// Como funciona: la app arranca con <c>--ctl carpeta</c> y se queda escuchando. La
    /// terminal deja <c>cmd-0001.json</c> en esa carpeta y la app contesta con
    /// <c>resp-0001.json</c>. Un solo arranque sirve para muchos casos (arrancar la ventana
    /// cuesta ~2 s; el caso, milisegundos).
    ///
    /// Operaciones (las de <see cref="CtlExecute"/> siguen valiendo: run, settext, capture,
    /// js, getoutput, quit):
    ///   {"op":"setcode","text":"..."}          poner el codigo en el editor que se VE
    ///   {"op":"gettext"}                       leer ese codigo -> {"text":"..."}
    ///   {"op":"setcaret","line":2,"col":20}    cursor (col opcional = final de linea)
    ///   {"op":"select","line":2,"col":20,"len":9}
    ///   {"op":"button","name":"BoldButton"}    PULSAR el boton de la XAML (por su x:Name)
    ///   {"op":"button","tag":"&lt;em&gt;‖&lt;/em&gt;"}      ...o uno inventado, por su Tag
    ///   {"op":"buttons"}                       inventario de botones con Tag -> {name,tag}
    ///   {"op":"knows","names":[...]}           ¿el MOTOR conoce estos nombres? (ver Conoce)
    ///   {"op":"gotoline","line":5}             como pulsar esa linea en el reporte
    ///   {"op":"fold","all":true|false}         plegar / desplegar todo
    ///   {"op":"state"}                         editor activo, linea/columna, seleccion, pliegues,
    ///                                          y el MOTOR con el que se calculo lo ultimo
    /// </summary>
    public partial class MainWindow
    {
        /// <summary>Operaciones de prueba del editor. Devuelve null si la op no es de aqui
        /// (entonces el canal contesta "op desconocida" como siempre).</summary>
        private string CtlEditorOp(string op, JsonElement root)
        {
            switch (op)
            {
                case "setcode":
                    {
                        var texto = Txt(root, "text") ?? string.Empty;
                        if (!EditorPlegableActivo) return Error("el editor plegable no esta activo");
                        AvalonEditor.Text = texto.Replace("\r\n", "\n").Replace("\n", Environment.NewLine);
                        AvalonEditor.CaretOffset = 0;
                        return Ok();
                    }
                case "gettext":
                    return "{\"ok\":true,\"text\":" + Json(TextoDelEditor()) + "}";

                case "setcaret":
                case "select":
                    {
                        if (!EditorPlegableActivo) return Error("el editor plegable no esta activo");
                        var (off, ok) = Posicion(root);
                        if (!ok) return Error("linea fuera del documento");
                        var len = (int)Num(root, "len", 0);
                        if (op == "select" && len > 0) AvalonEditor.Select(off, len);
                        else AvalonEditor.CaretOffset = off;
                        AvalonEditor.Focus();
                        return Ok();
                    }
                case "button":
                    {
                        var nombre = Txt(root, "name");
                        FrameworkElement el;
                        if (!string.IsNullOrEmpty(nombre))
                        {
                            el = FindName(nombre) as FrameworkElement;
                            if (el is null) return Error($"no hay ningun control llamado {nombre}");
                            if (el.Tag is null) return Error($"{nombre} no tiene Tag");
                        }
                        else
                        {
                            var tag = Txt(root, "tag");
                            if (string.IsNullOrEmpty(tag)) return Error("falta name o tag");
                            el = new Button { Tag = tag };
                        }
                        Button_Click(el, null);
                        return "{\"ok\":true,\"tag\":" + Json(el.Tag.ToString()) + "}";
                    }
                case "buttons":
                    return Inventario();

                case "knows":
                    return Conoce(root);

                case "gotoline":
                    LineClicked(((int)Num(root, "line", 1)).ToString());
                    return Ok();

                case "fold":
                    {
                        var todo = root.TryGetProperty("all", out var a) && a.ValueKind == JsonValueKind.True;
                        if (todo) PlegarTodo_Click(null, null); else DesplegarTodo_Click(null, null);
                        return Ok();
                    }
                case "state":
                    return Estado();

                // Anchos del reparto Codigo | divisor | Output, y DONDE esta el divisor en
                // pantalla (px fisicos) para arrastrarlo con el raton de verdad.
                case "layout":
                    {
                        var inv = System.Globalization.CultureInfo.InvariantCulture;
                        var p0 = MainSplitter.PointToScreen(new Point(0, 0));
                        var p1 = MainSplitter.PointToScreen(new Point(MainSplitter.ActualWidth, MainSplitter.ActualHeight));
                        return string.Format(inv,
                            "{{\"ok\":true,\"editor\":{0},\"splitter\":{1},\"web\":{2},\"sx0\":{3},\"sy0\":{4},\"sx1\":{5},\"sy1\":{6}}}",
                            EditorCol.ActualWidth, MainSplitter.ActualWidth, WebCol.ActualWidth,
                            (int)p0.X, (int)p0.Y, (int)p1.X, (int)p1.Y);
                    }

                // Zoom del codigo como Ctrl+rueda: {"op":"zoom","steps":+1|-1} -> tamaño de letra
                case "zoom":
                    {
                        var pasos = root.TryGetProperty("steps", out var sp) ? sp.GetInt32() : 1;
                        double fs = AvalonEditor.FontSize;
                        for (int k = 0; k < Math.Abs(pasos); k++) fs = ZoomEditor(Math.Sign(pasos));
                        return "{\"ok\":true,\"fontSize\":" + fs.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}";
                    }

                // Autocompletado SIN capturas: el popup es otra ventana y no sale en el PNG
                // (por eso «no funciona» parecia cierto cuando si funcionaba). Esto devuelve
                // lo que el popup ofreceria para un prefijo, y si el editor que lo tiene
                // —el PLEGABLE— esta activo, que es la condicion que lo apaga entero.
                case "complete":
                    {
                        var pre = root.TryGetProperty("prefix", out var pp) ? (pp.GetString() ?? "") : "";
                        var items = MatlabLang.Items(pre, null, int.MaxValue, _isDarkTheme).ToList();
                        var nombres = items.Take(12).Select(i => i.Text).ToArray();
                        return System.Text.Json.JsonSerializer.Serialize(new {
                            ok = true,
                            prefijo = pre,
                            plegable = EditorPlegableActivo,   // si es false, NO hay popup al escribir
                            total = items.Count,
                            primeros = nombres,
                        });
                    }

                default:
                    return null;
            }
        }

        private string TextoDelEditor()
        {
            if (EditorPlegableActivo) return AvalonEditor.Text;
            try { return GetInputText(); } catch { return string.Empty; }
        }

        /// <summary>Offset del documento para {line, col}. Sin col, el final de la linea.</summary>
        private (int, bool) Posicion(JsonElement root)
        {
            var doc = AvalonEditor.Document;
            var n = (int)Num(root, "line", 1);
            if (n < 1 || n > doc.LineCount) return (0, false);
            var l = doc.GetLineByNumber(n);
            if (!root.TryGetProperty("col", out _)) return (l.EndOffset, true);
            var c = (int)Num(root, "col", 1);
            return (Math.Min(l.Offset + Math.Max(0, c - 1), l.EndOffset), true);
        }

        private string Estado()
        {
            var sb = new System.Text.StringBuilder("{\"ok\":true");
            sb.Append(",\"editor\":").Append(EditorPlegableActivo ? "\"avalon\"" : "\"clasico\"");
            // Con QUE motor se calculo lo ultimo: "matlab" · "fortran" · "calcpad" · "nada".
            // Un .m que salga por "calcpad" esta pasando por el parser heredado.
            sb.Append(",\"motor\":").Append(Json(_ctlUltimoMotor ?? "nada"));
            if (EditorPlegableActivo)
            {
                var doc = AvalonEditor.Document;
                var l = doc.GetLineByOffset(AvalonEditor.CaretOffset);
                sb.Append(",\"line\":").Append(l.LineNumber);
                sb.Append(",\"col\":").Append(AvalonEditor.CaretOffset - l.Offset + 1);
                sb.Append(",\"lines\":").Append(doc.LineCount);
                sb.Append(",\"sel\":").Append(Json(AvalonEditor.SelectedText ?? string.Empty));
                int pliegues = 0, cerrados = 0;
                if (_foldingManager is not null)
                    foreach (var f in _foldingManager.AllFoldings) { ++pliegues; if (f.IsFolded) ++cerrados; }
                sb.Append(",\"folds\":").Append(pliegues).Append(",\"folded\":").Append(cerrados);
                sb.Append(",\"focus\":").Append(AvalonEditor.TextArea.IsKeyboardFocusWithin ? "true" : "false");
            }
            return sb.Append('}').ToString();
        }

        /// <summary>Todos los controles con Tag (barra de formato Y menus). Sirve para probar
        /// la XAML ENTERA de una vez: p.ej. que ninguna plantilla siga escribiendo comentarios
        /// de Calcpad (<c>'&lt;div&gt;</c>), que en MATLAB son "Unterminated char-array".</summary>
        private string Inventario()
        {
            var vistos = new List<string>();
            var sb = new System.Text.StringBuilder("{\"ok\":true,\"buttons\":[");
            var primero = true;
            Recorrer(this);
            return sb.Append("]}").ToString();

            void Recorrer(object nodo)
            {
                if (nodo is FrameworkElement fe && fe.Tag is string tag && tag.Length > 0 &&
                    (nodo is ButtonBase || nodo is MenuItem))
                {
                    var clave = (fe.Name ?? string.Empty) + "" + tag;
                    if (!vistos.Contains(clave))
                    {
                        vistos.Add(clave);
                        if (!primero) sb.Append(',');
                        primero = false;
                        sb.Append("{\"name\":").Append(Json(fe.Name ?? string.Empty))
                          .Append(",\"tag\":").Append(Json(tag)).Append('}');
                    }
                }
                if (nodo is DependencyObject dep)
                    foreach (var hijo in System.Windows.LogicalTreeHelper.GetChildren(dep))
                        Recorrer(hijo);
            }
        }

        /// <summary>¿El MOTOR conoce estos nombres? Se pregunta a <see cref="MatlabBuiltins"/>,
        /// que sale de los nombres REALES que registra el motor (Symbolic.Core/Matlab), no de
        /// una lista escrita a mano.
        ///
        /// Dos cajones:
        ///   motor — funcion, palabra clave o constante que el motor da (sin, zeros, end, pi);
        ///   nadie — NO existe en MATLAB: es herencia de Calcpad (√, atan2d escrito raro,
        ///           nombres de otro lenguaje) y en la hoja sale "Undefined function".
        /// Solo el segundo es un fallo. Sirve para vigilar los Tag de la XAML: lo que un
        /// boton escribe tiene que poder correr.</summary>
        private static string Conoce(JsonElement root)
        {
            if (!root.TryGetProperty("names", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return Error("falta names (lista de nombres)");

            var motor = new List<string>();
            var nadie = new List<string>();
            foreach (var e in arr.EnumerateArray())
            {
                var n = e.ValueKind == JsonValueKind.String ? e.GetString() : null;
                if (string.IsNullOrEmpty(n)) continue;
                if (Array.IndexOf(MatlabBuiltins.All, n) >= 0 ||
                    Array.IndexOf(MatlabBuiltins.Keywords, n) >= 0 ||
                    Array.IndexOf(MatlabBuiltins.Constants, n) >= 0) motor.Add(n);
                else nadie.Add(n);
            }
            return "{\"ok\":true,\"motor\":" + Lista(motor) + ",\"nadie\":" + Lista(nadie) + "}";
        }

        private static string Lista(IEnumerable<string> xs) =>
            "[" + string.Join(",", System.Linq.Enumerable.Select(xs, Json)) + "]";

        private static string Ok() => "{\"ok\":true}";
        private static string Error(string msg) => "{\"ok\":false,\"error\":" + Json(msg) + "}";
        private static string Json(string s) => JsonSerializer.Serialize(s);
        private static string Txt(JsonElement root, string nombre) =>
            root.TryGetProperty(nombre, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        private static double Num(JsonElement root, string nombre, double porDefecto) =>
            root.TryGetProperty(nombre, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : porDefecto;
    }
}
