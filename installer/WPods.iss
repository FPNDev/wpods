; WPods installer (Inno Setup 6). Built by ..\build.ps1, which passes /DAppVersion.
#ifndef AppVersion
  #error AppVersion is not defined: build with build.ps1
#endif

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
DisableDirPage=yes
DisableProgramGroupPage=yes
PrivilegesRequired=admin
ArchitecturesAllowed=x64os
ArchitecturesInstallIn64BitMode=x64os
MinVersion=10.0.19041
SetupIconFile=..\src\WPods\Assets\WPods.ico
WizardStyle=modern
Compression=lzma2/max
SolidCompression=yes
; Restart Manager closes WPods gracefully; it must not restart it (it would run elevated).
CloseApplications=yes
RestartApplications=no
; Never offer a reboot: drivers.ps1 reports a pending restart itself.
RestartIfNeededByRun=no
SetupLogging=yes
OutputDir=..\dist
OutputBaseFilename=WPods-Setup

[Tasks]
Name: desktopicon; Description: "{cm:CreateDesktopIcon}"; GroupDescription: "{cm:AdditionalIcons}"; Flags: unchecked
Name: startup; Description: "Start WPods when I sign in"; Check: IsFirstInstall

[InstallDelete]
; Folders fully owned by the app: drop files a previous version had (e.g. a removed locale would stay selectable).
Type: filesandordirs; Name: "{app}\web"
Type: filesandordirs; Name: "{app}\locales"
Type: filesandordirs; Name: "{app}\licenses"

[Files]
Source: "..\.build\publish\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs
Source: "..\.build\drivers\NTAPD\*"; DestDir: "{app}\drivers\NTAPD"; Flags: ignoreversion
Source: "..\.build\drivers\NTA2DPVM\*"; DestDir: "{app}\drivers\NTA2DPVM"; Flags: ignoreversion
Source: "drivers.ps1"; DestDir: "{app}\drivers"; Flags: ignoreversion

[UninstallDelete]
Type: files; Name: "{app}\drivers\installed.txt"

[Icons]
Name: "{autoprograms}\WPods"; Filename: "{app}\WPods.exe"
Name: "{autodesktop}\WPods"; Filename: "{app}\WPods.exe"; Tasks: desktopicon

[Run]
; The app writes the HKCU Run value itself, as the user who started Setup (never the elevating admin).
Filename: "{app}\WPods.exe"; Parameters: "--register-startup"; Tasks: startup; Check: IsFirstInstall; Flags: runasoriginaluser waituntilterminated
Filename: "{app}\WPods.exe"; Description: "{cm:LaunchProgram,WPods}"; Flags: postinstall runasoriginaluser nowait

[UninstallRun]
Filename: "{sys}\WindowsPowerShell\v1.0\powershell.exe"; Parameters: "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File ""{app}\drivers\drivers.ps1"" -Uninstall"; Flags: runhidden waituntilterminated logoutput; RunOnceId: "Drivers"

[Code]
const
  DotNetUrl = 'https://aka.ms/dotnet/10.0/windowsdesktop-runtime-win-x64.exe';
  DotNetFile = 'windowsdesktop-runtime-win-x64.exe';
  WebView2Url = 'https://go.microsoft.com/fwlink/p/?LinkId=2124703';
  WebView2File = 'MicrosoftEdgeWebview2Setup.exe';
  WebView2Key = 'SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}';
  RunKey = 'Software\Microsoft\Windows\CurrentVersion\Run';
  // Exit code of RunMicrosoftInstaller when the file is not validly signed by Microsoft Corporation.
  NotMicrosoftSigned = 22352;
  SystemCodeIntegrityInformation = 103;
  CodeIntegrityOptionTestSign = 2;
  // Exit codes of the BitLocker script: not protected (or done), suspended now by this call, protected.
  BitLockerOff = 0;
  BitLockerSuspended = 10;
  BitLockerOn = 11;
  // Exit code of the TestSigningConfigured script when {current} has testsigning set.
  BcdTestSigningSet = 10;

type
  TCodeIntegrityInformation = record
    Length: Cardinal;
    Options: Cardinal;
  end;

