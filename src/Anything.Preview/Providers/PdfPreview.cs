using System;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Anything.Preview.Util;
using Windows.Data.Pdf;
using Windows.Storage.Streams;

namespace Anything.Preview;

/// <summary>PDF (and PDF-compatible .ai) pages through Windows.Data.Pdf, rendered lazily with page navigation.</summary>
internal static class PdfPreview
{
    /// <summary>Files up to this size are loaded into memory (no handle kept); larger ones stream with full sharing.</summary>
    private const long InMemoryLimit = 96L * 1024 * 1024;

    public static async Task<PreviewResult?> RenderAsync(string path, Size target, CancellationToken ct, string provider = "PDF")
    {
        var src = await PdfPageSource.OpenAsync(path, ct).ConfigureAwait(false);
        if (src == null) return null;
        try
        {
            var first = await src.RenderAsync(0, target, ct).ConfigureAwait(false);
            var res = new PreviewResult(path, PreviewKind.Image, provider)
            {
                FormatName = provider == "PDF" ? "PDF document" : "PDF-compatible document",
                Image = first,
                Pages = src.Count > 1 ? src : null,
            };
            res.AddMeta("Pages", src.Count.ToString());
            res.AddMeta("Page size", src.FirstPageSizeText);
            if (src.Count <= 1) src.Dispose();
            return res;
        }
        catch
        {
            src.Dispose();
            throw;
        }
    }

    internal sealed class PdfPageSource : IPageSource
    {
        private readonly PdfDocument _doc;
        private readonly IDisposable? _stream;
        private readonly SemaphoreSlim _gate = new(1, 1);
        // Windows.Data.Pdf renders through Direct2D/Direct3D 11. Concurrent renders from the preview pane and
        // several thumbnail workers crashed inside the AMD D3D11 driver (atidxx64.dll access violation), so all
        // page renders in the process go through one gate.
        private static readonly SemaphoreSlim s_renderGate = new(1, 1);
        private bool _disposed;

        private PdfPageSource(PdfDocument doc, IDisposable? stream)
        {
            _doc = doc;
            _stream = stream;
            Count = (int)doc.PageCount;
            try
            {
                using var p = doc.GetPage(0);
                var s = p.Size; // points (1/72 in) at 96 dpi DIPs actually; Windows reports DIPs
                FirstPageSizeText = $"{s.Width * 72 / 96 / 72 * 25.4:0} × {s.Height * 72 / 96 / 72 * 25.4:0} mm";
            }
            catch { FirstPageSizeText = ""; }
        }

        public int Count { get; }
        public string Unit => "Page";
        public string FirstPageSizeText { get; }

        public static async Task<PdfPageSource?> OpenAsync(string path, CancellationToken ct)
        {
            IRandomAccessStream ras;
            IDisposable? owner = null;
            var fi = new FileInfo(path);
            if (fi.Length <= InMemoryLimit)
            {
                byte[] bytes = FileUtil.ReadAllCapped(path, InMemoryLimit) ?? Array.Empty<byte>();
                var mem = new InMemoryRandomAccessStream();
                using (var w = new DataWriter(mem))
                {
                    w.WriteBytes(bytes);
                    await w.StoreAsync().AsTask(ct).ConfigureAwait(false);
                    await w.FlushAsync().AsTask(ct).ConfigureAwait(false);
                    w.DetachStream();
                }
                mem.Seek(0);
                ras = mem;
                owner = mem;
            }
            else
            {
                var fs = FileUtil.OpenShared(path, 1 << 16);
                ras = fs.AsRandomAccessStream();
                owner = new CompositeDisposable(ras, fs);
            }
            try
            {
                var doc = await PdfDocument.LoadFromStreamAsync(ras).AsTask(ct).ConfigureAwait(false);
                if (doc.PageCount == 0) { owner?.Dispose(); return null; }
                return new PdfPageSource(doc, owner);
            }
            catch
            {
                owner?.Dispose();
                throw;
            }
        }

        public async Task<Bitmap> RenderAsync(int index, Size target, CancellationToken ct)
        {
            await _gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                using var page = _doc.GetPage((uint)Math.Clamp(index, 0, Count - 1));
                double pw = page.Size.Width, ph = page.Size.Height;
                if (!(pw > 0 && ph > 0)) { pw = 612; ph = 792; }
                // Render ~1.5x the pane so small zooms stay crisp; cap the long edge.
                var box = new Size(Math.Max(target.Width, 300) * 3 / 2, Math.Max(target.Height, 300) * 3 / 2);
                double scale = Math.Min(box.Width / pw, box.Height / ph);
                scale = Math.Min(scale, 4096 / Math.Max(pw, ph));
                var opts = new PdfPageRenderOptions
                {
                    DestinationWidth = (uint)Math.Max(1, Math.Round(pw * scale)),
                    DestinationHeight = (uint)Math.Max(1, Math.Round(ph * scale)),
                    BackgroundColor = Windows.UI.Color.FromArgb(255, 255, 255, 255),
                    BitmapEncoderId = Windows.Graphics.Imaging.BitmapEncoder.BmpEncoderId,
                };
                using var outStream = new InMemoryRandomAccessStream();
                await s_renderGate.WaitAsync(ct).ConfigureAwait(false);
                try { await page.RenderToStreamAsync(outStream, opts).AsTask(ct).ConfigureAwait(false); }
                finally { s_renderGate.Release(); }
                outStream.Seek(0);
                using var s = outStream.AsStreamForRead();
                using var ms = new MemoryStream();
                await s.CopyToAsync(ms, ct).ConfigureAwait(false);
                ms.Position = 0;
                using var tmp = new Bitmap(ms);
                return new Bitmap(tmp); // detach from the stream
            }
            finally
            {
                _gate.Release();
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try { _stream?.Dispose(); } catch { }
        }
    }

    private sealed class CompositeDisposable(params IDisposable[] items) : IDisposable
    {
        public void Dispose()
        {
            foreach (var i in items) try { i.Dispose(); } catch { }
        }
    }
}
