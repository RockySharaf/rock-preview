using System.Text;

namespace Anything.Core.Content.Extract;

/// <summary>One leaf page with its inherited attributes.</summary>
internal sealed record PdfPage(int Index, PdfDict Dict, PdfDict? Resources, double[] MediaBox, double[]? CropBox, int Rotate)
{
    /// <summary>The visible box: CropBox within MediaBox, else MediaBox (x0, y0, x1, y1, normalised).</summary>
    public double[] VisibleBox
    {
        get
        {
            var m = MediaBox;
            if (CropBox is not { } c) return m;
            double x0 = Math.Max(c[0], m[0]), y0 = Math.Max(c[1], m[1]), x1 = Math.Min(c[2], m[2]), y1 = Math.Min(c[3], m[3]);
            return x1 - x0 > 1 && y1 - y0 > 1 ? [x0, y0, x1, y1] : m;
        }
    }
}

/// <summary>
/// Xref tables and streams, /Prev and /XRefStm chains (at most 32 hops, loop set), object streams (decoded ones kept in
/// a 4 MB LRU), the page tree (iterative, inherited attributes), stream data, and a broken-xref rebuild that scans for
/// "n g obj" (files of 64 MB or less, within the read budget). Classic xref entries are read by direct 20-byte
/// addressing, never all loaded; xref-stream entries stay in their decoded bytes. Small reads go through a block cache,
/// so a dictionary next to an image costs a few KB of that image at most and the image data is never read through.
/// Loading one object can load others (an object stream, its /Length) only a few levels deep: an object stream's own
/// entries never come from another object stream, and nesting stops at 16 levels, so crafted chains can't run the
/// stack out. Stream buffers come from the pool; Dispose gives back the object streams kept.
/// </summary>
internal sealed class PdfFile : IDisposable
{
    const int BlockSize = 4096, BlockCount = 64;
    const int MaxHops = 32;
    const long RebuildMaxBytes = 64L << 20;
    const int MaxObjectBytes = 8 << 20;
    const int ObjStmCacheBytes = 4 << 20;
    const int MaxCachedObjects = 50_000;
    const int MaxObjStmObjects = 1 << 20;
    /// <summary>Most objects the rebuild scan records, and most object-stream members it indexes (about 6 MB of offsets).</summary>
    internal const int MaxRebuiltObjects = 1 << 19;
    /// <summary>Most object streams the rebuild scan opens.</summary>
    internal const int MaxRebuiltObjStms = 4096;
    /// <summary>Most "trailer" and xref-stream hits the rebuild keeps: only the newest ones decide the trailer.</summary>
    const int MaxRebuiltTrailers = 64;
    /// <summary>Most objects being loaded inside one another (legitimate files need about 4).</summary>
    const int MaxNesting = 16;

    readonly IByteSource _source;
    readonly ExtractBudget _budget;
    readonly long[] _blockIds = new long[BlockCount];
    readonly byte[][] _blocks = new byte[BlockCount][];
    readonly int[] _blockLengths = new int[BlockCount];
    readonly long[] _blockAge = new long[BlockCount];
    long _clock;
    long _header;

    readonly List<Section> _sections = [];
    readonly Dictionary<int, Entry> _entries = [];
    readonly Dictionary<int, PdfObject?> _objects = [];
    readonly HashSet<int> _resolving = [];
    readonly LinkedList<ObjStm> _objStms = new();
    long _objStmBytes;
    int _encryptNumber = -1;
    int _inObjStm;

    bool _rebuilt;
    ScanTable? _scanned;
    List<int>? _stmCandidates;
    Dictionary<int, (int Stream, int Index)>? _stmIndex;

    abstract class Section { }

    sealed class TableSection(List<(int Start, int Count, long Offset)> subsections, int stride) : Section
    {
        public readonly List<(int Start, int Count, long Offset)> Subsections = subsections;
        public readonly int Stride = stride;
    }

    sealed class StreamSection(byte[] data, int length, int w0, int w1, int w2, List<(int Start, int Count, long Index)> ranges) : Section
    {
        public readonly byte[] Data = data;
        public readonly int Length = length, W0 = w0, W1 = w1, W2 = w2;
        public readonly List<(int Start, int Count, long Index)> Ranges = ranges;
    }

    readonly record struct Entry(byte Type, long Field2, int Field3);

    sealed class ObjStm(int number, PdfBytes bytes, int first, int[] numbers, int[] offsets)
    {
        public readonly int Number = number;
        public readonly PdfBytes Bytes = bytes;
        public readonly int First = first;
        public readonly int[] Numbers = numbers, Offsets = offsets;
        public byte[] Data => Bytes.Array;
        public int Length => Bytes.Length;
    }

    PdfFile(IByteSource source, ExtractBudget budget)
    {
        _source = source;
        _budget = budget;
        Array.Fill(_blockIds, -1);
    }

    /// <summary>The file's length.</summary>
    public long Length => _source.Length;
    /// <summary>The merged trailer (Root, Info, Encrypt, ID from the newest section that has them).</summary>
    public PdfDict Trailer { get; private set; } = new();
    /// <summary>The document catalog.</summary>
    public PdfDict? Catalog { get; private set; }
    /// <summary>The empty-password decryption, when the document is encrypted and it opened.</summary>
    public PdfCrypt? Crypt { get; private set; }
    /// <summary>The document is encrypted with a password or a handler this reader doesn't open.</summary>
    public bool Locked { get; private set; }
    /// <summary>The cross-reference had to be rebuilt by scanning.</summary>
    public bool Rebuilt => _scanned != null;
    /// <summary>The budget everything is charged to.</summary>
    public ExtractBudget Budget => _budget;

    /// <summary>The most entries the document's font tables hold in all (/W widths, ToUnicode and encoding CMaps, fonts' Unicode caches).</summary>
    internal const int MaxFontTableEntries = 1 << 20;

    /// <summary>
    /// Font tables parsed once per document, by the object they came from (a /W array, a descendant font, a CMap stream):
    /// the fonts that share one share its table, however many there are.
    /// </summary>
    internal Dictionary<(byte Kind, int Number), object?> FontTables { get; } = [];

    /// <summary>Entries the document's font tables hold so far.</summary>
    internal int FontTableEntries { get; private set; }

    /// <summary>Entries still allowed in the document's font tables (past them, fonts fall back to default widths and no mapping).</summary>
    internal int FontTableRoom => Math.Max(0, MaxFontTableEntries - FontTableEntries);

