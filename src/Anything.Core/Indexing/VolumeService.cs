using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Anything.Core.Indexing;

/// <summary>
/// The Rock Preview service (installed once, with the administrator's approval, and running as LocalSystem) reads
/// NTFS volumes for the users it was installed for, so Rock Preview itself never needs administrator rights.
/// Deliberately narrow: local NTFS volumes only (fixed and USB), read-only operations only, and it returns metadata only
/// (names, parent folders, sizes, dates, attributes and change-journal records), never raw disk data, so it can't
/// be used to read file contents. The pipe admits only the allowed users, SYSTEM and administrators, never the network.
/// </summary>
public static class VolumeService
{
    public const string PipeName = "RockPreview.Volumes";
    /// <summary>2: records carry dates accessed. Version 1 clients (and services) are still served (and used).</summary>
    public const int ProtocolVersion = 2;
    internal const int OldestProtocolVersion = 1;

    /// <summary>
    /// <see cref="OpClose"/> closes the volume and acknowledges, so a USB drive can be ejected right after; sent during
    /// <see cref="OpReadRecords"/>, it stops the read and the connection just ends once the volume is closed. Services
    /// from before it existed drop the connection on an unknown op, which closes the volume too.
    /// </summary>
    internal const byte OpQueryJournal = 2, OpReadRecords = 3, OpReadJournal = 4, OpClose = 5;
    internal const byte ReplyOk = 0, ReplyError = 1, ReplyProgress = 2;
    internal const int MaxJournalRead = 1 << 20;
    const int MaxConnections = 32;

    /// <summary>Whether the service is installed and answering.</summary>
    public static bool IsRunning() => ServiceVolumeSource.IsAvailable();

    /// <summary>Serves clients until <paramref name="ct"/> is cancelled.</summary>
    public static void Run(IReadOnlyCollection<SecurityIdentifier> allowedUsers, CancellationToken ct)
    {
        var security = CreateSecurity(allowedUsers);
        int active = 0;
        while (!ct.IsCancellationRequested)
        {
            NamedPipeServerStream pipe;
            try
            {
                pipe = NamedPipeServerStreamAcl.Create(PipeName, PipeDirection.InOut, NamedPipeServerStream.MaxAllowedServerInstances,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, 0, 0, security);
            }
            catch (Exception) { ct.WaitHandle.WaitOne(1000); continue; }
            try { pipe.WaitForConnectionAsync(ct).GetAwaiter().GetResult(); }
            catch (Exception) { pipe.Dispose(); continue; }
            if (Interlocked.Increment(ref active) > MaxConnections)
            {
                Interlocked.Decrement(ref active);
                pipe.Dispose();
                continue;
            }
            new Thread(() =>
            {
                try { Serve(pipe, ct); }
                catch (Exception) { }
                finally { pipe.Dispose(); Interlocked.Decrement(ref active); }
            }) { IsBackground = true, Name = "Rock Preview service client" }.Start();
        }
    }

