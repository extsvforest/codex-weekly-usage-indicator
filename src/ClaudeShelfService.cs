namespace WeeklyUsageIndicator;

internal enum ClaudeShelfProblem { None, Incompatible, Held, Failed }

/// <summary>A UI-thread snapshot of the Desktop app and the session-list fill.</summary>
internal sealed record ClaudeShelfStatus(
    ClaudeDesktopRunState Desktop,
    ClaudeDesktopAccountState App,
    string? AppProfileId,
    int? PendingForApp,
    int MappedAccounts,
    DateTimeOffset? LastFillAt,
    int LastFillCreated,
    bool LastFillComplete,
    ClaudeShelfProblem Problem,
    string? ProblemDetail,
    DateTimeOffset? RelaunchArmedUntil,
    DateTime? HostStartedAt = null)
{
    internal static readonly ClaudeShelfStatus Initial = new(ClaudeDesktopRunState.Unknown, ClaudeDesktopAccountState.Unknown,
        null, null, 0, null, 0, true, ClaudeShelfProblem.None, null, null);
    internal bool AppConfirmed => Desktop == ClaudeDesktopRunState.Running && App.Confirmed && AppProfileId is not null;
    internal bool RelaunchArmed => RelaunchArmedUntil is { } until && until > DateTimeOffset.Now;
}

/// <summary>Seams for tests. Production uses the real probes and ShellExecute.</summary>
internal sealed record ClaudeShelfEnvironment(Func<ClaudeDesktopRuntime> Observe, Func<ClaudeDesktopPackage?> FindPackage,
    Func<string, bool> IsCompatible, Action<ClaudeDesktopPackage> Launch, Func<DateTimeOffset> Now)
{
    internal static readonly ClaudeShelfEnvironment Real = new(ClaudeDesktopProbe.Observe, ClaudeDesktopProbe.FindPackage,
        ClaudeDesktopProbe.IsCompatible, ClaudeDesktopProbe.Launch, () => DateTimeOffset.Now);
}

/// <summary>What one background pass observed. <see cref="Reservation"/> is the reopen request that the closure
/// accepted in this pass, if any. <see cref="SwitchedFrom"/> is the account the app left when this pass saw it
/// confirm another one.</summary>
internal sealed record ClaudeShelfPass(ClaudeShelfStatus Status, Guid? Reservation, ClaudeDesktopPackage? Package,
    string? SwitchedFrom = null, int RelaunchChoice = 0);

