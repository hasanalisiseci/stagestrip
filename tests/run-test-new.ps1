# Third end-to-end test, throwaway windows only: per-display stages (the screen is split in two
# with STAGE_FAKE_DISPLAYS), always-on-top windows, animation frames, tilted cards, drag ghost
# and graceful --quit. Moves the real mouse during the ghost screenshot.
param([string]$ShotDir = $env:TEMP)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$bin = Join-Path $PSScriptRoot 'bin'
$debug = Join-Path $ShotDir 'stage-state.txt'
$exe = Join-Path $root 'StageManager.exe'

Add-Type -AssemblyName System.Windows.Forms, System.Drawing
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class W {
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L,T,Ri,B; }
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr h, IntPtr a, int x, int y, int cx, int cy, uint f);
  [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f, int x, int y, int d, IntPtr e);
  [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
  [StructLayout(LayoutKind.Sequential)] public struct P { public int X, Y; }
  [DllImport("user32.dll")] static extern IntPtr WindowFromPoint(P p);
  [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr h, uint f);
  public static long RootAt(int x, int y) { P p; p.X = x; p.Y = y; return GetAncestor(WindowFromPoint(p), 2).ToInt64(); }
}
'@
[W]::SetProcessDPIAware() | Out-Null

$script:fails = 0
function Check($name, $ok, $detail = '') { if ($ok) { "PASS  $name" } else { "FAIL  $name  $detail"; $script:fails++ } }
function Left($h) { $r = New-Object W+R; [W]::GetWindowRect([IntPtr][long]$h, [ref]$r) | Out-Null; $r.L }
function OnScreen($h) { (Left $h) -lt 1920 }
function Displays {
  Start-Sleep -Milliseconds 900
  $list = @(); $cur = $null
  # The app rewrites this file constantly; only trust a complete copy ("moved=" is its last line).
  $lines = @()
  foreach ($try in 1..20) {
    try { $lines = @(Get-Content $debug -ErrorAction Stop) } catch { $lines = @() }
    if ($lines.Count -gt 0 -and $lines[-1] -like 'moved=*') { break }
    Start-Sleep -Milliseconds 50
  }
  foreach ($l in $lines) {
    if ($l -match '^display=(-?\d+),(\d+)') { $cur = @{ x = [int]$Matches[1]; tiles = @(); active = @() }; $list += ,$cur }
    elseif ($l -match '^strip=(\d+) shown=(\w+)') { $cur.strip = [long]$Matches[1]; $cur.shown = $Matches[2] }
    elseif ($l -match '^active=(.*)') { $cur.active = @($Matches[1] -split ',' | Where-Object { $_ }) }
    elseif ($l -match '^tile=(\d+),(\d+),(\d+),(\d+) windows=(.*)') {
      $cur.tiles += ,@{ x = [int]$Matches[1]; y = [int]$Matches[2]; win = @($Matches[5] -split ',') } }
  }
  ,$list
}
function ClickTileWith($d, $h) {
  if ($d.active -contains "$h") { return }   # already on stage, e.g. it had the focus at startup
  foreach ($t in $d.tiles) {
    if ($t.win -contains "$h") {
      $lp = [IntPtr](($t.y + 40) * 65536 + $t.x + 40)
      [W]::PostMessage([IntPtr]$d.strip, 0x201, [IntPtr]1, $lp) | Out-Null
      [W]::PostMessage([IntPtr]$d.strip, 0x202, [IntPtr]0, $lp) | Out-Null
      return
    }
  }
  throw "no tile for $h"
}
function Shot($name, $w, $h) {
  $b = New-Object Drawing.Bitmap $w, $h
  $g = [Drawing.Graphics]::FromImage($b)
  $g.CopyFromScreen(0, 0, 0, 0, $b.Size)
  $b.Save((Join-Path $ShotDir $name)); $g.Dispose(); $b.Dispose()
}
function Launch($n) { $p = Start-Process (Join-Path $bin "$n.exe") -PassThru; $p.WaitForInputIdle(3000) | Out-Null; Start-Sleep -Milliseconds 300; $p.Refresh(); $p }
function StartApp { $env:STAGE_DEBUG_FILE = $debug; Start-Process $exe -ArgumentList '--only', 'StageTestA,StageTestB,StageTestC' -PassThru }
function QuitApp($a) { Start-Process $exe -ArgumentList '--quit' -Wait; if (-not $a.WaitForExit(4000)) { Stop-Process -Id $a.Id -Force; Start-Process $exe -ArgumentList '--restore' -Wait; return $false }; Start-Sleep -Milliseconds 300; $true }

