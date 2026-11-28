# Builds wpods_native.dll (x64 or arm64, MSVC /O2 /guard:cf, C++/WinRT from Windows SDK 10.0.28000.0, hybrid CRT: static vcruntime + system UCRT).
# Compiles the pinned liblc3 release into it (LC3 codec, wrapped by helper\lc3.cpp).
# Output: .build\native\bin\wpods_native.dll (import lib: .build\native\obj\helper\wpods_native.lib); arm64: .build\native-arm64
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = 'x64' # target architecture (build.ps1 -Arch)
)
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 downloads many times slower while drawing the progress bar.
$ProgressPreference = 'SilentlyContinue'
. "$PSScriptRoot\common.ps1"

$Sdk = '10.0.28000.0'

$Lc3Version = '1.1.3'
$Lc3Url = "https://github.com/google/liblc3/archive/refs/tags/v$Lc3Version.tar.gz"
$Lc3Sha256 = '276752FF54CE6A77D54EC133397B9D7E71F90CAF3D9AFA32D8B0E891B8ECB8AF'

$src = "$PSScriptRoot\helper"
$root = Get-NativeRoot $Arch
$obj = "$root\obj\helper"
$bin = "$root\bin"
$dl = "$root\src\dl"
$lc3Src = "$root\src\liblc3"
if (Test-Path $obj) {
    Remove-Item -Recurse -Force $obj
}
New-Item -ItemType Directory -Force $obj, $bin, $dl | Out-Null

$kits = "${env:ProgramFiles(x86)}\Windows Kits\10"
$cppwinrt = "$kits\Include\$Sdk\cppwinrt"
if (-not (Test-Path "$cppwinrt\winrt\base.h")) {
    throw "C++/WinRT headers not found in Windows SDK $Sdk ($cppwinrt)"
}

$lc3Archive = "$dl\liblc3-$Lc3Version.tar.gz"
Get-Pinned $Lc3Url $lc3Archive $Lc3Sha256
Reset-Dir $lc3Src
& "$env:SystemRoot\System32\tar.exe" -xf $lc3Archive -C $lc3Src
if ($LASTEXITCODE) {
    throw 'tar failed'
}
$lc3 = "$lc3Src\liblc3-$Lc3Version"

# MSVC environment for $Arch, pinned to the SDK above.
Import-Msvc $Arch $Sdk

cl /nologo /c /std:c++20 /permissive- /W4 /WX /O2 /EHsc /MT /Gy /Gw /guard:cf /Brepro /DNOMINMAX /DWINRT_NO_SOURCE_LOCATION `
    "/I$cppwinrt" "/I$lc3\include" "/Fo$obj\" "$src\wpods_native.cpp" "$src\mixer.cpp" "$src\jitter.cpp" "$src\lc3.cpp"
if ($LASTEXITCODE) {
    throw 'cl failed'
}

# liblc3 marks its API __declspec(dllexport) on Windows; this force-included header drops that so the DLL exports
# only what wpods_native.def lists.
Set-Content "$obj\lc3_no_export.h" "#include <lc3_private.h>`n#undef LC3_EXPORT`n#define LC3_EXPORT`n" -Encoding Ascii
# Upstream's MSVC options (meson.build): /fp:fast, conversion warnings C4244 / C4305 off.
$lc3Files = 'attdet', 'bits', 'bwdet', 'energy', 'lc3', 'ltpf', 'mdct', 'plc', 'sns', 'spec', 'tables', 'tns'
New-Item -ItemType Directory "$obj\liblc3" | Out-Null
$lc3Shim = @()
if ($Arch -eq 'arm64') {
    # MSVC defines __ARM_ARCH on Arm64, so common.h includes <arm_acle.h>, which MSVC does not have (its ACLE
    # intrinsics are in <intrin.h>). Nothing else applies: liblc3's NEON / ACLE code needs __ARM_NEON or
    # __ARM_FEATURE_*, which MSVC does not define, so liblc3 builds as portable C, as with MSVC on x64.
    New-Item -ItemType Directory "$obj\arm64-shim" | Out-Null
    Set-Content "$obj\arm64-shim\arm_acle.h" "#include <intrin.h>`n" -Encoding Ascii
    $lc3Shim = @("/I$obj\arm64-shim")
}
cl /nologo /c /std:c11 /W3 /wd4244 /wd4305 /fp:fast /O2 /MT /Gy /Gw /guard:cf /Brepro "/I$lc3\include" @lc3Shim "/FI$obj\lc3_no_export.h" `
    "/Fo$obj\liblc3\" @(foreach ($f in $lc3Files) { "$lc3\src\$f.c" })
if ($LASTEXITCODE) {
    throw 'cl failed (liblc3)'
}

link /nologo /DLL /guard:cf /Brepro /OPT:REF /OPT:ICF "/DEF:$src\wpods_native.def" /NODEFAULTLIB:libucrt.lib ucrt.lib `
    runtimeobject.lib ole32.lib oleaut32.lib cfgmgr32.lib avrt.lib "/OUT:$bin\wpods_native.dll" "/IMPLIB:$obj\wpods_native.lib" `
    "$obj\wpods_native.obj" "$obj\mixer.obj" "$obj\jitter.obj" "$obj\lc3.obj" (Get-ChildItem "$obj\liblc3\*.obj").FullName
if ($LASTEXITCODE) {
    throw 'link failed'
}
Write-Host "Built $bin\wpods_native.dll ($((Get-Item "$bin\wpods_native.dll").Length) bytes)"
