using System.Globalization;
using System.Text;

namespace Anything.Core;

/// <summary>
/// Run history, as Everything keeps it: how many times each file or folder was opened from the app, and when last
/// (Run Count and Date Run, runcount: and dr:). Kept by full path, so a file deleted and made again keeps its count,
/// and saved as "Run History.csv" in Everything's own format, so Everything's file can be imported as is.
/// Thread-safe.
/// </summary>
public sealed class RunHistory
{
    /// <summary>A path's run count and when it was last run (FILETIME UTC).</summary>
    public readonly record struct Entry(int Count, long LastRun);

    const string Header = "Filename,Run Count,Last Run Date";

    readonly object _gate = new();
    readonly Dictionary<string, Entry> _byPath = new(StringComparer.OrdinalIgnoreCase);
    long _changes, _savedChanges;

    /// <summary>Raised (outside the lock) after a count changes.</summary>
    public event Action? Changed;

    public int Count { get { lock (_gate) return _byPath.Count; } }

    /// <summary>Changed since it was loaded or last saved.</summary>
    public bool IsDirty { get { lock (_gate) return _changes != _savedChanges; } }

    /// <summary>The entry for <paramref name="path"/> (Count 0 when it was never run).</summary>
    public Entry Get(string path)
    {
        lock (_gate) return _byPath.TryGetValue(Normalize(path), out var e) ? e : default;
    }

    /// <summary>Counts one more run of <paramref name="path"/> at <paramref name="nowFileTimeUtc"/>. Returns the new count.</summary>
    public int Increment(string path, long nowFileTimeUtc)
    {
        int count;
        lock (_gate)
        {
            path = Normalize(path);
            count = _byPath.TryGetValue(path, out var e) ? e.Count + 1 : 1;
            Put(path, new Entry(count, nowFileTimeUtc));
        }
        Changed?.Invoke();
        return count;
    }

    /// <summary>Sets the run count of <paramref name="path"/> (Everything's Set Run Count); 0 forgets it.</summary>
    public void Set(string path, int count, long nowFileTimeUtc)
    {
        lock (_gate)
        {
            path = Normalize(path);
            if (count <= 0)
            {
                if (!_byPath.Remove(path)) return;
                Dirty(path);
            }
            else Put(path, new Entry(count, nowFileTimeUtc));
        }
        Changed?.Invoke();
    }

    public void Clear()
    {
        lock (_gate)
        {
            if (_byPath.Count == 0) return;
            _byPath.Clear();
            _changes++;
            _resolveAll = true;
        }
        Changed?.Invoke();
    }

    /// <summary>Every entry, most runs first.</summary>
    public List<(string Path, Entry Entry)> Entries()
    {
        lock (_gate)
            return [.. _byPath.Select(p => (p.Key, p.Value)).OrderByDescending(p => p.Value.Count).ThenByDescending(p => p.Value.LastRun)];
    }

    void Put(string path, Entry e)
    {
        _byPath[path] = e;
        Dirty(path);
    }

    void Dirty(string path)
    {
        _changes++;
        _dirtyPaths.Add(path);
    }

    /// <summary>Full paths the way the index writes them: "C:\" for a drive, no trailing backslash otherwise.</summary>
    public static string Normalize(string path)
    {
        path = path.Trim().Replace('/', '\\');
        if (path.Length == 2 && path[1] == ':') return path + "\\";
        return path.Length > 3 ? path.TrimEnd('\\') : path;
    }

    // ---- Run History.csv --------------------------------------------------------------------------

    /// <summary>Replaces the history with the file's (missing or unreadable: empty). Everything's file reads too.</summary>
    public void Load(string file)
    {
        var read = Read(file);
        lock (_gate)
        {
            _byPath.Clear();
            foreach (var (path, e) in read) _byPath[path] = e;
            _changes++;
            _savedChanges = _changes;
            _resolveAll = true;
        }
        Changed?.Invoke();
    }

    /// <summary>
    /// Adds another history (Everything's "Run History.csv", say): a path in both keeps the higher count and the later
    /// date. Returns how many paths were added or changed.
    /// </summary>
    public int Import(string file)
    {
        var read = Read(file);
        int changed = 0;
        lock (_gate)
        {
            foreach (var (path, e) in read)
            {
                var had = _byPath.TryGetValue(path, out var h) ? h : default;
                var merged = new Entry(Math.Max(had.Count, e.Count), Math.Max(had.LastRun, e.LastRun));
                if (merged == had) continue;
                Put(path, merged);
                changed++;
            }
        }
        if (changed > 0) Changed?.Invoke();
        return changed;
    }

