using System.Buffers.Binary;
using Anything.Core.Content.Extract.Office;

namespace Anything.Core.Content.Extract;

/// <summary>
/// Word, Excel and PowerPoint 97-2003 (.doc, .dot, .xls, .xlt, .ppt, .pps, .pot) for the text index, through the readers
/// the preview pane uses (LegacyWord, LegacyExcel, LegacyPowerPoint), run on this reader's source and budget. Word: the
/// main text, then text boxes, footnotes and endnotes as LegacyWord gives them. Excel: every sheet in workbook order as its
/// own page starting with its name, cells separated by tabs, as OoxmlReader writes xlsx. PowerPoint: every slide in
/// presentation order, one page each. The compound file's streams decide the format, the extension only which is tried
/// first. A file that isn't a compound file is read by what it starts with: RTF as RTF, a web page (a report exported
/// as .xls) as HTML with its tags left out, anything else (a tab-separated export, a text .doc) as a user's extra text
/// extension is, by PlainTextReader. An encrypted one is PasswordProtected; a broken one Damaged. The limits are
/// OoxmlReader's: the sink's text and page limits, the budget's time and bytes read, and its cancellation, which the
/// readers' parses get as a token.
/// </summary>
/// <param name="beforeParse">Tests: called with the token a reader's parse gets, just before the parse.</param>
internal sealed class LegacyOfficeReader(Action<CancellationToken>? beforeParse = null) : IContentReader
{
    /// <summary>Excel 97-2003's own row and column limits: no cell lies beyond them.</summary>
    internal const int MaxRows = 65_536, MaxColumns = 256;

    const int MaxCurrentUserBytes = 64 << 10;

    public ContentKind Kind => ContentKind.Office;
    /// <summary>2: above PlainTextReader's 1, so one of these files a user once listed as an extra text extension is read again (ExtractorChanges).</summary>
    public byte Version => 2;
    public bool Handles(ReadOnlySpan<char> extension) => extension is "doc" or "dot" or "xls" or "xlt" or "ppt" or "pps" or "pot";

    enum Format { Word, Excel, PowerPoint }

    public DocStatus Read(IByteSource source, ReadOnlySpan<char> extension, ExtractSink sink, ExtractBudget budget)
    {
        if (!CompoundFile.IsCompoundFile(source)) return NotCompound(source, extension, sink, budget);
        var cfb = CompoundFile.Open(source, budget);
        if (cfb.HasEncryptedPackage()) return DocStatus.PasswordProtected;
        Format first = extension is "xls" or "xlt" ? Format.Excel : extension is "ppt" or "pps" or "pot" ? Format.PowerPoint : Format.Word;
        ReadOnlySpan<Format> order = [first, Format.Word, Format.Excel, Format.PowerPoint];
        for (int i = 0; i < order.Length; i++)
        {
            var format = order[i];
            if (i > 0 && format == first) continue;
            var status = format switch
            {
                Format.Word => Word(cfb, sink, budget),
                Format.Excel => Excel(cfb, sink, budget),
                _ => PowerPoint(cfb, sink, budget),
            };
            if (status is { } s) return s;
        }
        return DocStatus.Unsupported; // a compound file of another program (Works, Visio, an installer)
    }

    /// <summary>
    /// A file with an old Office name that isn't a compound file, read by its first bytes whatever its extension (after a
    /// byte-order mark and white space): "{\rtf" is RTF (WordPad and many programs save it as .doc, others as .xls or
    /// .ppt), read as RtfReader reads it; '&lt;' is a web page or XML (a report exported from a website or Excel as .xls),
    /// read as HTML so its tags, styles and class names stay out of the index. Anything else is read as a user's extra
    /// text extension was read before this reader: by PlainTextReader, with its limits and its answer for a file that
    /// isn't text. So a tab-separated export saved as .xls and a text .doc stay searchable. Such a file has no page
    /// count, so a match in it gets no sheet or slide place (SnippetFormatter).
    /// </summary>
    static DocStatus NotCompound(IByteSource source, ReadOnlySpan<char> extension, ExtractSink sink, ExtractBudget budget)
    {
        // A small head, read once and charged to the budget; then only the reader it picks reads the file.
        Span<byte> head = stackalloc byte[HeadBytes];
        head = head[..source.ReadFully(0, head)];
        switch (StartOf(head))
        {
            case Start.Rtf:
                var rtf = new RtfReader.Parser(source, sink, budget);
                if (!rtf.Start()) break;
                rtf.Run();
                return rtf.Cut ? DocStatus.Truncated : DocStatus.Ok;
            case Start.Markup:
                return PlainText.Read(source, "html", sink, budget);
        }
        return PlainText.Read(source, extension, sink, budget);
    }

    /// <summary>How much of a file's start is looked at for RTF or markup (white space before them included).</summary>
    const int HeadBytes = 1024;

    internal enum Start { Other, Rtf, Markup }

