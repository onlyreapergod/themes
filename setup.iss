; Win11ProSuite - 1-click per-user installer (Inno Setup 6)
; Built automatically by .github/workflows/build.yml

#define MyAppName "Win11ProSuite"
#define MyAppVersion "1.0.0"
#define MyAppExe "Win11ProSuite.exe"

[Setup]
AppId={{B7A3C1E2-5D4F-4C8A-9E21-6F0A8D3B7C55}
AppName={#MyAppName}
AppVersion={#MyAppVersion}
AppPublisher={#MyAppName}
DefaultDirName={localappdata}\Programs\Win11ProSuite
DefaultGroupName={#MyAppName}
PrivilegesRequired=lowest
ArchitecturesAllowed=x64
ArchitecturesInstallIn64BitMode=x64
OutputDir=installer_out
OutputBaseFilename=Win11ProSuite-Setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\{#MyAppExe}
AppMutex=Win11ProSuite.SingleInstance
CloseApplications=yes
RestartApplications=no
; 1-click experience: the only page left is "Install"
DisableStartupPrompt=yes
DisableWelcomePage=yes
DisableDirPage=yes
DisableProgramGroupPage=yes
DisableFinishedPage=yes

[Languages]
Name: "english"; MessagesFile: "compiler:Default.isl"

[Files]
Source: "publish\Win11ProSuite.exe"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\Win11ProSuite"; Filename: "{app}\{#MyAppExe}"
Name: "{autodesktop}\Win11ProSuite"; Filename: "{app}\{#MyAppExe}"

[Run]
; Launches immediately after install (also runs on silent installs)
Filename: "{app}\{#MyAppExe}"; Description: "Launch Win11ProSuite"; Flags: nowait
