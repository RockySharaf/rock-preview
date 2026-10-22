using System.IO.Compression;
using System.Text;
using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Indexing;
using Anything.Core.Content.Store;
using Anything.Tests.Content.Indexing;
using Anything.Tests.Content.Pdf;
using Anything.Tests.Content.Readers;

namespace Anything.Tests.Content.Integration;

/// <summary>The real file reader with its reads counted per path (the follower's I/O, seen from outside).</summary>
internal sealed class CountingFiles(IContentSource inner) : IContentSource
{
    readonly object _gate = new();
    readonly List<string> _paths = [];

    public int Reads { get { lock (_gate) return _paths.Count; } }
    public IReadOnlyList<string> Paths { get { lock (_gate) return [.. _paths]; } }
    public int ReadsOf(string path) { lock (_gate) return _paths.Count(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)); }
    /// <summary>Runs before each read with its number (1-based) and token (tests stop a session part-way there).</summary>
    public Action<int, CancellationToken>? OnRead { get; set; }

    readonly List<(string Path, long StartMs, long EndMs, long BytesRead, bool Network)> _log = [];

    /// <summary>Each finished read: when it started and ended (Environment.TickCount64), what it cost and whether it was a network read.</summary>
    public IReadOnlyList<(string Path, long StartMs, long EndMs, long BytesRead, bool Network)> Log { get { lock (_gate) return [.. _log]; } }

    public ContentRead Read(ExtractRequest request, CancellationToken ct)
    {
        int n;
        lock (_gate)
        {
            _paths.Add(request.FullPath);
            n = _paths.Count;
        }
        OnRead?.Invoke(n, ct);
        long start = Environment.TickCount64;
        var read = inner.Read(request, ct);
        lock (_gate) _log.Add((request.FullPath, start, Environment.TickCount64, read.Doc.BytesRead, request.Network));
        return read;
    }

    public bool TryStat(string fullPath, out long size, out long modifiedUtc, out FileAttributes attributes)
        => inner.TryStat(fullPath, out size, out modifiedUtc, out attributes);
}

/// <summary>
/// I2's rig: real files in a real folder, a FileIndex that mirrors them as the name index would (a folder root, real sizes
/// and dates), and the real content index over them: ContentStore in a folder, the real readers (ContentServices.Files),
/// real volume keys and the crash marker in the content folder. Only time, power and the root gate are the tests' own,
/// and the follower runs on the test's thread (ManualLoop), so every step is deterministic.
///
/// The files live below the test binaries, not in %TEMP%: %TEMP% and every AppData folder are on the built-in deny-list
/// (rule 3), so nothing there is ever read inside.
/// </summary>
internal sealed class RealFilesRig : IDisposable
{
    public readonly string Root;
    public readonly Store.TempFolder ContentFolder = new();
    public readonly FakeClock Clock = new() { Now = DateTime.UtcNow };
    public readonly FakeGate Gate = new();
    public readonly FakePower Power = new();
    public CountingFiles Files = new(ContentServices.Files());
    public FileIndex Index { get; private set; } = null!;
    public FakeNames Names { get; private set; } = null!;
    public ContentIndex? Content { get; private set; }
    readonly Dictionary<string, int> _ids = new(StringComparer.OrdinalIgnoreCase);
    readonly ContentSettings _settings;

    /// <summary>The rig; viaShare reaches the same folder through the drive's administrative share (a network root).</summary>
    public RealFilesRig(ContentMode mode = ContentMode.DocumentsAndPictures, bool viaShare = false)
    {
        string local = Path.Combine(AppContext.BaseDirectory, "it-files", Guid.NewGuid().ToString("N"));
        Root = viaShare ? ShareOf(local) : local;
        Directory.CreateDirectory(Root);
        _settings = new ContentSettings { Mode = mode, ModeExplicit = true, UseMyFolders = false, Folders = [Root] };
        NewIndex();
    }

    public ContentSettings Settings => _settings.Clone();

    /// <summary>A local path through "\\localhost\X$" (the drive's administrative share).</summary>
    public static string ShareOf(string local) => @"\\localhost\" + local[0] + "$" + local[2..];

