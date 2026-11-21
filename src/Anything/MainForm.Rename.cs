using Anything.Core;

namespace Anything;

/// <summary>Renaming a search result in place, over its name in the list or under its thumbnail, as File Explorer does.</summary>
internal sealed partial class MainForm
{
    RenameBox? _renameBox;
    bool _renameHooked;

    /// <summary>
    /// Starts renaming result <paramref name="index"/> in place. False when it can't be (the name isn't on show, the
    /// row isn't a file or folder): the caller then asks with the Rename dialog.
    /// </summary>
    bool StartInPlaceRename(int index)
    {
        _renameBox?.Commit();
        if (ExplorerShown) return false;
        var row = RowAt(index);
        if (row is null || row.IsRoot || row.FullPath.Length == 0 || row.Name.Length == 0) return false;
        Control host = ThumbnailMode ? _grid : _list;
        if (!host.Visible || !host.IsHandleCreated) return false;
        HookRenameEnders();

        Rectangle area;
        if (ThumbnailMode)
        {
            _grid.EnsureVisible(index);
            area = _grid.CaptionRect(index);
        }
        else
        {
            _list.EnsureVisible(index);
            if (NameEditArea(index) is not { } listArea) return false; // the Name column isn't shown
            area = listArea;
        }
        area.Intersect(host.ClientRectangle);
        if (area.Width < LogicalToDeviceUnits(24) || area.Height <= 0) return false;

        var box = new RenameBox(index, row.FullPath, row.Name, row.IsDir)
        {
            Font = host.Font,
            BackColor = _list.BackColor,
            ForeColor = _list.ForeColor,
            TextAlign = ThumbnailMode ? HorizontalAlignment.Center : HorizontalAlignment.Left,
        };
        // As tall as the box wants for the font, in the middle of a row (at the top of a thumbnail's name).
        int top = ThumbnailMode ? area.Top : area.Top + (area.Height - box.Height) / 2;
        box.Bounds = new Rectangle(area.Left, Math.Max(0, top), area.Width, box.Height);
        box.Finished += OnRenameFinished;
        _renameBox = box;
        host.Controls.Add(box);
        box.BringToFront();
        box.Focus();
        box.SelectBaseName();
        return true;
    }

    /// <summary>
    /// Where the box goes over a row's name (client): from where its text starts to the end of the Name column, as tall
    /// as the row's box. Null when the Name column isn't shown.
    /// </summary>
    Rectangle? NameEditArea(int index)
    {
        if (NameTextArea(index) is not { } text) return null;
        // The box's own border and margin: its text then starts where the row's text was drawn.
        int inset = LogicalToDeviceUnits(3);
        return Rectangle.FromLTRB(text.Left - inset, text.Top, text.Right, text.Bottom);
    }

    /// <summary>Where a row's name may be drawn (client): after its icon, to the end of the Name column, within the row's box.</summary>
    Rectangle? NameTextArea(int index)
    {
        if (_list.Columns.Cast<ColumnHeader>().FirstOrDefault(c => c.Tag is ColumnDef { Key: "name" }) is not { } name) return null;
        var whole = _list.GetItemRect(index, ItemBoundsPortion.Entire);
        int x = whole.X;
        foreach (ColumnHeader ch in _list.Columns)
            if (ch.DisplayIndex < name.DisplayIndex) x += ch.Width;
        var cell = new Rectangle(x, whole.Y, name.Width, whole.Height);
        int pad = LogicalToDeviceUnits(4), left = ContentLeft(cell, whole);
        if (name.Index == 0) left += ShellInfo.SmallIconSize.Width + pad; // the icon is drawn in the first column (OnDrawSubItem)
        int top = whole.Top + _list.RowGapTop, bottom = Math.Max(top + 1, whole.Bottom - _list.RowGapBottom);
        var r = Rectangle.FromLTRB(left, top, cell.Right - pad, bottom);
        return r.Width > 0 ? r : null;
    }

    /// <summary>A slow second click on the only selected row: on its name, it renames.</summary>
    void OnListRenameClick(int index, Point p)
    {
        if (_list.SelectedIndices.Count != 1 || RowAt(index) is not { } row) return;
        if (_list.Columns.Cast<ColumnHeader>().FirstOrDefault(c => c.Tag is ColumnDef { Key: "name" })?.Tag is not ColumnDef def) return;
        if (NameTextArea(index) is not { } area) return;
        int width = Math.Min(area.Width, TextWidth(row, def));
        if (p.X < area.Left || p.X >= area.Left + width || p.Y < area.Top || p.Y >= area.Bottom) return;
        StartInPlaceRename(index);
    }

    /// <summary>A slow second click on the only selected thumbnail: on its name, it renames.</summary>
    void OnGridRenameClick(int index, Point p)
    {
        if (_list.SelectedIndices.Count == 1 && _grid.CaptionRect(index).Contains(p)) StartInPlaceRename(index);
    }

