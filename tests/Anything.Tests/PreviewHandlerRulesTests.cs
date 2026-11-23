using Anything.Core;
using Facts = Anything.Core.PreviewHandlerRules.WindowFacts;
using W = Anything.Core.PreviewHandlerRules.WaitingMessage;

namespace Anything.Tests;

/// <summary>
/// <see cref="PreviewHandlerRules"/>: what the preview pane says when a shell preview handler could not be created,
/// which program the handler runs in, and which window of a hidden copy of that program is a message waiting for an answer.
/// </summary>
public sealed class PreviewHandlerRulesTests
{
    const int RetryLater = unchecked((int)0x8001010A);
    const int Rejected = unchecked((int)0x80010001);
    const int ExecFailure = unchecked((int)0x80080005);
    const int ClassNotReg = unchecked((int)0x80040154);
    const int NoInterface = unchecked((int)0x80004002);
    const int AccessDenied = unchecked((int)0x80070005);

    const W NotFound = W.NotFound, InFront = W.BroughtToFront, Flashed = W.FlashedOnly;

    public static TheoryData<int, int, string, W, string> Errors() => new()
    {
        // Busy, from the out-of-process attempt: what is said follows what became of the waiting message.
        { RetryLater, ClassNotReg, "Microsoft Word", InFront,
          "Microsoft Word is waiting for an answer in one of its windows. Its message is now in front: answer it, then select the file again. (0x8001010A)" },
        { RetryLater, ClassNotReg, "Microsoft Word", Flashed,
          "Microsoft Word is waiting for an answer in one of its windows. Look for its message (it may be behind other windows), answer it, then select the file again. (0x8001010A)" },
        { RetryLater, ClassNotReg, "Microsoft Word", NotFound,
          "Microsoft Word is busy. If it stays busy, close Microsoft Word (in Task Manager if none of its windows is open) and select the file again. (0x8001010A)" },
        { Rejected, ClassNotReg, "Microsoft Excel", NotFound,
          "Microsoft Excel is busy. If it stays busy, close Microsoft Excel (in Task Manager if none of its windows is open) and select the file again. (0x80010001)" },
        { Rejected, 0, "Microsoft Excel", Flashed,
          "Microsoft Excel is waiting for an answer in one of its windows. Look for its message (it may be behind other windows), answer it, then select the file again. (0x80010001)" },
        { RetryLater, 0, "the previewer", NotFound,
          "The previewer is busy. Select the file again in a moment. (0x8001010A)" },
        { RetryLater, 0, " ", NotFound,
          "The previewer is busy. Select the file again in a moment. (0x8001010A)" },
        // A shared host is not named: closing it would not help, or could not be done.
        { RetryLater, 0, "prevhost", NotFound,
          "The previewer is busy. Select the file again in a moment. (0x8001010A)" },
        { Rejected, 0, "DllHost", NotFound,
          "The previewer is busy. Select the file again in a moment. (0x80010001)" },
        { RetryLater, 0, "SomeViewer", NotFound,
          "SomeViewer is busy. If it stays busy, close SomeViewer (in Task Manager if none of its windows is open) and select the file again. (0x8001010A)" },
        // Could not start: the waiting message does not apply.
        { ExecFailure, ClassNotReg, "Microsoft PowerPoint", NotFound, "Microsoft PowerPoint could not be started (0x80080005)" },
        { ExecFailure, ClassNotReg, "Microsoft PowerPoint", InFront, "Microsoft PowerPoint could not be started (0x80080005)" },
        // Anything else: the more telling code.
        { ClassNotReg, NoInterface, "the previewer", NotFound, "could not create handler (0x80004002)" },
        { AccessDenied, ClassNotReg, "the previewer", NotFound, "could not create handler (0x80070005)" },
        { ClassNotReg, ClassNotReg, "the previewer", NotFound, "could not create handler (0x80040154)" },
        { ClassNotReg, 0, "the previewer", NotFound, "could not create handler (0x80040154)" },
        { 0, AccessDenied, "the previewer", NotFound, "could not create handler (0x80070005)" },
        { 0, 0, "the previewer", NotFound, "could not create handler" },
        // Busy only counts from the out-of-process attempt.
        { ClassNotReg, RetryLater, "Microsoft Word", InFront, "could not create handler (0x8001010A)" },
    };

    [Theory, MemberData(nameof(Errors))]
    public void The_error_says_what_happened_with_the_code_last(int localHr, int inprocHr, string program, W waiting, string expected)
        => Assert.Equal(expected, PreviewHandlerRules.CreateError(localHr, inprocHr, program, waiting));

