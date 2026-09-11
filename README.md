# Hekatan Lab

[![Versión](https://img.shields.io/badge/versi%C3%B3n-1.1.0-blue)](https://github.com/GiorgioBurbanelli89/hekatan-lab/releases)
[![Descargar](https://img.shields.io/badge/%E2%AC%87%20Descargar-Instalador%20Windows-success)](https://github.com/GiorgioBurbanelli89/hekatan-lab/releases/latest)
[![Licencia](https://img.shields.io/badge/licencia-MIT-green)](#licencia)
[![MATLAB 2017a](https://img.shields.io/badge/compatible-MATLAB%202017a-orange)](#compatibilidad-con-matlab-2017a)

**Hekatan Lab** es una calculadora científica programable con **sintaxis MATLAB**: escribís
archivos `.m` de MATLAB puro y obtenés un **reporte HTML tipo libro** (memoria de cálculo),
exportable a PDF y Word. El motor —parser, evaluador, JIT, álgebra numérica (MKL) y cálculo
simbólico— está escrito en C# y corre **in-process**: **no necesitás tener MATLAB instalado**.

> El **mismo archivo `.m` corre idéntico en Hekatan Lab y en MATLAB 2017a**. Todo el formato
> del reporte vive en comentarios `%`, que MATLAB ignora — así nunca se rompe la compatibilidad.

> ⚙️ **Instalación en un paso:** el instalador es *self-contained* (**trae el runtime .NET 10
> embebido**, no hace falta instalar .NET aparte). Windows 10/11 de 64 bits.

---

## Novedades — v1.1.0

- **Cálculo simbólico exacto** como MATLAB: `diff`, `int`, `limit` (punto numérico **y simbólico**),
  `taylor`, `symsum` (incluida la **suma de Riemann** → integral), `solve`, `dsolve`, transformadas
  de Laplace/Fourier. Racionales exactos (`symsum(1/i^2,1,4)=205/144`, no decimal).
- **Render matemático tipo libro**: fracciones, raíces, `∑`/`∏`/`∫`, `lim` con la condición debajo,
  `ℒ{}`/`ℱ{}` para transformadas, `exp(x)→eˣ`, sistema `K\F → K⁻¹·F`, derivada parcial `∂`.
- **Formato de documento en `%`** (ver *[El lenguaje](#el-lenguaje)*): ecuaciones numeradas,
  columnas, centrado, saltos de página, márgenes, **bibliografía `[N]`**, pies de figura/tabla
  auto-numerados, notas al pie y referencias cruzadas — todo dentro de comentarios `%`.
- **Motor rápido**: JIT propio + oneMKL embebido (matmul a la par de MATLAB; cálculo simbólico
  in-process **mucho más rápido** que el MuPAD de MATLAB 2017a).

---

## Características principales

- Sintaxis **MATLAB** completa: escalares, vectores, matrices, funciones `f(x,y,…)`, control de
  flujo (`if`/`for`/`while`), funciones anónimas `@(x)`, `classdef`.
- **Números reales y complejos**, con formato fijo y redondeo inteligente.
- **Álgebra lineal numérica** sobre oneMKL: `*`, `inv`, `det`, factorizaciones (Cholesky, LU, QR,
  SVD), `eig`, sistemas lineales `A\b`, matrices dispersas.
- **Cálculo simbólico** (motor propio + puente Giac): derivadas, integrales, límites, series,
  sumatorias, despejes, EDOs, transformadas — exacto.
- **Gráficas** (`plot`, `surf`, `contourf`, `patch`, …) con hover interactivo, embebidas en el
  reporte sin dependencias externas.
- **Reporte HTML profesional** con render matemático, exportable a **PDF** y **Word (.docx)**.
- **CLI** (`CalcpadLabCli.exe`) para generar reportes headless.

---

## Instalación

Descargá el instalador desde **[Releases](https://github.com/GiorgioBurbanelli89/hekatan-lab/releases)**
y ejecutalo. Es *self-contained* (incluye el runtime .NET 10, no hace falta instalar .NET aparte).
Instala la app de escritorio (WPF) **+ el CLI** y asocia los archivos `.m`.

---

## Cómo funciona

1. **Escribí** código MATLAB y comentarios en el panel **Code** (izquierda).
2. Presioná **F5** (o *AutoRun*) para calcular. El resultado aparece en **Output** (derecha)
   como un reporte HTML tipo libro.
3. **Exportá** a HTML, PDF o Word, o imprimí.

El mismo `.m` se puede abrir en MATLAB 2017a y correr sin cambios.

---

## El lenguaje

Hekatan Lab lee **MATLAB puro**. Todo lo que va fuera de `%` se **ejecuta y se renderiza**; todo lo
que va en `%` es comentario (MATLAB lo ignora) y en Hekatan Lab da **formato al reporte**.

### 1) Código real (fuera de `%`) — se ejecuta y se renderiza

| Escribís | Se ve |
|---|---|
| `x = 2 + 3` | x = 5 |
| `syms x; F = k*u` | F = k·u |
| `sqrt(b^2-4*a*c)` | √(b²−4·a·c) |
| `exp(-3*t)` | e⁻³ᵗ |
| `symsum(1/i^2,i,1,4)` | ∑ … = 205/144 |
| `int(f,x)` · `diff(f,x)` · `limit(...)` | ∫ f dx · d/dx · lim |
| `laplace(f)` · `ilaplace(F)` | ℒ{f} · ℒ⁻¹{F} |
| `solve(a*x^2+b*x+c, x)` | a·x²+b·x+c = 0 ⟹ x |
| `K\F` · `inv(K)*F` | K⁻¹·F |

**Tokens en nombres de variable** (válidos en MATLAB): `sigma_max`→σ_max · `xsup2`→x² ·
`a__b`→fracción a/b · `fprime_c`→f′c · `sqrt`/`bar`/`hat`/`vec`/`dot`/`tilde` → √/x̄/x̂/F⃗/ẋ/x̃ ·
griegas por nombre (`nu`→ν).

### 2) `%` inline — anota la línea de código

| Escribís | Efecto |
|---|---|
| `F = k*u    %@@(2.1)` | número **(2.1)** a la derecha de la ecuación real |
| `x = 2+3    %' resultado` | anotación de texto visible |
| `x = 2+3    %texto` | comentario oculto (código) |

### 3) `%` en línea propia — texto con formato

`%"Título` · `%'texto` · `%'-----` línea · `%'<` / `%'|` / `%'>` alinear ·
`%'*` / `%'/` / `%'_` negrita/itálica/subrayado · `%'{rojo,centro,negrita} texto` bloque de
atributos · `%'\ …` escape · `%'<table>…</table>` HTML crudo.

### 4) `%` directivas de una línea — elementos de libro

| Directiva | Efecto |
|---|---|
| `%#deq sigma = P/A_g @@(2.2)` | ecuación numerada (math + número a la derecha) |
| `%#col a ; b ; c`  (o `%#inl`) | columnas iguales (`'`=texto, sin `'`=ecuación) |
| `%#cen texto/ecuación` | centrado |
| `%#pgb` | salto de página |
| `%#cita McCormac (2011)…` | bibliografía **[N]** auto-numerada |
| `%#fig Descripción` · `%#tab Descripción` | **Figura N.** / **Tabla N.** auto-numerado |
| `%#nota Aclaración` | nota al pie |
| `%#ref Ec. (2.1), pág. 34` | referencia cruzada "(ver …)" |
| `%#img <ruta o data-uri>` | imagen incrustada |

### 5) `%` bloques — abrir … cerrar

| Abre | Cierra | Efecto |
|---|---|---|
| `%#deq` | `%#endeq` | bloque de ecuaciones: la **variable real** adentro se numera/renderiza |
| `%#margen 25` | `%#endmargen` | bloque justificado con márgenes de N mm |
| `%#md` | `%#endmd` | bloque Markdown |
| `%#hide` | `%#show` | ejecuta pero no muestra el render |
| `%#plain` | `%#render` | texto de `disp`/`fprintf` plano ↔ renderizado |
| `%#nogreek` | `%#greek` | nombres literales ↔ símbolo griego |
| `%% Sección` | — | sección MATLAB oculta |

Ejemplos completos en la carpeta [`_ejemplos_render/`](_ejemplos_render) y el
[manual de formato](_ejemplos_render/MANUAL_FORMATO_HEKATAN_LAB.md).

---

## Compatibilidad con MATLAB 2017a

Todo `.m` de Hekatan Lab está pensado para **correr idéntico en MATLAB R2017a**:

- El código real es MATLAB estándar.
- Todo el formato del reporte vive en comentarios `%` → MATLAB los ignora.
- Los resultados numéricos y simbólicos se verifican contra MATLAB 2017a (mismos valores).

---

## Créditos

Hekatan Lab hereda la **base de render e interfaz** (plantilla HTML del reporte, panel WPF +
WebView2, estilos de math) del proyecto **[Calcpad](https://codeberg.org/proektsoft/Calcpad)** de
**Nedelcho Ganchovski / PROEKTSOFT EOOD** (licencia MIT) — el crédito de esa base es suyo.

El resto es desarrollo propio de **Hekatan Engineers**: el **intérprete de MATLAB** (tokenizer,
parser, evaluador), el **JIT**, el **álgebra numérica sobre oneMKL**, el **motor de cálculo
simbólico** y las **directivas de formato tipo libro** descritas arriba.

**Aviso honesto sobre el simbólico:** `int`, `simplify`, `factor`, `expand`, `limit`, `solve` y
`symsum` se envían **primero a [Giac](https://www-fourier.univ-grenoble-alpes.fr/~parisse/giac.html)**
(Bernard Parisse y Renée De Graeve, licencia **GPL-3.0**), cargado como `giac.dll`; si Giac no
responde, se usa el motor propio. También se incluyen Intel oneMKL, OpenBLAS, Triangle (Shewchuk),
Eigen, AvalonEdit, WebView2, plotly.js, three.js y otros — lista completa, versiones y licencias en
[`THIRD_PARTY_NOTICES.md`](THIRD_PARTY_NOTICES.md) y la carpeta `licenses/`.

---

## Licencia

Distribuido bajo licencia **MIT**. Ver el archivo `LICENSE`. El crédito de la base de render/UI
corresponde a PROEKTSOFT EOOD® (Calcpad, MIT). Los componentes de terceros conservan sus propias
licencias (ver `THIRD_PARTY_NOTICES.md`).
