function bench1_math()
% BENCHMARK 1 con expresion matematica renderizada ($Sum) + tic/toc
m = 1; dm = 0.1; cm = 0.01; mm = 0.001;
n = 100000000;
tic;
S = $Sum{i*1*m + i*2*dm + i*3*cm + i*4*mm @ i = 1 : n};
t = toc;
fprintf('S = %.10g m\n', S);
fprintf('Hekatan Lab (tic/toc) = %.4f s\n', t);
end
