using System.Globalization;
using Anything.Core;
using Anything.Core.Indexing;
using SortKey = Anything.Core.SortKey;

// rockpreview-cli: command-line search over the Rock Preview database (like Everything's es.exe).
// It reads the database the Rock Preview app saves, so results reflect the last save.

var opts = new SearchOptions();
var query = new List<string>();
SortKey sort = SortKey.Name;
bool descending = false, showSize = false, showDate = false, showAccessed = false, showRuns = false, folderSizes = false, countOnly = false;
int max = int.MaxValue, offset = 0;
string? db = null;

for (int i = 0; i < args.Length; i++)
{
    string a = args[i];
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"{a} needs a value");
    switch (a.ToLowerInvariant())
    {
        case "-h" or "-help" or "--help" or "/?": PrintHelp(); return 0;
        case "-i" or "-case": opts = opts with { MatchCase = true }; break;
        case "-w" or "-ww" or "-whole-word": opts = opts with { MatchWholeWord = true }; break;
        case "-p" or "-match-path": opts = opts with { MatchPath = true }; break;
        case "-a" or "-diacritics": opts = opts with { MatchDiacritics = true }; break;
        case "-r" or "-regex": opts = opts with { Regex = true }; query.Add(Next()); break;
        case "-n" or "-max-results": max = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "-o" or "-offset": offset = int.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "-s" or "-sort-path": sort = SortKey.Path; break;
        case "-sort": sort = Enum.Parse<SortKey>(Next().Replace("-", ""), ignoreCase: true); break;
        case "-sort-descending" or "-desc": descending = true; break;
        case "-size": showSize = true; break;
        case "-dm" or "-date-modified": showDate = true; break;
        case "-da" or "-date-accessed": showAccessed = true; break;
        case "-run-count" or "-date-run": showRuns = true; break;
        case "-folder-sizes": folderSizes = true; break;
        case "-get-result-count" or "-count": countOnly = true; break;
        case "-db": db = Next(); break;
        case "-path" or "-parent-path": query.Add($"\"{Next().TrimEnd('\\')}\\\""); break;
        default: query.Add(a.Contains(' ') && !a.StartsWith('"') ? $"\"{a}\"" : a); break;
    }
}

db ??= new IndexSettings().DatabasePath;
if (!File.Exists(db))
{
    Console.Error.WriteLine($"No database at {db}. Start Ultimate File Explorer once so it indexes and saves, or pass -db <path>.");
    return 2;
}

var index = new FileIndex();
// Dates accessed are kept when the app saved them (Settings › Indexing › Date accessed); else they are read from the disk.
index.Configure(dateAccessed: true, folderSizes: false, recentChanges: false);
var loaded = IndexDatabase.Load(db, index);
if (loaded is null)
{
    Console.Error.WriteLine($"Could not read database {db}.");
    return 2;
}
index.Configure(dateAccessed: loaded.Any(r => r.HasDateAccessed), folderSizes: folderSizes, recentChanges: false);
// The app's run history (runcount:, dr:), from beside its settings.
index.RunHistory.Load(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Rock Preview", "Run History.csv"));

var engine = Services.CreateSearchEngine();
var results = engine.Search(index, string.Join(' ', query), opts, sort, descending, CancellationToken.None);
if (results.Error is { } err)
{
    Console.Error.WriteLine(err);
    return 1;
}
if (countOnly)
{
    Console.WriteLine(results.Ids.Length);
    return 0;
}

var output = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = false };
foreach (int id in results.Ids.Skip(offset).Take(max))
{
    if (showSize) output.Write($"{(index.Size(id) < 0 ? "" : index.Size(id).ToString(CultureInfo.InvariantCulture)),15} ");
    if (showDate) output.Write(Date(index.Modified(id)) + " ");
    if (showAccessed) output.Write(Date(index.HasDateAccessed ? index.Accessed(id) : File.GetLastAccessTimeUtc(index.GetFullPath(id)).ToFileTimeUtc()) + " ");
    if (showRuns)
    {
        var run = index.RunHistory.Get(index.GetFullPath(id));
        output.Write($"{(run.Count > 0 ? run.Count.ToString(CultureInfo.InvariantCulture) : ""),6} {Date(run.LastRun)} ");
    }
    output.WriteLine(index.GetFullPath(id));
}
output.Flush();
return 0;

static string Date(long ft) => ft > 0 ? DateTime.FromFileTimeUtc(ft).ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) : "".PadRight(16);

static void PrintHelp() => Console.WriteLine("""
    rockpreview-cli [options] [search text]
    Searches the Rock Preview index using Everything's search syntax (ext:, size:, dm:, da:, runcount:, |, !, < >, wildcards ...).

      -i, -case            match case            -w, -ww           match whole words
      -p, -match-path      match full path       -a, -diacritics   match diacritics
      -r, -regex <regex>   regex search          -path <folder>    limit to a folder
      -n, -max-results <n> limit results         -o, -offset <n>   skip results
      -s, -sort-path       sort by path          -sort <key>       name|path|size|extension|type|date-modified|date-created|
                                                                   attributes|run-count|date-recently-changed|date-accessed|date-run
      -desc                descending            -size, -dm, -da   also print size / date modified / date accessed
      -run-count           also print run count and date run      -folder-sizes   folders get the size of everything in them
      -count               print only the number of results
      -db <file>           database file (default %LOCALAPPDATA%\Rock Preview\RockPreview.db)
    """);
