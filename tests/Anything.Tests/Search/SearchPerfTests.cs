using System.Diagnostics;
using Anything.Core;
using Anything.Core.Search;
using Xunit.Abstractions;

namespace Anything.Tests.Search;

public class SearchPerfTests(ITestOutputHelper output)
{
    static readonly string[] Words = ["alpha", "beta", "gamma", "delta", "report", "image", "music", "video", "notes", "backup", "Café", "draft"];
    static readonly string[] Exts = ["txt", "jpg", "mp3", "dll", "cs", "pdf", "png", "exe", "json", "xml"];

    /// <summary>Synthetic tree: 1,000 folders under 10 top-level folders, 1,000 files each (~1M entries).</summary>
    internal static FileIndex BuildLarge(int folders, int filesPerFolder)
    {
        var index = new FileIndex();
        var rnd = new Random(1234);
        index.BulkAdd(add =>
        {
            int root = add.Add("C:", FileIndex.NoParent, FileAttributes.Directory, -1, 0, 0);
            var tops = new int[10];
            for (int t = 0; t < tops.Length; t++) tops[t] = add.Add("top" + t, root, FileAttributes.Directory, -1, 0, 0);
            for (int f = 0; f < folders; f++)
            {
                int folder = add.Add($"{Words[f % Words.Length]}_folder{f}", tops[f % tops.Length], FileAttributes.Directory, -1, 0, 0);
                for (int i = 0; i < filesPerFolder; i++)
                {
                    string name = $"{Words[rnd.Next(Words.Length)]}_{rnd.Next(100000)}_{Words[rnd.Next(Words.Length)]}.{Exts[rnd.Next(Exts.Length)]}";
                    add.Add(name, folder, FileAttributes.Archive, rnd.Next(10_000_000), 133_500_000_000_000_000L + rnd.Next(), 0);
                }
            }
        });
        return index;
    }

    [Fact]
    public void Substring_search_over_a_million_entries_is_fast()
    {
        var index = BuildLarge(1000, 1000);
        var engine = new SearchEngine();
        var opts = new SearchOptions();

        // Warm up (JIT, fold tables).
        engine.Search(index, "warmup", opts, SortKey.Name, false, default);

        double Time(string q, SortKey sort = SortKey.Name, SearchOptions? o = null)
        {
            var sw = Stopwatch.StartNew();
            var r = engine.Search(index, q, o ?? opts, sort, false, default);
            sw.Stop();
            Assert.Null(r.Error);
            output.WriteLine($"{q,-28} sort={sort,-12} results={r.Ids.Length,9:N0}  {sw.Elapsed.TotalMilliseconds,8:F1} ms");
            return sw.Elapsed.TotalMilliseconds;
        }

        output.WriteLine($"entries: {index.LiveCount:N0}, cores: {Environment.ProcessorCount}");
        double substring = Time("12345");
        Time("alpha_123");
        Time("cafe");
        Time("*.mp3");
        Time("ext:jpg;png size:>5mb");
        Time(@"folder12\");
        Time("regex:^gamma_9+_");
        Time("alpha", SortKey.Name);
        Time("file:", SortKey.Size);
        Time("", SortKey.Name);
        Time("", SortKey.Path);

        Assert.True(substring < 2000, $"substring search took {substring} ms");
    }
}
