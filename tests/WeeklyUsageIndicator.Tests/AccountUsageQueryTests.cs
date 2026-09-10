using System.Reflection;
using WeeklyUsageIndicator;

internal static class AccountUsageQueryTests
{
    internal static async Task RunAsync()
    {
        foreach (var outcome in new[] { "success", "failure", "cancel" }) await RotationAsync(outcome);
        foreach (var point in new[] { "usage-journal-written", "usage-staged", "usage-helper-stopped", "usage-commit-prepared", "usage-vault-committed" })
            await RecoverAsync(point);
        await SaveFailureAsync();
        await IdentityRaceAsync();
        await ExclusiveAsync();
        await UnconfirmedShutdownAsync();
    }

    private static async Task RotationAsync(string outcome)
    {
        using var f = new Fixture();
        var original = File.ReadAllBytes(f.LiveAuth);
        var before = f.Store.ListAccounts().Single(a => a.Id == f.Target.Id);
        try
        {
            await Task.Run(() => f.Store.QueryInactiveUsage(f.Target.Id, async (home, token) =>
            {
                await Task.Delay(15, token).ConfigureAwait(false); // Deliberately resume on another worker.
                Check(home != f.Home, "query must use a separate home");
                File.WriteAllBytes(Path.Combine(home, "auth.json"), Auth("b", "rotated"));
                if (outcome == "failure") throw new IOException("synthetic request failure");
                if (outcome == "cancel") throw new OperationCanceledException();
                return Result();
            }, CancellationToken.None));
            Check(outcome == "success", "failure/cancel must propagate");
        }
        catch (Exception ex) when ((outcome == "failure" && ex is IOException) || (outcome == "cancel" && ex is OperationCanceledException)) { }
        Check(File.ReadAllBytes(f.LiveAuth).SequenceEqual(original), "query leaves active credentials byte-for-byte unchanged");
        var after = f.Store.ListAccounts().Single(a => a.Id == f.Target.Id);
        Check(outcome == "success" ? after.Usage?.UsedPercent == 27 && after.Usage.ShortWindow?.UsedPercent == 13 && after.ObservedAt is not null
            : after.Usage == before.Usage && after.ObservedAt == before.ObservedAt, "only success updates last observation");
        Check(!f.Store.HasPendingUsageQuery && !Directory.Exists(Path.Combine(f.Store.RootPath, "usage-query")), "committed query removes plaintext staging and journal");
        f.Store.SwitchTo(f.Target.Id, () => { });
        Check(File.ReadAllBytes(f.LiveAuth).SequenceEqual(Auth("b", "rotated")), "success/failure/cancel all preserve rotated credentials for next switch");
    }

    private static async Task RecoverAsync(string point)
    {
        using var f = new Fixture();
        var original = File.ReadAllBytes(f.LiveAuth);
        f.Store.Checkpoint = reached => { if (point == reached) throw new IOException("synthetic crash"); };
        await ThrowsAsync(() => Task.Run(() => f.Store.QueryInactiveUsage(f.Target.Id, (home, _) =>
        {
            File.WriteAllBytes(Path.Combine(home, "auth.json"), Auth("b", "rotated"));
            return Task.FromResult(Result());
        }, CancellationToken.None)));
        Check(f.Store.HasPendingUsageQuery, "interruption keeps durable query journal");
        f.Store.Checkpoint = null;
        var reopened = new CodexAccountStore(f.Store.RootPath, f.Home);
        var checks = 0;
        reopened.Recover(() => checks++);
        Check(checks > 0 && !reopened.HasPendingUsageQuery, "recovery requires stopped writers and closes transaction");
        Check(File.ReadAllBytes(f.LiveAuth).SequenceEqual(original), "recovery never writes live auth");
        reopened.SwitchTo(f.Target.Id, () => { });
        Check(File.ReadAllBytes(f.LiveAuth).SequenceEqual(Auth("b", point is "usage-helper-stopped" or "usage-commit-prepared" or "usage-vault-committed" ? "rotated" : "initial")), "crash recovery preserves latest staged credential");
    }

