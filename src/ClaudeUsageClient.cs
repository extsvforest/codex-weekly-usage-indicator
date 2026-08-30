using System.Globalization;
using System.Net;
using System.Text.Json;

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

internal sealed class ClaudeUsageRateLimitedException(DateTimeOffset retryAfter)
    : HttpRequestException("Claude usage service is temporarily rate-limited.")
{
    public DateTimeOffset RetryAfter { get; } = retryAfter;
}

internal sealed class ClaudeUsageClient : IDisposable
{
    private static readonly Uri UsageEndpoint = new("https://api.anthropic.com/api/oauth/usage");
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan DefaultBackoff = TimeSpan.FromMinutes(5);

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly Func<string> _accessTokenProvider;
    private readonly SemaphoreSlim _requestGate = new(1, 1);
    private ClaudeUsageSnapshot? _cachedSnapshot;
    private DateTimeOffset _lastFetchedAt = DateTimeOffset.MinValue;
    private DateTimeOffset _retryAfter = DateTimeOffset.MinValue;
    private int _consecutiveRateLimits;
    private bool _disposed;

    public ClaudeUsageClient()
        : this(new HttpClientHandler(), TimeProvider.System, ReadAccessToken)
    {
    }

    internal ClaudeUsageClient(
        HttpMessageHandler messageHandler,
        TimeProvider timeProvider,
        Func<string> accessTokenProvider)
    {
        _httpClient = new HttpClient(messageHandler)
        {
            Timeout = TimeSpan.FromSeconds(15)
        };
        _timeProvider = timeProvider;
        _accessTokenProvider = accessTokenProvider;
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
                return _cachedSnapshot is not null
                    ? CreateCachedResult(isStale: true)
                    : throw new ClaudeUsageRateLimitedException(_retryAfter);
            }

            if (_cachedSnapshot is not null &&
                now - _lastFetchedAt < CacheLifetime)
            {
                return CreateCachedResult(isStale: false);
            }

            var accessToken = _accessTokenProvider();
            using var request = new HttpRequestMessage(HttpMethod.Get, UsageEndpoint);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
            request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
            request.Headers.TryAddWithoutValidation("User-Agent", "codex-claude-weekly-usage-indicator");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");

            HttpResponseMessage response;
            try
            {
                response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
            }
            catch (Exception exception) when (
                IsTransientException(exception) &&
                _cachedSnapshot is not null)
            {
                SetTransientBackoff(now);
                return CreateCachedResult(isStale: true);
            }

            using (response)
            {
                if (response.StatusCode == HttpStatusCode.TooManyRequests)
                {
                    _consecutiveRateLimits++;
                    _retryAfter = ResolveRetryAfter(response, now, _consecutiveRateLimits);
                    return _cachedSnapshot is not null
                        ? CreateCachedResult(isStale: true)
                        : throw new ClaudeUsageRateLimitedException(_retryAfter);
                }

                if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
                    throw new UnauthorizedAccessException("Claude Code login is missing or expired.");

                if (response.StatusCode == HttpStatusCode.RequestTimeout ||
                    (int)response.StatusCode >= 500)
                {
                    SetTransientBackoff(now);
                    return _cachedSnapshot is not null
                        ? CreateCachedResult(isStale: true)
                        : throw new HttpRequestException(
                            $"Claude usage service returned HTTP {(int)response.StatusCode}.");
                }

                if (!response.IsSuccessStatusCode)
                    throw new HttpRequestException(
                        $"Claude usage service returned HTTP {(int)response.StatusCode}.");

                string json;
                try
                {
                    json = await response.Content.ReadAsStringAsync(cancellationToken);
                }
                catch (Exception exception) when (
                    IsTransientException(exception) &&
                    _cachedSnapshot is not null)
                {
                    SetTransientBackoff(now);
                    return CreateCachedResult(isStale: true);
                }

                var snapshot = ParseUsage(json);
                _cachedSnapshot = snapshot;
                _lastFetchedAt = now;
                _retryAfter = DateTimeOffset.MinValue;
                _consecutiveRateLimits = 0;
                return new ClaudeUsageResult(snapshot, now, null, IsStale: false);
            }
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

    private void SetTransientBackoff(DateTimeOffset now)
    {
        _retryAfter = now + DefaultBackoff;
    }

    private static bool IsTransientException(Exception exception) =>
        exception is HttpRequestException or TaskCanceledException;

    internal static ClaudeUsageSnapshot ParseUsage(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Claude returned an invalid usage response.");

        var fiveHour = ReadRootWindow(root, "five_hour") ?? ReadLimit(root, "session");
        var weekly = ReadRootWindow(root, "seven_day") ?? ReadLimit(root, "weekly_all");
        var fable = ReadScopedFableLimit(root);

        if (fiveHour is null && weekly is null && fable is null)
            throw new InvalidDataException("Claude did not return any usage limits.");

        return new ClaudeUsageSnapshot(fiveHour, weekly, fable);
    }

