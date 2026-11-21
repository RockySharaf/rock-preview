namespace Anything.Core;

/// <summary>Rules for what goes on and comes off the clipboard that need no window, so they can be tested.</summary>
public static class ClipboardRules
{
    /// <summary>The "Preferred DropEffect" Explorer writes for Cut (Ctrl+X): DROPEFFECT_MOVE.</summary>
    public const int DropEffectMove = 2;

    /// <summary>
    /// File Explorer's "Copy as path": each full path in double quotes, one per line (CRLF between them, none after the
    /// last). Empty paths are left out; empty when there are none.
    /// </summary>
    public static string QuotedPaths(IEnumerable<string> paths)
        => string.Join("\r\n", paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => "\"" + p + "\""));

    /// <summary>
    /// The paths that are cut, waiting to be pasted (shown dimmed, as in Explorer): the clipboard's files when its
    /// "Preferred DropEffect" says move; none for copied files, other content or no effect at all. Case doesn't matter.
    /// </summary>
    public static HashSet<string> CutPaths(IEnumerable<string>? files, int? preferredDropEffect)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (files is null || preferredDropEffect != DropEffectMove) return set;
        foreach (string f in files)
            if (!string.IsNullOrWhiteSpace(f)) set.Add(f.TrimEnd('\\').Length == 2 ? f : f.TrimEnd('\\')); // "C:\" stays a root
        return set;
    }

    /// <summary>The DWORD a "Preferred DropEffect" clipboard entry holds, or null when it is too short to hold one.</summary>
    public static int? ReadDropEffect(ReadOnlySpan<byte> data)
        => data.Length >= 4 ? BitConverter.ToInt32(data[..4]) : null;
}
