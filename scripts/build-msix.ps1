# Builds Temno.App as a single-project MSIX package. The default artifact is intentionally unsigned.
# -Environment is compiled into Temno.Core (WP-260). With -Artifact the script builds nothing: it
# reads that MSIX's compiled environment, checks it against -Environment and the deploy profile,
# and prints the backend it belongs to only when they agree.
[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [ValidateSet('development', 'production')]
    [string]$Environment,

    [string]$Artifact,

    [string]$ProfileRoot,

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [ValidateSet('x64', 'x86', 'ARM64')]
    [string]$Architecture = 'x64',

    [string]$OutputDirectory,

    [string]$CertificatePath,

    [AllowNull()]
    [string]$CertificatePassword,

    [string]$CertificateThumbprint,

    [ValidateSet('CurrentUser', 'LocalMachine')]
    [string]$CertificateStore = 'CurrentUser',

    [string]$TimestampUrl,

    [string]$AppInstallerUri,

    [int]$UpdateHours = 24,

    [switch]$Clean,

    [switch]$NoRestore,

    [switch]$ValidateOnly
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\msix-common.ps1')
. (Join-Path $PSScriptRoot 'lib\temno-environment.ps1')
. (Join-Path $PSScriptRoot 'lib\app-payload.ps1')
$Environment = $Environment.ToLowerInvariant()

if (-not [string]::IsNullOrWhiteSpace($Artifact)) {
    return Invoke-TemnoArtifactVerification -Path $Artifact -Environment $Environment -Service App -ProfileRoot $ProfileRoot
}

$repoRoot = Get-TemnoRepoRoot -StartPath $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\Temno.App\Temno.App.csproj'
$manifestPath = Join-Path $repoRoot 'src\Temno.App\Package.appxmanifest'
$whisperModelPath = Join-Path $repoRoot 'tools\whisper\ggml-base.en.bin'

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot "artifacts\msix\$Configuration\$Architecture"
}

$CertificatePath = Get-TemnoEnvironmentValue -ExplicitValue $CertificatePath -EnvironmentVariable 'TEMNO_MSIX_CERTIFICATE_PATH'
$CertificateThumbprint = Get-TemnoEnvironmentValue -ExplicitValue $CertificateThumbprint -EnvironmentVariable 'TEMNO_MSIX_CERTIFICATE_THUMBPRINT'
$TimestampUrl = Get-TemnoEnvironmentValue -ExplicitValue $TimestampUrl -EnvironmentVariable 'TEMNO_MSIX_TIMESTAMP_URL'
$AppInstallerUri = Get-TemnoEnvironmentValue -ExplicitValue $AppInstallerUri -EnvironmentVariable 'TEMNO_MSIX_APPINSTALLER_URI'

if (-not $PSBoundParameters.ContainsKey('CertificatePassword')) {
    $CertificatePassword = Get-TemnoEnvironmentValue -EnvironmentVariable 'TEMNO_MSIX_CERTIFICATE_PASSWORD'
}

if (-not $PSBoundParameters.ContainsKey('UpdateHours')) {
    $environmentHours = Get-TemnoEnvironmentValue -EnvironmentVariable 'TEMNO_MSIX_UPDATE_HOURS'
    if (-not [string]::IsNullOrWhiteSpace($environmentHours)) {
        $parsedHours = 0
        if (-not [int]::TryParse($environmentHours, [ref]$parsedHours)) {
            throw 'TEMNO_MSIX_UPDATE_HOURS must be an integer from 0 through 255.'
        }

        $UpdateHours = $parsedHours
    }
}

if ($UpdateHours -lt 0 -or $UpdateHours -gt 255) {
    throw 'UpdateHours must be an integer from 0 through 255.'
}

$hasCertificatePath = -not [string]::IsNullOrWhiteSpace($CertificatePath)
$hasCertificateThumbprint = -not [string]::IsNullOrWhiteSpace($CertificateThumbprint)
$signingRequested = $hasCertificatePath -or $hasCertificateThumbprint

