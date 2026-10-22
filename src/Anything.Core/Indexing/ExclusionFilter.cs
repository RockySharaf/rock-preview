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
    // Each rule as written in the settings, at the same index as its compiled form: what RuleHiding names.
    readonly string[] _folderPrefixRules;
    readonly string[] _folderPathWildcardRules;
    readonly string[] _folderNameWildcardRules;
    readonly string[] _namePatternRules;
    readonly FileAttributes _attrMask;

    public static ExclusionFilter None { get; } = new(null);

    public ExclusionFilter(IndexSettings? settings)
    {
        var prefixes = new List<string>();
        var pathWild = new List<Wildcard>();
        var nameWild = new List<Wildcard>();
        var prefixRules = new List<string>();
        var pathWildRules = new List<string>();
        var nameWildRules = new List<string>();
        foreach (var raw in settings?.ExcludeFolders ?? [])
        {
            var f = raw?.Trim().Trim('"');
            if (string.IsNullOrEmpty(f)) continue;
            bool wild = f.IndexOfAny(['*', '?']) >= 0;
            if (IsPathRule(f))
            {
                f = f.Replace('/', '\\').TrimEnd('\\');
                if (wild) { pathWild.Add(new Wildcard(f)); pathWildRules.Add(raw!); }
                else { prefixes.Add(f); prefixRules.Add(raw!); }
            }
            else { nameWild.Add(new Wildcard(f)); nameWildRules.Add(raw!); }
        }
        _folderPrefixes = [.. prefixes];
        _folderPathWildcards = [.. pathWild];
        _folderNameWildcards = [.. nameWild];
        _folderPrefixRules = [.. prefixRules];
        _folderPathWildcardRules = [.. pathWildRules];
        _folderNameWildcardRules = [.. nameWildRules];
        _namePatternRules = [.. (settings?.ExcludePatterns ?? []).Where(p => !string.IsNullOrWhiteSpace(p))];
        _namePatterns = [.. _namePatternRules.Select(p => new Wildcard(p.Trim()))];
        if (settings?.ExcludeHidden == true) _attrMask |= FileAttributes.Hidden;
        if (settings?.ExcludeSystem == true) _attrMask |= FileAttributes.System;
        NeedsFolderPaths = _folderPrefixes.Length + _folderPathWildcards.Length > 0;
        IsEmpty = !NeedsFolderPaths && _folderNameWildcards.Length == 0 && _namePatterns.Length == 0 && _attrMask == 0;
    }

    /// <summary>The rules of <paramref name="from"/>, keeping only the path rules at the given indexes.</summary>
    ExclusionFilter(ExclusionFilter from, int[] prefixes, int[] pathWildcards)
    {
        _folderPrefixes = [.. prefixes.Select(i => from._folderPrefixes[i])];
        _folderPrefixRules = [.. prefixes.Select(i => from._folderPrefixRules[i])];
        _folderPathWildcards = [.. pathWildcards.Select(i => from._folderPathWildcards[i])];
        _folderPathWildcardRules = [.. pathWildcards.Select(i => from._folderPathWildcardRules[i])];
        _folderNameWildcards = from._folderNameWildcards;
        _folderNameWildcardRules = from._folderNameWildcardRules;
        _namePatterns = from._namePatterns;
        _namePatternRules = from._namePatternRules;
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
        bool AtOrAbove(string p) => root.Equals(p, StringComparison.OrdinalIgnoreCase)
            || (root.Length > p.Length && root.StartsWith(p, StringComparison.OrdinalIgnoreCase) && root[p.Length] == '\\');
        var prefixes = Enumerable.Range(0, _folderPrefixes.Length).Where(i => !AtOrAbove(_folderPrefixes[i])).ToArray();
        var wild = Enumerable.Range(0, _folderPathWildcards.Length).Where(i => !self.Exists(a => _folderPathWildcards[i].IsMatch(a))).ToArray();
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

    public bool ExcludesName(string name) => NameRule(name) != null;

    /// <summary>The name pattern that leaves out <paramref name="name"/>, as written, or null.</summary>
    string? NameRule(string name)
    {
        for (int i = 0; i < _namePatterns.Length; i++)
            if (_namePatterns[i].IsMatch(name)) return _namePatternRules[i];
        return null;
    }

    /// <summary>Folder rules only (path prefix / path wildcard / folder-name wildcard). <paramref name="fullPath"/> may be null if <see cref="NeedsFolderPaths"/> is false.</summary>
    public bool ExcludesFolder(string? fullPath, string name) => FolderRule(fullPath, name) != null;

    /// <summary>The folder rule that leaves out this folder, as written, or null.</summary>
    string? FolderRule(string? fullPath, string name)
    {
        for (int i = 0; i < _folderNameWildcards.Length; i++)
            if (_folderNameWildcards[i].IsMatch(name)) return _folderNameWildcardRules[i];
        if (fullPath is null) return null;
        var path = fullPath.AsSpan().TrimEnd('\\');
        for (int i = 0; i < _folderPrefixes.Length; i++)
        {
            var p = _folderPrefixes[i];
            if (path.Length < p.Length || !path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) continue;
            if (path.Length == p.Length || path[p.Length] == '\\') return _folderPrefixRules[i];
        }
        for (int i = 0; i < _folderPathWildcards.Length; i++)
            if (_folderPathWildcards[i].IsMatch(path)) return _folderPathWildcardRules[i];
        return null;
    }

    /// <summary>Full check for one entry. <paramref name="fullPath"/> is only consulted for folders.</summary>
    public bool Excludes(string name, FileAttributes attributes, string? fullPath)
    {
        if (IsEmpty) return false;
        if (ExcludesAttributes(attributes) || ExcludesName(name)) return true;
        return (attributes & FileAttributes.Directory) != 0 && ExcludesFolder(fullPath, name);
    }

    /// <summary>True if <paramref name="fullPath"/> or any of its ancestors (below <paramref name="rootPath"/>) is an excluded folder.</summary>
    public bool ExcludesPathOrAncestor(string rootPath, string fullPath) => RuleHiding(rootPath, fullPath) != null;

    /// <summary>
    /// The rule that leaves out <paramref name="fullPath"/> or a folder between <paramref name="rootPath"/> and it, as
    /// written in the settings: a path, a path wildcard, a folder name or a name pattern. Null when none does. Leaving
    /// out hidden or system folders is not a rule here: that depends on each folder's attributes.
    /// </summary>
    public string? RuleHiding(string rootPath, string fullPath)
    {
        if (IsEmpty) return null;
        string root = rootPath.TrimEnd('\\');
        var rel = fullPath.Length > root.Length ? fullPath[(root.Length + 1)..] : "";
        string cur = root;
        foreach (var part in rel.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            cur = cur + "\\" + part;
            if ((NameRule(part) ?? FolderRule(cur, part)) is { } rule) return rule;
        }
        return null;
    }

    /// <summary>An exclude entry with a backslash (or a slash) is a path; one without is a folder name.</summary>
    internal static bool IsPathRule(string rule) => rule.Contains('\\') || rule.Contains('/');

    public static string Join(string parentPath, string name)
        => parentPath.EndsWith('\\') ? parentPath + name : parentPath + "\\" + name;
}
