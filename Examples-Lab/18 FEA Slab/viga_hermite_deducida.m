%% Hermite DEDUCIDA y rigidez de viga: el mismo camino que el vídeo de Hekatan School
%' <h3>Funciones de forma de Hermite, deducidas → matriz de rigidez → flecha</h3>
%' <hr/>
%' Nada se teclea a mano: se supone la cúbica, cada grado de libertad da una condición,
%' y el motor arma C, la invierte y saca las cuatro funciones. Luego, la rigidez y un número.
%' Sin disp ni fprintf: cada línea sin punto y coma se escribe sola en el reporte.

%' <h4>1. Se supone la cúbica, y se deriva (el giro es la pendiente)</h4>
syms x L a_1 a_2 a_3 a_4 EI real
v = a_1 + a_2*x + a_3*x^2 + a_4*x^3
theta = diff(v, x)

%' <h4>2. Cada grado da una condición: flecha y giro en x = 0 y en x = L</h4>
E_1 = subs(v, x, 0)
E_2 = subs(theta, x, 0)
E_3 = subs(v, x, L)
E_4 = subs(theta, x, L)

%' <h4>3. Los factores de a_1 … a_4 en cada condición son las filas de C</h4>
C = jacobian([E_1; E_2; E_3; E_4], [a_1, a_2, a_3, a_4])

%' <h4>4. Se invierte</h4>
Ci = simplify(inv(C))

%' <h4>5. Y las cuatro salen solas: N = base · C⁻¹</h4>
base = [1, x, x^2, x^3];
N = simplify(base * Ci)

%' <h4>6. La curvatura: la segunda derivada de cada una</h4>
B = simplify(diff(N, x, 2))

%' <h4>7. La rigidez: K = EI · ∫ Bᵀ·B dx, de 0 a L</h4>
K = simplify(EI * int(B.' * B, x, 0, L))

%' <h4>8. Con números: un voladizo con carga en la punta</h4>
% Datos (ocultos): E en kN/m², I en m⁴, L en m, P en kN
E = 210e6;  Iz = 8.333e-6;  Lv = 4;  P = 10;
%' E = @E kN/m² ,  I = @Iz m⁴ ,  L = @Lv m ,  P = @P kN
Kn = double(subs(K, {EI, L}, {E*Iz, Lv}))
d = Kn(3:4, 3:4) \ [-P; 0];   % nodo 1 empotrado: quedan v_2 y θ_2
v_2 = d(1)*1000               %' flecha en la punta  v_2 = @ mm
v_teo = -P*Lv^3/(3*E*Iz)*1000 %' fórmula clásica P·L³/(3·E·I) = @ mm
