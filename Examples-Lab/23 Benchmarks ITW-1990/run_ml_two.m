for nm = {'test1_simplebeam','test3_cook'}
  close all; run(nm{1});
  f=sort(double(get(0,'children')));
  set(f(end),'PaperUnits','points','PaperPosition',[0 0 560 420]);
  print(f(end), ['ml_' nm{1}], '-dpng','-r72');
end
exit
