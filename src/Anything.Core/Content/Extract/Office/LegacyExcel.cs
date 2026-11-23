using System.Buffers.Binary;
using System.Globalization;
using System.Text;

namespace Anything.Core.Content.Extract.Office;

/// <summary>One worksheet of an Excel 97-2003 workbook: its cells as text, row by row.</summary>
/// <param name="Name">The sheet's name as its tab shows it.</param>
/// <param name="Rows">Row by row from the first row; a null for an empty cell, trailing empty cells and trailing empty rows cut.</param>
/// <param name="Truncated">
/// Rows or columns beyond the limits were left out, or the sheet was cut short; also set on a sheet the workbook's caps
/// kept from being read (NotRead too). A sheet that was read and is Truncated with no rows kept nothing: every cell lies
/// beyond the limits, or (when every sheet is read, with no chooser) a cap stopped it at or before its first cell.
/// </param>
/// <param name="Hidden">The sheet is hidden or very hidden (its tab is not shown in Excel).</param>
/// <param name="NotRead">
/// The sheet's cells were not read at all: another sheet was chosen, or a cap ended the reading before this sheet.
/// </param>
internal sealed record LegacyExcelSheet(string Name, IReadOnlyList<string?[]> Rows, bool Truncated, bool Hidden = false, bool NotRead = false);

/// <summary>
/// Excel 97-2003 workbooks (.xls, .xlt: BIFF8 in a "Workbook" stream) and Excel 5.0/95 ones (BIFF5 in a "Book" stream),
/// read as text for the preview pane when Office's own previewer is missing, busy or not allowed. The workbook globals give
/// the sheets (BOUNDSHEET), the shared strings (SST, with its CONTINUE records), the date system and which cell styles
/// are dates; each worksheet's substream gives its cells (LABELSST, LABEL, RSTRING, NUMBER, RK, MULRK, BOOLERR and FORMULA
/// with its cached result). Chart sheets, macro sheets and charts inside a worksheet are skipped. An encrypted workbook
/// (FILEPASS) gives null. The stream is untrusted: every record is checked against the stream, every loop moves forward,
/// the work over all sheets (CONTINUE runs included) is capped at a small multiple of the stream's size, the shared strings
/// are gathered up to MaxSstGatherBytes and MaxSstPieces, the token is checked every few thousand cells, cells beyond the limits are not
/// formatted, and the cells kept over all sheets are capped by the row and column limits (SlotLimit). A worksheet that a cap keeps from being read is
/// still listed, with no rows and marked Truncated and NotRead, so the sheet list stays whole. A caller that shows one
/// sheet can choose it from the sheet list, and only that sheet's cells are read.
/// </summary>
internal static class LegacyExcel
{
    /// <summary>Largest workbook stream read; a larger one is not read at all.</summary>
    internal const long MaxStreamBytes = 64L << 20;
    /// <summary>A cell's text is cut here.</summary>
    internal const int MaxCellChars = 32_000;
    /// <summary>Shared strings kept: past this count, cells that use later ones are left blank.</summary>
    internal const int MaxSharedStrings = 2_000_000;
    /// <summary>Characters kept over all shared strings: past this, cells that use later ones are left blank.</summary>
    internal const long MaxSharedChars = 16L << 20;
    /// <summary>Most worksheets listed (read or not), so a sheet list built to be endless stays small.</summary>
    internal const int MaxSheets = 4096;

    static readonly ExtractLimits Limits = new(MaxBytesRead: 160L << 20, MaxMilliseconds: 30_000);

    static LegacyExcel() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    /// <summary>
    /// The worksheets with their cells as text (row by row, a null for an empty cell, trailing empty cells and trailing
    /// empty rows cut); null when the file is not an Excel 97-2003 workbook, is encrypted, or cannot be read.
    /// </summary>
    public static IReadOnlyList<LegacyExcelSheet>? ReadSheets(string path, int maxRows, int maxColumns, CancellationToken ct)
        => ReadSheets(path, maxRows, maxColumns, null, ct);

    /// <summary>
    /// The same, reading the cells of one sheet only. Once the sheet list is known, and before any cells are read,
    /// choose is called once with the sheets that will be returned, in order (names and Hidden set, no rows, NotRead), and
    /// gives the index of the sheet to read; every other sheet comes back as listed. An index outside the list reads no
    /// sheet. A null choose reads every sheet. If choose throws (other than a cancellation), the result is null.
    /// </summary>
    public static IReadOnlyList<LegacyExcelSheet>? ReadSheets(string path, int maxRows, int maxColumns,
        Func<IReadOnlyList<LegacyExcelSheet>, int>? choose, CancellationToken ct)
    {
        try
        {
            var budget = new ExtractBudget(Limits, ct);
            FileByteSource.TryOpen(Path.GetFullPath(path), budget, out var source, out _);
            if (source == null) return null;
            using (source)
            {
                if (!CompoundFile.IsCompoundFile(source) || CompoundFile.TryOpen(source, budget) is not { } cfb || cfb.HasEncryptedPackage()) return null;
                int id = cfb.Find(cfb.Root, "Workbook");
                if (id < 0) id = cfb.Find(cfb.Root, "Book");
                if (id < 0 || cfb.Type(id) != CompoundFile.EntryType.Stream || cfb.Size(id) > MaxStreamBytes) return null;
                var stream = cfb.ReadStream(id, MaxStreamBytes);
                return stream == null ? null : Parse(stream, maxRows, maxColumns, choose, ct, out _);
            }
        }
        catch (Exception e) when (e is not OperationCanceledException) { return null; }
    }

