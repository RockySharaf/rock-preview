using Anything.Interop;

namespace Anything;

internal sealed partial class MainForm
{
    const TextFormatFlags BaseTextFlags = TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine | TextFormatFlags.VerticalCenter
                                          | TextFormatFlags.NoPadding | TextFormatFlags.PreserveGraphicsClipping;

    /// <summary>
    /// Explorer's selected rows, measured from its own Details view (the same whatever the accent colour): a 1px border
    /// around the fill, the keyboard-focused row's black (light) or grey (dark); while the list hasn't the focus, grey
    /// without a border.
    /// </summary>
    static (Color Back, Color Border, Color Fore) SelectionColors(bool active, bool focusedRow)
    {
        if (Application.IsDarkModeEnabled)
            return !active ? (Color.FromArgb(0x3C, 0x3C, 0x3C), Color.FromArgb(0x3C, 0x3C, 0x3C), Color.Gainsboro)
                : focusedRow ? (Color.FromArgb(0x50, 0x50, 0x50), Color.FromArgb(0xC3, 0xC3, 0xC3), Color.White)
                : (Color.FromArgb(0x62, 0x62, 0x62), Color.FromArgb(0x60, 0xCD, 0xFF), Color.White);
        return !active ? (Color.FromArgb(0xD9, 0xD9, 0xD9), Color.FromArgb(0xD9, 0xD9, 0xD9), SystemColors.WindowText)
            : (Color.FromArgb(0xCC, 0xE8, 0xFF), focusedRow ? Color.Black : Color.FromArgb(0x00, 0x78, 0xD4), SystemColors.WindowText);
    }

    /// <summary>
    /// Explorer's Details rows, measured from its own view: 30 apart at 100% with each item 25 high (37 and 31 at 125%),
    /// so selected rows stay apart; Explorer's compact view packs them closer. Taller when the font is bigger.
    /// </summary>
    void ApplyRowHeight()
    {
        float scale = DeviceDpi / 96f;
        bool compact = ExplorerTheme.CompactView;
        int pitch = (int)((compact ? 22 : 30) * scale), box = (int)((compact ? 20 : 25) * scale);
        int least = TextRenderer.MeasureText("Ag", _list.Font).Height + (int)(2 * scale); // a bigger font: taller rows
        if (box < least) { pitch += least - box; box = least; }
        _rowGap = pitch - box;
        _list.RowHeight = pitch;
        // Explorer's empty space, where a press draws a selection rectangle: 14 before the rows (18 at 125%) and the gaps.
        _list.RowGapTop = _rowGap / 2;
        _list.RowGapBottom = _rowGap - _rowGap / 2;
        _list.RowMargin = LogicalToDeviceUnits(14);
        _list.Invalidate();
    }

    int _rowGap;

    /// <summary>Rows are drawn cell by cell (OnDrawSubItem), each with its part of the row's background.</summary>
    void OnDrawItem(object? sender, DrawListViewItemEventArgs e) => e.DrawDefault = _list.View != View.Details;

    /// <summary>
    /// A cell's part of its row: the background, and for a selected row its part of the row's box, as in Explorer (a
    /// 1px border spanning the columns after the margin, and a gap to the next row) so selected neighbours stay apart.
    /// Each cell paints only itself: Windows redraws single cells (on hover, when a matching line arrives), and a cell
    /// painting its whole row wiped the others. Painted over what the list draws for a selected row itself.
    /// </summary>
    void PaintRowPart(Graphics g, int index, Rectangle row, Rectangle cell, bool selected)
    {
        var state = g.Save();
        try
        {
            g.SetClip(cell, System.Drawing.Drawing2D.CombineMode.Intersect);
            using (var bg = new SolidBrush(_list.BackColor)) g.FillRectangle(bg, cell);
            if (!selected) return;
            bool focusedRow = _list.Focused && _list.FocusCuesShown && _list.FocusedItem?.Index == index;
            var (back, border, _) = SelectionColors(_list.Focused, focusedRow);
            var box = new Rectangle(row.Left + _list.RowMargin, row.Top + _list.RowGapTop, Math.Max(1, row.Width - _list.RowMargin),
                Math.Max(1, row.Height - _list.RowGapTop - _list.RowGapBottom));
            using (var fill = new SolidBrush(back)) g.FillRectangle(fill, box);
            using (var pen = new Pen(border)) g.DrawRectangle(pen, box.X, box.Y, box.Width - 1, box.Height - 1);
        }
        finally { g.Restore(state); }
    }

