using System.Runtime.ExceptionServices;

namespace Anything.Core.Content.Extract;

/// <summary>
/// PDF text objects (and PDF-compatible AI files; other AI files through their XMP packet); version 1. Reads the
/// cross-reference, /Info and the catalog's XMP, then each leaf page in order: a page break goes between every two
/// pages whether or not a page's content is read, and content streams are read only for pages whose resources (or the
/// forms they use) have fonts, so scans and outlined art cost a few dictionaries; visible annotations' appearances follow
/// each page's content (filled-in form fields). Image streams are never read. A broken page, object or stream is skipped
/// and the document keeps what was read (Truncated); so does one that was only partly read (a stream cut by its cap,
/// broken midway or ended early, an object that is listed but couldn't be read).
/// </summary>
internal sealed class PdfTextReader : IContentReader
{
    /// <summary>Most bytes of the catalog's XMP packet read.</summary>
    const int MaxXmpBytes = 64 << 10;
    /// <summary>How far into an AI file the "%PDF" header may start.</summary>
    const int HeaderWindow = 1024;
    /// <summary>How much of a non-PDF AI file is searched for its XMP packet.</summary>
    const int AiXmpWindow = 256 << 10;

    /// <inheritdoc/>
    public ContentKind Kind => ContentKind.Pdf;
    /// <inheritdoc/>
    public byte Version => 1;
    /// <inheritdoc/>
    public bool Handles(ReadOnlySpan<char> extension) => extension is "pdf" or "ai";

    /// <inheritdoc/>
    public DocStatus Read(IByteSource source, ReadOnlySpan<char> extension, ExtractSink sink, ExtractBudget budget)
    {
        if (extension.Equals("ai", StringComparison.OrdinalIgnoreCase) && !HasPdfHeader(source))
            return ReadXmpOnly(source, sink);
        using var file = PdfFile.Open(source, budget);
        if (file.Locked) return DocStatus.PasswordProtected;

        Exception? firstFailure = null;
        int failures = 0;
        void Unit(Action read)
        {
            try { read(); }
            catch (Exception e) when (IsUnitFailure(e)) { failures++; firstFailure ??= e; }
        }
        Unit(() => ReadInfo(file, sink));
        Unit(() => ReadXmp(file, sink));

        // The declared page count is read first: once a document-level limit ends the read, reading more objects would
        // only hit that limit again. A page tree that can't be read here fails the page walk below, which counts it.
        int declared = 0;
        try { declared = file.DeclaredPageCount; }
        catch (Exception e) when (IsUnitFailure(e)) { }
        var target = new SinkTarget(sink);
        var walker = new PdfContentWalker(file, new PdfTextAssembler(target), () => sink.Full);
        int pages = 0;
        bool stoppedEarly = false;
        try
        {
            foreach (var page in file.Pages())
            {
                if (pages > 0) sink.PageBreak();
                if (sink.Full) { stoppedEarly = true; break; }
                pages++;
                Unit(() => walker.WalkPage(page));
                if (sink.Full) { stoppedEarly = true; break; }
            }
        }
        catch (ContentLimitException)
        {
            // Bytes read, decoded bytes or time ran out (the document is Truncated with what was read): it keeps its
            // page count, so pagecount: still finds it.
            sink.Pages = Math.Max(pages, declared);
            throw;
        }
        failures += walker.Failures;
        firstFailure ??= walker.FirstFailure;
        // Units read only in part: content cut, broken or ended early, objects that couldn't be read, pages left out.
        int partial = walker.Partial + file.Broken;
        sink.Pages = stoppedEarly ? Math.Max(pages, declared) : pages;
        if (failures > 0 || partial > 0)
        {
            if (sink.HasContent) return DocStatus.Truncated;
            if (firstFailure != null) ExceptionDispatchInfo.Throw(firstFailure);
        }
        return DocStatus.Ok;
    }

    /// <summary>A failure that skips one page, object or stream: bad data or one stream past its cap (not the document's limits).</summary>
    internal static bool IsUnitFailure(Exception e)
        => e is ContentLimitException { Limit: ContentLimitException.Stream } || PdfFile.IsFormatError(e);

    static bool HasPdfHeader(IByteSource source)
    {
        Span<byte> head = stackalloc byte[HeaderWindow];
        int n = 0;
        while (n < head.Length)
        {
            int got = source.ReadAt(n, head[n..]);
            if (got <= 0) break;
            n += got;
        }
        return head[..n].IndexOf("%PDF"u8) >= 0;
    }

    static DocStatus ReadXmpOnly(IByteSource source, ExtractSink sink)
    {
        int size = (int)Math.Min(AiXmpWindow, source.Length);
        var window = PdfPool.Rent(size);
        try
        {
            int n = 0;
            while (n < size)
            {
                int got = source.ReadAt(n, window.AsSpan(n, size - n));
                if (got <= 0) break;
                n += got;
            }
            var (start, length) = Xmp.FindPacket(window.AsSpan(0, n));
            if (start >= 0) Xmp.Read(window.AsMemory(start, length), sink);
        }
        finally { PdfPool.Return(window); }
        return DocStatus.Ok;
    }

    /// <summary>Most bytes of an /Info string decoded: a field keeps 4K characters and the keywords 64K, so a 4 MB string costs no more.</summary>
    const int MaxInfoBytes = 4 * ExtractSink.MaxKeywordChars + 3;

