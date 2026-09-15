%% ==========================================================================
%%  MESA DE TORSION FEA — DKE (Discrete Kirchhoff Element)
%% ==========================================================================
%%  Portico 3D: 4 columnas C40x40 + 4 vigas V30x50 + losa Shell-Thin 10cm
%%  Formulacion: DKE Batoz-Tahar 1982 = Wilson Ch8 DKE (Kirchhoff)
%%  Carga: Live = 0.5 tonf/m2
%%  Mesh: 5x5 = 25 elementos shell, 36 nodos de piso
%%  Ref: Ibrahimbegovic & Wilson (1991), CSI Analysis Reference Ch.X
%% ==========================================================================

%% ── PARTE I: DATOS DE ENTRADA ──────────────────────────────────────────
%  Geometria del modelo (tomada del e2k de ETABS)

a = 6;          % Luz en X (m)
b = 6;          % Luz en Y (m)
H_p = 4;        % Altura de piso (m)

%  Secciones
b_c = 0.40;     % Ancho columna (m)
h_c = 0.40;     % Peralte columna (m)
b_v = 0.30;     % Ancho viga (m)
h_v = 0.50;     % Peralte viga (m)
t_l = 0.10;     % Espesor losa (m)

%  Material — Concreto 4000 Psi (E = 2534564 tonf/m2, nu = 0.2)
E_c = 2534564 * 9.80665;   % kN/m2
nu_c = 0.20;
G_c = E_c / (2*(1 + nu_c));
g = 9.80665;                % kN/tonf

%  Malla 5x5
n_m = 5;
d_x = a / n_m;
d_y = b / n_m;

%  Topologia
n_base = 4;                         % 4 nodos de base (columnas)
n_floor = (n_m + 1)^2;             % 36 nodos de piso
n_j = n_base + n_floor;            % 40 nodos total
n_dof = 6 * n_j;                   % 240 DOFs

fprintf('Nodos = %d, DOFs = %d, Elementos shell = %d, Frames = %d\n', ...
    n_j, n_dof, n_m^2, 4 + 4*n_m);

%% ── Teoria: de donde sale la constante de torsion (Saint-Venant / Prandtl) ──
%' ### Deduccion simbolica de la constante de torsion
%'
%' La rigidez torsional de una seccion maciza viene de la teoria de Saint-Venant.
%' Se introduce la FUNCION DE TENSION DE PRANDTL phi, que se anula en el contorno y
%' cumple la ecuacion de Poisson  lap(phi) = -2*G*theta. Lo deduzco simbolico para
%' una seccion ELIPTICA de semiejes p, q (el caso con solucion cerrada exacta).
syms xs ys p q Gs ths ms real
phi_s = ms*(xs^2/p^2 + ys^2/q^2 - 1);            % se anula en (x/p)^2 + (y/q)^2 = 1
lap_s = simplify(diff(phi_s, xs, 2) + diff(phi_s, ys, 2));
disp('Ecuacion de Poisson  lap(phi) ='), disp(lap_s)
m_s = simplify(solve(lap_s == -2*Gs*ths, ms));  % imponer Poisson -> constante m
disp('Constante  m ='), disp(m_s)
%' El torque es T = 2*(integral de phi sobre el area). Con los momentos de area de
%' la elipse (int x^2 dA = pi p^3 q/4,  int y^2 dA = pi p q^3/4,  int dA = pi p q),
%' y J = T/(G*theta), en UN solo simplify para que G y theta cancelen:
Pi_s = sym('pi');
IntPhi_s = m_s*(Pi_s*p^3*q/4/p^2 + Pi_s*p*q^3/4/q^2 - Pi_s*p*q);
J_elipse = simplify( 2*IntPhi_s / (Gs*ths) );
disp('Constante de torsion de la elipse  J = pi p^3 q^3 / (p^2 + q^2):'), disp(J_elipse)
%' Para una seccion RECTANGULAR la solucion exacta es una serie de Fourier; por eso
%' en ingenieria se usa la aproximacion  J = beta*b*h^3  con beta(h/b) — la que se
%' aplica abajo a las columnas y vigas del portico.

