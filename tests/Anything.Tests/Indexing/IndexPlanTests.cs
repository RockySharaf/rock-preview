using Anything.Core;
using Anything.Core.Indexing;

namespace Anything.Tests.Indexing;

public sealed class IndexPlanTests
{
    const StringComparison Ci = StringComparison.OrdinalIgnoreCase;

    static DriveEntry Fixed(string root, string fs = "NTFS", bool ready = true) => new(root, DriveKind.Fixed, ready ? fs : "", "", ready, null, null);
    static DriveEntry Removable(string root, string fs = "NTFS", bool ready = true) => new(root, DriveKind.Removable, ready ? fs : "", "", ready, null, null);
    static DriveEntry Optical(string root, bool ready = true) => new(root, DriveKind.Optical, ready ? "UDF" : "", "", ready, null, null);
    static DriveEntry Network(string root, string share, bool ready = true) => new(root, DriveKind.Network, ready ? "NTFS" : "", "", ready, share, null);
    static DriveEntry Subst(string root, string target) => new(root, DriveKind.Fixed, "NTFS", "", true, null, target);

    // Plans never read this machine's disk unless a test asks: the folders below exist only in the tests.
    static List<PlannedRoot> Plan(IndexSettings settings, params DriveEntry[] drives) => PlanOn(new Dictionary<string, FileAttributes>(), settings, drives);

    /// <summary>A plan whose folder attribute reads come from <paramref name="disk"/> (a missing folder reads as unreadable).</summary>
    static List<PlannedRoot> PlanOn(Dictionary<string, FileAttributes> disk, IndexSettings settings, params DriveEntry[] drives)
    {
        var ci = new Dictionary<string, FileAttributes>(disk, StringComparer.OrdinalIgnoreCase);
        return IndexPlan.Plan(settings, drives, new ExclusionFilter(settings), p => ci.TryGetValue(p, out var a) ? a : null);
    }

    const FileAttributes Dir = FileAttributes.Directory;
    const FileAttributes Link = FileAttributes.Directory | FileAttributes.ReparsePoint;

    static PlannedRoot Root(List<PlannedRoot> plan, string name) => Assert.Single(plan, r => string.Equals(r.Name, name, Ci));

    static string[] Names(List<PlannedRoot> plan) => plan.Select(r => r.Name).ToArray();

    // ---- drives ----

