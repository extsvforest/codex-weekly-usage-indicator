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
/// <para>It also keeps what the accounts' sidebars last agreed on (<see cref="ClaudeSessionLayout"/>), which the
/// <c>claude-session-sync</c> plan reads: the mapped accounts and their labels, when the app last left each one, and,
/// after a plan ran in an account, what that account now shows alike with each other one.</para>
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
    // Counts the user's reopen choices, so a pass that started before one never overrides it.
    private int _relaunchChoice;
    // What the accounts' sidebars agreed on, kept by this widget alone; null until read or started.
    private readonly ClaudeSessionSync.Sources _layoutSources;
    private ClaudeSessionLayout? _layout;
    private bool _layoutChanged;
    private readonly Dictionary<string, EventWaitHandle> _plannedEvents = new(StringComparer.Ordinal);
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
        ClaudeShelfEnvironment? environment = null, ClaudeDesktopLogWatcher? log = null,
        ClaudeSessionSync.Sources? layoutSources = null)
    {
        _owner = owner; _profiles = profiles; _shelf = shelf ?? new ClaudeSessionShelf();
        _environment = environment ?? ClaudeShelfEnvironment.Real; _log = log ?? new ClaudeDesktopLogWatcher();
        _layoutSources = layoutSources ?? ClaudeSessionSync.Sources.For(_shelf.Paths);
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
        KeepLayout();
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
                // Lost log lines could hide a switch: the next account seen is no switch.
                if (_log.LostLines) _confirmedKey = null;
                if (app.Key is { } confirmed && _accounts.Any(account => account.Key == confirmed) && confirmed != _confirmedKey)
                {
                    if (_confirmedKey is { } left) { switchedFrom = left; NoteLeft(left, now); }
                    _confirmedKey = confirmed;
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

    /// <summary>Keeps <see cref="ClaudeSessionLayout"/>: starts it the first time, keeps the mapped accounts and their
    /// labels, and after a plan ran in an account records what that account now shows alike with each other one.</summary>
    private void KeepLayout()
    {
        if (_layout is null)
        {
            var (kept, exists) = ClaudeSessionLayout.Load(_layoutSources.Ledger);
            // One that is there but cannot be read is never replaced with a guess.
            if (exists && kept is null) return;
            if (kept is null)
            {
                // The old reference is matched against the mapped accounts, so those come first.
                if (_accounts.Count == 0 || StartLayout() is not { } started) return;
                kept = started; _layoutChanged = true;
            }
            _layout = kept;
        }
        if (_accounts.Count > 0)
        {
            var mapped = _accounts.DistinctBy(account => account.Key)
                .ToDictionary(account => account.Key, account => account.Label, StringComparer.Ordinal);
            foreach (var key in _layout.Accounts.Keys.Where(key => !mapped.ContainsKey(key)).ToArray())
            { _layout.Accounts.Remove(key); _layoutChanged = true; }
            foreach (var (key, label) in mapped)
                if (!_layout.Accounts.TryGetValue(key, out var known) || known.Label != label)
                { _layout.Accounts[key] = new(label, known?.LeftAt); _layoutChanged = true; }
        }
        var wanted = _layout.Accounts.Keys.ToDictionary(ClaudeSessionSync.PlannedEventName, key => key, StringComparer.Ordinal);
        foreach (var name in _plannedEvents.Keys.Where(name => !wanted.ContainsKey(name)).ToArray())
        { _plannedEvents[name].Dispose(); _plannedEvents.Remove(name); }
        foreach (var (name, key) in wanted)
        {
            if (!_plannedEvents.TryGetValue(name, out var signal))
            {
                try { _plannedEvents[name] = signal = new EventWaitHandle(false, EventResetMode.AutoReset, name); }
                catch (Exception exception) when (exception is UnauthorizedAccessException or IOException
                                                      or WaitHandleCannotBeOpenedException) { continue; }
            }
            if (!signal.WaitOne(0)) continue;
            // Unreadable pins or groups teach nothing this time; the next plan signals again.
            var others = _layout.Accounts.Keys.Where(other => other != key).ToArray();
            try
            {
                foreach (var agreement in ClaudeSessionSync.Agreements(_layoutSources, key, others) ?? [])
                { _layout.Record(agreement); _layoutChanged = true; }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
        SaveLayout();
    }

    /// <summary>The first layout: the mapped accounts, each matched against the old reference if there is one, taking
    /// its values as what they last agreed on. Null when that cannot be read now; a later pass tries again.</summary>
    private ClaudeSessionLayout? StartLayout()
    {
        var layout = new ClaudeSessionLayout();
        var keys = _accounts.Select(account => account.Key).Distinct(StringComparer.Ordinal).ToArray();
        // An old reference that cannot be opened now may still be there to start from: wait for it.
        var (reference, known) = ClaudeSessionReference.Read(_layoutSources.Ledger);
        if (!known) return null;
        if (reference is not null && keys.Contains(reference.Account))
        {
            try
            {
                if (ClaudeSessionSync.Baseline(_layoutSources, reference.Account, keys) is not { } baseline) return null;
                foreach (var agreement in baseline) layout.Record(agreement);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
            // The user chose the reference just before leaving it.
            layout.Accounts[reference.Account] = new(_accounts.First(account => account.Key == reference.Account).Label,
                reference.SetAt);
        }
        return layout;
    }

    /// <summary>The app left an account: what changed there is now the newest, should it disagree with another
    /// account's change.</summary>
    private void NoteLeft(string left, DateTimeOffset now)
    {
        if (_layout is null || !_layout.Accounts.TryGetValue(left, out var account)) return;
        _layout.Accounts[left] = account with { LeftAt = now };
        _layoutChanged = true;
        SaveLayout();
    }

    private void SaveLayout()
    {
        if (!_layoutChanged || _layout is null) return;
        try { _layout.Save(_layoutSources.Ledger); _layoutChanged = false; }
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

    /// <summary>The layout this widget keeps, for tests: read after a pass, never while one runs.</summary>
    internal ClaudeSessionLayout? LayoutForTest => _layout;
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
