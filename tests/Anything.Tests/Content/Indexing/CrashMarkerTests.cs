using System.Buffers.Binary;
using Anything.Core.Content;
using Anything.Core.Content.Indexing;

namespace Anything.Tests.Content.Indexing;

public sealed class CrashMarkerTests : IDisposable
{
    readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("rp-marker-");
    string MarkerPath => Path.Combine(_dir.FullName, "reading");

    public void Dispose()
    {
        try { _dir.Delete(true); } catch (IOException) { }
    }

    [Fact]
    public void A_slot_left_by_the_last_session_names_its_file()
    {
        var m = CrashMarker.Open(MarkerPath, out var leftovers)!;
        Assert.Empty(leftovers);
        int slot = m.Acquire();
        m.Enter(slot, new MarkerEntry(42, 3, MarkerLane.Local, 1000, 5000));
        // The process dies here: the handle closes without the slot being zeroed (Dispose would zero it).
        CloseWithoutClearing(m);
        using var again = CrashMarker.Open(MarkerPath, out leftovers)!;
        var e = Assert.Single(leftovers);
        Assert.Equal((42UL, (ushort)3, MarkerLane.Local, 1000L, 5000L), (e.Key, e.Volume, e.Lane, e.Size, e.Modified));
        Assert.Equal(1, again.FreshCulprits);
        Assert.Single(again.Carried);
    }

    [Fact]
    public void A_file_left_normally_blames_nothing()
    {
        using (var m = CrashMarker.Open(MarkerPath, out _)!)
        {
            int slot = m.Acquire();
            uint epoch = m.Enter(slot, new MarkerEntry(42, 0, MarkerLane.Local, 1, 2));
            m.Leave(slot, epoch);
            CloseWithoutClearing(m);
        }
        using var again = CrashMarker.Open(MarkerPath, out var leftovers)!;
        Assert.Empty(leftovers);
    }

