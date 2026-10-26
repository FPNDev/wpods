# Builds webrtc_apm.dll (x64, MSVC, hybrid CRT: static vcruntime + system UCRT): the pinned webrtc-audio-processing
# release (freedesktop.org packaging of the WebRTC AudioProcessing module, unmodified source) built as static
# libraries with meson + ninja, plus abseil-cpp fetched by meson from the release's own pinned wrap file
# (subprojects\abseil-cpp.wrap: source_hash and patch_hash are checked by meson), linked with the small C API
# wrapper native\webrtc-apm\webrtc_apm.cc (wapm_create / wapm_process / wapm_destroy: noise suppression + high-pass).
# meson and ninja come from PyPI (versions + SHA-256 in native\requirements-webrtc-apm.txt) into a venv under
# .build\native\meson-venv (needs Python 3).
# Output: .build\native\bin\webrtc_apm.dll
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 downloads many times slower while drawing the progress bar.
$ProgressPreference = 'SilentlyContinue'
. "$PSScriptRoot\common.ps1"

$Version = '2.1'
$Url = "https://freedesktop.org/software/pulseaudio/webrtc-audio-processing/webrtc-audio-processing-$Version.tar.xz"
# Same value as $Url.sha256sum published next to the tarball.
$Sha256 = 'AE9302824B2038D394F10213CAB05312C564A038434269F11DBF68F511F9F9FE'
# Must match the meson pin in requirements-webrtc-apm.txt (an existing venv with another meson is recreated).
$MesonVersion = '1.6.1'

$root = [IO.Path]::GetFullPath("$PSScriptRoot\..\.build\native")
$dl = "$root\src\dl"
$src = "$root\src\webrtc-apm"
$obj = "$root\obj\webrtc-apm"
$bin = "$root\bin"
$venv = "$root\meson-venv"
New-Item -ItemType Directory -Force $dl, $bin | Out-Null

$archive = "$dl\webrtc-audio-processing-$Version.tar.xz"
Get-Pinned $Url $archive $Sha256

# meson + ninja in a private venv.
if (-not (Test-Path "$venv\Scripts\meson.exe") -or (& "$venv\Scripts\meson.exe" --version) -ne $MesonVersion) {
    if (Get-Command py -ErrorAction SilentlyContinue) {
        py -3 -m venv $venv
    }
    else {
        python -m venv $venv
    }
    if ($LASTEXITCODE) {
        throw 'python -m venv failed'
    }
    & "$venv\Scripts\python.exe" -m pip install --disable-pip-version-check -q --require-hashes --no-deps -r "$PSScriptRoot\requirements-webrtc-apm.txt"
    if ($LASTEXITCODE) {
        throw 'pip install meson ninja failed'
    }
}
$env:PATH = "$venv\Scripts;$env:PATH"

Reset-Dir $src, $obj
& "$env:SystemRoot\System32\tar.exe" -xf $archive -C $src
if ($LASTEXITCODE) {
    throw 'tar failed'
}
$s = "$src\webrtc-audio-processing-$Version"

# MSVC x64 environment.
Import-Msvc

# Static libraries, release (/O2), static vcruntime (/MT). C++20 instead of upstream's C++17 for the main project:
# current MSVC rejects the C++20 designated initializers in agc2 code under /std:c++17 (error C7555); abseil keeps
# its own C++17 (absl dependency default_options). Control Flow Guard (/guard:cf) for every C and C++ object, abseil
# included. Upstream defaults otherwise (inline SSE + AVX2 kernels with runtime CPU detection).
$build = "$obj\meson"
meson setup $build $s --backend=ninja --buildtype=release -Ddefault_library=static -Db_vscrt=mt -Dcpp_std=c++20 `
    '-Dc_args=/guard:cf' '-Dcpp_args=/guard:cf'
if ($LASTEXITCODE) {
    throw 'meson setup failed'
}
ninja -C $build
if ($LASTEXITCODE) {
    throw 'ninja failed'
}

# Wrapper + every static library meson produced (the linker keeps only what the wrapper reaches).
$absl = (Get-ChildItem "$s\subprojects" -Directory -Filter 'abseil-cpp-*' | Select-Object -First 1).FullName
cl /nologo /c /O2 /MT /EHsc /std:c++20 /Gy /Gw /guard:cf /Brepro /DWEBRTC_WIN /D_WIN32 /DNOMINMAX /D_USE_MATH_DEFINES /DNDEBUG `
    "/I$s" "/I$s\webrtc" "/I$absl" "/Fo$obj\webrtc_apm.obj" "$PSScriptRoot\webrtc-apm\webrtc_apm.cc"
if ($LASTEXITCODE) {
    throw 'cl failed (wrapper)'
}
$libs = (Get-ChildItem $build -Recurse -Filter '*.a').FullName
link /nologo /DLL /guard:cf /Brepro /OPT:REF /OPT:ICF /NODEFAULTLIB:libucrt.lib ucrt.lib winmm.lib "/OUT:$bin\webrtc_apm.dll" "/IMPLIB:$obj\webrtc_apm.lib" "$obj\webrtc_apm.obj" @libs
if ($LASTEXITCODE) {
    throw 'link failed'
}
Write-Host "Built $bin\webrtc_apm.dll ($((Get-Item "$bin\webrtc_apm.dll").Length) bytes)"
