# Runs the acrylic spike variants on the VM and prints the summaries.
# Idempotent: restores the "Transparency effects" setting it may have turned on.
param(
    [string]$Exe = "C:\src\dropwheel\spikes\AcrylicSpike\bin\Release\net10.0-windows10.0.26100.0\win-arm64\AcrylicSpike.exe",
    [int]$Runs = 20
)
$ErrorActionPreference = 'Continue'
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
$orig = (Get-ItemProperty -Path $key -Name EnableTransparency -ErrorAction SilentlyContinue).EnableTransparency
Write-Output "EnableTransparency before: $orig"
try {
    if ($orig -eq 0) {
        Set-ItemProperty -Path $key -Name EnableTransparency -Value 1
        Write-Output "EnableTransparency set to 1 for this run"
    }
    Get-Process AcrylicSpike -ErrorAction SilentlyContinue | Stop-Process -Force
    $gpu = Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name
    Write-Output "GPU: $gpu"
    $logDir = Join-Path $env:LOCALAPPDATA 'DropWheelSpike'
    $sets = @(
        @('--variant', 'acrylic', '--runs', "$Runs"),
        @('--variant', 'none', '--runs', "$Runs"),
        @('--variant', 'opaque', '--runs', "$Runs"),
        @('--variant', 'acrylic', '--runs', '5', '--default-config')
    )
    foreach ($args in $sets) {
        Write-Output ""
        Write-Output ">>> AcrylicSpike $($args -join ' ')"
        $before = Get-ChildItem $logDir -Filter 'acrylic-*.log' -ErrorAction SilentlyContinue | ForEach-Object FullName
        $p = Start-Process -FilePath $Exe -ArgumentList $args -PassThru
        if (-not $p.WaitForExit(240000)) {
            Write-Output "TIMEOUT, killing"
            $p.Kill()
        }
        Write-Output "exit code: $($p.ExitCode)"
        $log = Get-ChildItem $logDir -Filter 'acrylic-*.log' | Where-Object { $before -notcontains $_.FullName } | Sort-Object LastWriteTime | Select-Object -Last 1
        if ($log) {
            Write-Output "log: $($log.FullName)"
            Get-Content $log.FullName
        } else {
            Write-Output "no new log file"
        }
    }
}
finally {
    if ($orig -eq 0) {
        Set-ItemProperty -Path $key -Name EnableTransparency -Value 0
        Write-Output "EnableTransparency restored to 0"
    }
}
