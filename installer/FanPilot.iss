; Inno Setup script for FanPilot. Build with scripts\build.ps1, which passes /DAppVersion from
; Directory.Build.props.

#define AppName "FanPilot"
#ifndef AppVersion
  #define AppVersion "0.0.0-dev"
#endif
#define AppExe "FanPilot.exe"
#define ServiceName "FanPilot"
; Must match Program.AppUserModelId so the window groups under the taskbar pin.
#define AppUserModelId "MarceloGoluboff.FanPilot"

[Setup]
AppId={{0B1C93ED-7E8D-4604-88FE-52F373040439}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Marcelo Goluboff
AppPublisherURL=https://github.com/MarceloGoluboff/FanPilot
AppSupportURL=https://github.com/MarceloGoluboff/FanPilot/issues
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
; ASUS's AsusWinIO64.dll is x64 only; x64os also keeps it off ARM64 Windows.
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.17763
OutputDir=..\dist
OutputBaseFilename=FanPilot-Setup-{#AppVersion}
SetupIconFile=..\src\FanPilot\Assets\fan.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
LicenseFile=..\LICENSE
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
; The exe plus WPF's native DLLs, which ship beside it rather than self-extracting to a temp folder.
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "{#AppUserModelId}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "{#AppUserModelId}"; Tasks: desktopicon

[Registry]
; Tray icon at sign-in for every user; each user can turn it off in FanPilot's settings.
; The service runs regardless.
Root: HKLM; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"" --tray"; Flags: uninsdeletevalue

[Run]
; Creates or updates the LocalSystem service and starts it.
Filename: "{app}\{#AppExe}"; Parameters: "--install-service"; StatusMsg: "Starting the FanPilot service..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExe}"; Description: "Open FanPilot"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
; Stopping the service hands the fans back to ASUS. The taskkill filter skips session 0 so it can
; only close windows, never kill the service before it has restored the fans.
Filename: "{sys}\net.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"
Filename: "{sys}\taskkill.exe"; Parameters: "/f /fi ""SESSION ne 0"" /im {#AppExe}"; Flags: runhidden waituntilterminated; RunOnceId: "StopApp"

[Messages]
FinishedLabel=Setup has finished installing FanPilot.%n%nFan control runs as a background service and starts with Windows. To pin the app: open Start, right-click FanPilot and choose Pin to taskbar.

[Code]
function AsusDriverPresent(): Boolean;
var
  FindRec: TFindRec;
begin
  // The ASUS System Control Interface driver package carries AsusWinIO64.dll.
  Result := FindFirst(ExpandConstant('{sys}\DriverStore\FileRepository\asussci*'), FindRec);
  if Result then FindClose(FindRec);
end;

function InitializeSetup(): Boolean;
begin
  Result := True;
  if not AsusDriverPresent() then
    Result := SuppressibleMsgBox(
      'The ASUS System Control Interface driver was not found. FanPilot needs it to control the fans; ' +
      'it is installed with MyASUS on ASUS laptops.' + #13#10#13#10 +
      'Install FanPilot anyway?', mbConfirmation, MB_YESNO, IDYES) = IDYES;
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  ResultCode: Integer;
begin
  // Upgrades: stop the service gracefully (it restores ASUS control), then close any windows.
  // Session 0 is excluded so a service that failed to stop is never killed mid-restore.
  Exec(ExpandConstant('{sys}\net.exe'), 'stop {#ServiceName}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/f /fi "SESSION ne 0" /im {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Result := '';
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  UserDir, ServiceDir: String;
begin
  if CurUninstallStep = usPostUninstall then
  begin
    UserDir := ExpandConstant('{userappdata}\{#AppName}');
    // The service's data folder: LocalSystem's local app data.
    ServiceDir := ExpandConstant('{sys}\config\systemprofile\AppData\Local\{#AppName}');
    if (DirExists(UserDir) or DirExists(ServiceDir)) and
       (SuppressibleMsgBox('Also delete FanPilot settings, fan setup results and logs?', mbConfirmation, MB_YESNO, IDNO) = IDYES) then
    begin
      DelTree(UserDir, True, True, True);
      DelTree(ServiceDir, True, True, True);
    end;
  end;
end;
