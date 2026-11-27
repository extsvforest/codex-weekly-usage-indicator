using System.Text.Json;
using WeeklyUsageIndicator;

// Started by an Antigravity read under test in place of agy: nothing else of this program runs.
if (AntigravityTests.StandIn(args) is { } standInExit) Environment.Exit(standInExit);
if (args.Contains("--english")) UiText.SetLanguage("en");
if (args.Contains("--live-environment-smoke")) { await LiveEnvironmentSmoke.RunAsync(); return; }
if (args.Contains("--claude-shelf-live-probe")) { ClaudeSessionShelfTests.LiveProbe(); return; }
if (args.Contains("--claude-shelf-memory-probe")) { ClaudeShelfMemoryProbe.Run(args); return; }
if (args.Contains("--account-manager-memory-probe")) { await AccountManagerMemoryProbe.RunAsync(args); return; }
if (args.Contains("--claude-layout-dry-run")) { ClaudeSessionShelfTests.LayoutDryRun(); return; }
if (args.Contains("--claude-older-states")) { ClaudeOlderStatesProbe.Run(args); return; }
if (args.Contains("--claude-sidebar-store-shape")) { ClaudeSessionShelfTests.SidebarStoreShape(); return; }
if (args.Contains("--claude-layout-diff")) { ClaudeSessionShelfTests.LayoutDiff(args[Array.IndexOf(args, "--claude-layout-diff") + 1], args[Array.IndexOf(args, "--claude-layout-diff") + 2]); return; }
// Reads an app bundle (app.asar) against the shelf design, read-only: the contract, and every marker, window or flow that
// does not read as reviewed, so the review of a new version starts where the shape moved.
if (args.Contains("--claude-contract-review"))
{
    var bundle = File.ReadAllBytes(args[Array.IndexOf(args, "--claude-contract-review") + 1]);
    Console.WriteLine("contract=" + ClaudeDesktopProbe.Contract(bundle));
    foreach (var spellings in ClaudeDesktopProbe.ContractMarkers)
        if (!spellings.Any(spelling => bundle.AsSpan().IndexOf(System.Text.Encoding.UTF8.GetBytes(spelling)) >= 0))
            Console.WriteLine("missing marker " + spellings[0]);
    foreach (var item in ClaudeDesktopProbe.Unmatched(bundle)) Console.WriteLine("unmatched " + item);
    return;
}
if (args.Contains("--claude-profile-tests")) { await ClaudeProfileTests.RunAsync(); Console.WriteLine("PASS: Claude profiles"); return; }
if (args.Contains("--claude-app-data-tests")) { await ClaudeAppDataTests.RunAsync(); Console.WriteLine("PASS: Claude profile folder"); return; }
if (args.Contains("--claude-shelf-tests")) { await ClaudeSessionShelfTests.RunAsync(); Console.WriteLine("PASS: Claude session shelf"); return; }
if (args.Contains("--claude-sync-tests")) { await ClaudeSessionSyncTests.RunAsync(); Console.WriteLine("PASS: Claude session sync"); return; }
if (args.Contains("--widget-face-preview")) { WidgetFaceTests.Preview(args); return; }
if (args.Contains("--widget-native-preview")) { await WidgetFaceTests.NativePreview(args); return; }
if (args.Contains("--widget-face-tests")) { await WidgetFaceTests.RunAsync(); Console.WriteLine("PASS: widget face"); return; }
if (args.Contains("--widget-scale-tests")) { await WidgetScaleTests.RunAsync(); Console.WriteLine("PASS: widget scale"); return; }
if (args.Contains("--antigravity-tests")) { await AntigravityTests.RunAsync(); Console.WriteLine("PASS: Antigravity usage"); return; }
if (args.Contains("--antigravity-live-check")) { await AntigravityTests.LiveCheckAsync(); return; }
if (args.Contains("--claude-menu-smoke")) { await ClaudeAccountMenuSmoke.RunAsync(); return; }
if (args.Contains("--live-claude-menu-smoke")) { await ClaudeAccountMenuSmoke.RunAsync(live: true); return; }
if (args.Contains("--language-ui-smoke")) { await LanguageUiSmoke.RunAsync(); return; }
if (args.Contains("--localization-tests")) { await LocalizationTests.RunAsync(); return; }
if (args.Contains("--account-style-preview")) { await AccountStylePreview.RunAsync(args.Contains("--hold"), args.Contains("--standard-dpi")); return; }
// The same window without pictures: opening reads nothing, one row's read, three at a time, the Antigravity states, closing.
if (args.Contains("--account-window-tests")) { await AccountStylePreview.RunAsync(false); return; }
if (args.Contains("--account-opening-smoke")) { await AccountOpeningSmoke.RunAsync(args.Contains("--trace-only")); return; }

