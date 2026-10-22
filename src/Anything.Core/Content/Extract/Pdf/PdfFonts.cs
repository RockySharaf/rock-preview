using System.Globalization;
using System.Text;

namespace Anything.Core.Content.Extract;

/// <summary>
/// A CMap: codespace ranges (how many bytes a code takes), Unicode mappings (bfchar, bfrange with arrays) and CID
/// mappings (cidchar, cidrange), at most 65,536 entries; ranges stay ranges, so &lt;0000&gt; &lt;FFFF&gt; costs one entry.
/// Codespace ranges are kept by code length, at most 256 of them (real CMaps define a handful): each code of a string
/// is matched against them.
/// </summary>
internal sealed class PdfCMap
{
    /// <summary>Most entries kept (bfchar, bfrange and their arrays, cidchar, cidrange).</summary>
    public const int MaxEntries = 65_536;
    /// <summary>Most codespace ranges kept (more are dropped, as duplicates are).</summary>
    public const int MaxCodespace = 256;

    // Codespace ranges by code length (1 to 4 bytes).
    readonly List<(uint Lo, uint Hi)>[] _codespace = [[], [], [], [], []];
    int _codespaceCount, _shortest = 1;
    readonly Dictionary<uint, string> _chars = [];
    readonly List<(uint Lo, uint Hi, string Start)> _ranges = [];
    readonly Dictionary<uint, int> _cids = [];
    readonly List<(uint Lo, uint Hi, int Cid)> _cidRanges = [];
    int _entries, _max = MaxEntries;

    /// <summary>The predefined CMap this one builds on (/UseCMap or "usecmap").</summary>
    public string? UseCMap { get; private set; }
    /// <summary>1 = vertical writing.</summary>
    public int WMode { get; private set; }
    /// <summary>Some codespace range is defined.</summary>
    public bool HasCodespace => _codespaceCount > 0;
    /// <summary>Unicode mappings were found.</summary>
    public bool HasUnicode => _chars.Count > 0 || _ranges.Count > 0;
    /// <summary>Entries kept (mappings and codespace ranges): what the document's font tables are charged for it.</summary>
    public int Entries => _entries + _codespaceCount;

    /// <summary>
    /// Parses a CMap program in place (never throws for bad data: what was read before an error is kept), keeping at most
    /// <paramref name="maxEntries"/> mappings (what is left of the document's font tables).
    /// </summary>
    public static PdfCMap Parse(PdfBytes data, ExtractBudget budget, int maxEntries = MaxEntries)
    {
        var map = new PdfCMap { _max = Math.Clamp(maxEntries, 0, MaxEntries) };
        var lx = new PdfLexer(data.Array, data.Length);
        var operands = new List<PdfObject>(8);
        int tokens = 0;
        string section = "";
        try
        {
            while (lx.Next())
            {
                if ((++tokens & 4095) == 0) budget.Check();
                switch (lx.Kind)
                {
                    case PdfToken.Keyword:
                        string k = lx.Text;
                        if (k.StartsWith("begin", StringComparison.Ordinal) && k.EndsWith("range", StringComparison.Ordinal) || k is "beginbfchar" or "begincidchar")
                        {
                            section = k;
                            operands.Clear();
                            continue;
                        }
                        if (k.StartsWith("end", StringComparison.Ordinal) && section.Length > 0) { section = ""; operands.Clear(); continue; }
                        if (k == "usecmap" && operands.Count > 0 && operands[^1] is PdfName used) map.UseCMap = used.Value;
                        if (k == "def" && operands.Count >= 2 && operands[^2] is PdfName key)
                        {
                            if (key.Value == "WMode" && operands[^1] is PdfNumber wm) map.WMode = (int)wm.Value;
                            if (key.Value == "UseCMap" && operands[^1] is PdfName um) map.UseCMap = um.Value;
                        }
                        operands.Clear();
                        continue;
                    case PdfToken.ArrayOpen:
                    case PdfToken.DictOpen:
                        operands.Add(PdfParser.Parse(lx, false, budget, out _));
                        break;
                    default:
                        operands.Add(PdfParser.Parse(lx, false, budget, out _));
                        break;
                }
                if (map._entries >= map._max) break;
                map.Consume(section, operands);
            }
        }
        catch (Exception e) when (PdfFile.IsFormatError(e)) { }
        map.Finish();
        return map;
    }

