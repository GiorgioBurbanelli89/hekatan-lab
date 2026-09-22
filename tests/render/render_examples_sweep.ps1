# =============================================================================
# TEST DE RENDER — Hekatan Lab: renderiza CADA ejemplo .m a PNG (y GIF si anima)
# via HekatanLab.exe --shot / --gif (headless) y clasifica el resultado.
#
# Uso:   pwsh -NoProfile -File tests\render\render_examples_sweep.ps1
# Salida: tests\render\out\results.csv  +  PNGs en tests\render\out\pngs\
#         resumen por consola + tests\render\out\summary.txt
#
# Clasificacion:
#   OK        exit=0 y PNG > 0 bytes
#   NOFIG     exit=0, sin error, PNG=0  (script de solo-texto/datos: NO es fallo)
#   ERROR     el log del motor tiene <p class="err"> (fallo real de render)
#   TIMEOUT   no termino en el limite
#   EMPTY     archivo .m vacio
#   NONZERO   exit != 0 sin error en el log (revisar: suele ser lentitud/carga)
# Nota: correr AISLADO (esta maquina, sin otra carga) evita falsos TIMEOUT/NONZERO
#       por el blindaje anti-cuelgue interno cuando la CPU esta saturada.
# =============================================================================
param(
  [int]$TimeoutSec = 90,
  [string]$Only = ""    # subcadena para filtrar (p.ej. "18 FEA Slab") ; vacio = todos
)
$ErrorActionPreference = 'Continue'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$repo = Resolve-Path (Join-Path $here "..\..")
$exe  = Join-Path $repo "Symbolic.Wpf\bin\Release\net10.0-windows\HekatanLab.exe"
$root = Join-Path $repo "Examples-Lab"
$out  = Join-Path $here "out"
$pngd = Join-Path $out "pngs"
New-Item -ItemType Directory -Force -Path $pngd | Out-Null
$csv  = Join-Path $out "results.csv"
$log  = Join-Path $env:TEMP "calcpad_lab_log.html"
$env:HK_GL_HEADLESS = "1"
if (-not (Test-Path $exe)) { Write-Error "No existe $exe . Compila: dotnet build Symbolic.Wpf/Symbolic.Wpf.csproj -c Release"; exit 1 }

"idx;rel;kind;status;exit;secs;pngbytes;errline;err" | Out-File -LiteralPath $csv -Encoding UTF8
$files = Get-ChildItem -Path $root -Recurse -Filter *.m | Sort-Object FullName
if ($Only) { $files = $files | Where-Object { $_.FullName -like "*$Only*" } }

# --- Limpieza de huérfanos WebView2 (BUG CONOCIDO): al repetir --shot, matar HekatanLab NO
#     mata sus hijos msedgewebview2 -> se acumulan y el --shot se cuelga determinísticamente
#     (PNG 0 bytes / TIMEOUT), aunque el render EN VIVO funciona. Hay que matar el árbol y
#     limpiar las carpetas de user-data por-PID ANTES de cada shot.
#     OJO: cierra TODAS las instancias de WebView2 -> no uses apps con WebView2 (Edge, algún
#     panel de VS Code) mientras corre este test.
function Clear-ShotOrphans {
  Get-Process HekatanLab,msedgewebview2 -ErrorAction SilentlyContinue | Stop-Process -Force -ErrorAction SilentlyContinue
  Get-ChildItem -Path $env:TEMP -Directory -Filter 'CalcpadLabWebView2_shot_*' -ErrorAction SilentlyContinue |
    Remove-Item -Recurse -Force -ErrorAction SilentlyContinue
  Start-Sleep -Milliseconds 400
}