%% ── PARTE II: PROPIEDADES DE SECCION ────────────────────────────────────

%  Columna C40x40
A_c = b_c * h_c;
I_yc = b_c * h_c^3 / 12;
I_zc = h_c * b_c^3 / 12;
r_c = min(b_c, h_c) / max(b_c, h_c);
beta_c = (1/3) * (1 - 0.21*r_c*(1 - r_c^4/12));
J_c = beta_c * max(b_c, h_c) * min(b_c, h_c)^3;

%  Viga V30x50
A_v = b_v * h_v;
I_yv = b_v * h_v^3 / 12;
I_zv = h_v * b_v^3 / 12;
r_v = min(b_v, h_v) / max(b_v, h_v);
beta_v = (1/3) * (1 - 0.21*r_v*(1 - r_v^4/12));
J_v = beta_v * max(b_v, h_v) * min(b_v, h_v)^3;

fprintf('Columna: A=%.4f, Iy=%.6f, J=%.6f\n', A_c, I_yc, J_c);
fprintf('Viga:    A=%.4f, Iy=%.6f, J=%.6f\n', A_v, I_yv, J_v);

%% ── PARTE III: RIGIDEZ FLEXURAL DKE (Batoz DKQ) ────────────────────────
%%  Elemento Discrete Kirchhoff Quadrilateral
%%  DOFs por nodo: [w, bx, by] (3 DOFs x 4 nodos = 12)
%%  bx = dw/dx (pendiente en x), by = dw/dy (pendiente en y)
%%  Cuadratura: 2x2 Gauss | Kirchhoff discreto en mid-sides Q8 Serendipity

%  Rigidez flexural
D_f = E_c * t_l^3 / (12*(1 - nu_c^2));
D_mat = D_f * [1, nu_c, 0; nu_c, 1, 0; 0, 0, (1-nu_c)/2];

%  Parametros DKE
a_h = d_x / 2;
b_h = d_y / 2;
c_a = 1.5 / d_x;
c_b = 1.5 / d_y;
gp = [-1/sqrt(3); 1/sqrt(3)];
gw = [1; 1];

%  Funciones de forma y derivadas se evaluan en cada punto de Gauss
%  (ver funciones al final del archivo)

