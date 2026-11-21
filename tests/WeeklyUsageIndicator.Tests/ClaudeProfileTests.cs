using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;
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
            foreach (var name in new[] { "cache", "marketplaces" })
            {
                Directory.CreateDirectory(Path.Combine(plugins, name));
                File.WriteAllText(Path.Combine(plugins, name, "fixture.txt"), "plugin-data");
            }
            File.WriteAllText(Path.Combine(plugins, "installed_plugins.json"), "{\"version\":2,\"plugins\":{}}");
            var originalEntries = Directory.EnumerateFileSystemEntries(original, "*", SearchOption.AllDirectories).Count();
            var store = new ClaudeProfileStore(Path.Combine(root, "metadata"), original, defaultUsesHome: false);
            var identity = Identity(store.DefaultProfile, "first@example.test");
            var first = store.RegisterDefault("Main", identity);
            Check(first.IsSelected && store.SelectedProfileId == "default", "registration selects the first verified profile");
            var second = store.CreateProfile("Other");
            Check(!second.IsVerified && store.SelectedProfileId == first.Id, "an empty profile cannot replace selection");
            MustThrow<InvalidOperationException>(() => store.Select(second.Id, identity));

            var runner = new FakeRunner();
            var runtime = new ClaudeProfileRuntime(store, runner, "fixture-claude.exe");
            Check(runtime.PrepareProfile(second).Count == 0 && !Directory.EnumerateFileSystemEntries(second.ConfigDirectory).Any(),
                "a profile is prepared empty: no link to the original working data, no copied login, app state, settings, instructions or plugins");
            Check(runtime.PrepareProfile(first).Count == 0 && runtime.RetireSharedLinks(first).Count == 0,
                "the default home is never prepared or cleaned");
            CheckJunctionRetirement(store, runtime, original, root);
            CheckDefaultMove(root, original);
            Check(File.ReadAllText(Path.Combine(original, ".credentials.json")) == "synthetic sentinel, never a credential"
                  && Directory.EnumerateFileSystemEntries(original, "*", SearchOption.AllDirectories).Count() == originalEntries,
                "profile setup and link retirement leave the original home untouched");

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

            var ownData = store.CreateProfile("Own data");
            Directory.CreateDirectory(Path.Combine(ownData.ConfigDirectory, "projects"));
            File.WriteAllText(Path.Combine(ownData.ConfigDirectory, "projects", "keep.txt"), "user data");
            Check(runtime.PrepareProfile(ownData).Count == 0
                  && File.ReadAllText(Path.Combine(ownData.ConfigDirectory, "projects", "keep.txt")) == "user data",
                "a real folder in a profile is never linked over, merged or removed");

            store.MarkLoggedOut(second.Id);
            Check(store.SelectedProfileId is null && !store.ListProfiles().Single(profile => profile.Id == second.Id).IsVerified,
                "an official logout can clear verification and selection without deleting a profile");
            store.Remove(second.Id);
            Check(Directory.Exists(second.ConfigDirectory) && File.Exists(Path.Combine(original, "projects", "fixture.txt")),
                "removing a row cannot delete a login folder or the original working data");
            ValidateStatusFailures(second);
        }
        finally { DeleteFixtureTree(root); }
    }

    // Profiles made by earlier builds hold junctions into the original home. Retirement removes those links one by one
    // and nothing else: their targets, real folders, and links of another kind or under another name stay.
    private static void CheckJunctionRetirement(ClaudeProfileStore store, ClaudeProfileRuntime runtime, string original,
        string root)
    {
        var legacy = store.CreateProfile("Legacy");
        var folder = legacy.ConfigDirectory;
        foreach (var name in new[] { "projects", "skills", "file-history" })
            CreateJunction(Path.Combine(folder, name), Path.Combine(original, name));
        Directory.CreateDirectory(Path.Combine(folder, "plugins"));
        File.WriteAllText(Path.Combine(folder, "plugins", "installed_plugins.json"), "{\"version\":2,\"plugins\":{}}");
        CreateJunction(Path.Combine(folder, "plugins", "cache"), Path.Combine(original, "plugins", "cache"));
        var gone = Directory.CreateDirectory(Path.Combine(root, "gone")).FullName;
        CreateJunction(Path.Combine(folder, "scripts"), gone);
        Directory.Delete(gone, recursive: false); // a link whose folder no longer exists
        Directory.CreateDirectory(Path.Combine(folder, "hooks"));
        File.WriteAllText(Path.Combine(folder, "hooks", "own.txt"), "profile data");
        CreateJunction(Path.Combine(folder, "custom"), Path.Combine(original, "agents"));
        // The same reparse tag naming a volume instead of a folder on a drive is a mount point, not a junction.
        CreateMountPoint(Path.Combine(folder, "agents"), @"\??\Volume{00000000-0000-0000-0000-000000000000}\", "");
        // Symbolic links need a privilege or Developer Mode; without either these two checks have nothing to hold.
        var symbolicLinks = new List<string>();
        foreach (var (name, create) in new (string, Action<string>)[]
                 {
                     ("commands", path => Directory.CreateSymbolicLink(path, Path.Combine(original, "commands"))),
                     ("rules", path => File.CreateSymbolicLink(path, Path.Combine(original, "CLAUDE.md")))
                 })
        {
            try { create(Path.Combine(folder, name)); symbolicLinks.Add(name); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }

        var removed = runtime.RetireSharedLinks(legacy);
        string[] retired = ["file-history", Path.Combine("plugins", "cache"), "projects", "scripts", "skills"];
        Check(removed.Order(StringComparer.Ordinal).SequenceEqual(retired.Order(StringComparer.Ordinal)),
            "only junctions under the names earlier builds linked are retired: " + string.Join(", ", removed));
        Check(retired.All(name => !Exists(Path.Combine(folder, name))), "each retired junction is gone as a link");
        Check(new[] { "projects", "skills", "file-history" }.All(name =>
                  File.ReadAllText(Path.Combine(original, name, "fixture.txt")) == "working-data")
              && File.ReadAllText(Path.Combine(original, "plugins", "cache", "fixture.txt")) == "plugin-data",
            "the original folders behind the junctions keep their files");
        Check(File.ReadAllText(Path.Combine(folder, "hooks", "own.txt")) == "profile data"
              && (File.GetAttributes(Path.Combine(folder, "hooks")) & FileAttributes.ReparsePoint) == 0
              && File.Exists(Path.Combine(folder, "plugins", "installed_plugins.json")),
            "real folders and files in the profile stay, even under a retired name");
        Check(ClaudeProfileRuntime.TryReadJunctionTarget(Path.Combine(folder, "custom"), out var custom)
              && ClaudeProfileStore.PathsEqual(custom, Path.Combine(original, "agents")),
            "a junction under another name stays");
        Check((File.GetAttributes(Path.Combine(folder, "agents")) & FileAttributes.ReparsePoint) != 0
              && !ClaudeProfileRuntime.TryReadJunctionTarget(Path.Combine(folder, "agents"), out _),
            "a volume mount point stays, even under a retired name");
        Check(symbolicLinks.All(name => (File.GetAttributes(Path.Combine(folder, name)) & FileAttributes.ReparsePoint) != 0),
            "symbolic links stay, even under a retired name");
        Check(runtime.RetireSharedLinks(legacy).Count == 0, "a second pass finds nothing left to retire");
        CreateJunction(Path.Combine(folder, "projects"), Path.Combine(original, "projects"));
        Check(runtime.PrepareProfile(legacy).SequenceEqual(["projects"])
              && File.ReadAllText(Path.Combine(original, "projects", "fixture.txt")) == "working-data",
            "preparing a profile for sign-in retires a leftover junction the same way");
        store.Remove(legacy.Id);
    }

    // The main account leaves the default home: the row keeps its name, place and selection on a new profile signed in
    // to the same account, and no row uses the default home afterwards.
    private static void CheckDefaultMove(string root, string original)
    {
        var store = new ClaudeProfileStore(Path.Combine(root, "move-metadata"), original, defaultUsesHome: false);
        var main = store.RegisterDefault("Main", Identity(store.DefaultProfile, "main@example.test"));
        var other = store.CreateProfile("Other");
        var target = store.NewDetachedProfile("Ignored");
        Check(!Directory.Exists(target.ConfigDirectory) && store.ListProfiles().Count == 2 && !store.DefaultHomeRetired,
            "a detached profile is neither listed nor created before its sign-in");
        MustThrow<InvalidOperationException>(() => store.AdoptDefault(target, Identity(target, "someone@example.test")));
        Check(store.ListProfiles().Select(profile => profile.Id).SequenceEqual(["default", other.Id]) && !store.DefaultHomeRetired,
            "another account's sign-in moves nothing");
        var moved = store.AdoptDefault(target, main.Identity!);
        var profiles = store.ListProfiles();
        Check(profiles.Select(profile => profile.Id).SequenceEqual([target.Id, other.Id]) && profiles[0].Label == "Main"
              && profiles[0].IsSelected && moved.IsSelected && !profiles[0].IsDefault && !profiles[0].UseDefaultHome
              && ClaudeProfileStore.PathsEqual(profiles[0].ConfigDirectory, target.ConfigDirectory)
              && profiles[0].Identity == main.Identity && store.DefaultHomeRetired,
            "the moved row keeps its name, place and selection on its own profile");
        MustThrow<InvalidOperationException>(() => store.AdoptDefault(store.NewDetachedProfile("Again"), main.Identity!));
        Check(new ClaudeProfileStore(store.RootPath, original, false).DefaultHomeRetired, "the move survives a new store instance");

        var path = Path.Combine(store.RootPath, "profiles.json");
        var metadata = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        Check(metadata["Version"]!.GetValue<int>() == 1, "the list keeps the format earlier builds read");
        metadata.Remove("DefaultHomeRetired");
        File.WriteAllText(path, metadata.ToJsonString());
        Check(!store.DefaultHomeRetired && store.ListProfiles().Count == 2, "a list an earlier build rewrote reads as not moved");
        metadata["DefaultHomeRetired"] = true;
        File.WriteAllText(path, metadata.ToJsonString());
        var again = store.RegisterDefault("Terminal", main.Identity!);
        Check(again.IsDefault && !store.DefaultHomeRetired && store.ListProfiles()[0].Id == "default"
              && store.SelectedProfileId == target.Id,
            "connecting the default home again ends its retirement and keeps the selection");
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

    // The entry itself, not what it points to; a link whose folder is gone still counts.
    private static bool Exists(string path) => (int)new FileInfo(path).Attributes != -1;

    /// <summary>A directory junction as earlier builds made them, for fixtures that hold one.</summary>
    internal static void CreateJunction(string linkPath, string targetPath)
    {
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath));
        CreateMountPoint(linkPath, @"\??\" + target, target);
    }

    // A mount-point reparse point with the given substitute name: a junction, or a volume mount point.
    private static void CreateMountPoint(string linkPath, string substitute, string target)
    {
        var names = Encoding.Unicode.GetBytes(substitute + '\0' + target + '\0');
        var buffer = new byte[16 + names.Length];
        BitConverter.GetBytes(0xA0000003u).CopyTo(buffer, 0);
        BitConverter.GetBytes(checked((ushort)(names.Length + 8))).CopyTo(buffer, 4);
        BitConverter.GetBytes(checked((ushort)(substitute.Length * 2))).CopyTo(buffer, 10);
        BitConverter.GetBytes(checked((ushort)((substitute.Length + 1) * 2))).CopyTo(buffer, 12);
        BitConverter.GetBytes(checked((ushort)(target.Length * 2))).CopyTo(buffer, 14);
        names.CopyTo(buffer, 16);
        Directory.CreateDirectory(linkPath);
        try
        {
            using var handle = CreateFile(linkPath, 0x40000000, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
            if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length, IntPtr.Zero, 0, out _,
                    IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "The fixture junction could not be created.");
        }
        catch { Directory.Delete(linkPath, recursive: false); throw; }
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint controlCode, byte[] input,
        int inputLength, IntPtr output, int outputLength, out int bytesReturned, IntPtr overlapped);

    internal static void DeleteFixtureTree(string root)
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