    /// <summary>The view moving under the box (scrolled, resized, columns resized or moved) ends the edit, keeping the name typed.</summary>
    void HookRenameEnders()
    {
        if (_renameHooked) return;
        _renameHooked = true;
        _list.Scrolling += CommitRename;
        _grid.Scrolling += CommitRename;
        _list.Resize += (_, _) => CommitRename();
        _grid.Resize += (_, _) => CommitRename();
        _list.ColumnWidthChanged += (_, _) => CommitRename();
        _list.ColumnReordered += (_, _) => CommitRename();
    }

    void CommitRename() => _renameBox?.Commit();

    /// <summary>
    /// After the results were replaced (a live index update, a new order): the box follows its item to the row that holds
    /// it now, so it never sits over another item. It stays open, keeping what is being typed; where the item can't be
    /// found, or its row is out of sight, the box stays where it was (it still renames its own item).
    /// </summary>
    void KeepRenameBoxOnItsRow()
    {
        if (_renameBox is not { } box || box.Parent is not { } host) return;
        int i = CurrentIndexOf(box);
        if (RowAt(i) is not { } row || !string.Equals(row.FullPath, box.FullPath, StringComparison.OrdinalIgnoreCase)) return;
        box.Index = i;
        Rectangle area;
        if (ThumbnailMode) area = _grid.CaptionRect(i);
        else if (NameEditArea(i) is { } listArea) area = listArea;
        else return;
        area.Intersect(host.ClientRectangle);
        if (area.Width < LogicalToDeviceUnits(24) || area.Height <= 0) return;
        int top = ThumbnailMode ? area.Top : area.Top + (area.Height - box.Height) / 2;
        box.Bounds = new Rectangle(area.Left, Math.Max(0, top), area.Width, box.Height);
    }

    void OnRenameFinished(RenameBox box, string? text, int move)
    {
        if (_renameBox != box) return;
        _renameBox = null;
        var host = box.Parent;
        // The keys go back to the results (unless the click that ended it went elsewhere), before any question below.
        if (box.ContainsFocus && host is { CanFocus: true }) host.Focus();
        box.Visible = false;
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        // After the key or focus change that ended it has run its course: a question (the extension) or the shell's
        // own prompts never come up in the middle of it.
        try { BeginInvoke(() => { box.Dispose(); FinishRename(box, text, move); }); }
        catch (InvalidOperationException) { box.Dispose(); }
    }

    /// <summary>
    /// Where the result being renamed is now: the focused row (which a refresh puts back by id) or the row the box opened
    /// on, whichever still holds its path; the row it opened on when neither does.
    /// </summary>
    int CurrentIndexOf(RenameBox box)
    {
        foreach (int i in (ReadOnlySpan<int>)[FocusedIndex(), box.Index])
            if (RowAt(i) is { } row && string.Equals(row.FullPath, box.FullPath, StringComparison.OrdinalIgnoreCase)) return i;
        return box.Index;
    }

    void FinishRename(RenameBox box, string? text, int move)
    {
        if (text is null) return; // Esc: nothing changes
        string? name = RenameRules.Normalize(text); // nothing typed: the name stays as it was
        // Tab and Shift+Tab go on to the result beside this one as it is now: a refresh while the box was open may have
        // moved it, and the rename below (and its prompts) may bring another. It is found again by its id afterwards.
        int beside = move == 0 ? -1 : RenameRules.Step(CurrentIndexOf(box), move, _ids.Length);
        int besideId = beside >= 0 ? _ids[beside] : -1;
        var besideIndex = _resultIndex;
        long besideGeneration = _resultGeneration;
        bool rename = name is not null && name != box.OldName;
        bool released = rename && ReleasePreviewOf([box.FullPath]);
        bool ok = !rename || FileOps.Rename(this, box.FullPath, name!, box.IsDirectory);
        if (!ok && released) QueuePreview(); // cancelled or failed: the file is shown again
        if (!ok || move == 0 || beside < 0) return;
        // Tab and Shift+Tab: the next or previous result is selected and renamed, as in Explorer.
        int next = beside;
        if (ReferenceEquals(besideIndex, _resultIndex) && besideGeneration == _resultGeneration)
        {
            int now = Array.IndexOf(_ids, besideId);
            if (now >= 0) next = now;
        }
        if (next >= _ids.Length || ExplorerShown) return;
        _list.SetAllSelected(false);
        _list.SetSelected(next, true);
        _list.SetFocusedIndex(next);
        if (ThumbnailMode) _grid.Invalidate();
        if (!StartInPlaceRename(next)) (ThumbnailMode ? (Control)_grid : _list).Focus();
    }
}
