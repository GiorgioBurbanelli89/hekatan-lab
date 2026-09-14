% ============================================================
%  ARMADURA WARREN 2D  —  como SAP2000 (Axial Force Diagram)
%  Mismo ejercicio que se resuelve en MATLAB: b = 8 m, h = 4 m,
%  300 kN en el nudo central inferior. Rigidez directa y los
%  resultados con las tablas y la gráfica que muestra SAP2000.
%  Comprobado contra SAP2000 v24 (el juez): mismas N, reacciones
%  y desplazamientos (ver Armadura_Warren_SAP2000_sap.json).
% ============================================================

% --- Modelo como DATOS ---------------------------------------------------
b = 8;   h = 4;                       % paño (m) y altura (m)
E = 200e6;   A = 0.01;                % kN/m2 , m2  (acero, A = 100 cm2)
% NUDOS: [ x   y   restrX restrY    Fx     Fy ]   (m , kN)
NUDOS = [ 0     0     1     1      0      0        % 1  apoyo fijo
          b     0     0     0      0   -300        % 2  carga
          2*b   0     0     1      0      0        % 3  apoyo móvil
          b/2   h     0     0      0      0        % 4
          3*b/2 h     0     0      0      0 ];     % 5
% BARRAS: [ nudoI  nudoJ ]
BARRAS = [ 1 2      % cordón inferior
           2 3
           4 5      % cordón superior
           1 4      % diagonales
           4 2
           2 5
           5 3 ];
nN = size(NUDOS,1);   nE = size(BARRAS,1);

% --- Rigidez global  K = Σ (EA/L) · T' · [1 -1; -1 1] · T ---------------
K = zeros(2*nN);
L = zeros(nE,1);  cs = zeros(nE,2);
for e = 1:nE
    i = BARRAS(e,1);  j = BARRAS(e,2);
    dx = NUDOS(j,1) - NUDOS(i,1);   dy = NUDOS(j,2) - NUDOS(i,2);
    L(e) = sqrt(dx^2 + dy^2);   c = dx/L(e);   s = dy/L(e);   cs(e,:) = [c s];
    k = E*A/L(e) * [ c*c  c*s -c*c -c*s
                     c*s  s*s -c*s -s*s
                    -c*c -c*s  c*c  c*s
                    -c*s -s*s  c*s  s*s ];
    g = [2*i-1 2*i 2*j-1 2*j];
    K(g,g) = K(g,g) + k;
end

