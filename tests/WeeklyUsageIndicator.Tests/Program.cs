using System.Text.Json;
using WeeklyUsageIndicator;

var tests = new (string Name, Func<Task> Run)[]
{
    ("Codex app-server lifecycle isolation", AppServerLifecycleTests.RunAsync),
    ("Codex account vault and crash recovery", AccountStoreTests.RunAsync),
    ("isolated Codex login runtime", AccountRuntimeTests.RunAsync),
    ("optional synthetic account UI capture", AccountUiSmoke.RunAsync),
    ("supervisor retries abnormal exits but respects normal Quit", TestSupervisorAsync),
    ("official Claude /usage output is parsed", TestObservedUsageOutputAsync),
    ("usage without reset times remains valid through client and tooltip", TestUsageWithoutResetsAsync),
    ("optional limits fail independently", TestIndependentLimitsAsync),
    ("unknown reset format preserves verified percentages", TestUnknownResetFormatAsync),
    ("missing or invalid Fable never becomes zero usage", TestInvalidFableAsync),
    ("decimal percentages and year rollover are parsed", TestUsageTextVariantsAsync),
    ("model turns and costs are rejected", TestNoModelTurnGuardAsync),
    ("authentication and malformed output are hard failures", TestSourceHardFailuresAsync),
    ("Claude process invocation is shell-free and session-free", TestProcessArgumentsAsync),
    ("ten-minute cache prevents duplicate commands", TestFreshCacheAsync),
    ("transient command failures keep cached usage and back off", TestTransientRecoveryAsync),
    ("persisted usage protects a transient cold start", TestPersistedColdStartAsync),
    ("hard failures clear cached usage", TestHardFailuresClearCacheAsync),
    ("caller cancellation preserves accepted cached usage", TestCallerCancellationPreservesCacheAsync),
    ("expired persisted usage is rejected", TestExpiredPersistedCacheAsync),
    ("file cache stores only the sanitized snapshot", TestFileCacheAsync),
    ("temporary errors expose the next attempt", TestRetryErrorTextAsync),
    ("stale tooltip reports last success and next retry", TestStaleTooltipAsync)
};

foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine($"PASS: {test.Name}");
}

return;

static Task TestSupervisorAsync()
{
    var runs = 0;
    var waits = 0;
    var result = WidgetSupervisor.RunLoop(() => ++runs < 3 ? -1 : 0, interval =>
    {
        Assert(interval == TimeSpan.FromMinutes(1), "recovery must not tight-loop");
        waits++;
    });
    Assert(result == 0 && runs == 3 && waits == 2, "nonzero exits retry; normal Quit stops");
    runs = waits = 0;
    result = WidgetSupervisor.RunLoop(() => { runs++; return 0; }, _ => waits++);
    Assert(result == 0 && runs == 1 && waits == 0, "normal Quit must never relaunch");
    runs = waits = 0;
    result = WidgetSupervisor.RunLoop(() =>
    {
        runs++;
        throw new System.ComponentModel.Win32Exception("test start failure");
    }, _ => waits++);
    Assert(result != 0 && runs == 1000 && waits == 999, "failed starts have a bounded retry budget");
    return Task.CompletedTask;
}

static Task TestObservedUsageOutputAsync()
{
    var now = new DateTimeOffset(2026, 9, 1, 4, 25, 0, TimeSpan.Zero);
    var text = """
        You are currently using your subscription to power your Claude Code usage

        Current session: 0% used · resets Sep 1, 6:19pm (Asia/Seoul)
        Current week (all models): 12% used · resets Sep 5, 5:59pm (Asia/Seoul)
        Current week (Fable): 24% used · resets Sep 5, 5:59pm (Asia/Seoul)

        What's contributing to your limits usage?
        """;
    var snapshot = ClaudeCodeUsageSource.ParseEnvelope(UsageEnvelope(text), now);

    Assert(snapshot.FiveHour?.UsedPercent == 0, "the current session percentage should be parsed");
    Assert(snapshot.Weekly?.UsedPercent == 12, "the all-model weekly percentage should be parsed");
    Assert(snapshot.Fable?.UsedPercent == 24, "the Fable weekly percentage should be parsed");
    Assert(snapshot.FiveHour?.ResetsAt == new DateTimeOffset(2026, 9, 1, 18, 19, 0, TimeSpan.FromHours(9)),
        "the reset should preserve the reported time zone");
    return Task.CompletedTask;
}

