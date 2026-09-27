using System.Diagnostics;
using System.Text.Json;
using WeeklyUsageIndicator;

internal static class ClaudeControllerTests
{
    internal static async Task RunAsync()
    {
        await RegisteringSameDefaultKeepsAcceptedUsageAsync();
        await RegisteringChangedDefaultInvalidatesAcceptedUsageAsync();
        await LoginWaitKeepsOtherAccountAsync();
        await TransientStatusFailureKeepsVerifiedObservationAsync();
        await RejectedIdentityImmediatelyInvalidatesAsync();
        await ChangedBindingCannotReuseObservationAsync();
        await DisposalCancelsWaitersWithoutDisposingTheirGateAsync();
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
            Check(cached == initial && fixture.SourceCalls == 1 && fixture.CacheClears == 0 && invalidations == 0,
                "same-identity registration keeps the existing client and recovery cache without another usage call");
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
            var registered = await fixture.Controller.RegisterDefaultAsync(CancellationToken.None);
            Check(registered.Identity?.Key == Fixture.Identity(fixture.Store.DefaultProfile, fixture.Runner.Email).Key
                  && fixture.Controller.LastUsage("default") is null && fixture.CacheClears == 1 && invalidations == 1,
                "a different default identity invalidates previous usage, including an unregistered observation binding");
            Check(fixture.SourceCalls == 1, "registration verifies identity but does not add a usage request");
        }
    }

    private static async Task LoginWaitKeepsOtherAccountAsync()
    {
        using var fixture = new Fixture();
        var initial = await fixture.Controller.ReadUsageAsync(null, CancellationToken.None);
        using var loginCancellation = new CancellationTokenSource();
        var login = fixture.Controller.AddAsync("Pending login", loginCancellation.Token);
        await fixture.Runner.LoginStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var delayed = await fixture.Controller.ReadUsageAsync(null, CancellationToken.None,
            timeout: TimeSpan.FromMilliseconds(50));
        Check(delayed.IsStale && delayed.LastUpdatedAt == initial.LastUpdatedAt
              && delayed.Snapshot.Weekly == initial.Snapshot.Weekly && delayed.RetryAfter > fixture.Clock.Now,
            "another profile's interactive login must keep the selected account's verified usage visible");
        Check(fixture.SourceCalls == 1 && fixture.Runner.StatusCalls == 1,
            "a gate timeout returns an observation and cannot start another CLI command");

        using var manualCancellation = new CancellationTokenSource();
        manualCancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(() => fixture.Controller.ReadUsageAsync(null, manualCancellation.Token));
        Check(fixture.Controller.LastUsage("default")?.Snapshot.Weekly == initial.Snapshot.Weekly,
            "explicit caller cancellation stays cancellation and does not erase accepted usage");
        loginCancellation.Cancel();
        await ThrowsAsync<OperationCanceledException>(async () => await login);
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
        Check(fixture.Controller.LastUsage("default") is null && invalidations == 1 && fixture.CacheClears == 1,
            "a rejected manual query immediately invalidates the selected widget's observation and recovery cache");
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
        await ThrowsAsync<ClaudeUsageTemporarilyUnavailableException>(() =>
            unregistered.Controller.ReadUsageAsync(null, CancellationToken.None));
        Check(unregistered.Controller.LastUsage("default") is null && invalidations >= 1,
            "even an unregistered default never keeps usage from the account it just identified as replaced");
    }

    private static async Task DisposalCancelsWaitersWithoutDisposingTheirGateAsync()
    {
        using var fixture = new Fixture();
        await fixture.Controller.ReadUsageAsync(null, CancellationToken.None);
        var login = fixture.Controller.AddAsync("Pending login", CancellationToken.None);
        await fixture.Runner.LoginStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var query = fixture.Controller.ReadUsageAsync(null, CancellationToken.None, timeout: TimeSpan.FromSeconds(5));
        fixture.Controller.Dispose();
        await ThrowsAsync<OperationCanceledException>(async () => await login);
        await ThrowsAsync<OperationCanceledException>(async () => await query);
        await ThrowsAsync<ObjectDisposedException>(() => fixture.Controller.ReadUsageAsync(null, CancellationToken.None));
        fixture.Controller.Dispose();
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _root;
        private readonly ClaudeUsageRequestThrottle _throttle = new();
        internal readonly FixtureClock Clock = new();
        internal readonly FixtureRunner Runner = new();
        internal ClaudeProfileStore Store { get; }
        internal ClaudeAccountController Controller { get; }
        internal int SourceCalls;
        internal int CacheClears;

        internal Fixture(bool registerDefault = true)
        {
            var testBase = Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT")
                ?? Path.Combine(Path.GetTempPath(), "gfs-agent", "claude-controller-tests");
            _root = Path.Combine(testBase, "controller-" + Guid.NewGuid().ToString("N"));
            var original = Directory.CreateDirectory(Path.Combine(_root, "original")).FullName;
            Directory.CreateDirectory(Path.Combine(original, "projects"));
            Directory.CreateDirectory(Path.Combine(original, "skills"));
            Store = new ClaudeProfileStore(Path.Combine(_root, "profiles"), original, defaultUsesHome: false);
            if (registerDefault) Store.RegisterDefault("Main", Identity(Store.DefaultProfile, Runner.Email));
            var runtime = new ClaudeProfileRuntime(Store, Runner, "fixture-claude.exe");
            Controller = new ClaudeAccountController(Store, runtime, Clock,
                (_, key) => new ClaudeUsageClient(new FixtureSource(() =>
                {
                    SourceCalls++;
                    return new ClaudeUsageSnapshot(new(10, Clock.Now.AddHours(1)),
                        new(25, Clock.Now.AddDays(7)), new(30, Clock.Now.AddDays(3)));
                }), Clock, ClaudeUsageNullCacheStore.Instance, key, _throttle),
                () => CacheClears++);
        }

        internal static ClaudeProfileIdentity Identity(ClaudeProfile profile, string email) =>
            ClaudeProfileRuntime.ParseStatus(FixtureRunner.Status(profile.ConfigDirectory, email), profile);

        public void Dispose()
        {
            Controller.Dispose();
            DeleteFixtureTree(_root);
        }

        private static void DeleteFixtureTree(string root)
        {
            foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos())
            {
                if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) entry.Delete();
                else if (entry is DirectoryInfo directory) DeleteFixtureTree(directory.FullName);
                else entry.Delete();
            }
            Directory.Delete(root, recursive: false);
        }
    }

    private sealed class FixtureClock : TimeProvider
    {
        internal DateTimeOffset Now = new(2026, 9, 28, 0, 0, 0, TimeSpan.Zero);
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private sealed class FixtureSource(Func<ClaudeUsageSnapshot> read) : IClaudeUsageSource
    {
        public Task<ClaudeUsageSnapshot> ReadAsync(CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); return Task.FromResult(read()); }
    }

    private sealed class FixtureRunner : IClaudeProfileCommandRunner
    {
        internal readonly TaskCompletionSource<bool> LoginStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal string Email = "main@example.test";
        internal Exception? StatusFailure;
        internal bool MalformedStatus;
        internal int StatusCalls;

        public Task<ClaudeProfileCommandResult> RunStatusAsync(ProcessStartInfo start, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            StatusCalls++;
            if (StatusFailure is not null) return Task.FromException<ClaudeProfileCommandResult>(StatusFailure);
            return Task.FromResult(MalformedStatus ? new ClaudeProfileCommandResult(0, "{invalid")
                : Status(start.Environment["CLAUDE_CONFIG_DIR"]!, Email));
        }

        public async Task<int> RunInteractiveAsync(ProcessStartInfo start, CancellationToken token)
        {
            LoginStarted.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return 0;
        }

        public Task<int> RunQuietAsync(ProcessStartInfo start, CancellationToken token) => Task.FromResult(0);
        public int Launch(ProcessStartInfo start) => 77;

        internal static ClaudeProfileCommandResult Status(string directory, string email) => new(0,
            JsonSerializer.Serialize(new { loggedIn = true, authMethod = "claude.ai", apiProvider = "firstParty",
                email, orgId = "ec267d1f-b13f-4186-aee1-5f8090289d84", configDirectory = directory }));
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static async Task ThrowsAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
}
