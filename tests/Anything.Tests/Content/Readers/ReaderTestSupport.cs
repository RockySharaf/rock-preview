using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Extract;

namespace Anything.Tests.Content.Readers;

/// <summary>Runs readers over bytes in memory, the way ContentReaders.Extract does for real files.</summary>
internal static class ReaderRun
{
    /// <summary>Reads bytes as a file of this extension (its ContentTypes kind, or Text for an unknown one).</summary>
    public static ExtractedDoc Bytes(byte[] bytes, string extension, ExtractLimits? limits = null)
    {
        var kind = ContentTypes.KindOf("x." + extension);
        if (kind == ContentKind.None) kind = ContentKind.Text;
        var budget = new ExtractBudget(limits ?? ExtractLimits.Local, CancellationToken.None);
        using var source = new MemoryByteSource(bytes, budget);
        return ContentReaders.Extract(source, extension, kind, budget);
    }

    /// <summary>Reads through a counting source, so tests can see which bytes a reader touched.</summary>
    public static (ExtractedDoc Doc, CountingSource Source) Counted(byte[] bytes, string extension, ExtractLimits? limits = null)
    {
        var kind = ContentTypes.KindOf("x." + extension);
        var budget = new ExtractBudget(limits ?? ExtractLimits.Local, CancellationToken.None);
        var source = new CountingSource(bytes, budget);
        return (ContentReaders.Extract(source, extension, kind, budget), source);
    }

    /// <summary>A field's value, or null.</summary>
    public static string? Field(this ExtractedDoc doc, DocField field)
    {
        foreach (var (f, v) in doc.Fields) if (f == field) return v;
        return null;
    }

    public static byte[] Utf8(string s) => Encoding.UTF8.GetBytes(s);
}

/// <summary>A memory source that records every range read (to assert pixels or image streams are never read).</summary>
internal sealed class CountingSource(byte[] bytes, ExtractBudget budget) : IByteSource
{
    readonly MemoryByteSource _inner = new(bytes, budget);
    public List<(long Offset, int Length)> Reads { get; } = [];
    public long Length => _inner.Length;
    public long BytesRead => _inner.BytesRead;

    public int ReadAt(long offset, Span<byte> buffer)
    {
        int n = _inner.ReadAt(offset, buffer);
        if (n > 0) Reads.Add((offset, n));
        return n;
    }

    /// <summary>Whether any read touched [from, to).</summary>
    public bool Touched(long from, long to)
    {
        foreach (var (o, l) in Reads) if (o < to && o + l > from) return true;
        return false;
    }

    public void Dispose() => _inner.Dispose();
}

/// <summary>A memory source whose k-th read fails with a file I/O error (a dropped share, a watchdog cancel).</summary>
internal sealed class FailingSource(byte[] bytes, ExtractBudget budget, int failAt) : IByteSource
{
    readonly MemoryByteSource _inner = new(bytes, budget);
    public int Reads { get; private set; }
    public bool Threw { get; private set; }
    public long Length => _inner.Length;
    public long BytesRead => _inner.BytesRead;

    public int ReadAt(long offset, Span<byte> buffer)
    {
        if (offset < Length && !buffer.IsEmpty && ++Reads == failAt)
        {
            Threw = true;
            throw new ContentIoException(64, "The specified network name is no longer available.");
        }
        return _inner.ReadAt(offset, buffer);
    }

    public void Dispose() => _inner.Dispose();
}
