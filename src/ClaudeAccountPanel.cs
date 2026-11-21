namespace WeeklyUsageIndicator;

/// <summary>
/// The Claude page (design screens A, B and G): the total of this session's readings, the main-account callout, the
/// list with a query per row, and the session-list footer. Opening the page reads nothing: a row shows this session's
/// reading, or the saved one with its time, which never counts. A full query reads every signed-in account, at most
/// three at once; a row's query reads that account alone. The Desktop account changes only inside the official app.
/// </summary>
internal sealed class ClaudeAccountPanel : UserControl
{
    private readonly ClaudeAccountController _controller;
    private readonly ClaudeShelfService? _shelf;
    private readonly ClaudeUsageSummaryPanel _summary = new();
    private readonly AccountNotice _callout = new("ClaudeMainCallout");
    private readonly AccountButton _move = AccountUiTheme.Action("ClaudeMoveMainButton", UiText.T("따로 로그인"), AccountButtonKind.Amber);
    private readonly AccountNotice _notice = new("ClaudeNotice", AccountNoticeKind.Progress) { Visible = false };
    private readonly AccountButton _cancel = AccountUiTheme.Action("ClaudeCancelButton", UiText.T("취소"), AccountButtonKind.Secondary, compact: true);
    // The list heading reads "계정" like the column, but "Accounts" in English: its own key (accounts.ko.json).
    private readonly AccountListBar _bar = new("ClaudeListBar", UiText.T("계정 목록"));
    private readonly AccountButton _add = AccountUiTheme.Action("ClaudeAddAccountButton", UiText.T("+ 계정 추가"), AccountButtonKind.Add);
    private readonly AccountButton _register = AccountUiTheme.Action("ClaudeRegisterDefaultButton", UiText.T("기존 계정 연결"), AccountButtonKind.Add);
    private readonly AccountColumnHeaders _headers = new("ClaudeColumnHeaders", AccountGrid.Claude,
        UiText.T("계정"), UiText.T("주간 잔여"), UiText.T("초기화"));
    private readonly AccountRowList _list = new("ClaudeAccountList");
    private readonly AccountText _emptyTitle = new(13.5f, AccountWeight.SemiBold, AccountUiTheme.Ink2) { Name = "ClaudeEmptyState" };
    private readonly AccountText _emptyBody = new(12, AccountWeight.Regular, AccountUiTheme.Ink3) { Name = "ClaudeEmptyStateDetail" };
    private readonly AccountFooter _footer = new("ClaudeFooter");
    private readonly AccountButton _shelfAction = AccountUiTheme.Action("ClaudeShelfRelaunchButton", UiText.T("종료하면 다시 열기"),
        AccountButtonKind.Secondary, compact: true);
    private readonly ToolTip _tips = new() { InitialDelay = 500, ReshowDelay = 100, AutoPopDelay = 10000, ShowAlways = true };
    private readonly System.Windows.Forms.Timer _clock = new() { Interval = 30_000 };
    private readonly Dictionary<string, ClaudeRow> _rows = new(StringComparer.Ordinal);
    private readonly Dictionary<string, CancellationTokenSource> _reads = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Exception> _localFailures = new(StringComparer.Ordinal);
    private readonly HashSet<string> _justRead = new(StringComparer.Ordinal);
    private readonly HashSet<string> _batchDone = new(StringComparer.Ordinal);
    private HashSet<string>? _batchTargets;
    private CancellationTokenSource? _operation;
    private (int Done, int Total)? _batchProgress;
    private string[] _order = Array.Empty<string>();
    private AccountMenu? _menu;
    private IDisposable? _lease;
    private int _leaseUsers;
    private readonly string? _leaseRoot;

    /// <summary>A sign-in, an addition, a removal from the shared home or the full query is running.</summary>
    internal bool IsBusy => _operation is not null;
    /// <summary>One or more rows are being read on their own.</summary>
    internal bool IsReading => _reads.Count > 0;
    /// <summary>A query of every account is running; it only reads usage and can be cancelled at any point.</summary>
    internal bool IsRefreshingAll => _batchTargets is not null;
    internal bool CanRefreshAll => !IsBusy && _controller.ReadableProfiles().Count > 0;
    internal int AccountCount => _rows.Count;
    internal string CheckedAtText => _summary.CheckedAtText;
    internal DateTimeOffset? LatestObservation => _summary.LatestObservation;
    internal (int Done, int Total)? Progress => _batchProgress;
    internal event EventHandler? BusyChanged;
    internal event EventHandler? SummaryChanged;

