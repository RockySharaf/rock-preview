namespace Anything.Core.Indexing;

/// <summary>Whether a drive is indexed under the current settings, and if not, why.</summary>
public enum DriveIndexing : byte
{
    Indexed,
    /// <summary>Settings › Indexing › Index whole drives is off.</summary>
    AllDrivesOff,
    /// <summary>
    /// The drive's own switch is off (<see cref="IndexSettings.SkipDrives"/>), or it is a fixed drive missing from the
    /// list of drives 1.4.0 had on (<see cref="IndexSettings.Volumes"/>).
    /// </summary>
    TurnedOff,
    /// <summary>USB drives or network drives are turned off as a group.</summary>
    KindOff,
    /// <summary>The drive (or the folder it is mounted in) is on the list of folders to leave out.</summary>
    LeftOut,
    /// <summary>Not ready: an empty card reader or DVD drive, or a BitLocker-locked drive.</summary>
    NotReady,
    /// <summary>A SUBST drive: it shows files of another drive, which are indexed there.</summary>
    Subst,
}

/// <summary>One root the index should have.</summary>
/// <param name="Name">Index root name: "C:", "Z:", "C:\Mount\Data", "\\server\share" or a folder.</param>
/// <param name="Volume">Read the drive's NTFS file table (MFT + change journal) when possible; otherwise scan its folders.</param>
/// <param name="Network">On the network: scanned in the background, rescanned on a schedule, kept while offline.</param>
/// <param name="Explicit">A folder from Settings › Folders: indexed even inside a drive or folder that is left out.</param>
/// <param name="Share">For a mapped network drive, the share it maps.</param>
internal sealed record PlannedRoot(string Name, bool Volume, DriveKind Kind, bool Network, bool Ready, bool Explicit, string? Share = null)
{
    public string RootPath => Name.Length == 2 && Name[1] == ':' ? Name + "\\" : Name;
}

/// <summary>
/// Decides what gets indexed: every drive Windows has (fixed, USB, network, mounted in a folder; NTFS read from its
/// file table, anything else scanned), minus what Settings turns off, plus the extra folders. One plan is shared by
/// the index manager and Settings, so what Settings shows is what gets indexed.
/// </summary>
public static class IndexPlan
{
    public static DriveIndexing ForDrive(IndexSettings settings, DriveEntry drive, ExclusionFilter? filter = null)
    {
        if (!settings.IndexNtfsVolumes) return DriveIndexing.AllDrivesOff;
        if (drive.SubstTarget != null) return DriveIndexing.Subst;
        if (Contains(settings.SkipDrives, drive.Root)) return DriveIndexing.TurnedOff;
        // 1.4.0 kept the drives that were on, and listed only fixed drives: a fixed drive missing from that list (an
        // external disk that was off and is not plugged in now) stays off. USB, CD/DVD and network drives follow their
        // own switches.
        if (drive.Kind == DriveKind.Fixed && settings.Volumes.Count > 0 && !Contains(settings.Volumes, drive.Root))
            return DriveIndexing.TurnedOff;
        if (drive.Kind is (DriveKind.Removable or DriveKind.Optical) && !settings.IndexRemovableDrives) return DriveIndexing.KindOff;
        if (drive.Kind == DriveKind.Network && !settings.IndexNetworkDrives) return DriveIndexing.KindOff;
        if (IsLeftOut(filter ?? new ExclusionFilter(settings), drive.Root)) return DriveIndexing.LeftOut;
        // An unreachable network drive stays: its files are kept searchable until it is back.
        if (!drive.IsReady && drive.Kind != DriveKind.Network) return DriveIndexing.NotReady;
        return DriveIndexing.Indexed;
    }

    /// <summary>True when the exclude list leaves out all of <paramref name="root"/> (the drive itself, or a folder it is mounted under).</summary>
    public static bool IsLeftOut(ExclusionFilter filter, string root)
    {
        string name = RootIndexer.NormalizeRootName(root);
        if (filter.ExcludesFolder(name, name)) return true;
        string? volume = name.Length > 2 && name[1] == ':' ? name[..2] + "\\" : null; // mounted in a folder of this drive
        return volume != null && filter.ExcludesPathOrAncestor(volume, name);
    }

