namespace WeeklyUsageIndicator;

internal sealed class CombinedUsagePanel : UserControl
{
    private readonly Label _total = AccountUiTheme.Label("—", 34, true);
    private readonly Label _capacity = AccountUiTheme.Label("", 17, color: AccountUiTheme.Muted);
    private readonly Label _coverage = AccountUiTheme.Label("", color: AccountUiTheme.Muted);
    private readonly Label _next = AccountUiTheme.Label("—", 23, true);
    private readonly Label _nextAccount = AccountUiTheme.Label("", color: AccountUiTheme.Muted);
    private readonly Label _stamp = AccountUiTheme.Label("", color: AccountUiTheme.Muted);
    private readonly Label _last = AccountUiTheme.Label("", 20, true);
    private readonly Label _lastTitle = AccountUiTheme.Label(UiText.T("가장 늦은 초기화"), color: AccountUiTheme.Muted);
    private readonly TableLayoutPanel _totalLine;
    private readonly AccountSummaryLayout _layout;
    internal CombinedUsagePanel()
    {
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        Name = "CombinedUsagePanel"; BackColor = AccountUiTheme.Background;
        _total.Font = AccountFonts.Create(34, semibold: true); _next.Font = AccountFonts.Create(23, semibold: true);
        _total.Name = "CombinedRemainingLabel"; _coverage.Name = "CombinedCoverageLabel";
        _next.Name = "NextResetLabel"; _stamp.Name = "CombinedCheckedAtLabel"; _last.Name = "LatestResetLabel";
        var layout = _layout = new AccountSummaryLayout { Dock = DockStyle.Fill, ColumnCount = 3, RowCount = 1,
            Padding = new Padding(0, 0, 0, 26), Margin = new Padding(0) };
        foreach (var width in new[] { 36, 36, 28 }) layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, width));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var totalLine = _totalLine = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
        totalLine.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        totalLine.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 150)); totalLine.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        _total.AutoSize = false; _total.Dock = DockStyle.Fill; _total.TextAlign = ContentAlignment.MiddleLeft; _capacity.Dock = DockStyle.Fill;
        _capacity.AutoSize = false; _capacity.TextAlign = ContentAlignment.MiddleLeft; _capacity.Padding = new Padding(4, 10, 0, 0);
        totalLine.Controls.Add(_total, 0, 0); totalLine.Controls.Add(_capacity, 1, 0);
        layout.Controls.Add(Column(AccountUiTheme.Label(UiText.T("전체 주간 잔여"), color: AccountUiTheme.Muted), totalLine, _coverage, 0), 0, 0);
        layout.Controls.Add(Column(AccountUiTheme.Label(UiText.T("다음 초기화"), color: AccountUiTheme.Muted), _next, _nextAccount, 28), 1, 0);
        layout.Controls.Add(Column(_lastTitle, _last, _stamp, 28), 2, 0);
        foreach (var label in new[] { _next, _coverage, _nextAccount, _stamp, _last, _lastTitle })
        { label.Dock = DockStyle.Fill; label.AutoSize = false; label.AutoEllipsis = true; label.TextAlign = ContentAlignment.MiddleLeft; }
        Controls.Add(layout);
    }
    private static TableLayoutPanel Column(Label title, Control value, Label detail, int inset)
    {
        var column = AccountUiTheme.Stack(3); column.Padding = new Padding(inset, 4, 10, 0);
        title.Dock = DockStyle.Fill; title.AutoSize = false; title.TextAlign = ContentAlignment.MiddleLeft;
        column.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); column.RowStyles.Add(new RowStyle(SizeType.Absolute, 68));
        column.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        column.Controls.Add(title, 0, 0); column.Controls.Add(value, 0, 1); column.Controls.Add(detail, 0, 2);
        return column;
    }
    internal void Display(CombinedUsageSnapshot snapshot, bool busy, int completed = 0, bool querying = false)
    {
        var now = DateTimeOffset.Now; var complete = snapshot.IsComplete(now); var count = snapshot.ConfirmedCount(now);
        _total.Text = complete ? $"{snapshot.ConfirmedRemaining(now)}%" : "—";
        _totalLine.ColumnStyles[0].Width = TextRenderer.MeasureText(_total.Text, _total.Font).Width;
        _layout.Padding = new Padding(0, 0, 0, (int)(26 * DeviceDpi / 96f));
        _capacity.Text = snapshot.Accounts.Count > 0 ? $"/ {snapshot.Accounts.Count * 100}%" : "";
        _total.ForeColor = complete ? AccountUiTheme.Accent : AccountUiTheme.Muted;
        _coverage.Text = complete ? UiText.F($"{snapshot.Accounts.Count}개 계정 · 계정당 100% 기준") : !snapshot.IsBatch ? (snapshot.CompletedAt is null ? UiText.T("저장된 값 · 전체 갱신 필요") : UiText.T("개별 확인값 · 전체 갱신 필요")) : count > 0
            ? UiText.F($"확인된 잔여 {snapshot.ConfirmedRemaining(now)}% · {count}/{snapshot.Accounts.Count}개") : UiText.T("전체 합계 확인 필요");
        _coverage.ForeColor = complete ? AccountUiTheme.Muted : AccountUiTheme.Warning;
        var dated = snapshot.ByReset().Where(row => row.IsCurrent(now)).ToArray(); var next = dated.FirstOrDefault();
        _next.Text = next?.Account.Usage?.ResetsAt is { } reset ? TimeUntil(reset, now) : UiText.T("확인 필요");
        _nextAccount.Text = next is null ? UiText.T("계정별 확인 상태를 살펴보세요.") : $"{next.Account.Label} · {next.Account.Usage!.ResetsAt!.Value.ToLocalTime():MM.dd HH:mm}";
        var uncertain = snapshot.ByReset().FirstOrDefault(row => !row.IsCurrent(now) &&
            (row.Account.Usage?.ResetsAt is null || next is null || row.Account.Usage.ResetsAt <= next.Account.Usage!.ResetsAt));
        if (uncertain is not null)
        {
            _next.Text = UiText.T("확인 필요");
            _nextAccount.Text = uncertain.Account.Usage?.ResetsAt is { } previous
                ? UiText.F($"{uncertain.Account.Label} · 이전 일정 {previous.ToLocalTime():MM/dd HH:mm}") : UiText.F($"{uncertain.Account.Label} · 초기화 시각 미확인");
        }
        var latest = dated.LastOrDefault()?.Account.Usage?.ResetsAt;
        _lastTitle.Text = complete ? UiText.T("가장 늦은 초기화") : UiText.T("확인된 계정 중 마지막");
        _last.Text = latest is { } last ? $"{last.ToLocalTime():MM.dd HH:mm}" : "—";
        _stamp.Text = querying ? UiText.F($"전체 조회 중 · {completed}/{snapshot.Accounts.Count}개 확인") : snapshot.MembershipChanged ? UiText.T("계정 구성 변경 · 전체 갱신 필요")
            : snapshot.WasCanceled ? UiText.T("전체 조회 취소 · 다시 갱신해 주세요.") : snapshot.Interrupted ? UiText.T("전체 조회 중단 · 확인 필요")
            : !snapshot.IsBatch && count > 0 ? UiText.T("개별 확인 · 전체 합계 갱신 필요")
            : snapshot.CompletedAt is { } checkedAt ? CheckedAtText(checkedAt, now, complete) : UiText.T("저장된 값 · 전체 갱신 필요");
    }
    private static string CheckedAtText(DateTimeOffset checkedAt, DateTimeOffset now, bool complete)
    {
        var day = checkedAt.LocalDateTime.Date == now.LocalDateTime.Date ? UiText.T("오늘") : checkedAt.ToLocalTime().ToString("MM.dd");
        return complete ? UiText.F($"{day} {checkedAt.ToLocalTime():HH:mm}에 전체 확인") : UiText.F($"{day} {checkedAt.ToLocalTime():HH:mm}에 일부 확인");
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
