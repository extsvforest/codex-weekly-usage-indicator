using WeeklyUsageIndicator;

internal static class CombinedUsageTests
{
    internal static Task RunAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var accounts = new[] { Account("a", 37, now.AddDays(2), now), Account("b", 19, now.AddDays(4), now), Account("c", 76, now.AddDays(6), now) };
        var set = new CombinedUsageSnapshot();
        set.Initialize(accounts, batch: false);
        Check(!set.IsComplete(now), "cached observations never count as this batch's successful results");
        set.Initialize(accounts, batch: true);
        foreach (var account in accounts) set.SetResult(account.Id, account, CombinedReadState.Success);
        set.CompletedAt = now;
        Check(set.IsComplete(now) && set.ConfirmedRemaining(now) == 168 && set.ConfirmedCount(now) == 3, "sum is additive, including above 100 percent");
        Check(set.ByReset().First().Account.Id == "a" && set.ByReset().Last().Account.Id == "c", "schedule orders by actual reset time");
        Check(!set.IsComplete(now.AddDays(2)) && set.ConfirmedRemaining(now.AddDays(2)) == 105, "exact reset boundary invalidates total without inventing 100 percent");
        set.SetResult("b", accounts[1], CombinedReadState.Failed, "synthetic failure");
        Check(!set.IsComplete(now) && set.ConfirmedRemaining(now) == 87 && set.Accounts[1].Account.Usage == accounts[1].Usage,
            "failed result stays separate from retained previous observation");
        set.SetResult("b", accounts[1] with { Usage = accounts[1].Usage! with { ResetsAt = null } }, CombinedReadState.Success);
        Check(!set.IsComplete(now) && set.ConfirmedCount(now) == 2, "unknown reset cannot certify a current total");
        set.SetResult("b", accounts[1] with { Usage = accounts[1].Usage! with { WindowDurationMinutes = 300 } }, CombinedReadState.Success);
        Check(!set.IsComplete(now), "5-hour-only source cannot silently become a weekly allowance");
        set.SetResult("b", accounts[1], CombinedReadState.Success);
        set.WasCanceled = true; Check(!set.IsComplete(now), "cancellation cannot publish a complete batch");
        set.WasCanceled = false; set.Interrupted = true; Check(!set.IsComplete(now), "final identity or recovery interruption invalidates even all successful rows");
        set.Interrupted = false;
        set.Reconcile(accounts.Select(a => a with { Label = "Renamed " + a.Id, Usage = a.Usage! with { UsedPercent = 99 } }).ToArray());
        Check(set.ConfirmedRemaining(now) == 168 && set.Accounts.All(a => a.Account.Label.StartsWith("Renamed")), "local metadata refresh keeps batch values frozen");
        Check(!set.Reconcile(accounts.Take(2).ToArray()) && !set.IsComplete(now), "removing a member invalidates coverage and denominator");
        set.Reconcile(accounts); Check(!set.IsComplete(now), "restoring a member does not silently recertify an old batch");
        set.Initialize(Array.Empty<SavedCodexAccount>(), batch: true); set.CompletedAt = now;
        Check(!set.IsComplete(now) && set.ConfirmedRemaining(now) == 0, "zero accounts is empty, not a valid zero balance");
        set.Initialize(accounts.Take(1).ToArray(), batch: true); set.SetResult("a", accounts[0] with { Usage = accounts[0].Usage! with { UsedPercent = 100 } }, CombinedReadState.Success); set.CompletedAt = now;
        Check(set.IsComplete(now) && set.ConfirmedRemaining(now) == 0, "one genuinely exhausted account is a valid zero balance");
        return Task.CompletedTask;
    }

    private static SavedCodexAccount Account(string id, int used, DateTimeOffset reset, DateTimeOffset observed) =>
        new(id, "Account " + id, "synthetic", id == "a", new(used, reset, 10080, "codex"), observed);
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Combined usage: " + message); }
}
