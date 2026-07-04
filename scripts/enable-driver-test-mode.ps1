$ErrorActionPreference = "Stop"

$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$principal = [Security.Principal.WindowsPrincipal]::new($identity)
if (-not $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) {
    throw "Run this script from an elevated PowerShell window."
}

& bcdedit.exe /set testsigning on
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

Write-Host "Windows test-signing mode was enabled. Reboot before loading the driver."
