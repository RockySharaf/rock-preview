namespace Anything.Core.Indexing;

/// <summary>Which drives were just plugged in, between two listings of the drives: what Rock Preview offers to open.</summary>
public static class DriveArrival
{
    /// <summary>
    /// The drives in <paramref name="now"/> that weren't ready in <paramref name="before"/>: a drive letter of the kind a
    /// USB stick or disk shows as (removable, or a fixed disk), ready, and not a SUBST letter or a network drive. A card
    /// put in a reader counts too (its letter was there, empty). The first listing (<paramref name="before"/> null) has
    /// none: what is there when Rock Preview starts was plugged in before.
    /// </summary>
    public static List<DriveEntry> Plugged(IReadOnlyList<DriveEntry>? before, IReadOnlyList<DriveEntry> now)
    {
        if (before is null) return [];
        var ready = new HashSet<string>(before.Where(d => d.IsReady).Select(d => d.Root), StringComparer.OrdinalIgnoreCase);
        return now.Where(d => d.IsDriveLetter && d.IsReady && d.Kind is DriveKind.Removable or DriveKind.Fixed
                              && d.NetworkPath is null && d.SubstTarget is null && !ready.Contains(d.Root)).ToList();
    }

    /// <summary>How a drive is named in the prompt: its label and letter ("SANDISK (E:)"), or "USB drive (E:)".</summary>
    public static string Name(DriveEntry drive)
    {
        string letter = drive.Root.TrimEnd('\\');
        return string.IsNullOrWhiteSpace(drive.Label) ? $"USB drive ({letter})" : $"{drive.Label} ({letter})";
    }
}
