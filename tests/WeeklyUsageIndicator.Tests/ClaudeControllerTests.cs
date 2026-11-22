using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using WeeklyUsageIndicator;

internal static class ClaudeControllerTests
{
    internal static async Task RunAsync()
    {
        await RegisteringSameDefaultKeepsAcceptedUsageAsync();
        await AccountIntervalsUseVerifiedIdentityAsync();
        await RegisteringChangedDefaultInvalidatesAcceptedUsageAsync();
        await LoginWaitsOnlyForItsOwnAccountAsync();
        await ReadsRunSideBySideUpToThreeAsync();
        await TransientStatusFailureKeepsVerifiedObservationAsync();
        await RejectedIdentityImmediatelyInvalidatesAsync();
        await LoginFailureRemainsVisibleUntilRecoveryAsync();
        await ChangedBindingCannotReuseObservationAsync();
        await SavedValuesStayPerAccountAsync();
        await MovingTheDefaultHomeAccountAsync();
        await MovingRejectsAnotherAccountAsync();
        await ReadRetiresLeftoverLinksAsync();
        await DisposalCancelsWaitersWithoutDisposingTheirGateAsync();
    }

    private static async Task AccountIntervalsUseVerifiedIdentityAsync()
    {
        using var fixture = new Fixture();
        var selected = fixture.Store.SelectedProfileId;
        var main = await fixture.Controller.ReadUsageAsync(null, CancellationToken.None);
        var duplicate = fixture.Store.CreateProfile("Same account, separate profile");
        duplicate = fixture.Store.RecordVerifiedIdentity(duplicate.Id, Fixture.Identity(duplicate, fixture.Runner.Email));
        await ThrowsAsync<ClaudeUsageTemporarilyUnavailableException>(() =>
            fixture.Controller.ReadUsageAsync(duplicate.Id, CancellationToken.None));
        Check(fixture.SourceCalls == 1, "controller passes the verified account identity rather than the profile ID to the limiter");

        var second = fixture.Store.CreateProfile("Second account");
        fixture.Runner.Email = "second@example.test";
        second = fixture.Store.RecordVerifiedIdentity(second.Id, Fixture.Identity(second, fixture.Runner.Email));
        var additional = await fixture.Controller.ReadUsageAsync(second.Id, CancellationToken.None);
        Check(!additional.IsStale && fixture.SourceCalls == 2
            && fixture.Controller.LastUsage("default") == main
            && fixture.Controller.LastUsage(second.Id) == additional
            && fixture.Store.SelectedProfileId == selected,
            "main and additional account reads succeed in sequence while preserving selection and separate observations");
        await fixture.Controller.ReadUsageAsync(second.Id, CancellationToken.None);
        Check(fixture.SourceCalls == 2, "repeating the additional account read reuses its accepted value");
    }

    private static async Task RegisteringSameDefaultKeepsAcceptedUsageAsync()
    {
        foreach (var alreadyRegistered in new[] { false, true })
        {
            using var fixture = new Fixture(registerDefault: alreadyRegistered);
            var initial = await fixture.Controller.ReadUsageAsync(null, CancellationToken.None);
            var invalidations = 0;
            fixture.Controller.UsageChanged += (_, update) =>
            { if (fixture.Controller.LastUsage(update.ProfileId) is null) invalidations++; };

            var registered = await fixture.Controller.RegisterDefaultAsync(CancellationToken.None);
            Check(registered.IsVerified && fixture.Controller.LastUsage("default") == initial,
                "connecting or relabeling the same default account preserves its accepted observation");
            var cached = await fixture.Controller.ReadUsageAsync(null, CancellationToken.None);
            Check(cached == initial && fixture.SourceCalls == 1 && invalidations == 0
                  && fixture.Cache.For(Fixture.KeyOf("default", Fixture.MainEmail)).Load() is not null,
                "same-identity registration keeps the existing client and recovery value without another usage call");
        }
    }

