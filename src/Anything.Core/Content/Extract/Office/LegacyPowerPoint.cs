using System.Buffers.Binary;
using System.Text;

namespace Anything.Core.Content.Extract.Office;

/// <summary>
/// The text of PowerPoint 97-2003 presentations (.ppt, .pps, .pot: [MS-PPT]), for the preview pane when Office's own
/// previewer is missing, busy or not allowed. The "PowerPoint Document" stream is a tree of records (2 bytes version and
/// instance, 2 bytes type, 4 bytes length; version 0xF is a container of more records). Slides come in presentation order
/// from the Document's SlideListWithText (instance 0; masters and notes are left out), with the placeholder texts that
/// follow each SlidePersistAtom; each slide's own text boxes come from its Slide container, found through the persist
/// directory that the "Current User" stream leads to (the latest edit wins), or, when that stream is missing or leads
/// nowhere, the one the stream's last UserEditAtom leads to. Without that directory only the outline texts are given:
/// title masters are Slide containers too and can't be told from slides by their bytes, so pairing slides with
/// containers by stream order would put text boxes on the wrong slides.
/// Every offset and length is checked against the stream, a record running past its parent is cut to it, nesting is
/// walked with an explicit stack of bounded depth, and all the work (records visited, texts read, slides put together,
/// persist directory words read) is bounded by the stream's size, so a hostile file can neither throw nor run long.
/// </summary>
internal static class LegacyPowerPoint
{
    /// <summary>Largest stream read; a larger one means the file can't be read here.</summary>
    internal const long MaxStreamBytes = 64L << 20;
    const long MaxCurrentUserBytes = 64 << 10;
    const int MaxDepth = 64, MaxEdits = 4096, MaxPersistEntries = 1 << 20, MaxSlides = 1 << 16;

    const int RtDocument = 0x03E8, RtSlide = 0x03EE, RtSlidePersistAtom = 0x03F3, RtSlideListWithText = 0x0FF0,
        RtTextCharsAtom = 0x0FA0, RtTextBytesAtom = 0x0FA8, RtUserEditAtom = 0x0FF5, RtCurrentUserAtom = 0x0FF6,
        RtPersistDirectoryAtom = 0x1772, RtCryptSession10Container = 0x2F14, RtClientTextbox = 0xF00D;
    /// <summary>The CurrentUserAtom's header token of an encrypted presentation.</summary>
    const uint EncryptedToken = 0xF3D1C4DF;

    // Two streams of at most 64 MB each plus the compound file's own tables.
    static readonly ExtractLimits Limits = new(MaxBytesRead: 3 * MaxStreamBytes, MaxStreamBytes: MaxStreamBytes, MaxMilliseconds: 15_000);