    /// <summary>Whether the test binaries' drive can be reached through its administrative share (not on every PC).</summary>
    public static bool ShareReachable()
    {
        try { return Directory.Exists(ShareOf(AppContext.BaseDirectory)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>Changes the settings the next Start uses.</summary>
    public void Configure(Action<ContentSettings> change) => change(_settings);

    /// <summary>A fresh name index (as after the app restarts or the name index is rebuilt) holding every file on disk.</summary>
    public void NewIndex(bool fill = false)
    {
        Index = new FileIndex();
        Names = new FakeNames(Index);
        _ids.Clear();
        _ids[Root] = Index.AddRoot(Root);
        if (fill) AddAll();
    }

    /// <summary>Every file and folder below the root that the index doesn't hold yet, as a scan would add them.</summary>
    public void AddAll()
    {
        foreach (var dir in Directory.EnumerateDirectories(Root, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase)) Folder(dir);
        foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories).Order(StringComparer.OrdinalIgnoreCase))
            if (!_ids.ContainsKey(file)) AddEntry(file);
        Index.RaiseChanged();
    }

    public string Full(string relative) => Path.Combine(Root, relative);

    /// <summary>Writes a file (folders made as needed) and adds it to the name index.</summary>
    public string Write(string relative, byte[] bytes, bool index = true)
    {
        string full = Full(relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, bytes);
        if (index)
        {
            AddEntry(full);
            Index.RaiseChanged();
        }
        return full;
    }

    public string Write(string relative, string text, bool index = true) => Write(relative, Encoding.UTF8.GetBytes(text), index);

    int Folder(string full)
    {
        if (_ids.TryGetValue(full, out int id)) return id;
        string parent = Path.GetDirectoryName(full)!;
        id = Index.Add(Path.GetFileName(full), Folder(parent), FileAttributes.Directory, -1, 0, 0);
        _ids[full] = id;
        return id;
    }

    void AddEntry(string full)
    {
        var info = new FileInfo(full);
        _ids[full] = Index.Add(info.Name, Folder(info.DirectoryName!), FileAttributes.Archive, info.Length, info.LastWriteTimeUtc.ToFileTimeUtc(),
            info.CreationTimeUtc.ToFileTimeUtc());
    }

    public int Id(string full) => _ids[full];

    /// <summary>Rewrites a file with a later date (so its stamp surely changes) and tells the name index.</summary>
    public void Change(string full, byte[] bytes)
    {
        File.WriteAllBytes(full, bytes);
        var stamp = File.GetLastWriteTimeUtc(full);
        var later = stamp.AddMinutes(1);
        File.SetLastWriteTimeUtc(full, later);
        Index.Update(_ids[full], size: bytes.Length, modifiedFileTimeUtc: later.ToFileTimeUtc());
        Index.RaiseChanged();
    }

    public void Change(string full, string text) => Change(full, Encoding.UTF8.GetBytes(text));

    public void Rename(string from, string to)
    {
        bool folder = Directory.Exists(from);
        if (folder) Directory.Move(from, to);
        else File.Move(from, to);
        int id = _ids[from];
        Index.Rename(id, Path.GetFileName(to), Folder(Path.GetDirectoryName(to)!));
        foreach (var key in _ids.Keys.Where(k => k.StartsWith(from + "\\", StringComparison.OrdinalIgnoreCase) || k.Equals(from, StringComparison.OrdinalIgnoreCase)).ToList())
        {
            int v = _ids[key];
            _ids.Remove(key);
            _ids[to + key[from.Length..]] = v;
        }
        Index.RaiseChanged();
    }

    public void Delete(string full)
    {
        File.Delete(full);
        Index.Remove(_ids[full]);
        _ids.Remove(full);
        Index.RaiseChanged();
    }

    /// <summary>The name index compacts (ids move down); the rig follows its own ids.</summary>
    public void Compact()
    {
        var remap = Index.Compact();
        foreach (var key in _ids.Keys.ToList())
        {
            int id = _ids[key];
            if ((uint)id < (uint)remap.Length && remap[id] >= 0) _ids[key] = remap[id];
            else _ids.Remove(key);
        }
    }

    /// <summary>The name index is cleared (Rebuild) and scanned again: every entry comes back with a new id.</summary>
    public void ClearAndRescan()
    {
        Index.Clear();
        _ids.Clear();
        _ids[Root] = Index.AddRoot(Root);
        AddAll();
    }

    public ContentIndexOptions Options => new()
    {
        ManualLoop = true,
        Clock = () => Clock.Now,
        Folder = ContentFolder.Path,
        OpenReadOnly = () => ContentStore.Open(ContentFolder.Path, readOnly: true),
        RecentItems = () => [],
    };

    /// <summary>
    /// Starts the real content index over the current name index (the store opened in the content folder): driven by the
    /// test (ManualLoop, the rig's clock), or threaded as in the app (its own follower and reader threads, real time).
    /// </summary>
    public ContentIndex Start(bool threaded = false, Func<ContentIndexOptions, ContentIndexOptions>? tweak = null)
    {
        if (Content != null) throw new InvalidOperationException("Already started.");
        var options = threaded ? Options with { ManualLoop = false, Clock = null } : Options;
        if (tweak != null) options = tweak(options);
        var content = new ContentIndex(() => ContentStore.Open(ContentFolder.Path), Files, Gate, Power, options);
        content.Start(Names, _settings);
        Content = content;
        return content;
    }

    /// <summary>Stops the content index (commits what fits), as the app does at exit.</summary>
    public void Stop()
    {
        Content?.Stop(TimeSpan.FromMilliseconds(800));
        Content = null;
    }

    public ContentFollower Follower => Content?.Follower ?? throw new InvalidOperationException("Not following.");

    public int Run(int max = 10_000) => Follower.RunUntilIdle(max);

    /// <summary>Lets time pass (settle, grace, commit timers) and runs the follower at each step.</summary>
    public void Pass(int ms, int step = 1000)
    {
        for (int t = 0; t < ms; t += step)
        {
            Clock.Advance(Math.Min(step, ms - t));
            Run();
        }
        Run();
    }

    /// <summary>Settles: 15 s in 1 s steps (settle 5 s, grace 10 s, commit 2 s after the queue empties).</summary>
    public void Settle() => Pass(15_000);

    // ── what the index holds ──

    /// <summary>The live rows of the store now (its current view).</summary>
    public List<(DocRow Row, string Path)> Rows()
    {
        using var view = Content!.AcquireView() ?? throw new InvalidOperationException("No view.");
        return [.. view.Store.RowsByKey().Select(r => (r.Row, view.Store.Segments[r.Ref.Segment].ReadPath(r.Ref.Doc) ?? "?"))];
    }

    /// <summary>The document key of a file below the root (real volume key and prefix).</summary>
    public ulong KeyOf(string full)
    {
        var (key, prefix, _) = VolumeKeys.For(Root);
        string rel = Path.GetRelativePath(Root, full);
        return ContentKeys.Of(key, prefix.Length == 0 ? rel : prefix + "\\" + rel);
    }

    public DocRow? Row(string full)
    {
        ulong key = KeyOf(full);
        foreach (var (row, _) in Rows()) if (row.Key == key) return row;
        return null;
    }

    public void Dispose()
    {
        Stop();
        ContentFolder.Dispose();
        for (int attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Thread.Sleep(50); }
        }
    }
}

