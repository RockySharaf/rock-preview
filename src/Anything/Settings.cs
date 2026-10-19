using System.Globalization;
using SortKey = Anything.Core.SortKey;
using System.Text;
using Anything.Core;
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
}

/// <summary>Application settings persisted as a simple key=value ini in %APPDATA%\Anything\Anything.ini.</summary>
internal sealed class AppSettings
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
    /// <summary>Global hotkey that shows or hides Rock Preview from any app; empty = none.</summary>
    public string Hotkey { get; set; } = DefaultHotkey;
    public const string DefaultHotkey = "Alt+X";

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
        s.ExplorerNavPane = Bool("explorer_nav_pane", true);
        s.NavPaneWidth = Math.Clamp(Int("nav_pane_width", 176), 120, 1200);
        if (s.NavPaneWidth == 250) s.NavPaneWidth = 176; // the old default; Explorer's pane is 176 (220px at 125%)
        s.LastLocation = Str("last_location", "");
        s.PreviewWidth = Math.Clamp(Int("preview_width", 460), 150, 4000);
        int thumb = Int("thumbnail_size", 0);
        s.ThumbnailSize = thumb <= 0 ? 0 : Math.Clamp(thumb, 48, 512);

        s.RunInBackground = Bool("run_in_background", true);
        s.ShowTrayIcon = Bool("show_tray_icon", true);
        // "none" = turned off in Options; missing or empty (what versions before Alt+X saved) = the default.
        string hotkey = Str("hotkey", "").Trim();
        s.Hotkey = hotkey.Equals("none", StringComparison.OrdinalIgnoreCase) ? "" : hotkey.Length == 0 ? DefaultHotkey : hotkey;
        s.StartWithWindows = ReadStartWithWindows();
        s.UseInsteadOfExplorer = ExplorerReplacement.IsEnabled();

        var ix = s.Index;
        ix.IndexNtfsVolumes = Bool("index_ntfs_volumes", ix.IndexNtfsVolumes);
        ix.Volumes = List("volumes", ix.Volumes);
        ix.Folders = List("folders", ix.Folders);
        ix.ExcludeFolders = List("exclude_folders", ix.ExcludeFolders);
        ix.ExcludePatterns = List("exclude_patterns", ix.ExcludePatterns);
        ix.ExcludeHidden = Bool("exclude_hidden", ix.ExcludeHidden);
        ix.ExcludeSystem = Bool("exclude_system", ix.ExcludeSystem);
        if (Str("database_path", "") is { Length: > 0 } db) ix.DatabasePath = db;

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
        W("hotkey", Hotkey.Length == 0 ? "none" : Hotkey);
        B("index_ntfs_volumes", Index.IndexNtfsVolumes);
        W("volumes", string.Join('|', Index.Volumes));
        W("folders", string.Join('|', Index.Folders));
        W("exclude_folders", string.Join('|', Index.ExcludeFolders));
        W("exclude_patterns", string.Join('|', Index.ExcludePatterns));
        B("exclude_hidden", Index.ExcludeHidden);
        B("exclude_system", Index.ExcludeSystem);
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
