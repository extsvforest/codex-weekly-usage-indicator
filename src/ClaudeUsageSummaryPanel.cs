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

/// <summary>The Claude total: only readings of this session count; a saved value or a failure never adds up.</summary>
internal sealed class ClaudeUsageSummaryPanel : AccountSummaryStrip
{
    internal ClaudeUsageSummaryPanel() : base(AccountUiTheme.Claude)
    {
        Name = "ClaudeUsageSummaryPanel";
        Total.Name = "ClaudeSelectedWeeklyRemainingLabel"; Note.Name = "ClaudeSelectedProfileLabel"; Capacity.Name = "ClaudeSelectedCapacityLabel";
        Next.Name = "ClaudeSelectedResetLabel"; NextSub.Name = "ClaudeSelectedResetDateLabel"; Latest.Name = "ClaudeSelectedCheckedAtLabel";
    }

    /// <param name="progress">Accounts checked so far while every account is being read.</param>
    /// <param name="savedAt">When the saved values shown in the rows were read; they date the top bar, never the total.</param>
    internal void Display(IReadOnlyList<ClaudeSummaryRow> rows, (int Done, int Total)? progress = null, IEnumerable<DateTimeOffset>? savedAt = null)
    {
        var now = AccountUsageText.Now; var total = ClaudeCombinedUsage.From(rows, now);
        ShowTotal(total.Accounts, total.Confirmed, total.Remaining, progress);
        DateTimeOffset? Reset(ClaudeSummaryRow row) => row.Usage?.Snapshot.Weekly?.ResetsAt;
        if (total.Accounts == 0) { Next.Text = "—"; NextSub.Text = ""; }
        else if (total.Next is { } next && Reset(next) is { } reset)
        {
            Next.Text = CombinedUsagePanel.TimeUntil(reset, now); NextSub.Text = AccountUsageText.Refills(next.Profile.Label, reset, now);
        }
        else if (total.Uncertain is { } uncertain)
        {
            Next.Text = UiText.T("확인 필요");
            NextSub.Text = Reset(uncertain) is { } previous
                ? UiText.F($"{uncertain.Profile.Label} · 이전 일정 {AccountUsageText.ResetOn(previous, now)}")
                : UiText.F($"{uncertain.Profile.Label} · 초기화 시각 미확인");
        }
        else { Next.Text = UiText.T("초기화 미확인"); NextSub.Text = ""; }
        var latest = rows.Where(row => ClaudeCombinedUsage.IsConfirmed(row, now) && Reset(row) is not null).OrderBy(row => Reset(row)).LastOrDefault();
        Latest.Text = latest is not null && Reset(latest) is { } last
            ? UiText.F($"가장 늦은 초기화 {AccountUsageText.ResetOn(last, now)} · {latest.Profile.Label}") : "";
        LatestObservation = rows.Where(row => row.Usage is not null).Select(row => row.Usage!.LastUpdatedAt)
            .Concat(savedAt ?? Array.Empty<DateTimeOffset>()).Select(at => (DateTimeOffset?)at).DefaultIfEmpty().Max();
        CheckedAtText = progress is { } running ? UiText.F($"조회 중 · {running.Done}/{running.Total}")
            : LatestObservation is { } at ? AccountUsageText.CheckedOn(at, now) : UiText.T("아직 조회 안 함");
        Changed();
    }
}