/// <summary>Real documents, pictures and hostile files for the rig, from the readers' own fixture writers.</summary>
internal static class RealCorpus
{
    public const string Notes = @"Docs\notes.txt", Letter = @"Docs\letter.docx", Book = @"Docs\budget.xlsx", Deck = @"Docs\pitch.pptx",
        Report = @"Docs\report.pdf", Memo = @"Docs\memo.rtf", Page = @"Docs\page.html", Message = @"Mail\message.eml", Beach = @"Pictures\beach.jpg",
        Scan = @"Pictures\scan.tif", Lake = @"Pictures\lake.png", Poster = @"Design\poster.psd";

    public const string BrokenPdf = @"Docs\broken.pdf", HalfDocx = @"Docs\half.docx", Bomb = @"Docs\bomb.docx", DeepRtf = @"Docs\deep.rtf";

    public static readonly string[] Readable = [Notes, Letter, Book, Deck, Report, Memo, Page, Message, Beach, Scan, Lake, Poster];
    public static readonly string[] Hostile = [BrokenPdf, HalfDocx, Bomb, DeepRtf];
    /// <summary>Never read: denied names and folders, kinds that aren't read.</summary>
    public static readonly string[] Ignored = [@"Docs\.env", @"Docs\id_rsa", @"Docs\server.key", @"Docs\node_modules\readme.txt", @"Docs\.git\notes.txt",
        @"Docs\archive.zip", @"Docs\~$letter.docx"];

