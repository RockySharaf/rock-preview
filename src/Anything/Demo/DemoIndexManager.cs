using Anything.Core;

namespace Anything.Demo;

/// <summary>
/// In-memory sample index used by the hidden --demo switch so the UI can be exercised without the real indexer.
/// Simulates a short scanning phase (adding entries in batches) and then a slow trickle of live changes.
/// </summary>
internal sealed class DemoIndexManager : IIndexManager
{
    static readonly string[] Words =
    [
        "report", "budget", "holiday", "invoice", "project", "notes", "summary", "draft", "final", "backup", "photo", "music",
        "video", "setup", "readme", "config", "data", "archive", "letter", "resume", "plan", "design", "logo", "theme", "game",
        "save", "level", "track", "album", "movie", "clip", "scan", "receipt", "contract", "manual", "guide", "test", "build",
        "release", "café", "naïve", "résumé", "Ärger", "smörgåsbord", "zebra", "alpha", "omega", "delta", "kernel", "driver",
    ];

    static readonly string[] Extensions =
    [
        "txt", "docx", "pdf", "xlsx", "pptx", "jpg", "png", "gif", "mp3", "flac", "wav", "mp4", "mkv", "avi", "zip", "7z",
        "rar", "exe", "dll", "msi", "cs", "json", "xml", "ini", "log", "md", "html", "css", "js", "bat", "ps1", "lnk", "ico",
    ];

    static readonly string[] TopFolders =
    [
        @"Windows", @"Windows\System32", @"Windows\Fonts", @"Program Files", @"Program Files\Common Files", @"Program Files (x86)",
        @"Users\Demo\Documents", @"Users\Demo\Pictures", @"Users\Demo\Music", @"Users\Demo\Videos", @"Users\Demo\Downloads",
        @"Users\Demo\Desktop", @"Users\Demo\AppData\Local\Temp", @"Projects\Anything\src", @"Projects\Anything\docs", @"Games",
    ];

    CancellationTokenSource _cts = new();
    volatile IndexState _state = IndexState.Idle;
    volatile string _status = "";

    public FileIndex Index { get; } = new();
    public IndexState State => _state;
    public string StatusText => _status;
    public event Action? StateChanged;
    public bool IsElevated => Program.IsElevated();

    public void Start(IndexSettings settings) => StartScan();

    public void Rebuild()
    {
        _cts.Cancel();
        _cts = new CancellationTokenSource();
        Index.Clear();
        StartScan();
    }

    public void Save() { }

    void SetState(IndexState s, string text)
    {
        _state = s;
        _status = text;
        StateChanged?.Invoke();
    }

    void StartScan()
    {
        var token = _cts.Token;
        Task.Run(async () =>
        {
            SetState(IndexState.Scanning, "Scanning C:\\ (0 objects)");
            var rng = new Random(1234);
            var dirs = new List<int>();
            Index.BulkAdd(a =>
            {
                long now = DateTime.UtcNow.ToFileTimeUtc();
                foreach (var root in new[] { "C:", "D:" })
                {
                    int r = a.Add(root, FileIndex.NoParent, FileAttributes.Directory, -1, 0, 0);
                    var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase) { [""] = r };
                    foreach (var f in TopFolders)
                    {
                        string parentPath = "";
                        foreach (var part in f.Split('\\'))
                        {
                            string p = parentPath.Length == 0 ? part : parentPath + "\\" + part;
                            if (!map.TryGetValue(p, out int id))
                            {
                                var attrs = FileAttributes.Directory | (part == "AppData" ? FileAttributes.Hidden : 0);
                                id = a.Add(part, map[parentPath], attrs, -1, now - rng.NextInt64(1, 3_000_000_000_000_000), now - rng.NextInt64(3_000_000_000_000_000, 6_000_000_000_000_000));
                                map[p] = id;
                                dirs.Add(id);
                            }
                            parentPath = p;
                        }
                    }
                    // A few real files so the Explorer context menu, real icons and Open can be tried in the demo.
                    string winDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
                    if (root == "C:" && string.Equals(winDir, @"C:\Windows", StringComparison.OrdinalIgnoreCase))
                    {
                        foreach (var real in new[] { "notepad.exe", "explorer.exe", "regedit.exe", "win.ini", "System32\\calc.exe" })
                        {
                            var fi = new FileInfo(Path.Combine(winDir, real));
                            if (!fi.Exists) continue;
                            int parent = map[real.Contains('\\') ? @"Windows\System32" : "Windows"];
                            a.Add(fi.Name, parent, fi.Attributes, fi.Length, fi.LastWriteTimeUtc.ToFileTimeUtc(), fi.CreationTimeUtc.ToFileTimeUtc());
                        }
                    }
                }
            });

            const int Total = 120_000, Batch = 6_000;
            int added = 0;
            while (added < Total && !token.IsCancellationRequested)
            {
                Index.BulkAdd(a =>
                {
                    long now = DateTime.UtcNow.ToFileTimeUtc();
                    for (int i = 0; i < Batch; i++)
                    {
                        int parent = dirs[rng.Next(dirs.Count)];
                        if (rng.Next(12) == 0 && dirs.Count < 6000)
                        {
                            string dn = Words[rng.Next(Words.Length)] + (rng.Next(3) == 0 ? " " + Words[rng.Next(Words.Length)] : "");
                            dirs.Add(a.Add(dn, parent, FileAttributes.Directory, -1, now - rng.NextInt64(0, 5_000_000_000_000_000), now - rng.NextInt64(5_000_000_000_000_000, 9_000_000_000_000_000)));
                            continue;
                        }
                        string name = Words[rng.Next(Words.Length)];
                        if (rng.Next(2) == 0) name += (rng.Next(3) == 0 ? " " : "_") + Words[rng.Next(Words.Length)];
                        if (rng.Next(3) == 0) name += " " + rng.Next(1, 999);
                        string ext = Extensions[rng.Next(Extensions.Length)];
                        var attrs = FileAttributes.Archive;
                        if (rng.Next(40) == 0) attrs |= FileAttributes.ReadOnly;
                        if (rng.Next(60) == 0) attrs |= FileAttributes.Hidden;
                        long size = (long)Math.Pow(10, rng.NextDouble() * 9);
                        long created = now - rng.NextInt64(0, 9_000_000_000_000_000);
                        a.Add(name + "." + ext, parent, attrs, size, created + rng.NextInt64(0, 100_000_000_000), created);
                    }
                });
                added += Batch;
                _status = $"Scanning C:\\ ({Index.LiveCount:N0} objects)";
                StateChanged?.Invoke();
                await Task.Delay(60, token).ConfigureAwait(false);
            }
            SetState(IndexState.Ready, $"{Index.LiveCount:N0} objects");

            // Trickle of live changes so the "re-run search on change" path is exercised.
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(4000, token).ConfigureAwait(false);
                int parent = dirs[rng.Next(dirs.Count)];
                long now = DateTime.UtcNow.ToFileTimeUtc();
                Index.Add($"new {Words[rng.Next(Words.Length)]} {DateTime.Now:HHmmss}.txt", parent, FileAttributes.Archive, rng.Next(1, 100_000), now, now);
                Index.RaiseChanged();
            }
        }, token).ContinueWith(_ => { }, TaskScheduler.Default);
    }

    public void Dispose() => _cts.Cancel();
}
