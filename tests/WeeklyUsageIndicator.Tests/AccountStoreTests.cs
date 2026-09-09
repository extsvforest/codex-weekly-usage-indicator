using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WeeklyUsageIndicator;

internal static class AccountStoreTests
{
    public static async Task RunAsync()
    {
        using var fixture = new Fixture();
        TestOpaqueRoundTripAndRotation(fixture);
        TestIdentityAndValidation(fixture);
        TestUsageBindingAndRemoval(fixture);
        TestCrashRecovery(fixture);
        TestUnknownIdentityRecovery(fixture);
        TestPreSwapRace(fixture);
        TestDurabilityFailure(fixture);
        TestCorruptionAndBounds(fixture);
        TestPrivateAcl(fixture);
        TestReparsePaths(fixture);
        await TestConcurrentLockAsync(fixture);
    }

    private static void TestOpaqueRoundTripAndRotation(Fixture fixture)
    {
        var (store, home) = fixture.NewStore();
        Assert(!store.IsEnabled && !Directory.Exists(store.RootPath), "construction and empty list do not opt in");
        Assert(store.ListAccounts().Count == 0 && !Directory.Exists(store.RootPath), "empty listing has no side effects");
        var aOriginal = Auth("user-a", "workspace-a", "original-a");
        WriteAuth(home, aOriginal);
        var a = store.RegisterCurrent("Pro A");
        Assert(store.IsEnabled && a.IsActive, "registration opts in and identifies current account");
        var bBytes = Auth("user-b", "workspace-b", "original-b");
        var b = fixture.Import(store, bBytes, "Pro B");
        var aRotated = Auth("user-a", "workspace-a", "rotated-a");
        WriteAuth(home, aRotated);
        var stopChecks = 0;
        store.SwitchTo(b.Id, () => stopChecks++);
        Assert(stopChecks >= 2, "stopped writers are checked before capture and before swap");
        Equal(ReadAuth(home), bBytes, "target auth preserves every opaque byte");
        store.SwitchTo(a.Id, () => { });
        Equal(ReadAuth(home), aRotated, "switching back uses latest source rotation, not registration snapshot");
        Assert(!store.HasPendingRecovery, "successful round trip closes journal");
        Assert(store.ListAccounts().Count(a2 => a2.IsActive) == 1, "exactly one live identity is active");
        AssertThrows(() => fixture.Import(store, aOriginal, "stale active"), "isolated login cannot overwrite live account tokens");
    }

