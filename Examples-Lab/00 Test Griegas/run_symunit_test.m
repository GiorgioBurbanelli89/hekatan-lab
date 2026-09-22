diary('symunit_out.txt'); diary on;
try
  symunit_MATLAB_2017a
catch e
  disp(['ERROR: ' e.message]);
end
diary off;
exit
