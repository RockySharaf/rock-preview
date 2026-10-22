using System.IO.Compression;
using System.Text;
using Anything.Core.Content;
using Anything.Core.Content.Extract;
using static Anything.Tests.Content.Readers.OfficeFixtures;

namespace Anything.Tests.Content.Readers;

public class OfficeReadersTests
{
    [Fact]
    public void Docx_reads_body_parts_and_properties_and_skips_what_is_not_text()
    {
        var doc = ReaderRun.Bytes(Docx(), "docx");
        Assert.Equal(DocStatus.Ok, doc.Status);
        var lines = doc.Body.Split('\n');
        Assert.Equal("Invoice INV-2024-001\ttotal", lines[0]); // preserved space kept, tab-stop definitions not text
        Assert.Equal("first", lines[1]);
        Assert.Equal("second", lines[2]);
        Assert.DoesNotContain("removed", doc.Body);
        Assert.DoesNotContain("fieldcode", doc.Body);
        Assert.Contains("shown link", doc.Body);
        Assert.Single(System.Text.RegularExpressions.Regex.Matches(doc.Body, "boxed text")); // mc:Fallback skipped
        Assert.Contains("cdata words", doc.Body);
        foreach (var part in new[] { "Confidential header", "Footer line", "Footnote source", "Reviewer remark" }) Assert.Contains(part, doc.Body);
        Assert.True(doc.Body.IndexOf("Invoice", StringComparison.Ordinal) < doc.Body.IndexOf("Confidential header", StringComparison.Ordinal));
        Assert.Equal("Quarterly Report", doc.Field(DocField.Title));
        Assert.Equal("Budget planning", doc.Field(DocField.Subject));
        Assert.Equal("Rocky Writer", doc.Field(DocField.Author));
        Assert.Equal("finance; quarterly; forecast", doc.Field(DocField.Keywords));
        Assert.Equal("Internal draft", doc.Field(DocField.Comment));
        Assert.Equal(7, doc.Pages);
        Assert.Equal(1, doc.ExtractorVersion);
    }

    [Fact]
    public void Docx_strict_namespaces_read_like_transitional()
    {
        const string strict = "http://purl.oclc.org/ooxml/wordprocessingml/main";
        var body = $"<w:document xmlns:w=\"{strict}\"><w:body><w:p><w:r><w:t>strict words</w:t></w:r></w:p></w:body></w:document>";
        var bytes = Zip(false,
            ("_rels/.rels", "<Relationships xmlns=\"http://schemas.openxmlformats.org/package/2006/relationships\"><Relationship Id=\"r1\" " +
                "Type=\"http://purl.oclc.org/ooxml/officeDocument/relationships/officeDocument\" Target=\"word/document.xml\"/></Relationships>"),
            ("word/document.xml", body));
        var doc = ReaderRun.Bytes(bytes, "docx");
        Assert.Equal(DocStatus.Ok, doc.Status);
        Assert.Equal("strict words", doc.Body.Trim());
    }

    [Fact]
    public void Xlsx_reads_sheets_in_workbook_order_with_shared_strings_decoded()
    {
        var doc = ReaderRun.Bytes(Xlsx(), "xlsx");
        Assert.Equal(DocStatus.Ok, doc.Status);
        var pages = doc.Body.Split('\f');
        Assert.Equal(2, pages.Length);
        Assert.Equal(2, doc.Pages);
        var first = pages[0].Split('\n');
        Assert.Equal("Budget 2024", first[0]);
        Assert.Equal("Revenue\t1250.5\t東京", first[1]); // phonetic reading left out
        // _x000D_ is a carriage return, stored as a line end inside the cell's text.
        Assert.Equal("inline words\tformula result\tTRUE\tline", first[2]);
        Assert.Equal("break\trich run", first[3]);
        Assert.DoesNotContain("トウキョウ", doc.Body);
        Assert.DoesNotContain("CONCAT", doc.Body);
        Assert.DoesNotContain("Header code", doc.Body);
        Assert.StartsWith("Notes\nhidden sheet words", pages[1]);
        Assert.Equal("Budget Book", doc.Field(DocField.Title));
    }

