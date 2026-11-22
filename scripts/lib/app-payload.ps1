# What an app package must carry, checked on the finished archive (WP-264, WP-401, WP-427; shared since WP-451).
# An MSIX and Temno-win-x64.zip are both ZIP archives with the app at their root, so build-msix.ps1 and
# build-zip.ps1 run the same checks: the pinned Whisper model, the pinned FFmpeg and nothing beside it, the notices,
# and the Visual C++ runtime for every import. Dot-source it after lib\msix-common.ps1 and
# lib\temno-environment.ps1; it is strict-mode clean, as they are.

# The same pin is in scripts/ensure-whisper-model.ps1 and in the app (WhisperModelStore, WP-401); the Core tests
# read these two lines and compare.
$whisperModelBytes = 147964211
$whisperModelSha256 = 'A03779C86DF3323075F5E796CB2CE5029F00EC8869EEE3FDFB897AFE36C6D002'
$whisperPackageEntry = 'whisper/ggml-base.en.bin'

function Assert-TemnoWhisperModelFile {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $model = Get-Item -LiteralPath $Path -ErrorAction Stop
    if ($model.Length -ne $whisperModelBytes) {
        throw "Whisper model '$Path' is $($model.Length) bytes; expected $whisperModelBytes bytes."
    }

    $actualSha256 = (Get-FileHash -LiteralPath $model.FullName -Algorithm SHA256).Hash
    if ($actualSha256 -ne $whisperModelSha256) {
        throw "Whisper model '$Path' has SHA-256 $actualSha256; expected $whisperModelSha256. Delete the cached file and rerun the build to fetch the pinned payload."
    }

    return [pscustomobject]@{
        Path = $model.FullName
        Bytes = $model.Length
        Sha256 = $actualSha256
    }
}

