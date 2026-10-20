using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using Anything.Core.Indexing;

namespace Anything.Tests.Indexing;

/// <summary>
/// Safely Remove while the service reads a USB drive's records: the app stopping the read (or hanging up) must make the
/// service close the volume at once, mid-read, and the app must hear back only once the volume is closed, so the
/// drive's handles are gone when Windows checks. A test pipe and a fake volume stand in for the service's real ones.
/// </summary>
public sealed class VolumeServiceStopTests
{
    /// <summary>
    /// A volume read in chunks until stopped, like the MFT reader: progress between chunks, none while one is read, and
    /// a chunk under way always runs to its end (a ReadFile can't be called back; it is what keeps the handle open).
    /// </summary>
    sealed unsafe class SlowVolume : IVolumeSource
    {
        public int ChunkMs = 400;
        public Func<VolumeRecords>? Records; // set: returns these instead of reading until stopped
        public readonly ManualResetEventSlim Reading = new();
        public readonly AutoResetEvent ChunkStarted = new(false);
        public long DisposedAt, ReturnedAt; // Stopwatch timestamps, 0 = not yet
        readonly ManualResetEventSlim _disposed = new();

        public string Kind => "local";

        public bool TryQueryJournal(bool create, out UsnJournalInfo info)
        {
            info = default;
            return false;
        }

        public VolumeRecords ReadRecords(long highUsn, CancellationToken ct, Action<int> progress, out string method)
        {
            method = "MFT";
            Reading.Set();
            if (Records != null) return Records();
            for (int n = 0; !ct.IsCancellationRequested && !_disposed.IsSet; n += 1000)
            {
                progress(n);
                ChunkStarted.Set();
                Thread.Sleep(ChunkMs);
            }
            Interlocked.Exchange(ref ReturnedAt, Stopwatch.GetTimestamp());
            throw new OperationCanceledException(ct);
        }

        /// <summary>Waits until a chunk has just started (after the first, so progress is flowing): a stop now finds a whole chunk ahead.</summary>
        public void AtChunkStart()
        {
            Assert.True(Reading.Wait(5000));
            Assert.True(ChunkStarted.WaitOne(5000) && ChunkStarted.WaitOne(5000), "the read never got going");
        }

        public int ReadJournal(ulong journalId, long start, byte* buffer, int size) => -1;

        public void Dispose()
        {
            Interlocked.CompareExchange(ref DisposedAt, Stopwatch.GetTimestamp(), 0);
            _disposed.Set();
        }
    }

    static VolumeRecords Sample()
    {
        var r = new VolumeRecords();
        r.EnsureRoot();
        r.SetFromUsn(16, VolumeRecords.RootRecord, "a.txt", FileAttributes.Archive);
        return r;
    }

    static double Ms(long from, long to) => (to - from) * 1000.0 / Stopwatch.Frequency;

