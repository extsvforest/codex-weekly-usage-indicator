using System.Text.Json;
using System.Text.Json.Nodes;
using WeeklyUsageIndicator;

internal static class ClaudeCliRoutingTests
{
    internal static async Task RunAsync()
    {
        var testBase = Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT")
            ?? Path.Combine(Path.GetTempPath(), "gfs-agent", "claude-routing-tests");
        var root = Path.Combine(testBase, "route-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var original = Directory.CreateDirectory(Path.Combine(root, "original")).FullName;
            var store = new ClaudeProfileStore(Path.Combine(root, "metadata"), original, defaultUsesHome: false);
            var first = store.RegisterDefault("First", Identity(store.DefaultProfile, "one@example.invalid"));
            var second = store.CreateProfile("Second");
            second = store.RecordVerifiedIdentity(second.Id, Identity(second, "two@example.invalid"));
            var host = new FakeHost(first, second);
            var routing = new ClaudeCliRouting(store, host);
            var settings = Path.Combine(original, "settings.json");
            var sharedSettings = Path.Combine(second.ConfigDirectory, "settings.json");
            File.WriteAllText(settings, "{\"fixture\":1}");
            var sharing = new ClaudeSharedSettings(store);
            sharing.Prepare(second);
            Check(ClaudeSharedSettings.Identify(settings) == ClaudeSharedSettings.Identify(sharedSettings),
                "settings are the same filesystem object without copying their values");
            var replacement = Path.Combine(original, "replacement.json");
            File.WriteAllText(replacement, "{\"fixture\":2}");
            File.Move(replacement, settings, overwrite: true);
            sharing.Prepare(second);
            Check(File.ReadAllText(sharedSettings) == "{\"fixture\":2}"
                && Directory.GetFiles(second.ConfigDirectory, "settings.detached-*.json").Length == 1,
                "an unchanged detached link is preserved before reconnecting the new original");
            File.WriteAllText(replacement, "{\"fixture\":3}");
            File.Move(replacement, sharedSettings, overwrite: true);
            await Throws<ClaudeSettingsConflictException>(() => { sharing.Prepare(second); return Task.CompletedTask; });
            Check(File.ReadAllText(sharedSettings) == "{\"fixture\":3}" && File.ReadAllText(settings) == "{\"fixture\":2}",
                "independent profile edits are never replaced");
            await routing.ApplyAsync(second, CancellationToken.None);
            Check(host.Route.Value == second.ConfigDirectory && host.Reads == 1 && !routing.NeedsRecovery,
                "fresh ordinary status must match before accepting the user route");
            Check(store.SelectedProfileId == first.Id,
                "CLI application alone must not claim that Desktop and the selected account changed");
            await routing.ApplyAsync(first, CancellationToken.None);
            Check(host.Route.Value == first.ConfigDirectory && !routing.NeedsRecovery, "main route restores");

            host.Status = () => Status(first, "one@example.invalid");
            await Throws<InvalidDataException>(() => routing.ApplyAsync(second, CancellationToken.None));
            Check(host.Route.Value == first.ConfigDirectory && !routing.NeedsRecovery,
                "a correct identity from the wrong path rolls back");
            host.Status = () => Status(second, "one@example.invalid");
            await Throws<ClaudeIdentityChangedException>(() => routing.ApplyAsync(second, CancellationToken.None));
            Check(host.Route.Value == first.ConfigDirectory && !routing.NeedsRecovery,
                "wrong account rolls back without changing metadata selection");

            using (var cancelled = new CancellationTokenSource())
            {
                host.Status = () => { cancelled.Cancel(); throw new OperationCanceledException(cancelled.Token); };
                await Throws<OperationCanceledException>(() => routing.ApplyAsync(second, cancelled.Token));
                Check(host.Route.Value == first.ConfigDirectory && !routing.NeedsRecovery,
                    "caller cancellation cannot cancel route cleanup");
            }

            var foreign = new ClaudeCliRoute(Path.Combine(root, "foreign"));
            host.Status = () => { host.Route = foreign; return Status(second, "one@example.invalid"); };
            await Throws<ClaudeCliRecoveryRequiredException>(() => routing.ApplyAsync(second, CancellationToken.None));
            Check(host.Route == foreign && routing.NeedsRecovery, "foreign routing edit survives rollback");
            await Throws<InvalidOperationException>(() => routing.RecoverAsync(CancellationToken.None));
            Check(host.Route == foreign && routing.NeedsRecovery, "recovery does not overwrite a third route");
            host.Route = new ClaudeCliRoute(second.ConfigDirectory);
            await routing.RecoverAsync(CancellationToken.None);
            Check(host.Route.Value == first.ConfigDirectory && !routing.NeedsRecovery,
                "prepared transaction recovery restores only its own route");
            host.Status = null;

            // Simulate a crash after the applied-state commit and before deleting the journal.
            var operation = Guid.NewGuid().ToString("N");
            host.Route = new ClaudeCliRoute(second.ConfigDirectory);
            File.WriteAllText(Path.Combine(store.RootPath, "cli-route.json"), JsonSerializer.Serialize(new
            { OperationId = operation, ProfileId = second.Id, IdentityKey = second.Identity!.Key,
                OriginalRoute = new ClaudeCliRoute(first.ConfigDirectory), AppliedRoute = host.Route }));
            File.WriteAllText(Path.Combine(store.RootPath, "cli-switch.json"), JsonSerializer.Serialize(new
            { OperationId = operation, ProfileId = second.Id, IdentityKey = second.Identity!.Key,
                Before = new ClaudeCliRoute(first.ConfigDirectory), After = host.Route,
                OriginalRoute = new ClaudeCliRoute(first.ConfigDirectory) }));
            await routing.RecoverAsync(CancellationToken.None);
            Check(host.Route.Value == second.ConfigDirectory && !routing.NeedsRecovery,
                "committed recovery checks actual identity instead of reverting a successful route");

            host.Status = () => new(0, "{\"loggedIn\":true}");
            await Throws<InvalidDataException>(() => routing.VerifyAsync(second, CancellationToken.None));
            host.Status = null;

            var metadataPath = Path.Combine(store.RootPath, "profiles.json");
            var metadata = JsonNode.Parse(File.ReadAllText(metadataPath))!.AsObject();
            metadata.Remove("OriginalConfigDirectory");
            metadata.Remove("OriginalUsesHome");
            File.WriteAllText(metadataPath, metadata.ToJsonString());
            var oldEnvironment = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            try
            {
                Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", second.ConfigDirectory);
                var migrated = new ClaudeProfileStore(store.RootPath);
                Check(migrated.DefaultConfigDirectory == original && !migrated.DefaultProfile.UseDefaultHome,
                    "legacy default metadata pins original home even under an already changed user route");
                migrated.Rename(second.Id, "Second renamed");
                Check(File.ReadAllText(metadataPath).Contains("OriginalConfigDirectory"),
                    "next metadata write persists the original location");
            }
            finally { Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", oldEnvironment); }

            var homeStore = new ClaudeProfileStore(Path.Combine(root, "home-metadata"), original, defaultUsesHome: true);
            var home = homeStore.RegisterDefault("Home", Identity(homeStore.DefaultProfile, "home@example.invalid"));
            var other = homeStore.CreateProfile("Other");
            other = homeStore.RecordVerifiedIdentity(other.Id, Identity(other, "other@example.invalid"));
            var homeHost = new FakeHost(home, other) { Route = new ClaudeCliRoute(null) };
            var homeRouting = new ClaudeCliRouting(homeStore, homeHost);
            await homeRouting.ApplyAsync(other, CancellationToken.None);
            await homeRouting.ApplyAsync(home, CancellationToken.None);
            Check(homeHost.Route.Value is null, "default home restores absence, never an empty override");

            var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            homeHost.BeforeStatus = async () => { ready.SetResult(); await proceed.Task; };
            var pending = homeRouting.ApplyAsync(other, CancellationToken.None);
            await ready.Task;
            await Throws<InvalidOperationException>(() => new ClaudeCliRouting(homeStore, homeHost)
                .ApplyAsync(home, CancellationToken.None));
            proceed.SetResult();
            await pending;
            Check(!homeRouting.NeedsRecovery, "cross-instance lock spans the asynchronous verification");
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static ClaudeProfileIdentity Identity(ClaudeProfile profile, string email) =>
        ClaudeProfileRuntime.ParseStatus(Status(profile, email), profile);
    private static ClaudeProfileCommandResult Status(ClaudeProfile profile, string email) => new(0,
        JsonSerializer.Serialize(new { loggedIn = true, authMethod = "claude.ai", apiProvider = "firstParty",
            configDirectory = profile.ConfigDirectory, email, orgId = "11111111-1111-1111-1111-111111111111",
            subscriptionType = "max" }));
    private static void Check(bool condition, string message)
    { if (!condition) throw new InvalidOperationException(message); }
    private static async Task Throws<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new InvalidOperationException("Expected " + typeof(T).Name);
    }
    private sealed class FakeHost(ClaudeProfile first, ClaudeProfile second) : IClaudeCliRoutingHost
    {
        public ClaudeCliRoute Route = new(first.ConfigDirectory);
        public int Reads;
        public Func<ClaudeProfileCommandResult>? Status;
        public Func<Task>? BeforeStatus;
        public ClaudeCliRoute ReadRoute() => Route;
        public void ReplaceRoute(ClaudeCliRoute expected, ClaudeCliRoute replacement)
        {
            if (Route != expected) throw new InvalidOperationException("Foreign route");
            Route = replacement;
        }
        public async Task<ClaudeProfileCommandResult> ReadOrdinaryStatusAsync(CancellationToken token)
        {
            Reads++;
            if (BeforeStatus is not null) await BeforeStatus();
            if (Status is not null) return Status();
            var profile = Route.Value == second.ConfigDirectory ? second : first;
            var email = profile.Identity!.Key == Identity(profile, "one@example.invalid").Key ? "one@example.invalid"
                : profile.Identity.Key == Identity(profile, "two@example.invalid").Key ? "two@example.invalid"
                : profile.IsDefault ? "home@example.invalid" : "other@example.invalid";
            return ClaudeCliRoutingTests.Status(profile, email);
        }
    }
}