static async Task TestUsageWithoutResetsAsync()
{
    var now = new DateTimeOffset(2026, 9, 5, 0, 0, 0, TimeSpan.Zero);
    var raw = UsageEnvelope("""
        Current session: 0% used
        Current week (all models): 0% used
        Current week (Fable): 0% used
        """);
    var source = new ClaudeCodeUsageSource(
        new SequenceCommandRunner(new ClaudeCommandResult(0, raw, string.Empty)),
        new ManualTimeProvider(now));
    var store = new MemoryCacheStore();
    using var client = new ClaudeUsageClient(source, new ManualTimeProvider(now), store);
    var result = await client.GetUsageAsync(CancellationToken.None);
    Assert(!result.IsStale && result.Snapshot.Fable?.UsedPercent == 0,
        "the observed reset-free response should produce fresh usage");
    Assert(result.Snapshot.FiveHour?.ResetsAt is null && result.Snapshot.Weekly?.ResetsAt is null &&
        result.Snapshot.Fable?.ResetsAt is null, "absent reset times must remain unknown");
    Assert(store.Entry?.Snapshot == result.Snapshot, "reset-free usage should be persisted");
    var tooltip = UsageIndicatorForm.BuildTooltipText(null, result, null, null, showClaude: true);
    Assert(tooltip.Contains("Fable: 100% 남음", StringComparison.Ordinal), "the panel should have a valid remaining value");
    Assert(tooltip.Contains("초기화: 정보 없음", StringComparison.Ordinal), "missing resets must not be invented");
}

static Task TestIndependentLimitsAsync()
{
    var now = new DateTimeOffset(2026, 9, 1, 4, 0, 0, TimeSpan.Zero);
    foreach (var session in new[] { "", "Current session: unavailable", "Current session: 101% used" })
    {
        var text = session + "\nCurrent week (all models): 20% used\nCurrent week (Fable): 30% used";
        var snapshot = ClaudeCodeUsageSource.ParseEnvelope(UsageEnvelope(text), now);
        Assert(snapshot.FiveHour is null, "missing or invalid session usage should remain unknown");
        Assert(snapshot.Weekly?.UsedPercent == 20 && snapshot.Fable?.UsedPercent == 30,
            "an optional session failure must not discard valid weekly limits");
    }
    var fableOnly = ClaudeCodeUsageSource.ParseUsageText("Current week (Fable): 30% used", now);
    Assert(fableOnly.FiveHour is null && fableOnly.Weekly is null && fableOnly.Fable?.UsedPercent == 30,
        "Fable remains usable when both optional windows are absent");
    return Task.CompletedTask;
}

static Task TestUnknownResetFormatAsync()
{
    var now = new DateTimeOffset(2026, 9, 1, 4, 0, 0, TimeSpan.Zero);
    foreach (var suffix in new[] { " · resets tomorrow", " · resets Sep 5, 6pm (Unknown/Zone)", " · resets not-a-date (Asia/Seoul)" })
    {
        var snapshot = ClaudeCodeUsageSource.ParseUsageText("Current week (Fable): 30% used" + suffix, now);
        Assert(snapshot.Fable?.UsedPercent == 30 && snapshot.Fable.ResetsAt is null,
            "an unknown reset format should not erase a valid percentage or invent a date");
    }
    return Task.CompletedTask;
}

static Task TestInvalidFableAsync()
{
    var now = DateTimeOffset.UtcNow;
    foreach (var fable in new[] { "", "Current week (Fable): unavailable", "Current week (Fable): 101% used", "Current week (Fable): -1% used", "Current week (Fable): NaN% used", "Current week (Fable): 30% used garbage" })
    {
        AssertThrows<InvalidDataException>(() => ClaudeCodeUsageSource.ParseUsageText(
            "Current session: 10% used\nCurrent week (all models): 20% used\n" + fable, now),
            "invalid Fable usage must not become zero usage or another limit");
    }
    return Task.CompletedTask;
}

