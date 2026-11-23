using System.Collections.Concurrent;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using Anything.Core.Content.Indexing;
using Anything.Core.Thumbs;
using Anything.Preview.Thumbnails;

namespace Anything.Tests.Thumbs;

/// <summary>
/// The user's choice of what Instant thumbnails keeps, asked inside the service: a picture of a type left out is shown
/// but not handed to the store, and the idle step doesn't make it at all. Two small pictures built here (a BMP left out,
/// a PNG kept as the check that storing works at all), a store in the test's own temp folder, Rock Preview's own decoder
/// (no Windows extractor, Windows' cache not warmed). The third place the choice is asked, the grid's idle planner, lives
/// in the app and is not reached from here. Last, a full store of pictures: the idle step doesn't make a document the
/// store would throw away.
/// </summary>
public sealed class ThumbServiceKeepTests : IDisposable
{
    static readonly TimeSpan Patience = TimeSpan.FromSeconds(60);

    readonly DirectoryInfo _dir = Directory.CreateTempSubdirectory("rp-thumb-keep-");
    readonly string _leftOut, _kept, _storeFolder;

    public ThumbServiceKeepTests()
    {
        _leftOut = Path.Combine(_dir.FullName, "left out.bmp");
        _kept = Path.Combine(_dir.FullName, "kept.png");
        _storeFolder = Path.Combine(_dir.FullName, "store");
        MakePicture(_leftOut, ImageFormat.Bmp, Color.SteelBlue);
        MakePicture(_kept, ImageFormat.Png, Color.IndianRed);
    }

    public void Dispose()
    {
        for (int i = 0; i < 10; i++)
        {
            try { _dir.Delete(true); return; }
            catch (IOException) { Thread.Sleep(300); }
            catch (UnauthorizedAccessException) { Thread.Sleep(300); }
        }
    }

    static void MakePicture(string path, ImageFormat format, Color color)
    {
        using var bmp = new Bitmap(600, 400, PixelFormat.Format24bppRgb);
        using (var g = Graphics.FromImage(bmp))
        {
            g.Clear(color);
            g.FillEllipse(Brushes.White, 100, 50, 400, 300);
        }
        bmp.Save(path, format);
    }

    static ThumbRequest Request(string path, int index)
    {
        var fi = new FileInfo(path);
        return new ThumbRequest(path, new ThumbStamp(fi.Length, fi.LastWriteTimeUtc.ToFileTimeUtc()), 160, ThumbPriority.Visible, index);
    }

    // Both kinds are pictures, so only the list of types left out tells them apart.
    static readonly ThumbKeep LeaveOutBmp = new(true, true, true, ["bmp"]);

    ThumbnailService StartService()
    {
        var service = new ThumbnailService(new ThumbServiceOptions
        {
            Power = NoPowerPolicy.Instance,
            WarmWindowsCache = false,
            Name = "keep test thumbnails",
        });
        service.StoreKeep = LeaveOutBmp;
        service.UseStore(() => ThumbStore.Open(_storeFolder));
        // Opened now (Clear opens it on the thread pool), so no picture meets a store still opening.
        service.ClearStore();
        Assert.True(WaitFor(() => service.CurrentStore is { IsEnabled: true }), "the store opened");
        return service;
    }