    /// <summary>Writes the history (to a temporary file moved into place).</summary>
    public void Save(string file)
    {
        List<(string Path, Entry Entry)> entries;
        long changes;
        lock (_gate)
        {
            entries = [.. _byPath.Select(p => (p.Key, p.Value))];
            changes = _changes;
        }
        var sb = new StringBuilder(64 + entries.Count * 80);
        sb.Append(Header).Append("\r\n");
        foreach (var (path, e) in entries.OrderByDescending(x => x.Entry.Count).ThenBy(x => x.Path, StringComparer.OrdinalIgnoreCase))
            sb.Append('"').Append(path.Replace("\"", "\"\"")).Append("\",")
              .Append(e.Count.ToString(CultureInfo.InvariantCulture)).Append(',')
              .Append(e.LastRun.ToString(CultureInfo.InvariantCulture)).Append("\r\n");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
        string tmp = file + ".tmp";
        File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
        File.Move(tmp, file, overwrite: true);
        lock (_gate) _savedChanges = Math.Max(_savedChanges, changes);
    }

    static List<(string Path, Entry Entry)> Read(string file)
    {
        var list = new List<(string, Entry)>();
        string[] lines;
        try { lines = File.Exists(file) ? File.ReadAllLines(file, Encoding.UTF8) : []; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return list; }
        foreach (var line in lines)
            if (TryParse(line, out var path, out var e)) list.Add((path, e));
        return list;
    }

    /// <summary>One "path",count,date line (the path may also be unquoted; it may hold commas, the numbers can't).</summary>
    internal static bool TryParse(string line, out string path, out Entry entry)
    {
        path = "";
        entry = default;
        line = line.Trim();
        if (line.Length == 0 || line.StartsWith(Header, StringComparison.OrdinalIgnoreCase)) return false;
        string rest;
        if (line[0] == '"')
        {
            var sb = new StringBuilder();
            int i = 1;
            for (; i < line.Length; i++)
            {
                if (line[i] != '"') { sb.Append(line[i]); continue; }
                if (i + 1 < line.Length && line[i + 1] == '"') { sb.Append('"'); i++; continue; }
                break;
            }
            if (i >= line.Length) return false;
            path = sb.ToString();
            rest = line[(i + 1)..].TrimStart(',');
        }
        else
        {
            int second = line.LastIndexOf(',');
            int first = second > 0 ? line.LastIndexOf(',', second - 1) : -1;
            if (first <= 0) return false;
            path = line[..first];
            rest = line[(first + 1)..];
        }
        var parts = rest.Split(',');
        if (parts.Length < 1 || !int.TryParse(parts[0].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int count) || count <= 0) return false;
        long date = parts.Length > 1 && long.TryParse(parts[1].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long d) ? d : 0;
        if (path.Trim().Length == 0) return false;
        path = Normalize(path);
        entry = new Entry(count, Math.Max(0, date));
        return true;
    }

    // ---- by index id (for searching and sorting) --------------------------------------------------
    //
    // Searches need the run count of an index id. The history is matched against the index once (a pass that looks
    // only at entries named like a run file) and then kept current from the index's change log and from the paths that
    // changed here, so a search pays for a full match only after a rescan or a folder being renamed or moved.

    Dictionary<int, Entry> _byId = [];
    readonly Dictionary<int, string> _pathOfId = [];
    readonly HashSet<string> _dirtyPaths = new(StringComparer.OrdinalIgnoreCase);
    FileIndex? _resolvedFor;
    long _generation = -1, _sequence, _pathVersion = -1;
    bool _resolveAll = true;
    // Ids whose entry changed, for presorted orders by run count / date run (see RunLookup.TryGetChangesSince).
    readonly List<int> _log = [];
    long _logBase;

    /// <summary>
    /// The history by id for <paramref name="index"/>, current with both. Call while holding the index's read lock.
    /// </summary>
    internal RunLookup Lookup(FileIndex index)
    {
        lock (_gate)
        {
            var touched = new List<int>();
            // A folder renamed or moved changes the paths below it without touching them: match everything again.
            bool full = _resolveAll || !ReferenceEquals(_resolvedFor, index) || _generation != index.Generation || _pathVersion != index.PathVersion
                        || !index.TryGetChangesSince(_sequence, touched) || touched.Count > 100_000;
            if (full) ResolveAll(index);
            else if (touched.Count > 0 || _dirtyPaths.Count > 0) Update(index, touched);
            _resolvedFor = index;
            _generation = index.Generation;
            _sequence = index.ChangeSequence;
            _pathVersion = index.PathVersion;
            _dirtyPaths.Clear();
            _resolveAll = false;
            return new RunLookup(_byId, _logBase + _log.Count, this);
        }
    }

    internal bool TryGetChanges(long from, long to, List<int> into)
    {
        lock (_gate)
        {
            if (from < _logBase || to > _logBase + _log.Count || from > to) return false;
            for (long i = from - _logBase; i < to - _logBase; i++) into.Add(_log[(int)i]);
            return true;
        }
    }

