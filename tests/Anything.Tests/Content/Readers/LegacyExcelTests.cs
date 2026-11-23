using System.Diagnostics;
using System.Text;
using Anything.Core.Content.Extract.Office;

namespace Anything.Tests.Content.Readers;

/// <summary>
/// The Excel 97-2003 (BIFF8) and Excel 5.0/95 (BIFF5) reader: workbook streams written record by record here, so every
/// field, boundary and limit can be placed exactly where a test wants it.
/// </summary>
public class LegacyExcelTests
{
    // ------------------------------------------------------------------ a small BIFF record writer

    static byte[] Cat(params byte[][] parts) => parts.SelectMany(p => p).ToArray();
    static byte[] U16(int v) => BitConverter.GetBytes((ushort)v);
    static byte[] U32(uint v) => BitConverter.GetBytes(v);
    static byte[] F64(double v) => BitConverter.GetBytes(v);
    static byte[] Latin1(string s) => Encoding.Latin1.GetBytes(s);
    static byte[] Wide(string s) => Encoding.Unicode.GetBytes(s);
    static byte[] Bytes(params int[] b) => b.Select(x => (byte)x).ToArray();

    /// <summary>A record: type, length, data.</summary>
    static byte[] R(int type, params byte[][] data)
    {
        var body = Cat(data);
        return Cat(U16(type), U16(body.Length), body);
    }

    /// <summary>A BIFF8 string with a 2-byte count: compressed when every character fits in a byte.</summary>
    static byte[] Str(string s) => s.All(c => c < 256) ? Cat(U16(s.Length), [0], Latin1(s)) : Cat(U16(s.Length), [1], Wide(s));

    static byte[] Xf(int format) => R(0x00E0, U16(0), U16(format), new byte[16]);
    static byte[] Format(int index, string code) => R(0x041E, U16(index), Str(code));
    static byte[] Sst(params string[] strings) => R(0x00FC, U32((uint)strings.Length), U32((uint)strings.Length), Cat(strings.Select(Str).ToArray()));

    static byte[] Number(int row, int col, double v, int xf = 0) => R(0x0203, U16(row), U16(col), U16(xf), F64(v));
    static byte[] Rk(int row, int col, uint rk, int xf = 0) => R(0x027E, U16(row), U16(col), U16(xf), U32(rk));
    static byte[] Label(int row, int col, string s) => R(0x0204, U16(row), U16(col), U16(0), Str(s));
    static byte[] RString(int row, int col, string s) => R(0x00D6, U16(row), U16(col), U16(0), Str(s), U16(1), U16(0), U16(0));
    static byte[] LabelSst(int row, int col, uint index) => R(0x00FD, U16(row), U16(col), U16(0), U32(index));
    static byte[] BoolErr(int row, int col, int value, bool error) => R(0x0205, U16(row), U16(col), U16(0), [(byte)value, (byte)(error ? 1 : 0)]);
    static byte[] Blank(int row, int col) => R(0x0201, U16(row), U16(col), U16(0));
    static byte[] Formula(int row, int col, byte[] result, int xf = 0) => R(0x0006, U16(row), U16(col), U16(xf), result, U16(0), U32(0), U16(0));
    static byte[] Special(int kind, int value) => Bytes(kind, 0, value, 0, 0, 0, 0xFF, 0xFF);
    static byte[] StringRec(string s) => R(0x0207, Str(s));
    static byte[] MulRk(int row, int first, params uint[] rks)
        => R(0x00BD, U16(row), U16(first), Cat(rks.Select(rk => Cat(U16(0), U32(rk))).ToArray()), U16(first + rks.Length - 1));
    static byte[] Continue(params byte[][] data) => R(0x003C, data);

    static uint RkInt(int v, bool div100 = false) => (uint)(v << 2) | 2u | (div100 ? 1u : 0u);
    static uint RkDouble(double v, bool div100 = false) => (uint)(BitConverter.DoubleToInt64Bits(v) >>> 32) & 0xFFFFFFFC | (div100 ? 1u : 0u);

    sealed record Sheet(string Name, byte[][] Records, byte Kind = 0, byte Hidden = 0, int BofKind = 0x0010);

    static byte[] Bof(int kind, bool biff5) => biff5 ? R(0x0809, U16(0x0500), U16(kind), U16(0), U16(0)) : R(0x0809, U16(0x0600), U16(kind), new byte[12]);
    static readonly byte[] Eof = R(0x000A);

    /// <summary>A workbook stream: globals (BOF, the given records, a BOUNDSHEET per sheet, EOF) then each sheet's substream.</summary>
    static byte[] Book(byte[][] globals, Sheet[] sheets, bool biff5 = false, uint?[]? offsets = null)
        => Book(globals, sheets, out _, biff5, offsets);

    static byte[] Book(byte[][] globals, Sheet[] sheets, out int firstBoundSheet, bool biff5 = false, uint?[]? offsets = null)
    {
        byte[] BoundSheet(Sheet s, uint offset)
        {
            if (biff5) return R(0x0085, U32(offset), [s.Hidden, s.Kind, (byte)s.Name.Length], Encoding.GetEncoding(1252).GetBytes(s.Name));
            bool wide = s.Name.Any(c => c >= 256);
            return R(0x0085, U32(offset), [s.Hidden, s.Kind, (byte)s.Name.Length, (byte)(wide ? 1 : 0)], wide ? Wide(s.Name) : Latin1(s.Name));
        }
        var head = Cat([Bof(0x0005, biff5), .. globals]);
        firstBoundSheet = head.Length;
        int boundSheets = sheets.Sum(s => BoundSheet(s, 0).Length);
        var bodies = sheets.Select(s => Cat([Bof(s.BofKind, biff5), .. s.Records, Eof])).ToArray();
        uint at = (uint)(head.Length + boundSheets + Eof.Length);
        var list = new List<byte[]> { head };
        for (int i = 0; i < sheets.Length; i++)
        {
            list.Add(BoundSheet(sheets[i], offsets?[i] ?? at));
            at += (uint)bodies[i].Length;
        }
        list.Add(Eof);
        list.AddRange(bodies);
        return Cat(list.ToArray());
    }

    /// <summary>
    /// A small Excel 97-2003 file written by OLE, for other tests: each sheet by name, hidden or not, with text cells
    /// (row, column, text) and number cells.
    /// </summary>
    internal static byte[] XlsFile(params (string Name, bool Hidden, (int Row, int Col, string Text)[] Labels, (int Row, int Col, double Value)[] Numbers)[] sheets)
    {
        var book = XlsStream(sheets);
        return CfbFixtures.Ole(root => CfbFixtures.Stream(root, "Workbook", book));
    }

    /// <summary>The Workbook stream of <see cref="XlsFile"/>, for tests that parse it directly.</summary>
    internal static byte[] XlsStream(params (string Name, bool Hidden, (int Row, int Col, string Text)[] Labels, (int Row, int Col, double Value)[] Numbers)[] sheets)
        => Book([Xf(0)], sheets.Select(s => new Sheet(s.Name,
            [.. s.Labels.Select(l => Label(l.Row, l.Col, l.Text)), .. s.Numbers.Select(n => Number(n.Row, n.Col, n.Value))],
            Hidden: (byte)(s.Hidden ? 1 : 0))).ToArray());

    /// <summary>A one-sheet Excel 97-2003 file written by OLE whose globals hold a FILEPASS record (an encrypted workbook), for other tests.</summary>
    internal static byte[] EncryptedXlsFile()
        => CfbFixtures.Ole(root => CfbFixtures.Stream(root, "Workbook",
            Book([R(0x002F, U16(1), new byte[52]), Xf(0)], [new Sheet("S", [Label(0, 0, "secret cell")])])));

    static byte[] OneSheet(byte[][] globals, params byte[][] cells) => Book(globals, [new Sheet("S", cells)]);
    static byte[] OneSheet(params byte[][] cells) => OneSheet([Xf(0)], cells);

    static IReadOnlyList<LegacyExcelSheet>? Parse(byte[] stream, int maxRows = 1000, int maxColumns = 100)
        => LegacyExcel.Parse(stream, maxRows, maxColumns, CancellationToken.None);

    static string?[][] Cells(byte[] stream, int maxRows = 1000, int maxColumns = 100)
    {
        var sheets = Parse(stream, maxRows, maxColumns);
        Assert.NotNull(sheets);
        return Assert.Single(sheets).Rows.ToArray();
    }

    static string? Cell(byte[] stream, int row, int col)
    {
        var rows = Cells(stream);
        return row < rows.Length && col < rows[row].Length ? rows[row][col] : null;
    }

    static void Eq(IEnumerable<string?> expected, IEnumerable<string?> actual) => Assert.Equal(expected, actual);
    static void EqRows(IEnumerable<IEnumerable<string?>> expected, IEnumerable<IEnumerable<string?>> actual) => Assert.Equal(expected, actual);

    static LegacyExcelTests() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

    // ------------------------------------------------------------------ cells

    [Fact]
    public void Every_cell_record_type_gives_its_text()
    {
        var rows = Cells(OneSheet([Xf(0), Sst("shared one", "shared two")],
            LabelSst(0, 0, 1),
            Label(0, 1, "label"),
            RString(0, 2, "rich"),
            Number(0, 3, 1234.5),
            Rk(0, 4, RkInt(42)),
            MulRk(1, 0, RkInt(1), RkInt(2), RkInt(3)),
            BoolErr(2, 0, 1, false),
            BoolErr(2, 1, 0, false),
            BoolErr(2, 2, 0x07, true),
            Blank(2, 3),
            Formula(3, 0, F64(7.25)),
            Formula(3, 1, Special(0, 0)), StringRec("formula text"),
            Formula(3, 2, Special(1, 1)),
            Formula(3, 3, Special(2, 0x2A)),
            Formula(3, 4, Special(3, 0)),
            Formula(3, 5, Special(1, 0))));
        Eq(["shared two", "label", "rich", "1234.5", "42"], rows[0]);
        Eq(["1", "2", "3"], rows[1]);
        Eq(["TRUE", "FALSE", "#DIV/0!"], rows[2]);
        Eq(["7.25", "formula text", "TRUE", "#N/A", null, "FALSE"], rows[3]);
    }

