namespace Anything.Core;

/// <summary>
/// The New folder command's rules for the folder it makes: which window message brings the shell's word of it, and
/// whether its select and rename may still start. Kept apart from the window so they can be tested.
/// </summary>
public static class NewFolderRename
{
    /// <summary>
    /// The messages the shell's word comes in (WM_APP + 0x100 ... + 0x1FF): each command a message of its own, so word
    /// still queued for an earlier command, whose wait has ended, is told apart and only let go.
    /// </summary>
    public const int FirstMessage = 0x8000 + 0x100, LastMessage = FirstMessage + 0xFF;

    /// <summary>The message for the <paramref name="command"/>-th command (counting on past the last message wraps round).</summary>
    public static int MessageFor(int command) => FirstMessage + (command & 0xFF);

    /// <summary>
    /// How long, in milliseconds, the command waits for the shell's word once the view's command has returned. The word
    /// normally comes at once, because the view's command has made the folder before it returns; a wait still open later
    /// would take a folder made there afterwards by something else (a paste, a copy in File Explorer) for the command's.
    /// </summary>
    public const int WaitMilliseconds = 10_000;

    public static bool IsMessage(int msg) => msg is >= FirstMessage and <= LastMessage;

    /// <summary>
    /// Whether where the folder lands on disk must be asked of the shell: a library (also one opened from its
    /// .library-ms file, whose own path is the file's) makes it in its save folder; a location without a path asks too.
    /// </summary>
    public static bool NeedsSaveFolder(string? folder) => folder == null || folder.EndsWith(".library-ms", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the new folder is selected and its rename started: only while the user is still where the command left
    /// them, the keys in the view (the command gave it the focus) and the same location shown. Gone to the search box,
    /// the address bar, another tab or another folder meanwhile, what they do stands.
    /// </summary>
    public static bool MayStart(bool keysInView, string? locationNow, string? locationAtCommand)
        => keysInView && string.Equals(locationNow, locationAtCommand, StringComparison.OrdinalIgnoreCase);
}
