using System.Text;
using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Search;
using Xunit.Abstractions;

namespace Anything.Tests.Content.Integration;

/// <summary>A fact that runs only where the test binaries' drive is reachable as \\localhost\X$; otherwise skipped.</summary>
public sealed class ShareFactAttribute : FactAttribute
{
    public ShareFactAttribute(bool slow = false)
    {
        if (!RealFilesRig.ShareReachable()) Skip = "No administrative share for this drive (\\\\localhost\\X$).";
        else if (slow && Environment.GetEnvironmentVariable("RP_SLOW_IT") != "1") Skip = "Over a minute: set RP_SLOW_IT=1 to run it.";
    }
}

/// <summary>
/// I2 over a real share (the drive's own administrative share, so a network root with SMB in between): the network lane,
/// the per-share speed limit end to end, and the watchdog leaving the limit's waits out of a read's time.
/// </summary>
[Collection(RealFilesSerial.Name)]
public sealed class NetworkShareTests(ITestOutputHelper output) : IDisposable
{
    readonly RealFilesRig _rig = new(ContentMode.Documents, viaShare: true);

    public void Dispose() => _rig.Dispose();

    static byte[] Words(int bytes, int seed)
    {
        var rnd = new Random(seed);
        var sb = new StringBuilder(bytes + 16);
        while (sb.Length < bytes) sb.Append("word").Append(rnd.Next(100_000)).Append(rnd.Next(12) == 0 ? '\n' : ' ');
        return Encoding.ASCII.GetBytes(sb.ToString(0, bytes));
    }

    bool WaitUpToDate(int documents, TimeSpan timeout)
        => SpinWait.SpinUntil(() => _rig.Content!.Status is { Phase: ContentPhase.UpToDate } s && s.Done == documents && _rig.Rows().Count == documents, timeout);

    [ShareFact]
    public void Network_files_are_read_at_the_shares_speed_limit()
    {
        // Each under the 1 MB text limit, so each is read whole (a text file past it is read only as far as fills it).
        const int Files = 9;
        for (int i = 0; i < Files; i++) _rig.Write($@"Share\big{i}.txt", Words(1_000_000, i));
        _rig.Configure(s => s.NetworkKilobytesPerSecond = 2048); // 2 MB/s
        _rig.Start(threaded: true);
        Assert.True(WaitUpToDate(Files, TimeSpan.FromSeconds(60)), $"not read in time: {_rig.Content!.Status}");

        var log = _rig.Files.Log;
        Assert.Equal(Files, log.Count);
        Assert.All(log, r => Assert.True(r.Network, $"{r.Path} wasn't read as a network file"));
        long bytes = log.Sum(r => r.BytesRead);
        double seconds = (log.Max(r => r.EndMs) - log.Min(r => r.StartMs)) / 1000.0;
        double rate = bytes / seconds;
        output.WriteLine($"{bytes:N0} bytes in {seconds:F2} s: {rate / (1 << 20):F2} MB/s against a limit of 2 MB/s (1 s of burst)");
        Assert.True(bytes >= 8 << 20, $"read only {bytes:N0} bytes");
        // 8 MB at 2 MB/s with one second of burst: at least 3 s; never much faster than the limit.
        Assert.True(seconds >= 2.5, $"read in {seconds:F2} s");
        Assert.True((bytes - (2 << 20)) / seconds <= 2.3 * (1 << 20), $"{rate / (1 << 20):F2} MB/s");

        // What was read is searchable through the share's own key.
        var r = new SearchEngine().Search(_rig.Index, "content:word42*", new SearchOptions(), SortKey.Name, false, default);
        Assert.Null(r.Error);
        Assert.NotEmpty(r.Ids);
        Assert.StartsWith(@"\\localhost\", _rig.Index.GetFullPath(r.Ids[0]), StringComparison.OrdinalIgnoreCase);
    }

    [ShareFact]
    public void A_share_searched_by_name_is_read_inside_once_network_drives_are_switched_on()
    {
        // The share is searched by name only (not among the folders searched inside): nothing on it is read ...
        _rig.Write(@"Share\note.txt", "zqxnetprobe words on the share");
        _rig.Write(@"Share\Jobs\job.txt", "zqxnetprobe second file");
        _rig.Configure(s => s.Folders = []);
        _rig.Start(threaded: true);
        Assert.True(SpinWait.SpinUntil(() => _rig.Content!.Status is { Phase: ContentPhase.UpToDate }, TimeSpan.FromSeconds(30)),
            $"never up to date: {_rig.Content!.Status}");
        Thread.Sleep(500);
        Assert.Empty(_rig.Files.Log);
        Assert.Empty(_rig.Rows());

        // ... until Network drives is switched on: then every file on it is read, as a network file, and found by its words.
        var on = _rig.Settings;
        on.Folders = [];
        on.ReadNetworkDrives = true;
        _rig.Content!.Apply(on);
        Assert.True(WaitUpToDate(2, TimeSpan.FromSeconds(60)), $"not read in time: {_rig.Content!.Status}");
        Assert.All(_rig.Files.Log, r => Assert.True(r.Network, $"{r.Path} wasn't read as a network file"));
        var r = new SearchEngine().Search(_rig.Index, "content:zqxnetprobe", new SearchOptions(), SortKey.Name, false, default);
        Assert.Null(r.Error);
        Assert.Equal(2, r.Ids.Length);
        Assert.All(r.Ids, id => Assert.StartsWith(@"\\localhost\", _rig.Index.GetFullPath(id), StringComparison.OrdinalIgnoreCase));
    }

    [ShareFact(slow: true)]
    public void A_read_held_back_by_the_limit_past_its_time_limit_is_not_cut_off()
    {
        // A 5.5 MB RTF, mostly a picture's hex data (read through, up to 32 MB, without filling the 1 MB text limit) at
        // 64 KB/s: about 90 s of throttle waits, well past the network time limit (60 s, the watchdog cancelling at 65 s) if
        // they counted.
        var hex = new byte[5_500_000];
        for (int i = 0; i < hex.Length; i++) hex[i] = (byte)"0123456789abcdef"[i * 7 % 16];
        _rig.Write(@"Share\slow.rtf", [.. "{\\rtf1 slow words {\\pict\\pngblip "u8, .. hex, .. "} after}"u8]);
        _rig.Configure(s => s.NetworkKilobytesPerSecond = 64);
        _rig.Start(threaded: true);
        Assert.True(WaitUpToDate(1, TimeSpan.FromMinutes(4)), $"not read in time: {_rig.Content!.Status}");
        var row = _rig.Row(_rig.Full(@"Share\slow.rtf"))!.Value;
        Assert.True(row.Status is DocStatus.Ok or DocStatus.Truncated, $"read as {row.Status}");
        var read = Assert.Single(_rig.Files.Log);
        output.WriteLine($"{read.BytesRead:N0} bytes in {(read.EndMs - read.StartMs) / 1000.0:F1} s");
        Assert.True(read.EndMs - read.StartMs > 70_000, "the limit didn't hold the read back past the time limit");
    }
}
