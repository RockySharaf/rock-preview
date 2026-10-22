using Anything.Core.Indexing;

namespace Anything.Core.Content.Indexing;

/// <summary>Where a folder stands: outside the scope for good, above an include folder (a descendant may be in), or inside.</summary>
internal enum FolderVerdict : byte { Out, Pending, In }

/// <summary>
/// Which files are read (§3 rules, in order): under My folders or an added folder; not under an excluded folder or a
/// built-in denied folder (AppData, .ssh, .git, node_modules, %TEMP%, the app's own data; the deny-list always wins, even
/// over the user's own folders); a kind switched on by the mode and Settings; a name not on the secrets deny-list; not
/// Temporary. Folders are judged by key state, chained down from the volume like document keys, so no path strings are
/// built (except for wildcard path exclusions, which need them). Immutable: a settings change builds a new scope.
/// </summary>
internal sealed class ContentScope
{
    readonly HashSet<ulong> _include = [];
    readonly HashSet<ulong> _ancestors = [];
    readonly HashSet<ulong> _blocked = [];
    readonly ExclusionFilter? _nameRules;
    readonly ExclusionFilter? _pathRules;
    readonly HashSet<string>? _extras;

    ContentScope(ContentSettings settings, bool allRoots)
    {
        Mode = settings.Mode;
        Kinds = settings.Kinds;
        AllRoots = allRoots;
        var extras = ContentTypes.ExtraSet(settings.ExtraTextExtensions);
        _extras = extras.Count > 0 ? extras : null;
        var names = new List<string>();
        var paths = new List<string>();
        foreach (var raw in settings.ExcludeFolders)
        {
            string f = raw?.Trim().Trim('"') ?? "";
            if (f.Length == 0) continue;
            bool path = f.Contains('\\') || f.Contains('/');
            bool wild = f.IndexOfAny(['*', '?']) >= 0;
            if (!path) names.Add(f);
            else if (wild) paths.Add(f);
        }
        if (names.Count > 0) _nameRules = new ExclusionFilter(new IndexSettings { ExcludeFolders = names });
        if (paths.Count > 0) _pathRules = new ExclusionFilter(new IndexSettings { ExcludeFolders = paths });
    }

    /// <summary>The mode the scope was built for (pictures only in DocumentsAndPictures).</summary>
    public ContentMode Mode { get; }
    /// <summary>Kinds of document switched on.</summary>
    public ContentKinds Kinds { get; }
    /// <summary>Every root is an include folder (the demo); the deny-list still applies.</summary>
    public bool AllRoots { get; }
    /// <summary>Wildcard path exclusions need the folder's full path (the walker builds it only then).</summary>
    public bool NeedsFolderPaths => _pathRules != null;
    /// <summary>Changes whenever the set of files in scope can change (a reconcile then checks stored documents' paths).</summary>
    public ulong Fingerprint { get; private set; }

    /// <summary>
    /// The scope for these settings. <paramref name="resolve"/> turns a folder path into its volume key and the path below
    /// the volume (the follower resolves through its cached roots; tests pass their own). Include folders that can't be
    /// resolved are left out; so are exclusions.
    /// </summary>
    public static ContentScope Build(ContentSettings settings, bool allRootsInScope, IEnumerable<string> extraDeniedPaths,
        Func<string, (string Key, string Prefix)?> resolve)
    {
        var scope = new ContentScope(settings, allRootsInScope);
        ulong fp = 14695981039346656037UL;
        void Mix(ulong v) { fp = (fp ^ v) * 1099511628211UL; fp = (fp ^ (v >> 32)) * 1099511628211UL; }
        Mix((ulong)settings.Mode);
        Mix((ulong)settings.Kinds);
        Mix(allRootsInScope ? 1UL : 0UL);
        if (!allRootsInScope)
            foreach (var folder in settings.EffectiveFolders())
            {
                if (resolve(folder) is not { } r) continue;
                ulong state = ContentKeys.Start(r.Key);
                var parts = r.Prefix.Split('\\', StringSplitOptions.RemoveEmptyEntries);
                foreach (var part in parts)
                {
                    scope._ancestors.Add(state);
                    state = ContentKeys.Continue(state, part);
                }
                scope._include.Add(state);
            }
        foreach (var path in settings.ExcludeFolders.Concat(ContentTypes.DeniedFolderPaths()).Concat(extraDeniedPaths))
        {
            string f = path?.Trim().Trim('"') ?? "";
            if (f.Length == 0 || f.IndexOfAny(['*', '?']) >= 0 || !(f.Contains('\\') || f.Contains('/'))) continue;
            if (ContentSettings.Normalize(f) is not { } n || resolve(n) is not { } r) continue;
            scope._blocked.Add(ContentKeys.Of(r.Key, r.Prefix));
        }
        foreach (var s in scope._include.Order()) Mix(s);
        Mix(0xA5A5);
        foreach (var s in scope._blocked.Order()) Mix(s);
        foreach (var e in (scope._extras ?? []).Order(StringComparer.OrdinalIgnoreCase)) Mix(ContentKeys.NameHash(e));
        foreach (var e in settings.ExcludeFolders.Where(x => !string.IsNullOrWhiteSpace(x)).Order(StringComparer.OrdinalIgnoreCase))
            Mix(ContentKeys.NameHash(e.Trim()));
        scope.Fingerprint = fp;
        return scope;
    }

