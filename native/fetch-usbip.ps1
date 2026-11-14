# Fetches the pinned, SHA-256 checked usbip-win2 installer (BSD-2-Clause) that WPods-Setup bundles.
$ErrorActionPreference = 'Stop'
# Windows PowerShell 5.1 downloads many times slower while drawing the progress bar.
$ProgressPreference = 'SilentlyContinue'
. "$PSScriptRoot\common.ps1"

$UsbipVersion = '0.9.8.1'
$UsbipUrl = "https://github.com/vadimgrn/usbip-win2/releases/download/v.$UsbipVersion/USBip-$UsbipVersion-x64.exe"
$UsbipSha256 = '38CAD6D4432B52D5BB9409D9AD03B72FDFFC4ADA4CD3A48FBECA1A2752A8518A'

$setup = [IO.Path]::GetFullPath("$PSScriptRoot\..\.build\deps\USBip-$UsbipVersion-x64.exe")
Get-Pinned $UsbipUrl $setup $UsbipSha256
Write-Host "Fetched $setup ($((Get-Item $setup).Length) bytes)"
