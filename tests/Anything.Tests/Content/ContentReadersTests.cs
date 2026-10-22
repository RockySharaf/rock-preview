using Anything.Core.Content;
using Anything.Core.Content.Extract;

namespace Anything.Tests.Content;

public class ContentReadersTests
{
    /// <summary>A reader that writes what it is told to, then returns or throws.</summary>
    sealed class FakeReader(Action<ExtractSink, ExtractBudget>? write, Func<DocStatus>? finish) : IContentReader
    {
        public ContentKind Kind => ContentKind.Text;
        public byte Version => 3;
        public bool Handles(ReadOnlySpan<char> extension) => true;
        public DocStatus Read(IByteSource source, ReadOnlySpan<char> extension, ExtractSink sink, ExtractBudget budget)
        {
            Span<byte> buffer = stackalloc byte[4];
            source.ReadAt(0, buffer);
            write?.Invoke(sink, budget);
            return finish?.Invoke() ?? DocStatus.Ok;
        }
    }

    /// <summary>A reader of one kind and version for some extensions; reads nothing.</summary>
    sealed class VersionedReader(ContentKind kind, byte version, params string[] extensions) : IContentReader
    {
        public ContentKind Kind => kind;
        public byte Version => version;
        public bool Handles(ReadOnlySpan<char> extension)
        {
            foreach (var e in extensions)
                if (extension.SequenceEqual(e)) return true;
            return false;
        }
        public DocStatus Read(IByteSource source, ReadOnlySpan<char> extension, ExtractSink sink, ExtractBudget budget) => DocStatus.NoText;
    }

    static ExtractedDoc Run(Action<ExtractSink, ExtractBudget>? write, Func<DocStatus>? finish = null, ExtractLimits? limits = null)
        => Run(new FakeReader(write, finish), "txt", ContentKind.Office, limits);

    static ExtractedDoc Run(IContentReader reader, string extension, ContentKind kind, ExtractLimits? limits = null)
    {
        var budget = new ExtractBudget(limits ?? ExtractLimits.Local, CancellationToken.None);
        using var source = new MemoryByteSource(new byte[100], budget);
        return ContentReaders.Run(reader, source, extension, kind, budget);
    }

    static void Text(ExtractSink s, ExtractBudget _) => s.Text("some words");

    [Fact]
    public void Ok_with_text_stays_Ok_and_carries_the_reader_facts()
    {
        var doc = Run(Text);
        Assert.Equal(DocStatus.Ok, doc.Status);
        Assert.Equal("some words", doc.Body);
        Assert.Equal(ContentKind.Office, doc.Kind);
        Assert.Equal(3, doc.ExtractorVersion);
        Assert.Equal(4, doc.BytesRead);
    }

    [Fact]
    public void Ok_with_nothing_is_NoText_and_a_full_sink_is_Truncated()
    {
        Assert.Equal(DocStatus.NoText, Run(null).Status);
        Assert.Equal(DocStatus.Truncated, Run(Text, limits: new ExtractLimits(MaxTextChars: 4)).Status);
        Assert.Equal(DocStatus.PasswordProtected, Run(null, () => DocStatus.PasswordProtected).Status);
    }

    [Fact]
    public void Ok_without_words_in_the_body_is_NoText_and_keeps_fields_and_picture_details()
    {
        // A scan whose /Info has a title: its pages are empty, so it is NoText (an extractor bump or OCR can reach it).
        var scan = Run((s, _) =>
        {
            s.Field(DocField.Title, "Scanned invoice");
            for (int page = 0; page < 5; page++) s.PageBreak();
            s.Pages = 6;
        });
        Assert.Equal(DocStatus.NoText, scan.Status);
        Assert.Equal("", scan.Body);
        Assert.Equal([(DocField.Title, "Scanned invoice")], scan.Fields);
        Assert.Equal(6, scan.Pages);

        // A picture: only details.
        var picture = Run((s, _) => { s.Picture.Width = 640; s.Field(DocField.Keywords, "beach"); });
        Assert.Equal(DocStatus.NoText, picture.Status);
        Assert.Equal(640, picture.Picture!.Value.Width);
        Assert.Equal([(DocField.Keywords, "beach")], picture.Fields);

        // More blank pages than the page limit: still no text, not Truncated.
        var many = Run((s, _) => { for (int page = 0; page < 10; page++) s.PageBreak(); }, limits: new ExtractLimits(MaxPages: 3));
        Assert.Equal(DocStatus.NoText, many.Status);
    }