    /// <summary>Counts entries a font table took (tables are cut to <see cref="FontTableRoom"/> before they are built).</summary>
    internal void ChargeFontTable(int entries) => FontTableEntries += Math.Max(0, entries);
    /// <summary>Decoded bytes of the object streams kept now (at most 4 MB, plus the one just read).</summary>
    internal long CachedObjStmBytes => _objStmBytes;
    /// <summary>Object streams kept now.</summary>
    internal int CachedObjStms => _objStms.Count;
    /// <summary>Most bytes one stream decodes to (ExtractLimits.MaxStreamBytes).</summary>
    public int StreamCap => (int)Math.Clamp(_budget.Limits.MaxStreamBytes, 0, int.MaxValue / 2);
    /// <summary>
    /// Objects that are listed but couldn't be read, reference chains that never end, nesting cut at its cap, and page
    /// tree nodes skipped: what was read of the document is then only part of it.
    /// </summary>
    public int Broken { get; private set; }

    /// <summary>Gives back the pooled buffers (read blocks, decoded object streams kept); the file isn't used afterwards.</summary>
    public void Dispose()
    {
        DropObjStms();
        for (int i = 0; i < BlockCount; i++)
        {
            if (_blocks[i] is { } block) PdfPool.Return(block);
            _blocks[i] = null!;
            _blockIds[i] = -1;
        }
    }

    void DropObjStms()
    {
        foreach (var o in _objStms) o.Bytes.Release();
        _objStms.Clear();
        _objStmBytes = 0;
    }

    /// <summary>Opens a PDF: header, cross-reference (rebuilt when broken), trailer, security handler, catalog.</summary>
    public static PdfFile Open(IByteSource source, ExtractBudget budget)
    {
        var file = new PdfFile(source, budget);
        file.FindHeader();
        bool ok = false;
        try { ok = file.ReadXref(); }
        catch (Exception e) when (IsFormatError(e)) { }
        if (!ok || file.Trailer["Root"] == null) file.Rebuild();
        file.SetUpSecurity();
        file.Catalog = file.Resolve(file.Trailer["Root"]) as PdfDict;
        if (file.Catalog == null && !file._rebuilt)
        {
            file.Rebuild();
            file.Catalog = file.Resolve(file.Trailer["Root"]) as PdfDict;
        }
        if (file.Catalog == null) throw new PdfFormatException("no document catalog");
        return file;
    }

    /// <summary>An exception that means bad file data (not a limit, cancellation, I/O error or out of memory).</summary>
    internal static bool IsFormatError(Exception e)
        => e is not (OperationCanceledException or OutOfMemoryException or ContentIoException or ContentLimitException);

    void FindHeader()
    {
        Span<byte> head = stackalloc byte[1024];
        int n = ReadSmall(0, head);
        int at = head[..n].IndexOf("%PDF-"u8);
        _header = Math.Max(0, at);
        _hasHeader = at >= 0;
    }

    bool _hasHeader;

    // ── Reading bytes ──────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Reads through the block cache; returns the bytes read (fewer only at the end of the file).</summary>
    int ReadSmall(long offset, Span<byte> dest)
    {
        int done = 0;
        while (done < dest.Length)
        {
            long at = offset + done;
            if (at >= Length || at < 0) break;
            long id = at / BlockSize;
            var block = Block(id, out int blockLength);
            int inBlock = (int)(at - id * BlockSize);
            int n = Math.Min(dest.Length - done, blockLength - inBlock);
            if (n <= 0) break;
            block.AsSpan(inBlock, n).CopyTo(dest[done..]);
            done += n;
        }
        return done;
    }

    byte[] Block(long id, out int length)
    {
        int oldest = 0;
        for (int i = 0; i < BlockCount; i++)
        {
            if (_blockIds[i] == id) { _blockAge[i] = ++_clock; length = _blockLengths[i]; return _blocks[i]; }
            if (_blockAge[i] < _blockAge[oldest]) oldest = i;
        }
        var block = _blocks[oldest] ??= PdfPool.Rent(BlockSize);
        _blockIds[oldest] = -1;
        long start = id * BlockSize;
        int want = (int)Math.Min(BlockSize, Length - start);
        int got = 0;
        while (got < want)
        {
            int n = _source.ReadAt(start + got, block.AsSpan(got, want - got));
            if (n <= 0) break;
            got += n;
        }
        _blockIds[oldest] = id;
        _blockLengths[oldest] = got;
        _blockAge[oldest] = ++_clock;
        length = got;
        return block;
    }

    /// <summary>Reads a range for a lexer: small ranges through the cache, large ones straight from the file.</summary>
    int FillLexer(long offset, byte[] buffer, int index, int count)
    {
        if (count >= 16 << 10)
        {
            int got = 0;
            while (got < count)
            {
                int n = _source.ReadAt(offset + got, buffer.AsSpan(index + got, count - got));
                if (n <= 0) break;
                got += n;
            }
            return got;
        }
        return ReadSmall(offset, buffer.AsSpan(index, count));
    }

    /// <summary>A lexer over the file from an offset, reading at most limit bytes past it.</summary>
    PdfLexer LexerAt(long offset, long limit = MaxObjectBytes)
        => new(FillLexer, offset, Math.Min(Length, offset + limit), firstChunk: 512);

    // ── Cross-reference ────────────────────────────────────────────────────────────────────────────────────────

    bool ReadXref()
    {
        long start = FindStartXref();
        if (start < 0) return false;
        var visited = new HashSet<long>();
        var queue = new Queue<long>();
        queue.Enqueue(start);
        bool first = true;
        while (queue.Count > 0 && visited.Count < MaxHops)
        {
            _budget.Check();
            long at = queue.Dequeue();
            if (at < 0 || at >= Length || !visited.Add(at)) continue;
            var trailer = ReadSection(at) ?? (_header > 0 ? ReadSection(at + _header) : null);
            if (trailer == null)
            {
                if (first) return false;
                continue;
            }
            first = false;
            MergeTrailer(trailer);
            if (trailer["XRefStm"] is PdfNumber stm && stm.Value > 0)
            {
                long s = (long)stm.Value;
                if (visited.Add(s)) ReadSection(s);
            }
            if (trailer["Prev"] is PdfNumber prev && prev.Value >= 0) queue.Enqueue((long)prev.Value);
        }
        return _sections.Count > 0;
    }

    void MergeTrailer(PdfDict trailer)
    {
        foreach (var key in (ReadOnlySpan<string>)["Root", "Info", "Encrypt", "ID", "Size"])
            if (Trailer[key] == null && trailer[key] is { } value) Trailer.Items[key] = value;
    }

