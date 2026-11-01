# Second end-to-end test, throwaway windows only: maximize, edge reveal, minimize, one-at-a-time.
# Temporarily replaces settings.ini for the one-at-a-time part and puts it back afterwards.
param([string]$ShotDir = $env:TEMP)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$bin = Join-Path $PSScriptRoot 'bin'
$debug = Join-Path $ShotDir 'stage-state.txt'
$ini = "$env:APPDATA\StageManagerWin\settings.ini"
$iniBackup = if (Test-Path $ini) { Get-Content $ini -Raw } else { $null }

Add-Type -AssemblyName System.Windows.Forms
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class W {
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L,T,Ri,B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int c);
  [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
}
'@
[W]::SetProcessDPIAware() | Out-Null

$script:fails = 0
function Check($name, $ok, $detail = '') { if ($ok) { "PASS  $name" } else { "FAIL  $name  $detail"; $script:fails++ } }
function Left($h) { $r = New-Object W+R; [W]::GetWindowRect([IntPtr][long]$h, [ref]$r) | Out-Null; $r.L }
function OnScreen($h) { (Left $h) -lt 1920 }
function State {
  Start-Sleep -Milliseconds 900
  $s = @{ tiles = @(); active = @() }
  foreach ($l in Get-Content $debug) {
    if ($l -match '^strip=(\d+) shown=(\w+)') { $s.strip = [long]$Matches[1]; $s.shown = $Matches[2] }
    elseif ($l -match '^active=(.*)') { $s.active = @($Matches[1] -split ',' | Where-Object { $_ }) }
    elseif ($l -match '^tile=(\d+),(\d+),(\d+),(\d+) windows=(.*)') {
      $s.tiles += ,@{ x = [int]$Matches[1]; y = [int]$Matches[2]; win = @($Matches[5] -split ',') } }
  }
  $s
}
function ClickTileWith($s, $h) {
  foreach ($t in $s.tiles) {
    if ($t.win -contains "$h") {
      $lp = [IntPtr](($t.y + 40) * 65536 + $t.x + 40)
      [W]::PostMessage([IntPtr]$s.strip, 0x201, [IntPtr]1, $lp) | Out-Null
      [W]::PostMessage([IntPtr]$s.strip, 0x202, [IntPtr]0, $lp) | Out-Null
      return
    }
  }
}
function Launch($n) { $p = Start-Process (Join-Path $bin "$n.exe") -PassThru; $p.WaitForInputIdle(3000) | Out-Null; Start-Sleep -Milliseconds 300; $p.Refresh(); $p }
function StartApp { $env:STAGE_DEBUG_FILE = $debug; Start-Process (Join-Path $root 'StageManager.exe') -ArgumentList '--only', 'StageTestA,StageTestB,StageTestC' -PassThru }
function StopApp($a) { Stop-Process -Id $a.Id -Force; Start-Sleep -Milliseconds 200; Start-Process (Join-Path $root 'StageManager.exe') -ArgumentList '--restore' -Wait }

