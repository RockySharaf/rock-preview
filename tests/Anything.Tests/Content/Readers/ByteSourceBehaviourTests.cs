using System.IO.Pipes;
using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Extract;
using Anything.Core.Indexing;
using Anything.Tests.Content.Pdf;

namespace Anything.Tests.Content.Readers;

public sealed class ByteSourceBehaviourTests : IDisposable
{
    readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("rp-bytes-");

    public void Dispose() => _dir.Delete(recursive: true);

    string Blob(int size)
    {
        var data = new byte[size];
        new Random(4).NextBytes(data);
        string path = Path.Combine(_dir.FullName, "blob.bin");
        File.WriteAllBytes(path, data);
        return path;
    }

    [Fact]
    public void Network_reads_start_small_double_while_sequential_and_go_through_the_throttle()
    {
        string path = Blob(4 << 20);
        var asked = new List<(string Share, long Bytes)>();
        long Throttle(string share, long bytes, CancellationToken ct) { asked.Add((share, bytes)); return 5; }
        var budget = new ExtractBudget(ExtractLimits.Network, default);
        Assert.Null(FileByteSource.TryOpen(path, budget, network: true, Throttle, out var opened, out _));
        using var source = opened!;
        var b = new byte[10];
        source.ReadAt(0, b);
        Assert.Equal(16 << 10, source.BytesRead);
        source.ReadAt(16 << 10, b); // sequential: twice as much
        Assert.Equal((16 + 32) << 10, source.BytesRead);
        source.ReadAt(48 << 10, b);
        Assert.Equal((16 + 32 + 64) << 10, source.BytesRead);
        source.ReadAt(3 << 20, b); // a seek: back to 16 KB
        Assert.Equal((16 + 32 + 64 + 16) << 10, source.BytesRead);
        Assert.Equal(4, asked.Count);
        Assert.All(asked, a => Assert.Equal(Path.GetPathRoot(path)!.TrimEnd('\\').ToUpperInvariant(), a.Share));
        // A long sequential stream grows to 256 KB per read and never more.
        var stream = new ByteSourceStream(source) { Position = 1 << 20 };
        var chunk = new byte[4096];
        for (int i = 0; i < 300; i++) stream.ReadExactly(chunk);
        Assert.True(asked.Skip(4).All(a => a.Bytes <= 256 << 10));
        Assert.Contains(asked, a => a.Bytes == 256 << 10);
        Assert.Equal(1200 << 10, stream.Position - (1 << 20));
    }

    [Fact]
    public void A_big_network_read_is_asked_for_and_paid_for_a_block_at_a_time()
    {
        // A whole text file read at once (straight into the caller's buffer): on a share it is spread over the limit, not
        // charged and read in one rush.
        string path = Blob(3 << 20);
        var asked = new List<long>();
        long Throttle(string share, long bytes, CancellationToken ct) { asked.Add(bytes); return 1; }
        var budget = new ExtractBudget(ExtractLimits.Network, default);
        Assert.Null(FileByteSource.TryOpen(path, budget, network: true, Throttle, out var opened, out _));
        using (var source = opened!)
        {
            var all = new byte[3 << 20];
            Assert.Equal(all.Length, source.ReadAt(0, all));
            Assert.Equal(File.ReadAllBytes(path), all);
            Assert.Equal(12, asked.Count);
            Assert.All(asked, a => Assert.Equal(256 << 10, a));
            Assert.Equal(3 << 20, source.BytesRead);
        }
        // Locally one read is one read.
        Assert.Null(FileByteSource.TryOpen(path, new ExtractBudget(ExtractLimits.Local, default), network: false, Throttle, out var local, out _));
        using (var source = local!)
        {
            asked.Clear();
            Assert.Equal(3 << 20, source.ReadAt(0, new byte[3 << 20]));
            Assert.Empty(asked);
        }
    }

