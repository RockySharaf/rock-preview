using System.Globalization;
using System.Text;
using Anything.Core.Thumbs;

namespace Anything;

/// <summary>"Instant thumbnails" in RockPreview.ini (thumbnail_store*), and the count of waits that leads to offering it.</summary>
internal sealed partial class AppSettings
{
    /// <summary>Keep thumbnails on disk so they appear at once (Settings › Thumbnails). Off unless the user turns it on.</summary>
    public bool ThumbnailStore { get; set; }

    /// <summary>The most the saved thumbnails may take, in megabytes: one of <see cref="ThumbStore.CapChoicesMegabytes"/>.</summary>
    public int ThumbnailStoreMegabytes { get; set; } = ThumbStore.DefaultCapMegabytes;

    /// <summary>The folder the user chose for them (Change location...); empty: beside the index database.</summary>
    public string ThumbnailStorePath { get; set; } = "";

    /// <summary>Times the thumbnails on screen took more than a second to appear, on separate occasions (the bar needs three).</summary>
    public int ThumbnailWaits { get; set; }

    /// <summary>The bar offering to keep thumbnails on disk was shown (once ever).</summary>
    public bool ThumbnailStoreOffered { get; set; }

    /// <summary>
    /// Store folders that couldn't be removed when the option was turned off or moved (a drive asleep or gone, a file in
    /// use): removed when they can be, at the next start or turn-off, and by uninstall.
    /// </summary>
    public List<string> ThumbnailStoreLeftovers { get; } = [];

    /// <summary>Where the saved thumbnails are, or would be: "Rock Preview Thumbnails" in the chosen folder, else "Thumbs" beside the database.</summary>
    public string ThumbnailStoreFolder => ThumbOption.StoreFolder(Index.DatabasePath, ThumbnailStorePath);

    static void LoadThumbs(AppSettings s, Dictionary<string, string> v)
    {
        string? Get(string k) => v.TryGetValue(k, out var x) ? x : null;
        s.ThumbnailStore = Get("thumbnail_store")?.Trim() is "1" or "true" or "True";
        s.ThumbnailStoreMegabytes = ThumbOption.CapMegabytesFrom(Get("thumbnail_store_mb"));
        s.ThumbnailStorePath = Get("thumbnail_store_path")?.Trim() ?? "";
        s.ThumbnailWaits = int.TryParse(Get("thumbnail_waits")?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int waits) ? Math.Max(0, waits) : 0;
        s.ThumbnailStoreOffered = Get("thumbnail_store_offered")?.Trim() is "1" or "true" or "True";
        // Folders can't hold '|' in their names: it parts them.
        s.ThumbnailStoreLeftovers.Clear();
        foreach (var f in (Get("thumbnail_store_leftover") ?? "").Split('|', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (!s.ThumbnailStoreLeftovers.Exists(x => SamePath(x, f))) s.ThumbnailStoreLeftovers.Add(f);
    }

    void SaveThumbs(StringBuilder sb)
    {
        void W(string k, string value) => sb.Append(k).Append('=').Append(value).Append("\r\n");
        W("thumbnail_store", ThumbnailStore ? "1" : "0");
        W("thumbnail_store_mb", ThumbnailStoreMegabytes.ToString(CultureInfo.InvariantCulture));
        if (ThumbnailStorePath.Length > 0) W("thumbnail_store_path", Clean(ThumbnailStorePath));
        W("thumbnail_waits", ThumbnailWaits.ToString(CultureInfo.InvariantCulture));
        W("thumbnail_store_offered", ThumbnailStoreOffered ? "1" : "0");
        if (ThumbnailStoreLeftovers.Count > 0) W("thumbnail_store_leftover", Clean(string.Join("|", ThumbnailStoreLeftovers)));
    }
}
