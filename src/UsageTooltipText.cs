using System.Text;

namespace WeeklyUsageIndicator;

internal static class UsageTooltipText
{
    internal static string Build(UsageSnapshot? codex, ClaudeUsageResult? claude, string? codexError,
        string? claudeError, bool showClaude, CombinedUsageSnapshot? combined = null, string? activeLabel = null)
    {
        var now = DateTimeOffset.Now; var text = new StringBuilder();
        text.AppendLine("CODEX · " + (activeLabel is null ? "현재 계정" : Name(activeLabel)));
        text.AppendLine(Window("주간", codex?.UsedPercent, codex?.ResetsAt, now));
        if (codex?.ShortWindow is { } shortWindow) text.AppendLine(Window("5시간", shortWindow.UsedPercent, shortWindow.ResetsAt, now));
        if (codexError is not null) text.AppendLine("확인 필요: " + codexError);
        if (combined is { Accounts.Count: > 0 })
        {
            text.AppendLine();
            text.AppendLine(combined.IsComplete(now)
                ? $"전체 계정 · 주간 잔여 {combined.ConfirmedRemaining(now)}% / {combined.Accounts.Count * 100}%"
                : "전체 계정 · 합계 갱신 필요");
            text.AppendLine(!combined.IsBatch && combined.CompletedAt is not null ? "개별 확인값 · 전체 합계 갱신 필요" : combined.CompletedAt is { } at
                ? $"{(combined.IsComplete(now) ? "전체 확인" : "마지막 조회")} {at.ToLocalTime():MM/dd HH:mm} 기준 · 계정당 100%"
                : "저장된 값 · 계정 관리에서 전체 갱신해 주세요.");
            if (combined.IsBatch && !combined.IsComplete(now) && combined.ConfirmedCount(now) > 0)
                text.AppendLine($"확인된 잔여 {combined.ConfirmedRemaining(now)}% · {combined.ConfirmedCount(now)}/{combined.Accounts.Count}개");
            foreach (var row in combined.ByReset().Take(5))
            {
                var a = row.Account;
                var remaining = a.Usage?.ResetsAt <= now ? "갱신 필요" : row.HasWeeklyValue
                    ? $"{(row.State == CombinedReadState.Success ? "" : "이전 ")}{100 - a.Usage!.UsedPercent}%" : "미확인";
                var state = row.State == CombinedReadState.Failed ? " · 조회 실패" : row.State == CombinedReadState.Canceled ? " · 조회 취소" : "";
                text.AppendLine($"{Name(a.Label)}{(a.IsActive ? " [사용 중]" : "")}  {remaining}{state}");
                text.AppendLine($"  초기화 {Reset(a.Usage?.ResetsAt, now)}" +
                    (row.State != CombinedReadState.Success && a.ObservedAt is { } savedAt ? $" · {savedAt.ToLocalTime():MM/dd HH:mm} 값" : ""));
            }
            if (combined.Accounts.Count > 5) text.AppendLine($"외 {combined.Accounts.Count - 5}개 · 계정 관리에서 모두 보기");
        }
        if (showClaude)
        {
            text.AppendLine(); text.AppendLine("CLAUDE");
            var snapshot = claudeError is null ? claude?.Snapshot : null;
            text.AppendLine(Window("Fable", snapshot?.Fable?.UsedPercent, snapshot?.Fable?.ResetsAt, now));
            text.AppendLine(Window("5시간", snapshot?.FiveHour?.UsedPercent, snapshot?.FiveHour?.ResetsAt, now));
            text.AppendLine(Window("전체 주간", snapshot?.Weekly?.UsedPercent, snapshot?.Weekly?.ResetsAt, now));
            if (claudeError is not null) text.AppendLine("확인 필요: " + claudeError);
            else if (claude is { IsStale: true })
            {
                text.AppendLine($"업데이트 지연 · 마지막 성공 {claude.LastUpdatedAt.ToLocalTime():MM/dd HH:mm}");
                if (claude.RetryAfter is { } retry) text.AppendLine($"다음 시도 {Reset(retry, now)}");
            }
        }
        text.AppendLine(); text.Append("우클릭 → Codex 계정 관리 · 전체 갱신 / 계정 전환");
        return text.ToString();
    }
    private static string Window(string label, double? used, DateTimeOffset? reset, DateTimeOffset now)
    {
        if (used is null) return label + ": 정보 없음";
        var value = reset <= now ? "갱신 필요" : $"{100 - (int)Math.Round(Math.Clamp(used.Value, 0, 100), MidpointRounding.AwayFromZero)}% 남음";
        return $"{label}: {value} · 초기화: {Reset(reset, now)}";
    }
    private static string Reset(DateTimeOffset? reset, DateTimeOffset now) => reset is { } at
        ? $"{at.ToLocalTime():MM/dd HH:mm} ({CombinedUsagePanel.TimeUntil(at, now)})" : "정보 없음";
    private static string Name(string text) => text.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ');
}
