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

/// <summary>Whether the index holds a folder's files under the current settings, and if not, why.</summary>
public enum CoverageReason
{
    /// <summary>
    /// A planned root's index holds the folder: the files directly in it are searched. A folder inside it can still be
    /// left out on its own (by a rule, as hidden or system, through a link, or as another volume mounted there).
    /// </summary>
    Covered,
    /// <summary>Settings › Indexing › Index whole drives is off, and no folder on Also search these folders holds it.</summary>
    AllDrivesOff,
    /// <summary>Its drive's own switch is off.</summary>
    DriveTurnedOff,
    /// <summary>Its drive is a USB or network drive, and those are turned off as a group.</summary>
    DriveKindOff,
    /// <summary>Its drive (or the folder the drive is mounted in) is on the list of folders to leave out.</summary>
    DriveLeftOut,
    /// <summary>Its drive is not ready: an empty card reader or DVD drive, or a BitLocker-locked drive.</summary>
    DriveNotReady,
    /// <summary>It, or a folder above it, is on the list of folders to leave out by its path.</summary>
    FolderLeftOut,
    /// <summary>Its name, or the name of a folder above it, is left out (a folder name or a name pattern).</summary>
    NameLeftOut,
    /// <summary>It, or a folder above it, is hidden, and hidden files and folders are left out.</summary>
    HiddenLeftOut,
    /// <summary>It, or a folder above it, is a system folder, and system files and folders are left out.</summary>
    SystemLeftOut,
    /// <summary>
    /// It is reached through a junction or symbolic link, which the drive's index does not go through, and the folder the
    /// link points to is not searched either (or where it points cannot be read).
    /// </summary>
    BehindLink,
    /// <summary>
    /// It is on a volume mounted in a folder that is not indexed, for no reason of the volume's own: the drive around it
    /// holds only the mount point. When the volume's switches or rules leave it out, that reason is given instead.
    /// </summary>
    MountNotIndexed,
    /// <summary>
    /// On a network share or a drive that is not listed, and no folder on Also search these folders holds it. Also a path
    /// that is not a folder on a drive or a share (a relative path, a server, a device).
    /// </summary>
    NotSearched,
}

/// <summary>Whether the index holds a folder (<see cref="IndexPlan.Coverage"/>), and if not, why.</summary>
/// <param name="Folder">The folder asked about, as given.</param>
/// <param name="Root">The planned root whose index holds the folder, or leaves it out; null when no planned root contains it.</param>
/// <param name="IndexedAs">
/// The folder as the index names it: S:\Proj is C:\Work\Proj when S: is a SUBST of C:\Work, a folder on the second
/// letter mapped to a share is under the first, and C:\Games\Big is D:\Games\Big when it is a junction to that folder and
/// D:\Games\Big is searched. A folder no root holds but one is below is named as that root spells it: Z:\ is \\nas\share
/// while \\nas\share\Docs is searched. Otherwise the folder itself; null when it is not a folder on a drive or a share.
/// </param>
/// <param name="Rule">The entry on the list of folders to leave out (or the name pattern) that hides it, as written.</param>
/// <param name="Drive">The drive or mounted volume that is not indexed, as the drive list names it ("D:\", "C:\Mount\Data\").</param>
public sealed record FolderCoverage(string Folder, CoverageReason Reason, string? Root, string? IndexedAs, string? Rule, string? Drive)
{
    public bool Covered => Reason == CoverageReason.Covered;
}

