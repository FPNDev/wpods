# Builds wpods_native.dll (x64, MSVC /O2 /guard:cf, C++/WinRT from Windows SDK 10.0.28000.0, hybrid CRT: static vcruntime + system UCRT).
# Output: .build\native\bin\wpods_native.dll (import lib: .build\native\obj\helper\wpods_native.lib)
$ErrorActionPreference = 'Stop'
. "$PSScriptRoot\common.ps1"

$Sdk = '10.0.28000.0'

$src = "$PSScriptRoot\helper"
$root = [IO.Path]::GetFullPath("$PSScriptRoot\..\.build\native")
$obj = "$root\obj\helper"
$bin = "$root\bin"
if (Test-Path $obj) {
    Remove-Item -Recurse -Force $obj
}
New-Item -ItemType Directory -Force $obj, $bin | Out-Null

$kits = "${env:ProgramFiles(x86)}\Windows Kits\10"
$cppwinrt = "$kits\Include\$Sdk\cppwinrt"
if (-not (Test-Path "$cppwinrt\winrt\base.h")) {
    throw "C++/WinRT headers not found in Windows SDK $Sdk ($cppwinrt)"
}

# MSVC x64 environment, pinned to the SDK above.
Import-Msvc $Sdk

cl /nologo /c /std:c++20 /permissive- /W4 /WX /O2 /EHsc /MT /Gy /Gw /guard:cf /Brepro /DNOMINMAX /DWINRT_NO_SOURCE_LOCATION `
    "/I$cppwinrt" "/Fo$obj\" "$src\wpods_native.cpp"
if ($LASTEXITCODE) {
    throw 'cl failed'
}

link /nologo /DLL /guard:cf /Brepro /OPT:REF /OPT:ICF "/DEF:$src\wpods_native.def" /NODEFAULTLIB:libucrt.lib ucrt.lib `
    runtimeobject.lib ole32.lib oleaut32.lib cfgmgr32.lib "/OUT:$bin\wpods_native.dll" "/IMPLIB:$obj\wpods_native.lib" "$obj\wpods_native.obj"
if ($LASTEXITCODE) {
    throw 'link failed'
}
Write-Host "Built $bin\wpods_native.dll ($((Get-Item "$bin\wpods_native.dll").Length) bytes)"
