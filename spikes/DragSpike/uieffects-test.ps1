# §7 item 2 (second half): does SysDragImage still appear with the drag-related visual effects turned off?
# Turns off "Show window contents while dragging", "Use visual effects/animations" (SPI_SETUIEFFECTS),
# "Show shadows under windows" and "Show shadows under mouse pointer" for the duration of one
# `DragSpike --selftest --explorer` run, then restores them. Not persisted (no SPIF_UPDATEINIFILE).
# Run inside the interactive Windows session after building the spike to C:\src\dropwheel (see README).
[Console]::OutputEncoding=[Text.Encoding]::UTF8
$ProgressPreference='SilentlyContinue'
Add-Type -Namespace Spi -Name Native -MemberDefinition @'
[DllImport("user32.dll", SetLastError=true)] public static extern bool SystemParametersInfo(uint a, uint p, ref int v, uint f);
[DllImport("user32.dll", SetLastError=true)] public static extern bool SystemParametersInfo(uint a, uint p, IntPtr v, uint f);
'@
# name, SPI_GET, SPI_SET, value-passed-in-uiParam
$items = @(
  @{ n='DragFullWindows'; get=0x0026; set=0x0025; viaParam=$true },
  @{ n='UiEffects';       get=0x103E; set=0x103F; viaParam=$false },
  @{ n='DropShadow';      get=0x1024; set=0x1025; viaParam=$false },
  @{ n='CursorShadow';    get=0x101A; set=0x101B; viaParam=$false }
)
function Get-Spi($i) { $v = 0; [void][Spi.Native]::SystemParametersInfo($i.get, 0, [ref]$v, 0); $v }
function Set-Spi($i, $on) {
  if ($i.viaParam) { [void][Spi.Native]::SystemParametersInfo($i.set, [uint32]$on, [IntPtr]::Zero, 2) }
  else { [void][Spi.Native]::SystemParametersInfo($i.set, 0, [IntPtr]$on, 2) }
}
$before = @{}
foreach ($i in $items) { $before[$i.n] = Get-Spi $i; "before: $($i.n)=$($before[$i.n])" }
try {
  foreach ($i in $items) { Set-Spi $i 0 }
  foreach ($i in $items) { "during: $($i.n)=$(Get-Spi $i)" }
  Start-Sleep -Seconds 2
  $shell = (Get-Process explorer | Sort-Object StartTime | Select-Object -First 1).Id
  Get-Process explorer | Where-Object { $_.Id -ne $shell -and $_.MainWindowHandle -eq 0 } | ForEach-Object { "killed zombie explorer $($_.Id)"; Stop-Process -Id $_.Id -Force }
  $exe = 'C:\src\dropwheel\spikes\DragSpike\bin\Release\net10.0-windows10.0.26100.0\DragSpike.exe'
  $p = Start-Process -FilePath $exe -ArgumentList '--selftest','--explorer' -PassThru -NoNewWindow -RedirectStandardOutput C:\temp\spike-out.txt -RedirectStandardError C:\temp\spike-err.txt
  if (-not $p.WaitForExit(60000)) { "TIMEOUT; killing"; Stop-Process -Id $p.Id -Force; Get-Process DragSpike -ErrorAction SilentlyContinue | Stop-Process -Force }
  "exit: $($p.ExitCode)"
  Get-Content -Encoding UTF8 C:\temp\spike-out.txt | Select-String 'drag detected|SysDragImage|RESULT|FAIL' | ForEach-Object { "$_" }
} finally {
  foreach ($i in $items) { Set-Spi $i $before[$i.n] }
  foreach ($i in $items) { "after: $($i.n)=$(Get-Spi $i)" }
}
