namespace Anything;

/// <summary>Base for small code-built dialogs: DPI auto-scaling, OK/Cancel buttons in a bottom row.</summary>
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

    InputDialog(string title, string prompt, string value, int selectLength) : base(title)
    {
        SuspendLayout();
        var body = new Panel { Dock = DockStyle.Fill, Padding = new Padding(12, 12, 12, 4) };
        var label = new Label { Text = prompt, Dock = DockStyle.Top, AutoSize = true, Padding = new Padding(0, 0, 0, 4) };
        body.Controls.Add(_text);
        body.Controls.Add(label);
        _text.Text = value;
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

    public static string? Show(IWin32Window owner, string title, string prompt, string value, int selectLength)
    {
        using var d = new InputDialog(title, prompt, value, selectLength);
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

        SuspendLayout();
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
        """;

    SearchSyntaxForm()
    {
        AutoScaleDimensions = new SizeF(96F, 96F);
        AutoScaleMode = AutoScaleMode.Dpi;
        Text = "Search Syntax - Ultimate File Explorer";
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(700, 600);
        MinimizeBox = false;
        var box = new TextBox
        {
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Dock = DockStyle.Fill,
            Font = new Font(FontFamily.GenericMonospace, 9.5f),
            Text = SyntaxText.Replace("\n", "\r\n").Replace("\r\r\n", "\r\n"),
            BorderStyle = BorderStyle.None,
        };
        Controls.Add(box);
        KeyPreview = true;
        KeyDown += (_, e) => { if (e.KeyCode == Keys.Escape) Close(); };
        Shown += (_, _) => box.Select(0, 0);
    }

    public static void ShowSyntax(IWin32Window owner)
    {
        using var f = new SearchSyntaxForm();
        f.ShowDialog(owner);
    }
}