    void Consume(string section, List<PdfObject> operands)
    {
        switch (section)
        {
            case "begincodespacerange" when operands.Count == 2:
                if (operands[0] is PdfString lo && operands[1] is PdfString hi && lo.Bytes.Length is >= 1 and <= 4 && hi.Bytes.Length == lo.Bytes.Length
                    && _codespaceCount < MaxCodespace)
                {
                    var range = (Code(lo.Bytes), Code(hi.Bytes));
                    var same = _codespace[lo.Bytes.Length];
                    if (!same.Contains(range)) { same.Add(range); _codespaceCount++; }
                }
                operands.Clear();
                break;
            case "beginbfchar" when operands.Count == 2:
                if (operands[0] is PdfString src && src.Bytes.Length is >= 1 and <= 4 && Destination(operands[1]) is { } dst)
                {
                    _chars[Code(src.Bytes)] = dst;
                    _entries++;
                }
                operands.Clear();
                break;
            case "beginbfrange" when operands.Count == 3:
                if (operands[0] is PdfString rlo && operands[1] is PdfString rhi && rlo.Bytes.Length is >= 1 and <= 4 && rhi.Bytes.Length <= 4)
                {
                    uint a = Code(rlo.Bytes), b = Code(rhi.Bytes);
                    if (b >= a)
                    {
                        if (operands[2] is PdfArray list)
                        {
                            for (int i = 0; i < list.Count && a + (uint)i <= b && _entries < _max; i++)
                                if (Destination(list[i]) is { } item) { _chars[a + (uint)i] = item; _entries++; }
                        }
                        else if (Destination(operands[2]) is { Length: > 0 } start)
                        {
                            _ranges.Add((a, b, start));
                            _entries++;
                        }
                    }
                }
                operands.Clear();
                break;
            case "begincidchar" when operands.Count == 2:
                if (operands[0] is PdfString cs && cs.Bytes.Length is >= 1 and <= 4 && operands[1] is PdfNumber cid)
                {
                    _cids[Code(cs.Bytes)] = (int)Math.Clamp(cid.Value, 0, 65535);
                    _entries++;
                }
                operands.Clear();
                break;
            case "begincidrange" when operands.Count == 3:
                if (operands[0] is PdfString clo && operands[1] is PdfString chi && operands[2] is PdfNumber first && clo.Bytes.Length is >= 1 and <= 4)
                {
                    uint a = Code(clo.Bytes), b = Code(chi.Bytes);
                    if (b >= a) { _cidRanges.Add((a, b, (int)Math.Clamp(first.Value, 0, 65535))); _entries++; }
                }
                operands.Clear();
                break;
            case "":
                if (operands.Count > 16) operands.RemoveAt(0);
                break;
            default:
                if (operands.Count > 3) operands.Clear();
                break;
        }
    }

    void Finish()
    {
        _ranges.Sort((x, y) => x.Lo.CompareTo(y.Lo));
        _cidRanges.Sort((x, y) => x.Lo.CompareTo(y.Lo));
        _shortest = 1;
        for (int n = 1; n <= 4; n++)
            if (_codespace[n].Count > 0) { _shortest = n; break; }
    }

    static uint Code(byte[] b)
    {
        uint v = 0;
        foreach (byte x in b) v = (v << 8) | x;
        return v;
    }

    /// <summary>A bf destination: UTF-16BE bytes (a lone byte as itself, UCS-4 when it looks so) or a glyph name.</summary>
    static string? Destination(PdfObject o)
    {
        switch (o)
        {
            case PdfName n: return PdfGlyphNames.ToUnicode(n.Value);
            case PdfString s:
                var b = s.Bytes;
                if (b.Length == 0) return null;
                if (b.Length == 1) return ((char)b[0]).ToString();
                if (b.Length == 4 && b[0] == 0 && b[1] == 0 && !(b[2] is >= 0xD8 and <= 0xDB))
                {
                    int cp = (b[2] << 8) | b[3];
                    return ((char)cp).ToString();
                }
                return Encoding.BigEndianUnicode.GetString(b, 0, b.Length & ~1);
            default: return null;
        }
    }

    /// <summary>The Unicode text of a code, or null.</summary>
    public string? Lookup(uint code)
    {
        if (_chars.TryGetValue(code, out var s)) return s;
        int i = FindRange(_ranges, code);
        if (i < 0) return null;
        var (lo, _, start) = _ranges[i];
        return Offset(start, code - lo);
    }

    /// <summary>The CID of a code, or null when the CMap doesn't say.</summary>
    public int? Cid(uint code)
    {
        if (_cids.TryGetValue(code, out int c)) return c;
        int lo = 0, hi = _cidRanges.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (_cidRanges[mid].Lo <= code) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        if (found >= 0 && code <= _cidRanges[found].Hi) return _cidRanges[found].Cid + (int)(code - _cidRanges[found].Lo);
        return null;
    }

    static int FindRange(List<(uint Lo, uint Hi, string Start)> ranges, uint code)
    {
        int lo = 0, hi = ranges.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (ranges[mid].Lo <= code) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        // Overlapping ranges: walk back to one that holds the code.
        for (int i = found; i >= 0 && i > found - 8; i--)
            if (code >= ranges[i].Lo && code <= ranges[i].Hi) return i;
        return -1;
    }

    /// <summary>The range start with its last character moved on by delta (a surrogate pair as one code point).</summary>
    static string? Offset(string start, uint delta)
    {
        if (delta == 0) return start;
        if (start.Length >= 2 && char.IsSurrogatePair(start[^2], start[^1]))
        {
            long cp = char.ConvertToUtf32(start[^2], start[^1]) + (long)delta;
            if (cp > 0x10FFFF) return null;
            return string.Concat(start.AsSpan(0, start.Length - 2), char.ConvertFromUtf32((int)cp));
        }
        long v = start[^1] + (long)delta;
        if (v > 0xFFFF) return null;
        if (v is >= 0xD800 and <= 0xDFFF) return "�";
        return string.Concat(start.AsSpan(0, start.Length - 1), ((char)v).ToString());
    }

    /// <summary>The length in bytes of the code starting at s[i] by the codespace ranges; 0 when none fits.</summary>
    public int CodeLength(ReadOnlySpan<byte> s, int i)
    {
        uint v = 0;
        for (int n = 1; n <= 4 && i + n <= s.Length; n++)
        {
            v = (v << 8) | s[i + n - 1];
            foreach (var (lo, hi) in _codespace[n])
                if (v >= lo && v <= hi) return n;
        }
        return 0;
    }

