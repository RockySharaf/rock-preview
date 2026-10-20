using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Anything.Core;
using Anything.Core.Indexing;
using static Anything.Tests.Indexing.DriveIndexingTestSupport;

namespace Anything.Tests.Indexing;

/// <summary>
/// An NTFS root's scan and journal monitor against a fake volume: status after failures, scans the manager gave up
/// on, and releasing the drive for an eject (nothing may reopen it afterwards).
/// </summary>
public sealed class NtfsVolumeIndexerLifecycleTests
{
    sealed class Host : IIndexerHost
    {
        public FileIndex Index { get; } = new();
        public ExclusionFilter Filter => ExclusionFilter.None;
        public void MarkChanged() { }
        public void RequestRescan(RootIndexer root, string reason) { }
        public void ReportProgress(RootIndexer root, long objects) { }
    }

    sealed unsafe class FakeVolume(string kind = "local") : IVolumeSource
    {
        public Func<CancellationToken, VolumeRecords> Records = _ => Sample();
        public bool ThrowDisposedOnJournalRead;
        public int JournalReads, RecordReads;
        public volatile bool Disposed;
        /// <summary>Journal records the next read returns (then <see cref="PendingNextUsn"/>), and where reads started.</summary>
        public byte[]? PendingJournal;
        public long PendingNextUsn;
        public readonly List<(ulong JournalId, long Start)> JournalStarts = [];

        public string Kind => kind;

        public bool TryQueryJournal(bool create, out UsnJournalInfo info)
        {
            info = new UsnJournalInfo(7, 0, 100, 0, long.MaxValue, 1 << 20, 1 << 16);
            return true;
        }

        public VolumeRecords ReadRecords(long highUsn, CancellationToken ct, Action<int> progress, out string method)
        {
            Interlocked.Increment(ref RecordReads);
            method = "MFT";
            return Records(ct);
        }

        public int ReadJournal(ulong journalId, long start, byte* buffer, int size)
        {
            Interlocked.Increment(ref JournalReads);
            if (ThrowDisposedOnJournalRead || Disposed) throw new ObjectDisposedException("volume");
            lock (JournalStarts) JournalStarts.Add((journalId, start));
            var pending = Interlocked.Exchange(ref PendingJournal, null);
            if (pending != null)
            {
                *(long*)buffer = PendingNextUsn;
                pending.CopyTo(new Span<byte>(buffer + 8, size - 8));
                return 8 + pending.Length;
            }
            *(long*)buffer = start; // nothing new
            return 8;
        }

        public void Dispose() => Disposed = true;
    }

    static VolumeRecords Sample()
    {
        var r = new VolumeRecords();
        r.EnsureRoot();
        r.SetFromUsn(16, VolumeRecords.RootRecord, "a.txt", FileAttributes.Archive);
        return r;
    }