    private static async Task SaveFailureAsync()
    {
        using var f = new Fixture();
        await ThrowsAsync(() => Task.Run(() => f.Store.QueryInactiveUsage(f.Target.Id, (home, _) =>
        {
            File.WriteAllBytes(Path.Combine(home, "auth.json"), Auth("b", "rotated"));
            f.Store.Checkpoint = point => { if (point == "accounts.dpapi-temp-flushed") throw new IOException("synthetic disk failure"); };
            return Task.FromResult(Result());
        }, CancellationToken.None)));
        Check(f.Store.HasPendingUsageQuery && File.Exists(Path.Combine(f.Store.RootPath, "usage-query", "auth.json")), "save failure retains the only rotated copy");
        f.Store.Checkpoint = null;
        f.Store.Recover(() => { });
        f.Store.SwitchTo(f.Target.Id, () => { });
        Check(File.ReadAllBytes(f.LiveAuth).SequenceEqual(Auth("b", "rotated")), "retry recovery saves rotated token");
    }

    private static async Task IdentityRaceAsync()
    {
        foreach (var race in new[] { "stage", "active" })
        {
            using var f = new Fixture();
            await ThrowsAsync(() => Task.Run(() => f.Store.QueryInactiveUsage(f.Target.Id, (home, _) =>
            {
                File.WriteAllBytes(Path.Combine(home, "auth.json"), Auth(race == "stage" ? "wrong" : "b", "rotated"));
                if (race == "active") File.WriteAllBytes(f.LiveAuth, Auth("b", "external-newer"));
                return Task.FromResult(Result());
            }, CancellationToken.None)));
            Check(f.Store.HasPendingUsageQuery, "identity race fails closed and preserves recovery evidence");
            if (race == "active")
            {
                f.Store.Checkpoint = point => { if (point == "usage-vault-committed") throw new IOException("second crash during recovery"); };
                await ThrowsAsync(() => Task.Run(() => f.Store.Recover(() => { })));
                f.Store.Checkpoint = null;
                File.WriteAllBytes(f.LiveAuth, Auth("a", "external-return"));
                f.Store.Recover(() => { });
                Check(File.ReadAllBytes(f.LiveAuth).SequenceEqual(Auth("a", "external-return")), "recovery preserves live auth after another external login");
                f.Store.SwitchTo(f.Target.Id, () => { });
                Check(File.ReadAllBytes(f.LiveAuth).SequenceEqual(Auth("b", "external-newer")), "repeated recovery preserves the earlier committed live credential");
                var source = f.Store.ListAccounts().Single(a => !a.IsActive);
                f.Store.SwitchTo(source.Id, () => { });
                f.Store.SwitchTo(f.Target.Id, () => { });
                Check(File.ReadAllBytes(f.LiveAuth).SequenceEqual(Auth("b", "external-newer")), "live credential, not staged snapshot, survives later switches");
            }
        }
        using var active = new Fixture();
        var current = active.Store.ListAccounts().Single(a => a.IsActive);
        await ThrowsAsync(() => Task.Run(() => active.Store.QueryInactiveUsage(current.Id, (_, _) => throw new Exception("must not launch"), CancellationToken.None)));
        Check(!active.Store.HasPendingUsageQuery, "active target rejected before staging or helper launch");
    }

