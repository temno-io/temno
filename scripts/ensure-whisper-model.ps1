# Ensures the pinned ggml-base.en.bin is present for a Temno build or publish folder.
# Caches the download under tools/whisper/ (gitignored). Does not commit weights.
#
# The model is fetched from one fixed Hugging Face revision, never a moving branch, and every copy is checked against the
# pinned size and SHA-256 before it is used, as ensure-ffmpeg.ps1 checks FFmpeg (WP-402). The same pin is in
# scripts/build-msix.ps1 and in the app (WhisperModelStore.VerifyPinned, WP-401): Temno loads no other file.
#
# Usage:
#   .\scripts\ensure-whisper-model.ps1
#   .\scripts\ensure-whisper-model.ps1 -DestDir artifacts\app\TemnoPro\whisper

param(
    [string]$DestDir = ""
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$fileName = "ggml-base.en.bin"
# ggerganov/whisper.cpp at the commit that last changed this file (2023-03-22); its LFS object is the pinned hash.
$revision = "80da2d8bfee42b0e836fc3a9890373e5defc00a6"
$url = "https://huggingface.co/ggerganov/whisper.cpp/resolve/$revision/$fileName"
$pinnedBytes = 147964211
$pinnedSha256 = "A03779C86DF3323075F5E796CB2CE5029F00EC8869EEE3FDFB897AFE36C6D002"
$cacheDir = Join-Path $repoRoot "tools\whisper"
$cacheFile = Join-Path $cacheDir $fileName

if (-not $DestDir) {
    $DestDir = $cacheDir
}

# Null when the file is the pinned model; otherwise why it is not.
function Get-ModelProblem([string]$path) {
    if (-not (Test-Path -LiteralPath $path)) { return "it is missing" }
    $length = (Get-Item -LiteralPath $path).Length
    if ($length -ne $pinnedBytes) { return "it is $length bytes, not the pinned $pinnedBytes" }
    $actual = (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToUpperInvariant()
    if ($actual -ne $pinnedSha256) { return "its SHA-256 is $actual, not the pinned $pinnedSha256" }
    return $null
}

function Assert-PinnedModel([string]$path) {
    $problem = Get-ModelProblem $path
    if ($problem) {
        throw "Refusing the Whisper model at $path`: $problem. Delete the file and run this script again to fetch the pinned model."
    }
}

New-Item -ItemType Directory -Path $DestDir -Force | Out-Null
$destFile = Join-Path $DestDir $fileName

if (Test-Path -LiteralPath $destFile) {
    Assert-PinnedModel $destFile
    Write-Host "Whisper model already present and pinned: $destFile" -ForegroundColor DarkGray
    exit 0
}

if (Test-Path -LiteralPath $cacheFile) {
    Assert-PinnedModel $cacheFile
}
else {
    New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
    $tmp = "$cacheFile.tmp"
    Write-Host "Downloading the pinned Whisper English model (~142 MB)…" -ForegroundColor Cyan
    Write-Host "  $url"
    try {
        & curl.exe -L --fail --retry 3 --retry-delay 2 -o $tmp $url
        if ($LASTEXITCODE -ne 0) { throw "curl exited $LASTEXITCODE" }
    }
    catch {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Force }
        throw "Failed to download $fileName. Copy the pinned file to $cacheFile and run this script again. $_"
    }
    $problem = Get-ModelProblem $tmp
    if ($problem) {
        Remove-Item -LiteralPath $tmp -Force
        throw "Refusing the downloaded Whisper model: $problem. Nothing was kept."
    }
    Move-Item -Force -LiteralPath $tmp -Destination $cacheFile
}

$destFull = [IO.Path]::GetFullPath($destFile)
$cacheFull = [IO.Path]::GetFullPath($cacheFile)
if (-not $destFull.Equals($cacheFull, [StringComparison]::OrdinalIgnoreCase)) {
    Copy-Item -LiteralPath $cacheFile -Destination $destFile -Force
}

Assert-PinnedModel $destFile
Write-Host "Whisper model (pinned): $destFile" -ForegroundColor Green
