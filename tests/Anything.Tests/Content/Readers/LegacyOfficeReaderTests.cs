using System.Text;
using Anything.Core;
using Anything.Core.Content;
using Anything.Core.Content.Extract;
using Anything.Core.Content.Extract.Office;
using Anything.Core.Content.Indexing;
using Anything.Core.Content.Search;
using Anything.Core.Content.Store;

namespace Anything.Tests.Content.Readers;

/// <summary>
/// Word, Excel and PowerPoint 97-2003 files read for the text index (LegacyOfficeReader), from files built in memory by
/// the legacy readers' own test builders and written by OLE; held against what OoxmlReader does with docx, xlsx and pptx.
/// </summary>
public class LegacyOfficeReaderTests
{
    static readonly string LongText = string.Join(" ", Enumerable.Range(0, 400).Select(i => $"word{i:D3}")); // 3,199 characters

    static byte[] Doc(string text = "Zeppelin memo\rThe quarterly figures\r") => LegacyWordTests.DocFile(text);

    static byte[] Xls() => LegacyExcelTests.XlsFile(
        ("Budget 2024", false, [(0, 0, "Item"), (0, 1, "Cost"), (1, 0, "Paper")], [(1, 1, 12.5)]),
        ("Hidden notes", true, [(0, 0, "secret plan")], []));

    static byte[] Ppt(params string[] slides) => LegacyPowerPointTests.PptFile(slides.Length == 0 ? ["Opening slide", "Closing thanks"] : slides);

    // ------------------------------------------------------------------ each kind

    [Fact]
    public void Old_Word_files_give_their_text()
    {
        foreach (var extension in new[] { "doc", "dot" })
        {
            var doc = ReaderRun.Bytes(Doc(), extension);
            Assert.Equal(DocStatus.Ok, doc.Status);
            Assert.Equal(ContentKind.Office, doc.Kind);
            Assert.Equal(2, doc.ExtractorVersion);
            Assert.Equal("Zeppelin memo\nThe quarterly figures\n", doc.Body);
        }
    }

    [Fact]
    public void Old_Excel_files_give_every_sheet_as_a_page_starting_with_its_name()
    {
        foreach (var extension in new[] { "xls", "xlt" })
        {
            var doc = ReaderRun.Bytes(Xls(), extension);
            Assert.Equal(DocStatus.Ok, doc.Status);
            // As OoxmlReader writes a workbook: a hidden sheet too, cells separated by tabs.
            Assert.Equal("Budget 2024\nItem\tCost\nPaper\t12.5\n\fHidden notes\nsecret plan\n", doc.Body);
            Assert.Equal(2, doc.Pages);
        }
    }

    [Fact]
    public void Old_PowerPoint_files_give_every_slide_as_a_page()
    {
        foreach (var extension in new[] { "ppt", "pps", "pot" })
        {
            var doc = ReaderRun.Bytes(Ppt(), extension);
            Assert.Equal(DocStatus.Ok, doc.Status);
            Assert.Equal("Opening slide\fClosing thanks", doc.Body);
            Assert.Equal(2, doc.Pages);
        }
    }

    [Fact]
    public void The_streams_decide_the_format_whatever_the_extension()
    {
        var workbook = ReaderRun.Bytes(Xls(), "doc");
        Assert.Equal(DocStatus.Ok, workbook.Status);
        Assert.StartsWith("Budget 2024\n", workbook.Body);
        Assert.Equal("Zeppelin memo\nThe quarterly figures\n", ReaderRun.Bytes(Doc(), "ppt").Body);
    }

    // ------------------------------------------------------------------ limits, as the modern reader's

    [Fact]
    public void The_text_limit_cuts_where_it_cuts_a_docx()
    {
        var limits = new ExtractLimits(MaxTextChars: 1000);
        var modern = ReaderRun.Bytes(OfficeFixtures.Docx(body:
            $"<w:document xmlns:w=\"{OfficeFixtures.W}\"><w:body><w:p><w:r><w:t>{LongText}</w:t></w:r></w:p></w:body></w:document>"), "docx", limits);
        var old = ReaderRun.Bytes(Doc(LongText + "\r"), "doc", limits);
        Assert.Equal(DocStatus.Truncated, modern.Status);
        Assert.Equal(DocStatus.Truncated, old.Status);
        Assert.Equal(modern.Body, old.Body);
        Assert.Equal(1000, old.Body.Length);
    }

