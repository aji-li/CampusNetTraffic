$ErrorActionPreference = "Stop"

$serviceName = "CampusNetTrafficTraffic"
$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue

if (-not $existing) {
    Write-Host "Service is not installed: $serviceName"
    return
}

if ($existing.Status -ne "Stopped") {
    Stop-Service -Name $serviceName -Force
}

sc.exe delete $serviceName | Out-Null
Write-Host "Deleted service: $serviceName"
