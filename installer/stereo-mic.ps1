# One-time update step for AirPods without the stereo mic stream (AirPods 1-3, Pro 1, Max 1), run by Setup between
# stopping and starting the service when it updates a WPods older than 0.4.3: turns their saved "Disable Hands-Free
# mode" off (it was the old default) and removes the WPods mic WPods made for them. Touches only those pairs'
# entries in WPods' own settings and registry values, and only non-present devnodes with their WPods mic serial.
param(
    [Parameter(Mandatory)][string]$PreviousVersion
)
$ErrorActionPreference = 'Stop'

if (-not ([version]::TryParse($PreviousVersion, [ref]$null)) -or [version]$PreviousVersion -ge [version]'0.4.3') {
    Write-Host "previous version ${PreviousVersion}: nothing to do"
    exit 0
}

# Model numbers (AAP 0x1D) and Bluetooth product IDs of the models without the stream, as in Models.cs.
$numbers = 'A1523', 'A1722', 'A2032', 'A2031', 'A2565', 'A2564', 'A2084', 'A2083', 'A2096', 'A3184'
$productIds = '2002', '200F', '2013', '200E', '200A', '201F'
$enum = 'HKLM:\SYSTEM\CurrentControlSet\Enum'
$micDevice = 'USB\VID_1209&PID_0001'
$serviceKey = 'HKLM:\SOFTWARE\WPods\Service'

# Every user's settings.json, from the profile list (Setup may run as SYSTEM for a service update).
$settingsFiles = foreach ($user in Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList') {
    $dir = $user.GetValue('ProfileImagePath')
    if ($dir) {
        $file = Join-Path $dir 'AppData\Local\WPods\settings.json'
        if (Test-Path -LiteralPath $file) { $file }
    }
}

# Addresses (12 hex digits, as WPods writes them) of the pairs without the stream: by the stored 0x1D model, or by the
# product ID in Windows' Bluetooth device names ("..._VID&0001004c_PID&200a\...&<address>_C00000000").
$pairs = @{}
foreach ($file in $settingsFiles) {
    try {
        $models = (Get-Content -LiteralPath $file -Raw | ConvertFrom-Json).models
        foreach ($entry in @($models.PSObject.Properties)) {
            if ($entry.Value.number -in $numbers -and $entry.Name -match '^[0-9A-F]{12}$') {
                $pairs[$entry.Name] = $entry.Value.number
            }
        }
    } catch {
        Write-Host "skipped unreadable $file`: $_"
    }
}
foreach ($device in Get-ChildItem "$enum\BTHENUM" -ErrorAction Ignore) {
    if ($device.PSChildName -match '_VID&0001004C_PID&([0-9A-F]{4})$' -and $Matches[1] -in $productIds) {
        $productId = $Matches[1]
        foreach ($instance in Get-ChildItem $device.PSPath -ErrorAction Ignore) {
            if ($instance.PSChildName -match '&([0-9A-F]{12})_C[0-9A-F]+$') {
                $pairs[$Matches[1].ToUpperInvariant()] = "PID $productId"
            }
        }
    }
}
if ($pairs.Count -eq 0) {
    Write-Host 'no AirPods without the stereo mic'
    exit 0
}
Write-Host ('AirPods without the stereo mic: ' + (($pairs.GetEnumerator() | ForEach-Object { "$($_.Key) ($($_.Value))" }) -join ', '))

