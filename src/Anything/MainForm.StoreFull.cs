using Anything.Core.Thumbs;

namespace Anything;

/// <summary>
/// The bar saying "Instant thumbnails" is full: shown above the thumbnails once for each limit, when the store starts
/// dropping thumbnails while this window shows thumbnails (or the next time it does). Full because it holds as many
/// thumbnails as it can, it says so without offering a higher limit. It never takes the focus, and it goes when the limit
/// is raised, the store is cleared, the option is turned off, the bar is answered, or the thumbnails go.
/// </summary>
internal sealed partial class MainForm
{
    readonly FlowLayoutPanel _thumbStoreFullBar = new();
    readonly Label _thumbStoreFullText = new() { AutoSize = true, UseMnemonic = false, Margin = new Padding(0, 2, 6, 2) };
    readonly LinkLabel _thumbStoreFullRaise = new() { AutoSize = true, Text = "Raise the limit", Margin = new Padding(0, 2, 12, 2) };

    void BuildThumbStoreFullBar()
    {
        _thumbStoreFullBar.Dock = DockStyle.Top;
        _thumbStoreFullBar.AutoSize = true;
        _thumbStoreFullBar.AutoSizeMode = AutoSizeMode.GrowAndShrink;
        _thumbStoreFullBar.WrapContents = true;
        _thumbStoreFullBar.Padding = new Padding(6, 3, 6, 3);
        _thumbStoreFullBar.BackColor = SystemColors.Info;
        _thumbStoreFullBar.ForeColor = SystemColors.InfoText;
        ExplorerTheme.StyleBarLinks(_thumbStoreFullBar);
        _thumbStoreFullBar.AccessibleName = "Instant thumbnails is full";
        // Opens Settings at the option, where the limit is typed.
        _thumbStoreFullRaise.LinkClicked += (_, _) => { _app.ThumbStoreFullAnswered(); _app.ShowSettings(this, "Thumbnails"); };
        var ok = new LinkLabel { AutoSize = true, Text = "OK", Margin = new Padding(0, 2, 0, 2) };
        ok.LinkClicked += (_, _) => _app.ThumbStoreFullAnswered();
        _thumbStoreFullBar.Controls.AddRange([_thumbStoreFullText, _thumbStoreFullRaise, ok]);
        _thumbStoreFullBar.Visible = false;
        // About the thumbnails, so it goes with them; a filling not yet said is said when they show again.
        _grid.VisibleChanged += (_, _) =>
        {
            if (_grid.Visible) ShowThumbStoreFullBar();
            else HideThumbStoreFullBar();
        };
    }

    /// <summary>
    /// Shows the bar when this window shows thumbnails and the program says it is due (once for each limit). True when
    /// the bar shows in this window now.
    /// </summary>
    internal bool ShowThumbStoreFullBar()
    {
        if (IsDisposed || !_grid.Visible) return false;
        if (_thumbStoreFullBar.Visible) return true;
        if (!_app.TakeThumbStoreFullNotice()) return false;
        // Full at the most thumbnails it can hold, a higher limit wouldn't help: not offered.
        var (text, offersRaise) = ThumbOption.FullBar(ThumbHost.StoreFullByCount, _app.Settings.ThumbnailStoreMegabytes);
        _thumbStoreFullText.Text = text;
        _thumbStoreFullRaise.Visible = offersRaise;
        // Last in the panel's list, so it is laid out first and takes the top (as the offer bar does).
        _thumbStoreFullBar.SendToBack();
        _thumbStoreFullBar.Visible = true;
        return true;
    }

    /// <summary>The limit raised, the store cleared, the option off, or the bar answered: it goes.</summary>
    internal void HideThumbStoreFullBar() => _thumbStoreFullBar.Visible = false;
}
