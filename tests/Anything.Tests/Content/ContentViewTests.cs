using Anything.Core.Content;

namespace Anything.Tests.Content;

public class ContentViewTests
{
    static ContentView View(out CountingStoreView store)
    {
        store = new CountingStoreView();
        return new ContentView(store, DocIdMap.Empty(1), ContentMode.Documents);
    }

    [Fact]
    public void The_last_release_releases_the_store_once()
    {
        var view = View(out var store);
        Assert.True(view.TryAddRef());
        Assert.Same(view, view.AddRef());
        view.Dispose();
        view.Dispose();
        Assert.Equal(0, store.DisposeCount);
        Assert.False(view.IsReleased);
        view.Dispose();
        Assert.Equal(1, store.DisposeCount);
        Assert.True(view.IsReleased);

        Assert.False(view.TryAddRef());
        Assert.Throws<ObjectDisposedException>(() => view.AddRef());
        view.Dispose(); // one release too many: never releases the store again
        Assert.Equal(1, store.DisposeCount);
        Assert.False(view.TryAddRef());
    }

    [Fact]
    public async Task Acquire_reads_again_when_the_published_view_was_released()
    {
        ContentView? slot = null;
        Assert.Null(ContentSource.Acquire(ref slot));

        var old = View(out _);
        slot = old;
        var lease = ContentSource.Acquire(ref slot);
        Assert.Same(old, lease);
        lease!.Dispose();

        old.Dispose(); // the writer released it but a stale reader still sees it in the slot
        Assert.True(old.IsReleased);
        var fresh = View(out _);
        var reader = Task.Run(() => ContentSource.Acquire(ref slot));
        await Task.Delay(20);
        Volatile.Write(ref slot, fresh);
        Assert.Same(fresh, await reader.WaitAsync(TimeSpan.FromSeconds(5)));
    }

    [Fact]
    public async Task Leases_never_see_a_released_view_while_the_writer_publishes()
    {
        const int Readers = 8;
        ContentView? slot = View(out _);
        var stores = new System.Collections.Concurrent.ConcurrentBag<CountingStoreView>();
        int bad = 0, leasesWhilePublishing = 0, publishing = 0;
        using var started = new CountdownEvent(Readers);
        using var stop = new CancellationTokenSource();
        var readers = Enumerable.Range(0, Readers).Select(_ => Task.Factory.StartNew(() =>
        {
            bool first = true;
            while (!stop.IsCancellationRequested)
            {
                var lease = ContentSource.Acquire(ref slot);
                if (lease == null) continue;
                if (lease.IsReleased || ((CountingStoreView)lease.Store).DisposeCount != 0) Interlocked.Increment(ref bad);
                if (Volatile.Read(ref publishing) == 1) Interlocked.Increment(ref leasesWhilePublishing);
                lease.Dispose();
                if (first) { first = false; started.Signal(); }
            }
        }, TaskCreationOptions.LongRunning)).ToArray();

        // Every reader holds leases in a loop before the writer starts, and the writer keeps publishing until the readers
        // took plenty of leases meanwhile, so the read → TryAddRef → retry race is exercised on every run.
        Assert.True(started.Wait(TimeSpan.FromSeconds(10)), "the readers never started");
        Volatile.Write(ref publishing, 1);
        var clock = System.Diagnostics.Stopwatch.StartNew();
        for (int i = 0; i < 10_000 || (Volatile.Read(ref leasesWhilePublishing) < 10_000 && i < 200_000 && clock.Elapsed < TimeSpan.FromSeconds(10)); i++)
        {
            var next = View(out var store);
            stores.Add(store);
            var previous = Interlocked.Exchange(ref slot, next);
            previous?.Dispose();
        }
        Volatile.Write(ref publishing, 0);
        stop.Cancel();
        await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(10));
        slot?.Dispose();

        Assert.Equal(0, bad);
        Assert.True(leasesWhilePublishing > 0, "no lease was taken while the writer published");
        Assert.All(stores, s => Assert.Equal(1, s.DisposeCount));
    }
}
