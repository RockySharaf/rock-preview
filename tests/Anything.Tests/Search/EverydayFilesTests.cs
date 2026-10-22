using Anything.Core;
using Anything.Core.Search;

namespace Anything.Tests.Search;

/// <summary><see cref="EverydayFiles"/>: the files the "Everyday files" toggle keeps in "every file in" a folder.</summary>
public sealed class EverydayFilesTests
{
    [Fact]
    public void Extensions_are_lower_case_without_dots_and_listed_once()
    {
        Assert.All(EverydayFiles.Extensions, e =>
        {
            Assert.Equal(e.ToLowerInvariant(), e);
            Assert.DoesNotContain('.', e);
            Assert.False(string.IsNullOrWhiteSpace(e));
        });
        Assert.Equal(EverydayFiles.Extensions.Count, EverydayFiles.Extensions.Distinct().Count());
    }

    [Theory]
    [InlineData("cs"), InlineData("js"), InlineData("ts"), InlineData("tsx"), InlineData("py"), InlineData("json"),
     InlineData("xml"), InlineData("html"), InlineData("css"), InlineData("md"), InlineData("yml"), InlineData("ini"),
     InlineData("log"), InlineData("dll"), InlineData("exe"), InlineData("pdb"), InlineData("obj"), InlineData("sln"),
     InlineData("csproj"), InlineData("key"), InlineData("dot"), InlineData("lock"), InlineData("map"), InlineData("tmp")]
    public void Code_settings_and_program_files_are_not_everyday(string extension)
    {
        Assert.DoesNotContain(extension, EverydayFiles.Extensions);
        Assert.False(EverydayFiles.IsEveryday("file." + extension));
    }

    [Theory]
    [InlineData("photo.jpg", true), InlineData("PHOTO.JPG", true), InlineData("scan.pdf", true), InlineData("logo.psd", true),
     InlineData("raw.CR3", true), InlineData("clip.mp4", true), InlineData("book.epub", true), InlineData("a.tar.zip", true),
     InlineData("Program.cs", false), InlineData("jpg", false), InlineData("photo.", false), InlineData("", false),
     InlineData(".gitignore", false), InlineData("archive.tar.gz", false), InlineData(".png", false), InlineData(".config.png", true)]
    public void IsEveryday_goes_by_the_last_extension(string name, bool expected)
        => Assert.Equal(expected, EverydayFiles.IsEveryday(name));

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
        Assert.Equal(EverydayFiles.Extensions.Contains(tree.Index.Extension(id)), EverydayFiles.IsEveryday(name));
    }

    [Theory]
    [InlineData("mts"), InlineData("m2ts"), InlineData("jxr"), InlineData("wdp"), InlineData("hdp"), InlineData("pub")]
    public void Camcorder_videos_HDR_screenshots_and_Publisher_files_are_everyday(string extension)
        => Assert.True(EverydayFiles.IsEveryday("file." + extension));

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
        var options = new SearchOptions { Filter = $"file: \"C:\\Project\\\" {EverydayFiles.Filter}" };
        var found = engine.Search(tree.Index, "", options, SortKey.Name, false, CancellationToken.None)
            .Ids.Select(tree.Index.GetFullPath).ToList();

        Assert.Equal(everyday.Order(StringComparer.OrdinalIgnoreCase), found.Order(StringComparer.OrdinalIgnoreCase));
    }
}
