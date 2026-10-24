# Runs the acrylic spike variants in the interactive session and prints the summaries.
# Idempotent: restores the "Transparency effects" setting it changes.
# EnableTransparency is forced to 1 unless it already is 1: an absent value is NOT "on" everywhere
# (on a Win10 21H2 LTSC machine it meant UISettings.AdvancedEffectsEnabled=False, so acrylic fell back).
#   -IdleSeconds N     extra set: acrylic, 6 cycles, hidden for N s between cycle 1 and 2 (does idle undo the warm-up?)
#   -TransparencyOff   extra set: acrylic with the system "Transparency effects" switched off (expected: Fallback, no black box)
#   -Counters          sample GPU Engine utilization (per pid) and dwm % Processor Time once a second during every set
param(
    [string]$Exe = "C:\src\dropwheel\spikes\AcrylicSpike\bin\Release\net10.0-windows10.0.26100.0\win-arm64\AcrylicSpike.exe",
    [int]$Runs = 20,
    [int]$IdleSeconds = 0,
    [switch]$TransparencyOff,
    [switch]$Counters
)
$ErrorActionPreference = 'Continue'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$key = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize'
Add-Type -Namespace Spike -Name Native -MemberDefinition @'
[DllImport("user32.dll", CharSet=CharSet.Unicode)] public static extern IntPtr SendMessageTimeout(IntPtr h, uint msg, IntPtr w, string l, uint flags, uint timeout, out IntPtr result);
'@
function Get-Transparency { (Get-ItemProperty -Path $key -Name EnableTransparency -ErrorAction SilentlyContinue).EnableTransparency }
function Set-Transparency([object]$value) {
    if ($null -eq $value) { Remove-ItemProperty -Path $key -Name EnableTransparency -ErrorAction SilentlyContinue }
    else { Set-ItemProperty -Path $key -Name EnableTransparency -Value ([int]$value) }
    $r = [IntPtr]::Zero
    # HWND_BROADCAST WM_SETTINGCHANGE "ImmersiveColorSet": what the Settings app sends so UISettings.AdvancedEffectsEnabled updates
    [void][Spike.Native]::SendMessageTimeout([IntPtr]0xFFFF, 0x001A, [IntPtr]::Zero, 'ImmersiveColorSet', 2, 1000, [ref]$r)
    Start-Sleep -Milliseconds 1500
}
function Summarize-Counters($samples, $pids) {
    if (-not $samples -or $samples.Count -eq 0) { Write-Output "counters: no samples"; return }
    $rows = foreach ($set in $samples) {
        $cs = $set.CounterSamples
        $gpu = $cs | Where-Object { $_.Path -like '*gpu engine*' }
        $row = [ordered]@{
            gpu3D_all = ($gpu | Where-Object { $_.InstanceName -like '*engtype_3d*' } | Measure-Object CookedValue -Sum).Sum
            gpuAny_all = ($gpu | Measure-Object CookedValue -Sum).Sum
            dwm_cpu = ($cs | Where-Object { $_.Path -like '*process(dwm)*' } | Measure-Object CookedValue -Sum).Sum
        }
        foreach ($name in $pids.Keys) {
            $row["gpu3D_$name"] = ($gpu | Where-Object { $_.InstanceName -like "pid_$($pids[$name])_*" -and $_.InstanceName -like '*engtype_3d*' } | Measure-Object CookedValue -Sum).Sum
            $row["gpuAny_$name"] = ($gpu | Where-Object { $_.InstanceName -like "pid_$($pids[$name])_*" } | Measure-Object CookedValue -Sum).Sum
        }
        [pscustomobject]$row
    }
    Write-Output "counters: $($rows.Count) one-second samples (GPU % = sum of engine utilization; dwm CPU % = one core = 100)"
    foreach ($col in $rows[0].PSObject.Properties.Name) {
        $m = $rows | Measure-Object $col -Average -Maximum
        Write-Output ("  {0,-16} mean {1,6:F1}  max {2,6:F1}" -f $col, $m.Average, $m.Maximum)
    }
}

$orig = Get-Transparency
Write-Output "EnableTransparency before: $(if ($null -eq $orig) { '<absent>' } else { $orig })"
Write-Output "OS: $((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').DisplayVersion) build $((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').CurrentBuild).$((Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows NT\CurrentVersion').UBR) $env:PROCESSOR_ARCHITECTURE"
Write-Output "GPU: $((Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name) -join ' | ')"
Write-Output "session: $([System.Diagnostics.Process]::GetCurrentProcess().SessionId), interactive: $([Environment]::UserInteractive)"
try {
    if ($orig -ne 1) {
        Set-Transparency 1
        Write-Output "EnableTransparency set to 1 for this run"
    }
    Get-Process AcrylicSpike -ErrorAction SilentlyContinue | Stop-Process -Force
    $logDir = Join-Path $env:LOCALAPPDATA 'DropWheelSpike'
    $sets = @(
        @{ args = @('--variant', 'acrylic', '--runs', "$Runs") },
        @{ args = @('--variant', 'none', '--runs', "$Runs") },
        @{ args = @('--variant', 'opaque', '--runs', "$Runs") },
        @{ args = @('--variant', 'acrylic', '--runs', '5', '--default-config') }
    )
    if ($IdleSeconds -gt 0) { $sets += @{ args = @('--variant', 'acrylic', '--runs', '6', '--idle-ms', "$($IdleSeconds * 1000)") } }
    if ($TransparencyOff) { $sets += @{ args = @('--variant', 'acrylic', '--runs', '5'); transparency = 0 } }
    foreach ($set in $sets) {
        $a = $set.args
        Write-Output ""
        Write-Output ">>> AcrylicSpike $($a -join ' ')$(if ($set.ContainsKey('transparency')) { ' [EnableTransparency=0]' })"
        if ($set.ContainsKey('transparency')) { Set-Transparency $set.transparency; Write-Output "EnableTransparency now: $(Get-Transparency)" }
        $before = Get-ChildItem $logDir -Filter 'acrylic-*.log' -ErrorAction SilentlyContinue | ForEach-Object FullName
        $job = $null
        if ($Counters) {
            $job = Start-Job -ScriptBlock { Get-Counter -Counter '\GPU Engine(*)\Utilization Percentage', '\Process(dwm)\% Processor Time' -SampleInterval 1 -MaxSamples 600 -ErrorAction SilentlyContinue }
            Start-Sleep -Seconds 2
        }
        $p = Start-Process -FilePath $Exe -ArgumentList $a -PassThru
        $timeout = 240000 + $IdleSeconds * 1000
        if (-not $p.WaitForExit($timeout)) {
            Write-Output "TIMEOUT, killing"
            $p.Kill()
        }
        Write-Output "exit code: $($p.ExitCode)"
        if ($job) {
            Start-Sleep -Seconds 1
            Stop-Job $job
            $samples = Receive-Job $job
            Remove-Job $job -Force
            $pids = @{ dwm = (Get-Process dwm | Select-Object -First 1).Id; spike = $p.Id }
            Summarize-Counters $samples $pids
        }
        if ($set.ContainsKey('transparency')) { Set-Transparency $orig; Write-Output "EnableTransparency restored: $(if ($null -eq (Get-Transparency)) { '<absent>' } else { Get-Transparency })" }
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
    if ((Get-Transparency) -ne $orig) {
        Set-Transparency $orig
        Write-Output "EnableTransparency restored to $(if ($null -eq $orig) { '<absent>' } else { $orig })"
    }
}