static Task TestUsageTextVariantsAsync()
{
    var now = new DateTimeOffset(2026, 12, 31, 14, 0, 0, TimeSpan.Zero);
    var text = """
        Current session: 1.5% used · resets Jan 1, 2am (Asia/Seoul)
        Current week (all models): 2.25% used · resets Jan 3, 6:30pm (Asia/Seoul)
        Current week (Fable): 3.75% used · resets Jan 3, 6:30pm (Asia/Seoul)
        """;
    var snapshot = ClaudeCodeUsageSource.ParseUsageText(text, now);

    Assert(snapshot.FiveHour?.UsedPercent == 1.5, "decimal percentages should be accepted");
    Assert(snapshot.FiveHour?.ResetsAt?.Year == 2027, "a January reset after December should roll into the next year");
    return Task.CompletedTask;
}

static Task TestNoModelTurnGuardAsync()
{
    var now = DateTimeOffset.UtcNow;
    AssertThrows<InvalidDataException>(
        () => ClaudeCodeUsageSource.ParseEnvelope(UsageEnvelope(SampleUsageText(), numTurns: 1), now),
        "a model turn must never be accepted as a usage lookup");
    AssertThrows<InvalidDataException>(
        () => ClaudeCodeUsageSource.ParseEnvelope(UsageEnvelope(SampleUsageText(), totalCost: 0.01), now),
        "a paid response must never be accepted as a usage lookup");
    return Task.CompletedTask;
}

static async Task TestSourceHardFailuresAsync()
{
    var clock = new ManualTimeProvider(new DateTimeOffset(2026, 9, 1, 4, 0, 0, TimeSpan.Zero));
    var loginRunner = new SequenceCommandRunner(
        new ClaudeCommandResult(1, string.Empty, "Not logged in. Please login."));
    var loginSource = new ClaudeCodeUsageSource(loginRunner, clock);
    await AssertThrowsAsync<UnauthorizedAccessException>(
        () => loginSource.ReadAsync(CancellationToken.None),
        "a Claude Code login failure should remain a hard failure");

    var malformedSource = new ClaudeCodeUsageSource(
        new SequenceCommandRunner(new ClaudeCommandResult(0, "not-json", string.Empty)),
        clock);
    await AssertThrowsAsync<JsonException>(
        () => malformedSource.ReadAsync(CancellationToken.None),
        "malformed JSON should not revive stale usage");

    var invalidKeySource = new ClaudeCodeUsageSource(
        new SequenceCommandRunner(new ClaudeCommandResult(1, string.Empty, "Invalid API key")),
        clock);
    await AssertThrowsAsync<UnauthorizedAccessException>(
        () => invalidKeySource.ReadAsync(CancellationToken.None),
        "official invalid-key errors should not be treated as transient");
}

static Task TestProcessArgumentsAsync()
{
    var startInfo = ClaudeCodeProcessRunner.CreateStartInfo("claude.exe");
    var arguments = startInfo.ArgumentList.ToArray();
    Assert(!startInfo.UseShellExecute && startInfo.CreateNoWindow, "Claude must run without a shell or visible window");
    Assert(arguments.Contains("/usage"), "the built-in /usage command should be invoked");
    Assert(arguments.Contains("--safe-mode"), "custom hooks, plugins, and MCP servers must not affect the lookup");
    Assert(arguments.Contains("--no-session-persistence"), "the lookup must not leave a resumable session");
    Assert(arguments.Contains("--no-chrome"), "the lookup must not open browser integration");
    Assert(arguments.SequenceEqual([
        "-p", "--safe-mode", "--no-session-persistence", "--no-chrome", "/usage", "--output-format", "json", "--max-turns", "0"]),
        "the command contract should remain exact and reviewable");
    Assert(UsageIndicatorForm.ClaudeRefreshTimeout > ClaudeCodeProcessRunner.CommandTimeout,
        "the UI timeout must leave the process runner time to classify a slow command as transient");
    return Task.CompletedTask;
}

