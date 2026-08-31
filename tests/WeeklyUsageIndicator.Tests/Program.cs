using System.Net;
using System.Text;
using WeeklyUsageIndicator;

var tests = new (string Name, Func<Task> Run)[]
{
    ("ten-minute cache prevents duplicate requests", TestFreshCacheAsync),
    ("429 keeps cached usage and backs off progressively", TestRateLimitRecoveryAsync),
    ("Retry-After is honored while cached usage remains visible", TestRetryAfterAsync),
    ("server errors keep cached usage until recovery", TestServerErrorRecoveryAsync),
    ("network errors keep cached usage until recovery", TestNetworkErrorRecoveryAsync),
    ("body-read errors keep cached usage until recovery", TestBodyReadErrorRecoveryAsync),
    ("request timeouts keep cached usage until recovery", TestRequestTimeoutRecoveryAsync),
    ("authentication and schema failures remain hard failures", TestHardFailuresAsync),
    ("429 without cached usage remains a hard failure", TestRateLimitWithoutCacheAsync),
    ("persisted usage protects a rate-limited cold start", TestPersistedColdStartAsync),
    ("persisted usage protects other transient cold starts", TestPersistedTransientFailuresAsync),
    ("expired persisted usage is rejected", TestExpiredPersistedCacheAsync),
    ("credential changes invalidate persisted usage", TestCredentialChangeInvalidatesCacheAsync),
    ("transient backoff keeps its failure reason", TestTransientBackoffReasonAsync),
    ("file cache stores only the sanitized snapshot", TestFileCacheAsync),
    ("cold-start retry errors expose the next attempt", TestRetryErrorTextAsync),
    ("stale tooltip reports last success and next retry", TestStaleTooltipAsync)
};

foreach (var test in tests)
{
    await test.Run();
    Console.WriteLine($"PASS: {test.Name}");
}

return;

static async Task TestFreshCacheAsync()
{
    var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero));
    var handler = new SequenceHandler(SuccessResponse(10));
    using var client = CreateClient(handler, clock);

    var first = await client.GetUsageAsync(CancellationToken.None);
    clock.Advance(TimeSpan.FromMinutes(9));
    var cached = await client.GetUsageAsync(CancellationToken.None);

    Assert(handler.RequestCount == 1, "fresh cache should avoid a second HTTP request");
    Assert(!first.IsStale && !cached.IsStale, "fresh cache should not be marked stale");
    Assert(cached.Snapshot.Fable?.UsedPercent == 10, "cached Fable value should be preserved");
}

static async Task TestRateLimitRecoveryAsync()
{
    var start = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    var clock = new ManualTimeProvider(start);
    var handler = new SequenceHandler(
        SuccessResponse(10),
        StatusResponse(HttpStatusCode.TooManyRequests),
        StatusResponse(HttpStatusCode.TooManyRequests),
        StatusResponse(HttpStatusCode.TooManyRequests),
        SuccessResponse(22));
    using var client = CreateClient(handler, clock);

    await client.GetUsageAsync(CancellationToken.None);
    clock.Advance(TimeSpan.FromMinutes(10));
    var firstLimited = await client.GetUsageAsync(CancellationToken.None);
    Assert(firstLimited.IsStale, "a cached value should become stale after 429");
    Assert(firstLimited.RetryAfter == clock.GetUtcNow().AddMinutes(5), "first fallback backoff should be five minutes");
    Assert(firstLimited.Snapshot.Fable?.UsedPercent == 10, "429 should keep the last Fable value");

    var duringBackoff = await client.GetUsageAsync(CancellationToken.None);
    Assert(duringBackoff.IsStale, "cached value should remain stale during backoff");
    Assert(handler.RequestCount == 2, "backoff should suppress repeated HTTP requests");

    clock.Advance(TimeSpan.FromMinutes(5));
    var secondLimited = await client.GetUsageAsync(CancellationToken.None);
    Assert(secondLimited.RetryAfter == clock.GetUtcNow().AddMinutes(10), "second fallback backoff should be ten minutes");

    clock.Advance(TimeSpan.FromMinutes(10));
    var thirdLimited = await client.GetUsageAsync(CancellationToken.None);
    Assert(thirdLimited.RetryAfter == clock.GetUtcNow().AddMinutes(15), "third fallback backoff should be fifteen minutes");

    clock.Advance(TimeSpan.FromMinutes(15));
    var recovered = await client.GetUsageAsync(CancellationToken.None);
    Assert(!recovered.IsStale, "a successful retry should clear stale state");
    Assert(recovered.RetryAfter is null, "a successful retry should clear retry timing");
    Assert(recovered.Snapshot.Fable?.UsedPercent == 22, "recovery should replace the cached value");
    Assert(handler.RequestCount == 5, "recovery should use the next queued response");
}

