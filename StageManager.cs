// Sahne Yöneticisi (Stage Manager) for Windows.
// Build with build.cmd (uses the C# 5 compiler that ships with Windows).
//
// Background windows are moved off-screen rather than minimized, because DWM only
// produces live thumbnails for windows that are not minimized. Every original position
// is written to a recovery file, so a crash can never strand a window: starting the app
// again (or running it with --restore) puts everything back.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace StageManagerWin
{
    static class Native
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public RECT(int l, int t, int r, int b) { Left = l; Top = t; Right = r; Bottom = b; }
            public int Width { get { return Right - Left; } }
            public int Height { get { return Bottom - Top; } }
            public Rectangle ToRectangle() { return Rectangle.FromLTRB(Left, Top, Right, Bottom); }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct POINT { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct SIZE { public int X, Y; }

        [StructLayout(LayoutKind.Sequential)]
        public struct WINDOWPLACEMENT
        {
            public int length, flags, showCmd;
            public POINT ptMin, ptMax;
            public RECT rcNormal;
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct DWM_THUMBNAIL_PROPERTIES
        {
            public int dwFlags;
            public RECT rcDestination, rcSource;
            public byte opacity;
            public bool fVisible, fSourceClientAreaOnly;
        }

        public delegate bool EnumProc(IntPtr h, IntPtr l);
        public delegate void WinEventProc(IntPtr hook, uint ev, IntPtr h, int obj, int child, uint thread, uint time);

        [DllImport("user32.dll")] public static extern bool SetProcessDPIAware();
        [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc p, IntPtr l);
        [DllImport("user32.dll")] public static extern bool IsWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsIconic(IntPtr h);
        [DllImport("user32.dll")] public static extern bool IsHungAppWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern IntPtr GetWindow(IntPtr h, uint cmd);
        [DllImport("user32.dll")] public static extern IntPtr GetAncestor(IntPtr h, uint flags);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")] public static extern int GetWindowLong(IntPtr h, int i);
        [DllImport("user32.dll", EntryPoint = "GetClassLongPtrW")] public static extern IntPtr GetClassLongPtr(IntPtr h, int i);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetWindowText(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern int GetClassName(IntPtr h, StringBuilder s, int n);
        [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll", SetLastError = true)] public static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr h, int cmd);
        [DllImport("user32.dll")] public static extern bool GetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
        [DllImport("user32.dll")] public static extern bool SetWindowPlacement(IntPtr h, ref WINDOWPLACEMENT p);
        [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
        [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
        [DllImport("user32.dll")] public static extern bool AttachThreadInput(uint a, uint b, bool attach);
        [DllImport("user32.dll")] public static extern bool PostMessage(IntPtr h, uint m, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern IntPtr SendMessageTimeout(IntPtr h, uint m, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);
        [DllImport("user32.dll")] public static extern IntPtr SetWinEventHook(uint min, uint max, IntPtr mod, WinEventProc p, uint pid, uint thread, uint flags);
        [DllImport("user32.dll")] public static extern bool UnhookWinEvent(IntPtr hook);
        [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr h, int id, uint mods, uint vk);
        [DllImport("user32.dll")] public static extern bool UnregisterHotKey(IntPtr h, int id);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string cls, string title);
        [DllImport("kernel32.dll")] public static extern uint GetCurrentThreadId();
        [DllImport("kernel32.dll")] public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
        [DllImport("kernel32.dll")] public static extern bool CloseHandle(IntPtr h);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern bool QueryFullProcessImageName(IntPtr h, int flags, StringBuilder s, ref int n);
        [DllImport("dwmapi.dll")] public static extern int DwmRegisterThumbnail(IntPtr dest, IntPtr src, out IntPtr id);
        [DllImport("dwmapi.dll")] public static extern int DwmUnregisterThumbnail(IntPtr id);
        [DllImport("dwmapi.dll")] public static extern int DwmUpdateThumbnailProperties(IntPtr id, ref DWM_THUMBNAIL_PROPERTIES p);
        [DllImport("dwmapi.dll")] public static extern int DwmQueryThumbnailSourceSize(IntPtr id, out SIZE s);
        [DllImport("dwmapi.dll")] public static extern int DwmGetWindowAttribute(IntPtr h, int attr, out int v, int size);
        [DllImport("dwmapi.dll")] public static extern int DwmFlush();
        [DllImport("user32.dll")] public static extern bool PrintWindow(IntPtr h, IntPtr hdc, uint flags);
        [StructLayout(LayoutKind.Sequential, Pack = 1)]
        public struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
        [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr h, IntPtr dst, ref POINT pos, ref SIZE size, IntPtr src, ref POINT srcPos, int key, ref BLENDFUNCTION blend, int flags);
        [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr h);
        [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr h, IntPtr dc);
        [StructLayout(LayoutKind.Sequential)]
        public struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }
        [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr dc, ref BITMAPINFOHEADER info, uint usage, out IntPtr bits, IntPtr section, uint offset);
        [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr dc);
        [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr dc, IntPtr o);
        [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr o);
        [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr dc);
        public delegate IntPtr HookProc(int code, IntPtr w, IntPtr l);
        [StructLayout(LayoutKind.Sequential)]
        public struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam, lParam; public uint time; public POINT pt; }
        [DllImport("user32.dll")] public static extern IntPtr SetWindowsHookEx(int id, HookProc p, IntPtr mod, uint thread);
        [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hook, int code, IntPtr w, IntPtr l);
        [DllImport("user32.dll")] public static extern int GetMessage(out MSG m, IntPtr h, uint min, uint max);
        [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr GetModuleHandle(string name);

        public const uint SWP_NOSIZE = 1, SWP_NOMOVE = 2, SWP_NOZORDER = 4, SWP_NOACTIVATE = 0x10, SWP_ASYNC = 0x4000;
        public const int SW_HIDE = 0, SW_SHOW = 5, SW_RESTORE = 9, SW_SHOWMINNOACTIVE = 7;
        public const int WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 8, WS_EX_APPWINDOW = 0x40000, WS_EX_NOACTIVATE = 0x8000000;
        public const int GWL_EXSTYLE = -20, WS_MAXIMIZE = 0x1000000, GWL_STYLE = -16, DWMWA_CLOAKED = 14;
        public const uint GW_OWNER = 4, GA_ROOTOWNER = 3, WM_CLOSE = 0x10, WM_GETICON = 0x7F;
        public const uint EVENT_FOREGROUND = 3, EVENT_MOVESIZESTART = 0xA, EVENT_MOVESIZEEND = 0xB, EVENT_MINIMIZESTART = 0x16, EVENT_MINIMIZEEND = 0x17;

        public static string ClassOf(IntPtr h)
        {
            var s = new StringBuilder(256);
            GetClassName(h, s, s.Capacity);
            return s.ToString();
        }

        public static string TitleOf(IntPtr h)
        {
            var s = new StringBuilder(512);
            GetWindowText(h, s, s.Capacity);
            return s.ToString();
        }

        public static string ProcessPathOf(IntPtr h)
        {
            uint pid;
            GetWindowThreadProcessId(h, out pid);
            IntPtr p = OpenProcess(0x1000, false, pid);
            if (p == IntPtr.Zero) return "";
            try
            {
                var s = new StringBuilder(1024);
                int n = s.Capacity;
                return QueryFullProcessImageName(p, 0, s, ref n) ? s.ToString() : "";
            }
            finally { CloseHandle(p); }
        }

        [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")] static extern int DwmGetWindowRect(IntPtr h, int attr, out RECT v, int size);

        // The part of a window that is actually drawn, relative to its window rect. GetWindowRect
        // also counts the invisible resize border, which would show up as a frame in previews.
        public static Rectangle VisiblePartOf(IntPtr h)
        {
            RECT w, f;
            if (!GetWindowRect(h, out w)) return Rectangle.Empty;
            var whole = new Rectangle(0, 0, w.Width, w.Height);
            if (DwmGetWindowRect(h, 9, out f, Marshal.SizeOf(typeof(RECT))) != 0) return whole;
            var part = Rectangle.Intersect(whole, new Rectangle(f.Left - w.Left, f.Top - w.Top, f.Width, f.Height));
            return part.Width > 0 && part.Height > 0 ? part : whole;
        }

        public static bool IsCloaked(IntPtr h)
        {
            int v;
            return DwmGetWindowAttribute(h, DWMWA_CLOAKED, out v, 4) == 0 && v != 0;
        }
    }

    class Settings
    {
        public bool Enabled = true, ShowRecent = true, ShowDesktopItems = false, OneAtATime = false;
        public bool Animate = true, Tilt = true, ManageTopmost = true;

        static string Dir { get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "StageManagerWin"); } }
        static string FilePath { get { return Path.Combine(Dir, "settings.ini"); } }
        public static string RecoveryPath { get { Directory.CreateDirectory(Dir); return Path.Combine(Dir, "recovery.txt"); } }
        public static string LogPath { get { Directory.CreateDirectory(Dir); return Path.Combine(Dir, "error.log"); } }

        public static Settings Load()
        {
            var s = new Settings();
            try
            {
                foreach (var line in File.ReadAllLines(FilePath))
                {
                    var p = line.Split('=');
                    if (p.Length != 2) continue;
                    bool v = p[1].Trim() == "1";
                    switch (p[0].Trim())
                    {
                        case "enabled": s.Enabled = v; break;
                        case "showRecent": s.ShowRecent = v; break;
                        case "showDesktopItems": s.ShowDesktopItems = v; break;
                        case "oneAtATime": s.OneAtATime = v; break;
                        case "animate": s.Animate = v; break;
                        case "tilt": s.Tilt = v; break;
                        case "manageTopmost": s.ManageTopmost = v; break;
                    }
                }
            }
            catch (IOException) { }
            return s;
        }

        public void Save()
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllLines(FilePath, new[] {
                "enabled=" + (Enabled ? 1 : 0), "showRecent=" + (ShowRecent ? 1 : 0),
                "showDesktopItems=" + (ShowDesktopItems ? 1 : 0), "oneAtATime=" + (OneAtATime ? 1 : 0),
                "animate=" + (Animate ? 1 : 0), "tilt=" + (Tilt ? 1 : 0), "manageTopmost=" + (ManageTopmost ? 1 : 0) });
        }
    }

    // A stage is one app or a user-made group. The last entry is the most recently used window.
    class Stage
    {
        public readonly List<IntPtr> Windows = new List<IntPtr>();
        public bool Hidden;   // hidden by the user: not on stage, not in the strip, until activated again
        public IntPtr Last { get { return Windows[Windows.Count - 1]; } }
        public void Touch(IntPtr h) { if (Windows.Remove(h)) Windows.Add(h); }
    }

    class Tile
    {
        public Stage Stage;
        public List<IntPtr> Windows;
        public Rectangle Card;
    }
}
