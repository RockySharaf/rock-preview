namespace Anything.Core;

/// <summary>
/// Rules for a shell preview handler that could not be created (Office's previewers among them): what to tell the
/// user, which program the handler runs in, and how often to ask a program that was busy a moment ago. Pure decisions
/// on plain facts (times are passed in), so they are tested; the preview pane gathers the facts from COM, the registry
/// and the clock.
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

    /// <summary>
    /// The program's name when it is worth telling the user (see <see cref="ProgramName"/>); null when it is not known
    /// or is a shared host, since closing that would not help, or could not be done.
    /// </summary>
    public static string? NamedProgram(string? program)
    {
        if (string.IsNullOrWhiteSpace(program)) return null;
        string p = program.Trim();
        if (p.Equals("the previewer", StringComparison.OrdinalIgnoreCase) || IsSharedHost(p)) return null;
        return p;
    }

    /// <summary>
    /// What to do about a busy previewer, without its code: a named program (<see cref="NamedProgram"/>) is named and
    /// the user told how to close it; otherwise only to try again.
    /// </summary>
    public static string BusyAdvice(string? namedProgram) => namedProgram is { Length: > 0 } p
        // Its windows may all be hidden, so Task Manager is named as the way to close it.
        ? p + " is busy. If it stays busy, close " + p + " (in Task Manager if none of its windows is open) and select the file again."
        : "The previewer is busy. Select the file again in a moment.";

    /// <summary>
    /// The line under a preview that Rock Preview made itself because the previewer was busy: "shown as text" for a
    /// text or document view, otherwise "a simpler view" (a table, or a picture that may come from Windows).
    /// </summary>
    public static string BusyFooter(string? namedProgram, bool asText)
    {
        string who = namedProgram is { Length: > 0 } p ? p : "The previewer";
        return who + (asText ? " is busy, so this is shown as text" : " is busy, so a simpler view is shown")
            + " (hover here for what to do)";
    }

    /// <summary>
    /// The error shown when the handler could not be created, from what the out-of-process and the in-process attempts
    /// answered (0 for an attempt that was not made or that answered nothing useful) and the program's name (see
    /// <see cref="ProgramName"/>). A busy program gets <see cref="BusyAdvice"/>. The code goes last, in brackets, so a
    /// narrow line cuts the code and not the advice.
    /// </summary>
    public static string CreateError(int localHr, int inprocHr, string program)
    {
        string text;
        int code;
        if (IsBusy(localHr))
        {
            text = BusyAdvice(NamedProgram(program));
            code = localHr;
        }
        else if (localHr == ServerExecFailure)
        {
            text = (string.IsNullOrWhiteSpace(program) ? "the previewer" : program) + " could not be started";
            code = localHr;
        }
        else
        {
            text = "the previewer could not be started";
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

    /// <summary>Programs that host many unrelated things (COM surrogates, the shell, service hosts, the preview host).</summary>
    private static readonly HashSet<string> SharedHosts = new(StringComparer.OrdinalIgnoreCase)
    {
        "dllhost", "explorer", "svchost", "prevhost", "rundll32", "sihost", "runtimebroker",
    };

    /// <summary>
    /// The handler's program (a path, a file name, or a name without ".exe") is one that many things run in, so it is
    /// not named in the busy advice. False for null or empty.
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
}
