# Builds rnnoise.dll (x64 or arm64, MSVC /O2 /guard:cf, hybrid CRT: static vcruntime + system UCRT) from the pinned RNNoise release.
# SIMD on x64: SSE2 baseline + SSE4.1/AVX2 kernels selected at runtime by RNNoise's own x86 RTCD (cpuid).
# SIMD on arm64: NEON (part of every Arm64 CPU, no runtime detection) through upstream's vec_neon.h, compiled with
# Visual Studio's clang-cl (see below) and linked by MSVC.
# Output: .build\native\bin\rnnoise.dll (arm64: .build\native-arm64\bin\rnnoise.dll)
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = 'x64' # target architecture (build.ps1 -Arch)
)
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 downloads many times slower while drawing the progress bar.
$ProgressPreference = 'SilentlyContinue'
. "$PSScriptRoot\common.ps1"

$Version = '0.2'
$Url = "https://github.com/xiph/rnnoise/releases/download/v$Version/rnnoise-$Version.tar.gz"
$Sha256 = '90FCE4B00B9FF24C08DBFE31B82FFD43BAE383D85C5535676D28B0A2B11C0D37'

$root = Get-NativeRoot $Arch
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

# MSVC environment for $Arch.
Import-Msvc $Arch

$cflags = @('/nologo', '/c', '/O2', '/MT', '/Gy', '/Gw', '/guard:cf', '/Brepro', "/I$s\include", "/I$s\src",
    '/DWIN32', '/DRNNOISE_BUILD', '/DDLL_EXPORT', "/Fo$obj\")
$base = 'denoise', 'rnn', 'pitch', 'kiss_fft', 'celt_lpc', 'nnet', 'nnet_default', 'parse_lpcnet_weights', 'rnnoise_data', 'rnnoise_tables'
$files = @(foreach ($f in $base) { "$s\src\$f.c" })

if ($Arch -eq 'arm64') {
    # vec_neon.h includes "os_support.h", a header of Opus (where the file comes from) that the RNNoise tarball does not
    # contain. Of it, vec_neon.h uses only OPUS_CLEAR (OPUS_INLINE is in common.h): Opus's definition, which is also
    # RNNoise's own RNN_CLEAR.
    New-Item -ItemType Directory "$obj\arm64-shim" | Out-Null
    Set-Content "$obj\arm64-shim\os_support.h" -Encoding Ascii @(
        '/* Written by build-rnnoise.ps1: the part of Opus''s os_support.h that vec_neon.h uses. */'
        '#include <string.h>'
        '#define OPUS_CLEAR(dst, n) (memset((dst), 0, (n)*sizeof(*(dst))))'
    )
    # clang-cl, not cl: vec_neon.h casts between NEON vector types gcc-style, which MSVC rejects (C2440). clang
    # defines __ARM_NEON itself (selecting vec_neon.h in vec.h) and takes the same options and CRT as cl.
    & (Get-ClangCl) --target=aarch64-pc-windows-msvc @cflags "/I$obj\arm64-shim" @files
    if ($LASTEXITCODE) {
        throw 'clang-cl failed (base)'
    }
}
else {
    # __SSE2__ selects the SSE code path in vec.h exactly like gcc/clang on x64 (MSVC does not define it).
    $cflags += '/DRNN_ENABLE_X86_RTCD', '/D__SSE2__'
    cl @cflags @files "$s\src\x86\x86_dnn_map.c" "$s\src\x86\x86cpu.c"
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
}

link /nologo /DLL /guard:cf /Brepro /OPT:REF /OPT:ICF /NODEFAULTLIB:libucrt.lib ucrt.lib "/OUT:$bin\rnnoise.dll" "/IMPLIB:$obj\rnnoise.lib" (Get-ChildItem "$obj\*.obj").FullName
if ($LASTEXITCODE) {
    throw 'link failed'
}
Write-Host "Built $bin\rnnoise.dll ($((Get-Item "$bin\rnnoise.dll").Length) bytes)"
