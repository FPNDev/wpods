# Fetches ONNX Runtime (CPU, win-x64 release build) and the pinned ONNX models with a direct download used by the
# Audio\Onnx mic stages. Every download is SHA-256 checked; nothing is built.
# Models without a download are exported by native\export-models.ps1.
# Output: .build\native\bin\onnxruntime.dll and .build\native\bin\models\*.onnx
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 downloads many times slower while drawing the progress bar.
$ProgressPreference = 'SilentlyContinue'

$OrtVersion = '1.31.0'
$OrtUrl = "https://github.com/microsoft/onnxruntime/releases/download/v$OrtVersion/onnxruntime-win-x64-$OrtVersion.zip"
$OrtSha256 = '3958D8A44984160692C1B4FFD0DE3CB48F8C39F9D34BB31E433DB06E30B82195'

$DpdfNet = 'https://huggingface.co/Ceva-IP/DPDFNet/resolve/dd6818d00f50c836fed43a6243ebe49116de5964/onnx'
$FastEnhancer = 'https://github.com/aask1357/fastenhancer/releases/download'
$Gtcrn = 'https://raw.githubusercontent.com/Xiaobin-Rong/gtcrn/502ebfab64da7c4a9af78dcb9c6ceef1ebb01c73/stream/onnx_models'

# File name in models\ = source URL + SHA-256.
$Models = [ordered]@{
    'dpdfnet2_48khz_hr.onnx' = "$DpdfNet/dpdfnet2_48khz_hr.onnx", '7F0575A5CEC0BA4FFD8F8BD657E06D007E4CCDD955D76FAAB922B9D3291DC14B'
    'dpdfnet8_48khz_hr.onnx' = "$DpdfNet/dpdfnet8_48khz_hr.onnx", '7B3AFBB260A08FE9AF3D16E3BDA992971BE1E7E951D1DEE7C2D235F5C43F5631'
    'fastenhancer48k_t.onnx' = "$FastEnhancer/onnx-48khz-v1/fastenhancer_t.onnx", '1993A3F58AE95D959123B3D28779C2800E0F016D6F4E1177F1213144F301B89C'
    'fastenhancer48k_b.onnx' = "$FastEnhancer/onnx-48khz-v1/fastenhancer_b.onnx", '70E23BBA3D41E80D30EBC5EBA39D9DF64F0E0315F31C772022BB17576C4D96BF'
    'fastenhancer48k_s.onnx' = "$FastEnhancer/onnx-48khz-v1/fastenhancer_s.onnx", 'F04ECE2BEED330DA367264C54CEDDED62F65A117FBDE5C005D3A88FC796D0BA3'
    'fastenhancer48k_m.onnx' = "$FastEnhancer/onnx-48khz-v1/fastenhancer_m.onnx", 'C7DA800810B583F4734D757C6E14D235F3EEC81476121B595743E5866B66EFA2'
    'fastenhancer48k_l.onnx' = "$FastEnhancer/onnx-48khz-v1/fastenhancer_l.onnx", 'D7138309EC98266C668B2F86658FCC8F4E82DED67CD7FC8D6598534104B4BF89'
    'gtcrn_simple.onnx'      = "$Gtcrn/gtcrn_simple.onnx", 'B4718DF6228E7BDF1A8A435CF98F838636EB2FD331ACABF86BA87C5192EBCB87'
    'fevd_gtcrn_spec.onnx'   = "$FastEnhancer/onnx-vd-v1.0.0/gtcrn.spec.onnx", '35E0AA36DABE7FAA2A21DADAD3C4E2702C52520BA43B277C16A8C63E0815D8CC'
}

$root = [IO.Path]::GetFullPath("$PSScriptRoot\..\.build\native")
$dl = "$root\src\dl"
$bin = "$root\bin"
$modelDir = "$bin\models"
New-Item -ItemType Directory -Force $dl, $bin, $modelDir | Out-Null

# Downloads url to path unless path already has the expected hash.
function Get-Pinned([string]$Url, [string]$Path, [string]$Sha256) {
    if ((Test-Path $Path) -and (Get-FileHash $Path -Algorithm SHA256).Hash -eq $Sha256) {
        return
    }
    Invoke-WebRequest $Url -OutFile $Path
    $hash = (Get-FileHash $Path -Algorithm SHA256).Hash
    if ($hash -ne $Sha256) {
        Remove-Item $Path
        throw "SHA-256 mismatch for $Url`: got $hash, expected $Sha256"
    }
}

$archive = "$dl\onnxruntime-win-x64-$OrtVersion.zip"
Get-Pinned $OrtUrl $archive $OrtSha256
$src = "$root\src\onnxruntime"
if (Test-Path $src) {
    Remove-Item -Recurse -Force $src
}
New-Item -ItemType Directory $src | Out-Null
& "$env:SystemRoot\System32\tar.exe" -xf $archive -C $src "onnxruntime-win-x64-$OrtVersion/lib/onnxruntime.dll"
if ($LASTEXITCODE) {
    throw 'tar failed'
}
Copy-Item "$src\onnxruntime-win-x64-$OrtVersion\lib\onnxruntime.dll" "$bin\onnxruntime.dll" -Force
Write-Host "Fetched $bin\onnxruntime.dll ($((Get-Item "$bin\onnxruntime.dll").Length) bytes)"

foreach ($name in $Models.Keys) {
    $url, $sha256 = $Models[$name]
    Get-Pinned $url "$modelDir\$name" $sha256
}
Write-Host "Fetched $($Models.Count) models into $modelDir"
