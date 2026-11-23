using Anything.Core.Thumbs;

namespace Anything;

/// <summary>
/// Settings › Thumbnails › Instant thumbnails: keep small copies of thumbnails on disk so they appear at once. Off unless
/// turned on; before it is, the card says what it would take for this PC's files (once indexing is complete). On, it shows
/// the limit, where they are kept with the free space there, what they take now, and Clear. Turning it off asks first,
/// saying how much space that frees. Not in the demo.
/// </summary>
internal sealed partial class SettingsForm
{
    readonly ToggleSwitch _thumbStore = new();
    readonly ComboBox _thumbStoreLimit = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly NoteLabel _thumbStoreNote = new();
    readonly System.Windows.Forms.Timer _thumbStoreTimer = new() { Interval = 2000 };
    SettingCard? _thumbStoreCard;
    SubRow? _thumbStoreWhere, _thumbStoreNow;
    Button? _thumbStoreClear, _thumbStoreMove;
    bool _thumbStoreBusy;   // a question or a change is under way: the switch's own events are ours
    int _thumbStoreLook;    // the last look's number: an older one that answers later is dropped
    string _thumbStoreDone = ""; // what the last change did, when it is worth saying (a folder left alone)

    /// <summary>An entry of the limit's list.</summary>
    sealed record LimitChoice(int Megabytes)
    {
        public override string ToString() => ThumbOption.CapText(Megabytes);
    }

    SettingCard ThumbStoreCard()
    {
        var s = _app.Settings;
        _thumbStoreLimit.Width = S(110);
        _thumbStoreLimit.AccessibleName = "Keep at most";
        foreach (int mb in ThumbStore.CapChoicesMegabytes) _thumbStoreLimit.Items.Add(new LimitChoice(mb));
        _thumbStoreLimit.SelectedItem = _thumbStoreLimit.Items.Cast<LimitChoice>().FirstOrDefault(c => c.Megabytes == s.ThumbnailStoreMegabytes)
                                        ?? _thumbStoreLimit.Items[1];
        _thumbStoreMove = CardButton("Change location...", ChangeThumbStoreLocation);
        _thumbStoreClear = CardButton("Clear", ClearThumbStore);
        _thumbStoreClear.AccessibleName = "Clear saved thumbnails";
        var rows = new RowList();
        rows.Controls.Add(new SubRow("Keep at most", _thumbStoreLimit));
        // The path and the free space must always be read whole, at any scaling and however long the path: this row's
        // second line wraps, and the row grows with it.
        rows.Controls.Add(_thumbStoreWhere = new SubRow("Where they're kept", _thumbStoreMove, detail: "", wrapDetail: true));
        rows.Controls.Add(_thumbStoreNow = new SubRow("Saved now", _thumbStoreClear, detail: ""));
        rows.Controls.Add(_thumbStoreNote);
        _thumbStore.Checked = s.ThumbnailStore;
        _thumbStoreCard = Card("Instant thumbnails", ThumbOption.EstimateText(_app.ThumbEstimate, _app.IndexingComplete, s.ThumbnailStoreMegabytes, _app.ThumbEstimateClass),
            _thumbStore, rows);

        _thumbStore.CheckedChanged += (_, _) => ThumbStoreSwitched();
        _thumbStoreLimit.SelectedIndexChanged += (_, _) =>
        {
            if (_thumbStoreLimit.SelectedItem is not LimitChoice choice || choice.Megabytes == _app.Settings.ThumbnailStoreMegabytes) return;
            _app.SetThumbStoreLimit(choice.Megabytes);
            RefreshThumbStore();
        };
        _app.ThumbEstimateChanged += RefreshThumbStore;
        _thumbStoreTimer.Tick += (_, _) => { if (ThumbnailsShown) RefreshThumbStore(); };
        FormClosed += (_, _) =>
        {
            _app.ThumbEstimateChanged -= RefreshThumbStore;
            _thumbStoreTimer.Stop();
        };
        Disposed += (_, _) => _thumbStoreTimer.Dispose();
        RefreshThumbStore();
        return _thumbStoreCard;
    }

