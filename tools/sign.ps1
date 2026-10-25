# Signs driver catalogs with a fresh "WPods Test Signing" certificate and writes the public certificate as
# WPods-TestSigning.cer next to each catalog. The private key lives only for this run: it is created
# non-exportable in Cert:\CurrentUser\My and deleted together with the certificate once the catalogs are signed.
param(
    [Parameter(Mandatory)][string[]]$Catalog
)
$ErrorActionPreference = 'Stop'

$subject = 'CN=WPods Test Signing' # installer\drivers.ps1 finds the trusted certificates of all builds by it
$signtool = Join-Path ${env:ProgramFiles(x86)} 'Windows Kits\10\bin\10.0.28000.0\x64\signtool.exe'
$timestamper = 'http://timestamp.digicert.com' # RFC 3161; unreachable = signtool fails = the build fails

# Not a CA (basicConstraints ca=false). The signatures carry an RFC 3161 time stamp, so they keep validating after
# the certificate expires (Windows checks them again for every new matching device); 10 years is extra margin.
$cert = New-SelfSignedCertificate -Type CodeSigningCert -Subject $subject `
    -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 `
    -KeyExportPolicy NonExportable -NotAfter (Get-Date).AddYears(10) `
    -TextExtension @('2.5.29.19={critical}{text}ca=false') `
    -CertStoreLocation Cert:\CurrentUser\My
try {
    foreach ($cat in $Catalog) {
        & $signtool sign /q /fd SHA256 /tr $timestamper /td SHA256 /s My /sha1 $cert.Thumbprint $cat
        if ($LASTEXITCODE -ne 0) {
            throw "signtool failed for $cat (signing or time stamping via $timestamper)"
        }
        Export-Certificate -Cert $cert -FilePath (Join-Path (Split-Path $cat) 'WPods-TestSigning.cer') | Out-Null
    }
} finally {
    Remove-Item "Cert:\CurrentUser\My\$($cert.Thumbprint)" -DeleteKey
}
