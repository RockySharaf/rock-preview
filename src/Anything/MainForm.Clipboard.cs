using System.Runtime.InteropServices;
using Anything.Core;
using Anything.Interop;

namespace Anything;

/// <summary>
/// The clipboard as Explorer follows it: Paste lights up the moment files are copied anywhere, and items cut (Ctrl+X)
/// show dimmed in the results until they are pasted or something else is copied. Also Undo/Redo and Copy as path.
/// </summary>
internal sealed partial class MainForm
{
    const int WM_CLIPBOARDUPDATE = 0x031D;
    const uint CF_HDROP = 15;

    /// <summary>The paths on the clipboard as cut (shown dimmed); rebuilt on every clipboard change.</summary>
    HashSet<string> _cutPaths = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>The clipboard changed while the folder view showed, so <see cref="_cutPaths"/> wasn't read; read when results show.</summary>
    bool _cutPathsStale;
    bool _clipboardRefreshQueued;
    static uint s_cfDropEffect, s_cfFileGroup, s_cfFileGroupW;

    /// <summary>Called when the window's handle is made (again: WinForms can recreate it).</summary>
    void StartClipboardListener()
    {
        _grid.IsCut = path => _cutPaths.Count > 0 && _cutPaths.Contains(path);
        AddClipboardFormatListener(Handle);
        QueueClipboardRefresh(); // something may have been cut before this window opened
    }

    protected override void OnHandleDestroyed(EventArgs e)
    {
        RemoveClipboardFormatListener(Handle);
        base.OnHandleDestroyed(e);
    }

    /// <summary>From WndProc: true when the message was the clipboard's change notice.</summary>
    bool HandleClipboardMessage(ref Message m)
    {
        if (m.Msg != WM_CLIPBOARDUPDATE) return false;
        QueueClipboardRefresh();
        m.Result = IntPtr.Zero;
        return true;
    }

    /// <summary>One refresh for a burst of changes (programs often set the clipboard several times in a row).</summary>
    void QueueClipboardRefresh()
    {
        if (_clipboardRefreshQueued || !IsHandleCreated || IsDisposed) return;
        _clipboardRefreshQueued = true;
        BeginInvoke(() =>
        {
            _clipboardRefreshQueued = false;
            if (IsDisposed) return;
            UpdateCommandStates();
            RefreshCutPaths();
        });
    }

    /// <summary>Whether the clipboard holds anything Explorer's view can paste as files, virtual ones (Outlook attachments) too.</summary>
    static bool ClipboardHasPasteableFiles()
    {
        if (s_cfIdList == 0) s_cfIdList = RegisterClipboardFormat("Shell IDList Array");
        if (s_cfFileGroupW == 0) s_cfFileGroupW = RegisterClipboardFormat("FileGroupDescriptorW");
        if (s_cfFileGroup == 0) s_cfFileGroup = RegisterClipboardFormat("FileGroupDescriptor");
        return IsClipboardFormatAvailable(CF_HDROP) || IsClipboardFormatAvailable(s_cfIdList)
            || IsClipboardFormatAvailable(s_cfFileGroupW) || IsClipboardFormatAvailable(s_cfFileGroup);
    }

    /// <summary>Reads which files are cut and redraws the results that show if that changed.</summary>
    void RefreshCutPaths()
    {
        // The folder view dims its own cut items, so nothing here needs them while it shows. Reading the clipboard asks its
        // owner (Explorer, usually) to draw up the data, and waits for it: not done on every copy anywhere for nothing.
        if (ExplorerShown) { _cutPathsStale = true; return; }
        _cutPathsStale = false;
        if (s_cfDropEffect == 0) s_cfDropEffect = RegisterClipboardFormat("Preferred DropEffect");
        HashSet<string> cut;
        if (!IsClipboardFormatAvailable(CF_HDROP) || !IsClipboardFormatAvailable(s_cfDropEffect)) cut = ClipboardRules.CutPaths(null, null);
        else if (ReadCutFiles() is { } read) cut = read;
        else return; // another program has the clipboard open: what was cut stays dimmed until the next change
        if (cut.SetEquals(_cutPaths)) return;
        _cutPaths = cut;
        if (ThumbnailMode) _grid.Invalidate(); // only what shows is painted
        else _list.Invalidate();
    }

