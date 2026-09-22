diary('femK_out.txt'); diary on;
try
  femK_run
catch e
  disp(['ERROR: ' e.message]);
end
diary off; exit
