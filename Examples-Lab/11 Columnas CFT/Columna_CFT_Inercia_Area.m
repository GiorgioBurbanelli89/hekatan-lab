clc
clear all

% Datos de prueba
Seccion = 'Mixta';

b_c_acero = 300;  % Ancho de la columna en mm
h_c_acero = 300;  % Altura de la columna en mm
t_c_acero = 10;   % Espesor de la pared de la columna en mm
fc = 280;
Ec = 15100 * sqrt(fc);  % Módulo de elasticidad del concreto
Es = 2038901.92;        % Módulo de elasticidad del acero

[A_col_acero, I_col_acero] = I_A_Col_acero(h_c_acero, b_c_acero, t_c_acero, Ec, Es, Seccion)

% Usar format shortG para formato corto sin notación científica
format shortG

% Mostrar resultados con 2 decimales sin notación científica
fprintf("Inercia del acero de la columna: %.2f cm^4\n", I_col_acero);
fprintf("Area del acero de la columna: %.2f cm^4\n", A_col_acero);

    