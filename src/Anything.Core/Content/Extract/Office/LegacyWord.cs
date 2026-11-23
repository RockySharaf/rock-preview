using System.Buffers.Binary;
using System.Text;

namespace Anything.Core.Content.Extract.Office;

/// <summary>
/// The text of Word 97-2003 binary documents (.doc, .dot; [MS-DOC]) for the preview pane, read without Word: the
/// "WordDocument" stream starts with the FIB, which gives the character count of each story (ccpText, ccpFtn and so on),
/// which table stream holds the piece table ("0Table" or "1Table") and where in it the Clx is. The piece table maps
/// character positions to runs of 8-bit (Windows-1252) or UTF-16 text in the WordDocument stream. The main document is
/// read, then the text boxes, footnotes and endnotes, each after an empty line; headers, footers and comments are left
/// out. Field instructions are dropped and their results kept; paragraph, line and page ends become '\n', a table cell
/// mark a tab and a table row mark '\n' (rows found through the paragraph properties), and other control characters are
/// left out.
/// </summary>
/// <remarks>
/// The file is untrusted: each stream is capped at 64 MB, every offset and length is checked before use, and the work is
/// bounded by the WordDocument stream's size (pieces that overlap or pages listed again can't multiply it). A bad file
/// gives null; paragraph properties that can't be read only mean every table mark becomes a tab.
/// </remarks>
internal static class LegacyWord
{
    /// <summary>Largest stream read; a larger one means the file isn't read.</summary>
    internal const int MaxStreamBytes = 64 << 20;

    /// <summary>Most open fields whose state is kept one by one; fields nested deeper count as hidden until they end.</summary>
    const int MaxFieldDepth = 256;

    /// <summary>Most table-row-end paragraphs kept; past it rows aren't told apart from cells anywhere in the document.</summary>
    internal const int MaxRowEndRuns = 1_000_000;

    const ushort Word97Ident = 0xA5EC;
    const ushort FlagEncrypted = 0x0100, FlagWhichTable = 0x0200;
    const int FcPlcfBtePapxIndex = 13; // fcPlcfBtePapx/lcbPlcfBtePapx is the 14th pair of FibRgFcLcb97
    const int FcClxIndex = 33; // fcClx/lcbClx is the 34th pair of FibRgFcLcb97
    const int FkpBytes = 512;
    const ushort SprmPFTtp = 0x2417, SprmTDefTable = 0xD608, SprmPChgTabs = 0xC615;

