using System.Drawing.Drawing2D;
using System.Globalization;
using System.Xml.Linq;

namespace Anything;

/// <summary>
/// Windows 11 File Explorer's own command icons: the two-tone SVGs that ship with Windows in the File Explorer package
/// (FileExplorerExtensions\Assets\images\contrast-standard\theme-light|theme-dark). They are read from this PC at run
/// time — nothing is copied into Rock Preview — and drawn with GDI+. Only the SVG subset those icons use is supported:
/// path / rect / circle / ellipse with fill, stroke, fill-rule and opacity, nested groups, simple transforms.
/// </summary>
internal static class ExplorerIcons
{
    public const string NewItem = "windows.newitem", Cut = "windows.cut", Copy = "windows.copy", Paste = "windows.paste",
        Rename = "windows.rename", Share = "Windows.ModernShare", Delete = "windows.ribbondelete", Sort = "SortBy", View = "View",
        DetailsPane = "windows.previewpane", SearchOptions = "SearchFilter", CloseSearch = "windows.searchclosetab",
        NewFolder = "newfolder", SelectAll = "windows.selectall", SelectNone = "windows.selectnone",
        InvertSelection = "windows.invertselection", Properties = "windows.properties", Options = "windows.folderoptions",
        NavPane = "windows.navpane", Refresh = "windows.refresh", SortColumn = "windows.sortbycolumn",
        CopyPath = "windows.copyaspath", OpenNewWindow = "windows.opennewwindow", Undo = "windows.undo",
        IconsExtraLarge = "windows.iconsize.extralarge", IconsLarge = "windows.iconsize.large", IconsMedium = "windows.iconsize.medium",
        IconsSmall = "windows.iconsize.smallicon", IconsList = "windows.iconsize.list", IconsDetails = "windows.iconsize.details",
        IconsTiles = "windows.iconsize.tile", IconsContent = "windows.iconsize.content", ShowHidden = "windows.showhiddenfiles",
        RunAsAdmin = "RunAsAdmin", CompactMode = "windows.compactmode",
        AddressThisPc = "addressbar.desktop", AddressHome = "addressbar.home", AddressGallery = "addressbar.gallery",
        AddressNetwork = "addressbar.network";

