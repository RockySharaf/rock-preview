using Anything.Core;
using Anything.Core.Search;

namespace Anything.Tests.Search;

/// <summary><see cref="EverydayFiles"/>: the files the "Everyday files" toggle keeps in "every file in" a folder.</summary>
public sealed class EverydayFilesTests
{
    [Fact]
    public void Extensions_are_lower_case_without_dots_and_listed_once()
    {
        Assert.All(EverydayFiles.Default.Extensions, e =>
        {
            Assert.Equal(e.ToLowerInvariant(), e);
            Assert.DoesNotContain('.', e);
            Assert.False(string.IsNullOrWhiteSpace(e));
        });
        Assert.Equal(EverydayFiles.Default.Extensions.Count, EverydayFiles.Default.Extensions.Distinct().Count());
    }

    [Theory]
    [InlineData("cs"), InlineData("js"), InlineData("ts"), InlineData("tsx"), InlineData("py"), InlineData("json"),
     InlineData("xml"), InlineData("html"), InlineData("css"), InlineData("md"), InlineData("yml"), InlineData("ini"),
     InlineData("log"), InlineData("dll"), InlineData("exe"), InlineData("pdb"), InlineData("obj"), InlineData("sln"),
     InlineData("csproj"), InlineData("key"), InlineData("dot"), InlineData("lock"), InlineData("map"), InlineData("tmp")]
    public void Code_settings_and_program_files_are_not_everyday(string extension)
    {
        Assert.DoesNotContain(extension, EverydayFiles.Default.Extensions);
        Assert.False(EverydayFiles.Default.IsEveryday("file." + extension));
    }

    [Theory]
    [InlineData("photo.jpg", true), InlineData("PHOTO.JPG", true), InlineData("scan.pdf", true), InlineData("logo.psd", true),
     InlineData("raw.CR3", true), InlineData("clip.mp4", true), InlineData("book.epub", true), InlineData("a.tar.zip", true),
     InlineData("Program.cs", false), InlineData("jpg", false), InlineData("photo.", false), InlineData("", false),
     InlineData(".gitignore", false), InlineData("archive.tar.gz", false), InlineData(".png", false), InlineData(".config.png", true)]
    public void IsEveryday_goes_by_the_last_extension(string name, bool expected)
        => Assert.Equal(expected, EverydayFiles.Default.IsEveryday(name));

