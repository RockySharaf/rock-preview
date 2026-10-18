using System;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Anything.Preview.Util;
using Wpf = System.Windows;
using WpfMedia = System.Windows.Media;

namespace Anything.Preview.UI;

/// <summary>
/// Audio/video player: a WPF MediaPlayer rendered through a VideoDrawing in an ElementHost. Video opens paused on its
/// first frame (muted while priming); audio shows album art and never autoplays. Released on <see cref="Stop"/>.
/// </summary>
internal sealed class MediaView : UserControl
{
    private readonly ElementHost _host = new() { Dock = DockStyle.Fill, BackColor = Color.Black, Visible = false };
    private readonly ImageView _poster = new() { Dock = DockStyle.Fill };
    private readonly Panel _bar = new() { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(6, 4, 6, 4) };
    private readonly Button _play = new() { Text = "▶", Width = 40, Dock = DockStyle.Left, FlatStyle = FlatStyle.System };
    private readonly CheckBox _mute = new() { Text = "Mute", AutoSize = true, Dock = DockStyle.Right, Padding = new Padding(6, 3, 0, 0) };
    private readonly Label _time = new() { AutoSize = false, Width = 110, Dock = DockStyle.Right, TextAlign = ContentAlignment.MiddleCenter };
    private readonly TrackBar _seek = new() { Dock = DockStyle.Fill, Minimum = 0, Maximum = 1000, TickStyle = TickStyle.None, AutoSize = false, Height = 26 };
    private readonly Timer _timer = new() { Interval = 250 };
    private WpfMedia.MediaPlayer? _player;
    private bool _isVideo, _playing, _priming, _seeking;
    private string? _error;

    public event EventHandler<string>? StatusChanged;

    public MediaView()
    {
        _bar.Controls.Add(_seek);
        _bar.Controls.Add(_time);
        _bar.Controls.Add(_mute);
        _bar.Controls.Add(_play);
        Controls.Add(_host);
        Controls.Add(_poster);
        Controls.Add(_bar);
        _play.Click += (_, _) => TogglePlay();
        _mute.CheckedChanged += (_, _) => { if (_player != null && !_priming) _player.IsMuted = _mute.Checked; };
        _timer.Tick += (_, _) => UpdatePosition();
        _seek.MouseDown += (_, _) => _seeking = true;
        _seek.MouseUp += (_, _) => { _seeking = false; SeekToBar(); };
        _seek.KeyUp += (_, _) => SeekToBar();
    }

    public bool IsOpen => _player != null;

    public void Open(string path, PreviewResult result)
    {
        Stop();
        _isVideo = result.Media?.IsVideo ?? false;
        _error = null;
        _poster.SetImage(result.Image, alpha: true);
        _poster.Visible = true;
        _host.Visible = false;
        _seek.Value = 0;
        _time.Text = result.Media?.Duration is { } d ? "0:00 / " + FileUtil.FormatDuration(d) : "";
        _play.Text = "▶";
        _playing = false;

        var p = new WpfMedia.MediaPlayer { ScrubbingEnabled = true, Volume = 0.8 };
        _player = p;
        p.MediaOpened += (_, _) => OnOpened(p);
        p.MediaFailed += (_, e) => OnFailed(p, e.ErrorException?.Message ?? "unknown error");
        p.MediaEnded += (_, _) => { if (ReferenceEquals(p, _player)) { p.Pause(); p.Position = TimeSpan.Zero; SetPlaying(false); } };
        try
        {
            p.Open(new Uri(path));
            if (_isVideo)
            {
                // Prime the pipeline muted so the first frame is decoded, then pause in MediaOpened.
                _priming = true;
                p.IsMuted = true;
                p.Play();
            }
        }
        catch (Exception ex)
        {
            OnFailed(p, ex.Message);
        }
    }

    private void OnOpened(WpfMedia.MediaPlayer p)
    {
        if (!ReferenceEquals(p, _player)) return;
        if (_isVideo)
        {
            p.Pause();
            p.Position = TimeSpan.Zero;
            p.IsMuted = _mute.Checked;
            _priming = false;
            int w = Math.Max(1, p.NaturalVideoWidth), h = Math.Max(1, p.NaturalVideoHeight);
            if (p.HasVideo)
            {
                var drawing = new WpfMedia.VideoDrawing { Player = p, Rect = new Wpf.Rect(0, 0, w, h) };
                var img = new Wpf.Controls.Image
                {
                    Source = new WpfMedia.DrawingImage(drawing),
                    Stretch = WpfMedia.Stretch.Uniform,
                };
                var grid = new Wpf.Controls.Grid { Background = WpfMedia.Brushes.Black };
                grid.Children.Add(img);
                _host.Child = grid;
                _host.Visible = true;
                _poster.Visible = false;
            }
        }
        UpdatePosition();
        StatusChanged?.Invoke(this, p.NaturalDuration.HasTimeSpan ? "Ready · " + FileUtil.FormatDuration(p.NaturalDuration.TimeSpan) : "Ready");
    }

    private void OnFailed(WpfMedia.MediaPlayer p, string message)
    {
        if (!ReferenceEquals(p, _player)) return;
        _error = message;
        _priming = false;
        _host.Visible = false;
        _poster.Visible = true;
        _time.Text = "can't play";
        StatusChanged?.Invoke(this, "Cannot play this file: " + message);
    }

    private void TogglePlay()
    {
        if (_player == null || _error != null) return;
        if (_playing) { _player.Pause(); SetPlaying(false); }
        else { _player.IsMuted = _mute.Checked; _player.Play(); SetPlaying(true); }
    }

    private void SetPlaying(bool playing)
    {
        _playing = playing;
        _play.Text = playing ? "❚❚" : "▶";
        if (playing) _timer.Start(); else { _timer.Stop(); UpdatePosition(); }
    }

    private void SeekToBar()
    {
        if (_player == null || !_player.NaturalDuration.HasTimeSpan) return;
        var d = _player.NaturalDuration.TimeSpan;
        _player.Position = TimeSpan.FromMilliseconds(d.TotalMilliseconds * _seek.Value / 1000.0);
        UpdatePosition();
    }

    private void UpdatePosition()
    {
        if (_player == null) return;
        if (!_player.NaturalDuration.HasTimeSpan) return;
        var d = _player.NaturalDuration.TimeSpan;
        var pos = _player.Position;
        if (!_seeking && d.TotalMilliseconds > 0)
            _seek.Value = Math.Clamp((int)(pos.TotalMilliseconds * 1000 / d.TotalMilliseconds), 0, 1000);
        _time.Text = FileUtil.FormatDuration(pos) + " / " + FileUtil.FormatDuration(d);
    }

    /// <summary>Stops playback and releases the media (and its file handle).</summary>
    public void Stop()
    {
        _timer.Stop();
        _playing = false;
        _priming = false;
        var p = _player;
        _player = null;
        if (p != null)
        {
            try { p.Stop(); } catch { }
            try { p.Close(); } catch { }
        }
        if (_host.Child != null) _host.Child = null;
        _host.Visible = false;
        _poster.Clear();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            Stop();
            _timer.Dispose();
        }
        base.Dispose(disposing);
    }
}
