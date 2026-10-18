using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Formats.Tar;
using System.IO;
using System.IO.Compression;
using System.Runtime.CompilerServices;
using System.Text;
using System.Windows.Media.Imaging;

namespace PreviewHarness;

/// <summary>Writes small but structurally real sample files for every format family into a scratch folder.</summary>
internal static class SampleGenerator
{
    public static List<string> Generate(string dir)
    {
        Directory.CreateDirectory(dir);
        var files = new List<string>();
        string P(string name) { var p = Path.Combine(dir, name); files.Add(p); return p; }

        using (var bmp = Drawing(640, 420, alpha: true)) bmp.Save(P("alpha.png"), ImageFormat.Png);
        using (var bmp = Drawing(800, 500, alpha: false))
        {
            bmp.Save(P("photo.bmp"), ImageFormat.Bmp);
            // JPEG with EXIF orientation 6 (rotate 90° CW on display).
            var pi = (PropertyItem)RuntimeHelpers.GetUninitializedObject(typeof(PropertyItem));
            pi.Id = 0x0112; pi.Type = 3; pi.Len = 2; pi.Value = BitConverter.GetBytes((ushort)6);
            bmp.SetPropertyItem(pi);
            bmp.Save(P("rotated-exif6.jpg"), ImageFormat.Jpeg);
        }
        WriteWpf(P("multipage.tif"), new TiffBitmapEncoder(), 3);
        WriteAnimatedGif(P("animated.gif"), 4);
        WriteIco(P("icon.ico"));
        WriteTga(P("truecolor32.tga"), rle: false);
        WriteTga(P("rle24.tga"), rle: true);
        WritePsd(P("layered.psd"));
        WriteEmf(P("vector.emf"));
        WritePdf(P("two-pages.pdf"));
        WriteDocx(P("report.docx"));
        WriteXlsx(P("numbers.xlsx"));
        WritePptx(P("deck.pptx"));
        WriteOdt(P("notes.odt"));
        WriteZip(P("bundle.zip"));
        WriteTarGz(P("source.tar.gz"));
        WriteDxf(P("drawing.dxf"));
        WriteDwg(P("synthetic.dwg"));
        WriteCdrRiff(P("legacy.cdr"));
        WriteCdrZip(P("modern-x4.cdr"));
        WriteEps(P("preview.eps"));
        WriteIndd(P("layout.indd"));
        File.WriteAllText(P("readme.txt"), "Hello from Anything!\nLine two\n\tTabbed line\nÜnïcödé ✓\n");
        File.WriteAllText(P("Program.cs"), "using System;\nclass P {\n    static void Main() => Console.WriteLine(\"hi\");\n}\n");
        File.WriteAllText(P("minified.json"), "{\"name\":\"anything\",\"items\":[" + string.Join(",", Enumerable(60)) + "],\"nested\":{\"a\":1,\"b\":[true,false,null]}}");
        File.WriteAllText(P("utf16.txt"), "UTF-16 text with BOM\r\nsecond line\r\n", new UnicodeEncoding(false, true));
        File.WriteAllText(P("unknown.foo"), "This file has an unknown extension but is plain text.\nkey=value\n");
        File.WriteAllText(P("data.csv"), "Name,Qty,Price\n\"Widget, large\",3,9.99\nGadget,10,1.5\n\"Quote \"\"this\"\"\",1,0\n");
        File.WriteAllText(P("letter.rtf"), @"{\rtf1\ansi{\fonttbl{\f0 Arial;}}\f0\fs28 \b Bold title\b0\par Plain \i italic\i0  text.\par}");
        var rnd = new Random(42);
        var bin = new byte[10000];
        rnd.NextBytes(bin);
        File.WriteAllBytes(P("random.bin"), bin);
        WriteWav(P("tone.wav"));
        return files;
    }

    private static IEnumerable<string> Enumerable(int n)
    {
        for (int i = 0; i < n; i++) yield return "{\"id\":" + i + ",\"label\":\"item " + i + "\"}";
    }

