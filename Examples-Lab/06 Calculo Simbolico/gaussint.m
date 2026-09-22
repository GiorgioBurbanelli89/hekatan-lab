function I = gaussint(f, a, b, n)
%GAUSSINT Cuadratura de Gauss-Legendre numerica:  I = gaussint(f, a, b[, n])
%   f: function handle  @(x) ...   (o @(x) f(x))
%   a, b: limites de integracion.  n: numero de puntos (defecto 5).
%   Exacto para polinomios de grado <= 2*n-1.  Ejemplos:
%       gaussint(@(x) x.^2, 0, 1)        % = 1/3
%       gaussint(@(t) exp(-t.^2), 0, 1)  % ~ 0.746824
%   En Hekatan Lab esta funcion ya es un builtin (usa sintaxis simbolica:
%   gaussint(x^2, x, 0, 1) tras `syms x`). Este archivo es para MATLAB real.
if nargin < 3, error('gaussint(@f, a, b[, n])'); end
if nargin < 4 || isempty(n), n = 5; end
if ~isa(f, 'function_handle'), error('gaussint: el 1er argumento debe ser @(x) ...'); end

[xg, wg] = gauss_legendre(n);
c = (b + a)/2; h = (b - a)/2;
I = h * sum(wg .* f(c + h*xg));
end

function [xg, wg] = gauss_legendre(n)
% Nodos y pesos de Gauss-Legendre en [-1, 1] (Newton sobre Legendre).
xg = zeros(n, 1); wg = zeros(n, 1);
m = floor((n + 1)/2);
tol = 1e-15;
for i = 1:m
    z = cos(pi*(i - 0.25)/(n + 0.5));
    pp = 0;
    while true
        p1 = 1; p2 = 0;
        for j = 1:n
            p3 = p2; p2 = p1;
            p1 = ((2*j - 1)*z*p2 - (j - 1)*p3)/j;
        end
        pp = n*(z*p1 - p2)/(z*z - 1);
        z1 = z;
        z = z1 - p1/pp;
        if abs(z - z1) <= tol, break; end
    end
    xg(i) = -z;  xg(n - i + 1) = z;
    wg(i) = 2/((1 - z*z)*pp*pp);  wg(n - i + 1) = wg(i);
end
end
