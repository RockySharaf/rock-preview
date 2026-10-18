using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using Anything.Explorer;

namespace NavTreeHarness;

/// <summary>
/// Throwaway harness: hosts NavigationTree for a few seconds and checks SelectFolder / keyboard / context menu /
/// RefreshRoots / Dispose. Nothing destructive: the only simulated input is an arrow key and opening (then
/// dismissing) a context menu. Usage: NavTreeHarness [screenshot.png]
/// </summary>
static class Program
{
    static int failures;
    static readonly List<string> raised = [];

    static void Log(string s) => Console.WriteLine(s);
    static void Check(bool ok, string what) { Console.WriteLine((ok ? "PASS  " : "FAIL  ") + what); if (!ok) failures++; }

    [STAThread]
    static int Main(string[] args)
    {
        string shot = args.Length > 0 ? args[0] : Path.Combine(Path.GetTempPath(), "navtree.png");
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var form = new Form { Text = "NavigationTree harness", Width = 360, Height = 820, StartPosition = FormStartPosition.Manual, Location = new Point(60, 60) };
        var tree = new NavigationTree { Dock = DockStyle.Fill };
        var box = new TextBox { Dock = DockStyle.Top, Text = "other focusable control" };
        form.Controls.Add(tree);
        form.Controls.Add(box);
        tree.FolderSelected += s => { raised.Add(s); Log("  event FolderSelected(\"" + s + "\")"); };
        form.Shown += async (_, _) =>
        {
            try { await Run(form, tree, shot); }
            catch (Exception ex) { Log("EXCEPTION " + ex); failures++; }
            form.Close();
        };
        Application.Run(form);
        Check(tree.IsDisposed, "form closed and NavigationTree disposed without exceptions");
        Log(failures == 0 ? "ALL PASSED" : failures + " FAILURE(S)");
        return failures;
    }

    static bool IsSelected(NavigationTree tree, string name)
        => tree.GetSelectedParsingNames().Any(s => string.Equals(s, name, StringComparison.OrdinalIgnoreCase));

    static async Task<long> WaitSelected(NavigationTree tree, string name, int timeoutMs = 5000)
    {
        var sw = Stopwatch.StartNew();
        while (!IsSelected(tree, name) && sw.ElapsedMilliseconds < timeoutMs) await Task.Delay(50);
        return IsSelected(tree, name) ? sw.ElapsedMilliseconds : -1;
    }

