using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;
using Anything.Preview.Util;

namespace Anything.Preview;

/// <summary>
/// ASCII DXF wireframe renderer: LINE, LWPOLYLINE (bulges), POLYLINE/VERTEX, CIRCLE, ARC, ELLIPSE, SPLINE (de Boor),
/// SOLID/TRACE/3DFACE, HATCH (boundaries; solid fills), POINT, TEXT/MTEXT, INSERT (nested blocks, arrays) and
/// DIMENSION blocks. Geometry is flattened to world space, then fitted to the bitmap.
/// </summary>
internal sealed class DxfRenderer
{
    private sealed class Ent
    {
        public string Type = "";
        public readonly List<(int Code, string Value)> Pairs = new();
        public List<Ent>? Vertices; // POLYLINE

        public string? S(int code) { foreach (var p in Pairs) if (p.Code == code) return p.Value; return null; }
        public double D(int code, double def = 0) =>
            S(code) is { } s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : def;
        public int I(int code, int def = 0) =>
            S(code) is { } s && int.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : def;
    }

    private sealed class Block { public double BaseX, BaseY; public readonly List<Ent> Ents = new(); }

    private readonly struct Xf
    {
        public readonly double A, B, C, D, E, F; // x' = A x + C y + E ; y' = B x + D y + F
        public Xf(double a, double b, double c, double d, double e, double f) { A = a; B = b; C = c; D = d; E = e; F = f; }
        public static readonly Xf Identity = new(1, 0, 0, 1, 0, 0);
        public PointD Apply(double x, double y) => new(A * x + C * y + E, B * x + D * y + F);
        public Xf Then(Xf o) => new( // apply this, then o
            o.A * A + o.C * B, o.B * A + o.D * B,
            o.A * C + o.C * D, o.B * C + o.D * D,
            o.A * E + o.C * F + o.E, o.B * E + o.D * F + o.F);
        public double ScaleFactor => Math.Sqrt(Math.Abs(A * D - B * C));
        public double Rotation => Math.Atan2(B, A);
    }

    private readonly record struct PointD(double X, double Y);
    private sealed record Prim(PointD[] Pts, Color Color, bool Closed, bool Fill);
    private sealed record Txt(PointD At, double Height, double Rot, string Text, Color Color);

    private readonly Dictionary<string, Color> _layerColors = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Block> _blocks = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<Ent> _entities = new();
    private readonly List<Prim> _prims = new();
    private readonly List<Txt> _texts = new();
    private long _segments;
    private const long MaxSegments = 3_000_000;
    public string? Version { get; private set; }
    public int EntityCount => _entities.Count;

    public static PreviewResult? Render(string path, Size target, CancellationToken ct)
    {
        var head = FileUtil.ReadHead(path, 32);
        if (Bytes.StartsWithAscii(head, "AutoCAD Binary DXF")) return null;
        var fi = new FileInfo(path);
        if (fi.Length > 200L * 1024 * 1024) return null;
        var r = new DxfRenderer();
        using (var fs = FileUtil.OpenShared(path, 1 << 16, sequential: true))
        using (var sr = new StreamReader(fs, Encoding.Latin1))
            r.Parse(sr, ct);
        r.Flatten(ct);
        if (r._prims.Count == 0 && r._texts.Count == 0) return null;
        var size = BitmapUtil.DecodeBox(target);
        var bmp = r.Draw(new Size(Math.Min(size.Width, 2400), Math.Min(size.Height, 2400)));
        if (bmp == null) return null;
        var res = new PreviewResult(path, PreviewKind.Image, "DXF") { FormatName = "AutoCAD DXF drawing", Image = bmp };
        if (r.Version != null) res.AddMeta("Version", (EmbeddedPreviews.DwgVersionName(r.Version) ?? r.Version) + $" ({r.Version})");
        res.AddMeta("Entities", r._entities.Count.ToString("N0", CultureInfo.InvariantCulture));
        res.AddMeta("Blocks", r._blocks.Count.ToString(CultureInfo.InvariantCulture));
        res.AddMeta("Layers", r._layerColors.Count.ToString(CultureInfo.InvariantCulture));
        return res;
    }

    // ------------------------------------------------------------------ parsing

