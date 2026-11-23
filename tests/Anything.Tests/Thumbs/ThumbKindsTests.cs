using Anything.Core.Thumbs;

namespace Anything.Tests.Thumbs;

public sealed class ThumbKindsTests
{
    [Theory]
    [InlineData("jpg", (int)ThumbRoute.Picture)]
    [InlineData(".HEIC", (int)ThumbRoute.Picture)]
    [InlineData("cr3", (int)ThumbRoute.Picture)]
    [InlineData("ico", (int)ThumbRoute.Picture)]
    [InlineData("psb", (int)ThumbRoute.Psd)]
    [InlineData("pdf", (int)ThumbRoute.OwnExtractor)]
    [InlineData("docx", (int)ThumbRoute.OwnExtractor)]
    [InlineData("ttf", (int)ThumbRoute.OwnExtractor)]
    [InlineData("cs", (int)ThumbRoute.OwnExtractor)]
    [InlineData("cdr", (int)ThumbRoute.OwnExtractor)]
    [InlineData("dwg", (int)ThumbRoute.OwnExtractor)]
    [InlineData("tga", (int)ThumbRoute.OwnExtractor)]
    [InlineData("emf", (int)ThumbRoute.OwnExtractor)]
    [InlineData("blend", (int)ThumbRoute.OwnExtractor)]
    [InlineData("afphoto", (int)ThumbRoute.OwnExtractor)]
    [InlineData("mp4", (int)ThumbRoute.Shell)]
    [InlineData("mkv", (int)ThumbRoute.Shell)]
    [InlineData("doc", (int)ThumbRoute.OwnExtractor)] // Windows first (ShellFirst), then Rock Preview's page
    [InlineData("XLS", (int)ThumbRoute.OwnExtractor)]
    [InlineData("ppt", (int)ThumbRoute.Shell)]
    [InlineData("pps", (int)ThumbRoute.Shell)]
    [InlineData("zip", (int)ThumbRoute.Shell)]
    [InlineData("", (int)ThumbRoute.Shell)]
    public void Each_kind_takes_its_route(string ext, int route) => Assert.Equal((ThumbRoute)route, ThumbKinds.RouteOf(ext));

    [Fact]
    public void Paths_and_folders()
    {
        Assert.Equal(ThumbRoute.Picture, ThumbKinds.RouteOf(@"C:\Photos\Holiday.Beach.JPG"));
        Assert.Equal(ThumbRoute.Shell, ThumbKinds.RouteOf(@"C:\Photos\README"));
        Assert.Equal(ThumbRoute.Shell, ThumbKinds.RouteOf(@"C:\Photos.jpg", isFolder: true));
        Assert.Equal("jpg", ThumbKinds.Ext(@"C:\a.b\photo.JPG"));
        Assert.Equal("", ThumbKinds.Ext(@"C:\a.b\photo"));
        Assert.Equal("png", ThumbKinds.Ext(".PNG"));
    }

    [Fact]
    public void Embedded_previews_never_for_heic()
    {
        Assert.Equal(EmbeddedPreview.Exif, ThumbKinds.EmbeddedPreviewOf("jpeg"));
        Assert.Equal(EmbeddedPreview.PsdResource, ThumbKinds.EmbeddedPreviewOf("psd"));
        Assert.Equal(EmbeddedPreview.Raw, ThumbKinds.EmbeddedPreviewOf("nef"));
        Assert.Equal(EmbeddedPreview.None, ThumbKinds.EmbeddedPreviewOf("heic"));
        Assert.Equal(EmbeddedPreview.None, ThumbKinds.EmbeddedPreviewOf("png"));
    }

    [Fact]
    public void Windows_cache_is_asked_for_its_own_kinds_and_for_big_files()
    {
        Assert.True(ThumbKinds.AskWindowsCache("mp4", 10));
        Assert.True(ThumbKinds.AskWindowsCache("", 0, isFolder: true));
        Assert.False(ThumbKinds.AskWindowsCache("jpg", 1536 * 1024 - 1));
        Assert.True(ThumbKinds.AskWindowsCache("jpg", 1536 * 1024));
        Assert.False(ThumbKinds.AskWindowsCache("pdf", 0));
        Assert.True(ThumbKinds.AskWindowsCache("pdf", 5_000_000));
    }

