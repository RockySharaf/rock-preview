using System.Diagnostics;
using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Extract;
using Xunit.Abstractions;

namespace Anything.Tests.Content.Readers;

/// <summary>
/// Hostile input: mutations and truncations of every fixture (uncompressed variants, so mutations reach the readers' own
/// code) must never let an exception out, run past the time budget or allocate past the limit.
/// </summary>
public class ReaderFuzzTests
{
    /// <summary>Mutations per fixture: 1,000, or RP_FUZZ_MUTATIONS for a longer manual run.</summary>
    static readonly int Mutations = int.TryParse(Environment.GetEnvironmentVariable("RP_FUZZ_MUTATIONS"), out int n) && n > 0 ? n : 1000;
    const long MaxAllocation = 100L << 20;
    static readonly ExtractLimits Limits = new(MaxMilliseconds: 1000);

    static readonly Lazy<(string Name, string Extension, byte[] Bytes)[]> Fixtures = new(() =>
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        var rtf = "{\\rtf1\\ansi\\ansicpg1252{\\fonttbl{\\f0\\fcharset0 Arial;}{\\f1\\fcharset128 Gothic;}}{\\info{\\title T}}" +
            "\\f1\\'93\\'fa\\f0 words \\u20320?\\par {\\pict\\bin4 abcd}{\\*\\fldinst x}{\\upr{a}{\\*\\ud{b}}}end}";
        var html = "<html><head><title>T &amp; t</title><meta name=keywords content='a,b'></head><body><p>caf&eacute;<table><tr><td>x</td></tr></table>" +
            "<script>no</script><pre>p</pre>&#x1F600;</body></html>";
        var eml = "Subject: =?utf-8?B?4oKs?= x\r\nFrom: a@b\r\nContent-Type: multipart/mixed; boundary=\"b\"\r\n\r\n--b\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\nContent-Transfer-Encoding: quoted-printable\r\n\r\ncaf=C3=A9 soft=\r\nline\r\n--b\r\n" +
            "Content-Type: text/html\r\nContent-Transfer-Encoding: base64\r\n\r\nPHA+aHRtbDwvcD4=\r\n--b\r\nContent-Disposition: attachment; " +
            "filename*=UTF-8''%E2%82%AC.pdf\r\n\r\nAAAA\r\n--b--\r\n";
        var smallIndd = new byte[64 << 10];
        Encoding.UTF8.GetBytes(OfficeFixtures.XmpPacket("Small", "application/x-indesign", "k")).CopyTo(smallIndd, 30_000);
        return
        [
            ("text", "txt", Encoding.UTF8.GetBytes("plain words\r\nline two\fpage\rthree")),
            ("utf16", "txt", Encoding.Unicode.GetPreamble().Concat(Encoding.Unicode.GetBytes("wide words ünd mehr")).ToArray()),
            ("html", "html", Encoding.UTF8.GetBytes(html)),
            ("rtf", "rtf", Encoding.ASCII.GetBytes(rtf)),
            ("docx", "docx", OfficeFixtures.Docx(stored: true)),
            ("docx-deflated", "docx", OfficeFixtures.Docx()),
            ("xlsx-deflated", "xlsx", OfficeFixtures.Xlsx()),
            ("xlsx", "xlsx", OfficeFixtures.Xlsx(stored: true)),
            ("pptx", "pptx", OfficeFixtures.Pptx(stored: true)),
            ("odt", "odt", OfficeFixtures.Odt(stored: true)),
            ("ods", "ods", OfficeFixtures.Ods(stored: true)),
            ("odp", "odp", OfficeFixtures.Odp(stored: true)),
            ("idml", "idml", OfficeFixtures.Idml(stored: true)),
            ("eml", "eml", Encoding.ASCII.GetBytes(eml)),
            ("eml-generated", "eml", MailReadersTests.GeneratedMessage()),
            ("msg", "msg", MailReadersTests.Msg()),
            ("msg-rtf", "msg", MailReadersTests.Msg(compressedRtf: true)),
            ("jpeg", "jpg", PictureFixtures.Jpeg(components: 4, adobeTransform: 2)),
            ("tiff-ii", "tif", PictureFixtures.Tiff(false)),
            ("tiff-mm", "tif", PictureFixtures.Tiff(true)),
            ("bigtiff", "tif", PictureFixtures.Tiff(false, bigTiff: true)),
            ("png", "png", PictureFixtures.Png()),
            ("gif", "gif", PictureFixtures.Gif()),
            ("bmp", "bmp", PictureFixtures.Bmp()),
            ("webp", "webp", PictureFixtures.WebP()),
            ("heic", "heic", PictureFixtures.Heic()),
            ("psd", "psd", DesignFixtures.Psd()),
            ("psb16", "psb", DesignFixtures.Psd(psb: true, depth: 16)),
            ("indd", "indd", smallIndd),
            ("eps", "eps", DesignFixtures.Eps(dos: true)),
        ];
    });

    /// <summary>Every fixture: the fuzz inputs, and the timing test's documents and pictures.</summary>
    internal static IReadOnlyList<(string Name, string Extension, byte[] Bytes)> All => Fixtures.Value;

    public static IEnumerable<object[]> Names() => Fixtures.Value.Select(f => new object[] { f.Name });

    static (string Name, string Extension, byte[] Bytes) Fixture(string name) => Fixtures.Value.Single(f => f.Name == name);

    /// <summary>
    /// Runs one input as a task on its own thread (a long-running task, so a busy pool can't delay its start) and waits
    /// twice the time budget from the moment it starts; fails with the seed and a hex head when it runs past that, lets
    /// an exception out, or allocates past the limit.
    /// </summary>
    static ExtractedDoc RunGuarded(byte[] input, string extension, string what)
    {
        long allocated = 0;
        ExtractedDoc? doc = null;
        Exception? error = null;
        using var started = new ManualResetEventSlim();
        var task = Task.Factory.StartNew(() =>
        {
            started.Set();
            long before = GC.GetAllocatedBytesForCurrentThread();
            try { doc = ReaderRun.Bytes(input, extension, Limits); }
            catch (Exception e) { error = e; }
            allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        started.Wait();
        if (!task.Wait(2 * Limits.MaxMilliseconds))
            throw new Xunit.Sdk.XunitException($"{what}: ran past its time budget; head {Convert.ToHexString(input.AsSpan(0, Math.Min(64, input.Length)))}");
        if (error != null) throw new Xunit.Sdk.XunitException($"{what}: {error.GetType().Name} escaped: {error.Message}");
        Assert.True(allocated < MaxAllocation, $"{what}: allocated {allocated:N0} bytes");
        return doc!;
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Fixture_reads_cleanly(string name)
    {
        var (_, extension, bytes) = Fixture(name);
        var doc = RunGuarded(bytes, extension, name);
        Assert.True(doc.Status is DocStatus.Ok or DocStatus.NoText or DocStatus.Truncated, $"{name}: {doc.Status}");
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Mutations_never_escape(string name)
    {
        var (_, extension, bytes) = Fixture(name);
        int seed = name.GetHashCode(StringComparison.Ordinal) & 0x7FFFFFFF;
        var random = new Random(seed);
        uint[] interesting = [0, 1, 0x7F, 0xFF, 0x7FFF, 0xFFFF, 0x7FFFFFFF, 0x80000000, 0xFFFFFFFF, 0xFFFFFFFE, 16, 4096];
        for (int i = 0; i < Mutations; i++)
        {
            var m = (byte[])bytes.Clone();
            switch (random.Next(4))
            {
                case 0:
                    for (int k = random.Next(1, 17); k > 0; k--) m[random.Next(m.Length)] = (byte)random.Next(256);
                    break;
                case 1:
                {
                    int at = random.Next(Math.Max(1, m.Length - 4));
                    BitConverter.GetBytes(interesting[random.Next(interesting.Length)]).AsSpan(0, Math.Min(4, m.Length - at)).CopyTo(m.AsSpan(at));
                    if (random.Next(2) == 0) m.AsSpan(at, Math.Min(4, m.Length - at)).Reverse();
                    break;
                }
                case 2:
                    m = m[..random.Next(m.Length)];
                    break;
                default:
                {
                    int from = random.Next(m.Length), length = random.Next(Math.Min(512, m.Length - from) + 1), to = random.Next(m.Length);
                    m = [.. m[..to], .. m.AsSpan(from, length), .. m[to..]];
                    break;
                }
            }
            RunGuarded(m, extension, $"{name} mutation {i} (seed {seed})");
        }
    }

    [Theory]
    [MemberData(nameof(Names))]
    public void Truncation_at_every_kilobyte_never_escapes(string name)
    {
        var (_, extension, bytes) = Fixture(name);
        int step = Math.Max(1024, bytes.Length / 256);
        for (int cut = 0; cut < bytes.Length; cut += step)
            RunGuarded(bytes[..cut], extension, $"{name} cut at {cut}");
    }

}

/// <summary>The reader timing test runs alone: other test classes running in parallel would make its numbers noise.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class ReaderTimingCollection
{
    public const string Name = "Reader timing";
}

/// <summary>U3's done criterion as it stands: each reader's p95 on the fixtures under 15 ms, pictures under 3 ms.</summary>
[Collection(ReaderTimingCollection.Name)]
public class ReaderTimingTests(ITestOutputHelper output)
{
    [Fact]
    public void Readers_are_fast_on_fixtures()
    {
        var lines = new List<string>();
        var slow = new List<string>();
        foreach (var (name, extension, bytes) in ReaderFuzzTests.All)
        {
            for (int i = 0; i < 3; i++) ReaderRun.Bytes(bytes, extension); // warm up (JIT)
            var times = new List<double>();
            for (int i = 0; i < 40; i++)
            {
                var sw = Stopwatch.StartNew();
                ReaderRun.Bytes(bytes, extension);
                times.Add(sw.Elapsed.TotalMilliseconds);
            }
            times.Sort();
            double p95 = times[(int)(times.Count * 0.95) - 1];
            bool picture = ContentTypes.KindOf("x." + extension) == ContentKind.Picture;
            lines.Add($"{name}: p95 {p95:F2} ms");
            if (p95 >= (picture ? 3 : 15)) slow.Add($"{name}: p95 {p95:F2} ms");
        }
        output.WriteLine(string.Join(Environment.NewLine, lines));
        Assert.Empty(slow);
    }
}