# 1. Saved "Disable Hands-Free mode" on -> off. A text edit of that one value keeps everything else as it is; the
#    record's first property is disableHandsFree (HostSettings), so the pattern matches only the "devices" entry.
$utf8 = New-Object System.Text.UTF8Encoding $false
foreach ($file in $settingsFiles) {
    try {
        $text = [IO.File]::ReadAllText($file)
        $changed = $text
        foreach ($address in $pairs.Keys) {
            $changed = $changed -replace ('("' + $address + '"\s*:\s*\{\s*"disableHandsFree"\s*:\s*)true\b'), '${1}false'
        }
        if ($changed -eq $text) {
            continue
        }
        $devices = ($changed | ConvertFrom-Json).devices
        foreach ($address in $pairs.Keys) {
            if ($devices.$address -and $devices.$address.disableHandsFree) {
                throw "edit check failed for $address"
            }
        }
        $temp = "$file.tmp"
        [IO.File]::WriteAllText($temp, $changed, $utf8)
        [IO.File]::Replace($temp, $file, "$file.bak-0.4.3")
        Write-Host "Hands-Free mode back on by default in $file (backup: $file.bak-0.4.3)"
    } catch {
        Write-Host "left $file unchanged: $_"
        Remove-Item -LiteralPath "$file.tmp" -ErrorAction Ignore
    }
}

# 2. The service's wanted mic and remembered mic names of those pairs.
$service = Get-Item $serviceKey -ErrorAction Ignore
if ($service) {
    $micAddress = $service.GetValue('MicAddress')
    if ($null -ne $micAddress -and ('{0:X12}' -f [uint64]$micAddress) -in $pairs.Keys) {
        Remove-ItemProperty $serviceKey -Name 'MicAddress', 'MicName' -ErrorAction Ignore
        Write-Host ('service: no mic for {0:X12}' -f [uint64]$micAddress)
    }
    foreach ($address in $pairs.Keys) {
        Remove-ItemProperty "$serviceKey\MicNames" -Name "WPODS$address" -ErrorAction Ignore
    }
}

# 3. Their WPods mic devnodes (USB\VID_1209&PID_0001\WPODS<address> and its children), only while not present: the
#    service is stopped, so they are unplugged. The pid.codes test ID is shared; the WPODS serial is ours alone.
Add-Type -TypeDefinition @'
using System.Runtime.InteropServices;
public static class Devnode {
    [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode)] static extern int CM_Locate_DevNodeW(out uint dev, string id, uint flags);
    [DllImport("cfgmgr32.dll")] static extern int CM_Uninstall_DevNode(uint dev, uint flags);
    public static string ForgetHidden(string id) {
        uint dev;
        if (CM_Locate_DevNodeW(out dev, id, 0) == 0) return "present, kept";
        if (CM_Locate_DevNodeW(out dev, id, 1) != 0) return "not found";
        return CM_Uninstall_DevNode(dev, 0) == 0 ? "removed" : "not removed";
    }
}
'@
$roots = @($pairs.Keys | ForEach-Object { "$micDevice\WPODS$_" } | Where-Object { Test-Path -LiteralPath "$enum\$_" })
if ($roots.Count -eq 0) {
    exit 0
}
# Every instance ID once; children are found through their parent's ParentIdPrefix ("<prefix>&...").
$all = foreach ($enumerator in Get-ChildItem $enum -ErrorAction Ignore) {
    foreach ($device in Get-ChildItem $enumerator.PSPath -ErrorAction Ignore) {
        foreach ($instance in Get-ChildItem $device.PSPath -ErrorAction Ignore) {
            "$($enumerator.PSChildName)\$($device.PSChildName)\$($instance.PSChildName)"
        }
    }
}
# Children first, so a parent goes after its subtree.
function Get-Subtree([string]$id) {
    $prefix = (Get-ItemProperty -LiteralPath "$enum\$id" -Name ParentIdPrefix -ErrorAction Ignore).ParentIdPrefix
    if (-not $prefix) { return }
    foreach ($child in $all) {
        if ($child.Substring($child.LastIndexOf('\') + 1).StartsWith("$prefix&", [StringComparison]::OrdinalIgnoreCase)) {
            Get-Subtree $child
            $child
        }
    }
}
foreach ($root in $roots) {
    foreach ($id in @(Get-Subtree $root) + $root) {
        Write-Host "$id`: $([Devnode]::ForgetHidden($id))"
    }
}
exit 0
