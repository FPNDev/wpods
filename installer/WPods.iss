; WPods installer (Inno Setup 6). Built by ..\build.ps1, which passes /DAppVersion.
#ifndef AppVersion
  #error AppVersion is not defined: build with build.ps1
#endif

[Setup]
AppId={{08E1B0A8-4D58-46F6-B9EC-006F5CC3A69F}
AppName=WPods
AppVersion={#AppVersion}
AppPublisher=WPods
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

type
  TCodeIntegrityInformation = record
    Length: Cardinal;
    Options: Cardinal;
  end;

var
  DownloadPage: TDownloadWizardPage;
  RemoveSettings: Boolean;
  FirstInstall: Boolean;

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

function DotNetInstalled: Boolean;
var
  FindRec: TFindRec;
begin
  Result := False;
  if FindFirst(ExpandConstant('{autopf}\dotnet\shared\Microsoft.WindowsDesktop.App\10.*'), FindRec) then begin
    try
      repeat
        if (FindRec.Attributes and FILE_ATTRIBUTE_DIRECTORY) <> 0 then begin
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
  if not TestSigningOn then begin
    Log('Windows test signing is off: the WPods drivers will not load.');
    CreateOutputMsgPage(wpWelcome, 'Test signing is off', 'The WPods drivers will not load yet.',
      'WPods uses test-signed drivers. Windows loads them only while test signing is on.' + #13#10#13#10 +
      'You can continue: WPods will be installed, but it cannot talk to your AirPods until you turn test signing ' +
      'on (for example "bcdedit /set testsigning on" in an administrator command prompt; Secure Boot must be off) ' +
      'and restart Windows.' + #13#10#13#10 +
      'Setup does not change boot settings and does not restart your PC.');
  end;
end;

// Downloads and installs the .NET 10 Desktop Runtime and the WebView2 Runtime when missing, before any file is copied.
function PrepareToInstall(var NeedsRestart: Boolean): String;
var
  NeedDotNet, NeedWebView2: Boolean;
begin
  Result := '';
  NeedDotNet := not DotNetInstalled;
  NeedWebView2 := not WebView2Installed;
  if not (NeedDotNet or NeedWebView2) then
    Exit;
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
  else if Code = 3010 then
    SuppressibleMsgBox('The WPods drivers were installed. Restart Windows when convenient to finish updating them.',
      mbInformation, MB_OK, IDOK)
  else if Code <> 0 then
    SuppressibleMsgBox(Format('WPods was installed, but its drivers could not be installed (error %d). ' +
      'WPods cannot connect to your AirPods until they are.' + #13#10#13#10 +
      'Run Setup again to retry. Details are in the setup log:' + #13#10 + '%s', [Code, ExpandConstant('{log}')]),
      mbError, MB_OK, IDOK);
end;

procedure CurStepChanged(CurStep: TSetupStep);
begin
  if CurStep = ssInstall then
    StopWPods
  else if CurStep = ssPostInstall then
    InstallDrivers;
end;

procedure CurUninstallStepChanged(CurUninstallStep: TUninstallStep);
var
  Startup: String;
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
  end;
end;
