; Game Price Hunter installer (Inno Setup 6). Built by build\publish.ps1, which passes the version:
;   ISCC /DAppVersion=1.2.3 /DArch=x64 /DPublishDir=<folder with GamePriceHunter.exe> GamePriceHunter.iss
;
; Installs per user into %LOCALAPPDATA%\Programs\Game Price Hunter, so no administrator rights are needed.
; The app updates itself by downloading the next release's setup and running it with
;   /VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /NORESTART /RELAUNCH=1
; which upgrades in place and starts the new version.

#define AppName "Game Price Hunter"
#define AppExe "GamePriceHunter.exe"
#define AppUrl "https://github.com/FLABER87/game-price-hunter"
; Created by the running app (App.xaml.cs); Setup waits for it to disappear before replacing files.
#define AppMutex "GamePriceHunter.Running"

#ifndef Arch
  #define Arch "x64"
#endif
#ifndef PublishDir
  #define PublishDir AddBackslash(SourcePath) + "..\..\artifacts\publish\win-" + Arch
#endif
#ifndef AppVersion
  #define AppVersion GetVersionNumbersString(AddBackslash(PublishDir) + AppExe)
#endif

; The file names must match UpdateChecker.InstallerFileName, which the app downloads.
#if Arch == "x64"
  #define ArchAllowed "x64compatible"
  #define SetupName "GamePriceHunter-Setup"
#elif Arch == "arm64"
  #define ArchAllowed "arm64"
  #define SetupName "GamePriceHunter-Setup-arm64"
#elif Arch == "x86"
  #define ArchAllowed "x86compatible"
  #define SetupName "GamePriceHunter-Setup-x86"
#else
  #error Unknown Arch; use x64, arm64 or x86.
#endif

[Setup]
; Never change AppId: upgrades find the existing installation by it.
AppId={{EE2E60A7-30FC-44AE-A2C8-C9D8670764D3}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppName}
AppPublisherURL={#AppUrl}
AppSupportURL={#AppUrl}/issues
AppUpdatesURL={#AppUrl}/releases
VersionInfoVersion={#AppVersion}
VersionInfoProductName={#AppName}
VersionInfoDescription={#AppName} Setup

PrivilegesRequired=lowest
DefaultDirName={userpf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
ArchitecturesAllowed={#ArchAllowed}
#if Arch != "x86"
ArchitecturesInstallIn64BitMode={#ArchAllowed}
#endif
MinVersion=10.0

; Restart Manager closes a running copy that holds the files; the app restarts itself via /RELAUNCH.
CloseApplications=yes
RestartApplications=no

OutputDir=..\..\artifacts\installer
OutputBaseFilename={#SetupName}
SetupIconFile=..\..\src\GamePriceHunter.App\Assets\AppIcon.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
ShowLanguageDialog=no

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"

[Tasks]
Name: "desktopicon"; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked

[Files]
Source: "{#PublishDir}\{#AppExe}"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "{cm:LaunchProgram,{#AppName}}"; Flags: nowait postinstall skipifsilent; Check: not ShouldRelaunch
Filename: "{app}\{#AppExe}"; Flags: nowait; Check: ShouldRelaunch

[UninstallDelete]
; Cache, logs and downloaded images (AppPaths.CacheDirectory). Settings in %APPDATA% are kept for a reinstall.
Type: filesandordirs; Name: "{localappdata}\GamePriceHunter"

[Code]
// Passed by the app's updater, so the new version starts after a silent upgrade.
function ShouldRelaunch: Boolean;
begin
  Result := ExpandConstant('{param:RELAUNCH|0}') = '1';
end;

function InitializeSetup: Boolean;
var
  Attempts: Integer;
begin
  // The updater closes the app right after starting Setup; give it a few seconds to exit.
  if WizardSilent then
  begin
    Attempts := 0;
    while CheckForMutexes('{#AppMutex}') and (Attempts < 50) do
    begin
      Sleep(200);
      Attempts := Attempts + 1;
    end;
  end;
  Result := True;
end;

function InitializeUninstall: Boolean;
begin
  Result := True;
  if UninstallSilent then
    Exit;
  while Result and CheckForMutexes('{#AppMutex}') do
    Result := MsgBox(FmtMessage(SetupMessage(msgUninstallAppRunningError), ['{#AppName}']), mbError, MB_OKCANCEL) = IDOK;
end;
