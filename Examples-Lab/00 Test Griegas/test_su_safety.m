u = symunit;
K = [2 1; 1 2]*u.kN/u.m;
x = [3; 4]*u.m;
prod = K*x
% funcion (posible JIT) con arg con unidad -> debe bailout al interprete
function y = doble(a)
  y = 2*a;
end
r = doble(5*u.kN)