/// <summary>What <see cref="IndexPlan.SearchFolder"/> did to make a folder searched.</summary>
public enum SearchFolderResult
{
    /// <summary>It was searched already: nothing changed.</summary>
    AlreadySearched,
    /// <summary>It came off the list of folders to leave out.</summary>
    Unexcluded,
    /// <summary>It went on Also search these folders.</summary>
    Added,
    /// <summary>
    /// A whole drive (<see cref="IndexPlan.IsWholeDrive"/>) or a volume mounted in a folder: nothing changed, the drive's
    /// own include applies.
    /// </summary>
    DriveRoot,
    /// <summary>
    /// Its drive is not ready (an empty card reader, a locked drive, or a drive list older than the drive): nothing
    /// changed. Every switch lets the drive in, so the drive is searched once it is ready. A folder in it on the list of
    /// folders to leave out stays left out until it is asked for again once the drive is ready.
    /// </summary>
    DriveNotReady,
    /// <summary>Not a folder on a drive or a share (a relative path, a server, a device): nothing changed.</summary>
    NotAFolder,
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

    /// <summary>
    /// True when <paramref name="folder"/> is a whole drive ("C:\", "Z:\"), searched through the drive's own switches.
    /// A SUBST drive's root is not: it is a folder of the drive it shows.
    /// </summary>
    public static bool IsWholeDrive(IReadOnlyList<DriveEntry> drives, string folder) =>
        FolderName(folder) is { } name && IsDriveRoot(name) && DriveOf(drives, name)?.SubstTarget == null;

    /// <summary>
    /// Whether the index holds <paramref name="folder"/> under these settings (the files in it are searched), and if not,
    /// why: the plan's roots are checked under each name the folder has, then the switches of the drive it is on. A folder
    /// reached through a link is held when the folder the link points to is: Root and IndexedAs then name it there.
    /// </summary>
    /// <param name="attributesOf">
    /// Reads a folder's attributes, null when it cannot be read; null reads the disk. Callers on the UI thread pass the
    /// folder's <see cref="ChainReaders"/>: the same answer, reading only the folder and the folders above it, and
    /// nothing on the network.
    /// </param>
    /// <param name="linkTargetOf">
    /// Reads the target a junction or symbolic link holds, as <see cref="FileSystemInfo.LinkTarget"/> gives it (the
    /// link's own data, not the folder it points to), null when it cannot be read. Null reads the disk when
    /// <paramref name="attributesOf"/> is null too, and follows no link when it is not: a folder behind a link is then
    /// <see cref="CoverageReason.BehindLink"/>. Callers on the UI thread pass the folder's <see cref="ChainReaders"/>.
    /// </param>
    public static FolderCoverage Coverage(IndexSettings settings, IReadOnlyList<DriveEntry> drives, string folder, ExclusionFilter? filter = null,
        Func<string, FileAttributes?>? attributesOf = null, Func<string, string?>? linkTargetOf = null)
    {
        if (FolderName(folder) is not { } name) return new(folder, CoverageReason.NotSearched, null, null, null, null);
        filter ??= new ExclusionFilter(settings);
        var read = attributesOf ?? ReadAttributes;
        var roots = Plan(settings, drives, filter, read);
        var mounts = Mounts(drives);
        var coverage = CoverageByName(settings, drives, roots, mounts, folder, name, filter, read);
        if (coverage.Covered) return coverage;
        // Reached through a link, its files are where the link points, and searched there under that name.
        foreach (var target in ThroughLinks(drives, name, read, LinkReader(attributesOf, linkTargetOf)).Skip(1))
            if (CoverageByName(settings, drives, roots, mounts, folder, target, filter, read) is { Covered: true } there) return there;
        return coverage;
    }

