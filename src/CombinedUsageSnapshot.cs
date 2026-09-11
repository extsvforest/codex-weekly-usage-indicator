namespace WeeklyUsageIndicator;

internal enum CombinedReadState { Saved, Waiting, Success, Failed, Canceled }
internal sealed record CombinedAccountUsage(SavedCodexAccount Account, CombinedReadState State, string? Error = null)
{
    internal bool HasWeeklyValue => Account.Usage is { UsedPercent: >= 0 and <= 100, WindowDurationMinutes: 10080 };
    internal bool IsCurrent(DateTimeOffset now) => State == CombinedReadState.Success && HasWeeklyValue &&
        Account.ObservedAt is not null && Account.Usage!.ResetsAt is { } reset && reset > now;
    internal string Status(DateTimeOffset now)
    {
        if (State == CombinedReadState.Failed) return Error ?? "조회 실패";
        if (State == CombinedReadState.Canceled) return "조회 취소 · 이전 값";
        if (State == CombinedReadState.Waiting) return "이번 전체 조회에서 미확인";
        if (!HasWeeklyValue) return "주간 사용량 미확인";
        if (Account.Usage!.ResetsAt is null) return "초기화 시각 미확인";
        if (Account.Usage.ResetsAt <= now) return "초기화 시점 지남 · 갱신 필요";
        return State == CombinedReadState.Saved ? "저장된 값 · 전체 갱신 필요" : "확인 완료";
    }
}

// A single in-memory observation set, never a usage history or a live sum of independently polling accounts.
internal sealed class CombinedUsageSnapshot
{
    internal IReadOnlyList<CombinedAccountUsage> Accounts { get; private set; } = Array.Empty<CombinedAccountUsage>();
    internal DateTimeOffset? CompletedAt { get; set; }
    internal bool WasCanceled { get; set; }
    internal bool Interrupted { get; set; }
    internal bool MembershipChanged { get; private set; }
    internal bool IsBatch { get; private set; }

    internal void Initialize(IReadOnlyList<SavedCodexAccount> accounts, bool batch)
    {
        Accounts = accounts.Select(a => new CombinedAccountUsage(a, batch ? CombinedReadState.Waiting : CombinedReadState.Saved)).ToArray();
        CompletedAt = null; WasCanceled = false; Interrupted = false; MembershipChanged = false; IsBatch = batch;
    }

    internal void SetResult(string id, SavedCodexAccount account, CombinedReadState state, string? error = null) =>
        Accounts = Accounts.Select(row => row.Account.Id == id ? new CombinedAccountUsage(account, state, error) : row).ToArray();

    internal void RecordIndividual(SavedCodexAccount account)
    {
        // An explicit individual read changes the visible list. Require a new batch to certify the total.
        SetResult(account.Id, account, CombinedReadState.Success);
        IsBatch = false; CompletedAt = DateTimeOffset.UtcNow; WasCanceled = false; Interrupted = false;
    }

    internal bool Reconcile(IReadOnlyList<SavedCodexAccount> current)
    {
        var ids = Accounts.Select(row => row.Account.Id).ToHashSet(StringComparer.Ordinal);
        var changed = !ids.SetEquals(current.Select(a => a.Id));
        if (changed) MembershipChanged = true;
        var old = Accounts.ToDictionary(row => row.Account.Id, StringComparer.Ordinal);
        // Names and active markers may change locally; percentages and check times stay frozen.
        Accounts = current.Select(a => old.TryGetValue(a.Id, out var row)
            ? row with { Account = row.Account with { Label = a.Label, IsActive = a.IsActive } }
            : new CombinedAccountUsage(a, CombinedReadState.Saved)).ToArray();
        return !changed;
    }

    internal bool IsComplete(DateTimeOffset now) => Accounts.Count > 0 && IsBatch && CompletedAt is not null &&
        !WasCanceled && !Interrupted && !MembershipChanged && Accounts.All(row => row.IsCurrent(now));
    internal int ConfirmedCount(DateTimeOffset now) => Accounts.Count(row => row.IsCurrent(now));
    internal int ConfirmedRemaining(DateTimeOffset now) => Accounts.Where(row => row.IsCurrent(now)).Sum(row => 100 - row.Account.Usage!.UsedPercent);
    internal IReadOnlyList<CombinedAccountUsage> ByReset() => Accounts.OrderBy(row => row.Account.Usage?.ResetsAt ?? DateTimeOffset.MaxValue)
        .ThenBy(row => row.Account.Label, StringComparer.CurrentCulture).ToArray();
}
