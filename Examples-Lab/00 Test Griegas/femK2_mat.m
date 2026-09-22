diary('femK2_out.txt'); diary on;
u = symunit;
E = 2e8*u.kN/u.m^2; A = 0.01*u.m^2; I = 8.33e-6*u.m^4; L = 3*u.m;
c1 = E*A/L; c2 = 12*E*I/L^3; c3 = 6*E*I/L^2; c4 = 4*E*I/L; c5 = 2*E*I/L;
K = [ c1,0,0,-c1,0,0; 0,c2,c3,0,-c2,c3; 0,c3,c4,0,-c3,c5; -c1,0,0,c1,0,0; 0,-c2,-c3,0,c2,-c3; 0,c3,c5,0,-c3,c4 ];
d = [0.001*u.m; 0.002*u.m; 0.0005; 0; 0; 0];
for w=1:20, fw = K*d; end
N = 200;
tic; for it=1:N, f = K*d; end; t = toc;
fprintf('MATLAB K(6x6) mixta * d (steady): %.4f ms/iter\n', 1000*t/N);
diary off; exit
