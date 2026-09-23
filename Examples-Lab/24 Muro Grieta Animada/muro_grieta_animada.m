function muro_grieta_animada()
% =====================================================================================
%  MURO DE CORTE - la GRIETA crece EN VIVO mientras itera (drawnow por paso de carga)
% -------------------------------------------------------------------------------------
%  Mismo modelo que "Muro afinado Abaqus vs Python" (Hekatan Py, ejemplo 14):
%  muro 3 m x 2 m x 0.2 m, 54x36 Q4 plane stress, bordes confinados (3*ft), carga
%  lateral en el tope hasta ux = 4.04 mm (67 % de Abaqus), 30 pasos, visc 0.004.
%  Dano a traccion en la direccion principal + regularizacion viscosa.
%  En cada paso: malla DEFORMADA (x74) coloreada por el dano d (0..0.9, jet).
%  N, mm, MPa.
% =====================================================================================
W=3000; Hh=2000; t=200; nx=54; ny=36; E=25000; nu=0.2; ft=2.6;
be=0.15*W; umax=4.04; nstep=30; visc=0.004; sf=74;
D0=E/(1-nu^2)*[1 nu 0; nu 1 0; 0 0 (1-nu)/2];
gp=[-1 -1;1 -1;1 1;-1 1]/sqrt(3);
nid=@(i,j) j*(nx+1)+i+1;
XY=zeros((nx+1)*(ny+1),2);
for j=0:ny, for i=0:nx, XY(nid(i,j),:)=[W*i/nx, Hh*j/ny]; end; end
els=zeros(nx*ny,4); e=0;
for j=0:ny-1, for i=0:nx-1, e=e+1; els(e,:)=[nid(i,j) nid(i+1,j) nid(i+1,j+1) nid(i,j+1)]; end; end
NE=size(els,1); NN=size(XY,1); ng=2*NN;
Bc=cell(NE,1); Ke0=cell(NE,1); Dof=zeros(NE,8); Vole=zeros(NE,1); cent=zeros(NE,2);
for ee=1:NE
  co=XY(els(ee,:),:); Ke=zeros(8); vv=0;
  for q=1:4
    dN=shp(gp(q,1),gp(q,2)); J=dN*co; dJ=abs(det(J)); dNx=J\dN;
    B=Bm(dNx); Ke=Ke+B'*D0*B*dJ*t; vv=vv+dJ*t;
  end
  dN0=shp(0,0); Bc{ee}=Bm((dN0*co)\dN0); Ke0{ee}=Ke; Vole(ee)=vv; cent(ee,:)=mean(co,1);
  d=zeros(1,8); for a=1:4, d(2*a-1:2*a)=[2*els(ee,a)-1 2*els(ee,a)]; end; Dof(ee,:)=d;
end
I=zeros(NE*64,1); Jj=I; V0=I;
for ee=1:NE, d=Dof(ee,:); Ke=Ke0{ee};
  I((ee-1)*64+1:ee*64)=repmat(d',8,1); Jj((ee-1)*64+1:ee*64)=reshape(repmat(d,8,1),[],1); V0((ee-1)*64+1:ee*64)=Ke(:);
end
borde=(cent(:,1)<be)|(cent(:,1)>W-be); ftE=ft*ones(NE,1); ftE(borde)=3*ft;   % bordes confinados
base=[]; top=[]; for i=0:nx, base=[base nid(i,0)]; top=[top nid(i,ny)]; end
fix=[]; for n=base, fix=[fix 2*n-1 2*n]; end
for n=top, fix=[fix 2*n]; end
imp=2*top-1; fix=[fix imp]; free=setdiff(1:ng,fix);
cx=[0 0.0006 0.002 0.006]; cs=[2.6 0.5 0.1 0.05];          % ablandamiento a traccion
dv=zeros(NE,1); epl=zeros(NE,3); epq=zeros(NE,1); U=zeros(ng,1); dts=1/nstep;

figure('Color','w','Position',[60 60 1000 700]); colormap(jet(256));
for st=1:nstep                                              % --- cada paso = un cuadro ---
  ux=umax*st/nstep;
  for it=1:10
    Vv=V0.*repelem(1-dv,64); K=sparse(I,Jj,Vv,ng,ng);
    Fpl=zeros(ng,1);
    for ee=1:NE, Fpl(Dof(ee,:))=Fpl(Dof(ee,:))+(1-dv(ee))*Vole(ee)*(Bc{ee}'*(D0*epl(ee,:)')); end
    U=zeros(ng,1); U(imp)=ux; R=Fpl-K*U; U(free)=K(free,free)\R(free);
    dold=dv;
    for ee=1:NE
      sb=D0*(Bc{ee}*U(Dof(ee,:)')-epl(ee,:)');
      savg=(sb(1)+sb(2))/2; rad=sqrt(((sb(1)-sb(2))/2)^2+sb(3)^2); s1=savg+rad;
      th=0.5*atan2(2*sb(3), sb(1)-sb(2));
      if s1>ftE(ee)
        c=cos(th); s2=sin(th); m=[c*c; s2*s2; 2*c*s2]; dl=(s1-ftE(ee))/(m'*D0*m);
        if dl>0, epl(ee,:)=epl(ee,:)+(dl*m)'; epq(ee)=epq(ee)+dl; end
      end
      if epq(ee)<=0, db=0;
      elseif epq(ee)<0.006, db=min(0.95,1-interp1(cx,cs,epq(ee))/ft);
      else db=min(0.95,1-0.05/ft); end
      dv(ee)=(dv(ee)+(dts/visc)*db)/(1+dts/visc);
    end
    if max(abs(dv-dold))<3e-3, break; end
  end
  % ---- dibujo del paso: malla deformada coloreada por el dano ----
  XYdef=XY+sf*[U(1:2:end) U(2:2:end)];
  cla; hold on;
  patch('Vertices',XYdef,'Faces',els,'FaceVertexCData',dv,'FaceColor','flat','EdgeColor',[.8 .8 .8],'LineWidth',0.2);
  plot([0 W W 0 0],[0 0 Hh Hh 0],'Color',[.35 .35 .35],'LineWidth',1.5);   % contorno sin deformar
  axis equal; axis([-100 W+400 -100 Hh+100]); caxis([0 0.9]); colorbar;
  title(sprintf('Grieta del muro - paso %d/%d  u_x=%.2f mm  d_{max}=%.3f  agrietados=%d', ...
        st, nstep, ux, max(dv), sum(dv>0.5)));
  drawnow;                                                  % el cuadro reemplaza al anterior
end
fprintf('FIN: d_max=%.3f, elementos agrietados=%d (Hekatan Py da 0.950 y 252)\n',max(dv),sum(dv>0.5));
end
function dN=shp(xi,et)
dN=0.25*[-(1-et) (1-et) (1+et) -(1+et); -(1-xi) -(1+xi) (1+xi) (1-xi)];
end
function B=Bm(dNx)
B=zeros(3,8);
for a=1:4, B(1,2*a-1)=dNx(1,a); B(2,2*a)=dNx(2,a); B(3,2*a-1)=dNx(2,a); B(3,2*a)=dNx(1,a); end
end