    static readonly string Root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows),
        @"SystemApps\MicrosoftWindows.Client.FileExp_cw5n1h2txyewy\FileExplorerExtensions\Assets\images\contrast-standard");

    static readonly Dictionary<string, XElement?> s_docs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Renders an icon at <paramref name="px"/> pixels, or null when this Windows doesn't have it.</summary>
    public static Bitmap? Render(string name, int px, bool dark)
    {
        var svg = Load(name, dark);
        if (svg is null || px <= 0) return null;
        try
        {
            var vb = (svg.Attribute("viewBox")?.Value ?? "0 0 16 16").Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(v => float.Parse(v, CultureInfo.InvariantCulture)).ToArray();
            var bmp = new Bitmap(px, px, System.Drawing.Imaging.PixelFormat.Format32bppPArgb);
            using var g = Graphics.FromImage(bmp);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float scale = px / Math.Max(vb[2], vb[3]); // keep the aspect ratio of non-square icons, centred
            g.TranslateTransform((px - vb[2] * scale) / 2, (px - vb[3] * scale) / 2);
            g.ScaleTransform(scale, scale);
            g.TranslateTransform(-vb[0], -vb[1]);
            DrawChildren(g, svg, Inherit(Style.Default, svg), svg);
            return bmp;
        }
        catch (Exception) { return null; }
    }

    static XElement? Load(string name, bool dark)
    {
        string key = (dark ? "d:" : "l:") + name;
        if (s_docs.TryGetValue(key, out var doc)) return doc;
        try
        {
            string file = Path.Combine(Root, dark ? "theme-dark" : "theme-light", name + ".svg");
            doc = File.Exists(file) ? XElement.Load(file) : null;
        }
        catch (Exception) { doc = null; }
        s_docs[key] = doc;
        return doc;
    }

    // ---- painting -------------------------------------------------------------------------------

    sealed record Style(string? Fill, string? Stroke, float StrokeWidth, float Opacity, float FillOpacity, float StrokeOpacity,
                        bool EvenOdd, LineCap Cap, LineJoin Join)
    {
        public static readonly Style Default = new("#000", null, 1f, 1f, 1f, 1f, false, LineCap.Flat, LineJoin.Miter);
    }

    static void DrawChildren(Graphics g, XElement parent, Style style, XElement root)
    {
        foreach (var el in parent.Elements())
        {
            string tag = el.Name.LocalName;
            if (tag is "defs" or "clipPath" or "mask" or "filter" or "linearGradient" or "radialGradient" or "title" or "desc") continue;
            var s = Inherit(style, el);
            var state = g.Save();
            ApplyTransform(g, el.Attribute("transform")?.Value);
            switch (tag)
            {
                case "g" or "svg": DrawChildren(g, el, s, root); break;
                case "path":
                    using (var p = PathData.Parse(el.Attribute("d")?.Value ?? "")) Paint(g, p, s, root);
                    break;
                case "rect":
                {
                    float x = F(el, "x"), y = F(el, "y"), w = F(el, "width"), h = F(el, "height");
                    float rx = el.Attribute("rx") is null ? F(el, "ry") : F(el, "rx");
                    using var p = rx > 0 ? ExplorerTheme.RoundedRect(new RectangleF(x, y, w, h), rx) : Rect(x, y, w, h);
                    Paint(g, p, s, root);
                    break;
                }
                case "circle" or "ellipse":
                {
                    float cx = F(el, "cx"), cy = F(el, "cy");
                    float rx = tag == "circle" ? F(el, "r") : F(el, "rx"), ry = tag == "circle" ? rx : F(el, "ry");
                    using var p = new GraphicsPath();
                    p.AddEllipse(cx - rx, cy - ry, rx * 2, ry * 2);
                    Paint(g, p, s, root);
                    break;
                }
            }
            g.Restore(state);
        }
    }

    static GraphicsPath Rect(float x, float y, float w, float h)
    {
        var p = new GraphicsPath();
        p.AddRectangle(new RectangleF(x, y, w, h));
        return p;
    }

    static float F(XElement el, string attr)
        => float.TryParse(el.Attribute(attr)?.Value?.Replace("px", ""), NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0f;

    static Style Inherit(Style s, XElement el)
    {
        var a = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var at in el.Attributes()) a[at.Name.LocalName] = at.Value;
        if (a.TryGetValue("style", out var css))
            foreach (var decl in css.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                int i = decl.IndexOf(':');
                if (i > 0) a[decl[..i].Trim()] = decl[(i + 1)..].Trim();
            }
        static float P(string v) => float.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var f) ? f : 1f;
        return s with
        {
            Fill = a.TryGetValue("fill", out var fill) ? fill : s.Fill,
            Stroke = a.TryGetValue("stroke", out var stroke) ? stroke : s.Stroke,
            StrokeWidth = a.TryGetValue("stroke-width", out var sw) ? P(sw) : s.StrokeWidth,
            Opacity = a.TryGetValue("opacity", out var op) ? s.Opacity * P(op) : s.Opacity,
            FillOpacity = a.TryGetValue("fill-opacity", out var fo) ? P(fo) : s.FillOpacity,
            StrokeOpacity = a.TryGetValue("stroke-opacity", out var so) ? P(so) : s.StrokeOpacity,
            EvenOdd = a.TryGetValue("fill-rule", out var fr) ? fr == "evenodd" : s.EvenOdd,
            Cap = a.TryGetValue("stroke-linecap", out var lc) ? lc switch { "round" => LineCap.Round, "square" => LineCap.Square, _ => LineCap.Flat } : s.Cap,
            Join = a.TryGetValue("stroke-linejoin", out var lj) ? lj switch { "round" => LineJoin.Round, "bevel" => LineJoin.Bevel, _ => LineJoin.Miter } : s.Join,
        };
    }

    static void Paint(Graphics g, GraphicsPath path, Style s, XElement root)
    {
        path.FillMode = s.EvenOdd ? FillMode.Alternate : FillMode.Winding;
        if (ParseColor(s.Fill, s.Opacity * s.FillOpacity, root) is { } fill)
        {
            using var b = new SolidBrush(fill);
            g.FillPath(b, path);
        }
        if (ParseColor(s.Stroke, s.Opacity * s.StrokeOpacity, root) is { } stroke && s.StrokeWidth > 0)
        {
            using var pen = new Pen(stroke, s.StrokeWidth) { StartCap = s.Cap, EndCap = s.Cap, LineJoin = s.Join };
            g.DrawPath(pen, path);
        }
    }

    static Color? ParseColor(string? v, float opacity, XElement root)
    {
        if (string.IsNullOrWhiteSpace(v) || v == "none" || v == "transparent") return null;
        v = v.Trim();
        if (v.StartsWith("url(#", StringComparison.Ordinal))
        {
            // Gradients: use the first stop's colour (the command icons only use them for subtle shading).
            string id = v[5..^1];
            var grad = root.Descendants().FirstOrDefault(e => (string?)e.Attribute("id") == id);
            var stop = grad?.Elements().FirstOrDefault(e => e.Name.LocalName == "stop");
            return stop is null ? null : ParseColor(stop.Attribute("stop-color")?.Value, opacity, root);
        }
        Color c;
        if (v[0] == '#')
        {
            string h = v[1..];
            if (h.Length is 3 or 4) h = string.Concat(h.Select(ch => new string(ch, 2)));
            uint n = uint.Parse(h, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            c = h.Length == 8 ? Color.FromArgb((int)(n & 0xFF), (int)(n >> 24), (int)((n >> 16) & 0xFF), (int)((n >> 8) & 0xFF))
                              : Color.FromArgb((int)((n >> 16) & 0xFF), (int)((n >> 8) & 0xFF), (int)(n & 0xFF));
        }
        else if (v.Equals("white", StringComparison.OrdinalIgnoreCase)) c = Color.White;
        else if (v.Equals("black", StringComparison.OrdinalIgnoreCase)) c = Color.Black;
        else if (v.Equals("currentColor", StringComparison.OrdinalIgnoreCase)) c = ExplorerTheme.Text;
        else c = Color.FromName(v);
        return Color.FromArgb((int)Math.Clamp(c.A * opacity, 0, 255), c);
    }

    static void ApplyTransform(Graphics g, string? t)
    {
        if (string.IsNullOrWhiteSpace(t)) return;
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(t, @"(\w+)\s*\(([^)]*)\)"))
        {
            var n = m.Groups[2].Value.Split([' ', ','], StringSplitOptions.RemoveEmptyEntries)
                .Select(x => float.Parse(x, CultureInfo.InvariantCulture)).ToArray();
            switch (m.Groups[1].Value)
            {
                case "translate": g.TranslateTransform(n[0], n.Length > 1 ? n[1] : 0); break;
                case "scale": g.ScaleTransform(n[0], n.Length > 1 ? n[1] : n[0]); break;
                case "rotate":
                    if (n.Length == 3) { g.TranslateTransform(n[1], n[2]); g.RotateTransform(n[0]); g.TranslateTransform(-n[1], -n[2]); }
                    else g.RotateTransform(n[0]);
                    break;
                case "matrix" when n.Length == 6:
                    using (var mx = new Matrix(n[0], n[1], n[2], n[3], n[4], n[5])) g.MultiplyTransform(mx);
                    break;
            }
        }
    }

    /// <summary>SVG path data ("d") to a GraphicsPath: M L H V C S Q T A Z, absolute and relative.</summary>
    static class PathData
    {
        public static GraphicsPath Parse(string d)
        {
            var path = new GraphicsPath();
            int i = 0;
            char cmd = ' ';
            PointF cur = default, start = default, lastCtrl = default;
            char prev = ' ';
            while (true)
            {
                SkipSeparators(d, ref i);
                if (i >= d.Length) break;
                if (char.IsLetter(d[i])) cmd = d[i++];
                else if (cmd == ' ') break;
                bool rel = char.IsLower(cmd);
                PointF Pt(float x, float y) => rel ? new PointF(cur.X + x, cur.Y + y) : new PointF(x, y);
                switch (char.ToUpperInvariant(cmd))
                {
                    case 'M':
                    {
                        var p = Pt(Num(d, ref i), Num(d, ref i));
                        path.StartFigure();
                        cur = start = p;
                        cmd = rel ? 'l' : 'L'; // further pairs are lines
                        break;
                    }
                    case 'L': { var p = Pt(Num(d, ref i), Num(d, ref i)); path.AddLine(cur, p); cur = p; break; }
                    case 'H': { float x = Num(d, ref i); var p = new PointF(rel ? cur.X + x : x, cur.Y); path.AddLine(cur, p); cur = p; break; }
                    case 'V': { float y = Num(d, ref i); var p = new PointF(cur.X, rel ? cur.Y + y : y); path.AddLine(cur, p); cur = p; break; }
                    case 'C':
                    {
                        var c1 = Pt(Num(d, ref i), Num(d, ref i));
                        var c2 = Pt(Num(d, ref i), Num(d, ref i));
                        var p = Pt(Num(d, ref i), Num(d, ref i));
                        path.AddBezier(cur, c1, c2, p);
                        lastCtrl = c2; cur = p;
                        break;
                    }
                    case 'S':
                    {
                        var c1 = "CcSs".Contains(prev) ? new PointF(2 * cur.X - lastCtrl.X, 2 * cur.Y - lastCtrl.Y) : cur;
                        var c2 = Pt(Num(d, ref i), Num(d, ref i));
                        var p = Pt(Num(d, ref i), Num(d, ref i));
                        path.AddBezier(cur, c1, c2, p);
                        lastCtrl = c2; cur = p;
                        break;
                    }
                    case 'Q':
                    {
                        var q = Pt(Num(d, ref i), Num(d, ref i));
                        var p = Pt(Num(d, ref i), Num(d, ref i));
                        AddQuad(path, cur, q, p);
                        lastCtrl = q; cur = p;
                        break;
                    }
                    case 'T':
                    {
                        var q = "QqTt".Contains(prev) ? new PointF(2 * cur.X - lastCtrl.X, 2 * cur.Y - lastCtrl.Y) : cur;
                        var p = Pt(Num(d, ref i), Num(d, ref i));
                        AddQuad(path, cur, q, p);
                        lastCtrl = q; cur = p;
                        break;
                    }
                    case 'A':
                    {
                        float rx = Num(d, ref i), ry = Num(d, ref i), rot = Num(d, ref i);
                        bool large = Flag(d, ref i), sweep = Flag(d, ref i);
                        var p = Pt(Num(d, ref i), Num(d, ref i));
                        AddArc(path, cur, p, rx, ry, rot, large, sweep);
                        cur = p;
                        break;
                    }
                    case 'Z':
                        path.CloseFigure();
                        cur = start;
                        break;
                    default:
                        return path;
                }
                prev = cmd;
            }
            return path;
        }

        static void SkipSeparators(string d, ref int i)
        {
            while (i < d.Length && (char.IsWhiteSpace(d[i]) || d[i] == ',')) i++;
        }

        static float Num(string d, ref int i)
        {
            SkipSeparators(d, ref i);
            int s = i;
            if (i < d.Length && (d[i] == '-' || d[i] == '+')) i++;
            bool dot = false, exp = false;
            while (i < d.Length)
            {
                char c = d[i];
                if (char.IsDigit(c)) { i++; continue; }
                if (c == '.' && !dot && !exp) { dot = true; i++; continue; }
                if ((c == 'e' || c == 'E') && !exp) { exp = true; i++; if (i < d.Length && (d[i] == '-' || d[i] == '+')) i++; continue; }
                break;
            }
            return float.Parse(d.AsSpan(s, i - s), NumberStyles.Float, CultureInfo.InvariantCulture);
        }

        static bool Flag(string d, ref int i)
        {
            SkipSeparators(d, ref i);
            return d[i++] == '1';
        }

        static void AddQuad(GraphicsPath path, PointF p0, PointF q, PointF p)
            => path.AddBezier(p0, new PointF(p0.X + 2f / 3 * (q.X - p0.X), p0.Y + 2f / 3 * (q.Y - p0.Y)),
                              new PointF(p.X + 2f / 3 * (q.X - p.X), p.Y + 2f / 3 * (q.Y - p.Y)), p);

        /// <summary>SVG endpoint arc → cubic Béziers (SVG 1.1 implementation notes, F.6.5).</summary>
        static void AddArc(GraphicsPath path, PointF p1, PointF p2, float rx, float ry, float angleDeg, bool large, bool sweep)
        {
            if (p1 == p2) return;
            rx = Math.Abs(rx); ry = Math.Abs(ry);
            if (rx < 1e-6 || ry < 1e-6) { path.AddLine(p1, p2); return; }
            double phi = angleDeg * Math.PI / 180, cos = Math.Cos(phi), sin = Math.Sin(phi);
            double dx = (p1.X - p2.X) / 2.0, dy = (p1.Y - p2.Y) / 2.0;
            double x1 = cos * dx + sin * dy, y1 = -sin * dx + cos * dy;
            double lambda = x1 * x1 / (rx * rx) + y1 * y1 / (ry * ry);
            double Rx = rx, Ry = ry;
            if (lambda > 1) { double s = Math.Sqrt(lambda); Rx *= s; Ry *= s; }
            double num = Rx * Rx * Ry * Ry - Rx * Rx * y1 * y1 - Ry * Ry * x1 * x1;
            double den = Rx * Rx * y1 * y1 + Ry * Ry * x1 * x1;
            double coef = (large != sweep ? 1 : -1) * Math.Sqrt(Math.Max(0, num / den));
            double cx1 = coef * Rx * y1 / Ry, cy1 = coef * -Ry * x1 / Rx;
            double cx = cos * cx1 - sin * cy1 + (p1.X + p2.X) / 2.0, cy = sin * cx1 + cos * cy1 + (p1.Y + p2.Y) / 2.0;
            static double Angle(double ux, double uy, double vx, double vy)
            {
                double a = Math.Atan2(ux * vy - uy * vx, ux * vx + uy * vy);
                return a;
            }
            double t1 = Angle(1, 0, (x1 - cx1) / Rx, (y1 - cy1) / Ry);
            double dt = Angle((x1 - cx1) / Rx, (y1 - cy1) / Ry, (-x1 - cx1) / Rx, (-y1 - cy1) / Ry);
            if (!sweep && dt > 0) dt -= 2 * Math.PI;
            else if (sweep && dt < 0) dt += 2 * Math.PI;
            int segs = (int)Math.Ceiling(Math.Abs(dt) / (Math.PI / 2));
            double step = dt / segs, k = 4.0 / 3 * Math.Tan(step / 4);
            PointF Map(double ex, double ey) => new((float)(cos * Rx * ex - sin * Ry * ey + cx), (float)(sin * Rx * ex + cos * Ry * ey + cy));
            var from = p1;
            for (int s = 0; s < segs; s++)
            {
                double a1 = t1 + s * step, a2 = a1 + step;
                double c1x = Math.Cos(a1), c1y = Math.Sin(a1), c2x = Math.Cos(a2), c2y = Math.Sin(a2);
                var cp1 = Map(c1x - k * c1y, c1y + k * c1x);
                var cp2 = Map(c2x + k * c2y, c2y - k * c2x);
                var to = s == segs - 1 ? p2 : Map(c2x, c2y);
                path.AddBezier(from, cp1, cp2, to);
                from = to;
            }
        }
    }
}
