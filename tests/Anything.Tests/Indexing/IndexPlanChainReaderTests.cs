using System.Diagnostics;
using System.Runtime.InteropServices;
using Anything.Core;
using Anything.Core.Indexing;

namespace Anything.Tests.Indexing;

/// <summary>
/// <see cref="IndexPlan.ChainReader"/>: what the UI thread reads to tell whether the index holds a folder. It gives the
/// answer reading the disk gives, reading only the folder and the folders above it, and nothing on the network.
/// </summary>
public sealed class IndexPlanChainReaderTests : IDisposable
{
    const FileAttributes Dir = FileAttributes.Directory;
    const FileAttributes Link = FileAttributes.Directory | FileAttributes.ReparsePoint;

    static DriveEntry Fixed(string root) => new(root, DriveKind.Fixed, "NTFS", "", true, null, null);
    static DriveEntry Network(string root, string share) => new(root, DriveKind.Network, "NTFS", "", true, share, null);
    static DriveEntry Subst(string root, string target) => new(root, DriveKind.Fixed, "NTFS", "", true, null, target);

    readonly string _temp = Directory.CreateTempSubdirectory("anything-chain-").FullName;
    readonly List<string> _links = [];

    public void Dispose()
    {
        // The links first, so deleting the folder cannot reach through one.
        foreach (var link in _links) try { Directory.Delete(link); } catch { }
        try { Directory.Delete(_temp, true); } catch { }
    }

    /// <summary>A junction at <paramref name="link"/> to <paramref name="target"/>, as mklink /J makes one (no rights needed, unlike a symbolic link).</summary>
    void Junction(string link, string target)
    {
        var start = new ProcessStartInfo("cmd.exe", $"/c mklink /J \"{link}\" \"{target}\"")
        {
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
        };
        using var p = Process.Start(start)!;
        var output = p.StandardOutput.ReadToEndAsync();
        string error = p.StandardError.ReadToEnd();
        p.WaitForExit();
        Assert.True(p.ExitCode == 0, $"mklink: {output.Result} {error}");
        _links.Add(link);
        Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
    }

    /// <summary>
    /// Coverage and SearchFolder of <paramref name="folder"/> with the chain readers give what they give reading the whole
    /// disk, and the coverage is returned.
    /// </summary>
    static FolderCoverage SameAsTheDisk(IndexSettings settings, IReadOnlyList<DriveEntry> drives, string folder)
    {
        var (chain, links) = IndexPlan.ChainReaders(drives, folder);
        var disk = IndexPlan.Coverage(settings, drives, folder);
        Assert.Equal(disk, IndexPlan.Coverage(settings, drives, folder, attributesOf: chain, linkTargetOf: links));

        var byDisk = settings.Clone();
        var byChain = settings.Clone();
        Assert.Equal(IndexPlan.SearchFolder(byDisk, drives, folder), IndexPlan.SearchFolder(byChain, drives, folder, chain, links));
        Assert.Equal(byDisk.ExcludeFolders, byChain.ExcludeFolders);
        Assert.Equal(byDisk.Folders, byChain.Folders);
        return disk;
    }

