using System.Buffers.Binary;
using System.IO.Compression;
using System.Runtime.ExceptionServices;
using System.Text;
using System.Xml;

namespace Anything.Core.Content.Extract;

/// <summary>
/// Checks a zip's end of central directory before ZipArchive reads it: ZipArchive builds every central-directory entry on
/// first use, so a crafted directory would spend its memory before any count is seen. Reads the end record (and the Zip64
/// locator and record) in our own code, choosing the record exactly as ZipArchive does, then walks the headers ZipArchive
/// will build. Refuses more than 10,000 entries or a directory over 4 MB (TooBig), and a directory that doesn't lie inside
/// the file, whose headers don't follow each other or that holds more headers than it declares (Damaged).
/// </summary>
internal static class ZipGuard
{
    /// <summary>Most entries in a zip that is read.</summary>
    public const int MaxEntries = 10_000;
    /// <summary>Largest central directory read (headers, names, extra fields and comments).</summary>
    public const long MaxDirectoryBytes = 4L << 20;

    const uint EndSignature = 0x06054B50, Zip64LocatorSignature = 0x07064B50, Zip64EndSignature = 0x06064B50;
    const uint HeaderSignature = 0x02014B50;
    const int EndLength = 22, MaxComment = 0xFFFF, HeaderLength = 46;

    /// <summary>Null when the zip may be opened; TooBig or Damaged otherwise.</summary>
    public static DocStatus? Check(IByteSource source) => Check(source, out _);