    /// <summary>The shortest code length defined (1 when none).</summary>
    public int ShortestCode => _shortest;
}

/// <summary>
/// One font as text extraction sees it: how a string splits into codes, each code's Unicode text and width.
/// Simple fonts (Type1, TrueType, Type3) map codes through ToUnicode, then /Differences glyph names over the base
/// encoding; Type0 fonts through ToUnicode, then the predefined CJK CMaps (UCS2/UTF16 as UTF-16BE, RKSJ, GBK and
/// GB EUC, UHC and KSC EUC, Big5 through code pages 932, 936, 949, 950). Widths: /Widths, /MissingWidth, the
/// standard 14 fonts' built-in widths, /AvgWidth; for CID fonts /W and /DW (default 1000).
/// </summary>
internal sealed class PdfFont
{
    static PdfFont() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    enum Split : byte { One, Two, Ucs2, Utf16, Utf8, Utf32, Multi, CodePage, Codespace }

    readonly int _id;
    // Simple fonts.
    readonly string?[]? _simple;
    readonly double[]? _simpleWidths;
    // CID fonts.
    Split _split = Split.One;
    PdfCMap? _encodingCMap;
    PdfCMap? _toUnicode;
    Encoding? _codePage;
    bool _identityUnicode, _ucsUnicode;
    readonly Dictionary<uint, string?> _cache = [];
    // The document, whose font tables the cache is charged to (CID fonts).
    PdfFile? _file;
    List<(int First, int Last, double Width)>? _cidWidths;
    double _defaultWidth = 1000, _verticalAdvance = -1000;
    bool _cidIsCode;

    PdfFont(int id, string?[]? simple, double[]? widths)
    {
        _id = id;
        _simple = simple;
        _simpleWidths = widths;
    }

    /// <summary>A number for this font within its document (garbage counts).</summary>
    public int Id => _id;
    /// <summary>Codes take more than one byte (Type0).</summary>
    public bool IsComposite => _simple == null;
    /// <summary>Vertical writing (-V CMaps, WMode 1).</summary>
    public bool Vertical { get; private set; }
    /// <summary>Top of the glyphs above the baseline, in text space per unit of font size.</summary>
    public double Ascent { get; private set; } = 0.8;
    /// <summary>Bottom of the glyphs below the baseline (negative), in text space per unit of font size.</summary>
    public double Descent { get; private set; } = -0.2;
    /// <summary>Type3 glyph widths scale (FontMatrix a × 1000); 1 for other fonts.</summary>
    public double WidthScale { get; private set; } = 1;
    /// <summary>Glyphs of this font seen and how many of them weren't readable (the per-font garbage guard).</summary>
    public int Seen, Bad;

    /// <summary>Most of this font's glyphs aren't readable text: its later runs are left out.</summary>
    public bool IsGarbage => Seen >= 20 && Bad * 10 > Seen * 3;

    /// <summary>The next code in a string at i; returns its length in bytes (at least 1).</summary>
    public int NextCode(ReadOnlySpan<byte> s, int i, out uint code)
    {
        int n = _split switch
        {
            Split.One => 1,
            Split.Two or Split.Ucs2 => 2,
            Split.Utf16 => i + 1 < s.Length && s[i] is >= 0xD8 and <= 0xDB ? 4 : 2,
            Split.Utf32 => 4,
            Split.Utf8 => s[i] switch { < 0x80 => 1, >= 0xF0 => 4, >= 0xE0 => 3, >= 0xC0 => 2, _ => 1 },
            Split.Multi => MultiByteLength(s[i]),
            Split.CodePage => MultiByteLength(s[i]),
            Split.Codespace => _encodingCMap!.CodeLength(s, i) is var len && len > 0 ? len : _encodingCMap.ShortestCode,
            _ => 1,
        };
        n = Math.Clamp(n, 1, s.Length - i);
        uint v = 0;
        for (int k = 0; k < n; k++) v = (v << 8) | s[i + k];
        code = v;
        return n;
    }

    byte _leadLow = 0x81, _leadHigh = 0xFE, _leadLow2 = 0, _leadHigh2 = 0;

    int MultiByteLength(byte b) => b >= _leadLow && b <= _leadHigh || _leadHigh2 != 0 && b >= _leadLow2 && b <= _leadHigh2 ? 2 : 1;

    /// <summary>The Unicode text of a code; null when the font doesn't say (unmapped).</summary>
    public string? Unicode(uint code, int length)
    {
        if (_simple != null) return code < 256 ? _simple[code] : null;
        if (_cache.TryGetValue(code, out var hit)) return hit;
        string? s = _toUnicode?.Lookup(code);
        if (s == null && _ucsUnicode) s = CodeAsText(code, length);
        if (s == null && _codePage != null) s = FromCodePage(code, length);
        if (s == null && _identityUnicode) s = code is > 0 and <= 0xFFFF and not (>= 0xD800 and <= 0xDFFF) ? ((char)code).ToString() : null;
        // Kept while the document's font tables have room (thousands of fonts each showing many codes add up).
        if (_cache.Count < 65_536 && _file is { FontTableRoom: > 0 } file)
        {
            _cache[code] = s;
            file.ChargeFontTable(1);
        }
        return s;
    }