    /// <summary>
    /// Whether a planned root holds <paramref name="folder"/>, looked for as <paramref name="name"/> and the other names
    /// its drive gives it, and if not, why.
    /// </summary>
    static FolderCoverage CoverageByName(IndexSettings settings, IReadOnlyList<DriveEntry> drives, List<PlannedRoot> roots, HashSet<string> mounts,
        string folder, string name, ExclusionFilter filter, Func<string, FileAttributes?> read)
    {
        var forms = Aliases(drives, name);
        FolderCoverage? stopped = null;
        foreach (var form in forms)
        {
            var (reason, root, indexedAs, rule) = Explain(roots, drives, mounts, form, filter, read);
            if (root == null) continue;
            if (reason == CoverageReason.Covered) return new(folder, reason, root.Name, AsPath(indexedAs!), null, null);
            // The first name inside a root says why that root leaves it out; another name may still be covered.
            if (stopped != null) continue;
            string? drive = null;
            if (reason == CoverageReason.MountNotIndexed && VolumeOf(drives, form) is { } volume)
            {
                // The volume's own switch or rule is why, as it would be on a letter of its own.
                drive = volume.Root;
                var why = WhyNotIndexed(settings, volume, filter);
                if (why.Reason != CoverageReason.NotSearched) (reason, rule) = why;
            }
            stopped = new(folder, reason, root.Name, AsPath(indexedAs!), rule, drive);
        }
        if (stopped != null) return stopped;

        // No planned root contains it: its drive, or the volume mounted in a folder around it, is not indexed. A root can
        // still lie below it, under another of its names (Z:\ while \\nas\share\Docs, added by its share, is searched).
        string? above = NameAboveRoot(drives, roots, forms);
        foreach (var form in forms)
        {
            var drive = VolumeOf(drives, form);
            if (drive == null || drive.SubstTarget != null) continue; // a SUBST drive's files are under the next name
            var (reason, rule) = WhyNotIndexed(settings, drive, filter);
            if (reason == CoverageReason.NotSearched) continue;
            return new(folder, reason, null, AsPath(above ?? form), rule, drive.Root);
        }
        return new(folder, CoverageReason.NotSearched, null, AsPath(above ?? forms[0]), null, null);
    }

    /// <summary>
    /// The first of <paramref name="forms"/> that a planned root lies below, spelled as that root's name spells it:
    /// "\\nas\share" for Z: while \\nas\share\Docs is searched, "Z:" while Z:\Docs is (picked in Settings), "Y:" while
    /// Y:\Docs is and both letters map one share. A folder listing of it finds those files under that name. Null when no
    /// root is below any of them.
    /// </summary>
    static string? NameAboveRoot(IReadOnlyList<DriveEntry> drives, List<PlannedRoot> roots, List<string> forms)
    {
        foreach (var form in forms)
            foreach (var r in roots)
                foreach (var below in Aliases(drives, r.Name))
                {
                    if (below.Length <= form.Length || !Under(below, form)) continue;
                    // Another name of a root differs from its own only in front: the root's name less the same end is the folder.
                    string tail = below[form.TrimEnd('\\').Length..];
                    if (r.Name.Length > tail.Length && r.Name.EndsWith(tail, StringComparison.OrdinalIgnoreCase)) return r.Name[..^tail.Length];
                }
        return null;
    }

