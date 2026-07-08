; Inno Setup script for Lookout — produces a per-user (no-UAC) installer.
; Compiled by build-installer.ps1, which passes the published folder via /DSourceDir.

#define AppName "Lookout"
#define AppVersion "1.1.0"
#define AppPublisher "Joseph Arnold"

#ifndef SourceDir
  #define SourceDir "..\dist\Lookout-x64"
#endif

[Setup]
AppId={{B7F3A2E1-4C8D-4E9A-9F2B-1A6D3C5E7F90}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher={#AppPublisher}
AppVerName={#AppName} {#AppVersion}
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
UninstallDisplayIcon={app}\Lookout.exe
UninstallDisplayName={#AppName}
OutputDir=..\dist
OutputBaseFilename=Lookout-Setup-{#AppVersion}-x64
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=dialog
SetupIconFile=..\src\Lookout\Assets\lookout.ico

[Tasks]
Name: "startup"; Description: "Start Lookout automatically when I sign in"; Flags: unchecked

[Files]
Source: "{#SourceDir}\*"; DestDir: "{app}"; Flags: recursesubdirs createallsubdirs ignoreversion

[Icons]
Name: "{group}\Lookout"; Filename: "{app}\Lookout.exe"
Name: "{group}\Uninstall Lookout"; Filename: "{uninstallexe}"
Name: "{autostartup}\Lookout"; Filename: "{app}\Lookout.exe"; Tasks: startup

[Run]
Filename: "{app}\Lookout.exe"; Description: "Launch Lookout now"; Flags: nowait postinstall skipifsilent
