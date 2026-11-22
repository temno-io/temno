# Packs Temno.FFmpeg.Runtime.win-x64 (WP-406) from FFmpeg built from source, and proves the package's ffmpeg\
# folder holds exactly the files pinned in Temno.FFmpeg.targets. Needs the .NET SDK, and Docker with Linux
# containers unless tools\ffmpeg\build\<version> already holds a build whose files equal their pins and that wrote
# compiled-sources.txt, from which source-notices.ps1 writes FFMPEG-SOURCE-NOTICES.md (WP-427).
#
# Publishing is the owner's step, once per version (AGENTS.md sacred constraint 6):
#   dotnet nuget push <package> --source https://api.nuget.org/v3/index.json --api-key <key>
# nuget.org adds its repository signature. The lock files' contentHash leaves signatures out, so they stay valid.
#
# -Rid linux-x64 packs Temno.FFmpeg.Runtime.linux-x64 (WP-425), the Gateway's FFmpeg, from the pinned output of
# build-ffmpeg.ps1 -Rid linux-x64 (reused when every file still equals its pin). Its ffmpeg/ folder keeps bin/ and
# lib/, and it carries no MSBuild file: the Gateway's Dockerfile takes it by SHA-256.
#
# Usage:
#   .\scripts\ffmpeg\pack-ffmpeg-nuget.ps1
#   .\scripts\ffmpeg\pack-ffmpeg-nuget.ps1 -OutputDirectory artifacts\nuget
#   .\scripts\ffmpeg\pack-ffmpeg-nuget.ps1 -Rid linux-x64

[CmdletBinding()]
param(
    [string]$OutputDirectory = "artifacts\nuget",
    [ValidateSet("win-x64", "linux-x64")]
    [string]$Rid = "win-x64"
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "pins.ps1")
$linux = $Rid -eq "linux-x64"
$pins = if ($linux) { Read-TemnoFFmpegPins -Path (Join-Path $PSScriptRoot "Temno.FFmpeg.linux-x64.targets") } else { Read-TemnoFFmpegPins }

$scriptsDir = Split-Path -Parent $PSScriptRoot
$repoRoot = Split-Path -Parent $scriptsDir
$nugetDir = Join-Path $PSScriptRoot "nuget"
$id = $pins.PackageId
if (-not [System.IO.Path]::IsPathRooted($OutputDirectory)) { $OutputDirectory = Join-Path $repoRoot $OutputDirectory }
$OutputDirectory = [System.IO.Path]::GetFullPath($OutputDirectory)
$packageName = "$id.$($pins.PackageVersion).nupkg"
$packagePath = Join-Path $OutputDirectory $packageName

$nuspecPath = Join-Path $nugetDir "$id.nuspec"
[xml]$nuspec = [System.IO.File]::ReadAllText($nuspecPath)
if ($nuspec.package.metadata.id -ne $id) { throw "$nuspecPath names package $($nuspec.package.metadata.id), not $id." }