static async Task TestRetryAfterAsync()
{
    var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero));
    var limited = StatusResponse(HttpStatusCode.TooManyRequests);
    limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromMinutes(30));
    var handler = new SequenceHandler(SuccessResponse(10), limited);
    using var client = CreateClient(handler, clock);

    await client.GetUsageAsync(CancellationToken.None);
    clock.Advance(TimeSpan.FromMinutes(10));
    var stale = await client.GetUsageAsync(CancellationToken.None);
    Assert(stale.RetryAfter == clock.GetUtcNow().AddMinutes(30), "Retry-After should be honored beyond the fallback cap");

    clock.Advance(TimeSpan.FromMinutes(29));
    await client.GetUsageAsync(CancellationToken.None);
    Assert(handler.RequestCount == 2, "Retry-After should suppress early requests");
}

static async Task TestServerErrorRecoveryAsync()
{
    var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero));
    var handler = new SequenceHandler(
        SuccessResponse(10),
        StatusResponse(HttpStatusCode.ServiceUnavailable),
        SuccessResponse(30));
    using var client = CreateClient(handler, clock);

    await client.GetUsageAsync(CancellationToken.None);
    clock.Advance(TimeSpan.FromMinutes(10));
    var stale = await client.GetUsageAsync(CancellationToken.None);
    Assert(stale.IsStale, "server errors should use the cached value");
    Assert(stale.RetryAfter == clock.GetUtcNow().AddMinutes(5), "server errors should back off for five minutes");

    clock.Advance(TimeSpan.FromMinutes(5));
    var recovered = await client.GetUsageAsync(CancellationToken.None);
    Assert(!recovered.IsStale && recovered.Snapshot.Fable?.UsedPercent == 30, "server recovery should refresh the value");
}

static async Task TestNetworkErrorRecoveryAsync()
{
    var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero));
    var handler = new SequenceHandler(
        SuccessResponse(10),
        new HttpRequestException("offline"),
        SuccessResponse(40));
    using var client = CreateClient(handler, clock);

    await client.GetUsageAsync(CancellationToken.None);
    clock.Advance(TimeSpan.FromMinutes(10));
    var stale = await client.GetUsageAsync(CancellationToken.None);
    Assert(stale.IsStale, "network errors should use the cached value");
    Assert(stale.RetryAfter == clock.GetUtcNow().AddMinutes(5), "network errors should back off for five minutes");

    clock.Advance(TimeSpan.FromMinutes(5));
    var recovered = await client.GetUsageAsync(CancellationToken.None);
    Assert(!recovered.IsStale && recovered.Snapshot.Fable?.UsedPercent == 40, "network recovery should refresh the value");
}

static async Task TestBodyReadErrorRecoveryAsync()
{
    var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero));
    var brokenResponse = new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new ThrowingContent(new HttpRequestException("body disconnected"))
    };
    var handler = new SequenceHandler(SuccessResponse(10), brokenResponse, SuccessResponse(45));
    using var client = CreateClient(handler, clock);

    await client.GetUsageAsync(CancellationToken.None);
    clock.Advance(TimeSpan.FromMinutes(10));
    var stale = await client.GetUsageAsync(CancellationToken.None);
    Assert(stale.IsStale, "body-read transport errors should use the cached value");
    Assert(stale.RetryAfter == clock.GetUtcNow().AddMinutes(5), "body-read errors should back off for five minutes");

    clock.Advance(TimeSpan.FromMinutes(5));
    var recovered = await client.GetUsageAsync(CancellationToken.None);
    Assert(!recovered.IsStale && recovered.Snapshot.Fable?.UsedPercent == 45, "body-read recovery should refresh the value");
}

static async Task TestRequestTimeoutRecoveryAsync()
{
    var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero));
    var handler = new SequenceHandler(
        SuccessResponse(10),
        StatusResponse(HttpStatusCode.RequestTimeout),
        SuccessResponse(50));
    using var client = CreateClient(handler, clock);

    await client.GetUsageAsync(CancellationToken.None);
    clock.Advance(TimeSpan.FromMinutes(10));
    var stale = await client.GetUsageAsync(CancellationToken.None);
    Assert(stale.IsStale, "HTTP 408 should use the cached value");

    clock.Advance(TimeSpan.FromMinutes(5));
    var recovered = await client.GetUsageAsync(CancellationToken.None);
    Assert(!recovered.IsStale && recovered.Snapshot.Fable?.UsedPercent == 50, "HTTP 408 recovery should refresh the value");
}

