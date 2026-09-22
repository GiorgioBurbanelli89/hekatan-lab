diary('mcheck_out.txt'); diary on;
u = symunit;
n = 20;
A = reshape(1:n*n, n, n);
x = (1:n)';
% --- CON unidad (symunit) ---
Ku = A*u.kN/u.m;  xu = x*u.m;
fprintf('class(Ku) = %s   (double=numerico/MKL, sym=simbolico)\n', class(Ku));
N = 5;
tic; for i=1:N, fu = Ku*xu; end; tu = toc;
fprintf('matmul CON unidad:  %.4f ms/iter\n', 1000*tu/N);
% --- SIN unidad (numerico puro, usa BLAS/MKL) ---
Kn = double(A); xn = double(x);
fprintf('class(Kn) = %s\n', class(Kn));
N2 = 100000;
tic; for i=1:N2, fn = Kn*xn; end; tn = toc;
fprintf('matmul SIN unidad:  %.6f ms/iter\n', 1000*tn/N2);
fprintf('RATIO (unidad/numerico) = %.0fx mas lento con unidad\n', (tu/N)/(tn/N2));
diary off; exit
