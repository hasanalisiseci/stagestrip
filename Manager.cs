using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace StageManagerWin
{
    // Each display has its own stage on screen, its own recents and its own strip.
    class Display
    {
        public Rectangle Bounds, Area;
        public readonly List<Stage> Stages = new List<Stage>();   // most recent first
        public Stage Active;
        public Stage Cleared;   // sent to the strip by a desktop click; the next click brings it back
        public StripForm Strip;
        public StageOverlay Overlay;
        public bool Revealed;
    }

    // Reports plain clicks on the empty desktop. Runs on its own thread so the mouse never waits
    // for the UI thread, which is busy during stage animations.
    class DesktopClickWatcher
    {
        const int WH_MOUSE_LL = 14, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202, MaxClickMove = 5;
        readonly Action<Point> onClick;
        Native.HookProc proc;   // kept in a field so the GC can't collect the callback
        IntPtr hook;
        Native.POINT down;

        public DesktopClickWatcher(Action<Point> onClick) { this.onClick = onClick; }

        public void Start()
        {
            new Thread(() =>
            {
                proc = OnMouse;
                hook = Native.SetWindowsHookEx(WH_MOUSE_LL, proc, Native.GetModuleHandle(null), 0);
                Native.MSG m;
                while (Native.GetMessage(out m, IntPtr.Zero, 0, 0) > 0) { }
            }) { IsBackground = true }.Start();
        }

        IntPtr OnMouse(int code, IntPtr w, IntPtr l)
        {
            if (code >= 0)
            {
                int message = w.ToInt32();
                if (message == WM_LBUTTONDOWN || message == WM_LBUTTONUP)
                {
                    var p = (Native.POINT)Marshal.PtrToStructure(l, typeof(Native.POINT));
                    if (message == WM_LBUTTONDOWN) down = p;
                    else if (Math.Abs(p.X - down.X) <= MaxClickMove && Math.Abs(p.Y - down.Y) <= MaxClickMove && IsDesktop(p))
                        onClick(new Point(p.X, p.Y));
                }
            }
            return Native.CallNextHookEx(hook, code, w, l);
        }

        static bool IsDesktop(Native.POINT p)
        {
            string cls = Native.ClassOf(Native.GetAncestor(Native.WindowFromPoint(p), 2));
            return cls == "Progman" || cls == "WorkerW";
        }
    }

    class Manager
    {
        const uint LVM_GETSELECTEDCOUNT = 0x1032;
        const int WPF_RESTORETOMAXIMIZED = 2, SW_SHOWNOACTIVATE = 4, SW_MAXIMIZE = 3;
        public const int StripWidth = 230;

        public readonly Settings Settings;
        readonly HashSet<string> only;
        readonly List<Display> displays = new List<Display>();
        readonly Dictionary<IntPtr, Native.RECT> moved = new Dictionary<IntPtr, Native.RECT>();
        readonly Dictionary<IntPtr, string> keys = new Dictionary<IntPtr, string>();
        readonly HashSet<IntPtr> placed = new HashSet<IntPtr>();
        readonly HashSet<IntPtr> ignored = new HashSet<IntPtr>();
        readonly HashSet<IntPtr> maximizeOnShow = new HashSet<IntPtr>();
        readonly string debugFile = Environment.GetEnvironmentVariable("STAGE_DEBUG_FILE");
        readonly SynchronizationContext ui;
        IntPtr lastForeground;
        bool syncing, movingWindow;

        static readonly HashSet<string> BlockedClasses = new HashSet<string> {
            "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "Progman", "WorkerW", "NotifyIconOverflowWindow",
            "Windows.UI.Core.CoreWindow", "XamlExplorerHostIslandWindow", "TopLevelWindowForOverflowXamlIsland",
            "Shell_InputSwitchTopLevelWindow", "ApplicationManager_ImmersiveShellWindow", "MSCTFIME UI", "IME" };

        public Manager(Settings settings, HashSet<string> only, SynchronizationContext ui)
        {
            Settings = settings;
            this.only = only;
            this.ui = ui;
        }

        bool TestMode { get { return only != null; } }

        static int OffscreenX { get { return SystemInformation.VirtualScreen.Right + 5000; } }

        // ---- Displays ----

        public void BuildDisplays()
        {
            foreach (var d in displays)
            {
                d.Strip.Close();
                d.Strip.Dispose();
                d.Overlay.Dispose();
            }
            displays.Clear();

            var areas = new List<Rectangle[]>();
            foreach (var s in Screen.AllScreens.OrderByDescending(x => x.Primary))
                areas.Add(new[] { s.Bounds, s.WorkingArea });

            // Lets the multi-display logic be tested on a machine with one monitor.
            if (Environment.GetEnvironmentVariable("STAGE_FAKE_DISPLAYS") == "2")
            {
                Rectangle b = areas[0][0], a = areas[0][1];
                areas.Clear();
                areas.Add(new[] { new Rectangle(b.X, b.Y, b.Width / 2, b.Height), new Rectangle(a.X, a.Y, a.Width / 2, a.Height) });
                areas.Add(new[] { new Rectangle(b.X + b.Width / 2, b.Y, b.Width / 2, b.Height), new Rectangle(a.X + a.Width / 2, a.Y, a.Width / 2, a.Height) });
            }

            foreach (var pair in areas)
            {
                var d = new Display { Bounds = pair[0], Area = pair[1] };
                d.Strip = new StripForm(this, d);
                d.Overlay = new StageOverlay(d.Bounds);
                displays.Add(d);
            }
        }

        public void OnDisplaysChanged()
        {
            RestoreAll();
            BuildDisplays();
            ApplyDesktopSetting();
            Sync();
        }

        Display DisplayAt(Native.RECT r)
        {
            var center = new Point(r.Left + r.Width / 2, r.Top + r.Height / 2);
            return displays.FirstOrDefault(d => d.Bounds.Contains(center));
        }

        Display DisplayOfStage(Stage s) { return displays.FirstOrDefault(d => d.Stages.Contains(s)); }

        Stage StageOf(IntPtr h)
        {
            foreach (var d in displays)
            {
                var s = d.Stages.FirstOrDefault(x => x.Windows.Contains(h));
                if (s != null) return s;
            }
            return null;
        }

        // ---- Sync: the single path that reconciles real windows with our model ----

        public void Sync()
        {
            if (syncing || !Settings.Enabled) return;
            syncing = true;
            try
            {
                RemoveDeadWindows();
                AdoptNewWindows();
                FollowForeground();
                FollowDisplayMoves();
                foreach (var d in displays)
                {
                    if (d.Active != null && d.Active.Windows.All(w => Native.IsIconic(w) || !Native.IsWindowVisible(w))) d.Active = null;
                    foreach (var s in d.Stages)
                        foreach (var w in s.Windows)
                            if (Native.IsIconic(w) && Native.IsWindowVisible(w) && !Native.IsHungAppWindow(w)) Unminimize(w);
                    d.Strip.SetActiveWindows(d.Active == null ? new IntPtr[0] : d.Active.Windows.ToArray());
                    d.Strip.SetTiles(BuildTiles(d));
                }
                PersistRecovery();
                UpdateStripVisibility();
                DumpDebug();
            }
            finally { syncing = false; }
        }

        void RemoveDeadWindows()
        {
            foreach (var h in moved.Keys.ToList())
                if (!Native.IsWindow(h)) moved.Remove(h);
            ignored.RemoveWhere(h => !Native.IsWindow(h));
            placed.RemoveWhere(h => !Native.IsWindow(h));

            foreach (var d in displays)
            {
                foreach (var s in d.Stages.ToList())
                {
                    foreach (var h in s.Windows.ToList())
                    {
                        // An app closed to the tray only hides its window. It's still running, so its
                        // tile stays; clicking it shows the window again.
                        if (Native.IsWindow(h) && !Native.IsCloaked(h)) continue;
                        s.Windows.Remove(h);
                        keys.Remove(h);
                        Native.RECT r;
                        if (Native.IsWindow(h) && moved.TryGetValue(h, out r)) { RestoreOne(h, r); moved.Remove(h); }
                    }
                    if (s.Windows.Count == 0)
                    {
                        d.Stages.Remove(s);
                        if (d.Active == s) d.Active = null;
                    }
                }
            }
        }

        void AdoptNewWindows()
        {
            IntPtr fg = Native.GetForegroundWindow();
            IntPtr fgRoot = fg == IntPtr.Zero ? fg : Native.GetAncestor(fg, Native.GA_ROOTOWNER);
            var found = new List<IntPtr>();
            Native.EnumWindows((h, l) => { if (!keys.ContainsKey(h) && !ignored.Contains(h) && IsEligible(h)) found.Add(h); return true; }, IntPtr.Zero);
            found.Reverse();   // EnumWindows is top-first; adopt bottom-first so recency matches z-order

            foreach (var h in found)
            {
                // Without administrator rights Windows refuses to move an elevated window; it stays unmanaged.
                if (!Native.SetWindowPos(h, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE))
                {
                    ignored.Add(h);
                    continue;
                }
                Native.RECT r;
                Native.GetWindowRect(h, out r);
                Display d = (Native.IsIconic(h) ? null : DisplayAt(r)) ?? displays[0];
                string key = KeyOf(h);
                keys[h] = key;
                Stage target = d.Stages.FirstOrDefault(s => s.Windows.Any(w => keys[w] == key));
                if (target == null)
                {
                    target = new Stage();
                    d.Stages.Add(target);
                }
                target.Windows.Add(h);
                if (h == fgRoot) { lastForeground = IntPtr.Zero; continue; }   // FollowForeground brings it up
                if (target == d.Active && !Settings.OneAtATime) ShowWin(h);
                else
                {
                    // Keep recency order so it doesn't displace the visible window.
                    if (target == d.Active && target.Windows.Count > 1)
                    {
                        target.Windows.Remove(h);
                        target.Windows.Insert(0, h);
                    }
                    HideWin(h);
                }
            }
        }

        bool IsEligible(IntPtr h)
        {
            if (!Native.IsWindowVisible(h) || Native.IsHungAppWindow(h) || Native.IsCloaked(h)) return false;
            int ex = Native.GetWindowLong(h, Native.GWL_EXSTYLE);
            bool app = (ex & Native.WS_EX_APPWINDOW) != 0;
            if (Native.GetWindow(h, Native.GW_OWNER) != IntPtr.Zero && !app) return false;
            if ((ex & Native.WS_EX_TOOLWINDOW) != 0 && !app) return false;
            if ((ex & Native.WS_EX_NOACTIVATE) != 0) return false;
            if ((ex & Native.WS_EX_TOPMOST) != 0 && !Settings.ManageTopmost) return false;
            if (BlockedClasses.Contains(Native.ClassOf(h))) return false;
            if (Native.TitleOf(h).Length == 0) return false;

            uint pid;
            Native.GetWindowThreadProcessId(h, out pid);
            if (pid == (uint)Process.GetCurrentProcess().Id) return false;

            Native.RECT r;
            if (!Native.GetWindowRect(h, out r) || r.Width < 100 || r.Height < 50) return false;
            if (!Native.IsIconic(h) && !moved.ContainsKey(h) && DisplayAt(r) == null) return false;
            if (IsFullscreenApp(h)) return false;

            if (only != null && !only.Contains(Path.GetFileNameWithoutExtension(Native.ProcessPathOf(h)).ToLowerInvariant())) return false;
            return true;
        }

        // Games and other borderless full-screen apps are never moved or captured, and the strip
        // stays out of their way entirely.
        bool IsFullscreenApp(IntPtr h)
        {
            const int WS_CAPTION = 0xC00000;
            if (h == IntPtr.Zero || (Native.GetWindowLong(h, Native.GWL_STYLE) & WS_CAPTION) == WS_CAPTION) return false;
            if (BlockedClasses.Contains(Native.ClassOf(h))) return false;
            Native.RECT r;
            if (!Native.GetWindowRect(h, out r)) return false;
            return displays.Any(d => r.ToRectangle().Contains(d.Bounds)) || Screen.AllScreens.Any(s => r.ToRectangle().Contains(s.Bounds));
        }

        bool FullscreenAppInFrontOn(Display d)
        {
            IntPtr fg = Native.GetForegroundWindow();
            Native.RECT r;
            return IsFullscreenApp(fg) && StageOf(fg) == null && Native.GetWindowRect(fg, out r) && r.ToRectangle().IntersectsWith(d.Bounds);
        }

        static string KeyOf(IntPtr h)
        {
            string path = Native.ProcessPathOf(h).ToLowerInvariant();
            // Store apps all share this host process, so each window counts as its own app.
            if (path.Length == 0 || path.EndsWith("applicationframehost.exe")) return "window:" + h.ToInt64();
            return path;
        }

        void FollowForeground()
        {
            IntPtr fg = Native.GetForegroundWindow();
            if (fg == lastForeground) return;
            lastForeground = fg;
            if (fg == IntPtr.Zero || displays.Any(d => d.Strip.Handle == fg)) return;
            IntPtr root = Native.GetAncestor(fg, Native.GA_ROOTOWNER);
            Stage s = StageOf(root);
            if (s == null) return;
            Display on = DisplayOfStage(s);
            if (s != on.Active || Settings.OneAtATime) Activate(s, root, null);
            else s.Touch(root);
        }

        // A window that ended up on another display (dragged, Win+Shift+arrow) joins that display.
        void FollowDisplayMoves()
        {
            if (movingWindow) return;
            foreach (var d in displays)
            {
                if (d.Active == null) continue;
                foreach (var w in VisibleOf(d.Active).ToList())
                {
                    Native.RECT r;
                    if (moved.ContainsKey(w) || Native.IsIconic(w) || !Native.GetWindowRect(w, out r)) continue;
                    Display to = DisplayAt(r);
                    if (to == null || to == d) continue;
                    Stage s;
                    if (d.Active.Windows.Count == 1)
                    {
                        s = d.Active;
                        d.Stages.Remove(s);
                        d.Active = null;
                    }
                    else
                    {
                        d.Active.Windows.Remove(w);
                        s = new Stage();
                        s.Windows.Add(w);
                    }
                    Stage displaced = to.Active;
                    to.Stages.Insert(0, s);
                    to.Active = s;
                    if (displaced != null) foreach (var o in displaced.Windows) HideWin(o);
                    return;   // lists changed; the next sync picks up anything else
                }
            }
        }

        // ---- Stage operations ----

        public void Activate(Stage stage, IntPtr front, Rectangle? fromCard)
        {
            Display d = DisplayOfStage(stage);
            if (d == null) return;
            if (front != IntPtr.Zero) stage.Touch(front);
            stage.Hidden = false;
            Stage old = d.Active;
            d.Active = stage;
            d.Cleared = null;
            d.Stages.Remove(stage);
            d.Stages.Insert(0, stage);

            var visible = VisibleOf(stage).ToList();
            var leaving = new List<IntPtr>();
            if (old != null && old != stage) leaving.AddRange(old.Windows);
            if (Settings.OneAtATime) leaving.AddRange(stage.Windows.Where(w => !visible.Contains(w)));

            Action hide = () => { foreach (var w in leaving) HideWin(w); };
            Action show = () =>
            {
                foreach (var w in visible)
                {
                    ShowWin(w);
                    Native.SetWindowPos(w, IntPtr.Zero, 0, 0, 0, 0, Native.SWP_NOMOVE | Native.SWP_NOSIZE | Native.SWP_NOACTIVATE);
                }
                Focus(stage.Last);
                lastForeground = Native.GetForegroundWindow();
            };

            var anims = Settings.Animate ? BuildAnimations(d, visible, leaving, fromCard) : new List<StageOverlay.Item>();
            if (anims.Count > 0) d.Overlay.Animate(anims, hide, show);
            else { show(); hide(); }
            PersistRecovery();
        }

        List<StageOverlay.Item> BuildAnimations(Display d, List<IntPtr> visible, List<IntPtr> leaving, Rectangle? fromCard)
        {
            var items = new List<StageOverlay.Item>();
            foreach (var w in visible)
            {
                if (!moved.ContainsKey(w) || Native.IsIconic(w)) continue;
                Rectangle to = PlannedRect(d, w).ToRectangle();
                items.Add(new StageOverlay.Item { Source = w, From = Miniature(d, to, fromCard), To = to });
            }
            foreach (var w in leaving)
            {
                Native.RECT r;
                if (moved.ContainsKey(w) || Native.IsIconic(w) || !Native.GetWindowRect(w, out r)) continue;
                Rectangle from = r.ToRectangle();
                items.Add(new StageOverlay.Item { Source = w, From = from, To = Miniature(d, from, null), Leaving = true });
            }
            return items;
        }

        // Like Cmd+H: the front app leaves the screen and the strip, and the next app takes the
        // stage. Its windows stay on the taskbar and in Alt+Tab, and activating one brings it back.
        public void HideFrontApp()
        {
            if (!Settings.Enabled) return;
            Stage s = StageOf(Native.GetAncestor(Native.GetForegroundWindow(), Native.GA_ROOTOWNER));
            Display d = s == null ? null : DisplayOfStage(s);
            if (d == null) return;
            s.Hidden = true;
            if (d.Cleared == s) d.Cleared = null;
            Stage next = d.Stages.FirstOrDefault(x => x != s && !x.Hidden);
            if (next != null) Activate(next, IntPtr.Zero, null);   // sends the old stage off-screen too
            else
            {
                d.Active = null;
                foreach (var w in s.Windows) HideWin(w);
            }
            lastForeground = Native.GetForegroundWindow();
            Sync();
        }

        public void ShowHiddenApps()
        {
            foreach (var d in displays)
                foreach (var s in d.Stages) s.Hidden = false;
            Sync();
        }

        // First click on the empty desktop sends the stage to the strip; the next one brings it back.
        void OnDesktopClick(Point p)
        {
            if (!Settings.Enabled) return;
            Display d = displays.FirstOrDefault(x => x.Bounds.Contains(p));
            if (d == null) return;

            // Clicking an icon selects it (the click already reached the desktop), so that's not "empty".
            IntPtr list = FindDesktopList();
            IntPtr selected;
            if (list != IntPtr.Zero && Native.IsWindowVisible(list) &&
                Native.SendMessageTimeout(list, LVM_GETSELECTEDCOUNT, IntPtr.Zero, IntPtr.Zero, 2, 50, out selected) != IntPtr.Zero &&
                selected != IntPtr.Zero) return;

            if (d.Active != null)
            {
                Stage stage = d.Active;
                d.Active = null;
                d.Cleared = stage;
                var leaving = stage.Windows.ToList();
                Action hide = () => { foreach (var w in leaving) HideWin(w); };
                var anims = Settings.Animate ? BuildAnimations(d, new List<IntPtr>(), leaving, null) : new List<StageOverlay.Item>();
                if (anims.Count > 0) d.Overlay.Animate(anims, hide, () => { });
                else hide();
                PersistRecovery();
            }
            else if (d.Cleared != null && d.Stages.Contains(d.Cleared))
                Activate(d.Cleared, IntPtr.Zero, null);
            Sync();
        }

        // Where a window of this size looks like on the strip: inside its card, or mid-strip.
        static Rectangle Miniature(Display d, Rectangle window, Rectangle? card)
        {
            Rectangle box = card ?? new Rectangle(d.Area.Left + 10, d.Area.Top + d.Area.Height / 2 - 60, 170, 120);
            double scale = Math.Min((double)box.Width / Math.Max(1, window.Width), (double)box.Height / Math.Max(1, window.Height));
            int w = (int)(window.Width * scale), h = (int)(window.Height * scale);
            return new Rectangle(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
        }

        IEnumerable<IntPtr> VisibleOf(Stage s)
        {
            return Settings.OneAtATime ? new[] { s.Last } : s.Windows.ToArray();
        }

        // Adds every window of a stage to the one on screen (drag to the centre / Shift+click).
        public void MergeIntoActive(Display d, Tile tile)
        {
            if (d.Active == null || tile.Stage == d.Active) { ActivateTile(d, tile); return; }
            d.Stages.Remove(tile.Stage);
            foreach (var w in tile.Stage.Windows)
            {
                d.Active.Windows.Add(w);
                if (!Settings.OneAtATime) ShowWin(w);
            }
            Activate(d.Active, tile.Stage.Last, null);
            Sync();
        }

        public void ActivateTile(Display d, Tile tile)
        {
            Rectangle card = tile.Card;
            card.Offset(d.Strip.Left, d.Strip.Top);
            Activate(tile.Stage, tile.Windows[tile.Windows.Count - 1], card);
            Sync();
        }

        public void Ungroup(Display d, Tile tile)
        {
            // Each app gets its own stage back; the stage's first app keeps the existing one.
            var byKey = tile.Stage.Windows.GroupBy(w => keys[w]).ToList();
            foreach (var g in byKey.Skip(1))
            {
                var s = new Stage();
                s.Windows.AddRange(g);
                foreach (var w in g) { tile.Stage.Windows.Remove(w); HideWin(w); }
                d.Stages.Add(s);
            }
            Sync();
        }

        public void CloseStage(Tile tile)
        {
            foreach (var w in tile.Windows) Native.PostMessage(w, Native.WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        }

        void Detach(Display d, IntPtr h)
        {
            d.Active.Windows.Remove(h);
            var s = new Stage();
            s.Windows.Add(h);
            d.Stages.Insert(d.Stages.IndexOf(d.Active) + 1, s);
            placed.Remove(h);
            HideWin(h);
            if (Settings.OneAtATime) ShowWin(d.Active.Last);
            Focus(d.Active.Last);
            lastForeground = Native.GetForegroundWindow();
        }

        // ---- Moving real windows ----

        void HideWin(IntPtr h)
        {
            foreach (var owned in OwnedBy(h)) MoveOff(owned);
            MoveOff(h);
        }

        void MoveOff(IntPtr h)
        {
            if (moved.ContainsKey(h) || Native.IsIconic(h)) return;
            Native.RECT r;
            if (!Native.GetWindowRect(h, out r)) return;
            moved[h] = r;
            Native.SetWindowPos(h, IntPtr.Zero, OffscreenX, r.Top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_ASYNC);
        }

        // Minimized windows have no image to preview, so a minimized window is instead restored
        // directly off-screen, without a flash and without taking focus. It stays on the taskbar
        // and in the strip with a live preview, and returns to where it was.
        void Unminimize(IntPtr h)
        {
            var p = new Native.WINDOWPLACEMENT();
            p.length = Marshal.SizeOf(p);
            if (!Native.GetWindowPlacement(h, ref p)) return;
            if (!moved.ContainsKey(h)) moved[h] = p.rcNormal;
            if ((p.flags & WPF_RESTORETOMAXIMIZED) != 0)
            {
                maximizeOnShow.Add(h);
                p.flags &= ~WPF_RESTORETOMAXIMIZED;
            }
            int width = p.rcNormal.Width, height = p.rcNormal.Height;
            p.rcNormal = new Native.RECT(OffscreenX, p.rcNormal.Top, OffscreenX + width, p.rcNormal.Top + height);
            p.showCmd = SW_SHOWNOACTIVATE;
            Native.SetWindowPlacement(h, ref p);
        }

        void ShowWin(IntPtr h)
        {
            if (Native.IsIconic(h)) Native.ShowWindow(h, Native.SW_RESTORE);
            if (!Native.IsWindowVisible(h)) Native.ShowWindow(h, Native.SW_SHOW);
            Stage stage = StageOf(h);
            Display d = stage == null ? displays[0] : DisplayOfStage(stage);
            Native.RECT to = PlannedRect(d, h);
            placed.Add(h);
            moved.Remove(h);
            bool maximize = maximizeOnShow.Remove(h);
            // Must land before maximizing, so the window maximizes on the right display.
            Native.SetWindowPos(h, IntPtr.Zero, to.Left, to.Top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | (maximize ? 0 : Native.SWP_ASYNC));
            if (maximize) Native.ShowWindow(h, SW_MAXIMIZE);
            foreach (var k in moved.Keys.ToList())
            {
                if (Native.GetWindow(k, Native.GW_OWNER) != h) continue;
                Native.RECT o = moved[k];
                moved.Remove(k);
                Native.SetWindowPos(k, IntPtr.Zero, o.Left, o.Top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_ASYNC);
            }
        }

        // Where a window goes when it comes on stage. The first time, one that would sit under
        // the strip is centred in the free space if it fits. Bigger or maximized windows stay
        // put; the strip hides itself while they overlap it.
        Native.RECT PlannedRect(Display d, IntPtr h)
        {
            Native.RECT r;
            if (!moved.TryGetValue(h, out r)) Native.GetWindowRect(h, out r);
            if (placed.Contains(h) || (Native.GetWindowLong(h, Native.GWL_STYLE) & Native.WS_MAXIMIZE) != 0) return r;
            int free = d.Area.Width - StripWidth;
            if (r.Left >= d.Area.Left + StripWidth || r.Width > free) return r;
            int x = d.Area.Left + StripWidth + (free - r.Width) / 2;
            int y = Math.Max(d.Area.Top, d.Area.Top + (d.Area.Height - r.Height) / 2);
            return new Native.RECT(x, y, x + r.Width, y + r.Height);
        }

        static List<IntPtr> OwnedBy(IntPtr owner)
        {
            var list = new List<IntPtr>();
            Native.EnumWindows((h, l) => { if (Native.GetWindow(h, Native.GW_OWNER) == owner && Native.IsWindowVisible(h)) list.Add(h); return true; }, IntPtr.Zero);
            return list;
        }

        static void Focus(IntPtr h)
        {
            if (Native.SetForegroundWindow(h) && Native.GetForegroundWindow() == h) return;
            uint pid;
            uint mine = Native.GetCurrentThreadId();
            uint theirs = Native.GetWindowThreadProcessId(Native.GetForegroundWindow(), out pid);
            bool attached = theirs != 0 && theirs != mine && Native.AttachThreadInput(mine, theirs, true);
            Native.SetForegroundWindow(h);
            if (attached) Native.AttachThreadInput(mine, theirs, false);
        }

        static void RestoreOne(IntPtr h, Native.RECT r)
        {
            if (Native.IsIconic(h))
            {
                // Put the remembered position back without un-minimizing the window.
                var p = new Native.WINDOWPLACEMENT();
                p.length = Marshal.SizeOf(p);
                if (!Native.GetWindowPlacement(h, ref p)) return;
                p.rcNormal = r;
                p.showCmd = Native.SW_SHOWMINNOACTIVE;
                Native.SetWindowPlacement(h, ref p);
                return;
            }
            Native.SetWindowPos(h, IntPtr.Zero, r.Left, r.Top, 0, 0, Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE | Native.SWP_ASYNC);
        }

        public void RestoreAll()
        {
            foreach (var pair in moved) if (Native.IsWindow(pair.Key)) RestoreOne(pair.Key, pair.Value);
            moved.Clear();
            foreach (var h in maximizeOnShow) if (Native.IsWindow(h)) Native.ShowWindow(h, SW_MAXIMIZE);
            maximizeOnShow.Clear();
            foreach (var d in displays)
            {
                d.Stages.Clear();
                d.Active = null;
                d.Cleared = null;
                d.Strip.SetTiles(new List<Tile>());
            }
            keys.Clear();
            placed.Clear();
            lastForeground = IntPtr.Zero;
            PersistRecovery();
            SetDesktopIcons(true);
        }

        // ---- Crash recovery ----

        string lastPersisted;

        void PersistRecovery()
        {
            string text = string.Join("\n", moved.Select(p => p.Key.ToInt64() + "," + p.Value.Left + "," + p.Value.Top));
            if (text == lastPersisted) return;
            lastPersisted = text;
            try { File.WriteAllText(Settings.RecoveryPath, text); }
            catch (IOException) { }
        }

        public static void RecoverFromFile()
        {
            try
            {
                if (File.Exists(Settings.RecoveryPath))
                {
                    int limit = SystemInformation.VirtualScreen.Right;
                    foreach (var line in File.ReadAllLines(Settings.RecoveryPath))
                    {
                        var p = line.Split(',');
                        if (p.Length != 3) continue;
                        var h = new IntPtr(long.Parse(p[0]));
                        int x = int.Parse(p[1]), y = int.Parse(p[2]);
                        Native.RECT r;
                        if (Native.IsWindow(h) && Native.GetWindowRect(h, out r) && (r.Left >= limit || Native.IsIconic(h)))
                            RestoreOne(h, new Native.RECT(x, y, x + r.Width, y + r.Height));
                    }
                    File.Delete(Settings.RecoveryPath);
                }
            }
            catch (IOException) { }
            catch (FormatException) { }
            SetDesktopIcons(true);
        }

        // ---- Desktop icons ----

        static IntPtr FindDesktopList()
        {
            IntPtr defView = Native.FindWindowEx(Native.FindWindowEx(IntPtr.Zero, IntPtr.Zero, "Progman", null), IntPtr.Zero, "SHELLDLL_DefView", null);
            IntPtr worker = IntPtr.Zero;
            while (defView == IntPtr.Zero && (worker = Native.FindWindowEx(IntPtr.Zero, worker, "WorkerW", null)) != IntPtr.Zero)
                defView = Native.FindWindowEx(worker, IntPtr.Zero, "SHELLDLL_DefView", null);
            return defView == IntPtr.Zero ? IntPtr.Zero : Native.FindWindowEx(defView, IntPtr.Zero, "SysListView32", null);
        }

        static void SetDesktopIcons(bool show)
        {
            IntPtr list = FindDesktopList();
            if (list != IntPtr.Zero) Native.ShowWindow(list, show ? Native.SW_SHOW : Native.SW_HIDE);
        }

        public void ApplyDesktopSetting()
        {
            if (!TestMode) SetDesktopIcons(!Settings.Enabled || Settings.ShowDesktopItems);
        }

        // ---- Strip content and visibility ----

        List<Tile> BuildTiles(Display d)
        {
            var tiles = new List<Tile>();
            // In one-at-a-time mode the on-stage app's other windows wait in the strip.
            if (d.Active != null && Settings.OneAtATime && d.Active.Windows.Count > 1)
                tiles.Add(new Tile { Stage = d.Active, Windows = d.Active.Windows.Where(w => w != d.Active.Last).ToList() });
            foreach (var s in d.Stages)
                if (s != d.Active && !s.Hidden) tiles.Add(new Tile { Stage = s, Windows = s.Windows.ToList() });
            return tiles;   // the strip shows as many as its display's height allows
        }

        // Fast check, run often: a strip steps aside for windows that overlap it and comes
        // back while the cursor rests on that display's left edge.
        public void UpdateStripVisibility()
        {
            foreach (var d in displays)
            {
                if (!Settings.Enabled || FullscreenAppInFrontOn(d)) { d.Revealed = false; d.Strip.SetShown(false); continue; }
                Point c = Cursor.Position;
                bool onThisDisplay = c.Y >= d.Bounds.Top && c.Y < d.Bounds.Bottom && c.X >= d.Bounds.Left && c.X < d.Bounds.Right;
                if (onThisDisplay && c.X <= d.Bounds.Left + 1) d.Revealed = true;
                else if ((!onThisDisplay || c.X > d.Area.Left + StripWidth + 20) && !d.Strip.Dragging) d.Revealed = false;

                bool show = d.Strip.HasTiles && (d.Revealed || d.Strip.Dragging || movingWindow || (Settings.ShowRecent && !ActiveOverlapsStrip(d)));
                d.Strip.SetShown(show);
            }
        }

        bool ActiveOverlapsStrip(Display d)
        {
            if (d.Active == null) return false;
            var strip = new Rectangle(d.Area.Left, d.Area.Top, StripWidth, d.Area.Height);
            foreach (var w in VisibleOf(d.Active))
            {
                Native.RECT r;
                if (!Native.IsIconic(w) && Native.GetWindowRect(w, out r) && r.ToRectangle().IntersectsWith(strip)) return true;
            }
            return false;
        }

        // ---- System events ----

        Native.WinEventProc hookProc;   // kept in a field so the GC can't collect the callback
        readonly List<IntPtr> hooks = new List<IntPtr>();
        DesktopClickWatcher desktopClicks;

        public void InstallHooks()
        {
            desktopClicks = new DesktopClickWatcher(p => ui.Post(_ => OnDesktopClick(p), null));
            desktopClicks.Start();
            hookProc = OnWinEvent;
            foreach (var ev in new[] { Native.EVENT_FOREGROUND, Native.EVENT_MOVESIZESTART, Native.EVENT_MOVESIZEEND, Native.EVENT_MINIMIZESTART, Native.EVENT_MINIMIZEEND })
                hooks.Add(Native.SetWinEventHook(ev, ev, IntPtr.Zero, hookProc, 0, 0, 2));
        }

        public void RemoveHooks()
        {
            foreach (var h in hooks) Native.UnhookWinEvent(h);
            hooks.Clear();
        }

        void OnWinEvent(IntPtr hook, uint ev, IntPtr h, int obj, int child, uint thread, uint time)
        {
            if (ev == Native.EVENT_MOVESIZESTART) movingWindow = true;
            if (ev == Native.EVENT_MOVESIZEEND)
            {
                movingWindow = false;
                // Dropping a grouped window onto the strip takes it out of the group.
                Point c = Cursor.Position;
                Stage s = StageOf(h);
                Display d = s == null ? null : DisplayOfStage(s);
                if (d != null && s == d.Active && s.Windows.Count > 1 && d.Bounds.Contains(c) && c.X < d.Area.Left + StripWidth)
                    Detach(d, h);
            }
            ui.Post(_ => Sync(), null);
        }

        public void SetEnabled(bool on)
        {
            Settings.Enabled = on;
            Settings.Save();
            if (!on) { RestoreAll(); UpdateStripVisibility(); }
            ApplyDesktopSetting();
            Sync();
        }

        public void SetOneAtATime(bool on)
        {
            Settings.OneAtATime = on;
            Settings.Save();
            foreach (var d in displays)
            {
                if (d.Active == null) continue;
                Activate(d.Active, d.Active.Last, null);
                if (!on) foreach (var w in d.Active.Windows) ShowWin(w);
            }
            Sync();
        }

        public void SetManageTopmost(bool on)
        {
            Settings.ManageTopmost = on;
            Settings.Save();
            // Start over, so windows that just became (un)managed are sorted out cleanly.
            RestoreAll();
            ApplyDesktopSetting();
            Sync();
        }

        public void SetTilt(bool on)
        {
            Settings.Tilt = on;
            Settings.Save();
            foreach (var d in displays) d.Strip.OnTiltChanged();
            Sync();
        }

        void DumpDebug()
        {
            if (debugFile == null) return;
            var b = new StringBuilder();
            foreach (var d in displays)
            {
                b.AppendLine("display=" + d.Bounds.X + "," + d.Bounds.Width);
                b.AppendLine("strip=" + d.Strip.Handle.ToInt64() + " shown=" + d.Strip.Visible);
                b.AppendLine("active=" + (d.Active == null ? "" : string.Join(",", d.Active.Windows.Select(w => w.ToInt64()))));
                foreach (var t in d.Strip.Tiles)
                    b.AppendLine("tile=" + t.Card.X + "," + t.Card.Y + "," + t.Card.Width + "," + t.Card.Height + " windows=" + string.Join(",", t.Windows.Select(w => w.ToInt64())));
            }
            b.AppendLine("moved=" + moved.Count);
            try { File.WriteAllText(debugFile, b.ToString()); }
            catch (IOException) { }
        }
    }
}
