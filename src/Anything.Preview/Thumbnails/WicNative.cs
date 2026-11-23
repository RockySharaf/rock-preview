using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using Anything.Core.Thumbs;
using Anything.Preview.Util;

namespace Anything.Preview.Thumbnails;

/// <summary>
/// Windows Imaging Component called directly, for the thumbnail service's busy paths: decoding a picture at the size
/// class, its EXIF thumbnail, a stored master, and scaling a picture to the grid's size. Not through WPF's imaging
/// classes: each of their bitmaps reports its native memory to the garbage collector, and at the rate the service
/// decodes that forced a full collection about once per picture (measured: 283 to 365 in a 7-second scroll), and every
/// collection stops the UI thread too. Not through GDI+ for scaling either: GDI+ lets one call run at a time in the
/// process, so a worker's high-quality resize made the grid's painting wait (captions 0.1 ms alone, 6 ms with four
/// workers resizing). Files are read through handles Rock Preview opens itself and stored bytes
/// through WIC's own memory stream: no codec calls back into managed code. The caller owns every bitmap returned; each
/// is 32bpp premultiplied. Any thread (the factory is one per thread); a codec's failure comes out as a COMException.
/// </summary>
internal static class WicNative
{
    private const int CacheOnDemand = 0;                 // WICDecodeMetadataCacheOnDemand
    private const int Fant = 3;                           // WICBitmapInterpolationModeFant: WPF's choice for decoding
    private const int ComponentNotFound = unchecked((int)0x88982F50); // WINCODEC_ERR_COMPONENTNOTFOUND: no codec

    private static readonly Guid ClsidFactory = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    private static readonly Guid Pbgra32 = new("6fddc324-4e03-4bfe-b185-3d77768dc910");
    private static readonly Guid Bgra32 = new("6fddc324-4e03-4bfe-b185-3d77768dc90f");
    private static readonly Guid Bgr32 = new("6fddc324-4e03-4bfe-b185-3d77768dc90e");
    private static readonly Guid Bgr24 = new("6fddc324-4e03-4bfe-b185-3d77768dc90c");
    private static readonly Guid ContainerJpeg = new("19e4a5aa-5662-4fc5-a0c0-1758028e1057"); // GUID_ContainerFormatJpeg

    [ThreadStatic] private static IWICImagingFactory? _factory;

