namespace WeeklyUsageIndicator;

internal enum ClaudeDisplayMetric
{
    OverallWeekly,
    FableWeekly
}

internal static class ClaudeDisplayMetrics
{
    internal static string SettingsValue(this ClaudeDisplayMetric metric) => metric switch
    {
        ClaudeDisplayMetric.OverallWeekly => "overall-weekly",
        ClaudeDisplayMetric.FableWeekly => "fable-weekly",
        _ => throw new ArgumentOutOfRangeException(nameof(metric))
    };

    internal static ClaudeDisplayMetric FromSettings(string? value, bool existingSettings) => value switch
    {
        "overall-weekly" => ClaudeDisplayMetric.OverallWeekly,
        "fable-weekly" => ClaudeDisplayMetric.FableWeekly,
        _ => existingSettings ? ClaudeDisplayMetric.FableWeekly : ClaudeDisplayMetric.OverallWeekly
    };

    internal static ClaudeUsageWindow? SelectWindow(this ClaudeDisplayMetric metric, ClaudeUsageSnapshot? snapshot) =>
        metric == ClaudeDisplayMetric.OverallWeekly ? snapshot?.Weekly : snapshot?.Fable;

    internal static double? SelectUsedPercent(this ClaudeDisplayMetric metric, ClaudeUsageSnapshot? snapshot, DateTimeOffset now)
    {
        var window = metric.SelectWindow(snapshot);
        return IsUsable(window, now) ? window!.UsedPercent : null;
    }

    internal static bool IsUsable(ClaudeUsageWindow? window, DateTimeOffset now) =>
        window is not null && UsagePercentage.Remaining(window.UsedPercent) is not null &&
        (window.ResetsAt is not { } reset || reset > now);
}

internal static class UsagePercentage
{
    // Round the remaining amount once, identically in the widget, tooltip and copy text.
    internal static int? Remaining(double? used) => used is { } value && double.IsFinite(value) && value is >= 0d and <= 100d
        ? (int)Math.Round(100d - value, MidpointRounding.AwayFromZero)
        : null;
}
