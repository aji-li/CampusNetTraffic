$ErrorActionPreference = "Stop"

$repoRoot = Split-Path -Parent $PSScriptRoot
$propsPath = Join-Path $repoRoot "Directory.Build.props"
$props = [xml](Get-Content -Raw -Encoding UTF8 $propsPath)
$version = $props.Project.PropertyGroup.Version
$tag = "v$version"
$releaseRoot = Join-Path $repoRoot "release"
$installer = Join-Path $releaseRoot "CAUCNetTraffic-v$version-Setup.exe"
$portable = Join-Path $releaseRoot "CAUCNetTraffic-v$version.zip"
$latest = Join-Path $repoRoot "latest.json"
$repo = "aji-li/CampusNetTraffic"

$gh = Get-Command "gh.exe" -ErrorAction SilentlyContinue
if (-not $gh) {
    $candidates = @(
        "${env:ProgramFiles}\GitHub CLI\gh.exe",
        "${env:LOCALAPPDATA}\Programs\GitHub CLI\gh.exe"
    )
    foreach ($candidate in $candidates) {
        if ($candidate -and (Test-Path $candidate)) {
            $gh = Get-Item $candidate
            break
        }
    }
}

if (-not $gh) {
    throw "GitHub CLI gh.exe was not found. Install it with: winget install --id GitHub.cli -e"
}

$ghPath = if ($gh.Source) { $gh.Source } else { $gh.FullName }

if (-not (Test-Path $installer) -or -not (Test-Path $portable)) {
    & powershell -ExecutionPolicy Bypass -File (Join-Path $PSScriptRoot "build-installer.ps1")
}

& $ghPath auth status --hostname github.com

$notes = if (Test-Path $latest) {
    $info = Get-Content -Raw -Encoding UTF8 $latest | ConvertFrom-Json
    ($info.notes | ForEach-Object { "- $_" }) -join "`n"
} else {
    "CAUCNet Traffic $version"
}

$previousErrorActionPreference = $ErrorActionPreference
$ErrorActionPreference = "Continue"
& $ghPath release view $tag --repo $repo *> $null
$releaseExists = $LASTEXITCODE -eq 0
$ErrorActionPreference = $previousErrorActionPreference

if ($releaseExists) {
    Write-Host "Release $tag exists. Uploading assets..."
    & $ghPath release upload $tag $installer $portable --repo $repo --clobber
} else {
    Write-Host "Creating release $tag..."
    & $ghPath release create $tag $installer $portable --repo $repo --title "CAUCNet Traffic $version" --notes $notes
}

Write-Host "Release URL: https://github.com/$repo/releases/tag/$tag"