if (args.Contains("--live-account-usage-smoke"))
{
    await LiveAccountUsageSmoke.RunAsync();
    return;
}
if (args.Contains("--live-usage-cleanup-smoke"))
{
    await LiveUsageCleanupSmoke.RunAsync();
    return;
}

if (args.Contains("--combined-ui-smoke")) { await CombinedUsageUiSmoke.RunAsync(); return; }
if (args.Contains("--account-ui-smoke"))
{
    // The account window's own checks, on their own: registration and renaming, then the row's manual query.
    await AccountUiSmoke.RunAsync(); Console.WriteLine("PASS: account manager registration, rename, cancellation and input focus");
    await AccountUsageUiSmoke.RunAsync(); Console.WriteLine("PASS: manual usage buttons, async failure and cancellation");
    return;
}

if (args.Contains("--live-combined-usage-smoke")) { await LiveCombinedUsageSmoke.RunAsync(); return; }

if (args.Contains("--tooltip-ui-smoke")) { await UsageTooltipHoverSmoke.RunAsync(); return; }
if (args.Contains("--tooltip-hover-smoke")) { await UsageTooltipHoverSmoke.RunAsync(); return; }

var tests = new (string Name, Func<Task> Run)[]
{
    // Run the brief cursor-driven check first so a local user can resume using the
    // mouse while the slower credential and lifecycle fixtures continue.
    ("stable light tooltip hover, placement and focus", UsageTooltipHoverSmoke.RunAsync),
    ("widget face: gauges, grounds and states", WidgetFaceTests.RunAsync),
    ("widget scale: placement, menu and the size window", WidgetScaleTests.RunAsync),
    ("Antigravity usage: report, account, store and one read at a time", AntigravityTests.RunAsync),
    ("Claude weekly metric, independent expiry and profile cache", ClaudeMetricTests.RunAll),
    ("Claude official profile identity, link-free profiles and junction-only retirement", ClaudeProfileTests.RunAsync),
    ("Claude profile folder: legacy roaming, moved out of the package, inside it", ClaudeAppDataTests.RunAsync),
    ("Claude session shelf: fill by adding and advancing, closed- and running-app writes, account mapping and log observation", ClaudeSessionShelfTests.RunAsync),
    ("Claude combined weekly total and session-layout plan", ClaudeSessionSyncTests.RunAsync),
    ("Claude per-account reads, saved values, moving the default-home account and identity invalidation", ClaudeControllerTests.RunAsync),
    ("Claude account menu actions and inactive-profile usage", () => ClaudeAccountMenuSmoke.RunAsync()),
    ("account operation lease blocks lifecycle replacement across awaits", AccountOperationLeaseTests.RunAsync),
    ("WSL native targets, independent login and joint recovery", WslCodexTests.RunAsync),
    ("language switching preserves bounds, snapshots and account data", LanguageUiSmoke.RunAsync),
    ("translation catalogs, known errors and language preferences", LocalizationTests.RunAsync),
    ("Codex app-server lifecycle isolation", AppServerLifecycleTests.RunAsync),
    ("Codex account vault and crash recovery", AccountStoreTests.RunAsync),
    ("manual inactive usage, rotated credentials and recovery", AccountUsageQueryTests.RunAsync),
    ("isolated Codex login runtime", AccountRuntimeTests.RunAsync),
    ("account manager registration, rename, cancellation and input focus", AccountUiSmoke.RunAsync),
    ("manual usage buttons, async failure and cancellation", AccountUsageUiSmoke.RunAsync),
    ("combined weekly snapshot coverage and reset boundaries", CombinedUsageTests.RunAsync),
    ("combined manager opening, refresh, cancellation and cleanup", CombinedUsageUiSmoke.RunAsync),
    ("account window: no read on opening, one row's read, three at a time, Antigravity states and closing during a query",
        () => AccountStylePreview.RunAsync(false)),
    ("account manager first paint and stable partial updates", () => AccountOpeningSmoke.RunAsync()),
    ("multi-account tooltip observations and refresh boundaries", UsageTooltipTests.RunAsync),
    ("supervisor retries abnormal exits but respects normal Quit", TestSupervisorAsync),
    ("official Claude /usage output is parsed", TestObservedUsageOutputAsync),
    ("verbose official usage rejects seeded fallback and local behavior percentages", TestVerboseUsageOutputAsync),
    ("usage without reset times remains valid through client and tooltip", TestUsageWithoutResetsAsync),
    ("optional limits fail independently", TestIndependentLimitsAsync),
    ("unknown reset format preserves verified percentages", TestUnknownResetFormatAsync),
    ("missing or invalid Fable never becomes zero usage", TestInvalidFableAsync),
    ("decimal percentages and year rollover are parsed", TestUsageTextVariantsAsync),
    ("model turns and costs are rejected", TestNoModelTurnGuardAsync),
    ("authentication and malformed output are hard failures", TestSourceHardFailuresAsync),
    ("Claude process invocation is shell-free and session-free", TestProcessArgumentsAsync),
    ("slow current /usage report parses and a timed-out retry names its cause", TestSlowUsageReportAsync),
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

if (args.Contains("--claude-usage-tests"))
{
    // Window-free Claude usage checks: parsing, invocation, client cache/backoff, metric and controller.
    var first = Array.FindIndex(tests, test => test.Name == "official Claude /usage output is parsed");
    foreach (var test in new (string Name, Func<Task> Run)[]
             {
                 ("Claude weekly metric, independent expiry and profile cache", ClaudeMetricTests.RunAll),
                 ("Claude per-account reads, saved values, moving the default-home account and identity invalidation", ClaudeControllerTests.RunAsync)
             }.Concat(tests[first..]))
    {
        await test.Run();
        Console.WriteLine($"PASS: {test.Name}");
    }
    return;
}

foreach (var test in tests)
{
    if (args.Contains("--skip-live-pointer-check") && test.Name == "stable light tooltip hover, placement and focus")
    { Console.WriteLine("SKIP: live pointer check (explicit flag; run separately)"); continue; }
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

static Task TestVerboseUsageOutputAsync()
{
    var now = DateTimeOffset.UtcNow;
    object current = new { rate_limits = new { limits = new[] { new { kind = "weekly", percent = 20 } } } };
    string Envelope(object? report, string? text = null, int turns = 0, double cost = 0) =>
        JsonSerializer.Serialize(new object[]
        {
            new { type = "system", subtype = "init" },
            new { type = "assistant", usage_report = report },
            new { type = "result", is_error = false, num_turns = turns,
                total_cost_usd = cost, result = text ?? SampleUsageText() }
        });

    var accepted = ClaudeCodeUsageSource.ParseEnvelope(Envelope(current), now);
    Assert(accepted.Weekly?.UsedPercent == 20 && accepted.Fable?.UsedPercent == 30,
        "verbose output retains independently parsed official quota windows");
    foreach (var unavailable in new object[]
    {
        new { rate_limits = (object?)null },
        new { rate_limits = new { extra_usage = (object?)null } },
        new { rate_limits = new { limits = Array.Empty<object>() } }
    })
    {
        AssertThrows<IOException>(() => ClaudeCodeUsageSource.ParseEnvelope(Envelope(unavailable), now),
            "a seeded headless response must not relabel old text percentages as a new observation");
    }
    AssertThrows<IOException>(() => ClaudeCodeUsageSource.ParseEnvelope(
        Envelope(new { rate_limits = (object?)null }, "Last 7d\n87% of your usage was at >150k context"), now),
        "local behavior percentages are not quota availability");
    AssertThrows<InvalidDataException>(() => ClaudeCodeUsageSource.ParseEnvelope(Envelope(null), now),
        "an unknown verbose report schema is rejected");
    AssertThrows<InvalidDataException>(() => ClaudeCodeUsageSource.ParseEnvelope(Envelope(current, turns: 1), now),
        "verbose results retain the no-model-turn guard");
    AssertThrows<InvalidDataException>(() => ClaudeCodeUsageSource.ParseEnvelope(Envelope(current, cost: 0.01), now),
        "verbose results retain the zero-cost guard");
    var duplicateResult = "[" + UsageEnvelope(SampleUsageText()).Replace("{", "{\"type\":\"result\",", StringComparison.Ordinal)
        + "," + UsageEnvelope(SampleUsageText()).Replace("{", "{\"type\":\"result\",", StringComparison.Ordinal) + "]";
    AssertThrows<InvalidDataException>(() => ClaudeCodeUsageSource.ParseEnvelope(duplicateResult, now),
        "ambiguous final results are rejected");
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
        var result = ClaudeCodeUsageSource.ParseUsageText(
            "Current session: 10% used\nCurrent week (all models): 20% used\n" + fable, now);
        Assert(result.Fable is null && result.Weekly?.UsedPercent == 20,
            "invalid Fable stays missing while an independently valid weekly value survives");
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
    await AssertThrowsAsync<ClaudeLoginRequiredException>(
        () => loginSource.ReadAsync(CancellationToken.None),
        "a Claude Code login failure should remain a hard failure");

    var loginEnvelope = JsonSerializer.Serialize(new { is_error = true, num_turns = 0,
        total_cost_usd = 0, result = "Not logged in. Please login." });
    await AssertThrowsAsync<ClaudeLoginRequiredException>(
        () => Task.FromResult(ClaudeCodeUsageSource.ParseEnvelope(loginEnvelope, clock.GetUtcNow())),
        "usage authentication failures expose the same recovery action as auth status");

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
        "-p", "--safe-mode", "--no-session-persistence", "--no-chrome", "/usage", "--output-format", "json", "--verbose", "--max-turns", "0"]),
        "the command contract should remain exact and reviewable");
    Assert(UsageIndicatorForm.ClaudeRefreshTimeout > ClaudeCodeProcessRunner.CommandTimeout,
        "the UI timeout must leave the process runner time to classify a slow command as transient");
    Assert(UsageIndicatorForm.ClaudeRefreshTimeout - ClaudeCodeProcessRunner.CommandTimeout >= TimeSpan.FromSeconds(30),
        "the UI timeout must also cover the identity check that precedes the read");
    // Observed 2026-11-14 with Claude Code 2.1.321: /usage answered after 27 s and 50 s.
    Assert(ClaudeCodeProcessRunner.CommandTimeout >= TimeSpan.FromMinutes(2),
        "the read must outlast the official CLI's local usage analysis");
    Assert(startInfo.RedirectStandardInput, "print mode must not wait on an inherited or open stdin");
    return Task.CompletedTask;
}