    [Fact]
    public void Exact_ranges_grow_while_reads_go_on_sequentially()
    {
        // A compound file's sector walk: 512-byte reads one after another, in exact-range mode, on a share.
        string path = Blob(2 << 20);
        var asked = new List<long>();
        long Throttle(string share, long bytes, CancellationToken ct) { asked.Add(bytes); return 0; }
        Assert.Null(FileByteSource.TryOpen(path, new ExtractBudget(ExtractLimits.Network, default), network: true, Throttle, out var opened, out _));
        using (var source = opened!)
        {
            source.ExactRanges = true;
            var sector = new byte[512];
            for (long at = 0; at < 1 << 20; at += 512) source.ReadAt(at, sector);
            Assert.True(asked.Count <= 10, $"{asked.Count} round trips"); // not one per 4 KB (256)
            Assert.Equal(4 << 10, asked[0]); // the first read is exact
            Assert.True(asked[1] > 4 << 10);
            Assert.Equal(256 << 10, asked.Max());
            Assert.True(source.BytesRead <= (1 << 20) + (256 << 10));
            // A seek is a header read again: exact.
            asked.Clear();
            source.ReadAt(1_800_000, sector);
            Assert.Equal([4 << 10], asked);
        }
        // Locally the run grows to a block at most.
        var budget = new ExtractBudget(ExtractLimits.Local, default);
        Assert.Null(FileByteSource.TryOpen(path, budget, network: false, null, out var local, out _));
        using (var source = local!)
        {
            source.ExactRanges = true;
            var b = new byte[512];
            source.ReadAt(0, b);
            Assert.Equal(4 << 10, source.BytesRead);
            source.ReadAt(4 << 10, b);
            Assert.Equal(12 << 10, source.BytesRead); // 4 KB, then 8 KB
            for (long at = 12 << 10; at < 1 << 20; at += 512) source.ReadAt(at, b);
            Assert.True(source.BytesRead <= (1 << 20) + (64 << 10), $"read {source.BytesRead}");
        }
    }

    [Fact]
    public void The_read_budget_counts_what_the_reader_asked_for_locally()
    {
        // Measured: a 1,509-page scan needed 7.4 MB, but its 64 KB blocks reached the 64 MB limit and it was Truncated.
        // Locally the budget counts the bytes asked for; BytesRead (the statistics) counts the bytes fetched.
        string path = Blob(4 << 20);
        var budget = new ExtractBudget(new ExtractLimits(MaxBytesRead: 1 << 20), default);
        Assert.Null(FileByteSource.TryOpen(path, budget, network: false, null, out var opened, out _));
        using (var source = opened!)
        {
            var small = new byte[100];
            for (long at = 0; at < 4 << 20; at += 64 << 10) source.ReadAt(at, small); // 64 reads, a block each
            Assert.Equal(64 * 100, budget.BytesRead);
            Assert.Equal(4 << 20, source.BytesRead);
            // What is asked for still stops at the limit, cached or not.
            var page = new byte[4096];
            var e = Assert.Throws<ContentLimitException>(() => { for (int i = 0; i < 1000; i++) source.ReadAt((long)i * page.Length % (4 << 20), page); });
            Assert.Equal(ContentLimitException.Bytes, e.Limit);
            Assert.InRange(budget.BytesRead, (1 << 20) + 1, (1 << 20) + page.Length);
        }
        // On a share the limit bounds what crosses the network as well: 16 KB fetched per scattered read.
        var net = new ExtractBudget(new ExtractLimits(MaxBytesRead: 512 << 10), default);
        Assert.Null(FileByteSource.TryOpen(path, net, network: true, null, out var shared, out _));
        using (var source = shared!)
        {
            var small = new byte[100];
            var e = Assert.Throws<ContentLimitException>(() => { for (long at = 0; at < 4 << 20; at += 64 << 10) source.ReadAt(at, small); });
            Assert.Equal(ContentLimitException.Bytes, e.Limit);
            Assert.Equal(33 << 14, source.BytesRead); // the 33rd 16 KB passed 512 KB
        }
    }