    [Fact]
    public void A_formula_string_waits_past_shared_formula_records_but_not_past_another_cell()
    {
        var rows = Cells(OneSheet(
            Formula(0, 0, Special(0, 0)), R(0x04BC, new byte[10]), StringRec("after shrfmla"),
            Formula(1, 0, Special(0, 0)), Number(1, 1, 5), StringRec("orphan")));
        Eq(["after shrfmla"], rows[0]);
        Eq([null, "5"], rows[1]);
    }

    [Fact]
    public void A_formula_string_split_by_a_continue_changes_width()
    {
        // "abc" compressed in the STRING record, then a flags byte saying two bytes per character for "日本".
        var stream = OneSheet(
            Formula(0, 0, Special(0, 0)),
            R(0x0207, U16(5), [0], Latin1("abc")),
            Continue([1], Wide("日本")));
        Assert.Equal("abc日本", Cell(stream, 0, 0));
    }

    [Theory]
    [InlineData(0u, 0.0)]
    [InlineData(1u, 0.0)]
    public void Packed_number_of_zero(uint rk, double expected) => Assert.Equal(expected, LegacyExcel.Rk(rk));

    [Fact]
    public void Packed_numbers_in_their_four_forms()
    {
        Assert.Equal(123, LegacyExcel.Rk(RkInt(123)));
        Assert.Equal(-5, LegacyExcel.Rk(RkInt(-5)));
        Assert.Equal(123.45, LegacyExcel.Rk(RkInt(12345, div100: true)));
        Assert.Equal(1.5, LegacyExcel.Rk(RkDouble(1.5)));
        Assert.Equal(0.015, LegacyExcel.Rk(RkDouble(1.5, div100: true)));
        Assert.Equal(-1024, LegacyExcel.Rk(RkDouble(-1024)));

        var rows = Cells(OneSheet(Rk(0, 0, RkInt(12345, div100: true)), Rk(0, 1, RkDouble(1.5, div100: true)), Rk(0, 2, RkInt(-7))));
        Eq(["123.45", "0.015", "-7"], rows[0]);
    }

    [Theory]
    [InlineData(1234.5, "1234.5")]
    [InlineData(0.1, "0.1")]
    [InlineData(100, "100")]
    [InlineData(1e20, "1E+20")]
    [InlineData(-0.0, "0")]
    [InlineData(-2.5, "-2.5")]
    [InlineData(double.NaN, "#NUM!")]
    [InlineData(double.PositiveInfinity, "#NUM!")]
    [InlineData(123456789012345.0, "123456789012345")]
    [InlineData(1234567890123456789.0, "1.23456789012346E+18")]
    [InlineData(double.MaxValue, "1.7976931348623157E+308")]
    [InlineData(double.Epsilon, "5E-324")]
    public void Numbers_read_back_the_same(double value, string expected) => Assert.Equal(expected, LegacyExcel.NumberText(value));

    [Fact]
    public void Numbers_show_at_most_15_significant_digits_like_Excel()
    {
        Assert.Equal("0.30000000000000004", (0.1 + 0.2).ToString(System.Globalization.CultureInfo.InvariantCulture)); // the noise itself
        Assert.Equal("0.3", LegacyExcel.NumberText(0.1 + 0.2));
        Assert.Equal("3.3", LegacyExcel.NumberText(1.1 * 3));
        Assert.Equal("-0.3", LegacyExcel.NumberText(-(0.1 + 0.2)));
        Assert.Equal("1E-16", LegacyExcel.NumberText(1e-16));

        var rows = Cells(OneSheet(Number(0, 0, 0.1 + 0.2), Formula(0, 1, F64(1.1 * 3))));
        Eq(["0.3", "3.3"], rows[0]);
    }

    [Fact]
    public void Error_codes_as_Excel_shows_them()
    {
        var codes = new[] { 0x00, 0x07, 0x0F, 0x17, 0x1D, 0x24, 0x2A };
        Eq(["#NULL!", "#DIV/0!", "#VALUE!", "#REF!", "#NAME?", "#NUM!", "#N/A"], codes.Select(LegacyExcel.Error));
    }

    // ------------------------------------------------------------------ dates

    [Fact]
    public void Dates_in_the_1900_system()
    {
        var rows = Cells(OneSheet([Xf(0), Xf(14), Xf(22), Xf(47)],
            Number(0, 0, 45366, xf: 1),
            Number(0, 1, 45366.75, xf: 2),
            Number(0, 2, 0.5, xf: 3),
            Number(0, 3, 45366, xf: 0),
            Rk(0, 4, RkInt(60), xf: 1),
            Rk(0, 5, RkInt(59), xf: 1),
            Rk(0, 6, RkInt(61), xf: 1),
            Rk(0, 7, RkInt(1), xf: 1),
            Number(0, 8, -1, xf: 1),
            Number(0, 9, 1e9, xf: 1)));
        Eq(["2024-03-15", "2024-03-15 18:00:00", "12:00:00", "45366", "1900-02-29", "1900-02-28", "1900-03-01", "1900-01-01", "-1", "1000000000"], rows[0]);
    }

    [Fact]
    public void Dates_in_the_1904_system()
    {
        var rows = Cells(OneSheet([R(0x0022, U16(1)), Xf(0), Xf(14)],
            Number(0, 0, 45366 - 1462, xf: 1),
            Number(0, 1, 0, xf: 1),
            Number(0, 2, 1, xf: 1)));
        Eq(["2024-03-15", "1904-01-01", "1904-01-02"], rows[0]);
    }

    [Theory]
    // Built-in formats: 14 date, 18 and 21 times, 22 both, 46 elapsed, 47 a time.
    [InlineData(14, 45366.75, false, "2024-03-15")]          // a date format leaves the time out
    [InlineData(14, 0, false, "1900-01-00")]                 // day zero as Excel shows it, not a time
    [InlineData(14, 0.5, false, "1900-01-00")]
    [InlineData(14, 0, true, "1904-01-01")]
    [InlineData(18, 45366.75, false, "18:00:00")]            // a time format leaves the date out
    [InlineData(21, 0.5, false, "12:00:00")]
    [InlineData(21, 0, false, "00:00:00")]
    [InlineData(22, 45366.75, false, "2024-03-15 18:00:00")]
    [InlineData(22, 45366, false, "2024-03-15 00:00:00")]    // both, whatever the value
    [InlineData(22, 0.5, false, "1900-01-00 12:00:00")]
    [InlineData(46, 1.5, false, "36:00:00")]                 // elapsed: the total, never a calendar date
    [InlineData(46, 0.25, false, "06:00:00")]
    [InlineData(46, 45366.5, false, "1088796:00:00")]
    [InlineData(47, 45366.75, false, "18:00:00")]
    [InlineData(31, 45366, false, "2024-03-15")]
    [InlineData(55, 45366, false, "2024-03-15")]
    [InlineData(0, 45366.75, false, "45366.75")]
    [InlineData(14, -1, false, "-1")]                        // outside the calendar: the number
    [InlineData(46, -1.5, false, "-1.5")]
    [InlineData(14, 1e9, false, "1000000000")]
    public void A_formatted_number_shows_what_its_format_says(int format, double value, bool date1904, string expected)
    {
        byte[][] globals = date1904 ? [R(0x0022, U16(1)), Xf(0), Xf(format)] : [Xf(0), Xf(format)];
        Assert.Equal(expected, Cell(OneSheet(globals, Number(0, 0, value, xf: 1)), 0, 0));
    }

    [Theory]
    [InlineData("yyyy-mm-dd", 45366.75, "2024-03-15")]
    [InlineData("d mmm", 45366.75, "2024-03-15")]
    [InlineData("mm", 45366.75, "2024-03-15")]                       // m alone is the month
    [InlineData("h:mm", 45366.75, "18:00:00")]                         // m right after h is minutes
    [InlineData("mm:ss", 45366.75, "18:00:00")]                        // m right before s is minutes
    [InlineData("h:mm AM/PM", 45366.75, "18:00:00")]                   // the m of AM/PM is neither
    [InlineData("hh \"o'clock\" A/P", 0.25, "06:00:00")]
    [InlineData("dd/mm/yyyy hh:mm", 45366.75, "2024-03-15 18:00:00")]
    [InlineData("[h]:mm:ss", 1.5, "36:00:00")]
    [InlineData("[mm]:ss", 1.5, "36:00:00")]
    [InlineData("[ss]", 1.5, "36:00:00")]
    [InlineData("[Red][h]:mm", 1.5, "36:00:00")]
    [InlineData("[$-409]h:mm:ss", 45366.75, "18:00:00")]
    [InlineData("0.00 \"days\"", 1.5, "1.5")]
    public void A_custom_format_decides_date_time_both_or_elapsed(string code, double value, string expected)
        => Assert.Equal(expected, Cell(OneSheet([Xf(0), Format(164, code), Xf(164)], Number(0, 0, value, xf: 1)), 0, 0));