    private static void TestIdentityAndValidation(Fixture fixture)
    {
        var a = CodexAccountStore.ParseIdentity(Auth("same-user", "workspace-a", "a"));
        var b = CodexAccountStore.ParseIdentity(Auth("same-user", "workspace-b", "b"));
        var c = CodexAccountStore.ParseIdentity(Auth("other-user", "workspace-a", "c"));
        Assert(a.Key != b.Key && a.Key != c.Key, "identity includes both user and workspace");
        Assert(a.Hint.StartsWith("s***") && !a.Hint.Contains("@"), "hint masks identity");
        AssertThrows(() => CodexAccountStore.ParseIdentity(Encoding.UTF8.GetBytes("{}")), "empty auth rejected");
        var external = Encoding.UTF8.GetString(Auth("a", "b", "x")).Replace("\"chatgpt\"", "\"chatgptAuthTokens\"");
        AssertThrows(() => CodexAccountStore.ParseIdentity(Encoding.UTF8.GetBytes(external)), "external token mode rejected");
        var duplicate = Encoding.UTF8.GetString(Auth("a", "b", "x")).Replace("\"auth_mode\":", "\"auth_mode\":\"apikey\",\"auth_mode\":");
        AssertThrows(() => CodexAccountStore.ParseIdentity(Encoding.UTF8.GetBytes(duplicate)), "ambiguous duplicate keys rejected");
        var mismatch = Encoding.UTF8.GetString(Auth("a", "b", "x")).Replace("\"account_id\":\"b\"", "\"account_id\":\"other\"");
        AssertThrows(() => CodexAccountStore.ParseIdentity(Encoding.UTF8.GetBytes(mismatch)), "workspace mismatch rejected");
        foreach (var legacyMode in new[] { "missing", "null" })
        {
            var legacy = JsonNode.Parse(Auth("a", "b", "legacy"))!.AsObject();
            if (legacyMode == "missing") legacy.Remove("auth_mode");
            else legacy["auth_mode"] = null;
            var legacyBytes = JsonSerializer.SerializeToUtf8Bytes(legacy);
            Assert(CodexAccountStore.ParseIdentity(legacyBytes).Key ==
                CodexAccountStore.ParseIdentity(Auth("a", "b", "modern")).Key,
                "official legacy managed ChatGPT mode accepted: " + legacyMode);
            foreach (var conflict in new[] { "OPENAI_API_KEY", "personal_access_token", "agent_identity", "bedrock_api_key", "bedrock_access_keys" })
            {
                var mixed = JsonNode.Parse(legacyBytes)!.AsObject();
                mixed[conflict] = "synthetic-conflicting-credential";
                AssertThrows(() => CodexAccountStore.ParseIdentity(JsonSerializer.SerializeToUtf8Bytes(mixed)),
                    "legacy mode with competing credentials rejected: " + conflict);
            }
        }
        var badMode = JsonNode.Parse(Auth("a", "b", "bad-mode"))!.AsObject();
        badMode["auth_mode"] = 42;
        AssertThrows(() => CodexAccountStore.ParseIdentity(JsonSerializer.SerializeToUtf8Bytes(badMode)), "wrong type mode rejected");
        var (store, home) = fixture.NewStore();
        WriteAuth(home, Auth("a", "b", "x"));
        File.WriteAllText(Path.Combine(home, "config.toml"), "cli_auth_credentials_store = \"keyring\"\n");
        AssertThrows(() => store.RegisterCurrent("A"), "unsupported keyring config rejected without mutation");
        Assert(!store.IsEnabled, "failed capability check leaves feature disabled");
        File.WriteAllText(Path.Combine(home, "config.toml"), "cli_auth_credentials_store = 'file' # explicit\n");
        store.RegisterCurrent("A");
        File.WriteAllText(Path.Combine(home, "config.toml"), "forced_chatgpt_workspace_id = \"locked\"\n");
        AssertThrows(() => store.GetCurrentIdentity(), "workspace policy fails closed");
    }

    private static void TestUsageBindingAndRemoval(Fixture fixture)
    {
        var (store, home) = fixture.NewStore();
        WriteAuth(home, Auth("user-a", "workspace", "a"));
        var a = store.RegisterCurrent("A");
        var keyA = store.GetCurrentIdentity().Key;
        var b = fixture.Import(store, Auth("user-b", "workspace", "b"), "B");
        var usage = new UsageSnapshot(31, DateTimeOffset.UtcNow.AddDays(4), 10080, "codex");
        store.SaveUsage(keyA, usage);
        Assert(store.ListAccounts().Single(e => e.Id == a.Id).Usage == usage, "usage saved against active identity");
        AssertThrows(() => store.Remove(a.Id), "active credential cannot be deleted");
        store.SwitchTo(b.Id, () => { });
        store.SaveUsage(keyA, usage with { UsedPercent = 99 });
        Assert(store.ListAccounts().Single(e => e.Id == a.Id).Usage?.UsedPercent == 31,
            "late A usage response cannot become B or alter inactive observation");
        Assert(store.ListAccounts().Single(e => e.Id == b.Id).Usage is null, "target never inherits old account usage");
        store.Remove(a.Id);
        Assert(store.ListAccounts().Count == 1, "inactive credential can be removed");
    }

