<#
.SYNOPSIS
    Downloads the sidecar binaries pinned in scripts/sidecars.lock, checks their SHA-256 and unpacks
    ffmpeg.exe, ffprobe.exe, their DLLs and the license into tools/sidecars/<name>/<arch>/.

.DESCRIPTION
    Safe to run again: a folder whose .sha256 marker matches the pin is left alone.
    Same behavior as scripts/fetch-sidecars.sh. Runs in Windows PowerShell 5.1 as well as pwsh 7.

    Usage:
        .\scripts\fetch-sidecars.ps1              # every arch in the lock file
        .\scripts\fetch-sidecars.ps1 -Arch arm64
#>
[CmdletBinding()]
param([string[]]$Arch)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# Windows PowerShell 5.1 may still default to TLS 1.0, which GitHub refuses.
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$RepoRoot = (Resolve-Path (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) '..')).Path
$Root = Join-Path $RepoRoot 'tools/sidecars'
$Cache = Join-Path $Root '.download'
New-Item -ItemType Directory -Force -Path $Cache | Out-Null

function Get-Sha256([string]$Path) { (Get-FileHash -Algorithm SHA256 -LiteralPath $Path).Hash.ToLowerInvariant() }

foreach ($line in Get-Content (Join-Path $RepoRoot 'scripts/sidecars.lock')) {
    if ($line -match '^\s*(#|$)') { continue }
    # Not $arch: PowerShell names are case-insensitive, so that would overwrite the -Arch parameter.
    $name, $lineArch, $url, $sum = -split $line
    if ($Arch -and $Arch -notcontains $lineArch) { continue }

    $dest = Join-Path $Root "$name/$lineArch"
    $marker = Join-Path $dest '.sha256'
    if ((Test-Path $marker) -and (Get-Content $marker -Raw).Trim() -eq $sum) {
        Write-Output "==> $name/$lineArch up to date"
        continue
    }

    $zip = Join-Path $Cache ([IO.Path]::GetFileName($url))
    if (-not (Test-Path $zip) -or (Get-Sha256 $zip) -ne $sum) {
        Write-Output "==> downloading $([IO.Path]::GetFileName($url))"
        for ($try = 1; ; $try++) {
            try { Invoke-WebRequest -Uri $url -OutFile "$zip.part" -UseBasicParsing; break }
            catch { if ($try -ge 3) { throw }; Start-Sleep -Seconds (5 * $try) }
        }
        Move-Item -Force "$zip.part" $zip
    }
    $actual = Get-Sha256 $zip
    if ($actual -ne $sum) { throw "SHA-256 mismatch for ${zip}: expected $sum, got $actual" }

    $work = Join-Path $Cache ("unpack." + [Guid]::NewGuid().ToString('N'))
    Expand-Archive -LiteralPath $zip -DestinationPath $work
    $top = Get-ChildItem -LiteralPath $work -Directory | Select-Object -First 1
    if (Test-Path $dest) { Remove-Item -Recurse -Force $dest }
    New-Item -ItemType Directory -Force -Path $dest | Out-Null
    $bin = Join-Path $top.FullName 'bin'
    Copy-Item (Join-Path $bin 'ffmpeg.exe'), (Join-Path $bin 'ffprobe.exe') $dest
    Copy-Item (Join-Path $bin '*.dll') $dest
    Copy-Item (Join-Path $top.FullName 'LICENSE.txt') (Join-Path $dest 'LICENSE.txt')
    $commit = $top.Name -replace '^ffmpeg-n?[^-]*-[0-9]+-g([0-9a-f]+)-.*$', '$1'
    # WriteAllLines writes UTF-8 without a BOM in both editions; Set-Content cannot in 5.1.
    [IO.File]::WriteAllLines((Join-Path $dest 'SOURCE.txt'), [string[]]@(
        "Binary: $url"
        "SHA-256: $sum"
        'Built by: https://github.com/BtbN/FFmpeg-Builds (LGPL variant, shared libraries)'
        "FFmpeg source: https://github.com/FFmpeg/FFmpeg/commit/$commit"
    ))
    Remove-Item -Recurse -Force $work
    Set-Content -NoNewline -Encoding ascii $marker $sum
    Write-Output "==> $name/$lineArch ready in $dest"
}