    [Fact]
    public void Custom_formats_are_dates_only_with_date_letters_outside_quotes_and_brackets()
    {
        var rows = Cells(OneSheet([Xf(0), Format(164, "yyyy/mm/dd"), Format(165, "0.00 \"days\""), Format(166, "[Red]0.00"),
                Format(167, "[h]:mm"), Format(168, "\\d0"), Format(169, "[$-409]D-MMM-YY"), Format(14, "0.0"), Xf(164), Xf(165), Xf(166), Xf(167), Xf(168), Xf(169), Xf(14)],
            Number(0, 0, 45366, xf: 1),
            Number(0, 1, 45366, xf: 2),
            Number(0, 2, 45366, xf: 3),
            Number(0, 3, 45366.5, xf: 4),
            Number(0, 4, 45366, xf: 5),
            Number(0, 5, 45366, xf: 6),
            Number(0, 6, 45366, xf: 7)));
        Eq(["2024-03-15", "45366", "45366", "1088796:00:00", "45366", "2024-03-15", "45366"], rows[0]);
    }

    [Theory]
    [InlineData("d/m/yyyy", "Date")]
    [InlineData("mmm-yy", "Date")]
    [InlineData("MMMM", "Date")]
    [InlineData("h:mm AM/PM", "Time")]
    [InlineData("HH:MM:SS", "Time")]
    [InlineData("mm:ss.0", "Time")]
    [InlineData("AM/PM", "Time")]
    [InlineData("m/d/yy h:mm", "DateTime")]
    [InlineData("yyyy-mm-dd hh:mm:ss", "DateTime")]
    [InlineData("d \"at\" h", "DateTime")]
    [InlineData("[h]:mm:ss", "Elapsed")]
    [InlineData("[HH]", "Elapsed")]
    [InlineData("[mm]:ss", "Elapsed")]
    [InlineData("[s]", "Elapsed")]
    [InlineData("[Red]0.00", "None")]
    [InlineData("[hm]0", "None")]
    [InlineData("General", "None")]
    [InlineData("0.00E+00", "None")]
    [InlineData("#,##0 \"months\"", "None")]
    [InlineData("_(* #,##0_)", "None")]
    [InlineData("0\\s", "None")]
    [InlineData("\"unclosed", "None")]
    [InlineData("d[unclosed", "None")]
    [InlineData("", "None")]
    public void Custom_format_kinds(string format, string kind) => Assert.Equal(Enum.Parse<LegacyExcel.DateKind>(kind), LegacyExcel.FormatKind(format));

    [Fact]
    public void Built_in_format_kinds()
    {
        int[] dates = [14, 15, 16, 17, 27, 28, 30, 33, 36, 50, 52, 55, 58];
        int[] times = [18, 19, 20, 21, 45, 47];
        int[] others = [0, 1, 2, 9, 10, 11, 12, 13, 23, 26, 37, 44, 48, 49, 59, 164];
        Assert.All(dates, f => Assert.Equal(LegacyExcel.DateKind.Date, LegacyExcel.BuiltInKind(f)));
        Assert.All(times, f => Assert.Equal(LegacyExcel.DateKind.Time, LegacyExcel.BuiltInKind(f)));
        Assert.Equal(LegacyExcel.DateKind.DateTime, LegacyExcel.BuiltInKind(22));
        Assert.Equal(LegacyExcel.DateKind.Elapsed, LegacyExcel.BuiltInKind(46));
        Assert.All(others, f => Assert.Equal(LegacyExcel.DateKind.None, LegacyExcel.BuiltInKind(f)));
    }

    // ------------------------------------------------------------------ shared strings

    static byte[] SstOf(params byte[][] records) => OneSheet([Xf(0), .. records], LabelSst(0, 0, 0), LabelSst(0, 1, 1), LabelSst(0, 2, 2));

    [Fact]
    public void Shared_strings_compressed_two_byte_with_runs_and_with_extended_data()
    {
        var sst = R(0x00FC, U32(4), U32(3),
            Cat(U16(5), [0x00], Latin1("plain")),
            Cat(U16(2), [0x09], U16(2), Wide("日本"), U32(0x00010000), U32(0x00020001)),
            Cat(U16(3), [0x04], U32(6), Latin1("ext"), new byte[6]));
        var rows = Cells(SstOf(sst));
        Eq(["plain", "日本", "ext"], rows[0]);
    }

    [Fact]
    public void Shared_string_cut_inside_its_characters_goes_on_with_a_new_width()
    {
        // "abc" compressed, then the CONTINUE starts with a flags byte saying two bytes per character for "déf€".
        var sst = R(0x00FC, U32(3), U32(3), Cat(U16(7), [0x00], Latin1("abc")));
        var more = Continue([0x01], Wide("déf€"), Str("next"), Str("last"));
        Eq(["abcdéf€", "next", "last"], Cells(SstOf(sst, more))[0]);

        // And back: two-byte characters first, compressed after the cut.
        var sst2 = R(0x00FC, U32(3), U32(3), Cat(U16(4), [0x01], Wide("日本")));
        var more2 = Continue([0x00], Latin1("ab"), Str("x"), Str("y"));
        Eq(["日本ab", "x", "y"], Cells(SstOf(sst2, more2))[0]);
    }

    [Fact]
    public void Shared_string_whose_characters_begin_at_a_boundary_has_a_flags_byte_there()
    {
        var sst = R(0x00FC, U32(3), U32(3), Str("one"), Cat(U16(3), [0x00]));
        var more = Continue([0x01], Wide("two"), Str("three"));
        Eq(["one", "two", "three"], Cells(SstOf(sst, more))[0]);
    }

    [Fact]
    public void Shared_string_cut_inside_its_header_goes_on_without_a_flags_byte()
    {
        // The count's two bytes are split, then the flags and the run count are split from the characters.
        var sst = R(0x00FC, U32(3), U32(3), Str("one"), [3]);
        var more = Continue([0, 0x08, 1]);
        var more2 = Continue([0], Latin1("two"), U32(0), Str("three"));
        Eq(["one", "two", "three"], Cells(SstOf(sst, more, more2))[0]);
    }

    [Fact]
    public void Shared_string_cut_inside_its_runs_or_extended_data_goes_on_without_a_flags_byte()
    {
        var sst = R(0x00FC, U32(3), U32(3), Cat(U16(3), [0x08], U16(2), Latin1("one"), U32(0), U16(0)));
        var more = Continue(U16(0), Cat(U16(3), [0x04], U32(5), Latin1("two"), new byte[2]));
        var more2 = Continue(new byte[3], Str("three"));
        Eq(["one", "two", "three"], Cells(SstOf(sst, more, more2))[0]);
    }

    [Fact]
    public void Empty_continue_records_are_passed_over()
    {
        var sst = R(0x00FC, U32(3), U32(3), Str("one"), Cat(U16(3), [0x00], Latin1("t")));
        Eq(["one", "two", "three"], Cells(SstOf(sst, Continue(), Continue([0x00], Latin1("wo")), Continue(), Continue(Str("three"))))[0]);
    }

    [Fact]
    public void Shared_string_index_past_the_table_is_blank()
    {
        var rows = Cells(OneSheet([Xf(0), Sst("only")], LabelSst(0, 0, 0), LabelSst(0, 1, 5), LabelSst(0, 2, uint.MaxValue), Label(0, 3, "x")));
        Eq(["only", null, null, "x"], rows[0]);
    }

    [Fact]
    public void A_long_cell_is_cut_at_32000_characters()
    {
        string big = new('a', 20_000);
        var sst = R(0x00FC, U32(1), U32(1), Cat(U16(40_000), [0x00], Latin1(big)));
        var stream = OneSheet([Xf(0), sst, Continue([0x00], Latin1(big))], LabelSst(0, 0, 0), Label(0, 1, "after"));
        var rows = Cells(stream);
        Assert.Equal(LegacyExcel.MaxCellChars, rows[0][0]!.Length);
        Assert.Equal("after", rows[0][1]);
    }

    // ------------------------------------------------------------------ BIFF5

    [Fact]
    public void Biff5_sheet_names_and_labels_in_the_code_page()
    {
        static byte[] Label5(int row, int col, byte[] text) => R(0x0204, U16(row), U16(col), U16(0), U16(text.Length), text);
        var stream = Book([R(0x0042, U16(1252)), Xf(0)],
            [new Sheet("Café €", [Label5(0, 0, Bytes(0x80, 0x20, 0xE9)), Number(0, 1, 2.5), R(0x0006, U16(1), U16(0), U16(0), Special(0, 0), U16(0), U32(0), U16(0)),
                R(0x0207, U16(3), Latin1("abc"))]),
             new Sheet("Second", [Label5(0, 0, Latin1("x"))])], biff5: true);
        var sheets = Parse(stream);
        Assert.NotNull(sheets);
        Eq(["Café €", "Second"], sheets.Select(s => s.Name));
        Eq(["€ é", "2.5"], sheets[0].Rows[0]);
        Eq(["abc"], sheets[0].Rows[1]);
    }

    [Fact]
    public void Biff5_without_a_windows_code_page_reads_latin1()
    {
        var stream = Book([R(0x0042, U16(10000))], [new Sheet("S", [R(0x0204, U16(0), U16(0), U16(0), U16(2), Bytes(0x80, 0xE9))])], biff5: true);
        Assert.Equal("\u0080é", Assert.Single(Parse(stream)!).Rows[0][0]);
    }

    // ------------------------------------------------------------------ sheets

