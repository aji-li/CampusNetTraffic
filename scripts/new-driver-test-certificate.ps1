param(
    [string]$Subject = "CN=CampusNetTraffic Test Driver Certificate",
    [switch]$EnableTestSigning
)

$ErrorActionPreference = "Stop"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell window."
}

$repoRoot = Split-Path -Parent $PSScriptRoot
$certPath = Join-Path $repoRoot "Driver\CampusNetTrafficTest.cer"

$cert = Get-ChildItem Cert:\LocalMachine\My |
    Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (-not $cert) {
    $cert = New-SelfSignedCertificate `
        -Type CodeSigningCert `
        -Subject $Subject `
        -CertStoreLocation Cert:\LocalMachine\My `
        -KeyAlgorithm RSA `
        -KeyLength 2048 `
        -HashAlgorithm SHA256 `
        -NotAfter (Get-Date).AddYears(5)
}

Export-Certificate -Cert $cert -FilePath $certPath | Out-Null
Import-Certificate -FilePath $certPath -CertStoreLocation Cert:\LocalMachine\Root | Out-Null
Import-Certificate -FilePath $certPath -CertStoreLocation Cert:\LocalMachine\TrustedPublisher | Out-Null

Write-Host "Driver test certificate is ready."
Write-Host "Subject:    $($cert.Subject)"
Write-Host "Thumbprint: $($cert.Thumbprint)"
Write-Host "Exported:   $certPath"

if ($EnableTestSigning) {
    & bcdedit.exe /set testsigning on
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
    Write-Host "Windows test-signing mode was enabled. Reboot before loading the driver."
}
