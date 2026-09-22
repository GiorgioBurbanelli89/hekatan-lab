cd('C:\Users\j-b-j\Documents\Hekatan Calc 1.0.0\hekatan-lab');
rf = 'C:\Users\j-b-j\AppData\Local\Temp\claude\C--Users-j-b-j-Documents-Hekatan-Calc-1-0-0\df542e56-7f61-4c59-9b9c-18a9da05a420\scratchpad\dump8_matlab.txt';
fid = fopen(rf, 'w');
try
  dump8;                 % corre dump8.m (que llama frame_fiber; de la misma carpeta)
  fprintf(fid, 'MATLAB R2017a OK: dump8 corrio. size(M) = [%d %d]\n', size(M,1), size(M,2));
catch e
  fprintf(fid, 'MATLAB R2017a ERROR: %s\n', e.message);
end
fclose(fid);
exit
