# Writes the notices for FFmpeg's own permissively licensed source files (WP-425). FFmpeg is GPL in Temno's builds,
# but some of its files carry BSD-, MIT-, ISC-, zlib- or IJG-style terms of their own whose notices must travel with
# the binaries. This reads the files a build compiled (compiled-sources.txt, from the compiler's dependency files,
# which list every source and header each object read), takes each one from the pinned source tarball, and copies
# every comment block in it that grants such terms, verbatim, grouped where the text is identical. A file that only
# names the licence of code it derives from must be resolved in source-notices.psd1, which supplies that licence's
# terms; any other such file stops the script. Deterministic: the same tarball, list and table give the same bytes.
#   .\scripts\ffmpeg\source-notices.ps1 -Tarball tools\ffmpeg\cache\ffmpeg-9.1.tar.xz `
#       -CompiledList tools\ffmpeg\build\9.1-temno1-linux-x64\compiled-sources.txt -OutFile <file>
# -Rid picks each build's own entries for the libraries it links (WP-427): the Windows build also links libvpl and
# compiles AMD's and NVIDIA's headers, and its dav1d adds Windows' thread code.
#   .\scripts\ffmpeg\source-notices.ps1 -Rid win-x64 -Tarball tools\ffmpeg\cache\ffmpeg-9.1.tar.xz `
#       -CompiledList tools\ffmpeg\build\9.1-temno1\compiled-sources.txt -OutFile <file>
# -SourceRoot and -Title default to the tarball's: ffmpeg-9.1 and "FFmpeg 9.1" for ffmpeg-9.1.tar.xz.
param(
    [Parameter(Mandatory)] [string]$Tarball,
    [Parameter(Mandatory)] [string]$CompiledList,
    [Parameter(Mandatory)] [string]$OutFile,
    [string]$SourceRoot = ([System.IO.Path]::GetFileName($Tarball) -replace '\.tar\.xz$', ''),
    [string]$Title = ("FFmpeg " + ([System.IO.Path]::GetFileName($Tarball) -replace '^ffmpeg-(.+)\.tar\.xz$', '$1')),
    [ValidateSet("linux-x64", "win-x64")]
    [string]$Rid = "linux-x64",
    [string]$Table = (Join-Path $PSScriptRoot "source-notices.psd1")
)
$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

# Phrases that open a grant whose notice must be reproduced. libjpeg's (IJG) terms open with "Permission is hereby
# granted". A block that only says "LGPL" or "GPL" is left out: the package's LICENSE and notices carry those terms.
$markers = @(
    "Redistribution and use in source and binary forms",          # BSD
    "Permission is hereby granted",                               # MIT, X11, Boost, IJG, HPND variants
    "Permission to use, copy, modify",                            # ISC, HPND
    "Permission is granted to anyone to use this software"        # zlib style
)
# A licence named in passing ("based on BSD licensed ..."), which needs its terms from the table.
$named = '(?i)\b(BSD|MIT|ISC|Apache|zlib|X11|Boost|WTFPL|CC0|MPL|Mozilla|expat|Unlicense|libpng|OpenSSL|Artistic|Creative Commons)\b[^\n]{0,60}\b(licen[cs]e|terms)|\b(licen[cs]ed|distributed|released|available) under\b|\bpublic[ -]domain\b'
$gplContext = '(?i)general public licen[cs]e|\bL?GPL\b'
# ffbuild/ holds tools the build runs on the build machine (bin2c); they are not part of the binaries.
$buildTools = '^ffbuild/'

# Dependency files name some headers through ".." (libswscale/x86/../swscale.h) and list the objects themselves.
function ConvertTo-SourcePath([string]$path) {
    $parts = [System.Collections.Generic.List[string]]::new()
    foreach ($part in ($path.Trim() -split '/')) {
        if ($part -eq '..' -and $parts.Count -gt 0) { $parts.RemoveAt($parts.Count - 1) }
        elseif ($part -ne '.' -and $part -ne '') { $parts.Add($part) }
    }
    return ($parts -join '/')
}
$listed = @([System.IO.File]::ReadAllLines((Resolve-Path -LiteralPath $CompiledList)) |
    ForEach-Object { ConvertTo-SourcePath $_ } |
    Where-Object { $_ -and $_ -notmatch '\.o$' -and $_ -notmatch $buildTools } | Sort-Object -Unique -CaseSensitive)
if ($listed.Count -lt 100) { throw "$CompiledList lists only $($listed.Count) files; it is not a build's list." }
$data = Import-PowerShellDataFile -LiteralPath $Table

