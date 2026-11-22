namespace WeeklyUsageIndicator;

/// <param name="Saved">The value kept from an earlier run, given when nothing was read since and the row shows it.</param>
internal sealed record ClaudeSummaryRow(ClaudeProfile Profile, ClaudeUsageResult? Usage, Exception? Failure, ClaudeSavedUsage? Saved = null);

/// <summary>
/// The overall weekly balance of every registered Claude account, counted once per verified identity and 100% per
/// account, like the Codex total. A usable weekly value counts, read in this session or kept from an earlier one, as
/// the 2026-11-21 design adds old values in; a failure or a missing login leaves the account out, reported as partial
/// coverage. The Fable window is a separate limit and is never added in.
/// </summary>
internal sealed record ClaudeCombinedUsage(int Accounts, int Confirmed, int Remaining, DateTimeOffset? OldestObservation,
    ClaudeSummaryRow? Next, ClaudeSummaryRow? Uncertain, DateTimeOffset? LatestReset)
{
    internal bool Complete => Accounts > 0 && Confirmed == Accounts;

    /// <summary>A usable reading of this session: what a recommendation of the next account rests on.</summary>
    internal static bool IsConfirmed(ClaudeSummaryRow row, DateTimeOffset now) => row.Failure is null
        && row.Usage is { IsStale: false } usage && ClaudeDisplayMetrics.IsUsable(usage.Snapshot.Weekly, now);

    /// <summary>What the total takes: a usable weekly value, read now or kept from before, whose latest read did not fail.</summary>
    internal static bool Counts(ClaudeSummaryRow row, DateTimeOffset now) => row.Failure is null
        && row.Usage is not { IsStale: true } && ClaudeDisplayMetrics.IsUsable(Weekly(row), now);

    internal static ClaudeUsageWindow? Weekly(ClaudeSummaryRow row) => (row.Usage?.Snapshot ?? row.Saved?.Snapshot)?.Weekly;
    private static DateTimeOffset? Observed(ClaudeSummaryRow row) => row.Usage?.LastUpdatedAt ?? row.Saved?.ObservedAt;

    internal static ClaudeCombinedUsage From(IReadOnlyList<ClaudeSummaryRow> rows, DateTimeOffset now)
    {
        // Two profiles signed in to the same subscription are one account: keep its best observation.
        var accounts = rows.GroupBy(row => row.Profile.Identity?.Key ?? "profile:" + row.Profile.Id, StringComparer.Ordinal)
            .Select(group => group.OrderByDescending(row => Counts(row, now))
                .ThenByDescending(row => Observed(row) ?? DateTimeOffset.MinValue).First()).ToArray();
        var counted = accounts.Where(row => Counts(row, now)).ToArray();
        DateTimeOffset? Reset(ClaudeSummaryRow row) => Weekly(row)?.ResetsAt;
        var dated = counted.Where(row => Reset(row) is not null).OrderBy(row => Reset(row)).ToArray();
        var next = dated.FirstOrDefault();
        // An account left out whose earlier schedule may come first; the page names it when nothing counts.
        var uncertain = accounts.Where(row => !Counts(row, now)).OrderBy(row => Reset(row) ?? DateTimeOffset.MaxValue)
            .FirstOrDefault(row => Reset(row) is not { } previous || next is null || previous <= Reset(next));
        return new ClaudeCombinedUsage(accounts.Length, counted.Length,
            counted.Sum(row => UsagePercentage.Remaining(Weekly(row)!.UsedPercent) ?? 0),
            counted.Length == 0 ? null : counted.Min(row => Observed(row)), next, uncertain,
            dated.LastOrDefault() is { } last ? Reset(last) : null);
    }
}

/// <summary>The Claude total: readings of this session and values kept from an earlier one add up; a failure never does.</summary>
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
        DateTimeOffset? Reset(ClaudeSummaryRow row) => ClaudeCombinedUsage.Weekly(row)?.ResetsAt;
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
        var latest = rows.Where(row => ClaudeCombinedUsage.Counts(row, now) && Reset(row) is not null).OrderBy(row => Reset(row)).LastOrDefault();
        Latest.Text = latest is not null && Reset(latest) is { } last
            ? UiText.F($"가장 늦은 초기화 {AccountUsageText.ResetOn(last, now)} · {latest.Profile.Label}") : "";
        LatestObservation = rows.Where(row => row.Usage is not null).Select(row => row.Usage!.LastUpdatedAt)
            .Concat(savedAt ?? Array.Empty<DateTimeOffset>()).Select(at => (DateTimeOffset?)at).DefaultIfEmpty().Max();
        CheckedAtText = progress is { } running ? UiText.F($"조회 중 · {running.Done}/{running.Total}")
            : LatestObservation is { } at ? AccountUsageText.CheckedOn(at, now) : UiText.T("아직 조회 안 함");
        Changed();
    }
}