    private static Bitmap Drawing(int w, int h, bool alpha)
    {
        var bmp = new Bitmap(w, h, alpha ? PixelFormat.Format32bppArgb : PixelFormat.Format24bppRgb);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.Clear(alpha ? Color.Transparent : Color.White);
        using (var lg = new LinearGradientBrush(new Rectangle(0, 0, w, h), Color.SteelBlue, Color.Orange, 30f))
            g.FillEllipse(lg, w / 10, h / 10, w * 8 / 10, h * 8 / 10);
        using var f = new Font("Segoe UI", h / 8f, FontStyle.Bold);
        g.DrawString("Anything", f, Brushes.Black, w / 6f, h / 2.6f);
        g.FillRectangle(Brushes.Red, 0, 0, w / 8, h / 8); // marks the top-left corner
        return bmp;
    }

    private static void WriteWpf(string path, BitmapEncoder enc, int frames)
    {
        for (int i = 0; i < frames; i++)
        {
            using var bmp = new Bitmap(320, 240, PixelFormat.Format24bppRgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.Clear(Color.FromArgb(40 + i * 60, 120, 200 - i * 40));
                using var f = new Font("Segoe UI", 48, FontStyle.Bold);
                g.DrawString((i + 1).ToString(), f, Brushes.White, 120, 70);
            }
            using var ms = new MemoryStream();
            bmp.Save(ms, ImageFormat.Png);
            ms.Position = 0;
            enc.Frames.Add(BitmapFrame.Create(ms, BitmapCreateOptions.None, BitmapCacheOption.OnLoad));
        }
        using var fs = File.Create(path);
        enc.Save(fs);
    }

    /// <summary>A real animated GIF: GDI+-encoded single frames stitched with local color tables + NETSCAPE loop.</summary>
    private static void WriteAnimatedGif(string path, int frames)
    {
        using var o = new MemoryStream();
        for (int i = 0; i < frames; i++)
        {
            byte[] g;
            using (var bmp = new Bitmap(160, 120, PixelFormat.Format24bppRgb))
            {
                using (var gr = Graphics.FromImage(bmp))
                {
                    gr.Clear(Color.FromArgb(40 + i * 50, 100, 220 - i * 40));
                    using var f = new Font("Segoe UI", 40, FontStyle.Bold);
                    gr.DrawString((i + 1).ToString(), f, Brushes.White, 55, 25);
                }
                using var ms = new MemoryStream();
                bmp.Save(ms, ImageFormat.Gif);
                g = ms.ToArray();
            }
            int p = 6;
            int lsdPacked = g[p + 4];
            p += 7;
            byte[] gct = Array.Empty<byte>();
            int gctBits = lsdPacked & 7;
            if ((lsdPacked & 0x80) != 0) { gct = g.AsSpan(p, 3 * (1 << (gctBits + 1))).ToArray(); p += gct.Length; }
            if (i == 0)
            {
                o.Write(g.AsSpan(0, 6));
                o.Write(g.AsSpan(6, 4));
                o.WriteByte((byte)(lsdPacked & 0x70)); // no global table
                o.WriteByte(0); o.WriteByte(0);
                o.Write(new byte[] { 0x21, 0xFF, 0x0B }); o.Write("NETSCAPE2.0"u8); o.Write(new byte[] { 3, 1, 0, 0, 0 });
            }
            while (g[p] == 0x21) // skip extensions
            {
                p += 2;
                while (g[p] != 0) p += g[p] + 1;
                p++;
            }
            if (g[p] != 0x2C) throw new InvalidDataException("unexpected GIF layout");
            o.Write(new byte[] { 0x21, 0xF9, 0x04, 0x08, 30, 0, 0, 0 }); // GCE: dispose to background, 300 ms
            o.Write(g.AsSpan(p, 9));
            int idPacked = g[p + 9];
            o.WriteByte((byte)((idPacked & 0x40) | 0x80 | gctBits)); // local color table = the frame's global table
            p += 10;
            o.Write(gct);
            int start = p;
            p++; // LZW min code size
            while (g[p] != 0) p += g[p] + 1;
            p++;
            o.Write(g.AsSpan(start, p - start));
        }
        o.WriteByte(0x3B);
        File.WriteAllBytes(path, o.ToArray());
    }

