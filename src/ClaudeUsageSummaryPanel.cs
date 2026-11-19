namespace WeeklyUsageIndicator;

internal sealed record ClaudeSummaryRow(ClaudeProfile Profile, ClaudeUsageResult? Usage, Exception? Failure);

/// <summary>
/// The overall weekly balance of every registered Claude account, counted once per verified identity and 100% per
/// account, like the Codex total. Only a fresh, usable observation counts; a previous value, a failure or a missing
/// login withholds the total and is reported as partial coverage. The Fable window is a separate limit and is never
/// added in.
/// </summary>
internal sealed record ClaudeCombinedUsage(int Accounts, int Confirmed, int Remaining, DateTimeOffset? OldestObservation,
    ClaudeSummaryRow? Next, ClaudeSummaryRow? Uncertain, DateTimeOffset? LatestReset)
{
    internal bool Complete => Accounts > 0 && Confirmed == Accounts;

    internal static bool IsConfirmed(ClaudeSummaryRow row, DateTimeOffset now) => row.Failure is null
        && row.Usage is { IsStale: false } usage && ClaudeDisplayMetrics.IsUsable(usage.Snapshot.Weekly, now);

    internal static ClaudeCombinedUsage From(IReadOnlyList<ClaudeSummaryRow> rows, DateTimeOffset now)
    {
        // Two profiles signed in to the same subscription are one account: keep its best observation.
        var accounts = rows.GroupBy(row => row.Profile.Identity?.Key ?? "profile:" + row.Profile.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(row => IsConfirmed(row, now))
                .ThenByDescending(row => row.Usage?.LastUpdatedAt ?? DateTimeOffset.MinValue).First()).ToArray();
        var confirmed = accounts.Where(row => IsConfirmed(row, now)).ToArray();
        DateTimeOffset? Reset(ClaudeSummaryRow row) => row.Usage?.Snapshot.Weekly?.ResetsAt;
        var dated = confirmed.Where(row => Reset(row) is not null).OrderBy(row => Reset(row)).ToArray();
        var next = dated.FirstOrDefault();
        // An unconfirmed account whose (previous) reset may come first leaves the next reset uncertain.
        var uncertain = accounts.Where(row => !IsConfirmed(row, now)).OrderBy(row => Reset(row) ?? DateTimeOffset.MaxValue)
            .FirstOrDefault(row => Reset(row) is not { } previous || next is null || previous <= Reset(next));
        return new ClaudeCombinedUsage(accounts.Length, confirmed.Length,
            confirmed.Sum(row => UsagePercentage.Remaining(row.Usage!.Snapshot.Weekly!.UsedPercent) ?? 0),
            confirmed.Length == 0 ? null : confirmed.Min(row => row.Usage!.LastUpdatedAt), next, uncertain,
            dated.LastOrDefault() is { } last ? Reset(last) : null);
    }
}

