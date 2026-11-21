using System.Runtime.InteropServices;
using Anything.Core;

namespace Anything;

/// <summary>
/// The mouse's Back and Forward side buttons, and a keyboard's or remote's Browser Back / Forward keys, go back and
/// forward anywhere in the window, like Alt+Left and Alt+Right and as in File Explorer.
/// </summary>
internal sealed partial class MainForm
{
    const int WM_APPCOMMAND = 0x0319;
    const int WM_XBUTTONDOWN = 0x020B, WM_XBUTTONUP = 0x020C, WM_XBUTTONDBLCLK = 0x020D,
              WM_NCXBUTTONDOWN = 0x00AB, WM_NCXBUTTONUP = 0x00AC, WM_NCXBUTTONDBLCLK = 0x00AD;
    const int WM_KEYDOWN = 0x0100, WM_KEYUP = 0x0101, WM_SYSKEYDOWN = 0x0104, WM_SYSKEYUP = 0x0105;
    const uint GA_ROOT = 2;

    /// <summary>Whether this UI thread has the back-and-forward filter (every window torn off a tab shares the thread).</summary>
    [ThreadStatic] static bool t_backForwardFiltered;

    /// <summary>
    /// Installs the back-and-forward filter once per UI thread. It stays for the life of the thread: when no window is
    /// open it only looks at side-button and Browser key messages and lets them through, and taking it out and putting
    /// it back as windows close and open (with Run in background keeping the app alive between) would gain nothing.
    /// </summary>
    static void FilterBackForward()
    {
        if (t_backForwardFiltered) return;
        Application.AddMessageFilter(new BackForwardInput());
        t_backForwardFiltered = true;
    }

    /// <summary>
    /// The file list and the navigation pane are Windows' own windows, not WinForms controls, so their mouse messages
    /// never reach a WinForms event; every posted message on the thread passes here first. A side-button message or a
    /// Browser Back / Forward key for any window inside a Rock Preview window is taken (so Windows' view can't act on it
    /// too, and Windows never turns it into a WM_APPCOMMAND as well), and the button's release or the key's press goes
    /// back or forward. Other windows (Settings, dialogs, drop-down menus) keep theirs.
    /// </summary>
    sealed class BackForwardInput : IMessageFilter
    {
        public bool PreFilterMessage(ref Message m)
        {
            NavigationStep step;
            bool act;
            switch (m.Msg)
            {
                case WM_XBUTTONUP or WM_NCXBUTTONUP:
                    (step, act) = (NavigationInput.FromXButton(m.WParam), true); break;
                case WM_XBUTTONDOWN or WM_XBUTTONDBLCLK or WM_NCXBUTTONDOWN or WM_NCXBUTTONDBLCLK:
                    (step, act) = (NavigationInput.FromXButton(m.WParam), false); break;
                case WM_KEYDOWN or WM_SYSKEYDOWN:
                    (step, act) = (NavigationInput.FromKey(m.WParam), true); break;
                case WM_KEYUP or WM_SYSKEYUP:
                    (step, act) = (NavigationInput.FromKey(m.WParam), false); break;
                default: return false;
            }
            if (step == NavigationStep.None) return false;
            IntPtr root = GetAncestor(m.HWnd, GA_ROOT);
            if (root == IntPtr.Zero || Control.FromHandle(root) is not MainForm form || form.IsDisposed || form.Disposing) return false;
            // A modal dialog open over the window disables it: the press is still taken, but goes nowhere.
            if (act && IsWindowEnabled(root))
                form.BeginInvoke(() => { if (!form.IsDisposed) form.Go(step); }); // after the message, not inside the filter
            return true;
        }
    }

    /// <summary>
    /// WM_APPCOMMAND's Browser Back / Forward, for what doesn't pass through the filter above: devices and mouse
    /// software that send the command themselves, and the preview pane's handler, which runs in another process and
    /// passes the command up to this window. True when handled.
    /// </summary>
    bool HandleAppCommand(ref Message m)
    {
        if (m.Msg != WM_APPCOMMAND) return false;
        var step = NavigationInput.FromAppCommand(m.LParam);
        if (step == NavigationStep.None) return false;
        // As with the side buttons, a modal dialog open over the window (it is disabled) means the command goes nowhere.
        if (IsWindowEnabled(Handle)) Go(step);
        m.Result = 1; // handled: Windows doesn't pass it on to a shell hook
        return true;
    }

    void Go(NavigationStep step)
    {
        if (step == NavigationStep.Back) GoBack();
        else if (step == NavigationStep.Forward) GoForward();
    }

    [DllImport("user32.dll")] static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);
    [DllImport("user32.dll")] static extern bool IsWindowEnabled(IntPtr hwnd);
}