    [Theory, MemberData(nameof(Errors))]
    public void No_sentence_of_the_error_has_two_colons(int localHr, int inprocHr, string program, W waiting, string expected)
    {
        Assert.NotNull(expected);
        foreach (string sentence in PreviewHandlerRules.CreateError(localHr, inprocHr, program, waiting).Split(". "))
            Assert.True(sentence.Count(c => c == ':') <= 1, sentence);
    }

    [Fact]
    public void A_missing_program_name_reads_as_the_previewer()
        => Assert.Equal("the previewer could not be started (0x80080005)", PreviewHandlerRules.CreateError(ExecFailure, 0, " ", NotFound));

    [Theory]
    [InlineData(RetryLater, true)]
    [InlineData(Rejected, true)]
    [InlineData(ExecFailure, false)]
    [InlineData(ClassNotReg, false)]
    [InlineData(0, false)]
    public void Only_the_two_busy_answers_are_busy(int hr, bool busy) => Assert.Equal(busy, PreviewHandlerRules.IsBusy(hr));

    [Theory]
    [InlineData(@"C:\Program Files\Office\WINWORD.EXE /Automation -Embedding", @"C:\Program Files\Office\WINWORD.EXE")]
    [InlineData(@"""C:\Program Files\Office\EXCEL.EXE"" -Embedding", @"C:\Program Files\Office\EXCEL.EXE")]
    [InlineData(@"  ""C:\Apps\Viewer.exe""", @"C:\Apps\Viewer.exe")]
    [InlineData(@"""C:\Apps\No close quote.exe -x", @"C:\Apps\No close quote.exe -x")]
    [InlineData(@"C:\PROGRA~1\Office\POWERPNT.EXE", @"C:\PROGRA~1\Office\POWERPNT.EXE")]
    [InlineData(@"C:\Tools\my.exetools\run.exe -a", @"C:\Tools\my.exetools\run.exe")]
    [InlineData(@"C:\Tools\server -Embedding", @"C:\Tools\server")]
    [InlineData("viewer.exe", "viewer.exe")]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("\"\"", null)]
    [InlineData(null, null)]
    public void The_executable_is_taken_out_of_the_command_line(string? command, string? expected)
        => Assert.Equal(expected, PreviewHandlerRules.ExecutablePath(command));

    [Theory]
    [InlineData(@"C:\Program Files\Office\WINWORD.EXE /Automation -Embedding", "Microsoft Word")]
    [InlineData(@"""C:\Program Files\Office\excel.exe"" -Embedding", "Microsoft Excel")]
    [InlineData(@"C:\Office\PowerPnt.exe", "Microsoft PowerPoint")]
    [InlineData(@"C:\Windows\System32\prevhost.exe", "prevhost")]
    [InlineData(@"C:\Apps\Some Viewer\SomeViewer.exe -preview", "SomeViewer")]
    [InlineData("viewer", "viewer")]
    [InlineData("", "the previewer")]
    [InlineData(null, "the previewer")]
    [InlineData(@"C:\Apps\", "the previewer")]
    public void The_program_is_named_from_its_file_with_office_given_product_names(string? command, string expected)
        => Assert.Equal(expected, PreviewHandlerRules.ProgramName(command));

    [Theory]
    [InlineData("dllhost", true)]
    [InlineData("DllHost.exe", true)]
    [InlineData(@"C:\Windows\System32\dllhost.exe", true)]
    [InlineData("EXPLORER.EXE", true)]
    [InlineData("svchost", true)]
    [InlineData(@"C:\Windows\system32\prevhost.exe", true)]
    [InlineData("rundll32.exe", true)]
    [InlineData("SiHost", true)]
    [InlineData("RuntimeBroker.exe", true)]
    [InlineData(@"C:\Program Files\Office\WINWORD.EXE", false)]
    [InlineData("EXCEL", false)]
    [InlineData("dllhosts.exe", false)]
    [InlineData("myexplorer", false)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData(null, false)]
    public void Programs_that_many_things_run_in_are_never_searched(string? program, bool shared)
        => Assert.Equal(shared, PreviewHandlerRules.IsSharedHost(program));

    static readonly DateTime Brought = new(2026, 1, 10, 12, 0, 0, DateTimeKind.Utc);

    [Theory]
    [InlineData(100L, 7u, -60.0, true)]   // the same window, its process older than the bringing forward
    [InlineData(100L, 7u, null, true)]    // the process's start time unknown
    [InlineData(101L, 7u, -60.0, false)]  // a different window
    [InlineData(100L, 8u, -60.0, false)]  // the same handle in another process
    [InlineData(100L, 7u, 60.0, false)]   // the process id taken by a process started later
    public void A_waiting_window_is_brought_forward_once(long hwnd, uint pid, double? startOffsetSeconds, bool same)
    {
        DateTime? start = startOffsetSeconds is double s ? Brought.AddSeconds(s) : null;
        Assert.Equal(same, PreviewHandlerRules.SameWaitingWindow((nint)hwnd, pid, start, 100, 7, Brought));
    }

    [Fact]
    public void Nothing_brought_forward_yet_is_never_the_same_window()
        => Assert.False(PreviewHandlerRules.SameWaitingWindow(0, 0, null, 0, 0, default));

    // Facts: class, visible, enabled, has owner, process shows another visible main window (enabled or not).
    public static TheoryData<string, Facts, int> Windows() => new()
    {
        // The case seen in the field: Office's dialog owned by a hidden, disabled main window.
        { "NUIDialog owned by hidden window", new Facts("NUIDialog", true, true, true, false), 2 },
        { "standard dialog owned by hidden window", new Facts("#32770", true, true, true, false), 2 },
        { "bosa dialog owned by hidden window", new Facts("bosa_sdm_msword", true, true, true, false), 2 },
        { "other window owned by hidden window", new Facts("SomeWindow", true, true, true, false), 1 },
        { "no class name", new Facts(null, true, true, true, false), 1 },
        // Unowned: only a dialog in a process that shows nothing else.
        { "unowned dialog, nothing else shown", new Facts("#32770", true, true, false, false), 2 },
        { "unowned dialog, main window shown", new Facts("#32770", true, true, false, true), 0 },
        { "unowned main window of its own", new Facts("OpusApp", true, true, false, false), 0 },
        // Hidden or disabled windows are never it.
        { "hidden dialog", new Facts("NUIDialog", false, true, true, false), 0 },
        { "disabled dialog", new Facts("NUIDialog", true, false, true, false), 0 },
        // Any visible main window means the user's own open program, which is never touched.
        { "user's program in use, box owned by its main window", new Facts("#32770", true, true, true, true), 0 },
        { "user's program in use, other owned window", new Facts("SomeWindow", true, true, true, true), 0 },
        // A box whose owner is hidden while the process shows a visible but disabled main window: that is how a
        // program looks while it shows its own modal box to the user, so it is the user's program, not a stuck helper.
        { "box of a hidden owner beside a visible, disabled main window", new Facts("NUIDialog", true, true, true, true), 0 },
    };

    [Theory, MemberData(nameof(Windows))]
    public void A_waiting_message_is_a_visible_enabled_box_of_a_hidden_program(string why, Facts facts, int rank)
        => Assert.True(rank == PreviewHandlerRules.WaitingDialogRank(facts), why);

    [Theory]
    [InlineData("#32770", true)]
    [InlineData("NUIDialog", true)]
    [InlineData("bosa_sdm_XL9", true)]
    [InlineData("bosa_sdm_", true)]
    [InlineData("BOSA_SDM_XL9", false)]
    [InlineData("nuidialog", false)]
    [InlineData("OpusApp", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void Dialog_classes_are_known_by_name(string? className, bool dialog)
        => Assert.Equal(dialog, PreviewHandlerRules.IsDialogClass(className));

    [Theory]
    [InlineData(null, 1_000_000L, 4)]          // never busy (or forgotten): the full retries
    [InlineData(1_000_000L, 1_000_000L, 1)]    // busy just now: one attempt
    [InlineData(1_000_000L, 1_004_999L, 1)]    // still within the five seconds
    [InlineData(1_000_000L, 1_005_000L, 4)]    // five seconds on: the full retries again
    [InlineData(1_000_000L, 9_000_000L, 4)]
    [InlineData(1_000_000L, 999_999L, 4)]      // a tick after now counts as long ago
    [InlineData(-3_000L, 1_000L, 1)]           // only the difference matters
    public void A_class_busy_a_moment_ago_gets_one_attempt(long? lastBusy, long now, int attempts)
        => Assert.Equal(attempts, PreviewHandlerRules.CreateAttempts(lastBusy, now));

    [Fact]
    public void The_full_attempts_and_the_memory_are_as_described()
    {
        Assert.Equal(4, PreviewHandlerRules.FullCreateAttempts);
        Assert.Equal(5000, PreviewHandlerRules.BusyMemoryMs);
    }

    [Theory]
    [InlineData(10_000u, 9_000u, 1_000u)]
    [InlineData(10_000u, 10_000u, 0u)]
    [InlineData(5u, uint.MaxValue - 4, 10u)] // across the 49.7-day wrap
    public void Input_idle_time_survives_the_tick_wrap(uint now, uint last, uint idle)
        => Assert.Equal(idle, PreviewHandlerRules.InputIdleMs(now, last));

    [Theory]
    [InlineData(700u, false, true)]
    [InlineData(5_000u, false, true)]
    [InlineData(699u, false, false)]    // a key pressed or a click just now
    [InlineData(0u, false, false)]
    [InlineData(5_000u, true, false)]   // a key or button held down
    public void The_foreground_is_taken_only_while_the_user_is_quiet(uint idle, bool down, bool may)
        => Assert.Equal(may, PreviewHandlerRules.MayTakeForeground(idle, down));

    [Theory]
    [InlineData(true, false, 5_000u, false, PreviewHandlerRules.WaitingAction.Leave)]  // not responding: not even flashed
    [InlineData(true, true, 0u, true, PreviewHandlerRules.WaitingAction.Leave)]
    [InlineData(false, true, 5_000u, false, PreviewHandlerRules.WaitingAction.Flash)]  // brought forward before
    [InlineData(false, false, 100u, false, PreviewHandlerRules.WaitingAction.Flash)]   // the user is typing
    [InlineData(false, false, 5_000u, true, PreviewHandlerRules.WaitingAction.Flash)]  // a key held down
    [InlineData(false, false, 700u, false, PreviewHandlerRules.WaitingAction.BringForward)]
    public void What_is_done_with_a_waiting_message(bool hung, bool before, uint idle, bool down, PreviewHandlerRules.WaitingAction action)
        => Assert.Equal(action, PreviewHandlerRules.WaitingWindowAction(hung, before, idle, down));

    [Theory]
    [InlineData(false, false, 100u, false, true)]    // flashed because the user was typing
    [InlineData(false, false, 5_000u, true, true)]   // flashed because a key was held down
    [InlineData(false, false, 700u, false, false)]   // quiet: it was brought forward (or Windows refused)
    [InlineData(false, true, 100u, false, false)]    // brought forward before
    [InlineData(true, false, 100u, false, false)]    // not responding
    public void A_message_only_flashed_for_recent_input_is_looked_at_again(bool hung, bool before, uint idle, bool down, bool retry)
        => Assert.Equal(retry, PreviewHandlerRules.RetryAfterFlash(hung, before, idle, down));

    [Theory]
    [InlineData(PreviewHandlerRules.WaitingLook.Found, 700u, false, 1, PreviewHandlerRules.RetryStep.BringForwardAndStop)]
    [InlineData(PreviewHandlerRules.WaitingLook.Found, 5_000u, false, 15, PreviewHandlerRules.RetryStep.BringForwardAndStop)] // the last tick still may
    [InlineData(PreviewHandlerRules.WaitingLook.Found, 100u, false, 1, PreviewHandlerRules.RetryStep.LookAgain)]
    [InlineData(PreviewHandlerRules.WaitingLook.Found, 5_000u, true, 14, PreviewHandlerRules.RetryStep.LookAgain)]
    [InlineData(PreviewHandlerRules.WaitingLook.Found, 100u, false, 15, PreviewHandlerRules.RetryStep.Stop)]   // the last tick
    [InlineData(PreviewHandlerRules.WaitingLook.Found, 100u, false, 16, PreviewHandlerRules.RetryStep.Stop)]
    [InlineData(PreviewHandlerRules.WaitingLook.NoWindow, 5_000u, false, 1, PreviewHandlerRules.RetryStep.Stop)]
    [InlineData(PreviewHandlerRules.WaitingLook.Hung, 5_000u, false, 1, PreviewHandlerRules.RetryStep.Stop)]
    [InlineData(PreviewHandlerRules.WaitingLook.BroughtBefore, 5_000u, false, 1, PreviewHandlerRules.RetryStep.Stop)]
    [InlineData(PreviewHandlerRules.WaitingLook.BroughtBefore, 100u, false, 1, PreviewHandlerRules.RetryStep.Stop)]
    public void What_a_look_again_leads_to(PreviewHandlerRules.WaitingLook look, uint idle, bool down, int ticks, PreviewHandlerRules.RetryStep step)
        => Assert.Equal(step, PreviewHandlerRules.WaitingRetryStep(look, idle, down, ticks));

    [Fact]
    public void The_looks_again_last_six_seconds()
    {
        Assert.Equal(400, PreviewHandlerRules.RetryIntervalMs);
        Assert.Equal(15, PreviewHandlerRules.MaxRetryTicks);
    }
}