    string? CodeAsText(uint code, int length)
    {
        switch (_split)
        {
            case Split.Ucs2: return code is > 0 and <= 0xFFFF and not (>= 0xD800 and <= 0xDFFF) ? ((char)code).ToString() : null;
            case Split.Utf16:
                if (length == 4)
                {
                    char hi = (char)(code >> 16), lo = (char)(code & 0xFFFF);
                    return char.IsSurrogatePair(hi, lo) ? new string([hi, lo]) : null;
                }
                return code is > 0 and <= 0xFFFF and not (>= 0xD800 and <= 0xDFFF) ? ((char)code).ToString() : null;
            case Split.Utf32: return code is > 0 and <= 0x10FFFF and not (>= 0xD800 and <= 0xDFFF) ? char.ConvertFromUtf32((int)code) : null;
            case Split.Utf8:
                {
                    Span<byte> b = stackalloc byte[length];
                    for (int k = 0; k < length; k++) b[length - 1 - k] = (byte)(code >> (8 * k));
                    return System.Text.Unicode.Utf8.IsValid(b) ? Encoding.UTF8.GetString(b) : null;
                }
            default: return null;
        }
    }

    string? FromCodePage(uint code, int length)
    {
        Span<byte> b = stackalloc byte[length];
        for (int k = 0; k < length; k++) b[length - 1 - k] = (byte)(code >> (8 * k));
        try
        {
            string s = _codePage!.GetString(b);
            return s.Length == 0 || s.Contains('�') || s.Contains('?') && !b.Contains((byte)'?') ? null : s;
        }
        catch (ArgumentException) { return null; }
    }

