namespace WeeklyUsageIndicator;

internal sealed record ClaudeUsageWindow(
    double UsedPercent,
    DateTimeOffset? ResetsAt);

internal sealed record ClaudeUsageSnapshot(
    ClaudeUsageWindow? FiveHour,
    ClaudeUsageWindow? Weekly,
    ClaudeUsageWindow? Fable);

internal sealed record ClaudeUsageResult(
    ClaudeUsageSnapshot Snapshot,
    DateTimeOffset LastUpdatedAt,
    DateTimeOffset? RetryAfter,
    bool IsStale);

internal sealed class ClaudeUsageTemporarilyUnavailableException(
    DateTimeOffset retryAfter,
    Exception? innerException = null)
    : IOException("Claude Code usage is temporarily unavailable.", innerException)
{
    public DateTimeOffset RetryAfter { get; } = retryAfter;
}

internal sealed class ClaudeUsageClient : IDisposable
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PersistentCacheLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan DefaultBackoff = TimeSpan.FromMinutes(5);

    private readonly IClaudeUsageSource _usageSource;
    private readonly TimeProvider _timeProvider;
    private readonly IClaudeUsageCacheStore _cacheStore;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private ClaudeUsageSnapshot? _cachedSnapshot;
    private DateTimeOffset _lastFetchedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;
    private int _consecutiveTransientFailures;
    private bool _hasLiveSnapshot;
    private bool _disposed;

    public ClaudeUsageClient()
        : this(
            new ClaudeCodeUsageSource(),
            TimeProvider.System,
            ClaudeUsageFileCacheStore.CreateDefault())
    {
    }

    internal ClaudeUsageClient(
        IClaudeUsageSource usageSource,
        TimeProvider timeProvider,
        IClaudeUsageCacheStore? cacheStore = null)
    {
        _usageSource = usageSource;
        _timeProvider = timeProvider;
        _cacheStore = cacheStore ?? ClaudeUsageNullCacheStore.Instance;

        var persisted = _cacheStore.Load();
        var now = _timeProvider.GetUtcNow();
        if (persisted is not null &&
            IsCacheUsable(persisted.Snapshot, persisted.LastUpdatedAt, now))
        {
            _cachedSnapshot = persisted.Snapshot;
            _lastFetchedAt = persisted.LastUpdatedAt;
        }
        else if (persisted is not null)
        {
            _cacheStore.Clear();
        }
    }

    public async Task<ClaudeUsageResult> GetUsageAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _requestGate.WaitAsync(cancellationToken);
        try
        {
            var now = _timeProvider.GetUtcNow();
            if (now < _retryAfter)
            {
                return HasUsableCache(now)
                    ? CreateCachedResult(isStale: true)
                    : throw new ClaudeUsageTemporarilyUnavailableException(_retryAfter);
            }

            if (_hasLiveSnapshot &&
                HasUsableCache(now) &&
                now - _lastFetchedAt < CacheLifetime)
            {
                return CreateCachedResult(isStale: false);
            }

            ClaudeUsageSnapshot snapshot;
            try
            {
                snapshot = await _usageSource.ReadAsync(cancellationToken);
            }
            catch (Exception exception) when (IsTransientException(exception, cancellationToken))
            {
                SetTransientBackoff(now);
                return HasUsableCache(now)
                    ? CreateCachedResult(isStale: true)
                    : throw new ClaudeUsageTemporarilyUnavailableException(_retryAfter, exception);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                // A caller timeout or shutdown must not invalidate a previously accepted snapshot.
                throw;
            }
            catch
            {
                ClearCachedSnapshot(clearPersisted: true);
                throw;
            }

            var fetchedAt = _timeProvider.GetUtcNow();
            if (!IsCacheUsable(snapshot, fetchedAt, fetchedAt))
            {
                ClearCachedSnapshot(clearPersisted: true);
                throw new InvalidDataException("Claude Code did not return a usable Fable limit.");
            }

            _cachedSnapshot = snapshot;
            _lastFetchedAt = fetchedAt;
            _retryAfter = DateTimeOffset.MinValue;
            _consecutiveTransientFailures = 0;
            _hasLiveSnapshot = true;

            _cacheStore.Save(new ClaudeUsageCacheEntry(snapshot, fetchedAt));
            return new ClaudeUsageResult(snapshot, fetchedAt, null, IsStale: false);
        }
        finally
        {
            _requestGate.Release();
        }
    }

    private ClaudeUsageResult CreateCachedResult(bool isStale) => new(
        _cachedSnapshot!,
        _lastFetchedAt,
        isStale ? _retryAfter : null,
        isStale);

    private bool HasUsableCache(DateTimeOffset now)
    {
        if (_cachedSnapshot is null || !IsCacheUsable(_cachedSnapshot, _lastFetchedAt, now))
        {
            ClearCachedSnapshot(clearPersisted: true);
            return false;
        }

        return true;
    }

    private static bool IsCacheUsable(
        ClaudeUsageSnapshot? snapshot,
        DateTimeOffset lastUpdatedAt,
        DateTimeOffset now)
    {
        var age = now - lastUpdatedAt;
        if (age < TimeSpan.Zero ||
            age > PersistentCacheLifetime ||
            snapshot?.Fable is not { } fable ||
            !IsValidPercent(fable.UsedPercent))
        {
            return false;
        }

        return fable.ResetsAt is not { } resetsAt || resetsAt > now;
    }

    private static bool IsValidPercent(double value) =>
        double.IsFinite(value) && value is >= 0d and <= 100d;

    private void ClearCachedSnapshot(bool clearPersisted)
    {
        _cachedSnapshot = null;
        _lastFetchedAt = DateTimeOffset.MinValue;
        _hasLiveSnapshot = false;
        if (clearPersisted) _cacheStore.Clear();
    }

    private void SetTransientBackoff(DateTimeOffset now)
    {
        _consecutiveTransientFailures++;
        var multiplier = Math.Pow(2, Math.Min(_consecutiveTransientFailures - 1, 2));
        var exponential = TimeSpan.FromTicks((long)(DefaultBackoff.Ticks * multiplier));
        var duration = exponential > TimeSpan.FromMinutes(15)
            ? TimeSpan.FromMinutes(15)
            : exponential;
        _retryAfter = now + duration;
    }

    private static bool IsTransientException(
        Exception exception,
        CancellationToken callerCancellationToken) =>
        exception is IOException and not FileNotFoundException or TimeoutException ||
        exception is OperationCanceledException && !callerCancellationToken.IsCancellationRequested;

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _requestGate.Dispose();
    }
}
