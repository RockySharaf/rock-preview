using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Media.Imaging;
using Anything.Core.Thumbs;
using Anything.Preview.Interop;
using Anything.Preview.Util;

namespace Anything.Preview.Thumbnails;

/// <summary>
/// The pieces of the ladder that make, store and show one picture: Windows' cache and extractor, the previews inside
/// files, the encoding for the store and back, and the scaling to the grid's size. Stateless; each method runs on the
/// calling thread (the shell ones on an STA thread). The caller owns every bitmap returned.
/// </summary>
internal static class ThumbSources
{
    /// <summary>Windows' answer for a miss in its thumbnail cache (a cache-only lookup never starts an extraction).</summary>
    public const int HrNotCached = unchecked((int)0x80030002);

    private static readonly ImageCodecInfo? JpegCodec = ImageCodecInfo.GetImageEncoders().FirstOrDefault(c => c.FormatID == ImageFormat.Jpeg.Guid);

    /// <summary>
    /// Windows' thumbnail cache only (ThumbnailOnly | InCacheOnly | BiggerSizeOk at the size class): a few milliseconds,
    /// and a miss starts nothing. Windows may hand back a smaller picture than asked: the caller judges it with
    /// <see cref="ThumbKinds.QualityOf"/>. Straight alpha. STA only.
    /// </summary>
    public static Bitmap? CacheProbe(string path, int sizeClass, out int hr)
        => Shell.GetImageSta(path, sizeClass, Shell.SIIGBF.ThumbnailOnly | Shell.SIIGBF.InCacheOnly | Shell.SIIGBF.BiggerSizeOk, out hr);

    /// <summary>
    /// Windows' extractor at the size class (ThumbnailOnly, without BiggerSizeOk: with it Windows returned a 1280×720
    /// video frame for a 256 request). Can take seconds or hang inside a third-party handler. STA only.
    /// </summary>
    public static Bitmap? ShellExtract(string path, int sizeClass, out int hr)
        => Shell.GetImageSta(path, sizeClass, Shell.SIIGBF.ThumbnailOnly, out hr);

    /// <summary>
    /// In place of <see cref="ShellExtract"/> for the files Windows' extractor is never asked for in this process
    /// (<see cref="ThumbKinds.WindowsCacheOnly"/>: folders, shortcuts, the PDF kinds): Windows' thumbnail cache only, at the
    /// size class (ThumbnailOnly | InCacheOnly, without BiggerSizeOk for the same reason as there). A miss starts nothing
    /// and never reaches a thumbnail handler. STA only.
    /// </summary>
    public static Bitmap? CacheOnlyExtract(string path, int sizeClass, out int hr)
        => Shell.GetImageSta(path, sizeClass, Shell.SIIGBF.ThumbnailOnly | Shell.SIIGBF.InCacheOnly, out hr);

    /// <summary>Windows' answer when the icon asked for is still being extracted for another caller (E_PENDING).</summary>
    public const int HrPending = unchecked((int)0x8000000A);

    // How often, and how far apart, an icon that is still being extracted elsewhere is asked for again (about 300 ms in
    // all; the first extraction of a kind's icon took 50 to 90 ms in the probe).
    private const int PendingTries = 12;
    private const int PendingWaitMs = 25;

    /// <summary>
    /// The file-type icon (or the file's own icon) at <paramref name="size"/>. STA only. When two threads ask for the
    /// icon of one kind at the same moment and Windows hasn't got it yet, the second is told it is pending (E_PENDING)
    /// rather than given it: seen with 40 Excel files at once, where the first "no picture" answer then left with no
    /// icon. That answer is asked again a little later, a few times; any other failure is final.
    /// </summary>
    public static Bitmap? Icon(string path, int size)
    {
        size = Math.Clamp(size, 16, 256);
        var bmp = Shell.GetImageSta(path, size, Shell.SIIGBF.IconOnly, out int hr);
        for (int i = 1; bmp == null && hr == HrPending && i < PendingTries; i++)
        {
            Thread.Sleep(PendingWaitMs);
            bmp = Shell.GetImageSta(path, size, Shell.SIIGBF.IconOnly, out hr);
        }
        return bmp;
    }

