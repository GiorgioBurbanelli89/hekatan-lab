function bench1_render()
% BENCHMARK 1 : Suma con unidades  (i = 1 : 1e8)
m = 1; dm = 0.1; cm = 0.01; mm = 0.001;
n = 100000000;
% Expresion renderizada (markup Calcpad en comentario; MATLAB lo ignora):
%$Sum{i*1m + i*2dm + i*3cm + i*4mm @ i = 1 : n}
tic;
S = 0;
for i = 1:n
  S = S + i*1*m + i*2*dm + i*3*cm + i*4*mm;
end
t = toc;
fprintf('S = %.10g m\n', S);
fprintf('Hekatan Lab (tic/toc, sum nativo) = %.4f s\n', t);
end