$stage = Join-Path $OutputDirectory ("stage-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $stage -Force | Out-Null
try {
    # 1. The payload: built from source, checked file by file, and run (ensure-ffmpeg.ps1 on Windows; on Linux the
    #    build container runs the same checks, since only it can run the binaries).
    if ($linux) {
        $built = [System.IO.Path]::Combine($repoRoot, "tools", "ffmpeg", "build", "$($pins.Version)-linux-x64")
        # The build's compiled-sources.txt feeds the source notices (step 2), so a build without it is built again; so is
        # one made with other patches than the recipe below carries.
        $current = (Test-Path -LiteralPath (Join-Path $built "compiled-sources.txt")) -and (Test-TemnoFFmpegBuildPatches $built) -and @($pins.Files.Keys | Where-Object {
            $file = Join-Path $built $_
            -not (Test-Path -LiteralPath $file) -or (Get-TemnoSha256 $file) -ne $pins.Files[$_]
        }).Count -eq 0
        if (-not $current) { $null = & (Join-Path $PSScriptRoot "build-ffmpeg.ps1") -Rid linux-x64 -OutDir $built }
        foreach ($name in $pins.Files.Keys) {
            $target = Join-Path (Join-Path $stage "ffmpeg") $name
            New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
            Copy-Item -LiteralPath (Join-Path $built $name) -Destination $target
        }
        $manifest = [pscustomobject]@{ Files = $pins.Files }
    }
    else {
        # As on Linux, a build without compiled-sources.txt (one from before WP-427) is built again; ensure-ffmpeg.ps1
        # then takes this build's files, writes configure.txt and source.txt, and runs the binaries.
        $built = [System.IO.Path]::Combine($repoRoot, "tools", "ffmpeg", "build", $pins.Version)
        $current = (Test-Path -LiteralPath (Join-Path $built "compiled-sources.txt")) -and (Test-TemnoFFmpegBuildPatches $built) -and
            ($null -eq (Get-TemnoFFmpegPinProblem $built $pins.BuildFiles $pins))
        if (-not $current) { $null = & (Join-Path $PSScriptRoot "build-ffmpeg.ps1") -OutDir $built }
        $manifest = & (Join-Path $scriptsDir "ensure-ffmpeg.ps1") -FromSource -DestDir (Join-Path $stage "ffmpeg")
    }
    $stagedFfmpeg = Join-Path $stage "ffmpeg"
    $present = @(Get-ChildItem -LiteralPath $stagedFfmpeg -File -Recurse |
        ForEach-Object { [System.IO.Path]::GetRelativePath($stagedFfmpeg, $_.FullName).Replace('\', '/') } | Sort-Object)
    $expected = @($pins.Files.Keys | Sort-Object)
    if (($present -join "|") -ne ($expected -join "|")) {
        throw "The staged ffmpeg folder holds $($present -join ', '); the pins name $($expected -join ', ')."
    }

    # 2. The build file, the readme, the notices for what the DLLs link, and the recipe that rebuilds the payload.
    if (-not $linux) {
        New-Item -ItemType Directory -Path (Join-Path $stage "build") -Force | Out-Null
        # Kept beside the nuspec here (the repository ignores build folders); NuGet imports it from build\ in the package.
        Copy-Item -LiteralPath (Join-Path $nugetDir "$id.targets") -Destination (Join-Path $stage "build\$id.targets")
    }
    Copy-Item -LiteralPath (Join-Path $nugetDir $(if ($linux) { "README.linux-x64.md" } else { "README.md" })) -Destination (Join-Path $stage "README.md")
    Copy-Item -LiteralPath $nuspecPath -Destination (Join-Path $stage "$id.nuspec")

    $notices = [System.IO.File]::ReadAllText((Join-Path $repoRoot "THIRD-PARTY-NOTICES.md")) -replace "`r`n", "`n"
    $sections = [regex]::Split($notices, '(?m)^(?=## )')
    # Each build has its own section, matched by its whole heading line. Both link MIT code (Windows: libvpl, AMF and
    # NVIDIA headers, winpthreads; both: x265's json11) and ISC code (x86inc.asm in x264, x265 and dav1d), whose
    # license texts are the shared MIT and ISC sections.
    $heading = if ($linux) { "## FFmpeg $($pins.Version) for Linux x64" } else { "## FFmpeg $($pins.Version)" }
    $ffmpegSection = @($sections | Where-Object { ($_ -split "`n", 2)[0].TrimEnd() -eq $heading })
    $sharedSections = foreach ($shared in "## MIT license", "## ISC license") {
        $found = @($sections | Where-Object { ($_ -split "`n", 2)[0].TrimEnd() -eq $shared })
        if ($found.Count -ne 1) { throw "THIRD-PARTY-NOTICES.md must have one '$shared' section." }
        $found[0].TrimEnd()
    }
    if ($ffmpegSection.Count -ne 1) { throw "THIRD-PARTY-NOTICES.md must have one '$heading' section." }
    $packageNotices = "# Third-party notices`n`nThe FFmpeg build in this package, from Temno's THIRD-PARTY-NOTICES.md.`n`n" +
        $ffmpegSection[0].TrimEnd() + "`n`n" + ($sharedSections -join "`n`n") + "`n"
    # Links into this repository do not resolve inside a package; keep their text.
    $packageNotices = [regex]::Replace($packageNotices, '\[([^\]]+)\]\((?!https?:)[^)]+\)', '$1')
    [System.IO.File]::WriteAllText((Join-Path $stage "THIRD-PARTY-NOTICES.md"), $packageNotices.Replace("`n", "`r`n"))

    # FFmpeg's own files under BSD, MIT, ISC, zlib or IJG terms, as this build compiled them, from the pinned tarball
    # that build-ffmpeg.ps1 downloaded and checked, and the notices of the libraries each build links (WP-425, WP-427).
    $tarball = [System.IO.Path]::Combine($repoRoot, "tools", "ffmpeg", "cache", [System.IO.Path]::GetFileName($pins.SourceUrl))
    if (-not (Test-Path -LiteralPath $tarball) -or (Get-TemnoSha256 $tarball) -ne $pins.SourceSha256) {
        throw "$tarball is missing or is not the pinned source ($($pins.SourceSha256))."
    }
    $null = & (Join-Path $PSScriptRoot "source-notices.ps1") -Rid $Rid -Tarball $tarball `
        -SourceRoot "ffmpeg-$($pins.SourceRelease)" -Title "FFmpeg $($pins.SourceRelease)" `
        -CompiledList (Join-Path $built "compiled-sources.txt") -OutFile (Join-Path $stage "FFMPEG-SOURCE-NOTICES.md")

    $recipe = Join-Path $stage "source\scripts"
    New-Item -ItemType Directory -Path (Join-Path $recipe "ffmpeg") -Force | Out-Null
    $recipeFiles = if ($linux) { "build-ffmpeg.ps1", "build-in-container-linux.sh", "pins.ps1", "source-notices.ps1", "source-notices.psd1", "Temno.FFmpeg.linux-x64.targets" }
        else { "build-ffmpeg.ps1", "build-in-container.sh", "pins.ps1", "source-notices.ps1", "source-notices.psd1", "Temno.FFmpeg.targets" }
    if (-not $linux) { Copy-Item -LiteralPath (Join-Path $scriptsDir "ensure-ffmpeg.ps1") -Destination (Join-Path $recipe "ensure-ffmpeg.ps1") }
    foreach ($name in $recipeFiles) {
        Copy-Item -LiteralPath (Join-Path $PSScriptRoot $name) -Destination (Join-Path $recipe "ffmpeg\$name")
    }
    # Upstream fixes ahead of the release, if any: none since 9.1 (WP-442), so the recipe has no patches folder.
    foreach ($patch in Get-TemnoFFmpegPatches) {
        New-Item -ItemType Directory -Path (Join-Path $recipe "ffmpeg\patches") -Force | Out-Null
        Copy-Item -LiteralPath $patch.FullName -Destination (Join-Path $recipe "ffmpeg\patches\$($patch.Name)")
    }

    # 3. Pack. The project only carries the nuspec; nothing of this repository's build settings applies to it.
    @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>netstandard2.0</TargetFramework>
    <IncludeBuildOutput>false</IncludeBuildOutput>
    <NuspecFile>$id.nuspec</NuspecFile>
    <NuspecBasePath>`$(MSBuildProjectDirectory)</NuspecBasePath>
    <NuspecProperties>version=$($pins.PackageVersion)</NuspecProperties>
    <!-- With these, two packs hold the same entries in the same order; only the zip timestamp, the pack's time, differs.
         The lock files pin the one package that is published. -->
    <Deterministic>true</Deterministic>
    <DeterministicTimestamp>true</DeterministicTimestamp>
    <!-- Native files outside lib\ and scripts outside tools\ are what this package is. -->
    <NoWarn>`$(NoWarn);NU5100;NU5110;NU5111</NoWarn>
  </PropertyGroup>
</Project>
"@ | Set-Content -LiteralPath (Join-Path $stage "pack.csproj") -Encoding utf8
    if (Test-Path -LiteralPath $packagePath) { Remove-Item -LiteralPath $packagePath -Force }
    & dotnet pack (Join-Path $stage "pack.csproj") -o $OutputDirectory --nologo `
        -p:ImportDirectoryBuildProps=false -p:ImportDirectoryBuildTargets=false | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "dotnet pack exited $LASTEXITCODE." }
}
finally {
    if (Test-Path -LiteralPath $stage) { Remove-Item -LiteralPath $stage -Recurse -Force }
}

# 4. Check the package itself: ffmpeg\ holds exactly the pinned files, and the metadata is what the nuspec says.
Add-Type -AssemblyName System.IO.Compression.FileSystem
$archive = [System.IO.Compression.ZipFile]::OpenRead($packagePath)
try {
    $entries = @($archive.Entries | Where-Object { $_.FullName -like "ffmpeg/*" })
    $names = @($entries | ForEach-Object { $_.FullName.Substring("ffmpeg/".Length) } | Sort-Object)
    if (($names -join "|") -ne (@($pins.Files.Keys | Sort-Object) -join "|")) {
        throw "$packageName's ffmpeg folder holds $($names -join ', '), not exactly the pinned files."
    }
    foreach ($entry in $entries) {
        $stream = $entry.Open()
        try {
            $sha = [System.Security.Cryptography.SHA256]::Create()
            $actual = ([System.BitConverter]::ToString($sha.ComputeHash($stream))).Replace("-", "")
        }
        finally { $stream.Dispose() }
        $name = $entry.FullName.Substring("ffmpeg/".Length)
        if ($actual -ne $pins.Files[$name]) { throw "$packageName's $($entry.FullName) has SHA-256 $actual, not $($pins.Files[$name])." }
    }
    $nuspecEntry = $archive.GetEntry("$id.nuspec")
    $reader = New-Object System.IO.StreamReader($nuspecEntry.Open())
    try { [xml]$packed = $reader.ReadToEnd() } finally { $reader.Dispose() }
    $metadata = $packed.package.metadata
    if ($metadata.id -ne $id -or $metadata.version -ne $pins.PackageVersion -or $metadata.license.'#text' -ne "GPL-3.0-or-later") {
        throw "$packageName's nuspec says $($metadata.id) $($metadata.version), licence $($metadata.license.'#text')."
    }
    foreach ($required in "THIRD-PARTY-NOTICES.md", "FFMPEG-SOURCE-NOTICES.md") {
        if ($null -eq $archive.GetEntry($required)) { throw "$packageName does not carry $required." }
    }
    $entryList = @($archive.Entries | ForEach-Object FullName | Sort-Object)
}
finally {
    $archive.Dispose()
}

# NuGet's contentHash for an unsigned package is the SHA-512 of the file; the lock files record it.
$sha512 = [System.Security.Cryptography.SHA512]::Create()
$bytes = [System.IO.File]::ReadAllBytes($packagePath)
$record = [ordered]@{
    package = $packageName
    id = $id
    version = $pins.PackageVersion
    sha256 = (Get-TemnoSha256 $packagePath)
    contentHash = [Convert]::ToBase64String($sha512.ComputeHash($bytes))
    bytes = $bytes.Length
    entries = $entryList
    ffmpeg = $manifest.Files
}
$record | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath "$packagePath.json" -Encoding utf8
Write-Host "Packed $packagePath ($([math]::Round($bytes.Length / 1MB, 1)) MB); its ffmpeg folder holds exactly the pinned files." -ForegroundColor Green
Write-Host "  contentHash $($record.contentHash)" -ForegroundColor DarkGray
[pscustomobject]$record
