namespace WeeklyUsageIndicator;

// The selected profile only: Claude windows are never summed across profiles.
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

    internal void Display(ClaudeProfile? profile, ClaudeUsageResult? usage, Exception? failure)
    {
        using var layout = AccountUiTheme.DeferLayout(_layout);
        var now = DateTimeOffset.Now; var window = usage?.Snapshot.Weekly;
        var loginRequired = failure is ClaudeLoginRequiredException;
        var usable = profile is not null && failure is not (ClaudeLoginRequiredException or ClaudeIdentityChangedException) && ClaudeDisplayMetrics.IsUsable(window, now);
        var remaining = usable ? UsagePercentage.Remaining(window!.UsedPercent) : null;
        _remaining.Text = remaining is { } value ? $"{value}%" : "—";
        _remaining.ForeColor = remaining is null || usage?.IsStale == true || failure is not null ? AccountUiTheme.Muted : AccountUiTheme.Text;
        _capacity.Text = profile is not null ? "/ 100%" : "";
        _bar.Remaining = remaining;
        _bar.FillColor = AccountUiTheme.Accent;
        _profile.Text = profile is null ? UiText.T("Claude 계정 연결") : loginRequired
            ? UiText.T("CLI 로그인 필요") + " · " + profile.Label : UiText.F($"{profile.Label} 기준");
        _profile.ForeColor = loginRequired ? AccountUiTheme.Warning : AccountUiTheme.Muted;
        _reset.Text = window?.ResetsAt is { } reset ? CombinedUsagePanel.TimeUntil(reset, now) : UiText.T("초기화 미확인");
        _resetDate.Text = window?.ResetsAt is { } date ? date.ToLocalTime().ToString("MM.dd HH:mm") : "—";
        _checkedAt.Text = usage?.IsStale == true ? UiText.T("갱신 필요") : "";
        _checkedAt.ForeColor = AccountUiTheme.Warning;
        var details = usage?.IsStale == true && usage.RetryAfter is { } retry
            ? UiText.F($"이전 확인값 · 다음 확인 가능 {retry.ToLocalTime():HH:mm}") : _checkedAt.Text;
        _details.SetToolTip(_checkedAt, details); _checkedAt.AccessibleDescription = details;
        CheckedAtText = loginRequired ? UiText.T("CLI 로그인 필요")
            : usage is null ? failure is null ? UiText.T("사용량 미확인") : UiText.T("조회 실패")
            : usage.IsStale || failure is not null ? AccountUsageText.PreviousValue(usage.LastUpdatedAt, now)
            : AccountUsageText.LastUpdated(usage.LastUpdatedAt, now);
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