static async Task TestHardFailuresAsync()
{
    var start = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    var authClock = new ManualTimeProvider(start);
    var authHandler = new SequenceHandler(
        SuccessResponse(10),
        StatusResponse(HttpStatusCode.Unauthorized));
    using (var authClient = CreateClient(authHandler, authClock))
    {
        await authClient.GetUsageAsync(CancellationToken.None);
        authClock.Advance(TimeSpan.FromMinutes(10));
        await AssertThrowsAsync<UnauthorizedAccessException>(
            () => authClient.GetUsageAsync(CancellationToken.None),
            "authentication errors should not return stale data");
    }

    var schemaClock = new ManualTimeProvider(start);
    var schemaHandler = new SequenceHandler(
        SuccessResponse(10),
        JsonResponse("not-json"));
    using var schemaClient = CreateClient(schemaHandler, schemaClock);
    await schemaClient.GetUsageAsync(CancellationToken.None);
    schemaClock.Advance(TimeSpan.FromMinutes(10));
    await AssertThrowsAsync<System.Text.Json.JsonException>(
        () => schemaClient.GetUsageAsync(CancellationToken.None),
        "schema errors should not return stale data");

    var persistedSnapshot = new ClaudeUsageSnapshot(
        new ClaudeUsageWindow(10, start.AddHours(2)),
        new ClaudeUsageWindow(20, start.AddDays(3)),
        new ClaudeUsageWindow(30, start.AddDays(3)));
    var persistedAuthStore = new MemoryCacheStore
    {
        Entry = new ClaudeUsageCacheEntry(persistedSnapshot, start)
    };
    using (var persistedAuthClient = new ClaudeUsageClient(
        new SequenceHandler(
            StatusResponse(HttpStatusCode.Forbidden),
            StatusResponse(HttpStatusCode.ServiceUnavailable)),
        new ManualTimeProvider(start),
        () => "test-token",
        persistedAuthStore))
    {
        await AssertThrowsAsync<UnauthorizedAccessException>(
            () => persistedAuthClient.GetUsageAsync(CancellationToken.None),
            "a persisted snapshot should not hide authentication errors");
        Assert(persistedAuthStore.Entry is null, "authentication errors should delete persisted usage");
        await AssertThrowsAsync<ClaudeUsageTemporarilyUnavailableException>(
            () => persistedAuthClient.GetUsageAsync(CancellationToken.None),
            "a transient failure after authentication failure should not resurrect stale usage");
    }

    var persistedSchemaStore = new MemoryCacheStore
    {
        Entry = new ClaudeUsageCacheEntry(persistedSnapshot, start)
    };
    using var persistedSchemaClient = new ClaudeUsageClient(
        new SequenceHandler(
            JsonResponse("not-json"),
            StatusResponse(HttpStatusCode.TooManyRequests)),
        new ManualTimeProvider(start),
        () => "test-token",
        persistedSchemaStore);
    await AssertThrowsAsync<System.Text.Json.JsonException>(
        () => persistedSchemaClient.GetUsageAsync(CancellationToken.None),
        "a persisted snapshot should not hide schema errors");
    Assert(persistedSchemaStore.Entry is null, "schema errors should delete persisted usage");
    await AssertThrowsAsync<ClaudeUsageRateLimitedException>(
        () => persistedSchemaClient.GetUsageAsync(CancellationToken.None),
        "a rate limit after schema failure should not resurrect stale usage");
}

static async Task TestRateLimitWithoutCacheAsync()
{
    var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero));
    var handler = new SequenceHandler(StatusResponse(HttpStatusCode.TooManyRequests));
    using var client = CreateClient(handler, clock);

    try
    {
        await client.GetUsageAsync(CancellationToken.None);
        throw new InvalidOperationException("429 without a cached value should throw");
    }
    catch (ClaudeUsageRateLimitedException exception)
    {
        Assert(exception.RetryAfter == clock.GetUtcNow().AddMinutes(5), "hard failure should carry retry timing");
    }
}