    [Fact]
    public void Several_sheets_in_order_with_a_chart_sheet_and_an_embedded_chart_skipped()
    {
        var embeddedChart = Cat(Bof(0x0020, false), Number(9, 9, 99), Eof);
        var stream = Book([Xf(0)],
        [
            new Sheet("First", [Label(0, 0, "a"), embeddedChart, Label(1, 0, "b")]),
            new Sheet("Chart", [Number(0, 0, 1)], Kind: 2, BofKind: 0x0020),
            new Sheet("Hidden", [Label(0, 0, "h")], Hidden: 1),
            new Sheet("Πρώτο", [Label(0, 0, "c")]),
            new Sheet("Macro", [Label(0, 0, "m")], Kind: 1, BofKind: 0x0040),
        ]);
        var sheets = Parse(stream);
        Assert.NotNull(sheets);
        Eq(["First", "Hidden", "Πρώτο"], sheets.Select(s => s.Name));
        EqRows([["a"], ["b"]], sheets[0].Rows);
        Assert.Equal("h", sheets[1].Rows[0][0]);
        Assert.Equal("c", sheets[2].Rows[0][0]);
        Assert.Equal(new[] { false, true, false }, sheets.Select(s => s.Hidden));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Visible_hidden_and_very_hidden_sheets_are_read_and_marked(bool biff5)
    {
        static byte[] Label5(string s) => R(0x0204, U16(0), U16(0), U16(0), U16(s.Length), Latin1(s));
        byte[] Cell(string s) => biff5 ? Label5(s) : Label(0, 0, s);
        var stream = Book([Xf(0)],
        [
            new Sheet("Visible", [Cell("v")]),
            new Sheet("Hidden", [Cell("h")], Hidden: 1),
            new Sheet("VeryHidden", [Cell("vh")], Hidden: 2),
            new Sheet("HighBitsOnly", [Cell("x")], Hidden: 0xFC), // only the low two bits say
        ], biff5);
        var sheets = Parse(stream);
        Assert.NotNull(sheets);
        Eq(["Visible", "Hidden", "VeryHidden", "HighBitsOnly"], sheets.Select(s => s.Name));
        Assert.Equal(new[] { false, true, true, false }, sheets.Select(s => s.Hidden));
        Eq(["v", "h", "vh", "x"], sheets.Select(s => s.Rows[0][0]));
    }

    [Fact]
    public void A_worksheet_entry_that_points_at_a_dialog_or_other_substream_is_skipped()
    {
        var stream = Book([Xf(0)], [new Sheet("Dialog", [Label(0, 0, "d")], BofKind: 0x0020), new Sheet("Real", [Label(0, 0, "r")])]);
        Assert.Equal("Real", Assert.Single(Parse(stream)!).Name);
    }

    [Fact]
    public void Empty_workbook_and_empty_sheet()
    {
        Assert.Empty(Parse(Book([Xf(0)], []))!);
        var sheet = Assert.Single(Parse(Book([Xf(0)], [new Sheet("Empty", [])]))!);
        Assert.Equal("Empty", sheet.Name);
        Assert.Empty(sheet.Rows);
        Assert.False(sheet.Truncated);
    }

    [Fact]
    public void Trailing_empty_cells_and_rows_are_cut_and_gaps_are_null()
    {
        var rows = Cells(OneSheet(Label(0, 0, "a"), Blank(0, 5), Label(2, 3, "b"), Label(3, 1, ""), Blank(4, 0), Formula(5, 0, Special(3, 0))));
        Assert.Equal(3, rows.Length);
        Eq(["a"], rows[0]);
        Assert.Empty(rows[1]);
        Eq([null, null, null, "b"], rows[2]);
    }

    [Fact]
    public void Rows_and_columns_past_the_limits_are_dropped_and_marked()
    {
        var stream = OneSheet(Label(0, 0, "a"), Label(0, 1, "b"), Label(0, 2, "c"), Label(1, 0, "d"), Label(2, 0, "e"), MulRk(1, 1, RkInt(1), RkInt(2)));
        var sheet = Assert.Single(Parse(stream, maxRows: 2, maxColumns: 2)!);
        Assert.True(sheet.Truncated);
        EqRows([["a", "b"], ["d", "1"]], sheet.Rows);

        Assert.False(Assert.Single(Parse(stream, maxRows: 3, maxColumns: 3)!).Truncated);
        var none = Assert.Single(Parse(stream, maxRows: 0, maxColumns: 0)!);
        Assert.Empty(none.Rows);
        Assert.True(none.Truncated);
        // Empty cells past the limits leave nothing out.
        Assert.False(Assert.Single(Parse(OneSheet(Label(0, 0, "a"), Blank(9, 9), Label(5, 5, "")), maxRows: 1, maxColumns: 1)!).Truncated);
    }

    // ------------------------------------------------------------------ not a workbook, encrypted

    [Fact]
    public void Encrypted_workbook_gives_null()
    {
        var filePass = R(0x002F, U16(1), new byte[52]);
        Assert.Null(Parse(Book([filePass, Xf(0)], [new Sheet("S", [Label(0, 0, "secret")])])));
        Assert.Null(Parse(Book([Xf(0), Sst("x"), filePass], [new Sheet("S", [Label(0, 0, "secret")])])));
    }

    [Fact]
    public void Not_a_workbook_stream_gives_null()
    {
        Assert.Null(Parse([]));
        Assert.Null(Parse(Bytes(0x09, 0x08)));
        Assert.Null(Parse(Label(0, 0, "x")));
        Assert.Null(Parse(R(0x0809, U16(0x0600), U16(0x0010), new byte[12]))); // a worksheet BOF, not the globals
        Assert.Null(Parse(R(0x0809, U16(0x0400), U16(0x0005), new byte[12]))); // BIFF4
        Assert.Null(Parse(R(0x0809, U16(0x0600))));                            // BOF too short
    }

    // ------------------------------------------------------------------ hostile input

    static void Fast(Action action)
    {
        var watch = Stopwatch.StartNew();
        action();
        Assert.True(watch.ElapsedMilliseconds < 5000, $"took {watch.ElapsedMilliseconds} ms");
    }

    [Fact]
    public void Boundsheets_that_point_nowhere_or_at_themselves_are_skipped()
    {
        var sheets = new[] { new Sheet("Nowhere", [Label(0, 0, "a")]), new Sheet("Past", [Label(0, 0, "b")]), new Sheet("Good", [Label(0, 0, "c")]) };
        var plain = Book([Xf(0)], sheets, out int firstBoundSheet);
        var stream = Book([Xf(0)], sheets, offsets: [(uint)firstBoundSheet, uint.MaxValue, null]);
        Assert.Equal(plain.Length, stream.Length);
        Assert.Equal("Good", Assert.Single(Parse(stream)!).Name);
        // Pointing at the globals' own BOF.
        var again = Book([Xf(0)], [new Sheet("Globals", []), new Sheet("A", [Label(0, 0, "a")]), new Sheet("B", [])],
            offsets: [0u, null, null]);
        Eq(["A", "B"], Parse(again)!.Select(s => s.Name));
        // Two entries for the same sheet: read once.
        var twice = Book([Xf(0)], [new Sheet("A", [Label(0, 0, "a")]), new Sheet("Copy", [])]);
        int first = BitConverter.ToInt32(twice, firstBoundSheet + 4);
        twice = Book([Xf(0)], [new Sheet("A", [Label(0, 0, "a")]), new Sheet("Copy", [])], offsets: [null, (uint)first]);
        Assert.Equal("A", Assert.Single(Parse(twice)!).Name);
    }

    [Fact]
    public void A_shared_string_count_of_four_billion_ends_with_the_data()
    {
        var sst = R(0x00FC, U32(uint.MaxValue), U32(uint.MaxValue), Str("one"), Str("two"));
        Fast(() => Eq(["one", "two"], Cells(SstOf(sst))[0]));
        // Huge run counts and extended sizes run off the end without looping.
        var huge = R(0x00FC, U32(2), U32(2), Str("one"), Cat(U16(3), [0x0C], U16(0xFFFF), U32(uint.MaxValue), Latin1("two")));
        Fast(() => Eq(["one", "two"], Cells(SstOf(huge, Continue(new byte[100])))[0]));
    }

    [Fact]
    public void Continue_with_nothing_before_it_and_stray_records_are_ignored()
    {
        var stream = OneSheet([Continue(Str("stray")), Xf(0), Continue(new byte[8]), Sst("s")],
            Continue(new byte[4]), LabelSst(0, 0, 0), R(0x0207, Str("no formula")), Continue(Bytes(1, 2, 3)));
        Eq(["s"], Cells(stream)[0]);
    }

    [Fact]
    public void A_record_length_past_the_end_keeps_what_came_before()
    {
        var stream = OneSheet(Label(0, 0, "kept"), Number(0, 1, 3));
        // Cut the sheet's last records off and claim a long record in their place.
        var cut = Cat(stream[..^(Eof.Length + 18)], U16(0x0203), U16(0xFFFF), new byte[4]);
        Eq(["kept"], Cells(cut)[0]);
    }

    [Fact]
    public void Short_cell_records_are_skipped()
    {
        var stream = OneSheet(R(0x0203, U16(0), U16(0)), R(0x00FD, U16(0), U16(1)), R(0x027E, new byte[9]), R(0x00BD, new byte[11]), R(0x0006, new byte[13]),
            R(0x0205, new byte[7]), R(0x0204, U16(0), U16(2), U16(0)), R(0x0204, U16(0), U16(3), U16(0), U16(50), [0], Latin1("cut")), Label(0, 4, "ok"));
        Eq([null, null, null, "cut", "ok"], Cells(stream)[0]);
    }

    static byte[] Everything() => Book(
        [R(0x0022, U16(0)), Xf(0), Xf(14), Format(164, "yyyy"), Xf(164), R(0x00FC, U32(3), U32(3), Str("one"), Cat(U16(4), [0x08], U16(1), Latin1("tw"))),
            Continue([0x01], Wide("oo"), U32(0), Cat(U16(5), [0x04], U32(2), Latin1("three"), new byte[2]))],
        [
            new Sheet("A", [LabelSst(0, 0, 0), LabelSst(0, 1, 1), LabelSst(0, 2, 2), Number(1, 0, 45366, xf: 1), Rk(1, 1, RkInt(3)),
                MulRk(2, 0, RkInt(1), RkDouble(2.5)), Formula(3, 0, Special(0, 0)), R(0x0207, U16(4), [0], Latin1("ab")), Continue([1], Wide("cd")),
                BoolErr(4, 0, 1, false), Label(5, 0, "label")]),
            new Sheet("Chart", [Number(0, 0, 1)], Kind: 2, BofKind: 0x0020),
            new Sheet("B", [Label(0, 0, "b"), Cat(Bof(0x0020, false), Eof), Number(1, 1, 2, xf: 2)]),
        ]);

    [Fact]
    public void The_full_fixture_reads_as_expected()
    {
        var sheets = Parse(Everything());
        Assert.NotNull(sheets);
        Eq(["A", "B"], sheets.Select(s => s.Name));
        Eq(["one", "twoo", "three"], sheets[0].Rows[0]);
        Eq(["2024-03-15", "3"], sheets[0].Rows[1]);
        Eq(["1", "2.5"], sheets[0].Rows[2]);
        Eq(["abcd"], sheets[0].Rows[3]);
        Eq(["TRUE"], sheets[0].Rows[4]);
        Eq(["label"], sheets[0].Rows[5]);
        Eq([null, "1900-01-02"], sheets[1].Rows[1]);
    }

    [Fact]
    public void Truncated_at_every_byte_never_throws()
    {
        var full = Everything();
        Fast(() =>
        {
            for (int n = 0; n <= full.Length; n++) Parse(full[..n]);
        });
        // A cut inside the last sheet's BOF leaves that sheet out and keeps the one before it whole.
        var cut = Parse(full[..^75]);
        Eq(["A"], cut!.Select(s => s.Name));
        Assert.Equal(6, cut![0].Rows.Count);
    }

    [Fact]
    public void Random_damage_never_throws()
    {
        var full = Everything();
        var random = new Random(1234);
        Fast(() =>
        {
            for (int i = 0; i < 3000; i++)
            {
                var copy = (byte[])full.Clone();
                int flips = 1 + random.Next(8);
                for (int f = 0; f < flips; f++) copy[random.Next(copy.Length)] = (byte)random.Next(256);
                Parse(copy, maxRows: 50, maxColumns: 20);
            }
        });
    }

    [Fact]
    public void Nested_worksheet_substreams_cannot_make_the_work_quadratic()
    {
        // Ten thousand worksheet BOFs nested in each other, each with its own BOUNDSHEET: every sheet would walk to the end.
        const int n = 10_000;
        var bodies = new List<byte[]>();
        for (int i = 0; i < n; i++) { bodies.Add(Bof(0x0010, false)); bodies.Add(Number(0, 0, i)); }
        for (int i = 0; i < n; i++) bodies.Add(Eof);
        var head = Cat(Bof(0x0005, false), Xf(0));
        int boundSheet = R(0x0085, U32(0), [0, 0, 1, 0], Latin1("s")).Length;
        int at = head.Length + n * boundSheet + Eof.Length, bofLength = Bof(0x0010, false).Length, numberLength = Number(0, 0, 0).Length;
        var list = new List<byte[]> { head };
        for (int i = 0; i < n; i++) list.Add(R(0x0085, U32((uint)(at + i * (bofLength + numberLength))), [0, 0, 1, 0], Latin1("s")));
        list.Add(Eof);
        list.AddRange(bodies);
        var stream = Cat(list.ToArray());
        IReadOnlyList<LegacyExcelSheet>? sheets = null;
        var counts = default(LegacyExcel.ParseCounts);
        Fast(() => sheets = LegacyExcel.Parse(stream, 1000, 100, CancellationToken.None, out counts));
        Assert.NotNull(sheets);
        // Every sheet stays listed up to the bound on how many are listed, but only the first few are walked: the rest
        // come back unread, with no rows and marked Truncated, and the work stays within its cap.
        Assert.Equal(LegacyExcel.MaxSheets, sheets.Count);
        Assert.Equal("0", sheets[0].Rows[0][0]);
        Assert.InRange(sheets.Count(s => s.Rows.Count > 0), 1, 100);
        Assert.All(sheets.Skip(100), s => { Assert.Equal("s", s.Name); Assert.Empty(s.Rows); Assert.True(s.Truncated); Assert.True(s.NotRead); });
        Assert.InRange(counts.Work, 0, counts.WorkLimit + 65_539);
    }

    /// <summary>count empty CONTINUE records, back to back.</summary>
    static byte[] EmptyContinues(int count)
    {
        var run = new byte[4 * count];
        for (int i = 0; i < run.Length; i += 4) run[i] = 0x3C;
        return run;
    }

    [Fact]
    public void A_continue_run_that_many_sheets_reach_is_counted_against_the_work_cap()
    {
        // n worksheets, each starting inside the record before it: sheet i's BOF and string FORMULA are followed by a record
        // whose data is sheet i+1's BOF and FORMULA, so every sheet's walk hops over the later sheets' starts, reaches the
        // same STRING record, and finds after it the same long run of empty CONTINUE records.
        const int n = 1000, continues = 400_000;
        var bof = Bof(0x0010, false);
        var formula = Formula(0, 0, Special(0, 0));
        int hopData = bof.Length + formula.Length;
        var bodies = new List<byte[]>();
        for (int i = 0; i < n; i++)
        {
            bodies.Add(bof);
            bodies.Add(formula);
            if (i < n - 1) bodies.Add(Cat(U16(0x04BC), U16(hopData)));
        }
        bodies.Add(StringRec("hello"));
        bodies.Add(EmptyContinues(continues));
        bodies.Add(Eof);
        var head = Cat(Bof(0x0005, false), Xf(0));
        int boundSheet = R(0x0085, U32(0), [0, 0, 1, 0], Latin1("s")).Length;
        int at = head.Length + n * boundSheet + Eof.Length;
        var list = new List<byte[]> { head };
        for (int i = 0; i < n; i++) list.Add(R(0x0085, U32((uint)(at + i * (hopData + 4))), [0, 0, 1, 0], Latin1("s")));
        list.Add(Eof);
        list.AddRange(bodies);
        var stream = Cat(list.ToArray());

        var sheets = LegacyExcel.Parse(stream, 1000, 100, CancellationToken.None, out var counts);
        Assert.NotNull(sheets);
        Assert.Equal("hello", sheets[0].Rows[0][0]);
        Assert.False(sheets[0].Truncated);
        // Each sheet's walk of the run counts in full, so the cap (about two and a half runs here) stops the third sheet;
        // the rest stay listed unread (no rows, Truncated), and the work never goes past the cap by more than one record
        // or one gathering.
        Assert.Equal(n, sheets.Count);
        int walked = sheets.Count(s => s.Rows.Count > 0);
        Assert.InRange(walked, 2, 4);
        Assert.True(sheets[walked - 1].Truncated || sheets[walked].Truncated);
        Assert.All(sheets.Skip(4), s => { Assert.Empty(s.Rows); Assert.True(s.Truncated); });
        Assert.InRange(counts.Work, counts.WorkLimit, counts.WorkLimit + LegacyExcel.MaxStringGatherBytes + 65_539);
    }

    [Fact]
    public void A_continue_run_after_a_formula_string_counts_as_work_once()
    {
        // Twice as many empty CONTINUE records as one gathering takes: the text ends in the STRING record itself, the
        // gathered part counts as it is gathered, and the rest counts record by record as the walk passes over it.
        int records = (int)(LegacyExcel.MaxStringGatherBytes / 4) * 2;
        var stream = OneSheet(Formula(0, 0, Special(0, 0)), StringRec("text"), EmptyContinues(records), Label(1, 0, "after"));
        var sheets = LegacyExcel.Parse(stream, 1000, 100, CancellationToken.None, out var counts);
        Eq(["text"], sheets![0].Rows[0]);
        Eq(["after"], sheets[0].Rows[1]);
        Assert.InRange(counts.Work, 4L * records, 4L * records + 200);
    }

    [Fact]
    public void Cells_kept_over_all_sheets_are_capped_and_the_sheet_that_reaches_the_cap_is_marked()
    {
        const int maxRows = 100, maxColumns = 50;
        long limit = LegacyExcel.SlotLimit(maxRows, maxColumns);
        Assert.Equal(8L * maxRows * (maxColumns + 1), limit);
        // One cell at the last column of every row: each sheet takes maxRows x (maxColumns + 1) slots, an 8th of the cap,
        // so 8 sheets filled to every limit fit whole, the 9th is reached but stops at its first cell, and the rest are
        // listed unread.
        var far = Enumerable.Range(0, maxRows).Select(r => Rk(r, maxColumns - 1, RkInt(r))).ToArray();
        var sheets = LegacyExcel.Parse(Book([Xf(0)], Enumerable.Range(0, 12).Select(i => new Sheet("S" + i, far)).ToArray()),
            maxRows, maxColumns, CancellationToken.None, out var counts);
        Assert.NotNull(sheets);
        Eq(Enumerable.Range(0, 12).Select(i => "S" + i), sheets.Select(s => s.Name));
        Assert.All(sheets.Take(8), s =>
        {
            Assert.False(s.Truncated);
            Assert.False(s.NotRead);
            Assert.Equal(maxRows, s.Rows.Count);
            Assert.Equal(maxColumns, s.Rows[^1].Length);
        });
        Assert.All(sheets.Skip(8), s => { Assert.True(s.Truncated); Assert.Empty(s.Rows); });
        Assert.False(sheets[8].NotRead);
        Assert.All(sheets.Skip(9), s => Assert.True(s.NotRead));
        Assert.Equal(limit, counts.SlotsKept);
        Assert.Equal(limit, counts.SlotLimit);
    }

    [Fact]
    public void Many_large_sheets_of_ordinary_width_fit_under_the_cell_cap()
    {
        // 15 sheets of 2000 rows by 100 columns at the preview's limits (3,030,000 of the 3,216,000 slots): each row is
        // charged its 100 columns, not the 128 its array grew to while the cells came in one by one.
        const int maxRows = 2000, maxColumns = 200, rows = 2000, columns = 100;
        var rks = Enumerable.Repeat(RkInt(0), columns).ToArray();
        var body = Enumerable.Range(0, rows).Select(r => MulRk(r, 0, rks)).ToArray();
        var stream = Book([Xf(0)], Enumerable.Range(0, 15).Select(i => new Sheet("S" + i, body)).ToArray());
        var sheets = LegacyExcel.Parse(stream, maxRows, maxColumns, CancellationToken.None, out var counts);
        Assert.NotNull(sheets);
        Assert.Equal(15, sheets.Count);
        Assert.All(sheets, s =>
        {
            Assert.False(s.Truncated);
            Assert.Equal(rows, s.Rows.Count);
            Assert.All(s.Rows, r => Assert.Equal(columns, r.Length));
        });
        Assert.Equal(15L * rows * (columns + 1), counts.SlotsKept);
    }

    [Fact]
    public void Once_the_cell_cap_is_spent_no_later_cell_of_a_mulrk_is_kept()
    {
        // Limits 1 x 10: a cap of 8 x 11 slots. The first sheet takes 2, then 7 full rows take 11 each, leaving 9 for
        // the last sheet's 10-cell MULRK: its row and seven more cells fit, and nothing after the first that doesn't.
        const int maxRows = 1, maxColumns = 10;
        var full = MulRk(0, 0, Enumerable.Range(1, maxColumns).Select(i => RkInt(i)).ToArray());
        var list = new List<Sheet> { new("First", [Rk(0, 0, RkInt(0))]) };
        list.AddRange(Enumerable.Range(0, 7).Select(i => new Sheet("F" + i, [full])));
        list.Add(new Sheet("Last", [full]));
        list.Add(new Sheet("After", [Rk(0, 0, RkInt(9))]));
        var sheets = LegacyExcel.Parse(Book([Xf(0)], list.ToArray()), maxRows, maxColumns, CancellationToken.None, out var counts);
        Assert.NotNull(sheets);
        Assert.Equal(10, sheets.Count);
        var last = sheets[8];
        Assert.Equal("Last", last.Name);
        Assert.True(last.Truncated);
        Assert.False(last.NotRead);
        Eq(["1", "2", "3", "4", "5", "6", "7", "8"], Assert.Single(last.Rows));
        Assert.Equal(counts.SlotLimit, counts.SlotsKept);
        Assert.Empty(sheets[9].Rows);
        Assert.True(sheets[9].Truncated);
        Assert.True(sheets[9].NotRead);
    }

    [Fact]
    public void Once_the_cell_cap_is_spent_a_cell_that_needs_no_new_slot_is_not_kept_either()
    {
        // Limits 2 x 4: a cap of 8 x 2 x 5 = 80 slots. Seven full sheets take 10 each and a small one 2, leaving 8. The
        // last sheet's first row takes 5 for a cell at its last column, its second row cannot have the 5 it needs, and
        // then a cell at a lower column of the first row, whose array is already wide enough, must not be kept.
        const int maxRows = 2, maxColumns = 4;
        var fullSheet = Enumerable.Range(0, maxRows).Select(r => MulRk(r, 0, RkInt(1), RkInt(2), RkInt(3), RkInt(4))).ToArray();
        var list = Enumerable.Range(0, 7).Select(i => new Sheet("F" + i, fullSheet)).ToList();
        list.Add(new Sheet("Pad", [Rk(0, 0, RkInt(0))]));
        list.Add(new Sheet("Last", [Label(0, 3, "x"), Label(1, 3, "spends"), Label(0, 0, "late")]));
        var sheets = LegacyExcel.Parse(Book([Xf(0)], list.ToArray()), maxRows, maxColumns, CancellationToken.None, out var counts);
        Assert.NotNull(sheets);
        var last = sheets[^1];
        Assert.Equal("Last", last.Name);
        Assert.True(last.Truncated);
        Eq([null, null, null, "x"], Assert.Single(last.Rows));
        Assert.Equal(counts.SlotLimit - 3, counts.SlotsKept);
    }

    /// <summary>A MULRK of count cells, all the same RK, written straight into one array (fast for very long records).</summary>
    static byte[] LongMulRk(int row, int first, int count, uint rk)
    {
        int length = 6 + 6 * count;
        var r = new byte[4 + length];
        BitConverter.TryWriteBytes(r.AsSpan(0), (ushort)0x00BD);
        BitConverter.TryWriteBytes(r.AsSpan(2), (ushort)length);
        BitConverter.TryWriteBytes(r.AsSpan(4), (ushort)row);
        BitConverter.TryWriteBytes(r.AsSpan(6), (ushort)first);
        for (int i = 0; i < count; i++) BitConverter.TryWriteBytes(r.AsSpan(8 + 6 * i + 2), rk);
        BitConverter.TryWriteBytes(r.AsSpan(r.Length - 2), (ushort)(first + count - 1));
        return r;
    }

    /// <summary>Most cells one MULRK can hold (its length is 16 bits).</summary>
    const int FullMulRk = (0xFFFF - 6) / 6;

    [Fact]
    public void Numbers_beyond_the_limits_are_not_formatted()
    {
        // 200 full MULRK records (over two million cells) on rows past the limit, a MULRK running past the column
        // limit, and single numbers past either limit: only the cells inside the limits are turned into text.
        const int maxRows = 10, maxColumns = 5;
        var records = new List<byte[]> { MulRk(0, 0, RkInt(1), RkInt(2), RkInt(3)), LongMulRk(1, 0, FullMulRk, RkInt(7)) };
        records.AddRange(Enumerable.Range(0, 200).Select(i => LongMulRk(maxRows + i, 0, FullMulRk, RkInt(9))));
        records.Add(Number(maxRows, 0, 1.5));
        records.Add(Number(2, maxColumns, 1.5));
        records.Add(Rk(maxRows + 1, 0, RkInt(4)));
        records.Add(Formula(2, maxColumns + 3, F64(2.5)));
        records.Add(Number(2, 1, 6.5));
        var sheets = LegacyExcel.Parse(OneSheet(records.ToArray()), maxRows, maxColumns, CancellationToken.None, out var counts);
        var sheet = Assert.Single(sheets!);
        Assert.True(sheet.Truncated);
        EqRows([["1", "2", "3"], ["7", "7", "7", "7", "7"], [null, "6.5"]], sheet.Rows);
        Assert.Equal(3 + maxColumns + 1, counts.NumbersFormatted);
    }

    [Fact]
    public void A_row_of_a_mulrk_inside_the_limits_reads_as_before_when_it_starts_past_column_zero()
    {
        var sheets = LegacyExcel.Parse(OneSheet(MulRk(0, 2, RkInt(1), RkInt(2), RkInt(3), RkInt(4)), Rk(1, 3, RkInt(5)), Rk(1, 4, RkInt(6))),
            10, 4, CancellationToken.None, out var counts);
        var sheet = Assert.Single(sheets!);
        Assert.True(sheet.Truncated);
        EqRows([[null, null, "1", "2"], [null, null, null, "5"]], sheet.Rows);
        Assert.Equal(3, counts.NumbersFormatted);
    }

    [Fact]
    public void A_cancel_inside_one_long_mulrk_stops_within_a_bounded_number_of_cells()
    {
        // One MULRK of over ten thousand cells inside the limits: a cancel at cell 5000 takes effect within one check
        // interval, not at the end of the record.
        var stream = OneSheet(LongMulRk(0, 0, FullMulRk, RkInt(1)));
        using var cts = new CancellationTokenSource();
        long last = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => LegacyExcel.Parse(stream, 10, 20_000, null, cts.Token, step =>
        {
            last = step;
            if (step == 5000) cts.Cancel();
        }, out _));
        Assert.InRange(last, 5000, 5000 + LegacyExcel.CancelCheckSteps);
        Assert.True(last < FullMulRk);
    }

