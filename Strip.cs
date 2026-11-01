using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows.Forms;

namespace StageManagerWin
{
    // A per-pixel-alpha layered window: everything is composed into one bitmap, which is what
    // gives previews soft shadows and smooth edges over any wallpaper, and lets tiles animate
    // individually inside the window instead of the whole window sliding onto a neighbour display.
    class StripForm : Form
    {
        const int CardWidth = StripRenderer.ArtWidth, CardHeight = StripRenderer.ArtHeight, Gap = 36, MinimizedIcon = 48;
        const int DragThreshold = 6, CaptureIntervalMs = 800, SlideMs = 260, HoverMs = 140, MoveMs = 90, AppearMs = 220;
        const int MinGap = 12, EdgeMargin = 16;
        const int ActiveCaptureEvery = 3;   // capture cycles between refreshes of on-stage windows
        const float Stagger = 0.18f;   // how far each tile's slide lags behind the one above

        readonly Manager manager;
        readonly Display display;
        readonly Dictionary<IntPtr, Bitmap> iconCache = new Dictionary<IntPtr, Bitmap>();
        readonly Dictionary<IntPtr, Bitmap> badgeCache = new Dictionary<IntPtr, Bitmap>();
        readonly TileLabel label = new TileLabel();
        readonly System.Windows.Forms.Timer slideTimer = new System.Windows.Forms.Timer { Interval = 15 };
        readonly Stopwatch slideClock = new Stopwatch();
        readonly Dictionary<IntPtr, Bitmap> tilted = new Dictionary<IntPtr, Bitmap>();
        readonly Dictionary<Stage, float> hoverAmount = new Dictionary<Stage, float>();
        readonly Dictionary<Stage, Motion> motions = new Dictionary<Stage, Motion>();
        readonly List<ThumbRef> thumbs = new List<ThumbRef>();
        readonly Thread captureThread;
        volatile IntPtr[] captureList = new IntPtr[0];
        volatile IntPtr[] activeWindows = new IntPtr[0];
        volatile HashSet<IntPtr> haveArt = new HashSet<IntPtr>();
        volatile bool closing, wantShown;
        List<Tile> tiles = new List<Tile>();
        string signature = "";
        double slide;   // 0 = off the edge and invisible, 1 = fully in place
        int hover = -1, pressed = -1;
        Point pressPoint;
        DragGhost ghost;
        LayerSurface surface;

        class ThumbRef { public IntPtr Id, Source; public Rectangle Area; public int TileIndex; }
        class Motion { public float Y, Appear; }

        public StripForm(Manager m, Display d)
        {
            manager = m;
            display = d;
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            Bounds = StripBounds();
            CreateHandle();
            slideTimer.Tick += (s, e) => StepAnimation();
            captureThread = new Thread(CaptureLoop) { IsBackground = true };
            captureThread.Start();
        }

        Rectangle StripBounds()
        {
            Rectangle a = display.Area;
            return new Rectangle(a.Left, a.Top, Manager.StripWidth, a.Height);
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                p.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | 0x80000;   // layered
                return p;
            }
        }

        public List<Tile> Tiles { get { return tiles; } }
        public bool HasTiles { get { return tiles.Count > 0; } }
        public bool Dragging { get { return ghost != null; } }

        // ---- Showing and hiding ----

        public void SetShown(bool show)
        {
            if (!show) label.Hide();
            if (wantShown == show) return;
            wantShown = show;
            if (!manager.Settings.Animate)
            {
                slide = show ? 1 : 0;
                UpdateThumbRects();
                Render();
                if (show) Show(); else Hide();
                return;
            }
            if (show && !Visible)
            {
                Render();
                Show();
            }
            StartAnimation();
        }

        void StartAnimation()
        {
            if (slideTimer.Enabled) return;
            slideClock.Restart();
            slideTimer.Start();
        }

