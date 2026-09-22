c = {'x2','2x','_x','x_2','xsup2','x^2','x-y','x.y','sigma','alpha', ...
     'for','end','pi','sqrtA','raiz','x y','xdotminus','a__b','fprime_c','ñ','café','x$','X_MAX','n123'};
for i=1:numel(c)
  fprintf('%-12s -> %d\n', c{i}, isvarname(c{i}));
end
fprintf('namelengthmax = %d\n', namelengthmax);