    [Fact]
    public void The_text_limit_cuts_a_workbook_and_a_presentation()
    {
        var limits = new ExtractLimits(MaxTextChars: 1000);
        var cells = Enumerable.Range(0, 400).Select(i => (i, 0, $"cell{i:D3}")).ToArray();
        var book = ReaderRun.Bytes(LegacyExcelTests.XlsFile(("Big", false, cells, [])), "xls", limits);
        Assert.Equal(DocStatus.Truncated, book.Status);
        Assert.Equal(1000, book.Body.Length);
        Assert.StartsWith("Big\ncell000\ncell001\n", book.Body);

        var deck = ReaderRun.Bytes(Ppt(LongText, "never reached"), "ppt", limits);
        Assert.Equal(DocStatus.Truncated, deck.Status);
        Assert.Equal(LongText[..1000], deck.Body);
    }

    [Fact]
    public void The_page_limit_stops_slides_and_sheets_as_it_stops_pptx_and_xlsx()
    {
        var limits = new ExtractLimits(MaxPages: 3);
        var deck = ReaderRun.Bytes(Ppt("one", "two", "three", "four", "five"), "ppt", limits);
        Assert.Equal(DocStatus.Truncated, deck.Status);
        Assert.Equal("one\ftwo\fthree", deck.Body);

        var sheets = Enumerable.Range(1, 5).Select(i => ($"S{i}", false, new[] { (0, 0, $"sheet{i} text") }, Array.Empty<(int, int, double)>())).ToArray();
        var book = ReaderRun.Bytes(LegacyExcelTests.XlsFile(sheets), "xls", limits);
        Assert.Equal(DocStatus.Truncated, book.Status);
        Assert.Equal(2, book.Body.Count(c => c == '\f'));
    }

    [Fact]
    public void Bytes_read_and_time_are_limited_by_the_budget()
    {
        // Past the bytes limit before anything was read: TooBig, as a docx is.
        var limits = new ExtractLimits(MaxBytesRead: 1024);
        Assert.Equal(DocStatus.TooBig, ReaderRun.Bytes(Doc(), "doc", limits).Status);
        Assert.Equal(DocStatus.TooBig, ReaderRun.Bytes(OfficeFixtures.Docx(), "docx", limits).Status);

        // A clock that moves a second each time it is asked: the time limit is reached while reading.
        long now = 0;
        var budget = new ExtractBudget(new ExtractLimits(MaxMilliseconds: 5_000), CancellationToken.None, () => now += 1000);
        using var source = new MemoryByteSource(Xls(), budget);
        Assert.Equal(DocStatus.TimedOut, ContentReaders.Extract(source, "xls", ContentKind.Office, budget).Status);
    }

    /// <summary>The bytes the reader has read by the time it reaches the last of these streams: the header, the directory and the streams before it.</summary>
    static long BytesBefore(byte[] file, params string[] streams)
    {
        var budget = new ExtractBudget(ExtractLimits.Local, CancellationToken.None);
        using var source = new MemoryByteSource(file, budget);
        Assert.True(CompoundFile.IsCompoundFile(source));
        var cfb = CompoundFile.Open(source, budget);
        cfb.HasEncryptedPackage();
        for (int i = 0; i < streams.Length; i++)
        {
            int id = cfb.Find(cfb.Root, streams[i]);
            if (i < streams.Length - 1) cfb.ReadStream(id, long.MaxValue);
        }
        return source.BytesRead;
    }

