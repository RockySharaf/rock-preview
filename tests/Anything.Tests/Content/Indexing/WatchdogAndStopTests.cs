using System.Diagnostics;
using System.IO.Pipes;
using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Indexing;

namespace Anything.Tests.Content.Indexing;

/// <summary>The reader lanes' watchdog: a hung read is freed (CancelSynchronousIo) or abandoned, and counted per lane and per share.</summary>
[Collection(TimingCollection.Name)]
public class WatchdogTests
{
    static readonly ExtractLimits Quick = new(MaxMilliseconds: 40);

    static ReadJob Job(string path, bool network = false, string share = "path:C:")
        => new()
        {
            FileId = 1, Generation = 0, Key = ContentKeys.Of("path:", path), Volume = 0, RelativePath = path, RootName = "C:", RootPath = "C:",
            Share = share, Network = network,
            Request = new ExtractRequest(path, ContentKind.Text, 10, 10, network, false, long.MaxValue, Quick),
        };

    static ReadScheduler Scheduler(IContentSource source, Func<string, CancellationToken, int>? probe = null) => new(source, CrashMarker.InMemory(), new ReadSchedulerOptions
    {
        WatchdogPeriodMs = 15,
        CancelGraceMs = 20,
        AbandonGraceMs = 120,
        ProbeRoot = probe ?? ((_, _) => 0),
    });

    /// <summary>Reads that never come back in managed code (a loop with no budget check): only abandoning helps.</summary>
    sealed class HangingSource : IContentSource
    {
        public readonly ManualResetEventSlim Release = new(false);
        public int Reads;
        public ContentRead Read(ExtractRequest request, CancellationToken ct)
        {
            Interlocked.Increment(ref Reads);
            Release.Wait();
            return new ContentRead(ExtractedDoc.Failed(request.Kind, DocStatus.Ok), 10, 10, false, false);
        }
        public bool TryStat(string fullPath, out long size, out long modifiedUtc, out FileAttributes attributes) { size = 10; modifiedUtc = 10; attributes = 0; return true; }
    }

    /// <summary>A read stuck in a synchronous ReadFile on a pipe nobody writes to: CancelSynchronousIo frees it.</summary>
    sealed class StuckIoSource : IContentSource
    {
        public ContentRead Read(ExtractRequest request, CancellationToken ct)
        {
            using var server = new AnonymousPipeServerStream(PipeDirection.In);
            using var client = new AnonymousPipeClientStream(PipeDirection.Out, server.ClientSafePipeHandle);
            var buffer = new byte[16];
            try { if (server.Read(buffer, 0, buffer.Length) == 0) return new ContentRead(ExtractedDoc.Failed(request.Kind, DocStatus.Ok), 10, 10, Retry: true, false); }
            catch (IOException) { return new ContentRead(ExtractedDoc.Failed(request.Kind, DocStatus.Ok), 10, 10, Retry: true, false); }
            catch (OperationCanceledException) { return new ContentRead(ExtractedDoc.Failed(request.Kind, DocStatus.Ok), 10, 10, Retry: true, false); }
            return new ContentRead(ExtractedDoc.Failed(request.Kind, DocStatus.Ok), 10, 10, false, false);
        }
        public bool TryStat(string fullPath, out long size, out long modifiedUtc, out FileAttributes attributes) { size = 10; modifiedUtc = 10; attributes = 0; return true; }
    }

