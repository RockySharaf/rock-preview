using System.Globalization;
using System.Text;
using Anything.Core;

namespace Anything;

/// <summary>Display view-model for one visible result row (cached while the row is on screen).</summary>
internal sealed class Row
{
    public int Id;
    public string Name = "";
    public string Path = "";
    public string FullPath = "";
    public string Ext = "";
    public bool IsDir, IsRoot;
    public long Size = -1;
    public string SizeText = "", ModifiedText = "", CreatedText = "", AttrText = "";
    /// <summary>Date accessed from the index (FILETIME UTC), or read from the disk when it isn't indexed (<see cref="AccessedText"/>).</summary>
    public long Accessed;
    bool _accessedFromDisk;
    public long RecentChange, LastRun;
    public int RunCount;
    string? _type;
    public int Icon = -1;
    public IReadOnlyList<(int Start, int Length)>? NameHighlights, PathHighlights;
    public ListViewItem? Item;
    /// <summary>The line this result matched inside the file (Matching text), once SnippetResolver has it; null meanwhile or when none.</summary>
    public Anything.Core.Content.ContentSnippet? Snippet;
    /// <summary>Why the file's words aren't searchable, shown in Matching text instead of a line ("Password-protected").</summary>
    public string? SnippetReason;
    /// <summary>The snippet was looked up for the current query (found or not), so the row doesn't ask again.</summary>
    public bool SnippetAsked;

    public string TypeText => _type ??= ShellInfo.GetTypeName(Ext, IsDir);

    public static Row Build(FileIndex index, int id)
    {
        var r = new Row { Id = id };
        try
        {
            r.Name = index.Name(id) ?? "";
            if (!index.IsAlive(id)) return r;
            r.IsDir = index.IsDirectory(id);
            r.IsRoot = index.Parent(id) == FileIndex.NoParent;
            r.FullPath = index.GetFullPath(id);
            r.Path = r.IsRoot ? "" : index.GetParentPath(id);
            r.Ext = index.Extension(id);
            r.Size = index.Size(id);
            // Folders have a size only with folder sizes on (Settings › Indexing); otherwise it's unknown (-1).
            r.SizeText = r.Size < 0 ? "" : FormatKb(r.Size);
            r.ModifiedText = FormatDate(index.Modified(id));
            r.CreatedText = FormatDate(index.Created(id));
            r.AttrText = FormatAttributes(index.Attributes(id));
            // Not indexed, or not known to the index (a drive read through an older service): from the disk.
            r.Accessed = index.Accessed(id);
            r._accessedFromDisk = r.Accessed == 0;
            r.RecentChange = index.RecentChange(id);
            var run = index.RunHistory.Get(r.FullPath);
            r.RunCount = run.Count;
            r.LastRun = run.LastRun;
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or NullReferenceException or ArgumentException)
        {
            // The index changed underneath us; the pending re-search will replace this row.
        }
        return r;
    }

    public string Text(ColumnDef c) => c.Key switch
    {
        "name" => Name,
        "path" => Path,
        "size" => SizeText,
        "extension" => Ext,
        "type" => Name.Length == 0 ? "" : TypeText,
        "date_modified" => ModifiedText,
        "date_created" => CreatedText,
        "attributes" => AttrText,
        "date_accessed" => Name.Length == 0 ? "" : FormatDate(AccessedValue),
        "date_recently_changed" => FormatDate(RecentChange),
        "run_count" => RunCount > 0 ? RunCount.ToString("N0", CultureInfo.CurrentCulture) : "",
        "date_run" => FormatDate(LastRun),
        "content" => Snippet is { } s ? Anything.Core.Content.ContentPresentation.SnippetPrefix(s) + Anything.Core.Content.ContentPresentation.SnippetBody(s).Text : SnippetReason ?? "",
        _ => "",
    };

    /// <summary>
    /// Date accessed; when it isn't indexed, read from the disk in the background (0 until it is: the window repaints
    /// when <see cref="DiskDates.Resolved"/> says it came in).
    /// </summary>
    public long AccessedValue
    {
        get
        {
            if (!_accessedFromDisk || FullPath.Length == 0) return Accessed;
            if (!DiskDates.TryGet(FullPath, out long t)) return 0;
            Accessed = t;
            _accessedFromDisk = false;
            return t;
        }
    }

    /// <summary>Everything style: kilobytes rounded up with thousands separators.</summary>
    public static string FormatKb(long bytes) => ((bytes + 1023) / 1024).ToString("N0", CultureInfo.CurrentCulture) + " KB";

    public static string FormatSize(long bytes)
    {
        string[] units = ["bytes", "KB", "MB", "GB", "TB", "PB"];
        if (bytes < 1024) return bytes.ToString("N0", CultureInfo.CurrentCulture) + " bytes";
        double v = bytes;
        int u = 0;
        while (v >= 1024 && u < units.Length - 1) { v /= 1024; u++; }
        return v.ToString(v < 10 ? "0.##" : v < 100 ? "0.#" : "N0", CultureInfo.CurrentCulture) + " " + units[u];
    }

    public static string FormatDate(long fileTimeUtc)
    {
        if (fileTimeUtc <= 0) return "";
        try { return DateTime.FromFileTimeUtc(fileTimeUtc).ToLocalTime().ToString("g", CultureInfo.CurrentCulture); }
        catch (ArgumentOutOfRangeException) { return ""; }
    }

    public static string FormatAttributes(FileAttributes a)
    {
        var sb = new StringBuilder(8);
        if ((a & FileAttributes.ReadOnly) != 0) sb.Append('R');
        if ((a & FileAttributes.Hidden) != 0) sb.Append('H');
        if ((a & FileAttributes.System) != 0) sb.Append('S');
        if ((a & FileAttributes.Directory) != 0) sb.Append('D');
        if ((a & FileAttributes.Archive) != 0) sb.Append('A');
        if ((a & FileAttributes.Compressed) != 0) sb.Append('C');
        if ((a & FileAttributes.Encrypted) != 0) sb.Append('E');
        if ((a & FileAttributes.NotContentIndexed) != 0) sb.Append('I');
        if ((a & FileAttributes.ReparsePoint) != 0) sb.Append('L');
        if ((a & FileAttributes.Offline) != 0) sb.Append('O');
        if ((a & FileAttributes.SparseFile) != 0) sb.Append('P');
        if ((a & FileAttributes.Temporary) != 0) sb.Append('T');
        return sb.ToString();
    }
}
