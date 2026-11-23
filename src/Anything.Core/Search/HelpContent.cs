using System.Text;

namespace Anything.Core.Search;

/// <summary>How a part's rows name what they explain.</summary>
public enum HelpLabel
{
    /// <summary>Chips in the code face: keys, and what to type in the search box.</summary>
    Chips,
    /// <summary>Plain bold names, as the things are called on the screen ("The search box").</summary>
    Names,
}

/// <summary>
/// One part of the help (F1): a big heading, a line on how to use it, and its sections. The window jumps to a part from
/// the strip at its top; the column of what each row names is worked out per part, so one part's long names never
/// change another's column.
/// </summary>
/// <param name="Intro">The line under the heading (drawn, not copied), or null.</param>
public sealed record HelpPart(string Title, string? Intro, IReadOnlyList<HelpSection> Sections, HelpLabel Label)
{
    /// <summary>The part as plain text (Windows line ends): its heading, then each section as <see cref="SearchHelp.PlainHeading"/> and its rows.</summary>
    public string PlainText()
    {
        var sb = new StringBuilder();
        sb.Append(Title).Append("\r\n\r\n");
        foreach (var s in Sections)
        {
            sb.Append(SearchHelp.PlainHeading(s)).Append("\r\n");
            foreach (var e in s.Entries) sb.Append("  ").Append(e.PlainText()).Append("\r\n");
            if (s.Note is not null) sb.Append("  ").Append(s.Note).Append("\r\n");
            sb.Append("\r\n");
        }
        return sb.ToString().TrimEnd() + "\r\n";
    }
}

/// <summary>
/// The program's help (F1), as data: how to use Rock Preview, every key, and the search syntax (<see cref="SearchHelp"/>).
/// The window draws it; Copy and screen readers use <see cref="PlainText"/>, so what is copied is what is shown. Every
/// sentence says what the program does today; the shortcut that opens Rock Preview is the one set in Settings.
/// </summary>
public static class HelpContent
{
    /// <summary>The parts in order, as <see cref="Build"/> returns them.</summary>
    public const int HowTo = 0, Keys = 1, Syntax = 2;

    /// <summary>The strip's buttons, one per part, in order.</summary>
    public static IReadOnlyList<string> StripLabels { get; } = ["How to use", "Keys", "Search syntax"];

    /// <summary>The key set to open Rock Preview when none is known: Settings' own default.</summary>
    public const string DefaultHotkey = "Alt+X";

    /// <param name="hotkey">The shortcut that opens Rock Preview now, or null when none is working (then Alt+X, said as the usual one).</param>
    public static IReadOnlyList<HelpPart> Build(string? hotkey) => [HowToPart(hotkey), KeysPart(hotkey), SearchHelp.Part];

    /// <summary>The whole help as plain text, every part with its heading, for Copy all and screen readers.</summary>
    public static string PlainText(IReadOnlyList<HelpPart> parts) =>
        string.Join("\r\n", parts.Select(p => p.PlainText()));

    static HelpEntry Row(string name, string text) => new([name], text, [], HelpInsert.None);
    static HelpEntry Key(string key, string text) => new([key], text, [], HelpInsert.None);
    static HelpEntry Key(string[] keys, string text) => new(keys, text, [], HelpInsert.None);

