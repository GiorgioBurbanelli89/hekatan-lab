diary('cmap_mat_out.txt'); diary on;
try
  run('geo5_colormap_desplazamientos.m');
  saveas(gcf, 'mcmap_geo5.png');
  disp('FIG OK');
catch e
  disp(['ERROR: ' e.message]);
end
diary off; exit
