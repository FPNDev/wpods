# Dev helper, run elevated: trusts the WPods test certificate and installs the NTAPD and NTA2DPVM
# packages built by drivers\build.ps1. Never reboots.
#Requires -RunAsAdministrator
$ErrorActionPreference = 'Stop'

$packages = Join-Path (Split-Path $PSScriptRoot) '.build\drivers'
$cer = Join-Path $packages 'NTAPD\WPods-TestSigning.cer'
$thumbprint = [Security.Cryptography.X509Certificates.X509Certificate2]::new($cer).Thumbprint
foreach ($store in 'Root', 'TrustedPublisher') {
    if (-not (Test-Path "Cert:\LocalMachine\$store\$thumbprint")) {
        Import-Certificate -FilePath $cer -CertStoreLocation "Cert:\LocalMachine\$store" | Out-Null
    }
}

function Add-DriverPackage([string]$Inf) {
    pnputil /add-driver $Inf /install | Out-Host
    # 259: no matching device needed an update; 3010: a device restart is pending.
    if ($LASTEXITCODE -notin 0, 259, 3010) {
        throw "pnputil /add-driver $Inf failed ($LASTEXITCODE)"
    }
}

Add-DriverPackage (Join-Path $packages 'NTAPD\NTAPD.inf')

# NTA2DPVM is root-enumerated: the ROOT\NTA2DPVM\nnnn devnode is created once (what devcon install does).
$hardwareId = 'WPods\NTA2DPVM'
# Hardware ID of NTA2DPVM devnodes created by older builds: the current package no longer matches it.
$legacyHardwareId = 'Root\NTA2DPVM'
Add-Type -TypeDefinition @'
using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

public static class RootDevnode
{
    [StructLayout(LayoutKind.Sequential)]
    struct SP_DEVINFO_DATA { public int cbSize; public Guid ClassGuid; public int DevInst; public IntPtr Reserved; }

    [DllImport("setupapi.dll", SetLastError = true)]
    static extern IntPtr SetupDiCreateDeviceInfoList(ref Guid classGuid, IntPtr hwndParent);
    [DllImport("setupapi.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    static extern bool SetupDiCreateDeviceInfoW(IntPtr set, string deviceName, ref Guid classGuid, string description, IntPtr hwndParent, int flags, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiSetDeviceRegistryPropertyW(IntPtr set, ref SP_DEVINFO_DATA data, int property, byte[] buffer, int size);
    [DllImport("setupapi.dll", SetLastError = true)]
    static extern bool SetupDiCallClassInstaller(int installFunction, IntPtr set, ref SP_DEVINFO_DATA data);
    [DllImport("setupapi.dll")]
    static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    const int DICD_GENERATE_ID = 1;
    const int SPDRP_HARDWAREID = 1;
    const int DIF_REGISTERDEVICE = 0x19;

    public static void Create(string name, Guid classGuid, string hardwareId)
    {
        IntPtr set = SetupDiCreateDeviceInfoList(ref classGuid, IntPtr.Zero);
        if (set == new IntPtr(-1)) throw new Win32Exception();
        try
        {
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf<SP_DEVINFO_DATA>() };
            byte[] ids = Encoding.Unicode.GetBytes(hardwareId + "\0\0");
            if (!SetupDiCreateDeviceInfoW(set, name, ref classGuid, null, IntPtr.Zero, DICD_GENERATE_ID, ref data) ||
                !SetupDiSetDeviceRegistryPropertyW(set, ref data, SPDRP_HARDWAREID, ids, ids.Length) ||
                !SetupDiCallClassInstaller(DIF_REGISTERDEVICE, set, ref data))
            {
                throw new Win32Exception();
            }
        }
        finally
        {
            SetupDiDestroyDeviceInfoList(set);
        }
    }
}
'@

function Get-Nta2dpvm([string]$Id = $hardwareId) {
    Get-PnpDevice | Where-Object { $_.HardwareID -contains $Id }
}

function Install-Nta2dpvm {
    if (-not (Get-Nta2dpvm)) {
        [RootDevnode]::Create('NTA2DPVM', [Guid]'4d36e96c-e325-11ce-bfc1-08002be10318', $hardwareId)
    }
    Add-DriverPackage (Join-Path $packages 'NTA2DPVM\NTA2DPVM.inf')
}

foreach ($legacy in Get-Nta2dpvm $legacyHardwareId) {
    pnputil /remove-device $legacy.InstanceId | Out-Host
}
Install-Nta2dpvm
# When the running driver could not be unloaded for an update (code 14, CM_PROB_NEED_RESTART), a fresh
# devnode loads the new driver without a reboot.
$device = Get-Nta2dpvm
if ($device.Problem -eq 'CM_PROB_NEED_RESTART') {
    pnputil /remove-device $device.InstanceId | Out-Host
    Install-Nta2dpvm
}

# This build's packages are identified in the store by the version stampinf wrote into DriverVer (date,version).
function Get-InfVersion([string]$Name) {
    $line = Select-String -Path (Join-Path $packages "$Name\$Name.inf") -Pattern '^\s*DriverVer\s*=\s*[^,]+,\s*([\d.]+)' |
        Select-Object -First 1
    [version]$line.Matches[0].Groups[1].Value
}
$current = @{ 'ntapd.inf' = Get-InfVersion NTAPD; 'nta2dpvm.inf' = Get-InfVersion NTA2DPVM }

# Drop superseded packages; pnputil refuses (and we ignore) any package still installed on a device. This build's
# packages are kept explicitly: without paired AirPods no device uses NTAPD, so pnputil would delete it.
$store = [xml](pnputil /enum-drivers /format xml)
foreach ($package in $store.PnpUtil.Driver | Where-Object { $_.OriginalName -in 'ntapd.inf', 'nta2dpvm.inf' }) {
    # DriverVersion is "date version".
    if ([version]($package.DriverVersion -split ' ')[-1] -ne $current[$package.OriginalName]) {
        pnputil /delete-driver $package.DriverName | Out-Null
    }
}

Get-PnpDevice | Where-Object { $_.Service -in 'NTAPD', 'NTA2DPVM' -or $_.HardwareID -contains $hardwareId } |
    Select-Object InstanceId, FriendlyName, Status, Problem | Format-Table -AutoSize | Out-Host
