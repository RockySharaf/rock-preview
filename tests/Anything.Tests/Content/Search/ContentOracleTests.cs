using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Search;

namespace Anything.Tests.Content.Search;

/// <summary>
/// The index's answers against brute force: for random documents and queries, content: must find exactly the documents
/// whose stored text FindSequence matches under the same rules (no candidate lost by the dictionary lookups, no document
/// let through that the text doesn't confirm), and hostile queries never throw.
/// </summary>
public class ContentOracleTests
{
    static readonly string[] Vocabulary =
    [
        "alpha", "Alpha", "alphabet", "beta", "gamma", "café", "Café", "cafe", "resume", "résumé", "INV-2024-001", "INV 2024 001",
        "inv2024001", "INV2024", "INV-2025-7", "report.pdf", "report", "Report", "reporting", "東京都", "京都", "東京", "大阪",
        "don't", "dont", "a1", "2024", "x", "naïve", "naive", "e-mail", "email", "co-op", "Straße", "strasse", "ΑΘΗΝΑ", "αθηνα",
        "iPhone15", "iPhone15を", "สวัสดี", "price: 20", "v1.2.3", "v123",
    ];

    static readonly string[] Separators = [" ", " ", " ", "\n", ", ", ". ", " - ", "\t", "\f", "; "];

    /// <summary>A fixture for one test: fakes here; the integration tests run the same tests on a real ContentStore.</summary>
    private protected virtual ContentFixture NewFixture() => new();

    (ContentFixture Fixture, List<(string Path, string Body, string Title)> Docs) Corpus(int seed, int count)
    {
        var rnd = new Random(seed);
        var f = NewFixture();
        var docs = new List<(string, string, string)>();
        for (int i = 0; i < count; i++)
        {
            if (i > 0 && i % 40 == 0) f.NextSegment();
            var sb = new System.Text.StringBuilder();
            int words = rnd.Next(1, 9);
            for (int w = 0; w < words; w++)
            {
                if (w > 0) sb.Append(Separators[rnd.Next(Separators.Length)]);
                sb.Append(Vocabulary[rnd.Next(Vocabulary.Length)]);
            }
            string title = rnd.Next(4) == 0 ? Vocabulary[rnd.Next(Vocabulary.Length)] : "";
            string path = $@"C:\Corpus\doc{i:D3}.txt";
            f.Doc(path, sb.ToString(), title.Length > 0 ? [(DocField.Title, title)] : null);
            docs.Add((path, sb.ToString(), title));
        }
        f.Publish();
        return (f, docs);
    }

    static bool Oracle(string text, QueryWord word, bool matchCase, bool matchDiacritics, bool wholeWord)
        => text.Length > 0 && ContentTokenizer.FindSequence(text, word, matchCase, matchDiacritics, wholeWord).Start >= 0;

    public static TheoryData<int> Seeds => [1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12];

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Explicit_content_terms_find_exactly_what_the_text_has(int seed)
    {
        var (f, docs) = Corpus(seed, 160);
        var rnd = new Random(seed * 31);
        var queries = new List<string>();
        foreach (var v in Vocabulary)
        {
            if (v.Length < 2 && !char.IsLetter(v[0])) continue;
            queries.Add(v);
            if (v.Length > 3) queries.Add(v[..3]);
        }
        for (int i = 0; i < 40; i++) queries.Add(Vocabulary[rnd.Next(Vocabulary.Length)] + " " + Vocabulary[rnd.Next(Vocabulary.Length)]);

        int checkedQueries = 0;
        foreach (var value in queries)
            foreach (var (prefix, mc, md, ww) in new[] { ("", false, false, false), ("ww:", false, false, true), ("case:", true, false, false), ("diacritics:", false, true, false) })
            {
                string query = prefix + "content:\"" + value + "\"";
                var r = f.Run(query);
                if (r.Error != null) continue; // a value the index can't look up (one letter): an error, not a wrong answer
                var word = ContentTokenizer.Analyze(value, lastIsPrefix: !ww);
                var expected = docs.Where(d => Oracle(d.Body, word, mc, md, ww) || Oracle(d.Title, word, mc, md, ww)).Select(d => d.Path)
                    .Order(StringComparer.OrdinalIgnoreCase).ToArray();
                var actual = r.Ids.Select(f.Index.GetFullPath).Order(StringComparer.OrdinalIgnoreCase).ToArray();
                Assert.True(expected.SequenceEqual(actual, StringComparer.OrdinalIgnoreCase),
                    $"[{query}] missing: {string.Join(", ", expected.Except(actual))}; extra: {string.Join(", ", actual.Except(expected))}");
                checkedQueries++;
            }
        Assert.True(checkedQueries > 150, $"only {checkedQueries} queries checked");
    }

