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

        // Record 0 describes $MFT itself.
        var mft = new MftRecord();
        var runs = new List<DataRun>();
        long dataSize, initSize;
        {
            byte[] rec0 = ReadVirtual(volume, [new DataRun(0, bs.MftLcn, long.MaxValue / cs)], cs, ss, 0, rs);
            if (!MftRecordParser.ApplyFixups(rec0) || !MftRecordParser.Parse(rec0, 0, mft, captureDataRuns: true) || !mft.HasData || !mft.DataNonResident)
                throw new InvalidDataException("cannot parse $MFT record 0");
            runs.AddRange(mft.DataRuns);
            dataSize = mft.DataSize;
            initSize = mft.DataInitializedSize;
            // Heavily fragmented $MFT: further $DATA extents live in extension records listed in $ATTRIBUTE_LIST.
            if (mft.HasAttributeList)
            {
                var extents = mft.AttributeList.Where(e => e.Type == MftRecordParser.AttrData && !e.Named && e.RecordNumber != 0 && e.StartingVcn > 0)
                    .OrderBy(e => e.StartingVcn).ToList();
                var ext = new MftRecord();
                foreach (var e in extents)
                {
                    byte[] buf = ReadVirtual(volume, runs, cs, ss, e.RecordNumber * rs, rs);
                    if (!MftRecordParser.ApplyFixups(buf) || !MftRecordParser.Parse(buf, e.RecordNumber, ext, captureDataRuns: true))
                        throw new InvalidDataException("cannot parse $MFT extension record");
                    runs.AddRange(ext.DataRuns);
                }
                runs.Sort((a, b) => a.Vcn.CompareTo(b.Vcn));
            }
        }
        long mftBytes = initSize > 0 ? Math.Min(dataSize, initSize) : dataSize;
        long totalRecords = mftBytes / rs;
        if (totalRecords <= 0 || totalRecords > int.MaxValue) throw new InvalidDataException("invalid $MFT size");

        var records = new VolumeRecords((int)Math.Min(totalRecords, 1 << 24));

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
        records.EnsureRoot();
        return records;
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
            if (!Native.ReadFile(handle, buffer + done, length - done, out int read, &ov))
                throw new Win32Exception(Marshal.GetLastPInvokeError(), $"volume read failed at offset {offset + done}");
            if (read == 0) throw new EndOfStreamException("unexpected end of volume");
            done += read;
        }
    }
}