    static void ReadInfo(PdfFile file, ExtractSink sink)
    {
        if (file.Resolve(file.Trailer["Info"]) is not PdfDict info) return;
        string? Text(string key) => file.Resolve(info[key]) is PdfString s
            ? PdfText.Decode(s.Bytes.AsSpan(0, Math.Min(s.Bytes.Length, MaxInfoBytes))).Trim() : null;
        if (Text("Title") is { Length: > 0 } title) sink.Field(DocField.Title, title);
        if (Text("Author") is { Length: > 0 } author) sink.Field(DocField.Author, author);
        if (Text("Subject") is { Length: > 0 } subject) sink.Field(DocField.Subject, subject);
        if (Text("Keywords") is { Length: > 0 } keywords) sink.Keywords(keywords);
        if (Text("Creator") is { Length: > 0 } creator) sink.Field(DocField.CreatorTool, creator);
    }

    static void ReadXmp(PdfFile file, ExtractSink sink)
    {
        if (file.Resolve(file.Catalog?["Metadata"]) is not PdfStream metadata) return;
        if (file.ReadStream(metadata, MaxXmpBytes) is not { } data) return;
        try
        {
            if (data.Length == 0) return;
            var bytes = data.Array.AsMemory(0, data.Length);
            var (start, length) = Xmp.FindPacket(bytes.Span);
            Xmp.Read(start >= 0 ? bytes.Slice(start, length) : bytes, sink);
        }
        finally { data.Release(); }
    }

    /// <summary>Writes the assembled text into the sink.</summary>
    sealed class SinkTarget(ExtractSink sink) : IPdfTextTarget
    {
        /// <inheritdoc/>
        public void Text(string text, in PdfGlyph glyph) => sink.Text(text);
        /// <inheritdoc/>
        public void Space() => sink.Text(" ");
        /// <inheritdoc/>
        public void NewLine() => sink.LineBreak();
    }
}

/// <summary>One shown glyph: its text, whether it is unreadable, and where it stands in user space.</summary>
internal struct PdfGlyph
{
    /// <summary>Unicode text; null when the font doesn't say.</summary>
    public string? Text;
    /// <summary>Unmapped, private-use, control or replacement characters only (the garbage guard counts them).</summary>
    public bool Bad;
    /// <summary>A TJ kern of more than 0.2 em came right before it (a word space).</summary>
    public bool KernSpace;
    /// <summary>Start and end of the glyph on its baseline, in user space.</summary>
    public double X0, Y0, X1, Y1;
    /// <summary>The "up" vector of the text in user space, one font size long.</summary>
    public double Ux, Uy;
    /// <summary>The writing direction in user space (unit length).</summary>
    public double Dx, Dy;
    /// <summary>Font size in user space.</summary>
    public double Size;
    /// <summary>Ascent and descent as fractions of the font size (vertical fonts: half a size either side).</summary>
    public double Ascent, Descent;
}

/// <summary>Where assembled page text goes: the sink (indexing) or a page buffer with glyph positions (match boxes).</summary>
internal interface IPdfTextTarget
{
    /// <summary>Writes a glyph's text, with where it stands.</summary>
    void Text(string text, in PdfGlyph glyph);
    /// <summary>A word space the assembler added (no glyph behind it).</summary>
    void Space();
    /// <summary>A line end the assembler added (no glyph behind it).</summary>
    void NewLine();
}

/// <summary>
/// Turns glyph runs into text the way the index stores it: a gap of more than 0.2 × the font size along the line (or a
/// TJ kern below −200) is a space, a move of more than half the size across the line (or a change of direction) is a new
/// line. The garbage guard works per font and per run: a run with more than 30% unreadable glyphs, or any run of a font
/// whose glyphs so far are more than 30% unreadable (after 20), is left out, with a space in its place.
/// </summary>
internal sealed class PdfTextAssembler(IPdfTextTarget target)
{
    // Spaces and line ends are held until the next character is written, so text left out never leaves one behind.
    bool _hasPrevious, _atLineStart = true, _lastSpace = true, _pendingSpace, _pendingLine;
    double _ex, _ey, _dx = 1, _dy;

    /// <summary>A new page: nothing carries over.</summary>
    public void StartPage()
    {
        _hasPrevious = false;
        _atLineStart = true;
        _lastSpace = true;
        _pendingSpace = false;
        _pendingLine = false;
    }

    /// <summary>What follows starts a new line and is never glued to what came before (an annotation after the page).</summary>
    public void Break()
    {
        NewLine();
        _hasPrevious = false;
    }

    /// <summary>Text given as it is (a form field's value): its spaces and line ends are kept, nothing is dropped.</summary>
    public void Plain(in PdfGlyph glyph)
    {
        Emit(glyph);
        _hasPrevious = false;
    }

    /// <summary>One show-text operation's glyphs.</summary>
    public void Run(PdfFont font, List<PdfGlyph> glyphs)
    {
        int visible = 0, bad = 0;
        foreach (var g in glyphs)
        {
            if (g.Text != null && IsBlank(g.Text)) continue;
            visible++;
            if (g.Bad) bad++;
        }
        font.Seen += visible;
        font.Bad += bad;
        bool drop = font.IsGarbage || bad * 10 > visible * 3;
        for (int i = 0; i < glyphs.Count; i++)
        {
            var g = glyphs[i];
            Separate(g);
            if (drop || g.Bad || g.Text == null)
            {
                if (g.Text == null || !IsBlank(g.Text)) _pendingSpace = true;
            }
            else Emit(g);
            _ex = g.X1; _ey = g.Y1;
            if (g.Dx != 0 || g.Dy != 0) { _dx = g.Dx; _dy = g.Dy; }
            _hasPrevious = true;
        }
    }