    /// <summary>
    /// Makes <paramref name="folder"/> searched (the bar's "Show them in searches"): entries on the list of folders to
    /// leave out that name it, under any of its names, come off, and when that is not enough it goes on Also search these
    /// folders, under the name that lasts (the folder a SUBST drive shows, a mapped drive's share, the folder a link
    /// points to, so its files are not indexed twice). Those are searched even inside drives and folders left out. A
    /// folder searched already, a whole drive, a volume mounted in a folder and a folder on a drive that is not ready
    /// change nothing: the drive's own include or its readiness is what counts there.
    /// </summary>
    /// <param name="attributesOf">
    /// As for <see cref="Coverage"/>. Pass the readers the bar's coverage was found with, or the two can disagree (a
    /// hidden folder the bar names, which this then finds searched already).
    /// </param>
    /// <param name="linkTargetOf">As for <see cref="Coverage"/>.</param>
    public static SearchFolderResult SearchFolder(IndexSettings settings, IReadOnlyList<DriveEntry> drives, string folder,
        Func<string, FileAttributes?>? attributesOf = null, Func<string, string?>? linkTargetOf = null)
    {
        if (FolderName(folder) is not { } name) return SearchFolderResult.NotAFolder;
        // Searched already comes first: a drive searched as an added folder keeps the entries that leave it out.
        var coverage = Coverage(settings, drives, name, null, attributesOf, linkTargetOf);
        if (coverage.Covered) return SearchFolderResult.AlreadySearched;
        if (IsWholeDrive(drives, name) || drives.Any(d => !d.IsDriveLetter && Same(RootIndexer.NormalizeRootName(d.Root), name)))
            return SearchFolderResult.DriveRoot;
        if (coverage.Reason == CoverageReason.DriveNotReady) return SearchFolderResult.DriveNotReady;

        // An entry names the folder when one of its names is one of the folder's: S:\Proj is C:\Work\Proj when S: shows
        // C:\Work, Z:\Albums is Y:\Albums when both letters map one share, and C:\Games\Big is D:\Games\Big when it is a
        // junction to that folder.
        var paths = ThroughLinks(drives, name, attributesOf ?? ReadAttributes, LinkReader(attributesOf, linkTargetOf)).ToList();
        var forms = paths.SelectMany(p => Aliases(drives, p)).ToList();
        bool Names(string path) => Aliases(drives, path).Exists(a => forms.Exists(f => Same(a, f)));
        // Entries are read the way the exclude list reads them: spaces, quotes, slashes and a trailing backslash aside.
        var kept = settings.ExcludeFolders.Where(f => f == null || !Names(f.Trim().Trim('"').Replace('/', '\\'))).ToList();
        if (kept.Count < settings.ExcludeFolders.Count)
        {
            settings.ExcludeFolders = kept;
            if (Coverage(settings, drives, name, null, attributesOf, linkTargetOf).Covered) return SearchFolderResult.Unexcluded;
        }
        // A SUBST or mapped letter can be gone after a restart or as administrator; the folder it shows stays.
        if (!settings.Folders.Exists(f => !string.IsNullOrWhiteSpace(f) && Names(RootIndexer.NormalizeRootName(f))))
            settings.Folders = [.. settings.Folders, Aliases(drives, paths[^1])[^1]];
        return SearchFolderResult.Added;
    }

    /// <summary>
    /// A reader of folder attributes for <see cref="Coverage"/> and <see cref="SearchFolder"/> of <paramref name="folder"/>
    /// that gives the same answer as reading the disk, for a caller that must not wait (the UI thread): it reads only the
    /// folder and the folders above it, under each of the folder's names and through the links on its way, each once, and
    /// nothing on the network, nor anything behind a link to a share. Every other path reads as null, an ordinary folder:
    /// no other folder changes whether this one is searched. Without a link reader no link is followed;
    /// <see cref="ChainReaders"/> gives this reader with the link reader that goes with it.
    /// </summary>
    public static Func<string, FileAttributes?> ChainReader(IReadOnlyList<DriveEntry> drives, string folder) =>
        ChainReader(drives, folder, ReadAttributes, ReadLinkTarget);

    /// <param name="read">Reads a path the chain allows (tests pass their own).</param>
    /// <param name="linkTargetOf">Reads the target of a link the chain allows (tests pass their own); null follows no link.</param>
    internal static Func<string, FileAttributes?> ChainReader(IReadOnlyList<DriveEntry> drives, string folder, Func<string, FileAttributes?> read,
        Func<string, string?>? linkTargetOf = null)
    {
        if (FolderName(folder) is not { } name) return _ => null;
        return new Chain(drives, name, read, linkTargetOf ?? NoLink).Attributes;
    }

    /// <summary>
    /// The two readers for <see cref="Coverage"/> and <see cref="SearchFolder"/> of <paramref name="folder"/>, for a caller
    /// that must not wait (the UI thread): the folder attributes <see cref="ChainReader"/> reads, and the targets of the
    /// links on the folder's own chain, nothing else and nothing on the network. They share one reading, so each path
    /// is read once. Every other path reads as null.
    /// </summary>
    public static (Func<string, FileAttributes?> Attributes, Func<string, string?> LinkTargets) ChainReaders(IReadOnlyList<DriveEntry> drives, string folder) =>
        ChainReaders(drives, folder, ReadAttributes, ReadLinkTarget);

