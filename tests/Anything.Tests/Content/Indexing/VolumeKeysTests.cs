using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Indexing;

namespace Anything.Tests.Content.Indexing;

public class VolumeKeysTests
{
    [Theory]
    [InlineData(@"\\nas\jobs", @"unc:\\NAS\JOBS", "", @"\\nas\jobs")]
    [InlineData(@"\\Nas\Jobs\2024\Spring", @"unc:\\NAS\JOBS", @"2024\Spring", @"\\Nas\Jobs")]
    [InlineData(@"\\?\UNC\nas\jobs\x", @"unc:\\NAS\JOBS", "x", @"\\nas\jobs")]
    public void Shares_are_keyed_by_server_and_share_without_touching_the_network(string root, string key, string prefix, string display)
    {
        Assert.Equal((key, prefix, display), VolumeKeys.For(root));
    }

    [Fact]
    public void A_mapped_letter_and_its_share_give_the_same_document_key()
    {
        // Z: → \\nas\jobs\dir is resolved through WNetGetConnection; the key and prefix then match the UNC form.
        var (unc, prefix, _) = VolumeKeys.For(@"\\nas\jobs\dir\sub");
        Assert.Equal(ContentKeys.Of(unc, prefix + @"\a.pdf"), ContentKeys.Of(@"unc:\\nas\jobs", @"dir\sub\a.pdf"));
    }

    [Fact]
    public void A_local_drive_is_keyed_by_its_volume_guid()
    {
        string system = Path.GetPathRoot(Environment.SystemDirectory)!.TrimEnd('\\');
        var (key, prefix, display) = VolumeKeys.For(system);
        Assert.Matches(@"^vol:\{[0-9A-F-]{36}\}$", key);
        Assert.Equal("", prefix);
        Assert.Equal(system + "\\", display);
        // A folder root below it keeps the volume's key and gets the folder as its prefix.
        var below = VolumeKeys.For(system + @"\Windows\System32");
        Assert.Equal(key, below.Key);
        Assert.Equal(@"Windows\System32", below.RelativePrefix);
    }

