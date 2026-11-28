# Builds deepfilter.dll (DeepFilterNet 3 C API from libDF, crate deep_filter) and copies the DFN3 model, x64 or arm64
# MSVC. Unmodified source of tag v0.5.6. Upstream's own "release-lto" profile (fat LTO, strip, panic=abort: a panic
# aborts instead of unwinding across the C ABI). Hybrid CRT like the other native DLLs: static vcruntime + system UCRT.
# The C API loads the model from a file (df_create(path, atten_lim)); the copy embedded by the "default-model"
# feature is never referenced and the linker drops it.
# The Rust toolchain is the build machine's own; a target of another architecture is cross-compiled.
# Output: .build\native\bin\deepfilter.dll, .build\native\bin\DeepFilterNet3_onnx.tar.gz (arm64: .build\native-arm64)
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = 'x64' # target architecture (build.ps1 -Arch)
)
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 downloads many times slower while drawing the progress bar.
$ProgressPreference = 'SilentlyContinue'
. "$PSScriptRoot\common.ps1"

$Tag = 'v0.5.6'
$Commit = '978576aa8400552a4ce9730838c635aa30db5e61'
$Url = "https://github.com/Rikorose/DeepFilterNet/archive/$Commit.tar.gz"
$Sha256 = '61A0E88025B70643805ED1E0C29B9B07693B538BE538DD189972FC15C1212075'
$Triples = @{ x64 = 'x86_64-pc-windows-msvc'; arm64 = 'aarch64-pc-windows-msvc' }
$HostTriple = $Triples[(Get-HostArch)]
$Target = $Triples[$Arch]
# Last Rust before 1.80: newer compilers reject time 0.3.28 pinned by the tag's Cargo.lock (E0282).
$Toolchain = "1.79.0-$HostTriple"
$Model = 'DeepFilterNet3_onnx.tar.gz'

$root = Get-NativeRoot $Arch
$dl = "$root\src\dl"
$src = "$root\src\deepfilter"
$obj = "$root\obj\deepfilter"
$bin = "$root\bin"
New-Item -ItemType Directory -Force $dl, $bin | Out-Null

# Rust toolchain: rustup via winget if missing, then the pinned toolchain.
$cargoBin = "$env:USERPROFILE\.cargo\bin"
if (-not (Test-Path "$cargoBin\rustup.exe")) {
    winget install --id Rustlang.Rustup -e --silent --accept-source-agreements --accept-package-agreements
    if (-not (Test-Path "$cargoBin\rustup.exe")) {
        throw 'rustup install failed'
    }
}
$env:Path = "$cargoBin;$env:Path"
rustup toolchain install $Toolchain --profile minimal --no-self-update
if ($LASTEXITCODE) {
    throw "rustup toolchain install $Toolchain failed"
}
# Cross build: the target's standard library. --target only then, so a native build stays exactly as before
# (RUSTFLAGS also reach build scripts, output in $obj\release-lto).
$cross = @()
$out = "$obj\release-lto"
if ($Target -ne $HostTriple) {
    rustup target add $Target --toolchain $Toolchain
    if ($LASTEXITCODE) {
        throw "rustup target add $Target failed"
    }
    $cross = '--target', $Target
    $out = "$obj\$Target\release-lto"
}

$archive = "$dl\DeepFilterNet-$Commit.tar.gz"
Get-Pinned $Url $archive $Sha256

Reset-Dir $src
# The only symlink in the tree (Python package README) cannot be created without privileges and is not needed.
& "$env:SystemRoot\System32\tar.exe" -xf $archive -C $src --exclude "DeepFilterNet-$Commit/DeepFilterNet/README.md"
if ($LASTEXITCODE) {
    throw 'tar failed'
}
$s = "$src\DeepFilterNet-$Commit"

# arm64: tract-linalg (the inference engine) assembles its NEON kernels from GNU-syntax .S files through the cc crate,
# which cl.exe cannot do. clang-cl (Visual Studio component C++ Clang Compiler for Windows) can, through
# native\deepfilter\tract-clang-cl.rs, which first undoes the ';' comments tract-linalg writes for MSVC targets.
if ($Arch -eq 'arm64') {
    New-Item -ItemType Directory -Force $obj | Out-Null
    $wrapper = "$obj\tract-clang-cl.exe"
    rustc "+$Toolchain" --edition 2021 -O -o $wrapper "$PSScriptRoot\deepfilter\tract-clang-cl.rs"
    if ($LASTEXITCODE) {
        throw 'rustc failed (tract-clang-cl)'
    }
    $env:WPODS_CLANG_CL = Get-ClangCl
    $env:CC_aarch64_pc_windows_msvc = $wrapper
}

# v0.5.6 declares no cdylib crate type (cargo-c adds it), so the DLL is requested with cargo rustc --crate-type.
# --locked: exactly the tag's Cargo.lock (crates.io checksums verified by cargo).
$env:CARGO_TARGET_DIR = $obj
$env:RUSTFLAGS = '-C target-feature=+crt-static -C link-arg=/NODEFAULTLIB:libucrt.lib -C link-arg=ucrt.lib'
Push-Location $s
try {
    cargo "+$Toolchain" rustc --profile release-lto --locked -p deep_filter --lib --no-default-features --features capi --crate-type cdylib @cross
    if ($LASTEXITCODE) {
        throw 'cargo rustc failed'
    }
}
finally {
    Pop-Location
    Remove-Item env:RUSTFLAGS
    Remove-Item env:CARGO_TARGET_DIR
    Remove-Item env:CC_aarch64_pc_windows_msvc, env:WPODS_CLANG_CL -ErrorAction Ignore
}

Copy-Item "$out\df.dll" "$bin\deepfilter.dll" -Force
Copy-Item "$s\models\$Model" "$bin\$Model" -Force
Write-Host "Built $bin\deepfilter.dll ($((Get-Item "$bin\deepfilter.dll").Length) bytes), model $Model ($((Get-Item "$bin\$Model").Length) bytes)"