    void Separate(in PdfGlyph g)
    {
        if (!_hasPrevious) return;
        double size = g.Size > 0 ? g.Size : 1;
        double vx = g.X0 - _ex, vy = g.Y0 - _ey;
        double along = vx * g.Dx + vy * g.Dy;
        double across = Math.Abs(vx * -g.Dy + vy * g.Dx);
        double turn = g.Dx * _dx + g.Dy * _dy;
        if (turn < 0.9 || across > 0.5 * size) { NewLine(); return; }
        if (along > 0.2 * size || along < -0.6 * size || g.KernSpace) _pendingSpace = true;
    }

    void NewLine()
    {
        _pendingSpace = false;
        if (!_atLineStart) _pendingLine = true;
    }

    /// <summary>Writes a held line end or space before a character.</summary>
    void Flush()
    {
        if (_pendingLine)
        {
            target.NewLine();
            _atLineStart = true;
            _lastSpace = true;
        }
        else if (_pendingSpace && !_lastSpace && !_atLineStart) target.Space();
        _pendingLine = _pendingSpace = false;
    }

    void Emit(in PdfGlyph g)
    {
        var text = g.Text!;
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            if (c is '\n' or '\r') { NewLine(); continue; }
            if (IsSpace(c)) { if (!_pendingLine) _pendingSpace = true; continue; }
            if (c < ' ' || c is >= '\u007F' and <= '\u009F') continue;
            Flush();
            if (c is >= 'ﬀ' and <= 'ﬆ') target.Text(Ligature(c), g);
            else if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1])) { target.Text(text.Substring(i, 2), g); i++; }
            else target.Text(text.Length == 1 ? text : c.ToString(), g);
            _atLineStart = false;
            _lastSpace = false;
        }
    }

    static string Ligature(char c) => c switch
    {
        'ﬀ' => "ff",
        'ﬁ' => "fi",
        'ﬂ' => "fl",
        'ﬃ' => "ffi",
        'ﬄ' => "ffl",
        _ => "st",
    };

    static bool IsSpace(char c) => c is ' ' or '\t' or ' ' or '　' or (>= ' ' and <= ' ') or ' ' or ' ';

    static bool IsBlank(string s)
    {
        foreach (char c in s)
            if (!IsSpace(c) && c is not ('\n' or '\r')) return false;
        return true;
    }

    /// <summary>Text that can't be read: private use, controls and replacement characters only.</summary>
    public static bool IsUnreadable(string s)
    {
        foreach (char c in s)
        {
            if (c is >= '' and <= '' or '�' or '￾' or '￿') continue;
            if (char.IsHighSurrogate(c) || char.IsLowSurrogate(c))
            {
                // Supplementary private use (planes 15 and 16): U+DB80-DBFF high surrogates.
                if (c is >= '\uDB80' and <= '\uDBFF' || char.IsLowSurrogate(c)) continue;
                return false;
            }
            if (c < ' ' && c is not ('\t' or '\n' or '\r')) continue;
            if (c is >= '\u007F' and <= '\u009F') continue;
            return false;
        }
        return true;
    }
}

/// <summary>
/// Walks a page's content streams and the forms they draw (at most 8 deep, never one inside itself), tracking the text
/// state (Tm, Td, TD, T*, TL, Tc, Tw, Tz, Ts, Tf) and the CTM (cm, q/Q), and hands every Tj, TJ, ' and " to the
/// assembler as positioned glyphs. Inline images are skipped; image XObjects are never read (only their dictionaries
/// are looked at, through the resources). No recursion on file data. Each page starts the fonts' garbage counts again,
/// so a page reads the same whether the whole document is walked (indexing) or only that page (match boxes).
/// </summary>
internal sealed class PdfContentWalker
{
    const int MaxFormDepth = 8;
    const int MaxStateDepth = 64;
    const int MaxOperands = 64;
    const int MaxFonts = 4096;

    readonly PdfFile _file;
    readonly PdfTextAssembler _assembler;
    readonly Func<bool> _stop;
    readonly ExtractBudget _budget;
    readonly Dictionary<object, PdfFont> _fonts = new(ReferenceEqualityComparer.Instance);
    readonly Dictionary<int, PdfFont> _fontsByRef = [];
    readonly List<PdfGlyph> _run = [];
    PdfFont? _fallbackFont;

    /// <summary>Forms or content streams skipped because they were broken.</summary>
    public int Failures { get; private set; }
    /// <summary>The first such failure.</summary>
    public Exception? FirstFailure { get; private set; }
    /// <summary>Content, form or appearance streams read only in part (cut at the cap, broken or ended early) or not at all (a filter that isn't read).</summary>
    public int Partial { get; private set; }

    /// <summary>A walker for one document's pages, writing to the assembler until stop says the sink is full.</summary>
    public PdfContentWalker(PdfFile file, PdfTextAssembler assembler, Func<bool> stop)
    {
        _file = file;
        _assembler = assembler;
        _stop = stop;
        _budget = file.Budget;
    }

    /// <summary>A 2-D affine matrix [a b c d e f] in PDF's row-vector convention.</summary>
    internal readonly record struct Matrix(double A, double B, double C, double D, double E, double F)
    {
        /// <summary>No transformation.</summary>
        public static Matrix Identity { get; } = new(1, 0, 0, 1, 0, 0);
        /// <summary>This, then m.</summary>
        public Matrix Then(in Matrix m) => new(A * m.A + B * m.C, A * m.B + B * m.D, C * m.A + D * m.C, C * m.B + D * m.D,
            E * m.A + F * m.C + m.E, E * m.B + F * m.D + m.F);
        /// <summary>A point through the matrix.</summary>
        public (double X, double Y) Apply(double x, double y) => (x * A + y * C + E, x * B + y * D + F);
        /// <summary>A direction through the matrix (no translation).</summary>
        public (double X, double Y) Vector(double x, double y) => (x * A + y * C, x * B + y * D);
        /// <summary>No infinity or NaN (bad operands never make it in).</summary>
        public bool IsFinite => double.IsFinite(A) && double.IsFinite(B) && double.IsFinite(C) && double.IsFinite(D) && double.IsFinite(E) && double.IsFinite(F);
    }