$savedCursor = [Windows.Forms.Cursor]::Position
$procs = @(); $app = $null
try {
  [W]::SetCursorPos(1000, 500) | Out-Null
  $a = Launch 'StageTestA'; $b = Launch 'StageTestB'; $c = Launch 'StageTestC'; $procs += $a, $b, $c
  $ha = $a.MainWindowHandle.ToInt64(); $hb = $b.MainWindowHandle.ToInt64(); $hc = $c.MainWindowHandle.ToInt64()

  # ---- Two displays: A and C start on the left half, B on the right ----
  [W]::SetWindowPos([IntPtr]$hb, [IntPtr]::Zero, 1100, 100, 600, 400, 0x14) | Out-Null
  [W]::SetWindowPos([IntPtr]$ha, [IntPtr]::Zero, 250, 100, 600, 400, 0x14) | Out-Null
  [W]::SetWindowPos([IntPtr]$hc, [IntPtr]::Zero, 250, 100, 600, 400, 0x14) | Out-Null
  $env:STAGE_FAKE_DISPLAYS = '2'
  $app = StartApp
  Start-Sleep -Milliseconds 800
  $ds = Displays
  Check 'displays: two strips exist' ($ds.Count -eq 2) "count=$($ds.Count)"
  ClickTileWith $ds[1] $hb
  $ds = Displays
  ClickTileWith $ds[0] $ha
  $ds = Displays
  Check 'displays: left has its own stage' ($ds[0].active -contains "$ha") "left=$($ds[0].active -join ',')"
  Check 'displays: right has its own stage' ($ds[1].active -contains "$hb") "right=$($ds[1].active -join ',')"
  Check 'displays: both stages on screen together' ((OnScreen $ha) -and (OnScreen $hb))
  Check 'displays: C waits only on the left strip' ((@($ds[0].tiles | Where-Object { $_.win -contains "$hc" }).Count -eq 1) -and (@($ds[1].tiles).Count -eq 0))

  # A window moved across joins the other display's stage.
  [W]::SetWindowPos([IntPtr]$ha, [IntPtr]::Zero, 1150, 200, 0, 0, 0x15) | Out-Null
  $ds = Displays
  Check 'displays: moved window joins that display' ($ds[1].active -contains "$ha") "right=$($ds[1].active -join ',')"
  Check 'displays: it displaces the stage there' (-not (OnScreen $hb)) "left=$(Left $hb)"
  Check 'quit: exits by itself' (QuitApp $app)
  $app = $null
  Remove-Item Env:STAGE_FAKE_DISPLAYS
  $off = @($ha, $hb, $hc | Where-Object { -not (OnScreen $_) }).Count
  Check 'quit: every window back on screen' ($off -eq 0) "off=$off"

  # ---- Always-on-top window takes part ----
  [W]::SetWindowPos([IntPtr]$hc, [IntPtr](-1), 300, 100, 0, 0, 0x13) | Out-Null
  $app = StartApp
  Start-Sleep -Milliseconds 800
  $ds = Displays
  ClickTileWith $ds[0] $ha
  $ds = Displays
  Check 'topmost: always-on-top window leaves the stage' (-not (OnScreen $hc)) "left=$(Left $hc)"
  Check 'topmost: it has a tile' (@($ds[0].tiles | Where-Object { $_.win -contains "$hc" }).Count -eq 1)
  ClickTileWith $ds[0] $hc
  $ds = Displays
  Check 'topmost: click brings it back' (OnScreen $hc)

  # ---- Tilted cards: give the capture thread time, then look ----
  Start-Sleep -Seconds 3
  Shot 'new-tilted.png' 220 1040

  # ---- Drag ghost: screenshot mid-drag, then let go on the strip (no merge) ----
  $ds = Displays
  $t = $ds[0].tiles[0]
  [W]::SetCursorPos($t.x + 60, $t.y + 50) | Out-Null; Start-Sleep -Milliseconds 150
  # Never press unless the cursor is really on the strip: someone else may be using the mouse.
  if ([Windows.Forms.Cursor]::Position.X -eq ($t.x + 60) -and [W]::RootAt($t.x + 60, $t.y + 50) -eq $ds[0].strip) {
    [W]::mouse_event(2, 0, 0, 0, [IntPtr]::Zero); Start-Sleep -Milliseconds 100
    foreach ($i in 1..8) { [W]::SetCursorPos(($t.x + 60) + 25 * $i, $t.y + 50) | Out-Null; Start-Sleep -Milliseconds 40 }
    Start-Sleep -Milliseconds 200
    Shot 'new-ghost.png' 220 1040
    [W]::SetCursorPos($t.x + 60, $t.y + 50) | Out-Null; Start-Sleep -Milliseconds 100
    [W]::mouse_event(4, 0, 0, 0, [IntPtr]::Zero)
  } else { 'SKIP  ghost screenshot: cursor not on the strip' }
  Check 'quit: exits by itself (2)' (QuitApp $app)
  $app = $null

  # ---- Animation: slowed down so a mid-flight frame can be photographed ----
  $env:STAGE_ANIM_MS = '2500'
  [W]::SetWindowPos([IntPtr]$hc, [IntPtr](-2), 0, 0, 0, 0, 0x13) | Out-Null
  $app = StartApp
  Start-Sleep -Milliseconds 800
  $ds = Displays
  ClickTileWith $ds[0] $ha
  Start-Sleep -Seconds 4
  $ds = Displays
  ClickTileWith $ds[0] $hb
  Start-Sleep -Milliseconds 500
  Shot 'new-anim.png' 220 1040   # strip only: the area around it is the user's own desktop
  Start-Sleep -Seconds 3
  Check 'animation: window lands on stage afterwards' (OnScreen $hb) "left=$(Left $hb)"
  Check 'quit: exits by itself (3)' (QuitApp $app)
  $app = $null
}
finally {
  if ($app) { Stop-Process -Id $app.Id -Force -ErrorAction SilentlyContinue; Start-Process $exe -ArgumentList '--restore' -Wait }
  foreach ($p in $procs) { Stop-Process -Id $p.Id -Force -ErrorAction SilentlyContinue }
  [W]::SetCursorPos($savedCursor.X, $savedCursor.Y) | Out-Null
  Remove-Item Env:STAGE_DEBUG_FILE, Env:STAGE_FAKE_DISPLAYS, Env:STAGE_ANIM_MS -ErrorAction SilentlyContinue
}
"FAILS: $script:fails"
if (Test-Path "$env:APPDATA\StageManagerWin\error.log") { "--- error.log ---"; Get-Content "$env:APPDATA\StageManagerWin\error.log" -Tail 15 }
