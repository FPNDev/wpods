; WPods installer (Inno Setup 6). Built by ..\build.ps1, which passes /DAppVersion.
#ifndef AppVersion
  #error AppVersion is not defined: build with build.ps1
#endif
; Fetched by ..\native\fetch-usbip.ps1 into ..\.build\deps.
#define UsbipSetup "USBip-0.9.8.1-x64.exe"

[Setup]
AppId={{08E1B0A8-4D58-46F6-B9EC-006F5CC3A69F}
AppName=WPods
AppVersion={#AppVersion}
AppPublisher=WPods
AppCopyright=Copyright (C) 2026 fpndev (rimjoeh) <rimjoeh@gmail.com>
LicenseFile=TERMS.rtf
VersionInfoVersion={#AppVersion}
UninstallDisplayName=WPods
UninstallDisplayIcon={app}\WPods.exe
DefaultDirName={autopf}\WPods
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19041
SetupIconFile=..\src\WPods\Assets\WPods.ico
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; Restart Manager closes only the app and never restarts it (it would run elevated). The service keeps running
; through the copy (see MoveServiceFilesAside); stopped by Restart Manager, it would leave the adapter on WinUSB.
CloseApplications=yes
CloseApplicationsFilter=WPods.exe
RestartApplications=no
; Only NeedRestart offers a restart; a Test Mode change applies at the user's next restart.
RestartIfNeededByRun=no
SetupLogging=yes
OutputDir=..\dist
OutputBaseFilename=WPods-Setup

[Tasks]
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: startup; Description: "Start WPods when I sign in"; Check: IsFirstInstall
Name: autoupdate; Description: "Keep WPods up to date automatically"

[InstallDelete]
; Service files the previous update moved aside while the service used them (see MoveServiceFilesAside).
Type: files; Name: "{app}\*.old-*"
; Left by an older service version that rotated its log; nothing writes it any more.
Type: files; Name: "{commonappdata}\WPods\logs\service.1.log"
; Folders fully owned by the app: drop files a previous version had (e.g. a removed locale would stay selectable).
Type: filesandordirs; Name: "{app}\web"
Type: filesandordirs; Name: "{app}\locales"
Type: filesandordirs; Name: "{app}\licenses"

[Files]
Source: "..\.build\publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
; Removes the drivers of WPods 0.2.x and older (updates and uninstall).
Source: "drivers.ps1"; DestDir: "{app}\drivers"; Flags: ignoreversion
; Extracted to {tmp} and run by PrepareToInstall, before any file is copied.
Source: "..\.build\deps\{#UsbipSetup}"; Flags: dontcopy
; One-time update step, extracted to {tmp} (see UpdateStereoMicPairs).
Source: "stereo-mic.ps1"; Flags: dontcopy

[UninstallDelete]
Type: files; Name: "{app}\drivers\installed.txt"
; Updates the service downloaded.
Type: filesandordirs; Name: "{commonappdata}\WPods\updates"

[Icons]
Name: "{autoprograms}\WPods"; Filename: "{app}\WPods.exe"
Name: "{autodesktop}\WPods"; Filename: "{app}\WPods.exe"; Tasks: desktopicon

[Run]
; The app writes the HKCU Run value itself, as the user who started Setup (never the elevating admin).
Filename: "{app}\WPods.exe"; Parameters: "--register-startup"; Tasks: startup; Check: IsFirstInstall; Flags: runasoriginaluser waituntilterminated
Filename: "{app}\WPods.exe"; Description: "{cm:LaunchProgram,WPods}"; Check: not FromService; Flags: postinstall runasoriginaluser nowait

[UninstallRun]
; Runs before the service and usbip-win2 are removed (usPostUninstall); the RunOnceId matches the
; entry of WPods 0.2.x, so it runs once.
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\drivers\drivers.ps1"" -Uninstall"; Flags: runhidden waituntilterminated logoutput; RunOnceId: "Drivers"

[Code]
const
  DotNetUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe';
  DotNetFile = 'windowsdesktop-runtime-win-x64.exe';
  WebView2Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';
  WebView2File = 'MicrosoftEdgeWebview2Setup.exe';
  WebView2Key = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  UsbipSetup = '{#UsbipSetup}';
  ServiceName = 'WPodsService';
  // HKLM 64-bit view: TestSigningByWPods (WPods 0.2.x turned Test Mode on), UsbipInstalledByWPods.
  WPodsKey = 'SOFTWARE\WPods';
  // Exit code of RunMicrosoftInstaller when the file is not validly signed by Microsoft Corporation.
  NotMicrosoftSigned = 22352;
  SystemCodeIntegrityInformation = 103;
  CodeIntegrityOptionTestSign = 2;
  BitLockerOff = 0;
  BitLockerSuspended = 10;
  BitLockerOn = 11;
  BitLockerConverting = 12;
  // Exit code of the TestSigningConfigured script when {current} has testsigning set.
  BcdTestSigningSet = 10;

type
  TCodeIntegrityInformation = record
    Length: Cardinal;
    Options: Cardinal;
  end;

var
  DownloadPage: TDownloadWizardPage;
  BluetoothPage: TOutputMsgWizardPage;
  AdapterChecked: Boolean;
  // "Leave Test Mode" page and its controls; nil when the page is not needed (see NeedsLeavePage).
  LeavePage: TWizardPage;
  LeaveTestModeBox: TNewCheckBox;
  FirmwareButton: TNewButton;
  RecoveryKeyBox: TNewCheckBox;
  // BitLocker('status') result, read once for the Leave Test Mode page.
  BitLockerStatus: Integer;
  // {current} has testsigning set (it may only apply after the next restart).
  TestModeConfigured: Boolean;
  // This run turned test signing off (so it is not done twice).
  TestModeTurnedOff: Boolean;
  // Setup closes itself for the firmware restart: no "Exit Setup?" question.
  ClosingForFirmware: Boolean;
  RemoveSettings: Boolean;
  FirstInstall: Boolean;
  // The version this run updates, '' on a first install.
  PreviousVersion: String;
  // Uninstall: the Bluetooth adapter is back on Windows' own driver, so usbip-win2 may go.
  AdapterRestored: Boolean;
  // Uninstall: usbip-win2 was installed by Setup but stays, so its marker must stay too.
  UsbipKept: Boolean;
  // Uninstall: this run removed usbip-win2 (and with it the virtual adapter's device nodes).
  UsbipRemoved: Boolean;
  // Uninstall: the service files were kept aside, so the service is removed at the end (usPostUninstall).
  ServiceKeptForLater: Boolean;
  // BitLocker on the OS drive was suspended by this run (so only this run may resume it).
  BitLockerSuspendedByRun: Boolean;
  // This run installed usbip-win2, which works only after a restart.
  UsbipJustInstalled: Boolean;
  // The stamp of the service files MoveServiceFilesAside renamed ('' = none moved, or moved back).
  MovedAsideStamp: String;
  // The files are installed (ssPostInstall reached): no rollback can take the new service files away any more.
  FilesInstalled: Boolean;
  // The Select Tasks page was shown (it shows the current auto-update choice, see CurPageChanged).
  TasksPageShown: Boolean;

function InitializeSetup: Boolean;
begin
  // Read once, before this install registers itself: an update must not touch the user's startup choice.
  FirstInstall := not RegKeyExists(HKLM64,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{08E1B0A8-4D58-46F6-B9EC-006F5CC3A69F}_is1');
  if not RegQueryStringValue(HKLM64,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{08E1B0A8-4D58-46F6-B9EC-006F5CC3A69F}_is1',
    'DisplayVersion', PreviousVersion) then
    PreviousVersion := '';
  Result := True;
end;

// Startup is owned by the app; Setup offers it only on a first install.
function IsFirstInstall: Boolean;
begin
  Result := FirstInstall;
end;

// Started silently as SYSTEM by the WPods service to install an update; the service reopens the app.
function FromService: Boolean;
begin
  Result := ExpandConstant('{param:FROMSERVICE|0}') = '1';
end;

function NtQuerySystemInformation(InfoClass: Integer; var Info: TCodeIntegrityInformation; InfoLength: Cardinal;
  var ReturnLength: Cardinal): Integer;
  external 'NtQuerySystemInformation@ntdll.dll stdcall';

function PowerShell: String;
begin
  Result := ExpandConstant('{sys}\WindowsPowerShell\v1.0\powershell.exe');
end;

// PowerShell single-quoted string literal.
function PSString(const S: String): String;
begin
  Result := S;
  StringChangeEx(Result, '''', '''''', True);
  Result := '''' + Result + '''';
end;

// 'Setup' or 'Uninstall', for messages shared by both.
function Who: String;
begin
  if IsUninstaller then
    Result := 'Uninstall'
  else
    Result := 'Setup';
end;

function WPodsFlag(const Name: String): Boolean;
var
  Value: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM64, WPodsKey, Name, Value) and (Value = 1);
end;

// WPods 0.2.x turned Windows test signing on (it wrote this marker).
function TestSigningByWPods: Boolean;
begin
  Result := WPodsFlag('TestSigningByWPods');
end;

procedure DeleteTestSigningMarker;
begin
  RegDeleteValue(HKLM64, WPodsKey, 'TestSigningByWPods');
  RegDeleteKeyIfEmpty(HKLM64, WPodsKey);
end;

// Running state, not the boot configuration: a TESTSIGNING change only applies after a restart.
function TestSigningOn: Boolean;
var
  Info: TCodeIntegrityInformation;
  ReturnLength: Cardinal;
begin
  Info.Length := 8;
  Info.Options := 0;
  Result := (NtQuerySystemInformation(SystemCodeIntegrityInformation, Info, 8, ReturnLength) = 0) and
    ((Info.Options and CodeIntegrityOptionTestSign) <> 0);
end;

// HKLM 64-bit view; a missing value or 0 means off or a legacy BIOS.
function SecureBootOn: Boolean;
var
  Value: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM64, 'SYSTEM\CurrentControlSet\Control\SecureBoot\State',
    'UEFISecureBootEnabled', Value) and (Value = 1);
end;

// BitLocker 'status', 'suspend' (for RebootCount restarts) or 'resume' on the OS drive via WMI, as the BitLocker
// module is missing on some Home installs. Any result but a BitLocker* code is a failure, which callers treat as
// "may be protected": a boot configuration change under BitLocker makes Windows ask for the recovery key.
function BitLocker(const Action: String; const RebootCount: Integer): Integer;
var
  Code: Integer;
begin
  if not Exec(PowerShell, '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' +
    '$ErrorActionPreference = ''Stop''; try { ' +
    '$v = Get-CimInstance -Namespace root/CIMV2/Security/MicrosoftVolumeEncryption -ClassName Win32_EncryptableVolume | ' +
    'Where-Object DriveLetter -eq $env:SystemDrive; $a = ' + PSString(Action) + '; ' +
    'if (-not $v) { if ($a -eq ''resume'') { exit 0 }; exit 1 }; ' +
    'if ($a -eq ''resume'') { ' +
    'if ((Invoke-CimMethod -InputObject $v -MethodName EnableKeyProtectors).ReturnValue -eq 0) { exit 0 }; exit 1 }; ' +
    '$p = (Invoke-CimMethod -InputObject $v -MethodName GetProtectionStatus).ProtectionStatus; ' +
    'if ($p -eq 0) { $c = (Invoke-CimMethod -InputObject $v -MethodName GetConversionStatus).ConversionStatus; ' +
    'if ($c -eq 0 -or $c -eq 1) { exit ' + IntToStr(BitLockerOff) + ' }; ' +
    'if ($c -ge 2 -and $c -le 5) { exit ' + IntToStr(BitLockerConverting) + ' }; exit 1 }; ' +
    'if ($p -ne 1) { exit 1 }; ' +
    'if ($a -eq ''status'') { exit ' + IntToStr(BitLockerOn) + ' }; ' +
    'if ((Invoke-CimMethod -InputObject $v -MethodName DisableKeyProtectors ' +
    '-Arguments @{DisableCount = [uint32]' + IntToStr(RebootCount) + '}).ReturnValue -ne 0) { exit 1 }; ' +
    'if ((Invoke-CimMethod -InputObject $v -MethodName GetProtectionStatus).ProtectionStatus -ne 0) { exit 1 }; ' +
    'exit ' + IntToStr(BitLockerSuspended) + ' } catch { exit 1 }"', '', SW_HIDE, ewWaitUntilTerminated, Code) then begin
    Log('Could not run PowerShell for BitLocker ' + Action + ': ' + SysErrorMessage(Code));
    Code := -1;
  end;
  Result := Code;
end;

// Only BitLockerOff (off or already suspended) and BitLockerSuspended allow a boot change, see BitLockerSafe.
function SuspendBitLocker(const RebootCount: Integer): Integer;
begin
  Result := BitLocker('suspend', RebootCount);
  if Result = BitLockerSuspended then begin
    BitLockerSuspendedByRun := True;
    Log(Format('BitLocker suspended on the OS drive for the next %d restart(s).', [RebootCount]));
  end else if Result = BitLockerOff then
    Log('BitLocker does not protect the OS drive (off or already suspended).')
  else if Result = BitLockerConverting then
    Log('BitLocker is still encrypting or decrypting the OS drive; nothing was changed.')
  else
    Log(Format('Could not confirm that BitLocker is off or suspended on the OS drive (exit code %d).', [Result]));
end;

// True when a SuspendBitLocker result allows changing the boot configuration.
function BitLockerSafe(const Code: Integer): Boolean;
begin
  Result := (Code = BitLockerOff) or (Code = BitLockerSuspended);
end;

// Resumes BitLocker only when this run suspended it; false when it stays suspended.
function ResumeBitLocker: Boolean;
begin
  Result := True;
  if not BitLockerSuspendedByRun then
    Exit;
  Result := BitLocker('resume', 0) = BitLockerOff;
  if Result then begin
    BitLockerSuspendedByRun := False;
    Log('BitLocker resumed on the OS drive.');
  end else
    Log('Could not resume BitLocker on the OS drive; it stays suspended for the restarts it was suspended for.');
end;

// How to suspend BitLocker for RebootCount restarts by hand, for every case Setup cannot do it safely itself.
function BitLockerManualSteps(const RebootCount: Integer): String;
begin
  Result := 'To suspend BitLocker (or Device encryption) yourself, open Windows PowerShell or Terminal as ' +
    'administrator and run:' + #13#10 +
    'manage-bde -protectors -disable ' + ExpandConstant('{sd}') + ' -RebootCount ' + IntToStr(RebootCount) + #13#10#13#10 +
    'Keep your BitLocker recovery key at hand: https://aka.ms/myrecoverykey';
end;

// Boot configuration, not the running state: {current} has testsigning set. A failed read counts as not set.
function TestSigningConfigured: Boolean;
var
  Code: Integer;
begin
  Result := Exec(PowerShell, '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' +
    '$ErrorActionPreference = ''Stop''; try { ' +
    '$o = [wmi]''root\wmi:BcdObject.Id=\"{fa926493-6f1c-4193-a414-58f0b2456d1e}\",StoreFilePath=\"\"''; ' +
    '$e = $o.GetElement(0x16000049); ' +
    'if ($e.ReturnValue -and $e.Element.Boolean) { exit ' + IntToStr(BcdTestSigningSet) + ' }; exit 0 ' +
    '} catch { exit 0 }"', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = BcdTestSigningSet);
end;

// Turns test signing off only when BitLocker is confirmed off or suspended, else Windows asks for the recovery key.
// The suspend covers 2 restarts: this change and turning Secure Boot back on.
function TurnTestSigningOff(const ShowDone: Boolean): Boolean;
var
  Code: Integer;
  Manual, Msg: String;
begin
  Result := False;
  Manual := 'To turn Test Mode off yourself:' + #13#10#13#10 + BitLockerManualSteps(2) + #13#10#13#10 +
    'Then run: bcdedit /set testsigning off' + #13#10 + 'Then restart Windows.';
  Code := SuspendBitLocker(2);
  if Code = BitLockerConverting then begin
    SuppressibleMsgBox('Windows Test Mode was left on: BitLocker (or Device encryption) is still encrypting or ' +
      'decrypting drive ' + ExpandConstant('{sd}') + '. Wait until it finishes.' + #13#10#13#10 + Manual,
      mbError, MB_OK, IDOK);
    Exit;
  end;
  if not BitLockerSafe(Code) then begin
    SuppressibleMsgBox('Windows Test Mode was left on: ' + Who + ' could not confirm that BitLocker (or Device ' +
      'encryption) is off or suspended on drive ' + ExpandConstant('{sd}') + '. Turning Test Mode off while ' +
      'BitLocker protects the drive makes Windows ask for the BitLocker recovery key.' + #13#10#13#10 + Manual,
      mbError, MB_OK, IDOK);
    Exit;
  end;
  if not Exec(ExpandConstant('{sys}\bcdedit.exe'), '/set testsigning off', '', SW_HIDE, ewWaitUntilTerminated,
    Code) then
    Log('Could not run bcdedit to turn Windows test signing off: ' + SysErrorMessage(Code))
  else if Code <> 0 then
    Log(Format('Could not turn Windows test signing off (bcdedit exit code %d).', [Code]))
  else begin
    Result := True;
    Log('Windows test signing turned off; it takes effect after a restart.');
    if ShowDone then begin
      Msg := 'Windows Test Mode turns off when you restart Windows.';
      if BitLockerSuspendedByRun then
        Msg := Msg + ' BitLocker is suspended for the next 2 restarts, so restart soon. If you turn Secure Boot ' +
          'back on during one of them, Windows does not ask for the BitLocker recovery key.';
      SuppressibleMsgBox(Msg + #13#10#13#10 +
        'If you turn Secure Boot back on later, suspend BitLocker first, or Windows may ask for the BitLocker ' +
        'recovery key.' + #13#10#13#10 + BitLockerManualSteps(1), mbInformation, MB_OK, IDOK);
    end;
    Exit;
  end;
  // bcdedit failed, so the boot configuration is unchanged: undo this run's suspend.
  ResumeBitLocker;
  SuppressibleMsgBox(Who + ' could not turn Windows Test Mode off.' + #13#10#13#10 + Manual, mbError, MB_OK, IDOK);
end;

// TurnTestSigningOff, then the marker goes (it only stays while Test Mode may still be on).
function LeaveTestMode(const ShowDone: Boolean): Boolean;
begin
  Result := TurnTestSigningOff(ShowDone);
  if Result then begin
    TestModeTurnedOff := True;
    DeleteTestSigningMarker;
  end;
end;

{ Mirrors the .NET host lookup: registered x64 install location (32-bit registry view), else Program Files\dotnet.
  Preview runtimes (folder names with '-') cannot run a release build, so they don't count. }
function DotNetInstalled: Boolean;
var
  Root: String;
  FindRec: TFindRec;
begin
  Result := False;
  if not RegQueryStringValue(HKLM32, 'SOFTWARE\dotnet\Setup\InstalledVersions\x64', 'InstallLocation', Root) or (Root = '') then begin
    Root := ExpandConstant('{commonpf64}\dotnet');
  end;
  if FindFirst(AddBackslash(Root) + 'shared\Microsoft.WindowsDesktop.App\10.*', FindRec) then begin
    try
      repeat
        if ((FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0) and (Pos('-', FindRec.Name) = 0) then begin
          Result := True;
          Exit;
        end;
      until not FindNext(FindRec);
    finally
      FindClose(FindRec);
    end;
  end;
end;

function WebView2InstalledIn(RootKey: Integer): Boolean;
var
  Version: String;
begin
  Result := RegQueryStringValue(RootKey, WebView2Key, 'pv', Version) and (Version <> '') and (Version <> '0.0.0.0');
end;

function WebView2Installed: Boolean;
begin
  // HKLM32 = HKLM\SOFTWARE\WOW6432Node (per-machine install); HKCU = per-user install.
  Result := WebView2InstalledIn(HKLM32) or WebView2InstalledIn(HKCU);
end;

function UsbipInstalled: Boolean;
begin
  Result := FileExists(ExpandConstant('{commonpf64}\USBip\usbip.exe'));
end;

// usbip-win2 installed by an earlier run still waits for a restart (the service clears the value when it starts).
function ServiceWaitsForRestart: Boolean;
var
  Value: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM64, 'SOFTWARE\WPods\Service', 'WaitsForRestart', Value) and (Value = 1);
end;

function ServiceRegistered: Boolean;
begin
  Result := RegKeyExists(HKLM64, 'SYSTEM\CurrentControlSet\Services\' + ServiceName);
end;

// Runs a downloaded installer only if validly signed with O=Microsoft Corporation (.NET's signer is CN=.NET). The
// file is held open, write and delete denied, until the installer exits, so it cannot be swapped after the check.
function RunMicrosoftInstaller(const FileName, Params: String; var ResultCode: Integer): Boolean;
var
  Script: String;
begin
  Script :=
    '$f = [IO.File]::Open(' + PSString(FileName) + ', ''Open'', ''Read'', ''Read''); ' +
    'try { ' +
    '$s = Get-AuthenticodeSignature -LiteralPath ' + PSString(FileName) + '; ' +
    'if ($s.Status -ne ''Valid'' -or ($s.SignerCertificate.SubjectName.Format($true) -split ''\r?\n'') ' +
    '-cnotcontains ''O=Microsoft Corporation'') { exit ' + IntToStr(NotMicrosoftSigned) + ' } ' +
    '$p = [Diagnostics.Process]::Start(' + PSString(FileName) + ', ' + PSString(Params) + '); ' +
    '$p.WaitForExit(); exit $p.ExitCode ' +
    '} finally { $f.Dispose() }';
  Result := Exec(PowerShell, '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' + Script + '"', '',
    SW_HIDE, ewWaitUntilTerminated, ResultCode);
end;

// Returns '' on success, else the error to show.
function InstallPrerequisite(const Name, FileName, Params: String): String;
var
  Code: Integer;
begin
  DownloadPage.SetText('Installing ' + Name + '...', '');
  if not RunMicrosoftInstaller(ExpandConstant('{tmp}\' + FileName), Params, Code) then
    Result := 'Setup could not verify the ' + Name + ' installer: ' + SysErrorMessage(Code)
  else if Code = NotMicrosoftSigned then
    Result := 'The downloaded ' + Name + ' installer is not signed by Microsoft Corporation. Setup did not run it.'
  else begin
    Log(Format('%s installer exit code: %d', [Name, Code]));
    Result := '';
    if ((FileName = DotNetFile) and not DotNetInstalled) or ((FileName = WebView2File) and not WebView2Installed) then
      Result := Format('%s could not be installed (installer exit code %d).', [Name, Code]);
  end;
end;

// Installs the bundled usbip-win2 and records it for Uninstall. Returns '' on success, else the error to show.
function InstallUsbip: String;
var
  Code, I: Integer;
  LogFile: String;
  Lines: TArrayOfString;
begin
  Result := '';
  DownloadPage.SetText('Preparing Bluetooth...', '');
  DownloadPage.Show;
  try
    ExtractTemporaryFile(UsbipSetup);
    LogFile := ExpandConstant('{tmp}\usbip-setup.log');
    // No desktop shortcut: usbip-win2 is a part of WPods, not an app the user opens.
    if not Exec(ExpandConstant('{tmp}\' + UsbipSetup), '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /MERGETASKS="!desktopicon" ' +
      '/LOG="' + LogFile + '"', '', SW_HIDE, ewWaitUntilTerminated, Code) then
      Result := 'Setup could not install a part WPods needs for Bluetooth (usbip-win2): ' + SysErrorMessage(Code) +
        ' Nothing was installed.'
    else begin
      Log(Format('usbip-win2 installer exit code: %d', [Code]));
      if UsbipInstalled then begin
        UsbipJustInstalled := True;
        if not RegWriteDWordValue(HKLM64, WPodsKey, 'UsbipInstalledByWPods', 1) then
          Log('Could not record HKLM64\SOFTWARE\WPods\UsbipInstalledByWPods.');
      end else begin
        // Its log lives in {tmp}, which goes when Setup exits: keep it in this log.
        if LoadStringsFromFile(LogFile, Lines) then
          for I := 0 to GetArrayLength(Lines) - 1 do
            Log('usbip-win2: ' + Lines[I]);
        Result := Format('Setup could not install a part WPods needs for Bluetooth (usbip-win2, error %d). ' +
          'Nothing was installed.', [Code]);
      end;
    end;
  finally
    DownloadPage.Hide;
  end;
end;

// Stops the old service with the adapter kept on WinUSB, so InstallService starts the new one without a driver switch.
procedure StopService;
var
  Code: Integer;
  Exe: String;
begin
  Exe := ExpandConstant('{app}\WPods.Service.exe');
  if not FileExists(Exe) then
    Exit;
  if not ExecAndLogOutput(Exe, '--stop', '', SW_HIDE, ewWaitUntilTerminated, Code, nil) then
    Log('Could not run WPods.Service.exe --stop: ' + SysErrorMessage(Code))
  else if Code <> 0 then
    Log(Format('WPods.Service.exe --stop exit code: %d', [Code]));
end;

// Files the running service keeps open. Files in use can be renamed, so they are moved aside before the copy or
// delete and the service (and Bluetooth) keeps running until the end; if a rename fails, it is stopped first.
function ServiceFiles: TArrayOfString;
begin
  SetArrayLength(Result, 6);
  Result[0] := 'WPods.Service.exe';
  Result[1] := 'WPods.Service.dll';
  Result[2] := 'System.ServiceProcess.ServiceController.dll';
  Result[3] := 'System.Diagnostics.EventLog.dll';
  Result[4] := 'System.Diagnostics.EventLog.Messages.dll';
  // The mic's shared jitter buffer.
  Result[5] := 'wpods_native.dll';
end;

// Puts moved-aside files back where nothing replaced them: after a partial move or a cancelled or failed install.
procedure MoveServiceFilesBack;
var
  Names: TArrayOfString;
  I: Integer;
  Path: String;
begin
  if MovedAsideStamp = '' then
    Exit;
  Names := ServiceFiles;
  for I := 0 to GetArrayLength(Names) - 1 do begin
    Path := ExpandConstant('{app}\') + Names[I];
    if FileExists(Path + '.old-' + MovedAsideStamp) and not FileExists(Path) then
      if RenameFile(Path + '.old-' + MovedAsideStamp, Path) then
        Log('Moved ' + Path + ' back.')
      else
        Log('Could not move ' + Path + ' back.');
  end;
  MovedAsideStamp := '';
end;

// False when a file could not be moved aside; the files already moved are put back then.
function MoveServiceFilesAside: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  Path: String;
begin
  Result := True;
  Names := ServiceFiles;
  MovedAsideStamp := GetDateTimeString('yyyymmddhhnnss', #0, #0);
  for I := 0 to GetArrayLength(Names) - 1 do begin
    Path := ExpandConstant('{app}\') + Names[I];
    if FileExists(Path) and not RenameFile(Path, Path + '.old-' + MovedAsideStamp) then begin
      Log('Could not move ' + Path + ' aside.');
      MoveServiceFilesBack;
      Result := False;
      Exit;
    end;
  end;
end;

// Deletes the files moved aside, once the service that used them is gone (retries while its process exits).
procedure DeleteServiceFilesAside;
var
  F: TFindRec;
begin
  if FindFirst(ExpandConstant('{app}\*.old-*'), F) then
    try
      repeat
      begin
        DelayDeleteFile(ExpandConstant('{app}\') + F.Name, 20);
        if FileExists(ExpandConstant('{app}\') + F.Name) then
          Log('Could not delete ' + F.Name);
      end
      until not FindNext(F);
    finally
      FindClose(F);
    end;
end;

// Logs the Bluetooth LE, input and paired classic devices to the Setup log and C:\ProgramData\WPods-bluetooth.txt,
// which is kept after uninstall.
procedure LogBluetoothDevices(const Stage: String);
var
  Code: Integer;
begin
  ExecAndLogOutput(PowerShell, '-NoProfile -NonInteractive -Command "' +
    '$o = @(''=== ' + Stage + ' '' + (Get-Date -Format ''yyyy-MM-dd HH:mm:ss'')); ' +
    '$p = @{}; foreach ($d in Get-PnpDevice -ErrorAction SilentlyContinue) { $p[$d.InstanceId] = $d }; ' +
    'foreach ($c in ''BTHLE'', ''BTHLEDevice'', ''BTHENUM'') { ' +
    'foreach ($k in Get-ChildItem (''HKLM:\SYSTEM\CurrentControlSet\Enum\'' + $c) -ErrorAction SilentlyContinue) { ' +
    'foreach ($i in Get-ChildItem $k.PSPath) { $id = $c + ''\'' + $k.PSChildName + ''\'' + $i.PSChildName; ' +
    'if ($c -eq ''BTHENUM'' -and $id -notmatch ''Dev_|00001124|00001812'') { continue }; ' +
    '$d = $p[$id]; $o += (''{0} | present={1} | {2}'' -f $id, $d.Present, $d.FriendlyName) } } }; ' +
    '$o += ''paired classic: '' + ((Get-ChildItem HKLM:\SYSTEM\CurrentControlSet\Services\BTHPORT\Parameters\Devices ' +
    '-ErrorAction SilentlyContinue).PSChildName -join '', ''); ' +
    '$o | Add-Content C:\ProgramData\WPods-bluetooth.txt; $o"', '', SW_HIDE, ewWaitUntilTerminated, Code, nil);
end;

// Pages: Leave Test Mode.

// A word-wrapped label at Top, as wide as the page, as high as its text.
function AddText(const Page: TWizardPage; const Top: Integer; const Text: String): TNewStaticText;
begin
  Result := TNewStaticText.Create(Page);
  Result.Parent := Page.Surface;
  Result.Top := Top;
  Result.Width := Page.SurfaceWidth;
  Result.AutoSize := False;
  Result.WordWrap := True;
  Result.Caption := Text;
  Result.AdjustHeight;
end;

// Next needs the recovery key box when Test Mode is to change with BitLocker maybe on; the firmware restart always
// needs it, plus the Test Mode box while testsigning is still set.
procedure UpdateLeavePage(Sender: TObject);
var
  KeyOk: Boolean;
begin
  KeyOk := True;
  if RecoveryKeyBox <> nil then
    KeyOk := RecoveryKeyBox.Checked;
  WizardForm.NextButton.Enabled := KeyOk or not LeaveTestModeBox.Checked;
  if FirmwareButton <> nil then
    FirmwareButton.Enabled := KeyOk and (LeaveTestModeBox.Checked or not TestModeConfigured);
end;

// Restarts into the UEFI firmware settings so the user can turn Secure Boot back on. Setup closes without installing.
// Test Mode is turned off first when the box is ticked; BitLocker is suspended first either way.
procedure FirmwareButtonClick(Sender: TObject);
var
  Code: Integer;
  Msg: String;
begin
  Msg := 'Restart into your PC''s firmware settings now?' + #13#10#13#10 + 'Save your work first. Setup closes ' +
    'without updating WPods; run it again after Windows starts.';
  if LeaveTestModeBox.Checked and not TestModeTurnedOff then
    Msg := Msg + ' Setup turns Test Mode off before the restart.';
  if RecoveryKeyBox <> nil then
    Msg := Msg + ' Setup suspends BitLocker for the restart, but keep your BitLocker recovery key at hand.';
  if MsgBox(Msg, mbConfirmation, MB_YESNO or MB_DEFBUTTON2) <> IDYES then
    Exit;
  if LeaveTestModeBox.Checked and not TestModeTurnedOff then begin
    // Suspends BitLocker for 2 restarts itself, and reports its own errors.
    if not LeaveTestMode(False) then
      Exit;
  end else if not BitLockerSafe(SuspendBitLocker(1)) then begin
    MsgBox('Setup did not restart: it could not confirm that BitLocker (or Device encryption) is off or suspended ' +
      'on drive ' + ExpandConstant('{sd}') + '. Turning Secure Boot back on while BitLocker protects the drive ' +
      'makes Windows ask for the BitLocker recovery key.' + #13#10#13#10 + BitLockerManualSteps(1), mbError, MB_OK);
    Exit;
  end;
  if not Exec(ExpandConstant('{sys}\shutdown.exe'), '/r /fw /t 0', '', SW_HIDE, ewWaitUntilTerminated, Code) or
    (Code <> 0) then begin
    Log(Format('Could not restart into the firmware settings (code %d).', [Code]));
    MsgBox(Format('Setup could not restart into the firmware settings (error %d). Your PC may not support ' +
      'it.', [Code]), mbError, MB_OK);
    Exit;
  end;
  Log('Restarting into the firmware settings.');
  ClosingForFirmware := True;
  WizardForm.Close;
end;

// Upgrade page, shown when Windows Test Mode is on: offers to turn it off (and Secure Boot back on), with a BitLocker
// warning when BitLocker is on or Setup could not tell.
procedure CreateLeavePage(const AfterID: Integer);
var
  Text: TNewStaticText;
  Memo: TNewMemo;
  Top: Integer;
begin
  LeavePage := CreateCustomPage(AfterID, 'Leave Test Mode',
    'Windows Test Mode is on. This version of WPods no longer needs it.');
  Text := AddText(LeavePage, 0, 'Older versions of WPods needed Windows Test Mode. If you tick the box, Setup ' +
    'turns it off after it installs WPods; it takes effect when you restart Windows. Leave it on if other software ' +
    'you use needs it.');
  Top := Text.Top + Text.Height + ScaleY(6);
  LeaveTestModeBox := TNewCheckBox.Create(LeavePage);
  LeaveTestModeBox.Parent := LeavePage.Surface;
  LeaveTestModeBox.Top := Top;
  LeaveTestModeBox.Width := LeavePage.SurfaceWidth;
  LeaveTestModeBox.Height := ScaleY(17);
  LeaveTestModeBox.Caption := 'Turn Windows Test Mode off';
  // Checked only when WPods turned it on: otherwise the user (or other software) did.
  LeaveTestModeBox.Checked := TestSigningByWPods;
  LeaveTestModeBox.OnClick := @UpdateLeavePage;
  Top := Top + LeaveTestModeBox.Height + ScaleY(12);

  if not SecureBootOn then begin
    Text := AddText(LeavePage, Top, 'Secure Boot is off too. Setup cannot turn it back on, but you can ' +
      '(if your PC supports it):' + #13#10 +
      '1. Click the button below to restart into your PC''s firmware settings (Test Mode must be off first).' + #13#10 +
      '2. Find Secure Boot (often under Boot or Security) and set it to Enabled.' + #13#10 +
      '3. Save and exit.');
    Top := Text.Top + Text.Height + ScaleY(6);
    FirmwareButton := TNewButton.Create(LeavePage);
    FirmwareButton.Parent := LeavePage.Surface;
    FirmwareButton.Top := Top;
    FirmwareButton.Width := ScaleX(220);
    FirmwareButton.Height := WizardForm.NextButton.Height;
    FirmwareButton.Caption := 'Restart into firmware settings...';
    FirmwareButton.OnClick := @FirmwareButtonClick;
    Top := Top + FirmwareButton.Height + ScaleY(12);
  end;

  if BitLockerStatus <> BitLockerOff then begin
    RecoveryKeyBox := TNewCheckBox.Create(LeavePage);
    RecoveryKeyBox.Parent := LeavePage.Surface;
    RecoveryKeyBox.Height := ScaleY(17);
    RecoveryKeyBox.Top := LeavePage.SurfaceHeight - RecoveryKeyBox.Height;
    RecoveryKeyBox.Width := LeavePage.SurfaceWidth;
    RecoveryKeyBox.Caption := 'I have my BitLocker recovery key or BitLocker is off';
    RecoveryKeyBox.OnClick := @UpdateLeavePage;
    Memo := TNewMemo.Create(LeavePage);
    Memo.Parent := LeavePage.Surface;
    Memo.Top := Top;
    Memo.Width := LeavePage.SurfaceWidth;
    Memo.Height := RecoveryKeyBox.Top - ScaleY(6) - Top;
    Memo.ScrollBars := ssVertical;
    Memo.ReadOnly := True;
    Memo.WantReturns := False;
    Memo.Font.Style := [fsBold];
    if BitLockerStatus = BitLockerOn then
      Memo.Text := 'BitLocker is on for drive ' + ExpandConstant('{sd}') + '.'
    else
      Memo.Text := 'Setup could not confirm that BitLocker is off for drive ' + ExpandConstant('{sd}') + '.';
    Memo.Text := Memo.Text + ' Turning Test Mode off and turning Secure Boot back on both change what BitLocker ' +
      'checks when Windows starts. Windows may then ask for your 48-digit BitLocker recovery key at the next ' +
      'start.' + #13#10#13#10 + 'Get it now from https://aka.ms/myrecoverykey (or your printout or USB key) before ' +
      'you continue. Setup suspends BitLocker before it changes anything, but keep the key at hand.';
  end;
end;

// Needed on an update (or where WPods 0.2.x turned Test Mode on) while Test Mode is on or set. Never when silent: a
// silent run changes nothing and keeps the marker, so Uninstall or a later Setup still offers it.
function NeedsLeavePage: Boolean;
begin
  Result := False;
  if WizardSilent or (FirstInstall and not TestSigningByWPods) then
    Exit;
  TestModeConfigured := TestSigningConfigured;
  Result := TestModeConfigured or TestSigningOn;
end;

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
  BluetoothPage := CreateOutputMsgPage(wpLicense, 'Bluetooth', 'What happens to your Bluetooth.',
    'WPods works through your PC''s Bluetooth. Your other Bluetooth devices (headphones, speakers, mice, ' +
    'keyboards, controllers) keep working as before, and you don''t need to pair them again.' + #13#10#13#10 +
    'While Setup runs, Bluetooth turns off for a short while, and your USB devices (including keyboard and ' +
    'mouse) may disconnect for a moment. If Setup asks you to restart Windows, Bluetooth comes back after the ' +
    'restart.' + #13#10#13#10 +
    'If something goes wrong, or when you uninstall WPods, your Bluetooth goes back to how it was before.' + #13#10#13#10 +
    'Includes usbip-win2 (c) 2021-2026 Vadym Hrynchyshyn, BSD 2-Clause License');
  if NeedsLeavePage then begin
    BitLockerStatus := BitLocker('status', 0);
    CreateLeavePage(BluetoothPage.ID);
  end;
end;

// The Bluetooth adapter page explains the service, so it is shown until the service is registered.
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = BluetoothPage.ID) and ServiceRegistered;
end;

// The PC's Bluetooth adapters: 0 = a USB adapter WPods can use (or none at all), 3 = only adapters not connected over
// USB (UART, SDIO). Same rule as the service (code B3).
function AdapterProblem: Integer;
begin
  if not Exec(PowerShell, '-NoProfile -NonInteractive -Command "' +
    '$r = @(Get-PnpDevice -PresentOnly -Class Bluetooth -ErrorAction Ignore | Where-Object { $_.InstanceId -notmatch ''^BTH'' } | ForEach-Object InstanceId); ' +
    'if (($r.Count -eq 0) -or ($r | Where-Object { $_ -match ''^USB\\VID_'' })) { exit 0 }; exit 3"', '', SW_HIDE,
    ewWaitUntilTerminated, Result) then
    Result := 0;
  Log(Format('Bluetooth adapter check: %d', [Result]));
end;

procedure CurPageChanged(CurPageID: Integer);
var
  Problem: Integer;
  AutoUpdate: Cardinal;
begin
  // An update shows the auto-update choice as it is now (the app may have changed it), not the one Setup remembers.
  if (CurPageID = wpSelectTasks) and not TasksPageShown then begin
    TasksPageShown := True;
    if RegQueryDWordValue(HKLM64, 'SOFTWARE\WPods\Service', 'AutoUpdate', AutoUpdate) then
      if AutoUpdate = 0 then
        WizardSelectTasks('!autoupdate')
      else
        WizardSelectTasks('autoupdate');
  end;
  if LeavePage <> nil then
    if CurPageID = LeavePage.ID then
      UpdateLeavePage(nil);
  // A warning only: Setup goes on either way (a USB dongle plugged in later works).
  if (CurPageID = BluetoothPage.ID) and not AdapterChecked then begin
    AdapterChecked := True;
    Problem := AdapterProblem;
    if Problem = 3 then
      BluetoothPage.MsgLabel.Caption := 'Warning: your PC''s Bluetooth adapter isn''t connected over USB, so WPods ' +
        'can''t work with it (error B3). A USB Bluetooth adapter, such as a small USB dongle, works.' + #13#10#13#10 +
        BluetoothPage.MsgLabel.Caption;
  end;
end;

// The firmware restart closes Setup without the "Exit Setup?" question.
procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if ClosingForFirmware then
    Confirm := False;
end;

// Before any file is copied: installs the .NET 10 Desktop Runtime, WebView2 Runtime and usbip-win2 when missing.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  NeedDotNet, NeedWebView2: Boolean;
begin
  Result := '';
  NeedDotNet := not DotNetInstalled;
  NeedWebView2 := not WebView2Installed;
  if NeedDotNet or NeedWebView2 then begin
    DownloadPage.Clear;
    if NeedDotNet then
      DownloadPage.Add(DotNetUrl, DotNetFile, '');
    if NeedWebView2 then
      DownloadPage.Add(WebView2Url, WebView2File, '');
    DownloadPage.Show;
    try
      try
        DownloadPage.Download;
      except
        if DownloadPage.AbortedByUser then
          Result := 'The download was canceled.'
        else
          Result := 'Setup could not download ' + DownloadPage.LastBaseNameOrUrl + ': ' + GetExceptionMessage;
      end;
      if (Result = '') and NeedDotNet then
        Result := InstallPrerequisite('the .NET 10 Desktop Runtime', DotNetFile, '/install /quiet /norestart');
      if (Result = '') and NeedWebView2 then
        Result := InstallPrerequisite('the Microsoft Edge WebView2 Runtime', WebView2File, '/silent /install');
    finally
      DownloadPage.Hide;
    end;
  end;
  if (Result = '') and not UsbipInstalled then
    Result := InstallUsbip;
end;

// Ends WPods.exe from this install folder if Restart Manager did not close it.
procedure StopWPods;
var
  Code: Integer;
begin
  Exec(PowerShell, '-NoProfile -NonInteractive -Command "Get-Process WPods -ErrorAction Ignore | ' +
    'Where-Object Path -eq ' + PSString(ExpandConstant('{app}\WPods.exe')) + ' | ' +
    'ForEach-Object { $_.Kill(); $_.WaitForExit(10000) | Out-Null }"', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

// WPods 0.2.x left its driver packages or their record behind (drivers.ps1 removes them once it succeeds).
function LegacyDriversLeft: Boolean;
begin
  Result := DirExists(ExpandConstant('{app}\drivers\NTAPD')) or DirExists(ExpandConstant('{app}\drivers\NTA2DPVM')) or
    FileExists(ExpandConstant('{app}\drivers\installed.txt'));
end;

procedure RemoveLegacyDrivers;
var
  Code: Integer;
begin
  WizardForm.StatusLabel.Caption := 'Removing the drivers older versions of WPods used...';
  if not ExecAndLogOutput(PowerShell, '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{app}\drivers\drivers.ps1') + '" -Uninstall', '', SW_HIDE, ewWaitUntilTerminated, Code, nil) then
    SuppressibleMsgBox('Setup could not start removing the drivers older versions of WPods used: ' +
      SysErrorMessage(Code) + #13#10#13#10 + 'Run Setup again to retry.', mbError, MB_OK, IDOK)
  else if Code = 3010 then
    Log('Old WPods drivers removed; Windows finishes removing them at the next restart.')
  else if Code <> 0 then
    SuppressibleMsgBox(Format('Setup could not remove all the drivers older versions of WPods used (error %d). ' +
      'Run Setup again to retry. Details are in the setup log:' + #13#10 + '%s', [Code,
      ExpandConstant('{log}')]), mbError, MB_OK, IDOK);
end;

// Updates from before 0.4.3: AirPods without the stereo mic get Hands-Free mode back and lose the WPods mic made for
// them (stereo-mic.ps1). Runs while the service is stopped and the app closed; a failure only leaves them as they were.
procedure UpdateStereoMicPairs;
var
  Code: Integer;
begin
  if PreviousVersion = '' then
    Exit;
  ExtractTemporaryFile('stereo-mic.ps1');
  if not ExecAndLogOutput(PowerShell, '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{tmp}\stereo-mic.ps1') + '" -PreviousVersion ' + AddQuotes(PreviousVersion), '', SW_HIDE,
    ewWaitUntilTerminated, Code, nil) then
    Log('Could not run stereo-mic.ps1: ' + SysErrorMessage(Code))
  else if Code <> 0 then
    Log(Format('stereo-mic.ps1 exit code: %d', [Code]));
end;

// A failure keeps the app installed. Until the restart usbip-win2 needs, the service only starts with Windows, as a
// takeover before it would leave every Bluetooth device without a working adapter.
procedure InstallService;
var
  Code: Integer;
  Params: String;
begin
  WizardForm.StatusLabel.Caption := 'Setting up the Bluetooth adapter...';
  Params := '--install';
  if UsbipJustInstalled or ServiceWaitsForRestart then
    Params := '--install --no-start';
  if not ExecAndLogOutput(ExpandConstant('{app}\WPods.Service.exe'), Params, '', SW_HIDE, ewWaitUntilTerminated,
    Code, nil) then
    SuppressibleMsgBox('WPods was installed, but Setup could not start setting up its background service: ' +
      SysErrorMessage(Code) + #13#10#13#10 + 'Run Setup again to retry.', mbError, MB_OK, IDOK)
  else if Code <> 0 then
    SuppressibleMsgBox(Format('WPods was installed, but its background service could not be set up (error %d). ' +
      'WPods cannot connect to your AirPods until it is.' + #13#10#13#10 +
      'Run Setup again to retry. Details are in the setup log:' + #13#10 + '%s', [Code, ExpandConstant('{log}')]),
      mbError, MB_OK, IDOK);
end;

// The service reads this value and the app's switch changes it; a silent update keeps the current choice.
procedure SaveAutoUpdate;
var
  Value: Cardinal;
begin
  if not TasksPageShown and RegQueryDWordValue(HKLM64, 'SOFTWARE\WPods\Service', 'AutoUpdate', Value) then
    Exit;
  if WizardIsTaskSelected('autoupdate') then
    Value := 1
  else
    Value := 0;
  if not RegWriteDWordValue(HKLM64, 'SOFTWARE\WPods\Service', 'AutoUpdate', Value) then
    Log('Could not write HKLM\SOFTWARE\WPods\Service\AutoUpdate.');
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then begin
    StopWPods;
    LogBluetoothDevices('install: before');
    if not MoveServiceFilesAside then
      StopService;
  end else if CurStep = ssPostInstall then begin
    FilesInstalled := True;
    // Old drivers go before Test Mode does: a test-signed driver left behind would fail to load without it.
    if LegacyDriversLeft then
      RemoveLegacyDrivers;
    // The old service still runs from the renamed files: restart it as the new one.
    StopService;
    UpdateStereoMicPairs;
    InstallService;
    if LeavePage <> nil then
      if LeaveTestModeBox.Checked and not TestModeTurnedOff then begin
        WizardForm.StatusLabel.Caption := 'Turning Windows Test Mode off...';
        LeaveTestMode(True);
      end;
    if not FromService then
      SaveAutoUpdate;
  end;
end;

// A cancelled or failed install rolled back the new service files: the old service keeps running from the files moved
// aside, and gets them back under their names, or it would not start again after a restart.
procedure DeinitializeSetup;
begin
  if not FilesInstalled then
    MoveServiceFilesBack;
end;

// Offers the restart usbip-win2 needs before Bluetooth works through WPods; never for an update the service runs.
function NeedRestart: Boolean;
begin
  Result := (UsbipJustInstalled or ServiceWaitsForRestart) and not FromService;
end;

// Keeps the service running until the end of uninstall: its files are copied to {tmp}\svc, which UninstallService
// runs at the end, and the open ones moved aside. False when that failed; the service is then removed first.
function KeepServiceForLater: Boolean;
var
  Names: TArrayOfString;
  I: Integer;
  Dir: String;
begin
  Result := FileExists(ExpandConstant('{app}\WPods.Service.exe'));
  if not Result then
    Exit;
  Dir := ExpandConstant('{tmp}\svc');
  ForceDirectories(Dir);
  Names := ServiceFiles;
  SetArrayLength(Names, GetArrayLength(Names) + 2);
  Names[GetArrayLength(Names) - 2] := 'WPods.Service.deps.json';
  Names[GetArrayLength(Names) - 1] := 'WPods.Service.runtimeconfig.json';
  for I := 0 to GetArrayLength(Names) - 1 do
    if FileExists(ExpandConstant('{app}\') + Names[I]) and
      not FileCopy(ExpandConstant('{app}\') + Names[I], Dir + '\' + Names[I], False) then begin
      Log('Could not copy ' + Names[I] + ' for the late service removal.');
      Result := False;
      Exit;
    end;
  Result := MoveServiceFilesAside;
end;

// Stops and deletes the WPods service, which gives the Bluetooth adapter back to Windows' own driver. If that fails,
// it still tries to give the adapter back (--restore-native). AdapterRestored stays False when both fail.
procedure UninstallService(const Exe: String);
var
  Code, RestoreCode: Integer;
begin
  AdapterRestored := True;
  if not FileExists(Exe) then begin
    // No service program but a registered service: nothing here can give the adapter back.
    AdapterRestored := not ServiceRegistered;
    if not AdapterRestored then begin
      Log('WPods.Service.exe is missing but the service is registered: the adapter cannot be given back.');
      SuppressibleMsgBox('Uninstall could not give your Bluetooth adapter back to Windows: a WPods file is missing. ' +
        'It kept usbip-win2. Install WPods again, then uninstall it.', mbError, MB_OK, IDOK);
    end;
    Exit;
  end;
  if Exec(Exe, '--uninstall', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = 0) then
    Exit;
  Log(Format('WPods.Service.exe --uninstall failed (code %d).', [Code]));
  if not Exec(Exe, '--restore-native', '', SW_HIDE, ewWaitUntilTerminated, RestoreCode) then
    RestoreCode := 1;
  AdapterRestored := RestoreCode = 0;
  if AdapterRestored then
    SuppressibleMsgBox(Format('Uninstall could not remove the WPods background service (error %d), but it gave your ' +
      'Bluetooth adapter back to Windows.', [Code]), mbError, MB_OK, IDOK)
  else begin
    Log(Format('WPods.Service.exe --restore-native failed (code %d).', [RestoreCode]));
    SuppressibleMsgBox(Format('Uninstall could not give your Bluetooth adapter back to Windows (error %d), so it ' +
      'kept usbip-win2. If Bluetooth does not work after you restart Windows, install WPods again.', [Code]),
      mbError, MB_OK, IDOK);
  end;
end;

// Removes usbip-win2 when Setup installed it, and only once the adapter is back on Windows' own driver.
procedure UninstallUsbip;
var
  Code: Integer;
  Uninstaller: String;
begin
  if not WPodsFlag('UsbipInstalledByWPods') then
    Exit;
  if not AdapterRestored then begin
    UsbipKept := True;
    Exit;
  end;
  Uninstaller := ExpandConstant('{commonpf64}\USBip\unins000.exe');
  if not FileExists(Uninstaller) then
    Exit;
  UsbipRemoved := Exec(Uninstaller, '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART', '', SW_HIDE, ewWaitUntilTerminated,
    Code) and (Code = 0);
  if not UsbipRemoved then begin
    UsbipKept := True;
    Log(Format('Could not uninstall usbip-win2 (code %d).', [Code]));
    SuppressibleMsgBox(Format('Uninstall could not remove usbip-win2 (error %d). You can remove it yourself in ' +
      'Windows Settings, Apps.', [Code]), mbError, MB_OK, IDOK);
  end;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Startup: String;
  ReplugCode: Integer;
begin
  if CurUninstallStep = usUninstall then begin
    RemoveSettings := SuppressibleMsgBox('Also remove your WPods settings?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2,
      IDNO) = IDYES;
    StopWPods;
    LogBluetoothDevices('uninstall: before');
    // The service (and Bluetooth through it) keeps running until the end; if its files cannot be kept for that, it
    // goes now, before [UninstallRun] and the files.
    ServiceKeptForLater := KeepServiceForLater;
    if not ServiceKeptForLater then
      UninstallService(ExpandConstant('{app}\WPods.Service.exe'));
  end else if CurUninstallStep = usPostUninstall then begin
    if ServiceKeptForLater then begin
      UninstallService(ExpandConstant('{tmp}\svc\WPods.Service.exe'));
      DeleteServiceFilesAside;
    end;
    LogBluetoothDevices('uninstall: adapter back on its own driver');
    // {localappdata} and HKCU belong to the account Uninstall runs as (the signed-in user when they approve the
    // UAC prompt themselves; the admin's when another account elevates it).
    if RegQueryStringValue(HKCU, RunKey, 'WPods', Startup) and
      (Pos(Lowercase(ExpandConstant('{app}\WPods.exe')), Lowercase(Startup)) > 0) then
      RegDeleteValue(HKCU, RunKey, 'WPods');
    if RemoveSettings then begin
      DelTree(ExpandConstant('{localappdata}\WPods'), True, True, True);
      RegDeleteValue(HKCU, 'SOFTWARE\WPods', 'AutoUpdate');
      RegDeleteKeyIfEmpty(HKCU, 'SOFTWARE\WPods');
    end;
    UninstallUsbip;
    // The native driver sets paired devices up while usbip-win2 still removes the virtual adapter's nodes, which can
    // leave one failed ("driver error"); a re-plug sets them all up again.
    if UsbipRemoved and ServiceKeptForLater then
      Exec(ExpandConstant('{tmp}\svc\WPods.Service.exe'), '--replug', '', SW_HIDE, ewWaitUntilTerminated, ReplugCode);
    LogBluetoothDevices('uninstall: after usbip-win2');
    // Offer to undo the Test Mode WPods 0.2.x turned on (its drivers are gone by now).
    if TestSigningByWPods then begin
      if SuppressibleMsgBox('Turn Windows Test Mode off as well? This takes effect after you restart Windows.',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2,
        IDNO) = IDYES then
        TurnTestSigningOff(True);
    end;
    // Drop the marker key whatever the answer; keep only the usbip-win2 record while usbip-win2 stays.
    if UsbipKept then
      DeleteTestSigningMarker
    else
      RegDeleteKeyIncludingSubkeys(HKLM64, WPodsKey);
  end;
end;
