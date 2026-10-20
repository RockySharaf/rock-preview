using System.Collections.Concurrent;
using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Anything.Core.Indexing;

/// <summary>
/// Streams the raw $MFT of an NTFS volume (boot sector → record 0 → $DATA runs → large sequential reads)
/// and parses every FILE record into a <see cref="VolumeRecords"/>. Needs a volume handle (administrator) in
/// production; any readable handle to an NTFS image works too (used by tests).
/// </summary>
public static unsafe class MftReader
{
    const int ChunkSize = 16 << 20;
    // One ReadFile reads at most this much: closing the volume for an eject takes effect once the read under way
    // returns, a fraction of a second even on a slow USB stick, rather than after a whole chunk.
    const int MaxRead = 4 << 20;
    const uint IOCTL_DISK_GET_LENGTH_INFO = 0x0007405C;

    /// <remarks>
    /// Everything here comes from the disk, which can be a crafted USB stick read by the LocalSystem service: every
    /// position and size is checked against the volume before it is used, so none can drive a huge allocation.
    /// </remarks>
    public static VolumeRecords Read(SafeFileHandle volume, CancellationToken ct, Action<int>? progress)
    {
        byte* boot = (byte*)NativeMemory.AlignedAlloc(4096, 4096);
        NtfsBootSector bs;
        try
        {
            ReadAt(volume, boot, 4096, 0);
            bs = NtfsBootSector.Parse(new ReadOnlySpan<byte>(boot, 4096));
        }
        finally { NativeMemory.AlignedFree(boot); }

        int rs = bs.BytesPerFileRecord;
        long cs = bs.BytesPerCluster;
        int ss = bs.BytesPerSector;
        long volumeBytes = VolumeLength(volume, bs);
        long clusters = volumeBytes / cs;
        // The MFT can't hold more records than fit on the volume.
        long maxRecords = Math.Min(volumeBytes / rs, int.MaxValue);
        if (bs.MftLcn >= clusters) throw new InvalidDataException("invalid mft_lcn");

        // Record 0 describes $MFT itself.
        var mft = new MftRecord();
        var runs = new List<DataRun>();
        long dataSize, allocatedSize, initSize;
        {
            byte[] rec0 = ReadVirtual(volume, [new DataRun(0, bs.MftLcn, clusters - bs.MftLcn)], cs, ss, 0, rs);
            if (!MftRecordParser.ApplyFixups(rec0) || !MftRecordParser.Parse(rec0, 0, mft, captureDataRuns: true) || !mft.HasData || !mft.DataNonResident)
                throw new InvalidDataException("cannot parse $MFT record 0");
            runs.AddRange(CheckRuns(mft.DataRuns, clusters));
            dataSize = mft.DataSize;
            allocatedSize = mft.DataAllocatedSize;
            initSize = mft.DataInitializedSize;
            // Heavily fragmented $MFT: further $DATA extents live in extension records listed in $ATTRIBUTE_LIST.
            if (mft.HasAttributeList)
            {
                // Each extension record once: it is parsed whole (all its extents), and a list naming one record over
                // and over would otherwise add its runs over and over.
                var extents = mft.AttributeList.Where(e => e.Type == MftRecordParser.AttrData && !e.Named && e.RecordNumber != 0 && e.StartingVcn > 0)
                    .OrderBy(e => e.StartingVcn).DistinctBy(e => e.RecordNumber).ToList();
                var ext = new MftRecord();
                foreach (var e in extents)
                {
                    if (e.RecordNumber >= maxRecords) throw new InvalidDataException("invalid $MFT extension record");
                    byte[] buf = ReadVirtual(volume, runs, cs, ss, e.RecordNumber * rs, rs);
                    if (!MftRecordParser.ApplyFixups(buf) || !MftRecordParser.Parse(buf, e.RecordNumber, ext, captureDataRuns: true))
                        throw new InvalidDataException("cannot parse $MFT extension record");
                    runs.AddRange(CheckRuns(ext.DataRuns, clusters));
                }
                runs.Sort((a, b) => a.Vcn.CompareTo(b.Vcn));
            }
        }
        if (dataSize < 0 || dataSize > allocatedSize || allocatedSize > volumeBytes) throw new InvalidDataException("invalid $MFT size");
        long mappedClusters = CheckContiguous(runs, allocatedSize / cs + (allocatedSize % cs != 0 ? 1 : 0));
        long mftBytes = initSize > 0 ? Math.Min(dataSize, initSize) : dataSize;
        long totalRecords = mftBytes / rs;
        if (totalRecords <= 0 || totalRecords > maxRecords) throw new InvalidDataException("invalid $MFT size");
        if (totalRecords * rs > mappedClusters * cs) throw new InvalidDataException("$MFT data runs end before its records do");

        // Record numbers (and base-record references) at or past the end of the MFT are ignored.
        var records = new VolumeRecords((int)Math.Min(totalRecords, 1 << 24), (int)totalRecords);

        // Reader thread fills chunks; this thread parses them (double/triple buffering).
        var free = new BlockingCollection<nint>();
        var full = new BlockingCollection<(nint Buffer, long FirstRecord, int Bytes)>();
        var buffers = new nint[3];
        for (int i = 0; i < buffers.Length; i++) { buffers[i] = (nint)NativeMemory.AlignedAlloc(ChunkSize, 4096); free.Add(buffers[i]); }
        using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var reader = Task.Run(() =>
        {
            try
            {
                long v = 0, end = totalRecords * rs;
                while (v < end)
                {
                    readCts.Token.ThrowIfCancellationRequested();
                    nint buf = free.Take(readCts.Token);
                    int want = (int)Math.Min(ChunkSize, end - v);
                    FillVirtual(volume, runs, cs, ss, v, (byte*)buf, want, ChunkSize);
                    full.Add((buf, v / rs, want), readCts.Token);
                    v += want;
                }
            }
            finally { full.CompleteAdding(); }
        }, readCts.Token);

        try
        {
            int parsed = 0;
            foreach (var (buf, first, bytes) in full.GetConsumingEnumerable(ct))
            {
                var chunk = new Span<byte>((void*)buf, bytes);
                int count = bytes / rs;
                for (int i = 0; i < count; i++)
                {
                    var rec = chunk.Slice(i * rs, rs);
                    if (!MftRecordParser.HasFileSignature(rec)) continue;
                    // Skip unused records before paying for fixups.
                    if ((rec[0x16] & 0x01) == 0) continue;
                    if (!MftRecordParser.ApplyFixups(rec)) continue;
                    if (!MftRecordParser.Parse(rec, first + i, mft)) continue;
                    records.Merge(mft);
                }
                parsed += count;
                progress?.Invoke(parsed);
                free.Add(buf);
            }
            reader.GetAwaiter().GetResult(); // rethrow read errors
        }
        catch
        {
            readCts.Cancel();
            try { reader.Wait(); } catch { }
            throw;
        }
        finally
        {
            foreach (var b in buffers) NativeMemory.AlignedFree((void*)b);
        }
        records.FinishMerge();
        records.EnsureRoot();
        return records;
    }

