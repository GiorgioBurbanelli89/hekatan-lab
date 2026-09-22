%% Matriz de rigidez REAL de un elemento viga-columna 2D (6x6) CON unidades
% DOFs: [u1 v1 th1 u2 v2 th2]. Entradas MIXTAS: kN/m, kN, kN*m.
u = symunit;
E = 2e8*u.kN/u.m^2;   % modulo (kN/m^2)
A = 0.01*u.m^2;       % area
I = 8.33e-6*u.m^4;    % inercia
L = 3*u.m;            % longitud
c1 = E*A/L;           % axial      -> kN/m
c2 = 12*E*I/L^3;      % flex-transl-> kN/m
c3 = 6*E*I/L^2;       % transl-rot -> kN
c4 = 4*E*I/L;         % rot-rot    -> kN*m
c5 = 2*E*I/L;         % rot-rot    -> kN*m
K = [ c1,  0,   0,  -c1,  0,   0;
      0,   c2,  c3,  0,  -c2,  c3;
      0,   c3,  c4,  0,  -c3,  c5;
     -c1,  0,   0,   c1,  0,   0;
      0,  -c2, -c3,  0,   c2, -c3;
      0,   c3,  c5,  0,  -c3,  c4 ];
% desplazamiento nodal: traslaciones en m, rotaciones adimensionales
d = [0.001*u.m; 0.002*u.m; 0.0005; 0; 0; 0];
N = 50;
tic;
for it=1:N
  f = K*d;   % fuerzas: [kN kN kN*m kN kN kN*m]
end
t = toc;
fprintf('K(6x6) con unidades * d : %d iters, %.3f ms/iter\n', N, 1000*t/N);

