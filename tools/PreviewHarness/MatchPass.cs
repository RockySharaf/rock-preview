using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using Anything.Core.Content;
using Anything.Preview;
using Anything.Preview.UI;

namespace PreviewHarness;

/// <summary>
/// The manual pass for the preview at a search match (PreviewHarness --match): writes sample files with a known word,
/// shows each in a PreviewPane with a PreviewMatch, saves a screenshot per step and logs what the pane shows. The finder
/// is a plain case-insensitive search for the word and PDF boxes come from a stub, standing in for the index.
/// </summary>
internal static class MatchPass
{
    private const string Word = "zephyr";
    private const int DeepLine = 300_000;

    private sealed record Step(string Name, Func<PreviewPane, Task> Run, Func<PreviewPane, string?> Check, int SettleMs = 900);

    public static int Run(string outDir, Action<string> log)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        string dir = Path.Combine(outDir, "match-samples");
        string shots = Path.Combine(outDir, "match-renders");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(shots);
        log("\n## Preview at the match (--match)\n");
        var sw = Stopwatch.StartNew();
        var s = Samples.Write(dir);
        log($"- samples written in {sw.ElapsedMilliseconds} ms to {dir}");

        var find = Finder(Word);
        PreviewMatch Text(string path, int line, string key = Word) =>
            new(key, find, Line: line) { TextWindow = (l, ct) => TextWindow.LoadAsync(path, l, 1 << 20, ct) };
        var steps = new List<Step>
        {
            new("txt: line 300,000 of a 21 MB file (text window)", p => p.ShowAsync(s.BigText, Text(s.BigText, DeepLine)),
                // The footer gives the window's lines, not the head's line count and size.
                p => Expect(p, "first-line=", "current=(" + DeepLine + ", ", "window=True", "footer='Shown: lines ") ?? Absent(p, "Lines:", "MB of"), 1500),
            new("txt: F3 to the next match", p => { Press(p, Keys.F3); return Task.CompletedTask; },
                p => Expect(p, "current=(" + (DeepLine + 3) + ", ")),
            new("txt: F3 again wraps round to the first match in the window", p => { Press(p, Keys.F3); return Task.CompletedTask; },
                p => Expect(p, "current=(" + DeepLine + ", ")),
            new("txt: Shift+F3 back", p => { Press(p, Keys.F3 | Keys.Shift); return Task.CompletedTask; },
                p => Expect(p, "current=(" + (DeepLine + 3) + ", ")),
            new("txt: SetMatch to a line in the first megabyte (head shown again)", p => { p.SetMatch(Text(s.BigText, 12, "zephyr-head")); return Task.CompletedTask; },
                p => Expect(p, "first-line=1 ", "current=(12, ", "window=False", "footer='Lines: ", "Shown: first ")),
            new("txt: SetMatch(null): plain text, no highlights", p => { p.SetMatch(null); return Task.CompletedTask; },
                p => Expect(p, "first-line=1 ", "current=(0, -1)")),
            new("txt: CR-only line ends, line 7", p => p.ShowAsync(s.CrOnly, Text(s.CrOnly, 7)),
                p => Expect(p, "lines=10 ", "current=(7, ")),
            // UTF-8 for 1.2 MB (a U+2028 in every 10th line), then a byte that isn't: the index reads it as ANSI.
            new("txt: with a match, decoded as the index decodes it (its line 500 is line 500)", p => p.ShowAsync(s.MixedEncoding,
                    new PreviewMatch("quasar", Finder("quasar"), Line: 500) { TextWindow = (l, ct) => TextWindow.LoadAsync(s.MixedEncoding, l, 1 << 20, ct) }),
                p => Expect(p, "current=(500, ") ?? Absent(p, "encoding=UTF-8 ")),
            new("plain: a UTF-8 log with one stray byte at 100 KB shows as UTF-8, as before", p => p.ShowAsync(s.StrayByte),
                p => Expect(p, "view=TextView", "encoding=UTF-8 ")),
            new("html: charset windows-1251 declared in <meta>", p => p.ShowAsync(s.Html, new PreviewMatch("мир", Finder("мир"))),
                p => Expect(p, "view=TextView", "current=(3, ")),
            new("docx: built-in text with the match highlighted", p => p.ShowAsync(s.Docx, new PreviewMatch(Word, find)),
                p => Expect(p, "view=DocumentView", "doc=(1, 0, ")),
            new("docx: Show original", p => { p.ToggleOriginal(); return Task.CompletedTask; }, p => Expect(p, "link='Highlight matches'"), 6000),
            new("docx: Highlight matches again", p => { p.ToggleOriginal(); return Task.CompletedTask; }, p => Expect(p, "view=DocumentView", "doc=(1, 0, ")),
            new("xlsx: the second sheet, row 150", p => p.ShowAsync(s.Xlsx, new PreviewMatch(Word, find, Sheet: "Details")),
                p => Expect(p, "sheet=Details", "table=(1, (149, 2), ")),
            new("xlsx: SetMatch without a sheet: back to the first sheet", p => { p.SetMatch(new PreviewMatch("budget", Finder("budget"))); return Task.CompletedTask; },
                p => Expect(p, "sheet=Summary", "table=(1, (1, 1), ")),
            new("pptx: slide 3 in presentation order (part slide4.xml)", p => p.ShowAsync(s.Pptx, new PreviewMatch(Word, find, Page: 3)),
                p => Expect(p, "view=DocumentView", "doc=(2, 1, ")),
            new("pptx: F3 to the match in slide 2's notes", p => { Press(p, Keys.F3); return Task.CompletedTask; }, p => Expect(p, "doc=(2, 0, ")),
            new("pdf: page 4 of 5, with boxes", p => p.ShowAsync(s.MultiPdf, PdfMatch(4)), p => Expect(p, "page-index=3", "boxes=1"), 1500),
            new("pdf: one page keeps its page source, with boxes", p => p.ShowAsync(s.SinglePdf, PdfMatch(1)), p => Expect(p, "pages=1", "boxes=1"), 1500),
            new("jpeg: banner with the keyword bold", p => p.ShowAsync(s.Jpeg, new PreviewMatch("beach", Finder("beach"), Banner: "Keywords: sunset, beach, summer 2024",
                BannerBold: [(18, 5)])), p => Expect(p, "banner='Keywords: sunset, beach, summer 2024' bold='beach'")),
            new("banner: blanks before the text don't move the bold word", p => p.ShowAsync(s.Jpeg, new PreviewMatch("invoice", Finder("invoice"),
                Banner: "  Page 4: invoice total", BannerBold: [(10, 7)])), p => Expect(p, "banner='Page 4: invoice total' bold='invoice'")),
            new("banner: a line end before the text", p => p.ShowAsync(s.Jpeg, new PreviewMatch("beach", Finder("beach"),
                Banner: "\nKeywords: sunset, beach", BannerBold: [(19, 5)])), p => Expect(p, "banner='Keywords: sunset, beach' bold='beach'")),
            new("csv: scroll to the matching cell (no line for csv)", p => p.ShowAsync(s.Csv, new PreviewMatch(Word, find)), p => Expect(p, "table=(1, (299, 2), ")),
            new("no match: docx is the plain preview again", p => p.ShowAsync(s.Docx), p => Expect(p, "result=HostedHandler")),
            new("plain: a five-page PDF at page 4, no match", async p => { await p.ShowAsync(s.MultiPdf); p.GoToPage(3); },
                p => Expect(p, "view=ImageView", "page-index=3"), 1500),
            new("plain: the 21 MB text, no match", p => p.ShowAsync(s.BigText), p => Expect(p, "view=TextView", "first-line=1 "), 1200),
            // Hostile and odd inputs: none of them may take the pane down.
            new("hostile: a finder that throws", p => p.ShowAsync(s.CrOnly, new PreviewMatch("boom", _ => throw new InvalidOperationException("finder bug"), Line: 3)),
                p => Expect(p, "view=TextView", "current=(0, -1)")),
            new("hostile: ranges out of bounds, negative and overlapping", p => p.ShowAsync(s.Docx, new PreviewMatch("odd",
                    t => [(-5, 3), (t.Length - 2, 50), (10, -4), (int.MaxValue, 1), (20, 5), (22, 5)])),
                p => Expect(p, "view=DocumentView", "doc=(3, 0, ")),
            new("hostile: page boxes that throw, a page past the end", p => p.ShowAsync(s.MultiPdf, new PreviewMatch("nobox", find, Page: 99)
                { PageBoxes = (_, _) => throw new IOException("locate failed") }), p => Expect(p, "page-index=4", "boxes=0"), 1500),
            // A broken text matrix or a tiny CropBox: boxes far outside the page, or not numbers at all. Only the two with
            // a part on the page are kept (cut to it), and painting never throws (a red X would stay for good).
            new("hostile: page boxes far outside the page, infinite or not numbers", p => p.ShowAsync(s.MultiPdf, new PreviewMatch("huge", find, Page: 1)
                {
                    PageBoxes = (_, _) => Task.FromResult<IReadOnlyList<RectangleF>>([new(-1e9f, 0.4f, 2e9f, 0.05f), new(1e30f, 1e30f, 1e30f, 1e30f),
                        new(float.NaN, 0.1f, 0.1f, 0.1f), new(0.1f, 0.1f, -0.2f, 0.1f), new(-0.5f, -0.5f, 2f, 2f), new(float.MaxValue, 0, float.MaxValue, 1),
                        new(0.2f, float.NegativeInfinity, 0.1f, float.PositiveInfinity)]),
                }),
                p => { p.Refresh(); return Expect(p, "page-index=0", "boxes=2"); }, 1500),
            new("after those boxes: a picture still paints (no red X)", p => p.ShowAsync(s.Jpeg), p => { p.Refresh(); return Expect(p, "view=ImageView") ?? Painted(p); }),
            new("pdf: the match's page can't be rendered: no boxes on the page shown", async p =>
                {
                    await p.ShowAsync(s.MultiPdf);
                    if (p.CurrentResult?.Pages is { } pages) p.CurrentResult.Pages = new FailingPages(pages, failAt: 2);
                    p.SetMatch(PdfMatch(3));
                },
                p => Expect(p, "page-index=0", "shown-page=0", "boxes=0"), 1500),
            new("hostile: no text window (the index has none)", p => p.ShowAsync(s.BigText, new PreviewMatch("nowin", find, Line: DeepLine)
                { TextWindow = (_, _) => Task.FromResult<(string Text, int FirstLine)?>(null) }), p => Expect(p, "first-line=1 ", "window=False"), 1200),
            new("load: a docx with 3,000 matches", p => p.ShowAsync(s.ManyMatches, new PreviewMatch(Word, find)),
                p => Expect(p, "doc=(3000, 0, "), 2500),
            new("load: F3 through the 3,000-match docx", p => { for (int i = 0; i < 50; i++) p.FindNextMatch(); return Task.CompletedTask; },
                p => Expect(p, "doc=(3000, 50, ")),
            new("rtf: an .rtf file with 400 matches (those on screen coloured one by one)", p => p.ShowAsync(s.Rtf, new PreviewMatch(Word, find)),
                p => Expect(p, "view=DocumentView", "doc=(400, 0, ", $"hit='{Word}'") ?? ColouredOnScreen(p, Word), 2500),
            new("rtf: scrolling down colours the matches that come on screen", p => { PageDown(p, 6); return Task.CompletedTask; },
                p => ColouredOnScreen(p, Word)),
            new("rtf: another query on the same file puts the old colours back", p => { p.SetMatch(new PreviewMatch("notes", Finder("notes"))); return Task.CompletedTask; },
                p => Expect(p, "doc=(400, 0, ", "hit='notes'") ?? NotColoured(p, Word)),
            // Word saves letters and invoices with tables; RichEdit counts table delimiters that RichTextBox.Text leaves out.
            new("rtf: tables before the matches (Word letters, invoices)", p => p.ShowAsync(s.TableRtf, new PreviewMatch(Word, find)),
                p => Expect(p, "doc=(3, 0, ", $"hit='{Word}'"), 1200),
            new("rtf: F3 after the first table", p => { Press(p, Keys.F3); return Task.CompletedTask; }, p => Expect(p, "doc=(3, 1, ", $"hit='{Word}'")),
            new("rtf: F3 after the second table", p => { Press(p, Keys.F3); return Task.CompletedTask; }, p => Expect(p, "doc=(3, 2, ", $"hit='{Word}'")),
            new("rtf: a 6 MB file at a match", p => p.ShowAsync(s.BigRtf, new PreviewMatch("budget", Finder("budget"))),
                p => Expect(p, "hit='budget'"), 4000),
            new("rtf: a new query with dense matches on the 6 MB file (under a second)", p => { p.SetMatch(new PreviewMatch("invoice", Finder("invoice"))); return Task.CompletedTask; },
                p => Expect(p, "hit='invoice'") ?? ColouredOnScreen(p, "invoice") ?? NotColoured(p, "budget") ?? Faster(DocumentView.LastHighlightMs, 1000), 1500),
            new("rtf: then a query with few matches on the 6 MB file", p => { p.SetMatch(new PreviewMatch("budge", Finder("budge"))); return Task.CompletedTask; },
                p => Expect(p, "hit='budge'") ?? NotColoured(p, "invoice") ?? Faster(DocumentView.LastHighlightMs, 1000), 1500),
        };