    [Fact]
    public void Flat_kinds_and_transparency_are_stored_as_png()
    {
        Assert.Equal(ThumbEncoding.Jpeg85, ThumbKinds.EncodingFor("jpg", hasAlpha: false, 256));
        Assert.Equal(ThumbEncoding.Jpeg85, ThumbKinds.EncodingFor("mp4", hasAlpha: false, 512));
        Assert.Equal(ThumbEncoding.Png, ThumbKinds.EncodingFor("jpg", hasAlpha: true, 256));
        foreach (var flat in new[] { "pdf", "docx", "txt", "ttf", "png", "bmp", "gif", "ico", "dxf", "emf" })
            Assert.Equal(ThumbEncoding.Png, ThumbKinds.EncodingFor(flat, hasAlpha: false, 256));
        // A PNG that came out too big becomes JPEG 90, unless it needs its transparency.
        Assert.Equal(ThumbEncoding.Png, ThumbKinds.EncodingFor("pdf", false, 256, pngBytes: 48 * 1024));
        Assert.Equal(ThumbEncoding.Jpeg90, ThumbKinds.EncodingFor("pdf", false, 256, pngBytes: 48 * 1024 + 1));
        Assert.Equal(ThumbEncoding.Png, ThumbKinds.EncodingFor("pdf", false, 512, pngBytes: 100 * 1024));
        Assert.Equal(ThumbEncoding.Jpeg90, ThumbKinds.EncodingFor("pdf", false, 512, pngBytes: 160 * 1024 + 1));
        Assert.Equal(ThumbEncoding.Png, ThumbKinds.EncodingFor("png", true, 256, pngBytes: 1 << 20));
    }

    [Theory]
    [InlineData("jpg", (int)ThumbCost.Photo)]
    [InlineData("HEIC", (int)ThumbCost.Photo)]
    [InlineData("nef", (int)ThumbCost.Photo)]
    [InlineData("psd", (int)ThumbCost.Photo)]
    [InlineData("tga", (int)ThumbCost.Photo)]
    [InlineData("afphoto", (int)ThumbCost.Photo)]
    [InlineData("mp4", (int)ThumbCost.Video)]
    [InlineData("mts", (int)ThumbCost.Video)]
    [InlineData("pdf", (int)ThumbCost.Document)]
    [InlineData("docx", (int)ThumbCost.Document)]
    [InlineData("pptx", (int)ThumbCost.Document)]
    [InlineData("png", (int)ThumbCost.Document)]
    [InlineData("gif", (int)ThumbCost.Document)]
    [InlineData("ttf", (int)ThumbCost.Document)]
    [InlineData("dwg", (int)ThumbCost.Document)]
    [InlineData("txt", (int)ThumbCost.Nothing)]  // text pages are never stored
    [InlineData("cs", (int)ThumbCost.Nothing)]
    [InlineData("svg", (int)ThumbCost.Nothing)]
    [InlineData("xlsx", (int)ThumbCost.Nothing)] // spreadsheets almost never carry a thumbnail
    [InlineData("ods", (int)ThumbCost.Nothing)]
    [InlineData("exe", (int)ThumbCost.Nothing)]
    [InlineData("zip", (int)ThumbCost.Nothing)]
    [InlineData("mp3", (int)ThumbCost.Nothing)]
    [InlineData("doc", (int)ThumbCost.Nothing)]
    [InlineData("", (int)ThumbCost.Nothing)]
    public void Each_kind_has_its_cost(string ext, int cost)
    {
        Assert.Equal((ThumbCost)cost, ThumbKinds.CostOf(ext));
        // The index walk's allocation-free lookup agrees, in any case.
        Assert.Equal((ThumbCost)cost, ThumbKinds.CostOfExtension(ext.ToUpperInvariant()));
    }

    [Theory]
    [InlineData("txt")]
    [InlineData("md")]
    [InlineData("cs")]
    [InlineData("json")]
    [InlineData("html")]
    [InlineData("csv")]
    [InlineData("m3u")]
    [InlineData("cue")]
    [InlineData("desktop")]
    [InlineData(@"C:\Users\me\.ssh\config.yml")]
    public void Text_pages_are_shown_but_never_kept_on_disk_planned_for_idle_or_counted(string kind)
    {
        // Drawn as a page of the file's own text on screen ...
        Assert.True(ThumbKinds.DrawsText(kind), kind);
        Assert.Equal(ThumbRoute.OwnExtractor, ThumbKinds.RouteOf(kind));
        // ... and never stored: neither when shown nor by the idle step, which plans only what may be kept ...
        Assert.False(ThumbKinds.KeepOnDisk(kind), kind);
        // ... and the estimate counts it as nothing, neither in the number of files nor in the bytes.
        Assert.Equal(ThumbCost.Nothing, ThumbKinds.CostOf(kind));
        var census = ThumbEstimator.Count(["page." + ThumbKinds.Ext(kind)]);
        Assert.Equal((0L, 0L), (census.Files, census.BytesAt(256)));
    }