    [Fact]
    public void A_stream_larger_than_the_bytes_left_is_too_big_without_being_read()
    {
        // A Word document whose table stream is past the budget, after its document stream was read.
        var fib = new byte[600];
        fib[0] = 0xEC;
        fib[1] = 0xA5;
        var withTable = CfbFixtures.Ole(root =>
        {
            CfbFixtures.Stream(root, "WordDocument", fib);
            CfbFixtures.Stream(root, "0Table", new byte[900]);
        });
        foreach (var (file, extension, streams) in new (byte[], string, string[])[]
        {
            (CfbFixtures.Ole(root => CfbFixtures.Stream(root, "WordDocument", new byte[600])), "doc", ["WordDocument"]),
            (withTable, "doc", ["WordDocument", "0Table"]),
            (CfbFixtures.Ole(root => CfbFixtures.Stream(root, "Workbook", new byte[600])), "xls", ["Workbook"]),
            (CfbFixtures.Ole(root => CfbFixtures.Stream(root, "PowerPoint Document", new byte[600])), "ppt", ["PowerPoint Document"]),
        })
        {
            // A few hundred bytes left for a stream a little larger: TooBig, as a docx past the bytes limit, and the
            // stream is never read.
            long before = BytesBefore(file, streams);
            var doc = ReaderRun.Bytes(file, extension, new ExtractLimits(MaxBytesRead: before + 300));
            Assert.Equal(DocStatus.TooBig, doc.Status);
            Assert.Equal(before, doc.BytesRead);
        }
    }

    [Fact]
    public void Cancellation_stops_the_reading()
    {
        foreach (var (bytes, extension) in new[] { (Doc(), "doc"), (Xls(), "xls"), (Ppt(), "ppt") })
        {
            using var cts = new CancellationTokenSource();
            var budget = new ExtractBudget(ExtractLimits.Local, cts.Token);
            using var source = new CancellingSource(bytes, budget, cts, cancelAt: 3);
            Assert.ThrowsAny<OperationCanceledException>(() => ContentReaders.Extract(source, extension, ContentKind.Office, budget));
            Assert.True(source.BytesRead < bytes.Length, $"{extension}: read {source.BytesRead} of {bytes.Length}");
        }
    }

    /// <summary>A memory source that cancels its token at the k-th read; the next budget check stops the reader.</summary>
    sealed class CancellingSource(byte[] bytes, ExtractBudget budget, CancellationTokenSource cts, int cancelAt) : IByteSource
    {
        readonly MemoryByteSource _inner = new(bytes, budget);
        int _reads;
        public long Length => _inner.Length;
        public long BytesRead => _inner.BytesRead;

        public int ReadAt(long offset, Span<byte> buffer)
        {
            if (++_reads == cancelAt) cts.Cancel();
            return _inner.ReadAt(offset, buffer);
        }

        public void Dispose() => _inner.Dispose();
    }

    /// <summary>
    /// Files whose parse only ends by its token: a Word text past 65,536 characters (LegacyWord checks its token every
    /// 65,536 characters of a piece), a workbook and a presentation (checked per sheet and per slide).
    /// </summary>
    static (byte[] Bytes, string Extension)[] ParsedFiles() => [(Doc(new string('a', 70_000) + "\r"), "doc"), (Xls(), "xls"), (Ppt(), "ppt")];

    [Fact]
    public void The_parse_gets_the_budgets_cancellation()
    {
        foreach (var (bytes, extension) in ParsedFiles())
        {
            using var cts = new CancellationTokenSource();
            var budget = new ExtractBudget(ExtractLimits.Local, cts.Token);
            // Cancelled once every byte the parse needs has been read: only the parse's own token can stop it now.
            bool seen = false;
            var reader = new LegacyOfficeReader(token =>
            {
                cts.Cancel();
                seen = token.IsCancellationRequested;
            });
            using var source = new MemoryByteSource(bytes, budget);
            Assert.ThrowsAny<OperationCanceledException>(() => ContentReaders.Run(reader, source, extension, ContentKind.Office, budget));
            Assert.True(seen, $"{extension}: the parse's token didn't follow the budget's");
        }
    }

