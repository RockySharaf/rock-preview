using System.Globalization;
using System.Text;
using Anything.Core;

namespace Anything;

internal enum ExportFormat { Efu = 0, Csv = 1, Txt = 2 }

/// <summary>File > Export: Everything file lists (.efu), CSV of the visible columns, or plain text full paths.</summary>
internal static class ResultExporter
{
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
                    var row = Row.Build(index, id);
                    var cells = columns.Select(c => c.Key switch
                    {
                        // Machine-friendly values for CSV: raw bytes and sortable local timestamps.
                        "size" => row.Size < 0 ? "" : row.Size.ToString(CultureInfo.InvariantCulture),
                        "date_modified" => IsoDate(index.Modified(id)),
                        "date_created" => IsoDate(index.Created(id)),
                        "date_accessed" => IsoDate(AccessedOf(index, id, row)),
                        "date_recently_changed" => IsoDate(row.RecentChange),
                        "run_count" => row.RunCount > 0 ? row.RunCount.ToString(CultureInfo.InvariantCulture) : "",
                        "date_run" => IsoDate(row.LastRun),
                        _ => Quote(row.Text(c)),
                    });
                    w.WriteLine(string.Join(',', cells));
                }
                break;

            default:
                foreach (int id in ids)
                    if (index.IsAlive(id)) w.WriteLine(index.GetFullPath(id));
                break;
        }
    }

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