function Assert-TemnoWhisperPackageEntry {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $entry = $archive.Entries |
            Where-Object { $_.FullName.Replace('\', '/') -ieq $whisperPackageEntry } |
            Select-Object -First 1
        if ($null -eq $entry) {
            throw "Package '$PackagePath' does not contain $whisperPackageEntry."
        }
        if ($entry.Length -ne $whisperModelBytes) {
            throw "Packaged Whisper model is $($entry.Length) bytes; expected $whisperModelBytes bytes."
        }

        $stream = $entry.Open()
        $sha256 = [System.Security.Cryptography.SHA256]::Create()
        try {
            $actualSha256 = [BitConverter]::ToString($sha256.ComputeHash($stream)).Replace('-', '')
        }
        finally {
            $sha256.Dispose()
            $stream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }

    if ($actualSha256 -ne $whisperModelSha256) {
        throw "Packaged Whisper model has SHA-256 $actualSha256; expected $whisperModelSha256."
    }

    return [pscustomobject]@{
        Entry = $whisperPackageEntry
        Bytes = $entry.Length
        Sha256 = $actualSha256
    }
}

function Get-TemnoMsixEntry {
    param(
        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchive]$Archive,

        [Parameter(Mandatory)]
        [string]$EntryName
    )

    $normalized = $EntryName.Replace('\', '/')
    return $Archive.Entries |
        Where-Object { $_.FullName.Replace('\', '/') -ieq $normalized } |
        Select-Object -First 1
}

function Get-TemnoMsixEntrySha256 {
    param(
        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchiveEntry]$Entry
    )

    $stream = $Entry.Open()
    $sha256 = [System.Security.Cryptography.SHA256]::Create()
    try {
        return [BitConverter]::ToString($sha256.ComputeHash($stream)).Replace('-', '')
    }
    finally {
        $sha256.Dispose()
        $stream.Dispose()
    }
}

function Assert-TemnoFfmpegPackage {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath,

        [Parameter(Mandatory)]
        $Pin
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        foreach ($file in $Pin.Files) {
            $entry = Get-TemnoMsixEntry -Archive $archive -EntryName $file.Entry
            if ($null -eq $entry) {
                throw "Package '$PackagePath' does not contain $($file.Entry)."
            }

            $actual = Get-TemnoMsixEntrySha256 -Entry $entry
            if ($actual -ne $file.Sha256) {
                throw "Packaged $($file.Entry) has SHA-256 $actual; expected $($file.Sha256)."
            }
        }

        # Windows loads a DLL from ffmpeg.exe's own folder before System32 for most imports, so an
        # extra file there runs inside FFmpeg (WP-401). The folder holds the pinned files and nothing else.
        $pinned = @($Pin.Files | ForEach-Object { $_.Entry.ToLowerInvariant() })
        $extra = @($archive.Entries | ForEach-Object { [System.Uri]::UnescapeDataString($_.FullName).Replace('\', '/') } |
            Where-Object { $_ -like 'ffmpeg/*' -and $_.ToLowerInvariant() -notin $pinned })
        if ($extra.Count -gt 0) {
            throw "Package '$PackagePath' carries unpinned files in ffmpeg/: $($extra -join ', ')."
        }

        $configure = Get-TemnoMsixEntry -Archive $archive -EntryName 'ffmpeg/configure.txt'
        $configureText = Read-TemnoMsixEntryText -Entry $configure
        foreach ($flag in @('--enable-gpl', '--enable-libx264', '--enable-libx265', '--disable-network', '--disable-protocols')) {
            if ($configureText -notlike "*$flag*") {
                throw "Packaged ffmpeg/configure.txt is missing $flag."
            }
        }
        if ($configureText -like '*--enable-nonfree*') {
            throw 'Packaged ffmpeg/configure.txt is a nonfree build.'
        }

        $source = Get-TemnoMsixEntry -Archive $archive -EntryName 'ffmpeg/source.txt'
        $sourceText = Read-TemnoMsixEntryText -Entry $source
        if ($sourceText -notlike "*$($Pin.SourceCommit)*") {
            throw "Packaged ffmpeg/source.txt does not name commit $($Pin.SourceCommit)."
        }
    }
    finally {
        $archive.Dispose()
    }
}

function Read-TemnoMsixEntryText {
    param(
        [Parameter(Mandatory)]
        [System.IO.Compression.ZipArchiveEntry]$Entry
    )

    $stream = $Entry.Open()
    try {
        $reader = New-Object System.IO.StreamReader($stream, [System.Text.Encoding]::UTF8, $true, 1024, $true)
        try { return $reader.ReadToEnd() }
        finally { $reader.Dispose() }
    }
    finally {
        $stream.Dispose()
    }
}

function Test-TemnoVcRuntimeDllName {
    param([Parameter(Mandatory)][string]$Name)
    $file = [System.IO.Path]::GetFileName($Name)
    return $file -match '^(?i)(vcruntime140(_\d+)?|msvcp140(_\d+|_atomic_wait|_codecvt_ids)?|vcomp140|concrt140|vccorlib140|msvcr1\d\d)\.dll$'
}

function Get-TemnoPeImportedDlls {
    param(
        [Parameter(Mandatory)][System.IO.Stream]$Stream,
        [Parameter(Mandatory)][long]$Length
    )
    if (-not ('Temno.Packaging.PeImports' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
namespace Temno.Packaging {
  public static class PeImports {
    public static string[] Read(Stream stream, long length) {
      if (length < 64) return new string[0];
      var cap = (int)Math.Min(length, 32L * 1024 * 1024);
      var cache = new byte[cap];
      var filled = 0;
      byte[] Take(int offset, int count) {
        var need = offset + count;
        if (need > cache.Length) throw new InvalidDataException("Import data is past the scanned prefix.");
        while (filled < need) {
          var read = stream.Read(cache, filled, need - filled);
          if (read <= 0) throw new EndOfStreamException();
          filled += read;
        }
        var slice = new byte[count];
        Buffer.BlockCopy(cache, offset, slice, 0, count);
        return slice;
      }
      int U16(byte[] b, int o) { return b[o] | (b[o + 1] << 8); }
      uint U32(byte[] b, int o) { return (uint)(b[o] | (b[o + 1] << 8) | (b[o + 2] << 16) | (b[o + 3] << 24)); }
      var dos = Take(0, 64);
      if (dos[0] != 0x4D || dos[1] != 0x5A) return new string[0];
      var lfanew = (int)U32(dos, 0x3C);
      if (lfanew <= 0 || lfanew + 24 > length) return new string[0];
      var pe = Take(lfanew, 24);
      if (pe[0] != 0x50 || pe[1] != 0x45) return new string[0];
      var sectionCount = U16(pe, 6);
      var optionalSize = U16(pe, 20);
      var optional = Take(lfanew + 24, optionalSize);
      var magic = U16(optional, 0);
      var dirOffset = magic == 0x20B ? 112 : 96;
      if (optional.Length < dirOffset + 16) return new string[0];
      var importRva = U32(optional, dirOffset + 8);
      var importSize = U32(optional, dirOffset + 12);
      if (importRva == 0) return new string[0];
      var sectionStart = lfanew + 24 + optionalSize;
      var va = new uint[sectionCount];
      var vsz = new uint[sectionCount];
      var raw = new uint[sectionCount];
      var rawSize = new uint[sectionCount];
      for (var i = 0; i < sectionCount; i++) {
        var row = Take(sectionStart + (i * 40), 40);
        vsz[i] = U32(row, 8);
        va[i] = U32(row, 12);
        rawSize[i] = U32(row, 16);
        raw[i] = U32(row, 20);
      }
      int Rva(uint rva) {
        for (var i = 0; i < sectionCount; i++) {
          var span = Math.Max(vsz[i], rawSize[i]);
          if (rva >= va[i] && rva < va[i] + span) return (int)(raw[i] + (rva - va[i]));
        }
        return -1;
      }
      var count = importSize >= 20 ? Math.Min((int)(importSize / 20), 256) : 64;
      var names = new List<string>();
      for (var i = 0; i < count; i++) {
        var desc = Rva(importRva + (uint)(i * 20));
        if (desc < 0 || desc + 20 > cap) break;
        var row = Take(desc, 20);
        var nameRva = U32(row, 12);
        if (nameRva == 0 && U32(row, 0) == 0 && U32(row, 16) == 0) break;
        var name = Rva(nameRva);
        if (name < 0 || name >= cap) continue;
        var bytes = new List<byte>();
        for (var n = 0; n < 260 && name + n < cap; n++) {
          var one = Take(name + n, 1)[0];
          if (one == 0) break;
          bytes.Add(one);
        }
        if (bytes.Count > 0) names.Add(Encoding.ASCII.GetString(bytes.ToArray()));
      }
      return names.ToArray();
    }
  }
}
'@
    }
    return @([Temno.Packaging.PeImports]::Read($Stream, $Length))
}

function Assert-TemnoVcRuntimePackaged {
    param([Parameter(Mandatory)][string]$PackagePath)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $present = New-Object 'System.Collections.Generic.HashSet[string]' ([StringComparer]::OrdinalIgnoreCase)
        foreach ($entry in $archive.Entries) {
            $file = [System.IO.Path]::GetFileName($entry.FullName)
            if (Test-TemnoVcRuntimeDllName $file) { [void]$present.Add($file) }
        }
        foreach ($name in @($script:TemnoVcRuntimeSha256.Keys)) {
            $pinned = Get-TemnoMsixEntry -Archive $archive -EntryName $name
            if ($null -eq $pinned) {
                throw "Package '$PackagePath' does not contain the pinned VC++ runtime $name."
            }
            $actualHash = Get-TemnoMsixEntrySha256 -Entry $pinned
            if ($actualHash -ne $script:TemnoVcRuntimeSha256[$name]) {
                throw "Packaged $name SHA-256 is $actualHash; the pin is $($script:TemnoVcRuntimeSha256[$name])."
            }
        }

        $missing = New-Object System.Collections.Generic.List[string]
        foreach ($entry in @($archive.Entries)) {
            $file = [System.IO.Path]::GetFileName($entry.FullName)
            if ($file -notmatch '\.(dll|exe)$') { continue }
            $stream = $entry.Open()
            try {
                $imports = @(Get-TemnoPeImportedDlls -Stream $stream -Length $entry.Length)
            }
            catch {
                throw "Could not read imports of packaged '$($entry.FullName)': $($_.Exception.Message)"
            }
            finally { $stream.Dispose() }
            foreach ($import in $imports) {
                if ((Test-TemnoVcRuntimeDllName $import) -and -not $present.Contains([System.IO.Path]::GetFileName($import))) {
                    $missing.Add("$($entry.FullName) imports $import")
                }
            }
        }
        if ($missing.Count -gt 0) {
            throw "Package '$PackagePath' imports Visual C++ runtime DLLs it does not contain:`n$($missing -join "`n")"
        }
    }
    finally { $archive.Dispose() }
}

# ONNX Runtime crashes with msvcp140 older than 14.40. This package pins the
# 14.51.36247.0 files from the build machine and refuses any other version.
$script:TemnoVcRuntimeVersion = '14.51.36247.0'
$script:TemnoVcRuntimeFloor = [version]'14.40.0'
$script:TemnoVcRuntimeSha256 = @{
    'vcruntime140.dll' = 'D1F4225DF2CD877DBF130D5668A021DCE3F94118455FF5EC952061C30AFC9CE7'
    'vcruntime140_1.dll' = 'A7146C08F89FE5B04541AB507CDB59FF7B44534D4BA3C668A426C6450A03434E'
    'msvcp140.dll' = '7C26614E1D733892C2DEAC7E245CE115504B1D80592DD0A01B08E3E5A55F89CA'
    'msvcp140_1.dll' = '206C931BF90FDAD8816DE3B5E2EF80B2BCAA9406C89ECC05FE6FDDFFE251E982'
    'vcomp140.dll' = '95D4CE4A6802D1E18B5E0E1722CC30EA72CA7E033F83828F05C0B7B993FE7CBF'
}

function Copy-TemnoVcRuntime {
    param([Parameter(Mandatory)][string]$Destination)
    New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    foreach ($name in $script:TemnoVcRuntimeSha256.Keys) {
        $source = Join-Path $env:SystemRoot "System32\$name"
        if (-not (Test-Path -LiteralPath $source)) {
            throw "VC++ runtime '$source' is not on this machine, so the package cannot bundle it."
        }
        $info = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($source)
        $parsed = $null
        if (-not [version]::TryParse($info.FileVersion, [ref]$parsed) -or $parsed -lt $script:TemnoVcRuntimeFloor) {
            throw "VC++ runtime $name is '$($info.FileVersion)'. ONNX Runtime crashes with msvcp140 below 14.40."
        }
        if ($info.FileVersion -ne $script:TemnoVcRuntimeVersion) {
            throw "VC++ runtime $name is $($info.FileVersion); this package pins $($script:TemnoVcRuntimeVersion)."
        }
        $dest = Join-Path $Destination $name
        Copy-Item -LiteralPath $source -Destination $dest -Force
        $hash = (Get-FileHash -LiteralPath $dest -Algorithm SHA256).Hash
        if ($hash -ne $script:TemnoVcRuntimeSha256[$name]) {
            throw "VC++ runtime $name SHA-256 is $hash; the pin is $($script:TemnoVcRuntimeSha256[$name])."
        }
    }
}

function Assert-TemnoPackageRootNotices {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath,

        [Parameter(Mandatory)]
        [string]$RepoRoot,

        # WP-427: the FFmpeg package's FFMPEG-SOURCE-NOTICES.md, which its build file places beside the app.
        [Parameter(Mandatory)]
        [string]$FfmpegNoticesPath
    )

    $expectedFiles = [ordered]@{}
    foreach ($name in 'THIRD-PARTY-NOTICES.md', 'LICENSE', 'NOTICE') { $expectedFiles[$name] = Join-Path $RepoRoot $name }
    $expectedFiles['FFMPEG-SOURCE-NOTICES.md'] = $FfmpegNoticesPath
    $names = @($expectedFiles.Keys)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        foreach ($name in $names) {
            $entry = Get-TemnoMsixEntry -Archive $archive -EntryName $name
            if ($null -eq $entry) {
                throw "Package '$PackagePath' does not contain $name."
            }

            $expected = (Get-FileHash -LiteralPath $expectedFiles[$name] -Algorithm SHA256).Hash
            $actual = Get-TemnoMsixEntrySha256 -Entry $entry
            if ($actual -ne $expected) {
                throw "Packaged $name has SHA-256 $actual; expected $($expectedFiles[$name])'s $expected."
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

# The restored FFmpeg package's FFMPEG-SOURCE-NOTICES.md, which the package root must carry unchanged (WP-427).
function Get-TemnoFfmpegNoticesPath {
    param(
        # What ensure-ffmpeg.ps1 returns.
        [Parameter(Mandatory)]
        $Pin
    )

    $globalPackages = @(& dotnet nuget locals global-packages --list) | Where-Object { $_ -match '^global-packages:' } | Select-Object -First 1
    if (-not $globalPackages) { throw 'dotnet nuget locals did not name the global packages folder.' }
    $notices = [System.IO.Path]::Combine(($globalPackages -replace '^global-packages:\s*', '').Trim(),
        $Pin.PackageId.ToLowerInvariant(), $Pin.PackageVersion.ToLowerInvariant(), 'FFMPEG-SOURCE-NOTICES.md')
    if (-not (Test-Path -LiteralPath $notices)) {
        throw "$($Pin.PackageId) $($Pin.PackageVersion) is not restored with FFMPEG-SOURCE-NOTICES.md ($notices)."
    }

    return $notices
}

# What the app in a ZIP was built as, read from the Temno.Core.dll at its root: its environment, its official-build
# stamp and the version it compares the update feed with. An MSIX's environment goes through
# Assert-TemnoArtifactEnvironment, which reads the same entry; a .zip there is a service package with a stamp file.
function Get-TemnoAppZipStamp {
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        # Every entry an extractor could write to the root's Temno.Core.dll counts, however it is spelled.
        $entries = @($archive.Entries | Where-Object { (Get-TemnoZipEntryExtractedName -FullName $_.FullName) -ieq 'Temno.Core.dll' })
        if ($entries.Count -ne 1 -or $entries[0].FullName -cne 'Temno.Core.dll') {
            $found = @($entries | ForEach-Object { "'$($_.FullName)'" }) -join ', '
            throw "Package '$PackagePath' must carry exactly one Temno.Core.dll, at its root (found: $(if ($found) { $found } else { 'none' }))."
        }

        $bytes = Read-TemnoZipEntryBytes -Entry $entries[0]
    }
    finally {
        $archive.Dispose()
    }

    $source = "$PackagePath!Temno.Core.dll"
    $environment = Read-TemnoAssemblyEnvironment -Bytes $bytes -Source $source
    if ($null -ne $environment.Problem) { throw $environment.Problem }
    $official = Get-TemnoAssemblyMetadataValues -Bytes $bytes -Key 'TemnoOfficialBuild' -Source $source
    $version = Get-TemnoAssemblyMetadataValues -Bytes $bytes -Key 'TemnoAppVersion' -Source $source
    return [pscustomobject]@{
        Environment = $environment.Environment
        # Official only for the value true, as BuildProvenance reads it.
        Official = ($official.Count -eq 1 -and "$($official[0])".Trim() -ieq 'true')
        Version = $(if ($version.Count -eq 1) { "$($version[0])".Trim() } else { '' })
    }
}