static async Task TestPersistedColdStartAsync()
{
    var start = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    var clock = new ManualTimeProvider(start);
    var store = new MemoryCacheStore();
    using (var firstClient = new ClaudeUsageClient(
        new SequenceHandler(SuccessResponse(10)),
        clock,
        () => "test-token",
        store))
    {
        await firstClient.GetUsageAsync(CancellationToken.None);
    }

    Assert(store.Entry is not null, "a successful request should persist one sanitized snapshot");

    var limited = StatusResponse(HttpStatusCode.TooManyRequests);
    limited.Headers.RetryAfter = new System.Net.Http.Headers.RetryConditionHeaderValue(TimeSpan.FromHours(8));
    var limitedHandler = new SequenceHandler(limited);
    using var restartedClient = new ClaudeUsageClient(
        limitedHandler,
        clock,
        () => "test-token",
        store);

    var stale = await restartedClient.GetUsageAsync(CancellationToken.None);
    Assert(limitedHandler.RequestCount == 1, "persisted usage should not suppress the cold-start network request");
    Assert(stale.IsStale, "a cold-start 429 should use the persisted snapshot as stale data");
    Assert(stale.Snapshot.Fable?.UsedPercent == 10, "the persisted Fable value should remain visible");
    Assert(stale.LastUpdatedAt == start, "the tooltip should retain the original successful update time");
    Assert(stale.RetryAfter == start.AddHours(8), "the tooltip should expose the server retry time");
}

static async Task TestPersistedTransientFailuresAsync()
{
    var start = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    var snapshot = new ClaudeUsageSnapshot(
        new ClaudeUsageWindow(10, start.AddHours(2)),
        new ClaudeUsageWindow(20, start.AddDays(3)),
        new ClaudeUsageWindow(30, start.AddDays(3)));
    var outcomes = new (string Name, Func<object> Create)[]
    {
        ("network", () => new HttpRequestException("offline")),
        ("body", () => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ThrowingContent(new HttpRequestException("body disconnected"))
        }),
        ("408", () => StatusResponse(HttpStatusCode.RequestTimeout)),
        ("503", () => StatusResponse(HttpStatusCode.ServiceUnavailable))
    };

    foreach (var outcome in outcomes)
    {
        var store = new MemoryCacheStore
        {
            Entry = new ClaudeUsageCacheEntry(snapshot, start)
        };
        using var client = new ClaudeUsageClient(
            new SequenceHandler(outcome.Create()),
            new ManualTimeProvider(start),
            () => "test-token",
            store);
        var stale = await client.GetUsageAsync(CancellationToken.None);
        Assert(stale.IsStale, $"{outcome.Name} should use persisted usage during cold start");
        Assert(stale.Snapshot.Fable?.UsedPercent == 30, $"{outcome.Name} should preserve the Fable value");
    }
}

static async Task TestExpiredPersistedCacheAsync()
{
    var start = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    var snapshot = new ClaudeUsageSnapshot(
        new ClaudeUsageWindow(10, start.AddHours(2)),
        new ClaudeUsageWindow(20, start.AddDays(3)),
        new ClaudeUsageWindow(30, start.AddDays(3)));

    var oldStore = new MemoryCacheStore
    {
        Entry = new ClaudeUsageCacheEntry(snapshot, start.AddHours(-25))
    };
    using (var oldClient = new ClaudeUsageClient(
        new SequenceHandler(StatusResponse(HttpStatusCode.TooManyRequests)),
        new ManualTimeProvider(start),
        () => "test-token",
        oldStore))
    {
        await AssertThrowsAsync<ClaudeUsageRateLimitedException>(
            () => oldClient.GetUsageAsync(CancellationToken.None),
            "a snapshot older than 24 hours should not be shown");
    }
    Assert(oldStore.Entry is null, "an expired snapshot should be deleted");

    var resetSnapshot = snapshot with
    {
        Fable = new ClaudeUsageWindow(30, start.AddMinutes(-1))
    };
    var resetStore = new MemoryCacheStore
    {
        Entry = new ClaudeUsageCacheEntry(resetSnapshot, start.AddHours(-1))
    };
    using var resetClient = new ClaudeUsageClient(
        new SequenceHandler(StatusResponse(HttpStatusCode.TooManyRequests)),
        new ManualTimeProvider(start),
        () => "test-token",
        resetStore);
    await AssertThrowsAsync<ClaudeUsageRateLimitedException>(
        () => resetClient.GetUsageAsync(CancellationToken.None),
        "a snapshot past its Fable reset should not be shown");
    Assert(resetStore.Entry is null, "a snapshot past its Fable reset should be deleted");
}

