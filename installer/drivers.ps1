# Removes the test-signed drivers of WPods 0.2.x and older (NTAPD, NTA2DPVM): devices, packages and certificates.
# Run elevated by Setup and Uninstall; safe to run again, never changes boot settings, never reboots.
# Exit codes: 0 done, 3010 done but Windows needs a restart to finish, 1 failed (details in the output).
param(
    # The only mode; the uninstall entry of WPods 0.2.x runs "drivers.ps1 -Uninstall".
    [switch]$Uninstall
)
$ErrorActionPreference = 'Stop'

# Original INF name -> provider name of our packages (both must match, or the package is in $record).
$packages = @{ 'ntapd.inf' = 'NTAPD'; 'nta2dpvm.inf' = 'NTA2DPVM' }
$services = 'NTAPD', 'NTA2DPVM'
# Both hardware IDs NTA2DPVM devnodes were created with.
$hardwareIds = 'WPods\NTA2DPVM', 'Root\NTA2DPVM'
$record = Join-Path $PSScriptRoot 'installed.txt' # published names (oemNN.inf) of the packages WPods 0.2.x installed
# Each WPods 0.2.x build installed its own certificate, all with this subject.
$certSubject = 'CN=WPods Test Signing'
$certStores = 'Root', 'TrustedPublisher'

# Driver-store packages that are ours, keyed by published name. DISM objects: locale-safe. Published names are reused
# after deletion, so a recorded name counts only while it still holds one of our INFs.
function Get-StorePackages {
    $recorded = @(Get-Content $record -ErrorAction Ignore)
    $found = @{}
    foreach ($driver in Get-WindowsDriver -Online) {
        $inf = (Split-Path $driver.OriginalFileName -Leaf).ToLowerInvariant()
        if ($packages.ContainsKey($inf) -and ($driver.ProviderName -eq $packages[$inf] -or $driver.Driver -in $recorded)) {
            $found[$driver.Driver] = $driver
        }
    }
    $found
}

# Present and non-present devices run by our drivers, plus NTA2DPVM devnodes by hardware ID (also driverless ones).
function Get-OurDevices {
    Get-PnpDevice -ErrorAction Ignore | Where-Object {
        $_.Service -in $services -or @($_.HardwareID | Where-Object { $_ -in $hardwareIds }).Count -gt 0
    }
}

$failed = $false
$restart = $false
try {
    foreach ($device in Get-OurDevices) {
        pnputil /remove-device $device.InstanceId | Out-Host
        $failed = $failed -or ($LASTEXITCODE -notin 0, 3010)
        $restart = $restart -or ($LASTEXITCODE -eq 3010)
    }
    # /uninstall also takes the driver off devices that came back meanwhile (AirPods reconnecting).
    foreach ($name in (Get-StorePackages).Keys) {
        pnputil /delete-driver $name /uninstall | Out-Host
        $failed = $failed -or ($LASTEXITCODE -notin 0, 3010)
        $restart = $restart -or ($LASTEXITCODE -eq 3010)
    }
} catch {
    Write-Host $_
    $failed = $true
} finally {
    foreach ($certStore in $certStores) {
        foreach ($trusted in Get-ChildItem "Cert:\LocalMachine\$certStore") {
            if ($trusted.Subject -eq $certSubject) {
                Remove-Item $trusted.PSPath
                Write-Host "Removed certificate $($trusted.Thumbprint) from LocalMachine\$certStore"
            }
        }
    }
}
if ($failed) {
    exit 1
}
Remove-Item $record -ErrorAction Ignore
foreach ($name in 'NTAPD', 'NTA2DPVM') {
    Remove-Item (Join-Path $PSScriptRoot $name) -Recurse -Force -ErrorAction Ignore
}
if ($restart) {
    exit 3010
}
exit 0
