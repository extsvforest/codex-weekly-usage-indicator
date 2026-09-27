using System.Drawing;
using System.Reflection;
using System.Text.Json;
using System.Windows.Forms;
using WeeklyUsageIndicator;

internal static class ClaudeMetricTests
{
    internal static async Task RunAll()
    {
        IndependentWindowsAndFreshness();
        SettingsMigration();
        await IndependentExpiryAsync();
        await MinimumAttemptIntervalAsync();
        await ProfileCacheIsolationAsync();
        await SelectionDoesNotQueryAsync();
    }

    private static void IndependentWindowsAndFreshness()
    {
        var now = DateTimeOffset.UtcNow;
        var overallOnly = ClaudeCodeUsageSource.ParseUsageText(
            "Current week (all models): 12.5% used\nCurrent week (Fable): unavailable", now);
        Check(overallOnly.Weekly?.UsedPercent == 12.5 && overallOnly.Fable is null,
            "missing Fable leaves overall usage intact");
        Check(ClaudeDisplayMetric.OverallWeekly.SelectUsedPercent(overallOnly, now) == 12.5 &&
            ClaudeDisplayMetric.FableWeekly.SelectUsedPercent(overallOnly, now) is null,
            "the selected metric never falls back to another weekly limit");
        Check(UsagePercentage.Remaining(12.5) == 88 && UsagePercentage.Remaining(double.NaN) is null,
            "fractional remaining is rounded once and malformed percentages stay unknown");
        var fableOnly = ClaudeCodeUsageSource.ParseUsageText("Current week (Fable): 30% used", now);
        Check(fableOnly.Weekly is null && fableOnly.Fable?.UsedPercent == 30, "Fable also remains independent");
        Throws<InvalidDataException>(() => ClaudeCodeUsageSource.ParseUsageText(
            "Current session: unavailable\nCurrent week (all models): 101% used\nCurrent week (Fable): unavailable", now));
        Throws<IOException>(() => ClaudeCodeUsageSource.ParseUsageText(
            "Showing last-known usage\nCurrent week (all models): 12% used", now));

        var snapshot = new ClaudeUsageSnapshot(new(5, now.AddHours(2)), new(12.5, now.AddDays(2)), new(30, now.AddDays(3)));
        var result = new ClaudeUsageResult(snapshot, now, null, false);
        var previousLanguage = UiText.Language;
        try
        {
            UiText.SetLanguage("ko");
            var overall = UsageIndicatorForm.BuildTooltipText(null, result, null, null, true,
                claudeMetric: ClaudeDisplayMetric.OverallWeekly);
            var fable = UsageIndicatorForm.BuildTooltipText(null, result, null, null, true,
                claudeMetric: ClaudeDisplayMetric.FableWeekly);
            Check(overall.Contains("전체 주간: 88% 남음", StringComparison.Ordinal), "tooltip matches widget rounding");
            Check(overall.IndexOf("전체 주간:", StringComparison.Ordinal) < overall.IndexOf("Fable:", StringComparison.Ordinal) &&
                fable.IndexOf("Fable:", StringComparison.Ordinal) < fable.IndexOf("전체 주간:", StringComparison.Ordinal),
                "the selected window is first, and the other windows are retained");
            var expired = result with { Snapshot = snapshot with { Weekly = snapshot.Weekly! with { ResetsAt = now.AddSeconds(-1) } } };
            Check(ClaudeDisplayMetric.OverallWeekly.SelectUsedPercent(expired.Snapshot, now) is null &&
                ClaudeDisplayMetric.FableWeekly.SelectUsedPercent(expired.Snapshot, now) == 30,
                "an expired selection is hidden independently of other valid windows");
            Check(UsageIndicatorForm.BuildTooltipText(null, expired, null, null, true)
                .Contains("전체 주간: 갱신 필요", StringComparison.Ordinal), "expired data is not presented as remaining usage");
        }
        finally { UiText.SetLanguage(previousLanguage); }
    }