    private static void WriteIco(string path)
    {
        var sizes = new[] { 16, 32, 256 };
        var pngs = new List<byte[]>();
        foreach (var s in sizes)
        {
            using var b = Drawing(s, s, alpha: true);
            using var ms = new MemoryStream();
            b.Save(ms, ImageFormat.Png);
            pngs.Add(ms.ToArray());
        }
        using var w = new BinaryWriter(File.Create(path));
        w.Write((ushort)0); w.Write((ushort)1); w.Write((ushort)sizes.Length);
        int off = 6 + 16 * sizes.Length;
        for (int i = 0; i < sizes.Length; i++)
        {
            w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i])); w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
            w.Write((byte)0); w.Write((byte)0); w.Write((ushort)1); w.Write((ushort)32);
            w.Write(pngs[i].Length); w.Write(off);
            off += pngs[i].Length;
        }
        foreach (var p in pngs) w.Write(p);
    }

    private static void WriteTga(string path, bool rle)
    {
        int w = 200, h = 120;
        using var src = Drawing(w, h, alpha: !rle);
        using var bw = new BinaryWriter(File.Create(path));
        bw.Write((byte)0); bw.Write((byte)0); bw.Write((byte)(rle ? 10 : 2));
        bw.Write(new byte[5]);
        bw.Write((ushort)0); bw.Write((ushort)0); bw.Write((ushort)w); bw.Write((ushort)h);
        bw.Write((byte)(rle ? 24 : 32)); bw.Write((byte)(rle ? 0x20 : 0x28)); // top-left origin (+8 alpha bits)
        for (int y = 0; y < h; y++)
        {
            if (!rle)
            {
                for (int x = 0; x < w; x++) { var c = src.GetPixel(x, y); bw.Write(c.B); bw.Write(c.G); bw.Write(c.R); bw.Write(c.A); }
                continue;
            }
            int xx = 0;
            while (xx < w)
            {
                var c = src.GetPixel(xx, y);
                int run = 1;
                while (xx + run < w && run < 128 && src.GetPixel(xx + run, y) == c) run++;
                if (run > 1) { bw.Write((byte)(0x80 | (run - 1))); bw.Write(c.B); bw.Write(c.G); bw.Write(c.R); xx += run; }
                else { bw.Write((byte)0); bw.Write(c.B); bw.Write(c.G); bw.Write(c.R); xx++; }
            }
        }
    }

    private static void WritePsd(string path)
    {
        int w = 300, h = 200;
        using var src = Drawing(w, h, alpha: false);
        using var fs = File.Create(path);
        void BE16(int v) { fs.WriteByte((byte)(v >> 8)); fs.WriteByte((byte)v); }
        void BE32(long v) { fs.WriteByte((byte)(v >> 24)); fs.WriteByte((byte)(v >> 16)); fs.WriteByte((byte)(v >> 8)); fs.WriteByte((byte)v); }
        fs.Write("8BPS"u8); BE16(1); fs.Write(new byte[6]); BE16(3); BE32(h); BE32(w); BE16(8); BE16(3);
        BE32(0); // color mode data
        BE32(0); // image resources
        BE32(0); // layer and mask
        BE16(1); // RLE
        var rows = new List<byte[]>();
        for (int c = 0; c < 3; c++)
            for (int y = 0; y < h; y++)
            {
                var line = new byte[w];
                for (int x = 0; x < w; x++) { var px = src.GetPixel(x, y); line[x] = c == 0 ? px.R : c == 1 ? px.G : px.B; }
                rows.Add(PackBits(line));
            }
        foreach (var r in rows) BE16(r.Length);
        foreach (var r in rows) fs.Write(r);
    }

    private static byte[] PackBits(byte[] d)
    {
        var o = new List<byte>();
        int i = 0;
        while (i < d.Length)
        {
            int run = 1;
            while (i + run < d.Length && run < 128 && d[i + run] == d[i]) run++;
            if (run >= 2) { o.Add((byte)(257 - run)); o.Add(d[i]); i += run; continue; }
            int start = i, lit = 0;
            while (i < d.Length && lit < 128 && !(i + 1 < d.Length && d[i + 1] == d[i])) { i++; lit++; }
            if (lit == 0) { i++; lit = 1; }
            o.Add((byte)(lit - 1));
            for (int k = start; k < start + lit; k++) o.Add(d[k]);
        }
        return o.ToArray();
    }

    private static void WriteEmf(string path)
    {
        using var refBmp = new Bitmap(1, 1);
        using var rg = Graphics.FromImage(refBmp);
        IntPtr hdc = rg.GetHdc();
        using (var mf = new Metafile(path, hdc, new RectangleF(0, 0, 400, 300), MetafileFrameUnit.Pixel, EmfType.EmfPlusDual))
        {
            rg.ReleaseHdc(hdc);
            using var g = Graphics.FromImage(mf);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.FillRectangle(Brushes.LightYellow, 0, 0, 400, 300);
            g.DrawEllipse(new Pen(Color.DarkGreen, 6), 40, 40, 320, 220);
            g.DrawString("EMF", new Font("Arial", 40), Brushes.Navy, 140, 110);
        }
    }

    private static void WritePdf(string path)
    {
        var objs = new List<string>
        {
            "<< /Type /Catalog /Pages 2 0 R >>",
            "<< /Type /Pages /Kids [3 0 R 5 0 R] /Count 2 >>",
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 4 0 R /Resources << /Font << /F1 7 0 R >> >> >>",
            Stream("BT /F1 36 Tf 72 700 Td (Page one) Tj ET 0 0 1 rg 72 400 300 200 re f"),
            "<< /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Contents 6 0 R /Resources << /Font << /F1 7 0 R >> >> >>",
            Stream("BT /F1 36 Tf 72 700 Td (Page two) Tj ET 1 0 0 rg 100 300 200 200 re f"),
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>",
        };
        static string Stream(string s) => $"<< /Length {s.Length} >>\nstream\n{s}\nendstream";
        var sb = new StringBuilder("%PDF-1.4\n");
        var offsets = new List<int>();
        for (int i = 0; i < objs.Count; i++)
        {
            offsets.Add(Encoding.ASCII.GetByteCount(sb.ToString()));
            sb.Append($"{i + 1} 0 obj\n{objs[i]}\nendobj\n");
        }
        int xref = Encoding.ASCII.GetByteCount(sb.ToString());
        sb.Append($"xref\n0 {objs.Count + 1}\n0000000000 65535 f \n");
        foreach (var o in offsets) sb.Append($"{o:D10} 00000 n \n");
        sb.Append($"trailer\n<< /Size {objs.Count + 1} /Root 1 0 R >>\nstartxref\n{xref}\n%%EOF\n");
        File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
    }

    private static void Zip(string path, params (string Name, string Content)[] entries)
    {
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (n, c) in entries)
        {
            var e = z.CreateEntry(n);
            using var w = new StreamWriter(e.Open(), new UTF8Encoding(false));
            w.Write(c);
        }
    }

    private static void WriteDocx(string path)
    {
        const string W = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
        Zip(path,
            ("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/word/document.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.wordprocessingml.document.main+xml\"/></Types>"),
            ("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>"),
            ("word/document.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?><w:document xmlns:w=\"{W}\"><w:body>" +
                "<w:p><w:pPr><w:pStyle w:val=\"Title\"/></w:pPr><w:r><w:t>Quarterly report</w:t></w:r></w:p>" +
                "<w:p><w:pPr><w:pStyle w:val=\"Heading1\"/></w:pPr><w:r><w:t>Summary</w:t></w:r></w:p>" +
                "<w:p><w:r><w:t xml:space=\"preserve\">Revenue grew </w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>12%</w:t></w:r><w:r><w:t xml:space=\"preserve\"> this quarter.</w:t></w:r></w:p>" +
                "<w:p><w:pPr><w:numPr><w:ilvl w:val=\"0\"/><w:numId w:val=\"1\"/></w:numPr></w:pPr><w:r><w:t>First bullet</w:t></w:r></w:p>" +
                "<w:p><w:r><w:t>Tab</w:t></w:r><w:r><w:tab/><w:t>separated</w:t></w:r></w:p>" +
                "</w:body></w:document>"));
    }

    private static void WriteXlsx(string path)
    {
        const string S = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
        Zip(path,
            ("[Content_Types].xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"rels\" ContentType=\"application/vnd.openxmlformats-package.relationships+xml\"/><Default Extension=\"xml\" ContentType=\"application/xml\"/><Override PartName=\"/xl/workbook.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sheet.main+xml\"/><Override PartName=\"/xl/worksheets/sheet1.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.worksheet+xml\"/><Override PartName=\"/xl/sharedStrings.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.sharedStrings+xml\"/><Override PartName=\"/xl/styles.xml\" ContentType=\"application/vnd.openxmlformats-officedocument.spreadsheetml.styles+xml\"/></Types>"),
            ("_rels/.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/officeDocument\" Target=\"xl/workbook.xml\"/></Relationships>"),
            ("xl/workbook.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><workbook xmlns=\"{S}\" xmlns:r=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships\"><sheets><sheet name=\"Budget\" sheetId=\"1\" r:id=\"rId1\"/><sheet name=\"Other\" sheetId=\"2\" r:id=\"rId9\"/></sheets></workbook>"),
            ("xl/_rels/workbook.xml.rels", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"rId1\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/worksheet\" Target=\"worksheets/sheet1.xml\"/><Relationship Id=\"rId2\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/sharedStrings\" Target=\"sharedStrings.xml\"/><Relationship Id=\"rId3\" Type=\"http://schemas.openxmlformats.org/officeDocument/2006/relationships/styles\" Target=\"styles.xml\"/></Relationships>"),
            ("xl/sharedStrings.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><sst xmlns=\"{S}\" count=\"5\" uniqueCount=\"5\"><si><t>Item</t></si><si><t>Amount</t></si><si><t>Rent</t></si><si><r><t>Rich </t></r><r><rPr><b/></rPr><t>Food</t></r></si><si><t>Date</t></si></sst>"),
            ("xl/styles.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><styleSheet xmlns=\"{S}\"><cellXfs count=\"2\"><xf numFmtId=\"0\"/><xf numFmtId=\"14\" applyNumberFormat=\"1\"/></cellXfs></styleSheet>"),
            ("xl/worksheets/sheet1.xml", $"<?xml version=\"1.0\" encoding=\"UTF-8\"?><worksheet xmlns=\"{S}\"><sheetData>" +
                "<row r=\"1\"><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\" t=\"s\"><v>1</v></c><c r=\"C1\" t=\"s\"><v>4</v></c></row>" +
                "<row r=\"2\"><c r=\"A2\" t=\"s\"><v>2</v></c><c r=\"B2\"><v>1200.5</v></c><c r=\"C2\" s=\"1\"><v>45658</v></c></row>" +
                "<row r=\"3\"><c r=\"A3\" t=\"s\"><v>3</v></c><c r=\"B3\"><f>B2/4</f><v>300.125</v></c><c r=\"D3\" t=\"inlineStr\"><is><t>inline</t></is></c></row>" +
                "<row r=\"5\"><c r=\"A5\" t=\"b\"><v>1</v></c></row>" +
                "</sheetData></worksheet>"));
    }

    private static void WritePptx(string path)
    {
        const string A = "http://schemas.openxmlformats.org/drawingml/2006/main";
        const string P = "http://schemas.openxmlformats.org/presentationml/2006/main";
        Zip(path,
            ("[Content_Types].xml", "<?xml version=\"1.0\"?><Types xmlns=\"http://schemas.openxmlformats.org/package/2006/content-types\"><Default Extension=\"xml\" ContentType=\"application/xml\"/></Types>"),
            ("ppt/slides/slide1.xml", $"<p:sld xmlns:p=\"{P}\" xmlns:a=\"{A}\"><p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>Welcome</a:t></a:r></a:p><a:p><a:r><a:t>to Anything</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>"),
            ("ppt/slides/slide2.xml", $"<p:sld xmlns:p=\"{P}\" xmlns:a=\"{A}\"><p:cSld><p:spTree><p:sp><p:txBody><a:p><a:r><a:t>Second slide</a:t></a:r></a:p></p:txBody></p:sp></p:spTree></p:cSld></p:sld>"));
    }

    private static void WriteOdt(string path)
    {
        Zip(path,
            ("mimetype", "application/vnd.oasis.opendocument.text"),
            ("content.xml", "<?xml version=\"1.0\" encoding=\"UTF-8\"?><office:document-content xmlns:office=\"urn:oasis:names:tc:opendocument:xmlns:office:1.0\" xmlns:text=\"urn:oasis:names:tc:opendocument:xmlns:text:1.0\"><office:body><office:text><text:h text:outline-level=\"1\">ODT heading</text:h><text:p>Paragraph<text:s text:c=\"3\"/>with spaces.</text:p></office:text></office:body></office:document-content>"));
    }

    private static void WriteZip(string path) =>
        Zip(path, ("docs/readme.md", "# Readme\n"), ("src/main.c", "int main(){return 0;}\n"), ("data/values.csv", "a,b\n1,2\n"));

    private static void WriteTarGz(string path)
    {
        using var fs = File.Create(path);
        using var gz = new GZipStream(fs, CompressionLevel.Fastest);
        using var tw = new TarWriter(gz, TarEntryFormat.Pax);
        foreach (var (n, c) in new[] { ("pkg/a.txt", "alpha"), ("pkg/b/b.txt", "bravo bravo") })
        {
            var e = new PaxTarEntry(TarEntryType.RegularFile, n) { DataStream = new MemoryStream(Encoding.UTF8.GetBytes(c)) };
            tw.WriteEntry(e);
        }
    }

    private static void WriteDxf(string path)
    {
        var sb = new StringBuilder();
        void G(int code, object v) => sb.Append(code).Append('\n').Append(Convert.ToString(v, System.Globalization.CultureInfo.InvariantCulture)).Append('\n');
        G(0, "SECTION"); G(2, "HEADER"); G(9, "$ACADVER"); G(1, "AC1015"); G(0, "ENDSEC");
        G(0, "SECTION"); G(2, "TABLES"); G(0, "TABLE"); G(2, "LAYER"); G(0, "LAYER"); G(2, "WALLS"); G(70, 0); G(62, 3); G(6, "CONTINUOUS"); G(0, "ENDTAB"); G(0, "ENDSEC");
        G(0, "SECTION"); G(2, "BLOCKS");
        G(0, "BLOCK"); G(8, "0"); G(2, "DOOR"); G(70, 0); G(10, 0.0); G(20, 0.0);
        G(0, "LINE"); G(8, "0"); G(62, 1); G(10, 0); G(20, 0); G(11, 10); G(21, 0);
        G(0, "ARC"); G(8, "0"); G(62, 1); G(10, 0); G(20, 0); G(40, 10); G(50, 0); G(51, 90);
        G(0, "ENDBLK");
        G(0, "ENDSEC");
        G(0, "SECTION"); G(2, "ENTITIES");
        G(0, "LWPOLYLINE"); G(8, "WALLS"); G(90, 4); G(70, 1); G(10, 0); G(20, 0); G(10, 100); G(20, 0); G(42, 0.4); G(10, 100); G(20, 60); G(10, 0); G(20, 60);
        G(0, "CIRCLE"); G(8, "0"); G(62, 5); G(10, 50); G(20, 30); G(40, 15);
        G(0, "LINE"); G(8, "0"); G(62, 2); G(10, 0); G(20, 0); G(11, 100); G(21, 60);
        G(0, "TEXT"); G(8, "0"); G(62, 7); G(10, 5); G(20, 65); G(40, 6); G(1, "Floor plan");
        G(0, "INSERT"); G(8, "0"); G(2, "DOOR"); G(10, 20); G(20, 0); G(41, 1); G(42, 1); G(50, 0);
        G(0, "INSERT"); G(8, "0"); G(2, "DOOR"); G(10, 80); G(20, 60); G(41, 1); G(42, 1); G(50, 180);
        G(0, "ELLIPSE"); G(8, "0"); G(62, 6); G(10, 50); G(20, 30); G(11, 30); G(21, 0); G(40, 0.5); G(41, 0); G(42, 6.283185307);
        G(0, "HATCH"); G(8, "0"); G(62, 4); G(2, "SOLID"); G(70, 1); G(71, 0); G(91, 1); G(92, 2); G(72, 0); G(73, 1); G(93, 3);
        G(10, 70); G(20, 10); G(10, 90); G(20, 10); G(10, 80); G(20, 25); G(97, 0); G(75, 0); G(76, 1); G(98, 0);
        G(0, "ENDSEC"); G(0, "EOF");
        File.WriteAllText(path, sb.ToString(), Encoding.ASCII);
    }

    private static byte[] Dib(int w, int h)
    {
        using var b = Drawing(w, h, alpha: false);
        using var ms = new MemoryStream();
        b.Save(ms, ImageFormat.Bmp);
        return ms.ToArray()[14..]; // strip BITMAPFILEHEADER
    }

    private static void WriteDwg(string path)
    {
        var dib = Dib(180, 120);
        var sentinel = new byte[] { 0x1F, 0x25, 0x6D, 0x07, 0xD4, 0x36, 0x28, 0x28, 0x9D, 0x57, 0xCA, 0x3F, 0x9D, 0x44, 0x10, 0x2B };
        using var ms = new MemoryStream();
        var head = new byte[0x80];
        Encoding.ASCII.GetBytes("AC1015").CopyTo(head, 0);
        int imageAt = 0x80;
        BitConverter.GetBytes(imageAt).CopyTo(head, 0x0D);
        ms.Write(head);
        ms.Write(sentinel);
        int dirLen = 4 + 1 + 9 * 2;
        int headerData = imageAt + 16 + dirLen, bmpAt = headerData + 80;
        ms.Write(BitConverter.GetBytes(dirLen + 80 + dib.Length));
        ms.WriteByte(2);
        ms.WriteByte(1); ms.Write(BitConverter.GetBytes(headerData)); ms.Write(BitConverter.GetBytes(80));
        ms.WriteByte(2); ms.Write(BitConverter.GetBytes(bmpAt)); ms.Write(BitConverter.GetBytes(dib.Length));
        ms.Write(new byte[80]);
        ms.Write(dib);
        ms.Write(new byte[256]);
        File.WriteAllBytes(path, ms.ToArray());
    }

    private static void WriteCdrRiff(string path)
    {
        var dib = Dib(128, 96);
        using var ms = new MemoryStream();
        ms.Write("RIFF"u8);
        ms.Write(BitConverter.GetBytes(4 + 8 + 4 + dib.Length + 8 + 4));
        ms.Write("CDR9"u8);
        ms.Write("vrsn"u8); ms.Write(BitConverter.GetBytes(2)); ms.Write(new byte[] { 0x84, 0x03 });
        ms.Write("DISP"u8); ms.Write(BitConverter.GetBytes(4 + dib.Length)); ms.Write(new byte[4]); ms.Write(dib);
        if ((dib.Length & 1) == 1) ms.WriteByte(0);
        File.WriteAllBytes(path, ms.ToArray());
    }

    private static void WriteCdrZip(string path)
    {
        using var z = ZipFile.Open(path, ZipArchiveMode.Create);
        var e = z.CreateEntry("previews/thumbnail.png");
        using (var s = e.Open())
        using (var b = Drawing(256, 180, alpha: true)) b.Save(s, ImageFormat.Png);
        var c = z.CreateEntry("content/root.dat");
        using (var s = c.Open()) s.Write(new byte[64]);
    }

    private static void WriteEps(string path)
    {
        string ps = "%!PS-Adobe-3.0 EPSF-3.0\n%%BoundingBox: 0 0 200 100\nnewpath 10 10 moveto 190 90 lineto stroke\nshowpage\n%%EOF\n";
        byte[] psb = Encoding.ASCII.GetBytes(ps);
        byte[] tif;
        using (var b = Drawing(200, 100, alpha: false))
        using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Tiff); tif = ms.ToArray(); }
        using var o = new MemoryStream();
        o.Write(new byte[] { 0xC5, 0xD0, 0xD3, 0xC6 });
        o.Write(BitConverter.GetBytes(30)); o.Write(BitConverter.GetBytes(psb.Length));
        o.Write(BitConverter.GetBytes(0)); o.Write(BitConverter.GetBytes(0));
        o.Write(BitConverter.GetBytes(30 + psb.Length)); o.Write(BitConverter.GetBytes(tif.Length));
        o.Write(new byte[] { 0xFF, 0xFF });
        o.Write(psb); o.Write(tif);
        File.WriteAllBytes(path, o.ToArray());
    }

    private static void WriteIndd(string path)
    {
        byte[] jpg;
        using (var b = Drawing(240, 320, alpha: false))
        using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Jpeg); jpg = ms.ToArray(); }
        string b64 = Convert.ToBase64String(jpg);
        // XMP wraps base64 at 76 chars with &#xA; entities, like InDesign does.
        var wrapped = new StringBuilder();
        for (int i = 0; i < b64.Length; i += 76) wrapped.Append(b64, i, Math.Min(76, b64.Length - i)).Append("&#xA;");
        string xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF><rdf:Description xmlns:xmpGImg=\"http://ns.adobe.com/xap/1.0/g/img/\">" +
                     "<xmp:PageInfo><rdf:Seq><rdf:li><xmpGImg:format>JPEG</xmpGImg:format><xmpGImg:image>" + wrapped +
                     "</xmpGImg:image></rdf:li></rdf:Seq></xmp:PageInfo></rdf:Description></rdf:RDF></x:xmpmeta>";
        using var o = new MemoryStream();
        o.Write(new byte[] { 0x06, 0x06, 0xED, 0xF5, 0xD8, 0x1D, 0x46, 0xE5, 0xBD, 0x31, 0xEF, 0xE7, 0xFE, 0x74, 0xB7, 0x1D });
        o.Write(Encoding.ASCII.GetBytes("DOCUMENT"));
        o.Write(new byte[4096]);
        o.Write(Encoding.UTF8.GetBytes(xmp));
        o.Write(new byte[1024]);
        File.WriteAllBytes(path, o.ToArray());
    }

    private static void WriteWav(string path)
    {
        int rate = 22050, n = rate;
        using var w = new BinaryWriter(File.Create(path));
        w.Write("RIFF"u8); w.Write(36 + n * 2); w.Write("WAVE"u8);
        w.Write("fmt "u8); w.Write(16); w.Write((short)1); w.Write((short)1); w.Write(rate); w.Write(rate * 2); w.Write((short)2); w.Write((short)16);
        w.Write("data"u8); w.Write(n * 2);
        for (int i = 0; i < n; i++) w.Write((short)(Math.Sin(i * 2 * Math.PI * 440 / rate) * 3000));
    }
}
