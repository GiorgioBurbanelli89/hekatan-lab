# Auditoría de render de ejemplos — Hekatan Lab (2026-08-10)

Test: [`render_examples_sweep.ps1`](render_examples_sweep.ps1) — renderiza cada `.m` de
`Examples-Lab/` a PNG via `HekatanLab.exe --shot` (headless) y clasifica el resultado.

## Comando de render (headless)
```
HekatanLab.exe --shot <salida.png> <archivo.m>
```
- Orden: `--shot`, ruta PNG, luego el `.m` (`MainWindow.xaml.cs:3636`).
- Headless real, se cierra solo (`CaptureWebViewerAndExit` → `Application.Current.Shutdown()`), blindaje anti-cuelgue interno.
- `HK_GL_HEADLESS=1` fuerza el render GL de animaciones también en headless.
- Animaciones: `--gif <dir> [nframes] [intervalMs]` vuelca PNGs numerados.
- Errores de render → `%TEMP%\calcpad_lab_log.html` como `<p class="err">Error on line N: ...`.

## Resultado global
**218 ejemplos** en `Examples-Lab/` (24 subcarpetas). **Rendean bien esencialmente todos.**
Los únicos problemas REALES son 3 archivos vacíos y 1 animación lenta (ver abajo).

> ### ⚠️ CAUSA DE LOS FALSOS FALLOS: huérfanos de WebView2 (bug conocido)
> Al lanzar `--shot` repetidamente, **matar `HekatanLab` NO mata sus hijos `msedgewebview2`** →
> se acumulan y el `--shot` se **cuelga determinísticamente** (PNG 0 bytes / TIMEOUT), aunque el
> render EN VIVO funciona perfecto. Todo "fallo" del barrido masivo (`paso1_malla_bfs`,
> `slab_timing`, `malla_delaunay_demo`, `timoshenko` PNG=0, etc.) era ESTO, no un bug de motor
> ni del ejemplo: **re-corridos AISLADOS y con el árbol limpio dan exit=0 con PNG** (p.ej.
> `paso1_malla_bfs` → PNG 127 KB; repro de `patch` en bucle → PNG 15 KB).
>
> **`render_examples_sweep.ps1` NO mata el webview2 antes de cada shot** (hacerlo fuerza re-init
> lento + locks del user-data-dir → TIMEOUT falso; verificado). En su lugar: si un shot falla,
> corre `Clear-ShotOrphans` (mata `HekatanLab`+`msedgewebview2`, borra `%TEMP%\CalcpadLabWebView2_shot_*`)
> y **REINTENTA una vez aislado** — que empíricamente siempre rinde el PNG. Solo si el reintento
> también falla es un fallo REAL. Nota: el reintento cierra TODAS las instancias de WebView2.
> (Fix de motor pendiente y aparte: que `--shot` mate/limpie sus propios hijos al salir, para que
> ni el reintento haga falta.)
>
> **NO existe ningún "bug de `patch` en bucle".** Un `patch(x,y,c)` repetido rendea bien; el
> `png=0` que se vio antes fue este mismo artefacto de huérfanos.

## Hallazgos que requieren acción

### 1. Ejemplos VACÍOS (0 líneas) — decisión de contenido
- `11 Columnas CFT/Inercia_columna_hueca_acero.m`
- `15 Ejemplos Variados/Ejemplo_2.m`
- `15 Ejemplos Variados/Operaciones_Funcion_unique.m`
Acción sugerida: llenarlos con un ejemplo real acorde a su carpeta, o borrarlos.

### 2. NO son fallos (dejar como están)
- `05 Framework FEM MATLAB/timoshenko_edge_beams.m`: solo `fprintf` (benchmark numérico, sin figura)
  → PNG=0 es correcto. Usaba `graphics_toolkit('gnuplot')` (Octave); ya se añadió como no-op en el
  motor (`MatlabEvaluator.cs`, junto a `clc`).
- `21 Dano Q4/q4_damage.m`: análisis de daño con animación → lento (`--shot` >60 s). Probar con
  `--gif` y timeout amplio; no es un bug, es costo de cómputo.
- Harness que NO son ejemplos (usan `exit`, `print -dpng`, `eval(fileread(...))`): `wrapml.m`,
  `run_ml_acople.m` → excluir del barrido.
