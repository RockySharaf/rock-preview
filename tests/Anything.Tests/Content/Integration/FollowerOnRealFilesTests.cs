using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Indexing;
using Anything.Core.Content.Store;
using Anything.Core.Search;
using Anything.Tests.Content.Indexing;
using static Anything.Tests.Content.Integration.RealCorpus;

namespace Anything.Tests.Content.Integration;

/// <summary>Real files on disk, read through the content pipeline end to end; they share the disk, so they run one at a time.</summary>
[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class RealFilesSerial
{
    public const string Name = "Content on real files";
}

/// <summary>
/// I2 (spec §13): the follower (U5) with the real store (U1) and the real readers (U2, U3) over files written to disk:
/// the first build, following edits, renames and deletes with the reads they cost, the name index compacting and being
/// cleared, a restart that resumes without duplicates, a crash-marker leftover, hostile files in the mix, and what the
/// app asks of the index (snippets, statuses, searches).
/// </summary>
[Collection(RealFilesSerial.Name)]
public sealed class FollowerOnRealFilesTests : IDisposable
{
    readonly RealFilesRig _rig = new();

    public void Dispose() => _rig.Dispose();

    static readonly SearchEngine Engine = new() { ContentBudget = TimeSpan.FromSeconds(30) };

    string[] Find(string query, SearchOptions? options = null)
    {
        var r = Engine.Search(_rig.Index, query, options ?? new SearchOptions(), SortKey.Name, false, default);
        Assert.Null(r.Error);
        return [.. r.Ids.Select(id => Path.GetRelativePath(_rig.Root, _rig.Index.GetFullPath(id))).Order(StringComparer.OrdinalIgnoreCase)];
    }

    void Same(string[] expected, string query, SearchOptions? options = null)
    {
        var actual = Find(query, options);
        Assert.True(expected.Order(StringComparer.OrdinalIgnoreCase).SequenceEqual(actual, StringComparer.OrdinalIgnoreCase),
            $"[{query}]\n expected: {string.Join(" | ", expected.Order(StringComparer.OrdinalIgnoreCase))}\n actual:   {string.Join(" | ", actual)}");
    }

    ContentSnippet? Snippet(string relative, string query)
        => _rig.Content!.GetSnippet(_rig.Full(relative), ContentHighlighter.Create(query, new SearchOptions { MatchContent = true })!);

    void Build(bool hostile = true)
    {
        WriteAll(_rig, hostile);
        _rig.Start();
        _rig.Settle();
    }

    int Candidates(bool hostile = true) => Readable.Length + (hostile ? Hostile.Length : 0);

    [Fact]
    public void The_first_build_reads_every_candidate_once_and_finds_their_words()
    {
        Build();
        var content = _rig.Content!;

        // Each candidate read exactly once; denied names and folders, and kinds that aren't read, never.
        Assert.Equal(Candidates(), _rig.Files.Reads);
        foreach (var f in Readable.Concat(Hostile)) Assert.Equal(1, _rig.Files.ReadsOf(_rig.Full(f)));
        foreach (var f in Ignored) Assert.Equal(0, _rig.Files.ReadsOf(_rig.Full(f)));
        Assert.Equal(Candidates(), _rig.Rows().Count);
        Assert.Equal(Candidates(), _rig.Rows().Select(r => r.Row.Key).Distinct().Count());

        var status = content.Status;
        Assert.Equal(ContentPhase.UpToDate, status.Phase);
        Assert.Equal(Candidates(), status.Total);
        Assert.Equal(Candidates(), status.Done);
        Assert.True(status.Linked);
        Assert.True(status.IndexBytes > 0);
        Assert.False(content.CatchingUp);

        // What each reader made of its file.
        foreach (var f in (string[])[Notes, Letter, Book, Deck, Report, Memo, Page, Message, Poster])
            Assert.True(_rig.Row(_rig.Full(f))!.Value.Status == DocStatus.Ok, $"{f}: {_rig.Row(_rig.Full(f))!.Value.Status}");
        foreach (var f in (string[])[Beach, Scan, Lake]) Assert.Equal(DocStatus.NoText, _rig.Row(_rig.Full(f))!.Value.Status);
        foreach (var f in Hostile) Assert.NotNull(_rig.Row(_rig.Full(f)));
        Assert.Equal(DocStatus.Damaged, _rig.Row(_rig.Full(BrokenPdf))!.Value.Status);
        Assert.Equal(DocStatus.Damaged, _rig.Row(_rig.Full(HalfDocx))!.Value.Status);
        Assert.Equal(2, _rig.Row(_rig.Full(Report))!.Value.Pages);
        Assert.Equal(ContentKind.Picture, _rig.Row(_rig.Full(Beach))!.Value.Kind);

        // Searches through the real engine find the words, fields and picture details.
        Same([Notes, Report], "content:annual");
        Same([Letter], "content:INV-2024");
        Same([Book, Report], "content:revenue");
        Same([Deck], "content:\"closing thanks\"");
        Same([Letter, Book, Deck, Memo, Message], "content:quarterly");
        Same([Message], "content:café");
        Same([Book], "content:東京");
        Same([Page], "title:\"price list\"");
        Same([Page], "content:gadgets");
        Same([Poster], "title:\"poster design\"");
        Same([Poster], "keywords:poster");
        Same([Poster], "content:headline");
        Same([Beach], "keywords:sunset");
        Same([Beach], "camera:canon");
        Same([Beach], "dpi:240");
        Same([Beach], "gps:");
        Same([Lake], "title:\"mountain lake\"");
        Same([Scan, Poster], "colormode:cmyk");
        Same([Scan, Poster], "width:2480");
        Same([Report, Deck, Book], "pagecount:2"); // two pages, two slides, two sheets
        Same([BrokenPdf, HalfDocx], "contentstatus:damaged");
        // A plain word: report.pdf by name, then the files with a word starting "report" inside (the letter's title, the mail's subject).
        var plain = Engine.Search(_rig.Index, "report", new SearchOptions { MatchContent = true }, SortKey.Name, false, default);
        Assert.Equal(1, plain.NameMatches);
        Assert.Equal(Report, Path.GetRelativePath(_rig.Root, _rig.Index.GetFullPath(plain.Ids[0])));
        Same([Report, Notes, Letter, Message], "report", new SearchOptions { MatchContent = true });
        var failed = Find("contentstatus:failed");
        Assert.Contains(BrokenPdf, failed);
        Assert.Contains(HalfDocx, failed);
        Assert.DoesNotContain(Notes, failed);
        Assert.Empty(Find("content:secret"));      // the denied files' words
        Assert.Empty(Find("content:zipped"));
        Assert.True(content.CountsByStatus().GetValueOrDefault(DocStatus.Damaged) >= 2);
    }

    [Fact]
    public void Searches_hear_the_index_is_catching_up_until_it_has_caught_up()
    {
        WriteAll(_rig, hostile: false);
        _rig.Start();
        SearchResults Run(string q) => Engine.Search(_rig.Index, q, new SearchOptions { MatchContent = true }, SortKey.Name, false, default);

        // Before the first reconcile: content matches nothing yet and says so; names are found as ever.
        Assert.True(_rig.Content!.CatchingUp);
        var early = Run("content:annual");
        Assert.True(early.ContentPending);
        Assert.Empty(early.Ids);
        var plain = Run("report");
        Assert.True(plain.ContentPending);
        Assert.Equal([Report], plain.Ids.Select(id => Path.GetRelativePath(_rig.Root, _rig.Index.GetFullPath(id))));

        _rig.Settle();
        Assert.False(_rig.Content.CatchingUp);
        var done = Run("content:annual");
        Assert.False(done.ContentPending);
        Assert.Equal(2, done.Ids.Length);

        // The name index rebuilt (cleared, scanned again): stale until the follower has mapped the documents again.
        _rig.ClearAndRescan();
        var stale = Run("content:annual");
        Assert.True(stale.ContentPending);
        Assert.Empty(stale.Ids);
        _rig.Settle();
        var again = Run("content:annual");
        Assert.False(again.ContentPending);
        Assert.Equal(2, again.Ids.Length);
    }

    [Fact]
    public void Snippets_and_statuses_come_from_the_index()
    {
        Build();
        var content = _rig.Content!;

        var letter = Snippet(Letter, "content:invoice")!;
        Assert.Contains("Invoice", letter.Text);
        Assert.NotEmpty(letter.Highlights);
        Assert.Equal("Invoice", letter.Text.Substring(letter.Highlights[0].Start, letter.Highlights[0].Length));
        Assert.Equal(ContentKind.Office, letter.Kind);

        var report = Snippet(Report, "content:\"second page\"")!;
        Assert.Equal(2, report.Page);
        Assert.Equal(2, report.Pages);

        var deck = Snippet(Deck, "content:closing")!;
        Assert.Equal(2, deck.Page); // slides in sldIdLst order: the closing slide is the second

        var book = Snippet(Book, "content:inline")!;
        Assert.Equal("Budget 2024", book.Sheet);

        var notes = Snippet(Notes, "content:approved")!;
        Assert.Equal(3, notes.Line);

        var beach = Snippet(Beach, "keywords:sunset")!;
        Assert.Equal(DocField.Keywords, beach.Field);

        Assert.Null(Snippet(Notes, "content:nowhere"));
        Assert.Null(Snippet(@"Docs\.env", "content:secret"));

        // Statuses: problems and left-out files say why; readable files (a scan without words too) and files out of
        // scope say nothing.
        Assert.Equal(DocStatus.Damaged, content.GetStatus(_rig.Full(BrokenPdf)));
        Assert.Null(content.GetStatus(_rig.Full(Scan)));
        Assert.Null(content.GetStatus(_rig.Full(Notes)));
        Assert.Null(content.GetStatus(_rig.Full(@"Docs\.env")));
        Assert.Null(content.GetStatus(_rig.Full(@"Docs\node_modules\readme.txt")));
        Assert.Null(content.GetStatus(Path.Combine(Path.GetTempPath(), "elsewhere.txt")));
    }

    [Fact]
    public void Edits_renames_and_deletes_are_followed_with_only_the_reads_they_need()
    {
        Build(hostile: false);
        int reads = _rig.Files.Reads;

        // An edit: read again once, the old words gone, the new ones found.
        _rig.Change(_rig.Full(Notes), "Meeting moved\nnew zebra words");
        _rig.Settle();
        Assert.Equal(reads + 1, _rig.Files.Reads);
        Assert.Equal(2, _rig.Files.ReadsOf(_rig.Full(Notes)));
        Same([Notes], "content:zebra");
        Same([Report], "content:annual");
        reads = _rig.Files.Reads;

        // A file renamed: no read; found under its new name, with its snippet.
        string renamed = @"Docs\letter-final.docx";
        _rig.Rename(_rig.Full(Letter), _rig.Full(renamed));
        _rig.Settle();
        Assert.Equal(reads, _rig.Files.Reads);
        Same([renamed], "content:INV-2024");
        Assert.NotNull(Snippet(renamed, "content:invoice"));
        Assert.Null(Snippet(Letter, "content:invoice"));
        Assert.Null(_rig.Row(_rig.Full(Letter)));
        Assert.NotNull(_rig.Row(_rig.Full(renamed)));

        // A folder renamed: nothing below it read; every picture found at its new path.
        _rig.Rename(_rig.Full("Pictures"), _rig.Full("Photos"));
        _rig.Settle();
        Assert.Equal(reads, _rig.Files.Reads);
        Same([@"Photos\scan.tif", Poster], "colormode:cmyk");
        Same([@"Photos\beach.jpg"], "keywords:sunset");
        Assert.Equal(DocStatus.NoText, _rig.Row(_rig.Full(@"Photos\lake.png"))!.Value.Status);
        Assert.Null(_rig.Row(_rig.Full(Lake)));

        // A delete: gone from the store once the grace period has passed; nothing read.
        _rig.Delete(_rig.Full(Report));
        Same([], "content:\"second page\""); // its id is dead at once
        _rig.Pass(4000);
        Assert.NotNull(_rig.Row(_rig.Full(Report))); // still within the 10 s grace (a safe-save would bind it again)
        _rig.Settle();
        Assert.Null(_rig.Row(_rig.Full(Report)));
        Same([Book], "content:revenue");
        Assert.Equal(reads, _rig.Files.Reads);
        Assert.Equal(Readable.Length - 1, _rig.Rows().Count);
        Assert.Equal(ContentPhase.UpToDate, _rig.Content!.Status.Phase);
        Assert.True(Readable.Length - 1 == _rig.Content.Status.Total, _rig.Content.Status.ToString());
    }

    [Fact]
    public void A_safe_save_keeps_the_document_and_reads_the_new_text_once()
    {
        Build(hostile: false);
        int reads = _rig.Files.Reads;
        // Word's safe save: write a temp file, delete the original, rename the temp over it.
        string temp = _rig.Write(@"Docs\~WRL0001.tmp", SafeSavedDocx());
        _rig.Delete(_rig.Full(Letter));
        _rig.Rename(temp, _rig.Full(Letter));
        _rig.Settle();
        Assert.Equal(reads + 1, _rig.Files.Reads);
        Same([Letter], "content:\"safe saved\"");
        Same([], "content:INV-2024");
        Assert.Equal(Readable.Length, _rig.Rows().Count);
    }

    static byte[] SafeSavedDocx() => Readers.OfficeFixtures.Docx(body:
        $"<w:document xmlns:w=\"{Readers.OfficeFixtures.W}\"><w:body><w:p><w:r><w:t>safe saved text</w:t></w:r></w:p></w:body></w:document>");

    [Fact]
    public void The_name_index_compacting_or_being_cleared_reads_nothing_again()
    {
        Build(hostile: false);
        int reads = _rig.Files.Reads;

        // Compact: ids move down (removed entries before the documents' files); results still map to the right files.
        _rig.Delete(_rig.Full(Notes)); // created first: every later id moves
        _rig.Compact();
        _rig.Settle();
        Assert.Equal(reads, _rig.Files.Reads);
        Same([Report], "content:annual");
        Same([Letter], "content:INV-2024");
        Same([Scan, Poster], "colormode:cmyk");
        Assert.Equal(Readable.Length - 1, _rig.Rows().Count);

        // Clear (Rebuild Index): every entry comes back with a new id; documents bind again by key and stamp.
        _rig.ClearAndRescan();
        _rig.Settle();
        Assert.Equal(reads, _rig.Files.Reads);
        Same([Report], "content:annual");
        Same([Deck], "content:\"closing thanks\"");
        Same([Beach], "keywords:sunset");
        Assert.Equal(Readable.Length - 1, _rig.Rows().Count);
        Assert.Equal(ContentPhase.UpToDate, _rig.Content!.Status.Phase);
        Assert.True(_rig.Content.Status.Linked);
    }

    [Fact]
    public void A_restart_resumes_where_it_stopped_without_duplicates()
    {
        // More files than one pass of the loop reads, so the first session can stop part-way.
        WriteAll(_rig, hostile: false);
        for (int i = 0; i < 60; i++) _rig.Write($@"Many\note{i:D2}.txt", $"memo number {i} about topic{i % 7}");
        int candidates = Readable.Length + 60;

        // Session 1 runs as in the app (its own threads) and stops (app exit) while reading: the 26th read waits until the
        // stop cancels it.
        using var reading = new ManualResetEventSlim(false);
        _rig.Files.OnRead = (n, ct) =>
        {
            if (n <= 25) return;
            reading.Set();
            ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(20));
        };
        _rig.Start(threaded: true);
        Assert.True(reading.Wait(TimeSpan.FromSeconds(30)), "reading never got that far");
        _rig.Stop();
        _rig.Files.OnRead = null;
        Assert.Equal(26, _rig.Files.Reads);
        int committed;
        using (var store = ContentStore.Open(_rig.ContentFolder.Path, readOnly: true))
        using (var view = store.Acquire())
        {
            var keys = view.RowsByKey().Select(r => r.Row.Key).ToList();
            committed = keys.Count;
            Assert.Equal(committed, keys.Distinct().Count());
        }
        Assert.True(committed > 0, "nothing was committed at stop");
        int firstReads = _rig.Files.Reads;

        // Session 2: the app starts again, the name index loaded afresh (new ids): only what wasn't committed is read.
        _rig.NewIndex(fill: true);
        _rig.Start();
        _rig.Settle();
        int secondReads = _rig.Files.Reads - firstReads;
        Assert.Equal(candidates - committed, secondReads);
        var rows = _rig.Rows();
        Assert.Equal(candidates, rows.Count);
        Assert.Equal(candidates, rows.Select(r => r.Row.Key).Distinct().Count());
        Same([@"Many\note07.txt"], "content:\"memo number 7\"");
        Assert.Equal(ContentPhase.UpToDate, _rig.Content!.Status.Phase);

        // Session 3: nothing to read, nothing duplicated, everything searchable.
        _rig.Stop();
        int before = _rig.Files.Reads;
        _rig.NewIndex(fill: true);
        _rig.Start();
        _rig.Settle();
        Assert.Equal(before, _rig.Files.Reads);
        Assert.Equal(candidates, _rig.Rows().Count);
        Same([Letter], "content:INV-2024");
        Assert.NotNull(Snippet(Deck, "content:closing"));
    }

    [Fact]
    public void The_command_line_attaches_read_only_while_the_app_writes()
    {
        Build(hostile: false); // the app's index holds the folder as its writer
        string[] filesBefore = [.. Directory.GetFiles(_rig.ContentFolder.Path).Select(Path.GetFileName).Order()!];

        // Anything.Cli: its own name index (other ids), the text index opened read-only beside the running app.
        var cli = new FileIndex();
        var ids = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [_rig.Root] = cli.AddRoot(_rig.Root) };
        int Folder(string full) => ids.TryGetValue(full, out int id) ? id
            : ids[full] = cli.Add(Path.GetFileName(full), Folder(Path.GetDirectoryName(full)!), FileAttributes.Directory, -1, 0, 0);
        foreach (var file in Directory.EnumerateFiles(_rig.Root, "*", SearchOption.AllDirectories).OrderByDescending(f => f, StringComparer.Ordinal))
        {
            var info = new FileInfo(file);
            cli.Add(info.Name, Folder(info.DirectoryName!), FileAttributes.Archive, info.Length, info.LastWriteTimeUtc.ToFileTimeUtc(), 0);
        }
        Assert.True(ContentServices.AttachForSearch(cli, _rig.ContentFolder.Path));
        string[] CliFind(string query)
        {
            var r = Engine.Search(cli, query, new SearchOptions(), SortKey.Name, false, default);
            Assert.Null(r.Error);
            return [.. r.Ids.Select(id => Path.GetRelativePath(_rig.Root, cli.GetFullPath(id))).Order(StringComparer.OrdinalIgnoreCase)];
        }
        try
        {
            Assert.Equal([Letter], CliFind("content:INV-2024"));
            Assert.Equal([Beach], CliFind("keywords:sunset"));
            Assert.Equal([Poster, Scan], CliFind("colormode:cmyk"));
            // The read-only open deleted, swept and rewrote nothing.
            Assert.Equal(filesBefore, Directory.GetFiles(_rig.ContentFolder.Path).Select(Path.GetFileName).Order()!);

            // The app goes on writing meanwhile: an edit is read and committed, the CLI never in its way.
            int reads = _rig.Files.Reads;
            _rig.Change(_rig.Full(Notes), "Meeting moved\nnew zebra words");
            _rig.Settle();
            Assert.Equal(reads + 1, _rig.Files.Reads);
            Same([Notes], "content:zebra");
            Assert.Equal([Notes, Report], CliFind("content:annual")); // the CLI answers from the commit it opened
        }
        finally { (cli.Content as IDisposable)?.Dispose(); }
    }

    [Fact]
    public void A_file_named_by_a_leftover_crash_marker_is_recorded_crashed_and_the_rest_are_read()
    {
        WriteAll(_rig);
        string culprit = _rig.Full(Letter);
        var info = new FileInfo(culprit);
        // The last session died while reading the letter: its slot was never zeroed.
        var marker = CrashMarker.Open(Path.Combine(_rig.ContentFolder.Path, "reading"), out _)!;
        marker.Enter(marker.Acquire(), new MarkerEntry(_rig.KeyOf(culprit), 0, MarkerLane.Local, info.Length, info.LastWriteTimeUtc.ToFileTimeUtc()));
        CrashMarkerTests.CloseWithoutClearing(marker);

        _rig.Start();
        _rig.Settle();
        Assert.Equal(0, _rig.Files.ReadsOf(culprit));
        Assert.Equal(Candidates() - 1, _rig.Files.Reads);
        Assert.Equal(DocStatus.Crashed, _rig.Row(culprit)!.Value.Status);
        Assert.Equal(DocStatus.Crashed, _rig.Content!.GetStatus(culprit));
        Same([Letter], "contentstatus:crashed");
        Same([], "content:INV-2024");
        Same([Notes, Report], "content:annual"); // the rest, hostile files included, didn't stop anything
        Assert.Equal(Candidates(), _rig.Rows().Count);

        // Changed since: read again (one strike was for that version of the file).
        _rig.Change(culprit, Readers.OfficeFixtures.Docx());
        _rig.Settle();
        Assert.Equal(1, _rig.Files.ReadsOf(culprit));
        Assert.Equal(DocStatus.Ok, _rig.Row(culprit)!.Value.Status);
        Same([Letter], "content:INV-2024");
        Assert.Empty(Find("contentstatus:crashed"));
    }
}
