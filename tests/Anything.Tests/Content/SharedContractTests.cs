using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Extract;

namespace Anything.Tests.Content;

/// <summary>The shared-contract changes made at integration (units' requests), each held to what the units rely on.</summary>
public class SharedContractTests
{
    [Fact]
    public void A_sink_tells_a_fields_value_so_far_without_side_effects()
    {
        var sink = new ExtractSink(ExtractLimits.Local);
        Assert.Null(sink.Value(DocField.Title));
        sink.Text("body words");
        sink.Field(DocField.Title, "  First title ");
        sink.Field(DocField.Title, "Second title");
        sink.Field(DocField.Keywords, "beach");
        sink.Field(DocField.Keywords, "sunset");
        sink.Field(DocField.Keywords, "BEACH");
        Assert.Equal("First title", sink.Value(DocField.Title));
        Assert.Equal("beach; sunset", sink.Value(DocField.Keywords));
        Assert.Null(sink.Value(DocField.Comment));
        // Asking changes nothing the document is built from.
        sink.Text(" more");
        var doc = sink.ToDoc(ContentKind.Text, DocStatus.Ok, 1, 0, 0);
        Assert.Equal("body words more", doc.Body);
        Assert.Equal([(DocField.Title, "First title"), (DocField.Keywords, "beach; sunset")], doc.Fields);
    }

    [Fact]
    public void An_empty_source_has_no_text_and_is_no_problem()
    {
        var budget = new ExtractBudget(ExtractLimits.Local, CancellationToken.None);
        foreach (var ext in (string[])["pdf", "docx", "txt", "jpg", "msg", "psd", "log"])
        {
            var kind = ext == "log" ? ContentKind.Text : ContentTypes.KindOf("a." + ext);
            var doc = ContentReaders.Extract(new MemoryByteSource(ReadOnlyMemory<byte>.Empty, budget), ext, kind, budget);
            Assert.Equal(DocStatus.NoText, doc.Status);
            Assert.Equal(ContentReaders.CurrentVersion(ext, kind), doc.ExtractorVersion);
        }
    }