    /// <summary><see cref="Check(IByteSource)"/>, with the number of entries ZipArchive will build.</summary>
    public static DocStatus? Check(IByteSource source, out long entries)
    {
        entries = 0;
        long length = source.Length;
        if (length < EndLength) return DocStatus.Damaged;
        long end = FindEnd(source, Math.Min(length, 1024)) ?? FindEnd(source, Math.Min(length, EndLength + MaxComment)) ?? -1;
        if (end < 0) return DocStatus.Damaged;
        Span<byte> e = stackalloc byte[EndLength];
        if (!source.TryRead(end, e)) return DocStatus.Damaged;
        int disk = BinaryPrimitives.ReadUInt16LittleEndian(e[4..]);
        int directoryDisk = BinaryPrimitives.ReadUInt16LittleEndian(e[6..]);
        long onDisk = BinaryPrimitives.ReadUInt16LittleEndian(e[8..]);
        long total = BinaryPrimitives.ReadUInt16LittleEndian(e[10..]);
        long size = BinaryPrimitives.ReadUInt32LittleEndian(e[12..]);
        long offset = BinaryPrimitives.ReadUInt32LittleEndian(e[16..]);
        if (onDisk != total) return DocStatus.Damaged; // a split archive: ZipArchive refuses it too
        // The count ZipArchive will build, and the largest count any record states (both must pass).
        long expected = total, declared = total;
        bool sizeKnown = size != 0xFFFFFFFF;
        long limit = end;
        // ZipArchive follows the Zip64 records only when the disk, the offset or the count is masked (a masked size alone
        // sends it to the end record's own offset and count), so the guard looks at the same directory.
        if (disk == 0xFFFF || offset == 0xFFFFFFFF || total == 0xFFFF)
        {
            // Zip64: the locator sits right before the end record and points at the Zip64 end record.
            Span<byte> locator = stackalloc byte[20];
            if (end < 20 || !source.TryRead(end - 20, locator) || BinaryPrimitives.ReadUInt32LittleEndian(locator) != Zip64LocatorSignature)
                return DocStatus.Damaged;
            long at = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(locator[8..]), long.MaxValue);
            Span<byte> z = stackalloc byte[56];
            if (at < 0 || at > end - 20 - 56 || !source.TryRead(at, z) || BinaryPrimitives.ReadUInt32LittleEndian(z) != Zip64EndSignature)
                return DocStatus.Damaged;
            disk = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(z[16..]), int.MaxValue);
            directoryDisk = (int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(z[20..]), int.MaxValue);
            long onDisk64 = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(z[24..]), long.MaxValue);
            expected = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(z[32..]), long.MaxValue);
            if (onDisk64 != expected) return DocStatus.Damaged;
            declared = total == 0xFFFF ? expected : Math.Max(total, expected);
            size = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(z[40..]), long.MaxValue);
            sizeKnown = true;
            offset = (long)Math.Min(BinaryPrimitives.ReadUInt64LittleEndian(z[48..]), long.MaxValue);
            limit = at;
        }
        entries = expected;
        if (disk != 0 || directoryDisk != 0) return DocStatus.Damaged; // split archives aren't Office files
        if (declared > MaxEntries || (sizeKnown && size > MaxDirectoryBytes)) return DocStatus.TooBig;
        if (offset < 0 || offset > limit || (sizeKnown && (size < 0 || size > limit - offset))) return DocStatus.Damaged;
        if (sizeKnown && expected * HeaderLength > size) return DocStatus.Damaged; // each directory entry takes at least 46 bytes
        return Walk(source, offset, expected, limit);
    }

    /// <summary>
    /// Walks the headers ZipArchive will build, from the directory's offset: each must follow the last, the walk must stay
    /// within 4 MB and before the end records, and no further header may follow the last one counted (ZipArchive would
    /// build those too before it finds the count wrong). So the names ZipArchive keeps never add up to more than 4 MB.
    /// </summary>
    static DocStatus? Walk(IByteSource source, long offset, long count, long limit)
    {
        Span<byte> h = stackalloc byte[HeaderLength];
        long pos = offset;
        for (long i = 0; i < count; i++)
        {
            if (!source.TryRead(pos, h) || BinaryPrimitives.ReadUInt32LittleEndian(h) != HeaderSignature) return DocStatus.Damaged;
            pos += HeaderLength + BinaryPrimitives.ReadUInt16LittleEndian(h[28..]) + BinaryPrimitives.ReadUInt16LittleEndian(h[30..])
                + BinaryPrimitives.ReadUInt16LittleEndian(h[32..]);
            if (pos - offset > MaxDirectoryBytes) return DocStatus.TooBig;
            if (pos > limit) return DocStatus.Damaged;
        }
        return source.TryRead(pos, h[..4]) && BinaryPrimitives.ReadUInt32LittleEndian(h) == HeaderSignature ? DocStatus.Damaged : null;
    }

    /// <summary>The end record's offset within the last <paramref name="window"/> bytes, searching backwards; null when none.</summary>
    static long? FindEnd(IByteSource source, long window)
    {
        long start = source.Length - window;
        var tail = source.ReadBytes(start, window, EndLength + MaxComment);
        if (tail == null) return null;
        for (int i = tail.Length - EndLength; i >= 0; i--)
        {
            if (BinaryPrimitives.ReadUInt32LittleEndian(tail.AsSpan(i)) != EndSignature) continue;
            int comment = BinaryPrimitives.ReadUInt16LittleEndian(tail.AsSpan(i + 20));
            if (i + EndLength + comment <= tail.Length) return start + i;
        }
        return null;
    }
}

/// <summary>
/// A checked zip opened through the byte source (only the central directory and the entries asked for are read), with
/// entries looked up by name case-insensitively and opened through a capped stream.
/// </summary>
internal sealed class OfficeZip : IDisposable
{
    readonly ZipArchive _archive;
    readonly Dictionary<string, ZipArchiveEntry> _byName = new(StringComparer.OrdinalIgnoreCase);

    OfficeZip(ZipArchive archive)
    {
        _archive = archive;
        foreach (var e in archive.Entries) _byName.TryAdd(Normalize(e.FullName), e);
    }