static async Task TestFreshCacheAsync()
{
    var start = new DateTimeOffset(2026, 9, 1, 4, 0, 0, TimeSpan.Zero);
    var clock = new ManualTimeProvider(start);
    var source = new SequenceUsageSource(Snapshot(10, start));
    using var client = new ClaudeUsageClient(source, clock);

    var first = await client.GetUsageAsync(CancellationToken.None);
    clock.Advance(TimeSpan.FromMinutes(9));
    var cached = await client.GetUsageAsync(CancellationToken.None);

    Assert(source.ReadCount == 1, "fresh cache should avoid a second Claude command");
    Assert(!first.IsStale && !cached.IsStale, "fresh cache should not be marked stale");
    Assert(cached.Snapshot.Fable?.UsedPercent == 10, "the cached Fable value should be preserved");
}

static async Task TestTransientRecoveryAsync()
{
    var start = new DateTimeOffset(2026, 9, 1, 4, 0, 0, TimeSpan.Zero);
    var clock = new ManualTimeProvider(start);
    var source = new SequenceUsageSource(
        Snapshot(10, start),
        new IOException("temporarily unavailable"),
        new TimeoutException("slow"),
        new IOException("temporarily unavailable"),
        Snapshot(22, start.AddMinutes(40)));
    using var client = new ClaudeUsageClient(source, clock);

    await client.GetUsageAsync(CancellationToken.None);
    clock.Advance(TimeSpan.FromMinutes(10));
    var first = await client.GetUsageAsync(CancellationToken.None);
    Assert(first.IsStale && first.RetryAfter == clock.GetUtcNow().AddMinutes(5),
        "the first transient failure should keep the cache and wait five minutes");

    var suppressed = await client.GetUsageAsync(CancellationToken.None);
    Assert(suppressed.IsStale && source.ReadCount == 2, "backoff should suppress repeated commands");

    clock.Advance(TimeSpan.FromMinutes(5));
    var second = await client.GetUsageAsync(CancellationToken.None);
    Assert(second.RetryAfter == clock.GetUtcNow().AddMinutes(10), "the second failure should wait ten minutes");

    clock.Advance(TimeSpan.FromMinutes(10));
    var third = await client.GetUsageAsync(CancellationToken.None);
    Assert(third.RetryAfter == clock.GetUtcNow().AddMinutes(15), "the third failure should wait fifteen minutes");

    clock.Advance(TimeSpan.FromMinutes(15));
    var recovered = await client.GetUsageAsync(CancellationToken.None);
    Assert(!recovered.IsStale && recovered.Snapshot.Fable?.UsedPercent == 22,
        "a successful command should replace stale usage");
}

static async Task TestPersistedColdStartAsync()
{
    var start = new DateTimeOffset(2026, 9, 1, 4, 0, 0, TimeSpan.Zero);
    var store = new MemoryCacheStore
    {
        Entry = new ClaudeUsageCacheEntry(Snapshot(30, start), start)
    };
    var source = new SequenceUsageSource(new IOException("offline"));
    using var client = new ClaudeUsageClient(source, new ManualTimeProvider(start), store);

    var stale = await client.GetUsageAsync(CancellationToken.None);
    Assert(source.ReadCount == 1, "persisted usage must not suppress the first live command");
    Assert(stale.IsStale && stale.Snapshot.Fable?.UsedPercent == 30,
        "a transient cold start should keep the persisted value visible");
}

static async Task TestHardFailuresClearCacheAsync()
{
    var start = new DateTimeOffset(2026, 9, 1, 4, 0, 0, TimeSpan.Zero);
    var clock = new ManualTimeProvider(start);
    var store = new MemoryCacheStore();
    var source = new SequenceUsageSource(
        Snapshot(10, start),
        new InvalidDataException("schema changed"),
        new IOException("offline"));
    using var client = new ClaudeUsageClient(source, clock, store);

    await client.GetUsageAsync(CancellationToken.None);
    clock.Advance(TimeSpan.FromMinutes(10));
    await AssertThrowsAsync<InvalidDataException>(
        () => client.GetUsageAsync(CancellationToken.None),
        "a schema failure should not return stale data");
    Assert(store.Entry is null, "a schema failure should delete persisted usage");

    await AssertThrowsAsync<ClaudeUsageTemporarilyUnavailableException>(
        () => client.GetUsageAsync(CancellationToken.None),
        "a later transient failure must not resurrect rejected data");
}

