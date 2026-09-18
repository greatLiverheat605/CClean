#define AppVersion "0.1.2"
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif

[Setup]
AppId={{09E71B53-3267-46CF-ACAB-E4C51E3C6B1C}
AppName=CClean Internal
AppVersion={#AppVersion}
AppPublisher=CClean Project
DefaultDirName={localappdata}\Programs\CClean Internal
DefaultGroupName=CClean Internal
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir=..\artifacts\packages
OutputBaseFilename=CClean-0.1.2-internal-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\CClean.App.exe
CloseApplications=no
RestartApplications=no
SetupLogging=yes
InfoBeforeFile=internal-notice.txt

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\docs\user-guide.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\docs\privacy.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\docs\testing\release-acceptance.md"; DestDir: "{app}\docs\testing"; Flags: ignoreversion
Source: "..\docs\testing\personal-acceptance.md"; DestDir: "{app}\docs\testing"; Flags: ignoreversion
Source: "..\docs\testing\storage-home-acceptance.md"; DestDir: "{app}\docs\testing"; Flags: ignoreversion

[Icons]
Name: "{group}\CClean Internal"; Filename: "{app}\CClean.App.exe"

; Deliberately no UninstallDelete entries: monitored roots and runtime data are never removed.