    static HelpPart HowToPart(string? hotkey)
    {
        string press = hotkey is { Length: > 0 } key ? $"Press {key}" : $"Press the shortcut ({DefaultHotkey} unless you changed it)";
        return new("How to use Rock Preview",
            "Click How to use, Keys or Search syntax at the top to jump there. Right-click a row to copy it, or press Ctrl+C to copy all of this help.",
        [
            new("Opening", null,
            [
                Row("The shortcut", $"{press} in any program to open Rock Preview, ready to search everything; press it again " +
                    "while Rock Preview is in front to hide it. Change the key in Settings › Keyboard."),
                Row("The tray icon", "The icon by the clock opens Rock Preview with a click; right-click it for a new search " +
                    "window, Settings or Exit. To hide it, turn off Settings › General › Show the tray icon."),
                Row("Closing the window", "Rock Preview keeps running in the background, with your searches as you left them, " +
                    "so the shortcut brings it back at once (Settings › General › Keep running in the background). To end " +
                    "it, choose File › Exit under the gear's arrow, or press Ctrl+Q."),
            ], null),

            new("Searching", null,
            [
                Row("The search box", "Type, and the list fills as you type. Several words must all be in the name, in any " +
                    "order; the Search syntax part shows everything else you can type."),
                Row("Earlier searches", "The arrow in the search box, or Alt+Down, lists your last 20 searches; pressing Enter " +
                    "or opening a result remembers one."),
                Row("Everything / folder name", "At the start of the search box: Everything, in blue, when Rock Preview " +
                    "searches everywhere, or the open folder's name when it searches only in that folder and its subfolders. " +
                    $"Click it to switch. When a search in a folder finds nothing, click {SearchScope.EverythingInsteadLink} in the empty list."),
                Row("Everyday files", "Shown next to Thumbnails while every file in a folder shows, or search results show " +
                    "as thumbnails. On unless you turn it off, it keeps to pictures, videos, music, documents and other " +
                    "everyday files, not code or system files; click it for every file, and use its arrow to pick the kinds."),
                Row("Kind of file", "Choose Search options › Kind, or right-click the status bar, to show only audio, " +
                    "compressed files, documents, programs, folders, pictures or videos. The kind chosen shows on the Search options " +
                    "button and at the bottom right; the \u00D7 beside the button shows every kind again."),
                Row("Matching", "Match case, whole word, path, diacritics (accents) and regular expressions are off unless " +
                    "you turn them on: in Search options, in the Search menu under the gear's arrow, with Ctrl+I, Ctrl+B, " +
                    "Ctrl+U, Ctrl+M and Ctrl+R, or with a right-click on the status bar. Those that are on show at the bottom " +
                    "right. A switch stays as you set it: new tabs, new windows and the next start begin with it."),
                Row("Inside files", "With Settings › Indexing › Search inside files set to \"Also search inside documents\", " +
                    "a word also finds the documents that have it inside, while Search › Match inside files is on. The Search " +
                    "syntax part has the details."),
            ], null),

            new("Folders and tabs", null,
            [
                Row("Address bar", "It shows where you are. Click a part to go there, the arrow after it to pick a folder " +
                    "inside, or the empty space to type a path (Ctrl+L too)."),
                Row("Back and Forward", "Go back and forward with the arrows at the top left, Alt+Left and Alt+Right, or the " +
                    "mouse's side buttons. They also go back to a search you left, with its words."),
                Row("Up", "Opens the folder that holds this one (Alt+Up)."),
                Row("Tabs", "Ctrl+T, or the + after the tabs, opens a tab; Ctrl+W, its \u00D7 or a middle click closes it. Drag a " +
                    "tab to move it, or out of the strip to give it a window of its own; right-click it for more."),
                Row("Navigation pane", "The folders on the left: click one to open it, or middle-click it to open it in a new tab."),
                Row("Bookmarks", "Ctrl+D saves the search you typed, with its kind of file and sort. To run one again, pick " +
                    "it from Bookmarks under the gear's arrow; Ctrl+Shift+B organizes them."),
            ], null),

            new("Looking at files", null,
            [
                Row("Thumbnails", "In a folder, the Thumbnails button shows every file in it and its subfolders as pictures; " +
                    "while searching, the files found and every file in the folders found. Click it again to go back; its " +
                    "arrow shows the files as a list instead, or this folder only."),
                Row("Preview", "The pane at the right shows the selected file: pictures, PDFs, documents, design files, " +
                    "videos and much more. Click the Preview button, or press Alt+P, to show or hide it; Office files are " +
                    "shown by Office when it can, otherwise Rock Preview shows their text."),
                Row("Instant thumbnails", "Off unless you turn it on in Settings › Thumbnails. It keeps small copies of " +
                    "thumbnails on disk, so thumbnails you have seen before appear at once, also after a restart. Type how " +
                    "much space it may use in Keep at most (in GB), choose where with Change location, and tick the kinds " +
                    "of files under Keep thumbnails of. When it is full, thumbnails of documents are dropped first, then " +
                    "videos, then the oldest pictures; switching a kind off deletes its saved thumbnails."),
            ], null),

            new("Files", null,
            [
                Row("Open", "Double-click, or press Enter, to open a file in its program or a folder here. Ctrl+Enter goes " +
                    "to the folder a search result is in."),
                Row("Right-click menu", "Windows' own menu for the selection: Open with, Send to, Properties and the rest; " +
                    "search results also have Open file location."),
                Row("Rename", "F2, or a slow second click on the name, renames in place: Enter keeps the new name, Esc leaves it."),
                Row("Delete", "The Delete key moves the selection to the Recycle Bin, Shift+Delete deletes it for good. Ctrl+Z undoes " +
                    "the last file operation, Ctrl+Y redoes it."),
                Row("Copy and paste", "Ctrl+C or Ctrl+X copies or cuts the selected files, and Ctrl+V pastes them into the " +
                    "open folder. Ctrl+Shift+C copies their full paths as text."),
                Row("Drag and drop", "Drag files to another program, or onto a folder, a part of the address bar or a tab " +
                    "to put them there."),
                Row("Selecting", "In the list of results, drag from an empty part to select with a box, holding Ctrl to add. " +
                    "Select all, Select none and Invert selection are under the gear's arrow."),
            ], null),

            new("Settings and updates", null,
            [
                Row("Settings", "Click the gear on the command bar, or press Ctrl+, (comma). The arrow beside the gear has " +
                    "every other command: File, Edit, View, Browse, Search, Bookmarks, Tools and Help."),
                Row("Updates", "Rock Preview looks for a new version once a day and says when there is one. With Settings › " +
                    "General › Update automatically on, the installed copy updates itself at night or while it is closed, " +
                    "keeping your settings; to look now, choose Help › Check for Updates under the gear's arrow."),
                Row("Get Started", "To see the short tour again, choose Help › Get Started under the gear's arrow."),
            ], null),
        ], HelpLabel.Names);
    }

