param(
    [switch]$TestSign
)

$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "Driver\CampusNetTrafficNet.vcxproj"
$packagesPath = Join-Path $repoRoot "packages"
$nuget = Get-Command "nuget.exe" -ErrorAction SilentlyContinue
if (-not $nuget) {
    $nugetCandidates = Get-ChildItem $env:LOCALAPPDATA -Recurse -Filter nuget.exe -ErrorAction SilentlyContinue
    $nuget = $nugetCandidates | Select-Object -First 1
}

if ($nuget) {
    $nugetPath = if ($nuget.Source) { $nuget.Source } else { $nuget.FullName }
    & $nugetPath restore (Join-Path $repoRoot "Driver\packages.config") -PackagesDirectory $packagesPath
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}

$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (-not (Test-Path $vswhere)) {
    throw "vswhere.exe was not found. Install Visual Studio Build Tools."
}

$vsPath = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
if (-not $vsPath) {
    throw "Visual Studio C++ build tools were not found."
}

$msbuild = Join-Path $vsPath "MSBuild\Current\Bin\MSBuild.exe"
if (-not (Test-Path $msbuild)) {
    $msbuild = Join-Path $vsPath "MSBuild\15.0\Bin\MSBuild.exe"
}

if (-not (Test-Path $msbuild)) {
    throw "MSBuild.exe was not found under: $vsPath"
}

& $msbuild $projectPath /p:Configuration=Release /p:Platform=x64 /m
if ($LASTEXITCODE -ne 0) {
    exit $LASTEXITCODE
}

if ($TestSign) {
    & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "sign-driver.ps1")
    if ($LASTEXITCODE -ne 0) {
        exit $LASTEXITCODE
    }
}
