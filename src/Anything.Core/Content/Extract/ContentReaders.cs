using System.Runtime.ExceptionServices;

namespace Anything.Core.Content.Extract;

/// <summary>Reads one family of formats into a sink; may throw for bad input (ContentReaders maps it); must not recurse on file data.</summary>
internal interface IContentReader
{
    /// <summary>
    /// The kind of the files it is written for (documentation and tests). Documents are stored under their ContentTypes
    /// kind, which can differ: PdfTextReader also reads AI files, which are Design.
    /// </summary>
    ContentKind Kind { get; }
    /// <summary>
    /// This reader's own version, stamped on every document it reads and compared per reader (the reader that handles the
    /// document's extension; <see cref="ExtractorChanges"/>), so readers of one kind bump independently. Bump it when the
    /// output changes. An extension moved to another reader needs a version above the old reader's.
    /// </summary>
    byte Version { get; }
    /// <summary>Handles this extension (lower case, no dot).</summary>
    bool Handles(ReadOnlySpan<char> extension);
    /// <summary>
    /// Reads into the sink; returns Ok, Truncated (part of the file was skipped as damaged or over a per-unit cap, and the
    /// rest kept), NoText, PasswordProtected, Unsupported, Damaged or TooBig (a structure refused before reading, such as a
    /// zip with too many entries). NoText throws away body text already written (fields and picture details stay); the
    /// problem statuses keep nothing. Ok and Truncated become NoText when the body has no words (fields and picture details
    /// alone are no text), and Ok becomes Truncated when the sink is full.
    /// </summary>
    DocStatus Read(IByteSource source, ReadOnlySpan<char> extension, ExtractSink sink, ExtractBudget budget);
}

/// <summary>Extension → reader, and the one wrapper that turns exceptions and limits into a status.</summary>
internal static class ContentReaders
{
    /// <summary>PdfTextReader (U2); OoxmlReader, OdfReader, IdmlReader, PlainTextReader, RtfReader, EmlReader, MsgReader, PictureReader, PsdReader, XmpScanReader (U3).</summary>
    public static IReadOnlyList<IContentReader> All { get; } =
    [
        new PdfTextReader(), new OoxmlReader(), new OdfReader(), new IdmlReader(), new PlainTextReader(), new RtfReader(),
        new EmlReader(), new MsgReader(), new PictureReader(), new PsdReader(), new XmpScanReader(),
    ];

    /// <summary>The reader for an extension (any case, no dot), or null.</summary>
    public static IContentReader? For(ReadOnlySpan<char> extension) => For(extension, All);

    /// <summary>The reader that reads a file of this extension and kind: its own, or for a user's extra text extension (kind Text, no reader of its own) the plain-text reader.</summary>
    public static IContentReader? ReaderFor(ReadOnlySpan<char> extension, ContentKind kind) => ReaderFor(extension, kind, All);

    /// <summary>The version a document of this extension and kind is read with now (what ExtractorChanges compares stored versions with); 0 when no reader handles it.</summary>
    public static byte CurrentVersion(ReadOnlySpan<char> extension, ContentKind kind) => ReaderFor(extension, kind)?.Version ?? 0;

    /// <summary>ReaderFor over a given reader list (tests).</summary>
    internal static IContentReader? ReaderFor(ReadOnlySpan<char> extension, ContentKind kind, IReadOnlyList<IContentReader> readers)
        => For(extension, readers) ?? (kind == ContentKind.Text ? For("txt", readers) : null);

    /// <summary>Allocation-free (an index loop, no enumerator): the indexer asks for every stored document at a reconcile.</summary>
    static IContentReader? For(ReadOnlySpan<char> extension, IReadOnlyList<IContentReader> readers)
    {
        if (extension.IsEmpty || extension.Length > 16) return null;
        Span<char> lower = stackalloc char[extension.Length];
        extension.ToLowerInvariant(lower);
        for (int i = 0; i < readers.Count; i++)
            if (readers[i].Handles(lower)) return readers[i];
        return null;
    }

    /// <summary>
    /// Runs the reader under the budget; never throws except OperationCanceledException, OutOfMemoryException and
    /// ContentIoException. A user's extra text extension (kind Text, no reader of its own) is read as plain text. An empty
    /// source has no words and is no problem: NoText, without running the reader (as FileContentSource does for an empty
    /// file).
    /// </summary>
    public static ExtractedDoc Extract(IByteSource source, ReadOnlySpan<char> extension, ContentKind kind, ExtractBudget budget)
    {
        var reader = ReaderFor(extension, kind);
        if (reader == null) return ExtractedDoc.Failed(kind, DocStatus.Unsupported);
        if (source.Length == 0) return ExtractedDoc.Failed(kind, DocStatus.NoText, reader.Version);
        return Run(reader, source, extension, kind, budget);
    }

