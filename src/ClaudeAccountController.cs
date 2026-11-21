namespace WeeklyUsageIndicator;

internal sealed class ClaudeUsageObservedEventArgs(string profileId) : EventArgs
{ internal string ProfileId { get; } = profileId; }

/// <summary>An account's last usage kept on disk, for a page that opens without a query: its windows (any of them may
/// have reset since) and when they were observed. Never a fresh observation, a recovery value or part of a total.</summary>
internal sealed record ClaudeSavedUsage(ClaudeUsageSnapshot Snapshot, DateTimeOffset ObservedAt);

/// <summary>How one account's read in <see cref="ClaudeAccountController.ReadUsageManyAsync"/> ended: its result, or
/// the failure, which also stays on that account (<see cref="ClaudeAccountController.LastFailure"/>).</summary>
internal sealed record ClaudeUsageReadOutcome(string ProfileId, ClaudeUsageResult? Result, Exception? Failure);

/// <summary>Moving the default-home account (<see cref="ClaudeAccountController.MoveDefaultHomeAccountAsync"/>) ended
/// in another account's sign-in. Nothing was changed.</summary>
internal sealed class ClaudeMoveSignedInAnotherAccountException()
    : InvalidOperationException("다른 Claude 계정으로 로그인했습니다. 옮길 계정으로 다시 로그인하세요.");

// Verified profile identity binds each account's usage. Operations on one profile (sign-in, selection, sign-out, a read)
// hold that profile's gate and run one at a time; reads of different profiles run side by side, at most
// ReadConcurrency at once. Credentials remain entirely inside the unmodified official Claude CLI.
internal sealed class ClaudeAccountController : IDisposable
{
    internal const int ReadConcurrency = 3;
    private static readonly TimeSpan ObservationLifetime = TimeSpan.FromHours(24);
    private static readonly TimeSpan StatusRetryDelay = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(30);
    internal ClaudeProfileStore Store { get; }
    internal ClaudeProfileRuntime Runtime { get; }
    // Locked on itself. The clients of one profile are made, used and replaced only under that profile's gate.
    private readonly Dictionary<string, ClaudeUsageClient> _clients = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ClaudeUsageResult> _observations = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Exception> _failures = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ObservationBinding> _observationBindings = new(StringComparer.Ordinal);
    private readonly object _observationGate = new();
    private readonly Dictionary<string, SemaphoreSlim> _profileGates = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim _readSlots = new(ReadConcurrency, ReadConcurrency);
    private readonly HashSet<string> _linksChecked = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly TimeProvider _timeProvider;
    private readonly Func<ClaudeProfile, string, string, ClaudeUsageClient> _clientFactory;
    private readonly ClaudeUsageFileCacheStore _usageCache;
    private volatile bool _disposed;
    internal event EventHandler? SelectionChanged;
    internal event EventHandler<ClaudeUsageObservedEventArgs>? UsageChanged;
    internal ClaudeProfile SelectedProfile => Store.SelectedProfile ?? Store.DefaultProfile;
    internal string SelectionKey => SelectedProfile.Id;

    internal ClaudeAccountController(ClaudeProfileStore? store = null, ClaudeProfileRuntime? runtime = null,
        TimeProvider? timeProvider = null, Func<ClaudeProfile, string, string, ClaudeUsageClient>? clientFactory = null,
        ClaudeUsageFileCacheStore? usageCache = null)
    {
        Store = store ?? new();
        Runtime = runtime ?? new(Store);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _usageCache = usageCache ?? ClaudeUsageFileCacheStore.CreateDefault(_timeProvider);
        _clientFactory = clientFactory ?? ((profile, key, accountKey) =>
        {
            var runner = new ClaudeCodeProcessRunner(configureProcess: start =>
                ClaudeProfileRuntime.ApplyProfileEnvironment(start, profile));
            return new ClaudeUsageClient(new ClaudeCodeUsageSource(runner, _timeProvider), _timeProvider,
                _usageCache.For(key), key, ClaudeUsageRequestThrottle.Shared, accountKey);
        });
    }

    internal ClaudeUsageResult? LastUsage(string profileId)
    {
        lock (_observationGate)
            return _observations.TryGetValue(profileId, out var result)
                ? UsableObservation(result, _timeProvider.GetUtcNow()) : null;
    }