    [Fact]
    public void A_drive_that_isnt_there_falls_back_to_its_path()
    {
        string? missing = null;
        for (char c = 'Z'; c >= 'G'; c--)
            if (!Directory.Exists(c + @":\")) { missing = c + ":"; break; }
        if (missing == null) return;
        var (key, _, _) = VolumeKeys.For(missing);
        Assert.StartsWith("path:", key);
    }

    [Theory]
    [InlineData(@"C:\a\", @"C:\a")]
    [InlineData("C:/a/b", @"C:\a\b")]
    [InlineData(@"\\?\C:\x", @"C:\x")]
    [InlineData("\"D:\\q\"", @"D:\q")]
    public void Paths_are_cleaned(string raw, string clean) => Assert.Equal(clean, VolumeKeys.Clean(raw));

    [Fact]
    public void Volume_names_give_their_guid()
    {
        Assert.Equal("{0A1B2C3D-0000-1111-2222-333344445555}", VolumeKeys.GuidOf(@"\\?\Volume{0a1b2c3d-0000-1111-2222-333344445555}\"));
        Assert.Null(VolumeKeys.GuidOf(@"\\?\Volume{nope}\"));
    }

    [Fact]
    public void The_root_snapshot_resolves_a_path_through_the_longest_root()
    {
        var snap = new RootSnapshot([("C:", "path:C:", "", @"C:\", 0), (@"C:\Mount\Data", "vol:{X}", "", @"C:\Mount\Data\", 1), (@"D:\Data", "path:D:", "Data", @"D:\", 2)]);
        Assert.True(snap.TryResolve(@"C:\Mount\Data\a.pdf", out var key, out var rel, out var volume, out _));
        Assert.Equal(("vol:{X}", "a.pdf", (ushort)1), (key, rel, volume));
        Assert.True(snap.TryResolve(@"C:\Mount\Database\a.pdf", out key, out rel, out _, out _));
        Assert.Equal(("path:C:", @"Mount\Database\a.pdf"), (key, rel));
        Assert.True(snap.TryResolve(@"D:\Data\Jobs\b.pdf", out key, out rel, out _, out _));
        Assert.Equal(("path:D:", @"Data\Jobs\b.pdf"), (key, rel));
        Assert.False(snap.TryResolve(@"E:\b.pdf", out _, out _, out _, out _));
    }
}

public class RecentItemsTests
{
    /// <summary>A minimal Shell Link: header, an ID list to skip, LinkInfo with a local base path (ANSI and Unicode) or a share.</summary>
    static byte[] Link(string? local, string? share = null, string suffix = "", bool unicode = true, bool idList = true)
    {
        var info = new MemoryStream();
        var w = new BinaryWriter(info);
        int header = unicode ? 0x24 : 0x1C;
        var ansiLocal = local == null ? [] : Encoding.ASCII.GetBytes(local + "\0");
        var wideLocal = local == null ? [] : Encoding.Unicode.GetBytes(local + "\0");
        var ansiSuffix = Encoding.ASCII.GetBytes(suffix + "\0");
        var volumeId = new byte[] { 0x10, 0, 0, 0, 3, 0, 0, 0, 0x12, 0x34, 0x56, 0x78, 0x10, 0, 0, 0 };
        byte[] net = [];
        if (share != null)
        {
            var name = Encoding.ASCII.GetBytes(share + "\0");
            var n = new MemoryStream();
            var nw = new BinaryWriter(n);
            nw.Write(20 + name.Length); nw.Write(0); nw.Write(20); nw.Write(0); nw.Write(0x00020000); nw.Write(name);
            net = n.ToArray();
        }
        int offset = header;
        int volumeOffset = local != null ? offset : 0; if (local != null) offset += volumeId.Length;
        int localOffset = local != null ? offset : 0; offset += ansiLocal.Length;
        int netOffset = share != null ? offset : 0; offset += net.Length;
        int suffixOffset = offset; offset += ansiSuffix.Length;
        int wideLocalOffset = unicode && local != null ? offset : 0; if (unicode) offset += wideLocal.Length;
        int wideSuffixOffset = 0;
        int flags = (local != null ? 1 : 0) | (share != null ? 2 : 0);
        w.Write(offset); w.Write(header); w.Write(flags); w.Write(volumeOffset); w.Write(localOffset); w.Write(netOffset); w.Write(suffixOffset);
        if (unicode) { w.Write(wideLocalOffset); w.Write(wideSuffixOffset); }
        if (local != null) w.Write(volumeId);
        w.Write(ansiLocal); w.Write(net); w.Write(ansiSuffix);
        if (unicode) w.Write(wideLocal);
        var file = new MemoryStream();
        var f = new BinaryWriter(file);
        f.Write(0x4C);
        f.Write(new Guid("00021401-0000-0000-C000-000000000046").ToByteArray());
        f.Write(0x2 | (idList ? 0x1 : 0) | 0x80); // HasLinkInfo, HasLinkTargetIDList, IsUnicode
        f.Write(new byte[0x4C - 0x18]);
        if (idList) { f.Write((ushort)6); f.Write(new byte[] { 4, 0, 1, 2, 0, 0 }); }
        f.Write(info.ToArray());
        return file.ToArray();
    }

    [Fact]
    public void A_local_shortcut_gives_its_target()
    {
        Assert.Equal(@"C:\Users\Me\Documents\Quote.docx", RecentItems.Target(Link(@"C:\Users\Me\Documents\Quote.docx")));
        Assert.Equal(@"C:\Users\Me\Café.pdf", RecentItems.Target(Link(@"C:\Users\Me\Café.pdf", unicode: true)));
        Assert.Equal(@"C:\Data\a.txt", RecentItems.Target(Link(@"C:\Data\a.txt", unicode: false, idList: false)));
    }

    [Fact]
    public void A_shortcut_to_a_share_gives_the_share_and_suffix()
        => Assert.Equal(@"\\nas\jobs\2024\job.pdf", RecentItems.Target(Link(null, @"\\nas\jobs", @"2024\job.pdf")));

    [Fact]
    public void Damaged_shortcuts_give_nothing_and_never_throw()
    {
        var good = Link(@"C:\Users\Me\a.txt");
        Assert.Null(RecentItems.Target([]));
        Assert.Null(RecentItems.Target(good.AsSpan(0, 0x4C)));
        var random = new Random(5);
        for (int i = 0; i < 2000; i++)
        {
            var bad = (byte[])good.Clone();
            for (int k = 0; k < 4; k++) bad[random.Next(0x14, bad.Length)] = (byte)random.Next(256);
            _ = RecentItems.Target(bad.AsSpan(0, random.Next(bad.Length + 1)));
        }
    }

    [Fact]
    public void The_newest_shortcuts_of_a_folder_are_read()
    {
        var dir = Directory.CreateTempSubdirectory("rp-recent-");
        try
        {
            File.WriteAllBytes(Path.Combine(dir.FullName, "old.lnk"), Link(@"C:\old.txt"));
            File.SetLastWriteTimeUtc(Path.Combine(dir.FullName, "old.lnk"), DateTime.UtcNow.AddDays(-3));
            File.WriteAllBytes(Path.Combine(dir.FullName, "new.lnk"), Link(@"C:\new.txt"));
            File.WriteAllBytes(Path.Combine(dir.FullName, "junk.lnk"), [1, 2, 3]);
            File.SetLastWriteTimeUtc(Path.Combine(dir.FullName, "junk.lnk"), DateTime.UtcNow.AddDays(-9));
            File.WriteAllText(Path.Combine(dir.FullName, "note.txt"), "not a shortcut");
            Assert.Equal([@"C:\new.txt", @"C:\old.txt"], RecentItems.Targets(dir.FullName));
            Assert.Equal([@"C:\new.txt"], RecentItems.Targets(dir.FullName, max: 1));
        }
        finally { dir.Delete(true); }
    }

    [Fact]
    public void The_real_recent_items_folder_reads_without_throwing() => _ = RecentItems.Targets();
}
