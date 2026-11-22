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

/// <summary>Help > Search Syntax.</summary>
internal sealed class SearchSyntaxForm : Form
{
    const string SyntaxText = """
        OPERATORS
          space          AND
          |              OR
          !              NOT
          < >            Grouping
          " "            Search for an exact phrase (keeps spaces)

        WILDCARDS
          *              Matches zero or more characters
          ?              Matches one character
          (a wildcard term must match the whole file name; use path: or \ to match full paths)

        MODIFIERS (prefix a term)
          case:  nocase:             Match case / ignore case
          ww:  wholeword:  noww:     Match whole words only
          path:  nopath:             Match the full path and name / name only
          diacritics:  nodiacritics: Match accent marks
          regex:  noregex:           Treat the term as a regular expression
          file:  folder:             Match files only / folders only

        FUNCTIONS
          ext:<list>                 Extension, semicolon separated (ext:jpg;png)
          size:<size>                File size, e.g. size:>1mb  size:1kb..10kb  size:empty|tiny|small|medium|large|huge|gigantic
          dm:<date>  dc:<date>       Date modified / created, e.g. dm:today  dm:2024  dm:>=2024-01-01  dc:lastweek
          attrib:<RHSDA...>          Attributes, e.g. attrib:H
          parent:<path>              Items directly inside a folder
          depth:<n>  len:<n>         Folder depth / name length
          type:<text>                Shell file type name

        COMPARISONS (for size:, dm:, dc:, depth:, len:)
          =  <  <=  >  >=  start..end

        FILTER MACROS
          audio:  zip:  doc:  exe:  pic:  video:

        SEARCH INSIDE FILES (Settings › Indexing › Search inside files)
          plain words                With Search › Match inside files on, a word of 3 or more letters also
                                     finds files with it inside; those matched by name come first
                                     (Chinese, Japanese, Korean: 2 characters; use content: for longer);
                                     with Match Path or Regex on, words match names only (use content:)
          content:<word>             A word inside the file, starting like this: content:invoice
          ww:content:report          Exactly that word
          content:"annual report"    Words next to each other
          content:INV-2024           Codes: the separators don't matter
          content:inv*24             A wildcard within one word (regular expressions can't be used)
          case:content:Café          Exact case; diacritics:content:café for exact accents
          content:                   Files whose words are searchable
                                     (utf8content:, ansicontent: and Everything's other content: names mean the same)
          title:  subject:  comment: (description:)  author: (authors:)  keywords: (keyword:, tags:, tag:)
                                     A word in that field (documents' properties, pictures; an email's subject
                                     is subject:, its sender author:)
          from: (sender:)  to: (recipients:)
                                     An email's sender / its recipients (To and Cc)
          pagecount:<n>              Pages, slides or sheets, e.g. pagecount:>20 (also page-count:)
          contentstatus:<status>     How reading went: ok, truncated, notext, cloud, optedout, efs, password,
                                     toobig, damaged, unsupported, denied, timeout, crashed; failed = any problem

        PICTURES' DETAILS (with "Also search inside documents and pictures' details")
          width:  height:            Size in pixels as shown, e.g. width:>=3000 (also wide:, high:)
          dimensions:<w>x<h>         e.g. dimensions:2480x3508  dimensions:>=1920x1080 (also dimension:)
          orientation:<o>            landscape, portrait or square
          dpi:<n>                    Resolution (the lower of the two), e.g. dpi:<300  dpi:72..150
          horizontalresolution:<n>  verticalresolution:<n>
                                     Resolution across / down, in dpi
          colormode:<mode>           rgb, cmyk, gray, lab, indexed, bitmap, multichannel, duotone;
                                     cmyk: and rgb: for short (cmyk: logo)
          colorspace:<space>         The EXIF colour space: srgb or uncalibrated (also colorrepresentation:)
          bitdepth:<n>               Bits per pixel (also bitsperpixel:, bpp:)
          rating:<n>                 Stars, 0-5
          datetaken:<date>           Like dm:, e.g. datetaken:2023  date-taken:lastweek
          camera:  cameramake: (cameramaker:)  cameramodel:   e.g. camera:canon
          gps:  !gps:                With / without a place

          Names with a dash work too (date-taken:). These function names are new: to find a file whose
          name contains one, put it in quotes, e.g. "title:draft" or "camera:old.jpg".

        EXAMPLES
          report pdf                 names containing "report" and "pdf"
          *.mp3 | *.flac             mp3 or flac files
          ext:jpg;png size:>2mb      large images
          "my documents" !temp       phrase, excluding "temp"
          c:\projects\ .cs            .cs files under c:\projects
          dm:today folder:           folders modified today
          content:invoice ext:pdf    PDFs with "invoice" inside
          dpi:<300 cmyk:             CMYK pictures under 300 dpi
          tags:beach datetaken:2024  pictures tagged "beach", taken in 2024

        Search menu toggles (Ctrl+I case, Ctrl+B whole word, Ctrl+U path, Ctrl+M diacritics, Ctrl+R regex,
        Match inside files) apply to the whole search; modifiers override them per term.

        KEYS
          Alt+Down                   In the search box: earlier
                                     searches (Delete forgets the
                                     highlighted one)
          F2                         Rename; one search result is
                                     renamed in place (Enter keeps the
                                     name, Esc leaves it, Tab goes on
                                     to the next item, F2 again selects
                                     all, then the extension)
          Delete  Shift+Delete       To the Recycle Bin / delete for
                                     good (Windows asks as the Recycle
                                     Bin says)
          Ctrl+Shift+C               Copy as path: each selected item's
                                     full path in quotes, one per line
          Ctrl+Z  Ctrl+Y             Undo / redo the last file
                                     operation (in the search box,
                                     Ctrl+Z undoes typing)
          F4  Alt+D  Ctrl+L          Type an address; F4 or Alt+Down
                                     while typing lists the addresses
                                     typed before
          Alt+Left  Alt+Right  Alt+Up
                                     Back / Forward / Up
          Ctrl+N                     A new window on the current folder
          Ctrl+Shift+N               New folder
          Ctrl+Shift+1 .. 8          Folder layout: extra large, large,
                                     medium, small icons, list,
                                     details, tiles, content; in search
                                     results 1, 2, 3 are thumbnail
                                     sizes and 6 is details
        """;