% --- Solución  K u = F ----------------------------------------------------
F = reshape(NUDOS(:,5:6)', [], 1);
R = reshape(NUDOS(:,3:4)', [], 1);
libres = find(R == 0);   fijos = find(R == 1);
u = zeros(2*nN,1);
u(libres) = K(libres,libres) \ F(libres);
Reac = K*u - F;                                   % reacciones (solo en GDL fijos)

% --- Fuerza axial por barra (+ tracción, − compresión) -------------------
N = zeros(nE,1);
for e = 1:nE
    i = BARRAS(e,1);  j = BARRAS(e,2);  c = cs(e,1);  s = cs(e,2);
    N(e) = E*A/L(e) * ( c*(u(2*j-1)-u(2*i-1)) + s*(u(2*j)-u(2*i)) );
end

% --- Tablas como SAP2000 ---------------------------------------------------
disp('TABLE:  Joint Displacements   (DEAD, m)')
fprintf('  Joint        U1              U3\n');
for n = 1:nN
    fprintf('  %3d   %13.6e   %13.6e\n', n, u(2*n-1), u(2*n));
end
disp('TABLE:  Joint Reactions   (DEAD, kN)')
fprintf('  Joint        F1              F3\n');
for n = 1:nN
    if any(NUDOS(n,3:4))
        fprintf('  %3d   %13.4f   %13.4f\n', n, Reac(2*n-1), Reac(2*n));
    end
end
disp('TABLE:  Element Forces - Frames   (DEAD, kN)')
fprintf('  Frame   I-J       P (kN)     L (m)\n');
for e = 1:nE
    fprintf('  %3d    %d-%d   %11.3f   %7.3f\n', e, BARRAS(e,1), BARRAS(e,2), N(e), L(e));
end
fprintf('Suma de reacciones verticales = %.3f kN  (carga = %.3f kN)\n', sum(Reac(2:2:end)), -sum(F(2:2:end)));

% --- Gráfica 1: Axial Force Diagram (DEAD) [kN], estilo SAP2000 ------------
% Banda perpendicular a cada barra, alto proporcional a |N|, con rayas como SAP.
% Azul = tracción, rojo = compresión (colores de SAP2000 para P).
figure; hold on; axis equal; axis off;
title('Axial Force Diagram (DEAD)  [kN]');
Nmax = max(abs(N));   esc = 0.10*b / Nmax;           % alto de banda: 10 % del paño para Nmax
for e = 1:nE
    i = BARRAS(e,1);  j = BARRAS(e,2);  c = cs(e,1);  s = cs(e,2);
    xi = NUDOS(i,1);  yi = NUDOS(i,2);  xj = NUDOS(j,1);  yj = NUDOS(j,2);
    nx = -s;  ny = c;                                  % normal a la barra
    d  = N(e)*esc;
    if N(e) >= 0,  col = [0.15 0.25 0.85];  else,  col = [0.85 0.15 0.15];  end
    X = [xi, xj, xj + nx*d, xi + nx*d];
    Y = [yi, yj, yj + ny*d, yi + ny*d];
    patch(X, Y, col, 'FaceColor', 'none', 'EdgeColor', col, 'LineWidth', 1.4);
    nr = 14;                                            % rayas perpendiculares
    for r = 0:nr
        t = r/nr;
        px = xi + t*(xj - xi);   py = yi + t*(yj - yi);
        plot([px, px + nx*d], [py, py + ny*d], '-', 'Color', col, 'LineWidth', 0.8);
    end
    % etiqueta a mitad de la barra
    xm = (xi + xj)/2;   ym = (yi + yj)/2;
    if abs(s) < 1e-9
        % cordones: siempre POR ENCIMA de la barra (fuera de la banda si es de tracción)
        ym = yi + max(d*ny, 0) + 0.45;
    else
        % diagonales: del lado CONTRARIO a la banda, así no pisa las bandas vecinas
        sd = sign(d + 1e-12);
        xm = xm - nx*sd*0.55;   ym = ym - ny*sd*0.55;
    end
    text(xm, ym, sprintf('%.3f', N(e)), 'Color', col, 'FontWeight', 'bold', ...
         'HorizontalAlignment', 'center', 'Rotation', atan2(s, c)*180/pi);
end
for e = 1:nE                                          % la armadura encima
    i = BARRAS(e,1);  j = BARRAS(e,2);
    plot(NUDOS([i j],1), NUDOS([i j],2), '-', 'Color', [0.2 0.2 0.55], 'LineWidth', 2.2);
end
plot(NUDOS(:,1), NUDOS(:,2), 'o', 'Color', [0.2 0.2 0.55], 'MarkerFaceColor', 'w', 'MarkerSize', 6);
% apoyos: fijo (triángulo) y móvil (triángulo + círculo), verdes como SAP2000
for n = find(NUDOS(:,4) == 1)'
    x0 = NUDOS(n,1);  y0 = NUDOS(n,2);  a = 0.55;
    patch([x0, x0 - a, x0 + a], [y0, y0 - 1.1*a, y0 - 1.1*a], [0.2 0.75 0.2], 'FaceColor', 'none', 'EdgeColor', [0.2 0.75 0.2], 'LineWidth', 1.5);
    if NUDOS(n,3) == 0
        tt = linspace(0, 2*pi, 30);
        plot(x0 + 0.18*cos(tt), y0 - 1.1*a - 0.2 + 0.18*sin(tt), '-', 'Color', [0.2 0.75 0.2], 'LineWidth', 1.2);
    end
    text(x0, y0 - 2.1, sprintf('R = %.1f kN', Reac(2*n)), 'Color', [0.1 0.5 0.1], 'HorizontalAlignment', 'center', 'FontWeight', 'bold');
end
% carga
for n = find(NUDOS(:,6) ~= 0)'
    x0 = NUDOS(n,1);  y0 = NUDOS(n,2);
    plot([x0 x0], [y0 + 2.2, y0 + 0.15], '-', 'Color', [0.9 0.5 0], 'LineWidth', 2);
    patch([x0, x0 - 0.25, x0 + 0.25], [y0 + 0.15, y0 + 0.65, y0 + 0.65], [0.9 0.5 0], 'EdgeColor', [0.9 0.5 0]);
    text(x0, y0 + 2.55, sprintf('%.0f kN', -NUDOS(n,6)), 'Color', [0.9 0.5 0], 'FontWeight', 'bold', 'HorizontalAlignment', 'center');
end
for n = 1:nN
    text(NUDOS(n,1) - 0.35, NUDOS(n,2) + 0.35, sprintf('%d', n), 'Color', [0.35 0.35 0.35]);
end

% --- Gráfica 2: deformada (DEAD), como «Deformed Shape» de SAP2000 --------
figure; hold on; axis equal; axis off;
fac = 0.08*b / max(abs(u));
title(sprintf('Deformed Shape (DEAD)  —  escala x%.0f ,  |u|max = %.3f mm', fac, 1000*max(abs(u))));
for e = 1:nE
    i = BARRAS(e,1);  j = BARRAS(e,2);
    plot(NUDOS([i j],1), NUDOS([i j],2), ':', 'Color', [0.6 0.6 0.6], 'LineWidth', 1.2);
    plot(NUDOS([i j],1) + fac*u([2*i-1 2*j-1]), NUDOS([i j],2) + fac*u([2*i 2*j]), '-', 'Color', [0.8 0.2 0.5], 'LineWidth', 2);
end
plot(NUDOS(:,1) + fac*u(1:2:end), NUDOS(:,2) + fac*u(2:2:end), 'o', 'Color', [0.8 0.2 0.5], 'MarkerFaceColor', 'w', 'MarkerSize', 6);