    internal Exception? LastFailure(string profileId)
    {
        lock (_observationGate) return _failures.GetValueOrDefault(profileId);
    }

    /// <summary>The account's last usage kept on disk, read without a query: only its own verified identity's value,
    /// for up to <see cref="ClaudeUsageFileCacheStore.HistoryLifetime"/>. A page shows it with its check time as an
    /// earlier value; it never becomes <see cref="LastUsage"/>, a recovery value or part of a total.</summary>
    internal ClaudeSavedUsage? SavedUsage(string profileId)
    {
        var profile = Store.ListProfiles().SingleOrDefault(saved => saved.Id == profileId);
        if (profile?.Identity is not { } identity) return null;
        return _usageCache.Last(profile.Id + ":" + identity.Key) is { } entry
            ? new ClaudeSavedUsage(entry.Snapshot, entry.LastUpdatedAt) : null;
    }

    /// <summary>The accounts a full refresh reads: each verified identity once, leaving out those that need a sign-in
    /// or show another account. The selected profile comes first; of two profiles for one account, the one with its
    /// own folder is read rather than the default home.</summary>
    internal IReadOnlyList<ClaudeProfile> ReadableProfiles()
    {
        var selectedId = SelectedProfile.Id;
        return Store.ListProfiles()
            .Where(profile => profile.IsVerified
                && LastFailure(profile.Id) is not (ClaudeLoginRequiredException or ClaudeIdentityChangedException))
            .OrderByDescending(profile => profile.Id == selectedId).ThenBy(profile => profile.IsDefault)
            .DistinctBy(profile => profile.Identity!.Key, StringComparer.Ordinal).ToArray();
    }

    internal async Task<ClaudeProfile> RegisterDefaultAsync(CancellationToken token)
    {
        using var operation = BeginOperation(token);
        var gate = ProfileGate(Store.DefaultProfile.Id);
        await gate.WaitAsync(operation.Token);
        try
        {
            var profile = Store.DefaultProfile;
            var identity = await Runtime.VerifyAsync(profile, operation.Token);
            var previous = Store.ListProfiles().SingleOrDefault(saved => saved.Id == profile.Id);
            if (previous?.Identity is { } stored && stored.Key != identity.Key)
                throw new ClaudeIdentityChangedException();
            var changedIdentity = false;
            lock (_observationGate)
                changedIdentity |= _observationBindings.TryGetValue(profile.Id, out var binding)
                    && !binding.Matches(profile, identity.Key);
            // Connecting the already-used default account only adds its row and label. Keep its
            // accepted usage/client: dropping them would leave it blank until this account's throttle expires.
            if (changedIdentity) Forget(profile.Id);
            var registered = Store.RegisterDefault(previous?.Label ?? UiText.T("기존 Claude 계정"), identity);
            ClearAuthenticationFailure(profile.Id);
            return registered;
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidDataException)
        { Forget(Store.DefaultProfile.Id, exception); throw; }
        finally { gate.Release(); }
    }

    internal async Task<ClaudeProfile> AddAsync(string label, CancellationToken token)
    {
        using var operation = BeginOperation(token);
        operation.Token.ThrowIfCancellationRequested();
        var profile = Store.CreateProfile(label);
        var gate = ProfileGate(profile.Id);
        await gate.WaitAsync(operation.Token);
        try
        {
            var identity = await Runtime.LoginAsync(profile, operation.Token);
            return Store.RecordVerifiedIdentity(profile.Id, identity);
        }
        finally { gate.Release(); }
    }

    internal async Task LoginAsync(ClaudeProfile profile, CancellationToken token)
    {
        using var operation = BeginOperation(token);
        var gate = ProfileGate(profile.Id);
        await gate.WaitAsync(operation.Token);
        var previousFailure = LastFailure(profile.Id);
        try
        {
            // This profile's login is about to change. Other profiles go on reading and displaying their
            // usage while the official login terminal is waiting for the user.
            Forget(profile.Id);
            var identity = await Runtime.LoginAsync(profile, operation.Token);
            Store.RecordVerifiedIdentity(profile.Id, identity);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidDataException)
        { Forget(profile.Id, exception); throw; }
        catch (Exception exception) when (exception is OperationCanceledException or InvalidOperationException)
        {
            if (previousFailure is not null) PublishFailure(profile.Id, previousFailure);
            else if (exception is not OperationCanceledException) PublishFailure(profile.Id, exception);
            throw;
        }
        finally { gate.Release(); }
    }

