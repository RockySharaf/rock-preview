using System.Diagnostics;
using Anything.Core.Content;
using Anything.Core.Content.Extract;
using Anything.Core.Content.Indexing;

namespace Anything.Tests.Content.Indexing;

/// <summary>The per-share speed limit: the bucket itself, the scheduler handing it to network reads, and the real source honouring it.</summary>
[Collection(TimingCollection.Name)]
public class ShareThrottleTests
{
    static readonly ExtractLimits Quick = ExtractLimits.Local with { MaxMilliseconds = 2000 };

    [Fact]
    public void A_share_reads_one_second_ahead_then_waits_for_its_average()
    {
        long now = 0;
        var t = new ShareThrottle(() => now) { BytesPerSecond = 64 * 1024 };
        const long Tenth = 64 * 1024 / 10; // 100 ms worth
        for (int i = 0; i < 11; i++) Assert.Equal(0, t.Wait("unc:A", Tenth, default)); // the burst: 1 s ahead
        var sw = Stopwatch.StartNew();
        long waited = t.Wait("unc:A", Tenth, default);
        Assert.InRange(waited, 60, 2000);
        Assert.True(sw.ElapsedMilliseconds >= 60);
        Assert.Equal(0, t.Wait("unc:B", Tenth, default)); // every share has its own bucket
        Assert.False(t.Ready("unc:A", now));
        Assert.True(t.Ready("unc:B", now));
    }

    [Fact]
    public void A_cancelled_wait_ends_at_once()
    {
        long now = 0;
        var t = new ShareThrottle(() => now) { BytesPerSecond = 64 * 1024 };
        t.Charge("unc:A", 64L * 1024 * 30, 0); // 30 s behind
        using var cts = new CancellationTokenSource(50);
        var sw = Stopwatch.StartNew();
        Assert.ThrowsAny<OperationCanceledException>(() => t.Wait("unc:A", 1024, cts.Token));
        Assert.True(sw.ElapsedMilliseconds < 5000);
    }

    [Fact]
    public void Network_reads_wait_on_their_share_bucket_inside_the_read_and_are_not_charged_twice()
    {
        var source = new ThrottlingSource();
        using var s = new ReadScheduler(source, CrashMarker.InMemory(), new ReadSchedulerOptions { Inline = true, ClockMs = () => 0 }) { NetworkBytesPerSecond = 1 << 20 };
        var job = Job(@"\\nas\jobs\a.pdf", network: true, share: "unc:\\\\NAS\\JOBS");
        Assert.True(s.Start(job));
        Assert.True(s.Results.TryDequeue(out var outcome));
        Assert.Equal(ReadResultKind.Read, outcome.Kind);
        Assert.Equal(4L << 20, Interlocked.Read(ref job.ThrottledBytes));
        // The follower charges what the source didn't wait for: nothing here, so the bucket holds only the read itself, under
        // the root's volume key (whatever share name the reader passed).
        s.Charge(job.Share, outcome.Read.Doc.BytesRead - Interlocked.Read(ref job.ThrottledBytes), 0);
        Assert.False(s.ShareReady(job.Share, 2000)); // 4 s worth at 1 MB/s, one of them burst
        Assert.True(s.ShareReady(job.Share, 3000));

        var local = Job(@"C:\a.txt");
        Assert.True(s.Start(local));
        Assert.True(s.Results.TryDequeue(out _));
        Assert.Null(source.LastThrottle); // local reads never get one
    }

    [Fact]
    public void The_real_source_waits_on_the_request_throttle_before_its_own()
    {
        string dir = Path.Combine(Path.GetTempPath(), "rp-throttle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            string path = Path.Combine(dir, "a.txt");
            File.WriteAllText(path, "words to read over the network");
            var info = new FileInfo(path);
            int own = 0, requested = 0;
            var files = new FileContentSource((_, _, _) => { own++; return 0; });
            var request = new ExtractRequest(path, ContentKind.Text, info.Length, info.LastWriteTimeUtc.ToFileTimeUtc(), Network: true, false, 0, ExtractLimits.Network)
            {
                Throttle = (_, _, _) => { requested++; return 0; },
            };
            var read = files.Read(request, default);
            Assert.Equal(DocStatus.Ok, read.Doc.Status);
            Assert.True(requested > 0);
            Assert.Equal(0, own);
            var plain = files.Read(request with { Throttle = null }, default);
            Assert.Equal(DocStatus.Ok, plain.Doc.Status);
            Assert.True(own > 0);
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    static ReadJob Job(string path, bool network = false, string share = "path:C:")
        => new()
        {
            FileId = 1, Generation = 0, Key = ContentKeys.Of("path:", path), Volume = 0, RelativePath = path, RootName = "C:", RootPath = "C:",
            Share = share, Network = network,
            Request = new ExtractRequest(path, ContentKind.Text, 10, 10, network, false, 0, Quick),
        };

    /// <summary>Asks the request's throttle for 4 MB as a real network read would, then says it read them.</summary>
    sealed class ThrottlingSource : IContentSource
    {
        public NetworkThrottle? LastThrottle;

        public ContentRead Read(ExtractRequest request, CancellationToken ct)
        {
            LastThrottle = request.Throttle;
            request.Throttle?.Invoke(@"\\NAS\JOBS", 4L << 20, ct);
            return new ContentRead(new ExtractedDoc { Kind = request.Kind, Status = DocStatus.Ok, Body = "words", BytesRead = 4L << 20 }, 10, 10, false, false);
        }

        public bool TryStat(string fullPath, out long size, out long modifiedUtc, out FileAttributes attributes)
        {
            size = 10;
            modifiedUtc = 10;
            attributes = FileAttributes.Archive;
            return true;
        }
    }
}