    [Fact]
    public void The_time_limit_reaches_the_parse()
    {
        foreach (var (bytes, extension) in ParsedFiles())
        {
            // The budget's clock stands still, so its own checks never run out of time: only the timer given to the parse
            // can end it, and the parse starts once that timer has fired. Without it the file would read Ok.
            var budget = new ExtractBudget(new ExtractLimits(MaxMilliseconds: 1), CancellationToken.None, () => 0);
            bool fired = false;
            var reader = new LegacyOfficeReader(token => fired = token.WaitHandle.WaitOne(TimeSpan.FromMinutes(1)));
            using var source = new MemoryByteSource(bytes, budget);
            var doc = ContentReaders.Run(reader, source, extension, ContentKind.Office, budget);
            Assert.True(fired, $"{extension}: the parse got no timer");
            Assert.Equal(DocStatus.TimedOut, doc.Status);
        }
    }

    [Fact]
    public void A_big_numeric_workbook_keeps_only_the_cells_its_text_can_use()
    {
        // 1,000 rows of 20 numbers: about 150,000 characters of cells, read for a 1,000-character text limit.
        var numbers = (from r in Enumerable.Range(0, 1000) from c in Enumerable.Range(0, 20) select (r, c, r * 20.0 + c + 0.5)).ToArray();
        var sheet = ("Numbers", false, Array.Empty<(int, int, string)>(), numbers);
        var stream = LegacyExcelTests.XlsStream(sheet);

        // The preview's way keeps every cell.
        var all = LegacyExcel.Parse(stream, LegacyOfficeReader.MaxRows, LegacyOfficeReader.MaxColumns, CancellationToken.None)!;
        Assert.Equal(20_000, all[0].Rows.Sum(row => row.Count(c => !string.IsNullOrEmpty(c))));

        // The index's way: the slots follow the text limit, and the cells kept stop just past it.
        var kept = LegacyExcel.Parse(stream, LegacyOfficeReader.MaxRows, LegacyOfficeReader.MaxColumns, 1001, CancellationToken.None, out var counts)!;
        Assert.Equal(2 * 1001L + LegacyOfficeReader.MaxRows, counts.SlotLimit);
        Assert.True(counts.SlotsKept <= 1001, $"{counts.SlotsKept} slots kept");
        long chars = kept[0].Rows.Sum(row => row.Where(c => !string.IsNullOrEmpty(c)).Sum(c => c!.Length + 1L));
        Assert.InRange(chars, 1001, 1001 + 16);
        Assert.True(kept[0].Truncated);

        // What reaches the index is the same as the whole sheet cut at the limit.
        var whole = "Numbers\n" + string.Concat(all[0].Rows.Select(row => string.Join('\t', row.Where(c => !string.IsNullOrEmpty(c))) + "\n"));
        var doc = ReaderRun.Bytes(LegacyExcelTests.XlsFile(sheet), "xls", new ExtractLimits(MaxTextChars: 1000));
        Assert.Equal(DocStatus.Truncated, doc.Status);
        Assert.Equal(whole[..1000], doc.Body);
    }

    [Fact]
    public void A_sparse_wide_workbook_is_kept_whole_when_its_text_fits()
    {
        // 50,000 rows with a value in column A and a note in column Z: about 200,000 characters, well under a 1 MB text
        // limit, but every empty column up to Z takes a slot: about 1.35 million, more than characters + 65,536.
        const int Rows = 50_000;
        var numbers = Enumerable.Range(0, Rows).SelectMany(r => new[] { (r, 0, 1.0), (r, 25, 2.0) }).ToArray();
        var stream = LegacyExcelTests.XlsStream(("Sparse", false, Array.Empty<(int, int, string)>(), numbers));
        long kept = new ExtractLimits().MaxTextChars + 1L;
        var sheets = LegacyExcel.Parse(stream, LegacyOfficeReader.MaxRows, LegacyOfficeReader.MaxColumns, kept, CancellationToken.None, out var counts)!;
        Assert.Equal(2 * kept + LegacyOfficeReader.MaxRows, counts.SlotLimit);
        Assert.True(counts.SlotsKept > kept + LegacyOfficeReader.MaxRows, $"{counts.SlotsKept} slots kept"); // the old allowance would have cut it
        Assert.False(sheets[0].Truncated);
        Assert.False(sheets[0].NotRead);
        Assert.Equal(2 * Rows, sheets[0].Rows.Sum(row => row.Count(c => !string.IsNullOrEmpty(c))));
    }