var
  DownloadPage: TDownloadWizardPage;
  SecureBootPage: TWizardPage;
  RemoveSettings: Boolean;
  FirstInstall: Boolean;
  // BitLocker on the OS drive was suspended by this run (so only this run may resume it).
  BitLockerSuspendedByRun: Boolean;
  // This run changed the boot configuration (testsigning on), so DeinitializeSetup undoes it if Setup stops early.
  TestSigningSetByRun: Boolean;
  InstallFinished: Boolean;

function InitializeSetup: Boolean;
begin
  // Read once, before this install registers itself: an update must not touch the user's startup choice.
  FirstInstall := not RegKeyExists(HKLM64,
    'Software\Microsoft\Windows\CurrentVersion\Uninstall\{08E1B0A8-4D58-46F6-B9EC-006F5CC3A69F}_is1');
  Result := True;
end;

// Startup is owned by the app; Setup offers it only on a first install.
function IsFirstInstall: Boolean;
begin
  Result := FirstInstall;
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

// Secure Boot blocks test signing. HKLM 64-bit view; a missing value or 0 means off or a legacy BIOS.
function SecureBootOn: Boolean;
var
  Value: Cardinal;
begin
  Result := RegQueryDWordValue(HKLM64, 'SYSTEM\CurrentControlSet\Control\SecureBoot\State',
    'UEFISecureBootEnabled', Value) and (Value = 1);
end;

// Test signing cannot be turned on while Secure Boot is on, and there is nothing to do when it already runs.
function SecureBootBlocksInstall: Boolean;
begin
  Result := SecureBootOn and not TestSigningOn;
end;

// Runs Action ('status', 'suspend' or 'resume') for BitLocker (or Device encryption) on the OS drive through WMI
// Win32_EncryptableVolume, which needs no BitLocker PowerShell module (missing on some Windows Home installs).
// Returns the script exit code (BitLockerOff, BitLockerSuspended, BitLockerOn), any other value is a failure, -1 when
// PowerShell could not run. Changing testsigning or Secure Boot while BitLocker protects the drive makes Windows ask
// for the recovery key, so callers treat every failure as "may be protected".
function BitLocker(const Action: String): Integer;
var
  Code: Integer;
begin
  if not Exec(PowerShell, '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' +
    '$ErrorActionPreference = ''Stop''; try { ' +
    '$v = Get-CimInstance -Namespace root/CIMV2/Security/MicrosoftVolumeEncryption -ClassName Win32_EncryptableVolume | ' +
    'Where-Object DriveLetter -eq $env:SystemDrive; if (-not $v) { exit 0 }; $a = ' + PSString(Action) + '; ' +
    'if ($a -eq ''resume'') { ' +
    'if ((Invoke-CimMethod -InputObject $v -MethodName EnableKeyProtectors).ReturnValue -eq 0) { exit 0 }; exit 1 }; ' +
    '$p = (Invoke-CimMethod -InputObject $v -MethodName GetProtectionStatus).ProtectionStatus; ' +
    'if ($p -eq 0) { exit ' + IntToStr(BitLockerOff) + ' }; if ($p -ne 1) { exit 1 }; ' +
    'if ($a -eq ''status'') { exit ' + IntToStr(BitLockerOn) + ' }; ' +
    'if ((Invoke-CimMethod -InputObject $v -MethodName DisableKeyProtectors ' +
    '-Arguments @{DisableCount = [uint32]1}).ReturnValue -ne 0) { exit 1 }; ' +
    'if ((Invoke-CimMethod -InputObject $v -MethodName GetProtectionStatus).ProtectionStatus -ne 0) { exit 1 }; ' +
    'exit ' + IntToStr(BitLockerSuspended) + ' } catch { exit 1 }"', '', SW_HIDE, ewWaitUntilTerminated, Code) then begin
    Log('Could not run PowerShell for BitLocker ' + Action + ': ' + SysErrorMessage(Code));
    Code := -1;
  end;
  Result := Code;
end;

// True only when the OS drive is not protected (off or already suspended) or this call suspended it for the next
// restart. False on any error or doubt.
function SuspendBitLocker: Boolean;
var
  Code: Integer;
begin
  Code := BitLocker('suspend');
  Result := (Code = BitLockerOff) or (Code = BitLockerSuspended);
  if Code = BitLockerSuspended then begin
    BitLockerSuspendedByRun := True;
    Log('BitLocker suspended on the OS drive until the next restart.');
  end else if Code = BitLockerOff then
    Log('BitLocker does not protect the OS drive (off or already suspended).')
  else
    Log(Format('Could not confirm that BitLocker is off or suspended on the OS drive (exit code %d).', [Code]));
