using System.IO.Enumeration;

namespace Anything.Core.Indexing;

/// <summary>
/// Compiled exclude rules (Options → Exclude). Folder entries containing a backslash match full paths
/// (prefix match, or wildcard match when they contain * or ?); entries without a backslash match folder names.
/// Name patterns (* and ?) apply to file and folder names. All matching is case-insensitive.
/// </summary>
public sealed class ExclusionFilter
{
    readonly string[] _folderPrefixes;
    readonly string[] _folderPathWildcards;
    readonly string[] _folderNameWildcards;
    readonly string[] _namePatterns;
    readonly FileAttributes _attrMask;

    public static ExclusionFilter None { get; } = new(null);

    public ExclusionFilter(IndexSettings? settings)
    {
        var prefixes = new List<string>();
        var pathWild = new List<string>();
        var nameWild = new List<string>();
        foreach (var raw in settings?.ExcludeFolders ?? [])
        {
            var f = raw?.Trim().Trim('"');
            if (string.IsNullOrEmpty(f)) continue;
            bool wild = f.IndexOfAny(['*', '?']) >= 0;
            if (f.Contains('\\') || f.Contains('/'))
            {
                f = f.Replace('/', '\\').TrimEnd('\\');
                if (wild) pathWild.Add(f);
                else prefixes.Add(f);
            }
            else nameWild.Add(f);
        }
        _folderPrefixes = [.. prefixes];
        _folderPathWildcards = [.. pathWild];
        _folderNameWildcards = [.. nameWild];
        _namePatterns = [.. (settings?.ExcludePatterns ?? []).Select(p => p?.Trim() ?? "").Where(p => p.Length > 0)];
        if (settings?.ExcludeHidden == true) _attrMask |= FileAttributes.Hidden;
        if (settings?.ExcludeSystem == true) _attrMask |= FileAttributes.System;
        NeedsFolderPaths = _folderPrefixes.Length + _folderPathWildcards.Length > 0;
        IsEmpty = !NeedsFolderPaths && _folderNameWildcards.Length == 0 && _namePatterns.Length == 0 && _attrMask == 0;
    }

    public bool IsEmpty { get; }

    /// <summary>True when folder exclusion needs full paths (callers may skip building paths otherwise).</summary>
    public bool NeedsFolderPaths { get; }

    public bool ExcludesAttributes(FileAttributes attributes) => (attributes & _attrMask) != 0;

    public bool ExcludesName(string name)
    {
        foreach (var p in _namePatterns)
            if (FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true)) return true;
        return false;
    }

    /// <summary>Folder rules only (path prefix / path wildcard / folder-name wildcard). <paramref name="fullPath"/> may be null if <see cref="NeedsFolderPaths"/> is false.</summary>
    public bool ExcludesFolder(string? fullPath, string name)
    {
        foreach (var p in _folderNameWildcards)
            if (FileSystemName.MatchesSimpleExpression(p, name, ignoreCase: true)) return true;
        if (fullPath is null) return false;
        var path = fullPath.AsSpan().TrimEnd('\\');
        foreach (var p in _folderPrefixes)
        {
            if (path.Length < p.Length || !path.StartsWith(p, StringComparison.OrdinalIgnoreCase)) continue;
            if (path.Length == p.Length || path[p.Length] == '\\') return true;
        }
        foreach (var p in _folderPathWildcards)
            if (FileSystemName.MatchesSimpleExpression(p, path, ignoreCase: true)) return true;
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
