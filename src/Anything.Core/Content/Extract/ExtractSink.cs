using System.Globalization;
using System.Text;

namespace Anything.Core.Content.Extract;

/// <summary>
/// Collects one file's body, fields and picture details within the text and page limits. The body keeps the stored
/// format whatever the reader passes: line ends of every kind become '\n', only <see cref="PageBreak"/> and
/// <see cref="StartSheet"/> write '\f', other control characters become spaces, lone surrogates become U+FFFD, and the
/// text limit never cuts a surrogate pair or a letter from its marks.
/// </summary>
internal sealed class ExtractSink
{
    /// <summary>Most characters kept per field value.</summary>
    public const int MaxFieldChars = 4096;
    /// <summary>Most characters kept for all keywords together.</summary>
    public const int MaxKeywordChars = 65536;

    readonly ExtractLimits _limits;
    readonly StringBuilder _body = new();
    readonly List<(DocField Field, string Value)> _fields = [];
    // Keywords, kept apart and joined once in ToDoc (joining as they come would copy the whole list each time).
    List<string>? _keywords;
    HashSet<string>? _keywordSet;
    int _keywordChars, _keywordsAt = -1;
    PictureBuilder? _picture;
    int _breaks;
    bool _afterCr;
    char _pendingHigh;

    public ExtractSink(ExtractLimits limits) => _limits = limits;

    /// <summary>The text or page limit is reached: the reader stops (status Truncated).</summary>
    public bool Full { get; private set; }

    /// <summary>Picture details, created on first use.</summary>
    public PictureBuilder Picture => _picture ??= new PictureBuilder();

    /// <summary>Some picture detail was set.</summary>
    public bool HasPicture => _picture?.Any == true;

    /// <summary>Pages, slides or sheets counted by the reader.</summary>
    public int Pages { get; set; }

    /// <summary>Body characters so far.</summary>
    public int TextLength => _body.Length;

    /// <summary>Fields set so far.</summary>
    public int FieldCount => _fields.Count;

    /// <summary>
    /// The body has a word (a letter, digit or kept mark; <see cref="ContentTokenizer.HasWords"/>), not just line and page
    /// ends: a scan's empty pages are no text.
    /// </summary>
    public bool HasWords
    {
        get
        {
            char carry = '\0';
            foreach (var chunk in _body.GetChunks())
            {
                var span = chunk.Span;
                if (span.IsEmpty) continue;
                // A surrogate pair split across two chunks.
                if (carry != '\0' && ContentTokenizer.HasWords([carry, span[0]])) return true;
                if (ContentTokenizer.HasWords(span)) return true;
                carry = char.IsHighSurrogate(span[^1]) ? span[^1] : '\0';
            }
            return false;
        }
    }

    /// <summary>Anything was read: words in the body, a field or a picture detail.</summary>
    public bool HasContent => _fields.Count > 0 || HasPicture || HasWords;

    /// <summary>Appends body text (cut at the limit).</summary>
    public void Text(ReadOnlySpan<char> text)
    {
        for (int i = 0; i < text.Length && !Full; i++)
        {
            char c = text[i];
            if (_pendingHigh != 0)
            {
                char high = _pendingHigh;
                _pendingHigh = '\0';
                if (char.IsLowSurrogate(c)) { AppendPair(high, c); continue; }
                Append('�');
                if (Full) return;
            }
            if (c == '\n')
            {
                if (_afterCr) { _afterCr = false; continue; }
                Append('\n');
            }
            else if (c is '\r' or '\v' or '\f' or '\u0085' or '\u2028' or '\u2029')
            {
                Append('\n');
                _afterCr = c == '\r';
                continue;
            }
            else if (char.IsHighSurrogate(c)) _pendingHigh = c;
            else if (char.IsLowSurrogate(c)) Append('�');
            else if (c != '\t' && (c < ' ' || c is >= '\u007F' and <= '\u009F')) Append(' ');
            else Append(c);
            _afterCr = false;
        }
    }

    /// <summary>Ends a line ('\n').</summary>
    public void LineBreak()
    {
        FlushPending();
        _afterCr = false;
        if (!Full) Append('\n');
    }

    /// <summary>
    /// Ends a page or slide ('\f'): call it after every page but the last, also after a page with no text (a PDF whose
    /// first page is a scan starts with '\f'), never before the first page. Sheets use <see cref="StartSheet"/> instead.
    /// Past the page limit nothing more is kept and the sink is full.
    /// </summary>
    public void PageBreak()
    {
        FlushPending();
        _afterCr = false;
        if (Full) return;
        if (_breaks + 1 >= _limits.MaxPages) { Full = true; return; }
        if (Append('\f')) _breaks++;
    }

