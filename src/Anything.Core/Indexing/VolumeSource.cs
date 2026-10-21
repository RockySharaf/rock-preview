using Microsoft.Win32.SafeHandles;

namespace Anything.Core.Indexing;

/// <summary>
/// Where an NTFS volume's records and change journal come from: the volume itself (<see cref="LocalVolumeSource"/>,
/// needs administrator) or the Rock Preview service, which reads them for the user after a one-time install
/// (<see cref="ServiceVolumeSource"/>). Either way the indexer gets metadata only: names, parents, sizes, dates.
/// </summary>
internal interface IVolumeSource : IDisposable
{
    /// <summary>"local" or "service", for the status text.</summary>
    string Kind { get; }

    /// <summary>Whether the records it reads carry dates accessed (a service from before them doesn't send them).</summary>
    bool ProvidesAccessTimes => true;

    /// <summary>Queries the USN journal; with <paramref name="create"/>, creates it first when the volume has none.</summary>
    bool TryQueryJournal(bool create, out UsnJournalInfo info);

    /// <summary>
    /// Every record of the volume: from the raw MFT, else from the USN enumeration up to <paramref name="highUsn"/>.
    /// <paramref name="method"/> is "MFT" or "USN".
    /// </summary>
    VolumeRecords ReadRecords(long highUsn, CancellationToken ct, Action<int> progress, out string method);

    /// <summary>
    /// Journal records from <paramref name="start"/>: bytes in <paramref name="buffer"/> (the first 8 are the next
    /// USN), or -Win32 error. Never waits: with nothing new it returns just the next USN (the caller polls).
    /// </summary>
    unsafe int ReadJournal(ulong journalId, long start, byte* buffer, int size);
}

/// <summary>The volume opened directly (\\.\X:), which needs administrator.</summary>
internal sealed class LocalVolumeSource : IVolumeSource
{
    readonly SafeFileHandle _volume;

    LocalVolumeSource(SafeFileHandle volume) => _volume = volume;

    /// <summary>Opens \\.\X: for reading; null when that isn't allowed (not administrator) or fails.</summary>
    public static LocalVolumeSource? TryOpen(string drive)
    {
        var h = Native.CreateFile(@"\\.\" + drive, Native.GENERIC_READ, Native.FILE_SHARE_ALL, 0, Native.OPEN_EXISTING, Native.FILE_FLAG_BACKUP_SEMANTICS, 0);
        if (h.IsInvalid) { h.Dispose(); return null; }
        return new LocalVolumeSource(h);
    }

    public string Kind => "local";

    public bool TryQueryJournal(bool create, out UsnJournalInfo info) =>
        create ? UsnJournal.TryQueryOrCreate(_volume, out info) : UsnJournal.TryQuery(_volume, out info, out _);

    public VolumeRecords ReadRecords(long highUsn, CancellationToken ct, Action<int> progress, out string method)
    {
        try
        {
            var records = MftReader.Read(_volume, ct, progress);
            method = "MFT";
            return records;
        }
        catch (OperationCanceledException) { throw; }
        // Cancelled, or the handle was closed for an eject: reading the volume another way would only hold it open.
        catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
        catch (ObjectDisposedException) { throw; }
        catch (Exception)
        {
            progress(0);
            var records = UsnJournal.Enumerate(_volume, highUsn, ct, progress);
            method = "USN";
            return records;
        }
    }

    public unsafe int ReadJournal(ulong journalId, long start, byte* buffer, int size) =>
        UsnJournal.Read(_volume, journalId, start, buffer, size);

    public void Dispose() => _volume.Dispose();
}