    /// <summary>Selects the profile whose usage the widget shows. <paramref name="stillWanted"/> is checked right
    /// before the selection is stored, so a verification that outlives its reason (the app changed accounts again)
    /// never lands.</summary>
    internal async Task SelectAsync(ClaudeProfile profile, CancellationToken token, Func<bool>? stillWanted = null)
    {
        using var operation = BeginOperation(token);
        var gate = ProfileGate(profile.Id);
        await gate.WaitAsync(operation.Token);
        try
        {
            var identity = await Runtime.VerifyAsync(profile, operation.Token);
            // Both modes must identify the same subscription. The usage command
            // ignores extensions and launch settings; selection cannot conceal that.
            var usageIdentity = await Runtime.VerifyAsync(profile, operation.Token, safeMode: true);
            if (identity.Key != usageIdentity.Key)
                throw new InvalidOperationException("Claude 실행과 사용량 조회의 계정이 다릅니다. 로그인 설정을 확인해 주세요.");
            operation.Token.ThrowIfCancellationRequested();
            if (stillWanted?.Invoke() == false) throw new OperationCanceledException("The Desktop account changed again.");
            Store.Select(profile.Id, identity);
            ClearAuthenticationFailure(profile.Id);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or InvalidDataException)
        { Forget(profile.Id, exception); throw; }
        finally { gate.Release(); }
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
        var gate = ProfileGate(requestedProfile.Id);
        var entered = false;
        var reading = false;
        try
        {
            await gate.WaitAsync(operation.Token);
            entered = true;
            var profile = ResolveProfile(profileId);
            if (profile.Id != requestedProfile.Id)
                throw new OperationCanceledException("Claude account selection changed during the request.");
            requestedProfile = profile;
            // The account on the default home moved to its own profile. The terminal and the app keep that home;
            // the widget runs nothing there, not even a status check, until an account is selected again.
            if (profile.IsDefault && Store.DefaultHomeRetired) throw new ClaudeLoginRequiredException();
            RetireLinksOnce(profile);
            await _readSlots.WaitAsync(operation.Token);
            reading = true;
            var identity = await Runtime.VerifyAsync(profile, operation.Token, safeMode: true);
            if (profile.Identity is not null && profile.Identity.Key != identity.Key)
                throw new ClaudeIdentityChangedException();
            var key = profile.Id + ":" + identity.Key;
            // Once verification identifies a different account, an older observation is no longer a
            // fallback, even if the subsequent usage request fails transiently.
            bool changedIdentity;
            lock (_observationGate)
                changedIdentity = _observationBindings.TryGetValue(profile.Id, out var binding)
                    && !binding.Matches(profile, identity.Key);
            if (changedIdentity) Forget(profile.Id);
            var client = Client(profile, key, identity.Key);
            var result = UsableObservation(await client.GetUsageAsync(operation.Token), _timeProvider.GetUtcNow())
                ?? throw new InvalidDataException("Claude Code did not return a usable usage limit.");
            Publish(profile, identity.Key, result);
            return result;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or InvalidDataException)
        {
            Forget(requestedProfile.Id, ex);
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
            var failure = new ClaudeUsageTemporarilyUnavailableException(retryAfter, ex);
            PublishFailure(requestedProfile.Id, failure);
            throw failure;
        }
        catch (FileNotFoundException ex) { Forget(requestedProfile.Id, ex); throw; }
        finally
        {
            if (reading) _readSlots.Release();
            if (entered) gate.Release();
        }
    }

