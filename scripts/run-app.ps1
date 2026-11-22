# Build and launch Temno (WinUI app) from the local build output.
# FFmpeg comes with the build: restore fetches the NuGet package Temno.FFmpeg.Runtime.win-x64 and places it
# beside the app (WP-406). This script also fetches the pinned Whisper model first, so transcription works
# offline; CI and -SkipWhisperModel leave it to the app, which fetches it at first use.
# Usage (from repo root or anywhere):
#   .\scripts\run-app.ps1
#   .\scripts\run-app.ps1 -NoBuild
#   .\scripts\run-app.ps1 -Configuration Release
#   .\scripts\run-app.ps1 -SkipWhisperModel
#
# For a self-contained folder + Desktop shortcut (no MSIX / no AI subscription):
#   .\scripts\publish-app.ps1

param(
    [switch]$NoBuild,
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Debug",
    [switch]$SkipWhisperModel
)

$ErrorActionPreference = "Stop"
$repoRoot = Split-Path -Parent $PSScriptRoot
$project = Join-Path $repoRoot "src\Temno.App\Temno.App.csproj"

if (-not (Test-Path $project)) {
    throw "Could not find Temno.App project at $project"
}

# Add the machine and user PATH too, for a terminal opened before a winget install.
# The caller's PATH stays first, so an SDK put there (a repo-local .NET 10) wins over the machine's.
$env:Path = $env:Path + ";" + [System.Environment]::GetEnvironmentVariable("Path", "Machine") + ";" +
            [System.Environment]::GetEnvironmentVariable("Path", "User")

if (-not (Get-Command dotnet -ErrorAction SilentlyContinue)) {
    throw ".NET SDK not found. Install: winget install Microsoft.DotNet.SDK.10"
}

# Before the build, which bundles the model only when it is already there.
if (-not $SkipWhisperModel -and $env:CI -ne "true") {
    & (Join-Path $PSScriptRoot "ensure-whisper-model.ps1")
}

if (-not $NoBuild) {
    Write-Host "Building Temno.App ($Configuration | x64)..." -ForegroundColor Cyan
    dotnet build $project -c $Configuration -p:Platform=x64
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}

$exe = Join-Path $repoRoot "src\Temno.App\bin\x64\$Configuration\net10.0-windows10.0.26100.0\win-x64\Temno.App.exe"
if (-not (Test-Path $exe)) {
    # Fallback: search under bin in case TFM folder changes
    $found = Get-ChildItem -Path (Join-Path $repoRoot "src\Temno.App\bin\x64\$Configuration") -Recurse -Filter "Temno.App.exe" -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
    if ($found) { $exe = $found }
}

if (-not (Test-Path $exe)) {
    throw "Temno.App.exe not found. Build first (omit -NoBuild)."
}

Write-Host "Launching:" -ForegroundColor Green
Write-Host "  $exe"
Start-Process -FilePath $exe -WorkingDirectory (Split-Path $exe)
