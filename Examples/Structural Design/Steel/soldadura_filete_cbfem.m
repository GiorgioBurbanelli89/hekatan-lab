%% Soldadura de filete como ELEMENTO FINITO (idealizacion CBFEM de IDEA StatiCa)
%" Soldadura de filete como elemento finito (idealización CBFEM de IDEA StatiCa)
%' Fuente: extraído del binario de IDEA StatiCa 26.0 (AnalysisModelCreatorConnection.dll,
%' AnalysisModel.dll, IdeaStatiCa.ConnectionCheckAISC.dll) y verificado corriendo su solver
%' k2fem64 con un mini deck (placa-base-8pernos/RE_soldadura_idea_sap.txt y weld_deck/).
%' Nada de esta hoja es de memoria: cada fórmula tiene su clase de origen.
%' Unidades: mm, N, MPa.
%'-----

%"< 1. Del triángulo del filete a la TIRA en el plano de la garganta
%' El filete es un triángulo de pierna s. IDEA no lo malla con sólidos: lo reemplaza por
%' una TIRA de cáscara (CQUAD4) que va del punto medio de una pierna al punto medio de la
%' otra (WeldTools.GetSolidWeldGeometry → GetHeightPoints). Primero todo en álgebra:
%' La garganta es la distancia de la raíz a la cara del filete (triángulo rectángulo a 45°):
% #deq a = s*cos(pi/4) = s/sqrt(2) @@(1.1)
%' El ancho de la tira es la distancia entre los 2 puntos medios, (0, s/2) y (s/2, 0):
syms s positive
b_sym = simplify(sqrt((s/2)^2 + (s/2)^2))   %' (con s > 0, |s| = s)
%' El ancho de la tira sale igual a la garganta (b = a) y la tira queda a 45°, paralela a la
%' cara del filete. IDEA le da espesor PSHELL T = a (wld.Size guarda la garganta).
%' Ahora los valores del ejemplo:
a = 5              %' garganta: @ mm
s_leg = a*sqrt(2)  %' pierna: @ mm
%' Área efectiva de un tramo de largo L_e (la que usa el chequeo, sec. 5):  A_e = L_e·a.

% ---- FIGURA 1: corte del filete ------------------------------------------
figure('Color','w'); hold on; axis equal;
tw_d = 6;                                      % media pared dibujada (a la izquierda)
patch([-tw_d 0 0 -tw_d], [0 0 12 12], [.80 .80 .85], 'EdgeColor','k');           % pared
patch([-tw_d 12 12 -tw_d], [-5 -5 0 0], [.80 .80 .85], 'EdgeColor','k');        % placa
patch([0 s_leg 0], [0 0 s_leg], [1 .85 .55], 'EdgeColor',[.6 .4 0], 'LineWidth',1.5);   % filete
% tira (superficie media): punto medio pierna vertical -> punto medio pierna horizontal
P1 = [0 s_leg/2];  P2 = [s_leg/2 0];
nrm = [1 1]/sqrt(2);
Q = [P1 - nrm*a/2; P2 - nrm*a/2; P2 + nrm*a/2; P1 + nrm*a/2];
patch(Q(:,1), Q(:,2), [.3 .5 .9], 'FaceAlpha',0.25, 'EdgeColor',[.2 .3 .8], 'LineStyle','--');
plot([P1(1) P2(1)], [P1(2) P2(2)], 'b-', 'LineWidth',3);
plot([P1(1) P2(1)], [P1(2) P2(2)], 'bo', 'MarkerFaceColor','b');
plot([0 s_leg/2], [0 s_leg/2], 'r-', 'LineWidth',1.5);                        % garganta
text(s_leg/4+0.3, s_leg/4-0.6, 'garganta a', 'Color','r');
text(s_leg/2, -1.2, 's (pierna)', 'HorizontalAlignment','center');
text(-0.4, s_leg/2+0.8, 's', 'HorizontalAlignment','right');
plot([0 s_leg], [-0.6 -0.6], 'k-');  plot([-0.2 -0.2], [0 s_leg], 'k-');
text(P1(1)+0.3, P1(2)+0.9, 'tira CQUAD4 (ancho a)', 'Color','b');
text(Q(3,1)+0.2, Q(3,2), 'espesor T = a', 'Color',[.2 .3 .8]);
text(1.0, 0.35, '45 deg');
text(-tw_d+0.5, 10, 'pared'); text(8, -3, 'placa');
title(sprintf('Filete s = %.2f mm  ->  tira de ancho a = %.1f mm, espesor a, a 45 grados', s_leg, a));
xlabel('x [mm]'); ylabel('z [mm]');
snapnow;

