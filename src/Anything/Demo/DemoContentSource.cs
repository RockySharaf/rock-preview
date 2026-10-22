using System.Text;
using Anything.Core;
using Anything.Core.Content;

namespace Anything.Demo;

/// <summary>
/// What the demo's text index reads: the sample index's paths are made up, so each document gets text and each picture
/// details made from its name (the same every time), from word lists of its own: sentences, an invoice code, accented
/// words (café, résumé), a Japanese or Chinese line in about one file in twenty, titles and keywords; pictures get a size,
/// 72, 150 or 300 dpi, RGB or CMYK, a camera, a date, keywords (beach, sunset, …) and, for about half, a place. The few
/// real files the demo lists (C:\Windows\win.ini …) go through the real readers.
/// </summary>
internal sealed class DemoContentSource(FileIndex index) : IContentSource
{
    static readonly string[] Words =
    [
        "report", "budget", "invoice", "project", "meeting", "summary", "draft", "final", "contract", "proposal", "schedule",
        "quarter", "revenue", "client", "delivery", "design", "print", "proof", "approval", "deadline", "estimate", "order",
        "payment", "review", "notes", "plan", "launch", "brochure", "poster", "catalogue", "menu", "label", "packaging",
        "banner", "flyer", "logo", "artwork", "layout", "paper", "glossy", "matte", "bleed", "margin", "varnish", "colour",
        "café", "résumé", "naïve", "fiancée", "Zürich", "smörgåsbord", "jalapeño", "Ärger", "the", "and", "for", "with",
    ];

    static readonly string[] PictureWords =
    [
        "beach", "sunset", "mountain", "city", "portrait", "family", "holiday", "wedding", "garden", "snow", "forest",
        "river", "harbour", "street", "night", "concert", "product", "studio", "team", "skyline",
    ];

    static readonly (string Make, string Model)[] Cameras =
    [
        ("Canon", "Canon EOS R5"), ("Canon", "Canon EOS 5D Mark IV"), ("NIKON CORPORATION", "NIKON Z 6"), ("SONY", "ILCE-7M3"),
        ("FUJIFILM", "X-T4"), ("Apple", "iPhone 15 Pro"), ("samsung", "Galaxy S23"), ("Google", "Pixel 8"),
    ];

    static readonly string[] Places = ["Brighton", "Lisbon", "Kyoto", "Cape Town", "Vancouver", "Cairo", "Sydney", "Reykjavík"];

    static readonly string[] Unspaced = ["東京都の会議資料と見積書", "2024年の予算報告書", "北京办公室的季度报告", "大阪支店の請求書"];

    static readonly string[] People = ["Ana Lima", "Ben Okafor", "Chloé Martin", "Dev Patel", "Emma Schultz", "Kenji Sato"];

    readonly IContentSource _files = ContentServices.Files();
    readonly string _windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    readonly object _gate = new();
    Dictionary<string, List<int>>? _byName; // leaf name → ids, for TryStat on made-up paths
    (long Generation, long Version) _byNameAt = (-1, -1);

    /// <summary>A file of the demo that is really on this PC (C:\Windows\win.ini and the like).</summary>
    bool IsReal(string path) => _windows.Length > 0 && path.StartsWith(_windows + "\\", StringComparison.OrdinalIgnoreCase) && File.Exists(path);

    public ContentRead Read(ExtractRequest request, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (IsReal(request.FullPath)) return _files.Read(request, ct);
        long size = request.ExpectedSize, modified = request.ExpectedModifiedUtc;
        if (size < 0 || modified == 0) TryStat(request.FullPath, out size, out modified, out _);
        var doc = request.Kind == ContentKind.Picture ? Picture(request.FullPath, request.Kind) : Document(request.FullPath, request.Kind);
        return new ContentRead(doc, size, modified, Retry: false, Offline: false);
    }