    struct State
    {
        public Matrix Ctm;
        public PdfFont? Font;
        public double Size, CharSpace, WordSpace, Scale, Leading, Rise;
    }

    /// <summary>Content being interpreted: its bytes (given back when the frame ends), resources and the state to restore.</summary>
    sealed class Frame(PdfBytes data, PdfDict? resources, int stackBase, int formNumber, State saved)
    {
        public readonly PdfBytes Data = data;
        public readonly PdfLexer Lexer = new(data.Array, data.Length);
        public readonly PdfDict? Resources = resources;
        public readonly int StackBase = stackBase, FormNumber = formNumber;
        public readonly State Saved = saved;
    }

    struct Operand
    {
        public double Number;
        public object? Value; // string (name), byte[] (string), PdfArray, PdfDict
        public bool IsNumber;
    }

    State _gs;
    Matrix _tm, _tlm;
    readonly List<State> _stack = [];
    readonly List<Frame> _frames = [];
    readonly Operand[] _ops = new Operand[MaxOperands];
    int _opCount, _overflowQ;

    /// <summary>The page's resources (or a form's) have fonts, so its content may show text; looks at dictionaries only.</summary>
    public bool HasFonts(PdfDict? resources) => HasFonts(resources, 0, []);

    bool HasFonts(PdfDict? resources, int depth, HashSet<object> seen)
    {
        if (resources == null || depth > MaxFormDepth || !seen.Add(resources)) return false;
        if (_file.ResolveDict(resources["Font"]) is { Items.Count: > 0 }) return true;
        if (_file.ResolveDict(resources["XObject"]) is not { } xobjects) return false;
        foreach (var value in xobjects.Items.Values)
        {
            _budget.Check();
            if (_file.Resolve(value) is not PdfStream x || x.Dict.Name("Subtype") != "Form") continue;
            var formResources = _file.ResolveDict(x.Dict["Resources"]);
            if (formResources != null && HasFonts(formResources, depth + 1, seen)) return true;
        }
        return false;
    }

    /// <summary>
    /// Walks one page: its content when its resources have fonts, then the normal appearance of each visible annotation
    /// whose resources have fonts (filled-in form fields, free text, stamps), as viewers show them.
    /// </summary>
    public void WalkPage(PdfPage page)
    {
        _assembler.StartPage();
        ResetGarbageCounts();
        if (HasFonts(page.Resources))
        {
            // Several content streams are one content stream split between tokens: each is decoded only when the
            // walk reaches it, and operands and state carry over from one to the next.
            var contents = _file.Resolve(page.Dict["Contents"]);
            var parts = contents switch
            {
                PdfStream single => [single],
                PdfArray array => array.Items.Take(4096).Select(p => _file.Resolve(p) as PdfStream).OfType<PdfStream>().ToList(),
                _ => new List<PdfStream>(),
            };
            int next = 0;
            PdfBytes? NextPart()
            {
                while (next < parts.Count)
                    if (ReadContent(parts[next++]) is { } data) return data;
                return null;
            }
            if (NextPart() is { } first) Walk(first, page.Resources, Matrix.Identity, NextPart);
        }
        if (_stop()) return;
        WalkAnnotations(page);
    }

    /// <summary>The per-font garbage guard counts one page at a time.</summary>
    void ResetGarbageCounts()
    {
        foreach (var f in _fonts.Values) f.Seen = f.Bad = 0;
        foreach (var f in _fontsByRef.Values) f.Seen = f.Bad = 0;
        if (_fallbackFont != null) _fallbackFont.Seen = _fallbackFont.Bad = 0;
    }

    const int MaxAnnotations = 2000;

    void WalkAnnotations(PdfPage page)
    {
        if (_file.Resolve(page.Dict["Annots"]) is not PdfArray annots) return;
        PdfDict? formResources = null;
        bool formResourcesRead = false;
        bool? needAppearances = null;
        for (int i = 0; i < annots.Count && i < MaxAnnotations; i++)
        {
            _budget.Check();
            if (_stop()) return;
            try
            {
                if (_file.ResolveDict(annots[i]) is not { } annot) continue;
                if (annot.Name("Subtype") is "Popup" or "Link") continue;
                int flags = (int)_file.Number(annot["F"], 0);
                if ((flags & (2 | 32)) != 0) continue; // Hidden, NoView
                var normal = _file.Resolve(_file.ResolveDict(annot["AP"])?["N"]);
                var appearance = normal as PdfStream
                    ?? (normal is PdfDict states && annot.Name("AS") is { } state ? _file.Resolve(states[state]) as PdfStream : null);
                // A text or choice field shows its value: viewers redraw it from /V when the form asks them to
                // (/NeedAppearances) or when it has no appearance.
                if (annot.Name("Subtype") == "Widget" && FieldValue(annot) is { Length: > 0 } value)
                {
                    needAppearances ??= _file.ResolveDict(_file.Catalog?["AcroForm"])?["NeedAppearances"] is PdfBool { Value: true };
                    if (appearance == null || needAppearances == true)
                    {
                        PlainText(value, _file.Numbers(annot["Rect"], 4));
                        continue;
                    }
                }
                if (appearance == null) continue;
                var resources = _file.ResolveDict(appearance.Dict["Resources"]);
                if (resources == null)
                {
                    if (!formResourcesRead)
                    {
                        formResources = _file.ResolveDict(_file.ResolveDict(_file.Catalog?["AcroForm"])?["DR"]);
                        formResourcesRead = true;
                    }
                    resources = formResources;
                }
                if (!HasFonts(resources)) continue;
                var ctm = AppearanceMatrix(_file.Numbers(annot["Rect"], 4), _file.Numbers(appearance.Dict["BBox"], 4), _file.Numbers(appearance.Dict["Matrix"], 6));
                if (ReadContent(appearance) is not { } data) continue;
                _assembler.Break();
                Walk(data, resources, ctm);
            }
            catch (Exception e) when (PdfTextReader.IsUnitFailure(e)) { Fail(e); }
        }
    }

