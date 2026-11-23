using System.Runtime.ExceptionServices;
using Anything.Core.Thumbs;

namespace Anything;

/// <summary>
/// Settings › Thumbnails › Instant thumbnails: keep small copies of thumbnails on disk so they appear at once. Off unless
/// turned on; before it is, the card says what it would take for this PC's files (once indexing is complete). It shows
/// the limit (typed, in GB) with the free space on its drive, which kinds of thumbnails are kept, where they are kept,
/// what they take now, and Clear; and says when the store is full or its drive nearly is. Turning it off asks first when
/// something is saved, saying how much space that frees, and so does a limit lower than what the store holds. A new
/// location says afterwards that the thumbnails in the old one were deleted. Not in the demo.
/// </summary>
internal sealed partial class SettingsForm
{
    readonly ToggleSwitch _thumbStore = new();
    readonly TextBox _thumbStoreLimit = new();
    readonly LinkLabel _thumbStoreEnough = new(), _thumbStoreEnoughBelow = new();
    readonly CheckBox _keepPictures = new(), _keepVideos = new(), _keepDocuments = new();
    readonly TextBox _keepSkip = new();
    readonly NoteLabel _keepNote = new();
    // The open store holds thumbnails an earlier version saved: the note under the boxes says they are the exception.
    bool _keepEarlier;
    readonly NoteLabel _thumbStoreNote = new();
    readonly System.Windows.Forms.Timer _thumbStoreTimer = new() { Interval = 2000 };
    SettingCard? _thumbStoreCard;
    SubRow? _thumbStoreLimitRow, _thumbStoreWhere, _thumbStoreNow;
    StackRow? _thumbStoreEnoughLine;
    Button? _thumbStoreClear, _thumbStoreMove;
    bool _thumbStoreBusy;   // a question or a change is under way: the switch's own events are ours
    bool _keepShowing;      // the boxes are being set from the settings: their events are ours
    bool _thumbLimitAsking; // the store is measured or the question before a lower limit shows: a commit meanwhile waits
    bool _closeAfterLimit;  // the window was closed meanwhile: it closes once the limit is settled
    Task _thumbLimitCommit = Task.CompletedTask; // the last commit of the limit box, still measuring or done
    int _thumbStoreLook;    // the last look's number: an older one that answers later is dropped
    int _thumbStoreEnoughMb; // what "Use 3 GB: enough for everything on this PC" sets (0: not offered)
    string _thumbStoreDone = ""; // what the last change did, when it is worth saying (a folder left alone, thumbnails deleted by a move)

    SettingCard ThumbStoreCard()
    {
        var s = _app.Settings;
        // The limit is typed: a plain number of GB, committed with Enter or when the box is left.
        _thumbStoreLimit.Width = S(64);
        _thumbStoreLimit.AccessibleName = "Keep at most, in GB";
        _thumbStoreLimit.Text = ThumbOption.LimitBoxText(s.ThumbnailStoreMegabytes);
        _thumbStoreLimit.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true; // no beep
            CommitThumbStoreLimit();
        };
        // Asked after the focus has moved on, not inside Leave: a question shown while the focus changes can lose the click.
        _thumbStoreLimit.Leave += (_, _) => AfterFocusMoves(CommitThumbStoreLimit);
        var limitBox = new UnitBox(_thumbStoreLimit, "GB") { TabIndex = 0 };
        foreach (var link in new[] { _thumbStoreEnough, _thumbStoreEnoughBelow })
        {
            link.AutoSize = false;
            link.UseMnemonic = false;
            link.BackColor = Color.Transparent;
            link.LinkColor = link.ActiveLinkColor = link.VisitedLinkColor = ExplorerTheme.Accent;
            link.LinkClicked += (_, _) => UseEnoughForEverything();
        }
        _thumbStoreEnough.TabIndex = 1; // after the box, though the row lays it out before it
        _thumbStoreMove = CardButton("Change location...", ChangeThumbStoreLocation);
        _thumbStoreClear = CardButton("Clear", ClearThumbStore);
        _thumbStoreClear.AccessibleName = "Clear saved thumbnails";
        var rows = new ThumbRows(PlaceEnoughLink);
        // The free space must always be read whole, at any scaling: this row's second line wraps, and the row grows with it.
        rows.Controls.Add(_thumbStoreLimitRow = new SubRow("Keep at most", limitBox, extra: _thumbStoreEnough, detail: "", wrapDetail: true));
        // In a narrow window the link has no room beside the box: it goes on a line of its own under it.
        _thumbStoreEnoughLine = new StackRow();
        _thumbStoreEnoughLine.Add(_thumbStoreEnoughBelow, shown: false);
        rows.Controls.Add(_thumbStoreEnoughLine);
        rows.Controls.Add(KeepRow(s.ThumbnailStoreKeep));
        // The path must always be read whole too, however long: this row's second line wraps as well.
        rows.Controls.Add(_thumbStoreWhere = new SubRow("Where they're kept", _thumbStoreMove, detail: "", wrapDetail: true));
        // With what each kind takes the line can be long: it wraps too.
        rows.Controls.Add(_thumbStoreNow = new SubRow("Saved now", _thumbStoreClear, detail: "", wrapDetail: true));
        rows.Controls.Add(_thumbStoreNote);
        _thumbStore.Checked = s.ThumbnailStore;
        _thumbStoreCard = Card("Instant thumbnails",
            ThumbOption.EstimateText(_app.ThumbEstimate, _app.IndexingComplete, s.ThumbnailStoreMegabytes, _app.ThumbEstimateClass, s.ThumbnailStoreKeep),
            _thumbStore, rows);

