using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

namespace PreviewHarness.Bench;

/// <summary>
/// Paints pictures that compress like photographs rather than like noise or flat graphics: a soft scene (sky and
/// ground gradients, hills, a few hundred overlapping soft-edged shapes) drawn at a quarter of the size and enlarged
/// (out-of-focus areas), then sharp shapes and text at full size (in-focus edges), then a fine texture made of
/// multi-octave value noise (foliage, fabric, asphalt) whose strength varies across the frame, and a little sensor grain.
/// </summary>
internal static class PhotoPainter
{
    public static Bitmap Photo(int w, int h, int seed)
    {
        var rnd = new Random(seed);
        int sw = Math.Max(64, w / 4), sh = Math.Max(48, h / 4);
        var bmp = new Bitmap(w, h, PixelFormat.Format24bppRgb);
        using (var small = new Bitmap(sw, sh, PixelFormat.Format24bppRgb))
        {
            using (var g = Graphics.FromImage(small))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                var skyTop = Color.FromArgb(rnd.Next(40, 120), rnd.Next(90, 170), rnd.Next(170, 250));
                var skyLow = Color.FromArgb(rnd.Next(180, 250), rnd.Next(170, 230), rnd.Next(150, 230));
                using (var sky = new LinearGradientBrush(new Rectangle(0, 0, sw, sh / 2 + 1), skyTop, skyLow, 90f)) g.FillRectangle(sky, 0, 0, sw, sh / 2 + 1);
                var gTop = Color.FromArgb(rnd.Next(60, 140), rnd.Next(90, 160), rnd.Next(30, 90));
                var gLow = Color.FromArgb(rnd.Next(30, 90), rnd.Next(40, 90), rnd.Next(20, 60));
                using (var ground = new LinearGradientBrush(new Rectangle(0, sh / 2, sw, sh / 2 + 1), gTop, gLow, 90f)) g.FillRectangle(ground, 0, sh / 2, sw, sh - sh / 2);
                // Hills.
                for (int k = 0; k < 4; k++)
                {
                    var pts = new PointF[24];
                    float baseY = sh * (0.38f + k * 0.06f);
                    for (int i = 0; i < 22; i++) pts[i] = new PointF(sw * i / 21f, baseY - (float)(rnd.NextDouble() * sh * 0.12));
                    pts[22] = new PointF(sw, sh);
                    pts[23] = new PointF(0, sh);
                    using var b = new SolidBrush(Color.FromArgb(200, rnd.Next(30, 110), rnd.Next(60, 130), rnd.Next(40, 120)));
                    g.FillClosedCurve(b, pts, FillMode.Alternate, 0.4f);
                }
                // Soft shapes: clouds, bushes, people-sized blobs.
                for (int i = 0; i < 260; i++)
                {
                    float cx = (float)rnd.NextDouble() * sw, cy = (float)rnd.NextDouble() * sh;
                    float r = (float)(rnd.NextDouble() * rnd.NextDouble() * sw / 6 + 2);
                    using var path = new GraphicsPath();
                    path.AddEllipse(cx - r, cy - r * 0.7f, r * 2, r * 1.4f);
                    using var pb = new PathGradientBrush(path)
                    {
                        CenterColor = Color.FromArgb(rnd.Next(120, 230), rnd.Next(256), rnd.Next(256), rnd.Next(256)),
                        SurroundColors = new[] { Color.FromArgb(0, rnd.Next(256), rnd.Next(256), rnd.Next(256)) },
                    };
                    g.FillPath(pb, path);
                }
            }
            using var big = Graphics.FromImage(bmp);
            big.InterpolationMode = InterpolationMode.HighQualityBicubic;
            big.PixelOffsetMode = PixelOffsetMode.HighQuality;
            big.DrawImage(small, new Rectangle(0, 0, w, h));
        }
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
            // In-focus objects: buildings, poles, signs with text.
            for (int i = 0; i < 70; i++)
            {
                int x = rnd.Next(w), y = rnd.Next(h / 3, h);
                int bw = rnd.Next(w / 80, w / 12), bh = rnd.Next(h / 40, h / 5);
                var c = Color.FromArgb(rnd.Next(30, 230), rnd.Next(30, 230), rnd.Next(30, 230));
                using var b = new LinearGradientBrush(new Rectangle(x, y - bh, bw, bh + 1), c, ControlPaint.Dark(c), (float)rnd.Next(360));
                g.FillRectangle(b, x, y - bh, bw, bh);
                using var p = new Pen(Color.FromArgb(160, 20, 20, 20), Math.Max(1, w / 1500f));
                g.DrawRectangle(p, x, y - bh, bw, bh);
                // Windows on buildings.
                if (bw > w / 40)
                    for (int wy = y - bh + bh / 10; wy < y - bh / 10; wy += Math.Max(4, bh / 8))
                        for (int wx = x + bw / 10; wx < x + bw - bw / 10; wx += Math.Max(4, bw / 6))
                            g.FillRectangle(Brushes.DarkSlateGray, wx, wy, Math.Max(2, bw / 12), Math.Max(2, bh / 16));
            }
            string[] words = { "MAIN ST", "Café", "OPEN", "No Parking 8–18", "Platform 2", "SALE –40%", "Hotel", "Bakery", "Exit", "Bus 42" };
            for (int i = 0; i < 18; i++)
            {
                float fs = Math.Max(8, h / (float)rnd.Next(25, 90));
                using var f = new Font(i % 2 == 0 ? "Segoe UI" : "Georgia", fs, i % 3 == 0 ? FontStyle.Bold : FontStyle.Regular, GraphicsUnit.Pixel);
                int x = rnd.Next(w * 9 / 10), y = rnd.Next(h * 9 / 10);
                var sz = g.MeasureString(words[i % words.Length], f);
                g.FillRectangle(new SolidBrush(Color.FromArgb(220, rnd.Next(256), rnd.Next(256), rnd.Next(256))), x - 4, y - 2, sz.Width + 8, sz.Height + 4);
                g.DrawString(words[i % words.Length], f, Brushes.Black, x, y);
            }
            // Wires and branches.
            for (int i = 0; i < 40; i++)
            {
                using var p = new Pen(Color.FromArgb(rnd.Next(80, 200), 25, 25, 25), Math.Max(1, w / (float)rnd.Next(800, 3000)));
                g.DrawBezier(p, rnd.Next(w), rnd.Next(h), rnd.Next(w), rnd.Next(h), rnd.Next(w), rnd.Next(h), rnd.Next(w), rnd.Next(h));
            }
        }
        AddTexture(bmp, rnd);
        return bmp;
    }

    /// <summary>Flat graphics (screenshot, chart, poster) with optional transparency: compresses like a PNG of a UI.</summary>
    public static Bitmap Graphic(int w, int h, int seed, bool alpha)
    {
        var rnd = new Random(seed);
        var bmp = new Bitmap(w, h, alpha ? PixelFormat.Format32bppArgb : PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(alpha ? Color.Transparent : Color.FromArgb(246, 246, 248));
        for (int i = 0; i < 40; i++)
        {
            var c = Color.FromArgb(alpha ? rnd.Next(120, 256) : 255, rnd.Next(256), rnd.Next(256), rnd.Next(256));
            using var b = new SolidBrush(c);
            int x = rnd.Next(w), y = rnd.Next(h), bw = rnd.Next(w / 30, w / 4), bh = rnd.Next(h / 30, h / 4);
            if (i % 3 == 0) g.FillEllipse(b, x, y, bw, bh); else g.FillRectangle(b, x, y, bw, bh);
        }
        float fs = Math.Max(9, h / 60f);
        using var f = new Font("Segoe UI", fs, GraphicsUnit.Pixel);
        for (int y = (int)fs; y < h - fs * 2; y += (int)(fs * 1.6f))
            if (rnd.Next(3) > 0) g.DrawString("Quarterly figures: revenue 12.4 m, cost 9.1 m, margin 26 % — region north and south", f, Brushes.Black, fs, y);
        return bmp;
    }

    /// <summary>Adds texture (value noise, 4 octaves, strength varying across the frame) and grain, row by row.</summary>
    private static void AddTexture(Bitmap bmp, Random rnd)
    {
        const int T = 512;
        var tile = new sbyte[T * T];
        // Value noise: random lattice at three scales, bilinear, summed.
        var lattice = new float[4][];
        int[] cells = { 8, 32, 128, 256 };
        float[] amp = { 0.35f, 0.25f, 0.2f, 0.2f };
        for (int o = 0; o < 4; o++)
        {
            lattice[o] = new float[(cells[o] + 1) * (cells[o] + 1)];
            for (int i = 0; i < lattice[o].Length; i++) lattice[o][i] = (float)(rnd.NextDouble() * 2 - 1);
        }
        for (int y = 0; y < T; y++)
            for (int x = 0; x < T; x++)
            {
                float v = 0;
                for (int o = 0; o < 4; o++)
                {
                    int n = cells[o];
                    float fx = x * n / (float)T, fy = y * n / (float)T;
                    int ix = (int)fx, iy = (int)fy;
                    float tx = fx - ix, ty = fy - iy;
                    float[] L = lattice[o];
                    int s = n + 1;
                    float a = L[iy * s + ix], b = L[iy * s + ix + 1], c = L[(iy + 1) * s + ix], d = L[(iy + 1) * s + ix + 1];
                    v += amp[o] * (a + (b - a) * tx + (c - a) * ty + (a - b - c + d) * tx * ty);
                }
                tile[y * T + x] = (sbyte)Math.Clamp(v * 90, -127, 127);
            }
        int w = bmp.Width, h = bmp.Height;
        var data = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadWrite, PixelFormat.Format24bppRgb);
        try
        {
            var row = new byte[data.Stride];
            uint state = (uint)rnd.Next() | 1u;
            // Texture strength changes in large patches (sky smooth, ground busy).
            int px = Math.Max(1, w / 6), py = Math.Max(1, h / 6);
            var strength = new float[7 * 7];
            for (int i = 0; i < strength.Length; i++) strength[i] = (float)rnd.NextDouble();
            for (int y = 0; y < h; y++)
            {
                IntPtr p = data.Scan0 + y * data.Stride;
                Marshal.Copy(p, row, 0, w * 3);
                int ty = y % T, offx = (y / T) * 131 % T;
                float sy = Math.Min(6, y / (float)py);
                for (int x = 0; x < w; x++)
                {
                    float sx = Math.Min(6, x / (float)px);
                    float s = strength[(int)sy * 7 + (int)sx] * (y > h / 3 ? 1f : 0.35f);
                    int t = tile[ty * T + (x + offx) % T];
                    state ^= state << 13; state ^= state >> 17; state ^= state << 5;
                    int grain = (int)(state & 15) - 7;
                    int add = (int)(t * (0.25f + s) * 0.8f) + grain;
                    int i = x * 3;
                    row[i] = (byte)Math.Clamp(row[i] + add, 0, 255);
                    row[i + 1] = (byte)Math.Clamp(row[i + 1] + add, 0, 255);
                    row[i + 2] = (byte)Math.Clamp(row[i + 2] + add + (grain >> 1), 0, 255);
                }
                Marshal.Copy(row, 0, p, w * 3);
            }
        }
        finally { bmp.UnlockBits(data); }
    }

    private static class ControlPaint
    {
        public static Color Dark(Color c) => Color.FromArgb(c.R * 6 / 10, c.G * 6 / 10, c.B * 6 / 10);
    }
}