    [Fact]
    public void Failed_scan_puts_the_status_back_and_says_why()
    {
        var host = new Host();
        var volume = new FakeVolume { Records = _ => throw new IOException("the drive stopped answering") };
        using var root = new NtfsVolumeIndexer(host, "T:", volume);

        Assert.Throws<IOException>(() => root.Scan(CancellationToken.None));
        Assert.Equal(RootStatus.Waiting, root.Status); // nothing in the index yet
        Assert.Equal("the drive stopped answering", root.Detail);

        volume.Records = _ => Sample();
        root.Scan(CancellationToken.None);
        Assert.Equal(RootStatus.Ready, root.Status);
        Assert.Null(root.Detail);

        volume.Records = _ => throw new IOException("again");
        Assert.Throws<IOException>(() => root.Scan(CancellationToken.None));
        Assert.Equal(RootStatus.Ready, root.Status); // the previous scan is still searchable
        Assert.Equal("again", root.Detail);
        Assert.Equal([@"T:\", @"T:\a.txt"], Paths(host.Index));
    }

    [Fact]
    public void Scan_finishing_after_the_index_was_cleared_writes_nothing()
    {
        var host = new Host();
        // The manager stopped waiting for this scan and reloaded the index meanwhile.
        var volume = new FakeVolume { Records = _ => { var r = Sample(); host.Index.Clear(); return r; } };
        using var root = new NtfsVolumeIndexer(host, "T:", volume);

        Assert.ThrowsAny<OperationCanceledException>(() => root.Scan(CancellationToken.None));
        Assert.Equal(0, host.Index.LiveCount);
        Assert.Equal(-1, root.RootId);
        Assert.Equal(RootStatus.Waiting, root.Status);
        Assert.Null(root.Detail); // abandoned, not failed
    }

    [Fact]
    public void Build_adds_nothing_when_abandoned_under_the_index_lock()
    {
        var index = new FileIndex();
        var (rootId, _, count) = Sample().BuildInto(index, "T:", ExclusionFilter.None, null, abandoned: () => true);
        Assert.Equal(-1, rootId);
        Assert.Equal(0, count);
        Assert.Equal(0, index.LiveCount);
    }

    [Theory]
    [InlineData("local")]
    [InlineData("service")]
    public void Release_closes_the_volume_and_monitoring_never_starts_again(string kind)
    {
        var host = new Host();
        var volume = new FakeVolume(kind);
        using var root = new NtfsVolumeIndexer(host, "T:", volume);
        root.Scan(CancellationToken.None);
        root.StartMonitoring();
        Assert.True(WaitFor(() => Volatile.Read(ref volume.JournalReads) >= 2), "the monitor never polled the journal");

        var sw = Stopwatch.StartNew();
        root.Release();
        Assert.True(sw.ElapsedMilliseconds < 1000, $"release took {sw.ElapsedMilliseconds} ms");
        Assert.True(volume.Disposed);
        Assert.True(root.Released);

        // The manager finishing a scan (or a rescan request) after the eject released the drive.
        int reads = Volatile.Read(ref volume.JournalReads);
        root.StartMonitoring();
        Thread.Sleep(700);
        Assert.Equal(reads, Volatile.Read(ref volume.JournalReads));
        Assert.Throws<InvalidOperationException>(() => root.Scan(CancellationToken.None));
    }

    [Fact]
    public void Revived_after_a_failed_eject_resumes_the_journal_where_it_stopped()
    {
        var host = new Host();
        var first = new FakeVolume("service");
        using var root = new NtfsVolumeIndexer(host, "T:", first);
        root.Scan(CancellationToken.None); // journal 7, from USN 100
        root.StartMonitoring();
        Assert.True(WaitFor(() => Volatile.Read(ref first.JournalReads) >= 1));

        // Not released: nothing to revive, and the spare source isn't kept.
        var spare = new FakeVolume("service");
        Assert.True(root.Revive(spare));
        Assert.True(spare.Disposed);
        Assert.False(first.Disposed);

        root.Release();
        root.EjectFailed = true;
        root.ReleasedAtTicks = Environment.TickCount64;
        var lifetime = root.Lifetime;
        lifetime.Cancel(); // as the manager does for the eject
        Assert.True(first.Disposed);
        Assert.False(root.Revive(null));
        Assert.True(root.Released);

        // Windows said the eject failed: a new connection, and a file made meanwhile waiting in the journal.
        var second = new FakeVolume("service") { PendingJournal = UsnRecordBytes(30, VolumeRecords.RootRecord, 100, "b.txt", UsnReason.FileCreate | UsnReason.Close), PendingNextUsn = 180 };
        Assert.True(root.Revive(second));
        Assert.False(root.Released);
        Assert.False(root.EjectFailed);
        Assert.Equal(0, root.ReleasedAtTicks);
        Assert.NotSame(lifetime, root.Lifetime);
        Assert.False(root.Lifetime.IsCancellationRequested);

        root.StartMonitoring();
        Assert.True(WaitFor(() => Paths(host.Index).Contains(@"T:\b.txt")), "the journal record was never applied");
        lock (second.JournalStarts) Assert.Equal((7UL, 100L), second.JournalStarts[0]);
        Assert.True(WaitFor(() => { lock (root.Sync) return root.JournalPosition == (7UL, 180L); }));
        Assert.Equal(0, second.RecordReads); // no rescan
        Assert.Equal([@"T:\", @"T:\a.txt", @"T:\b.txt"], Paths(host.Index));
        Assert.False(second.Disposed);

        root.StopMonitoring();
    }

    [Fact]
    public void Released_before_its_first_scan_is_not_revived_and_the_source_stays_the_callers()
    {
        var root = new NtfsVolumeIndexer(new Host(), "T:", new FakeVolume());
        root.Release();
        var source = new FakeVolume();
        Assert.False(root.Revive(source));
        Assert.False(source.Disposed);
        Assert.True(root.Released);
        root.Dispose();
        Assert.False(source.Disposed);
    }

    static byte[] UsnRecordBytes(long record, long parent, long usn, string name, UsnReason reason)
    {
        int len = (0x3C + name.Length * 2 + 7) & ~7;
        var b = new byte[len];
        BitConverter.GetBytes(len).CopyTo(b, 0);
        BitConverter.GetBytes((ushort)2).CopyTo(b, 4);
        BitConverter.GetBytes((ulong)record | 1UL << 48).CopyTo(b, 8);
        BitConverter.GetBytes((ulong)parent | 1UL << 48).CopyTo(b, 16);
        BitConverter.GetBytes(usn).CopyTo(b, 24);
        BitConverter.GetBytes(0L).CopyTo(b, 32);
        BitConverter.GetBytes((uint)reason).CopyTo(b, 40);
        BitConverter.GetBytes((uint)FileAttributes.Archive).CopyTo(b, 52);
        BitConverter.GetBytes((ushort)(name.Length * 2)).CopyTo(b, 56);
        BitConverter.GetBytes((ushort)0x3C).CopyTo(b, 58);
        System.Text.Encoding.Unicode.GetBytes(name).CopyTo(b, 0x3C);
        return b;
    }

    [Fact]
    public void Monitor_ends_quietly_when_the_volume_is_closed_under_it()
    {
        var host = new Host();
        var volume = new FakeVolume();
        using var root = new NtfsVolumeIndexer(host, "T:", volume);
        root.Scan(CancellationToken.None);
        volume.ThrowDisposedOnJournalRead = true;
        // Uncaught, this would take the whole process down (it is a plain thread).
        root.StartMonitoring();
        Assert.True(WaitFor(() => Volatile.Read(ref volume.JournalReads) >= 1));
        Thread.Sleep(600);
        Assert.Equal(1, Volatile.Read(ref volume.JournalReads));
        var sw = Stopwatch.StartNew();
        root.StopMonitoring();
        Assert.True(sw.ElapsedMilliseconds < 1000);
    }

    [Fact]
    public unsafe void Disposed_service_source_fails_at_once_and_never_reconnects()
    {
        // Built unconnected, for a drive the service would refuse anyway: nothing here may reach the service.
        var source = (ServiceVolumeSource)Activator.CreateInstance(typeof(ServiceVolumeSource),
            BindingFlags.Instance | BindingFlags.NonPublic, null, ["1:"], null)!;
        source.Dispose();

        var sw = Stopwatch.StartNew();
        Assert.False(source.TryQueryJournal(create: false, out _)); // a reconnect attempt would wait up to 2 s
        Assert.Throws<ObjectDisposedException>(() => source.ReadRecords(0, CancellationToken.None, _ => { }, out _));
        Assert.Throws<ObjectDisposedException>(() => ReadJournal(source));
        Assert.True(sw.ElapsedMilliseconds < 500, $"took {sw.ElapsedMilliseconds} ms");
        source.Dispose(); // twice is fine
    }

    static unsafe int ReadJournal(IVolumeSource source)
    {
        byte* buffer = (byte*)NativeMemory.Alloc(4096);
        try { return source.ReadJournal(0, 0, buffer, 4096); }
        finally { NativeMemory.Free(buffer); }
    }
}
