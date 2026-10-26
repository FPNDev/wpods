# Builds rnnoise.dll (x64, MSVC /O2 /guard:cf, hybrid CRT: static vcruntime + system UCRT) from the pinned RNNoise release.
# SIMD: SSE2 baseline + SSE4.1/AVX2 kernels selected at runtime by RNNoise's own x86 RTCD (cpuid).
# Output: .build\native\bin\rnnoise.dll
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 downloads many times slower while drawing the progress bar.
$ProgressPreference = 'SilentlyContinue'
. "$PSScriptRoot\common.ps1"

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
Get-Pinned $Url $archive $Sha256

Reset-Dir $src, $obj
& "$env:SystemRoot\System32\tar.exe" -xf $archive -C $src
if ($LASTEXITCODE) {
    throw 'tar failed'
}
$s = "$src\rnnoise-$Version"

# MSVC x64 environment.
Import-Msvc

# __SSE2__ selects the SSE code path in vec.h exactly like gcc/clang on x64 (MSVC does not define it).
$cflags = @('/nologo', '/c', '/O2', '/MT', '/Gy', '/Gw', '/guard:cf', '/Brepro', "/I$s\include", "/I$s\src",
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

link /nologo /DLL /guard:cf /Brepro /OPT:REF /OPT:ICF /NODEFAULTLIB:libucrt.lib ucrt.lib "/OUT:$bin\rnnoise.dll" "/IMPLIB:$obj\rnnoise.lib" (Get-ChildItem "$obj\*.obj").FullName
if ($LASTEXITCODE) {
    throw 'link failed'
}
Write-Host "Built $bin\rnnoise.dll ($((Get-Item "$bin\rnnoise.dll").Length) bytes)"
