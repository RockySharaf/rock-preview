using System.IO.Pipes;
using System.Security.Principal;
using System.Text;

namespace Anything.Core.Indexing;

/// <summary>
/// An NTFS volume read through the Rock Preview service (see <see cref="VolumeService"/>), for when Rock Preview runs
/// without administrator rights. One connection per volume; reconnects if the service restarts.
/// </summary>
internal sealed unsafe class ServiceVolumeSource : IVolumeSource
{
    const int ErrorBrokenPipe = 109, ErrorInvalidData = 13;

    readonly string _drive;
    readonly object _gate = new();
    NamedPipeClientStream? _pipe;
    BufferedStream? _stream;
    BinaryReader? _r;
    BinaryWriter? _w;

    ServiceVolumeSource(string drive) => _drive = drive;

    /// <summary>Connects to the service for <paramref name="drive"/> ("C:"); null when the service isn't installed or running.</summary>
    public static ServiceVolumeSource? TryConnect(string drive, int timeoutMs = 500)
    {
        var source = new ServiceVolumeSource(drive);
        if (source.Connect(timeoutMs)) return source;
        source.Dispose();
        return null;
    }

    /// <summary>Whether the service is installed and answering (any volume works for the check).</summary>
    public static bool IsAvailable(int timeoutMs = 300)
    {
        try
        {
            using var pipe = new NamedPipeClientStream(".", VolumeService.PipeName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
            pipe.Connect(timeoutMs);
            return IsServiceOwned(pipe);
        }
        catch (Exception) { return false; }
    }

    public string Kind => "service";

    bool Connect(int timeoutMs)
    {
        Close();
        var pipe = new NamedPipeClientStream(".", VolumeService.PipeName, PipeDirection.InOut, PipeOptions.None, TokenImpersonationLevel.Identification);
        try
        {
            pipe.Connect(timeoutMs);
            if (!IsServiceOwned(pipe)) { pipe.Dispose(); return false; }
            _pipe = pipe;
            _stream = new BufferedStream(pipe, 1 << 20);
            _r = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
            _w = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
            _w.Write(VolumeService.ProtocolVersion);
            _w.Write(_drive);
            _w.Flush();
            if (_r.ReadByte() == VolumeService.ReplyOk) return true;
        }
        catch (Exception) { }
        Close();
        return false;
    }

    /// <summary>
    /// The pipe must belong to the service (created by SYSTEM), not to some other program that took the same name
    /// first: anything else could feed Rock Preview a made-up file list.
    /// </summary>
    static bool IsServiceOwned(NamedPipeClientStream pipe)
    {
        try
        {
            var owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
            return owner != null && (owner.IsWellKnown(WellKnownSidType.LocalSystemSid) || owner.IsWellKnown(WellKnownSidType.BuiltinAdministratorsSid));
        }
        catch (Exception) { return false; }
    }

    bool EnsureConnected() => _pipe is { IsConnected: true } || Connect(2000);

    public bool TryQueryJournal(bool create, out UsnJournalInfo info)
    {
        info = default;
        lock (_gate)
        {
            try
            {
                if (!EnsureConnected()) return false;
                _w!.Write(VolumeService.OpQueryJournal);
                _w.Write(create);
                _w.Flush();
                bool ok = _r!.ReadBoolean();
                info = new UsnJournalInfo(_r.ReadUInt64(), _r.ReadInt64(), _r.ReadInt64(), _r.ReadInt64(), _r.ReadInt64(), _r.ReadUInt64(), _r.ReadUInt64());
                return ok;
            }
            catch (Exception) { Close(); return false; }
        }
    }

    public VolumeRecords ReadRecords(long highUsn, CancellationToken ct, Action<int> progress, out string method)
    {
        lock (_gate)
        {
            if (!EnsureConnected()) throw new IOException("The Rock Preview service isn't running.");
            using var cancel = ct.Register(Close);
            try
            {
                _w!.Write(VolumeService.OpReadRecords);
                _w.Write(highUsn);
                _w.Flush();
                while (true)
                {
                    byte reply = _r!.ReadByte();
                    if (reply == VolumeService.ReplyProgress) { progress(_r.ReadInt32()); continue; }
                    if (reply == VolumeService.ReplyError) throw new IOException("Rock Preview service: " + _r.ReadString());
                    if (reply != VolumeService.ReplyOk) throw new InvalidDataException("unexpected reply from the Rock Preview service");
                    method = _r.ReadString();
                    return VolumeRecords.ReadFrom(_r);
                }
            }
            catch (Exception) when (ct.IsCancellationRequested) { throw new OperationCanceledException(ct); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or EndOfStreamException) { Close(); throw; }
        }
    }

    public int ReadJournal(ulong journalId, long start, byte* buffer, int size)
    {
        size = Math.Min(size, VolumeService.MaxJournalRead);
        lock (_gate)
        {
            try
            {
                if (!EnsureConnected()) return -ErrorBrokenPipe;
                _w!.Write(VolumeService.OpReadJournal);
                _w.Write(journalId);
                _w.Write(start);
                _w.Write(size);
                _w.Flush();
                int ret = _r!.ReadInt32();
                if (ret <= 0) return ret;
                if (ret > size) { Close(); return -ErrorInvalidData; }
                var into = new Span<byte>(buffer, ret);
                while (into.Length > 0)
                {
                    int got = _r.Read(into);
                    if (got <= 0) throw new EndOfStreamException();
                    into = into[got..];
                }
                return ret;
            }
            catch (Exception) { Close(); return -ErrorBrokenPipe; }
        }
    }

    void Close()
    {
        try { _r?.Dispose(); _w?.Dispose(); _stream?.Dispose(); _pipe?.Dispose(); } catch (Exception) { }
        _r = null;
        _w = null;
        _stream = null;
        _pipe = null;
    }

    public void Dispose()
    {
        lock (_gate) Close();
    }
}