    [Fact]
    public void A_cancel_among_many_records_beyond_the_limits_stops_within_a_bounded_number_of_records()
    {
        var records = Enumerable.Range(0, 20_000).Select(i => Rk(5000 + i, 0, RkInt(i))).ToArray();
        var stream = OneSheet(records);
        using var cts = new CancellationTokenSource();
        long last = 0;
        Assert.ThrowsAny<OperationCanceledException>(() => LegacyExcel.Parse(stream, 10, 10, null, cts.Token, step =>
        {
            last = step;
            if (step == 3000) cts.Cancel();
        }, out _));
        Assert.InRange(last, 3000, 3000 + LegacyExcel.CancelCheckSteps);
    }

    [Fact]
    public void Empty_continue_records_after_the_sst_add_no_pieces()
    {
        // A million empty CONTINUE records inside the SST, between a string's first characters and its last one.
        var sst = R(0x00FC, U32(2), U32(2), Str("one"), U16(3), [0], Latin1("tw"));
        var stream = SstOf(sst, EmptyContinues(1_000_000), Continue([0], Latin1("o")));
        var sheets = LegacyExcel.Parse(stream, 1000, 100, CancellationToken.None, out var counts);
        Eq(["one", "two"], Assert.Single(sheets!).Rows[0]);
        Assert.Equal(2, counts.SharedStringPieces);
    }

