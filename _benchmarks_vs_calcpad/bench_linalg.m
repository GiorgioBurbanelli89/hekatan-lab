function bench_linalg()
% Benchmark lineal: matmul, inv(SPD), fft, eig. Entradas DETERMINISTAS
% (idénticas en MATLAB y Hekatan) -> se compara tiempo Y norma del resultado.
% best-of-3 con warmup previo. tic/toc.
fprintf('=== bench_linalg ===\n');

% ---------- #2 matmul 2000x2000 ----------
n = 2000;
A = mod((1:n)'*(1:n), 7) + 0.5;
B = mod((1:n)'*((2:n+1)), 5) + 0.3;
C = A*B;                                  % warmup
t = inf; for k=1:3; tic; C = A*B; t = min(t, toc); end
fprintf('matmul  %dx%d : %.4f s  | chk=%.6e\n', n, n, t, norm(C(:,1)));

% ---------- #3 inv(SPD) 1500x1500 (Cholesky) ----------
m = 1500;
S = A(1:m,1:m);
SPD = S'*S + m*eye(m);                     % simetrica definida positiva
Xi = inv(SPD);                             % warmup
t = inf; for k=1:3; tic; Xi = inv(SPD); t = min(t, toc); end
fprintf('inv SPD %dx%d : %.4f s  | chk=%.6e\n', m, m, t, norm(Xi(:,1)));

% ---------- #4 fft 2^22 ----------
L = 2^22;
x = cos((0:L-1)'*0.001) + 0.5*sin((0:L-1)'*0.003);
y = fft(x);                               % warmup
t = inf; for k=1:3; tic; y = fft(x); t = min(t, toc); end
fprintf('fft     2^22   : %.4f s  | chk=%.6e\n', t, abs(y(2)));

% ---------- #5 eig simetrica 1000x1000 ----------
p = 1000;
Sy = A(1:p,1:p); Sy = (Sy + Sy')/2;
e = eig(Sy);                              % warmup
t = inf; for k=1:3; tic; e = eig(Sy); t = min(t, toc); end
fprintf('eig sym %dx%d : %.4f s  | chk=%.6e\n', p, p, t, max(real(e)));
fprintf('=== fin ===\n');
end
