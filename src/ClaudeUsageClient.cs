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

internal sealed class ClaudeUsageRequestThrottle
{
    internal static readonly TimeSpan MinimumInterval = TimeSpan.FromMinutes(10);
    internal static ClaudeUsageRequestThrottle Shared { get; } = new();
    private readonly object _gate = new();
    private readonly Dictionary<string, DateTimeOffset> _nextAllowedByAccount = new(StringComparer.Ordinal);

    internal bool TryStart(string accountKey, DateTimeOffset now, out DateTimeOffset retryAfter)
    {
        lock (_gate)
        {
            retryAfter = _nextAllowedByAccount.GetValueOrDefault(accountKey, DateTimeOffset.MinValue);
            if (now < retryAfter) return false;
            _nextAllowedByAccount[accountKey] = now + MinimumInterval;
            return true;
        }
    }

    internal void DeferUntil(string accountKey, DateTimeOffset retryAfter)
    {
        lock (_gate)
        {
            if (retryAfter > _nextAllowedByAccount.GetValueOrDefault(accountKey, DateTimeOffset.MinValue))
                _nextAllowedByAccount[accountKey] = retryAfter;
        }
    }
}

internal sealed class ClaudeUsageClient : IDisposable
{
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan PersistentCacheLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan DefaultBackoff = TimeSpan.FromMinutes(5);

    private readonly IClaudeUsageSource _usageSource;
    private readonly TimeProvider _timeProvider;
    private readonly IClaudeUsageCacheStore _cacheStore;
    private readonly string _profileKey;
    private readonly string _accountKey;
    private readonly ClaudeUsageRequestThrottle _throttle;
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
            ClaudeUsageCacheEntry.DefaultProfileKey, ClaudeUsageCacheEntry.DefaultProfileKey)
    {
    }

    internal ClaudeUsageClient(IClaudeUsageSource usageSource, string profileKey, string accountKey)
        : this(usageSource, TimeProvider.System, ClaudeUsageFileCacheStore.CreateDefault().For(profileKey),
            profileKey, ClaudeUsageRequestThrottle.Shared, accountKey)
    {
    }

    internal ClaudeUsageClient(
        IClaudeUsageSource usageSource,
        TimeProvider timeProvider,
        IClaudeUsageCacheStore? cacheStore = null,
        string profileKey = ClaudeUsageCacheEntry.DefaultProfileKey,
        ClaudeUsageRequestThrottle? throttle = null,
        string? accountKey = null)
    {
        if (string.IsNullOrWhiteSpace(profileKey)) throw new ArgumentException("A Claude profile key is required.", nameof(profileKey));
        if (accountKey is not null && string.IsNullOrWhiteSpace(accountKey))
            throw new ArgumentException("A Claude account key cannot be empty.", nameof(accountKey));
        _usageSource = usageSource;
        _timeProvider = timeProvider;
        _cacheStore = cacheStore ?? ClaudeUsageNullCacheStore.Instance;
        _profileKey = profileKey;
        _accountKey = accountKey ?? profileKey;
        _throttle = throttle ?? new ClaudeUsageRequestThrottle();

        var persisted = _cacheStore.Load();
        var now = _timeProvider.GetUtcNow();
        if (persisted is not null && persisted.ProfileKey == _profileKey &&
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

            // Verified account identity owns the interval, not the profile row.
            // Other accounts can read immediately; duplicate profiles and
            // recreated clients retain the same failed/canceled attempt limit.
            if (!_throttle.TryStart(_accountKey, now, out var nextAllowedAt))
            {
                _retryAfter = nextAllowedAt;
                return HasUsableCache(now)
                    ? CreateCachedResult(isStale: true)
                    : throw new ClaudeUsageTemporarilyUnavailableException(_retryAfter);
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
                throw new InvalidDataException("Claude Code did not return a usable usage limit.");
            }

            _cachedSnapshot = snapshot;
            _lastFetchedAt = fetchedAt;
            _retryAfter = DateTimeOffset.MinValue;
            _consecutiveTransientFailures = 0;
            _hasLiveSnapshot = true;

            _cacheStore.Save(new ClaudeUsageCacheEntry(snapshot, fetchedAt, _profileKey));
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
            snapshot is null)
        {
            return false;
        }

        return ClaudeDisplayMetrics.IsUsable(snapshot.FiveHour, now) ||
            ClaudeDisplayMetrics.IsUsable(snapshot.Weekly, now) ||
            ClaudeDisplayMetrics.IsUsable(snapshot.Fable, now);
    }

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
        if (duration < ClaudeUsageRequestThrottle.MinimumInterval)
            duration = ClaudeUsageRequestThrottle.MinimumInterval;
        _retryAfter = now + duration;
        _throttle.DeferUntil(_accountKey, _retryAfter);
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
