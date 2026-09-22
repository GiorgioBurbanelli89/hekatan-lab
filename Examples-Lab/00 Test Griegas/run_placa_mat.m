diary('placa_mat_out.txt'); diary on;
try
  run('Placa_Rectangular_Unidades.m');
  for f = findall(0,'Type','figure')'
    saveas(f, sprintf('mplaca_fig%d.png', f.Number));
  end
  disp('FIGURAS GUARDADAS OK');
catch e
  disp(['ERROR: ' e.message]);
end
diary off; exit
