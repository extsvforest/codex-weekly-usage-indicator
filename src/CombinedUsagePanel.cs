namespace WeeklyUsageIndicator;

internal sealed class CombinedUsagePanel : UserControl
{
    internal const int PreferredLogicalHeight = 172;
    private readonly AccountMetricLabel _total = new(40);
    private readonly Label _capacity = AccountUiTheme.Label("", 14, color: AccountUiTheme.Subtle);
    private readonly Label _coverage = AccountUiTheme.Label("", color: AccountUiTheme.Muted);
    private readonly AccountUsageBar _bar = new() { Dock = DockStyle.Fill, FillColor = AccountUiTheme.Accent, Margin = new Padding(0) };
    private readonly AccountMetricLabel _next = new(30);
    private readonly Label _nextAccount = AccountUiTheme.Label("", color: AccountUiTheme.Muted);
    private readonly Label _nextDate = AccountUiTheme.Label("", color: AccountUiTheme.Muted);
    private readonly Label _last = AccountUiTheme.Label("", 9.5f, color: AccountUiTheme.Subtle);
    private readonly TableLayoutPanel _totalLine, _nextDetail, _totalColumn, _nextColumn;
    private readonly AccountSummaryLayout _layout;
    internal string CheckedAtText { get; private set; } = "";
    internal event EventHandler? SummaryChanged;

