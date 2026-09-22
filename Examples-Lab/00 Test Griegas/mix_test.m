diary('mix_out.txt'); diary on;
u = symunit;
disp('--- vector con DISTINTAS unidades por elemento ---');
mix = [5*u.m; 3*u.kN; 2*u.s]
disp('--- acceder a un elemento ---');
e2 = mix(2)
disp('--- matriz mixta (rigidez con unidades distintas) ---');
Kmix = [3*u.kN/u.m, 2*u.kN; 2*u.kN, 5*u.kN*u.m]
disp('--- separateUnits sobre vector mixto ---');
try
  [val,unt] = separateUnits(mix);
  val
  unt
catch e
  disp(['separateUnits mixto ERROR: ' e.message]);
end
diary off; exit
