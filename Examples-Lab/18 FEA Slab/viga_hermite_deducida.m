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

%' <h4>5. Se DESPEJAN las constantes en función de los grados de libertad</h4>
%' Si C·a = u, entonces a = C⁻¹·u. Cada constante es una mezcla de v_1, θ_1, v_2, θ_2:
syms v_1 t_1 v_2 t_2 real
a = Ci * [v_1; t_1; v_2; t_2]

%' <h4>6. Se sustituyen en la cúbica y se agrupa</h4>
%' La flecha queda en función de los cuatro movimientos del nodo:
v_u = expand(subs(v, [a_1, a_2, a_3, a_4], a.'))

%' <h4>7. Lo que multiplica a cada grado ES su función de forma</h4>
%' No se escriben: se leen de v(x) derivando respecto de cada grado de libertad.
N_1 = simplify(diff(v_u, v_1))
N_2 = simplify(diff(v_u, t_1))
N_3 = simplify(diff(v_u, v_2))
N_4 = simplify(diff(v_u, t_2))
%' Las cuatro cúbicas de Hermite, juntas:
N = [N_1, N_2, N_3, N_4]

%' <h4>8. Las cuatro, dibujadas (con L = 1)</h4>
%' La gráfica también sale del script: se evalúan las N deducidas y se dibujan.
Ns = subs(N, L, 1);
xs = linspace(0, 1, 101);
Y = zeros(4, numel(xs));
for k = 1:4
    Y(k, :) = double(subs(Ns(k), x, xs));
end
plot(xs, Y(1,:), xs, Y(2,:), xs, Y(3,:), xs, Y(4,:), 'LineWidth', 2)
legend('N_1', 'N_2', 'N_3', 'N_4'); xlabel('x / L'); ylabel('N'); grid on
%' Comprobación: cada N vale 1 en SU grado y 0 en los otros tres.
%' Fila 1 = las cuatro en x = 0 ; fila 2 = las cuatro en x = L:
Nnodos = [Y(:,1)'; Y(:,end)']

%' <h4>9. La curvatura: la segunda derivada de cada una</h4>
B = simplify(diff(N, x, 2))

%' <h4>10. La rigidez: K = EI · ∫ Bᵀ·B dx, de 0 a L</h4>
K = simplify(EI * int(B.' * B, x, 0, L))

%' <h4>11. Con números: un voladizo con carga en la punta</h4>
% Datos (ocultos): E en kN/m², I en m⁴, L en m, P en kN
E = 210e6;  Iz = 8.333e-6;  Lv = 4;  P = 10;
%' E = @E kN/m² ,  I = @Iz m⁴ ,  L = @Lv m ,  P = @P kN
Kn = double(subs(K, {EI, L}, {E*Iz, Lv}))
d = Kn(3:4, 3:4) \ [-P; 0];   % nodo 1 empotrado: quedan v_2 y θ_2
v_2 = d(1)*1000               %' flecha en la punta  v_2 = @ mm
v_teo = -P*Lv^3/(3*E*Iz)*1000 %' fórmula clásica P·L³/(3·E·I) = @ mm