    /// <param name="read">Reads a path the chain allows (tests pass their own).</param>
    /// <param name="linkTargetOf">Reads the target of a link the chain allows (tests pass their own).</param>
    internal static (Func<string, FileAttributes?> Attributes, Func<string, string?> LinkTargets) ChainReaders(IReadOnlyList<DriveEntry> drives,
        string folder, Func<string, FileAttributes?> read, Func<string, string?> linkTargetOf)
    {
        if (FolderName(folder) is not { } name) return (_ => null, _ => null);
        var chain = new Chain(drives, name, read, linkTargetOf);
        return (chain.Attributes, chain.LinkTarget);
    }

    /// <summary>
    /// What <see cref="ChainReader"/> and <see cref="ChainReaders"/> read for a folder: it and the folders above it,
    /// under each of its names and through the links on its way.
    /// </summary>
    sealed class Chain(IReadOnlyList<DriveEntry> drives, string name, Func<string, FileAttributes?> read, Func<string, string?> linkTargetOf)
    {
        readonly Dictionary<string, FileAttributes?> _attributes = new(StringComparer.OrdinalIgnoreCase);
        readonly Dictionary<string, string?> _targets = new(StringComparer.OrdinalIgnoreCase);
        readonly HashSet<string> _mounts = Mounts(drives);
        List<string>? _forms; // the folder's names, then its names through the links on its way

        public FileAttributes? Attributes(string path) => Allowed(path) ? Read(path) : null;

        public string? LinkTarget(string path) => Allowed(path) && IsLink(Read(path)) ? Target(path) : null;

        bool Allowed(string path)
        {
            var names = Aliases(drives, RootIndexer.NormalizeRootName(path));
            if (names.Exists(n => IsNetwork(drives, n))) return false;
            // The way through the links is found once, reading the folder's own chain.
            _forms ??= [.. ThroughLinks(drives, name, Read, Target).SelectMany(p => Aliases(drives, p))];
            // A folder above another name of it counts too: with S: showing C:\Work, S:\Proj is above C:\Work\Proj\Sub.
            return names.Exists(n => _forms.Exists(f => Under(f, n))) && !Remote(names[^1], 0);
        }

        /// <summary>Reaching <paramref name="path"/> goes over the network: a folder above it is a link to a share, or to a folder behind one.</summary>
        bool Remote(string path, int depth)
        {
            var names = Aliases(drives, path);
            if (depth > 4 || names.Exists(n => IsNetwork(drives, n))) return true;
            string route = names[^1];
            // Each folder above it on the way, from the top: a link there sends the rest of the way where it points.
            for (int end = route.Length > 3 ? route.IndexOf('\\', 3) : -1; end > 0; end = route.IndexOf('\\', end + 1))
            {
                string above = route[..end];
                // A volume mounted in a folder is local, and no link.
                if (!_mounts.Contains(above) && IsLink(Read(above)) && PointsTo(above, Target(above)) is { } target)
                    return Remote(target + route[end..], depth + 1);
            }
            return false;
        }

        FileAttributes? Read(string path)
        {
            string key = RootIndexer.NormalizeRootName(path);
            if (!_attributes.TryGetValue(key, out var a)) _attributes[key] = a = read(path);
            return a;
        }

        string? Target(string path)
        {
            string key = RootIndexer.NormalizeRootName(path);
            if (!_targets.TryGetValue(key, out var t)) _targets[key] = t = linkTargetOf(path);
            return t;
        }
    }