    /// <summary>
    /// Starts a sheet of a workbook (xlsx, ods): a page break unless the body is still empty (the first sheet opens it),
    /// then the sheet's name on its own line (an empty name gives an empty line), so every sheet's page starts with its
    /// name. Call it before each sheet's cells and write nothing else into a workbook's body.
    /// </summary>
    public void StartSheet(string name)
    {
        FlushPending();
        _afterCr = false;
        if (Full) return;
        if (_body.Length > 0)
        {
            PageBreak();
            if (Full) return;
        }
        Text(Clean(name, MaxFieldChars));
        FlushPending();
        if (!Full) Append('\n');
    }

    /// <summary>The value a field has so far (cleaned and capped; Keywords joined as ToDoc joins them); null when none. No side effects.</summary>
    public string? Value(DocField field)
    {
        if (field == DocField.Keywords) return _keywords is { Count: > 0 } k ? SafeCut(string.Join("; ", k), MaxKeywordChars) : null;
        foreach (var (f, v) in _fields) if (f == field) return v;
        return null;
    }

    /// <summary>Sets a field once (first value wins; Keywords values are joined with "; ", repeats left out). Values are capped.</summary>
    public void Field(DocField field, string value)
    {
        if (field == DocField.Keywords) { AddKeyword(value); return; }
        foreach (var (f, _) in _fields) if (f == field) return;
        string v = Clean(value, MaxFieldChars);
        if (v.Length > 0) _fields.Add((field, v));
    }

    /// <summary>Most characters of one keyword list looked at, so a long run of repeats (each left out) costs no more.</summary>
    public const int MaxKeywordListChars = 4 * MaxKeywordChars;

    /// <summary>Keywords that are full: later ones are left out.</summary>
    public bool KeywordsFull => _keywordChars >= MaxKeywordChars;

    /// <summary>
    /// Adds a list of keywords split at ',' and ';' (each trimmed, empty ones left out), walking it without building every
    /// part: it stops once the keywords are full, and looks at the first <see cref="MaxKeywordListChars"/> characters only.
    /// </summary>
    public void Keywords(ReadOnlySpan<char> list)
    {
        if (list.Length > MaxKeywordListChars) list = list[..MaxKeywordListChars];
        while (!list.IsEmpty && !KeywordsFull)
        {
            int at = list.IndexOfAny(',', ';');
            var part = (at < 0 ? list : list[..at]).Trim();
            if (!part.IsEmpty) AddKeyword(part.ToString());
            list = at < 0 ? default : list[(at + 1)..];
        }
    }

