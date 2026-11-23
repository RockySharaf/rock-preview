using System.Runtime.InteropServices;
using Anything.Core;

namespace Anything;

/// <summary>
/// Teaching the Thumbnails button: a folder that stays open for 3 seconds makes the button pulse in the accent colour
/// (a still highlight when Windows' animations are off), once per folder, 10 times in all, and never again once
/// Thumbnails has been used.
/// </summary>
internal sealed partial class MainForm
{
    const int ThumbnailsHintLimit = 10;
    const int HintPulses = 3, HintPulseMs = 800;

    readonly System.Windows.Forms.Timer _hintDelay = new() { Interval = 3000 };
    readonly System.Windows.Forms.Timer _hintPulse = new() { Interval = 30 };
    long _hintStarted;  // Environment.TickCount64 when the pulse began
    bool _hintAnimate;  // pulse, or hold a still highlight

    [DllImport("user32.dll")]
    static extern bool SystemParametersInfo(uint action, uint param, out bool value, uint winIni);

    /// <summary>"Animation effects" in Windows' settings (SPI_GETCLIENTAREAANIMATION); on when it can't be read.</summary>
    static bool AnimationsOn() => !SystemParametersInfo(0x1042, 0, out bool on, 0) || on;

    void BuildThumbnailsHint()
    {
        _hintDelay.Tick += (_, _) => { _hintDelay.Stop(); ShowThumbnailsHint(); };
        _hintPulse.Tick += (_, _) => PulseThumbnailsHint();
    }

    /// <summary>A folder opened: if it is still open 3 seconds from now, the Thumbnails button shows itself.</summary>
    void QueueThumbnailsHint()
    {
        _hintDelay.Stop();
        var s = _app.Settings;
        if (!s.ThumbnailsHintDone && s.ThumbnailsHintCount < ThumbnailsHintLimit) _hintDelay.Start();
    }

    void ShowThumbnailsHint()
    {
        var s = _app.Settings;
        if (IsDisposed || s.ThumbnailsHintDone || s.ThumbnailsHintCount >= ThumbnailsHintLimit || _hintPulse.Enabled) return;
        // Only while the folder is what the user is looking at: this window in front, the folder showing, the button on the bar.
        if (ActiveForm != this || WindowState == FormWindowState.Minimized || !ExplorerShown || _explorer.CurrentPath == null) return;
        if (!_thumbsButton.Enabled || !_thumbsButton.Visible || _thumbsButton.IsOnOverflow) return;
        s.ThumbnailsHintCount++;
        s.Save();
        _hintAnimate = AnimationsOn();
        _hintStarted = Environment.TickCount64;
        _hintPulse.Start();
        PulseThumbnailsHint();
    }