    static HelpPart KeysPart(string? hotkey)
    {
        string open = hotkey is { Length: > 0 } key ? key : DefaultHotkey;
        string openText = hotkey is { Length: > 0 }
            ? "From any program: open Rock Preview, ready to search everything, or hide it while it is in front. Change it in Settings › Keyboard."
            : $"From any program: open Rock Preview, ready to search everything, or hide it while it is in front ({DefaultHotkey} unless you changed it in Settings › Keyboard).";
        return new("Keys", "Grouped by where they work. Where a key does something else in another place, its row says so.",
        [
            new("Everywhere", null,
            [
                Key(open, openText),
                Key("F1", "Open this help."),
                Key(["Ctrl+E", "Ctrl+F"], "Go to the search box."),
                Key("Esc", "Clear the search. With nothing typed: from every file in a folder back to the folder, or else close the window."),
                Key(["Ctrl+I", "Ctrl+B", "Ctrl+U", "Ctrl+M", "Ctrl+R"],
                    "Match case / whole word / path / diacritics / regular expressions, on or off."),
                Key("Ctrl+N", "A new window on the current folder."),
                Key("Ctrl+D", "Add the search to Bookmarks."),
                Key("Ctrl+Shift+B", "Organize Bookmarks."),
                Key("Alt+Enter", "Properties of the selected items."),
                Key("Ctrl+P", "Print the selected files."),
                Key("Ctrl+S", "Save the list of results as a file (Everything file list, CSV or text)."),
                Key("F5", "Refresh."),
                Key(["Ctrl++", "Ctrl+-", "Ctrl+0"], "The window's text larger / smaller / back to its usual size (in a " +
                    "picture in the preview you have clicked in, they zoom the picture)."),
                Key("Ctrl+,", "Open Settings."),
                Key("Alt+F4", "Close the window."),
                Key("Ctrl+Q", "Exit Rock Preview."),
            ], null),

            new("Search box", null,
            [
                Key("Enter", "Remember the search and go to the results."),
                Key(["Down", "PageDown"], "Go to the results."),
                Key("Ctrl+Enter", "Go to the folder the selected result is in."),
                Key("Alt+Down", "Earlier searches (Delete forgets the highlighted one)."),
                Key("Ctrl+Backspace", "Delete the word before the caret."),
                Key("Ctrl+Z", "Undo typing."),
            ], null),

            new("Results and folders", null,
            [
                Key("Enter", "Open."),
                Key("Ctrl+Enter", "Go to the folder it is in."),
                Key("F2", "Rename; one search result is renamed in place (Enter keeps the name, Esc leaves it, Tab goes on to " +
                    "the next item and Shift+Tab to the one before, F2 again selects all, then the extension)."),
                Key(["Delete", "Shift+Delete"], "To the Recycle Bin / delete for good (Windows asks first if the Recycle Bin " +
                    "is set to ask)."),
                Key(["Ctrl+Z", "Ctrl+Y"], "Undo / redo the last file operation (in the search box, Ctrl+Z undoes typing)."),
                Key("Ctrl+A", "Select all."),
                Key(["Ctrl+C", "Ctrl+X"], "Copy / cut the selected files (Ctrl+Insert copies too)."),
                Key("Ctrl+V", "In a folder: paste files. In search results: paste text into the search box (Shift+Insert too)."),
                Key("Ctrl+Shift+C", "Copy as path: each selected item's full path in quotes, one per line."),
                Key("Backspace", "In a folder: up one folder. In search results: back to the search box, taking off its last letter."),
                Key("Up", "On the first result: back to the search box."),
                Key("Any letter", "Typing in the results goes on in the search box."),
                Key("Ctrl+1 .. 9", "Sort the results by name, path, size, extension, type, date modified, date created, " +
                    "attributes; 9 by date recently changed (in a picture in the preview you have clicked in, Ctrl+1 shows the " +
                    "picture at its actual size)."),
            ], null),

            new("Thumbnails and layout", null,
            [
                Key("Ctrl+Shift+X", "Thumbnails on or off: the search results, or every file in the folder and its subfolders."),
                Key("Ctrl+Shift+Z", "Every file in the folder and its subfolders, as a list."),
                Key("Ctrl+Shift+1 .. 8", "Folder layout: extra large, large, medium, small icons, list, details, tiles, " +
                    "content; in search results 1, 2, 3 are thumbnail sizes and 6 is details."),
                Key(["Arrows", "PageUp", "PageDown", "Home", "End"], "Move between thumbnails; with Shift, select as you go."),
            ], null),

            new("Folders and tabs", null,
            [
                Key(["Alt+Left", "Alt+Right", "Alt+Up"], "Back / Forward / Up. Back and Forward also go back to a search you left."),
                Key("Alt+Home", "Go to Home, as in File Explorer (This PC on a Windows without Home)."),
                Key(["F4", "Alt+D", "Ctrl+L"], "Type an address; F4 or Alt+Down while typing lists the addresses typed before."),
                Key(["Enter", "Esc"], "While typing an address: go there / stop typing."),
                Key("Ctrl+Shift+N", "New folder."),
                Key("Ctrl+T", "New tab."),
                Key("Ctrl+W", "Close the tab (the last one closes the window)."),
                Key(["Ctrl+Tab", "Ctrl+Shift+Tab"], "Next / previous tab."),
            ], null),

            new("Preview", null,
            [
                Key("Alt+P", "Show or hide the preview pane."),
                Key(["PageDown", "PageUp"], "Next / previous page of a preview with several pages, once you have clicked in it " +
                    "(Right and Left too)."),
                Key(["F3", "Shift+F3"], "Next / previous match of the search words, once you have clicked in the preview."),
                Key(["Ctrl++", "Ctrl+-", "Ctrl+0", "Ctrl+1"], "In a picture or PDF page, once you have clicked in it: zoom in / " +
                    "zoom out / fit the pane / actual size."),
                Key(["Ctrl+A", "Ctrl+C"], "In a text preview: select all / copy."),
            ], null),

            new("This help", null,
            [
                Key(["Ctrl+1", "Ctrl+2", "Ctrl+3"], "Jump to How to use / Keys / Search syntax."),
                Key(["Left", "Right"], "On the buttons at the top: move between them (Tab gets there, Enter or Space jumps)."),
                Key("Ctrl+C", "Copy all of this help (Ctrl+Insert too)."),
                Key("Esc", "Close this help."),
            ], null),

            new("Mouse", null,
            [
                Key("Middle click", "On a tab: close it. On a folder in the navigation pane: open it in a new tab."),
                Key("Side buttons", "Go back and forward, as the arrows at the top left do."),
                Key("Ctrl+wheel", "Thumbnails: bigger or smaller. In the preview: zoom a picture at the pointer, or make text " +
                    "larger or smaller."),
                Key("Drag", "A tab: along the strip to move it, out of it for a window of its own, or onto another Rock " +
                    "Preview window's tabs. Files: onto another program, a folder, a part of the address bar or a tab."),
                Key("Drag on empty space", "In the list of results (not Thumbnails): select with a box; hold Ctrl to add to the selection."),
                Key("Slow click", "A second, slower click on a selected item's name renames it."),
                Key("Double-click", "Open a file or folder. On a picture in the preview: fit it, or show its actual size. In " +
                    "the Search syntax part of this help: try a name or an example in the search box."),
                Key("Right-click", "A tab: its menu. The status bar: the matching switches and kinds of file. A column " +
                    "heading: which columns show. The address bar: edit the address or delete its history."),
            ], null),
        ], HelpLabel.Chips);
    }
}