    long FindStartXref()
    {
        foreach (int window in (ReadOnlySpan<int>)[4096, 32 << 10])
        {
            int size = (int)Math.Min(window, Length);
            var tail = PdfPool.Rent(size);
            try
            {
                int n = ReadSmall(Length - size, tail.AsSpan(0, size));
                int at = tail.AsSpan(0, n).LastIndexOf("startxref"u8);
                if (at < 0) continue;
                int i = at + 9;
                while (i < n && PdfLexer.IsWhite(tail[i])) i++;
                int j = i;
                while (j < n && tail[j] is >= (byte)'0' and <= (byte)'9') j++;
                if (j > i && PdfLexer.TryNumber(tail.AsSpan(i, j - i), out long offset, out _, out bool isInt) && isInt) return offset;
            }
            finally { PdfPool.Return(tail); }
        }
        return -1;
    }

    /// <summary>Reads a table or stream section at an offset; returns its trailer dictionary, or null when there is none.</summary>
    PdfDict? ReadSection(long offset)
    {
        try
        {
            var lx = LexerAt(offset, Length);
            if (!lx.Next()) return null;
            if (lx.IsKeyword("xref")) return ReadTable(lx);
            if (lx.Kind == PdfToken.Integer) return ReadStreamSection(offset);
            return null;
        }
        catch (Exception e) when (IsFormatError(e)) { return null; }
    }

    /// <summary>
    /// Most subsections one table section keeps (as many as an xref stream's /Index can hold); a table with more is taken
    /// as broken (it costs memory and a lookup each, never seen in a real file), and the cross-reference is rebuilt.
    /// </summary>
    internal const int MaxSubsections = PdfParser.MaxItems;

    PdfDict? ReadTable(PdfLexer lx)
    {
        var subsections = new List<(int Start, int Count, long Offset)>();
        int stride = 20;
        bool sawEntries = false, sawHeader = false;
        PdfDict? trailer = null;
        while (lx.Next())
        {
            _budget.Check();
            if (lx.IsKeyword("trailer"))
            {
                lx.Next();
                trailer = PdfParser.Parse(lx, true, _budget, out _) as PdfDict;
                break;
            }
            if (lx.Kind != PdfToken.Integer) break;
            long start = lx.Integer;
            if (!lx.Next() || lx.Kind != PdfToken.Integer) break;
            long count = lx.Integer;
            lx.SkipWhite();
            long entries = lx.Position;
            if (start < 0 || count < 0 || start > int.MaxValue) break;
            sawHeader = true;
            if (!sawEntries && count > 0) { stride = EntryStride(entries); sawEntries = true; }
            long room = Math.Max(0, (Length - entries) / stride);
            count = Math.Min(count, room);
            if (start + count > int.MaxValue) count = int.MaxValue - start;
            lx.Position = entries + count * stride;
            if (count == 0) continue; // an empty header lists nothing
            // One that carries on from the last (its numbers and its entries) is the same subsection.
            if (subsections.Count > 0 && subsections[^1] is var last && last.Start + (long)last.Count == start && last.Offset + last.Count * stride == entries)
            {
                subsections[^1] = last with { Count = (int)(last.Count + count) };
                continue;
            }
            if (subsections.Count >= MaxSubsections) return null;
            subsections.Add(((int)start, (int)count, entries));
        }
        if (subsections.Count > 0) _sections.Add(new TableSection(SortedByStart(subsections), stride));
        return trailer ?? (sawHeader ? new PdfDict() : null);
    }

    /// <summary>Subsections or ranges by their first number (file order among equal ones), for <see cref="Holding"/>.</summary>
    static List<(int Start, int Count, long Offset)> SortedByStart(List<(int Start, int Count, long Offset)> list)
    {
        for (int i = 1; i < list.Count; i++)
            if (list[i].Start < list[i - 1].Start) return [.. list.OrderBy(s => s.Start)];
        return list;
    }

    /// <summary>
    /// The subsection or range holding an object number, by binary search over the list sorted by start (one walk back
    /// over a few overlapping ones); -1 when none does.
    /// </summary>
    static int Holding(List<(int Start, int Count, long Offset)> list, int number)
    {
        int lo = 0, hi = list.Count - 1, found = -1;
        while (lo <= hi)
        {
            int mid = (lo + hi) >> 1;
            if (list[mid].Start <= number) { found = mid; lo = mid + 1; } else hi = mid - 1;
        }
        for (int i = found; i >= 0 && i > found - 8; i--)
            if (number < list[i].Start + (long)list[i].Count) return i;
        return -1;
    }

    /// <summary>20 bytes per entry as the format says; 19 when a writer ended entries with one byte.</summary>
    int EntryStride(long entries)
    {
        Span<byte> e = stackalloc byte[40];
        int n = ReadSmall(entries, e);
        if (n >= 38 && e[18] is (byte)'\r' or (byte)'\n' && e[19] is >= (byte)'0' and <= (byte)'9' && e[37] is (byte)'\r' or (byte)'\n') return 19;
        return 20;
    }

    PdfDict? ReadStreamSection(long offset)
    {
        if (ParseIndirect(offset, -1, decrypt: false) is not PdfStream stream) return null;
        var d = stream.Dict;
        if (d.Name("Type") != "XRef" && d["W"] == null) return null;
        if (d["W"] is not PdfArray w || w.Count < 3) return null;
        int w0 = Width(w[0]), w1 = Width(w[1]), w2 = Width(w[2]);
        if (w0 < 0 || w1 < 0 || w2 < 0 || w0 + w1 + w2 == 0) return null;
        var data = ReadStream(stream, StreamCap, decrypt: false);
        if (data is not { } bytes) return d;
        int entrySize = w0 + w1 + w2;
        var ranges = new List<(int, int, long)>();
        long size = d["Size"] is PdfNumber s ? (long)s.Value : 0;
        if (d["Index"] is PdfArray index && index.Count >= 2)
        {
            for (int i = 0; i + 1 < index.Count; i += 2)
                if (index[i] is PdfNumber a && index[i + 1] is PdfNumber b && a.Value >= 0 && b.Value >= 0)
                    ranges.Add(((int)Math.Min(a.Value, int.MaxValue), (int)Math.Min(b.Value, int.MaxValue), 0));
        }
        else ranges.Add((0, (int)Math.Clamp(size, 0, int.MaxValue), 0));
        // Entries run on through the ranges; cut every range to the decoded bytes.
        long position = 0, available = bytes.Length / entrySize;
        for (int i = 0; i < ranges.Count; i++)
        {
            var (start, count, _) = ranges[i];
            int fit = (int)Math.Clamp(available - position, 0, count);
            ranges[i] = (start, fit, position);
            position += fit;
        }
        _sections.Add(new StreamSection(bytes.Array, bytes.Length, w0, w1, w2, SortedByStart(ranges)));
        return d;
    }

