# Builds the FFmpeg that Temno ships (WP-401), from source, in a Linux build image pinned by digest, and fails
# unless every file it writes equals its pin in Temno.FFmpeg.targets (WP-406): the build is reproducible. Needs
# Docker with Linux containers, on Windows or Linux. Temno's source builds do not run this: they restore the
# same files as the NuGet package Temno.FFmpeg.Runtime.win-x64. The public CI's Linux job runs it to check that
# package, and pack-ffmpeg-nuget.ps1 packs its output.
#
# Source:  the FFmpeg release tarball named in the pins (signed by the FFmpeg release key), hash-checked here,
#          plus any upstream fixes in patches\. Since 9.1 (WP-442) there are none: 9.1 carries WP-401's six.
# Image:   BtbN/FFmpeg-Builds win64-gpl-shared: the mingw-w64 cross toolchain and the static
#          x264, x265, zimg, dav1d, libvpl, nv-codec-headers, AMF, zlib and liblzma it links. Pulled by
#          digest from ghcr.io/btbn, or from the owner's copy of the same digest if that one is gone.
# Recipe:  build-in-container.sh (configure line, exclusions).
#
# Linux:   -Rid linux-x64 builds the Gateway's FFmpeg (WP-425) the same way, from the same source and patches, in
#          BtbN's linux64-gpl-shared image, with build-in-container-linux.sh and the pins in
#          Temno.FFmpeg.linux-x64.targets. Its output keeps bin/ and lib/, and this script writes its source.txt.
#
# Usage:
#   .\scripts\ffmpeg\build-ffmpeg.ps1
#   .\scripts\ffmpeg\build-ffmpeg.ps1 -OutDir D:\tmp\ffmpeg-out -Jobs 4
#   .\scripts\ffmpeg\build-ffmpeg.ps1 -AllowUnpinned    # building a new version: report hashes, do not fail
#   .\scripts\ffmpeg\build-ffmpeg.ps1 -Rid linux-x64

[CmdletBinding()]
param(
    [string]$OutDir = "",
    [ValidateRange(1, 64)]
    [int]$Jobs = 8,
    [switch]$AllowUnpinned,
    [ValidateSet("win-x64", "linux-x64")]
    [string]$Rid = "win-x64"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "pins.ps1")
$linux = $Rid -eq "linux-x64"
$pins = if ($linux) { Read-TemnoFFmpegPins -Path (Join-Path $PSScriptRoot "Temno.FFmpeg.linux-x64.targets") } else { Read-TemnoFFmpegPins }
$containerScript = if ($linux) { "build-in-container-linux.sh" } else { "build-in-container.sh" }

$onWindows = $env:OS -eq "Windows_NT"
$TarballName = [System.IO.Path]::GetFileName($pins.SourceUrl)
$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$cacheDir = [System.IO.Path]::Combine($repoRoot, "tools", "ffmpeg", "cache")
if (-not $OutDir) {
    $OutDir = [System.IO.Path]::Combine($repoRoot, "tools", "ffmpeg", "build", $(if ($linux) { "$($pins.Version)-linux-x64" } else { $pins.Version }))
}
$OutDir = [System.IO.Path]::GetFullPath($OutDir)

# Patches and the shell script must reach the container with LF endings whatever the checkout did.
function Copy-TemnoLfFile([string]$Source, [string]$Destination) {
    $bytes = [System.IO.File]::ReadAllBytes($Source)
    $text = [System.Text.Encoding]::UTF8.GetString($bytes).Replace("`r`n", "`n")
    [System.IO.File]::WriteAllBytes($Destination, (New-Object System.Text.UTF8Encoding($false)).GetBytes($text))
}

# The digest is part of each reference, so either copy is the same image byte for byte.
function Resolve-TemnoBuildImage {
    $candidates = @($pins.BuildImage, $pins.BuildImageArchive)
    foreach ($ref in $candidates) {
        & docker image inspect $ref *> $null
        if ($LASTEXITCODE -eq 0) { return $ref }
    }
    foreach ($ref in $candidates) {
        Write-Host "Pulling $ref" -ForegroundColor Cyan
        & docker pull $ref | Out-Host
        if ($LASTEXITCODE -eq 0) { return $ref }
        Write-Warning "Could not pull $ref."
    }
    throw "Neither copy of the FFmpeg build image could be pulled: $($candidates -join ', ')."
}

& docker version --format "{{.Server.Version}}" *> $null
if ($LASTEXITCODE -ne 0) {
    throw "Docker is not running. Building FFmpeg needs Docker with Linux containers."
}

New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
$tarball = Join-Path $cacheDir $TarballName
if (-not ((Test-Path -LiteralPath $tarball) -and ((Get-TemnoSha256 $tarball) -eq $pins.SourceSha256))) {
    $tmp = "$tarball.tmp"
    $curl = if ($onWindows) { "curl.exe" } else { "curl" }
    Write-Host "Downloading $($pins.SourceUrl)" -ForegroundColor Cyan
    & $curl -L --fail --retry 3 --retry-delay 2 -o $tmp $pins.SourceUrl | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "curl exited $LASTEXITCODE downloading $($pins.SourceUrl)." }
    Move-Item -LiteralPath $tmp -Destination $tarball -Force
    $actual = Get-TemnoSha256 $tarball
    if ($actual -ne $pins.SourceSha256) { throw "$TarballName has SHA-256 $actual; expected $($pins.SourceSha256)." }
}