    [Fact]
    public void Pptx_reads_slides_in_list_order_with_notes()
    {
        var doc = ReaderRun.Bytes(Pptx(), "pptx");
        var slides = doc.Body.Split('\f');
        Assert.Equal(2, slides.Length);
        Assert.Contains("Opening slide title", slides[0]);
        Assert.Contains("agenda item", slides[0]);
        Assert.Contains("speaker notes here", slides[0]);
        Assert.Contains("Closing thanks", slides[1]);
        Assert.DoesNotContain("\t", doc.Body); // a:tab in a tab list is a tab stop, not text
        Assert.Equal(2, doc.Pages);
        Assert.Equal("Pitch Deck", doc.Field(DocField.Title));
    }

    [Fact]
    public void Encrypted_package_is_password_protected()
    {
        var file = CfbFixtures.Ole(root =>
        {
            CfbFixtures.Stream(root, "EncryptionInfo", new byte[64]);
            CfbFixtures.Stream(root, "EncryptedPackage", new byte[5000]);
        });
        Assert.Equal(DocStatus.PasswordProtected, ReaderRun.Bytes(file, "docx").Status);
        var legacy = CfbFixtures.Ole(root => CfbFixtures.Stream(root, "WordDocument", new byte[600]));
        Assert.Equal(DocStatus.Unsupported, ReaderRun.Bytes(legacy, "xlsx").Status);
    }

    [Fact]
    public void Dtd_is_refused_and_the_part_skipped()
    {
        var body = "<?xml version=\"1.0\"?><!DOCTYPE w [<!ENTITY a \"aaaaaaaaaa\"><!ENTITY b \"&a;&a;&a;&a;&a;&a;&a;&a;&a;&a;\">]>" +
            $"<w:document xmlns:w=\"{W}\"><w:body><w:p><w:r><w:t>&b;</w:t></w:r></w:p></w:body></w:document>";
        var doc = ReaderRun.Bytes(Docx(body: body), "docx");
        Assert.DoesNotContain("aaaa", doc.Body);
        Assert.Equal(DocStatus.Truncated, doc.Status); // the other parts were read, the document part skipped
        Assert.Contains("Confidential header", doc.Body);
    }

    [Fact]
    public void Zip_guard_refuses_crafted_directories_before_ZipArchive_sees_them()
    {
        Assert.Equal(DocStatus.TooBig, ZipGuard.Check(new MemoryByteSource(CraftedDirectory(10_001), Budget())));
        Assert.Null(ZipGuard.Check(new MemoryByteSource(CraftedDirectory(100), Budget())));
        var crafted = CraftedDirectory(60_001);
        long before = GC.GetAllocatedBytesForCurrentThread();
        var doc = ReaderRun.Bytes(crafted, "docx");
        Assert.Equal(DocStatus.TooBig, doc.Status);
        Assert.True(GC.GetAllocatedBytesForCurrentThread() - before < 8 << 20);

        // A directory that claims to lie outside the file, and one larger than 4 MB.
        var outside = CraftedDirectory(3);
        BitConverter.GetBytes(0x7FFF0000u).CopyTo(outside, outside.Length - 6);
        Assert.Equal(DocStatus.Damaged, ZipGuard.Check(new MemoryByteSource(outside, Budget())));
        var huge = CraftedDirectory(3);
        BitConverter.GetBytes(5u << 20).CopyTo(huge, huge.Length - 10);
        Assert.Equal(DocStatus.TooBig, ZipGuard.Check(new MemoryByteSource(huge, Budget())));
        Assert.Equal(DocStatus.Damaged, ZipGuard.Check(new MemoryByteSource(new byte[100], Budget())));
    }