    bool ThumbnailsShown => _nav.SelectedIndex >= 0 && _sections[_nav.SelectedIndex].Name == "Thumbnails";

    /// <summary>The Thumbnails page was shown: its numbers are looked at again, and kept up to date while it shows.</summary>
    void ThumbnailsPageShown()
    {
        if (_thumbStoreCard is null) return;
        RefreshThumbStore();
        _thumbStoreTimer.Start();
    }

    async void ThumbStoreSwitched()
    {
        if (_thumbStoreBusy) return;
        if (_thumbStore.Checked)
        {
            _thumbStoreDone = "";
            _app.TurnOnThumbStore();
            RefreshThumbStore();
            return;
        }
        // Off: asked first, with the space it frees. The switch shows On until the answer.
        _thumbStoreBusy = true;
        try
        {
            _thumbStore.Checked = true;
            long bytes = ThumbHost.StoreSizeBytes;
            string folder = _app.Settings.ThumbnailStoreFolder;
            if (bytes < 0) bytes = await Task.Run(() => ThumbStore.MeasureFolder(folder));
            if (IsDisposed) return;
            var turnOff = new TaskDialogButton("Turn off");
            var page = new TaskDialogPage
            {
                Caption = Text,
                Heading = "Turn off instant thumbnails?",
                Text = ThumbOption.TurnOffQuestion(bytes),
                Buttons = { turnOff, TaskDialogButton.Cancel },
                DefaultButton = TaskDialogButton.Cancel,
                Icon = TaskDialogIcon.Information,
            };
            if (TaskDialog.ShowDialog(this, page) != turnOff) return;
            _thumbStore.Checked = false;
            var removal = await _app.TurnOffThumbStore();
            _thumbStoreDone = ThumbOption.RemovalNote(removal, folder);
        }
        finally
        {
            _thumbStoreBusy = false;
            if (!IsDisposed) RefreshThumbStore();
        }
    }

    async void ChangeThumbStoreLocation()
    {
        // A short title, as the program's other folder dialogs have (a long description is squeezed into a narrow column
        // at the dialog's lower left). It opens where the thumbnails are kept now, not wherever Windows last left a
        // Rock Preview dialog: the chosen folder named in the box, so Select Folder keeps it; else the folder that holds
        // the default place.
        using var fb = new FolderBrowserDialog
        {
            Description = "Choose where to keep saved thumbnails",
            UseDescriptionForTitle = true,
            ShowNewFolderButton = true,
        };
        string chosen = _app.Settings.ThumbnailStorePath;
        if (chosen.Length > 0) fb.SelectedPath = chosen;
        else if (Path.GetDirectoryName(_app.Settings.ThumbnailStoreFolder) is { Length: > 0 } holder) fb.InitialDirectory = holder;
        if (fb.ShowDialog(this) != DialogResult.OK || fb.SelectedPath.Length == 0) return;
        if (_thumbStoreMove != null) _thumbStoreMove.Enabled = false;
        try
        {
            _thumbStoreDone = "";
            string old = _app.Settings.ThumbnailStoreFolder;
            var (moved, removal) = await _app.MoveThumbStore(fb.SelectedPath);
            if (moved) _thumbStoreDone = ThumbOption.RemovalNote(removal, old);
            else if (ThumbOption.IsWithin(ThumbOption.StoreFolder(_app.Settings.Index.DatabasePath, fb.SelectedPath), old)
                     && !AppSettings.SamePath(ThumbOption.StoreFolder(_app.Settings.Index.DatabasePath, fb.SelectedPath), old))
                _thumbStoreDone = "That folder is inside the one the thumbnails are kept in now. Choose a folder outside it.";
        }
        finally
        {
            if (!IsDisposed)
            {
                if (_thumbStoreMove != null) _thumbStoreMove.Enabled = true;
                RefreshThumbStore();
            }
        }
    }

    async void ClearThumbStore()
    {
        if (_thumbStoreClear != null) _thumbStoreClear.Enabled = false;
        try { await _app.ClearThumbStore(); }
        finally
        {
            if (!IsDisposed) RefreshThumbStore();
        }
    }