    private void Parse(StreamReader sr, CancellationToken ct)
    {
        string? section = null;
        Block? curBlock = null;
        Ent? cur = null;
        Ent? curPolyline = null;
        string? tableType = null;
        string? lastVar = null;
        long n = 0;
        while (true)
        {
            string? cl = sr.ReadLine();
            string? vl = sr.ReadLine();
            if (cl == null || vl == null) break;
            if ((++n & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
            if (!int.TryParse(cl.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int code)) continue;
            string v = vl.Trim();
            if (code == 0)
            {
                // finish current entity
                if (cur != null) Commit(cur, section, curBlock, ref curPolyline);
                cur = null;
                switch (v)
                {
                    case "SECTION": section = null; continue;
                    case "ENDSEC": section = null; curBlock = null; continue;
                    case "EOF": return;
                    case "TABLE": continue;
                    case "ENDTAB": tableType = null; continue;
                    case "BLOCK": cur = new Ent { Type = "BLOCK" }; continue;
                    case "ENDBLK": curBlock = null; continue;
                    case "SEQEND": curPolyline = null; continue;
                }
                cur = new Ent { Type = v };
                if (section == "TABLES" && tableType == null && v != "LAYER") { /* other table records */ }
                continue;
            }
            if (code == 2 && cur == null && section == null) { section = v; continue; }
            if (section == "HEADER")
            {
                if (code == 9) lastVar = v;
                else if (code == 1 && lastVar == "$ACADVER") Version = v;
                continue;
            }
            if (cur == null) continue;
            if (cur.Type == "BLOCK")
            {
                cur.Pairs.Add((code, v));
                // The block definition header ends with its first entity (code 0), handled in Commit.
                continue;
            }
            cur.Pairs.Add((code, v));
        }
        if (cur != null) Commit(cur, section, curBlock, ref curPolyline);

        void Commit(Ent e, string? sec, Block? blk, ref Ent? poly)
        {
            if (e.Type == "BLOCK")
            {
                var b = new Block { BaseX = e.D(10), BaseY = e.D(20) };
                string name = e.S(2) ?? e.S(3) ?? "";
                _blocks[name] = b;
                curBlock = b;
                return;
            }
            if (sec == "TABLES")
            {
                if (e.Type == "LAYER" && e.S(2) is { } ln)
                {
                    int ci = e.I(62, 7);
                    var col = e.S(420) is { } tc && int.TryParse(tc, out int rgb) ? Color.FromArgb(255, Color.FromArgb(rgb)) : Aci(Math.Abs(ci));
                    _layerColors[ln] = ci < 0 ? Color.Empty : col; // negative = layer off
                }
                return;
            }
            if (sec != "ENTITIES" && sec != "BLOCKS") return;
            var list = sec == "BLOCKS" ? curBlock?.Ents : _entities;
            if (list == null) return;
            if (e.Type == "VERTEX" && poly != null) { (poly.Vertices ??= new()).Add(e); return; }
            if (e.Type == "POLYLINE") poly = e;
            else if (e.Type != "VERTEX") poly = null;
            list.Add(e);
        }
    }

    // ------------------------------------------------------------------ flattening

    private void Flatten(CancellationToken ct)
    {
        int i = 0;
        foreach (var e in _entities)
        {
            if ((++i & 1023) == 0) ct.ThrowIfCancellationRequested();
            Emit(e, Xf.Identity, Color.White, 0, ct);
            if (_segments > MaxSegments) break;
        }
    }

    private Color ResolveColor(Ent e, Color byBlock)
    {
        if (e.S(420) is { } tc && int.TryParse(tc, out int rgb)) return Color.FromArgb(255, Color.FromArgb(rgb));
        int ci = e.I(62, 256);
        if (ci == 0) return byBlock;
        if (ci == 256)
        {
            string layer = e.S(8) ?? "0";
            if (_layerColors.TryGetValue(layer, out var lc)) return lc; // may be Empty (off)
            return Color.White;
        }
        if (ci < 0) return Color.Empty;
        return Aci(ci);
    }

    private static bool OcsFlip(Ent e) => e.D(230, 1) < 0;

    private void Emit(Ent e, Xf xf, Color byBlock, int depth, CancellationToken ct)
    {
        if (_segments > MaxSegments) return;
        var col = ResolveColor(e, byBlock);
        if (col.IsEmpty) return;
        double fx = OcsFlip(e) ? -1 : 1;
        switch (e.Type)
        {
            case "LINE":
                Add(new[] { xf.Apply(e.D(10), e.D(20)), xf.Apply(e.D(11), e.D(21)) }, col, false);
                break;
            case "POINT":
            {
                var p = xf.Apply(e.D(10), e.D(20));
                Add(new[] { p, p }, col, false);
                break;
            }
            case "CIRCLE":
                Add(ArcPts(fx * e.D(10), e.D(20), e.D(40), 0, 360, xf, fx), col, true);
                break;
            case "ARC":
                Add(ArcPts(fx * e.D(10), e.D(20), e.D(40), e.D(50), e.D(51), xf, fx), col, false);
                break;
            case "ELLIPSE":
                Add(EllipsePts(e, xf), col, false);
                break;
            case "LWPOLYLINE":
            {
                var pts = new List<(double X, double Y, double Bulge)>();
                foreach (var (c, v) in e.Pairs)
                {
                    if (c == 10) pts.Add((fx * Num(v), 0, 0));
                    else if (c == 20 && pts.Count > 0) pts[^1] = (pts[^1].X, Num(v), pts[^1].Bulge);
                    else if (c == 42 && pts.Count > 0) pts[^1] = (pts[^1].X, pts[^1].Y, fx * Num(v));
                }
                bool closed = (e.I(70) & 1) != 0;
                Add(BulgePolyline(pts, closed, xf), col, closed);
                break;
            }
            case "POLYLINE":
            {
                int flags = e.I(70);
                if ((flags & 64) != 0 || e.Vertices == null) break; // polyface mesh: skip
                var pts = new List<(double X, double Y, double Bulge)>();
                foreach (var v in e.Vertices)
                {
                    if ((v.I(70) & 16) != 0) continue; // spline frame control point
                    pts.Add((fx * v.D(10), v.D(20), fx * v.D(42)));
                }
                bool closed = (flags & 1) != 0;
                Add(BulgePolyline(pts, closed, xf), col, closed);
                break;
            }
            case "SPLINE":
                Add(SplinePts(e, xf), col, (e.I(70) & 1) != 0);
                break;
            case "SOLID":
            case "TRACE":
            case "3DFACE":
            {
                var p1 = xf.Apply(e.D(10), e.D(20));
                var p2 = xf.Apply(e.D(11), e.D(21));
                var p3 = xf.Apply(e.D(12), e.D(22));
                var p4 = xf.Apply(e.D(13, e.D(12)), e.D(23, e.D(22)));
                bool fill = e.Type != "3DFACE";
                Add(fill ? new[] { p1, p2, p4, p3 } : new[] { p1, p2, p3, p4 }, col, true, fill);
                break;
            }
            case "HATCH":
                EmitHatch(e, xf, col);
                break;
            case "TEXT":
            case "ATTRIB":
            case "MTEXT":
            {
                string? text = e.Type == "MTEXT" ? MText(e) : e.S(1);
                if (string.IsNullOrWhiteSpace(text)) break;
                bool aligned = e.Type != "MTEXT" && (e.I(72) != 0 || e.I(73) != 0) && e.S(11) != null;
                var at = aligned ? xf.Apply(fx * e.D(11), e.D(21)) : xf.Apply(fx * e.D(10), e.D(20));
                double rot = e.D(50) * Math.PI / 180;
                if (e.Type == "MTEXT" && e.S(11) != null) rot = Math.Atan2(e.D(21), e.D(11));
                double h = e.D(40, 1) * xf.ScaleFactor;
                if (_texts.Count < 20000) _texts.Add(new Txt(at, h, rot + xf.Rotation, text, col));
                break;
            }
            case "INSERT":
            case "DIMENSION":
            {
                if (depth > 12) break;
                string? name = e.S(2);
                if (name == null || !_blocks.TryGetValue(name, out var blk)) break;
                if (e.Type == "DIMENSION")
                {
                    foreach (var be in blk.Ents) Emit(be, xf, col, depth + 1, ct);
                    break;
                }
                double sx = e.D(41, 1), sy = e.D(42, 1), rot = e.D(50) * Math.PI / 180;
                int cols = Math.Clamp(e.I(70, 1), 1, 100), rows = Math.Clamp(e.I(71, 1), 1, 100);
                double cs = e.D(44), rs = e.D(45);
                double ix = fx * e.D(10), iy = e.D(20);
                for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    // block -> (minus base) -> scale -> array offset -> rotate -> translate -> parent
                    var local = new Xf(sx, 0, 0, sy, -blk.BaseX * sx + c * cs, -blk.BaseY * sy + r * rs);
                    double cosr = Math.Cos(rot), sinr = Math.Sin(rot);
                    var rt = new Xf(fx * cosr, sinr, -fx * sinr, cosr, ix, iy);
                    var m = local.Then(rt).Then(xf);
                    foreach (var be in blk.Ents)
                    {
                        Emit(be, m, col, depth + 1, ct);
                        if (_segments > MaxSegments) return;
                    }
                    // attributes following an INSERT are separate ATTRIB entities and already emitted
                }
                break;
            }
        }
    }

    private void Add(PointD[] pts, Color c, bool closed, bool fill = false)
    {
        if (pts.Length == 0) return;
        _segments += pts.Length;
        _prims.Add(new Prim(pts, c, closed, fill));
    }

    private static double Num(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;

    private static PointD[] ArcPts(double cx, double cy, double r, double a0Deg, double a1Deg, Xf xf, double fx)
    {
        if (fx < 0) { (a0Deg, a1Deg) = (180 - a1Deg, 180 - a0Deg); }
        double a0 = a0Deg * Math.PI / 180, a1 = a1Deg * Math.PI / 180;
        while (a1 <= a0) a1 += 2 * Math.PI;
        if (a1Deg - a0Deg == 360) a1 = a0 + 2 * Math.PI;
        int n = (int)Math.Clamp((a1 - a0) / (2 * Math.PI) * 96, 8, 400);
        var pts = new PointD[n + 1];
        for (int i = 0; i <= n; i++)
        {
            double t = a0 + (a1 - a0) * i / n;
            pts[i] = xf.Apply(cx + r * Math.Cos(t), cy + r * Math.Sin(t));
        }
        return pts;
    }

    private static PointD[] EllipsePts(Ent e, Xf xf)
    {
        double cx = e.D(10), cy = e.D(20), mx = e.D(11), my = e.D(21), ratio = e.D(40, 1);
        double t0 = e.D(41, 0), t1 = e.D(42, 2 * Math.PI);
        if (OcsFlip(e)) { cx = -cx; mx = -mx; (t0, t1) = (-t1, -t0); }
        while (t1 <= t0) t1 += 2 * Math.PI;
        double nx = -my * ratio, ny = mx * ratio;
        int n = (int)Math.Clamp((t1 - t0) / (2 * Math.PI) * 96, 8, 400);
        var pts = new PointD[n + 1];
        for (int i = 0; i <= n; i++)
        {
            double t = t0 + (t1 - t0) * i / n;
            pts[i] = xf.Apply(cx + mx * Math.Cos(t) + nx * Math.Sin(t), cy + my * Math.Cos(t) + ny * Math.Sin(t));
        }
        return pts;
    }

    private static PointD[] BulgePolyline(List<(double X, double Y, double Bulge)> pts, bool closed, Xf xf)
    {
        var res = new List<PointD>(pts.Count * 2);
        int segs = closed ? pts.Count : pts.Count - 1;
        if (pts.Count == 1) return new[] { xf.Apply(pts[0].X, pts[0].Y) };
        for (int i = 0; i < segs; i++)
        {
            var a = pts[i];
            var b = pts[(i + 1) % pts.Count];
            if (i == 0) res.Add(xf.Apply(a.X, a.Y));
            if (Math.Abs(a.Bulge) > 1e-9)
            {
                double dx = b.X - a.X, dy = b.Y - a.Y, chord = Math.Sqrt(dx * dx + dy * dy);
                if (chord > 1e-12)
                {
                    double theta = 4 * Math.Atan(a.Bulge);
                    double r = chord / (2 * Math.Sin(theta / 2));
                    double mxp = (a.X + b.X) / 2, myp = (a.Y + b.Y) / 2;
                    double sagDist = r * Math.Cos(theta / 2);
                    // center is perpendicular to the chord at distance sagDist (to the left for positive bulge)
                    double ux = -dy / chord, uy = dx / chord;
                    double ccx = mxp + ux * sagDist, ccy = myp + uy * sagDist;
                    double start = Math.Atan2(a.Y - ccy, a.X - ccx);
                    int n = (int)Math.Clamp(Math.Abs(theta) / (2 * Math.PI) * 96, 4, 200);
                    double rr = Math.Abs(r);
                    for (int k = 1; k < n; k++)
                    {
                        double t = start + theta * k / n;
                        res.Add(xf.Apply(ccx + rr * Math.Cos(t), ccy + rr * Math.Sin(t)));
                    }
                }
            }
            res.Add(xf.Apply(b.X, b.Y));
        }
        return res.ToArray();
    }

    private static PointD[] SplinePts(Ent e, Xf xf)
    {
        int degree = e.I(71, 3);
        var knots = new List<double>();
        var ctrl = new List<(double X, double Y)>();
        var fit = new List<(double X, double Y)>();
        var weights = new List<double>();
        foreach (var (c, v) in e.Pairs)
        {
            switch (c)
            {
                case 40: knots.Add(Num(v)); break;
                case 41: weights.Add(Num(v)); break;
                case 10: ctrl.Add((Num(v), 0)); break;
                case 20: if (ctrl.Count > 0) ctrl[^1] = (ctrl[^1].X, Num(v)); break;
                case 11: fit.Add((Num(v), 0)); break;
                case 21: if (fit.Count > 0) fit[^1] = (fit[^1].X, Num(v)); break;
            }
        }
        if (ctrl.Count > degree && knots.Count == ctrl.Count + degree + 1)
        {
            bool rational = weights.Count == ctrl.Count;
            int n = Math.Clamp(ctrl.Count * 12, 16, 2000);
            double u0 = knots[degree], u1 = knots[ctrl.Count];
            var pts = new PointD[n + 1];
            for (int i = 0; i <= n; i++)
            {
                double u = u0 + (u1 - u0) * i / n;
                var (x, y) = DeBoor(degree, knots, ctrl, rational ? weights : null, Math.Min(u, u1 - 1e-12 * Math.Max(1, Math.Abs(u1))));
                pts[i] = xf.Apply(x, y);
            }
            return pts;
        }
        var src = fit.Count >= 2 ? fit : ctrl;
        var r = new PointD[src.Count];
        for (int i = 0; i < src.Count; i++) r[i] = xf.Apply(src[i].X, src[i].Y);
        return r;
    }

    private static (double, double) DeBoor(int p, List<double> t, List<(double X, double Y)> c, List<double>? w, double u)
    {
        int k = p;
        for (int i = p; i < c.Count; i++) if (u >= t[i] && u < t[i + 1]) { k = i; break; }
        var dx = new double[p + 1];
        var dy = new double[p + 1];
        var dw = new double[p + 1];
        for (int j = 0; j <= p; j++)
        {
            int idx = Math.Clamp(j + k - p, 0, c.Count - 1);
            double wt = w?[idx] ?? 1;
            dx[j] = c[idx].X * wt;
            dy[j] = c[idx].Y * wt;
            dw[j] = wt;
        }
        for (int r = 1; r <= p; r++)
        {
            for (int j = p; j >= r; j--)
            {
                int i = j + k - p;
                double den = t[i + p - r + 1] - t[i];
                double a = den == 0 ? 0 : (u - t[i]) / den;
                dx[j] = (1 - a) * dx[j - 1] + a * dx[j];
                dy[j] = (1 - a) * dy[j - 1] + a * dy[j];
                dw[j] = (1 - a) * dw[j - 1] + a * dw[j];
            }
        }
        double ww = dw[p] == 0 ? 1 : dw[p];
        return (dx[p] / ww, dy[p] / ww);
    }

    private void EmitHatch(Ent e, Xf xf, Color col)
    {
        bool solid = e.I(70) == 1;
        var pairs = e.Pairs;
        int i = pairs.FindIndex(p => p.Code == 91);
        if (i < 0) return;
        int paths = (int)Num(pairs[i].Value);
        i++;
        var loops = new List<PointD[]>();
        for (int pth = 0; pth < paths && i < pairs.Count; pth++)
        {
            while (i < pairs.Count && pairs[i].Code != 92) i++;
            if (i >= pairs.Count) break;
            int flags = (int)Num(pairs[i++].Value);
            var loop = new List<PointD>();
            if ((flags & 2) != 0)
            {
                bool hasBulge = false;
                int nv = 0;
                while (i < pairs.Count && pairs[i].Code is 72 or 73 or 93)
                {
                    if (pairs[i].Code == 72) hasBulge = Num(pairs[i].Value) != 0;
                    if (pairs[i].Code == 93) nv = (int)Num(pairs[i].Value);
                    i++;
                }
                var pts = new List<(double, double, double)>();
                for (int v = 0; v < nv && i < pairs.Count; v++)
                {
                    double x = 0, y = 0, b = 0;
                    if (pairs[i].Code == 10) x = Num(pairs[i++].Value);
                    if (i < pairs.Count && pairs[i].Code == 20) y = Num(pairs[i++].Value);
                    if (hasBulge && i < pairs.Count && pairs[i].Code == 42) b = Num(pairs[i++].Value);
                    pts.Add((x, y, b));
                }
                loop.AddRange(BulgePolyline(pts, true, xf));
            }
            else
            {
                int ne = 0;
                if (i < pairs.Count && pairs[i].Code == 93) ne = (int)Num(pairs[i++].Value);
                for (int ed = 0; ed < ne && i < pairs.Count; ed++)
                {
                    while (i < pairs.Count && pairs[i].Code != 72) i++;
                    if (i >= pairs.Count) break;
                    int type = (int)Num(pairs[i++].Value);
                    var f = new Dictionary<int, double>();
                    var fitPts = new List<(double X, double Y)>();
                    // read until the next edge (72) or end of this path's edges (97/92/75)
                    while (i < pairs.Count && pairs[i].Code is not (72 or 97 or 92 or 75 or 76 or 98))
                    {
                        if (type == 4 && pairs[i].Code == 10) fitPts.Add((Num(pairs[i].Value), 0));
                        else if (type == 4 && pairs[i].Code == 20 && fitPts.Count > 0) fitPts[^1] = (fitPts[^1].X, Num(pairs[i].Value));
                        else f.TryAdd(pairs[i].Code, Num(pairs[i].Value));
                        i++;
                    }
                    double G(int c) => f.TryGetValue(c, out var v) ? v : 0;
                    switch (type)
                    {
                        case 1: loop.Add(xf.Apply(G(10), G(20))); loop.Add(xf.Apply(G(11), G(21))); break;
                        case 2:
                        {
                            double a0 = G(50), a1 = G(51);
                            bool ccw = !f.ContainsKey(73) || G(73) != 0;
                            if (!ccw) (a0, a1) = (360 - a1, 360 - a0);
                            var arc = ArcPts(G(10), G(20), G(40), a0, a1, Xf.Identity, 1);
                            if (!ccw) { Array.Reverse(arc); for (int k = 0; k < arc.Length; k++) arc[k] = new PointD(arc[k].X, 2 * G(20) - arc[k].Y); }
                            foreach (var p in arc) loop.Add(xf.Apply(p.X, p.Y));
                            break;
                        }
                        case 3:
                        {
                            double cx = G(10), cy = G(20), mx = G(11), my = G(21), ratio = G(40);
                            double t0 = G(50) * Math.PI / 180, t1 = G(51) * Math.PI / 180;
                            while (t1 <= t0) t1 += 2 * Math.PI;
                            for (int k = 0; k <= 48; k++)
                            {
                                double t = t0 + (t1 - t0) * k / 48;
                                loop.Add(xf.Apply(cx + mx * Math.Cos(t) - my * ratio * Math.Sin(t), cy + my * Math.Cos(t) + mx * ratio * Math.Sin(t)));
                            }
                            break;
                        }
                        case 4:
                            foreach (var p in fitPts) loop.Add(xf.Apply(p.X, p.Y));
                            break;
                    }
                }
            }
            if (loop.Count >= 2) loops.Add(loop.ToArray());
        }
        if (solid && loops.Count > 0)
        {
            // Encode all loops in one fill primitive (alternate fill gives holes) — stored as separate prims sharing fill.
            foreach (var l in loops) Add(l, col, true, fill: true);
        }
        else
        {
            foreach (var l in loops) Add(l, col, true);
        }
    }

    private static string MText(Ent e)
    {
        var sb = new StringBuilder();
        foreach (var (c, v) in e.Pairs) if (c == 3) sb.Append(v);
        sb.Append(e.S(1));
        string s = sb.ToString();
        var o = new StringBuilder(s.Length);
        for (int i = 0; i < s.Length; i++)
        {
            char ch = s[i];
            if (ch == '\\' && i + 1 < s.Length)
            {
                char n = s[i + 1];
                if (n is 'P' or 'n') { o.Append('\n'); i++; continue; }
                if (n is '\\' or '{' or '}') { o.Append(n); i++; continue; }
                if (n is 'S') { int semi = s.IndexOf(';', i); if (semi > 0) { o.Append(s, i + 2, semi - i - 2); i = semi; } continue; }
                if (char.IsLetter(n))
                {
                    int semi = s.IndexOf(';', i);
                    if (n is 'L' or 'l' or 'O' or 'o' or 'K' or 'k') { i++; continue; }
                    if (semi > 0) { i = semi; continue; }
                }
                continue;
            }
            if (ch is '{' or '}') continue;
            o.Append(ch);
        }
        return o.ToString().Replace("%%d", "°").Replace("%%c", "Ø").Replace("%%p", "±");
    }

    // ------------------------------------------------------------------ drawing

    private Bitmap? Draw(Size size)
    {
        double minX = double.MaxValue, minY = double.MaxValue, maxX = double.MinValue, maxY = double.MinValue;
        void Ext(PointD p)
        {
            if (double.IsNaN(p.X) || double.IsNaN(p.Y) || double.IsInfinity(p.X) || double.IsInfinity(p.Y)) return;
            if (p.X < minX) minX = p.X;
            if (p.Y < minY) minY = p.Y;
            if (p.X > maxX) maxX = p.X;
            if (p.Y > maxY) maxY = p.Y;
        }
        foreach (var p in _prims) foreach (var pt in p.Pts) Ext(pt);
        foreach (var t in _texts)
        {
            // Include the (approximate) text box, not just its anchor, so labels aren't clipped.
            int longest = 0;
            foreach (var l in t.Text.Split('\n')) longest = Math.Max(longest, l.Length);
            int lines = t.Text.Split('\n').Length;
            double tw = longest * t.Height * 0.6, th = t.Height * 1.25 * lines;
            double c = Math.Cos(t.Rot), s = Math.Sin(t.Rot);
            Ext(t.At);
            Ext(new PointD(t.At.X + tw * c, t.At.Y + tw * s));
            Ext(new PointD(t.At.X - th * s, t.At.Y + th * c));
            Ext(new PointD(t.At.X + tw * c - th * s, t.At.Y + tw * s + th * c));
        }
        if (minX > maxX) return null;
        double w = Math.Max(maxX - minX, 1e-9), h = Math.Max(maxY - minY, 1e-9);
        if (w < 1e-6 && h < 1e-6) { w = h = 1; }
        // Output aspect follows the drawing.
        double aspect = w / h;
        int bw = size.Width, bh = size.Height;
        if (aspect > (double)bw / bh) bh = Math.Max(64, (int)(bw / aspect)); else bw = Math.Max(64, (int)(bh * aspect));
        int margin = Math.Max(8, Math.Min(bw, bh) / 40);
        double scale = Math.Min((bw - 2.0 * margin) / w, (bh - 2.0 * margin) / h);
        float X(double x) => (float)(margin + (x - minX) * scale + ((bw - 2 * margin) - w * scale) / 2);
        float Y(double y) => (float)(bh - margin - (y - minY) * scale - ((bh - 2 * margin) - h * scale) / 2);

        var bmp = new Bitmap(bw, bh, PixelFormat.Format32bppPArgb);
        using var g = Graphics.FromImage(bmp);
        g.Clear(Color.FromArgb(33, 38, 46));
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        var pens = new Dictionary<int, Pen>();
        var brushes = new Dictionary<int, SolidBrush>();
        try
        {
            foreach (var p in _prims)
            {
                int key = p.Color.ToArgb();
                if (p.Fill && p.Pts.Length >= 3)
                {
                    if (!brushes.TryGetValue(key, out var br)) brushes[key] = br = new SolidBrush(p.Color);
                    var pts = new PointF[p.Pts.Length];
                    for (int i = 0; i < pts.Length; i++) pts[i] = new PointF(X(p.Pts[i].X), Y(p.Pts[i].Y));
                    g.FillPolygon(br, pts, FillMode.Alternate);
                    continue;
                }
                if (!pens.TryGetValue(key, out var pen)) pens[key] = pen = new Pen(p.Color, 1f);
                if (p.Pts.Length == 2 && p.Pts[0] == p.Pts[1])
                {
                    g.FillRectangle(Brushes.White, X(p.Pts[0].X) - 1, Y(p.Pts[0].Y) - 1, 2, 2);
                    continue;
                }
                var arr = new PointF[p.Pts.Length + (p.Closed ? 1 : 0)];
                for (int i = 0; i < p.Pts.Length; i++) arr[i] = new PointF(X(p.Pts[i].X), Y(p.Pts[i].Y));
                if (p.Closed) arr[^1] = arr[0];
                if (arr.Length >= 2) g.DrawLines(pen, arr);
            }
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;
            foreach (var t in _texts)
            {
                float px = (float)(t.Height * scale); // DXF height = cap height
                if (px < 4 || px > 2000) continue;
                float em = px / 0.7f;
                using var font = new Font("Segoe UI", em, GraphicsUnit.Pixel);
                using var br = new SolidBrush(t.Color);
                var st = g.Save();
                g.TranslateTransform(X(t.At.X), Y(t.At.Y));
                g.RotateTransform((float)(-t.Rot * 180 / Math.PI));
                g.DrawString(t.Text, font, br, 0, -em * 1.05f, StringFormat.GenericTypographic);
                g.Restore(st);
            }
        }
        finally
        {
            foreach (var p in pens.Values) p.Dispose();
            foreach (var b in brushes.Values) b.Dispose();
        }
        return bmp;
    }

    private static Color Aci(int i)
    {
        switch (i)
        {
            case 1: return Color.FromArgb(255, 0, 0);
            case 2: return Color.FromArgb(255, 255, 0);
            case 3: return Color.FromArgb(0, 255, 0);
            case 4: return Color.FromArgb(0, 255, 255);
            case 5: return Color.FromArgb(0, 0, 255);
            case 6: return Color.FromArgb(255, 0, 255);
            case 7: return Color.FromArgb(255, 255, 255);
            case 8: return Color.FromArgb(128, 128, 128);
            case 9: return Color.FromArgb(192, 192, 192);
        }
        if (i >= 250 && i <= 255)
        {
            int v = 51 + (i - 250) * 40;
            return Color.FromArgb(v, v, v);
        }
        if (i < 10 || i > 249) return Color.White;
        double hue = (i / 10 - 1) * 15.0;
        int d = i % 10;
        double[] val = { 1.0, 1.0, 0.8, 0.8, 0.6, 0.6, 0.5, 0.5, 0.3, 0.3 };
        double sat = d % 2 == 0 ? 1.0 : 0.5;
        return FromHsv(hue, sat, val[d]);
    }

    private static Color FromHsv(double h, double s, double v)
    {
        double c = v * s, x = c * (1 - Math.Abs(h / 60 % 2 - 1)), m = v - c;
        (double r, double g, double b) = h switch
        {
            < 60 => (c, x, 0.0), < 120 => (x, c, 0.0), < 180 => (0.0, c, x),
            < 240 => (0.0, x, c), < 300 => (x, 0.0, c), _ => (c, 0.0, x),
        };
        return Color.FromArgb((int)((r + m) * 255), (int)((g + m) * 255), (int)((b + m) * 255));
    }
}