    static int Width(PdfObject o) => o is PdfNumber n && n.Value is >= 0 and <= 8 ? (int)n.Value : -1;

    Entry Lookup(int number)
    {
        if (_entries.TryGetValue(number, out var cached)) return cached;
        var entry = Find(number);
        if (_entries.Count > 200_000) _entries.Clear();
        _entries[number] = entry;
        return entry;
    }

    Entry Find(int number)
    {
        if (_scanned != null)
        {
            if (_scanned.TryGet(number, out long at)) return new Entry(1, at, 0);
            var index = StmIndex();
            if (index.TryGetValue(number, out var place)) return new Entry(2, place.Stream, place.Index);
            return default;
        }
        Span<byte> entry = stackalloc byte[20];
        foreach (var section in _sections)
        {
            if (section is TableSection table)
            {
                if (Holding(table.Subsections, number) is >= 0 and var k)
                {
                    var (start, _, offset) = table.Subsections[k];
                    var e = entry;
                    int n = ReadSmall(offset + (long)(number - start) * table.Stride, e);
                    if (ParseTableEntry(e[..n], out long objectOffset, out int gen, out bool inUse) && inUse && objectOffset > 0)
                        return new Entry(1, objectOffset, gen);
                }
            }
            else if (section is StreamSection stream)
            {
                if (Holding(stream.Ranges, number) is >= 0 and var k)
                {
                    var (start, _, index) = stream.Ranges[k];
                    int size = stream.W0 + stream.W1 + stream.W2;
                    long at = (index + number - start) * size;
                    if (at + size <= stream.Length)
                    {
                        var e = stream.Data.AsSpan((int)at, size);
                        long type = stream.W0 == 0 ? 1 : Field(e[..stream.W0]);
                        long f2 = Field(e.Slice(stream.W0, stream.W1));
                        long f3 = Field(e.Slice(stream.W0 + stream.W1, stream.W2));
                        if (type == 1 && f2 > 0) return new Entry(1, f2, (int)Math.Min(f3, 65535));
                        if (type == 2 && f2 is > 0 and <= int.MaxValue) return new Entry(2, f2, (int)Math.Min(f3, int.MaxValue));
                    }
                }
            }
        }
        return default;
    }

    static long Field(ReadOnlySpan<byte> bytes)
    {
        long v = 0;
        foreach (byte b in bytes) v = (v << 8) | b;
        return v;
    }

    /// <summary>"oooooooooo ggggg n": leniently, two numbers then n or f.</summary>
    static bool ParseTableEntry(ReadOnlySpan<byte> e, out long offset, out int gen, out bool inUse)
    {
        offset = 0; gen = 0; inUse = false;
        int i = 0;
        while (i < e.Length && e[i] == ' ') i++;
        long a = 0;
        int digits = 0;
        while (i < e.Length && e[i] is >= (byte)'0' and <= (byte)'9' && digits < 12) { a = a * 10 + (e[i++] - '0'); digits++; }
        if (digits == 0) return false;
        while (i < e.Length && e[i] == ' ') i++;
        long b = 0;
        digits = 0;
        while (i < e.Length && e[i] is >= (byte)'0' and <= (byte)'9' && digits < 6) { b = b * 10 + (e[i++] - '0'); digits++; }
        if (digits == 0) return false;
        while (i < e.Length && e[i] == ' ') i++;
        if (i >= e.Length || e[i] is not ((byte)'n' or (byte)'f')) return false;
        offset = a;
        gen = (int)b;
        inUse = e[i] == 'n';
        return true;
    }

    // ── Rebuild ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Scans the file for "n g obj" and "trailer" (files of 64 MB or less whose scan fits in what is left of the read
    /// budget) and reads everything from that. What the scan keeps is bounded whatever the file holds: at most
    /// <see cref="MaxRebuiltObjects"/> objects (12 bytes each) and <see cref="MaxRebuiltObjStms"/> object streams, past
    /// which the document is read only in part, and the newest 64 trailers and xref streams.
    /// </summary>
    void Rebuild()
    {
        if (_rebuilt) return;
        _rebuilt = true;
        // Larger files keep their cross-reference as it is: a scan would cost too much, and one past the read budget
        // would only end in its limit with nothing read (a network file is 32 MB at most). A file with no "%PDF-" in
        // its first KB isn't scanned either (web pages saved as .pdf).
        // The scan reads 1/128 more than the file (its chunks overlap): what is left must cover that and the objects after.
        long left = _budget.Limits.MaxBytesRead - _budget.BytesRead;
        if (Length > RebuildMaxBytes || Length + Length / 64 > left || !_hasHeader) return;
        _scanned = new ScanTable();
        _stmCandidates = [];
        _entries.Clear();
        _objects.Clear();
        _cachedSpan = 0;
        var trailers = new Queue<long>();
        var xrefStreams = new Queue<long>();
        int catalog = -1;
        bool capped = false;
        const int Chunk = 64 << 10, Overlap = 256;
        var buffer = PdfPool.Rent(Chunk + Overlap);
        try
        {
            long pos = 0;
            while (pos < Length)
            {
                _budget.Check();
                int want = (int)Math.Min(Chunk + Overlap, Length - pos);
                int n = 0;
                while (n < want)
                {
                    int got = _source.ReadAt(pos + n, buffer.AsSpan(n, want - n));
                    if (got <= 0) break;
                    n += got;
                }
                if (n == 0) break;
                var span = buffer.AsSpan(0, n);
                // Chunks overlap by twice Overlap: each one after the first scans from Overlap on, so the bytes before a
                // hit (its object number) and after it (its dictionary) are in the same chunk, and every hit is scanned once.
                int scanStart = pos == 0 ? 0 : Overlap;
                int scanEnd = pos + n >= Length ? n : n - Overlap;
                for (int i = IndexFrom(span, scanStart, "obj"u8); i >= 0 && i < scanEnd; )
                {
                    if (ObjHeaderBefore(span, i, pos == 0, out int number, out int numberStart) && (i + 3 >= n || PdfLexer.IsBreak(span[i + 3])))
                    {
                        long at = pos + numberStart;
                        if (!_scanned.TryAdd(number, at)) capped = true;
                        var after = span[(i + 3)..Math.Min(n, i + 3 + 160)];
                        if (after.IndexOf("/ObjStm"u8) >= 0)
                        {
                            if (_stmCandidates.Count < MaxRebuiltObjStms) _stmCandidates.Add(number);
                            else capped = true;
                        }
                        if (after.IndexOf("/XRef"u8) >= 0) KeepNewest(xrefStreams, at);
                        if (after.IndexOf("/Catalog"u8) >= 0) catalog = number;
                    }
                    i = IndexFrom(span, i + 3, "obj"u8);
                }
                for (int i = IndexFrom(span, scanStart, "trailer"u8); i >= 0 && i < scanEnd; i = IndexFrom(span, i + 7, "trailer"u8))
                    KeepNewest(trailers, pos + i);
                if (pos + n >= Length || n < want) break;
                pos += Math.Max(1, n - 2 * Overlap);
            }
        }
        finally { PdfPool.Return(buffer); }
        _scanned.Seal();
        // Objects past the cap are left out: what is read is only part of the document.
        if (capped) Broken++;
        Trailer = new PdfDict();
        var newest = trailers.ToArray();
        for (int t = newest.Length - 1; t >= 0; t--)
        {
            _budget.Check();
            try
            {
                var lx = LexerAt(newest[t]);
                if (!lx.Next() || !lx.IsKeyword("trailer") || !lx.Next()) continue;
                if (PdfParser.Parse(lx, true, _budget, out _) is PdfDict d) MergeTrailer(d);
            }
            catch (Exception e) when (IsFormatError(e)) { }
        }
        var streams = xrefStreams.ToArray();
        for (int t = streams.Length - 1; t >= 0 && Trailer["Root"] == null; t--)
        {
            _budget.Check();
            try
            {
                if (ParseIndirect(streams[t], -1, decrypt: false) is PdfStream s) MergeTrailer(s.Dict);
            }
            catch (Exception e) when (IsFormatError(e)) { }
        }
        if (Trailer["Root"] == null && catalog >= 0) Trailer.Items["Root"] = new PdfRef(catalog, 0);
    }

