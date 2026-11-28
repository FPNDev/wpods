# Fetches the pinned, SHA-256 checked usbip-win2 installer (BSD-2-Clause) that WPods-Setup bundles, for x64 or arm64.
param(
    [ValidateSet('x64', 'arm64')]
    [string]$Arch = 'x64' # target architecture (build.ps1 -Arch)
)
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 downloads many times slower while drawing the progress bar.
$ProgressPreference = 'SilentlyContinue'
. "$PSScriptRoot\common.ps1"

$UsbipVersion = '0.9.8.1'
# Same values as the "digest" GitHub reports for each release asset.
$UsbipSha256 = @{
    x64   = '38CAD6D4432B52D5BB9409D9AD03B72FDFFC4ADA4CD3A48FBECA1A2752A8518A'
    arm64 = 'CAB7FF97F79275EEB5C5B8BB2EB3111FF6B6C4EEAD07D9BEC9BE5BD1E3A35800'
}[$Arch]
$UsbipUrl = "https://github.com/vadimgrn/usbip-win2/releases/download/v.$UsbipVersion/USBip-$UsbipVersion-$Arch.exe"

$setup = [IO.Path]::GetFullPath("$PSScriptRoot\..\.build\deps\USBip-$UsbipVersion-$Arch.exe")
Get-Pinned $UsbipUrl $setup $UsbipSha256
Write-Host "Fetched $setup ($((Get-Item $setup).Length) bytes)"