    [Fact]
    public void The_shared_strings_are_gathered_up_to_their_cap_and_the_rest_is_walked_past()
    {
        // Full CONTINUE records after the SST, more of them than the cap allows: the gathering stops at the cap, the
        // walk passes the rest, and the sheet after the globals is still read.
        Assert.True(LegacyExcel.MaxSstGatherBytes >= 2 * LegacyExcel.MaxSharedChars + 9L * LegacyExcel.MaxSharedStrings);
        const int data = 8224, record = data + 4;
        int records = (int)(LegacyExcel.MaxSstGatherBytes / record) + 100;
        var run = new byte[(long)records * record];
        for (int i = 0; i < records; i++)
        {
            BitConverter.TryWriteBytes(run.AsSpan(i * record), (ushort)0x003C);
            BitConverter.TryWriteBytes(run.AsSpan(i * record + 2), (ushort)data);
        }
        var stream = OneSheet([Xf(0), Sst("kept"), run], LabelSst(0, 0, 0));
        var sheets = LegacyExcel.Parse(stream, 1000, 100, CancellationToken.None, out var counts);
        Eq(["kept"], Assert.Single(sheets!).Rows[0]);
        Assert.InRange(counts.SharedStringPieces, 2, LegacyExcel.MaxSstGatherBytes / record + 2);
        Assert.True(counts.SharedStringPieces < records);
        Assert.InRange(counts.Work, LegacyExcel.MaxSstGatherBytes, LegacyExcel.MaxSstGatherBytes + record + 200);
    }

