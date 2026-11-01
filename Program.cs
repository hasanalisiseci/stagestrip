using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Security.Principal;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace StageManagerWin
{
    // Receives the global hotkey.
    class HotkeyWindow : NativeWindow
    {
        const int WM_HOTKEY = 0x312;
        const uint MOD_ALT = 1, MOD_CONTROL = 2, MOD_SHIFT = 4;
        readonly Action onToggle, onHide;

        public HotkeyWindow(Action onToggle, Action onHide)
        {
            this.onToggle = onToggle;
            this.onHide = onHide;
            CreateHandle(new CreateParams());
            Native.RegisterHotKey(Handle, 1, MOD_CONTROL | MOD_ALT | MOD_SHIFT, (uint)Keys.S);
            Native.RegisterHotKey(Handle, 2, MOD_CONTROL | MOD_ALT, (uint)Keys.H);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_HOTKEY)
            {
                if (m.WParam.ToInt32() == 1) onToggle();
                else onHide();
            }
            base.WndProc(ref m);
        }

        public void Release()
        {
            Native.UnregisterHotKey(Handle, 1);
            Native.UnregisterHotKey(Handle, 2);
            DestroyHandle();
        }
    }

    static class Program
    {
        const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run", RunName = "StageManagerWin";
        const string QuitEventName = @"Local\StageManagerWin.Quit";

        static Manager manager;
        static int cleanedUp;

        static void CleanUp()
        {
            if (Interlocked.Exchange(ref cleanedUp, 1) == 0 && manager != null) manager.RestoreAll();
        }

        static void LogAndExit(object error)
        {
            try { File.AppendAllText(Settings.LogPath, DateTime.Now + " " + error + Environment.NewLine); }
            catch (IOException) { }
            CleanUp();
            Environment.Exit(1);
        }

        static bool IsElevated()
        {
            using (var id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        [STAThread]
        static int Main(string[] args)
        {
            Native.SetProcessDPIAware();

            if (args.Contains("--quit"))
            {
                EventWaitHandle running;
                if (EventWaitHandle.TryOpenExisting(QuitEventName, out running)) using (running) running.Set();
                return 0;
            }

            // After a restart as administrator, wait for the old copy to finish putting windows back.
            int after = Array.IndexOf(args, "--after-pid");
            if (after >= 0 && after + 1 < args.Length)
            {
                try { using (var old = Process.GetProcessById(int.Parse(args[after + 1]))) old.WaitForExit(5000); }
                catch (ArgumentException) { }
                catch (FormatException) { }
            }

            bool isNew;
            using (var mutex = new Mutex(true, @"Local\StageManagerWin", out isNew))
            using (var quit = new EventWaitHandle(false, EventResetMode.ManualReset, QuitEventName))
            {
                if (!isNew)
                {
                    MessageBox.Show("Sahne Yöneticisi zaten çalışıyor. Simgesini görev çubuğunun bildirim alanında bulabilirsiniz.", "Sahne Yöneticisi");
                    return 0;
                }

                // Whatever a previous run left behind goes back first.
                Manager.RecoverFromFile();
                if (args.Contains("--restore")) return 0;

                HashSet<string> only = null;
                int o = Array.IndexOf(args, "--only");
                if (o >= 0 && o + 1 < args.Length)
                    only = new HashSet<string>(args[o + 1].ToLowerInvariant().Split(','));

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
                Application.ThreadException += (s, e) => LogAndExit(e.Exception);
                AppDomain.CurrentDomain.UnhandledException += (s, e) => LogAndExit(e.ExceptionObject);

                var ui = new WindowsFormsSynchronizationContext();
                SynchronizationContext.SetSynchronizationContext(ui);

                manager = new Manager(Settings.Load(), only, ui);
                manager.BuildDisplays();
                var hotkey = new HotkeyWindow(() => manager.SetEnabled(!manager.Settings.Enabled), () => manager.HideFrontApp());

                using (var tray = BuildTray())
                using (var fast = new System.Windows.Forms.Timer { Interval = 100 })
                using (var slow = new System.Windows.Forms.Timer { Interval = 500 })
                {
                    fast.Tick += (s, e) =>
                    {
                        manager.UpdateStripVisibility();
                        if (quit.WaitOne(0)) Application.Exit();
                    };
                    slow.Tick += (s, e) => manager.Sync();
                    Application.ApplicationExit += (s, e) => CleanUp();
                    SystemEvents.SessionEnding += (s, e) => CleanUp();
                    SystemEvents.DisplaySettingsChanged += (s, e) => ui.Post(_ => manager.OnDisplaysChanged(), null);

                    manager.InstallHooks();
                    manager.ApplyDesktopSetting();
                    manager.Sync();
                    fast.Start();
                    slow.Start();

                    Application.Run();

                    manager.RemoveHooks();
                    hotkey.Release();
                }
                CleanUp();
            }
            return 0;
        }

        // Windows won't let an ordinary app move an elevated window, so managing those
        // requires this app to be elevated too. Stays on the old copy if UAC is declined.
        static void RestartAsAdmin()
        {
            try
            {
                Process.Start(new ProcessStartInfo(Application.ExecutablePath)
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    Arguments = "--after-pid " + Process.GetCurrentProcess().Id
                });
                Application.Exit();
            }
            catch (Win32Exception) { }
        }

        static NotifyIcon BuildTray()
        {
            var menu = new ContextMenuStrip();
            var enabled = new ToolStripMenuItem("Sahne Yöneticisi") { CheckOnClick = true };
            var recent = new ToolStripMenuItem("Son kullanılan uygulamaları göster") { CheckOnClick = true };
            var desktop = new ToolStripMenuItem("Masaüstü öğelerini göster") { CheckOnClick = true };
            var all = new ToolStripMenuItem("Pencereleri tümü bir arada göster");
            var one = new ToolStripMenuItem("Pencereleri tek tek göster");
            var animate = new ToolStripMenuItem("Geçiş animasyonu") { CheckOnClick = true };
            var tilt = new ToolStripMenuItem("Eğik önizlemeler (yaklaşık 1 kare/sn)") { CheckOnClick = true };
            var topmost = new ToolStripMenuItem("Her zaman üstte kalan pencereleri de yönet") { CheckOnClick = true };
            var autostart = new ToolStripMenuItem("Windows ile başlat") { CheckOnClick = true };

            Action refresh = () =>
            {
                var s = manager.Settings;
                enabled.Checked = s.Enabled;
                recent.Checked = s.ShowRecent;
                desktop.Checked = s.ShowDesktopItems;
                all.Checked = !s.OneAtATime;
                one.Checked = s.OneAtATime;
                animate.Checked = s.Animate;
                tilt.Checked = s.Tilt;
                topmost.Checked = s.ManageTopmost;
                autostart.Checked = IsAutostart();
            };
            refresh();
            menu.Opening += (s, e) => refresh();

            enabled.Click += (s, e) => manager.SetEnabled(enabled.Checked);
            recent.Click += (s, e) => { manager.Settings.ShowRecent = recent.Checked; manager.Settings.Save(); manager.UpdateStripVisibility(); };
            desktop.Click += (s, e) => { manager.Settings.ShowDesktopItems = desktop.Checked; manager.Settings.Save(); manager.ApplyDesktopSetting(); };
            all.Click += (s, e) => manager.SetOneAtATime(false);
            one.Click += (s, e) => manager.SetOneAtATime(true);
            animate.Click += (s, e) => { manager.Settings.Animate = animate.Checked; manager.Settings.Save(); };
            tilt.Click += (s, e) => manager.SetTilt(tilt.Checked);
            topmost.Click += (s, e) => manager.SetManageTopmost(topmost.Checked);
            autostart.Click += (s, e) => SetAutostart(autostart.Checked);

            menu.Items.Add(enabled);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(recent);
            menu.Items.Add(desktop);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(all);
            menu.Items.Add(one);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(animate);
            menu.Items.Add(tilt);
            menu.Items.Add(topmost);
            menu.Items.Add("Gizlenen uygulamaları göster (gizlemek için Ctrl+Alt+H)", null, (s, e) => manager.ShowHiddenApps());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(autostart);
            if (!IsElevated()) menu.Items.Add("Yönetici olarak yeniden başlat", null, (s, e) => RestartAsAdmin());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Çıkış", null, (s, e) => Application.Exit());

            return new NotifyIcon { Icon = BuildIcon(), Text = "Sahne Yöneticisi", ContextMenuStrip = menu, Visible = true };
        }

        static Icon BuildIcon()
        {
            using (var b = new Bitmap(32, 32))
            using (var g = Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent);
                using (var side = new SolidBrush(Color.FromArgb(150, 170, 200)))
                {
                    g.FillRectangle(side, 2, 6, 6, 5);
                    g.FillRectangle(side, 2, 14, 6, 5);
                    g.FillRectangle(side, 2, 22, 6, 5);
                }
                using (var main = new SolidBrush(Color.FromArgb(70, 140, 240))) g.FillRectangle(main, 11, 6, 19, 21);
                return Icon.FromHandle(b.GetHicon());
            }
        }

        static bool IsAutostart()
        {
            using (var k = Registry.CurrentUser.OpenSubKey(RunKey))
                return k != null && k.GetValue(RunName) != null;
        }

        static void SetAutostart(bool on)
        {
            using (var k = Registry.CurrentUser.CreateSubKey(RunKey))
            {
                if (on) k.SetValue(RunName, "\"" + Application.ExecutablePath + "\"");
                else k.DeleteValue(RunName, false);
            }
        }
    }
}
