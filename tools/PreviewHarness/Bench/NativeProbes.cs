using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Security.Cryptography;

namespace PreviewHarness.Bench;

/// <summary>
/// Windows' own thumbnails through IShellItemImageFactory, with the HRESULT kept (the product's Shell.GetImageSta
/// drops it). Call on an STA thread.
/// </summary>
internal static class ShellProbe
{
    public const int ResizeToFit = 0x00, BiggerSizeOk = 0x01, MemoryOnly = 0x02, IconOnly = 0x04, ThumbnailOnly = 0x08, InCacheOnly = 0x10;

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP { public int bmType, bmWidth, bmHeight, bmWidthBytes; public ushort bmPlanes, bmBitsPixel; public IntPtr bmBits; }

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")] private static extern int GetObjectBitmap(IntPtr h, int c, ref BITMAP bm);

    public sealed record Result(int Hr, int Width, int Height, double Ms, double CreateMs, string? PixelHash);

    /// <summary>One GetImage call. <paramref name="hashPixels"/> also hashes the picture (to compare icons).</summary>
    public static Result Get(string path, int size, int flags, bool hashPixels = false)
    {
        var sw = Stopwatch.StartNew();
        object? obj = null;
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            var iid = new Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b");
            int hrc = SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out obj);
            double createMs = sw.Elapsed.TotalMilliseconds;
            if (hrc != 0 || obj is not IShellItemImageFactory f) return new Result(hrc, 0, 0, sw.Elapsed.TotalMilliseconds, createMs, null);
            int hr = f.GetImage(new SIZE { cx = size, cy = size }, flags, out hbmp);
            double ms = sw.Elapsed.TotalMilliseconds;
            if (hr != 0 || hbmp == IntPtr.Zero) return new Result(hr, 0, 0, ms, createMs, null);
            var bm = new BITMAP();
            GetObjectBitmap(hbmp, Marshal.SizeOf<BITMAP>(), ref bm);
            string? hash = null;
            if (hashPixels)
            {
                using var img = System.Drawing.Image.FromHbitmap(hbmp);
                using var b = new Bitmap(img);
                hash = HashPixels(b);
            }
            return new Result(hr, bm.bmWidth, Math.Abs(bm.bmHeight), ms, createMs, hash);
        }
        catch (Exception ex)
        {
            return new Result(ex.HResult == 0 ? -1 : ex.HResult, 0, 0, sw.Elapsed.TotalMilliseconds, 0, null);
        }
        finally
        {
            if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
            if (obj != null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
        }
    }

    public static string HashPixels(Bitmap b)
    {
        var d = b.LockBits(new Rectangle(0, 0, b.Width, b.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var buf = new byte[d.Stride * b.Height];
            Marshal.Copy(d.Scan0, buf, 0, buf.Length);
            return Convert.ToHexString(SHA1.HashData(buf))[..12] + $"@{b.Width}x{b.Height}";
        }
        finally { b.UnlockBits(d); }
    }

    public static string Hr(int hr) => "0x" + hr.ToString("X8");
}