    void ResolveAll(FileIndex index)
    {
        var byId = new Dictionary<int, Entry>();
        _pathOfId.Clear();
        if (_byPath.Count > 0)
        {
            var leaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var path in _byPath.Keys) AddLeaves(path, leaves);
            ScanNames(index, leaves, id =>
            {
                string full = Normalize(index.GetFullPath(id));
                if (_byPath.TryGetValue(full, out var e)) { byId[id] = e; _pathOfId[id] = full; }
            });
        }
        _byId = byId;
        // Every value may differ: orders built on the old ones start over.
        _logBase += _log.Count + 1;
        _log.Clear();
    }

    /// <summary>Applies index changes (entries added, removed or renamed) and the paths that changed here.</summary>
    void Update(FileIndex index, List<int> touched)
    {
        var byId = new Dictionary<int, Entry>(_byId);
        var changedIds = new List<int>();
        var leaves = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in _byPath.Keys) AddLeaves(path, leaves);
        var seen = new HashSet<int>();
        foreach (int id in touched)
        {
            if (!seen.Add(id)) continue;
            bool alive = index.IsAlive(id);
            string? full = alive && leaves.Contains(index.Name(id)) ? Normalize(index.GetFullPath(id)) : null;
            if (full != null && _byPath.TryGetValue(full, out var e))
            {
                if (!byId.TryGetValue(id, out var had) || had != e) changedIds.Add(id);
                byId[id] = e;
                _pathOfId[id] = full;
            }
            else if (byId.Remove(id))
            {
                _pathOfId.Remove(id);
                changedIds.Add(id);
            }
        }
        if (_dirtyPaths.Count > 0)
        {
            // Ids already matched to a changed path take its new value (or lose it); then look for the paths not matched yet.
            var unmatched = new HashSet<string>(_dirtyPaths, StringComparer.OrdinalIgnoreCase);
            foreach (var (id, path) in _pathOfId.ToArray())
            {
                if (!_dirtyPaths.Contains(path)) continue;
                unmatched.Remove(path);
                if (_byPath.TryGetValue(path, out var e)) byId[id] = e;
                else { byId.Remove(id); _pathOfId.Remove(id); }
                changedIds.Add(id);
            }
            unmatched.RemoveWhere(p => !_byPath.ContainsKey(p));
            if (unmatched.Count > 0)
            {
                var want = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var path in unmatched) AddLeaves(path, want);
                ScanNames(index, want, id =>
                {
                    string full = Normalize(index.GetFullPath(id));
                    if (!unmatched.Contains(full) || !_byPath.TryGetValue(full, out var e)) return;
                    byId[id] = e;
                    _pathOfId[id] = full;
                    changedIds.Add(id);
                });
            }
        }
        if (changedIds.Count == 0) return;
        _byId = byId;
        _log.AddRange(changedIds);
        if (_log.Count > 1 << 16) { _logBase += _log.Count + 1; _log.Clear(); }
    }

    /// <summary>The name an entry of <paramref name="path"/> has in the index (a root's name is its path), both forms to be safe.</summary>
    static void AddLeaves(string path, HashSet<string> into)
    {
        string trimmed = path.TrimEnd('\\');
        into.Add(trimmed);
        int cut = trimmed.LastIndexOf('\\');
        if (cut >= 0) into.Add(trimmed[(cut + 1)..]);
    }

    /// <summary>Calls <paramref name="found"/> for every live entry whose name is in <paramref name="names"/> (lengths checked first: no hashing for most).</summary>
    static void ScanNames(FileIndex index, HashSet<string> names, Action<int> found)
    {
        if (names.Count == 0) return;
        var lengths = new bool[1024];
        bool longNames = false;
        foreach (var n in names) { if (n.Length < lengths.Length) lengths[n.Length] = true; else longNames = true; }
        int count = index.Count;
        for (int id = 0; id < count; id++)
        {
            if (!index.IsAlive(id)) continue;
            string name = index.Name(id);
            if (name.Length < lengths.Length ? !lengths[name.Length] : !longNames) continue;
            if (names.Contains(name)) found(id);
        }
    }
}

/// <summary>The run history by index id at one moment (immutable): what searches and sorts read.</summary>
internal sealed class RunLookup(Dictionary<int, RunHistory.Entry> byId, long sequence, RunHistory owner)
{
    public RunHistory.Entry Get(int id) => byId.TryGetValue(id, out var e) ? e : default;

    /// <summary>Position in the owner's change log this lookup reflects.</summary>
    public long Sequence => sequence;

    /// <summary>Ids whose run entry changed between <paramref name="from"/> and this lookup; false when that history is gone.</summary>
    public bool TryGetChangesSince(long from, List<int> into) => owner.TryGetChanges(from, sequence, into);
}
