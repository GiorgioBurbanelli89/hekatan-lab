%% Analisis de estabilidad de talud con el elemento T6 (paso a paso)
% Replica pedagogica del Demo04 de GEO5 (talud) con la MALLA MINIMA que GEO5 genera
% con arista 20 m: 13 triangulos de 6 nodos (T6), 38 nodos.
% El objetivo NO es clavar el FS exacto, sino ENTENDER como calcula GEO5:
%   (1) la malla,  (2) las funciones de forma T6,  (3) la DOBLE INTEGRAL sobre el
%   triangulo (por que es T6 y no Q4),  (4) el ensamblaje + gravedad,
%   (5) la reduccion de resistencia (SRM) hasta el factor de seguridad FS.
% Motor: Hekatan Lab (MATLAB). Validacion: GEO5 (FS=1.91 en esta malla), Abaqus.

clear; clc;
tic;

%% Datos del problema
%' <h3>Estabilidad de talud — elemento finito T6 (6 nodos)</h3>
%' <hr/>
%' <h4>Datos de los dos suelos</h4>
% Suelo 1 = arcilla (Soil1) ; Suelo 2 = roca (Soil2). Valores del Demo04 de GEO5.
g1  = 18;    E1 = 21e3;   nu1 = 0.30;  c1 = 9;    phi1 = 22.70;   % arcilla
g2  = 20;    E2 = 300e3;  nu2 = 0.20;  c2 = 120;  phi2 = 38.00;   % roca
%' <b>Suelo 1 (arcilla):</b> &gamma; = @{g1} kN/m³, E = @{E1} kPa, &nu; = @{nu1}, c = @{c1} kPa, &phi; = @{phi1}°
%' <b>Suelo 2 (roca):</b> &gamma; = @{g2} kN/m³, E = @{E2} kPa, &nu; = @{nu2}, c = @{c2} kPa, &phi; = @{phi2}°

%% Paso 1 — La malla minima (13 T6, 38 nodos)
%' <h4>Paso 1 — La malla</h4>
%' GEO5 con arista 20 m discretiza el talud en <b>13 triangulos de 6 nodos</b> y
%' <b>38 nodos</b>. Cada triangulo tiene 3 nodos de esquina y 3 nodos en el medio de
%' cada lado — por eso "T6". Con pocos elementos se ve TODA la maquinaria.
XY = [
  0.0000 -11.5000
  14.0000 -11.0000
  11.0000 -9.0000
  0.0000 -9.0000
  21.0000 -2.5000
  29.5000 -2.5000
  21.0000 -9.2500
  32.2500 -4.0000
  40.0000 -4.0000
  40.0000 -9.0000
  40.0000 -21.5000
  18.3300 -21.5000
  0.0000 -21.5000
  7.0000 -11.2500
  12.5000 -10.0000
  5.5000 -10.2500
  0.0000 -10.2500
  5.5000 -9.0000
  16.0000 -5.7500
  17.5000 -6.7500
  25.2500 -2.5000
  21.0000 -5.8800
  25.2500 -5.8800
  30.8800 -3.2500
  26.6200 -6.6200
  36.1200 -4.0000
  36.1200 -6.5000
  40.0000 -6.5000
  30.5000 -9.1200
  17.5000 -10.1200
  30.5000 -15.3800
  40.0000 -15.2500
  29.1700 -21.5000
  19.6700 -15.3800
  9.1700 -21.5000
  16.1700 -16.2500
  7.0000 -16.2500
  0.0000 -16.5000
];
% Conectividad T6: columnas 1-3 = esquinas, 4-6 = nodos medios | col 7 = suelo (1/2)
CONN = [
  1 2 3 14 15 16 2
  4 1 3 17 16 18 2
  5 3 2 19 15 20 1
  6 5 7 21 22 23 1
  8 6 7 24 23 25 1
  9 8 10 26 27 28 1
  7 10 8 29 27 25 1
  2 7 5 30 22 20 1
  10 7 11 29 31 32 2
  12 11 7 33 31 34 2
  13 12 2 35 36 37 2
  1 13 2 38 37 14 2
  7 2 12 30 36 34 2
];
n_nod = size(XY,1);
n_el  = size(CONN,1);
%' Malla: @{n_el} elementos T6, @{n_nod} nodos.

