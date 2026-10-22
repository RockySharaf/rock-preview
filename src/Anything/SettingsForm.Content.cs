using System.Runtime.InteropServices;
using Anything.Core;
using Anything.Core.Content;

namespace Anything;

/// <summary>
/// Settings › Indexing › Search inside files: the choice with what each costs, how reading goes, where and what to read;
/// and Settings › Search's "Find words inside files". Changes are saved at once and reach the text index when the window
/// closes (<see cref="AnythingApp.ApplyContentSettings"/>); scope changes that drop many documents ask first.
/// </summary>
internal sealed partial class SettingsForm
{
    bool _contentChanged, _estimatesStale = true, _estimatedWhileScanning;
    readonly ComboBox _contentMode = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    readonly NoteLabel _contentNote = new(), _addedFolderNote = new();
    readonly RowList _contentFolders = new();
    SubRow? _myFoldersRow;
    ActionNote? _notByNameNote;
    List<string> _namesAdded = []; // folders the note's "Search them by name too" added (the name index takes them when Settings closes)
    string _problemsText = "";
    string? _readingText;
    readonly FolderList _contentExclude = new();
    readonly ToggleSwitch _myFolders = new(), _readEncrypted = new(), _readOnBattery = new(), _matchContentDefault = new(), _ocr = new();
    readonly List<(ContentKinds Kind, ToggleSwitch Switch)> _kindSwitches = [];
    readonly TextBox _extraExtensions = new();
    readonly ComboBox _maxFileSize = new() { DropDownStyle = ComboBoxStyle.DropDownList }, _networkSpeed = new() { DropDownStyle = ComboBoxStyle.DropDownList };
    SettingCard? _modeCard, _readingCard, _searchContentCard;
    Button? _pauseButton, _showProblems, _retryProblems, _rebuildContent;
    Task? _contentRefresh;
    Task? _estimateTask;
    CancellationTokenSource? _estimateCts, _addedCts;
    ContentEstimate? _documentsEstimate, _picturesEstimate;
    long _lastProblemsLook;
    string? _confirmedExclude;
    Task? _extensionsCommit;
    bool _closeAfterExtensions;
    AddedEstimate? _added;

    static readonly int[] FileSizeChoices = [100, 500, 2048, 0];
    static readonly int[] NetworkChoices = [1024, 4096, 8192, 32768, 0];
    const int UnlimitedKbps = ContentPresentation.NoNetworkLimitKbps;

    /// <summary>
    /// The folder the "Adding …" note talks about, while its count can still get better: counted from a root of the name
    /// index that was still being scanned (<paramref name="WaitForRoot"/>), it is counted again once that root is ready.
    /// </summary>
    sealed record AddedEstimate(string Path, bool NotInNameIndex, string? WaitForRoot);

    /// <summary>An entry of the mode list; its title is the one setup and the hints use.</summary>
    sealed record ContentChoice(ContentMode Mode)
    {
        public override string ToString() => ContentPresentation.OptionTitle(Mode);
    }

    /// <summary>An entry of Largest file read (MB; 0 = no limit).</summary>
    sealed record SizeChoice(int MB)
    {
        public override string ToString() => MB == 0 ? "No limit" : MB >= 1024 ? $"{MB / 1024} GB" : $"{MB} MB";
    }

    /// <summary>An entry of Network speed limit (KB/s; 0 = no limit).</summary>
    sealed record SpeedChoice(int Kbps)
    {
        public override string ToString() => Kbps == 0 ? "No limit" : $"{Kbps / 1024} MB/s";
    }

    // ---- cards ----------------------------------------------------------------------------------