    [Theory]
    [InlineData("rtf")]
    [InlineData("eml")]
    [InlineData("mbox")]
    [InlineData("url")]
    [InlineData("svg")]
    [InlineData("ics")]
    [InlineData("lock")]
    [InlineData("map")]
    [InlineData("ipynb")]
    [InlineData("geojson")]
    [InlineData("kml")]
    [InlineData("gpx")]
    [InlineData("wkt")]
    [InlineData("gcode")]
    [InlineData("nc")]
    [InlineData("obj")]
    [InlineData("ply")]
    [InlineData("mtl")]
    [InlineData("pem")]
    [InlineData("crt")]
    [InlineData("cer")]
    [InlineData("KEY")]
    [InlineData("pub")]
    [InlineData("asc")]
    [InlineData("csr")]
    [InlineData("env")]
    [InlineData("npmrc")]
    [InlineData("vcf")]
    [InlineData("ngc")]
    [InlineData("tap")]
    [InlineData("cnc")]
    [InlineData("stl")]
    [InlineData("gltf")]
    [InlineData("step")]
    [InlineData("STP")]
    [InlineData(@"D:\Work\drawing.SVG")]
    public void Machine_text_and_keys_get_Windows_picture_or_their_icon_never_a_text_page(string kind)
    {
        Assert.True(ThumbKinds.NoTextPage(kind), kind);
        // Asked of Windows like any kind Rock Preview doesn't draw (its cache, then its extractor) ...
        Assert.Equal(ThumbRoute.Shell, ThumbKinds.RouteOf(kind));
        Assert.True(ThumbKinds.AskWindowsCache(kind, 0));
        // ... and no page of its text, so no text turn either.
        Assert.False(ThumbKinds.DrawsText(kind), kind);
        Assert.False(ThumbKinds.DrawsTextWhenMade(kind), kind);
        // What Windows gives stays in memory, as for other kinds only it pictures.
        Assert.False(ThumbKinds.KeepOnDisk(kind), kind);
        Assert.Equal(ThumbCost.Nothing, ThumbKinds.CostOf(kind));
    }

    [Fact]
    public void Text_kinds_with_a_readable_start_keep_their_page()
    {
        foreach (var kind in new[] { "txt", "cs", "m3u", "m3u8", "pls", "cue", "desktop", "html", "" })
            Assert.False(ThumbKinds.NoTextPage(kind), kind);
    }

