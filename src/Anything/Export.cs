using System.Globalization;
using System.Text;
using Anything.Core;

namespace Anything;

internal enum ExportFormat { Efu = 0, Csv = 1, Txt = 2 }

/// <summary>File > Export: Everything file lists (.efu), CSV of the visible columns, or plain text full paths.</summary>
internal static class ResultExporter
{
    /// <summary>
    /// <see cref="Export"/> with the Matching text column of a CSV: each file's line from the text index (for the query the
    /// results came from) is looked up off the UI thread, a thousand rows at a time, so a long export doesn't freeze the
    /// window. Everything read from the name index by id (names, paths, sizes, dates) is taken first, on the caller's
    /// thread as before, and checked against <paramref name="generation"/> (the results' FileIndex.Generation): a Compact
    /// renumbers ids, and later chunks would otherwise describe other files. After that only paths are used.
    /// <paramref name="contentText"/> reads the index: it is only called off the UI thread. Null when the results didn't
    /// look inside files. The CSV then holds document text.
    /// </summary>
    public static async Task ExportAsync(string path, ExportFormat format, FileIndex index, int[] ids, IReadOnlyList<ColumnDef> columns,
        Func<string, string>? contentText, long? generation = null)
    {
        if (format != ExportFormat.Csv || contentText is null || !columns.Any(c => c.Key == "content"))
        {
            Export(path, format, index, ids, columns);
            return;
        }
        long gen = generation ?? index.Generation;
        var rows = new List<(string Path, string?[] Cells)>(ids.Length);
        foreach (int id in ids)
        {
            if (!index.IsAlive(id)) continue;
            var row = Row.Build(index, id);
            rows.Add((row.FullPath, CsvCells(index, id, row, columns)));
        }
        // Checked after the rows were read: a Compact may run on another thread at any time.
        if (index.Generation != gen) throw new InvalidOperationException("The results changed while they were exported. Export them again.");
        const int Chunk = 1000;
        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        w.WriteLine(string.Join(',', columns.Select(c => Quote(c.Title))));
        for (int at = 0; at < rows.Count; at += Chunk)
        {
            var paths = rows.GetRange(at, Math.Min(Chunk, rows.Count - at)).Select(r => r.Path).ToArray();
            var lines = await Task.Run(() => paths.Select(p => p.Length > 0 ? contentText(p) : "").ToArray());
            for (int i = 0; i < lines.Length; i++) w.WriteLine(Join(rows[at + i].Cells, lines[i]));
        }
    }

    /// <summary>Writes the export on the caller's thread; a Matching text column stays empty (<see cref="ExportAsync"/> fills it).</summary>
    public static void Export(string path, ExportFormat format, FileIndex index, int[] ids, IReadOnlyList<ColumnDef> columns)
    {
        using var w = new StreamWriter(path, false, new UTF8Encoding(true));
        switch (format)
        {
            case ExportFormat.Efu:
                // Same layout Everything writes: FILETIME integers for dates, decimal attribute flags.
                w.WriteLine("Filename,Size,Date Modified,Date Created,Attributes");
                foreach (int id in ids)
                {
                    if (!index.IsAlive(id)) continue;
                    bool dir = index.IsDirectory(id);
                    long size = index.Size(id);
                    long dm = index.Modified(id), dc = index.Created(id);
                    w.Write(Quote(index.GetFullPath(id)));
                    w.Write(',');
                    if (!dir && size >= 0) w.Write(size.ToString(CultureInfo.InvariantCulture));
                    w.Write(',');
                    if (dm > 0) w.Write(dm.ToString(CultureInfo.InvariantCulture));
                    w.Write(',');
                    if (dc > 0) w.Write(dc.ToString(CultureInfo.InvariantCulture));
                    w.Write(',');
                    w.WriteLine(((int)index.Attributes(id)).ToString(CultureInfo.InvariantCulture));
                }
                break;

            case ExportFormat.Csv:
                w.WriteLine(string.Join(',', columns.Select(c => Quote(c.Title))));
                foreach (int id in ids)
                {
                    if (!index.IsAlive(id)) continue;
                    w.WriteLine(CsvLine(index, id, Row.Build(index, id), columns, ""));
                }
                break;

            default:
                foreach (int id in ids)
                    if (index.IsAlive(id)) w.WriteLine(index.GetFullPath(id));
                break;
        }
    }

    /// <summary>One CSV row of the visible columns; <paramref name="content"/> is its Matching text.</summary>
    static string CsvLine(FileIndex index, int id, Row row, IReadOnlyList<ColumnDef> columns, string content)
        => Join(CsvCells(index, id, row, columns), content);

    /// <summary>The CSV cells of a row, read from the index by id now; the Matching text cell is null (filled by <see cref="Join"/>).</summary>
    static string?[] CsvCells(FileIndex index, int id, Row row, IReadOnlyList<ColumnDef> columns)
    {
        var cells = new string?[columns.Count];
        for (int i = 0; i < cells.Length; i++)
            cells[i] = columns[i].Key switch
            {
                // Machine-friendly values for CSV: raw bytes and sortable local timestamps.
                "size" => row.Size < 0 ? "" : row.Size.ToString(CultureInfo.InvariantCulture),
                "date_modified" => IsoDate(index.Modified(id)),
                "date_created" => IsoDate(index.Created(id)),
                "date_accessed" => IsoDate(AccessedOf(index, id, row)),
                "date_recently_changed" => IsoDate(row.RecentChange),
                "run_count" => row.RunCount > 0 ? row.RunCount.ToString(CultureInfo.InvariantCulture) : "",
                "date_run" => IsoDate(row.LastRun),
                "content" => null,
                _ => Quote(row.Text(columns[i])),
            };
        return cells;
    }

    /// <summary>A CSV line from its cells, with <paramref name="content"/> as the Matching text.</summary>
    static string Join(string?[] cells, string content) => string.Join(',', cells.Select(c => c ?? Quote(content)));

    /// <summary>Date accessed from the index, or read from the disk now when it isn't indexed (an export waits for it).</summary>
    static long AccessedOf(FileIndex index, int id, Row row)
    {
        if (index.Accessed(id) is not 0 and var indexed) return indexed;
        try { return File.GetLastAccessTimeUtc(row.FullPath).ToFileTimeUtc(); } // 1601 (0) when it is gone
        catch (Exception) { return 0; }
    }

    static string IsoDate(long ft)
    {
        if (ft <= 0) return "";
        try { return DateTime.FromFileTimeUtc(ft).ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture); }
        catch (ArgumentOutOfRangeException) { return ""; }
    }

    static string Quote(string s) => "\"" + s.Replace("\"", "\"\"") + "\"";
}