    /// <summary>Where value first turns up in s at or after from; -1 when it doesn't.</summary>
    static int IndexFrom(ReadOnlySpan<byte> s, int from, ReadOnlySpan<byte> value)
    {
        if (from >= s.Length) return -1;
        int at = s[from..].IndexOf(value);
        return at < 0 ? -1 : from + at;
    }

    /// <summary>Adds an offset, keeping only the newest <see cref="MaxRebuiltTrailers"/>.</summary>
    static void KeepNewest(Queue<long> offsets, long at)
    {
        offsets.Enqueue(at);
        if (offsets.Count > MaxRebuiltTrailers) offsets.Dequeue();
    }

    /// <summary>
    /// The rebuild scan's object offsets: numbers and offsets in two arrays (12 bytes an object, no hash table), at most
    /// <see cref="MaxRebuiltObjects"/>, sorted once the scan ends. A number found twice keeps its last offset: the scan
    /// runs forward, so that is the newest definition (an incremental update).
    /// </summary>
    sealed class ScanTable
    {
        int[] _numbers = new int[1024];
        long[] _offsets = new long[1024];

        /// <summary>Objects recorded (after <see cref="Seal"/>, distinct numbers).</summary>
        public int Count { get; private set; }

        /// <summary>Records an object; false when the table is full and it was left out.</summary>
        public bool TryAdd(int number, long at)
        {
            if (Count == _numbers.Length)
            {
                if (Count >= MaxRebuiltObjects) return false;
                int size = Math.Min(_numbers.Length * 2, MaxRebuiltObjects);
                Array.Resize(ref _numbers, size);
                Array.Resize(ref _offsets, size);
            }
            _numbers[Count] = number;
            _offsets[Count++] = at;
            return true;
        }

        /// <summary>Sorts by number and keeps each number's last offset.</summary>
        public void Seal()
        {
            Array.Sort(_numbers, _offsets, 0, Count);
            int kept = 0;
            for (int i = 0; i < Count; i++)
            {
                if (kept > 0 && _numbers[kept - 1] == _numbers[i])
                {
                    _offsets[kept - 1] = Math.Max(_offsets[kept - 1], _offsets[i]);
                    continue;
                }
                _numbers[kept] = _numbers[i];
                _offsets[kept++] = _offsets[i];
            }
            Count = kept;
        }

        /// <summary>The offset of an object (after <see cref="Seal"/>).</summary>
        public bool TryGet(int number, out long at)
        {
            int i = Array.BinarySearch(_numbers, 0, Count, number);
            at = i >= 0 ? _offsets[i] : 0;
            return i >= 0;
        }

        /// <summary>The object was found by the scan.</summary>
        public bool Contains(int number) => Array.BinarySearch(_numbers, 0, Count, number) >= 0;
    }

    /// <summary>
    /// Before "obj" at i: white space, a generation, white space, an object number, and a break before it. A number that
    /// starts at the span's first byte counts only when the span starts the file (otherwise its start may be cut off).
    /// </summary>
    static bool ObjHeaderBefore(ReadOnlySpan<byte> s, int i, bool atFileStart, out int number, out int numberStart)
    {
        number = 0; numberStart = 0;
        int j = i - 1;
        if (j < 0 || !PdfLexer.IsWhite(s[j])) return false;
        while (j >= 0 && PdfLexer.IsWhite(s[j])) j--;
        int genEnd = j;
        while (j >= 0 && s[j] is >= (byte)'0' and <= (byte)'9') j--;
        if (j == genEnd || genEnd - j > 5 || j < 0 || !PdfLexer.IsWhite(s[j])) return false;
        while (j >= 0 && PdfLexer.IsWhite(s[j])) j--;
        int numEnd = j;
        while (j >= 0 && s[j] is >= (byte)'0' and <= (byte)'9') j--;
        if (j == numEnd || numEnd - j > 10) return false;
        if (j < 0 ? !atFileStart : !PdfLexer.IsBreak(s[j])) return false;
        long v = 0;
        for (int k = j + 1; k <= numEnd; k++) v = v * 10 + (s[k] - '0');
        if (v > int.MaxValue) return false;
        number = (int)v;
        numberStart = j + 1;
        return true;
    }

    /// <summary>Object number → (object stream, index) for every object stream found by the rebuild scan.</summary>
    Dictionary<int, (int Stream, int Index)> StmIndex()
    {
        if (_stmIndex != null) return _stmIndex;
        _stmIndex = [];
        foreach (int stm in _stmCandidates ?? [])
        {
            _budget.Check();
            try
            {
                var o = GetObjStm(stm);
                if (o == null) continue;
                for (int i = 0; i < o.Numbers.Length; i++)
                {
                    if (_scanned!.Contains(o.Numbers[i])) continue;
                    // Members past the cap are left out, like objects past the scan's cap.
                    if (_stmIndex.Count >= MaxRebuiltObjects && !_stmIndex.ContainsKey(o.Numbers[i])) { Broken++; return _stmIndex; }
                    _stmIndex[o.Numbers[i]] = (stm, i);
                }
            }
            catch (Exception e) when (IsFormatError(e)) { }
        }
        return _stmIndex;
    }