    /// <summary>Horizontal advance of a code in thousandths of the font size (Type3 widths already scaled).</summary>
    public double Width(uint code)
    {
        if (_simpleWidths != null) return code < 256 ? _simpleWidths[code] : 0;
        if (_cidWidths == null) return _defaultWidth;
        int cid = _cidIsCode ? (int)Math.Min(code, int.MaxValue) : _encodingCMap?.Cid(code) ?? -1;
        if (cid < 0) return _defaultWidth;
        int lo = 0, hi = _cidWidths.Count - 1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            var r = _cidWidths[mid];
            if (cid < r.First) hi = mid - 1;
            else if (cid > r.Last) lo = mid + 1;
            else return r.Width;
        }
        return _defaultWidth;
    }

    /// <summary>Vertical advance (negative is down) in thousandths of the font size, for vertical fonts.</summary>
    public double VerticalAdvance => _verticalAdvance;

    // ── Loading ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Loads a font dictionary; never throws for bad data (a font that can't be read maps nothing).</summary>
    public static PdfFont Load(PdfFile file, PdfDict dict, int id)
    {
        try
        {
            var toUnicodeRef = dict["ToUnicode"];
            var toUnicodeObject = file.Resolve(toUnicodeRef);
            PdfCMap? toUnicode = toUnicodeObject is PdfStream ts ? CMapOf(file, toUnicodeRef, ts, 4 << 20, TableToUnicode) : null;
            bool identity = toUnicodeObject is PdfName { Value: "Identity-H" or "Identity-V" or "Identity" };
            return dict.Name("Subtype") == "Type0"
                ? LoadComposite(file, dict, id, toUnicode, identity)
                : LoadSimple(file, dict, id, toUnicode);
        }
        catch (Exception e) when (PdfFile.IsFormatError(e))
        {
            return new PdfFont(id, new string?[256], new double[256]);
        }
    }

    static string BaseName(PdfDict dict)
    {
        string name = dict.Name("BaseFont") ?? "";
        int plus = name.IndexOf('+');
        return plus == 6 ? name[7..] : name;
    }

    static PdfFont LoadSimple(PdfFile file, PdfDict dict, int id, PdfCMap? toUnicode)
    {
        string baseName = BaseName(dict);
        string subtype = dict.Name("Subtype") ?? "Type1";
        var descriptor = file.ResolveDict(dict["FontDescriptor"]);
        int flags = (int)file.Number(descriptor?["Flags"], 0);
        bool symbolic = (flags & 4) != 0 && (flags & 32) == 0;
        var standard = Standard14.Find(baseName);

        // Base encoding and /Differences.
        char[]? baseTable = null;
        var differences = new string?[256];
        var encoding = file.Resolve(dict["Encoding"]);
        string? encodingName = encoding switch { PdfName n => n.Value, PdfDict d => d.Name("BaseEncoding"), _ => null };
        baseTable = Encodings.ByName(encodingName);
        if (baseTable == null)
        {
            if (standard?.Symbol == true) baseTable = Encodings.Symbol;
            else if (standard?.Dingbats == true) baseTable = null;
            else if (subtype == "TrueType" && symbolic) baseTable = Encodings.WinAnsi;
            else baseTable = Encodings.Standard;
        }
        if (encoding is PdfDict ed && file.Resolve(ed["Differences"]) is PdfArray diffs)
        {
            int code = -1;
            for (int i = 0; i < diffs.Count; i++)
            {
                var item = file.Resolve(diffs[i]);
                if (item is PdfNumber num) code = (int)num.Value;
                else if (item is PdfName name && code is >= 0 and < 256) differences[code++] = name.Value;
            }
        }
        var table = new string?[256];
        for (int c = 0; c < 256; c++)
        {
            string? s = toUnicode?.Lookup((uint)c);
            if (s == null)
            {
                if (differences[c] is { } glyph) s = PdfGlyphNames.ToUnicode(glyph);
                else if (baseTable != null && baseTable[c] != '\0') s = Cached(baseTable[c]);
            }
            table[c] = s;
        }

        // Widths.
        var widths = new double[256];
        int firstChar = (int)file.Number(dict["FirstChar"], 0);
        var w = file.Resolve(dict["Widths"]) as PdfArray;
        double missing = file.Number(descriptor?["MissingWidth"], 0);
        double average = file.Number(descriptor?["AvgWidth"], 0);
        bool type3 = subtype == "Type3";
        double scale = 1;
        var font = new PdfFont(id, table, widths);
        if (type3)
        {
            var m = file.Numbers(dict["FontMatrix"], 6) ?? [0.001, 0, 0, 0.001, 0, 0];
            scale = Math.Abs(m[0]) * 1000;
            if (scale is <= 0 or > 1e6 || !double.IsFinite(scale)) scale = 1;
            font.WidthScale = scale;
            if (file.Numbers(dict["FontBBox"], 4) is { } bbox && m[3] != 0)
            {
                double top = Math.Max(bbox[1], bbox[3]) * Math.Abs(m[3]), bottom = Math.Min(bbox[1], bbox[3]) * Math.Abs(m[3]);
                if (top > 0 && top < 4) font.Ascent = top;
                if (bottom < 0 && bottom > -4) font.Descent = bottom;
            }
        }
        for (int c = 0; c < 256; c++)
        {
            int i = c - firstChar;
            double value = -1;
            if (w != null && i >= 0 && i < w.Count && file.Resolve(w[i]) is PdfNumber n && double.IsFinite(n.Value)) value = n.Value * scale;
            if (value < 0 && missing > 0) value = missing;
            if (value < 0 && standard != null) value = standard.Width(table[c]);
            if (value < 0 && average > 0) value = average;
            if (value < 0) value = 500;
            widths[c] = value;
        }
        font.SetMetrics(file, descriptor);
        return font;
    }

    void SetMetrics(PdfFile file, PdfDict? descriptor)
    {
        if (descriptor == null) return;
        double ascent = file.Number(descriptor["Ascent"], 0), descent = file.Number(descriptor["Descent"], 0);
        if (ascent is > 100 and < 2000) Ascent = ascent / 1000;
        if (descent is < -10 and > -1000) Descent = descent / 1000;
    }

    /// <summary>Kinds of font tables PdfFile.FontTables keeps, by the object they came from.</summary>
    const byte TableToUnicode = 0, TableEncoding = 1, TableWidthsArray = 2, TableWidthsOfDescendant = 3;

    /// <summary>
    /// A CMap stream parsed once per document (by its object number: fonts that share it share it), cut to what is left of
    /// the document's font tables and charged to them; null when nothing is left (no mapping, the font's fallbacks apply).
    /// </summary>
    static PdfCMap? CMapOf(PdfFile file, PdfObject? reference, PdfStream stream, int maxBytes, byte kind)
    {
        (byte, int)? key = reference is PdfRef r ? (kind, r.Number) : null;
        if (key is { } known && file.FontTables.TryGetValue(known, out var table)) return table as PdfCMap;
        PdfCMap? map = null;
        file.Budget.Check();
        if (file.FontTableRoom > 0 && file.ReadStream(stream, maxBytes) is { } data)
        {
            try { map = PdfCMap.Parse(data, file.Budget, file.FontTableRoom); }
            finally { data.Release(); }
            file.ChargeFontTable(map.Entries);
        }
        if (key is { } k) file.FontTables[k] = map;
        return map;
    }

    static PdfFont LoadComposite(PdfFile file, PdfDict dict, int id, PdfCMap? toUnicode, bool identity)
    {
        var font = new PdfFont(id, null, null) { _toUnicode = toUnicode, _identityUnicode = identity, _file = file };
        var encodingRef = dict["Encoding"];
        var encoding = file.Resolve(encodingRef);
        string? predefined = null;
        if (encoding is PdfName en) predefined = en.Value;
        else if (encoding is PdfStream es && CMapOf(file, encodingRef, es, 1 << 20, TableEncoding) is { } cmap)
        {
            predefined = cmap.UseCMap ?? file.ResolveDict(es)?.Name("UseCMap");
            if (cmap.HasCodespace) { font._encodingCMap = cmap; font._split = Split.Codespace; }
            if (cmap.WMode == 1) font.Vertical = true;
        }
        predefined ??= "Identity-H";
        font.Predefined(predefined, font._split == Split.Codespace);

        var descendants = file.Resolve(dict["DescendantFonts"]) as PdfArray;
        var descendantRef = descendants is { Count: > 0 } ? descendants[0] : null;
        var descendant = descendantRef != null ? file.ResolveDict(descendantRef) : null;
        if (descendant != null)
        {
            font._defaultWidth = file.Number(descendant["DW"], 1000);
            if (file.Numbers(descendant["DW2"], 2) is { } dw2) font._verticalAdvance = dw2[1];
            font._cidWidths = WidthsOf(file, descendant["W"], descendantRef);
            font.SetMetrics(file, file.ResolveDict(descendant["FontDescriptor"]));
        }
        return font;
    }

    /// <summary>
    /// A descendant's /W widths, parsed once per document (by the /W array's object, or the descendant's when /W is
    /// direct: many Type0 fonts can share one descendant), cut to what is left of the document's font tables.
    /// </summary>
    static List<(int First, int Last, double Width)>? WidthsOf(PdfFile file, PdfObject? w, PdfObject? descendantRef)
    {
        (byte, int)? key = w is PdfRef wr ? (TableWidthsArray, wr.Number) : descendantRef is PdfRef dr ? (TableWidthsOfDescendant, dr.Number) : null;
        if (key is { } known && file.FontTables.TryGetValue(known, out var table)) return table as List<(int, int, double)>;
        List<(int, int, double)>? widths = null;
        if (file.Resolve(w) is PdfArray wa && file.FontTableRoom > 0)
        {
            widths = CidWidths(file, wa, file.FontTableRoom);
            file.ChargeFontTable(widths.Count);
        }
        if (key is { } k) file.FontTables[k] = widths;
        return widths;
    }

    void Predefined(string name, bool keepSplit)
    {
        bool identity = name.StartsWith("Identity", StringComparison.Ordinal);
        if (name.EndsWith("-V", StringComparison.Ordinal) || name == "V") Vertical = true;
        Split split;
        if (identity) { split = Split.Two; _cidIsCode = true; }
        else if (name.Contains("UCS2", StringComparison.Ordinal)) { split = Split.Ucs2; _ucsUnicode = true; }
        else if (name.Contains("UTF16", StringComparison.Ordinal)) { split = Split.Utf16; _ucsUnicode = true; }
        else if (name.Contains("UTF32", StringComparison.Ordinal)) { split = Split.Utf32; _ucsUnicode = true; }
        else if (name.Contains("UTF8", StringComparison.Ordinal)) { split = Split.Utf8; _ucsUnicode = true; }
        else if (name.Contains("RKSJ", StringComparison.Ordinal))
        {
            split = Split.CodePage; _codePage = CodePage(932);
            _leadLow = 0x81; _leadHigh = 0x9F; _leadLow2 = 0xE0; _leadHigh2 = 0xFC;
        }
        else if (name.Contains("GBK", StringComparison.Ordinal) || name.Contains("GB-EUC", StringComparison.Ordinal) || name.StartsWith("GBpc-EUC", StringComparison.Ordinal) || name.StartsWith("GBT-EUC", StringComparison.Ordinal))
        { split = Split.CodePage; _codePage = CodePage(936); }
        else if (name.Contains("UHC", StringComparison.Ordinal) || name.StartsWith("KSC-EUC", StringComparison.Ordinal) || name.StartsWith("KSCpc-EUC", StringComparison.Ordinal))
        { split = Split.CodePage; _codePage = CodePage(949); }
        else if (name.Contains("B5", StringComparison.Ordinal))
        { split = Split.CodePage; _codePage = CodePage(950); }
        else if (name is "EUC-H" or "EUC-V")
        { split = Split.CodePage; _codePage = CodePage(51932) ?? CodePage(20932); _leadLow = 0x8E; }
        else split = Split.Two;
        if (!keepSplit) _split = split;
    }

    static Encoding? CodePage(int cp)
    {
        try { return Encoding.GetEncoding(cp, EncoderFallback.ExceptionFallback, new DecoderReplacementFallback("�")); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException) { return null; }
    }

    /// <summary>
    /// /W: "c [w1 w2 …]" and "c1 c2 w" entries as sorted ranges (at most 65,536, and at most <paramref name="max"/>);
    /// neighbouring codes of one width become one range, so a flat list costs one entry.
    /// </summary>
    static List<(int, int, double)> CidWidths(PdfFile file, PdfArray w, int max = PdfCMap.MaxEntries)
    {
        var list = new List<(int First, int Last, double Width)>();
        max = Math.Min(max, PdfCMap.MaxEntries);
        int i = 0, seen = 0;
        while (i < w.Count && list.Count < max)
        {
            if (file.Resolve(w[i]) is not PdfNumber first) { i++; continue; }
            var next = file.Resolve(w[i + 1]);
            if (next is PdfArray widths)
            {
                int c = (int)Math.Clamp(first.Value, 0, int.MaxValue - 65_536);
                for (int k = 0; k < widths.Count && list.Count < max; k++)
                {
                    if ((++seen & 4095) == 0) file.Budget.Check();
                    if (file.Resolve(widths[k]) is not PdfNumber wn) continue;
                    if (list.Count > 0 && list[^1] is var last && last.Last == c + k - 1 && last.Width == wn.Value) list[^1] = last with { Last = c + k };
                    else list.Add((c + k, c + k, wn.Value));
                }
                i += 2;
            }
            else if (next is PdfNumber last && file.Resolve(w[i + 2]) is PdfNumber width)
            {
                int a = (int)Math.Clamp(first.Value, 0, int.MaxValue), b = (int)Math.Clamp(last.Value, 0, int.MaxValue);
                if (b >= a) list.Add((a, b, width.Value));
                i += 3;
            }
            else i++;
        }
        list.Sort((x, y) => x.First.CompareTo(y.First));
        return list;
    }

    static readonly string[] s_chars = new string[0x3000];

    static string Cached(char c)
    {
        if (c >= s_chars.Length) return c.ToString();
        return s_chars[c] ??= c.ToString();
    }
}

