namespace Anything.Core.Content.Indexing;

/// <summary>Power, idle, full-screen and disk state, polled every 5 s.</summary>
internal interface IPowerPolicy
{
    /// <summary>The state now, with the free space of the drive holding the content folder.</summary>
    PowerState Now(string contentFolder);
}

/// <summary>One poll of the power policy.</summary>
/// <param name="FreeBytes">Free bytes for this user on the content folder's drive; -1 = unknown (never pauses).</param>
/// <param name="TotalBytes">That drive's size; -1 = unknown.</param>
internal readonly record struct PowerState(bool OnBattery, bool BatterySaver, TimeSpan Idle, bool FullScreen, long FreeBytes, long TotalBytes = -1)
{
    /// <summary>Plenty of everything: mains power, idle for an hour, nothing full screen, free space unknown.</summary>
    public static PowerState Unlimited { get; } = new(false, false, TimeSpan.FromHours(1), false, -1, -1);
}

/// <summary>The PC's real state through Win32 (GetSystemPowerStatus, GetLastInputInfo, SHQueryUserNotificationState, GetDiskFreeSpaceEx).</summary>
internal sealed class SystemPowerPolicy : IPowerPolicy
{
    /// <summary>The one instance (the state is the PC's, not the index's).</summary>
    public static SystemPowerPolicy Instance { get; } = new();

    readonly object _gate = new();
    long _polledAt = -1_000_000;
    string? _polledFolder;
    PowerState _last;

    /// <inheritdoc/>
    public PowerState Now(string contentFolder)
    {
        lock (_gate)
        {
            long now = Environment.TickCount64;
            // Idle time changes every keystroke, so it is read fresh; the rest is polled every 5 s.
            var idle = ContentNative.IdleTime();
            if (now - _polledAt >= 5000 || !string.Equals(_polledFolder, contentFolder, StringComparison.OrdinalIgnoreCase))
            {
                var (battery, saver) = ContentNative.Power();
                var (free, total) = ContentNative.DiskSpace(contentFolder);
                _last = new PowerState(battery, saver, idle, ContentNative.FullScreen(), free, total);
                _polledAt = now;
                _polledFolder = contentFolder;
            }
            return _last with { Idle = idle };
        }
    }
}

/// <summary>A policy that never pauses (demo, tests).</summary>
internal sealed class NoPowerPolicy : IPowerPolicy
{
    /// <summary>The one instance.</summary>
    public static NoPowerPolicy Instance { get; } = new();
    /// <inheritdoc/>
    public PowerState Now(string contentFolder) => PowerState.Unlimited;
}

/// <summary>What the power state allows (§10 table): reading, merges, and how fast.</summary>
internal static class PowerRules
{
    /// <summary>Input within this long counts as the user being active: reading runs at a 50% duty cycle.</summary>
    public static readonly TimeSpan ActiveWindow = TimeSpan.FromMinutes(2);
    /// <summary>Below this much free space on the content folder's drive, reading and merges pause.</summary>
    public const long LowDiskBytes = 2L << 30;

    /// <summary>The user touched the keyboard or mouse recently (duty cycle on).</summary>
    public static bool UserActive(in PowerState s) => s.Idle < ActiveWindow;

    /// <summary>Less than 2 GB, or under 5% of the drive, is free.</summary>
    public static bool LowDisk(in PowerState s)
        => s.FreeBytes >= 0 && (s.FreeBytes < LowDiskBytes || (s.TotalBytes > 0 && s.FreeBytes < s.TotalBytes / 20));

    /// <summary>
    /// Why reading pauses now, in plain words, or null when it may go on: Battery Saver, battery (unless allowed), a
    /// full-screen app, low disk space, or running as administrator (files only administrators can open stay out of
    /// the user's index).
    /// </summary>
    public static string? ReadPause(in PowerState s, bool readOnBattery, bool elevated)
    {
        if (s.BatterySaver) return "Battery Saver is on";
        if (s.OnBattery && !readOnBattery) return "on battery";
        if (s.FullScreen) return "an app is full screen";
        if (LowDisk(s)) return "low on disk space";
        if (elevated) return "running as administrator";
        return null;
    }

    /// <summary>Why merges pause now (battery and Battery Saver stop them, as do a full-screen app and low disk space); null = they may run.</summary>
    public static string? MergePause(in PowerState s, bool readOnBattery)
    {
        if (s.BatterySaver) return "Battery Saver is on";
        if (s.OnBattery && !readOnBattery) return "on battery";
        if (s.FullScreen) return "an app is full screen";
        if (LowDisk(s)) return "low on disk space";
        return null;
    }

    /// <summary>Large merges wait for the user to be idle for 2 minutes and for mains power, whatever the queue holds.</summary>
    public static bool AllowLargeMerge(in PowerState s) => s.Idle >= ActiveWindow && !s.OnBattery && !s.BatterySaver;
}
