namespace Anything.Core;

/// <summary>
/// Rules for a shell preview handler that could not be created (Office's previewers among them): what to tell the
/// user, which program the handler runs in, which of that program's windows is a message waiting for an answer, how
/// often to ask a program that was busy a moment ago, and whether its message may be pulled to the front now. Pure
/// decisions on plain facts (times are passed in), so they are tested; the preview pane gathers the facts from COM,
/// Win32 and the clock.
/// </summary>
public static class PreviewHandlerRules
{
    /// <summary>RPC_E_SERVERCALL_RETRYLATER: the server's message filter answered "busy, call again later", typically
    /// because a modal window of the server is waiting.</summary>
    public const int ServerCallRetryLater = unchecked((int)0x8001010A);

    /// <summary>RPC_E_CALL_REJECTED: the server's message filter turned the call away outright; Office does this too
    /// while one of its windows waits for an answer, so it is treated as busy.</summary>
    public const int CallRejected = unchecked((int)0x80010001);

    /// <summary>CO_E_SERVER_EXEC_FAILURE: the server's program could not be started.</summary>
    public const int ServerExecFailure = unchecked((int)0x80080005);

    /// <summary>REGDB_E_CLASSNOTREG: no server of this kind is registered (the usual answer of the in-process attempt
    /// for a handler that runs only in its own program).</summary>
    public const int ClassNotRegistered = unchecked((int)0x80040154);

    /// <summary>What became of the search for a message the handler's busy program is waiting on.</summary>
    public enum WaitingMessage
    {
        /// <summary>No waiting message was found, the program was not searched, or the window found is not responding
        /// (it is then left alone).</summary>
        NotFound,
        /// <summary>The message was found and brought to the front.</summary>
        BroughtToFront,
        /// <summary>The message was found but only flashed in the taskbar: Windows refused the foreground, the user
        /// was pressing keys or clicking just then, or it was brought to the front once before and is not pulled
        /// forward again.</summary>
        FlashedOnly,
    }

    /// <summary>
    /// The handler's program turned the creation away: <see cref="ServerCallRetryLater"/> or <see cref="CallRejected"/>,
    /// both of which its message filter answers while it is busy (a short wait may cure it). No other code counts.
    /// </summary>
    public static bool IsBusy(int hresult) => hresult == ServerCallRetryLater || hresult == CallRejected;

    /// <summary>Attempts at creating a handler whose program is not known to be busy: the first and three more.</summary>
    public const int FullCreateAttempts = 4;

    /// <summary>How long a busy answer is remembered for a handler's class: meanwhile one attempt is made, no waits.</summary>
    public const long BusyMemoryMs = 5000;

    /// <summary>
    /// How many times to try creating a handler now, given the tick (milliseconds, a steady clock) at which a creation
    /// for the same class last ended busy (null for not since it last answered otherwise). Within
    /// <see cref="BusyMemoryMs"/> of that, one attempt only, so moving through a folder of such files while the program
    /// waits on a hidden message does not stall the pane on each file; otherwise, or for the first busy answer, the
    /// full retries (a program just starting answers busy for a moment). A tick later than now (should not happen on
    /// a steady clock) counts as long ago.
    /// </summary>
    public static int CreateAttempts(long? lastBusyTick, long nowTick)
    {
        if (lastBusyTick is not long last) return FullCreateAttempts;
        long since = nowTick - last;
        return since >= 0 && since < BusyMemoryMs ? 1 : FullCreateAttempts;
    }

    /// <summary>The user must have pressed or clicked nothing for this long before a waiting message is pulled forward.</summary>
    public const uint QuietInputMs = 700;

    /// <summary>
    /// Milliseconds since the last input, from GetTickCount and GetLastInputInfo's tick (both 32-bit, wrapping about
    /// every 49.7 days: the unsigned difference stays right across a wrap).
    /// </summary>
    public static uint InputIdleMs(uint nowTick, uint lastInputTick) => unchecked(nowTick - lastInputTick);

    /// <summary>
    /// A waiting message may be brought to the front only while the user is not typing or clicking: nothing pressed or
    /// clicked for <see cref="QuietInputMs"/> and no key or mouse button held down. Otherwise the user's next key
    /// (Enter, Space, a button's letter), meant for the file list, would answer that message unread.
    /// </summary>
    public static bool MayTakeForeground(uint idleMs, bool inputDown) => !inputDown && idleMs >= QuietInputMs;

    /// <summary>What to do with the waiting message that was found.</summary>
    public enum WaitingAction
    {
        /// <summary>Leave it alone: its window is not responding, and even flashing it could wait on it.</summary>
        Leave,
        /// <summary>Only flash its taskbar button.</summary>
        Flash,
        /// <summary>Try to bring it to the front (flash it if Windows refuses).</summary>
        BringForward,
    }

