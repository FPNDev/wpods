# Builds deepfilter.dll (DeepFilterNet 3 C API from libDF, crate deep_filter) and copies the DFN3 model, x64 MSVC.
# Unmodified source of tag v0.5.6. Upstream's own "release-lto" profile (fat LTO, strip, panic=abort: a panic aborts
# instead of unwinding across the C ABI). Hybrid CRT like the other native DLLs: static vcruntime + system UCRT.
# The C API loads the model from a file (df_create(path, atten_lim)); the copy embedded by the "default-model"
# feature is never referenced and the linker drops it.
# Output: .build\native\bin\deepfilter.dll, .build\native\bin\DeepFilterNet3_onnx.tar.gz
$ErrorActionPreference = 'Stop'

$Tag = 'v0.5.6'
$Commit = '978576aa8400552a4ce9730838c635aa30db5e61'
$Url = "https://github.com/Rikorose/DeepFilterNet/archive/$Commit.tar.gz"
$Sha256 = '61A0E88025B70643805ED1E0C29B9B07693B538BE538DD189972FC15C1212075'
# Last Rust before 1.80: newer compilers reject time 0.3.28 pinned by the tag's Cargo.lock (E0282).
$Toolchain = '1.79.0-x86_64-pc-windows-msvc'
$Model = 'DeepFilterNet3_onnx.tar.gz'

$root = [IO.Path]::GetFullPath("$PSScriptRoot\..\.build\native")
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

$archive = "$dl\DeepFilterNet-$Commit.tar.gz"
if (-not (Test-Path $archive) -or (Get-FileHash $archive -Algorithm SHA256).Hash -ne $Sha256) {
    Invoke-WebRequest $Url -OutFile $archive
    $hash = (Get-FileHash $archive -Algorithm SHA256).Hash
    if ($hash -ne $Sha256) {
        Remove-Item $archive
        throw "SHA-256 mismatch for $Url`: got $hash, expected $Sha256"
    }
}

if (Test-Path $src) {
    Remove-Item -Recurse -Force $src
}
New-Item -ItemType Directory $src | Out-Null
# The only symlink in the tree (Python package README) cannot be created without privileges and is not needed.
& "$env:SystemRoot\System32\tar.exe" -xf $archive -C $src --exclude "DeepFilterNet-$Commit/DeepFilterNet/README.md"
if ($LASTEXITCODE) {
    throw 'tar failed'
}
$s = "$src\DeepFilterNet-$Commit"

# v0.5.6 declares no cdylib crate type (cargo-c adds it), so the DLL is requested with cargo rustc --crate-type.
# --locked: exactly the tag's Cargo.lock (crates.io checksums verified by cargo).
$env:CARGO_TARGET_DIR = $obj
$env:RUSTFLAGS = '-C target-feature=+crt-static -C link-arg=/NODEFAULTLIB:libucrt.lib -C link-arg=ucrt.lib'
Push-Location $s
try {
    cargo "+$Toolchain" rustc --profile release-lto --locked -p deep_filter --lib --no-default-features --features capi --crate-type cdylib
    if ($LASTEXITCODE) {
        throw 'cargo rustc failed'
    }
}
finally {
    Pop-Location
    Remove-Item env:RUSTFLAGS
    Remove-Item env:CARGO_TARGET_DIR
}

Copy-Item "$obj\release-lto\df.dll" "$bin\deepfilter.dll" -Force
Copy-Item "$s\models\$Model" "$bin\$Model" -Force
Write-Host "Built $bin\deepfilter.dll ($((Get-Item "$bin\deepfilter.dll").Length) bytes), model $Model ($((Get-Item "$bin\$Model").Length) bytes)"
