using System.Text;

namespace Anything.Core.Content.Extract;

/// <summary>
/// Match boxes on one PDF page from our own content-stream walk (Tm, Td, TJ, cm, /Widths, CropBox, /Rotate): the page's
/// text is assembled exactly as the index stores it (the walker judges fonts page by page, so walking only this page
/// gives the same text as walking the whole document), the finder runs on it, and each match's glyphs become one box
/// per line, in 0..1 of the page as shown (CropBox within MediaBox, rotation applied, origin top-left).
/// </summary>
internal static class PdfPageText
{
    /// <summary>
    /// Boxes in 0..1 of the shown page around runs whose text the finder matches; empty when none, when the file is
    /// locked or damaged, or the page doesn't exist. Limits, cancellation and I/O errors are thrown as when indexing.
    /// </summary>
    public static IReadOnlyList<PageBox> FindBoxes(IByteSource source, int pageIndex, Func<string, IReadOnlyList<(int Start, int Length)>> find, ExtractBudget budget)
    {
        try { return Find(source, pageIndex, find, budget); }
        catch (Exception e) when (PdfFile.IsFormatError(e)) { return []; }
    }

    static List<PageBox> Find(IByteSource source, int pageIndex, Func<string, IReadOnlyList<(int Start, int Length)>> find, ExtractBudget budget)
    {
        using var file = PdfFile.Open(source, budget);
        if (file.Locked) return [];
        var page = file.PageAt(pageIndex);
        if (page == null) return [];
        var target = new PageTarget();
        var walker = new PdfContentWalker(file, new PdfTextAssembler(target), () => target.Length >= budget.Limits.MaxTextChars);
        walker.WalkPage(page);
        string text = target.ToString();
        if (text.Length == 0) return [];
        var matches = find(text);
        var boxes = new List<PageBox>();
        foreach (var (start, length) in matches)
        {
            int from = Math.Max(0, start), to = Math.Min(text.Length, start + length);
            Rect? line = null;
            for (int i = from; i < to; i++)
            {
                if (text[i] == '\n')
                {
                    Add(boxes, line, page);
                    line = null;
                    continue;
                }
                int g = target.GlyphAt(i);
                if (g < 0) continue;
                var r = target.Bounds(g);
                line = line is { } l ? l.Union(r) : r;
            }
            Add(boxes, line, page);
        }
        return boxes;
    }

    /// <summary>A rectangle in user space.</summary>
    readonly record struct Rect(double X0, double Y0, double X1, double Y1)
    {
        /// <summary>The smallest rectangle holding both.</summary>
        public Rect Union(in Rect o) => new(Math.Min(X0, o.X0), Math.Min(Y0, o.Y0), Math.Max(X1, o.X1), Math.Max(Y1, o.Y1));
    }

    static void Add(List<PageBox> boxes, Rect? rect, PdfPage page)
    {
        if (rect is not { } r) return;
        // Several matches inside one block of text (a form field's value) give the same box once.
        if (ToPage(r, page) is { } box && !boxes.Contains(box)) boxes.Add(box);
    }

    /// <summary>A user-space rectangle in 0..1 of the shown page: the visible box, then the page's rotation (clockwise).</summary>
    static PageBox? ToPage(Rect r, PdfPage page)
    {
        var box = page.VisibleBox;
        double w = box[2] - box[0], h = box[3] - box[1];
        if (w <= 0 || h <= 0) return null;
        double u0 = (r.X0 - box[0]) / w, u1 = (r.X1 - box[0]) / w;
        double v0 = (box[3] - r.Y1) / h, v1 = (box[3] - r.Y0) / h; // top-left origin
        (double U, double V) Rotate(double u, double v) => page.Rotate switch
        {
            90 => (1 - v, u),
            180 => (1 - u, 1 - v),
            270 => (v, 1 - u),
            _ => (u, v),
        };
        var a = Rotate(u0, v0);
        var b = Rotate(u1, v1);
        double x0 = Math.Clamp(Math.Min(a.U, b.U), 0, 1), x1 = Math.Clamp(Math.Max(a.U, b.U), 0, 1);
        double y0 = Math.Clamp(Math.Min(a.V, b.V), 0, 1), y1 = Math.Clamp(Math.Max(a.V, b.V), 0, 1);
        if (x1 - x0 <= 0 || y1 - y0 <= 0 || !double.IsFinite(x0 + x1 + y0 + y1)) return null;
        return new PageBox((float)x0, (float)y0, (float)(x1 - x0), (float)(y1 - y0));
    }

    /// <summary>The page's assembled text, with the glyph behind each character (-1 for spaces and line ends it added).</summary>
    sealed class PageTarget : IPdfTextTarget
    {
        readonly StringBuilder _text = new();
        readonly List<int> _glyphOf = [];
        readonly List<Rect> _glyphs = [];

        /// <summary>Characters of page text so far.</summary>
        public int Length => _text.Length;

        /// <inheritdoc/>
        public void Text(string text, in PdfGlyph glyph)
        {
            int index = _glyphs.Count;
            _glyphs.Add(BoundsOf(glyph));
            _text.Append(text);
            for (int i = 0; i < text.Length; i++) _glyphOf.Add(index);
        }

        /// <inheritdoc/>
        public void Space() { _text.Append(' '); _glyphOf.Add(-1); }
        /// <inheritdoc/>
        public void NewLine() { _text.Append('\n'); _glyphOf.Add(-1); }

        /// <summary>The glyph behind the character at i; -1 for an added space or line end, or past the end.</summary>
        public int GlyphAt(int i) => i >= 0 && i < _glyphOf.Count ? _glyphOf[i] : -1;
        /// <summary>Where a glyph stands on the page, in user space.</summary>
        public Rect Bounds(int glyph) => _glyphs[glyph];
        /// <summary>The page's text.</summary>
        public override string ToString() => _text.ToString();

        /// <summary>The glyph's quad (baseline from start to end, descent to ascent along "up"), as an axis-aligned rectangle.</summary>
        static Rect BoundsOf(in PdfGlyph g)
        {
            double ax = g.Ux * g.Ascent, ay = g.Uy * g.Ascent, dx = g.Ux * g.Descent, dy = g.Uy * g.Descent;
            double x0 = Math.Min(Math.Min(g.X0 + ax, g.X0 + dx), Math.Min(g.X1 + ax, g.X1 + dx));
            double x1 = Math.Max(Math.Max(g.X0 + ax, g.X0 + dx), Math.Max(g.X1 + ax, g.X1 + dx));
            double y0 = Math.Min(Math.Min(g.Y0 + ay, g.Y0 + dy), Math.Min(g.Y1 + ay, g.Y1 + dy));
            double y1 = Math.Max(Math.Max(g.Y0 + ay, g.Y0 + dy), Math.Max(g.Y1 + ay, g.Y1 + dy));
            return new Rect(x0, y0, x1, y1);
        }
    }
}