    /// <summary>
    /// What to do with a waiting message found: a window that is not responding is left alone; one brought to the front
    /// before (<see cref="SameWaitingWindow"/>) or found while the user is typing or clicking
    /// (<see cref="MayTakeForeground"/>) is only flashed; otherwise it is brought forward.
    /// </summary>
    public static WaitingAction WaitingWindowAction(bool hung, bool broughtBefore, uint idleMs, bool inputDown)
    {
        if (hung) return WaitingAction.Leave;
        if (broughtBefore || !MayTakeForeground(idleMs, inputDown)) return WaitingAction.Flash;
        return WaitingAction.BringForward;
    }

    /// <summary>How often a message that was only flashed is looked at again, to bring it forward once the user is quiet.</summary>
    public const int RetryIntervalMs = 400;

    /// <summary>Most looks again after the last call that asked for them: 15 x 400 ms, six seconds.</summary>
    public const int MaxRetryTicks = 15;

    /// <summary>
    /// Whether to look at a waiting message again later: it was only flashed because the user was typing or clicking
    /// (not because it is not responding or was brought forward before). A message box owned by a hidden program has no
    /// taskbar button, so its flash cannot be seen, and the selection that found it is nearly always just made.
    /// </summary>
    public static bool RetryAfterFlash(bool hung, bool broughtBefore, uint idleMs, bool inputDown)
        => !hung && !broughtBefore && !MayTakeForeground(idleMs, inputDown);

    /// <summary>What a look again found for the same program.</summary>
    public enum WaitingLook
    {
        /// <summary>No waiting message (it was answered, or its program is gone).</summary>
        NoWindow,
        /// <summary>A waiting message whose window is not responding.</summary>
        Hung,
        /// <summary>The message that was brought to the front before (<see cref="SameWaitingWindow"/>).</summary>
        BroughtBefore,
        /// <summary>A waiting message that may be brought forward when the user is quiet.</summary>
        Found,
    }

    /// <summary>What to do after a look again.</summary>
    public enum RetryStep
    {
        /// <summary>Look again at the next tick.</summary>
        LookAgain,
        /// <summary>Bring the message forward and stop looking.</summary>
        BringForwardAndStop,
        /// <summary>Stop looking.</summary>
        Stop,
    }

    /// <summary>
    /// After a look again (ticksRun of them so far, this one included): a found message is brought forward once the user
    /// is quiet (<see cref="MayTakeForeground"/>); no message, one not responding or one brought forward before ends the
    /// looking; otherwise it goes on, at most <see cref="MaxRetryTicks"/> looks.
    /// </summary>
    public static RetryStep WaitingRetryStep(WaitingLook look, uint idleMs, bool inputDown, int ticksRun)
    {
        if (look != WaitingLook.Found) return RetryStep.Stop;
        if (MayTakeForeground(idleMs, inputDown)) return RetryStep.BringForwardAndStop;
        return ticksRun >= MaxRetryTicks ? RetryStep.Stop : RetryStep.LookAgain;
    }

    /// <summary>
    /// The error shown when the handler could not be created, from what the out-of-process and the in-process attempts
    /// answered (0 for an attempt that was not made or that answered nothing useful), the program's name (see
    /// <see cref="ProgramName"/>) and what became of the search for a message it is waiting on. A busy program with no
    /// message found is named and the user told how to close it, unless its name is not known or it is a shared host
    /// (closing that would not help, or could not be done). The code goes last, in brackets, so a narrow line cuts the
    /// code and not the advice.
    /// </summary>
    public static string CreateError(int localHr, int inprocHr, string program, WaitingMessage waiting)
    {
        if (string.IsNullOrWhiteSpace(program)) program = "the previewer";
        bool named = !program.Equals("the previewer", StringComparison.OrdinalIgnoreCase) && !IsSharedHost(program);
        string text;
        int code;
        if (IsBusy(localHr))
        {
            text = waiting switch
            {
                WaitingMessage.BroughtToFront => program + " is waiting for an answer in one of its windows. "
                    + "Its message is now in front: answer it, then select the file again.",
                WaitingMessage.FlashedOnly => program + " is waiting for an answer in one of its windows. "
                    + "Look for its message (it may be behind other windows), answer it, then select the file again.",
                // Its windows may all be hidden, so Task Manager is named as the way to close it.
                _ when named => program + " is busy. If it stays busy, close " + program
                    + " (in Task Manager if none of its windows is open) and select the file again.",
                _ => "The previewer is busy. Select the file again in a moment.",
            };
            code = localHr;
        }
        else if (localHr == ServerExecFailure)
        {
            text = program + " could not be started";
            code = localHr;
        }
        else
        {
            text = "could not create handler";
            code = TellingCode(localHr, inprocHr);
        }
        return code == 0 ? text : text + " (0x" + code.ToString("X8") + ")";
    }

    /// <summary>
    /// The more telling of the two attempts' codes: the out-of-process one, unless it says nothing (0) or only that
    /// no such server is registered while the in-process attempt said something.
    /// </summary>
    public static int TellingCode(int localHr, int inprocHr)
    {
        if (localHr == 0) return inprocHr;
        if (localHr == ClassNotRegistered && inprocHr != 0) return inprocHr;
        return localHr;
    }