    // ── Security ───────────────────────────────────────────────────────────────────────────────────────────────

    void SetUpSecurity()
    {
        var encryptRef = Trailer["Encrypt"];
        if (encryptRef == null || encryptRef is PdfNull) return;
        if (encryptRef is PdfRef r) _encryptNumber = r.Number;
        if (Resolve(encryptRef) is not PdfDict encrypt) return;
        byte[]? id = Resolve(Trailer["ID"]) is PdfArray ids && Resolve(ids[0]) is PdfString s ? s.Bytes : null;
        var outcome = PdfCrypt.Open(encrypt, id, out var crypt);
        if (outcome != PdfCrypt.Outcome.Open) { Locked = true; return; }
        Crypt = crypt;
        // Objects and object streams read so far were read without decryption.
        _objects.Clear();
        DropObjStms();
        _stmIndex = null;
    }

    // ── Objects ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>Follows references (at most 32 in a row: a longer chain is broken); null for a missing or broken object.</summary>
    public PdfObject? Resolve(PdfObject? o)
    {
        for (int hop = 0; o is PdfRef r; hop++)
        {
            if (hop >= MaxHops) { Broken++; return null; }
            o = Get(r.Number, r.Generation);
        }
        return o is PdfNull ? null : o;
    }

    /// <summary>The dictionary of an object (a stream's dictionary for a stream), or null.</summary>
    public PdfDict? ResolveDict(PdfObject? o) => Resolve(o) switch { PdfDict d => d, PdfStream s => s.Dict, _ => null };

    /// <summary>A number, resolved; fallback when it isn't one.</summary>
    public double Number(PdfObject? o, double fallback) => Resolve(o) is PdfNumber n && double.IsFinite(n.Value) ? n.Value : fallback;

    /// <summary>An array of numbers (at least count of them), resolved; null when it isn't one.</summary>
    public double[]? Numbers(PdfObject? o, int count)
    {
        if (Resolve(o) is not PdfArray a || a.Count < count) return null;
        var result = new double[a.Count];
        for (int i = 0; i < a.Count; i++)
        {
            if (Resolve(a[i]) is not PdfNumber n || !double.IsFinite(n.Value)) return null;
            result[i] = n.Value;
        }
        return result;
    }

    PdfObject? Get(int number, int generation)
    {
        if (_objects.TryGetValue(number, out var cached)) return cached;
        // An object stream's /Length, /N, /First and /Filter never come from an object stream (ISO 32000-1, 7.5.7):
        // refused, not cached, so a crafted chain of object streams each measured by the next can't nest. A /Length
        // refused this way is found by looking for "endstream" instead.
        if (_inObjStm > 0 && Lookup(number).Type == 2) return null;
        // Loading an object loads others inside it only through object streams; deeper than legitimate files go is cut.
        if (_resolving.Count >= MaxNesting) { Broken++; return null; }
        if (!_resolving.Add(number)) return null; // a reference loop
        try
        {
            var obj = Load(number);
            if (obj == null && Lookup(number).Type != 0)
            {
                // Listed but not there: the cross-reference may be broken (rebuilt once, within the budget).
                if (!_rebuilt)
                {
                    Rebuild();
                    obj = Load(number);
                }
                if (obj == null) Broken++;
            }
            // The cache is bounded by count and by the source bytes its objects were parsed from (huge arrays).
            _cachedSpan += _lastSpan;
            if (_objects.Count >= MaxCachedObjects || _cachedSpan > MaxCachedSpan) { _objects.Clear(); _cachedSpan = _lastSpan; }
            _objects[number] = obj;
            return obj;
        }
        finally { _resolving.Remove(number); }
    }

    const long MaxCachedSpan = 4 << 20;
    long _cachedSpan, _lastSpan;

    PdfObject? Load(int number)
    {
        _lastSpan = 0;
        var entry = Lookup(number);
        try
        {
            return entry.Type switch
            {
                1 => ParseIndirect(entry.Field2, number, decrypt: true)
                     ?? (_header > 0 ? ParseIndirect(entry.Field2 + _header, number, decrypt: true) : null)
                     ?? NearbyIndirect(entry.Field2, number),
                2 => FromObjStm((int)entry.Field2, entry.Field3, number),
                _ => null,
            };
        }
        catch (Exception e) when (IsFormatError(e)) { return null; }
    }

    /// <summary>Looks for "number g obj" within 1 KB around an offset that missed (writers that miscount a little).</summary>
    PdfObject? NearbyIndirect(long offset, int number)
    {
        long from = Math.Max(0, offset - 512);
        long size = Math.Min(1024, Length - from); // computed as a long: an offset past the end gives a negative size
        if (size <= 0) return null;
        Span<byte> window = stackalloc byte[1024];
        int n = ReadSmall(from, window[..(int)size]);
        var head = Encoding.ASCII.GetBytes(number.ToString(System.Globalization.CultureInfo.InvariantCulture) + " ");
        var span = window[..n];
        for (int i = span.IndexOf(head); i >= 0; )
        {
            if (i == 0 || PdfLexer.IsBreak(span[i - 1]))
            {
                var obj = ParseIndirect(from + i, number, decrypt: true);
                if (obj != null) return obj;
            }
            int next = span[(i + 1)..].IndexOf(head);
            i = next < 0 ? -1 : i + 1 + next;
        }
        return null;
    }

    /// <summary>Parses "n g obj … [stream]" at an offset; null when the header isn't there (or isn't number, unless -1).</summary>
    PdfObject? ParseIndirect(long offset, int number, bool decrypt)
    {
        if (offset < 0 || offset >= Length) return null;
        var lx = LexerAt(offset);
        if (!lx.Next() || lx.Kind != PdfToken.Integer) return null;
        long num = lx.Integer;
        if (number >= 0 && num != number) return null;
        if (!lx.Next() || lx.Kind != PdfToken.Integer) return null;
        int gen = (int)Math.Clamp(lx.Integer, 0, 65535);
        if (!lx.Next() || !lx.IsKeyword("obj")) return null;
        if (!lx.Next()) return PdfNull.Instance;
        var obj = PdfParser.Parse(lx, true, _budget, out bool stopped);
        _lastSpan = lx.Position - offset;
        int objNumber = (int)Math.Clamp(num, 0, int.MaxValue);
        if (obj is PdfDict dict)
        {
            if (!stopped) lx.Next();
            if (lx.IsKeyword("stream"))
            {
                // The data starts after the end of line that follows "stream".
                int b = lx.Peek();
                while (b == ' ') { lx.ReadByte(); b = lx.Peek(); }
                if (b == '\r') { lx.ReadByte(); if (lx.Peek() == '\n') lx.ReadByte(); }
                else if (b == '\n') lx.ReadByte();
                var stream = new PdfStream(dict, lx.Position, objNumber, gen);
                if (decrypt && Crypt != null && objNumber != _encryptNumber) DecryptStrings(dict, objNumber, gen);
                return stream;
            }
        }
        if (decrypt && Crypt != null && objNumber != _encryptNumber) DecryptStrings(obj, objNumber, gen);
        return obj;
    }

