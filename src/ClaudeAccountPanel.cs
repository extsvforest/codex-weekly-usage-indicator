namespace WeeklyUsageIndicator;

internal sealed class ClaudeAccountPanel : UserControl
{
    private readonly ClaudeAccountController _controller;
    private readonly ClaudeShelfService? _shelf;
    private readonly TableLayoutPanel _layout = AccountUiTheme.Stack(5);
    private readonly ClaudeUsageSummaryPanel _summary = new() { Dock = DockStyle.Fill, Margin = new Padding(0) };
    private readonly TableLayoutPanel _notice = new() { Name = "ClaudeNotice", Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0, 0, 0, 12), Padding = new Padding(12, 6, 8, 6), BackColor = AccountUiTheme.Raised };
    private readonly FlowLayoutPanel _noticeActions = new() { Dock = DockStyle.Fill, AutoSize = true, WrapContents = false, Margin = new Padding(8, 0, 0, 0), FlowDirection = FlowDirection.RightToLeft };
    private readonly Panel _list = new() { Name = "ClaudeAccountList", Dock = DockStyle.Fill, AutoScroll = true, Margin = new Padding(0), BackColor = Color.Transparent };
    private readonly AccountSheetLayout _sheet = new() { Dock = DockStyle.Top, Margin = new Padding(0), ColumnCount = 1, RowCount = 3 };
    private readonly TableLayoutPanel _headings = new() { Dock = DockStyle.Fill, Margin = new Padding(0), BackColor = Color.Transparent };
    private readonly TableLayoutPanel _rows = AccountUiTheme.Stack(0);
    private readonly FlowLayoutPanel _actions = new() { Dock = DockStyle.Fill, Margin = new Padding(0), WrapContents = false, BackColor = Color.Transparent };
    private readonly Dictionary<string, ProfileRow> _profileRows = new(StringComparer.Ordinal);
    private ContextMenuStrip? _profileMenu;
    private readonly Label _empty = AccountUiTheme.Label(UiText.T("지금 사용하는 Claude 계정을 먼저 연결하세요.\n추가 계정도 Claude의 공식 로그인으로 등록합니다."), 12, color: AccountUiTheme.Muted);
    private readonly Label _count = AccountUiTheme.Label("", 14, true);
    private readonly Label _status = AccountUiTheme.Label("", color: AccountUiTheme.Muted);
    private readonly ToolTip _actionDetails = new() { InitialDelay = 500, ReshowDelay = 100, AutoPopDelay = 10000 };
    private readonly Button _register = AccountUiTheme.Button("ClaudeRegisterDefaultButton", UiText.T("기존 계정 연결"));
    private readonly Button _add = AccountUiTheme.Button("ClaudeAddAccountButton", UiText.T("+ 계정 추가"));
    private readonly Button _cancel = AccountUiTheme.Button("ClaudeCancelButton", UiText.T("취소"));
    private readonly Button _repair = AccountUiTheme.Button("ClaudeRecoverAccountButton", UiText.T("다시 로그인"));
    private readonly Label _shelfStatus = AccountUiTheme.Label("", 9, color: AccountUiTheme.Muted);
    private readonly Button _shelfAction = AccountUiTheme.Button("ClaudeShelfRelaunchButton", UiText.T("종료하면 다시 열기"));
    private CancellationTokenSource? _cancellation;
    private string? _operationMessage;
    private bool _operationError, _operationSuccess;
    private string? _usageFailureProfileId, _usageFailureMessage;
    private DateTimeOffset? _usageFailureObservedAt;
    private (int Done, int Total)? _batchProgress;
    internal bool IsBusy => _cancellation is not null;
    internal bool CanRefreshSelected => !IsBusy && RegisteredSelection is not null;
    internal bool CanRefreshAll => !IsBusy && _controller.Store.ListProfiles().Any(profile => profile.IsVerified);
    /// <summary>A refresh of every account is running; it only reads usage and can be cancelled at any point.</summary>
    internal bool IsRefreshingAll => _batchProgress is not null;
    internal void CancelOperation() { _cancellation?.Cancel(); _cancel.Enabled = false; }
    internal string CheckedAtText => _summary.CheckedAtText;
    internal event EventHandler? BusyChanged;
    internal event EventHandler? SummaryChanged;
    private ClaudeProfile? RegisteredSelection => _profileRows.GetValueOrDefault(_controller.SelectedProfile.Id)?.Profile;

    internal ClaudeAccountPanel(ClaudeAccountController controller, ClaudeShelfService? shelf = null)
    {
        _controller = controller; _shelf = shelf; Name = "ClaudeAccountPanel"; BackColor = Color.Transparent;
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        foreach (var height in new[] { ClaudeUsageSummaryPanel.PreferredLogicalHeight, 0, 48 }) _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100)); _layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        _layout.Controls.Add(_summary, 0, 0);
        _notice.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); _notice.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        _status.Name = "ClaudeStatusLabel"; _status.Dock = DockStyle.Fill; _status.AutoSize = false; _status.TextAlign = ContentAlignment.MiddleLeft;
        _noticeActions.Controls.AddRange(new Control[] { _cancel, _repair });
        _notice.Controls.Add(_status, 0, 0); _notice.Controls.Add(_noticeActions, 1, 0); _layout.Controls.Add(_notice, 0, 1);
        _count.Name = "ClaudeAccountCountLabel"; _count.Anchor = AnchorStyles.Left; _layout.Controls.Add(_count, 0, 2);
        _sheet.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _sheet.RowStyles.Add(new RowStyle(SizeType.Absolute, 34)); _sheet.RowStyles.Add(new RowStyle(SizeType.Absolute, 0)); _sheet.RowStyles.Add(new RowStyle(SizeType.Absolute, 46));
        SetColumns(_headings);
        foreach (var (text, column) in new[] { (UiText.T("계정"), 0), (UiText.T("주간 잔여"), 1), (UiText.T("초기화"), 2) })
        {
            var label = AccountUiTheme.Label(text, 9, color: AccountUiTheme.Subtle);
            label.Dock = DockStyle.Fill; label.AutoSize = false; label.TextAlign = ContentAlignment.MiddleLeft;
            _headings.Controls.Add(label, column, 0);
        }
        _sheet.Controls.Add(_headings, 0, 0); _sheet.Controls.Add(_rows, 0, 1); _sheet.Controls.Add(_actions, 0, 2); _list.Controls.Add(_sheet);
        _empty.Name = "ClaudeEmptyState"; _empty.Dock = DockStyle.Fill; _empty.AutoSize = false;
        _layout.Controls.Add(_list, 0, 3);
        foreach (var button in new[] { _add, _register }) { button.AutoSize = false; ((AccountButton)button).Quiet = true; }
        _actions.Controls.AddRange(new Control[] { _add, _register });
        var footerRow = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0), BackColor = Color.Transparent };
        footerRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); footerRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        footerRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _shelfStatus.Name = "ClaudeShelfStatusLabel"; _shelfStatus.Dock = DockStyle.Fill; _shelfStatus.AutoSize = false;
        _shelfStatus.AutoEllipsis = true; _shelfStatus.TextAlign = ContentAlignment.MiddleLeft;
        _shelfAction.AccessibleDescription = UiText.T("Claude를 종료하면 세션 목록을 채운 뒤 다시 엽니다. 30분 안에 한 번 적용됩니다.");
        _actionDetails.SetToolTip(_shelfAction, _shelfAction.AccessibleDescription);
        _shelfAction.Font = AccountFonts.Create(9); _shelfAction.MinimumSize = new Size(0, 26); _shelfAction.Height = 26; _shelfAction.Padding = new Padding(8, 2, 8, 2);
        footerRow.Controls.Add(_shelfStatus, 0, 0); footerRow.Controls.Add(_shelfAction, 1, 0); _layout.Controls.Add(footerRow, 0, 4); Controls.Add(_layout);
        _summary.SummaryChanged += (_, _) => SummaryChanged?.Invoke(this, EventArgs.Empty);
        _register.Click += async (_, _) => await ConnectCurrentLoginAsync();
        _add.Click += async (_, _) =>
        {
            using var dialog = new AccountNameDialog("", adding: true);
            if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
            var label = string.IsNullOrWhiteSpace(dialog.AccountName) ? UiText.F($"Claude 계정 {_controller.Store.ListProfiles().Count + 1}") : dialog.AccountName;
            await RunAsync(UiText.T("공식 Claude 로그인 창에서 추가할 계정으로 로그인하세요."), async token =>
            { await _controller.AddAsync(label, token); return UiText.T("Claude 계정을 추가했습니다. 사용할 계정을 선택하세요."); });
        };
        _cancel.Click += (_, _) => { _cancellation?.Cancel(); _cancel.Enabled = false; };
        _repair.Click += async (_, _) =>
        {
            if (RegisteredSelection is not { } selected) return;
            await SignInAgainAsync(selected);
        };
        _shelfAction.Click += (_, _) => _shelf?.ArmRelaunch(!_shelf.Status.RelaunchArmed);
        _controller.UsageChanged += OnUsageChanged;
        _controller.SelectionChanged += OnSelectionChanged;
        if (_shelf is not null) _shelf.StatusChanged += OnShelfChanged;
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
                // Update observations in place, preserving focused controls and open menus.
                using var layout = AccountUiTheme.DeferLayout(_layout);
                foreach (var (id, row) in _profileRows)
                    if (e is not ClaudeUsageObservedEventArgs observed || observed.ProfileId == id)
                        row.DisplayUsage(_controller.LastUsage(id), _controller.LastFailure(id));
                ClearResolvedUsageFailure(); DisplaySummary(); RenderNotice(); UpdateActions();
            }));
        }
        catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated) { }
    }

    private void OnShelfChanged(object? sender, EventArgs e) { if (!IsDisposed && !Disposing && !IsBusy) Reload(); }
    private void OnSelectionChanged(object? sender, EventArgs e)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke((Action)(() => { if (!IsDisposed && !Disposing && !IsBusy) Reload(); })); }
        catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated) { }
    }

    // "In use" means the account the Desktop app confirmed in its current run. Without that observation the
    // widget's selection keeps the row highlight but no badge.
    private string? AppProfileId => _shelf?.Status is { AppConfirmed: true } status ? status.AppProfileId : null;

    internal void Reload()
    {
        using var layout = AccountUiTheme.DeferLayout(_layout);
        var profiles = _controller.Store.ListProfiles(); var selectedId = _controller.SelectedProfile.Id;
        _count.Text = UiText.F($"내 계정  {profiles.Count}");
        // Once the default-home account has moved to its own profile, connecting that shared home again is not offered.
        _register.Visible = !profiles.Any(p => p.IsDefault) && !_controller.Store.DefaultHomeRetired;
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
                { row = CreateRow(profile); _profileRows.Add(profile.Id, row); _rows.Controls.Add(row, 0, index); }
                row.TabIndex = index; _rows.SetCellPosition(row, new TableLayoutPanelCellPosition(0, index));
                _rows.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
                row.DisplayProfile(profile, AppProfileId is { } app ? profile.Id == app : profile.Id == selectedId,
                    profile.Id == AppProfileId, IsBusy);
                row.DisplayUsage(_controller.LastUsage(profile.Id), _controller.LastFailure(profile.Id));
            }
        }
        ClearResolvedUsageFailure(); DisplaySummary(); RenderNotice(); UpdateActions(); UpdateShelfFooter(); ApplyGeometry();
    }


    private void UpdateShelfFooter()
    {
        var status = _shelf?.Status;
        var label = status?.AppProfileId is { } id ? _profileRows.GetValueOrDefault(id)?.Profile.Label : null;
        var (text, warning, action) = ShelfFooter(status, label, DateTimeOffset.Now);
        _shelfStatus.Text = text; _shelfStatus.ForeColor = warning ? AccountUiTheme.Warning : AccountUiTheme.Muted;
        _actionDetails.SetToolTip(_shelfStatus, text);
        _shelfAction.Visible = action is not null;
        _shelfAction.Text = action ?? "";
        _shelfAction.Enabled = !IsBusy;
    }

    internal static (string Text, bool Warning, string? Action) ShelfFooter(ClaudeShelfStatus? status, string? appLabel,
        DateTimeOffset now)
    {
        if (status is null) return ("", false, null);
        // A paused fill comes first: a reopen still happens, but nothing is filled before it.
        if (status.Problem == ClaudeShelfProblem.Incompatible && status.MappedAccounts >= 2)
            return (UiText.T("세션 목록 채우기 멈춤 · 이 Claude 버전은 아직 확인되지 않았습니다."), true,
                status.RelaunchArmed ? UiText.T("다시 열기 취소") : null);
        // A record that does not parse may count for nothing with the app, so a delete in another account could take
        // its conversation: that warning stays until the record reads again.
        if (status.Damaged is { } damaged && status.MappedAccounts >= 2)
            return (UiText.F($"{damaged} 목록에 읽을 수 없는 세션 기록이 있습니다. 고쳐질 때까지 세션은 삭제 대신 보관해 주세요."), true,
                status.RelaunchArmed ? UiText.T("다시 열기 취소") : null);
        // Filling goes on with a version not reviewed yet, but deleting a session there may not spare the conversation
        // other accounts still list: that warning stays until a reviewed version is installed.
        if (status.Unreviewed is not null && status.MappedAccounts >= 2)
            return (UiText.T("확인 전 Claude 버전 · 세션은 삭제 대신 보관해 주세요."), true,
                status.RelaunchArmed ? UiText.T("다시 열기 취소")
                : status.AppConfirmed && status.PendingForApp is > 0 ? UiText.T("종료하면 다시 열기") : null);
        if (status.RelaunchArmed)
            return (status.AppConfirmed && status.PendingForApp is > 0 and var waiting
                ? UiText.F($"채울 세션 {waiting}개 · Claude를 종료하면 채운 뒤 다시 엽니다.")
                : UiText.T("다시 열기 예약됨 · Claude를 종료하면 세션 목록을 채운 뒤 다시 엽니다."), false, UiText.T("다시 열기 취소"));
        // Scheduled tasks are aligned across every account on this PC, mapped or not.
        if (status.MappedAccounts < 2)
            return status.Scheduled is { } alone ? (ScheduledLine(alone), true, null)
                : (UiText.T("Claude 계정을 두 개 이상 연결하면 세션 목록을 서로 채웁니다."), false, null);
        if (status.AppConfirmed && status.PendingForApp is > 0 and var pending)
            return (UiText.F($"{appLabel ?? ""} 목록에 채울 세션 {pending}개 · Claude를 종료하면 채웁니다."), false,
                UiText.T("종료하면 다시 열기"));
        if (status.Problem == ClaudeShelfProblem.Failed)
            return (UiText.F($"세션 목록 채우기 실패 · {UiText.Message(status.ProblemDetail ?? "")}"), true, null);
        // Scheduled tasks are aligned beside the session lists; a problem that lasts, or what aligning left for the user
        // to settle, takes the same line.
        if (status.Scheduled is { } scheduled) return (ScheduledLine(scheduled), true, null);
        if (status.Problem == ClaudeShelfProblem.Held || !status.LastFillComplete)
            return (UiText.T("세션 목록 일부 보류 · 사용 중이거나 복구 중인 세션은 나중에 채웁니다."), false, null);
        if (status.LastFillAt is { } at)
            return (UiText.F($"세션 목록 · 모든 계정 채움 · {AccountUsageText.DateAndTime(at, now)}"), false, null);
        return (UiText.T("세션 목록 · 다른 계정은 켠 채로, 지금 계정은 Claude를 종료하면 채웁니다."), false, null);
    }

    private static string ScheduledLine(ClaudeScheduledProblem scheduled) => scheduled.Kind switch
    {
        ClaudeScheduledProblemKind.Unreviewed => UiText.T("예약 작업 맞추기 멈춤 · 이 Claude 버전의 예약 작업 저장 방식은 아직 확인되지 않았습니다."),
        ClaudeScheduledProblemKind.Unreadable => UiText.F($"예약 작업 맞추기 보류 · {scheduled.Detail ?? ""} 예약 목록을 읽을 수 없습니다."),
        ClaudeScheduledProblemKind.TooLarge => UiText.F($"예약 작업 맞추기 보류 · {scheduled.Detail ?? ""} 예약 목록이 Claude가 읽을 수 있는 크기를 넘게 됩니다."),
        ClaudeScheduledProblemKind.Conflict => UiText.F($"예약 작업 {scheduled.Detail ?? ""}은 여러 계정에서 따로 바뀌어 맞추지 않았습니다. 한 계정에서 다시 고치면 그 내용으로 맞춥니다."),
        ClaudeScheduledProblemKind.RemovedHere => UiText.F($"예약 작업 {scheduled.Detail ?? ""}은 한 계정에서만 지워져 다른 계정에는 남아 있습니다."),
        ClaudeScheduledProblemKind.Held => UiText.F($"예약 작업 {scheduled.Detail ?? ""}은 권한이 넓어 다른 계정에 자동으로 만들지 않았습니다."),
        _ => UiText.F($"예약 작업 맞추기 실패 · {scheduled.Detail switch
        {
            "store_unreadable" => UiText.T("위젯의 예약 작업 기록을 읽을 수 없습니다."),
            "store_unwritable" => UiText.T("위젯의 예약 작업 기록을 저장할 수 없습니다."),
            var detail => UiText.Message(detail ?? "")
        }}")
    };

    private void DisplaySummary() => _summary.Display(_controller.Store.ListProfiles().Select(profile =>
        new ClaudeSummaryRow(profile, _controller.LastUsage(profile.Id), _controller.LastFailure(profile.Id))).ToArray(), _batchProgress);

    internal Task RefreshSelectedUsageAsync() => RegisteredSelection is { } selected ? ReadUsageAsync(selected.Id) : Task.CompletedTask;

    /// <summary>
    /// Checks every signed-in account once, up to three at a time and the in-use one first, as the Codex tab does when
    /// it opens. Each account keeps its own ten-minute interval, so one checked recently reuses its observation instead
    /// of a new command. A failure stays on its row and the remaining accounts continue. Accounts that need a sign-in
    /// are skipped.
    /// </summary>
    internal Task RefreshAllUsageAsync() => RunAsync(UiText.T("전체 계정의 주간 사용량을 확인하고 있습니다…"), async token =>
    {
        var targets = _controller.ReadableProfiles();
        if (targets.Count == 0) throw new InvalidOperationException("확인할 수 있는 Claude 계정이 없습니다. 로그인이 필요한 계정은 다시 로그인하세요.");
        var failed = 0;
        _batchProgress = (0, targets.Count); DisplaySummary();
        try
        {
            // Each account's row updates through UsageChanged as soon as it ends; this only counts them.
            await _controller.ReadUsageManyAsync(targets.Select(profile => profile.Id), token, outcome =>
            {
                if (outcome.Failure is not null) failed++;
                if (_batchProgress is { } progress) { _batchProgress = (progress.Done + 1, progress.Total); DisplaySummary(); }
            });
        }
        finally { _batchProgress = null; }
        if (failed > 0) throw new InvalidOperationException("일부 계정의 확인이 필요합니다. 계정별 상태를 확인하고 다시 갱신하세요.");
        return UiText.F($"{targets.Count}개 계정의 주간 잔여량을 확인했습니다.");
    });
    private Task ReadUsageAsync(string profileId) => RunAsync(UiText.T("Claude 사용량을 확인하고 있습니다…"), async token =>
    {
        var result = await _controller.ReadUsageAsync(profileId, token);
        return result.IsStale ? UiText.F($"이전 확인값 · 다음 확인 가능 {result.RetryAfter?.ToLocalTime():HH:mm}") : UiText.T("Claude 사용량을 확인했습니다.");
    }, usageProfileId: profileId);

    private Task ConnectCurrentLoginAsync() => RunAsync(UiText.T("기존 Claude 로그인을 확인하고 있습니다…"), async token =>
    {
        await _controller.RegisterDefaultAsync(token);
        return UiText.T("기존 Claude 계정을 연결했습니다.");
    });

    private Task SignInAgainAsync(ClaudeProfile profile) => RunAsync(UiText.T("공식 Claude 로그인 창을 열고 있습니다…"), async token =>
    {
        await _controller.LoginAsync(profile, token);
        await _controller.ReadUsageAsync(profile.Id, token);
        return UiText.T("Claude 로그인을 확인했습니다.");
    });

    private void UpdateActions()
    {
        _register.Enabled = _add.Enabled = !IsBusy;
        _cancel.Visible = IsBusy; _cancel.Enabled = IsBusy;
        var selected = RegisteredSelection;
        var failure = selected is null ? null : _controller.LastFailure(selected.Id);
        _repair.Visible = !IsBusy && selected is not null && failure is ClaudeLoginRequiredException or ClaudeIdentityChangedException;
        _repair.Text = UiText.T("다시 로그인");
        _repair.Enabled = !IsBusy;
        _shelfAction.Enabled = !IsBusy;
    }

    private void RenderNotice()
    {
        var selected = RegisteredSelection;
        var failure = selected is null ? null : _controller.LastFailure(selected.Id);
        var message = IsBusy ? _operationMessage : failure is not null ? UsageIndicatorForm.FriendlyClaudeError(failure)
            : _operationSuccess ? null : _operationMessage;
        _status.Text = message ?? "";
        _status.ForeColor = !IsBusy && (failure is not null || _operationError) ? AccountUiTheme.Error : AccountUiTheme.Muted;
        _notice.Visible = !string.IsNullOrEmpty(message) || IsBusy;
        _layout.RowStyles[1].Height = _notice.Visible ? (int)Math.Round(72 * DeviceDpi / 96f) : 0;
    }

    // Match AccountTable: shared visual columns, spacing, row and action dimensions.
    private static void SetColumns(TableLayoutPanel layout)
    {
        layout.ColumnCount = 5; layout.RowCount = 1; layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        foreach (var width in new[] { 40, 32, 28 }) layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, width));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 36));
    }

    private static void ScaleColumns(TableLayoutPanel layout, float scale, int verticalInset)
    {
        layout.Padding = new Padding((int)(24 * scale), (int)(verticalInset * scale), (int)(12 * scale), (int)(verticalInset * scale));
        layout.ColumnStyles[3].Width = 94 * scale; layout.ColumnStyles[4].Width = 36 * scale;
    }

    private void ApplyGeometry()
    {
        using var layout = AccountUiTheme.DeferLayout(_layout);
        var scale = DeviceDpi / 96f;
        int Pixels(int logical) => (int)Math.Round(logical * scale);
        _layout.RowStyles[0].Height = Pixels(172); _layout.RowStyles[2].Height = Pixels(48); _layout.RowStyles[4].Height = Pixels(30);
        _notice.Margin = new Padding(0, 0, 0, Pixels(12)); _notice.Padding = new Padding(Pixels(12), Pixels(6), Pixels(8), Pixels(6));
        _noticeActions.Margin = new Padding(Pixels(8), 0, 0, 0);
        _sheet.Padding = new Padding((int)Math.Ceiling(scale), (int)Math.Ceiling(scale), (int)Math.Ceiling(scale), (int)Math.Ceiling(4 * scale));
        var contentHeight = _profileRows.Count == 0 ? Pixels(104) : _profileRows.Count * Pixels(66);
        var headingHeight = _profileRows.Count == 0 ? 0 : Pixels(34);
        foreach (RowStyle row in _rows.RowStyles) row.Height = _profileRows.Count == 0 ? Pixels(104) : Pixels(66);
        _sheet.RowStyles[0].Height = headingHeight; _sheet.RowStyles[1].Height = contentHeight; _sheet.RowStyles[2].Height = Pixels(46);
        _sheet.Height = headingHeight + contentHeight + Pixels(46) + _sheet.Padding.Vertical;
        _headings.Visible = _profileRows.Count > 0; ScaleColumns(_headings, scale, 0);
        _empty.Padding = new Padding(Pixels(24), Pixels(18), Pixels(24), Pixels(12));
        _actions.Padding = new Padding((int)(12 * scale), (int)(6 * scale), (int)(12 * scale), (int)(6 * scale));
        foreach (var (button, width) in new[] { (_add, 150), (_register, 188) })
        {
            button.Size = button.MinimumSize = new Size((int)(width * scale), (int)(34 * scale));
            button.Margin = new Padding(0); button.Padding = new Padding(0);
        }
        foreach (var button in new[] { _cancel, _repair })
        { button.MinimumSize = new Size(Pixels(92), Pixels(38)); button.Padding = new Padding(Pixels(12), Pixels(4), Pixels(12), Pixels(4)); }
        _shelfAction.MinimumSize = new Size(0, Pixels(26)); _shelfAction.Height = Pixels(26); _shelfAction.Padding = new Padding(Pixels(8), Pixels(2), Pixels(8), Pixels(2));
        foreach (var row in _profileRows.Values) row.ApplyGeometry(scale);
        RenderNotice();
    }

    protected override void OnLoad(EventArgs e) { base.OnLoad(e); ApplyGeometry(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); ApplyGeometry(); }

    private ProfileRow CreateRow(ClaudeProfile profile)
    {
        var row = new ProfileRow(profile);
        // The row action only repairs the usage login. The Desktop account changes inside the official app.
        row.SelectButton.Click += async (_, _) => { if (row.RequiresLogin) await SignInAgainAsync(row.Profile); };
        row.MoreButton.Click += (_, _) => ShowMenu(row.Profile, row.MoreButton);
        return row;
    }

    private sealed class ProfileRow : UserControl
    {
        private readonly AccountRowLayout _layout;
        private readonly TableLayoutPanel _identity, _usage, _reset;
        private readonly Label _name = AccountUiTheme.Label("", 12, true);
        private readonly Label _state = AccountUiTheme.Label("", 8.5f, color: AccountUiTheme.Muted);
        private readonly AccountMetricLabel _remaining = new(14);
        private readonly AccountUsageBar _bar = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, Height = 6 };
        private readonly Label _resetRelative = AccountUiTheme.Label("", 10.5f, true);
        private readonly Label _resetDate = AccountUiTheme.Label("", 9, color: AccountUiTheme.Muted);
        private readonly ToolTip _details = new() { InitialDelay = 500, ReshowDelay = 100, AutoPopDelay = 10000, ShowAlways = true };
        private bool _busy;
        internal ClaudeProfile Profile { get; private set; }
        internal bool IsSelected { get; private set; }
        internal bool IsAppAccount { get; private set; }
        internal bool RequiresLogin { get; private set; }
        internal Button SelectButton { get; }
        internal Button MoreButton { get; }

        internal ProfileRow(ClaudeProfile profile)
        {
            Profile = profile;
            Name = "ClaudeProfileRow-" + profile.Id; Dock = DockStyle.Fill; Margin = new Padding(0); BackColor = Color.Transparent;
            AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96); Height = 66;
            _layout = new AccountRowLayout { Name = "ClaudeRowLayout-" + profile.Id, Dock = DockStyle.Fill, Margin = new Padding(0) }; SetColumns(_layout);
            _name.Name = "ClaudeProfileName-" + profile.Id; _state.Name = "ClaudeProfileState-" + profile.Id;
            _identity = Stack(_name, _state); _identity.Name = "ClaudeIdentity-" + profile.Id;
            _name.Dock = DockStyle.None; _name.Anchor = AnchorStyles.Left | AnchorStyles.Right; _name.Height = 24;
            _usage = new TableLayoutPanel { Name = "ClaudeUsageColumn-" + profile.Id, Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0), BackColor = Color.Transparent };
            _usage.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
            _usage.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 56)); _usage.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
            _remaining.Name = "ClaudeWeeklyRemaining-" + profile.Id; _remaining.Dock = DockStyle.Fill;
            _usage.Controls.Add(_remaining, 0, 0); _usage.Controls.Add(_bar, 1, 0);
            _reset = Stack(_resetRelative, _resetDate); _reset.Name = "ClaudeResetColumn-" + profile.Id;
            SelectButton = AccountUiTheme.Button("ClaudeSelect-" + profile.Id, UiText.T("선택"));
            MoreButton = new AccountButton { Name = "ClaudeMore-" + profile.Id, Text = "···", Quiet = true, Font = AccountFonts.Create(14, true), FlatStyle = FlatStyle.Flat, FlatAppearance = { BorderSize = 0 }, Cursor = Cursors.Hand };
            foreach (var button in new[] { SelectButton, MoreButton }) { button.AutoSize = false; button.Margin = new Padding(0); button.Padding = new Padding(0); button.Anchor = AnchorStyles.None; }
            _layout.Controls.Add(_identity, 0, 0); _layout.Controls.Add(_usage, 1, 0); _layout.Controls.Add(_reset, 2, 0);
            _layout.Controls.Add(SelectButton, 3, 0); _layout.Controls.Add(MoreButton, 4, 0); Controls.Add(_layout);
        }

        private static TableLayoutPanel Stack(params Label[] labels)
        {
            var panel = AccountUiTheme.Stack(labels.Length);
            for (var index = 0; index < labels.Length; index++)
            {
                panel.RowStyles.Add(new RowStyle(SizeType.Percent, index == 0 ? 55 : 45));
                labels[index].Dock = DockStyle.Fill; labels[index].AutoSize = false; labels[index].AutoEllipsis = true;
                labels[index].TextAlign = ContentAlignment.MiddleLeft; panel.Controls.Add(labels[index], 0, index);
            }
            return panel;
        }

        internal void DisplayProfile(ClaudeProfile profile, bool selected, bool appAccount, bool busy)
        {
            Profile = profile; IsSelected = selected; IsAppAccount = appAccount; _busy = busy; _name.Text = profile.Label;
            _layout.Active = selected; _layout.Invalidate();
            MoreButton.Enabled = !busy; MoreButton.AccessibleName = UiText.F($"{profile.Label} 계정 관리 메뉴");
        }

        internal void ApplyGeometry(float scale)
        {
            Height = (int)Math.Round(66 * scale); ScaleColumns(_layout, scale, 10);
            _name.Height = (int)Math.Round(24 * scale);
            _identity.Margin = new Padding(0, 0, (int)(24 * scale), 0); _usage.Margin = new Padding(0, 0, (int)(24 * scale), 0);
            _bar.Height = (int)Math.Round(6 * scale); _bar.MinimumSize = new Size((int)(20 * scale), (int)(6 * scale)); _bar.Margin = new Padding((int)(10 * scale), 0, 0, 0);
            _reset.Margin = new Padding(0, 0, (int)(10 * scale), 0);
            SelectButton.Size = SelectButton.MinimumSize = new Size((int)(84 * scale), (int)(30 * scale));
            MoreButton.Size = MoreButton.MinimumSize = new Size((int)(32 * scale), (int)(30 * scale));
        }

        internal void DisplayUsage(ClaudeUsageResult? usage, Exception? failure)
        {
            using var layout = AccountUiTheme.DeferLayout(this);
            var now = DateTimeOffset.Now; var window = usage?.Snapshot.Weekly;
            RequiresLogin = failure is ClaudeLoginRequiredException or ClaudeIdentityChangedException;
            var identityChanged = failure is ClaudeIdentityChangedException;
            var remaining = !RequiresLogin && !identityChanged && ClaudeDisplayMetrics.IsUsable(window, now) ? UsagePercentage.Remaining(window!.UsedPercent) : null;
            var stale = usage?.IsStale == true || failure is not null;
            _remaining.Text = remaining is { } value ? stale ? UiText.F($"이전 {value}%") : $"{value}%" : "—";
            _remaining.ForeColor = remaining is null || stale ? AccountUiTheme.Muted : AccountUiTheme.Text;
            _usage.ColumnStyles[0].Width = (stale && remaining is not null ? 78 : 56) * DeviceDpi / 96f;
            _bar.Remaining = remaining; _bar.FillColor = IsSelected ? AccountUiTheme.Apricot : AccountUiTheme.Blue;
            _bar.TrackColor = IsSelected ? Color.FromArgb(244, 226, 212) : AccountUiTheme.Raised;
            _state.Text = failure is ClaudeLoginRequiredException ? UiText.T("CLI 로그인 필요") : failure is not null ? UsageIndicatorForm.FriendlyClaudeError(failure)
                : Profile.Identity is null ? UiText.T("로그인 필요") : usage is null ? UiText.T("사용량 미확인")
                : usage.IsStale || window?.ResetsAt <= now ? UiText.T("갱신 필요") : "";
            var problem = _state.Text.Length > 0;
            var showState = problem;
            _state.Visible = showState; _state.ForeColor = stale && problem ? AccountUiTheme.Warning : AccountUiTheme.Muted;
            _identity.RowStyles[0].SizeType = SizeType.Percent; _identity.RowStyles[0].Height = 100;
            _identity.RowStyles[1].SizeType = SizeType.Absolute; _identity.RowStyles[1].Height = showState ? 18 * DeviceDpi / 96f : 0;
            _resetRelative.Text = window?.ResetsAt is { } reset ? CombinedUsagePanel.TimeUntil(reset, now) : UiText.T("초기화 미확인");
            _resetDate.Text = window?.ResetsAt is { } date ? date.ToLocalTime().ToString("MM.dd HH:mm") : "—";
            ((AccountButton)SelectButton).ActiveBadge = IsAppAccount && !RequiresLogin;
            SelectButton.Text = RequiresLogin ? UiText.T("로그인") : IsAppAccount ? UiText.T("사용 중") : "";
            SelectButton.Visible = RequiresLogin || IsAppAccount;
            SelectButton.Enabled = !_busy && RequiresLogin;
            SelectButton.AccessibleName = Profile.Label + " · " + SelectButton.Text;
            var details = (Profile.Identity?.Hint ?? UiText.T("로그인 필요")) + (usage is null ? "" : "\n" + AccountUsageText.LastUpdated(usage.LastUpdatedAt, now));
            if (failure is not null) details += "\n" + UsageIndicatorForm.FriendlyClaudeError(failure);
            else if (usage?.IsStale == true && usage.RetryAfter is { } retry) details += "\n" + UiText.F($"이전 확인값 · 다음 확인 가능 {retry.ToLocalTime():HH:mm}");
            _details.SetToolTip(_name, details); _details.SetToolTip(_state, details); _details.SetToolTip(_remaining, details);
            SelectButton.AccessibleDescription = RequiresLogin ? UiText.T("이 계정의 사용량 조회 로그인이 필요합니다.")
                : UiText.T("Claude 앱이 지금 이 계정으로 로그인되어 있습니다.");
            _details.SetToolTip(SelectButton, SelectButton.AccessibleDescription);
            _name.AccessibleDescription = _state.AccessibleDescription = _remaining.AccessibleDescription = details;
            ApplyGeometry(DeviceDpi / 96f);
        }

        protected override void Dispose(bool disposing) { if (disposing) _details.Dispose(); base.Dispose(disposing); }
    }

    private void ShowMenu(ClaudeProfile profile, Control anchor)
    {
        // Closed fires while WinForms is still hiding the dropdown. Disposing it
        // there can crash both menu actions and outside-click dismissal.
        _profileMenu?.Dispose();
        var menu = _profileMenu = new ContextMenuStrip { Font = Font, ShowImageMargin = false, BackColor = AccountUiTheme.Surface, ForeColor = AccountUiTheme.Text,
            Renderer = new ToolStripProfessionalRenderer(new AccountMenuColors()) };
        menu.Items.Add(UiText.T("사용량 갱신"), null, async (_, _) => await ReadUsageAsync(profile.Id));
        menu.Items.Add(UiText.T("이름 변경"), null, (_, _) =>
        {
            using var dialog = new AccountNameDialog(profile.Label);
            if (dialog.ShowDialog(FindForm()) != DialogResult.OK) { SetStatus(UiText.T("이름 변경을 취소했습니다.")); return; }
            try
            {
                _controller.Store.Rename(profile.Id, dialog.AccountName);
                Reload(); SetStatus(UiText.T("계정 이름을 변경했습니다."), success: true);
            }
            catch (Exception ex) { SetStatus(ex.Message, true); }
        });
        menu.Items.Add(UiText.T("상세 정보"), null, (_, _) => ShowAccountInfo(profile));
        menu.Items.Add(UiText.T("다시 로그인"), null, async (_, _) => await SignInAgainAsync(profile));
        menu.Items.Add(UiText.T("로그아웃"), null, async (_, _) =>
        {
            if (MessageBox.Show(FindForm(), UiText.T("이 프로필에서 로그아웃할까요? 같은 프로필의 작업을 먼저 마쳐주세요."), UiText.T("Claude 로그아웃"), MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            await RunAsync(UiText.T("Claude에서 로그아웃하고 있습니다…"), async token => { await _controller.LogoutAsync(profile, token); return UiText.T("Claude에서 로그아웃했습니다."); });
        });
        menu.Items.Add(new ToolStripSeparator());
        var remove = menu.Items.Add(UiText.T("목록에서 제거"), null, (_, _) =>
        {
            if (MessageBox.Show(FindForm(), UiText.T("계정 목록에서 제거할까요? Claude의 로그인과 대화 파일은 유지됩니다."), UiText.T("목록에서 제거"), MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            try { _controller.Store.Remove(profile.Id); Reload(); } catch (Exception ex) { SetStatus(ex.Message, true); }
        });
        remove.Enabled = profile.Id != _controller.SelectedProfile.Id;
        menu.Show(anchor, new Point(0, anchor.Height));
    }

    private void ShowAccountInfo(ClaudeProfile profile)
    {
        using var dialog = new Form { Name = "ClaudeAccountInfoDialog", Text = UiText.T("계정 상세"), ClientSize = new Size(460, 280), StartPosition = FormStartPosition.CenterParent, FormBorderStyle = FormBorderStyle.FixedDialog, MaximizeBox = false, MinimizeBox = false };
        AccountUiTheme.SetForm(dialog);
        var layout = AccountUiTheme.Stack(5); layout.Padding = new Padding(24);
        foreach (var height in new[] { 44, 34, 58, 52, 44 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.Controls.Add(AccountUiTheme.Label(profile.Label, 16, true), 0, 0);
        layout.Controls.Add(AccountUiTheme.Label(profile.Identity?.Hint ?? UiText.T("로그인 필요"), color: AccountUiTheme.Muted), 0, 1);
        var usage = _controller.LastUsage(profile.Id); var window = usage?.Snapshot.Weekly; var failure = _controller.LastFailure(profile.Id);
        var remaining = ClaudeDisplayMetrics.IsUsable(window, DateTimeOffset.Now) ? UsagePercentage.Remaining(window!.UsedPercent) : null;
        var value = remaining is { } percent ? $"{percent}%" : "—";
        var reset = window?.ResetsAt is { } date ? date.ToLocalTime().ToString("MM.dd HH:mm") : "—";
        layout.Controls.Add(AccountUiTheme.Label(UiText.T("주간 잔여") + "  " + value + "\n" + UiText.T("초기화") + "  " + reset), 0, 2);
        var observed = usage is null ? UiText.T("사용량 미확인") : AccountUsageText.LastUpdated(usage.LastUpdatedAt, DateTimeOffset.Now);
        if (failure is not null) observed = UsageIndicatorForm.FriendlyClaudeError(failure) + "\n" + observed;
        var details = AccountUiTheme.Label(observed, color: AccountUiTheme.Muted); details.AutoSize = false; details.Dock = DockStyle.Fill; layout.Controls.Add(details, 0, 3);
        var close = AccountUiTheme.Button("CloseClaudeAccountInfoButton", UiText.T("닫기")); close.DialogResult = DialogResult.OK; layout.Controls.Add(close, 0, 4); dialog.AcceptButton = dialog.CancelButton = close;
        dialog.Controls.Add(layout); dialog.ShowDialog(FindForm());
    }

    private async Task RunAsync(string message, Func<CancellationToken, Task<string>> action, string? usageProfileId = null)
    {
        if (IsBusy) return;
        using var cancellation = new CancellationTokenSource(); _cancellation = cancellation;
        SetStatus(message); Reload(); BusyChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            using var operationLease = AccountOperationLease.Acquire();
            SetStatus(await action(cancellation.Token), success: true);
        }
        catch (OperationCanceledException) { SetStatus(UiText.T("취소했습니다. 선택한 계정은 유지됩니다.")); }
        catch (ClaudeUsageTemporarilyUnavailableException ex) { SetStatus(UiText.F($"다음 확인 가능 {ex.RetryAfter.ToLocalTime():HH:mm} · Claude 사용량은 계정별로 10분 간격으로 확인합니다."), usageFailureProfileId: usageProfileId); }
        catch (Exception ex) { SetStatus(UsageIndicatorForm.FriendlyClaudeError(ex), true, usageFailureProfileId: usageProfileId); }
        finally { _cancellation = null; Reload(); BusyChanged?.Invoke(this, EventArgs.Empty); }
    }

    private void ClearResolvedUsageFailure()
    {
        if (IsBusy || _usageFailureProfileId is not { } id || _operationMessage != _usageFailureMessage ||
            _controller.LastFailure(id) is not null || _controller.LastUsage(id) is not { IsStale: false } usage ||
            _usageFailureObservedAt is { } previous && usage.LastUpdatedAt <= previous) return;
        _operationMessage = _usageFailureMessage = _usageFailureProfileId = null;
        _usageFailureObservedAt = null; _operationError = _operationSuccess = false;
    }

    private void SetStatus(string text, bool error = false, bool success = false, string? usageFailureProfileId = null)
    {
        _operationMessage = UiText.Message(text); _operationError = error; _operationSuccess = success;
        _usageFailureProfileId = usageFailureProfileId;
        _usageFailureMessage = usageFailureProfileId is null ? null : _operationMessage;
        _usageFailureObservedAt = usageFailureProfileId is null ? null : _controller.LastUsage(usageFailureProfileId)?.LastUpdatedAt;
        using var layout = AccountUiTheme.DeferLayout(_layout); RenderNotice(); UpdateActions();
    }
    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _profileMenu?.Dispose(); _actionDetails.Dispose();
            _controller.UsageChanged -= OnUsageChanged;
            _controller.SelectionChanged -= OnSelectionChanged;
            if (_shelf is not null) _shelf.StatusChanged -= OnShelfChanged;
            if (_empty.Parent is null) _empty.Dispose();
        }
        base.Dispose(disposing);
    }
}
