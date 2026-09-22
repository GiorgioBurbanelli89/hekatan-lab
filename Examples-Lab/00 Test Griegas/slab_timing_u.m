%% Timing slab FEA CON UNIDADES (symunit) - patron I/O unidades + solve numerico
% #plain
un = symunit;
a_u=6*un.m; b_u=4*un.m; t_u=0.1*un.m; q_u=10*un.kN/un.m^2; E_u=35000*un.MPa; nu=0.15;
val=@(x) double(separateUnits(x));
a=val(a_u); b=val(b_u); t=val(t_u); q=val(q_u); E=val(E_u); E_si=E*1000;
n_a=6; n_b=4; n_e=n_a*n_b; n_j=(n_a+1)*(n_b+1);
a_1=a/n_a; b_1=b/n_b; n_s=2*(n_a+n_b); n_g=4*n_j;
x_j=zeros(n_j,1); y_j=zeros(n_j,1); xv=0; yv=0;
for j=1:n_j, x_j(j)=xv; y_j(j)=yv; yv=yv+b_1; if yv>b+1e-9, yv=0; xv=xv+a_1; end, end
e_j=zeros(n_e,4);
for i_a=1:n_a, for i_b=1:n_b
  e=i_b+n_b*(i_a-1); j=e+i_a-1; e_j(e,1)=j; e_j(e,2)=j+n_b+1; e_j(e,3)=j+n_b+2; e_j(e,4)=j+1;
end, end
s_j=zeros(n_s,1); i_s=0;
for i=1:n_a+1, i_s=i_s+1; s_j(i_s)=(n_b+1)*i-n_b; end
for i=1:n_a+1, i_s=i_s+1; s_j(i_s)=(n_b+1)*i; end
for i=2:n_b, i_s=i_s+1; s_j(i_s)=i; end
for i=2:n_b, i_s=i_s+1; s_j(i_s)=n_a*(n_b+1)+i; end
D=E_si*t^3/(12*(1-nu^2))*[1,nu,0; nu,1,0; 0,0,(1-nu)/2];
gp4=[-0.861136311594053;-0.339981043584856;0.339981043584856;0.861136311594053];
gw4=[0.347854845137454;0.652145154862546;0.652145154862546;0.347854845137454];
gp=(gp4+1)/2; gw=gw4/2; n_gp=4;
for w=1:5, wc=analiza(D,gp,gw,n_gp,a_1,b_1,q,n_e,n_j,n_g,e_j,s_j,x_j,y_j,b,a,n_a,n_b); end
N=50; tic;
for it=1:N
  wc=analiza(D,gp,gw,n_gp,a_1,b_1,q,n_e,n_j,n_g,e_j,s_j,x_j,y_j,b,a,n_a,n_b);
  w_u = (wc*1000)*un.mm;   % <-- salida CON unidad cada iteracion
end
tt=toc;
[wv,~]=separateUnits(w_u);
fprintf('slab FEA CON unidades: %.4f ms/iter  (w=%.4f mm, N=%d)\n', 1000*tt/N, double(wv), N);

function wc = analiza(D,gp,gw,n_gp,a_1,b_1,q,n_e,n_j,n_g,e_j,s_j,x_j,y_j,b,a,n_a,n_b)
  K_e=zeros(16,16); F_e=zeros(16,1);
  for ig=1:n_gp, for jg=1:n_gp
    u=gp(ig); v=gp(jg); wgt=gw(ig)*gw(jg);
    B_e=Bmat(u,v,a_1,b_1); K_e=K_e+B_e.'*D*B_e*a_1*b_1*wgt;
    for jj=1:16, [ix,iy]=bfs_ix(jj); F_e(jj)=F_e(jj)+q*phi(ix,u,a_1)*phi(iy,v,b_1)*a_1*b_1*wgt; end
  end, end
  K=zeros(n_g,n_g); F=zeros(n_g,1);
  for e=1:n_e
    for ni=1:4, ji=e_j(e,ni);
      for nj=1:4, jj=e_j(e,nj);
        for di=1:4, for dj=1:4
          K(4*(ji-1)+di,4*(jj-1)+dj)=K(4*(ji-1)+di,4*(jj-1)+dj)+K_e(4*(ni-1)+di,4*(nj-1)+dj);
        end, end
      end
      for di=1:4, F(4*(ji-1)+di)=F(4*(ji-1)+di)+F_e(4*(ni-1)+di); end
    end
  end
  k_s=1e20;
  for i=1:length(s_j)
    js=s_j(i); g=4*(js-1)+1; K(g,g)=K(g,g)+k_s;
    if abs(y_j(js))<1e-9||abs(y_j(js)-b)<1e-9, K(g+2,g+2)=K(g+2,g+2)+k_s; end
    if abs(x_j(js))<1e-9||abs(x_j(js)-a)<1e-9, K(g+1,g+1)=K(g+1,g+1)+k_s; end
  end
  Z=K\F; cc=n_a/2+1; cr=n_b/2+1; cj=(cc-1)*(n_b+1)+cr; wc=Z(4*(cj-1)+1);
end
function v=phi(k,u,L)
  if k==1, v=1-u^2*(3-2*u); elseif k==2, v=u*L*(1-u*(2-u)); elseif k==3, v=u^2*(3-2*u); elseif k==4, v=u^2*L*(-1+u); else, v=0; end
end
function v=phi_dd(k,u,L)
  if k==1, v=-6/L^2+12*u/L^2; elseif k==2, v=(-4+6*u)/L; elseif k==3, v=6/L^2-12*u/L^2; elseif k==4, v=(-2+6*u)/L; else, v=0; end
end
function v=phi_d(k,u,L)
  if k==1, v=-6*u/L+6*u^2/L; elseif k==2, v=1-4*u+3*u^2; elseif k==3, v=6*u/L-6*u^2/L; elseif k==4, v=-2*u+3*u^2; else, v=0; end
end
function [ix,iy]=bfs_ix(j)
  node=floor((j-1)/4)+1; sub=mod(j-1,4)+1;
  if node==1, ixw=1; iyw=1; elseif node==2, ixw=3; iyw=1; elseif node==3, ixw=3; iyw=3; else, ixw=1; iyw=3; end
  if sub==1, ix=ixw; iy=iyw; elseif sub==2, ix=ixw; iy=iyw+1; elseif sub==3, ix=ixw+1; iy=iyw; else, ix=ixw+1; iy=iyw+1; end
end
function Bm=Bmat(u,v,a1,b1)
  Bm=zeros(3,16);
  for j=1:16, [ix,iy]=bfs_ix(j);
    Bm(1,j)=phi_dd(ix,u,a1)*phi(iy,v,b1); Bm(2,j)=phi(ix,u,a1)*phi_dd(iy,v,b1); Bm(3,j)=2*phi_d(ix,u,a1)*phi_d(iy,v,b1);
  end
end