    /// <param name="leaseRoot">The folder whose operation lock install and update scripts wait on: the Codex account
    /// folder the window works in (the default one outside tests).</param>
    internal ClaudeAccountPanel(ClaudeAccountController controller, ClaudeShelfService? shelf = null, string? leaseRoot = null)
    {
        _controller = controller; _shelf = shelf; _leaseRoot = leaseRoot; Name = "ClaudeAccountPanel";
        AutoScaleMode = AutoScaleMode.None; BackColor = AccountUiTheme.Canvas; DoubleBuffered = true;
        _callout.SetActions(_move);
        _notice.SetActions(_cancel);
        _bar.SetActions(_register, _add);
        _footer.Status.Name = "ClaudeShelfStatusLabel";
        _footer.SetActions(_shelfAction);
        _shelfAction.AccessibleDescription = UiText.T("Claude를 종료하면 세션 목록을 채운 뒤 다시 엽니다. 30분 안에 한 번 적용됩니다.");
        _tips.SetToolTip(_shelfAction, _shelfAction.AccessibleDescription);
        var empty = UiText.T("지금 사용하는 Claude 계정을 먼저 연결하세요.\n추가 계정도 Claude의 공식 로그인으로 등록합니다.").Split('\n');
        _emptyTitle.Text = empty[0]; _emptyBody.Text = empty.Length > 1 ? empty[1] : "";
        Controls.AddRange(new Control[] { _summary, _callout, _notice, _bar, _headers, _list, _emptyTitle, _emptyBody, _footer });
        _list.ClientSizeChanged += (_, _) => PerformLayout();
        _summary.SummaryChanged += (_, _) => SummaryChanged?.Invoke(this, EventArgs.Empty);
        _register.Click += async (_, _) => await RunAsync(UiText.T("기존 Claude 로그인을 확인하고 있습니다…"),
            token => _controller.RegisterDefaultAsync(token));
        _add.Click += async (_, _) => await AddAsync();
        _move.Click += async (_, _) => await RunAsync(UiText.T("공식 Claude 로그인 창에서 같은 계정으로 다시 로그인하세요."),
            token => _controller.MoveDefaultHomeAccountAsync(token));
        _cancel.Click += (_, _) => { _operation?.Cancel(); _cancel.Enabled = false; };
        _shelfAction.Click += (_, _) => _shelf?.ArmRelaunch(!_shelf.Status.RelaunchArmed);
        _clock.Tick += (_, _) => { if (Visible) Render(); };
        _controller.UsageChanged += OnUsageChanged;
        _controller.ReadStateChanged += OnUsageChanged;
        _controller.SelectionChanged += OnSelectionChanged;
        if (_shelf is not null) _shelf.StatusChanged += OnShelfChanged;
        _clock.Start();
        Reload();
    }

    private void OnUsageChanged(object? sender, ClaudeUsageObservedEventArgs e) => Post(() =>
    {
        if (_rows.ContainsKey(e.ProfileId)) Render();
    });
    private void OnSelectionChanged(object? sender, EventArgs e) => Post(() => { if (!IsBusy) Reload(); });
    private void OnShelfChanged(object? sender, EventArgs e) => Post(() => { if (!IsBusy) Render(); });

