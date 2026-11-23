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
        _scopeHintDelay.Dispose();
        _scopeHintPulse.Dispose();
    }

    // ---- the search box's folder button ---------------------------------------------------------

    /// <summary>Clicked this often to search everything, the user knows it: it no longer lights up.</summary>
    const int ScopeHintUntilUses = 3;

    readonly System.Windows.Forms.Timer _scopeHintDelay = new() { Interval = 3000 }; // nothing found for 3 seconds: the user is waiting
    readonly System.Windows.Forms.Timer _scopeHintPulse = new() { Interval = 30 };
    long _scopeHintStarted;
    bool _scopeHintAnimate, _scopeHintBuilt;

    /// <summary>
    /// A search inside a folder found nothing: the user may not know it looked only there. Once nothing has been found for
    /// 3 seconds, the folder's button at the start of the search box pulses in the accent colour (a still highlight when Windows'
    /// animations are off), saying "click me to search everything". Not once it has been used for that 3 times.
    /// </summary>
    void UpdateScopeHint()
    {
        if (!_scopeHintBuilt)
        {
            _scopeHintBuilt = true;
            _scopeHintDelay.Tick += (_, _) => { _scopeHintDelay.Stop(); StartScopeHint(); };
            _scopeHintPulse.Tick += (_, _) => PulseScopeHint();
        }
        if (!ScopeHintWanted()) { StopScopeHint(); return; }
        if (_scopeHintPulse.Enabled) return; // already showing for these results
        _scopeHintDelay.Stop();
        _scopeHintDelay.Start();
    }

    /// <summary>Nothing found by a search that looked only in a folder, and the button not yet used enough to be known.</summary>
    bool ScopeHintWanted() => _loaded && !IsDisposed && _search.TextLength > 0 && !_searchPending && _ids.Length == 0
                              && _app.Settings.ScopeChipUses < ScopeHintUntilUses && ScopeButton().Click == SearchScope.Click.ToEverything;

    void StartScopeHint()
    {
        if (!ScopeHintWanted() || ActiveForm != this) return;
        _scopeHintAnimate = AnimationsOn();
        _scopeHintStarted = Environment.TickCount64;
        _scopeHintPulse.Start();
        PulseScopeHint();
    }

    void PulseScopeHint()
    {
        long t = Environment.TickCount64 - _scopeHintStarted;
        if (IsDisposed || !ScopeHintWanted()) { StopScopeHint(); return; }
        // Three pulses, then it stays lightly lit while nothing is found (a still highlight without animations).
        float amount = !_scopeHintAnimate ? 1f
            : t < HintPulses * HintPulseMs ? (float)(0.5 - 0.5 * Math.Cos(2 * Math.PI * (t % HintPulseMs) / HintPulseMs))
            : 0.35f;
        _searchField.SetScopeGlow(amount);
    }

    void StopScopeHint()
    {
        _scopeHintDelay.Stop();
        _scopeHintPulse.Stop();
        _searchField.SetScopeGlow(0);
    }

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
        var turnOn = new LinkLabel { AutoSize = true, Text = "Turn on...", Margin = new Padding(0, 2, 12, 2) };
        // Opens Settings at the option and switches nothing by itself: the user decides there, with the cost in front of them.
        turnOn.LinkClicked += (_, _) => { _thumbStoreBar.Visible = false; _app.ShowSettings(this, "Thumbnails"); };
        var notNow = new LinkLabel { AutoSize = true, Text = "Not now", Margin = new Padding(0, 2, 0, 2) };
        notNow.LinkClicked += (_, _) => _thumbStoreBar.Visible = false;
        _thumbStoreBar.Controls.AddRange([_thumbStoreText, turnOn, notNow]);
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

    /// <summary>The button was clicked to search everything: one more use; from the third, it never lights up again.</summary>
    void ScopeChipUsed()
    {
        StopScopeHint();
        var s = _app.Settings;
        if (s.ScopeChipUses >= ScopeHintUntilUses) return;
        s.ScopeChipUses++;
        s.Save();
    }
}
