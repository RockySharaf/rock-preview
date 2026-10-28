using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Anything.Core;
using Anything.Core.Indexing;
using Anything.Core.Search;
using static Anything.Tests.Indexing.DriveIndexingTestSupport;

namespace Anything.Tests.Indexing;

/// <summary><see cref="FolderListing"/>: folders the index doesn't hold, read straight from the disk into a private, live index.</summary>
public sealed class FolderListingTests : IDisposable
{
    const int DBT_DEVICEQUERYREMOVE = 0x8001, DBT_DEVICEQUERYREMOVEFAILED = 0x8002, DBT_DEVICEREMOVECOMPLETE = 0x8004;

    readonly string _root;
    readonly string _outside;

    public FolderListingTests()
    {
        _root = Directory.CreateTempSubdirectory("anything-listing-").FullName;
        _outside = Directory.CreateTempSubdirectory("anything-listing-out-").FullName;
        Directory.CreateDirectory(Path.Combine(_root, "sub", "deep"));
        Directory.CreateDirectory(Path.Combine(_root, "skip"));
        Directory.CreateDirectory(Path.Combine(_root, "node_modules", "pkg"));
        File.WriteAllText(Path.Combine(_root, "a.txt"), "hello");
        File.WriteAllText(Path.Combine(_root, "b.tmp"), "excluded by pattern");
        File.WriteAllText(Path.Combine(_root, "sub", "c.txt"), "c");
        File.WriteAllText(Path.Combine(_root, "sub", "deep", "d.txt"), "dd");
        File.WriteAllText(Path.Combine(_root, "skip", "e.txt"), "excluded by folder path");
        File.WriteAllText(Path.Combine(_root, "node_modules", "pkg", "f.txt"), "excluded by folder name");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch { }
        try { Directory.Delete(_outside, true); } catch { }
    }

    /// <summary>
    /// Leaves out the folder's own drive and the folder above it (as a stray exclude_folders=C:\ would), and inside it a
    /// folder by path, folders named node_*, and *.tmp files.
    /// </summary>
    IndexSettings Settings() => new()
    {
        IndexNtfsVolumes = false,
        ExcludeFolders = [Path.GetPathRoot(_root)!, Path.GetDirectoryName(_root)!, Path.Combine(_root, "skip"), "node_*"],
        ExcludePatterns = ["*.tmp"],
    };

    string[] ExpectedRoot(params string[] extra) =>
        Expected(_root, [.. new[] { "a.txt", "sub", @"sub\c.txt", @"sub\deep", @"sub\deep\d.txt" }.Concat(extra)]);

    /// <summary>2000 files in 40 folders under "many", enough that reading them takes a while; their paths, relative.</summary>
    List<string> ManyFiles()
    {
        var many = new List<string>();
        for (int d = 0; d < 40; d++)
            for (int f = 0; f < 50; f++)
            {
                string rel = $@"many\d{d}\f{f}.txt";
                Write(Path.Combine(_root, rel));
                many.Add(rel);
            }
        for (int d = 0; d < 40; d++) many.Add($@"many\d{d}");
        many.Add("many");
        return many;
    }

    static FolderListing Listed(IReadOnlyList<string> folders, IndexSettings settings)
    {
        var listing = new FolderListing(folders, settings);
        listing.Start();
        // Every first read counted and over too: Ready is said just before a read counts itself done.
        Assert.True(WaitFor(() => listing.State != ListingState.Reading && listing.ReadsDone >= listing.Folders.Count
                                  && listing.Indexers.All(r => Volatile.Read(ref r.ScanBusy) == 0)), "still reading");
        return listing;
    }

    static bool Has(FolderListing listing, string path) => Paths(listing.Index).Contains(path, StringComparer.OrdinalIgnoreCase);

    static string[] Search(FolderListing listing, string filter)
    {
        var results = listing.Engine.Search(listing.Index, "", new SearchOptions { Filter = filter }, SortKey.Path, false, CancellationToken.None);
        Assert.Null(results.Error);
        listing.Index.Lock.EnterReadLock();
        try
        {
            return results.Ids.Select(listing.Index.GetFullPath).OrderBy(p => p, StringComparer.OrdinalIgnoreCase).ToArray();
        }
        finally { listing.Index.Lock.ExitReadLock(); }
    }

    /// <summary>No read of the listing's one folder runs.</summary>
    static bool Idle(FolderListing listing) => Volatile.Read(ref listing.Indexers[0].ScanBusy) == 0;

    /// <summary>Whether <paramref name="task"/> ends within <paramref name="ms"/>.</summary>
    static bool Ends(Task task, int ms) => ((IAsyncResult)task).AsyncWaitHandle.WaitOne(ms);

    [Fact]
    public void Lists_every_file_of_a_folder_whose_drive_and_parent_are_left_out()
    {
        var settings = Settings();
        // The main index would leave the whole folder out.
        Assert.True(new ExclusionFilter(settings).ExcludesFolder(_root, Path.GetFileName(_root)));

        using var listing = Listed([_root], settings);
        Assert.Equal(ListingState.Ready, listing.State);
        Assert.Null(listing.Problem);
        Assert.Equal([_root], listing.Folders);
        // Rules about what is inside it still apply: no b.tmp, skip or node_modules.
        Assert.Equal(ExpectedRoot(), Paths(listing.Index));
        Assert.Equal(5, listing.Found);
    }

    [Fact]
    public void Hidden_and_system_rules_apply_inside_the_folder_not_to_it()
    {
        string folder = Path.Combine(_outside, "hidden folder");
        Write(Path.Combine(folder, "shown.txt"));
        Write(Path.Combine(folder, "hidden.txt"));
        Write(Path.Combine(folder, "system.txt"));
        Write(Path.Combine(folder, "inner", "in a hidden folder.txt"));
        Write(Path.Combine(folder, "sub", "deep.txt"));
        File.SetAttributes(Path.Combine(folder, "hidden.txt"), FileAttributes.Hidden);
        File.SetAttributes(Path.Combine(folder, "system.txt"), FileAttributes.System);
        new DirectoryInfo(Path.Combine(folder, "inner")).Attributes |= FileAttributes.Hidden;
        new DirectoryInfo(folder).Attributes |= FileAttributes.Hidden; // the folder listed: read all the same

        var settings = new IndexSettings { IndexNtfsVolumes = false, ExcludeHidden = true, ExcludeSystem = true };
        using var listing = Listed([folder], settings);
        Assert.Equal(ListingState.Ready, listing.State);
        Assert.Equal(Expected(folder, "shown.txt", "sub", @"sub\deep.txt"), Paths(listing.Index));

        // Also for what comes in later: a hidden file moved in, then a plain one (changes are applied in order).
        string hiddenLater = Path.Combine(_outside, "hidden later.txt"), shownLater = Path.Combine(_outside, "shown later.txt");
        Write(hiddenLater);
        Write(shownLater);
        File.SetAttributes(hiddenLater, FileAttributes.Hidden);
        File.Move(hiddenLater, Path.Combine(folder, "hidden later.txt"));
        File.Move(shownLater, Path.Combine(folder, "shown later.txt"));
        Assert.True(WaitFor(() => Has(listing, Path.Combine(folder, "shown later.txt"))), "change not listed");
        Assert.False(Has(listing, Path.Combine(folder, "hidden later.txt")));
    }

