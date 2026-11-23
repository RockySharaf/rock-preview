namespace Anything;

/// <summary>
/// Base for small code-built dialogs: DPI auto-scaling, OK/Cancel buttons in a bottom row. It leaves the layout
/// suspended: a derived constructor builds the dialog in 96-DPI units and ends with ResumeLayout(false), PerformLayout().
/// </summary>
internal class DialogBase : Form
{
    protected readonly FlowLayoutPanel Buttons = new()
    {
        Dock = DockStyle.Bottom,
        FlowDirection = FlowDirection.RightToLeft,
        AutoSize = true,
        AutoSizeMode = AutoSizeMode.GrowAndShrink,
        Padding = new Padding(8, 4, 8, 8),
    };

    protected DialogBase(string title)
    {
        // WinForms scales a form to its DPI only for AutoScale settings made while its layout is suspended, when the
        // layout resumes (as designer code does). Set before the suspend, a dialog kept its 96-DPI size in pixels.
        SuspendLayout();
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = title;
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = false;
        MinimizeBox = false;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
    }

    protected Button AddButton(string text, DialogResult result)
    {
        var b = new Button { Text = text, DialogResult = result, AutoSize = true, MinimumSize = new Size(80, 0), Margin = new Padding(6, 0, 0, 0) };
        Buttons.Controls.Add(b);
        if (result == DialogResult.OK) AcceptButton = b;
        if (result == DialogResult.Cancel) CancelButton = b;
        return b;
    }
}

/// <summary>Single-line text prompt (rename, bookmark name).</summary>
internal sealed class InputDialog : DialogBase
{
    readonly TextBox _text = new() { Dock = DockStyle.Top, Margin = new Padding(0) };

    InputDialog(string title, string prompt, string value, int selectLength, bool fileName) : base(title)
    {
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 4) };
        var label = new Label { Text = prompt, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 4) };
        body.Controls.Add(_text);
        body.Controls.Add(label);
        _text.Text = value;
        if (fileName) FileNameFilter.Attach(_text); // characters a name can't hold are refused as typed, as in Explorer
        Controls.Add(body);
        Controls.Add(Buttons);
        AddButton("Cancel", DialogResult.Cancel);
        AddButton("OK", DialogResult.OK);
        ClientSize = new Size(420, 120);
        ResumeLayout(false);
        PerformLayout();
        Shown += (_, _) =>
        {
            _text.Focus();
            _text.Select(0, Math.Clamp(selectLength, 0, _text.TextLength));
        };
    }

    public static string? Show(IWin32Window owner, string title, string prompt, string value, int selectLength, bool fileName = false)
    {
        using var d = new InputDialog(title, prompt, value, selectLength, fileName);
        return d.ShowDialog(owner) == DialogResult.OK ? d._text.Text : null;
    }
}

/// <summary>Bookmarks > Organize: rename, delete and reorder bookmarks.</summary>
internal sealed class BookmarksForm : DialogBase
{
    readonly ListBox _listBox = new() { Dock = DockStyle.Fill, IntegralHeight = false };
    readonly List<Bookmark> _items;

    public IReadOnlyList<Bookmark> Result => _items;

    public BookmarksForm(IEnumerable<Bookmark> bookmarks) : base("Organize Bookmarks")
    {
        _items = bookmarks.Select(b => new Bookmark
        {
            Name = b.Name, Query = b.Query, Filter = b.Filter, Sort = b.Sort, Descending = b.Descending,
            MatchCase = b.MatchCase, MatchWholeWord = b.MatchWholeWord, MatchPath = b.MatchPath, MatchDiacritics = b.MatchDiacritics, Regex = b.Regex,
            MatchContent = b.MatchContent,
        }).ToList();

        FormBorderStyle = FormBorderStyle.Sizable;
        MinimumSize = new Size(380, 260);
        var side = new FlowLayoutPanel { Dock = DockStyle.Right, FlowDirection = FlowDirection.TopDown, AutoSize = true, Padding = new Padding(6, 0, 0, 0) };
        Button Side(string text, Action a)
        {
            var b = new Button { Text = text, AutoSize = true, MinimumSize = new Size(96, 0), Margin = new Padding(0, 0, 0, 6) };
            b.Click += (_, _) => a();
            side.Controls.Add(b);
            return b;
        }
        Side("&Rename...", Rename);
        Side("&Delete", Delete);
        Side("Move &Up", () => MoveItem(-1));
        Side("Move Do&wn", () => MoveItem(1));
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 4) };
        body.Controls.Add(_listBox);
        body.Controls.Add(side);
        _listBox.DoubleClick += (_, _) => Rename();
        Controls.Add(body);
        Controls.Add(Buttons);
        AddButton("Cancel", DialogResult.Cancel);
        AddButton("OK", DialogResult.OK);
        ClientSize = new Size(520, 320);
        ResumeLayout(false);
        PerformLayout();
        Reload(0);
    }

    void Reload(int select)
    {
        _listBox.BeginUpdate();
        _listBox.Items.Clear();
        foreach (var b in _items) _listBox.Items.Add(string.IsNullOrEmpty(b.Query) ? b.Name : $"{b.Name}    ({b.Query})");
        _listBox.EndUpdate();
        if (_items.Count > 0) _listBox.SelectedIndex = Math.Clamp(select, 0, _items.Count - 1);
    }

    void Rename()
    {
        int i = _listBox.SelectedIndex;
        if (i < 0) return;
        var name = InputDialog.Show(this, "Rename Bookmark", "Name:", _items[i].Name, _items[i].Name.Length);
        if (string.IsNullOrWhiteSpace(name)) return;
        _items[i].Name = name.Trim();
        Reload(i);
    }

    void Delete()
    {
        int i = _listBox.SelectedIndex;
        if (i < 0) return;
        _items.RemoveAt(i);
        Reload(i);
    }

    void MoveItem(int delta)
    {
        int i = _listBox.SelectedIndex, j = i + delta;
        if (i < 0 || j < 0 || j >= _items.Count) return;
        (_items[i], _items[j]) = (_items[j], _items[i]);
        Reload(j);
    }
}