    /// <summary>
    /// The worksheets of a workbook stream's bytes ("Workbook" or "Book"); null when it doesn't start with the workbook
    /// globals' BOF of BIFF5 or BIFF8, or is encrypted.
    /// </summary>
    internal static IReadOnlyList<LegacyExcelSheet>? Parse(byte[] stream, int maxRows, int maxColumns, CancellationToken ct)
        => Parse(stream, maxRows, maxColumns, ct, out _);

    /// <summary>The same, with how much work and memory the walk took against its caps (for tests).</summary>
    internal static IReadOnlyList<LegacyExcelSheet>? Parse(byte[] stream, int maxRows, int maxColumns, CancellationToken ct, out ParseCounts counts)
        => Parse(stream, maxRows, maxColumns, null, ct, out counts);

    /// <summary>The same, with a chooser of the one sheet to read (see ReadSheets).</summary>
    internal static IReadOnlyList<LegacyExcelSheet>? Parse(byte[] stream, int maxRows, int maxColumns,
        Func<IReadOnlyList<LegacyExcelSheet>, int>? choose, CancellationToken ct, out ParseCounts counts)
        => Parse(stream, maxRows, maxColumns, choose, ct, null, out counts);

    /// <summary>
    /// The same, calling onStep with the running count of a sheet walk's steps (each record, and each MULRK cell
    /// formatted) as each one is taken (for tests: a token cancelled from there shows how soon the walk stops).
    /// </summary>
    internal static IReadOnlyList<LegacyExcelSheet>? Parse(byte[] stream, int maxRows, int maxColumns,
        Func<IReadOnlyList<LegacyExcelSheet>, int>? choose, CancellationToken ct, Action<long>? onStep, out ParseCounts counts)
    {
        var book = new Book(stream, ct, onStep);
        var sheets = book.Read(Math.Max(0, maxRows), Math.Max(0, maxColumns), choose);
        counts = book.Counts;
        return sheets;
    }

    /// <summary>
    /// Bytes walked against the work cap, cell slots kept against the cell cap, numbers turned into text, and the pieces
    /// (records with data) the shared strings were gathered from.
    /// </summary>
    internal readonly record struct ParseCounts(long Work, long WorkLimit, long SlotsKept, long SlotLimit,
        long NumbersFormatted = 0, int SharedStringPieces = 0);

    /// <summary>
    /// Most bytes of CONTINUE records gathered after the SST (headers included): room for everything SharedStrings can
    /// use under its caps, MaxSharedChars characters of two bytes each and a header of at most 9 bytes for each of
    /// MaxSharedStrings strings, plus a margin for the CONTINUE records' own headers and flags bytes. The rest of a
    /// longer run is passed over record by record. Formatting runs and extended data are not counted: a workbook full of
    /// them can reach this cap first, and the strings after it are then left blank, as past the other caps.
    /// </summary>
    internal const long MaxSstGatherBytes = 2 * MaxSharedChars + 9L * MaxSharedStrings + (1 << 20);

    /// <summary>
    /// Most pieces (records with data) gathered for the SST, the record itself included. The byte cap alone still lets
    /// a run of one-byte CONTINUE records make some ten million list entries; a real table needs a few thousand (Excel
    /// splits at 8224 bytes per record). The rest of a longer run is passed over record by record, as past the byte cap.
    /// </summary>
    internal const int MaxSstPieces = 1_000_000;

    /// <summary>The token is checked after at most this many steps of a sheet's walk (a record, or one MULRK cell).</summary>
    internal const int CancelCheckSteps = 1024;

    /// <summary>
    /// Most bytes of CONTINUE records gathered after a formula's STRING record: enough for MaxCellChars characters of two
    /// bytes each, with room for the record headers and the flags byte each CONTINUE starts with. The rest of a longer run
    /// is passed over record by record like any other record. It counts raw bytes, so a string split into very many tiny
    /// CONTINUE records (each with its own header and flags byte) is cut below MaxCellChars; that is accepted, since Excel
    /// itself splits at about 8 KB per record, only odd writers are affected, and nothing is read out of bounds.
    /// </summary>
    internal const long MaxStringGatherBytes = 2L * MaxCellChars + 4096;

