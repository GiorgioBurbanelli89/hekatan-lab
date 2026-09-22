u = symunit;
n = 20;
A = reshape(1:n*n, n, n);
K = A*u.kN/u.m;
x = (1:n)'*u.m;
N = 200;
tic;
for it=1:N
  f = K*x;
end
t = toc;
fprintf('matmul con unidades %dx%d, %d iters: %.4f s total, %.3f ms/iter\n', n, n, N, t, 1000*t/N);
