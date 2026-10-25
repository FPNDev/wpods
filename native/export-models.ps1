# Exports the ONNX model that has no ONNX download (script in native\export\) into .build\native\bin\models. The
# export runs only when its output is missing. Downloads are pinned and SHA-256 checked into
# .build\native\src\export\<name>\dl, and the script runs with Python 3.11 in a pinned venv under .build\native\:
#   venv-export: onnx + onnxruntime set (NSNet2)
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 downloads many times slower while drawing the progress bar.
$ProgressPreference = 'SilentlyContinue'

$native = [IO.Path]::GetFullPath("$PSScriptRoot\..\.build\native")
$models = "$native\bin\models"
$scripts = "$PSScriptRoot\export"
New-Item -ItemType Directory -Force $models | Out-Null

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

# Creates the venv once with exactly the packages of the requirements file (every package with version and SHA-256,
# pip --require-hashes --no-deps, so nothing floats); returns its python.exe.
function Get-Venv([string]$Name, [string]$Requirements) {
    $venv = "$native\$Name"
    $python = "$venv\Scripts\python.exe"
    $stamp = "$venv\packages.txt"
    $want = Get-Content $Requirements -Raw
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
    & $python -m pip install --no-input --disable-pip-version-check --require-hashes --no-deps -r $Requirements | Out-Host
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
    $python = Get-Venv 'venv-export' "$PSScriptRoot\requirements-export.txt"
    Invoke-Export $python 'export_nsnet2.py' @("$dir\dl\$name", "$models\nsnet2_48k.onnx")
}

Write-Host "Exported models are in $models"