    /// <summary>
    /// Opens a zip that passed <see cref="ZipGuard"/>; a status when it didn't (or ZipArchive refused it). A file I/O error
    /// ZipArchive wrapped in an InvalidDataException goes on up as the I/O error, never Damaged.
    /// </summary>
    public static DocStatus? TryOpen(IByteSource source, out OfficeZip? zip)
    {
        zip = null;
        if (ZipGuard.Check(source) is { } refused) return refused;
        source.PreferExactRanges(); // the directory and local headers are small structures far apart
        var stream = new ByteSourceStream(source);
        try { zip = new OfficeZip(new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true)); }
        catch (InvalidDataException e)
        {
            IoErrors.ThrowIfWrapped(e);
            return DocStatus.Damaged;
        }
        // Entry data is read as streams: back to blocks.
        if (source is FileByteSource file) file.ExactRanges = false;
        return null;
    }

    /// <summary>Every entry.</summary>
    public IReadOnlyCollection<ZipArchiveEntry> Entries => _byName.Values;

    /// <summary>The entry with this name (any case, either slash, no leading slash); null when there is none.</summary>
    public ZipArchiveEntry? Entry(string name) => _byName.TryGetValue(Normalize(name), out var e) ? e : null;

    /// <summary>
    /// An entry's inflated bytes as a stream that charges the budget and stops the entry (ContentLimitException.Stream)
    /// past MaxStreamBytes, or past 1 MB at more than 100 times its compressed size (a zip bomb); null when it can't be opened.
    /// </summary>
    public static Stream? Open(ZipArchiveEntry entry, ExtractBudget budget)
    {
        try { return new CappedStream(entry.Open(), entry.CompressedLength, budget); }
        catch (Exception e) when (e is InvalidDataException or NotSupportedException or IOException and not ContentIoException)
        {
            IoErrors.ThrowIfWrapped(e);
            return null;
        }
    }

    /// <summary>A part's path resolved against the part it is relative to ("xl/workbook.xml" + "worksheets/sheet1.xml").</summary>
    public static string Resolve(string fromPart, string target)
    {
        target = target.Replace('\\', '/');
        if (target.StartsWith('/')) return target.TrimStart('/');
        int slash = fromPart.LastIndexOf('/');
        var parts = new List<string>(slash < 0 ? [] : fromPart[..slash].Split('/'));
        foreach (var p in target.Split('/'))
        {
            if (p is "" or ".") continue;
            if (p == "..") { if (parts.Count > 0) parts.RemoveAt(parts.Count - 1); continue; }
            parts.Add(p);
        }
        return string.Join('/', parts);
    }

    static string Normalize(string name) => Uri.UnescapeDataString(name.Replace('\\', '/').TrimStart('/'));

    public void Dispose() => _archive.Dispose();
}

/// <summary>Counts an entry's inflated bytes: charges the budget and stops the entry at its caps.</summary>
internal sealed class CappedStream(Stream inner, long compressed, ExtractBudget budget) : Stream
{
    /// <summary>Above this, an entry may not inflate to more than 100 times its compressed size.</summary>
    public const long RatioFloor = 1 << 20;
    public const int MaxRatio = 100;

    long _total;

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => _total; set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        budget.Check();
        int n = inner.Read(buffer);
        if (n <= 0) return n;
        _total += n;
        budget.Decoded(n);
        if (_total > budget.Limits.MaxStreamBytes || (_total > RatioFloor && _total > MaxRatio * Math.Max(1, compressed)))
            throw new ContentLimitException(ContentLimitException.Stream);
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>File I/O errors that a BCL reader (ZipArchive, an inflater) wrapped in a format exception.</summary>
internal static class IoErrors
{
    /// <summary>The file I/O error inside an exception's inner exceptions; null when there is none.</summary>
    public static ContentIoException? Wrapped(Exception e)
    {
        for (var x = e.InnerException; x != null; x = x.InnerException)
            if (x is ContentIoException io) return io;
        return null;
    }