    private static async Task RegisteringChangedDefaultInvalidatesAcceptedUsageAsync()
    {
        foreach (var alreadyRegistered in new[] { false, true })
        {
            using var fixture = new Fixture(registerDefault: alreadyRegistered);
            await fixture.Controller.ReadUsageAsync(null, CancellationToken.None);
            var invalidations = 0;
            fixture.Controller.UsageChanged += (_, update) =>
            { if (fixture.Controller.LastUsage(update.ProfileId) is null) invalidations++; };

            fixture.Runner.Email = "changed@example.test";
            if (alreadyRegistered)
            {
                var before = fixture.Store.SelectedProfile;
                await ThrowsAsync<ClaudeIdentityChangedException>(() => fixture.Controller.RegisterDefaultAsync(CancellationToken.None));
                Check(fixture.Store.SelectedProfile == before,
                    "reconnecting cannot silently replace a saved default with an accidentally signed-in account");
            }
            else
            {
                var registered = await fixture.Controller.RegisterDefaultAsync(CancellationToken.None);
                Check(registered.Identity?.Key == Fixture.Identity(fixture.Store.DefaultProfile, fixture.Runner.Email).Key,
                    "the first explicit registration adopts the verified current account");
            }
            var previous = Fixture.KeyOf("default", Fixture.MainEmail);
            Check(fixture.Controller.LastUsage("default") is null && invalidations == 1
                  && fixture.Cache.For(previous).Load() is null && fixture.Cache.Last(previous) is not null,
                "a different default identity invalidates previous usage and ends its recovery, including an unregistered observation binding");
            Check(fixture.SourceCalls == 1, "registration verifies identity but does not add a usage request");
        }
    }

    private static async Task LoginWaitsOnlyForItsOwnAccountAsync()
    {
        using var fixture = new Fixture();
        var initial = await fixture.Controller.ReadUsageAsync(null, CancellationToken.None);
        using var loginCancellation = new CancellationTokenSource();
        var login = fixture.Controller.AddAsync("Pending login", loginCancellation.Token);
        await fixture.Runner.LoginStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var during = await fixture.Controller.ReadUsageAsync(null, CancellationToken.None, timeout: TimeSpan.FromSeconds(5));
        Check(!during.IsStale && during == initial && fixture.SourceCalls == 1 && fixture.Runner.StatusCalls == 2,
            "another profile's interactive login neither delays nor replaces the selected account's verified read");

        using var manualCancellation = new CancellationTokenSource();
        manualCancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => fixture.Controller.ReadUsageAsync(null, manualCancellation.Token));
        Check(fixture.Controller.LastUsage("default")?.Snapshot.Weekly == initial.Snapshot.Weekly,
            "explicit caller cancellation stays cancellation and does not erase accepted usage");