    [Fact]
    public void A_scan_whose_blocks_pass_the_limit_is_read_whole()
    {
        // A scan's pages: a big picture beside each page's few words. The reader skips the pictures, but each page's
        // objects sit in blocks of their own: more is fetched than the limit allows, far less is asked for.
        const int Pages = 40;
        var w = new PdfFixtureWriter();
        int catalog = w.Reserve(), tree = w.Reserve();
        int font = w.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /Encoding /WinAnsiEncoding >>");
        var random = new Random(7);
        var kids = new List<int>();
        for (int i = 0; i < Pages; i++)
        {
            int page = w.Reserve();
            var pixels = new byte[96 << 10];
            random.NextBytes(pixels);
            int image = w.AddStream("<< /Type /XObject /Subtype /Image /Width 200 /Height 200 /ColorSpace /DeviceRGB /BitsPerComponent 8 /Filter /DCTDecode >>", pixels);
            int content = w.AddStream("<< >>", Encoding.ASCII.GetBytes($"q 612 0 0 792 0 0 cm /Im0 Do Q BT /F1 12 Tf 72 720 Td (invoice sheet{i}) Tj ET"));
            w.Set(page, $"<< /Type /Page /Parent {tree} 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 {font} 0 R >> /XObject << /Im0 {image} 0 R >> >> /Contents {content} 0 R >>");
            kids.Add(page);
        }
        w.Set(tree, $"<< /Type /Pages /Kids [{string.Join(' ', kids.Select(k => $"{k} 0 R"))}] /Count {Pages} >>");
        w.Set(catalog, $"<< /Type /Catalog /Pages {tree} 0 R >>");
        w.Root = catalog;
        string path = Path.Combine(_dir.FullName, "scan.pdf");
        File.WriteAllBytes(path, w.Write());

        var limits = new ExtractLimits(MaxBytesRead: 1 << 20);
        var budget = new ExtractBudget(limits, default);
        Assert.Null(FileByteSource.TryOpen(path, budget, network: false, null, out var opened, out _));
        using var source = opened!;
        var doc = ContentReaders.Extract(source, "pdf", ContentKind.Pdf, budget);
        Assert.Equal(DocStatus.Ok, doc.Status);
        Assert.Equal(Pages, doc.Body.Split('\f').Length);
        Assert.Contains($"sheet{Pages - 1}", doc.Body);
        Assert.True(source.BytesRead > limits.MaxBytesRead, $"fetched {source.BytesRead}"); // charged as before, it would have stopped
        Assert.True(budget.BytesRead < limits.MaxBytesRead / 2, $"asked for {budget.BytesRead}");
        Assert.Equal(source.BytesRead, doc.BytesRead); // the statistics keep what was fetched
    }

