# Installs or removes the WPods drivers (NTAPD, NTA2DPVM + its ROOT\NTA2DPVM device) from the packages next to
# this script. Run elevated by WPods-Setup. Never changes boot settings, never reboots.
# Exit codes: 0 done, 3010 done but Windows needs a restart to finish, 1 failed (details in the output).
param(
    [Parameter(Mandatory, ParameterSetName = 'Install')][switch]$Install,
    [Parameter(Mandatory, ParameterSetName = 'Uninstall')][switch]$Uninstall
)
$ErrorActionPreference = 'Stop'

$infNames = 'ntapd.inf', 'nta2dpvm.inf'
$hardwareId = 'WPods\NTA2DPVM'
# Hardware ID of NTA2DPVM devnodes created by older builds: the current package no longer matches it.
$legacyHardwareId = 'Root\NTA2DPVM'
$record = Join-Path $PSScriptRoot 'installed.txt' # published names (oemNN.inf) of the packages this install owns
$cer = Join-Path $PSScriptRoot 'NTAPD\WPods-TestSigning.cer'
$thumbprint = [Security.Cryptography.X509Certificates.X509Certificate2]::new($cer).Thumbprint
$certStores = 'Root', 'TrustedPublisher'

# Driver-store packages whose original INF name is one of ours, keyed by published name. DISM objects: locale-safe.
function Get-StorePackages {
    $packages = @{}
    foreach ($driver in Get-WindowsDriver -Online) {
        if ((Split-Path $driver.OriginalFileName -Leaf) -in $infNames) {
            $packages[$driver.Driver] = $driver
        }
    }
    $packages
}

function Get-Nta2dpvm([string]$Id = $hardwareId) {
    Get-PnpDevice | Where-Object { $_.HardwareID -contains $Id }
}

if ($Uninstall) {
    $failed = $false
    $restart = $false
    try {
        foreach ($device in @(Get-Nta2dpvm) + @(Get-Nta2dpvm $legacyHardwareId)) {
            pnputil /remove-device $device.InstanceId | Out-Host
            $failed = $failed -or ($LASTEXITCODE -notin 0, 3010)
            $restart = $restart -or ($LASTEXITCODE -eq 3010)
        }
        $store = Get-StorePackages
        foreach ($name in Get-Content $record -ErrorAction Ignore) {
            # Published names are reused after deletion: only delete a recorded name that still holds one of our INFs.
            if ($store.ContainsKey($name)) {
                pnputil /delete-driver $name /uninstall | Out-Host
                $failed = $failed -or ($LASTEXITCODE -notin 0, 3010)
                $restart = $restart -or ($LASTEXITCODE -eq 3010)
            }
        }
    } finally {
        foreach ($certStore in $certStores) {
            Remove-Item "Cert:\LocalMachine\$certStore\$thumbprint" -ErrorAction Ignore
        }
    }
    if ($failed) {
        exit 1
    }
    if ($restart) {
        exit 3010
    }
    exit 0
}

# This build's packages are identified in the store by the version stampinf wrote into DriverVer (date,version).
function Get-InfVersion([string]$Name) {
    $line = Select-String -Path (Join-Path $PSScriptRoot "$Name\$Name.inf") -Pattern '^\s*DriverVer\s*=\s*[^,]+,\s*([\d.]+)' |
        Select-Object -First 1
    [version]$line.Matches[0].Groups[1].Value
}
$current = @{ 'ntapd.inf' = Get-InfVersion NTAPD; 'nta2dpvm.inf' = Get-InfVersion NTA2DPVM }

function Test-Current($Package) {
    [version]$Package.Version -eq $current[(Split-Path $Package.OriginalFileName -Leaf)]
}

# Owned = this build's packages + packages recorded by earlier installs that are still in the store.
function Save-Record {
    $previous = @(Get-Content $record -ErrorAction Ignore)
    $owned = foreach ($package in (Get-StorePackages).Values) {
        if ($package.Driver -in $previous -or (Test-Current $package)) {
            $package.Driver
        }
    }
    Set-Content $record $owned
}

function Add-DriverPackage([string]$Name) {
    pnputil /add-driver (Join-Path $PSScriptRoot "$Name\$Name.inf") /install | Out-Host
    $code = $LASTEXITCODE
    # 259: no device needed an update; 3010: a device restart is pending.
    if ($code -notin 0, 259, 3010) {
        throw "pnputil /add-driver $Name.inf failed ($code)"
    }
    $code
}

# NTA2DPVM is root-enumerated: its ROOT\NTA2DPVM\nnnn devnode is created once (what devcon install does).
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
            var data = new SP_DEVINFO_DATA { cbSize = Marshal.SizeOf(typeof(SP_DEVINFO_DATA)) };
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

function Install-Nta2dpvm {
    if (-not (Get-Nta2dpvm)) {
        [RootDevnode]::Create('NTA2DPVM', [Guid]'4d36e96c-e325-11ce-bfc1-08002be10318', $hardwareId)
    }
    Add-DriverPackage NTA2DPVM
}

foreach ($certStore in $certStores) {
    if (-not (Test-Path "Cert:\LocalMachine\$certStore\$thumbprint")) {
        Import-Certificate -FilePath $cer -CertStoreLocation "Cert:\LocalMachine\$certStore" | Out-Null
    }
}
try {
    $ntapd = Add-DriverPackage NTAPD
    foreach ($device in Get-Nta2dpvm $legacyHardwareId) {
        pnputil /remove-device $device.InstanceId | Out-Host
        if ($LASTEXITCODE -notin 0, 3010) {
            throw "pnputil /remove-device $($device.InstanceId) failed ($LASTEXITCODE)"
        }
    }
    $nta2dpvm = Install-Nta2dpvm
    # Code 14 (CM_PROB_NEED_RESTART): the running driver could not be unloaded for the update; a fresh devnode
    # loads the new driver without a reboot.
    $stuck = @(Get-Nta2dpvm | Where-Object ConfigManagerErrorCode -eq 14)
    if ($stuck) {
        foreach ($device in $stuck) {
            pnputil /remove-device $device.InstanceId | Out-Host
            if ($LASTEXITCODE -notin 0, 3010) {
                throw "pnputil /remove-device $($device.InstanceId) failed ($LASTEXITCODE)"
            }
        }
        $nta2dpvm = Install-Nta2dpvm
    }
    # Drop superseded packages; pnputil refuses (and we ignore) any package still in use by a device.
    foreach ($package in (Get-StorePackages).Values) {
        if (-not (Test-Current $package)) {
            pnputil /delete-driver $package.Driver | Out-Host
        }
    }
} finally {
    Save-Record
}
if (3010 -in $ntapd, $nta2dpvm) {
    exit 3010
}
exit 0
