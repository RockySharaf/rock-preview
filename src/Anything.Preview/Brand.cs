using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;

namespace Anything.Preview;

/// <summary>
/// Rock Preview's mark (the R in a folder, with its pixels), drawn in any colour: the title bar's badge and the empty
/// preview pane's watermark. The embedded image is the mark's shape only (black, with its transparency).
/// </summary>
public static class Brand
{
    static Bitmap? _mark;
    static readonly Dictionary<(int Argb, int Height), Bitmap> Tints = [];

    /// <summary>The mark's width over its height.</summary>
    public static float Aspect => Mark is { } m ? (float)m.Width / m.Height : 1.5f;

    static Bitmap? Mark
    {
        get
        {
            if (_mark != null) return _mark;
            try
            {
                using var s = typeof(Brand).Assembly.GetManifestResourceStream("RockPreviewMark.png");
                if (s != null) _mark = new Bitmap(s);
            }
            catch (Exception e) when (e is ArgumentException or IOException) { }
            return _mark;
        }
    }

    /// <summary>The mark <paramref name="height"/> pixels high in <paramref name="color"/> (its alpha too); null if it couldn't load. Kept for reuse.</summary>
    public static Bitmap? Tinted(Color color, int height)
    {
        if (Mark is not { } mark || height < 1) return null;
        var key = (color.ToArgb(), height);
        lock (Tints)
        {
            if (Tints.TryGetValue(key, out var have)) return have;
            int width = Math.Max(1, (int)Math.Round(height * Aspect));
            var bmp = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            using (var attrs = new ImageAttributes())
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
                // Black shape -> the colour; the shape's alpha times the colour's.
                var m = new ColorMatrix(
                [
                    [0, 0, 0, 0, 0],
                    [0, 0, 0, 0, 0],
                    [0, 0, 0, 0, 0],
                    [0, 0, 0, color.A / 255f, 0],
                    [color.R / 255f, color.G / 255f, color.B / 255f, 0, 1],
                ]);
                attrs.SetColorMatrix(m);
                g.DrawImage(mark, new Rectangle(0, 0, width, height), 0, 0, mark.Width, mark.Height, GraphicsUnit.Pixel, attrs);
            }
            if (Tints.Count > 32) Tints.Clear();
            Tints[key] = bmp;
            return bmp;
        }
    }
}

/// <summary>
/// The empty pane's message with the mark faintly above it, small, so the pane says whose it is without drawing the eye.
/// </summary>
internal sealed class EmptyPreview : Label
{
    public EmptyPreview()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        e.Graphics.Clear(BackColor);
        var size = TextRenderer.MeasureText(e.Graphics, Text, Font);
        int markH = LogicalToDeviceUnits(56);
        int gap = LogicalToDeviceUnits(14);
        // Only when there's room for both; a short pane keeps just the words.
        bool mark = ClientSize.Height >= markH + gap + size.Height + LogicalToDeviceUnits(40) && ClientSize.Width >= LogicalToDeviceUnits(120);
        int top = (ClientSize.Height - (mark ? markH + gap : 0) - size.Height) / 2;
        if (mark && Brand.Tinted(Color.FromArgb(18, ForeColor), markH) is { } bmp)
            e.Graphics.DrawImage(bmp, (ClientSize.Width - bmp.Width) / 2, top);
        var text = new Rectangle(0, top + (mark ? markH + gap : 0), ClientSize.Width, size.Height);
        TextRenderer.DrawText(e.Graphics, Text, Font, text, ForeColor, TextFormatFlags.HorizontalCenter | TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
    }
}
