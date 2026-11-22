# Validates the source configuration needed for the explicit MSIX release path.
[CmdletBinding()]
param(
    [string]$ProjectPath = (Join-Path (Split-Path -Parent $PSScriptRoot) 'src\Temno.App\Temno.App.csproj'),

    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\msix-common.ps1')

$resolvedProject = (Resolve-Path -LiteralPath $ProjectPath).Path
$projectDirectory = Split-Path -Parent $resolvedProject
$manifestPath = Join-Path $projectDirectory 'Package.appxmanifest'

if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
    throw "MSIX manifest was not found at '$manifestPath'."
}

$projectContents = Get-Content -LiteralPath $resolvedProject -Raw
if ($projectContents -notmatch '<EnableMsixTooling>\s*true\s*</EnableMsixTooling>') {
    throw "'$resolvedProject' must set EnableMsixTooling to true for the MSIX release path."
}

if ($projectContents -notmatch '<WindowsPackageType>\s*None\s*</WindowsPackageType>') {
    throw "'$resolvedProject' must keep WindowsPackageType=None for ordinary unpackaged developer builds."
}

if ($projectContents -notmatch 'Microsoft\.Windows\.SDK\.BuildTools') {
    throw "'$resolvedProject' does not reference Microsoft.Windows.SDK.BuildTools required by single-project MSIX packaging."
}

$manifest = Read-TemnoAppxManifest -Path $manifestPath
Assert-TemnoAppxManifestInfo -Manifest $manifest
Assert-TemnoLockedMsixIdentity -Manifest $manifest

foreach ($assetPath in $manifest.AssetPaths) {
    if (-not (Test-TemnoManifestAssetOnDisk -ManifestDirectory $projectDirectory -AssetPath $assetPath)) {
        throw "Manifest asset '$assetPath' is not present under '$projectDirectory', including a valid MSIX resource-qualified variant."
    }
}

$result = [pscustomobject]@{
    ProjectPath = $resolvedProject
    ManifestPath = $manifestPath
    IdentityName = $manifest.IdentityName
    Publisher = $manifest.Publisher
    Version = $manifest.Version
    DisplayName = $manifest.DisplayName
    PublisherDisplayName = $manifest.PublisherDisplayName
    Description = $manifest.Description
    BackgroundColor = $manifest.BackgroundColor
    DeveloperBuildPackageType = 'None'
    MsixToolingEnabled = $true
}

if (-not $Quiet) {
    Write-Host "MSIX configuration is valid for $($result.IdentityName) $($result.Version)." -ForegroundColor Green
}

return $result
