# Signs a previously validated MSIX package with an explicit PFX or certificate-store thumbprint.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$PackagePath,

    [string]$CertificatePath,

    [AllowNull()]
    [string]$CertificatePassword,

    [string]$CertificateThumbprint,

    [ValidateSet('CurrentUser', 'LocalMachine')]
    [string]$CertificateStore = 'CurrentUser',

    [string]$TimestampUrl
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\msix-common.ps1')

function Get-TemnoSigningCertificate {
    [CmdletBinding()]
    param(
        [string]$PfxPath,

        [AllowNull()]
        [string]$PfxPassword,

        [string]$Thumbprint,

        [ValidateSet('CurrentUser', 'LocalMachine')]
        [string]$Store
    )

    if (-not [string]::IsNullOrWhiteSpace($PfxPath)) {
        if ([string]::IsNullOrEmpty($PfxPassword)) {
            return [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($PfxPath)
        }

        return [System.Security.Cryptography.X509Certificates.X509Certificate2]::new($PfxPath, $PfxPassword)
    }

    $certificatePath = "Cert:\$Store\My\$Thumbprint"
    if (-not (Test-Path -LiteralPath $certificatePath)) {
        throw "Certificate '$Thumbprint' was not found in $Store\\My."
    }

    return Get-Item -LiteralPath $certificatePath
}

function Assert-TemnoSigningCertificate {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [System.Security.Cryptography.X509Certificates.X509Certificate2]$Certificate,

        [Parameter(Mandatory)]
        [psobject]$Manifest
    )

    if (-not $Certificate.HasPrivateKey) {
        throw "Certificate '$($Certificate.Thumbprint)' does not have an accessible private key."
    }

    if (-not [string]::Equals($Certificate.Subject, $Manifest.Publisher, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Certificate subject '$($Certificate.Subject)' must exactly match MSIX publisher '$($Manifest.Publisher)'."
    }

    $now = [DateTime]::UtcNow
    if ($Certificate.NotBefore.ToUniversalTime() -gt $now -or $Certificate.NotAfter.ToUniversalTime() -lt $now) {
        throw "Certificate '$($Certificate.Thumbprint)' is not valid at the current time."
    }

    $enhancedKeyUsage = $Certificate.Extensions |
        Where-Object { $_ -is [System.Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension] } |
        Select-Object -First 1
    if ($null -ne $enhancedKeyUsage) {
        $codeSigningOid = '1.3.6.1.5.5.7.3.3'
        $hasCodeSigningUsage = $false
        foreach ($usage in $enhancedKeyUsage.EnhancedKeyUsages) {
            if ($usage.Value -eq $codeSigningOid) {
                $hasCodeSigningUsage = $true
                break
            }
        }

        if (-not $hasCodeSigningUsage) {
            throw "Certificate '$($Certificate.Thumbprint)' does not permit code signing."
        }
    }
}

$CertificatePath = Get-TemnoEnvironmentValue -ExplicitValue $CertificatePath -EnvironmentVariable 'TEMNO_MSIX_CERTIFICATE_PATH'
$CertificateThumbprint = Get-TemnoEnvironmentValue -ExplicitValue $CertificateThumbprint -EnvironmentVariable 'TEMNO_MSIX_CERTIFICATE_THUMBPRINT'
$TimestampUrl = Get-TemnoEnvironmentValue -ExplicitValue $TimestampUrl -EnvironmentVariable 'TEMNO_MSIX_TIMESTAMP_URL'
if (-not $PSBoundParameters.ContainsKey('CertificatePassword')) {
    $CertificatePassword = Get-TemnoEnvironmentValue -EnvironmentVariable 'TEMNO_MSIX_CERTIFICATE_PASSWORD'
}

$hasCertificatePath = -not [string]::IsNullOrWhiteSpace($CertificatePath)
$hasCertificateThumbprint = -not [string]::IsNullOrWhiteSpace($CertificateThumbprint)
if ($hasCertificatePath -eq $hasCertificateThumbprint) {
    throw 'Specify exactly one of CertificatePath (PFX) or CertificateThumbprint (certificate store).'
}

if (-not [string]::IsNullOrEmpty($CertificatePassword) -and -not $hasCertificatePath) {
    throw 'CertificatePassword is only valid with CertificatePath (a PFX file).'
}

if ($hasCertificatePath) {
    $CertificatePath = (Resolve-Path -LiteralPath $CertificatePath -ErrorAction Stop).Path
    if ([System.IO.Path]::GetExtension($CertificatePath) -ine '.pfx') {
        throw "CertificatePath '$CertificatePath' must point to a .pfx file."
    }
}
else {
    $CertificateThumbprint = $CertificateThumbprint -replace '\s', ''
    if ($CertificateThumbprint -notmatch '^[A-Fa-f0-9]{40}$') {
        throw 'CertificateThumbprint must be a 40-character SHA-1 thumbprint.'
    }
}

if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
    Assert-TemnoHttpsUri -Value $TimestampUrl -Label 'TimestampUrl' | Out-Null
}

