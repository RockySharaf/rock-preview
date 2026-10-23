using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using Anything.Interop;

namespace Anything;

/// <summary>Shell operations on result items.</summary>
internal static class FileOps
{
    [System.Runtime.InteropServices.DllImport("shlwapi.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
    static extern IntPtr StrFormatByteSizeW(long qdw, System.Text.StringBuilder pszBuf, int cchBuf);

    /// <summary>Sizes the way Explorer's status bar writes them ("2.34 KB"), via the same shell function.</summary>
    public static string FormatByteSize(long bytes)
    {
        var sb = new System.Text.StringBuilder(64);
        return StrFormatByteSizeW(bytes, sb, sb.Capacity) != IntPtr.Zero ? sb.ToString() : bytes.ToString("N0") + " bytes";
    }

    /// <summary>Opens each path with its default app. Returns the ones that opened (for the run history).</summary>
    public static List<string> Open(IWin32Window owner, IReadOnlyList<string> paths)
    {
        var opened = new List<string>();
        if (paths.Count == 0) return opened;
        if (paths.Count > 16 && MessageBox.Show(owner, $"Are you sure you wish to open {paths.Count} files?", "Confirm open",
                MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
            return opened;
        foreach (var p in paths)
        {
            try
            {
                var psi = new ProcessStartInfo(p) { UseShellExecute = true };
                string? dir = Path.GetDirectoryName(p);
                if (!string.IsNullOrEmpty(dir)) psi.WorkingDirectory = dir;
                Process.Start(psi)?.Dispose();
                opened.Add(p);
            }
            catch (Win32Exception ex) when (ex.NativeErrorCode == 1223) { /* cancelled */ }
            catch (Exception ex)
            {
                MessageBox.Show(owner, $"Unable to open \"{p}\".\n\n{ex.Message}", "Rock Preview", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
        return opened;
    }

    public static void OpenPath(IWin32Window owner, IReadOnlyList<string> paths)
    {
        foreach (var p in paths.Take(16))
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{p}\"") { UseShellExecute = true })?.Dispose();
            }
            catch (Exception ex)
            {
                MessageBox.Show(owner, $"Unable to open the path of \"{p}\".\n\n{ex.Message}", "Rock Preview", MessageBoxButtons.OK, MessageBoxIcon.Error);
            }
        }
    }

    public static void Properties(IWin32Window owner, IReadOnlyList<string> paths)
    {
        foreach (var p in paths.Take(8))
            Native.SHObjectProperties(owner.Handle, Native.SHOP_FILEPATH, p, null);
    }

    /// <summary>Recycle (or permanently delete) after confirmation. Returns true when the operation ran.</summary>
    public static bool Delete(IWin32Window owner, IReadOnlyList<string> paths, bool permanent)
    {
        if (paths.Count == 0) return false;
        string what = paths.Count == 1 ? $"\"{Path.GetFileName(paths[0].TrimEnd('\\'))}\"" : $"these {paths.Count:N0} items";
        string msg = permanent
            ? $"Are you sure you want to permanently delete {what}?"
            : $"Are you sure you want to move {what} to the Recycle Bin?";
        if (MessageBox.Show(owner, msg, permanent ? "Delete Permanently" : "Delete", MessageBoxButtons.YesNo,
                permanent ? MessageBoxIcon.Warning : MessageBoxIcon.Question) != DialogResult.Yes)
            return false;
        var op = new Native.SHFILEOPSTRUCT
        {
            hwnd = owner.Handle,
            wFunc = Native.FO_DELETE,
            pFrom = string.Join('\0', paths) + '\0',
            fFlags = (ushort)(Native.FOF_NOCONFIRMATION | (permanent ? 0 : Native.FOF_ALLOWUNDO | Native.FOF_WANTNUKEWARNING)),
        };
        int r = Native.SHFileOperation(ref op);
        if (r != 0 && !op.fAnyOperationsAborted)
            MessageBox.Show(owner, $"Unable to delete (error 0x{r:X}).", "Rock Preview", MessageBoxButtons.OK, MessageBoxIcon.Error);
        return r == 0;
    }

    public static bool Rename(IWin32Window owner, string fullPath, string newName)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(owner, "The file name is not valid.", "Rename", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        string dir = Path.GetDirectoryName(fullPath) ?? "";
        var op = new Native.SHFILEOPSTRUCT
        {
            hwnd = owner.Handle,
            wFunc = Native.FO_RENAME,
            pFrom = fullPath + '\0',
            pTo = Path.Combine(dir, newName) + '\0',
            fFlags = Native.FOF_ALLOWUNDO,
        };
        int r = Native.SHFileOperation(ref op);
        if (r != 0 && !op.fAnyOperationsAborted)
        {
            MessageBox.Show(owner, $"Unable to rename \"{Path.GetFileName(fullPath)}\" (error 0x{r:X}).", "Rename", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return false;
        }
        return r == 0;
    }

    /// <summary>Puts files on the clipboard the way Explorer does (FileDrop + Preferred DropEffect).</summary>
    public static void SetClipboardFiles(IReadOnlyList<string> paths, bool cut)
    {
        if (paths.Count == 0) return;
        var data = new DataObject();
        var sc = new StringCollection();
        sc.AddRange(paths.ToArray());
        data.SetFileDropList(sc);
        var effect = new MemoryStream(BitConverter.GetBytes(cut ? 2 : 5)); // DROPEFFECT_MOVE : COPY|LINK
        data.SetData("Preferred DropEffect", effect);
        TrySetClipboard(data);
    }

    public static void SetClipboardText(string text)
    {
        if (string.IsNullOrEmpty(text)) return;
        TrySetClipboard(new DataObject(DataFormats.UnicodeText, text));
    }

    static void TrySetClipboard(DataObject data)
    {
        try { Clipboard.SetDataObject(data, true, 5, 50); }
        catch (System.Runtime.InteropServices.ExternalException) { }
    }
}