    private static IWICImagingFactory Factory => _factory ??= (IWICImagingFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidFactory, throwOnError: true)!)!;

    // ── Decoding ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// What <see cref="WicImage.DecodeThumbnail"/> promises, done directly: the file opened once, decoded straight to
    /// <paramref name="sizeClass"/> on its long side (a baseline JPEG, as its decoder and its markers say, whatever its
    /// name, scaled by the codec as it decodes), the EXIF orientation applied; a RAW's embedded preview when it is large enough for the class; one
    /// frame of an animated or multi-page file (for icons the biggest). A full-frame decode over 64 megapixels waits for
    /// <paramref name="largeGate"/> (or, with <paramref name="waitForGate"/> false, answers Busy when it is taken), and one
    /// over 400 megapixels is refused.
    /// </summary>
    public static Bitmap? DecodeThumbnail(string path, string ext, int sizeClass, SemaphoreSlim? largeGate, out WicImage.ThumbStatus status,
        out int sourceLongSide, bool waitForGate)
    {
        status = WicImage.ThumbStatus.NoCodec;
        sourceLongSide = 0;
        using var file = OpenFile(path);
        bool isRaw = WicImage.RawExtensions.Contains(ext);
        // A progressive JPEG can't be scaled while it is read: its codec holds every coefficient of the whole picture
        // first (measured: 3 KB claiming 65500 x 65500 took 24 s and 5.8 GB), so it obeys the large-decode rule. The
        // markers are read for every file, whatever its name, before the decoder has the handle (the reads move its
        // file pointer): for a file that isn't a JPEG that is one 2-byte read of a page the decoder reads anyway.
        var jpegFrame = ThumbKinds.JpegFrameOf((offset, buffer) => RandomAccess.Read(file, buffer, offset));
        var dec = OpenDecoder(file);
        if (dec == null) return null;
        try
        {
            // The decoder's own word for what the file is: a misnamed PNG or TIFF is not let past the limits as a JPEG.
            bool containerIsJpeg = dec.GetContainerFormat(out var container) == 0 && container == ContainerJpeg;
            Check(dec.GetFrameCount(out uint frames));
            if (frames == 0) return null;
            uint index = ext is ".ico" or ".cur" ? BiggestFrame(dec, frames) : 0;
            Check(dec.GetFrame(index, out var frame));
            try
            {
                Check(frame.GetSize(out uint ow, out uint oh));
                sourceLongSide = (int)Math.Min(int.MaxValue, Math.Max(ow, oh));
                int orientation = Orientation(frame);
                var obox = BitmapUtil.OrientBox(new Size(sizeClass, sizeClass), orientation);
                Bitmap? bmp = null;
                if (isRaw)
                {
                    bmp = RawPreview(dec, obox, sizeClass, sourceLongSide, largeGate, waitForGate, out status);
                    if (status == WicImage.ThumbStatus.Busy) return null;
                }
                if (bmp == null)
                {
                    // A baseline JPEG is scaled by its codec while it decodes (at 1/2 to 1/8): not a full-frame decode. A RAW
                    // codec demosaics the whole sensor first, and every other kind decodes every pixel before it scales.
                    bool fullFrame = !ThumbKinds.ScaledWhileRead(containerIsJpeg, frames, jpegFrame);
                    using var lease = fullFrame ? Admit(ow, oh, largeGate, waitForGate, out status) : null;
                    if (status is WicImage.ThumbStatus.Refused or WicImage.ThumbStatus.Busy) return null;
                    bmp = Scaled(frame, BitmapUtil.FitSize(new Size((int)Math.Min(ow, int.MaxValue), (int)Math.Min(oh, int.MaxValue)), obox));
                }
                bmp = Oriented(bmp, orientation);
                status = WicImage.ThumbStatus.Ok;
                return bmp;
            }
            finally { Release(frame); }
        }
        finally { Release(dec); }
    }

    /// <summary>The EXIF thumbnail of a JPEG (or a RAW's thumbnail), turned by the orientation; null when there is none.</summary>
    public static Bitmap? EmbeddedThumbnail(string path)
    {
        using var file = OpenFile(path);
        var dec = OpenDecoder(file);
        if (dec == null) return null;
        try
        {
            Check(dec.GetFrameCount(out uint frames));
            if (frames == 0) return null;
            Check(dec.GetFrame(0, out var frame));
            try
            {
                IWICBitmapSource? thumb = null;
                if (frame.GetThumbnail(out var t) == 0) thumb = t;
                else if (dec.GetThumbnail(out var d) == 0) thumb = d;
                if (thumb == null) return null;
                try
                {
                    Check(thumb.GetSize(out uint w, out uint h));
                    // An EXIF thumbnail is at most 160 px; anything claiming more than a master is not a thumbnail.
                    if (w == 0 || h == 0 || w > 1024 || h > 1024) return null;
                    return Oriented(Scaled(thumb, new Size((int)w, (int)h)), Orientation(frame));
                }
                finally { Release(thumb); }
            }
            finally { Release(frame); }
        }
        finally { Release(dec); }
    }

    /// <summary>
    /// A stored master decoded and scaled to fit <paramref name="size"/>; null when the bytes don't decode as a picture
    /// at most <paramref name="maxSide"/> on a side (the header is read before any pixel).
    /// </summary>
    public static unsafe Bitmap? DecodeStored(ReadOnlyMemory<byte> payload, int size, int maxSide)
    {
        if (payload.IsEmpty) return null;
        // WIC's own stream over the bytes, pinned until every object that may read them is released: no codec calls back
        // into managed code.
        using var pin = payload.Pin();
        Check(Factory.CreateStream(out var stream));
        IWICBitmapDecoder? dec = null;
        try
        {
            Check(stream.InitializeFromMemory((IntPtr)pin.Pointer, (uint)payload.Length));
            int hr = Factory.CreateDecoderFromStream((IStream)stream, IntPtr.Zero, CacheOnDemand, out dec);
            if (hr == ComponentNotFound) return null;
            Check(hr);
            return StoredFrame(dec, size, maxSide);
        }
        finally
        {
            Release(dec);
            Release(stream);
        }
    }

    /// <summary>The first frame of a stored master, scaled to fit <paramref name="size"/>; null when it is no thumbnail.</summary>
    private static Bitmap? StoredFrame(IWICBitmapDecoder dec, int size, int maxSide)
    {
        {
            Check(dec.GetFrameCount(out uint frames));
            if (frames == 0) return null;
            Check(dec.GetFrame(0, out var frame));
            try
            {
                Check(frame.GetSize(out uint w, out uint h));
                if (w == 0 || h == 0 || w > maxSide || h > maxSide) return null;
                return Scaled(frame, BitmapUtil.FitSize(new Size((int)w, (int)h), new Size(size, size)));
            }
            finally { Release(frame); }
        }
    }

    // ── Scaling ──────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// A copy of <paramref name="src"/> that fits in <paramref name="size"/>×<paramref name="size"/> (never enlarged),
    /// 32bpp premultiplied, scaled by WIC (GDI+ only reads and writes the pixels).
    /// </summary>
    public static Bitmap Fit(Bitmap src, int size)
    {
        var fit = BitmapUtil.FitSize(src.Size, new Size(size, size));
        var source = FromBitmap(src);
        try { return Scaled(source, fit); }
        finally { Release(source); }
    }

    /// <summary><see cref="Fit"/> that hands back <paramref name="src"/> itself when it fits already (it then keeps it; else it is disposed).</summary>
    public static Bitmap FitOrSelf(Bitmap src, int size)
    {
        var fit = BitmapUtil.FitSize(src.Size, new Size(size, size));
        if (fit.Width >= src.Width && fit.Height >= src.Height) return src;
        var scaled = Fit(src, size);
        src.Dispose();
        return scaled;
    }

    /// <summary>A WIC bitmap holding a copy of a GDI+ bitmap's pixels (in its own format when WIC has it, else as premultiplied 32 bit).</summary>
    private static IWICBitmapSource FromBitmap(Bitmap src)
    {
        var (gdi, wic) = src.PixelFormat switch
        {
            PixelFormat.Format32bppPArgb => (PixelFormat.Format32bppPArgb, Pbgra32),
            PixelFormat.Format32bppArgb => (PixelFormat.Format32bppArgb, Bgra32),
            PixelFormat.Format32bppRgb => (PixelFormat.Format32bppRgb, Bgr32),
            PixelFormat.Format24bppRgb => (PixelFormat.Format24bppRgb, Bgr24),
            _ => (PixelFormat.Format32bppPArgb, Pbgra32),
        };
        int w = src.Width, h = src.Height;
        var data = src.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, gdi);
        try
        {
            if (data.Stride <= 0) throw new ArgumentException("a bottom-up bitmap");
            Check(Factory.CreateBitmapFromMemory((uint)w, (uint)h, ref wic, (uint)data.Stride, (uint)((long)data.Stride * h), data.Scan0, out var bitmap));
            return bitmap;
        }
        finally { src.UnlockBits(data); }
    }

    /// <summary><paramref name="source"/> scaled to <paramref name="fit"/> (Fant; as it is when the size is its own), in a new 32bpp premultiplied bitmap.</summary>
    private static Bitmap Scaled(IWICBitmapSource source, Size fit)
    {
        Check(source.GetSize(out uint sw, out uint sh));
        uint w = (uint)Math.Max(1, fit.Width), h = (uint)Math.Max(1, fit.Height);
        IWICBitmapSource? scaler = null;
        IWICFormatConverter? converter = null;
        try
        {
            IWICBitmapSource step = source;
            if (w != sw || h != sh)
            {
                Check(Factory.CreateBitmapScaler(out var s));
                scaler = s;
                Check(s.Initialize(source, w, h, Fant));
                step = s;
            }
            Check(Factory.CreateFormatConverter(out converter));
            var format = Pbgra32;
            Check(converter.Initialize(step, ref format, 0, IntPtr.Zero, 0.0, 0));
            var bmp = new Bitmap((int)w, (int)h, PixelFormat.Format32bppPArgb);
            var data = bmp.LockBits(new Rectangle(0, 0, (int)w, (int)h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            int hr;
            try { hr = converter.CopyPixels(IntPtr.Zero, (uint)data.Stride, (uint)((long)data.Stride * h), data.Scan0); }
            finally { bmp.UnlockBits(data); }
            if (hr != 0)
            {
                bmp.Dispose();
                Marshal.ThrowExceptionForHR(hr);
            }
            return bmp;
        }
        finally
        {
            if (converter != null) Release(converter);
            if (scaler != null) Release(scaler);
        }
    }

    // ── Pieces ───────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The file opened as every reader here opens it: other programs may still rename, delete and overwrite it while it
    /// is decoded (WIC reads through the handle and doesn't map the file). The handle is closed only after every WIC
    /// object made from it is released (the callers' using blocks end after their finally blocks).
    /// </summary>
    private static SafeFileHandle OpenFile(string path)
        => File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);

    /// <summary>
    /// A decoder reading the file through its handle; null when no installed codec reads it. Through the handle and not
    /// a managed stream: no codec then calls back into managed code, a file cut short while it is read ends the decode at
    /// once (a managed stream answering "0 bytes" kept the PNG codec asking for 20 s), and a PNG is read once, not twice.
    /// </summary>
    private static IWICBitmapDecoder? OpenDecoder(SafeFileHandle file)
    {
        // Reading the JPEG's markers moved the file pointer: the decoder starts at the beginning.
        if (!SetFilePointerEx(file, 0, IntPtr.Zero, 0)) Marshal.ThrowExceptionForHR(Marshal.GetHRForLastWin32Error());
        int hr = Factory.CreateDecoderFromFileHandle(file.DangerousGetHandle(), IntPtr.Zero, CacheOnDemand, out var dec);
        if (hr == ComponentNotFound) return null;
        Check(hr);
        return dec;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFilePointerEx(SafeFileHandle file, long distance, IntPtr newPosition, int moveMethod);

    /// <summary>The biggest frame of an icon (then the deepest colour), as Windows shows it.</summary>
    private static uint BiggestFrame(IWICBitmapDecoder dec, uint frames)
    {
        uint best = 0;
        long bestScore = -1;
        for (uint i = 0; i < frames; i++)
        {
            if (dec.GetFrame(i, out var f) != 0) continue;
            try
            {
                if (f.GetSize(out uint w, out uint h) != 0) continue;
                f.GetPixelFormat(out var format);
                long score = (long)w * h * 64 + BitsPerPixel(format);
                if (score > bestScore) { bestScore = score; best = i; }
            }
            finally { Release(f); }
        }
        return best;
    }

    // The pixel formats icons come in ({6fddc324-4e03-4bfe-b185-3d77768dc9xx}, told apart by their last byte); others count as 16.
    private static int BitsPerPixel(Guid format)
    {
        Span<byte> b = stackalloc byte[16], t = stackalloc byte[16];
        format.TryWriteBytes(b);
        Pbgra32.TryWriteBytes(t);
        if (!b[..15].SequenceEqual(t[..15])) return 16;
        return b[15] switch
        {
            0x01 or 0x05 => 1, 0x02 or 0x06 => 2, 0x03 or 0x07 => 4, 0x04 or 0x08 => 8,
            0x0c or 0x0d => 24, 0x0e or 0x0f or 0x10 => 32,
            _ => 16,
        };
    }

    /// <summary>
    /// A RAW's embedded preview, scaled to the box, when it is large enough for the class; null otherwise (the RAW itself
    /// is then decoded). The preview obeys the large-decode rule on its own size, as any full-frame decode does: over 400
    /// megapixels it is not used, over 64 it holds the gate while it is scaled (Busy, and null, when the gate is taken
    /// and <paramref name="wait"/> is false).
    /// </summary>
    private static Bitmap? RawPreview(IWICBitmapDecoder dec, Size box, int sizeClass, int sourceLongSide, SemaphoreSlim? largeGate, bool wait,
        out WicImage.ThumbStatus status)
    {
        status = WicImage.ThumbStatus.Ok;
        try
        {
            if (dec.GetPreview(out var prev) != 0 || prev == null) return null;
            try
            {
                if (prev.GetSize(out uint w, out uint h) != 0 || w == 0 || h == 0) return null;
                if (ThumbKinds.QualityOf((int)Math.Min(int.MaxValue, Math.Max(w, h)), sizeClass, sourceLongSide) != ThumbQuality.Final) return null;
                using var lease = Admit(w, h, largeGate, wait, out var admitted);
                if (admitted == WicImage.ThumbStatus.Busy) status = admitted;
                // Refused: the RAW takes the normal path, which has its own admission.
                if (admitted != WicImage.ThumbStatus.Ok) return null;
                return Scaled(prev, BitmapUtil.FitSize(new Size((int)Math.Min(w, int.MaxValue), (int)Math.Min(h, int.MaxValue)), box));
            }
            finally { Release(prev); }
        }
        catch (COMException)
        {
            return null; // a preview the codec can't give: the RAW itself is decoded
        }
    }

    /// <summary>The EXIF orientation (1 to 8) of a frame; 1 when it has none or its kind has no metadata.</summary>
    private static int Orientation(IWICBitmapFrameDecode frame)
    {
        if (frame.GetMetadataQueryReader(out var reader) != 0 || reader == null) return 1;
        var value = new PropVariant();
        try
        {
            if (reader.GetMetadataByName("System.Photo.Orientation", ref value) != 0) return 1;
            int o = value.Type switch
            {
                18 => value.UShort, // VT_UI2
                19 or 3 => value.Int, // VT_UI4, VT_I4
                2 => value.UShort,  // VT_I2
                _ => 1,
            };
            return o is >= 1 and <= 8 ? o : 1;
        }
        catch (COMException)
        {
            return 1;
        }
        finally
        {
            PropVariantClear(ref value);
            Release(reader);
        }
    }

    /// <summary>
    /// <paramref name="bmp"/> turned and mirrored as its EXIF orientation says (the pixels are moved here, not by GDI+);
    /// the same bitmap when there is nothing to do, else a new one (the old one is disposed).
    /// </summary>
    private static unsafe Bitmap Oriented(Bitmap bmp, int orientation)
    {
        if (orientation is < 2 or > 8) return bmp;
        int w = bmp.Width, h = bmp.Height;
        bool swap = orientation >= 5;
        int dw = swap ? h : w, dh = swap ? w : h;
        var dst = new Bitmap(dw, dh, PixelFormat.Format32bppPArgb);
        var s = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
        var d = dst.LockBits(new Rectangle(0, 0, dw, dh), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
        try
        {
            byte* sp = (byte*)s.Scan0, dp = (byte*)d.Scan0;
            for (int y = 0; y < dh; y++)
            {
                uint* row = (uint*)(dp + (long)y * d.Stride);
                for (int x = 0; x < dw; x++)
                {
                    // Where the pixel at (x, y) of the turned picture comes from in the stored one.
                    var (sx, sy) = orientation switch
                    {
                        2 => (w - 1 - x, y),          // mirrored
                        3 => (w - 1 - x, h - 1 - y),  // upside down
                        4 => (x, h - 1 - y),          // mirrored upside down
                        5 => (y, x),                  // mirrored, turned left
                        6 => (y, h - 1 - x),          // turned right
                        7 => (w - 1 - y, h - 1 - x),  // mirrored, turned right
                        _ => (w - 1 - y, x),          // 8: turned left
                    };
                    row[x] = *(uint*)(sp + (long)sy * s.Stride + sx * 4);
                }
            }
        }
        finally
        {
            bmp.UnlockBits(s);
            dst.UnlockBits(d);
        }
        bmp.Dispose();
        return dst;
    }

    /// <summary>The large-decode rule for a full-frame decode: run, wait for the one large decode, or refuse.</summary>
    private static IDisposable? Admit(long width, long height, SemaphoreSlim? largeGate, bool wait, out WicImage.ThumbStatus status)
    {
        status = WicImage.ThumbStatus.Ok;
        switch (ThumbKinds.AdmitDecode(width, height))
        {
            case DecodeAdmission.Refused:
                status = WicImage.ThumbStatus.Refused;
                return null;
            case DecodeAdmission.LargeGate when largeGate != null:
                if (!wait)
                {
                    if (!largeGate.Wait(0))
                    {
                        status = WicImage.ThumbStatus.Busy;
                        return null;
                    }
                }
                else largeGate.Wait();
                return new GateLease(largeGate);
            default:
                return null;
        }
    }

    private sealed class GateLease(SemaphoreSlim gate) : IDisposable
    {
        private SemaphoreSlim? _gate = gate;
        public void Dispose() => Interlocked.Exchange(ref _gate, null)?.Release();
    }

    private static void Check(int hr)
    {
        if (hr < 0) Marshal.ThrowExceptionForHR(hr);
    }

    private static void Release(object? com)
    {
        if (com != null && Marshal.IsComObject(com)) Marshal.ReleaseComObject(com);
    }

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant pvar);

    /// <summary>A PROPVARIANT, enough of it for the small numbers the orientation comes in.</summary>
    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public ushort UShort;
        [FieldOffset(8)] public int Int;
        [FieldOffset(8)] public IntPtr Pointer;
        [FieldOffset(16)] public IntPtr Pointer2;
    }

    // ── COM ──────────────────────────────────────────────────────────────────────────────────────────────────────────
    // Only the methods used are typed; the others hold their place in the interface's order.

    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICImagingFactory
    {
        void CreateDecoderFromFilename();
        [PreserveSig] int CreateDecoderFromStream(IStream stream, IntPtr vendor, int options, out IWICBitmapDecoder dec);
        [PreserveSig] int CreateDecoderFromFileHandle(IntPtr file, IntPtr vendor, int options, out IWICBitmapDecoder dec);
        void CreateComponentInfo();
        void CreateDecoder();
        void CreateEncoder();
        void CreatePalette();
        [PreserveSig] int CreateFormatConverter(out IWICFormatConverter converter);
        [PreserveSig] int CreateBitmapScaler(out IWICBitmapScaler scaler);
        void CreateBitmapClipper();
        void CreateBitmapFlipRotator();
        [PreserveSig] int CreateStream(out IWICStream stream);
        void CreateColorContext();
        void CreateColorTransformer();
        void CreateBitmap();
        void CreateBitmapFromSource();
        void CreateBitmapFromSourceRect();
        [PreserveSig] int CreateBitmapFromMemory(uint width, uint height, ref Guid format, uint stride, uint size, IntPtr buffer, out IWICBitmapSource bitmap);
    }

    [ComImport, Guid("9EDDE9E7-8DEE-47ea-99DF-E6FAF2ED44BF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapDecoder
    {
        void QueryCapability();
        void Initialize();
        [PreserveSig] int GetContainerFormat(out Guid format);
        void GetDecoderInfo();
        void CopyPalette();
        void GetMetadataQueryReader();
        [PreserveSig] int GetPreview(out IWICBitmapSource source);
        void GetColorContexts();
        [PreserveSig] int GetThumbnail(out IWICBitmapSource source);
        [PreserveSig] int GetFrameCount(out uint count);
        [PreserveSig] int GetFrame(uint index, out IWICBitmapFrameDecode frame);
    }

    [ComImport, Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapSource
    {
        [PreserveSig] int GetSize(out uint width, out uint height);
        [PreserveSig] int GetPixelFormat(out Guid format);
        void GetResolution();
        void CopyPalette();
        [PreserveSig] int CopyPixels(IntPtr rect, uint stride, uint size, IntPtr buffer);
    }

    [ComImport, Guid("3B16811B-6A43-4ec9-A813-3D930C13B940"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapFrameDecode : IWICBitmapSource
    {
        [PreserveSig] new int GetSize(out uint width, out uint height);
        [PreserveSig] new int GetPixelFormat(out Guid format);
        new void GetResolution();
        new void CopyPalette();
        [PreserveSig] new int CopyPixels(IntPtr rect, uint stride, uint size, IntPtr buffer);
        [PreserveSig] int GetMetadataQueryReader(out IWICMetadataQueryReader reader);
        void GetColorContexts();
        [PreserveSig] int GetThumbnail(out IWICBitmapSource thumbnail);
    }

    [ComImport, Guid("00000302-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapScaler : IWICBitmapSource
    {
        [PreserveSig] new int GetSize(out uint width, out uint height);
        [PreserveSig] new int GetPixelFormat(out Guid format);
        new void GetResolution();
        new void CopyPalette();
        [PreserveSig] new int CopyPixels(IntPtr rect, uint stride, uint size, IntPtr buffer);
        [PreserveSig] int Initialize(IWICBitmapSource source, uint width, uint height, int mode);
    }

    [ComImport, Guid("00000301-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICFormatConverter : IWICBitmapSource
    {
        [PreserveSig] new int GetSize(out uint width, out uint height);
        [PreserveSig] new int GetPixelFormat(out Guid format);
        new void GetResolution();
        new void CopyPalette();
        [PreserveSig] new int CopyPixels(IntPtr rect, uint stride, uint size, IntPtr buffer);
        [PreserveSig] int Initialize(IWICBitmapSource source, ref Guid format, int dither, IntPtr palette, double alphaThreshold, int paletteTranslate);
    }

    [ComImport, Guid("30989668-E1C9-4597-B395-458EEDB808DF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICMetadataQueryReader
    {
        void GetContainerFormat();
        void GetLocation();
        [PreserveSig] int GetMetadataByName([MarshalAs(UnmanagedType.LPWStr)] string name, ref PropVariant value);
    }

    /// <summary>WIC's own stream (over memory here), handed to a decoder as the IStream it is.</summary>
    [ComImport, Guid("135FF860-22B7-4ddf-B0F6-218F4F299A43"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICStream
    {
        // IStream's methods hold their places; none is called from here.
        void Read();
        void Write();
        void Seek();
        void SetSize();
        void CopyTo();
        void Commit();
        void Revert();
        void LockRegion();
        void UnlockRegion();
        void Stat();
        void Clone();
        void InitializeFromIStream();
        void InitializeFromFilename();
        [PreserveSig] int InitializeFromMemory(IntPtr buffer, uint size);
    }
}
