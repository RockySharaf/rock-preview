using System.Diagnostics;
using System.Text;
using Anything.Core.Content;
using Anything.Tests.Content.Pdf;
using Anything.Tests.Content.Readers;
using Anything.Tests.Content.Store;
using Xunit.Abstractions;

namespace Anything.Tests.Content.Integration;

/// <summary>
/// I2, the estimator (U5) against the real pipeline: CountFoldersAsync, Calibrate (real readers, tokenizer and encoder) and
/// Estimate on a folder of real files, then the index the follower actually builds from the same files (R12: size within
/// ±25% is the goal; the test holds it to a factor and reports the figures).
/// </summary>
[Collection(RealFilesSerial.Name)]
public sealed class EstimateOnRealFilesTests(ITestOutputHelper output) : IDisposable
{
    readonly RealFilesRig _rig = new();

    public void Dispose() => _rig.Dispose();

    /// <summary>
    /// A few hundred documents of prose, plus pictures and design files. Real files keep bringing new words (names,
    /// numbers, codes): the estimator's dictionary term is the brief's Heaps fit on this PC's files (204,267 terms at
    /// 1,169,777 pairs), so the prose mixes Zipf words with about one new token in six, as that fit implies here; a
    /// capped vocabulary would put the whole difference on that one term.
    /// </summary>
    Dictionary<ContentKind, int> WriteCorpus()
    {
        var words = Synth.Vocabulary(6000, seed: 3);
        var zipf = new Synth.Zipf(words.Length, 1.05);
        var rnd = new Random(5);
        const string Letters = "abcdefghijklmnopqrstuvwxyz0123456789";
        string Prose(int count)
        {
            var sb = new StringBuilder();
            for (int i = 0; i < count; i++)
            {
                if (rnd.Next(6) == 0)
                    for (int c = 0, n = 5 + rnd.Next(5); c < n; c++) sb.Append(Letters[rnd.Next(c == 0 ? 26 : Letters.Length)]);
                else sb.Append(words[zipf.Next(rnd)]);
                sb.Append(i % 17 == 16 ? ".\n" : " ");
            }
            return sb.ToString();
        }
        var kinds = new Dictionary<ContentKind, int>();
        void Count(ContentKind k) => kinds[k] = kinds.GetValueOrDefault(k) + 1;
        for (int i = 0; i < 240; i++)
        {
            _rig.Write($@"Notes\n{i:D3}.txt", Prose(200 + rnd.Next(900)), index: false);
            Count(ContentKind.Text);
        }
        for (int i = 0; i < 40; i++)
        {
            var body = new StringBuilder($"<w:document xmlns:w=\"{OfficeFixtures.W}\"><w:body>");
            for (int p = 0; p < 6 + rnd.Next(20); p++) body.Append("<w:p><w:r><w:t>").Append(Prose(40 + rnd.Next(80))).Append("</w:t></w:r></w:p>");
            _rig.Write($@"Office\d{i:D3}.docx", OfficeFixtures.Docx(body: body.Append("</w:body></w:document>").ToString()), index: false);
            Count(ContentKind.Office);
        }
        for (int i = 0; i < 30; i++)
        {
            var pages = Enumerable.Range(0, 1 + rnd.Next(4))
                .Select(_ => PdfFixtureWriter.Lines([.. Enumerable.Range(0, 20).Select(_ => Prose(12))])).ToArray();
            _rig.Write($@"Pdf\p{i:D3}.pdf", PdfFixtureWriter.SimpleDocument(pages).Write(), index: false);
            Count(ContentKind.Pdf);
        }
        for (int i = 0; i < 60; i++)
        {
            _rig.Write($@"Pictures\img{i:D3}.jpg", PictureFixtures.Jpeg(), index: false);
            Count(ContentKind.Picture);
        }
        for (int i = 0; i < 5; i++)
        {
            _rig.Write($@"Design\poster{i}.psd", DesignFixtures.Psd(), index: false);
            Count(ContentKind.Design);
        }
        _rig.Write(@"Notes\.env", "SECRET=1", index: false); // never counted
        return kinds;
    }

