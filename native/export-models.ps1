# Exports the ONNX model that has no ONNX download (script in native\export\) into .build\native\bin\models. The
# export runs only when its output is missing. Downloads are pinned and SHA-256 checked into
# .build\native\src\export\<name>\dl, and the script runs with Python 3.11 in a pinned venv under .build\native\:
#   venv-export: onnx + onnxruntime set (NSNet2)
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 downloads many times slower while drawing the progress bar.
$ProgressPreference = 'SilentlyContinue'
. "$PSScriptRoot\common.ps1"

$native = [IO.Path]::GetFullPath("$PSScriptRoot\..\.build\native")
$models = "$native\bin\models"
$scripts = "$PSScriptRoot\export"
New-Item -ItemType Directory -Force $models | Out-Null

# NSNet2 48 kHz (Microsoft DNS-Challenge baseline; weights CC BY 4.0, code MIT): streaming graph surgery.
if (-not (Test-Path "$models\nsnet2_48k.onnx")) {
    $dir = "$native\src\export\nsnet2"
    $name = 'nsnet2-20ms-48k-baseline.onnx'
    Get-Pinned "https://raw.githubusercontent.com/microsoft/DNS-Challenge/5582dcf5ba43155621de72a035eb54a7d233af14/NSNet2-baseline/$name" "$dir\dl\$name" 'E7386855E8CB99B8B502EEDB891A243E8746C019B6D29327633E2344CA520453'

    # The venv is created once with exactly the packages of the requirements file (every package with version and
    # SHA-256, pip --require-hashes --no-deps, so nothing floats).
    $requirements = "$PSScriptRoot\requirements-export.txt"
    $venv = "$native\venv-export"
    $python = "$venv\Scripts\python.exe"
    $stamp = "$venv\packages.txt"
    $want = Get-Content $requirements -Raw
    if (-not ((Test-Path $stamp) -and (Get-Content $stamp -Raw) -eq $want)) {
        if (Test-Path $venv) {
            Remove-Item -Recurse -Force $venv
        }
        & py -3.11 -m venv $venv
        if ($LASTEXITCODE) {
            throw "py -3.11 -m venv failed (Python 3.11 is required)"
        }
        & $python -m pip install --no-input --disable-pip-version-check --require-hashes --no-deps -r $requirements
        if ($LASTEXITCODE) {
            throw "pip install failed for venv-export"
        }
        Set-Content $stamp $want -NoNewline
    }

    & $python -I "$scripts\export_nsnet2.py" "$dir\dl\$name" "$models\nsnet2_48k.onnx"
    if ($LASTEXITCODE) {
        throw "export_nsnet2.py failed ($LASTEXITCODE)"
    }
}

Write-Host "Exported models are in $models"
