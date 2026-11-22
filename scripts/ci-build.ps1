# Builds Temno from a clean clone the way CI does: the solution in Release x64, the Core tests, and an
# unsigned MSIX. Every step writes its log under -OutputDirectory, and the script stops at the first
# failure. Needs Windows x64, Git, the .NET 10 SDK and PowerShell 7, and no Docker: restore fetches the pinned
# FFmpeg as the NuGet package Temno.FFmpeg.Runtime.win-x64 (WP-406), and the MSIX step fetches the pinned Whisper
# model.
[CmdletBinding()]
param(
    [string]$OutputDirectory = 'artifacts\ci',

    # The backend the build and the MSIX are for. Without it, the build takes this repository's default,
    # production (Temno.Public.props), the build temno.io ships; its public identifiers are in
    # src/Temno.Core/Environments/EnvironmentIdentity.cs.
    [ValidateSet('production', 'development')]
    [string]$Environment,

    [switch]$SkipTests,

    [switch]$SkipMsix
)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location -LiteralPath $repoRoot
$out = [System.IO.Path]::GetFullPath((Join-Path $repoRoot $OutputDirectory))
$logs = Join-Path $out 'logs'
New-Item -ItemType Directory -Path $logs -Force | Out-Null
$chosen = $PSBoundParameters.ContainsKey('Environment')
if (-not $chosen) { $Environment = 'production' }
$summary = [ordered]@{ commit = (git rev-parse HEAD); environment = $Environment; environmentGiven = $chosen; steps = @() }

function Invoke-Step {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][scriptblock]$Body
    )

    $log = Join-Path $logs "$Name.log"
    $watch = [System.Diagnostics.Stopwatch]::StartNew()
    Write-Host "==> $Name" -ForegroundColor Cyan
    $global:LASTEXITCODE = 0
    try {
        & $Body *>&1 | Tee-Object -FilePath $log | Out-Host
        $code = $global:LASTEXITCODE
    }
    catch {
        $_ | Out-String | Add-Content -LiteralPath $log
        $code = -1
    }
    $watch.Stop()
    $script:summary.steps += [ordered]@{ name = $Name; exitCode = $code; minutes = [math]::Round($watch.Elapsed.TotalMinutes, 1); log = "logs/$Name.log" }
    $script:summary | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $out 'summary.json')
    if ($code -ne 0) {
        throw "$Name failed (exit $code). See $log."
    }
}

Invoke-Step 'environment' {
    dotnet --info
    $PSVersionTable | Out-String
    git log -1 --format='%H %s'
}

# Restores keep to the lock files: a package whose bytes or dependencies differ from them stops the build.
Invoke-Step 'build' {
    if ($chosen) {
        dotnet build TemnoWindows.sln -c Release -p:Platform=x64 -p:TemnoEnvironment=$Environment -p:RestoreLockedMode=true -nologo
    }
    else {
        dotnet build TemnoWindows.sln -c Release -p:Platform=x64 -p:RestoreLockedMode=true -nologo
    }
}

# The app's own copy of Temno.Core says which backend the build belongs to; the test step rebuilds Temno.Core's
# bin as a development build, so it is not read there.
Invoke-Step 'environment-check' {
    . (Join-Path $repoRoot 'scripts\lib\temno-environment.ps1')
    $core = Get-ChildItem -LiteralPath (Join-Path $repoRoot 'src\Temno.App\bin\x64\Release') -Recurse -Filter 'Temno.Core.dll' | Select-Object -First 1
    if ($null -eq $core) { throw 'The build left no Temno.Core.dll beside Temno.App.' }
    $bytes = [System.IO.File]::ReadAllBytes($core.FullName)
    $built = (Get-TemnoAssemblyMetadataValues -Bytes $bytes -Key 'TemnoEnvironment' -Source $core.FullName) -join ','
    $official = (Get-TemnoAssemblyMetadataValues -Bytes $bytes -Key 'TemnoOfficialBuild' -Source $core.FullName) -join ','
    Write-Output "Temno.App's Temno.Core: TemnoEnvironment=$built TemnoOfficialBuild=$official ($($core.FullName))"
    if ($built -ne $Environment) { throw "The app was built for '$built', not '$Environment'." }
}

if (-not $SkipTests) {
    $results = Join-Path $out 'test-results'
    # The tests run as a development build, so they never touch an installed Temno's data.
    # --blame-hang-timeout bounds any stalled test at 2 minutes and logs the active test name instead of hanging CI.
    Invoke-Step 'core-tests' {
        dotnet test tests/Temno.Core.Tests -c Release -p:TemnoEnvironment=development -p:RestoreLockedMode=true --nologo `
            --settings tests/Temno.Core.Tests/serial.runsettings `
            --blame-hang-timeout 2m --blame-hang-dump-type none `
            --logger 'trx;LogFileName=core.trx' --results-directory $results
    }
}

if (-not $SkipMsix) {
    # build-msix.ps1 names the backend from a deploy profile. Production's is written from the
    # public identity in source; development's from the example, whose hosts do not resolve.
    $profileRoot = Join-Path $out 'deploy-profiles'
    New-Item -ItemType Directory -Path $profileRoot -Force | Out-Null
    if ($Environment -eq 'production') {
        . (Join-Path $repoRoot 'scripts\lib\temno-environment.ps1')
        $targets = Read-TemnoProductionTargets
        [ordered]@{
            environment = 'production'
            gatewayHost = $targets.GatewayHost
            accountsHost = $targets.AccountsHost
            supabaseProjectRef = $targets.SupabaseProjectRef
            note = 'Written by scripts/ci-build.ps1 from EnvironmentIdentity.Production.'
        } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $profileRoot 'production.json')
    }
    else {
        Copy-Item -LiteralPath (Join-Path $repoRoot 'scripts\deploy-profile.example.json') -Destination (Join-Path $profileRoot 'development.json')
    }

    # build-msix.ps1 cleans only under artifacts\, so a folder elsewhere must start empty.
    $msixOut = Join-Path $out 'msix'
    $clean = $msixOut.StartsWith((Join-Path $repoRoot 'artifacts') + [System.IO.Path]::DirectorySeparatorChar, [System.StringComparison]::OrdinalIgnoreCase)
    Invoke-Step 'msix' {
        & (Join-Path $repoRoot 'scripts\build-msix.ps1') -Environment $Environment -ProfileRoot $profileRoot `
            -Configuration Release -Architecture x64 -OutputDirectory $msixOut -Clean:$clean
    }
}

Write-Host "Done. Summary: $(Join-Path $out 'summary.json')" -ForegroundColor Green
