using System;
using System.Drawing;
using System.Windows.Forms;
using System.Windows.Forms.Integration;
using Anything.Preview.Util;
using Wpf = System.Windows;
using WpfMedia = System.Windows.Media;

namespace Anything.Preview.UI;

/// <summary>
/// Audio/video player: a WPF MediaPlayer rendered through a VideoDrawing in an ElementHost. The media engine opens the file
/// without delete sharing, so the player holds the file only while it plays or is paused: until Play is pressed the pane
/// shows the poster (the shell's thumbnail, or album art) and the duration from the file's properties, and the player is
/// closed again when playback finishes and on <see cref="Stop"/>. That keeps a previewed clip deletable and renamable.
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
    private string? _path;
    // The result's bitmap (owned by the pane's PreviewResult), shown again whenever the player is closed.
    private Bitmap? _posterImage;
    private TimeSpan? _duration;
    // Where playback starts the next time the player opens (a seek made while nothing was open yet).
    private TimeSpan _resumeAt;
    private bool _playing, _seeking;
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
        _mute.CheckedChanged += (_, _) => { if (_player != null) _player.IsMuted = _mute.Checked; };
        _timer.Tick += (_, _) => UpdatePosition();
        _seek.MouseDown += (_, _) => _seeking = true;
        _seek.MouseUp += (_, _) => { _seeking = false; SeekToBar(); };
        _seek.KeyUp += (_, _) => SeekToBar();
    }

    public bool IsOpen => _player != null;

    /// <summary>Shows the file ready to play. Nothing opens the file until Play is pressed.</summary>
    public void Open(string path, PreviewResult result)
    {
        Stop();
        _path = path;
        _posterImage = result.Image;
        _duration = result.Media?.Duration;
        _resumeAt = TimeSpan.Zero;
        _error = null;
        ShowPoster();
        _seek.Value = 0;
        UpdatePosition();
        _play.Text = "▶";
        _playing = false;
    }

    private void ShowPoster()
    {
        if (_host.Child != null) _host.Child = null;
        _host.Visible = false;
        _poster.SetImage(_posterImage, alpha: true);
        _poster.Visible = true;
    }

    /// <summary>Opens the file in a new player; it starts at <see cref="_resumeAt"/> once the engine has opened it.</summary>
    private void OpenAndPlay()
    {
        if (_path == null) return;
        var p = new WpfMedia.MediaPlayer { ScrubbingEnabled = true, Volume = 0.8, IsMuted = _mute.Checked };
        _player = p;
        p.MediaOpened += (_, _) => OnOpened(p);
        p.MediaFailed += (_, e) => OnFailed(p, e.ErrorException?.Message ?? "unknown error");
        p.MediaEnded += (_, _) => OnEnded(p);
        SetPlaying(true);
        try
        {
            p.Open(new Uri(_path));
        }
        catch (Exception ex)
        {
            OnFailed(p, ex.Message);
        }
    }

    private void OnOpened(WpfMedia.MediaPlayer p)
    {
        if (!ReferenceEquals(p, _player)) return;
        if (p.NaturalDuration.HasTimeSpan) _duration = p.NaturalDuration.TimeSpan;
        if (p.HasVideo)
        {
            int w = Math.Max(1, p.NaturalVideoWidth), h = Math.Max(1, p.NaturalVideoHeight);
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
        if (_resumeAt > TimeSpan.Zero && (_duration == null || _resumeAt < _duration.Value)) p.Position = _resumeAt;
        _resumeAt = TimeSpan.Zero;
        p.IsMuted = _mute.Checked;
        if (_playing) p.Play();
        UpdatePosition();
        StatusChanged?.Invoke(this, _duration is { } d ? "Ready · " + FileUtil.FormatDuration(d) : "Ready");
    }

    private void OnEnded(WpfMedia.MediaPlayer p)
    {
        if (!ReferenceEquals(p, _player)) return;
        // Finished: let go of the file and go back to the poster, ready to play from the start.
        ReleasePlayer();
        _resumeAt = TimeSpan.Zero;
        SetPlaying(false);
        ShowPoster();
        _seek.Value = 0;
        UpdatePosition();
    }

    private void OnFailed(WpfMedia.MediaPlayer p, string message)
    {
        if (!ReferenceEquals(p, _player)) return;
        ReleasePlayer();
        _error = message;
        SetPlaying(false);
        ShowPoster();
        _time.Text = "can't play";
        StatusChanged?.Invoke(this, "Cannot play this file: " + message);
    }

    private void TogglePlay()
    {
        if (_error != null || _path == null) return;
        if (_player == null) { OpenAndPlay(); return; }
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
        if (_duration is not { } d) return;
        var at = TimeSpan.FromMilliseconds(d.TotalMilliseconds * _seek.Value / 1000.0);
        // With nothing open yet (or the engine still opening), remember the spot and start there.
        if (_player == null || !_player.NaturalDuration.HasTimeSpan) _resumeAt = at;
        else _player.Position = at;
        UpdatePosition();
    }

    private void UpdatePosition()
    {
        if (_error != null) return;
        if (_duration is not { } d) { _time.Text = ""; return; }
        var pos = _player != null && _player.NaturalDuration.HasTimeSpan ? _player.Position : _resumeAt;
        if (!_seeking && d.TotalMilliseconds > 0)
            _seek.Value = Math.Clamp((int)(pos.TotalMilliseconds * 1000 / d.TotalMilliseconds), 0, 1000);
        _time.Text = FileUtil.FormatDuration(pos) + " / " + FileUtil.FormatDuration(d);
    }

    /// <summary>Stops and closes the player, which releases its file handle.</summary>
    private void ReleasePlayer()
    {
        var p = _player;
        _player = null;
        if (p != null)
        {
            try { p.Stop(); } catch { }
            try { p.Close(); } catch { }
        }
    }

    /// <summary>Stops playback and releases the media (and its file handle).</summary>
    public void Stop()
    {
        _timer.Stop();
        _playing = false;
        ReleasePlayer();
        _path = null;
        _posterImage = null;
        _duration = null;
        _resumeAt = TimeSpan.Zero;
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
