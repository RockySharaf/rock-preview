using System.Buffers.Binary;
using Anything.Core.Thumbs;

namespace Anything.Tests.Thumbs;

public sealed class ThumbMarkerTests : IDisposable
{
    readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("rp-thumbmarker-");
    string MarkerPath => Path.Combine(_dir.FullName, "inflight.bin");

    public void Dispose()
    {
        try { _dir.Delete(true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    static ThumbKey Key(int n) => ThumbKey.Of($@"D:\Videos\clip {n}.mp4", new ThumbStamp(n, n), 256);

    [Fact]
    public void A_slot_left_by_a_crash_names_its_key_until_cleared()
    {
        var m = ThumbMarker.Open(MarkerPath, out var leftovers)!;
        Assert.Empty(leftovers);
        int slot = m.Acquire();
        m.Enter(slot, Key(1));
        Assert.Equal([new InFlightEntry(Key(1).Hash64, Key(1).Check32)], m.Live());
        m.Abandon();
        var again = ThumbMarker.Open(MarkerPath, out leftovers)!;
        Assert.Equal([new InFlightEntry(Key(1).Hash64, Key(1).Check32)], leftovers);
        // Not yet recorded: a second crash now would still name it.
        Assert.Single(again.Live());
        again.ClearLeftovers();
        Assert.Empty(again.Live());
        again.Dispose();
        using var third = ThumbMarker.Open(MarkerPath, out leftovers)!;
        Assert.Empty(leftovers);
    }

    [Fact]
    public void A_clean_stop_blames_nothing()
    {
        using (var m = ThumbMarker.Open(MarkerPath, out _)!) m.Enter(m.Acquire(), Key(1));
        using var again = ThumbMarker.Open(MarkerPath, out var leftovers)!;
        Assert.Empty(leftovers);
    }

    [Fact]
    public void An_abandoned_thread_returning_late_does_not_clear_its_successors_slot()
    {
        using var m = ThumbMarker.Open(MarkerPath, out _)!;
        int slot = m.Acquire();
        uint stuck = m.Enter(slot, Key(1));
        // The stuck thread is replaced; its successor reuses the slot for another key.
        m.Enter(slot, Key(2));
        m.Leave(slot, stuck);
        Assert.Equal([new InFlightEntry(Key(2).Hash64, Key(2).Check32)], m.Live());
    }

    [Fact]
    public void Slots_from_an_earlier_boot_blame_nothing()
    {
        var m = ThumbMarker.Open(MarkerPath, out _)!;
        m.Enter(m.Acquire(), Key(1));
        m.Abandon();
        var bytes = File.ReadAllBytes(MarkerPath);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(12), BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(12)) + 1);
        BinaryPrimitives.WriteInt64LittleEndian(bytes.AsSpan(16), BinaryPrimitives.ReadInt64LittleEndian(bytes.AsSpan(16)) - TimeSpan.FromDays(1).Ticks);
        File.WriteAllBytes(MarkerPath, bytes);
        using var again = ThumbMarker.Open(MarkerPath, out var leftovers)!;
        Assert.Empty(leftovers);
        Assert.Empty(again.Live());
    }

    [Fact]
    public void Slots_run_out_and_come_back()
    {
        using var m = ThumbMarker.InMemory();
        var slots = Enumerable.Range(0, ThumbMarker.Slots).Select(_ => m.Acquire()).ToList();
        Assert.DoesNotContain(-1, slots);
        Assert.Equal(-1, m.Acquire());
        m.Release(slots[3]);
        Assert.Equal(slots[3], m.Acquire());
        Assert.Equal(0u, m.Enter(-1, Key(1)));
    }

    [Fact]
    public void A_file_held_by_another_marker_cannot_be_opened()
    {
        using var first = ThumbMarker.Open(MarkerPath, out _)!;
        Assert.Null(ThumbMarker.Open(MarkerPath, out _));
    }
}