        void StepAnimation()
        {
            double ms = slideClock.ElapsedMilliseconds;
            slideClock.Restart();

            double target = wantShown ? 1 : 0;
            slide = slide < target ? Math.Min(target, slide + ms / SlideMs) : Math.Max(target, slide - ms / SlideMs);
            bool settled = slide == target;

            for (int i = 0; i < tiles.Count; i++)
            {
                float now = HoverOf(tiles[i]), want = i == hover ? 1 : 0;
                float next = now < want ? Math.Min(want, now + (float)(ms / HoverMs)) : Math.Max(want, now - (float)(ms / HoverMs));
                hoverAmount[tiles[i].Stage] = next;
                if (next != want) settled = false;

                // Tiles glide to new slots when the list changes, and new ones fade in.
                Motion m;
                if (!motions.TryGetValue(tiles[i].Stage, out m)) continue;
                float gap = tiles[i].Card.Y - m.Y;
                m.Y = Math.Abs(gap) < 0.5f ? tiles[i].Card.Y : m.Y + gap * (float)Math.Min(1, ms / MoveMs);
                m.Appear = Math.Min(1, m.Appear + (float)(ms / AppearMs));
                if (m.Y != tiles[i].Card.Y || m.Appear < 1) settled = false;
            }

            UpdateThumbRects();
            Render();
            if (!settled) return;
            slideTimer.Stop();
            if (wantShown) Render();   // final frame at full quality
            else Hide();
        }

        void UpdateMotions(bool animate)
        {
            var stages = new HashSet<Stage>(tiles.Select(t => t.Stage));
            foreach (var gone in motions.Keys.Where(s => !stages.Contains(s)).ToList()) motions.Remove(gone);
            foreach (var gone in hoverAmount.Keys.Where(s => !stages.Contains(s)).ToList()) hoverAmount.Remove(gone);
            foreach (var t in tiles)
            {
                Motion m;
                if (!motions.TryGetValue(t.Stage, out m))
                    motions[t.Stage] = new Motion { Y = t.Card.Y, Appear = animate ? 0 : 1 };
                else if (!animate) m.Y = t.Card.Y;
            }
            if (animate) StartAnimation();
        }

        float HoverOf(Tile t)
        {
            float v;
            return hoverAmount.TryGetValue(t.Stage, out v) ? v : 0;
        }

        // Tiles arrive one after another rather than as a block.
        float RevealOf(int index)
        {
            double v = slide * (1 + Stagger * Math.Max(0, tiles.Count - 1)) - Stagger * index;
            return (float)Math.Max(0, Math.Min(1, v));
        }

        // ---- Content ----

        public void SetTiles(List<Tile> next)
        {
            if (Dragging) return;   // the dragged tile must stay valid until it is dropped
            Rectangle b = StripBounds();
            if (Bounds != b) Bounds = b;

            // As many as fit: spacing tightens from roomy to compact before any tile is left out.
            int fit = Math.Max(1, (b.Height - EdgeMargin * 2 + MinGap) / (CardHeight + MinGap));
            next = next.Take(fit).ToList();
            int gap = next.Count < 2 ? Gap : Math.Max(MinGap, Math.Min(Gap, (b.Height - EdgeMargin * 2 - next.Count * CardHeight) / (next.Count - 1)));
            int y = (b.Height - (next.Count * CardHeight + (next.Count - 1) * gap)) / 2;
            foreach (var t in next)
            {
                t.Card = new Rectangle((Manager.StripWidth - CardWidth) / 2, y, CardWidth, CardHeight);
                y += CardHeight + gap;
            }

            string sig = string.Join("|", next.Select(t => string.Join(",", ShownOf(t).Select(w => w.ToInt64() + (Native.IsIconic(w) ? "i" : "") + (tilted.ContainsKey(w) ? "t" : "")))));
            tiles = next;
            if (sig != signature)
            {
                signature = sig;
                hover = -1;
                label.Hide();
                UpdateMotions(Visible && manager.Settings.Animate);
                RebuildThumbs();
                Render();
            }
            else UpdateThumbRects();
            captureList = manager.Settings.Tilt ? next.SelectMany(ShownOf).ToArray() : new IntPtr[0];
        }

        public void SetActiveWindows(IntPtr[] windows) { activeWindows = windows; }

        // Up to three windows stacked, the most recent last (drawn on top).
        static List<IntPtr> ShownOf(Tile t)
        {
            return t.Windows.Skip(Math.Max(0, t.Windows.Count - 3)).ToList();
        }

        // The largest rectangle with the given proportions, centred in the area.
        static Rectangle Fit(Rectangle area, int width, int height)
        {
            double scale = Math.Min((double)area.Width / Math.Max(1, width), (double)area.Height / Math.Max(1, height));
            int w = Math.Max(1, (int)(width * scale)), h = Math.Max(1, (int)(height * scale));
            return new Rectangle(area.X + (area.Width - w) / 2, area.Y + (area.Height - h) / 2, w, h);
        }

