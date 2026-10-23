using Anything.Core.Content;
using Anything.Core.Content.Indexing;

namespace Anything.Tests.Content.Indexing;

/// <summary>
/// Settings' Network drives switch: every share the name index holds (a mapped letter through its share, or
/// \\server\share) is read inside as if it were among the folders searched inside; exclusions and the deny-list still
/// apply, and switching it off drops what was read there.
/// </summary>
public sealed class NetworkDrivesTests
{
    static Harness Ready(ContentSettings settings, Action<Harness> files, ContentIndexOptions? options = null)
    {
        var h = new Harness(settings, null, options);
        h.Folder("C:");
        h.Folder(@"C:\Users\Me");
        files(h);
        h.Start();
        h.Settle();
        return h;
    }

    static void Share(Harness h)
    {
        h.Folder(@"\\nas\jobs");
        h.File(@"\\nas\jobs\a.pdf", "print job");
        h.File(@"\\nas\jobs\Proofs\b.pdf", "proof copy");
        h.File(@"C:\Users\Me\local.pdf", "local words");
    }

    static ContentSettings With(ContentSettings s, Action<ContentSettings> change)
    {
        var copy = s.Clone();
        change(copy);
        return copy;
    }

    [Fact]
    public void A_share_searched_by_name_is_read_inside_only_once_network_drives_are_switched_on()
    {
        var s = Harness.DefaultSettings();
        using var h = Ready(s, Share);
        Assert.Equal("local words", h.Doc(@"C:\Users\Me\local.pdf")!.Body);
        Assert.DoesNotContain(h.Files.ReadPaths, p => p.StartsWith(@"\\nas\", StringComparison.OrdinalIgnoreCase));
        Assert.Null(h.Row(@"\\nas\jobs\a.pdf"));
        Assert.Equal(ContentPhase.UpToDate, h.Content.Status.Phase);

        h.Content.Apply(With(s, t => t.ReadNetworkDrives = true));
        h.Settle();
        Assert.Equal("print job", h.Doc(@"\\nas\jobs\a.pdf")!.Body);
        Assert.Equal("proof copy", h.Doc(@"\\nas\jobs\Proofs\b.pdf")!.Body);
        Assert.Equal("local words", h.Doc(@"C:\Users\Me\local.pdf")!.Body);
    }

    [Fact]
    public void A_mapped_drive_letter_is_read_inside_through_its_share_on_the_network_lane()
    {
        var s = With(Harness.DefaultSettings(), t => t.ReadNetworkDrives = true);
        var options = new ContentIndexOptions
        {
            VolumeResolver = p => p.StartsWith("Z:", StringComparison.OrdinalIgnoreCase)
                ? (@"unc:\\NAS\JOBS", p.Length > 2 ? p[2..].Trim('\\') : "", @"Z:\")
                : Harness.Resolve(p),
        };
        var network = new List<string>();
        using var h = Ready(s, h =>
        {
            h.Files.OnRead = r =>
            {
                if (r.Network) lock (network) network.Add(r.FullPath);
                return null;
            };
            h.Folder("Z:");
            h.File(@"Z:\Jobs\a.pdf", "mapped job");
        }, options);
        // Stored under the share's own key: Z:\Jobs\a.pdf and \\nas\jobs\Jobs\a.pdf are one document.
        Assert.Equal("mapped job", h.Doc(@"\\nas\jobs\Jobs\a.pdf")!.Body);
        lock (network) Assert.Contains(network, p => p.Equals(@"Z:\Jobs\a.pdf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Folders_left_out_stay_out_on_a_share()
    {
        var s = With(Harness.DefaultSettings(), t =>
        {
            t.ReadNetworkDrives = true;
            t.ExcludeFolders.Add(@"\\nas\jobs\Proofs");
        });
        using var h = Ready(s, Share);
        Assert.Equal("print job", h.Doc(@"\\nas\jobs\a.pdf")!.Body);
        Assert.Null(h.Row(@"\\nas\jobs\Proofs\b.pdf"));
        Assert.DoesNotContain(h.Files.ReadPaths, p => p.Equals(@"\\nas\jobs\Proofs\b.pdf", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Switching_it_off_drops_what_was_read_on_shares_and_keeps_the_rest()
    {
        var s = With(Harness.DefaultSettings(), t => t.ReadNetworkDrives = true);
        using var h = Ready(s, Share);
        Assert.NotNull(h.Row(@"\\nas\jobs\a.pdf"));
        h.Content.Apply(With(s, t => t.ReadNetworkDrives = false));
        h.Settle();
        Assert.Null(h.Row(@"\\nas\jobs\a.pdf"));
        Assert.Null(h.Row(@"\\nas\jobs\Proofs\b.pdf"));
        Assert.Equal("local words", h.Doc(@"C:\Users\Me\local.pdf")!.Body);
    }

    [Fact]
    public void A_share_added_as_a_folder_is_read_whether_the_switch_is_on_or_not()
    {
        var s = With(Harness.DefaultSettings(), t => t.Folders.Add(@"\\nas\jobs"));
        using var h = Ready(s, Share);
        Assert.Equal("print job", h.Doc(@"\\nas\jobs\a.pdf")!.Body);
        int reads = h.Files.Reads;
        // Switching it on covers the same share again: nothing new to read, nothing dropped ...
        h.Content.Apply(With(s, t => t.ReadNetworkDrives = true));
        h.Settle();
        Assert.Equal(reads, h.Files.Reads);
        Assert.NotNull(h.Row(@"\\nas\jobs\a.pdf"));
        // ... and switching it off again keeps the share, which the folder still holds.
        h.Content.Apply(With(s, t => t.ReadNetworkDrives = false));
        h.Settle();
        Assert.Equal(reads, h.Files.Reads);
        Assert.Equal("print job", h.Doc(@"\\nas\jobs\a.pdf")!.Body);
    }
}

/// <summary>The scope's rule for network drives, on its own: share keys are in, other keys aren't, exclusions win.</summary>
public sealed class NetworkDrivesScopeTests
{
    static ContentScope Scope(bool network, params string[] excluded)
    {
        var s = Harness.DefaultSettings();
        s.ReadNetworkDrives = network;
        s.ExcludeFolders.AddRange(excluded);
        return ContentScope.Build(s, allRootsInScope: false, [], f =>
        {
            var (key, prefix, _) = Harness.Resolve(f);
            return (key, prefix);
        });
    }

    [Fact]
    public void Shares_are_in_only_while_network_drives_are_read_inside()
    {
        Assert.Equal(FolderVerdict.Out, Scope(false).RootVerdict(@"unc:\\NAS\JOBS", "", @"\\nas\jobs", out _));
        Assert.Equal(FolderVerdict.In, Scope(true).RootVerdict(@"unc:\\NAS\JOBS", "", @"\\nas\jobs", out _));
        Assert.Equal(FolderVerdict.In, Scope(true).RootVerdict(@"unc:\\NAS\JOBS", "Clients", @"\\nas\jobs", out _));
        Assert.True(Scope(true).InScope(@"unc:\\NAS\JOBS", @"Clients\a.pdf", @"\\nas\jobs", out var kind));
        Assert.Equal(ContentKind.Pdf, kind);
        Assert.False(Scope(false).InScope(@"unc:\\NAS\JOBS", @"Clients\a.pdf", @"\\nas\jobs", out _));
    }

    [Fact]
    public void Local_drives_and_unnamed_mappings_are_not_shares()
    {
        var scope = Scope(true);
        Assert.Equal(FolderVerdict.Out, scope.RootVerdict("path:E:", "", @"E:\", out _));
        Assert.Equal(FolderVerdict.Out, scope.RootVerdict("vol:{6B29FC40-CA47-1067-B31D-00DD010662DA}", "", @"E:\", out _));
        // A mapped letter whose share Windows couldn't name: its root and its stored documents agree (both out).
        Assert.Equal(FolderVerdict.Out, scope.RootVerdict("path:Z:", "", @"Z:\", out _));
        Assert.False(scope.InScope("path:Z:", "a.pdf", @"Z:\", out _));
    }

    [Fact]
    public void Exclusions_and_the_deny_list_still_apply_on_shares()
    {
        var scope = Scope(true, @"\\nas\jobs\Private");
        Assert.False(scope.InScope(@"unc:\\NAS\JOBS", @"Private\a.pdf", @"\\nas\jobs", out _));
        Assert.True(scope.InScope(@"unc:\\NAS\JOBS", @"Public\a.pdf", @"\\nas\jobs", out _));
        Assert.Equal(FolderVerdict.Out, Scope(true, @"\\nas\jobs").RootVerdict(@"unc:\\NAS\JOBS", "", @"\\nas\jobs", out _));
        Assert.False(scope.InScope(@"unc:\\NAS\JOBS", @"$RECYCLE.BIN\a.pdf", @"\\nas\jobs", out _));
    }

    [Fact]
    public void The_scope_fingerprint_changes_with_the_switch_and_only_then()
    {
        Assert.NotEqual(Scope(false).Fingerprint, Scope(true).Fingerprint);
        Assert.Equal(Scope(false).Fingerprint, Scope(false).Fingerprint);
    }
}