    /// <summary>The value of a text or choice field (inherited through /Parent), or null.</summary>
    string? FieldValue(PdfDict widget)
    {
        string? type = null;
        PdfObject? value = null;
        var node = widget;
        for (int depth = 0; node != null && depth < 32 && (type == null || value == null); depth++)
        {
            type ??= node.Name("FT");
            value ??= _file.Resolve(node["V"]);
            node = _file.ResolveDict(node["Parent"]);
        }
        if (type is not ("Tx" or "Ch")) return null;
        return value switch
        {
            PdfString s => PdfText.Decode(s.Bytes),
            PdfArray a => string.Join("\n", a.Items.Select(x => _file.Resolve(x)).OfType<PdfString>().Take(256).Select(x => PdfText.Decode(x.Bytes))),
            _ => null,
        };
    }

    /// <summary>Text that isn't drawn with a font (a field's value), as one block over a rectangle.</summary>
    void PlainText(string text, double[]? rect)
    {
        double x0 = 0, y0 = 0, x1 = 0, y1 = 0;
        if (rect is { Length: >= 4 } r) { x0 = Math.Min(r[0], r[2]); x1 = Math.Max(r[0], r[2]); y0 = Math.Min(r[1], r[3]); y1 = Math.Max(r[1], r[3]); }
        double height = Math.Max(1, y1 - y0);
        var glyph = new PdfGlyph
        {
            Text = text.Length > ExtractSink.MaxFieldChars * 16 ? text[..(ExtractSink.MaxFieldChars * 16)] : text,
            X0 = x0, Y0 = y0, X1 = x1, Y1 = y0,
            Ux = 0, Uy = height, Dx = 1, Dy = 0, Size = height, Ascent = 1, Descent = 0,
        };
        _assembler.Break();
        _assembler.Plain(glyph);
    }

    /// <summary>Maps an appearance's BBox (through its Matrix) onto the annotation's Rect (PDF 12.5.5).</summary>
    static Matrix AppearanceMatrix(double[]? rect, double[]? bbox, double[]? matrix)
    {
        var m = matrix is { Length: >= 6 } v ? new Matrix(v[0], v[1], v[2], v[3], v[4], v[5]) : Matrix.Identity;
        if (rect is not { Length: >= 4 } r || bbox is not { Length: >= 4 } b) return m;
        // The bounding box of the transformed BBox.
        var (ax, ay) = m.Apply(b[0], b[1]);
        var (bx, by) = m.Apply(b[2], b[1]);
        var (cx, cy) = m.Apply(b[2], b[3]);
        var (dx, dy) = m.Apply(b[0], b[3]);
        double x0 = Math.Min(Math.Min(ax, bx), Math.Min(cx, dx)), x1 = Math.Max(Math.Max(ax, bx), Math.Max(cx, dx));
        double y0 = Math.Min(Math.Min(ay, by), Math.Min(cy, dy)), y1 = Math.Max(Math.Max(ay, by), Math.Max(cy, dy));
        double rx0 = Math.Min(r[0], r[2]), rx1 = Math.Max(r[0], r[2]), ry0 = Math.Min(r[1], r[3]), ry1 = Math.Max(r[1], r[3]);
        double sx = x1 - x0 > 1e-9 ? (rx1 - rx0) / (x1 - x0) : 1, sy = y1 - y0 > 1e-9 ? (ry1 - ry0) / (y1 - y0) : 1;
        var fit = new Matrix(sx, 0, 0, sy, rx0 - x0 * sx, ry0 - y0 * sy);
        var result = m.Then(fit);
        return result.IsFinite ? result : Matrix.Identity;
    }

    /// <summary>
    /// A content, form or appearance stream's bytes (pooled; the frame that walks them gives them back), or null when it
    /// is empty, broken or uses a filter that isn't read. Cut, broken and unread streams count as partial.
    /// </summary>
    PdfBytes? ReadContent(PdfStream stream)
    {
        PdfBytes? data;
        try { data = _file.ReadStream(stream, _file.StreamCap); }
        catch (Exception e) when (PdfTextReader.IsUnitFailure(e)) { Fail(e); return null; }
        if (data is not { } bytes) { Partial++; return null; }
        if (bytes.Truncated) Partial++;
        if (bytes.Length > 0) return bytes;
        bytes.Release();
        return null;
    }

    void Fail(Exception e)
    {
        Failures++;
        FirstFailure ??= e;
    }