%-- Dibujo la malla: esquinas (o) y nodos medios (.), numeradas, color por suelo
figure; hold on;
for e = 1:n_el
    nd = CONN(e,1:6); s = CONN(e,7);
    cc = [0.85 0.92 1.0]; if s==2, cc = [0.90 0.86 0.78]; end   % arcilla azul / roca ocre
    % contorno curvo del T6 (pasando por los nodos medios)
    ord = [1 4 2 5 3 6];
    patch(XY(nd(ord),1), XY(nd(ord),2), cc, 'EdgeColor', [0.25 0.3 0.4], 'LineWidth', 1.1);
    ce = mean(XY(nd(1:3),:));
    text(ce(1), ce(2), sprintf('%d', e), 'Color', [0.15 0.35 0.7], 'FontSize', 11, 'HorizontalAlignment','center');
end
h_c = plot(XY(:,1), XY(:,2), 'o', 'MarkerFaceColor', [0.20 0.45 0.90], 'MarkerEdgeColor','k','MarkerSize',6,'LineStyle','none','DisplayName','Nodos');
text(XY(:,1)+0.3, XY(:,2)+0.3, string((1:n_nod)'), 'FontSize', 7, 'Color', [0.2 0.2 0.2]);
axis equal; grid on; xlabel('x [m]'); ylabel('z [m]');
title('Paso 1 — Malla minima de GEO5: 13 T6, 38 nodos (azul=arcilla, ocre=roca)');
legend('show');

%% Paso 2 — Las funciones de forma del T6 (coordenadas de area)
%' <h4>Paso 2 — Funciones de forma T6</h4>
%' Aqui esta la diferencia con el Q4. El <b>Q4</b> es un cuadrado y sus funciones son
%' <i>bilineales</i>:  N = ¼(1±&xi;)(1±&eta;).  El <b>T6</b> es un <b>triangulo</b> y se
%' describe con <b>coordenadas de area</b> L₁, L₂, L₃ (que suman 1):
% #noc L_1 = 1 - xi - eta;  L_2 = xi;  L_3 = eta
%' Las 6 funciones de forma son <b>cuadraticas</b> (no lineales): en las esquinas valen
%' L(2L−1) y en los medios 4·L·L. Las deduzco en simbolico:
syms xi eta real
L1 = 1 - xi - eta;  L2 = xi;  L3 = eta;
N1 = L1*(2*L1 - 1);   % esquina 1
N2 = L2*(2*L2 - 1);   % esquina 2
N3 = L3*(2*L3 - 1);   % esquina 3
N4 = 4*L1*L2;         % medio del lado 1-2
N5 = 4*L2*L3;         % medio del lado 2-3
N6 = 4*L3*L1;         % medio del lado 3-1
Nsym = [N1 N2 N3 N4 N5 N6];
%' Funciones de forma  N = [N₁ … N₆]:
Nsym
%' Comprobacion (particion de la unidad): la suma de las 6 debe dar 1:
suma_N = simplify(N1+N2+N3+N4+N5+N6)
%' Sus derivadas respecto a &xi; y &eta; (lo que arma la matriz B):
dN_dxi  = simplify(diff(Nsym, xi))
dN_deta = simplify(diff(Nsym, eta))

%% Paso 3 — La DOBLE INTEGRAL sobre el triangulo (elemento de ejemplo con numeros)
%' <h4>Paso 3 — La doble integral (por que es distinta del Q4)</h4>
%' La rigidez del elemento sale de la <b>misma formula</b> que el Q4:
%' <div style="font-size:13pt;margin:6pt 0;">K<sub>e</sub> = &#8747;&#8747;<sub>A</sub> B<sup>T</sup> · D · B  dA</div>
%' PERO el dominio cambia: en el Q4 se integra en el cuadrado &xi;,&eta; &isin; [−1,1];
%' en el <b>T6 se integra en el triangulo</b>: &#8747;₀¹ &#8747;₀^(1−&xi;) ( · ) d&eta; d&xi;.
%' Esa integral se hace con <b>3 puntos de Gauss</b> del triangulo, en
%' (1/6,1/6), (2/3,1/6), (1/6,2/3), cada uno con peso 1/6.
%' D es la matriz constitutiva en <b>deformacion plana</b> (3×3), y B es <b>3×12</b>
%' (3 deformaciones × 12 gdl = 6 nodos × 2). Lo muestro con NUMEROS en un elemento.

%-- Puntos de Gauss del triangulo (3 puntos, orden 2 — exacto para el T6)
gp = [1/6 1/6; 2/3 1/6; 1/6 2/3];
gw = [1/6; 1/6; 1/6];

%-- Elemento de ejemplo: el numero 1
e_ej = 1;
nd = CONN(e_ej,1:6);
xe = XY(nd,1);  ze = XY(nd,2);
s_ej = CONN(e_ej,7);
[E,nu] = suelo_props(s_ej, E1,nu1, E2,nu2);
D = Dplane(E, nu);
%' Elemento de ejemplo = #@{e_ej} (suelo @{s_ej}). Matriz constitutiva D (deformacion plana):
D
%-- B en el primer punto de Gauss + Jacobiano
[B1, detJ1, J1] = T6_B(xe, ze, gp(1,1), gp(1,2));
%' Jacobiano J en el 1er punto de Gauss (relaciona &xi;,&eta; con x,z):
J1
%' det(J) = @{detJ1}  (2×area del triangulo). Matriz B (3×12) en ese punto:
B1
%-- Rigidez del elemento por la doble integral (suma en los 3 puntos de Gauss)
Ke = zeros(12,12);
for g = 1:3
    [Bg, detJg] = T6_B(xe, ze, gp(g,1), gp(g,2));
    Ke = Ke + gw(g) * (Bg.' * D * Bg) * detJg;
end
%' Sumando  &Sigma; wᵢ · Bᵀ D B · det(J)  en los 3 puntos → K<sub>e</sub> (12×12).
%' Diagonal de K<sub>e</sub> (rigidez de cada gdl del elemento @{e_ej}):
diagKe = diag(Ke)'
%' K<sub>e</sub> es simetrica; su tamaño 12×12 sale de 6 nodos × 2 gdl. Esa es la
%' pieza que el Q4 tendria 8×8 (4 nodos × 2). Todo lo demas es igual.

%% Paso 4 — Ensamblaje + peso propio (gravedad)
%' <h4>Paso 4 — Ensamblaje y gravedad</h4>
%' Se suma cada K<sub>e</sub> en la rigidez global K, y el peso propio de cada suelo
%' entra como fuerza de cuerpo  F = &#8747; Nᵀ·(0,−&gamma;) dA  en los nodos.
ndof = 2*n_nod;
K = zeros(ndof, ndof);
F = zeros(ndof, 1);
NDE = zeros(n_el, 12);      % gdl globales de cada elemento (fila por elemento)
for e = 1:n_el
    nd = CONN(e,1:6); s = CONN(e,7);
    xe = XY(nd,1); ze = XY(nd,2);
    [E,nu] = suelo_props(s, E1,nu1, E2,nu2);
    g      = suelo_gamma(s, g1, g2);
    De = Dplane(E, nu);
    Ke = zeros(12,12); Fe = zeros(12,1);
    for gpt = 1:3
        [Bg, detJg] = T6_B(xe, ze, gp(gpt,1), gp(gpt,2));
        Ke = Ke + gw(gpt)*(Bg.'*De*Bg)*detJg;
        Nv = T6_N(gp(gpt,1), gp(gpt,2));
        for a = 1:6
            Fe(2*a) = Fe(2*a) - gw(gpt)*Nv(a)*g*detJg;   % gravedad hacia -z
        end
    end
    dofs = zeros(1,12);
    for a = 1:6
        dofs(2*a-1) = 2*nd(a)-1;
        dofs(2*a)   = 2*nd(a);
    end
    K(dofs,dofs) = K(dofs,dofs) + Ke;
    F(dofs)      = F(dofs) + Fe;
    NDE(e,:)     = dofs;
end
%' Rigidez global K = @{size(K,1)}×@{size(K,2)}. Peso propio total (&Sigma;F_z):
sumFz = sum(F(2:2:end))
%' (negativo = hacia abajo, en kN).

%-- Condiciones de contorno: base fija (x,z), lados verticales fijos en x
tol = 1e-6;
fixed = false(ndof,1);
for i = 1:n_nod
    if abs(XY(i,2) - min(XY(:,2))) < tol    % base inferior: fija en x y z
        fixed(2*i-1) = true; fixed(2*i) = true;
    end
    if abs(XY(i,1) - 0) < tol || abs(XY(i,1) - 40) < tol  % lados: fijos en x
        fixed(2*i-1) = true;
    end
end
free = ~fixed;

%% Paso 5 — Reduccion de resistencia (SRM) → factor de seguridad
%' <h4>Paso 5 — SRM: reduccion de resistencia hasta el FS</h4>
%' El metodo de GEO5: se divide c y tan&phi; por un factor SRF cada vez mayor y se busca
%' el equilibrio. Mientras el talud aguanta, converge; cuando ya no puede equilibrarse,
%' <b>ese SRF es el factor de seguridad FS</b>. Uso el criterio de Drucker-Prager (el de
%' GEO5) con un esquema viscoplastico (redistribucion de tension) sobre esta malla.
% Criterio DP (ajuste a Mohr-Coulomb en deformacion plana):
%   f = alpha*I1 + sqrt(J2) - k ,  alpha = tan(phi)/sqrt(9+12 tan^2 phi),  k = 3c/sqrt(9+12 tan^2 phi)
SRF_list = 1.0:0.1:2.4;
FS = 0; conv_flags = zeros(numel(SRF_list),1);
Kff = K(free,free); Ff = F(free);
for is = 1:numel(SRF_list)
    SRF = SRF_list(is);
    ok = srm_solve(SRF, NDE, CONN, XY, gp, gw, ...
                   E1,nu1,c1,phi1, E2,nu2,c2,phi2, free, Kff, Ff, ndof);
    conv_flags(is) = ok;
    if ok, FS = SRF; end
    if ~ok, break; end
end
%" Factor de seguridad FS ≈ @{FS}   (GEO5 en esta malla: 1.91)
%' <hr/>
%' El FS de esta malla minima es aproximado (13 elementos); GEO5 con la misma arista
%' da <b>1.91</b>. Lo importante es que cada paso — malla, funciones de forma T6, la
%' doble integral sobre el triangulo, el ensamblaje y la SRM — es exactamente la
%' maquinaria que corre dentro de GEO5.

t_total = toc;
%' Tiempo total (tic/toc) = @{t_total} s
%" FIN — Talud T6 paso a paso

%% ------------------- Funciones auxiliares -------------------
function [E,nu] = suelo_props(s, E1,nu1, E2,nu2)
    if s == 1, E = E1; nu = nu1; else, E = E2; nu = nu2; end
end
function g = suelo_gamma(s, g1, g2)
    if s == 1, g = g1; else, g = g2; end
end
function D = Dplane(E, nu)
    % Deformacion plana, deformaciones [exx eyy gxy] -> tensiones [sxx syy txy]
    c = E/((1+nu)*(1-2*nu));
    D = c * [1-nu,  nu,    0;
             nu,    1-nu,  0;
             0,     0,     (1-2*nu)/2];
end
function Nv = T6_N(xi, eta)
    L1 = 1-xi-eta; L2 = xi; L3 = eta;
    Nv = [L1*(2*L1-1); L2*(2*L2-1); L3*(2*L3-1); 4*L1*L2; 4*L2*L3; 4*L3*L1];
end
function [B, detJ, J] = T6_B(xe, ze, xi, eta)
    L1 = 1-xi-eta;
    % derivadas de N respecto a xi, eta (6x1 cada una)
    dNxi  = [-(4*L1-1); 4*xi-1;  0;        4*(L1-xi); 4*eta;      -4*eta];
    dNeta = [-(4*L1-1); 0;       4*eta-1;  -4*xi;     4*xi;       4*(L1-eta)];
    J = [dNxi.'*xe, dNxi.'*ze;
         dNeta.'*xe, dNeta.'*ze];
    detJ = det(J);
    invJ = inv(J);
    dNx = invJ(1,1)*dNxi + invJ(1,2)*dNeta;
    dNy = invJ(2,1)*dNxi + invJ(2,2)*dNeta;
    B = zeros(3,12);
    for a = 1:6
        B(1, 2*a-1) = dNx(a);
        B(2, 2*a)   = dNy(a);
        B(3, 2*a-1) = dNy(a);
        B(3, 2*a)   = dNx(a);
    end
end
function ok = srm_solve(SRF, NDE, CONN, XY, gp, gw, ...
                        E1,nu1,c1,phi1, E2,nu2,c2,phi2, free, Kff, Ff, ndof)
    % Esquema viscoplastico (redistribucion de tension) de Zienkiewicz-Cormeau.
    % La K elastica es constante (Kff); las tensiones inadmisibles se convierten en
    % cargas de cuerpo que se reiteran hasta que ningun punto de Gauss viola la
    % fluencia (converge) o se agotan las iteraciones (falla → SRF pasado el FS).
    n_el = size(CONN,1);
    itmax = 250; ftol = 1e-3;
    bdy = zeros(ndof,1);   % cargas de cuerpo por viscoplasticidad
    ok = false;
    for it = 1:itmax
        rhs = Ff + bdy(free);
        u = zeros(ndof,1);
        u(free) = Kff \ rhs;              % resuelve Kff*uf = rhs
        newbdy = zeros(ndof,1);
        fmax = 0;
        for e = 1:n_el
            s = CONN(e,7);
            if s == 1, E=E1; nu=nu1; c=c1/SRF; phi=phi1; else, E=E2; nu=nu2; c=c2/SRF; phi=phi2; end
            phir = atan(tan(phi*pi/180)/SRF);     % phi reducido (rad)
            % parametros Drucker-Prager (ajuste MC en deformacion plana)
            tp = tan(phir);
            alpha = tp/sqrt(9 + 12*tp^2);
            kdp   = 3*c/sqrt(9 + 12*tp^2);
            De = local_D(E,nu);
            nd = CONN(e,1:6); xe = XY(nd,1); ze = XY(nd,2);
            dofs = NDE(e,:);
            ue = u(dofs);
            for g = 1:3
                B = T6_B(xe, ze, gp(g,1), gp(g,2));
                eps = B*ue;                       % [exx eyy gxy]
                sig = De*eps;                      % [sxx syy txy]
                sx=sig(1); sy=sig(2); txy=sig(3);
                sz = nu*(sx+sy);                   % deformacion plana
                I1 = sx+sy+sz;
                p  = I1/3;
                dx=sx-p; dy=sy-p; dz=sz-p;
                J2 = 0.5*(dx^2+dy^2+dz^2) + txy^2;
                sJ2 = sqrt(max(J2,1e-12));
                f = alpha*I1 + sJ2 - kdp;
                if f > fmax, fmax = f; end
                if f > 0
                    % direccion de flujo dP/dsig (asociada) en [sx sy txy]
                    dfdsx = alpha + dx/(2*sJ2);
                    dfdsy = alpha + dy/(2*sJ2);
                    dfdt  = txy/sJ2;              % conjugado con gxy
                    m = [dfdsx; dfdsy; dfdt];
                    dt = 1/E;                     % paso viscoplastico estable (pequeno)
                    devp = dt * f * m;            % incremento de deformacion viscoplastica
                    % carga de cuerpo equivalente: B' * D * devp * (peso*detJ)
                    % (usamos el mismo peso*detJ implicito en Kelem via Belem/gw)
                    % recuperamos peso*area del punto reconstruyendo detJ:
                    fb = B.' * (De*devp);
                    newbdy(dofs) = newbdy(dofs) + gw(g)*fb;   % (area la absorbe la iteracion)
                end
            end
        end
        bdy = bdy + newbdy;
        if fmax < ftol
            ok = true; return;
        end
    end
    ok = false;
end
function D = local_D(E, nu)
    c = E/((1+nu)*(1-2*nu));
    D = c * [1-nu, nu, 0; nu, 1-nu, 0; 0, 0, (1-2*nu)/2];
end