%"< 2. Tramos con nudos propios y unión RBE3
%' A lo largo del cordón la tira se parte en n tramos, n = round(L/h), con h el tamaño de
%' malla de las placas. Cada tramo tiene NUDOS PROPIOS (hueco de 10⁻⁴ mm entre vecinos,
%' AddWeldElemPoints): no hay continuidad a lo largo del cordón. Por qué: así cada tramo
%' es un "resorte" independiente que puede plastificar solo, y la redistribución la hacen
%' las PLACAS (que sí son continuas), como pasa en un cordón real.
%' Cada nudo de la tira se cuelga de la placa con un RBE3 (AddRbe3LinksUnity):
% #deq u_ref = (w_1*u_1 + w_2*u_2)/(w_1 + w_2) @@(2.1)
% #deq F_1 = w_1/(w_1 + w_2)*F_ref @@(2.2)
%' El RBE3 no es rígido (ese es el RBE2): el nudo de referencia se mueve como el PROMEDIO
%' PONDERADO de los nudos de la placa y su fuerza se reparte con los mismos pesos. No añade
%' rigidez: toda la rigidez del enlace es la de la tira. IDEA usa w_i = 1, componentes 123.

% ---- FIGURA 2: esquema tramos + RBE3 ----------------------------------------
figure('Color','w'); hold on;
yn = 0:20:60;
plot([-5 65], [0 0], 'k-', 'LineWidth',2);  plot([-5 65], [10 10], 'k-', 'LineWidth',2);
plot(yn, 0*yn, 'ks', 'MarkerFaceColor',[.5 .5 .5], 'MarkerSize',8);
plot(yn, 0*yn+10, 'ks', 'MarkerFaceColor',[.5 .5 .5], 'MarkerSize',8);
for j = 1:3
  y0 = yn(j) + 1.2;  y1 = yn(j+1) - 1.2;
  patch([y0 y1 y1 y0], [3 3 7 7], [.3 .5 .9], 'FaceAlpha',0.35, 'EdgeColor','b');
  plot([y0 y1 y1 y0], [3 3 7 7], 'bo', 'MarkerFaceColor','b');
  for yy = [y0 y1]
    plot([yy yn(j)], [3 0], 'r--');  plot([yy yn(j+1)], [3 0], 'r--');
    plot([yy yn(j)], [7 10], 'r--'); plot([yy yn(j+1)], [7 10], 'r--');
  end
  text((y0+y1)/2, 5, sprintf('tramo %d', j), 'HorizontalAlignment','center');
end
text(-4, 11.3, 'pared (nudos de la malla)');  text(-4, -1.3, 'placa (nudos de la malla)');
text(22, 8.3, 'RBE3 (rojo): promedio ponderado, sin rigidez', 'Color','r');
text(22, 1.7, 'hueco 1e-4 mm entre tramos', 'Color','b');
axis([-6 66 -3 13]);
title('Soldadura partida en tramos con nudos propios, colgados con RBE3');
xlabel('y a lo largo del cordon [mm]'); ylabel('z [mm]');
snapnow;

%"< 3. Qué transmite cada tramo
%' En los ejes locales de la tira (x a lo largo del cordón, y a lo ancho, z normal):
%' • σ<sub>⊥</sub> = σ<sub>y</sub> : normal a la garganta
%' • τ<sub>⊥</sub> = τ<sub>yz</sub> : corte transversal
%' • τ<sub>∥</sub> = τ<sub>xy</sub> : corte longitudinal
%' • σ<sub>∥</sub> = σ<sub>x</sub> = 0 : la tira NO toma tensión paralela al cordón, ni flexión
%'   (cara superior = cara inferior).
%' Verificado en el mini deck de k2fem64 (elemento 201, Fz = 100 kN):
sigma_par_201 = 0.00       %' sig_x: @ MPa
sigma_perp_201 = 52.47     %' sig_y (arriba = abajo): @ MPa
tau_perp_201 = 52.47       %' tau_yz: @ MPa
tau_par_201 = -37.79       %' tau_xy: @ MPa
%' σ<sub>⊥</sub> = |τ<sub>⊥</sub>| exacto: la fuerza del tramo es vertical y se reparte a 45° en partes iguales.

