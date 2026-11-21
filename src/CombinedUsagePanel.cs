namespace WeeklyUsageIndicator;

/// <summary>
/// The two-column total under the top bar (design .summary-strip): the weekly remaining of the confirmed accounts,
/// one tick per account, then the next reset with the latest one under it. Only confirmed readings add up; the note
/// says how many of the accounts that is. Codex and Claude fill it from their own data.
/// </summary>
internal abstract class AccountSummaryStrip : UserControl
{
    // CSS: margin-top 14, padding 12 0 14, a 1px rule above and below; a label row (11.5px × 1.4), 4px, then the
    // value row (26px × 1.08 on the left; 21px × 1.15 and a 5px-gap meta line of 11.5px × 1.4 on the right).
    internal const float LogicalHeight = 1 + 12 + 16.1f + 4 + 24.15f + 5 + 16.1f + 14 + 1;
    protected readonly AccountText Title = new(11.5f, AccountWeight.SemiBold, AccountUiTheme.Ink3) { Name = "UsageSummaryTitle" };
    protected readonly AccountText Note = new(11.5f, AccountWeight.SemiBold, AccountUiTheme.WarnInk);
    protected readonly AccountMetricLabel Total = new(26, 16, AccountWeight.Bold, AccountWeight.SemiBold) { UnitColor = AccountUiTheme.Ink2 };
    protected readonly AccountText Capacity = new(13.5f, AccountWeight.Regular, AccountUiTheme.Ink4);
    protected readonly AccountMeter Bar = new() { Name = "SummaryBar" };
    protected readonly AccountText NextTitle = new(11.5f, AccountWeight.SemiBold, AccountUiTheme.Ink3) { Name = "NextResetTitle" };
    protected readonly AccountMetricLabel Next = new(21, 14, AccountWeight.Bold, AccountWeight.SemiBold)
    { UnitColor = AccountUiTheme.Ink2, UnitTrail = 3, WordFont = AccountUiTheme.Type(18, AccountWeight.SemiBold), WordColor = AccountUiTheme.Ink2 };
    protected readonly AccountText NextSub = new(12, AccountWeight.Regular, AccountUiTheme.Ink3);
    protected readonly AccountText Latest = new(11.5f, AccountWeight.Regular, AccountUiTheme.Ink3);
    internal string CheckedAtText { get; private protected set; } = "";
    /// <summary>The newest reading on this tab, for the top bar.</summary>
    internal DateTimeOffset? LatestObservation { get; private protected set; }
    internal (int Done, int Total)? Progress { get; private protected set; }
    internal event EventHandler? SummaryChanged;

    protected AccountSummaryStrip(AccountProviderColors provider)
    {
        AutoScaleMode = AutoScaleMode.None; BackColor = Color.Transparent; DoubleBuffered = true;
        Bar.FillColor = provider.Main; Bar.TrackColor = AccountUiTheme.SubtleBackground; Bar.Shares = 0;
        Title.Text = UiText.T("주간 잔여 합계"); NextTitle.Text = UiText.T("다음 초기화");
        Total.EmptyFont = AccountUiTheme.Type(22); Capacity.Name = Name + "Capacity";
        Controls.AddRange(new Control[] { Title, Note, Total, Capacity, Bar, NextTitle, Next, NextSub, Latest });
    }

    internal int PreferredHeight => (int)Math.Round(LogicalHeight * DeviceDpi / 96f);

    /// <param name="confirmed">Accounts whose reading counts now; <paramref name="remaining"/> is their sum.</param>
    private protected void ShowTotal(int accounts, int confirmed, int remaining, (int Done, int Total)? progress)
    {
        Progress = progress;
        if (accounts == 0) { Total.Text = "—"; Capacity.Text = ""; Bar.Remaining = null; Bar.Shares = 0; Note.Text = ""; return; }
        Total.Text = confirmed > 0 ? $"{remaining}%" : "—";
        Capacity.Text = confirmed > 0 ? $"/ {confirmed * 100}%" : "";
        Bar.Capacity = Math.Max(100, confirmed * 100); Bar.Remaining = confirmed > 0 ? remaining : null; Bar.Shares = confirmed;
        if (progress is { } running)
            Note.SetText(UiText.F($"· 조회 중 {running.Done}/{running.Total}"), AccountUiTheme.Ink3, AccountUiTheme.Type(11.5f));
        else if (confirmed < accounts)
            Note.SetText(UiText.F($"· {accounts}개 중 {confirmed}개 확인"), AccountUiTheme.WarnInk, AccountUiTheme.Type(11.5f, AccountWeight.SemiBold));
        else Note.SetText("");
        Note.AccessibleDescription = Note.Text;
    }

    private protected void Changed()
    {
        AccessibleDescription = CheckedAtText;
        PerformLayout(); Invalidate(true);
        SummaryChanged?.Invoke(this, EventArgs.Empty);
    }