    static (NamedPipeServerStream Server, NamedPipeClientStream Client) Connect()
    {
        string name = "RockPreview.Tests." + Guid.NewGuid().ToString("N");
        var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous);
        var accepted = server.WaitForConnectionAsync();
        client.Connect(5000);
        accepted.GetAwaiter().GetResult();
        return (server, client);
    }

    /// <summary>The service's side of one OpReadRecords; with <paramref name="thenNextOp"/>, also the request after it.</summary>
    static Task<(bool Open, int NextOp)> Serve(NamedPipeServerStream server, IVolumeSource volume, bool thenNextOp = false) => Task.Run(() =>
    {
        var r = new BinaryReader(server, Encoding.UTF8, leaveOpen: true);
        var w = new BinaryWriter(new BufferedStream(server, 1 << 20), Encoding.UTF8, leaveOpen: true);
        if (server.ReadByte() != VolumeService.OpReadRecords) throw new InvalidDataException("expected OpReadRecords");
        bool open = VolumeService.ReadRecords(server, w, volume, r.ReadInt64(), CancellationToken.None, out int next);
        int op = open && thenNextOp ? next >= 0 ? next : server.ReadByte() : next;
        server.Dispose();
        return (open, op);
    });

    static void Request(Stream client)
    {
        var w = new BinaryWriter(client, Encoding.UTF8, leaveOpen: true);
        w.Write(VolumeService.OpReadRecords);
        w.Write(0L);
        w.Flush();
    }

    /// <summary>Reads the reply to OpReadRecords as the app does.</summary>
    static VolumeRecords Reply(Stream client)
    {
        var r = new BinaryReader(client, Encoding.UTF8, leaveOpen: true);
        while (true)
        {
            byte reply = r.ReadByte();
            if (reply == VolumeService.ReplyProgress) { r.ReadInt32(); continue; }
            Assert.Equal(VolumeService.ReplyOk, reply);
            Assert.Equal("MFT", r.ReadString());
            return VolumeRecords.ReadFrom(r);
        }
    }

    static void AssertStopped(SlowVolume volume, long stoppedAt, long heardAt)
    {
        Assert.True(volume.DisposedAt != 0, "the service never closed the volume");
        // At once: not when the chunk under way is done (a service that only sees the stop when it next reports
        // progress closes it a whole chunk later).
        Assert.True(Ms(stoppedAt, volume.DisposedAt) < volume.ChunkMs / 3, $"closed {Ms(stoppedAt, volume.DisposedAt):F0} ms after the stop");
        Assert.True(volume.ReturnedAt != 0 && volume.ReturnedAt <= heardAt, "the app heard back before the read let go of the volume");
    }

    [Fact]
    public async Task Cancelled_read_closes_the_volume_at_once_and_returns_once_it_is_closed()
    {
        var (server, client) = Connect();
        var volume = new SlowVolume();
        var serving = Serve(server, volume);
        using var source = ServiceVolumeSource.ForConnectedPipe("T:", client);
        using var cts = new CancellationTokenSource();
        int reports = 0;
        var reading = Task.Run(() => source.ReadRecords(0, cts.Token, _ => Interlocked.Increment(ref reports), out _));
        Assert.True(DriveIndexingTestSupport.WaitFor(() => Volatile.Read(ref reports) > 0), "no progress came through");
        volume.AtChunkStart();

        long stoppedAt = Stopwatch.GetTimestamp();
        cts.Cancel();
        Assert.True(((IAsyncResult)reading).AsyncWaitHandle.WaitOne(5000), "the read never ended");
        long heardAt = Stopwatch.GetTimestamp();
        Assert.ThrowsAny<OperationCanceledException>(() => reading.GetAwaiter().GetResult());
        AssertStopped(volume, stoppedAt, heardAt);
        Assert.True(Ms(stoppedAt, heardAt) < 1400, $"took {Ms(stoppedAt, heardAt):F0} ms (cut off, not stopped)");
        Assert.False((await serving.WaitAsync(TimeSpan.FromSeconds(5))).Open);

        var sw = Stopwatch.StartNew();
        source.Dispose(); // nothing left to close
        Assert.True(sw.ElapsedMilliseconds < 1000, $"dispose took {sw.ElapsedMilliseconds} ms");
    }

    [Fact]
    public async Task Disposing_during_a_read_stops_it_and_returns_once_the_volume_is_closed()
    {
        var (server, client) = Connect();
        var volume = new SlowVolume();
        var serving = Serve(server, volume);
        var source = ServiceVolumeSource.ForConnectedPipe("T:", client);
        var reading = Task.Run(() => source.ReadRecords(0, CancellationToken.None, _ => { }, out _));
        volume.AtChunkStart();

        long stoppedAt = Stopwatch.GetTimestamp();
        source.Dispose();
        long heardAt = Stopwatch.GetTimestamp();
        AssertStopped(volume, stoppedAt, heardAt);
        Assert.True(Ms(stoppedAt, heardAt) < 1400, $"dispose took {Ms(stoppedAt, heardAt):F0} ms");
        Assert.True(((IAsyncResult)reading).AsyncWaitHandle.WaitOne(5000));
        Assert.ThrowsAny<OperationCanceledException>(() => reading.GetAwaiter().GetResult());
        Assert.False((await serving.WaitAsync(TimeSpan.FromSeconds(5))).Open);
    }

    [Fact]
    public async Task App_hanging_up_mid_read_closes_the_volume_at_once()
    {
        var (server, client) = Connect();
        var volume = new SlowVolume();
        var serving = Serve(server, volume);
        Request(client);
        // Takes the progress reports as the app does (the pipe has no buffer: an unread report holds up the service).
        _ = Task.Run(() =>
        {
            try { while (client.ReadByte() >= 0) { } }
            catch (Exception) { }
        });
        volume.AtChunkStart();

        long stoppedAt = Stopwatch.GetTimestamp();
        client.Dispose();
        Assert.True(DriveIndexingTestSupport.WaitFor(() => Interlocked.Read(ref volume.DisposedAt) != 0, 2000));
        Assert.True(Ms(stoppedAt, volume.DisposedAt) < volume.ChunkMs / 3, $"closed {Ms(stoppedAt, volume.DisposedAt):F0} ms after the hang-up");
        Assert.False((await serving.WaitAsync(TimeSpan.FromSeconds(5))).Open);
    }

    [Fact]
    public async Task Finished_read_replies_and_the_next_request_still_arrives()
    {
        var (server, client) = Connect();
        var volume = new SlowVolume { Records = Sample };
        var serving = Serve(server, volume, thenNextOp: true);
        Request(client);
        Assert.Equal("a.txt", Reply(client).NameOf(16));
        client.WriteByte(VolumeService.OpQueryJournal);

        var (open, next) = await serving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(open);
        Assert.Equal(VolumeService.OpQueryJournal, next);
        Assert.Equal(0, volume.DisposedAt);
        client.Dispose();
    }

    [Fact]
    public async Task Request_sent_during_the_read_is_kept_for_after_it()
    {
        var (server, client) = Connect();
        var volume = new SlowVolume { Records = () => { Thread.Sleep(300); return Sample(); } };
        var serving = Serve(server, volume, thenNextOp: true);
        Request(client);
        Assert.True(volume.Reading.Wait(5000));
        client.WriteByte(VolumeService.OpQueryJournal);
        Assert.Equal("a.txt", Reply(client).NameOf(16));

        var (open, next) = await serving.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(open);
        Assert.Equal(VolumeService.OpQueryJournal, next);
        Assert.Equal(0, volume.DisposedAt);
        client.Dispose();
    }

    [Fact]
    public async Task Service_that_ignores_the_stop_is_cut_off_within_a_bound()
    {
        // A service from before OpClose: it keeps reporting progress and reads nothing until its read is done.
        var (server, client) = Connect();
        var serving = Task.Run(() =>
        {
            try
            {
                var w = new BinaryWriter(server, Encoding.UTF8, leaveOpen: true);
                server.ReadByte();
                new BinaryReader(server, Encoding.UTF8, leaveOpen: true).ReadInt64();
                for (int n = 0; ; n++)
                {
                    w.Write(VolumeService.ReplyProgress);
                    w.Write(n);
                    w.Flush();
                    Thread.Sleep(50);
                }
            }
            catch (IOException) { }
            catch (ObjectDisposedException) { }
            finally { server.Dispose(); }
        });
        var source = ServiceVolumeSource.ForConnectedPipe("T:", client);
        using var cts = new CancellationTokenSource();
        var reading = Task.Run(() => source.ReadRecords(0, cts.Token, _ => { }, out _));
        Thread.Sleep(200);

        var sw = Stopwatch.StartNew();
        cts.Cancel();
        Assert.True(((IAsyncResult)reading).AsyncWaitHandle.WaitOne(5000), "the read never ended");
        Assert.ThrowsAny<OperationCanceledException>(() => reading.GetAwaiter().GetResult());
        Assert.True(sw.ElapsedMilliseconds < 2500, $"took {sw.ElapsedMilliseconds} ms");
        source.Dispose();
        Assert.True(sw.ElapsedMilliseconds < 2800, $"took {sw.ElapsedMilliseconds} ms");
        await serving.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
