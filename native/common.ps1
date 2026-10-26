# Helpers shared by the native build scripts (dot-sourced: . "$PSScriptRoot\common.ps1").

# Downloads url to path unless path already has the expected hash.
function Get-Pinned([string]$Url, [string]$Path, [string]$Sha256) {
    if ((Test-Path $Path) -and (Get-FileHash $Path -Algorithm SHA256).Hash -eq $Sha256) {
        return
    }
    New-Item -ItemType Directory -Force (Split-Path $Path) | Out-Null
    Invoke-WebRequest $Url -OutFile $Path
    $hash = (Get-FileHash $Path -Algorithm SHA256).Hash
    if ($hash -ne $Sha256) {
        Remove-Item $Path
        throw "SHA-256 mismatch for $Url`: got $hash, expected $Sha256"
    }
}

# Imports the MSVC x64 environment (vcvars64.bat, optionally pinned to Windows SDK $Sdk) into this process.
function Import-Msvc([string]$Sdk = '') {
    $vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) {
        throw 'MSVC x64 toolset not found'
    }
    foreach ($line in (cmd /c "`"$vs\VC\Auxiliary\Build\vcvars64.bat`" $Sdk >nul 2>nul && set")) {
        if ($line -match '^([^=]+)=(.*)$') {
            Set-Item "env:$($Matches[1])" $Matches[2]
        }
    }
}

# Removes each directory if present, then creates it empty.
function Reset-Dir([string[]]$Paths) {
    foreach ($dir in $Paths) {
        if (Test-Path $dir) {
            Remove-Item -Recurse -Force $dir
        }
        New-Item -ItemType Directory $dir | Out-Null
    }
}