    /// <summary>The verdict of a volume's own root (state = ContentKeys.Start of its key).</summary>
    public FolderVerdict VolumeStart(ulong state)
    {
        if (_blocked.Contains(state)) return FolderVerdict.Out;
        if (AllRoots || _include.Contains(state)) return FolderVerdict.In;
        return _ancestors.Contains(state) ? FolderVerdict.Pending : FolderVerdict.Out;
    }

    /// <summary>
    /// The verdict of a folder from its parent's, its own key state and name. <paramref name="fullPath"/> is asked for
    /// only while wildcard path exclusions exist.
    /// </summary>
    public FolderVerdict Child(FolderVerdict parent, ulong state, ReadOnlySpan<char> name, Func<string>? fullPath = null)
    {
        if (parent == FolderVerdict.Out) return FolderVerdict.Out;
        if (ContentTypes.IsDeniedFolder(name) || _blocked.Contains(state)) return FolderVerdict.Out;
        if (_nameRules != null && _nameRules.ExcludesFolder(null, name.ToString())) return FolderVerdict.Out;
        if (_pathRules != null && fullPath?.Invoke() is { } p && _pathRules.ExcludesFolder(p, name.ToString())) return FolderVerdict.Out;
        if (parent == FolderVerdict.In) return FolderVerdict.In;
        if (_include.Contains(state)) return FolderVerdict.In;
        return _ancestors.Contains(state) ? FolderVerdict.Pending : FolderVerdict.Out;
    }

    /// <summary>The verdict and key state of a root below its volume (the prefix's folders judged one by one).</summary>
    public FolderVerdict RootVerdict(string volumeKey, string prefix, string displayRoot, out ulong state)
    {
        state = ContentKeys.Start(volumeKey);
        var verdict = VolumeStart(state);
        string path = displayRoot.TrimEnd('\\');
        foreach (var part in prefix.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            state = ContentKeys.Continue(state, part);
            path = path + "\\" + part;
            string p = path;
            verdict = Child(verdict, state, part, () => p);
        }
        return verdict;
    }

    /// <summary>The kind a file is read as, or None: folders and Temporary files never; then the deny-list, kinds and mode.</summary>
    public ContentKind FileKind(ReadOnlySpan<char> name, FileAttributes attributes)
    {
        if ((attributes & (FileAttributes.Directory | FileAttributes.Temporary)) != 0) return ContentKind.None;
        return ContentTypes.CandidateKind(name, Kinds, Mode, _extras);
    }

    /// <summary>
    /// Whether a stored document is in scope now, from its volume and volume-relative path alone (offline volumes too);
    /// <paramref name="displayRoot"/> rebuilds a full path for wildcard exclusions.
    /// </summary>
    public bool InScope(string volumeKey, ReadOnlySpan<char> relativePath, string? displayRoot, out ContentKind kind)
    {
        kind = ContentKind.None;
        ulong state = ContentKeys.Start(volumeKey);
        var verdict = VolumeStart(state);
        string? path = NeedsFolderPaths ? (displayRoot ?? "").TrimEnd('\\') : null;
        var rest = relativePath;
        while (!rest.IsEmpty)
        {
            int cut = rest.IndexOfAny('\\', '/');
            if (cut < 0)
            {
                if (verdict != FolderVerdict.In) return false;
                kind = FileKind(rest, FileAttributes.Normal);
                return kind != ContentKind.None;
            }
            var part = rest[..cut];
            rest = rest[(cut + 1)..];
            if (part.IsEmpty) continue;
            state = ContentKeys.Continue(state, part);
            if (path != null) path = path + "\\" + part.ToString();
            string? p = path;
            verdict = Child(verdict, state, part, p == null ? null : () => p);
            if (verdict == FolderVerdict.Out) return false;
        }
        return false;
    }
}

/// <summary>A folder's place: its key state, its root's slot and its verdict.</summary>
internal readonly record struct FolderState(ulong State, int Slot, FolderVerdict Verdict);

/// <summary>
/// Judges FileIndex folders by walking parents (guard 4,096) and chaining key states down, memoised per folder: folders
/// inside the scope or above an include folder keep their key state in a dictionary; folders out for good are one bit
/// each. Never builds a path (except for wildcard exclusions) and never calls Win32: an unknown root is reported in
/// <see cref="UnknownRoots"/> for the follower to resolve outside the lock (its folders count as out until then, so a
/// caller that sees UnknownRoots must resolve them, reset the walker and look again before acting on an "out"). Call
/// under the index's read lock; reset it when the generation, the folder paths (PathVersion) or the scope change.
/// </summary>
internal sealed class FolderWalker
{
    readonly Dictionary<int, FolderState> _known = [];
    readonly List<int> _chain = new(64);
    ulong[] _out = new ulong[1024];
    FileIndex _index = null!;
    ContentScope _scope = null!;
    RootTable _roots = null!;