    [Fact]
    public void Junction_is_followed_as_reading_the_disk_follows_it()
    {
        string target = Directory.CreateDirectory(Path.Combine(_temp, "target", "Proj", "src")).Parent!.FullName;
        string link = Path.Combine(Directory.CreateDirectory(Path.Combine(_temp, "a")).FullName, "link");
        Junction(link, Path.Combine(_temp, "target"));
        string folder = Path.Combine(link, "Proj");

        // The temporary folder searched as an added folder, and as part of the whole drive it is on.
        var drive = Fixed(Path.GetPathRoot(_temp)!);
        var cases = new (IndexSettings Settings, DriveEntry[] Drives)[]
        {
            (new IndexSettings { IndexNtfsVolumes = false, Folders = [_temp] }, []),
            (new IndexSettings(), [drive]),
        };
        foreach (var (settings, drives) in cases)
        {
            // The folder it points to is searched, and so are its files, under that folder's name.
            var c = SameAsTheDisk(settings, drives, folder);
            Assert.True(c.Covered);
            Assert.Equal(folder, c.Folder);
            Assert.Equal(target, c.IndexedAs);
            Assert.Equal(Path.Combine(target, "src"), SameAsTheDisk(settings, drives, Path.Combine(folder, "src")).IndexedAs);
            Assert.Equal(target, SameAsTheDisk(settings, drives, target).IndexedAs);
            // Without the link reader, the link is not followed; reading nothing takes it for an ordinary folder.
            Assert.Equal(CoverageReason.BehindLink, IndexPlan.Coverage(settings, drives, folder, attributesOf: IndexPlan.ChainReader(drives, folder)).Reason);
            Assert.True(IndexPlan.Coverage(settings, drives, folder, attributesOf: _ => null).Covered);
        }

        // Where it points is not searched: behind a link, and searching it adds that folder, not a second way to it.
        var outside = new IndexSettings { IndexNtfsVolumes = false, Folders = [Path.Combine(_temp, "a")] };
        Assert.Equal(CoverageReason.BehindLink, SameAsTheDisk(outside, [], folder).Reason);
        var (attributes, links) = IndexPlan.ChainReaders([], folder);
        Assert.Equal(SearchFolderResult.Added, IndexPlan.SearchFolder(outside, [], folder, attributes, links));
        Assert.Equal([Path.Combine(_temp, "a"), target], outside.Folders.ToArray());
        var now = SameAsTheDisk(outside, [], Path.Combine(folder, "src"));
        Assert.True(now.Covered);
        Assert.Equal(target, now.Root);
        Assert.Equal(Path.Combine(target, "src"), now.IndexedAs);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    static extern bool GetVolumeNameForVolumeMountPointW(string mountPoint, [Out] char[] volumeName, int size);

    [Fact]
    public void Volume_mounted_in_a_folder_is_not_taken_for_a_link()
    {
        // Mounting a volume in a folder makes a junction to the volume by its id, which mklink /J makes too, without the
        // rights mountvol needs: here the volume the temporary folder is on.
        string drive = Path.GetPathRoot(_temp)!;
        var id = new char[64];
        Assert.True(GetVolumeNameForVolumeMountPointW(drive, id, id.Length));
        string volume = new(id, 0, Array.IndexOf(id, '\0')); // \\?\Volume{...}\
        string mount = Path.Combine(_temp, "Mount");
        Junction(mount, volume);
        Assert.Equal(volume[4..], new DirectoryInfo(mount).LinkTarget); // Volume{...}\, as the tests' disks give it
        Directory.CreateDirectory(Path.Combine(_temp, "Photos"));
        string folder = Path.Combine(mount, _temp[drive.Length..], "Photos"); // that folder, through the mount

        // The drive the tests run in is listed too: a target made up there would be found searched.
        var drives = new List<DriveEntry> { Fixed(drive) };
        string here = Path.GetPathRoot(Environment.CurrentDirectory)!;
        if (!drives.Exists(d => string.Equals(d.Root, here, StringComparison.OrdinalIgnoreCase))) drives.Add(Fixed(here));
        DriveEntry[] listed = [.. drives, Fixed(mount + @"\")];

        // Listed and turned off: the volume's own switch is why, and showing it in searches adds the folder.
        var off = new IndexSettings { SkipDrives = [mount + @"\"] };
        var c = SameAsTheDisk(off, listed, folder);
        Assert.Equal(CoverageReason.DriveTurnedOff, c.Reason);
        Assert.Equal(mount + @"\", c.Drive);
        Assert.Equal(folder, c.IndexedAs);
        Assert.Equal(c, IndexPlan.Coverage(off, listed, folder, attributesOf: IndexPlan.ChainReader(listed, folder)));
        Assert.Equal(SearchFolderResult.DriveRoot, IndexPlan.SearchFolder(off.Clone(), listed, mount));
        var (attributes, links) = IndexPlan.ChainReaders(listed, folder);
        Assert.Equal(SearchFolderResult.Added, IndexPlan.SearchFolder(off, listed, folder, attributes, links));
        Assert.Equal([folder], off.Folders.ToArray());
        Assert.True(SameAsTheDisk(off, listed, Path.Combine(folder, "2024")).Covered);

        // Listed and searched: in the volume's own root.
        var on = SameAsTheDisk(new IndexSettings(), listed, folder);
        Assert.True(on.Covered);
        Assert.Equal(mount, on.Root);

        // Listed as not ready: nothing changes.
        DriveEntry[] locked = [.. drives, new DriveEntry(mount + @"\", DriveKind.Fixed, "", "", false, null, null)];
        Assert.Equal(CoverageReason.DriveNotReady, SameAsTheDisk(new IndexSettings(), locked, folder).Reason);
        Assert.Equal(SearchFolderResult.DriveNotReady, IndexPlan.SearchFolder(new IndexSettings(), locked, folder));
        Assert.Equal(SearchFolderResult.DriveRoot, IndexPlan.SearchFolder(new IndexSettings(), locked, mount));

        // Not listed: behind a link, walked through, so showing it in searches adds the folder as it is named.
        Assert.Equal(CoverageReason.BehindLink, SameAsTheDisk(new IndexSettings(), drives, folder).Reason);
        var add = new IndexSettings();
        Assert.Equal(SearchFolderResult.Added, IndexPlan.SearchFolder(add, drives, folder));
        Assert.Equal([folder], add.Folders.ToArray());
    }

    [Theory]
    [InlineData(FileAttributes.Hidden, true, false, CoverageReason.HiddenLeftOut)]
    [InlineData(FileAttributes.System, false, true, CoverageReason.SystemLeftOut)]
    [InlineData(FileAttributes.Hidden | FileAttributes.System, false, true, CoverageReason.SystemLeftOut)]
    public void Hidden_or_system_folder_is_found_as_reading_the_disk_finds_it(FileAttributes attributes, bool hidden, bool system, CoverageReason reason)
    {
        string left = Directory.CreateDirectory(Path.Combine(_temp, "left", "inner", "kept")).Parent!.Parent!.FullName;
        File.SetAttributes(left, FileAttributes.Directory | attributes);
        Directory.CreateDirectory(Path.Combine(_temp, "plain", "x"));
        string kept = Path.Combine(left, "inner", "kept");
        var settings = new IndexSettings { IndexNtfsVolumes = false, ExcludeHidden = hidden, ExcludeSystem = system, Folders = [_temp, kept] };

        Assert.Equal(reason, SameAsTheDisk(settings, [], left).Reason);
        Assert.Equal(reason, SameAsTheDisk(settings, [], Path.Combine(left, "inner")).Reason);
        Assert.True(IndexPlan.Coverage(settings, [], Path.Combine(left, "inner"), attributesOf: _ => null).Covered);

        // The added folder inside it: the plan reads the folders above it too, and finds it a root of its own.
        var inKept = SameAsTheDisk(settings, [], Path.Combine(kept, "deep"));
        Assert.True(inKept.Covered);
        Assert.Equal(kept, inKept.Root);

        // A folder beside it does not read it, and is covered either way.
        var plain = SameAsTheDisk(settings, [], Path.Combine(_temp, "plain", "x"));
        Assert.True(plain.Covered);
        Assert.Equal(_temp, plain.Root);

        // Not left out: covered.
        Assert.True(SameAsTheDisk(new IndexSettings { IndexNtfsVolumes = false, Folders = [_temp] }, [], Path.Combine(left, "inner")).Covered);
    }

    [Fact]
    public void Reads_only_the_folder_and_the_folders_above_it_under_each_of_its_names()
    {
        var drives = new[] { Fixed(@"C:\"), Subst(@"S:\", @"C:\Work"), Network(@"Z:\", @"\\nas\music"), Subst(@"T:\", @"Z:\Albums") };
        var read = new List<string>();
        var reader = IndexPlan.ChainReader(drives, @"S:\Proj\Sub", p => { read.Add(p); return Dir; });
        string[] chain = [@"S:\Proj\Sub", @"S:\Proj", @"C:\Work\Proj\Sub", @"c:\work\proj", @"C:\Work", @"C:\"];
        string[] others = [@"S:\Other", @"C:\Work\Other", @"C:\Users", @"C:\Work\Proj\Sub\Deeper", @"C:\Work\Pro", @"D:\Proj\Sub", @"Z:\Proj"];
        foreach (var p in chain) Assert.Equal(Dir, reader(p));
        foreach (var p in others) Assert.Null(reader(p));
        // Each once, the folders on the way first (looking for links on it).
        Assert.True(read.ToHashSet(StringComparer.OrdinalIgnoreCase).SetEquals(chain), string.Join(", ", read));
        Assert.Equal(read.Count, read.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // Nothing on the network: a share, a mapped letter, a SUBST of a mapped letter.
        read.Clear();
        foreach (var folder in new[] { @"Z:\Albums\Live", @"\\nas\music\Albums\Live", @"T:\Live", @"\\nas\docs\letters" })
        {
            var net = IndexPlan.ChainReader(drives, folder, p => { read.Add(p); return Dir; });
            foreach (var p in new[] { folder, @"Z:\Albums", @"\\nas\music\Albums", @"\\nas\music", @"T:\Live", @"\\nas\docs" }) Assert.Null(net(p));
        }
        Assert.Empty(read);

        // Not a folder on a drive or a share: nothing is read.
        var none = IndexPlan.ChainReader(drives, "Documents", p => { read.Add(p); return Dir; });
        Assert.Null(none(@"C:\Users"));
        Assert.Null(none("Documents"));
        Assert.Empty(read);
    }

    [Fact]
    public void Reads_a_folder_above_it_under_a_name_it_was_not_given_by()
    {
        // S: shows C:\Work, and S:\Proj is a junction. C:\Work\Proj\Sub is inside the folder added as S:\Proj, which is a
        // root only because reading S:\Proj finds the link: the reader must read S:\Proj for C:\Work\Proj\Sub.
        var drives = new[] { Fixed(@"C:\"), Subst(@"S:\", @"C:\Work") };
        var disk = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase) { [@"C:\Work\Proj"] = Link, [@"S:\Proj"] = Link };
        Func<string, FileAttributes?> read = p => disk.TryGetValue(p, out var a) ? a : null;
        var settings = new IndexSettings { SkipDrives = [@"C:\"], Folders = [@"S:\", @"S:\Proj"] };

        var c = IndexPlan.Coverage(settings, drives, @"C:\Work\Proj\Sub", attributesOf: read);
        Assert.True(c.Covered);
        Assert.Equal(@"S:\Proj", c.Root);
        Assert.Equal(c, IndexPlan.Coverage(settings, drives, @"C:\Work\Proj\Sub", attributesOf: IndexPlan.ChainReader(drives, @"C:\Work\Proj\Sub", read)));
    }

    [Fact]
    public void Gives_the_answer_reading_the_disk_gives()
    {
        // Random settings, drives and folder attributes; every folder asked about, with each reader.
        var rng = new Random(20261021);
        var drivePool = new[]
        {
            Fixed(@"C:\"), new DriveEntry(@"D:\", DriveKind.Fixed, "exFAT", "", true, null, null), Fixed(@"C:\Mount\Data\"),
            new DriveEntry(@"C:\Mount\Locked\", DriveKind.Fixed, "", "", false, null, null), Subst(@"S:\", @"C:\Work"), Subst(@"U:\", @"C:\"),
            Subst(@"V:\", @"S:\Proj"), Network(@"Y:\", @"\\nas\music"), Network(@"Z:\", @"\\nas\music"), Subst(@"T:\", @"Z:\Albums"),
        };
        string[] excludePool = [@"C:\", @"C:\Work", @"C:\Work\Proj", @"S:\Proj", @"C:\Users\me\AppData", "node_modules", "Proj", @"C:\Mount", @"*\Linked", @"Y:\Albums", @"D:\Games\"];
        string[] patternPool = ["Mount", "*.bak", "Pro?", "Sub"];
        string[] folderPool =
        [
            @"C:\Work\Proj\Sub", @"S:\Proj", @"S:\", @"C:\Users\me\AppData\Roaming", @"C:\Users\me\Linked\Proj", @"C:\Mount\Data\Docs", @"Z:\Albums",
            @"\\nas\docs", @"D:\Games", @"C:\Work\Proj\.cache\keep", @"V:\.cache", @"U:\Users\me", @"C:\Mount\Locked\x", @"T:\Live",
        ];
        string[] canonical = [@"C:\Users", @"C:\Users\me", @"C:\Users\me\AppData", @"C:\Users\me\Linked", @"C:\Work", @"C:\Work\Proj", @"C:\Work\Proj\.cache", @"C:\Work\Proj\Sub", @"C:\Mount", @"D:\Games"];
        FileAttributes[] kinds = [Dir, Dir | FileAttributes.Hidden, Dir | FileAttributes.System, Link, Dir | FileAttributes.Hidden | FileAttributes.System];
        // Links, and where each may point: a local folder, another drive, a share, a mapped or SUBST letter, a mounted volume.
        var linkPool = new (string Link, string[] Targets)[]
        {
            (@"C:\Users\me\Linked", [@"C:\Work", @"D:\Games", @"\\nas\music\Albums", @"Z:\Albums", @"S:\Proj", @"C:\Mount\Data", @"..\..\Work\Proj"]),
            (@"C:\Work\Proj\.cache", [@"C:\Users\me\AppData", @"\\nas\docs", @"D:\Games"]),
            (@"D:\Games", [@"C:\Work\Proj", @"\\nas\music", @"Y:\"]),
        };
        // Volumes mounted in folders: on the disk whatever the drive list says, as junctions to the volume by its id.
        var mountPoints = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Mount\Data"] = @"Volume{0b6e0f3a-0000-0000-0000-100000000001}\", [@"C:\Mount\Locked"] = @"Volume{0b6e0f3a-0000-0000-0000-100000000002}\",
            [@"C:\Mount\Other"] = @"Volume{0b6e0f3a-0000-0000-0000-100000000003}\",
        };
        string[] asked =
        [
            .. folderPool, @"C:\Work\Proj\Sub\x", @"S:\Proj\Sub", @"S:\Proj\.cache\keep\y", @"C:\Work", @"U:\Work\Proj", @"U:\", @"V:\", @"V:\Sub\x",
            @"C:\Users\me\AppData\Roaming\Code", @"C:\Users\me\Linked\Proj\src", @"C:\Mount\Data\Docs\a", @"C:\Mount\Data", @"C:\Mount\Other",
            @"Y:\Albums\Live", @"\\nas\music\Albums", @"D:\Games\Old", @"C:\", @"D:\", "Documents", @"\\nas", @"C:\Users\me\Linked",
            @"C:\Users\me\Linked\Live\2024", @"C:\Work\Proj\.cache\Roaming\x", @"D:\Games\Sub\x",
        ];
        T[] Some<T>(T[] pool, double p) => [.. pool.Where(_ => rng.NextDouble() < p)];
        int followed = 0; // answers a link changed: the test reaches through links

        for (int round = 0; round < 600; round++)
        {
            var drives = Some(drivePool, 0.7);
            var settings = new IndexSettings
            {
                IndexNtfsVolumes = rng.NextDouble() < 0.8,
                SkipDrives = [.. Some(new[] { @"C:\", @"D:\", @"C:\Mount\Data\", @"Y:\" }, 0.25)],
                IndexNetworkDrives = rng.NextDouble() < 0.7,
                ExcludeHidden = rng.NextDouble() < 0.5,
                ExcludeSystem = rng.NextDouble() < 0.5,
                ExcludeFolders = [.. Some(excludePool, 0.15)],
                ExcludePatterns = [.. Some(patternPool, 0.1)],
                Folders = [.. Some(folderPool, 0.3)],
            };
            // The folders as they are, then under every name a drive shows them by, and through every link.
            var canon = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in canonical)
                if (rng.NextDouble() < 0.4) canon[path] = kinds[rng.Next(kinds.Length)];
            var links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (link, targets) in linkPool)
                if (rng.NextDouble() < 0.35) links[link] = targets[rng.Next(targets.Length)];
            FileAttributes? Disk(string path) =>
                Reached(drives, links, path) is not { } at ? null : links.ContainsKey(at) || mountPoints.ContainsKey(at) ? Link : canon.TryGetValue(at, out var a) ? a : null;
            string? LinkOf(string path) => Reached(drives, links, path) is { } at ? links.GetValueOrDefault(at) ?? mountPoints.GetValueOrDefault(at) : null;

            foreach (var folder in asked)
            {
                var read = new List<string>();
                var readLinks = new List<string>();
                FileAttributes? Read(string p) { read.Add(p); return Disk(p); }
                string? ReadLink(string p) { readLinks.Add(p); return LinkOf(p); }
                var (chain, chainLinks) = IndexPlan.ChainReaders(drives, folder, Read, ReadLink);
                var expected = IndexPlan.Coverage(settings, drives, folder, attributesOf: Disk, linkTargetOf: LinkOf);
                Assert.Equal(expected, IndexPlan.Coverage(settings, drives, folder, attributesOf: chain, linkTargetOf: chainLinks));
                if (expected != IndexPlan.Coverage(settings, drives, folder, attributesOf: Disk)) followed++;

                var byDisk = settings.Clone();
                var byChain = settings.Clone();
                Assert.Equal(IndexPlan.SearchFolder(byDisk, drives, folder, Disk, LinkOf), IndexPlan.SearchFolder(byChain, drives, folder, chain, chainLinks));
                Assert.Equal(byDisk.ExcludeFolders, byChain.ExcludeFolders);
                Assert.Equal(byDisk.Folders, byChain.Folders);
                // Never a share, a letter that shows one, or a folder behind a link to one; each path once.
                Assert.DoesNotContain(read.Concat(readLinks), p => Reached(drives, links, p) == null);
                Assert.Equal(read.Count, read.Distinct(StringComparer.OrdinalIgnoreCase).Count());
                Assert.Equal(readLinks.Count, readLinks.Distinct(StringComparer.OrdinalIgnoreCase).Count());
            }
        }
        Assert.True(followed > 100, $"{followed}");
    }

    /// <summary>
    /// Where opening <paramref name="path"/> lands: through SUBST drives and the links on the folders above it (not itself,
    /// whose own attributes and link are read); null on the network, where the test's disk has nothing.
    /// </summary>
    static string? Reached(IReadOnlyList<DriveEntry> drives, Dictionary<string, string> links, string path)
    {
        for (int step = 0; step < 8; step++)
        {
            if (Canonical(drives, path) is not { } at) return null;
            string? next = null;
            for (int end = at.Length > 3 ? at.IndexOf('\\', 3) : -1; end > 0 && next == null; end = at.IndexOf('\\', end + 1))
            {
                if (!links.TryGetValue(at[..end], out var target)) continue;
                // A relative target is from the link's own folder.
                if (!Path.IsPathFullyQualified(target)) target = Path.GetFullPath(target, Path.GetDirectoryName(at[..end])!);
                next = target.TrimEnd('\\') + at[end..];
            }
            if (next == null) return at;
            path = next;
        }
        return null;
    }

    /// <summary>
    /// The path the files of <paramref name="path"/> really are at, through SUBST drives; null on the network (a share, a
    /// mapped drive, or a SUBST of one), where the test's disk has nothing.
    /// </summary>
    static string? Canonical(IReadOnlyList<DriveEntry> drives, string path)
    {
        for (int i = 0; i < 5; i++)
        {
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return null;
            var d = drives.FirstOrDefault(d => d.IsDriveLetter && path.Length >= 2 && char.ToUpperInvariant(d.Root[0]) == char.ToUpperInvariant(path[0]) && path[1] == ':');
            if (d?.Kind == DriveKind.Network) return null;
            if (d?.SubstTarget is not { } target) return path;
            path = target.TrimEnd('\\') + path[2..];
        }
        return path;
    }
}