$package = Get-TemnoMsixPackageDetails -PackagePath $PackagePath
Assert-TemnoAppxManifestInfo -Manifest $package.Manifest -RequireResolvedApplicationTokens
& (Join-Path $PSScriptRoot 'verify-msix-package.ps1') -PackagePath $package.PackagePath -RequireUnsigned | Out-Null

$certificate = Get-TemnoSigningCertificate -PfxPath $CertificatePath -PfxPassword $CertificatePassword -Thumbprint $CertificateThumbprint -Store $CertificateStore
$certificateThumbprint = $certificate.Thumbprint
try {
    Assert-TemnoSigningCertificate -Certificate $certificate -Manifest $package.Manifest

    $signTool = Resolve-TemnoSignTool
    $signArguments = @('sign', '/fd', 'SHA256', '/v')
    if ($hasCertificatePath) {
        # Without /a, SignTool fails on ambiguous PFX contents instead of silently choosing a different certificate.
        $signArguments += '/f', $CertificatePath
        if (-not [string]::IsNullOrEmpty($CertificatePassword)) {
            $signArguments += '/p', $CertificatePassword
        }
    }
    else {
        if ($CertificateStore -eq 'LocalMachine') {
            $signArguments += '/sm'
        }

        $signArguments += '/sha1', $CertificateThumbprint
    }

    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
        $signArguments += '/tr', $TimestampUrl, '/td', 'SHA256'
    }

    $signArguments += $package.PackagePath
    & $signTool @signArguments
    if ($LASTEXITCODE -ne 0) {
        throw "SignTool failed to sign '$($package.PackagePath)' (exit code $LASTEXITCODE)."
    }

    $actualSigner = Get-TemnoMsixSignatureInfo -PackagePath $package.PackagePath
    if (-not [string]::Equals($actualSigner.Thumbprint, $certificateThumbprint, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "SignTool signed '$($package.PackagePath)' with certificate '$($actualSigner.Thumbprint)', not the validated certificate '$certificateThumbprint'."
    }

    if (-not [string]::Equals($actualSigner.Subject, $package.Manifest.Publisher, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "The package signature subject '$($actualSigner.Subject)' does not match publisher '$($package.Manifest.Publisher)'."
    }

    $verifyArguments = @('verify', '/pa', '/v', $package.PackagePath)
    & $signTool @verifyArguments
    if ($LASTEXITCODE -ne 0) {
        throw "SignTool failed to verify '$($package.PackagePath)' with Authenticode policy (exit code $LASTEXITCODE)."
    }
}
finally {
    if ($null -ne $certificate) {
        $certificate.Dispose()
    }
}

$result = [pscustomobject]@{
    PackagePath = $package.PackagePath
    Thumbprint = $certificateThumbprint
    Publisher = $package.Manifest.Publisher
    Timestamped = -not [string]::IsNullOrWhiteSpace($TimestampUrl)
}

Write-Host "Signed and verified: $($result.PackagePath)" -ForegroundColor Green
return $result
