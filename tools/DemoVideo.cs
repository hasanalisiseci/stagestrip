// Renders the demo video for the README: docs/demo.mp4 and docs/demo.gif.
// An animation using the app's own preview renderer and placeholder windows, not a screen recording.
// Opens no windows. Encodes with Media Foundation, which ships with Windows.
// Usage: DemoVideo.exe <docs-folder> <folder-for-keyframe-pngs>
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using StageManagerWin;

static class DemoVideo
{
    const int W = 1920, H = 1080, Fps = 30, GifW = 800, GifH = 450;
    const double Length = 20.5, Flight = 0.55;
    static readonly Rectangle Stage = new Rectangle(525, 170, 1100, 680);
    static readonly Rectangle GroupA = new Rectangle(330, 110, 1000, 618), GroupB = new Rectangle(790, 330, 1000, 618);

    class Key { public double T; public Rectangle R; public bool Tile; }
    class Actor
    {
        public string Name; public Bitmap Full, Art, Icon;
        public List<Key> Keys = new List<Key>();
        public void At(double t, Rectangle r, bool tile = false) { Keys.Add(new Key { T = t, R = r, Tile = tile }); }
    }

    static Rectangle Slot(int index, int count)
    {
        const int gap = 40;
        int top = (H - 48 - (count * StripRenderer.ArtHeight + (count - 1) * gap)) / 2;
        return new Rectangle(30, top + index * (StripRenderer.ArtHeight + gap), StripRenderer.ArtWidth, StripRenderer.ArtHeight);
    }

    static double Ease(double x) { x = Math.Max(0, Math.Min(1, x)); return 1 - Math.Pow(1 - x, 3); }
    static double Fade(double t, double a, double b) { return Math.Min(Ease((t - a) / 0.3), Ease((b - t) / 0.3)); }
    static int Lerp(int a, int b, double e) { return (int)(a + (b - a) * e); }

    // ---- Placeholder apps ----