$tally = @{ OK=0; NOFIG=0; ERROR=0; TIMEOUT=0; EMPTY=0; NONZERO=0 }
$i = 0
foreach ($f in $files) {
  $i++
  $rel = $f.FullName.Substring($root.Length+1)
  $lines = Get-Content -LiteralPath $f.FullName -ErrorAction SilentlyContinue
  $firstReal = ($lines | Where-Object { $_ -notmatch '^\s*(%|$)' } | Select-Object -First 1)
  $kind = if ($firstReal -match '^\s*function\b') { 'FUNC' } else { 'script' }
  $png = Join-Path $pngd ("{0:D3}.png" -f $i)

  $secs=0; $exit='NA'; $sz=0; $errline=''; $err=''; $status=''
  if (($lines | Measure-Object).Count -eq 0) {
    $status='EMPTY'; $exit='EMPTY'
  } else {
    # SIN pre-kill (matar el webview2 compartido antes de cada shot fuerza re-init lento + locks
    # -> TIMEOUT falso). En su lugar: si un shot falla, limpiar el árbol de huérfanos y REINTENTAR
    # UNA vez aislado (empíricamente eso siempre rinde el PNG). Solo si el reintento también falla
    # es un fallo REAL.
    $attempt=0
    do {
      $attempt++
      if ($attempt -gt 1) { Clear-ShotOrphans }   # limpieza solo antes del reintento
      Remove-Item $png,$log -ErrorAction SilentlyContinue
      $exit='NA'; $sz=0; $errline=''; $err=''; $status=''
      $sw=[Diagnostics.Stopwatch]::StartNew()
      try {
        $p = Start-Process -FilePath $exe -ArgumentList @('--shot',$png,$f.FullName) -PassThru -WindowStyle Hidden
        $ok = $p.WaitForExit($TimeoutSec*1000); $sw.Stop()
        $secs=[math]::Round($sw.Elapsed.TotalSeconds,1)
        if (-not $ok) { try{$p.Kill($true)}catch{}; $exit='TIMEOUT'; $status='TIMEOUT' } else { $exit=$p.ExitCode }
      } catch { $exit='LAUNCHERR'; $err=$_.Exception.Message; $status='ERROR' }
      if (Test-Path $png) { $sz=(Get-Item $png).Length }
      if (Test-Path $log) {
        $h = Get-Content -LiteralPath $log -Raw -ErrorAction SilentlyContinue
        $m = [regex]::Match($h,'class="err">\s*Error on line (\d+):\s*(.*?)</p>','Singleline')
        if ($m.Success) {
          $errline=$m.Groups[1].Value
          $t=[regex]::Replace($m.Groups[2].Value,'<[^>]+>',''); $t=[regex]::Replace($t,'\s+',' ')
          $err=[System.Net.WebUtility]::HtmlDecode($t.Trim())
        }
      }
      if ($status -eq '') {
        if     ($err -ne '')       { $status='ERROR' }
        elseif ($exit -ne 0)       { $status='NONZERO' }
        elseif ($sz -gt 0)         { $status='OK' }
        else                       { $status='NOFIG' }
      }
    } while ($status -in @('TIMEOUT','NONZERO') -and $attempt -lt 2)
  }
  $tally[$status]++
  $errC = ($err -replace ';',',')
  "$i;$rel;$kind;$status;$exit;$secs;$sz;$errline;$errC" | Out-File -LiteralPath $csv -Append -Encoding UTF8
  Write-Host ("[{0,3}] {1,-7} {2}" -f $i,$status,$rel)
}
$sum = "TOTAL=$i  OK=$($tally.OK)  NOFIG=$($tally.NOFIG)  ERROR=$($tally.ERROR)  NONZERO=$($tally.NONZERO)  TIMEOUT=$($tally.TIMEOUT)  EMPTY=$($tally.EMPTY)"
$sum | Tee-Object -FilePath (Join-Path $out "summary.txt")
"`nRevisar (no-OK):" | Out-File -Append (Join-Path $out "summary.txt")
Import-Csv -Delimiter ';' $csv | Where-Object { $_.status -notin @('OK','NOFIG') } |
  ForEach-Object { "  $($_.status)  $($_.rel)  $($_.err)" } | Tee-Object -Append -FilePath (Join-Path $out "summary.txt")
