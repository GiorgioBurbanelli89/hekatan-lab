eval(fileread('Plot_Basico_Seno.m'));
set(gcf,'Visible','off','PaperPositionMode','auto');
print(gcf,'-dpng','-r96','seno_matlab.png');
