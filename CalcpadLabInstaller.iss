; Inno Setup Script para Calcpad-Lab
; Genera un instalador setup.exe

#define MyAppName "Hekatan Lab"
#define MyAppVersion "1.3.13"
#define MyAppPublisher "Jorge Burbano"
#define MyAppURL "https://github.com/GiorgioBurbanelli89/hekatan-lab"
#define MyAppExeName "HekatanLab.exe"
#define MyAppPublishDir "C:\Users\j-b-j\Desktop\CalcpadLab-Installer\CalcpadLab"
; Publish del CLI (CalcpadLabCli.exe). Se instala EN LA MISMA carpeta que la app:
; comparte el runtime self-contained, mkl\ (910 MB) y Calcpad.Core.dll, asi que
; suma ~2 MB en vez de duplicar 1.1 GB.
#define MyCliPublishDir "C:\Users\j-b-j\Desktop\CalcpadLab-Installer\CalcpadLabCli"
#define MyCliExeName "CalcpadLabCli.exe"

[Setup]
AppId={{A7B8C9D0-1E2F-3A4B-5C6D-7E8F9A0B1C2D}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
DefaultDirName={autopf}\Hekatan Lab
UsePreviousAppDir=no
DefaultGroupName=Hekatan Lab
AllowNoIcons=yes
LicenseFile=LICENSE
OutputDir=.\Installer
OutputBaseFilename=Hekatan-Lab-Setup-{#MyAppVersion}
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesInstallIn64BitMode=x64
PrivilegesRequired=admin
UninstallDisplayIcon={app}\{#MyAppExeName}
SetupIconFile=Symbolic.Wpf\resources\calcpad.ico

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"
Name: "spanish"; MessagesFile: "compiler:Languages\Spanish.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"
Name: "fileassoc_m"; Description: "Asociar archivos .m (MATLAB) con Hekatan Lab"; GroupDescription: "Asociaciones de archivo:"

[InstallDelete]
; Limpiar Examples viejos antes de copiar — evita que queden .m huérfanos de
; versiones anteriores (ej. fem_demo.m híbrido roto).
Type: filesandordirs; Name: "{app}\Examples"

[Files]
; Application files — self-contained .NET 10 publish
Source: "{#MyAppPublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs

; Examples — scripts .m (MATLAB) bundleados en {app}\Examples.
; La app al primer arranque los copia a {userdocs}\Calcpad-Lab\Examples del usuario real
; (evita el problema de install elevado donde {userdocs} apunta al admin).
Source: "Examples-Lab\*"; DestDir: "{app}\Examples"; Flags: ignoreversion recursesubdirs skipifsourcedoesntexist

; CLI headless (CalcpadLabCli.exe): .m -> HTML / TXT / .tex, y --png para capturar
; el render. Solo sus archivos propios: el runtime, mkl\ y las DLL del motor ya
; vienen con la app. El doc\ de la app le sirve igual (template mas nuevo).
Source: "{#MyCliPublishDir}\{#MyCliExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyCliPublishDir}\CalcpadLabCli.dll"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyCliPublishDir}\CalcpadLabCli.deps.json"; DestDir: "{app}"; Flags: ignoreversion
Source: "{#MyCliPublishDir}\CalcpadLabCli.runtimeconfig.json"; DestDir: "{app}"; Flags: ignoreversion
; render_html.py: lo usa --png para renderizar el HTML en Chromium headless.
Source: "{#MyCliPublishDir}\doc\render_html.py"; DestDir: "{app}\doc"; Flags: ignoreversion
; Settings.xml lo lee el CLI de su carpeta; sin el intentaria crearlo en Program Files.
Source: "{#MyCliPublishDir}\Settings.xml"; DestDir: "{app}"; Flags: ignoreversion onlyifdoesntexist

; Documentation
Source: "README.md"; DestDir: "{app}"; Flags: ignoreversion isreadme
Source: "LICENSE"; DestDir: "{app}"; Flags: ignoreversion

[Dirs]
; (La app crea {userdocs}\Calcpad-Lab\Examples en el primer arranque del usuario real.)

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Examples (bundleados)"; Filename: "{app}\Examples"
Name: "{group}\{cm:ProgramOnTheWeb,{#MyAppName}}"; Filename: "{#MyAppURL}"
Name: "{group}\{cm:UninstallProgram,{#MyAppName}}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
; .m file association
Root: HKA; Subkey: "Software\Classes\.m\OpenWithProgids"; ValueType: string; ValueName: "CalcpadLab.MFile"; ValueData: ""; Flags: uninsdeletevalue; Tasks: fileassoc_m
Root: HKA; Subkey: "Software\Classes\CalcpadLab.MFile"; ValueType: string; ValueName: ""; ValueData: "Hekatan Lab MATLAB Document"; Flags: uninsdeletekey; Tasks: fileassoc_m
Root: HKA; Subkey: "Software\Classes\CalcpadLab.MFile\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"; Tasks: fileassoc_m
Root: HKA; Subkey: "Software\Classes\CalcpadLab.MFile\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: fileassoc_m
; Hekatan Lab SOLO asocia .m (MATLAB). .cpd es de la VM/Suite (Calcpad), NO de Lab.

[Run]
Filename: "{app}\{#MyAppExeName}"; Description: "Iniciar Hekatan Lab"; Flags: nowait postinstall skipifsilent