    /// <summary>The cards of Settings › Indexing › Search inside files.</summary>
    Control[] ContentCards()
    {
        bool real = !_app.IsDemo; // the demo's text index is in memory: nothing on disk is chosen or read

        foreach (var m in new[] { ContentMode.NamesOnly, ContentMode.Documents, ContentMode.DocumentsAndPictures }) _contentMode.Items.Add(new ContentChoice(m));
        _contentMode.Width = S(380);
        _contentMode.AccessibleName = "What to search";
        var modeRows = new RowList();
        modeRows.Controls.Add(new SubRow("Choose", _contentMode, detail: null));
        modeRows.Controls.Add(_contentNote);
        _modeCard = Card("What to search", "", null, modeRows);

        _pauseButton = CardButton("Pause...", ShowPauseMenu);
        _showProblems = CardButton("Show files that couldn't be read", ShowProblemFiles);
        _retryProblems = CardButton("Read problem files again", () => { _app.Content.RetryProblems(); RefreshContent(); });
        _rebuildContent = CardButton("Rebuild text index", RebuildContent);
        var buttons = new FlowLayoutPanel { AutoSize = true, WrapContents = true, BackColor = Color.Transparent, Margin = Padding.Empty };
        buttons.Controls.AddRange([_pauseButton, _showProblems, _retryProblems, _rebuildContent]);
        foreach (Control b in buttons.Controls) b.Margin = new Padding(0, S(4), S(8), S(4));
        _readingCard = Card("Reading inside files", "", null, new ButtonRow(buttons));

        _myFolders.AccessibleName = "My folders";
        _myFoldersRow = new SubRow("My folders", _myFolders, detail: "Desktop, Documents, Downloads, Pictures and OneDrive.");
        // Under it: the folders searched inside that the name index doesn't hold (so never read inside), and a button that
        // adds them to the folders searched by name. Nothing changes until it is clicked.
        _notByNameNote = new ActionNote(CardButton(ContentPresentation.SearchByNameButton(2), SearchContentFoldersByName));
        var folderButtons = new FlowLayoutPanel { AutoSize = true, WrapContents = false, BackColor = Color.Transparent, Margin = Padding.Empty };
        var typePath = CardButton("Type a path...", AddTypedContentFolder);
        var addFolder = CardButton("Add a folder", () =>
        {
            using var fb = new FolderBrowserDialog { Description = "Search inside this folder too", UseDescriptionForTitle = true, ShowNewFolderButton = false };
            if (fb.ShowDialog(this) == DialogResult.OK) AddContentFolder(fb.SelectedPath);
        });
        typePath.Enabled = addFolder.Enabled = real;
        folderButtons.Controls.Add(typePath);
        folderButtons.Controls.Add(addFolder);

        var excludeAdd = CardButton("Add a folder", () => _contentExclude.AddWithPicker(this, "Don't search inside this folder"));
        excludeAdd.Enabled = real;
        _contentExclude.Confirm = ConfirmContentExclude;

        var kinds = new RowList();
        // Details fit on one line at 100–150% (about 350 of the 415 pixels a row has at the window's first size); a longer
        // story goes in the detail's tooltip.
        foreach (var (kind, label, detail, tip) in new (ContentKinds, string, string, string?)[]
        {
            (ContentKinds.Pdf, "PDF", "Their text, never the pictures in them.", null),
            (ContentKinds.Office, "Word, Excel and PowerPoint (2007 and later), OpenDocument", "docx, xlsx, pptx, odt, ods, odp and their templates.", null),
            (ContentKinds.Text, "Text", "txt, md, csv, tsv, rtf, htm and html, and the extensions below.", null),
            (ContentKinds.Mail, "Saved emails", "eml and msg: subject, sender, text and attachment names.", null),
            (ContentKinds.Design, "Design files", "psd, ai, idml: their text. indd, eps: title, keywords, fonts and plates.",
                "PSD: text layers. AI and IDML: their text. InDesign (indd) and EPS: title, description, keywords, fonts and plates, not the layout's text."),
        })
        {
            var sw = new ToggleSwitch { AccessibleName = label };
            _kindSwitches.Add((kind, sw));
            var row = new SubRow(label, sw, detail: detail);
            if (tip is not null && row.DetailLabel is { } shown) _tips.SetToolTip(shown, tip);
            kinds.Controls.Add(row);
        }

        _extraExtensions.Width = S(260);
        _extraExtensions.PlaceholderText = "log; json";
        _extraExtensions.Leave += (_, _) => _ = CommitExtraExtensions();
        foreach (int mb in FileSizeChoices) _maxFileSize.Items.Add(new SizeChoice(mb));
        foreach (int kbps in NetworkChoices) _networkSpeed.Items.Add(new SpeedChoice(kbps));
        _maxFileSize.Width = _networkSpeed.Width = S(160);
        var sizeRow = new RowList();
        sizeRow.Controls.Add(new SubRow("Largest document read", _maxFileSize, detail: "Bigger documents are left out; pictures never are."));
        var speedRow = new RowList();
        speedRow.Controls.Add(new SubRow("Network speed limit", _networkSpeed, detail: "The most each network share is read at."));
        _ocr.Enabled = false;
        var ocrCard = Card("Read scans and screenshots (OCR)", "Coming later.", _ocr);
        ocrCard.Enabled = false;

        LoadContentValues();
        WireContentValues();
        Shown += (_, _) =>
        {
            if (_app.TakeContentSettingsFocus() && _modeCard is { } card) BeginInvoke(() => _content.ScrollControlIntoView(card));
        };
        // Closing with the title bar's X raises no Leave: extensions typed but not committed yet are committed here, before
        // the window applies its changes (removing some asks first, so the window closes once that's answered).
        FormClosing += (_, e) =>
        {
            if (e.Cancel || !ExtraExtensionsEdited(out var removed)) return;
            if (removed.Count > 0 && _app.ContentOn)
            {
                if (e.CloseReason == CloseReason.UserClosing)
                {
                    e.Cancel = true;
                    if (!_closeAfterExtensions)
                    {
                        _closeAfterExtensions = true;
                        _ = CloseAfterExtensions();
                    }
                    return;
                }
                // Closing without a chance to ask (Windows shutting down, the app exiting): nothing read is dropped unasked.
                CommitExtraExtensionsKeeping(removed);
                return;
            }
            CommitExtraExtensionsKeeping([]);
        };
        FormClosed += (_, _) =>
        {
            _estimateCts?.Cancel();
            _addedCts?.Cancel();
        };

        return
        [
            Header("Search inside files"),
            _modeCard,
            _readingCard,
            Card("Folders to search inside",
                @"Your own folders, and the folders, drives and network shares (\\server\share) you add. A folder that isn't searched by name yet is added there too.",
                folderButtons, _contentFolders),
            Card("Don't search inside these folders", "Nothing inside them is read, even within the folders above.", excludeAdd, _contentExclude),
            Card("Kinds of files", "Keys, certificates, password stores, password exports, recovery codes and app data are never read (known by their names), whatever is chosen here.", null, kinds),
            Card("Other text extensions", "More plain-text files to read, separated by semicolons. For example log; json", _extraExtensions),
            Card("Sizes and speed", "", null, Rows(sizeRow, speedRow)),
            Card("Read files encrypted by Windows", "Files encrypted with Windows' own encryption (EFS); their words would be kept unencrypted in the index.", _readEncrypted),
            Card("Read on battery", "Off: reading waits until the PC is plugged in.", _readOnBattery),
            ocrCard,
        ];
    }

    static RowList Rows(params RowList[] lists)
    {
        var all = new RowList();
        foreach (var list in lists)
            while (list.Controls.Count > 0) all.Controls.Add(list.Controls[0]);
        return all;
    }

    /// <summary>Settings › Search: whether new searches' plain words also look inside files.</summary>
    SettingCard ContentSearchDefaultCard()
    {
        _searchContentCard = Card("Find words inside files", "", _matchContentDefault);
        UpdateSearchContentCard();
        return _searchContentCard;
    }

    /// <summary>Follows the choice made on the Indexing page, even before it's applied (when Settings closes).</summary>
    void UpdateSearchContentCard()
    {
        var s = _app.Settings.Content;
        bool on = (s.ModeExplicit ? s.Mode : ContentMode.NamesOnly) != ContentMode.NamesOnly;
        _searchContentCard?.SetDescription(on
            ? "Plain search words also find files with those words inside (Search › Match inside files)."
            : "Turn on search inside files in Settings › Indexing first; then plain search words can find words inside files too.");
        _matchContentDefault.Enabled = on;
    }