    [Fact]
    public void NoText_from_the_reader_drops_the_body_and_problem_statuses_keep_nothing()
    {
        // The PDF garbage guard: text was streamed, then judged unreadable.
        var garbage = Run((s, _) => { s.Text("ÿþ garbage"); s.Field(DocField.Title, "Kept"); }, () => DocStatus.NoText);
        Assert.Equal(DocStatus.NoText, garbage.Status);
        Assert.Equal("", garbage.Body);
        Assert.Equal([(DocField.Title, "Kept")], garbage.Fields);

        foreach (var problem in new[] { DocStatus.PasswordProtected, DocStatus.Unsupported })
        {
            var doc = Run((s, _) => { s.Text("words"); s.Field(DocField.Title, "t"); s.Picture.Width = 3; s.Pages = 2; }, () => problem);
            Assert.Equal(problem, doc.Status);
            Assert.Equal("", doc.Body);
            Assert.Empty(doc.Fields);
            Assert.Null(doc.Picture);
            Assert.Equal(0, doc.Pages);
            Assert.Equal(3, doc.ExtractorVersion);
            Assert.Equal(4, doc.BytesRead);
        }
    }

    [Fact]
    public void Reader_exceptions_keep_what_was_read()
    {
        var kept = Run((s, _) => { s.Text("page one"); throw new InvalidDataException("bad stream"); });
        Assert.Equal(DocStatus.Truncated, kept.Status);
        Assert.Equal("page one", kept.Body);
        Assert.Equal(DocStatus.Damaged, Run((_, _) => throw new FormatException("bad")).Status);
        Assert.Equal(DocStatus.Damaged, Run((_, _) => throw new IndexOutOfRangeException()).Status);
        Assert.Equal(DocStatus.Truncated, Run((s, _) => { s.Picture.Width = 10; throw new ArgumentException("bad date"); }).Status);
        Assert.Equal(DocStatus.Truncated, Run((s, _) => { s.Field(DocField.Keywords, "k"); throw new ArgumentException(); }).Status);
    }

    [Fact]
    public void Limits_map_to_TimedOut_or_TooBig_only_when_nothing_was_read()
    {
        Assert.Equal(DocStatus.TimedOut, Run((_, _) => throw new ContentLimitException(ContentLimitException.Time)).Status);
        Assert.Equal(DocStatus.TooBig, Run((_, _) => throw new ContentLimitException(ContentLimitException.Bytes)).Status);
        Assert.Equal(DocStatus.TooBig, Run((_, _) => throw new ContentLimitException(ContentLimitException.Decoded)).Status);
        Assert.Equal(DocStatus.Truncated, Run((s, b) => { Text(s, b); throw new ContentLimitException(ContentLimitException.Time); }).Status);
        Assert.Equal(DocStatus.TooBig, Run(null, limits: new ExtractLimits(MaxBytesRead: 2)).Status);
        // Page breaks alone are no content: a scan that runs out of time is TimedOut, not Truncated.
        Assert.Equal(DocStatus.TimedOut, Run((s, _) => { s.PageBreak(); s.PageBreak(); throw new ContentLimitException(ContentLimitException.Time); }).Status);
    }

    [Fact]
    public void Limits_wrapped_in_other_exceptions_map_like_limits()
    {
        static Exception Wrapped(string limit) => new InvalidDataException("bad entry", new ContentLimitException(limit));
        Assert.Equal(DocStatus.TimedOut, Run((_, _) => throw Wrapped(ContentLimitException.Time)).Status);
        Assert.Equal(DocStatus.TooBig, Run((_, _) => throw Wrapped(ContentLimitException.Decoded)).Status);
        Assert.Equal(DocStatus.TooBig, Run((_, _) => throw new AggregateException(new ContentLimitException(ContentLimitException.Bytes))).Status);
        Assert.Equal(DocStatus.Truncated, Run((s, b) => { Text(s, b); throw Wrapped(ContentLimitException.Time); }).Status);
    }

    [Fact]
    public void Io_errors_and_cancellation_go_back_to_the_source()
    {
        Assert.Throws<ContentIoException>(() => Run((_, _) => throw new ContentIoException(995, "cancelled")));
        var wrapped = Assert.Throws<ContentIoException>(() => Run((_, _) => throw new InvalidDataException("zip", new ContentIoException(64, "net"))));
        Assert.Equal(64, wrapped.Win32Error);
        Assert.Throws<OperationCanceledException>(() => Run((_, _) => throw new OperationCanceledException()));
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var budget = new ExtractBudget(ExtractLimits.Local, cts.Token);
        using var source = new MemoryByteSource(new byte[1], budget);
        Assert.Throws<OperationCanceledException>(() => ContentReaders.Run(new FakeReader(null, null), source, "txt", ContentKind.Text, budget));
    }

    [Fact]
    public void Readers_by_extension()
    {
        Assert.Equal(11, ContentReaders.All.Count);
        Assert.IsType<PdfTextReader>(ContentReaders.For("pdf"));
        Assert.IsType<PdfTextReader>(ContentReaders.For("AI"));
        Assert.IsType<OoxmlReader>(ContentReaders.For("DOCX"));
        Assert.IsType<PlainTextReader>(ContentReaders.For("md"));
        Assert.IsType<PictureReader>(ContentReaders.For("heic"));
        Assert.Null(ContentReaders.For("xyz"));
        Assert.Null(ContentReaders.For(""));
    }