    static ReadOutcome Next(ReadScheduler s, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < timeoutMs)
        {
            if (s.Results.TryDequeue(out var o)) return o;
            Thread.Sleep(5);
        }
        throw new TimeoutException("no result");
    }

    [Fact]
    public void A_read_stuck_in_synchronous_io_is_freed_and_recorded_timed_out_when_its_root_still_answers()
    {
        using var s = Scheduler(new StuckIoSource(), probe: (_, _) => 0);
        Assert.True(s.Start(Job(@"C:\a.txt")));
        var o = Next(s);
        Assert.Equal(ReadResultKind.TimedOut, o.Kind);
        Assert.False(o.Abandoned);
        Assert.Equal(0, s.AbandonedThreads);
        Assert.True(s.CanStart(network: false));
    }

    [Fact]
    public void A_read_stuck_on_a_share_that_went_away_is_freed_and_comes_back_offline()
    {
        using var s = Scheduler(new StuckIoSource(), probe: (_, _) => 53 /* ERROR_BAD_NETPATH */);
        Assert.True(s.Start(Job(@"\\nas\jobs\a.pdf", network: true, share: @"unc:\\NAS\JOBS")));
        Assert.Equal(ReadResultKind.Offline, Next(s).Kind);
    }

    [Fact]
    public void Two_hung_threads_stop_the_local_lane_and_each_is_recorded_timed_out()
    {
        var source = new HangingSource();
        using var s = Scheduler(source);
        try
        {
            Assert.True(s.Start(Job(@"C:\one.txt")));
            var first = Next(s);
            Assert.Equal((ReadResultKind.TimedOut, true), (first.Kind, first.Abandoned));
            Assert.Null(s.LocalStopped);
            Assert.True(s.Start(Job(@"C:\two.txt"))); // a fresh thread took the lane
            var second = Next(s);
            Assert.True(second.Abandoned);
            Assert.Equal(2, s.AbandonedThreads);
            Assert.Contains("two.txt", s.LocalStopped);
            Assert.False(s.Start(Job(@"C:\three.txt")));
            Assert.False(s.CanStart(network: false));
            Assert.True(s.CanStart(network: true)); // a local hang never stops the network lane
        }
        finally { source.Release.Set(); }
    }

    [Fact]
    public void Two_hung_threads_on_one_share_stop_that_share_not_the_lane()
    {
        var source = new HangingSource();
        using var s = Scheduler(source);
        try
        {
            for (int i = 0; i < 2; i++)
            {
                Assert.True(s.Start(Job($@"\\a\s\{i}.pdf", network: true, share: "unc:A")));
                Assert.True(Next(s).Abandoned);
            }
            Assert.True(s.IsStopped("unc:A"));
            Assert.False(s.ShareReady("unc:A", 0));
            Assert.Null(s.NetworkStopped);
            Assert.True(s.ShareReady("unc:B", 0));
            for (int i = 0; i < 2; i++)
            {
                Assert.True(s.Start(Job($@"\\b\s\{i}.pdf", network: true, share: "unc:B")));
                Assert.True(Next(s).Abandoned);
            }
            Assert.NotNull(s.NetworkStopped); // four in all stop the network lane
        }
        finally { source.Release.Set(); }
    }

    [Fact]
    public void A_late_result_from_an_abandoned_thread_is_dropped()
    {
        var source = new HangingSource();
        using var s = Scheduler(source);
        Assert.True(s.Start(Job(@"C:\one.txt")));
        Assert.True(Next(s).Abandoned);
        source.Release.Set();
        Thread.Sleep(200);
        Assert.True(s.Results.IsEmpty);
    }

    [Fact]
    public void The_share_throttle_holds_a_share_back_until_its_average_is_back_at_the_limit()
    {
        using var s = Scheduler(new HangingSource());
        s.NetworkBytesPerSecond = 1 << 20;
        s.Charge("unc:A", 4 << 20, nowMs: 0); // 4 s worth at 1 MB/s
        Assert.False(s.ShareReady("unc:A", 1000));
        Assert.True(s.ShareReady("unc:A", 3000)); // one second of burst allowed
        Assert.True(s.ShareReady("unc:B", 0));
        s.BackOff("unc:B", nowMs: 0);
        Assert.False(s.ShareReady("unc:B", 299_000));
        Assert.True(s.ShareReady("unc:B", 300_000));
    }
}

/// <summary>Stop, Checkpoint and commands after Stop, on real threads.</summary>
[Collection(TimingCollection.Name)]
public class StopTests
{
    static (ContentIndex Content, FakeNames Names, FakeFiles Files, FakeStore Store) Threaded(FakeFiles files, ContentIndexOptions? options = null,
        Action<FileIndex, int>? more = null)
    {
        var index = new FileIndex();
        int root = index.AddRoot("C:");
        int users = index.Add("Users", root, FileAttributes.Directory, -1, 0, 0);
        int me = index.Add("Me", users, FileAttributes.Directory, -1, 0, 0);
        for (int i = 0; i < 3; i++)
        {
            files.Put($@"C:\Users\Me\f{i}.txt", 15, 100 + i, "words " + i);
            index.Add($"f{i}.txt", me, FileAttributes.Archive, 15, 100 + i, 0);
        }
        more?.Invoke(index, me);
        var names = new FakeNames(index);
        var store = new FakeStore();
        var content = new ContentIndex(() => store.Reopen(), files, new FakeGate(), NoPowerPolicy.Instance, (options ?? new ContentIndexOptions()) with
        {
            VolumeResolver = Harness.Resolve, RecentItems = () => [],
        });
        content.Start(names, Harness.DefaultSettings());
        return (content, names, files, store);
    }