    /// <summary>
    /// Interprets content with resources from a starting CTM; more parts of the same content come from next, if given.
    /// Takes over the content's bytes (and the next parts'): each is given back once it has been walked.
    /// </summary>
    public void Walk(PdfBytes content, PdfDict? resources, Matrix ctm, Func<PdfBytes?>? next = null)
    {
        _gs = new State { Ctm = ctm, Scale = 1 };
        _tm = _tlm = Matrix.Identity;
        _stack.Clear();
        ReleaseFrames();
        _opCount = 0;
        _overflowQ = 0;
        _frames.Add(new Frame(content, resources, 0, -1, _gs));
        try
        {
            int tokens = 0;
            while (_frames.Count > 0)
            {
                if ((++tokens & 1023) == 0)
                {
                    _budget.Check();
                    if (_stop()) return;
                }
                var frame = _frames[^1];
                var lx = frame.Lexer;
                if (!lx.Next())
                {
                    if (_frames.Count == 1 && next?.Invoke() is { } more)
                    {
                        // The next part of the page's content: same state, same operands.
                        frame.Data.Release();
                        _frames[0] = new Frame(more, frame.Resources, 0, -1, frame.Saved);
                        continue;
                    }
                    EndFrame(frame);
                    continue;
                }
                switch (lx.Kind)
                {
                    case PdfToken.Integer:
                    case PdfToken.Real:
                        Push(new Operand { Number = lx.Number, IsNumber = true });
                        break;
                    case PdfToken.Name:
                        Push(new Operand { Value = lx.Text });
                        break;
                    case PdfToken.String:
                    case PdfToken.HexString:
                        Push(new Operand { Value = lx.StringArray() });
                        break;
                    case PdfToken.ArrayOpen:
                    case PdfToken.DictOpen:
                        Push(new Operand { Value = PdfParser.Parse(lx, false, _budget, out bool stopped) });
                        if (stopped) Operator(frame, lx.Text);
                        break;
                    case PdfToken.Keyword:
                        Operator(frame, lx.Text);
                        break;
                    default:
                        break; // stray closers
                }
            }
        }
        finally { ReleaseFrames(); }
    }

    /// <summary>Gives back the bytes of every frame still open (a walk that stopped early or failed).</summary>
    void ReleaseFrames()
    {
        foreach (var f in _frames) f.Data.Release();
        _frames.Clear();
    }

    void EndFrame(Frame frame)
    {
        _frames.RemoveAt(_frames.Count - 1);
        frame.Data.Release();
        if (_frames.Count == 0) return;
        // Leave the form: its unbalanced q's are dropped and the state before Do comes back.
        if (_stack.Count > frame.StackBase) _stack.RemoveRange(frame.StackBase, _stack.Count - frame.StackBase);
        _gs = frame.Saved;
        _opCount = 0;
    }

    void Push(in Operand op)
    {
        if (_opCount == MaxOperands)
        {
            Array.Copy(_ops, 1, _ops, 0, MaxOperands - 1);
            _opCount--;
        }
        _ops[_opCount++] = op;
    }

    double Num(int fromEnd, double fallback = 0)
    {
        int i = _opCount - fromEnd;
        return i >= 0 && _ops[i].IsNumber && double.IsFinite(_ops[i].Number) ? _ops[i].Number : fallback;
    }

    bool HasNumbers(int count)
    {
        if (_opCount < count) return false;
        for (int i = _opCount - count; i < _opCount; i++) if (!_ops[i].IsNumber) return false;
        return true;
    }

    object? Value(int fromEnd) => _opCount - fromEnd >= 0 ? _ops[_opCount - fromEnd].Value : null;

    void Operator(Frame frame, string op)
    {
        switch (op)
        {
            case "q":
                if (_stack.Count < MaxStateDepth) _stack.Add(_gs); else _overflowQ++;
                break;
            case "Q":
                if (_overflowQ > 0) _overflowQ--;
                else if (_stack.Count > frame.StackBase) { _gs = _stack[^1]; _stack.RemoveAt(_stack.Count - 1); }
                break;
            case "cm":
                if (HasNumbers(6))
                {
                    var m = new Matrix(Num(6), Num(5), Num(4), Num(3), Num(2), Num(1));
                    var ctm = m.Then(_gs.Ctm);
                    if (ctm.IsFinite) _gs.Ctm = ctm;
                }
                break;
            case "BT": _tm = _tlm = Matrix.Identity; break;
            case "Tc": if (HasNumbers(1)) _gs.CharSpace = Num(1); break;
            case "Tw": if (HasNumbers(1)) _gs.WordSpace = Num(1); break;
            case "Tz": if (HasNumbers(1)) _gs.Scale = Num(1, 100) / 100; break;
            case "TL": if (HasNumbers(1)) _gs.Leading = Num(1); break;
            case "Ts": if (HasNumbers(1)) _gs.Rise = Num(1); break;
            case "Tf":
                if (_opCount >= 2 && Value(2) is string fontName)
                {
                    _gs.Font = Font(frame, fontName);
                    _gs.Size = Num(1, 0);
                }
                break;
            case "Td": if (HasNumbers(2)) MoveLine(Num(2), Num(1)); break;
            case "TD":
                if (HasNumbers(2)) { _gs.Leading = -Num(1); MoveLine(Num(2), Num(1)); }
                break;
            case "Tm":
                if (HasNumbers(6))
                {
                    var m = new Matrix(Num(6), Num(5), Num(4), Num(3), Num(2), Num(1));
                    if (m.IsFinite) _tm = _tlm = m;
                }
                break;
            case "T*": MoveLine(0, -_gs.Leading); break;
            case "Tj":
                if (Value(1) is byte[] s) Show(s, false);
                break;
            case "'":
                MoveLine(0, -_gs.Leading);
                if (Value(1) is byte[] s1) Show(s1, false);
                break;
            case "\"":
                if (_opCount >= 3 && Value(1) is byte[] s2)
                {
                    _gs.WordSpace = Num(3);
                    _gs.CharSpace = Num(2);
                    MoveLine(0, -_gs.Leading);
                    Show(s2, false);
                }
                break;
            case "TJ":
                if (Value(1) is PdfArray array) ShowArray(array);
                break;
            case "Do":
                if (Value(1) is string xobject) DoXObject(frame, xobject);
                break;
            case "BI":
                SkipInlineImage(frame.Lexer);
                break;
        }
        _opCount = 0;
    }

