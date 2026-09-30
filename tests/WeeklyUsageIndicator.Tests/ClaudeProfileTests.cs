using System.Diagnostics;
using System.Text.Json;
using WeeklyUsageIndicator;

internal static class ClaudeProfileTests
{
    internal static async Task RunAsync()
    {
        var testBase = Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT")
            ?? Path.Combine(Path.GetTempPath(), "gfs-agent", "claude-profile-tests");
        var root = Path.GetFullPath(Path.Combine(testBase, "profiles-" + Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(root);
        try
        {
            var original = Directory.CreateDirectory(Path.Combine(root, "original")).FullName;
            foreach (var name in new[] { "projects", "skills", "agents", "commands", "file-history", "hooks", "scripts" })
            {
                Directory.CreateDirectory(Path.Combine(original, name));
                File.WriteAllText(Path.Combine(original, name, "fixture.txt"), "working-data");
            }
            File.WriteAllText(Path.Combine(original, "CLAUDE.md"), "Synthetic shared instructions.");
            File.WriteAllText(Path.Combine(original, "history.jsonl"), "synthetic prompt history");
            File.WriteAllText(Path.Combine(original, "settings.json"), "{\"permissions\":{\"defaultMode\":\"manual\"}}");
            // Sentinel names prove setup does not copy login or opaque app-state files.
            File.WriteAllText(Path.Combine(original, ".credentials.json"), "synthetic sentinel, never a credential");
            File.WriteAllText(Path.Combine(original, ".claude.json"), "synthetic app-state sentinel");
            var plugins = Directory.CreateDirectory(Path.Combine(original, "plugins")).FullName;
            foreach (var name in new[] { "cache", "marketplaces", "synced" }) Directory.CreateDirectory(Path.Combine(plugins, name));
            File.WriteAllText(Path.Combine(plugins, "installed_plugins.json"), JsonSerializer.Serialize(new
            {
                version = 2,
                plugins = new Dictionary<string, object[]>
                {
                    ["local@fixture"] = [new { scope = "user", installPath = Path.Combine(plugins, "cache", "local"),
                        version = "1.0.0", privateState = "discard this unknown field" }],
                    ["cloud@fixture"] = [new { scope = "user", installPath = Path.Combine(plugins, "synced", "cloud"), version = "1.0.0" }]
                }
            }));
            File.WriteAllText(Path.Combine(plugins, "known_marketplaces.json"), JsonSerializer.Serialize(new
            {
                fixture = new { source = new { source = "github", repo = "example/fixture", privateState = "discard" },
                    installLocation = Path.Combine(plugins, "marketplaces", "fixture"), lastUpdated = "2026-09-28T00:00:00Z" }
            }));
            var store = new ClaudeProfileStore(Path.Combine(root, "metadata"), original, defaultUsesHome: false);
            var identity = Identity(store.DefaultProfile, "first@example.test");
            var first = store.RegisterDefault("Main", identity);
            Check(first.IsSelected && store.SelectedProfileId == "default", "registration selects the first verified profile");
            var second = store.CreateProfile("Other");
            Check(!second.IsVerified && store.SelectedProfileId == first.Id, "an empty profile cannot replace selection");
            MustThrow<InvalidOperationException>(() => store.Select(second.Id, identity));

            var runner = new FakeRunner();
            var runtime = new ClaudeProfileRuntime(store, runner, "fixture-claude.exe");
            var sharing = runtime.PrepareProfile(second);
            Check(sharing.SharedDirectories.Contains("projects") && sharing.SharedDirectories.Contains("skills")
                  && sharing.SharedDirectories.Contains("file-history") && sharing.SharedDirectories.Contains("hooks"),
                "conversations, local skills, checkpoints and hook files share the original directories");
            Check(!Directory.Exists(Path.Combine(original, "memory")), "setup does not create absent original directories");
            Check(!File.Exists(Path.Combine(second.ConfigDirectory, ".credentials.json"))
                  && !File.Exists(Path.Combine(second.ConfigDirectory, ".claude.json"))
                  && !File.Exists(Path.Combine(second.ConfigDirectory, "settings.json")),
                "profile setup never copies authentication, app state or settings");
            File.WriteAllText(Path.Combine(second.ConfigDirectory, "projects", "new-session.txt"), "synthetic session");
            Check(File.Exists(Path.Combine(original, "projects", "new-session.txt")), "new conversation data has one shared location");
            _ = runtime.PrepareProfile(second);
            var pluginRegistry = Path.Combine(second.ConfigDirectory, "plugins", "installed_plugins.json");
            var pluginCopy = File.ReadAllText(pluginRegistry);
            Check(pluginCopy.Contains("local@fixture") && !pluginCopy.Contains("cloud@fixture")
                  && !pluginCopy.Contains("privateState") && !Directory.Exists(Path.Combine(second.ConfigDirectory, "plugins", "synced")),
                "local plugin metadata is allowlisted and account-synced installations remain independent");
            Check(sharing.SharedDirectories.Contains("plugins/cache") && sharing.SharedDirectories.Contains("plugins/marketplaces"),
                "local marketplace and plugin assets stay available without sharing the whole plugins folder");
            File.WriteAllText(pluginRegistry, "{\"version\":2,\"plugins\":{},\"fixtureUserChange\":true}");
            _ = runtime.PrepareProfile(second);
            Check(File.ReadAllText(pluginRegistry).Contains("fixtureUserChange"),
                "profile plugin changes win over a later setup and are never overwritten");
            Check(File.ReadAllText(Path.Combine(original, ".credentials.json")) == "synthetic sentinel, never a credential",
                "profile setup leaves the original login sentinel untouched");

            var nextIdentity = Identity(second, "second@example.test");
            var verified = store.RecordVerifiedIdentity(second.Id, nextIdentity);
            store.Select(second.Id, nextIdentity);
            store.Rename(second.Id, "Personal");
            Check(new ClaudeProfileStore(store.RootPath, original, false).SelectedProfile?.Label == "Personal",
                "selection and alias survive a new store instance");
            MustThrow<InvalidOperationException>(() => store.Select(second.Id, identity));
            MustThrow<InvalidOperationException>(() => store.RecordVerifiedIdentity(second.Id, identity));
            var metadata = File.ReadAllText(Path.Combine(store.RootPath, "profiles.json"));
            Check(!metadata.Contains("second@example.test") && !metadata.Contains("accessToken")
                  && !metadata.Contains("refreshToken"), "metadata persists only a digest and masked identity");

            runner.Status = Status(second, "second@example.test");
            var verifiedIdentity = await runtime.VerifyAsync(verified, CancellationToken.None);
            Check(verifiedIdentity.Key == nextIdentity.Key, "official status verifies the expected identity");
            var statusStart = runner.LastStatus!;
            Check(statusStart.CreateNoWindow && !statusStart.UseShellExecute
                  && statusStart.ArgumentList.SequenceEqual(new[] { "--settings", Path.Combine(original, "settings.json"),
                      "auth", "status", "--json" }), "status uses the same shared settings and hides its console");
            runner.Status = Status(second, "first@example.test");
            await MustThrowAsync<UnauthorizedAccessException>(() => runtime.VerifyAsync(verified, CancellationToken.None));
            runner.Status = Status(second, "second@example.test", "api_key");
            await MustThrowAsync<UnauthorizedAccessException>(() => runtime.VerifyAsync(verified, CancellationToken.None));

            Check(typeof(ClaudeProfileRuntime).GetMethod("LaunchAsync") is null
                  && typeof(IClaudeProfileCommandRunner).GetMethod("Launch") is null,
                "the utility never starts an interactive Claude session with a profile");
            runner.InteractiveExit = 1;
            await MustThrowAsync<InvalidOperationException>(() => runtime.LoginAsync(second, CancellationToken.None));
            Check(store.SelectedProfileId == second.Id, "failed official login leaves selection unchanged");
            Check(runner.LastInteractive!.ArgumentList.SequenceEqual(new[] { "--safe-mode", "auth", "login", "--claudeai" })
                  && !runner.LastInteractive.RedirectStandardOutput && !runner.LastInteractive.RedirectStandardInput,
                "login codes and URLs stay between the official terminal and the user");

            var defaultHome = store.DefaultProfile with { UseDefaultHome = true };
            var environment = new ProcessStartInfo("fixture.exe");
            environment.Environment.Remove("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC");
            environment.Environment["ANTHROPIC_API_KEY"] = "synthetic sentinel";
            environment.Environment["ANTHROPIC_BASE_URL"] = "https://invalid.test";
            environment.Environment["CLAUDE_CODE_USE_BEDROCK"] = "1";
            environment.Environment["CLAUDE_CODE_OAUTH_TOKEN"] = "synthetic sentinel";
            environment.Environment["CLAUDE_CONFIG_DIR"] = "different-profile";
            ClaudeProfileRuntime.ApplyProfileEnvironment(environment, defaultHome);
            Check(!environment.Environment.Keys.Any(key => key.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)
                  || key.StartsWith("CLAUDE_CODE_OAUTH", StringComparison.OrdinalIgnoreCase)
                  || key.StartsWith("CLAUDE_CODE_USE_", StringComparison.OrdinalIgnoreCase))
                  && !environment.Environment.ContainsKey("CLAUDE_CONFIG_DIR"),
                "default home removes overrides without relocating the default .claude.json");
            Check(!environment.Environment.ContainsKey("CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC")
                  && environment.Environment["DISABLE_TELEMETRY"] == "1"
                  && environment.Environment["DISABLE_ERROR_REPORTING"] == "1"
                  && environment.Environment["DISABLE_AUTOUPDATER"] == "1",
                "usage remains available while telemetry, crash reporting and automatic updates are disabled");
            environment.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] = "1";
            ClaudeProfileRuntime.ApplyProfileEnvironment(environment, second);
            Check(environment.Environment["CLAUDE_CONFIG_DIR"] == second.ConfigDirectory,
                "additional profiles receive only their explicit home");
            Check(environment.Environment["CLAUDE_CODE_DISABLE_NONESSENTIAL_TRAFFIC"] == "1",
                "an inherited user traffic restriction is not silently lifted");

            var unavailable = store.CreateProfile("Conflict");
            Directory.CreateDirectory(Path.Combine(unavailable.ConfigDirectory, "projects"));
            File.WriteAllText(Path.Combine(unavailable.ConfigDirectory, "projects", "keep.txt"), "user data");
            MustThrow<IOException>(() => runtime.PrepareProfile(unavailable));
            Check(File.Exists(Path.Combine(unavailable.ConfigDirectory, "projects", "keep.txt")),
                "an existing unlinked working folder is preserved and setup refuses to overwrite it");

            store.MarkLoggedOut(second.Id);
            Check(store.SelectedProfileId is null && !store.ListProfiles().Single(profile => profile.Id == second.Id).IsVerified,
                "an official logout can clear verification and selection without deleting a profile");
            store.Remove(second.Id);
            Check(Directory.Exists(second.ConfigDirectory) && File.Exists(Path.Combine(original, "projects", "new-session.txt")),
                "removing a row cannot delete a login folder or a junction target");
            ValidateStatusFailures(second);
        }
        finally { DeleteFixtureTree(root); }
    }