    /// <summary>
    /// Rethrows the file I/O error a format exception wraps (ZipArchive turns IOExceptions from the stream into
    /// InvalidDataException), so it reaches the file source, which classifies it, instead of becoming Damaged.
    /// </summary>
    public static void ThrowIfWrapped(Exception e)
    {
        if (Wrapped(e) is { } io) ExceptionDispatchInfo.Throw(io);
    }
}

/// <summary>
/// The shape of XML bytes as they pass, checked before XmlReader parses them: element nesting (at most
/// <see cref="OfficeXml.MaxDepth"/>), the attributes of one tag (at most 4,096) and its length (at most 4 M characters).
/// XmlReader keeps an object per open element and parses every attribute of a tag before it returns it (at a cost that
/// grows faster than their number), so a deeply nested or many-attributed part would cost hundreds of MB and seconds
/// inside it whatever the walker above checks (Skip, ReadText and plain Read loops included); past a limit this throws
/// InvalidDataException, which skips the part. One long value is fine: Word keeps a base64 copy of each VML shape in
/// o:gfxdata, hundreds of KB in one attribute. Comments, CDATA, processing instructions and quoted attribute values are
/// followed, so the '&lt;', '&gt;' and '/' inside them don't count. Bytes are read as 8-bit units (UTF-8 and other
/// ASCII-compatible encodings), or as UTF-16 in either byte order (a byte order mark, or a zero byte beside the first
/// character).
/// </summary>
internal sealed class XmlShape
{
    /// <summary>Longest tag, '&lt;' to '&gt;', in characters.</summary>
    public const int MaxTagChars = 4 << 20;
    /// <summary>Most attributes (namespace declarations included) in one tag; real Office tags have tens.</summary>
    public const int MaxAttributes = 4096;

    enum State : byte { Text, Open, Bang, Tag, Quote, Declaration, DeclarationQuote, Comment, CData, Instruction }

    int _unitSize; // 0 = not decided yet, 1 = bytes, 2 = UTF-16
    bool _bigEndian;
    int _carry = -1; // a byte waiting for the next: the first byte while undecided, or half a UTF-16 unit
    State _state;
    char _quote, _previous;
    bool _endTag;
    int _run, _depth, _attributes;
    long _tagChars;

    /// <summary>Whether a whole document (an XMP packet) stays within the limits.</summary>
    public static bool Allows(ReadOnlySpan<byte> xml)
    {
        try { new XmlShape().Feed(xml); return true; }
        catch (InvalidDataException) { return false; }
    }

    /// <summary>
    /// Xmp.Read for a packet held as bytes (the readers feed it up to 16 MB; Xmp checks its shape as it parses); false when
    /// the packet was refused part-way.
    /// </summary>
    public static bool ReadXmp(ReadOnlyMemory<byte> packet, ExtractSink sink) => Xmp.Read(packet, sink);

    /// <summary>Checks the next bytes of the document; InvalidDataException past a limit.</summary>
    public void Feed(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        if (_unitSize == 0)
        {
            if (_carry < 0 && bytes.Length == 1) { _carry = bytes[0]; return; }
            byte b0 = _carry >= 0 ? (byte)_carry : bytes[0], b1 = _carry >= 0 ? bytes[0] : bytes[1];
            if ((b0 == 0xFF && b1 == 0xFE) || (b0 != 0 && b1 == 0)) _unitSize = 2;
            else if ((b0 == 0xFE && b1 == 0xFF) || (b0 == 0 && b1 != 0)) { _unitSize = 2; _bigEndian = true; }
            else _unitSize = 1;
            if (_unitSize == 1 && _carry >= 0)
            {
                Unit((char)_carry);
                _carry = -1;
            }
        }
        if (_unitSize == 1) { Bytes(bytes); return; }
        int i = 0;
        if (_carry >= 0)
        {
            Unit(Utf16((byte)_carry, bytes[0]));
            _carry = -1;
            i = 1;
        }
        for (; i + 1 < bytes.Length; i += 2) Unit(Utf16(bytes[i], bytes[i + 1]));
        if (i < bytes.Length) _carry = bytes[i];
    }

    char Utf16(byte first, byte second) => (char)(_bigEndian ? first << 8 | second : second << 8 | first);

    /// <summary>8-bit units, skipping quickly over text and over the inside of tags and quoted values.</summary>
    void Bytes(ReadOnlySpan<byte> bytes)
    {
        int i = 0;
        while (i < bytes.Length)
        {
            var rest = bytes[i..];
            int stop;
            switch (_state)
            {
                case State.Text:
                    stop = rest.IndexOf((byte)'<');
                    if (stop < 0) return;
                    break;
                case State.Tag or State.Declaration:
                    stop = rest.IndexOfAny((byte)'"', (byte)'\'', (byte)'>');
                    int skipped = stop < 0 ? rest.Length : stop;
                    if (skipped > 0)
                    {
                        Count(skipped);
                        _previous = (char)rest[skipped - 1];
                    }
                    if (stop < 0) return;
                    break;
                case State.Quote or State.DeclarationQuote:
                    stop = rest.IndexOf((byte)_quote);
                    Count(stop < 0 ? rest.Length : stop);
                    if (stop < 0) return;
                    break;
                default:
                    stop = 0;
                    break;
            }
            i += stop;
            Unit((char)bytes[i++]);
        }
    }

