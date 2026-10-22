using Anything.Core.Content;
using Anything.Core.Content.Extract;
using Anything.Core.Content.Indexing;
using Anything.Core.Content.Store;

namespace Anything.Tests.Content.Indexing;

/// <summary>The §10 table: which problem records are read again, and when.</summary>
public class RetryPolicyTests
{
    const string A = @"C:\Users\Me\a.pdf";

    /// <summary>A stored row as the reader that handles a.pdf now would write it (its current version, so no bump applies).</summary>
    static DocRow Row(DocStatus status, DocFlags flags = DocFlags.None, long size = 100, long modified = 5000, ContentKind kind = ContentKind.Pdf,
        int version = -1)
        => new(1, modified, size, 0, 0, kind, status, version < 0 ? ContentReaders.CurrentVersion("pdf", kind) : (byte)version, flags);

    static ReadPolicy.Reason Decide(DocRow row, long size = 100, long modified = 5000, FileAttributes attrs = FileAttributes.Archive,
        ContentSettings? settings = null, long segmentAgeHours = 0, bool full = true)
    {
        long now = new DateTime(2026, 10, 21, 0, 0, 0, DateTimeKind.Utc).ToFileTimeUtc();
        return ReadPolicy.Decide(row, size, modified, attrs, "a.pdf", settings ?? new ContentSettings(), now - segmentAgeHours * 36_000_000_000L, now, full);
    }

    [Fact]
    public void Every_status_is_read_again_when_its_file_changes_and_an_unknown_date_is_stated_first()
    {
        foreach (var status in Enum.GetValues<DocStatus>())
        {
            Assert.True(Decide(Row(status), size: 101).Read, status.ToString());
            Assert.True(Decide(Row(status), modified: 5001).Read, status.ToString());
            var unknown = Decide(Row(status), modified: 0);
            Assert.True(unknown.Read && unknown.StatFirst, status.ToString());
        }
    }

    [Fact]
    public void Unchanged_problem_records_wait_for_their_own_condition()
    {
        Assert.False(Decide(Row(DocStatus.Ok)).Read);
        Assert.False(Decide(Row(DocStatus.Truncated)).Read);
        Assert.False(Decide(Row(DocStatus.PasswordProtected)).Read);
        Assert.False(Decide(Row(DocStatus.Crashed)).Read);
        Assert.False(Decide(Row(DocStatus.Damaged)).Read); // no extractor bump
        // A row from an older reader version: the bump reads Damaged again (default bump statuses), never Ok.
        byte current = ContentReaders.CurrentVersion("pdf", ContentKind.Pdf);
        Assert.True(current > 0);
        Assert.True(Decide(Row(DocStatus.Damaged, version: current - 1)).Read);
        Assert.False(Decide(Row(DocStatus.Ok, version: current - 1)).Read);
        // CloudOnly: at each full reconcile (a stat; hydration changes neither size nor date), not on every touch.
        Assert.True(Decide(Row(DocStatus.CloudOnly)).Read);
        Assert.False(Decide(Row(DocStatus.CloudOnly), full: false).Read);
        // OptedOut: once the "don't index contents" attribute is gone.
        Assert.False(Decide(Row(DocStatus.OptedOut), attrs: (FileAttributes)0x2020).Read);
        Assert.True(Decide(Row(DocStatus.OptedOut), attrs: FileAttributes.Archive).Read);
        // EFS: once allowed, or once no longer encrypted.
        Assert.False(Decide(Row(DocStatus.EfsEncrypted), attrs: FileAttributes.Encrypted).Read);
        Assert.True(Decide(Row(DocStatus.EfsEncrypted), attrs: FileAttributes.Encrypted, settings: new ContentSettings { ReadEncrypted = true }).Read);
        Assert.True(Decide(Row(DocStatus.EfsEncrypted), attrs: FileAttributes.Archive).Read);
        // TooBig: once the limit allows it.
        var big = Row(DocStatus.TooBig, size: 3L << 30);
        Assert.False(Decide(big, size: 3L << 30).Read);
        Assert.True(Decide(big, size: 3L << 30, settings: new ContentSettings { MaxFileSizeMB = 4096 }).Read);
        Assert.True(Decide(big, size: 3L << 30, settings: new ContentSettings { MaxFileSizeMB = 0 }).Read);
        // AccessDenied: daily.
        Assert.False(Decide(Row(DocStatus.AccessDenied), segmentAgeHours: 23).Read);
        Assert.True(Decide(Row(DocStatus.AccessDenied), segmentAgeHours: 25).Read);
        // TimedOut: once more, then only on change.
        Assert.True(Decide(Row(DocStatus.TimedOut, DocFlags.Attempt1)).Read);
        Assert.False(Decide(Row(DocStatus.TimedOut, DocFlags.Attempt1 | DocFlags.Attempt2)).Read);
    }

