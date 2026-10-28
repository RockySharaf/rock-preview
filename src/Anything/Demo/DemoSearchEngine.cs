using System.Diagnostics;
using System.Globalization;
using SortKey = Anything.Core.SortKey;
using System.Text.RegularExpressions;
using Anything.Core;
using Anything.Core.Search;

namespace Anything.Demo;

/// <summary>
/// Trivial search engine for --demo: space = AND, | = OR, ! = NOT, "quotes", * and ? wildcards,
/// ext:a;b, file:, folder:, and the filter macros (audio:, zip:, doc:, exe:, pic:, video:).
/// The filter is read with <see cref="QueryParser.FilterOptions"/>, like the real engine reads it.
/// </summary>
internal sealed class DemoSearchEngine : ISearchEngine
{
    static readonly Dictionary<string, HashSet<string>> Macros = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audio"] = Set("aac;ac3;aif;aifc;aiff;au;cda;dts;fla;flac;it;m1a;m2a;m3u;m4a;mid;midi;mka;mod;mp2;mp3;mpa;ogg;ra;rmi;spc;snd;umx;voc;wav;wma;xm"),
        ["zip"] = Set("7z;ace;arj;bz2;cab;gz;gzip;jar;r00;r01;r02;r03;r04;r05;r06;r07;r08;r09;r10;r11;r12;r13;r14;r15;r16;r17;r18;r19;r20;r21;r22;r23;r24;r25;r26;r27;r28;r29;rar;tar;tgz;z;zip"),
        ["doc"] = Set("c;chm;cpp;csv;cxx;doc;docm;docx;dot;dotm;dotx;h;hpp;htm;html;hxx;ini;java;lua;mht;mhtml;odt;pdf;potx;potm;ppam;ppsm;ppsx;pps;ppt;pptm;pptx;rtf;sldm;sldx;thmx;txt;vsd;wpd;wps;wri;xlam;xls;xlsb;xlsm;xlsx;xltm;xltx;xml"),
        ["exe"] = Set("bat;cmd;exe;msi;msp;scr"),
        ["pic"] = Set("ani;bmp;gif;ico;jpe;jpeg;jpg;pcx;png;psd;tga;tif;tiff;webp;wmf"),
        ["video"] = Set("3g2;3gp;3gp2;3gpp;amr;amv;asf;avi;bdmv;bik;d2v;divx;drc;dsa;dsm;dss;dsv;evo;f4v;flc;fli;flic;flv;hdmov;ifo;ivf;m1v;m2p;m2t;m2ts;m2v;m4b;m4p;m4v;mkv;mp2v;mp4;mp4v;mpe;mpeg;mpg;mpls;mpv2;mpv4;mov;mts;ogm;ogv;pss;pva;qt;ram;ratdvd;rm;rmm;rmvb;roq;rpm;smil;smk;swf;tp;tpr;ts;vob;vp6;webm;wm;wmp;wmv"),
    };

    static HashSet<string> Set(string s) => new(s.Split(';'), StringComparer.OrdinalIgnoreCase);

    sealed class Term
    {
        public bool Not;
        public List<Alt> Alts = [];
    }

    sealed class Alt
    {
        public string Text = "";
        public bool UsePath;
        public Regex? Wildcard;
        public HashSet<string>? Extensions;
        public bool? Folder;
        public CompareOptions Compare;
        public bool WholeWord;
    }

    public SearchResults Search(FileIndex index, string query, SearchOptions options, SortKey sort, bool descending, CancellationToken cancellationToken)
    {
        var sw = Stopwatch.StartNew();
        Regex? regex = null;
        List<Term> terms;
        int queryTerms;
        try
        {
            if (options.Regex && !string.IsNullOrWhiteSpace(query))
            {
                regex = new Regex(query, RegexOptions.CultureInvariant | (options.MatchCase ? 0 : RegexOptions.IgnoreCase));
                terms = [];
            }
            else
            {
                terms = Parse(query ?? "", options);
            }
            queryTerms = terms.Count;
            // The app builds the filter (macros, folder scopes): Match whole words or Match case must not empty it.
            terms.AddRange(Parse(options.Filter ?? "", QueryParser.FilterOptions));
        }
        catch (ArgumentException ex)
        {
            return new SearchResults { Ids = [], Error = "Invalid regular expression: " + ex.Message };
        }

        var compare = CultureInfo.InvariantCulture.CompareInfo;

        index.Lock.EnterReadLock();
        try
        {
            var ids = new List<int>();
            int count = index.Count;
            // Thumbnails while searching (SearchOptions.InsideFolders): per folder, 1 = it or a folder above it is found.
            var found = options.InsideFolders ? new sbyte[count] : null;
            for (int id = 0; id < count; id++)
            {
                if ((id & 0x3FFF) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (!index.IsAlive(id)) continue;
                if (found is null) { if (Matches(id, 0, terms.Count, withRegex: true)) ids.Add(id); }
                else if (!index.IsDirectory(id) && Matches(id, queryTerms, terms.Count, withRegex: false)
                    && (Matches(id, 0, queryTerms, withRegex: true) || FolderFound(found, index.Parent(id))))
                    ids.Add(id);
            }
            var arr = ids.ToArray();
            SortIds(index, arr, sort, descending, cancellationToken);
            return new SearchResults { Ids = arr, Elapsed = sw.Elapsed };

            // A folder found by the query, or one above it; a root (a drive) never counts, as in the real engine.
            bool FolderFound(sbyte[] found, int folder)
            {
                var unknown = new List<int>();
                sbyte answer = -1;
                for (int cur = folder; cur != FileIndex.NoParent; cur = index.Parent(cur))
                {
                    if (found[cur] != 0) { answer = found[cur]; break; }
                    if (index.Parent(cur) != FileIndex.NoParent && Matches(cur, 0, queryTerms, withRegex: true)) { found[cur] = answer = 1; break; }
                    unknown.Add(cur);
                }
                foreach (int f in unknown) found[f] = answer;
                return answer > 0;
            }

            // The query's terms are [0, queryTerms), the filter's the rest; the regex is the query's.
            bool Matches(int id, int from, int to, bool withRegex)
            {
                string name = index.Name(id);
                string? full = null;
                if (withRegex && regex is not null && !regex.IsMatch(options.MatchPath ? (full ??= index.GetFullPath(id)) : name)) return false;
                for (int i = from; i < to; i++)
                {
                    var t = terms[i];
                    bool any = false;
                    foreach (var a in t.Alts)
                    {
                        if (a.Folder is { } f && index.IsDirectory(id) != f) continue;
                        if (a.Extensions is not null && !a.Extensions.Contains(index.Extension(id))) continue;
                        if (a.Text.Length > 0)
                        {
                            string hay = a.UsePath ? (full ??= index.GetFullPath(id)) : name;
                            if (a.Wildcard is not null) { if (!a.Wildcard.IsMatch(hay)) continue; }
                            else if (!Contains(compare, hay, a.Text, a.Compare, a.WholeWord)) continue;
                        }
                        any = true;
                        break;
                    }
                    if (any == t.Not) return false;
                }
                return true;
            }
        }
        finally { index.Lock.ExitReadLock(); }
    }

    static bool Contains(CompareInfo ci, string hay, string needle, CompareOptions o, bool wholeWord)
    {
        int start = 0;
        while (start <= hay.Length)
        {
            int i = ci.IndexOf(hay, needle, start, o);
            if (i < 0) return false;
            if (!wholeWord) return true;
            int end = i + needle.Length;
            bool left = i == 0 || !char.IsLetterOrDigit(hay[i - 1]);
            bool right = end >= hay.Length || !char.IsLetterOrDigit(hay[end]);
            if (left && right) return true;
            start = i + 1;
        }
        return false;
    }

    static List<Term> Parse(string text, SearchOptions o)
    {
        var terms = new List<Term>();
        var cmp = (o.MatchCase ? CompareOptions.None : CompareOptions.IgnoreCase) | (o.MatchDiacritics ? 0 : CompareOptions.IgnoreNonSpace);
        foreach (var token in Tokenize(text))
        {
            var t = new Term();
            string tok = token;
            if (tok.StartsWith('!')) { t.Not = true; tok = tok[1..]; }
            foreach (var part in tok.Split('|'))
            {
                string p = part.Replace("\"", "");
                var alt = new Alt { Compare = cmp, WholeWord = o.MatchWholeWord };
                int colon = p.IndexOf(':');
                if (colon > 0 && colon < 12 && p.IndexOf('\\') < 0)
                {
                    string fn = p[..colon];
                    string arg = p[(colon + 1)..];
                    if (fn.Equals("ext", StringComparison.OrdinalIgnoreCase)) { alt.Extensions = Set(arg); arg = ""; }
                    else if (fn.Equals("folder", StringComparison.OrdinalIgnoreCase)) alt.Folder = true;
                    else if (fn.Equals("file", StringComparison.OrdinalIgnoreCase)) alt.Folder = false;
                    else if (Macros.TryGetValue(fn, out var set)) { alt.Extensions = set; alt.Folder = false; }
                    else arg = p;
                    p = arg;
                }
                alt.Text = p;
                alt.UsePath = o.MatchPath || p.Contains('\\');
                if (p.IndexOfAny(['*', '?']) >= 0)
                {
                    string pattern = "^" + Regex.Escape(p).Replace(@"\*", ".*").Replace(@"\?", ".") + "$";
                    alt.Wildcard = new Regex(pattern, RegexOptions.CultureInvariant | (o.MatchCase ? 0 : RegexOptions.IgnoreCase));
                }
                if (alt.Text.Length > 0 || alt.Extensions is not null || alt.Folder is not null) t.Alts.Add(alt);
            }
            if (t.Alts.Count > 0) terms.Add(t);
        }
        return terms;
    }

    static IEnumerable<string> Tokenize(string text)
    {
        var sb = new System.Text.StringBuilder();
        bool quoted = false;
        foreach (char c in text)
        {
            if (c == '"') { quoted = !quoted; sb.Append(c); continue; }
            if (char.IsWhiteSpace(c) && !quoted)
            {
                if (sb.Length > 0) { yield return sb.ToString(); sb.Clear(); }
                continue;
            }
            sb.Append(c);
        }
        if (sb.Length > 0) yield return sb.ToString();
    }

    static void SortIds(FileIndex index, int[] ids, SortKey sort, bool descending, CancellationToken ct)
    {
        switch (sort)
        {
            case SortKey.Path:
                SortBy(ids, id => index.GetParentPath(id) + "\\" + index.Name(id), StringComparer.OrdinalIgnoreCase);
                break;
            case SortKey.Size:
                SortBy(ids, id => index.IsDirectory(id) ? -1 : index.Size(id), Comparer<long>.Default);
                break;
            case SortKey.Extension:
            case SortKey.Type:
                SortBy(ids, id => index.Extension(id) + "\0" + index.Name(id), StringComparer.OrdinalIgnoreCase);
                break;
            case SortKey.DateModified:
                SortBy(ids, index.Modified, Comparer<long>.Default);
                break;
            case SortKey.DateCreated:
                SortBy(ids, index.Created, Comparer<long>.Default);
                break;
            case SortKey.Attributes:
                SortBy(ids, id => (int)index.Attributes(id), Comparer<int>.Default);
                break;
            default:
                SortBy(ids, index.Name, StringComparer.OrdinalIgnoreCase);
                break;
        }
        ct.ThrowIfCancellationRequested();
        if (descending) Array.Reverse(ids);
    }

    static void SortBy<T>(int[] ids, Func<int, T> key, IComparer<T> comparer)
    {
        var keys = new T[ids.Length];
        for (int i = 0; i < ids.Length; i++) keys[i] = key(ids[i]);
        Array.Sort(keys, ids, comparer);
    }

    public IReadOnlyList<(int Start, int Length)> GetHighlights(string query, SearchOptions options, string text)
    {
        if (string.IsNullOrEmpty(text) || string.IsNullOrWhiteSpace(query)) return [];
        var ranges = new List<(int Start, int Length)>();
        try
        {
            if (options.Regex)
            {
                var re = new Regex(query, RegexOptions.CultureInvariant | (options.MatchCase ? 0 : RegexOptions.IgnoreCase));
                foreach (Match m in re.Matches(text)) if (m.Length > 0) ranges.Add((m.Index, m.Length));
            }
            else
            {
                var ci = CultureInfo.InvariantCulture.CompareInfo;
                var cmp = (options.MatchCase ? CompareOptions.None : CompareOptions.IgnoreCase) | (options.MatchDiacritics ? 0 : CompareOptions.IgnoreNonSpace);
                foreach (var t in Parse(query, options))
                {
                    if (t.Not) continue;
                    foreach (var a in t.Alts)
                    {
                        if (a.Wildcard is not null || a.Text.Length == 0) continue;
                        foreach (var piece in a.Text.Split('\\', StringSplitOptions.RemoveEmptyEntries))
                        {
                            int start = 0;
                            while (start < text.Length)
                            {
                                int i = ci.IndexOf(text.AsSpan(start), piece.AsSpan(), cmp, out int len);
                                if (i < 0 || len == 0) break;
                                i += start;
                                ranges.Add((i, len));
                                start = i + len;
                            }
                        }
                    }
                }
            }
        }
        catch (ArgumentException) { return []; }
        if (ranges.Count <= 1) return ranges;
        ranges.Sort((a, b) => a.Start.CompareTo(b.Start));
        var merged = new List<(int Start, int Length)> { ranges[0] };
        for (int i = 1; i < ranges.Count; i++)
        {
            var last = merged[^1];
            var r = ranges[i];
            if (r.Start <= last.Start + last.Length)
                merged[^1] = (last.Start, Math.Max(last.Start + last.Length, r.Start + r.Length) - last.Start);
            else merged.Add(r);
        }
        return merged;
    }
}
