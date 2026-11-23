using System.Text;

namespace Anything.Core.Search;

/// <summary>What a double-click on a part of the help does with the search box.</summary>
public enum HelpInsert
{
    /// <summary>Nothing: an operator, a key, a sign that means nothing alone.</summary>
    None,
    /// <summary>A name (ext:, content:...) typed in where the caret is.</summary>
    AtCaret,
    /// <summary>A whole search: it takes the place of what is in the box.</summary>
    Replace,
}

/// <summary>
/// One thing the help explains: what to type (one or more names, drawn as chips), what it does, and examples.
/// </summary>
/// <param name="Code">What to type. Several when they belong together (case: nocase:).</param>
/// <param name="Meaning">What it does, in plain words. One sentence or a few; the window wraps it.</param>
/// <param name="Examples">Whole searches to try, drawn as chips under the meaning.</param>
/// <param name="Insert">What a double-click on <paramref name="Code"/> does. Examples always replace the search.</param>
public sealed record HelpEntry(string[] Code, string Meaning, string[] Examples, HelpInsert Insert)
{
    /// <summary>The entry as one line of text: what Copy puts on the clipboard and what a screen reader says.</summary>
    public string PlainText()
    {
        var sb = new StringBuilder();
        sb.Append(string.Join("  ", Code));
        // Lined up like the old help's columns, so the copy reads well in Notepad; a long code just gets two spaces.
        sb.Append(' ', Math.Max(2, SearchHelp.PlainCodeColumn - sb.Length));
        sb.Append(Meaning);
        if (Examples.Length > 0) sb.Append(Examples.Length == 1 ? "  Example: " : "  Examples: ").Append(string.Join("  ", Examples));
        return sb.ToString();
    }
}

/// <param name="Subtitle">A line under the heading (what has to be on for these to work), or null.</param>
/// <param name="Note">Small print under the section's card, or null.</param>
public sealed record HelpSection(string Title, string? Subtitle, HelpEntry[] Entries, string? Note);

/// <summary>
/// The help's Search syntax part (F1), as data: the window draws it, Copy and screen readers use <see cref="PlainText"/>,
/// so what is copied is exactly what is shown. Every line of the plain-text help of 1.7.28 is here (the tests pin its
/// names; its keys moved to the help's Keys part, see <see cref="HelpContent"/>); a few cryptic lines are said more
/// plainly, and the four names the README already shows (da:, rc:, dr:, runcount:) were added.
/// </summary>
public static class SearchHelp
{
    public const string Title = "Search syntax";

    /// <summary>Under the title: how to use the help itself.</summary>
    public const string Intro =
        "Type these in the search box. Double-click a name or an example to try it.";

    /// <summary>Where the meanings start in <see cref="PlainText"/>, in characters, as in the old help.</summary>
    internal const int PlainCodeColumn = 29;

    static HelpEntry E(string code, string meaning, params string[] examples) => new([code], meaning, examples, HelpInsert.AtCaret);
    static HelpEntry E(string[] code, string meaning, params string[] examples) => new(code, meaning, examples, HelpInsert.AtCaret);
    // Operators, signs and keys: there is nothing sensible to put in the box from them alone.
    static HelpEntry Sign(string code, string meaning, params string[] examples) => new([code], meaning, examples, HelpInsert.None);
    static HelpEntry Sign(string[] code, string meaning, params string[] examples) => new(code, meaning, examples, HelpInsert.None);
    static HelpEntry Example(string search, string meaning) => new([search], meaning, [], HelpInsert.Replace);