    void Unit(char u)
    {
        switch (_state)
        {
            case State.Text:
                if (u == '<') { _state = State.Open; _tagChars = 1; _attributes = 0; }
                return;
            case State.Open:
                if (u == '!') { _state = State.Bang; return; }
                if (u == '?') { _state = State.Instruction; _run = 0; return; }
                _state = State.Tag;
                _endTag = u == '/';
                _previous = '<';
                if (_endTag) { Count(1); return; }
                TagUnit(u, declaration: false);
                return;
            case State.Bang:
                _run = 0;
                _state = u == '-' ? State.Comment : u == '[' ? State.CData : State.Declaration;
                if (_state == State.Declaration) TagUnit(u, declaration: true);
                return;
            case State.Tag:
                TagUnit(u, declaration: false);
                return;
            case State.Declaration:
                TagUnit(u, declaration: true);
                return;
            case State.Quote or State.DeclarationQuote:
                Count(1);
                if (u == _quote)
                {
                    _state = _state == State.Quote ? State.Tag : State.Declaration;
                    _previous = u;
                }
                return;
            case State.Comment:
                if (u == '-') _run++;
                else if (u == '>' && _run >= 2) _state = State.Text;
                else _run = 0;
                return;
            case State.CData:
                if (u == ']') _run++;
                else if (u == '>' && _run >= 2) _state = State.Text;
                else _run = 0;
                return;
            case State.Instruction:
                if (u == '>' && _run == 1) _state = State.Text;
                else _run = u == '?' ? 1 : 0;
                return;
        }
    }

    /// <summary>
    /// One character inside a tag: a quote opens a value (one per attribute: XML values are always quoted), '&gt;' ends
    /// the tag (an element opens or closes).
    /// </summary>
    void TagUnit(char u, bool declaration)
    {
        Count(1);
        if (u is '"' or '\'')
        {
            if (!declaration && ++_attributes > MaxAttributes) throw new InvalidDataException("An XML tag has too many attributes.");
            _quote = u;
            _state = declaration ? State.DeclarationQuote : State.Quote;
            return;
        }
        if (u != '>') { _previous = u; return; }
        _state = State.Text;
        if (declaration) return;
        if (_endTag) { if (_depth > 0) _depth--; }
        else if (_previous != '/' && ++_depth > OfficeXml.MaxDepth + 1) throw new InvalidDataException("The XML is nested too deep.");
    }

    void Count(long chars)
    {
        _tagChars += chars;
        if (_tagChars > MaxTagChars) throw new InvalidDataException("An XML tag is too long.");
    }
}

/// <summary>A read-only stream that passes what it reads through an <see cref="XmlShape"/> before XmlReader sees it.</summary>
internal sealed class XmlShapeStream(Stream inner) : Stream
{
    readonly XmlShape _shape = new();

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }

    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int n = inner.Read(buffer);
        if (n > 0) _shape.Feed(buffer[..n]);
        return n;
    }

    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// The name table of one XmlReader, which keeps every distinct element, attribute and prefix name for the reader's life:
/// past 16,384 names or 1 M characters of names a new one throws InvalidDataException, which skips the part. Real Office
/// parts and XMP packets use a few hundred; a crafted 16 MB part of distinct names would otherwise hold well over 100 MB.
/// </summary>
internal sealed class CappedNameTable : NameTable
{
    /// <summary>Most distinct names.</summary>
    public const int MaxNames = 16_384;
    /// <summary>Most characters of all distinct names together.</summary>
    public const int MaxNameChars = 1 << 20;

    int _names;
    long _chars;

    /// <inheritdoc/>
    public override string Add(char[] key, int start, int len)
    {
        if (Get(key, start, len) is { } known) return known;
        Charge(len);
        return base.Add(key, start, len);
    }

