# Exports the ONNX model that has no ONNX download (script in native\export\) into .build\native\bin\models. The
# export runs only when its output is missing. Downloads are pinned and SHA-256 checked into
# .build\native\src\export\<name>\dl, and the script runs with Python 3.11 in a pinned venv under .build\native\:
#   venv-export: onnx + onnxruntime set (NSNet2)
$ErrorActionPreference = 'Stop'

$native = [IO.Path]::GetFullPath("$PSScriptRoot\..\.build\native")
$models = "$native\bin\models"
$scripts = "$PSScriptRoot\export"
New-Item -ItemType Directory -Force $models | Out-Null

# Exact versions of every package (pip --no-deps, so nothing floats).
$ExportPackages = @(
    'numpy==2.2.6', 'onnx==1.19.0', 'onnxruntime==1.23.0', 'coloredlogs==15.0.1', 'humanfriendly==10.0',
    'pyreadline3==3.5.6', 'flatbuffers==25.12.19', 'ml_dtypes==0.6.0', 'mpmath==1.3.0', 'packaging==26.3',
    'protobuf==7.36.2', 'sympy==1.13.1', 'typing_extensions==4.16.0'
)

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

# Creates the venv once with exactly the given packages; returns its python.exe.
function Get-Venv([string]$Name, [string[]]$Packages) {
    $venv = "$native\$Name"
    $python = "$venv\Scripts\python.exe"
    $stamp = "$venv\packages.txt"
    $want = $Packages -join "`n"
    if ((Test-Path $stamp) -and (Get-Content $stamp -Raw) -eq $want) {
        return $python
    }
    if (Test-Path $venv) {
        Remove-Item -Recurse -Force $venv
    }
    # Out-Host: tool output must not become part of the function's return value.
    & py -3.11 -m venv $venv | Out-Host
    if ($LASTEXITCODE) {
        throw "py -3.11 -m venv failed (Python 3.11 is required)"
    }
    & $python -m pip install --no-input --disable-pip-version-check --no-deps @Packages | Out-Host
    if ($LASTEXITCODE) {
        throw "pip install failed for $Name"
    }
    Set-Content $stamp $want -NoNewline
    return $python
}

function Invoke-Export([string]$Python, [string]$Script, [string[]]$Arguments) {
    & $Python -I "$scripts\$Script" @Arguments
    if ($LASTEXITCODE) {
        throw "$Script failed ($LASTEXITCODE)"
    }
}

# NSNet2 48 kHz (Microsoft DNS-Challenge baseline; weights CC BY 4.0, code MIT): streaming graph surgery.
if (-not (Test-Path "$models\nsnet2_48k.onnx")) {
    $dir = "$native\src\export\nsnet2"
    $name = 'nsnet2-20ms-48k-baseline.onnx'
    Get-Pinned "https://raw.githubusercontent.com/microsoft/DNS-Challenge/5582dcf5ba43155621de72a035eb54a7d233af14/NSNet2-baseline/$name" "$dir\dl\$name" 'E7386855E8CB99B8B502EEDB891A243E8746C019B6D29327633E2344CA520453'
    $python = Get-Venv 'venv-export' $ExportPackages
    Invoke-Export $python 'export_nsnet2.py' @("$dir\dl\$name", "$models\nsnet2_48k.onnx")
}

Write-Host "Exported models are in $models"
