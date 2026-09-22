syms a b c x v v0 t A r M S sigma L w E I delta
tic; x_12 = solve(a*x^2 + b*x + c, x); t1 = toc*1000;
tic; t_desp = solve(v0 + a*t - v, t); t2 = toc*1000;
tic; r_desp = solve(pi*r^2 - A, r); t3 = toc*1000;
tic; S_desp = solve(sigma*S - M, S); t4 = toc*1000;
tic; I_desp = solve(384*E*delta*I - 5*w*L^4, I); t5 = toc*1000;
fprintf('MATLAB solve ms: cuad=%.2f cinem=%.2f circ=%.2f flex=%.2f defl=%.2f\n', t1,t2,t3,t4,t5);
