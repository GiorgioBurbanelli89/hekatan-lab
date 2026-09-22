E=1000; nu=0.3; t=1; nd=3;
xn=[0 1 2 0 1 2 0 1 2]'; yn=[0 0 0 1 1 1 2 2 2]'; xn(5)=1.0; yn(5)=1.0;
ej=[1 2 5 4;2 3 6 5;4 5 8 7;5 6 9 8]; nj=9; ng=nd*nj;
K0=zeros(ng,ng);
for e=1:4
  q=ej(e,:)'; Ke=ke_dkq(xn(q),yn(q),E,nu,t);
  for ic=1:4,for jc=1:4
    i1=nd*(ej(e,ic)-1);i2=nd*(ej(e,jc)-1);
    K0(i1+1:i1+3,i2+1:i2+3)=K0(i1+1:i1+3,i2+1:i2+3)+Ke(nd*(ic-1)+1:nd*ic,nd*(jc-1)+1:nd*jc);
  end,end
end
bnd=[1 2 3 4 6 7 8 9]; wex=0.5*xn(5)^2;
combos=[0 1; 0 -1; 1 0; -1 0];  % [thx_coef*x , thy_coef*x] con dw/dx=x, dw/dy=0
names={'thx=0,thy=x','thx=0,thy=-x','thx=x,thy=0','thx=-x,thy=0'};
for cc=1:4
  K=K0; F=zeros(ng,1); ks=1e12;
  for b=bnd
    w=0.5*xn(b)^2; thx=combos(cc,1)*xn(b); thy=combos(cc,2)*xn(b);
    v=[w thx thy]; dd=[nd*(b-1)+1 nd*(b-1)+2 nd*(b-1)+3];
    for m=1:3, K(dd(m),dd(m))=K(dd(m),dd(m))+ks; F(dd(m))=F(dd(m))+ks*v(m); end
  end
  Z=K\F; w5=Z(nd*4+1);
  fprintf('%-14s : w=%.5f (exacto %.5f) err=%.2e\n', names{cc}, w5, wex, abs(w5-wex));
end
