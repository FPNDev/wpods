# One-time update step, run by Setup while the service is stopped when it updates a WPods older than 0.4.3: "Low audio
# latency" was one setting for sound and mic and is now two. Each pair's saved value moves to low mic latency, low audio
# latency starts off, and hotkeys set for low latency move to low mic latency. Touches only those values in WPods'
# own settings.
param(
    [Parameter(Mandatory)][string]$PreviousVersion
)
$ErrorActionPreference = 'Stop'

if (-not ([version]::TryParse($PreviousVersion, [ref]$null)) -or [version]$PreviousVersion -ge [version]'0.4.3') {
    Write-Host "previous version ${PreviousVersion}: nothing to do"
    exit 0
}

# Every user's settings.json, from the profile list (Setup may run as SYSTEM for a service update).
$settingsFiles = foreach ($user in Get-ChildItem 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList') {
    $dir = $user.GetValue('ProfileImagePath')
    if ($dir) {
        $file = Join-Path $dir 'AppData\Local\WPods\settings.json'
        if (Test-Path -LiteralPath $file) { $file }
    }
}

# Text edits keep everything else as it is: "lowLatency" is a key only in the pairs' records, "lowLatency.on" and
# "lowLatency.off" only in the hotkey bindings.
$utf8 = New-Object System.Text.UTF8Encoding $false
foreach ($file in $settingsFiles) {
    try {
        $text = [IO.File]::ReadAllText($file)
        if ($text.Contains('"lowLatencyMic')) {
            continue
        }
        $changed = $text -replace '("lowLatency"\s*:\s*)(true|false)', '${1}false, "lowLatencyMic": $2'
        $changed = $changed.Replace('"lowLatency.on"', '"lowLatencyMic.on"').Replace('"lowLatency.off"', '"lowLatencyMic.off"')
        if ($changed -eq $text) {
            continue
        }
        $before = $text | ConvertFrom-Json
        $json = $changed | ConvertFrom-Json
        foreach ($entry in @(if ($json.devices) { $json.devices.PSObject.Properties })) {
            if ($entry.Value.lowLatency -or ($entry.Value.lowLatencyMic -ne $before.devices.($entry.Name).lowLatency)) {
                throw "edit check failed for $($entry.Name)"
            }
        }
        $temp = "$file.tmp"
        [IO.File]::WriteAllText($temp, $changed, $utf8)
        [IO.File]::Replace($temp, $file, "$file.bak-0.4.2")
        Write-Host "low latency moved to low mic latency, low audio latency off, in $file (backup: $file.bak-0.4.2)"
    } catch {
        Write-Host "left $file unchanged: $_"
        Remove-Item -LiteralPath "$file.tmp" -ErrorAction Ignore
    }
}
exit 0