    /// <summary>
    /// <paramref name="name"/>, then the folder with the first link on its way replaced by where it points, and so on:
    /// "C:\Games\Big\Saves", then "D:\Games\Big\Saves" when C:\Games\Big is a junction to D:\Games\Big.
    /// </summary>
    static IEnumerable<string> ThroughLinks(IReadOnlyList<DriveEntry> drives, string name, Func<string, FileAttributes?> attributesOf,
        Func<string, string?> linkTargetOf)
    {
        var mounts = Mounts(drives);
        var seen = new List<string> { name };
        yield return name;
        // A link to a folder behind another link takes more than one step; the cap stops a loop.
        while (seen.Count < 5 && ThroughLink(drives, mounts, name, attributesOf, linkTargetOf) is { } next && !seen.Exists(s => Same(s, next)))
        {
            seen.Add(next);
            yield return name = next;
        }
    }

    /// <summary>
    /// <paramref name="name"/> with the first link on its way (a junction, or a symbolic link to a folder) replaced by
    /// where it points; null when there is none. A volume mounted in a folder that the drive list has is no link: its
    /// own switches say whether it is searched. A share's folders are never read.
    /// </summary>
    static string? ThroughLink(IReadOnlyList<DriveEntry> drives, HashSet<string> mounts, string name, Func<string, FileAttributes?> attributesOf,
        Func<string, string?> linkTargetOf)
    {
        var names = Aliases(drives, name);
        if (names.Exists(n => IsNetwork(drives, n))) return null;
        string route = names[^1]; // where its files are, through SUBST drives
        if (route.Length <= 3) return null; // a drive's root is no link
        for (int end = route.IndexOf('\\', 3); ; end = route.IndexOf('\\', end + 1))
        {
            string path = end < 0 ? route : route[..end];
            // A link that can't be read, or that points at a volume by its id (one mounted there that the list doesn't
            // have), is walked through.
            if (!mounts.Contains(path) && IsLink(attributesOf(path)) && PointsTo(path, linkTargetOf(path)) is { } target && !Same(target, path))
                return RootIndexer.NormalizeRootName(target + route[path.Length..]);
            if (end < 0) return null;
        }
    }