    // ------------------------------------------------------------------ files that aren't readable

    [Fact]
    public void A_file_that_is_not_a_compound_file_is_no_problem()
    {
        // These names weren't read before, so such a file mustn't become a problem now. RTF under a Word name is read as
        // an .rtf file is.
        var rtf = Encoding.ASCII.GetBytes("{\\rtf1\\ansi Memo text\\par}");
        var asRtf = ReaderRun.Bytes(rtf, "rtf");
        foreach (var extension in new[] { "doc", "dot" })
        {
            var doc = ReaderRun.Bytes(rtf, extension);
            Assert.Equal(DocStatus.Ok, doc.Status);
            Assert.Equal(ContentKind.Office, doc.Kind);
            Assert.Equal(2, doc.ExtractorVersion);
            Assert.Contains("Memo text", doc.Body);
            Assert.Equal(asRtf.Body, doc.Body);
        }

        // A binary file of another program under one of these names: the plain-text reader's answer, no words.
        var binary = Enumerable.Range(0, 3000).Select(i => (byte)(i * 31)).ToArray();
        foreach (var extension in OldNames)
        {
            var doc = ReaderRun.Bytes(binary, extension);
            Assert.Equal(AsExtraText(binary, extension).Status, doc.Status);
            Assert.Equal(DocStatus.NoText, doc.Status);
            Assert.False(ContentFollower.IsProblem(doc.Status), extension);
            Assert.Equal("", doc.Body);
            Assert.Equal(2, doc.ExtractorVersion);
        }
    }

    static readonly string[] OldNames = ["doc", "dot", "xls", "xlt", "ppt", "pps", "pot"];

    /// <summary>Text saved under old Office names: what banks, accounting tools and plain editors write.</summary>
    static (byte[] Bytes, string Word)[] TextFiles() =>
    [
        (Encoding.UTF8.GetBytes("<html><body><table><tr><td>Statement</td><td>Zanzibar</td></tr></table></body></html>"), "zanzibar"),
        (Encoding.ASCII.GetBytes("Name\tAmount\nZanzibar paper\t12\n"), "zanzibar"),
        (Encoding.ASCII.GetBytes("Meeting notes\r\nZanzibar trip is booked\r\n"), "zanzibar"),
        ([.. Encoding.Unicode.GetPreamble(), .. Encoding.Unicode.GetBytes("Notes in UTF-16\r\nZanzibar\r\n")], "zanzibar"),
    ];

    /// <summary>How a build before this reader read the file when the user listed its extension as an extra text extension.</summary>
    static ExtractedDoc AsExtraText(byte[] bytes, string extension)
    {
        var budget = new ExtractBudget(ExtractLimits.Local, CancellationToken.None);
        using var source = new MemoryByteSource(bytes, budget);
        return ContentReaders.Run(new PlainTextReader(), source, extension, ContentKind.Text, budget);
    }

    [Fact]
    public void Text_under_an_old_Office_name_is_read_as_an_extra_text_extension_was()
    {
        foreach (var (bytes, word) in TextFiles())
            foreach (var extension in OldNames)
                foreach (var extras in new[] { null, ContentTypes.ExtraSet([extension]) })
                {
                    // Listed as an extra or not, the kind is Office and the file is read by this reader.
                    var kind = ContentTypes.KindOf("export." + extension, extras);
                    Assert.Equal(ContentKind.Office, kind);
                    var budget = new ExtractBudget(ExtractLimits.Local, CancellationToken.None);
                    using var source = new MemoryByteSource(bytes, budget);
                    var doc = ContentReaders.Extract(source, extension, kind, budget);
                    var before = AsExtraText(bytes, extension);
                    Assert.Equal(DocStatus.Ok, doc.Status);
                    Assert.Equal(before.Status, doc.Status);
                    Assert.Equal(before.Body, doc.Body);
                    Assert.Contains(word, doc.Body, StringComparison.OrdinalIgnoreCase);
                    Assert.Equal(0, doc.Pages);
                }
    }