$Tarball = (Resolve-Path -LiteralPath $Tarball).Path
# Windows' own bsdtar reads xz and Windows paths; Git's GNU tar, often first on PATH, takes "C:" for a remote host.
$tar = if ($IsWindows) { Join-Path $env:SystemRoot "System32\tar.exe" } else { "tar" }
$work = Join-Path ([System.IO.Path]::GetTempPath()) ("temno-source-notices-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $work | Out-Null
Push-Location -LiteralPath $work
try {
    # Only names the tarball holds: the list also names files configure and make generate (config.h and the like).
    $members = @(& $tar -tJf $Tarball | ForEach-Object { $_.Trim() })
    if ($LASTEXITCODE -ne 0) { throw "tar could not list $Tarball." }
    $memberSet = [System.Collections.Generic.HashSet[string]]::new([string[]]$members, [StringComparer]::Ordinal)
    $wanted = @($listed | Where-Object { $memberSet.Contains("$SourceRoot/$_") })
    $generated = @($listed | Where-Object { -not $memberSet.Contains("$SourceRoot/$_") })
    # LF, so no tar reads a CR into each name.
    [System.IO.File]::WriteAllText((Join-Path $work "names.txt"), ((@($wanted | ForEach-Object { "$SourceRoot/$_" }) -join "`n") + "`n"))
    & $tar -xJf $Tarball -T names.txt
    if ($LASTEXITCODE -ne 0) { throw "tar could not extract the listed files from $Tarball." }

    # Comment blocks: /* ... */, runs of // lines, and runs of ; or @ or # lines (assembly). Each is returned as plain
    # text with its comment markers taken off and its line breaks kept.
    function Get-CommentBlocks([string]$text) {
        $blocks = [System.Collections.Generic.List[string]]::new()
        foreach ($m in [regex]::Matches($text, '/\*(.*?)\*/', 'Singleline')) {
            $lines = $m.Groups[1].Value -split "`n" | ForEach-Object { ($_ -replace '^\s*\*+ ?', '').TrimEnd() }
            $blocks.Add(($lines -join "`n").Trim("`n"))
        }
        foreach ($m in [regex]::Matches($text, '(?m)(?:^[ \t]*(?://|;|@|#)[^\n]*\n?){2,}')) {
            $lines = $m.Value.TrimEnd("`n") -split "`n" | ForEach-Object { ($_ -replace '^\s*(?://+|;+\*?|@+|#+) ?', '').TrimEnd() }
            $blocks.Add(($lines -join "`n").Trim("`n"))
        }
        return $blocks
    }

    $resolved = @{}
    foreach ($entry in @($data.References) + @($data.NoNotice)) { foreach ($f in $entry.Files) { $resolved[$f] = $true } }
    $groups = [ordered]@{}
    $unresolved = [System.Collections.Generic.List[string]]::new()
    foreach ($name in $wanted) {
        $text = [System.IO.File]::ReadAllText((Join-Path $work "$SourceRoot/$name")) -replace "`r`n", "`n"
        $found = @(Get-CommentBlocks $text | Where-Object { $block = $_; @($markers | Where-Object { $block.Contains($_) }).Count -gt 0 } |
            Select-Object -Unique)
        foreach ($block in $found) {
            $key = [regex]::Replace($block, '\s+', ' ')
            if (-not $groups.Contains($key)) { $groups[$key] = [pscustomobject]@{ Text = $block; Files = [System.Collections.Generic.List[string]]::new() } }
            if (-not $groups[$key].Files.Contains($name)) { $groups[$key].Files.Add($name) }
        }
        if ($found.Count -eq 0 -and -not $resolved.ContainsKey($name)) {
            foreach ($m in [regex]::Matches($text, $named)) {
                $context = $text.Substring([Math]::Max(0, $m.Index - 80), [Math]::Min($text.Length - [Math]::Max(0, $m.Index - 80), $m.Length + 160))
                if ($context -match $gplContext -and $m.Value -notmatch '(?i)BSD|MIT|ISC|Apache|public') { continue }
                $unresolved.Add("$name ($($m.Value.Trim()))"); break
            }
        }
    }
    if ($unresolved.Count -gt 0) {
        throw "These compiled files name a licence they do not grant, and $([System.IO.Path]::GetFileName($Table)) does not resolve them: $($unresolved -join '; ')."
    }
    if ($groups.Count -eq 0) { throw "No permissively licensed file was found; the list or the markers are wrong." }
    $references = @($data.References | Where-Object { @($_.Files | Where-Object { $wanted -ccontains $_ }).Count -gt 0 })

    $granted = @($groups.Values | ForEach-Object { $_.Files } | Sort-Object -Unique)
    $referenced = @($references | ForEach-Object { $_.Files } | Where-Object { $wanted -ccontains $_ } | Sort-Object -Unique)
    $out = [System.Collections.Generic.List[string]]::new()
    $out.Add("# Notices for files inside $Title")
    $out.Add("")
    $out.Add("FFmpeg is licensed under the GPL in this build (see ``LICENSE``), but some of its source files carry")
    $out.Add("BSD-, MIT-, ISC-, zlib- or IJG-style terms of their own. This build compiled $($granted.Count) files that carry")
    $out.Add("such terms, and $($referenced.Count) more that name the terms of code they derive from. The first are")
    $out.Add("listed with each notice exactly as it appears in the $Title source, files with the same notice")
    $out.Add("together; the second follow, with those terms. Written by ``scripts/ffmpeg/source-notices.ps1`` from the")
    $out.Add("files the compiler's dependency files list: $($wanted.Count) from the source, and $($generated.Count) generated by")
    $out.Add("configure and make. Tools the build runs on the build machine (``ffbuild/``) are left out: they are not")
    $out.Add("part of the binaries.")
    foreach ($group in ($groups.Values | Sort-Object { $_.Files[0] } -CaseSensitive)) {
        $out.Add("")
        $out.Add("## " + (($group.Files | ForEach-Object { "``$_``" }) -join ", "))
        $out.Add("")
        $out.Add('```text')
        foreach ($line in ($group.Text -split "`n")) { $out.Add($line) }
        $out.Add('```')
    }
    foreach ($reference in $references) {
        $out.Add("")
        $out.Add("## " + ((@($reference.Files | Where-Object { $wanted -ccontains $_ }) | ForEach-Object { "``$_``" }) -join ", ") + ": code they derive from")
        $out.Add("")
        foreach ($line in ($reference.Attribution.Trim() -split "\r?\n")) { $out.Add($line) }
        $out.Add("")
        $out.Add('```text')
        foreach ($line in ($reference.Text.Trim() -split "\r?\n")) { $out.Add($line) }
        $out.Add('```')
    }
    # The libraries FFmpeg links statically come compiled in the build image, so their notices cannot be read from a
    # dependency list here: they were found in each library's source at its pinned commit, from the archive's members
    # and the files those compile and include, and are kept verbatim in the table.
    # An entry with Rids belongs to those builds only; an entry's Files, and LibrariesIntro, may differ by build.
    $libraries = @(if ($data.ContainsKey('Libraries')) {
        $data.Libraries | Where-Object { -not $_.ContainsKey('Rids') -or $_.Rids -contains $Rid } })
    if ($libraries.Count -gt 0) {
        $out.Add("")
        $out.Add("# Notices for files inside the libraries linked into $Title")
        $out.Add("")
        $intro = if ($data.LibrariesIntro -is [hashtable]) { $data.LibrariesIntro[$Rid] } else { $data.LibrariesIntro }
        foreach ($line in ($intro.Trim() -split "\r?\n")) { $out.Add($line) }
        foreach ($entry in $libraries) {
            $out.Add("")
            $entryFiles = if ($entry.Files -is [hashtable]) { $entry.Files[$Rid] } else { $entry.Files }
            $out.Add("## $($entry.Library): " + (($entryFiles | ForEach-Object { if ($_ -match '\s') { $_ } else { "``$_``" } }) -join ", "))
            $out.Add("")
            foreach ($line in ($entry.Attribution.Trim() -split "\r?\n")) { $out.Add($line) }
            if ($entry.Text) {
                $out.Add("")
                $out.Add('```text')
                # Not trimmed at the end: a licence file's own final empty line stays (the table keeps it on purpose).
                foreach ($line in ($entry.Text.TrimStart("`r", "`n") -split "\r?\n")) { $out.Add($line) }
                $out.Add('```')
            }
        }
    }
    $dir = Split-Path -Parent ([System.IO.Path]::GetFullPath($OutFile))
    if ($dir) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    [System.IO.File]::WriteAllText([System.IO.Path]::GetFullPath($OutFile), (($out -join "`n") + "`n"))
    [pscustomobject]@{ OutFile = [System.IO.Path]::GetFullPath($OutFile); Granted = $granted.Count; Notices = $groups.Count;
        Referenced = $referenced.Count; FromSource = $wanted.Count; Generated = $generated.Count }
}
finally {
    Pop-Location
    Remove-Item -LiteralPath $work -Recurse -Force -ErrorAction SilentlyContinue
}
