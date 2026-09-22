diary('slabu_mat_out.txt'); diary on;
try, slab_timing_u; disp('OK'); catch e, disp(['ERROR: ' e.message]); end
diary off; exit
