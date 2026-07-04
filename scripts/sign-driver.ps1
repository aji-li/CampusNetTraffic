param(
    [string]$DriverPath,
    [string]$Subject = "CN=CampusNetTraffic Test Driver Certificate"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $DriverPath) {
    $DriverPath = Join-Path $repoRoot "Driver\x64\Release\CampusNetTrafficNet.sys"
}

if (-not (Test-Path $DriverPath)) {
    throw "Driver was not found: $DriverPath"
}

$signtool = Get-Command "signtool.exe" -ErrorAction SilentlyContinue
if (-not $signtool) {
    $candidates = @(
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\10.0.26100.0\x64\signtool.exe",
        "${env:ProgramFiles(x86)}\Windows Kits\10\bin\x64\signtool.exe"
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) {
            $signtool = Get-Item $candidate
            break
        }
    }
}

if (-not $signtool) {
    throw "signtool.exe was not found. Install the Windows SDK/WDK signing tools."
}

$cert = Get-ChildItem Cert:\LocalMachine\My, Cert:\CurrentUser\My -ErrorAction SilentlyContinue |
    Where-Object { $_.Subject -eq $Subject -and $_.HasPrivateKey } |
    Sort-Object NotAfter -Descending |
    Select-Object -First 1

if (-not $cert) {
    throw "Driver signing certificate was not found. Run scripts\new-driver-test-certificate.ps1 from elevated PowerShell."
}

$signtoolPath = if ($signtool.Source) { $signtool.Source } else { $signtool.FullName }
& $signtoolPath sign /v /fd SHA256 /sha1 $cert.Thumbprint $DriverPath
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

& $signtoolPath verify /v /pa $DriverPath
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Signed driver: $DriverPath"