/// <summary>Built-in single-byte encodings as Unicode per code ('\0' = no glyph).</summary>
internal static class Encodings
{
    /// <summary>StandardEncoding (Type 1 fonts' default).</summary>
    public static readonly char[] Standard = BuildStandard();
    /// <summary>WinAnsiEncoding (Windows-1252).</summary>
    public static readonly char[] WinAnsi = BuildWinAnsi();
    /// <summary>MacRomanEncoding.</summary>
    public static readonly char[] MacRoman = BuildMacRoman();
    /// <summary>The Symbol font's built-in encoding (Greek letters, mathematical signs).</summary>
    public static readonly char[] Symbol = BuildSymbol();

    /// <summary>The table of an /Encoding or /BaseEncoding name, or null.</summary>
    public static char[]? ByName(string? name) => name switch
    {
        "WinAnsiEncoding" => WinAnsi,
        "MacRomanEncoding" => MacRoman,
        "StandardEncoding" => Standard,
        "PDFDocEncoding" => PdfText.PdfDoc,
        "MacExpertEncoding" => Standard,
        _ => null,
    };

    static char[] Ascii()
    {
        var t = new char[256];
        for (int i = 32; i < 127; i++) t[i] = (char)i;
        return t;
    }

    static char[] BuildStandard()
    {
        var t = Ascii();
        t[39] = '’'; t[96] = '‘';
        Put(t, 161, "¡¢£⁄¥ƒ§¤'“«‹›ﬁﬂ");
        t[174] = 'f'; t[175] = 'f'; // fi, fl: see Ligatures
        Put(t, 177, "–†‡·");
        Put(t, 182, "¶•‚„”»…‰");
        t[191] = '¿';
        Put(t, 193, "`´ˆ˜¯˘˙¨");
        Put(t, 202, "˚¸");
        Put(t, 205, "˝˛ˇ—");
        t[225] = 'Æ'; t[227] = 'ª'; t[232] = 'Ł'; t[233] = 'Ø'; t[234] = 'Œ'; t[235] = 'º';
        t[241] = 'æ'; t[245] = 'ı'; t[248] = 'ł'; t[249] = 'ø'; t[250] = 'œ'; t[251] = 'ß';
        t[174] = 'ﬁ'; t[175] = 'ﬂ';
        return t;
    }