    /// <summary>
    /// The folder the link at <paramref name="link"/> points to, from the target it holds (as
    /// <see cref="FileSystemInfo.LinkTarget"/> gives it): a relative target is taken from the link's own folder. Null for a
    /// volume by its id ("Volume{…}\", a volume mounted in a folder) and for anything else that is not a folder on a drive
    /// or a share.
    /// </summary>
    static string? PointsTo(string link, string? target)
    {
        // The link holds "\??\Volume{…}\" for a volume; .NET takes the "\??\" off.
        if (string.IsNullOrWhiteSpace(target) || target.StartsWith("Volume{", StringComparison.OrdinalIgnoreCase)) return null;
        if (!Path.IsPathFullyQualified(target))
        {
            if (Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(link)) is not { Length: > 0 } folder) return null;
            try { target = Path.GetFullPath(target, folder); }
            catch (Exception) { return null; } // the link's own path is not a full one
        }
        return FolderName(target);
    }

    static bool IsLink(FileAttributes? attributes) => attributes is { } a && (a & FileAttributes.ReparsePoint) != 0;

    /// <summary>On the network: a share, or a folder on a mapped drive.</summary>
    static bool IsNetwork(IReadOnlyList<DriveEntry> drives, string path) => IsUnc(path) || DriveOf(drives, path)?.Kind == DriveKind.Network;

    /// <summary>The link reader <see cref="Coverage"/> uses: the disk's with the disk's attributes, none with a reader of the caller's own.</summary>
    static Func<string, string?> LinkReader(Func<string, FileAttributes?>? attributesOf, Func<string, string?>? linkTargetOf) =>
        linkTargetOf ?? (attributesOf == null ? ReadLinkTarget : NoLink);

    static string? NoLink(string path) => null;

    /// <summary>
    /// <paramref name="path"/> as a root name ("C:", "C:\Users", "\\server\share\docs"), or null when it is not a folder on
    /// a drive or a share: a relative path, a server without a share, a device. A "\\?\" or "\\.\" in front comes off.
    /// </summary>
    static string? FolderName(string path)
    {
        string p = path.Trim().Replace('/', '\\');
        if (p.StartsWith(@"\\?\", StringComparison.Ordinal) || p.StartsWith(@"\\.\", StringComparison.Ordinal))
        {
            p = p[4..];
            if (p.StartsWith(@"UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p[4..];
            else if (!(p.Length >= 2 && p[1] == ':')) return null; // a volume by its id, a disk, a pipe
        }
        bool drive = p.Length >= 2 && p[1] == ':' && char.IsAsciiLetter(p[0]) && (p.Length == 2 || p[2] == '\\');
        if (!drive && !(IsUnc(p) && Path.IsPathFullyQualified(p))) return null;
        string name = RootIndexer.NormalizeRootName(p);
        if (IsUnc(name)) return name[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries).Length >= 2 ? name : null;
        return name.Length >= 2 && name[1] == ':' ? name : null;
    }

    /// <summary>Why <paramref name="drive"/> is not indexed, and the rule that leaves it out; NotSearched when it is indexed.</summary>
    static (CoverageReason Reason, string? Rule) WhyNotIndexed(IndexSettings settings, DriveEntry drive, ExclusionFilter filter)
    {
        var reason = ForDrive(settings, drive, filter) switch
        {
            DriveIndexing.AllDrivesOff => CoverageReason.AllDrivesOff,
            DriveIndexing.TurnedOff => CoverageReason.DriveTurnedOff,
            DriveIndexing.KindOff => CoverageReason.DriveKindOff,
            DriveIndexing.LeftOut => CoverageReason.DriveLeftOut,
            DriveIndexing.NotReady => CoverageReason.DriveNotReady,
            _ => CoverageReason.NotSearched,
        };
        return reason == CoverageReason.DriveLeftOut ? RuleHidingDrive(settings, filter, drive.Root) : (reason, null);
    }

    /// <summary>
    /// The rule that hides the drive at <paramref name="root"/>: the entry on the list of folders to leave out, found as
    /// Settings finds it (each entry on its own), else the name that leaves out a folder the drive is mounted in. A name
    /// pattern (Leave out files named like) is a name left out, not a folder on the list.
    /// </summary>
    static (CoverageReason Reason, string? Rule) RuleHidingDrive(IndexSettings settings, ExclusionFilter filter, string root)
    {
        if (settings.ExcludeFolders.FirstOrDefault(f => IsLeftOut(new ExclusionFilter(new IndexSettings { ExcludeFolders = [f] }), root)) is { } entry)
            return (CoverageReason.DriveLeftOut, entry);
        string name = RootIndexer.NormalizeRootName(root);
        var rule = name.Length > 2 && name[1] == ':' ? filter.RuleHiding(name[..2] + "\\", name) : null;
        return (rule == null || ExclusionFilter.IsPathRule(rule) ? CoverageReason.DriveLeftOut : CoverageReason.NameLeftOut, rule);
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
        var mounts = Mounts(drives);
        var read = attributesOf ?? ReadAttributes;
        foreach (var name in folders)
        {
            if (Aliases(drives, name).Exists(form => Explain(roots, drives, mounts, form, filter, read).Reason == CoverageReason.Covered)) continue;
            var drive = DriveOf(drives, name);
            bool network = IsUnc(name) || drive?.Kind == DriveKind.Network;
            var kind = network ? DriveKind.Network : drive?.Kind ?? DriveKind.Unknown;
            roots.Add(new PlannedRoot(name, false, kind, network, Ready: true, Explicit: true, Share: drive?.NetworkPath));
        }
        return roots;
    }

    /// <summary>
    /// Whether a planned root already indexes <paramref name="folder"/>, and if not, why: it is inside the root, not left
    /// out there, and the root's index reaches down to it. Root is null when no planned root contains the folder;
    /// IndexedAs is the folder's path under the root's name, and Rule the exclude rule that leaves it out.
    /// </summary>
    static (CoverageReason Reason, PlannedRoot? Root, string? IndexedAs, string? Rule) Explain(List<PlannedRoot> roots,
        IReadOnlyList<DriveEntry> drives, HashSet<string> mounts, string folder, ExclusionFilter filter, Func<string, FileAttributes?> attributesOf)
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
        if (best == null) return (CoverageReason.NotSearched, null, null, null);
        string rest = folder[bestPath!.TrimEnd('\\').Length..];
        // Rules see the paths the root's indexer builds: a mapped drive's files under its letter, not under its share.
        string indexedAs = best.Name.TrimEnd('\\') + rest;
        var f = best.Explicit ? filter.WithoutAncestorsOf(best.Name) : filter;
        if (f.RuleHiding(best.Name, indexedAs) is { } rule)
            return (ExclusionFilter.IsPathRule(rule) ? CoverageReason.FolderLeftOut : CoverageReason.NameLeftOut, best, indexedAs, rule);
        return (Reaches(best, bestPath, rest, mounts, f, attributesOf), best, indexedAs, null);
    }

    /// <summary>
    /// Whether the root's index goes down from <paramref name="rootPath"/> through each folder of <paramref name="rest"/>
    /// (<see cref="CoverageReason.Covered"/>), or where it stops: at a volume mounted in a folder (the outer drive's file
    /// table has only the mount point), at a junction or symbolic link (not in the file table either, and folder scans
    /// never follow one), and at a hidden or system folder when those are left out.
    /// </summary>
    static CoverageReason Reaches(PlannedRoot root, string rootPath, string rest, HashSet<string> mounts, ExclusionFilter filter,
        Func<string, FileAttributes?> attributesOf)
    {
        // A share is never asked: the plan is redone on a timer, so it must not wait on a server or change while one is offline.
        bool read = !root.Network;
        string path = rootPath.TrimEnd('\\');
        foreach (var part in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            path += "\\" + part;
            if (mounts.Contains(path)) return CoverageReason.MountNotIndexed;
            if (!read || attributesOf(path) is not { } a) continue;
            if ((a & FileAttributes.ReparsePoint) != 0) return CoverageReason.BehindLink;
            if (filter.ExcludesAttributes(a))
                return filter.ExcludesAttributes(a & FileAttributes.Hidden) ? CoverageReason.HiddenLeftOut : CoverageReason.SystemLeftOut;
        }
        return CoverageReason.Covered;
    }

    /// <summary>Every volume mounted in a folder, indexed or not: none is in the file table of the drive around it.</summary>
    static HashSet<string> Mounts(IReadOnlyList<DriveEntry> drives) =>
        drives.Where(d => !d.IsDriveLetter).Select(d => RootIndexer.NormalizeRootName(d.Root)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    static FileAttributes? ReadAttributes(string path)
    {
        using (Drives.QuietErrors())
        {
            try { return File.GetAttributes(path); }
            catch (Exception) { return null; } // missing, no access, drive not ready: taken as an ordinary folder
        }
    }

    static string? ReadLinkTarget(string path)
    {
        using (Drives.QuietErrors())
        {
            // The link's own data only: the folder it points to is not opened. Not ResolveLinkTarget, whose full name for
            // "Volume{…}\" is a folder of that name in the current directory.
            try { return new DirectoryInfo(path).LinkTarget; }
            catch (Exception) { return null; } // missing, no access
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

    /// <summary>The innermost drive or volume mounted in a folder that holds <paramref name="path"/>; null on a share or an unlisted drive.</summary>
    static DriveEntry? VolumeOf(IReadOnlyList<DriveEntry> drives, string path)
    {
        DriveEntry? best = null;
        string? bestName = null;
        foreach (var d in drives)
        {
            string name = RootIndexer.NormalizeRootName(d.Root);
            if (Under(path, name) && (bestName == null || name.Length > bestName.Length)) { best = d; bestName = name; }
        }
        return best;
    }

    /// <summary>A root name as a path: "C:" is "C:\".</summary>
    static string AsPath(string name) => name.Length == 2 && name[1] == ':' ? name + "\\" : name;

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
