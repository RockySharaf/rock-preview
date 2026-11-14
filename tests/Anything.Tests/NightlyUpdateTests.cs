using Anything.Core;

namespace Anything.Tests;

/// <summary>The nightly update: due once a day at its time, and caught up the next time the PC is on when it was missed.</summary>
public sealed class NightlyUpdateTests
{
    // Toronto: UTC-5 in winter, UTC-4 in summer.
    static readonly TimeZoneInfo Zone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
    static readonly TimeSpan Eleven = new(23, 0, 0);

    static DateTime Local(int y, int mo, int d, int h, int mi = 0) =>
        TimeZoneInfo.ConvertTimeToUtc(new DateTime(y, mo, d, h, mi, 0, DateTimeKind.Unspecified), Zone);

    [Fact]
    public void Before_eleven_the_last_slot_is_last_night()
        => Assert.Equal(Local(2026, 11, 13, 23), NightlyUpdate.LastSlot(Local(2026, 11, 14, 9), Eleven, Zone));

    [Fact]
    public void From_eleven_on_it_is_tonight()
    {
        Assert.Equal(Local(2026, 11, 14, 23), NightlyUpdate.LastSlot(Local(2026, 11, 14, 23), Eleven, Zone));
        Assert.Equal(Local(2026, 11, 14, 23), NightlyUpdate.LastSlot(Local(2026, 11, 14, 23, 45), Eleven, Zone));
    }

    [Fact]
    public void Done_tonight_it_is_not_due_again_until_tomorrow_night()
    {
        var done = Local(2026, 11, 14, 23);
        Assert.False(NightlyUpdate.Due(done, Local(2026, 11, 15, 8), Eleven, Zone));
        Assert.False(NightlyUpdate.Due(done, Local(2026, 11, 15, 22, 59), Eleven, Zone));
        Assert.True(NightlyUpdate.Due(done, Local(2026, 11, 15, 23), Eleven, Zone));
    }

    [Fact]
    public void A_night_the_PC_was_off_is_caught_up_when_it_starts()
    {
        var done = Local(2026, 11, 12, 23);
        // Off on the 13th at 11 PM, on again the morning after: due.
        Assert.True(NightlyUpdate.Due(done, Local(2026, 11, 14, 8), Eleven, Zone));
        // Several nights off still make one update, not one per night: the slot is the last one.
        Assert.Equal(Local(2026, 11, 13, 23), NightlyUpdate.LastSlot(Local(2026, 11, 14, 8), Eleven, Zone));
    }

    [Fact]
    public void Its_time_can_be_changed()
    {
        var two = new TimeSpan(2, 0, 0);
        Assert.Equal(Local(2026, 11, 14, 2), NightlyUpdate.LastSlot(Local(2026, 11, 14, 9), two, Zone));
        Assert.Equal(Local(2026, 11, 13, 2), NightlyUpdate.LastSlot(Local(2026, 11, 14, 1), two, Zone));
    }

    [Fact]
    public void Clock_changes_keep_it_at_its_local_time()
    {
        // Summer time began 2027-03-14 at 2 AM (to 3 AM): a 2:30 AM update that night runs at 3:30.
        var halfTwo = new TimeSpan(2, 30, 0);
        Assert.Equal(Local(2027, 3, 14, 3, 30), NightlyUpdate.LastSlot(Local(2027, 3, 14, 9), halfTwo, Zone));
        // Across it, 11 PM stays 11 PM local.
        Assert.Equal(Local(2027, 3, 14, 23), NightlyUpdate.LastSlot(Local(2027, 3, 15, 8), Eleven, Zone));
    }
}