    public static IReadOnlyList<HelpSection> Sections { get; } =
    [
        new("Operators", null,
        [
            Sign("space", "AND: every term must match.", "report pdf"),
            Sign("|", "OR: either term may match.", "*.mp3 | *.flac"),
            Sign("!", "NOT: leaves out what matches.", "!temp"),
            Sign("< >", "Group terms, like brackets.", "<report | invoice> 2024"),
            Sign("\" \"", "Search for an exact phrase (keeps spaces).", "\"my documents\""),
        ], null),

        new("Wildcards", null,
        [
            Sign("*", "Matches zero or more characters.", "*.mp3"),
            Sign("?", "Matches one character.", "IMG_????.jpg"),
        ], "A term with a wildcard must match the whole file name; use path: or a \\ in the term to match full paths."),

        new("Modifiers", "Put one in front of a term.",
        [
            E(["case:", "nocase:"], "Match case / ignore case."),
            E(["ww:", "wholeword:", "noww:"], "Match whole words only; noww: turns it off."),
            E(["path:", "nopath:"], "Match the full path and name / the name only."),
            E(["diacritics:", "nodiacritics:"], "Match accent marks / ignore them."),
            E(["regex:", "noregex:"], "Treat the term as a regular expression / as plain text."),
            E(["file:", "folder:"], "Match files only / folders only."),
        ], "The Search menu's switches (Ctrl+I case, Ctrl+B whole word, Ctrl+U path, Ctrl+M diacritics, Ctrl+R regex, " +
           "Match inside files) apply to the whole search; a modifier overrides them for one term."),

        new("Functions", null,
        [
            E("ext:", "Extension; several separated by semicolons.", "ext:jpg;png"),
            E("size:", "File size. Named sizes: empty, tiny, small, medium, large, huge, gigantic.", "size:>1mb", "size:1kb..10kb", "size:huge"),
            E(["dm:", "dc:"], "Date modified / date created.", "dm:today", "dm:2024", "dm:>=2024-01-01", "dc:lastweek"),
            E("da:", "Date accessed: when the file was last opened.", "da:yesterday"),
            E("rc:", "Date recently changed: when the app saw the item created, changed, moved or renamed.", "rc:today"),
            E("dr:", "Date run: when you last opened the file from the results.", "dr:thisweek"),
            E("runcount:", "How often you opened the file from the results (Enter or double-click).", "runcount:>2"),
            E("attrib:", "Attributes: R read-only, H hidden, S system, D folder, A archive, and others.", "attrib:H"),
            E("parent:", "Items directly inside a folder, not in its subfolders.", "parent:\"D:\\Photos\""),
            E(["depth:", "len:"], "How many folders deep the item is / how many characters its name has.", "depth:2", "len:>40"),
            E("type:", "The file type as Windows names it.", "type:\"Text Document\""),
        ], null),

        new("Comparisons", "For size:, dm:, dc:, da:, rc:, dr:, runcount:, depth:, len: and the other numbers and dates.",
        [
            Sign(["=", "<", "<=", ">", ">=", "start..end"],
                "Equal to, less than, at most, more than, at least, from start to end.", "size:>=1gb", "dm:2024-01-01..2024-03-31"),
        ], null),

        new("Kinds of file", "Alone they show only that kind; before a word they look for it in that kind only.",
        [
            E("audio:", "Music and other sound files (.mp3, .flac, .wav...)."),
            E("zip:", "Archives (.zip, .rar, .7z...)."),
            E("doc:", "Documents: text, PDF, Office, web pages and source code."),
            E("exe:", "Programs and scripts (.exe, .msi, .bat...)."),
            E("pic:", "Pictures (.jpg, .png, .gif, .psd...)."),
            E("video:", "Videos (.mp4, .mkv, .mov...)."),
        ], null),

        new("Search inside files", "Turn it on in Settings › Indexing › Search inside files.",
        [
            Sign("plain words", "With Search › Match inside files on, a word of 3 or more letters also finds files with it " +
                 "inside; those matched by name come first. Chinese, Japanese, Korean: 2 characters (use content: for longer). " +
                 "With Match Path or Regex on, words match names only (use content:)."),
            E("content:", "A word inside the file, starting like this.", "content:invoice"),
            E("ww:content:report", "Exactly that word."),
            E("content:\"annual report\"", "Words next to each other."),
            E("content:INV-2024", "Codes: the separators don't matter."),
            E("content:inv*24", "A wildcard within one word (regular expressions can't be used)."),
            E("case:content:Café", "Exact case; use diacritics: for exact accents.", "diacritics:content:café"),
            new(["content:"], "On its own: files whose text has been read and can be searched. utf8content:, ansicontent: " +
                "and Everything's other content: names mean the same.", [], HelpInsert.AtCaret),
            E("title:", "A word in the title (documents' properties, pictures).", "title:draft"),
            E("subject:", "A word in the subject; an email's subject too."),
            E("comment:", "A word in the comments (also description:)."),
            E("author:", "A word in the author (also authors:); for an email, its sender."),
            E("keywords:", "A word in the tags (also keyword:, tags:, tag:).", "tags:beach"),
            E(["from:", "to:"], "An email's sender / its recipients, To and Cc (also sender:, recipients:)."),
            E("pagecount:", "Pages, slides or sheets (also page-count:).", "pagecount:>20"),
            E("contentstatus:", "Why a file's text could or couldn't be read: ok, truncated, notext, cloud, optedout, efs, " +
              "password, toobig, damaged, unsupported, denied, timeout, crashed; failed means any problem.", "contentstatus:failed"),
        ], null),

        new("Pictures' details", "With \"Also search inside documents and pictures' details\" on.",
        [
            E(["width:", "height:"], "Size in pixels as shown (also wide:, high:).", "width:>=3000"),
            E("dimensions:", "Width x height in pixels (also dimension:).", "dimensions:2480x3508", "dimensions:>=1920x1080"),
            E("orientation:", "landscape, portrait or square.", "orientation:portrait"),
            E("dpi:", "Resolution (the lower of the two).", "dpi:<300", "dpi:72..150"),
            E(["horizontalresolution:", "verticalresolution:"], "Resolution across / down, in dpi."),
            E("colormode:", "rgb, cmyk, gray, lab, indexed, bitmap, multichannel, duotone (cmyk: and rgb: for short).", "colormode:gray", "cmyk: logo"),
            E("colorspace:", "The EXIF colour space: srgb or uncalibrated (also colorrepresentation:).", "colorspace:srgb"),
            E("bitdepth:", "Bits per pixel (also bitsperpixel:, bpp:)."),
            E("rating:", "Stars, 0-5."),
            E("datetaken:", "The date taken (written as for dm:).", "datetaken:2023", "date-taken:lastweek"),
            E(["camera:", "cameramake:", "cameramodel:"], "The camera (also cameramaker:).", "camera:canon"),
            E(["gps:", "!gps:"], "With / without a place."),
        ], "Names with a dash work too (date-taken:). These function names are new: to find a file whose name contains " +
           "one, put it in quotes, e.g. \"title:draft\" or \"camera:old.jpg\"."),

        new("Examples", null,
        [
            Example("report pdf", "Names containing \"report\" and \"pdf\"."),
            Example("*.mp3 | *.flac", "mp3 or flac files."),
            Example("ext:jpg;png size:>2mb", "Large images."),
            Example("\"my documents\" !temp", "The phrase, leaving out \"temp\"."),
            Example("c:\\projects\\ ext:cs", ".cs files under c:\\projects."),
            Example("dm:today folder:", "Folders modified today."),
            Example("content:invoice ext:pdf", "PDFs with \"invoice\" inside."),
            Example("dpi:<300 cmyk:", "CMYK pictures under 300 dpi."),
            Example("tags:beach datetaken:2024", "Pictures tagged \"beach\", taken in 2024."),
        ], null),
    ];