/// <summary>
/// Windows Imaging Component used directly (harness only): decoder from a counted stream, then either a full decode
/// in strips or a scaled decode through IWICBitmapSourceTransform (the JPEG codec's DCT scaling).
/// </summary>
internal static class WicDirect
{
    [ComImport, Guid("ec5ec8a9-c395-4314-9c77-54d7a935ff70"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICImagingFactory
    {
        [PreserveSig] int CreateDecoderFromFilename([MarshalAs(UnmanagedType.LPWStr)] string name, IntPtr vendor, uint access, int options, out IWICBitmapDecoder dec);
        [PreserveSig] int CreateDecoderFromStream(IStream stream, IntPtr vendor, int options, out IWICBitmapDecoder dec);
        void CreateDecoderFromFileHandle();
        void CreateComponentInfo();
        void CreateDecoder();
        void CreateEncoder();
        void CreatePalette();
        void CreateFormatConverter();
        [PreserveSig] int CreateBitmapScaler(out IWICBitmapScaler scaler);
    }

    [ComImport, Guid("9EDDE9E7-8DEE-47ea-99DF-E6FAF2ED44BF"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapDecoder
    {
        void QueryCapability();
        void Initialize();
        [PreserveSig] int GetContainerFormat(out Guid g);
        void GetDecoderInfo();
        void CopyPalette();
        void GetMetadataQueryReader();
        [PreserveSig] int GetPreview(out IWICBitmapSource src);
        void GetColorContexts();
        [PreserveSig] int GetThumbnail(out IWICBitmapSource src);
        [PreserveSig] int GetFrameCount(out uint n);
        [PreserveSig] int GetFrame(uint i, out IWICBitmapSource frame);
    }

    [ComImport, Guid("00000120-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapSource
    {
        [PreserveSig] int GetSize(out uint w, out uint h);
        [PreserveSig] int GetPixelFormat(out Guid fmt);
        [PreserveSig] int GetResolution(out double x, out double y);
        void CopyPalette();
        [PreserveSig] int CopyPixels(IntPtr rect, uint stride, uint size, IntPtr buffer);
    }

    [ComImport, Guid("00000302-a8f2-4877-ba0a-fd2b6645fb94"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapScaler
    {
        [PreserveSig] int GetSize(out uint w, out uint h);
        [PreserveSig] int GetPixelFormat(out Guid fmt);
        [PreserveSig] int GetResolution(out double x, out double y);
        void CopyPalette();
        [PreserveSig] int CopyPixels(IntPtr rect, uint stride, uint size, IntPtr buffer);
        [PreserveSig] int Initialize(IWICBitmapSource src, uint w, uint h, int mode);
    }

    [ComImport, Guid("3B16811B-6A43-4ec9-B713-3D5A0C13B940"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IWICBitmapSourceTransform
    {
        [PreserveSig] int CopyPixels(IntPtr rect, uint w, uint h, ref Guid dstFormat, int transform, uint stride, uint size, IntPtr buffer);
        [PreserveSig] int GetClosestSize(ref uint w, ref uint h);
        [PreserveSig] int GetClosestPixelFormat(ref Guid fmt);
        [PreserveSig] int DoesSupportTransform(int transform, out int supported);
    }

    [StructLayout(LayoutKind.Sequential)] private struct WICRect { public int X, Y, W, H; }

    private static readonly Guid ClsidFactory = new("cacaf262-9370-4615-a13b-9f5539da4c0a");
    private static readonly Guid Bgr24 = new("6fddc324-4e03-4bfe-b185-3d77768dc90c");
    private const int CacheOnDemand = 0;

    [ThreadStatic] private static IWICImagingFactory? _factory;

    private static IWICImagingFactory Factory => _factory ??= (IWICImagingFactory)Activator.CreateInstance(Type.GetTypeFromCLSID(ClsidFactory)!)!;

    public sealed record Outcome(bool Ok, int Width, int Height, int DecodedWidth, int DecodedHeight, long PixelsDecoded, string Note);

    private static IWICBitmapDecoder? Open(Stream s, out ManagedIStream ist)
    {
        ist = new ManagedIStream(s);
        return Factory.CreateDecoderFromStream(ist, IntPtr.Zero, CacheOnDemand, out var dec) == 0 ? dec : null;
    }

    /// <summary>Decodes every pixel of frame 0 in strips of 256 rows (as 24-bit BGR), like a decoder that does not scale.</summary>
    public static Outcome FullDecode(Stream s)
    {
        var dec = Open(s, out _);
        if (dec == null) return new Outcome(false, 0, 0, 0, 0, 0, "no WIC decoder");
        try
        {
            if (dec.GetFrame(0, out var frame) != 0) return new Outcome(false, 0, 0, 0, 0, 0, "no frame");
            frame.GetSize(out uint w, out uint h);
            // Convert through the transform interface when it exists (asks for BGR24); else copy native format.
            uint stride = w * 8; // room for up to 64 bits per pixel (16-bit TIFF/PNG, RAW)
            const int strip = 256;
            var buf = Marshal.AllocHGlobal((IntPtr)(stride * strip));
            try
            {
                for (int y = 0; y < h; y += strip)
                {
                    var r = new WICRect { X = 0, Y = y, W = (int)w, H = (int)Math.Min(strip, h - y) };
                    var pr = Marshal.AllocHGlobal(Marshal.SizeOf<WICRect>());
                    try
                    {
                        Marshal.StructureToPtr(r, pr, false);
                        int hr = frame.CopyPixels(pr, stride, stride * (uint)r.H, buf);
                        if (hr != 0) return new Outcome(false, (int)w, (int)h, 0, 0, (long)w * y, "CopyPixels " + ShellProbe.Hr(hr));
                    }
                    finally { Marshal.FreeHGlobal(pr); }
                }
            }
            finally { Marshal.FreeHGlobal(buf); }
            Marshal.ReleaseComObject(frame);
            return new Outcome(true, (int)w, (int)h, (int)w, (int)h, (long)w * h, "full");
        }
        finally { Marshal.ReleaseComObject(dec); }
    }

    /// <summary>
    /// Scaled decode: asks the codec for the closest size it can produce natively at or above the box
    /// (IWICBitmapSourceTransform), then WIC's Fant scaler for the rest.
    /// </summary>
    public static Outcome ScaledDecode(Stream s, int box)
    {
        var dec = Open(s, out _);
        if (dec == null) return new Outcome(false, 0, 0, 0, 0, 0, "no WIC decoder");
        try
        {
            if (dec.GetFrame(0, out var frame) != 0) return new Outcome(false, 0, 0, 0, 0, 0, "no frame");
            frame.GetSize(out uint w, out uint h);
            double sc = Math.Min(1.0, Math.Min((double)box / w, (double)box / h));
            uint tw = Math.Max(1, (uint)Math.Round(w * sc)), th = Math.Max(1, (uint)Math.Round(h * sc));
            if (frame is not IWICBitmapSourceTransform t) { Marshal.ReleaseComObject(frame); return new Outcome(false, (int)w, (int)h, 0, 0, 0, "no source transform"); }
            // Smallest native size that is still at least the target.
            uint cw = tw, ch = th;
            t.GetClosestSize(ref cw, ref ch);
            if (cw < tw || ch < th)
            {
                // The codec rounded down: step back up through the powers of two it offers.
                for (int div = 8; div >= 1; div /= 2)
                {
                    uint dw = (uint)Math.Ceiling(w / (double)div), dh = (uint)Math.Ceiling(h / (double)div);
                    uint qw = dw, qh = dh;
                    t.GetClosestSize(ref qw, ref qh);
                    if (qw >= tw && qh >= th) { cw = qw; ch = qh; break; }
                }
            }
            var fmt = Bgr24;
            t.GetClosestPixelFormat(ref fmt);
            int bpp = fmt == Bgr24 ? 3 : 8;
            uint stride = (cw * (uint)bpp + 3) & ~3u;
            long bytes = (long)stride * ch;
            var buf = Marshal.AllocHGlobal((IntPtr)bytes);
            try
            {
                int hr = t.CopyPixels(IntPtr.Zero, cw, ch, ref fmt, 0, stride, (uint)bytes, buf);
                if (hr != 0) return new Outcome(false, (int)w, (int)h, 0, 0, 0, "transform CopyPixels " + ShellProbe.Hr(hr));
                // The final Fant step to the exact box is cheap on a small bitmap; done in managed code here
                // (a box filter over the strip) so that the probe stays a measure of the codec's work.
                return new Outcome(true, (int)w, (int)h, (int)cw, (int)ch, (long)cw * ch, $"native {cw}x{ch} then scale to {tw}x{th}");
            }
            finally
            {
                Marshal.FreeHGlobal(buf);
                Marshal.ReleaseComObject(frame);
            }
        }
        finally { Marshal.ReleaseComObject(dec); }
    }

    /// <summary>WIC's own scaler on frame 0 (what WPF's TransformedBitmap / DecodePixelWidth use underneath).</summary>
    public static Outcome ScalerDecode(Stream s, int box)
    {
        var dec = Open(s, out _);
        if (dec == null) return new Outcome(false, 0, 0, 0, 0, 0, "no WIC decoder");
        try
        {
            if (dec.GetFrame(0, out var frame) != 0) return new Outcome(false, 0, 0, 0, 0, 0, "no frame");
            frame.GetSize(out uint w, out uint h);
            double sc = Math.Min(1.0, Math.Min((double)box / w, (double)box / h));
            uint tw = Math.Max(1, (uint)Math.Round(w * sc)), th = Math.Max(1, (uint)Math.Round(h * sc));
            if (Factory.CreateBitmapScaler(out var scaler) != 0) return new Outcome(false, (int)w, (int)h, 0, 0, 0, "no scaler");
            try
            {
                int hr = scaler.Initialize(frame, tw, th, 3 /* Fant */);
                if (hr != 0) return new Outcome(false, (int)w, (int)h, 0, 0, 0, "scaler init " + ShellProbe.Hr(hr));
                scaler.GetPixelFormat(out _);
                uint stride = (tw * 4 + 3) & ~3u;
                var buf = Marshal.AllocHGlobal((IntPtr)(stride * th));
                try
                {
                    hr = scaler.CopyPixels(IntPtr.Zero, stride, stride * th, buf);
                    if (hr != 0) return new Outcome(false, (int)w, (int)h, 0, 0, 0, "scaler CopyPixels " + ShellProbe.Hr(hr));
                }
                finally { Marshal.FreeHGlobal(buf); }
                return new Outcome(true, (int)w, (int)h, (int)tw, (int)th, (long)tw * th, "WIC Fant scaler");
            }
            finally
            {
                Marshal.ReleaseComObject(scaler);
                Marshal.ReleaseComObject(frame);
            }
        }
        finally { Marshal.ReleaseComObject(dec); }
    }

    /// <summary>A managed stream handed to WIC as an IStream (reads only).</summary>
    [ComVisible(true)]
    private sealed class ManagedIStream : IStream
    {
        private readonly Stream _s;
        public ManagedIStream(Stream s) { _s = s; }

        public void Read(byte[] pv, int cb, IntPtr pcbRead)
        {
            int total = 0;
            while (total < cb)
            {
                int n = _s.Read(pv, total, cb - total);
                if (n <= 0) break;
                total += n;
            }
            if (pcbRead != IntPtr.Zero) Marshal.WriteInt32(pcbRead, total);
        }

        public void Seek(long dlibMove, int dwOrigin, IntPtr plibNewPosition)
        {
            long p = _s.Seek(dlibMove, (SeekOrigin)dwOrigin);
            if (plibNewPosition != IntPtr.Zero) Marshal.WriteInt64(plibNewPosition, p);
        }

        public void Stat(out STATSTG pstatstg, int grfStatFlag) => pstatstg = new STATSTG { type = 2, cbSize = _s.Length, grfMode = 0 };

        public void Write(byte[] pv, int cb, IntPtr pcbWritten) => throw new COMException("read only", unchecked((int)0x80030005));
        public void SetSize(long libNewSize) => throw new COMException("read only", unchecked((int)0x80030005));
        public void CopyTo(IStream pstm, long cb, IntPtr pcbRead, IntPtr pcbWritten) => throw new NotImplementedException();
        public void Commit(int grfCommitFlags) { }
        public void Revert() { }
        public void LockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("", unchecked((int)0x80030001));
        public void UnlockRegion(long libOffset, long cb, int dwLockType) => throw new COMException("", unchecked((int)0x80030001));
        public void Clone(out IStream ppstm) => throw new NotImplementedException();
    }
}
