%% Finite Element Analysis of Rectangular Slab
% #md
% # Finite Element Analysis of Rectangular Slab
% #endmd
%' ---
% #md
% ## Input data
% #endmd
% #img https://calcpad.eu/media/mechanics/elastic/slab.png
a = 6; b = 4; t = 0.1; q = 10; E = 35000; nu = 0.15;
%' Slab dimensions - @a m, @b m
%' Thickness - @t m
%' Load - @q kN/m²
%' Modulus of elasticity - @E MPa
%' Poisson's ratio - @nu

%% Finite element mesh
% #md
% ## Finite element mesh
% #endmd
n = 16;
%' We will use rectangular finite element with @n DOFs
n_a = 6; n_b = 4;
%' Number of elements along a and b - @n_a, @n_b
n_e = n_a*n_b           %' Total number of elements
n_j = (n_a + 1)*(n_b + 1)   %' Total number of joints
a_1 = a/n_a             %' Element dimensions (m)
b_1 = b/n_b
n_s = 2*(n_a + n_b)     %' Supported joints count

x_j = zeros(n_j, 1); y_j = zeros(n_j, 1);
x = 0; y = 0;
for j = 1:n_j
    x_j(j) = x;
    y_j(j) = y;
    y = y + b_1;
    if y > b + 1e-9
        y = 0;
        x = x + a_1;
    end
end
e_j = zeros(n_e, 4);
for i_a = 1:n_a
    for i_b = 1:n_b
        e = i_b + n_b*(i_a - 1);
        j = e + i_a - 1;
        e_j(e, 1) = j;
        e_j(e, 2) = j + n_b + 1;
        e_j(e, 3) = j + n_b + 2;
        e_j(e, 4) = j + 1;
    end
end
s_j = zeros(n_s, 1);
i_s = 0;
for i = 1:n_a + 1
    i_s = i_s + 1;
    s_j(i_s) = (n_b + 1)*i - n_b;
end
for i = 1:n_a + 1
    i_s = i_s + 1;
    s_j(i_s) = (n_b + 1)*i;
end
for i = 2:n_b
    i_s = i_s + 1;
    s_j(i_s) = i;
end
for i = 2:n_b
    i_s = i_s + 1;
    s_j(i_s) = n_a*(n_b + 1) + i;
end

%' Joint coordinates (m)
x_j'
y_j'
%' Numbers of elements joints
e_j'
%' Supported joints
s_j'

figure; hold on;
for e = 1:n_e
    k = e_j(e, :);
    patch(x_j(k), y_j(k), [0.9 1 0.9], 'EdgeColor', [0.18 0.55 0.34]);
    text(mean(x_j(k)), mean(y_j(k)), num2str(e), 'HorizontalAlignment', 'center');
