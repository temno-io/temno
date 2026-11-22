# Places Temno's FFmpeg in tools\ffmpeg\win-x64, proves it is the pinned build (WP-401), and returns the manifest
# build-msix.ps1 checks the MSIX against.
#
# A build does not need this script: Temno.App and the Core tests reference the NuGet package
# Temno.FFmpeg.Runtime.win-x64, which restore fetches and scripts\ffmpeg\Temno.FFmpeg.targets checks (WP-406). This
# script serves the MSIX gates, tools that run ffmpeg.exe from tools\ffmpeg\win-x64, and the maintainer who makes
# the package's files from source.
#
# Where the files come from, first match wins:
#   1. A copy already in -DestDir whose every file equals its pin.
#   2. By default, the restored NuGet package. If it is not restored yet, the Core tests are restored, so the
#      package arrives under their lock file. No Docker.
#   3. With -FromSource, a build in tools\ffmpeg\build\<version> whose files equal their pins, or a new one from
#      scripts\ffmpeg\build-ffmpeg.ps1 (needs Docker). configure.txt and source.txt are written here, from the
#      binaries and the patch list, and must equal their pins too.
# Then the binaries are run to check the version, the configure line, the encoders, the protocols, and the
# demuxers and decoders the build leaves out.
#
# Usage:
#   .\scripts\ensure-ffmpeg.ps1
#   .\scripts\ensure-ffmpeg.ps1 -DestDir tools\ffmpeg\win-x64
#   .\scripts\ensure-ffmpeg.ps1 -FromSource -DestDir artifacts\nuget\stage\ffmpeg

[CmdletBinding()]
param(
    [string]$DestDir = "",
    [switch]$FromSource
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "ffmpeg\pins.ps1")
$pins = Read-TemnoFFmpegPins

$Version = $pins.Version
$VersionLine = "ffmpeg version $Version"
$AllNames = @($pins.Files.Keys)
$RuntimeNames = @($AllNames | Where-Object { $_ -like "*.exe" -or $_ -like "*.dll" })

# Components FFmpeg advisories name that the build must not contain (docs/verification/wp-401).
$ExcludedDemuxers = @("dash", "imf", "hls", "rtsp", "sdp", "rtp", "sap", "cdxl", "pcx_pipe", "sunrast_pipe")
$ExcludedDecoders = @("cdxl", "g2m", "pcx", "sunrast")

function Get-TemnoFfmpegNames([string]$Exe, [string]$Flag) {
    $text = & $Exe -hide_banner $Flag 2>&1 | Out-String
    if ($LASTEXITCODE -ne 0) { throw "ffmpeg $Flag exited $LASTEXITCODE." }
    # Each list is a legend, a dashed rule, then one "<flags> <name> <description>" line per entry.
    $names = New-Object System.Collections.Generic.HashSet[string]
    $listing = $false
    foreach ($line in ($text -split "`r?`n")) {
        if ($line -match '^\s*-+\s*$') { $listing = $true; continue }
        $tokens = $line.Trim() -split '\s+'
        if ($listing -and $tokens.Count -ge 2) { [void]$names.Add($tokens[1]) }
    }
    if ($names.Count -eq 0) { throw "ffmpeg $Flag printed no list." }
    return ,$names
}

# The restored package's ffmpeg\ folder, restoring the Core tests first when it is not there.
function Get-TemnoFfmpegPackageDir {
    $relative = Join-Path $pins.PackageId.ToLowerInvariant() (Join-Path $pins.PackageVersion.ToLowerInvariant() "ffmpeg")
    $find = {
        Push-Location $repoRoot
        try { $lines = @(& dotnet nuget locals global-packages --list 2>$null) }
        finally { Pop-Location }
        $line = $lines | Where-Object { $_ -match '^global-packages:' } | Select-Object -First 1
        if ($LASTEXITCODE -ne 0 -or -not $line) { throw "dotnet nuget locals did not name the global packages folder." }
        Join-Path ($line -replace '^global-packages:\s*', '').Trim() $relative
    }
    $dir = & $find
    if (-not (Test-Path -LiteralPath (Join-Path $dir "ffmpeg.exe"))) {
        $tests = Join-Path $repoRoot "tests\Temno.Core.Tests\Temno.Core.Tests.csproj"
        Write-Host "Restoring $($pins.PackageId) $($pins.PackageVersion) with the Core tests" -ForegroundColor Cyan
        & dotnet restore $tests --nologo | Out-Host
        if ($LASTEXITCODE -ne 0) { throw "dotnet restore of $tests exited $LASTEXITCODE." }
        $dir = & $find
    }
    return $dir
}