    [Fact]
    public void Temporary_trouble_is_retried_after_a_minute_ten_minutes_an_hour_then_not_this_session()
    {
        var t = new RetryTable();
        Assert.Equal(60_000, t.Fail(1, 5, 0));
        Assert.True(t.Waiting(1, 59_999));
        Assert.False(t.Waiting(1, 60_000));
        Assert.Equal(1000 + 600_000, t.Fail(1, 5, 1000));
        Assert.Equal(2000 + 3_600_000, t.Fail(1, 5, 2000));
        Assert.Equal(long.MaxValue, t.Fail(1, 5, 3000));
        t.Succeeded(1);
        Assert.False(t.Waiting(1, 0));
    }

    [Fact]
    public void A_file_in_use_is_tried_again_on_the_ladder_and_nothing_is_recorded()
    {
        using var h = new Harness();
        h.Folder(@"C:\Users\Me");
        h.File(A, "locked words");
        h.Files.OnRead = r => new ContentRead(ExtractedDoc.Failed(r.Kind, DocStatus.Ok), r.ExpectedSize, r.ExpectedModifiedUtc, Retry: true, false);
        h.Start();
        h.Settle();
        Assert.Equal(1, h.Files.Reads);
        Assert.Null(h.Row(A));
        h.Pass(61_000, step: 5000);
        Assert.Equal(2, h.Files.Reads);
        h.Pass(601_000, step: 20_000);
        Assert.Equal(3, h.Files.Reads);
        h.Pass(3_601_000, step: 60_000);
        Assert.Equal(4, h.Files.Reads);
        h.Pass(24 * 3_600_000, step: 600_000);
        h.Index.Configure(dateAccessed: true, folderSizes: false, recentChanges: true); // even a full reconcile waits
        h.Settle();
        Assert.Equal(4, h.Files.Reads);
        h.Files.OnRead = null;
        h.Change(A, "free now");
        h.Pass(4 * 3_600_000, step: 600_000);
        Assert.Equal(4, h.Files.Reads); // "then the next session"
    }

    [Fact]
    public void Access_denied_is_read_again_once_its_record_is_a_day_old()
    {
        using var h = new Harness();
        h.Folder(@"C:\Users\Me");
        h.File(A, "secret");
        h.Files.OnRead = r => new ContentRead(ExtractedDoc.Failed(r.Kind, DocStatus.AccessDenied), r.ExpectedSize, r.ExpectedModifiedUtc, false, false);
        h.Start();
        h.Settle();
        Assert.Equal(DocStatus.AccessDenied, h.Row(A)!.Value.Status);
        Reconcile(h, TimeSpan.FromHours(5));
        Assert.Equal(1, h.Files.Reads);
        Reconcile(h, TimeSpan.FromHours(20));
        Assert.Equal(2, h.Files.Reads);
        Reconcile(h, TimeSpan.FromHours(1));
        Assert.Equal(2, h.Files.Reads); // the new record is fresh again
    }

    [Fact]
    public void A_timed_out_file_gets_one_more_try_then_waits_for_a_change()
    {
        using var h = new Harness();
        h.Folder(@"C:\Users\Me");
        h.File(A, "slow");
        h.Files.OnRead = r => new ContentRead(ExtractedDoc.Failed(r.Kind, DocStatus.TimedOut), r.ExpectedSize, r.ExpectedModifiedUtc, false, false);
        h.Start();
        h.Settle();
        Assert.Equal(DocFlags.Attempt1, h.Row(A)!.Value.Flags & (DocFlags.Attempt1 | DocFlags.Attempt2));
        Reconcile(h, TimeSpan.FromMinutes(1));
        Assert.Equal(2, h.Files.Reads);
        Assert.Equal(DocFlags.Attempt1 | DocFlags.Attempt2, h.Row(A)!.Value.Flags & (DocFlags.Attempt1 | DocFlags.Attempt2));
        Reconcile(h, TimeSpan.FromMinutes(1));
        Assert.Equal(2, h.Files.Reads);
        h.Files.OnRead = null;
        h.Change(A, "fast now");
        h.Settle();
        Assert.Equal(DocStatus.Ok, h.Row(A)!.Value.Status);
    }

    [Fact]
    public void A_cloud_placeholder_is_checked_at_each_full_reconcile_without_writing_anything()
    {
        using var h = new Harness();
        h.Folder(@"C:\Users\Me");
        h.File(A, "in the cloud");
        bool hydrated = false;
        h.Files.OnRead = r => hydrated ? null : new ContentRead(ExtractedDoc.Failed(r.Kind, DocStatus.CloudOnly), r.ExpectedSize, r.ExpectedModifiedUtc, false, false);
        h.Start();
        h.Settle();
        Assert.Equal(DocStatus.CloudOnly, h.Row(A)!.Value.Status);
        int commits = h.Store.CommitCount;
        Reconcile(h, TimeSpan.FromMinutes(1));
        Assert.Equal(2, h.Files.Reads);
        Assert.Equal(commits, h.Store.CommitCount);
        hydrated = true; // downloaded: same size and date
        Reconcile(h, TimeSpan.FromMinutes(1));
        Assert.Equal(DocStatus.Ok, h.Row(A)!.Value.Status);
    }