%"< 4. Ley elasto-plástica bilineal (AddWeldNonlinearMaterial)
%' En álgebra (AISC LRFD; k = HardeningCoefficient, fijo 0.75 en AISC; ε<sub>pl</sub> = LimitStrain):
syms F_EXX phi_w k_h epsilon_pl real
sigma_lim = phi_w*0.6*F_EXX              %' tensión límite (WeldSigmaLimit)
sigma_y = k_h*sigma_lim                  %' fluencia (tarjeta MATS1 LIMIT)
H = sigma_lim*(1 - k_h)/epsilon_pl       %' pendiente de endurecimiento
%' Con esto la tensión llega a σ<sub>lim</sub> justo cuando la deformación plástica vale ε<sub>pl</sub>:
chk = simplify(sigma_y + H*epsilon_pl - sigma_lim)
%' Y ahora se reemplazan los valores (electrodo E70XX):
F_EXX = 482         %' MPa
phi_w = 0.75        %' φ LRFD de filetes
k_h = 0.75          %' k de AISC
epsilon_pl = 0.05   %' deformación plástica límite (5 %)
sigma_lim = phi_w*0.6*F_EXX              %' MPa
sigma_y = k_h*sigma_lim                  %' MPa
H = sigma_lim*(1 - k_h)/epsilon_pl       %' MPa
FEXX = F_EXX;  phi = phi_w;  kh = k_h;  epl = epsilon_pl;

% ---- FIGURA 3: ley sigma - eps_pl -------------------------------------------
figure('Color','w'); hold on;
ep = [0 0 epl 1.3*epl];
sg = [0 sigma_y sigma_lim sigma_lim + H*0.3*epl];
plot(ep*100, sg, 'b-', 'LineWidth',2.5);
plot([0 epl*100], [sigma_lim sigma_lim], 'k--');
plot([epl*100 epl*100], [0 sigma_lim], 'r--');
plot(ep(2:3)*100, sg(2:3), 'bo', 'MarkerFaceColor','b');
text(0.2, sigma_y - 12, sprintf('fluencia k*sigma_lim = %.1f MPa', sigma_y));
text(0.2, sigma_lim + 8, sprintf('sigma_lim = %.1f MPa', sigma_lim));
text(epl*100 + 0.1, 40, 'eps_pl = 5 %  (UC = 1)', 'Color','r');
text(2.2, (sigma_y+sigma_lim)/2 - 8, sprintf('H = %.0f MPa', H), 'Color','b');
grid on; xlabel('deformacion plastica eps_pl [%]'); ylabel('sigma_eq [MPa]');
title('Ley bilineal de la soldadura (AISC LRFD, E70XX)');
axis([0 6.5 0 260]);
snapnow;

%"< 5. Tensión equivalente y chequeo AISC J2.4 por elemento
%' Criterio del solver con WTYPE 2 (AISC con aumento direccional), verificado corriendo:
% #deq sigma_eq = sqrt(sigma_perp^2 + tau_perp^2 + tau_par^2)/(1 + 0.5*sin(theta)^1.5) @@(5.1)
%' θ = ángulo entre la fuerza del tramo y el eje del cordón. Chequeo (WeldCalculatorAISC),
%' tramo por tramo, no por cordón entero:
% #deq F_nw = 0.6*F_EXX*(1 + 0.5*sin(theta)^1.5) @@(5.2)
% #deq A_e = L_e*a @@(5.3)
% #deq R_n = phi*F_nw*A_e @@(5.4)
% #deq F_e = A_e*sqrt(tau_par^2 + tau_perp^2 + sigma_perp^2) @@(5.5)
% #deq UC = F_e/R_n @@(5.6)
%' Por qué el 1 + 0.5 sin<sup>1.5</sup>θ: un filete cargado de través (θ = 90°) resiste 1.5 veces
%' lo que uno cargado a lo largo (θ = 0°). Como el solver fluye en 0.75 σ_lim, un tramo
%' plastificado marca UC ≈ 0.75 y llega a UC = 1 cuando ε_pl = 5 %.

