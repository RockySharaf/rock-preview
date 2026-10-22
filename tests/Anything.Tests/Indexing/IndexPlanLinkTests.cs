using Anything.Core;
using Anything.Core.Indexing;

namespace Anything.Tests.Indexing;

/// <summary>
/// A folder reached through a junction or symbolic link: <see cref="IndexPlan.Coverage"/> follows the link to the folder
/// it points to, <see cref="IndexPlan.SearchFolder"/> never adds a second way to the same files, and the chain readers
/// never read behind a link to a share.
/// </summary>
public sealed class IndexPlanLinkTests
{
    const FileAttributes Dir = FileAttributes.Directory;
    const FileAttributes Link = FileAttributes.Directory | FileAttributes.ReparsePoint;
    static readonly StringComparer Ci = StringComparer.OrdinalIgnoreCase;

    /// <summary>What a volume mounted in a folder holds as its target, as .NET gives it (the "\??\" in front comes off).</summary>
    const string Volume = @"Volume{0b6e0f3a-0000-0000-0000-100000000000}\";

    static DriveEntry Fixed(string root, bool ready = true) => new(root, DriveKind.Fixed, ready ? "NTFS" : "", "", ready, null, null);
    static DriveEntry Network(string root, string share) => new(root, DriveKind.Network, "NTFS", "", true, share, null);
    static DriveEntry Subst(string root, string target) => new(root, DriveKind.Fixed, "NTFS", "", true, null, target);

    /// <summary>
    /// A disk of folders: the links, with the target each holds as <see cref="FileSystemInfo.LinkTarget"/> gives it (a
    /// full path, a relative one, or <see cref="Volume"/>); every other folder is an ordinary one.
    /// </summary>
    sealed class Disk(Dictionary<string, string> links)
    {
        public readonly List<string> Read = [], Followed = [];
        public FileAttributes? Attributes(string path) { Read.Add(path); return links.ContainsKey(path.TrimEnd('\\')) ? Link : Dir; }
        public string? Target(string path) { Followed.Add(path); return links.GetValueOrDefault(path.TrimEnd('\\')); }
    }

    static Disk On(params (string Link, string Target)[] links) => new(links.ToDictionary(l => l.Link, l => l.Target, Ci));

    static FolderCoverage Coverage(IndexSettings settings, IReadOnlyList<DriveEntry> drives, string folder, Disk disk)
    {
        var c = IndexPlan.Coverage(settings, drives, folder, attributesOf: disk.Attributes, linkTargetOf: disk.Target);
        // The chain readers give the same answer.
        var (chain, links) = IndexPlan.ChainReaders(drives, folder, disk.Attributes, disk.Target);
        Assert.Equal(c, IndexPlan.Coverage(settings, drives, folder, attributesOf: chain, linkTargetOf: links));
        return c;
    }

    static SearchFolderResult SearchFolder(IndexSettings settings, IReadOnlyList<DriveEntry> drives, string folder, Disk disk)
    {
        var (chain, links) = IndexPlan.ChainReaders(drives, folder, disk.Attributes, disk.Target);
        return IndexPlan.SearchFolder(settings, drives, folder, chain, links);
    }