    [Fact]
    public void Read_problem_files_again_forgets_the_records()
    {
        using var h = new Harness();
        h.Folder(@"C:\Users\Me");
        h.File(A, "words");
        h.Files.OnRead = r => new ContentRead(ExtractedDoc.Failed(r.Kind, DocStatus.Damaged), r.ExpectedSize, r.ExpectedModifiedUtc, false, false);
        h.Start();
        h.Settle();
        Assert.Equal(DocStatus.Damaged, h.Row(A)!.Value.Status);
        h.Files.OnRead = null;
        h.Content.RetryProblems();
        h.Settle();
        Assert.Equal(DocStatus.Ok, h.Row(A)!.Value.Status);
        Assert.Equal(2, h.Files.Reads);
    }

    [Fact]
    public void Words_of_encrypted_files_leave_once_reading_them_is_no_longer_allowed()
    {
        var settings = Harness.DefaultSettings();
        settings.ReadEncrypted = true;
        using var h = new Harness(settings);
        h.Folder(@"C:\Users\Me");
        h.File(A, "secret words", attributes: FileAttributes.Archive | FileAttributes.Encrypted);
        h.Files.OnRead = r => r.ReadEncrypted ? null : new ContentRead(ExtractedDoc.Failed(r.Kind, DocStatus.EfsEncrypted), r.ExpectedSize, r.ExpectedModifiedUtc, false, false);
        h.Start();
        h.Settle();
        Assert.Equal("secret words", h.Doc(A)!.Body);
        Assert.True((h.Row(A)!.Value.Flags & DocFlags.EncryptedOptIn) != 0);
        var off = settings.Clone();
        off.ReadEncrypted = false;
        h.Content.Apply(off);
        h.Settle();
        Assert.Equal(DocStatus.EfsEncrypted, h.Row(A)!.Value.Status);
        Assert.Equal("", h.Doc(A)!.Body);
    }

    [Fact]
    public void A_store_made_by_a_newer_version_is_never_written_or_read_for()
    {
        var store = new FakeStore { IsReadOnly = true, IsNewerFormat = true };
        using var h = new Harness(store: store);
        h.Folder(@"C:\Users\Me");
        h.File(A, "words");
        h.Start();
        h.Settle();
        Assert.Equal(0, h.Files.Reads);
        Assert.Equal(ContentPhase.Stopped, h.Content.Status.Phase);
        Assert.Contains("newer version", h.Content.Status.Reason);
        h.Pass(120_000, step: 10_000);
        Assert.Equal(1, h.StoreOpens); // never opened again: only a Rebuild changes it
    }

    [Fact]
    public void A_store_in_use_for_now_is_opened_again_later_and_then_read_for()
    {
        var store = new FakeStore { UnavailableOpens = 1 }; // a backup holds a segment at the first open, then lets go
        using var h = new Harness(store: store);
        h.Folder(@"C:\Users\Me");
        h.File(A, "words");
        h.Start();
        h.Settle();
        Assert.Equal(0, h.Files.Reads);
        Assert.Equal(ContentPhase.Paused, h.Content.Status.Phase); // tried again on a timer
        Assert.Contains("in use", h.Content.Status.Reason);
        h.Pass(40_000, step: 5000);
        h.Settle();
        Assert.True(h.StoreOpens >= 2);
        Assert.Equal(1, h.Files.Reads);
        Assert.Equal("words", h.Doc(A)!.Body);
        Assert.Null(h.Content.Status.Reason);
    }

    [Fact]
    public void A_damaged_segment_is_dropped_by_an_empty_commit_and_its_files_are_read_again()
    {
        using var h = new Harness();
        h.Folder(@"C:\Users\Me");
        h.File(A, "words");
        h.Start();
        h.Settle();
        Assert.Equal(1, h.Files.Reads);
        using (var v = h.Store.Acquire()) h.Store.Damaged.Add(v.Segments[0].Id);
        h.Pass(15_000, step: 5000);
        h.Settle();
        Assert.False(h.Store.HasDamage);
        Assert.Equal(2, h.Files.Reads);
        Assert.Equal("words", h.Doc(A)!.Body);
        Assert.Equal(h.Id(A), h.BoundId(A));
    }

    static void Reconcile(Harness h, TimeSpan after)
    {
        h.Clock.Advance(after);
        h.Index.Configure(dateAccessed: !h.Index.HasDateAccessed, folderSizes: false, recentChanges: true);
        h.Settle();
    }
}
