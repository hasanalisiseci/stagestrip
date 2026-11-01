// Times one strip animation frame without opening any window. Usage: FrameBench.exe
// Prints to stdout-less winexe safely by writing to the path given as the first argument.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using StageManagerWin;

static class FrameBench
{
    static void Main(string[] args)
    {
        var tiles = new List<StripRenderer.TileArt>();
        using (var src = new Bitmap(1600, 1000, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(src))
            {
                g.Clear(Color.FromArgb(40, 40, 46));
                g.FillRectangle(Brushes.SteelBlue, 200, 100, 1200, 300);
            }
            for (int i = 0; i < 8; i++)
            {
                var t = new StripRenderer.TileArt { Reveal = 0.6f, Icon = StripRenderer.PrepareIcon(src) };
                t.Layers.Add(StripRenderer.Prepare(src, Rectangle.Empty));
                tiles.Add(t);
            }
        }
        using (var canvas = new Bitmap(230, 1040, PixelFormat.Format32bppPArgb))
        using (var writer = new StreamWriter(args[0]))
        {
            foreach (bool moving in new[] { false, true })
            {
                var clock = Stopwatch.StartNew();
                for (int frame = 0; frame < 60; frame++)
                {
                    using (var g = Graphics.FromImage(canvas))
                    {
                        g.Clear(Color.Transparent);
                        for (int i = 0; i < tiles.Count; i++)
                        {
                            tiles[i].Moving = moving;
                            StripRenderer.DrawTile(g, tiles[i], 30, 16 + i * 124);
                        }
                    }
                }
                writer.WriteLine((moving ? "hizli cizim" : "tam kalite") + ": " + (clock.ElapsedMilliseconds / 60.0).ToString("0.0") + " ms/kare (8 kart)");
            }
        }
    }
}
