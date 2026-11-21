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

    /// <summary>
    /// Recycle (or permanently delete), the shell asking first as File Explorer's Delete does (the user's Recycle Bin
    /// setting; always for a permanent delete). Returns true when the operation ran.
    /// </summary>
    public static bool Delete(IWin32Window owner, IReadOnlyList<string> paths, bool permanent)
    {
        if (paths.Count == 0) return false;
        switch (ShellFileOperation.Delete(owner.Handle, paths, permanent, out int error, out var skipped))
        {
            case ShellFileOperation.Outcome.Done:
                if (skipped.Count > 0) ShowSkipped(owner, skipped);
                return true;
            case ShellFileOperation.Outcome.NotDone: return false;
            case ShellFileOperation.Outcome.Failed:
                MessageBox.Show(owner, $"Unable to delete (error 0x{error:X}).", "Rock Preview", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
        }
        // The copy engine isn't there: the older shell function, which asks the same way without FOF_NOCONFIRMATION.
        var op = new Native.SHFILEOPSTRUCT
        {
            hwnd = owner.Handle,
            wFunc = Native.FO_DELETE,
            pFrom = string.Join('\0', paths) + '\0',
            fFlags = (ushort)(permanent ? 0 : Native.FOF_ALLOWUNDO | Native.FOF_WANTNUKEWARNING),
        };
        int r = Native.SHFileOperation(ref op);
        if (r != 0 && !op.fAnyOperationsAborted)
            MessageBox.Show(owner, $"Unable to delete (error 0x{r:X}).", "Rock Preview", MessageBoxButtons.OK, MessageBoxIcon.Error);
        return r == 0;
    }

    /// <summary>The items a delete left where they were because the shell couldn't be given them: how many, the first few, and why.</summary>
    static void ShowSkipped(IWin32Window owner, List<(string Path, int Error)> skipped)
    {
        string nl = Environment.NewLine;
        string count = skipped.Count == 1 ? "1 item couldn't be deleted" : $"{skipped.Count:N0} items couldn't be deleted";
        var lines = skipped.Take(5).Select(s => $"{s.Path} (error 0x{s.Error:X})");
        string more = skipped.Count > 5 ? $"{nl}...and {skipped.Count - 5:N0} more" : "";
        MessageBox.Show(owner, $"{count}:{nl}{nl}{string.Join(nl, lines)}{more}", "Rock Preview", MessageBoxButtons.OK, MessageBoxIcon.Warning);
    }

    /// <summary>
    /// Renames with File Explorer's checks: a name with characters a name can't hold is refused, a file's changed
    /// extension is asked about first, and the shell itself does the rename (asking about a name that's taken, and
    /// putting it on its undo list). True when it was renamed.
    /// </summary>
    public static bool Rename(IWin32Window owner, string fullPath, string newName, bool isDirectory = false)
    {
        if (string.IsNullOrWhiteSpace(newName) || newName.Any(Anything.Core.RenameRules.IsForbidden)
            || newName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            MessageBox.Show(owner, Anything.Core.RenameRules.ForbiddenMessage, "Rename", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return false;
        }
        if (Anything.Core.RenameRules.ExtensionChanged(Path.GetFileName(fullPath.TrimEnd('\\')), newName, isDirectory)
            && MessageBox.Show(owner, Anything.Core.RenameRules.ExtensionChangeQuestion, "Rename", MessageBoxButtons.YesNo,
                MessageBoxIcon.Warning) != DialogResult.Yes)
            return false;
        switch (ShellFileOperation.Rename(owner.Handle, fullPath, newName, out int error))
        {
            case ShellFileOperation.Outcome.Done: return true;
            case ShellFileOperation.Outcome.NotDone: return false;
            case ShellFileOperation.Outcome.Failed:
                MessageBox.Show(owner, $"Unable to rename \"{Path.GetFileName(fullPath)}\" (error 0x{error:X}).", "Rename", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return false;
        }
        // The copy engine isn't there: the older shell function, which also asks about a name that's taken.
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
