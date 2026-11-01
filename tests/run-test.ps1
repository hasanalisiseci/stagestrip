# End-to-end test against throwaway windows only (the app runs with --only StageTest*).
# Moves the real mouse during the drag steps. Usage: tests\run-test.ps1 <screenshot-dir>
param([string]$ShotDir = $env:TEMP)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$bin = Join-Path $PSScriptRoot 'bin'
$debug = Join-Path $ShotDir 'stage-state.txt'

Add-Type -AssemblyName System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class W {
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L,T,Ri,B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, int d, IntPtr e);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct P { public int X, Y; }
  [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(P p);
  [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint f);
  [DllImport("user32.dll")] public static extern bool GetCursorPos(out P p);
  public static long RootAt(int x, int y) { P p; p.X = x; p.Y = y; return GetAncestor(WindowFromPoint(p), 2).ToInt64(); }
}
'@
[W]::SetProcessDPIAware() | Out-Null

$script:fails = 0
function Check($name, $ok, $detail = '') {
  if ($ok) { "PASS  $name" } else { "FAIL  $name  $detail"; $script:fails++ }
}
function Left($h) { $r = New-Object W+R; [W]::GetWindowRect([IntPtr][long]$h, [ref]$r) | Out-Null; $r.L }
function OnScreen($h) { (Left $h) -lt 1920 }
function State {
  Start-Sleep -Milliseconds 900
  $s = @{ tiles = @() }
  # The app rewrites this file constantly; only trust a complete copy ("moved=" is its last line).
  $lines = @()
  foreach ($try in 1..20) {
    try { $lines = @(Get-Content $debug -ErrorAction Stop) } catch { $lines = @() }
    if ($lines.Count -gt 0 -and $lines[-1] -like 'moved=*') { break }
    Start-Sleep -Milliseconds 50
  }
  $script:lastState = $lines -join ' | '
  foreach ($l in $lines) {
    if ($l -match '^strip=(\d+) shown=(\w+)') { $s.strip = [long]$Matches[1]; $s.shown = $Matches[2] }
    elseif ($l -match '^active=(.*)') { $s.active = @($Matches[1] -split ',' | Where-Object { $_ }) }
    elseif ($l -match '^tile=(\d+),(\d+),(\d+),(\d+) windows=(.*)') {
      $s.tiles += ,@{ x = [int]$Matches[1]; y = [int]$Matches[2]; w = [int]$Matches[3]; h = [int]$Matches[4]; win = @($Matches[5] -split ',') } }
  }
  $s
}
function ClickTile($s, $i) {
  $t = $s.tiles[$i]
  $lp = [IntPtr](($t.y + 40) * 65536 + $t.x + 40)
  [W]::PostMessage([IntPtr]$s.strip, 0x201, [IntPtr]1, $lp) | Out-Null
  [W]::PostMessage([IntPtr]$s.strip, 0x202, [IntPtr]0, $lp) | Out-Null
}
# Real mouse drag. Never presses unless the cursor is really over the expected window, so it
# can't click into whatever else is on screen if someone else is using the mouse. Returns success.
function Drag($x1, $y1, $x2, $y2, $expected) {
  [W]::SetCursorPos($x1, $y1) | Out-Null; Start-Sleep -Milliseconds 150
  $p = New-Object W+P; [W]::GetCursorPos([ref]$p) | Out-Null
  $under = [W]::RootAt($x1, $y1)
  $script:why = "cursor=$($p.X),$($p.Y) wanted=$x1,$y1 under=$under expected=$expected"
  if ($p.X -ne $x1 -or $p.Y -ne $y1 -or $under -ne $expected) { return $false }
  [W]::mouse_event(2, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 150
  foreach ($i in 1..8) {
    [W]::SetCursorPos($x1 + ($x2 - $x1) * $i / 8, $y1 + ($y2 - $y1) * $i / 8) | Out-Null
    Start-Sleep -Milliseconds 40
  }
  Start-Sleep -Milliseconds 150
  [W]::mouse_event(4, 0, 0, 0, [IntPtr]::Zero)
  $true
}
function Shot($name) {
  $b = New-Object Drawing.Bitmap 200, 1040
  $g = [Drawing.Graphics]::FromImage($b)
  $g.CopyFromScreen(0, 0, 0, 0, $b.Size)
  $b.Save((Join-Path $ShotDir $name)); $g.Dispose(); $b.Dispose()
}
function Launch($n) { $p = Start-Process (Join-Path $bin "$n.exe") -PassThru; $p.WaitForInputIdle(3000) | Out-Null; $p.Refresh(); $p }

Add-Type -AssemblyName System.Windows.Forms
$savedCursor = [Windows.Forms.Cursor]::Position
$procs = @()
$app = $null
try {
  $a = Launch 'StageTestA'; $b = Launch 'StageTestB'; $c = Launch 'StageTestC'; $procs += $a, $b, $c
  Start-Sleep -Milliseconds 500
  $ha = $a.MainWindowHandle.ToInt64(); $hb = $b.MainWindowHandle.ToInt64(); $hc = $c.MainWindowHandle.ToInt64()
  $origLeft = Left $ha

  $env:STAGE_DEBUG_FILE = $debug
  $app = Start-Process (Join-Path $root 'StageManager.exe') -ArgumentList '--only', 'StageTestA,StageTestB,StageTestC' -PassThru
  Start-Sleep -Milliseconds 800

  $s = State
  $onCount = @($ha, $hb, $hc | Where-Object { OnScreen $_ }).Count
  Check 'startup: at most one app stays on stage' ($onCount -le 1) "on=$onCount"
  Check 'startup: others become tiles' ($s.tiles.Count -ge 2) "tiles=$($s.tiles.Count)"

  # Click: chosen tile comes on stage, previous one leaves.
  $target = [long]$s.tiles[0].win[-1]
  ClickTile $s 0
  $s = State
  Check 'click: tile window comes on stage' (OnScreen $target) "left=$(Left $target)"
  Check 'click: it is the active stage' ($s.active -contains "$target")
  $onCount = @($ha, $hb, $hc | Where-Object { OnScreen $_ }).Count
  Check 'click: only one app on stage' ($onCount -eq 1) "on=$onCount"
  Check 'click: strip visible next to window' ($s.shown -eq 'True') "shown=$($s.shown)"
  Shot 'strip-1-click.png'

  $prev = $target
  $target = [long]$s.tiles[0].win[-1]
  ClickTile $s 0
  $s = State
  Check 'switch: new window on stage' (OnScreen $target)
  Check 'switch: previous window left stage' (-not (OnScreen $prev))
  Check 'switch: previous is top tile' ($s.tiles[0].win -contains "$prev")

  # Second window of the same app shares its stage.
  $a2 = Launch 'StageTestA'; $procs += $a2
  Start-Sleep -Milliseconds 300
  $ha2 = $a2.MainWindowHandle.ToInt64()
  $s = State
  $inA = @($s.tiles | Where-Object { $_.win -contains "$ha" }) + @()
  $together = ($s.active -contains "$ha" -and $s.active -contains "$ha2") -or ($inA.Count -eq 1 -and $inA[0].win -contains "$ha2")
  Check 'same app: two windows share one stage' $together
  Shot 'strip-2-stack.png'

  # Drag a tile to the centre: groups it with what's on stage.
  $s = State
  if ($s.active.Count -eq 0) { ClickTile $s 0; $s = State }
  $t = $s.tiles[0]
  $dragged = [long]$t.win[-1]
  $activeBefore = $s.active
  if (Drag ($t.x + 60) ($t.y + 50) 1000 500 $s.strip) {
    $s = State
    Check 'drag to centre: tile joins the stage' ($s.active -contains "$dragged") "active=$($s.active -join ',')"
    Check 'drag to centre: group keeps old windows' ($s.active -contains $activeBefore[0])
    Check 'drag to centre: both on screen' ((OnScreen $dragged) -and (OnScreen $activeBefore[0]))

    # Drag a grouped window's title bar onto the strip: leaves the group.
    Start-Sleep -Milliseconds 300
    $front = [long]$s.active[-1]
    $r = New-Object W+R; [W]::GetWindowRect([IntPtr]$front, [ref]$r) | Out-Null
    if (Drag ($r.L + 300) ($r.T + 12) 90 500 $front) {
      $s = State
      Check 'drag to strip: window leaves group' (-not ($s.active -contains "$front")) "active=$($s.active -join ',')"
      Check 'drag to strip: window off stage' (-not (OnScreen $front)) "left=$(Left $front)"
    } else { "SKIP  drag to strip: cursor not on the test window (on $([W]::RootAt($r.L + 300, $r.T + 12)), wanted $front)" }
  } else { "SKIP  drag to centre: $script:why  stripShown=$($s.shown)" }

  # Hard kill = crash. --restore must bring everything back.
  $offBefore = @($ha, $hb, $hc | Where-Object { -not (OnScreen $_) }).Count
  Check 'crash setup: some windows are off-screen' ($offBefore -ge 1)
  Stop-Process -Id $app.Id -Force
  $app = $null
  Start-Sleep -Milliseconds 300
  (Start-Process (Join-Path $root 'StageManager.exe') -ArgumentList '--restore' -PassThru -Wait) | Out-Null
  Start-Sleep -Milliseconds 400
  $offAfter = @($ha, $hb, $hc, $ha2 | Where-Object { -not (OnScreen $_) }).Count
  Check 'crash recovery: all windows back on screen' ($offAfter -eq 0) "off=$offAfter"
}
catch {
  "ERROR  $($_.Exception.Message) at line $($_.InvocationInfo.ScriptLineNumber)"
  "STATE  $script:lastState"
  $script:fails++
}
finally {
  if ($app) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue; Start-Process (Join-Path $root 'StageManager.exe') -ArgumentList '--restore' -Wait }
  foreach ($p in $procs) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
  [W]::SetCursorPos($savedCursor.X, $savedCursor.Y) | Out-Null
  Remove-Item Env:STAGE_DEBUG_FILE -ErrorAction SilentlyContinue
}
"FAILS: $script:fails"
if (Test-Path "$env:APPDATA\StageManagerWin\error.log") { "--- error.log ---"; Get-Content "$env:APPDATA\StageManagerWin\error.log" -Tail 15 }