# Replaces FFmpeg's files in $Destination with $Names from $Source; a previous build (WP-264's Gyan files) may be there.
function Copy-TemnoFfmpegFiles([string]$Source, [string]$Destination, [string[]]$Names) {
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    Get-ChildItem -LiteralPath $Destination -File |
        Where-Object { $_.Name -match '^(ff(mpeg|probe|play)\.exe|(av|sw|postproc)\w*-\d+\.dll|LICENSE|configure\.txt|source\.txt)$' } |
        Remove-Item -Force
    foreach ($name in $Names) {
        Copy-Item -LiteralPath (Join-Path $Source $name) -Destination (Join-Path $Destination $name) -Force
    }
}

$repoRoot = Split-Path -Parent $PSScriptRoot
if (-not $DestDir) {
    $DestDir = Join-Path $repoRoot "tools\ffmpeg\win-x64"
}
$DestDir = [System.IO.Path]::GetFullPath($DestDir)
$buildDir = Join-Path $repoRoot "tools\ffmpeg\build\$Version"

$writeGenerated = $false
$origin = "the verified copy in $DestDir"
if ($null -ne (Get-TemnoFFmpegPinProblem $DestDir $AllNames $pins)) {
    if ($FromSource) {
        if (-not (Test-TemnoFFmpegBuildPatches $buildDir) -or $null -ne (Get-TemnoFFmpegPinProblem $buildDir $pins.BuildFiles $pins)) {
            & (Join-Path $PSScriptRoot "ffmpeg\build-ffmpeg.ps1") -OutDir $buildDir | Out-Null
        }
        Copy-TemnoFfmpegFiles $buildDir $DestDir $pins.BuildFiles
        $writeGenerated = $true
        $origin = "the build from source in $buildDir"
    }
    else {
        $packageDir = Get-TemnoFfmpegPackageDir
        $problem = Get-TemnoFFmpegPinProblem $packageDir $AllNames $pins
        if ($null -ne $problem) {
            throw "The restored $($pins.PackageId) $($pins.PackageVersion) in $packageDir does not match scripts\ffmpeg\Temno.FFmpeg.targets: $problem. Delete $(Split-Path -Parent $packageDir) and restore again."
        }
        Copy-TemnoFfmpegFiles $packageDir $DestDir $AllNames
        $origin = "the NuGet package in $packageDir"
    }
}

# Windows loads most of ffmpeg.exe's imports from its own folder first, so an unpinned DLL or EXE here
# would run inside FFmpeg and ship in the MSIX's ffmpeg\ folder.
$unpinned = @(Get-ChildItem -LiteralPath $DestDir -File |
    Where-Object { $_.Extension -in ".exe", ".dll" -and $_.Name -notin $RuntimeNames } | ForEach-Object Name)
if ($unpinned.Count -gt 0) {
    throw "$DestDir holds files that are not part of the pinned FFmpeg: $($unpinned -join ', '). Remove them."
}

$ffmpegExe = Join-Path $DestDir "ffmpeg.exe"
$ffprobeExe = Join-Path $DestDir "ffprobe.exe"
$versionText = & $ffmpegExe -version 2>&1 | Out-String
if ($LASTEXITCODE -ne 0) { throw "ffmpeg -version exited $LASTEXITCODE." }
if ($versionText -notlike "*$VersionLine*") { throw "ffmpeg -version did not report '$VersionLine'." }
foreach ($flag in @("--enable-gpl", "--enable-version3", "--enable-shared", "--enable-libx264", "--enable-libx265",
        "--disable-network", "--disable-protocols")) {
    if ($versionText -notlike "*$flag*") { throw "ffmpeg configure line is missing $flag." }
}
if ($versionText -like "*--enable-nonfree*") { throw "Refusing a nonfree FFmpeg build." }