    void MoveLine(double tx, double ty)
    {
        var m = new Matrix(1, 0, 0, 1, tx, ty).Then(_tlm);
        if (!m.IsFinite) return;
        _tlm = _tm = m;
    }

    PdfFont Font(Frame frame, string name)
    {
        var fonts = _file.ResolveDict(frame.Resources?["Font"]);
        var entry = fonts?[name];
        if (entry is PdfRef r && _fontsByRef.TryGetValue(r.Number, out var byRef)) return byRef;
        var dict = _file.ResolveDict(entry);
        if (dict == null) return _fallbackFont ??= Fallback();
        if (_fonts.TryGetValue(dict, out var known)) return known;
        if (_fonts.Count + _fontsByRef.Count >= MaxFonts) return _fallbackFont ??= Fallback();
        _budget.Check(); // thousands of fonts may each be loaded by a Tf alone
        var font = PdfFont.Load(_file, dict, _fonts.Count + _fontsByRef.Count + 1);
        if (entry is PdfRef fr) _fontsByRef[fr.Number] = font;
        else _fonts[dict] = font;
        return font;
    }

    PdfFont Fallback() => PdfFont.Load(_file, new PdfDict(), 0);

    void ShowArray(PdfArray array)
    {
        bool kern = false;
        foreach (var item in array.Items)
        {
            switch (item)
            {
                case PdfString s:
                    Show(s.Bytes, kern);
                    kern = false;
                    break;
                case PdfNumber n when double.IsFinite(n.Value):
                    {
                        double adjust = -n.Value / 1000 * _gs.Size;
                        if (_gs.Font?.Vertical == true) Advance(0, adjust);
                        else Advance(adjust * _gs.Scale, 0);
                        if (n.Value < -200 && _gs.Font?.Vertical != true) kern = true;
                        break;
                    }
            }
        }
    }

    void Advance(double tx, double ty)
    {
        var m = new Matrix(1, 0, 0, 1, tx, ty).Then(_tm);
        if (m.IsFinite) _tm = m;
    }

    void Show(byte[] bytes, bool kernSpace)
    {
        var font = _gs.Font ??= _fallbackFont ??= Fallback();
        if (bytes.Length == 0) return;
        _run.Clear();
        double size = _gs.Size, scale = _gs.Scale, rise = _gs.Rise;
        bool vertical = font.Vertical;
        for (int i = 0; i < bytes.Length;)
        {
            int n = font.NextCode(bytes, i, out uint code);
            i += n;
            string? text = font.Unicode(code, n);
            var m = _tm.Then(_gs.Ctm);
            double tx = 0, ty = 0;
            if (vertical) ty = font.VerticalAdvance / 1000 * size + _gs.CharSpace + (n == 1 && code == 32 ? _gs.WordSpace : 0);
            else tx = (font.Width(code) / 1000 * size + _gs.CharSpace + (n == 1 && code == 32 ? _gs.WordSpace : 0)) * scale;
            var (x0, y0) = m.Apply(0, rise);
            var (x1, y1) = m.Apply(tx, rise + ty);
            var (ux, uy) = m.Vector(0, size);
            var (dx, dy) = vertical ? m.Vector(0, -1) : m.Vector(1, 0);
            double dl = Math.Sqrt(dx * dx + dy * dy);
            if (dl > 0) { dx /= dl; dy /= dl; } else { dx = 1; dy = 0; }
            if (vertical) (ux, uy) = m.Vector(size, 0);
            double usize = Math.Sqrt(ux * ux + uy * uy);
            _run.Add(new PdfGlyph
            {
                Text = text,
                Bad = text == null || PdfTextAssembler.IsUnreadable(text),
                KernSpace = kernSpace && _run.Count == 0,
                X0 = x0, Y0 = y0, X1 = x1, Y1 = y1,
                Ux = ux, Uy = uy, Dx = dx, Dy = dy,
                Size = double.IsFinite(usize) ? usize : 0,
                Ascent = vertical ? 0.5 : font.Ascent,
                Descent = vertical ? -0.5 : font.Descent,
            });
            Advance(tx, ty);
            if (_run.Count == MaxRunGlyphs)
            {
                // A huge string goes to the assembler a piece at a time (bounded memory, budget checked between).
                _assembler.Run(font, _run);
                _run.Clear();
                _budget.Check();
                if (_stop()) return;
            }
            else if ((++_glyphs & (CheckGlyphs - 1)) == 0)
            {
                // Every 1,024 glyphs, whatever the strings' sizes: many strings just under a run between two of the
                // walker's token checks, each glyph costly (a font made to be slow), never run past the time limit.
                _budget.Check();
                if (_stop()) { _assembler.Run(font, _run); return; }
            }
        }
        _assembler.Run(font, _run);
    }

    const int MaxRunGlyphs = 4096;
    /// <summary>Glyphs between two budget checks in <see cref="Show"/> (a power of two).</summary>
    const int CheckGlyphs = 1024;
    int _glyphs;

