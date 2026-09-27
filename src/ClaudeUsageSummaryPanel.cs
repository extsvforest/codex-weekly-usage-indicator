namespace WeeklyUsageIndicator;

// The selected profile only: Claude windows are never summed across profiles.
internal sealed class ClaudeUsageSummaryPanel : UserControl
{
    internal const int PreferredLogicalHeight = 172;
    private readonly AccountSummaryLayout _layout = new() { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0) };
    private readonly AccountMetricLabel _remaining = new(40) { Name = "ClaudeSelectedWeeklyRemainingLabel", Dock = DockStyle.Fill };
    private readonly AccountMetricLabel _reset = new(30) { Name = "ClaudeSelectedResetLabel", Dock = DockStyle.Fill };
    private readonly Label _profile = AccountUiTheme.Label("", color: AccountUiTheme.Muted);
    private readonly Label _resetDate = AccountUiTheme.Label("", color: AccountUiTheme.Muted);
    private readonly Label _checkedAt = AccountUiTheme.Label("", 9, color: AccountUiTheme.Subtle);
    private readonly AccountUsageBar _bar = new() { Anchor = AnchorStyles.Left | AnchorStyles.Right, FillColor = AccountUiTheme.Apricot, Margin = new Padding(0) };
    private readonly Button _refresh = AccountUiTheme.Button("ClaudeRefreshSelectedButton", UiText.T("갱신"));
    private readonly TableLayoutPanel _left, _right, _resetHeading;
    private readonly ToolTip _details = new() { InitialDelay = 500, ReshowDelay = 100, AutoPopDelay = 10000 };
    internal event EventHandler? RefreshRequested;

    internal ClaudeUsageSummaryPanel()
    {
        Name = "ClaudeUsageSummaryPanel"; BackColor = Color.Transparent;
        AutoScaleMode = AutoScaleMode.Dpi; AutoScaleDimensions = new SizeF(96, 96);
        _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50)); _layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 50));
        _layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var title = AccountUiTheme.Label(UiText.T("선택한 계정 · 전체 주간 잔여"), color: AccountUiTheme.Muted);
        title.Dock = DockStyle.Fill; title.AutoSize = false; title.TextAlign = ContentAlignment.MiddleLeft;
        _profile.Name = "ClaudeSelectedProfileLabel"; _resetDate.Name = "ClaudeSelectedResetDateLabel"; _checkedAt.Name = "ClaudeSelectedCheckedAtLabel";
        foreach (var label in new[] { _profile, _resetDate, _checkedAt })
        { label.Dock = DockStyle.Fill; label.AutoSize = false; label.AutoEllipsis = true; label.TextAlign = ContentAlignment.MiddleLeft; }
        _left = Column(title, _remaining, _profile, _bar);
        _resetHeading = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, Margin = new Padding(0), BackColor = Color.Transparent };
        _resetHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100)); _resetHeading.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 72));
        _resetHeading.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var resetTitle = AccountUiTheme.Label(UiText.T("초기화"), color: AccountUiTheme.Muted);
        resetTitle.Dock = DockStyle.Fill; resetTitle.AutoSize = false; resetTitle.TextAlign = ContentAlignment.MiddleLeft;
        _refresh.AutoSize = false; _refresh.Anchor = AnchorStyles.Right; _refresh.Margin = new Padding(0);
        _refresh.AccessibleName = UiText.T("이 계정 사용량 조회");
        _refresh.Click += (_, _) => RefreshRequested?.Invoke(this, EventArgs.Empty);
        _resetHeading.Controls.Add(resetTitle, 0, 0); _resetHeading.Controls.Add(_refresh, 1, 0);
        _right = Column(_resetHeading, _reset, _resetDate, _checkedAt);
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

    internal void Display(ClaudeProfile? profile, ClaudeUsageResult? usage, bool busy)
    {
        using var layout = AccountUiTheme.DeferLayout(_layout);
        var now = DateTimeOffset.Now; var window = usage?.Snapshot.Weekly;
        var usable = profile is not null && ClaudeDisplayMetrics.IsUsable(window, now);
        var remaining = usable ? UsagePercentage.Remaining(window!.UsedPercent) : null;
        _remaining.Text = remaining is { } value ? $"{value}%" : "—";
        _remaining.ForeColor = remaining is null || usage?.IsStale == true ? AccountUiTheme.Muted : AccountUiTheme.Text;
        _bar.Remaining = remaining;
        _bar.FillColor = usage?.IsStale == true ? AccountUiTheme.Subtle : AccountUiTheme.Apricot;
        _profile.Text = profile is null ? UiText.T("Claude 계정 연결") : UiText.F($"다음 실행 · {profile.Label}");
        _reset.Text = window?.ResetsAt is { } reset ? CombinedUsagePanel.TimeUntil(reset, now) : UiText.T("초기화 미확인");
        _resetDate.Text = window?.ResetsAt is { } date ? date.ToLocalTime().ToString("MM.dd HH:mm")
            : window is null ? UiText.T("사용량 미확인") : "—";
        _checkedAt.Text = usage is null ? "" : usage.IsStale
            ? UiText.F($"이전 확인 {usage.LastUpdatedAt.ToLocalTime():MM.dd HH:mm}")
            : UiText.F($"{usage.LastUpdatedAt.ToLocalTime().ToString("MM.dd HH:mm")} 확인");
        _checkedAt.ForeColor = usage?.IsStale == true ? AccountUiTheme.Warning : AccountUiTheme.Subtle;
        var details = usage?.IsStale == true && usage.RetryAfter is { } retry
            ? UiText.F($"이전 확인값 · 다음 확인 가능 {retry.ToLocalTime():HH:mm}") : _checkedAt.Text;
        _details.SetToolTip(_checkedAt, details); _checkedAt.AccessibleDescription = details;
        _refresh.Enabled = !busy && profile is not null;
        _details.SetToolTip(_refresh, UiText.T("이 계정 사용량 조회"));
        ApplyGeometry();
    }

    private void ApplyGeometry()
    {
        var scale = DeviceDpi / 96f;
        int Pixels(int logical) => (int)Math.Round(logical * scale);
        _layout.Padding = new Padding(Math.Max(1, Pixels(1)), Math.Max(1, Pixels(1)), Math.Max(1, Pixels(1)), Pixels(4));
        foreach (var column in new[] { _left, _right })
        {
            column.Padding = new Padding(Pixels(24), Pixels(14), Pixels(24), Pixels(16));
            column.RowStyles[0].Height = Pixels(24); column.RowStyles[1].Height = Pixels(66); column.RowStyles[2].Height = Pixels(27);
        }
        _resetHeading.ColumnStyles[1].Width = Pixels(72);
        _refresh.Size = _refresh.MinimumSize = new Size(Pixels(68), Pixels(24));
        _refresh.Padding = new Padding(Pixels(6), 0, Pixels(6), 0);
        _bar.Height = Pixels(6); _bar.MinimumSize = new Size(Pixels(30), Pixels(6));
    }

    protected override void OnLoad(EventArgs e) { base.OnLoad(e); ApplyGeometry(); }
    protected override void OnDpiChangedAfterParent(EventArgs e) { base.OnDpiChangedAfterParent(e); ApplyGeometry(); }
    protected override void Dispose(bool disposing) { if (disposing) _details.Dispose(); base.Dispose(disposing); }
}
