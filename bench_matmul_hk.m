% Benchmark matmul Hekatan Lab (mismo test de Ned/MATLAB) con tic/toc
sizes = [1000 2000 4000];
for k = 1:numel(sizes)
    n = sizes(k);
    A = floor(1000*rand(n,n));
    B = 2*A;
    C = A*B;                    % warm-up
    A(1,1) = A(1,1) + 1e-9;     % mutar operando: evita hoisting del JIT
    t = tic;
    C = A*B;
    dt = toc(t);
    s = C(1,1);                 % sumidero de liveness
    fprintf('%4d   %8.3f s   %6.0f GFLOP/s\n', n, dt, 2*n^3/dt/1e9);
end