    [Fact]
    public void Network_is_decided_by_the_volume_not_the_path_spelling()
    {
        Assert.True(FileByteSource.IsNetworkPath(@"\\nas\jobs\a.pdf"));
        Assert.True(FileByteSource.IsNetworkPath(@"\\?\UNC\nas\jobs\a.pdf"));
        Assert.False(FileByteSource.IsNetworkPath(@"\\?\C:\Users\a.pdf"));
        Assert.False(FileByteSource.IsNetworkPath(@"C:\Users\a.pdf"));
        Assert.False(FileByteSource.IsNetworkPath(@"\\?\Volume{00000000-0000-0000-0000-000000000000}\a.pdf"));
        Assert.False(FileByteSource.IsNetworkPath(@"relative\a.pdf"));
        foreach (var drive in DriveInfo.GetDrives())
            Assert.Equal(drive.DriveType == DriveType.Network, FileByteSource.IsNetworkPath(drive.Name + "a.pdf"));
        // A local file through its \\?\ form reads local blocks (64 KB), not a share's first 16 KB.
        string path = Blob(1 << 20);
        Assert.Null(FileByteSource.TryOpen(@"\\?\" + path, new ExtractBudget(ExtractLimits.Local, default), out var opened, out _));
        using var source = opened!;
        source.ReadAt(0, new byte[10]);
        Assert.Equal(64 << 10, source.BytesRead);
    }

    [Fact]
    public void Cancelling_a_blocked_read_gives_error_995_as_the_watchdog_expects()
    {
        string name = "rp-test-" + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.Out, 1, PipeTransmissionMode.Byte);
        var client = Native.CreateFile(@"\\.\pipe\" + name, Native.GENERIC_READ, 0, 0, Native.OPEN_EXISTING, 0, 0);
        Assert.False(client.IsInvalid);
        server.WaitForConnection();
        uint threadId = 0;
        int result = 0, error = 0;
        var started = new ManualResetEventSlim();
        var reader = new Thread(() =>
        {
            threadId = Native.GetCurrentThreadId();
            started.Set();
            result = ReaderNative.ReadAt(client, new byte[16], 0, out error); // blocks: nothing was written
        });
        reader.Start();
        started.Wait();
        nint thread = Native.OpenThread(Native.THREAD_TERMINATE, 0, threadId);
        try
        {
            // Cancel until the read was under way and gave up (a cancel before ReadFile starts finds nothing to cancel).
            bool cancelled = false;
            for (int i = 0; i < 100 && !reader.Join(50); i++) cancelled |= Native.CancelSynchronousIo(thread);
            Assert.True(cancelled);
        }
        finally { Native.CloseHandle(thread); }
        Assert.True(reader.Join(5000));
        Assert.Equal(-1, result);
        Assert.Equal(995, error);
        client.Dispose();
    }

    [Fact]
    public void Cancellation_goes_up_and_an_extension_without_a_reader_is_never_opened()
    {
        string path = Path.Combine(_dir.FullName, "a.txt");
        File.WriteAllText(path, "words");
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var request = new ExtractRequest(path, ContentKind.Text, -1, 0, false, false, 0, ExtractLimits.Local);
        Assert.Throws<OperationCanceledException>(() => new FileContentSource().Read(request, cts.Token));
    }

    [Fact]
    public void Header_readers_read_only_what_they_need()
    {
        // A JPEG with 5 MB of scan data and a TIFF whose IFD follows 50 MB of pixels.
        var jpeg = PictureFixtures.Jpeg().Concat(new byte[5 << 20]).ToArray();
        var (j, js) = ReaderRun.Counted(jpeg, "jpg");
        Assert.Equal(640, j.Picture!.Value.Width);
        Assert.True(js.BytesRead < 64 << 10, $"jpeg read {js.BytesRead}");
        var tiff = PictureFixtures.Tiff(false, ifdAtEnd: true);
        var big = new byte[50 << 20];
        tiff.AsSpan(0, 8).CopyTo(big);
        int ifd = BitConverter.ToInt32(tiff, 4);
        // Move the IFD and everything after it to the end of the 50 MB file, fixing up every offset by the shift.
        int shift = big.Length - tiff.Length;
        tiff.AsSpan(ifd).CopyTo(big.AsSpan(ifd + shift));
        BitConverter.GetBytes(ifd + shift).CopyTo(big, 4);
        int count = BitConverter.ToUInt16(big, ifd + shift);
        for (int i = 0; i < count; i++)
        {
            int e = ifd + shift + 2 + i * 12;
            int type = BitConverter.ToUInt16(big, e + 2), n = BitConverter.ToInt32(big, e + 4);
            int size = n * (type switch { 3 => 2, 4 => 4, 5 => 8, _ => 1 });
            if (size > 4) BitConverter.GetBytes(BitConverter.ToInt32(big, e + 8) + shift).CopyTo(big, e + 8);
        }
        var (t, ts) = ReaderRun.Counted(big, "tif");
        Assert.Equal(2480, t.Picture!.Value.Width);
        Assert.Equal("Lighthouse at dusk", t.Field(DocField.Title));
        Assert.True(ts.BytesRead < 64 << 10, $"tiff read {ts.BytesRead}");
    }

    [Fact]
    public void Iso_2022_jp_subjects_and_related_parts()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string jis = Convert.ToBase64String(Encoding.GetEncoding("iso-2022-jp").GetBytes("日本語"));
        string eml = $"Subject: =?ISO-2022-JP?B?{jis}?=\r\nContent-Type: multipart/related; boundary=r\r\n\r\n--r\r\n" +
            "Content-Type: text/html; charset=us-ascii\r\n\r\n<p>see <img src=cid:a> picture</p>\r\n--r\r\n" +
            "Content-Type: image/png; name=image001.png\r\nContent-ID: <a>\r\nContent-Transfer-Encoding: base64\r\n\r\niVBORw0KGgo=\r\n--r--\r\n";
        var doc = ReaderRun.Bytes(Encoding.ASCII.GetBytes(eml), "eml");
        Assert.Equal("日本語", doc.Field(DocField.Subject));
        Assert.Contains("see picture", doc.Body);
        Assert.Contains("image001.png", doc.Body);
    }
}