        // The same profile's sign-in holds that profile's gate: a read of it waits and starts no command meanwhile.
        using var reloginCancellation = new CancellationTokenSource();
        var relogin = fixture.Controller.LoginAsync(fixture.Store.SelectedProfile!, reloginCancellation.Token);
        var statusCalls = fixture.Runner.StatusCalls;
        await ThrowsAsync<ClaudeUsageTemporarilyUnavailableException>(() =>
            fixture.Controller.ReadUsageAsync(null, CancellationToken.None, timeout: TimeSpan.FromMilliseconds(50)));
        Check(fixture.Runner.StatusCalls == statusCalls && fixture.SourceCalls == 1,
            "a read behind its own account's sign-in times out without running a CLI command");
        reloginCancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(async () => await relogin);
        loginCancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(async () => await login);
    }

    private static async Task ReadsRunSideBySideUpToThreeAsync()
    {
        using var fixture = new Fixture();
        var others = Enumerable.Range(1, 4)
            .Select(index => fixture.AddAccount("Account " + index, $"account{index}@example.test")).ToArray();
        fixture.SourceHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var ended = new ConcurrentQueue<string>();
        var readStates = new ConcurrentQueue<string>();
        fixture.Controller.ReadStateChanged += (_, e) => readStates.Enqueue(e.ProfileId);
        var batch = fixture.Controller.ReadUsageManyAsync(null, CancellationToken.None,
            outcome => ended.Enqueue(outcome.ProfileId));
        await WaitUntilAsync(() => fixture.InFlight == ClaudeAccountController.ReadConcurrency, "three reads in flight");
        await Task.Delay(100);
        Check(fixture.InFlight == 3 && fixture.SourceCalls == 3 && ended.IsEmpty
              && fixture.Started.First() == fixture.Store.SelectedProfileId,
            "three accounts are read at once, the selected one first, and the others wait for a free slot");
        var everyId = fixture.Store.ListProfiles().Select(profile => profile.Id).ToArray();
        Check(everyId.Count(fixture.Controller.IsReading) == 3 && fixture.Controller.IsReading(fixture.Store.SelectedProfileId!)
              && readStates.Distinct().Count() == 3,
            "only the accounts holding a read slot count as being read; the queued ones wait");
        fixture.SourceHold.SetResult();
        var outcomes = await batch.WaitAsync(TimeSpan.FromSeconds(10));
        Check(!everyId.Any(fixture.Controller.IsReading) && readStates.Count == 10,
            "every account reports when its read starts and ends");
        Check(outcomes.Count == 5 && outcomes.All(outcome => outcome.Failure is null && outcome.Result is { IsStale: false })
              && ended.Count == 5 && ended.Distinct().Count() == 5 && fixture.MaxInFlight == 3 && fixture.SourceCalls == 5,
            "each account is read once, never more than three at a time, and reports as soon as it ends");
        Check(outcomes[0].ProfileId == fixture.Store.SelectedProfileId
              && others.All(profile => fixture.Controller.LastUsage(profile.Id) is { IsStale: false }),
            "each account keeps its own observation");

        // Profiles of one verified account share its interval even when they are read side by side.
        fixture.Clock.Now += TimeSpan.FromMinutes(11);
        var duplicate = fixture.AddAccount("Account 1 again", "account1@example.test");
        Check(fixture.Controller.ReadableProfiles().Count == 5, "a full refresh reads each verified account once");
        var pair = await fixture.Controller.ReadUsageManyAsync([others[0].Id, duplicate.Id], CancellationToken.None);
        Check(pair[0].Result is { IsStale: false } && pair[1].Failure is ClaudeUsageTemporarilyUnavailableException
              && fixture.SourceCalls == 6,
            "two profiles of one account read side by side still make one usage request");

        fixture.Clock.Now += TimeSpan.FromMinutes(11);
        fixture.SourceHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        using var cancellation = new CancellationTokenSource();
        var canceled = fixture.Controller.ReadUsageManyAsync(null, cancellation.Token);
        await WaitUntilAsync(() => fixture.InFlight == ClaudeAccountController.ReadConcurrency, "three reads in flight again");
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => canceled);
        Check(fixture.InFlight == 0 && others.All(profile => fixture.Controller.LastUsage(profile.Id) is not null),
            "canceling a batch ends each read and keeps the accepted observations");
    }

    private static async Task TransientStatusFailureKeepsVerifiedObservationAsync()
    {
        using var fixture = new Fixture();
        var initial = await fixture.Controller.ReadUsageAsync(null, CancellationToken.None);
        fixture.Runner.StatusFailure = new TimeoutException("synthetic auth-status timeout");
        var stale = await fixture.Controller.ReadUsageAsync("default", CancellationToken.None);
        Check(stale.IsStale && stale.LastUpdatedAt == initial.LastUpdatedAt && stale.RetryAfter > fixture.Clock.Now,
            "a transient local status failure returns stale usage with its original observation time");
        Check(fixture.Runner.StatusCalls == 2 && fixture.SourceCalls == 1,
            "cached usage does not bypass a fresh request's identity verification");

        fixture.Clock.Now += TimeSpan.FromHours(2);
        var partlyExpired = await fixture.Controller.ReadUsageAsync(null, CancellationToken.None);
        Check(partlyExpired.Snapshot.FiveHour is null && partlyExpired.Snapshot.Weekly is not null,
            "expired windows are removed independently while unexpired weekly usage survives");
        fixture.Clock.Now = initial.LastUpdatedAt + TimeSpan.FromHours(25);
        await ThrowsAsync<ClaudeUsageTemporarilyUnavailableException>(() =>
            fixture.Controller.ReadUsageAsync(null, CancellationToken.None));
        Check(fixture.Controller.LastUsage("default") is null, "observations older than 24 hours cannot be reused");

        using var future = new Fixture();
        await future.Controller.ReadUsageAsync(null, CancellationToken.None);
        future.Clock.Now -= TimeSpan.FromSeconds(1);
        future.Runner.StatusFailure = new IOException("synthetic command failure");
        await ThrowsAsync<ClaudeUsageTemporarilyUnavailableException>(() =>
            future.Controller.ReadUsageAsync(null, CancellationToken.None));
        Check(future.Controller.LastUsage("default") is null, "future-dated observations are never reused");
    }

    private static async Task RejectedIdentityImmediatelyInvalidatesAsync()
    {
        using var fixture = new Fixture();
        await fixture.Controller.ReadUsageAsync(null, CancellationToken.None);
        var invalidations = 0;
        fixture.Controller.UsageChanged += (_, update) =>
        {
            if (update.ProfileId == "default" && fixture.Controller.LastUsage(update.ProfileId) is null) invalidations++;
        };
        fixture.Runner.Email = "another@example.test";
        await ThrowsAsync<UnauthorizedAccessException>(() =>
            fixture.Controller.ReadUsageAsync("default", CancellationToken.None));
        Check(fixture.Controller.LastUsage("default") is null && invalidations == 1
              && fixture.Cache.For(Fixture.KeyOf("default", Fixture.MainEmail)).Load() is null,
            "a rejected manual query immediately invalidates the selected widget's observation and recovery value");
        Check(fixture.SourceCalls == 1, "a mismatched identity must not request usage");

        using var malformed = new Fixture();
        await malformed.Controller.ReadUsageAsync(null, CancellationToken.None);
        malformed.Runner.MalformedStatus = true;
        await ThrowsAsync<InvalidDataException>(() => malformed.Controller.ReadUsageAsync("default", CancellationToken.None));
        Check(malformed.Controller.LastUsage("default") is null, "an untrusted status schema invalidates the observation");
    }

    private static async Task ChangedBindingCannotReuseObservationAsync()
    {
        using var fixture = new Fixture();
        await fixture.Controller.ReadUsageAsync(null, CancellationToken.None);
        fixture.Store.RegisterDefault("Changed account", Fixture.Identity(fixture.Store.DefaultProfile, "changed@example.test"));
        fixture.Runner.StatusFailure = new TimeoutException("synthetic status timeout after identity changed");
        await ThrowsAsync<ClaudeUsageTemporarilyUnavailableException>(() =>
            fixture.Controller.ReadUsageAsync(null, CancellationToken.None));
        Check(fixture.Controller.LastUsage("default") is null,
            "the same profile id cannot reuse an observation bound to a different registered identity");

        // A legacy default profile can be used before it is registered. If the official status then
        // identifies a different account, invalidate its old observation before any new usage attempt.
        using var unregistered = new Fixture(registerDefault: false);
        await unregistered.Controller.ReadUsageAsync(null, CancellationToken.None);
        var invalidations = 0;
        unregistered.Controller.UsageChanged += (_, update) =>
        { if (unregistered.Controller.LastUsage(update.ProfileId) is null) invalidations++; };
        unregistered.Runner.Email = "changed@example.test";
        unregistered.UsageFailure = new IOException("synthetic usage failure after account changed");
        await ThrowsAsync<ClaudeUsageTemporarilyUnavailableException>(() =>
            unregistered.Controller.ReadUsageAsync(null, CancellationToken.None));
        Check(unregistered.Controller.LastUsage("default") is null && invalidations >= 1 && unregistered.SourceCalls == 2,
            "even an unregistered default never keeps usage from the account it just identified as replaced");
    }

    private static async Task LoginFailureRemainsVisibleUntilRecoveryAsync()
    {
        using var fixture = new Fixture();
        await fixture.Controller.ReadUsageAsync(null, CancellationToken.None);
        Exception? observedFailure = null;
        fixture.Controller.UsageChanged += (_, update) =>
            observedFailure = fixture.Controller.LastFailure(update.ProfileId);
        fixture.Runner.LoggedOut = true;
        await ThrowsAsync<ClaudeLoginRequiredException>(() =>
            fixture.Controller.ReadUsageAsync(null, CancellationToken.None));
        Check(observedFailure is ClaudeLoginRequiredException
              && fixture.Controller.LastFailure("default") is ClaudeLoginRequiredException
              && fixture.Controller.LastFailure("another-profile") is null,
            "a background authentication failure is retained per profile for a manager opened later");
        Check(fixture.Controller.LastUsage("default") is null && fixture.SourceCalls == 1,
            "a logged-out profile cannot publish old data or run usage under an unverified identity");
        Check(UsageIndicatorForm.FriendlyClaudeError(observedFailure!) == UiText.T("Claude Code 로그인이 필요합니다."),
            "the widget reports required CLI login without inventing an expiry cause");

        using (var canceledLogin = new CancellationTokenSource())
        {
            var login = fixture.Controller.LoginAsync(fixture.Store.SelectedProfile!, canceledLogin.Token);
            await fixture.Runner.LoginStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
            canceledLogin.Cancel();
            await ThrowsAsync<OperationCanceledException>(() => login);
            Check(fixture.Controller.LastFailure("default") is ClaudeLoginRequiredException,
                "canceling the official login keeps its recoverable error visible");
        }

        fixture.Runner.LoggedOut = false;
        await fixture.Controller.RegisterDefaultAsync(CancellationToken.None);
        Check(fixture.Controller.LastFailure("default") is null,
            "successful identity verification clears a resolved authentication error before usage is available");
        fixture.Clock.Now += TimeSpan.FromMinutes(11);
        await fixture.Controller.ReadUsageAsync(null, CancellationToken.None);
        Check(fixture.Controller.LastFailure("default") is null && observedFailure is null
              && fixture.Controller.LastUsage("default") is not null,
            "a successful verified observation clears the retained error and notifies open surfaces");

        fixture.Runner.Email = "changed@example.test";
        await ThrowsAsync<ClaudeIdentityChangedException>(() =>
            fixture.Controller.ReadUsageAsync(null, CancellationToken.None));
        Check(fixture.Controller.LastFailure("default") is ClaudeIdentityChangedException,
            "another logged-in identity remains distinct from a missing login");
        fixture.Runner.MalformedStatus = true;
        await ThrowsAsync<InvalidDataException>(() => fixture.Controller.ReadUsageAsync(null, CancellationToken.None));
        Check(fixture.Controller.LastFailure("default") is InvalidDataException,
            "schema failures cannot advertise reauthentication as their remedy");
    }

    private static async Task SavedValuesStayPerAccountAsync()
    {
        using var fixture = new Fixture();
        var main = await fixture.Controller.ReadUsageAsync(null, CancellationToken.None);
        var second = fixture.AddAccount("Second", "second@example.test");
        var other = await fixture.Controller.ReadUsageAsync(second.Id, CancellationToken.None);
        Check(fixture.Controller.SavedUsage("default") is { } saved && saved.ObservedAt == main.LastUpdatedAt
              && saved.Snapshot == main.Snapshot && fixture.Controller.SavedUsage(second.Id)?.ObservedAt == other.LastUpdatedAt,
            "each account keeps its own last value");
        var text = File.ReadAllText(fixture.CachePath);
        Check(!text.Contains("example.test", StringComparison.OrdinalIgnoreCase)
              && !text.Contains(FixtureRunner.Organization, StringComparison.OrdinalIgnoreCase),
            "saved values hold no email or organization");

        var statusCalls = fixture.Runner.StatusCalls;
        var later = fixture.NewController();
        Check(later.SavedUsage("default")?.Snapshot == main.Snapshot && later.SavedUsage(second.Id) is not null
              && later.LastUsage("default") is null && fixture.Runner.StatusCalls == statusCalls && fixture.SourceCalls == 2,
            "a later run shows each account's last value without a query and without taking it as a fresh observation");

        fixture.Clock.Now += TimeSpan.FromHours(25);
        fixture.UsageFailure = new IOException("synthetic offline usage command");
        var nextDay = fixture.NewController();
        await ThrowsAsync<ClaudeUsageTemporarilyUnavailableException>(() =>
            nextDay.ReadUsageAsync(null, CancellationToken.None));
        Check(nextDay.LastUsage("default") is null && nextDay.SavedUsage("default")?.ObservedAt == main.LastUpdatedAt,
            "after 24 hours a last value no longer recovers the widget but stays to show with its check time");
        fixture.Clock.Now = main.LastUpdatedAt + TimeSpan.FromDays(31);
        Check(fixture.NewController().SavedUsage("default") is null && fixture.NewController().SavedUsage(second.Id) is null,
            "a last value older than 30 days is no longer kept");

        using var changed = new Fixture();
        await changed.Controller.ReadUsageAsync(null, CancellationToken.None);
        changed.Store.RegisterDefault("Main", Fixture.Identity(changed.Store.DefaultProfile, "changed@example.test"));
        Check(changed.Controller.SavedUsage("default") is null, "another account's value is never shown for a row");

        using var signedOut = new Fixture();
        var before = await signedOut.Controller.ReadUsageAsync(null, CancellationToken.None);
        signedOut.Runner.LoggedOut = true;
        await ThrowsAsync<ClaudeLoginRequiredException>(() => signedOut.Controller.ReadUsageAsync(null, CancellationToken.None));
        Check(signedOut.Cache.For(Fixture.KeyOf("default", Fixture.MainEmail)).Load() is null
              && signedOut.Controller.SavedUsage("default")?.ObservedAt == before.LastUpdatedAt,
            "a sign-in failure ends recovery from the last value but keeps it to show");
    }

    private static async Task MovingTheDefaultHomeAccountAsync()
    {
        using var fixture = new Fixture();
        var other = fixture.AddAccount("Other", "other@example.test");
        var initial = await fixture.Controller.ReadUsageAsync(null, CancellationToken.None);
        var originalEntries = Directory.GetFileSystemEntries(fixture.Original, "*", SearchOption.AllDirectories).Order().ToArray();
        var selections = 0;
        fixture.Controller.SelectionChanged += (_, _) => selections++;
        fixture.Runner.LoginEmail = Fixture.MainEmail;
        var moved = await fixture.Controller.MoveDefaultHomeAccountAsync(CancellationToken.None);
        var profiles = fixture.Store.ListProfiles();
        Check(profiles.Select(profile => profile.Id).SequenceEqual([moved.Id, other.Id]) && profiles[0].Label == "Main"
              && profiles[0].IsSelected && !profiles[0].IsDefault && !profiles[0].UseDefaultHome
              && fixture.Store.DefaultHomeRetired && selections == 1,
            "the main account keeps its name, place and selection on a profile of its own");
        Check(fixture.Runner.Logins.Single() == moved.ConfigDirectory && fixture.Runner.Logouts.IsEmpty
              && Directory.GetFileSystemEntries(fixture.Original, "*", SearchOption.AllDirectories).Order().SequenceEqual(originalEntries),
            "the official sign-in ran in the new profile and the default home was left as it was");
        Check(fixture.Controller.SavedUsage(moved.Id)?.ObservedAt == initial.LastUpdatedAt
              && fixture.Controller.SavedUsage("default") is null && fixture.Controller.LastUsage("default") is null,
            "the account's saved value moves with it");

        var statusCalls = fixture.Runner.StatusCalls;
        var next = await fixture.Controller.ReadUsageAsync(null, CancellationToken.None);
        Check(next.IsStale && next.Snapshot == initial.Snapshot && next.LastUpdatedAt == initial.LastUpdatedAt
              && fixture.SourceCalls == 1 && fixture.Runner.StatusCalls == statusCalls + 1
              && fixture.Runner.LastStatusDirectory == moved.ConfigDirectory,
            "the moved account is read in its own profile and keeps the account's interval, showing its last value meanwhile");
        fixture.Clock.Now += TimeSpan.FromMinutes(11);
        var fresh = await fixture.Controller.ReadUsageAsync(null, CancellationToken.None);
        Check(!fresh.IsStale && fixture.SourceCalls == 2, "the next interval reads the moved account as usual");

        // With nothing selected the widget no longer falls back to the default home.
        fixture.Store.MarkLoggedOut(moved.Id);
        statusCalls = fixture.Runner.StatusCalls;
        await ThrowsAsync<ClaudeLoginRequiredException>(() => fixture.Controller.ReadUsageAsync(null, CancellationToken.None));
        Check(fixture.Runner.StatusCalls == statusCalls
              && fixture.Controller.ReadableProfiles().All(profile => !profile.IsDefault),
            "after the move nothing runs against the default home");
    }

    private static async Task MovingRejectsAnotherAccountAsync()
    {
        using var fixture = new Fixture();
        var initial = await fixture.Controller.ReadUsageAsync(null, CancellationToken.None);
        var before = fixture.Store.ListProfiles();
        var selections = 0;
        fixture.Controller.SelectionChanged += (_, _) => selections++;
        fixture.Runner.LoginEmail = "someone-else@example.test";
        await ThrowsAsync<ClaudeMoveSignedInAnotherAccountException>(() =>
            fixture.Controller.MoveDefaultHomeAccountAsync(CancellationToken.None));
        var signedIn = fixture.Runner.Logins.Single();
        Check(fixture.Store.ListProfiles().SequenceEqual(before) && !fixture.Store.DefaultHomeRetired && selections == 0
              && fixture.Controller.LastUsage("default") == initial
              && fixture.Cache.For(Fixture.KeyOf("default", Fixture.MainEmail)).Load() is not null,
            "a sign-in to another account changes nothing");
        Check(fixture.Runner.Logouts.SequenceEqual([signedIn]) && !ClaudeProfileStore.PathsEqual(signedIn, fixture.Original),
            "that account's new sign-in is ended again through the official CLI");

        fixture.Runner.LoginEmail = null;
        using var cancellation = new CancellationTokenSource();
        var move = fixture.Controller.MoveDefaultHomeAccountAsync(cancellation.Token);
        await WaitUntilAsync(() => fixture.Runner.Logins.Count == 2, "the second sign-in");
        cancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => move);
        Check(fixture.Store.ListProfiles().SequenceEqual(before) && !fixture.Store.DefaultHomeRetired
              && fixture.Runner.Logouts.Count == 2,
            "a canceled move changes nothing and does not leave its sign-in behind");

        using var unregistered = new Fixture(registerDefault: false);
        await ThrowsAsync<InvalidOperationException>(() =>
            unregistered.Controller.MoveDefaultHomeAccountAsync(CancellationToken.None));
        Check(unregistered.Runner.Logins.IsEmpty, "with no connected default-home account there is nothing to move");
    }

    private static async Task ReadRetiresLeftoverLinksAsync()
    {
        using var fixture = new Fixture();
        var legacy = fixture.AddAccount("Legacy", "legacy@example.test");
        File.WriteAllText(Path.Combine(fixture.Original, "projects", "kept.jsonl"), "synthetic conversation");
        ClaudeProfileTests.CreateJunction(Path.Combine(legacy.ConfigDirectory, "projects"),
            Path.Combine(fixture.Original, "projects"));
        await fixture.Controller.ReadUsageAsync(legacy.Id, CancellationToken.None);
        Check(!Directory.Exists(Path.Combine(legacy.ConfigDirectory, "projects"))
              && File.ReadAllText(Path.Combine(fixture.Original, "projects", "kept.jsonl")) == "synthetic conversation",
            "the first read of a profile made by an earlier build retires its link and keeps the conversations behind it");
    }

    private static async Task DisposalCancelsWaitersWithoutDisposingTheirGateAsync()
    {
        using var fixture = new Fixture();
        await fixture.Controller.ReadUsageAsync(null, CancellationToken.None);
        // A sign-in holds the selected profile's gate and a read of that profile waits behind it.
        var login = fixture.Controller.LoginAsync(fixture.Store.SelectedProfile!, CancellationToken.None);
        await fixture.Runner.LoginStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var waitingForGate = fixture.Controller.ReadUsageAsync(null, CancellationToken.None, timeout: TimeSpan.FromSeconds(5));
        // Three reads of other accounts hold the read slots and a fourth waits for one.
        fixture.SourceHold = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var readers = Enumerable.Range(1, ClaudeAccountController.ReadConcurrency + 1)
            .Select(index => fixture.AddAccount("Reader " + index, $"reader{index}@example.test"))
            .Select(profile => fixture.Controller.ReadUsageAsync(profile.Id, CancellationToken.None)).ToArray();
        await WaitUntilAsync(() => fixture.InFlight == ClaudeAccountController.ReadConcurrency, "three reads in flight");
        fixture.Controller.Dispose();
        await ThrowsAsync<OperationCanceledException>(async () => await login);
        await ThrowsAsync<OperationCanceledException>(async () => await waitingForGate);
        foreach (var reader in readers) await ThrowsAsync<OperationCanceledException>(async () => await reader);
        Check(fixture.SourceCalls == 1 + ClaudeAccountController.ReadConcurrency,
            "the read waiting for a slot never started its usage command");
        await ThrowsAsync<ObjectDisposedException>(() => fixture.Controller.ReadUsageAsync(null, CancellationToken.None));
        fixture.Controller.Dispose();
    }

    private sealed class Fixture : IDisposable
    {
        internal const string MainEmail = "main@example.test";
        internal Exception? UsageFailure;
        // When set, each usage command waits for it, so reads stay in flight.
        internal TaskCompletionSource? SourceHold;
        private readonly string _root;
        private readonly ClaudeUsageRequestThrottle _throttle = new();
        private readonly List<ClaudeAccountController> _controllers = [];
        private int _sourceCalls, _inFlight, _maxInFlight;
        internal readonly FixtureClock Clock = new();
        internal readonly FixtureRunner Runner = new();
        internal readonly ConcurrentQueue<string> Started = new();
        internal string Original { get; }
        internal string CachePath { get; }
        internal ClaudeProfileStore Store { get; }
        internal ClaudeUsageFileCacheStore Cache { get; }
        internal ClaudeAccountController Controller { get; }
        internal int SourceCalls => Volatile.Read(ref _sourceCalls);
        internal int InFlight => Volatile.Read(ref _inFlight);
        internal int MaxInFlight => Volatile.Read(ref _maxInFlight);

        internal Fixture(bool registerDefault = true)
        {
            var testBase = Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT")
                ?? Path.Combine(Path.GetTempPath(), "gfs-agent", "claude-controller-tests");
            _root = Path.Combine(testBase, "controller-" + Guid.NewGuid().ToString("N"));
            Original = Directory.CreateDirectory(Path.Combine(_root, "original")).FullName;
            Directory.CreateDirectory(Path.Combine(Original, "projects"));
            Directory.CreateDirectory(Path.Combine(Original, "skills"));
            Store = new ClaudeProfileStore(Path.Combine(_root, "profiles"), Original, defaultUsesHome: false);
            if (registerDefault) Store.RegisterDefault("Main", Identity(Store.DefaultProfile, MainEmail));
            CachePath = Path.Combine(_root, "usage-cache.json");
            Cache = new ClaudeUsageFileCacheStore(CachePath, Clock);
            Controller = NewController(_throttle);
        }

        // Another controller over the same profiles and saved values: a later run of the widget with its own limiter.
        internal ClaudeAccountController NewController(ClaudeUsageRequestThrottle? throttle = null)
        {
            var limiter = throttle ?? new ClaudeUsageRequestThrottle();
            var cache = new ClaudeUsageFileCacheStore(CachePath, Clock);
            var runtime = new ClaudeProfileRuntime(Store, Runner, "fixture-claude.exe");
            var controller = new ClaudeAccountController(Store, runtime, Clock,
                (profile, key, accountKey) => new ClaudeUsageClient(new FixtureSource(token => ReadAsync(profile.Id, token)),
                    Clock, cache.For(key), key, limiter, accountKey), cache);
            _controllers.Add(controller);
            return controller;
        }

        internal ClaudeProfile AddAccount(string label, string email)
        {
            var profile = Store.CreateProfile(label);
            Runner.EmailByDirectory[profile.ConfigDirectory] = email;
            return Store.RecordVerifiedIdentity(profile.Id, Identity(profile, email));
        }

        private async Task<ClaudeUsageSnapshot> ReadAsync(string profileId, CancellationToken token)
        {
            Interlocked.Increment(ref _sourceCalls);
            Started.Enqueue(profileId);
            var current = Interlocked.Increment(ref _inFlight);
            for (var seen = Volatile.Read(ref _maxInFlight); current > seen; seen = Volatile.Read(ref _maxInFlight))
                Interlocked.CompareExchange(ref _maxInFlight, current, seen);
            try
            {
                if (SourceHold is { } hold) await hold.Task.WaitAsync(token);
                if (UsageFailure is not null) throw UsageFailure;
                return new ClaudeUsageSnapshot(new(10, Clock.Now.AddHours(1)),
                    new(25, Clock.Now.AddDays(7)), new(30, Clock.Now.AddDays(3)));
            }
            finally { Interlocked.Decrement(ref _inFlight); }
        }

        internal static ClaudeProfileIdentity Identity(ClaudeProfile profile, string email) =>
            ClaudeProfileRuntime.ParseStatus(FixtureRunner.Status(profile.ConfigDirectory, email), profile);

        internal static string KeyOf(string profileId, string email) =>
            profileId + ":" + ClaudeProfileRuntime.IdentityKey(email, FixtureRunner.Organization);

        public void Dispose()
        {
            foreach (var controller in _controllers) controller.Dispose();
            ClaudeProfileTests.DeleteFixtureTree(_root);
        }
    }

    private sealed class FixtureClock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixtureSource(Func<CancellationToken, Task<ClaudeUsageSnapshot>> read) : IClaudeUsageSource
    {
        public Task<ClaudeUsageSnapshot> ReadAsync(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return read(cancellationToken); }
    }

    private sealed class FixtureRunner : IClaudeProfileCommandRunner
    {
        internal const string Organization = "ec267d1f-b13f-4186-aee1-5f8090289d84";
        internal readonly TaskCompletionSource<bool> LoginStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly ConcurrentDictionary<string, string> EmailByDirectory = new(StringComparer.OrdinalIgnoreCase);
        internal readonly ConcurrentDictionary<string, bool> SignedOut = new(StringComparer.OrdinalIgnoreCase);
        internal readonly ConcurrentQueue<string> Logins = new();
        internal readonly ConcurrentQueue<string> Logouts = new();
        internal volatile string Email = Fixture.MainEmail;
        // When set, an official sign-in completes at once, signed in to this account; otherwise it waits to be canceled.
        internal volatile string? LoginEmail;
        internal Exception? StatusFailure;
        internal bool MalformedStatus;
        internal bool LoggedOut;
        internal volatile string? LastStatusDirectory;
        private int _statusCalls;
        internal int StatusCalls => Volatile.Read(ref _statusCalls);

        public Task<ClaudeProfileCommandResult> RunStatusAsync(ProcessStartInfo start, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            Interlocked.Increment(ref _statusCalls);
            var directory = start.Environment["CLAUDE_CONFIG_DIR"]!;
            LastStatusDirectory = directory;
            if (StatusFailure is not null) return Task.FromException<ClaudeProfileCommandResult>(StatusFailure);
            return Task.FromResult(MalformedStatus ? new ClaudeProfileCommandResult(0, "{invalid")
                : LoggedOut || SignedOut.ContainsKey(directory)
                    ? new ClaudeProfileCommandResult(1, "{\"loggedIn\":false,\"authMethod\":\"none\"}")
                    : Status(directory, EmailByDirectory.GetValueOrDefault(directory, Email)));
        }

        public async Task<int> RunInteractiveAsync(ProcessStartInfo start, CancellationToken token)
        {
            var directory = start.Environment["CLAUDE_CONFIG_DIR"]!;
            Logins.Enqueue(directory);
            LoginStarted.TrySetResult(true);
            if (LoginEmail is { } email)
            {
                EmailByDirectory[directory] = email;
                SignedOut.TryRemove(directory, out _);
                return 0;
            }
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        }

        public Task<int> RunQuietAsync(ProcessStartInfo start, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var directory = start.Environment["CLAUDE_CONFIG_DIR"]!;
            if (start.ArgumentList.Contains("logout")) { Logouts.Enqueue(directory); SignedOut[directory] = true; }
            return Task.FromResult(0);
        }

        internal static ClaudeProfileCommandResult Status(string directory, string email) => new(0,
            JsonSerializer.Serialize(new { loggedIn = true, authMethod = "claude.ai", apiProvider = "firstParty",
                email, orgId = Organization, configDirectory = directory }));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, string what)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(10);
        while (!condition())
        {
            if (DateTime.UtcNow > deadline) throw new Exception("Timed out waiting for " + what);
            await Task.Delay(10);
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
