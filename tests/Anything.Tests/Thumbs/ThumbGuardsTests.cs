using Anything.Core.Thumbs;

namespace Anything.Tests.Thumbs;

public sealed class ThumbGuardsTests
{
    [Theory]
    [InlineData(".DOC", true)]
    [InlineData(".xls", true)]
    [InlineData(".Ppt", true)]
    [InlineData(".docx", true)]
    [InlineData(".xltm", true)]
    [InlineData(".xlsb", true)]
    [InlineData(".PPSX", true)]
    [InlineData(".sldx", true)]
    [InlineData(".sldm", true)]
    [InlineData(".odg", true)]
    [InlineData(".jpg", false)]
    [InlineData(".pdf", false)]
    [InlineData("", false)]
    public void Office_pictures_are_checked_for_blank_pages_by_extension(string extension, bool office)
        => Assert.Equal(office, ThumbGuards.IsOfficeDocument(extension));

    [Fact]
    public void Only_one_suspect_holds_the_turn_until_it_gives_it_back()
    {
        var turn = new SuspectTurn();
        Assert.True(turn.TryTake(1, 1_000));
        Assert.False(turn.TryTake(2, 1_001));
        Assert.Equal(1, turn.Holder);
        Assert.True(turn.Release(1));
        Assert.Equal(0, turn.Holder);
        Assert.True(turn.TryTake(2, 1_002));
        Assert.Equal(2, turn.Holder);
    }

    [Fact]
    public void The_holder_may_take_its_own_turn_again_and_its_time_starts_again()
    {
        var turn = new SuspectTurn(limitMs: 100);
        Assert.True(turn.TryTake(7, 0));
        Assert.True(turn.TryTake(7, 90));
        // Counted from the second take: 150 is only 60 after it.
        Assert.False(turn.TryTake(8, 150));
        Assert.True(turn.TryTake(8, 190));
    }

    [Fact]
    public void A_holder_that_never_gives_the_turn_back_loses_it_after_the_limit()
    {
        var turn = new SuspectTurn();
        Assert.True(turn.TryTake(1, 10_000));
        Assert.False(turn.TryTake(2, 10_000 + SuspectTurn.LimitMs - 1));
        Assert.True(turn.TryTake(2, 10_000 + SuspectTurn.LimitMs));
        // The stuck holder comes back late: it gives back nothing, the new holder keeps the turn.
        Assert.False(turn.Release(1));
        Assert.Equal(2, turn.Holder);
        Assert.False(turn.TryTake(3, 10_000 + SuspectTurn.LimitMs + 1));
    }

    [Fact]
    public void Giving_back_a_turn_not_held_does_nothing()
    {
        var turn = new SuspectTurn();
        Assert.False(turn.Release(5));
        Assert.False(turn.Release(0));
        Assert.True(turn.TryTake(5, 0));
        Assert.False(turn.Release(6));
        Assert.Equal(5, turn.Holder);
        Assert.Throws<ArgumentOutOfRangeException>(() => turn.TryTake(0, 0));
    }

    [Fact]
    public void Many_threads_never_hold_the_turn_together()
    {
        var turn = new SuspectTurn();
        int inside = 0, most = 0, takes = 0;
        Parallel.For(1, 2001, i =>
        {
            for (int tries = 0; tries < 1000; tries++)
            {
                if (!turn.TryTake(i, 0)) { Thread.SpinWait(20); continue; }
                int now = Interlocked.Increment(ref inside);
                int seen;
                while ((seen = Volatile.Read(ref most)) < now && Interlocked.CompareExchange(ref most, now, seen) != seen) { }
                Interlocked.Increment(ref takes);
                Interlocked.Decrement(ref inside);
                Assert.True(turn.Release(i));
                return;
            }
        });
        Assert.Equal(1, most);
        Assert.True(takes > 0);
        Assert.Equal(0, turn.Holder);
    }

    [Fact]
    public void A_picture_is_stored_only_for_the_file_it_was_made_for()
    {
        var made = new ThumbStamp(1234, 5678);
        Assert.True(ThumbGuards.MayStore(made, readNow: true, new ThumbStamp(1234, 5678)));
        // Replaced meanwhile: another length, or another time.
        Assert.False(ThumbGuards.MayStore(made, true, new ThumbStamp(1235, 5678)));
        Assert.False(ThumbGuards.MayStore(made, true, new ThumbStamp(1234, 5679)));
        // The file can't be read again (gone, locked away, a drive that went): not stored.
        Assert.False(ThumbGuards.MayStore(made, readNow: false, made));
        // A stamp never known can't be compared.
        Assert.False(ThumbGuards.MayStore(ThumbStamp.Unknown, true, ThumbStamp.Unknown));
    }

    [Fact]
    public void A_folder_of_another_account_is_never_tried_again_but_a_passing_problem_is()
    {
        Assert.True(ThumbGuards.RetriesOpen(ThumbStoreProblem.FolderUnusable));
        Assert.True(ThumbGuards.RetriesOpen(ThumbStoreProblem.InUse));
        Assert.False(ThumbGuards.RetriesOpen(ThumbStoreProblem.OtherAccount));
        Assert.False(ThumbGuards.RetriesOpen(ThumbStoreProblem.None));
        Assert.False(ThumbGuards.RetriesOpen(ThumbStoreProblem.Off));
    }
}
