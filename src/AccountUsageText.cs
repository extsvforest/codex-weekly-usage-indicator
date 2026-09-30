namespace WeeklyUsageIndicator;

// Both providers use the same language for observation times. Read scope is
// described separately below the weekly balance and on the refresh action.
internal static class AccountUsageText
{
    internal static string LastUpdated(DateTimeOffset at, DateTimeOffset now) => UiText.F($"마지막 갱신 · {DateAndTime(at, now)}");
    internal static string PartiallyUpdated(DateTimeOffset at, DateTimeOffset now) => UiText.F($"일부 갱신 · {DateAndTime(at, now)}");
    internal static string PreviousValue(DateTimeOffset at, DateTimeOffset now) => UiText.F($"이전 값 · {DateAndTime(at, now)}");

    internal static string DateAndTime(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        var day = local.Date == now.ToLocalTime().Date ? UiText.T("오늘") : local.ToString("MM.dd");
        return $"{day} {local:HH:mm}";
    }
}