static async Task TestCallerCancellationPreservesCacheAsync()
{
    var start = new DateTimeOffset(2026, 9, 1, 4, 0, 0, TimeSpan.Zero);
    var clock = new ManualTimeProvider(start);
    var store = new MemoryCacheStore();
    using var cancellation = new CancellationTokenSource();
    var source = new CallbackUsageSource(call =>
    {
        if (call == 1) return Snapshot(10, start);
        cancellation.Cancel();
        throw new OperationCanceledException(cancellation.Token);
    });
    using var client = new ClaudeUsageClient(source, clock, store);

    await client.GetUsageAsync(CancellationToken.None);
    clock.Advance(TimeSpan.FromMinutes(10));
    await AssertThrowsAsync<OperationCanceledException>(
        () => client.GetUsageAsync(cancellation.Token),
        "the caller cancellation should still propagate");
    Assert(store.Entry?.Snapshot.Fable?.UsedPercent == 10,
        "caller cancellation must not delete the last accepted snapshot");
}

static async Task TestExpiredPersistedCacheAsync()
{
    var start = new DateTimeOffset(2026, 9, 1, 4, 0, 0, TimeSpan.Zero);
    var oldStore = new MemoryCacheStore
    {
        Entry = new ClaudeUsageCacheEntry(Snapshot(10, start), start.AddHours(-25))
    };
    using var oldClient = new ClaudeUsageClient(
        new SequenceUsageSource(new IOException("offline")),
        new ManualTimeProvider(start),
        oldStore);
    await AssertThrowsAsync<ClaudeUsageTemporarilyUnavailableException>(
        () => oldClient.GetUsageAsync(CancellationToken.None),
        "a snapshot older than 24 hours should not be shown");
    Assert(oldStore.Entry is null, "an old snapshot should be deleted");

    var resetSnapshot = Snapshot(20, start) with
    {
        Fable = new ClaudeUsageWindow(20, start.AddMinutes(-1))
    };
    var resetStore = new MemoryCacheStore
    {
        Entry = new ClaudeUsageCacheEntry(resetSnapshot, start.AddHours(-1))
    };
    using var resetClient = new ClaudeUsageClient(
        new SequenceUsageSource(new IOException("offline")),
        new ManualTimeProvider(start),
        resetStore);
    await AssertThrowsAsync<ClaudeUsageTemporarilyUnavailableException>(
        () => resetClient.GetUsageAsync(CancellationToken.None),
        "a snapshot past its Fable reset should not be shown");
    Assert(resetStore.Entry is null, "a snapshot past its reset should be deleted");
}

static Task TestFileCacheAsync()
{
    var temporaryDirectory = Path.Combine(Path.GetTempPath(), $"WeeklyUsageIndicator.Tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temporaryDirectory);
    try
    {
        var cachePath = Path.Combine(temporaryDirectory, ClaudeUsageFileCacheStore.CacheFileName);
        var now = new DateTimeOffset(2026, 9, 1, 4, 0, 0, TimeSpan.Zero);
        var entry = new ClaudeUsageCacheEntry(Snapshot(30, now), now);
        var store = new ClaudeUsageFileCacheStore(cachePath);

        store.Save(entry);
        using (var document = JsonDocument.Parse(File.ReadAllText(cachePath)))
        {
            var fields = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            Assert(fields.SequenceEqual(["Snapshot", "LastUpdatedAt"]),
                "the cache should contain only snapshot and update time");
        }
        Assert(store.Load() == entry, "the file cache should round-trip the sanitized snapshot");

        File.WriteAllText(cachePath, "not-json");
        Assert(store.Load() is null && !File.Exists(cachePath), "a corrupt cache should be ignored and deleted");
    }
    finally
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }
    return Task.CompletedTask;
}

static Task TestRetryErrorTextAsync()
{
    var error = UsageIndicatorForm.FriendlyClaudeError(
        new ClaudeUsageTemporarilyUnavailableException(DateTimeOffset.Now.AddMinutes(5)));
    var missing = UsageIndicatorForm.FriendlyClaudeError(
        new FileNotFoundException("Claude Code executable was not found."));
    Assert(error.Contains("다음 시도", StringComparison.Ordinal), "temporary errors should show the next attempt");
    Assert(missing.Contains("실행 파일", StringComparison.Ordinal), "a missing Claude Code install should be actionable");
    return Task.CompletedTask;
}

