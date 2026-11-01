// Draws the strip to PNG files with no windows at all, so the look can be judged on a busy machine.
// Usage: RenderPreview.exe <output-folder>
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using StageManagerWin;

static class RenderPreview
{
    static Bitmap FakeWindow(Color accent, bool dark)
    {
        var b = new Bitmap(1200, 800, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(b))
        using (var font = new Font("Segoe UI", 26f))
        {
            Color bg = dark ? Color.FromArgb(30, 30, 34) : Color.FromArgb(250, 250, 251);
            Color bar = dark ? Color.FromArgb(44, 44, 50) : Color.FromArgb(236, 236, 240);
            Color line = dark ? Color.FromArgb(70, 70, 78) : Color.FromArgb(215, 215, 222);
            g.Clear(bg);
            using (var s = new SolidBrush(bar)) g.FillRectangle(s, 0, 0, 1200, 70);
            using (var s = new SolidBrush(accent)) { g.FillRectangle(s, 0, 70, 260, 730); g.FillRectangle(s, 300, 110, 860, 240); }
            using (var s = new SolidBrush(line))
                for (int y = 390; y < 780; y += 58) g.FillRectangle(s, 300, y, 860 - (y % 7) * 60, 26);
            using (var s = new SolidBrush(dark ? Color.White : Color.Black)) g.DrawString("Uygulama", font, s, 20, 14);
        }
        return b;
    }

    static Bitmap DetailWindow()
    {
        var b = new Bitmap(1920, 1040, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(b))
        using (var font = new Font("Segoe UI", 11f))
        using (var text = new SolidBrush(Color.FromArgb(225, 225, 225)))
        using (var bar = new SolidBrush(Color.FromArgb(44, 44, 44)))
        using (var folder = new SolidBrush(Color.FromArgb(245, 200, 80)))
        {
            g.Clear(Color.FromArgb(25, 25, 25));
            g.FillRectangle(bar, 0, 0, 1920, 90);
            g.DrawString("Bu bilgisayar  >  Belgeler  >  Projeler", font, text, 340, 45);
            for (int y = 110; y < 1020; y += 30) g.DrawString("Hızlı erişim klasörü", font, text, 20, y);
            for (int y = 120; y < 700; y += 34)
            {
                g.FillRectangle(folder, 340, y + 3, 20, 16);
                g.DrawString("Klasör adı " + y + "     1.11.2026 02:30     Dosya klasörü", font, text, 370, y);
            }
        }
        return b;
    }

