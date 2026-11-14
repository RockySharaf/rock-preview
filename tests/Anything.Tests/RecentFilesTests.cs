using Anything.Core;

namespace Anything.Tests;

/// <summary>Home's recent files: the Recent folder's shortcuts, newest first, files only, each once.</summary>
public sealed class RecentFilesTests : IDisposable
{
    readonly string _folder = Path.Combine(Path.GetTempPath(), "RockPreviewRecent-" + Guid.NewGuid().ToString("N"));

    public RecentFilesTests() => Directory.CreateDirectory(_folder);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch (IOException) { }
    }

    /// <summary>A shortcut whose "target" is its content (the test's resolver reads it), written some minutes ago.</summary>
    void Link(string name, string target, int minutesAgo)
    {
        string path = Path.Combine(_folder, name + ".lnk");
        File.WriteAllText(path, target);
        File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(-minutesAgo));
    }

    List<string> Read(HashSet<string> files, int limit = RecentFiles.Limit)
        => RecentFiles.Read(_folder, File.ReadAllText, files.Contains, limit);

    [Fact]
    public void Newest_first_files_only_each_once()
    {
        Link("report", @"C:\Docs\report.docx", 30);
        Link("photo", @"D:\Pictures\photo.jpg", 5);
        Link("docs", @"C:\Docs", 1);                       // a folder: left out
        Link("report (2)", @"C:\Docs\report.docx", 60);    // the same file again, older
        Link("gone", @"C:\Old\deleted.pdf", 2);            // not there any more
        Link("web", "https://example.com", 3);             // not a file
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { @"C:\Docs\report.docx", @"D:\Pictures\photo.jpg" };
        Assert.Equal([@"D:\Pictures\photo.jpg", @"C:\Docs\report.docx"], Read(files));
    }

    [Fact]
    public void At_most_the_limit()
    {
        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < 10; i++)
        {
            Link("f" + i, $@"C:\F\{i}.txt", i);
            files.Add($@"C:\F\{i}.txt");
        }
        Assert.Equal([@"C:\F\0.txt", @"C:\F\1.txt", @"C:\F\2.txt"], Read(files, limit: 3));
    }

    [Fact]
    public void No_recent_folder_is_no_files()
        => Assert.Empty(RecentFiles.Read(Path.Combine(_folder, "missing"), File.ReadAllText, _ => true));

    [Fact]
    public void No_files_finds_nothing()
        => Assert.Equal("file: !file:", RecentFiles.Filter([]));
}
