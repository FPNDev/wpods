# Builds minimal LGPL FFmpeg DLLs (avcodec with only the aac decoder, avutil, swresample), x64, MSVC.
# Hybrid CRT (static vcruntime + system UCRT): DLLs import only Windows/UCRT DLLs and each other.
# Needs MSYS2 at C:\msys64 (pinned make, diffutils, nasm packages are installed by this script).
# Output: .build\native\bin\{avcodec,avutil,swresample}-*.dll (headers + import libs: .build\native\ffmpeg)
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 downloads many times slower while drawing the progress bar.
$ProgressPreference = 'SilentlyContinue'

$Version = '7.1.5'
$Url = "https://ffmpeg.org/releases/ffmpeg-$Version.tar.xz"
$Sha256 = 'DE668509CAF9E35E3CD162473441FDB29538C6D96ED080292B3CF9E6FC5D558F'

# MSYS2 build tools, exact package files (installed with pacman -U). Their dependencies (sh, libintl, libiconv) are
# part of the base MSYS2 install.
$MsysRepo = 'https://repo.msys2.org/msys/x86_64'
$MsysPackages = [ordered]@{
    'make-4.4.1-3-x86_64.pkg.tar.zst'     = 'AF0BDBA17F06FE037F0194069ADAA31A8FE45F1A11381501896AEA1FAE37BD5D'
    'diffutils-3.12-1-x86_64.pkg.tar.zst' = '7902C8CE3D4DD69A0F5E98DC9D5C83C17B23314BA486169DB57EF6E2835CE3B6'
    'nasm-3.02-1-x86_64.pkg.tar.zst'      = '1AB3D9D7F86F57A66CC506799648527B9215C3EB1B7BD404D886D59B503A05B7'
}

$root = [IO.Path]::GetFullPath("$PSScriptRoot\..\.build\native")
$dl = "$root\src\dl"
$src = "$root\src\ffmpeg"
$obj = "$root\obj\ffmpeg"
$prefix = "$root\ffmpeg"
$bin = "$root\bin"
$bash = 'C:\msys64\usr\bin\bash.exe'
if (-not (Test-Path $bash)) {
    throw 'MSYS2 not found at C:\msys64 (winget install MSYS2.MSYS2)'
}
New-Item -ItemType Directory -Force $dl, $bin | Out-Null

# Downloads url to path unless path already has the expected hash.
function Get-Pinned([string]$Url, [string]$Path, [string]$Sha256) {
    if ((Test-Path $Path) -and (Get-FileHash $Path -Algorithm SHA256).Hash -eq $Sha256) {
        return
    }
    Invoke-WebRequest $Url -OutFile $Path
    $hash = (Get-FileHash $Path -Algorithm SHA256).Hash
    if ($hash -ne $Sha256) {
        Remove-Item $Path
        throw "SHA-256 mismatch for $Url`: got $hash, expected $Sha256"
    }
}

$archive = "$dl\ffmpeg-$Version.tar.xz"
Get-Pinned $Url $archive $Sha256

foreach ($dir in $src, $obj, $prefix) {
    if (Test-Path $dir) {
        Remove-Item -Recurse -Force $dir
    }
    New-Item -ItemType Directory $dir | Out-Null
}
& "$env:SystemRoot\System32\tar.exe" -xf $archive -C $src
if ($LASTEXITCODE) {
    throw 'tar failed'
}

# MSYS2 paths, single-quoted for the shell.
$packages = foreach ($name in $MsysPackages.Keys) {
    Get-Pinned "$MsysRepo/$name" "$dl\$name" $MsysPackages[$name]
    "'$(& 'C:\msys64\usr\bin\cygpath.exe' -u "$dl\$name")'"
}
& $bash -lc "pacman -U --noconfirm $packages"
if ($LASTEXITCODE) {
    throw 'pacman failed'
}

# MSVC x64 environment, inherited by the MSYS2 shell.
$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) {
    throw 'MSVC x64 toolset not found'
}
foreach ($line in (cmd /c "`"$vs\VC\Auxiliary\Build\vcvars64.bat`" >nul 2>nul && set")) {
    if ($line -match '^([^=]+)=(.*)$') {
        Set-Item "env:$($Matches[1])" $Matches[2]
    }
}
$env:MSYS2_PATH_TYPE = 'inherit'

$u = @(foreach ($p in "$PSScriptRoot\build-ffmpeg.sh", "$src\ffmpeg-$Version", $obj, $prefix) { & 'C:\msys64\usr\bin\cygpath.exe' -u $p })
& $bash -l @u
if ($LASTEXITCODE) {
    throw 'FFmpeg build failed'
}
Copy-Item "$prefix\bin\*.dll" $bin
Get-ChildItem "$prefix\bin\*.dll" | Format-Table Name, Length
