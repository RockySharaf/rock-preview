using Anything.Core.Indexing;

namespace Anything.Core.Content.Indexing;

/// <summary>
/// Root name → volume key, relative prefix and display root (GetVolumeNameForVolumeMountPoint, WNetGetConnection);
/// resolved and cached per root on the follower, never on demand for a search or a snippet. Keys: "vol:{GUID}" for a
/// local volume, "unc:\\SERVER\SHARE" for a share or a mapped letter, "path:&lt;UPPER ROOT&gt;" when Windows can't tell.
/// </summary>
internal static class VolumeKeys
{
    /// <summary>The key of the volume holding a root (or any path below one), the path below the volume, and how to show the volume.</summary>
    public static (string Key, string RelativePrefix, string DisplayRoot) For(string rootName)
    {
        string path = Clean(rootName);
        try
        {
            if (TryUnc(path, out var unc)) return unc;
            if (path.Length >= 2 && path[1] == ':' && char.IsAsciiLetter(path[0]))
            {
                string letter = char.ToUpperInvariant(path[0]) + ":";
                string rest = path.Length > 2 ? path[2..].Trim('\\') : "";
                var kind = Drives.KindOf(letter + "\\");
                if (kind == DriveKind.Network)
                {
                    // A mapped letter: the share's own key, so Z:\a and \\nas\jobs\a are the same document.
                    if (ContentNative.MappedTarget(letter) is { } target && TryUnc(Clean(target), out var mapped))
                        return (mapped.Key, Join(mapped.RelativePrefix, rest), letter + "\\");
                    return Fallback(letter, rest);
                }
                string full = rest.Length == 0 ? letter + "\\" : letter + "\\" + rest;
                string mount = ContentNative.VolumePathOf(full.EndsWith('\\') ? full : full + "\\") ?? letter + "\\";
                if (!mount.EndsWith('\\')) mount += "\\";
                string below = full.Length > mount.Length && full.StartsWith(mount, StringComparison.OrdinalIgnoreCase)
                    ? full[mount.Length..].Trim('\\')
                    : string.Equals(full.TrimEnd('\\'), mount.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase) ? "" : rest;
                if (ContentNative.VolumeNameOf(mount) is { } volume && GuidOf(volume) is { } guid)
                    return ("vol:" + guid, below, mount);
                return Fallback(mount.TrimEnd('\\'), below);
            }
        }
        catch (Exception e) when (e is not OutOfMemoryException) { }
        return ("path:" + path.ToUpperInvariant(), "", path);
    }

    /// <summary>The key, prefix and display root of a UNC path ("\\server\share\sub" → "unc:\\SERVER\SHARE", "sub"); no network access.</summary>
    internal static bool TryUnc(string path, out (string Key, string RelativePrefix, string DisplayRoot) result)
    {
        result = default;
        if (!path.StartsWith(@"\\", StringComparison.Ordinal) || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal))
            return false;
        var parts = path[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return false;
        string server = parts[0], share = parts[1];
        result = ("unc:\\\\" + server.ToUpperInvariant() + "\\" + share.ToUpperInvariant(), string.Join('\\', parts.Skip(2)),
            "\\\\" + server + "\\" + share);
        return true;
    }

    /// <summary>"{GUID}" from "\\?\Volume{GUID}\", upper case; null when it isn't that form.</summary>
    internal static string? GuidOf(string volumeName)
    {
        int open = volumeName.IndexOf('{'), close = volumeName.IndexOf('}');
        if (open < 0 || close <= open) return null;
        var g = volumeName.AsSpan(open, close - open + 1);
        return Guid.TryParse(g, out _) ? g.ToString().ToUpperInvariant() : null;
    }