static async Task TestCredentialChangeInvalidatesCacheAsync()
{
    var start = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
    var clock = new ManualTimeProvider(start);
    var credentialState = new ClaudeCredentialFileState("credentials-a", start.AddHours(-1), 100);
    var store = new MemoryCacheStore();
    var handler = new SequenceHandler(
        SuccessResponse(10),
        StatusResponse(HttpStatusCode.TooManyRequests));
    using var client = new ClaudeUsageClient(
        handler,
        clock,
        () => "test-token",
        () => credentialState,
        store);

    await client.GetUsageAsync(CancellationToken.None);
    Assert(store.Entry is not null, "the first credential should create a cache");
    var firstCredentialSnapshot = store.Entry!.Snapshot;

    credentialState = new ClaudeCredentialFileState("credentials-b", start.AddMinutes(1), 120);
    clock.Advance(TimeSpan.FromMinutes(1));
    await AssertThrowsAsync<ClaudeUsageRateLimitedException>(
        () => client.GetUsageAsync(CancellationToken.None),
        "a changed credential file should not reuse the previous account cache");
    Assert(store.Entry is null, "a credential-file change should delete the persisted cache");
    Assert(handler.RequestCount == 2, "a credential-file change should bypass the ten-minute memory cache");

    var startupStore = new MemoryCacheStore
    {
        Entry = new ClaudeUsageCacheEntry(firstCredentialSnapshot, start)
    };
    using var restartedClient = new ClaudeUsageClient(
        new SequenceHandler(StatusResponse(HttpStatusCode.TooManyRequests)),
        new ManualTimeProvider(start.AddMinutes(2)),
        () => "test-token",
        () => new ClaudeCredentialFileState("credentials-b", start.AddMinutes(1), 120),
        startupStore);
    await AssertThrowsAsync<ClaudeUsageRateLimitedException>(
        () => restartedClient.GetUsageAsync(CancellationToken.None),
        "a new process should reject cache older than the credential file");
    Assert(startupStore.Entry is null, "startup should delete cache older than the credential file");
}

static async Task TestTransientBackoffReasonAsync()
{
    var clock = new ManualTimeProvider(new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero));
    var handler = new SequenceHandler(new HttpRequestException("offline"));
    using var client = CreateClient(handler, clock);

    await AssertThrowsAsync<ClaudeUsageTemporarilyUnavailableException>(
        () => client.GetUsageAsync(CancellationToken.None),
        "the first network failure should be temporarily unavailable");
    await AssertThrowsAsync<ClaudeUsageTemporarilyUnavailableException>(
        () => client.GetUsageAsync(CancellationToken.None),
        "network backoff should not be relabeled as rate-limited");
    Assert(handler.RequestCount == 1, "the second call should be suppressed by transient backoff");
}

static Task TestFileCacheAsync()
{
    var temporaryDirectory = Path.Combine(
        Path.GetTempPath(),
        $"WeeklyUsageIndicator.Tests-{Guid.NewGuid():N}");
    Directory.CreateDirectory(temporaryDirectory);
    try
    {
        var cachePath = Path.Combine(temporaryDirectory, ClaudeUsageFileCacheStore.CacheFileName);
        var now = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
        var entry = new ClaudeUsageCacheEntry(
            new ClaudeUsageSnapshot(
                new ClaudeUsageWindow(10, now.AddHours(2)),
                new ClaudeUsageWindow(20, now.AddDays(3)),
                new ClaudeUsageWindow(30, now.AddDays(3))),
            now);
        var store = new ClaudeUsageFileCacheStore(cachePath);

        store.Save(entry);
        var json = File.ReadAllText(cachePath);
        using var document = System.Text.Json.JsonDocument.Parse(json);
        var fields = document.RootElement.EnumerateObject().Select(property => property.Name).ToArray();
        Assert(fields.SequenceEqual(["Snapshot", "LastUpdatedAt"]), "the cache should contain only snapshot and update time");
        Assert(store.Load() == entry, "the file cache should round-trip the sanitized snapshot");

        File.WriteAllText(cachePath, "not-json");
        Assert(store.Load() is null, "a corrupt cache should be ignored");
        Assert(!File.Exists(cachePath), "a corrupt cache should be deleted");

        File.WriteAllText(cachePath, $$"""
            {
              "Snapshot": null,
              "LastUpdatedAt": "{{now:O}}"
            }
            """);
        Assert(store.Load() is null, "a null snapshot should be rejected");
        Assert(!File.Exists(cachePath), "a semantically invalid cache should be deleted");

        File.WriteAllText(cachePath, $$"""
            {
              "Snapshot": {
                "FiveHour": null,
                "Weekly": null,
                "Fable": {}
              },
              "LastUpdatedAt": "{{now:O}}"
            }
            """);
        Assert(store.Load() is null, "a Fable window without an explicit percentage should be rejected");
    }
    finally
    {
        Directory.Delete(temporaryDirectory, recursive: true);
    }

    return Task.CompletedTask;
}