    private static void SettingsMigration()
    {
        WithFixture(root =>
        {
            var fresh = Path.Combine(root, "fresh.json");
            Check(IndicatorSettingsStore.LoadClaudeMetric(fresh) == ClaudeDisplayMetric.OverallWeekly,
                "new installs default to overall weekly");
            IndicatorSettingsStore.SavePosition(new Point(14, 27), fresh);
            Check(IndicatorSettingsStore.LoadClaudeMetric(fresh) == ClaudeDisplayMetric.OverallWeekly,
                "a first position save preserves the new default");
            var legacy = Path.Combine(root, "legacy.json");
            File.WriteAllText(legacy, "{\"X\":14,\"Y\":27,\"ShowClaude\":false,\"Language\":\"ko\"}");
            Check(IndicatorSettingsStore.LoadClaudeMetric(legacy) == ClaudeDisplayMetric.FableWeekly,
                "existing installs without a preference keep Fable");
            IndicatorSettingsStore.SaveLanguage("en", legacy);
            Check(IndicatorSettingsStore.LoadClaudeMetric(legacy) == ClaudeDisplayMetric.FableWeekly,
                "a legacy language save must not silently migrate the displayed number");
            IndicatorSettingsStore.SaveClaudeMetric(ClaudeDisplayMetric.OverallWeekly, legacy);
            IndicatorSettingsStore.SaveShowClaude(true, legacy);
            IndicatorSettingsStore.SavePosition(new Point(-250, 100), legacy);
            IndicatorSettingsStore.SaveLanguage("ko", legacy);
            Check(IndicatorSettingsStore.LoadClaudeMetric(legacy) == ClaudeDisplayMetric.OverallWeekly &&
                IndicatorSettingsStore.LoadShowClaude(legacy) && IndicatorSettingsStore.LoadLanguage(legacy) == "ko" &&
                IndicatorSettingsStore.LoadPosition(legacy) == new Point(-250, 100),
                "visibility, position and language changes retain the explicit metric");
            IndicatorSettingsStore.SaveClaudeMetric(ClaudeDisplayMetric.FableWeekly, legacy);
            Check(IndicatorSettingsStore.LoadClaudeMetric(legacy) == ClaudeDisplayMetric.FableWeekly &&
                IndicatorSettingsStore.LoadPosition(legacy) == new Point(-250, 100),
                "metric selection persists without changing other preferences");

            var cache = new ClaudeUsageFileCacheStore(Path.Combine(root, "cache.json"));
            var entry = new ClaudeUsageCacheEntry(new(null, new(42, null), null), DateTimeOffset.UtcNow, "profile-fixture");
            cache.Save(entry);
            Check(cache.Load() == entry, "a profile-bound overall-only cache round trips without Fable");
            File.WriteAllText(Path.Combine(root, "cache.json"), JsonSerializer.Serialize(new
            { entry.Snapshot, entry.LastUpdatedAt }));
            Check(cache.Load()?.ProfileKey == ClaudeUsageCacheEntry.DefaultProfileKey,
                "pre-profile caches are bound only to the default profile");
        });
    }

