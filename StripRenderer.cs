using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace StageManagerWin
{
    // Draws preview art without touching any window, so the look can be checked from a PNG.
    // All bitmaps are 32bpp premultiplied, ready for a per-pixel-alpha layered window.
    static class StripRenderer
    {
        public const int ArtWidth = 170, ArtHeight = 112, ShadowPad = 22;
        const float EdgeSlope = 0.06f;        // top and bottom edges drop this much per pixel of width, for every window shape
        const float MinFarEdgeScale = 0.5f;   // keeps extremely wide windows from collapsing to a point
        const float CornerRadius = 7f;        // in source-window pixels at art size
        const int Supersample = 4, ShadowRadius = 9, ShadowDrop = 7;
        const float ShadowStrength = 0.42f, HairlineStrength = 0.20f, SheenStrength = 0.07f;

        // A window image in perspective with rounded corners, a hairline edge and a soft shadow.
        public static Bitmap Prepare(Bitmap source, Rectangle part)
        {
            if (part.IsEmpty) part = new Rectangle(0, 0, source.Width, source.Height);
            double scale = Math.Min((double)ArtWidth / part.Width, (double)ArtHeight / part.Height);
            int w = Math.Max(8, (int)(part.Width * scale)), h = Math.Max(8, (int)(part.Height * scale));

            int[] flat = Resample(source, part, w * Supersample, h * Supersample);
            int[] warped = Warp(flat, w * Supersample, h * Supersample);
            int[] small = Shrink(warped, w * Supersample, h * Supersample, Supersample);
            return WithShadow(small, w, h, ShadowPad, ShadowDrop, ShadowRadius, ShadowStrength);
        }

        static int[] Resample(Bitmap source, Rectangle part, int w, int h)
        {
            using (var b = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(b))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    using (var wrap = new ImageAttributes())
                    {
                        wrap.SetWrapMode(WrapMode.TileFlipXY);
                        g.DrawImage(source, new Rectangle(0, 0, w, h), part.X, part.Y, part.Width, part.Height, GraphicsUnit.Pixel, wrap);
                    }
                }
                return ToPixels(b);
            }
        }

        // Inverse mapping: for each output pixel, find the source point under true perspective,
        // so content compresses toward the far edge instead of only the outline narrowing.
        static int[] Warp(int[] src, int w, int h)
        {
            var dst = new int[w * h];
            float r = Math.Max(MinFarEdgeScale, 1 - 2 * EdgeSlope * w / h);
            float radius = CornerRadius * Supersample;
            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    float f = (x + 0.5f) / w;
                    float depth = (1 - f) + f * r;              // on-screen height at this column
                    float u = (f / r) / ((1 - f) + f / r);      // perspective-correct position
                    float v = ((y + 0.5f) / h - 0.5f) / depth + 0.5f;
                    if (v < 0 || v > 1) continue;

                    float sx = u * w, sy = v * h;
                    float d = RoundedDistance(sx, sy, w, h, radius);
                    if (d > 0) continue;

                    int color = Bilinear(src, w, h, sx - 0.5f, sy - 0.5f);
                    // Light catching the near top corner, and a fine edge to separate from any wallpaper.
                    float sheen = SheenStrength * (1 - f) * (1 - v) * (1 - v);
                    float line = d > -1.2f * Supersample ? HairlineStrength : 0;
                    dst[x + y * w] = Lighten(color, Math.Min(1, sheen + line));
                }
            }
            return dst;
        }

        static int Bilinear(int[] src, int w, int h, float x, float y)
        {
            int x0 = Clamp((int)Math.Floor(x), w), y0 = Clamp((int)Math.Floor(y), h);
            int x1 = Clamp(x0 + 1, w), y1 = Clamp(y0 + 1, h);
            int fx = (int)((x - (float)Math.Floor(x)) * 256), fy = (int)((y - (float)Math.Floor(y)) * 256);
            int c00 = src[x0 + y0 * w], c10 = src[x1 + y0 * w], c01 = src[x0 + y1 * w], c11 = src[x1 + y1 * w];
            int result = 0;
            for (int shift = 0; shift < 32; shift += 8)
            {
                int top = ((c00 >> shift) & 255) * (256 - fx) + ((c10 >> shift) & 255) * fx;
                int low = ((c01 >> shift) & 255) * (256 - fx) + ((c11 >> shift) & 255) * fx;
                result |= ((top * (256 - fy) + low * fy) >> 16) << shift;
            }
            return result;
        }

        static float RoundedDistance(float x, float y, float w, float h, float radius)
        {
            float qx = Math.Abs(x - w / 2) - (w / 2 - radius);
            float qy = Math.Abs(y - h / 2) - (h / 2 - radius);
            float ox = Math.Max(qx, 0), oy = Math.Max(qy, 0);
            return (float)Math.Sqrt(ox * ox + oy * oy) + Math.Min(Math.Max(qx, qy), 0) - radius;
        }

        static int Lighten(int c, float amount)
        {
            int a = (c >> 24) & 255, r = (c >> 16) & 255, g = (c >> 8) & 255, b = c & 255;
            r += (int)((255 - r) * amount);
            g += (int)((255 - g) * amount);
            b += (int)((255 - b) * amount);
            return (a << 24) | (r << 16) | (g << 8) | b;
        }

        static int[] Shrink(int[] src, int w, int h, int by)
        {
            int ow = w / by, oh = h / by;
            var dst = new int[ow * oh];
            int count = by * by;
            for (int y = 0; y < oh; y++)
            {
                for (int x = 0; x < ow; x++)
                {
                    int a = 0, r = 0, g = 0, b = 0;
                    for (int j = 0; j < by; j++)
                    {
                        for (int i = 0; i < by; i++)
                        {
                            int c = src[x * by + i + (y * by + j) * w];
                            a += (c >> 24) & 255; r += (c >> 16) & 255; g += (c >> 8) & 255; b += c & 255;
                        }
                    }
                    dst[x + y * ow] = ((a / count) << 24) | ((r / count) << 16) | ((g / count) << 8) | (b / count);
                }
            }
            return dst;
        }

        static Bitmap WithShadow(int[] art, int w, int h, int pad, int drop, int radius, float strength)
        {
            int ow = w + pad * 2, oh = h + pad * 2;
            var alpha = new int[ow * oh];
            for (int y = 0; y < h; y++)
            {
                int ty = y + pad + drop;
                if (ty >= oh) continue;
                for (int x = 0; x < w; x++) alpha[x + pad + ty * ow] = (art[x + y * w] >> 24) & 255;
            }
            for (int pass = 0; pass < 3; pass++) alpha = BoxBlur(alpha, ow, oh, Math.Max(1, radius / 2));

            var result = new int[ow * oh];
            for (int i = 0; i < result.Length; i++)
                result[i] = (int)(alpha[i] * strength) << 24;   // black, so premultiplied RGB stays 0

            for (int y = 0; y < h; y++)
            {
                for (int x = 0; x < w; x++)
                {
                    int c = art[x + y * w];
                    int a = (c >> 24) & 255;
                    int i = x + pad + (y + pad) * ow;
                    result[i] = Over(c, a, result[i]);
                }
            }
            return FromPixels(result, ow, oh);
        }

        static int Over(int top, int topAlpha, int under)
        {
            int inv = 255 - topAlpha;
            int a = topAlpha + ((under >> 24) & 255) * inv / 255;
            int r = ((top >> 16) & 255) + ((under >> 16) & 255) * inv / 255;
            int g = ((top >> 8) & 255) + ((under >> 8) & 255) * inv / 255;
            int b = (top & 255) + (under & 255) * inv / 255;
            return (a << 24) | (r << 16) | (g << 8) | b;
        }

        static int[] BoxBlur(int[] src, int w, int h, int radius)
        {
            var tmp = new int[src.Length];
            var dst = new int[src.Length];
            int size = radius * 2 + 1;
            for (int y = 0; y < h; y++)
            {
                int sum = 0;
                for (int x = -radius; x <= radius; x++) sum += src[Clamp(x, w) + y * w];
                for (int x = 0; x < w; x++)
                {
                    tmp[x + y * w] = sum / size;
                    sum += src[Clamp(x + radius + 1, w) + y * w] - src[Clamp(x - radius, w) + y * w];
                }
            }
            for (int x = 0; x < w; x++)
            {
                int sum = 0;
                for (int y = -radius; y <= radius; y++) sum += tmp[x + Clamp(y, h) * w];
                for (int y = 0; y < h; y++)
                {
                    dst[x + y * w] = sum / size;
                    sum += tmp[x + Clamp(y + radius + 1, h) * w] - tmp[x + Clamp(y - radius, h) * w];
                }
            }
            return dst;
        }

        static int Clamp(int v, int size) { return v < 0 ? 0 : v >= size ? size - 1 : v; }

        static int[] ToPixels(Bitmap b)
        {
            var data = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            var pixels = new int[b.Width * b.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
            b.UnlockBits(data);
            return pixels;
        }

        static Bitmap FromPixels(int[] pixels, int w, int h)
        {
            var b = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            var data = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
            b.UnlockBits(data);
            return b;
        }

        // ---- Composing a tile ----

        public class TileArt
        {
            public List<Bitmap> Layers = new List<Bitmap>();   // back to front
            public Bitmap Icon;
            public float Hover;     // 0..1, eased by the caller
            public float Reveal;    // 0..1: slides from the screen edge and fades in
            public bool Moving;     // mid-animation: favour speed, nobody can see fine detail in motion
        }

        const int IconSize = 30, IconOverlap = 12, LayerStep = 9, IconShadowPad = 8;

        // Draws one tile with its top-left content corner at (x, y).
        public static void DrawTile(Graphics g, TileArt tile, int x, int y)
        {
            if (tile.Reveal <= 0 || tile.Layers.Count == 0) return;
            float e = 1 - (float)Math.Pow(1 - tile.Reveal, 3);
            float slide = (1 - e) * -(ArtWidth + 40);
            float grow = 1 + 0.07f * tile.Hover;

            g.InterpolationMode = tile.Moving ? InterpolationMode.Bilinear : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = tile.Moving ? PixelOffsetMode.None : PixelOffsetMode.HighQuality;
            int n = tile.Layers.Count;
            for (int i = 0; i < n; i++)
            {
                int back = n - 1 - i;
                Bitmap art = tile.Layers[i];
                float s = grow * (1 - 0.07f * back);
                float w = art.Width * s, h = art.Height * s;
                float cx = x + slide + ArtWidth / 2f + back * LayerStep * (1 + tile.Hover);
                float cy = y + ArtHeight / 2f - back * LayerStep * 0.55f * (1 + tile.Hover);
                float dim = 1 - 0.14f * back;
                var dest = Rectangle.Round(new RectangleF(cx - w / 2, cy - h / 2, w, h));
                if (dim == 1 && e == 1)
                {
                    // The common case: colour processing is by far the slowest part of a draw.
                    g.DrawImage(art, dest, 0, 0, art.Width, art.Height, GraphicsUnit.Pixel);
                    continue;
                }
                using (var ia = new ImageAttributes())
                {
                    ia.SetColorMatrix(new ColorMatrix { Matrix00 = dim, Matrix11 = dim, Matrix22 = dim, Matrix33 = e });
                    g.DrawImage(art, dest, 0, 0, art.Width, art.Height, GraphicsUnit.Pixel, ia);
                }
            }

            if (tile.Icon == null) return;
            using (var ia = new ImageAttributes())
            {
                ia.SetColorMatrix(new ColorMatrix { Matrix33 = e });
                // Sits on the front window's near bottom corner, wherever that window's shape puts it.
                Bitmap front = tile.Layers[n - 1];
                float frontW = (front.Width - ShadowPad * 2) * grow, frontH = (front.Height - ShadowPad * 2) * grow;
                float left = x + slide + ArtWidth / 2f - frontW / 2;
                float bottom = y + ArtHeight / 2f + frontH / 2;
                float size = tile.Icon.Width * grow, pad = IconShadowPad * grow;
                var dest = Rectangle.Round(new RectangleF(left - pad - 6, bottom - pad - IconSize * grow + IconOverlap, size, size));
                g.DrawImage(tile.Icon, dest, 0, 0, tile.Icon.Width, tile.Icon.Height, GraphicsUnit.Pixel, ia);
            }
        }

        // The name tag: a smooth-edged dark pill with a soft shadow.
        public static Bitmap RenderLabel(string text)
        {
            const int padX = 12, padY = 7, radius = 9;
            using (var font = new Font("Segoe UI", 9.5f))
            {
                Size measured;
                using (var probe = new Bitmap(1, 1))
                using (var pg = Graphics.FromImage(probe))
                    measured = Size.Ceiling(pg.MeasureString(text, font));
                int w = measured.Width + padX * 2, h = measured.Height + padY * 2;
                using (var b = new Bitmap(w, h, PixelFormat.Format32bppPArgb))
                using (var g = Graphics.FromImage(b))
                using (var path = new GraphicsPath())
                using (var fill = new SolidBrush(Color.FromArgb(240, 34, 34, 38)))
                using (var edge = new Pen(Color.FromArgb(46, 255, 255, 255)))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
                    int d = radius * 2;
                    path.AddArc(0, 0, d, d, 180, 90);
                    path.AddArc(w - d - 1, 0, d, d, 270, 90);
                    path.AddArc(w - d - 1, h - d - 1, d, d, 0, 90);
                    path.AddArc(0, h - d - 1, d, d, 90, 90);
                    path.CloseFigure();
                    g.FillPath(fill, path);
                    g.DrawPath(edge, path);
                    g.DrawString(text, font, Brushes.White, padX, padY);
                    return WithShadow(ToPixels(b), w, h, LabelShadowPad, 3, 7, 0.4f);
                }
            }
        }

        public const int LabelShadowPad = 12;

        // A lone image (a minimized window's icon) that fades in with its tile.
        public static void DrawAlone(Graphics g, Bitmap image, Rectangle dest, float reveal)
        {
            float e = 1 - (float)Math.Pow(1 - reveal, 3);
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            using (var ia = new ImageAttributes())
            {
                ia.SetColorMatrix(new ColorMatrix { Matrix33 = e });
                dest.Offset((int)((1 - e) * -(ArtWidth + 40)), 0);
                g.DrawImage(image, dest, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, ia);
            }
        }

        // An app icon at badge size with its own soft shadow, which follows the icon's real shape.
        public static Bitmap PrepareIcon(Bitmap icon)
        {
            int big = IconSize * Supersample;
            int[] art;
            using (var b = new Bitmap(big, big, PixelFormat.Format32bppPArgb))
            {
                using (var g = Graphics.FromImage(b))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(icon, 0, 0, big, big);
                }
                art = Shrink(ToPixels(b), big, big, Supersample);
            }
            return WithShadow(art, IconSize, IconSize, IconShadowPad, 2, 3, 0.45f);
        }
    }
}
