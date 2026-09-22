function bench1_suma()
% BENCHMARK 1  -  Suma con unidades:  S = Sum i*(1m + 2dm + 3cm + 4mm), i = 1 : 1e8
% (el mismo de N. Ganchovski).  Hekatan mide con tic/toc; Calcpad con timer.
m = 1; dm = 0.1; cm = 0.01; mm = 0.001;
n = 100000000;
tic;
S = 0;
for i = 1:n
  S = S + i*1*m + i*2*dm + i*3*cm + i*4*mm;
end
t = toc;
fprintf('=== BENCHMARK 1 : Suma con unidades  (i = 1 : 1e8) ===\n');
fprintf('S = %.10g m\n', S);
fprintf('Hekatan Lab  (tic/toc) = %.4f s\n', t);
end
