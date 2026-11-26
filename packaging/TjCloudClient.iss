#ifndef AppVersion
  #error Build with scripts/Package.ps1 to supply repository metadata.
#endif

[Setup]
AppId={{12ca27e9-70d7-48f0-b0ac-350146b5578a}
AppName={#ProductName}
AppVersion={#AppVersion}
AppPublisher={#Publisher}
AppPublisherURL={#RepositoryUrl}
AppSupportURL={#RepositoryUrl}/issues
AppUpdatesURL={#RepositoryUrl}/releases
VersionInfoVersion={#AppVersion}.0
VersionInfoProductName={#ProductName}
VersionInfoProductVersion={#AppVersion}
DefaultDirName={localappdata}\Programs\TJCloudClient
DefaultGroupName={#ProductName}
DisableProgramGroupPage=yes
AllowNoIcons=yes
PrivilegesRequired=lowest
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0.17763
OutputDir={#OutputDirectory}
OutputBaseFilename={#ArtifactStem}-setup
Compression=lzma2
SolidCompression=yes
WizardStyle=modern
CloseApplications=no
RestartApplications=no
AlwaysRestart=no
RestartIfNeededByRun=no
UninstallDisplayName={#ProductName}
UninstallDisplayIcon={app}\TjCloudClient.exe
LicenseFile={#PublishDirectory}\LICENSE

[Files]
Source: "{#PublishDirectory}\*"; DestDir: "{app}"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{group}\{#ProductName}"; Filename: "{app}\TjCloudClient.exe"

[Run]
Filename: "{app}\TjCloudClient.exe"; WorkingDir: "{app}"; Flags: nowait runasoriginaluser; Check: ShouldRestartClient

[Code]
function OpenProcess(DesiredAccess: DWORD; InheritHandle: BOOL; ProcessId: DWORD): THandle;
  external 'OpenProcess@kernel32.dll stdcall';
function GetProcessTimes(Process: THandle; var Creation, ExitTime, Kernel, User: TFileTime): BOOL;
  external 'GetProcessTimes@kernel32.dll stdcall';
function WaitForSingleObject(Handle: THandle; Milliseconds: DWORD): DWORD;
  external 'WaitForSingleObject@kernel32.dll stdcall';
function OpenEvent(DesiredAccess: DWORD; InheritHandle: BOOL; Name: String): THandle;
  external 'OpenEventW@kernel32.dll stdcall';
function SetEvent(Handle: THandle): BOOL;
  external 'SetEvent@kernel32.dll stdcall';
function CloseHandle(Handle: THandle): BOOL;
  external 'CloseHandle@kernel32.dll stdcall';

var
  ParentProcess, ParentReady, ParentAbort, ParentStopped: THandle;
  ParentWaitChecked: Boolean;
  ParentWaitResult: String;

function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  I, PidCount, StartCount, Pid, Waited: Integer;
  PidText, StartText, Value, EventPrefix: String;
  Started, ActualStarted: Int64;
  Creation, ExitTime, Kernel, User: TFileTime;
  WaitResult: DWORD;
begin
  if ParentWaitChecked then
  begin
    Result := ParentWaitResult;
    Exit;
  end;
  ParentWaitChecked := True;
  ParentWaitResult := 'Update handoff was not authorized. Close the client and run Setup manually.';
  Result := ParentWaitResult;
  PidCount := 0;
  StartCount := 0;
  for I := 1 to ParamCount do
  begin
    Value := ParamStr(I);
    if Pos('/WAITPID', Uppercase(Value)) = 1 then
    begin
      PidCount := PidCount + 1;
      if Pos('/WAITPID=', Value) <> 1 then Exit;
      PidText := Copy(Value, 10, Length(Value));
    end;
    if Pos('/WAITSTART', Uppercase(Value)) = 1 then
    begin
      StartCount := StartCount + 1;
      if Pos('/WAITSTART=', Value) <> 1 then Exit;
      StartText := Copy(Value, 12, Length(Value));
    end;
  end;
  { Manual and WinGet installs without the updater flags retain their existing behavior. }
  if (PidCount = 0) and (StartCount = 0) then
  begin
    ParentWaitResult := '';
    Result := '';
    Exit;
  end;
  if (PidCount <> 1) or (StartCount <> 1) then Exit;
  Pid := StrToIntDef(PidText, 0);
  Started := StrToInt64Def(StartText, 0);
  if (Pid <= 0) or (IntToStr(Pid) <> PidText) or
     (Started <= 0) or (IntToStr(Started) <> StartText) then Exit;
  EventPrefix := 'Local\TJCloudClient.Update.' + PidText + '.' + StartText + '.';
  ParentReady := OpenEvent($100002, False, EventPrefix + 'Ready');
  ParentAbort := OpenEvent($100000, False, EventPrefix + 'Abort');
  ParentStopped := OpenEvent($100002, False, EventPrefix + 'Stopped');
  try
    if (ParentReady = 0) or (ParentAbort = 0) or (ParentStopped = 0) then Exit;
    ParentProcess := OpenProcess($101000, False, Pid);
    if ParentProcess = 0 then Exit;
    if not GetProcessTimes(ParentProcess, Creation, ExitTime, Kernel, User) then Exit;
    ActualStarted := (Int64(Creation.dwHighDateTime) shl 32) or Creation.dwLowDateTime;
    if ActualStarted <> Started then Exit;
    if WaitForSingleObject(ParentAbort, 0) <> $102 then Exit;
    if not SetEvent(ParentReady) then Exit;
    Waited := 0;
    while Waited < 60000 do
    begin
      if WaitForSingleObject(ParentAbort, 0) <> $102 then Exit;
      WaitResult := WaitForSingleObject(ParentProcess, 100);
      if WaitForSingleObject(ParentAbort, 0) <> $102 then Exit;
      if WaitResult = 0 then
      begin
        ParentWaitResult := '';
        Result := '';
        Exit;
      end;
      if WaitResult <> $102 then Exit;
      Waited := Waited + 100;
    end;
  finally
    { Latch rejection before acknowledgement. A second PrepareToInstall cannot retry an aborted handoff. }
    if (ParentWaitResult <> '') and (ParentStopped <> 0) then SetEvent(ParentStopped);
  end;
end;

procedure DeinitializeSetup;
begin
  if ParentProcess <> 0 then CloseHandle(ParentProcess);
  if ParentReady <> 0 then CloseHandle(ParentReady);
  if ParentAbort <> 0 then CloseHandle(ParentAbort);
  if ParentStopped <> 0 then CloseHandle(ParentStopped);
end;

function NeedRestart: Boolean;
begin
  Result := False;
end;

function ShouldRestartClient: Boolean;
var
  I, Requests: Integer;
begin
  Result := False;
  Requests := 0;
  for I := 1 to ParamCount do
  begin
    if ParamStr(I) = '/RESTARTCLIENT=1' then
      Requests := Requests + 1
    else if Pos('/RESTARTCLIENT', Uppercase(ParamStr(I))) = 1 then
      Exit;
  end;
  if Requests <> 1 then
    Exit;
  if RegValueExists(HKLM, 'SYSTEM\CurrentControlSet\Control\Session Manager', 'PendingFileRenameOperations') or
     RegKeyExists(HKLM, 'SOFTWARE\Microsoft\Windows\CurrentVersion\Component Based Servicing\RebootPending') or
     RegKeyExists(HKLM, 'SOFTWARE\Microsoft\Windows\CurrentVersion\WindowsUpdate\Auto Update\RebootRequired') then
    Exit;
  Result := True;
end;
