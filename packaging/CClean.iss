#ifndef AppVersion
  #define AppVersion "0.2.0-preview.2"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\publish\win-x64"
#endif

[Setup]
AppId={{09E71B53-3267-46CF-ACAB-E4C51E3C6B1C}
AppName=CClean Internal
AppVersion={#AppVersion}
VersionInfoVersion={#Copy(AppVersion, 1, Pos('-', AppVersion) > 0 ? Pos('-', AppVersion) - 1 : Len(AppVersion))}
AppPublisher=CClean Project
DefaultDirName={localappdata}\Programs\CClean Internal
DefaultGroupName=CClean Internal
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.22000
OutputDir=..\artifacts\packages
OutputBaseFilename=CClean-{#AppVersion}-internal-win-x64
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UninstallDisplayIcon={app}\CClean.App.exe
CloseApplications=no
RestartApplications=no
SetupLogging=yes
InfoBeforeFile=internal-notice.txt

[Languages]
Name: "chinesesimp"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\docs\user-guide.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\docs\privacy.md"; DestDir: "{app}\docs"; Flags: ignoreversion
Source: "..\docs\testing\release-acceptance.md"; DestDir: "{app}\docs\testing"; Flags: ignoreversion
Source: "..\docs\testing\personal-acceptance.md"; DestDir: "{app}\docs\testing"; Flags: ignoreversion
Source: "..\docs\testing\storage-home-acceptance.md"; DestDir: "{app}\docs\testing"; Flags: ignoreversion
Source: "..\docs\audit\implemented-features.md"; DestDir: "{app}\docs\audit"; Flags: ignoreversion
Source: "..\docs\testing\reliability-017.md"; DestDir: "{app}\docs\testing"; Flags: ignoreversion
Source: "..\docs\testing\preview-020.md"; DestDir: "{app}\docs\testing"; Flags: ignoreversion

[Icons]
Name: "{group}\CClean Internal"; Filename: "{app}\CClean.App.exe"

; Deliberately no UninstallDelete entries: monitored roots and runtime data are never removed.
