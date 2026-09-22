Muro_Acople_ITW;
f=sort(double(get(0,'children')));
for i=1:numel(f)
  set(f(i),'PaperUnits','points','PaperPosition',[0 0 560 420]);
  print(f(i), sprintf('matlab_acople_%d',i), '-dpng','-r72');
end
exit
