namespace WeeklyUsageIndicator;

internal sealed class ClaudeAccountPanel : UserControl
{
    private readonly ClaudeAccountController _controller;
    private readonly TableLayoutPanel _layout = AccountUiTheme.Stack(4);
    private readonly ClaudeUsageSummaryPanel _summary = new() { Dock = DockStyle.Fill, Margin = new Padding(0) };
    private readonly Panel _list = new() { Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0), BackColor = Color.Transparent };
    private readonly AccountSheetLayout _sheet = new() { Dock = DockStyle.Top, Margin = new Padding(0), ColumnCount = 1, RowCount = 3 };
    private readonly TableLayoutPanel _headings = new() { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = Color.Transparent };
    private readonly TableLayoutPanel _rows = AccountUiTheme.Stack(0);
    private readonly FlowLayoutPanel _actions = new() { Dock = DockStyle.Fill, Margin = new Padding(0), WrapContents = false, BackColor = Color.Transparent };
    private readonly Dictionary<string, ProfileRow> _profileRows = new(StringComparer.Ordinal);
    private readonly Label _empty = AccountUiTheme.Label(UiText.T("지금 사용하는 Claude 계정을 먼저 연결하세요.\n추가 계정도 Claude의 공식 로그인으로 등록합니다."), 12, color: AccountUiTheme.Muted);
    private readonly Label _count = AccountUiTheme.Label("", 14, true);
    private readonly Label _status = AccountUiTheme.Label("", 9, color: AccountUiTheme.Muted);
    private readonly Button _register = AccountUiTheme.Button("ClaudeRegisterDefaultButton", UiText.T("기존 계정 연결"));
    private readonly Button _add = AccountUiTheme.Button("ClaudeAddAccountButton", UiText.T("+ 계정 추가"));
    private readonly Button _cancel = AccountUiTheme.Button("ClaudeCancelButton", UiText.T("취소"));
    private CancellationTokenSource? _cancellation;
    internal bool IsBusy => _cancellation is not null;
    internal event EventHandler? BusyChanged;

    internal ClaudeAccountPanel(ClaudeAccountController controller)
    {
        _controller = controller; Name = "ClaudeAccountPanel"; BackColor = Color.Transparent;
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, ClaudeUsageSummaryPanel.PreferredLogicalHeight));
        _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 48));
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 64));
        _layout.Controls.Add(_summary, 0, 0);
        _count.Name = "ClaudeAccountCountLabel"; _count.Dock = DockStyle.Fill; _count.AutoSize = false; _count.TextAlign = ContentAlignment.MiddleLeft;
        _layout.Controls.Add(_count, 0, 1);
        _sheet.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _sheet.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); _sheet.RowStyles.Add(new RowStyle(SizeType.Absolute, 0)); _sheet.RowStyles.Add(new RowStyle(SizeType.Absolute, 52));
        SetColumns(_headings);
        foreach (var (text, column) in new[] { (UiText.T("계정"), 0), (UiText.T("주간 잔여"), 1), (UiText.T("초기화"), 2) })
        {
            var label = AccountUiTheme.Label(text, 9, color: AccountUiTheme.Subtle);
            label.Dock = DockStyle.Fill; label.AutoSize = false; label.TextAlign = ContentAlignment.MiddleLeft;
            _headings.Controls.Add(label, column, 0);
        }
        _sheet.Controls.Add(_headings, 0, 0); _sheet.Controls.Add(_rows, 0, 1); _sheet.Controls.Add(_actions, 0, 2); _list.Controls.Add(_sheet);
        _empty.Name = "ClaudeEmptyState"; _empty.Dock = DockStyle.Fill; _empty.AutoSize = false;
        _layout.Controls.Add(_list, 0, 2);
        _actions.Controls.AddRange(new Control[] { _add, _register, _cancel }); _cancel.Visible = false;
        foreach (var button in new[] { _add, _register, _cancel }) button.AutoSize = false;
        _status.Name = "ClaudeStatusLabel"; _status.Dock = DockStyle.Fill; _status.AutoSize = false;
        _status.Text = UiText.T("선택한 계정으로 Claude를 새로 엽니다. 기존 대화와 스킬은 그대로 이어집니다.");
        _layout.Controls.Add(_status, 0, 3); Controls.Add(_layout);
        _summary.RefreshRequested += async (_, _) => await ReadUsageAsync(_controller.SelectedProfile.Id);
        _register.Click += async (_, _) => await RunAsync(UiText.T("기존 Claude 로그인을 확인하고 있습니다…"), async token =>
        { await _controller.RegisterDefaultAsync(token); return UiText.T("기존 Claude 계정을 연결했습니다."); });
        _add.Click += async (_, _) =>
        {
            using var dialog = new AccountNameDialog("", adding: true);
            if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
            var label = string.IsNullOrWhiteSpace(dialog.AccountName) ? UiText.F($"Claude 계정 {_controller.Store.ListProfiles().Count + 1}") : dialog.AccountName;
            await RunAsync(UiText.T("공식 Claude 로그인 창에서 추가할 계정으로 로그인하세요."), async token =>
            { await _controller.AddAsync(label, token); return UiText.T("Claude 계정을 추가했습니다. 사용할 계정을 선택하세요."); });
        };
        _cancel.Click += (_, _) => { _cancellation?.Cancel(); _cancel.Enabled = false; };
        _controller.UsageChanged += OnUsageChanged;
        Reload();
    }

    private void OnUsageChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try
        {
            BeginInvoke((Action)(() =>
            {
                if (IsDisposed || Disposing || !Visible) return;
                // Publish values, including invalidation, without replacing any row,
                // focused button or open menu. Busy state only disables actions.
                foreach (var (id, row) in _profileRows)
                    if (e is not ClaudeUsageObservedEventArgs observed || observed.ProfileId == id)
                        row.DisplayUsage(_controller.LastUsage(id));
                DisplaySummary();
            }));
        }
        catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated) { }
    }

    internal void Reload()
    {
        using var layout = AccountUiTheme.DeferLayout(_layout);
        var profiles = _controller.Store.ListProfiles(); var selectedId = _controller.SelectedProfile.Id;
        _count.Text = UiText.F($"내 계정  {profiles.Count}");
        _register.Visible = !profiles.Any(p => p.IsDefault);
        _register.Enabled = _add.Enabled = !IsBusy;
        _cancel.Visible = IsBusy; _cancel.Enabled = IsBusy;
        var ids = profiles.Select(profile => profile.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var removed in _profileRows.Keys.Where(id => !ids.Contains(id)).ToArray())
        { _profileRows[removed].Dispose(); _profileRows.Remove(removed); }
        _rows.RowCount = Math.Max(1, profiles.Count); _rows.RowStyles.Clear();
        if (profiles.Count == 0)
        {
            _rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 104));
            if (_empty.Parent != _rows) _rows.Controls.Add(_empty, 0, 0);
        }
        else
        {
            _rows.Controls.Remove(_empty);
            for (var index = 0; index < profiles.Count; index++)
            {
                var profile = profiles[index];
                if (!_profileRows.TryGetValue(profile.Id, out var row))
                {
                    row = CreateRow(profile); _profileRows.Add(profile.Id, row); _rows.Controls.Add(row, 0, index);
                }
                row.TabIndex = index; _rows.SetCellPosition(row, new TableLayoutPanelCellPosition(0, index));
                _rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 76));
                row.DisplayProfile(profile, profile.Id == selectedId, IsBusy);
                row.DisplayUsage(_controller.LastUsage(profile.Id));
            }
        }
        DisplaySummary(); ApplyGeometry();
    }

    private void DisplaySummary()
    {
        var selectedId = _controller.SelectedProfile.Id;
        var selected = _profileRows.GetValueOrDefault(selectedId)?.Profile;
        _summary.Display(selected, selected is null ? null : _controller.LastUsage(selected.Id), IsBusy);
    }

    private Task ReadUsageAsync(string profileId) => RunAsync(UiText.T("Claude 사용량을 확인하고 있습니다…"), async token =>
    {
        var result = await _controller.ReadUsageAsync(profileId, token);
        return result.IsStale ? UiText.F($"이전 확인값 · 다음 확인 가능 {result.RetryAfter?.ToLocalTime():HH:mm}") : UiText.T("Claude 사용량을 확인했습니다.");
    });

    private static void SetColumns(TableLayoutPanel layout)
    {
        layout.ColumnCount = 5; layout.RowCount = 1; layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foreach (var width in new[] { 40, 34, 26 }) layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, width));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 136)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
    }

    private static void ScaleColumns(TableLayoutPanel layout, float scale, int verticalInset)
    {
        int Pixels(int logical) => (int)Math.Round(logical * scale);
        layout.Padding = new Padding(Pixels(24), Pixels(verticalInset), Pixels(12), Pixels(verticalInset));
        layout.ColumnStyles[3].Width = Pixels(136); layout.ColumnStyles[4].Width = Pixels(36);
    }

    private void ApplyGeometry()
    {
        if (_sheet.RowStyles.Count != 3) return;
        using var layout = AccountUiTheme.DeferLayout(_layout);
        var scale = DeviceDpi / 96f;
        int Pixels(int logical) => (int)Math.Round(logical * scale);
        _layout.RowStyles[0].Height = Pixels(ClaudeUsageSummaryPanel.PreferredLogicalHeight); _layout.RowStyles[1].Height = Pixels(48); _layout.RowStyles[3].Height = Pixels(64);
        _status.Margin = new Padding(0, Pixels(12), 0, 0);
        _sheet.Padding = new Padding(Math.Max(1, Pixels(1)), Math.Max(1, Pixels(1)), Math.Max(1, Pixels(1)), Pixels(4));
        var contentHeight = _profileRows.Count == 0 ? Pixels(104) : _profileRows.Count * Pixels(76);
        var headingHeight = _profileRows.Count == 0 ? 0 : Pixels(34);
        foreach (RowStyle row in _rows.RowStyles) row.Height = _profileRows.Count == 0 ? Pixels(104) : Pixels(76);
        _sheet.RowStyles[0].Height = headingHeight; _sheet.RowStyles[1].Height = contentHeight; _sheet.RowStyles[2].Height = Pixels(52);
        _sheet.Height = headingHeight + contentHeight + Pixels(52) + _sheet.Padding.Vertical;
        _headings.Visible = _profileRows.Count > 0; ScaleColumns(_headings, scale, 0);
        _empty.Padding = new Padding(Pixels(22), Pixels(18), Pixels(22), Pixels(12));
        _actions.Padding = new Padding(Pixels(12), Pixels(7), Pixels(12), Pixels(7));
        foreach (var (button, width) in new[] { (_add, 138), (_register, 188), (_cancel, 92) })
        {
            button.Size = button.MinimumSize = new Size(Pixels(width), Pixels(36));
            button.Margin = new Padding(0, 0, Pixels(8), 0); button.Padding = new Padding(Pixels(10), 0, Pixels(10), 0);
        }
        foreach (var row in _profileRows.Values) row.ApplyGeometry(scale);
    }

    protected override void OnLoad(EventArgs e) { base.OnLoad(e); ApplyGeometry(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); ApplyGeometry(); }

    private ProfileRow CreateRow(ClaudeProfile profile)
    {
        var row = new ProfileRow(profile);
        row.UsageButton.Click += async (_, _) => await ReadUsageAsync(row.Profile.Id);
        row.SelectButton.Click += async (_, _) =>
        {
            var current = row.Profile;
            if (!row.IsSelected)
            {
                await RunAsync(UiText.T("Claude 계정을 확인하고 있습니다…"), async token =>
                { await _controller.SelectAsync(current, token); return UiText.T("다음 실행 계정을 바꿨습니다. 이미 열린 작업은 기존 계정을 유지합니다."); });
                return;
            }
            using var folder = new FolderBrowserDialog { Description = UiText.T("Claude로 작업할 폴더를 선택하세요"), UseDescriptionForTitle = true, SelectedPath = _controller.LastWorkingDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments) };
            if (folder.ShowDialog(FindForm()) != DialogResult.OK) return;
            await RunAsync(UiText.T("선택한 계정으로 Claude를 열고 있습니다…"), async token =>
            { await _controller.Runtime.LaunchAsync(current, folder.SelectedPath, token); _controller.LastWorkingDirectory = folder.SelectedPath; return UiText.T("Claude를 새 창에서 열었습니다."); });
        };
        row.MoreButton.Click += (_, _) => ShowMenu(row.Profile, row.MoreButton);
        return row;
    }

    private sealed class ProfileRow : UserControl
    {
        private readonly AccountRowLayout _layout;
        private readonly TableLayoutPanel _identity, _usage, _usageLine, _reset;
        private readonly Label _name, _hint;
        private readonly AccountMetricLabel _remaining = new(14);
        private readonly AccountUsageBar _bar = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 6, Margin = new Padding(0) };
        private readonly Label _resetRelative = AccountUiTheme.Label("", 10.5f, true);
        private readonly Label _resetDate = AccountUiTheme.Label("", 9, color: AccountUiTheme.Muted);
        private readonly Label _usageNote = AccountUiTheme.Label("", 8.5f, color: AccountUiTheme.Muted);
        private readonly ToolTip _details = new() { InitialDelay = 500, ReshowDelay = 100, AutoPopDelay = 10000, ShowAlways = true };
        internal ClaudeProfile Profile { get; private set; }
        internal bool IsSelected { get; private set; }
        internal Button UsageButton { get; }
        internal Button SelectButton { get; }
        internal Button MoreButton { get; }

        internal ProfileRow(ClaudeProfile profile)
        {
            Profile = profile;
            Name = "ClaudeProfileRow-" + profile.Id; Dock = DockStyle.Fill; Margin = new Padding(0); BackColor = Color.Transparent;
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96); Height = 76;
            _layout = new AccountRowLayout { Name = "ClaudeRowLayout-" + profile.Id, Dock = DockStyle.Fill, Margin = new Padding(0) }; SetColumns(_layout);
            _identity = TwoLines("ClaudeIdentity-" + profile.Id);
            _name = AccountUiTheme.Label("", 12, true); _name.Name = "ClaudeProfileName-" + profile.Id;
            _name.AutoSize = false; _name.AutoEllipsis = true; _name.TextAlign = ContentAlignment.BottomLeft; _name.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom;
            _hint = AccountUiTheme.Label("", 9, color: AccountUiTheme.Muted); _hint.Name = "ClaudeIdentityHint-" + profile.Id;
            _hint.AutoSize = false; _hint.AutoEllipsis = true; _hint.TextAlign = ContentAlignment.TopLeft; _hint.Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Top;
            _identity.Controls.Add(_name, 0, 0); _identity.Controls.Add(_hint, 0, 1); _layout.Controls.Add(_identity, 0, 0);
            _usage = TwoLines("ClaudeUsageColumn-" + profile.Id);
            _usageLine = new TableLayoutPanel { Name = "ClaudeUsageLine-" + profile.Id, Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1, Margin = new Padding(0), BackColor = Color.Transparent };
            _usageLine.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _usageLine.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 50)); _usageLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); _usageLine.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
            _remaining.Name = "ClaudeWeeklyRemaining-" + profile.Id; _remaining.Dock = DockStyle.Fill;
            UsageButton = new AccountButton { Name = "ClaudeUsage-" + profile.Id, Text = "↻", Quiet = true, Font = new Font("Segoe UI Symbol", 11), FlatStyle = FlatStyle.Flat, FlatAppearance = { BorderSize = 0 }, Cursor = Cursors.Hand, Anchor = AnchorStyles.Right };
            _usageLine.Controls.Add(_remaining, 0, 0); _usageLine.Controls.Add(_bar, 1, 0); _usageLine.Controls.Add(UsageButton, 2, 0);
            _usageNote.Name = "ClaudeUsageAge-" + profile.Id; _usageNote.AutoSize = false; _usageNote.AutoEllipsis = true;
            _usageNote.Dock = DockStyle.Fill; _usageNote.TextAlign = ContentAlignment.TopLeft;
            _usage.Controls.Add(_usageLine, 0, 0); _usage.Controls.Add(_usageNote, 0, 1); _layout.Controls.Add(_usage, 1, 0);
            _reset = TwoLines("ClaudeResetColumn-" + profile.Id);
            foreach (var label in new[] { _resetRelative, _resetDate }) { label.Dock = DockStyle.Fill; label.AutoSize = false; label.AutoEllipsis = true; }
            _resetRelative.TextAlign = ContentAlignment.BottomLeft; _resetDate.TextAlign = ContentAlignment.TopLeft;
            _reset.Controls.Add(_resetRelative, 0, 0); _reset.Controls.Add(_resetDate, 0, 1); _layout.Controls.Add(_reset, 2, 0);
            SelectButton = AccountUiTheme.Button("ClaudeSelect-" + profile.Id, ""); SelectButton.Anchor = AnchorStyles.Right;
            MoreButton = new AccountButton { Name = "ClaudeMore-" + profile.Id, Text = "···", Quiet = true, Font = AccountFonts.Create(14, true), FlatStyle = FlatStyle.Flat, FlatAppearance = { BorderSize = 0 }, Cursor = Cursors.Hand, Anchor = AnchorStyles.Right };
            foreach (var button in new[] { UsageButton, SelectButton, MoreButton }) { button.AutoSize = false; button.Margin = new Padding(0); }
            _layout.Controls.Add(SelectButton, 3, 0); _layout.Controls.Add(MoreButton, 4, 0); Controls.Add(_layout);
        }

        private static TableLayoutPanel TwoLines(string name)
        {
            var panel = AccountUiTheme.Stack(2); panel.Name = name;
            panel.RowStyles.Add(new RowStyle(SizeType.Percent, 55)); panel.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
            return panel;
        }

        internal void DisplayProfile(ClaudeProfile profile, bool selected, bool busy)
        {
            Profile = profile; IsSelected = selected; _name.Text = profile.Label;
            _hint.Text = profile.Identity?.Hint ?? UiText.T("로그인 필요");
            _layout.Active = selected; _layout.Invalidate();
            ((AccountButton)SelectButton).Primary = selected;
            SelectButton.Text = selected ? UiText.T("Claude 열기…") : UiText.T("선택"); SelectButton.Invalidate();
            foreach (var button in new[] { UsageButton, SelectButton, MoreButton }) button.Enabled = !busy;
            UsageButton.AccessibleName = profile.Label + " · " + UiText.T("이 계정 사용량 조회");
            MoreButton.AccessibleName = UiText.F($"{profile.Label} 계정 관리 메뉴");
        }

        internal void ApplyGeometry(float scale)
        {
            int Pixels(int logical) => (int)Math.Round(logical * scale);
            Height = Pixels(76); ScaleColumns(_layout, scale, 10);
            _identity.Margin = new Padding(0, 0, Pixels(20), 0); _name.Height = Pixels(22); _hint.Height = Pixels(20); _hint.Margin = new Padding(0, Pixels(3), 0, 0);
            _usage.Margin = new Padding(0, 0, Pixels(18), 0); _usageNote.Margin = new Padding(0, Pixels(3), 0, 0);
            _usageLine.ColumnStyles[0].Width = Pixels(50); _usageLine.ColumnStyles[2].Width = Pixels(30);
            _bar.Height = Pixels(6); _bar.MinimumSize = new Size(Pixels(16), Pixels(6)); _bar.Margin = new Padding(0, 0, Pixels(7), 0);
            _reset.Margin = new Padding(0, 0, Pixels(12), 0); _resetDate.Margin = new Padding(0, Pixels(3), 0, 0);
            UsageButton.Size = UsageButton.MinimumSize = new Size(Pixels(26), Pixels(26)); UsageButton.Padding = new Padding(0);
            SelectButton.Size = SelectButton.MinimumSize = new Size(Pixels(124), Pixels(34)); SelectButton.Padding = new Padding(Pixels(7), 0, Pixels(7), 0);
            MoreButton.Size = MoreButton.MinimumSize = new Size(Pixels(32), Pixels(32)); MoreButton.Padding = new Padding(0);
        }

        internal void DisplayUsage(ClaudeUsageResult? usage)
        {
            var now = DateTimeOffset.Now; var window = usage?.Snapshot.Weekly;
            var remaining = ClaudeDisplayMetrics.IsUsable(window, now) ? UsagePercentage.Remaining(window!.UsedPercent) : null;
            _remaining.Text = remaining is { } value ? $"{value}%" : "—";
            _remaining.ForeColor = remaining is null || usage?.IsStale == true ? AccountUiTheme.Muted : AccountUiTheme.Text;
            _bar.Remaining = remaining; _bar.FillColor = IsSelected ? AccountUiTheme.Apricot : AccountUiTheme.Blue;
            _bar.TrackColor = IsSelected ? Color.FromArgb(244, 226, 212) : AccountUiTheme.Raised;
            _usageNote.Text = usage is null ? UiText.T("사용량 미확인") : usage.IsStale
                ? UiText.F($"이전 확인 {usage.LastUpdatedAt.ToLocalTime():MM.dd HH:mm}")
                : UiText.F($"{usage.LastUpdatedAt.ToLocalTime().ToString("MM.dd HH:mm")} 확인");
            _usageNote.ForeColor = usage?.IsStale == true ? AccountUiTheme.Warning : AccountUiTheme.Muted;
            _resetRelative.Text = window?.ResetsAt is { } reset ? CombinedUsagePanel.TimeUntil(reset, now) : UiText.T("초기화 미확인");
            _resetDate.Text = window?.ResetsAt is { } date ? date.ToLocalTime().ToString("MM.dd HH:mm") : "—";
            var details = usage?.IsStale == true && usage.RetryAfter is { } retry
                ? UiText.F($"이전 확인값 · 다음 확인 가능 {retry.ToLocalTime():HH:mm}") : _usageNote.Text;
            _details.SetToolTip(UsageButton, UiText.T("이 계정 사용량 조회") + "\n" + details);
            _details.SetToolTip(_usageNote, details); UsageButton.AccessibleDescription = _usageNote.AccessibleDescription = details;
        }

        protected override void Dispose(bool disposing) { if (disposing) _details.Dispose(); base.Dispose(disposing); }
    }
    private void ShowMenu(ClaudeProfile profile, Control anchor)
    {
        var menu = new ContextMenuStrip { Font = Font, ShowImageMargin = false, BackColor = AccountUiTheme.Surface, ForeColor = AccountUiTheme.Text,
            Renderer = new ToolStripProfessionalRenderer(new AccountMenuColors()) };
        menu.Closed += (_, _) => menu.Dispose();
        menu.Items.Add(UiText.T("이름 변경"), null, (_, _) =>
        {
            using var dialog = new AccountNameDialog(profile.Label);
            if (dialog.ShowDialog(FindForm()) == DialogResult.OK) { _controller.Store.Rename(profile.Id, dialog.AccountName); Reload(); }
        });
        menu.Items.Add(UiText.T("다시 로그인"), null, async (_, _) => await RunAsync(UiText.T("공식 Claude 로그인 창을 열고 있습니다…"), async token =>
        { await _controller.LoginAsync(profile, token); return UiText.T("Claude 로그인을 확인했습니다."); }));
        menu.Items.Add(UiText.T("로그아웃"), null, async (_, _) =>
        {
            if (MessageBox.Show(FindForm(), UiText.T("이 프로필에서 로그아웃할까요? 같은 프로필의 작업을 먼저 마쳐주세요."), UiText.T("Claude 로그아웃"), MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            await RunAsync(UiText.T("Claude에서 로그아웃하고 있습니다…"), async token => { await _controller.LogoutAsync(profile, token); return UiText.T("Claude에서 로그아웃했습니다."); });
        });
        var remove = menu.Items.Add(UiText.T("목록에서 제거"), null, (_, _) =>
        {
            if (MessageBox.Show(FindForm(), UiText.T("계정 목록에서 제거할까요? Claude의 로그인과 대화 파일은 유지됩니다."), UiText.T("목록에서 제거"), MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            try { _controller.Store.Remove(profile.Id); Reload(); } catch (Exception ex) { SetStatus(ex.Message, true); }
        });
        remove.Enabled = profile.Id != _controller.SelectedProfile.Id;
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    private async Task RunAsync(string message, Func<CancellationToken, Task<string>> action)
    {
        if (IsBusy) return;
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        SetStatus(message); Reload(); BusyChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            using var operationLease = AccountOperationLease.Acquire();
            SetStatus(await action(cancellation.Token));
        }
        catch (OperationCanceledException) { SetStatus(UiText.T("취소했습니다. 선택한 계정은 유지됩니다.")); }
        catch (ClaudeUsageTemporarilyUnavailableException ex) { SetStatus(UiText.F($"다음 확인 가능 {ex.RetryAfter.ToLocalTime():HH:mm} · Claude 사용량은 10분 간격으로 확인합니다.")); }
        catch (Exception ex) { SetStatus(ex.Message, true); }
        finally { _cancellation = null; Reload(); BusyChanged?.Invoke(this, EventArgs.Empty); }
    }

    private void SetStatus(string text, bool error = false)
    { _status.Text = UiText.Message(text); _status.ForeColor = error ? AccountUiTheme.Error : AccountUiTheme.Muted; }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _controller.UsageChanged -= OnUsageChanged;
            if (_empty.Parent is null) _empty.Dispose();
        }
        base.Dispose(disposing);
    }
}
