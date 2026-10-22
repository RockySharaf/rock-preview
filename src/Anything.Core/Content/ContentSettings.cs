using System.Runtime.InteropServices;

namespace Anything.Core.Content;

/// <summary>Search inside files settings (RockPreview.ini content_*); Settings edits this object in place, the index only ever works from Clone().</summary>
public sealed partial class ContentSettings
{
    /// <summary>Names only (default: nothing is read), documents' words, or also pictures' details.</summary>
    public ContentMode Mode { get; set; } = ContentMode.NamesOnly;
    /// <summary>
    /// The mode was chosen: content_mode is in the ini, or Settings or Setup set it. False = never chosen (or an older build
    /// dropped the key): read as NamesOnly, and a text index left on disk is kept, neither followed nor deleted. Load sets it
    /// exactly when the key holds a known value, and Save writes content_mode only while it is true (<see cref="LoadMode"/>,
    /// <see cref="ModeToSave"/>): saving an unchosen mode as "names" would delete the kept index at the next start.
    /// </summary>
    public bool ModeExplicit { get; set; }
    /// <summary>Read inside Desktop, Documents, Downloads, Pictures and the OneDrive roots, wherever Windows keeps them now.</summary>
    public bool UseMyFolders { get; set; } = true;
    /// <summary>More folders, drives and shares to read inside ("E:\", "\\nas\jobs"); added to My folders, never replacing them.</summary>
    public List<string> Folders { get; set; } = [];
    /// <summary>Folders never read inside, even within the ones above.</summary>
    public List<string> ExcludeFolders { get; set; } = [];
    /// <summary>Kinds of document read.</summary>
    public ContentKinds Kinds { get; set; } = ContentKinds.All;
    /// <summary>More plain-text extensions to read, without dots ("log", "json"); the secrets deny-list still wins.</summary>
    public List<string> ExtraTextExtensions { get; set; } = [];
    /// <summary>Documents larger than this are left out (TooBig); pictures never are (only headers are read). 0 = no limit.</summary>
    public int MaxFileSizeMB { get; set; } = 2048;
    /// <summary>Read on battery too (off: reading waits for the charger).</summary>
    public bool ReadOnBattery { get; set; }
    /// <summary>Read files encrypted by Windows (EFS); their words would sit unencrypted in the index.</summary>
    public bool ReadEncrypted { get; set; }
    /// <summary>The most a network share is read at, in KB/s.</summary>
    public int NetworkKilobytesPerSecond { get; set; } = 8192;
    /// <summary>Reserved for OCR (later): stored and copied, not used yet.</summary>
    public List<string> OcrFolders { get; set; } = [];

    /// <summary>A copy, lists included.</summary>
    public ContentSettings Clone() => new()
    {
        Mode = Mode, ModeExplicit = ModeExplicit, UseMyFolders = UseMyFolders, Folders = [.. Folders], ExcludeFolders = [.. ExcludeFolders],
        Kinds = Kinds, ExtraTextExtensions = [.. ExtraTextExtensions], MaxFileSizeMB = MaxFileSizeMB, ReadOnBattery = ReadOnBattery,
        ReadEncrypted = ReadEncrypted, NetworkKilobytesPerSecond = NetworkKilobytesPerSecond, OcrFolders = [.. OcrFolders],
    };

    /// <summary>The content_mode value of a mode: names, documents or pictures.</summary>
    public static string ModeName(ContentMode mode) => mode switch
    {
        ContentMode.Documents => "documents",
        ContentMode.DocumentsAndPictures => "pictures",
        _ => "names",
    };

    /// <summary>
    /// Reads content_mode as AppSettings.Load must: names, documents or pictures (any case) choose that mode, explicitly; a
    /// missing or unknown value (an older or newer build's) leaves the mode unchosen (NamesOnly, ModeExplicit false), so it
    /// never deletes an index.
    /// </summary>
    public void LoadMode(string? iniValue)
    {
        ModeExplicit = true;
        switch (iniValue?.Trim().ToLowerInvariant())
        {
            case "names": Mode = ContentMode.NamesOnly; break;
            case "documents": Mode = ContentMode.Documents; break;
            case "pictures": Mode = ContentMode.DocumentsAndPictures; break;
            default: Mode = ContentMode.NamesOnly; ModeExplicit = false; break;
        }
    }

    /// <summary>The content_mode value AppSettings.Save writes, or null to leave the key out (the mode was never chosen).</summary>
    public string? ModeToSave() => ModeExplicit ? ModeName(Mode) : null;