    [Fact]
    public void The_shared_strings_are_gathered_up_to_a_piece_count_and_the_rest_is_walked_past()
    {
        // One-byte CONTINUE records after the SST, more of them than the piece cap and far under the byte cap: the
        // gathering stops at the piece cap, the walk passes the rest, and the sheet after the globals is still read.
        Assert.True(LegacyExcel.MaxSstPieces < LegacyExcel.MaxSstGatherBytes / 5);
        const int record = 5;
        int records = LegacyExcel.MaxSstPieces + 1000;
        var run = new byte[(long)records * record];
        for (int i = 0; i < records; i++)
        {
            run[i * record] = 0x3C;
            run[i * record + 2] = 1;
        }
        var stream = OneSheet([Xf(0), Sst("kept"), run], LabelSst(0, 0, 0));
        var sheets = LegacyExcel.Parse(stream, 1000, 100, CancellationToken.None, out var counts);
        Eq(["kept"], Assert.Single(sheets!).Rows[0]);
        Assert.Equal(LegacyExcel.MaxSstPieces, counts.SharedStringPieces);
    }

    // ------------------------------------------------------------------ choosing the one sheet to read

    static IReadOnlyList<LegacyExcelSheet>? Choose(byte[] stream, Func<IReadOnlyList<LegacyExcelSheet>, int>? choose,
        out LegacyExcel.ParseCounts counts, int maxRows = 1000, int maxColumns = 100)
        => LegacyExcel.Parse(stream, maxRows, maxColumns, choose, CancellationToken.None, out counts);

    /// <summary>What the chooser was shown, copied when it was called.</summary>
    sealed record Seen(string Name, bool Hidden, bool Truncated, bool NotRead, int Rows);

    static Seen[] Snapshot(IReadOnlyList<LegacyExcelSheet> sheets) => sheets.Select(s => new Seen(s.Name, s.Hidden, s.Truncated, s.NotRead, s.Rows.Count)).ToArray();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void With_a_chooser_only_the_chosen_sheet_is_read(bool biff5)
    {
        static byte[] Label5(int row, string s) => R(0x0204, U16(row), U16(0), U16(0), U16(s.Length), Latin1(s));
        byte[] Text(int row, string s) => biff5 ? Label5(row, s) : Label(row, 0, s);
        Sheet[] Sheets() =>
        [
            new("One", [Text(0, "one")]),
            new("Chart", [Number(0, 0, 1)], Kind: 2, BofKind: 0x0020),
            new("Secret", [Text(0, "secret")], Hidden: 1),
            new("Dialog", [Text(0, "d")], BofKind: 0x0020),
            new("Three", [Text(0, "three a"), Text(1, "three b")]),
            new("Copy", []),
            new("Four", [Text(0, "four")], Hidden: 2),
        ];
        // "Copy" points at the same substream as "One": dropped, like the dialog, before the chooser sees the list.
        Book([Xf(0)], Sheets(), out int firstBoundSheet, biff5);
        uint firstOffset = BitConverter.ToUInt32(Book([Xf(0)], Sheets(), biff5), firstBoundSheet + 4);
        var stream = Book([Xf(0)], Sheets(), biff5, offsets: [null, null, null, null, null, firstOffset, null]);

        int calls = 0;
        Seen[]? shown = null;
        var sheets = Choose(stream, listed => { calls++; shown = Snapshot(listed); return 2; }, out _);
        Assert.NotNull(sheets);
        Assert.Equal(1, calls);
        Assert.Equal(
            new[] { new Seen("One", false, false, true, 0), new Seen("Secret", true, false, true, 0), new Seen("Three", false, false, true, 0), new Seen("Four", true, false, true, 0) },
            shown);
        // The result is the same list, in the same order, with the chosen sheet read.
        Eq(shown!.Select(s => s.Name), sheets.Select(s => s.Name));
        Assert.Equal(shown!.Select(s => s.Hidden), sheets.Select(s => s.Hidden));
        EqRows([["three a"], ["three b"]], sheets[2].Rows);
        Assert.False(sheets[2].NotRead);
        Assert.False(sheets[2].Truncated);
        Assert.All(new[] { 0, 1, 3 }, i =>
        {
            Assert.True(sheets[i].NotRead);
            Assert.False(sheets[i].Truncated);
            Assert.Empty(sheets[i].Rows);
        });

        // A hidden sheet can be chosen too.
        Eq(["secret"], Assert.Single(Choose(stream, _ => 1, out _)![1].Rows));
    }

    [Fact]
    public void A_chosen_index_outside_the_list_reads_no_sheet()
    {
        var stream = Book([Xf(0)], [new Sheet("A", [Label(0, 0, "a")]), new Sheet("B", [Label(0, 0, "b")])]);
        foreach (int index in new[] { -1, 2, int.MaxValue, int.MinValue })
        {
            var sheets = Choose(stream, _ => index, out var counts);
            Assert.NotNull(sheets);
            Eq(["A", "B"], sheets.Select(s => s.Name));
            Assert.All(sheets, s => { Assert.True(s.NotRead); Assert.Empty(s.Rows); Assert.False(s.Truncated); });
            Assert.Equal(0, counts.SlotsKept);
        }
        // An empty workbook: the chooser sees an empty list.
        int seen = -1;
        Assert.Empty(Choose(Book([Xf(0)], []), listed => { seen = listed.Count; return 0; }, out _)!);
        Assert.Equal(0, seen);
    }

    static void SameSheets(IReadOnlyList<LegacyExcelSheet>? expected, IReadOnlyList<LegacyExcelSheet>? actual)
    {
        Assert.NotNull(expected);
        Assert.NotNull(actual);
        Assert.Equal(expected.Count, actual.Count);
        for (int i = 0; i < expected.Count; i++)
        {
            Assert.Equal((expected[i].Name, expected[i].Truncated, expected[i].Hidden, expected[i].NotRead),
                (actual[i].Name, actual[i].Truncated, actual[i].Hidden, actual[i].NotRead));
            EqRows(expected[i].Rows, actual[i].Rows);
        }
    }

