using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace Anything.Preview.UI;

/// <summary>Colours of search matches in the preview's views.</summary>
internal static class MatchColors
{
    /// <summary>Every match.</summary>
    public static readonly Color Match = Color.FromArgb(255, 236, 130);
    /// <summary>The match F3 is on.</summary>
    public static readonly Color Current = Color.FromArgb(255, 175, 70);
}

/// <summary>Virtual-mode, read-only grid for spreadsheets, CSV, archive and folder listings.</summary>
internal sealed class TableView : UserControl
{
    // Most matching cells kept (coloured, and visited by F3).
    private const int MaxHits = 50_000;
    private readonly Label _caption = new() { Dock = DockStyle.Top, Height = 24, TextAlign = ContentAlignment.MiddleLeft, Padding = new Padding(6, 0, 0, 0), Visible = false };
    private readonly Grid _grid = new();
    private TableData? _data;
    private Func<string, IReadOnlyList<(int Start, int Length)>>? _find;
    private List<(int Row, int Col)> _hits = new();
    private HashSet<long>? _hitSet;
    private int _current = -1;
    private (int Row, int Col)? _pendingScroll;
    private CancellationTokenSource? _scan;

    private sealed class Grid : DataGridView
    {
        public Grid()
        {
            DoubleBuffered = true;
            Dock = DockStyle.Fill;
            VirtualMode = true;
            ReadOnly = true;
            AllowUserToAddRows = false;
            AllowUserToDeleteRows = false;
            AllowUserToResizeRows = false;
            AllowUserToOrderColumns = false;
            BorderStyle = BorderStyle.None;
            BackgroundColor = SystemColors.Window;
            SelectionMode = DataGridViewSelectionMode.CellSelect;
            RowHeadersWidthSizeMode = DataGridViewRowHeadersWidthSizeMode.DisableResizing;
            ColumnHeadersHeightSizeMode = DataGridViewColumnHeadersHeightSizeMode.DisableResizing;
            ClipboardCopyMode = DataGridViewClipboardCopyMode.EnableWithoutHeaderText;
            EnableHeadersVisualStyles = false;
            ColumnHeadersDefaultCellStyle.BackColor = Color.FromArgb(240, 240, 240);
            RowHeadersDefaultCellStyle.BackColor = Color.FromArgb(240, 240, 240);
            DefaultCellStyle.WrapMode = DataGridViewTriState.False;
            GridColor = Color.FromArgb(225, 225, 225);
            RowTemplate.Height = 20;
        }
    }

    public TableView()
    {
        Controls.Add(_grid);
        Controls.Add(_caption);
        _grid.CellValueNeeded += (_, e) =>
        {
            if (_data == null || e.RowIndex < 0 || e.RowIndex >= _data.Rows.Count) return;
            var row = _data.Rows[e.RowIndex];
            e.Value = e.ColumnIndex < row.Length ? row[e.ColumnIndex] : "";
        };
        _grid.RowPostPaint += (_, e) =>
        {
            if (_data?.ShowRowNumbers != true) return;
            var r = new Rectangle(e.RowBounds.Left, e.RowBounds.Top, _grid.RowHeadersWidth - 4, e.RowBounds.Height);
            TextRenderer.DrawText(e.Graphics, (e.RowIndex + 1).ToString(), _grid.Font, r, Color.Gray,
                TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPrefix);
        };
        _grid.CellFormatting += (_, e) =>
        {
            if (_hitSet == null || e.RowIndex < 0 || e.ColumnIndex < 0 || e.CellStyle == null || !_hitSet.Contains(Key(e.RowIndex, e.ColumnIndex))) return;
            bool current = _current >= 0 && _hits[_current] == (e.RowIndex, e.ColumnIndex);
            e.CellStyle.BackColor = current ? MatchColors.Current : MatchColors.Match;
            e.CellStyle.SelectionBackColor = current ? Color.FromArgb(230, 140, 40) : Color.FromArgb(214, 190, 70);
        };
        _grid.SizeChanged += (_, _) => ApplyPendingScroll();
        _grid.VisibleChanged += (_, _) => ApplyPendingScroll();
    }

    public void SetData(TableData? data)
    {
        Highlight(null);
        _data = data;
        _grid.SuspendLayout();
        _grid.RowCount = 0;
        _grid.Columns.Clear();
        if (data != null)
        {
            _grid.RowHeadersVisible = data.ShowRowNumbers;
            _grid.RowHeadersWidth = Math.Max(40, TextRenderer.MeasureText(data.Rows.Count.ToString(), _grid.Font).Width + 18);
            for (int c = 0; c < data.Columns.Count; c++)
            {
                int w = TextRenderer.MeasureText(data.Columns[c], _grid.Font).Width + 16;
                for (int r = 0; r < Math.Min(60, data.Rows.Count); r++)
                {
                    var row = data.Rows[r];
                    if (c < row.Length && row[c].Length > 0)
                        w = Math.Max(w, TextRenderer.MeasureText(row[c].Length > 80 ? row[c][..80] : row[c], _grid.Font).Width + 12);
                }
                var col = new DataGridViewTextBoxColumn
                {
                    HeaderText = data.Columns[c], Width = Math.Clamp(w, 40, 420), SortMode = DataGridViewColumnSortMode.NotSortable,
                    FillWeight = 1,
                };
                _grid.Columns.Add(col);
            }
            _grid.RowCount = data.Rows.Count;
            _grid.CurrentCell = null; // no current-row glyph over the row numbers
            string cap = data.Caption ?? "";
            if (data.Truncated) cap += (cap.Length > 0 ? "  -  " : "") + "showing the first " + data.Rows.Count.ToString("N0") + " rows";
            _caption.Text = cap;
            _caption.Visible = cap.Length > 0;
        }
        _grid.ResumeLayout();
    }