    [Theory]
    [InlineData(".env")]
    [InlineData(".env.local")]
    [InlineData(".npmrc")]
    [InlineData("id_rsa")]
    [InlineData("id_ed25519")]
    [InlineData("x.ppk")]
    [InlineData("x.gpg")]
    [InlineData("x.ovpn")]
    [InlineData("x.csr")]
    public void Secret_files_never_get_a_text_page_by_name_or_path(string name)
    {
        // By file name and by path, in any case: neither Rock Preview's own page nor the text sniff after Windows' miss.
        foreach (var path in new[] { name, @"D:\Work\" + name, "D:/Work/" + name.ToUpperInvariant() })
        {
            if (path == name && !name.Contains('.')) continue; // a bare word is an extension (below)
            Assert.True(ThumbKinds.NoTextPage(path), path);
            Assert.False(ThumbKinds.DrawsText(path), path);
        }
    }

    [Theory]
    [InlineData("BitLocker Recovery Key 1A2B3C4D.TXT")]
    [InlineData("Chrome Passwords.csv")]
    [InlineData("github-recovery-codes.txt")]
    [InlineData("credentials.json")]
    [InlineData("id_rsa.txt")]
    [InlineData(".env.json")]
    [InlineData("Passwords.docx")]
    [InlineData("Passwords.xlsx")]
    [InlineData("password list.xls")]
    [InlineData("old passwords.doc")]
    public void Secret_names_of_kinds_drawn_as_text_go_to_Windows_not_their_own_page(string name)
    {
        // Rock Preview's own page would show the file's first lines or cells: Windows' picture or the icon instead.
        foreach (var path in new[] { name, @"D:\Work\" + name, "D:/Work/" + name.ToUpperInvariant() })
        {
            Assert.Equal(ThumbRoute.Shell, ThumbKinds.RouteOf(path));
            Assert.True(ThumbKinds.NoTextPage(path), path); // and no text sniff after Windows' miss
        }
    }

    [Fact]
    public void Ordinary_names_of_kinds_drawn_as_text_keep_their_own_page()
    {
        foreach (var name in new[] { "notes.txt", "report.docx", "table.csv", "budget.xlsx", "letter.doc", "sheet.xls" })
            foreach (var path in new[] { name, @"D:\Work\" + name })
                Assert.Equal(ThumbRoute.OwnExtractor, ThumbKinds.RouteOf(path));
        // A picture or a PDF is no page of the file's text, whatever its name; a bare word is an extension only.
        Assert.Equal(ThumbRoute.Picture, ThumbKinds.RouteOf(@"D:\Work\password reset.png"));
        Assert.Equal(ThumbRoute.OwnExtractor, ThumbKinds.RouteOf(@"D:\Work\password policy.pdf"));
        Assert.Equal(ThumbRoute.OwnExtractor, ThumbKinds.RouteOf("txt"));
    }

    [Fact]
    public void A_bare_word_is_only_an_extension_and_ordinary_text_files_keep_their_page()
    {
        // Where only an extension is known, only the list of kinds counts: "id_rsa" and "ppk" are kinds no list names.
        foreach (var kind in new[] { "id_rsa", "ppk", "ovpn", "txt" })
            Assert.False(ThumbKinds.NoTextPage(kind), kind);
        foreach (var name in new[] { "notes.txt", "script.py", "table.csv" })
            foreach (var path in new[] { name, @"D:\Work\" + name })
            {
                Assert.False(ThumbKinds.NoTextPage(path), path);
                Assert.True(ThumbKinds.DrawsText(path), path);
            }
    }

    [Fact]
    public void Pictures_video_frames_and_document_pages_may_be_kept()
    {
        foreach (var kind in new[] { "jpg", "png", "heic", "nef", "psd", "mp4", "pdf", "docx", "pptx", "ttf", "dwg", "eps", "tga" })
        {
            Assert.True(ThumbKinds.KeepOnDisk(kind), kind);
            Assert.False(ThumbKinds.DrawsText(kind), kind);
        }
        Assert.True(ThumbKinds.KeepOnDisk(@"C:\Photos\Holiday.jpg"));
    }

    [Fact]
    public void What_only_Windows_pictures_for_a_kind_the_estimate_counts_as_nothing_stays_in_memory()
    {
        // A music file's cover, a folder's picture, an archive or a name without a kind (README, id_rsa): Windows may
        // picture them, and its own cache keeps that; the store doesn't, since the estimate counts them as nothing.
        foreach (var kind in new[] { "mp3", "flac", "m4a", "zip", "7z", "exe", "dll", "xlsx", "ods", "doc", "xls", "", @"C:\Users\me\.ssh\id_rsa" })
            Assert.False(ThumbKinds.KeepOnDisk(kind), kind);
        Assert.False(ThumbKinds.KeepOnDisk(@"C:\Photos\Holiday.jpg", isFolder: true));
    }

    [Fact]
    public void A_kind_is_kept_on_disk_exactly_when_the_estimate_counts_it()
    {
        // Every extension the tables name (and a few they don't), in both cases: kept if and only if it costs something.
        string[] kinds =
        [
            "jpg", "jpeg", "jpe", "jfif", "png", "apng", "bmp", "dib", "rle", "gif", "tif", "tiff", "webp", "heic", "heif", "hif",
            "avif", "jxr", "wdp", "hdp", "dds", "jxl", "ico", "cur", "cr2", "cr3", "crw", "nef", "nrw", "arw", "srf", "sr2", "dng",
            "orf", "rw2", "raf", "pef", "srw", "x3f", "kdc", "dcr", "mrw", "3fr", "erf", "mef", "mos", "iiq", "rwl", "psd", "psb",
            "cdr", "cdt", "cdrx", "cmx", "des", "dwg", "dwt", "dxf", "ai", "ait", "eps", "epsf", "ps", "indd", "indt", "emf", "wmf",
            "afdesign", "afpub", "pdf", "tga", "icb", "vda", "blend", "afphoto", "docx", "docm", "dotx", "dotm", "xlsx", "xlsm",
            "xltx", "xltm", "pptx", "pptm", "ppsx", "ppsm", "potx", "potm", "odt", "ott", "ods", "ots", "odp", "otp", "odg", "ttf",
            "otf", "ttc", "otc", "woff", "fon", "fnt", "txt", "md", "json", "xml", "cs", "py", "html", "svg", "pem", "key", "eml",
            "ics", "vcf", "csv", "rtf", "mp4", "m4v", "mov", "avi", "mkv", "webm", "wmv", "flv", "f4v", "mpg", "mpeg", "mpe", "m2ts",
            "mts", "m2t", "3gp", "3g2", "ogv", "vob", "asf", "rm", "rmvb", "divx", "mxf", "mp3", "flac", "wav", "m4a", "zip", "exe",
            "lnk", "url", "doc", "xls", "ppt", "dat", "",
        ];
        foreach (var kind in kinds)
            foreach (var k in new[] { kind, kind.ToUpperInvariant() })
            {
                bool counted = ThumbKinds.CostOf(k) != ThumbCost.Nothing;
                Assert.True(counted == ThumbKinds.KeepOnDisk(k), k);
                Assert.True(counted == ThumbKinds.KeepOnDisk(@"C:\Files\a." + k), k);
                // The census counts a file exactly when it is kept.
                Assert.Equal(counted ? 1L : 0L, ThumbEstimator.Count(["a." + k]).Files);
            }
    }

    [Fact]
    public void Pdf_pages_ask_Windows_first()
    {
        Assert.True(ThumbKinds.ShellFirst("pdf"));
        Assert.True(ThumbKinds.ShellFirst(@"C:\Docs\Logo.AI"));
        Assert.True(ThumbKinds.ShellFirst("ttf"));   // fonts too: Windows draws their samples in a helper process
        Assert.True(ThumbKinds.ShellFirst("OTF"));
        Assert.False(ThumbKinds.ShellFirst("docx"));
        Assert.False(ThumbKinds.ShellFirst("jpg"));
        // Only the PDF kinds render a page (one at a time for the process); fonts and text kinds draw text.
        Assert.True(ThumbKinds.RendersPdfPage("ai"));
        Assert.False(ThumbKinds.RendersPdfPage("ttf"));
        Assert.True(ThumbKinds.DrawsTextWhenMade("ttf"));
        Assert.True(ThumbKinds.DrawsTextWhenMade("cs"));
        Assert.True(ThumbKinds.DrawsTextWhenMade("docx"));
        Assert.False(ThumbKinds.DrawsTextWhenMade("pdf"));
        Assert.False(ThumbKinds.DrawsTextWhenMade("jpg"));
        // Still Rock Preview's own kind when Windows has nothing.
        Assert.Equal(ThumbRoute.OwnExtractor, ThumbKinds.RouteOf("pdf"));
    }

    [Theory]
    [InlineData("doc")]
    [InlineData("dot")]
    [InlineData("xls")]
    [InlineData("xlt")]
    [InlineData(".DOC")]
    [InlineData(@"C:\Files\Budget.Xls")]
    public void Old_Word_and_Excel_files_ask_Windows_first_then_draw_a_page_never_kept(string kind)
    {
        // Windows is asked as before: its cache first whatever the size, then its extractor (the picture the author saved).
        Assert.True(ThumbKinds.AskWindowsCache(kind, 0));
        Assert.True(ThumbKinds.ShellFirst(kind));
        // After a miss, Rock Preview's own extractor draws a page: a text job, one at a time.
        Assert.Equal(ThumbRoute.OwnExtractor, ThumbKinds.RouteOf(kind));
        Assert.True(ThumbKinds.DrawsTextWhenMade(kind));
        Assert.False(ThumbKinds.RendersPdfPage(kind));
        // Not cache-only (that is folders, shortcuts and the PDF kinds): Windows' extractor is asked, and a miss is
        // remembered as a failure as it was when only Windows pictured them.
        Assert.False(ThumbKinds.WindowsCacheOnly(kind));
        Assert.True(ThumbLanes.RemembersFailure(ThumbKinds.WindowsCacheOnly(kind), decodeTried: true));
        Assert.True(ThumbLanes.DecodeAfterShellMiss(ThumbKinds.RouteOf(kind), decodeTried: false, gateToShell: false));
        // Never kept on disk and never counted: not Windows' picture (as before), not the page.
        Assert.False(ThumbKinds.KeepOnDisk(kind));
        Assert.False(ThumbKinds.DrawsText(kind)); // a page known only once drawn (marked), like a Word file's
        Assert.Equal(ThumbCost.Nothing, ThumbKinds.CostOf(kind));
        var census = ThumbEstimator.Count(["a." + ThumbKinds.Ext(kind)]);
        Assert.Equal((0L, 0L), (census.Files, census.BytesAt(256)));
    }

    [Fact]
    public void Excel_workbooks_draw_their_sheet_page_as_a_text_job_and_PowerPoint_stays_as_it_was()
    {
        foreach (var kind in new[] { "xlsx", "xlsm", "xltx", "xltm" })
        {
            Assert.Equal(ThumbRoute.OwnExtractor, ThumbKinds.RouteOf(kind));
            Assert.True(ThumbKinds.DrawsTextWhenMade(kind), kind);
            Assert.False(ThumbKinds.ShellFirst(kind), kind); // the picture saved inside the zip is read first, by Rock Preview
            Assert.False(ThumbKinds.AskWindowsCache(kind, 0), kind);
            Assert.False(ThumbKinds.KeepOnDisk(kind), kind);
            Assert.Equal(ThumbCost.Nothing, ThumbKinds.CostOf(kind));
        }
        // OpenDocument spreadsheets: no sheet page, no text turn.
        Assert.False(ThumbKinds.DrawsTextWhenMade("ods"));
        // Old PowerPoint files: only Windows, then the text sniff, as before; new ones: the saved picture, no text turn.
        foreach (var kind in new[] { "ppt", "pps", "pot" })
        {
            Assert.Equal(ThumbRoute.Shell, ThumbKinds.RouteOf(kind));
            Assert.False(ThumbKinds.ShellFirst(kind), kind);
            Assert.False(ThumbKinds.DrawsTextWhenMade(kind), kind);
            Assert.False(ThumbKinds.KeepOnDisk(kind), kind);
        }
        foreach (var kind in new[] { "pptx", "pptm", "ppsx", "potx" })
        {
            Assert.Equal(ThumbRoute.OwnExtractor, ThumbKinds.RouteOf(kind));
            Assert.False(ThumbKinds.DrawsTextWhenMade(kind), kind);
            Assert.False(ThumbKinds.ShellFirst(kind), kind);
            Assert.Equal(ThumbCost.Document, ThumbKinds.CostOf(kind));
        }
    }

    [Theory]
    [InlineData("pdf", false, true)]
    [InlineData("ai", false, true)]
    [InlineData("ait", false, true)]
    [InlineData("lnk", false, true)]
    [InlineData(".PDF", false, true)]
    [InlineData("AI", false, true)]
    [InlineData("AiT", false, true)]
    [InlineData("LNK", false, true)]
    [InlineData(@"C:\Docs\Report.Pdf", false, true)]
    [InlineData(@"D:\Art\Logo.ai", false, true)]
    [InlineData(@"\\server\share\Template.AIT", false, true)]
    [InlineData(@"C:\Docs\Report.pdf.lnk", false, true)]
    [InlineData(@"C:\Docs\Tools.LNK", false, true)]
    // Internet shortcuts too: one may point at a local PDF.
    [InlineData("url", false, true)]
    [InlineData(@"C:\Docs\Report.URL", false, true)]
    [InlineData(@"C:\Docs\Site.url ", false, true)]
    // Names as Windows opens them: trailing spaces and dots ignored, a stream name cut off.
    [InlineData(@"C:\Docs\a.pdf ", false, true)]
    [InlineData(@"C:\Docs\a.pdf.", false, true)]
    [InlineData(@"C:\Docs\a.PDF . .", false, true)]
    [InlineData(@"C:\Docs\a.pdf:x", false, true)]
    [InlineData(@"C:\Docs\a.pdf:x.txt", false, true)]
    [InlineData(@"C:\Docs\Tools.lnk ", false, true)]
    [InlineData("a.pdf:stream", false, true)]
    [InlineData(@"C:\Docs\a.txt:x.pdf", false, false)]
    [InlineData(@"C:\Docs\a.mp4.", false, false)]
    [InlineData(@"C:\Docs\pdf.", false, false)]
    // A drive-relative name: the colon is the drive's, not a stream's.
    [InlineData(@"D:report.pdf", false, true)]
    [InlineData(@"d:Tools.LNK", false, true)]
    [InlineData(@"D:report.pdf:x", false, true)]
    [InlineData(@"D:clip.mp4", false, false)]
    [InlineData(@"D:pdf", false, false)]
    // Trailing separators are ignored before the name is taken.
    [InlineData(@"C:\Docs\a.pdf\", false, true)]
    [InlineData(@"C:\Docs\a.pdf\\", false, true)]
    [InlineData(@"C:/Docs/a.ai/", false, true)]
    [InlineData(@"C:\Docs\a.mp4\", false, false)]
    // A folder, whatever its name.
    [InlineData(@"C:\Docs\Projects", true, true)]
    [InlineData(@"C:\Docs\Clips.mp4", true, true)]
    [InlineData(@"C:\", true, true)]
    [InlineData("", true, true)]
    // Everything else asks Windows' extractor as before.
    [InlineData("mp4", false, false)]
    [InlineData("ttf", false, false)]
    [InlineData("jpg", false, false)]
    [InlineData("docx", false, false)]
    [InlineData(@"C:\Docs\Clips.mp4", false, false)]
    [InlineData(@"C:\Docs\README", false, false)]
    [InlineData(@"C:\Docs\pdf", false, false)]
    [InlineData("", false, false)]
    public void Folders_shortcuts_and_pdf_kinds_ask_only_Windows_cache(string extensionOrPath, bool isFolder, bool cacheOnly)
        => Assert.Equal(cacheOnly, ThumbKinds.WindowsCacheOnly(extensionOrPath, isFolder));

    [Theory]
    [InlineData(2, 1)]
    [InlineData(3, 2)]
    [InlineData(6, 2)]
    [InlineData(1, 0)]
    public void Idle_work_always_leaves_a_decode_thread_free(int decodeThreads, int idle)
        => Assert.Equal(idle, ThumbRules.IdleDecodeThreadsFor(decodeThreads));

    [Fact]
    public void Folders_cost_nothing_and_videos_are_known()
    {
        Assert.Equal(ThumbCost.Nothing, ThumbKinds.CostOf("Holiday.jpg", isFolder: true));
        Assert.Equal(ThumbCost.Photo, ThumbKinds.CostOf(@"C:\Photos\Holiday.JPG"));
        Assert.Equal(ThumbCost.Video, ThumbKinds.CostOf("MOV"));
        Assert.Equal(ThumbCost.Nothing, ThumbKinds.CostOf("mp3"));
        // Every video takes the route through Windows' extractor.
        Assert.Equal(ThumbRoute.Shell, ThumbKinds.RouteOf("webm"));
    }

    [Theory]
    // Documents and presentations, old and new, and OpenDocument.
    [InlineData("pptx", true)] [InlineData("docx", true)] [InlineData("xlsx", true)] [InlineData("ppsx", true)] [InlineData("odp", true)]
    [InlineData("doc", true)] [InlineData("xls", true)]
    // Pages only Windows pictures: old PowerPoint, binary Excel, XPS, Visio, Publisher.
    [InlineData("ppt", true)] [InlineData("pps", true)] [InlineData("pot", true)] [InlineData("sldx", true)] [InlineData("xlsb", true)]
    [InlineData("xps", true)] [InlineData("oxps", true)] [InlineData("vsd", true)] [InlineData("vsdx", true)] [InlineData("pub", true)]
    // PDF, vector and design files, fonts, flat pictures (screenshots).
    [InlineData("pdf", true)] [InlineData("ai", true)] [InlineData("eps", true)] [InlineData("dwg", true)] [InlineData("emf", true)]
    [InlineData("indd", true)] [InlineData("afdesign", true)] [InlineData("ttf", true)] [InlineData("otf", true)]
    [InlineData("png", true)] [InlineData("gif", true)] [InlineData("bmp", true)]
    // Photos, camera files, PSD, videos: no outline.
    [InlineData("jpg", false)] [InlineData("heic", false)] [InlineData("webp", false)] [InlineData("tif", false)] [InlineData("cr2", false)]
    [InlineData("psd", false)] [InlineData("mp4", false)] [InlineData("mov", false)]
    // Icons are shapes, text pages draw their own border, and what has no picture of its own.
    [InlineData("ico", false)] [InlineData("cur", false)] [InlineData("txt", false)] [InlineData("cs", false)] [InlineData("svg", false)]
    [InlineData("csv", false)] [InlineData("mp3", false)] [InlineData("zip", false)] [InlineData("exe", false)] [InlineData("", false)]
    public void Page_like_kinds_get_an_outline(string ext, bool pageLike)
    {
        Assert.Equal(pageLike, ThumbKinds.IsPageLike(ext));
        Assert.Equal(pageLike, ThumbKinds.IsPageLike("." + ext.ToUpperInvariant()) && ext.Length > 0);
        Assert.Equal(pageLike, ThumbKinds.IsPageLike(@"D:\Work\Slides 2024." + ext) && ext.Length > 0);
    }

    [Fact]
    public void Page_like_reads_the_extension_as_the_other_kind_questions_do()
    {
        Assert.True(ThumbKinds.IsPageLike(@"D:\a.b\Report.v2.PDF"));
        Assert.True(ThumbKinds.IsPageLike("D:/scans/letter.Png"));
        // A name without an extension, in a folder whose name has a dot; a name ending in a dot.
        Assert.False(ThumbKinds.IsPageLike(@"D:\slides.pptx\README"));
        Assert.False(ThumbKinds.IsPageLike(@"D:\Work\report."));
        Assert.False(ThumbKinds.IsPageLike(""));
        // Every page-like kind is one Rock Preview or Windows pictures: never a kind that only shows its icon.
        foreach (var ext in new[] { "pptx", "pdf", "ttf", "png", "dwg", "doc" })
            Assert.NotEqual(ThumbRoute.Shell, ThumbKinds.RouteOf(ext));
    }

    [Fact]
    public void Some_icons_belong_to_the_file()
    {
        foreach (var own in new[] { "exe", "lnk", "url", "ico", "cur", "ani", "scr", "cpl", "msi", "pif", "appref-ms" })
            Assert.True(ThumbKinds.IconBelongsToFile(own), own);
        Assert.True(ThumbKinds.IconBelongsToFile("", isFolder: true));
        Assert.False(ThumbKinds.IconBelongsToFile("pdf"));
        Assert.False(ThumbKinds.IconBelongsToFile("mp4"));
    }

    [Fact]
    public void Too_small_a_picture_is_a_stand_in()
    {
        Assert.Equal(ThumbQuality.None, ThumbKinds.QualityOf(0, 256));
        Assert.Equal(ThumbQuality.Low, ThumbKinds.QualityOf(160, 256));
        Assert.Equal(ThumbQuality.Final, ThumbKinds.QualityOf(205, 256));
        Assert.Equal(ThumbQuality.Low, ThumbKinds.QualityOf(256, 512));
        // A source smaller than the class can't give more than itself.
        Assert.Equal(ThumbQuality.Final, ThumbKinds.QualityOf(100, 256, sourceLongSide: 100));
        Assert.Equal(ThumbQuality.Low, ThumbKinds.QualityOf(60, 256, sourceLongSide: 100));
    }

    [Fact]
    public void Large_decodes_wait_and_huge_ones_are_refused()
    {
        Assert.Equal(DecodeAdmission.Run, ThumbKinds.AdmitDecode(8000, 8000));
        Assert.Equal(DecodeAdmission.LargeGate, ThumbKinds.AdmitDecode(10_000, 10_000));
        Assert.Equal(DecodeAdmission.Refused, ThumbKinds.AdmitDecode(30_000, 30_000));
        Assert.Equal(DecodeAdmission.Refused, ThumbKinds.AdmitDecode(0, 100));
        Assert.Equal(DecodeAdmission.Refused, ThumbKinds.AdmitDecode(long.MaxValue / 2, 4));
    }
}

public sealed class ThumbKeyTests
{
    [Fact]
    public void Size_classes()
    {
        Assert.Equal(256, ThumbSizeClass.For(96));
        Assert.Equal(256, ThumbSizeClass.For(256));
        Assert.Equal(512, ThumbSizeClass.For(257));
        Assert.Equal(512, ThumbSizeClass.For(1024));
    }

    [Fact]
    public void A_key_is_the_upper_case_path_the_stamp_and_the_class()
    {
        var stamp = new ThumbStamp(123, 456);
        var a = ThumbKey.Of(@"C:\Photos\a.jpg", stamp, 160);
        var b = ThumbKey.Of(@"c:\photos\A.JPG", stamp, 200);
        Assert.Equal(a, b);
        Assert.Equal(@"C:\PHOTOS\A.JPG", a.Path);
        Assert.Equal(256, a.SizeClass);
        Assert.NotEqual(a.Hash64, a.WithSizeClass(512).Hash64);
        Assert.NotEqual(a.Hash64, a.WithStamp(new ThumbStamp(124, 456)).Hash64);
        Assert.NotEqual(a.Hash64, a.WithStamp(new ThumbStamp(123, 457)).Hash64);
        Assert.NotEqual(a.Check32, a.WithStamp(new ThumbStamp(123, 457)).Check32);
        Assert.NotEqual(a.Hash64, ThumbKey.Of(@"C:\Photos\b.jpg", stamp, 160).Hash64);
        Assert.NotEqual(0UL, a.Hash64);
    }

    [Fact]
    public void Hashes_are_the_same_in_every_process()
    {
        // Pinned values: the store finds pictures written by earlier runs only while these stay the same.
        var key = ThumbKey.Of(@"C:\Photos\a.jpg", new ThumbStamp(123, 456), 256);
        var (hash, check) = ThumbKey.HashOf(@"C:\PHOTOS\A.JPG", new ThumbStamp(123, 456), 256);
        Assert.Equal((hash, check), (key.Hash64, key.Check32));
        Assert.Equal(PinnedHash, key.Hash64);
        Assert.Equal(PinnedCheck, key.Check32);
    }

    const ulong PinnedHash = 2896004062589299158;
    const uint PinnedCheck = 147952445;

    [Fact]
    public void Long_paths_hash_too()
    {
        string path = @"\\?\D:\" + string.Join('\\', Enumerable.Repeat("folder with a long name", 60)) + @"\photo.jpg";
        var key = ThumbKey.Of(path, new ThumbStamp(1, 2), 256);
        Assert.Equal(key, ThumbKey.Of(path.ToLowerInvariant(), new ThumbStamp(1, 2), 256));
        Assert.DoesNotContain("folder", key.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Unknown_and_folder_stamps()
    {
        Assert.True(ThumbStamp.Unknown.IsUnknown);
        Assert.False(ThumbStamp.ForFolder(99).IsUnknown);
        Assert.Equal(0, ThumbStamp.ForFolder(99).Length);
    }
}
