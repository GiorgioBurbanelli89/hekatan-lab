%% La ecuación de la recta por dos puntos: y = m·x + b — deducida
% #md
% # La ecuación de la recta que pasa por dos puntos
% Lo más elemental: dados dos puntos (x_1, y_1) y (x_2, y_2), hallamos la recta
% y = m·x + b que pasa por ambos. Es la MISMA idea que una función de forma, pero
% contada como la recta de toda la vida.
% #endmd

% #md
% ## 1. La recta tiene dos incógnitas: pendiente m y ordenada al origen b
% #endmd
syms x m b x_1 y_1 x_2 y_2 real
y = m*x + b

% #md
% ## 2. Imponemos que pase por los dos puntos
% En x = x_1 la recta vale y_1, y en x = x_2 vale y_2:
% #endmd
cond1 = subs(y, x, x_1) == y_1
cond2 = subs(y, x, x_2) == y_2

% #md
% ## 3. Despejamos la pendiente m y la ordenada b
% #endmd
sol = solve([cond1, cond2], [m, b]);
m = simplify(sol.m);
b = simplify(sol.b);
m
b

% #md
% ## 4. La recta final (fórmula de los dos puntos)
% #endmd
y_recta = simplify(m*x + b)
%" m = (y_2 - y_1)/(x_2 - x_1) es la pendiente; y = m·x + b, la recta clásica.