    /// <summary>
    /// Colours the cells whose text <paramref name="find"/> matches and scrolls to the first one; the cells are searched
    /// off the UI thread (a sheet can hold 400,000 cells). Null removes the colours. The current cell is never set.
    /// </summary>
    public void Highlight(Func<string, IReadOnlyList<(int Start, int Length)>>? find)
    {
        _scan?.Cancel();
        _scan = null;
        _find = find;
        _hits = new();
        _hitSet = null;
        _current = -1;
        _pendingScroll = null;
        _grid.Invalidate();
        var data = _data;
        if (find == null || data == null || data.Rows.Count == 0) return;
        var ui = SynchronizationContext.Current;
        if (ui == null) return;
        var cts = new CancellationTokenSource();
        _scan = cts;
        Task.Run(() => Scan(data, find, cts.Token), cts.Token).ContinueWith(t =>
        {
            if (t.Status != TaskStatus.RanToCompletion) return;
            ui.Post(_ =>
            {
                if (IsDisposed || cts.IsCancellationRequested || !ReferenceEquals(data, _data) || !ReferenceEquals(find, _find)) return;
                _hits = t.Result;
                _hitSet = new HashSet<long>(_hits.Count);
                foreach (var (r, c) in _hits) _hitSet.Add(Key(r, c));
                if (_hits.Count > 0)
                {
                    _current = 0;
                    ScrollTo(_hits[0]);
                }
                _grid.Invalidate();
            }, null);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    /// <summary>Matching cells found so far, the current one and the first row shown (diagnostics).</summary>
    internal (int Count, (int Row, int Col)? Current, int FirstRow) MatchState =>
        (_hits.Count, _current >= 0 ? _hits[_current] : null, _grid.RowCount > 0 ? _grid.FirstDisplayedScrollingRowIndex : -1);

    /// <summary>Moves to the next (or previous) matching cell, wrapping around; false when there is none.</summary>
    public bool FindNext(bool backwards)
    {
        int n = _hits.Count;
        if (n == 0) return false;
        _current = _current < 0 ? (backwards ? n - 1 : 0) : (_current + (backwards ? -1 : 1) + n) % n;
        ScrollTo(_hits[_current], onlyIfHidden: true);
        _grid.Invalidate();
        return true;
    }

    private static long Key(int row, int col) => (long)row << 32 | (uint)col;

    private static List<(int Row, int Col)> Scan(TableData data, Func<string, IReadOnlyList<(int Start, int Length)>> find, CancellationToken ct)
    {
        var hits = new List<(int Row, int Col)>();
        for (int r = 0; r < data.Rows.Count && hits.Count < MaxHits; r++)
        {
            if ((r & 255) == 0) ct.ThrowIfCancellationRequested();
            var row = data.Rows[r];
            for (int c = 0; c < row.Length && c < data.Columns.Count; c++)
            {
                string cell = row[c];
                if (string.IsNullOrEmpty(cell)) continue;
                IReadOnlyList<(int Start, int Length)>? found;
                try { found = find(cell); }
                catch (Exception e) when (e is not OutOfMemoryException) { found = null; }
                if (found is { Count: > 0 }) hits.Add((r, c));
            }
        }
        return hits;
    }

    private void ScrollTo((int Row, int Col) cell, bool onlyIfHidden = false)
    {
        if (cell.Row >= _grid.RowCount || cell.Col >= _grid.ColumnCount) return;
        if (!_grid.IsHandleCreated || _grid.ClientSize.Height <= 0 || _grid.ClientSize.Width <= 0)
        {
            _pendingScroll = cell;
            return;
        }
        _pendingScroll = null;
        try
        {
            int shown = Math.Max(1, _grid.DisplayedRowCount(false));
            int first = _grid.FirstDisplayedScrollingRowIndex;
            if (!onlyIfHidden || first < 0 || cell.Row < first || cell.Row >= first + shown)
                _grid.FirstDisplayedScrollingRowIndex = Math.Max(0, cell.Row - Math.Min(2, shown / 3));
            if (!_grid.Columns[cell.Col].Displayed) _grid.FirstDisplayedScrollingColumnIndex = cell.Col;
        }
        catch (InvalidOperationException)
        {
            // Not laid out yet (no rows displayed): try again on the next resize.
            _pendingScroll = cell;
        }
    }

    private void ApplyPendingScroll()
    {
        if (_pendingScroll is { } cell && _grid.Visible) ScrollTo(cell);
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _scan?.Cancel();
        base.Dispose(disposing);
    }
}

/// <summary>Simple documents (docx/odt/pptx text, RTF) in a read-only RichTextBox.</summary>
internal sealed class DocumentView : RichTextBox
{
    private const int MaxChars = 400_000;
    // Matches F3 can visit in one document.
    private const int MaxHits = 100_000;
    // An RTF file's matches are coloured one by one, each a selection and a format change whose cost grows with the
    // document (with UI Automation clients listening, about 18 ms each in a 5 MB file on this PC): so only the matches on
    // screen and the current one are coloured, more as the view scrolls, at most this many in all and per screen. Text
    // built here (Office files) has its matches coloured in the RTF itself, at no cost.
    private const int MaxColouredOneByOne = 300, MaxColouredPerScreen = 120;
    // The back colours a coloured match had before are kept to put them back (a word may have several runs); past
    // these, the document is loaded again instead.
    private const int MaxRunsPerHit = 16, MaxSavedRuns = 4096;
    /// <summary>How long the last batch of colour changes took (diagnostics, PreviewHarness --match).</summary>
    internal static long LastBatchMs;
    /// <summary>How long the last <see cref="Highlight"/> took on the UI thread (diagnostics, PreviewHarness --match).</summary>
    internal static long LastHighlightMs;
    private const int WM_SETREDRAW = 0x000B, EM_SETEVENTMASK = 0x0400 + 69, EM_GETLINECOUNT = 0x00BA, EM_GETSCROLLPOS = 0x0400 + 221, EM_SETSCROLLPOS = 0x0400 + 222;
    private const int EM_EXSETSEL = 0x0400 + 55, EM_GETCHARFORMAT = 0x0400 + 58, EM_SETCHARFORMAT = 0x0400 + 68, EM_GETTEXTRANGE = 0x0400 + 75,
        EM_GETTEXTEX = 0x0400 + 94, EM_GETTEXTLENGTHEX = 0x0400 + 95;
    private const int SCF_SELECTION = 1, CFM_BACKCOLOR = 0x04000000, CFE_AUTOBACKCOLOR = 0x04000000;
    private const int GT_RAWTEXT = 4, GTL_PRECISE = 2, GTL_NUMCHARS = 8, CP_UNICODE = 1200;

    private readonly List<(int Start, int Length)> _hits = new();
    private int _current = -1;     // index in _hits of the match F3 is on; -1 = none
    private int _searchFrom;       // with no current match, F3 starts here (a slide's heading)
    private int _pendingScroll = -1;
    private int _placed = -1;      // the character a match scroll put in view, placed again on resize until the user scrolls
    private string? _shownRtf;     // what SetDocument showed, to start again from a clean document
    private string? _text;         // the text as RichEdit holds it (positions = character positions); null = read it again
    private IReadOnlyList<DocBlock>? _blocks; // the paragraphs shown, when the RTF was built here (not an .rtf file)
    private Func<string, IReadOnlyList<(int Start, int Length)>>? _builtWith; // the finder whose matches the built RTF colours
    // Back colours of the ranges coloured one by one, as they were before, in the order saved (put back in reverse, so
    // overlapping ranges end with their first colour); the hits saved and the hits coloured; and whether one couldn't be saved.
    private readonly List<(int Start, int Length, int Effects, int Color)> _saved = new();
    private readonly HashSet<int> _savedHits = new();
    private readonly HashSet<int> _coloured = new();
    private bool _restoreByReload;
    private double _changeMs = 1;  // time one colour change takes in this document (batches so far: their time / their changes)
    private double _batchMs;
    private int _batchChanges;
    private long _loadMs;          // time the document took to load: putting many colours back costs more than loading it again
    private int _changes;          // colour changes in the running batch
    private bool _colourQueued;    // colouring the matches on screen is queued (after a scroll)

    public DocumentView()
    {
        ReadOnly = true;
        BorderStyle = BorderStyle.None;
        BackColor = SystemColors.Window;
        DetectUrls = false;
        ShortcutsEnabled = true;
        WordWrap = true;
        ScrollBars = RichTextBoxScrollBars.Vertical;
    }

    /// <summary>Shows a document; with <paramref name="find"/>, built paragraphs come with their matches coloured.</summary>
    public void SetDocument(PreviewResult r, Func<string, IReadOnlyList<(int Start, int Length)>>? find = null)
    {
        ForgetMatches();
        ForgetSaved();
        _text = null;
        _blocks = r.Rtf == null ? r.Document ?? Array.Empty<DocBlock>() : null;
        _builtWith = _blocks != null ? find : null;
        _changeMs = 1;
        _batchMs = 0;
        _batchChanges = 0;
        try
        {
            _shownRtf = r.Rtf ?? BuildRtf(_blocks!, find);
            Load(_shownRtf);
        }
        catch
        {
            _shownRtf = null;
            _blocks = null;
            Text = r.Rtf ?? string.Join(Environment.NewLine, (r.Document ?? Array.Empty<DocBlock>()).Select(b => b.Text));
        }
    }

    /// <summary>Empties the view.</summary>
    public void Reset()
    {
        ForgetMatches();
        ForgetSaved();
        _text = null;
        _shownRtf = null;
        _blocks = null;
        _builtWith = null;
        Clear();
    }

    /// <summary>Loads RTF, timing it; the text read before is forgotten.</summary>
    private void Load(string rtf)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        Rtf = rtf;
        _loadMs = clock.ElapsedMilliseconds;
        _text = null;
    }

    private void ForgetSaved()
    {
        _saved.Clear();
        _savedHits.Clear();
        _coloured.Clear();
        _restoreByReload = false;
    }

    private void ForgetMatches()
    {
        _hits.Clear();
        _current = -1;
        _searchFrom = 0;
        _pendingScroll = -1;
        _placed = -1;
    }

    /// <summary>
    /// Colours what <paramref name="find"/> returns in the shown text (RichEdit's own text: bullets, line breaks, table
    /// delimiters and the cut are already in it) and scrolls to the first match, or to the first one after the line equal
    /// to <paramref name="anchorLine"/> ("Slide 3") and before the next such heading, else to that line. Null removes the colours.
    /// </summary>
    public void Highlight(Func<string, IReadOnlyList<(int Start, int Length)>>? find, string? anchorLine = null)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        try { HighlightCore(find, anchorLine); }
        finally { LastHighlightMs = clock.ElapsedMilliseconds; }
    }

    private void HighlightCore(Func<string, IReadOnlyList<(int Start, int Length)>>? find, string? anchorLine)
    {
        if (!IsHandleCreated) _ = Handle;
        bool reloaded = false;
        if (_blocks != null && !ReferenceEquals(find, _builtWith))
        {
            // Built here: the matches go into the RTF (one load, however many there are).
            _builtWith = find;
            try
            {
                _shownRtf = BuildRtf(_blocks, find);
                Load(_shownRtf);
                reloaded = true;
            }
            catch (ArgumentException) { }
        }
        // The colours of the last matches go back to what they were (a reload already did that).
        if (reloaded) ForgetSaved();
        else Restore();
        ForgetMatches();
        if (find == null) return;
        // Positions in RichEdit's own text are what Select takes; RichTextBox.Text leaves out table delimiters.
        string text = _text ??= StoredText();
        IReadOnlyList<(int Start, int Length)>? found = null;
        try { found = find(text); }
        catch (Exception e) when (e is not OutOfMemoryException) { found = null; }
        if (found != null)
        {
            foreach (var (s, l) in found)
            {
                int a = Math.Clamp(s, 0, text.Length), b = Math.Clamp(s + Math.Max(0, l), a, text.Length);
                if (b > a) _hits.Add((a, b - a));
                if (_hits.Count >= MaxHits) break;
            }
            _hits.Sort((x, y) => x.Start.CompareTo(y.Start));
        }
        int target = -1;
        if (anchorLine != null && LineStart(text, anchorLine, 0) is int anchor and >= 0)
        {
            _searchFrom = anchor;
            int next = NextHeadingStart(text, anchorLine, anchor);
            int i = _hits.FindIndex(h => h.Start >= anchor);
            if (i >= 0 && (next < 0 || _hits[i].Start < next)) { _current = i; target = _hits[i].Start; }
            else target = anchor;
        }
        else if (_hits.Count > 0)
        {
            _current = 0;
            target = _hits[0].Start;
        }
        // Placed, then the current match and (an RTF file) the matches on screen are coloured.
        if (target >= 0) ScrollToChar(target);
        else ColourVisible();
    }

    /// <summary>Matches coloured, and the current one with its line (diagnostics).</summary>
    internal (int Count, int Current, int Line, int FirstVisibleLine, int Y, int Height) MatchState =>
        (_hits.Count, _current, _current >= 0 ? GetLineFromCharIndex(_hits[_current].Start) : -1, GetLineFromCharIndex(GetCharIndexFromPosition(new Point(2, 2))),
         _current >= 0 ? GetPositionFromCharIndex(_hits[_current].Start).Y : -1, ClientSize.Height);

    /// <summary>
    /// The characters RichEdit holds where the current match is coloured, read back by position (EM_GETTEXTRANGE), so
    /// it shows whether the colour landed on the word (diagnostics); null when there is no current match.
    /// </summary>
    internal string? CurrentHitText => _current >= 0 ? TextRange(_hits[_current].Start, _hits[_current].Length) : null;

    /// <summary>Moves to the next (or previous) match, wrapping around; false when there is none.</summary>
    public bool FindNext(bool backwards)
    {
        int n = _hits.Count;
        if (n == 0) return false;
        int next;
        if (_current >= 0) next = (_current + (backwards ? -1 : 1) + n) % n;
        else
        {
            next = backwards ? _hits.FindLastIndex(h => h.Start < _searchFrom) : _hits.FindIndex(h => h.Start >= _searchFrom);
            if (next < 0) next = backwards ? n - 1 : 0;
        }
        int old = _current;
        _current = next;
        WithoutRedraw(() =>
        {
            if (old >= 0) ColourHit(old, MatchColors.Match);
            ColourHit(next, MatchColors.Current);
        });
        var at = GetPositionFromCharIndex(_hits[next].Start);
        if (at.Y < 0 || at.Y > ClientSize.Height - Font.Height * 2) ScrollToChar(_hits[next].Start);
        return true;
    }

    /// <summary>
    /// Puts back the back colours of the matches coloured one by one (those that were on screen: a few format changes),
    /// or loads the document again when that is cheaper (many of them in a large document) or one couldn't be saved.
    /// </summary>
    private void Restore()
    {
        bool reload = _restoreByReload || _saved.Count * _changeMs > Math.Max(_loadMs, 100);
        if (reload && _coloured.Count > 0)
        {
            ForgetSaved();
            if (_shownRtf != null)
            {
                try
                {
                    Load(_shownRtf);
                    return;
                }
                catch (ArgumentException) { }
            }
            WithoutRedraw(() =>
            {
                SelectAll();
                SelectionBackColor = BackColor;
            });
            return;
        }
        if (_saved.Count > 0)
        {
            WithoutRedraw(() =>
            {
                for (int i = _saved.Count - 1; i >= 0; i--)
                {
                    var s = _saved[i];
                    SetBack(s.Start, s.Length, s.Effects, s.Color);
                }
            });
        }
        ForgetSaved();
    }

    /// <summary>
    /// Colours the current match, and (an RTF file) the matches on screen that aren't coloured yet: saved first, all of
    /// them before any is coloured, so overlapping ranges save the document's own colours.
    /// </summary>
    private void ColourVisible()
    {
        _colourQueued = false;
        if (IsDisposed || !IsHandleCreated || _hits.Count == 0) return;
        var todo = new List<int>();
        if (_current >= 0 && !_coloured.Contains(_current)) todo.Add(_current);
        if (_blocks == null && ClientSize.Width > 0 && ClientSize.Height > 0 && _coloured.Count < MaxColouredOneByOne)
        {
            int first = GetCharIndexFromPosition(Point.Empty);
            int last = GetCharIndexFromPosition(new Point(ClientSize.Width - 1, ClientSize.Height - 1));
            int room = Math.Min(MaxColouredPerScreen, MaxColouredOneByOne - _coloured.Count);
            for (int i = FirstHitEndingAfter(first); i < _hits.Count && _hits[i].Start <= last && todo.Count < room; i++)
                if (i != _current && !_coloured.Contains(i)) todo.Add(i);
        }
        if (todo.Count == 0) return;
        WithoutRedraw(() =>
        {
            foreach (int i in todo) Save(i);
            foreach (int i in todo) ColourHit(i, i == _current ? MatchColors.Current : MatchColors.Match);
        });
    }

    /// <summary>Colours the matches that came on screen, once the scrolling settles.</summary>
    private void QueueColourVisible()
    {
        if (_colourQueued || _blocks != null || _hits.Count == 0 || !IsHandleCreated || IsDisposed) return;
        _colourQueued = true;
        BeginInvoke(ColourVisible);
    }

    /// <summary>Index of the first match that ends after the position (the matches are sorted by start).</summary>
    private int FirstHitEndingAfter(int position)
    {
        int lo = 0, hi = _hits.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) >>> 1;
            if (_hits[mid].Start + _hits[mid].Length <= position) lo = mid + 1;
            else hi = mid;
        }
        // A long match starting earlier can still reach past it: look back a little.
        while (lo > 0 && _hits[lo - 1].Start + _hits[lo - 1].Length > position) lo--;
        return lo;
    }

    /// <summary>Colours one match, saving the back colours it had first.</summary>
    private void ColourHit(int index, Color color)
    {
        Save(index);
        _coloured.Add(index);
        var (start, length) = _hits[index];
        SetBack(start, length, 0, ColorTranslator.ToWin32(color));
    }

    /// <summary>Saves the back colours of one match's characters (its runs, when they differ), once per match.</summary>
    private void Save(int index)
    {
        if (!_savedHits.Add(index) || _restoreByReload) return;
        var (start, length) = _hits[index];
        int before = _saved.Count;
        var todo = new Stack<(int Start, int Length)>();
        todo.Push((start, length));
        while (todo.Count > 0)
        {
            if (_saved.Count - before >= MaxRunsPerHit || _saved.Count >= MaxSavedRuns)
            {
                // Too many runs to put back one by one: the document is loaded again instead.
                _restoreByReload = true;
                _saved.Clear();
                return;
            }
            var (s, l) = todo.Pop();
            var cf = GetFormat(s, l);
            if ((cf.dwMask & CFM_BACKCOLOR) != 0 || l == 1)
            {
                _saved.Add((s, l, cf.dwEffects & CFE_AUTOBACKCOLOR, cf.crBackColor));
                continue;
            }
            // Not one colour over the range: its halves, the first one on top.
            int half = l / 2;
            todo.Push((s + half, l - half));
            todo.Push((s, half));
        }
    }

    /// <summary>Selects a range by character position (EM_EXSETSEL), with no check against the text length.</summary>
    private void SelectRange(int start, int length)
    {
        var range = new CHARRANGE { cpMin = start, cpMax = start + length };
        SendMessage(Handle, EM_EXSETSEL, IntPtr.Zero, ref range);
    }

    private CHARFORMAT2W GetFormat(int start, int length)
    {
        SelectRange(start, length);
        var cf = new CHARFORMAT2W { cbSize = Marshal.SizeOf<CHARFORMAT2W>(), szFaceName = "" };
        SendMessage(Handle, EM_GETCHARFORMAT, (IntPtr)SCF_SELECTION, ref cf);
        return cf;
    }

    /// <summary>Sets the back colour of a range: automatic (the window's) when effects has CFE_AUTOBACKCOLOR, else the colour.</summary>
    private void SetBack(int start, int length, int effects, int color)
    {
        _changes++;
        SelectRange(start, length);
        var cf = new CHARFORMAT2W { cbSize = Marshal.SizeOf<CHARFORMAT2W>(), dwMask = CFM_BACKCOLOR, dwEffects = effects, crBackColor = color, szFaceName = "" };
        SendMessage(Handle, EM_SETCHARFORMAT, (IntPtr)SCF_SELECTION, ref cf);
    }

    /// <summary>
    /// The text exactly as RichEdit holds it (EM_GETTEXTEX with GT_RAWTEXT): '\r' ends paragraphs, and table row and
    /// cell delimiters and object markers are in it, so an index into it is a character position. RichTextBox.Text
    /// (a text stream) leaves the delimiters out, which moves every position after a table.
    /// </summary>
    private string StoredText()
    {
        try
        {
            var length = new GETTEXTLENGTHEX { flags = GTL_NUMCHARS | GTL_PRECISE, codepage = CP_UNICODE };
            int n = (int)SendMessage(Handle, EM_GETTEXTLENGTHEX, ref length, IntPtr.Zero);
            if (n <= 0) return n == 0 ? "" : Text;
            var buffer = new char[n + 1];
            var get = new GETTEXTEX { cb = buffer.Length * 2, flags = GT_RAWTEXT, codepage = CP_UNICODE };
            int got = (int)SendMessage(Handle, EM_GETTEXTEX, ref get, buffer);
            return got > 0 ? new string(buffer, 0, Math.Min(got, n)) : Text;
        }
        catch (Exception e) when (e is EntryPointNotFoundException or OverflowException) { return Text; }
    }

    /// <summary>RichEdit's characters from a position (EM_GETTEXTRANGE).</summary>
    private string TextRange(int start, int length)
    {
        length = Math.Clamp(length, 0, 4096);
        var buffer = Marshal.AllocHGlobal((length + 1) * 2);
        try
        {
            var range = new TEXTRANGE { chrg = new CHARRANGE { cpMin = start, cpMax = start + length }, lpstrText = buffer };
            int got = (int)SendMessage(Handle, EM_GETTEXTRANGE, IntPtr.Zero, ref range);
            return got > 0 ? Marshal.PtrToStringUni(buffer, Math.Min(got, length)) : "";
        }
        finally { Marshal.FreeHGlobal(buffer); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct CHARRANGE { public int cpMin, cpMax; }

    [StructLayout(LayoutKind.Sequential)]
    private struct TEXTRANGE { public CHARRANGE chrg; public IntPtr lpstrText; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GETTEXTLENGTHEX { public int flags, codepage; }

    [StructLayout(LayoutKind.Sequential)]
    private struct GETTEXTEX { public int cb, flags, codepage; public IntPtr lpDefaultChar, lpUsedDefChar; }

    /// <summary>richedit.h CHARFORMAT2W (116 bytes).</summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CHARFORMAT2W
    {
        public int cbSize, dwMask, dwEffects, yHeight, yOffset, crTextColor;
        public byte bCharSet, bPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string szFaceName;
        public short wWeight, sSpacing;
        public int crBackColor, lcid, dwReserved;
        public short sStyle, wKerning;
        public byte bUnderlineType, bAnimation, bRevAuthor, bUnderlineColor;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref CHARRANGE lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref TEXTRANGE lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref CHARFORMAT2W lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, ref GETTEXTLENGTHEX wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, ref GETTEXTEX wParam, [Out] char[] lParam);

    /// <summary>
    /// Runs a batch of selections and format changes without painting or scrolling, then shows the result once; the
    /// time it took per colour change is what putting colours back is reckoned at (against loading the document again).
    /// </summary>
    private void WithoutRedraw(Action change)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        _changes = 0;
        var scroll = Point.Empty;
        SendMessage(Handle, EM_GETSCROLLPOS, IntPtr.Zero, ref scroll);
        SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
        var mask = SendMessage(Handle, EM_SETEVENTMASK, IntPtr.Zero, IntPtr.Zero);
        var tom = TextDocument();
        int frozen = 0;
        tom?.Freeze(out frozen);
        try
        {
            change();
            Select(0, 0);
        }
        finally
        {
            tom?.Unfreeze(out frozen);
            SendMessage(Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref scroll);
            SendMessage(Handle, EM_SETEVENTMASK, IntPtr.Zero, mask);
            SendMessage(Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            Invalidate();
            LastBatchMs = clock.ElapsedMilliseconds;
            if (_changes > 0)
            {
                _batchMs += clock.Elapsed.TotalMilliseconds;
                _batchChanges += _changes;
                _changeMs = _batchMs / _batchChanges;
            }
        }
    }

    /// <summary>Puts the character a third of the way down, now or once the view has a size (RichEdit can't place text before).</summary>
    /// <param name="again">Also check once more after the queued layout work (the first call of a placement).</param>
    private void ScrollToChar(int index, bool again = true)
    {
        if (!IsHandleCreated || !Visible || ClientSize.Width <= 0 || ClientSize.Height <= 0)
        {
            _pendingScroll = index;
            return;
        }
        _pendingScroll = -1;
        _placed = index;
        int c = Math.Clamp(index, 0, TextLength), want = ClientSize.Height / 3;
        // Counting the lines makes RichEdit finish wrapping the whole text at the current width (after a resize it
        // re-wraps in the background, and positions measured before that move).
        SendMessage(Handle, EM_GETLINECOUNT, IntPtr.Zero, IntPtr.Zero);
        // RichEdit lays out lines it hasn't shown yet lazily, so the first position can be an estimate: measure again
        // after scrolling, and once more when the queued layout work is done.
        for (int pass = 0; pass < 3; pass++)
        {
            int y = GetPositionFromCharIndex(c).Y;
            if (Math.Abs(y - want) <= Font.Height) break;
            var scroll = Point.Empty;
            SendMessage(Handle, EM_GETSCROLLPOS, IntPtr.Zero, ref scroll);
            scroll.Y = Math.Max(0, scroll.Y + y - want);
            SendMessage(Handle, EM_SETSCROLLPOS, IntPtr.Zero, ref scroll);
        }
        ColourVisible();
        if (again)
        {
            BeginInvoke(() =>
            {
                if (_placed == index && !IsDisposed) ScrollToChar(index, again: false);
            });
        }
    }

    protected override void OnSizeChanged(EventArgs e)
    {
        base.OnSizeChanged(e);
        // Placed while the view was still being laid out (or wrapped at another width): place it again.
        if (_pendingScroll >= 0) ScrollToChar(_pendingScroll);
        else if (_placed >= 0) ScrollToChar(_placed);
        else QueueColourVisible();
    }

    protected override void OnVScroll(EventArgs e)
    {
        base.OnVScroll(e);
        QueueColourVisible();
    }

    protected override void WndProc(ref Message m)
    {
        // The user scrolls, clicks or types: leave the view where they put it (and colour the matches it brings).
        bool user = m.Msg is 0x0115 /* WM_VSCROLL */ or 0x020A /* WM_MOUSEWHEEL */ or 0x0201 /* WM_LBUTTONDOWN */ or 0x0100 /* WM_KEYDOWN */;
        if (user) _placed = -1;
        base.WndProc(ref m);
        if (user) QueueColourVisible();
    }

    protected override void OnVisibleChanged(EventArgs e)
    {
        base.OnVisibleChanged(e);
        if (_pendingScroll >= 0) ScrollToChar(_pendingScroll);
    }

    /// <summary>Start of the first line from <paramref name="from"/> on that is exactly <paramref name="line"/>; -1 when none.</summary>
    private static int LineStart(string text, string line, int from)
    {
        for (int at = text.IndexOf(line, from, StringComparison.Ordinal); at >= 0; at = text.IndexOf(line, at + 1, StringComparison.Ordinal))
        {
            bool starts = at == 0 || text[at - 1] is '\n' or '\r' or '\v';
            int end = at + line.Length;
            bool ends = end == text.Length || text[end] is '\n' or '\r' or '\v';
            if (starts && ends) return at;
        }
        return -1;
    }

    /// <summary>The next heading like "Slide N" after the anchor ("Slide N+1" or any later slide); -1 when none.</summary>
    private static int NextHeadingStart(string text, string anchorLine, int anchor)
    {
        int space = anchorLine.LastIndexOf(' ');
        if (space < 0 || !int.TryParse(anchorLine.AsSpan(space + 1), out _)) return -1;
        string prefix = anchorLine[..(space + 1)];
        for (int at = text.IndexOf(prefix, anchor + anchorLine.Length, StringComparison.Ordinal); at >= 0; at = text.IndexOf(prefix, at + 1, StringComparison.Ordinal))
        {
            if (at > 0 && text[at - 1] is not ('\n' or '\r' or '\v')) continue;
            int end = at + prefix.Length;
            while (end < text.Length && char.IsAsciiDigit(text[end])) end++;
            if (end > at + prefix.Length && (end == text.Length || text[end] is '\n' or '\r' or '\v')) return at;
        }
        return -1;
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, out IntPtr lParam);

    private ITextDocument? _tom;

    protected override void OnHandleDestroyed(EventArgs e)
    {
        // The text object model belongs to this window: a new handle gets its own.
        if (_tom != null)
        {
            try { Marshal.ReleaseComObject(_tom); } catch (ArgumentException) { }
            _tom = null;
        }
        base.OnHandleDestroyed(e);
    }

    /// <summary>
    /// RichEdit's text object model (Freeze/Unfreeze): format changes made while it is frozen cost about a fifth as much
    /// (2,000 one by one: 2.8 s instead of 14 s on this PC). Null when it isn't available.
    /// </summary>
    private ITextDocument? TextDocument()
    {
        if (_tom != null) return _tom;
        try
        {
            SendMessage(Handle, 0x0400 + 60 /* EM_GETOLEINTERFACE */, IntPtr.Zero, out IntPtr ole);
            if (ole == IntPtr.Zero) return null;
            try { _tom = Marshal.GetObjectForIUnknown(ole) as ITextDocument; }
            finally { Marshal.Release(ole); }
        }
        catch (Exception e) when (e is COMException or InvalidCastException) { _tom = null; }
        return _tom;
    }

    /// <summary>The start of TOM's ITextDocument, up to Unfreeze (tom.h order).</summary>
    [ComImport, Guid("8CC497C0-A1DF-11CE-8098-00AA0047BE5D"), InterfaceType(ComInterfaceType.InterfaceIsDual)]
    private interface ITextDocument
    {
        [PreserveSig] int GetName(out IntPtr name);
        [PreserveSig] int GetSelection(out IntPtr selection);
        [PreserveSig] int GetStoryCount(out int count);
        [PreserveSig] int GetStoryRanges(out IntPtr stories);
        [PreserveSig] int GetSaved(out int value);
        [PreserveSig] int SetSaved(int value);
        [PreserveSig] int GetDefaultTabStop(out float value);
        [PreserveSig] int SetDefaultTabStop(float value);
        [PreserveSig] int New();
        [PreserveSig] int Open(IntPtr var, int flags, int codePage);
        [PreserveSig] int Save(IntPtr var, int flags, int codePage);
        [PreserveSig] int Freeze(out int count);
        [PreserveSig] int Unfreeze(out int count);
    }

    [DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref Point lParam);

    /// <param name="find">Colours its matches in each paragraph (\highlight4); null = no colours, the RTF as always.</param>
    internal static string BuildRtf(IReadOnlyList<DocBlock> blocks, Func<string, IReadOnlyList<(int Start, int Length)>>? find = null)
    {
        var sb = new StringBuilder();
        sb.Append(@"{\rtf1\ansi\ansicpg1252\deff0{\fonttbl{\f0\fswiss Segoe UI;}}{\colortbl;\red40\green40\blue40;\red31\green78\blue121;\red110\green110\blue110;");
        if (find != null) sb.Append(@"\red").Append(MatchColors.Match.R).Append(@"\green").Append(MatchColors.Match.G).Append(@"\blue").Append(MatchColors.Match.B).Append(';');
        sb.Append('}');
        sb.Append(@"\viewkind4\uc1\f0\fs20\cf1\sa120 ");
        int chars = 0;
        foreach (var b in blocks)
        {
            if (chars > MaxChars) { sb.Append(@"\i (truncated)\i0\par "); break; }
            chars += b.Text.Length;
            switch (b.Style)
            {
                case DocBlockStyle.Title: sb.Append(@"\pard\sa200\fs40\b\cf2 "); break;
                case DocBlockStyle.Heading1: sb.Append(@"\pard\sb240\sa120\fs32\b\cf2 "); break;
                case DocBlockStyle.Heading2: sb.Append(@"\pard\sb200\sa100\fs26\b\cf2 "); break;
                case DocBlockStyle.Heading3: sb.Append(@"\pard\sb160\sa80\fs22\b\cf2 "); break;
                case DocBlockStyle.ListItem: sb.Append(@"\pard\li360\fi-200\sa60\fs20\cf1 \bullet  "); break;
                case DocBlockStyle.Caption: sb.Append(@"\pard\sa120\fs18\i\cf3 "); break;
                default: sb.Append(@"\pard\sa120\fs20\cf1 "); break;
            }
            if (find == null) Escape(sb, b.Text);
            else EscapeHighlighted(sb, b.Text, find);
            sb.Append(@"\b0\i0\par").Append('\n');
        }
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>A paragraph with its matches in {\highlight4 …} groups (sorted, clamped, overlaps merged).</summary>
    private static void EscapeHighlighted(StringBuilder sb, string s, Func<string, IReadOnlyList<(int Start, int Length)>> find)
    {
        IReadOnlyList<(int Start, int Length)>? found = null;
        if (s.Length > 0)
        {
            try { found = find(s); }
            catch (Exception e) when (e is not OutOfMemoryException) { found = null; }
        }
        if (found is not { Count: > 0 })
        {
            Escape(sb, s);
            return;
        }
        var ranges = new List<(int Start, int End)>(found.Count);
        foreach (var (start, length) in found)
        {
            int a = Math.Clamp(start, 0, s.Length), b = Math.Clamp(start + Math.Max(0, length), a, s.Length);
            if (b > a) ranges.Add((a, b));
        }
        ranges.Sort((x, y) => x.Start.CompareTo(y.Start));
        int at = 0;
        foreach (var (a, b) in ranges)
        {
            if (b <= at) continue;
            int from = Math.Max(a, at);
            Escape(sb, s.AsSpan(at, from - at));
            sb.Append(@"{\highlight4 ");
            Escape(sb, s.AsSpan(from, b - from));
            sb.Append('}');
            at = b;
        }
        Escape(sb, s.AsSpan(at));
    }

    private static void Escape(StringBuilder sb, ReadOnlySpan<char> s)
    {
        foreach (char c in s)
        {
            switch (c)
            {
                case '\\': sb.Append(@"\\"); break;
                case '{': sb.Append(@"\{"); break;
                case '}': sb.Append(@"\}"); break;
                case '\n': sb.Append(@"\line "); break;
                case '\r': break;
                case '\t': sb.Append(@"\tab "); break;
                default:
                    if (c < 0x80) sb.Append(c);
                    else sb.Append(@"\u").Append((short)c).Append('?');
                    break;
            }
        }
    }
}

/// <summary>Info card: big icon/thumbnail + facts, then a hex dump (binary files) or the folder listing.</summary>
internal sealed class InfoView : UserControl
{
    private readonly HeaderPanel _header = new() { Dock = DockStyle.Top };
    private readonly TextView _hex = new() { Dock = DockStyle.Fill, ShowLineNumbers = false, Visible = false };
    private readonly TableView _table = new() { Dock = DockStyle.Fill, Visible = false };

    public InfoView()
    {
        BackColor = SystemColors.Window;
        Controls.Add(_hex);
        Controls.Add(_table);
        Controls.Add(_header);
    }

    public void SetResult(PreviewResult r)
    {
        _header.Set(r);
        _hex.Visible = r.HexDump != null;
        if (r.HexDump != null) _hex.SetText(r.HexDump);
        // Note: Control.Visible reads false while the parent is hidden, so use the computed flag.
        bool showTable = r.HexDump == null && r.Table != null;
        _table.Visible = showTable;
        _table.SetData(showTable ? r.Table : null);
    }

    public void Clear() => _header.Set(null);

    private sealed class HeaderPanel : Control
    {
        private Bitmap? _icon; // not owned
        private string _title = "";
        private string _sub = "";
        private List<KeyValuePair<string, string>> _meta = new();
        private List<string> _diag = new();

        public HeaderPanel()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            BackColor = SystemColors.Window;
            Height = 160;
        }

        public void Set(PreviewResult? r)
        {
            _icon = r?.Image;
            _title = r == null ? "" : (Path.GetFileName(r.Path) is { Length: > 0 } n ? n : r.Path);
            _sub = r?.FormatName ?? "";
            _meta = r?.Metadata ?? new();
            _diag = r?.Diagnostics ?? new();
            int lines = 2 + _meta.Count + (_diag.Count > 0 ? 1 + _diag.Count : 0);
            Height = Math.Max(Math.Min(_icon?.Height ?? 0, 128) + 24, 20 + lines * (Font.Height + 4) + 12);
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.Clear(BackColor);
            int x = 16, y = 12;
            if (_icon != null)
            {
                int s = Math.Min(128, Math.Max(_icon.Width, _icon.Height));
                var fit = Util.BitmapUtil.FitSize(_icon.Size, new Size(s, s), allowUpscale: true);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.DrawImage(_icon, new Rectangle(x + (128 - fit.Width) / 2, y + (128 - fit.Height) / 2, fit.Width, fit.Height));
                x += 128 + 20;
            }
            int w = Math.Max(50, ClientSize.Width - x - 12);
            using (var bold = new Font(Font.FontFamily, Font.Size + 3, FontStyle.Bold))
            {
                TextRenderer.DrawText(g, _title, bold, new Rectangle(x, y, w, bold.Height + 2), ForeColor, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
                y += bold.Height + 2;
            }
            TextRenderer.DrawText(g, _sub, Font, new Rectangle(x, y, w, Font.Height + 2), Color.DimGray, TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            y += Font.Height + 10;
            int keyW = 0;
            foreach (var kv in _meta) keyW = Math.Max(keyW, TextRenderer.MeasureText(kv.Key, Font).Width);
            keyW = Math.Min(keyW + 8, 140);
            foreach (var kv in _meta)
            {
                TextRenderer.DrawText(g, kv.Key, Font, new Rectangle(x, y, keyW, Font.Height + 2), Color.Gray, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                TextRenderer.DrawText(g, kv.Value, Font, new Rectangle(x + keyW, y, w - keyW, Font.Height + 2), ForeColor, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                y += Font.Height + 4;
            }
            if (_diag.Count > 0)
            {
                y += 4;
                TextRenderer.DrawText(g, "Providers tried:", Font, new Point(x, y), Color.Gray, TextFormatFlags.NoPrefix);
                y += Font.Height + 2;
                foreach (var d in _diag)
                {
                    TextRenderer.DrawText(g, d, Font, new Rectangle(x, y, w, Font.Height + 2), Color.Firebrick, TextFormatFlags.NoPrefix | TextFormatFlags.EndEllipsis);
                    y += Font.Height + 2;
                }
            }
        }
    }
}
