E=1000; nu=0.3; t=1; X=[0;1;1;0]; Y=[0;0;1;1];
K=ke_dkq_v2(X,Y,E,nu,t); ev=sort(eig((K+K')/2));
nz=sum(abs(ev)<1e-6*max(abs(ev)));
fprintf('v2 (a,d +): modos cero = %d\n', nz);
fprintf('  ev1..4: %.3e %.3e %.3e %.3e\n', ev(1),ev(2),ev(3),ev(4));
