using System.Diagnostics;
using WeeklyUsageIndicator;

/// <summary>
/// Read-only: runs the passes the widget's background service makes while the app runs (observe the app, follow its log,
/// plan the fill; with <c>--checkpoint</c> also the read a checkpoint makes, on a layout kept in memory only) against the
/// real lists and transcripts, and prints after each step what it allocated and what stays. Nothing is written outside
/// a temporary mapping cache, and the widget is not asked anything.
/// <c>--claude-shelf-memory-probe [--iterations N] [--interval seconds] [--checkpoint]</c>
/// </summary>
internal static class ClaudeShelfMemoryProbe
{
    internal static void Run(string[] args)
    {
        var iterations = args.SkipWhile(arg => arg != "--iterations").Skip(1).FirstOrDefault() is { } count
            && int.TryParse(count, out var parsed) ? parsed : 10;
        // The widget plans once a minute while the app runs; transcripts written meanwhile are read again.
        var interval = args.SkipWhile(arg => arg != "--interval").Skip(1).FirstOrDefault() is { } seconds
            && int.TryParse(seconds, out var wait) ? TimeSpan.FromSeconds(wait) : TimeSpan.Zero;
        var checkpoint = args.Contains("--checkpoint");
        var scratch = Path.Combine(Path.GetTempPath(), "gfs-agent", "claude-shelf-memory-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var accounts = ClaudeShelfAccounts.Resolve(new ClaudeProfileStore().ListProfiles(), scratch);
            Console.WriteLine($"accounts: {accounts.Count}");
            // One shelf and one log watcher for the whole run, as the widget keeps them.
            var shelf = new ClaudeSessionShelf(acquireLease: () => new MemoryStream());
            var log = new ClaudeDesktopLogWatcher();
            var sources = ClaudeSessionSync.Sources.For(shelf.Paths);
            var (layout, _) = ClaudeSessionLayout.Load(sources.Ledger);
            Report("start", 0, TimeSpan.Zero);
            for (var iteration = 1; iteration <= iterations; iteration++)
            {
                if (iteration > 1) Thread.Sleep(interval);
                var runtime = Step($"{iteration} observe", () =>
                {
                    var observed = ClaudeDesktopProbe.Observe();
                    if (ClaudeDesktopProbe.FindPackage() is { } package) ClaudeDesktopProbe.Contract(package.AsarPath);
                    return observed;
                });
                var app = Step($"{iteration} log", () => log.Update(runtime.HostStartedAt, accounts));
                var plan = Step($"{iteration} plan", () => shelf.Plan(accounts));
                // A fill that may write follows the copies' titles after planning (read-only up to the writes).
                var titles = Step($"{iteration} titles", () => shelf.TitleCandidates(
                    ClaudeShelfLedger.Load(shelf.Paths.LedgerDirectory, shelf.Paths.SessionsRoot), plan,
                    accounts.Select(account => account.Key).ToHashSet(StringComparer.Ordinal)).Count());
                if (checkpoint && layout is not null && app.Key is { } shown)
                    Step($"{iteration} checkpoint", () => ClaudeSessionSync.Checkpoint(layout, sources, shown, DateTimeOffset.Now));
                if (iteration == 1)
                    Console.WriteLine($"  app {runtime.State} {app.Status}; actions {plan.Actions.Count}, advances {plan.Advances.Count}, "
                        + $"retirements {plan.Retirements.Count}, held {plan.Held.Count}, titles to follow {titles}");
            }
            GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
            Report("after a compacting collection", 0, TimeSpan.Zero);
        }
        finally { Directory.Delete(scratch, recursive: true); }
    }

    private static T Step<T>(string name, Func<T> step)
    {
        var allocated = GC.GetTotalAllocatedBytes(precise: true);
        var watch = Stopwatch.StartNew();
        var result = step();
        Report(name, GC.GetTotalAllocatedBytes(precise: true) - allocated, watch.Elapsed);
        return result;
    }

    private static void Report(string name, long allocated, TimeSpan took)
    {
        const double MB = 1024 * 1024;
        var info = GC.GetGCMemoryInfo(GCKind.Any);
        var generations = info.GenerationInfo.ToArray();
        string Gen(int index) => index < generations.Length ? $"{generations[index].SizeAfterBytes / MB:F0}" : "-";
        using var process = Process.GetCurrentProcess();
        Console.WriteLine($"{name,-24} alloc {allocated / MB,8:F1} MB {took.TotalSeconds,6:F1} s | managed {GC.GetTotalMemory(false) / MB,7:F0}"
            + $" heap {info.HeapSizeBytes / MB,7:F0} frag {info.FragmentedBytes / MB,6:F0} committed {info.TotalCommittedBytes / MB,7:F0}"
            + $" gen0/1/2/loh/poh {Gen(0)}/{Gen(1)}/{Gen(2)}/{Gen(3)}/{Gen(4)} | private {process.PrivateMemorySize64 / MB,7:F0} MB");
    }
}
