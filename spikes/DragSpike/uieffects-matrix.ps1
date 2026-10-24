# §7 item 2 (second half), finer-grained than uieffects-test.ps1: runs `DragSpike --selftest --explorer`
# with the four drag-related visual effects (a) all on (control), (b) all off, twice, (c) each one off
# alone, (d) all on again, and prints one line per run: detection source, wheel first paint, drop
# latency. Every run restores the settings it changed; nothing is persisted (no SPIF_UPDATEINIFILE).
# Unlike uieffects-test.ps1 it does NOT kill window-less explorer.exe processes before the run.
# Run inside the interactive session (double-click, `prlctl exec --current-user`, or
# `spikes/run-in-console-session.ps1` over SSH).
param([string]$Exe = 'C:\src\dropwheel\spikes\DragSpike\bin\Release\net10.0-windows10.0.26100.0\DragSpike.exe')
[Console]::OutputEncoding = [Text.Encoding]::UTF8
$ProgressPreference = 'SilentlyContinue'
Add-Type -Namespace Spi -Name Native -MemberDefinition @'
[DllImport("user32.dll", SetLastError=true)] public static extern bool SystemParametersInfo(uint a, uint p, ref int v, uint f);
[DllImport("user32.dll", SetLastError=true)] public static extern bool SystemParametersInfo(uint a, uint p, IntPtr v, uint f);
'@
$items = @{
  DragFullWindows = @{ get = 0x0026; set = 0x0025; viaParam = $true }
  UiEffects       = @{ get = 0x103E; set = 0x103F; viaParam = $false }
  DropShadow      = @{ get = 0x1024; set = 0x1025; viaParam = $false }
  CursorShadow    = @{ get = 0x101A; set = 0x101B; viaParam = $false }
}
function Get-Spi($i) { $v = 0; [void][Spi.Native]::SystemParametersInfo($i.get, 0, [ref]$v, 0); $v }
function Set-Spi($i, $on) {
  if ($i.viaParam) { [void][Spi.Native]::SystemParametersInfo($i.set, [uint32]$on, [IntPtr]::Zero, 2) }
  else { [void][Spi.Native]::SystemParametersInfo($i.set, 0, [IntPtr]$on, 2) }
}
$before = @{}; foreach ($n in $items.Keys) { $before[$n] = Get-Spi $items[$n] }
"before: " + (($items.Keys | Sort-Object | ForEach-Object { "$_=$($before[$_])" }) -join ' ')
function Invoke-Run($tag) {
  Get-Process DragSpike -ErrorAction SilentlyContinue | Stop-Process -Force
  $logDir = Join-Path $env:LOCALAPPDATA 'DropWheelSpike'
  $prev = Get-ChildItem $logDir -Filter 'spike-*.log' -ErrorAction SilentlyContinue | ForEach-Object FullName
  $p = Start-Process -FilePath $Exe -ArgumentList '--selftest', '--explorer' -PassThru -NoNewWindow -RedirectStandardOutput "$env:TEMP\uifx-out.txt" -RedirectStandardError "$env:TEMP\uifx-err.txt"
  if (-not $p.WaitForExit(60000)) { Stop-Process -Id $p.Id -Force; Get-Process DragSpike -ErrorAction SilentlyContinue | Stop-Process -Force }
  $log = Get-ChildItem $logDir -Filter 'spike-*.log' | Where-Object { $prev -notcontains $_.FullName } | Sort-Object LastWriteTime | Select-Object -Last 1
  if (-not $log) { "{0,-34} no log" -f $tag; return }
  $t = Get-Content $log.FullName -Encoding UTF8
  $det = ($t | Select-String 'PASS  drag detected|FAIL  drag detected' | Select-Object -First 1) -replace '.*drag detected: ', ''
  $drop = ($t | Select-String 'drop received' | Select-Object -First 1) -replace '.*drop received with the right file: \[[^\]]*\] ', ''
  $paint = ($t | Select-String 'wheel visible' | Select-Object -First 1) -replace '.*budget: ', ''
  $res = ($t | Select-String 'RESULT:' | Select-Object -Last 1) -replace '.*RESULT: ', ''
  "{0,-34} {1,-4} detect[{2}] paint[{3}] drop[{4}] log={5}" -f $tag, $res, $det, $paint, $drop, $log.Name
  Start-Sleep 2
}
$all = @('DragFullWindows', 'UiEffects', 'DropShadow', 'CursorShadow')
try {
  Invoke-Run 'control(all on)'
  foreach ($set in @($all, $all, @('DragFullWindows'), @('UiEffects'), @('DropShadow'))) {
    foreach ($n in $items.Keys) { Set-Spi $items[$n] ([int]($set -notcontains $n)) }
    Start-Sleep 2
    Invoke-Run ("off: " + ($set -join ','))
    foreach ($n in $items.Keys) { Set-Spi $items[$n] $before[$n] }
    Start-Sleep 1
  }
  Invoke-Run 'control(all on) #2'
} finally {
  foreach ($n in $items.Keys) { Set-Spi $items[$n] $before[$n] }
  "after:  " + (($items.Keys | Sort-Object | ForEach-Object { "$_=$(Get-Spi $items[$_])" }) -join ' ')
}