    /// <summary>Forward slashes as backslashes, quotes and the \\?\ prefix gone, no trailing separator (drive roots as "C:").</summary>
    internal static string Clean(string path)
    {
        string p = path.Trim().Trim('"').Replace('/', '\\');
        if (p.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) p = @"\\" + p[8..];
        else if (p.StartsWith(@"\\?\", StringComparison.Ordinal) && p.Length >= 6 && p[5] == ':') p = p[4..];
        return p.TrimEnd('\\');
    }

    static (string, string, string) Fallback(string root, string below) => ("path:" + root.ToUpperInvariant(), below, root + "\\");

    static string Join(string a, string b) => a.Length == 0 ? b : b.Length == 0 ? a : a + "\\" + b;
}

/// <summary>One root as the follower knows it: its volume key resolved once, and its place in the scope.</summary>
internal sealed class RootInfo
{
    /// <summary>The root entry's name in the index ("C:", "D:\Data", "\\server\share").</summary>
    public required string Name { get; init; }
    /// <summary>The volume key ("vol:{GUID}", "unc:\\SERVER\SHARE", "path:X:").</summary>
    public required string Key { get; init; }
    /// <summary>The root's path below its volume ("" for a drive root).</summary>
    public required string Prefix { get; init; }
    /// <summary>How the volume is shown ("C:\", "\\server\share").</summary>
    public required string Display { get; init; }
    /// <summary>The volume's index in the store's volume table.</summary>
    public ushort Volume { get; set; }
    /// <summary>A network drive or share (the network lane reads it, at most 8 MB/s).</summary>
    public required bool Network { get; init; }
    /// <summary>The key state of the root folder itself (the volume key chained over the prefix).</summary>
    public ulong State { get; set; }
    /// <summary>The root folder's scope verdict.</summary>
    public FolderVerdict Verdict { get; set; }
    /// <summary>The share the throttle and back-off are kept for (the volume key).</summary>
    public string Share => Key;
}

/// <summary>
/// The roots the follower has seen, by name (stable across Compact and Clear), with their volume keys resolved once and
/// cached: VolumeKeys calls Win32 only here, on the follower thread, outside the index lock. Slots never move, so folder
/// memos can keep a slot number.
/// </summary>
internal sealed class RootTable
{
    readonly List<RootInfo> _list = [];
    readonly Dictionary<string, int> _byName = new(StringComparer.OrdinalIgnoreCase);
    RootSnapshot _snapshot = RootSnapshot.Empty;

    /// <summary>Roots known.</summary>
    public int Count => _list.Count;

    /// <summary>A root by slot.</summary>
    public RootInfo this[int slot] => _list[slot];

    /// <summary>Every root known.</summary>
    public IReadOnlyList<RootInfo> All => _list;

    /// <summary>The slot of a root by its name; -1 when not resolved yet.</summary>
    public int SlotOf(string name) => _byName.TryGetValue(name, out int slot) ? slot : -1;

    /// <summary>
    /// Resolves roots not known yet (Win32, outside the index lock): their volume keys, the store's volume indexes and the
    /// scope verdicts. Returns whether any was added.
    /// </summary>
    public bool Add(IEnumerable<string> names, Func<string, (string Key, string RelativePrefix, string DisplayRoot)> resolve,
        Func<string, string, ushort> volumeIndex, ContentScope scope)
    {
        bool added = false;
        foreach (var name in names)
        {
            if (_byName.ContainsKey(name)) continue;
            var (key, prefix, display) = resolve(name);
            var info = new RootInfo
            {
                Name = name,
                Key = key,
                Prefix = prefix,
                Display = display,
                Network = key.StartsWith("unc:", StringComparison.OrdinalIgnoreCase) || IsNetworkLetter(display),
            };
            info.Volume = volumeIndex(key, display);
            info.Verdict = scope.RootVerdict(key, prefix, display, out ulong state);
            info.State = state;
            _byName[name] = _list.Count;
            _list.Add(info);
            added = true;
        }
        if (added) Publish();
        return added;
    }

    /// <summary>A mapped drive letter ("Z:\") whose share couldn't be named: still read on the network lane.</summary>
    static bool IsNetworkLetter(string display)
    {
        if (display.Length < 2 || display[1] != ':' || !char.IsAsciiLetter(display[0])) return false;
        try { return Anything.Core.Indexing.Drives.KindOf(display[..2] + "\\") == DriveKind.Network; }
        catch (Exception e) when (e is not OutOfMemoryException) { return false; }
    }

    /// <summary>Judges every root again for a new scope (the keys don't change).</summary>
    public void Rescope(ContentScope scope)
    {
        foreach (var r in _list)
        {
            r.Verdict = scope.RootVerdict(r.Key, r.Prefix, r.Display, out ulong state);
            r.State = state;
        }
        Publish();
    }

    /// <summary>A new store (Rebuild): volume indexes are asked for again.</summary>
    public void Revolume(Func<string, string, ushort> volumeIndex)
    {
        foreach (var r in _list) r.Volume = volumeIndex(r.Key, r.Display);
        Publish();
    }

    /// <summary>An immutable copy for other threads (path → key for GetStatus and GetSnippet, never calling Win32).</summary>
    public RootSnapshot Snapshot => Volatile.Read(ref _snapshot);

    void Publish() => Volatile.Write(ref _snapshot, new RootSnapshot([.. _list.Select(r => (r.Name, r.Key, r.Prefix, r.Display, r.Volume))]));
}

/// <summary>The roots at one moment, longest name first, for turning a full path into a document key on any thread.</summary>
internal sealed class RootSnapshot
{
    readonly (string Name, string Key, string Prefix, string Display, ushort Volume)[] _roots;

    /// <summary>No roots.</summary>
    public static RootSnapshot Empty { get; } = new([]);

    /// <summary>A snapshot of these roots.</summary>
    public RootSnapshot((string Name, string Key, string Prefix, string Display, ushort Volume)[] roots)
        => _roots = [.. roots.OrderByDescending(r => r.Name.Length)];

    /// <summary>Roots in the snapshot.</summary>
    public int Count => _roots.Length;

    /// <summary>
    /// The volume key and volume-relative path of a full path through the root holding it (the longest root name that is a
    /// path prefix); false when no known root holds it.
    /// </summary>
    public bool TryResolve(string fullPath, out string volumeKey, out string relativePath, out ushort volume, out string display)
    {
        string path = VolumeKeys.Clean(fullPath);
        foreach (var r in _roots)
        {
            if (!path.StartsWith(r.Name, StringComparison.OrdinalIgnoreCase)) continue;
            if (path.Length != r.Name.Length && path[r.Name.Length] != '\\') continue;
            string below = path.Length > r.Name.Length ? path[(r.Name.Length + 1)..] : "";
            volumeKey = r.Key;
            relativePath = r.Prefix.Length == 0 ? below : below.Length == 0 ? r.Prefix : r.Prefix + "\\" + below;
            volume = r.Volume;
            display = r.Display;
            return true;
        }
        volumeKey = relativePath = display = "";
        volume = 0;
        return false;
    }

    /// <summary>The volume key and the path below the volume of a folder (scope building); null when no root holds it.</summary>
    public (string Key, string Prefix)? Resolve(string folder)
        => TryResolve(folder, out var key, out var rel, out _, out _) ? (key, rel) : null;

    /// <summary>Whether a known root lies below a folder ("E:" while "E:\Data" is a root): a listing of it can hold its files.</summary>
    public bool HoldsRootBelow(string folder)
    {
        string path = VolumeKeys.Clean(folder);
        foreach (var r in _roots)
            if (r.Name.Length > path.Length + 1 && r.Name[path.Length] == '\\' && r.Name.StartsWith(path, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The document key of a full path; false when no known root holds it.</summary>
    public bool TryKeyOf(string fullPath, out ulong key, out string volumeKey, out string relativePath, out string display)
    {
        key = 0;
        if (!TryResolve(fullPath, out volumeKey, out relativePath, out _, out display)) return false;
        key = ContentKeys.Of(volumeKey, relativePath);
        return true;
    }
}
