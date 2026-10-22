using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Extract;

namespace Anything.Tests.Content.Readers;

public sealed class FileContentSourceTests : IDisposable
{
    readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("rp-content-");

    public void Dispose()
    {
        try { _dir.Delete(recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    string Write(string name, byte[] bytes)
    {
        string path = Path.Combine(_dir.FullName, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    static ExtractRequest Request(string path, ContentKind kind = ContentKind.Text, long maxBytes = 0, bool network = false, bool encrypted = false)
        => new(path, kind, -1, 0, network, encrypted, maxBytes, network ? ExtractLimits.Network : ExtractLimits.Local);

    [Fact]
    public void Reads_a_file_and_reports_its_stamp_without_changing_date_accessed()
    {
        string path = Write("notes.txt", Encoding.UTF8.GetBytes("hello inside words"));
        var old = new DateTime(2020, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastAccessTimeUtc(path, old);
        var read = new FileContentSource().Read(Request(path), CancellationToken.None);
        Assert.False(read.Retry);
        Assert.False(read.Offline);
        Assert.Equal(DocStatus.Ok, read.Doc.Status);
        Assert.Equal("hello inside words", read.Doc.Body);
        Assert.Equal(new FileInfo(path).Length, read.Size);
        Assert.Equal(File.GetLastWriteTimeUtc(path).ToFileTimeUtc(), read.ModifiedUtc);
        Assert.Equal(18, read.Doc.BytesRead);
        Assert.Equal(old, File.GetLastAccessTimeUtc(path));
    }

    [Fact]
    public void Attributes_decide_before_the_file_is_opened()
    {
        string path = Write("doc.txt", Encoding.UTF8.GetBytes("words"));
        int opened = 0;
        FileContentSource With(uint bits) => new(null, (_, a) => a | (FileAttributes)bits, _ => opened++);
        Assert.Equal(DocStatus.CloudOnly, With(0x400000).Read(Request(path), default).Doc.Status);
        Assert.Equal(DocStatus.CloudOnly, With(0x1000).Read(Request(path), default).Doc.Status);
        Assert.Equal(DocStatus.EfsEncrypted, With(0x4000).Read(Request(path), default).Doc.Status);
        Assert.Equal(DocStatus.Ok, With(0x4000).Read(Request(path, encrypted: true), default).Doc.Status);
        Assert.Equal(1, opened); // only the allowed EFS read opened the file

        File.SetAttributes(path, FileAttributes.NotContentIndexed);
        Assert.Equal(DocStatus.OptedOut, new FileContentSource().Read(Request(path), default).Doc.Status);
    }

    [Fact]
    public void Size_limit_gives_TooBig_except_for_pictures()
    {
        string doc = Write("big.txt", new byte[100]);
        Assert.Equal(DocStatus.TooBig, new FileContentSource().Read(Request(doc, maxBytes: 10), default).Doc.Status);
        string picture = Write("big.png", PictureFixtures.Png());
        var read = new FileContentSource().Read(Request(picture, ContentKind.Picture, maxBytes: 10), default);
        Assert.Equal(DocStatus.NoText, read.Doc.Status);
        Assert.Equal(1200, read.Doc.Picture!.Value.Width);
    }

    [Fact]
    public void A_change_during_the_read_gives_Retry()
    {
        string path = Write("changing.txt", Encoding.UTF8.GetBytes("before words"));
        var source = new FileContentSource(null, null, p =>
        {
            using var s = new FileStream(p, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
            s.Write(Encoding.UTF8.GetBytes(" appended"));
        });
        var read = source.Read(Request(path), default);
        Assert.True(read.Retry);
        Assert.Equal(0, FileContentSource.LastIoError);
    }

    [Fact]
    public void Missing_files_retry_and_denied_files_are_AccessDenied()
    {
        var missing = new FileContentSource().Read(Request(Path.Combine(_dir.FullName, "gone.txt")), default, out int error);
        Assert.True(missing.Retry);
        Assert.Equal(2, error);

        string path = Write("secret.txt", Encoding.UTF8.GetBytes("secret words"));
        var file = new FileInfo(path);
        var user = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(user, FileSystemRights.ReadData, AccessControlType.Deny);
        var acl = file.GetAccessControl();
        acl.AddAccessRule(rule);
        file.SetAccessControl(acl);
        try
        {
            var read = new FileContentSource().Read(Request(path), default, out int denied);
            Assert.Equal(DocStatus.AccessDenied, read.Doc.Status);
            Assert.False(read.Retry);
            Assert.Equal(5, denied);
            Assert.Equal(5, FileContentSource.LastIoError);
            Assert.DoesNotContain("secret", read.Doc.Body);
        }
        finally
        {
            var a = file.GetAccessControl();
            a.RemoveAccessRule(rule);
            file.SetAccessControl(a);
        }
    }

    [Fact]
    public void Denied_write_attributes_still_reads_read_only()
    {
        string path = Write("readonly.txt", Encoding.UTF8.GetBytes("still readable"));
        var file = new FileInfo(path);
        var user = WindowsIdentity.GetCurrent().User!;
        var rule = new FileSystemAccessRule(user, FileSystemRights.WriteAttributes, AccessControlType.Deny);
        var acl = file.GetAccessControl();
        acl.AddAccessRule(rule);
        file.SetAccessControl(acl);
        try
        {
            var read = new FileContentSource().Read(Request(path), default);
            Assert.Equal("still readable", read.Doc.Body);
        }
        finally
        {
            var a = file.GetAccessControl();
            a.RemoveAccessRule(rule);
            file.SetAccessControl(a);
        }
    }

    [Fact]
    public void Write_attribute_refusals_are_remembered_per_share_for_a_day()
    {
        WriteAttributeMemory.Clear();
        Assert.True(WriteAttributeMemory.ShouldTry(@"\\NAS\JOBS", 0));
        WriteAttributeMemory.Denied(@"\\NAS\JOBS", 1000);
        Assert.False(WriteAttributeMemory.ShouldTry(@"\\nas\jobs", 5000));
        Assert.True(WriteAttributeMemory.ShouldTry(@"\\nas\other", 5000));
        Assert.True(WriteAttributeMemory.ShouldTry(@"\\NAS\JOBS", 1000 + WriteAttributeMemory.ReprobeMs));
        WriteAttributeMemory.Allowed(@"\\NAS\JOBS");
        Assert.True(WriteAttributeMemory.ShouldTry(@"\\NAS\JOBS", 2000));
        Assert.Equal(@"\\NAS\JOBS", FileByteSource.ShareOf(@"\\nas\jobs\2024\a.pdf"));
        Assert.Equal(@"\\NAS\JOBS", FileByteSource.ShareOf(@"\\?\UNC\nas\jobs\a.pdf"));
        Assert.Equal("Z:", FileByteSource.ShareOf(@"z:\folder\a.pdf"));
        Assert.Null(FileByteSource.ShareOf(@"relative\a.pdf"));
    }

    [Fact]
    public void A_file_held_exclusively_is_retried_with_its_sharing_violation()
    {
        string path = Write("locked.txt", Encoding.UTF8.GetBytes("locked words"));
        using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        {
            var read = new FileContentSource().Read(Request(path), default, out int error);
            Assert.True(read.Retry);
            Assert.Equal(32, error);
        }
        Assert.Equal("locked words", new FileContentSource().Read(Request(path), default).Doc.Body);
    }

    [Fact]
    public void Paths_longer_than_max_path_and_unicode_names_read()
    {
        string folder = _dir.FullName;
        for (int i = 0; i < 6; i++) folder = Path.Combine(folder, "sub-folder-with-a-long-name-" + i + "-ÄÖÜ-漢字");
        Directory.CreateDirectory(folder);
        string path = Path.Combine(folder, "Bericht über Größen 報告.txt");
        File.WriteAllText(path, "deep words");
        Assert.True(path.Length > 260);
        var read = new FileContentSource().Read(Request(path), default);
        Assert.Equal("deep words", read.Doc.Body);
        Assert.True(new FileContentSource().TryStat(path, out long size, out _, out _));
        Assert.Equal(10, size);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Empty_files_have_no_text(bool network)
    {
        var problems = new List<string>();
        foreach (var ext in new[] { "txt", "html", "rtf", "docx", "xlsx", "pptx", "odt", "idml", "eml", "msg", "jpg", "png", "tif", "heic", "psd", "eps", "indd", "pdf" })
        {
            string path = Write("empty." + ext, []);
            var kind = ContentTypes.KindOf("x." + ext);
            var read = new FileContentSource().Read(Request(path, kind, network: network), default);
            if (read.Retry || read.Offline || read.Doc.Status != DocStatus.NoText) problems.Add($"{ext}: {read.Doc.Status} retry={read.Retry}");
            Assert.Equal(ContentReaders.CurrentVersion(ext, kind), read.Doc.ExtractorVersion);
            Assert.Equal(0, read.Size);
            Assert.Equal(File.GetLastWriteTimeUtc(path).ToFileTimeUtc(), read.ModifiedUtc);
        }
        Assert.Empty(problems);
    }

    [Fact]
    public void Unsupported_extensions_are_not_opened()
    {
        string path = Write("data.xyz", new byte[10]);
        Assert.Equal(DocStatus.Unsupported, new FileContentSource().Read(Request(path, ContentKind.None), default).Doc.Status);
    }

    [Fact]
    public void Office_files_read_through_the_file_source()
    {
        string path = Write("report.docx", OfficeFixtures.Docx());
        var read = new FileContentSource().Read(Request(path, ContentKind.Office), default);
        Assert.Equal(DocStatus.Ok, read.Doc.Status);
        Assert.Contains("INV-2024-001", read.Doc.Body);
        Assert.Equal("Quarterly Report", read.Doc.Field(DocField.Title));
    }

    [Fact]
    public void Byte_source_caches_ranges_counts_bytes_and_reads_exact_ranges_for_headers()
    {
        var data = new byte[1 << 20];
        new Random(9).NextBytes(data);
        string path = Write("blob.bin", data);
        var budget = new ExtractBudget(ExtractLimits.Local, default);
        Assert.Null(FileByteSource.TryOpen(path, budget, out var opened, out _));
        using (var source = opened!)
        {
            Assert.Equal(data.Length, source.Length);
            var buffer = new byte[100];
            Assert.Equal(100, source.ReadAt(1000, buffer));
            Assert.Equal(data.AsSpan(1000, 100).ToArray(), buffer);
            Assert.Equal(64 << 10, source.BytesRead); // one aligned 64 KB block
            source.ReadAt(2000, buffer);
            Assert.Equal(64 << 10, source.BytesRead); // served from the cache
            var tail = new byte[10];
            Assert.Equal(10, source.ReadAt(data.Length - 10, tail));
            Assert.Equal(0, source.ReadAt(data.Length, tail));
            Assert.Equal(5, source.ReadAt(data.Length - 5, tail));
            Assert.Equal(data.AsSpan(data.Length - 5).ToArray(), tail[..5]);
            // A read spanning a cached range and new data.
            var span = new byte[100_000];
            Assert.Equal(span.Length, source.ReadAt(60_000, span));
            Assert.Equal(data.AsSpan(60_000, span.Length).ToArray(), span);
        }
        var exact = new ExtractBudget(ExtractLimits.Local, default);
        Assert.Null(FileByteSource.TryOpen(path, exact, out var openedHeader, out _));
        using (var header = openedHeader!)
        {
            header.ExactRanges = true;
            var b = new byte[12];
            header.ReadAt(500_000, b);
            Assert.Equal(4096, header.BytesRead);
            Assert.Equal(data.AsSpan(500_000, 12).ToArray(), b);
        }
        // Over the byte budget: what is asked for counts (locally), not the 64 KB block fetched for it.
        var small = new ExtractBudget(new ExtractLimits(MaxBytesRead: 1000), default);
        Assert.Null(FileByteSource.TryOpen(path, small, out var openedLimited, out _));
        using (var limited = openedLimited!)
        {
            Assert.Equal(10, limited.ReadAt(0, new byte[10]));
            Assert.Equal(64 << 10, limited.BytesRead);
            Assert.Throws<ContentLimitException>(() => limited.ReadAt(10, new byte[991]));
        }
        // A missing file is temporary trouble with its error.
        Assert.Equal(DocStatus.TimedOut, FileByteSource.TryOpen(Path.Combine(_dir.FullName, "nope.bin"), budget, out _, out int error));
        Assert.Equal(2, error);
    }

    [Fact]
    public void Try_stat_reads_size_date_and_attributes()
    {
        string path = Write("stat.txt", new byte[42]);
        Assert.True(new FileContentSource().TryStat(path, out long size, out long modified, out var attributes));
        Assert.Equal(42, size);
        Assert.Equal(File.GetLastWriteTimeUtc(path).ToFileTimeUtc(), modified);
        Assert.Equal((FileAttributes)0, attributes & FileAttributes.Directory);
        Assert.False(new FileContentSource().TryStat(Path.Combine(_dir.FullName, "none.txt"), out _, out _, out _));
    }
}
