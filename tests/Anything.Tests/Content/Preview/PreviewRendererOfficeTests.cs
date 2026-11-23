using System.Drawing;
using System.Text;
using Anything.Preview;
using Anything.Tests.Content.Readers;

namespace Anything.Tests.Content.Preview;

/// <summary>
/// What <see cref="PreviewRenderer.RenderAsync(string, Size, PreviewOptions, CancellationToken)"/> shows for Office
/// files when no shell previewer may be used: an RTF file under a Word name, text and HTML under Office names, and the
/// header of the built-in readers.
/// </summary>
public sealed class PreviewRendererOfficeTests : IDisposable
{
    readonly TempFolder _dir = new();

    public void Dispose() => _dir.Dispose();

    static PreviewResult Render(string path, bool match = false)
    {
        var options = new PreviewOptions { AllowHostedHandler = false, PreferBuiltInText = match };
        return PreviewRenderer.RenderAsync(path, new Size(400, 300), options, CancellationToken.None).GetAwaiter().GetResult();
    }

    const string Rtf = "{\\rtf1\\ansi\\deff0{\\fonttbl{\\f0 Arial;}}\\f0\\fs20 Quarterly \\b figures\\b0 for the board.\\par}";

    [Theory]
    [InlineData("minutes.doc", false)]
    [InlineData("minutes.doc", true)]
    [InlineData("letter.dot", false)]
    [InlineData("letter.dot", true)]
    public void An_rtf_file_with_a_word_name_takes_the_rtf_view(string name, bool match)
    {
        string path = _dir.File(name, Encoding.ASCII.GetBytes(Rtf));
        using var r = Render(path, match);
        Assert.Equal(PreviewKind.Document, r.Kind);
        Assert.Equal("RTF", r.Provider);
        Assert.Equal(Rtf, r.Rtf);
        Assert.Null(r.Text);
    }

    [Fact]
    public void A_real_rtf_file_takes_the_same_view()
    {
        using var r = Render(_dir.File("minutes.rtf", Encoding.ASCII.GetBytes(Rtf)));
        Assert.Equal(PreviewKind.Document, r.Kind);
        Assert.Equal("RTF", r.Provider);
    }

    [Fact]
    public void A_plain_text_file_with_a_word_name_stays_text()
    {
        using var r = Render(_dir.File("notes.doc", Encoding.ASCII.GetBytes("Shopping list\r\nPaint, brushes and tape\r\n")));
        Assert.Equal(PreviewKind.Text, r.Kind);
        Assert.Equal("Text", r.Provider);
        Assert.Contains("Paint, brushes and tape", r.Text);
    }

    [Fact]
    public void An_html_file_with_an_excel_name_stays_text()
    {
        const string html = "<html><body><table><tr><td>Total</td><td>42</td></tr></table></body></html>";
        using var r = Render(_dir.File("report.xls", Encoding.ASCII.GetBytes(html)));
        Assert.Equal(PreviewKind.Text, r.Kind);
        Assert.Equal("Text", r.Provider);
        Assert.Contains("<td>Total</td>", r.Text);
    }

    [Theory]
    [InlineData("report.docx")]
    [InlineData("report.odt")]
    public void The_new_office_and_opendocument_readers_say_built_in_reader(string name)
    {
        byte[] bytes = name.EndsWith(".docx", StringComparison.Ordinal) ? OfficeFixtures.Docx() : OfficeFixtures.Odt();
        using var r = Render(_dir.File(name, bytes));
        Assert.Equal(PreviewKind.Document, r.Kind);
        Assert.Equal("built-in reader", r.Provider);
    }
}
