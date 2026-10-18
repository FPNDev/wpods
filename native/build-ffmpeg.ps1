# Builds minimal LGPL FFmpeg DLLs (avcodec with only the aac decoder, avutil, swresample), x64, MSVC.
# Hybrid CRT (static vcruntime + system UCRT): DLLs import only Windows/UCRT DLLs and each other.
# Needs MSYS2 at C:\msys64 (make, diffutils, nasm are installed by this script).
# Output: .build\native\bin\{avcodec,avutil,swresample}-*.dll (headers + import libs: .build\native\ffmpeg)
$ErrorActionPreference = 'Stop'

$Version = '7.1.5'
$Url = "https://ffmpeg.org/releases/ffmpeg-$Version.tar.xz"
$Sha256 = 'DE668509CAF9E35E3CD162473441FDB29538C6D96ED080292B3CF9E6FC5D558F'

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

$archive = "$dl\ffmpeg-$Version.tar.xz"
if (-not (Test-Path $archive) -or (Get-FileHash $archive -Algorithm SHA256).Hash -ne $Sha256) {
    Invoke-WebRequest $Url -OutFile $archive
    $hash = (Get-FileHash $archive -Algorithm SHA256).Hash
    if ($hash -ne $Sha256) {
        Remove-Item $archive
        throw "SHA-256 mismatch for $Url`: got $hash, expected $Sha256"
    }
}

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

& $bash -lc 'pacman -S --needed --noconfirm make diffutils nasm'
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