    /// <summary>
    /// Cell slots kept over all sheets when every sheet is read: each row up to the last one with text takes one, and each
    /// row's cells take one apiece up to its last cell (the width it really uses). A slot is one 8-byte reference, but a
    /// row's array can hold up to twice its charged width, and every kept cell also holds its own text (a number's text
    /// is some 30 to 60 bytes; shared strings are kept once however many cells use them). The cap is room for 8 sheets
    /// filled to every limit (maxRows x (maxColumns + 1) each): at the preview's limits 3.2M slots, so under 100 MB of
    /// arrays (with many short rows each row's array has its own header) plus each kept cell's text, some 100 to 200 MB
    /// more if every cell holds a number of its own. A file built to
    /// put one cell at a far column on every row of thousands of sheets stops here instead of growing to gigabytes. When
    /// one chosen sheet is read, the other sheets take nothing from it, and below the ceiling that sheet fits in an eighth
    /// of it. Never more than MaxSlots.
    /// </summary>
    internal static long SlotLimit(int maxRows, int maxColumns)
        => Math.Min(8L * Math.Min(Math.Max(0, maxRows), MaxSlots) * Math.Min(Math.Max(0, maxColumns) + 1L, MaxSlots), MaxSlots);

    /// <summary>
    /// The cell cap's ceiling, whatever limits the caller gives: 16M slots, up to about 256 MB of arrays (a reference per
    /// slot, arrays up to twice their charged width) before the cells' own texts.
    /// </summary>
    internal const long MaxSlots = 1L << 24;

    // Record types ([MS-XLS] 2.3).
    const int RtBof = 0x0809, RtEof = 0x000A, RtContinue = 0x003C, RtFilePass = 0x002F, RtBoundSheet = 0x0085,
        RtCodePage = 0x0042, RtDateMode = 0x0022, RtFormat = 0x041E, RtXf = 0x00E0, RtSst = 0x00FC,
        RtLabelSst = 0x00FD, RtLabel = 0x0204, RtRString = 0x00D6, RtNumber = 0x0203, RtRk = 0x027E, RtMulRk = 0x00BD,
        RtFormula = 0x0006, RtString = 0x0207, RtBoolErr = 0x0205, RtBlank = 0x0201, RtMulBlank = 0x00BE;

    /// <summary>One record: its type and where its data lies in the stream (already checked to fit).</summary>
    readonly record struct Rec(int Type, int Start, int Length)
    {
        public int Next => Start + Length;
    }

    /// <summary>One workbook stream's walk.</summary>
    sealed class Book(byte[] d, CancellationToken ct, Action<long>? onStep)
    {
        readonly List<int> _xfFormat = [];
        readonly Dictionary<int, DateKind> _customFormatKind = [];
        readonly List<(int Offset, string Name, bool Hidden)> _sheets = [];
        List<string?>? _sst;
        bool _biff8, _date1904;
        Encoding _codePage = Encoding.Latin1;
        long _work;
        readonly long _workLimit = 2L * d.Length + (1 << 20);
        Slots _slots = new(0);
        long _steps, _numbers;
        int _untilCheck = CancelCheckSteps, _sstPieces;

        public ParseCounts Counts => new(_work, _workLimit, _slots.Limit - _slots.Left, _slots.Limit, _numbers, _sstPieces);

        /// <summary>
        /// One step of a sheet's walk (a record, or a MULRK cell): the token is checked every CancelCheckSteps steps, so
        /// a sheet of huge MULRK records cannot run long between two checks.
        /// </summary>
        void Step()
        {
            _steps++;
            onStep?.Invoke(_steps);
            if (--_untilCheck > 0) return;
            _untilCheck = CancelCheckSteps;
            ct.ThrowIfCancellationRequested();
        }

