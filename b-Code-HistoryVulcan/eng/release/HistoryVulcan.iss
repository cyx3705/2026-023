; HistoryVulcan host installer — compiled by Pack-HistoryVulcanInstaller.ps1
; Do not compile this file by hand unless SourceDir / OutputDir define defines are set.

#ifndef MyAppVersion
  #error MyAppVersion must be defined (e.g. /DMyAppVersion=3.3.1)
#endif
#ifndef SourceDir
  #error SourceDir must be defined
#endif
#ifndef OutputDir
  #error OutputDir must be defined
#endif
#ifndef OutputBase
  #define OutputBase "HistoryVulcan-" + MyAppVersion + "-Setup"
#endif

#define MyAppName "HistoryVulcan"
#define MyAppPublisher "OneHistory"
#define MyAppURL "https://github.com/cyx3705/OneHistory"
#define MyAppExeName "HistoryVulcan.exe"

[Setup]
AppId={{A7C3E9F1-4B2D-4E8A-9C1F-6D5B8A0E2F34}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppVerName={#MyAppName} {#MyAppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppURL}
AppSupportURL={#MyAppURL}
DefaultDirName={autopf}\{#MyAppName}
DefaultGroupName={#MyAppName}
DisableProgramGroupPage=yes
OutputDir={#OutputDir}
OutputBaseFilename={#OutputBase}
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
UninstallDisplayIcon={app}\host\{#MyAppExeName}
VersionInfoVersion={#MyAppVersion}.0
VersionInfoProductVersion={#MyAppVersion}
SetupLogging=yes
CloseApplications=yes
RestartApplications=no
MinVersion=10.0

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional icons:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#MyAppName}"; Filename: "{app}\host\{#MyAppExeName}"; WorkingDir: "{app}\host"
Name: "{group}\Uninstall {#MyAppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#MyAppName}"; Filename: "{app}\host\{#MyAppExeName}"; WorkingDir: "{app}\host"; Tasks: desktopicon

[Run]
Filename: "{app}\host\{#MyAppExeName}"; Description: "Launch {#MyAppName}"; Flags: nowait postinstall skipifsilent

[Code]
function InitializeSetup(): Boolean;
begin
  Result := True;
end;
