using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Anything.Tests.Content.Readers;

/// <summary>
/// Compound files written by Windows' own OLE structured storage (StgCreateDocfile), an independent writer for the CFB
/// reader; plus the MS-CFB specification's worked example typed in as hex.
/// </summary>
internal static class CfbFixtures
{
    const uint STGM_CREATE = 0x1000, STGM_READWRITE = 0x2, STGM_SHARE_EXCLUSIVE = 0x10;

    [ComImport, Guid("0000000B-0000-0000-C000-000000000046"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    public interface IStorage
    {
        void CreateStream([MarshalAs(UnmanagedType.LPWStr)] string name, uint mode, uint reserved1, uint reserved2, out IStream stream);
        void OpenStream();
        void CreateStorage([MarshalAs(UnmanagedType.LPWStr)] string name, uint mode, uint reserved1, uint reserved2, out IStorage storage);
        void OpenStorage();
        void CopyTo();
        void MoveElementTo();
        void Commit(uint flags);
        void Revert();
        void EnumElements();
        void DestroyElement();
        void RenameElement();
        void SetElementTimes();
        void SetClass();
        void SetStateBits();
        void Stat();
    }

    [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
    static extern int StgCreateDocfile(string name, uint mode, uint reserved, out IStorage storage);

    /// <summary>A compound file built by OLE; the callback fills the root storage.</summary>
    public static byte[] Ole(Action<IStorage> fill)
    {
        string path = Path.Combine(Path.GetTempPath(), "rp-cfb-" + Guid.NewGuid().ToString("N") + ".cfb");
        try
        {
            Marshal.ThrowExceptionForHR(StgCreateDocfile(path, STGM_CREATE | STGM_READWRITE | STGM_SHARE_EXCLUSIVE, 0, out var root));
            try
            {
                fill(root);
                root.Commit(0);
            }
            finally { Marshal.ReleaseComObject(root); }
            return File.ReadAllBytes(path);
        }
        finally { File.Delete(path); }
    }

    public static void Stream(IStorage storage, string name, byte[] data)
    {
        storage.CreateStream(name, STGM_CREATE | STGM_READWRITE | STGM_SHARE_EXCLUSIVE, 0, 0, out var stream);
        try { stream.Write(data, data.Length, IntPtr.Zero); }
        finally { Marshal.ReleaseComObject(stream); }
    }

    public static IStorage Storage(IStorage parent, string name)
    {
        parent.CreateStorage(name, STGM_CREATE | STGM_READWRITE | STGM_SHARE_EXCLUSIVE, 0, 0, out var storage);
        return storage;
    }

    public static void Close(IStorage storage)
    {
        storage.Commit(0);
        Marshal.ReleaseComObject(storage);
    }

    /// <summary>A Unicode (001F) MSG string property.</summary>
    public static void Unicode(IStorage s, int prop, string value)
        => Stream(s, $"__substg1.0_{prop:X4}001F", Encoding.Unicode.GetBytes(value + "\0"));

    /// <summary>An 8-bit (001E) MSG string property in a code page.</summary>
    public static void String8(IStorage s, int prop, string value, int codePage)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        Stream(s, $"__substg1.0_{prop:X4}001E", Encoding.GetEncoding(codePage).GetBytes(value + "\0"));
    }

    /// <summary>The fixed-size property stream with PT_LONG values (header: 32 bytes for a message, 8 for recipients and attachments).</summary>
    public static void Properties(IStorage s, int header, params (int Prop, int Value)[] longs)
    {
        var ms = new MemoryStream();
        ms.Write(new byte[header]);
        var w = new BinaryWriter(ms);
        foreach (var (prop, value) in longs)
        {
            w.Write((uint)(prop << 16 | 0x0003));
            w.Write(2u); // readable
            w.Write(value);
            w.Write(0);
        }
        Stream(s, "__properties_version1.0", ms.ToArray());
    }

    /// <summary>The MS-CFB section 3 example file: "Storage 1" holding "Stream 1" (544 bytes of "Data for stream 1") in the mini stream.</summary>
    public static byte[] SpecExample()
    {
        var file = new byte[0xC00];
        void Hex(int at, string hex)
        {
            var parts = hex.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            foreach (var p in parts)
                for (int i = 0; i < p.Length; i += 2) file[at++] = Convert.ToByte(p.Substring(i, 2), 16);
        }
        // 3.1 The header (its DIFAT[1..108] are FREESECT).
        Hex(0x000, "D0CF 11E0 A1B1 1AE1 0000 0000 0000 0000");
        Hex(0x010, "0000 0000 0000 0000 3E00 0300 FEFF 0900");
        Hex(0x020, "0600 0000 0000 0000 0000 0000 0100 0000");
        Hex(0x030, "0100 0000 0000 0000 0010 0000 0200 0000");
        Hex(0x040, "0100 0000 FEFF FFFF 0000 0000 0000 0000");
        for (int i = 0x050; i < 0x200; i++) file[i] = 0xFF;
        // 3.2 Sector #0: FAT.
        for (int i = 0x200; i < 0x400; i++) file[i] = 0xFF;
        Hex(0x200, "FDFF FFFF FEFF FFFF FEFF FFFF 0400 0000");
        Hex(0x210, "FEFF FFFF");
        // 3.3 Sector #1: directory (root, Storage 1, Stream 1, unused).
        Hex(0x400, "5200 6F00 6F00 7400 2000 4500 6E00 7400");
        Hex(0x410, "7200 7900 0000 0000 0000 0000 0000 0000");
        Hex(0x440, "1600 0501 FFFF FFFF FFFF FFFF 0100 0000");
        Hex(0x450, "0067 6156 54C1 CE11 8553 00AA 00A1 F95B");
        Hex(0x460, "0000 0000 0000 0000 0000 0000 801E 9213");
        Hex(0x470, "4BB4 BA01 0300 0000 4002 0000 0000 0000");
        Hex(0x480, "5300 7400 6F00 7200 6100 6700 6500 2000");
        Hex(0x490, "3100 0000 0000 0000 0000 0000 0000 0000");
        Hex(0x4C0, "1400 0101 FFFF FFFF FFFF FFFF 0200 0000");
        Hex(0x4D0, "0061 6156 54C1 CE11 8553 00AA 00A1 F95B");
        Hex(0x4E0, "0000 0000 0088 F912 4BB4 BA01 801E 9213");
        Hex(0x4F0, "4BB4 BA01 0000 0000 0000 0000 0000 0000");
        Hex(0x500, "5300 7400 7200 6500 6100 6D00 2000 3100");
        Hex(0x540, "1200 0201 FFFF FFFF FFFF FFFF FFFF FFFF");
        Hex(0x570, "0000 0000 0000 0000 2002 0000 0000 0000");
        Hex(0x5C0, "0000 0000 FFFF FFFF FFFF FFFF FFFF FFFF");
        // 3.4 Sector #2: mini FAT (a chain of nine mini sectors).
        for (int i = 0x600; i < 0x800; i++) file[i] = 0xFF;
        Hex(0x600, "0100 0000 0200 0000 0300 0000 0400 0000");
        Hex(0x610, "0500 0000 0600 0000 0700 0000 0800 0000");
        Hex(0x620, "FEFF FFFF");
        // 3.5 Sectors #3 and #4: the mini stream, "Data for stream 1" 32 times, zero filled.
        var data = Encoding.ASCII.GetBytes(string.Concat(Enumerable.Repeat("Data for stream 1", 32)));
        data.CopyTo(file, 0x800);
        return file;
    }
}
