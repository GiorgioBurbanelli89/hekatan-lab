%% ShellMITC4 de OpenSees en Hekatan Lab
% #md
% # Elemento ShellMITC4 de OpenSees, traducido a Hekatan Lab
% Traducción línea a línea de *ShellMITC4.cpp*, *R3vectors.cpp* y *ElasticMembranePlateSection.cpp*
% (OpenSees, rama master). (C) 1999 The Regents of the University of California, All Rights Reserved;
% uso según el fichero COPYRIGHT de OpenSees. ShellMITC4: Ed Love; reimplementación L. Tesser,
% D. A. Talledo, V. Le Corvec. Referencia: Dvorkin y Bathe, *Eng. Comput.* 1, 77-88 (1984).
%
% El oráculo es **OpenSees corriendo** (openseespy 3.7.1): sus matrices están en *ref_opensees/*,
% las escribe *hekatan-opensees/opensees_port/shellmitc4/verificar_vs_opensees.py*.
% #endmd

%% Datos
% #md
% ## Datos
% #endmd
E = 2.5e7; nu = 0.2; h = 0.15;
%' Módulo de elasticidad @E kN/m², coeficiente de Poisson @nu, espesor @h m

%% 1. Sección
% #md
% ## 1. Sección elástica membrana + placa (ElasticMembranePlateSection::getSectionTangent)
% Deformaciones generalizadas: ε₁₁, ε₂₂, γ₁₂ (membrana), κ₁₁, κ₂₂, 2κ₁₂ (flexión), γ₁₃, γ₂₃ (cortante).
% La flexión entra con signo **menos**; el elemento lo corrige después multiplicando por −1 las filas de flexión de B_J.
% #endmd
M = E/(1 - nu^2)*h            %' rigidez de membrana (kN/m)
G = 0.5*E/(1 + nu)*h          %' rigidez a cortante en el plano (kN/m)
D = E*h^3/12/(1 - nu^2)       %' rigidez a flexión (kN·m)
G_s = 5/6*G                   %' cortante transversal con κ = 5/6 (kN/m)
d_d = zeros(8, 8);
d_d(1, 1) = M; d_d(2, 2) = M; d_d(1, 2) = nu*M; d_d(2, 1) = nu*M; d_d(3, 3) = G;
d_d(4, 4) = -D; d_d(5, 5) = -D; d_d(4, 5) = -nu*D; d_d(5, 4) = -nu*D; d_d(6, 6) = -0.5*D*(1 - nu);
d_d(7, 7) = G_s; d_d(8, 8) = G_s;
d_d
D_OS = readmatrix('ref_opensees/seccion.csv');
e_1 = max(max(abs(d_d - D_OS)))/max(max(abs(D_OS)))
%' Error relativo de la sección frente a OpenSees: @e_1

%% 2. Rigidez de giro en el plano (drilling)
% #md
% ## 2. Rigidez de giro en el plano (ShellMITC4::setDomain)
% K_tt es el **menor autovalor** del bloque de membrana 3×3, calculado con el Jacobi de *R3vectors.cpp* (LovelyEig).
% #endmd
lambda = lovely_eig(d_d(1:3, 1:3))
K_tt = min(lambda(3), min(lambda(1), lambda(2)))
%' Coincide con G·h: el menor autovalor es la rigidez a cortante @G kN/m

%% 3. Paso a paso en el cuadrilátero distorsionado
% #md
% ## 3. Paso a paso en un cuadrilátero distorsionado
% **Base local** (computeBasis): v₁ = ½(x₃ + x₂ − x₄ − x₁), v₂ = ½(x₄ + x₃ − x₂ − x₁), Gram-Schmidt, g₃ = g₁ × g₂.
% Las coordenadas locales son las **absolutas proyectadas** sobre g₁ y g₂ (no se resta el nudo 1).
% #endmd
X_d = [0 0 0; 2.2 0.3 0; 1.8 1.7 0; 0.2 1.2 0]
[x_l, g_1, g_2, g_3] = compute_basis(X_d);
g_1
g_2
g_3
x_l
% #md
% **Cortante MITC4**: la matriz G (4×12) da γ en los 4 puntos de amarre (lados 4-1, 2-1, 3-2, 3-4);
% en cada punto de Gauss se interpola con M_s (1 ± ξ, 1 ± η), se escala por r/(8·det J) y se gira con R.
% #endmd
[G_m, A_x, B_x, C_x, A_y, B_y, C_y, R] = mitc4_shear_data(x_l);
G_m
alpha = atan(A_y/A_x)
beta = pi/2 - atan(C_x/C_y)
R

%% 4. Rigidez del elemento contra OpenSees
% #md
% ## 4. Matriz de rigidez 24×24 contra OpenSees
% K = Σ_gauss [ B_Jᵀ·(d_d·dvol)·B_K + (K_tt·dvol)·b_J·b_K ], con B en GDL **globales**
% (assembleB ya multiplica por g₁, g₂, g₃; no hay transformación aparte). Gauss 2×2 en ±1/√3.
% #endmd
K_d = mitc4_K(X_d, d_d);
%' Primer bloque 6×6 (nudo 1 con nudo 1) del distorsionado:
K_d(1:6, 1:6)
e_dist = max(max(abs(K_d - readmatrix('ref_opensees/K_distorsionado.csv'))))/max(max(abs(K_d)))
writematrix(K_d, 'lab_K_distorsionado.csv');

X_c = [0 0 0; 1 0 0; 1 1 0; 0 1 0];
K_c = mitc4_K(X_c, d_d);
e_cuad = max(max(abs(K_c - readmatrix('ref_opensees/K_cuadrado.csv'))))/max(max(abs(K_c)))
writematrix(K_c, 'lab_K_cuadrado.csv');

%' Rectángulo 2 × 1 en un plano inclinado (prueba la base g fuera del plano XY):
X_r = [0 0 0; 1.6 1.2 0; 1.6 1.2 1; 0 0 1];
K_r = mitc4_K(X_r, d_d);
e_rect = max(max(abs(K_r - readmatrix('ref_opensees/K_rectangulo.csv'))))/max(max(abs(K_r)))
writematrix(K_r, 'lab_K_rectangulo.csv');

%' Cuadrilátero alabeado (los 4 nudos fuera de un mismo plano):
X_w = [0.1 0 0.05; 2.2 0.3 -0.12; 1.8 1.7 0.2; 0.2 1.2 -0.08];
K_w = mitc4_K(X_w, d_d);
e_alab = max(max(abs(K_w - readmatrix('ref_opensees/K_alabeado.csv'))))/max(max(abs(K_w)))
writematrix(K_w, 'lab_K_alabeado.csv');

%' Modos de energía nula del cuadrado (6 de sólido rígido):
lambda_K = sort(abs(eig(K_c)));
n_0 = sum(lambda_K < 1e-9*max(lambda_K))

%% 5. Placa 4 × 4
% #md
% ## 5. Placa 4 × 4 elementos, bordes con traslaciones fijas, carga puntual en el centro
% #endmd
n = 4; L = 2; P = -10;
%' Placa de @L m × @L m, @n × @n elementos, carga @P kN en el centro
n_n = (n + 1)^2;
x_n = zeros(n_n, 3);
for j = 0:n
    for i = 0:n
        x_n(j*(n + 1) + i + 1, :) = [i*L/n, j*L/n, 0];
    end
end
K = zeros(6*n_n, 6*n_n); F = zeros(6*n_n, 1);
for j = 0:n - 1
    for i = 0:n - 1
        a = j*(n + 1) + i + 1;
        c = [a, a + 1, a + n + 2, a + n + 1];
        g = zeros(1, 24);
        for k = 1:4
            g(6*k - 5:6*k) = 6*(c(k) - 1) + (1:6);
        end
        K(g, g) = K(g, g) + mitc4_K(x_n(c, :), d_d);
    end
end
k_c = (n/2)*(n + 1) + n/2 + 1;
F(6*(k_c - 1) + 3) = P;
fijo = zeros(1, 6*n_n);
for k = 1:n_n
    if min(x_n(k, 1), x_n(k, 2)) < 1e-12 || max(x_n(k, 1), x_n(k, 2)) > L - 1e-12
        fijo(1, 6*(k - 1) + (1:3)) = 1;
    end
end
libre = find(fijo == 0);
U = zeros(6*n_n, 1);
U(libre) = K(libre, libre) \ F(libre);
U_n = reshape(U, 6, n_n)';
w_c = U_n(k_c, 3)
U_OS = readmatrix('ref_opensees/U_placa.csv');
w_c_OS = U_OS(k_c, 3)
e_placa = max(max(abs(U_n - U_OS)))/max(max(abs(U_OS)))
writematrix(U_n, 'lab_U_placa.csv');
W = reshape(U_n(:, 3), n + 1, n + 1)';
figure; contourf(0:L/n:L, 0:L/n:L, W*1000, 20, 'LineStyle', 'none');
colorbar; colormap(flipud(jet)); axis equal; title('w (mm), Hekatan Lab');

%% Resumen
% #md
% ## Resumen: error relativo máximo frente a OpenSees
% #endmd
%' Sección 8×8: @e_1
%' K cuadrado: @e_cuad · K rectángulo inclinado: @e_rect
%' K distorsionado: @e_dist · K alabeado: @e_alab
%' Desplazamientos de la placa 4×4: @e_placa
%' (los ceros son errores del orden de 1e-16 que el formato redondea: ver arriba el valor completo)


% =====================================================================
%  Funciones: traducción de ShellMITC4.cpp y R3vectors.cpp (OpenSees)
% =====================================================================

% R3vectors.cpp, LovelyEig (l.70-221): Jacobi 3×3 simétrica, solo la mitad superior
function d = lovely_eig(Mx)
  tol = 1.0e-08;
  a = [Mx(1, 2), Mx(2, 3), Mx(3, 1)];
  d = [Mx(1, 1), Mx(2, 2), Mx(3, 3)];
  b = d; z = [0 0 0];
  its = 0;
  sm = abs(a(1)) + abs(a(2)) + abs(a(3));
  while sm > tol
    if its < 3
      thresh = 0.011*sm;
    else
      thresh = 0.0;
    end
    for i = 1:3
      j = mod(i, 3) + 1;
      k = mod(j, 3) + 1;
      aij = a(i);
      g = 100.0*abs(aij);
      if abs(d(i)) + g ~= abs(d(i)) || abs(d(j)) + g ~= abs(d(j))
        if abs(aij) > thresh
          a(i) = 0.0;
          hh = d(j) - d(i);
          if abs(hh) + g == abs(hh)
            t = aij/hh;
          else
            r = hh/aij;
            if r > 0.0
              t = 2.0/(r + sqrt(4.0 + r*r));
            else
              t = -2.0/(-r + sqrt(4.0 + r*r));
            end
          end
          c = 1.0/sqrt(1.0 + t*t);
          s = t*c;
          tau = s/(1.0 + c);
          hh = t*aij;
          z(i) = z(i) - hh; z(j) = z(j) + hh;
          d(i) = d(i) - hh; d(j) = d(j) + hh;
          hh = a(j); g = a(k);
          a(j) = hh + s*(g - hh*tau);
          a(k) = g - s*(hh + g*tau);
        end
      else
        a(i) = 0.0;
      end
    end
    b = b + z; d = b; z = [0 0 0];
    its = its + 1;
    sm = abs(a(1)) + abs(a(2)) + abs(a(3));
  end
end

% ShellMITC4.cpp, computeBasis (l.1766-1845)
function [xl, g1, g2, g3] = compute_basis(X)
  v1 = 0.5*(((X(3, :) + X(2, :)) - X(4, :)) - X(1, :));
  v2 = 0.5*(((X(4, :) + X(3, :)) - X(2, :)) - X(1, :));
  v1 = v1/norm(v1);
  alp = v2*v1';
  v2 = v2 - v1*alp;
  v2 = v2/norm(v2);
  g3 = [v1(2)*v2(3) - v1(3)*v2(2), v1(3)*v2(1) - v1(1)*v2(3), v1(1)*v2(2) - v1(2)*v2(1)];
  g1 = v1; g2 = v2;
  xl = zeros(2, 4);
  for i = 1:4
    xl(1, i) = X(i, :)*v1';
    xl(2, i) = X(i, :)*v2';
  end
end

% ShellMITC4.cpp, shape2d (l.2187-2245): N, derivadas en x, y locales y det J
function [shp, xsj] = shape2d(ss, tt, x)
  s = [-0.5, 0.5, 0.5, -0.5];
  t = [-0.5, -0.5, 0.5, 0.5];
  shp = zeros(3, 4);
  for i = 1:4
    shp(3, i) = (0.5 + s(i)*ss)*(0.5 + t(i)*tt);
    shp(1, i) = s(i)*(0.5 + t(i)*tt);
    shp(2, i) = t(i)*(0.5 + s(i)*ss);
  end
  xs = zeros(2, 2);
  for i = 1:2
    for j = 1:2
      for k = 1:4
        xs(i, j) = xs(i, j) + x(i, k)*shp(j, k);
      end
    end
  end
  xsj = xs(1, 1)*xs(2, 2) - xs(1, 2)*xs(2, 1);
  jinv = 1.0/xsj;
  sx = [xs(2, 2)*jinv, -xs(1, 2)*jinv; -xs(2, 1)*jinv, xs(1, 1)*jinv];
  for i = 1:4
    temp = shp(1, i)*sx(1, 1) + shp(2, i)*sx(2, 1);
    shp(2, i) = shp(1, i)*sx(1, 2) + shp(2, i)*sx(2, 2);
    shp(1, i) = temp;
  end
end

% ShellMITC4.cpp, getInitialStiff (l.874-935): G 4×12, Ax..Cy, rotación Rot
function [G, Ax, Bx, Cx, Ay, By, Cy, Rot] = mitc4_shear_data(xl)
  dx34 = xl(1, 3) - xl(1, 4); dy34 = xl(2, 3) - xl(2, 4);
  dx21 = xl(1, 2) - xl(1, 1); dy21 = xl(2, 2) - xl(2, 1);
  dx32 = xl(1, 3) - xl(1, 2); dy32 = xl(2, 3) - xl(2, 2);
  dx41 = xl(1, 4) - xl(1, 1); dy41 = xl(2, 4) - xl(2, 1);
  q = 0.25;
  G = zeros(4, 12);
  G(1, 1) = -0.5; G(1, 2) = -dy41*q; G(1, 3) = dx41*q;
  G(1, 10) = 0.5; G(1, 11) = -dy41*q; G(1, 12) = dx41*q;
  G(2, 1) = -0.5; G(2, 2) = -dy21*q; G(2, 3) = dx21*q;
  G(2, 4) = 0.5; G(2, 5) = -dy21*q; G(2, 6) = dx21*q;
  G(3, 4) = -0.5; G(3, 5) = -dy32*q; G(3, 6) = dx32*q;
  G(3, 7) = 0.5; G(3, 8) = -dy32*q; G(3, 9) = dx32*q;
  G(4, 7) = 0.5; G(4, 8) = -dy34*q; G(4, 9) = dx34*q;
  G(4, 10) = -0.5; G(4, 11) = -dy34*q; G(4, 12) = dx34*q;
  Ax = -xl(1, 1) + xl(1, 2) + xl(1, 3) - xl(1, 4);
  Bx = xl(1, 1) - xl(1, 2) + xl(1, 3) - xl(1, 4);
  Cx = -xl(1, 1) - xl(1, 2) + xl(1, 3) + xl(1, 4);
  Ay = -xl(2, 1) + xl(2, 2) + xl(2, 3) - xl(2, 4);
  By = xl(2, 1) - xl(2, 2) + xl(2, 3) - xl(2, 4);
  Cy = -xl(2, 1) - xl(2, 2) + xl(2, 3) + xl(2, 4);
  alph = atan(Ay/Ax);
  bet = 3.141592653589793/2 - atan(Cx/Cy);
  Rot = [sin(bet), -sin(alph); -cos(bet), cos(alph)];
end

% ShellMITC4.cpp, getInitialStiff (l.825-1102) con assembleB (l.1995-2117),
% computeBmembrane (l.2121), computeBbend (l.2152) y computeBdrill (l.1929)
function K = mitc4_K(X, dd)
  sg = [-1, 1, 1, -1]/sqrt(3);
  tg = [-1, -1, 1, 1]/sqrt(3);
  wg = [1, 1, 1, 1];
  lam = lovely_eig(dd(1:3, 1:3));
  Ktt = min(lam(3), min(lam(1), lam(2)));
  [xl, g1, g2, g3] = compute_basis(X);
  [G, Ax, Bx, Cx, Ay, By, Cy, Rot] = mitc4_shear_data(xl);
  Gmem = [g1; g2];
  Gshear = zeros(3, 6);
  Gshear(1, 1:3) = g3; Gshear(2, 4:6) = g1; Gshear(3, 4:6) = g2;
  K = zeros(24, 24);
  for i = 1:4
    r1 = Cx + sg(i)*Bx;
    r3 = Cy + sg(i)*By;
    r1 = sqrt(r1*r1 + r3*r3);
    r2 = Ax + tg(i)*Bx;
    r3 = Ay + tg(i)*By;
    r2 = sqrt(r2*r2 + r3*r3);
    [shp, xsj] = shape2d(sg(i), tg(i), xl);
    dvol = wg(i)*xsj;
    Ms = zeros(2, 4);
    Ms(2, 1) = 1 - sg(i); Ms(1, 2) = 1 - tg(i); Ms(2, 3) = 1 + sg(i); Ms(1, 4) = 1 + tg(i);
    Bsv = Ms*G;
    Bsv(1, :) = Bsv(1, :)*r1/(8*xsj);
    Bsv(2, :) = Bsv(2, :)*r2/(8*xsj);
    Bs = Rot*Bsv;
    B = zeros(8, 24); Bd = zeros(1, 24);
    for j = 1:4
      Bm = [shp(1, j), 0; 0, shp(2, j); shp(2, j), shp(1, j)];
      Bb = [0, -shp(1, j); shp(2, j), 0; shp(1, j), -shp(2, j)];
      Bsh = Bs(:, 3*j - 2:3*j);
      c = 6*j - 5;
      B(1:3, c:c + 2) = Bm*Gmem;
      B(4:6, c + 3:c + 5) = Bb*Gmem;
      B(7:8, c:c + 5) = Bsh*Gshear;
      Bd(c:c + 2) = -0.5*shp(2, j)*g1 + 0.5*shp(1, j)*g2;
      Bd(c + 3:c + 5) = -shp(3, j)*g3;
    end
    BJ = B;
    for j = 1:4
      c = 6*j - 5;
      BJ(4:6, c + 3:c + 5) = -BJ(4:6, c + 3:c + 5);
    end
    K = K + BJ'*(dd*dvol)*B + (Ktt*dvol)*(Bd'*Bd);
  end
end
