# Validates generated App Installer update metadata against a built MSIX package.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$AppInstallerPath,

    [Parameter(Mandatory)]
    [string]$PackagePath,

    [string]$ExpectedAppInstallerUri,

    [ValidateSet('x64', 'x86', 'ARM64')]
    [string]$ExpectedArchitecture,

    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\msix-common.ps1')

$resolvedAppInstallerPath = (Resolve-Path -LiteralPath $AppInstallerPath -ErrorAction Stop).Path
$document = New-Object System.Xml.XmlDocument
$document.Load($resolvedAppInstallerPath)
$root = $document.DocumentElement
if ($null -eq $root -or $root.LocalName -ne 'AppInstaller') {
    throw "'$resolvedAppInstallerPath' is not an App Installer document."
}

$appInstallerUri = Get-TemnoXmlAttribute -Element $root -Name 'Uri'
Assert-TemnoHttpsUri -Value $appInstallerUri -Label 'App Installer Uri' | Out-Null
if (-not [string]::IsNullOrWhiteSpace($ExpectedAppInstallerUri)) {
    $expectedUri = Assert-TemnoHttpsUri -Value $ExpectedAppInstallerUri -Label 'ExpectedAppInstallerUri'
    if (-not [string]::Equals($appInstallerUri, $expectedUri.AbsoluteUri, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "App Installer Uri '$appInstallerUri' does not match expected Uri '$($expectedUri.AbsoluteUri)'."
    }
}

$mainPackage = $root.SelectSingleNode("./*[local-name()='MainPackage']")
if ($null -eq $mainPackage) {
    throw "'$resolvedAppInstallerPath' does not contain a MainPackage. Temno's single-project MSIX pipeline does not produce bundles."
}

$packageUri = Get-TemnoXmlAttribute -Element $mainPackage -Name 'Uri'
Assert-TemnoHttpsUri -Value $packageUri -Label 'MainPackage Uri' | Out-Null
$processorArchitecture = Get-TemnoXmlAttribute -Element $mainPackage -Name 'ProcessorArchitecture'
if ([string]::IsNullOrWhiteSpace($processorArchitecture)) {
    throw "'$resolvedAppInstallerPath' does not declare MainPackage ProcessorArchitecture."
}

if (-not [string]::IsNullOrWhiteSpace($ExpectedArchitecture) -and
    -not [string]::Equals($processorArchitecture, $ExpectedArchitecture, [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "MainPackage ProcessorArchitecture '$processorArchitecture' does not match expected architecture '$ExpectedArchitecture'."
}

$package = Get-TemnoMsixPackageDetails -PackagePath $PackagePath
Assert-TemnoAppxManifestInfo -Manifest $package.Manifest -RequireResolvedApplicationTokens
$expectedMainPackageValues = @{
    Name = $package.Manifest.IdentityName
    Publisher = $package.Manifest.Publisher
    Version = $package.Manifest.Version
}
foreach ($pair in $expectedMainPackageValues.GetEnumerator()) {
    $actual = Get-TemnoXmlAttribute -Element $mainPackage -Name $pair.Key
    if (-not [string]::Equals($actual, $pair.Value, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "MainPackage $($pair.Key) '$actual' does not match MSIX value '$($pair.Value)'."
    }
}

$packageFileName = Split-Path -Leaf $package.PackagePath
$parsedPackageUri = [Uri]$packageUri
if (-not $parsedPackageUri.AbsolutePath.EndsWith("/$packageFileName", [System.StringComparison]::OrdinalIgnoreCase)) {
    throw "MainPackage Uri '$packageUri' must end with the generated package file '$packageFileName'."
}

$onLaunch = $root.SelectSingleNode("./*[local-name()='UpdateSettings']/*[local-name()='OnLaunch']")
if ($null -eq $onLaunch) {
    throw "'$resolvedAppInstallerPath' does not configure OnLaunch update checks."
}

$hoursText = Get-TemnoXmlAttribute -Element $onLaunch -Name 'HoursBetweenUpdateChecks'
$hours = 0
if (-not [int]::TryParse($hoursText, [ref]$hours) -or $hours -lt 0 -or $hours -gt 255) {
    throw "OnLaunch HoursBetweenUpdateChecks '$hoursText' must be an integer from 0 through 255."
}

$result = [pscustomobject]@{
    AppInstallerPath = $resolvedAppInstallerPath
    AppInstallerUri = $appInstallerUri
    PackageUri = $packageUri
    ProcessorArchitecture = $processorArchitecture
    UpdateHours = $hours
    IdentityName = $package.Manifest.IdentityName
    Version = $package.Manifest.Version
}

if (-not $Quiet) {
    Write-Host "App Installer metadata is valid: $($result.AppInstallerPath)" -ForegroundColor Green
}

return $result
