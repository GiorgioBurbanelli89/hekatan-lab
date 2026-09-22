%% Dibujar el talud con el cursor (ginput) — el MISMO .m en MATLAB 2017a y Hekatan Lab
%' <h3>Dibuja el perfil del talud con el cursor</h3>
%' <hr/>
%' En <b>MATLAB 2017a</b>: clic en la figura para colocar cada vértice, Enter para terminar.
%' En <b>Hekatan Lab</b>: dibuja en el lienzo (con snap a rejilla y a vértice, ángulo y
%' pendiente en vivo) y pulsa <b>Enviar al script</b>.
figure; axis([0 40 -22 0]); hold on; grid on;
xlabel('x [m]'); ylabel('z [m]'); title('Talud');

[x, z] = ginput;          % <-- captura de coordenadas con el cursor (nativo de 2017a)
P = [x, z];
%' Vértices capturados  P = [x  z]:
P

if size(P,1) >= 2
    %-- Dibujar el perfil dibujado
    plot(P(:,1), P(:,2), '-o', 'Color', [0.08 0.38 0.66], 'LineWidth', 2, ...
         'MarkerFaceColor', [0.08 0.38 0.66], 'MarkerSize', 6);
    n = size(P,1);
    %' Número de vértices dibujados: @{n}
    %-- Longitud total del perfil (suma de segmentos)
    Ltot = sum(hypot(diff(P(:,1)), diff(P(:,2))));
    %" Longitud del talud dibujado = @{Ltot} m
    %'
    %' Con esta geometría (los vértices P) siguen los demás pasos: suelos → malla T6 →
    %' ensamblaje → SRM, exactamente como en el flujo [Topo] de GEO5.
else
    %' <i>(Dibuja al menos 2 vértices y pulsa «Enviar al script».)</i>
end