internal sealed class ClaudeUsageSummaryPanel : UserControl
{
    internal const int PreferredLogicalHeight = 172;
    private readonly AccountSummaryLayout _layout = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
    private readonly AccountMetricLabel _remaining = new(40) { Name = "ClaudeSelectedWeeklyRemainingLabel", Dock = DockStyle.Fill };
    private readonly AccountMetricLabel _reset = new(30) { Name = "ClaudeSelectedResetLabel", Dock = DockStyle.Fill };
    private readonly Label _capacity = AccountUiTheme.Label("", 14, color: AccountUiTheme.Subtle);
    private readonly Label _profile = AccountUiTheme.Label("", color: AccountUiTheme.Muted);
    private readonly Label _resetDate = AccountUiTheme.Label("", color: AccountUiTheme.Muted);
    private readonly Label _checkedAt = AccountUiTheme.Label("", 9.5f, color: AccountUiTheme.Subtle);
    private readonly AccountUsageBar _bar = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, FillColor = AccountUiTheme.Accent, Margin = new Padding(0) };
    private readonly TableLayoutPanel _left, _right, _valueLine;
    private readonly ToolTip _details = new() { InitialDelay = 500, ReshowDelay = 100, AutoPopDelay = 10000 };
    internal string CheckedAtText { get; private set; } = "";
    internal event EventHandler? SummaryChanged;

    internal ClaudeUsageSummaryPanel()
    {
        Name = "ClaudeUsageSummaryPanel"; BackColor = Color.Transparent;
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = AccountUiTheme.Label(UiText.T("주간 잔여"), color: AccountUiTheme.Muted);
        title.Name = "UsageSummaryTitle";
        title.Dock = DockStyle.Fill; title.AutoSize = false; title.TextAlign = ContentAlignment.MiddleLeft;
        _profile.Name = "ClaudeSelectedProfileLabel"; _resetDate.Name = "ClaudeSelectedResetDateLabel"; _checkedAt.Name = "ClaudeSelectedCheckedAtLabel";
        foreach (var label in new[] { _profile, _resetDate, _checkedAt })
        { label.Dock = DockStyle.Fill; label.AutoSize = false; label.AutoEllipsis = true; label.TextAlign = ContentAlignment.MiddleLeft; }
        _valueLine = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0), BackColor = Color.Transparent };
        _valueLine.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); _valueLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _valueLine.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        _capacity.Dock = DockStyle.Fill; _capacity.AutoSize = false; _capacity.TextAlign = ContentAlignment.MiddleLeft;
        _valueLine.Controls.Add(_remaining, 0, 0); _valueLine.Controls.Add(_capacity, 1, 0);
        _left = Column(title, _valueLine, _profile, _bar);
        var resetTitle = AccountUiTheme.Label(UiText.T("다음 초기화"), color: AccountUiTheme.Muted);
        resetTitle.Dock = DockStyle.Fill; resetTitle.AutoSize = false; resetTitle.TextAlign = ContentAlignment.MiddleLeft;
        _right = Column(resetTitle, _reset, _resetDate, _checkedAt);
        _layout.Controls.Add(_left, 0, 0); _layout.Controls.Add(_right, 1, 0); Controls.Add(_layout);
        ApplyGeometry();
    }

    private static TableLayoutPanel Column(Control title, Control value, Control detail, Control lower)
    {
        var column = AccountUiTheme.Stack(4);
        column.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); column.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        column.RowStyles.Add(new RowStyle(SizeType.Absolute, 27)); column.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        column.Controls.Add(title, 0, 0); column.Controls.Add(value, 0, 1); column.Controls.Add(detail, 0, 2); column.Controls.Add(lower, 0, 3);
        return column;
    }

    /// <param name="progress">Accounts checked so far while a refresh of every account runs.</param>
    internal void Display(IReadOnlyList<ClaudeSummaryRow> rows, (int Done, int Total)? progress = null)
    {
        using var layout = AccountUiTheme.DeferLayout(_layout);
        var now = DateTimeOffset.Now; var total = ClaudeCombinedUsage.From(rows, now);
        var complete = total.Complete;
        _remaining.Text = complete ? $"{total.Remaining}%" : "—";
        _remaining.ForeColor = complete ? AccountUiTheme.Text : AccountUiTheme.Muted;
        _capacity.Text = total.Accounts > 0 ? $"/ {total.Accounts * 100}%" : "";
        _bar.Remaining = complete ? (int)Math.Round(total.Remaining / (double)total.Accounts) : null;
        _bar.FillColor = AccountUiTheme.Accent;
        _profile.Text = total.Accounts == 0 ? UiText.T("Claude 계정 연결") : complete ? UiText.F($"{total.Accounts}개 계정 합계")
            : total.Confirmed > 0 ? UiText.F($"확인된 잔여 {total.Remaining}% · {total.Confirmed}/{total.Accounts}개") : UiText.T("전체 합계 확인 필요");
        _profile.ForeColor = complete || total.Accounts == 0 ? AccountUiTheme.Muted : AccountUiTheme.Warning;
        _profile.AccessibleDescription = complete ? UiText.F($"{total.Accounts}개 계정 · 계정당 100% 기준") : _profile.Text;
        if (total.Uncertain is { } uncertain)
        {
            _reset.Text = UiText.T("확인 필요");
            _resetDate.Text = uncertain.Usage?.Snapshot.Weekly?.ResetsAt is { } previous
                ? UiText.F($"{uncertain.Profile.Label} · 이전 일정 {previous.ToLocalTime():MM/dd HH:mm}")
                : UiText.F($"{uncertain.Profile.Label} · 초기화 시각 미확인");
        }
        else if (total.Next is { } next && next.Usage!.Snapshot.Weekly!.ResetsAt is { } reset)
        {
            _reset.Text = CombinedUsagePanel.TimeUntil(reset, now);
            _resetDate.Text = $"{next.Profile.Label} · {reset.ToLocalTime():MM.dd HH:mm}";
        }
        else { _reset.Text = UiText.T("초기화 미확인"); _resetDate.Text = "—"; }
        _checkedAt.Text = (complete ? UiText.T("가장 늦은 초기화") : UiText.T("확인된 계정 중 마지막")) + "  "
            + (total.LatestReset is { } latest ? $"{latest.ToLocalTime():MM.dd HH:mm}" : "—");
        _checkedAt.ForeColor = AccountUiTheme.Subtle;
        var stale = rows.Where(row => row.Usage is { IsStale: true, RetryAfter: not null }).Select(row => row.Usage!.RetryAfter!.Value)
            .DefaultIfEmpty().Max();
        var details = stale != default ? UiText.F($"이전 확인값 · 다음 확인 가능 {stale.ToLocalTime():HH:mm}") : _checkedAt.Text;
        _details.SetToolTip(_checkedAt, details); _checkedAt.AccessibleDescription = details;
        CheckedAtText = progress is { } running ? UiText.F($"전체 조회 중 · {running.Done}/{running.Total}개 확인")
            : total.OldestObservation is { } oldest ? complete ? AccountUsageText.LastUpdated(oldest, now) : AccountUsageText.PartiallyUpdated(oldest, now)
            : rows.Any(row => row.Failure is ClaudeLoginRequiredException) ? UiText.T("CLI 로그인 필요")
            : rows.Any(row => row.Failure is not null) ? UiText.T("조회 실패") : UiText.T("사용량 미확인");
        AccessibleDescription = CheckedAtText;
        ApplyGeometry(); SummaryChanged?.Invoke(this, EventArgs.Empty);
    }

    private void ApplyGeometry()
    {
        var scale = DeviceDpi / 96f;
        int Pixels(int logical) => (int)(logical * scale);
        _layout.Padding = new Padding((int)Math.Ceiling(scale), (int)Math.Ceiling(scale), (int)Math.Ceiling(scale), (int)Math.Ceiling(4 * scale));
        foreach (var column in new[] { _left, _right })
        {
            column.Padding = new Padding(Pixels(24), Pixels(14), Pixels(24), Pixels(16));
            column.RowStyles[0].Height = 24 * scale; column.RowStyles[1].Height = 66 * scale; column.RowStyles[2].Height = 27 * scale;
        }
        _capacity.Padding = new Padding(Pixels(5), Pixels(26), 0, 0);
        _valueLine.ColumnStyles[0].Width = _remaining.ContentWidth + 4 * scale;
        _bar.Height = Pixels(6); _bar.MinimumSize = new Size(Pixels(30), Pixels(6));
    }

    protected override void OnLoad(EventArgs e) { base.OnLoad(e); ApplyGeometry(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); ApplyGeometry(); }
    protected override void Dispose(bool disposing) { if (disposing) _details.Dispose(); base.Dispose(disposing); }
}
