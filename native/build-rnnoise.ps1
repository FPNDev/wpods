# Builds rnnoise.dll (x64, MSVC /O2, hybrid CRT: static vcruntime + system UCRT) from the pinned RNNoise release.
# SIMD: SSE2 baseline + SSE4.1/AVX2 kernels selected at runtime by RNNoise's own x86 RTCD (cpuid).
# Output: .build\native\bin\rnnoise.dll
$ErrorActionPreference = 'Stop'

$Version = '0.2'
$Url = "https://github.com/xiph/rnnoise/releases/download/v$Version/rnnoise-$Version.tar.gz"
$Sha256 = '90FCE4B00B9FF24C08DBFE31B82FFD43BAE383D85C5535676D28B0A2B11C0D37'

$root = [IO.Path]::GetFullPath("$PSScriptRoot\..\.build\native")
$dl = "$root\src\dl"
$src = "$root\src\rnnoise"
$obj = "$root\obj\rnnoise"
$bin = "$root\bin"
New-Item -ItemType Directory -Force $dl, $bin | Out-Null

$archive = "$dl\rnnoise-$Version.tar.gz"
if (-not (Test-Path $archive) -or (Get-FileHash $archive -Algorithm SHA256).Hash -ne $Sha256) {
    Invoke-WebRequest $Url -OutFile $archive
    $hash = (Get-FileHash $archive -Algorithm SHA256).Hash
    if ($hash -ne $Sha256) {
        Remove-Item $archive
        throw "SHA-256 mismatch for $Url`: got $hash, expected $Sha256"
    }
}

foreach ($dir in $src, $obj) {
    if (Test-Path $dir) {
        Remove-Item -Recurse -Force $dir
    }
    New-Item -ItemType Directory $dir | Out-Null
}
& "$env:SystemRoot\System32\tar.exe" -xf $archive -C $src
if ($LASTEXITCODE) {
    throw 'tar failed'
}
$s = "$src\rnnoise-$Version"

# MSVC x64 environment.
$vs = & "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vs) {
    throw 'MSVC x64 toolset not found'
}
foreach ($line in (cmd /c "`"$vs\VC\Auxiliary\Build\vcvars64.bat`" >nul 2>nul && set")) {
    if ($line -match '^([^=]+)=(.*)$') {
        Set-Item "env:$($Matches[1])" $Matches[2]
    }
}

# __SSE2__ selects the SSE code path in vec.h exactly like gcc/clang on x64 (MSVC does not define it).
$cflags = @('/nologo', '/c', '/O2', '/MT', '/Gy', '/Gw', '/Brepro', "/I$s\include", "/I$s\src",
    '/DWIN32', '/DRNNOISE_BUILD', '/DDLL_EXPORT', '/DRNN_ENABLE_X86_RTCD', '/D__SSE2__', "/Fo$obj\")
$base = 'denoise', 'rnn', 'pitch', 'kiss_fft', 'celt_lpc', 'nnet', 'nnet_default', 'parse_lpcnet_weights', 'rnnoise_data', 'rnnoise_tables'
$files = @(foreach ($f in $base) { "$s\src\$f.c" }) + "$s\src\x86\x86_dnn_map.c", "$s\src\x86\x86cpu.c"

cl @cflags @files
if ($LASTEXITCODE) {
    throw 'cl failed (base)'
}
cl @cflags '/D__SSSE3__' '/D__SSE4_1__' "$s\src\x86\nnet_sse4_1.c"
if ($LASTEXITCODE) {
    throw 'cl failed (sse4.1)'
}
cl @cflags '/arch:AVX2' '/D__FMA__' "$s\src\x86\nnet_avx2.c"
if ($LASTEXITCODE) {
    throw 'cl failed (avx2)'
}

link /nologo /DLL /Brepro /OPT:REF /OPT:ICF /NODEFAULTLIB:libucrt.lib ucrt.lib "/OUT:$bin\rnnoise.dll" "/IMPLIB:$obj\rnnoise.lib" (Get-ChildItem "$obj\*.obj").FullName
if ($LASTEXITCODE) {
    throw 'link failed'
}
Write-Host "Built $bin\rnnoise.dll ($((Get-Item "$bin\rnnoise.dll").Length) bytes)"