    private static void ValidateStatusFailures(ClaudeProfile profile)
    {
        MustThrow<ClaudeLoginRequiredException>(() => ClaudeProfileRuntime.ParseStatus(
            new ClaudeProfileCommandResult(1, "{\"loggedIn\":false,\"authMethod\":\"none\"}"), profile));
        foreach (var invalid in new[] { "{}", "{\"loggedIn\":\"false\"}", "{\"loggedIn\":null}" })
            MustThrow<System.IO.InvalidDataException>(() => ClaudeProfileRuntime.ParseStatus(new(0, invalid), profile));
        MustThrow<System.IO.InvalidDataException>(() => ClaudeProfileRuntime.ParseStatus(new(0, "{broken"), profile));
        MustThrow<System.IO.InvalidDataException>(() => ClaudeProfileRuntime.ParseStatus(
            Status(profile with { ConfigDirectory = Path.Combine(profile.ConfigDirectory, "unexpected") }, "first@example.test"), profile));
        MustThrow<System.IO.InvalidDataException>(() => ClaudeProfileRuntime.ParseStatus(
            new(0, "{\"loggedIn\":true,\"authMethod\":\"claude.ai\",\"email\":\"not-an-email\"}"), profile));
    }

    private static ClaudeProfileIdentity Identity(ClaudeProfile profile, string email) =>
        ClaudeProfileRuntime.ParseStatus(Status(profile, email), profile);
    private static ClaudeProfileCommandResult Status(ClaudeProfile profile, string email, string method = "claude.ai") =>
        new(0, JsonSerializer.Serialize(new { loggedIn = true, authMethod = method, apiProvider = "firstParty", email,
            orgId = "ec267d1f-b13f-4186-aee1-5f8090289d84", subscriptionType = "max", configDirectory = profile.ConfigDirectory }));