    [Fact]
    public void Searches_find_exactly_the_files_in_the_folder()
    {
        using var listing = Listed([_root], Settings());
        string[] files = [Path.Combine(_root, "a.txt"), Path.Combine(_root, "sub", "c.txt"), Path.Combine(_root, "sub", "deep", "d.txt")];
        Assert.Equal(files, Search(listing, $"file: \"{_root}\\\""));
        Assert.Equal([Path.Combine(_root, "a.txt")], Search(listing, $"file: parent:\"{_root}\""));
    }

    [Fact]
    public void Keeps_the_information_the_settings_ask_for()
    {
        var settings = Settings();
        settings.IndexFolderSizes = true;
        settings.IndexDateAccessed = true;
        settings.IndexRecentChanges = false;
        using var listing = new FolderListing([_root], settings);
        listing.Start();
        Assert.True(WaitFor(() => listing.State == ListingState.Ready), $"state {listing.State}");

        Assert.True(listing.Index.HasFolderSizes);
        Assert.True(listing.Index.HasDateAccessed);
        Assert.False(listing.Index.RecordsRecentChanges);
        int sub = Enumerable.Range(0, listing.Index.Count).Single(i => listing.Index.IsAlive(i) && listing.Index.Name(i) == "sub");
        Assert.Equal(3, listing.Index.Size(sub)); // c.txt and d.txt
    }

    [Fact]
    public void Run_history_is_a_copy_that_follows_the_apps_until_Dispose()
    {
        string a = Path.Combine(_root, "a.txt"), c = Path.Combine(_root, "sub", "c.txt");
        var history = new RunHistory();
        history.Increment(a, 100);
        var listing = new FolderListing([_root], Settings(), history);
        listing.Start();
        Assert.True(WaitFor(() => listing.State == ListingState.Ready), $"state {listing.State}");
        var copy = listing.Index.RunHistory;
        Assert.NotSame(history, copy);
        Assert.Equal(new RunHistory.Entry(1, 100), copy.Get(a));

        // Runs counted in the app show in the listing at once, for Run Count and runcount:.
        history.Increment(a, 200);
        history.Set(c, 5, 300);
        Assert.Equal(new RunHistory.Entry(2, 200), copy.Get(a));
        Assert.Equal(5, copy.Get(c).Count);
        var found = listing.Engine.Search(listing.Index, "runcount:5", new SearchOptions(), SortKey.Name, false, CancellationToken.None);
        listing.Index.Lock.EnterReadLock();
        try { Assert.Equal([c], found.Ids.Select(listing.Index.GetFullPath)); }
        finally { listing.Index.Lock.ExitReadLock(); }
        history.Set(c, 0, 0);
        Assert.Equal(0, copy.Get(c).Count);

        // Each index matches its own history: looking up the listing's doesn't make the main index's match every name
        // again (which would sort its Run Count order from scratch).
        var main = new FileIndex { RunHistory = history };
        main.Add("a.txt", main.AddRoot(_root), FileAttributes.Archive, 5, 0, 0);
        long Sequence(FileIndex index)
        {
            index.Lock.EnterReadLock();
            try { return index.RunHistory.Lookup(index).Sequence; }
            finally { index.Lock.ExitReadLock(); }
        }
        long before = Sequence(main);
        Sequence(listing.Index);
        Assert.Equal(before, Sequence(main));

        // Not after Dispose.
        listing.Dispose();
        history.Increment(a, 400);
        Assert.Equal(new RunHistory.Entry(2, 200), copy.Get(a));
    }

    [Fact]
    public void Created_deleted_and_renamed_files_show_after_Changed()
    {
        using var listing = new FolderListing([_root], Settings());
        int changed = 0;
        listing.Changed += () => Interlocked.Increment(ref changed);
        listing.Start();
        Assert.True(WaitFor(() => listing.State == ListingState.Ready), $"state {listing.State}");

        string created = Path.Combine(_root, "sub", "new file.txt");
        int before = Volatile.Read(ref changed);
        File.WriteAllText(created, "12345678");
        Assert.True(WaitFor(() => Volatile.Read(ref changed) > before && Has(listing, created)), "created file not listed");

        string renamed = Path.Combine(_root, "sub", "renamed.txt");
        before = Volatile.Read(ref changed);
        File.Move(created, renamed);
        Assert.True(WaitFor(() => Volatile.Read(ref changed) > before && Has(listing, renamed) && !Has(listing, created)), "rename not applied");

        // Left out by a rule inside the folder, even when it appears later.
        File.WriteAllText(Path.Combine(_root, "live.tmp"), "x");
        string deleted = Path.Combine(_root, "a.txt");
        before = Volatile.Read(ref changed);
        File.Delete(deleted);
        Assert.True(WaitFor(() => Volatile.Read(ref changed) > before && !Has(listing, deleted)), "delete not applied");
        Assert.False(Has(listing, Path.Combine(_root, "live.tmp")));

        Assert.Equal(Expected(_root, "sub", @"sub\c.txt", @"sub\deep", @"sub\deep\d.txt", @"sub\renamed.txt"), Paths(listing.Index));
    }

    [Fact]
    public void State_goes_from_reading_to_ready_with_StateChanged()
    {
        using var listing = new FolderListing([_root], Settings());
        var states = new ConcurrentQueue<ListingState>();
        listing.StateChanged += () => states.Enqueue(listing.State);
        Assert.Equal(ListingState.Reading, listing.State);
        Assert.Equal(0, listing.Found);

        listing.Start();
        Assert.True(WaitFor(() => states.Contains(ListingState.Ready)), $"state {listing.State}");
        Assert.Equal(ListingState.Ready, listing.State);
        Assert.DoesNotContain(ListingState.Unavailable, states);
    }

    [Fact]
    public void Changed_is_raised_at_most_every_150_ms()
    {
        using var listing = new FolderListing([_root], Settings());
        var times = new ConcurrentQueue<long>();
        listing.Changed += () => times.Enqueue(Stopwatch.GetTimestamp());
        var host = (IIndexerHost)listing;

        var sw = Stopwatch.StartNew();
        int marks = 0;
        long last = 0;
        while (sw.ElapsedMilliseconds < 1000)
        {
            last = Stopwatch.GetTimestamp();
            host.MarkChanged();
            marks++;
            Thread.Sleep(1);
        }
        // The last one comes after the burst: waited for, however late a busy machine runs the timer; then a moment in
        // which no other may come.
        SpinWait.SpinUntil(() => times.Any(x => x >= last), 10_000);
        Thread.Sleep(200);

        var t = times.ToArray();
        Assert.True(t.Length >= 2 && t.Length <= 10, $"{t.Length} events for {marks} changes");
        for (int i = 1; i < t.Length; i++)
        {
            var gap = Stopwatch.GetElapsedTime(t[i - 1], t[i]).TotalMilliseconds;
            Assert.True(gap >= 145, $"only {gap:F0} ms between two events");
        }
        // The burst's last change is not lost: an event comes after it.
        Assert.True(t[^1] >= last, "no event after the last change");
    }