    static async Task Run(Form form, NavigationTree tree, string shot)
    {
        await Task.Delay(1500);
        Check(tree.Error == null, "NamespaceTreeControl created (Error = " + (tree.Error ?? "null") + ")");
        Capture(form, Path.Combine(Path.GetDirectoryName(shot)!, "navtree-start.png"));
        Log("  initial selection: [" + string.Join(", ", tree.GetSelectedParsingNames()) + "]");
        Log("  This PC state: " + tree.GetItemState("::{20D04FE0-3AEA-1069-A2D8-08002B30309D}"));
        Log("  C:\\ state before: " + (tree.GetItemState(@"C:\")?.ToString() ?? "not in tree"));
        Log("  C:\\Windows state before: " + (tree.GetItemState(@"C:\Windows")?.ToString() ?? "not in tree"));

        // 1. SelectFolder deep path: must expand This PC > C: > Windows and select System32, without raising.
        const string sys32 = @"C:\Windows\System32";
        var sw = Stopwatch.StartNew();
        tree.SelectFolder(sys32);
        Log($"  SelectFolder returned after {sw.ElapsedMilliseconds} ms; selected synchronously: {IsSelected(tree, sys32)}");
        long ms = await WaitSelected(tree, sys32);
        Check(ms >= 0, $"SelectFolder(\"{sys32}\") selected it (GetSelectedItems) after {ms} ms");
        Log("  C:\\ state: " + tree.GetItemState(@"C:\") + "; C:\\Windows state: " + tree.GetItemState(@"C:\Windows")
            + "; System32 state: " + tree.GetItemState(sys32));
        Check(tree.GetItemState(@"C:\Windows") is { } w && w.HasFlag(NSTCITEMSTATE.EXPANDED), "ancestor C:\\Windows was auto-expanded");
        Check(tree.GetItemState(sys32) is { } s32 && !s32.HasFlag(NSTCITEMSTATE.EXPANDED), "System32 itself was not expanded");
        await Task.Delay(400);
        Check(raised.Count == 0, "SelectFolder did not raise FolderSelected");
        Check(string.Equals(tree.SelectedParsingName, sys32, StringComparison.OrdinalIgnoreCase), "SelectedParsingName = " + tree.SelectedParsingName);
        Capture(form, shot);
        Log("  screenshot: " + shot);

        // 2. Other targets.
        string profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        foreach (var target in new[] { "::{20D04FE0-3AEA-1069-A2D8-08002B30309D}", profile, Path.Combine(profile, "Downloads"), @"C:\", @"C:\Program Files\Common Files" })
        {
            tree.SelectFolder(target);
            long t = await WaitSelected(tree, target);
            Check(t >= 0, $"SelectFolder(\"{target}\") -> selected after {t} ms; tree reports [{string.Join(", ", tree.GetSelectedParsingNames())}]");
            if (target.StartsWith("::")) { await Task.Delay(300); Capture(form, Path.Combine(Path.GetDirectoryName(shot)!, "navtree-roots.png")); }
        }
        // Bad input never throws.
        tree.SelectFolder(@"C:\definitely\not\here\9f8e7d");
        tree.SelectFolder("");
        tree.SelectFolder("::{00000000-0000-0000-0000-000000000000}");
        await Task.Delay(300);
        Check(raised.Count == 0, "no FolderSelected from any programmatic SelectFolder");

        // 3. RefreshRoots keeps the selection.
        tree.SelectFolder(sys32);
        await WaitSelected(tree, sys32);
        tree.RefreshRoots();
        long rr = await WaitSelected(tree, sys32);
        Check(rr >= 0, $"RefreshRoots kept System32 selected ({rr} ms)");
        Check(raised.Count == 0, "RefreshRoots did not raise FolderSelected");

        // 4. Keyboard: focus the tree, post Down arrow through the WinForms message loop (PreProcessMessage path).
        tree.Focus();
        await Task.Delay(200);
        IntPtr focus = GetFocus();
        Log("  focused window class after tree.Focus(): " + ClassOf(focus) + "; form.ActiveControl is tree: " + (form.ActiveControl == tree));
        Check(ClassOf(focus) == "SysTreeView32", "focus forwarded to the native SysTreeView32");
        PostMessage(focus, WM_KEYDOWN, (IntPtr)VK_DOWN, (IntPtr)0x00500001);
        PostMessage(focus, WM_KEYUP, (IntPtr)VK_DOWN, unchecked((IntPtr)0xC0500001));
        await Task.Delay(1000);
        var afterDown = tree.GetSelectedParsingNames();
        Log("  selection after Down: [" + string.Join(", ", afterDown) + "]");
        Check(afterDown.Count == 1 && !string.Equals(afterDown[0], sys32, StringComparison.OrdinalIgnoreCase), "Down arrow moved the tree selection");
        Check(raised.Count == 1 && afterDown.Count == 1 && string.Equals(raised[0], afterDown[0], StringComparison.OrdinalIgnoreCase),
            "keyboard selection raised FolderSelected exactly once (debounced) for the new folder");

        string? menuBefore = await ProbeContextMenu(tree);
        Check(menuBefore != null, "right-click (before any rename) shows the shell context menu" + (menuBefore != null ? " (" + menuBefore + ")" : ""));

        await Task.Delay(500);
        string? menuSecond = await ProbeContextMenu(tree);
        Check(menuSecond != null, "a second right-click on the same row shows the menu again");

        // 4b. F2 starts in-place rename (cancelled immediately: TVM_ENDEDITLABELNOW with fCancel = TRUE). Target is a
        //     throwaway folder the harness creates in its own bin directory and deletes afterwards.
        string f2Dir = Path.Combine(AppContext.BaseDirectory, "f2-rename-probe");
        Directory.CreateDirectory(f2Dir);
        tree.SelectFolder(f2Dir);
        Check(await WaitSelected(tree, f2Dir) >= 0, "SelectFolder on a deep E:\\ path (" + f2Dir + ")");
        tree.Focus();
        await Task.Delay(150);
        IntPtr tvF2 = GetFocus();
        PostMessage(tvF2, WM_KEYDOWN, (IntPtr)VK_F2, (IntPtr)0x003C0001);
        PostMessage(tvF2, WM_KEYUP, (IntPtr)VK_F2, unchecked((IntPtr)0xC03C0001));
        IntPtr edit = IntPtr.Zero;
        var ew = Stopwatch.StartNew();
        while (edit == IntPtr.Zero && ew.ElapsedMilliseconds < 2000) { await Task.Delay(50); edit = SendMessage(tvF2, TVM_GETEDITCONTROL, IntPtr.Zero, IntPtr.Zero); }
        Check(edit != IntPtr.Zero, "F2 in the tree starts the shell's in-place rename");
        if (edit != IntPtr.Zero)
        {
            // Escape through the WinForms loop (ProcessCmdKey/IsInputKey must hand it to the rename box).
            Log("  focus during rename: " + ClassOf(GetFocus()));
            PostMessage(GetFocus(), WM_KEYDOWN, (IntPtr)VK_ESCAPE, (IntPtr)0x00010001);
            PostMessage(GetFocus(), WM_KEYUP, (IntPtr)VK_ESCAPE, unchecked((IntPtr)0xC0010001));
            await Task.Delay(400);
            bool closed = SendMessage(tvF2, TVM_GETEDITCONTROL, IntPtr.Zero, IntPtr.Zero) == IntPtr.Zero;
            Check(closed, "Escape in the rename box cancels it (key reached the edit control)");
            if (!closed) SendMessage(tvF2, TVM_ENDEDITLABELNOW, (IntPtr)1, IntPtr.Zero);
        }
        await Task.Delay(300);
        Check(Directory.Exists(f2Dir) && raised.Count == 1, "rename cancelled; nothing changed, no FolderSelected");
        try { Directory.Delete(f2Dir); } catch (Exception ex) { Log("  (could not delete probe folder: " + ex.Message + ")"); }
        tree.SelectFolder(afterDown[0]);
        await WaitSelected(tree, afterDown[0]);
        tree.Focus();
        await Task.Delay(800);

        await Task.Delay(300);
        Check(raised.Count == 1, "context menu did not raise FolderSelected");
    }

    /// <summary>Posts a right-click on the selected row; returns the menu's items, or null when no menu appeared.</summary>
    static async Task<string?> ProbeContextMenu(NavigationTree tree, bool viaAppsKey = false)
    {
        IntPtr tv = GetFocus();
        IntPtr caret = SendMessage(tv, TVM_GETNEXTITEM, (IntPtr)TVGN_CARET, IntPtr.Zero);
        long hItem = caret.ToInt64(); // TVM_GETITEMRECT: the RECT's first pointer-sized field carries the HTREEITEM
        var rc = new RECT { Left = unchecked((int)(hItem & 0xFFFFFFFF)), Top = (int)(hItem >> 32) };
        SendMessageRect(tv, TVM_GETITEMRECT, (IntPtr)1, ref rc);
        int x = (rc.Left + rc.Right) / 2, y = (rc.Top + rc.Bottom) / 2;
        Log($"  right-click target: {ClassOf(tv)} caret=0x{caret:X} at ({x},{y}); selection [{string.Join(", ", tree.GetSelectedParsingNames())}]");
        string? menuReport = null;
        var probe = new System.Windows.Forms.Timer { Interval = 150 };
        int ticks = 0;
        probe.Tick += (_, _) =>
        {
            if (++ticks > 40) { probe.Stop(); return; }
            IntPtr menuWnd = FindThreadWindow("#32768");
            if (menuWnd == IntPtr.Zero) return;
            probe.Stop();
            IntPtr hmenu = SendMessage(menuWnd, MN_GETHMENU, IntPtr.Zero, IntPtr.Zero);
            int n = hmenu == IntPtr.Zero ? -1 : GetMenuItemCount(hmenu);
            var names = new List<string>();
            for (int i = 0; i < n && names.Count < 12; i++)
            {
                var sb = new StringBuilder(256);
                if (GetMenuString(hmenu, (uint)i, sb, sb.Capacity, MF_BYPOSITION) > 0) names.Add(sb.ToString().Replace("&", ""));
            }
            menuReport = $"{n} items: " + string.Join(" | ", names);
            EndMenu();
        };
        probe.Start();
        if (viaAppsKey)
        {
            PostMessage(tv, WM_KEYDOWN, (IntPtr)VK_APPS, (IntPtr)0x015D0001);
            PostMessage(tv, WM_KEYUP, (IntPtr)VK_APPS, unchecked((IntPtr)0xC15D0001));
        }
        else
        {
            PostMessage(tv, WM_RBUTTONDOWN, (IntPtr)MK_RBUTTON, MakeLParam(x, y));
            PostMessage(tv, WM_RBUTTONUP, IntPtr.Zero, MakeLParam(x, y));
        }
        var mw = Stopwatch.StartNew();
        while (menuReport == null && mw.ElapsedMilliseconds < 6000) await Task.Delay(100);
        probe.Stop();
        return menuReport;
    }

    static void Capture(Form form, string path)
    {
        using var bmp = new Bitmap(form.Width, form.Height);
        using (var g = Graphics.FromImage(bmp))
        {
            IntPtr hdc = g.GetHdc();
            try { PrintWindow(form.Handle, hdc, 2 /* PW_RENDERFULLCONTENT */); }
            finally { g.ReleaseHdc(hdc); }
        }
        bmp.Save(path, System.Drawing.Imaging.ImageFormat.Png);
    }

    static string ClassOf(IntPtr h)
    {
        var sb = new StringBuilder(64);
        GetClassName(h, sb, sb.Capacity);
        return sb.ToString();
    }

    static IntPtr FindThreadWindow(string cls)
    {
        IntPtr found = IntPtr.Zero;
        EnumThreadWindows(GetCurrentThreadId(), (h, _) =>
        {
            if (IsWindowVisible(h) && ClassOf(h) == cls) { found = h; return false; }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    static IntPtr MakeLParam(int x, int y) => (IntPtr)((y << 16) | (x & 0xFFFF));

    const int WM_KEYDOWN = 0x100, WM_KEYUP = 0x101, VK_DOWN = 0x28, WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, MK_RBUTTON = 2;
    const int TVM_GETNEXTITEM = 0x1100 + 10, TVGN_CARET = 9, TVM_GETITEMRECT = 0x1100 + 4, MN_GETHMENU = 0x01E1;
    const int VK_APPS = 0x5D, VK_ESCAPE = 0x1B, VK_F2 = 0x71, TVM_GETEDITCONTROL = 0x1100 + 15, TVM_ENDEDITLABELNOW = 0x1100 + 22;
    const uint MF_BYPOSITION = 0x400;

    delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);
    [DllImport("user32.dll")] static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
    [DllImport("user32.dll")] static extern IntPtr GetFocus();
    [DllImport("user32.dll")] static extern bool PostMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hwnd, int msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", EntryPoint = "SendMessageW")] static extern IntPtr SendMessageRect(IntPtr hwnd, int msg, IntPtr w, ref RECT l);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassName(IntPtr h, StringBuilder sb, int max);
    [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint tid, EnumWindowsProc cb, IntPtr l);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();
    [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
    [DllImport("user32.dll")] static extern int GetMenuItemCount(IntPtr hmenu);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetMenuString(IntPtr hmenu, uint id, StringBuilder sb, int max, uint flags);
    [DllImport("user32.dll")] static extern bool EndMenu();
}