    static char[] BuildWinAnsi()
    {
        var t = Ascii();
        Put(t, 128, "€\0‚ƒ„…†‡ˆ‰Š‹Œ\0Ž\0\0‘’“”•–—˜™š›œ\0žŸ");
        for (int i = 160; i < 256; i++) t[i] = (char)i;
        return t;
    }

    static char[] BuildMacRoman()
    {
        var t = Ascii();
        Put(t, 128, "ÄÅÇÉÑÖÜáàâäãåçéèêëíìîïñóòôöõúùûü†°¢£§•¶ß®©™´¨≠ÆØ∞±≤≥¥µ∂∑∏π∫ªºΩæø¿¡¬√ƒ≈∆«»… ÀÃÕŒœ–—“”‘’÷◊ÿŸ⁄€‹›ﬁﬂ‡·‚„‰ÂÊÁËÈÍÎÏÌÓÔÒÚÛÙıˆ˜¯˘˙˚¸˝˛ˇ");
        return t;
    }

    static char[] BuildSymbol()
    {
        var t = new char[256];
        Put(t, 32, " !∀#∃%&∋()∗+,−./0123456789:;<=>?≅ΑΒΧΔΕΦΓΗΙϑΚΛΜΝΟΠΘΡΣΤΥςΩΞΨΖ[∴]⊥_αβχδεφγηιϕκλμνοπθρστυϖωξψζ{|}∼");
        Put(t, 161, "ϒ′≤⁄∞ƒ♣♦♥♠↔←↑→↓°±″≥×∝∂•÷≠≡≈…");
        Put(t, 192, "ℵℑℜ℘⊗⊕∅∩∪⊃⊇⊄⊂⊆∈∉∠∇®©™∏√⋅¬∧∨⇔⇐⇑⇒⇓◊〈");
        Put(t, 229, "∑");
        Put(t, 241, "〉∫⌠");
        return t;
    }

    static void Put(char[] t, int at, string chars)
    {
        for (int i = 0; i < chars.Length && at + i < 256; i++)
            if (chars[i] != '\0') t[at + i] = chars[i];
    }
}

