using Anything.Core;

namespace Anything.Tests;

/// <summary>
/// <see cref="PreviewHandlerRules"/>: what the preview pane says when a shell preview handler could not be created,
/// and which program the handler runs in.
/// </summary>
public sealed class PreviewHandlerRulesTests
{
    const int RetryLater = unchecked((int)0x8001010A);
    const int Rejected = unchecked((int)0x80010001);
    const int ExecFailure = unchecked((int)0x80080005);
    const int ClassNotReg = unchecked((int)0x80040154);
    const int NoInterface = unchecked((int)0x80004002);
    const int AccessDenied = unchecked((int)0x80070005);

    public static TheoryData<int, int, string, string> Errors() => new()
    {
        // Busy, from the out-of-process attempt: a known program is named, with how to close it.
        { RetryLater, ClassNotReg, "Microsoft Word",
          "Microsoft Word is busy. If it stays busy, close Microsoft Word (in Task Manager if none of its windows is open) and select the file again. (0x8001010A)" },
        { Rejected, ClassNotReg, "Microsoft Excel",
          "Microsoft Excel is busy. If it stays busy, close Microsoft Excel (in Task Manager if none of its windows is open) and select the file again. (0x80010001)" },
        { RetryLater, 0, "the previewer",
          "The previewer is busy. Select the file again in a moment. (0x8001010A)" },
        { RetryLater, 0, " ",
          "The previewer is busy. Select the file again in a moment. (0x8001010A)" },
        // A shared host is not named: closing it would not help, or could not be done.
        { RetryLater, 0, "prevhost",
          "The previewer is busy. Select the file again in a moment. (0x8001010A)" },
        { Rejected, 0, "DllHost",
          "The previewer is busy. Select the file again in a moment. (0x80010001)" },
        { RetryLater, 0, "SomeViewer",
          "SomeViewer is busy. If it stays busy, close SomeViewer (in Task Manager if none of its windows is open) and select the file again. (0x8001010A)" },
        // Could not start its program.
        { ExecFailure, ClassNotReg, "Microsoft PowerPoint", "Microsoft PowerPoint could not be started (0x80080005)" },
        // Anything else: the more telling code.
        { ClassNotReg, NoInterface, "the previewer", "the previewer could not be started (0x80004002)" },
        { AccessDenied, ClassNotReg, "the previewer", "the previewer could not be started (0x80070005)" },
        { ClassNotReg, ClassNotReg, "the previewer", "the previewer could not be started (0x80040154)" },
        { ClassNotReg, 0, "the previewer", "the previewer could not be started (0x80040154)" },
        { 0, AccessDenied, "the previewer", "the previewer could not be started (0x80070005)" },
        { 0, 0, "the previewer", "the previewer could not be started" },
        // Busy only counts from the out-of-process attempt.
        { ClassNotReg, RetryLater, "Microsoft Word", "the previewer could not be started (0x8001010A)" },
    };

    [Theory, MemberData(nameof(Errors))]
    public void The_error_says_what_happened_with_the_code_last(int localHr, int inprocHr, string program, string expected)
        => Assert.Equal(expected, PreviewHandlerRules.CreateError(localHr, inprocHr, program));

    [Theory, MemberData(nameof(Errors))]
    public void No_sentence_of_the_error_has_two_colons(int localHr, int inprocHr, string program, string expected)
    {
        Assert.NotNull(expected);
        foreach (string sentence in PreviewHandlerRules.CreateError(localHr, inprocHr, program).Split(". "))
            Assert.True(sentence.Count(c => c == ':') <= 1, sentence);
    }

    [Fact]
    public void A_missing_program_name_reads_as_the_previewer()
        => Assert.Equal("the previewer could not be started (0x80080005)", PreviewHandlerRules.CreateError(ExecFailure, 0, " "));

    [Theory]
    [InlineData("Microsoft Word", "Microsoft Word")]
    [InlineData("  SomeViewer ", "SomeViewer")]
    [InlineData("the previewer", null)]
    [InlineData("The Previewer", null)]
    [InlineData("prevhost", null)]
    [InlineData("dllhost.exe", null)]
    [InlineData("", null)]
    [InlineData("  ", null)]
    [InlineData(null, null)]
    public void Only_a_known_program_of_its_own_is_named(string? program, string? named)
        => Assert.Equal(named, PreviewHandlerRules.NamedProgram(program));

    [Fact]
    public void The_busy_advice_is_the_error_without_its_code()
    {
        Assert.Equal("Microsoft Word is busy. If it stays busy, close Microsoft Word (in Task Manager if none of its windows is open) and select the file again.",
            PreviewHandlerRules.BusyAdvice("Microsoft Word"));
        Assert.Equal("The previewer is busy. Select the file again in a moment.", PreviewHandlerRules.BusyAdvice(null));
        foreach (string program in new[] { "Microsoft Excel", "prevhost", "the previewer" })
            Assert.Equal(PreviewHandlerRules.BusyAdvice(PreviewHandlerRules.NamedProgram(program)) + " (0x8001010A)",
                PreviewHandlerRules.CreateError(RetryLater, 0, program));
    }

    [Theory]
    [InlineData("Microsoft Word", true, "Microsoft Word is busy, so this is shown as text (hover here for what to do)")]
    [InlineData("Microsoft Excel", false, "Microsoft Excel is busy, so a simpler view is shown (hover here for what to do)")]
    [InlineData(null, true, "The previewer is busy, so this is shown as text (hover here for what to do)")]
    [InlineData(null, false, "The previewer is busy, so a simpler view is shown (hover here for what to do)")]
    public void The_line_under_the_preview_names_the_busy_program(string? program, bool asText, string expected)
        => Assert.Equal(expected, PreviewHandlerRules.BusyFooter(program, asText));

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
    public void Programs_that_many_things_run_in_are_known(string? program, bool shared)
        => Assert.Equal(shared, PreviewHandlerRules.IsSharedHost(program));

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
}