    [Fact]
    public void A_match_in_text_under_an_old_Office_name_gets_no_sheet_or_slide()
    {
        var highlighter = ContentHighlighter.Create("content:zanzibar", new SearchOptions())!;
        foreach (var (bytes, _) in TextFiles())
            foreach (var extension in OldNames)
            {
                var doc = ReaderRun.Bytes(bytes, extension);
                var snippet = SnippetFormatter.Format(new StoredDoc(doc.Body, [], null), doc.Kind, extension, doc.Pages, null, highlighter)!;
                Assert.Null(snippet.Sheet);
                Assert.Equal(0, snippet.Page);
                Assert.Equal(0, snippet.Line);
            }

        // A real workbook and presentation keep their places.
        var book = ReaderRun.Bytes(LegacyExcelTests.XlsFile(("Trips", false, [(0, 0, "Zanzibar")], [])), "xls");
        Assert.Equal("Trips", SnippetFormatter.Format(new StoredDoc(book.Body, [], null), book.Kind, "xls", book.Pages, null, highlighter)!.Sheet);
        var deck = ReaderRun.Bytes(Ppt("Opening", "Zanzibar"), "ppt");
        Assert.Equal(2, SnippetFormatter.Format(new StoredDoc(deck.Body, [], null), deck.Kind, "ppt", deck.Pages, null, highlighter)!.Page);
    }

    [Fact]
    public void The_reread_after_the_upgrade_keeps_the_words_of_a_text_file_under_an_old_Office_name()
    {
        foreach (var (bytes, word) in TextFiles())
            foreach (var extension in OldNames)
            {
                // What a build before stored for a user's extra text extension: the plain-text reader's record.
                var stored = AsExtraText(bytes, extension);
                Assert.Equal(1, stored.ExtractorVersion);
                Assert.True(ExtractorChanges.ShouldReread(extension, stored.Kind, stored.ExtractorVersion, stored.Status));
                var reread = ReaderRun.Bytes(bytes, extension);
                Assert.Equal(stored.Body, reread.Body);
                Assert.Contains(word, reread.Body, StringComparison.OrdinalIgnoreCase);
                Assert.False(ExtractorChanges.ShouldReread(extension, reread.Kind, reread.ExtractorVersion, reread.Status));
            }
    }

    [Fact]
    public void Encrypted_files_are_password_protected_as_an_encrypted_docx_is()
    {
        Assert.Equal(DocStatus.PasswordProtected, ReaderRun.Bytes(LegacyWordTests.DocFile("secret words", encrypted: true), "doc").Status);
        Assert.Equal(DocStatus.PasswordProtected, ReaderRun.Bytes(LegacyExcelTests.EncryptedXlsFile(), "xls").Status);
        Assert.Equal(DocStatus.PasswordProtected, ReaderRun.Bytes(LegacyPowerPointTests.PptFile(["secret slide"], encrypted: true), "ppt").Status);

        // An Office file encrypted with a password keeps its package in an EncryptedPackage stream, whatever its name.
        var package = CfbFixtures.Ole(root =>
        {
            CfbFixtures.Stream(root, "EncryptionInfo", new byte[100]);
            CfbFixtures.Stream(root, "EncryptedPackage", new byte[5000]);
        });
        Assert.Equal(DocStatus.PasswordProtected, ReaderRun.Bytes(package, "docx").Status);
        Assert.Equal(DocStatus.PasswordProtected, ReaderRun.Bytes(package, "doc").Status);
        Assert.Equal(DocStatus.PasswordProtected, ReaderRun.Bytes(package, "xls").Status);
    }

