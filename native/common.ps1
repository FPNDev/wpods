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

# Architecture of the build machine's Windows: x64 or arm64.
function Get-HostArch {
    switch ([System.Runtime.InteropServices.RuntimeInformation]::OSArchitecture) {
        'X64' { 'x64' }
        'Arm64' { 'arm64' }
        default { throw "Unsupported build machine architecture $_ (x64 or Arm64 Windows needed)" }
    }
}

# Build tree of one target architecture: .build\native for x64, .build\native-arm64 for arm64.
function Get-NativeRoot([string]$Arch) {
    $name = if ($Arch -eq 'x64') { 'native' } else { "native-$Arch" }
    [IO.Path]::GetFullPath("$PSScriptRoot\..\.build\$name")
}

# Imports the MSVC environment for target $Arch (x64 or arm64; vcvarsall.bat, optionally pinned to Windows SDK $Sdk)
# into this process. Uses the compilers native to this machine: arm64-hosted ones on Arm64 Windows, x64-hosted ones on
# x64 Windows, cross-compiling when the target differs.
function Import-Msvc([string]$Arch, [string]$Sdk = '') {
    $component = @{ x64 = 'Microsoft.VisualStudio.Component.VC.Tools.x86.x64'; arm64 = 'Microsoft.VisualStudio.Component.VC.Tools.ARM64' }[$Arch]
    if (-not $component) {
        throw "Unknown target architecture '$Arch' (x64 or arm64)"
    }
    $vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires $component -property installationPath
    if (-not $vs) {
        throw "MSVC $Arch toolset not found (Visual Studio component $component)"
    }
    $hostArch = Get-HostArch
    $target = if ($hostArch -eq $Arch) { $Arch } else { "${hostArch}_$Arch" }
    foreach ($line in (cmd /c "`"$vs\VC\Auxiliary\Build\vcvarsall.bat`" $target $Sdk >nul 2>nul && set")) {
        if ($line -match '^([^=]+)=(.*)$') {
            Set-Item "env:$($Matches[1])" $Matches[2]
        }
    }
    if ($env:VSCMD_ARG_TGT_ARCH -ne $Arch) {
        throw "vcvarsall.bat $target did not set up an MSVC $Arch environment"
    }
}

# Path of clang-cl.exe native to this machine, from Visual Studio's C++ Clang component. Needed for arm64 only: some
# upstream Arm64 code is written for gcc/clang (GNU assembly, gcc-style NEON vector casts) and MSVC cannot compile it.
function Get-ClangCl {
    $component = 'Microsoft.VisualStudio.Component.VC.Llvm.Clang'
    $vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires $component -property installationPath
    $llvmHost = @{ x64 = 'x64'; arm64 = 'ARM64' }[(Get-HostArch)]
    $clang = "$vs\VC\Tools\Llvm\$llvmHost\bin\clang-cl.exe"
    if (-not $vs -or -not (Test-Path $clang)) {
        throw "clang-cl not found (Visual Studio component $component)"
    }
    $clang
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