/// <summary>Built-in widths of the standard 14 fonts (and their common aliases), for fonts without /Widths.</summary>
internal sealed class Standard14
{
    // Widths of ' ' (32) to '~' (126), in thousandths.
    const string Helvetica = "278 278 355 556 556 889 667 191 333 333 389 584 278 333 278 278 556 556 556 556 556 556 556 556 556 556 278 278 584 584 584 556 1015 667 667 722 722 667 611 778 722 278 500 667 556 833 722 778 667 778 722 667 611 722 667 944 667 667 611 278 278 278 469 556 333 556 556 500 556 556 278 556 556 222 222 500 222 833 556 556 556 556 333 500 278 556 500 722 500 500 500 334 260 334 584";
    const string HelveticaBold = "278 333 474 556 556 889 722 238 333 333 389 584 278 333 278 278 556 556 556 556 556 556 556 556 556 556 333 333 584 584 584 611 975 722 722 722 722 667 611 778 722 278 556 722 611 833 722 778 667 778 722 667 611 722 667 944 667 667 611 333 278 333 584 556 333 556 611 556 611 556 333 611 611 278 278 556 278 889 611 611 611 611 389 556 333 611 556 778 556 556 500 389 280 389 584";
    const string TimesRoman = "250 333 408 500 500 833 778 180 333 333 500 564 250 333 250 278 500 500 500 500 500 500 500 500 500 500 278 278 564 564 564 444 921 722 667 667 722 611 556 722 722 333 389 722 611 889 722 722 556 722 667 556 611 722 722 944 722 722 611 333 278 333 469 500 333 444 500 444 500 444 333 500 500 278 278 500 278 778 500 500 500 500 333 389 278 500 500 722 500 500 444 480 200 480 541";
    const string TimesBold = "250 333 555 500 500 1000 833 278 333 333 500 570 250 333 250 278 500 500 500 500 500 500 500 500 500 500 333 333 570 570 570 500 930 722 667 722 722 667 611 778 778 389 500 778 667 944 722 778 611 778 722 556 667 722 722 1000 722 722 667 333 278 333 581 500 333 500 556 444 556 444 333 500 556 278 333 556 278 833 556 500 556 556 444 389 333 556 500 722 500 500 444 394 220 394 520";
    const string TimesItalic = "250 333 420 500 500 833 778 214 333 333 500 675 250 333 250 278 500 500 500 500 500 500 500 500 500 500 333 333 675 675 675 500 920 611 611 667 722 611 611 722 722 333 444 667 556 833 667 722 611 722 611 500 556 722 611 833 611 556 556 389 278 389 422 500 333 500 500 444 500 444 278 500 500 278 278 444 278 722 500 500 500 500 389 389 278 500 444 667 444 444 389 400 275 400 541";
    const string TimesBoldItalic = "250 389 555 500 500 833 778 278 333 333 500 570 250 333 250 278 500 500 500 500 500 500 500 500 500 500 333 333 570 570 570 500 832 667 667 667 722 667 667 722 778 389 500 667 611 889 722 722 611 722 667 556 611 722 667 889 667 611 611 333 278 333 570 500 333 500 500 444 500 444 333 500 556 278 278 500 278 778 556 500 500 500 389 389 278 556 444 667 500 444 389 348 220 348 570";

    readonly double[]? _ascii;
    readonly double _fixed;

    Standard14(string? widths, double fixedWidth, bool symbol = false, bool dingbats = false)
    {
        _ascii = widths?.Split(' ').Select(x => double.Parse(x, CultureInfo.InvariantCulture)).ToArray();
        _fixed = fixedWidth;
        Symbol = symbol;
        Dingbats = dingbats;
    }

    /// <summary>The Symbol font (its own encoding).</summary>
    public bool Symbol { get; }
    /// <summary>ZapfDingbats (no letters).</summary>
    public bool Dingbats { get; }

    static readonly Standard14 s_helvetica = new(Helvetica, 0), s_helveticaBold = new(HelveticaBold, 0),
        s_times = new(TimesRoman, 0), s_timesBold = new(TimesBold, 0), s_timesItalic = new(TimesItalic, 0),
        s_timesBoldItalic = new(TimesBoldItalic, 0), s_courier = new(null, 600), s_symbol = new(null, 500, symbol: true),
        s_dingbats = new(null, 800, dingbats: true);

    /// <summary>The standard font a base font name means (Arial is Helvetica, Times New Roman is Times), or null.</summary>
    public static Standard14? Find(string baseName)
    {
        if (baseName.Length == 0) return null;
        string n = baseName.Replace(" ", "", StringComparison.Ordinal);
        bool bold = n.Contains("Bold", StringComparison.OrdinalIgnoreCase) || n.Contains("Black", StringComparison.OrdinalIgnoreCase) || n.Contains("Heavy", StringComparison.OrdinalIgnoreCase);
        bool italic = n.Contains("Italic", StringComparison.OrdinalIgnoreCase) || n.Contains("Oblique", StringComparison.OrdinalIgnoreCase);
        if (n.StartsWith("Courier", StringComparison.OrdinalIgnoreCase)) return s_courier;
        if (n.StartsWith("Symbol", StringComparison.OrdinalIgnoreCase)) return s_symbol;
        if (n.StartsWith("ZapfDingbats", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Dingbats", StringComparison.OrdinalIgnoreCase)) return s_dingbats;
        if (n.StartsWith("Times", StringComparison.OrdinalIgnoreCase))
            return bold && italic ? s_timesBoldItalic : bold ? s_timesBold : italic ? s_timesItalic : s_times;
        if (n.StartsWith("Helvetica", StringComparison.OrdinalIgnoreCase) || n.StartsWith("Arial", StringComparison.OrdinalIgnoreCase))
            return bold ? s_helveticaBold : s_helvetica;
        return null;
    }

    /// <summary>The width of a glyph by its text (accented letters as their base letter, others as "n"); -1 when unknown.</summary>
    public double Width(string? text)
    {
        if (_ascii == null) return _fixed;
        if (string.IsNullOrEmpty(text)) return -1;
        char c = text[0];
        // Outside the BMP (or a lone half from a broken ToUnicode): no Latin base letter, and Normalize would throw.
        if (char.IsSurrogate(c)) return _ascii['n' - 32];
        if (c >= 128)
        {
            string d = text[..1].Normalize(NormalizationForm.FormD);
            c = d.Length > 0 ? d[0] : c;
        }
        if (c is >= ' ' and <= '~') return _ascii[c - 32];
        if (c == ' ') return _ascii[0];
        return _ascii['n' - 32];
    }
}