    private static async Task IndependentExpiryAsync()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var expiredMetric in new[] { ClaudeDisplayMetric.OverallWeekly, ClaudeDisplayMetric.FableWeekly })
        {
            var snapshot = new ClaudeUsageSnapshot(null,
                new(20, expiredMetric == ClaudeDisplayMetric.OverallWeekly ? now.AddMinutes(-1) : now.AddDays(1)),
                new(30, expiredMetric == ClaudeDisplayMetric.FableWeekly ? now.AddMinutes(-1) : now.AddDays(2)));
            var store = new MemoryCacheStore { Entry = new(snapshot, now.AddHours(-1)) };
            using var client = new ClaudeUsageClient(new SequenceUsageSource(new IOException("offline")), new ManualTimeProvider(now), store);
            var result = await client.GetUsageAsync(CancellationToken.None);
            Check(result.IsStale && expiredMetric.SelectUsedPercent(result.Snapshot, now) is null && store.Entry is not null,
                "one expired window must not erase another unexpired recovery window");
        }
        var expiredStore = new MemoryCacheStore
        {
            Entry = new(new(new(10, now.AddSeconds(-1)), new(20, now.AddSeconds(-1)), new(30, now.AddSeconds(-1))), now.AddHours(-1))
        };
        using var expiredClient = new ClaudeUsageClient(new SequenceUsageSource(new IOException("offline")), new ManualTimeProvider(now), expiredStore);
        await ThrowsAsync<ClaudeUsageTemporarilyUnavailableException>(() => expiredClient.GetUsageAsync(CancellationToken.None));
        Check(expiredStore.Entry is null, "a snapshot with no unexpired windows is removed");
    }

    private static async Task MinimumAttemptIntervalAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var clock = new ManualTimeProvider(now);
        var source = new SequenceUsageSource(new IOException("offline"), new ClaudeUsageSnapshot(null, new(15, now.AddDays(1)), null));
        using var client = new ClaudeUsageClient(source, clock);
        await ThrowsAsync<ClaudeUsageTemporarilyUnavailableException>(() => client.GetUsageAsync(CancellationToken.None));
        clock.Advance(TimeSpan.FromMinutes(9));
        await ThrowsAsync<ClaudeUsageTemporarilyUnavailableException>(() => client.GetUsageAsync(CancellationToken.None));
        Check(source.ReadCount == 1, "manual retry after a failure cannot bypass ten minutes");
        clock.Advance(TimeSpan.FromMinutes(1));
        Check(!(await client.GetUsageAsync(CancellationToken.None)).IsStale && source.ReadCount == 2,
            "a fresh query is allowed when the minimum interval elapses");

        var sharedThrottle = new ClaudeUsageRequestThrottle();
        var firstSource = new SequenceUsageSource(new ClaudeUsageSnapshot(null, new(10, now.AddDays(1)), null));
        var secondSource = new SequenceUsageSource(new ClaudeUsageSnapshot(null, new(20, now.AddDays(1)), null));
        using var first = new ClaudeUsageClient(firstSource, clock, profileKey: "a", throttle: sharedThrottle);
        using var second = new ClaudeUsageClient(secondSource, clock, profileKey: "b", throttle: sharedThrottle);
        await first.GetUsageAsync(CancellationToken.None);
        await ThrowsAsync<ClaudeUsageTemporarilyUnavailableException>(() => second.GetUsageAsync(CancellationToken.None));
        Check(secondSource.ReadCount == 0, "changing profiles does not bypass the shared request interval");
        clock.Advance(TimeSpan.FromMinutes(10));
        Check((await second.GetUsageAsync(CancellationToken.None)).Snapshot.Weekly?.UsedPercent == 20,
            "the next permitted request reads the selected profile");

        var resetClock = new ManualTimeProvider(now);
        var resetSource = new SequenceUsageSource(new ClaudeUsageSnapshot(null, new(10, now.AddMinutes(1)), null));
        using var resetClient = new ClaudeUsageClient(resetSource, resetClock);
        await resetClient.GetUsageAsync(CancellationToken.None);
        resetClock.Advance(TimeSpan.FromMinutes(2));
        await ThrowsAsync<ClaudeUsageTemporarilyUnavailableException>(() => resetClient.GetUsageAsync(CancellationToken.None));
        Check(resetSource.ReadCount == 1, "a window reset does not turn refresh into an early command");
    }

    private static async Task ProfileCacheIsolationAsync()
    {
        var now = DateTimeOffset.UtcNow;
        var store = new MemoryCacheStore
        {
            Entry = new(new(null, new(99, now.AddDays(1)), null), now, "profile-a")
        };
        using var client = new ClaudeUsageClient(new SequenceUsageSource(new IOException("offline")), new ManualTimeProvider(now), store, "profile-b");
        await ThrowsAsync<ClaudeUsageTemporarilyUnavailableException>(() => client.GetUsageAsync(CancellationToken.None));
        Check(store.Entry is null, "an old profile's usage must never stand in for a selected profile");
    }

    private static Task SelectionDoesNotQueryAsync()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try
            {
                var source = new SequenceUsageSource();
                using var client = new ClaudeUsageClient(source, TimeProvider.System);
                using var widget = new UsageIndicatorForm(previewMode: true, claudeClient: client);
                var menu = (ContextMenuStrip)typeof(UsageIndicatorForm).GetField("_contextMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(widget)!;
                var submenu = (ToolStripMenuItem)menu.Items.Find("ClaudeDisplayMenu", false).Single();
                var fable = (ToolStripMenuItem)submenu.DropDownItems.Find("ClaudeMetric-fable-weekly", false).Single();
                var overall = (ToolStripMenuItem)submenu.DropDownItems.Find("ClaudeMetric-overall-weekly", false).Single();
                fable.PerformClick();
                Check(widget.SelectedClaudeMetric == ClaudeDisplayMetric.FableWeekly && fable.Checked && !overall.Checked,
                    "the menu selects exactly one displayed weekly limit");
                overall.PerformClick();
                Check(widget.SelectedClaudeMetric == ClaudeDisplayMetric.OverallWeekly && overall.Checked && !fable.Checked && source.ReadCount == 0,
                    "changing the display does not request usage");
                completion.SetResult();
            }
            catch (Exception exception) { completion.SetException(exception); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static void WithFixture(Action<string> action)
    {
        var parent = Path.GetFullPath(Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT")
            ?? Path.Combine(Path.GetTempPath(), "gfs-agent", "indicator-tests"));
        var root = Path.GetFullPath(Path.Combine(parent, "claude-metric-" + Guid.NewGuid().ToString("N")));
        Check(string.Equals(Path.GetDirectoryName(root), parent.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase),
            "fixtures stay inside the assigned test root");
        Directory.CreateDirectory(root);
        try { action(root); }
        finally
        {
            foreach (var path in Directory.GetFiles(root)) File.Delete(path);
            Directory.Delete(root, recursive: false);
        }
    }

    private static void Throws<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Claude metric: expected " + typeof(T).Name);
    }

    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); }
        catch (T) { return; }
        throw new InvalidOperationException("Claude metric: expected " + typeof(T).Name);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Claude metric: " + message);
    }
}
