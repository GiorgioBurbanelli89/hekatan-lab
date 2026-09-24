% #plain
% CANARIO 5 — semantica de VALOR y asignacion indexada 1-D en el JIT.
% Motivo (24-sep-2026, port de ShellMITC4): dentro de una funcion compilada por el JIT
% `b = d` compartia el arreglo con d y un `d(i) = ...` posterior cambiaba b (LovelyEig
% daba autovalores mal). Y en un for, `v(a:b) = escalar` o `v(k:end) = x` no escribia.
% Valores esperados = los de MATLAB (semantica documentada de copia al asignar).

function r = f_alias(d)
  b = d;
  d(1) = 99;
  r = b(1);
end
function r = f_alias_loc()
  d = [1 2 3];
  b = d;
  d(2) = 50;
  r = b(2);
end
function r = f_param(d)
  b = d;
  b(1) = 9;
  r = d(1);
end
function r = f_loop(d)
  b = zeros(1,3);
  for k = 1:2
    b = d;
    d(k) = -1;
  end
  r = b(1) + b(2);
end
function r = f_struct()
  s.a = [1 2 3];
  t = s;
  s.a(1) = 8;
  r = t.a(1);
end

disp(['CHECK alias_arg ' num2str(f_alias([1 2 3]))]);
disp(['CHECK alias_loc ' num2str(f_alias_loc())]);
x0 = [1 2 3];
q = f_param(x0);
disp(['CHECK param_caller ' num2str(q) ' ' num2str(x0(1))]);
disp(['CHECK loop_alias ' num2str(f_loop([1 2 3]))]);
disp(['CHECK struct_alias ' num2str(f_struct())]);
p = [1 2 3];
for k = 1:1
  pp = p; p(1) = 5;
end
disp(['CHECK for_top_alias ' num2str(pp(1))]);

v = zeros(1,6);
for k = 1:1
  v(2:4) = 5;
end
disp(['CHECK for_esc ' num2str(v)]);
v = zeros(1,6);
for k = 1:2
  a = 3*k-2; v(a:a+2) = [1 2 3]*k;
end
disp(['CHECK for_vec ' num2str(v)]);
w = zeros(6,1);
for k = 1:2
  w(3*k-2:3*k) = k;
end
disp(['CHECK for_col ' num2str(w')]);
disp(['CHECK for_col_forma ' num2str(size(w))]);
g = zeros(1,3);
for k = 1:2
  g(k:end) = k;
end
disp(['CHECK for_end ' num2str(g)]);
h = [];
for k = 1:2
  h(2*k-1:2*k) = [k k];
end
disp(['CHECK for_grow ' num2str(h)]);
g2 = 1:6; s = 0;
for k = 1:2
  t = g2(k:end); s = s + sum(t);
end
disp(['CHECK gather_end ' num2str(s)]);
u = zeros(1,5); u(2:end) = 4;
disp(['CHECK top_end ' num2str(u)]);