    // ---- values ---------------------------------------------------------------------------------

    void LoadContentValues()
    {
        bool old = _loading;
        _loading = true;
        var s = _app.Settings.Content;
        _contentMode.SelectedIndex = (int)(s.ModeExplicit ? s.Mode : ContentMode.NamesOnly);
        _myFolders.Checked = s.UseMyFolders;
        _contentExclude.Folders = s.ExcludeFolders;
        foreach (var (kind, sw) in _kindSwitches) sw.Checked = (s.Kinds & kind) != 0;
        _extraExtensions.Text = string.Join("; ", s.ExtraTextExtensions);
        SelectChoice(_maxFileSize, s.MaxFileSizeMB, mb => new SizeChoice(mb), c => ((SizeChoice)c).MB);
        SelectChoice(_networkSpeed, s.NetworkKilobytesPerSecond >= UnlimitedKbps ? 0 : s.NetworkKilobytesPerSecond, k => new SpeedChoice(k), c => ((SpeedChoice)c).Kbps);
        _readEncrypted.Checked = s.ReadEncrypted;
        _readOnBattery.Checked = s.ReadOnBattery;
        _matchContentDefault.Checked = _app.Settings.MatchContent;
        _loading = old;
        RebuildContentFolders();
        UpdateContentCards();
    }

    /// <summary>Selects a value in a list of choices; a value typed into the settings file is kept and listed in its place.</summary>
    static void SelectChoice(ComboBox box, int value, Func<int, object> make, Func<object, int> valueOf)
    {
        int at = -1;
        for (int i = 0; i < box.Items.Count; i++) if (valueOf(box.Items[i]!) == value) at = i;
        if (at < 0)
        {
            box.Items.Insert(box.Items.Count - 1, make(value));
            at = box.Items.Count - 2;
        }
        box.SelectedIndex = at;
    }

