; Inno Setup script for Sysoptimizer. Build it through build.ps1, which publishes the app first and
; passes the version in. Modelled on File Labs' installer (vault: File Labs/Release si update.md).

#define AppName "Sysoptimizer"
#define AppExe "Sysoptimizer.exe"
#define AppPublisher "Protagonist Labs"

#ifndef AppVersion
  #define AppVersion "1.0.0"
#endif

[Setup]
; Never change AppId: it is what ties an update to the installation it replaces.
AppId={{8716521D-484A-4927-87F1-5246AD1F815A}
AppName={#AppName}
AppVersion={#AppVersion}
AppVerName={#AppName} {#AppVersion}
AppPublisher={#AppPublisher}
VersionInfoVersion={#AppVersion}

; The app itself requires admin at every launch (it stops services and writes HKLM), so installing
; per-machine costs nothing extra and puts it in the conventional place for a system tool.
PrivilegesRequired=admin
DefaultDirName={autopf}\{#AppName}
DefaultGroupName={#AppName}
DisableProgramGroupPage=yes
DisableDirPage=auto
UninstallDisplayIcon={app}\{#AppExe}
UninstallDisplayName={#AppName}

ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible

CloseApplications=force
RestartApplications=no

OutputDir=..\dist
OutputBaseFilename=Sysoptimizer-{#AppVersion}-setup
SetupIconFile=..\app.ico
Compression=lzma2/ultra64
SolidCompression=yes
WizardStyle=modern

[Languages]
Name: "en"; MessagesFile: "compiler:Default.isl"

[Tasks]
Name: "desktopicon"; Description: "Create a desktop shortcut"; GroupDescription: "Shortcuts:"; Flags: unchecked

[Files]
Source: "..\dist\app\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#AppName}"; Filename: "{app}\{#AppExe}"
Name: "{group}\Uninstall {#AppName}"; Filename: "{uninstallexe}"
Name: "{autodesktop}\{#AppName}"; Filename: "{app}\{#AppExe}"; Tasks: desktopicon

[Run]
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent

[UninstallDelete]
; The recorded resource history.
Type: filesandordirs; Name: "{commonappdata}\{#AppName}"

[Code]
{ Setup cannot replace files the running app holds open, so stop it first. }
procedure StopSysoptimizer();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(300);
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopSysoptimizer();
  Result := '';
end;

function InitializeUninstall(): Boolean;
begin
  StopSysoptimizer();
  Result := True;
end;

{ Undo what the app set up outside its folder: the logon task for background recording, and every app
  it blocked from starting — without Sysoptimizer there'd be nothing left to unblock them with. Only IFEO
  entries carrying our marker are touched. }
procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  ResultCode, I: Integer;
  Names: TArrayOfString;
  Key: String;
begin
  if CurUninstallStep <> usUninstall then Exit;
  Exec(ExpandConstant('{sys}\schtasks.exe'), '/Delete /TN "{#AppName}" /F', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Exec(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="Sysoptimizer remote monitoring"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  if RegGetSubkeyNames(HKLM64, 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options', Names) then
    for I := 0 to GetArrayLength(Names) - 1 do
    begin
      Key := 'SOFTWARE\Microsoft\Windows NT\CurrentVersion\Image File Execution Options\' + Names[I];
      if RegValueExists(HKLM64, Key, 'SysoptimizerBlocked') then
      begin
        RegDeleteValue(HKLM64, Key, 'Debugger');
        RegDeleteValue(HKLM64, Key, 'SysoptimizerBlocked');
        RegDeleteKeyIfEmpty(HKLM64, Key);
      end;
    end;
end;