static Task TestRetryErrorTextAsync()
{
    var retryAfter = DateTimeOffset.Now.AddHours(8);
    var rateLimited = UsageIndicatorForm.FriendlyClaudeError(
        new ClaudeUsageRateLimitedException(retryAfter));
    var unavailable = UsageIndicatorForm.FriendlyClaudeError(
        new ClaudeUsageTemporarilyUnavailableException(retryAfter));

    Assert(rateLimited.Contains("다음 시도", StringComparison.Ordinal), "429 errors should show the next attempt");
    Assert(unavailable.Contains("다음 시도", StringComparison.Ordinal), "temporary errors should show the next attempt");
    return Task.CompletedTask;
}

static Task TestStaleTooltipAsync()
{
    var now = DateTimeOffset.Now;
    var snapshot = new ClaudeUsageSnapshot(
        new ClaudeUsageWindow(10, now.AddHours(2)),
        new ClaudeUsageWindow(20, now.AddDays(3)),
        new ClaudeUsageWindow(30, now.AddDays(3)));
    var stale = new ClaudeUsageResult(snapshot, now.AddMinutes(-12), now.AddMinutes(5), IsStale: true);
    var tooltip = UsageIndicatorForm.BuildTooltipText(null, stale, null, null, showClaude: true);

    Assert(tooltip.Contains("업데이트 지연", StringComparison.Ordinal), "tooltip should identify delayed updates");
    Assert(tooltip.Contains("마지막 성공", StringComparison.Ordinal), "tooltip should show last success");
    Assert(tooltip.Contains("다음 시도", StringComparison.Ordinal), "tooltip should show next retry");
    Assert(tooltip.Contains("Fable: 70% 남음", StringComparison.Ordinal), "tooltip should retain cached Fable usage");

    var failedTooltip = UsageIndicatorForm.BuildTooltipText(
        null,
        stale,
        null,
        "hard failure",
        showClaude: true);
    Assert(!failedTooltip.Contains("Fable: 70% 남음", StringComparison.Ordinal), "hard failures should exclude rejected stale values");
    return Task.CompletedTask;
}

static ClaudeUsageClient CreateClient(SequenceHandler handler, ManualTimeProvider clock) =>
    new(handler, clock, () => "test-token");

static HttpResponseMessage SuccessResponse(double fableUsedPercent)
{
    var json = $$"""
        {
          "five_hour": { "utilization": 10, "resets_at": "2026-08-30T14:00:00Z" },
          "seven_day": { "utilization": 20, "resets_at": "2026-09-03T12:00:00Z" },
          "limits": [
            {
              "kind": "weekly_scoped",
              "percent": {{fableUsedPercent}},
              "resets_at": "2026-09-03T12:00:00Z",
              "scope": { "model": { "display_name": "Fable" } }
            }
          ]
        }
        """;
    return new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(json, Encoding.UTF8, "application/json")
    };
}

static HttpResponseMessage StatusResponse(HttpStatusCode statusCode) => new(statusCode)
{
    Content = new StringContent("{}", Encoding.UTF8, "application/json")
};

static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
{
    Content = new StringContent(json, Encoding.UTF8, "application/json")
};

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

internal sealed class SequenceHandler(params object[] outcomes) : HttpMessageHandler
{
    private readonly Queue<object> _outcomes = new(outcomes);

    public int RequestCount { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestCount++;
        if (_outcomes.Count == 0)
            throw new InvalidOperationException("No queued HTTP outcome remains.");

        return _outcomes.Dequeue() switch
        {
            HttpResponseMessage response => Task.FromResult(response),
            Exception exception => Task.FromException<HttpResponseMessage>(exception),
            _ => throw new InvalidOperationException("Unsupported HTTP outcome type.")
        };
    }
}

internal sealed class ThrowingContent(Exception exception) : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        Task.FromException(exception);

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