        // Windows without a capture yet (or with tilt turned off) show a flat live DWM thumbnail.
        void RebuildThumbs()
        {
            foreach (var t in thumbs) Native.DwmUnregisterThumbnail(t.Id);
            thumbs.Clear();
            for (int index = 0; index < tiles.Count; index++)
            {
                IntPtr front = tiles[index].Windows[tiles[index].Windows.Count - 1];
                if (tilted.ContainsKey(front)) continue;
                IntPtr id;
                if (Native.DwmRegisterThumbnail(Handle, front, out id) != 0) continue;
                thumbs.Add(new ThumbRef { Id = id, Source = front, Area = tiles[index].Card, TileIndex = index });
            }
            UpdateThumbRects();

            var inUse = new HashSet<IntPtr>(tiles.SelectMany(ShownOf).Concat(activeWindows));
            foreach (var h in tilted.Keys.Where(k => !inUse.Contains(k)).ToList())
            {
                tilted[h].Dispose();
                tilted.Remove(h);
            }
            foreach (var h in badgeCache.Keys.Where(k => !inUse.Contains(k)).ToList())
            {
                if (badgeCache[h] != null) badgeCache[h].Dispose();
                badgeCache.Remove(h);
            }
            haveArt = new HashSet<IntPtr>(tilted.Keys);
        }

