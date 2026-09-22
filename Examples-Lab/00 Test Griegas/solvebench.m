u = symunit;
n = 20;
A = reshape(1:n*n, n, n) + n*eye(n);   % bien condicionada
K = A*u.kN/u.m;
f = (1:n)'*u.kN;
for w=1:20, xw = K\f; end        % warmup
N = 500;
tic; for it=1:N, x = K\f; end; t = toc;
fprintf('K(20x20)\f con unidades (steady): %.4f ms/iter\n', 1000*t/N);
