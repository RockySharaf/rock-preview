using System.Runtime.InteropServices;

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
    }
}
