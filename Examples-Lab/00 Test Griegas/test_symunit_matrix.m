u = symunit;
v = [10; 20; 30]*u.kN
e2 = v(2)
mix = [5*u.m; 3*u.kN; 2*u.s]
m2 = mix(2)
sub = v(1:2)
K = [2 1; 1 2]*u.kN/u.m
k11 = K(1,1)