    /// <summary>
    /// The status mapping around one reader. Ok with words stays Ok (Truncated when the sink is full); Ok without words in
    /// the body is NoText; NoText keeps fields and picture details but no body; PasswordProtected and Unsupported keep
    /// nothing. A document-level limit or a reader exception (also one wrapping a limit) keeps what was read as Truncated
    /// whenever the sink holds words, fields or picture details; otherwise time → TimedOut, other limits → TooBig, other
    /// exceptions → Damaged. I/O errors (ContentIoException, also when wrapped) go back to the source, never Damaged.
    /// </summary>
    internal static ExtractedDoc Run(IContentReader reader, IByteSource source, ReadOnlySpan<char> extension, ContentKind kind, ExtractBudget budget)
    {
        var sink = new ExtractSink(budget.Limits);
        DocStatus status;
        try
        {
            budget.Check();
            status = reader.Read(source, extension, sink, budget);
            if (status is DocStatus.Ok or DocStatus.Truncated)
                status = !sink.HasWords ? DocStatus.NoText : sink.Full ? DocStatus.Truncated : status;
        }
        catch (OperationCanceledException) { throw; }
        catch (ContentIoException) { throw; }
        catch (ContentLimitException e) { status = LimitStatus(sink, e); }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            if (Inner<ContentIoException>(e) is { } io) ExceptionDispatchInfo.Throw(io);
            if (Inner<OperationCanceledException>(e) is { } oce) ExceptionDispatchInfo.Throw(oce);
            status = Inner<ContentLimitException>(e) is { } limit ? LimitStatus(sink, limit)
                : sink.HasContent ? DocStatus.Truncated : DocStatus.Damaged;
        }
        return sink.ToDoc(kind, status, reader.Version, source.BytesRead, (int)Math.Min(int.MaxValue, budget.ElapsedMs));
    }

    static DocStatus LimitStatus(ExtractSink sink, ContentLimitException e)
        => sink.HasContent ? DocStatus.Truncated : e.Limit == ContentLimitException.Time ? DocStatus.TimedOut : DocStatus.TooBig;

    static T? Inner<T>(Exception e) where T : Exception
    {
        for (var x = e.InnerException; x != null; x = x.InnerException)
            if (x is T t) return t;
        if (e is AggregateException a)
            foreach (var x in a.InnerExceptions)
                if (x is T t) return t;
        return null;
    }
}

/// <summary>
/// Per reader and version, which stored statuses an upgrade reads again (e.g. PdfTextReader v2: NoText and Damaged).
/// Versions are compared per reader, the one that handles the document's extension (ContentReaders.ReaderFor), because a
/// document is stamped with that reader's version: bumping OoxmlReader never touches ODF documents, and AI files (kind
/// Design) follow PdfTextReader.
/// </summary>
internal static class ExtractorChanges
{
    /// <summary>What a reader's version bump reads again; a bump with no entry reads again NoText, Damaged and Unsupported.</summary>
    static readonly (Type Reader, byte Version, DocStatus[] Statuses)[] Bumps =
    [
        // Mail v2: the subject is stored as Subject (it was Title), so every message read with its fields is read again.
        (typeof(EmlReader), 2, [DocStatus.Ok, DocStatus.Truncated, DocStatus.NoText, DocStatus.Damaged, DocStatus.Unsupported]),
        (typeof(MsgReader), 2, [DocStatus.Ok, DocStatus.Truncated, DocStatus.NoText, DocStatus.Damaged, DocStatus.Unsupported]),
    ];

    static readonly DocStatus[] DefaultStatuses = [DocStatus.NoText, DocStatus.Damaged, DocStatus.Unsupported];

    /// <summary>Whether a stored document of this extension and kind should be read again by the reader that handles it now.</summary>
    public static bool ShouldReread(ReadOnlySpan<char> extension, ContentKind kind, byte storedVersion, DocStatus storedStatus)
        => ShouldReread(ContentReaders.ReaderFor(extension, kind), storedVersion, storedStatus);

    /// <summary>The rule for a given reader (null = no reader handles the file: never).</summary>
    internal static bool ShouldReread(IContentReader? reader, byte storedVersion, DocStatus storedStatus)
    {
        if (reader == null) return false;
        var type = reader.GetType();
        for (int v = storedVersion + 1; v <= reader.Version; v++)
        {
            var statuses = DefaultStatuses;
            foreach (var b in Bumps)
                if (b.Reader == type && b.Version == v) { statuses = b.Statuses; break; }
            if (Array.IndexOf(statuses, storedStatus) >= 0) return true;
        }
        return false;
    }
}