    /// <summary>The known folders behind UseMyFolders, nested ones merged (SHGetKnownFolderPath and the OneDrive variables).</summary>
    public static IReadOnlyList<string> MyFolders()
    {
        var found = new List<string>();
        foreach (var (id, fallback) in KnownFolders)
        {
            string? path = KnownFolder(id);
            if (string.IsNullOrEmpty(path) && fallback is { } f) path = Environment.GetFolderPath(f);
            if (!string.IsNullOrEmpty(path)) found.Add(path);
        }
        foreach (var name in (string[])["OneDrive", "OneDriveConsumer", "OneDriveCommercial"])
            if (Environment.GetEnvironmentVariable(name) is { Length: > 0 } od) found.Add(od);
        return MergeNested(found);
    }

    /// <summary>My folders (when used) plus Folders, nested ones merged.</summary>
    public IReadOnlyList<string> EffectiveFolders() => MergeNested(UseMyFolders ? [.. MyFolders(), .. Folders] : Folders);

    /// <summary>
    /// The folder searched inside (My folders' or an added one) that already holds <paramref name="folder"/>, itself or a
    /// folder above it; null when none does. Adding it then reads nothing new.
    /// </summary>
    public string? CoveredBy(string folder)
    {
        if (Normalize(folder) is not { } f) return null;
        foreach (var e in EffectiveFolders()) if (IsInside(f, e)) return e;
        return null;
    }

    /// <summary>The folders searched inside that lie within <paramref name="folder"/> (adding it reads those again for nothing).</summary>
    public IReadOnlyList<string> FoldersInside(string folder)
    {
        if (Normalize(folder) is not { } f) return [];
        return [.. EffectiveFolders().Where(e => !string.Equals(e, f, StringComparison.OrdinalIgnoreCase) && IsInside(e, f))];
    }

    /// <summary>
    /// Full paths without a trailing separator (drive roots keep theirs: "C:\"), duplicates and folders inside another one
    /// dropped, first-seen order kept. Paths that aren't absolute or valid are left out.
    /// </summary>
    internal static List<string> MergeNested(IEnumerable<string> folders)
    {
        var normal = new List<string>();
        foreach (var f in folders)
            if (Normalize(f) is { } n && !normal.Contains(n, StringComparer.OrdinalIgnoreCase)) normal.Add(n);
        var kept = new List<string>(normal.Count);
        foreach (var n in normal)
        {
            bool inside = false;
            foreach (var other in normal)
                if (!ReferenceEquals(other, n) && IsInside(n, other)) { inside = true; break; }
            if (!inside) kept.Add(n);
        }
        return kept;
    }

    /// <summary>The folder as a full path ("C:\Users\Me", "C:\", "\\nas\jobs"), or null when it isn't an absolute path.</summary>
    internal static string? Normalize(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        string f = Environment.ExpandEnvironmentVariables(folder.Trim().Trim('"'));
        if (f.Length == 2 && f[1] == ':' && char.IsAsciiLetter(f[0])) f += "\\";
        if (!Path.IsPathFullyQualified(f)) return null;
        try { f = Path.GetFullPath(f); }
        catch (Exception e) when (e is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
        string root = Path.GetPathRoot(f) ?? "";
        return f.Length > root.Length ? f.TrimEnd('\\', '/') : root;
    }

    /// <summary>Whether <paramref name="path"/> is <paramref name="folder"/> or below it (both normalized).</summary>
    internal static bool IsInside(string path, string folder)
    {
        if (!path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) return false;
        if (path.Length == folder.Length) return true;
        return folder.EndsWith('\\') || path[folder.Length] == '\\';
    }

    static readonly (Guid Id, Environment.SpecialFolder? Fallback)[] KnownFolders =
    [
        (new("B4BFCC3A-DB2C-424C-B029-7FE99A87C641"), Environment.SpecialFolder.DesktopDirectory),
        (new("FDD39AD0-238F-46AF-ADB4-6C85480369C7"), Environment.SpecialFolder.MyDocuments),
        (new("374DE290-123F-4565-9164-39C4925E467B"), null), // Downloads has no SpecialFolder
        (new("33E28130-4E1E-4676-835A-98395C3BC3BB"), Environment.SpecialFolder.MyPictures),
    ];

    static string? KnownFolder(Guid id)
    {
        try
        {
            if (SHGetKnownFolderPath(in id, KF_FLAG_DONT_VERIFY, 0, out var p) != 0 || p == 0) return null;
            try { return Marshal.PtrToStringUni(p); }
            finally { Marshal.FreeCoTaskMem(p); }
        }
        catch (Exception e) when (e is DllNotFoundException or EntryPointNotFoundException) { return null; }
    }

    const uint KF_FLAG_DONT_VERIFY = 0x4000;

    [LibraryImport("shell32.dll")]
    private static partial int SHGetKnownFolderPath(in Guid rfid, uint dwFlags, nint hToken, out nint ppszPath);
}
