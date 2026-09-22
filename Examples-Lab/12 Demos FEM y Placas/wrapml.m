try
  eval(fileread('demo_fem_visual.m'));
  hs=findobj('Type','figure');
  for k=1:numel(hs)
    set(hs(k),'Visible','off','Position',[100 100 560 420],'PaperPositionMode','auto','Color','w');
    print(hs(k),'-dpng','-r96',sprintf('demo_fem_visual_ml%d.png',numel(hs)-k+1));
  end
catch e; disp(e.message); end