    [Theory]
    [MemberData(nameof(Seeds))]
    public void Plain_words_find_names_or_what_the_dictionary_has_and_never_more_than_the_text(int seed)
    {
        var (f, docs) = Corpus(seed, 120);
        foreach (var value in Vocabulary.Where(v => v.Length >= 3))
        {
            var r = f.Run("\"" + value + "\"", ContentFixture.Plain);
            Assert.Null(r.Error);
            var word = ContentTokenizer.Analyze(value, lastIsPrefix: true);
            // A plain word's content side is the dictionary only (A9), so it may find less than the text has, never more:
            // every content match is confirmed by the text with the index's own rules.
            foreach (var id in r.Ids.Skip(Math.Max(0, r.NameMatches)))
            {
                var doc = docs.Single(d => d.Path == f.Index.GetFullPath(id));
                Assert.True(Oracle(doc.Body, word, false, false, false) || Oracle(doc.Title, word, false, false, false)
                    || Oracle(doc.Body, ContentTokenizer.Analyze(Core.Content.Search.ContentPlan.WithoutShortTrailingParts(value), true), false, false, false),
                    $"[{value}] {doc.Path} has no such text: {doc.Body} / {doc.Title}");
            }
        }
    }

    static readonly string[] Fragments =
    [
        "content:", "title:", "keywords:", "camera:", "dpi:", "width:", "dimensions:", "colormode:", "datetaken:", "pagecount:",
        "contentstatus:", "cmyk:", "gps:", "rgb:", "ww:", "case:", "diacritics:", "regex:", "nowildcards:", "path:", "!", "<", ">", "|",
        "\"", "*", "?", ";", "..", "x", "1920x1080", ">=", "<=", "!=", "=", "INV-", "2024", "東京", "\u0301", "\uD800", "\uDC00", "\u200D",
        "\u00AD", " ", "  ", "-", ".", "'", "@", ":", "\\", "report", "a", "ÿ", "\u0E31", "lastweek", "unknown", "srgb", "failed",
        "\u0000", "\uFFFF", "ＡＢＣ", "Ⅻ", "٣", "\t", "\f", "dupe:", "ext:", "child:",
    ];

    [Fact]
    public void Hostile_queries_never_throw_and_highlight_safely()
    {
        var (f, docs) = Corpus(9, 80);
        var rnd = new Random(1234);
        for (int i = 0; i < 3000; i++)
        {
            var sb = new System.Text.StringBuilder();
            int parts = rnd.Next(1, 8);
            for (int p = 0; p < parts; p++) sb.Append(Fragments[rnd.Next(Fragments.Length)]);
            string query = sb.ToString();
            var options = new SearchOptions { MatchContent = rnd.Next(2) == 0, MatchCase = rnd.Next(5) == 0, MatchWholeWord = rnd.Next(5) == 0 };
            var r = f.Run(query, options); // only QueryException becomes an error; anything else would throw here
            Assert.NotNull(r.Ids);
            var h = ContentHighlighter.Create(query, options);
            if (h == null) continue;
            var doc = docs[rnd.Next(docs.Count)];
            foreach (var (start, length) in h.Find(doc.Body))
                Assert.True(start >= 0 && length > 0 && start + length <= doc.Body.Length, $"[{query}] bad range in {doc.Body}");
            _ = h.FirstMatch(doc.Body, rnd.Next(doc.Body.Length + 1), out _);
            _ = h.PictureConditions;
            var snippet = Core.Content.Search.SnippetFormatter.Format(new Core.Content.Store.StoredDoc(doc.Body, [(DocField.Title, doc.Title)], null),
                ContentKind.Text, "txt", 0, null, h, rnd.Next(1, 400));
            if (snippet != null)
                foreach (var (start, length) in snippet.Highlights) Assert.True(start >= 0 && start + length <= snippet.Text.Length);
        }
    }
}
