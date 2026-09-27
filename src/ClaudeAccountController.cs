namespace WeeklyUsageIndicator;

internal sealed class ClaudeUsageObservedEventArgs(string profileId) : EventArgs
{ internal string ProfileId { get; } = profileId; }

// One profile selection owns both new interactive launches and widget reads.
// Credentials remain entirely inside the unmodified official Claude CLI.
internal sealed class ClaudeAccountController : IDisposable
{
    private static readonly TimeSpan ObservationLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan StatusRetryDelay = TimeSpan.FromMinutes(1);
    internal ClaudeProfileStore Store { get; }
    internal ClaudeProfileRuntime Runtime { get; }
    private readonly Dictionary<string, ClaudeUsageClient> _clients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClaudeUsageResult> _observations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ObservationBinding> _observationBindings = new(StringComparer.Ordinal);
    private readonly object _observationGate = new();
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeProvider _timeProvider;
    private readonly Func<ClaudeProfile, string, ClaudeUsageClient> _clientFactory;
    private readonly Action _clearRecoveryCache;
    private volatile bool _disposed;
    internal event EventHandler? SelectionChanged;
    internal event EventHandler<ClaudeUsageObservedEventArgs>? UsageChanged;
    internal string? LastWorkingDirectory { get; set; }
    internal ClaudeProfile SelectedProfile => Store.SelectedProfile ?? Store.DefaultProfile;
    internal string SelectionKey => SelectedProfile.Id;

    internal ClaudeAccountController(ClaudeProfileStore? store = null, ClaudeProfileRuntime? runtime = null,
        TimeProvider? timeProvider = null, Func<ClaudeProfile, string, ClaudeUsageClient>? clientFactory = null,
        Action? clearRecoveryCache = null)
    {
        Store = store ?? new();
        Runtime = runtime ?? new(Store);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _clientFactory = clientFactory ?? ((profile, key) =>
        {
            var runner = new ClaudeCodeProcessRunner(configureProcess: start =>
                ClaudeProfileRuntime.ApplyProfileEnvironment(start, profile));
            return new ClaudeUsageClient(new ClaudeCodeUsageSource(runner, _timeProvider), key);
        });
        _clearRecoveryCache = clearRecoveryCache ?? (() => ClaudeUsageFileCacheStore.CreateDefault().Clear());
    }

    internal ClaudeUsageResult? LastUsage(string profileId)
    {
        lock (_observationGate)
            return _observations.TryGetValue(profileId, out var result)
                ? UsableObservation(result, _timeProvider.GetUtcNow()) : null;
    }

    internal async Task<ClaudeProfile> RegisterDefaultAsync(CancellationToken token)
    {
        using var operation = BeginOperation(token);
        await _gate.WaitAsync(operation.Token);
        try
        {
            var profile = Store.DefaultProfile;
            var identity = await Runtime.VerifyAsync(profile, operation.Token);
            var previous = Store.ListProfiles().SingleOrDefault(saved => saved.Id == profile.Id);
            var changedIdentity = previous?.Identity is { } stored && stored.Key != identity.Key;
            lock (_observationGate)
                changedIdentity |= _observationBindings.TryGetValue(profile.Id, out var binding)
                    && !binding.Matches(profile, identity.Key);
            // Connecting the already-used default account only adds its row and label. Keep its
            // accepted usage/client: dropping them would leave it blank until the global throttle expires.
            if (changedIdentity) Forget(profile.Id);
            return Store.RegisterDefault(UiText.T("기존 Claude 계정"), identity);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidDataException)
        { Forget(Store.DefaultProfile.Id); throw; }
        finally { _gate.Release(); }
    }

    internal async Task<ClaudeProfile> AddAsync(string label, CancellationToken token)
    {
        using var operation = BeginOperation(token);
        await _gate.WaitAsync(operation.Token);
        try
        {
            var profile = Store.CreateProfile(label);
            var identity = await Runtime.LoginAsync(profile, operation.Token);
            return Store.RecordVerifiedIdentity(profile.Id, identity);
        }
        finally { _gate.Release(); }
    }

    internal async Task LoginAsync(ClaudeProfile profile, CancellationToken token)
    {
        using var operation = BeginOperation(token);
        await _gate.WaitAsync(operation.Token);
        try
        {
            // This profile's login is about to change. Other profiles may continue displaying their
            // previously verified usage while the official login terminal is waiting for the user.
            Forget(profile.Id);
            var identity = await Runtime.LoginAsync(profile, operation.Token);
            Store.RecordVerifiedIdentity(profile.Id, identity);
        }
        finally { _gate.Release(); }
    }

