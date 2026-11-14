namespace Anything.Core;

/// <summary>When the nightly update is due: once a day at a time of day (11:00 PM unless changed), local time.</summary>
public static class NightlyUpdate
{
    /// <summary>The last time it was due at or before <paramref name="nowUtc"/>, as UTC: today's, or yesterday's while it isn't that time yet.</summary>
    public static DateTime LastSlot(DateTime nowUtc, TimeSpan at, TimeZoneInfo? zone = null)
    {
        zone ??= TimeZoneInfo.Local;
        var local = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(nowUtc, DateTimeKind.Utc), zone);
        var today = local.Date + at;
        var slot = today <= local ? today : today.AddDays(-1);
        // A time a clock change skips (the hour lost in spring) runs an hour later, at the same instant as the next valid one.
        if (zone.IsInvalidTime(slot)) slot = slot.AddHours(1);
        return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(slot, DateTimeKind.Unspecified), zone);
    }

    /// <summary>Whether it is due: a slot has come since the one last done.</summary>
    public static bool Due(DateTime lastDoneUtc, DateTime nowUtc, TimeSpan at, TimeZoneInfo? zone = null)
        => lastDoneUtc < LastSlot(nowUtc, at, zone);
}