    static bool WaitFor(Func<bool> condition)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < Patience)
        {
            if (condition()) return true;
            Thread.Sleep(20);
        }
        return condition();
    }

    [Fact]
    public void A_picture_of_a_type_left_out_is_shown_but_not_stored()
    {
        var service = StartService();
        var finals = new ConcurrentDictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var client = service.CreateClient(a =>
        {
            if (a.Quality == ThumbQuality.Final) finals[a.Path] = true;
            a.Bitmap?.Dispose();
        });
        try
        {
            client.SubmitViewport(1, [Request(_leftOut, 0), Request(_kept, 1)]);
            Assert.True(WaitFor(() => finals.Count == 2 && client.IsIdle), "both pictures were shown");
            Assert.True(WaitFor(() => service.Stats.FinalsStored >= 1), "the kept picture was stored");
            // A picture is stored just after it is shown, on the same thread: a moment more for one that shouldn't be.
            Thread.Sleep(500);
            Assert.Equal(1, service.Stats.FinalsStored);
            var store = service.CurrentStore!;
            Assert.True(store.WaitUntilWritten(TimeSpan.FromSeconds(10)));
            Assert.Equal(1, store.Count);
        }
        finally
        {
            client.Dispose();
            service.Dispose();
        }
    }

    [Fact]
    public void A_picture_is_saved_with_its_kind_and_switching_the_kind_off_deletes_it()
    {
        var service = StartService();
        var client = service.CreateClient(a => a.Bitmap?.Dispose());
        try
        {
            client.SubmitViewport(1, [Request(_kept, 0)]);
            Assert.True(WaitFor(() => service.Stats.FinalsStored >= 1 && client.IsIdle), "the picture was stored");
            var store = service.CurrentStore!;
            Assert.True(store.WaitUntilWritten(TimeSpan.FromSeconds(10)));
            // A PNG is a picture to the user: it went to the pictures' packs.
            Assert.True(store.Split.Pictures > 0);
            Assert.Equal(0, store.Split.Documents + store.Split.Videos + store.Split.Earlier);
            // Videos switched off: nothing of theirs to delete, the picture stays.
            service.StoreKeep = new ThumbKeep(true, false, true, []);
            Thread.Sleep(200);
            Assert.Equal(1, store.Count);
            // Pictures switched off: deleted in the background.
            service.StoreKeep = new ThumbKeep(false, false, true, []);
            Assert.True(WaitFor(() => store.Count == 0 && store.Split.Pictures == 0), "the pictures were deleted");
        }
        finally
        {
            client.Dispose();
            service.Dispose();
        }
    }

    [Fact]
    public void The_idle_step_leaves_out_a_type_the_user_left_out()
    {
        var service = StartService();
        var client = service.CreateClient(a => a.Bitmap?.Dispose());
        try
        {
            // Idle work belongs to the list on screen: an empty one here, so nothing but the idle step runs.
            client.SubmitViewport(1, []);
            Assert.Equal(2, client.SubmitIdle(1, [Request(_leftOut, 0), Request(_kept, 1)]));
            Assert.True(WaitFor(() => service.Stats.FinalsStored >= 1 && client.IsIdle), "the kept picture was made and stored");
            Thread.Sleep(500);
            var stats = service.Stats;
            // The left-out picture was not even decoded: skipped before any work, not made and then thrown away.
            Assert.Equal(1, stats.OwnDecodes);
            Assert.Equal(1, stats.FinalsStored);
        }
        finally
        {
            client.Dispose();
            service.Dispose();
        }
    }

    // An EMF is a document to the user (a vector format), and Rock Preview draws it itself.
    static void MakeDrawing(string path)
    {
        using var reference = new Bitmap(1, 1);
        using var rg = Graphics.FromImage(reference);
        IntPtr hdc = rg.GetHdc();
        try
        {
            using var emf = new Metafile(path, hdc, new RectangleF(0, 0, 600, 400), MetafileFrameUnit.Pixel, EmfType.EmfPlusDual);
            using var g = Graphics.FromImage(emf);
            g.Clear(Color.Khaki);
            g.FillRectangle(Brushes.DarkGreen, 100, 50, 400, 300);
        }
        finally
        {
            rg.ReleaseHdc(hdc);
        }
    }

    [Fact]
    public void The_idle_step_leaves_out_a_kind_the_full_store_would_not_save()
    {
        string drawing = Path.Combine(_dir.FullName, "drawing.emf"), second = Path.Combine(_dir.FullName, "second drawing.emf");
        MakeDrawing(drawing);
        MakeDrawing(second);
        Assert.Equal(ThumbKeepGroup.Documents, ThumbKeep.GroupOf(drawing));
        var service = new ThumbnailService(new ThumbServiceOptions
        {
            Power = NoPowerPolicy.Instance,
            WarmWindowsCache = false,
            Name = "full store test thumbnails",
        });
        service.UseStore(() => ThumbStore.Open(_storeFolder, new ThumbStoreOptions { PackBytes = 4096, CapBytes = 64 * 1024 }));
        service.ClearStore();
        Assert.True(WaitFor(() => service.CurrentStore is { IsEnabled: true }), "the store opened");
        var store = service.CurrentStore!;
        var client = service.CreateClient(a => a.Bitmap?.Dispose());
        try
        {
            client.SubmitViewport(1, []);
            // While the store has room, idle work makes the drawing: the check that it would be made at all.
            Assert.Equal(1, client.SubmitIdle(1, [Request(drawing, 0)]));
            Assert.True(WaitFor(() => service.Stats.FinalsStored >= 1 && client.IsIdle), "the drawing was made and stored");
            var made = service.Stats;
            long workBefore = made.OwnDecodes + made.OwnExtracts;
            Assert.True(workBefore >= 1);

            // Filled with pictures only (made-up records): documents are refused now, pictures are not.
            store.DropGroup(ThumbKeepGroup.Documents);
            Assert.True(WaitFor(() => store.Split.Documents == 0), "the drawing's thumbnail was deleted");
            for (int i = 0; i < 100; i++)
            {
                var key = ThumbKey.Of($@"D:\Pictures\photo {i}.jpg", new ThumbStamp(1000, 5000), 256);
                var payload = new byte[1000];
                payload[0] = (byte)i;
                Assert.True(store.Put(key, StoredKind.Jpeg, 256, 192, payload, ThumbKeepGroup.Pictures));
            }
            Assert.True(store.WaitUntilWritten(TimeSpan.FromSeconds(10)));
            Assert.True(WaitFor(() => store.IsFull && store.Refuses(ThumbKeepGroup.Documents)), "the store is full of pictures");
            Assert.False(store.Refuses(ThumbKeepGroup.Pictures));

            // A new drawing and a picture as idle work: the picture is made and stored, the drawing not even drawn.
            Assert.Equal(2, client.SubmitIdle(1, [Request(second, 0), Request(_kept, 1)]));
            Assert.True(WaitFor(() => service.Stats.FinalsStored >= made.FinalsStored + 1 && client.IsIdle), "the picture was made and stored");
            Thread.Sleep(500);
            var stats = service.Stats;
            Assert.Equal(made.FinalsStored + 1, stats.FinalsStored);
            Assert.Equal(workBefore + 1, stats.OwnDecodes + stats.OwnExtracts);
            Assert.Equal(made.OwnExtracts, stats.OwnExtracts);
        }
        finally
        {
            client.Dispose();
            service.Dispose();
        }
    }
}
