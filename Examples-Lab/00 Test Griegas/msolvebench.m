diary('msolveb_out.txt'); diary on;
u = symunit;
n = 20;
A = reshape(1:n*n, n, n) + n*eye(n);
K = A*u.kN/u.m;
f = (1:n)'*u.kN;
for w=1:2, xw = K\f; end
N = 3;
tic; for it=1:N, x = K\f; end; t = toc;
fprintf('MATLAB K(20x20) solve con unidades: %.4f ms/iter\n', 1000*t/N);
diary off; exit