    /// <inheritdoc/>
    public override string Add(string key)
    {
        if (Get(key) is { } known) return known;
        Charge(key.Length);
        return base.Add(key);
    }

    /// <summary>Counts a new name of len characters; throws past the caps.</summary>
    void Charge(int len)
    {
        _chars += len;
        if (++_names > MaxNames || _chars > MaxNameChars) throw new InvalidDataException("The XML has too many distinct names.");
    }
}

/// <summary>XML of Office parts read safely: XmlReader only, DTDs prohibited, nothing resolved, text read in chunks.</summary>
internal static class OfficeXml
{
    /// <summary>Deepest element nesting walked.</summary>
    public const int MaxDepth = 256;

    /// <summary>
    /// A reader over a part, DTDs prohibited, no resolver, comments and processing instructions ignored; the bytes pass an
    /// <see cref="XmlShape"/> first, so nesting and tag length are capped in every walk over the part, and its names are
    /// capped by a <see cref="CappedNameTable"/>.
    /// </summary>
    public static XmlReader Create(Stream stream) => XmlReader.Create(new XmlShapeStream(stream), new XmlReaderSettings
    {
        DtdProcessing = DtdProcessing.Prohibit,
        XmlResolver = null,
        MaxCharactersInDocument = 64L << 20,
        IgnoreComments = true,
        IgnoreProcessingInstructions = true,
        CheckCharacters = false,
        CloseInput = true,
        NameTable = new CappedNameTable(),
    });

    /// <summary>Text, CDATA and whitespace nodes: the ones whose characters are content inside a text element.</summary>
    public static bool IsText(XmlNodeType type) => type is XmlNodeType.Text or XmlNodeType.CDATA or XmlNodeType.Whitespace or XmlNodeType.SignificantWhitespace;

    /// <summary>
    /// Reads the text inside the current element (the reader is on its start tag) into a callback in chunks, child
    /// elements ignored; leaves the reader on the element's end tag (or where the element was empty).
    /// </summary>
    public static void ReadText(XmlReader r, char[] chunk, Action<ReadOnlySpan<char>> into)
    {
        if (r.IsEmptyElement) return;
        int depth = r.Depth;
        while (r.Read())
        {
            if (r.NodeType == XmlNodeType.EndElement && r.Depth == depth) return;
            if (!IsText(r.NodeType)) continue;
            if (!r.CanReadValueChunk) { into(r.Value); continue; }
            int n;
            while ((n = r.ReadValueChunk(chunk, 0, chunk.Length)) > 0) into(chunk.AsSpan(0, n));
        }
    }

    /// <summary>The text inside the current element, at most max characters, trimmed.</summary>
    public static string ElementText(XmlReader r, int max)
    {
        var sb = new StringBuilder();
        var chunk = new char[1024];
        ReadText(r, chunk, s => { if (sb.Length < max) sb.Append(s[..Math.Min(s.Length, max - sb.Length)]); });
        return sb.ToString().Trim();
    }

    /// <summary>Decodes Excel's _xHHHH_ escapes (_x000D_ for a carriage return); _x005F_ escapes a literal underscore.</summary>
    public static string DecodeEscapes(string s)
    {
        int at = s.IndexOf("_x", StringComparison.Ordinal);
        if (at < 0) return s;
        var sb = new StringBuilder(s.Length);
        int i = 0;
        while (i < s.Length)
        {
            if (i + 6 < s.Length && s[i] == '_' && s[i + 1] == 'x' && s[i + 6] == '_' && IsHex(s.AsSpan(i + 2, 4)))
            {
                sb.Append((char)Convert.ToInt32(s.Substring(i + 2, 4), 16));
                i += 7;
                continue;
            }
            sb.Append(s[i++]);
        }
        return sb.ToString();
    }

    static bool IsHex(ReadOnlySpan<char> s)
    {
        foreach (char c in s) if (!char.IsAsciiHexDigit(c)) return false;
        return true;
    }

    /// <summary>Whether a namespace is markup compatibility (mc:AlternateContent and friends).</summary>
    public static bool IsMarkupCompatibility(string ns) => ns == "http://schemas.openxmlformats.org/markup-compatibility/2006";
}