    private static void TestCrashRecovery(Fixture fixture)
    {
        foreach (var phase in new[] { "journal-written", "source-saved", "auth.json-temp-flushed", "auth-replaced", "vault-committed" })
        {
            var (store, home) = fixture.NewStore();
            WriteAuth(home, Auth("a", "workspace", "a-original"));
            var a = store.RegisterCurrent("A");
            var b = fixture.Import(store, Auth("b", "workspace", "b-original"), "B");
            store.Checkpoint = point => { if (point == phase) throw new SimulatedCrash(); };
            AssertThrows(() => store.SwitchTo(b.Id, () => { }), "injected transaction crash propagates");
            Assert(store.HasPendingRecovery, "durable journal survives " + phase);
            var landedOnTarget = phase is "auth-replaced" or "vault-committed";
            var refreshed = Auth(landedOnTarget ? "b" : "a", "workspace", "post-crash-rotation");
            WriteAuth(home, refreshed);
            var reopened = new CodexAccountStore(store.RootPath, home);
            // Simulate the only plaintext temp a process-kill could leave. Journal recovery must remove it.
            File.WriteAllBytes(Path.Combine(home, ".gfs-account-auth.tmp"), Auth("b", "workspace", "orphan"));
            reopened.Recover(() => { });
            Equal(ReadAuth(home), refreshed, "recovery never replaces newer live auth at " + phase);
            Assert(!reopened.HasPendingRecovery && !File.Exists(Path.Combine(home, ".gfs-account-auth.tmp")),
                "recovery closes journal and removes auth temp at " + phase);
            reopened.SwitchTo(landedOnTarget ? a.Id : b.Id, () => { });
            reopened.SwitchTo(landedOnTarget ? b.Id : a.Id, () => { });
            Equal(ReadAuth(home), refreshed, "newer rotated credential was saved at " + phase);
        }
    }

    private static void TestUnknownIdentityRecovery(Fixture fixture)
    {
        var (store, home) = fixture.NewStore();
        WriteAuth(home, Auth("a", "workspace", "a"));
        store.RegisterCurrent("A");
        var b = fixture.Import(store, Auth("b", "workspace", "b"), "B");
        store.Checkpoint = point => { if (point == "source-saved") throw new SimulatedCrash(); };
        AssertThrows(() => store.SwitchTo(b.Id, () => { }), "create pending journal");
        store.Checkpoint = null;
        var third = Auth("third", "workspace", "independent-login");
        WriteAuth(home, third);
        AssertThrows(() => store.Recover(() => { }), "third identity fails closed");
        Equal(ReadAuth(home), third, "third identity is untouched");
        Assert(store.HasPendingRecovery, "third identity keeps recoverable journal");
        AssertThrows(() => store.RegisterCurrent("third"), "registration cannot bypass pending recovery");
        File.Delete(Path.Combine(home, "auth.json"));
        AssertThrows(() => store.Recover(() => { }), "missing auth fails closed rather than silently reinstating login");
        Assert(!File.Exists(Path.Combine(home, "auth.json")), "missing auth is not invented");
    }

    private static void TestPreSwapRace(Fixture fixture)
    {
        var (store, home) = fixture.NewStore();
        WriteAuth(home, Auth("a", "workspace", "a"));
        store.RegisterCurrent("A");
        var b = fixture.Import(store, Auth("b", "workspace", "b"), "B");
        var latest = Auth("a", "workspace", "raced-refresh");
        var checks = 0;
        AssertThrows(() => store.SwitchTo(b.Id, () =>
        {
            if (++checks == 2) WriteAuth(home, latest);
        }), "concurrent refresh detected before replacement");
        Equal(ReadAuth(home), latest, "race does not clobber refreshed source");
        store.Recover(() => { });
        Assert(!store.HasPendingRecovery, "race recovers without swapping");
    }

    private static void TestDurabilityFailure(Fixture fixture)
    {
        foreach (var failure in new[] { "switch.dpapi-temp-flushed", "accounts.dpapi-temp-flushed" })
        {
            var (store, home) = fixture.NewStore();
            var original = Auth("a", "workspace", "a");
            WriteAuth(home, original);
            store.RegisterCurrent("A");
            var b = fixture.Import(store, Auth("b", "workspace", "b"), "B");
            store.Checkpoint = point => { if (point == failure) throw new IOException("Synthetic disk failure"); };
            AssertThrows(() => store.SwitchTo(b.Id, () => { }), "durable backup failure must stop auth replacement");
            Equal(ReadAuth(home), original, "backup failure leaves auth byte-for-byte intact");
            store.Checkpoint = null;
            if (store.HasPendingRecovery) store.Recover(() => { });
            Assert(!Directory.EnumerateFiles(store.RootPath, "*.tmp").Any(), "failed writes clean their own temp files");
        }
    }