    static Bitmap MakeWindow(string title, Color accent, bool dark, int style)
    {
        const int w = 1100, h = 680;
        var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(b))
        using (var font = new Font("Segoe UI", 15f))
        using (var bg = new SolidBrush(dark ? Color.FromArgb(32, 32, 36) : Color.FromArgb(250, 250, 252)))
        using (var bar = new SolidBrush(dark ? Color.FromArgb(46, 46, 52) : Color.FromArgb(236, 236, 241)))
        using (var line = new SolidBrush(dark ? Color.FromArgb(78, 78, 88) : Color.FromArgb(214, 214, 222)))
        using (var color = new SolidBrush(accent))
        using (var text = new SolidBrush(dark ? Color.White : Color.FromArgb(30, 30, 34)))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillRectangle(bg, 0, 0, w, h);
            g.FillRectangle(bar, 0, 0, w, 52);
            g.DrawString(title, font, text, 18, 12);
            for (int i = 0; i < 3; i++) g.FillRectangle(line, w - 44 - i * 44, 24, 20, 3);
            switch (style)
            {
                case 0:   // page
                    g.FillRectangle(color, 60, 90, w - 120, 230);
                    for (int y = 350; y < h - 30; y += 40) g.FillRectangle(line, 60, y, w - 120 - (y % 9) * 50, 16);
                    break;
                case 1:   // list with artwork
                    g.FillRectangle(color, 40, 90, 320, 320);
                    for (int y = 90; y < h - 40; y += 56) { g.FillRectangle(line, 400, y + 6, 420 + (y % 7) * 30, 14); g.FillRectangle(line, 400, y + 28, 200, 8); }
                    break;
                case 2:   // folders
                    for (int y = 90; y < h - 120; y += 150)
                        for (int x = 60; x < w - 150; x += 170) { g.FillRectangle(color, x, y + 14, 120, 80); g.FillRectangle(line, x, y + 106, 120, 10); }
                    break;
                case 3:   // code
                    g.FillRectangle(bar, 0, 52, 210, h - 52);
                    for (int y = 80; y < h - 24; y += 30)
                    {
                        g.FillRectangle(line, 24, y + 4, 130, 8);
                        int indent = 240 + ((y / 30) % 4) * 30;
                        g.FillRectangle(y % 90 == 50 ? color : line, indent, y + 4, 110, 10);
                        g.FillRectangle(line, indent + 124, y + 4, 90 + (y % 13) * 22, 10);
                    }
                    break;
                default:  // chat
                    for (int i = 0, y = 90; y < h - 110; i++, y += 92)
                    {
                        bool mine = i % 2 == 1;
                        g.FillRectangle(mine ? color : line, mine ? w - 500 : 50, y, 450, 68);
                    }
                    g.FillRectangle(line, 50, h - 66, w - 100, 40);
                    break;
            }
        }
        return b;
    }

    static Bitmap MakeIcon(Color c)
    {
        var b = new Bitmap(64, 64, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(b))
        using (var s = new SolidBrush(c))
        using (var path = Rounded(new RectangleF(2, 2, 60, 60), 14))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillPath(s, path);
            g.FillEllipse(Brushes.White, 20, 20, 24, 24);
        }
        using (b) return StripRenderer.PrepareIcon(b);
    }

    static GraphicsPath Rounded(RectangleF r, float radius)
    {
        float d = radius * 2;
        var p = new GraphicsPath();
        p.AddArc(r.X, r.Y, d, d, 180, 90);
        p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        p.CloseFigure();
        return p;
    }

    // ---- Scene ----

    static List<Actor> BuildActors()
    {
        string[] names = { "Browser", "Music", "Files", "Code", "Chat" };
        Color[] colors = { Color.FromArgb(66, 133, 244), Color.FromArgb(30, 190, 110), Color.FromArgb(245, 180, 40), Color.FromArgb(160, 100, 240), Color.FromArgb(235, 90, 70) };
        bool[] dark = { false, true, false, true, false };
        Rectangle[] messy = {
            new Rectangle(90, 60, 1100, 680), new Rectangle(720, 90, 1100, 680), new Rectangle(330, 300, 1100, 680),
            new Rectangle(60, 330, 1100, 680), new Rectangle(760, 340, 1100, 680) };

        var actors = new List<Actor>();
        for (int i = 0; i < names.Length; i++)
        {
            var a = new Actor { Name = names[i], Full = MakeWindow(names[i], colors[i], dark[i], i), Icon = MakeIcon(colors[i]) };
            a.Art = StripRenderer.Prepare(a.Full, Rectangle.Empty);
            a.At(2.8 + i * 0.22, messy[i]);
            actors.Add(a);
        }
        Actor browser = actors[0], music = actors[1], files = actors[2], code = actors[3], chat = actors[4];

        // Stage forms; Browser on stage, the rest in the strip.
        browser.At(5, Stage);
        music.At(5, Slot(0, 4), true); files.At(5, Slot(1, 4), true); code.At(5, Slot(2, 4), true); chat.At(5, Slot(3, 4), true);

        // Click Music: it swaps with Browser, which goes to the top of the strip.
        music.At(9.6, Stage);
        browser.At(9.6, Slot(0, 4), true);

        // Drag Files to the centre: Music and Files share the stage.
        files.At(12.2, GroupB);
        music.At(12.2, GroupA);
        browser.At(12.2, Slot(0, 3), true); code.At(12.2, Slot(1, 3), true); chat.At(12.2, Slot(2, 3), true);

        // Click the desktop: the group goes to the strip. Click again: it's back.
        music.At(15, Slot(0, 4), true); files.At(15, Slot(0, 4), true);
        browser.At(15, Slot(1, 4), true); code.At(15, Slot(2, 4), true); chat.At(15, Slot(3, 4), true);
        music.At(16.3, GroupA); files.At(16.3, GroupB);
        browser.At(16.3, Slot(0, 3), true); code.At(16.3, Slot(1, 3), true); chat.At(16.3, Slot(2, 3), true);
        return actors;
    }

    static readonly double[][] CursorPath = {
        new double[] { 7.8, 1500, 900 }, new double[] { 9.5, 110, Slot(0, 4).Y + 50 },    // to Music's tile
        new double[] { 10.4, 110, Slot(0, 4).Y + 50 }, new double[] { 11.1, 110, Slot(1, 4).Y + 50 },
        new double[] { 11.75, 110, Slot(1, 4).Y + 50 }, new double[] { 12.5, 1290, 640 }, // drags Files
        new double[] { 13.6, 1290, 640 }, new double[] { 14.8, 1860, 60 },                // to empty desktop
        new double[] { 16.6, 1860, 60 }, new double[] { 17.2, 1500, 1200 } };

    static PointF CursorAt(double t)
    {
        for (int i = 1; i < CursorPath.Length; i++)
        {
            if (t > CursorPath[i][0]) continue;
            double e = Ease((t - CursorPath[i - 1][0]) / (CursorPath[i][0] - CursorPath[i - 1][0]));
            return new PointF((float)(CursorPath[i - 1][1] + (CursorPath[i][1] - CursorPath[i - 1][1]) * e),
                (float)(CursorPath[i - 1][2] + (CursorPath[i][2] - CursorPath[i - 1][2]) * e));
        }
        return new PointF(1500, 1200);
    }

    static void DrawWallpaper(Graphics g)
    {
        using (var b = new LinearGradientBrush(new Rectangle(0, 0, W, H), Color.FromArgb(52, 96, 190), Color.FromArgb(150, 80, 170), 35f))
            g.FillRectangle(b, 0, 0, W, H);
        using (var path = new GraphicsPath())
        {
            path.AddEllipse(900, 300, 1400, 1100);
            using (var pb = new PathGradientBrush(path) { CenterColor = Color.FromArgb(120, 255, 170, 120), SurroundColors = new[] { Color.FromArgb(0, 255, 170, 120) } })
                g.FillPath(pb, path);
        }
        using (var bar = new SolidBrush(Color.FromArgb(150, 20, 22, 32))) g.FillRectangle(bar, 0, H - 48, W, 48);
    }

    static void DrawCaption(Graphics g, string text, double alpha)
    {
        if (alpha <= 0) return;
        using (var font = new Font("Segoe UI Semibold", 30f))
        {
            SizeF s = g.MeasureString(text, font);
            var r = new RectangleF((W - s.Width - 64) / 2, H - 170, s.Width + 64, s.Height + 26);
            using (var path = Rounded(r, 22))
            using (var fill = new SolidBrush(Color.FromArgb((int)(225 * alpha), 22, 22, 28)))
            using (var ink = new SolidBrush(Color.FromArgb((int)(255 * alpha), 255, 255, 255)))
            {
                g.FillPath(fill, path);
                using (var center = new StringFormat { Alignment = StringAlignment.Center, LineAlignment = StringAlignment.Center })
                    g.DrawString(text, font, ink, r, center);
            }
        }
    }

    static void CenterText(Graphics g, string text, float size, FontStyle style, int y, double alpha, Color color)
    {
        using (var font = new Font("Segoe UI", size, style))
        using (var ink = new SolidBrush(Color.FromArgb((int)(255 * alpha), color)))
        {
            SizeF s = g.MeasureString(text, font);
            g.DrawString(text, font, ink, (W - s.Width) / 2, y);
        }
    }

    static void DrawCursor(Graphics g, PointF p, bool pressed)
    {
        float k = pressed ? 1.5f : 1.75f;
        PointF[] arrow = { new PointF(0, 0), new PointF(0, 17), new PointF(4, 13), new PointF(7, 20), new PointF(10, 19), new PointF(7, 12), new PointF(13, 12) };
        var pts = arrow.Select(a => new PointF(p.X + a.X * k, p.Y + a.Y * k)).ToArray();
        using (var pen = new Pen(Color.Black, 2f)) { g.FillPolygon(Brushes.White, pts); g.DrawPolygon(pen, pts); }
    }

    static void DrawFlat(Graphics g, Actor a, Rectangle r, double alpha)
    {
        using (var path = Rounded(r, Math.Max(4, r.Width / 90f)))
        {
            int reach = Math.Min(18, r.Width / 40);
            for (int i = reach; i >= 1; i--)
            {
                var sr = r; sr.Inflate(i, i); sr.Offset(0, reach / 2);
                using (var sp = Rounded(sr, 12 + i))
                using (var sb = new SolidBrush(Color.FromArgb((int)(5 * alpha), 0, 0, 0))) g.FillPath(sb, sp);
            }
            var state = g.Save();
            g.SetClip(path);
            using (var ia = new ImageAttributes())
            {
                ia.SetColorMatrix(new ColorMatrix { Matrix33 = (float)alpha });
                g.DrawImage(a.Full, r, 0, 0, a.Full.Width, a.Full.Height, GraphicsUnit.Pixel, ia);
            }
            g.Restore(state);
        }
    }

    static void DrawFrame(Graphics g, double t, List<Actor> actors)
    {
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
        DrawWallpaper(g);

        // Tiles underneath, windows above; whichever arrived last is on top.
        var flat = new List<KeyValuePair<double, Action>>();
        foreach (var a in actors)
        {
            Key cur = a.Keys.LastOrDefault(k => k.T <= t);
            if (cur == null) continue;
            int idx = a.Keys.IndexOf(cur);
            Key prev = idx > 0 ? a.Keys[idx - 1] : null;
            double e = Ease((t - cur.T) / Flight);
            Actor actor = a;

            if (prev == null)
            {
                double pop = Ease((t - cur.T) / 0.3);
                var r = Rectangle.Inflate(cur.R, (int)(-30 * (1 - pop)), (int)(-20 * (1 - pop)));
                flat.Add(new KeyValuePair<double, Action>(cur.T, () => DrawFlat(g, actor, r, pop)));
            }
            else if (cur.Tile && prev.Tile)
            {
                // Glide between slots, keeping the tilted look.
                int y = Lerp(prev.R.Y, cur.R.Y, e);
                var art = new StripRenderer.TileArt { Reveal = 1, Icon = a.Icon };
                art.Layers.Add(a.Art);
                StripRenderer.DrawTile(g, art, cur.R.X, y);
            }
            else if (cur.Tile && e >= 1)
            {
                var art = new StripRenderer.TileArt { Reveal = 1, Icon = a.Icon };
                art.Layers.Add(a.Art);
                StripRenderer.DrawTile(g, art, cur.R.X, cur.R.Y);
            }
            else
            {
                var r = Rectangle.FromLTRB(Lerp(prev.R.Left, cur.R.Left, e), Lerp(prev.R.Top, cur.R.Top, e), Lerp(prev.R.Right, cur.R.Right, e), Lerp(prev.R.Bottom, cur.R.Bottom, e));
                flat.Add(new KeyValuePair<double, Action>(cur.T + (cur.Tile ? 100 : 0), () => DrawFlat(g, actor, r, 1)));
            }
        }
        foreach (var f in flat.OrderBy(k => k.Key)) f.Value();

        // Name tag while hovering over Files' tile.
        double tag = Fade(t, 11.15, 11.75);
        if (tag > 0)
            using (var label = StripRenderer.RenderLabel("Files"))
            using (var ia = new ImageAttributes())
            {
                ia.SetColorMatrix(new ColorMatrix { Matrix33 = (float)tag });
                Rectangle slot = Slot(1, 4);
                var dest = new Rectangle(slot.Right + 6, slot.Y + slot.Height / 2 - label.Height / 2, label.Width, label.Height);
                g.DrawImage(label, dest, 0, 0, label.Width, label.Height, GraphicsUnit.Pixel, ia);
            }

        // Words.
        double title = Fade(t, 0.2, 2.5);
        CenterText(g, "StageStrip", 76f, FontStyle.Bold, 390, title, Color.White);
        CenterText(g, "macOS Stage Manager's workflow, on Windows", 28f, FontStyle.Regular, 520, title, Color.White);
        DrawCaption(g, "Too many windows?", Fade(t, 3.2, 4.9));
        DrawCaption(g, "One app on stage. The rest wait on the side.", Fade(t, 5.2, 9.3));
        DrawCaption(g, "Click to switch.", Fade(t, 9.5, 11.5));
        DrawCaption(g, "Drag to use apps together.", Fade(t, 11.7, 14.5));
        DrawCaption(g, "Click the desktop to clear it. Click again to bring it back.", Fade(t, 14.8, 17.3));

        if (t >= 7.8 && t < 17.2)
        {
            bool pressed = Math.Abs(t - 9.6) < 0.1 || (t > 11.8 && t < 12.5) || Math.Abs(t - 15) < 0.1 || Math.Abs(t - 16.3) < 0.1;
            DrawCursor(g, CursorAt(t), pressed);
        }

        // End card.
        double end = Ease((t - 17.3) / 0.5);
        if (end > 0)
        {
            using (var cover = new SolidBrush(Color.FromArgb((int)(252 * end), 16, 18, 30))) g.FillRectangle(cover, 0, 0, W, H);
            CenterText(g, "A single .exe. Nothing to install.", 54f, FontStyle.Bold, 360, end, Color.White);
            CenterText(g, "github.com/hasanalisiseci/stagestrip", 32f, FontStyle.Regular, 470, end, Color.FromArgb(150, 195, 255));
            CenterText(g, "Built with Claude", 24f, FontStyle.Regular, 590, end, Color.FromArgb(200, 200, 210));
        }
    }

    // ---- Media Foundation ----

    // IMFMediaType's IID: a media type only answers to that, and its methods begin with IMFAttributes'.
    [ComImport, Guid("44ae0fa8-ea31-4109-8d2e-4cae4997c555"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFAttributes
    {
        void GetItem(); void GetItemType(); void CompareItem(); void Compare(); void GetUINT32(); void GetUINT64(); void GetDouble(); void GetGUID();
        void GetStringLength(); void GetString(); void GetAllocatedString(); void GetBlobSize(); void GetBlob(); void GetAllocatedBlob(); void GetUnknown();
        void SetItem(); void DeleteItem(); void DeleteAllItems();
        void SetUINT32([MarshalAs(UnmanagedType.LPStruct)] Guid key, int value);
        void SetUINT64([MarshalAs(UnmanagedType.LPStruct)] Guid key, long value);
        void SetDouble();
        void SetGUID([MarshalAs(UnmanagedType.LPStruct)] Guid key, [MarshalAs(UnmanagedType.LPStruct)] Guid value);
    }

    [ComImport, Guid("c40a00f2-b93a-4d80-ae8c-5a1c634f58e4"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSample
    {
        void A01(); void A02(); void A03(); void A04(); void A05(); void A06(); void A07(); void A08(); void A09(); void A10();
        void A11(); void A12(); void A13(); void A14(); void A15(); void A16(); void A17(); void A18(); void A19(); void A20();
        void A21(); void A22(); void A23(); void A24(); void A25(); void A26(); void A27(); void A28(); void A29(); void A30();
        void GetSampleFlags(); void SetSampleFlags();
        void GetSampleTime(); void SetSampleTime(long time);
        void GetSampleDuration(); void SetSampleDuration(long duration);
        void GetBufferCount(); void GetBufferByIndex(); void ConvertToContiguousBuffer();
        void AddBuffer(IMFMediaBuffer buffer);
    }

    [ComImport, Guid("045FA593-8799-42b8-BC8D-8968C6453507"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFMediaBuffer
    {
        void Lock(out IntPtr data, out int max, out int current);
        void Unlock();
        void GetCurrentLength();
        void SetCurrentLength(int length);
    }

    [ComImport, Guid("3137f1cd-fe5e-4805-a5d8-fb477448cb3d"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IMFSinkWriter
    {
        void AddStream([MarshalAs(UnmanagedType.Interface)] IMFAttributes type, out int stream);
        void SetInputMediaType(int stream, [MarshalAs(UnmanagedType.Interface)] IMFAttributes type, IntPtr parameters);
        void BeginWriting();
        void WriteSample(int stream, IMFSample sample);
        void SendStreamTick(); void PlaceMarker(); void NotifyEndOfSegment(); void Flush();
        void Finalize_();
    }

    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] static extern void MFStartup(int version, int flags);
    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] static extern void MFShutdown();
    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] static extern void MFCreateMediaType([MarshalAs(UnmanagedType.Interface)] out IMFAttributes type);
    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] static extern void MFCreateSample(out IMFSample sample);
    [DllImport("mfplat.dll", ExactSpelling = true, PreserveSig = false)] static extern void MFCreateMemoryBuffer(int size, out IMFMediaBuffer buffer);
    [DllImport("mfreadwrite.dll", ExactSpelling = true, PreserveSig = false)] static extern void MFCreateSinkWriterFromURL([MarshalAs(UnmanagedType.LPWStr)] string url, IntPtr stream, IntPtr attributes, out IMFSinkWriter writer);

    static readonly Guid MajorType = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f"), SubType = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5"),
        Bitrate = new Guid("20332624-fb0d-4d9e-bd0d-cbf6786c102e"), Interlace = new Guid("e2724bb8-e676-4806-b4b2-a8d6efb44ccd"),
        FrameSize = new Guid("1652c33d-d6b2-4012-b834-72030849a37d"), FrameRate = new Guid("c459a2e8-3d2c-4e44-b132-fee5156c7bb0"),
        PixelAspect = new Guid("c6376a1e-8d0a-4027-be45-6d9a0ad39bb6"), Video = new Guid("73646976-0000-0010-8000-00AA00389B71"),
        H264 = new Guid("34363248-0000-0010-8000-00AA00389B71"), Rgb32 = new Guid("00000016-0000-0010-8000-00AA00389B71");

    static IMFAttributes VideoType(Guid subtype)
    {
        IMFAttributes t;
        MFCreateMediaType(out t);
        t.SetGUID(MajorType, Video);
        t.SetGUID(SubType, subtype);
        t.SetUINT32(Interlace, 2);   // progressive
        t.SetUINT64(FrameSize, ((long)W << 32) | H);
        t.SetUINT64(FrameRate, ((long)Fps << 32) | 1);
        t.SetUINT64(PixelAspect, (1L << 32) | 1);
        return t;
    }

    // ---- GIF: a shared palette from sample frames, then LZW-compressed frames ----

    static byte[] BuildPalette(List<Bitmap> samples)
    {
        var colors = new List<int>();
        foreach (var s in samples)
            for (int y = 0; y < s.Height; y += 3)
                for (int x = 0; x < s.Width; x += 3) colors.Add(s.GetPixel(x, y).ToArgb() & 0xFFFFFF);
        var boxes = new List<List<int>> { colors };
        while (boxes.Count < 256)
        {
            var box = boxes.OrderByDescending(b => b.Count * Spread(b)).First();
            if (box.Count < 2) break;
            int shift = WidestChannel(box);
            var sorted = box.OrderBy(c => (c >> shift) & 255).ToList();
            boxes.Remove(box);
            boxes.Add(sorted.Take(sorted.Count / 2).ToList());
            boxes.Add(sorted.Skip(sorted.Count / 2).ToList());
        }
        var palette = new byte[256 * 3];
        for (int i = 0; i < boxes.Count; i++)
        {
            var b = boxes[i];
            palette[i * 3] = (byte)b.Average(c => (c >> 16) & 255);
            palette[i * 3 + 1] = (byte)b.Average(c => (c >> 8) & 255);
            palette[i * 3 + 2] = (byte)b.Average(c => c & 255);
        }
        return palette;
    }

    static int Range(List<int> b, int shift) { return b.Max(c => (c >> shift) & 255) - b.Min(c => (c >> shift) & 255); }
    static int Spread(List<int> b) { return Math.Max(Range(b, 16), Math.Max(Range(b, 8), Range(b, 0))); }
    static int WidestChannel(List<int> b)
    {
        int r = Range(b, 16), g = Range(b, 8), bl = Range(b, 0);
        return r >= g && r >= bl ? 16 : g >= bl ? 8 : 0;
    }

    class GifWriter : IDisposable
    {
        readonly BinaryWriter o;
        readonly byte[] palette;
        readonly byte[] lookup = new byte[1 << 15];
        readonly bool[] known = new bool[1 << 15];
        byte[] previous;

        public GifWriter(string path, byte[] palette, int delayCs)
        {
            this.palette = palette;
            this.delay = delayCs;
            o = new BinaryWriter(File.Create(path));
            o.Write(System.Text.Encoding.ASCII.GetBytes("GIF89a"));
            o.Write((short)GifW); o.Write((short)GifH);
            o.Write((byte)0xF7); o.Write((byte)0); o.Write((byte)0);
            o.Write(palette);
            o.Write(new byte[] { 0x21, 0xFF, 11 });
            o.Write(System.Text.Encoding.ASCII.GetBytes("NETSCAPE2.0"));
            o.Write(new byte[] { 3, 1, 0, 0, 0 });
        }

        readonly int delay;

        byte Nearest(int r, int g, int b)
        {
            int key = ((r >> 3) << 10) | ((g >> 3) << 5) | (b >> 3);
            if (known[key]) return lookup[key];
            int best = 0, bestD = int.MaxValue;
            for (int i = 0; i < 256; i++)
            {
                int dr = palette[i * 3] - r, dg = palette[i * 3 + 1] - g, db = palette[i * 3 + 2] - b;
                int d = dr * dr * 2 + dg * dg * 4 + db * db;
                if (d < bestD) { bestD = d; best = i; }
            }
            known[key] = true;
            lookup[key] = (byte)best;
            return (byte)best;
        }

        public void Add(Bitmap frame)
        {
            var data = frame.LockBits(new Rectangle(0, 0, GifW, GifH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
            var raw = new int[GifW * GifH];
            Marshal.Copy(data.Scan0, raw, 0, raw.Length);
            frame.UnlockBits(data);
            var idx = new byte[raw.Length];
            for (int i = 0; i < raw.Length; i++) idx[i] = Nearest((raw[i] >> 16) & 255, (raw[i] >> 8) & 255, raw[i] & 255);

            // Only the changed rectangle is stored; a still frame costs almost nothing.
            int x0 = 0, y0 = 0, x1 = GifW - 1, y1 = GifH - 1;
            if (previous != null)
            {
                x0 = GifW; y0 = GifH; x1 = 0; y1 = 0;
                for (int y = 0; y < GifH; y++)
                    for (int x = 0; x < GifW; x++)
                        if (idx[x + y * GifW] != previous[x + y * GifW])
                        {
                            if (x < x0) x0 = x; if (x > x1) x1 = x; if (y < y0) y0 = y; if (y > y1) y1 = y;
                        }
                if (x0 > x1) { x0 = 0; y0 = 0; x1 = 0; y1 = 0; }
            }
            previous = idx;

            o.Write(new byte[] { 0x21, 0xF9, 4, 0 }); o.Write((short)delay); o.Write((byte)0); o.Write((byte)0);
            o.Write((byte)0x2C); o.Write((short)x0); o.Write((short)y0); o.Write((short)(x1 - x0 + 1)); o.Write((short)(y1 - y0 + 1)); o.Write((byte)0);
            var pixels = new byte[(x1 - x0 + 1) * (y1 - y0 + 1)];
            for (int y = y0, n = 0; y <= y1; y++)
                for (int x = x0; x <= x1; x++) pixels[n++] = idx[x + y * GifW];
            Lzw(pixels);
        }

        void Lzw(byte[] pixels)
        {
            const int minCode = 8, clear = 256, eoi = 257;
            o.Write((byte)minCode);
            var output = new List<byte>(pixels.Length / 2);
            int bits = 0, count = 0;
            int size = minCode + 1, next = eoi + 1;
            var table = new Dictionary<int, int>();
            Action<int> emit = code =>
            {
                bits |= code << count;
                count += size;
                while (count >= 8) { output.Add((byte)bits); bits >>= 8; count -= 8; }
            };
            emit(clear);
            int current = pixels[0];
            for (int i = 1; i < pixels.Length; i++)
            {
                int key = (current << 8) | pixels[i], found;
                if (table.TryGetValue(key, out found)) { current = found; continue; }
                emit(current);
                if (next == 4096)
                {
                    emit(clear);
                    table.Clear();
                    size = minCode + 1;
                    next = eoi + 1;
                }
                else
                {
                    if (next == (1 << size)) size++;
                    table[key] = next++;
                }
                current = pixels[i];
            }
            emit(current);
            emit(eoi);
            if (count > 0) output.Add((byte)bits);
            for (int p = 0; p < output.Count; p += 255)
            {
                int n = Math.Min(255, output.Count - p);
                o.Write((byte)n);
                o.Write(output.GetRange(p, n).ToArray());
            }
            o.Write((byte)0);
        }

        public void Dispose()
        {
            o.Write((byte)0x3B);
            o.Dispose();
        }
    }

    // ---- Output ----

    [STAThread]
    static int Main(string[] args)
    {
        try { Run(args); return 0; }
        catch (Exception e)
        {
            File.WriteAllText(Path.Combine(args[1], "demo-error.txt"), e.ToString());
            return 1;
        }
    }

    static void Run(string[] args)
    {
        string docs = args[0], shots = args[1];
        var actors = BuildActors();
        int total = (int)(Length * Fps);
        double[] keyframes = { 1.2, 4.6, 5.3, 7, 9.85, 11.4, 12.4, 14, 15.2, 16, 19.5 };

        MFStartup(0x20070, 0);
        IMFSinkWriter writer;
        string mp4 = Path.Combine(docs, "demo.mp4");
        if (File.Exists(mp4)) File.Delete(mp4);
        MFCreateSinkWriterFromURL(mp4, IntPtr.Zero, IntPtr.Zero, out writer);
        IMFAttributes outType = VideoType(H264);
        outType.SetUINT32(Bitrate, 8000000);
        int stream;
        writer.AddStream(outType, out stream);
        writer.SetInputMediaType(stream, VideoType(Rgb32), IntPtr.Zero);
        writer.BeginWriting();

        long frameTime = 10000000L / Fps;
        using (var canvas = new Bitmap(W, H, PixelFormat.Format32bppPArgb))
        {
            // The GIF palette comes from moments spread across the whole video.
            var samples = new List<Bitmap>();
            foreach (double t in keyframes)
            {
                using (var g = Graphics.FromImage(canvas)) DrawFrame(g, t, actors);
                var small = new Bitmap(GifW, GifH, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(small)) { g.InterpolationMode = InterpolationMode.HighQualityBilinear; g.DrawImage(canvas, 0, 0, GifW, GifH); }
                samples.Add(small);
                canvas.Save(Path.Combine(shots, "key-" + t.ToString("00.00", System.Globalization.CultureInfo.InvariantCulture) + ".png"), ImageFormat.Png);
            }
            byte[] palette = BuildPalette(samples);
            samples.ForEach(s => s.Dispose());

            using (var gif = new GifWriter(Path.Combine(docs, "demo.gif"), palette, 10))
            using (var small = new Bitmap(GifW, GifH, PixelFormat.Format32bppArgb))
            {
                for (int f = 0; f < total; f++)
                {
                    using (var g = Graphics.FromImage(canvas)) DrawFrame(g, (double)f / Fps, actors);
                    IMFMediaBuffer buffer;
                    int bytes = W * H * 4;
                    MFCreateMemoryBuffer(bytes, out buffer);
                    IntPtr dest; int max, cur;
                    buffer.Lock(out dest, out max, out cur);
                    var data = canvas.LockBits(new Rectangle(0, 0, W, H), ImageLockMode.ReadOnly, PixelFormat.Format32bppRgb);
                    var row = new byte[W * 4];
                    for (int y = 0; y < H; y++)
                    {
                        // RGB32 video is stored bottom-up.
                        Marshal.Copy(data.Scan0 + (H - 1 - y) * data.Stride, row, 0, row.Length);
                        Marshal.Copy(row, 0, dest + y * row.Length, row.Length);
                    }
                    canvas.UnlockBits(data);
                    buffer.Unlock();
                    buffer.SetCurrentLength(bytes);

                    IMFSample sample;
                    MFCreateSample(out sample);
                    sample.AddBuffer(buffer);
                    sample.SetSampleTime(f * frameTime);
                    sample.SetSampleDuration(frameTime);
                    writer.WriteSample(stream, sample);
                    Marshal.ReleaseComObject(sample);
                    Marshal.ReleaseComObject(buffer);

                    if (f % 3 == 0)   // the GIF runs at 10 fps
                    {
                        using (var g = Graphics.FromImage(small))
                        {
                            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                            g.DrawImage(canvas, 0, 0, GifW, GifH);
                        }
                        gif.Add(small);
                    }
                }
            }
        }
        writer.Finalize_();
        MFShutdown();
    }
}
