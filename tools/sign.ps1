# Signs driver catalogs with the persistent "WPods Test Signing" certificate (created once in
# Cert:\CurrentUser\My, private key non-exportable) and writes the public certificate as
# WPods-TestSigning.cer next to each catalog.
param(
    [Parameter(Mandatory)][string[]]$Catalog
)
$ErrorActionPreference = 'Stop'

$subject = 'CN=WPods Test Signing'
$codeSigning = '1.3.6.1.5.5.7.3.3'
$signtool = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\10.0.28000.0\x64\signtool.exe'

$cert = Get-ChildItem Cert:\CurrentUser\My |
    Where-Object {
        $_.Subject -eq $subject -and $_.HasPrivateKey -and
        $_.NotAfter -gt (Get-Date).AddDays(30) -and
        $_.EnhancedKeyUsageList.ObjectId -contains $codeSigning
    } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (-not $cert) {
    $cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
        -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
        -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(10) `
        -CertStoreLocation Cert:\CurrentUser\My
    Write-Host "Created $subject ($($cert.Thumbprint))"
}

foreach ($cat in $Catalog) {
    & $signtool sign /q /fd SHA256 /s My /sha1 $cert.Thumbprint $cat
    if ($LASTEXITCODE -ne 0) {
        throw "signtool failed for $cat"
    }
    Export-Certificate -Cert $cert -FilePath (Join-Path (Split-Path $cat) 'WPods-TestSigning.cer') | Out-Null
}