%"< 6. Mini FEM: pared unida a la placa base por un filete doble
%' Mismo modelo que el mini deck de k2fem64 (gen_weld_deck.py): placa base 200×200×40
%' empotrada en los 4 bordes, pared 200×150×10 en x = 100, filete doble a = 5 mm en 10
%' tramos por lado (L_e = 20 mm), tracción Fz = 100 kN en la cabeza de la pared.
%' Modelo de esta hoja (propio, más simple que la cáscara de k2fem):
%'  • placa: Mindlin Q4 (w, θ<sub>x</sub>, θ<sub>y</sub>), 10×10, flexión 2×2 Gauss, corte 1 punto;
%'  • pared: membrana Q4 (u<sub>y</sub>, u<sub>z</sub>), 10×10;
%'  • cada tramo = enlace entre el PROMEDIO de sus 2 nudos de pared y el PROMEDIO de sus
%'    2 nudos de placa (eso es el RBE3 con w_i = 1), rigidez de la tira de espesor a.
%' Rigidez de la tira de un tramo: área L<sub>e</sub>·a sobre un largo a (el a se cancela).
%' Un desplazamiento vertical se reparte a 45°: la mitad estira la tira (E/(1−ν²)) y la
%' otra mitad la corta (κG). Con los 2 lados del filete doble:
% #deq k_v = 2*1/2*(E/(1 - nu^2) + kappa*G)*L_e @@(6.1)
% #deq k_y = 2*G*L_e @@(6.2)
%' k<sub>y</sub> es la rigidez al corte longitudinal (a lo largo del cordón). Valores:
E = 210000;  nu = 0.3;  G = E/(2*(1 + nu));  kappa = 5/6;
t_p = 40;  t_w = 10;  L_e = 20;  F_z = 100000;
ne = 10;  hx = 20;  hz = 15;
k_v = 2*1/2*(E/(1 - nu^2) + kappa*G)*L_e    %' N/mm
k_y = 2*G*L_e                               %' N/mm
kv = k_v;  ky = k_y;  Le = L_e;  Fz = F_z;  kap = kappa;  tp = t_p;  tw = t_w;

% ---- ensamble (placa: gdl 3 por nudo; pared: 2 por nudo) --------------------
Np = (ne+1)^2;  off = 3*Np;  N = off + 2*(ne+1)^2;
K = zeros(N, N);  F = zeros(N, 1);
Kp = ke_mindlin(E, nu, tp, hx, kap);
Kw = ke_membrana(E, nu, tw, hx, hz);
for i = 0:ne-1
  for j = 0:ne-1
    nd = [pn(i,j,ne) pn(i+1,j,ne) pn(i+1,j+1,ne) pn(i,j+1,ne)];
    d = reshape([3*nd-2; 3*nd-1; 3*nd], 1, 12);
    K(d,d) = K(d,d) + Kp;
  end
end
for j = 0:ne-1
  for k = 0:ne-1
    nd = [pn(j,k,ne) pn(j+1,k,ne) pn(j+1,k+1,ne) pn(j,k+1,ne)];
    d = off + reshape([2*nd-1; 2*nd], 1, 8);
    K(d,d) = K(d,d) + Kw;
  end
end
% enlaces de soldadura (RBE3 = promedio de 2 nudos)
Tv = zeros(ne, N);  Ty = zeros(ne, N);
for j = 0:ne-1
  Tv(j+1, off + 2*pn(j,0,ne))   = 0.5;   Tv(j+1, off + 2*pn(j+1,0,ne)) = 0.5;
  Tv(j+1, 3*pn(5,j,ne)-2)       = -0.5;  Tv(j+1, 3*pn(5,j+1,ne)-2)     = -0.5;
  Ty(j+1, off + 2*pn(j,0,ne)-1) = 0.5;   Ty(j+1, off + 2*pn(j+1,0,ne)-1) = 0.5;