    [Fact]
    public void Junction_to_a_searched_folder_is_searched_there()
    {
        // A game library moved to D: and left a junction behind.
        var drives = new[] { Fixed(@"C:\"), Fixed(@"D:\") };
        var disk = On((@"C:\Games\Big", @"D:\Games\Big"));
        var settings = new IndexSettings();

        var c = Coverage(settings, drives, @"C:\Games\Big", disk);
        Assert.True(c.Covered);
        Assert.Equal(@"C:\Games\Big", c.Folder);
        Assert.Equal("D:", c.Root);
        Assert.Equal(@"D:\Games\Big", c.IndexedAs);
        Assert.Null(c.Rule);
        Assert.Null(c.Drive);
        var inside = Coverage(settings, drives, @"c:\games\big\Saves\2024\", disk);
        Assert.Equal("D:", inside.Root);
        Assert.Equal(@"D:\Games\Big\Saves\2024", inside.IndexedAs);

        // Not followed (no link reader), it is behind a link, as before.
        Assert.Equal(CoverageReason.BehindLink, IndexPlan.Coverage(settings, drives, @"C:\Games\Big", attributesOf: disk.Attributes).Reason);

        // Showing it in searches would index its files twice: nothing changes.
        Assert.Equal(SearchFolderResult.AlreadySearched, SearchFolder(settings, drives, @"C:\Games\Big\Saves", disk));
        Assert.Empty(settings.Folders);
        Assert.Empty(settings.ExcludeFolders);

        // The link's own drive turned off: the drive it points to still holds the files.
        var off = new IndexSettings { SkipDrives = [@"C:\"] };
        var there = Coverage(off, drives, @"C:\Games\Big\Saves", disk);
        Assert.True(there.Covered);
        Assert.Equal(@"D:\Games\Big\Saves", there.IndexedAs);
        Assert.Equal(SearchFolderResult.AlreadySearched, SearchFolder(off, drives, @"C:\Games\Big\Saves", disk));
        Assert.Empty(off.Folders);
    }

    [Fact]
    public void Junction_to_a_folder_not_searched_adds_the_folder_it_points_to()
    {
        var drives = new[] { Fixed(@"C:\"), Fixed(@"D:\") };
        var disk = On((@"C:\Games\Big", @"D:\Games\Big"));

        // D: is off: behind a link; showing it in searches adds where it points, not the link.
        var off = new IndexSettings { SkipDrives = [@"D:\"] };
        var c = Coverage(off, drives, @"C:\Games\Big", disk);
        Assert.Equal(CoverageReason.BehindLink, c.Reason);
        Assert.Equal("C:", c.Root);
        Assert.Equal(@"C:\Games\Big", c.IndexedAs);
        Assert.Equal(SearchFolderResult.Added, SearchFolder(off, drives, @"C:\Games\Big", disk));
        Assert.Equal([@"D:\Games\Big"], off.Folders.ToArray());
        Assert.Equal([@"D:\"], off.SkipDrives.ToArray());
        var now = Coverage(off, drives, @"C:\Games\Big\Saves", disk);
        Assert.True(now.Covered);
        Assert.Equal(@"D:\Games\Big", now.Root);
        Assert.Equal(@"D:\Games\Big\Saves", now.IndexedAs);
        Assert.Equal(["C:", @"D:\Games\Big"], IndexPlan.Plan(off, drives, new ExclusionFilter(off), disk.Attributes).Select(r => r.Name).ToArray());
        Assert.Equal(SearchFolderResult.AlreadySearched, SearchFolder(off, drives, @"C:\Games\Big", disk));

        // Left out where it points by its own entry: that entry comes off.
        var excluded = new IndexSettings { ExcludeFolders = [@"D:\Backup", @"d:\games\big\"] };
        Assert.Equal(CoverageReason.BehindLink, Coverage(excluded, drives, @"C:\Games\Big", disk).Reason);
        Assert.Equal(SearchFolderResult.Unexcluded, SearchFolder(excluded, drives, @"C:\Games\Big", disk));
        Assert.Equal([@"D:\Backup"], excluded.ExcludeFolders.ToArray());
        Assert.Empty(excluded.Folders);
        Assert.Equal(@"D:\Games\Big\Saves", Coverage(excluded, drives, @"C:\Games\Big\Saves", disk).IndexedAs);

        // Left out by a folder above where it points: added there.
        var above = new IndexSettings { ExcludeFolders = [@"D:\Games"] };
        Assert.Equal(SearchFolderResult.Added, SearchFolder(above, drives, @"C:\Games\Big\Saves", disk));
        Assert.Equal([@"D:\Games\Big\Saves"], above.Folders.ToArray());
        Assert.True(Coverage(above, drives, @"C:\Games\Big\Saves\x", disk).Covered);
    }

    [Fact]
    public void Relative_link_points_from_its_own_folder()
    {
        // Symbolic links made with a relative target: from C:\Users\me, "..\Shared\Pics" is C:\Users\Shared\Pics, and
        // "\Data" is C:\Data.
        var drives = new[] { Fixed(@"C:\"), Fixed(@"D:\") };
        var disk = On((@"C:\Users\me\Pics", @"..\Shared\Pics"), (@"C:\Users\me\Data", @"\Data"));
        var settings = new IndexSettings { SkipDrives = [@"C:\"], Folders = [@"C:\Users\Shared"] };

        var pics = Coverage(settings, drives, @"C:\Users\me\Pics\2024", disk);
        Assert.True(pics.Covered);
        Assert.Equal(@"C:\Users\Shared", pics.Root);
        Assert.Equal(@"C:\Users\Shared\Pics\2024", pics.IndexedAs);

        Assert.Equal(CoverageReason.DriveTurnedOff, Coverage(settings, drives, @"C:\Users\me\Data\x", disk).Reason);
        Assert.Equal(SearchFolderResult.Added, SearchFolder(settings, drives, @"C:\Users\me\Data\x", disk));
        Assert.Equal([@"C:\Users\Shared", @"C:\Data\x"], settings.Folders.ToArray());
    }

    [Fact]
    public void Volume_mounted_in_a_folder_is_no_link()
    {
        // A mount point is a junction to the volume by its id. The drive list says what it is, and it is never read.
        var drives = new[] { Fixed(@"C:\"), Fixed(@"D:\"), Fixed(@"C:\Mount\Data\") };
        var disk = On((@"C:\Mount\Data", Volume), (@"C:\Mount\Gone", Volume));

        // Turned off: its own switch is why, the folder in it is added, and the volume itself goes through its include.
        var off = new IndexSettings { SkipDrives = [@"C:\Mount\Data\"] };
        var c = Coverage(off, drives, @"C:\Mount\Data\Photos", disk);
        Assert.Equal(CoverageReason.DriveTurnedOff, c.Reason);
        Assert.Equal("C:", c.Root);
        Assert.Equal(@"C:\Mount\Data\Photos", c.IndexedAs);
        Assert.Equal(@"C:\Mount\Data\", c.Drive);
        Assert.Equal(CoverageReason.DriveTurnedOff, Coverage(off, drives, @"C:\Mount\Data", disk).Reason);
        Assert.Equal(SearchFolderResult.DriveRoot, SearchFolder(off, drives, @"C:\Mount\Data", disk));
        Assert.Equal(SearchFolderResult.Added, SearchFolder(off, drives, @"C:\Mount\Data\Photos", disk));
        Assert.Equal([@"C:\Mount\Data\Photos"], off.Folders.ToArray());
        Assert.True(Coverage(off, drives, @"C:\Mount\Data\Photos\2024", disk).Covered);

        // Searched: in its own root.
        var on = Coverage(new IndexSettings(), drives, @"C:\Mount\Data\Photos", disk);
        Assert.True(on.Covered);
        Assert.Equal(@"C:\Mount\Data", on.Root);

        // Not ready: nothing changes, and it is searched once it is ready.
        var locked = new[] { Fixed(@"C:\"), Fixed(@"C:\Mount\Data\", ready: false) };
        var settings = new IndexSettings();
        Assert.Equal(CoverageReason.DriveNotReady, Coverage(settings, locked, @"C:\Mount\Data\Photos", disk).Reason);
        Assert.Equal(SearchFolderResult.DriveNotReady, SearchFolder(settings, locked, @"C:\Mount\Data\Photos", disk));
        Assert.Equal(SearchFolderResult.DriveRoot, SearchFolder(settings, locked, @"C:\Mount\Data", disk));
        Assert.Empty(settings.Folders);
        Assert.DoesNotContain(disk.Read.Concat(disk.Followed), p => Ci.Equals(p.TrimEnd('\\'), @"C:\Mount\Data"));

        // A volume the drive list doesn't have (it has a letter too, or the list is older): behind a link, walked through,
        // so showing it in searches adds the folder as it is named.
        var gone = Coverage(new IndexSettings(), drives, @"C:\Mount\Gone\Photos", disk);
        Assert.Equal(CoverageReason.BehindLink, gone.Reason);
        Assert.Equal("C:", gone.Root);
        var add = new IndexSettings();
        Assert.Equal(SearchFolderResult.Added, SearchFolder(add, drives, @"C:\Mount\Gone\Photos", disk));
        Assert.Equal([@"C:\Mount\Gone\Photos"], add.Folders.ToArray());
        Assert.True(Coverage(add, drives, @"C:\Mount\Gone\Photos", disk).Covered);
    }

    [Fact]
    public void Link_to_a_share_or_a_mapped_drive_is_searched_there()
    {
        var drives = new[] { Fixed(@"C:\"), Network(@"Z:\", @"\\nas\music") };
        var disk = On((@"C:\Users\me\NAS", @"\\nas\share"), (@"C:\Users\me\Music", @"Z:\Albums"));

        // To a mapped drive that is searched: under its letter.
        var music = Coverage(new IndexSettings(), drives, @"C:\Users\me\Music\Live", disk);
        Assert.True(music.Covered);
        Assert.Equal("Z:", music.Root);
        Assert.Equal(@"Z:\Albums\Live", music.IndexedAs);

        // Network drives off: the share is added, which lasts when the letter is gone.
        var off = new IndexSettings { IndexNetworkDrives = false };
        Assert.Equal(CoverageReason.BehindLink, Coverage(off, drives, @"C:\Users\me\Music\Live", disk).Reason);
        Assert.Equal(SearchFolderResult.Added, SearchFolder(off, drives, @"C:\Users\me\Music\Live", disk));
        Assert.Equal([@"\\nas\music\Albums\Live"], off.Folders.ToArray());
        Assert.True(Coverage(off, drives, @"C:\Users\me\Music\Live\2024", disk).Covered);

        // To a share no drive maps: the share is added, and planned as one (scanned in the background, kept while offline).
        var share = new IndexSettings();
        Assert.Equal(CoverageReason.BehindLink, Coverage(share, drives, @"C:\Users\me\NAS\docs", disk).Reason);
        Assert.Equal(SearchFolderResult.Added, SearchFolder(share, drives, @"C:\Users\me\NAS\docs", disk));
        Assert.Equal([@"\\nas\share\docs"], share.Folders.ToArray());
        var root = Assert.Single(IndexPlan.Plan(share, drives, new ExclusionFilter(share), disk.Attributes), r => r.Explicit);
        Assert.Equal(@"\\nas\share\docs", root.Name);
        Assert.True(root.Network);
        var now = Coverage(share, drives, @"C:\Users\me\NAS\docs\2024", disk);
        Assert.True(now.Covered);
        Assert.Equal(@"\\nas\share\docs", now.Root);
        Assert.Equal(@"\\nas\share\docs\2024", now.IndexedAs);
    }

    [Fact]
    public void Chain_never_reads_behind_a_link_to_a_share()
    {
        // NAS is a symbolic link to a share, and docs was added under the link's name before; Local is a junction to D:\Real,
        // where Far is a link to a share; Music is a link to a mapped drive.
        var drives = new[] { Fixed(@"C:\"), Fixed(@"D:\"), Network(@"Z:\", @"\\nas\music") };
        var links = new[] { (@"C:\Users\me\NAS", @"\\nas\share"), (@"C:\Users\me\Local", @"D:\Real"), (@"D:\Real\Far", @"\\nas\far"), (@"C:\Users\me\Music", @"Z:\Albums") };
        var settings = new IndexSettings { Folders = [@"C:\Users\me\NAS\docs", @"C:\Users\me\Local\Far\x"] };
        var cases = new (string Folder, string Root, string[] Reads)[]
        {
            // Under the added folder, whose files are reached through the link: found without reading them.
            (@"C:\Users\me\NAS\docs\2024\May", @"C:\Users\me\NAS\docs", [@"C:\Users", @"C:\Users\me", @"C:\Users\me\NAS"]),
            (@"C:\Users\me\Local\Far\x\y", @"C:\Users\me\Local\Far\x", [@"C:\Users", @"C:\Users\me", @"C:\Users\me\Local", @"D:\Real", @"D:\Real\Far"]),
            (@"C:\Users\me\Music\Live\2024", "Z:", [@"C:\Users", @"C:\Users\me", @"C:\Users\me\Music"]),
        };
        foreach (var (folder, root, reads) in cases)
        {
            var disk = On(links);
            var (chain, chainLinks) = IndexPlan.ChainReaders(drives, folder, disk.Attributes, disk.Target);
            var c = IndexPlan.Coverage(settings, drives, folder, attributesOf: chain, linkTargetOf: chainLinks);
            Assert.True(c.Covered, folder);
            Assert.Equal(root, c.Root);
            var byChain = settings.Clone();
            Assert.Equal(SearchFolderResult.AlreadySearched, IndexPlan.SearchFolder(byChain, drives, folder, chain, chainLinks));
            // The folders on the way down to each link, and the links: nothing behind them, each once.
            Assert.True(disk.Read.ToHashSet(Ci).SetEquals(reads), $"{folder}: {string.Join(", ", disk.Read.Distinct(Ci))}");
            Assert.Subset(links.Select(l => l.Item1).ToHashSet(Ci), disk.Followed.ToHashSet(Ci));
            Assert.Equal(disk.Read.Count, disk.Read.Distinct(Ci).Count());
            Assert.Equal(disk.Followed.Count, disk.Followed.Distinct(Ci).Count());
        }

        // The link reader reads only links on the folder's own chain.
        var only = On(links);
        var (_, reader) = IndexPlan.ChainReaders(drives, @"C:\Users\me\Local\Far\x", only.Attributes, only.Target);
        Assert.Equal(@"D:\Real", reader(@"C:\Users\me\Local"));
        Assert.Equal(@"\\nas\far", reader(@"D:\Real\Far"));
        Assert.Null(reader(@"C:\Users\me\NAS")); // a link, off the chain
        Assert.Null(reader(@"C:\Users\me")); // not a link
        Assert.Null(reader(@"D:\Real\Far\x")); // behind the link to the share
        Assert.DoesNotContain(only.Read.Concat(only.Followed), p => p.StartsWith(@"C:\Users\me\NAS", StringComparison.OrdinalIgnoreCase) || p.StartsWith(@"D:\Real\Far\", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Links_to_links_are_followed_and_loops_end()
    {
        var drives = new[] { Fixed(@"C:\"), Fixed(@"D:\"), Fixed(@"E:\"), Subst(@"S:\", @"E:\Work") };
        // Through a volume mounted in a folder that the drive list doesn't have, then two more links.
        var disk = On((@"C:\Mnt", Volume), (@"C:\Mnt\A", @"D:\B"), (@"D:\B\C", @"S:\Real"));
        var settings = new IndexSettings { SkipDrives = [@"C:\", @"D:\"] };
        var c = Coverage(settings, drives, @"C:\Mnt\A\C\x", disk);
        Assert.True(c.Covered);
        Assert.Equal("E:", c.Root);
        Assert.Equal(@"E:\Work\Real\x", c.IndexedAs);
        Assert.Equal(SearchFolderResult.AlreadySearched, SearchFolder(settings, drives, @"C:\Mnt\A\C\x", disk));

        // E: off too: the folder all the links lead to is added.
        settings.SkipDrives = [@"C:\", @"D:\", @"E:\"];
        Assert.Equal(CoverageReason.DriveTurnedOff, Coverage(settings, drives, @"C:\Mnt\A\C\x", disk).Reason);
        Assert.Equal(SearchFolderResult.Added, SearchFolder(settings, drives, @"C:\Mnt\A\C\x", disk));
        Assert.Equal([@"E:\Work\Real\x"], settings.Folders.ToArray());
        Assert.True(Coverage(settings, drives, @"C:\Mnt\A\C\x", disk).Covered);

        // A link to itself, and two links to each other.
        var loops = On((@"C:\Self", @"C:\Self"), (@"C:\Ping", @"C:\Pong"), (@"C:\Pong", @"C:\Ping"));
        var none = new IndexSettings { SkipDrives = [@"D:\"] };
        foreach (var folder in new[] { @"C:\Self\x", @"C:\Ping\x" })
        {
            Assert.Equal(CoverageReason.BehindLink, Coverage(none, drives, folder, loops).Reason);
            Assert.Equal(SearchFolderResult.Added, SearchFolder(none.Clone(), drives, folder, loops));
        }
    }

    [Fact]
    public void A_folders_names_are_its_own_its_drives_and_where_its_links_point()
    {
        var drives = new[] { Fixed(@"C:\"), Fixed(@"D:\"), Network(@"Z:\", @"\\nas\share"), Subst(@"S:\", @"Z:\Work") };
        var disk = On((@"C:\Games\Big", @"D:\Games\Big"));
        // Through a junction, read with the chain readers the app uses: nothing else is read.
        var (chain, links) = IndexPlan.ChainReaders(drives, @"C:\Games\Big\Saves", disk.Attributes, disk.Target);
        Assert.Equal([@"C:\Games\Big\Saves", @"D:\Games\Big\Saves"], IndexPlan.Names(drives, @"C:\Games\Big\Saves", chain, links));
        // A mapped letter is its share too, and a SUBST of it shows a folder there, which has the share's name as well.
        Assert.Equal([@"Z:\", @"\\nas\share"], IndexPlan.Names(drives, @"Z:\", disk.Attributes, disk.Target));
        Assert.Equal([@"S:\Proj", @"Z:\Work\Proj", @"\\nas\share\Work\Proj"], IndexPlan.Names(drives, @"S:\Proj", disk.Attributes, disk.Target));
        Assert.Equal([@"D:\Data"], IndexPlan.Names(drives, @"D:\Data\", disk.Attributes, disk.Target));
        Assert.Empty(IndexPlan.Names(drives, @"relative\path", disk.Attributes, disk.Target));
    }

    [Fact]
    public void A_listing_of_a_folder_behind_a_link_is_named_only_where_the_link_points()
    {
        // Documents\Projects is a junction to D:\Projects, D: is off, and "Show them in searches" added D:\Projects\Active.
        var drives = new[] { Fixed(@"C:\"), Fixed(@"D:\"), Fixed(@"E:\"), Subst(@"S:\", @"C:\Users\Me"), Subst(@"T:\", @"E:\Work") };
        const string Projects = @"C:\Users\Me\Documents\Projects";
        var disk = On((Projects, @"D:\Projects"));
        var settings = new IndexSettings { SkipDrives = [@"D:\"], Folders = [@"D:\Projects\Active"] };
        // As the app finds them: the coverage and the names in one look, with the chain readers.
        List<string> Names(IndexSettings s, string folder, Disk d)
        {
            var (chain, links) = IndexPlan.ChainReaders(drives, folder, d.Attributes, d.Target);
            return IndexPlan.ListingNames(s, drives, IndexPlan.Coverage(s, drives, folder, attributesOf: chain, linkTargetOf: links),
                attributesOf: chain, linkTargetOf: links);
        }

        var c = Coverage(settings, drives, Projects, disk);
        Assert.Equal(CoverageReason.BehindLink, c.Reason);
        Assert.Equal("C:", c.Root);
        Assert.Equal(Projects, c.IndexedAs);
        // C:'s index stops at the junction, so its name for the folder holds no words: only where it points, which
        // D:\Projects\Active lies below. Spelled by a SUBST letter of C:, the same.
        Assert.Equal([@"D:\Projects"], Names(settings, Projects, disk));
        Assert.Equal([@"D:\Projects"], Names(settings, @"S:\Documents\Projects", disk));

        // Searched where it points: its name in the index first, then every name it has, as for any folder.
        var there = new IndexSettings { SkipDrives = [@"D:\"], Folders = [@"D:\Projects"] };
        Assert.Equal([@"D:\Projects", Projects], Names(there, Projects, disk));
        Assert.Equal([@"D:\Projects", @"S:\Documents\Projects", Projects], Names(there, @"S:\Documents\Projects", disk));
        // No link on its way: its name in the index (as the root below spells it), then its own.
        Assert.Equal([@"D:\"], Names(settings, @"D:\", disk));

        // Where the junction points can't be read: nowhere else to look, so its own names stay.
        var unread = On((Projects, ""));
        Assert.Equal(CoverageReason.BehindLink, Coverage(settings, drives, Projects, unread).Reason);
        Assert.Equal([Projects], Names(settings, Projects, unread));

        // A link to a folder behind another link: only where the last one leads (with its drive's names), not the folder
        // on D:, whose index stops at the second link.
        var twice = On((@"C:\A", @"D:\B"), (@"D:\B\C", @"T:\Real"));
        var on = new IndexSettings { SkipDrives = [@"E:\"] };
        Assert.Equal(CoverageReason.BehindLink, Coverage(on, drives, @"C:\A\C", twice).Reason);
        Assert.Equal([@"T:\Real", @"E:\Work\Real"], Names(on, @"C:\A\C", twice));
        Assert.Empty(IndexPlan.ListingNames(on, drives, IndexPlan.Coverage(on, drives, @"relative\path", attributesOf: disk.Attributes, linkTargetOf: disk.Target)));
    }

    [Fact]
    public void A_listing_of_a_folder_behind_a_link_is_named_as_the_way_a_searched_folder_lies_nearest_below_has_it()
    {
        // Documents\Projects is a junction to D:\Projects and D: is off.
        var drives = new[] { Fixed(@"C:\"), Fixed(@"D:\"), Fixed(@"E:\"), Subst(@"S:\", @"C:\Users\Me"), Subst(@"T:\", @"E:\Work") };
        const string Projects = @"C:\Users\Me\Documents\Projects";
        var disk = On((Projects, @"D:\Projects"));
        // As the app finds them: the coverage and the names in one look, with the chain readers.
        List<string> Names(IndexSettings s, string folder, Disk d)
        {
            var (chain, links) = IndexPlan.ChainReaders(drives, folder, d.Attributes, d.Target);
            var c = IndexPlan.Coverage(s, drives, folder, attributesOf: chain, linkTargetOf: links);
            Assert.Equal(CoverageReason.BehindLink, c.Reason);
            return IndexPlan.ListingNames(s, drives, c, attributesOf: chain, linkTargetOf: links);
        }
        static IndexSettings Searching(params string[] folders) => new() { SkipDrives = [@"D:\"], Folders = [.. folders] };

        // Picked by browsing through the junction, a folder is searched as C: names it (C:'s index stops at the junction,
        // so it is a root of its own), and a listing of the junction finds its files' words that way: its name in the index
        // first, then every name, as for any folder. Picked through a SUBST letter of C:, the same.
        Assert.Equal([Projects, @"D:\Projects"], Names(Searching(Projects + @"\Active"), Projects, disk));
        Assert.Equal([Projects, @"S:\Documents\Projects", @"D:\Projects"], Names(Searching(Projects + @"\Active"), @"S:\Documents\Projects", disk));
        Assert.Equal([Projects, @"D:\Projects"], Names(Searching(@"S:\Documents\Projects\Active"), Projects, disk));

        // Searched on both sides of the junction: the side whose folder is nearer, as it holds more of the listing; where
        // the junction points on a tie.
        Assert.Equal([@"D:\Projects"], Names(Searching(Projects + @"\Active\Old", @"D:\Projects\Active"), Projects, disk));
        Assert.Equal([Projects, @"D:\Projects"], Names(Searching(Projects + @"\Active", @"D:\Projects\Active\Old"), Projects, disk));
        Assert.Equal([@"D:\Projects"], Names(Searching(Projects + @"\Active", @"D:\Projects\Old"), Projects, disk));

        // A link to a folder behind another link, searched below the middle step: only as that step has it. The name on C:
        // before it would place the files on C:, whose index stops at the first link.
        var twice = On((@"C:\A", @"D:\B"), (@"D:\B\C", @"T:\Real"));
        Assert.Equal([@"D:\B\C"], Names(new IndexSettings { SkipDrives = [@"E:\"], Folders = [@"D:\B\C\Sub"] }, @"C:\A\C", twice));
    }
}