    [Fact]
    public void Rescan_during_the_first_read_reads_again_after_it()
    {
        var expected = ExpectedRoot([.. ManyFiles()]);
        using var listing = new FolderListing([_root], Settings());
        // Asked for from inside the first read, as its first entries are added: always while that read runs.
        int asked = 0, stateWhenAsked = -1;
        listing.Index.Changed += () =>
        {
            if (Interlocked.Exchange(ref asked, 1) != 0) return;
            Volatile.Write(ref stateWhenAsked, (int)listing.State);
            listing.Rescan();
        };
        listing.Start();
        // One more read follows the first, and its copy replaces the first one with every entry once: a rescan swapping
        // the folder out from under the first read would lose what that read adds after it.
        Assert.True(WaitFor(() => listing.State == ListingState.Ready && listing.ReadsDone >= 2 && Idle(listing)
                                  && Paths(listing.Index).SequenceEqual(expected), 20_000),
            $"state {listing.State}, {listing.ReadsDone} reads, {listing.Index.LiveCount} live, expected {expected.Length}");
        Assert.Equal((int)ListingState.Reading, Volatile.Read(ref stateWhenAsked));
        Assert.Equal(2, listing.ReadsDone);
        Assert.Equal(expected.Length - 1, listing.Found);
    }

    [Fact]
    public void Rescans_leave_no_removed_slots_behind()
    {
        for (int i = 0; i < 50; i++) Write(Path.Combine(_root, "many", $"f{i}.txt"));
        var expected = ExpectedRoot([.. Enumerable.Range(0, 50).Select(i => $@"many\f{i}.txt"), "many"]);
        using var listing = Listed([_root], Settings());
        long generation = listing.Index.Generation;

        // Each rescan swaps in a new copy, leaving the old one as removed slots: reclaimed once they outnumber the
        // entries, so they never pile up (without that, 2 copies' worth after the second rescan, 6 after the sixth).
        for (int n = 1; n <= 6; n++)
        {
            int reads = listing.ReadsDone;
            listing.Rescan();
            Assert.True(WaitFor(() => listing.ReadsDone > reads), $"rescan {n} did not read");
            Assert.True(WaitFor(() => listing.Index.DeadCount <= expected.Length),
                $"{listing.Index.DeadCount} removed slots for {listing.Index.LiveCount} entries after {n} rescans");
        }
        Assert.True(listing.Index.Generation > generation, "never compacted");
        Assert.Equal(expected, Paths(listing.Index));
        Assert.Equal(expected.Length - 1, listing.Found);

        // The folder's ids were renumbered with it: changes and searches still find the right entries.
        string added = Path.Combine(_root, "sub", "added.txt"), gone = Path.Combine(_root, "many", "f0.txt");
        File.WriteAllText(added, "x");
        File.Delete(gone);
        Assert.True(WaitFor(() => Has(listing, added) && !Has(listing, gone)), "change not applied after compacting");
        Assert.Equal([added, Path.Combine(_root, "sub", "c.txt")], Search(listing, $"file: parent:\"{Path.Combine(_root, "sub")}\""));
    }

    [Fact]
    public void Missing_folder_is_unavailable_with_the_reason()
    {
        string missing = Path.Combine(_outside, "not-there");
        using var listing = new FolderListing([missing], Settings());
        int stateChanges = 0;
        listing.StateChanged += () => Interlocked.Increment(ref stateChanges);
        listing.Start();

        Assert.True(WaitFor(() => listing.State == ListingState.Unavailable), $"state {listing.State}");
        Assert.Equal(new Win32Exception(3).Message, listing.Problem);
        Assert.True(Volatile.Read(ref stateChanges) > 0);
        // Not shown as an empty folder.
        Assert.Equal(0, listing.Index.LiveCount);
        Assert.Equal(0, listing.Found);

        // Once it is there, a rescan reads it.
        Write(Path.Combine(missing, "x.txt"));
        listing.Rescan();
        Assert.True(WaitFor(() => listing.State == ListingState.Ready), $"state {listing.State}");
        Assert.Null(listing.Problem);
        Assert.Equal(Expected(missing, "x.txt"), Paths(listing.Index));
    }

    [Fact]
    public void Trying_a_missing_folder_again_reads_for_as_long_as_it_takes()
    {
        string missing = Path.Combine(_outside, "not-there");
        using var listing = Listed([missing], Settings());
        Assert.Equal(ListingState.Unavailable, listing.State);
        var states = new ConcurrentQueue<ListingState>();
        listing.StateChanged += () => states.Enqueue(listing.State);

        listing.Rescan();
        Assert.True(WaitFor(() => states.Count >= 2), $"states {string.Join(", ", states)}");
        // Reading while it asks (a share that's gone takes long to answer), then why again.
        Assert.Equal([ListingState.Reading, ListingState.Unavailable], states.Take(2));
        Assert.Equal(new Win32Exception(3).Message, listing.Problem);
    }

    [Fact]
    public void Missing_folder_is_read_by_the_next_check_once_it_is_there()
    {
        string missing = Path.Combine(_outside, "not-there");
        using var listing = Listed([missing], Settings());
        Assert.Equal(ListingState.Unavailable, listing.State);

        Write(Path.Combine(missing, "x.txt"));
        listing.CheckRootsNow();
        Assert.True(WaitFor(() => listing.State == ListingState.Ready), $"state {listing.State}");
        Assert.Null(listing.Problem);
        Assert.Equal(Expected(missing, "x.txt"), Paths(listing.Index));
    }

    [Fact]
    public void Local_folder_deleted_after_its_read_says_why_and_is_read_again_once_back()
    {
        string doomed = Path.Combine(_outside, "doomed");
        Write(Path.Combine(doomed, "x.txt"));
        Write(Path.Combine(doomed, "sub", "y.txt"));
        using var listing = Listed([doomed], Settings());
        Assert.Equal(ListingState.Ready, listing.State);

        // Its watcher sees it go: read again, it is gone, which it says in Windows' words.
        Assert.True(WaitFor(() => TryDelete(doomed)), "could not delete the folder");
        // That read over too: Unavailable is said just before it ends, and a check skips a folder being read.
        Assert.True(WaitFor(() => listing.State == ListingState.Unavailable && Idle(listing)), $"state {listing.State}");
        Assert.Equal(new Win32Exception(3 /* ERROR_PATH_NOT_FOUND */).Message, listing.Problem);

        // Back, with another file: the next check reads it.
        Write(Path.Combine(doomed, "z.txt"));
        listing.CheckRootsNow();
        Assert.True(WaitFor(() => listing.State == ListingState.Ready && Has(listing, Path.Combine(doomed, "z.txt"))),
            $"state {listing.State}, problem {listing.Problem}");
        Assert.Null(listing.Problem);
        Assert.Equal(Expected(doomed, "z.txt"), Paths(listing.Index));
    }

    [Fact]
    public void Folder_gone_while_its_first_read_runs_says_why()
    {
        string doomed = Path.Combine(_outside, "doomed");
        Write(Path.Combine(doomed, "x.txt"));
        Write(Path.Combine(doomed, "sub", "y.txt"));
        using var listing = new FolderListing([doomed], Settings());
        var states = new ConcurrentQueue<ListingState>();
        listing.StateChanged += () => states.Enqueue(listing.State);
        // Deleted once the read has found it and listed its top folder, before it reads what is below.
        int deleted = 0;
        bool gone = false;
        listing.Index.Changed += () =>
        {
            if (Interlocked.Exchange(ref deleted, 1) != 0) return;
            Volatile.Write(ref gone, WaitFor(() => TryDelete(doomed), 5000));
        };
        listing.Start();

        Assert.True(WaitFor(() => listing.State == ListingState.Unavailable), $"state {listing.State}, problem {listing.Problem}");
        Assert.True(Volatile.Read(ref gone), "could not delete the folder");
        Assert.NotNull(listing.Problem);
        // Never shown as read and fine: it says the folder can't be read instead.
        Assert.DoesNotContain(ListingState.Ready, states);
    }

