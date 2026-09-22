using System.Diagnostics;
var sw = Stopwatch.StartNew();
var p = new Calcpad.Core.Matlab.MatlabPipeline();
System.Console.WriteLine($"{sw.ElapsedMilliseconds,6} ms  ctor pipeline");
foreach (var l in new[] { "x = 5;\n", "disp(x);\n", "wLit = 5\n", "wSum = x + 1\n", "yWarm = 2*x + 1\n" })
{
    var t = sw.ElapsedMilliseconds;
    p.Run(l);
    System.Console.WriteLine($"{sw.ElapsedMilliseconds - t,6} ms  {l.Trim()}");
}
