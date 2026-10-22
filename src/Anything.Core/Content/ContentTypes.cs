namespace Anything.Core.Content;

/// <summary>The one table of extensions whose insides are read (census, readers, Settings), plus the secrets deny-list.</summary>
public static class ContentTypes
{
    static readonly (ContentKind Kind, string[] Extensions)[] Table =
    [
        (ContentKind.Pdf, ["pdf"]),
        (ContentKind.Office, ["docx", "docm", "dotx", "dotm", "xlsx", "xlsm", "xltx", "xltm", "pptx", "pptm", "ppsx", "ppsm", "potx", "potm",
            "odt", "ott", "ods", "ots", "odp", "otp"]),
        (ContentKind.Text, ["txt", "text", "md", "markdown", "csv", "tsv", "rtf", "htm", "html"]),
        (ContentKind.Mail, ["eml", "msg"]),
        (ContentKind.Design, ["psd", "psb", "ai", "indd", "idml", "eps"]),
        (ContentKind.Picture, ["jpg", "jpeg", "jpe", "jfif", "tif", "tiff", "png", "gif", "bmp", "webp", "heic", "heif", "avif"]),
    ];

    static readonly Dictionary<string, ContentKind> ByExtension = Build();
    static readonly Dictionary<string, ContentKind>.AlternateLookup<ReadOnlySpan<char>> ByExtensionSpan = ByExtension.GetAlternateLookup<ReadOnlySpan<char>>();

    // Extensions never read: keys, certificates, password stores, VPN and remote-desktop profiles, partial downloads.
    static readonly HashSet<string> DeniedExtensions = new(
        ["pem", "key", "pfx", "p12", "kdbx", "ppk", "asc", "gpg", "pgp", "ovpn", "rdp", "tmp", "crdownload", "part", "partial"],
        StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> DeniedExtensionsSpan = DeniedExtensions.GetAlternateLookup<ReadOnlySpan<char>>();

    static readonly HashSet<string> DeniedNames = new(
        [".npmrc", ".pypirc", ".netrc", ".pgpass", ".htpasswd", ".git-credentials"], StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> DeniedNamesSpan = DeniedNames.GetAlternateLookup<ReadOnlySpan<char>>();

    static readonly string[] DeniedPrefixes = [".env", "id_rsa", "id_dsa", "id_ed25519", "id_ecdsa", "credentials", "~$"];

    // Words in the names of the files people keep secrets in as text, whatever the extension: browser and password-manager
    // exports ("Chrome Passwords.csv", "bitwarden_export_….csv"), recovery and backup codes ("BitLocker Recovery Key ….TXT",
    // "github-recovery-codes.txt"), cloud access keys ("…_accessKeys.csv"). Matched by name only (a renamed file isn't).
    static readonly string[] DeniedNameParts =
    [
        "password", "passwd", "recovery key", "recoverykey", "recovery-key", "recovery_key", "recovery code", "recoverycode",
        "recovery-code", "recovery_code", "backup code", "backupcode", "backup-code", "backup_code", "accesskeys", "bitwarden_export",
        "lastpass_export", "client_secret", "credential_export", "credential-export",
    ];

    // The same for words that only mean a secret in a spreadsheet export ("logins.csv" from Firefox, AWS's "rootkey.csv").
    static readonly string[] DeniedCsvNameParts = ["logins", "rootkey"];

    static readonly HashSet<string> DeniedFolders = new(
        ["AppData", ".ssh", ".gnupg", ".aws", ".azure", ".kube", ".git", ".hg", ".svn", "node_modules", "__pycache__", ".venv",
            "$Recycle.Bin", "System Volume Information"],
        StringComparer.OrdinalIgnoreCase);
    static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>> DeniedFoldersSpan = DeniedFolders.GetAlternateLookup<ReadOnlySpan<char>>();

    static Dictionary<string, ContentKind> Build()
    {
        var d = new Dictionary<string, ContentKind>(StringComparer.OrdinalIgnoreCase);
        foreach (var (kind, exts) in Table)
            foreach (var e in exts) d[e] = kind;
        return d;
    }

    /// <summary>Kind of a file by its name's extension (span, no allocation); None when its insides aren't read.</summary>
    public static ContentKind KindOf(ReadOnlySpan<char> fileName) => KindOfExtension(ExtensionOf(fileName));

    /// <summary>Kind of a file, user extra text extensions (from <see cref="ExtraSet"/>) counting as Text; built-in kinds win over extras.</summary>
    public static ContentKind KindOf(ReadOnlySpan<char> fileName, HashSet<string>? extraText)
    {
        var ext = ExtensionOf(fileName);
        var kind = KindOfExtension(ext);
        if (kind != ContentKind.None || extraText is not { Count: > 0 } || ext.IsEmpty) return kind;
        bool extra = extraText.TryGetAlternateLookup<ReadOnlySpan<char>>(out var lookup) ? lookup.Contains(ext) : extraText.Contains(ext.ToString());
        return extra ? ContentKind.Text : ContentKind.None;
    }

    /// <summary>
    /// The kind a file is read as under these settings, or None: the deny-list first (it beats extras), then its kind,
    /// which must be switched on by <paramref name="kinds"/> (pictures by the mode instead).
    /// </summary>
    public static ContentKind CandidateKind(ReadOnlySpan<char> fileName, ContentKinds kinds, ContentMode mode, HashSet<string>? extraText)
    {
        if (mode == ContentMode.NamesOnly || IsDeniedFile(fileName)) return ContentKind.None;
        var kind = KindOf(fileName, extraText);
        return kind switch
        {
            ContentKind.None => ContentKind.None,
            ContentKind.Picture => mode == ContentMode.DocumentsAndPictures ? kind : ContentKind.None,
            _ => (kinds & FlagOf(kind)) != 0 ? kind : ContentKind.None,
        };
    }

    /// <summary>The user's extra text extensions as a lookup for <see cref="KindOf(ReadOnlySpan{char}, HashSet{string}?)"/>: dots, "*" and spaces trimmed, case ignored.</summary>
    public static HashSet<string> ExtraSet(IEnumerable<string> extensions)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in extensions)
        {
            string e = raw.Trim().TrimStart('*').TrimStart('.').Trim();
            if (e.Length is > 0 and <= 16 && e.AsSpan().IndexOfAny(['.', '\\', '/', ':', '*', '?', ' ']) < 0) set.Add(e);
        }
        return set;
    }