    private static ClaudeUsageWindow? ReadRootWindow(JsonElement root, string propertyName)
    {
        if (!root.TryGetProperty(propertyName, out var window) ||
            window.ValueKind != JsonValueKind.Object ||
            !TryReadNumber(window, "utilization", out var usedPercent))
        {
            return null;
        }

        return new ClaudeUsageWindow(
            Math.Clamp(usedPercent, 0d, 100d),
            ReadResetTime(window));
    }

    private static ClaudeUsageWindow? ReadLimit(JsonElement root, string expectedKind)
    {
        foreach (var limit in EnumerateLimits(root))
        {
            if (!TryReadString(limit, "kind", out var kind) ||
                !kind.Equals(expectedKind, StringComparison.OrdinalIgnoreCase) ||
                !TryReadNumber(limit, "percent", out var usedPercent))
            {
                continue;
            }

            return new ClaudeUsageWindow(
                Math.Clamp(usedPercent, 0d, 100d),
                ReadResetTime(limit));
        }

        return null;
    }

    private static ClaudeUsageWindow? ReadScopedFableLimit(JsonElement root)
    {
        foreach (var limit in EnumerateLimits(root))
        {
            if (!TryReadString(limit, "kind", out var kind) ||
                !kind.Equals("weekly_scoped", StringComparison.OrdinalIgnoreCase) ||
                !TryReadNumber(limit, "percent", out var usedPercent) ||
                !TryReadScopedModelName(limit, out var displayName) ||
                !displayName.Contains("fable", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return new ClaudeUsageWindow(
                Math.Clamp(usedPercent, 0d, 100d),
                ReadResetTime(limit));
        }

        return null;
    }

    private static IEnumerable<JsonElement> EnumerateLimits(JsonElement root)
    {
        if (!root.TryGetProperty("limits", out var limits) || limits.ValueKind != JsonValueKind.Array)
            yield break;

        foreach (var limit in limits.EnumerateArray())
        {
            if (limit.ValueKind == JsonValueKind.Object)
                yield return limit;
        }
    }

    private static bool TryReadScopedModelName(JsonElement limit, out string displayName)
    {
        displayName = string.Empty;
        return limit.TryGetProperty("scope", out var scope) &&
            scope.ValueKind == JsonValueKind.Object &&
            scope.TryGetProperty("model", out var model) &&
            model.ValueKind == JsonValueKind.Object &&
            TryReadString(model, "display_name", out displayName);
    }

    private static bool TryReadNumber(JsonElement element, string propertyName, out double value)
    {
        value = 0;
        return element.TryGetProperty(propertyName, out var property) &&
            property.ValueKind == JsonValueKind.Number &&
            property.TryGetDouble(out value) &&
            double.IsFinite(value);
    }

    private static bool TryReadString(JsonElement element, string propertyName, out string value)
    {
        value = string.Empty;
        if (!element.TryGetProperty(propertyName, out var property) || property.ValueKind != JsonValueKind.String)
            return false;

        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static DateTimeOffset? ReadResetTime(JsonElement element)
    {
        if (!TryReadString(element, "resets_at", out var rawReset)) return null;
        return DateTimeOffset.TryParse(
            rawReset,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
            out var reset)
            ? reset.ToLocalTime()
            : null;
    }

    private static string ReadAccessToken()
    {
        var configuredDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var claudeDirectory = string.IsNullOrWhiteSpace(configuredDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : configuredDirectory;
        var credentialPath = Path.Combine(claudeDirectory, ".credentials.json");

        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(credentialPath));
            var root = document.RootElement;
            if (root.TryGetProperty("claudeAiOauth", out var oauth) &&
                oauth.ValueKind == JsonValueKind.Object &&
                TryReadString(oauth, "accessToken", out var accessToken))
            {
                return accessToken;
            }
        }
        catch (FileNotFoundException)
        {
            throw new InvalidOperationException("Claude Code credentials were not found.");
        }
        catch (DirectoryNotFoundException)
        {
            throw new InvalidOperationException("Claude Code credentials were not found.");
        }
        catch (JsonException)
        {
            throw new InvalidDataException("Claude Code credentials are not valid JSON.");
        }

        throw new InvalidOperationException("Claude Code login is required.");
    }

    private static DateTimeOffset ResolveRetryAfter(
        HttpResponseMessage response,
        DateTimeOffset now,
        int consecutiveRateLimits)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta && delta > TimeSpan.Zero)
            return AddBackoffSafely(now, delta);
        if (retryAfter?.Date is { } date && date > now)
            return date;

        var multiplier = Math.Pow(2, Math.Min(Math.Max(0, consecutiveRateLimits - 1), 2));
        return now + ClampBackoff(TimeSpan.FromTicks((long)(DefaultBackoff.Ticks * multiplier)));
    }

    private static DateTimeOffset AddBackoffSafely(DateTimeOffset now, TimeSpan backoff) =>
        backoff >= DateTimeOffset.MaxValue - now ? DateTimeOffset.MaxValue : now + backoff;

    private static TimeSpan ClampBackoff(TimeSpan value)
    {
        var minimum = TimeSpan.FromMinutes(1);
        var maximum = TimeSpan.FromMinutes(15);
        return value < minimum ? minimum : value > maximum ? maximum : value;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _httpClient.Dispose();
        _requestGate.Dispose();
    }
}
