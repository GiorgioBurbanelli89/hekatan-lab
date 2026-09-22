% Las_5_Operaciones_Simbolicas.m — Las 5 operaciones del cálculo con variables
% simbólicas, SIN disp ni fprintf: basta escribir la expresión y se muestra el
% resultado solo. Corre igual en Hekatan Lab y en MATLAB 2017a (solo funciones
% nativas; las simbólicas requieren Symbolic Math Toolbox).
clear; clc;
syms x y k n

% #md
% ## 1) SUMATORIA con límite (exacta, simbólica)
% * Σ k²  con límite superior simbólico n → fórmula cerrada
% * Σ k   con límite numérico (1..100) → 5050
% #endmd
symsum(k^2, k, 1, n)
symsum(k, k, 1, 100)

% #md
% ## 2) INTEGRAL
% * Indefinida: ∫ x² dx
% * Definida:   ∫₀¹ x² dx = 1/3 (exacto, fracción)
% #endmd
syms x
int(x^2, x)
int(x^2, x, 0, 1)

% #md
% ## 3) INTEGRAL DOBLE
% * Simbólica (exacta): ∬ x·y dx dy sobre [0,1]² = 1/4
% * Numérica (integral2, nativa): misma integral
% #endmd
syms y
int(int(x*y, x, 0, 1), y, 0, 1)
integral2(@(x, y) x.*y, 0, 1, 0, 1)

% #md
% ## 4) INTEGRAL DE GAUSS (cuadratura numérica, funciones NATIVAS de MATLAB)
% * quadgk  (Gauss-Kronrod adaptativo): ∫₀¹ e^(−x²) dx ≈ 0.746824
% * quadl   (Lobatto adaptativo): la misma integral
% * La forma de LOOP de Gauss-Legendre está en el punto 5.5
% #endmd
quadgk(@(t) exp(-t.^2), 0, 1)
quadl(@(t) exp(-t.^2), 0, 1)

% #md
% ## 5) La forma de LOOPS de las 5 (a mano, para entender cómo se calculan)
% #endmd

% 5.1 Loop de la sumatoria (Σ k², k=1..100 → 338350)
S = 0;
for k = 1:100
    S = S + k^2;
end
S

% 5.2 Loop de la sumatoria con límite simbólico n (Σ k, k=1..n → n(n+1)/2)
syms n
Sn = 0;
for k = 1:50
    Sn = Sn + k;
end
Sn
n*(n+1)/2

% 5.3 Loop de la integral definida (∫₀¹ x² dx con trapecio → ⅓)
h = 1/100; T = 0;
for i = 0:100
    xi = i*h;
    if i == 0 || i == 100
        T = T + 0.5*xi^2;
    else
        T = T + xi^2;
    end
end
T = T*h
T

% 5.4 Loop de la integral doble (∬ x·y dx dy con trapecio anidado → 0.25)
n = 50; hx = 1/n; hy = 1/n; D = 0;
for i = 0:n
    xi = i*hx; wx = 0.5 + 0.5*((i ~= 0) && (i ~= n));
    for j = 0:n
        yj = j*hy; wy = 0.5 + 0.5*((j ~= 0) && (j ~= n));
        D = D + wx*wy*xi*yj;
    end
end
D = D*hx*hy
D

% 5.5 Loop de la integral de Gauss-Legendre (3 puntos, ∫₀¹ x² dx = ⅓ exacto)
gp = [-0.774596669241; 0; 0.774596669241];  gw = [5/9; 8/9; 5/9];
a = 0; b = 1; G = 0;
for k = 1:3
    xk = (a+b)/2 + (b-a)/2*gp(k);
    G = G + gw(k)*xk^2;
end
G = (b-a)/2*G
G

% #md
% ## Bonus: Gauss-Legendre 2D (loop, producto tensorial 2×2)
% ∬ x·y dx dy sobre [0,1]² = 0.25 — sin funciones extra
% #endmd
g2 = [-0.577350269190; 0.577350269190];  w2 = [1; 1];
Dg = 0;
for i = 1:2
    for j = 1:2
        xi = (g2(i)+1)/2;  yj = (g2(j)+1)/2;
        Dg = Dg + w2(i)*w2(j)*xi*yj;
    end
end
Dg = Dg/4
Dg
