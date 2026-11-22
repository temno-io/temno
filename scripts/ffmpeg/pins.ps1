# Reads the FFmpeg pins and source facts from Temno.FFmpeg.targets, their only home (WP-406). Dot-source it:
#   . (Join-Path $PSScriptRoot 'pins.ps1')        # from scripts\ffmpeg
#   $pins = Read-TemnoFFmpegPins

function Read-TemnoFFmpegPins {
    param([string]$Path = (Join-Path $PSScriptRoot 'Temno.FFmpeg.targets'))

    # XPath rather than the XML adapter's properties: callers such as build-msix.ps1 run under Set-StrictMode.
    [xml]$xml = [System.IO.File]::ReadAllText($Path)
    $properties = @{}
    foreach ($node in $xml.SelectNodes('/Project/PropertyGroup/*')) {
        $properties[$node.LocalName] = $node.InnerText.Trim()
    }
    $files = [ordered]@{}
    $origins = @{}
    foreach ($pin in $xml.SelectNodes('/Project/ItemGroup/TemnoFFmpegPin')) {
        $name = $pin.GetAttribute('Include')
        $files[$name] = $pin.GetAttribute('Sha256').ToUpperInvariant()
        $origins[$name] = $pin.GetAttribute('Origin')
    }
    foreach ($name in 'TemnoFFmpegPackageId', 'TemnoFFmpegVersion', 'TemnoFFmpegSourceUrl', 'TemnoFFmpegSourceSha256',
            'TemnoFFmpegSourceCommit', 'TemnoFFmpegBuildImage', 'TemnoFFmpegBuildImageArchive', 'TemnoFFmpegBuildImageDigest') {
        if (-not $properties[$name]) { throw "$Path does not set $name." }
    }
    if ($files.Count -eq 0) { throw "$Path pins no files." }

    [pscustomobject]@{
        Path = $Path
        PackageId = $properties.TemnoFFmpegPackageId
        # The build's version, which ffmpeg -version reports and the pinned files carry.
        Version = $properties.TemnoFFmpegVersion
        # The NuGet package's version. A later package may carry the same pinned files with new notices (WP-427), so it
        # can differ from Version; it equals Version unless TemnoFFmpegPackageVersion says otherwise.
        PackageVersion = $(if ($properties.TemnoFFmpegPackageVersion) { $properties.TemnoFFmpegPackageVersion } else { $properties.TemnoFFmpegVersion })
        SourceUrl = $properties.TemnoFFmpegSourceUrl
        # The FFmpeg release the tarball holds, from its name: "9.1" for ffmpeg-9.1.tar.xz.
        SourceRelease = ([System.IO.Path]::GetFileName($properties.TemnoFFmpegSourceUrl) -replace '^ffmpeg-(.+)\.tar\.xz$', '$1')
        SourceSha256 = $properties.TemnoFFmpegSourceSha256.ToUpperInvariant()
        SourceCommit = $properties.TemnoFFmpegSourceCommit
        BuildImage = "$($properties.TemnoFFmpegBuildImage)@$($properties.TemnoFFmpegBuildImageDigest)"
        BuildImageArchive = "$($properties.TemnoFFmpegBuildImageArchive)@$($properties.TemnoFFmpegBuildImageDigest)"
        # Name -> SHA-256 for every file in the package's ffmpeg\ folder.
        Files = $files
        # The files build-in-container.sh writes; the rest ensure-ffmpeg.ps1 writes from them.
        BuildFiles = @($files.Keys | Where-Object { $origins[$_] -eq 'build' })
    }
}

# The upstream fixes applied on top of the release tarball, in order. None since 9.1 (WP-442): the folder may be absent.
function Get-TemnoFFmpegPatches {
    $dir = Join-Path $PSScriptRoot 'patches'
    if (-not (Test-Path -LiteralPath $dir)) { return @() }
    return @(Get-ChildItem -LiteralPath $dir -Filter '*.patch' | Sort-Object Name)
}

# The lines source.txt gives the patches: one per patch with its FFmpeg commit, or a line saying there are none.
function Get-TemnoFFmpegPatchLines {
    $patches = @(Get-TemnoFFmpegPatches)
    if ($patches.Count -eq 0) { return @("Patches: none; the release tarball is built as published.") }
    return @("Patches from FFmpeg master, in scripts/ffmpeg/patches:") + @($patches | ForEach-Object {
        $commit = (Get-Content -LiteralPath $_.FullName -TotalCount 1) -replace '^From (\w+) .*$', '$1'
        "  $($_.Name) (FFmpeg commit $commit)"
    })
}

# True when the build in $Directory was made with the patches scripts/ffmpeg/patches holds now: build-ffmpeg.ps1 writes
# its patch lines to patches.txt (a diagnostic, never pinned or shipped). A cached build is reused only then, so new
# patches can never be packed beside binaries built without them (WP-442 T1).
function Test-TemnoFFmpegBuildPatches([string]$Directory) {
    $record = Join-Path $Directory 'patches.txt'
    if (-not (Test-Path -LiteralPath $record)) { return $false }
    return [System.IO.File]::ReadAllText($record) -ceq ((@(Get-TemnoFFmpegPatchLines) -join "`n") + "`n")
}

function Get-TemnoSha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToUpperInvariant()
}

# Null when $Directory holds the named pinned files with their hashes; otherwise what is wrong.
function Get-TemnoFFmpegPinProblem([string]$Directory, [string[]]$Names, $Pins) {
    $problems = @()
    foreach ($name in $Names) {
        $path = Join-Path $Directory $name
        if (-not (Test-Path -LiteralPath $path)) { $problems += "$name is missing"; continue }
        $actual = Get-TemnoSha256 $path
        if ($actual -ne $Pins.Files[$name]) { $problems += "$name has SHA-256 $actual, not the pinned $($Pins.Files[$name])" }
    }
    if ($problems.Count -eq 0) { return $null }
    return $problems -join "; "
}