end;

// Resumes BitLocker only when this run suspended it. False when it stays suspended (until the next restart).
function ResumeBitLocker: Boolean;
begin
  Result := True;
  if not BitLockerSuspendedByRun then
    Exit;
  Result := BitLocker('resume') = BitLockerOff;
  if Result then begin
    BitLockerSuspendedByRun := False;
    Log('BitLocker resumed on the OS drive.');
  end else
    Log('Could not resume BitLocker on the OS drive; it stays suspended until the next restart.');
end;

// How to suspend BitLocker for one restart by hand, for every case Setup cannot do it safely itself.
function BitLockerManualSteps: String;
begin
  Result := 'To suspend BitLocker (or Device encryption) for the next restart, open Terminal (Admin) and run:' + #13#10 +
    'manage-bde -protectors -disable ' + ExpandConstant('{sd}') + ' -RebootCount 1' + #13#10#13#10 +
    'Keep your BitLocker recovery key at hand: https://aka.ms/myrecoverykey';
end;

// Boot configuration, not the running state: True when {current} already has testsigning set (it applies after the
// next restart). Read through WMI BcdStore; a failed read counts as not set, which takes the checked path.
function TestSigningConfigured: Boolean;
var
  Code: Integer;
begin
  Result := Exec(PowerShell, '-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command "' +
    '$ErrorActionPreference = ''Stop''; try { ' +
    '$s = ([wmiclass]''root\wmi:BcdStore'').OpenStore('''').Store; ' +
    '$o = $s.OpenObject(''{fa926493-6f1c-4193-a414-58f0b2456d1e}'').Object; ' +
    '$e = $o.GetElement(0x16000049); ' +
    'if ($e.ReturnValue -and $e.Element.Boolean) { exit ' + IntToStr(BcdTestSigningSet) + ' }; exit 0 ' +
    '} catch { exit 0 }"', '', SW_HIDE, ewWaitUntilTerminated, Code) and (Code = BcdTestSigningSet);
end;

// Turns Windows test signing on before any file is copied. Returns '' on success, else a specific error to abort
// Setup with (nothing is installed on failure). The boot configuration is changed only when the OS drive is confirmed
// not BitLocker-protected or was suspended by this run. On success it records that Setup, not the user, made the change.
function EnableTestSigning: String;
var
  Code: Integer;
  Failure: String;
begin
  Result := '';
  if SecureBootOn then begin
    Result := 'Setup could not turn on Windows test signing: Secure Boot is on and must be turned off ' +
      'first.' + #13#10#13#10 + 'No changes were made and nothing was installed.';
    Exit;
  end;
  if TestSigningConfigured then begin
    Log('Windows test signing is already set in the boot configuration; it takes effect after a restart.');
    Exit;
  end;
  if not SuspendBitLocker then begin
    Result := 'Setup could not confirm that BitLocker (or Device encryption) is off or suspended on drive ' +
      ExpandConstant('{sd}') + ', so it did not turn on Windows test signing. Changing it while BitLocker protects ' +
      'the drive makes Windows ask for the BitLocker recovery key. Nothing was installed.' + #13#10#13#10 +
      BitLockerManualSteps + #13#10#13#10 + 'Then run Setup again.';
    Exit;
  end;
  if not Exec(ExpandConstant('{sys}\bcdedit.exe'), '/set testsigning on', '', SW_HIDE, ewWaitUntilTerminated, Code) then
    Failure := 'Setup could not run bcdedit to turn on Windows test signing: ' + SysErrorMessage(Code)
  else if Code <> 0 then
    Failure := Format('Setup could not turn on Windows test signing (bcdedit exit code %d).', [Code])
  else begin
    TestSigningSetByRun := True;
    Log('Windows test signing turned on; a restart is required for it to take effect.');
    // Marker so Uninstall can offer to turn test mode back off. RegWriteDWordValue creates SOFTWARE\WPods if needed.
    if not RegWriteDWordValue(HKLM64, 'SOFTWARE\WPods', 'TestSigningByWPods', 1) then
      Log('Could not record HKLM64\SOFTWARE\WPods\TestSigningByWPods.');
    Exit;
  end;
  // bcdedit failed, so the boot configuration is unchanged: undo this run's suspend.
  if ResumeBitLocker then
    Result := Failure + #13#10#13#10 + 'No changes were made and nothing was installed.'
  else
    Result := Failure + #13#10#13#10 + 'Nothing was installed. BitLocker stays suspended until the next restart.';
end;

// Full wizard page, shown (only when blocked) right after the license: Secure Boot is on, so the drivers cannot load.
procedure CreateSecureBootPage;
var
  Memo: TNewMemo;
begin
  SecureBootPage := CreateCustomPage(wpLicense, 'Secure Boot is on',
    'WPods cannot be installed until Secure Boot is turned off.');
  Memo := TNewMemo.Create(SecureBootPage);
  Memo.Parent := SecureBootPage.Surface;
  Memo.Left := 0;
  Memo.Top := 0;
  Memo.Width := SecureBootPage.SurfaceWidth;
  Memo.Height := SecureBootPage.SurfaceHeight;
  Memo.ScrollBars := ssVertical;
  Memo.ReadOnly := True;
  Memo.WantReturns := False;
  Memo.Text :=
    'WPods uses test-signed drivers, which Windows loads only when test signing is on. Test signing requires ' +
    'Secure Boot to be turned OFF, and Secure Boot is currently on.' + #13#10#13#10 +
    'Before you change Secure Boot: if BitLocker or Device encryption is on, it must be suspended first, or Windows ' +
    'asks for the BitLocker recovery key at the next start. When you click Exit Setup, Setup offers to suspend it ' +
    'for the next restart only. Make sure you can get your recovery key: https://aka.ms/myrecoverykey' + #13#10#13#10 +
    'To turn Secure Boot off:' + #13#10 +
    '1. Restart straight into the UEFI firmware settings (BIOS). Do not restart in between: the suspend lasts one ' +
    'restart only.' + #13#10 +
    '2. Find the Secure Boot option (usually under a Boot, Security, or Authentication section).' + #13#10 +
    '3. Set it to Disabled, then save and exit.' + #13#10#13#10 +
    'After Windows starts again, run WPods Setup once more.' + #13#10#13#10 +
    'Click Exit Setup to exit.';
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

// Runs a downloaded installer only if its Authenticode signature is Valid and the signer's organization is exactly
// "Microsoft Corporation" (O= RDN; the .NET runtime installer is signed as CN=.NET, O=Microsoft Corporation).
// The file stays open with write and delete denied from the check until the installer exits, so nothing can swap
// it in between.
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

procedure InitializeWizard;
begin
  DownloadPage := CreateDownloadPage(SetupMessage(msgWizardPreparing), SetupMessage(msgPreparingDesc), nil);
  DownloadPage.ShowBaseNameInsteadOfUrl := True;
  CreateSecureBootPage;
end;

// The Secure Boot block page (inserted after the license) appears only when Secure Boot blocks the install.
function ShouldSkipPage(PageID: Integer): Boolean;
begin
  Result := (PageID = SecureBootPage.ID) and not SecureBootBlocksInstall;
end;

procedure CurPageChanged(CurPageID: Integer);
begin
  // The block page is terminal: Back is removed and Next becomes an exit, so Setup can never reach the install.
  if CurPageID = SecureBootPage.ID then begin
    WizardForm.BackButton.Visible := False;
    WizardForm.NextButton.Caption := 'Exit Setup';
  end;
end;

function NextButtonClick(CurPageID: Integer): Boolean;
var
  Code: Integer;
begin
  Result := CurPageID <> SecureBootPage.ID;
  if Result then
    Exit;
  // On the block page the button exits Setup instead of advancing: the drivers cannot load with Secure Boot on.
  Log('Secure Boot is on; Setup cannot continue.');
  // A silent install exits by itself when this returns False before the install.
  if WizardSilent then
    Exit;
  // Turning Secure Boot off while BitLocker protects the drive makes Windows ask for the recovery key: offer to
  // suspend it for the one restart the user needs. It is never resumed here (that restart is the point).
  Code := BitLocker('status');
  if Code = BitLockerOn then begin
    if MsgBox('BitLocker (or Device encryption) protects drive ' + ExpandConstant('{sd}') + '. Turning Secure Boot ' +
      'off makes Windows ask for the BitLocker recovery key at the next start, unless BitLocker is suspended ' +
      'first.' + #13#10#13#10 + 'Suspend BitLocker now, for the next restart only?',
      mbConfirmation, MB_YESNO) = IDYES then begin
      if SuspendBitLocker then
        MsgBox('BitLocker is suspended until the next restart only. Turn Secure Boot off during that restart. ' +
          'BitLocker turns back on by itself after Windows starts.', mbInformation, MB_OK)
      else
        MsgBox('Setup could not suspend BitLocker.' + #13#10#13#10 + BitLockerManualSteps, mbError, MB_OK);
    end;
  end else if Code <> BitLockerOff then
    MsgBox('Setup could not check whether BitLocker (or Device encryption) protects drive ' + ExpandConstant('{sd}') +
      '. If it is on, suspend it before you turn Secure Boot off, or Windows asks for the BitLocker recovery ' +
      'key.' + #13#10#13#10 + BitLockerManualSteps, mbError, MB_OK);
  WizardForm.Close;
end;

// The block page exits without the "Exit Setup?" confirmation.
procedure CancelButtonClick(CurPageID: Integer; var Cancel, Confirm: Boolean);
begin
  if CurPageID = SecureBootPage.ID then
    Confirm := False;
end;

// Before any file is copied: installs the .NET 10 Desktop Runtime and WebView2 Runtime when missing, then turns on
// Windows test signing when it is off (the test-signed drivers load after the required restart).
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
  // EnableTestSigning checks Secure Boot and BitLocker itself and changes nothing when it fails.
  if (Result = '') and not TestSigningOn then
    Result := EnableTestSigning;
end;

// Ends WPods.exe from this install folder if Restart Manager did not close it (it holds the NTAPD driver).
procedure StopWPods;
var
  Code: Integer;
begin
  Exec(PowerShell, '-NoProfile -NonInteractive -Command "Get-Process WPods -ErrorAction Ignore | ' +
    'Where-Object Path -eq ' + PSString(ExpandConstant('{app}\WPods.exe')) + ' | ' +
    'ForEach-Object { $_.Kill(); $_.WaitForExit(10000) | Out-Null }"', '', SW_HIDE, ewWaitUntilTerminated, Code);
end;

procedure InstallDrivers;
var
  Code: Integer;
begin
  WizardForm.StatusLabel.Caption := 'Installing the WPods drivers...';
  if not ExecAndLogOutput(PowerShell, '-NoProfile -NonInteractive -ExecutionPolicy Bypass -File "' +
    ExpandConstant('{app}\drivers\drivers.ps1') + '" -Install', '', SW_HIDE, ewWaitUntilTerminated, Code, nil) then
    SuppressibleMsgBox('WPods was installed, but Setup could not start the driver installation: ' +
      SysErrorMessage(Code) + #13#10#13#10 + 'Run Setup again to retry.', mbError, MB_OK, IDOK)
  else if (Code = 0) or (Code = 3010) then begin
    // Test signing reads off only because it is set in the boot configuration (by Setup this run, or already): it
    // takes effect after a restart, and the drivers load then. When it was already on, keep the lighter 3010
    // "finish updating" note.
    if not TestSigningOn then
      SuppressibleMsgBox('WPods and its drivers were installed. Windows test mode turns on when Windows ' +
        'restarts.' + #13#10#13#10 +
        'You must restart Windows to finish. The drivers load, and WPods can connect to your ' +
        'AirPods, only after you restart.', mbInformation, MB_OK, IDOK)
    else if Code = 3010 then
      SuppressibleMsgBox('The WPods drivers were installed. Restart Windows when convenient to finish updating them.',
        mbInformation, MB_OK, IDOK);
  end
  else
    SuppressibleMsgBox(Format('WPods was installed, but its drivers could not be installed (error %d). ' +
      'WPods cannot connect to your AirPods until they are.' + #13#10#13#10 +
      'Run Setup again to retry. Details are in the setup log:' + #13#10 + '%s', [Code, ExpandConstant('{log}')]),
      mbError, MB_OK, IDOK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    StopWPods
  else if CurStep = ssPostInstall then begin
    // Files are in place: from here DeinitializeSetup keeps the test signing this run turned on.
    InstallFinished := True;
    InstallDrivers;
  end;
end;

// Setup stopped (canceled or failed) after turning test signing on but before the install finished: undo it.
procedure DeinitializeSetup;
var
  Code: Integer;
  Msg: String;
begin
  if TestSigningSetByRun and not InstallFinished then begin
    if Exec(ExpandConstant('{sys}\bcdedit.exe'), '/set testsigning off', '', SW_HIDE, ewWaitUntilTerminated, Code) and
      (Code = 0) then begin
      ResumeBitLocker;
      RegDeleteValue(HKLM64, 'SOFTWARE\WPods', 'TestSigningByWPods');
      RegDeleteKeyIfEmpty(HKLM64, 'SOFTWARE\WPods');
      Log('Setup did not finish; Windows test signing turned back off.');
    end else begin
      // The boot configuration may still have testsigning on: BitLocker must stay suspended until the next restart.
      Log(Format('Setup did not finish and could not turn Windows test signing back off (code %d).', [Code]));
      Msg := 'Setup stopped after turning on Windows test mode and could not turn it back off.';
      if BitLockerSuspendedByRun then
        Msg := Msg + ' BitLocker stays suspended until the next restart, so before restarting, open Terminal ' +
          '(Admin) and run:'
      else
        Msg := Msg + ' Before restarting, open Terminal (Admin) and run:';
      SuppressibleMsgBox(Msg + #13#10 + 'bcdedit /set testsigning off', mbError, MB_OK, IDOK);
    end;
  end;
end;

// Turns Windows test signing off (it takes effect after a restart), only when the OS drive is confirmed not
// BitLocker-protected or was suspended by this run: changing it while BitLocker protects the drive makes Windows ask
// for the recovery key.
procedure TurnTestSigningOff;
var
  Code: Integer;
  Manual: String;
begin
  Manual := 'To turn test mode off yourself:' + #13#10#13#10 + BitLockerManualSteps + #13#10#13#10 +
    'Then run: bcdedit /set testsigning off' + #13#10 + 'Then restart Windows.';
  if not SuspendBitLocker then begin
    SuppressibleMsgBox('Windows test mode was left on: Uninstall could not confirm that BitLocker (or Device ' +
      'encryption) is off or suspended on drive ' + ExpandConstant('{sd}') + '. Turning test mode off while ' +
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
    Log('Windows test signing turned off; it takes effect after a restart.');
    SuppressibleMsgBox('Windows test mode turns off when you restart Windows.' + #13#10#13#10 +
      'If you turn Secure Boot back on later, suspend BitLocker first, or Windows asks for the BitLocker ' +
      'recovery key.' + #13#10#13#10 + BitLockerManualSteps, mbInformation, MB_OK, IDOK);
    Exit;
  end;
  // bcdedit failed, so the boot configuration is unchanged: undo this run's suspend.
  ResumeBitLocker;
  SuppressibleMsgBox('Uninstall could not turn Windows test mode off.' + #13#10#13#10 + Manual, mbError, MB_OK, IDOK);
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Startup: String;
  Flag: Cardinal;
begin
  if CurUninstallStep = usUninstall then begin
    RemoveSettings := SuppressibleMsgBox('Also remove your WPods settings?', mbConfirmation, MB_YESNO or MB_DEFBUTTON2,
      IDNO) = IDYES;
    StopWPods;
  end else if CurUninstallStep = usPostUninstall then begin
    // {localappdata} and HKCU belong to the account Uninstall runs as (the signed-in user when they approve the
    // UAC prompt themselves; the admin's when another account elevates it).
    if RegQueryStringValue(HKCU, RunKey, 'WPods', Startup) and
      (Pos(Lowercase(ExpandConstant('{app}\WPods.exe')), Lowercase(Startup)) > 0) then
      RegDeleteValue(HKCU, RunKey, 'WPods');
    if RemoveSettings then
      DelTree(ExpandConstant('{localappdata}\WPods'), True, True, True);
    // Offer to undo the test mode Setup turned on, then drop the marker key whatever the answer.
    if RegQueryDWordValue(HKLM64, 'SOFTWARE\WPods', 'TestSigningByWPods', Flag) and (Flag = 1) then begin
      if SuppressibleMsgBox('Turn Windows test mode off as well? This takes effect after you restart Windows.',
        mbConfirmation, MB_YESNO or MB_DEFBUTTON2,
        IDNO) = IDYES then
        TurnTestSigningOff;
    end;
    RegDeleteKeyIncludingSubkeys(HKLM64, 'SOFTWARE\WPods');
  end;
end;
