# Creates a self-signed code-signing certificate for LOCAL MSIX test signing only.
#
# The certificate subject is taken from the app manifest's Package/Identity Publisher so it
# satisfies sign-msix.ps1's exact-subject-match check. The exported PFX can then be passed to
# build-msix.ps1 -CertificatePath, and (with -Trust) the public certificate is imported into the
# current user's Trusted People store so a per-user MSIX install validates.
#
# WARNING: This is a development-only credential. Never ship it, commit the PFX, or treat it as a
# production signing identity. Delete it (Remove-Item Cert:\CurrentUser\My\<thumbprint>) when done.
[CmdletBinding()]
param(
    [string]$OutputDirectory,

    [Parameter(Mandatory)]
    [string]$Password,

    [int]$ValidityMonths = 12,

    [switch]$Trust,

    [switch]$Force
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\msix-common.ps1')

if ([string]::IsNullOrWhiteSpace($Password)) {
    throw 'Password is required so the exported PFX private key is protected.'
}
if ($ValidityMonths -lt 1 -or $ValidityMonths -gt 120) {
    throw 'ValidityMonths must be between 1 and 120.'
}

$repoRoot = Get-TemnoRepoRoot -StartPath $PSScriptRoot
$manifestPath = Join-Path $repoRoot 'src\Temno.App\Package.appxmanifest'
$manifest = Read-TemnoAppxManifest -Path $manifestPath
$subject = $manifest.Publisher
if ([string]::IsNullOrWhiteSpace($subject) -or $subject -notmatch '^CN=') {
    throw "Manifest publisher '$subject' must begin with 'CN='."
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts\dev-cert'
}
$fullOutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $fullOutputDirectory -Force | Out-Null

$pfxPath = Join-Path $fullOutputDirectory 'temno-dev.pfx'
$cerPath = Join-Path $fullOutputDirectory 'temno-dev.cer'
if ((Test-Path -LiteralPath $pfxPath) -and -not $Force) {
    throw "A dev certificate already exists at '$pfxPath'. Re-run with -Force to overwrite."
}

Write-Host "Creating self-signed code-signing certificate for '$subject'..." -ForegroundColor Cyan
$certificate = New-SelfSignedCertificate `
    -Type CodeSigningCert `
    -Subject $subject `
    -KeyUsage DigitalSignature `
    -FriendlyName 'Temno Windows dev signing (local test only)' `
    -CertStoreLocation 'Cert:\CurrentUser\My' `
    -NotAfter ([DateTime]::UtcNow.AddMonths($ValidityMonths)) `
    -TextExtension @('2.5.29.37={text}1.3.6.1.5.5.7.3.3')

$securePassword = ConvertTo-SecureString -String $Password -Force -AsPlainText
Export-PfxCertificate -Cert $certificate -FilePath $pfxPath -Password $securePassword -Force | Out-Null
Export-Certificate -Cert $certificate -FilePath $cerPath -Force | Out-Null

if ($Trust) {
    # TrustedPeople lets a per-user MSIX sideload install validate. Root (CurrentUser) is also needed
    # so the chain built by 'signtool verify /pa' inside sign-msix.ps1 terminates in a trusted root;
    # a self-signed cert in TrustedPeople alone is not treated as a trusted root and verify would fail.
    Write-Host 'Importing public certificate into Cert:\CurrentUser\TrustedPeople and Cert:\CurrentUser\Root...' -ForegroundColor Cyan
    Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\CurrentUser\TrustedPeople' | Out-Null
    Import-Certificate -FilePath $cerPath -CertStoreLocation 'Cert:\CurrentUser\Root' | Out-Null
}

$result = [pscustomobject]@{
    Thumbprint = $certificate.Thumbprint
    Subject = $certificate.Subject
    PfxPath = $pfxPath
    CerPath = $cerPath
    Trusted = [bool]$Trust
    NotAfter = $certificate.NotAfter
}

Write-Host ''
Write-Host "Dev certificate ready (thumbprint $($result.Thumbprint))." -ForegroundColor Green
Write-Host "  PFX: $pfxPath" -ForegroundColor Green
Write-Host "  CER: $cerPath" -ForegroundColor Green
if (-not $Trust) {
    Write-Host 'Not trusted yet. To install the signed MSIX per-user, re-run with -Trust or import the .cer into Trusted People.' -ForegroundColor Yellow
}
Write-Host ''
Write-Host 'Next: build and sign with' -ForegroundColor Cyan
Write-Host "  .\scripts\build-msix.ps1 -Environment development -CertificatePath `"$pfxPath`" -CertificatePassword '<password>'" -ForegroundColor Cyan
Write-Host 'Delete this dev credential when finished:' -ForegroundColor Yellow
Write-Host "  Remove-Item Cert:\CurrentUser\My\$($result.Thumbprint)" -ForegroundColor Yellow

return $result