    /// <summary>
    /// What a file starts with after an optional byte-order mark (UTF-8, or UTF-16 in either byte order) and white space:
    /// "{\rtf" (8-bit only, as RTF always is), '&lt;', or something else.
    /// </summary>
    internal static Start StartOf(ReadOnlySpan<byte> head)
    {
        int at = 0, step = 1, low = 0;
        if (head.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF])) at = 3;
        else if (head.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE])) (at, step, low) = (2, 2, 0);
        else if (head.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF])) (at, step, low) = (2, 2, 1);
        // Each character is its low byte, with a zero high byte in UTF-16.
        for (; at + step <= head.Length; at += step)
        {
            if (step == 2 && head[at + 1 - low] != 0) return Start.Other;
            byte c = head[at + low];
            if (c is (byte)' ' or (byte)'\t' or (byte)'\r' or (byte)'\n') continue;
            if (c == '<') return Start.Markup;
            return step == 1 && head[at..].StartsWith("{\\rtf"u8) ? Start.Rtf : Start.Other;
        }
        return Start.Other;
    }

    static readonly PlainTextReader PlainText = new();

    // ------------------------------------------------------------------ Word

    /// <summary>Null when the file has no WordDocument stream.</summary>
    DocStatus? Word(CompoundFile cfb, ExtractSink sink, ExtractBudget budget)
    {
        int id = cfb.Find(cfb.Root, "WordDocument");
        if (id < 0) return null;
        bool cut = false;
        var document = Stream(cfb, id, LegacyWord.MaxStreamBytes, budget, ref cut);
        if (document == null || document.Length < 0x0C) return DocStatus.Damaged;
        ushort ident = BinaryPrimitives.ReadUInt16LittleEndian(document);
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(document.AsSpan(0x0A));
        if (ident != 0xA5EC) return DocStatus.Unsupported; // Word 6/95 (0xA5DC) and older
        if ((flags & 0x0100) != 0) return DocStatus.PasswordProtected; // fEncrypted: a password or XOR obfuscation
        // Only the table stream the FIB names is read (fWhichTblStm).
        bool one = (flags & 0x0200) != 0;
        int tableId = cfb.Find(cfb.Root, one ? "1Table" : "0Table");
        var table = tableId < 0 ? null : Stream(cfb, tableId, LegacyWord.MaxStreamBytes, budget, ref cut);
        int max = OneMore(budget.Limits.MaxTextChars);
        string? text = Timed(budget, ct => LegacyWord.FromStreams(document, one ? null : table, one ? table : null, max, ct));
        if (text == null) return DocStatus.Damaged;
        sink.Text(text);
        return cut ? DocStatus.Truncated : DocStatus.Ok;
    }

    // ------------------------------------------------------------------ Excel

    /// <summary>Null when the file has no Workbook (BIFF8) or Book (BIFF5) stream.</summary>
    DocStatus? Excel(CompoundFile cfb, ExtractSink sink, ExtractBudget budget)
    {
        int id = cfb.Find(cfb.Root, "Workbook");
        if (id < 0) id = cfb.Find(cfb.Root, "Book");
        if (id < 0) return null;
        bool cut = false;
        var stream = Stream(cfb, id, LegacyExcel.MaxStreamBytes, budget, ref cut);
        if (stream == null) return DocStatus.Damaged;
        if (HasFilePass(stream)) return DocStatus.PasswordProtected;
        // The cells kept stop at about what the text limit can take, not at Excel's grid, so a big numeric workbook
        // doesn't hold hundreds of megabytes of cells to keep 1 MB of text.
        int kept = OneMore(budget.Limits.MaxTextChars);
        var sheets = Timed(budget, ct => LegacyExcel.Parse(stream, MaxRows, MaxColumns, kept, ct, out _));
        if (sheets == null) return DocStatus.Damaged;
        bool partial = cut;
        int read = 0;
        foreach (var sheet in sheets)
        {
            if (sink.Full) break;
            // A sheet the reader's caps kept from being read: what came before stands.
            if (sheet.NotRead) { partial = true; break; }
            budget.Check();
            sink.StartSheet(sheet.Name);
            read++;
            foreach (var row in sheet.Rows)
            {
                if (sink.Full) break;
                bool rowHasCells = false;
                foreach (var cell in row)
                {
                    if (string.IsNullOrEmpty(cell)) continue;
                    if (rowHasCells) sink.Text("\t");
                    sink.Text(cell);
                    rowHasCells = true;
                }
                if (rowHasCells) sink.LineBreak();
            }
            if (sheet.Truncated) partial = true;
        }
        if (sink.Pages == 0) sink.Pages = read;
        return partial ? DocStatus.Truncated : DocStatus.Ok;
    }

    /// <summary>The workbook globals hold a FILEPASS record: the workbook is encrypted.</summary>
    internal static bool HasFilePass(ReadOnlySpan<byte> stream)
    {
        // Each record takes 4 bytes or more, so the walk ends within the stream.
        for (int at = 0, n = 0; at <= stream.Length - 4; n++)
        {
            int type = BinaryPrimitives.ReadUInt16LittleEndian(stream[at..]), length = BinaryPrimitives.ReadUInt16LittleEndian(stream[(at + 2)..]);
            if (n == 0 && type != 0x0809) return false; // not the globals' BOF
            if (n > 0 && type is 0x000A or 0x0809) return false; // the globals end
            if (type == 0x002F) return true;
            at += 4 + length;
        }
        return false;
    }

    // ------------------------------------------------------------------ PowerPoint

    /// <summary>Null when the file has no PowerPoint Document stream.</summary>
    DocStatus? PowerPoint(CompoundFile cfb, ExtractSink sink, ExtractBudget budget)
    {
        int id = cfb.Find(cfb.Root, "PowerPoint Document");
        if (id < 0) return null;
        bool cut = false;
        var document = Stream(cfb, id, LegacyPowerPoint.MaxStreamBytes, budget, ref cut);
        if (document == null) return DocStatus.Damaged;
        byte[]? currentUser = null;
        int user = cfb.Find(cfb.Root, "Current User");
        if (user >= 0 && cfb.Type(user) == CompoundFile.EntryType.Stream && cfb.Size(user) <= MaxCurrentUserBytes)
            currentUser = cfb.ReadStream(user, MaxCurrentUserBytes);
        if (IsEncryptedPresentation(document, currentUser)) return DocStatus.PasswordProtected;
        int max = OneMore(budget.Limits.MaxTextChars);
        var slides = Timed(budget, ct => LegacyPowerPoint.Parse(document, currentUser, max, ct));
        if (slides == null) return DocStatus.Damaged;
        int started = 0;
        foreach (var slide in slides)
        {
            if (sink.Full) break;
            if (started > 0) sink.PageBreak();
            started++;
            sink.Text(slide);
        }
        sink.Pages = started;
        return cut ? DocStatus.Truncated : DocStatus.Ok;
    }

    /// <summary>
    /// The Current User stream carries the encrypted header token, or the document stream has a CryptSession10Container
    /// at its top level: what PowerPoint writes for a presentation saved with a password.
    /// </summary>
    internal static bool IsEncryptedPresentation(ReadOnlySpan<byte> document, ReadOnlySpan<byte> currentUser)
    {
        // CurrentUserAtom: an 8-byte header, its size, then the header token.
        if (currentUser.Length >= 16 && BinaryPrimitives.ReadUInt16LittleEndian(currentUser[2..]) == 0x0FF6
            && BinaryPrimitives.ReadUInt32LittleEndian(currentUser[12..]) == 0xF3D1C4DF)
            return true;
        // Each top-level record takes 8 bytes or more, so the walk ends within the stream.
        for (long at = 0; at <= document.Length - 8;)
        {
            int i = (int)at;
            if (BinaryPrimitives.ReadUInt16LittleEndian(document[(i + 2)..]) == 0x2F14) return true;
            at += 8L + BinaryPrimitives.ReadUInt32LittleEndian(document[(i + 4)..]);
        }
        return false;
    }

    // ------------------------------------------------------------------ Shared

    /// <summary>
    /// A stream's bytes up to the reader's own cap (cut set when it is longer); null when it isn't a stream or its chain
    /// is broken. The streams are stored, not compressed, so MaxStreamBytes (a cap on decoded bytes) doesn't apply: the
    /// reads charge the budget's MaxBytesRead. What is read is also capped at the bytes the budget has left: a stream
    /// larger than that is the bytes limit (TooBig, as a docx past it), found before its array is allocated.
    /// </summary>
    static byte[]? Stream(CompoundFile cfb, int id, long readerCap, ExtractBudget budget, ref bool cut)
    {
        if (cfb.Type(id) != CompoundFile.EntryType.Stream) return null;
        long size = cfb.Size(id), left = Math.Max(0, budget.Limits.MaxBytesRead - budget.BytesRead);
        if (Math.Min(size, readerCap) > left) throw new ContentLimitException(ContentLimitException.Bytes);
        if (size > readerCap) cut = true;
        budget.Check();
        return cfb.ReadStream(id, readerCap);
    }

    /// <summary>The text limit plus one, so a document longer than the limit fills the sink and is marked Truncated.</summary>
    static int OneMore(int maxTextChars) => (int)Math.Min(int.MaxValue, Math.Max(0, maxTextChars) + 1L);

    /// <summary>
    /// Runs a reader's parse under the budget: the readers check a token, so the time left is given to them as one, and
    /// its firing becomes the time limit (the caller's own cancellation stays a cancellation).
    /// </summary>
    T Timed<T>(ExtractBudget budget, Func<CancellationToken, T> parse)
    {
        budget.Check();
        long left = Math.Max(1, budget.Limits.MaxMilliseconds - budget.ElapsedMs);
        using var timer = CancellationTokenSource.CreateLinkedTokenSource(budget.Token);
        timer.CancelAfter(TimeSpan.FromMilliseconds(left));
        try
        {
            beforeParse?.Invoke(timer.Token);
            var result = parse(timer.Token);
            budget.Check();
            return result;
        }
        catch (OperationCanceledException) when (!budget.Token.IsCancellationRequested)
        {
            throw new ContentLimitException(ContentLimitException.Time);
        }
    }
}