    public bool TryStat(string fullPath, out long size, out long modifiedUtc, out FileAttributes attributes)
    {
        size = -1;
        modifiedUtc = 0;
        attributes = 0;
        if (IsReal(fullPath))
        {
            try
            {
                var fi = new FileInfo(fullPath);
                (size, modifiedUtc, attributes) = (fi.Length, fi.LastWriteTimeUtc.ToFileTimeUtc(), fi.Attributes);
                return true;
            }
            catch (IOException) { return false; }
            catch (UnauthorizedAccessException) { return false; }
        }
        string name = Path.GetFileName(fullPath);
        index.Lock.EnterReadLock();
        try
        {
            if (!NameMap().TryGetValue(name, out var ids)) return false;
            foreach (int id in ids)
            {
                if (!index.IsAlive(id) || !string.Equals(index.GetFullPath(id), fullPath, StringComparison.OrdinalIgnoreCase)) continue;
                (size, modifiedUtc, attributes) = (index.Size(id), index.Modified(id), index.Attributes(id));
                return true;
            }
            return false;
        }
        catch (IndexOutOfRangeException) { return false; } // the sample index changed underneath
        finally { index.Lock.ExitReadLock(); }
    }

    /// <summary>The sample index's names (under its read lock), rebuilt when it changed since the last look.</summary>
    Dictionary<string, List<int>> NameMap()
    {
        lock (_gate)
        {
            var at = (index.Generation, index.Version);
            if (_byName is { } map && at == _byNameAt) return map;
            map = new Dictionary<string, List<int>>(StringComparer.OrdinalIgnoreCase);
            for (int id = 0, n = index.Count; id < n; id++)
            {
                if (!index.IsAlive(id) || index.IsDirectory(id)) continue;
                string name = index.Name(id);
                if (!map.TryGetValue(name, out var list)) map[name] = list = new List<int>(1);
                list.Add(id);
            }
            _byNameAt = at;
            return _byName = map;
        }
    }

    // ---- made-up insides ----------------------------------------------------------------------------

    /// <summary>The demo reader's version: above any real reader's, so nothing the demo made up is read again.</summary>
    const byte Version = byte.MaxValue;

    /// <summary>A seed from the file's name (FNV-1a, case ignored): the same text for the same name, run after run.</summary>
    static int Seed(string path)
    {
        uint h = 2166136261;
        foreach (char c in Path.GetFileName(path).ToUpperInvariant()) { h ^= c; h *= 16777619; }
        return (int)(h & 0x7FFFFFFF);
    }

    static string Pick(Random r, string[] words) => words[r.Next(words.Length)];