%  Ensamblar K_DKE 12x12
K_DKE = zeros(12, 12);
for ig = 1:2
    for jg = 1:2
        xi = gp(ig);
        eta = gp(jg);
        B = zeros(3, 12);
        for k = 1:12
            B(1,k) = dHxdxi(k, xi, eta, c_a, c_b, a_h, b_h) / a_h;
            B(2,k) = dHydeta(k, xi, eta, c_a, c_b, a_h, b_h) / b_h;
            B(3,k) = dHxdeta(k, xi, eta, c_a, c_b, a_h, b_h) / b_h ...
                   + dHydxi(k, xi, eta, c_a, c_b, a_h, b_h) / a_h;
        end
        K_DKE = K_DKE + gw(ig)*gw(jg) * (B' * D_mat * B) * a_h * b_h;
    end
end

fprintf('K_DKE (12x12) ensamblada.\n');

%% ── PARTE IV: MEMBRANA Q4 + SHELL 24x24 ────────────────────────────────

%  Membrana Q4 bilineal (8x8)
Em_f = E_c * t_l / (1 - nu_c^2);
K_mem = zeros(8, 8);
for ig = 1:2
    for jg = 1:2
        xi = gp(ig); eta = gp(jg);
        dN = [-(1-eta), (1-eta), (1+eta), -(1+eta);
              -(1-xi), -(1+xi), (1+xi), (1-xi)] / 4;
        dNx = dN(1,:) / a_h;
        dNy = dN(2,:) / b_h;
        B_m = zeros(3, 8);
        for k = 1:4
            B_m(1, 2*k-1) = dNx(k);
            B_m(2, 2*k)   = dNy(k);
            B_m(3, 2*k-1) = dNy(k);
            B_m(3, 2*k)   = dNx(k);
        end
        Dm = Em_f * [1, nu_c, 0; nu_c, 1, 0; 0, 0, (1-nu_c)/2];
        K_mem = K_mem + (B_m' * Dm * B_m) * a_h * b_h;
    end
end

%  Mapeo DKE -> Shell: bx = dw/dx = -ry, by = dw/dy = rx
T_bend = [1, 0, 0; 0, 0, 1; 0, -1, 0];

%  Ensamblar Shell 24x24 (membrana + bending + drilling)
K_shell = zeros(24, 24);

%  Insertar membrana (DOFs 1,2 de cada nodo)
for ni = 1:4
    for nj = 1:4
        K_shell((ni-1)*6+(1:2), (nj-1)*6+(1:2)) = ...
            K_mem((ni-1)*2+(1:2), (nj-1)*2+(1:2));
    end
end

%  Insertar bending con transformacion (DOFs 3,4,5 de cada nodo)
for ni = 1:4
    for nj = 1:4
        K_DKE_block = K_DKE((ni-1)*3+(1:3), (nj-1)*3+(1:3));
        K_DKE_t = T_bend' * K_DKE_block * T_bend;
        K_shell((ni-1)*6+(3:5), (nj-1)*6+(3:5)) = K_DKE_t;
    end
end

%  Drilling DOF (DOF 6) — rigidez artificial debil
drill = sum(abs(diag(K_mem))) * 1e-6 / 8;
for ni = 1:4
    K_shell((ni-1)*6+6, (ni-1)*6+6) = drill;
end

fprintf('K_shell (24x24) ensamblada.\n');

%% ── PARTE V: VIGAS Y COLUMNAS (Euler-Bernoulli 3D) ─────────────────────

%  Funcion de rigidez viga 3D 12x12
K_col_local = beam3d_stiffness(E_c, G_c, A_c, I_yc, I_zc, J_c, H_p);
K_viga_local = beam3d_stiffness(E_c, G_c, A_v, I_yv, I_zv, J_v, d_x);

%  Transformacion columna vertical (Z-up)
T_col = zeros(12);
for ii = 0:3
    T_col(ii*3+1, ii*3+3) = 1;
    T_col(ii*3+2, ii*3+1) = 1;
    T_col(ii*3+3, ii*3+2) = 1;
end
K_cg = T_col' * K_col_local * T_col;

%  Viga en X (sin rotacion)
K_vxg = K_viga_local;

%  Viga en Y (rotar 90 grados)
T_beamY = zeros(12);
for ii = 0:3
    T_beamY(ii*3+1, ii*3+2) = 1;
    T_beamY(ii*3+2, ii*3+1) = -1;
    T_beamY(ii*3+3, ii*3+3) = 1;
end
K_vyg = T_beamY' * K_viga_local * T_beamY;

%  End offsets (brazos rigidos)
ioff_col = 0.25;  % h_viga/2
R_col = eye(12);
R_col(7,11) = ioff_col;
R_col(8,10) = -ioff_col;
K_cg_off = R_col' * K_cg * R_col;

fprintf('Columnas y vigas ensambladas.\n');

%% ── PARTE VI: ENSAMBLAJE GLOBAL + CARGAS + BCs ─────────────────────────

n_k = 6;
K_G = zeros(n_dof);
F_G = zeros(n_dof, 1);

%  Conectividad de nodos de columna
col_nI = [1, 2, 3, 4];
col_nJ = [n_base+1, n_base+n_m+1, ...
           n_base+n_m*(n_m+1)+n_m+1, n_base+n_m*(n_m+1)+1];

%  Ensamblar shells
for jj = 0:n_m-1
    for ii = 0:n_m-1
        nd = [n_base + jj*(n_m+1) + ii + 1, ...
              n_base + jj*(n_m+1) + ii + 2, ...
              n_base + (jj+1)*(n_m+1) + ii + 2, ...
              n_base + (jj+1)*(n_m+1) + ii + 1];
        for ni = 1:4
            for nj = 1:4
                ri = (nd(ni)-1)*n_k + (1:n_k);
                rj = (nd(nj)-1)*n_k + (1:n_k);
                K_G(ri, rj) = K_G(ri, rj) + ...
                    K_shell((ni-1)*n_k+(1:n_k), (nj-1)*n_k+(1:n_k));
            end
        end
    end
end

%  Ensamblar columnas
for ci = 1:4
    nI = col_nI(ci); nJ = col_nJ(ci);
    ri = (nI-1)*n_k + (1:n_k);
    rj = (nJ-1)*n_k + (1:n_k);
    K_G(ri,ri) = K_G(ri,ri) + K_cg_off(1:n_k, 1:n_k);
    K_G(ri,rj) = K_G(ri,rj) + K_cg_off(1:n_k, n_k+1:2*n_k);
    K_G(rj,ri) = K_G(rj,ri) + K_cg_off(n_k+1:2*n_k, 1:n_k);
    K_G(rj,rj) = K_G(rj,rj) + K_cg_off(n_k+1:2*n_k, n_k+1:2*n_k);
end

%  Ensamblar vigas X (bordes sur y norte)
for row = [0, n_m]
    for ii = 0:n_m-1
        nI = n_base + row*(n_m+1) + ii + 1;
        nJ = nI + 1;
        ri = (nI-1)*n_k + (1:n_k);
        rj = (nJ-1)*n_k + (1:n_k);
        K_G(ri,ri) = K_G(ri,ri) + K_vxg(1:n_k, 1:n_k);
        K_G(ri,rj) = K_G(ri,rj) + K_vxg(1:n_k, n_k+1:2*n_k);
        K_G(rj,ri) = K_G(rj,ri) + K_vxg(n_k+1:2*n_k, 1:n_k);
        K_G(rj,rj) = K_G(rj,rj) + K_vxg(n_k+1:2*n_k, n_k+1:2*n_k);
    end
end

%  Ensamblar vigas Y (bordes oeste y este)
for col = [0, n_m]
    for jj = 0:n_m-1
        nI = n_base + col + jj*(n_m+1) + 1;
        nJ = nI + (n_m+1);
        ri = (nI-1)*n_k + (1:n_k);
        rj = (nJ-1)*n_k + (1:n_k);
        K_G(ri,ri) = K_G(ri,ri) + K_vyg(1:n_k, 1:n_k);
        K_G(ri,rj) = K_G(ri,rj) + K_vyg(1:n_k, n_k+1:2*n_k);
        K_G(rj,ri) = K_G(rj,ri) + K_vyg(n_k+1:2*n_k, 1:n_k);
        K_G(rj,rj) = K_G(rj,rj) + K_vyg(n_k+1:2*n_k, n_k+1:2*n_k);
    end
end

%  Cargas Live = 0.5 tonf/m2
q_live = 0.5;
q_total = q_live * g;   % kN/m2
for jj = 0:n_m
    for ii = 0:n_m
        nn = n_base + jj*(n_m+1) + ii + 1;
        if (ii==0 || ii==n_m) && (jj==0 || jj==n_m)
            fac = 0.25;
        elseif ii==0 || ii==n_m || jj==0 || jj==n_m
            fac = 0.5;
        else
            fac = 1.0;
        end
        F_G((nn-1)*6+3) = F_G((nn-1)*6+3) - q_total*d_x*d_y*fac;
    end
end

%  BCs: pinned base (DOFs 1-3 de nodos 1-4)
k_s = 1e20;
for nn = 1:4
    for dd = 1:3
        dof = (nn-1)*6 + dd;
        K_G(dof, dof) = K_G(dof, dof) + k_s;
    end
end

fprintf('K_G (%dx%d) ensamblada. Resolviendo...\n', n_dof, n_dof);

%% ── PARTE VII: SOLUCION ────────────────────────────────────────────────

Z = K_G \ F_G;
fprintf('Sistema resuelto.\n');

%  Deflexion maxima
w_max = 0;
for nn = n_base+1:n_j
    w_val = Z((nn-1)*6+3);
    if abs(w_val) > abs(w_max)
        w_max = w_val;
    end
end
fprintf('w_max losa = %.4f mm\n', w_max*1000);

%% ── PARTE VIII: RESULTADOS COLUMNA ─────────────────────────────────────

u_col = zeros(12, 1);
for dd = 1:6
    u_col(dd)   = Z((col_nI(1)-1)*6 + dd);
    u_col(6+dd) = Z((col_nJ(1)-1)*6 + dd);
end
u_col_end = R_col * u_col;
u_col_local = T_col * u_col_end;
f_col_local = K_col_local * u_col_local;

fprintf('\n=== COLUMNA 1 ===\n');
fprintf('P axial  = %.4f tonf\n', abs(f_col_local(1))/g);
fprintf('V shear  = %.4f tonf\n', abs(f_col_local(2))/g);
fprintf('M2 joint = %.4f tonf.m\n', abs(f_col_local(11))/g);
fprintf('M2 cara  = %.4f tonf.m\n', abs(f_col_local(11) - f_col_local(2)*ioff_col)/g);

%% ── PARTE IX: STRESS RECOVERY (Mxy → Torsion vigas) ────────────────────
%%  M = -D * B * Z_e en 4 puntos de Gauss + extrapolacion a nodos

Mx_n = zeros(n_m+1);
My_n = zeros(n_m+1);
Mxy_n = zeros(n_m+1);
cnt_n = zeros(n_m+1);
sqr3 = sqrt(3);

for ej = 0:n_m-1
    for ei = 0:n_m-1
        nd1 = n_base + ej*(n_m+1) + ei + 1;
        nd2 = nd1 + 1;
        nd3 = nd1 + (n_m+1) + 1;
        nd4 = nd1 + (n_m+1);
        nds = [nd1, nd2, nd3, nd4];

        Z_e = zeros(12, 1);
        for ni = 1:4
            nd_i = nds(ni);
            Z_e((ni-1)*3+1) = Z((nd_i-1)*6+3);     % w
            Z_e((ni-1)*3+2) = -Z((nd_i-1)*6+5);    % bx = -ry
            Z_e((ni-1)*3+3) = Z((nd_i-1)*6+4);     % by = rx
        end

        Mg = zeros(3, 4);  % [Mx; My; Mxy] x 4 Gauss
        for gc = 1:4
            xi = gp(mod(gc-1,2)+1);
            eta = gp(floor((gc-1)/2)+1);
            B = zeros(3, 12);
            for k = 1:12
                B(1,k) = dHxdxi(k, xi, eta, c_a, c_b, a_h, b_h) / a_h;
                B(2,k) = dHydeta(k, xi, eta, c_a, c_b, a_h, b_h) / b_h;
                B(3,k) = dHxdeta(k, xi, eta, c_a, c_b, a_h, b_h) / b_h ...
                       + dHydxi(k, xi, eta, c_a, c_b, a_h, b_h) / a_h;
            end
            kappa = B * Z_e;
            Mg(:,gc) = -D_mat * kappa;
        end

        corners = [-1,-1; 1,-1; -1,1; 1,1];
        for cc = 1:4
            xc = corners(cc,1); yc = corners(cc,2);
            if cc == 1;     ni_e = ei+1; nj_e = ej+1;
            elseif cc == 2; ni_e = ei+2; nj_e = ej+1;
            elseif cc == 3; ni_e = ei+1; nj_e = ej+2;
            else;           ni_e = ei+2; nj_e = ej+2;
            end
            Ng = [(1+(-1)*sqr3*xc)*(1+(-1)*sqr3*yc), ...
                  (1+(1)*sqr3*xc)*(1+(-1)*sqr3*yc), ...
                  (1+(-1)*sqr3*xc)*(1+(1)*sqr3*yc), ...
                  (1+(1)*sqr3*xc)*(1+(1)*sqr3*yc)] / 4;
            Mx_n(ni_e, nj_e) = Mx_n(ni_e, nj_e) + Ng * Mg(1,:)' / g;
            My_n(ni_e, nj_e) = My_n(ni_e, nj_e) + Ng * Mg(2,:)' / g;
            Mxy_n(ni_e, nj_e) = Mxy_n(ni_e, nj_e) + Ng * Mg(3,:)' / g;
            cnt_n(ni_e, nj_e) = cnt_n(ni_e, nj_e) + 1;
        end
    end
end

%  Promediar
idx = cnt_n > 0;
Mx_n(idx) = Mx_n(idx) ./ cnt_n(idx);
My_n(idx) = My_n(idx) ./ cnt_n(idx);
Mxy_n(idx) = Mxy_n(idx) ./ cnt_n(idx);

fprintf('\n=== Mxy nodal (tonf.m/m) ===\n');
disp(Mxy_n)

%% ── PARTE X: COLORMAPS ────────────────────────────────────────────────

[X_grid, Y_grid] = meshgrid(0:d_x:a, 0:d_y:b);

figure;
surf(X_grid, Y_grid, Mxy_n', 'EdgeColor', 'none');
%  Paleta como Hekatan Struct (SAFE): rojo = minimo, azul = maximo (jet invertido).
%  Va DESPUES del surf: en MATLAB colormap() es de la figura ACTUAL, y cada figure()
%  nueva nace con parula, asi que ponerla antes del figure no pinta esta grafica.
colormap(flipud(jet));
colorbar; view(2);
title('Mxy (tonf.m/m) — M12 en ETABS');
xlabel('X (m)'); ylabel('Y (m)');

figure;
w_floor = zeros(n_m+1);
for jj = 0:n_m
    for ii = 0:n_m
        nn = n_base + jj*(n_m+1) + ii + 1;
        w_floor(ii+1, jj+1) = Z((nn-1)*6+3) * 1000;
    end
end
surf(X_grid, Y_grid, w_floor', 'EdgeColor', 'none');
colormap(flipud(jet));   % misma paleta que Hekatan Struct, por figura (ver nota arriba)
colorbar; view(2);
title('Deflexion w (mm)');
xlabel('X (m)'); ylabel('Y (m)');

%% ── FUNCIONES AUXILIARES ───────────────────────────────────────────────

function K = beam3d_stiffness(E, G, A, Iy, Iz, J, L)
    K = zeros(12);
    K(1,1) = E*A/L;  K(1,7) = -E*A/L;
    K(7,1) = -E*A/L; K(7,7) = E*A/L;
    K(4,4) = G*J/L;  K(4,10) = -G*J/L;
    K(10,4) = -G*J/L; K(10,10) = G*J/L;
    c3y = 12*E*Iy/L^3; c2y = 6*E*Iy/L^2; c1y4 = 4*E*Iy/L; c1y2 = 2*E*Iy/L;
    K(3,3)=c3y; K(3,5)=c2y; K(3,9)=-c3y; K(3,11)=c2y;
    K(5,3)=c2y; K(5,5)=c1y4; K(5,9)=-c2y; K(5,11)=c1y2;
    K(9,3)=-c3y; K(9,5)=-c2y; K(9,9)=c3y; K(9,11)=-c2y;
    K(11,3)=c2y; K(11,5)=c1y2; K(11,9)=-c2y; K(11,11)=c1y4;
    c3z = 12*E*Iz/L^3; c2z = 6*E*Iz/L^2; c1z4 = 4*E*Iz/L; c1z2 = 2*E*Iz/L;
    K(2,2)=c3z; K(2,6)=-c2z; K(2,8)=-c3z; K(2,12)=-c2z;
    K(6,2)=-c2z; K(6,6)=c1z4; K(6,8)=c2z; K(6,12)=c1z2;
    K(8,2)=-c3z; K(8,6)=c2z; K(8,8)=c3z; K(8,12)=c2z;
    K(12,2)=-c2z; K(12,6)=c1z2; K(12,8)=c2z; K(12,12)=c1z4;
end

function v = dHxdxi(j, xi, eta, c_a, c_b, a_h, b_h)
    dN = dNq_dxi(xi, eta);
    switch j
        case 1;  v = -c_a*dN(5);
        case 2;  v = dN(1) - 0.25*dN(5) + 0.5*dN(8);
        case 3;  v = 0;
        case 4;  v = c_a*dN(5);
        case 5;  v = dN(2) - 0.25*dN(5) + 0.5*dN(6);
        case 6;  v = 0;
        case 7;  v = c_a*dN(7);
        case 8;  v = dN(3) + 0.5*dN(6) - 0.25*dN(7);
        case 9;  v = 0;
        case 10; v = -c_a*dN(7);
        case 11; v = dN(4) - 0.25*dN(7) + 0.5*dN(8);
        case 12; v = 0;
    end
end

function v = dHxdeta(j, xi, eta, c_a, c_b, a_h, b_h)
    dN = dNq_deta(xi, eta);
    switch j
        case 1;  v = -c_a*dN(5);
        case 2;  v = dN(1) - 0.25*dN(5) + 0.5*dN(8);
        case 3;  v = 0;
        case 4;  v = c_a*dN(5);
        case 5;  v = dN(2) - 0.25*dN(5) + 0.5*dN(6);
        case 6;  v = 0;
        case 7;  v = c_a*dN(7);
        case 8;  v = dN(3) + 0.5*dN(6) - 0.25*dN(7);
        case 9;  v = 0;
        case 10; v = -c_a*dN(7);
        case 11; v = dN(4) - 0.25*dN(7) + 0.5*dN(8);
        case 12; v = 0;
    end
end

function v = dHydxi(j, xi, eta, c_a, c_b, a_h, b_h)
    dN = dNq_dxi(xi, eta);
    switch j
        case 1;  v = -c_b*dN(8);
        case 2;  v = 0;
        case 3;  v = dN(1) + 0.5*dN(5) - 0.25*dN(8);
        case 4;  v = -c_b*dN(6);
        case 5;  v = 0;
        case 6;  v = dN(2) + 0.5*dN(5) - 0.25*dN(6);
        case 7;  v = c_b*dN(6);
        case 8;  v = 0;
        case 9;  v = dN(3) - 0.25*dN(6) + 0.5*dN(7);
        case 10; v = c_b*dN(8);
        case 11; v = 0;
        case 12; v = dN(4) + 0.5*dN(7) - 0.25*dN(8);
    end
end

function v = dHydeta(j, xi, eta, c_a, c_b, a_h, b_h)
    dN = dNq_deta(xi, eta);
    switch j
        case 1;  v = -c_b*dN(8);
        case 2;  v = 0;
        case 3;  v = dN(1) + 0.5*dN(5) - 0.25*dN(8);
        case 4;  v = -c_b*dN(6);
        case 5;  v = 0;
        case 6;  v = dN(2) + 0.5*dN(5) - 0.25*dN(6);
        case 7;  v = c_b*dN(6);
        case 8;  v = 0;
        case 9;  v = dN(3) - 0.25*dN(6) + 0.5*dN(7);
        case 10; v = c_b*dN(8);
        case 11; v = 0;
        case 12; v = dN(4) + 0.5*dN(7) - 0.25*dN(8);
    end
end

function dN = dNq_dxi(x, y)
    dN = zeros(8,1);
    dN(1) = (1-y)*(2*x+y)/4;
    dN(2) = (1-y)*(2*x-y)/4;
    dN(3) = (1+y)*(2*x+y)/4;
    dN(4) = (1+y)*(2*x-y)/4;
    dN(5) = -x*(1-y);
    dN(6) = (1-y^2)/2;
    dN(7) = -x*(1+y);
    dN(8) = -(1-y^2)/2;
end

function dN = dNq_deta(x, y)
    dN = zeros(8,1);
    dN(1) = (1-x)*(x+2*y)/4;
    dN(2) = (1+x)*(-x+2*y)/4;
    dN(3) = (1+x)*(x+2*y)/4;
    dN(4) = (1-x)*(-x+2*y)/4;
    dN(5) = -(1-x^2)/2;
    dN(6) = -y*(1+x);
    dN(7) = (1-x^2)/2;
    dN(8) = -y*(1-x);
end
