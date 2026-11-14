# Builds the native DLLs, the web UI, the app and the service, and packs them into dist\WPods-Setup.exe.
param(
    [switch]$SkipNative # never build the native DLLs (use .build\native\bin as is)
)
$ErrorActionPreference = 'Stop'

$Version = '0.3.2' # the only place the product version is defined (app and installer)

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
    # Downloaded mic models that WPods.csproj ships from models\.
    $fetched = 'dpdfnet2_48khz_hr', 'dpdfnet8_48khz_hr', 'fastenhancer48k_t', 'fastenhancer48k_b', 'fastenhancer48k_s',
        'fastenhancer48k_m', 'fastenhancer48k_l', 'gtcrn_simple', 'fevd_gtcrn_spec'
    $native = @(
        @{ Script = 'native\build-ffmpeg.ps1'; Outputs = 'avcodec-61.dll', 'avutil-59.dll', 'swresample-5.dll' },
        @{ Script = 'native\build-rnnoise.ps1'; Outputs = 'rnnoise.dll' },
        @{ Script = 'native\build-helper.ps1'; Outputs = 'wpods_native.dll' },
        @{ Script = 'native\build-deepfilter.ps1'; Outputs = 'deepfilter.dll', 'DeepFilterNet3_onnx.tar.gz' },
        @{ Script = 'native\build-webrtc-apm.ps1'; Outputs = 'webrtc_apm.dll' },
        @{ Script = 'native\fetch-models.ps1'; Outputs = @('onnxruntime.dll') + ($fetched | ForEach-Object { "models\$_.onnx" }) },
        # Exports with a pinned venv under .build\native.
        @{ Script = 'native\export-models.ps1'; Outputs = 'models\nsnet2_48k.onnx' }
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

# usbip-win2 installer for Setup; downloaded again only when its SHA-256 does not match.
Invoke-Script 'native\fetch-usbip.ps1'

# Web UI: ui\ builds into src\WPods\web (generated, not committed).
$ui = Join-Path $root 'ui'
Push-Location $ui
try {
    # node_modules\.package-lock.json missing or older than package-lock.json means stale dependencies.
    $installed = Get-Item (Join-Path $ui 'node_modules\.package-lock.json') -ErrorAction Ignore
    if (-not $installed -or (Get-Item (Join-Path $ui 'package-lock.json')).LastWriteTimeUtc -gt $installed.LastWriteTimeUtc) {
        npm ci
        if ($LASTEXITCODE -ne 0) {
            throw "npm ci failed ($LASTEXITCODE)"
        }
    }
    npm run build
    if ($LASTEXITCODE -ne 0) {
        throw "UI build failed ($LASTEXITCODE)"
    }
} finally {
    Pop-Location
}

$publish = Join-Path $build 'publish'
if (Test-Path $publish) {
    Remove-Item $publish -Recurse -Force
}
dotnet publish (Join-Path $root 'src\WPods\WPods.csproj') -c Release -o $publish -p:Version=$Version
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed ($LASTEXITCODE)"
}
dotnet publish (Join-Path $root 'src\WPods.Service\WPods.Service.csproj') -c Release -o $publish -p:Version=$Version
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish (service) failed ($LASTEXITCODE)"
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