    [Fact]
    public void Fixed_ntfs_drive_is_read_from_its_file_table()
    {
        var plan = Plan(new IndexSettings(), Fixed(@"C:\"));
        var c = Assert.Single(plan);
        Assert.Equal(new PlannedRoot("C:", Volume: true, DriveKind.Fixed, Network: false, Ready: true, Explicit: false), c);
        Assert.Equal(@"C:\", c.RootPath);
    }

    [Theory]
    [InlineData("exFAT")]
    [InlineData("ReFS")]
    [InlineData("FAT32")]
    public void Fixed_non_ntfs_drive_is_scanned(string fileSystem)
    {
        var d = Root(Plan(new IndexSettings(), Fixed(@"D:\", fileSystem)), "D:");
        Assert.False(d.Volume);
        Assert.False(d.Network);
        Assert.False(d.Explicit);
        Assert.Equal(DriveKind.Fixed, d.Kind);
    }

    [Fact]
    public void Ntfs_file_system_name_is_matched_case_insensitively()
    {
        Assert.True(Root(Plan(new IndexSettings(), Fixed(@"C:\", "ntfs")), "C:").Volume);
    }

    [Fact]
    public void Removable_drives_follow_their_switch()
    {
        var drives = new[] { Fixed(@"C:\"), Removable(@"E:\"), Removable(@"F:\", "exFAT") };

        var on = Plan(new IndexSettings { IndexRemovableDrives = true }, drives);
        Assert.Equal(["C:", "E:", "F:"], Names(on));
        Assert.True(Root(on, "E:").Volume); // a USB drive formatted NTFS is read from its file table too
        Assert.Equal(DriveKind.Removable, Root(on, "E:").Kind);
        Assert.False(Root(on, "F:").Volume);
        Assert.False(Root(on, "F:").Network);

        var offSettings = new IndexSettings { IndexRemovableDrives = false };
        Assert.Equal(["C:"], Names(Plan(offSettings, drives)));
        Assert.Equal(DriveIndexing.KindOff, IndexPlan.ForDrive(offSettings, drives[1]));
        Assert.Equal(DriveIndexing.Indexed, IndexPlan.ForDrive(offSettings, drives[0]));
    }

    [Fact]
    public void Optical_drives_are_scanned_and_follow_the_removable_switch()
    {
        var dvd = Optical(@"G:\");
        var on = Root(Plan(new IndexSettings(), dvd), "G:");
        Assert.False(on.Volume);
        Assert.Equal(DriveKind.Optical, on.Kind);

        var off = new IndexSettings { IndexRemovableDrives = false };
        Assert.Empty(Plan(off, dvd));
        Assert.Equal(DriveIndexing.KindOff, IndexPlan.ForDrive(off, dvd));

        // An empty DVD drive is not ready.
        Assert.Empty(Plan(new IndexSettings(), Optical(@"G:\", ready: false)));
        Assert.Equal(DriveIndexing.NotReady, IndexPlan.ForDrive(new IndexSettings(), Optical(@"G:\", ready: false)));
    }

    [Fact]
    public void Network_drives_are_planned_whether_or_not_they_answer()
    {
        var plan = Plan(new IndexSettings(), Network(@"Y:\", @"\\nas\photos"), Network(@"Z:\", @"\\nas\music", ready: false));
        Assert.Equal(["Y:", "Z:"], Names(plan));

        var y = Root(plan, "Y:");
        Assert.True(y.Network);
        Assert.False(y.Volume); // never read from a file table, even when the share says NTFS
        Assert.True(y.Ready);
        Assert.Equal(DriveKind.Network, y.Kind);
        Assert.Equal(@"\\nas\photos", y.Share);

        var z = Root(plan, "Z:");
        Assert.True(z.Network);
        Assert.False(z.Volume);
        Assert.False(z.Ready); // offline: kept, its entries stay searchable
        Assert.Equal(@"\\nas\music", z.Share);
        Assert.Equal(DriveIndexing.Indexed, IndexPlan.ForDrive(new IndexSettings(), Network(@"Z:\", @"\\nas\music", ready: false)));
    }

    [Fact]
    public void Network_drives_follow_their_switch()
    {
        var off = new IndexSettings { IndexNetworkDrives = false };
        var z = Network(@"Z:\", @"\\nas\music");
        Assert.Equal(["C:"], Names(Plan(off, Fixed(@"C:\"), z)));
        Assert.Equal(DriveIndexing.KindOff, IndexPlan.ForDrive(off, z));
        Assert.Equal(DriveIndexing.KindOff, IndexPlan.ForDrive(off, Network(@"Z:\", @"\\nas\music", ready: false)));
    }

    [Fact]
    public void Subst_drives_are_skipped()
    {
        var s = Subst(@"S:\", @"C:\Projects");
        Assert.Equal(["C:"], Names(Plan(new IndexSettings(), Fixed(@"C:\"), s)));
        Assert.Equal(DriveIndexing.Subst, IndexPlan.ForDrive(new IndexSettings(), s));
    }

    [Theory]
    [InlineData(@"D:\")]
    [InlineData("D:")]
    [InlineData("d:")]
    [InlineData(@" d:\ ")]
    public void Skipped_drives_are_left_out(string skip)
    {
        var settings = new IndexSettings { SkipDrives = [skip] };
        var d = Fixed(@"D:\");
        Assert.Equal(["C:"], Names(Plan(settings, Fixed(@"C:\"), d)));
        Assert.Equal(DriveIndexing.TurnedOff, IndexPlan.ForDrive(settings, d));
    }

    [Fact]
    public void Skipped_network_drive_and_mounted_volume_are_left_out()
    {
        var settings = new IndexSettings { SkipDrives = [@"Z:\", @"C:\Mount\Data\"] };
        var plan = Plan(settings, Fixed(@"C:\"), Network(@"Z:\", @"\\nas\music"), Fixed(@"C:\Mount\Data\"));
        Assert.Equal(["C:"], Names(plan));
        Assert.Equal(DriveIndexing.TurnedOff, IndexPlan.ForDrive(settings, Fixed(@"C:\Mount\Data\")));
    }

    [Fact]
    public void Legacy_volume_list_turns_off_only_the_fixed_drives_it_does_not_name()
    {
        // 1.4.0 listed the fixed drives that were on; it never listed USB, CD/DVD or network drives.
        var settings = new IndexSettings { Volumes = [@"C:\"] };
        var drives = new[]
        {
            Fixed(@"C:\"), Fixed(@"D:\", "exFAT"), Removable(@"E:\"), Optical(@"G:\"), Network(@"Z:\", @"\\nas\music"), Fixed(@"C:\Mount\Data\"),
        };
        Assert.Equal(["C:", "E:", "G:", "Z:"], Names(Plan(settings, drives)));
        Assert.Equal(DriveIndexing.Indexed, IndexPlan.ForDrive(settings, drives[0]));
        Assert.Equal(DriveIndexing.TurnedOff, IndexPlan.ForDrive(settings, drives[1]));
        Assert.Equal(DriveIndexing.Indexed, IndexPlan.ForDrive(settings, drives[2]));
        Assert.Equal(DriveIndexing.Indexed, IndexPlan.ForDrive(settings, drives[3]));
        Assert.Equal(DriveIndexing.Indexed, IndexPlan.ForDrive(settings, drives[4]));
        Assert.Equal(DriveIndexing.TurnedOff, IndexPlan.ForDrive(settings, drives[5])); // a fixed volume, mounted in a folder
    }

    [Fact]
    public void Legacy_volume_list_leaves_the_other_switches_in_charge()
    {
        var usb = Removable(@"E:\");
        var nas = Network(@"Z:\", @"\\nas\music");
        // Drives the list does not concern follow their kind's switch and their own.
        Assert.Equal(DriveIndexing.KindOff, IndexPlan.ForDrive(new IndexSettings { Volumes = [@"C:\"], IndexRemovableDrives = false }, usb));
        Assert.Equal(DriveIndexing.KindOff, IndexPlan.ForDrive(new IndexSettings { Volumes = [@"C:\"], IndexNetworkDrives = false }, nas));
        Assert.Equal(DriveIndexing.TurnedOff, IndexPlan.ForDrive(new IndexSettings { Volumes = [@"C:\"], SkipDrives = [@"E:\"] }, usb));
        // A fixed drive the list names is still turned off by its own switch.
        Assert.Equal(DriveIndexing.TurnedOff, IndexPlan.ForDrive(new IndexSettings { Volumes = [@"C:\", @"D:\"], SkipDrives = ["d:"] }, Fixed(@"D:\")));
        // Names match however they are spelled; an empty list turns nothing off.
        Assert.Equal(DriveIndexing.Indexed, IndexPlan.ForDrive(new IndexSettings { Volumes = ["c:", @"d:/"] }, Fixed(@"D:\")));
        Assert.Equal(DriveIndexing.Indexed, IndexPlan.ForDrive(new IndexSettings(), Fixed(@"D:\")));
    }

    [Fact]
    public void Whole_drives_off_plans_only_the_folders()
    {
        var settings = new IndexSettings { IndexNtfsVolumes = false, Folders = [@"C:\Users\me\Desktop", @"\\nas\docs"] };
        var plan = Plan(settings, Fixed(@"C:\"), Fixed(@"D:\"), Network(@"Z:\", @"\\nas\music"));
        Assert.Equal([@"C:\Users\me\Desktop", @"\\nas\docs"], Names(plan).Order(StringComparer.OrdinalIgnoreCase).ToArray());
        Assert.All(plan, r => Assert.True(r.Explicit));
        Assert.All(plan, r => Assert.False(r.Volume));
        Assert.Equal(DriveIndexing.AllDrivesOff, IndexPlan.ForDrive(settings, Fixed(@"C:\")));
        Assert.Equal(DriveKind.Fixed, Root(plan, @"C:\Users\me\Desktop").Kind);
        Assert.False(Root(plan, @"C:\Users\me\Desktop").Network);
        Assert.True(Root(plan, @"\\nas\docs").Network);
        Assert.Equal(DriveKind.Network, Root(plan, @"\\nas\docs").Kind);
    }

    [Fact]
    public void Drive_letters_come_before_mounted_volumes_and_duplicates_are_dropped()
    {
        var plan = Plan(new IndexSettings(), Fixed(@"C:\Mount\Data\"), Fixed(@"D:\", "exFAT"), Fixed(@"C:\"), Fixed(@"c:\"));
        Assert.Equal(["C:", "D:", @"C:\Mount\Data"], Names(plan));
        // A volume mounted in a folder is scanned: its file table would need a drive letter to open.
        Assert.False(Root(plan, @"C:\Mount\Data").Volume);
        Assert.Equal(@"C:\Mount\Data", Root(plan, @"C:\Mount\Data").RootPath);
    }

    // ---- left-out drives ----

    [Theory]
    [InlineData(@"C:\")]
    [InlineData("C:")]
    [InlineData("c:/")]
    public void Excluded_drive_root_is_left_out(string exclude)
    {
        var settings = new IndexSettings { ExcludeFolders = [exclude] };
        var c = Fixed(@"C:\");
        var plan = Plan(settings, c, Fixed(@"D:\"));
        Assert.Equal(["D:"], Names(plan));
        Assert.Equal(DriveIndexing.LeftOut, IndexPlan.ForDrive(settings, c));
        Assert.True(IndexPlan.IsLeftOut(new ExclusionFilter(settings), @"C:\"));
        Assert.False(IndexPlan.IsLeftOut(new ExclusionFilter(settings), @"D:\"));
    }

    [Fact]
    public void Excluded_mount_folder_leaves_the_mounted_volume_out()
    {
        var settings = new IndexSettings { ExcludeFolders = [@"C:\Mount"] };
        var mounted = Fixed(@"C:\Mount\Data\");
        Assert.Equal(["C:"], Names(Plan(settings, Fixed(@"C:\"), mounted)));
        Assert.Equal(DriveIndexing.LeftOut, IndexPlan.ForDrive(settings, mounted));
    }

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData("C:", true)]
    [InlineData(" c:/ ", true)]
    [InlineData(@"C:\Users", false)]
    [InlineData(@"\\server\share", false)]
    [InlineData("1:", false)]
    [InlineData("", false)]
    public void Drive_root_detection(string path, bool expected) => Assert.Equal(expected, IndexPlan.IsDriveRoot(path));

    // ---- folders ----

    [Fact]
    public void Folder_inside_an_excluded_drive_is_planned_explicitly()
    {
        var settings = new IndexSettings
        {
            ExcludeFolders = [@"C:\"],
            Folders = [@"C:\Users\me\Desktop", @"C:\Users\me\Downloads\"],
        };
        var plan = Plan(settings, Fixed(@"C:\"), Fixed(@"D:\"));
        Assert.Equal(["D:", @"C:\Users\me\Desktop", @"C:\Users\me\Downloads"], Names(plan));
        foreach (var name in new[] { @"C:\Users\me\Desktop", @"C:\Users\me\Downloads" })
        {
            var r = Root(plan, name);
            Assert.True(r.Explicit);
            Assert.False(r.Volume);
            Assert.False(r.Network);
            Assert.True(r.Ready);
            Assert.Equal(DriveKind.Fixed, r.Kind);
        }
    }

    [Fact]
    public void Folder_inside_an_indexed_drive_is_not_planned_again()
    {
        var settings = new IndexSettings { Folders = [@"C:\Users\me\Desktop", @"c:\users\ME\downloads\"] };
        Assert.Equal(["C:"], Names(Plan(settings, Fixed(@"C:\"))));
    }

    [Fact]
    public void Whole_drive_listed_as_a_folder_is_covered_by_the_drive()
    {
        var settings = new IndexSettings { Folders = [@"C:\"] };
        Assert.Equal(["C:"], Names(Plan(settings, Fixed(@"C:\"))));
    }

    [Fact]
    public void Folder_inside_an_excluded_folder_of_an_indexed_drive_is_planned()
    {
        var settings = new IndexSettings
        {
            ExcludeFolders = [@"C:\Users\me\AppData", "node_modules"],
            Folders = [@"C:\Users\me\AppData\Roaming\Tool", @"C:\src\node_modules\pkg", @"C:\src\app"],
        };
        var plan = Plan(settings, Fixed(@"C:\"));
        Assert.Equal(["C:", @"C:\src\node_modules\pkg", @"C:\Users\me\AppData\Roaming\Tool"], Names(plan));
        Assert.True(Root(plan, @"C:\Users\me\AppData\Roaming\Tool").Explicit);
        Assert.True(Root(plan, @"C:\src\node_modules\pkg").Explicit);
        Assert.DoesNotContain(plan, r => string.Equals(r.Name, @"C:\src\app", Ci));
    }

    [Fact]
    public void Folder_inside_a_mounted_volume_is_covered_by_that_volume()
    {
        var settings = new IndexSettings { Folders = [@"C:\Mount\Data\Projects"] };
        Assert.Equal(["C:", @"C:\Mount\Data"], Names(Plan(settings, Fixed(@"C:\"), Fixed(@"C:\Mount\Data\"))));

        // The outer drive turned off: the mounted volume still holds the folder.
        settings.SkipDrives = [@"C:\"];
        Assert.Equal([@"C:\Mount\Data"], Names(Plan(settings, Fixed(@"C:\"), Fixed(@"C:\Mount\Data\"))));
    }

    [Fact]
    public void Folder_inside_an_excluded_folder_of_a_mounted_volume_is_planned()
    {
        var settings = new IndexSettings
        {
            ExcludeFolders = [@"C:\Mount\Data\Archive"],
            Folders = [@"C:\Mount\Data\Archive\2024"],
        };
        var plan = Plan(settings, Fixed(@"C:\"), Fixed(@"C:\Mount\Data\"));
        Assert.Equal(["C:", @"C:\Mount\Data", @"C:\Mount\Data\Archive\2024"], Names(plan));
        Assert.True(Root(plan, @"C:\Mount\Data\Archive\2024").Explicit);
    }

    [Fact]
    public void Folder_inside_a_turned_off_mounted_volume_is_planned_explicitly()
    {
        // The mounted volume is turned off, C:\ is indexed. C:'s MFT does not contain C:\Mount\Data's files (another
        // volume), so nothing indexes C:\Mount\Data\Projects unless it gets a root of its own.
        var settings = new IndexSettings { SkipDrives = [@"C:\Mount\Data\"], Folders = [@"C:\Mount\Data\Projects"] };
        var plan = Plan(settings, Fixed(@"C:\"), Fixed(@"C:\Mount\Data\"));
        Assert.Equal(["C:", @"C:\Mount\Data\Projects"], Names(plan));
        Assert.True(Root(plan, @"C:\Mount\Data\Projects").Explicit);
    }

    [Fact]
    public void Folder_inside_a_mounted_volume_that_is_not_indexed_is_planned_explicitly()
    {
        // Not ready (locked), left out by its kind, or listed as a folder itself: C: never holds the volume's files.
        var folders = new IndexSettings { IndexRemovableDrives = false, Folders = [@"C:\Mount\Locked\Docs", @"C:\Mount\Card\DCIM", @"C:\Mount\Data"] };
        var plan = Plan(folders, Fixed(@"C:\"), Fixed(@"C:\Mount\Locked\", ready: false), Removable(@"C:\Mount\Card\"), Fixed(@"C:\Mount\Data\"));
        Assert.Equal(["C:", @"C:\Mount\Data", @"C:\Mount\Card\DCIM", @"C:\Mount\Locked\Docs"], Names(plan));
        Assert.False(Root(plan, @"C:\Mount\Data").Explicit); // the volume itself, indexed: the folder entry is covered by it
        Assert.True(Root(plan, @"C:\Mount\Card\DCIM").Explicit);
        Assert.True(Root(plan, @"C:\Mount\Locked\Docs").Explicit);

        var off = new IndexSettings { SkipDrives = [@"C:\Mount\Data\"], Folders = [@"C:\Mount\Data"] };
        var only = Root(Plan(off, Fixed(@"C:\"), Fixed(@"C:\Mount\Data\")), @"C:\Mount\Data");
        Assert.True(only.Explicit);
        Assert.False(only.Volume);
    }

    [Fact]
    public void Folder_inside_a_volume_mounted_in_a_mounted_volume_is_planned_explicitly()
    {
        var settings = new IndexSettings { SkipDrives = [@"C:\Mount\Data\Inner\"], Folders = [@"C:\Mount\Data\Inner\x", @"C:\Mount\Data\Other"] };
        var plan = Plan(settings, Fixed(@"C:\"), Fixed(@"C:\Mount\Data\"), Fixed(@"C:\Mount\Data\Inner\"));
        Assert.Equal(["C:", @"C:\Mount\Data", @"C:\Mount\Data\Inner\x"], Names(plan));
    }

    [Fact]
    public void Folder_below_a_junction_or_unlisted_mount_point_is_planned_explicitly()
    {
        // A junction or symbolic link (or a mount point the drive list does not show: a volume that also has a letter, a
        // second mount of one volume) is a reparse point, which neither the file table nor a folder scan goes through.
        var disk = new Dictionary<string, FileAttributes>
        {
            [@"C:\Users"] = Dir,
            [@"C:\Users\me"] = Dir,
            [@"C:\Users\me\Linked"] = Link,
            [@"C:\Users\me\Linked\Proj"] = Dir,
            [@"C:\Users\me\Docs"] = Dir,
            [@"C:\Mount"] = Dir,
            [@"C:\Mount\E"] = Link,
        };
        var settings = new IndexSettings { Folders = [@"C:\Users\me\Linked\Proj", @"C:\Mount\E\Photos", @"C:\Users\me\Docs"] };
        var plan = PlanOn(disk, settings, Fixed(@"C:\"));
        Assert.Equal(["C:", @"C:\Mount\E\Photos", @"C:\Users\me\Linked\Proj"], Names(plan));
        Assert.All(plan.Skip(1), r => Assert.True(r.Explicit));

        // The link itself listed: its own root, which then holds what is below it.
        settings.Folders = [@"C:\Users\me\Linked", @"C:\Users\me\Linked\Proj"];
        Assert.Equal(["C:", @"C:\Users\me\Linked"], Names(PlanOn(disk, settings, Fixed(@"C:\"))));
    }

    [Fact]
    public void Reparse_points_at_or_above_the_root_do_not_matter()
    {
        // Only folders below the covering root are looked at: the root's own index starts inside it.
        var disk = new Dictionary<string, FileAttributes> { [@"D:\Link"] = Link, [@"D:\Link\Sub"] = Dir };
        var settings = new IndexSettings { IndexNtfsVolumes = false, Folders = [@"D:\Link", @"D:\Link\Sub"] };
        Assert.Equal([@"D:\Link"], Names(PlanOn(disk, settings)));
    }

    [Fact]
    public void Network_folders_are_never_read_while_planning()
    {
        var read = new List<string>();
        var settings = new IndexSettings
        {
            ExcludeHidden = true,
            Folders = [@"Z:\Albums\Live", @"\\nas\docs", @"\\nas\docs\letters\2024", @"C:\Users\me\Desktop"],
        };
        var plan = IndexPlan.Plan(settings, [Fixed(@"C:\"), Network(@"Z:\", @"\\nas\music", ready: false)], new ExclusionFilter(settings),
            p => { read.Add(p); return null; });
        Assert.Equal(["C:", "Z:", @"\\nas\docs"], Names(plan));
        Assert.Equal([@"C:\Users", @"C:\Users\me", @"C:\Users\me\Desktop"], read);
    }

    // ---- hidden and system folders ----

    [Theory]
    [InlineData(true, false, FileAttributes.Hidden)]
    [InlineData(false, true, FileAttributes.System)]
    [InlineData(true, true, FileAttributes.Hidden | FileAttributes.System)]
    public void Folder_inside_a_left_out_hidden_or_system_folder_is_planned_explicitly(bool hidden, bool system, FileAttributes attributes)
    {
        var disk = new Dictionary<string, FileAttributes>
        {
            [@"C:\Users"] = Dir,
            [@"C:\Users\me"] = Dir,
            [@"C:\Users\me\AppData"] = Dir | attributes,
            [@"C:\Users\me\AppData\Roaming"] = Dir,
            [@"C:\Users\me\AppData\Roaming\Code"] = Dir,
            [@"C:\Users\me\Desktop"] = Dir,
        };
        var settings = new IndexSettings
        {
            ExcludeHidden = hidden,
            ExcludeSystem = system,
            Folders = [@"C:\Users\me\AppData\Roaming\Code", @"C:\Users\me\Desktop"],
        };
        var plan = PlanOn(disk, settings, Fixed(@"C:\"));
        Assert.Equal(["C:", @"C:\Users\me\AppData\Roaming\Code"], Names(plan));
        Assert.True(Root(plan, @"C:\Users\me\AppData\Roaming\Code").Explicit);

        // The hidden folder itself listed.
        settings.Folders = [@"C:\Users\me\AppData"];
        Assert.Equal(["C:", @"C:\Users\me\AppData"], Names(PlanOn(disk, settings, Fixed(@"C:\"))));
    }

    [Fact]
    public void Hidden_folders_are_covered_when_they_are_not_left_out()
    {
        var disk = new Dictionary<string, FileAttributes> { [@"C:\Users\me\AppData"] = Dir | FileAttributes.Hidden | FileAttributes.System };
        var settings = new IndexSettings { Folders = [@"C:\Users\me\AppData\Roaming\Code"] };
        Assert.Equal(["C:"], Names(PlanOn(disk, settings, Fixed(@"C:\"))));

        // Hidden is left out, System is not: a folder that is only System is still in the drive's index.
        disk[@"C:\Users\me\AppData"] = Dir | FileAttributes.System;
        settings.ExcludeHidden = true;
        Assert.Equal(["C:"], Names(PlanOn(disk, settings, Fixed(@"C:\"))));
    }

    [Fact]
    public void Hidden_folder_inside_an_extra_folder_gets_its_own_root()
    {
        // An extra folder is scanned with the attribute rules too, so a hidden folder below it is not in its index.
        var disk = new Dictionary<string, FileAttributes> { [@"D:\Work\.cache"] = Dir | FileAttributes.Hidden };
        var settings = new IndexSettings { IndexNtfsVolumes = false, ExcludeHidden = true, Folders = [@"D:\Work", @"D:\Work\.cache\keep", @"D:\Work\src"] };
        Assert.Equal([@"D:\Work", @"D:\Work\.cache\keep"], Names(PlanOn(disk, settings)));
    }

    [Fact]
    public void Attributes_are_read_from_the_disk()
    {
        // The real reader: a hidden folder in a temporary directory, planned as extra folders (no drive needed).
        string temp = Directory.CreateTempSubdirectory("anything-plan-").FullName;
        try
        {
            string hidden = Directory.CreateDirectory(Path.Combine(temp, "hidden", "inner")).Parent!.FullName;
            File.SetAttributes(hidden, FileAttributes.Directory | FileAttributes.Hidden);
            var settings = new IndexSettings { IndexNtfsVolumes = false, Folders = [temp, Path.Combine(temp, "hidden", "inner"), Path.Combine(temp, "missing", "x")] };
            Assert.Equal([temp], Names(IndexPlan.Plan(settings, [], new ExclusionFilter(settings))));

            settings.ExcludeHidden = true;
            Assert.Equal([temp, Path.Combine(temp, "hidden", "inner")], Names(IndexPlan.Plan(settings, [], new ExclusionFilter(settings))));
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch { }
        }
    }

    [Fact]
    public void Junctions_are_found_on_the_disk()
    {
        // Every Windows has "Documents and Settings" as a junction to Users on the system drive.
        string system = Path.GetPathRoot(Environment.SystemDirectory)!;
        string link = Path.Combine(system, "Documents and Settings");
        FileAttributes attributes;
        try { attributes = File.GetAttributes(link); } catch (IOException) { return; } catch (UnauthorizedAccessException) { return; }
        if ((attributes & FileAttributes.ReparsePoint) == 0) return;

        var settings = new IndexSettings { Folders = [Path.Combine(link, "Public")] };
        var plan = IndexPlan.Plan(settings, [Fixed(system)], new ExclusionFilter(settings));
        Assert.Equal([system.TrimEnd('\\'), Path.Combine(link, "Public")], Names(plan));
    }

    [Fact]
    public void Nested_and_repeated_folders_are_planned_once()
    {
        var settings = new IndexSettings
        {
            IndexNtfsVolumes = false,
            Folders = [@"D:\Work\Sub", @"D:\Work", @"d:\work\", @"D:\Other", "  ", @"D:\Work\Sub\Deeper"],
        };
        var plan = Plan(settings);
        Assert.Equal(2, plan.Count);
        Assert.Contains(plan, r => string.Equals(r.Name, @"D:\Work", Ci));
        Assert.Contains(plan, r => string.Equals(r.Name, @"D:\Other", Ci));
        Assert.All(plan, r => Assert.True(r.Explicit));
    }

    [Fact]
    public void Excluded_outer_folder_still_covers_its_subfolders()
    {
        // Listing a folder under Folders searches all of it, even when the exclude list names it.
        var settings = new IndexSettings { IndexNtfsVolumes = false, ExcludeFolders = [@"D:\Work"], Folders = [@"D:\Work", @"D:\Work\Keep"] };
        Assert.Equal([@"D:\Work"], Names(Plan(settings)));
    }

    [Fact]
    public void Subfolder_excluded_inside_an_outer_folder_gets_its_own_root()
    {
        var settings = new IndexSettings { IndexNtfsVolumes = false, ExcludeFolders = [@"D:\Work\Keep"], Folders = [@"D:\Work", @"D:\Work\Keep"] };
        Assert.Equal([@"D:\Work", @"D:\Work\Keep"], Names(Plan(settings)));
    }

    [Fact]
    public void Folder_on_a_mapped_share_is_covered_by_the_mapped_drive()
    {
        var settings = new IndexSettings { Folders = [@"\\nas\music", @"\\NAS\Music\Albums", @"Z:\Albums\Live", @"\\nas\music\"] };
        Assert.Equal(["Z:"], Names(Plan(settings, Network(@"Z:\", @"\\nas\music"))));
    }

    [Fact]
    public void Folder_on_an_unmapped_share_is_a_network_root()
    {
        var settings = new IndexSettings { Folders = [@"\\nas\backup\pc", @"\\nas\musicals"] };
        var plan = Plan(settings, Network(@"Z:\", @"\\nas\music"));
        Assert.Equal(["Z:", @"\\nas\musicals", @"\\nas\backup\pc"], Names(plan)); // folders outermost (shortest) first
        var backup = Root(plan, @"\\nas\backup\pc");
        Assert.True(backup.Network);
        Assert.True(backup.Explicit);
        Assert.False(backup.Volume);
        Assert.Equal(DriveKind.Network, backup.Kind);
    }

    [Fact]
    public void Folder_on_a_mapped_drive_with_network_drives_off_is_a_network_root()
    {
        var settings = new IndexSettings { IndexNetworkDrives = false, Folders = [@"Z:\Albums"] };
        var albums = Root(Plan(settings, Network(@"Z:\", @"\\nas\music")), @"Z:\Albums");
        Assert.True(albums.Network);
        Assert.True(albums.Explicit);
        Assert.Equal(DriveKind.Network, albums.Kind);
        Assert.Equal(@"\\nas\music", albums.Share);
    }

    [Fact]
    public void Two_letters_on_one_share_give_one_root()
    {
        var plan = Plan(new IndexSettings(), Network(@"Z:\", @"\\nas\music"), Network(@"Y:\", @"\\NAS\Music"), Network(@"X:\", @"\\nas\photos"));
        Assert.Equal(["X:", "Y:"], Names(plan));
    }

    [Fact]
    public void Folder_under_the_second_letter_of_a_share_is_covered_by_the_first()
    {
        var settings = new IndexSettings { Folders = [@"Z:\Albums"] };
        var plan = Plan(settings, Network(@"Y:\", @"\\nas\music"), Network(@"Z:\", @"\\nas\music"));
        Assert.Equal(["Y:"], Names(plan));
    }

    [Fact]
    public void Rules_for_a_folder_reached_through_another_name_use_the_indexed_name()
    {
        // Y: indexes the share, so its files are Y:\... paths: a rule for Y:\Albums leaves Z:\Albums out of Y:'s index.
        var settings = new IndexSettings { ExcludeFolders = [@"Y:\Albums"], Folders = [@"Z:\Albums", @"\\nas\music\Singles"] };
        var plan = Plan(settings, Network(@"Y:\", @"\\nas\music"), Network(@"Z:\", @"\\nas\music"));
        Assert.Equal(["Y:", @"Z:\Albums"], Names(plan));
        var albums = Root(plan, @"Z:\Albums");
        Assert.True(albums.Explicit);
        Assert.True(albums.Network);
        Assert.Equal(@"\\nas\music", albums.Share);

        // A rule for the share's own path is not one Y:'s indexer sees.
        settings = new IndexSettings { ExcludeFolders = [@"\\nas\music\Albums"], Folders = [@"Z:\Albums"] };
        Assert.Equal(["Y:"], Names(Plan(settings, Network(@"Y:\", @"\\nas\music"), Network(@"Z:\", @"\\nas\music"))));
    }

    [Theory]
    [InlineData(@"C:\Work", @"S:\Proj")]
    [InlineData(@"C:\Work", @"s:\proj\")]
    [InlineData(@"C:\Work", "S:")]
    [InlineData(@"C:\", @"S:\Work\Proj")]
    public void Folder_on_a_subst_drive_is_covered_by_the_drive_it_shows(string target, string folder)
    {
        var settings = new IndexSettings { Folders = [folder] };
        Assert.Equal(["C:"], Names(Plan(settings, Fixed(@"C:\"), Subst(@"S:\", target))));
    }

    [Fact]
    public void Folder_on_a_subst_drive_is_planned_when_the_drive_it_shows_leaves_it_out()
    {
        var settings = new IndexSettings { ExcludeFolders = [@"C:\Work\Proj"], Folders = [@"S:\Proj\Sub", @"S:\Other"] };
        var plan = Plan(settings, Fixed(@"C:\"), Subst(@"S:\", @"C:\Work"));
        Assert.Equal(["C:", @"S:\Proj\Sub"], Names(plan));
        var sub = Root(plan, @"S:\Proj\Sub");
        Assert.True(sub.Explicit);
        Assert.Equal(DriveKind.Fixed, sub.Kind);

        // C: turned off: the SUBST folder gets a root, which then holds the same folder typed through C:.
        settings = new IndexSettings { SkipDrives = [@"C:\"], Folders = [@"S:\Proj", @"C:\Work\Proj\Sub", @"C:\Work\Other"] };
        Assert.Equal([@"S:\Proj", @"C:\Work\Other"], Names(Plan(settings, Fixed(@"C:\"), Subst(@"S:\", @"C:\Work"))));
    }

    [Fact]
    public void Subst_of_a_mapped_drive_is_followed_to_the_share()
    {
        var settings = new IndexSettings { Folders = [@"T:\Live", @"U:\x"] };
        var drives = new[]
        {
            Network(@"Y:\", @"\\nas\music"), Subst(@"T:\", @"Y:\Albums"),
            Subst(@"U:\", @"V:\"), Subst(@"V:\", @"U:\"), // a loop resolves to nothing
        };
        Assert.Equal(["Y:", @"U:\x"], Names(Plan(settings, drives)));
    }

    [Fact]
    public void Folder_inside_a_subst_drive_of_a_mounted_volume_sees_the_mount()
    {
        // S: shows C:\Mount, where a volume that is turned off is mounted: S:\Data\Docs is C:\Mount\Data\Docs, not in C:'s index.
        var settings = new IndexSettings { SkipDrives = [@"C:\Mount\Data\"], Folders = [@"S:\Data\Docs"] };
        var plan = Plan(settings, Fixed(@"C:\"), Fixed(@"C:\Mount\Data\"), Subst(@"S:\", @"C:\Mount"));
        Assert.Equal(["C:", @"S:\Data\Docs"], Names(plan));
    }

    // ---- why a drive is or is not indexed ----

    [Fact]
    public void For_drive_reasons()
    {
        var all = new IndexSettings();
        Assert.Equal(DriveIndexing.Indexed, IndexPlan.ForDrive(all, Fixed(@"C:\")));
        Assert.Equal(DriveIndexing.Indexed, IndexPlan.ForDrive(all, Removable(@"E:\")));
        Assert.Equal(DriveIndexing.Indexed, IndexPlan.ForDrive(all, Fixed(@"C:\Mount\Data\")));

        Assert.Equal(DriveIndexing.AllDrivesOff, IndexPlan.ForDrive(new IndexSettings { IndexNtfsVolumes = false }, Fixed(@"C:\")));
        Assert.Equal(DriveIndexing.Subst, IndexPlan.ForDrive(all, Subst(@"S:\", @"C:\Projects")));
        Assert.Equal(DriveIndexing.TurnedOff, IndexPlan.ForDrive(new IndexSettings { SkipDrives = [@"C:\"] }, Fixed(@"C:\")));
        Assert.Equal(DriveIndexing.KindOff, IndexPlan.ForDrive(new IndexSettings { IndexRemovableDrives = false }, Removable(@"E:\")));
        Assert.Equal(DriveIndexing.KindOff, IndexPlan.ForDrive(new IndexSettings { IndexRemovableDrives = false }, Optical(@"G:\")));
        Assert.Equal(DriveIndexing.KindOff, IndexPlan.ForDrive(new IndexSettings { IndexNetworkDrives = false }, Network(@"Z:\", @"\\nas\music")));
        Assert.Equal(DriveIndexing.LeftOut, IndexPlan.ForDrive(new IndexSettings { ExcludeFolders = [@"E:\"] }, Removable(@"E:\")));
        Assert.Equal(DriveIndexing.NotReady, IndexPlan.ForDrive(all, Fixed(@"D:\", ready: false))); // BitLocker-locked
        Assert.Equal(DriveIndexing.NotReady, IndexPlan.ForDrive(all, Removable(@"E:\", ready: false))); // empty card reader

        // Precedence: the whole-drives switch first, then SUBST, then the drive's own switch, then its kind.
        var off = new IndexSettings { IndexNtfsVolumes = false, SkipDrives = [@"S:\"] };
        Assert.Equal(DriveIndexing.AllDrivesOff, IndexPlan.ForDrive(off, Subst(@"S:\", @"C:\Projects")));
        Assert.Equal(DriveIndexing.Subst, IndexPlan.ForDrive(new IndexSettings { SkipDrives = [@"S:\"] }, Subst(@"S:\", @"C:\Projects")));
        Assert.Equal(DriveIndexing.TurnedOff,
            IndexPlan.ForDrive(new IndexSettings { SkipDrives = [@"E:\"], IndexRemovableDrives = false }, Removable(@"E:\")));
        Assert.Equal(DriveIndexing.KindOff,
            IndexPlan.ForDrive(new IndexSettings { IndexRemovableDrives = false, ExcludeFolders = [@"E:\"] }, Removable(@"E:\", ready: false)));
        Assert.Equal(DriveIndexing.LeftOut, IndexPlan.ForDrive(new IndexSettings { ExcludeFolders = [@"E:\"] }, Removable(@"E:\", ready: false)));
    }

    [Fact]
    public void For_drive_uses_the_filter_it_is_given()
    {
        var settings = new IndexSettings();
        var filter = new ExclusionFilter(new IndexSettings { ExcludeFolders = [@"D:\"] });
        Assert.Equal(DriveIndexing.LeftOut, IndexPlan.ForDrive(settings, Fixed(@"D:\"), filter));
        Assert.Equal(DriveIndexing.Indexed, IndexPlan.ForDrive(settings, Fixed(@"D:\")));
    }
}