    [Fact]
    public void Zip64_end_records_are_followed()
    {
        // ZipArchive writes Zip64 records only for huge archives, so the records are written by hand around a small directory.
        var zip = Zip(false, ("a.txt", "x"));
        int end = zip.Length - 22;
        uint cdSize = BitConverter.ToUInt32(zip, end + 12), cdOffset = BitConverter.ToUInt32(zip, end + 16);
        var ms = new MemoryStream();
        ms.Write(zip, 0, end);
        long zip64At = ms.Position;
        var w = new BinaryWriter(ms);
        w.Write(0x06064B50u); w.Write(44UL); w.Write((ushort)45); w.Write((ushort)45); w.Write(0u); w.Write(0u);
        w.Write(1UL); w.Write(1UL); w.Write((ulong)cdSize); w.Write((ulong)cdOffset);
        w.Write(0x07064B50u); w.Write(0u); w.Write((ulong)zip64At); w.Write(1u);
        w.Write(0x06054B50u); w.Write((ushort)0xFFFF); w.Write((ushort)0xFFFF); w.Write((ushort)0xFFFF); w.Write((ushort)0xFFFF);
        w.Write(0xFFFFFFFFu); w.Write(0xFFFFFFFFu); w.Write((ushort)0);
        Assert.Null(ZipGuard.Check(new MemoryByteSource(ms.ToArray(), Budget()), out long entries));
        Assert.Equal(1, entries);
        // A Zip64 record that claims 20,000 entries (on this disk and in all).
        var bytes = ms.ToArray();
        BitConverter.GetBytes(20_000UL).CopyTo(bytes, zip64At + 24);
        BitConverter.GetBytes(20_000UL).CopyTo(bytes, zip64At + 32);
        Assert.Equal(DocStatus.TooBig, ZipGuard.Check(new MemoryByteSource(bytes, Budget())));
        // Counts that differ between this disk and all disks are a split archive, which ZipArchive refuses as well.
        BitConverter.GetBytes(1UL).CopyTo(bytes, zip64At + 24);
        Assert.Equal(DocStatus.Damaged, ZipGuard.Check(new MemoryByteSource(bytes, Budget())));
        // An end record whose own count isn't masked must pass too, even when ZipArchive takes the Zip64 one.
        var both = ms.ToArray();
        BitConverter.GetBytes((ushort)20_000).CopyTo(both, both.Length - 14);
        BitConverter.GetBytes((ushort)20_000).CopyTo(both, both.Length - 12);
        Assert.Equal(DocStatus.TooBig, ZipGuard.Check(new MemoryByteSource(both, Budget())));
    }

    [Fact]
    public void Zip_bomb_entry_stops_at_its_cap_and_keeps_what_was_read()
    {
        // 8 MB of one repeated paragraph deflates to almost nothing: past 1 MB at over 100:1 the entry stops.
        var sb = new StringBuilder($"<w:document xmlns:w=\"{W}\"><w:body>");
        var para = "<w:p><w:r><w:t>bomb words</w:t></w:r></w:p>";
        sb.Insert(sb.Length, para, 8 * 1024 * 1024 / para.Length);
        sb.Append("</w:body></w:document>");
        var doc = ReaderRun.Bytes(Docx(body: sb.ToString()), "docx");
        Assert.Equal(DocStatus.Truncated, doc.Status);
        Assert.Contains("bomb words", doc.Body);
        Assert.Contains("Confidential header", doc.Body); // later parts are still read
    }