end
K = K + kv*(Tv'*Tv) + ky*(Ty'*Ty);
% carga en la cabeza de la pared (mitad en los extremos)
for j = 0:ne
  w = 1;  if j == 0 || j == ne, w = 0.5; end
  F(off + 2*pn(j,ne,ne)) = Fz/ne*w;
end
% placa empotrada en los 4 bordes
fix = [];
for i = 0:ne
  for j = 0:ne
    if i == 0 || i == ne || j == 0 || j == ne
      n1 = pn(i,j,ne);  fix = [fix 3*n1-2 3*n1-1 3*n1];
    end
  end
end
free = setdiff(1:N, fix);
U = zeros(N, 1);  U(free) = K(free,free) \ F(free);

% ---- fuerza y tensiones por tramo ---------------------------------------------
Fj = kv*(Tv*U);                % fuerza vertical de cada tramo (2 lados) [N]
Fy = ky*(Ty*U);                % fuerza longitudinal de cada tramo [N]
Ae = Le*a;                     % área efectiva de un tramo [mm2]
F_total = sum(Fj)              %' N (equilibrio: la suma de los 10 tramos devuelve Fz = 100000 N)
%' Por lado, la fuerza F<sub>j</sub>/2 es vertical y se reparte a 45° (sec. 3):
% #deq sigma_perp = tau_perp = F_j/(2*sqrt(2)*A_e) @@(6.3)
% #deq tau_par = F_yj/(2*A_e) @@(6.4)
sp = Fj/(2*sqrt(2)*Ae);
tpar = abs(Fy)/(2*Ae);
fres = sqrt(2*sp.^2 + tpar.^2);
% k2fem64 (mini deck, weld_mini, elementos 201..210): |f| = sqrt(sig_y^2+tau_yz^2+tau_xy^2)
f_k2 = [83.27 59.12 42.27 39.05 36.47 36.47 39.05 42.27 59.12 83.27]';
err = (fres - f_k2)./f_k2*100;
%' Tabla por tramo (tensiones en MPa):
% #plain
fprintf('tramo  sig_perp=tau_perp  tau_par   |f| Hekatan  |f| k2fem64  error %%\n');
for j = 1:ne
  fprintf('%4d   %12.2f   %9.2f   %10.2f   %10.2f   %7.1f\n', j, sp(j), tpar(j), fres(j), f_k2(j), err(j));
end
% #render

f_extremo = fres(1)           %' MPa en el tramo del extremo (k2fem64: 83.3 MPa)
f_centro = fres(5)            %' MPa en el tramo del centro (k2fem64: 36.5 MPa)
err_extremo = err(1)          %' %
err_centro = err(5)           %' %
err_max = max(abs(err))       %' % (peor tramo)

% ---- chequeo AISC J2.4, tramo por tramo (ec. 5.2 a 5.4) ----------------------
UC = zeros(ne, 1);  th = zeros(ne, 1);
for j = 1:ne
  Fe = [tpar(j) sp(j) sp(j)]*Ae;
  th(j) = acos(abs(Fe(1))/norm(Fe));
  Fnw = 0.6*FEXX*(1 + 0.5*sin(th(j))^1.5);
  UC(j) = norm(Fe)/(phi*Fnw*Ae);
end
[UC_max, jc] = max(UC);
UC_max                        %' UC AISC del tramo crítico (k2fem64: 0.270)
theta_c = th(jc)*180/pi       %' ° (ángulo fuerza-eje del tramo crítico)
err_UC = (UC_max - 0.270)/0.270*100   %' %
sigma_eq_c = fres(jc)/(1 + 0.5*sin(th(jc))^1.5)   %' MPa, ec. (5.1)
%' σ<sub>eq</sub> queda bajo la fluencia de @sigma_y MPa → la soldadura sigue ELÁSTICA con 100 kN.

