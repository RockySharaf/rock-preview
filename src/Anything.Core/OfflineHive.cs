using System.ComponentModel;
using System.Runtime.InteropServices;

namespace Anything.Core;

/// <summary>
/// A registry hive file read without loading it into the registry, through Windows' Offline Registry Library
/// (offreg.dll in System32). The uninstall reads a signed-out account's classes (its UsrClass.dat) this way:
/// RegLoadAppKey only loads a hive whose keys all share one security descriptor, which a real UsrClass.dat never has,
/// and mounting it under HKEY_USERS would need backup and restore privileges. Nothing here changes the file.
/// </summary>
public sealed partial class OfflineHive : IDisposable
{
    const int ERROR_SUCCESS = 0;
    const int ERROR_FILE_NOT_FOUND = 2;
    const int ERROR_MORE_DATA = 234;
    const int REG_SZ = 1;
    const int REG_EXPAND_SZ = 2;

    nint _root;

    OfflineHive(nint root) => _root = root;

    /// <summary>
    /// Opens the hive file <paramref name="file"/> for reading. Throws when it can't: offreg.dll missing
    /// (DllNotFoundException), or the file can't be read or isn't a hive (Win32Exception).
    /// </summary>
    public static OfflineHive Open(string file)
    {
        int rc = OROpenHive(file, out nint root);
        if (rc != ERROR_SUCCESS) throw new Win32Exception(rc);
        return new OfflineHive(root);
    }

    /// <summary>
    /// The text in value <paramref name="valueName"/> (null: the key's default value) of key <paramref name="subKey"/>
    /// (a path below the hive's root), like RegistryKey.GetValue: null when the key or value isn't there or isn't text, and
    /// an expandable string expanded. Throws (Win32Exception) when the hive can't be read there.
    /// </summary>
    public string? ReadString(string subKey, string? valueName)
    {
        ObjectDisposedException.ThrowIf(_root == 0, this);
        int rc = OROpenKey(_root, subKey, out nint key);
        if (rc == ERROR_FILE_NOT_FOUND) return null;
        if (rc != ERROR_SUCCESS) throw new Win32Exception(rc);
        try
        {
            int size = 0;
            rc = ORGetValue(key, null, valueName, out int type, null, ref size);
            if (rc == ERROR_FILE_NOT_FOUND) return null;
            if (rc != ERROR_SUCCESS && rc != ERROR_MORE_DATA) throw new Win32Exception(rc);
            if (type is not (REG_SZ or REG_EXPAND_SZ)) return null;
            // The value can grow between the two calls only if someone else writes the file; read again then.
            for (int attempt = 0; attempt < 3; attempt++)
            {
                var data = new byte[Math.Max(size, 2)];
                int got = data.Length;
                rc = ORGetValue(key, null, valueName, out type, data, ref got);
                if (rc == ERROR_MORE_DATA) { size = got; continue; }
                if (rc == ERROR_FILE_NOT_FOUND) return null;
                if (rc != ERROR_SUCCESS) throw new Win32Exception(rc);
                if (type is not (REG_SZ or REG_EXPAND_SZ)) return null;
                string text = System.Text.Encoding.Unicode.GetString(data, 0, got & ~1);
                int end = text.IndexOf('\0');
                if (end >= 0) text = text[..end];
                return type == REG_EXPAND_SZ ? Environment.ExpandEnvironmentVariables(text) : text;
            }
            throw new Win32Exception(ERROR_MORE_DATA);
        }
        finally
        {
            ORCloseKey(key);
        }
    }

    public void Dispose()
    {
        if (_root == 0) return;
        ORCloseHive(_root);
        _root = 0;
    }

    // offreg.h (Windows Driver Kit); every function returns a Win32 error code. Loaded from System32 only: it isn't one of
    // Windows' known DLLs, and an elevated uninstall run from Downloads mustn't load a copy planted beside it.
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("offreg.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int OROpenHive(string hivePath, out nint hive);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("offreg.dll")]
    private static partial int ORCloseHive(nint hive);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("offreg.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int OROpenKey(nint key, string? subKeyName, out nint result);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("offreg.dll")]
    private static partial int ORCloseKey(nint key);

    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [LibraryImport("offreg.dll", StringMarshalling = StringMarshalling.Utf16)]
    private static partial int ORGetValue(nint key, string? subKey, string? valueName, out int type, byte[]? data, ref int dataSize);
}
