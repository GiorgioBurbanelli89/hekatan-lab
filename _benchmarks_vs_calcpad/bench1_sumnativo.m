function bench1_sumnativo()
% BENCHMARK 1 : Suma con unidades (i = 1 : 1e8) con sum() NATIVO de MATLAB
m = 1; dm = 0.1; cm = 0.01; mm = 0.001;
n = 100000000;
tic;
i = 1:n;
S = sum(i*1*m + i*2*dm + i*3*cm + i*4*mm);
t = toc;
fprintf('S = %.10g m\n', S);
fprintf('Hekatan Lab (tic/toc, sum nativo) = %.4f s\n', t);
end
