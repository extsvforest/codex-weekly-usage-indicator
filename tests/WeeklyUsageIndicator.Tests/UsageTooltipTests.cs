using WeeklyUsageIndicator;

internal static class UsageTooltipTests
{
    internal static Task RunAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var a = new SavedCodexAccount("a", "메인", "private-identity", true, new(37, now.AddDays(1), 10080, "codex"), now);
        var b = a with { Id = "b", Label = "서브", IsActive = false, Usage = a.Usage! with { UsedPercent = 19, ResetsAt = now.AddDays(3) } };
        var set = new CombinedUsageSnapshot(); set.Initialize(new[] { a, b }, true);
        set.SetResult("a", a, CombinedReadState.Success); set.SetResult("b", b, CombinedReadState.Success); set.CompletedAt = now;
        string Tip() => UsageIndicatorForm.BuildTooltipText(a.Usage, null, null, null, false, set, a.Label);
        var text = Tip();
        Check(text.Contains("주간 잔여 144% / 200%") && text.Contains("전체 확인") && text.Contains("메인 [사용 중]") && text.Contains("서브"), "sum, account names, active marker and observation date are present");
        Check(!text.Contains("private-identity") && !text.Contains("CLAUDE"), "identity hints and disabled provider are absent");
        set.Reconcile(new[] { a with { Usage = a.Usage! with { UsedPercent = 99 } }, b });
        Check(Tip().Contains("144% / 200%"), "hover uses frozen batch values, never a fresh sum of active polling");
        set.SetResult("a", a, CombinedReadState.Failed, "failed");
        Check(Tip().Contains("합계 갱신 필요") && Tip().Contains("이전 63% · 조회 실패") && Tip().Contains("81% · 1/2개"), "failure retains previous value and withholds total");
        set.RecordIndividual(a with { ObservedAt = now.AddSeconds(1) });
        Check(!set.IsComplete(now) && set.Accounts.Single(r => r.Account.Id == "b").State == CombinedReadState.Success &&
            Tip().Contains("개별 확인값") && !Tip().Contains("확인된 잔여"), "individual recovery preserves other observations without certifying a mixed total");
        set.SetResult("a", a with { Usage = a.Usage! with { ResetsAt = now.AddSeconds(-1) } }, CombinedReadState.Success);
        Check(Tip().Contains("메인 [사용 중]  갱신 필요"), "an expired account never appears as currently available");
        var cached = new CombinedUsageSnapshot(); cached.Initialize(new[] { a, b }, false);
        var cachedText = UsageIndicatorForm.BuildTooltipText(null, null, null, null, false, cached);
        Check(cachedText.Contains("저장된 값") && !cachedText.Contains("주간 잔여 144%"), "startup cache is not certified as a complete batch");
        return Task.CompletedTask;
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException("Tooltip: " + message); }
}
