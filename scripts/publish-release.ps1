$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$projectPath = Join-Path $repoRoot "CampusNetTraffic.csproj"
$propsPath = Join-Path $repoRoot "Directory.Build.props"
$distPath = Join-Path $repoRoot "dist"
$releaseRoot = Join-Path $repoRoot "release"
$props = [xml](Get-Content -Raw -Encoding UTF8 $propsPath)
$version = $props.Project.PropertyGroup.Version
$releaseName = "CAUCNetTraffic-v$version"
$releasePath = Join-Path $releaseRoot $releaseName
$zipPath = Join-Path $releaseRoot "$releaseName.zip"

Write-Host "Publishing CAUCNet Traffic $version..."

Get-Process -Name "CAUCNetTraffic","CampusNetTraffic" -ErrorAction SilentlyContinue |
    Where-Object { $_.Path -like "$repoRoot*" } |
    Stop-Process -Force

dotnet publish $projectPath `
    -c Release `
    -r win-x64 `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -o $distPath

New-Item -ItemType Directory -Force -Path $releaseRoot | Out-Null
if (Test-Path $releasePath) {
    Remove-Item -LiteralPath $releasePath -Recurse -Force
}
New-Item -ItemType Directory -Force -Path $releasePath | Out-Null

Copy-Item -LiteralPath (Join-Path $distPath "CAUCNetTraffic.exe") -Destination $releasePath
Copy-Item -LiteralPath (Join-Path $repoRoot "README.md") -Destination $releasePath
Copy-Item -LiteralPath (Join-Path $repoRoot "Assets\app.ico") -Destination $releasePath

if (Test-Path $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}
$releaseFiles = Get-ChildItem -LiteralPath $releasePath -File
Compress-Archive -LiteralPath $releaseFiles.FullName -DestinationPath $zipPath

Write-Host "Release folder: $releasePath"
Write-Host "Release zip:    $zipPath"
