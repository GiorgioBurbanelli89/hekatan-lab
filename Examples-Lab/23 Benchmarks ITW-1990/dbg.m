E=30000; nu=0.25; P=40; l=48; h=12; n=3;
D=E/(1-nu^2)*[1 nu 0; nu 1 0; 0 0 (1-nu)/2]; gam=E/(2*(1+nu));
xtop=[0 12 24 36 48]; xbot=[0 16 20 28 48];
xi=[xbot xtop]'; yi=[zeros(5,1); h*ones(5,1)];
ej=zeros(4,4); for i=1:4, ej(i,:)=[i i+1 5+i+1 5+i]; end
% chequear jacobianos (area con signo de cada quad)
for e=1:4
  q=ej(e,:); X=xi(q); Y=yi(q);
  A=0.5*((X(1)*Y(2)-X(2)*Y(1))+(X(2)*Y(3)-X(3)*Y(2))+(X(3)*Y(4)-X(4)*Y(3))+(X(4)*Y(1)-X(1)*Y(4)));
  fprintf('elem %d area con signo = %.2f\n', e, A);
end
K=assemble_itw(xi,yi,ej,D,gam,1);
for j=1:10, if xi(j)==0, d=n*(j-1); K(d+1,d+1)=K(d+1,d+1)+1e12; K(d+2,d+2)=K(d+2,d+2)+1e12; K(d+3,d+3)=K(d+3,d+3)+1e12; end, end
F=zeros(30,1); for j=1:10, if xi(j)==l, F(n*(j-1)+2)=F(n*(j-1)+2)+P/2; end, end
Z=K\F;
fprintf('uy nodo5 (48,0) = %.4f\n', Z(n*4+2));
fprintf('uy nodo10(48,12)= %.4f\n', Z(n*9+2));