    const int LogicalHeight = 600;
    const int EM_GETMARGINS = 0xD4, EM_GETFIRSTVISIBLELINE = 0xCE, EM_LINESCROLL = 0xB6;
    readonly TextBox _box;
    readonly IWin32Window _owner;

    SearchSyntaxForm(IWin32Window owner)
    {
        _owner = owner;
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Search Syntax - Rock Preview";
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(700, LogicalHeight); // until OnLoad sizes it to the text
        MinimizeBox = false;
        _box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical, // FitToText adds the horizontal one when the window can't fit the text
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9.5f),
            Text = SyntaxText.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n"),
            BorderStyle = BorderStyle.None,
        };
        Controls.Add(_box);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Shown += (_, _) => _box.Select(0, 0);
    }

    /// <summary>
    /// Sized from the text itself: no fixed width is right for every scaling, and the size set in the constructor is
    /// never auto-scaled (nothing resumes a suspended layout there), so it stayed 700 pixels wide at every scaling, while
    /// the longest line is about 960 pixels at 100%. Done here because the handle exists, so the window's DPI and its
    /// frame are known, and before Form.OnLoad centres it on its owner with that size.
    /// </summary>
    protected override void OnLoad(EventArgs e)
    {
        FitToText(Screen.FromHandle(_owner.Handle), LogicalToDeviceUnits(LogicalHeight));
        base.OnLoad(e);
    }

    /// <summary>
    /// Moved to a screen with another scaling, WinForms scales the window in proportion, but the font's width doesn't
    /// grow in proportion (Courier New is 8, 10, 11, 14 and 16 pixels a character at 100 to 200%): opened at 150% and
    /// moved to 125%, the longest line was cut off again. So it is sized from the text again, where it now is; the height,
    /// already scaled, stays as it is.
    /// </summary>
    protected override void OnDpiChanged(DpiChangedEventArgs e)
    {
        base.OnDpiChanged(e);
        if (!e.Cancel) FitToText(Screen.FromHandle(Handle), ClientSize.Height);
    }

    void FitToText(Screen screen, int height)
    {
        const TextFormatFlags flags = TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        int frameWidth = Width - ClientSize.Width, frameHeight = Height - ClientSize.Height; // read before anything changes
        int text = 0;
        foreach (var line in _box.Lines) text = Math.Max(text, TextRenderer.MeasureText(line, _box.Font, Size.Empty, flags).Width);
        long margins = (long)Interop.Native.SendMessage(_box.Handle, EM_GETMARGINS, IntPtr.Zero, IntPtr.Zero);
        int chrome = _box.Width - _box.ClientSize.Width                  // the vertical scroll bar
                   + (int)(margins & 0xFFFF) + (int)((margins >> 16) & 0xFFFF) // the box's margins either side of the text
                   + LogicalToDeviceUnits(12);                           // and the caret and a little air after the last character
        var area = screen.WorkingArea;
        MinimumSize = Size.Empty; // a minimum from the screen before must not hold the new size back
        var (w, h) = Core.TextWindowLayout.ClientSize(text, chrome, height, frameWidth, frameHeight, area.Width, area.Height);
        // Only a normal window is resized. A maximised one stays as it is: Windows keeps the size to restore it to, and
        // the minimum below, worked out from the fitted size, still holds when it is restored.
        bool normal = WindowState == FormWindowState.Normal;
        if (normal) ClientSize = new Size(w, h);
        // A horizontal scroll bar only when the box is too narrow for the longest line; otherwise it is a dead grey
        // strip. Set only when it changes, because setting it makes the box anew, back at the top with nothing selected:
        // the line at the top and the selection are put back, so a window moved to another screen keeps the reader's place.
        bool across = Core.TextWindowLayout.NeedsHorizontalScroll(text, chrome, normal ? w : ClientSize.Width);
        var bars = across ? ScrollBars.Both : ScrollBars.Vertical;
        if (_box.ScrollBars != bars)
        {
            int top = (int)Interop.Native.SendMessage(_box.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);
            int start = _box.SelectionStart, length = _box.SelectionLength;
            _box.ScrollBars = bars;
            _box.Select(start, length);
            int now = (int)Interop.Native.SendMessage(_box.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);
            if (top != now) Interop.Native.SendMessage(_box.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)(top - now));
        }
        // Without the horizontal scroll bar nothing would reach the end of a line cut off by a narrower window, so it can't
        // be made narrower than the text; it may still be made lower. The minimum is the fitted width, never the present
        // one, which for a maximised window is the whole screen.
        MinimumSize = Core.TextWindowLayout.NeedsHorizontalScroll(text, chrome, w)
            ? Size.Empty : new Size(w + frameWidth, LogicalToDeviceUnits(200));
    }

    public static void ShowSyntax(IWin32Window owner)
    {
        using var f = new SearchSyntaxForm(owner);
        f.ShowDialog(owner);
    }
}
