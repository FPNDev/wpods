# Dev helper, run elevated: installs the NTAPD and NTA2DPVM packages built by drivers\build.ps1 with the installer's
# own installer\drivers.ps1 -Install, copied next to the package folders in .build\drivers (it finds them via its
# $PSScriptRoot). Never reboots; exits with its exit code (3010: a restart is pending).
#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'

$root = Split-Path $PSScriptRoot
$packages = Join-Path $root '.build\drivers'
Copy-Item (Join-Path $root 'installer\drivers.ps1') $packages -Force
& (Join-Path $packages 'drivers.ps1') -Install
exit $LASTEXITCODE