    [Fact]
    public void Share_that_went_away_keeps_what_was_read_and_is_read_again_once_back()
    {
        string share = Path.Combine(_outside, "share"), away = share + "-away";
        Write(Path.Combine(share, "a.txt"));
        Write(Path.Combine(share, "sub", "b.txt"));
        using var listing = new FolderListing([share], Settings(), null, devices: null, kindOf: _ => DriveKind.Network);
        listing.Start();
        Assert.True(WaitFor(() => listing.State == ListingState.Ready), $"state {listing.State}");
        var root = listing.Indexers[0];
        Assert.True(root.IsNetwork);

        // The share answers, but not with the folder: as for a server that doesn't answer, what was read stays.
        Assert.True(WaitFor(() => TryMove(share, away)), "could not move the folder away");
        File.WriteAllText(share, "not a folder");
        listing.Rescan();
        // That read over too, so the check below doesn't skip the folder as one being read.
        Assert.True(WaitFor(() => listing.State == ListingState.Unavailable && Idle(listing)), $"state {listing.State}");
        Assert.Equal(RootStatus.Offline, root.Status);
        // In Windows' words, as for a folder never read.
        Assert.Equal(new Win32Exception(267 /* ERROR_DIRECTORY */).Message, listing.Problem);
        Assert.Equal(Expected(share, "a.txt", "sub", @"sub\b.txt"), Paths(listing.Index));
        Assert.Equal(3, listing.Found); // Unavailable, yet holding what it had

        // Back, with a file more: the next check reads it again.
        File.Delete(share);
        Assert.True(WaitFor(() => TryMove(away, share)), "could not move the folder back");
        Write(Path.Combine(share, "c.txt"));
        listing.CheckRootsNow();
        Assert.True(WaitFor(() => listing.State == ListingState.Ready && Has(listing, Path.Combine(share, "c.txt"))),
            $"state {listing.State}, problem {listing.Problem}");
        Assert.Null(listing.Problem);
        Assert.Equal(Expected(share, "a.txt", "c.txt", "sub", @"sub\b.txt"), Paths(listing.Index));
    }

