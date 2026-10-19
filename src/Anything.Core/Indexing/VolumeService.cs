using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;

namespace Anything.Core.Indexing;

/// <summary>
/// The Rock Preview service (installed once, with the administrator's approval, and running as LocalSystem) reads
/// NTFS volumes for the users it was installed for, so Rock Preview itself never needs administrator rights.
/// Deliberately narrow: local fixed NTFS volumes only, read-only operations only, and it returns metadata only
/// (names, parent folders, sizes, dates, attributes and change-journal records), never raw disk data, so it can't
/// be used to read file contents. The pipe admits only the allowed users, SYSTEM and administrators, never the network.
/// </summary>
public static class VolumeService
{
    public const string PipeName = "RockPreview.Volumes";
    public const int ProtocolVersion = 1;

    internal const byte OpQueryJournal = 2, OpReadRecords = 3, OpReadJournal = 4;
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

    /// <summary>Only "X:" of a local, fixed, ready NTFS volume.</summary>
    static bool IsLocalNtfsVolume(string drive)
    {
        if (drive.Length != 2 || !char.IsAsciiLetter(drive[0]) || drive[1] != ':') return false;
        try
        {
            var d = new DriveInfo(drive);
            return d.DriveType == DriveType.Fixed && d.IsReady && d.DriveFormat == "NTFS";
        }
        catch (Exception) { return false; }
    }

    static unsafe void Serve(NamedPipeServerStream pipe, CancellationToken ct)
    {
        using var stop = ct.Register(pipe.Dispose);
        var stream = new BufferedStream(pipe, 1 << 20);
        using var r = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        using var w = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);

        int version = r.ReadInt32();
        string drive = ReadShortString(r, 16);
        if (version != ProtocolVersion) { Fail(w, $"Rock Preview service speaks protocol {ProtocolVersion}, not {version}."); return; }
        if (!IsLocalNtfsVolume(drive)) { Fail(w, "Not a local NTFS volume."); return; }
        using var volume = LocalVolumeSource.TryOpen(drive.ToUpperInvariant());
        if (volume == null) { Fail(w, "The volume can't be opened."); return; }
        w.Write(ReplyOk);
        w.Flush();

        byte[] journal = new byte[MaxJournalRead];
        while (true)
        {
            int op = stream.ReadByte();
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
                {
                    long highUsn = r.ReadInt64();
                    var gate = new object();
                    long last = 0;
                    void Progress(int n)
                    {
                        long now = Environment.TickCount64;
                        if (now - Interlocked.Read(ref last) < 200) return;
                        lock (gate)
                        {
                            Interlocked.Exchange(ref last, now);
                            w.Write(ReplyProgress);
                            w.Write(n);
                            w.Flush();
                        }
                    }
                    try
                    {
                        var records = volume.ReadRecords(highUsn, ct, Progress, out string method);
                        lock (gate)
                        {
                            w.Write(ReplyOk);
                            w.Write(method);
                            records.WriteTo(w);
                            w.Flush();
                        }
                    }
                    catch (Exception ex) when (ex is not IOException and not ObjectDisposedException)
                    {
                        lock (gate) Fail(w, ex.Message);
                    }
                    finally
                    {
                        // A full read briefly needs hundreds of MB; give it back so the idle service stays small.
                        GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                    }
                    break;
                }
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
                default:
                    return; // not a Rock Preview client: drop it
            }
        }
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