    /// <summary>
    /// The document's text, paragraphs separated by '\n': the main document, then its text boxes, footnotes and endnotes,
    /// each after an empty line; table cells separated by tabs, one row per line. Null when the file is not a Word
    /// 97-2003 document, is encrypted, or cannot be read.
    /// </summary>
    public static string? ReadText(string path, int maxChars, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (maxChars <= 0 || string.IsNullOrEmpty(path)) return null;
        try
        {
            string full = Path.GetFullPath(path);
            // Two capped streams plus the FAT and directory; the time limit keeps a slow share from holding the preview.
            var budget = new ExtractBudget(new ExtractLimits(MaxBytesRead: 2L * MaxStreamBytes + (16 << 20), MaxMilliseconds: 30_000), ct);
            FileByteSource.TryOpen(full, budget, out var source, out _);
            if (source == null) return null;
            using (source)
            {
                if (!CompoundFile.IsCompoundFile(source)) return null;
                var cfb = CompoundFile.TryOpen(source, budget);
                if (cfb == null || cfb.HasEncryptedPackage()) return null;
                var document = Stream(cfb, "WordDocument");
                if (document == null || document.Length < 12) return null;
                // Only the table stream the FIB names is read.
                ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(document.AsSpan(0x0A));
                bool one = (flags & FlagWhichTable) != 0;
                var table = Stream(cfb, one ? "1Table" : "0Table");
                return FromStreams(document, one ? null : table, one ? table : null, maxChars, ct);
            }
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { return null; }
    }

    /// <summary>A stream of the root storage, or null when it's missing, broken or over the cap.</summary>
    static byte[]? Stream(CompoundFile cfb, string name)
    {
        int id = cfb.Find(cfb.Root, name);
        if (id < 0 || cfb.Type(id) != CompoundFile.EntryType.Stream || cfb.Size(id) > MaxStreamBytes) return null;
        return cfb.ReadStream(id, MaxStreamBytes);
    }

    /// <summary>
    /// The pure layer: the text from the WordDocument stream and the table streams (either may be null), at most maxChars
    /// characters. Null when the stream isn't Word 97 or later, is encrypted, has no readable piece table, or nothing could
    /// be read; "" for a document that was read whole and has no text.
    /// </summary>
    internal static string? FromStreams(ReadOnlySpan<byte> document, byte[]? table0, byte[]? table1, int maxChars, CancellationToken ct = default)
    {
        if (maxChars <= 0 || document.Length < 0x22) return null;
        if (BinaryPrimitives.ReadUInt16LittleEndian(document) != Word97Ident) return null; // 0xA5DC is Word 6/95
        ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(document[0x0A..]);
        if ((flags & FlagEncrypted) != 0) return null;
        var table = (flags & FlagWhichTable) != 0 ? table1 : table0;
        if (table == null) return null;

        // The FIB's variable parts: csw 16-bit values (FibRgW97), cslw 32-bit values (FibRgLw97, ccpText the 4th and the
        // other stories' counts the 5th to 11th), then cbRgFcLcb pairs of 32-bit values (FibRgFcLcb, fcClx/lcbClx the
        // 34th). Word writes 14, 22 and at least 93, which puts ccpText at 0x4C and fcClx at 0x1A2; the counts are followed
        // so another writer's FIB reads as well.
        int csw = BinaryPrimitives.ReadUInt16LittleEndian(document[0x20..]);
        int lwCountAt = 0x22 + csw * 2;
        if (lwCountAt + 2 > document.Length) return null;
        int cslw = BinaryPrimitives.ReadUInt16LittleEndian(document[lwCountAt..]);
        int lwAt = lwCountAt + 2;
        if (cslw < 4) return null;
        int pairCountAt = lwAt + cslw * 4;
        if (pairCountAt + 2 > document.Length) return null;
        int ccpText = BinaryPrimitives.ReadInt32LittleEndian(document[(lwAt + 12)..]);
        int pairs = BinaryPrimitives.ReadUInt16LittleEndian(document[pairCountAt..]);
        int clxAt = pairCountAt + 2 + FcClxIndex * 8;
        if (pairs <= FcClxIndex || clxAt + 8 > document.Length || ccpText < 0) return null;
        uint fcClx = BinaryPrimitives.ReadUInt32LittleEndian(document[clxAt..]);
        uint lcbClx = BinaryPrimitives.ReadUInt32LittleEndian(document[(clxAt + 4)..]);
        if (lcbClx == 0 || fcClx > (uint)table.Length || lcbClx > (uint)table.Length - fcClx) return null;
        var clx = table.AsSpan((int)fcClx, (int)lcbClx);

        if (!PieceTable(clx, out var plc, out int pieces)) return null;
        var stories = Stories(document, lwAt, cslw, ccpText);
        var rows = RowEnds.Find(document, table, pairs, pairCountAt + 2, ct);
        return Text(document, plc, pieces, stories, rows, maxChars, ct);
    }

    /// <summary>
    /// The stories read, in the order they are shown, as character ranges: the main document, the text boxes, the
    /// footnotes, the endnotes. In the character stream the stories follow one another as footnotes, headers, macros,
    /// comments, endnotes, text boxes, header text boxes (FibRgLw97's 5th to 11th values). A negative count ends the list
    /// before the first story placed at or after it; a FIB too short for the counts gives the main document alone.
    /// </summary>
    static List<(long Start, long Length)> Stories(ReadOnlySpan<byte> document, int lwAt, int cslw, int ccpText)
    {
        var stories = new List<(long, long)>(4) { (0, ccpText) };
        if (cslw < 11 || lwAt + 44 > document.Length) return stories;
        // ccpFtn, ccpHdd, ccpMcr, ccpAtn, ccpEdn, ccpTxbx, ccpHdrTxbx.
        var counts = new long[7];
        for (int i = 0; i < 7; i++) counts[i] = BinaryPrimitives.ReadInt32LittleEndian(document[(lwAt + 16 + i * 4)..]);
        long Start(int story)
        {
            long at = ccpText;
            for (int i = 0; i < story; i++) at += counts[i];
            return at;
        }
        bool Valid(int story)
        {
            for (int i = 0; i <= story; i++) if (counts[i] < 0) return false;
            return true;
        }
        foreach (int story in (ReadOnlySpan<int>)[5, 0, 4]) // text boxes, footnotes, endnotes
        {
            if (!Valid(story)) break;
            stories.Add((Start(story), counts[story]));
        }
        return stories;
    }

    /// <summary>
    /// Finds the PlcPcd in a Clx: Prc blocks (0x01, a 16-bit size, that many bytes) are skipped up to the Pcdt (0x02, a
    /// 32-bit size, the PlcPcd). False when the Clx doesn't hold one with at least one piece.
    /// </summary>
    static bool PieceTable(ReadOnlySpan<byte> clx, out ReadOnlySpan<byte> plc, out int pieces)
    {
        plc = default;
        pieces = 0;
        int at = 0;
        // Each Prc moves on at least 3 bytes, so the loop ends within the Clx.
        while (at < clx.Length && clx[at] == 0x01)
        {
            if (clx.Length - at < 3) return false;
            at += 3 + BinaryPrimitives.ReadUInt16LittleEndian(clx[(at + 1)..]);
        }
        if (at >= clx.Length || clx[at] != 0x02 || clx.Length - at < 5) return false;
        uint lcb = BinaryPrimitives.ReadUInt32LittleEndian(clx[(at + 1)..]);
        at += 5;
        if (lcb < 16 || lcb > (uint)(clx.Length - at)) return false;
        pieces = (int)((lcb - 4) / 12);
        plc = clx.Slice(at, (int)lcb);
        return pieces > 0;
    }

    /// <summary>
    /// Reads each story through the pieces, cleaning each character up as it goes. A piece out of order or outside the
    /// stream ends the reading with what was read before it; so does a story after the main one that reaches past the
    /// piece table.
    /// </summary>
    static string? Text(ReadOnlySpan<byte> document, ReadOnlySpan<byte> plc, int pieces, List<(long Start, long Length)> stories,
        RowEnds? rows, int maxChars, CancellationToken ct)
    {
        long total = 0;
        foreach (var s in stories) total += s.Length;
        var text = new Cleaner((int)Math.Min(maxChars, Math.Min(total, 1 << 16)), maxChars);
        // Every real character takes at least one byte of the stream, so more characters than that means pieces that
        // share bytes: the work stops there instead of growing with the piece count.
        long budget = document.Length;
        long lastCp = BinaryPrimitives.ReadInt32LittleEndian(plc[(pieces * 4)..]);
        bool whole = true;
        for (int s = 0; s < stories.Count && whole && !text.Full; s++)
        {
            var (from, length) = stories[s];
            if (length == 0) continue;
            if (s > 0 && from + length > lastCp) { whole = false; break; }
            text.StartStory(notes: s >= 2); // the list is main, text boxes, footnotes, endnotes
            whole = Story(document, plc, pieces, from, from + length, rows, text, ref budget, ct);
        }
        string result = text.ToString();
        return result.Length == 0 && !whole ? null : result;
    }

    /// <summary>Reads the characters [from, to) through the pieces; false when a piece is out of order or outside the stream.</summary>
    static bool Story(ReadOnlySpan<byte> document, ReadOnlySpan<byte> plc, int pieces, long from, long to, RowEnds? rows,
        Cleaner text, ref long budget, CancellationToken ct)
    {
        int pcdAt = (pieces + 1) * 4;
        int previousEnd = 0;
        for (int i = 0; i < pieces && !text.Full; i++)
        {
            if ((i & 0x3FFF) == 0x3FFF) ct.ThrowIfCancellationRequested();
            int start = BinaryPrimitives.ReadInt32LittleEndian(plc[(i * 4)..]);
            int end = BinaryPrimitives.ReadInt32LittleEndian(plc[(i * 4 + 4)..]);
            if (start >= to) break;
            if (start < previousEnd || end < start) return false;
            previousEnd = end;
            if (end <= from) continue;
            long first = Math.Max(start, from);
            int count = (int)(Math.Min(end, to) - first);
            if (count == 0) continue;
            if (count > budget) return false;
            budget -= count;

            uint fc = BinaryPrimitives.ReadUInt32LittleEndian(plc[(pcdAt + i * 8 + 2)..]);
            bool compressed = (fc & 0x40000000) != 0;
            int width = compressed ? 1 : 2;
            // The stream position of the piece's first character read: positions are what the paragraph runs use.
            long offset = (compressed ? (fc & 0x3FFFFFFF) / 2 : fc & 0x3FFFFFFF) + (first - start) * width;
            long bytes = (long)count * width;
            if (offset + bytes > document.Length) return false;
            var run = document.Slice((int)offset, (int)bytes);
            for (int k = 0; k < count && !text.Full; k++)
            {
                if ((k & 0xFFFF) == 0xFFFF) ct.ThrowIfCancellationRequested();
                char c = compressed ? Cp1252(run[k]) : (char)BinaryPrimitives.ReadUInt16LittleEndian(run[(k * 2)..]);
                if (c == '\u0007' && rows != null && rows.Contains(offset + (long)k * width)) text.EndRow();
                else text.Add(c);
            }
        }
        return true;
    }

    /// <summary>Windows-1252: 0x80-0x9F through its table (the five unassigned bytes keep their value), the rest as Latin-1.</summary>
    internal static char Cp1252(byte b) => b is >= 0x80 and <= 0x9F ? HighTable[b - 0x80] : (char)b;

    static ReadOnlySpan<char> HighTable =>
    [
        '€', '\u0081', '‚', 'ƒ', '„', '…', '†', '‡', // 80-87
        'ˆ', '‰', 'Š', '‹', 'Œ', '\u008D', 'Ž', '\u008F', // 88-8F
        '\u0090', '‘', '’', '“', '”', '•', '–', '—', // 90-97
        '˜', '™', 'š', '›', 'œ', '\u009D', 'ž', 'Ÿ', // 98-9F
    ];

    /// <summary>
    /// The stream positions of table-row-end paragraphs. A row ends with a paragraph that is a lone 0x07 mark whose
    /// properties hold sprmPFTtp; a cell ends with a 0x07 too, so only the properties tell them apart. Paragraph
    /// properties come from the PlcBtePapx (FibRgFcLcb97's 14th pair): n+1 stream positions, then n page numbers of
    /// 512-byte PapxFkp pages in the WordDocument stream, each a run of paragraphs with their properties.
    /// </summary>
    internal sealed class RowEnds
    {
        readonly int[] _starts, _ends; // sorted, merged ranges [start, end)

        RowEnds(int[] starts, int[] ends) { _starts = starts; _ends = ends; }

        public int Count => _starts.Length;

        public bool Contains(long position)
        {
            int lo = 0, hi = _starts.Length - 1;
            while (lo <= hi)
            {
                int mid = (lo + hi) >>> 1;
                if (_starts[mid] <= position) lo = mid + 1;
                else hi = mid - 1;
            }
            return hi >= 0 && position < _ends[hi];
        }

        /// <summary>
        /// The row ends, or null when the properties can't be read at all or a cap is reached: then every 0x07 is a cell
        /// mark throughout, never rows in one part and not in another. Broken pages and runs are skipped.
        /// </summary>
        public static RowEnds? Find(ReadOnlySpan<byte> document, byte[] table, int pairs, int pairsAt, CancellationToken ct)
        {
            int at = pairsAt + FcPlcfBtePapxIndex * 8;
            if (pairs <= FcPlcfBtePapxIndex || at + 8 > document.Length) return null;
            uint fc = BinaryPrimitives.ReadUInt32LittleEndian(document[at..]);
            uint lcb = BinaryPrimitives.ReadUInt32LittleEndian(document[(at + 4)..]);
            if (lcb < 12 || (lcb - 4) % 8 != 0 || fc > (uint)table.Length || lcb > (uint)table.Length - fc) return null;
            var plc = table.AsSpan((int)fc, (int)lcb);
            int n = (int)((lcb - 4) / 8);
            int pagesInStream = document.Length / FkpBytes;
            // A page listed many times is read each time; reading more pages than the stream holds means repeats.
            int pageBudget = pagesInStream;
            var starts = new List<int>();
            var ends = new List<int>();
            for (int i = 0; i < n; i++)
            {
                if ((i & 0x3FF) == 0x3FF) ct.ThrowIfCancellationRequested();
                // PnFkpPapx: the page number is the low 22 bits.
                uint pn = BinaryPrimitives.ReadUInt32LittleEndian(plc[((n + 1) * 4 + i * 4)..]) & 0x3FFFFF;
                if (pn >= (uint)pagesInStream) continue;
                if (--pageBudget < 0) return null;
                var page = document.Slice((int)pn * FkpBytes, FkpBytes);
                int crun = page[FkpBytes - 1];
                // crun+1 positions and crun 13-byte BxPap entries must fit before the count byte.
                if (crun == 0 || (crun + 1) * 4 + crun * 13 > FkpBytes - 1) continue;
                for (int r = 0; r < crun; r++)
                {
                    int start = BinaryPrimitives.ReadInt32LittleEndian(page[(r * 4)..]);
                    int end = BinaryPrimitives.ReadInt32LittleEndian(page[(r * 4 + 4)..]);
                    if (start < 0 || end <= start) continue;
                    int papxAt = page[(crun + 1) * 4 + r * 13] * 2;
                    if (papxAt == 0 || !IsRowEnd(page, papxAt)) continue;
                    if (starts.Count >= MaxRowEndRuns) return null;
                    starts.Add(start);
                    ends.Add(end);
                }
            }
            return Merge(starts, ends);
        }

        static RowEnds Merge(List<int> starts, List<int> ends)
        {
            var s = starts.ToArray();
            var e = ends.ToArray();
            Array.Sort(s, e);
            int count = 0;
            for (int i = 0; i < s.Length; i++)
            {
                if (count > 0 && s[i] <= e[count - 1]) e[count - 1] = Math.Max(e[count - 1], e[i]);
                else { s[count] = s[i]; e[count] = e[i]; count++; }
            }
            return new RowEnds(s[..count], e[..count]);
        }

        /// <summary>
        /// Whether the PapxInFkp at that page offset holds sprmPFTtp with a non-zero operand. Its size is a byte cb (when
        /// 0 the next byte cb' gives 2 x cb' bytes, otherwise 2 x cb - 1); a 2-byte style index comes before the sprms.
        /// </summary>
        static bool IsRowEnd(ReadOnlySpan<byte> page, int at)
        {
            int limit = FkpBytes - 1;
            if (at >= limit) return false;
            int cb = page[at++];
            int length;
            if (cb == 0)
            {
                if (at >= limit) return false;
                length = 2 * page[at++];
            }
            else length = 2 * cb - 1;
            if (length > limit - at || length < 2) return false;
            var grpprl = page.Slice(at + 2, length - 2);
            int q = 0;
            while (q + 2 <= grpprl.Length)
            {
                ushort sprm = BinaryPrimitives.ReadUInt16LittleEndian(grpprl[q..]);
                q += 2;
                if (sprm == SprmPFTtp) return q < grpprl.Length && grpprl[q] != 0;
                int size;
                switch (sprm >> 13)
                {
                    case 0 or 1: size = 1; break;
                    case 2 or 4 or 5: size = 2; break;
                    case 3: size = 4; break;
                    case 7: size = 3; break;
                    default:
                        if (sprm == SprmTDefTable)
                        {
                            // A 2-byte cb, then cb - 1 bytes: cb + 1 in all, as Word's own files bear out.
                            if (q + 2 > grpprl.Length) return false;
                            size = BinaryPrimitives.ReadUInt16LittleEndian(grpprl[q..]) + 1;
                        }
                        else
                        {
                            if (q >= grpprl.Length) return false;
                            // A tab change whose size byte is 255 has to be worked out from its content: stop there.
                            if (sprm == SprmPChgTabs && grpprl[q] == 255) return false;
                            size = 1 + grpprl[q];
                        }
                        break;
                }
                q += size;
            }
            return false;
        }
    }

    /// <summary>
    /// Turns Word's characters into plain text: special marks to '\n', tab, '-' or space; fields reduced to their results
    /// (0x13 begins one, 0x14 starts its result, 0x15 ends it; nested ones tracked with a small stack); other control
    /// characters dropped. Stops at maxChars characters.
    /// </summary>
    sealed class Cleaner(int capacity, int maxChars)
    {
        readonly StringBuilder _text = new(Math.Max(16, capacity));
        // Separator seen, per open field (innermost last), and how many open fields hide what's in them: those still in
        // their instruction, plus every field nested past the stack's depth.
        readonly bool[] _separated = new bool[MaxFieldDepth];
        int _depth, _overflow, _hiding;
        bool _stopped, _storyBreak;
        // Footnotes or endnotes; nothing shown yet in the paragraph; the last character a note's reference mark there.
        bool _notes, _paragraphStart, _afterNoteMark;

        public bool Full => _stopped || _text.Length >= maxChars;

        /// <summary>
        /// A new story: fields left open in the last one end with it, and its first character other than a line end comes
        /// after exactly one empty line (none before the first text, and none for a story that gives nothing else). In
        /// footnotes and endnotes the one space after a reference mark (0x02) that starts a paragraph is left out.
        /// </summary>
        public void StartStory(bool notes)
        {
            _depth = _overflow = _hiding = 0;
            _storyBreak = _text.Length > 0;
            _notes = notes;
            _paragraphStart = true;
            _afterNoteMark = false;
        }

        /// <summary>A table row's end: the tab the row's last cell mark gave becomes the line's end.</summary>
        public void EndRow()
        {
            if (_hiding > 0) return;
            _paragraphStart = true;
            _afterNoteMark = false;
            if (!_storyBreak && _text.Length > 0 && _text[^1] == '\t') _text.Length--;
            Append('\n');
        }

        public void Add(char c)
        {
            switch (c)
            {
                case '\u0013':
                    if (_depth < MaxFieldDepth) _separated[_depth++] = false;
                    else _overflow++;
                    _hiding++;
                    return;
                case '\u0014':
                    if (_overflow == 0 && _depth > 0 && !_separated[_depth - 1])
                    {
                        _separated[_depth - 1] = true;
                        _hiding--;
                    }
                    return;
                case '\u0015':
                    if (_overflow > 0) { _overflow--; _hiding--; }
                    else if (_depth > 0)
                    {
                        if (!_separated[--_depth]) _hiding--;
                    }
                    return;
            }
            if (_hiding > 0) return;
            bool paragraphStart = _paragraphStart, afterMark = _afterNoteMark;
            _paragraphStart = _afterNoteMark = false;
            switch (c)
            {
                case '\r' or '\f': _paragraphStart = true; Append('\n'); return;
                case '\v': Append('\n'); return;
                case '\u0007': _paragraphStart = true; Append('\t'); return;
                case '\t': Append('\t'); return;
                case '\u001E': Append('-'); return;
                case '\u00A0': Append(' '); return;
                case ' ' when afterMark: return; // the space Word puts after a note's reference mark
                case '\u0002': _afterNoteMark = _notes && paragraphStart; return;
            }
            if (c < ' ') return; // pictures, footnote and annotation marks, drawn objects, optional hyphens, the rest
            Append(c);
        }

        void Append(char c)
        {
            if (_storyBreak)
            {
                if (c == '\n') return; // a story's leading empty paragraphs, or a story of nothing else, add no lines
                _storyBreak = false;
                // Exactly one empty line: stories end with empty paragraphs of their own, which would add to it.
                while (_text.Length > 0 && _text[^1] == '\n') _text.Length--;
                for (int i = 0; i < 2 && _text.Length > 0; i++)
                {
                    if (_text.Length >= maxChars) return;
                    _text.Append('\n');
                }
                if (_text.Length >= maxChars) return;
            }
            // A surrogate pair is never cut in two at the limit: its first half in the last place ends the text.
            if (char.IsHighSurrogate(c) && _text.Length == maxChars - 1) { _stopped = true; return; }
            _text.Append(c);
        }

        public override string ToString() => _text.ToString();
    }
}
