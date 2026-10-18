using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using Anything.Preview.Util;

namespace Anything.Preview.Interop;

/// <summary>Shell thumbnails/icons (IShellItemImageFactory), type names and the property system. STA-pool backed.</summary>
internal static class Shell
{
    [Flags]
    public enum SIIGBF
    {
        ResizeToFit = 0x00,
        BiggerSizeOk = 0x01,
        MemoryOnly = 0x02,
        IconOnly = 0x04,
        ThumbnailOnly = 0x08,
        InCacheOnly = 0x10,
        CropToSquare = 0x20,
        WideThumbnails = 0x40,
        IconBackground = 0x80,
        ScaleUp = 0x100,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(SIZE size, SIIGBF flags, out IntPtr phbm);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr hObject);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAP
    {
        public int bmType, bmWidth, bmHeight, bmWidthBytes;
        public ushort bmPlanes, bmBitsPixel;
        public IntPtr bmBits;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth, biHeight;
        public ushort biPlanes, biBitCount;
        public uint biCompression, biSizeImage;
        public int biXPelsPerMeter, biYPelsPerMeter;
        public uint biClrUsed, biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DIBSECTION
    {
        public BITMAP dsBm;
        public BITMAPINFOHEADER dsBmih;
        public uint dsBitfields0, dsBitfields1, dsBitfields2;
        public IntPtr dshSection;
        public uint dsOffset;
    }

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObjectDib(IntPtr h, int c, ref DIBSECTION pv);

    private static readonly Guid IID_IShellItemImageFactory = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");
    private static readonly Guid IID_IShellItem = new("43826d1e-e718-42ee-bc55-a1e261c37bfe");

    /// <summary>Must be called on an STA thread.</summary>
    public static Bitmap? GetImageSta(string path, int size, SIIGBF flags)
    {
        object? obj = null;
        IntPtr hbmp = IntPtr.Zero;
        try
        {
            var iid = IID_IShellItemImageFactory;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out obj) != 0 || obj is not IShellItemImageFactory f)
                return null;
            int hr = f.GetImage(new SIZE { cx = size, cy = size }, flags, out hbmp);
            if (hr != 0 || hbmp == IntPtr.Zero) return null;
            return HBitmapToBitmap(hbmp);
        }
        catch
        {
            return null;
        }
        finally
        {
            if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
            if (obj != null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
        }
    }

    /// <summary>Real thumbnail only (no icon). Null when there is no thumbnail provider.</summary>
    public static Task<Bitmap?> GetThumbnailAsync(string path, int size, CancellationToken ct, bool highPriority = true, bool biggerOk = false)
    {
        size = Math.Clamp(size, 16, 2560);
        var flags = SIIGBF.ThumbnailOnly | (biggerOk ? SIIGBF.BiggerSizeOk : 0);
        return StaPool.Shared.RunAsync(() => GetImageSta(path, size, flags), ct, highPriority, 12000);
    }

    /// <summary>The file-type / folder icon at the given size (jumbo icons scale up to 256).</summary>
    public static Task<Bitmap?> GetIconAsync(string path, int size, CancellationToken ct, bool highPriority = true)
    {
        size = Math.Clamp(size, 16, 256);
        return StaPool.Shared.RunAsync(() => GetImageSta(path, size, SIIGBF.IconOnly), ct, highPriority, 8000);
    }

    /// <summary>Converts a (possibly 32bpp premultiplied alpha) DIB section to a GDI+ bitmap without losing alpha.</summary>
    private static Bitmap? HBitmapToBitmap(IntPtr hbmp)
    {
        var ds = new DIBSECTION();
        int got = GetObjectDib(hbmp, Marshal.SizeOf<DIBSECTION>(), ref ds);
        if (got >= Marshal.SizeOf<BITMAP>() && ds.dsBm.bmBitsPixel == 32 && ds.dsBm.bmBits != IntPtr.Zero)
        {
            int w = ds.dsBm.bmWidth, h = Math.Abs(ds.dsBm.bmHeight);
            // The shell hands out straight (non-premultiplied) alpha: fully transparent pixels may carry white RGB.
            var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            var bd = bmp.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
            bool anyAlpha = false;
            unsafe
            {
                // Let GDI deliver the rows top-down (negative height) instead of guessing the DIB's orientation from
                // the header — shell thumbnails came out upside down that way. 32-bit BI_RGB copies keep the alpha byte.
                var bmi = new BITMAPINFOHEADER { biSize = 40, biWidth = w, biHeight = -h, biPlanes = 1, biBitCount = 32 };
                IntPtr screen = GetDC(IntPtr.Zero);
                int lines;
                try { lines = GetDIBits(screen, hbmp, 0, (uint)h, bd.Scan0, ref bmi, 0); }
                finally { ReleaseDC(IntPtr.Zero, screen); }
                if (lines != h) { bmp.UnlockBits(bd); bmp.Dispose(); return FromHbitmapFallback(hbmp); }
                for (int y = 0; y < h && !anyAlpha; y++)
                {
                    byte* d = (byte*)bd.Scan0 + (long)y * bd.Stride;
                    for (int x = 0; x < w; x++) if (d[x * 4 + 3] != 0) { anyAlpha = true; break; }
                }
                if (!anyAlpha)
                {
                    // 24-bit content in a 32-bit DIB: alpha is unused, make it opaque.
                    for (int y = 0; y < h; y++)
                    {
                        byte* d = (byte*)bd.Scan0 + (long)y * bd.Stride;
                        for (int x = 0; x < w; x++) d[x * 4 + 3] = 255;
                    }
                }
            }
            bmp.UnlockBits(bd);
            return bmp;
        }
        return FromHbitmapFallback(hbmp);
    }