static Task TestStaleTooltipAsync()
{
    var now = DateTimeOffset.Now;
    var stale = new ClaudeUsageResult(
        Snapshot(30, now),
        now.AddMinutes(-12),
        now.AddMinutes(5),
        IsStale: true);
    var tooltip = UsageIndicatorForm.BuildTooltipText(null, stale, null, null, showClaude: true);

    Assert(tooltip.Contains("업데이트 지연", StringComparison.Ordinal), "the tooltip should identify delayed updates");
    Assert(tooltip.Contains("마지막 성공", StringComparison.Ordinal), "the tooltip should show the last success");
    Assert(tooltip.Contains("다음 시도", StringComparison.Ordinal), "the tooltip should show the next retry");
    Assert(tooltip.Contains("Fable: 70% 남음", StringComparison.Ordinal), "the tooltip should retain cached Fable usage");
    return Task.CompletedTask;
}

static ClaudeUsageSnapshot Snapshot(double fableUsedPercent, DateTimeOffset now) => new(
    new ClaudeUsageWindow(10, now.AddHours(3)),
    new ClaudeUsageWindow(20, now.AddDays(3)),
    new ClaudeUsageWindow(fableUsedPercent, now.AddDays(3)));

static string SampleUsageText() => """
    Current session: 10% used · resets Sep 1, 6pm (Asia/Seoul)
    Current week (all models): 20% used · resets Sep 5, 6pm (Asia/Seoul)
    Current week (Fable): 30% used · resets Sep 5, 6pm (Asia/Seoul)
    """;

static string UsageEnvelope(string result, int numTurns = 0, double totalCost = 0) =>
    JsonSerializer.Serialize(new
    {
        is_error = false,
        num_turns = numTurns,
        total_cost_usd = totalCost,
        duration_api_ms = 0,
        result
    });

static async Task AssertThrowsAsync<TException>(Func<Task> action, string message)
    where TException : Exception
{
    try
    {
        await action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException(message);
}

static void AssertThrows<TException>(Action action, string message)
    where TException : Exception
{
    try
    {
        action();
    }
    catch (TException)
    {
        return;
    }
    throw new InvalidOperationException(message);
}

static void Assert(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}

internal sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    private DateTimeOffset _utcNow = utcNow;
    public override DateTimeOffset GetUtcNow() => _utcNow;
    public void Advance(TimeSpan duration) => _utcNow += duration;
}

internal sealed class MemoryCacheStore : IClaudeUsageCacheStore
{
    public ClaudeUsageCacheEntry? Entry { get; set; }
    public ClaudeUsageCacheEntry? Load() => Entry;
    public void Save(ClaudeUsageCacheEntry entry) => Entry = entry;
    public void Clear() => Entry = null;
}

internal sealed class SequenceUsageSource(params object[] outcomes) : IClaudeUsageSource
{
    private readonly Queue<object> _outcomes = new(outcomes);
    public int ReadCount { get; private set; }

    public Task<ClaudeUsageSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        ReadCount++;
        if (_outcomes.Count == 0) throw new InvalidOperationException("No queued Claude usage outcome remains.");
        return _outcomes.Dequeue() switch
        {
            ClaudeUsageSnapshot snapshot => Task.FromResult(snapshot),
            Exception exception => Task.FromException<ClaudeUsageSnapshot>(exception),
            _ => throw new InvalidOperationException("Unsupported Claude usage outcome type.")
        };
    }
}

internal sealed class CallbackUsageSource(Func<int, ClaudeUsageSnapshot> callback) : IClaudeUsageSource
{
    private int _callCount;
    public Task<ClaudeUsageSnapshot> ReadAsync(CancellationToken cancellationToken) =>
        Task.FromResult(callback(++_callCount));
}

internal sealed class SequenceCommandRunner(params ClaudeCommandResult[] outcomes) : IClaudeCommandRunner
{
    private readonly Queue<ClaudeCommandResult> _outcomes = new(outcomes);
    public Task<ClaudeCommandResult> RunUsageAsync(CancellationToken cancellationToken) =>
        Task.FromResult(_outcomes.Dequeue());
}