    /// <summary>
    /// The executable of a registered command line (a LocalServer32 value): the text in the leading quotes, else up to
    /// and including the first ".exe" that ends a word (paths with spaces are often left unquoted), else up to the first
    /// space. Null when there is none.
    /// </summary>
    public static string? ExecutablePath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;
        string s = command.Trim();
        if (s[0] == '"')
        {
            int close = s.IndexOf('"', 1);
            string inner = (close < 0 ? s[1..] : s[1..close]).Trim();
            return inner.Length == 0 ? null : inner;
        }
        for (int at = s.IndexOf(".exe", StringComparison.OrdinalIgnoreCase); at >= 0;
             at = s.IndexOf(".exe", at + 4, StringComparison.OrdinalIgnoreCase))
        {
            int end = at + 4;
            if (end == s.Length || char.IsWhiteSpace(s[end])) return s[..end];
        }
        int space = s.IndexOfAny([' ', '\t']);
        return space < 0 ? s : s[..space];
    }

    /// <summary>
    /// The name of the program a handler runs in, from its LocalServer32 command line: the file name without extension,
    /// with Office's own programs given their product names. "the previewer" when there is no such program.
    /// </summary>
    public static string ProgramName(string? localServerCommand)
    {
        string? exe = ExecutablePath(localServerCommand);
        if (exe == null) return "the previewer";
        int slash = exe.LastIndexOfAny(['\\', '/']);
        string name = slash >= 0 ? exe[(slash + 1)..] : exe;
        int dot = name.LastIndexOf('.');
        if (dot > 0) name = name[..dot];
        if (name.Length == 0) return "the previewer";
        return name.ToUpperInvariant() switch
        {
            "WINWORD" => "Microsoft Word",
            "EXCEL" => "Microsoft Excel",
            "POWERPNT" => "Microsoft PowerPoint",
            _ => name,
        };
    }

    /// <summary>Programs that host many unrelated things (COM surrogates, the shell, service hosts, the preview host),
    /// so a window of theirs says nothing about the handler: they are never searched for a waiting message.</summary>
    private static readonly HashSet<string> SharedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "dllhost", "explorer", "svchost", "prevhost", "rundll32", "sihost", "runtimebroker",
    };

    /// <summary>
    /// The handler's program (a path, a file name, or a name without ".exe") is one that many things run in, so its
    /// windows are not searched and the plain busy message is used. False for null or empty.
    /// </summary>
    public static bool IsSharedHost(string? program)
    {
        if (string.IsNullOrWhiteSpace(program)) return false;
        string name = program.Trim();
        int slash = name.LastIndexOfAny(['\\', '/']);
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
        return SharedHosts.Contains(name);
    }

    /// <summary>
    /// The window now found waiting is the one brought to the front last time: the same handle in the same process,
    /// and that process was not started after that time (a process id can be reused). An unknown start time (the
    /// process could not be asked) does not count against it. Such a window is only flashed, never pulled forward
    /// again, so keys meant for the file list do not land in it.
    /// </summary>
    public static bool SameWaitingWindow(
        nint hwnd, uint pid, DateTime? processStartUtc, nint lastHwnd, uint lastPid, DateTime lastBroughtUtc)
        => lastHwnd != 0 && hwnd == lastHwnd && pid == lastPid
           && (processStartUtc is not DateTime start || start <= lastBroughtUtc);

    /// <summary>The facts about one top-level window of the handler's program.</summary>
    /// <param name="ClassName">Its window class.</param>
    /// <param name="Visible">It is visible.</param>
    /// <param name="Enabled">It takes input.</param>
    /// <param name="HasOwner">It has an owner window (GW_OWNER).</param>
    /// <param name="ProcessHasVisibleMainWindow">Its process shows another visible, unowned window, whether or not
    /// that window takes input.</param>
    public readonly record struct WindowFacts(
        string? ClassName, bool Visible, bool Enabled, bool HasOwner, bool ProcessHasVisibleMainWindow);

    /// <summary>
    /// How surely a window is a message a hidden program is waiting on: 0 not at all; 1 a visible, enabled, owned
    /// window; 2 the same with a dialog's class (the standard dialog "#32770", Office's "NUIDialog" and "bosa_sdm_"
    /// dialogs), or an unowned dialog. Only a process that shows no visible main window at all is a hidden program: a
    /// visible main window, even a disabled one, is the user's own open program (a main window is disabled exactly
    /// while the program shows its own box to the user), and its windows are never touched. An unowned window of
    /// another class is the program's own main window, never a waiting message.
    /// </summary>
    public static int WaitingDialogRank(WindowFacts f)
    {
        if (f.ProcessHasVisibleMainWindow) return 0;
        if (!f.Visible || !f.Enabled) return 0;
        bool dialog = IsDialogClass(f.ClassName);
        if (!f.HasOwner) return dialog ? 2 : 0;
        return dialog ? 2 : 1;
    }

    /// <summary>A window class that dialogs and message boxes use.</summary>
    public static bool IsDialogClass(string? className)
        => className is "#32770" or "NUIDialog"
           || (className != null && className.StartsWith("bosa_sdm_", StringComparison.Ordinal));
}