    [Theory]
    [InlineData("photo.jpg"), InlineData(".png"), InlineData("..png"), InlineData(".config.png"), InlineData("photo."),
     InlineData("png"), InlineData("Report.Final.PDF"), InlineData("archive.tar.gz")]
    public void IsEveryday_finds_the_extension_the_index_finds(string name)
    {
        // The disk check (IsEveryday) and the search's ext: filter (the index's extension) must agree, or the check
        // reads a folder from the disk for a view that stays empty.
        var tree = new TreeBuilder();
        tree.Folder("C:");
        int id = tree.File(@"C:\" + name);
        Assert.Equal(EverydayFiles.Default.Extensions.Contains(tree.Index.Extension(id)), EverydayFiles.Default.IsEveryday(name));
    }

    [Theory]
    [InlineData("mts"), InlineData("m2ts"), InlineData("jxr"), InlineData("wdp"), InlineData("hdp"), InlineData("pub")]
    public void Camcorder_videos_HDR_screenshots_and_Publisher_files_are_everyday(string extension)
        => Assert.True(EverydayFiles.Default.IsEveryday("file." + extension));

    [Fact]
    public void Filter_keeps_only_everyday_files_in_a_search()
    {
        var tree = new TreeBuilder();
        tree.Folder("C:");
        tree.Folder(@"C:\Project");
        string[] everyday = [@"C:\Project\cover.png", @"C:\Project\Brief.PDF", @"C:\Project\logo.ai", @"C:\Project\demo.mov"];
        string[] other = [@"C:\Project\Program.cs", @"C:\Project\app.ts", @"C:\Project\package.json", @"C:\Project\build.log",
                          @"C:\Project\README.md", @"C:\Project\Makefile", @"C:\Project\.png"];
        foreach (var f in everyday.Concat(other)) tree.File(f, 1);
        tree.Folder(@"C:\Project\images.png"); // a folder named like a picture is still a folder

        var engine = new SearchEngine();
        var options = new SearchOptions { Filter = $"file: \"C:\\Project\\\" {EverydayFiles.Default.Filter}" };
        var found = engine.Search(tree.Index, "", options, SortKey.Name, false, CancellationToken.None)
            .Ids.Select(tree.Index.GetFullPath).ToList();

        Assert.Equal(everyday.Order(StringComparer.OrdinalIgnoreCase), found.Order(StringComparer.OrdinalIgnoreCase));
    }
    // ---------------- chosen in Settings › Thumbnails ----------------

    static readonly string[] AllKeys = EverydayFiles.Kinds.Select(k => k.Key).ToArray();

    [Fact]
    public void Kinds_have_their_own_keys_and_extensions()
    {
        Assert.Equal(AllKeys.Length, AllKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        var all = EverydayFiles.Kinds.SelectMany(k => k.Extensions).ToList();
        Assert.Equal(all.Count, all.Distinct().Count()); // an extension belongs to one kind, so turning a kind off drops it
        Assert.Equal(all, EverydayFiles.Default.Extensions);
        Assert.Equal(AllKeys, EverydayFiles.Default.KindsOn);
        Assert.Empty(EverydayFiles.Default.Added);
    }

    [Fact]
    public void A_kind_turned_off_drops_its_extensions()
    {
        var noPdf = new EverydayFiles(AllKeys.Where(k => k != "pdf"), null);
        Assert.False(noPdf.IsEveryday("scan.pdf"));
        Assert.False(noPdf.IsEveryday("print.xps"));
        Assert.True(noPdf.IsEveryday("photo.jpg"));
        Assert.True(noPdf.IsEveryday("letter.docx"));
        Assert.DoesNotContain("pdf", noPdf.Filter.Split(':', ';'));
        // Unknown keys (from a later version, say) are ignored; known ones in any case count.
        var picturesOnly = new EverydayFiles(["PICTURES", "holograms"], null);
        Assert.Equal(["pictures"], picturesOnly.KindsOn);
        Assert.True(picturesOnly.IsEveryday("a.png"));
        Assert.False(picturesOnly.IsEveryday("a.mp4"));
    }

    [Fact]
    public void Added_extensions_count_too_cleaned_and_once()
    {
        var e = new EverydayFiles([], ["*.MD", ".json", " Log ", "md", "", "  "]);
        Assert.Equal(["md", "json", "log"], e.Added);
        Assert.Equal(["md", "json", "log"], e.Extensions);
        Assert.True(e.IsEveryday("README.md"));
        Assert.True(e.IsEveryday("build.LOG"));
        Assert.False(e.IsEveryday("photo.jpg"));
        Assert.Equal("ext:md;json;log", e.Filter);
        // Added to a kind that has it already: listed once.
        Assert.Equal(EverydayFiles.Default.Extensions.Count, new EverydayFiles(null, ["PNG"]).Extensions.Count);
    }

    [Theory]
    [InlineData("md json", new[] { "md", "json" })]
    [InlineData("md, json;  *.TXT |log", new[] { "md", "json", "txt", "log" })]
    [InlineData(" .md ;; .MD ", new[] { "md" })]
    [InlineData("", new string[0])]
    [InlineData("*.* ; .", new string[0])]
    public void Typed_extensions_are_read_like_this(string typed, string[] expected)
        => Assert.Equal(expected, EverydayFiles.ParseExtensions(typed));

    [Fact]
    public void Nothing_chosen_keeps_no_file()
    {
        var none = new EverydayFiles([], []);
        Assert.Empty(none.Extensions);
        Assert.False(none.IsEveryday("photo.jpg"));
        var tree = new TreeBuilder();
        tree.Folder("C:");
        tree.File(@"C:\photo.jpg", 1);
        tree.File(@"C:\Makefile", 1); // no extension: "ext:" alone would keep it
        var found = new SearchEngine().Search(tree.Index, "", new SearchOptions { Filter = none.Filter }, SortKey.Name, false, CancellationToken.None);
        Assert.Empty(found.Ids);
    }

    [Fact]
    public void Search_thumbnails_keep_the_everyday_files_inside_a_folder_found()
    {
        var tree = new TreeBuilder();
        tree.Folder("C:");
        tree.Folder(@"C:\Rocky");
        tree.Folder(@"C:\Rocky\site");
        foreach (var f in new[] { @"C:\Rocky\cover.png", @"C:\Rocky\notes.pdf", @"C:\Rocky\site\.eslintrc", @"C:\Rocky\site\app.js",
                                  @"C:\Rocky\site\.gitignore", @"C:\Rocky\site\hero.webp" })
            tree.File(f, 1);
        string[] Find(EverydayFiles everyday) => new SearchEngine()
            .Search(tree.Index, "rocky", new SearchOptions { InsideFolders = true, Filter = everyday.Filter }, SortKey.Name, false, CancellationToken.None)
            .Ids.Select(tree.Index.GetFullPath).Order(StringComparer.OrdinalIgnoreCase).ToArray();

        Assert.Equal([@"C:\Rocky\cover.png", @"C:\Rocky\notes.pdf", @"C:\Rocky\site\hero.webp"], Find(EverydayFiles.Default));
        Assert.Equal([@"C:\Rocky\cover.png", @"C:\Rocky\site\hero.webp"], Find(new EverydayFiles(AllKeys.Where(k => k != "pdf"), null)));
        Assert.Equal([@"C:\Rocky\site\app.js"], Find(new EverydayFiles([], ["js"])));
    }
}