    /// <summary>
    /// The volume's size in bytes: what the boot sector says, but never more than the device (or image file) really
    /// holds, since the boot sector is as much the disk's say-so as the MFT is.
    /// </summary>
    static long VolumeLength(SafeFileHandle volume, in NtfsBootSector bs)
    {
        long bytes = bs.TotalSectors > long.MaxValue / bs.BytesPerSector ? long.MaxValue : bs.TotalSectors * bs.BytesPerSector;
        long length;
        if (Native.DeviceIoControl(volume, IOCTL_DISK_GET_LENGTH_INFO, null, 0, &length, sizeof(long), out int got, 0) && got == sizeof(long) && length > 0)
            return Math.Min(bytes, length);
        try
        {
            long file = RandomAccess.GetLength(volume); // an image file (tests)
            if (file > 0) return Math.Min(bytes, file);
        }
        catch (Exception) { }
        return bytes;
    }

    /// <summary>$MFT extents must be real clusters inside the volume: no sparse runs, nothing past its end.</summary>
    static List<DataRun> CheckRuns(List<DataRun> runs, long clusters)
    {
        foreach (var r in runs)
            if (r.IsSparse || r.Length <= 0 || r.Length > clusters || r.Lcn > clusters - r.Length || r.Vcn < 0 || r.Vcn > clusters - r.Length)
                throw new InvalidDataException("$MFT data run outside the volume");
        return runs;
    }