$image = Resolve-TemnoBuildImage
$stage = Join-Path $cacheDir ("stage-" + [guid]::NewGuid().ToString("N"))
$container = "temno-ffbuild-" + [guid]::NewGuid().ToString("N").Substring(0, 12)
New-Item -ItemType Directory -Path (Join-Path $stage "patches") -Force | Out-Null
try {
    Copy-Item -LiteralPath $tarball -Destination (Join-Path $stage $TarballName)
    Copy-TemnoLfFile (Join-Path $PSScriptRoot $containerScript) (Join-Path $stage $containerScript)
    foreach ($patch in Get-TemnoFFmpegPatches) {
        Copy-TemnoLfFile $patch.FullName (Join-Path (Join-Path $stage "patches") $patch.Name)
    }

    Write-Host "Building FFmpeg $($pins.Version) in $image" -ForegroundColor Cyan
    & docker create --name $container -e "TEMNO_FFMPEG_JOBS=$Jobs" $image bash "/work/$containerScript" | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "docker create exited $LASTEXITCODE." }
    & docker cp (Join-Path $stage ".") "${container}:/work" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "docker cp into the build container exited $LASTEXITCODE." }
    & docker start --attach $container | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "The FFmpeg build exited $LASTEXITCODE." }

    if (Test-Path -LiteralPath $OutDir) { Remove-Item -LiteralPath $OutDir -Recurse -Force }
    New-Item -ItemType Directory -Path (Split-Path -Parent $OutDir) -Force | Out-Null
    & docker cp "${container}:/out" $OutDir | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "docker cp out of the build container exited $LASTEXITCODE." }
}
finally {
    & docker rm --force $container *> $null
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}

# The Linux record of source and build image, in the form ensure-ffmpeg.ps1 writes for Windows. CRLF on every
# platform: the file's hash is pinned.
if ($linux) {
    $sourceLines = @(
        "FFmpeg $($pins.Version) for Linux x64"
        "Built by Temno from source with scripts/ffmpeg/build-ffmpeg.ps1 -Rid linux-x64 (WP-425)."
        "Source: $($pins.SourceUrl), SHA-256 $($pins.SourceSha256)"
        "Upstream: https://github.com/FFmpeg/FFmpeg/commit/$($pins.SourceCommit) (the $($pins.SourceRelease) release)"
    ) + @(Get-TemnoFFmpegPatchLines) + @(
        "Build image: $($pins.BuildImage)"
        "Build image source: https://github.com/BtbN/FFmpeg-Builds"
        "License: GNU General Public License version 3. This build uses --enable-gpl and does not use --enable-nonfree."
    )
    [System.IO.File]::WriteAllText((Join-Path $OutDir "source.txt"), (($sourceLines -join "`r`n") + "`r`n"))
}
# The patches this build applied, so a later pack or ensure-ffmpeg.ps1 reuses it only for the same patch set.
[System.IO.File]::WriteAllText((Join-Path $OutDir "patches.txt"), ((@(Get-TemnoFFmpegPatchLines) -join "`n") + "`n"))

# compiler.txt is the toolchain's version line, compiled-sources.txt the files the compiler read, which
# source-notices.ps1 turns into the package's notices, compiled-headers-external.txt (Windows, WP-427) the image's
# headers it read, and patches.txt the patches applied; all are kept for the record, never pinned or shipped. Names
# are relative to the output folder with forward slashes: the Windows build is flat, the Linux one keeps bin/ and lib/.
$diagnostics = @("compiler.txt", "compiled-sources.txt", "compiled-headers-external.txt", "patches.txt")
$files = @(Get-ChildItem -LiteralPath $OutDir -File -Recurse | ForEach-Object {
    $name = [System.IO.Path]::GetRelativePath($OutDir, $_.FullName).Replace('\', '/')
    $pinned = $pins.Files[$name]
    $sha256 = Get-TemnoSha256 $_.FullName
    [pscustomobject]@{
        Name = $name
        Sha256 = $sha256
        Pinned = $pinned
        Matches = ($null -ne $pinned) -and ($sha256 -eq $pinned)
    }
} | Sort-Object Name)
$files | Format-Table Name, Sha256, Matches -AutoSize | Out-String | Write-Host
$problems = @()
foreach ($name in $pins.BuildFiles) {
    $file = $files | Where-Object Name -eq $name
    if ($null -eq $file) { $problems += "$name is missing" }
    elseif (-not $file.Matches) { $problems += "$name has SHA-256 $($file.Sha256), not the pinned $($file.Pinned)" }
}
foreach ($file in @($files | Where-Object { $_.Name -notin $pins.BuildFiles -and $_.Name -notin $diagnostics })) {
    $problems += "$($file.Name) is not a pinned build output"
}
if ($problems.Count -gt 0) {
    $message = "The FFmpeg build in $OutDir does not match scripts/ffmpeg/$([System.IO.Path]::GetFileName($pins.Path)): $($problems -join '; ')."
    if (-not $AllowUnpinned) { throw $message }
    Write-Warning $message
}
else {
    Write-Host "FFmpeg built and every file matches its pin: $OutDir" -ForegroundColor Green
}

[pscustomobject]@{
    Directory = $OutDir
    Image = $image
    SourceSha256 = $pins.SourceSha256
    Pinned = ($problems.Count -eq 0)
    Files = $files
}
