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
AppCopyright=Copyright (c) 2026 Protagonist Labs
VersionInfoVersion={#AppVersion}

; The app itself requires admin at every launch (it stops services and writes HKLM), so installing
; per-machine costs nothing extra and puts it in the conventional place for a system tool.
PrivilegesRequired=admin
; The studio's folder since 1.20. Before, Program Files\Sysoptimizer: an install from then is not reused (UsePreviousAppDir=no),
; it moves here and the old folder is removed (RemoveLegacyInstall).
DefaultDirName={autopf}\ProtagonistLabs\{#AppName}
UsePreviousAppDir=no
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
; Started with the installer's own (admin) rights: by default a postinstall entry runs as the signed-in user, and
; Windows refuses an exe that requires admin from there (CreateProcess error 740).
Filename: "{app}\{#AppExe}"; Description: "Launch {#AppName}"; Flags: nowait postinstall skipifsilent runascurrentuser
; A self-update runs silently, and the app's updater no longer starts the app itself (it can't know the folder): this does.
Filename: "{app}\{#AppExe}"; Flags: nowait runascurrentuser; Check: RestartAfterUpdate

[UninstallDelete]
; The recorded resource history, the log and the rest of the app's data (in ProgramData before 1.20, if it never moved),
; and its card in the shared folder.
Type: filesandordirs; Name: "{userappdata}\ProtagonistLabs\{#AppName}"
Type: filesandordirs; Name: "{commonappdata}\{#AppName}"
Type: files; Name: "{userappdata}\ProtagonistLabs\apps\sysoptimizer.json"

[Code]
var
  LegacyHandover: Boolean;

{ Setup cannot replace files the running app holds open, so stop it first. }
procedure StopSysoptimizer();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\taskkill.exe'), '/F /IM {#AppExe}', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  Sleep(300);
end;

{ The data moves from ProgramData to the studio's folder, while the app is stopped (the app does the same at start, for an
  install made from another admin account). Same volume, so a rename: instant, never half done. A new folder that already
  has data is left alone, and so is the old one when the rename fails: the app uses it and tries again at its next start. }
procedure MoveData();
var
  OldDir, NewDir: String;
  Found: TFindRec;
  HasData: Boolean;
  ResultCode: Integer;
begin
  OldDir := ExpandConstant('{commonappdata}\{#AppName}');
  NewDir := ExpandConstant('{userappdata}\ProtagonistLabs\{#AppName}');
  if not DirExists(OldDir) then Exit;
  HasData := False;
  if FindFirst(NewDir + '\*', Found) then
  begin
    try
      repeat
        if (Found.Name <> '.') and (Found.Name <> '..') then HasData := True;
      until HasData or not FindNext(Found);
    finally
      FindClose(Found);
    end;
  end;
  if HasData then
  begin
    Log('Data left in ' + OldDir + ': ' + NewDir + ' already has data');
    Exit;
  end;
  RemoveDir(NewDir);
  ForceDirectories(ExtractFileDir(NewDir));
  if RenameFile(OldDir, NewDir) then
  begin
    Log('Data moved to ' + NewDir);
    { ProgramData's permissions came along (the user could read but not delete their own files): take Roaming's. }
    Exec(ExpandConstant('{sys}\icacls.exe'), '"' + NewDir + '" /reset /T /C /Q', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
  end
  else
    Log('Could not move ' + OldDir + '; the app will try at its next start');
end;

function PrepareToInstall(var NeedsRestart: Boolean): String;
begin
  StopSysoptimizer();
  MoveData();
  Result := '';
end;

function RestartAfterUpdate(): Boolean;
begin
  Result := WizardSilent and not LegacyHandover;
end;

{ The install from before 1.20, in Program Files\Sysoptimizer: only its own files go, then the folder if that left it empty.
  The self-update of those versions starts Program Files\Sysoptimizer\Sysoptimizer.exe once setup ends, so a silent setup
  puts the new exe there for that one start: it hands over to the installed copy, which removes it. }
procedure RemoveLegacyInstall();
var
  Legacy: String;
begin
  Legacy := ExpandConstant('{autopf}\{#AppName}');
  if (CompareText(Legacy, ExpandConstant('{app}')) = 0) or not FileExists(Legacy + '\{#AppExe}') then Exit;
  if WizardSilent then
    LegacyHandover := FileCopy(ExpandConstant('{app}\{#AppExe}'), Legacy + '\{#AppExe}', False)
  else
    DeleteFile(Legacy + '\{#AppExe}');
  DeleteFile(Legacy + '\Sysoptimizer.pdb');
  DeleteFile(Legacy + '\sysoptimizer-mcp.exe');
  DeleteFile(Legacy + '\unins000.exe');
  DeleteFile(Legacy + '\unins000.dat');
  RemoveDir(Legacy);
end;

{ The logon task starts the exe it was given. The app re-points it at every start, but until then it would start one that is gone. }
procedure RepointLogonTask();
var
  ResultCode: Integer;
begin
  Exec(ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe'),
    '-NoProfile -NonInteractive -Command "if (Get-ScheduledTask -TaskName ''{#AppName}'' -ErrorAction SilentlyContinue) { ' +
    'Set-ScheduledTask -TaskName ''{#AppName}'' -Action (New-ScheduledTaskAction -Execute ''' + ExpandConstant('{app}\{#AppExe}') +
    ''' -Argument ''--tray'') | Out-Null }"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep <> ssPostInstall then Exit;
  RemoveLegacyInstall();
  RepointLogonTask();
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
  Exec(ExpandConstant('{sys}\netsh.exe'), 'advfirewall firewall delete rule name="Sysoptimizer phone page"', '', SW_HIDE, ewWaitUntilTerminated, ResultCode);
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