        // PREVIEW_MATCH_STEPS="txt,pdf" runs only the steps whose names contain one of the words.
        if (Environment.GetEnvironmentVariable("PREVIEW_MATCH_STEPS") is { Length: > 0 } only)
        {
            var words = only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            steps = steps.Where(st => words.Any(w => st.Name.Contains(w, StringComparison.OrdinalIgnoreCase))).ToList();
        }
        int errors = 0;
        // WPF (the picture decoder) makes the process DPI-aware when it loads; be so from the start, so screenshots match.
        Application.SetHighDpiMode(HighDpiMode.SystemAware);
        Application.EnableVisualStyles();
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => { errors++; log("- UI EXCEPTION: " + e.Exception); };
        var form = new Form { Text = "Anything.Preview harness — match", Width = 1000, Height = 760, StartPosition = FormStartPosition.Manual, Left = 40, Top = 40, TopMost = true };
        var pane = new PreviewPane { Dock = DockStyle.Fill };
        form.Controls.Add(pane);
        form.Shown += async (_, _) =>
        {
            try
            {
                int n = 0;
                foreach (var step in steps)
                {
                    var t = Stopwatch.StartNew();
                    await step.Run(pane);
                    await Task.Delay(step.SettleMs);
                    string? problem = step.Check(pane);
                    if (problem != null) errors++;
                    log($"- {step.Name} ({t.ElapsedMilliseconds} ms): {(problem == null ? "ok" : "**" + problem + "**")}\n  {pane.DescribeMatch()}");
                    Screenshot(form, Path.Combine(shots, $"match_{n++:00}.png"));
                }
            }
            catch (Exception ex)
            {
                errors++;
                log("- MATCH PASS EXCEPTION: " + ex);
            }
            finally
            {
                form.Close();
            }
        };
        Application.Run(form);
        pane.Dispose();
        log($"- match pass errors: {errors}; screenshots in {shots}");
        return errors;