    void DecryptStrings(PdfObject obj, int number, int generation)
    {
        var crypt = Crypt!;
        PdfParser.ForEachString(obj, s => s.Bytes = crypt.DecryptString(s.Bytes, number, generation));
    }

    PdfObject? FromObjStm(int stream, int index, int number)
    {
        var o = GetObjStm(stream);
        if (o == null) return null;
        int at = index >= 0 && index < o.Numbers.Length && o.Numbers[index] == number ? index : Array.IndexOf(o.Numbers, number);
        if (at < 0) return null;
        int start = o.First + o.Offsets[at];
        if (start < 0 || start >= o.Length) return null;
        var lx = new PdfLexer(o.Data, o.Length) { Position = start };
        if (!lx.Next()) return null;
        var obj = PdfParser.Parse(lx, true, _budget, out _);
        _lastSpan = lx.Position - start;
        return obj;
    }

    ObjStm? GetObjStm(int number)
    {
        for (var node = _objStms.First; node != null; node = node.Next)
            if (node.Value.Number == number)
            {
                _objStms.Remove(node);
                _objStms.AddFirst(node);
                return node.Value;
            }
        PdfBytes bytes;
        int n, first;
        _inObjStm++;
        try
        {
            if (Resolve(new PdfRef(number, 0)) is not PdfStream s) return null;
            n = (int)Math.Clamp(Number(s.Dict["N"], 0), 0, MaxObjStmObjects);
            first = (int)Math.Clamp(Number(s.Dict["First"], 0), 0, int.MaxValue);
            if (n == 0) return null;
            if (ReadStream(s, StreamCap, decrypt: true) is not { } data) return null;
            bytes = data;
        }
        finally { _inObjStm--; }
        if (first > bytes.Length) { bytes.Release(); return null; }
        var lx = new PdfLexer(bytes.Array, Math.Min(first, bytes.Length));
        var numbers = new List<int>();
        var offsets = new List<int>();
        while (numbers.Count < n && lx.Next() && lx.Kind == PdfToken.Integer)
        {
            long num = lx.Integer;
            if (!lx.Next() || lx.Kind != PdfToken.Integer) break;
            numbers.Add((int)Math.Clamp(num, 0, int.MaxValue));
            offsets.Add((int)Math.Clamp(lx.Integer, 0, int.MaxValue));
        }
        var result = new ObjStm(number, bytes, first, [.. numbers], [.. offsets]);
        _objStms.AddFirst(result);
        _objStmBytes += bytes.Length;
        // Keep at most 4 MB decoded, but always the one just read. Nothing reads an evicted one any more: objects
        // are parsed out of it right after it is found, with no other object stream read in between.
        while (_objStmBytes > ObjStmCacheBytes && _objStms.Count > 1)
        {
            var oldest = _objStms.Last!.Value;
            _objStmBytes -= oldest.Length;
            _objStms.RemoveLast();
            oldest.Bytes.Release();
        }
        return result;
    }

    // ── Streams ────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A stream's data, decrypted and decoded, at most cap bytes (Truncated when cut, broken or ended early); null when
    /// a filter isn't supported (images) or nothing could be read. Never trusts /Length: it is checked against
    /// "endstream", and a missing or wrong length is found by looking for "endstream" (within cap bytes). The bytes
    /// are pooled: the caller gives them back with <see cref="PdfBytes.Release"/> once it has read them.
    /// </summary>
    public PdfBytes? ReadStream(PdfStream stream, int cap, bool decrypt = true)
    {
        var dict = stream.Dict;
        var filter = ResolveFilter(dict["Filter"]);
        var parms = ResolveFilter(dict["DecodeParms"] ?? dict["DP"]);
        var raw = RawData(stream, cap);
        if (raw.Length == 0 && !raw.Truncated) { raw.Release(); return PdfBytes.Empty; }
        if (decrypt && Crypt != null && stream.Number != _encryptNumber && !IsUnencrypted(dict, filter, raw))
        {
            var plain = Crypt.DecryptStream(raw.Span, stream.Number, stream.Generation);
            bool cut = raw.Truncated;
            raw.Release();
            raw = plain with { Truncated = cut };
            _budget.Decoded(plain.Length);
        }
        return PdfFilters.Decode(raw, filter, parms, cap, _budget);
    }

    bool IsUnencrypted(PdfDict dict, PdfObject? filter, PdfBytes raw)
    {
        if (dict.Name("Type") == "XRef") return true;
        // A stream-level Identity crypt filter.
        if (filter is PdfArray a && a[0] is PdfName { Value: "Crypt" }) return true;
        if (filter is PdfName { Value: "Crypt" }) return true;
        if (dict.Name("Type") == "Metadata")
        {
            if (!Crypt!.EncryptMetadata) return true;
            // Some writers leave the XMP in the clear anyway.
            var head = raw.Span[..Math.Min(raw.Length, 64)];
            if (head.IndexOf("<?xpacket"u8) >= 0 || head.IndexOf("<x:xmpmeta"u8) >= 0) return true;
        }
        return false;
    }

    PdfObject? ResolveFilter(PdfObject? o)
    {
        var value = Resolve(o);
        if (value is not PdfArray a) return value;
        var copy = new PdfArray();
        foreach (var item in a.Items) copy.Items.Add(Resolve(item) ?? PdfNull.Instance);
        return copy;
    }

