diary('vec_out.txt'); diary on;
u = symunit;
disp('--- vector columna con unidades ---');
v = [10;20;30]*u.kN
disp('--- matriz con unidades ---');
K = [2 1;1 2]*u.kN/u.m
disp('--- suma elemento a elemento ---');
sv = v + 5*u.kN
disp('--- simplify para combinar ---');
sv2 = simplify(v + 5*u.kN)
disp('--- separateUnits de un vector ---');
[val,unt] = separateUnits(v);
val
disp(class(val));
disp('--- funciones disponibles en R2017a ---');
disp(['exist unitConvert: ' num2str(exist('unitConvert'))]);
disp(['exist separateUnits: ' num2str(exist('separateUnits'))]);
disp(['exist checkUnits: ' num2str(exist('checkUnits'))]);
disp(['exist newUnit: ' num2str(exist('newUnit'))]);
diary off; exit