    [Fact]
    public void Odt_reads_text_with_bounded_spaces_and_no_deleted_text()
    {
        var doc = ReaderRun.Bytes(Odt(), "odt");
        Assert.Equal(DocStatus.Ok, doc.Status);
        var lines = doc.Body.Split('\n');
        Assert.Equal("Project Plan", lines[0]);
        Assert.Equal("Words apart\ttabbed", lines[1]); // text:c of 2^31-1 is one space
        Assert.StartsWith("next line spanned", lines[2]);
        Assert.Contains("note body text", doc.Body);
        Assert.DoesNotContain("deleted", doc.Body);
        Assert.Equal("Plan Title", doc.Field(DocField.Title));
        Assert.Equal("First Author", doc.Field(DocField.Author));
        Assert.Equal("alpha; beta", doc.Field(DocField.Keywords));
        Assert.Equal("Odf comment", doc.Field(DocField.Comment));
        Assert.Equal(3, doc.Pages);
        Assert.True(doc.Body.Length < 200);
    }

    [Fact]
    public void Ods_never_expands_repeats_so_the_second_sheet_is_read()
    {
        var doc = ReaderRun.Bytes(Ods(), "ods");
        Assert.Equal(DocStatus.Ok, doc.Status);
        var pages = doc.Body.Split('\f');
        Assert.Equal(2, pages.Length);
        Assert.Equal("Sales\nRegion\tsame\n", pages[0]);
        Assert.Equal("Second\nsecond sheet text\n", pages[1]);
        Assert.Equal(2, doc.Pages);
    }

    [Fact]
    public void Odp_reads_pages_and_notes()
    {
        var doc = ReaderRun.Bytes(Odp(), "odp");
        var pages = doc.Body.Split('\f');
        Assert.Equal(2, pages.Length);
        Assert.Contains("First page words", pages[0]);
        Assert.Contains("odp speaker notes", pages[0]);
        Assert.Contains("Second page words", pages[1]);
        Assert.Equal(2, doc.Pages);
    }

    [Fact]
    public void Odf_encryption_is_password_protected()
        => Assert.Equal(DocStatus.PasswordProtected, ReaderRun.Bytes(Odt(encrypted: true), "odt").Status);

    [Fact]
    public void Idml_reads_stories_in_designmap_order_and_its_xmp()
    {
        var doc = ReaderRun.Bytes(Idml(), "idml");
        Assert.Equal(DocStatus.Ok, doc.Status);
        Assert.Equal(ContentKind.Design, doc.Kind);
        var lines = doc.Body.Split('\n');
        Assert.Equal(["Headline story", "Second story", "more text"], lines);
        Assert.Equal("Brochure Layout", doc.Field(DocField.Title));
        Assert.Equal("brochure; client", doc.Field(DocField.Keywords));
        Assert.Equal(12, doc.Pages);
    }

    [Fact]
    public void A_damaged_entry_skips_only_that_part()
    {
        var bytes = Docx(stored: true);
        // Break the footnotes part's XML in the stored zip.
        int at = IndexOf(bytes, Encoding.UTF8.GetBytes("<w:footnote "));
        bytes[at + 1] = (byte)'!';
        var doc = ReaderRun.Bytes(bytes, "docx");
        Assert.Equal(DocStatus.Truncated, doc.Status);
        Assert.Contains("Invoice", doc.Body);
        Assert.DoesNotContain("Footnote source", doc.Body);
        Assert.Contains("Reviewer remark", doc.Body);
    }

    [Fact]
    public void Media_entries_are_never_read()
    {
        var ms = new MemoryStream();
        ms.Write(Docx());
        var big = new byte[2 << 20];
        new Random(5).NextBytes(big);
        using (var zip = new ZipArchive(ms, ZipArchiveMode.Update, leaveOpen: true))
        {
            var e = zip.CreateEntry("word/media/image1.png", CompressionLevel.NoCompression);
            using var s = e.Open();
            s.Write(big);
        }
        var bytes = ms.ToArray();
        var (doc, source) = ReaderRun.Counted(bytes, "docx");
        Assert.Equal(DocStatus.Ok, doc.Status);
        Assert.True(source.BytesRead < 200_000, $"read {source.BytesRead} bytes of {bytes.Length}");
    }

    internal static int IndexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);

    static ExtractBudget Budget() => new(ExtractLimits.Local, CancellationToken.None);
}