    /// <summary>Reads several accounts side by side, at most <see cref="ReadConcurrency"/> at a time, each as
    /// <see cref="ReadUsageAsync"/> does: through its own profile gate and ten-minute interval. Each account's result
    /// or failure is published (<see cref="UsageChanged"/>) and handed to <paramref name="completed"/> as soon as that
    /// account ends, on the caller's context; a failure stays with its account while the others go on. Null reads
    /// <see cref="ReadableProfiles"/>. Cancellation by the caller, or disposal, ends the whole batch.</summary>
    internal async Task<IReadOnlyList<ClaudeUsageReadOutcome>> ReadUsageManyAsync(IEnumerable<string>? profileIds,
        CancellationToken token, Action<ClaudeUsageReadOutcome>? completed = null)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var ids = (profileIds ?? ReadableProfiles().Select(profile => profile.Id))
            .Distinct(StringComparer.Ordinal).ToArray();
        async Task<ClaudeUsageReadOutcome> ReadOne(string id)
        {
            ClaudeUsageReadOutcome outcome;
            try { outcome = new(id, await ReadUsageAsync(id, token), null); }
            catch (Exception exception) when (exception is OperationCanceledException && token.IsCancellationRequested
                                              || _lifetime.IsCancellationRequested) { throw; }
            catch (Exception exception) { outcome = new(id, null, exception); }
            completed?.Invoke(outcome);
            return outcome;
        }
        return await Task.WhenAll(ids.Select(ReadOne).ToArray());
    }

    internal async Task LogoutAsync(ClaudeProfile profile, CancellationToken token)
    {
        using var operation = BeginOperation(token);
        var gate = ProfileGate(profile.Id);
        await gate.WaitAsync(operation.Token);
        var wasSelected = false;
        try
        {
            wasSelected = profile.Id == SelectedProfile.Id;
            Forget(profile.Id);
            await Runtime.LogoutAsync(profile, operation.Token);
            Store.MarkLoggedOut(profile.Id);
            PublishFailure(profile.Id, new ClaudeLoginRequiredException());
        }
        finally { gate.Release(); }
        if (wasSelected && !_disposed) SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Moves the account registered on the default Claude home into a profile of its own, so the widget no longer
    /// shares that home's sign-in with the terminal CLI and the Desktop app. The official login opens as it does for a
    /// new account. Signed in to the same account, the row keeps its name, place and selection and takes the new
    /// profile, its saved usage goes with it, and nothing runs against the default home afterwards. Signed in to
    /// another account, nothing changes: that new login is signed out again and
    /// <see cref="ClaudeMoveSignedInAnotherAccountException"/> is thrown. The default home itself is never modified,
    /// and a folder left by a move that did not complete stays in place, out of the list.
    /// </summary>
    internal async Task<ClaudeProfile> MoveDefaultHomeAccountAsync(CancellationToken token)
    {
        using var operation = BeginOperation(token);
        var source = Store.ListProfiles().SingleOrDefault(profile => profile.IsDefault);
        if (source?.Identity is not { } expected) throw new InvalidOperationException("옮길 기존 Claude 계정이 없습니다.");
        var target = Store.NewDetachedProfile(source.Label);
        var targetGate = ProfileGate(target.Id);
        await targetGate.WaitAsync(operation.Token);
        ClaudeProfile moved;
        var loginStarted = false;
        var kept = false;
        try
        {
            loginStarted = true;
            var identity = await Runtime.LoginAsync(target, operation.Token);
            if (identity.Key != expected.Key) throw new ClaudeMoveSignedInAnotherAccountException();
            // As when selecting: both modes must identify the same subscription before the selection carries over.
            var usageIdentity = await Runtime.VerifyAsync(target, operation.Token, safeMode: true);
            if (usageIdentity.Key != identity.Key)
                throw new InvalidOperationException("Claude 실행과 사용량 조회의 계정이 다릅니다. 로그인 설정을 확인해 주세요.");
            var sourceGate = ProfileGate(source.Id);
            await sourceGate.WaitAsync(operation.Token);
            try
            {
                moved = Store.AdoptDefault(target, identity);
                kept = true;
                _usageCache.Move(source.Id + ":" + identity.Key, moved.Id + ":" + identity.Key);
                // The default row's observation and clients belong to the default home, which is no longer used.
                Forget(source.Id);
            }
            finally { sourceGate.Release(); }
        }
        finally
        {
            // A login that was started but not kept may have finished in the official terminal even when its check
            // failed or the request was canceled afterwards.
            if (loginStarted && !kept) await SignOutUnusedAsync(target);
            targetGate.Release();
        }
        if (!_disposed) SelectionChanged?.Invoke(this, EventArgs.Empty);
        return moved;
    }

    private SemaphoreSlim ProfileGate(string profileId)
    {
        lock (_profileGates)
        {
            if (!_profileGates.TryGetValue(profileId, out var gate)) _profileGates.Add(profileId, gate = new(1, 1));
            return gate;
        }
    }

    // The client of this profile and verified account, replacing one bound to another account. Under the profile's gate.
    private ClaudeUsageClient Client(ClaudeProfile profile, string key, string accountKey)
    {
        ClaudeUsageClient? client;
        ClaudeUsageClient[] replaced;
        lock (_clients)
        {
            var previous = _clients.Keys.Where(value => value.StartsWith(profile.Id + ":", StringComparison.Ordinal)
                && value != key).ToArray();
            replaced = previous.Select(value => _clients[value]).ToArray();
            foreach (var value in previous) _clients.Remove(value);
            _clients.TryGetValue(key, out client);
        }
        foreach (var old in replaced) old.Dispose();
        if (client is not null) return client;
        client = _clientFactory(profile, key, accountKey);
        lock (_clients) _clients[key] = client;
        return client;
    }

    // Links an earlier build left in this profile go before its first read in this process (see
    // ClaudeProfileRuntime.RetireSharedLinks). One that cannot go now is tried again at the next sign-in or start.
    private void RetireLinksOnce(ClaudeProfile profile)
    {
        if (profile.IsDefault) return;
        lock (_linksChecked)
        {
            if (!_linksChecked.Add(profile.Id)) return;
        }
        // Stopped when a folder was not where its link pointed: reads go on, and nothing else is removed.
        try { Runtime.RetireSharedLinks(profile); }
        catch (IOException) { }
    }

    // Ends, through the official CLI, a login the move did not keep, so the left folder holds no live sign-in. Best
    // effort and bounded; the folder itself is not deleted.
    private async Task SignOutUnusedAsync(ClaudeProfile profile)
    {
        if (_lifetime.IsCancellationRequested) return;
        using var cleanup = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
        cleanup.CancelAfter(CleanupTimeout);
        try { await Runtime.LogoutAsync(profile, cleanup.Token); }
        catch (Exception) { }
    }

    private void Forget(string id, Exception? failure = null)
    {
        InvalidateObservation(id, failure);
        ClaudeUsageClient[] removed;
        lock (_clients)
        {
            var keys = _clients.Keys.Where(key => key.StartsWith(id + ":", StringComparison.Ordinal)).ToArray();
            removed = keys.Select(key => _clients[key]).ToArray();
            foreach (var key in keys) _clients.Remove(key);
        }
        foreach (var client in removed) client.Dispose();
        // Its saved values stay for display; they no longer recover the widget.
        _usageCache.EndRecovery(id);
    }

    private void InvalidateObservation(string id, Exception? failure = null)
    {
        lock (_observationGate)
        {
            _observations.Remove(id); _observationBindings.Remove(id);
            if (failure is null) _failures.Remove(id);
            else _failures[id] = failure;
        }
        // Null is an explicit invalidation, not an absent update. A selected widget must clear it now.
        if (!_disposed) UsageChanged?.Invoke(this, new ClaudeUsageObservedEventArgs(id));
    }

    private void PublishFailure(string id, Exception failure)
    {
        lock (_observationGate) _failures[id] = failure;
        if (!_disposed) UsageChanged?.Invoke(this, new ClaudeUsageObservedEventArgs(id));
    }

    private void ClearAuthenticationFailure(string id)
    {
        lock (_observationGate)
        {
            if (_failures.GetValueOrDefault(id) is not UnauthorizedAccessException) return;
            _failures.Remove(id);
        }
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
                _failures.Remove(current.Id);
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
            _failures.Remove(profile.Id);
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
        // An auth/login/usage command may still be unwinding cancellation. Wait until every profile's operation has
        // left its gate, then keep the gates: nothing new can enter (its wait is already canceled), and no semaphore or
        // client is disposed underneath a waiter or finally block. The small gates are collected with this owner.
        SemaphoreSlim[] gates;
        lock (_profileGates) gates = _profileGates.Values.ToArray();
        foreach (var gate in gates) await gate.WaitAsync().ConfigureAwait(false);
        ClaudeUsageClient[] clients;
        lock (_clients)
        {
            clients = _clients.Values.ToArray();
            _clients.Clear();
        }
        foreach (var client in clients) client.Dispose();
    }
}