$encoders = Get-TemnoFfmpegNames $ffmpegExe "-encoders"
foreach ($name in @("libx264", "libx265")) {
    if (-not $encoders.Contains($name)) { throw "This FFmpeg build does not list the $name encoder." }
}
if (@($encoders | Where-Object { $_ -like "libfdk*" }).Count -gt 0) {
    throw "Refusing an FFmpeg build that lists libfdk (a nonfree AAC encoder)."
}

$protocols = (& $ffmpegExe -hide_banner -protocols 2>&1 | Out-String) -split '\s+' | Where-Object { $_ }
$inputProtocols = $protocols[([array]::IndexOf($protocols, "Input:") + 1)..([array]::IndexOf($protocols, "Output:") - 1)]
if (($inputProtocols -join ",") -ne "file,pipe") {
    throw "This FFmpeg build opens protocols other than file and pipe: $($inputProtocols -join ', ')."
}
$demuxers = Get-TemnoFfmpegNames $ffmpegExe "-demuxers"
$decoders = Get-TemnoFfmpegNames $ffmpegExe "-decoders"
foreach ($name in $ExcludedDemuxers) {
    if ($demuxers.Contains($name)) { throw "This FFmpeg build contains the excluded $name demuxer." }
}
foreach ($name in $ExcludedDecoders) {
    if ($decoders.Contains($name)) { throw "This FFmpeg build contains the excluded $name decoder." }
}

& $ffprobeExe -version 2>&1 | Out-Null
if ($LASTEXITCODE -ne 0) { throw "ffprobe -version exited $LASTEXITCODE." }

if ($writeGenerated) {
    $configuration = $null
    foreach ($line in ($versionText -split "`r?`n")) {
        if ($line.StartsWith("configuration:")) {
            $configuration = $line.Trim()
            break
        }
    }
    if ([string]::IsNullOrWhiteSpace($configuration)) { throw "ffmpeg -version did not print a configuration line." }
    [System.IO.File]::WriteAllText((Join-Path $DestDir "configure.txt"), "$VersionLine`r`n$configuration`r`n")

    $sourceLines = @(
        "FFmpeg $Version"
        "Built by Temno from source with scripts/ffmpeg/build-ffmpeg.ps1 (WP-401)."
        "Source: $($pins.SourceUrl), SHA-256 $($pins.SourceSha256)"
        "Upstream: https://github.com/FFmpeg/FFmpeg/commit/$($pins.SourceCommit) (the $($pins.SourceRelease) release)"
    ) + @(Get-TemnoFFmpegPatchLines) + @(
        "Build image: $($pins.BuildImage)"
        "Build image source: https://github.com/BtbN/FFmpeg-Builds"
        "License: GNU General Public License version 3. This build uses --enable-gpl and does not use --enable-nonfree."
    )
    # CRLF on every platform: the file's hash is pinned.
    [System.IO.File]::WriteAllText((Join-Path $DestDir "source.txt"), (($sourceLines -join "`r`n") + "`r`n"))

    $problem = Get-TemnoFFmpegPinProblem $DestDir @("configure.txt", "source.txt") $pins
    if ($null -ne $problem) {
        throw "The configure line or source record written in $DestDir does not match scripts\ffmpeg\Temno.FFmpeg.targets: $problem."
    }
}

$problem = Get-TemnoFFmpegPinProblem $DestDir $AllNames $pins
if ($null -ne $problem) { throw "Copying FFmpeg into $DestDir did not verify: $problem." }

$files = New-Object System.Collections.Generic.List[object]
foreach ($name in $AllNames) {
    $files.Add([pscustomobject]@{
        Name = $name
        Entry = "ffmpeg/$name"
        Sha256 = $pins.Files[$name]
    })
}

Write-Host "FFmpeg verified: $VersionLine, from $origin" -ForegroundColor Green
Write-Host "  $DestDir" -ForegroundColor DarkGray

[pscustomobject]@{
    Directory = $DestDir
    Version = $VersionLine
    PackageId = $pins.PackageId
    PackageVersion = $pins.PackageVersion
    SourceCommit = $pins.SourceCommit
    SourceSha256 = $pins.SourceSha256
    BuildImage = $pins.BuildImage
    Files = $files
}