    [Fact]
    public void Broken_files_are_damaged_as_a_broken_docx_is()
    {
        var docx = OfficeFixtures.Docx();
        Assert.Equal(DocStatus.Damaged, ReaderRun.Bytes(docx[..(docx.Length / 2)], "docx").Status);

        // Half a compound file: its directory or streams lie past the end.
        var doc = Doc(LongText + "\r");
        Assert.Equal(DocStatus.Damaged, ReaderRun.Bytes(doc[..(doc.Length / 2)], "doc").Status);
        // A compound header and nothing after it.
        Assert.Equal(DocStatus.Damaged, ReaderRun.Bytes(doc[..512], "doc").Status);
        // A WordDocument stream that isn't one.
        var garbage = CfbFixtures.Ole(root => CfbFixtures.Stream(root, "WordDocument", Enumerable.Repeat((byte)0xEC, 6000).ToArray()));
        Assert.Equal(DocStatus.Unsupported, ReaderRun.Bytes(garbage, "doc").Status);
        var notWord = Enumerable.Range(0, 6000).Select(i => (byte)(i * 7)).ToArray();
        notWord[0] = 0xEC;
        notWord[1] = 0xA5;
        notWord[0x0A] = 0;
        notWord[0x0B] = 0;
        Assert.Equal(DocStatus.Damaged, ReaderRun.Bytes(CfbFixtures.Ole(root => CfbFixtures.Stream(root, "WordDocument", notWord)), "doc").Status);
        // A compound file of another program: no Office stream in it.
        Assert.Equal(DocStatus.Unsupported, ReaderRun.Bytes(CfbFixtures.Ole(root => CfbFixtures.Stream(root, "Contents", new byte[5000])), "doc").Status);
    }

    // ------------------------------------------------------------------ the kinds and the upgrade

    [Fact]
    public void The_old_kinds_are_Office_documents_read_by_this_reader()
    {
        foreach (var extension in new[] { "doc", "dot", "xls", "xlt", "ppt", "pps", "pot", "DOC" })
        {
            Assert.Equal(ContentKind.Office, ContentTypes.KindOf("old." + extension));
            Assert.IsType<LegacyOfficeReader>(ContentReaders.For(extension));
        }
        Assert.Equal(ContentKind.Office, ContentTypes.CandidateKind("old.xls", ContentKinds.Office, ContentMode.Documents, null));
        Assert.Equal(ContentKind.None, ContentTypes.CandidateKind("old.xls", ContentKinds.Pdf, ContentMode.Documents, null));
        Assert.Equal(ContentKind.None, ContentTypes.CandidateKind("passwords.xls", ContentKinds.All, ContentMode.Documents, null));
        // A user's extra text extension no longer overrides the built-in kind.
        Assert.Equal(ContentKind.Office, ContentTypes.KindOf("old.doc", ContentTypes.ExtraSet(["doc"])));
    }

    [Fact]
    public void The_upgrade_reads_again_exactly_what_was_stored_for_these_kinds_before()
    {
        // What a build before this reader could have stored for these files: nothing at all (they weren't read), or, for
        // a user's extra text extension, the plain-text reader's record (version 1), whatever its status.
        foreach (var status in new[] { DocStatus.Ok, DocStatus.Truncated, DocStatus.NoText, DocStatus.Damaged, DocStatus.Unsupported })
        {
            Assert.True(ExtractorChanges.ShouldReread("doc", ContentKind.Text, 1, status), status.ToString());
            Assert.True(ExtractorChanges.ShouldReread("xls", ContentKind.Office, 0, status), status.ToString());
        }
        // Read by this reader: not again.
        foreach (var extension in new[] { "doc", "xls", "ppt" })
            Assert.False(ExtractorChanges.ShouldReread(extension, ContentKind.Office, 2, DocStatus.Unsupported));
        // No other kind is touched: their readers' versions didn't move.
        foreach (var extension in new[] { "docx", "xlsx", "pptx", "odt", "pdf", "txt", "rtf", "eml", "jpg" })
        {
            var reader = ContentReaders.For(extension)!;
            Assert.False(ExtractorChanges.ShouldReread(extension, reader.Kind, reader.Version, DocStatus.NoText), extension);
            Assert.False(ExtractorChanges.ShouldReread(extension, reader.Kind, reader.Version, DocStatus.Unsupported), extension);
        }
    }
}
