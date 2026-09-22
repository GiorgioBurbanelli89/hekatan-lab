test2_cantilever;
f = sort(double(get(0,'children')));
set(f(end),'PaperUnits','points','PaperPosition',[0 0 560 420]);
print(f(end),'ml_cantilever','-dpng','-r72');
exit