static Task TestSlowUsageReportAsync()
{
    // Shape of Claude Code 2.1.321 output (2026-11-14): no five-hour line while that window is
    // idle, structured limits beside the text, and the local analysis after the limits.
    var now = new DateTimeOffset(2026, 11, 14, 6, 30, 0, TimeSpan.Zero);
    var text = """
        You are currently using your subscription to power your Claude Code usage

        Current week (all models): 87% used · resets Nov 14, 6pm (Asia/Seoul)
        Current week (Fable): 82% used · resets Nov 14, 6pm (Asia/Seoul)

        What's contributing to your usage?
        Approximate · sessions on this machine only · shares overlap

        Last 24h · 5000 requests · 20 sessions
          95%  Start fresh when you switch tasks · run /clear
        """;
    var raw = JsonSerializer.Serialize(new object[]
    {
        new { type = "system", subtype = "init" },
        new
        {
            type = "assistant",
            usage_report = new
            {
                session = new { total_cost_usd = 0, total_duration_ms = 49518 },
                rate_limits = new
                {
                    limits = new object[]
                    {
                        new { kind = "weekly_all", group = "weekly", percent = 87, resets_at = "2026-11-14T09:00:00.410151+00:00", scope = (object?)null, is_active = true },
                        new { kind = "weekly_scoped", group = "weekly", percent = 82, resets_at = "2026-11-14T09:00:00.410262+00:00", scope = new { model = new { display_name = "Fable" } }, is_active = false }
                    },
                    extra_usage = new { is_enabled = false }
                }
            }
        },
        new { type = "result", subtype = "success", is_error = false, num_turns = 0, total_cost_usd = 0,
            duration_ms = 48587, local_command = "usage", result = text }
    });
    var snapshot = ClaudeCodeUsageSource.ParseEnvelope(raw, now);
    Assert(snapshot.FiveHour is null && snapshot.Weekly?.UsedPercent == 87 && snapshot.Fable?.UsedPercent == 82,
        "the current report parses its weekly windows and leaves the idle session window absent");
    Assert(snapshot.Weekly?.ResetsAt == new DateTimeOffset(2026, 11, 14, 18, 0, 0, TimeSpan.FromHours(9)),
        "the hour-only reset keeps its reported time zone");

    var waited = new TimeoutException("Claude Code /usage timed out.");
    var retry = DateTimeOffset.Now.AddMinutes(10);
    var shown = UsageIndicatorForm.FriendlyClaudeError(new ClaudeUsageTemporarilyUnavailableException(retry,
        new ClaudeUsageTemporarilyUnavailableException(retry, waited)));
    Assert(shown.StartsWith(UiText.T("Claude 응답 시간이 초과되었습니다."), StringComparison.Ordinal),
        "a retry wait that follows a timed-out read names the timeout");
    shown = UsageIndicatorForm.FriendlyClaudeError(new ClaudeUsageTemporarilyUnavailableException(retry));
    Assert(shown == UiText.T("Claude 사용량 조회가 지연되었습니다. 다음 시도: {0}", shown[(shown.LastIndexOf(": ", StringComparison.Ordinal) + 2)..]),
        "a plain interval wait keeps the general text");
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
    Assert(first.IsStale && first.RetryAfter == clock.GetUtcNow().AddMinutes(10),
        "the first transient failure keeps the cache and observes the ten-minute floor");

    var suppressed = await client.GetUsageAsync(CancellationToken.None);
    Assert(suppressed.IsStale && source.ReadCount == 2, "backoff should suppress repeated commands");

    clock.Advance(TimeSpan.FromMinutes(10));
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
    var independentlyValid = await resetClient.GetUsageAsync(CancellationToken.None);
    Assert(ClaudeDisplayMetric.FableWeekly.SelectUsedPercent(independentlyValid.Snapshot, start) is null &&
        ClaudeDisplayMetric.OverallWeekly.SelectUsedPercent(independentlyValid.Snapshot, start) == 20,
        "expired Fable is hidden while the still-valid overall window remains visible");
}

