diary('slab_mat_out.txt'); diary on;
try, slab_timing; disp('OK'); catch e, disp(['ERROR: ' e.message]); end
diary off; exit
