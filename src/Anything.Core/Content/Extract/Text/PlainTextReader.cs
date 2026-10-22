using System.Buffers;
using System.Text;

namespace Anything.Core.Content.Extract;

/// <summary>
/// Plain text, Markdown, CSV/TSV and HTML (tags stripped), plus the user's extra text extensions. At most the first 4 MB
/// are read (other text than HTML stops reading once what was read fills the text limit); the encoding comes from
/// <see cref="TextDecoding"/> (a BOM, BOM-less UTF-16, UTF-8 valid over the whole window read, else the PC's ANSI code
/// page), and for HTML from its &lt;meta charset&gt; when there is no BOM. An 8-bit file with NULs is binary (NoText).
/// Line ends of every kind become '\n', so stored line N is file line N.
/// </summary>
internal sealed class PlainTextReader : IContentReader
{
    /// <summary>Most bytes read from one file.</summary>
    public const int MaxBytes = 4 << 20;

    public ContentKind Kind => ContentKind.Text;
    public byte Version => 1;
    public bool Handles(ReadOnlySpan<char> extension) => extension is "txt" or "text" or "md" or "markdown" or "csv" or "tsv" or "htm" or "html";

    public DocStatus Read(IByteSource source, ReadOnlySpan<char> extension, ExtractSink sink, ExtractBudget budget)
    {
        int length = (int)Math.Min(source.Length, MaxBytes);
        if (length == 0) return DocStatus.NoText;
        var bytes = ArrayPool<byte>.Shared.Rent(length);
        try
        {
            bool html = extension is "htm" or "html";
            // HTML needs its whole window (tags are stripped after decoding); other text only what fills the text limit.
            int got = html ? source.ReadFully(0, bytes.AsSpan(0, length)) : ReadEnough(source, bytes.AsSpan(0, length), budget);
            var head = bytes.AsSpan(0, got);
            var encoding = TextDecoding.Detect(head);
            if (encoding == null) return DocStatus.NoText;
            if (html && encoding.GetPreamble().Length == 0 && encoding is not (UnicodeEncoding or UTF32Encoding)
                && TextDecoding.HtmlCharset(head) is { } declared
                // A page that says UTF-8 but isn't valid UTF-8 keeps the ANSI guess.
                && !(declared is UTF8Encoding && encoding is not UTF8Encoding))
                encoding = declared;
            bool cut = source.Length > got;
            if (html)
            {
                var text = new StringBuilder(Math.Min(got, budget.Limits.MaxTextChars * 2));
                Decode(head, encoding, cut, budget, chunk =>
                {
                    text.Append(chunk);
                    return text.Length < MaxBytes;
                });
                HtmlText.Extract(text.ToString(), sink, budget);
            }
            else
            {
                Decode(head, encoding, cut, budget, chunk =>
                {
                    sink.Text(chunk);
                    return !sink.Full;
                });
            }
            return cut ? DocStatus.Truncated : DocStatus.Ok;
        }
        finally { ArrayPool<byte>.Shared.Return(bytes); }
    }

    /// <summary>The first read of a text file: the text limit in bytes plus room for line ends and a few multi-byte letters.</summary>
    const int FirstSlack = 64 << 10;
    /// <summary>Each further read (a network read's size).</summary>
    const int Step = 256 << 10;

    /// <summary>
    /// Reads a plain text file in steps: first the text limit plus 64 KB, then 256 KB at a time, and stops once what was
    /// read is 8-bit or UTF-8 text that already fills the sink (or is binary), so a 5 MB CSV on a share costs about 1 MB of
    /// reads, not 4 MB. Detection then looks at exactly the bytes that are decoded. The number of bytes read.
    /// </summary>
    static int ReadEnough(IByteSource source, Span<byte> into, ExtractBudget budget)
    {
        int maxChars = budget.Limits.MaxTextChars;
        int got = 0, want = (int)Math.Min(into.Length, (long)maxChars + FirstSlack);
        while (true)
        {
            got += source.ReadFully(got, into[got..want]);
            if (got < want || got >= into.Length || Fills(into[..got], maxChars)) return got;
            budget.Check();
            want = Math.Min(into.Length, got + Step);
        }
    }

    /// <summary>
    /// The bytes are binary, or 8-bit or UTF-8 text that decodes to more than maxChars characters as the sink keeps them
    /// ("\r\n" is one line end). UTF-16 and UTF-32 text reads on to the 4 MB window.
    /// </summary>
    static bool Fills(ReadOnlySpan<byte> head, int maxChars)
    {
        var encoding = TextDecoding.Detect(head);
        if (encoding == null) return true;
        if (encoding is UnicodeEncoding or UTF32Encoding) return false;
        var preamble = encoding.GetPreamble();
        if (preamble.Length > 0 && head.StartsWith(preamble)) head = head[preamble.Length..];
        // A character cut by the end and a "\r\n" split across it are at most a few characters either way.
        long chars = (long)encoding.GetCharCount(head) - head.Count("\r\n"u8) - 4;
        return chars > maxChars;
    }

    /// <summary>Delegate for decoded chunks: returns false to stop.</summary>
    internal delegate bool ChunkSink(ReadOnlySpan<char> chunk);

    /// <summary>
    /// Decodes in 32K-character chunks (no string of the whole file), dropping a BOM; a character cut by the window's end is
    /// left out.
    /// </summary>
    internal static void Decode(ReadOnlySpan<byte> bytes, Encoding encoding, bool windowCut, ExtractBudget budget, ChunkSink into)
    {
        var preamble = encoding.GetPreamble();
        if (preamble.Length > 0 && bytes.StartsWith(preamble)) bytes = bytes[preamble.Length..];
        var decoder = encoding.GetDecoder();
        var chars = ArrayPool<char>.Shared.Rent(32 << 10);
        try
        {
            while (true)
            {
                budget.Check();
                int take = Math.Min(bytes.Length, 32 << 10);
                bool last = take == bytes.Length;
                decoder.Convert(bytes[..take], chars, flush: last && !windowCut, out int used, out int made, out _);
                bytes = bytes[used..];
                if (made > 0 && !into(chars.AsSpan(0, made))) return;
                if (last && bytes.IsEmpty) return;
                if (used == 0 && made == 0) return;
            }
        }
        finally { ArrayPool<char>.Shared.Return(chars); }
    }
}
