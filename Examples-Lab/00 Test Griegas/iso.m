u = symunit;
% 6x6 UNIFORME (todo kN/m)
Ku = ones(6,6)*u.kN/u.m;
du = ones(6,1)*u.m;
N = 200;
tic; for it=1:N, fu = Ku*du; end; tu=toc;
fprintf('6x6 UNIFORME: %.3f ms/iter\n', 1000*tu/N);
% 6x6 MIXTO (kN/m, kN, kN*m por columna)
Km = [1*u.kN/u.m, 1*u.kN, 1*u.kN*u.m, 1*u.kN/u.m, 1*u.kN, 1*u.kN*u.m;
      1*u.kN/u.m, 1*u.kN, 1*u.kN*u.m, 1*u.kN/u.m, 1*u.kN, 1*u.kN*u.m;
      1*u.kN/u.m, 1*u.kN, 1*u.kN*u.m, 1*u.kN/u.m, 1*u.kN, 1*u.kN*u.m;
      1*u.kN/u.m, 1*u.kN, 1*u.kN*u.m, 1*u.kN/u.m, 1*u.kN, 1*u.kN*u.m;
      1*u.kN/u.m, 1*u.kN, 1*u.kN*u.m, 1*u.kN/u.m, 1*u.kN, 1*u.kN*u.m;
      1*u.kN/u.m, 1*u.kN, 1*u.kN*u.m, 1*u.kN/u.m, 1*u.kN, 1*u.kN*u.m];
dm = [1*u.m; 1; 1; 1*u.m; 1; 1];
tic; for it=1:N, fm = Km*dm; end; tm=toc;
fprintf('6x6 MIXTO: %.3f ms/iter\n', 1000*tm/N);
% control: 6x6 SIN unidad (numerico puro)
Kn = ones(6,6); dn = ones(6,1);
tic; for it=1:N, fn = Kn*dn; end; tn=toc;
fprintf('6x6 SIN unidad: %.4f ms/iter\n', 1000*tn/N);