% ---- FIGURA 4: distribucion por tramo ---------------------------------------
yc = (0.5:1:ne-0.5)*Le;
figure('Color','w'); hold on;
bar(yc, fres, 0.6, 'FaceColor',[.3 .5 .9]);
plot(yc, f_k2, 'ro-', 'LineWidth',2, 'MarkerFaceColor','r');
plot([0 200], [1 1]*Fz/(2*sqrt(2)*a*200)*sqrt(2), 'k--');
grid on; xlabel('y a lo largo del cordon [mm]'); ylabel('|f| = sqrt(sp^2+tp^2+tpar^2) [MPa]');
legend('Hekatan Lab (Mindlin + membrana + RBE3)', 'k2fem64 (IDEA, mini deck)', 'uniforme F/(2 a L)');
title(sprintf('Tension por tramo, Fz = 100 kN: extremos %.1f vs %.1f, centro %.1f vs %.1f MPa', ...
      fres(1), f_k2(1), fres(5), f_k2(5)));
snapnow;

%"< Conclusión
%' La distribución NO es uniforme: los tramos de los extremos llevan más del doble que los
%' del centro, porque la placa empotrada es más rígida cerca de sus bordes y la pared tira
%' más por ahí. Con un modelo simple (placa Mindlin + pared membrana + RBE3 promedio) se
%' reproduce k2fem64 en los extremos y el centro (error de pocos %) y el UC AISC (0.270),
%' pero NO nudo a nudo: el 2.º tramo sale ~22 % bajo y τ<sub>∥</sub> en el extremo sale la mitad.
%' Razón honesta: la tira de IDEA es una cáscara con giros y corte transversal, el RBE3
%' también reparte momentos, y la pared de k2fem es cáscara (no membrana). Reproducir IDEA
%' exacto exige su deck real (.nas capturado), no este modelo.

% VERIFICACION (solo Claude):
% print(gcf,'_verif_soldadura.png','-dpng','-r110');

% =============================================================================
function n = pn(i, j, ne)
% numero de nudo (1-based) de la rejilla (ne+1)x(ne+1)
n = i*(ne+1) + j + 1;
end

function Ke = ke_mindlin(E, nu, t, h, kap)
% placa Mindlin Q4 cuadrada h x h: gdl por nudo [w tx ty]
Db = E*t^3/(12*(1 - nu^2))*[1 nu 0; nu 1 0; 0 0 (1-nu)/2];
Ds = kap*E/(2*(1 + nu))*t*eye(2);
g = 1/sqrt(3);  gp = [-g -g; g -g; g g; -g g];
Ke = zeros(12);
for q = 1:4
  [~, dx, dy] = shp(gp(q,1), gp(q,2), h, h);
  B = zeros(3, 12);
  for m = 1:4
    B(1,3*m) = dx(m);  B(2,3*m-1) = -dy(m);  B(3,3*m) = dy(m);  B(3,3*m-1) = -dx(m);
  end
  Ke = Ke + B'*Db*B*(h/2)^2;
end
[Nn, dx, dy] = shp(0, 0, h, h);
B = zeros(2, 12);
for m = 1:4
  B(1,3*m-2) = dx(m);  B(1,3*m) = Nn(m);  B(2,3*m-2) = dy(m);  B(2,3*m-1) = -Nn(m);
end
Ke = Ke + B'*Ds*B*4*(h/2)^2;
end

function Ke = ke_membrana(E, nu, t, h1, h2)
% membrana Q4 rectangular h1 x h2: gdl por nudo [u1 u2]
D = E*t/(1 - nu^2)*[1 nu 0; nu 1 0; 0 0 (1-nu)/2];
g = 1/sqrt(3);  gp = [-g -g; g -g; g g; -g g];
Ke = zeros(8);
for q = 1:4
  [~, dx, dy] = shp(gp(q,1), gp(q,2), h1, h2);
  B = zeros(3, 8);
  for m = 1:4
    B(1,2*m-1) = dx(m);  B(2,2*m) = dy(m);  B(3,2*m-1) = dy(m);  B(3,2*m) = dx(m);
  end
  Ke = Ke + B'*D*B*(h1/2)*(h2/2);
end
end

function [Nn, dx, dy] = shp(xi, et, h1, h2)
Nn = 0.25*[(1-xi)*(1-et) (1+xi)*(1-et) (1+xi)*(1+et) (1-xi)*(1+et)];
dx = 0.25*[-(1-et) (1-et) (1+et) -(1+et)]*2/h1;
dy = 0.25*[-(1-xi) -(1+xi) (1+xi) (1-xi)]*2/h2;
end
