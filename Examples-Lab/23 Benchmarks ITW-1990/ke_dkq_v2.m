function K = ke_dkq_v2(X4, Y4, E, nu, t)
% Placa DKQ (Discrete Kirchhoff Quadrilateral, Batoz & Ben Tahar 1982).
% 4 nudos x 3 GDL (w, bx, by) = 12x12, solo FLEXION.
% X4,Y4 = coords de los 4 nudos (columna, en el plano local del elemento).
Db = E*t^3/(12*(1-nu^2)) * [1 nu 0; nu 1 0; 0 0 (1-nu)/2];
% lados: 5=(1,2) 6=(2,3) 7=(3,4) 8=(4,1)
ii = [1 2 3 4];  jj = [2 3 4 1];
a=zeros(8,1); b=a; c=a; d=a; ee=a;
for s = 1:4
    k = 4+s;  I = ii(s);  J = jj(s);
    xij = X4(I)-X4(J);  yij = Y4(I)-Y4(J);  L2 = xij^2+yij^2;
    a(k) = xij/L2;
    b(k) = 0.75*xij*yij/L2;
    c(k) = (0.25*xij^2 - 0.5*yij^2)/L2;
    d(k) = yij/L2;
    ee(k)= (0.25*yij^2 - 0.5*xij^2)/L2;
end
g = 1/sqrt(3);  gp = [-g g];
K = zeros(12,12);
for ig = 1:2
    for jg = 1:2
        xi = gp(ig);  et = gp(jg);
        % derivadas de N respecto a xi, et (corner 1-4, midside 5-8)
        [Nx, Ne] = shp_deriv(xi, et);   % Nx=dN/dxi (8), Ne=dN/det (8)
        % Jacobiano (geometria bilineal de las 4 esquinas)
        Jx = Nx(1)*X4(1)+Nx(2)*X4(2)+Nx(3)*X4(3)+Nx(4)*X4(4);   % dx/dxi
        Jy = Nx(1)*Y4(1)+Nx(2)*Y4(2)+Nx(3)*Y4(3)+Nx(4)*Y4(4);   % dy/dxi
        Kx = Ne(1)*X4(1)+Ne(2)*X4(2)+Ne(3)*X4(3)+Ne(4)*X4(4);   % dx/det
        Ky = Ne(1)*Y4(1)+Ne(2)*Y4(2)+Ne(3)*Y4(3)+Ne(4)*Y4(4);   % dy/det
        detJ = Jx*Ky - Jy*Kx;
        j11 =  Ky/detJ;  j12 = -Jy/detJ;  j21 = -Kx/detJ;  j22 = Jx/detJ;
        % Hx,xi Hx,et Hy,xi Hy,et
        Hxx = Hx_vec(Nx, a, b, c);
        Hxe = Hx_vec(Ne, a, b, c);
        Hyx = Hy_vec(Nx, d, ee, b);
        Hye = Hy_vec(Ne, d, ee, b);
        % derivadas fisicas
        Hx_x = j11*Hxx + j12*Hxe;
        Hx_y = j21*Hxx + j22*Hxe;
        Hy_x = j11*Hyx + j12*Hye;
        Hy_y = j21*Hyx + j22*Hye;
        B = [Hx_x; Hy_y; Hx_y + Hy_x];   % 3x12 (curvaturas)
        K = K + (B'*Db*B)*detJ;          % peso Gauss = 1
    end
end
end

function [Nx, Ne] = shp_deriv(xi, et)
% derivadas de las 8 funciones (4 corner bilineales + 4 midside cuadraticas)
Nx = zeros(8,1);  Ne = zeros(8,1);
Nx(1)=-0.25*(1-et); Nx(2)= 0.25*(1-et); Nx(3)= 0.25*(1+et); Nx(4)=-0.25*(1+et);
Ne(1)=-0.25*(1-xi); Ne(2)=-0.25*(1+xi); Ne(3)= 0.25*(1+xi); Ne(4)= 0.25*(1-xi);
Nx(5)=-xi*(1-et);        Ne(5)=-0.5*(1-xi^2);
Nx(6)= 0.5*(1-et^2);     Ne(6)=-et*(1+xi);
Nx(7)=-xi*(1+et);        Ne(7)= 0.5*(1-xi^2);
Nx(8)=-0.5*(1-et^2);     Ne(8)=-et*(1-xi);
end

function H = Hx_vec(N, a, b, c)
% Hx (o su derivada) con N = valor o derivada de las 8 funciones
H = zeros(1,12);
H(1) = 1.5*(a(5)*N(5) - a(8)*N(8));
H(2) = b(5)*N(5) + b(8)*N(8);
H(3) = N(1) - c(5)*N(5) - c(8)*N(8);
H(4) = 1.5*(a(6)*N(6) - a(5)*N(5));
H(5) = b(6)*N(6) + b(5)*N(5);
H(6) = N(2) - c(6)*N(6) - c(5)*N(5);
H(7) = 1.5*(a(7)*N(7) - a(6)*N(6));
H(8) = b(7)*N(7) + b(6)*N(6);
H(9) = N(3) - c(7)*N(7) - c(6)*N(6);
H(10)= 1.5*(a(8)*N(8) - a(7)*N(7));
H(11)= b(8)*N(8) + b(7)*N(7);
H(12)= N(4) - c(8)*N(8) - c(7)*N(7);
end

function H = Hy_vec(N, d, ee, b)
H = zeros(1,12);
H(1) = 1.5*(d(5)*N(5) - d(8)*N(8));
H(2) = -N(1) + ee(5)*N(5) + ee(8)*N(8);
H(3) = -b(5)*N(5) - b(8)*N(8);
H(4) = 1.5*(d(6)*N(6) - d(5)*N(5));
H(5) = -N(2) + ee(6)*N(6) + ee(5)*N(5);
H(6) = -b(6)*N(6) - b(5)*N(5);
H(7) = 1.5*(d(7)*N(7) - d(6)*N(6));
H(8) = -N(3) + ee(7)*N(7) + ee(6)*N(6);
H(9) = -b(7)*N(7) - b(6)*N(6);
H(10)= 1.5*(d(8)*N(8) - d(7)*N(7));
H(11)= -N(4) + ee(8)*N(8) + ee(7)*N(7);
H(12)= -b(8)*N(8) - b(7)*N(7);
end