    void AddKeyword(string value)
    {
        // Once the keywords fill their cap, later ones are neither cleaned nor remembered.
        if (_keywordChars >= MaxKeywordChars) return;
        string v = Clean(value, MaxFieldChars);
        if (v.Length == 0) return;
        _keywordSet ??= new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!_keywordSet.Add(v)) return;
        if (_keywords == null)
        {
            _keywords = [];
            _keywordsAt = _fields.Count;
            _fields.Add((DocField.Keywords, "")); // its place in reading order; joined in ToDoc
        }
        _keywordChars += (_keywords.Count > 0 ? 2 : 0) + v.Length;
        _keywords.Add(v);
    }

    /// <summary>
    /// Builds the document (NFC body). Ok and Truncated keep everything; NoText keeps fields and picture details but no
    /// body; any other status (left out or a problem) keeps nothing but the reader facts.
    /// </summary>
    public ExtractedDoc ToDoc(ContentKind kind, DocStatus status, byte version, long bytesRead, int elapsedMs)
    {
        FlushPending();
        if (status is not (DocStatus.Ok or DocStatus.Truncated or DocStatus.NoText))
            return new ExtractedDoc { Kind = kind, Status = status, ExtractorVersion = version, BytesRead = bytesRead, ElapsedMs = elapsedMs };
        var fields = _fields.ToArray();
        if (_keywords != null) fields[_keywordsAt] = (DocField.Keywords, SafeCut(string.Join("; ", _keywords), MaxKeywordChars));
        return new ExtractedDoc
        {
            Kind = kind,
            Status = status,
            Body = status == DocStatus.NoText ? "" : SafeCut(Nfc(_body.ToString()), _limits.MaxTextChars),
            Fields = fields,
            Picture = HasPicture ? _picture!.ToInfo() : null,
            Pages = Pages,
            ExtractorVersion = version,
            BytesRead = bytesRead,
            ElapsedMs = elapsedMs,
        };
    }

    void FlushPending()
    {
        if (_pendingHigh == 0) return;
        _pendingHigh = '\0';
        if (!Full) Append('�');
    }

    bool Append(char c)
    {
        if (_body.Length + 1 > _limits.MaxTextChars) { Cut(IsMark(c)); return false; }
        _body.Append(c);
        return true;
    }

    void AppendPair(char high, char low)
    {
        if (_body.Length + 2 > _limits.MaxTextChars) { Cut(IsMark(char.ConvertToUtf32(high, low))); return; }
        _body.Append(high).Append(low);
    }

    /// <summary>The limit is reached: when the text that didn't fit is a mark, its letter goes too, so they're never split.</summary>
    void Cut(bool nextIsMark)
    {
        Full = true;
        if (!nextIsMark) return;
        int end = _body.Length;
        while (end > 0 && IsMarkBefore(_body, end, out int width)) end -= width;
        if (end > 0 && IsBase(_body[end - 1])) end -= char.IsLowSurrogate(_body[end - 1]) && end > 1 && char.IsHighSurrogate(_body[end - 2]) ? 2 : 1;
        _body.Length = end;
    }

    static bool IsMarkBefore(StringBuilder sb, int end, out int width)
    {
        width = 1;
        char c = sb[end - 1];
        if (char.IsLowSurrogate(c) && end > 1 && char.IsHighSurrogate(sb[end - 2]))
        {
            width = 2;
            return IsMark(char.ConvertToUtf32(sb[end - 2], c));
        }
        return IsMark(c);
    }

    /// <summary>A character marks can belong to (not a space, line or page end).</summary>
    static bool IsBase(char c) => !char.IsWhiteSpace(c) && !char.IsControl(c);

    static bool IsMark(int codePoint) => (uint)codePoint <= 0x10FFFF && !(codePoint is >= 0xD800 and <= 0xDFFF)
        && CharUnicodeInfo.GetUnicodeCategory(codePoint) is UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark
            or UnicodeCategory.EnclosingMark;

    /// <summary>A field value on one line: controls become spaces, lone surrogates U+FFFD, NFC, trimmed and capped.</summary>
    static string Clean(string value, int max)
    {
        if (string.IsNullOrEmpty(value)) return "";
        var sb = new StringBuilder(Math.Min(value.Length, max + 2));
        for (int i = 0; i < value.Length && sb.Length <= max + 1; i++)
        {
            char c = value[i];
            if (char.IsHighSurrogate(c) && i + 1 < value.Length && char.IsLowSurrogate(value[i + 1])) { sb.Append(c).Append(value[++i]); continue; }
            if (char.IsSurrogate(c)) sb.Append('�');
            else if (c < ' ' || c is >= '\u007F' and <= '\u009F' or '\u2028' or '\u2029') sb.Append(' ');
            else sb.Append(c);
        }
        return SafeCut(Nfc(sb.ToString()).Trim(), max);
    }

    /// <summary>NFC, guarded like Fold.Apply (text the sink built never has lone surrogates, but keep the call safe).</summary>
    static string Nfc(string s)
    {
        try { return s.IsNormalized(NormalizationForm.FormC) ? s : s.Normalize(NormalizationForm.FormC); }
        catch (ArgumentException) { return s; }
    }

    /// <summary>The string cut to at most max characters, never inside a surrogate pair or between a letter and its marks.</summary>
    internal static string SafeCut(string s, int max)
    {
        if (s.Length <= max) return s;
        int end = max;
        if (end > 0 && char.IsLowSurrogate(s[end]) && char.IsHighSurrogate(s[end - 1])) end--;
        if (end < s.Length && IsMarkAt(s, end))
        {
            while (end > 0 && IsMarkBefore(s, end, out int width)) end -= width;
            if (end > 0 && IsBase(s[end - 1])) end -= char.IsLowSurrogate(s[end - 1]) && end > 1 && char.IsHighSurrogate(s[end - 2]) ? 2 : 1;
        }
        return s[..end];
    }

    static bool IsMarkAt(string s, int i)
        => char.IsHighSurrogate(s[i]) && i + 1 < s.Length && char.IsLowSurrogate(s[i + 1]) ? IsMark(char.ConvertToUtf32(s[i], s[i + 1])) : IsMark(s[i]);

    static bool IsMarkBefore(string s, int end, out int width)
    {
        width = 1;
        char c = s[end - 1];
        if (char.IsLowSurrogate(c) && end > 1 && char.IsHighSurrogate(s[end - 2]))
        {
            width = 2;
            return IsMark(char.ConvertToUtf32(s[end - 2], c));
        }
        return IsMark(c);
    }
}