    static Bitmap FakeIcon(Color c)
    {
        var b = new Bitmap(64, 64, PixelFormat.Format32bppPArgb);
        using (var g = Graphics.FromImage(b))
        using (var s = new SolidBrush(c))
        using (var path = new GraphicsPath())
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            path.AddArc(2, 2, 22, 22, 180, 90); path.AddArc(40, 2, 22, 22, 270, 90);
            path.AddArc(40, 40, 22, 22, 0, 90); path.AddArc(2, 40, 22, 22, 90, 90);
            path.CloseFigure();
            g.FillPath(s, path);
            g.FillEllipse(Brushes.White, 20, 20, 24, 24);
        }
        using (b) return StripRenderer.PrepareIcon(b);
    }

    static void Wallpaper(Graphics g, Rectangle r, Color a, Color b)
    {
        using (var brush = new LinearGradientBrush(r, a, b, 70f)) g.FillRectangle(brush, r);
    }

    static Bitmap Art(Color accent, bool dark, Size size)
    {
        using (var src = FakeWindow(accent, dark))
        using (var scaled = new Bitmap(src, size))
            return StripRenderer.Prepare(scaled, Rectangle.Empty);
    }

    static void Main(string[] args)
    {
        string dir = args[0];
        var tiles = new List<StripRenderer.TileArt> {
            new StripRenderer.TileArt { Reveal = 1, Icon = FakeIcon(Color.FromArgb(66, 133, 244)), Layers = { Art(Color.FromArgb(66, 133, 244), false, new Size(1200, 800)) } },
            new StripRenderer.TileArt { Reveal = 1, Hover = 1, Icon = FakeIcon(Color.FromArgb(234, 88, 60)), Layers = { Art(Color.FromArgb(234, 88, 60), true, new Size(1200, 800)) } },
            new StripRenderer.TileArt { Reveal = 1, Icon = FakeIcon(Color.FromArgb(40, 170, 110)), Layers = { Art(Color.FromArgb(150, 90, 220), false, new Size(1200, 800)), Art(Color.FromArgb(40, 170, 110), false, new Size(1200, 800)), Art(Color.FromArgb(40, 170, 110), true, new Size(1200, 800)) } },
            new StripRenderer.TileArt { Reveal = 1, Icon = FakeIcon(Color.FromArgb(245, 180, 40)), Layers = { Art(Color.FromArgb(245, 180, 40), false, new Size(600, 900)) } },
        };

        // Small text on a dark window at full-screen size: the hardest case for looking pixelated.
        // Shown 1:1 and enlarged 3x without smoothing, so every real pixel can be judged.
        using (var src = DetailWindow())
        using (var art = StripRenderer.Prepare(src, Rectangle.Empty))
        using (var b = new Bitmap(art.Width * 4 + 40, art.Height * 3 + 20, PixelFormat.Format32bppPArgb))
        using (var g = Graphics.FromImage(b))
        {
            Wallpaper(g, new Rectangle(0, 0, b.Width, b.Height), Color.FromArgb(150, 200, 235), Color.FromArgb(120, 170, 90));
            g.DrawImage(art, 10, 10, art.Width, art.Height);
            g.InterpolationMode = InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            g.DrawImage(art, art.Width + 20, 10, art.Width * 3, art.Height * 3);
            b.Save(Path.Combine(dir, "preview-detail.png"), ImageFormat.Png);
        }

        using (var tag = StripRenderer.RenderLabel("Bu bilgisayar - Dosya Gezgini\nAnasayfa / X - Google Chrome"))
        using (var b = new Bitmap(tag.Width + 40, tag.Height + 40, PixelFormat.Format32bppPArgb))
        using (var g = Graphics.FromImage(b))
        {
            Wallpaper(g, new Rectangle(0, 0, b.Width, b.Height), Color.FromArgb(150, 200, 235), Color.FromArgb(120, 170, 90));
            g.DrawImage(tag, 20, 20, tag.Width, tag.Height);
            b.Save(Path.Combine(dir, "preview-label.png"), ImageFormat.Png);
        }

        // Light wallpaper, dark wallpaper, and one mid-slide frame.
        var scenes = new[] {
            new { Name = "preview-light.png", A = Color.FromArgb(214, 226, 240), B = Color.FromArgb(160, 190, 225), Reveal = 1f },
            new { Name = "preview-dark.png", A = Color.FromArgb(30, 34, 52), B = Color.FromArgb(10, 12, 22), Reveal = 1f },
            new { Name = "preview-slide.png", A = Color.FromArgb(70, 90, 140), B = Color.FromArgb(40, 50, 90), Reveal = 0.45f },
        };
        foreach (var scene in scenes)
        {
            using (var b = new Bitmap(280, 760, PixelFormat.Format32bppPArgb))
            using (var g = Graphics.FromImage(b))
            {
                Wallpaper(g, new Rectangle(0, 0, b.Width, b.Height), scene.A, scene.B);
                int y = 40;
                for (int i = 0; i < tiles.Count; i++)
                {
                    tiles[i].Reveal = Math.Min(1, scene.Reveal + 0.12f * (tiles.Count - i - 1) * (scene.Reveal < 1 ? 1 : 0));
                    StripRenderer.DrawTile(g, tiles[i], 24, y);
                    y += StripRenderer.ArtHeight + 70;
                }
                b.Save(Path.Combine(dir, scene.Name), ImageFormat.Png);
            }
        }
    }
}