    /// <summary>Moves a folder, false while something (a virus scanner, say) holds it.</summary>
    static bool TryMove(string from, string to)
    {
        try { Directory.Move(from, to); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>Deletes a folder and what is in it, false while something holds it.</summary>
    static bool TryDelete(string folder)
    {
        try { Directory.Delete(folder, true); return true; }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    [Fact]
    public void Share_is_read_again_once_its_last_read_is_older_than_the_rescan_interval()
    {
        Write(Path.Combine(_outside, "a.txt"));
        var settings = Settings();
        settings.NetworkRescanMinutes = 5;
        using var share = new FolderListing([_outside], settings, null, devices: null, kindOf: _ => DriveKind.Network);
        using var local = new FolderListing([_root], settings, null, devices: null, kindOf: _ => DriveKind.Fixed);
        share.Start();
        local.Start();
        Assert.True(WaitFor(() => share.State == ListingState.Ready && local.State == ListingState.Ready), "still reading");
        // Ready is said just before the read counts itself done: a check before then would skip the busy folder.
        Assert.True(WaitFor(() => share.ReadsDone == 1 && Idle(share) && local.ReadsDone == 1 && Idle(local)), "first reads not finished");
        var lastScan = typeof(RootIndexer).GetProperty(nameof(RootIndexer.LastScanUtc));
        Assert.NotNull(lastScan);

        // Read less than five minutes ago: not yet.
        share.CheckRootsNow();
        Assert.True(Idle(share));
        Assert.Equal(1, share.ReadsDone);

        // Six minutes ago: its change notifications may have missed something, so it is read again. A local folder is
        // left to its watcher.
        long sixMinutesAgo = DateTime.UtcNow.AddMinutes(-6).ToFileTimeUtc();
        lastScan.SetValue(share.Indexers[0], sixMinutesAgo);
        lastScan.SetValue(local.Indexers[0], sixMinutesAgo);
        share.CheckRootsNow();
        local.CheckRootsNow();
        Assert.True(Idle(local));
        Assert.True(WaitFor(() => share.ReadsDone == 2 && Idle(share)), $"{share.ReadsDone} reads");
        Assert.True(share.Indexers[0].LastScanUtc > sixMinutesAgo);
        Assert.Equal(1, local.ReadsDone);
    }

    [Fact]
    public void Watcher_turned_off_after_repeated_errors_is_watched_again_once_its_pause_is_over()
    {
        using var listing = Listed([_root], Settings());
        var root = listing.Indexers[0];
        var onError = typeof(FolderIndexer).GetMethod("OnWatcherError", BindingFlags.NonPublic | BindingFlags.Instance);
        var retryAfter = typeof(FolderIndexer).GetField("_watcherRetryAfter", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(onError);
        Assert.NotNull(retryAfter);

        // Two errors close together (a burst of changes overflowing its buffer, twice): each reads the folder again, and
        // the second turns the watcher off for ten minutes.
        for (int i = 0; i < 2; i++)
        {
            int reads = listing.ReadsDone;
            onError.Invoke(root, [new InternalBufferOverflowException()]);
            Assert.True(WaitFor(() => listing.ReadsDone > reads && Idle(listing)), "not read again after a watcher error");
        }
        Assert.False(root.WatcherDue);
        listing.CheckRootsNow();
        Assert.True(Idle(listing)); // still paused: nothing to do

        // The pause is over: the next check reads it again, which watches it again.
        retryAfter.SetValue(root, Environment.TickCount64 - 1);
        Assert.True(root.WatcherDue);
        int before = listing.ReadsDone;
        listing.CheckRootsNow();
        Assert.True(WaitFor(() => listing.ReadsDone > before && Idle(listing)), "not read again once the pause was over");
        Assert.False(root.WatcherDue);

        string late = Path.Combine(_root, "late.txt");
        File.WriteAllText(late, "x");
        Assert.True(WaitFor(() => Has(listing, late)), "a change after the pause was not listed");
        Assert.Equal(before + 1, listing.ReadsDone); // seen by the watcher, not by another read
        Assert.Equal(ListingState.Ready, listing.State);
    }

    [Fact]
    public void One_missing_folder_of_two_leaves_the_other_listed()
    {
        string missing = Path.Combine(_outside, "not-there");
        using var listing = Listed([missing, _root], Settings());
        Assert.Equal(ListingState.Ready, listing.State);
        Assert.Equal(new Win32Exception(3).Message, listing.Problem);
        Assert.Equal(ExpectedRoot(), Paths(listing.Index));
    }

    [Fact]
    public void Nested_and_repeated_folders_are_listed_once()
    {
        string sub = Path.Combine(_root, "sub");
        using var listing = Listed([sub, _root, Path.Combine(sub, "deep") + "\\", _root.ToUpperInvariant() + "\\", "  "], Settings());
        Assert.Equal([_root], listing.Folders);
        var paths = Paths(listing.Index);
        Assert.Equal(paths.Length, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Equal(ExpectedRoot(), paths);

        Write(Path.Combine(_outside, "o.txt"));
        using var two = Listed([_root, _outside], Settings());
        Assert.Equal([_root, _outside], two.Folders);
        Assert.Equal(ExpectedRoot().Concat(Expected(_outside, "o.txt")).OrderBy(p => p, StringComparer.OrdinalIgnoreCase), Paths(two.Index));
    }

    [Fact]
    public void Needs_a_folder()
    {
        Assert.Throws<ArgumentException>(() => new FolderListing(["", " "], Settings()));
    }

    [Fact]
    public void Drive_kind_comes_from_the_path_without_asking_a_server()
    {
        // \\?\ paths are local, not shares.
        using var local = new FolderListing([@"\\?\" + _root], Settings());
        Assert.False(local.Indexers[0].IsNetwork);
        Assert.Equal(Drives.KindOf(Path.GetPathRoot(_root)!), local.Indexers[0].DriveKind);

        // A share is on the network from its name alone: nothing is read before Start.
        using var share = new FolderListing([@"\\no-such-server.invalid\share\folder"], Settings());
        Assert.True(share.Indexers[0].IsNetwork);
        Assert.Equal(DriveKind.Network, share.Indexers[0].DriveKind);
    }

    [Fact]
    public void After_Dispose_no_events_and_Rescan_does_nothing()
    {
        var listing = new FolderListing([_root], Settings());
        int events = 0;
        listing.Changed += () => Interlocked.Increment(ref events);
        listing.StateChanged += () => Interlocked.Increment(ref events);
        listing.Start();
        // The first read counted and over too: Ready is said just before it ends.
        Assert.True(WaitFor(() => listing.State == ListingState.Ready && listing.ReadsDone == 1 && Idle(listing)), $"state {listing.State}");

        var sw = Stopwatch.StartNew();
        listing.Dispose();
        Assert.True(sw.ElapsedMilliseconds < 1000, $"Dispose took {sw.ElapsedMilliseconds} ms");
        int after = Volatile.Read(ref events);
        listing.Dispose();
        // The folder's monitor stops in the background: from then on nothing reads the folder or changes the index.
        Assert.NotNull(listing.Stopped);
        Assert.True(Ends(listing.Stopped, 10_000), "the folder's monitor did not stop");

        long version = listing.Index.Version;
        File.WriteAllText(Path.Combine(_root, "after.txt"), "x");
        File.Delete(Path.Combine(_root, "a.txt"));
        ((IIndexerHost)listing).MarkChanged();
        listing.Rescan();
        listing.Start();
        listing.CheckRootsNow();
        Assert.True(Idle(listing)); // no read started
        // Long enough for a watcher to have seen the changes and an event to have come.
        Assert.False(WaitFor(() => Volatile.Read(ref events) != after || listing.Index.Version != version, 1000), "changed after Dispose");
        Assert.Equal(1, listing.ReadsDone);
        Assert.Equal(ListingState.Ready, listing.State);
    }

    [Fact]
    public void Rescan_before_Start_does_nothing_and_a_second_Start_reads_nothing_more()
    {
        using var listing = new FolderListing([_root], Settings());
        listing.Rescan();
        listing.CheckRootsNow();
        Assert.False(WaitFor(() => listing.ReadsDone != 0 || !Idle(listing), 300), "read before Start");
        Assert.Equal(ListingState.Reading, listing.State);

        listing.Start();
        listing.Start();
        Assert.True(WaitFor(() => listing.State == ListingState.Ready && listing.ReadsDone == 1 && Idle(listing)), $"state {listing.State}");
        Assert.False(WaitFor(() => listing.ReadsDone != 1, 300), "read again");
        Assert.Equal(ExpectedRoot(), Paths(listing.Index));
    }

    [Fact]
    public void Dispose_waits_for_a_handler_already_running_and_none_runs_after()
    {
        var listing = new FolderListing([_root], Settings());
        using var entered = new ManualResetEventSlim();
        using var proceed = new ManualResetEventSlim();
        using var disposed = new ManualResetEventSlim();
        int calls = 0;
        bool disposedWhileRunning = true;
        listing.Changed += () =>
        {
            if (Interlocked.Increment(ref calls) != 1) return;
            entered.Set();
            proceed.Wait(10_000);
            Volatile.Write(ref disposedWhileRunning, disposed.IsSet);
        };
        ((IIndexerHost)listing).MarkChanged();
        Assert.True(entered.Wait(10_000), "Changed was not raised");
        // A second change while the handler runs: its raise comes 150 ms on and waits behind the running handler.
        ((IIndexerHost)listing).MarkChanged();
        Thread.Sleep(250);

        // On a thread of its own, known to have begun before the handler is let go: a pool thread can start late on a busy
        // machine, and the waiting raise, let in before Dispose began, would then rightly call the handler.
        using var disposing = new ManualResetEventSlim();
        var dispose = new Thread(() =>
        {
            disposing.Set();
            listing.Dispose();
            disposed.Set();
        }) { IsBackground = true };
        dispose.Start();
        Assert.True(disposing.Wait(10_000), "Dispose never began");
        Assert.False(disposed.Wait(300), "Dispose returned while a handler ran");
        proceed.Set();
        Assert.True(disposed.Wait(10_000), "Dispose did not return");
        Assert.False(Volatile.Read(ref disposedWhileRunning));

        // The raise that was waiting gets the gate now, after Dispose: it must not call the handler.
        Thread.Sleep(300);
        Assert.Equal(1, Volatile.Read(ref calls));
    }

    [Fact]
    public void Leaves_the_index_and_its_engine_alone()
    {
        // The index the app searches, searched, with its path and sort caches built. Its entries are made by hand: a folder
        // of its own would change it now and then (NTFS updates a folder's dates in its parent lazily, which its watcher sees).
        using var manager = Manager(() => []);
        manager.Start(new IndexSettings { IndexNtfsVolumes = false, DatabasePath = Path.Combine(_outside, "test.db") });
        WaitUntilSettled(manager);
        int top = manager.Index.AddRoot(@"C:\elsewhere");
        int sub = manager.Index.Add("sub", top, FileAttributes.Directory, -1, 0, 0);
        manager.Index.Add("kept.txt", top, FileAttributes.Archive, 1, 0, 0);
        manager.Index.Add("other.txt", sub, FileAttributes.Archive, 2, 0, 0);
        var engine = new SearchEngine { SortCacheThreshold = 0 };
        int[] All() => engine.Search(manager.Index, "", new SearchOptions(), SortKey.Name, false, CancellationToken.None).Ids;
        var found = All();
        Assert.Equal(4, found.Length);
        var pathCache = Field(engine, "_paths");
        var sortCache = Field(engine, "_order");
        var sortOrder = Field(sortCache, "_order");
        long version = manager.Index.Version, generation = manager.Index.Generation;

        // Listings made by that manager (they share its run history), reading, changing and read again.
        IIndexManager im = manager;
        using var first = im.CreateListing([_root], Settings());
        using var second = im.CreateListing([_root], Settings());
        first.Start();
        second.Start();
        Assert.True(WaitFor(() => first.State == ListingState.Ready && second.State == ListingState.Ready), "still reading");
        string more = Path.Combine(_root, "more.txt");
        File.WriteAllText(more, "x");
        Assert.True(WaitFor(() => Has(first, more) && Has(second, more)), "change not listed");
        first.Rescan();
        Assert.True(WaitFor(() => first.ReadsDone == 2 && Idle(first)), "not read again");
        Assert.Equal(ExpectedRoot("more.txt"), Paths(first.Index));
        Assert.Equal(ExpectedRoot("more.txt"), Paths(second.Index));
        Assert.Equal(Path.Combine(_root, "more.txt"), Assert.Single(Search(first, $"file: parent:\"{_root}\" more")));

        Assert.NotSame(first.Index, second.Index);
        Assert.NotSame(first.Engine, second.Engine);
        Assert.NotSame(engine, first.Engine);
        Assert.Equal(version, manager.Index.Version);
        Assert.Equal(generation, manager.Index.Generation);
        Assert.Equal(found, All());
        // The same caches, not built again.
        Assert.Same(pathCache, Field(engine, "_paths"));
        Assert.Same(sortCache, Field(engine, "_order"));
        Assert.Same(sortOrder, Field(sortCache, "_order"));
    }

    static object Field(object owner, string name)
    {
        var field = owner.GetType().GetField(name, BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(field);
        var value = field.GetValue(owner);
        Assert.NotNull(value);
        return value;
    }

    // ---- drives being ejected ----

    [Fact]
    public void Eject_during_the_first_read_lets_go_of_the_drive_and_a_failed_eject_reads_it_again()
    {
        var many = ManyFiles();
        using var devices = new DeviceWatcher();
        using var listing = new FolderListing([_root], Settings(), null, devices);
        var root = listing.Indexers[0];
        // Windows asks to let go while the first read runs (just as its first entries are added), and that read goes on
        // only once it has been told to stop.
        int asked = 0;
        Task? eject = null;
        listing.Index.Changed += () =>
        {
            if (Interlocked.Exchange(ref asked, 1) != 0) return;
            var watch = (DeviceWatcher.Watch)listing.RemovalWatch(0)!;
            Volatile.Write(ref eject, Task.Run(() => Send(devices, watch.Notify, DBT_DEVICEQUERYREMOVE)));
            SpinWait.SpinUntil(() => root.Lifetime.IsCancellationRequested, 10_000);
        };
        listing.Start();
        // Watched before it is read (set up off the caller's thread).
        Assert.True(WaitFor(() => listing.RemovalWatch(0) != null), "no eject watch");
        var first = Assert.IsType<DeviceWatcher.Watch>(listing.RemovalWatch(0));
        Assert.NotEqual(0, first.Notify);
        Assert.True(WaitFor(() => Volatile.Read(ref eject) != null), "the first read did not begin");
        Assert.True(Ends(eject!, 10_000), "letting go did not finish");

        // Let go of: the read stopped partway, and the folder is no longer watched or read. It says it can't be read
        // (not Reading for the minute until it is taken back), keeping what was read.
        Assert.True(root.Released);
        Assert.True(Idle(listing));
        Assert.Equal(0, listing.ReadsDone);
        Assert.Equal(ListingState.Unavailable, listing.State);
        Assert.Equal(new Win32Exception(21 /* ERROR_NOT_READY */).Message, listing.Problem);
        Assert.InRange(listing.Index.LiveCount, 1, many.Count - 1);
        listing.Rescan();
        listing.CheckRootsNow();
        Assert.True(Idle(listing));
        Assert.True(root.Released);

        // Windows says the eject didn't happen: watched again, and read in full with what changed meanwhile.
        Write(Path.Combine(_root, "meanwhile.txt"));
        Send(devices, first.Notify, DBT_DEVICEQUERYREMOVEFAILED);
        var expected = ExpectedRoot([.. many, "meanwhile.txt"]);
        Assert.True(WaitFor(() => listing.State == ListingState.Ready && Paths(listing.Index).SequenceEqual(expected), 20_000),
            $"state {listing.State}, {listing.Index.LiveCount} live, expected {expected.Length}");
        Assert.False(root.Released);
        Assert.Equal(0, first.Notify);
        var second = Assert.IsType<DeviceWatcher.Watch>(listing.RemovalWatch(0));
        Assert.NotSame(first, second);
        Assert.NotEqual(0, second.Notify);

        // Let go of for the next eject too; the watch ends with the listing.
        Send(devices, second.Notify, DBT_DEVICEQUERYREMOVE);
        Assert.True(root.Released);
        listing.Dispose();
        Assert.Equal(0, second.Notify);
    }

    [Fact]
    public void Drive_let_go_of_for_an_eject_that_went_through_is_taken_back_a_minute_later()
    {
        using var devices = new DeviceWatcher();
        using var listing = new FolderListing([_root], Settings(), null, devices);
        listing.Start();
        Assert.True(WaitFor(() => listing.State == ListingState.Ready), $"state {listing.State}");
        var root = listing.Indexers[0];
        var watch = Assert.IsType<DeviceWatcher.Watch>(listing.RemovalWatch(0));

        var states = new ConcurrentQueue<ListingState>();
        listing.StateChanged += () => states.Enqueue(listing.State);
        Send(devices, watch.Notify, DBT_DEVICEQUERYREMOVE);
        Send(devices, watch.Notify, DBT_DEVICEREMOVECOMPLETE); // gone: Windows ends the watch
        Assert.True(root.Released);
        Assert.Equal(0, watch.Notify);
        listing.CheckRootsNow();
        Assert.True(root.Released); // left alone for a minute

        // Its drive is gone: the listing says so, and keeps showing what was read.
        Assert.Equal(ListingState.Unavailable, listing.State);
        Assert.Equal(new Win32Exception(21 /* ERROR_NOT_READY */).Message, listing.Problem);
        Assert.Equal([ListingState.Unavailable], states);
        Assert.Equal(ExpectedRoot(), Paths(listing.Index));
        Assert.Equal(5, listing.Found);

        // A minute on (the drive plugged back in, say): watched again, and read again with what changed meanwhile.
        string meanwhile = Path.Combine(_root, "meanwhile.txt");
        Write(meanwhile);
        root.ReleasedAtTicks = Environment.TickCount64 - 61_000;
        listing.CheckRootsNow();
        Assert.False(root.Released);
        Assert.True(WaitFor(() => listing.ReadsDone == 2 && Has(listing, meanwhile) && listing.State == ListingState.Ready),
            $"{listing.ReadsDone} reads, state {listing.State}");
        Assert.Null(listing.Problem);
        Assert.NotEqual(0, Assert.IsType<DeviceWatcher.Watch>(listing.RemovalWatch(0)).Notify);
    }

    [Fact]
    public void Eject_that_does_not_happen_leaves_a_read_folder_as_it_was_and_reads_it_again_quietly()
    {
        using var devices = new DeviceWatcher();
        using var listing = new FolderListing([_root], Settings(), null, devices);
        listing.Start();
        Assert.True(WaitFor(() => listing.State == ListingState.Ready && listing.ReadsDone == 1 && Idle(listing)), $"state {listing.State}");
        var root = listing.Indexers[0];
        var watch = Assert.IsType<DeviceWatcher.Watch>(listing.RemovalWatch(0));
        var states = new ConcurrentQueue<(ListingState State, string? Problem)>();
        listing.StateChanged += () => states.Enqueue((listing.State, listing.Problem));

        // Windows asks, then says the drive stays (a program has a file open on it, or a lock is over): the folder is as
        // it was, and read again for what changed meanwhile without saying Reading, or that the drive isn't ready, while
        // a drive that never left is read.
        Send(devices, watch.Notify, DBT_DEVICEQUERYREMOVE);
        Assert.True(root.Released);
        string meanwhile = Path.Combine(_root, "meanwhile.txt");
        Write(meanwhile);
        Send(devices, watch.Notify, DBT_DEVICEQUERYREMOVEFAILED);
        Assert.True(WaitFor(() => listing.ReadsDone >= 2 && Idle(listing) && Has(listing, meanwhile)),
            $"{listing.ReadsDone} reads, state {listing.State}");
        Assert.False(root.Released);
        Assert.Equal([(ListingState.Unavailable, new Win32Exception(21 /* ERROR_NOT_READY */).Message), (ListingState.Ready, null)], states);
        Assert.Equal(ListingState.Ready, listing.State);
        Assert.Null(listing.Problem);
        Assert.Equal(ExpectedRoot("meanwhile.txt"), Paths(listing.Index));
        Assert.NotEqual(0, Assert.IsType<DeviceWatcher.Watch>(listing.RemovalWatch(0)).Notify);
    }

    /// <summary>
    /// Another drive being let go of, which keeps the device thread busy for two seconds (an index root waiting for its
    /// scan, say). Ends once that is over.
    /// </summary>
    static Task Busy(DeviceWatcher devices, string other, ManualResetEventSlim entered)
    {
        Directory.CreateDirectory(other);
        var watch = Assert.IsType<DeviceWatcher.Watch>(devices.WatchRemoval(other, () => { entered.Set(); Thread.Sleep(2000); }, () => { }));
        var eject = Task.Run(() => Send(devices, watch.Notify, DBT_DEVICEQUERYREMOVE));
        Assert.True(entered.Wait(5000), "the other drive's release did not start");
        return eject.ContinueWith(_ => watch.Dispose(), TaskScheduler.Default);
    }

    [Fact]
    public void Rescan_before_the_folder_is_watched_waits_for_its_first_read()
    {
        using var devices = new DeviceWatcher();
        using var listing = new FolderListing([_root], Settings(), null, devices);
        // Whether the folder was watched for an eject when its read added the first entries.
        int first = 0;
        bool watched = false;
        listing.Index.Changed += () =>
        {
            if (Interlocked.Exchange(ref first, 1) != 0) return;
            Volatile.Write(ref watched, listing.RemovalWatch(0) is DeviceWatcher.Watch { Notify: not 0 });
        };
        using (var entered = new ManualResetEventSlim())
        {
            var busy = Busy(devices, Path.Combine(_outside, "other"), entered);
            listing.Start();
            // A refresh while Start waits for the device thread to watch the folder: a read now would hold the drive
            // with nothing to let go of it for an eject.
            listing.Rescan();
            listing.CheckRootsNow();
            Assert.False(WaitFor(() => listing.ReadsDone != 0 || !Idle(listing), 500), "read before it was watched");
            Assert.True(Ends(busy, 10_000), "the other drive's release did not end");
        }

        // Watched, then read once: the refresh is part of the first read.
        Assert.True(WaitFor(() => listing.State == ListingState.Ready && listing.ReadsDone == 1 && Idle(listing)), $"state {listing.State}");
        Assert.True(Volatile.Read(ref watched), "read before it was watched");
        Assert.False(WaitFor(() => listing.ReadsDone != 1, 300), "read again");
        Assert.Equal(ExpectedRoot(), Paths(listing.Index));
    }

    [Fact]
    public void Timer_check_is_skipped_while_the_last_one_runs()
    {
        string missing = Path.Combine(_outside, "not-there");
        using var listing = Listed([missing], Settings());
        Assert.Equal(ListingState.Unavailable, listing.State);
        Write(Path.Combine(missing, "x.txt")); // there now: a check reads it
        var gate = Field(listing, "_checkGate");
        var checkIfIdle = typeof(FolderListing).GetMethod("CheckIfIdle", BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.NotNull(checkIfIdle);

        // A check stuck on a drive that stopped answering, holding the gate.
        using var held = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var stuck = new Thread(() =>
        {
            lock (gate)
            {
                held.Set();
                release.Wait(10_000);
            }
        }) { IsBackground = true };
        stuck.Start();
        try
        {
            Assert.True(held.Wait(5000), "the gate was not taken");

            // The timer's next check returns at once, without waiting for the gate or reading anything.
            var sw = Stopwatch.StartNew();
            checkIfIdle.Invoke(listing, null);
            Assert.True(sw.ElapsedMilliseconds < 1000, $"the check took {sw.ElapsedMilliseconds} ms");
            Assert.False(WaitFor(() => listing.ReadsDone != 1 || !Idle(listing), 300), "read while the last check ran");
            Assert.Equal(ListingState.Unavailable, listing.State);

            // Looking now still looks: it waits for the check running, then does its own.
            var now = Task.Run(listing.CheckRootsNow);
            Assert.False(Ends(now, 300), "looked without waiting for the check running");
            release.Set();
            Assert.True(Ends(now, 10_000), "the look did not end");
        }
        finally
        {
            release.Set();
            stuck.Join(10_000);
        }
        Assert.True(WaitFor(() => listing.State == ListingState.Ready), $"state {listing.State}");
        Assert.Equal(Expected(missing, "x.txt"), Paths(listing.Index));

        // With no check running, the timer's check looks as that one does.
        string other = Path.Combine(_outside, "not-there-either");
        using var second = Listed([other], Settings());
        Assert.Equal(ListingState.Unavailable, second.State);
        Write(Path.Combine(other, "y.txt"));
        checkIfIdle.Invoke(second, null);
        Assert.True(WaitFor(() => second.State == ListingState.Ready), $"state {second.State}");
        Assert.Equal(Expected(other, "y.txt"), Paths(second.Index));
    }

    [Fact]
    public void Start_and_Dispose_return_at_once_while_the_device_thread_lets_go_of_another_drive()
    {
        using var devices = new DeviceWatcher();
        string other = Path.Combine(_outside, "other");

        var listing = new FolderListing([_root], Settings(), null, devices);
        try
        {
            using (var entered = new ManualResetEventSlim())
            {
                var busy = Busy(devices, other, entered);
                var sw = Stopwatch.StartNew();
                listing.Start();
                Assert.True(sw.ElapsedMilliseconds < 1000, $"Start took {sw.ElapsedMilliseconds} ms");
                // Watched, then read, once the device thread is free.
                Assert.True(Ends(busy, 10_000), "the other drive's release did not end");
                Assert.True(WaitFor(() => listing.State == ListingState.Ready && listing.RemovalWatch(0) is DeviceWatcher.Watch { Notify: not 0 }),
                    $"state {listing.State}");
            }
            Assert.Equal(ExpectedRoot(), Paths(listing.Index));

            // Its drive went away and came back (Windows ended the watch): the next check watches it again, waiting for the
            // busy device thread. Dispose doesn't wait behind that, and the watch it then gets is thrown away.
            var ended = Assert.IsType<DeviceWatcher.Watch>(listing.RemovalWatch(0));
            Send(devices, ended.Notify, DBT_DEVICEREMOVECOMPLETE);
            Assert.Equal(0, ended.Notify);
            using (var entered = new ManualResetEventSlim())
            {
                var busy = Busy(devices, other, entered);
                var check = Task.Run(listing.CheckRootsNow);
                Thread.Sleep(200); // the check is waiting for the device thread now
                var sw = Stopwatch.StartNew();
                listing.Dispose();
                Assert.True(sw.ElapsedMilliseconds < 1000, $"Dispose took {sw.ElapsedMilliseconds} ms");
                Assert.True(Ends(busy, 10_000), "the other drive's release did not end");
                Assert.True(Ends(check, 10_000), "the check did not end");
            }
            Assert.Null(listing.RemovalWatch(0));
        }
        finally { listing.Dispose(); }
    }

    [Fact]
    public void Index_manager_makes_listings_that_follow_its_run_history_and_hear_ejects()
    {
        // Listens for device notifications, as the app's does.
        using var manager = new IndexManager { DriveSource = () => [], LetterSource = () => 0 };
        manager.Start(new IndexSettings { IndexNtfsVolumes = false, DatabasePath = Path.Combine(_outside, "test.db") });
        WaitUntilSettled(manager);
        IIndexManager im = manager;

        var listing = im.CreateListing([_root], Settings());
        try
        {
            string a = Path.Combine(_root, "a.txt");
            manager.Index.RunHistory.Increment(a, 1);
            Assert.Equal(1, listing.Index.RunHistory.Get(a).Count);
            listing.Start();
            Assert.True(WaitFor(() => listing.State == ListingState.Ready), $"state {listing.State}");
            Assert.Equal(ExpectedRoot(), Paths(listing.Index));
            var watch = Assert.IsType<DeviceWatcher.Watch>(listing.RemovalWatch(0));
            Assert.NotEqual(0, watch.Notify);
            listing.Dispose();
            Assert.Equal(0, watch.Notify); // let go of with the listing
        }
        finally { listing.Dispose(); }
        Assert.Throws<ArgumentException>(() => im.CreateListing(["", " "], Settings()));
    }

    [Fact]
    public void Any_index_manager_makes_listings_that_follow_its_run_history()
    {
        IIndexManager im = new BareManager();
        using var listing = im.CreateListing([_root], Settings());
        string a = Path.Combine(_root, "a.txt");
        im.Index.RunHistory.Increment(a, 1);
        Assert.Equal(1, listing.Index.RunHistory.Get(a).Count);
        listing.Start();
        Assert.True(WaitFor(() => listing.State == ListingState.Ready), $"state {listing.State}");
        Assert.Equal(ExpectedRoot(), Paths(listing.Index));
        Assert.Null(listing.RemovalWatch(0)); // no device notifications to hear ejects by
        Assert.Throws<ArgumentException>(() => im.CreateListing(["", " "], Settings()));
    }

    /// <summary>An index manager with nothing but what <see cref="IIndexManager"/> asks for.</summary>
    sealed class BareManager : IIndexManager
    {
        public FileIndex Index { get; } = new();
        public IndexState State => IndexState.Ready;
        public string StatusText => "";
        public event Action? StateChanged { add { } remove { } }
        public bool IsElevated => false;
        public bool UsesService => false;
        public void Start(IndexSettings settings) { }
        public void Rebuild() { }
        public void Save() { }
        public void Dispose() { }
    }

    /// <summary>Sends WM_DEVICECHANGE for one eject watch, synchronously, the way Windows sends it.</summary>
    static unsafe void Send(DeviceWatcher devices, nint notify, int ev)
    {
        var b = new DeviceWatcher.DEV_BROADCAST_HANDLE
        {
            dbch_size = (uint)sizeof(DeviceWatcher.DEV_BROADCAST_HANDLE),
            dbch_devicetype = 6, // DBT_DEVTYP_HANDLE
            dbch_hdevnotify = notify,
            dbch_nameoffset = -1,
        };
        SendMessageW(devices.Window, 0x0219 /* WM_DEVICECHANGE */, ev, (nint)(&b));
    }

    [DllImport("user32.dll")] static extern nint SendMessageW(nint hwnd, uint msg, nint wParam, nint lParam);

    // ---- a quick look ----

    [Fact]
    public void HasFile_finds_the_first_kept_file()
    {
        var filter = new ExclusionFilter(Settings()); // leaves out the temp folder itself: ignored for the folder looked in
        var ct = CancellationToken.None;

        Assert.True(FolderListing.HasFile(_root, filter, deep: false, maxFolders: 1, ct));

        string nested = Path.Combine(_outside, "nested");
        Write(Path.Combine(nested, "one", "two", "x.txt"));
        Assert.True(FolderListing.HasFile(nested, filter, deep: true, maxFolders: 100, ct));
        Assert.False(FolderListing.HasFile(nested, filter, deep: false, maxFolders: 100, ct));
        Assert.False(FolderListing.HasFile(nested, filter, deep: true, maxFolders: 2, ct)); // x.txt is in the third folder
        Assert.True(FolderListing.HasFile(nested, filter, deep: true, maxFolders: 3, ct));

        string empties = Path.Combine(_outside, "empties");
        Directory.CreateDirectory(Path.Combine(empties, "a", "b"));
        Directory.CreateDirectory(Path.Combine(empties, "c"));
        Assert.False(FolderListing.HasFile(empties, filter, deep: true, maxFolders: 100, ct));

        string tmps = Path.Combine(_outside, "tmps");
        Write(Path.Combine(tmps, "a.tmp"));
        Write(Path.Combine(tmps, "sub", "b.tmp"));
        Write(Path.Combine(tmps, "node_modules", "c.txt"));
        Assert.False(FolderListing.HasFile(tmps, filter, deep: true, maxFolders: 100, ct));
        Assert.True(FolderListing.HasFile(tmps, ExclusionFilter.None, deep: false, maxFolders: 1, ct));

        Assert.Null(FolderListing.HasFile(Path.Combine(_outside, "not-there"), filter, deep: true, maxFolders: 100, ct));

        // A limit below 1 still looks in the folder itself.
        Assert.True(FolderListing.HasFile(_root, filter, deep: true, maxFolders: 0, ct));
        Assert.True(FolderListing.HasFile(_root, filter, deep: false, maxFolders: -5, ct));
        Assert.False(FolderListing.HasFile(nested, filter, deep: true, maxFolders: 0, ct));
    }

    [Fact]
    public void HasFile_counts_only_the_files_asked_for()
    {
        string code = Path.Combine(_outside, "code");
        Write(Path.Combine(code, "Program.cs"));
        Write(Path.Combine(code, "src", "app.ts"));
        var ct = CancellationToken.None;
        Func<string, bool> everyday = n => EverydayFiles.Default.IsEveryday(n);

        Assert.True(FolderListing.HasFile(code, ExclusionFilter.None, deep: true, maxFolders: 100, ct));
        Assert.False(FolderListing.HasFile(code, ExclusionFilter.None, deep: true, maxFolders: 100, ct, everyday));

        Write(Path.Combine(code, "docs", "screenshot.png"));
        Assert.True(FolderListing.HasFile(code, ExclusionFilter.None, deep: true, maxFolders: 100, ct, everyday));
        Assert.False(FolderListing.HasFile(code, ExclusionFilter.None, deep: false, maxFolders: 100, ct, everyday));
    }

    [Fact]
    public void HasFile_stops_when_cancelled()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        Assert.Throws<OperationCanceledException>(() => FolderListing.HasFile(_root, ExclusionFilter.None, deep: true, maxFolders: 100, cts.Token));
    }

    [Fact]
    public void HasFile_looks_in_every_folder_its_limit_allows()
    {
        // Breadth first: the folder, then a, b, c and d (only d has a file), then a\deeper.
        string wide = Path.Combine(_outside, "wide");
        foreach (var name in new[] { "a", "b", "c" }) Directory.CreateDirectory(Path.Combine(wide, name));
        Write(Path.Combine(wide, "d", "x.txt"));
        Write(Path.Combine(wide, "a", "deeper", "y.txt"));
        var ct = CancellationToken.None;

        Assert.True(FolderListing.HasFile(wide, ExclusionFilter.None, deep: true, maxFolders: 5, ct));
        Assert.False(FolderListing.HasFile(wide, ExclusionFilter.None, deep: true, maxFolders: 4, ct));
        Assert.True(FolderListing.HasFile(Path.Combine(wide, "a"), ExclusionFilter.None, deep: true, maxFolders: 2, ct));
        Assert.False(FolderListing.HasFile(Path.Combine(wide, "a"), ExclusionFilter.None, deep: true, maxFolders: 1, ct));
    }
}