    internal async Task SelectAsync(ClaudeProfile profile, CancellationToken token)
    {
        using var operation = BeginOperation(token);
        await _gate.WaitAsync(operation.Token);
        try
        {
            var identity = await Runtime.VerifyAsync(profile, operation.Token);
            // Both modes must identify the same subscription. The usage command
            // ignores extensions and launch settings; selection cannot conceal that.
            var usageIdentity = await Runtime.VerifyAsync(profile, operation.Token, safeMode: true);
            if (identity.Key != usageIdentity.Key)
                throw new InvalidOperationException("Claude 실행과 사용량 조회의 계정이 다릅니다. 로그인 설정을 확인해 주세요.");
            Store.Select(profile.Id, identity);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidDataException)
        { Forget(profile.Id); throw; }
        finally { _gate.Release(); }
        if (!_disposed) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    internal async Task<ClaudeUsageResult> ReadUsageAsync(string? profileId, CancellationToken token,
        TimeSpan? timeout = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        // Bind a background request to the selection it started with. A concurrent completed switch
        // cannot make its timeout return another profile's observation under the old request.
        var requestedProfile = ResolveProfile(profileId);
        using var deadline = timeout is { } duration ? new CancellationTokenSource(duration, _timeProvider) : null;
        using var operation = BeginOperation(token, deadline?.Token ?? CancellationToken.None);
        var entered = false;
        try
        {
            await _gate.WaitAsync(operation.Token);
            entered = true;
            var profile = ResolveProfile(profileId);
            if (profile.Id != requestedProfile.Id)
                throw new OperationCanceledException("Claude account selection changed during the request.");
            requestedProfile = profile;
            var identity = await Runtime.VerifyAsync(profile, operation.Token, safeMode: true);
            if (profile.Identity is not null && profile.Identity.Key != identity.Key)
                throw new UnauthorizedAccessException("Claude 프로필의 로그인 계정이 바뀌었습니다. 계정 목록에서 다시 확인해 주세요.");
            var key = profile.Id + ":" + identity.Key;
            // Once verification identifies a different account, an older observation is no longer a
            // fallback, even if the subsequent usage request fails transiently.
            bool changedIdentity;
            lock (_observationGate)
                changedIdentity = _observationBindings.TryGetValue(profile.Id, out var binding)
                    && !binding.Matches(profile, identity.Key);
            if (changedIdentity) Forget(profile.Id);
            foreach (var previous in _clients.Keys.Where(value => value.StartsWith(profile.Id + ":", StringComparison.Ordinal)
                         && value != key).ToArray())
            { _clients[previous].Dispose(); _clients.Remove(previous); }
            if (!_clients.TryGetValue(key, out var client))
            {
                client = _clientFactory(profile, key);
                _clients.Add(key, client);
            }
            var result = UsableObservation(await client.GetUsageAsync(operation.Token), _timeProvider.GetUtcNow())
                ?? throw new InvalidDataException("Claude Code did not return a usable usage limit.");
            Publish(profile, identity.Key, result);
            return result;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidDataException)
        {
            Forget(requestedProfile.Id);
            throw;
        }
        catch (Exception ex) when (IsTransientStatusFailure(ex)
                                   || ex is OperationCanceledException && deadline?.IsCancellationRequested == true
                                   && !token.IsCancellationRequested && !_lifetime.IsCancellationRequested)
        {
            token.ThrowIfCancellationRequested();
            _lifetime.Token.ThrowIfCancellationRequested();
            var retryAfter = ex is ClaudeUsageTemporarilyUnavailableException unavailable
                ? unavailable.RetryAfter : _timeProvider.GetUtcNow() + StatusRetryDelay;
            if (TryUseStaleObservation(requestedProfile, profileId is null, retryAfter, out var cached)) return cached;
            throw new ClaudeUsageTemporarilyUnavailableException(retryAfter, ex);
        }
        finally { if (entered) _gate.Release(); }
    }

    internal async Task LogoutAsync(ClaudeProfile profile, CancellationToken token)
    {
        using var operation = BeginOperation(token);
        await _gate.WaitAsync(operation.Token);
        var wasSelected = false;
        try
        {
            wasSelected = profile.Id == SelectedProfile.Id;
            Forget(profile.Id);
            await Runtime.LogoutAsync(profile, operation.Token);
            Store.MarkLoggedOut(profile.Id);
        }
        finally { _gate.Release(); }
        if (wasSelected && !_disposed) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Forget(string id)
    {
        InvalidateObservation(id);
        foreach (var key in _clients.Keys.Where(key => key.StartsWith(id + ":", StringComparison.Ordinal)).ToArray())
        { _clients[key].Dispose(); _clients.Remove(key); }
        _clearRecoveryCache();
    }

    private void InvalidateObservation(string id)
    {
        lock (_observationGate)
        { _observations.Remove(id); _observationBindings.Remove(id); }
        // Null is an explicit invalidation, not an absent update. A selected widget must clear it now.
        if (!_disposed) UsageChanged?.Invoke(this, new ClaudeUsageObservedEventArgs(id));
    }

    private ClaudeProfile ResolveProfile(string? id) => id is null ? SelectedProfile
        : Store.ListProfiles().Single(profile => profile.Id == id);

    private bool TryUseStaleObservation(ClaudeProfile requestedProfile, bool selectedRequest,
        DateTimeOffset retryAfter, out ClaudeUsageResult cached)
    {
        cached = null!;
        var current = selectedRequest ? SelectedProfile
            : Store.ListProfiles().SingleOrDefault(profile => profile.Id == requestedProfile.Id);
        if (current is null || current.Id != requestedProfile.Id) return false;
        var now = _timeProvider.GetUtcNow();
        var invalid = false;
        var hadObservation = false;
        lock (_observationGate)
        {
            hadObservation = _observations.ContainsKey(current.Id);
            if (!_observationBindings.TryGetValue(current.Id, out var binding)
                || !binding.Matches(current, current.Identity?.Key ?? binding.IdentityKey)
                || !_observations.TryGetValue(current.Id, out var observation)
                || UsableObservation(observation, now) is not { } usable) invalid = true;
            else
            {
                if (retryAfter <= now) retryAfter = now + StatusRetryDelay;
                if (usable.RetryAfter is { } existingRetry && existingRetry > retryAfter) retryAfter = existingRetry;
                cached = usable with { IsStale = true, RetryAfter = retryAfter };
                _observations[current.Id] = cached;
            }
        }
        if (invalid)
        {
            if (hadObservation) InvalidateObservation(current.Id);
            return false;
        }
        if (!_disposed) UsageChanged?.Invoke(this, new ClaudeUsageObservedEventArgs(current.Id));
        return true;
    }

    private void Publish(ClaudeProfile profile, string identityKey, ClaudeUsageResult result)
    {
        lock (_observationGate)
        {
            _observations[profile.Id] = result;
            _observationBindings[profile.Id] = new ObservationBinding(identityKey, profile.ConfigDirectory, profile.UseDefaultHome);
        }
        if (!_disposed) UsageChanged?.Invoke(this, new ClaudeUsageObservedEventArgs(profile.Id));
    }

    private static ClaudeUsageResult? UsableObservation(ClaudeUsageResult result, DateTimeOffset now)
    {
        var age = now - result.LastUpdatedAt;
        if (age < TimeSpan.Zero || age > ObservationLifetime) return null;
        var snapshot = new ClaudeUsageSnapshot(
            ClaudeDisplayMetrics.IsUsable(result.Snapshot.FiveHour, now) ? result.Snapshot.FiveHour : null,
            ClaudeDisplayMetrics.IsUsable(result.Snapshot.Weekly, now) ? result.Snapshot.Weekly : null,
            ClaudeDisplayMetrics.IsUsable(result.Snapshot.Fable, now) ? result.Snapshot.Fable : null);
        return snapshot.FiveHour is null && snapshot.Weekly is null && snapshot.Fable is null
            ? null : result with { Snapshot = snapshot };
    }

    private CancellationTokenSource BeginOperation(CancellationToken caller, CancellationToken deadline = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return CancellationTokenSource.CreateLinkedTokenSource(caller, deadline, _lifetime.Token);
    }

    private static bool IsTransientStatusFailure(Exception exception) =>
        exception is TimeoutException || exception is IOException and not FileNotFoundException;

    private sealed record ObservationBinding(string IdentityKey, string ConfigDirectory, bool UseDefaultHome)
    {
        internal bool Matches(ClaudeProfile profile, string identityKey) => IdentityKey == identityKey
            && UseDefaultHome == profile.UseDefaultHome
            && ClaudeProfileStore.PathsEqual(ConfigDirectory, profile.ConfigDirectory);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _lifetime.Cancel();
        _ = DisposeClientsWhenIdleAsync();
    }

    private async Task DisposeClientsWhenIdleAsync()
    {
        // An auth/login/usage command may still be unwinding cancellation. Do not dispose a semaphore
        // or a client underneath its waiter/finally block. The small gate is collected with this owner.
        await _gate.WaitAsync().ConfigureAwait(false);
        try
        {
            foreach (var client in _clients.Values) client.Dispose();
            _clients.Clear();
        }
        finally { _gate.Release(); }
    }
}
