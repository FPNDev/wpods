# Builds everything and produces dist\WPods-Setup.exe:
# native DLLs (only when an output is missing) -> driver packages (only when their sources changed) -> web UI ->
# dotnet publish -> Inno Setup 6.
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
    # Mic mode models (the files WPods.csproj ships from models\): downloaded ones, then the exported one.
    $fetched = 'dpdfnet2_48khz_hr', 'dpdfnet8_48khz_hr', 'fastenhancer48k_t', 'fastenhancer48k_b', 'fastenhancer48k_s',
        'fastenhancer48k_m', 'fastenhancer48k_l', 'gtcrn_simple', 'fevd_gtcrn_spec'
    $native = @(
        @{ Script = 'native\build-ffmpeg.ps1'; Outputs = 'avcodec-61.dll', 'avutil-59.dll', 'swresample-5.dll' },
        @{ Script = 'native\build-rnnoise.ps1'; Outputs = 'rnnoise.dll' },
        @{ Script = 'native\build-helper.ps1'; Outputs = 'wpods_native.dll' },
        @{ Script = 'native\build-deepfilter.ps1'; Outputs = 'deepfilter.dll', 'DeepFilterNet3_onnx.tar.gz' },
        @{ Script = 'native\build-webrtc-apm.ps1'; Outputs = 'webrtc_apm.dll' },
        @{ Script = 'native\fetch-models.ps1'; Outputs = @('onnxruntime.dll') + ($fetched | ForEach-Object { "models\$_.onnx" }) },
        # Runs the export only when its output is missing (pinned venv under .build\native).
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

if (-not $SkipDrivers) {
    # Rebuilt only when a driver source or the signing script changed: a rebuild restamps DriverVer, and a new
    # DriverVer makes every app update reinstall (and restart) both drivers.
    $sourceHash = Join-Path $build 'drivers\source.hash'
    $sources = @(Get-ChildItem (Join-Path $root 'drivers\NTAPD'), (Join-Path $root 'drivers\NTA2DPVM') -File) +
        @(Get-Item (Join-Path $root 'drivers\Public.h'), (Join-Path $root 'drivers\build.ps1'), (Join-Path $root 'tools\sign.ps1'))
    $hash = @(foreach ($file in Get-FileHash -LiteralPath $sources.FullName -Algorithm SHA256) {
        "$($file.Hash) $($file.Path.Substring($root.Length + 1))"
    }) -join "`r`n"
    $missing = $false
    foreach ($name in 'NTAPD', 'NTA2DPVM') {
        foreach ($file in "$name.inf", "$name.sys", "$name.cat", 'WPods-TestSigning.cer') {
            if (-not (Test-Path (Join-Path $build "drivers\$name\$file"))) {
                $missing = $true
            }
        }
    }
    if ($missing -or (Get-Content $sourceHash -Raw -ErrorAction Ignore) -ne $hash) {
        # A failed build leaves no hash behind, so the next run builds again.
        Remove-Item $sourceHash -ErrorAction Ignore
        Invoke-Script 'drivers\build.ps1'
        Set-Content $sourceHash $hash -NoNewline
    }
}

# Web UI: ui\ (TypeScript + JSX via our build-time transform) -> src\WPods\web (generated, not committed).
$ui = Join-Path $root 'ui'
Push-Location $ui
try {
    # npm writes node_modules\.package-lock.json on install: missing or older than package-lock.json (e.g. after a
    # pull) means the installed dependencies are stale.
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