static Task TestFileCacheAsync()
{
    var temporaryDirectory = Path.Combine(Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT") ?? Path.Combine(Path.GetTempPath(), "gfs-agent", "indicator-tests"), $"cache-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temporaryDirectory);
    try
    {
        var cachePath = Path.Combine(temporaryDirectory, ClaudeUsageFileCacheStore.CacheFileName);
        var now = new DateTimeOffset(2026, 9, 1, 4, 0, 0, TimeSpan.Zero);
        var entry = new ClaudeUsageCacheEntry(Snapshot(30, now), now, "profile:digest");
        var store = new ClaudeUsageFileCacheStore(cachePath, new ManualTimeProvider(now));

        store.For(entry.ProfileKey).Save(entry);
        using (var document = JsonDocument.Parse(File.ReadAllText(cachePath)))
        {
            var fields = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
            var entryFields = document.RootElement.GetProperty("Entries").EnumerateArray().Single()
                .EnumerateObject().Select(property => property.Name).ToArray();
            Assert(fields.SequenceEqual(["Version", "Entries"])
                   && entryFields.SequenceEqual(["ProfileKey", "Snapshot", "LastUpdatedAt", "Recovery"]),
                "the cache contains only sanitized usage under a nonsecret attribution key per account");
        }
        Assert(store.For(entry.ProfileKey).Load() == entry && store.Last(entry.ProfileKey) == entry,
            "the file cache should round-trip the sanitized snapshot");

        File.WriteAllText(cachePath, "not-json");
        Assert(store.For(entry.ProfileKey).Load() is null && !File.Exists(cachePath), "a corrupt cache should be ignored and deleted");
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
