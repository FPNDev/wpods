# Builds everything and produces dist\WPods-Setup.exe:
# native DLLs (only when an output is missing) -> driver packages -> dotnet publish -> Inno Setup 6.
param(
    [switch]$SkipNative, # never build the native DLLs (use .build\native\bin as is)
    [switch]$SkipDrivers # use the driver packages already in .build\drivers
)
$ErrorActionPreference = 'Stop'

$Version = '0.3.0' # the only place the product version is defined (app and installer)

$root = $PSScriptRoot
$build = Join-Path $root '.build'
$shell = (Get-Process -Id $PID).Path

# Child process: the scripts change their environment (MSVC vars) and must not leak it into this session.
function Invoke-Script([string]$Script) {
    & $shell -NoProfile -File (Join-Path $root $Script)
    if ($LASTEXITCODE -ne 0) {
        throw "$Script failed ($LASTEXITCODE)"
    }
}

if (-not $SkipNative) {
    $nativeBin = Join-Path $build 'native\bin'
    $native = @(
        @{ Script = 'native\build-ffmpeg.ps1'; Outputs = 'avcodec-61.dll', 'avutil-59.dll', 'swresample-5.dll' },
        @{ Script = 'native\build-rnnoise.ps1'; Outputs = 'rnnoise.dll' },
        @{ Script = 'native\build-helper.ps1'; Outputs = 'wpods_native.dll' }
    )
    foreach ($step in $native) {
        foreach ($output in $step.Outputs) {
            if (-not (Test-Path (Join-Path $nativeBin $output))) {
                Invoke-Script $step.Script
                break
            }
        }
    }
}

if (-not $SkipDrivers) {
    Invoke-Script 'drivers\build.ps1'
}

$publish = Join-Path $build 'publish'
if (Test-Path $publish) {
    Remove-Item $publish -Recurse -Force
}
dotnet publish (Join-Path $root 'src\WPods\WPods.csproj') -c Release -r win-x64 -o $publish -p:Version=$Version
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed ($LASTEXITCODE)"
}

$innoKey = 'Microsoft\Windows\CurrentVersion\Uninstall\Inno Setup 6_is1'
$candidates = foreach ($key in "HKLM:\SOFTWARE\$innoKey", "HKLM:\SOFTWARE\WOW6432Node\$innoKey", "HKCU:\SOFTWARE\$innoKey") {
    $location = (Get-ItemProperty $key -ErrorAction Ignore).InstallLocation
    if ($location) {
        Join-Path $location 'ISCC.exe'
    }
}
$candidates = @($candidates) + "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe"
$iscc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) {
    throw 'Inno Setup 6 not found (winget install JRSoftware.InnoSetup)'
}
& $iscc /Qp "/DAppVersion=$Version" (Join-Path $root 'installer\WPods.iss')
if ($LASTEXITCODE -ne 0) {
    throw "ISCC failed ($LASTEXITCODE)"
}
$setup = Get-Item (Join-Path $root 'dist\WPods-Setup.exe')
Write-Host "Built $($setup.FullName) ($($setup.Length) bytes)"
