E=24850000; nu=0.2; t=0.25; X=[0;2;2;0]; Y=[0;0;4;4];
D=E*t/(1-nu^2)*[1 nu 0;nu 1 0;0 0 (1-nu)/2]; gam=E/(2*(1+nu));
K3=ke_itw(X,Y,D,gam,t); Kg=ke_itw_g(X,Y,D,gam,t,3);
fprintf('nG=3 vs ke_itw: max diff = %.2e\n', max(max(abs(K3-Kg))));