    /// <summary>Root names met that the root table doesn't hold yet.</summary>
    public HashSet<string> UnknownRoots { get; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folders remembered as inside or above the scope (memory check).</summary>
    public int Remembered => _known.Count;

    /// <summary>Forgets every verdict (the pooled bitset is kept).</summary>
    public void Reset(FileIndex index, ContentScope scope, RootTable roots)
    {
        _index = index;
        _scope = scope;
        _roots = roots;
        _known.Clear();
        Array.Clear(_out);
        UnknownRoots.Clear();
    }

    /// <summary>The state of a folder; false when it is out of scope for good or its root isn't resolved yet.</summary>
    public bool TryResolve(int folderId, out FolderState folder)
    {
        folder = default;
        if (folderId < 0) return false;
        if (IsOut(folderId)) return false;
        if (_known.TryGetValue(folderId, out folder)) return true;
        _chain.Clear();
        int cur = folderId;
        FolderState top;
        while (true)
        {
            if (IsOut(cur)) { MarkOut(); return false; }
            if (_known.TryGetValue(cur, out top)) break;
            int parent = _index.Parent(cur);
            if (parent == FileIndex.NoParent)
            {
                string name = _index.Name(cur);
                int slot = _roots.SlotOf(name);
                if (slot < 0)
                {
                    // Not resolved yet: remembered as out until the follower resolves it and resets the walker, so the
                    // rest of the pass doesn't walk up to it again for every file.
                    if (name.Length > 0) UnknownRoots.Add(name);
                    SetOut(cur);
                    MarkOut();
                    return false;
                }
                var root = _roots[slot];
                top = new FolderState(root.State, slot, root.Verdict);
                if (root.Verdict == FolderVerdict.Out) { SetOut(cur); MarkOut(); return false; }
                _known[cur] = top;
                break;
            }
            _chain.Add(cur);
            if (_chain.Count > 4096 || (uint)parent >= (uint)_index.Count) { MarkOut(); return false; }
            cur = parent;
        }
        for (int i = _chain.Count - 1; i >= 0; i--)
        {
            int f = _chain[i];
            string name = _index.Name(f);
            ulong state = ContentKeys.Continue(top.State, name);
            var verdict = _scope.Child(top.Verdict, state, name, _scope.NeedsFolderPaths ? () => PathOf(f) : null);
            if (verdict == FolderVerdict.Out)
            {
                for (int j = i; j >= 0; j--) SetOut(_chain[j]);
                return false;
            }
            top = new FolderState(state, top.Slot, verdict);
            _known[f] = top;
        }
        folder = top;
        return true;
    }

    /// <summary>The key of a file whose folder is in scope and whose kind is read; false otherwise.</summary>
    public bool TryKeyOf(int fileId, out ulong key, out FolderState folder, out ContentKind kind)
    {
        key = 0;
        kind = ContentKind.None;
        if (!_index.IsAlive(fileId) || _index.IsDirectory(fileId)) { folder = default; return false; }
        if (!TryResolve(_index.Parent(fileId), out folder) || folder.Verdict != FolderVerdict.In) return false;
        string name = _index.Name(fileId);
        kind = _scope.FileKind(name, _index.Attributes(fileId));
        if (kind == ContentKind.None) return false;
        key = ContentKeys.Continue(folder.State, name);
        return true;
    }

    /// <summary>A file's path below its root ("Users\Me\a.pdf"); empty for a root. Guard 4,096 levels.</summary>
    public string RelativeBelowRoot(int fileId)
    {
        var parts = new List<string>(16);
        for (int cur = fileId, guard = 0; cur != FileIndex.NoParent && guard < 4096; guard++)
        {
            int parent = _index.Parent(cur);
            if (parent == FileIndex.NoParent) break;
            parts.Add(_index.Name(cur));
            cur = parent;
        }
        parts.Reverse();
        return string.Join('\\', parts);
    }

    string PathOf(int folderId)
    {
        var parts = new List<string>(16);
        for (int cur = folderId, guard = 0; cur != FileIndex.NoParent && guard < 4096; cur = _index.Parent(cur), guard++)
            parts.Add(_index.Name(cur));
        parts.Reverse();
        return string.Join('\\', parts);
    }

    void MarkOut()
    {
        foreach (int f in _chain) SetOut(f);
    }

    bool IsOut(int id) => (uint)id < (uint)(_out.Length << 6) && (_out[id >> 6] & (1UL << id)) != 0;

    void SetOut(int id)
    {
        if (id < 0) return;
        int need = (id >> 6) + 1;
        if (need > _out.Length) Array.Resize(ref _out, Math.Max(need, _out.Length * 2));
        _out[id >> 6] |= 1UL << id;
    }
}