    /// <summary>
    /// The sorted $MFT extents must map it once: from VCN 0 on with no gaps and no overlaps, within its allocated size,
    /// and no cluster twice. Runs that repeat would let a small stick present the largest MFT the device size allows.
    /// Returns how many clusters they map.
    /// </summary>
    static long CheckContiguous(List<DataRun> runs, long allocatedClusters)
    {
        long next = 0;
        foreach (var r in runs)
        {
            if (r.Vcn != next) throw new InvalidDataException("$MFT data runs overlap or leave a gap");
            next += r.Length; // no overflow: CheckRuns keeps every run's end within the volume
        }
        if (next > allocatedClusters) throw new InvalidDataException("$MFT data runs map more than its allocated size");
        var byLcn = runs.ToArray();
        Array.Sort(byLcn, (a, b) => a.Lcn.CompareTo(b.Lcn));
        for (int i = 1; i < byLcn.Length; i++)
            if (byLcn[i].Lcn < byLcn[i - 1].Lcn + byLcn[i - 1].Length) throw new InvalidDataException("$MFT data runs map a cluster twice");
        return next;
    }

    /// <summary>Reads <paramref name="length"/> bytes at MFT-relative offset <paramref name="v"/> into a new array.</summary>
    static byte[] ReadVirtual(SafeFileHandle volume, List<DataRun> runs, long cs, int ss, long v, int length)
    {
        int cap = (int)AlignUp(Math.Max(length, ss), Math.Max(ss, 4096)) + (int)Math.Max(ss, 4096);
        byte* buf = (byte*)NativeMemory.AlignedAlloc((nuint)cap, 4096);
        try
        {
            // Align the start down to a sector boundary.
            long start = v - v % ss;
            int lead = (int)(v - start);
            int want = (int)AlignUp(lead + length, ss);
            FillVirtual(volume, runs, cs, ss, start, buf, want, cap);
            return new ReadOnlySpan<byte>(buf + lead, length).ToArray();
        }
        finally { NativeMemory.AlignedFree(buf); }
    }

    /// <summary>Fills <paramref name="buffer"/> with <paramref name="length"/> bytes of the MFT stream starting at virtual offset <paramref name="v"/>.</summary>
    static void FillVirtual(SafeFileHandle volume, List<DataRun> runs, long cs, int ss, long v, byte* buffer, int length, int capacity)
    {
        int filled = 0;
        int runIndex = 0;
        while (filled < length)
        {
            long vcn = (v + filled) / cs;
            while (runIndex < runs.Count && runs[runIndex].Vcn + runs[runIndex].Length <= vcn) runIndex++;
            if (runIndex >= runs.Count || runs[runIndex].Vcn > vcn) throw new InvalidDataException("$MFT offset outside data runs");
            var run = runs[runIndex];
            long inRun = (v + filled) - run.Vcn * cs;
            long avail = run.Length * cs - inRun;
            int n = (int)Math.Min(avail, length - filled);
            if (run.IsSparse)
            {
                new Span<byte>(buffer + filled, n).Clear();
            }
            else
            {
                int toRead = (int)Math.Min(AlignUp(n, ss), capacity - filled);
                ReadAt(volume, buffer + filled, toRead, run.Lcn * cs + inRun);
            }
            filled += n;
        }
    }

    static long AlignUp(long value, long alignment) => (value + alignment - 1) / alignment * alignment;

    internal static void ReadAt(SafeFileHandle handle, byte* buffer, int length, long offset)
    {
        int done = 0;
        while (done < length)
        {
            var ov = new NativeOverlapped { OffsetLow = (int)(offset + done), OffsetHigh = (int)((offset + done) >> 32) };
            if (!Native.ReadFile(handle, buffer + done, Math.Min(length - done, MaxRead), out int read, &ov))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"volume read failed at offset {offset + done}");
            if (read == 0) throw new EndOfStreamException("unexpected end of volume");
            done += read;
        }
    }
}