        bool TryRecord(int at, out Rec r)
        {
            r = default;
            if (at < 0 || at > d.Length - 4) return false;
            int type = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at)), length = BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at + 2));
            if (length > d.Length - at - 4) return false;
            r = new Rec(type, at + 4, length);
            return true;
        }

        int U16(int at) => BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(at));

        public IReadOnlyList<LegacyExcelSheet>? Read(int maxRows, int maxColumns, Func<IReadOnlyList<LegacyExcelSheet>, int>? choose)
        {
            if (!TryRecord(0, out var bof) || bof.Type != RtBof || bof.Length < 4 || U16(bof.Start + 2) != 0x0005) return null;
            int version = U16(bof.Start);
            if (version is not (0x0600 or 0x0500)) return null;
            _biff8 = version == 0x0600;
            _slots = new Slots(SlotLimit(maxRows, maxColumns));
            if (!Globals(bof.Next)) return null;

            // The sheets that will be returned, settled before any cells are read so an index means the same sheet to the
            // chooser and in the result.
            var offsets = new List<int>();
            var result = new List<LegacyExcelSheet>();
            var seen = new HashSet<int>();
            foreach (var (offset, name, hidden) in _sheets)
            {
                if (result.Count >= MaxSheets) break;
                if (!IsWorksheet(offset) || !seen.Add(offset)) continue;
                offsets.Add(offset);
                result.Add(new LegacyExcelSheet(name, [], false, hidden, NotRead: true));
            }

            int only = -1;
            if (choose != null)
            {
                // A chooser that fails leaves no right sheet to read: the file is treated as unreadable.
                try { only = choose(result.AsReadOnly()); }
                catch (Exception e) when (e is not OperationCanceledException) { return null; }
            }
            for (int i = 0; i < result.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                if (choose != null && i != only) continue;
                var listed = result[i];
                // Past the work cap or the cell cap, later sheets stay listed unread so the sheet list stays whole; the
                // sheet that reached the cap is marked Truncated with what it kept.
                result[i] = _work > _workLimit || _slots.Spent
                    ? listed with { Truncated = true }
                    : Sheet(offsets[i], listed.Name, listed.Hidden, maxRows, maxColumns);
            }
            return result;
        }

        /// <summary>The position holds a worksheet's BOF (a chart, macro or dialog substream does not count).</summary>
        bool IsWorksheet(int offset)
            => TryRecord(offset, out var bof) && bof.Type == RtBof && bof.Length >= 4 && U16(bof.Start + 2) == 0x0010;

        /// <summary>The workbook globals up to their EOF; false when the workbook is encrypted.</summary>
        bool Globals(int at)
        {
            int count = 0;
            while (TryRecord(at, out var r))
            {
                if ((++count & 4095) == 0) ct.ThrowIfCancellationRequested();
                int next = r.Next;
                switch (r.Type)
                {
                    case RtEof:
                        return true;
                    case RtBof:
                        return true; // a substream starts without the globals' EOF: the globals end here
                    case RtFilePass:
                        return false;
                    case RtCodePage when r.Length >= 2:
                        _codePage = CodePage(U16(r.Start));
                        break;
                    case RtDateMode when r.Length >= 2:
                        _date1904 = U16(r.Start) == 1;
                        break;
                    case RtXf when r.Length >= 4 && _xfFormat.Count < 0x10000:
                        _xfFormat.Add(U16(r.Start + 2));
                        break;
                    case RtFormat when r.Length >= 3:
                        if (InRecordString(r.Start + 2, r.Next, wideCount: _biff8) is { } format)
                            _customFormatKind[U16(r.Start)] = FormatKind(format);
                        break;
                    case RtBoundSheet when r.Length >= 7:
                        // Position of the sheet's BOF, visibility (low two bits: 0 visible, 1 hidden, 2 very hidden; hidden
                        // sheets are read too), kind (0 = worksheet), name.
                        if (d[r.Start + 5] == 0 && _sheets.Count < MaxSheets * 4)
                            _sheets.Add(((int)Math.Min(BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(r.Start)), int.MaxValue),
                                InRecordString(r.Start + 6, r.Next, wideCount: false) ?? "", (d[r.Start + 4] & 0x03) != 0));
                        break;
                    case RtSst when _biff8 && _sst == null:
                        // Capped in bytes and in pieces so a run of millions of CONTINUE records cannot grow the piece
                        // lists past what the shared strings' caps can use; the walk passes the rest one record at a time.
                        var pieces = Gather(r, MaxSstGatherBytes, out next, MaxSstPieces);
                        _sstPieces = pieces.Count;
                        _sst = [];
                        SharedStrings(pieces);
                        break;
                }
                at = next;
            }
            return true; // the stream ends inside the globals: what was read stands
        }

        /// <summary>
        /// The record's data and the CONTINUE records right after it, until maxBytes of them (headers included) or
        /// maxPieces pieces with data are in hand; next is the position after the last one gathered. Every byte walked
        /// here counts against the work cap, so a long CONTINUE run that many sheets reach costs each of them its share.
        /// </summary>
        Pieces Gather(Rec first, long maxBytes, out int next, int maxPieces = int.MaxValue)
        {
            var pieces = new Pieces(d);
            pieces.Add(first.Start, first.Next);
            int at = first.Next, count = 0;
            long walked = 0;
            while (walked < maxBytes && pieces.Count < maxPieces && TryRecord(at, out var c) && c.Type == RtContinue)
            {
                if ((++count & 1023) == 0) ct.ThrowIfCancellationRequested();
                pieces.Add(c.Start, c.Next);
                walked += c.Length + 4;
                at = c.Next;
            }
            _work += walked;
            next = at;
            return pieces;
        }

        /// <summary>The SST: total count, unique count, then the strings, kept up to the caps.</summary>
        void SharedStrings(Pieces p)
        {
            if (!p.U32(out _) || !p.U32(out uint unique)) return;
            long chars = 0;
            for (uint i = 0; i < unique; i++)
            {
                if ((i & 4095) == 0) ct.ThrowIfCancellationRequested();
                if (_sst!.Count >= MaxSharedStrings || chars >= MaxSharedChars) return;
                var s = p.UnicodeString(MaxCellChars, out bool complete);
                if (s == null) return;
                _sst.Add(s.Length == 0 ? null : s);
                chars += s.Length;
                if (!complete) return;
            }
        }

        /// <summary>
        /// A string inside one record: BIFF8 a 1- or 2-byte character count, a flags byte (two bytes per character,
        /// formatting runs, extended data) and the characters; BIFF5 a 1- or 2-byte count and bytes in the workbook's code
        /// page. Cut at the record's end and at MaxCellChars; null when not even its header fits.
        /// </summary>
        string? InRecordString(int at, int end, bool wideCount)
        {
            int countSize = wideCount ? 2 : 1;
            if (at > end - countSize) return null;
            int count = wideCount ? U16(at) : d[at];
            at += countSize;
            if (!_biff8)
            {
                int n = Math.Min(Math.Min(count, end - at), MaxCellChars);
                return _codePage.GetString(d, at, n);
            }
            if (at >= end) return count == 0 ? "" : null;
            int flags = d[at++];
            if ((flags & 0x08) != 0) at += 2;
            if ((flags & 0x04) != 0) at += 4;
            if (at > end) return null;
            bool wide = (flags & 0x01) != 0;
            int available = wide ? (end - at) / 2 : end - at;
            int take = Math.Min(Math.Min(count, available), MaxCellChars);
            return wide ? Encoding.Unicode.GetString(d, at, take * 2) : Encoding.Latin1.GetString(d, at, take);
        }

        /// <summary>One worksheet's cells; the position holds a worksheet's BOF (IsWorksheet).</summary>
        LegacyExcelSheet Sheet(int offset, string name, bool hidden, int maxRows, int maxColumns)
        {
            TryRecord(offset, out var bof);
            var grid = new Grid(maxRows, maxColumns, _slots);
            int at = bof.Next, depth = 0;
            (int Row, int Col)? pendingString = null;
            while (TryRecord(at, out var r))
            {
                Step();
                _work += r.Length + 4;
                if (_work > _workLimit) { grid.Truncated = true; break; }
                int next = r.Next;
                if (r.Type is RtBof or 0x0009 or 0x0209 or 0x0409) depth++; // a chart or other substream inside: skipped
                else if (r.Type == RtEof)
                {
                    if (depth == 0) break;
                    depth--;
                }
                else if (depth == 0)
                {
                    Cell(r, grid, ref pendingString, ref next);
                    if (_slots.Spent) break; // the cell cap is reached: Grid marked this sheet Truncated
                }
                at = next;
            }
            return new LegacyExcelSheet(name, grid.Rows(), grid.Truncated, hidden);
        }

        void Cell(Rec r, Grid grid, ref (int Row, int Col)? pendingString, ref int next)
        {
            int s = r.Start, length = r.Length;
            switch (r.Type)
            {
                case RtLabelSst when length >= 10:
                    pendingString = null;
                    {
                        uint index = BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(s + 6));
                        grid.Set(U16(s), U16(s + 2), _sst != null && index < (uint)_sst.Count ? _sst[(int)index] : null);
                    }
                    break;
                case RtLabel or RtRString when length >= 8:
                    pendingString = null;
                    grid.Set(U16(s), U16(s + 2), InRecordString(s + 6, r.Next, wideCount: true));
                    break;
                // A number beyond the limits is not formatted at all (its text is never empty, so the sheet is
                // Truncated either way).
                case RtNumber when length >= 14:
                    pendingString = null;
                    if (grid.Outside(U16(s), U16(s + 2))) grid.Truncated = true;
                    else grid.Set(U16(s), U16(s + 2), Number(BinaryPrimitives.ReadDoubleLittleEndian(d.AsSpan(s + 6)), U16(s + 4)));
                    break;
                case RtRk when length >= 10:
                    pendingString = null;
                    if (grid.Outside(U16(s), U16(s + 2))) grid.Truncated = true;
                    else grid.Set(U16(s), U16(s + 2), Number(Rk(BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(s + 6))), U16(s + 4)));
                    break;
                case RtMulRk when length >= 12:
                    pendingString = null;
                    {
                        int row = U16(s), col = U16(s + 2), cells = (length - 6) / 6;
                        // A row beyond the limit is skipped whole; within it, the cells stop at the column limit.
                        if (grid.Outside(row, 0)) { grid.Truncated = true; break; }
                        int kept = Math.Min(cells, Math.Max(0, grid.MaxColumns - col));
                        if (kept < cells) grid.Truncated = true;
                        for (int i = 0; i < kept; i++)
                        {
                            Step();
                            int at = s + 4 + i * 6;
                            grid.Set(row, col + i, Number(Rk(BinaryPrimitives.ReadUInt32LittleEndian(d.AsSpan(at + 2))), U16(at)));
                            if (_slots.Spent) break; // Grid marked the sheet Truncated and keeps nothing more
                        }
                    }
                    break;
                case RtBoolErr when length >= 8:
                    pendingString = null;
                    grid.Set(U16(s), U16(s + 2), d[s + 7] != 0 ? Error(d[s + 6]) : d[s + 6] != 0 ? "TRUE" : "FALSE");
                    break;
                case RtFormula when length >= 14:
                    pendingString = null;
                    {
                        int row = U16(s), col = U16(s + 2), result = s + 6;
                        if (U16(result + 6) != 0xFFFF)
                        {
                            if (grid.Outside(row, col)) grid.Truncated = true;
                            else grid.Set(row, col, Number(BinaryPrimitives.ReadDoubleLittleEndian(d.AsSpan(result)), U16(s + 4)));
                        }
                        else
                            switch (d[result])
                            {
                                case 0: pendingString = (row, col); break; // the text follows in a STRING record
                                case 1: grid.Set(row, col, d[result + 2] != 0 ? "TRUE" : "FALSE"); break;
                                case 2: grid.Set(row, col, Error(d[result + 2])); break;
                            }
                    }
                    break;
                case RtString:
                    if (pendingString is { } cell)
                    {
                        string? text;
                        if (_biff8)
                        {
                            var pieces = Gather(r, MaxStringGatherBytes, out next);
                            text = pieces.UnicodeString(MaxCellChars, out _);
                        }
                        else text = InRecordString(s, r.Next, wideCount: true);
                        grid.Set(cell.Row, cell.Col, text);
                    }
                    pendingString = null;
                    break;
                case RtBlank or RtMulBlank:
                    pendingString = null;
                    break;
            }
        }

        /// <summary>A number as its cell shows it: a date, time or elapsed time when its style's format is one, otherwise the number.</summary>
        string Number(double value, int xf)
        {
            _numbers++;
            return DateText(value, StyleKind(xf), _date1904) ?? NumberText(value);
        }

        DateKind StyleKind(int xf)
        {
            if (xf < 0 || xf >= _xfFormat.Count) return DateKind.None;
            int format = _xfFormat[xf];
            return _customFormatKind.TryGetValue(format, out var custom) ? custom : BuiltInKind(format);
        }
    }

    /// <summary>
    /// The data of a record and its CONTINUE records, read as one run that knows where each record ends: header fields,
    /// runs and extended data go straight on across a boundary, while characters cut by one go on after a new flags byte
    /// that says whether the rest of them take one or two bytes each.
    /// </summary>
    sealed class Pieces(byte[] d)
    {
        readonly List<int> _starts = [], _ends = [];
        int _piece, _pos;

        /// <summary>The pieces with data kept.</summary>
        public int Count => _starts.Count;

        public void Add(int start, int end)
        {
            // An empty record holds nothing, and reading would skip it anyway (no flags byte is read from it).
            if (end <= start) return;
            if (_starts.Count == 0) _pos = start;
            _starts.Add(start);
            _ends.Add(end);
        }

        /// <summary>Moves to the next piece with data left, skipping empty ones; false at the end.</summary>
        bool Advance()
        {
            while (_pos >= _ends[_piece])
            {
                if (_piece + 1 >= _starts.Count) return false;
                _piece++;
                _pos = _starts[_piece];
            }
            return true;
        }

        bool Byte(out int value)
        {
            value = 0;
            if (_starts.Count == 0 || !Advance()) return false;
            value = d[_pos++];
            return true;
        }

        public bool U16(out int value)
        {
            value = 0;
            if (!Byte(out int lo) || !Byte(out int hi)) return false;
            value = lo | hi << 8;
            return true;
        }

        public bool U32(out uint value)
        {
            value = 0;
            if (!U16(out int lo) || !U16(out int hi)) return false;
            value = (uint)lo | (uint)hi << 16;
            return true;
        }

        /// <summary>Skips bytes across pieces with no flags bytes; false when the data ends first.</summary>
        bool Skip(long count)
        {
            while (count > 0)
            {
                if (_starts.Count == 0 || !Advance()) return false;
                int n = (int)Math.Min(count, _ends[_piece] - _pos);
                _pos += n;
                count -= n;
            }
            return true;
        }

        /// <summary>
        /// A string with a 2-byte character count, a flags byte, the run count and extended size its flags announce, the
        /// characters, the runs and the extended data. Keeps at most keep characters; complete is false when the data ended
        /// inside it (the characters read so far are returned). Null when not even its header could be read.
        /// </summary>
        public string? UnicodeString(int keep, out bool complete)
        {
            complete = false;
            if (!U16(out int count) || !Byte(out int flags)) return null;
            int runs = 0;
            uint extended = 0;
            if ((flags & 0x08) != 0 && !U16(out runs)) return null;
            if ((flags & 0x04) != 0 && !U32(out extended)) return null;
            var text = Chars(count, (flags & 0x01) != 0, keep, out bool all);
            complete = all && Skip(4L * runs) && Skip(extended);
            return text;
        }

        string Chars(int count, bool wide, int keep, out bool all)
        {
            var text = new StringBuilder(Math.Min(count, keep));
            int left = count;
            all = false;
            while (left > 0)
            {
                if (_pos >= _ends[_piece])
                {
                    // The characters go on in the next CONTINUE record, after a flags byte of their own.
                    if (!Advance()) return text.ToString();
                    wide = (d[_pos++] & 0x01) != 0;
                    continue;
                }
                int available = _ends[_piece] - _pos;
                int n = Math.Min(left, wide ? available / 2 : available);
                if (n == 0) { _pos = _ends[_piece]; continue; } // half a character before the boundary: dropped
                int take = Math.Min(n, keep - text.Length);
                for (int i = 0; i < take; i++)
                    text.Append(wide ? (char)(d[_pos + 2 * i] | d[_pos + 2 * i + 1] << 8) : (char)d[_pos + i]);
                _pos += wide ? 2 * n : n;
                left -= n;
            }
            all = true;
            return text.ToString();
        }
    }

    /// <summary>The cell slots still free over all sheets (see SlotLimit); Spent once a cell did not fit.</summary>
    sealed class Slots(long limit)
    {
        public long Limit { get; } = limit;
        public long Left { get; set; } = limit;
        public bool Spent { get; set; }
    }

    /// <summary>
    /// A sheet's cells as they are found; rows and columns past the limits are counted, not kept. Every row entry and each
    /// column of a row's width (up to its last cell) is taken from the workbook's slots; a cell that does not fit marks
    /// the sheet Truncated, and from then on nothing more is kept anywhere.
    /// </summary>
    sealed class Grid(int maxRows, int maxColumns, Slots slots)
    {
        readonly List<string?[]?> _rows = [];
        readonly List<int> _widths = [];

        public bool Truncated { get; set; }

        public int MaxColumns => maxColumns;

        /// <summary>The cell lies beyond the row or column limit: it would be counted, not kept.</summary>
        public bool Outside(int row, int col) => row >= maxRows || col >= maxColumns;

        public void Set(int row, int col, string? text)
        {
            if (string.IsNullOrEmpty(text)) return;
            // A later cell of the same MULRK must not show up after the gap the cut left.
            if (slots.Spent) { Truncated = true; return; }
            if (row >= maxRows || col >= maxColumns) { Truncated = true; return; }
            var cells = row < _rows.Count ? _rows[row] : null;
            int had = cells?.Length ?? 0, size = had, width = row < _widths.Count ? _widths[row] : 0;
            if (had <= col) size = Math.Max(Math.Min(Math.Max(col + 1, had * 2), maxColumns), col + 1);
            // Charged for the width used, not the array's doubled capacity (at most twice the width).
            long need = Math.Max(0, row + 1 - _rows.Count) + Math.Max(0, col + 1 - width);
            if (need > slots.Left) { slots.Spent = true; Truncated = true; return; }
            slots.Left -= need;
            while (_rows.Count <= row) { _rows.Add(null); _widths.Add(0); }
            if (size != had)
            {
                Array.Resize(ref cells, size);
                _rows[row] = cells;
            }
            cells![col] = text; // had > col, or the array was just grown past col
            if (col + 1 > _widths[row]) _widths[row] = col + 1;
        }

        public IReadOnlyList<string?[]> Rows()
        {
            var rows = new List<string?[]>(_rows.Count);
            for (int i = 0; i < _rows.Count; i++)
            {
                var cells = _rows[i];
                int width = _widths[i];
                rows.Add(cells == null || width == 0 ? [] : cells.Length == width ? cells : cells[..width]);
            }
            // Rows are only added for a cell with text, so the last row is never empty.
            return rows;
        }
    }

    /// <summary>A packed number: bit 0 divides by 100, bit 1 means a signed 30-bit integer, otherwise the top of a double.</summary>
    internal static double Rk(uint rk)
    {
        double value = (rk & 0x02) != 0 ? (int)rk >> 2 : BitConverter.Int64BitsToDouble((long)(rk & 0xFFFFFFFC) << 32);
        return (rk & 0x01) != 0 ? value / 100 : value;
    }

    /// <summary>
    /// A number as Excel's General format shows it: rounded to 15 significant digits, the most Excel shows, so binary noise
    /// such as 0.1 + 0.2 = 0.30000000000000004 reads 0.3; then the shortest culture-invariant form (1234.5, 0.1, 100, 1E+20).
    /// </summary>
    internal static string NumberText(double value)
    {
        if (!double.IsFinite(value)) return "#NUM!";
        if (value == 0) return "0";
        if (double.TryParse(value.ToString("G15", CultureInfo.InvariantCulture), NumberStyles.Float, CultureInfo.InvariantCulture, out double shown)
            && double.IsFinite(shown) && shown != 0)
            value = shown; // near double.MaxValue the rounded digits overflow: the value is kept as it is
        return value.ToString(CultureInfo.InvariantCulture);
    }

    /// <summary>What a number format shows of a serial date: nothing (a plain number), the date, the time of day, both, or an elapsed time.</summary>
    internal enum DateKind { None, Date, Time, DateTime, Elapsed }

    /// <summary>
    /// A serial date as its format's kind shows it: a date as yyyy-MM-dd, a time of day as HH:mm:ss, both as
    /// yyyy-MM-dd HH:mm:ss, an elapsed time as its total hours:mm:ss (36:00:00 for 1.5); null for a plain number and
    /// outside 0 to 9999-12-31. The 1900 system keeps Excel's day zero (1900-01-00) and its false 29 February 1900
    /// (serial 60); the 1904 system counts from 1 January 1904.
    /// </summary>
    internal static string? DateText(double serial, DateKind kind, bool date1904)
    {
        if (kind == DateKind.None || !double.IsFinite(serial) || serial < 0 || serial >= 2_958_466) return null;
        long seconds = (long)Math.Round(serial * 86400, MidpointRounding.AwayFromZero);
        if (kind == DateKind.Elapsed)
            return string.Create(CultureInfo.InvariantCulture, $"{seconds / 3600:00}:{seconds / 60 % 60:00}:{seconds % 60:00}");
        long days = seconds / 86400, within = seconds % 86400;
        string time = string.Create(CultureInfo.InvariantCulture, $"{within / 3600:00}:{within / 60 % 60:00}:{within % 60:00}");
        if (kind == DateKind.Time) return time;
        string date;
        if (!date1904 && days == 0) date = "1900-01-00";
        else if (!date1904 && days == 60) date = "1900-02-29";
        else
        {
            var origin = date1904 ? new DateTime(1904, 1, 1) : days < 60 ? new DateTime(1899, 12, 31) : new DateTime(1899, 12, 30);
            if (days > (DateTime.MaxValue - origin).TotalDays - 1) return null;
            date = origin.AddDays(days).ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        }
        return kind == DateKind.Date ? date : date + " " + time;
    }

    /// <summary>
    /// What the built-in number formats show: 14 to 17, 27 to 36 and 50 to 58 dates, 18 to 21, 45 and 47 times of day,
    /// 22 both, 46 an elapsed time ([h]:mm:ss).
    /// </summary>
    internal static DateKind BuiltInKind(int format) => format switch
    {
        (>= 14 and <= 17) or (>= 27 and <= 36) or (>= 50 and <= 58) => DateKind.Date,
        (>= 18 and <= 21) or 45 or 47 => DateKind.Time,
        22 => DateKind.DateTime,
        46 => DateKind.Elapsed,
        _ => DateKind.None,
    };

    /// <summary>
    /// What a custom number format shows, from its letters outside quoted text, escapes and square brackets: y or d is a
    /// date; h, s, AM/PM or A/P a time of day; m is minutes right after an h or right before an s and a month otherwise.
    /// [h], [m] or [s] in square brackets make it an elapsed time; other bracketed parts ([Red], [$-409]) are passed over.
    /// An unclosed quote or bracket makes it a plain number.
    /// </summary>
    internal static DateKind FormatKind(string format)
    {
        var letters = new List<char>();
        bool elapsed = false, ampm = false;
        for (int i = 0; i < format.Length; i++)
        {
            char c = char.ToLowerInvariant(format[i]);
            switch (c)
            {
                case '"':
                    int close = format.IndexOf('"', i + 1);
                    if (close < 0) return DateKind.None;
                    i = close;
                    break;
                case '[':
                    int end = format.IndexOf(']', i + 1);
                    if (end < 0) return DateKind.None;
                    var inside = format.AsSpan(i + 1, end - i - 1);
                    char first = inside.Length > 0 ? char.ToLowerInvariant(inside[0]) : '\0';
                    if (first is 'h' or 'm' or 's' && AllSame(inside, first))
                    {
                        elapsed = true;
                        letters.Add(first);
                    }
                    i = end;
                    break;
                case '\\' or '_' or '*':
                    i++; // an escaped character, a space as wide as one, or a fill character
                    break;
                case 'a' when string.Compare(format, i, "am/pm", 0, 5, StringComparison.OrdinalIgnoreCase) == 0:
                    ampm = true;
                    i += 4;
                    break;
                case 'a' when string.Compare(format, i, "a/p", 0, 3, StringComparison.OrdinalIgnoreCase) == 0:
                    ampm = true;
                    i += 2;
                    break;
                case 'y' or 'd' or 'm' or 'h' or 's':
                    letters.Add(c); // a run of the same letter (yyyy, mm) is one part
                    while (i + 1 < format.Length && char.ToLowerInvariant(format[i + 1]) == c) i++;
                    break;
            }
        }
        if (elapsed) return DateKind.Elapsed;
        bool date = false, time = ampm;
        for (int i = 0; i < letters.Count; i++)
        {
            bool minutes = letters[i] == 'm' && (i > 0 && letters[i - 1] == 'h' || i + 1 < letters.Count && letters[i + 1] == 's');
            if (letters[i] is 'y' or 'd' || letters[i] == 'm' && !minutes) date = true;
            else time = true;
        }
        return date && time ? DateKind.DateTime : date ? DateKind.Date : time ? DateKind.Time : DateKind.None;

        static bool AllSame(ReadOnlySpan<char> s, char c)
        {
            foreach (char x in s)
                if (char.ToLowerInvariant(x) != c) return false;
            return true;
        }
    }

    /// <summary>An error value as Excel shows it.</summary>
    internal static string Error(int code) => code switch
    {
        0x00 => "#NULL!",
        0x07 => "#DIV/0!",
        0x0F => "#VALUE!",
        0x17 => "#REF!",
        0x1D => "#NAME?",
        0x24 => "#NUM!",
        0x2B => "#GETTING_DATA",
        _ => "#N/A",
    };

    /// <summary>The encoding of a BIFF5 workbook's 8-bit text: its Windows code page when it is a 1252-like one, otherwise Latin-1.</summary>
    static Encoding CodePage(int codePage)
    {
        if (codePage == 0x8001) codePage = 1252;
        if (codePage is 874 or (>= 1250 and <= 1258))
        {
            try { return Encoding.GetEncoding(codePage); }
            catch (Exception e) when (e is ArgumentException or NotSupportedException) { }
        }
        return Encoding.Latin1;
    }
}