    void WireContentValues()
    {
        _contentMode.SelectedIndexChanged += (_, _) => ModeChosen();
        _myFolders.CheckedChanged += async (_, _) =>
        {
            if (_loading) return;
            if (!_myFolders.Checked)
            {
                if (!await ConfirmScopeLoss("My folders", t => t.UseMyFolders = false))
                {
                    _loading = true; _myFolders.Checked = true; _loading = false;
                    return;
                }
            }
            _app.Settings.Content.UseMyFolders = _myFolders.Checked;
            ContentSettingsChanged();
        };
        _contentExclude.Changed += () => { _app.Settings.Content.ExcludeFolders = _contentExclude.Folders; ContentSettingsChanged(); };
        foreach (var (kind, sw) in _kindSwitches)
        {
            var k = kind;
            var toggle = sw;
            toggle.CheckedChanged += async (_, _) =>
            {
                if (_loading) return;
                if (!toggle.Checked)
                {
                    if (!await ConfirmScopeLoss(toggle.AccessibleName ?? k.ToString(), t => t.Kinds &= ~k))
                    {
                        _loading = true; toggle.Checked = true; _loading = false;
                        return;
                    }
                }
                var s = _app.Settings.Content;
                s.Kinds = toggle.Checked ? s.Kinds | k : s.Kinds & ~k;
                ContentSettingsChanged();
            };
        }
        _maxFileSize.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _maxFileSize.SelectedItem is not SizeChoice c) return;
            // No question when it is lowered: the limit applies when a file is read (spec §3 rule 7), so documents already
            // read stay searchable; larger ones changed or added from now on are left out (TooBig).
            _app.Settings.Content.MaxFileSizeMB = c.MB;
            ContentSettingsChanged();
        };
        _networkSpeed.SelectedIndexChanged += (_, _) =>
        {
            if (_loading || _networkSpeed.SelectedItem is not SpeedChoice c) return;
            _app.Settings.Content.NetworkKilobytesPerSecond = c.Kbps == 0 ? UnlimitedKbps : c.Kbps;
            ContentSettingsChanged();
        };
        _readEncrypted.CheckedChanged += (_, _) => { if (_loading) return; _app.Settings.Content.ReadEncrypted = _readEncrypted.Checked; ContentSettingsChanged(); };
        _readOnBattery.CheckedChanged += (_, _) => { if (_loading) return; _app.Settings.Content.ReadOnBattery = _readOnBattery.Checked; ContentSettingsChanged(); };
        _matchContentDefault.CheckedChanged += (_, _) =>
        {
            if (_loading) return;
            _app.Settings.MatchContent = _matchContentDefault.Checked;
            _app.Settings.Save();
        };
    }

    /// <summary>What to read changed: saved now, handed to the text index when the window closes, costs worked out again.</summary>
    void ContentSettingsChanged()
    {
        if (_loading) return;
        _contentChanged = true;
        _app.Settings.Save();
        _estimatesStale = true;
        _estimateCts?.Cancel();
        UpdateContentCards();
        RefreshContent();
    }

    async void ModeChosen()
    {
        if (_loading || _contentMode.SelectedItem is not ContentChoice choice) return;
        var s = _app.Settings.Content;
        var current = s.ModeExplicit ? s.Mode : ContentMode.NamesOnly;
        if (choice.Mode == current && s.ModeExplicit) return;
        if (choice.Mode == ContentMode.NamesOnly && (_app.ContentOn || _app.KeptContent is not null))
        {
            long bytes = _app.KeptContent?.Bytes ?? _app.Content.Status.IndexBytes;
            var page = new TaskDialogPage
            {
                Caption = Text,
                Heading = ContentPresentation.ConfirmNamesOnly(bytes),
                Text = "Only file names are searched then. Choosing a way to search inside files again later reads the files again.",
                Icon = TaskDialogIcon.Warning,
                Buttons = { TaskDialogButton.Yes, TaskDialogButton.No },
                DefaultButton = TaskDialogButton.No,
            };
            if (TaskDialog.ShowDialog(this, page) != TaskDialogButton.Yes)
            {
                _loading = true;
                _contentMode.SelectedIndex = (int)current;
                _loading = false;
                return;
            }
        }
        else if (choice.Mode == ContentMode.Documents && _app.ContentMode == ContentMode.DocumentsAndPictures)
        {
            // Back to documents: pictures leave the text index (design files keep their details).
            bool go = await ConfirmScopeLoss("pictures' details", t => t.Mode = ContentMode.Documents);
            if (IsDisposed || !ReferenceEquals(_contentMode.SelectedItem, choice)) return; // another choice was made meanwhile
            if (!go)
            {
                _loading = true;
                _contentMode.SelectedIndex = (int)current;
                _loading = false;
                return;
            }
        }
        s.Mode = choice.Mode;
        s.ModeExplicit = true;
        _app.Settings.ContentAsked = true; // the offer is answered too
        ContentSettingsChanged();
    }

    /// <summary>The extensions typed in Other text extensions, cleaned up and sorted.</summary>
    List<string> TypedExtraExtensions()
        => [.. ContentTypes.ExtraSet(_extraExtensions.Text.Split([';', '|', ',', ' '], StringSplitOptions.RemoveEmptyEntries)).Order(StringComparer.OrdinalIgnoreCase)];

    /// <summary>The box says something else than the settings; <paramref name="removed"/>: extensions it takes out.</summary>
    bool ExtraExtensionsEdited(out List<string> removed)
    {
        var typed = TypedExtraExtensions();
        var now = _app.Settings.Content.ExtraTextExtensions;
        removed = [.. now.Except(typed, StringComparer.OrdinalIgnoreCase)];
        return !typed.SequenceEqual(now, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Commits Other text extensions (once at a time); taking out some that hold many documents asks first.</summary>
    Task CommitExtraExtensions() => _extensionsCommit is { IsCompleted: false } running ? running : _extensionsCommit = CommitExtraExtensionsCore();

    async Task CommitExtraExtensionsCore()
    {
        if (_loading || IsDisposed) return;
        var typed = TypedExtraExtensions();
        if (!ExtraExtensionsEdited(out var removed))
        {
            _extraExtensions.Text = string.Join("; ", typed); // tidied
            return;
        }
        if (removed.Count > 0)
        {
            string what = string.Join(", ", removed.Select(e => "." + e)) + " files";
            if (!await ConfirmScopeLoss(what, t => t.ExtraTextExtensions = typed))
            {
                if (!IsDisposed) _extraExtensions.Text = string.Join("; ", _app.Settings.Content.ExtraTextExtensions);
                return;
            }
            if (IsDisposed) return;
        }
        _app.Settings.Content.ExtraTextExtensions = typed;
        _extraExtensions.Text = string.Join("; ", typed);
        ContentSettingsChanged();
    }

    /// <summary>Commits what was typed, keeping the extensions given (they would drop documents nobody was asked about).</summary>
    void CommitExtraExtensionsKeeping(IReadOnlyList<string> keep)
    {
        var list = TypedExtraExtensions().Union(keep, StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase).ToList();
        if (list.SequenceEqual(_app.Settings.Content.ExtraTextExtensions, StringComparer.OrdinalIgnoreCase)) return;
        _app.Settings.Content.ExtraTextExtensions = list;
        ContentSettingsChanged();
    }

    /// <summary>The window was closed while extensions taken out were not asked about yet: asks, then closes.</summary>
    async Task CloseAfterExtensions()
    {
        try { await CommitExtraExtensions(); }
        finally { _closeAfterExtensions = false; }
        if (IsDisposed) return;
        if (ExtraExtensionsEdited(out _)) _extraExtensions.Text = string.Join("; ", _app.Settings.Content.ExtraTextExtensions); // declined
        BeginInvoke(Close);
    }

    // ---- the cards' text -------------------------------------------------------------------------

    /// <summary>Descriptions, notes and what is enabled, from the settings, the estimates and the index's status.</summary>
    void UpdateContentCards()
    {
        var s = _app.Settings.Content;
        var chosen = s.ModeExplicit ? s.Mode : ContentMode.NamesOnly;
        long indexBytes = _app.KeptContent?.Bytes ?? (_app.ContentOn ? _app.Content.Status.IndexBytes : 0);
        // The choice whose reading is done (up to date, nothing changed here since): "in use", and the other one priced as
        // the switch it is, not as a first pass from scratch.
        var inUse = _app.ContentOn && !_contentChanged && _app.Content.Status.Phase == ContentPhase.UpToDate ? _app.ContentMode : ContentMode.NamesOnly;
        var notByName = NotSearchedByName();
        var namesAdded = NamesAddedHere();
        _modeCard?.SetDescription(ContentPresentation.ModeCardDescription(_documentsEstimate, _picturesEstimate, indexBytes, inUse, notByName, namesAdded));
        _notByNameNote?.Set(string.Join(" ", new[] { ContentPresentation.AddedToNames(namesAdded), ContentPresentation.NotSearchedByName(notByName) }.Where(t => t.Length > 0)),
            notByName.Count > 0 ? ContentPresentation.SearchByNameButton(notByName.Count) : null);
        var notes = new List<string>();
        if (_app.KeptContent is { } kept && !s.ModeExplicit)
            notes.Add($"A text index from before is kept ({ContentPresentation.Size(kept.Bytes)}): choose a way to search inside files to use it again, or {ContentPresentation.OptionTitle(ContentMode.NamesOnly)} to delete it.");
        if (chosen != ContentMode.NamesOnly && _app.IndexManager.IsElevated) notes.Add(ContentPresentation.AdminNote);
        if (_contentChanged && chosen != _app.ContentMode) notes.Add("Applied when you close Settings.");
        notes.Add(ContentPresentation.PrivacyNote);
        SetNote(_contentNote, string.Join(" ", notes));
        bool on = chosen != ContentMode.NamesOnly;
        foreach (var (_, sw) in _kindSwitches) sw.Enabled = on;
        _myFolders.Enabled = _readEncrypted.Enabled = _readOnBattery.Enabled = _extraExtensions.Enabled = _maxFileSize.Enabled = _networkSpeed.Enabled = on;
        UpdateSearchContentCard();
    }

    static void SetNote(NoteLabel note, string text)
    {
        if (note.Text == text) return;
        note.Text = text;
        note.Parent?.PerformLayout();
        note.Parent?.Parent?.PerformLayout();
        note.Parent?.Parent?.Parent?.PerformLayout();
    }

    /// <summary>
    /// Brings the reading card and the estimates up to date (every 2 seconds while Indexing shows, through RefreshRoots):
    /// the status is cheap; counts and estimates are worked out in the background.
    /// </summary>
    void RefreshContent()
    {
        if (IsDisposed || !IsHandleCreated) return;
        var content = _app.Content;
        ShowContentStatus(content.Status);
        if (_contentRefresh is not { IsCompleted: false } && Environment.TickCount64 - _lastProblemsLook > 10_000 && _app.ContentOn)
        {
            _lastProblemsLook = Environment.TickCount64;
            _contentRefresh = Task.Run(() =>
            {
                string problems;
                try { problems = ContentPresentation.Problems(content.CountsByStatus()); }
                catch (Exception) { problems = ""; }
                try
                {
                    BeginInvoke(() =>
                    {
                        if (IsDisposed) return;
                        _problemsText = problems;
                        ShowContentStatus(content.Status);
                    });
                }
                catch (InvalidOperationException) { } // the window closed meanwhile
            });
        }
        // Counted while the name index was still loading or scanning ("at least"): counted again once it is ready.
        bool namesReady = _app.IndexManager.State == IndexState.Ready;
        if (_estimatedWhileScanning && namesReady)
        {
            _estimatedWhileScanning = false;
            _estimatesStale = true;
        }
        // Estimates read a short sample of files: only once Indexing is looked at.
        if (_estimatesStale && IndexingShown && _estimateTask is not { IsCompleted: false }) StartEstimates();
        // An added folder counted while its root was still being scanned: counted again once the root (and the name index
        // as a whole, or the count still says "at least") is ready.
        if (_added is { WaitForRoot: { } root } added && namesReady && _roots.Any(r => AppSettings.SamePath(r.Path, root) && r.Status == RootStatus.Ready))
        {
            _added = added with { WaitForRoot = null };
            EstimateAddedFolder(added.Path, added.NotInNameIndex, again: true);
        }
    }

    void ShowContentStatus(ContentStatus status)
    {
        bool on = _app.ContentOn;
        string text = !on ? "Off: only file names are searched. Choose a way to search inside files above."
            : _problemsText.Length > 0 ? ContentPresentation.Progress(status) + "\n" + _problemsText
            : ContentPresentation.Progress(status);
        if (_readingCard is { } card && text != _readingText)
        {
            _readingText = text;
            card.SetDescription(text);
        }
        if (_pauseButton is { } pause)
        {
            string label = _app.ContentPausedByUser ? "Resume" : "Pause...";
            if (pause.Text != label) pause.Text = label;
            pause.Enabled = on && status.Phase != ContentPhase.Stopped;
        }
        if (_showProblems is { } show)
        {
            bool problems = on && status.Problems > 0;
            if (show.Visible != problems) show.Visible = problems;
        }
        if (_retryProblems is { } retry) retry.Enabled = on && status.Problems > 0;
        if (_rebuildContent is { } rebuild) rebuild.Enabled = on;
        if (!on) _problemsText = "";
    }

    /// <summary>What the two content choices would cost now (the live index's census, the index's own statistics or a short sample).</summary>
    void StartEstimates()
    {
        _estimatesStale = false;
        _estimatedWhileScanning = _app.IndexManager.State != IndexState.Ready;
        _estimateCts?.Cancel();
        var cts = new CancellationTokenSource();
        _estimateCts = cts;
        var documents = _app.Settings.Content.Clone();
        documents.Mode = ContentMode.Documents;
        documents.ModeExplicit = true;
        var pictures = documents.Clone();
        pictures.Mode = ContentMode.DocumentsAndPictures;
        var content = _app.Content;
        _estimateTask = Task.Run(async () =>
        {
            try
            {
                var d = await content.EstimateAsync(documents, cts.Token).ConfigureAwait(false);
                ShowEstimates(d, null, cts);
                var p = await content.EstimateAsync(pictures, cts.Token).ConfigureAwait(false);
                ShowEstimates(d, p, cts);
            }
            catch (OperationCanceledException) { }
            catch (Exception) { } // no estimate: the card keeps saying "counting"
        });
    }

    void ShowEstimates(ContentEstimate documents, ContentEstimate? pictures, CancellationTokenSource cts)
    {
        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed || cts.IsCancellationRequested) return;
                _documentsEstimate = documents;
                _picturesEstimate = pictures;
                UpdateContentCards();
            });
        }
        catch (InvalidOperationException) { }
    }

    // ---- buttons ---------------------------------------------------------------------------------

    void ShowPauseMenu()
    {
        if (_app.ContentPausedByUser)
        {
            _app.ResumeContent();
            RefreshContent();
            return;
        }
        var m = new ContextMenuStrip();
        m.Items.Add("For 1 hour", null, (_, _) => { _app.PauseContent(TimeSpan.FromHours(1)); RefreshContent(); });
        m.Items.Add("Until I resume or restart", null, (_, _) => { _app.PauseContent(null); RefreshContent(); });
        m.Closed += (_, _) => m.BeginInvoke(m.Dispose);
        if (_pauseButton is { } b) m.Show(b, new Point(0, b.Height));
    }

    /// <summary>Show them: a window searching for the files that couldn't be read.</summary>
    void ShowProblemFiles()
    {
        Close();
        _app.Post(() =>
        {
            var w = _app.CreateWindow(null);
            w.SetSearch("contentstatus:failed");
            _app.ShowWindow(w);
        });
    }

    void RebuildContent()
    {
        var mode = _app.ContentMode;
        var estimate = mode == ContentMode.DocumentsAndPictures ? _picturesEstimate : _documentsEstimate;
        var page = new TaskDialogPage
        {
            Caption = Text,
            Heading = "Delete the text index and read everything again?",
            Text = $"Searches inside files find less until it's done: {ContentPresentation.ChoiceCost(mode, estimate)}, in the background.",
            Icon = TaskDialogIcon.Warning,
            Buttons = { TaskDialogButton.Yes, TaskDialogButton.No },
            DefaultButton = TaskDialogButton.No,
        };
        if (TaskDialog.ShowDialog(this, page) != TaskDialogButton.Yes) return;
        _app.Content.Rebuild();
        RefreshContent();
    }

    // ---- folders -----------------------------------------------------------------------------------

    /// <summary>My folders (one row with a switch), then the folders added, each with Remove, then what adding one costs.</summary>
    void RebuildContentFolders()
    {
        _contentFolders.SuspendLayout();
        foreach (Control c in _contentFolders.Controls.Cast<Control>().ToList())
        {
            _contentFolders.Controls.Remove(c);
            if (c != _myFoldersRow && c != _addedFolderNote && c != _notByNameNote) c.Dispose();
        }
        if (_myFoldersRow is { } mine) _contentFolders.Controls.Add(mine);
        if (_notByNameNote is { } notByName) _contentFolders.Controls.Add(notByName);
        foreach (var folder in _app.Settings.Content.Folders)
        {
            var path = folder;
            var remove = CardButton("Remove", () => RemoveContentFolder(path));
            remove.Enabled = !_app.IsDemo;
            _contentFolders.Controls.Add(new SubRow(path, remove));
        }
        _contentFolders.Controls.Add(_addedFolderNote);
        _contentFolders.ResumeLayout();
        _contentFolders.Parent?.PerformLayout();
        _contentFolders.Parent?.Parent?.PerformLayout();
        _contentFolders.Parent?.Parent?.Parent?.PerformLayout();
    }

    /// <summary>"Type a path...": a folder the picker can't always reach, like a share on another PC.</summary>
    async void AddTypedContentFolder()
    {
        const string title = "Search inside this folder too";
        string text = "";
        while (true)
        {
            var typed = InputDialog.Show(this, title, @"The folder's path, like D:\Projects or \\server\share:", text, text.Length);
            if (string.IsNullOrWhiteSpace(typed) || IsDisposed) return;
            text = typed.Trim();
            if (FolderPath(text) is not { } path)
            {
                MessageBox.Show(this, @"That isn't a folder's path. Type all of it, like D:\Projects or \\server\share.", title, MessageBoxButtons.OK, MessageBoxIcon.Information);
                continue;
            }
            bool there = await FolderExists(path);
            if (IsDisposed) return;
            if (!there && MessageBox.Show(this, $"{path} can't be reached right now. Add it anyway? It's read whenever it can be reached.",
                    title, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes)
                continue;
            AddContentFolder(path);
            return;
        }
    }

    void AddContentFolder(string folder)
    {
        string path = FolderPath(folder) ?? folder;
        var s = _app.Settings.Content;
        if (s.Folders.Any(f => AppSettings.SamePath(f, path))) return;
        s.Folders = [.. s.Folders, path];
        // A folder no root of the name index holds (an unmapped \\nas\jobs) is searched by name too, so its files are followed.
        bool alsoNames = !SearchedByName(path);
        if (alsoNames)
        {
            _app.Settings.Index.Folders = [.. _app.Settings.Index.Folders, path];
            _include.Folders = _app.Settings.Index.Folders;
            IndexChanged();
        }
        RebuildContentFolders();
        ContentSettingsChanged();
        EstimateAddedFolder(path, alsoNames);
    }

    /// <summary>
    /// The folders searched inside (My folders while used, and the folders added) that the name index doesn't hold under
    /// the settings as they are now, by the plan's own answer (the folder bar's: drive switches, links, mounted volumes,
    /// hidden or system folders): nothing in them is read inside. None in the demo, whose text index reads its own.
    /// </summary>
    IReadOnlyList<string> NotSearchedByName()
    {
        if (_app.IsDemo) return [];
        try { return ContentEstimator.FoldersNotSearchedByName(_app.Settings.Content, _app.Settings.Index, coverageOf: _app.CoverageNow); }
        catch (Exception) { return []; }
    }

    /// <summary>
    /// The folders "Search them by name too" added here that are still searched inside and by name: the name index takes
    /// them when Settings closes, so the estimates don't count them yet.
    /// </summary>
    List<string> NamesAddedHere()
    {
        if (_namesAdded.Count == 0) return [];
        var inside = _app.Settings.Content.EffectiveFolders();
        var names = _app.Settings.Index.Folders;
        return [.. _namesAdded.Where(a => names.Any(f => AppSettings.SamePath(f, a)) && inside.Any(f => AppSettings.SamePath(f, a)))];
    }

    /// <summary>
    /// "Search them by name too": the folders searched inside that the name index doesn't hold go on the folders it
    /// searches (Folders › Also search these folders), as a folder added here does, so the name index lists them when
    /// Settings closes and their insides are read from then on. The note and the estimates are brought up to date.
    /// </summary>
    void SearchContentFoldersByName()
    {
        var missing = NotSearchedByName();
        if (missing.Count > 0)
        {
            var ix = _app.Settings.Index;
            ix.Folders = [.. ix.Folders, .. missing.Where(m => !ix.Folders.Any(f => AppSettings.SamePath(f, m)))];
            _include.Folders = ix.Folders;
            _namesAdded = [.. _namesAdded.Where(a => !missing.Any(m => AppSettings.SamePath(a, m))), .. missing];
            IndexChanged();
        }
        _estimatesStale = true;
        _estimateCts?.Cancel();
        UpdateContentCards();
        RefreshContent();
    }

    /// <summary>
    /// Whether the name index holds the folder already under the settings as they are now, by the plan's own answer: in a
    /// folder searched by name, or a drive searched whole, and not left out there (as hidden, behind a link, on a volume
    /// mounted in a folder).
    /// </summary>
    bool SearchedByName(string path) => ContentEstimator.HeldByName(_app.CoverageNow(path));

    /// <summary>
    /// What the added folder costs, counted live: from the name index when it holds it (again once its root is ready, if
    /// it was still being scanned), else by listing it (20 s at most; cut short, the note says how much the share holds).
    /// </summary>
    void EstimateAddedFolder(string path, bool notInNameIndex, bool again = false)
    {
        // Only what it adds is priced: a folder My folders or an added one already holds adds nothing, and the folders
        // searched inside that lie within it are left out of its count.
        var before = _app.Settings.Content.Clone();
        before.Folders = [.. before.Folders.Where(f => !AppSettings.SamePath(f, path))];
        if (before.CoveredBy(path) is { } covering)
        {
            _addedCts?.Cancel();
            _added = null;
            bool mine = before.UseMyFolders && ContentSettings.MyFolders().Any(f => AppSettings.SamePath(f, covering));
            SetNote(_addedFolderNote, ContentPresentation.AddedFolderCovered(path, mine ? null : covering, notInNameIndex));
            return;
        }
        var scope = _app.Settings.Content.Clone();
        scope.UseMyFolders = false;
        scope.Folders = [path];
        scope.ExcludeFolders = [.. scope.ExcludeFolders, .. before.FoldersInside(path)];
        scope.ModeExplicit = true;
        if (scope.Mode == ContentMode.NamesOnly) scope.Mode = ContentMode.Documents;
        string? scanning = notInNameIndex ? null
            : _roots.Where(r => AppSettings.IsUnder(path, r.Path) && r.Status != RootStatus.Ready).Select(r => r.Path).FirstOrDefault();
        if (!again) _added = new AddedEstimate(path, notInNameIndex, scanning);
        if (!again) SetNote(_addedFolderNote, $"Adding {path}: counting its files…");
        var content = _app.Content;
        _addedCts?.Cancel(); // the folder added before is no longer the one the note talks about
        var cts = _addedCts = new CancellationTokenSource();
        var token = cts.Token;
        // Listing a share can take a while: say how far it got.
        var progress = new Progress<ContentCensus>(c =>
        {
            if (IsDisposed || !_addedFolderNote.Text.StartsWith($"Adding {path}: counting", StringComparison.Ordinal)) return;
            SetNote(_addedFolderNote, $"Adding {path}: counting its files… ({c.Kinds.Values.Sum(k => k.Files):N0} so far)");
        });
        Task.Run<(ContentEstimate Estimate, long Used, bool Extrapolated)?>(async () =>
        {
            try
            {
                if (!notInNameIndex) return (await content.EstimateAsync(scope, token).ConfigureAwait(false), 0L, false);
                var census = await ContentEstimator.CountFoldersAsync([path], scope, TimeSpan.FromSeconds(20), progress, token).ConfigureAwait(false);
                // Priced as EstimateAsync prices the folders the name index holds: the index's own statistics, the rest a
                // 3 s sample of this folder's files (folders differ about 2× per kind: the priors alone were 38% off).
                var calibration = await content.CalibrateAsync(census, token).ConfigureAwait(false);
                var estimate = ContentEstimator.Estimate(census, calibration, scope.Mode, scope);
                long used = estimate.AtLeast ? UsedBytes(path) : 0L;
                // A whole drive or share cut short: scaled up from the part listed to what it holds ("about 0.8 GB"). A folder
                // below a root isn't: its drive holds far more than it.
                if (used > 0 && IsDriveOrShareRoot(path) && ContentEstimator.Extrapolate(estimate, census.ListedBytes, used) is { AtLeast: false } scaled)
                    return (scaled, used, true);
                return (estimate, used, false);
            }
            catch (Exception) { return null; }
        }).ContinueWith(t =>
        {
            if (t.Result is not { } found || token.IsCancellationRequested) return;
            try
            {
                BeginInvoke(() =>
                {
                    if (IsDisposed || token.IsCancellationRequested) return;
                    SetNote(_addedFolderNote, ContentPresentation.AddedFolder(path, found.Estimate, notInNameIndex, scope.NetworkKilobytesPerSecond, found.Used, found.Extrapolated));
                });
            }
            catch (InvalidOperationException) { }
        }, TaskScheduler.Default);
    }

    /// <summary>Whether a folder is a whole drive (E:\) or share (\\nas\jobs), not a folder below one.</summary>
    static bool IsDriveOrShareRoot(string folder)
    {
        try { return Path.GetPathRoot(folder) is { Length: > 0 } root && string.Equals(root.TrimEnd('\\'), folder.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase); }
        catch (ArgumentException) { return false; }
    }

    /// <summary>The space a folder's drive or share uses in all (total less free), or 0 when it can't be asked.</summary>
    static long UsedBytes(string folder)
    {
        try
        {
            string dir = folder.EndsWith('\\') ? folder : folder + "\\";
            return GetDiskFreeSpaceExW(dir, out _, out ulong total, out ulong free) && total >= free ? (long)Math.Min(total - free, long.MaxValue) : 0;
        }
        catch (Exception) { return 0; }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool GetDiskFreeSpaceExW(string directoryName, out ulong freeBytesAvailable, out ulong totalBytes, out ulong totalFreeBytes);

    async void RemoveContentFolder(string path)
    {
        if (!await ConfirmScopeLoss(path, t => t.Folders = [.. t.Folders.Where(f => !AppSettings.SamePath(f, path))]) || IsDisposed) return;
        var s = _app.Settings.Content;
        s.Folders = [.. s.Folders.Where(f => !AppSettings.SamePath(f, path))];
        if (_addedFolderNote.Text.StartsWith("Adding " + path + ":", StringComparison.Ordinal))
        {
            _addedCts?.Cancel();
            _added = null;
            SetNote(_addedFolderNote, "");
        }
        RebuildContentFolders();
        ContentSettingsChanged();
    }

    /// <summary>A folder going on "Don't search inside these folders" drops its documents: asked first when there are many.</summary>
    bool ConfirmContentExclude(string folder)
    {
        if (!_app.ContentOn) return true; // nothing read: nothing to lose
        if (_confirmedExclude is { } ok && AppSettings.SamePath(ok, folder))
        {
            _confirmedExclude = null;
            return true;
        }
        _ = ConfirmThenExclude(folder);
        return false; // added once confirmed (counting takes a moment)
    }

    async Task ConfirmThenExclude(string folder)
    {
        if (!await ConfirmScopeLoss(folder, t => t.ExcludeFolders = [.. t.ExcludeFolders, folder]) || IsDisposed) return;
        _confirmedExclude = folder;
        _contentExclude.Add(folder);
    }

    /// <summary>
    /// Before a scope change that drops documents: counts in the name index (in the background) what the scope holds now
    /// and what it would hold after <paramref name="change"/>, and when more than 1,000 documents and pictures leave, asks,
    /// saying what is lost and what reading it again costs. Only what the change takes out counts: a folder My folders
    /// still covers, an excluded folder that was never in scope, or pictures (which don't follow the kinds) aren't lost.
    /// True to go ahead.
    /// </summary>
    async Task<bool> ConfirmScopeLoss(string what, Action<ContentSettings> change)
    {
        if (!_app.ContentOn) return true; // nothing read yet: nothing lost
        var before = _app.Settings.Content.Clone();
        before.Mode = _app.ContentMode; // what the text index holds now
        before.ModeExplicit = true;
        var after = before.Clone();
        change(after);
        var index = _app.IndexManager.Index;
        var content = _app.Content;
        ContentEstimate? removed = null;
        UseWaitCursor = true;
        try
        {
            removed = await Task.Run(async () =>
            {
                try
                {
                    var now = ContentEstimator.CountIndex(index, before, CancellationToken.None);
                    var then = ContentEstimator.CountIndex(index, after, CancellationToken.None);
                    ContentEstimate Lost(ContentCalibration c) => ContentPresentation.Removed(
                        ContentEstimator.Estimate(now, c, before.Mode, before), ContentEstimator.Estimate(then, c, after.Mode, after));
                    var lost = Lost(ContentEstimator.Priors);
                    // How many leave is counted; what reading them again costs is priced as EstimateAsync prices it (the
                    // index's statistics, the rest a 3 s sample of the scope's files), only when the question is asked.
                    if (ContentPresentation.RemovedCount(lost) > 1000)
                        lost = Lost(await content.CalibrateAsync(now, CancellationToken.None).ConfigureAwait(false));
                    return (ContentEstimate?)lost;
                }
                catch (Exception) { return null; }
            });
        }
        finally
        {
            if (!IsDisposed) UseWaitCursor = false;
        }
        if (IsDisposed) return false;
        if (removed is not { } r || ContentPresentation.RemovedCount(r) <= 1000) return true;
        var page = new TaskDialogPage
        {
            Caption = Text,
            Heading = ContentPresentation.ConfirmRemoval(what, r),
            Icon = TaskDialogIcon.Warning,
            Buttons = { TaskDialogButton.Yes, TaskDialogButton.No },
            DefaultButton = TaskDialogButton.No,
        };
        return TaskDialog.ShowDialog(this, page) == TaskDialogButton.Yes;
    }

    /// <summary>
    /// A note under a card's row, wrapped to the card's width, with a button under it while one is wanted; it takes no
    /// room while it has nothing to say.
    /// </summary>
    sealed class ActionNote : Panel
    {
        readonly NoteLabel _note = new();
        readonly Button _button;
        bool _buttonShown;

        public ActionNote(Button button)
        {
            _button = button;
            BackColor = Color.Transparent;
            Margin = Padding.Empty;
            button.Visible = false;
            Controls.Add(_note);
            Controls.Add(button);
        }

        /// <summary>Says <paramref name="text"/> (empty: nothing, no room taken), with a button labelled <paramref name="button"/> under it, or none for null.</summary>
        public void Set(string text, string? button)
        {
            bool shown = text.Length > 0 && button is not null;
            if (_note.Text == text && _buttonShown == shown && (button is null || _button.Text == button)) return;
            _note.Text = text;
            if (button is not null) _button.Text = button;
            _buttonShown = shown;
            _button.Visible = shown;
            PerformLayout();
            Parent?.PerformLayout();
            Parent?.Parent?.PerformLayout();
            Parent?.Parent?.Parent?.PerformLayout();
        }

        public override Size GetPreferredSize(Size proposed)
        {
            int w = proposed.Width is > 0 and < 100_000 ? proposed.Width : Width;
            if (_note.Text.Length == 0) return new Size(w, 0);
            int h = _note.GetPreferredSize(new Size(w, 0)).Height;
            if (_buttonShown) h += _button.GetPreferredSize(Size.Empty).Height + LogicalToDeviceUnits(6);
            return new Size(w, h);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int noteHeight = _note.Text.Length == 0 ? 0 : _note.GetPreferredSize(new Size(ClientSize.Width, 0)).Height;
            _note.Bounds = new Rectangle(0, 0, ClientSize.Width, noteHeight);
            if (!_buttonShown) return;
            var size = _button.GetPreferredSize(Size.Empty);
            _button.Bounds = new Rectangle(0, noteHeight, size.Width, size.Height);
        }
    }

    /// <summary>A row of buttons inside a card, as tall as they need.</summary>
    sealed class ButtonRow : Panel
    {
        readonly FlowLayoutPanel _buttons;

        public ButtonRow(FlowLayoutPanel buttons)
        {
            _buttons = buttons;
            BackColor = Color.Transparent;
            Margin = Padding.Empty;
            Controls.Add(buttons);
        }

        public override Size GetPreferredSize(Size proposed)
        {
            int w = proposed.Width is > 0 and < 100_000 ? proposed.Width : Width;
            return new Size(w, _buttons.GetPreferredSize(new Size(Math.Max(1, w), 0)).Height);
        }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            _buttons.MaximumSize = new Size(ClientSize.Width, 0);
            _buttons.Bounds = new Rectangle(0, 0, ClientSize.Width, _buttons.GetPreferredSize(new Size(ClientSize.Width, 0)).Height);
        }
    }
}
