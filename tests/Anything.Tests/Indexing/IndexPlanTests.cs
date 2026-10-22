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

    // ---- whether a folder is searched, and why not ----

    static FolderCoverage Coverage(IndexSettings settings, string folder, params DriveEntry[] drives) => CoverageOn(new(), settings, folder, drives);

    /// <summary>Coverage of <paramref name="folder"/>, with folder attribute reads from <paramref name="disk"/> as in <see cref="PlanOn"/>.</summary>
    static FolderCoverage CoverageOn(Dictionary<string, FileAttributes> disk, IndexSettings settings, string folder, params DriveEntry[] drives)
    {
        var ci = new Dictionary<string, FileAttributes>(disk, StringComparer.OrdinalIgnoreCase);
        return IndexPlan.Coverage(settings, drives, folder, attributesOf: p => ci.TryGetValue(p, out var a) ? a : null);
    }

    [Fact]
    public void Folder_on_a_left_out_drive_names_the_rule()
    {
        var settings = new IndexSettings { ExcludeFolders = [@"C:\"], Folders = [@"C:\Users\ROG\Desktop"] };
        var c = Coverage(settings, @"C:\tbay\city-heartbeat", Fixed(@"C:\"), Fixed(@"D:\"));
        Assert.False(c.Covered);
        Assert.Equal(CoverageReason.DriveLeftOut, c.Reason);
        Assert.Equal(@"C:\", c.Rule);
        Assert.Equal(@"C:\", c.Drive);
        Assert.Null(c.Root);
        Assert.Equal(@"C:\tbay\city-heartbeat", c.Folder);
        Assert.Equal(@"C:\tbay\city-heartbeat", c.IndexedAs);

        // The entry that hides the drive, as written.
        settings.ExcludeFolders = [@"D:\Backup", "c:/"];
        var again = Coverage(settings, @"C:\tbay\city-heartbeat\", Fixed(@"C:\"), Fixed(@"D:\"));
        Assert.Equal(CoverageReason.DriveLeftOut, again.Reason);
        Assert.Equal("c:/", again.Rule);
        Assert.Equal(@"C:\tbay\city-heartbeat\", again.Folder);
        Assert.Equal(@"C:\tbay\city-heartbeat", again.IndexedAs);
    }

    [Fact]
    public void Folder_inside_an_added_folder_on_a_left_out_drive_is_covered()
    {
        var settings = new IndexSettings { ExcludeFolders = [@"C:\"], Folders = [@"C:\Users\ROG\Desktop"] };
        foreach (var folder in new[] { @"C:\Users\ROG\Desktop", @"c:\users\rog\desktop\", @"C:\Users\ROG\Desktop\city-heartbeat\src" })
        {
            var c = Coverage(settings, folder, Fixed(@"C:\"));
            Assert.True(c.Covered, folder);
            Assert.Equal(CoverageReason.Covered, c.Reason);
            Assert.Equal(@"C:\Users\ROG\Desktop", c.Root);
            Assert.Null(c.Rule);
            Assert.Null(c.Drive);
        }
        Assert.Equal(@"C:\Users\ROG\Desktop", Coverage(settings, @"c:\users\rog\desktop\", Fixed(@"C:\")).IndexedAs);
        Assert.Equal(@"C:\Users\ROG\Desktop\city-heartbeat\src", Coverage(settings, @"C:\Users\ROG\Desktop\city-heartbeat\src", Fixed(@"C:\")).IndexedAs);

        // Beside the added folder is still the left-out drive.
        Assert.Equal(CoverageReason.DriveLeftOut, Coverage(settings, @"C:\Users\ROG\Documents", Fixed(@"C:\")).Reason);
        Assert.Equal(CoverageReason.DriveLeftOut, Coverage(settings, @"C:\Users\ROG", Fixed(@"C:\")).Reason);
    }

    [Fact]
    public void Folder_left_out_by_its_path_names_the_rule()
    {
        var settings = new IndexSettings { ExcludeFolders = [@"C:\Work\Old\", @"*\bin\obj", "node_modules"] };
        var drives = new[] { Fixed(@"C:\"), Fixed(@"D:\") };

        var old = Coverage(settings, @"C:\Work\Old\2024", drives);
        Assert.Equal(CoverageReason.FolderLeftOut, old.Reason);
        Assert.Equal(@"C:\Work\Old\", old.Rule);
        Assert.Equal("C:", old.Root);
        Assert.Equal(@"C:\Work\Old\2024", old.IndexedAs);
        Assert.Null(old.Drive);
        Assert.Equal(CoverageReason.FolderLeftOut, Coverage(settings, @"C:\Work\Old", drives).Reason);

        var obj = Coverage(settings, @"D:\src\bin\obj\Debug", drives);
        Assert.Equal(CoverageReason.FolderLeftOut, obj.Reason);
        Assert.Equal(@"*\bin\obj", obj.Rule);
        Assert.Equal("D:", obj.Root);

        Assert.True(Coverage(settings, @"C:\Work\Older", drives).Covered);

        // The outermost folder left out is the one named.
        settings.ExcludeFolders = [@"C:\Work\Old\Deep", @"C:\Work"];
        Assert.Equal(@"C:\Work", Coverage(settings, @"C:\Work\Old\Deep\x", drives).Rule);
    }

    [Fact]
    public void Folder_left_out_by_a_name_names_the_rule()
    {
        var settings = new IndexSettings { ExcludeFolders = [@"D:\Backup", "node_modules"], ExcludePatterns = ["*.tmp", "*.bak"] };

        var modules = Coverage(settings, @"C:\src\app\node_modules\pkg", Fixed(@"C:\"));
        Assert.Equal(CoverageReason.NameLeftOut, modules.Reason);
        Assert.Equal("node_modules", modules.Rule);
        Assert.Equal("C:", modules.Root);
        Assert.Null(modules.Drive);

        var bak = Coverage(settings, @"C:\src\old.bak\x", Fixed(@"C:\"));
        Assert.Equal(CoverageReason.NameLeftOut, bak.Reason);
        Assert.Equal("*.bak", bak.Rule);

        Assert.True(Coverage(settings, @"C:\src\app", Fixed(@"C:\")).Covered);
    }

    [Theory]
    [InlineData(true, false, FileAttributes.Hidden, CoverageReason.HiddenLeftOut)]
    [InlineData(false, true, FileAttributes.System, CoverageReason.SystemLeftOut)]
    [InlineData(true, true, FileAttributes.Hidden | FileAttributes.System, CoverageReason.HiddenLeftOut)]
    [InlineData(false, true, FileAttributes.Hidden | FileAttributes.System, CoverageReason.SystemLeftOut)]
    public void Folder_inside_a_left_out_hidden_or_system_folder_says_so(bool hidden, bool system, FileAttributes attributes, CoverageReason reason)
    {
        var disk = new Dictionary<string, FileAttributes>
        {
            [@"C:\Users"] = Dir,
            [@"C:\Users\me"] = Dir,
            [@"C:\Users\me\AppData"] = Dir | attributes,
            [@"C:\Users\me\AppData\Roaming"] = Dir,
            [@"C:\Users\me\Desktop"] = Dir,
        };
        var settings = new IndexSettings { ExcludeHidden = hidden, ExcludeSystem = system };

        var c = CoverageOn(disk, settings, @"C:\Users\me\AppData\Roaming", Fixed(@"C:\"));
        Assert.Equal(reason, c.Reason);
        Assert.Equal("C:", c.Root);
        Assert.Equal(@"C:\Users\me\AppData\Roaming", c.IndexedAs);
        Assert.Null(c.Rule);
        // The folder itself: what is in it is left out with it.
        Assert.Equal(reason, CoverageOn(disk, settings, @"C:\Users\me\AppData", Fixed(@"C:\")).Reason);
        Assert.True(CoverageOn(disk, settings, @"C:\Users\me\Desktop", Fixed(@"C:\")).Covered);
        Assert.True(CoverageOn(disk, new IndexSettings(), @"C:\Users\me\AppData\Roaming", Fixed(@"C:\")).Covered);
    }

    [Fact]
    public void Folder_behind_a_junction_says_so()
    {
        var disk = new Dictionary<string, FileAttributes>
        {
            [@"C:\Users"] = Dir,
            [@"C:\Users\me"] = Dir,
            [@"C:\Users\me\Linked"] = Link,
            [@"C:\Users\me\Linked\Proj"] = Dir,
        };
        var c = CoverageOn(disk, new IndexSettings(), @"C:\Users\me\Linked\Proj", Fixed(@"C:\"));
        Assert.Equal(CoverageReason.BehindLink, c.Reason);
        Assert.Equal("C:", c.Root);
        Assert.Equal(@"C:\Users\me\Linked\Proj", c.IndexedAs);
        Assert.Null(c.Rule);
        Assert.Null(c.Drive);

        // Listed under Folders, the link is a root of its own, which holds what is below it.
        var listed = CoverageOn(disk, new IndexSettings { Folders = [@"C:\Users\me\Linked"] }, @"C:\Users\me\Linked\Proj", Fixed(@"C:\"));
        Assert.True(listed.Covered);
        Assert.Equal(@"C:\Users\me\Linked", listed.Root);

        // Read nothing: taken as an ordinary folder. The folder's own chain is enough to find the link.
        Assert.True(IndexPlan.Coverage(new IndexSettings(), [Fixed(@"C:\")], @"C:\Users\me\Linked\Proj", attributesOf: _ => null).Covered);
        var ci = new Dictionary<string, FileAttributes>(disk, StringComparer.OrdinalIgnoreCase);
        var chain = IndexPlan.ChainReader([Fixed(@"C:\")], @"C:\Users\me\Linked\Proj", p => ci.TryGetValue(p, out var a) ? a : null);
        Assert.Equal(c, IndexPlan.Coverage(new IndexSettings(), [Fixed(@"C:\")], @"C:\Users\me\Linked\Proj", attributesOf: chain));
    }

    [Fact]
    public void Folder_on_a_mounted_volume_that_is_not_indexed_says_why_the_volume_is_not()
    {
        var drives = new[] { Fixed(@"C:\"), Fixed(@"C:\Mount\Data\") };
        var settings = new IndexSettings { SkipDrives = [@"C:\Mount\Data\"] };

        // The volume's own switch, as it would be on a letter of its own; C: holds only the mount point.
        var c = Coverage(settings, @"C:\Mount\Data\Projects", drives);
        Assert.Equal(CoverageReason.DriveTurnedOff, c.Reason);
        Assert.Equal("C:", c.Root);
        Assert.Equal(@"C:\Mount\Data\", c.Drive);
        Assert.Equal(@"C:\Mount\Data\Projects", c.IndexedAs);
        Assert.Null(c.Rule);
        Assert.Equal(CoverageReason.DriveTurnedOff, Coverage(settings, @"C:\Mount\Data", drives).Reason);
        Assert.True(Coverage(settings, @"C:\Mount\Other", drives).Covered);

        // Locked, or of a kind that is off.
        var locked = Coverage(new IndexSettings(), @"C:\Mount\Locked\Docs", Fixed(@"C:\"), Fixed(@"C:\Mount\Locked\", ready: false));
        Assert.Equal(CoverageReason.DriveNotReady, locked.Reason);
        Assert.Equal(@"C:\Mount\Locked\", locked.Drive);
        var card = Coverage(new IndexSettings { IndexRemovableDrives = false }, @"C:\Mount\Card\DCIM", Fixed(@"C:\"), Removable(@"C:\Mount\Card\"));
        Assert.Equal(CoverageReason.DriveKindOff, card.Reason);
        Assert.Equal(@"C:\Mount\Card\", card.Drive);

        // Left out by the folder it is mounted in, which is searched as an added folder: the rule is why the volume is not.
        var inAdded = Coverage(new IndexSettings { ExcludeFolders = [@"D:\Backup", @"C:\Mount\"], Folders = [@"C:\Mount"] }, @"C:\Mount\Data\x", drives);
        Assert.Equal(CoverageReason.DriveLeftOut, inAdded.Reason);
        Assert.Equal(@"C:\Mount\", inAdded.Rule);
        Assert.Equal(@"C:\Mount\Data\", inAdded.Drive);
        Assert.Equal(@"C:\Mount", inAdded.Root);

        // With all drives off, only the added folder around it is searched.
        var all = Coverage(new IndexSettings { IndexNtfsVolumes = false, Folders = [@"C:\Mount"] }, @"C:\Mount\Data\x", drives);
        Assert.Equal(CoverageReason.AllDrivesOff, all.Reason);
        Assert.Equal(@"C:\Mount", all.Root);

        // Indexed, the volume holds it.
        var on = Coverage(new IndexSettings(), @"C:\Mount\Data\Projects", drives);
        Assert.True(on.Covered);
        Assert.Equal(@"C:\Mount\Data", on.Root);

        // The drive around it turned off too: then the volume's own switch is why.
        settings.SkipDrives = [@"C:\", @"C:\Mount\Data\"];
        var off = Coverage(settings, @"C:\Mount\Data\Projects", drives);
        Assert.Equal(CoverageReason.DriveTurnedOff, off.Reason);
        Assert.Equal(@"C:\Mount\Data\", off.Drive);
        Assert.Null(off.Root);
    }

    [Fact]
    public void Folder_on_a_drive_that_is_not_indexed_says_why()
    {
        var skipped = Coverage(new IndexSettings { SkipDrives = [@"D:\"] }, @"D:\Games", Fixed(@"C:\"), Fixed(@"D:\"));
        Assert.Equal(CoverageReason.DriveTurnedOff, skipped.Reason);
        Assert.Equal(@"D:\", skipped.Drive);
        Assert.Null(skipped.Root);
        Assert.Null(skipped.Rule);
        Assert.Equal(@"D:\Games", skipped.IndexedAs);

        var all = Coverage(new IndexSettings { IndexNtfsVolumes = false, Folders = [@"C:\Users\me\Desktop"] }, @"C:\tbay", Fixed(@"C:\"));
        Assert.Equal(CoverageReason.AllDrivesOff, all.Reason);
        Assert.Equal(@"C:\", all.Drive);

        var usb = Coverage(new IndexSettings { IndexRemovableDrives = false }, @"E:\Photos", Fixed(@"C:\"), Removable(@"E:\"));
        Assert.Equal(CoverageReason.DriveKindOff, usb.Reason);
        Assert.Equal(@"E:\", usb.Drive);

        var nas = Coverage(new IndexSettings { IndexNetworkDrives = false }, @"Z:\Albums", Network(@"Z:\", @"\\nas\music"));
        Assert.Equal(CoverageReason.DriveKindOff, nas.Reason);
        Assert.Equal(@"Z:\", nas.Drive);
        Assert.Equal(@"Z:\Albums", nas.IndexedAs);

        var locked = Coverage(new IndexSettings(), @"D:\Private", Fixed(@"C:\"), Fixed(@"D:\", ready: false));
        Assert.Equal(CoverageReason.DriveNotReady, locked.Reason);
        Assert.Equal(@"D:\", locked.Drive);

        // A volume left out by the folder it is mounted in, with the drive around it turned off.
        var mounted = Coverage(new IndexSettings { SkipDrives = [@"C:\"], ExcludeFolders = [@"D:\Backup", @"C:\Mount"] }, @"C:\Mount\Data\x",
            Fixed(@"C:\"), Fixed(@"C:\Mount\Data\"));
        Assert.Equal(CoverageReason.DriveLeftOut, mounted.Reason);
        Assert.Equal(@"C:\Mount", mounted.Rule);
        Assert.Equal(@"C:\Mount\Data\", mounted.Drive);

        // With the drive around it indexed, the folder rule is why.
        var inside = Coverage(new IndexSettings { ExcludeFolders = [@"C:\Mount"] }, @"C:\Mount\Data\x", Fixed(@"C:\"), Fixed(@"C:\Mount\Data\"));
        Assert.Equal(CoverageReason.FolderLeftOut, inside.Reason);
        Assert.Equal(@"C:\Mount", inside.Rule);
        Assert.Equal("C:", inside.Root);
    }

    [Fact]
    public void Volume_left_out_by_a_name_names_it()
    {
        // The folder it is mounted in has a name that is left out, and the drive around it is off. A name pattern is a
        // name left out, as it is with the drive around it on, not a folder on the list of folders to leave out.
        var drives = new[] { Fixed(@"C:\"), Fixed(@"C:\Mount\Data\") };
        var patterns = new IndexSettings { SkipDrives = [@"C:\"], ExcludePatterns = ["*.bak", "Mount"] };
        var pattern = Coverage(patterns, @"C:\Mount\Data\x", drives);
        Assert.Equal(CoverageReason.NameLeftOut, pattern.Reason);
        Assert.Equal("Mount", pattern.Rule);
        Assert.Equal(@"C:\Mount\Data\", pattern.Drive);
        Assert.Null(pattern.Root);
        Assert.Equal(CoverageReason.NameLeftOut, Coverage(new IndexSettings { ExcludePatterns = ["Mount"] }, @"C:\Mount\Data\x", drives).Reason);
        // Inside an added folder around it, too.
        var added = Coverage(new IndexSettings { SkipDrives = [@"C:\"], ExcludePatterns = ["Mount"], Folders = [@"C:\Mount"] }, @"C:\Mount\Data\x", drives);
        Assert.Equal(CoverageReason.NameLeftOut, added.Reason);
        Assert.Equal(@"C:\Mount", added.Root);
        Assert.Equal(@"C:\Mount\Data\", added.Drive);
        // Showing it in searches adds it, and it is searched.
        Assert.Equal(SearchFolderResult.Added, SearchFolder(patterns, @"C:\Mount\Data\x", drives));
        Assert.Equal([@"C:\Mount\Data\x"], patterns.Folders.ToArray());
        Assert.Equal(["*.bak", "Mount"], patterns.ExcludePatterns.ToArray());
        Assert.True(Coverage(patterns, @"C:\Mount\Data\x", drives).Covered);

        // A folder name on the list of folders to leave out is on that list.
        var name = Coverage(new IndexSettings { SkipDrives = [@"C:\"], ExcludeFolders = ["Mou?t"] }, @"C:\Mount\Data\x", drives);
        Assert.Equal(CoverageReason.DriveLeftOut, name.Reason);
        Assert.Equal("Mou?t", name.Rule);

        // An entry on the list of folders to leave out comes first, as Settings finds it.
        var both = Coverage(new IndexSettings { SkipDrives = [@"C:\"], ExcludeFolders = [@"C:\Mount"], ExcludePatterns = ["Mount"] }, @"C:\Mount\Data\x", drives);
        Assert.Equal(@"C:\Mount", both.Rule);
    }

    [Theory]
    [InlineData("Documents")]
    [InlineData(@"Work\Proj")]
    [InlineData(@"\Users\me")]
    [InlineData(@"C:Users")]
    [InlineData(@"\\nas")]
    [InlineData(@"\\nas\")]
    [InlineData(@"\\")]
    [InlineData(@"\\?\Volume{0b6e0f3a-0000-0000-0000-100000000000}\Docs")]
    [InlineData(@"\\.\PhysicalDrive0")]
    [InlineData(@"\\?\\?\C:\Users")]
    [InlineData("")]
    public void Path_that_is_not_a_folder_on_a_drive_or_share_is_not_searched(string path)
    {
        var settings = new IndexSettings { ExcludeFolders = [@"C:\"], Folders = [@"D:\Keep"] };
        var c = Coverage(settings, path, Fixed(@"C:\"), Fixed(@"D:\"));
        Assert.Equal(CoverageReason.NotSearched, c.Reason);
        Assert.Equal(path, c.Folder);
        Assert.Null(c.IndexedAs);
        Assert.Null(c.Root);

        Assert.Equal(SearchFolderResult.NotAFolder, SearchFolder(settings, path, Fixed(@"C:\"), Fixed(@"D:\")));
        Assert.Equal([@"C:\"], settings.ExcludeFolders.ToArray());
        Assert.Equal([@"D:\Keep"], settings.Folders.ToArray());
        Assert.False(IndexPlan.IsWholeDrive([Fixed(@"C:\")], path));
    }

    [Theory]
    [InlineData(@"\\?\C:\Users\me", @"C:\Users\me")]
    [InlineData(@"\\.\C:\Users\me", @"C:\Users\me")]
    [InlineData(@"\\?\c:\users\me\", @"c:\users\me")]
    [InlineData(@"\\?\UNC\nas\docs\letters", @"\\nas\docs\letters")]
    [InlineData(@"C:\Users\me\Docs\..", @"C:\Users\me")]
    public void Long_and_device_path_prefixes_come_off(string path, string folder)
    {
        var settings = new IndexSettings { ExcludeFolders = [@"C:\"] };
        var c = Coverage(settings, path, Fixed(@"C:\"));
        Assert.Equal(folder, c.IndexedAs);
        Assert.Equal(path, c.Folder);

        // Added under the name the index uses, not a second name for the same folder.
        Assert.Equal(SearchFolderResult.Added, SearchFolder(settings, path, Fixed(@"C:\")));
        Assert.Equal([folder], settings.Folders.ToArray());
        var now = Coverage(settings, path, Fixed(@"C:\"));
        Assert.True(now.Covered);
        Assert.Equal(folder, now.Root);
    }

    [Theory]
    [InlineData(@"C:\", true)]
    [InlineData("c:", true)]
    [InlineData(@" C:/ ", true)]
    [InlineData(@"C:\.", true)]
    [InlineData(@"C:\Users\..", true)]
    [InlineData(@"\\?\C:\", true)]
    [InlineData(@"Z:\", true)] // a mapped drive is a drive, with switches of its own
    [InlineData(@"Q:\", true)] // not listed yet
    [InlineData(@"S:\", false)] // a SUBST drive is a folder of the drive it shows
    [InlineData("s:", false)]
    [InlineData(@"C:\Users", false)]
    [InlineData(@"C:\Mount\Data\", false)]
    [InlineData(@"\\nas\music", false)]
    [InlineData("Documents", false)]
    public void Whole_drive_detection(string path, bool expected)
    {
        var drives = new[] { Fixed(@"C:\"), Fixed(@"C:\Mount\Data\"), Network(@"Z:\", @"\\nas\music"), Subst(@"S:\", @"C:\Work") };
        Assert.Equal(expected, IndexPlan.IsWholeDrive(drives, path));
    }

    [Fact]
    public void Folder_on_a_subst_drive_is_covered_as_the_folder_it_shows()
    {
        var drives = new[] { Fixed(@"C:\"), Subst(@"S:\", @"C:\Work") };

        var c = Coverage(new IndexSettings(), @"S:\Proj", drives);
        Assert.True(c.Covered);
        Assert.Equal("C:", c.Root);
        Assert.Equal(@"C:\Work\Proj", c.IndexedAs);
        Assert.Equal(@"S:\Proj", c.Folder);
        Assert.Equal(@"C:\Work", Coverage(new IndexSettings(), @"S:\", drives).IndexedAs);
        Assert.Equal(@"C:\", Coverage(new IndexSettings(), @"C:\", drives).IndexedAs);

        // Left out where its files really are.
        var excluded = Coverage(new IndexSettings { ExcludeFolders = [@"C:\Work\Proj"] }, @"S:\Proj\Sub", drives);
        Assert.Equal(CoverageReason.FolderLeftOut, excluded.Reason);
        Assert.Equal(@"C:\Work\Proj", excluded.Rule);
        Assert.Equal(@"C:\Work\Proj\Sub", excluded.IndexedAs);

        // The drive it shows turned off: that drive is why, not the SUBST letter.
        var off = Coverage(new IndexSettings { SkipDrives = [@"C:\"] }, @"S:\Proj", drives);
        Assert.Equal(CoverageReason.DriveTurnedOff, off.Reason);
        Assert.Equal(@"C:\", off.Drive);
        Assert.Equal(@"C:\Work\Proj", off.IndexedAs);
        Assert.Equal(CoverageReason.AllDrivesOff, Coverage(new IndexSettings { IndexNtfsVolumes = false }, @"S:\Proj", drives).Reason);
    }

    [Fact]
    public void Folder_on_the_second_letter_of_a_share_is_covered_under_the_first()
    {
        var drives = new[] { Network(@"Y:\", @"\\nas\music"), Network(@"Z:\", @"\\nas\music") };
        var c = Coverage(new IndexSettings(), @"Z:\Albums", drives);
        Assert.True(c.Covered);
        Assert.Equal("Y:", c.Root);
        Assert.Equal(@"Y:\Albums", c.IndexedAs);
        Assert.Equal(@"Y:\Albums", Coverage(new IndexSettings(), @"\\NAS\Music\Albums", drives).IndexedAs);

        // A rule for the first letter's path leaves it out.
        var excluded = Coverage(new IndexSettings { ExcludeFolders = [@"Y:\Albums"] }, @"Z:\Albums\Live", drives);
        Assert.Equal(CoverageReason.FolderLeftOut, excluded.Reason);
        Assert.Equal(@"Y:\Albums", excluded.Rule);
        Assert.Equal("Y:", excluded.Root);
        Assert.Equal(@"Y:\Albums\Live", excluded.IndexedAs);
    }

    [Fact]
    public void Folder_above_a_searched_folder_is_named_as_that_folders_root_spells_it()
    {
        // Network drives off, Z:\Docs shown in searches: it goes on Also search these folders by its share.
        var z = Network(@"Z:\", @"\\nas\share");
        var settings = new IndexSettings { IndexNetworkDrives = false };
        Assert.Equal(SearchFolderResult.Added, SearchFolder(settings, @"Z:\Docs", z));
        Assert.Equal([@"\\nas\share\Docs"], settings.Folders.ToArray());
        // Z:\ itself isn't searched, but a folder listing of it finds Docs' files under the share's name.
        var above = Coverage(settings, @"Z:\", z);
        Assert.Equal(CoverageReason.DriveKindOff, above.Reason);
        Assert.Null(above.Root);
        Assert.Equal(@"\\nas\share", above.IndexedAs);
        Assert.Equal(@"\\nas\share\Docs", Coverage(settings, @"Z:\Docs", z).IndexedAs);

        // Picked in Settings › Folders, the root keeps the letter: so does the folder above it.
        Assert.Equal(@"Z:\", Coverage(new IndexSettings { IndexNetworkDrives = false, Folders = [@"Z:\Docs"] }, @"Z:\", z).IndexedAs);
        // Under another letter of the same share, that letter.
        var y = Network(@"Y:\", @"\\nas\share");
        Assert.Equal(@"Y:\", Coverage(new IndexSettings { IndexNetworkDrives = false, Folders = [@"Y:\Docs"] }, @"Z:\", y, z).IndexedAs);
        // A SUBST letter above a folder added by that letter, its drive turned off: the letter, not the folder it shows.
        var subst = new[] { Fixed(@"C:\"), Subst(@"S:\", @"C:\Work") };
        Assert.Equal(@"S:\", Coverage(new IndexSettings { SkipDrives = [@"C:\"], Folders = [@"S:\Proj"] }, @"S:\", subst).IndexedAs);
        Assert.Equal(@"C:\Work", Coverage(new IndexSettings { SkipDrives = [@"C:\"], Folders = [@"C:\Work\Proj"] }, @"S:\", subst).IndexedAs);
        // With no root below, the folder itself, as before.
        Assert.Equal(@"Z:\", Coverage(new IndexSettings { IndexNetworkDrives = false }, @"Z:\", z).IndexedAs);
    }

    [Fact]
    public void Folder_on_a_share_is_searched_once_it_is_listed()
    {
        var c = Coverage(new IndexSettings(), @"\\nas\docs\letters", Fixed(@"C:\"));
        Assert.Equal(CoverageReason.NotSearched, c.Reason);
        Assert.Null(c.Root);
        Assert.Null(c.Drive);
        Assert.Null(c.Rule);
        Assert.Equal(@"\\nas\docs\letters", c.IndexedAs);

        var listed = Coverage(new IndexSettings { Folders = [@"\\nas\docs"] }, @"\\nas\docs\letters", Fixed(@"C:\"));
        Assert.True(listed.Covered);
        Assert.Equal(@"\\nas\docs", listed.Root);
        Assert.Equal(@"\\nas\docs\letters", listed.IndexedAs);

        // A drive that is not listed is not searched either.
        var unknown = Coverage(new IndexSettings(), @"Q:\x", Fixed(@"C:\"));
        Assert.Equal(CoverageReason.NotSearched, unknown.Reason);
        Assert.Null(unknown.Drive);
    }

    [Fact]
    public void Every_planned_folder_and_every_folder_inside_a_planned_root_is_covered()
    {
        var scenarios = new (IndexSettings Settings, DriveEntry[] Drives)[]
        {
            (new() { ExcludeFolders = [@"C:\"], Folders = [@"C:\Users\ROG\Desktop", @"C:\Users\ROG\Downloads\"] }, [Fixed(@"C:\"), Fixed(@"D:\")]),
            (new() { ExcludeFolders = [@"C:\Users\me\AppData", "node_modules"], Folders = [@"C:\Users\me\AppData\Roaming\Tool", @"C:\src\node_modules\pkg", @"C:\src\app"] },
                [Fixed(@"C:\")]),
            (new() { IndexNtfsVolumes = false, Folders = [@"C:\Users\me\Desktop", @"\\nas\docs", @"D:\Work", @"d:\work\Sub"] },
                [Fixed(@"C:\"), Fixed(@"D:\"), Network(@"Z:\", @"\\nas\music")]),
            (new() { SkipDrives = [@"C:\Mount\Data\"], Folders = [@"C:\Mount\Data\Projects", @"C:\Mount\Data"] }, [Fixed(@"C:\"), Fixed(@"C:\Mount\Data\")]),
            (new() { IndexRemovableDrives = false, Folders = [@"C:\Mount\Locked\Docs", @"C:\Mount\Card\DCIM"] },
                [Fixed(@"C:\"), Fixed(@"C:\Mount\Locked\", ready: false), Removable(@"C:\Mount\Card\")]),
            (new() { ExcludeFolders = [@"Y:\Albums"], Folders = [@"Z:\Albums", @"\\nas\music\Singles"] }, [Network(@"Y:\", @"\\nas\music"), Network(@"Z:\", @"\\nas\music")]),
            (new() { ExcludeFolders = [@"C:\Work\Proj"], Folders = [@"S:\Proj\Sub", @"S:\Other"] }, [Fixed(@"C:\"), Subst(@"S:\", @"C:\Work")]),
            (new() { SkipDrives = [@"C:\"], Folders = [@"S:\Proj", @"C:\Work\Proj\Sub", @"C:\Work\Other"] }, [Fixed(@"C:\"), Subst(@"S:\", @"C:\Work")]),
            (new() { IndexNetworkDrives = false, Folders = [@"Z:\Albums", @"\\nas\backup\pc"] }, [Network(@"Z:\", @"\\nas\music")]),
            (new() { IndexNtfsVolumes = false, ExcludeFolders = [@"D:\Work\Keep"], Folders = [@"D:\Work", @"D:\Work\Keep"] }, []),
            (new() { ExcludeHidden = true, ExcludeSystem = true, Folders = [@"C:\Users\me\AppData"] }, [Fixed(@"C:\"), Optical(@"G:\"), Removable(@"E:\", "exFAT")]),
        };
        foreach (var (settings, drives) in scenarios)
        {
            foreach (var folder in settings.Folders)
                Assert.True(Coverage(settings, folder, drives).Covered, folder);
            foreach (var root in Plan(settings, drives))
            {
                Assert.True(Coverage(settings, root.RootPath, drives).Covered, root.RootPath);
                string inside = Path.Combine(root.RootPath, "Sub", "Inner");
                var c = Coverage(settings, inside, drives);
                Assert.True(c.Covered, inside);
                Assert.Equal(root.Name, c.Root);
            }
        }
    }

    [Fact]
    public void Every_planned_folder_and_every_folder_inside_a_planned_root_is_covered_when_attributes_are_read()
    {
        // Links, hidden and system folders between the roots and the folders: those folders get roots of their own.
        var disk = new Dictionary<string, FileAttributes>
        {
            [@"C:\Users\me\AppData"] = Dir | FileAttributes.Hidden,
            [@"C:\Users\me\Linked"] = Link,
            [@"C:\Windows"] = Dir | FileAttributes.System,
            [@"C:\Work\Proj\.git"] = Dir | FileAttributes.Hidden,
            [@"S:\Proj\.git"] = Dir | FileAttributes.Hidden, // the same folder by its SUBST name
            [@"C:\Mount\E"] = Link, // a mount point the drive list does not show
            [@"D:\Work\.cache"] = Dir | FileAttributes.Hidden | FileAttributes.System,
        };
        var scenarios = new (IndexSettings Settings, DriveEntry[] Drives)[]
        {
            (new() { ExcludeHidden = true, Folders = [@"C:\Users\me\AppData\Roaming\Code", @"C:\Users\me\Linked\Proj", @"C:\Users\me\Desktop"] }, [Fixed(@"C:\")]),
            (new() { ExcludeSystem = true, Folders = [@"C:\Windows\Fonts", @"C:\Mount\E\Photos", @"C:\Users\me\AppData"] }, [Fixed(@"C:\")]),
            (new() { ExcludeHidden = true, ExcludeSystem = true, Folders = [@"S:\Proj\.git\hooks", @"C:\Work\Proj\.git", @"S:\Proj"] }, [Fixed(@"C:\"), Subst(@"S:\", @"C:\Work")]),
            (new() { ExcludeHidden = true, SkipDrives = [@"C:\"], Folders = [@"S:\Proj", @"C:\Work\Proj\.git\hooks"] }, [Fixed(@"C:\"), Subst(@"S:\", @"C:\Work")]),
            (new() { IndexNtfsVolumes = false, ExcludeSystem = true, Folders = [@"D:\Work", @"D:\Work\.cache\keep", @"D:\Work\src"] }, [Fixed(@"D:\")]),
            (new() { ExcludeHidden = true, ExcludeFolders = [@"C:\Users"], Folders = [@"C:\Users\me", @"C:\Users\me\AppData\Local", @"C:\Users\me\Linked"] }, [Fixed(@"C:\")]),
        };
        foreach (var (settings, drives) in scenarios)
        {
            var plan = PlanOn(disk, settings, drives);
            Assert.True(plan.Count > 1, string.Join(", ", Names(plan))); // the attributes made some folder a root of its own
            foreach (var folder in settings.Folders)
                Assert.True(CoverageOn(disk, settings, folder, drives).Covered, folder);
            foreach (var root in plan)
            {
                Assert.True(CoverageOn(disk, settings, root.RootPath, drives).Covered, root.RootPath);
                string inside = Path.Combine(root.RootPath, "Sub", "Inner");
                var c = CoverageOn(disk, settings, inside, drives);
                Assert.True(c.Covered, inside);
                Assert.Equal(root.Name, c.Root);
            }
        }
    }

    // ---- making a folder searched ----

    static SearchFolderResult SearchFolder(IndexSettings settings, string folder, params DriveEntry[] drives) =>
        IndexPlan.SearchFolder(settings, drives, folder, _ => null);

    [Fact]
    public void Search_folder_leaves_drives_to_the_drive_include()
    {
        var settings = new IndexSettings { ExcludeFolders = [@"C:\", @"C:\Mount"], SkipDrives = [@"C:\Mount\Data\"], Folders = [@"C:\Users\me\Desktop"] };
        var drives = new[] { Fixed(@"C:\"), Fixed(@"C:\Mount\Data\") };
        foreach (var root in new[] { @"C:\", "c:", @"C:\Mount\Data", @"c:\mount\data\" })
        {
            Assert.Equal(SearchFolderResult.DriveRoot, SearchFolder(settings, root, drives));
            Assert.Equal([@"C:\", @"C:\Mount"], settings.ExcludeFolders.ToArray());
            Assert.Equal([@"C:\Users\me\Desktop"], settings.Folders.ToArray());
            Assert.Equal([@"C:\Mount\Data\"], settings.SkipDrives.ToArray());
        }
    }

    [Fact]
    public void Search_folder_takes_only_its_own_entry_off_the_folders_left_out()
    {
        var settings = new IndexSettings { ExcludeFolders = [@"D:\Backup", " c:/work/old/ ", "node_modules", @"C:\Work\Old\Deeper"], Folders = [@"D:\Keep"] };
        var drives = new[] { Fixed(@"C:\"), Fixed(@"D:\") };
        Assert.Equal(SearchFolderResult.Unexcluded, SearchFolder(settings, @"C:\Work\Old", drives));
        Assert.Equal([@"D:\Backup", "node_modules", @"C:\Work\Old\Deeper"], settings.ExcludeFolders.ToArray());
        Assert.Equal([@"D:\Keep"], settings.Folders.ToArray());
        Assert.True(Coverage(settings, @"C:\Work\Old\2024", drives).Covered);
    }

    [Fact]
    public void Search_folder_adds_a_folder_on_a_left_out_drive()
    {
        var settings = new IndexSettings { ExcludeFolders = [@"C:\"], Folders = [@"C:\Users\ROG\Desktop"] };
        var drives = new[] { Fixed(@"C:\"), Fixed(@"D:\") };
        Assert.Equal(SearchFolderResult.Added, SearchFolder(settings, @"C:\tbay\city-heartbeat\", drives));
        Assert.Equal([@"C:\Users\ROG\Desktop", @"C:\tbay\city-heartbeat"], settings.Folders.ToArray());
        Assert.Equal([@"C:\"], settings.ExcludeFolders.ToArray());
        var now = Coverage(settings, @"C:\tbay\city-heartbeat", drives);
        Assert.True(now.Covered);
        Assert.Equal(@"C:\tbay\city-heartbeat", now.Root);

        // Asked again, it is searched already.
        Assert.Equal(SearchFolderResult.AlreadySearched, SearchFolder(settings, @"c:\tbay\city-heartbeat", drives));
        Assert.Equal(2, settings.Folders.Count);
    }

    [Fact]
    public void Search_folder_adds_a_folder_a_rule_above_it_leaves_out()
    {
        // Its own entry comes off; the one above it stays, and the folder is searched as an added folder.
        var settings = new IndexSettings { ExcludeFolders = [@"C:\Users", @"C:\Users\me\Docs"] };
        Assert.Equal(SearchFolderResult.Added, SearchFolder(settings, @"C:\Users\me\Docs", Fixed(@"C:\")));
        Assert.Equal([@"C:\Users"], settings.ExcludeFolders.ToArray());
        Assert.Equal([@"C:\Users\me\Docs"], settings.Folders.ToArray());
        Assert.True(Coverage(settings, @"C:\Users\me\Docs\2024", Fixed(@"C:\")).Covered);
    }

    [Fact]
    public void Search_folder_adds_hidden_folders_shares_and_folders_on_drives_that_are_off()
    {
        var disk = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase) { [@"C:\Users\me\AppData"] = Dir | FileAttributes.Hidden };
        Func<string, FileAttributes?> read = p => disk.TryGetValue(p, out var a) ? a : null;
        var hidden = new IndexSettings { ExcludeHidden = true };
        Assert.Equal(SearchFolderResult.Added, IndexPlan.SearchFolder(hidden, [Fixed(@"C:\")], @"C:\Users\me\AppData\Roaming", read));
        Assert.Equal([@"C:\Users\me\AppData\Roaming"], hidden.Folders.ToArray());
        Assert.True(IndexPlan.Coverage(hidden, [Fixed(@"C:\")], @"C:\Users\me\AppData\Roaming", attributesOf: read).Covered);

        var share = new IndexSettings();
        Assert.Equal(SearchFolderResult.Added, SearchFolder(share, @"\\nas\docs\letters\", Fixed(@"C:\")));
        Assert.Equal([@"\\nas\docs\letters"], share.Folders.ToArray());

        var off = new IndexSettings { SkipDrives = [@"D:\"], IndexRemovableDrives = false };
        Assert.Equal(SearchFolderResult.Added, SearchFolder(off, @"D:\Games", Fixed(@"D:\"), Removable(@"E:\")));
        Assert.Equal(SearchFolderResult.Added, SearchFolder(off, @"E:\Photos", Fixed(@"D:\"), Removable(@"E:\")));
        Assert.Equal([@"D:\Games", @"E:\Photos"], off.Folders.ToArray());
        Assert.Equal([@"D:\"], off.SkipDrives.ToArray());
        Assert.True(Coverage(off, @"E:\Photos\2024", Fixed(@"D:\"), Removable(@"E:\")).Covered);
    }

    [Fact]
    public void Search_folder_leaves_a_drive_searched_as_an_added_folder_alone()
    {
        // C:\ is on both lists, so C: is searched as an added folder; the bar is about D:, which is off.
        var settings = new IndexSettings { ExcludeFolders = [@"C:\"], Folders = [@"C:\"], SkipDrives = [@"D:\"] };
        var drives = new[] { Fixed(@"C:\"), Fixed(@"D:\"), Fixed(@"C:\Mount\Data\") };
        Assert.True(Coverage(settings, @"C:\", drives).Covered);
        foreach (var root in new[] { @"C:\", "c:" })
        {
            Assert.Equal(SearchFolderResult.AlreadySearched, SearchFolder(settings, root, drives));
            Assert.Equal([@"C:\"], settings.ExcludeFolders.ToArray());
            Assert.Equal([@"C:\"], settings.Folders.ToArray());
        }
        // A volume mounted in a folder of it is left out with it, and goes through the drive's include, as D: does.
        Assert.Equal(SearchFolderResult.DriveRoot, SearchFolder(settings, @"C:\Mount\Data", drives));
        Assert.Equal(SearchFolderResult.DriveRoot, SearchFolder(settings, @"D:\", drives));
        Assert.Equal(SearchFolderResult.Added, SearchFolder(settings, @"D:\Games", drives));
        Assert.Equal([@"C:\"], settings.ExcludeFolders.ToArray());
        Assert.Equal([@"C:\", @"D:\Games"], settings.Folders.ToArray());
        Assert.Equal([@"D:\"], settings.SkipDrives.ToArray());
    }

    [Fact]
    public void Search_folder_changes_nothing_on_a_drive_that_is_not_ready()
    {
        // The drive list was taken while the card reader was empty: the card's folders are searched once it is ready.
        var settings = new IndexSettings { ExcludeFolders = [@"D:\Backup"], Folders = [@"C:\Users\me\Desktop"] };
        var drives = new[] { Fixed(@"C:\"), Removable(@"E:\", ready: false), Fixed(@"C:\Mount\Locked\", ready: false) };
        foreach (var folder in new[] { @"E:\DCIM", @"e:\dcim\100CANON\", @"C:\Mount\Locked\Docs" })
        {
            Assert.Equal(CoverageReason.DriveNotReady, Coverage(settings, folder, drives).Reason);
            Assert.Equal(SearchFolderResult.DriveNotReady, SearchFolder(settings, folder, drives));
            Assert.Equal([@"D:\Backup"], settings.ExcludeFolders.ToArray());
            Assert.Equal([@"C:\Users\me\Desktop"], settings.Folders.ToArray());
        }
        Assert.True(Coverage(settings, @"E:\DCIM", Fixed(@"C:\"), Removable(@"E:\")).Covered);
        // The drive itself is the drive's include, as before.
        Assert.Equal(SearchFolderResult.DriveRoot, SearchFolder(settings, @"E:\", drives));
        Assert.Equal(SearchFolderResult.DriveRoot, SearchFolder(settings, @"C:\Mount\Locked", drives));

        // On the list of folders to leave out too: it stays there until it is asked for again once the drive is ready.
        var left = new IndexSettings { ExcludeFolders = [@"E:\DCIM"] };
        Assert.Equal(CoverageReason.DriveNotReady, Coverage(left, @"E:\DCIM", drives).Reason);
        Assert.Equal(SearchFolderResult.DriveNotReady, SearchFolder(left, @"E:\DCIM", drives));
        Assert.Equal([@"E:\DCIM"], left.ExcludeFolders.ToArray());
        var ready = new[] { Fixed(@"C:\"), Removable(@"E:\") };
        Assert.Equal(CoverageReason.FolderLeftOut, Coverage(left, @"E:\DCIM", ready).Reason);
        Assert.Equal(SearchFolderResult.Unexcluded, SearchFolder(left, @"E:\DCIM", ready));
        Assert.Empty(left.ExcludeFolders);

        // A switch that leaves it out is said first, and showing it in searches adds it.
        var off = new IndexSettings { IndexRemovableDrives = false };
        Assert.Equal(CoverageReason.DriveKindOff, Coverage(off, @"E:\DCIM", drives).Reason);
        Assert.Equal(SearchFolderResult.Added, SearchFolder(off, @"E:\DCIM", drives));
        Assert.Equal([@"E:\DCIM"], off.Folders.ToArray());
    }

    [Fact]
    public void Search_folder_changes_nothing_for_a_folder_already_searched()
    {
        var settings = new IndexSettings { ExcludeFolders = [@"C:\Users\me\AppData"], Folders = [@"D:\Keep"] };
        var drives = new[] { Fixed(@"C:\"), Subst(@"S:\", @"C:\Work") };
        Assert.Equal(SearchFolderResult.AlreadySearched, SearchFolder(settings, @"C:\Users\me\Desktop", drives));
        Assert.Equal(SearchFolderResult.AlreadySearched, SearchFolder(settings, @"S:\Proj", drives));
        Assert.Equal(SearchFolderResult.AlreadySearched, SearchFolder(settings, @"D:\Keep\x", drives));
        Assert.Equal([@"C:\Users\me\AppData"], settings.ExcludeFolders.ToArray());
        Assert.Equal([@"D:\Keep"], settings.Folders.ToArray());
    }

    [Theory]
    [InlineData(@"C:\.")]
    [InlineData(@"c:\Users\..")]
    [InlineData(@"C:\Users\me\..\..\")]
    [InlineData(@"\\?\C:\")]
    public void Search_folder_knows_a_drive_however_it_is_spelled(string root)
    {
        var settings = new IndexSettings { ExcludeFolders = [@"C:\"], Folders = [@"C:\Users\me\Desktop"] };
        Assert.Equal(SearchFolderResult.DriveRoot, SearchFolder(settings, root, Fixed(@"C:\")));
        Assert.Equal([@"C:\"], settings.ExcludeFolders.ToArray());
        Assert.Equal([@"C:\Users\me\Desktop"], settings.Folders.ToArray());
    }

    [Fact]
    public void Search_folder_takes_a_subst_drive_root_as_the_folder_it_shows()
    {
        // S: has no switch of its own: the folder it shows comes off the list of folders to leave out.
        var drives = new[] { Fixed(@"C:\"), Subst(@"S:\", @"C:\Work") };
        var settings = new IndexSettings { ExcludeFolders = [@"C:\Work"] };
        Assert.Equal(CoverageReason.FolderLeftOut, Coverage(settings, @"S:\", drives).Reason);
        Assert.Equal(SearchFolderResult.Unexcluded, SearchFolder(settings, @"S:\", drives));
        Assert.Empty(settings.ExcludeFolders);
        Assert.Empty(settings.Folders);
        Assert.True(Coverage(settings, @"S:\", drives).Covered);
        Assert.True(Coverage(settings, @"S:\Proj", drives).Covered);

        // A SUBST of a share: the share is added.
        var share = new IndexSettings();
        var nas = new[] { Fixed(@"C:\"), Subst(@"S:\", @"\\nas\docs") };
        Assert.Equal(CoverageReason.NotSearched, Coverage(share, "s:", nas).Reason);
        Assert.Equal(SearchFolderResult.Added, SearchFolder(share, "s:", nas));
        Assert.Equal([@"\\nas\docs"], share.Folders.ToArray());
        Assert.True(Coverage(share, @"S:\letters", nas).Covered);

        // A SUBST of a whole drive that is turned off: that drive, added as a folder.
        var off = new IndexSettings { SkipDrives = [@"D:\"] };
        var whole = new[] { Fixed(@"C:\"), Fixed(@"D:\"), Subst(@"S:\", @"D:\") };
        Assert.Equal(SearchFolderResult.Added, SearchFolder(off, @"S:\", whole));
        Assert.Equal(["D:"], off.Folders.ToArray());
        Assert.Equal([@"D:\"], off.SkipDrives.ToArray());
        Assert.True(Coverage(off, @"S:\Games", whole).Covered);
    }

    [Fact]
    public void Search_folder_takes_off_the_entry_that_names_it_under_another_name()
    {
        // The bar names C:\Work\Proj, the entry that hides S:\Proj: that entry comes off.
        var drives = new[] { Fixed(@"C:\"), Subst(@"S:\", @"C:\Work") };
        var settings = new IndexSettings { ExcludeFolders = [@"D:\Backup", @"c:/work/proj/"] };
        Assert.Equal(@"c:/work/proj/", Coverage(settings, @"S:\Proj", drives).Rule);
        Assert.Equal(SearchFolderResult.Unexcluded, SearchFolder(settings, @"S:\Proj", drives));
        Assert.Equal([@"D:\Backup"], settings.ExcludeFolders.ToArray());
        Assert.Empty(settings.Folders);

        // Two letters on one share: Z:\Albums is Y:\Albums in the index.
        var nas = new[] { Network(@"Y:\", @"\\nas\music"), Network(@"Z:\", @"\\nas\music") };
        var albums = new IndexSettings { ExcludeFolders = [@"Y:\Albums"] };
        Assert.Equal(SearchFolderResult.Unexcluded, SearchFolder(albums, @"Z:\Albums", nas));
        Assert.Empty(albums.ExcludeFolders);
        Assert.Empty(albums.Folders);
        Assert.True(Coverage(albums, @"Z:\Albums\Live", nas).Covered);

        // The folder under its SUBST name, typed through the drive it shows.
        var typed = new IndexSettings { ExcludeFolders = [@"S:\Proj", @"C:\Work\Proj"] };
        Assert.Equal(SearchFolderResult.Unexcluded, SearchFolder(typed, @"C:\Work\Proj", drives));
        Assert.Empty(typed.ExcludeFolders);
    }

    [Fact]
    public void Search_folder_adds_the_folder_under_the_name_that_lasts()
    {
        // A SUBST letter or a mapped letter may be gone after a restart (or as administrator): the folder it shows stays.
        var subst = new IndexSettings { SkipDrives = [@"C:\"] };
        var drives = new[] { Fixed(@"C:\"), Subst(@"S:\", @"C:\Work") };
        Assert.Equal(SearchFolderResult.Added, SearchFolder(subst, @"S:\Proj\", drives));
        Assert.Equal([@"C:\Work\Proj"], subst.Folders.ToArray());
        var now = Coverage(subst, @"S:\Proj", drives);
        Assert.True(now.Covered);
        Assert.Equal(@"C:\Work\Proj", now.Root);
        Assert.True(Coverage(subst, @"C:\Work\Proj\Sub", [Fixed(@"C:\")]).Covered); // with S: gone

        var mapped = new IndexSettings { IndexNetworkDrives = false };
        var nas = new[] { Network(@"Z:\", @"\\nas\music"), Subst(@"T:\", @"Z:\Albums") };
        Assert.Equal(SearchFolderResult.Added, SearchFolder(mapped, @"Z:\Albums", nas));
        Assert.Equal([@"\\nas\music\Albums"], mapped.Folders.ToArray());
        Assert.True(Coverage(mapped, @"Z:\Albums\Live", nas).Covered);
        Assert.Equal(SearchFolderResult.AlreadySearched, SearchFolder(mapped, @"T:\Live\2024", nas));
        Assert.True(Coverage(mapped, @"\\nas\music\Albums\Live", []).Covered); // with Z: gone

        var throughSubst = new IndexSettings { IndexNetworkDrives = false };
        Assert.Equal(SearchFolderResult.Added, SearchFolder(throughSubst, @"T:\Live\2024", nas)); // a SUBST of a mapped drive
        Assert.Equal([@"\\nas\music\Albums\Live\2024"], throughSubst.Folders.ToArray());

        // A hidden folder inside a folder added under its SUBST name: added under the name that lasts too.
        var hidden = new IndexSettings { ExcludeHidden = true, SkipDrives = [@"C:\"], Folders = [@"S:\Proj"] };
        var disk = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Work\Proj\.cache"] = Dir | FileAttributes.Hidden, [@"S:\Proj\.cache"] = Dir | FileAttributes.Hidden,
        };
        Func<string, FileAttributes?> read = p => disk.TryGetValue(p, out var a) ? a : null;
        Assert.Equal(CoverageReason.HiddenLeftOut, IndexPlan.Coverage(hidden, drives, @"S:\Proj\.cache", attributesOf: read).Reason);
        Assert.Equal(SearchFolderResult.Added, IndexPlan.SearchFolder(hidden, drives, @"S:\Proj\.cache", read));
        Assert.Equal([@"S:\Proj", @"C:\Work\Proj\.cache"], hidden.Folders.ToArray());
        Assert.True(IndexPlan.Coverage(hidden, drives, @"S:\Proj\.cache\x", attributesOf: read).Covered);
    }

    [Fact]
    public void Search_folder_leaves_every_folder_searched_or_changes_nothing()
    {
        // The settings and folders of the tests above, every way round.
        var drives = new[]
        {
            Fixed(@"C:\"), Fixed(@"D:\", "exFAT"), Fixed(@"C:\Mount\Data\"), Removable(@"E:\"), Removable(@"F:\", ready: false),
            Network(@"Y:\", @"\\nas\music"), Network(@"Z:\", @"\\nas\music"), Subst(@"S:\", @"C:\Work"), Subst(@"T:\", @"Z:\Albums"), Subst(@"U:\", @"D:\"),
        };
        var disk = new Dictionary<string, FileAttributes>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Users\me\AppData"] = Dir | FileAttributes.Hidden, [@"C:\Work\Linked"] = Link, [@"S:\Linked"] = Link, [@"C:\Sys"] = Dir | FileAttributes.System,
            [@"C:\Games"] = Link, [@"C:\NAS"] = Link, [@"C:\Users\me\Pics"] = Link, [@"C:\Mount\Data"] = Link, [@"C:\Mount\Gone"] = Link,
        };
        // Links to another drive, to a share and to a folder beside it (relative); C:\Work\Linked's target can't be read.
        // C:\Mount\Data and C:\Mount\Gone are volumes mounted there, with their targets as .NET gives them; the drive list
        // has only the first.
        var links = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [@"C:\Games"] = @"D:\Games", [@"C:\NAS"] = @"\\nas\docs", [@"C:\Users\me\Pics"] = @"..\Shared\Pics",
            [@"C:\Mount\Data"] = @"Volume{0b6e0f3a-0000-0000-0000-100000000001}\", [@"C:\Mount\Gone"] = @"Volume{0b6e0f3a-0000-0000-0000-100000000002}\",
        };
        Func<string, FileAttributes?> read = p => disk.TryGetValue(p, out var a) ? a : null;
        Func<string, string?> linkOf = p => links.GetValueOrDefault(p);
        var folders = new[]
        {
            @"C:\", @"C:\Work", @"S:\", @"S:\Proj", @"C:\Work\Proj\Sub", @"S:\Linked\x", @"C:\Users\me\AppData\Roaming", @"C:\Sys\x", @"C:\Mount\Data",
            @"C:\Mount\Data\Docs", @"D:\Games", @"U:\", @"U:\Games", @"E:\Photos", @"Y:\Albums", @"Z:\Albums\Live", @"T:\x", @"\\nas\docs", @"Q:\x", "Documents",
            @"F:\DCIM", @"C:\Games\Saves", @"C:\NAS\letters", @"C:\Users\me\Pics\2024", @"C:\Mount\Gone\x", @"C:\Mount\Gone",
        };
        var settingsList = new Func<IndexSettings>[]
        {
            () => new() { ExcludeFolders = [@"C:\"] },
            () => new() { ExcludeFolders = [@"C:\Work", @"Y:\Albums", @"C:\Mount", "Proj"], ExcludePatterns = ["Games"] },
            () => new() { SkipDrives = [@"C:\", @"D:\", @"C:\Mount\Data\"], IndexNetworkDrives = false, IndexRemovableDrives = false },
            () => new() { IndexNtfsVolumes = false, Folders = [@"C:\Work", @"\\nas\music"], ExcludeHidden = true, ExcludeSystem = true },
            () => new() { ExcludeFolders = [@"S:\Proj", @"Z:\Albums\", @"U:\Games"], ExcludeHidden = true, Folders = [@"C:\Users\me"] },
            () => new() { ExcludeFolders = [@"C:\"], Folders = [@"C:\"], SkipDrives = [@"D:\"] },
        };
        foreach (var make in settingsList)
        {
            foreach (var folder in folders)
            {
                var settings = make();
                var before = settings.Clone();
                var result = IndexPlan.SearchFolder(settings, drives, folder, read, linkOf);
                var after = IndexPlan.Coverage(settings, drives, folder, attributesOf: read, linkTargetOf: linkOf);
                if (result is SearchFolderResult.DriveRoot or SearchFolderResult.NotAFolder or SearchFolderResult.AlreadySearched or SearchFolderResult.DriveNotReady)
                {
                    Assert.Equal(before.ExcludeFolders, settings.ExcludeFolders);
                    Assert.Equal(before.Folders, settings.Folders);
                }
                // Never a second way to the same files: no link is added.
                Assert.DoesNotContain(settings.Folders, f => new[] { @"C:\Games", @"C:\NAS", @"C:\Users\me\Pics" }.Any(link => f.StartsWith(link, StringComparison.OrdinalIgnoreCase)));
                // Nor a made-up folder for a volume by its id.
                Assert.DoesNotContain(settings.Folders, f => f.Contains("Volume{", StringComparison.OrdinalIgnoreCase));
                if (result is SearchFolderResult.DriveRoot or SearchFolderResult.NotAFolder or SearchFolderResult.DriveNotReady) continue;
                Assert.True(after.Covered, $"{folder}: {result}, {after.Reason}");
                Assert.Equal(before.SkipDrives, settings.SkipDrives);
                Assert.Equal(before.IndexNtfsVolumes, settings.IndexNtfsVolumes);
            }
        }
    }
}
