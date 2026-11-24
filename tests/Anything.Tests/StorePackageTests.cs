using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Anything.Tests;

/// <summary>
/// The Microsoft Store's package. Microsoft refused allowElevation on 2026-11-24, so its manifest
/// (build/Store/AppxManifest.xml) declares no restricted capability but runFullTrust, the one a desktop program needs, and
/// the program never asks for elevation inside the package: one added back by accident fails here, before a
/// certification does. Its window never runs elevated there; instant indexing comes from an opt-in helper that Windows
/// starts elevated through shell:AppsFolder (src/Anything/IndexHelper.cs), handled before anything else starts. The
/// program's part is read from the source, as its windows can't be made in the tests.
/// </summary>
public sealed class StorePackageTests
{
    const string RestrictedCapabilities = "http://schemas.microsoft.com/appx/manifest/foundation/windows10/restrictedcapabilities";

    static string RepoRoot()
    {
        for (var dir = new DirectoryInfo(AppContext.BaseDirectory); dir is not null; dir = dir.Parent)
            if (File.Exists(Path.Combine(dir.FullName, "Anything.sln"))) return dir.FullName;
        throw new DirectoryNotFoundException("The tests run outside the source tree: Anything.sln wasn't found above " + AppContext.BaseDirectory);
    }

    static XDocument Manifest() => XDocument.Load(Path.Combine(RepoRoot(), "build", "Store", "AppxManifest.xml"));

