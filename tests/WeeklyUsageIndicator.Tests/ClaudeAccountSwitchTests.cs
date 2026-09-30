using System.Diagnostics;
using System.Text.Json;
using WeeklyUsageIndicator;

internal static class ClaudeAccountSwitchTests
{
    internal static async Task RunAsync()
    {
        var testBase = Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT")
            ?? Path.Combine(Path.GetTempPath(), "gfs-agent", "claude-switch-tests");
        var root = Path.Combine(testBase, "switch-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var original = Directory.CreateDirectory(Path.Combine(root, "original")).FullName;
            var live = Directory.CreateDirectory(Path.Combine(root, "live")).FullName;
            var storage = Path.Combine(root, "desktop");
            var store = new ClaudeProfileStore(Path.Combine(root, "metadata"), original, false);
            var main = store.RegisterDefault("Main", Identity(store.DefaultProfile, "main@example.invalid"));
            var sub = store.CreateProfile("Sub");
            sub = store.RecordVerifiedIdentity(sub.Id, Identity(sub, "sub@example.invalid"));
            var runner = new Runner(main, sub);
            var runtime = new ClaudeProfileRuntime(store, runner, "fixture.exe");
            var host = new Host(main, sub);
            var cli = new ClaudeCliRouting(store, host);
            var desktop = new Desktop(live);
            File.WriteAllText(Path.Combine(live, "fixture-account"), "main@example.invalid");
            var directories = new ClaudeDesktopDirectories(live, storage, desktop.RequireStopped);
            var switching = new ClaudeAccountSwitch(store, runtime, cli, directories, desktop);

            // A fake old UI selection is not proof that either real surface was switched.
            store.Select(sub.Id, sub.Identity!);
            Check(!switching.IsApplied(sub), "a legacy selected row is not an applied account");
            var complete = await switching.SwitchAsync(sub, CancellationToken.None);
            Check(!complete && switching.HasPending && !switching.IsApplied(sub), "first app login is pending, never success");
            Check(host.Route.Value == sub.ConfigDirectory && directories.CurrentProfileId == sub.Id,
                "the two actual surfaces change before asking for first app login");
            Check(File.ReadAllText(Path.Combine(storage, "profiles", main.Id, "fixture-account")) == "main@example.invalid",
                "original app files are preserved by whole-directory movement");
            File.WriteAllText(Path.Combine(live, "fixture-account"), "wrong@example.invalid");
            var beforeWrongConfirmation = desktop.Stops;
            Check(!await switching.ContinueAsync(false, CancellationToken.None) && switching.HasPending,
                "wrong app login cannot commit selection");
            Check(desktop.Stops == beforeWrongConfirmation,
                "confirmation of an already applied route never quits the app to retry a wrong login");
            File.WriteAllText(Path.Combine(live, "fixture-account"), "sub@example.invalid");
            var stops = desktop.Stops;
            switching = new ClaudeAccountSwitch(store, runtime, cli, directories, desktop);
            Check(await switching.ContinueAsync(false, CancellationToken.None) && !switching.HasPending
                && switching.IsApplied(sub) && desktop.Stops == stops,
                "restart resume confirms first login in place without closing the just-logged-in app");
            Check(await switching.SwitchAsync(main, CancellationToken.None) && switching.IsApplied(main)
                && store.SelectedProfileId == main.Id, "real main app and ordinary CLI roundtrip commits main");

            desktop.FailLaunch = true;
            await Throws<IOException>(() => switching.SwitchAsync(sub, CancellationToken.None));
            Check(switching.HasPending && store.SelectedProfileId == main.Id && !switching.IsApplied(main),
                "launch failure keeps a durable partial state and cannot claim old account still active");
            desktop.FailLaunch = false;
            switching = new ClaudeAccountSwitch(store, runtime, cli, directories, desktop);
            Check(await switching.ContinueAsync(true, CancellationToken.None) && switching.IsApplied(main),
                "rollback after restart restores both actual sources");

            desktop.CancelOnStop = true;
            await Throws<OperationCanceledException>(() => switching.SwitchAsync(sub, CancellationToken.None));
            Check(switching.HasPending && host.Route.Value == main.ConfigDirectory,
                "cancelled normal quit is resumable and writes no CLI target");
            desktop.CancelOnStop = false;
            Check(await switching.ContinueAsync(true, CancellationToken.None) && switching.IsApplied(main),
                "rollback also closes a prepared transaction without applying it");

            desktop.FailLaunch = true;
            await Throws<IOException>(() => switching.SwitchAsync(sub, CancellationToken.None));
            desktop.FailLaunch = false;
            var foreign = new ClaudeCliRoute(Path.Combine(root, "foreign"));
            host.Route = foreign;
            await Throws<InvalidOperationException>(() => switching.ContinueAsync(true, CancellationToken.None));
            Check(switching.HasPending && host.Route == foreign, "a foreign CLI route wins over coordinator rollback");
            host.Route = new ClaudeCliRoute(sub.ConfigDirectory);
            // A persisted rollback request must stay rollback even when the next call is Continue.
            Check(await switching.ContinueAsync(false, CancellationToken.None) && switching.IsApplied(main),
                "restart continuation honors a durable rollback decision");

            desktop.FailLaunch = true;
            await Throws<IOException>(() => switching.SwitchAsync(sub, CancellationToken.None));
            desktop.FailLaunch = false;
            var routeBeforeAbandon = host.Route;
            var directoryBeforeAbandon = directories.CurrentProfileId;
            var contentsBeforeAbandon = File.ReadAllText(Path.Combine(live, "fixture-account"));
            switching.Abandon();
            Check(!switching.HasPending && !switching.IsApplied(sub) && !switching.IsApplied(main)
                && host.Route == routeBeforeAbandon && directories.CurrentProfileId == directoryBeforeAbandon
                && File.ReadAllText(Path.Combine(live, "fixture-account")) == contentsBeforeAbandon
                && Directory.GetFiles(store.RootPath, "account-switch-abandoned-*.json").Length == 1,
                "explicit abandonment archives only the non-secret attempt and changes no live account");
            Check(await switching.SwitchAsync(main, CancellationToken.None) && switching.IsApplied(main),
                "a new manual switch is possible after a safely abandoned verification attempt");

            File.WriteAllText(Path.Combine(live, "fixture-account"), "external@example.invalid");
            await Throws<InvalidOperationException>(() => switching.SwitchAsync(sub, CancellationToken.None));
            Check(!switching.HasPending && host.Route.Value == main.ConfigDirectory
                && File.ReadAllText(Path.Combine(live, "fixture-account")) == "external@example.invalid",
                "an external Desktop re-login is detected before recording a false rollback identity");
        }
        finally { Directory.Delete(root, recursive: true); }
    }
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    { try { await action(); } catch (T) { return; } throw new InvalidOperationException("Expected " + typeof(T).Name); }
    private static ClaudeProfileCommandResult Status(ClaudeProfile profile, string email) => new(0,
        JsonSerializer.Serialize(new { loggedIn = true, authMethod = "claude.ai", apiProvider = "firstParty",
            configDirectory = profile.ConfigDirectory, email, orgId = "11111111-1111-1111-1111-111111111111", subscriptionType = "max" }));
    private static ClaudeProfileIdentity Identity(ClaudeProfile profile, string email) => ClaudeProfileRuntime.ParseStatus(Status(profile, email), profile);
    private sealed class Runner(ClaudeProfile main, ClaudeProfile sub) : IClaudeProfileCommandRunner
    {
        public Task<ClaudeProfileCommandResult> RunStatusAsync(ProcessStartInfo start, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var profile = start.Environment["CLAUDE_CONFIG_DIR"] == sub.ConfigDirectory ? sub : main;
            return Task.FromResult(Status(profile, profile.Id == main.Id ? "main@example.invalid" : "sub@example.invalid"));
        }
        public Task<int> RunInteractiveAsync(ProcessStartInfo start, CancellationToken token) => throw new NotSupportedException();
        public Task<int> RunQuietAsync(ProcessStartInfo start, CancellationToken token) => throw new NotSupportedException();
        public int Launch(ProcessStartInfo start) => throw new NotSupportedException();
    }
    private sealed class Host(ClaudeProfile main, ClaudeProfile sub) : IClaudeCliRoutingHost
    {
        public ClaudeCliRoute Route = new(main.ConfigDirectory);
        public ClaudeCliRoute ReadRoute() => Route;
        public void ReplaceRoute(ClaudeCliRoute expected, ClaudeCliRoute replacement)
        { if (Route != expected) throw new InvalidOperationException("foreign"); Route = replacement; }
        public Task<ClaudeProfileCommandResult> ReadOrdinaryStatusAsync(CancellationToken token)
        {
            var profile = Route.Value == sub.ConfigDirectory ? sub : main;
            return Task.FromResult(Status(profile, profile.Id == main.Id ? "main@example.invalid" : "sub@example.invalid"));
        }
    }
    private sealed class Desktop(string live) : IClaudeDesktopRuntime
    {
        public int Stops;
        public bool Running = true, FailLaunch, CancelOnStop;
        public bool IsRunning => Running;
        public Task StopAsync(CancellationToken token)
        { Stops++; if (CancelOnStop) throw new OperationCanceledException(); Running = false; return Task.CompletedTask; }
        public void RequireStopped() { if (Running) throw new InvalidOperationException("App busy"); }
        public void Launch() { if (FailLaunch) throw new IOException("Launch failed"); Running = true; }
        public Task<bool> VerifyAccountAsync(string hash, CancellationToken token) => Task.FromResult(Running
            && File.Exists(Path.Combine(live, "fixture-account"))
            && ClaudeDesktopRuntime.EmailHash(File.ReadAllText(Path.Combine(live, "fixture-account"))) == hash);
    }
}