end
plot(x_j(s_j), y_j(s_j), 'o', 'MarkerFaceColor', [1 0.71 0.76], 'MarkerEdgeColor', 'r', 'MarkerSize', 9);
plot(x_j, y_j, 'o', 'MarkerFaceColor', [1 0.27 0], 'MarkerEdgeColor', [1 0.27 0], 'MarkerSize', 4);
text(x_j + 0.08, y_j + 0.12, (1:n_j)');
axis equal; axis off;

%% Finite element formulation
% #md
% ## Finite element formulation
% **Shape functions**
% #endmd
%' Base functions, first and second derivatives along a dimension of length l (ξ = x/l):
Phi   = @(x, l) [1 - x^2*(3 - 2*x), x*l*(1 - x*(2 - x)), x^2*(3 - 2*x), x^2*l*(-1 + x)]
dPhi  = @(x, l) [-6*(x/l)*(1 - x), 1 - x*(4 - 3*x), 6*(x/l)*(1 - x), -x*(2 - 3*x)]
ddPhi = @(x, l) [-(6/l^2)*(1 - 2*x), -(2/l)*(2 - 3*x), 6/l^2*(1 - 2*x), -(2/l)*(1 - 3*x)]
%' Each of the 16 shape functions is a product N = Φ_ia(ξ)·Φ_ib(η). Per joint: w, θ_x, θ_y, ψ;
%' joints 1 = (0,0), 2 = (1,0), 3 = (1,1), 4 = (0,1). Index of Φ along a (i_a) and along b (i_b):
i_a = [1 2 1 2 3 4 3 4 3 4 3 4 1 2 1 2]
i_b = [1 1 2 2 1 1 2 2 3 3 4 4 3 3 4 4]
N = @(xi, eta) Phi(xi, a_1)(i_a).*Phi(eta, b_1)(i_b);

% #md
% **Constitutive matrix** (stress - strain relationship)
% #endmd
D = E*1000*t^3/(12*(1 - nu^2))*[1, nu, 0; nu, 1, 0; 0, 0, (1 - nu)/2]

% #md
% **Strain-displacement matrix**
% #endmd
%' B = [ Φ″_ia(ξ)·Φ_ib(η) ;  Φ_ia(ξ)·Φ″_ib(η) ;  2·Φ′_ia(ξ)·Φ′_ib(η) ]
B = @(xi, eta) [ddPhi(xi, a_1)(i_a).*Phi(eta, b_1)(i_b); ...
                Phi(xi, a_1)(i_a).*ddPhi(eta, b_1)(i_b); ...
                2*dPhi(xi, a_1)(i_a).*dPhi(eta, b_1)(i_b)];

%' The elements of the stiffness matrix will be calculated by using the equation
%' K_e = a_1·b_1·∫₀¹∫₀¹ B(ξ; η)ᵀ·D·B(ξ; η) dξ dη
%' The integrand is a polynomial of degree ≤ 6 in ξ and in η: Gauss-Legendre with
%' 4 points per direction integrates it exactly.
g_x = ([-0.861136311594053, -0.339981043584856, 0.339981043584856, 0.861136311594053] + 1)/2;
g_w = [0.347854845137454, 0.652145154862546, 0.652145154862546, 0.347854845137454]/2;
K_e = zeros(n, n); F_e = zeros(n, 1);
for i = 1:4
    for k = 1:4
        B_g = B(g_x(i), g_x(k));
        K_e = K_e + a_1*b_1*g_w(i)*g_w(k)*(B_g'*D*B_g);
        F_e = F_e + a_1*b_1*g_w(i)*g_w(k)*q*N(g_x(i), g_x(k))';
    end
end
% #md
% **Element stiffness matrix**
% #endmd
K_e
%' Element load vector (kN)
%' F_e = a_1·b_1·∫₀¹∫₀¹ N(ξ; η)ᵀ·q dξ dη
F_e'

%% Solution
% #md
% ## Solution
% #endmd
k_1 = n/4;
n_g = k_1*n_j;
K = zeros(n_g, n_g); F = zeros(n_g, 1);
for e = 1:n_e
    g = zeros(1, n);
    for i = 1:4
        g(k_1*(i - 1) + (1:k_1)) = k_1*(e_j(e, i) - 1) + (1:k_1);
    end
    K(g, g) = K(g, g) + K_e;
    F(g) = F(g) + F_e;
end
%' Addition of supports (penalty k_s on w, and on the rotation along the edge)
k_s = 1e20;
for i = 1:n_s
    j = k_1*(s_j(i) - 1) + 1;
    K(j, j) = K(j, j) + k_s;
    if y_j(s_j(i)) == 0 || y_j(s_j(i)) == b
        K(j + 1, j + 1) = K(j + 1, j + 1) + k_s;
    end
    if x_j(s_j(i)) == 0 || x_j(s_j(i)) == a
        K(j + 2, j + 2) = K(j + 2, j + 2) + k_s;
    end
end
%' Global stiffness matrix: @n_g × @n_g
%' Global load vector (kN)
F'
%' Solution of the system of equations (mm)
Z = 1000*(K \ F);
Z'

%% Results
% #md
% ## Results
% #endmd
%' Joint displacements (mm)
W_z = zeros(n_a + 1, n_b + 1);
for i = 1:n_a + 1
    for k = 1:n_b + 1
        W_z(i, k) = round(Z(4*((i - 1)*(n_b + 1) + k) - 3)*1000)/1000;
    end
end
W_z'
[X_g, Y_g] = meshgrid(0:a_1:a, 0:b_1:b);
[X_f, Y_f] = meshgrid(linspace(0, a, 61), linspace(0, b, 41));
figure; contourf(X_f, Y_f, interp2(X_g, Y_g, -W_z', X_f, Y_f, 'spline'), 20, 'LineStyle', 'none');
colorbar; colormap(flipud(jet)); axis equal; title('w (mm)');
w_c = W_z(n_a/2 + 1, n_b/2 + 1);
%' w(a/2; b/2) = @w_c mm

%' Bending moments
Z_e = @(e) Z(reshape(k_1*(e_j(e, :) - 1) + (1:k_1)', 1, []))/1000;
M_j = zeros(3, n_j); c_j = zeros(n_j, 1);
for e = 1:n_e
    z = Z_e(e);
    for i = 1:4
        j = e_j(e, i);
        c_j(j) = c_j(j) + 1;
        M_j(:, j) = M_j(:, j) - D*B((x_j(j) - x_j(e_j(e, 1)))/a_1, (y_j(j) - y_j(e_j(e, 1)))/b_1)*z;
    end
end
M_j = M_j./c_j';
M_x = reshape(M_j(1, :), n_b + 1, n_a + 1);
M_y = reshape(M_j(2, :), n_b + 1, n_a + 1);
M_xy = reshape(M_j(3, :), n_b + 1, n_a + 1);

e = (round(n_a/2) - 1)*n_a + round(n_b/2) + 1;
j = e_j(e, 1);
%' Results for element @e and joint @j:
Z_e15 = 1000*Z_e(e)'
M_e = -D*B(0, 0)*Z_e(e)
%' Average bending moments at joints (kNm/m)
M_j

%' Bending moments - M_x (kNm/m)
M_x
figure; contourf(X_f, Y_f, interp2(X_g, Y_g, M_x, X_f, Y_f, 'spline'), 20, 'LineStyle', 'none');
colorbar; colormap(flipud(jet)); axis equal; title('M_x (kNm/m)');
M_x_max = M_x(n_b/2 + 1, n_a/2 + 1);
%' Maximal value - M_x(a/2; b/2) = @M_x_max kNm/m

%' Bending moments - M_y (kNm/m)
M_y
figure; contourf(X_f, Y_f, interp2(X_g, Y_g, M_y, X_f, Y_f, 'spline'), 20, 'LineStyle', 'none');
colorbar; colormap(flipud(jet)); axis equal; title('M_y (kNm/m)');
M_y_max = M_y(n_b/2 + 1, n_a/2 + 1);
%' Maximal value - M_y(a/2; b/2) = @M_y_max kNm/m

%' Bending moments - M_xy (kNm/m)
M_xy
figure; contourf(X_f, Y_f, interp2(X_g, Y_g, M_xy, X_f, Y_f, 'spline'), 20, 'LineStyle', 'none');
colorbar; colormap(flipud(jet)); axis equal; title('M_{xy} (kNm/m)');
M_xy_0 = M_xy(1, 1);
%' Maximal value - M_xy(0; 0) = @M_xy_0 kNm/m