    [Fact]
    public void The_manifest_declares_no_elevation()
    {
        var named = Manifest().Descendants().Select(e => (string?)e.Attribute("Name")).OfType<string>().ToList();
        Assert.NotEmpty(named);
        Assert.DoesNotContain(named, n => n.Equals("allowElevation", StringComparison.OrdinalIgnoreCase));
        // Not even in a comment: people read the manifest sent back after the refusal too.
        Assert.DoesNotContain("allowElevation", File.ReadAllText(Path.Combine(RepoRoot(), "build", "Store", "AppxManifest.xml")), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void The_only_restricted_capability_is_runFullTrust()
    {
        // rescap, and any later restrictedcapabilities/N namespace.
        var restricted = Manifest().Descendants()
            .Where(e => e.Name.LocalName == "Capability" && e.Name.NamespaceName.StartsWith(RestrictedCapabilities, StringComparison.Ordinal))
            .Select(e => (string?)e.Attribute("Name") ?? "")
            .ToList();
        Assert.Equal(new[] { "runFullTrust" }, restricted);
    }

    [Fact]
    public void The_window_never_restarts_elevated_inside_the_package()
    {
        // The window's two ways of starting itself elevated refuse first thing there, whoever calls them. The helper's
        // own route (IndexHelper.Start, with the helper's switch only, never a window) is checked by the tests below.
        string program = File.ReadAllText(Path.Combine(RepoRoot(), "src", "Anything", "Program.cs"));
        foreach (string helper in new[] { "RelaunchElevated", "RunElevatedAndWait" })
            Assert.Matches(new Regex(@"public static \w+ " + helper + @"\(IEnumerable<string> args\)\s*\{\s*if \(StorePackage\.IsInstalled\) return "), program);
    }

    // ---- the Store copy's index helper (src/Anything/IndexHelper.cs) ----

    static string Source(params string[] path) => File.ReadAllText(Path.Combine([RepoRoot(), "src", "Anything", .. path]));

    /// <summary>A method's text, from its signature to the brace that closes it at the class's indentation.</summary>
    static string Method(string source, string signature)
    {
        int start = source.IndexOf(signature, StringComparison.Ordinal);
        Assert.True(start >= 0, "not found: " + signature);
        int end = source.IndexOf("\n    }", start, StringComparison.Ordinal);
        Assert.True(end > start, "no end for: " + signature);
        return source[start..end];
    }

    [Fact]
    public void The_helper_starts_through_shell_AppsFolder_with_runas_in_a_method_of_its_own()
    {
        string start = Method(Source("IndexHelper.cs"), "public static StartResult Start(int parentPid, int run)");
        Assert.Contains(@"@""shell:AppsFolder\"" + family + ""!"" + AppId", start);
        Assert.Contains("StorePackage.FamilyName", start);
        Assert.Contains(@"Verb = ""runas""", start);
        Assert.Contains("UseShellExecute = true", start);
        Assert.Contains(@"""-"" + Switch", start); // with the helper's switch, so what starts is the helper and never a window
        Assert.Contains(@"""-"" + RunSwitch, Number(run)", start); // and its start's number: its own stop event
        // Not the routes that weren't tried: the app alias, the program file.
        foreach (string other in new[] { "AliasPath", "LaunchPath", "ProcessPath" }) Assert.DoesNotContain(other, start);
        // Declined is told apart (remembered for the run, no second prompt).
        Assert.Contains("ERROR_CANCELLED", start);
    }

    [Fact]
    public void Only_the_helper_is_ever_started_through_shell_AppsFolder()
    {
        var users = Directory.EnumerateFiles(Path.Combine(RepoRoot(), "src", "Anything"), "*.cs", SearchOption.AllDirectories)
            .Where(f => !f.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar) && !f.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar))
            .Where(f => File.ReadAllText(f).Contains("shell:AppsFolder", StringComparison.OrdinalIgnoreCase))
            .Select(f => Path.GetFileName(f)!).Order(StringComparer.Ordinal).ToArray();
        // StorePackage.cs only names it in its summary.
        Assert.Equal(new[] { "IndexHelper.cs", "StorePackage.cs" }, users);
        Assert.Single(Regex.Matches(Source("IndexHelper.cs"), "shell:AppsFolder\\\\\" \\+").Cast<Match>());
        // The elevated relaunch and run-and-wait never take that route.
        string program = Source("Program.cs");
        Assert.DoesNotContain("IndexHelper.Start", Method(program, "public static bool RelaunchElevated("));
        Assert.DoesNotContain("IndexHelper.Start", Method(program, "public static int RunElevatedAndWait("));
    }

    [Fact]
    public void The_helper_is_started_with_the_app_s_Id_in_the_manifest()
    {
        XNamespace ns = "http://schemas.microsoft.com/appx/manifest/foundation/windows10";
        string id = (string)Manifest().Descendants(ns + "Application").Single().Attribute("Id")!;
        Assert.Contains($@"const string AppId = ""{id}"";", Source("IndexHelper.cs"));
    }

    [Fact]
    public void The_helper_mode_is_handled_before_anything_else_starts()
    {
        string program = Source("Program.cs");
        int helper = program.IndexOf("if (IndexHelper.IsHelperLaunch(args)) return IndexHelper.Run(args);", StringComparison.Ordinal);
        Assert.True(helper > 0, "Program.Main doesn't hand the helper's command line to IndexHelper.Run");
        // Before Setup, the Store's startup, the command line, the single-instance mutex and every window.
        foreach (string later in new[] { "Setup.IsSetupLaunch(args)", "StorePackage.StartedWithWindows()", "CommandLine.Parse(args)",
                     "new Mutex(", "ApplicationConfiguration.Initialize()", "AnythingApp.Create(" })
        {
            int at = program.IndexOf(later, StringComparison.Ordinal);
            Assert.True(at > helper, $"{later} comes before the helper's mode");
        }
    }

    [Fact]
    public void The_helper_runs_only_inside_the_package_and_only_elevated()
    {
        string run = Method(Source("IndexHelper.cs"), "public static int Run(IReadOnlyList<string> args)");
        Assert.Matches(new Regex(@"\{\s*if \(!StorePackage\.IsInstalled\) return ERROR_NOT_SUPPORTED;\s*if \(!Program\.IsElevated\(\)\) return ERROR_ACCESS_DENIED;"), run);
        // For this package's Rock Preview only, and never beside a service Setup installed (the pipe's name is its own).
        Assert.Contains("OpenRockPreview(pid)", run);
        int service = run.IndexOf("if (IndexService.IsInstalled()) return 0;", StringComparison.Ordinal);
        int serves = run.IndexOf("VolumeService.Run(", StringComparison.Ordinal);
        Assert.True(service > 0 && serves > service, "the helper serves without looking for an installed service first");
        // It stops with its Rock Preview, or at its own start's stop event: both end the token it serves with (the
        // end-watch itself is tested in HelperEndTests).
        Assert.Contains("StopEventName(pid, run)", run);
        Assert.Contains("WaitHandle[] ends = [parent, stop];", run);
        Assert.Contains("using var cts = VolumeService.CancelWhenAny(ends);", run);
        Assert.Contains("VolumeService.Run([me.User!], cts.Token, asElevatedHelper: true);", run);
    }

    [Fact]
    public void Each_start_of_the_helper_has_a_stop_event_of_its_own()
    {
        // One per start, made new and never reset: a late helper of an overtaken start finds its own event set or gone.
        string app = Source("AnythingApp.IndexHelper.cs");
        Assert.Contains("IndexHelper.StopEventName(Environment.ProcessId, run), out bool made)", app);
        Assert.Contains("IndexHelper.Start(Environment.ProcessId, run)", app);
        Assert.DoesNotContain(".Reset()", app);
        string stop = Method(app, "void StopIndexHelper()");
        Assert.Contains("Interlocked.Exchange(ref _helperStop, null)", stop);
        int set = stop.IndexOf("stop.Set();", StringComparison.Ordinal), close = stop.IndexOf("stop.Dispose();", StringComparison.Ordinal);
        Assert.True(set > 0 && close > set, "the stop event isn't set, then closed");
    }

    [Fact]
    public void With_the_switch_on_every_state_of_the_helper_offers_Turn_off()
    {
        // A declined or failed start asks again at every start until the switch is off: Turn off is always there.
        string card = Method(Source("SettingsForm.cs"), "(string Status, Control? Button) StoreInstantIndexing(");
        int states = card.IndexOf("return _app.HelperState switch", StringComparison.Ordinal);
        Assert.True(states > 0);
        string arms = card[states..];
        foreach (string state in new[] { "Declined", "Stopped", "InUse" })
            Assert.Matches(new Regex(@"IndexHelperState\." + state + @" => \(""[^""]+"", AskAgainOrTurnOff\(\)\)"), arms);
        Assert.Matches(new Regex(@"IndexHelperState\.Starting => \(""[^""]+"", Do\(""Turn off"", _app\.TurnOffIndexHelper\)\)"), arms);
        Assert.Contains(@"Do(""Turn off"", _app.TurnOffIndexHelper)", card[card.IndexOf("Control AskAgainOrTurnOff()", StringComparison.Ordinal)..states]);
        // The reading helper too.
        Assert.Contains(@"Do(""Turn off"", _app.TurnOffIndexHelper));", card[..states]);
    }

    [Fact]
    public void The_first_start_plans_its_drives_once_Windows_has_answered_and_restarts_off_the_UI_thread()
    {
        // Held before the index's first start, when the helper will be asked for as the first window opens.
        string ctor = Source("AnythingApp.cs");
        int hold = ctor.IndexOf("HoldPlanningForHelper(cmd);", StringComparison.Ordinal);
        int start = ctor.IndexOf("try { IndexManager.Start(Settings.Index); }", StringComparison.Ordinal);
        Assert.True(hold > 0 && start > hold, "the index starts before its planning is held for the helper");
        string app = Source("AnythingApp.IndexHelper.cs");
        Assert.Contains("IndexManager.PlanAfter(_planWait.Task);", Method(app, "void HoldPlanningForHelper(CommandLine cmd)"));
        Assert.Contains("StartsInTray(cmd)", Method(app, "void HoldPlanningForHelper(CommandLine cmd)"));
        // Whether the run planned is read before it is let plan: a run that waited needs no new start.
        string startHelper = Method(app, "async void StartIndexHelper()");
        int planned = startHelper.LastIndexOf("bool planned = IndexManager.HasPlanned;", StringComparison.Ordinal);
        int released = startHelper.LastIndexOf("ReleasePlanning();", StringComparison.Ordinal);
        Assert.True(planned > 0 && released > planned);
        // A restart waits for the run under way and saves: never on the UI thread.
        Assert.Contains("await Task.Run(() => IndexManager.Start(Settings.Index));", Method(app, "async Task RestartIndexing()"));
        Assert.DoesNotContain("void RestartIndexing()", app);
    }

    [Fact]
    public void The_Store_bar_stays_away_once_dismissed_or_turned_off()
    {
        string settings = Source("Settings.cs");
        Assert.Contains(@"s.StoreIndexingBarDismissed = Bool(""store_indexing_bar_dismissed"", false);", settings);
        Assert.Contains(@"B(""store_indexing_bar_dismissed"", StoreIndexingBarDismissed);", settings);
        Assert.Contains("!_app.Settings.StoreIndexingBarDismissed", Method(Source("MainForm.cs"), "void UpdateInfoBar()"));
        Assert.Contains("Settings.StoreIndexingBarDismissed = true;", Method(Source("AnythingApp.IndexHelper.cs"), "public async void TurnOffIndexHelper()"));
    }

    [Fact]
    public void The_helper_is_off_unless_turned_on_and_asks_only_once_a_window_shows()
    {
        string settings = Source("Settings.cs");
        Assert.Contains(@"s.StoreInstantIndexing = Bool(""store_instant_indexing"", false);", settings);
        Assert.Contains(@"B(""store_instant_indexing"", StoreInstantIndexing);", settings);
        // Started from a window being shown, never at a start in the tray (the startup task, -startup), which shows none.
        string app = Source("AnythingApp.cs");
        Assert.Single(Regex.Matches(app, @"Post\(StartIndexHelperOnce\)").Cast<Match>());
        Assert.Contains("Post(StartIndexHelperOnce)", Method(app, "public void ShowWindow(MainForm w)"));
        string once = Method(Source("AnythingApp.IndexHelper.cs"), "void StartIndexHelperOnce()");
        Assert.Contains("OffersIndexHelper && Settings.StoreInstantIndexing", once);
        // Not in the GitHub copy, not in the demo, not when the window already runs as administrator, and only for an
        // administrator's account, which Windows can elevate as itself.
        string helper = Source("AnythingApp.IndexHelper.cs");
        Assert.Contains("public bool OffersIndexHelper => StoreCopyScans && IndexHelper.UserCanElevate;", helper);
        Assert.Contains("bool StoreCopyScans => StorePackage.IsInstalled && !IsDemo && !IndexManager.IsElevated;", helper);
        Assert.Contains("type == TokenElevationTypeLimited", Source("IndexHelper.cs"));
    }

    [Fact]
    public void An_elevated_Store_copy_with_nothing_to_do_while_one_runs_ends_quietly()
    {
        // Should Windows drop the helper's arguments: no second, elevated window, nothing forwarded to the running one.
        string program = Source("Program.cs");
        int owned = program.IndexOf("if (!owned)", StringComparison.Ordinal);
        int quiet = program.IndexOf("if (StorePackage.IsInstalled && args.Length == 0 && IsElevated()) return 0;", StringComparison.Ordinal);
        int forward = program.IndexOf("Forward(hwnd, cmd.ToArgs())", StringComparison.Ordinal);
        Assert.True(owned > 0 && quiet > owned && forward > quiet);
    }

    [Fact]
    public void The_window_s_own_route_to_administrator_rights_stays_hidden_in_the_Store_copy()
    {
        // Tools › Run as Administrator, the -admin switch and the restart behind them: none of them in the package.
        Assert.Contains("bool OffersRunAsAdmin => !StorePackage.IsInstalled && ", Source("MainForm.cs"));
        Assert.Contains("if (cmd.Admin && !IsElevated() && !StorePackage.IsInstalled)", Source("Program.cs"));
        Assert.Matches(new Regex(@"public void RestartElevated\(\)\s*\{\s*if \(StorePackage\.IsInstalled\) return;"), Source("AnythingApp.cs"));
    }
}