    /// <summary>
    /// The card's numbers now: the estimate (from the app), and in the background the free space where the store is or
    /// would be, what it takes, and whether its location can be used (the store is opened to find out, when it is on).
    /// </summary>
    async void RefreshThumbStore()
    {
        if (_thumbStoreCard is null || IsDisposed) return;
        var s = _app.Settings;
        int look = ++_thumbStoreLook;
        bool on = s.ThumbnailStore;
        int cap = s.ThumbnailStoreMegabytes;
        string folder = s.ThumbnailStoreFolder;
        // Worked out for the thumbnail size in use now: a change of size class changes the figure at the next refresh.
        _thumbStoreCard.SetDescription(ThumbOption.EstimateText(_app.ThumbEstimate, _app.IndexingComplete, cap, _app.ThumbEstimateClass));
        if (!_thumbStoreBusy && _thumbStore.Checked != on)
        {
            _thumbStoreBusy = true;
            _thumbStore.Checked = on;
            _thumbStoreBusy = false;
        }
        // Opened now (on a worker) so that a location that can't be used says so here, not later while browsing.
        var service = on ? ThumbHost.Service : null;
        var leftovers = s.ThumbnailStoreLeftovers.ToList();
        var (free, size, problem, usable, isPrivate, leftoverBytes) = await Task.Run(() =>
        {
            long freeBytes = ThumbOption.FreeBytes(folder);
            long left = 0;
            foreach (var f in leftovers) left += ThumbStore.MeasureFolder(f);
            if (service == null) return (freeBytes, -1L, ThumbStoreProblem.None, false, true, left);
            var store = ThumbHost.OpenNow(service);
            if (store is { IsEnabled: true }) return (freeBytes, store.SizeBytes, ThumbStoreProblem.None, true, ThumbHost.StoreIsPrivate, left);
            return (freeBytes, ThumbStore.MeasureFolder(folder), store?.Problem ?? ThumbStoreProblem.None, false, true, left);
        });
        if (IsDisposed || look != _thumbStoreLook) return;
        string freeText = ThumbOption.FreeSpaceText(free);
        // The row holds only the path and the free space; it wraps, so a longer text makes the card taller.
        string where = freeText.Length > 0 ? $"{folder} · {freeText}" : folder;
        bool whereChanged = _thumbStoreWhere?.DetailLabel?.Text != where;
        _thumbStoreWhere?.SetDetail(where);
        // Saved thumbnails an earlier turn-off couldn't delete are said, not hidden behind "nothing is kept".
        _thumbStoreNow?.SetDetail(on ? ThumbOption.SizeNowText(Math.Max(0, size), cap)
            : leftoverBytes > 0 ? ThumbOption.LeftoverText(leftoverBytes, leftovers[0])
            : "Nothing is kept while this is off");
        // Clear empties the store in use: not one another copy of Rock Preview holds, or that can't be used where it is.
        if (_thumbStoreClear != null) _thumbStoreClear.Enabled = on && usable && size > 0;
        string note = on ? ThumbOption.ProblemText(problem) : "";
        if (on && usable && !isPrivate)
        {
            const string privacy = "This drive can't keep them private: anyone who can open it can see these pictures.";
            note = note.Length > 0 ? note + " " + privacy : privacy;
        }
        if (_thumbStoreDone.Length > 0) note = note.Length > 0 ? note + " " + _thumbStoreDone : _thumbStoreDone;
        // The Microsoft Store removes only the package's own data when Rock Preview is uninstalled: a folder chosen
        // elsewhere outlives it, and the user should know before choosing one. In the note, which wraps, so it is read whole.
        if (StorePackage.IsInstalled && s.ThumbnailStorePath.Length > 0)
        {
            const string uninstall = "Uninstalling Rock Preview from the Microsoft Store doesn't delete this folder: turn this off first to remove it.";
            note = note.Length > 0 ? note + " " + uninstall : uninstall;
        }
        if (_thumbStoreNote.Text != note || whereChanged)
        {
            _thumbStoreNote.Text = note;
            _thumbStoreWhere?.Parent?.PerformLayout(); // the rows measured again at their width (a wrapped line may be taller now)
            _thumbStoreCard.PerformLayout();
            _thumbStoreCard.Parent?.PerformLayout();
        }
    }
}