/// <summary>
/// Mutable picture details while reading; the first value set per field wins (later sets are ignored, 0 and NaN never
/// count as set), so a reader can offer every source in order of trust: header, then EXIF, then XMP.
/// </summary>
internal sealed class PictureBuilder
{
    int _width, _height, _dpiX, _dpiY, _iso;
    byte _bitDepth, _orientation, _exifColorSpace, _rating;
    ColorMode _colorMode;
    long _dateTaken;
    float _fNumber, _exposure, _focal;

    public int Width { get => _width; set { if (_width == 0 && value > 0) _width = value; } }
    public int Height { get => _height; set { if (_height == 0 && value > 0) _height = value; } }
    public int DpiX { get => _dpiX; set { if (_dpiX == 0 && value > 0) _dpiX = value; } }
    public int DpiY { get => _dpiY; set { if (_dpiY == 0 && value > 0) _dpiY = value; } }
    public int Iso { get => _iso; set { if (_iso == 0 && value > 0) _iso = value; } }
    public byte BitDepth { get => _bitDepth; set { if (_bitDepth == 0) _bitDepth = value; } }
    /// <summary>EXIF orientation 1-8 (other values ignored).</summary>
    public byte Orientation { get => _orientation; set { if (_orientation == 0 && value is >= 1 and <= 8) _orientation = value; } }
    /// <summary>EXIF ColorSpace: 1 sRGB, 2 uncalibrated.</summary>
    public byte ExifColorSpace { get => _exifColorSpace; set { if (_exifColorSpace == 0 && value is 1 or 2) _exifColorSpace = value; } }
    /// <summary>Stars 1-5 (0 and other values ignored).</summary>
    public byte Rating { get => _rating; set { if (_rating == 0 && value is >= 1 and <= 5) _rating = value; } }
    public ColorMode ColorMode { get => _colorMode; set { if (_colorMode == ColorMode.Unknown) _colorMode = value; } }
    /// <summary>FILETIME UTC.</summary>
    public long DateTakenUtc { get => _dateTaken; set { if (_dateTaken == 0 && value > 0) _dateTaken = value; } }
    public float FNumber { get => _fNumber; set { if (_fNumber == 0 && float.IsFinite(value) && value > 0) _fNumber = value; } }
    public float ExposureSeconds { get => _exposure; set { if (_exposure == 0 && float.IsFinite(value) && value > 0) _exposure = value; } }
    public float FocalLengthMm { get => _focal; set { if (_focal == 0 && float.IsFinite(value) && value > 0) _focal = value; } }

    /// <summary>A location was set.</summary>
    public bool HasGps { get; private set; }
    /// <summary>Latitude in micro-degrees, once set.</summary>
    public int LatitudeMicro { get; private set; }
    /// <summary>Longitude in micro-degrees, once set.</summary>
    public int LongitudeMicro { get; private set; }

    /// <summary>Sets the location once, in degrees; ignores NaN, out-of-range values and (0, 0), which cameras write for "no fix".</summary>
    public void SetGps(double latitude, double longitude)
    {
        if (HasGps || !double.IsFinite(latitude) || !double.IsFinite(longitude)) return;
        if (Math.Abs(latitude) > 90 || Math.Abs(longitude) > 180 || (latitude == 0 && longitude == 0)) return;
        LatitudeMicro = (int)Math.Round(latitude * 1e6);
        LongitudeMicro = (int)Math.Round(longitude * 1e6);
        HasGps = true;
    }

    /// <summary>Some detail was set.</summary>
    public bool Any => _width != 0 || _height != 0 || _dpiX != 0 || _dpiY != 0 || _iso != 0 || _bitDepth != 0 || _orientation != 0
        || _exifColorSpace != 0 || _rating != 0 || _colorMode != ColorMode.Unknown || _dateTaken != 0 || _fNumber != 0 || _exposure != 0
        || _focal != 0 || HasGps;

    /// <summary>The finished, immutable details.</summary>
    public PictureInfo ToInfo() => new(_width, _height, _dpiX, _dpiY, _bitDepth, _colorMode, _orientation, _exifColorSpace, _rating,
        _dateTaken, HasGps, LatitudeMicro, LongitudeMicro, _iso, _fNumber, _exposure, _focal);
}