    static PipeSecurity CreateSecurity(IEnumerable<SecurityIdentifier> users)
    {
        var s = new PipeSecurity();
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null), PipeAccessRights.FullControl, AccessControlType.Allow));
        s.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null), PipeAccessRights.ReadWrite, AccessControlType.Allow));
        foreach (var user in users) s.AddAccessRule(new PipeAccessRule(user, PipeAccessRights.ReadWrite, AccessControlType.Allow));
        return s;
    }

    /// <summary>Only "X:" of a local (fixed or USB), ready NTFS volume.</summary>
    static bool IsLocalNtfsVolume(string drive)
    {
        if (drive.Length != 2 || !char.IsAsciiLetter(drive[0]) || drive[1] != ':') return false;
        try
        {
            var d = new DriveInfo(drive);
            return d.DriveType is DriveType.Fixed or DriveType.Removable && d.IsReady && d.DriveFormat == "NTFS";
        }
        catch (Exception) { return false; }
    }

    static unsafe void Serve(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var stop = ct.Register(pipe.Dispose);
        // Requests are read straight from the pipe (BinaryReader never reads past what it is asked for): nothing the app
        // sends waits in a buffer, so the watch during a read (ReadRecords) sees the very next byte, and the buffered
        // replies never meet unread input (a BufferedStream can't write over read-ahead it can't seek back from).
        using var r = new BinaryReader(pipe, Encoding.UTF8, leaveOpen: true);
        var output = new BufferedStream(pipe, 1 << 20);
        using var w = new BinaryWriter(output, Encoding.UTF8, leaveOpen: true);

        int version = r.ReadInt32();
        string drive = ReadShortString(r, 16);
        if (version < OldestProtocolVersion || version > ProtocolVersion) { Fail(w, $"Ultimate File Explorer service speaks protocol {ProtocolVersion}, not {version}."); return; }
        if (!IsLocalNtfsVolume(drive)) { Fail(w, "Not a local NTFS volume."); return; }
        using var volume = LocalVolumeSource.TryOpen(drive.ToUpperInvariant());
        if (volume == null) { Fail(w, "The volume can't be opened."); return; }
        w.Write(ReplyOk);
        w.Flush();

        byte[] journal = new byte[MaxJournalRead];
        int early = -1; // a request that came in while records were being read
        while (true)
        {
            int op = early >= 0 ? early : pipe.ReadByte();
            early = -1;
            if (op < 0) return; // the app closed the connection
            switch (op)
            {
                case OpQueryJournal:
                {
                    bool create = r.ReadBoolean();
                    bool ok = volume.TryQueryJournal(create, out var info);
                    w.Write(ok);
                    w.Write(info.JournalId); w.Write(info.FirstUsn); w.Write(info.NextUsn); w.Write(info.LowestValidUsn);
                    w.Write(info.MaxUsn); w.Write(info.MaximumSize); w.Write(info.AllocationDelta);
                    w.Flush();
                    break;
                }
                case OpReadRecords:
                    if (!ReadRecords(pipe, w, volume, r.ReadInt64(), ct, out early, accessTimes: version >= 2)) return;
                    break;
                case OpReadJournal:
                {
                    ulong journalId = r.ReadUInt64();
                    long start = r.ReadInt64();
                    int size = Math.Clamp(r.ReadInt32(), 4096, MaxJournalRead);
                    int ret;
                    fixed (byte* p = journal) ret = volume.ReadJournal(journalId, start, p, size);
                    w.Write(ret);
                    if (ret > 0) w.Write(journal, 0, ret);
                    w.Flush();
                    break;
                }
                case OpClose:
                    volume.Dispose();
                    w.Write(ReplyOk);
                    w.Flush();
                    return;
                default:
                    return; // not a Rock Preview client: drop it
            }
        }
    }

    /// <summary>
    /// Reads every record of the volume for the app. While the app waits it sends nothing but <see cref="OpClose"/>, so a
    /// 1-byte read waits on the pipe meanwhile: that byte, or the app hanging up (an eject, a cancelled scan), stops the
    /// read and closes the volume at once, not at the next progress report after the disk read under way. The app takes
    /// the end of the connection to mean the volume is closed, so after a hang-up it ends only once the read has let go
    /// of the volume. False when the connection is done; <paramref name="next"/> is a request that came in early, else -1.
    /// </summary>
    internal static bool ReadRecords(PipeStream pipe, BinaryWriter w, IVolumeSource volume, long highUsn, CancellationToken ct, out int next,
        bool accessTimes = true)
    {
        const int Unwatched = -1, Gone = -2;
        var gate = new object();
        long last = 0;
        using var hungUp = CancellationTokenSource.CreateLinkedTokenSource(ct);
        int closing = 0;
        void HangUp()
        {
            if (Interlocked.Exchange(ref closing, 1) != 0) return;
            // Cancelled first, so the read takes the closed handle for a stop, not for a reason to try the USN enumeration.
            hungUp.Cancel();
            // A ReadFile under way keeps the handle (SafeHandle counts it): it really closes as soon as that returns.
            volume.Dispose();
        }
        void Progress(int n)
        {
            long now = Environment.TickCount64;
            if (now - Interlocked.Read(ref last) < 200 || hungUp.IsCancellationRequested) return;
            lock (gate)
            {
                Interlocked.Exchange(ref last, now);
                try
                {
                    w.Write(ReplyProgress);
                    w.Write(n);
                    w.Flush();
                }
                catch (Exception ex) when (ex is IOException or ObjectDisposedException) { HangUp(); }
            }
        }

        var op = new byte[1];
        using var unwatch = new CancellationTokenSource();
        Task<int> read;
        try { read = pipe.ReadAsync(op, unwatch.Token).AsTask(); }
        catch (Exception ex) { read = Task.FromException<int>(ex); }
        var watch = read.ContinueWith(t =>
        {
            if (t.IsCompletedSuccessfully && t.Result == 1 && op[0] != OpClose) return op[0];
            if (!t.IsCompletedSuccessfully && unwatch.IsCancellationRequested) return Unwatched;
            HangUp(); // the end of the stream, a broken pipe, or OpClose
            return Gone;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        bool open = false;
        try
        {
            var records = volume.ReadRecords(highUsn, hungUp.Token, Progress, out string method);
            // Still watched while the reply goes out: a stop that comes meanwhile closes the volume all the same.
            lock (gate)
            {
                w.Write(ReplyOk);
                w.Write(method);
                records.WriteTo(w, accessTimes);
                w.Flush();
            }
            open = true;
        }
        catch (OperationCanceledException) { } // the app hung up, or the service is stopping
        catch (Exception ex) when (ex is not IOException and not ObjectDisposedException)
        {
            lock (gate) Fail(w, ex.Message);
            open = true;
        }
        catch (Exception) { } // the drive or the connection failed
        finally
        {
            unwatch.Cancel();
            next = watch.GetAwaiter().GetResult();
            if (next == Gone) open = false;
            if (!open)
            {
                HangUp();
                pipe.Dispose(); // tells the app the volume is closed
            }
            // A full read briefly needs hundreds of MB; give it back so the idle service stays small. Only now: a
            // blocking collection before the close above would hold up an eject.
            GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
        }
        return open;
    }

    /// <summary>A length-prefixed string (as BinaryWriter writes it) of at most <paramref name="maxBytes"/>, so a client can't make the service allocate much.</summary>
    static string ReadShortString(BinaryReader r, int maxBytes)
    {
        int n = r.Read7BitEncodedInt();
        if (n < 0 || n > maxBytes) throw new InvalidDataException("request too long");
        return Encoding.UTF8.GetString(r.ReadBytes(n));
    }

    static void Fail(BinaryWriter w, string message)
    {
        w.Write(ReplyError);
        w.Write(message);
        w.Flush();
    }
}