    private static void DeleteFixtureTree(string root)
    {
        // Walk only this newly created fixture; remove reparse points themselves, never their targets.
        foreach (var entry in new DirectoryInfo(root).EnumerateFileSystemInfos())
        {
            if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) entry.Delete();
            else if (entry is DirectoryInfo directory) DeleteFixtureTree(directory.FullName);
            else entry.Delete();
        }
        Directory.Delete(root, recursive: false);
    }
    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static void MustThrow<T>(Action action) where T : Exception
    {
        try { action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }
    private static async Task MustThrowAsync<T>(Func<Task> action) where T : Exception
    {
        try { await action(); } catch (T) { return; }
        throw new Exception("Expected " + typeof(T).Name);
    }

    private sealed class FakeRunner : IClaudeProfileCommandRunner
    {
        public ClaudeProfileCommandResult Status { get; set; } = new(1, "{\"loggedIn\":false}");
        public int InteractiveExit { get; set; }
        public ProcessStartInfo? LastStatus { get; private set; }
        public ProcessStartInfo? LastInteractive { get; private set; }
        public Task<ClaudeProfileCommandResult> RunStatusAsync(ProcessStartInfo start, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); LastStatus = start; return Task.FromResult(Status); }
        public Task<int> RunInteractiveAsync(ProcessStartInfo start, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); LastInteractive = start; return Task.FromResult(InteractiveExit); }
        public Task<int> RunQuietAsync(ProcessStartInfo start, CancellationToken cancellationToken) => Task.FromResult(0);
    }
}
