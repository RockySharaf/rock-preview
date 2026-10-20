using System.Diagnostics.CodeAnalysis;
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
    // How long a stopped read may take to end (the service closing the volume) before the connection is cut.
    const int StopWaitMs = 1500;
    static readonly byte[] StopRequest = [VolumeService.OpClose];

    readonly string _drive;
    readonly object _gate = new();
    NamedPipeClientStream? _pipe;
    BufferedStream? _stream;
    BinaryReader? _r;
    BinaryWriter? _w;
    // Set by Dispose: from then on every operation fails at once. Reconnecting would make the service open the drive
    // again, which is what stops it from being ejected.
    volatile bool _disposed;
    // The connection a ReadRecords is waiting on (it holds _gate meanwhile), and whether it was asked to stop.
    volatile NamedPipeClientStream? _reading;
    int _stopping;
    Timer? _cut;

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
        // Asynchronous, so a read can be asked to stop while another thread waits on the pipe (on a synchronous pipe
        // the request would queue behind that wait).
        var pipe = new NamedPipeClientStream(".", VolumeService.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Identification);
        try
        {
            pipe.Connect(timeoutMs);
            if (!IsServiceOwned(pipe)) { pipe.Dispose(); return false; }
            Attach(pipe);
            _w.Write(VolumeService.ProtocolVersion);
            _w.Write(_drive);
            _w.Flush();
            // Disposed meanwhile (Dispose doesn't wait long for the lock): don't leave the drive open in the service.
            if (_r.ReadByte() == VolumeService.ReplyOk && !_disposed) return true;
        }
        catch (Exception) { }
        Close();
        return false;
    }

    [MemberNotNull(nameof(_pipe), nameof(_stream), nameof(_r), nameof(_w))]
    void Attach(NamedPipeClientStream pipe)
    {
        _pipe = pipe;
        _stream = new BufferedStream(pipe, 1 << 20);
        _r = new BinaryReader(_stream, Encoding.UTF8, leaveOpen: true);
        _w = new BinaryWriter(_stream, Encoding.UTF8, leaveOpen: true);
    }

    /// <summary>For tests: a source on an asynchronous pipe already connected to a stand-in for the service, past the handshake.</summary>
    internal static ServiceVolumeSource ForConnectedPipe(string drive, NamedPipeClientStream pipe)
    {
        var source = new ServiceVolumeSource(drive);
        source.Attach(pipe);
        return source;
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

    bool EnsureConnected() => !_disposed && (_pipe is { IsConnected: true } || Connect(2000));

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
            ct.ThrowIfCancellationRequested();
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!EnsureConnected()) throw new IOException("The Ultimate File Explorer service isn't running.");
            var pipe = _pipe!;
            Volatile.Write(ref _stopping, 0);
            _reading = pipe;
            var cancel = default(CancellationTokenRegistration);
            try
            {
                _w!.Write(VolumeService.OpReadRecords);
                _w.Write(highUsn);
                _w.Flush();
                // Cancelled (an eject, the root dropped): stopped in-band, so the drive is free once the read ends.
                cancel = ct.Register(() => StopRead(pipe));
                while (true)
                {
                    byte reply = _r!.ReadByte();
                    if (reply == VolumeService.ReplyProgress) { progress(_r.ReadInt32()); continue; }
                    if (reply == VolumeService.ReplyError) throw new IOException("Ultimate File Explorer service: " + _r.ReadString());
                    if (reply != VolumeService.ReplyOk) throw new InvalidDataException("unexpected reply from the Ultimate File Explorer service");
                    method = _r.ReadString();
                    return VolumeRecords.ReadFrom(_r);
                }
            }
            // Stopped: the service closed the volume and hung up (or was cut off).
            catch (Exception) when (ct.IsCancellationRequested || _disposed) { Close(); throw new OperationCanceledException(ct); }
            catch (Exception ex) when (ex is IOException or ObjectDisposedException or EndOfStreamException) { Close(); throw; }
            finally
            {
                cancel.Dispose(); // waits for a StopRead under way
                _reading = null;
                Interlocked.Exchange(ref _cut, null)?.Dispose();
                // Asked to stop just as the reply came in: the service would take the stop for the next request. Wait
                // (briefly) for its answer to the stop, the ack or the end of the stream, which come only once the volume
                // is closed, so an eject waiting on this isn't refused for a handle still open.
                if (Volatile.Read(ref _stopping) != 0)
                {
                    try { _stream?.ReadAsync(new byte[1]).AsTask().Wait(StopWaitMs); } catch (Exception) { }
                    Close();
                }
            }
        }
    }

    /// <summary>
    /// Stops a ReadRecords under way: asks the service in-band, which closes the volume at once and hangs up, ending the
    /// read only once the volume is closed. A service that doesn't (one from before OpClose, or one stuck reading a dying
    /// drive) is cut off after a moment; it then notices at its next progress report, as before.
    /// </summary>
    void StopRead(NamedPipeClientStream pipe)
    {
        if (Interlocked.Exchange(ref _stopping, 1) != 0) return;
        // Not waited for: an older service reads it only after the whole read.
        try { pipe.WriteAsync(StopRequest).AsTask().ContinueWith(t => _ = t.Exception, TaskContinuationOptions.OnlyOnFaulted); }
        catch (Exception) { }
        var cut = new Timer(static p => { try { ((IDisposable)p!).Dispose(); } catch (Exception) { } }, pipe, StopWaitMs, Timeout.Infinite);
        Interlocked.Exchange(ref _cut, cut)?.Dispose();
    }

    public int ReadJournal(ulong journalId, long start, byte* buffer, int size)
    {
        size = Math.Min(size, VolumeService.MaxJournalRead);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
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

    /// <summary>
    /// Asks the service to close the volume and waits (briefly) until it has, so the drive can be ejected right
    /// after. A read still running is stopped the same way (<see cref="StopRead"/>) and waited for; anything else
    /// that holds on is cut off, which makes the service close the volume too. Never reconnects: a connection a
    /// stopped read already ended stays closed.
    /// </summary>
    public void Dispose()
    {
        _disposed = true;
        // A read under way holds the gate until it ends, which once stopped is when the service has closed the volume.
        var reading = _reading;
        if (reading != null) StopRead(reading);
        bool locked = Monitor.TryEnter(_gate, reading != null ? StopWaitMs + 200 : 200);
        if (!locked && reading == null && _reading is { } started)
        {
            StopRead(started);
            locked = Monitor.TryEnter(_gate, StopWaitMs + 200);
        }
        if (!locked)
        {
            Close();
            return;
        }
        try
        {
            if (_pipe is { IsConnected: true } && _w != null && _r != null)
            {
                try
                {
                    _w.Write(VolumeService.OpClose);
                    _w.Flush();
                    var reply = _stream!.ReadAsync(new byte[1]).AsTask();
                    reply.Wait(1500); // the ack, or the end of the stream from a service that predates OpClose
                }
                catch (Exception) { }
            }
            Close();
        }
        finally
        {
            Interlocked.Exchange(ref _cut, null)?.Dispose();
            Monitor.Exit(_gate);
        }
    }
}
