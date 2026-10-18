; Game Price Hunter installer (Inno Setup 7). Built by build\publish.ps1, which passes the version:
;   ISCC /DAppVersion=1.2.3 /DArch=x64 /DPublishDir=<folder with GamePriceHunter.exe> GamePriceHunter.iss
;
; Installs per user into %LOCALAPPDATA%\Programs\Game Price Hunter, so no administrator rights are needed.
; The app updates itself by downloading the next release's setup and running it with
;   /VERYSILENT /SUPPRESSMSGBOXES /CLOSEAPPLICATIONS /NORESTART /RELAUNCH=1
; which upgrades in place and starts the new version.

#define AppName "Game Price Hunter"
#define AppExe "GamePriceHunter.exe"
#define AppUrl "https://github.com/FLABER87/game-price-hunter"
; Created by the running app (SingleInstance.cs); Setup waits for it to disappear before replacing files.
#define AppMutex "GamePriceHunter.Running"

; [Code] uses the Windows API types (HWND, HDC, THandle) that Inno Setup 7 added. 7.1 is the oldest version
; build\publish.ps1 can recognize (ISCC --version) and the one CI builds with.
#if Ver < EncodeVer(7, 1, 0)
  #error Inno Setup 7.1 or later is required: winget install --id JRSoftware.InnoSetup.7 -e
#endif

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
; The oldest Windows .NET 10 runs on: Windows 10 1607. Newer features (Windows 11 corners, snap layouts,
; caption colors, ICU names) are optional and fall back on their own.
MinVersion=10.0.14393

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

; The app's languages (Loc.Languages), all with official translations that ship with Inno Setup. Setup picks
; the one of the Windows display language, as the app does, and English when there is none.
[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"
Name: "de"; MessagesFile: "compiler:Languages\German.isl"
Name: "es"; MessagesFile: "compiler:Languages\Spanish.isl"
Name: "fr"; MessagesFile: "compiler:Languages\French.isl"
Name: "it"; MessagesFile: "compiler:Languages\Italian.isl"
Name: "pl"; MessagesFile: "compiler:Languages\Polish.isl"
Name: "ptbr"; MessagesFile: "compiler:Languages\BrazilianPortuguese.isl"
Name: "tr"; MessagesFile: "compiler:Languages\Turkish.isl"
Name: "ru"; MessagesFile: "compiler:Languages\Russian.isl"
Name: "uk"; MessagesFile: "compiler:Languages\Ukrainian.isl"
Name: "zhhans"; MessagesFile: "compiler:Languages\ChineseSimplified.isl"
Name: "ja"; MessagesFile: "compiler:Languages\Japanese.isl"
Name: "ko"; MessagesFile: "compiler:Languages\Korean.isl"

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
const
  // TNewCheckListBox indents its items by one cell of the classic OBM_CHECKBOXES bitmap, 13 pixels at any DPI
  ClassicCheckWidth = 13;
  BP_CHECKBOX = 3;
  CBS_UNCHECKEDNORMAL = 1;
  TS_TRUE = 1;

type
  TPartSize = record
    cx, cy: LONG;
  end;

function OpenThemeData(Wnd: HWND; ClassList: String): THandle;
  external 'OpenThemeData@uxtheme.dll stdcall';
function CloseThemeData(Theme: THandle): LONG;
  external 'CloseThemeData@uxtheme.dll stdcall';
function GetThemePartSize(Theme: THandle; DC: HDC; PartId, StateId: Integer; Rect: NativeInt; SizeType: Integer; var Size: TPartSize): LONG;
  external 'GetThemePartSize@uxtheme.dll stdcall';
function GetDC(Wnd: HWND): HDC;
  external 'GetDC@user32.dll stdcall';
function ReleaseDC(Wnd: HWND; DC: HDC): Integer;
  external 'ReleaseDC@user32.dll stdcall';

// The task list and the "Launch" check box on the last page draw the themed check box at its real size
// (40 pixels at 300%) but leave room for 13 pixels only, so at high DPI the box slides left under the edge
// of the list and only a sliver of it shows. Still so in Inno Setup 7.1.0. A larger Offset moves the box
// back into view and the text along with it.
procedure FitCheckBoxes(List: TNewCheckListBox);
var
  Theme: THandle;
  DC: HDC;
  Size: TPartSize;
begin
  // Without a theme the box is drawn at the 13 pixels it has room for
  Theme := OpenThemeData(List.Handle, 'Button');
  if Theme = 0 then
    Exit;
  DC := GetDC(List.Handle);
  try
    if (GetThemePartSize(Theme, DC, BP_CHECKBOX, CBS_UNCHECKEDNORMAL, 0, TS_TRUE, Size) = 0) and (Size.cx > ClassicCheckWidth) then
      List.Offset := List.Offset + Size.cx - ClassicCheckWidth;
  finally
    ReleaseDC(List.Handle, DC);
    CloseThemeData(Theme);
  end;
end;

procedure InitializeWizard;
begin
  FitCheckBoxes(WizardForm.TasksList);
  FitCheckBoxes(WizardForm.RunList);
end;

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
