using System.Drawing;
using System.Windows.Forms;

namespace Anything.Preview.UI;

/// <summary>
/// The colours the preview pane's own views draw text with: one set for a light window, one for a dark one. The light
/// set is what the views always used; the dark set keeps text readable on the dark window colour. A search match keeps
/// its yellow or orange in both, so text on a match stays dark.
/// </summary>
internal sealed class PreviewColors
{
    /// <summary>The views' colours as they always were.</summary>
    public static readonly PreviewColors Light = new()
    {
        Background = SystemColors.Window,
        Text = Color.FromArgb(30, 30, 30),
        GutterBack = Color.FromArgb(247, 247, 247),
        LineNumber = Color.FromArgb(150, 150, 150),
        GutterLine = Color.FromArgb(225, 225, 225),
        Selection = Color.FromArgb(204, 228, 247),
        MatchText = null,
        GridHeaderBack = Color.FromArgb(240, 240, 240),
        GridLines = Color.FromArgb(225, 225, 225),
        GridRowNumber = Color.Gray,
        GridSelectionText = null,
        DocText = Color.FromArgb(40, 40, 40),
        DocHeading = Color.FromArgb(31, 78, 121),
        DocCaption = Color.FromArgb(110, 110, 110),
        FilePageBack = null,
        FilePageText = null,
        InfoSub = Color.DimGray,
        InfoKey = Color.Gray,
        InfoDiag = Color.Firebrick,
        PaneSub = Color.DimGray,
        PaneFooter = SystemColors.GrayText,
        PaneLoading = Color.Gray,
    };

    /// <summary>For a dark window (SystemColors.Window is 50,50,50 there).</summary>
    public static readonly PreviewColors Dark = new()
    {
        Background = Color.FromArgb(50, 50, 50),
        Text = Color.FromArgb(230, 230, 230),
        GutterBack = Color.FromArgb(42, 42, 42),
        LineNumber = Color.FromArgb(155, 155, 155),
        GutterLine = Color.FromArgb(72, 72, 72),
        Selection = Color.FromArgb(38, 79, 120),
        MatchText = Color.FromArgb(30, 30, 30),
        GridHeaderBack = Color.FromArgb(60, 60, 60),
        GridLines = Color.FromArgb(78, 78, 78),
        GridRowNumber = Color.FromArgb(175, 175, 175),
        GridSelectionText = Color.White,
        DocText = Color.FromArgb(230, 230, 230),
        DocHeading = Color.FromArgb(130, 180, 230),
        DocCaption = Color.FromArgb(170, 170, 170),
        FilePageBack = Color.White,
        FilePageText = Color.Black,
        InfoSub = Color.FromArgb(175, 175, 175),
        InfoKey = Color.FromArgb(175, 175, 175),
        InfoDiag = Color.FromArgb(255, 130, 120),
        PaneSub = Color.FromArgb(175, 175, 175),
        PaneFooter = Color.FromArgb(175, 175, 175),
        PaneLoading = Color.FromArgb(175, 175, 175),
    };

    /// <summary>Every match, in both sets.</summary>
    public static readonly Color Match = Color.FromArgb(255, 236, 130);
    /// <summary>The match F3 is on.</summary>
    public static readonly Color CurrentMatch = Color.FromArgb(255, 175, 70);
    /// <summary>A selected matching cell in a table, and the selected current one.</summary>
    public static readonly Color MatchSelected = Color.FromArgb(214, 190, 70), CurrentMatchSelected = Color.FromArgb(230, 140, 40);

    /// <summary>The set for the app's colour mode now; read when painting, like the main window's colours.</summary>
    public static PreviewColors Now => For(Application.IsDarkModeEnabled);

    public static PreviewColors For(bool dark) => dark ? Dark : Light;

    /// <summary>Behind the text view's text.</summary>
    public Color Background { get; private init; }
    /// <summary>The text view's text (plain text, the hex dump).</summary>
    public Color Text { get; private init; }
    public Color GutterBack { get; private init; }
    public Color LineNumber { get; private init; }
    /// <summary>The line between the line numbers and the text.</summary>
    public Color GutterLine { get; private init; }
    public Color Selection { get; private init; }
    /// <summary>Text drawn on a match; null = each view's own text colour (light: dark text already reads on yellow).</summary>
    public Color? MatchText { get; private init; }
    public Color GridHeaderBack { get; private init; }
    public Color GridLines { get; private init; }
    public Color GridRowNumber { get; private init; }
    /// <summary>Text of a selected cell; null = the grid's own (dark mode's default is black on blue).</summary>
    public Color? GridSelectionText { get; private init; }
    /// <summary>Paragraphs, headings ("Slide 1") and captions of the documents built here.</summary>
    public Color DocText { get; private init; }
    public Color DocHeading { get; private init; }
    public Color DocCaption { get; private init; }
    /// <summary>
    /// A user's own .rtf file is shown on a white page with black text, like Office's previewers and PDF pages: its own
    /// colours (often explicit black) were written for white. Null = the view's own colours.
    /// </summary>
    public Color? FilePageBack { get; private init; }
    public Color? FilePageText { get; private init; }
    /// <summary>The info card's format line, its keys and its "Tried, without success" lines.</summary>
    public Color InfoSub { get; private init; }
    public Color InfoKey { get; private init; }
    public Color InfoDiag { get; private init; }
    /// <summary>The pane's line under the file name (and the zoom), its details line at the bottom and "Loading…".</summary>
    public Color PaneSub { get; private init; }
    public Color PaneFooter { get; private init; }
    public Color PaneLoading { get; private init; }
}
