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
    [InlineData("doc", (int)ThumbRoute.Shell)]
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
        Assert.True(ThumbKinds.IsRaw("DNG"));
        Assert.True(ThumbKinds.IsJpeg("jfif"));
        Assert.True(ThumbKinds.IsGif("gif"));
        Assert.False(ThumbKinds.HasOwnDecoder("mp4"));
        Assert.True(ThumbKinds.HasOwnDecoder("pdf"));
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
        foreach (var flat in new[] { "pdf", "docx", "txt", "ttf", "png", "bmp", "gif", "ico", "dxf", "emf", "svg" })
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
    [InlineData("pem")]
    [InlineData("KEY")]
    [InlineData("env")]
    [InlineData("eml")]
    [InlineData("mbox")]
    [InlineData("npmrc")]
    [InlineData("asc")]
    [InlineData("txt")]
    [InlineData("md")]
    [InlineData("cs")]
    [InlineData("json")]
    [InlineData("html")]
    [InlineData("svg")]
    [InlineData("csv")]
    [InlineData("rtf")]
    [InlineData("ics")]
    [InlineData("vcf")]
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
        Assert.True(ThumbKinds.IsVideo("MOV"));
        Assert.False(ThumbKinds.IsVideo("mp3"));
        // Every video takes the route through Windows' extractor.
        Assert.Equal(ThumbRoute.Shell, ThumbKinds.RouteOf("webm"));
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
