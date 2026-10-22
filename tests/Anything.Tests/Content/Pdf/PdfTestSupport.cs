using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Extract;

namespace Anything.Tests.Content.Pdf;

/// <summary>Reads fixtures through the real path (ContentReaders.Extract) and records what the reader asked for.</summary>
internal static class PdfTestSupport
{
    /// <summary>Extracts a PDF (or AI) held in memory.</summary>
    public static ExtractedDoc Read(byte[] pdf, string extension = "pdf", ExtractLimits? limits = null, ContentKind kind = ContentKind.Pdf)
    {
        var budget = new ExtractBudget(limits ?? ExtractLimits.Local, CancellationToken.None);
        using var source = new MemoryByteSource(pdf, budget);
        return ContentReaders.Extract(source, extension, kind, budget);
    }

    /// <summary>Extracts through a recording source, so tests can see which byte ranges were read.</summary>
    public static (ExtractedDoc Doc, RecordingSource Source) ReadRecorded(byte[] pdf, ExtractLimits? limits = null)
    {
        var budget = new ExtractBudget(limits ?? ExtractLimits.Local, CancellationToken.None);
        var source = new RecordingSource(pdf, budget);
        return (ContentReaders.Extract(source, "pdf", ContentKind.Pdf, budget), source);
    }

    /// <summary>The body's pages (split at '\f').</summary>
    public static string[] Pages(ExtractedDoc doc) => doc.Body.Split('\f');

    /// <summary>The words of a text as the index sees them (plain and code parts), for loose comparisons.</summary>
    public static string Words(string text)
    {
        var sb = new StringBuilder();
        foreach (var t in ListSink.Of(text).Tokens)
            if (t.Kind is TokenKind.Word or TokenKind.Part or TokenKind.Bigram) sb.Append(t.Folded).Append(' ');
        return sb.ToString().Trim();
    }

    public static byte[] Ascii(string s) => Encoding.Latin1.GetBytes(s);

    /// <summary>The task finished within the time (the Task.Run(...).Wait(2 × budget) rule, without blocking the test thread).</summary>
    public static async Task<bool> Finishes(Task task, int milliseconds) => await Task.WhenAny(task, Task.Delay(milliseconds)) == task;

    /// <summary>The offset of the first occurrence of text in a file.</summary>
    public static int IndexOf(byte[] file, string text) => file.AsSpan().IndexOf(Encoding.Latin1.GetBytes(text));

    /// <summary>A small mutation description for failure messages: seed and the input as hex (cut).</summary>
    public static string Describe(int seed, byte[] input) => $"seed {seed}, {input.Length} bytes: {Convert.ToHexString(input.AsSpan(0, Math.Min(input.Length, 4096)))}";
}

/// <summary>A memory source that records every range read.</summary>
internal sealed class RecordingSource(byte[] bytes, ExtractBudget budget) : IByteSource
{
    readonly MemoryByteSource _inner = new(bytes, budget);
    public List<(long Offset, int Count)> Reads { get; } = [];
    public long Length => _inner.Length;
    public long BytesRead => _inner.BytesRead;

    public int ReadAt(long offset, Span<byte> buffer)
    {
        int n = _inner.ReadAt(offset, buffer);
        if (n > 0) Reads.Add((offset, n));
        return n;
    }

    /// <summary>Bytes read that fall inside [start, end).</summary>
    public long BytesWithin(long start, long end)
    {
        long total = 0;
        foreach (var (o, c) in Reads)
        {
            long a = Math.Max(o, start), b = Math.Min(o + c, end);
            if (b > a) total += b - a;
        }
        return total;
    }

    public void Dispose() => _inner.Dispose();
}