    /// <summary>
    /// The text of each slide in presentation order (one string per slide, its lines separated by '\n'; a slide with no
    /// text is an empty string); null when the file is not a PowerPoint 97-2003 presentation, is encrypted, or cannot be
    /// read. maxChars limits the total over all slides: the slide that reaches it is cut there and the slides after it
    /// are left out, so the list can be shorter than the presentation. For maxChars above zero, the lengths of the
    /// strings add up to exactly maxChars when text or slides were left out at that limit (or less when the work bound
    /// is reached: text is then left out with a smaller total, and later slides come back empty), and to less when
    /// nothing was (the one exception: a presentation whose whole text is exactly maxChars long also adds up to
    /// maxChars), so a caller can tell when to say that more is not shown.
    /// </summary>
    public static IReadOnlyList<string>? ReadSlides(string path, int maxChars, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (string.IsNullOrEmpty(path)) return null;
            var budget = new ExtractBudget(Limits, ct);
            if (FileByteSource.TryOpen(Path.GetFullPath(path), budget, out var opened, out _) != null || opened == null) return null;
            using var source = opened;
            if (!CompoundFile.IsCompoundFile(source)) return null;
            var cfb = CompoundFile.TryOpen(source, budget);
            if (cfb == null || cfb.HasEncryptedPackage()) return null;
            int document = cfb.Find(cfb.Root, "PowerPoint Document");
            if (cfb.Type(document) != CompoundFile.EntryType.Stream || cfb.Size(document) > MaxStreamBytes) return null;
            var documentBytes = cfb.ReadStream(document, MaxStreamBytes);
            if (documentBytes == null) return null;
            byte[]? currentUserBytes = null;
            int currentUser = cfb.Find(cfb.Root, "Current User");
            if (cfb.Type(currentUser) == CompoundFile.EntryType.Stream && cfb.Size(currentUser) <= MaxCurrentUserBytes)
                currentUserBytes = cfb.ReadStream(currentUser, MaxCurrentUserBytes);
            return Parse(documentBytes, currentUserBytes, maxChars, ct);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            // A damaged file, a limit reached or an I/O error: the caller shows something else.
            return null;
        }
    }

    /// <summary>One record: its type, version and instance, and where its data lies (already cut to its parent).</summary>
    readonly record struct Record(int Start, int Type, int Version, int Instance, int Data, int End)
    {
        public bool IsContainer => Version == 0xF;
        public int Length => End - Data;
    }

    /// <summary>The bytes of one text atom: UTF-16LE (TextCharsAtom) or one byte per character (TextBytesAtom).</summary>
    readonly record struct TextRange(int Start, int End, bool Unicode);

    /// <summary>A slide listed in the outline: its persist id and its placeholder texts.</summary>
    sealed class Listed(uint persistId)
    {
        public uint PersistId { get; } = persistId;
        public List<TextRange> Outline { get; } = [];
    }

    /// <summary>The text of one Slide container's text boxes, built once; Cut when more text followed what it holds.</summary>
    readonly record struct BoxText(string Text, bool Cut);

    /// <summary>What reading one stream spent, for the tests that check the work stays bounded.</summary>
    internal readonly record struct WorkSpent(long Records, long Texts, long Slides, long DirectoryWords, long Edits);

    /// <summary>
    /// What walking UserEditAtom chains may cost, shared by every walk over one stream so that a second walk (from the
    /// last UserEditAtom when the Current User stream leads nowhere) adds nothing to the bounds: edits visited, directory
    /// words and entries read, and the edits and directories already seen.
    /// </summary>
    sealed class DirectoryWalk(int streamLength)
    {
        // Each edit writes a directory of its own, so in a well-formed stream the directories don't overlap and their
        // words add up to less than the stream's words (its length / 4). Twice that, shared by all directories, lets every
        // word be read about twice; directories that overlap (each running on to the stream's end, say) stop there.
        public long WordsLeft = streamLength / 4 * 2 + 1024;
        public long WordsRead, Entries;
        public int Edits;
        public readonly HashSet<uint> SeenEdits = [], SeenDirectories = [];
    }

    /// <summary>
    /// The work one stream may cost, shared by every pass over it: a record visited, a slide put together and a text
    /// read each cost one unit, and a text one more per 16 bytes it holds (it is read byte by byte).
    /// </summary>
    sealed class Work(long allowed)
    {
        long _left = allowed;
        public long Records, Texts, Slides;
        public bool Starved => _left <= 0;

        bool Take(long units)
        {
            if (_left < units) { _left = 0; return false; }
            _left -= units;
            return true;
        }

        public bool Record() { if (!Take(1)) return false; Records++; return true; }
        public bool Slide() { if (!Take(1)) return false; Slides++; return true; }
        public bool Text(TextRange range)
        {
            if (!Take(1 + (range.End - range.Start) / 16)) return false;
            Texts++;
            return true;
        }
    }

    /// <summary>
    /// The slides of a presentation from the bytes of its "PowerPoint Document" stream and, when there is one, its
    /// "Current User" stream; null when the stream holds no Document container or the presentation is encrypted.
    /// </summary>
    internal static List<string>? Parse(ReadOnlySpan<byte> document, ReadOnlySpan<byte> currentUser, int maxChars, CancellationToken ct)
        => Parse(document, currentUser, maxChars, ct, out _);

    /// <inheritdoc cref="Parse(ReadOnlySpan{byte}, ReadOnlySpan{byte}, int, CancellationToken)"/>
    internal static List<string>? Parse(ReadOnlySpan<byte> document, ReadOnlySpan<byte> currentUser, int maxChars, CancellationToken ct, out WorkSpent spent)
    {
        spent = default;
        if (document.Length < 8) return null;
        // Every record takes 8 bytes or more and every byte belongs to at most one text, so a well-formed stream, where
        // each record is visited once, spends well under half its length in units (records, texts by size, and one per
        // slide, each slide having a record of its own). Half the length leaves room for that however the persist
        // directory points slides at overlapping containers, and stops a hostile one soon after.
        var work = new Work(document.Length / 2 + 4096);
        var walk = new DirectoryWalk(document.Length);
        try
        {
            // Top level: the last Document container, the last UserEditAtom, and an encryption container.
            int lastDocument = -1, lastEdit = -1, at = 0, records = 0;
            while (Read(document, at, document.Length, out var r))
            {
                if ((++records & 4095) == 0) ct.ThrowIfCancellationRequested();
                if (r.Type == RtCryptSession10Container) return null;
                if (r.IsContainer && r.Type == RtDocument) lastDocument = r.Start;
                else if (!r.IsContainer && r.Type == RtUserEditAtom) lastEdit = r.Start;
                at = r.End;
            }
            if (records == 0) return null;

            // The persist directory, from the latest edit back; later edits win.
            Dictionary<uint, int>? persist = null;
            uint documentRef = 0;
            if (CurrentEdit(currentUser, out uint editAt, out bool encryptedUser))
            {
                if (encryptedUser) return null;
                persist = PersistDirectory(document, editAt, walk, out documentRef, out bool encrypted, ct);
                if (encrypted) return null;
                if (persist.Count == 0) persist = null;
            }
            // A missing, damaged or wrong Current User stream: Office appends each save, so the last UserEditAtom in the
            // stream is the latest edit. The walk shares the first one's bounds and stops at edits it already saw.
            if (persist == null && lastEdit >= 0)
            {
                persist = PersistDirectory(document, (uint)lastEdit, walk, out documentRef, out bool encrypted, ct);
                if (encrypted) return null;
                if (persist.Count == 0) persist = null;
            }

            int documentAt = -1;
            if (persist != null && persist.TryGetValue(documentRef, out int referenced)
                && Read(document, referenced, document.Length, out var d) && d.IsContainer && d.Type == RtDocument)
                documentAt = referenced;
            if (documentAt < 0) documentAt = lastDocument;
            if (documentAt < 0 || !Read(document, documentAt, document.Length, out var documentRecord)) return null;

            var listed = Outline(document, documentRecord, work, ct);

            // Each slide's Slide container, when the persist directory says where it is.
            var slideAt = new int[listed.Count];
            for (int i = 0; i < listed.Count; i++)
                slideAt[i] = persist != null && persist.TryGetValue(listed[i].PersistId, out int o) ? o : -1;

            return Assemble(document, listed, slideAt, maxChars, work, ct);
        }
        finally
        {
            spent = new WorkSpent(work.Records, work.Texts, work.Slides, walk.WordsRead, walk.Edits);
        }
    }

    /// <summary>Reads the record header at a position inside [.., end); false when it doesn't fit.</summary>
    static bool Read(ReadOnlySpan<byte> s, long at, int end, out Record record)
    {
        record = default;
        if (at < 0 || end > s.Length || at > end - 8L) return false;
        int start = (int)at;
        int versionInstance = BinaryPrimitives.ReadUInt16LittleEndian(s[start..]);
        int type = BinaryPrimitives.ReadUInt16LittleEndian(s[(start + 2)..]);
        uint length = BinaryPrimitives.ReadUInt32LittleEndian(s[(start + 4)..]);
        int data = start + 8;
        int recordEnd = (int)Math.Min(data + (long)length, end);
        record = new Record(start, type, versionInstance & 0xF, versionInstance >> 4, data, recordEnd);
        return true;
    }

    static uint U32(ReadOnlySpan<byte> s, int at) => BinaryPrimitives.ReadUInt32LittleEndian(s[at..]);

    /// <summary>The CurrentUserAtom's position of the latest UserEditAtom; false when the stream is missing or isn't one.</summary>
    static bool CurrentEdit(ReadOnlySpan<byte> currentUser, out uint editAt, out bool encrypted)
    {
        editAt = 0;
        encrypted = false;
        // Header (8), size (4), header token (4), offset to the current edit (4).
        if (currentUser.Length < 20 || !Read(currentUser, 0, currentUser.Length, out var r) || r.Type != RtCurrentUserAtom || r.Length < 12)
            return false;
        encrypted = U32(currentUser, r.Data + 4) == EncryptedToken;
        editAt = U32(currentUser, r.Data + 8);
        return true;
    }

    /// <summary>
    /// Walks the UserEditAtom chain from the latest edit back (bounded, a loop stops it) and merges the persist directories
    /// they point at, keeping the first position seen for each persist id. Also gives the latest edit's Document persist id
    /// and whether any edit names an encryption session. Edits, directories and words are counted in the shared walk.
    /// </summary>
    static Dictionary<uint, int> PersistDirectory(ReadOnlySpan<byte> s, uint editAt, DirectoryWalk walk, out uint documentRef, out bool encrypted, CancellationToken ct)
    {
        var map = new Dictionary<uint, int>();
        documentRef = 0;
        encrypted = false;
        bool latest = true;
        uint position = editAt;
        for (; walk.Edits < MaxEdits && walk.SeenEdits.Add(position); walk.Edits++)
        {
            ct.ThrowIfCancellationRequested();
            // UserEditAtom data: lastSlideIdRef (4), version (4), offsetLastEdit (4), offsetPersistDirectory (4),
            // docPersistIdRef (4), persistIdSeed (4), lastView and unused (4), then encryptSessionPersistIdRef (4) when present.
            if (!Read(s, position, s.Length, out var edit) || edit.Type != RtUserEditAtom || edit.Length < 16) break;
            if (edit.Length >= 32 && U32(s, edit.Data + 28) != 0) { encrypted = true; return map; }
            if (latest)
            {
                documentRef = edit.Length >= 20 ? U32(s, edit.Data + 16) : 0;
                latest = false;
            }
            uint directoryAt = U32(s, edit.Data + 12);
            if (walk.SeenDirectories.Add(directoryAt) && Read(s, directoryAt, s.Length, out var directory) && directory.Type == RtPersistDirectoryAtom)
            {
                // Blocks: first persist id (low 20 bits) and count (high 12 bits), then that many stream positions.
                // Every word read, block heads included, is charged; when the words run out, what was merged is kept.
                int p = directory.Data;
                while (p <= directory.End - 4)
                {
                    if (--walk.WordsLeft < 0) return map;
                    walk.WordsRead++;
                    uint head = U32(s, p);
                    p += 4;
                    uint first = head & 0xFFFFF, count = head >> 20;
                    for (uint i = 0; i < count && p <= directory.End - 4; i++, p += 4)
                    {
                        if (--walk.WordsLeft < 0 || ++walk.Entries > MaxPersistEntries) return map;
                        walk.WordsRead++;
                        uint offset = U32(s, p);
                        if (offset < (uint)s.Length) map.TryAdd(first + i, (int)offset);
                    }
                }
            }
            uint previous = U32(s, edit.Data + 8);
            if (previous == 0) break;
            position = previous;
        }
        return map;
    }

    /// <summary>The slides of the Document's first SlideListWithText of instance 0, each with the placeholder texts after it.</summary>
    static List<Listed> Outline(ReadOnlySpan<byte> s, Record documentRecord, Work work, CancellationToken ct)
    {
        var listed = new List<Listed>();
        if (!documentRecord.IsContainer) return listed;
        int at = documentRecord.Data;
        while (Read(s, at, documentRecord.End, out var child))
        {
            if (!work.Record()) return listed;
            if ((work.Records & 4095) == 0) ct.ThrowIfCancellationRequested();
            at = child.End;
            if (child.Type != RtSlideListWithText || !child.IsContainer || child.Instance != 0) continue;
            Listed? current = null;
            int p = child.Data;
            while (Read(s, p, child.End, out var r))
            {
                if (!work.Record()) return listed;
                if ((work.Records & 4095) == 0) ct.ThrowIfCancellationRequested();
                p = r.End;
                if (r.Type == RtSlidePersistAtom && !r.IsContainer)
                {
                    if (listed.Count >= MaxSlides) return listed;
                    current = new Listed(r.Length >= 4 ? U32(s, r.Data) : 0);
                    listed.Add(current);
                }
                else if (current != null && !r.IsContainer && r.Type is RtTextCharsAtom or RtTextBytesAtom)
                    current.Outline.Add(new TextRange(r.Data, r.End, r.Type == RtTextCharsAtom));
            }
            return listed;
        }
        return listed;
    }

    /// <summary>
    /// The text atoms inside the client text boxes of the Slide container at a position, in stream order. Placeholders
    /// that only point at the outline (OutlineTextRefAtom) hold no text atom, so nothing is counted twice. Containers
    /// deeper than MaxDepth are skipped.
    /// </summary>
    static List<TextRange> SlideTexts(ReadOnlySpan<byte> s, int slideAt, Work work, CancellationToken ct)
    {
        var ranges = new List<TextRange>();
        if (!Read(s, slideAt, s.Length, out var slide) || slide.Type != RtSlide || !slide.IsContainer) return ranges;
        var stack = new Stack<(int Position, int End, bool InBox)>();
        int position = slide.Data, end = slide.End;
        bool inBox = false;
        while (true)
        {
            while (Read(s, position, end, out var r))
            {
                if (!work.Record()) return ranges;
                if ((work.Records & 4095) == 0) ct.ThrowIfCancellationRequested();
                position = r.End;
                if (r.IsContainer)
                {
                    if (stack.Count >= MaxDepth) continue;
                    stack.Push((position, end, inBox));
                    position = r.Data;
                    end = r.End;
                    inBox |= r.Type == RtClientTextbox;
                }
                else if (inBox && r.Type is RtTextCharsAtom or RtTextBytesAtom)
                    ranges.Add(new TextRange(r.Data, r.End, r.Type == RtTextCharsAtom));
            }
            if (stack.Count == 0) return ranges;
            (position, end, inBox) = stack.Pop();
        }
    }

    /// <summary>
    /// The text of a Slide container's text boxes, one per line, at most limit characters, built once and kept for every
    /// slide that points at the same container.
    /// </summary>
    static BoxText Boxes(ReadOnlySpan<byte> s, int slideAt, int limit, Work work, CancellationToken ct)
    {
        var text = new StringBuilder();
        foreach (var range in SlideTexts(s, slideAt, work, ct))
        {
            if (!work.Text(range)) break;
            if (!Append(s, range, text, limit)) return new BoxText(text.ToString(), true);
        }
        return new BoxText(text.ToString(), false);
    }

    /// <summary>
    /// Each slide's text: outline texts, then its own text boxes, one per line; at most maxChars in all, and exactly
    /// maxChars when something was left out. A slide reached after the work ran out is given as an empty string.
    /// </summary>
    static List<string> Assemble(ReadOnlySpan<byte> s, List<Listed> listed, int[] slideAt, int maxChars, Work work, CancellationToken ct)
    {
        var slides = new List<string>(Math.Min(listed.Count, 1024));
        var boxes = new Dictionary<int, BoxText>();
        long used = 0, limit = Math.Max(0, maxChars);
        var text = new StringBuilder();
        for (int i = 0; i < listed.Count; i++)
        {
            if ((i & 255) == 0) ct.ThrowIfCancellationRequested();
            if (used >= limit && i > 0) break;
            if (work.Starved || !work.Slide()) { slides.Add(""); continue; }
            text.Clear();
            bool cut = false;
            int room = (int)Math.Min(int.MaxValue, limit - used);
            foreach (var range in listed[i].Outline)
            {
                if (!work.Text(range)) break;
                if (!Append(s, range, text, room)) { cut = true; break; }
            }
            if (!cut && slideAt[i] >= 0 && !work.Starved)
            {
                if (!boxes.TryGetValue(slideAt[i], out var box))
                {
                    box = Boxes(s, slideAt[i], (int)Math.Min(int.MaxValue, limit), work, ct);
                    boxes[slideAt[i]] = box;
                }
                cut = !AppendBoxes(text, box, room);
            }
            used += text.Length;
            slides.Add(text.ToString());
            if (cut) break;
        }
        return slides;
    }

    /// <summary>
    /// Appends a container's box text on a line of its own after the outline texts; false when it didn't all fit, and
    /// then the builder holds exactly room characters (a surrogate pair split by the cut becomes U+FFFD).
    /// </summary>
    static bool AppendBoxes(StringBuilder text, BoxText box, int room)
    {
        if (box.Text.Length == 0 && !box.Cut) return true;
        if (text.Length > 0)
        {
            if (text.Length >= room) return false;
            text.Append('\n');
        }
        int fit = Math.Min(box.Text.Length, room - text.Length);
        // A high surrogate in the box text is always followed by its low one, so one at the cut means a split pair.
        if (fit > 0 && fit < box.Text.Length && char.IsHighSurrogate(box.Text[fit - 1]))
            text.Append(box.Text, 0, fit - 1).Append('\uFFFD');
        else
            text.Append(box.Text, 0, fit);
        return fit == box.Text.Length && !box.Cut;
    }

    /// <summary>
    /// Appends one text on a line of its own: 0x0D and 0x0B become '\n', other control characters below 0x20 except tab
    /// are dropped, a lone surrogate becomes U+FFFD; trailing line ends are trimmed and an empty text adds nothing. False
    /// when the builder reached room before the text ended, and then it holds exactly room characters: nothing is trimmed,
    /// and a surrogate pair that doesn't fit becomes U+FFFD.
    /// </summary>
    static bool Append(ReadOnlySpan<byte> s, TextRange range, StringBuilder text, int room)
    {
        int mark = text.Length;
        if (mark > 0)
        {
            if (mark >= room) return false;
            text.Append('\n');
        }
        int contentStart = text.Length;
        if (range.Unicode)
        {
            for (int p = range.Start; p <= range.End - 2; p += 2)
            {
                char c = (char)BinaryPrimitives.ReadUInt16LittleEndian(s[p..]);
                if (char.IsHighSurrogate(c) && p <= range.End - 4 && char.IsLowSurrogate((char)BinaryPrimitives.ReadUInt16LittleEndian(s[(p + 2)..])))
                {
                    if (text.Length > room - 2)
                    {
                        if (text.Length < room) text.Append('\uFFFD');
                        return false;
                    }
                    text.Append(c).Append((char)BinaryPrimitives.ReadUInt16LittleEndian(s[(p + 2)..]));
                    p += 2;
                    continue;
                }
                if (char.IsSurrogate(c)) c = '\uFFFD';
                if (!Put(text, c, room)) return false;
            }
        }
        else
        {
            for (int p = range.Start; p < range.End; p++)
                if (!Put(text, (char)s[p], room)) return false;
        }
        // The whole text fitted: trim trailing line ends, and drop the separator when the text was empty.
        int length = text.Length;
        while (length > contentStart && text[length - 1] == '\n') length--;
        text.Length = length == contentStart ? mark : length;
        return true;
    }

    /// <summary>Appends one character after the control character rules; false when there is no room for it.</summary>
    static bool Put(StringBuilder text, char c, int room)
    {
        if (c is '\r' or '\v') c = '\n';
        else if (c < ' ' && c != '\t') return true;
        if (text.Length >= room) return false;
        text.Append(c);
        return true;
    }
}
