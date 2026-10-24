<#
.SYNOPSIS
    One-shot, idempotent Windows developer setup for DropWheel.

.DESCRIPTION
    Installs / verifies everything needed to build DropWheel.slnx and package the WinUI 3 app:

      1. .NET 10 SDK                       winget Microsoft.DotNet.SDK.10      (fallback: dotnet-install.ps1)
      2. winapp CLI                        winget Microsoft.winappcli          (fallback: GitHub release zip)
      3. Visual Studio 2022 Community      winget Microsoft.VisualStudio.2022.Community
                                           + workloads Microsoft.VisualStudio.Workload.Universal (WinUI)
                                                       Microsoft.VisualStudio.Workload.ManagedDesktop
                                                                               (fallback: vs_community.exe bootstrapper)
      4. Developer Mode                    HKLM\...\AppModelUnlock
      5. Local dev certificate             winapp cert generate --if-exists skip  -> <repo>\devcert.pfx (git-ignored)

    Every step checks first and skips when already satisfied, so the script can be re-run at any time.
    winget needs an interactive user session; when it is unusable (e.g. running as SYSTEM through
    `prlctl exec`) the script falls back to direct downloads automatically.

    Run from an elevated PowerShell (Windows PowerShell 5.1 or pwsh 7):
        powershell -NoProfile -ExecutionPolicy Bypass -File .\scripts\setup-windows.ps1
    Options:
        -SkipVisualStudio      only the CLI toolchain (SDK + winapp + dev mode + cert)
        -SkipCert              do not generate devcert.pfx
        -RepoRoot <path>       repository root (default: parent of this script's folder)

.NOTES
    Visual Studio takes 20-60 minutes. The script waits for the installer; a machine with less than
    8 GB RAM should not build at the same time.
#>
[CmdletBinding()]
param(
    [switch]$SkipVisualStudio,
    [switch]$SkipCert,
    [string]$RepoRoot
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
# $PSScriptRoot is not populated while parameter defaults are evaluated in Windows PowerShell 5.1.
$ScriptDir = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $RepoRoot) { $RepoRoot = (Resolve-Path (Join-Path $ScriptDir '..')).Path }
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
try { [Console]::OutputEncoding = [Text.Encoding]::UTF8 } catch { }
$env:WINAPP_CLI_TELEMETRY_OPTOUT = '1'

function Get-WinappVersion([string]$exe) {
    if (((& $exe --version 2>&1) | Out-String) -match '(\d+\.\d+[\d.]*)') { return $Matches[1] } else { return '?' }
}

$Arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64') { 'arm64' } else { 'x64' }
$Temp = Join-Path $env:TEMP 'dropwheel-setup'
New-Item -ItemType Directory -Force -Path $Temp | Out-Null
$Summary = New-Object System.Collections.Generic.List[string]

function Write-Step([string]$msg) { Write-Host "`n==> $msg" -ForegroundColor Cyan }
function Write-Ok([string]$msg)   { Write-Host "    [ok]   $msg" -ForegroundColor Green;  $Summary.Add("[ok]   $msg") }
function Write-Todo([string]$msg) { Write-Host "    [todo] $msg" -ForegroundColor Yellow; $Summary.Add("[todo] $msg") }

function Test-IsAdmin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    return (New-Object Security.Principal.WindowsPrincipal $id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}
if (-not (Test-IsAdmin)) { throw 'Run this script from an elevated (Administrator) PowerShell.' }

function Refresh-Path {
    $env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
}
function Add-MachinePath([string]$dir) {
    $p = [Environment]::GetEnvironmentVariable('Path', 'Machine')
    if (($p -split ';') -notcontains $dir) {
        [Environment]::SetEnvironmentVariable('Path', "$p;$dir", 'Machine')
    }
    Refresh-Path
}

# ---------------------------------------------------------------------------------------------
# winget: locate it even when not on PATH (SYSTEM account) and verify it actually answers.
# ---------------------------------------------------------------------------------------------
$script:Winget = $null
function Find-Winget {
    $cmd = Get-Command winget.exe -ErrorAction SilentlyContinue
    if ($cmd) { return $cmd.Source }
    $pkg = Get-ChildItem 'C:\Program Files\WindowsApps\Microsoft.DesktopAppInstaller_*\winget.exe' -ErrorAction SilentlyContinue |
        Sort-Object FullName -Descending | Select-Object -First 1
    if ($pkg) { return $pkg.FullName }
    return $null
}
function Test-Winget {
    $exe = Find-Winget
    if (-not $exe) { return $false }
    try {
        $p = Start-Process -FilePath $exe -ArgumentList '--version' -PassThru -WindowStyle Hidden -RedirectStandardOutput (Join-Path $Temp 'winget-version.txt')
        if (-not $p.WaitForExit(60000)) { $p.Kill(); return $false }
        if ($p.ExitCode -ne 0) { return $false }
        $script:Winget = $exe
        return $true
    } catch { return $false }
}
function Invoke-Winget([string]$id, [string]$override) {
    # Returns $true on success (incl. "already installed"), $false otherwise. Never throws.
    if (-not $script:Winget) { return $false }
    $args = @('install', '--id', $id, '--exact', '--silent', '--scope', 'machine',
              '--accept-source-agreements', '--accept-package-agreements', '--disable-interactivity')
    if ($override) { $args += @('--override', $override) }
    Write-Host "    winget $($args -join ' ')"
    $p = Start-Process -FilePath $script:Winget -ArgumentList $args -PassThru -Wait -WindowStyle Hidden
    # 0 ok · 0x8A15002B no applicable upgrade · 0x8A150011 already installed · 3010 reboot required
    return $p.ExitCode -in @(0, 3010, -1978335189, -1978335215)
}

Write-Step "DropWheel Windows setup  ·  arch=$Arch  ·  repo=$RepoRoot"
$wingetOk = Test-Winget
if ($wingetOk) { Write-Host "    winget: $script:Winget" } else { Write-Host '    winget: unavailable in this session, using direct downloads' -ForegroundColor Yellow }

# ---------------------------------------------------------------------------------------------
# 1. .NET 10 SDK
# ---------------------------------------------------------------------------------------------
Write-Step '.NET 10 SDK'
Refresh-Path
function Get-Dotnet10 {
    foreach ($exe in @((Get-Command dotnet.exe -ErrorAction SilentlyContinue | ForEach-Object Source), 'C:\Program Files\dotnet\dotnet.exe')) {
        if ($exe -and (Test-Path $exe)) {
            $sdks = & $exe --list-sdks 2>$null
            if ($sdks -match '^10\.') { return $exe }
        }
    }
    return $null
}
$dotnet = Get-Dotnet10
if ($dotnet) {
    Write-Ok ".NET SDK $((& $dotnet --list-sdks | Select-String '^10\.' | Select-Object -Last 1) -replace '\s.*','') already installed"
} else {
    $done = $wingetOk -and (Invoke-Winget 'Microsoft.DotNet.SDK.10')
    if (-not $done) {
        Write-Host '    downloading dotnet-install.ps1 ...'
        $inst = Join-Path $Temp 'dotnet-install.ps1'
        Invoke-WebRequest 'https://dot.net/v1/dotnet-install.ps1' -OutFile $inst
        & $inst -Channel 10.0 -InstallDir 'C:\Program Files\dotnet' -NoPath
        [Environment]::SetEnvironmentVariable('DOTNET_ROOT', 'C:\Program Files\dotnet', 'Machine')
    }
    Add-MachinePath 'C:\Program Files\dotnet'
    $dotnet = Get-Dotnet10
    if (-not $dotnet) { throw '.NET 10 SDK installation failed.' }
    Write-Ok ".NET SDK installed: $(& $dotnet --version)"
}
[Environment]::SetEnvironmentVariable('DOTNET_CLI_TELEMETRY_OPTOUT', '1', 'Machine')

# ---------------------------------------------------------------------------------------------
# 2. winapp CLI
# ---------------------------------------------------------------------------------------------
Write-Step 'winapp CLI'
Refresh-Path
$winapp = Get-Command winapp.exe -ErrorAction SilentlyContinue | ForEach-Object Source
if (-not $winapp -and (Test-Path 'C:\Program Files\winapp\winapp.exe')) { $winapp = 'C:\Program Files\winapp\winapp.exe'; Add-MachinePath 'C:\Program Files\winapp' }
if ($winapp) {
    Write-Ok "winapp $(Get-WinappVersion $winapp) already installed ($winapp)"
} else {
    $done = $wingetOk -and (Invoke-Winget 'Microsoft.winappcli')
    Refresh-Path
    $winapp = Get-Command winapp.exe -ErrorAction SilentlyContinue | ForEach-Object Source
    if (-not $done -or -not $winapp) {
        $zip = Join-Path $Temp "winappcli-$Arch.zip"
        $url = "https://github.com/microsoft/WinAppCli/releases/latest/download/winappcli-$Arch.zip"
        Write-Host "    downloading $url ..."
        Invoke-WebRequest $url -OutFile $zip
        $dest = 'C:\Program Files\winapp'
        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        Expand-Archive -Path $zip -DestinationPath $dest -Force
        Get-ChildItem $dest -Filter *.pdb | Remove-Item -Force   # ~340 MB of symbols nobody needs
        Add-MachinePath $dest
        $winapp = Join-Path $dest 'winapp.exe'
    }
    Write-Ok "winapp $(Get-WinappVersion $winapp) installed ($winapp)"
}

# ---------------------------------------------------------------------------------------------
# 3. Developer Mode (needed to register the app from build output / sideload unsigned dev builds)
# ---------------------------------------------------------------------------------------------
Write-Step 'Developer Mode'
$key = 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\AppModelUnlock'
New-Item -Path $key -Force | Out-Null
$cur = (Get-ItemProperty $key -ErrorAction SilentlyContinue)
if ($cur.AllowDevelopmentWithoutDevLicense -eq 1 -and $cur.AllowAllTrustedApps -eq 1) {
    Write-Ok 'Developer Mode already enabled'
} else {
    Set-ItemProperty $key -Name AllowDevelopmentWithoutDevLicense -Value 1 -Type DWord
    Set-ItemProperty $key -Name AllowAllTrustedApps -Value 1 -Type DWord
    Write-Ok 'Developer Mode enabled'
}

# ---------------------------------------------------------------------------------------------
# 4. Local development certificate (publisher read from Package.appxmanifest). Never committed:
#    *.pfx is in .gitignore. Default password "password" - local use only.
# ---------------------------------------------------------------------------------------------
Write-Step 'Development certificate'
if ($SkipCert) {
    Write-Host '    skipped (-SkipCert)'
} else {
    $manifest = Join-Path $RepoRoot 'src\DropWheel.App\Package.appxmanifest'
    $pfx = Join-Path $RepoRoot 'devcert.pfx'
    if (-not (Test-Path $manifest)) { throw "manifest not found: $manifest (pass -RepoRoot)" }
    Push-Location $RepoRoot
    try {
        & $winapp cert generate --manifest $manifest --output $pfx --if-exists skip 2>&1 |
            Where-Object { $_ -match 'certificate|Certificate|⚠|✅|ℹ|error|Error' }
        if ($LASTEXITCODE -ne 0) { throw "winapp cert generate failed ($LASTEXITCODE)" }
    } finally { Pop-Location }
    Write-Ok "dev certificate: $pfx (git-ignored; install with: winapp cert install $pfx)"
}

# ---------------------------------------------------------------------------------------------
# 5. Visual Studio 2022 Community with the WinUI + .NET desktop workloads
# ---------------------------------------------------------------------------------------------
Write-Step 'Visual Studio 2022 Community'
$Workloads = @('Microsoft.VisualStudio.Workload.Universal', 'Microsoft.VisualStudio.Workload.ManagedDesktop')
$vswhere = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\vswhere.exe'
$vsInstaller = 'C:\Program Files (x86)\Microsoft Visual Studio\Installer\setup.exe'
function Get-VsCommunity([string[]]$requires) {
    if (-not (Test-Path $vswhere)) { return $null }
    $args = @('-products', 'Microsoft.VisualStudio.Product.Community', '-version', '[17.0,18.0)', '-property', 'installationPath', '-nologo')
    if ($requires) { $args += @('-requires') + $requires }
    $r = & $vswhere @args 2>$null
    if ($r) { return ($r | Select-Object -First 1) }
    return $null
}
if ($SkipVisualStudio) {
    Write-Host '    skipped (-SkipVisualStudio)'
} elseif (Get-VsCommunity $Workloads) {
    Write-Ok "Visual Studio 2022 Community with WinUI + .NET desktop workloads already installed ($(Get-VsCommunity $Workloads))"
} else {
    $addArgs = ($Workloads | ForEach-Object { "--add $_" }) -join ' '
    $installPath = Get-VsCommunity $null
    if ($installPath) {
        # Community present, workloads missing -> modify
        Write-Host "    adding workloads to existing instance $installPath (this takes a while) ..."
        $p = Start-Process -FilePath $vsInstaller -ArgumentList "modify --installPath `"$installPath`" $addArgs --includeRecommended --quiet --norestart --wait" -PassThru -Wait
        $code = $p.ExitCode
    } else {
        $override = "--quiet --wait --norestart $addArgs --includeRecommended"
        $code = $null
        if ($wingetOk) {
            Write-Host '    installing via winget (20-60 min) ...'
            if (Invoke-Winget 'Microsoft.VisualStudio.2022.Community' $override) { $code = 0 }
        }
        if ($code -ne 0) {
            $boot = Join-Path $Temp 'vs_community.exe'
            Write-Host '    downloading vs_community.exe bootstrapper ...'
            Invoke-WebRequest 'https://aka.ms/vs/17/release/vs_community.exe' -OutFile $boot
            Write-Host '    running bootstrapper (20-60 min, no UI) ...'
            $p = Start-Process -FilePath $boot -ArgumentList $override -PassThru -Wait
            $code = $p.ExitCode
        }
    }
    if ($code -in @(0, 3010)) {
        $msg = "Visual Studio 2022 Community + WinUI/.NET desktop workloads installed"
        if ($code -eq 3010) { $msg += ' (reboot required)' }
        Write-Ok $msg
    } else {
        Write-Todo "Visual Studio installer exited with $code - re-run this script or install the workloads from the VS Installer UI"
    }
}

# ---------------------------------------------------------------------------------------------
Write-Step 'Summary'
$Summary | ForEach-Object { Write-Host "    $_" }
Write-Host "`nNext: pwsh .\scripts\build-windows.ps1   (or: powershell -File .\scripts\build-windows.ps1)`n"
