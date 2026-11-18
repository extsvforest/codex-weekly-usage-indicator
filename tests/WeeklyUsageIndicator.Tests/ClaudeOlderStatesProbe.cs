using System.Diagnostics;
using WeeklyUsageIndicator;

/// <summary>
/// Read-only: for each mapped account, the records of its list that another record continues, and what the session plan
/// makes of each — an older state (with the newest state it gives way to), or one that stays and why. Shows how long
/// dating each hand-over took, since that reads whole transcripts. Nothing is written and the widget is not asked.
/// <c>--claude-older-states [text a title must contain]</c>
/// </summary>
internal static class ClaudeOlderStatesProbe
{
    internal static void Run(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var only = args.SkipWhile(arg => arg != "--claude-older-states").Skip(1).FirstOrDefault();
        var sources = ClaudeSessionSync.Sources.Default();
        var (layout, _) = ClaudeSessionLayout.Load(sources.Ledger);
        if (layout is null) { Console.WriteLine("no layout to take the accounts from"); return; }
        var took = new Dictionary<string, TimeSpan>(StringComparer.Ordinal);
        var lineage = new ClaudeSessionSync.Lineage(null, cli => ClaudeSessionSync.Transcript(sources.ProjectsRoot, cli), (older, next) =>
        {
            var watch = Stopwatch.StartNew();
            var handOver = ClaudeSessionSync.HandOver(sources.ProjectsRoot, older, next);
            took[older + ">" + next] = watch.Elapsed;
            return handOver;
        });
        string Short(string? id) => id is { Length: >= 8 } ? id[..8] : id ?? "-";
        string At(DateTimeOffset? at) => at?.ToLocalTime().ToString("MM-dd HH:mm:ss") ?? "-";
        string Span(string? cli) => cli is null ? "-" : ClaudeSessionSync.Transcript(sources.ProjectsRoot, cli) is { } span
            ? $"created {At(span.Created)} written {At(span.Written)} last turn {At(span.LastEntry)}{(span.Dated ? "" : " undated")}" : "not read";
        foreach (var (account, known) in layout.Accounts.OrderBy(pair => pair.Value.Label, StringComparer.Ordinal))
        {
            var directory = Directory.EnumerateDirectories(sources.SessionsRoot, "*", SearchOption.AllDirectories)
                .FirstOrDefault(path => path.Replace(Path.DirectorySeparatorChar, '/').EndsWith(account, StringComparison.OrdinalIgnoreCase));
            if (directory is null) { Console.WriteLine($"== {known.Label}: no list"); continue; }
            var records = ClaudeSessionSync.ReadRecords(directory, null, out var whole, out var archived);
            var live = records.Select(record => record.SessionId).ToHashSet(StringComparer.Ordinal);
            var staying = new List<(ClaudeSessionSync.Record Record, string Why)>();
            var watch = Stopwatch.StartNew();
            var older = ClaudeSessionSync.OlderStates(records, lineage, archived, (record, why) => staying.Add((record, why)));
            Console.WriteLine($"== {known.Label}: {records.Count} live{(whole ? "" : " (not read whole)")}, {archived.Count} archived; "
                + $"older states {older.Count(pair => live.Contains(pair.Key))} live + {older.Count(pair => !live.Contains(pair.Key))} archived; "
                + $"continued but staying {staying.Count(item => live.Contains(item.Record.SessionId))} live + "
                + $"{staying.Count(item => !live.Contains(item.Record.SessionId))} archived; {watch.Elapsed.TotalSeconds:F1} s");
            var all = records.Concat(archived).ToDictionary(record => record.SessionId, StringComparer.Ordinal);
            foreach (var (id, state) in older.Where(pair => live.Contains(pair.Key)))
            {
                if (only is not null && all[id].Title?.Contains(only, StringComparison.OrdinalIgnoreCase) != true) continue;
                Console.WriteLine($"  older  {all[id].Title} [{Short(all[id].Cli)}] -> {state.Newest.Title} [{Short(state.Newest.Cli)}]; "
                    + $"last turn {At(state.LastWritten)}, went on {At(state.MovedOn)}");
            }
            foreach (var (record, why) in staying)
            {
                if (only is not null && record.Title?.Contains(only, StringComparison.OrdinalIgnoreCase) != true) continue;
                Console.WriteLine($"  stays  {(live.Contains(record.SessionId) ? "" : "(archived) ")}{record.Title} [{Short(record.Cli)}]: {why}");
                Console.WriteLine($"         own {Span(record.Cli)}");
                foreach (var other in records.Where(other => !other.Branch && other.SessionId != record.SessionId && other.Prior.Contains(record.Cli!)))
                    Console.WriteLine($"         continued by {other.Title} [{string.Join(" ", other.Prior.Append(other.Cli).Select(Short))}] {Span(other.Cli)}");
            }
        }
        Console.WriteLine("hand-overs dated, the slowest first:");
        foreach (var (pair, time) in took.OrderByDescending(item => item.Value).Take(8))
            Console.WriteLine($"  {time.TotalSeconds,6:F2} s  {string.Join(" > ", pair.Split('>').Select(Short))}");
    }
}
