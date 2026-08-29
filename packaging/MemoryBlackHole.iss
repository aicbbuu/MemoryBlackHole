#define AppName "MemoryBlackHole"
#define AppDisplayName "记忆黑洞"
#define AppVersion "2.1.2"
#define AppPublisher "vicz"
#define AppExeName "MemoryBlackHole.exe"

[Setup]
AppId={{8A6C8B50-6D6D-4D2C-9E3B-7D2C4D3B1A10}
AppName={#AppDisplayName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/
DefaultDirName={localappdata}\Programs\{#AppName}
DefaultGroupName={#AppDisplayName}
OutputDir=..\artifacts\installer
OutputBaseFilename=MemoryBlackHole-Setup-{#AppVersion}-win-x86
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=lowest
SetupIconFile=..\src\MemoryBlackHole\Assets\AppIcon.ico
UninstallDisplayName={#AppDisplayName}
SetupLogging=yes

[Files]
Source: "..\artifacts\publish\win-x86\MemoryBlackHole.exe"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\README.md"; DestDir: "{app}"; Flags: ignoreversion
Source: "..\COPYRIGHT.md"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{group}\{#AppDisplayName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppDisplayName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Tasks]
Name: "desktopicon"; Description: "创建桌面快捷方式"; GroupDescription: "附加快捷方式："

[Run]
Filename: "{app}\{#AppExeName}"; Description: "启动{#AppDisplayName}"; Flags: nowait postinstall skipifsilent
