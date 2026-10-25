<#
.SYNOPSIS
    Build, test and package DropWheel on Windows.

.DESCRIPTION
    0. scripts/fetch-sidecars.ps1 -Arch <x64|arm64>   (pinned ffmpeg LGPL build -> tools/sidecars)
    1. dotnet build DropWheel.slnx -c Release -p:Platform=<x64|ARM64>
    2. dotnet test  tests/DropWheel.Core.Tests and tests/DropWheel.Engines.Tests
    3. winapp pack  src/DropWheel.App  -> ./artifacts/DropWheel_<version>_<arch>.msix   (unsigned)

    The package is deliberately unsigned (Store submission / external signing). To sideload a dev
    build sign it with the certificate from setup-windows.ps1:
        winapp sign .\artifacts\<file>.msix .\devcert.pfx
        winapp cert install .\devcert.pfx      # once, as Administrator
        Add-AppxPackage .\artifacts\<file>.msix

    Usage:
        pwsh .\scripts\build-windows.ps1                 # x64 (default)
        pwsh .\scripts\build-windows.ps1 -Arch arm64
        pwsh .\scripts\build-windows.ps1 -SkipTests -SkipPack
#>
[CmdletBinding()]
param(
    [ValidateSet('x64', 'arm64')] [string]$Arch = 'x64',
    [string]$Configuration = 'Release',
    [switch]$SkipTests,
    [switch]$SkipPack
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
$env:DOTNET_NOLOGO = '1'
$env:WINAPP_CLI_TELEMETRY_OPTOUT = '1'

$RepoRoot = (Resolve-Path (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..')).Path
Set-Location $RepoRoot
$Platform = if ($Arch -eq 'arm64') { 'ARM64' } else { 'x64' }
$Artifacts = Join-Path $RepoRoot 'artifacts'
New-Item -ItemType Directory -Force -Path $Artifacts | Out-Null

# Pick up tools installed by setup-windows.ps1 even in a shell started before it ran.
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User') + ';' + $env:Path
foreach ($t in 'dotnet', 'winapp') {
    if (-not (Get-Command "$t.exe" -ErrorAction SilentlyContinue)) {
        throw "$t not found on PATH. Run .\scripts\setup-windows.ps1 first."
    }
}

function Invoke-Checked([string]$title, [scriptblock]$cmd) {
    Write-Host "`n==> $title" -ForegroundColor Cyan
    $sw = [Diagnostics.Stopwatch]::StartNew()
    & $cmd
    if ($LASTEXITCODE -ne 0) { throw "$title failed (exit $LASTEXITCODE)" }
    Write-Host "    done in $([int]$sw.Elapsed.TotalSeconds)s" -ForegroundColor Green
}

$winappVersion = '?'
if (((winapp --version 2>&1) | Out-String) -match '(\d+\.\d+[\d.]*)') { $winappVersion = $Matches[1] }
Write-Host "DropWheel build  ·  $Configuration | $Platform  ·  dotnet $(dotnet --version)  ·  winapp $winappVersion"

# The app copies tools/sidecars/ffmpeg/<arch> next to itself, and the Engines tests run that ffmpeg from the PATH.
Write-Host "`n==> fetch the ffmpeg sidecar ($Arch)" -ForegroundColor Cyan
& (Join-Path $RepoRoot 'scripts\fetch-sidecars.ps1') -Arch $Arch
$env:Path = (Join-Path $RepoRoot "tools\sidecars\ffmpeg\$Arch") + ';' + $env:Path

Invoke-Checked "dotnet build DropWheel.slnx -c $Configuration -p:Platform=$Platform" {
    dotnet build DropWheel.slnx -c $Configuration -p:Platform=$Platform
}

if ($SkipTests) {
    Write-Host "`n==> tests skipped (-SkipTests)"
} else {
    # The test project is AnyCPU; test it directly instead of through the solution platform.
    Invoke-Checked 'dotnet test tests/DropWheel.Core.Tests' {
        dotnet test tests/DropWheel.Core.Tests/DropWheel.Core.Tests.csproj -c $Configuration `
            --logger "trx;LogFileName=core.trx" --results-directory (Join-Path $Artifacts 'test-results')
    }
    # Engines tests are x64 | ARM64 like the engines, so they reuse the solution build above.
    Invoke-Checked 'dotnet test tests/DropWheel.Engines.Tests' {
        dotnet test tests/DropWheel.Engines.Tests/DropWheel.Engines.Tests.csproj -c $Configuration -p:Platform=$Platform --no-build `
            --logger "trx;LogFileName=engines.trx" --results-directory (Join-Path $Artifacts 'test-results')
    }
}

if ($SkipPack) {
    Write-Host "`n==> packaging skipped (-SkipPack)"
} else {
    # Project mode: winapp builds the app for the requested architecture and packs its output.
    # --no-sign -> unsigned .msix (what the Store / an external signing step wants).
    $csproj = Join-Path $RepoRoot 'src\DropWheel.App\DropWheel.App.csproj'
    Push-Location $Artifacts
    try {
        Invoke-Checked "winapp pack ($Arch, unsigned) -> $Artifacts" {
            winapp pack $csproj -c $Configuration --arch $Arch --no-sign
        }
    } finally { Pop-Location }
    Get-ChildItem $Artifacts -Filter *.msix | ForEach-Object {
        Write-Host ("    {0}  {1:N1} MB" -f $_.Name, ($_.Length / 1MB))
    }
}

Write-Host "`nOK" -ForegroundColor Green
