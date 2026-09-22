u = symunit;
K = [3*u.kN/u.m, 2*u.kN; 2*u.kN, 5*u.kN*u.m];
f = [12*u.kN; 19*u.kN*u.m];
d = K\f
% verificacion: K*d debe reproducir f
fcheck = K*d
