using System.IO.Compression;
using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Extract;

namespace Anything.Tests.Content;

public class ByteSourcesTests
{
    [Fact]
    public void Memory_source_reads_at_offsets_and_counts_bytes()
    {
        var budget = new ExtractBudget(ExtractLimits.Local, CancellationToken.None);
        using var source = new MemoryByteSource(Encoding.ASCII.GetBytes("0123456789"), budget);
        var buffer = new byte[4];
        Assert.Equal(4, source.ReadAt(2, buffer));
        Assert.Equal("2345", Encoding.ASCII.GetString(buffer));
        Assert.Equal(2, source.ReadAt(8, buffer));
        Assert.Equal(0, source.ReadAt(10, buffer));
        Assert.Equal(0, source.ReadAt(1000, buffer));
        Assert.Equal(6, source.BytesRead);
        Assert.Equal(6, budget.BytesRead);
        Assert.Throws<ArgumentOutOfRangeException>(() => source.ReadAt(-1, buffer));
    }

    [Fact]
    public void Reading_past_the_byte_budget_stops()
    {
        var budget = new ExtractBudget(new ExtractLimits(MaxBytesRead: 5), CancellationToken.None);
        using var source = new MemoryByteSource(new byte[100], budget);
        source.ReadAt(0, new byte[5]);
        var e = Assert.Throws<ContentLimitException>(() => source.ReadAt(5, new byte[1]));
        Assert.Equal(ContentLimitException.Bytes, e.Limit);
    }

    [Fact]
    public void Budget_time_leaves_out_waits_and_cancellation_throws()
    {
        long now = 0;
        var budget = new ExtractBudget(new ExtractLimits(MaxMilliseconds: 100), CancellationToken.None, () => now);
        now = 90;
        budget.Check();
        now = 150;
        budget.Waited(60);
        Assert.Equal(90, budget.ElapsedMs);
        budget.Check();
        now = 200;
        Assert.Equal(ContentLimitException.Time, Assert.Throws<ContentLimitException>(budget.Check).Limit);

        var decoded = new ExtractBudget(new ExtractLimits(MaxDecodedBytes: 10), CancellationToken.None);
        decoded.Decoded(10);
        Assert.Equal(ContentLimitException.Decoded, Assert.Throws<ContentLimitException>(() => decoded.Decoded(1)).Limit);

        using var cts = new CancellationTokenSource();
        var cancelled = new ExtractBudget(ExtractLimits.Local, cts.Token);
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(cancelled.Check);
    }

    [Fact]
    public void A_zip_is_read_through_the_stream_without_reading_every_entry()
    {
        var zip = new MemoryStream();
        using (var archive = new ZipArchive(zip, ZipArchiveMode.Create, leaveOpen: true))
        {
            var big = archive.CreateEntry("media/image1.png", CompressionLevel.NoCompression);
            using (var s = big.Open()) s.Write(new byte[512 * 1024]);
            var doc = archive.CreateEntry("word/document.xml");
            using (var s = doc.Open()) s.Write(Encoding.UTF8.GetBytes("<w:t>hello</w:t>"));
        }
        var bytes = zip.ToArray();
        var budget = new ExtractBudget(ExtractLimits.Local, CancellationToken.None);
        using var source = new MemoryByteSource(bytes, budget);
        using (var archive = new ZipArchive(new ByteSourceStream(source), ZipArchiveMode.Read))
        using (var reader = new StreamReader(archive.GetEntry("word/document.xml")!.Open()))
            Assert.Equal("<w:t>hello</w:t>", reader.ReadToEnd());
        Assert.True(source.BytesRead < bytes.Length / 4, $"read {source.BytesRead} of {bytes.Length}");
    }

    [Fact]
    public void The_stream_seeks_and_refuses_writes()
    {
        var budget = new ExtractBudget(ExtractLimits.Local, CancellationToken.None);
        using var source = new MemoryByteSource(Encoding.ASCII.GetBytes("abcdef"), budget);
        using var stream = new ByteSourceStream(source);
        Assert.True(stream.CanSeek);
        Assert.False(stream.CanWrite);
        Assert.Equal(6, stream.Length);
        Assert.Equal(4, stream.Seek(-2, SeekOrigin.End));
        Assert.Equal('e', stream.ReadByte());
        stream.Position = 0;
        Assert.Equal('a', stream.ReadByte());
        Assert.Equal(3, stream.Seek(2, SeekOrigin.Current));
        Assert.Throws<IOException>(() => stream.Seek(-10, SeekOrigin.Begin));
        Assert.Throws<NotSupportedException>(() => stream.Write([1], 0, 1));
        stream.Position = 6;
        Assert.Equal(-1, stream.ReadByte());
    }
}