    [Fact]
    public void Slots_from_an_earlier_boot_blame_nothing()
    {
        var m = CrashMarker.Open(MarkerPath, out _)!;
        m.Enter(m.Acquire(), new MarkerEntry(42, 0, MarkerLane.Local, 1, 2));
        CloseWithoutClearing(m);
        // Rewrite the header as if another boot wrote it (a power cut while reading).
        var bytes = File.ReadAllBytes(MarkerPath);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)) + 1);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16)) - TimeSpan.FromDays(1).Ticks);
        File.WriteAllBytes(MarkerPath, bytes);
        using var again = CrashMarker.Open(MarkerPath, out var leftovers)!;
        Assert.Empty(leftovers);
    }

    [Fact]
    public void A_late_thread_never_clears_its_successors_slot()
    {
        using var m = CrashMarker.Open(MarkerPath, out _)!;
        int slot = m.Acquire();
        uint first = m.Enter(slot, new MarkerEntry(1, 0, MarkerLane.Local, 1, 1));
        uint second = m.Enter(slot, new MarkerEntry(2, 0, MarkerLane.Local, 1, 1));
        m.Leave(slot, first); // the old read finishing late
        CloseWithoutClearing(m);
        using var again = CrashMarker.Open(MarkerPath, out var leftovers)!;
        Assert.Equal(2UL, Assert.Single(leftovers).Key);
        _ = second;
    }

    [Fact]
    public void Carried_culprits_stay_until_recorded_and_the_open_handle_lets_readers_in()
    {
        var m = CrashMarker.Open(MarkerPath, out _)!;
        m.Carry(new MarkerEntry(7, 0, MarkerLane.Network, 1, 1));
        m.Carry(new MarkerEntry(8, 0, MarkerLane.OnDemand, 1, 1));
        // Another reader can look at the file meanwhile (FileShare.Read).
        using (var peek = new FileStream(MarkerPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) Assert.Equal(4096, peek.Length);
        // Another writer can't: a second app instance doesn't get a marker.
        Assert.Null(CrashMarker.Open(MarkerPath, out _));
        m.Uncarry(7);
        m.Dispose();
        using var again = CrashMarker.Open(MarkerPath, out var leftovers)!;
        Assert.Equal(8UL, Assert.Single(leftovers).Key);
        Assert.Equal(0, again.FreshCulprits);
        again.ClearCarried();
        again.Dispose();
        using var third = CrashMarker.Open(MarkerPath, out leftovers)!;
        Assert.Empty(leftovers);
    }

    [Fact]
    public void Sixteen_slots_then_none()
    {
        using var m = CrashMarker.InMemory();
        var slots = Enumerable.Range(0, CrashMarker.LiveSlots).Select(_ => m.Acquire()).ToList();
        Assert.DoesNotContain(-1, slots);
        Assert.Equal(-1, m.Acquire());
        m.Release(slots[3]);
        Assert.Equal(slots[3], m.Acquire());
    }

    [Fact]
    public void A_crash_while_reading_records_the_file_Crashed_once_and_skips_it_until_it_changes()
    {
        var store = new FakeStore();
        var settings = Harness.DefaultSettings();
        // First session: the reader is inside bad.pdf when the process dies.
        var options = new ContentIndexOptions { Folder = _dir.FullName };
        using (var h = new Harness(settings, store, options))
        {
            h.Folder(@"C:\Users\Me");
            h.File(@"C:\Users\Me\good.txt", "fine");
            h.File(@"C:\Users\Me\bad.pdf", "would crash");
            h.Files.OnRead = r =>
            {
                if (r.FullPath.EndsWith("bad.pdf", StringComparison.Ordinal))
                {
                    CloseWithoutClearing(h.Follower.Marker!); // the process dies in the middle of this file
                    throw new OperationCanceledException();
                }
                return null;
            };
            h.Start();
            try { h.Settle(); } catch (OperationCanceledException) { }
        }
        Assert.Null(store.Reopen().Live().FirstOrDefault(d => d.Path.EndsWith("bad.pdf", StringComparison.Ordinal)).Path);

        // Next start: the same files with the same stamps (the harness's dates repeat in the same order). The leftover
        // slot names bad.pdf: it is recorded Crashed and never opened.
        using (var h = new Harness(settings, store, options))
        {
            h.Folder(@"C:\Users\Me");
            h.File(@"C:\Users\Me\good.txt", "fine");
            h.File(@"C:\Users\Me\bad.pdf", "would crash");
            h.Start();
            h.Settle();
            Assert.DoesNotContain(h.Files.ReadPaths, p => p.EndsWith("bad.pdf", StringComparison.Ordinal));
            Assert.Equal(DocStatus.Crashed, h.Row(@"C:\Users\Me\bad.pdf")!.Value.Status);
            Assert.Equal(DocStatus.Crashed, h.Content.GetStatus(@"C:\Users\Me\bad.pdf"));
            Assert.Null(h.Content.GetStatus(@"C:\Users\Me\good.txt"));
            Assert.Equal(1, h.Content.CountsByStatus()[DocStatus.Crashed]);
            // It changes: read again.
            h.Change(@"C:\Users\Me\bad.pdf", "fixed now");
            h.Settle();
            Assert.Equal("fixed now", h.Doc(@"C:\Users\Me\bad.pdf")!.Body);
        }
        // The culprit was recorded: a third start blames nothing.
        using var marker = CrashMarker.Open(MarkerPath, out var left)!;
        Assert.Empty(left);
    }

    [Fact]
    public void A_calibration_read_that_stopped_the_app_is_recorded_Crashed_by_the_index_and_never_read()
    {
        const string Bad = @"C:\Users\Me\bad.pdf", Boxes = @"C:\Users\Me\boxes.pdf";
        // Settings' calibration was in bad.pdf (an on-demand slot with the file's stamp, keyed by document key) when the app
        // died; a preview's page boxes were in boxes.pdf (no stamp: only those on-demand reads skip it).
        var m = CrashMarker.Open(MarkerPath, out _)!;
        m.Enter(m.Acquire(), new MarkerEntry(Harness.Key(Bad), 0, MarkerLane.OnDemand, 20, 1234));
        m.Enter(m.Acquire(), new MarkerEntry(Harness.Key(Boxes), 0, MarkerLane.OnDemand, 0, 0));
        CloseWithoutClearing(m);
        using var h = new Harness(Harness.DefaultSettings(), new FakeStore(), new ContentIndexOptions { Folder = _dir.FullName });
        h.Folder(@"C:\Users\Me");
        h.File(Bad, "would crash", size: 20, modified: 1234);
        h.File(Boxes, "fine words");
        h.Start();
        h.Settle();
        Assert.DoesNotContain(h.Files.ReadPaths, p => p.EndsWith("bad.pdf", StringComparison.Ordinal));
        Assert.Equal(DocStatus.Crashed, h.Row(Bad)!.Value.Status);
        Assert.Equal("fine words", h.Doc(Boxes)!.Body);
    }

    /// <summary>Closes the marker's handle without zeroing its live slots, as a process crash would.</summary>
    internal static void CloseWithoutClearing(CrashMarker m)
    {
        var field = typeof(CrashMarker).GetField("_file", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var handle = (Microsoft.Win32.SafeHandles.SafeFileHandle?)field.GetValue(m);
        var disposed = typeof(CrashMarker).GetField("_disposed", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        disposed.SetValue(m, true);
        handle?.Dispose();
    }
}