        PreviewMatch PdfMatch(int page)
        {
            string banner = $"Page {page}: … the {Word} launch …";
            return new(Word, find, Page: page, Banner: banner, BannerBold: [(banner.IndexOf(Word, StringComparison.Ordinal), Word.Length)])
            {
            // The stub stands in for IContentIndex.LocateOnPdfPageAsync: the box around the word as Samples drew it.
                PageBoxes = (index, _) => Task.FromResult<IReadOnlyList<RectangleF>>(index == page - 1 ? [Samples.WordBox] : []),
            };
        }
    }

    /// <summary>
    /// F3 through the pane's own key handling, sent to the focused view as a message (never to another window). Shift
    /// can't be put in a message (key handling reads the keyboard), so Shift+F3 calls what the key does.
    /// </summary>
    private static void Press(PreviewPane pane, Keys key)
    {
        if ((key & Keys.Shift) != 0)
        {
            if (!pane.FindNextMatch(backwards: true)) throw new InvalidOperationException("Shift+F3 found no match");
            return;
        }
        var target = Focused(pane) ?? pane;
        var msg = Message.Create(target.Handle, 0x0100 /* WM_KEYDOWN */, (IntPtr)(key & Keys.KeyCode), IntPtr.Zero);
        if (!target.PreProcessMessage(ref msg)) throw new InvalidOperationException("F3 was not handled by the pane");
    }

    private static Control? Focused(Control c)
    {
        foreach (Control child in c.Controls)
        {
            if (child.Visible && child is TextView or DocumentView or TableView)
            {
                child.Focus();
                return child;
            }
            if (Focused(child) is { } f) return f;
        }
        return null;
    }

    private static string? Expect(PreviewPane pane, params string[] parts)
    {
        string state = pane.DescribeMatch();
        var missing = parts.Where(p => !state.Contains(p, StringComparison.Ordinal)).ToList();
        return missing.Count == 0 ? null : "expected " + string.Join(", ", missing.Select(m => "'" + m + "'"));
    }

    private static string? Absent(PreviewPane pane, params string[] parts)
    {
        string state = pane.DescribeMatch();
        var found = parts.Where(p => state.Contains(p, StringComparison.Ordinal)).ToList();
        return found.Count == 0 ? null : "not expected " + string.Join(", ", found.Select(m => "'" + m + "'"));
    }

    /// <summary>
    /// The first occurrences of a word that is no longer searched for have no back colour any more, as RichEdit itself
    /// finds and reports them (RichTextBox.Find and SelectionBackColor, not the view's own bookkeeping).
    /// </summary>
    private static string? NotColoured(PreviewPane pane, string word)
    {
        if (pane.ActiveView is not DocumentView dv) return "no document view";
        int at = 0, seen = 0, coloured = 0;
        var scroll = Point.Empty;
        SendMessage(dv.Handle, 0x0400 + 221 /* EM_GETSCROLLPOS */, IntPtr.Zero, ref scroll);
        for (; seen < 40; seen++)
        {
            at = dv.Find(word, at, RichTextBoxFinds.None);
            if (at < 0) break;
            dv.Select(at, word.Length);
            if (dv.SelectionBackColor.ToArgb() != dv.BackColor.ToArgb()) coloured++;
            at += word.Length;
        }
        dv.Select(0, 0);
        SendMessage(dv.Handle, 0x0400 + 222 /* EM_SETSCROLLPOS */, IntPtr.Zero, ref scroll);
        return seen == 0 ? $"'{word}' not found" : coloured == 0 ? null : $"{coloured} of the first {seen} '{word}' still coloured";
    }

    /// <summary>The first occurrence of the word on screen is coloured, as RichEdit itself reports it.</summary>
    private static string? ColouredOnScreen(PreviewPane pane, string word)
    {
        if (pane.ActiveView is not DocumentView dv) return "no document view";
        int top = dv.GetCharIndexFromPosition(new Point(1, 1));
        int bottom = dv.GetCharIndexFromPosition(new Point(dv.ClientSize.Width - 2, dv.ClientSize.Height - 2));
        int at = dv.Find(word, top, RichTextBoxFinds.None);
        if (at < 0 || at > bottom) return $"no '{word}' on screen";
        var scroll = Point.Empty;
        SendMessage(dv.Handle, 0x0400 + 221 /* EM_GETSCROLLPOS */, IntPtr.Zero, ref scroll);
        dv.Select(at, word.Length);
        bool coloured = dv.SelectionBackColor.ToArgb() != dv.BackColor.ToArgb();
        dv.Select(0, 0);
        SendMessage(dv.Handle, 0x0400 + 222 /* EM_SETSCROLLPOS */, IntPtr.Zero, ref scroll);
        return coloured ? null : $"'{word}' on screen (at {at}) is not coloured";
    }

    /// <summary>Pages down in the shown document as the scroll bar does (WM_VSCROLL, SB_PAGEDOWN).</summary>
    private static void PageDown(PreviewPane pane, int times)
    {
        if (pane.ActiveView is not DocumentView dv) throw new InvalidOperationException("no document view");
        for (int i = 0; i < times; i++) SendMessage(dv.Handle, 0x0115 /* WM_VSCROLL */, (IntPtr)3 /* SB_PAGEDOWN */, IntPtr.Zero);
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, ref Point lParam);

    /// <summary>The image view paints the picture, not WinForms' red X of a view whose painting once threw.</summary>
    private static string? Painted(PreviewPane pane)
    {
        if (pane.ActiveView is not ImageView iv || iv.Width < 8 || iv.Height < 8) return "no image view";
        using var shot = new Bitmap(iv.Width, iv.Height);
        iv.DrawToBitmap(shot, new Rectangle(0, 0, iv.Width, iv.Height));
        // The sample is sky blue in the middle; the red X crosses there.
        var c = shot.GetPixel(iv.Width / 2, iv.Height / 2);
        return c.B > c.R + 40 ? null : $"centre pixel {c} (red X?)";
    }

    private static string? Faster(long ms, long limit) => ms <= limit ? null : $"took {ms} ms (more than {limit} ms)";

    /// <summary>Pages of a real PDF, except one that can't be rendered (a damaged page).</summary>
    private sealed class FailingPages(IPageSource inner, int failAt) : IPageSource
    {
        public int Count => inner.Count;
        public string Unit => inner.Unit;
        public Task<Bitmap> RenderAsync(int index, Size target, CancellationToken ct) =>
            index == failAt ? throw new InvalidDataException("page can't be rendered") : inner.RenderAsync(index, target, ct);
        public void Dispose() => inner.Dispose();
    }

    /// <summary>Case-insensitive occurrences of the words (the app passes ContentHighlighter.Find instead).</summary>
    private static Func<string, IReadOnlyList<(int Start, int Length)>> Finder(params string[] words) => text =>
    {
        var found = new List<(int Start, int Length)>();
        foreach (var w in words)
            for (int i = text.IndexOf(w, StringComparison.OrdinalIgnoreCase); i >= 0; i = text.IndexOf(w, i + w.Length, StringComparison.OrdinalIgnoreCase))
                found.Add((i, w.Length));
        return found;
    };

    private static void Screenshot(Form f, string path)
    {
        try
        {
            using var bmp = new Bitmap(f.Width, f.Height);
            using (var g = Graphics.FromImage(bmp)) g.CopyFromScreen(f.Left, f.Top, 0, 0, f.Size);
            bmp.Save(path, ImageFormat.Png);
        }
        catch (Exception e) when (e is System.Runtime.InteropServices.ExternalException or ArgumentException or IOException) { }
    }

    /// <summary>The sample files, each with the word where the steps expect it.</summary>
    private sealed record Samples(string BigText, string CrOnly, string Html, string Docx, string Xlsx, string Pptx, string MultiPdf,
        string SinglePdf, string Jpeg, string Csv, string ManyMatches, string Rtf, string TableRtf, string BigRtf, string MixedEncoding,
        string StrayByte)
    {
        /// <summary>Where the PDF samples draw the word, in 0..1 of the page from the top left (72 pt in, baseline 400 pt up, 24 pt Helvetica).</summary>
        public static readonly RectangleF WordBox = new(72f / 612, (792f - 400 - 18) / 792, 76f / 612, 24f / 792);

        public static Samples Write(string dir)
        {
            string P(string name) => Path.Combine(dir, name);
            var big = new StringBuilder();
            for (int n = 1; n <= 340_000; n++)
            {
                big.Append("Line ").Append(n).Append(": ");
                if (n is 12 or DeepLine or DeepLine + 3) big.Append("the ").Append(Word).Append(" wind blows here; ");
                big.Append('x', 20 + n % 40).Append('\n');
            }
            File.WriteAllText(P("big.txt"), big.ToString(), new UTF8Encoding(false));
            var cr = new StringBuilder();
            for (int n = 1; n <= 10; n++) cr.Append("cr line ").Append(n).Append(n == 7 ? " has the " + Word : "").Append(n < 10 ? "\r" : "");
            File.WriteAllText(P("mac-cr.txt"), cr.ToString(), Encoding.ASCII);
            File.WriteAllBytes(P("cyrillic.html"), Encoding.GetEncoding(1251).GetBytes(
                "<html><head><meta charset=\"windows-1251\"><title>Тест</title></head>\n<body>\n<p>Привет, мир!</p>\n<p>Second paragraph.</p>\n</body></html>\n"));
            WriteDocx(P("report.docx"));
            WriteDocx(P("many.docx"), paragraphs: 3000, every: 1);
            var rtf = new StringBuilder(@"{\rtf1\ansi{\fonttbl{\f0 Arial;}}\f0\fs22 ");
            for (int n = 1; n <= 400; n++) rtf.Append($"Line {n}: the {Word} notes\\par ");
            File.WriteAllText(P("notes.rtf"), rtf.Append('}').ToString(), Encoding.ASCII);
            WriteXlsx(P("book.xlsx"));
            WritePptx(P("deck.pptx"));
            WritePdf(P("five-pages.pdf"), 5, wordPage: 4);
            WritePdf(P("one-page.pdf"), 1, wordPage: 1);
            using (var bmp = new Bitmap(640, 400))
            {
                using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.SkyBlue); g.FillEllipse(Brushes.Orange, 420, 40, 140, 140); g.FillRectangle(Brushes.SandyBrown, 0, 300, 640, 100); }
                bmp.Save(P("beach.jpg"), ImageFormat.Jpeg);
            }
            var csv = new StringBuilder("id,name,comment\n");
            for (int n = 1; n <= 400; n++) csv.Append(n).Append(",item ").Append(n).Append(',').Append(n == 300 ? "late " + Word + " order" : "fine").Append('\n');
            File.WriteAllText(P("orders.csv"), csv.ToString(), Encoding.ASCII);
            // Two tables as Word writes them (\trowd … \cell … \row), the word in a cell and after each table.
            string Row(string a, string b) => $@"\trowd\cellx2500\cellx5000 \intbl {a}\cell {b}\cell\row ";
            File.WriteAllText(P("invoice.rtf"), @"{\rtf1\ansi{\fonttbl{\f0 Arial;}}\f0\fs22 Dear customer,\par "
                + Row("Item", "Amount") + Row("alpha", $"the {Word} kit") + Row("gamma", "delta")
                + $@"\pard After the first table: {Word} here.\par " + Row("one", "two") + Row("three", "four")
                + $@"\pard After the second table, the {Word} total.\par }}", Encoding.ASCII);
            var bigRtf = new StringBuilder(@"{\rtf1\ansi{\fonttbl{\f0 Arial;}{\f1 Times New Roman;}}{\colortbl;\red200\green0\blue0;}\f0 ");
            for (int para = 1; bigRtf.Length < 6_000_000; para++)
                bigRtf.Append(@"{\b Paragraph ").Append(para).Append("}  The quick brown fox jumps over the lazy dog, invoice ").Append(para % 50 == 0 ? "budget" : "total")
                   .Append(@" {\cf1\f1 coloured run} and some more ordinary words to make a realistic line of text.\par ");
            File.WriteAllText(P("big.rtf"), bigRtf.Append('}').ToString(), Encoding.ASCII);
            WriteMixedEncoding(P("mixed.txt"));
            var log = new List<byte>();
            bool stray = false;
            for (int n = 1; log.Count < 200_000; n++)
            {
                if (!stray && log.Count >= 100_000)
                {
                    log.Add(0xFF); // one line written by another program
                    stray = true;
                }
                log.AddRange(Encoding.UTF8.GetBytes($"{n:D6} Grüße aus München\n"));
            }
            File.WriteAllBytes(P("app.log"), log.ToArray());
            return new Samples(P("big.txt"), P("mac-cr.txt"), P("cyrillic.html"), P("report.docx"), P("book.xlsx"), P("deck.pptx"),
                P("five-pages.pdf"), P("one-page.pdf"), P("beach.jpg"), P("orders.csv"), P("many.docx"), P("notes.rtf"), P("invoice.rtf"),
                P("big.rtf"), P("mixed.txt"), P("app.log"));
        }

        /// <summary>
        /// Valid UTF-8 for its first 1.2 MB, with a U+2028 (a line end only when decoded as UTF-8) in every 10th line and
        /// "quasar" on line 500, then ASCII lines and one byte that isn't UTF-8 at about 1.5 MB: the index (4 MB) reads it as ANSI.
        /// </summary>
        private static void WriteMixedEncoding(string path)
        {
            var sb = new StringBuilder();
            for (int n = 1; sb.Length < 1_250_000; n++)
                sb.Append($"line {n} Grüße").Append(n == 500 ? " quasar" : "").Append(n % 10 == 0 ? "\u2028tail" : "").Append(' ', 40).Append('\n');
            var bytes = new List<byte>(Encoding.UTF8.GetBytes(sb.ToString()));
            for (int k = 0; k < 6000; k++) bytes.AddRange(Encoding.ASCII.GetBytes($"after {k}" + new string(' ', 40) + "\n"));
            bytes.Add(0xFF);
            bytes.AddRange(Encoding.ASCII.GetBytes("\nend\n"));
            File.WriteAllBytes(path, bytes.ToArray());
        }

        private static void Zip(string path, params (string Name, string Content)[] entries)
        {
            if (File.Exists(path)) File.Delete(path);
            using var z = ZipFile.Open(path, ZipArchiveMode.Create);
            foreach (var (n, c) in entries)
            {
                using var w = new StreamWriter(z.CreateEntry(n).Open(), new UTF8Encoding(false));
                w.Write(c);
            }
        }

        private const string Rels = "http://schemas.openxmlformats.org/package/2006/relationships";
        private const string OfficeRel = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

        /// <summary>A document whose paragraph 90 has the word, or (every = 1) every paragraph.</summary>
        private static void WriteDocx(string path, int paragraphs = 120, int every = 0)
        {
            const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
            var body = new StringBuilder();
            body.Append("<w:p><w:pPr><w:pStyle w:val=\"Title\"/></w:pPr><w:r><w:t>Quarterly report</w:t></w:r></w:p>");
            for (int n = 1; n <= paragraphs; n++)
            {
                string text = n == 90 || every > 0 && n % every == 0 ? $"Paragraph {n}: the {Word} project starts in spring." : $"Paragraph {n}: nothing to see in this line of filler text.";
                body.Append("<w:p><w:r><w:t xml:space=\"preserve\">").Append(text).Append("</w:t></w:r></w:p>");
            }
            Zip(path,
                ("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>"),
                ("_rels/.rels", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"{Rels}\"><Relationship Id=\"rId1\" Type=\"{OfficeRel}/officeDocument\" Target=\"word/document.xml\"/></Relationships>"),
                ("word/document.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"{W}\"><w:body>{body}</w:body></w:document>"));
        }

        private static void WriteXlsx(string path)
        {
            const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
            static string Cell(string r, string text) => $"<c r=\"{r}\" t=\"inlineStr\"><is><t>{text}</t></is></c>";
            var summary = new StringBuilder("<row r=\"1\">" + Cell("A1", "Summary") + "</row><row r=\"2\">" + Cell("A2", "Total") + Cell("B2", "budget 2024") + "</row>");
            var details = new StringBuilder();
            for (int r = 1; r <= 200; r++)
                details.Append($"<row r=\"{r}\">").Append(Cell("A" + r, "Item " + r)).Append($"<c r=\"B{r}\"><v>{r * 10}</v></c>")
                    .Append(Cell("C" + r, r == 150 ? $"{Word} delivery" : "regular")).Append("</row>");
            Zip(path,
                ("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/worksheets/sheet2.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/></Types>"),
                ("_rels/.rels", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"{Rels}\"><Relationship Id=\"rId1\" Type=\"{OfficeRel}/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
                ("xl/workbook.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"{S}\" xmlns:r=\"{OfficeRel}\"><sheets><sheet name=\"Summary\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Details\" sheetId=\"2\" r:id=\"rId2\"/></sheets></workbook>"),
                ("xl/_rels/workbook.xml.rels", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"{Rels}\"><Relationship Id=\"rId1\" Type=\"{OfficeRel}/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"{OfficeRel}/worksheet\" Target=\"worksheets/sheet2.xml\"/></Relationships>"),
                ("xl/worksheets/sheet1.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"{S}\"><sheetData>{summary}</sheetData></worksheet>"),
                ("xl/worksheets/sheet2.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"{S}\"><sheetData>{details}</sheetData></worksheet>"));
        }

        private static void WritePptx(string path)
        {
            const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
            const string Pn = "http://schemas.openxmlformats.org/presentationml/2006/main";
            static string Slide(string text) =>
                $"<p:sld xmlns:p=\"{Pn}\" xmlns:a=\"{A}\"><p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>{text}</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>";
            // Presentation order: slide3.xml, slide1.xml, slide4.xml, slide2.xml (parts renamed by another tool).
            Zip(path,
                ("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/></Types>"),
                ("ppt/presentation.xml", $"<p:presentation xmlns:p=\"{Pn}\" xmlns:r=\"{OfficeRel}\"><p:sldIdLst><p:sldId id=\"256\" r:id=\"rId3\"/><p:sldId id=\"257\" r:id=\"rId1\"/><p:sldId id=\"258\" r:id=\"rId4\"/><p:sldId id=\"259\" r:id=\"rId2\"/></p:sldIdLst></p:presentation>"),
                ("ppt/_rels/presentation.xml.rels", $"<Relationships xmlns=\"{Rels}\">" + string.Concat(Enumerable.Range(1, 4).Select(i =>
                    $"<Relationship Id=\"rId{i}\" Type=\"{OfficeRel}/slide\" Target=\"slides/slide{i}.xml\"/>")) + "</Relationships>"),
                ("ppt/slides/slide3.xml", Slide("Opening: welcome everyone")),
                ("ppt/slides/slide1.xml", Slide("Agenda for today")),
                ("ppt/slides/_rels/slide1.xml.rels", $"<Relationships xmlns=\"{Rels}\"><Relationship Id=\"rId9\" Type=\"{OfficeRel}/notesSlide\" Target=\"../notesSlides/notesSlide1.xml\"/></Relationships>"),
                ("ppt/notesSlides/notesSlide1.xml", $"<p:notes xmlns:p=\"{Pn}\" xmlns:a=\"{A}\"><p:cSld><p:spTree><p:sp><p:nvSpPr><p:cNvPr id=\"2\" name=\"Notes\"/><p:cNvSpPr/><p:nvPr><p:ph type=\"body\"/></p:nvPr></p:nvSpPr><p:txBody><a:p><a:r><a:t>Speaker notes: mention {Word} early</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:notes>"),
                ("ppt/slides/slide4.xml", Slide($"The {Word} launch plan")),
                ("ppt/slides/slide2.xml", Slide("Questions")));
        }

        private static void WritePdf(string path, int pages, int wordPage)
        {
            var objs = new List<string> { "<< /Type /Catalog /Pages 2 0 R >>", "" };
            var kids = new List<string>();
            for (int i = 1; i <= pages; i++)
            {
                string content = $"BT /F1 36 Tf 72 700 Td (Page {i}) Tj ET" + (i == wordPage ? $" BT /F1 24 Tf 72 400 Td ({Word}) Tj ET" : "");
                kids.Add($"{objs.Count + 1} 0 R");
                objs.Add($"<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents {objs.Count + 2} 0 R /Resources << /Font << /F1 {2 + pages * 2 + 1} 0 R >> >> >>");
                objs.Add($"<< /Length {content.Length} >>\nstream\n{content}\nendstream");
            }
            objs[1] = $"<< /Type /Pages /Kids [{string.Join(" ", kids)}] /Count {pages} >>";
            objs.Add("<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
            var sb = new StringBuilder("%PDF-1.4\n");
            var offsets = new List<int>();
            for (int i = 0; i < objs.Count; i++)
            {
                offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
                sb.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n");
            }
            int xref = Encoding.ASCII.GetByteCount(sb.ToString());
            sb.Append($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
            foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
            sb.Append($"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
            File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
        }
    }
}
