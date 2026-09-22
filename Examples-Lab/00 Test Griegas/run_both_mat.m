diary('both_mat_out.txt'); diary on;
try, slab_both; disp('OK'); catch e, disp(['ERROR: ' e.message]); end
diary off; exit
