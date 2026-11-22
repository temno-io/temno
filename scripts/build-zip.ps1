# Builds Temno-win-x64.zip: Temno as a folder to extract and run (WP-451). It is the production app, self-contained,
# with the pinned FFmpeg, the pinned Whisper model, the Visual C++ runtime and the notices inside. The finished ZIP
# passes the checks build-msix.ps1 runs on the MSIX (lib\app-payload.ps1), or the script fails and leaves no ZIP.
#
# The ZIP installs nothing and is unsigned: Windows shows its SmartScreen notice the first time it runs.
# A copy run from the ZIP does not update itself.
#
#   .\scripts\build-zip.ps1                  builds artifacts\zip\Temno-win-x64.zip
#   .\scripts\build-zip.ps1 -Official        Temno's own release: About shows no "Built from source"
#   .\scripts\build-zip.ps1 -Verify <zip>    checks an existing ZIP and builds nothing
#
# Needs Windows x64, the .NET 10 SDK, PowerShell 7 and the Visual C++ runtime this script pins.
[CmdletBinding()]
param(
    # Where the ZIP is written; artifacts\zip when empty. Only the file Temno-win-x64.zip is replaced there: the
    # folder it is made from is always this repository's artifacts\zip\stage.
    [string]$OutputDirectory,

    # An existing Temno-win-x64.zip to check. Nothing is built.
    [string]$Verify,

    # Stamps the build official (TemnoOfficialBuild). A label for support, not a security control: without it the
    # build says it was built from source and tells its user to merge the next release instead of downloading it.
    [switch]$Official
)

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'lib\msix-common.ps1')
. (Join-Path $PSScriptRoot 'lib\temno-environment.ps1')
. (Join-Path $PSScriptRoot 'lib\app-payload.ps1')

$zipName = 'Temno-win-x64.zip'
# Explorer's own extractor stops at 260 characters. Extracted to Downloads\Temno-win-x64 under a long user name, the
# folder takes about 60 of them, so no entry may be longer than this.
$longestEntryAllowed = 180
$repoRoot = Get-TemnoRepoRoot -StartPath $PSScriptRoot
$projectPath = Join-Path $repoRoot 'src\Temno.App\Temno.App.csproj'
$coreProjectPath = Join-Path $repoRoot 'src\Temno.Core\Temno.Core.csproj'
$whisperModelPath = Join-Path $repoRoot 'tools\whisper\ggml-base.en.bin'