    private static void TestCorruptionAndBounds(Fixture fixture)
    {
        var (store, home) = fixture.NewStore();
        var auth = Auth("a", "workspace", "a");
        WriteAuth(home, auth);
        store.RegisterCurrent("A");
        var vaultPath = Path.Combine(store.RootPath, "accounts.dpapi");
        var encrypted = File.ReadAllBytes(vaultPath);
        Assert(!Encoding.UTF8.GetString(encrypted).Contains("synthetic-refresh"), "inactive vault never contains plaintext tokens");
        encrypted[^1] ^= 0x55;
        File.WriteAllBytes(vaultPath, encrypted);
        AssertThrows(() => store.ListAccounts(), "DPAPI tampering is rejected");
        Equal(ReadAuth(home), auth, "corrupted vault never alters live auth");
        File.WriteAllBytes(Path.Combine(home, "auth.json"), new byte[1024 * 1024 + 1]);
        AssertThrows(() => store.GetCurrentIdentity(), "oversized auth fails bounded read");
    }

    private static void TestPrivateAcl(Fixture fixture)
    {
        var (store, home) = fixture.NewStore();
        WriteAuth(home, Auth("a", "workspace", "a"));
        store.RegisterCurrent("A");
        var b = fixture.Import(store, Auth("b", "workspace", "b"), "B");
        store.Checkpoint = point =>
        {
            if (point == "auth.json-temp-flushed") AssertPrivateFile(Path.Combine(home, ".gfs-account-auth.tmp"));
        };
        store.SwitchTo(b.Id, () => { });
        AssertPrivateFile(Path.Combine(home, "auth.json"));
        foreach (var file in Directory.EnumerateFiles(store.RootPath)) AssertPrivateFile(file);
        var acl = FileSystemAclExtensions.GetAccessControl(new DirectoryInfo(store.RootPath));
        Assert(acl.AreAccessRulesProtected, "vault directory disables inherited broad ACLs");
        AssertOnlyCurrentSid(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)));
    }

    private static void AssertPrivateFile(string path)
    {
        var acl = FileSystemAclExtensions.GetAccessControl(new FileInfo(path));
        Assert(acl.AreAccessRulesProtected, "credential file disables inherited ACLs");
        AssertOnlyCurrentSid(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)));
    }

    private static void AssertOnlyCurrentSid(AuthorizationRuleCollection rules)
    {
        var sid = WindowsIdentity.GetCurrent().User!.Value;
        Assert(rules.Count > 0 && rules.Cast<FileSystemAccessRule>().All(rule =>
            rule.IdentityReference.Value == sid && rule.AccessControlType == AccessControlType.Allow),
            "only current Windows user is granted file access");
    }

    private static void TestReparsePaths(Fixture fixture)
    {
        var (store, home) = fixture.NewStore();
        WriteAuth(home, Auth("a", "workspace", "a"));
        var linkedHome = Path.Combine(Path.GetDirectoryName(home)!, "linked-home");
        try { Directory.CreateSymbolicLink(linkedHome, home); }
        catch (Exception ex) when (ex is UnauthorizedAccessException || ex is IOException && (ex.HResult & 0xFFFF) == 1314)
        {
            // Junction creation needs no developer-mode privilege. Inputs are this fixture's
            // exact paths and are rejected if they contain any cmd metacharacters.
            Assert(!linkedHome.Concat(home).Any(c => "&|<>^%!\"\r\n".Contains(c)), "junction fixture paths are shell-safe");
            using var process = Process.Start(new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = $"/d /c mklink /J \"{linkedHome}\" \"{home}\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }) ?? throw new InvalidOperationException("Could not create synthetic junction fixture");
            process.StandardOutput.ReadToEnd();
            process.StandardError.ReadToEnd();
            process.WaitForExit();
            Assert(process.ExitCode == 0, "synthetic junction creation succeeds");
        }
        try
        {
            var linked = new CodexAccountStore(store.RootPath, linkedHome);
            AssertThrows(() => linked.RegisterCurrent("A"), "ancestor reparse path rejected");
            Assert(!store.IsEnabled, "reparse rejection creates no vault");
        }
        finally { Directory.Delete(linkedHome); }
    }

    private static async Task TestConcurrentLockAsync(Fixture fixture)
    {
        var (store, home) = fixture.NewStore();
        WriteAuth(home, Auth("a", "workspace", "a"));
        store.RegisterCurrent("A");
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var holder = Task.Run(() =>
        {
            using var mutex = new Mutex(false, CodexAccountStore.TransactionMutexName);
            mutex.WaitOne();
            entered.Set();
            release.Wait();
            mutex.ReleaseMutex();
        });
        entered.Wait();
        try { AssertThrows(() => store.RegisterCurrent("A"), "installer/second-process mutex prevents store mutation"); }
        finally { release.Set(); await holder; }
    }

    private static byte[] Auth(string user, string account, string revision)
    {
        var claims = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["sub"] = "subject-" + user,
            ["email"] = user + "@example.invalid",
            ["https://api.openai.com/auth"] = new Dictionary<string, string>
            {
                ["chatgpt_user_id"] = user,
                ["chatgpt_account_id"] = account
            }
        });
        var token = "synthetic-header." + Convert.ToBase64String(claims).TrimEnd('=').Replace('+', '-').Replace('/', '_') + ".synthetic-signature";
        var data = new
        {
            auth_mode = "chatgpt",
            OPENAI_API_KEY = (string?)null,
            tokens = new { id_token = token, access_token = "synthetic-access-" + revision,
                refresh_token = "synthetic-refresh-" + revision, account_id = account },
            last_refresh = "2026-09-09T01:00:00Z",
            unknown_future_field = new { preserve_me = revision }
        };
        return Encoding.UTF8.GetBytes(" \n" + JsonSerializer.Serialize(data) + "\n  ");
    }

    private static void WriteAuth(string home, byte[] bytes) => File.WriteAllBytes(Path.Combine(home, "auth.json"), bytes);
    private static byte[] ReadAuth(string home) => File.ReadAllBytes(Path.Combine(home, "auth.json"));
    private static void Equal(byte[] actual, byte[] expected, string message) => Assert(actual.SequenceEqual(expected), message);
    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Account store assertion failed: " + message);
    }
    private static void AssertThrows(Action action, string message)
    {
        try { action(); }
        catch (Exception ex) when (ex is InvalidOperationException or IOException or SimulatedCrash) { return; }
        throw new InvalidOperationException("Account store expected rejection: " + message);
    }
    private sealed class SimulatedCrash : Exception;

    private sealed class Fixture : IDisposable
    {
        private readonly string _path;
        public Fixture()
        {
            var parent = Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT") ??
                Path.Combine(Path.GetTempPath(), "gfs-agent", "260909_codex-account-switch", "tests");
            _path = Path.GetFullPath(Path.Combine(parent, "account-store-" + Guid.NewGuid().ToString("N")));
            Directory.CreateDirectory(_path);
        }
        public (CodexAccountStore Store, string Home) NewStore()
        {
            var directory = Path.Combine(_path, Guid.NewGuid().ToString("N"));
            var home = Path.Combine(directory, "synthetic-codex-home");
            Directory.CreateDirectory(home);
            return (new CodexAccountStore(Path.Combine(directory, "vault"), home), home);
        }
        public SavedCodexAccount Import(CodexAccountStore store, byte[] bytes, string label)
        {
            var input = Path.Combine(_path, "synthetic-login-" + Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllBytes(input, bytes);
            try { return store.ImportLoginFile(input, label); }
            finally { File.Delete(input); }
        }
        public void Dispose()
        {
            // Only this fixture's freshly generated, resolved subtree is ever recursively removed.
            if (!Path.GetFileName(_path).StartsWith("account-store-", StringComparison.Ordinal))
                throw new InvalidOperationException("Invalid synthetic fixture root");
            if (Directory.Exists(_path)) Directory.Delete(_path, recursive: true);
        }
    }
}