    [Fact]
    public async Task The_estimate_from_a_census_and_a_real_sample_is_close_to_the_index_built_from_the_same_files()
    {
        var kinds = WriteCorpus();
        var settings = _rig.Settings;

        // The census lists the folder the way Setup does (no name index).
        var census = await ContentEstimator.CountFoldersAsync([_rig.Root], settings, TimeSpan.FromSeconds(20), null, CancellationToken.None);
        Assert.True(census.Complete);
        foreach (var (kind, n) in kinds) Assert.Equal(n, census.Kinds.GetValueOrDefault(kind).Files);
        Assert.Equal(kinds.Values.Sum(), census.Kinds.Values.Sum(t => t.Files));

        // A real sample through the real readers (the crash marker in the rig's own folder, never the user's %TEMP% one).
        var calibration = ContentEstimator.Calibrate(census, ContentServices.Files(), TimeSpan.FromSeconds(4), maxNetworkSamples: 0,
            CancellationToken.None, markerPath: Path.Combine(_rig.ContentFolder.Path, "calibration.reading"));
        foreach (var kind in kinds.Keys) Assert.True(calibration.Measured.GetValueOrDefault(kind) > 0, $"{kind} was never sampled");
        var estimate = ContentEstimator.Estimate(census, calibration, ContentMode.DocumentsAndPictures, settings);
        Assert.Equal(kinds.Where(k => k.Key != ContentKind.Picture).Sum(k => k.Value), estimate.Documents);
        Assert.Equal(kinds[ContentKind.Picture], estimate.Pictures);
        Assert.False(estimate.AtLeast);

        // The index the follower builds from the same files.
        _rig.AddAll();
        var clock = Stopwatch.StartNew();
        _rig.Start();
        _rig.Settle();
        clock.Stop();
        var status = _rig.Content!.Status;
        Assert.Equal(ContentPhase.UpToDate, status.Phase);
        Assert.Equal(kinds.Values.Sum(), status.Total);
        Assert.Equal(kinds.Values.Sum(), _rig.Rows().Count);
        long actual = status.IndexBytes;

        // And Settings' own estimate, from the live name index and the index's statistics, says the same documents.
        var live = await _rig.Content.EstimateAsync(settings, CancellationToken.None);
        Assert.Equal(estimate.Documents, live.Documents);
        Assert.Equal(estimate.Pictures, live.Pictures);

        using (var view = _rig.Content.AcquireView()!)
        {
            long terms = 0;
            foreach (var seg in view.Store.Segments)
                seg.Terms(ReadOnlySpan<byte>.Empty, ReadOnlySpan<byte>.Empty, (t, in p) => { if (t.Length > 0 && t[0] != 1) terms++; return true; }, default);
            var st = view.Store.Stats;
            double pairs = st.Values.Sum(s => (double)s.Pairs);
            output.WriteLine($"index: {view.Store.Segments.Count} segments, {terms:N0} terms (Heaps fit says {ContentEstimator.HeapsK * Math.Pow(pairs, ContentEstimator.HeapsBeta):N0} for {pairs:N0} pairs); "
                + string.Join(", ", st.Select(s => $"{s.Key} {s.Value.Docs} docs {s.Value.StoredBytes / Math.Max(1.0, s.Value.Docs):F0} B stored {s.Value.Pairs / Math.Max(1.0, s.Value.Docs):F0} words")));
        }
        double ratio = estimate.IndexBytes / (double)actual;
        output.WriteLine($"census: {string.Join(", ", census.Kinds.Select(k => $"{k.Key} {k.Value.Files}"))}");
        output.WriteLine($"calibration: {string.Join(", ", calibration.Costs.Select(c => $"{c.Key} {c.Value.StoredBytesPerFile:F0} B stored, {c.Value.WordsPerFile:F0} words, {c.Value.MsPerFile:F1} ms (n={calibration.Measured.GetValueOrDefault(c.Key)})"))}");
        output.WriteLine($"estimate {estimate.IndexBytes:N0} B (Settings: {live.IndexBytes:N0} B) vs index {actual:N0} B: ratio {ratio:F2}; first pass estimated {estimate.FirstPass.TotalMilliseconds:F0} ms, built in {clock.ElapsedMilliseconds} ms");
        Assert.InRange(ratio, 0.5, 2.0);
        Assert.InRange(live.IndexBytes / (double)actual, 0.5, 2.0);
    }
}
