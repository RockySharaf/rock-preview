using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Anything.Core.Content.Extract;

/// <summary>Win32 calls the file readers need (open flags, positioned reads, SetFileTime, handle stat, error codes); blittable signatures only.</summary>
internal static partial class ReaderNative
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_WRITE_ATTRIBUTES = 0x0100;
    /// <summary>Read, write and delete sharing: a reader never blocks the owner's saves, renames or deletes.</summary>
    public const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4;
    public const uint OPEN_EXISTING = 3;
    /// <summary>Leave the data where it is (cloud placeholders and offline storage are not recalled by the open).</summary>
    public const uint FILE_FLAG_OPEN_NO_RECALL = 0x00100000;

    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_PATH_NOT_FOUND = 3;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_SHARING_VIOLATION = 32;
    public const int ERROR_LOCK_VIOLATION = 33;
    public const int ERROR_HANDLE_EOF = 38;
    public const int ERROR_OPERATION_ABORTED = 995;
    /// <summary>First and last of the ERROR_CLOUD_FILE_* codes (a placeholder that isn't downloaded, a provider not running).</summary>
    public const int ERROR_CLOUD_FILE_FIRST = 358, ERROR_CLOUD_FILE_LAST = 404;

    public const uint FILE_ATTRIBUTE_OFFLINE = 0x1000;
    public const uint FILE_ATTRIBUTE_NOT_CONTENT_INDEXED = 0x2000;
    public const uint FILE_ATTRIBUTE_ENCRYPTED = 0x4000;
    public const uint FILE_ATTRIBUTE_RECALL_ON_OPEN = 0x40000;
    public const uint FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS = 0x400000;
    /// <summary>Attributes of a file whose data is somewhere else (OneDrive placeholders, offline storage): never opened.</summary>
    public const uint CloudBits = FILE_ATTRIBUTE_OFFLINE | FILE_ATTRIBUTE_RECALL_ON_OPEN | FILE_ATTRIBUTE_RECALL_ON_DATA_ACCESS;

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool ReadFile(SafeFileHandle handle, Span<byte> buffer, int bytesToRead, out int bytesRead, ref Overlapped overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool SetFileTime(SafeFileHandle handle, nint creationTime, ref long lastAccessTime, nint lastWriteTime);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static partial bool GetFileInformationByHandle(SafeFileHandle handle, out ByHandleFileInformation info);

    /// <summary>
    /// Reads at an absolute offset on a synchronous handle (the read runs on the calling thread, so a watchdog's
    /// CancelSynchronousIo frees it with 995); 0 at the end of the file; the Win32 error on failure.
    /// </summary>
    public static int ReadAt(SafeFileHandle handle, Span<byte> buffer, long offset, out int error)
    {
        var overlapped = new Overlapped { OffsetLow = unchecked((int)offset), OffsetHigh = (int)(offset >> 32) };
        if (ReadFile(handle, buffer, buffer.Length, out int read, ref overlapped))
        {
            error = 0;
            return read;
        }
        error = Marshal.GetLastPInvokeError();
        if (error == ERROR_HANDLE_EOF) { error = 0; return 0; }
        return -1;
    }

    /// <summary>Tells Windows not to update Date Accessed for reads through this handle (needs FILE_WRITE_ATTRIBUTES).</summary>
    public static bool KeepAccessTime(SafeFileHandle handle)
    {
        long keep = -1; // 0xFFFFFFFF_FFFFFFFF: "preserve the last access time"
        return SetFileTime(handle, 0, ref keep, 0);
    }

    /// <summary>Size, date modified (FILETIME UTC) and attributes of an open file.</summary>
    public static bool TryStat(SafeFileHandle handle, out long size, out long modifiedUtc, out uint attributes)
    {
        if (GetFileInformationByHandle(handle, out var info))
        {
            size = ((long)info.FileSizeHigh << 32) | info.FileSizeLow;
            modifiedUtc = info.LastWriteTime;
            attributes = info.FileAttributes;
            return true;
        }
        size = 0; modifiedUtc = 0; attributes = 0;
        return false;
    }

    /// <summary>The Win32 error inside an exception thrown by a BCL file call (UnauthorizedAccess = access denied).</summary>
    public static int ErrorOf(Exception e) => e switch
    {
        UnauthorizedAccessException => ERROR_ACCESS_DENIED,
        _ when (e.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000) => e.HResult & 0xFFFF,
        _ => 0,
    };

    /// <summary>OVERLAPPED: on a synchronous handle only its offset is used.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct Overlapped
    {
        public nint Internal, InternalHigh;
        public int OffsetLow, OffsetHigh;
        public nint Event;
    }

    [StructLayout(LayoutKind.Sequential, Pack = 4)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public long CreationTime;
        public long LastAccessTime;
        public long LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