    private static Bitmap? FromHbitmapFallback(IntPtr hbmp)
    {
        try
        {
            using var tmp = System.Drawing.Image.FromHbitmap(hbmp);
            return new Bitmap(tmp);
        }
        catch
        {
            return null;
        }
    }

    [DllImport("gdi32.dll")]
    private static extern int GetDIBits(IntPtr hdc, IntPtr hbm, uint start, uint cLines, IntPtr lpvBits, ref BITMAPINFOHEADER lpbmi, uint usage);
    [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hWnd);
    [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    // ---------------------------------------------------------------- type name

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct SHFILEINFO
    {
        public IntPtr hIcon;
        public int iIcon;
        public uint dwAttributes;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szDisplayName;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)] public string szTypeName;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr SHGetFileInfo(string pszPath, uint attr, ref SHFILEINFO psfi, uint cb, uint flags);

    private static readonly Dictionary<string, string> _typeNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Explorer's "Type" column text, e.g. "JPEG image".</summary>
    public static string GetTypeName(string path, bool isDirectory)
    {
        string key = isDirectory ? "<dir>" : System.IO.Path.GetExtension(path);
        lock (_typeNames) if (_typeNames.TryGetValue(key, out var cached)) return cached;
        string name;
        try
        {
            var sfi = new SHFILEINFO();
            const uint SHGFI_TYPENAME = 0x400, SHGFI_USEFILEATTRIBUTES = 0x10;
            uint attr = isDirectory ? 0x10u : 0x80u;
            SHGetFileInfo(isDirectory ? "folder" : "file" + key, attr, ref sfi, (uint)Marshal.SizeOf<SHFILEINFO>(), SHGFI_TYPENAME | SHGFI_USEFILEATTRIBUTES);
            name = string.IsNullOrWhiteSpace(sfi.szTypeName) ? (key.TrimStart('.').ToUpperInvariant() + " File") : sfi.szTypeName;
        }
        catch
        {
            name = key.TrimStart('.').ToUpperInvariant() + " File";
        }
        lock (_typeNames) _typeNames[key] = name;
        return name;
    }