    public static byte[] Bytes(string relative) => relative switch
    {
        Notes => Encoding.UTF8.GetBytes("Meeting notes\r\nThe annual report is due.\r\nBudget approved."),
        Letter => OfficeFixtures.Docx(),
        Book => OfficeFixtures.Xlsx(),
        Deck => OfficeFixtures.Pptx(),
        Report => PdfFixtureWriter.SimpleDocument(PdfFixtureWriter.Lines("Annual Report 2023", "Total revenue grew"),
            PdfFixtureWriter.Lines("second page words")).Write(),
        Memo => Encoding.ASCII.GetBytes("{\\rtf1\\ansi{\\fonttbl{\\f0 Arial;}}\\f0 Memo about the \\b quarterly\\b0  meeting\\par Second paragraph}"),
        Page => Encoding.UTF8.GetBytes("<html><head><title>Price list</title><style>p{}</style></head><body><p>Widgets &amp; gadgets</p></body></html>"),
        Message => MailReadersTests.GeneratedMessage(),
        Beach => PictureFixtures.Jpeg(),
        Scan => PictureFixtures.Tiff(false),
        Lake => PictureFixtures.Png(),
        Poster => DesignFixtures.Psd(),
        BrokenPdf => Garbage("%PDF-1.7\n", 20_000, 11),
        HalfDocx => OfficeFixtures.Docx()[..(OfficeFixtures.Docx().Length / 2)],
        Bomb => ZipBomb(),
        DeepRtf => Encoding.ASCII.GetBytes("{\\rtf1 " + new string('{', 100_000) + "deep words" + new string('}', 100_000) + " after}"),
        _ when relative.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) => OfficeFixtures.Zip(false, ("a.txt", "zipped words")),
        _ => Encoding.UTF8.GetBytes("secret words never to be read"),
    };

    static byte[] Garbage(string head, int length, int seed)
    {
        var bytes = new byte[length];
        new Random(seed).NextBytes(bytes);
        Encoding.ASCII.GetBytes(head).CopyTo(bytes, 0);
        return bytes;
    }

    /// <summary>A docx whose document part inflates to 64 MB of zeros (ZipGuard stops it).</summary>
    static byte[] ZipBomb()
    {
        var ms = new MemoryStream();
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Create, leaveOpen: true))
        {
            using (var s = zip.CreateEntry("[Content_Types].xml").Open()) s.Write("<Types/>"u8);
            using (var s = zip.CreateEntry("word/document.xml", CompressionLevel.SmallestSize).Open())
            {
                s.Write(Encoding.UTF8.GetBytes($"<w:document xmlns:w=\"{OfficeFixtures.W}\"><w:body><w:p><w:r><w:t>bomb start "));
                var zeros = new byte[1 << 20];
                Array.Fill(zeros, (byte)' ');
                for (int i = 0; i < 64; i++) s.Write(zeros);
                s.Write(Encoding.UTF8.GetBytes("</w:t></w:r></w:p></w:body></w:document>"));
            }
        }
        return ms.ToArray();
    }

    /// <summary>Writes every file of the corpus (and the ignored ones) into the rig.</summary>
    public static void WriteAll(RealFilesRig rig, bool hostile = true, bool index = true)
    {
        foreach (var f in Readable) rig.Write(f, Bytes(f), index);
        if (hostile) foreach (var f in Hostile) rig.Write(f, Bytes(f), index);
        foreach (var f in Ignored) rig.Write(f, Bytes(f), index);
    }
}