    private static async Task ExclusiveAsync()
    {
        using var f = new Fixture();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var query = Task.Run(() => f.Store.QueryInactiveUsage(f.Target.Id, async (_, _) =>
        { entered.SetResult(); await release.Task.ConfigureAwait(false); return Result(); }, CancellationToken.None));
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            await ThrowsAsync(() => Task.Run(() => f.Store.SwitchTo(f.Target.Id, () => { })));
            await ThrowsAsync(() => Task.Run(() => f.Store.Remove(f.Target.Id)));
            await ThrowsAsync(() => Task.Run(() => f.Store.Rename(f.Target.Id, "changed")));
            await ThrowsAsync(() => Task.Run(() => f.Store.RegisterCurrent("changed")));
            await ThrowsAsync(() => Task.Run(() => f.Store.QueryInactiveUsage(f.Target.Id, (_, _) => Task.FromResult(Result()), CancellationToken.None)));
            // A live poll can still display its new value without a vault-busy error.
            f.Store.SaveUsage(f.Store.GetCurrentIdentity().Key, Result().Usage);
        }
        finally { release.SetResult(); await query; }
        Check(!f.Store.HasPendingUsageQuery, "mutex is released on its owner thread after async reader completes");
    }

    private static async Task UnconfirmedShutdownAsync()
    {
        using var f = new Fixture();
        await ThrowsAsync(() => Task.Run(() => f.Store.QueryInactiveUsage(f.Target.Id, (home, _) =>
        {
            File.WriteAllBytes(Path.Combine(home, "auth.json"), Auth("b", "rotated"));
            throw new UsageHelperShutdownException();
        }, CancellationToken.None)));
        Check(f.Store.HasPendingUsageQuery && File.Exists(Path.Combine(f.Store.RootPath, "usage-query", "auth.json")), "uncertain writer exit must retain staging");
        await ThrowsAsync(() => Task.Run(() => f.Store.Recover(() => throw new InvalidOperationException("writer still running"))));
        Check(f.Store.HasPendingUsageQuery, "recovery cannot discard staging while a writer may live");
        f.Store.Recover(() => { });
        f.Store.SwitchTo(f.Target.Id, () => { });
        Check(File.ReadAllBytes(f.LiveAuth).SequenceEqual(Auth("b", "rotated")), "recovery after confirmed stop preserves rotated auth");
    }

    internal static CodexAccountUsage Result() => new(new UsageSnapshot(27, DateTimeOffset.UtcNow.AddDays(2), 10080, "codex",
        new UsageWindow(13, DateTimeOffset.UtcNow.AddHours(2), 300)), "b@example.invalid", "pro", true);
    internal static byte[] Auth(string user, string revision) => (byte[])typeof(AccountStoreTests)
        .GetMethod("Auth", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, new object[] { user, "workspace", revision })!;
    private static void Check(bool value, string message) { if (!value) throw new InvalidOperationException("Usage query: " + message); }
    private static async Task ThrowsAsync(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or OperationCanceledException) { return; }
        throw new Exception("Expected usage query rejection");
    }
    private sealed class Fixture : IDisposable
    {
        private readonly string _path = Path.Combine(Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT") ??
            Path.Combine(Path.GetTempPath(), "gfs-agent", "260910_codex-account-usage", "tests"), "query-" + Guid.NewGuid().ToString("N"));
        internal string Home { get; }
        internal string LiveAuth => Path.Combine(Home, "auth.json");
        internal CodexAccountStore Store { get; }
        internal SavedCodexAccount Target { get; }
        internal Fixture()
        {
            Home = Directory.CreateDirectory(Path.Combine(_path, "home")).FullName;
            File.WriteAllBytes(LiveAuth, Auth("a", "initial"));
            Store = new CodexAccountStore(Path.Combine(_path, "vault"), Home);
            Store.RegisterCurrent("Current");
            var imported = Path.Combine(_path, "import.json");
            File.WriteAllBytes(imported, Auth("b", "initial"));
            Target = Store.ImportLoginFile(imported, "Saved");
            File.Delete(imported);
        }
        public void Dispose()
        {
            var path = Path.GetFullPath(_path);
            if (!Path.GetFileName(path).StartsWith("query-", StringComparison.Ordinal)) throw new InvalidOperationException();
            Directory.Delete(path, true);
        }
    }
}
