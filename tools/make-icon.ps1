# Builds src\WPods\Assets\WPods.ico from the design's app-icon generator.
# Headless Edge renders WPods.appIcon(size) for all nine sizes (tools\icon\render.html), then the PNGs are packed
# into one .ico as PNG-compressed 32-bit entries (Vista+).
# Usage: tools\make-icon.ps1 [-Generator <bundle.js or appicon.js>] [-Out <ico>] [-PngDir <dir to keep PNGs>]
param(
    [string]$Generator = "$PSScriptRoot\icon\appicon.js",
    [string]$Out = "$PSScriptRoot\..\src\WPods\Assets\WPods.ico",
    [string]$PngDir
)
$ErrorActionPreference = 'Stop'

# The files are written through .NET, which resolves relative paths against the process cwd, not the PowerShell
# location: resolve them here.
$Out = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($Out)
if ($PngDir) {
    $PngDir = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($PngDir)
}

$edge = "${env:ProgramFiles(x86)}\Microsoft\Edge\Application\msedge.exe"
$work = Join-Path ([IO.Path]::GetTempPath()) "wpods-icon-$PID"
New-Item -ItemType Directory -Force $work | Out-Null
try {
    $page = ([Uri](Resolve-Path "$PSScriptRoot\icon\render.html").Path).AbsoluteUri
    $src = ([Uri](Resolve-Path $Generator).Path).AbsoluteUri
    $dom = Join-Path $work 'dom.html'
    $edgeArgs = @('--headless', '--disable-gpu', '--no-first-run', "--user-data-dir=`"$work\profile`"",
        '--virtual-time-budget=10000', '--dump-dom', "`"$page`?src=$([Uri]::EscapeDataString($src))`"")
    Start-Process $edge -ArgumentList $edgeArgs -RedirectStandardOutput $dom -Wait -WindowStyle Hidden

    $match = [regex]::Match((Get-Content $dom -Raw), '<pre id="out">([^<]*)</pre>')
    $lines = $match.Groups[1].Value.Trim() -split "`n"
    if ($lines.Count -ne 9) {
        throw "Expected 9 renditions from Edge, got $($lines.Count)."
    }

    $images = foreach ($line in $lines) {
        $size, $b64 = $line.Trim() -split ' '
        [pscustomobject]@{ Size = [int]$size; Png = [Convert]::FromBase64String($b64) }
    }
    if ($PngDir) {
        New-Item -ItemType Directory -Force $PngDir | Out-Null
        foreach ($img in $images) {
            [IO.File]::WriteAllBytes((Join-Path $PngDir "appicon-$($img.Size).png"), $img.Png)
        }
    }

    # ICONDIR (6 bytes), one ICONDIRENTRY (16 bytes) per image, then the PNG data. 256 is stored as 0.
    $ms = New-Object IO.MemoryStream
    $w = New-Object IO.BinaryWriter $ms
    $w.Write([uint16]0)
    $w.Write([uint16]1)
    $w.Write([uint16]$images.Count)
    $offset = 6 + 16 * $images.Count
    foreach ($img in $images) {
        $dim = [byte]($img.Size % 256)
        $w.Write($dim)
        $w.Write($dim)
        $w.Write([byte]0)
        $w.Write([byte]0)
        $w.Write([uint16]1)
        $w.Write([uint16]32)
        $w.Write([uint32]$img.Png.Length)
        $w.Write([uint32]$offset)
        $offset += $img.Png.Length
    }
    foreach ($img in $images) {
        $w.Write($img.Png)
    }
    $w.Flush()
    [IO.File]::WriteAllBytes($Out, $ms.ToArray())
    Write-Host "Wrote $Out ($($ms.Length) bytes, sizes $(($images.Size) -join ', '))"
}
finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
