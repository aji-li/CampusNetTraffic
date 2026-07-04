param(
    [string]$Configuration = "Debug"
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "TrafficService\CampusNetTraffic.TrafficService.csproj"

dotnet build $projectPath --configuration $Configuration

$serviceName = "CampusNetTrafficTraffic"
$displayName = "CampusNetTraffic Traffic Service"
$exePath = Join-Path $repoRoot "TrafficService\bin\$Configuration\net8.0-windows\CampusNetTraffic.TrafficService.exe"

if (-not (Test-Path $exePath)) {
    throw "Service executable not found: $exePath"
}

$existing = Get-Service -Name $serviceName -ErrorAction SilentlyContinue
if ($existing) {
    if ($existing.Status -ne "Stopped") {
        Stop-Service -Name $serviceName -Force
    }

    sc.exe delete $serviceName | Out-Null
    Start-Sleep -Seconds 1
}

sc.exe create $serviceName binPath= "`"$exePath`"" start= auto DisplayName= "`"$displayName`"" | Out-Null
sc.exe description $serviceName "Provides low-overhead network byte counters for CAUCNet Traffic." | Out-Null
Start-Service -Name $serviceName

Get-Service -Name $serviceName