$savedCursor = [Windows.Forms.Cursor]::Position
$procs = @(); $app = $null
try {
  [W]::SetCursorPos(1000, 500) | Out-Null
  $a = Launch 'StageTestA'; $b = Launch 'StageTestB'; $procs += $a, $b
  $ha = $a.MainWindowHandle.ToInt64(); $hb = $b.MainWindowHandle.ToInt64()
  $app = StartApp
  Start-Sleep -Milliseconds 800
  $s = State
  ClickTileWith $s $ha
  $s = State
  Check 'setup: A on stage, strip shown' ((OnScreen $ha) -and $s.shown -eq 'True') "shown=$($s.shown)"

  # Maximized window overlaps the strip: strip steps aside, comes back at the left edge.
  [W]::ShowWindow([IntPtr]$ha, 3) | Out-Null
  $s = State
  Check 'maximize: strip hides' ($s.shown -eq 'False') "shown=$($s.shown)"
  [W]::SetCursorPos(0, 500) | Out-Null
  $s = State
  Check 'left edge: strip reveals' ($s.shown -eq 'True') "shown=$($s.shown)"
  [W]::SetCursorPos(1000, 500) | Out-Null
  $s = State
  Check 'cursor away: strip hides again' ($s.shown -eq 'False') "shown=$($s.shown)"

  # A maximized window survives a round trip off-screen.
  $leftMax = Left $ha
  [W]::SetCursorPos(0, 500) | Out-Null; $s = State
  ClickTileWith $s $hb
  [W]::SetCursorPos(1000, 500) | Out-Null; $s = State
  Check 'maximized: leaves stage on switch' (-not (OnScreen $ha)) "left=$(Left $ha)"
  ClickTileWith $s $ha
  $s = State
  Check 'maximized: returns to same spot' ((Left $ha) -eq $leftMax) "left=$(Left $ha) want=$leftMax"
  [W]::ShowWindow([IntPtr]$ha, 9) | Out-Null
  $s = State

  # Minimizing the only on-stage window sends it to the strip; clicking restores it.
  [W]::ShowWindow([IntPtr]$ha, 6) | Out-Null
  $s = State
  $inTile = @($s.tiles | Where-Object { $_.win -contains "$ha" }).Count -eq 1
  Check 'minimize: goes to the strip' ($inTile -or ($s.active -notcontains "$ha")) "active=$($s.active -join ',')"
  ClickTileWith $s $ha
  $s = State
  Check 'minimize: click restores it' ((-not [W]::IsIconic([IntPtr]$ha)) -and (OnScreen $ha)) "iconic=$([W]::IsIconic([IntPtr]$ha)) left=$(Left $ha)"
  StopApp $app; $app = $null

  # One-at-a-time: one of the app's windows on stage, the other waits in the strip.
  $a2 = Launch 'StageTestA'; $procs += $a2
  $ha2 = $a2.MainWindowHandle.ToInt64()
  Set-Content $ini "enabled=1`nshowRecent=1`nshowDesktopItems=0`noneAtATime=1"
  $app = StartApp
  Start-Sleep -Milliseconds 800
  $s = State
  ClickTileWith $s $ha
  $s = State
  $on = @($ha, $ha2 | Where-Object { OnScreen $_ }).Count
  Check 'one-at-a-time: single window of the app on stage' ($on -eq 1) "on=$on"
  $waiting = if (OnScreen $ha) { $ha2 } else { $ha }
  $shown = if (OnScreen $ha) { $ha } else { $ha2 }
  Check 'one-at-a-time: the other waits in the strip' (@($s.tiles | Where-Object { $_.win -contains "$waiting" }).Count -eq 1)
  ClickTileWith $s $waiting
  $s = State
  Check 'one-at-a-time: click swaps them' ((OnScreen $waiting) -and -not (OnScreen $shown)) "waiting=$(Left $waiting) shown=$(Left $shown)"
  StopApp $app; $app = $null

  $off = @($procs | ForEach-Object { $_.MainWindowHandle.ToInt64() } | Where-Object { -not (OnScreen $_) }).Count
  Check 'cleanup: every window back on screen' ($off -eq 0) "off=$off"
}
finally {
  if ($app) { StopApp $app }
  foreach ($p in $procs) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
  if ($iniBackup -ne $null) { Set-Content $ini $iniBackup -NoNewline } elseif (Test-Path $ini) { Remove-Item $ini }
  [W]::SetCursorPos($savedCursor.X, $savedCursor.Y) | Out-Null
  Remove-Item Env:STAGE_DEBUG_FILE -ErrorAction SilentlyContinue
}
"FAILS: $script:fails"
if (Test-Path "$env:APPDATA\StageManagerWin\error.log") { "--- error.log ---"; Get-Content "$env:APPDATA\StageManagerWin\error.log" -Tail 15 }