        void UpdateThumbRects()
        {
            foreach (var t in thumbs)
            {
                float reveal = RevealOf(t.TileIndex);
                float e = 1 - (float)Math.Pow(1 - reveal, 3);
                bool usable = true;
                Rectangle part;
                if (Native.IsIconic(t.Source))
                {
                    // A minimized window's rect is meaningless; ask DWM what it kept. Anything
                    // title-bar sized isn't worth showing, and the big icon underneath stays visible.
                    Native.SIZE s;
                    usable = Native.DwmQueryThumbnailSourceSize(t.Id, out s) == 0 && s.X >= 200 && s.Y >= 150;
                    part = usable ? new Rectangle(0, 0, s.X, s.Y) : t.Area;
                }
                else part = Native.VisiblePartOf(t.Source);
                if (part.IsEmpty) continue;
                Rectangle image = Fit(t.Area, part.Width, part.Height);
                image.Offset((int)((1 - e) * -(CardWidth + 40)), 0);
                var p = new Native.DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = 1 | 2 | 4 | 8 | 0x10,   // destination, source, opacity, visible, whole window
                    rcDestination = new Native.RECT(image.Left, image.Top, image.Right, image.Bottom),
                    rcSource = new Native.RECT(part.Left, part.Top, part.Right, part.Bottom),
                    opacity = (byte)(255 * e),
                    fVisible = reveal > 0 && usable,
                    fSourceClientAreaOnly = false
                };
                Native.DwmUpdateThumbnailProperties(t.Id, ref p);
            }
        }

        // ---- Tilted previews ----
        // DWM thumbnails can't be drawn in perspective, so tilted previews use periodic captures.
        // A window that captures blank has no art and keeps its flat live thumbnail.

        void CaptureLoop()
        {
            int cycle = 0;
            while (!closing)
            {
                foreach (var h in captureList)
                {
                    if (closing) return;
                    // While the strip is out of sight, capture only what's needed for a good first frame.
                    if (!wantShown && haveArt.Contains(h)) continue;
                    if (!CaptureAndSend(h)) return;
                }
                // On-stage windows are kept fairly fresh too: once minimized they can't be
                // captured, and this image is all their tile will have.
                if (++cycle % ActiveCaptureEvery == 0 && manager.Settings.Tilt)
                    foreach (var h in activeWindows)
                        if (closing || !CaptureAndSend(h)) return;
                Thread.Sleep(CaptureIntervalMs);
            }
        }

        // Returns false once the form is gone and capturing should stop.
        bool CaptureAndSend(IntPtr h)
        {
            Bitmap b = CaptureArt(h);
            if (b == null) return true;
            try { BeginInvoke((Action)(() => ReceiveArt(h, b))); }
            catch (InvalidOperationException) { b.Dispose(); return false; }
            return true;
        }

        void ReceiveArt(IntPtr h, Bitmap b)
        {
            Bitmap old;
            bool onStrip = tiles.Any(t => ShownOf(t).Contains(h));
            bool wanted = manager.Settings.Tilt && (onStrip || activeWindows.Contains(h));
            if (tilted.TryGetValue(h, out old)) old.Dispose();
            if (wanted) tilted[h] = b;
            else { tilted.Remove(h); b.Dispose(); }
            haveArt = new HashSet<IntPtr>(tilted.Keys);
            if (old == null && onStrip && wanted) manager.Sync();   // first capture: swap the DWM thumbnail out
            if (Visible && !slideTimer.Enabled) Render();   // a running animation draws it next frame anyway
        }

        public void OnTiltChanged()
        {
            if (manager.Settings.Tilt) return;
            captureList = new IntPtr[0];
            foreach (var b in tilted.Values) b.Dispose();
            tilted.Clear();
            haveArt = new HashSet<IntPtr>();
        }

        static Bitmap CaptureArt(IntPtr h)
        {
            Native.RECT r;
            if (!Native.IsWindow(h) || !Native.IsWindowVisible(h) || Native.IsIconic(h) || Native.IsHungAppWindow(h) || !Native.GetWindowRect(h, out r)) return null;
            if (r.Width < 10 || r.Height < 10 || r.Width > 8000 || r.Height > 8000) return null;

            using (var full = new Bitmap(r.Width, r.Height, PixelFormat.Format32bppArgb))
            {
                using (var g = Graphics.FromImage(full))
                {
                    IntPtr dc = g.GetHdc();
                    bool ok = Native.PrintWindow(h, dc, 2);   // 2 = include GPU-drawn content
                    g.ReleaseHdc(dc);
                    if (!ok) return null;
                }
                return IsBlank(full) ? null : StripRenderer.Prepare(full, Native.VisiblePartOf(h));
            }
        }

        static bool IsBlank(Bitmap b)
        {
            Color first = b.GetPixel(b.Width / 2, b.Height / 2);
            for (int y = 1; y < 12; y++)
                for (int x = 1; x < 12; x++)
                    if (b.GetPixel(b.Width * x / 12, b.Height * y / 12) != first) return false;
            return true;
        }

        // ---- Painting ----

        void Render()
        {
            if (Width <= 0 || Height <= 0 || !IsHandleCreated) return;
            if (surface == null || surface.Width != Width || surface.Height != Height)
            {
                if (surface != null) surface.Dispose();
                surface = new LayerSurface(Width, Height);
            }
            using (var g = Graphics.FromImage(surface.Image))
            {
                g.Clear(Color.Transparent);
                for (int i = 0; i < tiles.Count; i++) DrawTile(g, i);
            }
            surface.Present(this);
        }

        void DrawTile(Graphics g, int index)
        {
            Tile t = tiles[index];
            float reveal = RevealOf(index);
            if (reveal <= 0) return;

            // Nearly invisible, but a layered window only receives the mouse where it isn't fully clear.
            using (var hit = new SolidBrush(Color.FromArgb(1, 0, 0, 0))) g.FillRectangle(hit, t.Card);

            IntPtr last = t.Windows[t.Windows.Count - 1];
            Motion motion;
            int y = t.Card.Y;
            if (motions.TryGetValue(t.Stage, out motion))
            {
                y = (int)motion.Y;
                reveal = Math.Min(reveal, motion.Appear);
                if (reveal <= 0) return;
            }
            var art = new StripRenderer.TileArt { Hover = HoverOf(t), Reveal = reveal, Icon = BadgeOf(last), Moving = slideTimer.Enabled };
            foreach (var w in ShownOf(t))
            {
                Bitmap b;
                if (tilted.TryGetValue(w, out b)) art.Layers.Add(b);
            }

            // A minimized window has no live image, so its tile shows the big icon.
            Bitmap icon = Native.IsIconic(last) || !Native.IsWindowVisible(last) ? IconOf(last) : null;
            if (icon != null && art.Layers.Count == 0)
                StripRenderer.DrawAlone(g, icon, Rectangle.Inflate(t.Card, (MinimizedIcon - t.Card.Width) / 2, (MinimizedIcon - t.Card.Height) / 2), reveal);
            StripRenderer.DrawTile(g, art, t.Card.X, y);
        }

        Bitmap BadgeOf(IntPtr h)
        {
            Bitmap badge;
            if (badgeCache.TryGetValue(h, out badge)) return badge;
            Bitmap icon = IconOf(h);
            badge = icon == null ? null : StripRenderer.PrepareIcon(icon);
            badgeCache[h] = badge;
            return badge;
        }

        // Puts a premultiplied bitmap on a per-pixel-alpha layered window.
        public static void Present(Form form, Bitmap canvas)
        {
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            IntPtr mem = Native.CreateCompatibleDC(screen);
            IntPtr bitmap = canvas.GetHbitmap(Color.FromArgb(0));
            IntPtr old = Native.SelectObject(mem, bitmap);
            try
            {
                var pos = new Native.POINT { X = form.Left, Y = form.Top };
                var size = new Native.SIZE { X = form.Width, Y = form.Height };
                var src = new Native.POINT();
                var blend = new Native.BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
                Native.UpdateLayeredWindow(form.Handle, screen, ref pos, ref size, mem, ref src, 0, ref blend, 2);
            }
            finally
            {
                Native.SelectObject(mem, old);
                Native.DeleteObject(bitmap);
                Native.DeleteDC(mem);
                Native.ReleaseDC(IntPtr.Zero, screen);
            }
        }

        // The window names, shown beside a preview while the cursor is on it.
        void UpdateLabel()
        {
            StartAnimation();   // the preview under the cursor lifts
            if (hover < 0 || hover >= tiles.Count || Dragging) { label.Hide(); return; }
            Tile t = tiles[hover];
            var names = t.Windows.AsEnumerable().Reverse().Take(3).Select(Native.TitleOf).ToList();
            if (t.Windows.Count > 3) names.Add("+" + (t.Windows.Count - 3));
            label.ShowFor(string.Join("\n", names), new Point(StripBounds().Left + t.Card.Right + 12, Top + t.Card.Y + t.Card.Height / 2));
        }

        Bitmap IconOf(IntPtr h)
        {
            Bitmap b;
            if (iconCache.TryGetValue(h, out b)) return b;
            IntPtr icon;
            Native.SendMessageTimeout(h, Native.WM_GETICON, new IntPtr(1), IntPtr.Zero, 2, 50, out icon);
            if (icon == IntPtr.Zero) icon = Native.GetClassLongPtr(h, -14);
            try
            {
                if (icon != IntPtr.Zero) b = Icon.FromHandle(icon).ToBitmap();
                else
                {
                    string path = Native.ProcessPathOf(h);
                    if (path.Length > 0)
                        using (var ic = Icon.ExtractAssociatedIcon(path)) b = ic.ToBitmap();
                }
            }
            catch (ArgumentException) { }
            catch (FileNotFoundException) { }
            iconCache[h] = b;
            return b;
        }

        // ---- Mouse ----

        int TileAt(Point p)
        {
            for (int i = 0; i < tiles.Count; i++) if (tiles[i].Card.Contains(p)) return i;
            return -1;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            if (e.Button == MouseButtons.Left)
            {
                pressed = TileAt(e.Location);
                pressPoint = e.Location;            }
            base.OnMouseDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            int h = TileAt(e.Location);
            if (h != hover) { hover = h; UpdateLabel(); }
            if (pressed >= 0 && ghost == null && e.Button == MouseButtons.Left &&
                (Math.Abs(e.X - pressPoint.X) > DragThreshold || Math.Abs(e.Y - pressPoint.Y) > DragThreshold))
            {
                var t = tiles[pressed];
                IntPtr front = t.Windows[t.Windows.Count - 1];
                Bitmap art;
                tilted.TryGetValue(front, out art);
                ghost = new DragGhost(front, art);
                ghost.Show();
                UpdateLabel();
            }
            if (ghost != null) ghost.Follow(Cursor.Position);
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = -1;
            UpdateLabel();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (e.Button == MouseButtons.Right) { ShowTileMenu(e.Location); return; }
            if (e.Button != MouseButtons.Left) return;

            label.Hide();
            int index = pressed;
            pressed = -1;
            if (ghost != null)
            {
                bool droppedOnStage = Cursor.Position.X > Left + Width;
                ghost.Close();
                ghost.Dispose();
                ghost = null;
                if (droppedOnStage && index >= 0 && index < tiles.Count) manager.MergeIntoActive(display, tiles[index]);
                return;
            }
            if (index < 0 || index >= tiles.Count || TileAt(e.Location) != index) return;
            if ((ModifierKeys & Keys.Shift) != 0) manager.MergeIntoActive(display, tiles[index]);
            else manager.ActivateTile(display, tiles[index]);
        }

        void ShowTileMenu(Point p)
        {
            int i = TileAt(p);
            if (i < 0) return;
            Tile t = tiles[i];
            var menu = new ContextMenuStrip();
            if (t.Stage.Windows.Count > 1)
                menu.Items.Add("Grubu çöz", null, (s, e) => manager.Ungroup(display, t));
            menu.Items.Add("Kapat", null, (s, e) => manager.CloseStage(t));
            menu.Closed += (s, e) => BeginInvoke((Action)menu.Dispose);
            menu.Show(this, p);
        }

        protected override void Dispose(bool disposing)
        {
            closing = true;
            slideTimer.Dispose();
            label.Dispose();
            if (surface != null) surface.Dispose();
            foreach (var t in thumbs) Native.DwmUnregisterThumbnail(t.Id);
            thumbs.Clear();
            base.Dispose(disposing);
        }
    }

    // Memory shared directly with GDI, so an animation frame is drawn in place and handed to the
    // layered window with no per-frame allocation or copy.
    class LayerSurface : IDisposable
    {
        public readonly Bitmap Image;
        public readonly int Width, Height;
        readonly IntPtr memory, section, previous;

        public LayerSurface(int width, int height)
        {
            Width = width;
            Height = height;
            var info = new Native.BITMAPINFOHEADER { biWidth = width, biHeight = -height, biPlanes = 1, biBitCount = 32 };   // negative: top-down
            info.biSize = System.Runtime.InteropServices.Marshal.SizeOf(info);
            IntPtr screen = Native.GetDC(IntPtr.Zero);
            memory = Native.CreateCompatibleDC(screen);
            IntPtr bits;
            section = Native.CreateDIBSection(screen, ref info, 0, out bits, IntPtr.Zero, 0);
            Native.ReleaseDC(IntPtr.Zero, screen);
            previous = Native.SelectObject(memory, section);
            Image = new Bitmap(width, height, width * 4, PixelFormat.Format32bppPArgb, bits);
        }

        public void Present(Form form)
        {
            var pos = new Native.POINT { X = form.Left, Y = form.Top };
            var size = new Native.SIZE { X = Width, Y = Height };
            var src = new Native.POINT();
            var blend = new Native.BLENDFUNCTION { BlendOp = 0, SourceConstantAlpha = 255, AlphaFormat = 1 };
            Native.UpdateLayeredWindow(form.Handle, IntPtr.Zero, ref pos, ref size, memory, ref src, 0, ref blend, 2);
        }

        public void Dispose()
        {
            Image.Dispose();
            Native.SelectObject(memory, previous);
            Native.DeleteObject(section);
            Native.DeleteDC(memory);
        }
    }

    // The small name tag beside a preview. Ignores the mouse so it can't steal the hover.
    class TileLabel : Form
    {
        const int MaxChars = 60;

        public TileLabel()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
        }

        public void ShowFor(string text, Point leftMiddle)
        {
            string shortened = string.Join("\n", text.Split('\n').Select(s => s.Length > MaxChars ? s.Substring(0, MaxChars) + "…" : s));
            using (var image = StripRenderer.RenderLabel(shortened))
            {
                Bounds = new Rectangle(leftMiddle.X - StripRenderer.LabelShadowPad, leftMiddle.Y - image.Height / 2, image.Width, image.Height);
                if (!IsHandleCreated) CreateHandle();
                StripForm.Present(this, image);
            }
            if (!Visible) Show();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                p.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE | 0x20 | 0x80000;
                return p;
            }
        }
    }

    // The small preview that follows the cursor while a tile is being dragged.
    class DragGhost : Form
    {
        const int W = 170, H = 110;
        IntPtr thumb;

        public DragGhost(IntPtr source, Bitmap art)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            // With a tilted capture the ghost looks just like the tile it was lifted from.
            if (art != null)
            {
                Size = art.Size;
                Follow(Cursor.Position);
                CreateHandle();
                StripForm.Present(this, art);
                return;
            }
            Rectangle part = Native.VisiblePartOf(source);
            if (part.IsEmpty) part = new Rectangle(0, 0, W, H);
            double scale = Math.Min((double)W / part.Width, (double)H / part.Height);
            Size = new Size(Math.Max(1, (int)(part.Width * scale)), Math.Max(1, (int)(part.Height * scale)));
            Follow(Cursor.Position);
            CreateHandle();
            using (var clear = new Bitmap(Width, Height, System.Drawing.Imaging.PixelFormat.Format32bppPArgb))
                StripForm.Present(this, clear);
            if (Native.DwmRegisterThumbnail(Handle, source, out thumb) != 0) return;
            var p = new Native.DWM_THUMBNAIL_PROPERTIES
            {
                dwFlags = 1 | 2 | 4 | 8,
                rcDestination = new Native.RECT(0, 0, Width, Height),
                rcSource = new Native.RECT(part.Left, part.Top, part.Right, part.Bottom),
                opacity = 230,
                fVisible = true
            };
            Native.DwmUpdateThumbnailProperties(thumb, ref p);
        }

        public void Follow(Point p) { Location = new Point(p.X - Width / 2, p.Y - Height / 2); }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                // Layered and transparent to the mouse, so the strip keeps receiving the drag.
                p.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | 0x20 | 0x80000;
                return p;
            }
        }

        protected override void Dispose(bool disposing)
        {
            if (thumb != IntPtr.Zero) { Native.DwmUnregisterThumbnail(thumb); thumb = IntPtr.Zero; }
            base.Dispose(disposing);
        }
    }

    // Draws the stage-change animation: DWM scales the live thumbnails, which stays smooth even
    // for heavy apps. The real windows only jump into place once it has finished.
    class StageOverlay : Form
    {
        static readonly int DurationMs = ReadDuration();

        public class Item { public IntPtr Source; public Rectangle From, To; public bool Leaving; public IntPtr Thumb; }

        static int ReadDuration()
        {
            int v;
            return int.TryParse(Environment.GetEnvironmentVariable("STAGE_ANIM_MS"), out v) ? v : 220;
        }

        public StageOverlay(Rectangle bounds)
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            BackColor = Color.Magenta;
            TransparencyKey = Color.Magenta;
            Bounds = bounds;
            CreateHandle();
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                var p = base.CreateParams;
                p.ExStyle |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_TOPMOST | Native.WS_EX_NOACTIVATE | 0x20;
                return p;
            }
        }

        // Runs to completion on the UI thread; nothing else can interleave during these few frames.
        public void Animate(List<Item> items, Action beforeFrames, Action afterFrames)
        {
            foreach (var i in items)
                if (Native.DwmRegisterThumbnail(Handle, i.Source, out i.Thumb) != 0) i.Thumb = IntPtr.Zero;

            try
            {
                Draw(items, 0);
                Show();
                Update();
                Native.DwmFlush();
                beforeFrames();

                var clock = Stopwatch.StartNew();
                while (clock.ElapsedMilliseconds < DurationMs)
                {
                    double t = (double)clock.ElapsedMilliseconds / DurationMs;
                    Draw(items, 1 - Math.Pow(1 - t, 3));   // ease-out
                    if (Native.DwmFlush() != 0) Thread.Sleep(8);
                }
                Draw(items, 1);
                Native.DwmFlush();

                afterFrames();
                Native.DwmFlush();
                Native.DwmFlush();
            }
            finally
            {
                Hide();
                foreach (var i in items) if (i.Thumb != IntPtr.Zero) Native.DwmUnregisterThumbnail(i.Thumb);
            }
        }

        void Draw(List<Item> items, double e)
        {
            foreach (var i in items)
            {
                if (i.Thumb == IntPtr.Zero) continue;
                var p = new Native.DWM_THUMBNAIL_PROPERTIES
                {
                    dwFlags = 1 | 4 | 8,
                    rcDestination = new Native.RECT(
                        Lerp(i.From.Left, i.To.Left, e) - Left, Lerp(i.From.Top, i.To.Top, e) - Top,
                        Lerp(i.From.Right, i.To.Right, e) - Left, Lerp(i.From.Bottom, i.To.Bottom, e) - Top),
                    opacity = (byte)(i.Leaving ? 255 - 135 * e : 255),
                    fVisible = true
                };
                Native.DwmUpdateThumbnailProperties(i.Thumb, ref p);
            }
        }

        static int Lerp(int a, int b, double e) { return (int)(a + (b - a) * e); }
    }
}