    static bool WaitFor(Func<bool> condition, int ms = 10_000)
    {
        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < ms)
        {
            if (condition()) return true;
            Thread.Sleep(10);
        }
        return condition();
    }

    [Fact]
    public void Stop_returns_within_its_wait_while_a_read_hangs_and_is_final()
    {
        var gate = new ManualResetEventSlim(false);
        var started = new ManualResetEventSlim(false);
        var files = new FakeFiles { OnRead = _ => { started.Set(); gate.Wait(); return null; } };
        var (content, _, _, store) = Threaded(files);
        try
        {
            Assert.True(started.Wait(10_000), "no read started");
            var sw = Stopwatch.StartNew();
            content.Stop(TimeSpan.FromMilliseconds(800));
            Assert.True(sw.ElapsedMilliseconds < 1500, $"Stop took {sw.ElapsedMilliseconds} ms");
            sw.Restart();
            content.Stop(TimeSpan.FromSeconds(5));
            content.Dispose();
            Assert.True(sw.ElapsedMilliseconds < 100, "a second Stop must return at once");
            // Commands after Stop are ignored, never thrown.
            content.Apply(Harness.DefaultSettings());
            content.Pause(null);
            content.Resume();
            content.Rebuild();
            content.RetryProblems();
            content.Checkpoint(TimeSpan.FromMilliseconds(100));
            Assert.Equal(ContentPhase.Stopped, content.Status.Phase);
            Assert.True(store.Disposed);
        }
        finally { gate.Set(); }
    }

    [Fact]
    public async Task Two_stops_at_once_both_return()
    {
        var gate = new ManualResetEventSlim(false);
        var files = new FakeFiles { OnRead = _ => { gate.Wait(); return null; } };
        var (content, _, _, _) = Threaded(files);
        try
        {
            Thread.Sleep(200);
            var a = Task.Run(() => content.Stop(TimeSpan.FromMilliseconds(500)));
            var b = Task.Run(() => content.Stop(TimeSpan.FromMilliseconds(500)));
            await Task.WhenAll(a, b).WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { gate.Set(); }
    }

    [Fact]
    public void Checkpoint_commits_and_keeps_running()
    {
        using var started = new ManualResetEventSlim(false);
        using var release = new ManualResetEventSlim(false);
        // The oldest file is read last and takes long, so the batch (an hour's) holds the other three meanwhile: only the
        // checkpoint commits them.
        var files = new FakeFiles
        {
            OnRead = r =>
            {
                if (!r.FullPath.EndsWith("slow.txt", StringComparison.Ordinal)) return null;
                started.Set();
                release.Wait(10_000);
                return null;
            },
        };
        var (content, names, _, store) = Threaded(files, new ContentIndexOptions(CommitAfter: TimeSpan.FromHours(1)), (index, me) =>
        {
            files.Put(@"C:\Users\Me\slow.txt", 15, 50, "slow words");
            index.Add("slow.txt", me, FileAttributes.Archive, 15, 50, 0);
        });
        try
        {
            Assert.True(started.Wait(10_000), "the slow read didn't start");
            // The three are read and in the batch (the slow one is the only file still waiting).
            Assert.True(WaitFor(() => content.Status.Done == 3), "the first pass didn't read");
            Assert.Empty(store.Live());
            content.Checkpoint(TimeSpan.FromSeconds(2));
            Assert.Equal(3, store.Live().Count);
            Assert.NotEqual(ContentPhase.Stopped, content.Status.Phase);
            release.Set();
            Assert.True(WaitFor(() => store.Live().Count == 4, 15_000), "the slow file wasn't saved after the checkpoint");
            // Still following: a new file is read (after its 5 s settle).
            var index = names.Index;
            files.Put(@"C:\Users\Me\late.txt", 20, 999, "late words");
            index.Add("late.txt", 2, FileAttributes.Archive, 20, 999, 0);
            index.RaiseChanged();
            Assert.True(WaitFor(() => store.Live().Count == 5, 15_000), "nothing read after the checkpoint");
        }
        finally
        {
            release.Set();
            content.Stop(TimeSpan.FromSeconds(1));
        }
    }

    [Fact]
    public void Events_fire_on_progress_and_after_a_commit_and_a_throwing_handler_does_no_harm()
    {
        var files = new FakeFiles();
        int status = 0, changed = 0;
        var (content, _, _, store) = Threaded(files, new ContentIndexOptions(CommitAfter: TimeSpan.FromMilliseconds(100)));
        content.StatusChanged += () => throw new InvalidOperationException("a bad handler");
        content.StatusChanged += () => Interlocked.Increment(ref status);
        content.ContentChanged += () => Interlocked.Increment(ref changed);
        try
        {
            Assert.True(WaitFor(() => store.Live().Count == 3));
            Assert.True(WaitFor(() => Volatile.Read(ref changed) > 0, 6000), "no ContentChanged within the 3 s debounce");
            Assert.True(Volatile.Read(ref status) > 0);
            Assert.Equal(ContentPhase.UpToDate, content.Status.Phase);
        }
        finally { content.Stop(TimeSpan.FromSeconds(1)); }
    }
}
