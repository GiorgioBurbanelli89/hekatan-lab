% Define nod y Y

clc;clear all;
nod = 6;
Y = [0 0 3.5 3.5 3.5 3.5];

% Llama a la función Unique_funcion1 con nod y Y como argumentos
CG = Unique_funcion(nod, Y);

% Puedes mostrar el resultado si lo deseas
disp(CG);