    void DoXObject(Frame frame, string name)
    {
        var xobjects = _file.ResolveDict(frame.Resources?["XObject"]);
        var entry = xobjects?[name];
        int number = entry is PdfRef r ? r.Number : -1;
        if (_file.Resolve(entry) is not PdfStream form || form.Dict.Name("Subtype") != "Form") return; // images: dictionary only
        if (_frames.Count > MaxFormDepth) return;
        foreach (var f in _frames)
            if (number >= 0 && f.FormNumber == number) return; // a form that draws itself
        var resources = _file.ResolveDict(form.Dict["Resources"]);
        // A form with resources of its own and no fonts in them shows no text: its stream isn't read.
        if (resources != null && !HasFonts(resources)) return;
        resources ??= frame.Resources;
        var matrix = _file.Numbers(form.Dict["Matrix"], 6) is { } mv ? new Matrix(mv[0], mv[1], mv[2], mv[3], mv[4], mv[5]) : Matrix.Identity;
        if (ReadContent(form) is not { } data) return;
        var saved = _gs;
        var ctm = matrix.Then(_gs.Ctm);
        if (ctm.IsFinite) _gs.Ctm = ctm;
        _frames.Add(new Frame(data, resources, _stack.Count, number, saved));
    }

    /// <summary>Skips BI … ID &lt;data&gt; EI: the data's length from /L or the image's size when unfiltered, else the first EI followed by something that looks like content.</summary>
    void SkipInlineImage(PdfLexer lx)
    {
        var dict = new Dictionary<string, PdfObject>(StringComparer.Ordinal);
        string? key = null;
        int guard = 0;
        while (lx.Next() && guard++ < 4096)
        {
            if (lx.IsKeyword("ID")) break;
            if (lx.Kind == PdfToken.Name && key == null) { key = lx.Text; continue; }
            var value = PdfParser.Parse(lx, false, _budget, out bool stopped);
            if (key != null) dict[key] = value;
            key = null;
            if (stopped && lx.IsKeyword("ID")) break;
        }
        if (lx.Kind == PdfToken.Eof) return;
        // One white-space byte follows ID.
        if (PdfLexer.IsWhite(lx.Peek())) lx.ReadByte();
        long start = lx.Position;
        long length = -1;
        if (dict.TryGetValue("L", out var l) || dict.TryGetValue("Length", out l)) length = l is PdfNumber ln ? (long)ln.Value : -1;
        if (length < 0 && !dict.ContainsKey("F") && !dict.ContainsKey("Filter")) length = UnfilteredLength(dict);
        if (length >= 0 && LooksLikeEi(lx, start + length)) { lx.Position = EiEnd(lx, start + length); return; }
        // Search for white space, "EI", then white space or the end.
        for (long at = start; at < lx.Limit - 1; at++)
        {
            if (((at - start) & 0xFFFF) == 0) _budget.Check();
            lx.Position = at;
            int b = lx.ReadByte();
            if (b != 'E' || lx.Peek() != 'I') continue;
            if (at > start && !PdfLexer.IsWhite(ByteAt(lx, at - 1))) continue;
            if (LooksLikeEi(lx, at)) { lx.Position = at + 2; return; }
        }
        lx.Position = lx.Limit;
    }

    static int ByteAt(PdfLexer lx, long at)
    {
        long keep = lx.Position;
        lx.Position = at;
        int b = lx.ReadByte();
        lx.Position = keep;
        return b;
    }

    static long UnfilteredLength(Dictionary<string, PdfObject> d)
    {
        long w = Value(d, "W", "Width"), h = Value(d, "H", "Height"), bpc = Value(d, "BPC", "BitsPerComponent");
        if (d.TryGetValue("IM", out var im) || d.TryGetValue("ImageMask", out im)) { if (im is PdfBool { Value: true }) bpc = 1; }
        int components = (d.TryGetValue("CS", out var cs) || d.TryGetValue("ColorSpace", out cs)) && cs is PdfName n
            ? n.Value switch { "RGB" or "DeviceRGB" => 3, "CMYK" or "DeviceCMYK" => 4, _ => 1 } : 1;
        if (w <= 0 || h <= 0 || bpc <= 0 || w > 1 << 16 || h > 1 << 16) return -1;
        return h * ((w * components * bpc + 7) / 8);
    }

    static long Value(Dictionary<string, PdfObject> d, string a, string b)
        => (d.TryGetValue(a, out var v) || d.TryGetValue(b, out v)) && v is PdfNumber n ? (long)n.Value : -1;

    /// <summary>"EI" at an offset (after optional white space), followed by white space or the end.</summary>
    static bool LooksLikeEi(PdfLexer lx, long at)
    {
        if (at < 0 || at > lx.Limit) return false;
        lx.Position = at;
        while (PdfLexer.IsWhite(lx.Peek())) lx.ReadByte();
        if (lx.ReadByte() != 'E' || lx.ReadByte() != 'I') return false;
        int after = lx.Peek();
        if (after >= 0 && !PdfLexer.IsWhite(after) && !PdfLexer.IsDelimiter(after)) return false;
        // What follows should read as content: printable ASCII for a while.
        for (int i = 0; i < 24; i++)
        {
            int b = lx.ReadByte();
            if (b < 0) break;
            if (b >= 0x80 || b < 0x20 && !PdfLexer.IsWhite(b)) return false;
        }
        return true;
    }

    static long EiEnd(PdfLexer lx, long at)
    {
        lx.Position = at;
        while (PdfLexer.IsWhite(lx.Peek())) lx.ReadByte();
        return lx.Position + 2;
    }
}
