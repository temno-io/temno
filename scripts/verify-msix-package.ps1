# Validates a built MSIX archive without installing it.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackagePath,

    [switch]$RequireSignature,

    [switch]$RequireUnsigned,

    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\msix-common.ps1')

if ($RequireSignature -and $RequireUnsigned) {
    throw 'RequireSignature and RequireUnsigned cannot be used together.'
}

$package = Get-TemnoMsixPackageDetails -PackagePath $PackagePath
Assert-TemnoAppxManifestInfo -Manifest $package.Manifest -RequireResolvedApplicationTokens

foreach ($requiredEntry in @('[Content_Types].xml', 'AppxBlockMap.xml', 'AppxManifest.xml')) {
    if ($package.Entries -notcontains $requiredEntry) {
        throw "Package '$($package.PackagePath)' does not contain required MSIX entry '$requiredEntry'."
    }
}

foreach ($assetPath in $package.Manifest.AssetPaths) {
    if (-not (Test-TemnoManifestAssetInPackage -Entries $package.Entries -AssetPath $assetPath)) {
        throw "Package '$($package.PackagePath)' is missing manifest asset '$assetPath', including a valid MSIX resource-qualified variant."
    }
}

$hasSignature = $package.Entries -icontains 'AppxSignature.p7x'
if ($RequireSignature -and -not $hasSignature) {
    throw "Package '$($package.PackagePath)' is not signed."
}

if ($RequireUnsigned -and $hasSignature) {
    throw "Package '$($package.PackagePath)' is signed, but an unsigned test artifact was expected."
}

$result = [pscustomobject]@{
    PackagePath = $package.PackagePath
    IdentityName = $package.Manifest.IdentityName
    Publisher = $package.Manifest.Publisher
    Version = $package.Manifest.Version
    Signed = $hasSignature
    EntryCount = $package.Entries.Count
}

if (-not $Quiet) {
    Write-Host "MSIX package is structurally valid: $($result.PackagePath)" -ForegroundColor Green
}

return $result
