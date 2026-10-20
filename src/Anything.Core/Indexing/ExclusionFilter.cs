namespace Anything.Core.Indexing;

/// <summary>
/// Compiled exclude rules (Options → Exclude). Folder entries containing a backslash match full paths
/// (prefix match, or wildcard match when they contain * or ?, where * also spans backslashes); entries without a
/// backslash match folder names. Name patterns (* and ?) apply to file and folder names. All matching is case-insensitive.
/// </summary>
public sealed class ExclusionFilter
{
    readonly string[] _folderPrefixes;
    readonly Wildcard[] _folderPathWildcards;
    readonly Wildcard[] _folderNameWildcards;
    readonly Wildcard[] _namePatterns;
    readonly FileAttributes _attrMask;

    public static ExclusionFilter None { get; } = new(null);

    public ExclusionFilter(IndexSettings? settings)
    {
        var prefixes = new List<string>();
        var pathWild = new List<Wildcard>();
        var nameWild = new List<Wildcard>();
        foreach (var raw in settings?.ExcludeFolders ?? [])
        {
            var f = raw?.Trim().Trim('"');
            if (string.IsNullOrEmpty(f)) continue;
            bool wild = f.IndexOfAny(['*', '?']) >= 0;
            if (f.Contains('\\') || f.Contains('/'))
            {
                f = f.Replace('/', '\\').TrimEnd('\\');
                if (wild) pathWild.Add(new Wildcard(f));
                else prefixes.Add(f);
            }
            else nameWild.Add(new Wildcard(f));
        }
        _folderPrefixes = [.. prefixes];
        _folderPathWildcards = [.. pathWild];
        _folderNameWildcards = [.. nameWild];
        _namePatterns = [.. (settings?.ExcludePatterns ?? []).Select(p => p?.Trim() ?? "").Where(p => p.Length > 0).Select(p => new Wildcard(p))];
        if (settings?.ExcludeHidden == true) _attrMask |= FileAttributes.Hidden;
        if (settings?.ExcludeSystem == true) _attrMask |= FileAttributes.System;
        NeedsFolderPaths = _folderPrefixes.Length + _folderPathWildcards.Length > 0;
        IsEmpty = !NeedsFolderPaths && _folderNameWildcards.Length == 0 && _namePatterns.Length == 0 && _attrMask == 0;
    }

    ExclusionFilter(ExclusionFilter from, string[] prefixes, Wildcard[] pathWildcards)
    {
        _folderPrefixes = prefixes;
        _folderPathWildcards = pathWildcards;
        _folderNameWildcards = from._folderNameWildcards;
        _namePatterns = from._namePatterns;
        _attrMask = from._attrMask;
        NeedsFolderPaths = _folderPrefixes.Length + _folderPathWildcards.Length > 0;
        IsEmpty = !NeedsFolderPaths && _folderNameWildcards.Length == 0 && _namePatterns.Length == 0 && _attrMask == 0;
    }

    /// <summary>
    /// This filter for a folder the user asked to search by name (Settings › Folders › Also search these folders):
    /// folder rules that leave out <paramref name="rootPath"/> itself or a folder above it are dropped, since they
    /// would hide all of it; every rule about what is inside it still applies.
    /// </summary>
    public ExclusionFilter WithoutAncestorsOf(string rootPath)
    {
        string root = rootPath.Replace('/', '\\').TrimEnd('\\');
        if (root.Length == 0 || !NeedsFolderPaths) return this;
        var self = new List<string> { root };
        for (int i = root.LastIndexOf('\\'); i > 0; i = root.LastIndexOf('\\', i - 1)) self.Add(root[..i]);
        var prefixes = _folderPrefixes.Where(p => !(root.Equals(p, StringComparison.OrdinalIgnoreCase)
            || (root.Length > p.Length && root.StartsWith(p, StringComparison.OrdinalIgnoreCase) && root[p.Length] == '\\'))).ToArray();
        var wild = _folderPathWildcards.Where(w => !self.Exists(a => w.IsMatch(a))).ToArray();
        return prefixes.Length == _folderPrefixes.Length && wild.Length == _folderPathWildcards.Length ? this : new ExclusionFilter(this, prefixes, wild);
    }

