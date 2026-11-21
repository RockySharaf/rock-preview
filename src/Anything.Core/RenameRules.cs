namespace Anything.Core;

/// <summary>
/// File Explorer's rules for renaming an item, which need no window so they can be tested: the characters a name can't
/// hold, the part of the name an edit starts with selected, whether the extension changed, and where Tab goes next.
/// </summary>
public static class RenameRules
{
    /// <summary>The characters Explorer refuses as they are typed, in the order its balloon lists them.</summary>
    public const string ForbiddenCharacters = "\\/:*?\"<>|";

    /// <summary>What Explorer's balloon says when one of them is typed.</summary>
    public const string ForbiddenMessage = "A file name can't contain any of the following characters:\n\\ / : * ? \" < > |";

    /// <summary>What Explorer asks before a file's extension changes.</summary>
    public const string ExtensionChangeQuestion =
        "If you change a file name extension, the file might become unusable.\n\nAre you sure you want to change it?";

    /// <summary>
    /// Whether <paramref name="c"/> can't be in a file name: one of <see cref="ForbiddenCharacters"/>, a control
    /// character (a pasted line break), or DEL, which an edit box types for Ctrl+Backspace.
    /// </summary>
    public static bool IsForbidden(char c) => c < ' ' || c == '\x7f' || ForbiddenCharacters.Contains(c);

    /// <summary>
    /// What Ctrl+Backspace leaves in a box, as in Explorer's boxes: the selection taken out, or else the word before the
    /// caret (with the spaces after it); the text left and where the caret goes.
    /// </summary>
    public static (string Text, int Caret) DeletePreviousWord(string text, int start, int length)
    {
        start = Math.Clamp(start, 0, text.Length);
        length = Math.Clamp(length, 0, text.Length - start);
        if (length > 0) return (text.Remove(start, length), start);
        int i = start;
        while (i > 0 && char.IsWhiteSpace(text[i - 1])) i--;
        while (i > 0 && !char.IsWhiteSpace(text[i - 1])) i--;
        return (text[..i] + text[start..], i);
    }

    /// <summary><paramref name="text"/> without the characters a name can't hold (as pasted text is taken).</summary>
    public static string RemoveForbidden(string text)
    {
        if (!text.Any(IsForbidden)) return text;
        return new string(text.Where(c => !IsForbidden(c)).ToArray());
    }

    /// <summary>
    /// The extension as Windows finds it (PathFindExtension): from the last dot, unless a space follows that dot; ""
    /// when there is none. A name that starts with its only dot (".gitignore") is all extension, as in Explorer.
    /// </summary>
    public static string Extension(string name)
    {
        int dot = name.LastIndexOf('.');
        if (dot < 0 || name.IndexOf(' ', dot) >= 0) return "";
        return name[dot..];
    }

    /// <summary>
    /// How much of <paramref name="name"/> an edit starts with selected, as in Explorer: a file's name before its
    /// extension, or all of it for a folder, a name with no extension, or one that starts with its only dot.
    /// </summary>
    public static int BaseNameLength(string name, bool isDirectory)
    {
        if (isDirectory) return name.Length;
        int ext = Extension(name).Length;
        return ext == 0 || ext == name.Length ? name.Length : name.Length - ext;
    }

    /// <summary>
    /// What F2 selects next while a name is being edited: the name before the extension, then all of it, then the
    /// extension alone, and round again. Folders and names with no extension go between the base name and the caret
    /// at the end.
    /// </summary>
    public static (int Start, int Length) NextSelection(string text, bool isDirectory, int start, int length)
    {
        int baseLength = BaseNameLength(text, isDirectory);
        var steps = new List<(int Start, int Length)> { (0, baseLength) };
        if (baseLength < text.Length)
        {
            steps.Add((0, text.Length));
            steps.Add((baseLength, text.Length - baseLength));
        }
        else steps.Add((text.Length, 0));
        int now = steps.IndexOf((start, length));
        return steps[(now + 1) % steps.Count];
    }

    /// <summary>
    /// The name typed, made ready for renaming: without spaces before and after it (Windows drops trailing dots and
    /// spaces itself). Null when nothing is left, which leaves the name as it was.
    /// </summary>
    public static string? Normalize(string typed)
    {
        string name = typed.Trim();
        return name.Length == 0 || name.Trim('.').Length == 0 ? null : name;
    }

    /// <summary>
    /// Whether renaming <paramref name="oldName"/> to <paramref name="newName"/> changes a file's extension, which
    /// Explorer asks about first; only the case changed, or a folder, is not.
    /// </summary>
    public static bool ExtensionChanged(string oldName, string newName, bool isDirectory)
        => !isDirectory && !string.Equals(Extension(oldName), Extension(newName), StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The item Tab (<paramref name="move"/> 1) or Shift+Tab (-1) goes on to rename after <paramref name="index"/>, or
    /// -1 past the first or last of <paramref name="count"/>.
    /// </summary>
    public static int Step(int index, int move, int count)
    {
        int next = index + Math.Sign(move);
        return move == 0 || next < 0 || next >= count ? -1 : next;
    }
}