    void PulseThumbnailsHint()
    {
        long t = Environment.TickCount64 - _hintStarted;
        if (IsDisposed || t >= HintPulses * HintPulseMs) { StopThumbnailsHint(); return; }
        // 0 -> 1 -> 0 in each pulse.
        float amount = _hintAnimate ? (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * (t % HintPulseMs) / HintPulseMs)) : 1f;
        ExplorerRenderer.SetGlow(_thumbsButton, amount);
    }

    void StopThumbnailsHint()
    {
        _hintPulse.Stop();
        ExplorerRenderer.SetGlow(_thumbsButton, 0);
    }

    /// <summary>Thumbnails was used: the button has done its job and stops showing itself, for good.</summary>
    void ThumbnailsUsed()
    {
        _hintDelay.Stop();
        StopThumbnailsHint();
        var s = _app.Settings;
        if (s.ThumbnailsHintDone) return;
        s.ThumbnailsHintDone = true;
        s.Save();
    }

    void DisposeThumbnailsHint()
    {
        _hintDelay.Dispose();
        _hintPulse.Dispose();
        _pointerDelay.Dispose();
        _pointer?.Dismiss();
    }

    // ---- the pointer at the search box's scope, for those who haven't used it ---------------------

    /// <summary>Shown on this start of the program already (one showing per start, whichever window).</summary>
    static bool s_scopePointerShown;

    /// <summary>Its moments on this start that a menu, dialog or drag was in the way of; at the limit it gives up.</summary>
    static int s_scopePointerTries;

    /// <summary>Gave up on this start: something was in the way every time.</summary>
    static bool s_scopePointerGaveUp;

    readonly System.Windows.Forms.Timer _pointerDelay = new() { Interval = 1500 };
    ScopePointer? _pointer;

    void BuildScopePointer()
    {
        _pointerDelay.Tick += (_, _) => { _pointerDelay.Stop(); ShowScopePointer(); };
        // It waits until the window is in front, and goes when it isn't (ScopePointer closes itself then).
        Activated += (_, _) => QueueScopePointer();
        Deactivate += (_, _) => _pointerDelay.Stop();
        _searchField.ScopeClicked += () => _pointer?.Dismiss();
    }

    /// <summary>Whether the pointer is still to show on this start, as far as the settings and this start can tell.</summary>
    bool ScopePointerWanted(bool chipVisible) => ScopePointerRules.Wanted(_app.Settings.ScopePointerStarts, _app.IsDemo,
        s_scopePointerShown || s_scopePointerGaveUp, chipVisible, _app.Settings.ScopeChipUses);

    /// <summary>The window came to the front: if the pointer is still to show, it does in a moment, if the window still is.</summary>
    void QueueScopePointer()
    {
        if (_pointer is not null || !ScopePointerWanted(chipVisible: true)) return;
        _pointerDelay.Stop();
        _pointerDelay.Start();
    }

    void ShowScopePointer()
    {
        try
        {
            if (IsDisposed || !_loaded || _pointer is not null || ActiveForm != this || WindowState == FormWindowState.Minimized || !Visible) return;
            var s = _app.Settings;
            if (!ScopePointerWanted(chipVisible: !_searchField.ScopeChipOnScreen.IsEmpty)) return;
            // One lesson at a time: while the Get started window shows, it looks again after the same delay, uncounted.
            // That window never takes the focus, so its closing brings no Activated to start the delay again. A hidden,
            // minimized or deactivated window returns above without restarting, so this can't run on for ever.
            if (Application.OpenForms.OfType<GetStartedForm>().Any(f => f.Visible && !f.IsDisposed))
            {
                _pointerDelay.Start();
                return;
            }
            // Never under a menu, a drop-down or a dialog, nor during a drag: it tries again after the same delay.
            switch (ScopePointerRules.WhenDue(AnyMenuOpen(), AnyDialogOpen(), MouseCaptured(), s_scopePointerTries))
            {
                case ScopePointerRules.Moment.Wait:
                    s_scopePointerTries++;
                    _pointerDelay.Start();
                    return;
                case ScopePointerRules.Moment.GiveUp:
                    s_scopePointerGaveUp = true;
                    return;
            }
            var pointer = ScopePointer.TryShow(this, _searchField, ScopePointerRules.Line(ScopeButton()));
            if (pointer is null) return;
            _pointer = pointer;
            pointer.FormClosed += (_, _) =>
            {
                if (_pointer == pointer) _pointer = null;
                if (!IsDisposed) _searchField.SetScopeGlow(0);
                // Got it: understood, so it never comes back. Esc, a timeout or a click elsewhere only mean "not now".
                if (pointer.GotIt && s.ScopePointerStarts < ScopePointerRules.Starts)
                {
                    s.ScopePointerStarts = ScopePointerRules.Starts;
                    s.Save();
                }
            };
            s_scopePointerShown = true;
            s.ScopePointerStarts = Math.Min(ScopePointerRules.Starts, s.ScopePointerStarts + 1);
            s.Save();
            _searchField.SetScopeGlow(1); // the chip is lit while the pointer points at it
        }
        catch (Exception) { } // only a hint: never into the message loop
    }

    const uint GUI_INMENUMODE = 0x4, GUI_SYSTEMMENUMODE = 0x8, GUI_POPUPMENUMODE = 0x10;

    [StructLayout(LayoutKind.Sequential)]
    struct GUITHREADINFO
    {
        public int cbSize;
        public uint flags;
        public IntPtr hwndActive, hwndFocus, hwndCapture, hwndMenuOwner, hwndMoveSize, hwndCaret;
        public int caretLeft, caretTop, caretRight, caretBottom;
    }

    delegate bool ThreadWindowProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")] static extern bool GetGUIThreadInfo(uint thread, ref GUITHREADINFO info);
    [DllImport("user32.dll")] static extern bool EnumThreadWindows(uint thread, ThreadWindowProc proc, IntPtr lParam);
    [DllImport("kernel32.dll")] static extern uint GetCurrentThreadId();

    /// <summary>This window's thread as Windows sees it, or null when it can't be read.</summary>
    static GUITHREADINFO? ThreadInfo()
    {
        var info = new GUITHREADINFO { cbSize = Marshal.SizeOf<GUITHREADINFO>() };
        return GetGUIThreadInfo(GetCurrentThreadId(), ref info) ? info : null;
    }

    /// <summary>A menu is open: one of this window's drop-downs (the command bar's, the search history's, a shortcut menu) or a native one.</summary>
    bool AnyMenuOpen()
    {
        if (ThreadInfo() is { } info && (info.flags & (GUI_INMENUMODE | GUI_POPUPMENUMODE | GUI_SYSTEMMENUMODE)) != 0) return true;
        bool open = false;
        EnumThreadWindows(GetCurrentThreadId(), (hwnd, _) =>
        {
            if (FromHandle(hwnd) is ToolStripDropDown { Visible: true } dd && DropDownOwner(dd) is var owner && (owner is null || owner == this))
                open = true;
            return !open;
        }, IntPtr.Zero);
        return open;
    }

    /// <summary>The window a drop-down belongs to, through the menus it opened from; null when it can't be told (then it counts as this one's).</summary>
    static Form? DropDownOwner(ToolStripDropDown dd)
    {
        for (int depth = 0; depth < 32; depth++)
        {
            if (dd.OwnerItem?.Owner is ToolStripDropDown up) { dd = up; continue; }
            if (dd.OwnerItem?.Owner is { } strip) return strip.FindForm();
            if (dd is ContextMenuStrip { SourceControl: { } source }) return source.FindForm();
            break;
        }
        return null;
    }

    /// <summary>A dialog is up over the program, its own or Windows' (this window is disabled then).</summary>
    bool AnyDialogOpen() =>
        !IsWindowEnabled(Handle) || Application.OpenForms.Cast<Form>().Any(f => f != this && f.Visible && f.Modal);

    /// <summary>The mouse is captured: a drag is on its way.</summary>
    static bool MouseCaptured() => ThreadInfo() is { } info && info.hwndCapture != IntPtr.Zero;

    // ---- running as administrator ---------------------------------------------------------------

    readonly FlowLayoutPanel _elevatedBar = new();

    void BuildElevatedDropBar()
    {
        _elevatedBar.Dock = DockStyle.Top;
        _elevatedBar.AutoSize = true;
        _elevatedBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _elevatedBar.WrapContents = true;
        _elevatedBar.Padding = new Padding(6, 3, 6, 3);
        _elevatedBar.BackColor = SystemColors.Info;
        _elevatedBar.ForeColor = SystemColors.InfoText;
        ExplorerTheme.StyleBarLinks(_elevatedBar);
        // The Store's copy has no background service: run normally, it scans folders instead.
        string text = StorePackage.IsInstalled
            ? "Rock Preview is running as administrator, so Windows won't let you drag files into it from programs that aren't (your browser, Outlook, the desktop, File Explorer). Run it normally to drop files in; it then scans folders, which is slower."
            : "Rock Preview is running as administrator, so Windows won't let you drag files into it from programs that aren't (your browser, Outlook, the desktop, File Explorer). Run it normally to drop files in; with instant indexing turned on, searches stay instant.";
        var label = new Label { AutoSize = true, UseMnemonic = false, Text = text, Margin = new Padding(0, 2, 6, 2) };
        var dismiss = new LinkLabel { AutoSize = true, Text = "Dismiss", Margin = new Padding(0, 2, 0, 2) };
        dismiss.LinkClicked += (_, _) => _elevatedBar.Visible = false;
        _elevatedBar.Controls.AddRange([label, dismiss]);
        _elevatedBar.Visible = false;
    }

    /// <summary>
    /// Run as administrator, Windows refuses files dragged in from programs that aren't, before the app sees the drag, so
    /// nothing can say why at the time: the first window shown so says it once, in a bar (remembered in the settings).
    /// </summary>
    void ShowElevatedDropHintOnce()
    {
        var s = _app.Settings;
        if (s.ElevatedDropHintShown || _app.IsDemo || !_app.IndexManager.IsElevated) return;
        s.ElevatedDropHintShown = true;
        s.Save();
        _elevatedBar.Visible = true;
    }

    // ---- keeping thumbnails on disk ----------------------------------------------------------------

    readonly FlowLayoutPanel _thumbStoreBar = new();
    readonly Label _thumbStoreText = new() { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 2, 6, 2) };

    void BuildThumbStoreBar()
    {
        _thumbStoreBar.Dock = DockStyle.Top;
        _thumbStoreBar.AutoSize = true;
        _thumbStoreBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _thumbStoreBar.WrapContents = true;
        _thumbStoreBar.Padding = new Padding(6, 3, 6, 3);
        _thumbStoreBar.BackColor = SystemColors.Info;
        _thumbStoreBar.ForeColor = SystemColors.InfoText;
        ExplorerTheme.StyleBarLinks(_thumbStoreBar);
        var turnOn = new LinkLabel { AutoSize = true, Text = "Turn on...", Margin = new Padding(0, 2, 12, 2) };
        // Opens Settings at the option and switches nothing by itself: the user decides there, with the cost in front of them.
        turnOn.LinkClicked += (_, _) => { _thumbStoreBar.Visible = false; _app.ShowSettings(this, "Thumbnails"); };
        // Shown once ever, so it says no for good rather than promising a later.
        var noThanks = new LinkLabel { AutoSize = true, Text = "No thanks", Margin = new Padding(0, 2, 0, 2) };
        noThanks.LinkClicked += (_, _) => _thumbStoreBar.Visible = false;
        _thumbStoreBar.Controls.AddRange([_thumbStoreText, turnOn, noThanks]);
        _thumbStoreBar.Visible = false;
    }

    /// <summary>The grid's thumbnails took more than a second to appear: the program counts it, and may offer the option.</summary>
    void OnThumbnailWait()
    {
        if (_app.NoteThumbnailWait()) ShowThumbStoreBar();
    }

    /// <summary>
    /// The one-time bar saying thumbnails could appear at once if they were kept on disk, with what that takes for this
    /// PC's files. Shown in the window whose thumbnails kept the user waiting, once ever (remembered in the settings).
    /// </summary>
    internal void ShowThumbStoreBar()
    {
        if (IsDisposed || _thumbStoreBar.Visible || !ThumbnailMode || ExplorerShown) return;
        if (_app.TakeThumbStoreRecommendation() is not { } census) return;
        _thumbStoreText.Text = Anything.Core.Thumbs.ThumbOption.RecommendationText(census,
            Anything.Core.Thumbs.ThumbSizeClass.For(EffectiveThumbSize), _app.Settings.ThumbnailStoreMegabytes);
        // Last in the panel's list, so it is laid out first and takes the top: folder views of tabs opened since were
        // added after it.
        _thumbStoreBar.SendToBack();
        _thumbStoreBar.Visible = true;
    }

    /// <summary>The option was turned on: the bar has nothing left to offer.</summary>
    internal void HideThumbStoreBar() => _thumbStoreBar.Visible = false;

    /// <summary>The option was turned on: the grid may start keeping the rest of the list now, without waiting for a scroll.</summary>
    internal void ThumbStoreChosen() => _grid.StoreChoiceChanged();

    /// <summary>Settings' Clear: the pictures this window holds go too, and the ones on screen are made again.</summary>
    internal void ClearThumbnails() => _grid.ClearThumbs();

    /// <summary>The scope chip was clicked to switch where a search looks: the user knows it now, so the pointer at it never shows again.</summary>
    void ScopeChipUsed()
    {
        var s = _app.Settings;
        if (s.ScopeChipUses > 0) return;
        s.ScopeChipUses = 1;
        s.Save();
    }
}