    /// <summary>The part of the help these sections make, under its heading and intro.</summary>
    public static HelpPart Part { get; } = new(Title, Intro, Sections, HelpLabel.Chips);

    /// <summary>
    /// The search box's text and caret after a double-click on part of the help: a whole search takes the place of the
    /// box's text; a name goes in at the caret (in place of any selection), kept apart from the terms beside it by a
    /// space, with the caret right after it so its value can be typed next (ext: then jpg).
    /// </summary>
    public static (string Text, int Caret) Insert(string box, int selectionStart, int selectionLength, string text, HelpInsert how)
    {
        if (how == HelpInsert.None) return (box, Math.Clamp(selectionStart, 0, box.Length));
        if (how == HelpInsert.Replace) return (text, text.Length);
        int start = Math.Clamp(selectionStart, 0, box.Length);
        int end = Math.Clamp(start + Math.Max(0, selectionLength), start, box.Length);
        string before = box[..start], after = box[end..];
        string lead = before.Length > 0 && !char.IsWhiteSpace(before[^1]) ? " " : "";
        string trail = after.Length > 0 && !char.IsWhiteSpace(after[0]) ? " " : "";
        return (before + lead + text + trail + after, before.Length + lead.Length + text.Length);
    }

    /// <summary>A section's heading line in <see cref="PlainText"/>, with its subtitle.</summary>
    public static string PlainHeading(HelpSection s) =>
        s.Subtitle is null ? s.Title.ToUpperInvariant() : $"{s.Title.ToUpperInvariant()} ({s.Subtitle})";

    /// <summary>This part as plain text (Windows line ends); <see cref="HelpContent.PlainText"/> gives the whole help.</summary>
    public static string PlainText() => Part.PlainText();
}