    /// <summary>True when <paramref name="path"/> is a whole drive ("C:\", "C:") rather than a folder.</summary>
    public static bool IsDriveRoot(string path)
    {
        var p = path.Trim().TrimEnd('\\', '/');
        return p.Length == 2 && p[1] == ':' && char.IsAsciiLetter(p[0]);
    }

    /// <param name="attributesOf">
    /// Reads a folder's attributes, null when it cannot be read (tests pass their own). Only folders between a planned
    /// root and an extra folder inside it are read, never on the network.
    /// </param>
    internal static List<PlannedRoot> Plan(IndexSettings settings, IReadOnlyList<DriveEntry> drives, ExclusionFilter filter,
        Func<string, FileAttributes?>? attributesOf = null)
    {
        var roots = new List<PlannedRoot>();
        var shares = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // Drive letters first, then volumes mounted in folders (their paths sit inside a drive letter's).
        foreach (var d in drives.OrderBy(d => d.IsDriveLetter ? 0 : 1).ThenBy(d => d.Root, StringComparer.OrdinalIgnoreCase))
        {
            if (ForDrive(settings, d, filter) != DriveIndexing.Indexed) continue;
            string name = RootIndexer.NormalizeRootName(d.Root);
            if (roots.Any(r => Same(r.Name, name))) continue;
            // Two letters mapped to one share would list its files twice.
            if (d.NetworkPath != null && !shares.Add(d.NetworkPath)) continue;
            bool ntfs = d.IsReady && d.IsDriveLetter && d.Kind is (DriveKind.Fixed or DriveKind.Removable or DriveKind.Ram)
                        && d.FileSystem.Equals("NTFS", StringComparison.OrdinalIgnoreCase);
            roots.Add(new PlannedRoot(name, ntfs, d.Kind, d.Kind == DriveKind.Network, d.IsReady, Explicit: false, d.NetworkPath));
        }

        // Extra folders, outermost first, unless a planned root already holds them under one of their names.
        var folders = settings.Folders.Where(f => !string.IsNullOrWhiteSpace(f)).Select(RootIndexer.NormalizeRootName)
            .Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(f => Aliases(drives, f)[^1].Length).ToList();
        // Every volume mounted in a folder, indexed or not: none is in the file table of the drive around it.
        var mounts = drives.Where(d => !d.IsDriveLetter).Select(d => RootIndexer.NormalizeRootName(d.Root)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var read = attributesOf ?? ReadAttributes;
        foreach (var name in folders)
        {
            if (Aliases(drives, name).Exists(form => Covered(roots, drives, mounts, form, filter, read))) continue;
            var drive = DriveOf(drives, name);
            bool network = IsUnc(name) || drive?.Kind == DriveKind.Network;
            var kind = network ? DriveKind.Network : drive?.Kind ?? DriveKind.Unknown;
            roots.Add(new PlannedRoot(name, false, kind, network, Ready: true, Explicit: true, Share: drive?.NetworkPath));
        }
        return roots;
    }

    /// <summary>
    /// Whether a planned root already indexes <paramref name="folder"/>: it is inside the root, not left out there, and
    /// the root's index reaches down to it.
    /// </summary>
    static bool Covered(List<PlannedRoot> roots, IReadOnlyList<DriveEntry> drives, HashSet<string> mounts, string folder,
        ExclusionFilter filter, Func<string, FileAttributes?> attributesOf)
    {
        // The innermost root that contains the folder is the one whose index has it (a volume mounted in a folder is not
        // in the outer drive's file table).
        PlannedRoot? best = null;
        string? bestPath = null;
        foreach (var r in roots)
        {
            foreach (var form in Aliases(drives, r.Name))
            {
                if (!Under(folder, form)) continue;
                if (best == null || form.Length > bestPath!.Length) { best = r; bestPath = form; }
            }
        }
        if (best == null) return false;
        string rest = folder[bestPath!.TrimEnd('\\').Length..];
        // Rules see the paths the root's indexer builds: a mapped drive's files under its letter, not under its share.
        var f = best.Explicit ? filter.WithoutAncestorsOf(best.Name) : filter;
        if (f.ExcludesPathOrAncestor(best.Name, best.Name.TrimEnd('\\') + rest)) return false;
        return Reaches(best, bestPath, rest, mounts, f, attributesOf);
    }

    /// <summary>
    /// Whether the root's index goes down from <paramref name="rootPath"/> through each folder of <paramref name="rest"/>.
    /// It stops at a volume mounted in a folder (the outer drive's file table has only the mount point), at a junction or
    /// symbolic link (not in the file table either, and folder scans never follow one), and at a hidden or system folder
    /// when those are left out.
    /// </summary>
    static bool Reaches(PlannedRoot root, string rootPath, string rest, HashSet<string> mounts, ExclusionFilter filter,
        Func<string, FileAttributes?> attributesOf)
    {
        // A share is never asked: the plan is redone on a timer, so it must not wait on a server or change while one is offline.
        bool read = !root.Network;
        string path = rootPath.TrimEnd('\\');
        foreach (var part in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            path += "\\" + part;
            if (mounts.Contains(path)) return false;
            if (read && attributesOf(path) is { } a && ((a & FileAttributes.ReparsePoint) != 0 || filter.ExcludesAttributes(a))) return false;
        }
        return true;
    }

    static FileAttributes? ReadAttributes(string path)
    {
        using (Drives.QuietErrors())
        {
            try { return File.GetAttributes(path); }
            catch (Exception) { return null; } // missing, no access, drive not ready: taken as an ordinary folder
        }
    }

    /// <summary>
    /// The names <paramref name="path"/> is reachable by: its own, then what its drive letter shows (a SUBST drive's
    /// folder, a mapped drive's share). So S:\Proj is found under C: when S: shows C:\Work, and a folder on the second
    /// letter mapped to a share under the first.
    /// </summary>
    static List<string> Aliases(IReadOnlyList<DriveEntry> drives, string path)
    {
        var names = new List<string> { path };
        // A SUBST of a SUBST or of a mapped drive takes more than one step; the cap stops a loop.
        for (int i = 0; i < 4; i++)
        {
            var d = DriveOf(drives, path);
            string? target = d?.SubstTarget ?? d?.NetworkPath;
            if (target == null) break;
            path = RootIndexer.NormalizeRootName(target.TrimEnd('\\') + path[2..]);
            if (names.Exists(n => Same(n, path))) break;
            names.Add(path);
        }
        return names;
    }

    static DriveEntry? DriveOf(IReadOnlyList<DriveEntry> drives, string path)
    {
        if (path.Length < 2 || path[1] != ':') return null;
        return drives.FirstOrDefault(d => d.IsDriveLetter && char.ToUpperInvariant(d.Root[0]) == char.ToUpperInvariant(path[0]));
    }

    internal static bool IsUnc(string path) =>
        path.StartsWith(@"\\", StringComparison.Ordinal) && !path.StartsWith(@"\\?\", StringComparison.Ordinal) && !path.StartsWith(@"\\.\", StringComparison.Ordinal);

    /// <summary><paramref name="path"/> is <paramref name="root"/> or inside it.</summary>
    static bool Under(string path, string root)
    {
        root = root.TrimEnd('\\');
        return path.Equals(root, StringComparison.OrdinalIgnoreCase)
               || (path.Length > root.Length && path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && path[root.Length] == '\\');
    }

    static bool Same(string a, string b) => string.Equals(a.TrimEnd('\\'), b.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase);

    static bool Contains(List<string> list, string root)
    {
        string name = RootIndexer.NormalizeRootName(root);
        foreach (var item in list)
            if (!string.IsNullOrWhiteSpace(item) && Same(RootIndexer.NormalizeRootName(item), name)) return true;
        return false;
    }
}