    /// <summary>Light mode: native themed header. Dark mode: WinForms skips header text coloring for owner-drawn lists, so draw it.</summary>
    void OnDrawColumnHeader(object? sender, DrawListViewColumnHeaderEventArgs e)
    {
        if (!Application.IsDarkModeEnabled)
        {
            e.DrawDefault = true;
            return;
        }
        var g = e.Graphics;
        var r = e.Bounds;
        using (var bg = new SolidBrush(Color.FromArgb(0x2B, 0x2B, 0x2B))) g.FillRectangle(bg, r);
        using (var pen = new Pen(Color.FromArgb(0x4A, 0x4A, 0x4A)))
        {
            g.DrawLine(pen, r.Right - 1, r.Top + 3, r.Right - 1, r.Bottom - 4);
            g.DrawLine(pen, r.Left, r.Bottom - 1, r.Right, r.Bottom - 1);
        }
        int pad = LogicalToDeviceUnits(6);
        var flags = BaseTextFlags | TextFormatFlags.EndEllipsis
                    | (e.Header?.TextAlign == HorizontalAlignment.Right ? TextFormatFlags.Right : TextFormatFlags.Left);
        int indent = e.Header?.DisplayIndex == 0 ? _list.RowMargin : 0; // the leftmost title starts after the rows' margin
        var textRect = Rectangle.FromLTRB(r.Left + pad + indent, r.Top, r.Right - pad, r.Bottom);
        TextRenderer.DrawText(g, e.Header?.Text ?? "", _list.Font, textRect, Color.FromArgb(0xDE, 0xDE, 0xDE), flags);
        if (e.Header?.Tag is ColumnDef def && def.Sortable && def.Sort == _sort)
        {
            // Explorer-style chevron centred at the top of the sorted column.
            int cx = r.Left + r.Width / 2, h = LogicalToDeviceUnits(3), w = LogicalToDeviceUnits(4), top = r.Top + LogicalToDeviceUnits(1);
            Point[] pts = _desc
                ? [new(cx - w, top), new(cx, top + h), new(cx + w, top)]
                : [new(cx - w, top + h), new(cx, top), new(cx + w, top + h)];
            using var arrow = new Pen(Color.FromArgb(0xB0, 0xB0, 0xB0));
            g.DrawLines(arrow, pts);
        }
    }

