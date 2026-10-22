using System.Security.Principal;
using Anything.Core.Content;

namespace Anything;

/// <summary>Setup's side of search inside files: the choice made in the setup window, written into the user's settings.</summary>
internal static partial class Setup
{
    /// <summary>-content's value (names, documents or pictures); null for anything else, which leaves the choice alone.</summary>
    static ContentMode? ParseContentMode(string value) => value.Trim().ToLowerInvariant() switch
    {
        "names" => ContentMode.NamesOnly,
        "documents" => ContentMode.Documents,
        "pictures" => ContentMode.DocumentsAndPictures,
        _ => null,
    };

    /// <summary>The choice this user made before (an update shows it), or null when none was ever made.</summary>
    public static ContentMode? ContentModeOfUser()
    {
        try
        {
            var content = AppSettings.Load(AppSettings.DefaultPath(demo: false)).Content;
            return content.ModeExplicit ? content.Mode : null;
        }
        catch (Exception) { return null; }
    }

    /// <summary>
    /// The folders this user searches inside (My folders while used, and the folders added) that their name index doesn't
    /// hold under their settings, so nothing in them is read inside: the check Settings › Indexing makes
    /// (ContentEstimator.FoldersNotSearchedByName). Empty when there are none or the settings can't be read (a new
    /// install's defaults search every drive by name).
    /// </summary>
    public static IReadOnlyList<string> ContentFoldersNotSearchedByName()
    {
        try
        {
            var settings = AppSettings.Load(AppSettings.DefaultPath(demo: false));
            return ContentEstimator.FoldersNotSearchedByName(settings.Content, settings.Index);
        }
        catch (Exception) { return []; }
    }

    /// <summary>
    /// The size of this user's text index on disk (0 when there is none), for "Choosing this deletes …": whatever choice
    /// was made, also one kept from before with no choice made. It lists a folder that may be on a share: call it off the
    /// UI thread.
    /// </summary>
    public static long ContentIndexBytesOfUser()
    {
        try
        {
            return IndexFolder(AppSettings.Load(AppSettings.DefaultPath(demo: false)).ContentFolder) is { } folder
                ? new DirectoryInfo(folder).EnumerateFiles("*", SearchOption.AllDirectories).Sum(f => f.Length)
                : 0;
        }
        catch (Exception) { return 0; }
    }

    /// <summary>The folder when it holds a text index (a manifest or segments), else null: never another folder called Content.</summary>
    static string? IndexFolder(string folder) => ContentServices.HoldsIndex(folder) ? folder : null;

    /// <summary>The Windows account this process runs as (its SID), or null.</summary>
    public static string? CurrentUserSid()
    {
        try { return WindowsIdentity.GetCurrent().User?.Value; }
        catch (Exception) { return null; }
    }

    /// <summary>A user's own data: their settings file and their roaming and local data folders ("Rock Preview").</summary>
    internal sealed record UserData(string SettingsPath, string Roaming, string Local)
    {
        /// <summary>Where their name database is when no database_path moved it.</summary>
        public string DefaultDatabase => Path.Combine(Local, "RockPreview.db");
    }

