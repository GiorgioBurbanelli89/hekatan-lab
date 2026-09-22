// =============================================================================
// Calcpad Lab — BLAS/LAPACK via interfaz C (CBLAS + LAPACKE): Intel MKL ↔ OpenBLAS
// =============================================================================
//   El motor numerico carga UNA libreria nativa, elegida por PREFERENCIA:
//       1) Intel oneMKL  (mkl_rt.dll / mkl_rt.2.dll)  ← mas rapido, LEGAL
//       2) OpenBLAS      (libopenblas.dll)            ← fallback (va en el bin)
//   Si no hay ninguna, cae al loop managed (correcto, mas lento).
//
//   POR QUE LA INTERFAZ C (cblas_*/LAPACKE_*) Y NO LA FORTRAN:
//   - Enteros de 32 bits (lapack_int) → inmune al crash ILP64 de la mkl.dll de
//     MATLAB (que lee n/lda/ipiv como qword de 64 bits; ver memoria
//     reference_mkl_vs_openblas_lab). El redistribuible oneMKL `mkl_rt` y
//     OpenBLAS exportan LAPACKE en LP64 (int 32-bit).
//   - Mismos nombres EXACTOS en ambas libs (cblas_dgemm, LAPACKE_dgesv…),
//     sin la ambiguedad de decoracion Fortran (DGEMM vs dgemm_).
//   - Soportan row-major nativo (CblasRowMajor=101) → sin el truco de transpuesta.
//
//   oneMKL real ya presente en la maquina del usuario: el redistribuible que
//   trae FreeCAD (C:\Program Files\FreeCAD 1.0\bin\mkl_rt.2.dll). NO se usa la
//   mkl.dll de MATLAB (licencia + ILP64). Mismo enfoque que Hekatan.Core/MklInterop.
// =============================================================================
using System;
using System.IO;
using System.Numerics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace Calcpad.Core
{
    /// <summary>Loader nativo compartido (CBLAS + LAPACKE). Prefiere Intel oneMKL,
    /// cae a OpenBLAS, y si no hay nada deja todo en managed.</summary>
    internal static unsafe class NativeBlas
    {
        // Constantes de la interfaz C
        public const int CblasRowMajor = 101, CblasColMajor = 102;
        public const int CblasNoTrans = 111, CblasTrans = 112;
        public const int LapackRowMajor = 101, LapackColMajor = 102;

        /// <summary>"Intel MKL", "OpenBLAS" o "managed".</summary>
        public static string Provider { get; private set; } = "managed";
        /// <summary>Ruta de la libreria cargada (diagnostico).</summary>
        public static string LoadedFrom { get; private set; } = "";
        public static IntPtr Handle { get; private set; } = IntPtr.Zero;

        // ---- Punteros a las rutinas C (cdecl) ----
        // cblas_dgemm(layout, transA, transB, M, N, K, alpha, A, lda, B, ldb, beta, C, ldc)
        public static delegate* unmanaged[Cdecl]<int, int, int, int, int, int, double, double*, int, double*, int, double, double*, int, void> Dgemm;
        // cblas_dgemv(layout, transA, M, N, alpha, A, lda, X, incX, beta, Y, incY)
        public static delegate* unmanaged[Cdecl]<int, int, int, int, double, double*, int, double*, int, double, double*, int, void> Dgemv;
        // cblas_daxpy(N, alpha, X, incX, Y, incY)
        public static delegate* unmanaged[Cdecl]<int, double, double*, int, double*, int, void> Daxpy;
        // cblas_ddot(N, X, incX, Y, incY) -> double
        public static delegate* unmanaged[Cdecl]<int, double*, int, double*, int, double> Ddot;
        // LAPACKE_dgesv(layout, n, nrhs, A, lda, ipiv, B, ldb) -> info
        public static delegate* unmanaged[Cdecl]<int, int, int, double*, int, int*, double*, int, int> Dgesv;
        // LAPACKE_dpbsv(layout, uplo, n, kd, nrhs, AB, ldab, B, ldb) -> info
        public static delegate* unmanaged[Cdecl]<int, sbyte, int, int, int, double*, int, double*, int, int> Dpbsv;
        // LAPACKE_dpotrf(layout, uplo, n, A, lda) -> info  (Cholesky de simétrica SPD)
        public static delegate* unmanaged[Cdecl]<int, sbyte, int, double*, int, int> Dpotrf;
        // LAPACKE_dpotri(layout, uplo, n, A, lda) -> info  (inversa desde el factor Cholesky)
        public static delegate* unmanaged[Cdecl]<int, sbyte, int, double*, int, int> Dpotri;
        // MKL DFTI (FFT). handle = DFTI_DESCRIPTOR_HANDLE (void*). MKL_LONG = Int64 en oneMKL Win64.
        // DftiCreateDescriptor(&handle, precision, domain, dim, length) -> status
        public static delegate* unmanaged[Cdecl]<void**, int, int, long, long, long> DftiCreateDescriptor;
        public static delegate* unmanaged[Cdecl]<void*, long> DftiCommitDescriptor;
        public static delegate* unmanaged[Cdecl]<void*, void*, long> DftiComputeForward;
        public static delegate* unmanaged[Cdecl]<void*, void*, long> DftiComputeBackward;
        public static delegate* unmanaged[Cdecl]<void**, long> DftiFreeDescriptor;
        // LAPACKE_dsygv(layout, itype, jobz, uplo, n, A, lda, B, ldb, w) -> info  (LAPACKE gestiona el workspace)
        public static delegate* unmanaged[Cdecl]<int, int, sbyte, sbyte, int, double*, int, double*, int, double*, int> Dsygv;
        // LAPACKE_dgeev(layout, jobvl, jobvr, n, a, lda, wr, wi, vl, ldvl, vr, ldvr) -> info  (eig NO simétrica)
        public static delegate* unmanaged[Cdecl]<int, sbyte, sbyte, int, double*, int, double*, double*, double*, int, double*, int, int> Dgeev;
        // MKL_Set_Num_Threads(nt) — fuerza el paralelismo de MKL (matmul/solve/eig). Sin esto se
        // quedaba single-thread (matmul 300 ≈ 1.9 ms vs MATLAB multi 0.44 ms). Solo en oneMKL real.
        public static delegate* unmanaged[Cdecl]<int, void> MklSetNumThreads;
        // VML (Vector Math Library de MKL) — element-wise SIMD + threaded: sqrt/exp/log/sin/cos y
        // potencia. n, a, [b escalar], y. Es lo que hace rápido el element-wise de MATLAB.
        public static delegate* unmanaged[Cdecl]<int, double*, double*, void> VdSqrt;
        public static delegate* unmanaged[Cdecl]<int, double*, double, double*, void> VdPowx;
        public static delegate* unmanaged[Cdecl]<int, double*, double*, void> VdExp;
        public static delegate* unmanaged[Cdecl]<int, double*, double*, void> VdLn;
        public static delegate* unmanaged[Cdecl]<int, double*, double*, void> VdSin;
        public static delegate* unmanaged[Cdecl]<int, double*, double*, void> VdCos;
        // MKL PARDISO (sparse directo, el solver de OpenSees). Solo existe en mkl_rt.
        // pardiso(pt, maxfct, mnum, mtype, phase, n, a, ia, ja, perm, nrhs, iparm, msglvl, b, x, error)
        public static delegate* unmanaged[Cdecl]<void*, int*, int*, int*, int*, int*, double*, int*, int*, int*, int*, int*, int*, double*, double*, int*, void> Pardiso;
        // pardisoinit(pt, mtype, iparm)
        public static delegate* unmanaged[Cdecl]<void*, int*, int*, void> Pardisoinit;

        public static bool HasBlas => Dgemm != null && Dgemv != null && Daxpy != null && Ddot != null;
        public static bool HasLapack => Dgesv != null && Dpbsv != null && Dsygv != null;
        public static bool HasPardiso => Pardiso != null && Pardisoinit != null;

        static NativeBlas()
        {
            var asm = typeof(NativeBlas).Assembly;

            // MKL SIEMPRE preferido (Intel oneMKL). El override HEKATAN_BLAS que
            // permitía saltarlo/desactivarlo se eliminó: solo se usa OpenBLAS/managed
            // si oneMKL real no está presente en la máquina. HEKATAN_MKL_DIR puede
            // apuntar a una instalación concreta de oneMKL.

            // 1) Intel oneMKL real (mkl_rt*.dll) en directorios candidatos.
            //    mkl_rt carga sus satelites (mkl_avx2/mkl_core/libiomp5md…) desde
            //    su propia carpeta → prependemos esa carpeta al PATH.
            string[] mklDirs =
            {
                Environment.GetEnvironmentVariable("HEKATAN_MKL_DIR"),   // override explicito
                Path.Combine(AppContext.BaseDirectory, "mkl"),           // bundle oneMKL (bin\mkl\) ← preferido
                AppContext.BaseDirectory,                                 // junto al ejecutable
                @"C:\Program Files\FreeCAD 1.0\bin",                     // fallback: oneMKL de FreeCAD
            };
            foreach (var dir in mklDirs)
            {
                if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) continue;
                string[] hits;
                try { hits = Directory.GetFiles(dir, "mkl_rt*.dll"); }
                catch { continue; }
                if (hits.Length == 0) continue;
                try
                {
                    // Anexar (no anteponer) el dir de oneMKL al PATH: sus satelites
                    // (mkl_avx2.2/mkl_core.2/libiomp5md…) tienen nombres unicos → se
                    // hallan igual, pero las DLLs propias de la app ganan en colision.
                    Environment.SetEnvironmentVariable("PATH",
                        Environment.GetEnvironmentVariable("PATH") + Path.PathSeparator + dir);
                    if (NativeLibrary.TryLoad(hits[0], out var h) && Bind(h))
                    { Handle = h; Provider = "Intel MKL"; LoadedFrom = hits[0]; return; }
                }
                catch { /* probar siguiente */ }
            }

            // 2) OpenBLAS (en el bin de la app)
            const DllImportSearchPath search =
                DllImportSearchPath.AssemblyDirectory | DllImportSearchPath.SafeDirectories |
                DllImportSearchPath.ApplicationDirectory;
            foreach (var name in new[] { "libopenblas", "openblas" })
            {
                try
                {
                    if (NativeLibrary.TryLoad(name, asm, search, out var h) && Bind(h))
                    { Handle = h; Provider = "OpenBLAS"; LoadedFrom = name; return; }
                }
                catch { /* probar siguiente */ }
            }
            // 3) nada → managed
        }

        // Nº de hilos para MKL. Override: env var HEKATAN_MKL_THREADS. Default: núcleos de
        // RENDIMIENTO en CPU híbrido. i7-14650HX = 8P(+HT=16 lóg) + 8E; usar los 24 lastra el
        // GEMM con los E-cores. Heurística: si lógicos > físicos (hay HT) el equipo suele ser
        // híbrido/HT → usar ~2/3 de los lógicos (P-cores + su HT, sin E-cores). Cae a físicos.
        internal static int MklThreads { get; private set; } = 1;
        private static int ResolveMklThreads()
        {
            int logical = Math.Max(1, Environment.ProcessorCount);
            var env = Environment.GetEnvironmentVariable("HEKATAN_MKL_THREADS");
            if (int.TryParse(env, out int t) && t >= 1 && t <= logical) { MklThreads = t; return t; }
            // Heurística híbrido: 24 lóg → 16 (P-cores con HT). En no-híbrido queda ≈ lógicos.
            int guess = logical >= 20 ? (logical * 2 + 1) / 3 : logical;
            MklThreads = Math.Max(1, guess);
            return MklThreads;
        }

        private static bool Bind(IntPtr h)
        {
            if (!NativeLibrary.TryGetExport(h, "cblas_dgemm", out var dgemm)) return false;
            Dgemm = (delegate* unmanaged[Cdecl]<int, int, int, int, int, int, double, double*, int, double*, int, double, double*, int, void>)dgemm;
            if (NativeLibrary.TryGetExport(h, "cblas_dgemv", out var p)) Dgemv = (delegate* unmanaged[Cdecl]<int, int, int, int, double, double*, int, double*, int, double, double*, int, void>)p;
            if (NativeLibrary.TryGetExport(h, "cblas_daxpy", out p)) Daxpy = (delegate* unmanaged[Cdecl]<int, double, double*, int, double*, int, void>)p;
            if (NativeLibrary.TryGetExport(h, "cblas_ddot", out p)) Ddot = (delegate* unmanaged[Cdecl]<int, double*, int, double*, int, double>)p;
            if (NativeLibrary.TryGetExport(h, "LAPACKE_dgesv", out p)) Dgesv = (delegate* unmanaged[Cdecl]<int, int, int, double*, int, int*, double*, int, int>)p;
            if (NativeLibrary.TryGetExport(h, "LAPACKE_dpbsv", out p)) Dpbsv = (delegate* unmanaged[Cdecl]<int, sbyte, int, int, int, double*, int, double*, int, int>)p;
            if (NativeLibrary.TryGetExport(h, "LAPACKE_dpotrf", out p)) Dpotrf = (delegate* unmanaged[Cdecl]<int, sbyte, int, double*, int, int>)p;
            if (NativeLibrary.TryGetExport(h, "LAPACKE_dpotri", out p)) Dpotri = (delegate* unmanaged[Cdecl]<int, sbyte, int, double*, int, int>)p;
            if (NativeLibrary.TryGetExport(h, "DftiCreateDescriptor", out p)) DftiCreateDescriptor = (delegate* unmanaged[Cdecl]<void**, int, int, long, long, long>)p;
            if (NativeLibrary.TryGetExport(h, "DftiCommitDescriptor", out p)) DftiCommitDescriptor = (delegate* unmanaged[Cdecl]<void*, long>)p;
            if (NativeLibrary.TryGetExport(h, "DftiComputeForward", out p)) DftiComputeForward = (delegate* unmanaged[Cdecl]<void*, void*, long>)p;
            if (NativeLibrary.TryGetExport(h, "DftiComputeBackward", out p)) DftiComputeBackward = (delegate* unmanaged[Cdecl]<void*, void*, long>)p;
            if (NativeLibrary.TryGetExport(h, "DftiFreeDescriptor", out p)) DftiFreeDescriptor = (delegate* unmanaged[Cdecl]<void**, long>)p;
            if (NativeLibrary.TryGetExport(h, "LAPACKE_dsygv", out p)) Dsygv = (delegate* unmanaged[Cdecl]<int, int, sbyte, sbyte, int, double*, int, double*, int, double*, int>)p;
            if (NativeLibrary.TryGetExport(h, "LAPACKE_dgeev", out p)) Dgeev = (delegate* unmanaged[Cdecl]<int, sbyte, sbyte, int, double*, int, double*, double*, double*, int, double*, int, int>)p;
            // Forzar multi-threading de MKL (por defecto se quedaba en 1 thread en este proceso).
            // CPU HÍBRIDO (Raptor Lake: 8 P + 8 E cores): usar TODOS los lógicos mete a los
            // E-cores lentos como rezagados en BLAS compute-bound. Por defecto usamos solo los
            // núcleos "de rendimiento" ~ mitad de los lógicos (heurística P-core), con override
            // por env var HEKATAN_MKL_THREADS para tuning.
            if (NativeLibrary.TryGetExport(h, "MKL_Set_Num_Threads", out p))
            {
                MklSetNumThreads = (delegate* unmanaged[Cdecl]<int, void>)p;
                try { MklSetNumThreads(ResolveMklThreads()); } catch { }
            }
            // VML (element-wise SIMD+threaded)
            if (NativeLibrary.TryGetExport(h, "vdSqrt", out p)) VdSqrt = (delegate* unmanaged[Cdecl]<int, double*, double*, void>)p;
            if (NativeLibrary.TryGetExport(h, "vdPowx", out p)) VdPowx = (delegate* unmanaged[Cdecl]<int, double*, double, double*, void>)p;
            if (NativeLibrary.TryGetExport(h, "vdExp", out p)) VdExp = (delegate* unmanaged[Cdecl]<int, double*, double*, void>)p;
            if (NativeLibrary.TryGetExport(h, "vdLn", out p)) VdLn = (delegate* unmanaged[Cdecl]<int, double*, double*, void>)p;
            if (NativeLibrary.TryGetExport(h, "vdSin", out p)) VdSin = (delegate* unmanaged[Cdecl]<int, double*, double*, void>)p;
            if (NativeLibrary.TryGetExport(h, "vdCos", out p)) VdCos = (delegate* unmanaged[Cdecl]<int, double*, double*, void>)p;
            // PARDISO (solo MKL; OpenBLAS no lo exporta → quedan null)
            if (NativeLibrary.TryGetExport(h, "pardiso", out p)) Pardiso = (delegate* unmanaged[Cdecl]<void*, int*, int*, int*, int*, int*, double*, int*, int*, int*, int*, int*, int*, double*, double*, int*, void>)p;
            if (NativeLibrary.TryGetExport(h, "pardisoinit", out p)) Pardisoinit = (delegate* unmanaged[Cdecl]<void*, int*, int*, void>)p;
            return true;
        }
    }

    public static unsafe class BlasInterop
    {
        /// <summary>Threshold por debajo del cual usamos loop naive (overhead BLAS > compute).</summary>
        public const int BlasThreshold = 32;

        /// <summary>Proveedor BLAS activo: "Intel MKL", "OpenBLAS" o "managed".</summary>
        public static string Provider => NativeBlas.Provider;
        /// <summary>Ruta de la libreria nativa cargada (diagnostico).</summary>
        public static string LoadedFrom => NativeBlas.LoadedFrom;

        /// <summary>True si hay una libreria BLAS nativa cargada y con simbolos resueltos.</summary>
        public static readonly bool Available;

        static BlasInterop()
        {
            try
            {
                if (!NativeBlas.HasBlas) { Available = false; return; }
                const int N = 64;
                var a = new double[N * N];
                var b = new double[N * N];
                var c = new double[N * N];
                for (int i = 0; i < N; i++) { a[i * N + i] = 2.0; b[i * N + i] = 3.0; }
                MatMulNative(N, N, N, a, b, c);
                Available = Math.Abs(c[0] - 6.0) < 1e-9 && Math.Abs(c[N * N - 1] - 6.0) < 1e-9;
            }
            catch { Available = false; }
        }

        /// <summary>True si PARDISO (sparse directo de MKL, el solver de OpenSees) está disponible.</summary>
        public static bool PardisoAvailable => NativeBlas.HasPardiso;

        /// <summary>Resuelve A·x=b con A sparse CSR 0-based (matriz completa, real no-simétrica)
        /// vía MKL PARDISO. rowPtr[n+1], colIdx[nnz], vals[nnz]. Requiere Intel MKL.</summary>
        /// <summary>
        /// Factorización PARDISO RETENIDA — el corazón de `decomposition(A)` en Lab.
        /// El backslash normal hace la fase 13 (análisis+factorización+solve) y libera en
        /// cada llamada. Aquí separamos: Factor() hace 11+22 y GUARDA el handle interno pt;
        /// Solve() hace solo la fase 33 reusando esa factorización. En un Newton modificado
        /// (misma K, muchos RHS) esto evita re-factorizar N veces → techo medido 4.3× en el
        /// solve del Demo04, y crece con el tamaño del modelo.
        /// PARDISO en la fase 33 aún necesita a/ia/ja, así que la matriz se retiene también.
        /// </summary>
        public sealed unsafe class PardisoFactorization : IDisposable
        {
            private readonly long[] _pt = new long[64];      // handle interno de PARDISO (estado retenido)
            private readonly int[] _iparm = new int[64];
            private readonly int[] _perm;
            private readonly int[] _rowPtr, _colIdx;
            private double[] _vals;
            private readonly int _n, _mtype;
            private bool _disposed;

            public int N => _n;
            public int[] RowPtr => _rowPtr;   // patrón (para comparar y reusar el análisis)
            public int[] ColIdx => _colIdx;

            private PardisoFactorization(int n, int[] rowPtr, int[] colIdx, double[] vals, int mtype)
            {
                _n = n; _rowPtr = rowPtr; _colIdx = colIdx; _vals = vals; _mtype = mtype;
                _perm = new int[n];
            }

            /// <summary>Fases 11+22: análisis simbólico + factorización numérica. Retiene todo.</summary>
            public static PardisoFactorization Factor(int n, int[] rowPtr, int[] colIdx, double[] vals, int mtype = 11)
            {
                if (!NativeBlas.HasPardiso)
                    throw new InvalidOperationException("PARDISO no disponible (requiere Intel MKL real, no OpenBLAS).");
                var f = new PardisoFactorization(n, rowPtr, colIdx, vals, mtype);
                int error = 0;
                fixed (long* ppt = f._pt)
                fixed (int* pip = f._iparm, pia = f._rowPtr, pja = f._colIdx, ppe = f._perm)
                fixed (double* pa = f._vals)
                {
                    int mt = mtype;
                    NativeBlas.Pardisoinit((void*)ppt, &mt, pip);
                    f._iparm[34] = 1;               // indexado 0-based (CSR de Lab)
                    int mf = 1, mn = 1, ml = 0, nr = 1, N = n;
                    int phase = 12;                 // 11 (análisis) + 22 (factorización), SIN solve
                    double dummy = 0;
                    NativeBlas.Pardiso((void*)ppt, &mf, &mn, &mt, &phase, &N, pa, pia, pja, ppe, &nr, pip, &ml, &dummy, &dummy, &error);
                }
                if (error != 0) { f.Dispose(); throw new InvalidOperationException($"PARDISO factor error {error}"); }
                return f;
            }

            /// <summary>Fase 22: RE-factorización numérica reusando el ANÁLISIS simbólico (fase 11)
            /// ya hecho — para el MISMO patrón con valores nuevos (Newton/pushover: misma K, cambian
            /// los valores). Evita repetir el reordenamiento fill-reducing, que es la parte cara.</summary>
            public void Refactor(double[] newVals)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(PardisoFactorization));
                if (newVals.Length != _vals.Length)
                    throw new ArgumentException($"Refactor: {newVals.Length} valores, se esperaba {_vals.Length} (mismo patrón)");
                System.Array.Copy(newVals, _vals, _vals.Length);   // mismo patrón, valores nuevos
                int error = 0;
                fixed (long* ppt = _pt)
                fixed (int* pip = _iparm, pia = _rowPtr, pja = _colIdx, ppe = _perm)
                fixed (double* pa = _vals)
                {
                    int mt = _mtype, mf = 1, mn = 1, ml = 0, nr = 1, N = _n;
                    int phase = 22;                 // SOLO factorización numérica (reusa el análisis en _pt)
                    double dummy = 0;
                    NativeBlas.Pardiso((void*)ppt, &mf, &mn, &mt, &phase, &N, pa, pia, pja, ppe, &nr, pip, &ml, &dummy, &dummy, &error);
                }
                if (error != 0) throw new InvalidOperationException($"PARDISO refactor error {error}");
            }

            /// <summary>Fase 33: sustitución hacia adelante/atrás reusando la factorización retenida.</summary>
            public double[] Solve(double[] b)
            {
                if (_disposed) throw new ObjectDisposedException(nameof(PardisoFactorization));
                if (b.Length != _n) throw new ArgumentException($"decomposition\\b: b tiene {b.Length}, se esperaba {_n}");
                var x = new double[_n];
                int error = 0;
                fixed (long* ppt = _pt)
                fixed (int* pip = _iparm, pia = _rowPtr, pja = _colIdx, ppe = _perm)
                fixed (double* pa = _vals, pb = b, px = x)
                {
                    int mt = _mtype, mf = 1, mn = 1, ml = 0, nr = 1, N = _n;
                    int phase = 33;                 // solo solve
                    NativeBlas.Pardiso((void*)ppt, &mf, &mn, &mt, &phase, &N, pa, pia, pja, ppe, &nr, pip, &ml, pb, px, &error);
                }
                if (error != 0) throw new InvalidOperationException($"PARDISO solve error {error}");
                return x;
            }

            private void Free()
            {
                if (_disposed) return;
                _disposed = true;
                if (!NativeBlas.HasPardiso) return;
                int error = 0;
                fixed (long* ppt = _pt)
                fixed (int* pip = _iparm, pia = _rowPtr, pja = _colIdx, ppe = _perm)
                fixed (double* pa = _vals)
                {
                    int mt = _mtype, mf = 1, mn = 1, ml = 0, nr = 1, N = _n;
                    int phase = -1;                 // liberar memoria interna de PARDISO
                    double dummy = 0;
                    NativeBlas.Pardiso((void*)ppt, &mf, &mn, &mt, &phase, &N, pa, pia, pja, ppe, &nr, pip, &ml, &dummy, &dummy, &error);
                }
            }

            public void Dispose() { Free(); GC.SuppressFinalize(this); }
            ~PardisoFactorization() { Free(); }
        }

        public static double[] SolveSparsePardiso(int n, int[] rowPtr, int[] colIdx, double[] vals, double[] b)
        {
            if (!NativeBlas.HasPardiso)
                throw new InvalidOperationException("PARDISO no disponible (requiere Intel MKL real, no OpenBLAS).");
            var pt = new long[64];
            var iparm = new int[64];
            var perm = new int[n];
            var x = new double[n];
            int error = 0;
            fixed (long* ppt = pt)
            fixed (int* pip = iparm, pia = rowPtr, pja = colIdx, ppe = perm)
            fixed (double* pa = vals, pb = b, px = x)
            {
                int mt = 11;   // real, no-simétrica (matriz completa)
                NativeBlas.Pardisoinit((void*)ppt, &mt, pip);
                iparm[34] = 1; // indexado 0-based (C) → usar el CSR del Lab directo
                int mf = 1, mn = 1, ml = 0, nr = 1, N = n;
                int phase = 13;                 // análisis + factorización + solve
                NativeBlas.Pardiso((void*)ppt, &mf, &mn, &mt, &phase, &N, pa, pia, pja, ppe, &nr, pip, &ml, pb, px, &error);
                int err1 = error;
                int rel = -1, e2 = 0;           // liberar memoria interna
                NativeBlas.Pardiso((void*)ppt, &mf, &mn, &mt, &rel, &N, pa, pia, pja, ppe, &nr, pip, &ml, pb, px, &e2);
                if (err1 != 0) throw new InvalidOperationException($"PARDISO error {err1}");
            }
            return x;
        }

        /// <summary>y[m] = A[m×n] * x[n] via cblas_dgemv (row-major).</summary>
        public static void MatVec(int m, int n, double[] A, double[] x, double[] y)
        {
            if (!Available || NativeBlas.Dgemv == null || m < BlasThreshold)
            {
                for (int i = 0; i < m; i++)
                {
                    double s = 0;
                    int rowA = i * n;
                    for (int j = 0; j < n; j++) s += A[rowA + j] * x[j];
                    y[i] = s;
                }
                return;
            }
            fixed (double* pA = A, pX = x, pY = y)
                NativeBlas.Dgemv(NativeBlas.CblasRowMajor, NativeBlas.CblasNoTrans, m, n, 1.0, pA, n, pX, 1, 0.0, pY, 1);
        }

        /// <summary>y[n] += alpha * x[n] via cblas_daxpy.</summary>
        public static void Axpy(int n, double alpha, double[] x, double[] y)
        {
            if (!Available || NativeBlas.Daxpy == null || n < BlasThreshold)
            {
                for (int i = 0; i < n; i++) y[i] += alpha * x[i];
                return;
            }
            fixed (double* pX = x, pY = y)
                NativeBlas.Daxpy(n, alpha, pX, 1, pY, 1);
        }

        /// <summary>dot product nativo via cblas_ddot.</summary>
        public static double Dot(int n, double[] x, double[] y)
        {
            if (!Available || NativeBlas.Ddot == null || n < BlasThreshold)
            {
                double s = 0;
                for (int i = 0; i < n; i++) s += x[i] * y[i];
                return s;
            }
            fixed (double* pX = x, pY = y)
                return NativeBlas.Ddot(n, pX, 1, pY, 1);
        }

        /// <summary>C[m×n] = A[m×k] * B[k×n] (row-major) via cblas_dgemm.
        /// Cae al loop naive para tamaños chicos o sin BLAS.</summary>
        public static void MatMul(int m, int k, int n, double[] A, double[] B, double[] C)
        {
            int maxDim = m > n ? (m > k ? m : k) : (n > k ? n : k);
            if (!Available || maxDim < BlasThreshold)
            {
                MatMulNaive(m, k, n, A, B, C);
                return;
            }
            MatMulNative(m, k, n, A, B, C);
        }

        private static void MatMulNative(int m, int k, int n, double[] A, double[] B, double[] C)
        {
            // Row-major directo: C[m×n] = A[m×k]·B[k×n], lda=k, ldb=n, ldc=n.
            fixed (double* pA = A, pB = B, pC = C)
                NativeBlas.Dgemm(NativeBlas.CblasRowMajor, NativeBlas.CblasNoTrans, NativeBlas.CblasNoTrans,
                                 m, n, k, 1.0, pA, k, pB, n, 0.0, pC, n);
        }

        // Fallback SIN BLAS: ikj vectorizado con System.Numerics.Vector<double>.
        // El JIT lo compila a SIMD (AVX2 = 4 doubles, AVX-512 = 8) → el "9× mas lento
        // sin MKL" se reduce a ~2-3×, sin ninguna libreria nativa. Orden ikj = la fila
        // de B se recorre lineal (cache-friendly) y cada paso es un AXPY: C_i += a_ip · B_p.
        private static void MatMulNaive(int m, int k, int n, double[] A, double[] B, double[] C)
        {
            Array.Clear(C, 0, m * n);
            int W = Vector<double>.Count, nv = n - (n % W);
            for (int i = 0; i < m; i++)
            {
                int rowA = i * k, rowC = i * n;
                for (int p = 0; p < k; p++)
                {
                    double aip = A[rowA + p];
                    if (aip == 0.0) continue;          // salta ceros (matrices ralas)
                    int rowB = p * n, j = 0;
                    var va = new Vector<double>(aip);
                    for (; j < nv; j += W)             // tramo SIMD
                    {
                        var vb = new Vector<double>(B, rowB + j);
                        var vc = new Vector<double>(C, rowC + j);
                        (vc + va * vb).CopyTo(C, rowC + j);
                    }
                    for (; j < n; j++)                 // cola escalar
                        C[rowC + j] += aip * B[rowB + j];
                }
            }
        }

        // ---- VML: element-wise sqrt/exp/log/sin/cos y potencia a^b (b escalar) ----
        // Solo compensa el interop nativo si el vector es grande; debajo de esto el
        // bucle SIMD administrado ya es igual o mejor. Umbral empírico.
        public const int VmlThreshold = 1024;
        public static bool VmlAvailable => NativeBlas.VdSqrt != null;

        /// <summary>y = op(a) para fn: 0=sqrt 1=exp 2=ln 3=sin 4=cos. Devuelve false si VML no está.</summary>
        public static bool VmlUnary(int fn, int n, double[] a, double[] y)
        {
            fixed (double* pa = a, py = y)
            {
                switch (fn)
                {
                    case 0: if (NativeBlas.VdSqrt == null) return false; NativeBlas.VdSqrt(n, pa, py); return true;
                    case 1: if (NativeBlas.VdExp  == null) return false; NativeBlas.VdExp (n, pa, py); return true;
                    case 2: if (NativeBlas.VdLn   == null) return false; NativeBlas.VdLn  (n, pa, py); return true;
                    case 3: if (NativeBlas.VdSin  == null) return false; NativeBlas.VdSin (n, pa, py); return true;
                    case 4: if (NativeBlas.VdCos  == null) return false; NativeBlas.VdCos (n, pa, py); return true;
                    default: return false;
                }
            }
        }

        /// <summary>y = a.^b con b escalar (vdPowx). Devuelve false si VML no está.</summary>
        public static bool VmlPowx(int n, double[] a, double b, double[] y)
        {
            if (NativeBlas.VdPowx == null) return false;
            fixed (double* pa = a, py = y)
                NativeBlas.VdPowx(n, pa, b, py);
            return true;
        }
    }

    /// <summary>LAPACK via LAPACKE (misma libreria que BlasInterop: Intel MKL u OpenBLAS).
    /// Enteros de 32 bits (LP64) → sin el crash ILP64. Para las rutinas con
    /// almacenamiento delicado (banda, simetrica) usamos LAPACK_COL_MAJOR y
    /// replicamos el empaquetado Fortran ya validado; LAPACKE col-major reenvia
    /// a la rutina nativa sin transponer.</summary>
    public static unsafe class LapackInterop
    {
        /// <summary>Threshold: el solve denso solo conviene para n ≥ 64.</summary>
        public const int LapackThreshold = 64;

        /// <summary>Proveedor LAPACK activo: "Intel MKL", "OpenBLAS" o "managed".</summary>
        public static string Provider => NativeBlas.Provider;

        public static readonly bool Available;

        static LapackInterop()
        {
            try
            {
                if (!NativeBlas.HasLapack) { Available = false; return; }
                // Probe: solve [1,2;3,4]·x=[5;11] → x=[1;2] (row-major directo)
                var A = new[] { 1.0, 2.0, 3.0, 4.0 };
                var b = new[] { 5.0, 11.0 };
                var ipiv = new int[2];
                int info;
                fixed (double* pA = A, pB = b)
                fixed (int* pIpiv = ipiv)
                    info = NativeBlas.Dgesv(NativeBlas.LapackRowMajor, 2, 1, pA, 2, pIpiv, pB, 1);
                Available = info == 0 && Math.Abs(b[0] - 1.0) < 1e-9 && Math.Abs(b[1] - 2.0) < 1e-9;
            }
            catch { Available = false; }
        }

        private static bool _warmed;
        /// <summary>Dispara el init PESADO de MKL (threading/kernels de dpbsv) con un solve
        /// mediano, para que la PRIMERA factorizacion del usuario no pague ~70ms de arranque
        /// una sola vez. El probe del cctor es 2x2 (no dispara el threading). Llamar en el
        /// arranque del motor, FUERA de toda medicion (constructor de MatlabPipeline).</summary>
        public static void Warmup()
        {
            if (_warmed) return; _warmed = true;
            if (!Available) return;
            try
            {
                int n = 160, kd = 3;
                var A = new double[n * n];
                for (int i = 0; i < n; i++)
                {
                    A[i * n + i] = 4.0;
                    for (int k = 1; k <= kd; k++)
                        if (i >= k) { A[i * n + (i - k)] = -1.0; A[(i - k) * n + i] = -1.0; }
                }
                var b = new double[n];
                for (int i = 0; i < n; i++) b[i] = 1.0;
                SolveSymBanded(n, kd, A, b);           // dpbsv: init de MKL
            }
            catch { /* si falla, el primer solve real lo paga igual */ }
        }

        /// <summary>Resuelve A·x = b con A simétrica positive definite banded (bw=kd).
        /// A row-major n×n. Retorna x. (AB en banded col-major + LAPACK_COL_MAJOR.)</summary>
        public static double[] SolveSymBanded(int n, int kd, double[] A_row, double[] b)
        {
            if (!Available) throw new InvalidOperationException("LAPACK no disponible");
            int ldab = kd + 1;
            var AB = new double[ldab * n];
            for (int j = 0; j < n; j++)
            {
                int iMin = System.Math.Max(0, j - kd);
                for (int i = iMin; i <= j; i++)
                    AB[j * ldab + (kd + i - j)] = A_row[i * n + j];
            }
            var x = new double[n];
            System.Array.Copy(b, x, n);
            int info;
            fixed (double* pAB = AB, pX = x)
                info = NativeBlas.Dpbsv(NativeBlas.LapackColMajor, (sbyte)'U', n, kd, 1, pAB, ldab, pX, n);
            if (info != 0)
                throw new InvalidOperationException($"dpbsv info={info} (not SPD or arg error)");
            return x;
        }

        /// <summary>Resuelve A·x = b para A cuadrada n×n y b vector n×1.
        /// A es row-major → LAPACKE_dgesv en row-major directo. Retorna x.</summary>
        public static double[] Solve(int n, double[] A_row, double[] b)
        {
            if (!Available) throw new InvalidOperationException("LAPACK no disponible");
            var a = (double[])A_row.Clone();   // dgesv sobreescribe A con la LU
            var x = new double[n];
            Array.Copy(b, x, n);
            var ipiv = new int[n];
            int info;
            fixed (double* pA = a, pX = x)
            fixed (int* pIpiv = ipiv)
                info = NativeBlas.Dgesv(NativeBlas.LapackRowMajor, n, 1, pA, n, pIpiv, pX, 1);
            if (info != 0)
                throw new InvalidOperationException($"dgesv info={info} (singular or argument error)");
            return x;
        }

        /// <summary>A⁻¹ (n×n row-major) resolviendo A·X = I con dgesv (LU + n solves). Es lo que
        /// hace MATLAB (dgetrf+dgetri) — mucho más rápido que el Gauss-Jordan administrado.</summary>
        public static double[] Inverse(int n, double[] A_row)
        {
            if (!Available) throw new InvalidOperationException("LAPACK no disponible");
            var a = (double[])A_row.Clone();          // dgesv sobreescribe A con la LU
            var x = new double[n * n];                // B = I (row-major); sale como A⁻¹
            for (int i = 0; i < n; i++) x[i * n + i] = 1.0;
            var ipiv = new int[n];
            int info;
            fixed (double* pA = a, pX = x)
            fixed (int* pIpiv = ipiv)
                info = NativeBlas.Dgesv(NativeBlas.LapackRowMajor, n, n, pA, n, pIpiv, pX, n);
            if (info != 0)
                throw new InvalidOperationException($"dgesv(inv) info={info} (singular or argument error)");
            return x;
        }

        /// <summary>A⁻¹ para A SIMÉTRICA definida positiva vía Cholesky (dpotrf + dpotri). Hace ~2×
        /// menos trabajo que dgesv/dgetri general (aprovecha la simetría, como Calcpad). Devuelve
        /// null si A NO es SPD (dpotrf info>0) → el llamador cae a la ruta general. A row-major
        /// n×n; se factoriza el triángulo 'U' y luego se refleja a la mitad inferior.</summary>
        public static double[] InverseSPD(int n, double[] A_row)
        {
            if (!Available || NativeBlas.Dpotrf == null || NativeBlas.Dpotri == null) return null;
            var a = (double[])A_row.Clone();
            int info;
            // A es SIMÉTRICA → su almacenamiento row-major ES idéntico al col-major. Usamos
            // COL_MAJOR para que LAPACKE NO haga la transposición interna (2 copias de n²·8 bytes
            // = 200 MB en n=5000). La inversa también es simétrica → válida en ambos layouts.
            fixed (double* pA = a)
            {
                info = NativeBlas.Dpotrf(NativeBlas.LapackColMajor, (sbyte)'U', n, pA, n);
                if (info != 0) return null;           // info>0 → no SPD; info<0 → arg error
                info = NativeBlas.Dpotri(NativeBlas.LapackColMajor, (sbyte)'U', n, pA, n);
                if (info != 0) return null;
            }
            // dpotri('U' col-major) deja la inversa en el triángulo que, en ROW-major, es el
            // INFERIOR (row≥col). Se refleja inferior→superior para completar la matriz simétrica.
            for (int i = 0; i < n; i++)
                for (int j = i + 1; j < n; j++)
                    a[i * n + j] = a[j * n + i];
            return a;
        }

        // ── FFT vía MKL DFTI (rápida, precisa ~1e-15, y correcta para entrada real) ──
        private static bool? _fftOk;
        /// <summary>¿MKL DFTI disponible y con resultado correcto? Self-test contra fft([1,2,3,4]).</summary>
        public static bool FftAvailable
        {
            get
            {
                if (_fftOk.HasValue) return _fftOk.Value;
                _fftOk = false;
                if (NativeBlas.DftiCreateDescriptor == null || NativeBlas.DftiCommitDescriptor == null ||
                    NativeBlas.DftiComputeForward == null || NativeBlas.DftiComputeBackward == null ||
                    NativeBlas.DftiFreeDescriptor == null) return false;
                try
                {
                    var re = new double[] { 1, 2, 3, 4 }; var im = new double[4];
                    Fft1DCore(re, im, false);   // fft([1,2,3,4]) = [10, -2+2i, -2, -2-2i]
                    _fftOk = System.Math.Abs(re[0] - 10) < 1e-9 && System.Math.Abs(re[1] + 2) < 1e-9 &&
                             System.Math.Abs(im[1] - 2) < 1e-9 && System.Math.Abs(re[2] + 2) < 1e-9 &&
                             System.Math.Abs(im[3] + 2) < 1e-9;
                }
                catch { _fftOk = false; }
                return _fftOk.Value;
            }
        }

        /// <summary>FFT/IFFT 1D compleja in-place sobre (re,im) usando MKL DFTI. ifft divide por n
        /// (MKL backward no escala, como MATLAB). Lanza si DFTI falla → el llamador cae a Cooley-Tukey.</summary>
        public static void Fft1D(double[] re, double[] im, bool inverse) => Fft1DCore(re, im, inverse);

        private static void Fft1DCore(double[] re, double[] im, bool inverse)
        {
            int n = re.Length;
            var data = new double[2 * n];                 // complejo intercalado re,im,re,im,…
            for (int i = 0; i < n; i++) { data[2 * i] = re[i]; data[2 * i + 1] = im[i]; }
            void* handle = null;
            long st;
            fixed (double* pd = data)
            {
                st = NativeBlas.DftiCreateDescriptor(&handle, 36, 32, 1, n);   // DFTI_DOUBLE, DFTI_COMPLEX, dim=1
                if (st != 0) throw new InvalidOperationException($"DftiCreateDescriptor st={st}");
                st = NativeBlas.DftiCommitDescriptor(handle);
                if (st == 0)
                    st = inverse ? NativeBlas.DftiComputeBackward(handle, pd)
                                 : NativeBlas.DftiComputeForward(handle, pd);
                NativeBlas.DftiFreeDescriptor(&handle);
                if (st != 0) throw new InvalidOperationException($"Dfti compute st={st}");
            }
            double scale = inverse ? 1.0 / n : 1.0;
            for (int i = 0; i < n; i++) { re[i] = data[2 * i] * scale; im[i] = data[2 * i + 1] * scale; }
        }

        /// <summary>Valores propios generalizados simétricos A·φ = λ·B·φ (itype=1), A simétrica
        /// y B simétrica positive definite. A,B row-major n×n. Retorna (eigenvalues ASC,
        /// eigenvectors row-major V[i*n+k]=componente i del modo k).</summary>
        public static (double[] vals, double[] vecs) SymGenEig(int n, double[] A_row, double[] B_row)
        {
            if (!Available) throw new InvalidOperationException("LAPACK no disponible");
            // Simétricas → row-major≡col-major. Usamos COL_MAJOR para que, a la salida,
            // la columna k de A sea el eigenvector k (A[k*n+i]); LAPACKE gestiona el workspace.
            var A = (double[])A_row.Clone();
            var B = (double[])B_row.Clone();
            var w = new double[n];
            int info;
            fixed (double* pA = A, pB = B, pW = w)
                info = NativeBlas.Dsygv(NativeBlas.LapackColMajor, 1, (sbyte)'V', (sbyte)'U', n, pA, n, pB, n, pW);
            if (info != 0) throw new InvalidOperationException($"dsygv info={info} (B no SPD o arg error)");
            var V = new double[n * n];
            for (int k = 0; k < n; k++)
                for (int i = 0; i < n; i++)
                    V[i * n + k] = A[k * n + i];
            return (w, V);
        }

        public static bool HasGeev => NativeBlas.Dgeev != null;

        /// <summary>Eigenvalores de matriz GENERAL (no simétrica) via LAPACKE_dgeev.
        /// Devuelve (parte real wr, parte imaginaria wi), n cada una. Los eigenvalores no
        /// dependen del layout, así que row-major va directo.</summary>
        public static (double[] wr, double[] wi) Geev(int n, double[] A_row)
        {
            var A = (double[])A_row.Clone();   // dgeev sobreescribe A
            var wr = new double[n];
            var wi = new double[n];
            var dummy = new double[1];
            int info;
            fixed (double* pA = A, pWr = wr, pWi = wi, pD = dummy)
                info = NativeBlas.Dgeev(NativeBlas.LapackRowMajor, (sbyte)'N', (sbyte)'N', n, pA, n, pWr, pWi, pD, 1, pD, 1);
            if (info != 0) throw new InvalidOperationException($"dgeev info={info}");
            return (wr, wi);
        }
    }
}
