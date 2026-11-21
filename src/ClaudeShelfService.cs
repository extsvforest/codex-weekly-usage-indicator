using System.IO.MemoryMappedFiles;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WeeklyUsageIndicator;

internal enum ClaudeShelfProblem { None, Incompatible, Held, Failed }

/// <summary>A UI-thread snapshot of the Desktop app and the session-list fill. <see cref="Unreviewed"/> is the installed
/// app version whose deletion protection reads differently from the reviewed ones (see <see cref="ClaudeDesktopContract"/>):
/// filling goes on, and the user is told to archive sessions rather than delete them until it is reviewed.
/// <see cref="Damaged"/> names the accounts whose list has held a record that does not parse for a while: the app may
/// count nothing for such a record, so the same advice holds until it reads again.</summary>
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
    DateTime? HostStartedAt = null,
    string? Unreviewed = null,
    string? Damaged = null,
    ClaudeScheduledProblem? Scheduled = null)
{
    internal static readonly ClaudeShelfStatus Initial = new(ClaudeDesktopRunState.Unknown, ClaudeDesktopAccountState.Unknown,
        null, null, 0, null, 0, true, ClaudeShelfProblem.None, null, null);
    internal bool AppConfirmed => Desktop == ClaudeDesktopRunState.Running && App.Confirmed && AppProfileId is not null;
    internal bool RelaunchArmed => RelaunchArmedUntil is { } until && until > DateTimeOffset.Now;
}

/// <summary>Seams for tests. Production uses the real probes and ShellExecute. <see cref="ScheduledContract"/> reads
/// the app's way of keeping scheduled-task lists; without it they are not aligned.</summary>
internal sealed record ClaudeShelfEnvironment(Func<ClaudeDesktopRuntime> Observe, Func<ClaudeDesktopPackage?> FindPackage,
    Func<string, ClaudeDesktopContract> Contract, Action<ClaudeDesktopPackage> Launch, Func<DateTimeOffset> Now,
    Func<string, ClaudeDesktopContract>? ScheduledContract = null)
{
    internal static readonly ClaudeShelfEnvironment Real = new(ClaudeDesktopProbe.Observe, ClaudeDesktopProbe.FindPackage,
        ClaudeDesktopProbe.Contract, ClaudeDesktopProbe.Launch, () => DateTimeOffset.Now, ClaudeDesktopProbe.ScheduledContract);
}

/// <summary>What one background pass observed. <see cref="Reservation"/> is the reopen request that the closure
/// accepted in this pass, if any. <see cref="SwitchedFrom"/> is the account the app left when this pass saw it
/// confirm another one.</summary>
internal sealed record ClaudeShelfPass(ClaudeShelfStatus Status, Guid? Reservation, ClaudeDesktopPackage? Package,
    string? SwitchedFrom = null, int RelaunchChoice = 0);