    void OnDrawSubItem(object? sender, DrawListViewSubItemEventArgs e)
    {
        if (e.ColumnIndex < 0 || e.ColumnIndex >= _list.Columns.Count) return;
        var def = _list.Columns[e.ColumnIndex].Tag as ColumnDef;
        var row = RowAt(e.ItemIndex);
        var g = e.Graphics;

        Rectangle cell = e.Bounds;
        var whole = _list.GetItemRect(e.ItemIndex, ItemBoundsPortion.Entire);
        if (e.ColumnIndex == 0)
        {
            // Sub-item 0 reports the whole row; compute the real cell from the display order.
            var col0 = _list.Columns[0];
            int x = whole.X;
            foreach (ColumnHeader ch in _list.Columns)
                if (ch.DisplayIndex < col0.DisplayIndex) x += ch.Width;
            cell = new Rectangle(x, e.Bounds.Y, col0.Width, e.Bounds.Height);
        }

        bool selected = e.Item?.Selected ?? false;
        PaintRowPart(g, e.ItemIndex, whole, cell, selected);
        Color fore = selected ? SelectionColors(_list.Focused, false).Fore : _list.ForeColor;
        if (StartsContentMatches(e.ItemIndex))
        {
            // Above the first result matched only inside files: those matched by name come first.
            using var line = new Pen(ExplorerTheme.Accent, Math.Max(1, LogicalToDeviceUnits(1)));
            g.DrawLine(line, cell.Left, cell.Top, cell.Right, cell.Top);
        }
        if (row is null || def is null) return;

        int pad = LogicalToDeviceUnits(4);
        // The leftmost column starts after the rows' margin, its icon 6 into the box (at 125%) as in Explorer.
        int textLeft = cell.Left == whole.Left ? cell.Left + _list.RowMargin + LogicalToDeviceUnits(5) : cell.Left + pad;
        if (e.ColumnIndex == 0)
        {
            var isz = ShellInfo.SmallIconSize;
            if (row.Icon < 0 && row.Name.Length > 0)
                row.Icon = ShellInfo.GetIconIndex(row.Ext, row.IsDir, row.IsRoot, () => row.FullPath);
            if (row.Icon >= 0 && ShellInfo.SmallImageList != IntPtr.Zero && cell.Width > isz.Width + pad)
            {
                IntPtr hdc = g.GetHdc();
                try { Native.ImageList_Draw(ShellInfo.SmallImageList, row.Icon, hdc, textLeft, cell.Top + (cell.Height - isz.Height) / 2, Native.ILD_TRANSPARENT); }
                finally { g.ReleaseHdc(hdc); }
            }
            textLeft += isz.Width + pad;
        }

        var rect = Rectangle.FromLTRB(textLeft, cell.Top, cell.Right - pad, cell.Bottom);
        if (rect.Width <= 0) return;
        if (def.Key == "content")
        {
            DrawContentCell(g, row, rect, fore, selected);
            return;
        }
        string text = row.Text(def);
        if (text.Length == 0) return;
        var font = _list.Font;
        var flags = BaseTextFlags | TextFormatFlags.EndEllipsis;
        if (def.Align == HorizontalAlignment.Right) flags |= TextFormatFlags.Right;

        IReadOnlyList<(int Start, int Length)>? hl = null;
        if (_resultQuery.Length > 0 && (def.Key == "name" || def.Key == "path"))
            hl = def.Key == "name" ? (row.NameHighlights ??= Highlights(row.Name)) : (row.PathHighlights ??= Highlights(row.Path));

        if (hl is null || hl.Count == 0 || _boldFont is null)
            TextRenderer.DrawText(g, text, font, rect, fore, flags);
        else
            DrawHighlighted(g, text, hl, rect, font, _boldFont, fore);
    }

    IReadOnlyList<(int Start, int Length)> Highlights(string text)
    {
        if (text.Length == 0) return [];
        try { return _app.SearchEngine.GetHighlights(_resultQuery, _resultOptions, text) ?? []; }
        catch { return []; }
    }

    static void DrawHighlighted(Graphics g, string text, IReadOnlyList<(int Start, int Length)> ranges, Rectangle rect, Font normal, Font bold, Color fore)
    {
        var segments = new List<(string Text, bool Bold)>();
        int pos = 0;
        foreach (var (s, l) in ranges)
        {
            int st = Math.Clamp(s, pos, text.Length);
            int en = Math.Clamp(s + l, st, text.Length);
            if (st > pos) segments.Add((text[pos..st], false));
            if (en > st) segments.Add((text[st..en], true));
            pos = Math.Max(pos, en);
        }
        if (pos < text.Length) segments.Add((text[pos..], false));

        int x = rect.Left;
        var proposed = new Size(int.MaxValue, rect.Height);
        foreach (var (seg, isBold) in segments)
        {
            var f = isBold ? bold : normal;
            int remaining = rect.Right - x;
            if (remaining <= 0) break;
            int w = TextRenderer.MeasureText(g, seg, f, proposed, BaseTextFlags).Width;
            if (w > remaining)
            {
                TextRenderer.DrawText(g, seg, f, new Rectangle(x, rect.Top, remaining, rect.Height), fore, BaseTextFlags | TextFormatFlags.EndEllipsis);
                break;
            }
            TextRenderer.DrawText(g, seg, f, new Rectangle(x, rect.Top, w + 1, rect.Height), fore, BaseTextFlags);
            x += w;
        }
    }
}