    /// <summary>The files cut, none when they were copied; null when the clipboard couldn't be opened. Never throws.</summary>
    HashSet<string>? ReadCutFiles()
    {
        if (!OpenClipboard(Handle)) return null;
        try
        {
            int? effect = null;
            IntPtr h = GetClipboardData(s_cfDropEffect);
            IntPtr block = h == IntPtr.Zero ? IntPtr.Zero : GlobalLock(h);
            if (block != IntPtr.Zero)
            {
                try
                {
                    var bytes = new byte[(int)Math.Min(8UL, (ulong)GlobalSize(h))];
                    Marshal.Copy(block, bytes, 0, bytes.Length);
                    effect = ClipboardRules.ReadDropEffect(bytes);
                }
                finally { GlobalUnlock(h); }
            }
            // Copied, not cut: the file list isn't read at all (it can be long, and its owner draws it up on request).
            if (effect != ClipboardRules.DropEffectMove) return ClipboardRules.CutPaths(null, effect);
            return ClipboardRules.CutPaths(ShellDragDrop.DropFilePaths(GetClipboardData(CF_HDROP)), effect);
        }
        catch (Exception ex) when (ex is ExternalException or OverflowException or ArgumentException)
        {
            return null;
        }
        finally { CloseClipboard(); }
    }

    /// <summary>Whether a result is cut, waiting to be pasted: its icon is drawn at half strength.</summary>
    bool IsCut(Row row) => _cutPaths.Count > 0 && row.FullPath.Length > 0 && _cutPaths.Contains(row.FullPath);

    // ---- Undo, Redo, Copy as path -----------------------------------------------------------------

    /// <summary>
    /// Ctrl+Z / Ctrl+Y and See more's Undo and Redo: the shell's own undo list, which holds the renames, deletes, moves,
    /// copies and new folders made here and in the folder view. With the folder view showing, it is told the keystroke,
    /// as if typed there; over search results, its background menu's Undo/Redo runs (the view stays behind them). That
    /// menu lists them as "Undo Rename" (verb "undo") and "Redo Rename" (verb "redo") only while there is something to
    /// undo or redo (checked on Windows 11 with an off-screen ExplorerBrowser, 2026-11).
    /// </summary>
    void UndoRedo(bool redo)
    {
        if (ExplorerShown) { _explorer.SendToView(redo ? "^y" : "^z"); return; }
        object? view = _explorer.GetShellViewObject();
        // A tab that never showed a folder has no view. The undo list is the shell's, the same in every folder view, so
        // another tab's view in this window (same thread) serves as well.
        foreach (var tab in _tabs)
        {
            if (view != null) break;
            if (tab.Explorer != _explorer) view = tab.Explorer.GetShellViewObject();
        }
        try
        {
            if (!ShellContextMenu.InvokeBackgroundVerb(this, view, redo ? "redo" : "undo"))
                _statusRight.Text = redo ? "Nothing to redo" : "Nothing to undo";
        }
        finally
        {
            if (view != null) Marshal.ReleaseComObject(view);
        }
    }

    /// <summary>
    /// File Explorer's "Copy as path" (Ctrl+Shift+C): each selected item's full path in double quotes, one per line, from
    /// the folder view or the results, whichever is showing.
    /// </summary>
    void CopyAsPath()
    {
        var paths = ExplorerShown ? _explorer.GetSelectedPaths(1_000_000) : SelectedPaths(1_000_000);
        if (paths.Count > 0) FileOps.SetClipboardText(ClipboardRules.QuotedPaths(paths));
    }

    [DllImport("user32.dll", SetLastError = true)] static extern bool AddClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
    [DllImport("user32.dll", SetLastError = true)] static extern bool OpenClipboard(IntPtr hWndNewOwner);
    [DllImport("user32.dll", SetLastError = true)] static extern bool CloseClipboard();
    [DllImport("user32.dll", SetLastError = true)] static extern IntPtr GetClipboardData(uint uFormat);
    [DllImport("kernel32.dll")] static extern IntPtr GlobalLock(IntPtr hMem);
    [DllImport("kernel32.dll")] static extern bool GlobalUnlock(IntPtr hMem);
    [DllImport("kernel32.dll")] static extern UIntPtr GlobalSize(IntPtr hMem);
}
