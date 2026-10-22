using System.Globalization;
using SortKey = Anything.Core.SortKey;
using System.Text;
using Anything.Core;
using Anything.Core.Indexing;
using Microsoft.Win32;

namespace Anything;

internal sealed class ColumnSetting
{
    public bool Visible { get; set; }
    /// <summary>Width in 96-DPI logical pixels.</summary>
    public int Width { get; set; }
    /// <summary>Left-to-right display position.</summary>
    public int Position { get; set; }
}

internal sealed class Bookmark
{
    public string Name { get; set; } = "";
    public string Query { get; set; } = "";
    public string Filter { get; set; } = "";
    public SortKey Sort { get; set; }
    public bool Descending { get; set; }
    public bool MatchCase { get; set; }
    public bool MatchWholeWord { get; set; }
    public bool MatchPath { get; set; }
    public bool MatchDiacritics { get; set; }
    public bool Regex { get; set; }
    /// <summary>Plain words also look inside files (Search › Match inside files); null = leave the window's toggle as it is.</summary>
    public bool? MatchContent { get; set; }
}

/// <summary>Application settings persisted as a simple key=value ini in %APPDATA%\Anything\Anything.ini.</summary>
internal sealed partial class AppSettings
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string RunValue = "Rock Preview";

    public string FilePath { get; private set; } = "";

    // Window
    public Rectangle WindowBounds { get; set; } = Rectangle.Empty;
    public bool Maximized { get; set; }
    public bool AlwaysOnTop { get; set; }

    // Results
    public Dictionary<string, ColumnSetting> Columns { get; } = new(StringComparer.OrdinalIgnoreCase);
    public SortKey Sort { get; set; } = SortKey.Name;
    public bool SortDescending { get; set; }

    // Search
    public bool MatchCase { get; set; }
    public bool MatchWholeWord { get; set; }
    public bool MatchPath { get; set; }
    public bool MatchDiacritics { get; set; }
    public bool Regex { get; set; }
    public string Filter { get; set; } = "";

    // UI
    public bool FiltersVisible { get; set; }
    public bool StatusBarVisible { get; set; } = true;
    public string FontName { get; set; } = "";
    public float FontSize { get; set; }
    public int Zoom { get; set; }
    public int LastExportType { get; set; }
    /// <summary>"light" (default, the classic Everything look), "dark", or "system" (follow Windows). Applied at startup.</summary>
    public string Theme { get; set; } = "light";

    public static SystemColorMode ColorModeFor(string theme) => theme switch
    {
        "dark" => SystemColorMode.Dark,
        "system" => SystemColorMode.System,
        _ => SystemColorMode.Classic,
    };

    /// <summary>
    /// With nothing selected, typing while a folder is open searches only that folder (Search options); off = everywhere.
    /// Folders selected in the view always narrow the search to themselves.
    /// </summary>
    public bool ScopeSearchToFolder { get; set; }
    /// <summary>ZAP includes files in subfolders (otherwise only the folder's own files).</summary>
    public bool ZapIncludeSubfolders { get; set; } = true;
    /// <summary>"Every file in" shows only everyday files (pictures, videos, music, documents, design files...), not code or system files.</summary>
    public bool EverydayFilesOnly { get; set; } = true;
    /// <summary>Times the Thumbnails button has pulsed to show itself (a folder open for 3 seconds); it stops at 10.</summary>
    public int ThumbnailsHintCount { get; set; }
    /// <summary>Thumbnails has been used, so its button no longer pulses.</summary>
    public bool ThumbnailsHintDone { get; set; }
    /// <summary>Explorer's navigation pane (Quick access, This PC, drives) in the browse view.</summary>
    public bool ExplorerNavPane { get; set; } = true;
    /// <summary>Navigation pane width in 96-DPI pixels.</summary>
    public int NavPaneWidth { get; set; } = 176;
    /// <summary>Last browsed location (parsing name), used by Browse &gt; Last Folder.</summary>
    public string LastLocation { get; set; } = "";

    /// <summary>Preview pane beside the results (View &gt; Preview, Alt+P).</summary>
    public bool PreviewVisible { get; set; } = true;
    /// <summary>Preview pane width in 96-DPI pixels.</summary>
    public int PreviewWidth { get; set; } = 460;
    /// <summary>0 = details list; otherwise the thumbnail edge in pixels for the thumbnail grid.</summary>
    public int ThumbnailSize { get; set; }

    // General
    public bool RunInBackground { get; set; } = true;
    public bool ShowTrayIcon { get; set; } = true;
    public bool StartWithWindows { get; set; }
    /// <summary>Win+E and opening folders use Rock Preview instead of File Explorer (kept in the registry, not here).</summary>
    public bool UseInsteadOfExplorer { get; set; }
    /// <summary>Folders opened from Windows (and Win+E) open as a new tab in the window used last; off = a new window each.</summary>
    public bool OpenFoldersInTabs { get; set; } = true;
    /// <summary>Global hotkey that shows or hides Rock Preview from any app; empty = none.</summary>
    public string Hotkey { get; set; } = DefaultHotkey;
    public const string DefaultHotkey = "Alt+X";
    /// <summary>The window that shows the hotkey and asks to try it was shown (once per user, after installing).</summary>
    public bool WelcomeShown { get; set; }
    /// <summary>Count the files opened from the results (Run Count, Date Run), kept in "Run History.csv" like Everything's.</summary>
    public bool RunHistoryEnabled { get; set; } = true;

    // New versions
    /// <summary>Ask GitHub at most once a day whether a newer Rock Preview is out (Options &gt; General, Setup).</summary>
    public bool CheckForUpdates { get; set; } = true;
    /// <summary>When the last check got an answer (UTC).</summary>
    public DateTime LastUpdateCheck { get; set; }
    /// <summary>The latest version that check found, kept so the notice shows at the next start even offline.</summary>
    public string LatestVersion { get; set; } = "";
    /// <summary>The version a Windows notification was shown for: once per version.</summary>
    public string NotifiedVersion { get; set; } = "";

    // Drives left out
    /// <summary>
    /// Drive roots ("C:\") that are on Folders › Leave these folders out and that the user chose to keep left out:
    /// the window's "Nothing on C: shows up in searches" bar no longer asks about them.
    /// </summary>
    public List<string> LeftOutDrivesKept { get; set; } = [];

    public IndexSettings Index { get; } = new();
    public List<Bookmark> Bookmarks { get; } = [];

    public static string DefaultPath(bool demo) => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rock Preview", demo ? "RockPreview-demo.ini" : "RockPreview.ini");

    public static AppSettings Load(string path)
    {
        var s = new AppSettings { FilePath = path };
        foreach (var c in Anything.Columns.All)
            s.Columns[c.Key] = new ColumnSetting { Visible = c.DefaultVisible, Width = c.DefaultWidth, Position = c.DefaultPosition };

        Dictionary<string, string> v = new(StringComparer.OrdinalIgnoreCase);
        try
        {
            if (File.Exists(path))
            {
                foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
                {
                    var line = raw.Trim();
                    if (line.Length == 0 || line[0] is ';' or '#' or '[') continue;
                    int eq = line.IndexOf('=');
                    if (eq <= 0) continue;
                    v[line[..eq].Trim()] = line[(eq + 1)..];
                }
            }
        }
        catch { /* unreadable ini: defaults */ }

        string Str(string k, string def) => v.TryGetValue(k, out var x) ? x : def;
        bool Bool(string k, bool def) => v.TryGetValue(k, out var x) ? x.Trim() is "1" or "true" or "True" : def;
        int Int(string k, int def) => v.TryGetValue(k, out var x) && int.TryParse(x.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int i) ? i : def;
        float Float(string k, float def) => v.TryGetValue(k, out var x) && float.TryParse(x.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out float f) ? f : def;
        List<string> List(string k, List<string> def) => v.TryGetValue(k, out var x) ? x.Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList() : def;

        int w = Int("window_wide", 0), h = Int("window_high", 0);
        if (w > 0 && h > 0) s.WindowBounds = new Rectangle(Int("window_x", 0), Int("window_y", 0), w, h);
        s.Maximized = Bool("maximized", false);
        s.AlwaysOnTop = Bool("ontop", false);

        foreach (var c in Anything.Columns.All)
        {
            var cs = s.Columns[c.Key];
            cs.Visible = c.Key == "name" || Bool(c.Key + "_column_visible", cs.Visible);
            cs.Width = Math.Clamp(Int(c.Key + "_column_width", cs.Width), 20, 4000);
            cs.Position = Int(c.Key + "_column_pos", cs.Position);
        }
        if (Enum.TryParse(Str("sort", ""), true, out SortKey sk)) s.Sort = sk;
        s.SortDescending = !Bool("sort_ascending", true);

        s.MatchCase = Bool("match_case", false);
        s.MatchWholeWord = Bool("match_whole_word", false);
        s.MatchPath = Bool("match_path", false);
        s.MatchDiacritics = Bool("match_diacritics", false);
        s.Regex = Bool("match_regex", false);
        s.Filter = Str("filter", "");

        s.FiltersVisible = Bool("filters_visible", false);
        s.StatusBarVisible = Bool("status_bar_visible", true);
        s.FontName = Str("font", "");
        s.FontSize = Float("font_size", 0);
        s.Zoom = Math.Clamp(Int("zoom", 0), -6, 20);
        s.LastExportType = Int("last_export_type", 0);
        s.Theme = Str("theme", "light").Trim().ToLowerInvariant() is "dark" or "system" ? Str("theme", "").Trim().ToLowerInvariant() : "light";
        s.PreviewVisible = Bool("preview_visible", true);
        s.ScopeSearchToFolder = Bool("scope_search_to_folder", false);
        s.ZapIncludeSubfolders = Bool("zap_include_subfolders", true);
        s.EverydayFilesOnly = Bool("everyday_files_only", true);
        s.ThumbnailsHintCount = Math.Max(0, Int("thumbnails_hint_count", 0));
        // Settings saved before the hint have no key. Results left as thumbnails say thumbnails were used; nothing else
        // does, so everyone else still gets the hint, upgrading or not.
        s.ThumbnailsHintDone = Bool("thumbnails_hint_done", Int("thumbnail_size", 0) > 0);
        s.ExplorerNavPane = Bool("explorer_nav_pane", true);
        s.NavPaneWidth = Math.Clamp(Int("nav_pane_width", 176), 120, 1200);
        if (s.NavPaneWidth == 250) s.NavPaneWidth = 176; // the old default; Explorer's pane is 176 (220px at 125%)
        s.LastLocation = Str("last_location", "");
        s.PreviewWidth = Math.Clamp(Int("preview_width", 460), 150, 4000);
        int thumb = Int("thumbnail_size", 0);
        s.ThumbnailSize = thumb <= 0 ? 0 : Math.Clamp(thumb, 48, 512);

        s.RunInBackground = Bool("run_in_background", true);
        s.ShowTrayIcon = Bool("show_tray_icon", true);
        s.OpenFoldersInTabs = Bool("open_folders_in_tabs", true);
        // "none" = turned off in Options; missing or empty (what versions before Alt+X saved) = the default.
        string hotkey = Str("hotkey", "").Trim();
        s.Hotkey = hotkey.Equals("none", StringComparison.OrdinalIgnoreCase) ? "" : hotkey.Length == 0 ? DefaultHotkey : hotkey;
        s.WelcomeShown = Bool("welcome_shown", false);
        s.RunHistoryEnabled = Bool("run_history_enabled", true);
        s.CheckForUpdates = Bool("check_for_updates", true);
        s.LastUpdateCheck = DateTime.TryParse(Str("last_update_check", ""), CultureInfo.InvariantCulture,
            DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var checkedAt) ? checkedAt : default;
        s.LatestVersion = Str("latest_version", "").Trim();
        s.NotifiedVersion = Str("notified_version", "").Trim();
        s.LeftOutDrivesKept = List("left_out_drive_notice_dismissed", s.LeftOutDrivesKept);
        s.StartWithWindows = ReadStartWithWindows();
        s.UseInsteadOfExplorer = ExplorerReplacement.IsEnabled();

        var ix = s.Index;
        ix.IndexNtfsVolumes = Bool("index_ntfs_volumes", ix.IndexNtfsVolumes);
        ix.Volumes = List("volumes", ix.Volumes);
        ix.SkipDrives = List("skip_drives", ix.SkipDrives);
        if (ix.Volumes.Count > 0 && !v.ContainsKey("skip_drives")) MigrateVolumes(ix);
        ix.IndexRemovableDrives = Bool("index_removable_drives", ix.IndexRemovableDrives);
        ix.IndexNetworkDrives = Bool("index_network_drives", ix.IndexNetworkDrives);
        ix.NetworkRescanMinutes = Math.Max(0, Int("network_rescan_minutes", ix.NetworkRescanMinutes));
        ix.Folders = List("folders", ix.Folders);
        ix.ExcludeFolders = List("exclude_folders", ix.ExcludeFolders);
        ix.ExcludePatterns = List("exclude_patterns", ix.ExcludePatterns);
        ix.ExcludeHidden = Bool("exclude_hidden", ix.ExcludeHidden);
        ix.ExcludeSystem = Bool("exclude_system", ix.ExcludeSystem);
        // Everything's ini names for the same switches.
        ix.IndexDateAccessed = Bool("index_date_accessed", ix.IndexDateAccessed);
        ix.IndexFolderSizes = Bool("index_folder_size", ix.IndexFolderSizes);
        ix.IndexRecentChanges = Bool("index_recent_changes", ix.IndexRecentChanges);
        ix.RecentChangesFromJournal = Bool("load_recent_changes", ix.RecentChangesFromJournal);
        if (Str("database_path", "") is { Length: > 0 } db) ix.DatabasePath = db;
        LoadContent(s, v);

        int count = Int("bookmark_count", 0);
        for (int i = 0; i < count; i++)
        {
            string p = $"bookmark{i}_";
            var b = new Bookmark
            {
                Name = Str(p + "name", ""),
                Query = Str(p + "query", ""),
                Filter = Str(p + "filter", ""),
                Descending = Bool(p + "descending", false),
                MatchCase = Bool(p + "match_case", false),
                MatchWholeWord = Bool(p + "match_whole_word", false),
                MatchPath = Bool(p + "match_path", false),
                MatchDiacritics = Bool(p + "match_diacritics", false),
                Regex = Bool(p + "match_regex", false),
                MatchContent = v.ContainsKey(p + "match_content") ? Bool(p + "match_content", false) : null,
            };
            if (Enum.TryParse(Str(p + "sort", ""), true, out SortKey bs)) b.Sort = bs;
            if (b.Name.Length > 0) s.Bookmarks.Add(b);
        }
        return s;
    }

    public void Save()
    {
        var sb = new StringBuilder();
        void W(string k, object? val) => sb.Append(k).Append('=').Append(Convert.ToString(val, CultureInfo.InvariantCulture)).Append("\r\n");
        void B(string k, bool val) => W(k, val ? 1 : 0);
        sb.Append("[Rock Preview]\r\n");
        if (!WindowBounds.IsEmpty)
        {
            W("window_x", WindowBounds.X); W("window_y", WindowBounds.Y);
            W("window_wide", WindowBounds.Width); W("window_high", WindowBounds.Height);
        }
        B("maximized", Maximized);
        B("ontop", AlwaysOnTop);
        foreach (var (key, c) in Columns)
        {
            B(key + "_column_visible", c.Visible);
            W(key + "_column_width", c.Width);
            W(key + "_column_pos", c.Position);
        }
        W("sort", Sort);
        B("sort_ascending", !SortDescending);
        B("match_case", MatchCase);
        B("match_whole_word", MatchWholeWord);
        B("match_path", MatchPath);
        B("match_diacritics", MatchDiacritics);
        B("match_regex", Regex);
        W("filter", Filter);
        B("filters_visible", FiltersVisible);
        W("theme", Theme);
        B("preview_visible", PreviewVisible);
        B("scope_search_to_folder", ScopeSearchToFolder);
        B("zap_include_subfolders", ZapIncludeSubfolders);
        B("everyday_files_only", EverydayFilesOnly);
        W("thumbnails_hint_count", ThumbnailsHintCount);
        B("thumbnails_hint_done", ThumbnailsHintDone);
        B("explorer_nav_pane", ExplorerNavPane);
        W("nav_pane_width", NavPaneWidth);
        W("last_location", Clean(LastLocation));
        W("preview_width", PreviewWidth);
        W("thumbnail_size", ThumbnailSize);
        B("status_bar_visible", StatusBarVisible);
        W("font", FontName);
        W("font_size", FontSize);
        W("zoom", Zoom);
        W("last_export_type", LastExportType);
        B("run_in_background", RunInBackground);
        B("show_tray_icon", ShowTrayIcon);
        B("open_folders_in_tabs", OpenFoldersInTabs);
        W("hotkey", Hotkey.Length == 0 ? "none" : Hotkey);
        B("welcome_shown", WelcomeShown);
        B("run_history_enabled", RunHistoryEnabled);
        B("check_for_updates", CheckForUpdates);
        if (LastUpdateCheck != default) W("last_update_check", LastUpdateCheck.ToUniversalTime().ToString("o", CultureInfo.InvariantCulture));
        W("latest_version", Clean(LatestVersion));
        W("notified_version", Clean(NotifiedVersion));
        W("left_out_drive_notice_dismissed", string.Join('|', LeftOutDrivesKept));
        B("index_ntfs_volumes", Index.IndexNtfsVolumes);
        W("volumes", string.Join('|', Index.Volumes));
        W("skip_drives", string.Join('|', Index.SkipDrives));
        B("index_removable_drives", Index.IndexRemovableDrives);
        B("index_network_drives", Index.IndexNetworkDrives);
        W("network_rescan_minutes", Index.NetworkRescanMinutes);
        W("folders", string.Join('|', Index.Folders));
        W("exclude_folders", string.Join('|', Index.ExcludeFolders));
        W("exclude_patterns", string.Join('|', Index.ExcludePatterns));
        B("exclude_hidden", Index.ExcludeHidden);
        B("exclude_system", Index.ExcludeSystem);
        B("index_date_accessed", Index.IndexDateAccessed);
        B("index_folder_size", Index.IndexFolderSizes);
        B("index_recent_changes", Index.IndexRecentChanges);
        B("load_recent_changes", Index.RecentChangesFromJournal);
        SaveContent(sb);
        W("bookmark_count", Bookmarks.Count);
        for (int i = 0; i < Bookmarks.Count; i++)
        {
            var b = Bookmarks[i];
            string p = $"bookmark{i}_";
            W(p + "name", Clean(b.Name));
            W(p + "query", Clean(b.Query));
            W(p + "filter", Clean(b.Filter));
            W(p + "sort", b.Sort);
            B(p + "descending", b.Descending);
            B(p + "match_case", b.MatchCase);
            B(p + "match_whole_word", b.MatchWholeWord);
            B(p + "match_path", b.MatchPath);
            B(p + "match_diacritics", b.MatchDiacritics);
            B(p + "match_regex", b.Regex);
            if (b.MatchContent is bool mc) B(p + "match_content", mc);
        }
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            string tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, sb.ToString(), new UTF8Encoding(false));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch { /* best effort */ }
    }

    static string Clean(string s) => s.Replace('\r', ' ').Replace('\n', ' ');

    /// <summary>
    /// 1.4.0 and earlier kept the drives that are switched on ("only these"); now Settings keeps the ones switched off,
    /// so drives that come later (a USB stick, a network drive) are searched too. Converted once: every fixed drive
    /// there is now and that wasn't on the list is turned off. The old list stays: the index still applies it to fixed
    /// drives, so a disk that was off and isn't plugged in now (an external disk Windows calls fixed) stays off too.
    /// </summary>
    static void MigrateVolumes(IndexSettings ix)
    {
        try
        {
            // Runs while the settings load (on the UI thread): drive types only, and a look at the fixed drives alone
            // (to leave out SUBST drives), never the network or the volumes mounted in folders.
            var skip = new List<string>(ix.SkipDrives);
            uint mask = Drives.LetterMask();
            for (int i = 0; i < 26; i++)
            {
                if ((mask & (1u << i)) == 0) continue;
                string root = (char)('A' + i) + @":\";
                if (Drives.KindOf(root) != DriveKind.Fixed || ix.Volumes.Any(v => SamePath(v, root)) || skip.Any(v => SamePath(v, root))) continue;
                if (Drives.Describe(root).SubstTarget != null) continue;
                skip.Add(root);
            }
            ix.SkipDrives = skip;
        }
        catch (Exception) { /* the old list alone: the index still honours it */ }
    }

    /// <summary>Two paths name the same folder or drive: case and a trailing backslash don't matter ("C:" = "c:\").</summary>
    public static bool SamePath(string a, string b) => string.Equals(TrimPath(a), TrimPath(b), StringComparison.OrdinalIgnoreCase);

    /// <summary><paramref name="path"/> is <paramref name="folder"/> or inside it.</summary>
    public static bool IsUnder(string path, string folder)
    {
        string p = TrimPath(path), f = TrimPath(folder);
        return p.Equals(f, StringComparison.OrdinalIgnoreCase)
               || (f.Length > 0 && p.Length > f.Length && p.StartsWith(f, StringComparison.OrdinalIgnoreCase) && p[f.Length] == '\\');
    }

    static string TrimPath(string path) => path.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');

    /// <summary>
    /// The entries of Folders › Leave these folders out that leave out all of a drive (<paramref name="root"/>, "C:\"):
    /// the drive itself, or a folder it is mounted in.
    /// </summary>
    public List<string> ExclusionsHidingDrive(string root) =>
        [.. Index.ExcludeFolders.Where(f => IndexPlan.IsLeftOut(new ExclusionFilter(new IndexSettings { ExcludeFolders = [f] }), root))];

    /// <summary>
    /// Shows a drive that Folders › Leave these folders out hides: the drive itself comes off that list. When what hides
    /// it is a folder it is mounted in (C:\VMs, for a disk at C:\VMs\Disk2), that folder stays left out and the drive
    /// goes on Also search these folders instead, which are searched even inside folders left out. False when nothing
    /// on the list hides it.
    /// </summary>
    public bool IncludeDrive(string root)
    {
        var hiding = ExclusionsHidingDrive(root);
        if (hiding.Count == 0) return false;
        bool changed = false;
        if (hiding.Any(f => SamePath(f, root)))
        {
            Index.ExcludeFolders = [.. Index.ExcludeFolders.Where(f => !SamePath(f, root))];
            changed = true;
        }
        if (hiding.Any(f => !SamePath(f, root)) && !IsSearchedAsFolder(root))
        {
            Index.Folders = [.. Index.Folders, root.Length > 3 ? root.TrimEnd('\\') : root];
            changed = true;
        }
        return changed;
    }

    /// <summary>The drive (<paramref name="root"/>) is on Folders › Also search these folders itself, so it is searched even while left out.</summary>
    public bool IsSearchedAsFolder(string root) => Index.Folders.Any(f => SamePath(f, root));

    public bool IsLeftOutDriveKept(string root) => LeftOutDrivesKept.Any(r => SamePath(r, root));

    /// <summary>The user wants <paramref name="root"/> left out: the window's bar stops asking about it.</summary>
    public void KeepDriveLeftOut(string root)
    {
        if (!IsLeftOutDriveKept(root)) LeftOutDrivesKept.Add(root);
    }

    /// <summary>
    /// One-time move of settings, index and the startup entry from the app's former name ("Anything") so the
    /// rename keeps everything. Never touches the separate older RockPreview app's data or startup entry.
    /// </summary>
    public static void MigrateFromAnything(bool demo)
    {
        try
        {
            string roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            MoveIfMissing(Path.Combine(roaming, "Anything", demo ? "Anything-demo.ini" : "Anything.ini"), DefaultPath(demo));
            if (demo) return;
            MoveIfMissing(Path.Combine(local, "Anything", "Anything.db"), new IndexSettings().DatabasePath);
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue("Anything") is string)
            {
                key.DeleteValue("Anything", false);
                key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" -startup");
            }
        }
        catch { /* best effort: worst case the index is rebuilt */ }
    }

    static void MoveIfMissing(string from, string to)
    {
        if (!File.Exists(from) || File.Exists(to)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(to)!);
        File.Move(from, to);
        try
        {
            string dir = Path.GetDirectoryName(from)!;
            if (!Directory.EnumerateFileSystemEntries(dir).Any()) Directory.Delete(dir);
        }
        catch { }
    }

    /// <summary>Turns "Use Rock Preview instead of File Explorer" on or off for this user, when the choice changed.</summary>
    public void ApplyExplorerReplacement()
    {
        try
        {
            bool on = ExplorerReplacement.IsEnabled();
            if (UseInsteadOfExplorer && !on && Environment.ProcessPath is { } exe) ExplorerReplacement.Enable(exe);
            else if (!UseInsteadOfExplorer && on) ExplorerReplacement.Disable();
        }
        catch (Exception) { }
        UseInsteadOfExplorer = ExplorerReplacement.IsEnabled();
    }

    static bool ReadStartWithWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(RunValue) is string;
        }
        catch { return false; }
    }

    public void ApplyStartWithWindows()
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey);
            if (StartWithWindows)
                key.SetValue(RunValue, $"\"{Environment.ProcessPath}\" -startup");
            else if (key.GetValue(RunValue) is not null)
                key.DeleteValue(RunValue, false);
        }
        catch { }
    }
}