        _thumbStore.CheckedChanged += (_, _) => ThumbStoreSwitched();
        // Another page shown while a box was being typed in: committed, in case no Leave came (a page chosen from code).
        _nav.SelectedIndexChanged += (_, _) =>
        {
            if (!ThumbnailsShown) AfterFocusMoves(() => { CommitThumbStoreLimit(); CommitKeepIfChanged(); });
        };
        // Closing with the title bar's X raises no Leave: both boxes are committed here, as the other boxes of this window
        // are. A lower limit is asked about when the user closes; closing without a chance to ask keeps the old limit.
        // While the store's size has to be measured first (it is still opening), the window closes once that is answered.
        FormClosing += (_, e) =>
        {
            if (e.Cancel) return;
            bool user = e.CloseReason == CloseReason.UserClosing;
            if (user && (_thumbLimitAsking || LimitNeedsMeasuring()))
            {
                e.Cancel = true;
                if (!_closeAfterLimit)
                {
                    _closeAfterLimit = true;
                    _ = CloseAfterLimit();
                }
                return;
            }
            CommitThumbStoreLimit(ask: user);
            CommitKeepIfChanged();
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

    /// <summary>
    /// "Keep thumbnails of": the three groups the space estimate counts, ticked by default, and the file types never kept;
    /// under them which kinds go first when the store is full, and what happens to thumbnails already saved for a kind
    /// or a type switched off.
    /// </summary>
    StackRow KeepRow(ThumbKeep keep)
    {
        var row = new StackRow();
        row.Add(new Label { Text = "Keep thumbnails of", UseMnemonic = false, BackColor = Color.Transparent, ForeColor = ExplorerTheme.Text });
        // What each box covers is ThumbKeep.GroupOf: Excel files and Word or PowerPoint 97-2003 files are never kept, so
        // the documents box names Word and PowerPoint, not Office.
        _keepPictures.Text = "Pictures (photos, Photoshop, camera raw)";
        _keepVideos.Text = "Videos";
        _keepDocuments.Text = "Documents (PDF, Word, PowerPoint, Illustrator, CAD)";
        _keepPictures.AccessibleName = "Keep thumbnails of pictures (photos, Photoshop, camera raw)";
        _keepVideos.AccessibleName = "Keep thumbnails of videos";
        _keepDocuments.AccessibleName = "Keep thumbnails of documents (PDF, Word, PowerPoint, Illustrator, CAD)";
        foreach (var box in new[] { _keepPictures, _keepVideos, _keepDocuments })
        {
            box.UseMnemonic = false;
            box.BackColor = Color.Transparent;
            box.ForeColor = ExplorerTheme.Text;
            box.CheckedChanged += (_, _) => KeepChanged();
            row.Add(box, indent: S(2));
        }
        row.Add(new Label { Text = "But never for these file types:", UseMnemonic = false, BackColor = Color.Transparent, ForeColor = ExplorerTheme.Text }, gapBefore: S(6));
        _keepSkip.Width = S(260);
        _keepSkip.PlaceholderText = "pdf, psd";
        _keepSkip.AccessibleName = "Never keep thumbnails for these file types";
        _keepSkip.KeyDown += (_, e) =>
        {
            if (e.KeyCode != Keys.Enter) return;
            e.SuppressKeyPress = true;
            KeepChanged();
        };
        _keepSkip.Leave += (_, _) => KeepChanged();
        row.Add(_keepSkip);
        row.Add(_keepNote);
        ShowKeep(keep);
        return row;
    }

    void ShowKeep(ThumbKeep keep)
    {
        _keepShowing = true;
        try
        {
            _keepPictures.Checked = keep.Pictures;
            _keepVideos.Checked = keep.Videos;
            _keepDocuments.Checked = keep.Documents;
            _keepSkip.Text = string.Join(" ", keep.Skipped);
            _keepNote.Text = ThumbOption.KeepNote(keep, _keepEarlier);
        }
        finally { _keepShowing = false; }
    }

    /// <summary>A box ticked or cleared, or the types never kept changed: saved, and used for thumbnails made from now on.</summary>
    void KeepChanged()
    {
        if (_keepShowing || _thumbStoreCard is null) return;
        var keep = new ThumbKeep(_keepPictures.Checked, _keepVideos.Checked, _keepDocuments.Checked, ThumbKeep.ParseExtensions(_keepSkip.Text));
        ShowKeep(keep); // the types as they are read: "pdf, .PSD" reads "pdf psd"
        if (!keep.Equals(_app.Settings.ThumbnailStoreKeep)) _app.SetThumbStoreKeep(keep);
        RelayoutThumbStoreCard();
        RefreshThumbStore();
    }

    /// <summary>The types typed but not committed yet (the window closing, another page shown): saved only when they changed.</summary>
    void CommitKeepIfChanged()
    {
        if (_keepShowing || _thumbStoreCard is null || IsDisposed) return;
        var keep = new ThumbKeep(_keepPictures.Checked, _keepVideos.Checked, _keepDocuments.Checked, ThumbKeep.ParseExtensions(_keepSkip.Text));
        if (!keep.Equals(_app.Settings.ThumbnailStoreKeep)) KeepChanged();
    }

    /// <summary>Runs <paramref name="commit"/> once the focus change under way is over (nothing once the window is gone).</summary>
    void AfterFocusMoves(Action commit)
    {
        if (!IsHandleCreated || IsDisposed) return;
        BeginInvoke(() => { if (!IsDisposed) commit(); });
    }

    void CommitThumbStoreLimit() => CommitThumbStoreLimit(ask: true);

    void CommitThumbStoreLimit(bool ask)
    {
        // A commit under way keeps its own task: the window's close waits for that one.
        if (_thumbLimitAsking) return;
        _thumbLimitCommit = ReportingErrors(CommitThumbStoreLimitAsync(ask));
    }

    /// <summary>
    /// Waits for <paramref name="commit"/>; an error in it goes to the app's error report, as one on the UI thread does,
    /// instead of staying unseen in the stored task.
    /// </summary>
    static async Task ReportingErrors(Task commit)
    {
        try { await commit; }
        catch (Exception ex) when (SynchronizationContext.Current is { } ui)
        {
            ui.Post(_ => ExceptionDispatchInfo.Throw(ex), null);
        }
    }

    /// <summary>
    /// The limit box committed: a size sets the limit, anything else puts the old one back. A limit lower than what the
    /// store holds now deletes the oldest thumbnails at once, so it is asked about first (<paramref name="ask"/> false:
    /// no chance to ask, and the old limit stays). While the store is still opening its size isn't known, so its folder
    /// is measured first. Does nothing when the limit is unchanged.
    /// </summary>
    async Task CommitThumbStoreLimitAsync(bool ask)
    {
        if (_thumbStoreCard is null || _thumbLimitAsking || IsDisposed) return;
        int current = _app.Settings.ThumbnailStoreMegabytes;
        int mb = ThumbOption.LimitTyped(_thumbStoreLimit.Text, current);
        long held = _app.Settings.ThumbnailStore ? ThumbHost.StoreSizeBytes : -1;
        if (LimitNeedsMeasuring())
        {
            if (!ask) mb = current; // no time to measure: as with no chance to ask, the old limit stays
            else
            {
                string folder = _app.Settings.ThumbnailStoreFolder;
                _thumbLimitAsking = true;
                try { held = await Task.Run(() => ThumbStore.MeasureFolder(folder)); }
                finally { _thumbLimitAsking = false; }
                if (IsDisposed) return;
                current = _app.Settings.ThumbnailStoreMegabytes;
                // A value typed or chosen during the wait found the commit busy: the box as it is now is what counts.
                mb = ThumbOption.LimitTyped(_thumbStoreLimit.Text, current);
            }
        }
        if (mb != current && ThumbOption.AskBeforeLowering(current, mb, held) && !(ask && LowerLimitConfirmed(held, mb))) mb = current;
        if (IsDisposed) return;
        _thumbStoreLimit.Text = ThumbOption.LimitBoxText(mb);
        if (mb == current) return;
        _app.SetThumbStoreLimit(mb, held);
        RefreshThumbStore();
    }

    /// <summary>A lower limit is typed while the option is on and the store's size isn't known yet (it is still opening).</summary>
    bool LimitNeedsMeasuring()
    {
        if (_thumbStoreCard is null || IsDisposed || !_app.Settings.ThumbnailStore) return false;
        int current = _app.Settings.ThumbnailStoreMegabytes;
        return ThumbOption.LimitTyped(_thumbStoreLimit.Text, current) < current && ThumbHost.StoreSizeBytes < 0;
    }

    /// <summary>The window was closed while the limit waited for the store's size: settles the limit, then closes.</summary>
    async Task CloseAfterLimit()
    {
        try
        {
            await _thumbLimitCommit;
            if (!IsDisposed) CommitThumbStoreLimit(ask: true);
            await _thumbLimitCommit;
        }
        finally { _closeAfterLimit = false; }
        if (IsDisposed) return;
        CommitKeepIfChanged();
        BeginInvoke(Close);
    }

    /// <summary>Asks before a limit below what the store holds is applied; Cancel is the default.</summary>
    bool LowerLimitConfirmed(long storeBytes, int megabytes)
    {
        _thumbLimitAsking = true;
        try
        {
            var lower = new TaskDialogButton("Lower the limit");
            var page = new TaskDialogPage
            {
                Caption = Text,
                Text = ThumbOption.LowerLimitQuestion(storeBytes, megabytes),
                Buttons = { lower, TaskDialogButton.Cancel },
                DefaultButton = TaskDialogButton.Cancel,
                Icon = TaskDialogIcon.Information,
            };
            return TaskDialog.ShowDialog(this, page) == lower;
        }
        finally { _thumbLimitAsking = false; }
    }

    /// <summary>"Use 3 GB: enough for everything on this PC": the limit becomes the estimate for this PC, rounded up to whole GB (a lower one still asks).</summary>
    void UseEnoughForEverything()
    {
        if (_thumbStoreEnoughMb <= 0) return;
        _thumbStoreLimit.Text = ThumbOption.LimitBoxText(_thumbStoreEnoughMb);
        CommitThumbStoreLimit();
    }

    /// <summary>
    /// Where the "enough for everything" link goes for rows <paramref name="width"/> wide: beside the box while the label and its
    /// second line keep room to be read, else on its own line under it; hidden while not offered. Asked before the rows
    /// are measured and laid out, so both see the same.
    /// </summary>
    void PlaceEnoughLink(int width)
    {
        if (_thumbStoreLimitRow is null || _thumbStoreEnoughLine is null) return;
        bool offered = _thumbStoreEnoughMb > 0;
        int besideBox = _thumbStoreLimit.Parent!.GetPreferredSize(Size.Empty).Width + _thumbStoreEnough.GetPreferredSize(Size.Empty).Width + 2 * S(12);
        bool fits = width - besideBox >= S(160);
        _thumbStoreLimitRow.ShowExtra(offered && fits);
        _thumbStoreEnoughLine.SetShown(_thumbStoreEnoughBelow, offered && !fits);
    }

    void RelayoutThumbStoreCard()
    {
        if (_thumbStoreCard is null) return;
        _thumbStoreLimitRow?.Parent?.PerformLayout(); // the rows measured again at their width (a wrapped line may be taller now)
        _thumbStoreCard.PerformLayout();
        _thumbStoreCard.Parent?.PerformLayout();
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
        // Off: asked first, with the space it frees, when something is saved. The switch shows On until the answer.
        _thumbStoreBusy = true;
        try
        {
            _thumbStore.Checked = true;
            long bytes = ThumbHost.StoreSizeBytes;
            string folder = _app.Settings.ThumbnailStoreFolder;
            if (bytes < 0) bytes = await Task.Run(() => ThumbStore.MeasureFolder(folder));
            if (IsDisposed) return;
            // Nothing worth asking about (under 1 MB saved): it just turns off.
            if (ThumbOption.AskBeforeTurningOff(bytes))
            {
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
            }
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
            // What the old place holds, read before the move deletes it: a move that deleted thumbnails says so.
            bool on = _app.Settings.ThumbnailStore;
            long held = on ? ThumbHost.StoreSizeBytes : -1;
            if (on && held < 0) held = await Task.Run(() => ThumbStore.MeasureFolder(old));
            if (IsDisposed) return;
            var (moved, removal) = await _app.MoveThumbStore(fb.SelectedPath);
            if (moved)
            {
                string note = ThumbOption.RemovalNote(removal, old);
                _thumbStoreDone = note.Length > 0 ? note : ThumbOption.MovedNote(moved, on, held, removal);
            }
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
    /// would be, what it takes, whether it is full or its drive nearly is, and whether its location can be used (the
    /// store is opened to find out, when it is on).
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
        var census = _app.ThumbEstimate;
        _thumbStoreCard.SetDescription(ThumbOption.EstimateText(census, _app.IndexingComplete, cap, _app.ThumbEstimateClass, s.ThumbnailStoreKeep));
        if (!_thumbStoreBusy && _thumbStore.Checked != on)
        {
            _thumbStoreBusy = true;
            _thumbStore.Checked = on;
            _thumbStoreBusy = false;
        }
        // The box shows the limit as saved, except while the user is typing in it.
        if (!_thumbStoreLimit.Focused) _thumbStoreLimit.Text = ThumbOption.LimitBoxText(cap);
        // "Enough for everything" once the estimate is known, only when it would change the limit, and not for more files
        // than the store can hold at all.
        int enough = _app.IndexingComplete && census is { } c && ThumbOption.OffersEnough(c.Files) ? ThumbOption.EnoughMegabytes(c.BytesAt(_app.ThumbEstimateClass)) : 0;
        if (enough == cap) enough = 0;
        bool enoughChanged = enough != _thumbStoreEnoughMb;
        _thumbStoreEnoughMb = enough;
        if (enough > 0) _thumbStoreEnough.Text = _thumbStoreEnoughBelow.Text = ThumbOption.EnoughText(enough);
        // Opened now (on a worker) so that a location that can't be used says so here, not later while browsing.
        var service = on ? ThumbHost.Service : null;
        var leftovers = s.ThumbnailStoreLeftovers.ToList();
        var (free, size, problem, usable, isPrivate, leftoverBytes, full, fullByCount, diskLow, split) = await Task.Run(() =>
        {
            long freeBytes = ThumbOption.FreeBytes(folder);
            long left = 0;
            foreach (var f in leftovers) left += ThumbStore.MeasureFolder(f);
            if (service == null) return (freeBytes, -1L, ThumbStoreProblem.None, false, true, left, false, false, false, default(ThumbStoreSplit));
            var store = ThumbHost.OpenNow(service);
            // What each kind takes comes from figures the open store keeps in memory: no disk is read for it.
            if (store is { IsEnabled: true })
                return (freeBytes, store.SizeBytes, ThumbStoreProblem.None, true, ThumbHost.StoreIsPrivate, left, store.IsFull, store.FullByCount, store.DiskLow, store.Split);
            return (freeBytes, ThumbStore.MeasureFolder(folder), store?.Problem ?? ThumbStoreProblem.None, false, true, left, false, false, false, default(ThumbStoreSplit));
        });
        if (IsDisposed || look != _thumbStoreLook) return;
        // Under the limit: the free space on its drive, and whether the limit is more than that leaves room for.
        string space = ThumbOption.LimitSpaceText(free, folder, cap, on ? Math.Max(0, size) : 0);
        bool changed = enoughChanged || _thumbStoreLimitRow?.DetailLabel?.Text != space || _thumbStoreWhere?.DetailLabel?.Text != folder;
        _thumbStoreLimitRow?.SetDetail(space);
        // The row holds only the path (the free space is under the limit); it wraps, so a longer path makes the card taller.
        _thumbStoreWhere?.SetDetail(folder);
        // Saved thumbnails an earlier turn-off couldn't delete are said, not hidden behind "nothing is kept".
        string now = on ? ThumbOption.SizeNowText(Math.Max(0, size), cap, split)
            : leftoverBytes > 0 ? ThumbOption.LeftoverText(leftoverBytes, leftovers[0])
            : "Nothing is kept while this is off";
        // This row wraps now (what each kind takes can make it long): a new text may change its height.
        changed |= _thumbStoreNow?.DetailLabel?.Text != now;
        _thumbStoreNow?.SetDetail(now);
        // Known only once the store is open: whether it holds thumbnails an earlier version saved, which the rules above don't cover.
        bool earlier = on && split.Earlier > 0;
        if (earlier != _keepEarlier)
        {
            _keepEarlier = earlier;
            _keepNote.Text = ThumbOption.KeepNote(_app.Settings.ThumbnailStoreKeep, earlier);
            changed = true;
        }
        // Clear empties the store in use: not one another copy of Rock Preview holds, or that can't be used where it is.
        if (_thumbStoreClear != null) _thumbStoreClear.Enabled = on && usable && size > 0;
        string note = on ? ThumbOption.ProblemText(problem) : "";
        void Say(string line) => note = note.Length > 0 ? note + " " + line : line;
        // What the store does now that it can't keep everything: dropping the oldest, or not saving at all.
        if (on && usable && diskLow) Say(ThumbOption.DiskLowText(folder));
        else if (on && usable && full) Say(fullByCount ? ThumbOption.FullByCountText : ThumbOption.FullText);
        if (on && usable && !isPrivate) Say("This drive can't keep them private: anyone who can open it can see these pictures.");
        if (_thumbStoreDone.Length > 0) Say(_thumbStoreDone);
        // The Microsoft Store removes only the package's own data when Rock Preview is uninstalled: a folder chosen
        // elsewhere outlives it, and the user should know before choosing one. In the note, which wraps, so it is read whole.
        if (StorePackage.IsInstalled && s.ThumbnailStorePath.Length > 0)
            Say("Uninstalling Rock Preview from the Microsoft Store doesn't delete this folder: turn this off first to remove it.");
        if (_thumbStoreNote.Text != note || changed)
        {
            _thumbStoreNote.Text = note;
            RelayoutThumbStoreCard();
        }
    }

    /// <summary>The card's rows: as <see cref="RowList"/>, and its <see cref="StackRow"/>s as wide as it, measured for that width.</summary>
    /// <param name="widthChanging">Asked with the width before the rows are measured or laid out (a row's content may depend on it).</param>
    sealed class ThumbRows(Action<int> widthChanging) : RowList
    {
        readonly Action<int> _widthChanging = widthChanging;

        protected override void OnLayout(LayoutEventArgs e)
        {
            _widthChanging(ClientSize.Width);
            foreach (Control c in Controls)
                if (c is StackRow) c.Size = new Size(ClientSize.Width, c.GetPreferredSize(new Size(ClientSize.Width, 0)).Height);
            base.OnLayout(e);
        }

        public override Size GetPreferredSize(Size proposed)
        {
            int w = proposed.Width is > 0 and < 100_000 ? proposed.Width : Width;
            _widthChanging(w);
            return base.GetPreferredSize(new Size(w, proposed.Height));
        }
    }

    /// <summary>
    /// Controls one under another, each as wide as it needs up to the row's width: labels and check boxes wrap when the
    /// row is narrower than their text, a text box keeps its width while it fits, a <see cref="NoteLabel"/> fills the
    /// width. Takes no room while nothing in it is shown.
    /// </summary>
    sealed class StackRow : Panel
    {
        sealed class Item(Control control, int indent, int gapBefore, bool shown)
        {
            public readonly Control Control = control;
            public readonly int Indent = indent, GapBefore = gapBefore;
            public bool Shown = shown;
        }

        readonly List<Item> _items = [];

        public StackRow()
        {
            BackColor = Color.Transparent;
            Margin = Padding.Empty;
        }

        int S(int v) => LogicalToDeviceUnits(v);

        /// <param name="indent">Room at the left, in device pixels.</param>
        /// <param name="gapBefore">More room above it than the usual small gap, in device pixels.</param>
        public void Add(Control control, int indent = 0, int gapBefore = 0, bool shown = true)
        {
            if (control is Label and not NoteLabel || control is CheckBox) control.AutoSize = false; // sized here, wrapped when narrow
            control.Visible = shown;
            control.Margin = Padding.Empty;
            _items.Add(new Item(control, indent, gapBefore, shown));
            Controls.Add(control);
        }

        /// <summary>Shows or hides one of its controls (the row's own record: Visible reads false while the page is hidden).</summary>
        public void SetShown(Control control, bool shown)
        {
            var item = _items.Find(i => i.Control == control);
            if (item is null || item.Shown == shown) return;
            item.Shown = shown;
            control.Visible = shown;
        }

        Size SizeOf(Control c, int room)
        {
            room = Math.Max(1, room);
            switch (c)
            {
                case NoteLabel note:
                    return new Size(room, note.GetPreferredSize(new Size(room, 0)).Height);
                case TextBox box:
                    return new Size(Math.Min(box.Width, room), box.PreferredHeight);
                case CheckBox check:
                {
                    var natural = check.GetPreferredSize(Size.Empty);
                    if (natural.Width <= room) return natural;
                    // Narrower than its text: the text wraps beside the box, and the row grows.
                    int glyph = Math.Max(0, natural.Width - TextRenderer.MeasureText(check.Text, check.Font).Width);
                    var text = TextRenderer.MeasureText(check.Text, check.Font, new Size(Math.Max(1, room - glyph), int.MaxValue), TextFormatFlags.WordBreak);
                    return new Size(room, Math.Max(natural.Height, text.Height + S(4)));
                }
                case Label:
                {
                    // Its own size on one line; narrower than that, the text wraps and the label grows by the lines added.
                    var natural = c.GetPreferredSize(Size.Empty);
                    if (natural.Width <= room) return natural;
                    var one = TextRenderer.MeasureText(c.Text, c.Font);
                    var text = TextRenderer.MeasureText(c.Text, c.Font, new Size(room, int.MaxValue), TextFormatFlags.WordBreak);
                    return new Size(room, natural.Height + Math.Max(0, text.Height - one.Height));
                }
                default:
                {
                    var p = c.GetPreferredSize(Size.Empty);
                    return new Size(Math.Min(room, p.Width), p.Height);
                }
            }
        }

        public override Size GetPreferredSize(Size proposed)
        {
            int w = proposed.Width is > 0 and < 100_000 ? proposed.Width : Width;
            int h = 0;
            bool any = false;
            foreach (var item in _items)
            {
                if (!item.Shown) continue;
                h += (any ? S(4) : S(6)) + item.GapBefore + SizeOf(item.Control, w - item.Indent).Height;
                any = true;
            }
            return new Size(w, any ? h + S(6) : 0);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int y = 0;
            bool any = false;
            foreach (var item in _items)
            {
                if (!item.Shown) continue;
                y += (any ? S(4) : S(6)) + item.GapBefore;
                var size = SizeOf(item.Control, ClientSize.Width - item.Indent);
                item.Control.Bounds = new Rectangle(item.Indent, y, size.Width, size.Height);
                y += size.Height;
                any = true;
            }
        }
    }

    /// <summary>A text box with its unit after it ("8" GB), as one control for a <see cref="SubRow"/>.</summary>
    sealed class UnitBox : Panel
    {
        readonly TextBox _box;
        readonly Label _unit;

        public UnitBox(TextBox box, string unit)
        {
            _box = box;
            _unit = new Label { Text = unit, AutoSize = true, UseMnemonic = false, BackColor = Color.Transparent, ForeColor = ExplorerTheme.Text };
            BackColor = Color.Transparent;
            Margin = Padding.Empty;
            box.TabIndex = 0;
            Controls.Add(box);
            Controls.Add(_unit);
        }

        int Gap => LogicalToDeviceUnits(6);

        public override Size GetPreferredSize(Size proposed)
        {
            var unit = _unit.GetPreferredSize(Size.Empty);
            return new Size(_box.Width + Gap + unit.Width, Math.Max(_box.PreferredHeight, unit.Height));
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            var unit = _unit.GetPreferredSize(Size.Empty);
            _box.Bounds = new Rectangle(0, (ClientSize.Height - _box.PreferredHeight) / 2, _box.Width, _box.PreferredHeight);
            _unit.Bounds = new Rectangle(_box.Width + Gap, (ClientSize.Height - unit.Height) / 2, unit.Width, unit.Height);
        }
    }
}
