% Prueba 1: ^^ como nombre de variable
try
  eval('x^^2 = 5;');
  fprintf('CARET_OK: x^^2 = 5 funciono\n');
catch e
  fprintf('CARET_FALLA: %s\n', e.message);
end
% Prueba 2: sup token (nombre valido)
try
  xsup2 = 5;
  dsup2f = 7;
  fprintf('SUP_OK: xsup2=%d dsup2f=%d\n', xsup2, dsup2f);
catch e
  fprintf('SUP_FALLA: %s\n', e.message);
end