    private void Post(Action action)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try { BeginInvoke((Action)(() => { if (!IsDisposed && !Disposing) action(); })); }
        catch (InvalidOperationException) when (IsDisposed || Disposing || !IsHandleCreated) { }
    }

    // "In use" means the account the Desktop app confirmed in its current run. Without that observation the
    // widget's selection keeps the row highlight but no badge.
    private string? AppProfileId => _shelf?.Status is { AppConfirmed: true } status ? status.AppProfileId : null;
    private string InUseId => AppProfileId ?? _controller.SelectedProfile.Id;

    /// <summary>Rebuilds the rows from the store and shows every value again.</summary>
    internal void Reload()
    {
        if (IsDisposed) return;
        var profiles = _controller.Store.ListProfiles();
        var ids = profiles.Select(profile => profile.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var gone in _rows.Keys.Where(id => !ids.Contains(id)).ToArray())
        {
            _rows.Remove(gone); _localFailures.Remove(gone); _justRead.Remove(gone);
        }
        foreach (var profile in profiles)
        {
            if (_rows.TryGetValue(profile.Id, out var row)) row.Profile = profile;
            else _rows.Add(profile.Id, CreateRow(profile));
        }
        Render(profiles);
    }

    private void Render() => Render(_controller.Store.ListProfiles());

    private void Render(IReadOnlyList<ClaudeProfile> profiles)
    {
        if (IsDisposed || profiles.Any(profile => !_rows.ContainsKey(profile.Id))) { if (!IsDisposed) BeginReloadOnce(); return; }
        using var layout = AccountUiTheme.DeferLayout(this);
        var now = AccountUsageText.Now;
        var summaryRows = profiles.Select(profile => new ClaudeSummaryRow(profile, _controller.LastUsage(profile.Id),
            _controller.LastFailure(profile.Id))).ToArray();
        var saved = profiles.Where(profile => _controller.LastUsage(profile.Id) is null)
            .Select(profile => _controller.SavedUsage(profile.Id)?.ObservedAt).OfType<DateTimeOffset>().ToArray();
        _summary.Display(summaryRows, _batchProgress, saved);
        var recommended = Recommended(summaryRows, InUseId, now);
        var app = AppProfileId; var active = InUseId;
        foreach (var profile in profiles)
            _rows[profile.Id].Display(View(profile, now, profile.Id == active, profile.Id == app, profile.Id == recommended), _tips);
        _list.Arrange(Order(profiles, now).Select(profile => (AccountListRow)_rows[profile.Id]).ToArray());
        _bar.Count.Text = profiles.Count.ToString(System.Globalization.CultureInfo.InvariantCulture);
        _bar.Sort.Visible = profiles.Count > 1;
        var empty = profiles.Count == 0;
        _headers.Visible = _list.Visible = !empty; _emptyTitle.Visible = _emptyBody.Visible = empty;
        // Once the default-home account has moved to its own profile, connecting that shared home again is not offered.
        _register.Visible = !profiles.Any(profile => profile.IsDefault) && !_controller.Store.DefaultHomeRetired;
        _register.Enabled = _add.Enabled = !IsBusy;
        var main = profiles.FirstOrDefault(profile => profile.IsDefault && profile.Identity is not null);
        _callout.Visible = main is not null && !_controller.Store.DefaultHomeRetired;
        if (main is not null)
            _callout.SetMessage(UiText.F($"**{main.Label}**{AccountUsageText.Topic(main.Label)} 앱·터미널과 로그인 폴더가 겹쳐 자주 풀립니다. 위젯용으로 한 번 따로 로그인해 두세요."));
        _move.Enabled = !IsBusy && !IsReading;
        _cancel.Visible = IsBusy && !IsRefreshingAll;
        UpdateShelfFooter(profiles);
        PerformLayout(); Invalidate(true);
    }

    private bool _reloadPosted;
    private void BeginReloadOnce()
    {
        if (_reloadPosted) return;
        _reloadPosted = true;
        Post(() => { _reloadPosted = false; Reload(); });
    }

    private IReadOnlyList<ClaudeProfile> Order(IReadOnlyList<ClaudeProfile> profiles, DateTimeOffset now)
    {
        // While anything is being read the rows keep their places; the new order applies once reading ends.
        if ((IsRefreshingAll || IsReading) && _order.Length > 0)
        {
            var positions = _order.Select((id, index) => (id, index)).ToDictionary(p => p.id, p => p.index, StringComparer.Ordinal);
            return profiles.OrderBy(profile => positions.GetValueOrDefault(profile.Id, int.MaxValue)).ToArray();
        }
        var ordered = profiles.Select((profile, index) => (profile, index, reset: ResetOf(profile)))
            .OrderBy(item => item.reset is null).ThenBy(item => item.reset).ThenBy(item => item.index)
            .Select(item => item.profile).ToArray();
        _order = ordered.Select(profile => profile.Id).ToArray();
        return ordered;
    }

    private DateTimeOffset? ResetOf(ClaudeProfile profile)
    {
        if (RequiresLogin(profile, Failure(profile.Id))) return null;
        var snapshot = _controller.LastUsage(profile.Id)?.Snapshot ?? _controller.SavedUsage(profile.Id)?.Snapshot;
        return UsagePercentage.Remaining(snapshot?.Weekly?.UsedPercent) is null ? null : snapshot!.Weekly!.ResetsAt;
    }

    private Exception? Failure(string id) => _controller.LastFailure(id) ?? _localFailures.GetValueOrDefault(id);
    private static bool RequiresLogin(ClaudeProfile profile, Exception? failure) =>
        profile.Identity is null || failure is ClaudeLoginRequiredException or ClaudeIdentityChangedException;

    /// <summary>An account to use next: one that still has room and fills up again before the one in use does.</summary>
    private static string? Recommended(IReadOnlyList<ClaudeSummaryRow> rows, string inUseId, DateTimeOffset now)
    {
        DateTimeOffset? Reset(ClaudeSummaryRow row) => row.Usage?.Snapshot.Weekly?.ResetsAt;
        var current = rows.FirstOrDefault(row => row.Profile.Id == inUseId);
        if (current is null || !ClaudeCombinedUsage.IsConfirmed(current, now) || Reset(current) is not { } currentReset) return null;
        return rows.Where(row => row.Profile.Id != inUseId && ClaudeCombinedUsage.IsConfirmed(row, now)
                && UsagePercentage.Remaining(row.Usage!.Snapshot.Weekly!.UsedPercent) > 0
                && Reset(row) is { } reset && reset < currentReset
                && row.Profile.Identity?.Key != current.Profile.Identity?.Key)
            .OrderBy(row => Reset(row)).FirstOrDefault()?.Profile.Id;
    }

    private RowView View(ClaudeProfile profile, DateTimeOffset now, bool active, bool app, bool recommended)
    {
        var id = profile.Id;
        var usage = _controller.LastUsage(id);
        var failure = Failure(id);
        var login = RequiresLogin(profile, failure);
        var saved = usage is null && !login ? _controller.SavedUsage(id) : null;
        var snapshot = login ? null : usage?.Snapshot ?? saved?.Snapshot;
        var observedAt = login ? null : usage?.LastUpdatedAt ?? saved?.ObservedAt;
        var inBatch = _batchTargets?.Contains(id) == true && !_batchDone.Contains(id);
        var reading = _reads.ContainsKey(id) || inBatch && _controller.IsReading(id);
        var remaining = UsagePercentage.Remaining(snapshot?.Weekly?.UsedPercent);
        var stale = observedAt is { } at && AccountUsageText.IsStale(at, snapshot?.Weekly?.ResetsAt, now);
        var justRead = _justRead.Contains(id) && usage is { IsStale: false } && now - usage.LastUpdatedAt < TimeSpan.FromMinutes(1);
        return new RowView(profile, active, app, recommended && !reading, login, failure, remaining, snapshot, observedAt,
            saved is not null, usage?.IsStale == true ? usage.RetryAfter ?? now : null, stale, reading, inBatch && !reading, justRead,
            IsBusy);
    }

    private void UpdateShelfFooter(IReadOnlyList<ClaudeProfile> profiles)
    {
        var status = _shelf?.Status;
        var label = status?.AppProfileId is { } id ? profiles.FirstOrDefault(profile => profile.Id == id)?.Label : null;
        var (text, warning, action) = ShelfFooter(status, label, AccountUsageText.Now);
        _footer.SetStatus(text, warning);
        _tips.SetToolTip(_footer.Status, text);
        _shelfAction.Visible = action is not null;
        _shelfAction.Text = action ?? "";
        _shelfAction.Enabled = !IsBusy;
        _footer.Visible = _shelf is not null;
        _footer.PerformLayout();
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
        if (status.MappedAccounts < 2) return (UiText.T("Claude 계정을 두 개 이상 연결하면 세션 목록을 서로 채웁니다."), false, null);
        if (status.AppConfirmed && status.PendingForApp is > 0 and var pending)
            return (UiText.F($"{appLabel ?? ""} 목록에 채울 세션 {pending}개 · Claude를 종료하면 채웁니다."), false,
                UiText.T("종료하면 다시 열기"));
        if (status.Problem == ClaudeShelfProblem.Failed)
            return (UiText.F($"세션 목록 채우기 실패 · {UiText.Message(status.ProblemDetail ?? "")}"), true, null);
        if (status.Problem == ClaudeShelfProblem.Held || !status.LastFillComplete)
            return (UiText.T("세션 목록 일부 보류 · 사용 중이거나 복구 중인 세션은 나중에 채웁니다."), false, null);
        if (status.LastFillAt is { } at)
            return (UiText.F($"세션 목록 · 모든 계정 채움 · {AccountUsageText.DateAndTime(at, now)}"), false, null);
        return (UiText.T("세션 목록 · 다른 계정은 켠 채로, 지금 계정은 Claude를 종료하면 채웁니다."), false, null);
    }

    // The page fills its width under the top bar: summary, callout and notice, the list bar and headers, the rows down
    // to the footer. Distances are the design's (margin-top 14, 12, 16; a 28px bar with 4px under it; 24px headers).
    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var s = DeviceDpi / 96f; var width = ClientSize.Width; var height = ClientSize.Height;
        if (width <= 0 || height <= 0) return;
        void Put(Control control, float x, float y, float w, float h) =>
            AccountUiTheme.Place(control, (int)Math.Round(x), (int)Math.Round(y), (int)Math.Round(Math.Max(0, w)), (int)Math.Round(Math.Max(0, h)));
        var y = 14 * s;
        Put(_summary, 0, y, width, _summary.PreferredHeight); y += _summary.PreferredHeight;
        foreach (var notice in new[] { _callout, _notice }.Where(notice => notice.Visible))
        {
            y += 12 * s; var noticeHeight = notice.HeightFor(width);
            Put(notice, 0, y, width, noticeHeight); y += noticeHeight;
        }
        y += 16 * s;
        Put(_bar, 0, y, width, 28 * s); y += 32 * s;
        var footer = _footer.Visible ? _footer.PreferredHeight : 0;
        var footerTop = height - footer;
        if (_footer.Visible) Put(_footer, 0, footerTop, width, footer);
        if (_list.Visible)
        {
            var headersTop = y; y += 24 * s;
            Put(_list, 0, y, width, footerTop - y);
            Put(_headers, 0, headersTop, _list.ClientSize.Width, 24 * s);
        }
        else
        {
            var x = AccountGrid.Inset * s;
            AccountUiTheme.PlaceLine(_emptyTitle, x, y + 18 * s, 13.5f * 1.4f * s, Math.Min(_emptyTitle.ContentWidth + 1, width - 2 * x));
            AccountUiTheme.PlaceLine(_emptyBody, x, y + (18 + 13.5f * 1.4f + 4) * s, 16.8f * s, Math.Min(_emptyBody.ContentWidth + 1, width - 2 * x));
        }
    }

    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); Render(); }
    protected override void OnVisibleChanged(EventArgs e) { base.OnVisibleChanged(e); if (Visible) Render(); }

    private ClaudeRow CreateRow(ClaudeProfile profile)
    {
        var row = new ClaudeRow(profile);
        row.Read.Click += async (_, _) => await ReadRowAsync(row.Profile.Id);
        row.SignIn.Click += async (_, _) => await SignInAgainAsync(row.Profile);
        row.More.Click += (_, _) => ShowMenu(row);
        return row;
    }

    /// <summary>Reads every signed-in account once, up to three at a time and the in-use one first. Each account keeps its
    /// own ten-minute interval, so one read recently reuses its observation instead of a new command; a failure stays on
    /// its row while the others go on. Accounts that need a sign-in are left out.</summary>
    internal async Task RefreshAllUsageAsync()
    {
        if (IsBusy) return;
        var targets = _controller.ReadableProfiles();
        if (targets.Count == 0)
        {
            ShowNotice(UiText.Message("확인할 수 있는 Claude 계정이 없습니다. 로그인이 필요한 계정은 다시 로그인하세요."), AccountNoticeKind.Error);
            return;
        }
        using var cancellation = new CancellationTokenSource(); _operation = cancellation;
        _batchTargets = targets.Select(profile => profile.Id).ToHashSet(StringComparer.Ordinal); _batchDone.Clear();
        foreach (var id in _batchTargets) { _justRead.Remove(id); _localFailures.Remove(id); }
        _batchProgress = (0, targets.Count);
        HideNotice(); Render(); BusyChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            using var lease = HoldLease();
            // Each account's row updates as soon as it ends; this only counts them.
            await _controller.ReadUsageManyAsync(_batchTargets, cancellation.Token, outcome =>
            {
                _batchDone.Add(outcome.ProfileId);
                if (outcome.Failure is null && outcome.Result is { IsStale: false }) _justRead.Add(outcome.ProfileId);
                _batchProgress = (_batchDone.Count, targets.Count);
                if (!IsDisposed) Render();
            });
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { if (!IsDisposed) ShowNotice(Friendly(ex), AccountNoticeKind.Error); }
        finally
        {
            _operation = null; _batchTargets = null; _batchProgress = null; _batchDone.Clear();
            if (!IsDisposed) { Render(); BusyChanged?.Invoke(this, EventArgs.Empty); }
        }
    }

    /// <summary>Reads the in-use account alone, as its row's ↻ does.</summary>
    internal Task RefreshSelectedUsageAsync() => _rows.ContainsKey(InUseId) ? ReadRowAsync(InUseId) : Task.CompletedTask;

    private async Task ReadRowAsync(string id)
    {
        if (IsBusy || _reads.ContainsKey(id) || !_rows.ContainsKey(id)) return;
        var cancellation = new CancellationTokenSource(); _reads.Add(id, cancellation);
        _justRead.Remove(id); _localFailures.Remove(id);
        Render(); BusyChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            using var lease = HoldLease();
            var result = await _controller.ReadUsageAsync(id, cancellation.Token);
            if (!result.IsStale) _justRead.Add(id);
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { }
        catch (Exception ex) { if (_controller.LastFailure(id) is null) _localFailures[id] = ex; }
        finally
        {
            _reads.Remove(id); cancellation.Dispose();
            if (!IsDisposed) { Render(); BusyChanged?.Invoke(this, EventArgs.Empty); }
        }
    }

    private async Task AddAsync()
    {
        using var dialog = new AccountNameDialog("", adding: true);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        var label = string.IsNullOrWhiteSpace(dialog.AccountName)
            ? UiText.F($"Claude 계정 {_controller.Store.ListProfiles().Count + 1}") : dialog.AccountName;
        await RunAsync(UiText.T("공식 Claude 로그인 창에서 추가할 계정으로 로그인하세요."), token => _controller.AddAsync(label, token));
    }

    private Task SignInAgainAsync(ClaudeProfile profile) => RunAsync(UiText.T("공식 Claude 로그인 창을 열고 있습니다…"), async token =>
    {
        _localFailures.Remove(profile.Id);
        await _controller.LoginAsync(profile, token);
        await _controller.ReadUsageAsync(profile.Id, token);
        _justRead.Add(profile.Id);
    });

    /// <summary>One sign-in or list change at a time. Its progress takes the notice with a cancel; a failure stays there
    /// until the next operation, and a success says nothing more than the rows do.</summary>
    private async Task RunAsync(string message, Func<CancellationToken, Task> action)
    {
        if (IsBusy) return;
        using var cancellation = new CancellationTokenSource(); _operation = cancellation;
        _cancel.Enabled = true;
        ShowNotice(message, AccountNoticeKind.Progress);
        Render(); BusyChanged?.Invoke(this, EventArgs.Empty);
        try
        {
            using var lease = HoldLease();
            await action(cancellation.Token);
            HideNotice();
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { HideNotice(); }
        catch (Exception ex) { if (!IsDisposed) ShowNotice(Friendly(ex), AccountNoticeKind.Error); }
        finally
        {
            _operation = null;
            if (!IsDisposed) { Reload(); BusyChanged?.Invoke(this, EventArgs.Empty); }
        }
    }

    // The operation lease keeps install and update scripts from stopping the widget mid-operation. Reads of different
    // accounts run side by side, so one lease is held while any of this page's work runs.
    private IDisposable HoldLease()
    {
        if (_leaseUsers == 0) _lease = AccountOperationLease.Acquire(_leaseRoot);
        _leaseUsers++;
        return new LeaseUse(this);
    }

    private sealed class LeaseUse(ClaudeAccountPanel owner) : IDisposable
    {
        private bool _done;
        public void Dispose()
        {
            if (_done) return;
            _done = true;
            if (--owner._leaseUsers == 0) { owner._lease?.Dispose(); owner._lease = null; }
        }
    }

    private static string Friendly(Exception exception) => UsageIndicatorForm.FriendlyClaudeError(exception);

    private void ShowNotice(string message, AccountNoticeKind kind)
    {
        _notice.Kind = kind; _notice.SetMessage(message);
        _cancel.Visible = kind == AccountNoticeKind.Progress;
        _notice.Visible = true; PerformLayout();
    }

    private void HideNotice() { _notice.Visible = false; PerformLayout(); }

    /// <summary>Cancels the running query, every row being read and a sign-in that is waiting; used when the window closes.</summary>
    internal void CancelOperation()
    {
        _operation?.Cancel(); _cancel.Enabled = false;
        foreach (var read in _reads.Values) read.Cancel();
    }

    /// <summary>F2: renames the account whose row holds the focus, or the one in use.</summary>
    internal void RenameFocused()
    {
        if (IsBusy) return;
        var focused = _rows.Values.FirstOrDefault(row => row.ContainsFocus) ?? _rows.GetValueOrDefault(InUseId);
        if (focused is not null) Rename(focused.Profile);
    }

    private void ShowMenu(ClaudeRow row)
    {
        // Closed fires while WinForms is still hiding the dropdown. Disposing it there can crash both menu actions
        // and outside-click dismissal, so the previous menu goes when the next one opens.
        _menu?.Dispose();
        var profile = row.Profile; var view = row.LastView;
        var menu = _menu = new AccountMenu(this, "ClaudeAccountMenu");
        menu.Item("ClaudeReadMenuItem", UiText.T("조회"), AccountIcon.Refresh, async (_, _) => await ReadRowAsync(profile.Id))
            .Enabled = view is { RequiresLogin: false, Reading: false } && !IsBusy;
        menu.Item("ClaudeRenameMenuItem", UiText.T("이름 바꾸기"), AccountIcon.Pencil, (_, _) => Rename(profile), shortcut: "F2");
        menu.Item("ClaudeDetailsMenuItem", UiText.T("자세히"), AccountIcon.Info, (_, _) => ShowDetails(profile));
        menu.Separator();
        menu.Item("ClaudeSignInMenuItem", UiText.T("다시 로그인"), AccountIcon.SignIn, async (_, _) => await SignInAgainAsync(profile));
        menu.Item("ClaudeLogoutMenuItem", UiText.T("로그아웃"), AccountIcon.SignOut, async (_, _) =>
        {
            if (MessageBox.Show(FindForm(), UiText.T("이 프로필에서 로그아웃할까요? 같은 프로필의 작업을 먼저 마쳐주세요."),
                    UiText.T("Claude 로그아웃"), MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            await RunAsync(UiText.T("Claude에서 로그아웃하고 있습니다…"), token => _controller.LogoutAsync(profile, token));
        }).Enabled = profile.Identity is not null;
        menu.Separator();
        menu.Item("ClaudeRemoveMenuItem", UiText.T("목록에서 제거"), AccountIcon.Trash, (_, _) =>
        {
            if (MessageBox.Show(FindForm(), UiText.T("계정 목록에서 제거할까요? Claude의 로그인과 대화 파일은 유지됩니다."),
                    UiText.T("목록에서 제거"), MessageBoxButtons.OKCancel) != DialogResult.OK) return;
            try { _controller.Store.Remove(profile.Id); Reload(); }
            catch (Exception ex) { ShowNotice(UiText.Message(ex.Message), AccountNoticeKind.Error); }
        }, danger: true).Enabled = profile.Id != _controller.SelectedProfile.Id;
        row.MenuOpen = true; row.More.MenuOpen = true;
        menu.Closed += (_, _) => { row.MenuOpen = false; row.More.MenuOpen = false; };
        menu.ShowBelow(row.More);
    }

    private void Rename(ClaudeProfile profile)
    {
        using var dialog = new AccountNameDialog(profile.Label);
        if (dialog.ShowDialog(FindForm()) != DialogResult.OK) return;
        try { _controller.Store.Rename(profile.Id, dialog.AccountName); HideNotice(); Reload(); }
        catch (Exception ex) { ShowNotice(UiText.Message(ex.Message), AccountNoticeKind.Error); }
    }

    private void ShowDetails(ClaudeProfile profile)
    {
        var now = AccountUsageText.Now;
        var usage = _controller.LastUsage(profile.Id); var saved = usage is null ? _controller.SavedUsage(profile.Id) : null;
        var snapshot = usage?.Snapshot ?? saved?.Snapshot; var failure = Failure(profile.Id);
        string Remaining(ClaudeUsageWindow? window) => UsagePercentage.Remaining(window?.UsedPercent) is { } value ? $"{value}%" : "—";
        string Used(ClaudeUsageWindow? window) => UsagePercentage.Remaining(window?.UsedPercent) is { } value ? $"{100 - value}%" : "—";
        var rows = new List<(string, string)>
        {
            (UiText.T("주간 잔여"), Remaining(snapshot?.Weekly)),
            (UiText.T("Fable 주간 잔여"), Remaining(snapshot?.Fable)),
            (UiText.T("5시간 사용"), Used(snapshot?.FiveHour)),
            (UiText.T("초기화"), snapshot?.Weekly?.ResetsAt is { } reset ? AccountUsageText.ResetOn(reset, now) : "—"),
            (UiText.T("마지막 조회"), (usage?.LastUpdatedAt ?? saved?.ObservedAt) is { } at ? AccountUsageText.DateAndTime(at, now) : UiText.T("아직 조회 안 함"))
        };
        using var dialog = new AccountDetailsDialog("ClaudeAccountInfoDialog", "CloseClaudeAccountInfoButton", profile.Label,
            profile.Identity?.Hint ?? UiText.T("로그인 필요"), rows, failure is null ? null : Friendly(failure));
        dialog.ShowDialog(FindForm());
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _operation?.Cancel();
            foreach (var read in _reads.Values) read.Cancel();
            _clock.Stop(); _clock.Dispose();
            _menu?.Dispose(); _tips.Dispose();
            _controller.UsageChanged -= OnUsageChanged;
            _controller.ReadStateChanged -= OnUsageChanged;
            _controller.SelectionChanged -= OnSelectionChanged;
            if (_shelf is not null) _shelf.StatusChanged -= OnShelfChanged;
        }
        base.Dispose(disposing);
    }

    private sealed record RowView(ClaudeProfile Profile, bool Active, bool AppAccount, bool Recommended, bool RequiresLogin,
        Exception? Failure, int? Remaining, ClaudeUsageSnapshot? Snapshot, DateTimeOffset? ObservedAt, bool Saved,
        DateTimeOffset? RetryAfter, bool Stale, bool Reading, bool Waiting, bool JustRead, bool Busy);

    /// <summary>A Claude account row: weekly remaining with the Fable and five-hour values under it, the reset, and
    /// the in-use mark, a query and the menu; a row that needs a sign-in offers it instead of the query.</summary>
    private sealed class ClaudeRow : AccountListRow
    {
        private readonly AccountMetricLabel _percent = new(21, 12.5f) { EmptyFont = AccountUiTheme.Type(17) };
        private readonly AccountMeter _meter = new();
        private readonly AccountText _detail = new(11.5f, AccountWeight.Regular, AccountUiTheme.Ink3);
        private readonly AccountText _relative = new(14, AccountWeight.SemiBold);
        private readonly AccountText _absolute = new(11.5f, AccountWeight.Regular, AccountUiTheme.Ink3);
        internal ClaudeProfile Profile { get; set; }
        internal RowView? LastView { get; private set; }
        internal AccountButton Badge { get; }
        internal AccountButton SignIn { get; }
        internal AccountIconButton Read { get; }
        internal AccountIconButton More { get; }

        internal ClaudeRow(ClaudeProfile profile) : base("ClaudeProfileRow-" + profile.Id, AccountUiTheme.Claude, 70, AccountGrid.Claude)
        {
            Profile = profile; var id = profile.Id;
            Title.Name = "ClaudeProfileName-" + id; State.Name = "ClaudeProfileState-" + id; Pill.Name = "ClaudeProfilePill-" + id;
            _percent.Name = "ClaudeWeeklyRemaining-" + id; _detail.Name = "ClaudeUsageDetail-" + id;
            _relative.Name = "ClaudeResetRelative-" + id; _absolute.Name = "ClaudeResetDate-" + id;
            Remark.Text = UiText.T("· 다음 추천"); Remark.Name = "ClaudeRecommended-" + id;
            Badge = AccountUiTheme.Action("ClaudeSelect-" + id, UiText.T("사용 중"), AccountButtonKind.Badge);
            // A mark, not an action: the Desktop account changes only inside the official app.
            Badge.BadgeInk = AccountUiTheme.Claude.Ink; Badge.Enabled = false;
            SignIn = AccountUiTheme.Action("ClaudeSignIn-" + id, UiText.T("다시 로그인"), AccountButtonKind.DangerOutline);
            Read = new AccountIconButton("ClaudeRead-" + id, AccountIcon.Refresh, UiText.T("이 계정만 조회")) { Provider = AccountUiTheme.Claude };
            More = new AccountIconButton("ClaudeMore-" + id, AccountIcon.More, UiText.T("더 보기")) { Provider = AccountUiTheme.Claude };
            _meter.FillColor = AccountUiTheme.Claude.Main; _meter.QueryColor = AccountUiTheme.Claude.Main;
            Controls.AddRange(new Control[] { _percent, _meter, _detail, _relative, _absolute });
            SetActions(Badge, SignIn, Read, More);
        }

        protected override void LayoutCells(float[] left, float[] width, float s, float height)
        {
            LayoutUsage(_percent, _meter, _detail, left[1], width[1], s, height);
            LayoutReset(_relative, _absolute, left[2], width[2], s, height);
        }

        internal void Display(RowView v, ToolTip tips)
        {
            LastView = v;
            var now = AccountUsageText.Now; var ink3 = AccountUiTheme.Ink3; var quiet = AccountUiTheme.Ink4;
            var failed = v.Failure is not null && !v.RequiresLogin && !v.Reading;
            Active = v.Active;
            Title.SetText(Profile.Label, v.ObservedAt is null && !v.Reading && !v.RequiresLogin ? AccountUiTheme.Ink2 : AccountUiTheme.Ink1);
            Remark.Visible = v.Recommended;
            Pill.Visible = false;
            var checkedAt = v.ObservedAt is { } observed ? AccountUsageText.Checked(observed, now) : "";
            if (v.RequiresLogin)
            {
                Pill.Text = Profile.Identity is null ? UiText.T("로그인 필요") : UiText.T("다시 로그인 필요"); Pill.Visible = true;
                ShowState(false);
            }
            else if (v.Reading)
                ShowState(false, (UiText.T("읽는 중…"), AccountUiTheme.Claude.Ink, true),
                    (v.ObservedAt is { } previous ? UiText.F($"· 이전 {AccountUsageText.Time(previous)}") : "", ink3, false));
            else if (v.Waiting)
                ShowState(false, (checkedAt.Length > 0 ? UiText.F($"{checkedAt} · 조회 대기") : UiText.T("조회 대기"), ink3, false));
            else if (failed)
            {
                Pill.Text = UiText.T("조회 실패"); Pill.Visible = true;
                ShowState(false, (Reason(v.Failure!), AccountUiTheme.ErrorInk, false));
                // In a narrow window the retry time matters more than the cause, which stays in the tooltip.
                if (v.Failure is ClaudeUsageTemporarilyUnavailableException unavailable)
                    State.Compact = Runs((UiText.F($"{AccountUsageText.Time(unavailable.RetryAfter)} 재시도"), AccountUiTheme.ErrorInk, false));
            }
            else if (v.JustRead && v.ObservedAt is { } just)
                ShowState(true, (UiText.T("방금 조회"), AccountUiTheme.OkInk, true), (UiText.F($"· {AccountUsageText.Time(just)}"), ink3, false));
            else if (v.ObservedAt is null) ShowState(false, (UiText.T("아직 조회 안 함"), ink3, false));
            else if (v.Stale) ShowState(false, (checkedAt, ink3, false), (UiText.T("· 오래됨"), AccountUiTheme.WarnInk, true));
            else if (v.RetryAfter is { } retry)
                ShowState(false, (checkedAt, ink3, false), (UiText.F($"· {AccountUsageText.Time(retry)} 재시도"), ink3, false));
            else if (v.Saved) ShowState(false, (checkedAt, ink3, false), (UiText.T("· 이전 값"), ink3, false));
            else ShowState(false, (checkedAt, ink3, false), (v.Recommended ? UiText.T("· 가장 먼저 다시 참") : "", ink3, false));

            // A value being read again, a failed, an old, an unrefreshed and a saved one stay visible but step back. The
            // total takes only this session's confirmed readings (ClaudeCombinedUsage.IsConfirmed): an old one still
            // counts until its week resets; a failed, an unrefreshed and a saved one never do.
            var hasValue = v.Remaining is not null;
            var dim = v.Reading || failed || v.Stale || v.RetryAfter is not null || v.Saved;
            _percent.Text = hasValue ? $"{v.Remaining}%" : "—";
            var figure = v.Reading ? Dim(ink3, .52f) : failed ? Dim(ink3, .65f) : dim ? ink3 : AccountUiTheme.Ink1;
            _percent.ForeColor = figure; _percent.UnitColor = v.Reading || failed ? figure : ink3;
            // While a row is read the bar shows only the moving mark.
            _meter.Remaining = v.Reading ? 0 : v.Remaining;
            var grey = dim && !v.Reading;
            _meter.FillColor = grey ? AccountUiTheme.StaleBar : AccountUiTheme.Claude.Main;
            _meter.TrackColor = grey ? AccountUiTheme.StaleTrack : v.Active ? AccountUiTheme.Claude.Track : AccountUiTheme.SubtleBackground;
            _meter.Querying = v.Reading;
            _detail.Visible = hasValue;
            if (hasValue) ShowDetail(v, failed, dim, quiet, ink3);
            else _detail.SetText("");
            if (hasValue && v.Snapshot?.Weekly?.ResetsAt is { } resetAt)
            {
                _relative.SetText(CombinedUsagePanel.TimeUntil(resetAt, now), dim ? ink3 : AccountUiTheme.Ink1,
                    AccountUiTheme.Type(14, dim ? AccountWeight.Regular : AccountWeight.SemiBold));
                _absolute.Text = AccountUsageText.ResetOn(resetAt, now);
            }
            else { _relative.SetText("—", ink3, AccountUiTheme.Type(14)); _absolute.Text = ""; }

            Badge.Visible = v.AppAccount && !v.RequiresLogin;
            Badge.AccessibleName = UiText.F($"{Profile.Label} 사용 중");
            SignIn.Visible = v.RequiresLogin;
            SignIn.Text = Profile.Identity is null ? UiText.T("로그인") : UiText.T("다시 로그인");
            SignIn.Enabled = !v.Busy;
            Read.Visible = !v.RequiresLogin;
            Read.Spinning = v.Reading; Read.Enabled = !v.Busy && !v.Reading && !v.Waiting; Read.Bordered = failed;
            Read.AccessibleName = failed ? UiText.T("다시 조회") : UiText.F($"{Profile.Label} 사용량 조회");
            tips.SetToolTip(Read, v.Reading ? UiText.T("조회 중") : failed ? UiText.T("다시 조회") : UiText.T("이 계정만 조회"));
            More.Enabled = !v.Busy && !v.Reading;
            More.AccessibleName = UiText.F($"{Profile.Label} 계정 관리 메뉴");
            tips.SetToolTip(More, UiText.T("더 보기"));
            var details = Profile.Label + "\n" + (Profile.Identity?.Hint ?? UiText.T("로그인 필요"))
                + (v.ObservedAt is { } savedAt ? "\n" + AccountUsageText.LastUpdated(savedAt, now) : "")
                + (v.Failure is { } failure ? "\n" + UsageIndicatorForm.FriendlyClaudeError(failure) : "");
            foreach (var label in new Control[] { Title, State, Pill, _percent }) { tips.SetToolTip(label, details); label.AccessibleDescription = details; }
            PerformLayout(); Invalidate(true);
        }

        // Fable has its own weekly limit and the five-hour window is use, not room: both stay out of the figure. A low
        // Fable balance takes the warning ink; a quiet line stands for an old, a read again or a failed value.
        private void ShowDetail(RowView v, bool failed, bool dim, Color quiet, Color ink3)
        {
            var fable = UsagePercentage.Remaining(v.Snapshot?.Fable?.UsedPercent);
            var fiveHour = UsagePercentage.Remaining(v.Snapshot?.FiveHour?.UsedPercent) is { } room ? 100 - room : (int?)null;
            var fableText = fable is { } f ? UiText.T("Fable 잔여") + " " + $"{f}%" : "";
            var fiveText = fiveHour is { } used ? UiText.F($"5시간 사용 {used}%") : "";
            // A narrow window keeps the first part of the line whole rather than cutting the second.
            void Quiet(string first, string second)
            {
                _detail.SetText(string.Join(" · ", new[] { first, second }.Where(part => part.Length > 0)), quiet);
                if (first.Length > 0 && second.Length > 0) _detail.Compact = new[] { new AccountRun(first, _detail.Font, quiet) };
            }
            if (failed)
            {
                Quiet(v.ObservedAt is { } at ? UiText.F($"이전 값 ({AccountUsageText.Time(at)})") : "", fableText);
                return;
            }
            if (dim)
            {
                Quiet(fableText, fiveText);
                return;
            }
            var font = AccountUiTheme.Type(11.5f); var strong = AccountUiTheme.Type(11.5f, AccountWeight.SemiBold);
            var runs = new List<AccountRun>();
            if (fable is { } left)
            {
                runs.Add(new AccountRun(UiText.T("Fable 잔여"), font, ink3));
                runs.Add(left <= 25 ? new AccountRun($"{left}%", strong, AccountUiTheme.WarnInk, 3) : new AccountRun($"{left}%", font, ink3, 3));
            }
            var fableRuns = runs.ToArray();
            if (fiveHour is { } used5)
            {
                if (runs.Count > 0) runs.Add(new AccountRun("·", font, quiet, 6));
                runs.Add(new AccountRun(UiText.F($"5시간 사용 {used5}%"), font, used5 == 0 ? quiet : ink3, runs.Count > 0 ? 6 : 0));
            }
            _detail.SetRuns(runs.ToArray());
            if (fableRuns.Length > 0 && runs.Count > fableRuns.Length) _detail.Compact = fableRuns;
        }

        // A row has room for a few words: why it failed and, for a wait, when the next try may run.
        private static string Reason(Exception failure)
        {
            if (failure is ClaudeUsageTemporarilyUnavailableException unavailable)
            {
                var retry = AccountUsageText.Time(unavailable.RetryAfter);
                for (var cause = failure.InnerException; cause is not null; cause = cause.InnerException)
                    if (cause is TimeoutException) return UiText.F($"응답 시간 초과 · {retry} 재시도");
                return failure.InnerException is null ? UiText.F($"요청 한도 초과 · {retry} 재시도") : UiText.F($"조회 지연 · {retry} 재시도");
            }
            return failure switch
            {
                TimeoutException or TaskCanceledException => UiText.T("응답 시간 초과"),
                FileNotFoundException => UiText.T("Claude Code 없음"),
                InvalidDataException => UiText.T("응답을 읽지 못함"),
                InvalidOperationException when failure.Message.StartsWith("설치 또는 다른 계정 작업", StringComparison.Ordinal) => UiText.T("다른 작업 진행 중"),
                _ => UsageIndicatorForm.FriendlyClaudeError(failure)
            };
        }
    }
}