    /// <summary>
    /// The data of the user with this SID: the current user's when it is theirs (or none is given); another user's (an
    /// elevated copy started with an administrator's password runs as that administrator) from their profile folder in
    /// the ProfileList, with their Roaming AppData where their own shell folders put it (it may be redirected to a share).
    /// Null when that profile can't be found: then nothing is written or deleted for them, and never the administrator's
    /// own data instead.
    /// </summary>
    internal static UserData? DataOf(string? sid)
    {
        if (sid is not { Length: > 0 } || string.Equals(sid, CurrentUserSid(), StringComparison.OrdinalIgnoreCase))
            return new UserData(AppSettings.DefaultPath(demo: false),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), DataFolderName),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), DataFolderName));
        try
        {
            var id = new SecurityIdentifier(sid); // a SID, never a path
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Microsoft\Windows NT\CurrentVersion\ProfileList\" + id.Value);
            if (key?.GetValue("ProfileImagePath") is not string { Length: > 0 } raw) return null;
            string profile = Environment.ExpandEnvironmentVariables(raw);
            if (!Path.IsPathFullyQualified(profile) || !Directory.Exists(profile)) return null;
            string roaming = Path.Combine(RoamingAppDataOf(id, profile) ?? Path.Combine(profile, "AppData", "Roaming"), DataFolderName);
            return new UserData(Path.Combine(roaming, Path.GetFileName(AppSettings.DefaultPath(demo: false))), roaming,
                Path.Combine(profile, "AppData", "Local", DataFolderName));
        }
        catch (Exception ex)
        {
            Log(ex);
            return null;
        }
    }

    /// <summary>
    /// Another user's Roaming AppData as their own app finds it (GetFolderPath honours Folder Redirection, which can put it
    /// on a share): their hive's User Shell Folders\AppData, %USERPROFILE% being their profile folder, else the expanded
    /// Shell Folders value. Their hive is loaded while they are logged on, as the user who ran setup is; null when it
    /// isn't, or names a folder that can't be worked out (then their profile's AppData\Roaming is all there is to go on).
    /// </summary>
    static string? RoamingAppDataOf(SecurityIdentifier id, string profile)
    {
        const string Explorer = @"\Software\Microsoft\Windows\CurrentVersion\Explorer\";
        try
        {
            using (var key = Microsoft.Win32.Registry.Users.OpenSubKey(id.Value + Explorer + "User Shell Folders"))
                if (key?.GetValue("AppData", null, Microsoft.Win32.RegistryValueOptions.DoNotExpandEnvironmentNames) is string { Length: > 0 } raw)
                {
                    // Only the profile is known for another user; any other variable would expand to this process's own.
                    string path = raw.Replace("%USERPROFILE%", profile, StringComparison.OrdinalIgnoreCase);
                    if (!path.Contains('%') && Path.IsPathFullyQualified(path)) return path;
                }
            using (var key = Microsoft.Win32.Registry.Users.OpenSubKey(id.Value + Explorer + "Shell Folders"))
                if (key?.GetValue("AppData") is string { Length: > 0 } path && !path.Contains('%') && Path.IsPathFullyQualified(path)) return path;
        }
        catch (Exception ex) when (ex is System.Security.SecurityException or UnauthorizedAccessException or IOException) { Log(ex); }
        return null;
    }

    /// <summary>
    /// Writes the choice into the user's settings (after the running copy closed, so its exit doesn't overwrite it), and
    /// marks the in-app offer answered. An elevated copy started with another administrator's password runs as that
    /// administrator: <paramref name="asUser"/> names the user who chose, and the choice is written in their own profile
    /// (a names-only choice that stops reading their files must not be dropped), never the administrator's.
    /// </summary>
    static void SetContentMode(ContentMode mode, string? asUser)
    {
        if (DataOf(asUser) is not { } user) return;
        try
        {
            var settings = AppSettings.Load(user.SettingsPath);
            settings.Content.Mode = mode;
            settings.Content.ModeExplicit = true; // names chosen here deletes an index left on disk, at the app's next start
            settings.ContentAsked = true;
            settings.Save();
        }
        catch (Exception ex) { Log(ex); }
    }

    /// <summary>
    /// Uninstall's "delete my settings and search index" for the user who asked (<paramref name="asUser"/>; the current
    /// user when none is given): their text index wherever the database was moved, then their data folders. Never the
    /// administrator's whose password elevated the uninstall.
    /// </summary>
    static void DeleteUserData(string? asUser)
    {
        if (DataOf(asUser) is not { } user) return;
        DeleteContentIndexOf(user); // first: where it is comes from the settings deleted next
        TryDeleteTree(user.Roaming);
        TryDeleteTree(user.Local);
    }

    /// <summary>
    /// The user's text index (its Content folder) and their moved database itself, which lists every file name on the PC.
    /// Only the text index's own files are deleted (the folder too once nothing else is left in it), never anything else
    /// in a folder that happens to be called Content, and only a database file of ours.
    /// </summary>
    static void DeleteContentIndexOf(UserData user)
    {
        AppSettings settings;
        try { settings = AppSettings.Load(user.SettingsPath); }
        catch (Exception ex) { Log(ex); return; }
        // No database_path: their default one (the default this process works out is its own user's).
        string db = AppSettings.SamePath(settings.Index.DatabasePath, new Anything.Core.IndexSettings().DatabasePath) ? user.DefaultDatabase : settings.Index.DatabasePath;
        try
        {
            if (IndexFolder(ContentServices.FolderFor(db)) is { } folder) ContentServices.DeleteIndexFiles(folder);
        }
        catch (Exception ex) { Log(ex); }
        // The default database is in the data folder the uninstall deletes next.
        if (db.Length == 0 || AppSettings.SamePath(db, user.DefaultDatabase)) return;
        foreach (var file in new[] { db, db + ".tmp" })
        {
            try
            {
                if (IsNameDatabase(file)) File.Delete(file);
            }
            catch (Exception ex) { Log(ex); }
        }
    }

    /// <summary>The file exists and is a name database (it starts with the database's magic), or an empty one left by a crash.</summary>
    static bool IsNameDatabase(string file)
    {
        try
        {
            if (!File.Exists(file)) return false;
            using var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (fs.Length == 0) return file.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase);
            Span<byte> head = stackalloc byte[8];
            return fs.ReadAtLeast(head, 8, throwOnEndOfStream: false) == 8 && head.SequenceEqual("ANYTHGDB"u8);
        }
        catch (Exception) { return false; }
    }
}
