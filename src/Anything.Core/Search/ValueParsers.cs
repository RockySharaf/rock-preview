using System.Globalization;
using System.Text.RegularExpressions;

namespace Anything.Core.Search;

/// <summary>An inclusive value range; for dates a period [start, end) is stored as [start, end - 1].</summary>
internal readonly record struct ValueRange(long Lo, long Hi, bool Unknown = false)
{
    public static ValueRange Point(long v) => new(v, v);
}

/// <summary>One comparison: op applied to a range, or an explicit lo..hi range.</summary>
internal readonly struct NumCond(CompareOp op, ValueRange range, bool isRange)
{
    readonly CompareOp _op = op;
    readonly ValueRange _r = range;
    readonly bool _isRange = isRange;

    public bool Eval(long v, bool known)
    {
        if (_r.Unknown) return _op == CompareOp.NotEqual ? known : !known;
        if (!known) return false;
        if (_isRange) return v >= _r.Lo && v <= _r.Hi;
        return _op switch
        {
            CompareOp.Less => v < _r.Lo,
            CompareOp.LessOrEqual => v <= _r.Hi,
            CompareOp.Greater => v > _r.Hi,
            CompareOp.GreaterOrEqual => v >= _r.Lo,
            CompareOp.NotEqual => v < _r.Lo || v > _r.Hi,
            _ => v >= _r.Lo && v <= _r.Hi,
        };
    }
}

internal static class ValueParsers
{
    const long KB = 1024, MB = KB * 1024, GB = MB * 1024, TB = GB * 1024;