    protected override void OnLayout(LayoutEventArgs e)
    {
        base.OnLayout(e);
        var s = DeviceDpi / 96f; var width = ClientSize.Width;
        if (width <= 0) return;
        var split = width * 1.05f / 2.05f;
        float row1 = 13 * s, row1Height = 16.1f * s, row2 = row1 + row1Height + 4 * s;
        // Left: title, note; the total, its capacity and the bar.
        var leftEnd = split - 24 * s;
        var x = AccountUiTheme.PlaceLine(Title, 2 * s, row1, row1Height, Title.ContentWidth);
        AccountUiTheme.PlaceLine(Note, x + 8 * s, row1, row1Height, Math.Min(Note.ContentWidth, Math.Max(0, leftEnd - x - 8 * s)));
        var heroHeight = 26 * 1.08f * s;
        var totalWidth = Math.Min(Total.ContentWidth + 1, (int)(leftEnd - 2 * s));
        AccountUiTheme.PlaceLine(Total, 2 * s, row2, heroHeight, totalWidth);
        var after = Total.Right;
        if (Capacity.Text.Length > 0)
        {
            AccountUiTheme.PlaceOnBaseline(Capacity, Total, after + 6 * s, Math.Min(Capacity.ContentWidth + 1, Math.Max(0, leftEnd - after - 6 * s)));
            after = Capacity.Right;
        }
        var barLeft = after + 16 * s; var barWidth = Math.Min(172 * s, leftEnd - barLeft);
        Bar.Visible = barWidth >= 30 * s;
        AccountUiTheme.Place(Bar, (int)Math.Round(barLeft), (int)Math.Round(row2 + (heroHeight - 5 * s) / 2), (int)Math.Max(0, barWidth), (int)Math.Round(5 * s));
        // Right: title; time to the next reset and whose it is, on one baseline; the latest reset.
        var right = split + 24 * s; var rightEnd = width - 2 * s;
        AccountUiTheme.PlaceLine(NextTitle, right, row1, row1Height, Math.Min(NextTitle.ContentWidth, rightEnd - right));
        var timeHeight = 21 * 1.15f * s;
        AccountUiTheme.PlaceLine(Next, right, row2, timeHeight, Math.Min(Next.ContentWidth + 1, rightEnd - right));
        var sub = Next.Right + 10 * s;
        AccountUiTheme.PlaceOnBaseline(NextSub, Next, sub, Math.Max(0, Math.Min(NextSub.ContentWidth + 1, rightEnd - sub)));
        NextSub.Visible = NextSub.Width > 20 * s && NextSub.Text.Length > 0;
        AccountUiTheme.PlaceLine(Latest, right, row2 + timeHeight + 5 * s, 16.1f * s, Math.Min(Latest.ContentWidth + 1, rightEnd - right));
    }

    protected override void OnPaintBackground(PaintEventArgs e)
    {
        base.OnPaintBackground(e);
        var s = DeviceDpi / 96f; var line = Math.Max(1, (int)Math.Round(s));
        using var subtle = new SolidBrush(AccountUiTheme.RuleSubtle);
        using var strong = new SolidBrush(AccountUiTheme.RuleStrong);
        e.Graphics.FillRectangle(subtle, 0, 0, Width, line);
        e.Graphics.FillRectangle(strong, 0, Height - line, Width, line);
        var split = (int)Math.Round(Width * 1.05f / 2.05f);
        e.Graphics.FillRectangle(subtle, split, (int)Math.Round(15 * s), line, Math.Max(0, Height - (int)Math.Round(32 * s)));
    }
}

/// <summary>The Codex total: only accounts read in this session count; saved values from earlier runs never add up.</summary>
internal sealed class CombinedUsagePanel : AccountSummaryStrip
{
    internal CombinedUsagePanel() : base(AccountUiTheme.Codex)
    {
        Name = "CombinedUsagePanel";
        Total.Name = "CombinedRemainingLabel"; Note.Name = "CombinedCoverageLabel"; Capacity.Name = "CombinedCapacityLabel";
        Next.Name = "NextResetLabel"; NextSub.Name = "NextResetAccountLabel"; Latest.Name = "LatestResetLabel";
    }

    internal void Display(CombinedUsageSnapshot snapshot, bool busy, int completed = 0, bool querying = false)
    {
        var now = AccountUsageText.Now;
        var accounts = snapshot.Accounts.Count; var confirmed = snapshot.ConfirmedCount(now);
        ShowTotal(accounts, confirmed, snapshot.ConfirmedRemaining(now), querying ? (completed, accounts) : null);
        var dated = snapshot.ByReset().Where(row => row.IsCurrent(now)).ToArray(); var next = dated.FirstOrDefault();
        // The design names the next reset among confirmed accounts; the note already says how many those are.
        var uncertain = snapshot.ByReset().FirstOrDefault(row => !row.IsCurrent(now));
        if (accounts == 0) { Next.Text = "—"; NextSub.Text = ""; }
        else if (next?.Account.Usage?.ResetsAt is { } reset)
        {
            Next.Text = TimeUntil(reset, now); NextSub.Text = AccountUsageText.Refills(next.Account.Label, reset, now);
        }
        else if (uncertain is not null)
        {
            Next.Text = UiText.T("확인 필요");
            NextSub.Text = uncertain.Account.Usage?.ResetsAt is { } previous
                ? UiText.F($"{uncertain.Account.Label} · 이전 일정 {AccountUsageText.ResetOn(previous, now)}")
                : UiText.F($"{uncertain.Account.Label} · 초기화 시각 미확인");
        }
        else { Next.Text = UiText.T("초기화 미확인"); NextSub.Text = ""; }
        Latest.Text = dated.LastOrDefault() is { Account.Usage.ResetsAt: { } latest } last
            ? UiText.F($"가장 늦은 초기화 {AccountUsageText.ResetOn(latest, now)} · {last.Account.Label}") : "";
        LatestObservation = snapshot.Accounts.Select(row => row.Account.ObservedAt).Where(at => at is not null).DefaultIfEmpty().Max();
        CheckedAtText = querying ? UiText.F($"조회 중 · {completed}/{accounts}")
            : LatestObservation is { } at ? AccountUsageText.CheckedOn(at, now) : UiText.T("아직 조회 안 함");
        Changed();
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
