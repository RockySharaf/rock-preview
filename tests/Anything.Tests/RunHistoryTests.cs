using Anything.Core;
using Anything.Core.Search;

namespace Anything.Tests;

/// <summary>Run history: Everything's Run History.csv format, and matching paths to index ids as both change.</summary>
public sealed class RunHistoryTests : IDisposable
{
    readonly string _dir = Directory.CreateTempSubdirectory("anything-runs-").FullName;

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { }
    }

    [Fact]
    public void Reads_Everythings_file_as_written_on_a_real_PC()
    {
        string file = Path.Combine(_dir, "Run History.csv");
        File.WriteAllText(file, """
            Filename,Run Count,Last Run Date
            "E:\Desktop\New folder\Order Confirmation\6363_OrderConfirmation.rpt",21,134320502716270325
            "C:\Users\ROG\bot Gray\tb-particle-wind.mp4",8,134323647930563419
            "E:\Desktop\Afees",3,134318996582931505
            C:\Unquoted, with a comma.txt,2,134318996582931506
            "C:\Has ""quotes"".txt",1,1
            "C:\no count.txt",0,5
            garbage
            """);
        var history = new RunHistory();
        history.Load(file);

        Assert.Equal(5, history.Count);
        Assert.Equal(new RunHistory.Entry(21, 134320502716270325), history.Get(@"e:\desktop\new folder\order confirmation\6363_OrderConfirmation.rpt"));
        Assert.Equal(3, history.Get(@"E:\Desktop\Afees\").Count); // a folder, with or without the backslash
        Assert.Equal(2, history.Get(@"C:\Unquoted, with a comma.txt").Count);
        Assert.Equal(1, history.Get("C:\\Has \"quotes\".txt").Count);
        Assert.Equal(0, history.Get(@"C:\no count.txt").Count);
        Assert.False(history.IsDirty);
    }

    [Fact]
    public void Saves_in_Everythings_format_and_reads_it_back()
    {
        var history = new RunHistory();
        Assert.Equal(1, history.Increment(@"C:\a.txt", 100));
        Assert.Equal(2, history.Increment(@"c:\A.TXT", 200));
        history.Increment(@"C:\", 300);
        history.Set(@"D:\b, c.txt", 5, 400);
        Assert.True(history.IsDirty);

        string file = Path.Combine(_dir, "sub", "Run History.csv");
        history.Save(file);
        Assert.False(history.IsDirty);
        var lines = File.ReadAllLines(file);
        Assert.Equal("Filename,Run Count,Last Run Date", lines[0]);
        Assert.Equal("\"D:\\b, c.txt\",5,400", lines[1]); // most runs first
        Assert.Contains("\"C:\\a.txt\",2,200", lines);

        var again = new RunHistory();
        again.Load(file);
        Assert.Equal(new RunHistory.Entry(2, 200), again.Get(@"C:\a.txt"));
        Assert.Equal(new RunHistory.Entry(1, 300), again.Get("C:"));
        Assert.Equal(5, again.Get(@"D:\b, c.txt").Count);

        again.Set(@"C:\a.txt", 0, 500); // 0 forgets it, as in Everything
        Assert.Equal(0, again.Get(@"C:\a.txt").Count);
        Assert.Equal(2, again.Count);
    }

    [Fact]
    public void Import_keeps_the_higher_count_and_the_later_date()
    {
        string theirs = Path.Combine(_dir, "everything.csv");
        File.WriteAllText(theirs, "Filename,Run Count,Last Run Date\r\n\"C:\\a.txt\",9,50\r\n\"C:\\b.txt\",1,900\r\n\"C:\\new.txt\",4,10\r\n");
        var history = new RunHistory();
        history.Set(@"C:\a.txt", 2, 100);
        history.Set(@"C:\b.txt", 3, 100);
        int changes = 0;
        history.Changed += () => changes++;

        Assert.Equal(3, history.Import(theirs));
        Assert.Equal(new RunHistory.Entry(9, 100), history.Get(@"C:\a.txt"));
        Assert.Equal(new RunHistory.Entry(3, 900), history.Get(@"C:\b.txt"));
        Assert.Equal(new RunHistory.Entry(4, 10), history.Get(@"C:\new.txt"));
        Assert.Equal(1, changes);
        Assert.Equal(0, history.Import(theirs)); // nothing new the second time
    }

    static FileIndex Tree(out int c, out int docs, out int report)
    {
        var index = new FileIndex();
        c = index.AddRoot("C:");
        docs = index.Add("Docs", c, FileAttributes.Directory, -1, 0, 0);
        report = index.Add("report.docx", docs, FileAttributes.Archive, 1, 0, 0);
        index.Add("report.docx", c, FileAttributes.Archive, 1, 0, 0); // same name elsewhere: not the one that ran
        return index;
    }

    static RunLookup Lookup(FileIndex index)
    {
        index.Lock.EnterReadLock();
        try { return index.RunHistory.Lookup(index); }
        finally { index.Lock.ExitReadLock(); }
    }

    [Fact]
    public void Lookup_matches_paths_to_ids_and_keeps_up_with_both()
    {
        var index = Tree(out int c, out int docs, out int report);
        var history = index.RunHistory;
        history.Set(@"C:\Docs\report.docx", 3, 111);
        history.Set(@"C:\Later\new.txt", 1, 222);
        history.Set("C:", 7, 333);

        var lookup = Lookup(index);
        Assert.Equal(new RunHistory.Entry(3, 111), lookup.Get(report));
        Assert.Equal(7, lookup.Get(c).Count);
        Assert.Equal(0, lookup.Get(docs).Count);

        // Run again: only that id changes, and a presorted order can tell which.
        history.Increment(@"C:\Docs\report.docx", 444);
        var next = Lookup(index);
        Assert.Equal(new RunHistory.Entry(4, 444), next.Get(report));
        var changed = new List<int>();
        Assert.True(next.TryGetChangesSince(lookup.Sequence, changed));
        Assert.Equal([report], changed);

        // A file made after it ran gets its count back.
        int later = index.Add("Later", c, FileAttributes.Directory, -1, 0, 0);
        int fresh = index.Add("new.txt", later, FileAttributes.Archive, 1, 0, 0);
        Assert.Equal(1, Lookup(index).Get(fresh).Count);

        // The folder above it renamed: the path no longer ran (Everything keeps run history by path).
        index.Rename(docs, "Papers", c);
        Assert.Equal(0, Lookup(index).Get(report).Count);
        index.Rename(docs, "Docs", c);
        Assert.Equal(4, Lookup(index).Get(report).Count);

        index.Remove(report);
        Assert.Equal(0, Lookup(index).Get(report).Count);
        history.Clear();
        Assert.Equal(0, Lookup(index).Get(c).Count);
    }

    [Fact]
    public void Search_finds_and_sorts_by_run_count_and_date_run()
    {
        var index = Tree(out _, out _, out int report);
        int other = index.Add("other.txt", index.Parent(report), FileAttributes.Archive, 1, 0, 0);
        var now = new DateTime(2024, 5, 15, 12, 0, 0, DateTimeKind.Local);
        index.RunHistory.Set(@"C:\Docs\report.docx", 5, now.AddHours(-1).ToFileTimeUtc());
        index.RunHistory.Set(@"C:\Docs\other.txt", 2, now.AddDays(-30).ToFileTimeUtc());
        var engine = new SearchEngine { Clock = () => now };

        int[] Find(string q, SortKey sort = SortKey.Name, bool desc = false) =>
            engine.Search(index, q, new SearchOptions(), sort, desc, CancellationToken.None).Ids;

        Assert.Equal([report], Find("runcount:>=3"));
        Assert.Equal([other], Find("runcount:2"));
        Assert.Equal([report, other], Find("runcount:>0", SortKey.RunCount, desc: true));
        Assert.Equal([report], Find("dr:today"));
        Assert.Equal([report], Find("daterun:today"));
        Assert.Equal([other, report], Find("dr:>=2024", SortKey.DateRun));
        Assert.Equal(3, Find("!runcount:>0").Length); // C:, Docs and the other report.docx
    }
}
