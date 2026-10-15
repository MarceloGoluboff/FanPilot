; Inno Setup script for FanControl. Build with scripts\build.ps1.

#define AppName "FanControl"
#define AppVersion "1.1.0"
#define AppExe "FanControl.exe"
#define ServiceName "FanControl"
; Must match Program.AppUserModelId so the window groups under the taskbar pin.
#define AppUserModelId "MarceloGoluboff.FanControl"

[Setup]
AppId={{8F3C2A51-6B7E-4D4F-9C1A-2E5B7D9F0A13}
AppName={#AppName}
AppVersion={#AppVersion}
AppPublisher=Marcelo Goluboff
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
OutputDir=..\dist
OutputBaseFilename=FanControl-Setup-{#AppVersion}
SetupIconFile=..\src\FanControl\Assets\fan.ico
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
UsedUserAreasWarning=no
CloseApplications=no

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; Flags: unchecked

[Files]
; The exe plus WPF's native DLLs, which ship beside it rather than self-extracting to a temp folder.
Source: "..\publish\*"; DestDir: "{app}"; Flags: ignoreversion

[InstallDelete]
; 1.0.0 kept service data in ProgramData, which standard users can write. It now lives in SYSTEM's profile.
Type: filesandordirs; Name: "{commonappdata}\{#AppName}"

[Icons]
Name: "{autoprograms}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "{#AppUserModelId}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; AppUserModelID: "{#AppUserModelId}"; Tasks: desktopicon

[Registry]
; Tray icon at sign-in for the installing user. The service runs regardless.
Root: HKCU; Subkey: "Software\Microsoft\Windows\CurrentVersion\Run"; ValueType: string; ValueName: "{#AppName}"; ValueData: """{app}\{#AppExe}"" --tray"; Flags: uninsdeletevalue

[Run]
; Creates or updates the LocalSystem service and starts it.
Filename: "{app}\{#AppExe}"; Parameters: "--install-service"; StatusMsg: "Starting the FanControl service..."; Flags: runhidden waituntilterminated
Filename: "{app}\{#AppExe}"; Description: "Open FanControl"; Flags: postinstall nowait skipifsilent runasoriginaluser

[UninstallRun]
; Stopping the service hands the fans back to ASUS. The taskkill filter skips session 0 so it can
; only close windows, never kill the service before it has restored the fans.
Filename: "{sys}\net.exe"; Parameters: "stop {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "StopService"
Filename: "{sys}\sc.exe"; Parameters: "delete {#ServiceName}"; Flags: runhidden waituntilterminated; RunOnceId: "DeleteService"
Filename: "{sys}\taskkill.exe"; Parameters: "/f /fi ""SESSION ne 0"" /im {#AppExe}"; Flags: runhidden waituntilterminated; RunOnceId: "StopApp"

[Messages]
FinishedLabel=Setup has finished installing FanControl.%n%nFan control runs as a background service and starts with Windows. To pin the app: open Start, right-click FanControl and choose Pin to taskbar.

[Code]
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
       (MsgBox('Also delete FanControl settings and logs?', mbConfirmation, MB_YESNO) = IDYES) then
    begin
      DelTree(UserDir, True, True, True);
      DelTree(ServiceDir, True, True, True);
    end;
  end;
end;
