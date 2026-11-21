using Anything.Core;

namespace Anything;

/// <summary>
/// The box File Explorer puts over an item's name to rename it in place: it starts with the name before the extension
/// selected, Enter or a click elsewhere keeps the new name, Esc leaves the old one, Tab and Shift+Tab keep it and go on
/// to the next or previous item, and F2 moves the selection (base name, all, extension). Characters a name can't hold
/// are refused as they are typed.
/// </summary>
internal sealed class RenameBox : TextBox
{
    bool _finished;

    /// <summary>The result being renamed: its place in the list (kept up to date when a refresh moves it), its path and name as they were.</summary>
    [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
    public int Index { get; set; }
    public string FullPath { get; }
    public string OldName { get; }
    public bool IsDirectory { get; }

    /// <summary>Told once when the edit ends: the name typed (null when cancelled) and where Tab goes (1, -1, or 0).</summary>
    public event Action<RenameBox, string?, int>? Finished;

    public RenameBox(int index, string fullPath, string name, bool isDirectory)
    {
        Index = index;
        FullPath = fullPath;
        OldName = name;
        IsDirectory = isDirectory;
        Text = name;
        BorderStyle = BorderStyle.FixedSingle;
        AccessibleName = "Rename"; // a screen reader says what the box is for, not a bare "edit"
        FileNameFilter.Attach(this);
    }

    /// <summary>Selects the name before its extension, as the edit starts.</summary>
    public void SelectBaseName() => Select(0, RenameRules.BaseNameLength(Text, IsDirectory));

    /// <summary>Ends the edit keeping what was typed, as a click elsewhere does (the view scrolled or changed under it).</summary>
    public void Commit() => Finish(Text, 0);

    /// <summary>
    /// The edit's own keys, taken before the window sees them: Esc here must never reach the window's Esc (which clears
    /// the search or closes the window), nor Tab its focus moves. Ctrl+Z undoes typing in the box.
    /// </summary>
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        switch (keyData)
        {
            case Keys.Enter:
                Finish(Text, 0);
                return true;
            case Keys.Escape:
                Finish(null, 0);
                return true;
            case Keys.Tab:
                Finish(Text, 1);
                return true;
            case Keys.Shift | Keys.Tab:
                Finish(Text, -1);
                return true;
            case Keys.F2:
                var (start, length) = RenameRules.NextSelection(Text, IsDirectory, SelectionStart, SelectionLength);
                Select(start, length);
                return true;
            case Keys.Control | Keys.A:
                SelectAll();
                return true;
            case Keys.Control | Keys.Z:
                Undo();
                return true;
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    protected override void OnLeave(EventArgs e)
    {
        base.OnLeave(e);
        Finish(Text, 0); // clicked elsewhere in the window: the new name is kept, as in Explorer
    }

    void Finish(string? text, int move)
    {
        if (_finished) return;
        _finished = true;
        Finished?.Invoke(this, text, move);
    }
}

/// <summary>
/// Refuses the characters a file name can't hold as they are typed or pasted into a box, with Explorer's balloon
/// saying which they are.
/// </summary>
internal sealed class FileNameFilter
{
    readonly TextBox _box;
    readonly ToolTip _tip = new() { IsBalloon = true };
    bool _fixing;

    FileNameFilter(TextBox box)
    {
        _box = box;
        box.KeyDown += OnKeyDown;
        box.KeyPress += OnKeyPress;
        box.TextChanged += OnTextChanged;
        box.Leave += (_, _) => _tip.Hide(box);
        box.Disposed += (_, _) => _tip.Dispose();
    }

    public static void Attach(TextBox box) => _ = new FileNameFilter(box);

    /// <summary>
    /// Ctrl+Backspace deletes the word before the caret, as in Explorer's box. Left to the edit control it would type
    /// DEL (0x7F) into the name.
    /// </summary>
    void OnKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.KeyData != (Keys.Control | Keys.Back) || _box.ReadOnly) return;
        e.Handled = true;
        e.SuppressKeyPress = true; // no WM_CHAR 0x7F follows
        var (text, caret) = RenameRules.DeletePreviousWord(_box.Text, _box.SelectionStart, _box.SelectionLength);
        if (text == _box.Text) return;
        _box.Text = text;
        _box.Select(caret, 0);
    }

    void OnKeyPress(object? sender, KeyPressEventArgs e)
    {
        // Control characters here are keys (Backspace, Ctrl+A), not text.
        if (char.IsControl(e.KeyChar) || !RenameRules.IsForbidden(e.KeyChar)) return;
        e.Handled = true;
        Warn();
    }

    /// <summary>Pasted text: what a name can't hold is taken out, the caret staying where it was among the rest.</summary>
    void OnTextChanged(object? sender, EventArgs e)
    {
        if (_fixing) return;
        string text = _box.Text, clean = RenameRules.RemoveForbidden(text);
        if (clean == text) return;
        int caret = _box.SelectionStart;
        int before = caret - RenameRules.RemoveForbidden(text[..Math.Min(caret, text.Length)]).Length;
        _fixing = true;
        try
        {
            _box.Text = clean;
            _box.Select(Math.Clamp(caret - before, 0, clean.Length), 0);
        }
        finally { _fixing = false; }
        Warn();
    }

    void Warn()
    {
        if (!_box.IsHandleCreated || _box.IsDisposed) return;
        _tip.Hide(_box);
        _tip.Show(RenameRules.ForbiddenMessage, _box, _box.LogicalToDeviceUnits(8), _box.Height, 5000);
    }
}