/// <summary>
/// Owned by the widget. Every few seconds it observes Desktop off the UI thread. While Desktop runs it follows the
/// app's own log to learn the signed-in account and how many sessions that account still lacks, and every minute, and at
/// once when the app signs out or comes to another account, it fills the lists of the accounts the app has not shown for
/// a while (<see cref="SettleWindow"/>), all of which it watched. Once Desktop has been fully closed for a moment it fills
/// every registered account's session list (adding sessions, or bringing an older state of a conversation continued
/// elsewhere up to date in place). It relaunches Desktop when the user asked for that, or after a switch to an account
/// that lacked sessions; otherwise a closed app stays closed.
/// <para>It also keeps what each account changed (<see cref="ClaudeSessionLayout"/>), which the
/// <c>claude-session-sync</c> plan reads: the mapped accounts and their labels, when the app came to and left each one,
/// and checkpoints of the account the app comes to and of the account a plan ran in, with what changed there since the
/// checkpoint before.</para>
/// </summary>
internal sealed class ClaudeShelfService : IDisposable
{
    private static readonly TimeSpan ClosedGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan PendingInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan AccountsInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryInterval = TimeSpan.FromSeconds(60);
    private static readonly TimeSpan RetryWindow = TimeSpan.FromMinutes(30);
    private static readonly TimeSpan RelaunchWindow = TimeSpan.FromMinutes(30);
    // How long the account the app came to is retried for its first checkpoint, while it cannot be read whole.
    private static readonly TimeSpan ArrivalWindow = TimeSpan.FromMinutes(2);
    // While the app runs, how long after it last showed an account that account's list is left alone: the app ends that
    // account's Code sessions, and writes their records, some seconds after it shows the next one. The same time must
    // have been watched, so an account shown before the widget looked is past it too.
    internal static readonly TimeSpan SettleWindow = TimeSpan.FromMinutes(2);
    private readonly Control _owner;
    private readonly Func<IReadOnlyList<ClaudeProfile>> _profiles;
    private ClaudeSessionShelf _shelf;
    // Whether this service made its own lists and stores, and so follows the app's profile folder (FollowAppData).
    private readonly bool _followAppData;
    private DateTimeOffset _appDataCheckAt = DateTimeOffset.MinValue;
    private readonly ClaudeShelfEnvironment _environment;
    private readonly ClaudeDesktopLogWatcher _log;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 3000 };
    // Worker-owned state (only the single in-flight tick touches it).
    private IReadOnlyList<ClaudeShelfAccount> _accounts = [];
    private string _accountsKey = "";
    private DateTimeOffset _accountsAt = DateTimeOffset.MinValue, _cycleAt = DateTimeOffset.MinValue;
    // The app's state the last pass while it runs was made for: a change starts the next one at once.
    private string? _cycleFor;
    private int? _pending;
    private DateTimeOffset? _closedSince, _retryAt, _closureStarted;
    private bool _closureDone;
    private DateTimeOffset? _lastFillAt;
    private int _lastCreated;
    private bool _lastComplete = true;
    private ClaudeShelfProblem _problem;
    private string? _problemDetail;
    // The lists the last fill of each left held.
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);
    // The lists holding a record that does not parse, since when, and the labels of those that have for a while
    // (see NoteUnreadable).
    private readonly Dictionary<string, DateTimeOffset> _unreadable = new(StringComparer.Ordinal);
    private static readonly TimeSpan UnreadableGrace = TimeSpan.FromMinutes(2);
    private string? _damaged;
    private string? _unreviewed;
    private string? _confirmedKey;
    // The accounts the app shows (see NoteShown), when it last did, and since when this widget has seen every line of its
    // log, or the app closed.
    private HashSet<string> _shown = new(StringComparer.Ordinal);
    private readonly Dictionary<string, DateTimeOffset> _shownAt = new(StringComparer.Ordinal);
    private DateTimeOffset? _watchedSince;
    // Counts the user's reopen choices, so a pass that started before one never overrides it.
    private int _relaunchChoice;
    // What each account changed, kept by this widget alone; null until read or started.
    private ClaudeSessionSync.Sources _layoutSources;
    private ClaudeSessionLayout? _layout;
    private bool _layoutChanged;
    // Per mapped account, where a plan command asks for its plan (see ClaudeSessionSync.Ask).
    private readonly Dictionary<string, PlanChannel> _channels = new(StringComparer.Ordinal);
    // The account the app came to, until its first checkpoint there is taken or the window closes.
    private (string Account, DateTimeOffset Until)? _arrival;
    // Until this widget sees the app confirm an account, and again after lines of its log were lost, a switch it then
    // sees happened at a time it cannot tell: the account left is not dated.
    private bool _unwatched = true;
    // The scheduled-task alignment (see Schedule): when it last ran, the lists as it last read or wrote them, the app's
    // scheduled-task lines it last ran for, and what it reports once that has lasted.
    private static readonly TimeSpan ScheduledInterval = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ScheduledRetry = TimeSpan.FromMinutes(1);
    // While the app runs, how long after its last scheduled-task line (a reset, a load) nothing is written: the lines of
    // one account change come within a few seconds.
    internal static readonly TimeSpan ScheduledQuiet = TimeSpan.FromSeconds(6);
    private static readonly IReadOnlyDictionary<string, string> NoStamps = new Dictionary<string, string>();
    private static readonly JsonSerializerOptions AnswerOptions = new()
    { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private ClaudeScheduledSync _scheduled;
    private ClaudeScheduledChannel? _scheduledChannel;
    // The package whose running image this pass found reviewed (null: none): lists are written only while it still runs.
    private ClaudeDesktopPackage? _scheduledPackage;
    private IReadOnlyDictionary<string, string>? _scheduledStamps;
    private string? _scheduledPhase;
    private long _scheduledMark = -1;
    private DateTimeOffset _scheduledAt = DateTimeOffset.MinValue;
    private bool _scheduledWaiting;
    private DateTimeOffset? _scheduledTroubleSince;
    private ClaudeScheduledProblem? _scheduledShown;
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
        _followAppData = shelf is null && layoutSources is null;
        _environment = environment ?? ClaudeShelfEnvironment.Real; _log = log ?? new ClaudeDesktopLogWatcher();
        _layoutSources = layoutSources ?? ClaudeSessionSync.Sources.For(_shelf.Paths);
        _scheduled = NewScheduled();
        // The ledger folder stays when the app's profile folder moves, and so does where a mirror is asked for.
        _scheduledChannel = _environment.ScheduledContract is null ? null : ClaudeScheduledChannel.Open(_shelf.Paths.LedgerDirectory);
        _timer.Tick += (_, _) => Tick();
    }

    /// <summary>The scheduled-task mirror over the shelf's lists and store.</summary>
    private ClaudeScheduledSync NewScheduled() =>
        new(_shelf.Paths.SessionsRoot, Path.Combine(_shelf.Paths.LedgerDirectory, ClaudeScheduledSync.StoreFolder),
            _shelf.Paths.ScheduledTasksRoot, _shelf.Paths.AppScheduledTasksRoot, _shelf.AcquireLease);

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
        _ = Task.Run(() => WorkAndRelease(profiles, reservation)).ContinueWith(task =>
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
    /// <summary>For tests: these accounts stay until the next call, however much time passes (no profile resolves to them).</summary>
    internal void SetAccountsForTest(IReadOnlyList<ClaudeShelfAccount> accounts)
    {
        Volatile.Write(ref _accounts, accounts);
        _accountsKey = ""; _accountsAt = DateTimeOffset.MaxValue;
    }

    private void Publish(ClaudeShelfStatus status)
    {
        var changed = status != Status;
        Status = status;
        if (changed && !_disposed) StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    // What the process may allocate before the collector is asked to give it back at once, and the count at the last time.
    private const long ReleaseAfterBytes = 128L * 1024 * 1024;
    private long _allocatedAtRelease;

    /// <summary>One background pass, then a collection once enough was allocated since the last one. A pass that reads
    /// the lists whole (the first one, the plan a fill publishes, a plan made for the session sync) leaves a few hundred
    /// megabytes of large arrays behind at once. So little is allocated otherwise that the collector does not come for
    /// them for many minutes, and each left the process a step higher: 0.3 GB after start, 1.4 GB a quarter of an hour
    /// later (measured 2026-11-14). A compacting collection after every 128 MB allocated, which is right after such a
    /// pass and some ten minutes apart otherwise, gives the memory back.</summary>
    private ClaudeShelfPass WorkAndRelease(IReadOnlyList<ClaudeProfile> profiles, Guid? reservation)
    {
        try { return Work(profiles, reservation); }
        finally
        {
            // Counted since the last such collection, not per pass: records read again a few at a time (their kept
            // reads running out) leave as much behind over ten minutes as one heavy pass does at once.
            if (GC.GetTotalAllocatedBytes(false) - _allocatedAtRelease > ReleaseAfterBytes)
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Aggressive, blocking: true, compacting: true);
                _allocatedAtRelease = GC.GetTotalAllocatedBytes(false);
            }
        }
    }

    /// <summary>Background pass. Only one runs at a time; it owns the worker state below.</summary>
    internal ClaudeShelfPass Work(IReadOnlyList<ClaudeProfile> profiles, Guid? reservation)
    {
        var now = _environment.Now();
        var choice = Volatile.Read(ref _relaunchChoice);
        FollowAppData(now);
        RefreshAccounts(profiles, now);
        KeepLayout(now);
        var runtime = _environment.Observe();
        string? switchedFrom = null;
        Guid? accepted = null;
        ClaudeDesktopPackage? package = null;
        ClaudeDesktopAccountState app;
        switch (runtime.State)
        {
            case ClaudeDesktopRunState.Running:
                _closedSince = null; _closureDone = false; _retryAt = null; _closureStarted = null;
                // Report an incompatible version while the app runs, so nothing promises a fill that will not run.
                package = _environment.FindPackage();
                var contract = Review(package);
                app = Watch(runtime, now);
                if (app.Key is { } confirmed && _accounts.Any(account => account.Key == confirmed))
                {
                    if (confirmed != _confirmedKey)
                    {
                        // When the app confirmed the account, as its log tells.
                        var at = app.ObservedAt is { } observed && new DateTimeOffset(observed) is var logged && logged <= now ? logged : now;
                        if (_confirmedKey is not { } left) NoteActive(confirmed);
                        else if (_unwatched) NoteSwitch(left, confirmed, at, now, leftAt: null);
                        else { switchedFrom = left; NoteSwitch(left, confirmed, at, now, leftAt: at); }
                        _confirmedKey = confirmed;
                    }
                    _unwatched = false;
                    // An account without a checkpoint (the first time, or new) is checkpointed as soon as it can be read.
                    if (_arrival is null && _layout is not null && !_layout.Checkpoints.ContainsKey(confirmed))
                        _arrival = (confirmed, now + ArrivalWindow);
                }
                CheckpointArrival(now);
                ServePlans(now, app.Key is { } shown && shown == _confirmedKey ? shown : null);
                // Every minute, and at once when the app's account changes (a sign-out first: the account the user goes
                // to next is filled before the app reads it), fill what may be written and count what the shown list lacks.
                var cycle = app.Status is ClaudeDesktopAccountStatus.SignedIn or ClaudeDesktopAccountStatus.SignedOut
                    or ClaudeDesktopAccountStatus.Unregistered ? app.Status + ":" + (app.Key ?? app.AccountUuid) : null;
                if (cycle is null) { _pending = null; _cycleFor = null; }
                else if (cycle != _cycleFor || now - _cycleAt >= PendingInterval)
                {
                    _cycleFor = cycle; _cycleAt = now;
                    _pending = FillRunning(app, package, contract, now);
                }
                break;
            case ClaudeDesktopRunState.Closed:
                app = _log.Update(null, _accounts);
                NoteShown(app, now);
                _watchedSince ??= now;
                ServePlans(now, null);
                _pending = null; _cycleFor = null;
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
                // An uninspectable process: neither write nor claim the app is closed. What it showed meanwhile is not
                // seen, so the time watched starts again.
                app = _log.State.Status == ClaudeDesktopAccountStatus.Closed ? ClaudeDesktopAccountState.Unknown : _log.State;
                NoteShown(ClaudeDesktopAccountState.Unknown, now);
                _watchedSince = null;
                break;
        }
        Schedule(runtime, package, now);
        var profileId = app.Key is { } appKey ? _accounts.FirstOrDefault(account => account.Key == appKey)?.ProfileId : null;
        return new ClaudeShelfPass(new ClaudeShelfStatus(runtime.State, app, profileId, _pending, _accounts.Count, _lastFillAt,
            _lastCreated, _lastComplete, _problem, _problemDetail, null, runtime.HostStartedAt, _unreviewed, _damaged,
            _scheduledShown), accepted,
            package ?? _environment.FindPackage(), switchedFrom, choice);
    }

    /// <summary>
    /// Mirrors the scheduled tasks a person chose into the lists of the accounts on this PC (<see cref="ClaudeScheduledSync"/>):
    /// when a list it read or wrote changed on disk, a few seconds after the app resets or loads its list, when the app
    /// starts, stops or has been closed for a moment, every ten minutes, every minute while a list waits to be written,
    /// and when a command asks (a plan, read only, whatever the app's version; an apply). While the app runs only a list
    /// other than the one its log shows loaded is written, and none while that cannot be told (its log not read whole, a
    /// reset not followed by a load yet, a line a few seconds old), asked again before each file; once the app has been
    /// closed for a moment every list is, its state read again before each file. Nothing is written while that state is
    /// unknown, or unless the installed version's way of keeping the lists reads as reviewed and the app running is that
    /// installation. A problem that lasts, and what a mirror left for a person to settle, is reported in the status.
    /// </summary>
    private void Schedule(ClaudeDesktopRuntime runtime, ClaudeDesktopPackage? package, DateTimeOffset now)
    {
        if (_environment.ScheduledContract is not { } review) return;
        var channel = _scheduledChannel;
        (string Id, ClaudeScheduledRequest? Request)? asked;
        try { asked = channel?.Request(); }
        catch (ObjectDisposedException) { return; }
        void Answer(JsonObject answer)
        {
            if (asked is not { } request) return;
            try { channel!.Answer(request.Id, answer.ToJsonString(AnswerOptions)); }
            catch (ObjectDisposedException) { }
        }
        if (asked is { Request: null })
        {
            Answer(new JsonObject { ["status"] = "invalid_request" });
            asked = null;
        }
        if (runtime.State == ClaudeDesktopRunState.Unknown)
        {
            Answer(new JsonObject { ["status"] = "app_state_unknown" });
            return;
        }
        var closedLong = runtime.State == ClaudeDesktopRunState.Closed && _closedSince is { } closed && now - closed >= ClosedGrace;
        var phase = runtime.State == ClaudeDesktopRunState.Closed ? closedLong ? "closed" : "closing" : "running";
        var log = _log.Scheduled;
        // The app reset or loaded its list: once its lines have settled, the list it left may be written.
        var marked = log.Mark != _scheduledMark && (log.LastLine is not { } last || now >= last + ScheduledQuiet);
        var changed = _scheduledStamps is not { } stamps
            || _scheduled.Stamps(stamps.Keys).Any(pair => stamps.GetValueOrDefault(pair.Key) != pair.Value);
        if (asked is null && !changed && !marked && phase == _scheduledPhase
            && now - _scheduledAt < (_scheduledWaiting ? ScheduledRetry : ScheduledInterval)) return;
        _scheduledPhase = phase; _scheduledAt = now;
        if (marked) _scheduledMark = log.Mark;
        package ??= _environment.FindPackage();
        var reviewed = package is not null && review(package.AsarPath) == ClaudeDesktopContract.Reviewed
                       && ClaudeDesktopProbe.RunsFrom(runtime, package);
        _scheduledPackage = reviewed ? package : null;
        Func<string, bool> mayWrite = !reviewed ? _ => false
            : runtime.State == ClaudeDesktopRunState.Running ? MayWriteScheduled
            : closedLong ? _ => _environment.Observe().State == ClaudeDesktopRunState.Closed : _ => false;
        var running = runtime.State == ClaudeDesktopRunState.Running;
        ClaudeScheduledScene? Scene() => _scheduled.Scene(_accounts, running ? log : null, running);
        if (asked is { Request: { } request })
        {
            JsonObject answer;
            try { answer = Scene() is { } scene ? _scheduled.Serve(request, scene, mayWrite, now, reviewed) : new() { ["status"] = "sessions_unlisted" }; }
            catch (Exception exception) { answer = new JsonObject { ["status"] = "failed", ["failure"] = exception.Message }; }
            Answer(answer);
        }
        if (!reviewed)
        {
            _scheduledShown = new(ClaudeScheduledProblemKind.Unreviewed, package?.Version.ToString());
            _scheduledStamps = NoStamps;
            return;
        }
        ClaudeScheduledPass pass;
        try { pass = Scene() is { } scene ? _scheduled.Pass(scene, mayWrite, now) : new(0, [], [], "sessions_unlisted", null); }
        catch (Exception exception) { pass = new(0, [], [], exception.Message, null); }
        _scheduledStamps = pass.Processed ?? NoStamps;
        _scheduledWaiting = pass.Waiting.Count > 0;
        var trouble = pass.Failure is { } failure ? new ClaudeScheduledProblem(ClaudeScheduledProblemKind.Failed, failure)
            : pass.Unreadable.Count > 0 ? new ClaudeScheduledProblem(ClaudeScheduledProblemKind.Unreadable, Names(pass.Unreadable))
            : null;
        if (trouble is null) _scheduledTroubleSince = null;
        else { _scheduledTroubleSince ??= now; _scheduledWaiting = true; }
        _scheduledShown = trouble is not null && now - _scheduledTroubleSince >= UnreadableGrace ? trouble : pass.Notice;
    }

    /// <summary>The first three names, and how many more.</summary>
    private static string Names(IReadOnlyList<string> names) =>
        string.Join(", ", names.Take(3)) + (names.Count > 3 ? $" +{names.Count - 3}" : "");

    /// <summary>While the app runs, whether a scheduled-task list may be written now, read again before every file: the
    /// app running is the installation found reviewed, every line of its log in this run was read, its scheduled-task
    /// manager has a list loaded and its last line is a few seconds old, and this list is another one, by name and on
    /// disk.</summary>
    private bool MayWriteScheduled(string key)
    {
        var now = _environment.Now();
        var runtime = _environment.Observe();
        if (runtime.State != ClaudeDesktopRunState.Running || _scheduledPackage is not { } package
            || !ClaudeDesktopProbe.RunsFrom(runtime, package)) return false;
        Watch(runtime, now);
        var log = _log.Scheduled;
        return log is { Known: true, Pending: false, Loaded: { } loaded }
               && (log.LastLine is not { } last || now >= last + ScheduledQuiet)
               && key != loaded && !_scheduled.SameFolder(key, loaded);
    }

    /// <summary>Every minute, when this service made its own lists, moves them to the profile folder the app uses now: the
    /// packaged app can move it out of its package at a launch (<see cref="ClaudeAppData"/>), and lists written to the
    /// folder it left would never be read. The ledger and session layouts stay where they are.</summary>
    private void FollowAppData(DateTimeOffset now)
    {
        if (!_followAppData || now < _appDataCheckAt) return;
        _appDataCheckAt = now + AccountsInterval;
        var root = ClaudeAppData.Root();
        if (string.Equals(Path.Combine(root, ClaudeAppData.SessionsFolder), Path.TrimEndingDirectorySeparator(
                Path.GetFullPath(_shelf.Paths.SessionsRoot)), StringComparison.OrdinalIgnoreCase)) return;
        _shelf = new ClaudeSessionShelf(ClaudeShelfPaths.CreateDefault(appRoot: root));
        _scheduled = NewScheduled();
        _layoutSources = ClaudeSessionSync.Sources.For(_shelf.Paths);
    }

    /// <summary>Reads the installed app's version against the shelf design: an incompatible one stops filling and is
    /// reported as a problem; an unreviewed one is reported alongside (<see cref="ClaudeShelfStatus.Unreviewed"/>).</summary>
    private ClaudeDesktopContract Review(ClaudeDesktopPackage? package)
    {
        var contract = package is null ? ClaudeDesktopContract.Incompatible : _environment.Contract(package.AsarPath);
        if (contract == ClaudeDesktopContract.Incompatible)
        { _problem = ClaudeShelfProblem.Incompatible; _problemDetail = package?.Version.ToString(); }
        else if (_problem == ClaudeShelfProblem.Incompatible) { _problem = ClaudeShelfProblem.None; _problemDetail = null; }
        _unreviewed = contract == ClaudeDesktopContract.Unreviewed ? package!.Version.ToString() : null;
        return contract;
    }

    /// <summary>Reads what the app's log says now, and keeps which accounts it shows and since when every line was seen.</summary>
    private ClaudeDesktopAccountState Watch(ClaudeDesktopRuntime runtime, DateTimeOffset now)
    {
        var app = _log.Update(runtime.HostStartedAt, _accounts);
        // Lines lost could hide a switch: the account left then is not dated, and the time watched starts again.
        if (_log.LostLines) { _unwatched = true; _watchedSince = now; }
        else _watchedSince ??= now;
        NoteShown(app, now);
        return app;
    }

    /// <summary>
    /// Keeps which accounts the app shows: every organization of the account it shows or signs in to, and, until it
    /// confirms another account, the ones it showed before (signed out, on an account not registered, or in a state it
    /// cannot tell, it has not let them go: it ends an account's Code sessions only once it shows the next one). Those come
    /// from its log as well (<see cref="ClaudeDesktopLogWatcher.Held"/>), so a widget started while the app is signed out
    /// knows them too. An account it stops showing, or closes on, was shown until now.
    /// </summary>
    private void NoteShown(ClaudeDesktopAccountState app, DateTimeOffset now)
    {
        IEnumerable<string> Mapped(string account) =>
            _accounts.Where(mapped => string.Equals(mapped.AccountUuid, account, StringComparison.OrdinalIgnoreCase))
                .Select(mapped => mapped.Key);
        var showing = (app.Status == ClaudeDesktopAccountStatus.SignedIn && app.AccountUuid is { } account ? Mapped(account) : [])
            .Concat(app.Status == ClaudeDesktopAccountStatus.Closed ? [] : _log.Held.SelectMany(Mapped));
        var shown = app.Confirmed || app.Status == ClaudeDesktopAccountStatus.Closed
            ? showing.ToHashSet(StringComparer.Ordinal)
            : _shown.Concat(showing).ToHashSet(StringComparer.Ordinal);
        foreach (var key in shown.Concat(_shown)) _shownAt[key] = now;
        _shown = shown;
    }

    /// <summary>
    /// While the app runs, whether a list may be written (null: any list): an account mapped, not shown now and not for the
    /// settle window, while the app's account is known (one confirmed, signed out, or one not registered) and every line of
    /// its log was seen for the settle window.
    /// </summary>
    private bool MayWriteWhileRunning(ClaudeDesktopAccountState app, string? target, DateTimeOffset now)
    {
        if (_watchedSince is not { } since || now - since < SettleWindow
            || app.Status is not (ClaudeDesktopAccountStatus.SignedIn or ClaudeDesktopAccountStatus.SignedOut
                or ClaudeDesktopAccountStatus.Unregistered)) return false;
        bool Settled(string key) => !_shown.Contains(key) && (!_shownAt.TryGetValue(key, out var at) || now - at >= SettleWindow);
        return _accounts.Any(account => (target is null || account.Key == target) && Settled(account.Key));
    }

    /// <summary>Asked before every file a fill writes while the app runs (see <see cref="ClaudeSessionShelf.Apply(IReadOnlyList{ClaudeShelfAccount}, Func{string?, bool}, string?, bool)"/>):
    /// the app's state is read again, so a list it starts to sign in to is left before the next file.</summary>
    private bool MayWrite(string? target)
    {
        var now = _environment.Now();
        var runtime = _environment.Observe();
        return runtime.State == ClaudeDesktopRunState.Running && MayWriteWhileRunning(Watch(runtime, now), target, now);
    }

    /// <summary>One pass while the app runs: fills the lists that may be written, and returns what the list the app shows
    /// lacks (null when it shows none or that is not known). That list is filled when the app closes.</summary>
    private int? FillRunning(ClaudeDesktopAccountState app, ClaudeDesktopPackage? package, ClaudeDesktopContract contract,
        DateTimeOffset now)
    {
        try
        {
            if (_accounts.Count < 2 || package is null || contract == ClaudeDesktopContract.Incompatible
                || !MayWriteWhileRunning(app, null, now))
            {
                if (app.Key is not { } key) return null;
                var seen = _shelf.Plan(_accounts);
                NoteUnreadable(seen, now);
                return seen.PendingFor(key);
            }
            var result = _shelf.Apply(_accounts, MayWrite, package.Version.ToString(), running: true);
            NoteUnreadable(result.Plan, now);
            var targets = result.Targets ?? new HashSet<string>();
            if (result.Written > 0) { _lastFillAt = now; _lastCreated = result.Created; }
            if (targets.Count > 0) _lastComplete = result.Plan.IsCompleteFor(targets);
            foreach (var target in targets)
                if (result.Plan.Held.ContainsKey(target)) _held.Add(target);
                else _held.Remove(target);
            if (_problem is ClaudeShelfProblem.None or ClaudeShelfProblem.Held)
            {
                _problem = _held.Count > 0 ? ClaudeShelfProblem.Held : ClaudeShelfProblem.None;
                _problemDetail = _held.Count > 0 ? result.Plan.Held.Values.FirstOrDefault() ?? _problemDetail : null;
            }
            return app.Key is { } shown ? result.Plan.PendingFor(shown) : null;
        }
        catch (ClaudeDesktopRunningException exception)
        {
            // The app came to a list this pass writes: the next pass plans again.
            if (exception.Created > 0) { _lastFillAt = now; _lastCreated = exception.Created; _lastComplete = false; }
            return null;
        }
        catch (Exception)
        {
            // A list changed while preparing, the installer holds the lease, or anything else: the next pass tries again.
            return null;
        }
    }

    /// <summary>Keeps which lists hold a record that does not parse. The app counts the transcripts of such a record only
    /// as far as a text scan still finds their ids (Claude 5.11492), so one that is empty or cut short may claim nothing,
    /// and a delete of the same conversation in another account would then take its transcripts. A record being written
    /// passes within moments; one that stays is reported, so the user archives rather than deletes until it reads again.</summary>
    private void NoteUnreadable(ClaudeShelfPlan plan, DateTimeOffset now)
    {
        foreach (var key in _unreadable.Keys.Where(key => plan.Held.GetValueOrDefault(key) != "unreadable").ToArray())
            _unreadable.Remove(key);
        foreach (var (key, reason) in plan.Held)
            if (reason == "unreadable") _unreadable.TryAdd(key, now);
        var labels = _accounts.Where(account => _unreadable.TryGetValue(account.Key, out var since) && now - since >= UnreadableGrace)
            .Select(account => account.Label).ToArray();
        _damaged = labels.Length > 0 ? string.Join(", ", labels) : null;
    }

    /// <summary>Keeps <see cref="ClaudeSessionLayout"/>: starts it the first time, keeps the mapped accounts and their
    /// labels, and checkpoints the account the app shows when a plan ran there.</summary>
    private void KeepLayout(DateTimeOffset now)
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
            // The first format kept per-pair agreements: its accounts carry over in the current one.
            if (kept.Upgraded) _layoutChanged = true;
            _layout = kept;
            // The account the app showed before this widget started, so a switch made meanwhile is still told.
            _confirmedKey ??= kept.Active;
        }
        if (_accounts.Count > 0)
        {
            var mapped = _accounts.DistinctBy(account => account.Key)
                .ToDictionary(account => account.Key, account => account.Label, StringComparer.Ordinal);
            foreach (var key in _layout.Accounts.Keys.Where(key => !mapped.ContainsKey(key)).ToArray())
            { _layout.Forget(key); _layoutChanged = true; }
            foreach (var (key, label) in mapped)
                if (!_layout.Accounts.TryGetValue(key, out var known)) { _layout.Accounts[key] = new(label); _layoutChanged = true; }
                else if (known.Label != label) { _layout.Accounts[key] = known with { Label = label }; _layoutChanged = true; }
        }
        foreach (var key in _channels.Keys.Where(key => !_layout.Accounts.ContainsKey(key)).ToArray())
        {
            _channels[key].Dispose();
            _channels.Remove(key);
        }
        foreach (var key in _layout.Accounts.Keys.Where(key => !_channels.ContainsKey(key)))
            if (PlanChannel.Open(key) is { } channel) _channels[key] = channel;
        SaveLayout();
    }

    /// <summary>Answers the plan commands waiting: for the account the app shows, it checkpoints the account and makes
    /// the plan from there, keeping what the plan sets out to change, so the plan applied is the one noted here; for
    /// another, or while nothing can be saved, a plan for viewing only.</summary>
    private void ServePlans(DateTimeOffset now, string? shown)
    {
        if (_layout is null) return;
        foreach (var (key, channel) in _channels)
        {
            if (!channel.Planned.WaitOne(0) || channel.Request() is not { } request) continue;
            try
            {
                var plan = ClaudeSessionSync.Serve(_layout, _layoutSources, key, request.Self, active: key == shown, now);
                if (plan.Checkpointed == true)
                {
                    if (_arrival?.Account == key) _arrival = null;
                    _layoutChanged = true;
                    if (!SaveLayout()) plan = plan with { Checkpointed = false };
                }
                channel.Answer(request.Id, ClaudeSessionSync.Serialize(plan));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    /// <summary>The first layout: the mapped accounts, with the account the old reference names (if any) taken as the one
    /// the app left last, when it was chosen. Null while an old reference that is there cannot be read; a later pass
    /// tries again.</summary>
    private ClaudeSessionLayout? StartLayout()
    {
        var (reference, known) = ClaudeSessionReference.Read(_layoutSources.Ledger);
        if (!known) return null;
        var layout = new ClaudeSessionLayout();
        foreach (var account in _accounts.DistinctBy(account => account.Key))
            layout.Accounts[account.Key] = new(account.Label, reference?.Account == account.Key ? reference.SetAt : null);
        return layout;
    }

    /// <summary>The app left one account for another, coming to it at <paramref name="at"/>: what changed in the account
    /// it left was changed by <paramref name="leftAt"/> (null when that cannot be told, which leaves its time as it was),
    /// and the account it came to starts a stay, checkpointed as soon as it can be read.</summary>
    private void NoteSwitch(string left, string arrived, DateTimeOffset at, DateTimeOffset now, DateTimeOffset? leftAt)
    {
        _arrival = (arrived, now + ArrivalWindow);
        if (_layout is null) return;
        if (leftAt is not null && _layout.Accounts.TryGetValue(left, out var leaving))
            _layout.Accounts[left] = leaving with { LeftAt = leftAt };
        if (_layout.Accounts.TryGetValue(arrived, out var coming))
            _layout.Accounts[arrived] = coming with { ArrivedAt = at, Stay = coming.Stay + 1 };
        _layout.Active = arrived;
        _layoutChanged = true;
        SaveLayout();
    }

    /// <summary>The first account this widget sees the app confirm, with none known before it.</summary>
    private void NoteActive(string account)
    {
        if (_layout is null || _layout.Active == account) return;
        _layout.Active = account;
        _layoutChanged = true;
        SaveLayout();
    }

    /// <summary>Checkpoints the account the app came to once it can be read, so what changes there from now on is told
    /// from what it brought from its last stay.</summary>
    private void CheckpointArrival(DateTimeOffset now)
    {
        if (_arrival is not { } arrival || _layout is null) return;
        if (now > arrival.Until || arrival.Account != _confirmedKey) { _arrival = null; return; }
        try
        {
            if (!ClaudeSessionSync.Checkpoint(_layout, _layoutSources, arrival.Account, now)) return;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return; }
        _arrival = null;
        _layoutChanged = true;
        SaveLayout();
    }

    /// <summary>Saves what changed; false when it could not be saved.</summary>
    private bool SaveLayout()
    {
        if (!_layoutChanged || _layout is null) return true;
        try { _layout.Save(_layoutSources.Ledger); _layoutChanged = false; return true; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
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
        if (Review(package) == ClaudeDesktopContract.Incompatible) return package;
        try
        {
            var result = _shelf.Apply(_accounts, () => _environment.Observe().State == ClaudeDesktopRunState.Closed,
                package!.Version.ToString());
            NoteUnreadable(result.Plan, now);
            _lastFillAt = now; _lastCreated = result.Created;
            // A sidebar sync that could not finish is retried like a held list.
            _lastComplete = result.Plan.IsComplete && result.Prefs?.Complete != false;
            _held.Clear();
            _held.UnionWith(result.Plan.Held.Keys);
            _problem = _held.Count > 0 ? ClaudeShelfProblem.Held : ClaudeShelfProblem.None;
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
        // A pass may still be running: it finds the channel gone and answers nothing. Its other handles go with the process.
        Interlocked.Exchange(ref _scheduledChannel, null)?.Dispose();
    }

    /// <summary>The layout this widget keeps, for tests: read after a pass, never while one runs.</summary>
    internal ClaudeSessionLayout? LayoutForTest => _layout;
}

/// <summary>Where a plan command asks the widget for one account's plan: its request and answer events and the shared
/// memory that carries both (see <see cref="ClaudeSessionSync.Ask"/>).</summary>
internal sealed class PlanChannel(EventWaitHandle planned, EventWaitHandle answered, MemoryMappedFile mailbox,
    MemoryMappedViewAccessor view) : IDisposable
{
    internal EventWaitHandle Planned => planned;

    internal static PlanChannel? Open(string account)
    {
        EventWaitHandle? planned = null, answered = null;
        MemoryMappedFile? mailbox = null;
        try
        {
            planned = new EventWaitHandle(false, EventResetMode.AutoReset, ClaudeSessionSync.PlannedEventName(account));
            answered = new EventWaitHandle(false, EventResetMode.AutoReset, ClaudeSessionSync.CheckpointedEventName(account));
            mailbox = MemoryMappedFile.CreateOrOpen(ClaudeSessionSync.MailboxName(account), ClaudeSessionSync.MailboxBytes,
                MemoryMappedFileAccess.ReadWrite, MemoryMappedFileOptions.DelayAllocatePages, HandleInheritability.None);
            return new(planned, answered, mailbox, mailbox.CreateViewAccessor(0, ClaudeSessionSync.MailboxBytes));
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException
                                              or WaitHandleCannotBeOpenedException)
        {
            planned?.Dispose(); answered?.Dispose(); mailbox?.Dispose();
            return null;
        }
    }

    /// <summary>The request waiting: its id and the calling session; null when it is not one.</summary>
    internal (string Id, string Self)? Request()
    {
        try
        {
            if (ClaudeSessionSync.ReadSlot(view, 0, ClaudeSessionSync.AnswerOffset) is not { } text
                || JsonNode.Parse(text) is not JsonObject request
                || request["id"] is not JsonValue idValue || !idValue.TryGetValue<string>(out var id) || id.Length is 0 or > 64
                || request["self"] is not JsonValue selfValue || !selfValue.TryGetValue<string>(out var self)
                || !self.StartsWith("local_", StringComparison.Ordinal) || self.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
                return null;
            return (id, self);
        }
        catch (System.Text.Json.JsonException) { return null; }
    }

    internal void Answer(string id, string plan)
    {
        ClaudeSessionSync.WriteSlot(view, ClaudeSessionSync.AnswerOffset, ClaudeSessionSync.MailboxBytes - ClaudeSessionSync.AnswerOffset,
            System.Text.Encoding.UTF8.GetBytes(id + "\n" + plan));
        answered.Set();
    }

    public void Dispose()
    {
        view.Dispose(); mailbox.Dispose(); planned.Dispose(); answered.Dispose();
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