    /// <summary>
    /// The stream's raw bytes in a pooled buffer, at most cap of them; Truncated when the stream is longer than cap,
    /// or its end couldn't be found (no "endstream", and a /Length missing or past the end of the file: a cut file).
    /// </summary>
    PdfBytes RawData(PdfStream stream, int cap)
    {
        long start = stream.DataOffset;
        long available = Length - start;
        if (available <= 0) return new PdfBytes([], 0, Truncated: true); // the file ends where the data should start
        long declared =Resolve(stream.Dict["Length"]) is PdfNumber n && n.Value >= 0 && double.IsFinite(n.Value) ? (long)n.Value : -1;
        long length;
        bool ended = true;
        if (declared >= 0 && declared <= available && EndstreamAt(start + declared)) length = declared;
        else
        {
            length = FindEndstream(start, Math.Min(available, (long)cap + 64));
            if (length < 0)
            {
                // No "endstream" within reach: a /Length that fits in the file is believed; otherwise the data runs on
                // past what is read, or the file ends before the stream does.
                ended = declared >= 0 && declared <= available;
                length = declared >= 0 ? Math.Min(declared, available) : Math.Min(available, cap);
            }
        }
        bool truncated = length > cap || !ended;
        int take = (int)Math.Min(length, cap);
        var data = PdfPool.Rent(take);
        int got = 0;
        if (take < 16 << 10) got = ReadSmall(start, data.AsSpan(0, take)); // small streams share blocks with their dictionaries
        else
            while (got < take)
            {
                int r = _source.ReadAt(start + got, data.AsSpan(got, take - got));
                if (r <= 0) break;
                got += r;
            }
        return new PdfBytes(data, got, truncated || got < take, Pooled: true);
    }

    /// <summary>"endstream" (after optional white space) at an offset.</summary>
    bool EndstreamAt(long offset)
    {
        Span<byte> probe = stackalloc byte[24];
        int n = ReadSmall(offset, probe);
        int i = 0;
        while (i < n && PdfLexer.IsWhite(probe[i])) i++;
        return probe[i..n].StartsWith("endstream"u8);
    }

    /// <summary>The data length up to "endstream" (its end of line dropped), searched within max bytes; -1 when not found.</summary>
    long FindEndstream(long start, long max)
    {
        const int Chunk = 64 << 10;
        int size = (int)Math.Min(Chunk + 16, Math.Max(16, max));
        var buffer = PdfPool.Rent(size);
        try
        {
            long pos = 0;
            while (pos < max)
            {
                _budget.Check();
                int want = (int)Math.Min(size, max - pos);
                int n = 0;
                while (n < want)
                {
                    int got = want - n < BlockSize ? ReadSmall(start + pos + n, buffer.AsSpan(n, want - n)) : _source.ReadAt(start + pos + n, buffer.AsSpan(n, want - n));
                    if (got <= 0) break;
                    n += got;
                }
                if (n == 0) break;
                int at = buffer.AsSpan(0, n).IndexOf("endstream"u8);
                if (at >= 0)
                {
                    long end = pos + at;
                    if (end > 0 && at > 0 && buffer[at - 1] == '\n') { end--; if (at > 1 && buffer[at - 2] == '\r') end--; }
                    else if (end > 0 && at > 0 && buffer[at - 1] == '\r') end--;
                    return end;
                }
                if (n < want || pos + n >= max) break;
                pos += Math.Max(1, n - 16); // overlap, so "endstream" across two windows is found
            }
            return -1;
        }
        finally { PdfPool.Return(buffer); }
    }

    // ── Pages ──────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>The page count the tree's root declares (0 when absent or implausible).</summary>
    public int DeclaredPageCount => ResolveDict(Catalog?["Pages"]) is { } root && Number(root["Count"], 0) is var c && c is > 0 and < 10_000_000 ? (int)c : 0;

    sealed class Node(PdfDict dict, PdfDict? resources, double[]? mediaBox, double[]? cropBox, int rotate)
    {
        public readonly PdfDict Dict = dict;
        public readonly PdfDict? Resources = resources;
        public readonly double[]? MediaBox = mediaBox, CropBox = cropBox;
        public readonly int Rotate = rotate;
        public int Next;
    }

    /// <summary>
    /// The leaf pages in order, read iteratively (depth at most 64, every node visited once), with Resources, MediaBox,
    /// CropBox and Rotate inherited. Broken nodes are skipped.
    /// </summary>
    public IEnumerable<PdfPage> Pages()
    {
        var root = Resolve(Catalog?["Pages"]) as PdfDict;
        if (root == null) yield break;
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        var seenRefs = new HashSet<int>();
        var stack = new List<Node>();
        int index = 0;
        if (Catalog?["Pages"] is PdfRef rootRef) seenRefs.Add(rootRef.Number);
        seen.Add(root);
        var first = NodeOf(root, null);
        if (IsLeaf(root)) { yield return Leaf(first, index); yield break; }
        stack.Add(first);
        while (stack.Count > 0)
        {
            _budget.Check();
            var top = stack[^1];
            var kids = Resolve(top.Dict["Kids"]) as PdfArray;
            if (kids == null || top.Next >= kids.Count) { stack.RemoveAt(stack.Count - 1); continue; }
            var kidRef = kids[top.Next++];
            if (kidRef is PdfRef r && !seenRefs.Add(r.Number)) continue;
            PdfDict? kid;
            try { kid = Resolve(kidRef) as PdfDict; }
            catch (Exception e) when (IsFormatError(e)) { Broken++; continue; }
            if (kid == null) { Broken++; continue; }
            if (!seen.Add(kid)) continue;
            var node = NodeOf(kid, top);
            if (IsLeaf(kid)) yield return Leaf(node, index++);
            else if (stack.Count < 64) stack.Add(node);
            else Broken++; // deeper than 64: its pages are left out
        }
    }

    static bool IsLeaf(PdfDict d) => d.Name("Type") switch
    {
        "Page" => true,
        "Pages" => false,
        _ => d["Kids"] == null,
    };

    Node NodeOf(PdfDict d, Node? parent)
    {
        var resources = d["Resources"] != null ? ResolveDict(d["Resources"]) : parent?.Resources;
        var media = Box(d["MediaBox"]) ?? parent?.MediaBox;
        var crop = Box(d["CropBox"]) ?? parent?.CropBox;
        int rotate = d["Rotate"] != null ? (int)Number(d["Rotate"], 0) : parent?.Rotate ?? 0;
        return new Node(d, resources, media, crop, rotate);
    }

    static PdfPage Leaf(Node n, int index)
        => new(index, n.Dict, n.Resources, n.MediaBox ?? [0, 0, 612, 792], n.CropBox, ((n.Rotate % 360) + 360) % 360 / 90 * 90);

    double[]? Box(PdfObject? o)
    {
        var b = Numbers(o, 4);
        if (b == null) return null;
        double x0 = Math.Min(b[0], b[2]), x1 = Math.Max(b[0], b[2]), y0 = Math.Min(b[1], b[3]), y1 = Math.Max(b[1], b[3]);
        return x1 - x0 > 0 && y1 - y0 > 0 ? [x0, y0, x1, y1] : null;
    }

    /// <summary>The page with a 0-based index (the tree is walked: a wrong /Count can't give the wrong page).</summary>
    public PdfPage? PageAt(int index)
    {
        if (index < 0) return null;
        foreach (var page in Pages())
            if (page.Index == index) return page;
        return null;
    }
}
