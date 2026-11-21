using System.Globalization;

namespace WeeklyUsageIndicator;

// Observation and reset times read the same on every provider's tab. The clock can be replaced so that design
// captures show the fixture's moment; the app reads the system clock.
internal static class AccountUsageText
{
    internal static Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;
    internal static DateTimeOffset Now => Clock();
    /// <summary>A reading this old, or one whose reset has passed, is shown as old.</summary>
    internal static readonly TimeSpan StaleAfter = TimeSpan.FromHours(6);

    internal static string LastUpdated(DateTimeOffset at, DateTimeOffset now) => UiText.F($"마지막 갱신 · {DateAndTime(at, now)}");
    internal static string PartiallyUpdated(DateTimeOffset at, DateTimeOffset now) => UiText.F($"일부 갱신 · {DateAndTime(at, now)}");
    internal static string PreviousValue(DateTimeOffset at, DateTimeOffset now) => UiText.F($"이전 값 · {DateAndTime(at, now)}");

    internal static string DateAndTime(DateTimeOffset at, DateTimeOffset now)
    {
        var local = at.ToLocalTime();
        var day = local.Date == now.ToLocalTime().Date ? UiText.T("오늘") : local.ToString("MM.dd");
        return $"{day} {local:HH:mm}";
    }

    internal static string Time(DateTimeOffset at) => at.ToLocalTime().ToString("HH:mm", CultureInfo.InvariantCulture);
    private static int DaysBefore(DateTimeOffset at, DateTimeOffset now) => (now.ToLocalTime().Date - at.ToLocalTime().Date).Days;

    internal static bool IsStale(DateTimeOffset observedAt, DateTimeOffset? reset, DateTimeOffset now) =>
        now - observedAt >= StaleAfter || reset is { } passed && passed <= now;

    /// <summary>A row's reading time: "13:05 조회" today, "어제 23:10" yesterday, "3일 전 조회" before that.</summary>
    internal static string Checked(DateTimeOffset at, DateTimeOffset now) => DaysBefore(at, now) switch
    {
        <= 0 => UiText.F($"{Time(at)} 조회"),
        1 => UiText.F($"어제 {Time(at)}"),
        var days => UiText.F($"{days}일 전 조회")
    };

    /// <summary>The top bar's reading time, "오늘 13:05 조회".</summary>
    internal static string CheckedOn(DateTimeOffset at, DateTimeOffset now) => DaysBefore(at, now) switch
    {
        <= 0 => UiText.F($"오늘 {Time(at)} 조회"),
        1 => UiText.F($"어제 {Time(at)} 조회"),
        _ => UiText.F($"{at.ToLocalTime().ToString("MM.dd", CultureInfo.InvariantCulture)} {Time(at)} 조회")
    };

    /// <summary>"11.21(토) 17:59"; with <paramref name="today"/>, today's date reads "오늘 17:59".</summary>
    internal static string ResetOn(DateTimeOffset at, DateTimeOffset now, bool weekday = true, bool today = false)
    {
        var local = at.ToLocalTime();
        if (today && DaysBefore(at, now) == 0) return UiText.F($"오늘 {Time(at)}");
        var date = local.ToString("MM.dd", CultureInfo.InvariantCulture);
        if (!weekday) return $"{date} {Time(at)}";
        var day = UiText.Culture.DateTimeFormat.AbbreviatedDayNames[(int)local.DayOfWeek];
        return UiText.IsEnglish ? $"{date} ({day}) {Time(at)}" : $"{date}({day}) {Time(at)}";
    }

    /// <summary>"메인 Claude · 오늘 17:59 다시 참": when the next account fills up again.</summary>
    internal static string Refills(string label, DateTimeOffset reset, DateTimeOffset now) =>
        UiText.F($"{label} · {ResetOn(reset, now, today: true)} 다시 참");

    /// <summary>The Korean topic particle (은/는) after a name, read aloud; nothing in English.</summary>
    internal static string Topic(string name)
    {
        var trimmed = name.TrimEnd();
        if (UiText.IsEnglish || trimmed.Length == 0) return "";
        var last = trimmed[^1];
        if (last is >= '가' and <= '힣') return (last - '가') % 28 == 0 ? "는" : "은";
        if (char.IsDigit(last)) return "013678".Contains(last) ? "은" : "는";
        // Latin letters are read by their names; l, m, n and r end in a consonant.
        return "LMNRlmnr".Contains(last) ? "은" : "는";
    }
}
