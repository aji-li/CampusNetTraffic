$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$publishScript = Join-Path $PSScriptRoot "publish-release.ps1"
$issPath = Join-Path $repoRoot "installer\CAUCNetTraffic.iss"
$propsPath = Join-Path $repoRoot "Directory.Build.props"
$props = [xml](Get-Content -Raw -Encoding UTF8 $propsPath)
$version = $props.Project.PropertyGroup.Version
$installerPath = Join-Path $repoRoot "release\CAUCNetTraffic-v$version-Setup.exe"

& powershell -ExecutionPolicy Bypass -File $publishScript

$iscc = Get-Command "ISCC.exe" -ErrorAction SilentlyContinue
if (-not $iscc) {
    $candidates = @(
        "E:\Program Files\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe",
        "${env:ProgramFiles}\Inno Setup 6\ISCC.exe"
    )

    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) {
            $iscc = Get-Item $candidate
            break
        }
    }
}

if (-not $iscc) {
    Write-Warning "Inno Setup compiler ISCC.exe was not found."
    Write-Host "Install Inno Setup 6, then run again:"
    Write-Host "  powershell -ExecutionPolicy Bypass -File .\scripts\build-installer.ps1"
    Write-Host ""
    Write-Host "The portable zip was generated:"
    Write-Host "  $(Join-Path $repoRoot "release\CAUCNetTraffic-v$version.zip")"
    exit 1
}

$isccPath = if ($iscc.Source) { $iscc.Source } else { $iscc.FullName }
Write-Host "Building installer with $isccPath..."
& $isccPath "/DMyAppVersion=$version" $issPath

if (-not (Test-Path $installerPath)) {
    throw "Installer was not generated: $installerPath"
}

Write-Host "Installer: $installerPath"
