tests = {'test0_patch','test1_simplebeam','test2_cantilever','test3_cook'};
for k=1:numel(tests)
  close all; run(tests{k});
  f = sort(double(get(0,'children')));
  for i=1:numel(f)
    set(f(i),'PaperUnits','points','PaperPosition',[0 0 560 420]);
    print(f(i), sprintf('ml_%s_%d',tests{k},i), '-dpng','-r72');
  end
end
exit