    public bool IsEmpty { get; }

    /// <summary>
    /// A fingerprint of these rules (never 0), stable across runs: the database keeps, per root, the stamp of the rules
    /// its entries were built with, so entries from before an exclusion change are rescanned even after a restart.
    /// </summary>
    public ulong Stamp
    {
        get
        {
            ulong h = 14695981039346656037; // FNV-1a
            void Add(string s)
            {
                foreach (char c in s.ToUpperInvariant()) h = (h ^ c) * 1099511628211;
                h = (h ^ 0xFFFF) * 1099511628211; // separator, so "ab"+"c" differs from "a"+"bc"
            }
            foreach (var p in _folderPrefixes.Order(StringComparer.OrdinalIgnoreCase)) Add("p" + p);
            foreach (var w in _folderPathWildcards.Select(w => w.Pattern).Order(StringComparer.OrdinalIgnoreCase)) Add("w" + w);
            foreach (var w in _folderNameWildcards.Select(w => w.Pattern).Order(StringComparer.OrdinalIgnoreCase)) Add("n" + w);
            foreach (var w in _namePatterns.Select(w => w.Pattern).Order(StringComparer.OrdinalIgnoreCase)) Add("f" + w);
            Add("a" + (int)_attrMask);
            return h == 0 ? 1 : h;
        }
    }

    /// <summary>True when folder exclusion needs full paths (callers may skip building paths otherwise).</summary>
    public bool NeedsFolderPaths { get; }

    /// <summary>True when hidden or system files and folders are left out (so whether a folder is indexed depends on its attributes).</summary>
    public bool HasAttributeRules => _attrMask != 0;

    public bool ExcludesAttributes(FileAttributes attributes) => (attributes & _attrMask) != 0;

    public bool ExcludesName(string name)
    {
        foreach (var p in _namePatterns)
            if (p.IsMatch(name)) return true;
        return false;
    }

    /// <summary>Folder rules only (path prefix / path wildcard / folder-name wildcard). <paramref name="fullPath"/> may be null if <see cref="NeedsFolderPaths"/> is false.</summary>
    public bool ExcludesFolder(string? fullPath, string name)
    {
        foreach (var p in _folderNameWildcards)
            if (p.IsMatch(name)) return true;
        if (fullPath is null) return false;
        var path = fullPath.AsSpan().TrimEnd('\\');
        foreach (var p in _folderPrefixes)
        {
            if (path.Length < p.Length || !path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) continue;
            if (path.Length == p.Length || path[p.Length] == '\\') return true;
        }
        foreach (var p in _folderPathWildcards)
            if (p.IsMatch(path)) return true;
        return false;
    }

    /// <summary>Full check for one entry. <paramref name="fullPath"/> is only consulted for folders.</summary>
    public bool Excludes(string name, FileAttributes attributes, string? fullPath)
    {
        if (IsEmpty) return false;
        if (ExcludesAttributes(attributes) || ExcludesName(name)) return true;
        return (attributes & FileAttributes.Directory) != 0 && ExcludesFolder(fullPath, name);
    }

    /// <summary>True if <paramref name="fullPath"/> or any of its ancestors (below <paramref name="rootPath"/>) is an excluded folder.</summary>
    public bool ExcludesPathOrAncestor(string rootPath, string fullPath)
    {
        if (IsEmpty) return false;
        string root = rootPath.TrimEnd('\\');
        var rel = fullPath.Length > root.Length ? fullPath[(root.Length + 1)..] : "";
        string cur = root;
        foreach (var part in rel.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            cur = cur + "\\" + part;
            if (ExcludesName(part) || ExcludesFolder(cur, part)) return true;
        }
        return false;
    }

    public static string Join(string parentPath, string name)
        => parentPath.EndsWith('\\') ? parentPath + name : parentPath + "\\" + name;
}
