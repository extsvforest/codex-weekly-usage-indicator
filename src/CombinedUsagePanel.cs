namespace WeeklyUsageIndicator;

internal sealed class CombinedUsagePanel : UserControl
{
    private readonly Label _total = AccountUiTheme.Label("—", 34, true);
    private readonly Label _coverage = AccountUiTheme.Label("", color: AccountUiTheme.Muted);
    private readonly Label _next = AccountUiTheme.Label("—", 18, true);
    private readonly Label _nextAccount = AccountUiTheme.Label("", color: AccountUiTheme.Muted);
    private readonly Label _stamp = AccountUiTheme.Label("", color: AccountUiTheme.Muted);
    private readonly Label _last = AccountUiTheme.Label("", color: AccountUiTheme.Muted);
    internal CombinedUsagePanel()
    {
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        Name = "CombinedUsagePanel"; BackColor = AccountUiTheme.Surface;
        _total.Name = "CombinedRemainingLabel"; _coverage.Name = "CombinedCoverageLabel";
        _next.Name = "NextResetLabel"; _stamp.Name = "CombinedCheckedAtLabel"; _last.Name = "LatestResetLabel";
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 4,
            Padding = new Padding(24, 18, 24, 16), Margin = new Padding(0) };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        foreach (var height in new[] { 24, 60, 28, 26 }) layout.RowStyles.Add(new RowStyle(SizeType.Percent, height));
        layout.Controls.Add(AccountUiTheme.Label("전체 주간 잔여", color: AccountUiTheme.Muted), 0, 0);
        layout.Controls.Add(AccountUiTheme.Label("다음 초기화", color: AccountUiTheme.Muted), 1, 0);
        layout.Controls.Add(_total, 0, 1); layout.Controls.Add(_next, 1, 1);
        layout.Controls.Add(_coverage, 0, 2); layout.Controls.Add(_nextAccount, 1, 2);
        layout.Controls.Add(_stamp, 0, 3); layout.Controls.Add(_last, 1, 3);
        foreach (var label in new[] { _total, _next, _coverage, _nextAccount, _stamp, _last })
        { label.Dock = DockStyle.Fill; label.AutoSize = false; label.AutoEllipsis = true; label.TextAlign = ContentAlignment.MiddleLeft; }
        Controls.Add(layout);
    }
    internal void Display(CombinedUsageSnapshot snapshot, bool busy, int completed = 0, bool querying = false)
    {
        var now = DateTimeOffset.Now; var complete = snapshot.IsComplete(now); var count = snapshot.ConfirmedCount(now);
        _total.Text = complete ? $"{snapshot.ConfirmedRemaining(now)}%" : "—";
        _total.ForeColor = complete ? AccountUiTheme.Accent : AccountUiTheme.Muted;
        _coverage.Text = complete ? $"총 {snapshot.Accounts.Count * 100}% 중 · 계정당 100% 기준" : !snapshot.IsBatch ? "개별 확인값 · 전체 갱신 필요" : count > 0
            ? $"확인된 잔여 {snapshot.ConfirmedRemaining(now)}% · {count}/{snapshot.Accounts.Count}개" : "전체 합계 확인 필요";
        _coverage.ForeColor = complete ? AccountUiTheme.Muted : AccountUiTheme.Warning;
        var dated = snapshot.ByReset().Where(row => row.IsCurrent(now)).ToArray(); var next = dated.FirstOrDefault();
        _next.Text = next?.Account.Usage?.ResetsAt is { } reset ? TimeUntil(reset, now) : "확인 필요";
        _nextAccount.Text = next is null ? "계정별 확인 상태를 살펴보세요." : $"{next.Account.Label} · 잔여 {100 - next.Account.Usage!.UsedPercent}% · {next.Account.Usage.ResetsAt!.Value.ToLocalTime():MM/dd HH:mm}";
        var uncertain = snapshot.ByReset().FirstOrDefault(row => !row.IsCurrent(now) &&
            (row.Account.Usage?.ResetsAt is null || next is null || row.Account.Usage.ResetsAt <= next.Account.Usage!.ResetsAt));
        if (uncertain is not null)
        {
            _next.Text = "확인 필요";
            _nextAccount.Text = uncertain.Account.Label + (uncertain.Account.Usage?.ResetsAt is { } previous
                ? $" · 이전 일정 {previous.ToLocalTime():MM/dd HH:mm}" : " · 초기화 시각 미확인");
        }
        var latest = dated.LastOrDefault()?.Account.Usage?.ResetsAt;
        _last.Text = latest is { } last ? $"{(complete ? "가장 늦은 초기화" : "확인된 계정 중 마지막")}  {last.ToLocalTime():MM/dd HH:mm}" : "초기화 시각은 계정마다 다릅니다.";
        _stamp.Text = querying ? $"전체 조회 중 · {completed}/{snapshot.Accounts.Count}개 확인" : snapshot.MembershipChanged ? "계정 구성 변경 · 전체 갱신 필요"
            : snapshot.WasCanceled ? "전체 조회 취소 · 다시 갱신해 주세요." : snapshot.Interrupted ? "전체 조회 중단 · 확인 필요"
            : !snapshot.IsBatch && count > 0 ? "개별 확인 · 전체 합계 갱신 필요"
            : snapshot.CompletedAt is { } checkedAt ? $"{(complete ? "전체 확인" : "일부 확인")}  {checkedAt.ToLocalTime():MM/dd HH:mm} 기준" : "저장된 값 · 전체 갱신 필요";
    }
    internal static string TimeUntil(DateTimeOffset reset, DateTimeOffset now)
    {
        var span = reset - now;
        if (span <= TimeSpan.Zero) return "갱신 필요";
        if (span.TotalDays >= 1) return $"{(int)span.TotalDays}일 {span.Hours}시간 뒤";
        if (span.TotalHours >= 1) return $"{(int)span.TotalHours}시간 {span.Minutes}분 뒤";
        return $"{Math.Max(1, (int)Math.Ceiling(span.TotalMinutes))}분 뒤";
    }
}