    /// <summary>Kind of a bare extension (no dot, any case); None when its insides aren't read.</summary>
    internal static ContentKind KindOfExtension(ReadOnlySpan<char> extension)
        => !extension.IsEmpty && ByExtensionSpan.TryGetValue(extension, out var k) ? k : ContentKind.None;

    /// <summary>The extension of a file name without the dot ("" for none; ".env" has none).</summary>
    internal static ReadOnlySpan<char> ExtensionOf(ReadOnlySpan<char> fileName)
    {
        int dot = fileName.LastIndexOf('.');
        return dot <= 0 || dot == fileName.Length - 1 ? default : fileName[(dot + 1)..];
    }

    /// <summary>The built-in extensions of a kind, lower case, no dots.</summary>
    public static IReadOnlyList<string> Extensions(ContentKind kind)
    {
        foreach (var (k, exts) in Table)
            if (k == kind) return exts;
        return [];
    }

    /// <summary>The ContentKinds flag that switches a kind (Picture → None: pictures follow the mode).</summary>
    public static ContentKinds FlagOf(ContentKind kind) => kind switch
    {
        ContentKind.Pdf => ContentKinds.Pdf,
        ContentKind.Office => ContentKinds.Office,
        ContentKind.Text => ContentKinds.Text,
        ContentKind.Mail => ContentKinds.Mail,
        ContentKind.Design => ContentKinds.Design,
        _ => ContentKinds.None,
    };

    /// <summary>
    /// File names never read whatever the settings: keys, certificates, password stores, password-manager and browser
    /// exports, recovery and backup codes, .env, Office owner files, partial downloads (allocates nothing).
    /// </summary>
    public static bool IsDeniedFile(ReadOnlySpan<char> fileName)
    {
        if (fileName.IsEmpty) return false;
        foreach (var p in DeniedPrefixes)
            if (fileName.StartsWith(p, StringComparison.OrdinalIgnoreCase)) return true;
        if (DeniedNamesSpan.Contains(fileName)) return true;
        var ext = ExtensionOf(fileName);
        if (!ext.IsEmpty && DeniedExtensionsSpan.Contains(ext)) return true;
        // Pictures are only their details: a photo's name with "password" in it is no password store.
        if (KindOfExtension(ext) == ContentKind.Picture) return false;
        foreach (var part in DeniedNameParts)
            if (fileName.Contains(part, StringComparison.OrdinalIgnoreCase)) return true;
        if (ext.Equals("csv", StringComparison.OrdinalIgnoreCase))
            foreach (var part in DeniedCsvNameParts)
                if (fileName.Contains(part, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>Folder names never read inside: AppData, .ssh, .gnupg, .aws, .azure, .kube, .git, node_modules, $Recycle.Bin, …</summary>
    public static bool IsDeniedFolder(ReadOnlySpan<char> folderName) => !folderName.IsEmpty && DeniedFoldersSpan.Contains(folderName);

    /// <summary>The folder names <see cref="IsDeniedFolder"/> refuses (for exclusion lists such as the census's folder filter).</summary>
    internal static IReadOnlyCollection<string> DeniedFolderNames => DeniedFolders;

    /// <summary>Folders never read inside wherever they are: %TEMP% and the app's own data folders (full paths, no trailing separator).</summary>
    public static IReadOnlyList<string> DeniedFolderPaths()
    {
        var list = new List<string>();
        void Add(string? p) { if (ContentSettings.Normalize(p) is { } n) list.Add(n); }
        Add(Path.GetTempPath());
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rock Preview"));
        Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rock Preview"));
        return list;
    }
}