/// <summary>
/// Owned by the widget. Every few seconds it observes Desktop off the UI thread. While Desktop runs it follows the
/// app's own log to learn the signed-in account and how many sessions that account still lacks. Once Desktop has
/// been fully closed for a moment it fills every registered account's session list, add-only. It relaunches Desktop
/// when the user asked for that, or after a switch to an account that lacked sessions; otherwise a closed app stays
/// closed.
/// <para>When the app switches accounts, the account it left becomes the session layout reference if its sidebar was
/// found aligned (the plan command's signal) during that stay: it then holds the newest layout. An account left
/// without being aligned may hold a stale one, so the reference stays where it was.</para>
/// </summary>
internal sealed class ClaudeShelfService : IDisposable
{
    private static readonly TimeSpan ClosedGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PendingInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AccountsInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RelaunchWindow = TimeSpan.FromMinutes(30);
    private readonly Control _owner;
    private readonly Func<IReadOnlyList<ClaudeProfile>> _profiles;
    private readonly ClaudeSessionShelf _shelf;
    private readonly ClaudeShelfEnvironment _environment;
    private readonly ClaudeDesktopLogWatcher _log;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };
    // Worker-owned state (only the single in-flight tick touches it).
    private IReadOnlyList<ClaudeShelfAccount> _accounts = [];
    private string _accountsKey = "";
    private DateTimeOffset _accountsAt = DateTimeOffset.MinValue, _pendingAt = DateTimeOffset.MinValue;
    private string? _pendingFor;
    private int? _pending;
    private DateTimeOffset? _closedSince, _retryAt, _closureStarted;
    private bool _closureDone;
    private DateTimeOffset? _lastFillAt;
    private int _lastCreated;
    private bool _lastComplete = true;
    private ClaudeShelfProblem _problem;
    private string? _problemDetail;
    private string? _confirmedKey;
    private DateTimeOffset _arrivedAt;
    // When an account was found aligned, and against which reference (account and when it was set).
    private readonly Dictionary<string, (DateTimeOffset At, string? Reference, DateTimeOffset? SetAt)> _alignedAt =
        new(StringComparer.Ordinal);
    // Counts the user's reopen choices, so a pass that started before one never overrides it.
    private int _relaunchChoice;
    // The log watcher's sign-in count when the account was last confirmed.
    private int _confirmSignIns;
    private readonly Dictionary<string, EventWaitHandle> _alignedEvents = new(StringComparer.Ordinal);
    // UI-thread state.
    private bool _busy, _disposed;
    private (Guid Id, DateTimeOffset Until)? _reservation;
    private string? _launchFailure;
    internal ClaudeShelfStatus Status { get; private set; } = ClaudeShelfStatus.Initial;
    /// <summary>The Desktop account/organization of each registered profile, as last resolved by the worker.</summary>
    internal IReadOnlyList<ClaudeShelfAccount> Accounts => Volatile.Read(ref _accounts);
    internal string LedgerDirectory => _shelf.Paths.LedgerDirectory;
    internal event EventHandler? StatusChanged;

    internal ClaudeShelfService(Control owner, Func<IReadOnlyList<ClaudeProfile>> profiles, ClaudeSessionShelf? shelf = null,
        ClaudeShelfEnvironment? environment = null, ClaudeDesktopLogWatcher? log = null)
    {
        _owner = owner; _profiles = profiles; _shelf = shelf ?? new ClaudeSessionShelf();
        _environment = environment ?? ClaudeShelfEnvironment.Real; _log = log ?? new ClaudeDesktopLogWatcher();
        _timer.Tick += (_, _) => Tick();
    }

    internal void Start() { if (!_disposed) { _timer.Start(); Tick(); } }

    /// <summary>Asks for Desktop to be reopened after the next fill. The next accepted closure consumes the request
    /// whether or not the reopen succeeds; it expires after 30 minutes.</summary>
    internal void ArmRelaunch(bool armed)
    {
        Interlocked.Increment(ref _relaunchChoice);
        _reservation = armed ? (Guid.NewGuid(), _environment.Now() + RelaunchWindow) : null;
        Publish(Status with { RelaunchArmedUntil = _reservation?.Until });
    }

    private void Tick()
    {
        if (_busy || _disposed) return;
        _busy = true;
        IReadOnlyList<ClaudeProfile> profiles;
        try { profiles = _profiles(); }
        catch (Exception) { profiles = []; }
        var reservation = ActiveReservation();
        _ = Task.Run(() => Work(profiles, reservation)).ContinueWith(task =>
        {
            if (_disposed) return;
            try
            {
                _owner.BeginInvoke((Action)(() =>
                {
                    _busy = false;
                    if (!_disposed && !task.IsFaulted) Accept(task.Result);
                }));
            }
            catch (InvalidOperationException) { _busy = false; }
        }, TaskScheduler.Default);
    }

    /// <summary>UI thread: a switch to an account that still lacks sessions arms the reopen, so quitting once fills
    /// the list and brings the app back.</summary>
    private void ArmAfterSwitch(ClaudeShelfPass pass)
    {
        if (pass.SwitchedFrom is not null && pass.Status is { AppConfirmed: true, PendingForApp: > 0 }
            && pass.Status.Problem != ClaudeShelfProblem.Incompatible && _reservation is null
            && pass.RelaunchChoice == Volatile.Read(ref _relaunchChoice))
            _reservation = (Guid.NewGuid(), _environment.Now() + RelaunchWindow);
    }

    internal Guid? ActiveReservation()
    {
        if (_reservation is { } reservation && reservation.Until <= _environment.Now()) _reservation = null;
        return _reservation?.Id;
    }

    /// <summary>UI thread: applies a pass. A reservation accepted by that closure is consumed here, and Desktop is
    /// reopened only if the request was not cancelled or replaced meanwhile and the app is still closed.</summary>
    internal void Accept(ClaudeShelfPass pass)
    {
        // A request that expired while the pass ran is gone before it can be used.
        ActiveReservation();
        if (pass.Reservation is { } accepted && _reservation?.Id == accepted)
        {
            _reservation = null;
            _launchFailure = null;
            if (pass.Package is { } package && _environment.Observe().State == ClaudeDesktopRunState.Closed)
            {
                try { _environment.Launch(package); }
                catch (Exception exception) { _launchFailure = exception.Message; }
            }
        }
        if (pass.Status.Desktop == ClaudeDesktopRunState.Running) _launchFailure = null;
        ArmAfterSwitch(pass);
        var status = pass.Status with { RelaunchArmedUntil = _reservation?.Until };
        if (_launchFailure is not null && status.Problem == ClaudeShelfProblem.None)
            status = status with { Problem = ClaudeShelfProblem.Failed, ProblemDetail = _launchFailure };
        Publish(status);
    }

    internal void SetStatusForTest(ClaudeShelfStatus status) => Publish(status);
    internal void SetAccountsForTest(IReadOnlyList<ClaudeShelfAccount> accounts) => Volatile.Write(ref _accounts, accounts);

    private void Publish(ClaudeShelfStatus status)
    {
        var changed = status != Status;
        Status = status;
        if (changed && !_disposed) StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Background pass. Only one runs at a time; it owns the worker state below.</summary>
    internal ClaudeShelfPass Work(IReadOnlyList<ClaudeProfile> profiles, Guid? reservation)
    {
        var now = _environment.Now();
        var choice = Volatile.Read(ref _relaunchChoice);
        RefreshAccounts(profiles, now);
        TakeAlignedSignals(now);
        var runtime = _environment.Observe();
        string? switchedFrom = null;
        Guid? accepted = null;
        ClaudeDesktopPackage? package = null;
        ClaudeDesktopAccountState app;
        switch (runtime.State)
        {
            case ClaudeDesktopRunState.Running:
                _closedSince = null; _closureDone = false; _retryAt = null; _closureStarted = null;
                // Report an unreviewed version while the app runs, so nothing promises a fill that will not run.
                package = _environment.FindPackage();
                if (package is null || !_environment.IsCompatible(package.AsarPath))
                { _problem = ClaudeShelfProblem.Incompatible; _problemDetail = package?.Version.ToString(); }
                else if (_problem == ClaudeShelfProblem.Incompatible) { _problem = ClaudeShelfProblem.None; _problemDetail = null; }
                app = _log.Update(runtime.HostStartedAt, _accounts);
                // Lost log lines hide anything: start over rather than trust what was learned before them.
                if (_log.LostLines) { _confirmedKey = null; _alignedAt.Clear(); }
                if (app.Key is { } confirmed && _accounts.Any(account => account.Key == confirmed))
                {
                    // More than one sign-in since the account was last confirmed, over passes and app runs, means the app
                    // may have left an account and come back in stays no pass confirmed: start over the same way.
                    if (_confirmedKey is not null && _log.SignIns - _confirmSignIns > 1)
                    { _confirmedKey = null; _alignedAt.Clear(); }
                    if (confirmed != _confirmedKey)
                    {
                        if (_confirmedKey is { } left) { switchedFrom = left; FollowReference(left, now); }
                        _confirmedKey = confirmed; _arrivedAt = now;
                    }
                    _confirmSignIns = _log.SignIns;
                }
                if (app.Key is { } key && (key != _pendingFor || now - _pendingAt >= PendingInterval))
                {
                    try { _pending = _shelf.Plan(_accounts).PendingFor(key); }
                    catch (Exception) { _pending = null; }
                    _pendingFor = key; _pendingAt = now;
                }
                else if (app.Key is null) { _pending = null; _pendingFor = null; }
                break;
            case ClaudeDesktopRunState.Closed:
                app = _log.Update(null, _accounts);
                _pending = null; _pendingFor = null;
                _closedSince ??= now;
                _closureStarted ??= now;
                if (now - _closedSince >= ClosedGrace && (!_closureDone || _retryAt is { } retry && now >= retry))
                {
                    var first = !_closureDone;
                    package = Fill(now);
                    // The first pass of a closure accepts a pending reopen request, whatever the fill result.
                    if (first) accepted = reservation;
                }
                break;
            default:
                // An uninspectable process: neither write nor claim the app is closed.
                app = _log.State.Status == ClaudeDesktopAccountStatus.Closed ? ClaudeDesktopAccountState.Unknown : _log.State;
                break;
        }
        var profileId = app.Key is { } appKey ? _accounts.FirstOrDefault(account => account.Key == appKey)?.ProfileId : null;
        return new ClaudeShelfPass(new ClaudeShelfStatus(runtime.State, app, profileId, _pending, _accounts.Count, _lastFillAt,
            _lastCreated, _lastComplete, _problem, _problemDetail, null, runtime.HostStartedAt), accepted,
            package ?? _environment.FindPackage(), switchedFrom, choice);
    }

    /// <summary>Notes accounts the plan command found aligned with the reference in place since the last pass. A
    /// signal names the reference it was measured against; listening only for the current one drops the others unheard.</summary>
    private void TakeAlignedSignals(DateTimeOffset now)
    {
        var reference = ClaudeSessionReference.Load(_shelf.Paths.LedgerDirectory);
        var wanted = reference is null ? new Dictionary<string, string>(StringComparer.Ordinal)
            : _accounts.DistinctBy(account => account.Key).ToDictionary(
                account => ClaudeSessionSync.AlignedEventName(account.Key, reference.Account, reference.SetAt),
                account => account.Key, StringComparer.Ordinal);
        foreach (var name in _alignedEvents.Keys.Where(name => !wanted.ContainsKey(name)).ToArray())
        { _alignedEvents[name].Dispose(); _alignedEvents.Remove(name); }
        foreach (var (name, key) in wanted)
        {
            if (!_alignedEvents.TryGetValue(name, out var signal))
            {
                try { _alignedEvents[name] = signal = new EventWaitHandle(false, EventResetMode.AutoReset, name); }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException
                                                      or WaitHandleCannotBeOpenedException) { continue; }
            }
            if (signal.WaitOne(0)) _alignedAt[key] = (now, reference!.Account, reference.SetAt);
        }
    }

    /// <summary>The app left <paramref name="left"/>. It becomes the reference if it already is one, if there is none,
    /// or if it was found aligned with the current reference after the app arrived there; otherwise the reference stays.</summary>
    private void FollowReference(string left, DateTimeOffset now)
    {
        var ledger = _shelf.Paths.LedgerDirectory;
        var current = ClaudeSessionReference.Load(ledger);
        if (current?.Account == left
            || current is not null && !(_alignedAt.TryGetValue(left, out var aligned) && aligned.At >= _arrivedAt
                                        && aligned.Reference == current.Account && aligned.SetAt == current.SetAt)) return;
        if (_accounts.FirstOrDefault(account => account.Key == left) is not { } account) return;
        var labels = new Dictionary<string, string>(current?.Labels ?? new Dictionary<string, string>(), StringComparer.Ordinal);
        foreach (var known in _accounts) labels[known.Key] = known.Label;
        try { new ClaudeSessionReference(left, account.Label, account.ProfileId, now, labels).Save(ledger); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private void RefreshAccounts(IReadOnlyList<ClaudeProfile> profiles, DateTimeOffset now)
    {
        var key = string.Join("|", profiles.Select(profile => profile.Id + ":" + profile.Identity?.Key + ":" + profile.IsVerified));
        if (key == _accountsKey && now - _accountsAt < AccountsInterval) return;
        try { Volatile.Write(ref _accounts, ClaudeShelfAccounts.Resolve(profiles, _shelf.Paths.LedgerDirectory)); }
        catch (Exception) { Volatile.Write(ref _accounts, []); }
        _accountsKey = key; _accountsAt = now;
    }

    private ClaudeDesktopPackage? Fill(DateTimeOffset now)
    {
        _closureDone = true; _retryAt = null;
        var package = _environment.FindPackage();
        if (_accounts.Count < 2) { _problem = ClaudeShelfProblem.None; _problemDetail = null; return package; }
        if (package is null || !_environment.IsCompatible(package.AsarPath))
        {
            _problem = ClaudeShelfProblem.Incompatible; _problemDetail = package?.Version.ToString();
            return package;
        }
        try
        {
            var result = _shelf.Apply(_accounts, () => _environment.Observe().State == ClaudeDesktopRunState.Closed,
                package.Version.ToString());
            _lastFillAt = now; _lastCreated = result.Created;
            // A sidebar sync that could not finish is retried like a held list.
            _lastComplete = result.Plan.IsComplete && result.Prefs?.Complete != false;
            _problem = result.Plan.Held.Count > 0 ? ClaudeShelfProblem.Held : ClaudeShelfProblem.None;
            _problemDetail = result.Plan.Held.Values.FirstOrDefault();
            // Sessions held by a live writer or a pending app recovery clear up on their own.
            if (!_lastComplete) ScheduleRetry(now);
        }
        catch (ClaudeDesktopRunningException exception)
        {
            if (exception.Created > 0) { _lastFillAt = now; _lastCreated = exception.Created; _lastComplete = false; }
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException)
        {
            // A shelf changed while preparing, the installer holds the lease, or a run is already in progress.
            _problem = ClaudeShelfProblem.Failed; _problemDetail = exception.Message;
            ScheduleRetry(now);
        }
        catch (Exception exception)
        {
            _problem = ClaudeShelfProblem.Failed; _problemDetail = exception.Message;
        }
        return package;
    }

    private void ScheduleRetry(DateTimeOffset now)
    {
        if (_closureStarted is { } started && now - started < RetryWindow) _retryAt = now + RetryInterval;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _timer.Stop(); _timer.Dispose();
        // A pass may still be running; its handles go with the process.
    }
}

/// <summary>
/// UI-thread policy that keeps the selected Claude usage profile on the account the running Desktop app confirmed.
/// A failed selection is retried after two minutes, or at once after that account's usage check or sign-in succeeds.
/// A newer app account replaces a selection in flight, and the controller drops a selection whose reason has passed.
/// </summary>
internal sealed class ClaudeAppFollower(Func<ClaudeShelfStatus> status, Func<string> selectedProfileId,
    Func<string, Func<bool>, CancellationToken, Task> select, Func<DateTimeOffset> now, CancellationToken lifetime) : IDisposable
{
    private static readonly TimeSpan RetryDelay = TimeSpan.FromMinutes(2);
    private string? _followedKey;
    private DateTimeOffset _retryAt = DateTimeOffset.MinValue;
    private CancellationTokenSource? _selection;
    private bool _disposed;
    /// <summary>The profile being selected for the app's account while that selection runs.</summary>
    internal string? Following { get; private set; }
    /// <summary>Raised when a selection starts and when it ends, whatever its outcome.</summary>
    internal event EventHandler? Changed;

    internal void OnStatusChanged()
    {
        var current = status();
        var key = current.AppConfirmed ? current.App.Key : null;
        if (key is not null && key != _followedKey) { _followedKey = key; _retryAt = DateTimeOffset.MinValue; }
        Ensure();
    }

    /// <summary>A successful usage check or sign-in of the app's account is a reason to follow it again now.</summary>
    internal void OnUsageObserved(string profileId, bool succeeded)
    {
        if (succeeded && profileId == status().AppProfileId) _retryAt = DateTimeOffset.MinValue;
        Ensure();
    }

    internal void Ensure()
    {
        if (_disposed) return;
        var current = status();
        if (!current.AppConfirmed || current.AppProfileId is not { } profileId || profileId == selectedProfileId()
            || Following == profileId || now() < _retryAt) return;
        _ = FollowAsync(profileId);
    }

    private async Task FollowAsync(string profileId)
    {
        // Register the new request before cancelling the old one, so anything the cancellation runs sees the new
        // request as current. Only the current request may change the shared state when it ends.
        var previous = _selection;
        var request = _selection = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        Following = profileId;
        previous?.Cancel();
        Changed?.Invoke(this, EventArgs.Empty);
        var failed = false;
        try
        {
            await select(profileId, () => status() is { AppConfirmed: true } current && current.AppProfileId == profileId,
                request.Token);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { failed = true; }
        finally { request.Dispose(); }
        if (_selection != request) return;
        _selection = null; Following = null;
        // The widget keeps explaining instead of showing another account; the account page shows the failure.
        if (failed) _retryAt = now() + RetryDelay;
        if (_disposed) return;
        Changed?.Invoke(this, EventArgs.Empty);
        // The app may have moved on while this ran.
        Ensure();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        var selection = _selection;
        _selection = null;
        selection?.Cancel();
    }
}
