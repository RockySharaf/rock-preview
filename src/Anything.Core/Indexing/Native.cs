using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Anything.Core.Indexing;

/// <summary>Win32 interop used by the indexers (blittable signatures only).</summary>
internal static unsafe partial class Native
{
    public const uint GENERIC_READ = 0x80000000;
    public const uint FILE_LIST_DIRECTORY = 0x0001;
    public const uint FILE_SHARE_ALL = 0x1 | 0x2 | 0x4;
    public const uint OPEN_EXISTING = 3;
    public const uint FILE_FLAG_BACKUP_SEMANTICS = 0x02000000;

    public const uint FSCTL_ENUM_USN_DATA = 0x000900B3;
    public const uint FSCTL_READ_USN_JOURNAL = 0x000900BB;
    public const uint FSCTL_CREATE_USN_JOURNAL = 0x000900E7;
    public const uint FSCTL_QUERY_USN_JOURNAL = 0x000900F4;

    public const int ERROR_FILE_NOT_FOUND = 2;
    public const int ERROR_ACCESS_DENIED = 5;
    public const int ERROR_NO_MORE_FILES = 18;
    public const int ERROR_HANDLE_EOF = 38;
    public const int ERROR_OPERATION_ABORTED = 995;
    public const int ERROR_JOURNAL_DELETE_IN_PROGRESS = 1178;
    public const int ERROR_JOURNAL_NOT_ACTIVE = 1179;
    public const int ERROR_JOURNAL_ENTRY_DELETED = 1181;

    public const int FindExInfoBasic = 1;
    public const uint FIND_FIRST_EX_LARGE_FETCH = 2;
    public const int FileIdBothDirectoryInfo = 0x0A;
    public const uint THREAD_TERMINATE = 0x0001;

    [LibraryImport("kernel32.dll", EntryPoint = "CreateFileW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial SafeFileHandle CreateFile(string fileName, uint access, uint share, nint security, uint disposition, uint flags, nint template);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool ReadFile(SafeFileHandle handle, byte* buffer, int bytesToRead, out int bytesRead, NativeOverlapped* overlapped);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool DeviceIoControl(SafeFileHandle handle, uint code, void* inBuffer, int inSize, void* outBuffer, int outSize, out int bytesReturned, nint overlapped);

    [LibraryImport("kernel32.dll", EntryPoint = "GetFileAttributesExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileAttributesEx(string fileName, int infoLevel, out Win32FileAttributeData data);

    [LibraryImport("kernel32.dll", EntryPoint = "FindFirstFileExW", SetLastError = true, StringMarshalling = StringMarshalling.Utf16)]
    public static partial nint FindFirstFileEx(string fileName, int infoLevel, Win32FindData* data, int searchOp, nint filter, uint flags);

    [LibraryImport("kernel32.dll", EntryPoint = "FindNextFileW", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindNextFile(nint handle, Win32FindData* data);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool FindClose(nint handle);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool GetFileInformationByHandleEx(SafeFileHandle handle, int infoClass, void* buffer, int size);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    public static partial nint OpenThread(uint access, int inherit, uint threadId);

    [LibraryImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CancelSynchronousIo(nint thread);

    [LibraryImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static partial bool CloseHandle(nint handle);

    [LibraryImport("kernel32.dll")]
    public static partial uint GetCurrentThreadId();

    public static readonly nint InvalidHandle = -1;

    /// <summary>Adds the \\?\ prefix so paths longer than MAX_PATH work with the W APIs.</summary>
    public static string LongPath(string path)
    {
        if (path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\.\", StringComparison.Ordinal)) return path;
        if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path[2..];
        if (path.Length == 2 && path[1] == ':') path += "\\";
        return @"\\?\" + path;
    }

    /// <summary>Reads size/dates/attributes for one path. Returns false if it no longer exists.</summary>
    public static bool TryGetMetadata(string path, out FileAttributes attributes, out long size, out long modified, out long created)
        => TryGetMetadata(path, out attributes, out size, out modified, out created, out _);

    /// <summary><see cref="TryGetMetadata(string, out FileAttributes, out long, out long, out long)"/> with date accessed too.</summary>
    public static bool TryGetMetadata(string path, out FileAttributes attributes, out long size, out long modified, out long created, out long accessed)
    {
        if (GetFileAttributesEx(LongPath(path), 0, out var d))
        {
            attributes = (FileAttributes)d.FileAttributes;
            size = (attributes & FileAttributes.Directory) != 0 ? -1 : ((long)d.FileSizeHigh << 32) | d.FileSizeLow;
            modified = d.LastWriteTime;
            created = d.CreationTime;
            accessed = d.LastAccessTime;
            return true;
        }
        attributes = 0; size = 0; modified = 0; created = 0; accessed = 0;
        return false;
    }

    /// <summary>Date accessed of one path, read from the disk; on failure <paramref name="error"/> is the Win32 error.</summary>
    public static bool TryGetAccessed(string path, out long accessed, out int error)
    {
        if (GetFileAttributesEx(LongPath(path), 0, out var d))
        {
            accessed = d.LastAccessTime;
            error = 0;
            return true;
        }
        error = Marshal.GetLastPInvokeError();
        accessed = 0;
        return false;
    }
}

[StructLayout(LayoutKind.Sequential, Pack = 4)]
internal struct Win32FileAttributeData
{
    public uint FileAttributes;
    public long CreationTime;
    public long LastAccessTime;
    public long LastWriteTime;
    public uint FileSizeHigh;
    public uint FileSizeLow;
}

[StructLayout(LayoutKind.Sequential, Pack = 4, CharSet = CharSet.Unicode)]
internal unsafe struct Win32FindData
{
    public uint FileAttributes;
    public long CreationTime;
    public long LastAccessTime;
    public long LastWriteTime;
    public uint FileSizeHigh;
    public uint FileSizeLow;
    public uint Reserved0;
    public uint Reserved1;
    public fixed char FileName[260];
    public fixed char AlternateFileName[14];
}
