#define MyAppName      "Aurora Player"
#define MyAppVersion   "1.2.0"
#define MyAppPublisher "Виталий Николаевич (vitalikkontr)"
#define MyAppExeName   "AuroraPlayer.exe"
#define MyAppURL       "https://github.com/vitalikkontr"

[Setup]
AppId={{A1B2C3D4-E5F6-7890-ABCD-EF1234567890}}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
AppUpdatesURL={#MyAppURL}
AppCopyright=© 2026 Виталий Николаевич (vitalikkontr)

DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}

OutputDir=Output
OutputBaseFilename=AuroraPlayerSetup
SetupIconFile=C:\Users\vital\source\repos\Aurora Player\app.ico

Compression=lzma2
SolidCompression=yes
WizardStyle=modern

UninstallDisplayIcon={app}\{#MyAppExeName}

PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

DisableProgramGroupPage=yes
ChangesAssociations=yes

LanguageDetectionMethod=uilanguage

; Не запускать установщик после компиляции
CreateAppDir=yes
AlwaysShowComponentsList=no

[Languages]
Name: "russian"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "Создать ярлык на рабочем столе"; GroupDescription: "Дополнительные задачи:"
Name: "contextmenu_files"; Description: "Открывать музыкальные файлы через Aurora Player"; GroupDescription: "Интеграция с проводником:"; Flags: checkedonce
Name: "contextmenu_folder"; Description: "Открывать папки через Aurora Player"; GroupDescription: "Интеграция с проводником:"; Flags: checkedonce

[Files]
Source: "C:\Users\vital\source\repos\Aurora Player\app.ico"; DestDir: "{app}"; Flags: ignoreversion
Source: "C:\Users\vital\source\repos\Aurora Player\bin\Release\net8.0-windows7.0\win-x64\publish\{#MyAppExeName}"; DestDir: "{app}"; Flags: ignoreversion
Source: "C:\Users\vital\source\repos\Aurora Player\bin\Release\net8.0-windows7.0\win-x64\publish\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"
Name: "{group}\Удалить Aurora Player"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\{#MyAppExeName}"; Tasks: desktopicon

[Registry]
Root: HKLM; Subkey: "Software\Classes\AuroraPlayer.AudioFile"; ValueType: string; ValueName: ""; ValueData: "Аудиофайл Aurora Player"; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\AuroraPlayer.AudioFile\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#MyAppExeName},0"
Root: HKLM; Subkey: "Software\Classes\AuroraPlayer.AudioFile\shell\open"; ValueType: string; ValueName: ""; ValueData: "Открыть"
Root: HKLM; Subkey: "Software\Classes\AuroraPlayer.AudioFile\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""

Root: HKLM; Subkey: "Software\Classes\.mp3\OpenWithProgids"; ValueType: string; ValueName: "AuroraPlayer.AudioFile"; ValueData: ""; Tasks: contextmenu_files; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\.flac\OpenWithProgids"; ValueType: string; ValueName: "AuroraPlayer.AudioFile"; ValueData: ""; Tasks: contextmenu_files; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\.wav\OpenWithProgids"; ValueType: string; ValueName: "AuroraPlayer.AudioFile"; ValueData: ""; Tasks: contextmenu_files; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\.ogg\OpenWithProgids"; ValueType: string; ValueName: "AuroraPlayer.AudioFile"; ValueData: ""; Tasks: contextmenu_files; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\.opus\OpenWithProgids"; ValueType: string; ValueName: "AuroraPlayer.AudioFile"; ValueData: ""; Tasks: contextmenu_files; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\.m4a\OpenWithProgids"; ValueType: string; ValueName: "AuroraPlayer.AudioFile"; ValueData: ""; Tasks: contextmenu_files; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\.aac\OpenWithProgids"; ValueType: string; ValueName: "AuroraPlayer.AudioFile"; ValueData: ""; Tasks: contextmenu_files; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\.wma\OpenWithProgids"; ValueType: string; ValueName: "AuroraPlayer.AudioFile"; ValueData: ""; Tasks: contextmenu_files; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\.ape\OpenWithProgids"; ValueType: string; ValueName: "AuroraPlayer.AudioFile"; ValueData: ""; Tasks: contextmenu_files; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\.mp4\OpenWithProgids"; ValueType: string; ValueName: "AuroraPlayer.AudioFile"; ValueData: ""; Tasks: contextmenu_files; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\.wv\OpenWithProgids"; ValueType: string; ValueName: "AuroraPlayer.AudioFile"; ValueData: ""; Tasks: contextmenu_files; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\.aiff\OpenWithProgids"; ValueType: string; ValueName: "AuroraPlayer.AudioFile"; ValueData: ""; Tasks: contextmenu_files; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\.aif\OpenWithProgids"; ValueType: string; ValueName: "AuroraPlayer.AudioFile"; ValueData: ""; Tasks: contextmenu_files; Flags: uninsdeletevalue
Root: HKLM; Subkey: "Software\Classes\.m4b\OpenWithProgids"; ValueType: string; ValueName: "AuroraPlayer.AudioFile"; ValueData: ""; Tasks: contextmenu_files; Flags: uninsdeletevalue

Root: HKLM; Subkey: "Software\Classes\Directory\shell\AuroraPlayer"; ValueType: string; ValueName: ""; ValueData: "Открыть в Aurora Player"; Tasks: contextmenu_folder; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\Directory\shell\AuroraPlayer"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#MyAppExeName},0"; Tasks: contextmenu_folder
Root: HKLM; Subkey: "Software\Classes\Directory\shell\AuroraPlayer\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%1"""; Tasks: contextmenu_folder

Root: HKLM; Subkey: "Software\Classes\Directory\Background\shell\AuroraPlayer"; ValueType: string; ValueName: ""; ValueData: "Открыть в Aurora Player"; Tasks: contextmenu_folder; Flags: uninsdeletekey
Root: HKLM; Subkey: "Software\Classes\Directory\Background\shell\AuroraPlayer"; ValueType: string; ValueName: "Icon"; ValueData: "{app}\{#MyAppExeName},0"; Tasks: contextmenu_folder
Root: HKLM; Subkey: "Software\Classes\Directory\Background\shell\AuroraPlayer\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#MyAppExeName}"" ""%V"""; Tasks: contextmenu_folder