    [Fact]
    public void Unknown_extensions_are_unsupported_and_extra_text_goes_to_the_text_reader()
    {
        var budget = new ExtractBudget(ExtractLimits.Local, CancellationToken.None);
        using var source = new MemoryByteSource(new byte[10], budget);
        var unknown = ContentReaders.Extract(source, "xyz", ContentKind.None, budget);
        Assert.Equal(DocStatus.Unsupported, unknown.Status);
        var extra = ContentReaders.Extract(source, "log", ContentKind.Text, budget);
        Assert.Equal(ContentReaders.For("txt")!.Version, extra.ExtractorVersion);
        Assert.Same(ContentReaders.For("txt"), ContentReaders.ReaderFor("log", ContentKind.Text));
        Assert.Null(ContentReaders.ReaderFor("log", ContentKind.None));
        Assert.Equal(ContentReaders.For("txt")!.Version, ContentReaders.CurrentVersion("LOG", ContentKind.Text));
    }

    [Fact]
    public void Extractor_upgrades_read_again_only_some_statuses()
    {
        var v1 = new VersionedReader(ContentKind.Pdf, 1, "pdf");
        var v2 = new VersionedReader(ContentKind.Pdf, 2, "pdf");
        var v3 = new VersionedReader(ContentKind.Pdf, 3, "pdf");
        Assert.True(ExtractorChanges.ShouldReread(v2, 1, DocStatus.NoText));
        Assert.True(ExtractorChanges.ShouldReread(v2, 1, DocStatus.Damaged));
        Assert.True(ExtractorChanges.ShouldReread(v1, 0, DocStatus.Unsupported));
        Assert.False(ExtractorChanges.ShouldReread(v2, 1, DocStatus.Ok));
        Assert.False(ExtractorChanges.ShouldReread(v3, 1, DocStatus.PasswordProtected));
        Assert.False(ExtractorChanges.ShouldReread(v2, 2, DocStatus.NoText));
        Assert.False(ExtractorChanges.ShouldReread(v2, 3, DocStatus.NoText));
        Assert.False(ExtractorChanges.ShouldReread(null, 0, DocStatus.NoText));
        Assert.False(ExtractorChanges.ShouldReread("xyz", ContentKind.None, 0, DocStatus.Unsupported));
    }

    [Fact]
    public void Versions_are_per_reader_so_a_bump_reads_only_that_readers_documents_again_and_once()
    {
        // Two readers of one kind at different versions: Word bumped to 2, ODF still at 1.
        var ooxml = new VersionedReader(ContentKind.Office, 2, "docx");
        var odf = new VersionedReader(ContentKind.Office, 1, "odt");
        IContentReader[] readers = [ooxml, odf];
        IContentReader? Now(string extension) => ContentReaders.ReaderFor(extension, ContentKind.Office, readers);

        // Records from before the bump: only Word's are read again.
        Assert.True(ExtractorChanges.ShouldReread(Now("docx"), 1, DocStatus.NoText));
        Assert.False(ExtractorChanges.ShouldReread(Now("odt"), 1, DocStatus.NoText));

        // After reading, each document carries its own reader's version, so nothing is read again at the next reconcile.
        foreach (var extension in new[] { "docx", "odt" })
        {
            var reader = Now(extension)!;
            var doc = Run(reader, extension, ContentKind.Office);
            Assert.Equal(DocStatus.NoText, doc.Status);
            Assert.Equal(reader.Version, doc.ExtractorVersion);
            Assert.False(ExtractorChanges.ShouldReread(Now(extension), doc.ExtractorVersion, doc.Status));
        }
    }

    [Fact]
    public void Ai_files_are_Design_documents_stamped_and_compared_with_the_pdf_reader()
    {
        var pdf = ContentReaders.For("pdf")!;
        Assert.Same(pdf, ContentReaders.ReaderFor("ai", ContentKind.Design));
        Assert.Equal(pdf.Version, ContentReaders.CurrentVersion("ai", ContentKind.Design));

        var budget = new ExtractBudget(ExtractLimits.Local, CancellationToken.None);
        using var source = new MemoryByteSource(new byte[10], budget);
        var doc = ContentReaders.Extract(source, "ai", ContentKind.Design, budget);
        Assert.Equal(ContentKind.Design, doc.Kind);
        Assert.Equal(pdf.Version, doc.ExtractorVersion);
        Assert.False(ExtractorChanges.ShouldReread("ai", ContentKind.Design, doc.ExtractorVersion, doc.Status));

        // A PDF reader bump reaches AI files, and a bump of a Design reader doesn't.
        var bumped = new VersionedReader(ContentKind.Pdf, (byte)(pdf.Version + 1), "pdf", "ai");
        IContentReader[] readers = [bumped, new VersionedReader(ContentKind.Design, 9, "psd")];
        Assert.True(ExtractorChanges.ShouldReread(ContentReaders.ReaderFor("ai", ContentKind.Design, readers), pdf.Version, DocStatus.NoText));
        Assert.False(ExtractorChanges.ShouldReread(ContentReaders.ReaderFor("ai", ContentKind.Design, [pdf, readers[1]]), pdf.Version, DocStatus.NoText));
    }
}
