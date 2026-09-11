# Avisos de terceros — Hekatan Lab

Hekatan Lab es un fork de **Calcpad** (Nedelcho Ganchovski / PROEKTSOFT EOOD, MIT) al que se
añadió un motor MATLAB propio (Jorge Burbano). Además incluye o carga los componentes de terceros
listados abajo. Cada uno conserva **su propia licencia**; nada aquí la cambia.

Los textos completos están en la carpeta [`licenses/`](licenses/) (en su idioma original, inglés).
"Por confirmar" = no se pudo verificar en el propio archivo/paquete; se indica qué revisar.

Revisado: 2026-09-11, rama `lab-ceincilab-compat`, carpeta de publicación
`CalcpadLab-Installer\CalcpadLab` (la que empaqueta `CalcpadLabInstaller.iss`).

---

## 1. Base del programa

| Componente | Versión | Autor / copyright | Licencia | Uso en Hekatan Lab | Dónde | Texto |
|---|---|---|---|---|---|---|
| **Calcpad** | fork (upstream https://codeberg.org/proektsoft/Calcpad) | Copyright (c) 2024 Ned Ganchovski; Copyright (c) 2023 Proektsoft EOOD (`doc/LICENSE.TXT`) | MIT | Ventana WPF (MainWindow), plantilla del reporte `Symbolic.Wpf/doc/template.html`, ExpressionParser y núcleo de Calcpad — **modificados** | todo el repo salvo el motor MATLAB | `LICENSE`, `licenses/Calcpad-MIT.txt` |
| Motor MATLAB (Tokenizer/Parser/Evaluator/Jit/Symbolic/HtmlWriter, BlasInterop) | — | Copyright (c) 2026 Jorge Burbano | MIT (ver `LICENSE`) | código propio | `Symbolic.Core/Matlab`, `Symbolic.Core/Native/*.cs` | `LICENSE` |

## 2. Bibliotecas nativas (DLL) cargadas en el mismo proceso

| Componente | Versión | Autor / copyright | Licencia | Uso en Hekatan Lab | Dónde | Texto |
|---|---|---|---|---|---|---|
| **Giac** (núcleo CAS de Xcas) | **por confirmar** (la DLL no trae cadena de versión; la web cita 1.9.0 y estable 2.0.0-21) — revisar de dónde se compiló `giac.dll` | Bernard Parisse y Renée De Graeve, Univ. Grenoble Alpes | **GPL-3.0** ("license GPL3, for commercial dual-license contact us", https://www-fourier.univ-grenoble-alpes.fr/~parisse/giac.html) | `int`, `simplify`, `factor`, `expand`, `limit`, `solve`, `symsum` van **primero a Giac** por P/Invoke (`GiacRunner.cs`, `giac::caseval`); si falla, motor propio | `Symbolic.Core/Native/giac.dll` (copia en `test_mex/giac.dll`) → `{app}\giac.dll` | `licenses/Giac-GPL-3.0.txt` |
| GMP y MPFR (enlazados **estáticos** dentro de `giac.dll`) | por confirmar | Free Software Foundation / equipo MPFR | LGPL-3.0 (GMP es dual LGPL-3.0/GPL-2.0) — verificado por símbolos `__gmp*`/`mpfr_*` en la DLL | aritmética de precisión arbitraria de Giac | dentro de `giac.dll` | `licenses/GMP-MPFR-LGPL-3.0.txt` + `licenses/Giac-GPL-3.0.txt` |
| Runtime GCC/MinGW (libstdc++, libgcc, libgfortran estáticos) | GCC 14.2.0 (en `liblapack.dll`) | Free Software Foundation | GPL-3.0 **con GCC Runtime Library Exception 3.1** | runtime de las DLL compiladas con MinGW (giac, OpenBLAS, LAPACK, eigen_solver, matlab_helpers, triangle) | dentro de esas DLL | `licenses/GCC-Runtime-Library-Exception-3.1.txt` |
| **Intel oneMKL** | 2026.1 (`*.3.dll`) **y** 2024.2 (`*.2.dll`, ambos en la carpeta `mkl\`) | Copyright (C) 2015-2026 Intel Corporation | Intel Simplified Software License (ISSL) — texto tomado del paquete pip `mkl` 2025.3.0 (versión Oct-2022). **Por confirmar** que 2026.1 use el mismo texto: revisar el `LICENSE`/`redist.txt` del paquete oneMKL 2026.1 del que salieron las DLL | BLAS/LAPACK/PARDISO rápidos (proveedor preferido en `BlasInterop.cs`) | `Symbolic.Core/Native/mkl/` (no está en git) → `{app}\mkl\` | `licenses/Intel-oneMKL-ISSL.txt` |
| Intel OpenMP runtime (`libiomp5md.dll`) | 5.0 (según la DLL) | Copyright (C) 1997 Intel Corporation | **por confirmar**: el paquete pip `intel-openmp` 2025.3.1 usa el *Intel End User License Agreement for Developer Tools* (Aug-2024), no la ISSL | hilos de oneMKL | `{app}\mkl\libiomp5md.dll` | `licenses/Intel-OpenMP-EULA-Developer-Tools.txt` |
| `libimalloc.dll` | 2026.1 | Copyright (C) 2015-2026 Intel Corporation | **por confirmar** (probablemente ISSL como parte de oneMKL 2026.1; revisar la lista de redistribuibles de ese paquete) | asignador de memoria de oneMKL | `{app}\mkl\libimalloc.dll` | — |
| **OpenBLAS** | 0.3.33 (cadena de configuración de la DLL) | Copyright (c) 2011-2014, The OpenBLAS Project | BSD-3-Clause | BLAS/LAPACK de respaldo si no hay oneMKL | `Symbolic.Core/Native/libopenblas.dll` (no está en git) → `{app}` | `licenses/OpenBLAS-BSD-3-Clause.txt` |
| Reference LAPACK | por confirmar | The University of Tennessee y otros | BSD-3-Clause (modified BSD) | `liblapack.dll` (ver nota: depende de `libblas.dll`, `libxerbla.dll`, `libgfortran-5.dll`, que **no** están en la publicación) | `Symbolic.Core/Native/liblapack.dll` | `licenses/LAPACK-BSD-3-Clause.txt` |
| **Triangle** (fork wo80 con API por contexto) | 1.6 | Copyright 1993–2005 Jonathan Richard Shewchuk | **Licencia propia de Triangle, NO es de código abierto estándar**: redistribución libre sólo "sin compensación"; uso comercial "ONLY BY DIRECT ARRANGEMENT WITH THE AUTHOR" | mallado Delaunay con restricciones y calidad (`TriangleInterop.cs`) | `Symbolic.Core/Native/triangle.dll` → `{app}` | `licenses/Triangle-Shewchuk.txt` |
| Acute (dentro del fork wo80 de Triangle) | 1.0 (2009) | H. Erten, A. Üngör | **por confirmar**: `acute.c` no trae texto de licencia; revisar si `triangle.dll` se compiló con o sin `NO_ACUTE` | mejora de ángulos | dentro de `triangle.dll` (por confirmar) | — |
| **Eigen** (cabeceras, compiladas en `eigen_solver.dll`) | 3.4 (según `eigen_solver.cpp`) | Eigen authors | MPL-2.0 (partes de Eigen son LGPL-2.1; **por confirmar** si `SparseLU`/COLAMD usado arrastra código LGPL — compilar con `-DEIGEN_MPL2_ONLY` lo comprueba) | solver disperso LDLᵀ / LU | `Symbolic.Core/Native/eigen_solver.dll` | `licenses/Eigen-MPL-2.0.txt` |
| `matlab_helpers.dll` | — | Jorge Burbano | MIT (código propio, `matlab_helpers.cpp`) | linspace/unique/sort… nativos | `Symbolic.Core/Native/` | `LICENSE` |

## 3. Paquetes NuGet (.NET)

| Componente | Versión | Autor / copyright | Licencia (del `.nuspec`) | Uso | Texto |
|---|---|---|---|---|---|
| AvalonEdit | 6.3.0.90 | 2000-2023 AlphaSierraPapa for the SharpDevelop Team / AvalonEdit Contributors | MIT | editor de código | `licenses/AvalonEdit-MIT.txt` |
| Microsoft.Web.WebView2 | 1.0.3595.46 | © Microsoft Corporation | licencia propia tipo BSD (archivo `LICENSE.txt` del paquete) + `NOTICE.txt` | visor HTML del reporte | `licenses/WebView2-LICENSE.txt`, `licenses/WebView2-NOTICE.txt` |
| HtmlAgilityPack | 1.12.4 | Copyright © ZZZ Projects Inc. | MIT | procesar HTML | `licenses/HtmlAgilityPack-MIT.txt` |
| AngouriMath | 1.4.0 | © Angouri 2019-2026 (WhiteBlackGoose and contributors) | MIT | álgebra simbólica auxiliar | `licenses/AngouriMath-MIT.txt` |
| ↳ Antlr4.Runtime.Standard | 4.13.1 | Copyright (c) 2012-2020 The ANTLR Project | BSD-3-Clause | dependencia de AngouriMath | `licenses/ANTLR4-BSD-3-Clause.txt` |
| ↳ GenericTensor | 1.0.4 | WhiteBlackGoose | MIT (archivo `LICENSE` del paquete) | dependencia de AngouriMath | `licenses/GenericTensor-MIT.txt` |
| ↳ HonkSharp | 1.0.3 | WhiteBlackGoose 2021 | MIT | dependencia de AngouriMath | `licenses/HonkSharp-MIT.txt` |
| ↳ PeterO.Numbers | 1.8.0 | Peter Occil | CC0-1.0 | dependencia de AngouriMath (`Numbers.dll`) | `licenses/PeterO.Numbers-CC0-1.0.txt` |
| Markdig.Signed | 0.43.0 | Alexandre Mutel | BSD-2-Clause | Markdown → HTML | `licenses/Markdig-BSD-2-Clause.txt` |
| SkiaSharp (+ NativeAssets) | 3.119.1 | © Microsoft Corporation; Xamarin | MIT (+ avisos de Skia y otros en THIRD-PARTY-NOTICES) | gráficos raster (`libSkiaSharp.dll`) | `licenses/SkiaSharp-MIT.txt`, `licenses/SkiaSharp-THIRD-PARTY-NOTICES.txt` |
| DocumentFormat.OpenXml | 3.3.0 | © Microsoft Corporation / .NET Foundation | MIT | exportar Word (Calcpad.OpenXml) | `licenses/DocumentFormat.OpenXml-MIT.txt` |
| System.IO.Packaging | 10.0.0 | .NET Foundation | MIT (parte de .NET) | paquetes OPC | `licenses/dotnet-runtime-MIT.txt` |
| Runtime .NET 10 (publicación self-contained, incl. WPF) | 10.0.x (**por confirmar** la exacta; en caché 10.0.11) | .NET Foundation and Contributors | MIT (+ avisos de terceros del runtime) | runtime de la app | `licenses/dotnet-runtime-MIT.txt`, `licenses/dotnet-runtime-THIRD-PARTY-NOTICES.txt` |

## 4. JavaScript incluido en `doc\`

| Componente | Versión | Autor / copyright | Licencia | Uso | Dónde | Texto |
|---|---|---|---|---|---|---|
| plotly.js | 2.35.2 | Copyright 2012-2024 Plotly, Inc. | MIT (cabecera del archivo) | gráficas 2D/3D | `Symbolic.Wpf/doc/plotly-2.35.2.min.js` | `licenses/plotly.js-MIT.txt` |
| three.js | 0.145.0 (y r170 dentro de `calcpad-viz.umd.js`; otra copia en `Symbolic.Core/Resources/three.min.js`) | Copyright 2010-2022 Three.js Authors | MIT (SPDX en la cabecera) | visor 3D | `Symbolic.Wpf/doc/three-0.145.0.min.js` | `licenses/three.js-MIT.txt` |
| OrbitControls | 0.145.0 (ejemplos de three.js) | Three.js Authors | MIT (**por confirmar**: el archivo no trae cabecera; es parte de three.js) | órbita con ratón | `Symbolic.Wpf/doc/OrbitControls-0.145.0.js` | `licenses/three.js-MIT.txt` |
| jQuery | 3.6.3 | (c) OpenJS Foundation and other contributors | MIT | plantilla heredada de Calcpad | `Symbolic.Wpf/doc/jquery-3.6.3.min.js` | `licenses/jQuery-MIT.txt` |
| `calcpad-viz.umd.js` (bundle Vite de `calcpad-viz/`) | package-lock: plotly.js-dist-min 3.5.0, three 0.170.0, tweakpane 4.0.5 | varios | MIT (three, plotly, Tweakpane, buffer, safe-buffer…) + **BSD-3-Clause** (MapLibre GL JS 4.7.1, ieee754) — según los comentarios `@license` del bundle | visualización interactiva | `Symbolic.Wpf/doc/calcpad-viz.umd.js` | `licenses/plotly.js-MIT.txt`, `licenses/three.js-MIT.txt`, `licenses/Tweakpane-MIT.txt`, `licenses/MapLibre-GL-JS-BSD-3-Clause.txt` |
| `hekatan-3d.js`, `hekatan-plot.js`, `hekatan-math.js`, `hekatan-slider.js` | — | Jorge Burbano | MIT (propio) | gráficas/matemática del reporte | `Symbolic.Wpf/doc/` | `LICENSE` |
| Univer (`@univerjs/presets` 0.9.0) | 0.9.0 | DreamNum / Univer | **por confirmar** (se cree Apache-2.0; **no se distribuye**: `excel-viewer/index.html` lo carga desde unpkg.com en tiempo de ejecución) | visor de Excel | CDN | — |

## 5. Fuentes tipográficas y recursos

| Componente | Versión | Autor / copyright | Licencia | Dónde | Texto |
|---|---|---|---|---|---|
| Roboto | 2.137 | Copyright 2011 Google Inc. | Apache-2.0 (tabla `name` de la fuente) | `Fonts\` (Cli / PyCalcpad) | `licenses/Roboto-Apache-2.0.txt` |
| Jost | 3.7 | Copyright 2020 The Jost Project Authors | SIL OFL-1.1 | `Fonts\` | `licenses/Jost-OFL-1.1.txt` |
| **Georgia Pro** | 6.14 | © 2018 Microsoft Corporation; diseño Monotype | **Propietaria de Microsoft** — la tabla `name` dice "Microsoft supplied font… as permitted by the license terms… of the Microsoft product… in which this font was included". **No hay permiso de redistribución** a la vista | `Symbolic.Wpf/Fonts/`, `Symbolic.Cli/Fonts/` → `{app}\Fonts\` | — |
| Iconos | — | icons8.com | **por confirmar** (heredados de Calcpad; icons8 exige atribución o licencia de pago) | `Symbolic.Wpf/resources`, `doc/Images` | — |

---

## Pendiente legal

Esto **no es asesoría legal**; son riesgos detectados y opciones. La decisión es del autor.

1. **Giac (GPL-3.0) cargado en el mismo proceso.** `giac.dll` se carga por P/Invoke dentro de
   `HekatanLab.exe`. Para la FSF eso suele contar como una sola obra combinada, y la GPL-3.0 exige
   que **toda** la combinación distribuida se ofrezca bajo GPL-3.0 con su código fuente
   (incluido el de `giac.dll`, más GMP/MPFR). Hoy el instalador no lleva el texto GPL ni oferta de
   fuente. El código MIT propio es compatible con GPL, pero **no** lo son las piezas no libres que
   viajan en el mismo proceso: oneMKL/OpenMP de Intel (su EULA prohíbe ingeniería inversa y que
   el material quede sujeto a licencia recíproca) y Triangle (no comercial). Opciones: (a) mover Giac a un **proceso aparte** (ej. `giac.exe` o servidor por
   stdin/stdout) y enviar el texto GPL + fuente con él; (b) pedir a B. Parisse la **licencia
   comercial dual** que ofrece; (c) publicar la combinación bajo **GPL-3.0** (y resolver entonces
   la compatibilidad con MKL); (d) quitar Giac y usar sólo el motor propio/AngouriMath.
2. **Triangle (Shewchuk).** Redistribución permitida sólo sin compensación y uso comercial sólo con
   acuerdo directo con el autor; además exige conservar la cabecera y avisar de modificaciones
   (el fork wo80 lo es). Si Hekatan Lab se vende o forma parte de un producto de pago
   (modelo freemium), hace falta **permiso de Shewchuk** o reemplazarlo (p. ej. un mallador
   Delaunay propio, CDT/poly2tri-BSD u otro con licencia permisiva).
3. **Intel oneMKL / OpenMP.** La ISSL permite redistribuir **sin modificar** si se reproduce el
   aviso de copyright y los términos (ya en `licenses/`). El runtime OpenMP viene con el EULA de
   Developer Tools: sólo redistribuibles listados en `redist.txt`, sólo dentro del producto y
   **bajo un acuerdo que prohíba la ingeniería inversa** — la MIT de Hekatan Lab no lo prohíbe.
   Pendiente: confirmar el texto exacto de la 2026.1, revisar `redist.txt`, y decidir si se añade
   un EULA de usuario final para esos binarios. Se envían dos versiones (2024.2 y 2026.1): quitar
   la que no se use reduce superficie.
4. **Georgia Pro** es una fuente de Microsoft sin permiso de redistribución visible: quitarla del
   instalador o sustituirla (p. ej. una serif OFL) salvo que se confirme un permiso.
5. **LAPACK/Eigen/Acute/Iconos:** completar los "por confirmar" de arriba. `liblapack.dll` además
   depende de DLL que no se publican (probablemente no carga; revisar si sigue haciendo falta).
