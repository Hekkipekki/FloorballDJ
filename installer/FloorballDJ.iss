#define MyAppName "FloorballDJ"
#define MyAppPublisher "FloorballDJ"
#define MyAppUrl "https://floorballdj.netlify.app"

#ifndef AppVersion
  #define AppVersion "0.40.0-beta.13"
#endif
#ifndef VersionInfoVersion
  #define VersionInfoVersion "0.40.0.0"
#endif
#ifndef PublishDir
  #define PublishDir "..\artifacts\installer\0.40.0-beta\app"
#endif
#ifndef InstallerOutputDir
  #define InstallerOutputDir "..\artifacts\installer\0.40.0-beta"
#endif
#ifndef SignEnabled
  #define SignEnabled 0
#endif

[Setup]
AppId={{BF2F770D-263B-4AE8-8B55-81B4EECE81B4}
AppName={#MyAppName}
AppVersion={#AppVersion}
AppVerName={#MyAppName} {#AppVersion}
AppPublisher={#MyAppPublisher}
AppPublisherURL={#MyAppUrl}
AppSupportURL={#MyAppUrl}
AppUpdatesURL={#MyAppUrl}
DefaultDirName={autopf}\FloorballDJ
DefaultGroupName=FloorballDJ
DisableProgramGroupPage=yes
PrivilegesRequired=admin
PrivilegesRequiredOverridesAllowed=dialog commandline
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#InstallerOutputDir}
OutputBaseFilename=FloorballDJ-Setup
SetupIconFile=..\src\FloorballDJ\Assets\FloorballDJ-AppIcon.ico
UninstallDisplayIcon={app}\FloorballDJ.exe
Compression=lzma2/max
SolidCompression=yes
WizardStyle=modern dynamic windows11
CloseApplications=yes
RestartApplications=no
SetupLogging=yes
; Återanvänd installationsplatsen så att tidigare betaversioner uppgraderas
; i stället för att lämnas kvar som en separat kopia.
UsePreviousAppDir=yes
VersionInfoVersion={#VersionInfoVersion}
VersionInfoCompany={#MyAppPublisher}
VersionInfoDescription=Installationsprogram för FloorballDJ
VersionInfoProductName={#MyAppName}
VersionInfoProductVersion={#VersionInfoVersion}
#if SignEnabled == "1"
SignTool=FloorballDJSign
SignedUninstaller=yes
#else
SignedUninstaller=no
#endif

[Languages]
Name: "swedish"; MessagesFile: "compiler:Languages\Swedish.isl"
Name: "english"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Skapa en genväg på skrivbordet"; GroupDescription: "Genvägar:"; Flags: unchecked

[Files]
Source: "{#PublishDir}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\FloorballDJ"; Filename: "{app}\FloorballDJ.exe"; WorkingDir: "{app}"
Name: "{autodesktop}\FloorballDJ"; Filename: "{app}\FloorballDJ.exe"; WorkingDir: "{app}"; Tasks: desktopicon

[Run]
Filename: "{app}\FloorballDJ.exe"; Description: "Starta FloorballDJ"; Flags: nowait postinstall skipifsilent
