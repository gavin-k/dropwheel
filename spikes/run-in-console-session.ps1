# Runs a command in the logged-on user's interactive console session and waits for it.
# For driving the GUI spikes over SSH: an SSH session has no desktop, so windows cannot show and
# BitBlt captures nothing. Uses a scheduled task with LogonType Interactive (no password needed when
# the user is logged on). Idempotent: re-registering replaces the task; the task is removed afterwards.
#   .\run-in-console-session.ps1 -Command 'powershell -NoProfile -ExecutionPolicy Bypass -File C:\x\run.ps1 -Counters' -TimeoutSeconds 1800
param(
    [Parameter(Mandatory)][string]$Command,
    [int]$TimeoutSeconds = 1800,
    [string]$TaskName = 'DropWheelSpike',
    [string]$OutFile = (Join-Path $env:TEMP 'dropwheel-spike-session.txt')
)
$ErrorActionPreference = 'Stop'
[Console]::OutputEncoding = [Text.Encoding]::UTF8
if (-not (query user 2>$null | Select-String 'console')) { Write-Output "WARNING: nobody is logged on at the console; the GUI will not be visible" }
if (Test-Path $OutFile) { Remove-Item $OutFile -Force }
# No quotes around $Command: cmd /c strips the first and last quote of an argument string that begins with one.
$action = New-ScheduledTaskAction -Execute 'cmd.exe' -Argument "/c $Command > `"$OutFile`" 2>&1"
$principal = New-ScheduledTaskPrincipal -UserId $env:USERNAME -LogonType Interactive -RunLevel Limited
$settings = New-ScheduledTaskSettingsSet -ExecutionTimeLimit (New-TimeSpan -Seconds ($TimeoutSeconds + 60)) -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries -StartWhenAvailable
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false -ErrorAction SilentlyContinue
Register-ScheduledTask -TaskName $TaskName -Action $action -Principal $principal -Settings $settings | Out-Null
Start-ScheduledTask -TaskName $TaskName
$sw = [Diagnostics.Stopwatch]::StartNew()
do {
    Start-Sleep -Seconds 2
    $state = (Get-ScheduledTask -TaskName $TaskName).State
} while ($state -eq 'Running' -and $sw.Elapsed.TotalSeconds -lt $TimeoutSeconds)
$info = Get-ScheduledTaskInfo -TaskName $TaskName
Write-Output "task state=$state lastResult=$($info.LastTaskResult) elapsed=$([int]$sw.Elapsed.TotalSeconds)s"
if ($state -eq 'Running') { Write-Output "TIMEOUT: stopping task"; Stop-ScheduledTask -TaskName $TaskName }
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
if (Test-Path $OutFile) { Get-Content $OutFile -Encoding UTF8 } else { Write-Output "no output file" }