    [Fact]
    public void Finding_a_reader_allocates_nothing()
    {
        ContentReaders.ReaderFor("docx", ContentKind.Office);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++)
        {
            ContentReaders.ReaderFor("docx", ContentKind.Office);
            ContentReaders.ReaderFor("LOG", ContentKind.Text);
            ContentReaders.ReaderFor("zip", ContentKind.None);
        }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
    }

    [Fact]
    public void Every_xmp_packet_is_shape_checked_whoever_reads_it()
    {
        var good = Encoding.UTF8.GetBytes("<x:xmpmeta xmlns:x='adobe:ns:meta/'><rdf:RDF xmlns:rdf='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>"
            + "<rdf:Description xmlns:dc='http://purl.org/dc/elements/1.1/'><dc:format>application/pdf</dc:format>"
            + "<dc:title><rdf:Alt><rdf:li>Annual report</rdf:li></rdf:Alt></dc:title></rdf:Description></rdf:RDF></x:xmpmeta>");
        var sink = new ExtractSink(ExtractLimits.Local);
        Assert.True(Xmp.Read(new ReadOnlyMemory<byte>(good), sink));
        Assert.Equal("Annual report", sink.Value(DocField.Title));
        Assert.Equal("application/pdf", Xmp.Format(new ReadOnlyMemory<byte>(good)));

        // Nested far too deep (XmlReader would keep an object per open element): refused part-way, nothing past it read.
        var deep = new StringBuilder("<x:xmpmeta xmlns:x='adobe:ns:meta/'>");
        for (int i = 0; i < 5000; i++) deep.Append("<a>");
        var hostile = Encoding.UTF8.GetBytes(deep.ToString());
        Assert.False(XmlShape.Allows(hostile));
        var refused = new ExtractSink(ExtractLimits.Local);
        Assert.False(Xmp.Read(new ReadOnlyMemory<byte>(hostile), refused));
        Assert.Null(Xmp.Format(new ReadOnlyMemory<byte>(hostile)));
        Assert.Equal(0, refused.FieldCount);
    }

    [Fact]
    public void A_count_cut_short_scales_up_to_what_its_drive_holds()
    {
        var partial = new ContentEstimate(ContentMode.Documents, 1000, 10, 5_000_000, TimeSpan.FromMinutes(10), 2_000_000, AtLeast: true);
        var scaled = ContentEstimator.Extrapolate(partial, listedBytes: 1_000_000_000, usedBytes: 4_000_000_000);
        Assert.False(scaled.AtLeast);
        Assert.Equal(4000, scaled.Documents);
        Assert.Equal(40, scaled.Pictures);
        Assert.Equal(20_000_000, scaled.IndexBytes);
        Assert.Equal(TimeSpan.FromMinutes(40), scaled.FirstPass);
        Assert.Equal(8_000_000, scaled.NetworkBytesToRead);
        // Complete, unknown sizes, or nothing more held: unchanged.
        var complete = partial with { AtLeast = false };
        Assert.Equal(complete, ContentEstimator.Extrapolate(complete, 1, 4));
        Assert.Equal(partial, ContentEstimator.Extrapolate(partial, 0, 4_000_000_000));
        Assert.Equal(partial, ContentEstimator.Extrapolate(partial, 1_000_000_000, 500_000_000));
        var huge = ContentEstimator.Extrapolate(partial with { FirstPass = TimeSpan.MaxValue, IndexBytes = long.MaxValue }, 1, long.MaxValue);
        Assert.Equal(long.MaxValue, huge.IndexBytes);
        Assert.Equal(int.MaxValue, huge.Documents);
    }

    [Fact]
    public async Task A_folder_census_leaves_out_documents_over_the_size_limit_and_counts_what_it_listed()
    {
        // Not under %TEMP%: the census never lists inside it (a denied folder).
        var dir = Directory.CreateDirectory(Path.Combine(AppContext.BaseDirectory, "census-" + Guid.NewGuid().ToString("N")));
        try
        {
            File.WriteAllText(Path.Combine(dir.FullName, "small.txt"), "words");
            using (var big = File.Create(Path.Combine(dir.FullName, "big.txt"))) big.SetLength(3L << 20);
            using (var pic = File.Create(Path.Combine(dir.FullName, "big.jpg"))) pic.SetLength(3L << 20);
            using (var other = File.Create(Path.Combine(dir.FullName, "song.mp3"))) other.SetLength(1L << 20);
            var settings = new ContentSettings { Mode = ContentMode.DocumentsAndPictures, ModeExplicit = true, UseMyFolders = false, MaxFileSizeMB = 1 };
            var census = await ContentEstimator.CountFoldersAsync([dir.FullName], settings, TimeSpan.FromSeconds(20), null, CancellationToken.None);
            Assert.Equal(1, census.Kinds[ContentKind.Text].Files); // big.txt is TooBig: never read
            Assert.Equal(1, census.Kinds[ContentKind.Picture].Files); // pictures have no limit (only headers are read)
            Assert.Equal(5 + (3L << 20) * 2 + (1L << 20), census.ListedBytes);
            var unlimited = await ContentEstimator.CountFoldersAsync([dir.FullName], new ContentSettings { MaxFileSizeMB = 0, UseMyFolders = false }, TimeSpan.FromSeconds(20),
                null, CancellationToken.None);
            Assert.Equal(2, unlimited.Kinds[ContentKind.Text].Files);
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void A_calibration_keeps_its_network_overhead_apart_from_the_kinds()
    {
        Assert.Equal(5, ContentEstimator.Priors.NetworkOverheadMs);
        Assert.Equal(0, ContentEstimator.Priors.NetworkMeasured);
        Assert.False(ContentEstimator.Priors.Costs.ContainsKey(ContentKind.None));
        var census = new ContentCensus(new Dictionary<ContentKind, KindTally> { [ContentKind.Pdf] = new(100, 100L << 20, 100, 100L << 20) },
            new Dictionary<ContentKind, IReadOnlyList<string>>(), 0, true);
        var settings = new ContentSettings { Mode = ContentMode.Documents, ModeExplicit = true };
        var slow = ContentEstimator.Estimate(census, ContentEstimator.Priors with { NetworkOverheadMs = 1005 }, ContentMode.Documents, settings);
        var fast = ContentEstimator.Estimate(census, ContentEstimator.Priors, ContentMode.Documents, settings);
        // 100 network files, 1 s more each, times the time factor.
        Assert.InRange((slow.FirstPass - fast.FirstPass).TotalSeconds, 100, 200);
    }

    [Fact]
    public void Translating_ids_never_writes_into_the_map_it_started_from()
    {
        var b = DocIdMap.Empty(0).ToBuilder();
        b.AddSegment(1, 10_000);
        for (int d = 0; d < 10_000; d++) b.Set(1, d, d);
        var map = b.Build(0);
        var remap = new int[10_000];
        for (int i = 0; i < remap.Length; i++) remap[i] = i % 3 == 0 ? -1 : i + 5;
        var next = map.ToBuilder();
        next.RemapIds(remap);
        next.RemapIds(remap); // owned chunks now: translated in place
        var moved = next.Build(1);
        Assert.Equal(7, map.FileId(1, 7)); // the map it started from is untouched
        Assert.Equal(2, map.FileId(1, 2));
        Assert.Equal(12, moved.FileId(1, 2)); // 2 → 7 → 12
        Assert.Equal(15, moved.FileId(1, 5)); // 5 → 10 → 15
        Assert.Equal(-1, moved.FileId(1, 1)); // 1 → 6 → removed
        Assert.Equal(-1, moved.FileId(1, 3)); // removed by the first
        Assert.Equal(-1, moved.FileId(1, 9998)); // 9998 → 10003, past the remap's end: removed
    }

    [Fact]
    public void The_real_source_reads_a_path_written_with_forward_slashes()
    {
        var dir = Directory.CreateTempSubdirectory("rp-slash-");
        try
        {
            string path = Path.Combine(dir.FullName, "a.txt");
            File.WriteAllText(path, "forward slashes");
            var info = new FileInfo(path);
            var request = new ExtractRequest(path.Replace('\\', '/'), ContentKind.Text, info.Length, info.LastWriteTimeUtc.ToFileTimeUtc(), false, false, 0, ExtractLimits.Local);
            var read = ContentServices.Files().Read(request, CancellationToken.None);
            Assert.Equal(DocStatus.Ok, read.Doc.Status);
            Assert.Contains("forward slashes", read.Doc.Body);
        }
        finally { dir.Delete(true); }
    }
}
