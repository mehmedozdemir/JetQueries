<#
.SYNOPSIS
    Publishes JetQueries and builds a Velopack release (installer, full package, release feed) from it.

.DESCRIPTION
    1. dotnet publish: self-contained, ReadyToRun, for one runtime.
    2. vpk pack: creates <output>/JetQueries-<channel>-Setup.exe, the .nupkg and releases.<channel>.json.

    The version comes from the git tag through MinVer (v1.2.3 -> 1.2.3; untagged commits get a pre-release
    version). The package is not signed unless -SignParams is given; unsigned installers trigger SmartScreen.
    Run from any directory; needs the .NET SDK from global.json. vpk is restored from dotnet-tools.json.

.EXAMPLE
    scripts/pack.ps1
    scripts/pack.ps1 -Channel beta
    scripts/pack.ps1 -SignParams '/a /fd SHA256 /tr http://timestamp.digicert.com /td SHA256'
#>
[CmdletBinding()]
param(
    [ValidateSet('stable', 'beta', 'nightly')]
    [string] $Channel = 'stable',

    [ValidateSet('win-x64')]
    [string] $Runtime = 'win-x64',

    # Overrides the MinVer version, for trying the update flow locally.
    [string] $Version,

    # Passed to signtool.exe by vpk. Never put a certificate password here; use the certificate store.
    [string] $SignParams
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$root = Split-Path -Parent $PSScriptRoot
# The WPF application is the product (ADR 0007); the Avalonia one is paused and not packaged.
$project = Join-Path $root 'src/JetQueries.Wpf/JetQueries.Wpf.csproj'
$publishDir = Join-Path $root "artifacts/publish/$Runtime"
$releaseDir = Join-Path $root "artifacts/releases/$Runtime"

function Invoke-Checked {
    param([string] $What, [scriptblock] $Command)
    & $Command
    if ($LASTEXITCODE -ne 0) { throw "$What failed with exit code $LASTEXITCODE." }
}

Push-Location $root
try {
    Invoke-Checked 'dotnet tool restore' { dotnet tool restore }

    if (-not $Version) {
        $Version = (dotnet msbuild $project -nologo -t:MinVer -getProperty:MinVerVersion).Trim()
        if ($LASTEXITCODE -ne 0 -or -not $Version) { throw 'Could not read the version from MinVer.' }
    }
    Write-Host "Packing JetQueries $Version for $Runtime, channel $Channel"

    if (Test-Path $publishDir) { Remove-Item -Recurse -Force $publishDir }
    Invoke-Checked 'dotnet publish' {
        dotnet publish $project -c Release -r $Runtime --self-contained true `
            -p:PublishReadyToRun=true -p:MinVerVersionOverride=$Version -o $publishDir
    }

    # Velopack channels are per operating system: win-stable, win-beta, win-nightly.
    $os = $Runtime.Split('-')[0]
    $vpkArgs = @(
        'pack',
        '--packId', 'JetQueries',
        '--packTitle', 'JetQueries',
        '--packAuthors', 'JetQueries contributors',
        '--packVersion', $Version,
        '--packDir', $publishDir,
        '--mainExe', 'JetQueries.exe',
        '--icon', (Join-Path $root 'src/JetQueries.Wpf/Assets/JetQueries.ico'),
        '--runtime', $Runtime,
        '--channel', "$os-$Channel",
        '--outputDir', $releaseDir
    )
    if ($SignParams) { $vpkArgs += @('--signParams', $SignParams) }
    Invoke-Checked 'vpk pack' { dotnet vpk @vpkArgs }

    Write-Host "Done: $releaseDir"
    Get-ChildItem $releaseDir | Select-Object Name, @{ n = 'MB'; e = { [math]::Round($_.Length / 1MB, 1) } } | Format-Table -AutoSize
}
finally {
    Pop-Location
}