    /// <summary>The preview a file carries inside itself (step 3 of the ladder), or null. Always a stand-in.</summary>
    public static Bitmap? Embedded(string path, EmbeddedPreview kind, int sizeClass)
    {
        switch (kind)
        {
            case EmbeddedPreview.Exif:
            case EmbeddedPreview.Raw:
                return WicImage.EmbeddedThumbnail(path);
            case EmbeddedPreview.PsdResource:
            {
                var jpeg = PsdDecoder.ReadThumbnailResource(path);
                return jpeg == null ? null : BitmapUtil.Decode(jpeg, new Size(sizeClass, sizeClass));
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// The first lines of a file that turns out to be text, on a small page (the "text sniff" for unknown kinds). Marked
    /// as a text page (<see cref="ThumbnailProvider.IsTextPage"/>): shown, never kept on disk.
    /// </summary>
    public static Bitmap? TextSniff(string path, int sizeClass)
    {
        var bytes = FileUtil.ReadHead(path, 16 * 1024);
        if (!TextPreview.LooksLikeText(bytes, out _)) return null;
        var (text, _) = TextPreview.Decode(bytes);
        return text == null ? null : ThumbnailProvider.TextPage(text, sizeClass);
    }

    /// <summary>
    /// A copy of <paramref name="src"/> that fits in <paramref name="size"/>×<paramref name="size"/> (never enlarged), 32bpp
    /// premultiplied: what the grid draws. Scaled by WIC, not GDI+, whose one-call-at-a-time lock would make the grid's
    /// painting wait for the workers (<see cref="WicNative"/>).
    /// </summary>
    public static Bitmap ToDisplay(Bitmap src, int size) => WicNative.Fit(src, size);

    /// <summary>A picture that fits the size class: itself when it does already, else a scaled copy (the original is then disposed).</summary>
    public static Bitmap FitClass(Bitmap src, int sizeClass) => WicNative.FitOrSelf(src, sizeClass);

    /// <summary>Whether any pixel is less than opaque (then the master is stored as PNG whatever its kind).</summary>
    public static bool HasTransparency(Bitmap bmp)
    {
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                for (int y = 0; y < data.Height; y++)
                {
                    byte* row = (byte*)data.Scan0 + (long)y * data.Stride;
                    for (int x = 0; x < data.Width; x++)
                        if (row[x * 4 + 3] != 255) return true;
                }
            }
            return false;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    /// <summary>
    /// The master encoded for the store: JPEG 85 for photo kinds, PNG for flat kinds and anything with transparency, and
    /// JPEG 90 for a flat picture whose PNG came out too big (<see cref="ThumbKinds.EncodingFor"/>). Null when GDI+ can't.
    /// </summary>
    public static byte[]? Encode(Bitmap master, string extensionOrPath, int sizeClass, out StoredKind kind)
    {
        bool alpha = HasTransparency(master);
        var encoding = ThumbKinds.EncodingFor(extensionOrPath, alpha, sizeClass);
        try
        {
            if (encoding == ThumbEncoding.Png)
            {
                byte[] png = Save(master, ImageFormat.Png, null);
                encoding = ThumbKinds.EncodingFor(extensionOrPath, alpha, sizeClass, png.Length);
                if (encoding == ThumbEncoding.Png)
                {
                    kind = StoredKind.Png;
                    return png;
                }
            }
            kind = StoredKind.Jpeg;
            return Save(master, ImageFormat.Jpeg, encoding == ThumbEncoding.Jpeg90 ? 90 : 85);
        }
        catch (Exception e) when (e is ExternalException or ArgumentException)
        {
            kind = StoredKind.None;
            return null;
        }
    }

    private static byte[] Save(Bitmap bmp, ImageFormat format, long? quality)
    {
        using var ms = new MemoryStream();
        if (quality is long q && JpegCodec != null)
        {
            using var ps = new EncoderParameters(1);
            ps.Param[0] = new EncoderParameter(Encoder.Quality, q);
            bmp.Save(ms, JpegCodec, ps);
        }
        else
        {
            bmp.Save(ms, format);
        }
        return ms.ToArray();
    }

    /// <summary>A stored master is at most this big on a side (the large class); a record claiming more isn't decoded.</summary>
    const int StoredMaxSide = 1024;

    /// <summary>
    /// A stored master decoded and scaled to fit <paramref name="size"/> (32bpp premultiplied), or null when the bytes
    /// don't decode as a thumbnail. The store's checks only prove the bytes are the ones written, not what wrote them (a
    /// store at a chosen place may be shared): a picture claiming to be bigger than any master is not decoded, and no
    /// failure, low memory included, gets past this method.
    /// </summary>
    public static Bitmap? DecodeStored(ReadOnlyMemory<byte> payload, int size)
    {
        if (payload.Length < 8) return null;
        try
        {
            // The header is read first, so the size is known before any pixel is.
            return WicNative.DecodeStored(payload, size, StoredMaxSide);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary><see cref="ToDisplay"/>, or null when GDI+ fails (low memory, a picture it can't scale).</summary>
    public static Bitmap? TryToDisplay(Bitmap src, int size)
    {
        try { return ToDisplay(src, size); }
        catch (Exception e) when (e is OutOfMemoryException or ExternalException or ArgumentException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether a failure may go away by itself (a locked or unreachable file, low memory), so "no picture" must not be
    /// stored for good. A file a decoder can't read as its kind (damaged, unsupported) is not transient.
    /// </summary>
    public static bool IsTransient(Exception e) => e switch
    {
        EndOfStreamException => false,
        IOException => true,
        UnauthorizedAccessException => true,
        OutOfMemoryException or InsufficientExecutionStackException => true,
        COMException c => (unchecked((uint)c.HResult) >> 16) == 0x8007 && unchecked((uint)c.HResult) != 0x80070057,
        ExternalException => true, // GDI+'s "generic error" is mostly memory or a locked file
        _ => false,
    };

    /// <summary>What a file is now: its stamp, and whether it is a folder. False when it is gone or can't be reached.</summary>
    public static bool Stat(string path, out ThumbStamp stamp, out bool isFolder) => Stat(path, out stamp, out isFolder, out _);

    // FILE_ATTRIBUTE_OFFLINE, FILE_ATTRIBUTE_RECALL_ON_OPEN, FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS.
    const int NotHereAttributes = 0x1000 | 0x40000 | 0x400000;

    /// <summary>
    /// <see cref="Stat(string, out ThumbStamp, out bool)"/>, and whether the file's contents are not on this PC (a cloud
    /// placeholder such as OneDrive's online-only files, or an offline file): reading it would download it, so only
    /// Windows, which asks the cloud provider for its thumbnail, may make its picture.
    /// </summary>
    public static bool Stat(string path, out ThumbStamp stamp, out bool isFolder, out bool notHere)
    {
        stamp = ThumbStamp.Unknown;
        isFolder = false;
        notHere = false;
        try
        {
            var fi = new FileInfo(path);
            var attrs = fi.Attributes; // throws FileNotFoundException when the file is gone
            if ((int)attrs == -1) return false;
            notHere = ((int)attrs & NotHereAttributes) != 0;
            long write = fi.LastWriteTimeUtc.ToFileTimeUtc();
            isFolder = attrs.HasFlag(FileAttributes.Directory);
            stamp = isFolder ? ThumbStamp.ForFolder(write) : new ThumbStamp(fi.Length, write);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            return false;
        }
    }
}

/// <summary>
/// File-type icons remembered per extension and size (measured: identical for every file of an extension), except kinds
/// whose icon belongs to the file and folders, which are asked for each time. Thread-safe; hands out copies.
/// </summary>
internal sealed class IconMemory
{
    // Each extension may be asked at many sizes (the thumbnail size moves in 24 px steps): past this many icons the
    // memory starts again rather than grow for the life of the program (a few MB at most).
    private const int MaxIcons = 128;

    private readonly object _lock = new();
    private readonly Dictionary<(string Ext, int Size), Bitmap?> _byExt = new();

    /// <summary>Icons fetched from Windows, and icons answered from memory (for the benchmark's counts).</summary>
    public long Fetched, Remembered;

    /// <summary>The icon for <paramref name="path"/> at <paramref name="size"/> (an owned copy), or null. Cache-only reads can run on any thread.</summary>
    public Bitmap? Get(string path, bool isFolder, int size, bool cacheOnly = false)
    {
        size = Math.Clamp(size, 16, 256);
        string ext = ThumbKinds.Ext(path);
        bool shared = !ThumbKinds.IconBelongsToFile(path, isFolder) && ext.Length > 0;
        if (shared)
        {
            lock (_lock)
            {
                if (_byExt.TryGetValue((ext, size), out var known))
                {
                    Interlocked.Increment(ref Remembered);
                    // A premultiplied copy, as every picture handed to the grid is (new Bitmap would give straight alpha).
                    if (known == null) return null;
                    return cacheOnly ? ThumbSources.TryToDisplay(known, size) : ThumbSources.ToDisplay(known, size);
                }
            }
        }
        if (cacheOnly) return null;
        Interlocked.Increment(ref Fetched);
        var icon = ThumbSources.Icon(path, size);
        Bitmap? shown = icon == null ? null : ThumbSources.ToDisplay(icon, size);
        icon?.Dispose();
        if (!shared || shown == null) return shown;
        lock (_lock)
        {
            if (!_byExt.ContainsKey((ext, size)))
            {
                if (_byExt.Count >= MaxIcons)
                {
                    foreach (var b in _byExt.Values) b?.Dispose();
                    _byExt.Clear();
                }
                _byExt[(ext, size)] = ThumbSources.ToDisplay(shown, size);
            }
        }
        return shown;
    }

    /// <summary>
    /// The icon asked of Windows once more, past the memory (a copy the caller owns), or null: the last try before a "no
    /// picture" answer would leave with no icon at all, which the grid draws as an empty frame. STA only.
    /// </summary>
    public Bitmap? Refetch(string path, int size)
    {
        size = Math.Clamp(size, 16, 256);
        Interlocked.Increment(ref Fetched);
        using var icon = ThumbSources.Icon(path, size);
        return icon == null ? null : ThumbSources.ToDisplay(icon, size);
    }

    /// <summary>Forgets every icon.</summary>
    public void Clear()
    {
        lock (_lock)
        {
            foreach (var b in _byExt.Values) b?.Dispose();
            _byExt.Clear();
        }
    }
}
