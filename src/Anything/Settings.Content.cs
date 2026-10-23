using System.Text;
using Anything.Core;
using Anything.Core.Content;

namespace Anything;

/// <summary>Search inside files' settings in RockPreview.ini (content_*, match_content), plus the database path.</summary>
internal sealed partial class AppSettings
{
    /// <summary>Search inside files (Setup, Settings › Indexing): the choice, where and what to read. The index only ever works from a copy.</summary>
    public ContentSettings Content { get; } = new();

    /// <summary>New searches' plain words also find words inside files (Settings › Search; the windows' Search › Match inside files starts from it).</summary>
    public bool MatchContent { get; set; } = true;

    /// <summary>The window's offer to search inside files was answered (Choose… or Not now), or Setup asked already.</summary>
    public bool ContentAsked { get; set; }

    /// <summary>The window's question about reading inside network drives was answered, or Settings' Network drives switch was used.</summary>
    public bool ContentNetworkAsked { get; set; }

    /// <summary>This is the demo's ini (RockPreview-demo.ini), whose defaults let the sample index be searched inside.</summary>
    bool IsDemoFile => string.Equals(Path.GetFileName(FilePath), Path.GetFileName(DefaultPath(demo: true)), StringComparison.OrdinalIgnoreCase);

    static void LoadContent(AppSettings s, Dictionary<string, string> v)
    {
        ContentIni.Load(s.Content, k => v.TryGetValue(k, out var x) ? x : null, s.IsDemoFile);
        // Whitelisted like the content_* switches: 1, 0, true or false; anything else keeps the default.
        s.MatchContent = ContentIni.Bool(v.TryGetValue("match_content", out var mc) ? mc : null, s.MatchContent);
        s.ContentAsked = ContentIni.Bool(v.TryGetValue("content_asked", out var asked) ? asked : null, s.ContentAsked);
        s.ContentNetworkAsked = ContentIni.Bool(v.TryGetValue("content_network_asked", out var netAsked) ? netAsked : null, s.ContentNetworkAsked);
    }

    void SaveContent(StringBuilder sb)
    {
        void W(string k, string value) => sb.Append(k).Append('=').Append(value).Append("\r\n");
        ContentIni.Save(Content, W);
        W("content_asked", ContentAsked ? "1" : "0");
        W("content_network_asked", ContentNetworkAsked ? "1" : "0");
        W("match_content", MatchContent ? "1" : "0");
        // The name database's place, when moved: read since 1.0 but never written before, so a moved database (and the text
        // index beside it, in its Content folder) was forgotten at the next save.
        if (Index.DatabasePath.Length > 0 && !SamePath(Index.DatabasePath, new IndexSettings().DatabasePath))
            W("database_path", Index.DatabasePath.Replace('\r', ' ').Replace('\n', ' '));
    }

    /// <summary>Where the text index lives: the Content folder beside the name database.</summary>
    public string ContentFolder => ContentServices.FolderFor(Index.DatabasePath);
}