    [Fact]
    public void A_null_chooser_reads_every_sheet_like_the_method_without_one()
    {
        SameSheets(Parse(Everything()), Choose(Everything(), null, out _));
        // With the caps reached part way, too.
        var far = Enumerable.Range(0, 10).Select(r => Rk(r, 4, RkInt(r))).ToArray();
        var capped = Book([Xf(0)], Enumerable.Range(0, 12).Select(i => new Sheet("S" + i, far)).ToArray());
        SameSheets(Parse(capped, 10, 5), Choose(capped, null, out _, 10, 5));
        Assert.Contains(Choose(capped, null, out _, 10, 5)!, s => s.NotRead);

        var dir = Directory.CreateTempSubdirectory("rp-xls-");
        try
        {
            string file = Path.Combine(dir.FullName, "book.xls");
            File.WriteAllBytes(file, CfbFixtures.Ole(root => CfbFixtures.Stream(root, "Workbook", Everything())));
            SameSheets(LegacyExcel.ReadSheets(file, 100, 100, CancellationToken.None), LegacyExcel.ReadSheets(file, 100, 100, null, CancellationToken.None));
            var second = LegacyExcel.ReadSheets(file, 100, 100, listed => listed.Count - 1, CancellationToken.None);
            Assert.NotNull(second);
            Assert.True(second[0].NotRead);
            Assert.Equal("b", second[1].Rows[0][0]);
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Each_sheet_chosen_in_turn_reads_as_it_does_among_all()
    {
        var stream = Everything();
        var all = Parse(stream)!;
        for (int i = 0; i < all.Count; i++)
        {
            int chosen = i;
            var one = Choose(stream, _ => chosen, out _)!;
            Assert.Equal(all.Count, one.Count);
            for (int j = 0; j < all.Count; j++)
            {
                if (j == chosen) SameSheets([all[j]], [one[j]]);
                else { Assert.True(one[j].NotRead); Assert.Empty(one[j].Rows); }
            }
        }
    }

    [Fact]
    public void A_chooser_that_throws_gives_null_but_a_cancellation_is_passed_on()
    {
        var stream = Everything();
        Assert.Null(Choose(stream, _ => throw new InvalidOperationException("broken"), out _));
        Assert.ThrowsAny<OperationCanceledException>(() => Choose(stream, _ => throw new OperationCanceledException(), out _));

        var dir = Directory.CreateTempSubdirectory("rp-xls-");
        try
        {
            string file = Path.Combine(dir.FullName, "book.xls");
            File.WriteAllBytes(file, CfbFixtures.Ole(root => CfbFixtures.Stream(root, "Workbook", stream)));
            Assert.Null(LegacyExcel.ReadSheets(file, 100, 100, _ => throw new InvalidOperationException("broken"), CancellationToken.None));
            Assert.ThrowsAny<OperationCanceledException>(() => LegacyExcel.ReadSheets(file, 100, 100, _ => throw new OperationCanceledException(), CancellationToken.None));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void A_sheet_chosen_after_many_large_ones_costs_about_one_sheets_work()
    {
        // 30 sheets of 500 rows by 20 numbers: choosing the last walks only its own substream, not the 29 before it.
        const int sheetCount = 30;
        var rks = Enumerable.Range(0, 20).Select(c => RkInt(c)).ToArray();
        Sheet Big(int i) => new("S" + i, [Label(0, 0, "sheet " + i), .. Enumerable.Range(1, 500).Select(r => MulRk(r, 0, rks))]);
        var stream = Book([Xf(0)], Enumerable.Range(0, sheetCount).Select(Big).ToArray());
        long oneSheet = stream.Length / sheetCount;

        var all = Choose(stream, null, out var allCounts);
        var last = Choose(stream, listed => listed.Count - 1, out var lastCounts);
        Assert.NotNull(all);
        Assert.NotNull(last);
        Assert.Equal("sheet 29", last[^1].Rows[0][0]);
        Assert.Equal(501, last[^1].Rows.Count);
        SameSheets([all[^1]], [last[^1]]);
        Assert.All(last.Take(sheetCount - 1), s => Assert.True(s.NotRead));
        Assert.InRange(lastCounts.Work, oneSheet / 2, oneSheet * 3 / 2);
        Assert.True(allCounts.Work > 25 * lastCounts.Work, $"all {allCounts.Work}, last {lastCounts.Work}");
    }

    [Fact]
    public void With_a_chooser_the_cell_cap_never_stops_the_chosen_sheet_because_of_others()
    {
        // Twelve sheets filled to every limit: read all, the cap stops the 9th; chosen alone, the 12th is read whole.
        const int maxRows = 50, maxColumns = 10;
        var far = Enumerable.Range(0, maxRows).Select(r => Rk(r, maxColumns - 1, RkInt(r))).ToArray();
        var stream = Book([Xf(0)], Enumerable.Range(0, 12).Select(i => new Sheet("S" + i, far)).ToArray());
        Assert.True(Choose(stream, null, out _, maxRows, maxColumns)![11].NotRead);
        var sheets = Choose(stream, _ => 11, out var counts, maxRows, maxColumns);
        Assert.NotNull(sheets);
        var chosen = sheets[11];
        Assert.False(chosen.Truncated);
        Assert.False(chosen.NotRead);
        Assert.Equal(maxRows, chosen.Rows.Count);
        Assert.Equal((long)maxRows * (maxColumns + 1), counts.SlotsKept);
    }

    [Fact]
    public void A_sheet_read_with_every_cell_beyond_the_limits_is_truncated_with_no_rows_but_read()
    {
        var stream = Book([Xf(0)], [new Sheet("Near", [Label(0, 0, "a")]), new Sheet("Far", [Label(5, 0, "row"), Label(0, 5, "col")])]);
        foreach (var sheets in new[] { Parse(stream, 2, 2), Choose(stream, _ => 1, out _, 2, 2) })
        {
            Assert.NotNull(sheets);
            var far = sheets[1];
            Assert.True(far.Truncated);
            Assert.Empty(far.Rows);
            Assert.False(far.NotRead);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Choosing_works_the_same_in_both_formats(bool biff5)
    {
        static byte[] Label5(int row, int col, string s) => R(0x0204, U16(row), U16(col), U16(0), U16(s.Length), Latin1(s));
        byte[] Text(int row, int col, string s) => biff5 ? Label5(row, col, s) : Label(row, col, s);
        var stream = Book([Xf(0)],
        [
            new Sheet("A", [Text(0, 0, "a")]),
            new Sheet("B", [Text(0, 0, "b1"), Number(0, 1, 2.5), Text(3, 0, "b2")], Hidden: 1),
            new Sheet("C", [Text(0, 0, "c")]),
        ], biff5);
        var all = Parse(stream)!;
        var b = Choose(stream, listed => listed.ToList().FindIndex(s => s.Hidden), out _);
        Assert.NotNull(b);
        Eq(["A", "B", "C"], b.Select(s => s.Name));
        SameSheets([all[1]], [b[1]]);
        Eq(["b1", "2.5"], b[1].Rows[0]);
        Assert.True(b[0].NotRead && b[2].NotRead);
    }

    [Fact]
    public void The_cell_cap_follows_the_limits_up_to_its_ceiling()
    {
        Assert.Equal(8L * 2000 * 201, LegacyExcel.SlotLimit(2000, 200));
        Assert.Equal(3_216_000, LegacyExcel.SlotLimit(2000, 200));
        Assert.Equal(0, LegacyExcel.SlotLimit(0, 100));
        Assert.Equal(8, LegacyExcel.SlotLimit(1, 0));
        Assert.Equal(LegacyExcel.MaxSlots, LegacyExcel.SlotLimit(int.MaxValue, int.MaxValue));
        Assert.Equal(0, LegacyExcel.SlotLimit(-5, -5));
        // A small workbook keeps every cell it shows, counted exactly: two rows and three cells in the widest row.
        Assert.NotNull(LegacyExcel.Parse(OneSheet(Label(0, 0, "a"), Label(1, 2, "b")), 10, 10, CancellationToken.None, out var counts));
        Assert.Equal(2 + 1 + 3, counts.SlotsKept);
    }

    [Fact]
    public void Cancellation_reaches_a_long_continue_run()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        // No sheets and only a handful of records in the globals: only the walk of the SST's CONTINUE run looks at the token.
        var stream = Book([Xf(0), Sst("x"), EmptyContinues(10_000)], []);
        Assert.NotNull(LegacyExcel.Parse(stream, 10, 10, CancellationToken.None));
        Assert.ThrowsAny<OperationCanceledException>(() => LegacyExcel.Parse(stream, 10, 10, cts.Token));
    }

    [Fact]
    public void Cancellation_is_passed_on()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var many = OneSheet(Enumerable.Range(0, 5000).Select(i => Number(i % 100, i / 100, i)).ToArray());
        Assert.ThrowsAny<OperationCanceledException>(() => LegacyExcel.Parse(many, 100, 100, cts.Token));
        Assert.ThrowsAny<OperationCanceledException>(() => LegacyExcel.ReadSheets(typeof(LegacyExcelTests).Assembly.Location, 10, 10, cts.Token));
    }

    // ------------------------------------------------------------------ the public method, on files

    [Fact]
    public void Files_that_are_missing_or_not_compound_files_give_null()
    {
        var dir = Directory.CreateTempSubdirectory("rp-xls-");
        try
        {
            Assert.Null(LegacyExcel.ReadSheets(Path.Combine(dir.FullName, "missing.xls"), 10, 10, CancellationToken.None));
            string text = Path.Combine(dir.FullName, "text.xls");
            File.WriteAllText(text, "Name\tValue\r\nnot a workbook\t1\r\n");
            Assert.Null(LegacyExcel.ReadSheets(text, 10, 10, CancellationToken.None));
            string bare = Path.Combine(dir.FullName, "bare.xls");
            File.WriteAllBytes(bare, Everything()); // a workbook stream that isn't inside a compound file
            Assert.Null(LegacyExcel.ReadSheets(bare, 10, 10, CancellationToken.None));
            Assert.Null(LegacyExcel.ReadSheets("", 10, 10, CancellationToken.None));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public void Compound_files_with_a_workbook_or_book_stream_are_read()
    {
        var dir = Directory.CreateTempSubdirectory("rp-xls-");
        try
        {
            string biff8 = Path.Combine(dir.FullName, "book8.xls");
            File.WriteAllBytes(biff8, CfbFixtures.Ole(root => CfbFixtures.Stream(root, "Workbook", Everything())));
            using (File.Open(biff8, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)) // another program has it open
            {
                var sheets = LegacyExcel.ReadSheets(biff8, 100, 100, CancellationToken.None);
                Assert.NotNull(sheets);
                Eq(["A", "B"], sheets.Select(s => s.Name));
                Assert.Equal("twoo", sheets[0].Rows[0][1]);
            }

            string biff5 = Path.Combine(dir.FullName, "book5.xls");
            var book5 = Book([], [new Sheet("Old", [R(0x0204, U16(0), U16(0), U16(0), U16(2), Latin1("hi"))])], biff5: true);
            File.WriteAllBytes(biff5, CfbFixtures.Ole(root => CfbFixtures.Stream(root, "Book", book5)));
            Assert.Equal("hi", Assert.Single(LegacyExcel.ReadSheets(biff5, 100, 100, CancellationToken.None)!).Rows[0][0]);

            string other = Path.Combine(dir.FullName, "other.xls");
            File.WriteAllBytes(other, CfbFixtures.Ole(root => CfbFixtures.Stream(root, "WordDocument", new byte[600])));
            Assert.Null(LegacyExcel.ReadSheets(other, 100, 100, CancellationToken.None));

            string encrypted = Path.Combine(dir.FullName, "encrypted.xls");
            File.WriteAllBytes(encrypted, CfbFixtures.Ole(root =>
            {
                CfbFixtures.Stream(root, "Workbook", Everything());
                CfbFixtures.Stream(root, "EncryptionInfo", new byte[100]);
            }));
            Assert.Null(LegacyExcel.ReadSheets(encrypted, 100, 100, CancellationToken.None));
        }
        finally { dir.Delete(recursive: true); }
    }
}
