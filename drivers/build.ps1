# Builds NTAPD and NTA2DPVM (Release x64), stamps the INFs with the build time, creates and signs
# the catalogs and leaves ready packages in .build\drivers\<name>\ (.inf .sys .cat .cer).
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot
$kitBin = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\10.0.28000.0'
$kitTools = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\Tools\10.0.28000.0\x64'
$vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
$msbuild = & $vswhere -latest -prerelease -products * -requires Microsoft.Component.MSBuild `
    -find 'MSBuild\**\Bin\amd64\MSBuild.exe' | Select-Object -First 1
if (-not $msbuild) {
    throw 'MSBuild not found'
}

$now = Get-Date
$date = $now.ToString('MM/dd/yyyy', [cultureinfo]::InvariantCulture)
$version = '{0}.{1}.{2}.{3}' -f $now.Year, ($now.Month * 100 + $now.Day), ($now.Hour * 100 + $now.Minute), $now.Second

$drivers = @(
    @{ Name = 'NTAPD'; Kmdf = '1.15' },
    @{ Name = 'NTA2DPVM'; Kmdf = '1.31' }
)
$catalogs = foreach ($driver in $drivers) {
    $name = $driver.Name
    & $msbuild (Join-Path $PSScriptRoot "$name\$name.vcxproj") /t:Rebuild /p:Configuration=Release /p:Platform=x64 /m /nologo /v:minimal | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "$name build failed"
    }

    $package = Join-Path $root ".build\drivers\$name"
    Remove-Item $package -Recurse -Force -ErrorAction SilentlyContinue
    New-Item $package -ItemType Directory | Out-Null
    Copy-Item (Join-Path $root ".build\drivers\out\$name\Release\$name.sys") $package
    $inf = Join-Path $package "$name.inf"
    Copy-Item (Join-Path $PSScriptRoot "$name\$name.inf") $inf

    & "$kitBin\x64\stampinf.exe" -f $inf -a amd64 -d $date -v $version -k $driver.Kmdf | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "stampinf failed for $name"
    }
    & "$kitTools\infverif.exe" /w $inf | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw "InfVerif failed for $name"
    }
    & "$kitBin\x86\Inf2Cat.exe" "/driver:$package" /os:10_X64 /uselocaltime | Out-Null
    if ($LASTEXITCODE -ne 0) {
        throw "Inf2Cat failed for $name"
    }
    Join-Path $package "$name.cat"
}

& (Join-Path $root 'tools\sign.ps1') -Catalog $catalogs
Write-Host "Driver packages $version ready in $(Join-Path $root '.build\drivers')"