    internal CombinedUsagePanel()
    {
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        Name = "CombinedUsagePanel"; BackColor = Color.Transparent;
        _total.Name = "CombinedRemainingLabel"; _coverage.Name = "CombinedCoverageLabel";
        _next.Name = "NextResetLabel"; _last.Name = "LatestResetLabel";
        var layout = _layout = new AccountSummaryLayout { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Padding = new Padding(1, 1, 1, 4), Margin = new Padding(0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var totalLine = _totalLine = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0), BackColor = Color.Transparent };
        totalLine.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        totalLine.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); totalLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _total.Dock = _capacity.Dock = DockStyle.Fill; _capacity.AutoSize = false; _capacity.TextAlign = ContentAlignment.MiddleLeft;
        totalLine.Controls.Add(_total, 0, 0); totalLine.Controls.Add(_capacity, 1, 0);
        var title = AccountUiTheme.Label(UiText.T("주간 잔여"), color: AccountUiTheme.Muted);
        title.Name = "UsageSummaryTitle";
        _totalColumn = Column(title, totalLine, _coverage, _bar);
        var nextDetail = _nextDetail = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0), BackColor = Color.Transparent };
        nextDetail.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); nextDetail.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        nextDetail.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        nextDetail.Controls.Add(_nextAccount, 0, 0); nextDetail.Controls.Add(_nextDate, 1, 0);
        _nextColumn = Column(AccountUiTheme.Label(UiText.T("다음 초기화"), color: AccountUiTheme.Muted), _next, nextDetail, _last);
        layout.Controls.Add(_totalColumn, 0, 0); layout.Controls.Add(_nextColumn, 1, 0);
        foreach (var label in new Label[] { _next, _coverage, _nextAccount, _nextDate, _last })
        { label.Dock = DockStyle.Fill; label.AutoSize = false; label.AutoEllipsis = true; label.TextAlign = ContentAlignment.MiddleLeft; }
        nextDetail.Layout += (_, _) => AlignNextDetail(); Controls.Add(layout);
    }

    private void AlignNextDetail()
    {
        var dateWidth = TextRenderer.MeasureText(_nextDate.Text, _nextDate.Font).Width;
        var nameWidth = TextRenderer.MeasureText(_nextAccount.Text, _nextAccount.Font).Width;
        var width = _nextDate.Text.Length == 0 ? _nextDetail.ClientSize.Width : Math.Min(nameWidth, Math.Max(0, _nextDetail.ClientSize.Width - dateWidth));
        if (Math.Abs(_nextDetail.ColumnStyles[0].Width - width) > .5f) _nextDetail.ColumnStyles[0].Width = width;
    }

    private static TableLayoutPanel Column(Label title, Control value, Control detail, Control lower)
    {
        var column = AccountUiTheme.Stack(4); column.Padding = new Padding(24, 14, 24, 16);
        title.Dock = DockStyle.Fill; title.AutoSize = false; title.TextAlign = ContentAlignment.MiddleLeft;
        column.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); column.RowStyles.Add(new RowStyle(SizeType.Absolute, 66));
        column.RowStyles.Add(new RowStyle(SizeType.Absolute, 27)); column.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        column.Controls.Add(title, 0, 0); column.Controls.Add(value, 0, 1); column.Controls.Add(detail, 0, 2); column.Controls.Add(lower, 0, 3);
        return column;
    }

    internal void Display(CombinedUsageSnapshot snapshot, bool busy, int completed = 0, bool querying = false)
    {
        using var layout = AccountUiTheme.DeferLayout(this);
        var now = DateTimeOffset.Now; var complete = snapshot.IsComplete(now); var count = snapshot.ConfirmedCount(now);
        var scale = DeviceDpi / 96f;
        _layout.Padding = new Padding((int)Math.Ceiling(scale), (int)Math.Ceiling(scale), (int)Math.Ceiling(scale), (int)Math.Ceiling(4 * scale));
        foreach (var column in new[] { _totalColumn, _nextColumn })
        {
            column.Padding = new Padding((int)(24 * scale), (int)(14 * scale), (int)(24 * scale), (int)(16 * scale));
            column.RowStyles[0].Height = 24 * scale; column.RowStyles[1].Height = 66 * scale; column.RowStyles[2].Height = 27 * scale;
        }
        _capacity.Padding = new Padding((int)(5 * scale), (int)(26 * scale), 0, 0);
        _total.Text = complete ? $"{snapshot.ConfirmedRemaining(now)}%" : "—";
        _totalLine.ColumnStyles[0].Width = _total.ContentWidth + 4 * scale;
        _capacity.Text = snapshot.Accounts.Count > 0 ? $"/ {snapshot.Accounts.Count * 100}%" : "";
        _total.ForeColor = complete ? AccountUiTheme.Text : AccountUiTheme.Muted;
        _coverage.Text = complete ? UiText.F($"{snapshot.Accounts.Count}개 계정 합계") : !snapshot.IsBatch ? (snapshot.CompletedAt is null ? UiText.T("저장된 값 · 전체 갱신 필요") : UiText.T("개별 확인값 · 전체 갱신 필요")) : count > 0
            ? UiText.F($"확인된 잔여 {snapshot.ConfirmedRemaining(now)}% · {count}/{snapshot.Accounts.Count}개") : UiText.T("전체 합계 확인 필요");
        _coverage.ForeColor = complete ? AccountUiTheme.Muted : AccountUiTheme.Warning;
        _coverage.AccessibleDescription = complete ? UiText.F($"{snapshot.Accounts.Count}개 계정 · 계정당 100% 기준") : _coverage.Text;
        _bar.Remaining = complete ? (int)Math.Round(snapshot.ConfirmedRemaining(now) / (double)snapshot.Accounts.Count) : null;
        _bar.Dock = DockStyle.None; _bar.Anchor = AnchorStyles.Left | AnchorStyles.Right; _bar.Height = (int)(6 * scale);
        _bar.MinimumSize = new Size((int)(30 * scale), (int)(6 * scale));
        var dated = snapshot.ByReset().Where(row => row.IsCurrent(now)).ToArray(); var next = dated.FirstOrDefault();
        _next.Text = next?.Account.Usage?.ResetsAt is { } reset ? TimeUntil(reset, now) : UiText.T("확인 필요");
        _nextAccount.Text = next is null ? UiText.T("계정별 확인 상태를 살펴보세요.") : next.Account.Label;
        _nextDate.Text = next is null ? "" : $" · {next.Account.Usage!.ResetsAt!.Value.ToLocalTime():MM.dd HH:mm}";
        var uncertain = snapshot.ByReset().FirstOrDefault(row => !row.IsCurrent(now) &&
            (row.Account.Usage?.ResetsAt is null || next is null || row.Account.Usage.ResetsAt <= next.Account.Usage!.ResetsAt));
        if (uncertain is not null)
        {
            _next.Text = UiText.T("확인 필요"); _nextDate.Text = "";
            _nextAccount.Text = uncertain.Account.Usage?.ResetsAt is { } previous
                ? UiText.F($"{uncertain.Account.Label} · 이전 일정 {previous.ToLocalTime():MM/dd HH:mm}") : UiText.F($"{uncertain.Account.Label} · 초기화 시각 미확인");
        }
        var latest = dated.LastOrDefault()?.Account.Usage?.ResetsAt;
        _last.Text = (complete ? UiText.T("가장 늦은 초기화") : UiText.T("확인된 계정 중 마지막")) + "  " + (latest is { } last ? $"{last.ToLocalTime():MM.dd HH:mm}" : "—");
        CheckedAtText = querying ? UiText.F($"전체 조회 중 · {completed}/{snapshot.Accounts.Count}개 확인") : snapshot.MembershipChanged ? UiText.T("계정 구성 변경 · 전체 갱신 필요")
            : snapshot.WasCanceled ? UiText.T("전체 조회 취소 · 다시 갱신해 주세요.") : snapshot.Interrupted ? UiText.T("전체 조회 중단 · 확인 필요")
            : !snapshot.IsBatch && count > 0 ? UiText.T("개별 확인 · 전체 합계 갱신 필요")
            : snapshot.CompletedAt is { } checkedAt ? complete ? AccountUsageText.LastUpdated(checkedAt, now) : AccountUsageText.PartiallyUpdated(checkedAt, now) : UiText.T("저장된 값 · 전체 갱신 필요");
        AccessibleDescription = CheckedAtText; SummaryChanged?.Invoke(this, EventArgs.Empty);
    }
    internal static string TimeUntil(DateTimeOffset reset, DateTimeOffset now)
    {
        var span = reset - now;
        if (span <= TimeSpan.Zero) return UiText.T("갱신 필요");
        if (span.TotalDays >= 1) return UiText.F($"{(int)span.TotalDays}일 {span.Hours}시간 뒤");
        if (span.TotalHours >= 1) return UiText.F($"{(int)span.TotalHours}시간 {span.Minutes}분 뒤");
        return UiText.F($"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}분 뒤");
    }
}