    // ---------------------------------------------------------------- property system

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    public struct PROPERTYKEY
    {
        public Guid fmtid;
        public uint pid;
        public PROPERTYKEY(string g, uint p) { fmtid = new Guid(g); pid = p; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROPVARIANT
    {
        public ushort vt;
        public ushort r1, r2, r3;
        public IntPtr p1, p2;
    }

    [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint c);
        [PreserveSig] int GetAt(uint i, out PROPERTYKEY key);
        [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
        [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT pv);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(string path, IntPtr pbc, int flags, ref Guid riid,
        [MarshalAs(UnmanagedType.Interface)] out object ppv);

    [DllImport("propsys.dll", CharSet = CharSet.Unicode)]
    private static extern int PSFormatForDisplayAlloc(ref PROPERTYKEY key, ref PROPVARIANT pv, int flags, out IntPtr psz);

    [DllImport("propsys.dll")]
    private static extern int PropVariantToUInt64(ref PROPVARIANT pv, out ulong v);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PROPVARIANT pv);

    public static readonly PROPERTYKEY PKEY_Media_Duration = new("64440490-4C8B-11D1-8B70-080036B11A03", 3);
    public static readonly PROPERTYKEY PKEY_Audio_EncodingBitrate = new("64440490-4C8B-11D1-8B70-080036B11A03", 4);
    public static readonly PROPERTYKEY PKEY_Audio_SampleRate = new("64440490-4C8B-11D1-8B70-080036B11A03", 5);
    public static readonly PROPERTYKEY PKEY_Audio_ChannelCount = new("64440490-4C8B-11D1-8B70-080036B11A03", 7);
    public static readonly PROPERTYKEY PKEY_Video_FrameWidth = new("64440491-4C8B-11D1-8B70-080036B11A03", 3);
    public static readonly PROPERTYKEY PKEY_Video_FrameHeight = new("64440491-4C8B-11D1-8B70-080036B11A03", 4);
    public static readonly PROPERTYKEY PKEY_Video_FrameRate = new("64440491-4C8B-11D1-8B70-080036B11A03", 6);
    public static readonly PROPERTYKEY PKEY_Video_TotalBitrate = new("64440491-4C8B-11D1-8B70-080036B11A03", 43);
    public static readonly PROPERTYKEY PKEY_Title = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9", 2);
    public static readonly PROPERTYKEY PKEY_Author = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9", 4);
    public static readonly PROPERTYKEY PKEY_Music_Artist = new("56A3372E-CE9C-11D2-9F0E-006097C686F6", 2);
    public static readonly PROPERTYKEY PKEY_Music_AlbumTitle = new("56A3372E-CE9C-11D2-9F0E-006097C686F6", 4);
    public static readonly PROPERTYKEY PKEY_Media_Year = new("56A3372E-CE9C-11D2-9F0E-006097C686F6", 5);
    public static readonly PROPERTYKEY PKEY_Music_Genre = new("56A3372E-CE9C-11D2-9F0E-006097C686F6", 11);
    public static readonly PROPERTYKEY PKEY_Document_PageCount = new("F29F85E0-4FF9-1068-AB91-08002B27B3D9", 14);
    public static readonly PROPERTYKEY PKEY_Image_Dimensions = new("6444048F-4C8B-11D1-8B70-080036B11A03", 13);
    public static readonly PROPERTYKEY PKEY_Photo_CameraModel = new("14B81DA1-0135-4D31-96D9-6CBFC9671A99", 272);
    public static readonly PROPERTYKEY PKEY_Photo_DateTaken = new("14B81DA1-0135-4D31-96D9-6CBFC9671A99", 36867);

    public sealed record PropValue(string? Display, ulong? Number);

    /// <summary>Reads properties via the shell property system (must run on STA).</summary>
    public static Dictionary<string, PropValue> ReadPropertiesSta(string path, params (string Name, PROPERTYKEY Key)[] keys)
    {
        var result = new Dictionary<string, PropValue>();
        object? obj = null;
        try
        {
            var iid = new Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99");
            const int GPS_BESTEFFORT = 0x40, GPS_OPENSLOWITEM = 0x10;
            if (SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, GPS_BESTEFFORT | GPS_OPENSLOWITEM, ref iid, out obj) != 0 ||
                obj is not IPropertyStore store)
                return result;
            foreach (var (name, key0) in keys)
            {
                var key = key0;
                if (store.GetValue(ref key, out var pv) != 0) continue;
                try
                {
                    if (pv.vt == 0) continue; // VT_EMPTY
                    string? display = null;
                    if (PSFormatForDisplayAlloc(ref key, ref pv, 0, out var psz) == 0 && psz != IntPtr.Zero)
                    {
                        display = Marshal.PtrToStringUni(psz);
                        Marshal.FreeCoTaskMem(psz);
                    }
                    ulong? num = PropVariantToUInt64(ref pv, out var u) == 0 ? u : null;
                    result[name] = new PropValue(display, num);
                }
                finally
                {
                    PropVariantClear(ref pv);
                }
            }
        }
        catch
        {
        }
        finally
        {
            if (obj != null && Marshal.IsComObject(obj)) Marshal.ReleaseComObject(obj);
        }
        return result;
    }

    public static Task<Dictionary<string, PropValue>?> ReadPropertiesAsync(string path, CancellationToken ct,
        params (string Name, PROPERTYKEY Key)[] keys) =>
        StaPool.Shared.RunAsync<Dictionary<string, PropValue>>(() => ReadPropertiesSta(path, keys), ct, true, 8000);

    /// <summary>Creates an IShellItem for IInitializeWithItem (call on the thread that will use it).</summary>
    public static object? CreateShellItem(string path)
    {
        var iid = IID_IShellItem;
        return SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out var o) == 0 ? o : null;
    }
}
