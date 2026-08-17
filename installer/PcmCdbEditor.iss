#define AppName "PCM CDB Editor"
#define AppPublisher "Peter537"
#define AppExeName "PcmCdbEditor.exe"
#define AppProgId "Peter537.PcmCdbEditor.cdb"

#ifndef AppVersion
  #define AppVersion "0.1.0"
#endif

#ifndef SourceDir
  #error SourceDir must point to the verified win-x64 payload.
#endif

#ifndef OutputDir
  #error OutputDir must point to the release package directory.
#endif

[Setup]
AppId={{81DD1E0D-52AA-4E5F-BC2C-B884A3293B0F}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
AppPublisherURL=https://github.com/Peter537/pcm-cdb-editor
AppSupportURL=https://github.com/Peter537/pcm-cdb-editor/security
AppUpdatesURL=https://github.com/Peter537/pcm-cdb-editor/releases
DefaultDirName={localappdata}\Programs\PcmCdbEditor
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.19041
OutputDir={#OutputDir}
OutputBaseFilename=PcmCdbEditor-{#AppVersion}-win-x64-setup
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern
CloseApplications=yes
RestartApplications=no
ChangesAssociations=yes
UsePreviousAppDir=yes
UsePreviousLanguage=yes
UsePreviousTasks=yes
SetupLogging=yes
Uninstallable=yes
UninstallDisplayName={#AppName}
UninstallDisplayIcon={app}\{#AppExeName}
LicenseFile={#SourceDir}\LICENSE
VersionInfoCompany={#AppPublisher}
VersionInfoDescription={#AppName} per-user installer
VersionInfoProductName={#AppName}
VersionInfoProductVersion={#AppVersion}
VersionInfoVersion={#AppVersion}

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a &desktop shortcut"; GroupDescription: "Additional shortcuts:"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExeName}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExeName}"; Tasks: desktopicon

[Registry]
; Register an Open With handler without forcibly replacing an existing user default.
Root: HKCU; Subkey: "Software\Classes\.cdb\OpenWithProgids"; ValueType: string; ValueName: "{#AppProgId}"; ValueData: ""; Flags: uninsdeletevalue
Root: HKCU; Subkey: "Software\Classes\{#AppProgId}"; ValueType: string; ValueName: ""; ValueData: "PCM CDB database"; Flags: uninsdeletekey
Root: HKCU; Subkey: "Software\Classes\{#AppProgId}"; ValueType: string; ValueName: "FriendlyTypeName"; ValueData: "PCM CDB database"
Root: HKCU; Subkey: "Software\Classes\{#AppProgId}\DefaultIcon"; ValueType: string; ValueName: ""; ValueData: "{app}\{#AppExeName},0"
Root: HKCU; Subkey: "Software\Classes\{#AppProgId}\shell\open"; ValueType: string; ValueName: ""; ValueData: "Open with PCM CDB Editor"
Root: HKCU; Subkey: "Software\Classes\{#AppProgId}\shell\open\command"; ValueType: string; ValueName: ""; ValueData: """{app}\{#AppExeName}"" ""%1"""

; User settings, backups, and recoverable sessions live below
; %LOCALAPPDATA%\PcmCdbEditor and are intentionally outside {app}. There is no
; [UninstallDelete] section, so uninstall and upgrade do not remove that data.
