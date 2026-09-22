%% Funciones de forma 1D — deducidas paso a paso (1 GDL por nudo)
% #md
% # ¿Qué es una función de forma? Deducida paso a paso
% Una barra recta con dos nudos: en el nudo 1 el desplazamiento vale u_1 y en el nudo 2
% vale u_2 (su ÚNICO grado de libertad). ¿Cuánto vale u(x) en un punto CUALQUIERA entre
% los dos? Las FUNCIONES DE FORMA son los "pesos" con que cada valor nodal aporta al
% punto interior. Hay UNA función de forma por cada grado de libertad.
% #endmd

% #md
% ## 1. Proponemos lo más simple: una recta (2 constantes por hallar)
% #endmd
syms x x_1 x_2 u_1 u_2 a_0 a_1 real
u = a_0 + a_1*x

% #md
% ## 2. Imponemos que pase por los dos nudos
% En x = x_1 debe valer u_1, y en x = x_2 debe valer u_2:
% #endmd
cond1 = subs(u, x, x_1) == u_1
cond2 = subs(u, x, x_2) == u_2

% #md
% ## 3. Despejamos las dos constantes
% #endmd
sol = solve([cond1, cond2], [a_0, a_1]);
u_x = simplify(subs(u, [a_0, a_1], [sol.a_0, sol.a_1]));
a_0 = simplify(sol.a_0);
a_1 = simplify(sol.a_1);
a_0
a_1

% #md
% ## 4. Reescribimos u(x): aparecen las funciones de forma
% u(x) queda como  N_1·u_1 + N_2·u_2. Cada N es el coeficiente de su nudo:
% #endmd
u_x
N_1 = simplify(diff(u_x, u_1))
N_2 = simplify(diff(u_x, u_2))

% #md
% ## 5. Propiedades clave
% (a) Cada N_i vale 1 en SU nudo y 0 en el otro:
% #endmd
p_1 = simplify(subs(N_1, x, x_1))
p_2 = simplify(subs(N_1, x, x_2))
% #md
% (b) Suman 1 en todo punto (partición de la unidad):
% #endmd
suma = simplify(N_1 + N_2)

% #md
% ## 6. En coordenadas naturales (ξ de -1 a +1)
% Mapeo  x = (x_1+x_2)/2 + (x_2-x_1)/2·ξ. Las funciones quedan universales:
% #endmd
syms xi real
xmap = (x_1 + x_2)/2 + (x_2 - x_1)/2 * xi;
N_1 = simplify(subs(N_1, x, xmap))
N_2 = simplify(subs(N_2, x, xmap))
%" Resultado clásico:  N_1 = (1 - ξ)/2,  N_2 = (1 + ξ)/2. Todo DEDUCIDO — nadie tecleó las N.