# Every check on a finished ZIP, in the order a wrong build is cheapest to refuse. Returns what the ZIP is.
function Assert-TemnoZipPayload {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath,

        [Parameter(Mandatory)]
        $FfmpegPin,

        [Parameter(Mandatory)]
        [string]$FfmpegNoticesPath
    )

    $stamp = Get-TemnoAppZipStamp -PackagePath $PackagePath
    if ($stamp.Environment -cne 'production') {
        throw "Package '$PackagePath' was built for $($stamp.Environment). $zipName is the production app; a development build is not distributed."
    }
    Assert-TemnoMsixVersion -Version $stamp.Version -Source "Package '$PackagePath'"

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $names = @($archive.Entries | ForEach-Object { $_.FullName.Replace('\', '/') })
    }
    finally {
        $archive.Dispose()
    }
    # The checks below read the first entry of a name. Two entries that extract to one file would leave the other
    # unread, and an extractor keeps whichever it likes, so every entry must land on a path of its own, inside the folder.
    $landing = @{}
    foreach ($name in $names) {
        $extracted = Get-TemnoZipEntryExtractedName -FullName $name
        if ($null -eq $extracted) {
            throw "Package '$PackagePath' has an entry that climbs out of the folder it is extracted to: '$name'."
        }
        $key = $extracted.ToLowerInvariant()
        if ($landing.ContainsKey($key)) {
            throw "Package '$PackagePath' has two entries that extract to one file: '$($landing[$key])' and '$name'."
        }
        $landing[$key] = $name
    }
    if ($names -cnotcontains 'Temno.App.exe') {
        throw "Package '$PackagePath' has no Temno.App.exe at its root. The app must sit at the root, not in a folder."
    }
    $debugFiles = @($names | Where-Object { $_ -like '*.pdb' })
    if ($debugFiles.Count -gt 0) {
        throw "Package '$PackagePath' carries debug symbols: $($debugFiles -join ', ')."
    }
    $longest = $names | Sort-Object Length -Descending | Select-Object -First 1
    if ($longest.Length -gt $longestEntryAllowed) {
        throw "Package '$PackagePath' has an entry of $($longest.Length) characters ($longest). Explorer cannot extract a path over 260, so entries stop at $longestEntryAllowed."
    }

    $whisper = Assert-TemnoWhisperPackageEntry -PackagePath $PackagePath
    Assert-TemnoFfmpegPackage -PackagePath $PackagePath -Pin $FfmpegPin
    Assert-TemnoPackageRootNotices -PackagePath $PackagePath -RepoRoot $repoRoot -FfmpegNoticesPath $FfmpegNoticesPath
    Assert-TemnoVcRuntimePackaged -PackagePath $PackagePath

    $file = Get-Item -LiteralPath $PackagePath
    return [pscustomobject]@{
        PackagePath = $file.FullName
        Bytes = $file.Length
        Sha256 = (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
        Environment = $stamp.Environment
        Official = $stamp.Official
        Version = $stamp.Version
        Entries = $names.Count
        LongestEntry = $longest.Length
        WhisperModelSha256 = $whisper.Sha256
        FfmpegVersion = $FfmpegPin.Version
        FfmpegSourceCommit = $FfmpegPin.SourceCommit
        VcRuntimeVersion = $script:TemnoVcRuntimeVersion
    }
}

function Write-TemnoZipSummary {
    param([Parameter(Mandatory)] $Result, [Parameter(Mandatory)] [string]$Verb)

    $stampText = if ($Result.Official) { 'official' } else { 'built from source' }
    Write-Host "$Verb $($Result.PackagePath)" -ForegroundColor Green
    Write-Host "  Version      $($Result.Version) ($($Result.Environment), $stampText)"
    Write-Host "  Size         $($Result.Bytes) bytes in $($Result.Entries) entries; the longest path is $($Result.LongestEntry) characters"
    Write-Host "  SHA-256      $($Result.Sha256)"
    Write-Host "  Checked      Whisper model $($Result.WhisperModelSha256.Substring(0, 12).ToLowerInvariant()), $($Result.FfmpegVersion), the notices, VC++ runtime $($Result.VcRuntimeVersion) for every import"
    Write-Host '  Unsigned. Windows shows its SmartScreen notice the first time it runs.' -ForegroundColor Yellow
}

# The pinned FFmpeg's manifest, which the ZIP's ffmpeg\ must equal file for file, and its notices.
$ffmpegPin = & (Join-Path $PSScriptRoot 'ensure-ffmpeg.ps1')
if ($null -eq $ffmpegPin -or $null -eq $ffmpegPin.Files) {
    throw 'ensure-ffmpeg.ps1 did not return the pinned FFmpeg manifest.'
}
$ffmpegNotices = Get-TemnoFfmpegNoticesPath -Pin $ffmpegPin

if ($PSBoundParameters.ContainsKey('Verify')) {
    if ([string]::IsNullOrWhiteSpace($Verify)) { throw '-Verify needs the path of a ZIP. Nothing was built.' }
    $existing = (Resolve-Path -LiteralPath $Verify -ErrorAction Stop).Path
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    try { [System.IO.Compression.ZipFile]::OpenRead($existing).Dispose() }
    catch { throw "'$existing' is not a readable ZIP: $($_.Exception.Message)" }
    $result = Assert-TemnoZipPayload -PackagePath $existing -FfmpegPin $ffmpegPin -FfmpegNoticesPath $ffmpegNotices
    Write-TemnoZipSummary -Result $result -Verb 'Verified'
    Write-Host '  Verified only. Nothing was built.'
    return $result
}

if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repoRoot 'artifacts\zip'
}
# Against PowerShell's own location: .NET resolves a relative path against the process's, which can be another folder.
$fullOutputDirectory = $ExecutionContext.SessionState.Path.GetUnresolvedProviderPathFromPSPath($OutputDirectory)
# The published folder is deleted before each build, so it is always this script's own. A folder named
# Temno-win-x64 beside the ZIP may be somebody's extracted copy.
$stage = Join-Path $repoRoot 'artifacts\zip\stage\Temno-win-x64'
$zipPath = Join-Path $fullOutputDirectory $zipName
$buildingPath = "$zipPath.building"

& (Join-Path $PSScriptRoot 'ensure-whisper-model.ps1')
$whisperModelInfo = Assert-TemnoWhisperModelFile -Path $whisperModelPath
Write-Host "Whisper model verified: $($whisperModelInfo.Bytes) bytes, SHA-256 $($whisperModelInfo.Sha256)" -ForegroundColor DarkGray

# Temno.App.csproj ships the runtime only when this folder holds it, so a clean clone would publish without it.
$vcRuntimeDir = Join-Path $repoRoot 'tools\vcruntime'
Copy-TemnoVcRuntime -Destination $vcRuntimeDir
Write-Host "VC++ runtime staged: $vcRuntimeDir" -ForegroundColor DarkGray

# The version Temno.Core compiles in and compares the update feed with: Temno.Release.props in a release commit of
# the public repository, otherwise the manifest's. About shows the same number.
$version = "$(& dotnet msbuild $coreProjectPath -nologo -getProperty:TemnoAppVersion)".Trim()
if ($LASTEXITCODE -ne 0) { throw "dotnet msbuild could not read TemnoAppVersion (exit code $LASTEXITCODE)." }
Assert-TemnoMsixVersion -Version $version -Source 'TemnoAppVersion'

New-Item -ItemType Directory -Path $fullOutputDirectory -Force | Out-Null
if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
foreach ($stale in @($zipPath, $buildingPath)) {
    if (Test-Path -LiteralPath $stale -PathType Container) { throw "'$stale' is a folder. $zipName is written as a file; move the folder away first." }
    if (Test-Path -LiteralPath $stale) { Remove-Item -LiteralPath $stale -Force }
}

$officialValue = if ($Official) { 'true' } else { 'false' }
$publishArguments = @(
    'publish',
    $projectPath,
    '-c', 'Release',
    '-p:Platform=x64',
    '-p:RuntimeIdentifier=win-x64',
    '-p:WindowsPackageType=None',
    '-p:WindowsAppSDKSelfContained=true',
    '-p:SelfContained=true',
    '-p:PublishSingleFile=false',
    '-p:PublishTrimmed=false',
    '-p:DebugSymbols=false',
    '-p:DebugType=None',
    '-p:TemnoEnvironment=production',
    "-p:TemnoOfficialBuild=$officialValue",
    "-p:InformationalVersion=$version",
    '-o', $stage
)

Write-Host "Publishing Temno $version (Release | x64 | production | $(if ($Official) { 'official' } else { 'built from source' }))..." -ForegroundColor Cyan
& dotnet @publishArguments
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE."
}
if (-not (Test-Path -LiteralPath (Join-Path $stage 'Temno.App.exe'))) {
    throw "dotnet publish left no Temno.App.exe in '$stage'."
}

# The app at the archive root: Explorer's Extract All makes the folder Temno-win-x64 itself.
Add-Type -AssemblyName System.IO.Compression.FileSystem
[System.IO.Compression.ZipFile]::CreateFromDirectory($stage, $buildingPath, [System.IO.Compression.CompressionLevel]::Optimal, $false)

# Checked under a name nobody uploads, so a ZIP that fails is never left as Temno-win-x64.zip.
try {
    $result = Assert-TemnoZipPayload -PackagePath $buildingPath -FfmpegPin $ffmpegPin -FfmpegNoticesPath $ffmpegNotices
    if ($result.Version -ne $version -or $result.Official -ne [bool]$Official) {
        throw "The ZIP's Temno.Core says version $($result.Version), official $($result.Official); this run built $version, official $([bool]$Official)."
    }
}
catch {
    Remove-Item -LiteralPath $buildingPath -Force -ErrorAction SilentlyContinue
    throw
}
Move-Item -LiteralPath $buildingPath -Destination $zipPath
$result.PackagePath = $zipPath

Write-TemnoZipSummary -Result $result -Verb 'Built'
return $result