    /// <summary>
    /// Parses a function value list: "a;b" (OR), "lo..hi" ranges, optional per-item operator prefix.
    /// Returns null when the value is empty (no constraint).
    /// </summary>
    public static NumCond[]? ParseConds(string fname, CompareOp op, string value, Func<string, ValueRange?> parseOne, bool allowDashRange)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var list = new List<NumCond>();
        foreach (var rawPart in value.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string part = rawPart;
            var partOp = op;
            var stripped = StripOperator(part, out var pop);
            if (pop != CompareOp.None) { partOp = pop; part = stripped; }

            int dots = part.IndexOf("..", StringComparison.Ordinal);
            if (dots >= 0)
            {
                string a = part[..dots], b = part[(dots + 2)..];
                long lo = long.MinValue, hi = long.MaxValue;
                if (a.Length > 0) lo = (parseOne(a) ?? throw Bad(fname, a)).Lo;
                if (b.Length > 0) hi = (parseOne(b) ?? throw Bad(fname, b)).Hi;
                list.Add(new NumCond(CompareOp.None, new ValueRange(lo, hi), true));
                continue;
            }
            var r = parseOne(part);
            if (r is null && allowDashRange)
            {
                int dash = part.IndexOf('-', 1);
                if (dash > 0 && parseOne(part[..dash]) is { } ra && parseOne(part[(dash + 1)..]) is { } rb)
                {
                    list.Add(new NumCond(CompareOp.None, new ValueRange(ra.Lo, rb.Hi), true));
                    continue;
                }
            }
            if (r is null) throw Bad(fname, part);
            list.Add(new NumCond(partOp, r.Value, false));
        }
        return list.Count == 0 ? null : list.ToArray();
    }

    static string StripOperator(string s, out CompareOp op)
    {
        (string tok, CompareOp op)[] ops =
            [(">=", CompareOp.GreaterOrEqual), ("<=", CompareOp.LessOrEqual), ("!=", CompareOp.NotEqual),
             ("==", CompareOp.Equal), (">", CompareOp.Greater), ("<", CompareOp.Less), ("=", CompareOp.Equal)];
        foreach (var (tok, o) in ops)
            if (s.StartsWith(tok, StringComparison.Ordinal)) { op = o; return s[tok.Length..]; }
        op = CompareOp.None;
        return s;
    }

    static QueryException Bad(string fname, string v) => new($"{fname}: invalid value '{v}'");

    public static bool Eval(NumCond[] conds, long v, bool known)
    {
        foreach (var c in conds) if (c.Eval(v, known)) return true;
        return false;
    }

    // ---------------- integers ----------------

    public static ValueRange? ParseInt(string s) =>
        long.TryParse(s.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? ValueRange.Point(v) : null;

    // ---------------- sizes ----------------

    static readonly Regex SizeRx = new(@"^\s*(\d+(?:\.\d+)?|\.\d+)\s*([a-z]*)\s*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static ValueRange? ParseSize(string s)
    {
        switch (s.Trim().ToLowerInvariant())
        {
            case "empty": return new ValueRange(0, 0);
            case "tiny": return new ValueRange(1, 10 * KB);
            case "small": return new ValueRange(10 * KB + 1, 100 * KB);
            case "medium": return new ValueRange(100 * KB + 1, MB);
            case "large": return new ValueRange(MB + 1, 16 * MB);
            case "huge": return new ValueRange(16 * MB + 1, 128 * MB);
            case "gigantic": return new ValueRange(128 * MB + 1, long.MaxValue);
            case "unknown": return new ValueRange(0, 0, Unknown: true);
        }
        var m = SizeRx.Match(s);
        if (!m.Success) return null;
        double n = double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture);
        long mult = m.Groups[2].Value.ToLowerInvariant() switch
        {
            "" or "b" or "byte" or "bytes" => 1,
            "k" or "kb" or "kib" => KB,
            "m" or "mb" or "mib" => MB,
            "g" or "gb" or "gib" => GB,
            "t" or "tb" or "tib" => TB,
            _ => -1,
        };
        if (mult < 0) return null;
        double bytes = n * mult;
        if (bytes > long.MaxValue) return null;
        return ValueRange.Point((long)Math.Round(bytes));
    }

    // ---------------- dates ----------------

    static readonly Regex IsoRx = new(
        @"^(?<y>\d{4})(?:[-/.](?<m>\d{1,2})(?:[-/.](?<d>\d{1,2})(?:[Tt _](?<h>\d{1,2})(?::(?<mi>\d{2})(?::(?<s>\d{2}))?)?)?)?)?$",
        RegexOptions.CultureInvariant);

    static readonly Regex LocalRx = new(
        @"^(?<a>\d{1,2})[-/.](?<b>\d{1,2})[-/.](?<y>\d{4}|\d{2})(?:[Tt _](?<h>\d{1,2})(?::(?<mi>\d{2})(?::(?<s>\d{2}))?)?)?$",
        RegexOptions.CultureInvariant);

    static readonly Regex RelativeRx = new(
        @"^(?<dir>last|past|prev|previous|next|coming)(?<n>\d+)(?<unit>years?|months?|weeks?|days?|hours?|minutes?|mins?|seconds?|secs?)$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);

    static readonly string[] MonthNames =
        ["january", "february", "march", "april", "may", "june", "july", "august", "september", "october", "november", "december"];

    /// <summary>Parses a date value into the local period it denotes, as UTC FILETIME [start, end - 1].</summary>
    public static ValueRange? ParseDate(string raw, DateTime now)
    {
        string s = raw.Trim();
        if (s.Length == 0) return null;
        var p = ParsePeriod(s.ToLowerInvariant(), now);
        if (p is null) return null;
        if (p.Value.Unknown) return new ValueRange(0, 0, Unknown: true);
        long start = ToFileTime(p.Value.Start), end = ToFileTime(p.Value.End);
        return new ValueRange(start, Math.Max(start, end - 1));
    }

    internal readonly record struct Period(DateTime Start, DateTime End, bool Unknown = false);

    static long ToFileTime(DateTime local)
    {
        if (local.Year < 1601) return 0;
        try { return DateTime.SpecifyKind(local, DateTimeKind.Local).ToFileTimeUtc(); }
        catch (ArgumentOutOfRangeException) { return local.Year < 2000 ? 0 : long.MaxValue; }
    }

    internal static Period? ParsePeriod(string s, DateTime now)
    {
        DateTime today = now.Date;
        var firstDay = CultureInfo.CurrentCulture.DateTimeFormat.FirstDayOfWeek;
        DateTime weekStart = today.AddDays(-(((int)today.DayOfWeek - (int)firstDay + 7) % 7));
        DateTime monthStart = new(today.Year, today.Month, 1);
        DateTime yearStart = new(today.Year, 1, 1);

        switch (s)
        {
            case "unknown": return new Period(default, default, true);
            case "today": return new Period(today, today.AddDays(1));
            case "yesterday": return new Period(today.AddDays(-1), today);
            case "tomorrow": return new Period(today.AddDays(1), today.AddDays(2));
            case "thisweek" or "currentweek": return new Period(weekStart, weekStart.AddDays(7));
            case "lastweek" or "prevweek" or "previousweek": return new Period(weekStart.AddDays(-7), weekStart);
            case "nextweek" or "comingweek": return new Period(weekStart.AddDays(7), weekStart.AddDays(14));
            case "thismonth" or "currentmonth": return new Period(monthStart, monthStart.AddMonths(1));
            case "lastmonth" or "prevmonth" or "previousmonth": return new Period(monthStart.AddMonths(-1), monthStart);
            case "nextmonth" or "comingmonth": return new Period(monthStart.AddMonths(1), monthStart.AddMonths(2));
            case "thisyear" or "currentyear": return new Period(yearStart, yearStart.AddYears(1));
            case "lastyear" or "prevyear" or "previousyear": return new Period(yearStart.AddYears(-1), yearStart);
            case "nextyear" or "comingyear": return new Period(yearStart.AddYears(1), yearStart.AddYears(2));
            case "pastday": return new Period(now.AddDays(-1), now.AddTicks(1));
            case "pastweek": return new Period(now.AddDays(-7), now.AddTicks(1));
            case "pastmonth": return new Period(now.AddMonths(-1), now.AddTicks(1));
            case "pastyear": return new Period(now.AddYears(-1), now.AddTicks(1));
        }

        for (int i = 0; i < 12; i++)
        {
            if (s == MonthNames[i] || (s.Length == 3 && MonthNames[i].StartsWith(s, StringComparison.Ordinal)))
            {
                var ms = new DateTime(today.Year, i + 1, 1);
                return new Period(ms, ms.AddMonths(1));
            }
        }

        var rel = RelativeRx.Match(s);
        if (rel.Success)
        {
            if (!int.TryParse(rel.Groups["n"].Value, CultureInfo.InvariantCulture, out int n)) return null;
            bool forward = rel.Groups["dir"].Value is "next" or "coming";
            int k = forward ? n : -n;
            string unit = rel.Groups["unit"].Value;
            DateTime other;
            try
            {
                other = unit[0] switch
                {
                    'y' => now.AddYears(k),
                    'w' => now.AddDays(7.0 * k),
                    'd' => now.AddDays(k),
                    'h' => now.AddHours(k),
                    's' => now.AddSeconds(k),
                    _ when unit.StartsWith("mo", StringComparison.Ordinal) => now.AddMonths(k),
                    _ => now.AddMinutes(k),
                };
            }
            catch (ArgumentOutOfRangeException) { return null; }
            return forward ? new Period(now, other.AddTicks(1)) : new Period(other, now.AddTicks(1));
        }

        var iso = IsoRx.Match(s);
        if (iso.Success) return FromParts(iso, int.Parse(iso.Groups["y"].Value, CultureInfo.InvariantCulture),
            iso.Groups["m"], iso.Groups["d"]);

        var loc = LocalRx.Match(s);
        if (loc.Success)
        {
            int a = int.Parse(loc.Groups["a"].Value, CultureInfo.InvariantCulture);
            int b = int.Parse(loc.Groups["b"].Value, CultureInfo.InvariantCulture);
            int y = int.Parse(loc.Groups["y"].Value, CultureInfo.InvariantCulture);
            if (loc.Groups["y"].Value.Length == 2) y += y < 70 ? 2000 : 1900;
            bool dayFirst;
            if (a > 12) dayFirst = true;
            else if (b > 12) dayFirst = false;
            else
            {
                string pat = CultureInfo.CurrentCulture.DateTimeFormat.ShortDatePattern;
                int di = pat.IndexOf('d'), mi = pat.IndexOf('M');
                dayFirst = di >= 0 && mi >= 0 && di < mi;
            }
            int day = dayFirst ? a : b, month = dayFirst ? b : a;
            return Build(loc, y, month, day);
        }
        return null;
    }

    static Period? FromParts(Match m, int y, Group month, Group day)
    {
        if (!month.Success)
        {
            if (y < 1 || y > 9998) return null;
            var ys = new DateTime(y, 1, 1);
            return new Period(ys, ys.AddYears(1));
        }
        int mo = int.Parse(month.Value, CultureInfo.InvariantCulture);
        if (!day.Success)
        {
            if (mo is < 1 or > 12) return null;
            var ms = new DateTime(y, mo, 1);
            return new Period(ms, ms.AddMonths(1));
        }
        return Build(m, y, mo, int.Parse(day.Value, CultureInfo.InvariantCulture));
    }

    static Period? Build(Match m, int y, int mo, int d)
    {
        if (y is < 1 or > 9998 || mo is < 1 or > 12 || d < 1 || d > DateTime.DaysInMonth(y, mo)) return null;
        var date = new DateTime(y, mo, d);
        var h = m.Groups["h"];
        if (!h.Success) return new Period(date, date.AddDays(1));
        int hour = int.Parse(h.Value, CultureInfo.InvariantCulture);
        if (hour > 23) return null;
        var mi = m.Groups["mi"];
        if (!mi.Success) { var hs = date.AddHours(hour); return new Period(hs, hs.AddHours(1)); }
        int minute = int.Parse(mi.Value, CultureInfo.InvariantCulture);
        if (minute > 59) return null;
        var sec = m.Groups["s"];
        if (!sec.Success) { var ms = date.AddHours(hour).AddMinutes(minute); return new Period(ms, ms.AddMinutes(1)); }
        int second = int.Parse(sec.Value, CultureInfo.InvariantCulture);
        if (second > 59) return null;
        var ss = date.AddHours(hour).AddMinutes(minute).AddSeconds(second);
        return new Period(ss, ss.AddSeconds(1));
    }

    // ---------------- attributes ----------------

    public static FileAttributes? ParseAttributes(string s)
    {
        FileAttributes mask = 0;
        foreach (char ch in s.Trim())
        {
            mask |= char.ToUpperInvariant(ch) switch
            {
                'A' => FileAttributes.Archive,
                'C' => FileAttributes.Compressed,
                'D' => FileAttributes.Directory,
                'E' => FileAttributes.Encrypted,
                'H' => FileAttributes.Hidden,
                'I' => FileAttributes.NotContentIndexed,
                'L' => FileAttributes.ReparsePoint,
                'N' => FileAttributes.Normal,
                'O' => FileAttributes.Offline,
                'P' => FileAttributes.SparseFile,
                'R' => FileAttributes.ReadOnly,
                'S' => FileAttributes.System,
                'T' => FileAttributes.Temporary,
                'U' => (FileAttributes)0x00100000, // FILE_ATTRIBUTE_UNPINNED
                'V' => FileAttributes.IntegrityStream,
                _ => (FileAttributes)(-1),
            };
            if (mask == (FileAttributes)(-1)) return null;
        }
        return mask;
    }

    // ---------------- macros / types ----------------

    public static readonly Dictionary<string, string> MacroExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["audio"] = "aac;ac3;aif;aifc;aiff;au;cda;dts;fla;flac;it;m1a;m2a;m3u;m4a;mid;midi;mka;mod;mp2;mp3;mpa;ogg;ra;rmi;spc;rmi;snd;umx;voc;wav;wma;xm",
        ["zip"] = "7z;ace;arj;bz2;cab;gz;gzip;jar;r00;r01;r02;r03;r04;r05;r06;r07;r08;r09;r10;r11;r12;r13;r14;r15;r16;r17;r18;r19;r20;r21;r22;r23;r24;r25;r26;r27;r28;r29;rar;tar;tgz;z;zip",
        ["doc"] = "c;chm;cpp;csv;cxx;doc;docm;docx;dot;dotm;dotx;h;hpp;htm;html;hxx;ini;java;lua;mht;mhtml;odt;pdf;potx;potm;ppam;ppsm;ppsx;pps;ppt;pptm;pptx;rtf;sldm;sldx;thmx;txt;vsd;wpd;wps;wri;xlam;xls;xlsb;xlsm;xlsx;xltm;xltx;xml",
        ["exe"] = "bat;cmd;exe;msi;msp;scr",
        ["pic"] = "ani;bmp;gif;ico;jpe;jpeg;jpg;pcx;png;psd;tga;tif;tiff;webp;wmf",
        ["video"] = "3g2;3gp;3gp2;3gpp;amr;amv;asf;avi;bdmv;bik;d2v;divx;drc;dsa;dsm;dss;dsv;evo;f4v;flc;fli;flic;flv;hdmov;ifo;ivf;m1v;m2p;m2t;m2ts;m2v;m4b;m4p;m4v;mkv;mp2v;mp4;mp4v;mpe;mpeg;mpg;mpls;mpv2;mpv4;mov;mts;ogm;ogv;pss;pva;qt;ram;ratdvd;rm;rmm;rmvb;roq;rpm;smil;smk;swf;tp;tpr;ts;vob;vp6;webm;wm;wmp;wmv",
    };

    static readonly Dictionary<string, string> KnownTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        ["txt"] = "Text Document", ["log"] = "Text Document",
        ["exe"] = "Application", ["dll"] = "Application extension", ["sys"] = "System file",
        ["bat"] = "Windows Batch File", ["cmd"] = "Windows Command Script", ["ps1"] = "Windows PowerShell Script",
        ["ini"] = "Configuration settings", ["lnk"] = "Shortcut", ["url"] = "Internet Shortcut",
        ["jpg"] = "JPG File", ["jpeg"] = "JPEG File", ["png"] = "PNG File", ["gif"] = "GIF File", ["bmp"] = "BMP File",
        ["mp3"] = "MP3 File", ["wav"] = "WAV File", ["mp4"] = "MP4 File", ["mkv"] = "MKV File", ["avi"] = "AVI File",
        ["zip"] = "Compressed (zipped) Folder", ["7z"] = "7Z File", ["rar"] = "RAR File",
        ["pdf"] = "PDF Document", ["htm"] = "HTML Document", ["html"] = "HTML Document",
        ["doc"] = "Microsoft Word 97-2003 Document", ["docx"] = "Microsoft Word Document",
        ["xls"] = "Microsoft Excel 97-2003 Worksheet", ["xlsx"] = "Microsoft Excel Worksheet",
        ["ppt"] = "Microsoft PowerPoint 97-2003 Presentation", ["pptx"] = "Microsoft PowerPoint Presentation",
        ["cs"] = "C# Source File", ["json"] = "JSON File", ["xml"] = "XML Document", ["msi"] = "Windows Installer Package",
    };

    /// <summary>Approximate Explorer "Type" column: "File folder" for folders, a known name or "EXT File".</summary>
    public static string TypeName(bool isFolder, string ext)
    {
        if (isFolder) return "File folder";
        if (ext.Length == 0) return "File";
        return KnownTypes.TryGetValue(ext, out var t) ? t : ext.ToUpperInvariant() + " File";
    }
}