    static string Sentence(Random r, int words)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < words; i++)
        {
            if (i > 0) sb.Append(' ');
            string w = Pick(r, Words);
            sb.Append(i == 0 ? char.ToUpperInvariant(w[0]) + w[1..] : w);
        }
        return sb.Append('.').ToString();
    }

    static string Title(Random r) => string.Join(' ', Enumerable.Range(0, r.Next(2, 4)).Select(_ => Pick(r, Words)).Select(w => char.ToUpperInvariant(w[0]) + w[1..]));

    static ExtractedDoc Document(string path, ContentKind kind)
    {
        var r = new Random(Seed(path));
        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        var body = new StringBuilder();
        int pages = 0;
        string Line(int i)
        {
            if (i == 1 && r.Next(3) == 0) return $"Invoice INV-2024-{r.Next(1, 200):0000}: {Sentence(r, r.Next(3, 7))}";
            if (i == 2 && r.Next(20) == 0) return Pick(r, Unspaced);
            if (i == 3 && r.Next(4) == 0) return $"Total due {r.Next(100, 9999)}.{r.Next(0, 99):00} EUR, {Pick(r, People)}";
            return Sentence(r, r.Next(4, 12));
        }
        switch (ext)
        {
            case "pdf" or "pptx":
                // Pages (PDF) or slides (PowerPoint), '\f' between them.
                pages = r.Next(1, 7);
                for (int p = 0; p < pages; p++)
                {
                    if (p > 0) body.Append('\f');
                    int lines = r.Next(2, 7);
                    for (int i = 0; i < lines; i++) body.Append(Line(i + p)).Append('\n');
                }
                break;
            case "xlsx":
                // Sheets: each starts with its name on its own line; later ones after '\f'.
                pages = r.Next(1, 4);
                for (int s = 0; s < pages; s++)
                {
                    if (s > 0) body.Append('\f');
                    body.Append(s == 0 ? "Summary" : (2022 + s).ToString(System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
                    for (int i = 0, rows = r.Next(3, 9); i < rows; i++)
                        body.Append(Pick(r, Words)).Append('\t').Append(r.Next(1, 500)).Append('\t').Append(r.Next(10, 99_999)).Append('\n');
                }
                break;
            default:
                for (int i = 0, lines = r.Next(3, 25); i < lines; i++) body.Append(Line(i)).Append('\n');
                break;
        }
        var fields = new List<(DocField, string)>();
        if (kind is ContentKind.Pdf or ContentKind.Office || r.Next(3) == 0) fields.Add((DocField.Title, Title(r)));
        if (r.Next(2) == 0) fields.Add((DocField.Keywords, string.Join("; ", Enumerable.Range(0, r.Next(1, 4)).Select(_ => Pick(r, Words)).Distinct())));
        if (kind is ContentKind.Pdf or ContentKind.Office && r.Next(2) == 0) fields.Add((DocField.Author, Pick(r, People)));
        return new ExtractedDoc
        {
            Kind = kind,
            Status = DocStatus.Ok,
            Body = body.ToString().TrimEnd('\n'),
            Fields = fields,
            Pages = pages,
            ExtractorVersion = Version,
            BytesRead = 4096,
            ElapsedMs = 1,
        };
    }

    static ExtractedDoc Picture(string path, ContentKind kind)
    {
        var r = new Random(Seed(path));
        string ext = Path.GetExtension(path).TrimStart('.').ToLowerInvariant();
        (int w, int h) = r.Next(5) switch { 0 => (2480, 3508), 1 => (6000, 4000), 2 => (4032, 3024), 3 => (1920, 1080), _ => (800, 600) };
        int dpi = r.Next(3) switch { 0 => 72, 1 => 150, _ => 300 };
        var mode = ext == "gif" ? ColorMode.Indexed : r.Next(4) == 0 ? ColorMode.Cmyk : ColorMode.Rgb;
        byte orientation = (byte)(r.Next(6) == 0 ? 6 : 1);
        var camera = Cameras[r.Next(Cameras.Length)];
        bool photo = ext is "jpg" or "jpeg" && r.Next(5) != 0;
        long taken = photo ? new DateTime(2019 + r.Next(7), r.Next(1, 13), r.Next(1, 29), r.Next(24), r.Next(60), 0, DateTimeKind.Utc).ToFileTimeUtc() : 0;
        bool gps = photo && r.Next(2) == 0;
        var info = new PictureInfo(w, h, dpi, dpi, (byte)(mode == ColorMode.Cmyk ? 32 : mode == ColorMode.Indexed ? 8 : 24), mode, orientation,
            (byte)(mode == ColorMode.Rgb && photo ? 1 : 0), (byte)r.Next(6), taken, gps,
            gps ? r.Next(-60_000_000, 60_000_000) : 0, gps ? r.Next(-170_000_000, 170_000_000) : 0,
            photo ? 100 * r.Next(1, 33) : 0, photo ? 1.8f + r.Next(10) : 0, photo ? 1f / r.Next(30, 2000) : 0, photo ? r.Next(16, 200) : 0);
        var fields = new List<(DocField, string)>
        {
            (DocField.Keywords, string.Join("; ", Enumerable.Range(0, r.Next(1, 4)).Select(_ => Pick(r, PictureWords)).Distinct())),
        };
        if (photo)
        {
            fields.Add((DocField.CameraMake, camera.Make));
            fields.Add((DocField.CameraModel, camera.Model));
        }
        if (r.Next(3) == 0)
        {
            string first = Pick(r, PictureWords);
            fields.Add((DocField.Title, char.ToUpperInvariant(first[0]) + first[1..] + " " + Pick(r, PictureWords)));
        }
        if (gps) fields.Add((DocField.Location, Places[r.Next(Places.Length)]));
        // Like the real picture reader: details and fields, no body text (NoText).
        return new ExtractedDoc
        {
            Kind = kind,
            Status = DocStatus.NoText,
            Fields = fields,
            Picture = info,
            ExtractorVersion = Version,
            BytesRead = 16_384,
            ElapsedMs = 1,
        };
    }
}