if ($hasCertificatePath -and $hasCertificateThumbprint) {
    throw 'Specify either CertificatePath (PFX) or CertificateThumbprint (certificate store), not both.'
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

if ($hasCertificateThumbprint) {
    $CertificateThumbprint = $CertificateThumbprint -replace '\s', ''
    if ($CertificateThumbprint -notmatch '^[A-Fa-f0-9]{40}$') {
        throw 'CertificateThumbprint must be a 40-character SHA-1 thumbprint.'
    }
}

if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
    Assert-TemnoHttpsUri -Value $TimestampUrl -Label 'TimestampUrl' | Out-Null
    if (-not $signingRequested) {
        throw 'TimestampUrl requires CertificatePath or CertificateThumbprint because only signed packages can be timestamped.'
    }
}

$generateAppInstaller = -not [string]::IsNullOrWhiteSpace($AppInstallerUri)
if ($generateAppInstaller) {
    $parsedAppInstallerUri = Assert-TemnoHttpsUri -Value $AppInstallerUri -Label 'AppInstallerUri'
    if (-not $parsedAppInstallerUri.AbsolutePath.EndsWith('.appinstaller', [System.StringComparison]::OrdinalIgnoreCase)) {
        throw 'AppInstallerUri must point to a .appinstaller file hosted over HTTPS.'
    }

    if (-not $signingRequested) {
        throw 'App Installer metadata requires a signed package. Supply a certificate explicitly; the default unsigned build is only a local artifact.'
    }

    if ($Environment -eq 'development') {
        throw 'A development package is Temno-Dev, and App Installer metadata would name the identity it was built with. Publish it with scripts/publish-update.ps1 (WP-375).'
    }
}

& (Join-Path $PSScriptRoot 'verify-msix-config.ps1') -ProjectPath $projectPath -Quiet | Out-Null
$sourceManifest = Read-TemnoAppxManifest -Path $manifestPath
$fullOutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$deployProfile = Get-TemnoDeployProfile -Environment $Environment -ProfileRoot $ProfileRoot

$validation = [pscustomobject]@{
    Environment = $Environment
    GatewayHost = $deployProfile.GatewayHost
    AccountsHost = $deployProfile.AccountsHost
    SupabaseProjectRef = $deployProfile.SupabaseProjectRef
    ProjectPath = $projectPath
    OutputDirectory = $fullOutputDirectory
    Architecture = $Architecture
    RuntimeIdentifier = "win-$($Architecture.ToLowerInvariant())"
    SigningEnabled = $signingRequested
    GenerateAppInstaller = $generateAppInstaller
    UpdateHours = $UpdateHours
    PackageIdentity = (Get-TemnoDesktopApp -Environment $Environment).Name
    PackageVersion = $sourceManifest.Version
}

if ($Architecture -ne 'x64') {
    throw "FFmpeg is pinned for win-x64 only (WP-264). Architecture '$Architecture' has no pinned GPL build."
}

if ($ValidateOnly) {
    return $validation
}

& (Join-Path $PSScriptRoot 'ensure-whisper-model.ps1')
$whisperModelInfo = Assert-TemnoWhisperModelFile -Path $whisperModelPath
Write-Host "Whisper model verified: $($whisperModelInfo.Bytes) bytes, SHA-256 $($whisperModelInfo.Sha256)" -ForegroundColor DarkGray

# The MSIX's ffmpeg/ comes from the NuGet package Temno.FFmpeg.Runtime.win-x64, which the app's build checks file by
# file (WP-406). ensure-ffmpeg.ps1 runs the same files and returns the manifest the finished package must match:
# every file pinned in scripts/ffmpeg/Temno.FFmpeg.targets, and nothing else in ffmpeg/.
$ffmpegPin = & (Join-Path $PSScriptRoot 'ensure-ffmpeg.ps1')
if ($null -eq $ffmpegPin -or $null -eq $ffmpegPin.Files) {
    throw 'ensure-ffmpeg.ps1 did not return the pinned FFmpeg manifest.'
}
# The restored package's FFMPEG-SOURCE-NOTICES.md, which the MSIX root must carry unchanged (WP-427).
$ffmpegNotices = Get-TemnoFfmpegNoticesPath -Pin $ffmpegPin

$vcRuntimeDir = Join-Path $repoRoot 'tools\vcruntime'
$vcRuntime = Copy-TemnoVcRuntime -Destination $vcRuntimeDir
Write-Host "VC++ runtime staged ($($vcRuntime.Version)): $vcRuntimeDir" -ForegroundColor DarkGray

if ($Clean -and (Test-Path -LiteralPath $fullOutputDirectory)) {
    $artifactsRoot = [System.IO.Path]::GetFullPath((Join-Path $repoRoot 'artifacts'))
    $artifactsPrefix = $artifactsRoot.TrimEnd([System.IO.Path]::DirectorySeparatorChar, [System.IO.Path]::AltDirectorySeparatorChar) + [System.IO.Path]::DirectorySeparatorChar
    if (-not $fullOutputDirectory.StartsWith($artifactsPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to clean '$fullOutputDirectory'. Clean may only delete a directory under '$artifactsRoot'."
    }

    Remove-Item -LiteralPath $fullOutputDirectory -Recurse -Force
}

New-Item -ItemType Directory -Path $fullOutputDirectory -Force | Out-Null

$runtimeIdentifier = "win-$($Architecture.ToLowerInvariant())"
$generateAppInstallerValue = if ($generateAppInstaller) { 'true' } else { 'false' }
$appxPackageDirectory = $fullOutputDirectory
if (-not $appxPackageDirectory.EndsWith([System.IO.Path]::DirectorySeparatorChar.ToString()) -and
    -not $appxPackageDirectory.EndsWith([System.IO.Path]::AltDirectorySeparatorChar.ToString())) {
    # Global MSBuild properties passed on the command line cannot be normalized by the SDK targets.
    $appxPackageDirectory += [System.IO.Path]::DirectorySeparatorChar
}
$buildArguments = @(
    'build',
    $projectPath,
    '-c', $Configuration,
    "-p:Platform=$Architecture",
    "-p:RuntimeIdentifier=$runtimeIdentifier",
    '-p:WindowsPackageType=MSIX',
    '-p:GenerateAppxPackageOnBuild=true',
    '-p:AppxBundle=Never',
    '-p:AppxPackageSigningEnabled=false',
    '-p:DebugSymbols=false',
    '-p:DebugType=None',
    "-p:GenerateAppInstallerFile=$generateAppInstallerValue",
    '-p:BuildAppxUploadPackageForUap=false',
    "-p:AppxPackageDir=$appxPackageDirectory",
    "-p:TemnoEnvironment=$Environment",
    # WP-375: the About box shows the package's own version, so the two never disagree.
    "-p:InformationalVersion=$($sourceManifest.Version)"
)

if ($generateAppInstaller) {
    $buildArguments += "-p:AppInstallerUri=$AppInstallerUri"
    $buildArguments += "-p:HoursBetweenUpdateChecks=$UpdateHours"
}

if ($NoRestore) {
    $buildArguments += '--no-restore'
}

Write-Host "Building Temno.App MSIX ($Configuration | $Architecture | $Environment)..." -ForegroundColor Cyan
$buildStarted = [DateTime]::UtcNow
& dotnet @buildArguments
if ($LASTEXITCODE -ne 0) {
    throw "MSIX build failed with exit code $LASTEXITCODE."
}

$matchingPackages = New-Object 'System.Collections.Generic.List[System.IO.FileInfo]'
foreach ($candidate in @(Get-ChildItem -LiteralPath $fullOutputDirectory -Recurse -File -Filter '*.msix')) {
    try {
        $details = Get-TemnoMsixPackageDetails -PackagePath $candidate.FullName
        if ($details.Manifest.IdentityName -eq $sourceManifest.IdentityName) {
            $matchingPackages.Add($candidate)
        }
    }
    catch {
        Write-Verbose "Skipping non-Temno MSIX candidate '$($candidate.FullName)': $($_.Exception.Message)"
    }
}

if ($matchingPackages.Count -ne 1) {
    throw "Expected exactly one Temno MSIX package under '$fullOutputDirectory', found $($matchingPackages.Count). Re-run with -Clean after preserving any artifacts you need."
}

$package = $matchingPackages[0]
# WP-375: a development package is Temno-Dev, the app install-app.ps1 registers and the development update feed
# serves, so it carries that identity before any check or signature.
$desktopApp = Get-TemnoDesktopApp -Environment $Environment
if ($null -ne $desktopApp.Publisher) {
    Set-TemnoPackageIdentity -PackagePath $package.FullName -Name $desktopApp.Name -Publisher $desktopApp.Publisher `
        -DisplayName $desktopApp.DisplayName -PhoneProductId $desktopApp.PhoneProductId | Out-Null
}
$verifyPackageScript = Join-Path $PSScriptRoot 'verify-msix-package.ps1'
& $verifyPackageScript -PackagePath $package.FullName -RequireUnsigned | Out-Null

$packagedWhisperInfo = Assert-TemnoWhisperPackageEntry -PackagePath $package.FullName
Assert-TemnoFfmpegPackage -PackagePath $package.FullName -Pin $ffmpegPin
Assert-TemnoPackageRootNotices -PackagePath $package.FullName -RepoRoot $repoRoot -FfmpegNoticesPath $ffmpegNotices
$packagedVcRuntime = Assert-TemnoVcRuntimePackaged -PackagePath $package.FullName -ExpectedHashes $vcRuntime.Files
# Checked before signing, so a package whose compiled environment disagrees is never signed.
Assert-TemnoArtifactEnvironment -Path $package.FullName -Environment $Environment -Label 'Built MSIX' | Out-Null

if ($signingRequested) {
    $signArguments = @(
        '-PackagePath', $package.FullName,
        '-CertificateStore', $CertificateStore
    )

    if ($hasCertificatePath) {
        $signArguments += '-CertificatePath', $CertificatePath
        if ($null -ne $CertificatePassword) {
            $signArguments += '-CertificatePassword', $CertificatePassword
        }
    }
    else {
        $signArguments += '-CertificateThumbprint', $CertificateThumbprint
    }

    if (-not [string]::IsNullOrWhiteSpace($TimestampUrl)) {
        $signArguments += '-TimestampUrl', $TimestampUrl
    }

    & (Join-Path $PSScriptRoot 'sign-msix.ps1') @signArguments | Out-Null
    & $verifyPackageScript -PackagePath $package.FullName -RequireSignature | Out-Null
}

$appInstallerPath = $null
if ($generateAppInstaller) {
    $appInstallers = @(Get-ChildItem -LiteralPath $fullOutputDirectory -Recurse -File -Filter '*.appinstaller' |
        Where-Object { $_.LastWriteTimeUtc -ge $buildStarted.AddSeconds(-2) })
    if ($appInstallers.Count -ne 1) {
        throw "Expected exactly one newly generated .appinstaller file under '$fullOutputDirectory', found $($appInstallers.Count)."
    }

    $appInstallerPath = $appInstallers[0].FullName
    & (Join-Path $PSScriptRoot 'verify-appinstaller.ps1') -AppInstallerPath $appInstallerPath -PackagePath $package.FullName -ExpectedAppInstallerUri $AppInstallerUri -ExpectedArchitecture $Architecture | Out-Null
}

$builtManifest = (Get-TemnoMsixPackageDetails -PackagePath $package.FullName).Manifest
$result = [pscustomobject]@{
    PackagePath = $package.FullName
    AppInstallerPath = $appInstallerPath
    Environment = $Environment
    GatewayHost = $deployProfile.GatewayHost
    AccountsHost = $deployProfile.AccountsHost
    SupabaseProjectRef = $deployProfile.SupabaseProjectRef
    Signed = $signingRequested
    IdentityName = $builtManifest.IdentityName
    Publisher = $builtManifest.Publisher
    Version = $builtManifest.Version
    WhisperModelBytes = $packagedWhisperInfo.Bytes
    WhisperModelSha256 = $packagedWhisperInfo.Sha256
    FfmpegVersion = $ffmpegPin.Version
    FfmpegSourceCommit = $ffmpegPin.SourceCommit
    FfmpegSourceSha256 = $ffmpegPin.SourceSha256
    FfmpegBuildImage = $ffmpegPin.BuildImage
    VcRuntimeVersion = $packagedVcRuntime.Version
}

Write-Host "Environment: $Environment ($((Get-TemnoDeployTarget -DeployProfile $deployProfile -Service App).Description))" -ForegroundColor DarkGray
if ($signingRequested) {
    Write-Host "Signed package ready: $($result.PackagePath)" -ForegroundColor Green
}
else {
    Write-Host "Unsigned test package ready: $($result.PackagePath)" -ForegroundColor Yellow
    Write-Host 'Unsigned MSIX files cannot be installed on standard Windows. Sign the artifact before installation or publishing.' -ForegroundColor Yellow
}

return $result
