using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using WeeklyUsageIndicator;

internal static class WslCodexTests
{
    // Explicit opt-in. Exercises the actual Windows ArgumentList -> wsl.exe -> Python transport
    // and normal helper exit using synthetic /proc observations. It never opens an auth file.
    internal static async Task RunTransportSmokeAsync(string distribution)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var bridge = new WslCodexBridge();
        if (!(await bridge.ListRunningDistributionsAsync(timeout.Token)).Contains(distribution, StringComparer.Ordinal))
            throw new InvalidOperationException("The requested WSL fixture distribution is not already running.");
        foreach (var codexProcess in new[] { false, true })
        {
            var processName = codexProcess ? "codex" : "init";
            var overrideSource = $$"""
import io, types, builtins
def target_for(request):
    return dict(Distribution='Fixture', UserId=1000, UserName='fixture', HomeDirectory='/home/fixture', CodexHome='/home/fixture/.codex', Executable='/usr/bin/codex', Version='codex-cli 0.153.2')
os.listdir = lambda path: ['2147483646']
os.stat = lambda path: types.SimpleNamespace(st_uid=os.getuid())
def fixture_open(path, mode='r', **kwargs):
    if path.endswith('/cmdline'): return io.BytesIO(b'/usr/bin/{{processName}}\x00')
    if path.endswith('/comm'): return io.StringIO('{{processName}}\n')
    raise AssertionError('Fixture must not open any other file')
builtins.open = fixture_open
def inaccessible_exe(path):
    raise PermissionError()
os.readlink = inaccessible_exe
""";
            var helper = WslCodexLinuxHelper.Source.Replace("\r\n", "\n", StringComparison.Ordinal);
            var boundary = helper.IndexOf("\ntry:\n    # If wsl.exe", StringComparison.Ordinal);
            Check(boundary > 0, "the synthetic helper must have an explicit insertion point");
            var start = WslCodexBridge.CreateHelperStartInfo(distribution, null);
            start.ArgumentList[^1] = helper.Insert(boundary, "\n" + overrideSource + "\n");
            var input = Encoding.UTF8.GetBytes("{\"action\":\"probe\",\"distribution\":\"Fixture\",\"target\":null}");
            var run = typeof(WslCodexBridge).GetMethod("RunAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
            var invocation = (Task<byte[]>)run.Invoke(null, [start, input, TimeSpan.FromSeconds(15), 32768, timeout.Token])!;
            var output = await invocation;
            try
            {
                using var result = JsonDocument.Parse(output);
                Check(result.RootElement.GetProperty("ok").GetBoolean(), "synthetic helper must return a valid response");
                Check(result.RootElement.GetProperty("writers").GetInt32() == (codexProcess ? 1 : 0),
                    "inaccessible exe still distinguishes readable Codex and non-Codex comm/cmdline");
            }
            finally { CryptographicOperations.ZeroMemory(output); }
        }
        var busyLoginFixture = """
import io, types, builtins
def target_for(request):
    return dict(Distribution='Fixture', UserId=1000, UserName='fixture', HomeDirectory='/home/fixture', CodexHome='/home/fixture/.codex', Executable='/usr/bin/codex', Version='codex-cli 0.153.2')
writers = lambda: 2
safe_path = lambda path: path
os.makedirs = lambda *args, **kwargs: None
os.stat = lambda path: types.SimpleNamespace(st_uid=os.getuid(), st_mode=0o40700)
tempfile.mkdtemp = lambda **kwargs: '/fixture-stage'
def fixture_open(path, mode='r', **kwargs):
    if path == '/fixture-stage/config.toml': return io.StringIO()
    raise AssertionError('Login fixture must not open live files')
builtins.open = fixture_open
def fixture_read(path):
    if path != '/fixture-stage/auth.json': raise AssertionError('Only the isolated login result may be read')
    return b'fixture-staged-login'
read_private = fixture_read
def fixture_process(args, **kwargs):
    assert args[0:2] == ['/usr/bin/codex', 'login']
    assert kwargs['env']['CODEX_HOME'] == '/fixture-stage'
    assert kwargs['stderr'] == subprocess.PIPE
    read_fd, write_fd = os.pipe()
    os.write(write_fd, b'Untrusted diagnostic text\nhttps://evil.invalid/oauth/authorize?state=discard\nhttps://auth.openai.com/oauth/authorize?state=fixture-only&code_challenge=fixture\n')
    os.close(write_fd)
    return types.SimpleNamespace(stderr=os.fdopen(read_fd, 'rb'), wait=lambda **kwargs: 0)
subprocess.Popen = fixture_process
stop_owned = lambda: None
shutil.rmtree = lambda path: None
""";
        var source = WslCodexLinuxHelper.Source.Replace("\r\n", "\n", StringComparison.Ordinal);
        var insertion = source.IndexOf("\ntry:\n    # If wsl.exe", StringComparison.Ordinal);
        var loginStart = WslCodexBridge.CreateHelperStartInfo(distribution, null);
        loginStart.ArgumentList[^1] = source.Insert(insertion, "\n" + busyLoginFixture + "\n");
        var loginInput = Encoding.UTF8.GetBytes("{\"action\":\"login\",\"distribution\":\"Fixture\",\"target\":null}");
        var progress = new List<Uri>();
        var runner = typeof(WslCodexBridge).GetMethod("RunLoginAsync", System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic)!;
        var loginOutput = await (Task<byte[]>)runner.Invoke(null,
            [loginStart, loginInput, TimeSpan.FromSeconds(15), 32768, timeout.Token, new Action<Uri>(progress.Add)])!;
        try
        {
            using var result = JsonDocument.Parse(loginOutput);
            Check(result.RootElement.GetProperty("ok").GetBoolean() &&
                Encoding.UTF8.GetString(result.RootElement.GetProperty("auth").GetBytesFromBase64()) == "fixture-staged-login",
                "busy Linux helper login must read only the official isolated staging result");
            Check(progress.Count == 1 && WslCodexBridge.IsAuthorizationUrl(progress[0]),
                "official login URL crosses the real private transport and hostile URLs/diagnostics do not");
            Check(!Encoding.UTF8.GetString(loginOutput).Contains("fixture-only", StringComparison.Ordinal),
                "authorization progress is separated from the final authentication payload");
        }
        finally { CryptographicOperations.ZeroMemory(loginOutput); }
        Console.WriteLine("PASS WSL multiline transport, normal shutdown, inaccessible-exe and busy isolated-login fixtures");
    }

    internal static async Task RunAsync()
    {
        await IndependentCredentialsAndFreshSourceAsync();
        await PreparationBlocksBeforeWindowsAsync();
        await CrashAndRotationRecoveryAsync();
        await JointPartialRecoveryAsync();
        await ThirdIdentityAndCasAsync();
        await LoginIdentityAndSecrecyAsync();
        await StagingLoginWhileBusyAsync();
        await OfflinePreparedCancellationAsync();
        await AuthenticationFreeAbandonAsync();
        await OrphanRecoveryNeverAppliesAsync();
        await RotatedSourceSurvivesAbandonAsync();
        await LoginProgressIsPrivateAndBoundedAsync();
        TransportHasNoCredentials();
        ReplySurvivesInputErasure();
    }

    private static async Task LoginProgressIsPrivateAndBoundedAsync()
    {
        const string allowed = "https://auth.openai.com/oauth/authorize?state=fixture-state-do-not-log&code_challenge=fixture";
        var hostile = new[]
        {
            "http://auth.openai.com/oauth/authorize?x=y", "https://auth.openai.com.evil.invalid/oauth/authorize?x=y",
            "https://auth.openai.com@evil.invalid/oauth/authorize?x=y", "https://evil@auth.openai.com/oauth/authorize?x=y",
            "https://auth.openai.com:8443/oauth/authorize?x=y", "https://auth.openai.com/authorize?x=y",
            "https://auth.openai.com/oauth/authorize?x=y#fragment", "https://auth.openai.com/oauth/authorize",
            "file:///oauth/authorize?x=y", "/oauth/authorize?x=y", "https://auth.openai.com/oauth/authorize?x=bad value"
        };
        foreach (var value in hostile)
            Check(!Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out var candidate) || !WslCodexBridge.IsAuthorizationUrl(candidate),
                "only a complete official HTTPS authorization URL is eligible for the browser button");
        var frames = string.Join('\n', hostile.Append(allowed).Select(url => JsonSerializer.Serialize(new { loginUrl = url }))) +
            "\n{\"ok\":true,\"auth\":\"Zml4dHVyZQ==\"}\n";
        using var stream = new FragmentedLoginStream(Encoding.UTF8.GetBytes(frames));
        var received = new List<Uri>();
        var output = await WslCodexBridge.ReadLoginOutputAsync(stream, 32768, url =>
        {
            Check(stream.Position < stream.Length, "authorization progress arrives before the final result");
            received.Add(url);
        }, default);
        try
        {
            Check(received.Count == 1 && received[0].OriginalString == allowed, "hostile progress URLs are silently discarded");
            using var reply = WslCodexBridge.ParseReply(output);
            Check(Encoding.UTF8.GetString(reply.RootElement.GetProperty("auth").GetBytesFromBase64()) == "fixture",
                "the final credential payload is parsed after progress");
            Check(!Encoding.UTF8.GetString(output).Contains("fixture-state", StringComparison.Ordinal), "progress is never part of stored authentication");
        }
        finally { CryptographicOperations.ZeroMemory(output); }

        foreach (var broken in new[]
        {
            JsonSerializer.Serialize(new { loginUrl = allowed, ok = true }),
            "{\"loginUrl\":\"" + allowed,
            "{\"ok\":true,\"auth\":\"Zml4dHVyZQ==\"}\n" + JsonSerializer.Serialize(new { loginUrl = allowed })
        })
        {
            using var malformed = new MemoryStream(Encoding.UTF8.GetBytes(broken));
            try { await WslCodexBridge.ReadLoginOutputAsync(malformed, 32768, null, default); throw new Exception("Malformed login framing must fail"); }
            catch (IOException e) { Check(!e.ToString().Contains("fixture-state", StringComparison.Ordinal), "framing errors do not disclose the URL or state"); }
        }
        using (var callbackFailure = new MemoryStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { loginUrl = allowed }) + "\n")))
        {
            try
            {
                await WslCodexBridge.ReadLoginOutputAsync(callbackFailure, 32768, _ => throw new Exception(allowed), default);
                throw new Exception("Closed login UI must fail safely");
            }
            catch (IOException e) { Check(!e.ToString().Contains("fixture-state", StringComparison.Ordinal), "callback errors do not disclose the URL"); }
        }
        using (var overlong = new MemoryStream(Encoding.UTF8.GetBytes(frames)))
            await Fails(() => WslCodexBridge.ReadLoginOutputAsync(overlong, 32, null, default));
        using (var cancelled = new CancellationTokenSource())
        using (var waiting = new FragmentedLoginStream(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { loginUrl = allowed }) + "\n"), waitAtEnd: true))
        {
            try
            {
                await WslCodexBridge.ReadLoginOutputAsync(waiting, 32768, _ => cancelled.Cancel(), cancelled.Token);
                throw new Exception("Cancelled login must stop reading progress");
            }
            catch (OperationCanceledException) { }
        }
        var error = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { ok = false, code = allowed }));
        try { using var ignored = WslCodexBridge.ParseReply(error); throw new Exception("An untrusted helper error must fail"); }
        catch (WslCodexException e) { Check(e.Code == "helper-failed" && !e.ToString().Contains("fixture-state", StringComparison.Ordinal), "helper errors use a closed safe vocabulary"); }
        finally { CryptographicOperations.ZeroMemory(error); }
    }

    private sealed class FragmentedLoginStream(byte[] bytes, bool waitAtEnd = false) : MemoryStream(bytes, writable: false)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (Position == Length && waitAtEnd) await Task.Delay(Timeout.Infinite, cancellationToken);
            return await base.ReadAsync(buffer[..Math.Min(7, buffer.Length)], cancellationToken);
        }
    }

    private static void ReplySurvivesInputErasure()
    {
        var input = Encoding.UTF8.GetBytes("{\"ok\":true,\"target\":{\"Distribution\":\"Fixture\"},\"writers\":2,\"auth\":\"Zml4dHVyZQ==\"}");
        using var document = WslCodexBridge.ParseReply(input);
        CryptographicOperations.ZeroMemory(input);
        Check(document.RootElement.GetProperty("target").GetProperty("Distribution").GetString() == "Fixture" &&
            document.RootElement.GetProperty("writers").GetInt32() == 2 &&
            Encoding.UTF8.GetString(document.RootElement.GetProperty("auth").GetBytesFromBase64()) == "fixture",
            "the returned document must own its input after the transport clears its temporary response");
    }

    private static async Task IndependentCredentialsAndFreshSourceAsync()
    {
        using var fixture = new Fixture();
        await fixture.BindBothAsync();
        var refreshed = Auth("one", "linux-refreshed");
        fixture.Bridge.Live = refreshed.ToArray();
        var pending = await fixture.Service.PrepareAsync(fixture.Target, fixture.Id2, default);
        await fixture.Service.ApplyAsync(pending, default);
        var result = await fixture.Service.VerifyAndCommitAsync(pending, default);
        Check(result.Verified && fixture.Bridge.VerifyCalls == 1, "a fresh official client must verify the new account");
        Equal(fixture.Bridge.Live, Auth("two", "linux-two"), "target uses the environment's login, not a Windows snapshot");
        pending = await fixture.Service.PrepareAsync(fixture.Target, fixture.Id1, default);
        await fixture.Service.ApplyAsync(pending, default);
        await fixture.Service.VerifyAndCommitAsync(pending, default);
        Equal(fixture.Bridge.Live, refreshed, "a refreshed active source is saved before switching away");
        Check(!fixture.Service.HasPendingRecovery, "a verified operation removes its journal");
    }

    private static async Task PreparationBlocksBeforeWindowsAsync()
    {
        using var fixture = new Fixture();
        await fixture.BindBothAsync();
        var windows = fixture.Id1;
        var writes = 0;
        var coordinator = new WslCodexSwitchCoordinator(fixture.Root, fixture.Service);
        fixture.Bridge.Availability = WslCodexAvailability.MissingCli;
        await Fails(() => coordinator.SwitchAsync(fixture.Id2, [fixture.Target], () => windows,
            id => { writes++; windows = id; }, default));
        Check(writes == 0 && !coordinator.HasPendingRecovery, "missing CLI cannot change Windows or leave a fake success");
        fixture.Bridge.Availability = WslCodexAvailability.Ready;
        await Fails(() => coordinator.SwitchAsync(Guid.NewGuid().ToString("N"), [fixture.Target], () => windows,
            id => { writes++; windows = id; }, default));
        Check(writes == 0 && !coordinator.HasPendingRecovery, "missing selected-target binding blocks before all writes");
        fixture.Bridge.Availability = WslCodexAvailability.Busy;
        await Fails(() => fixture.Service.CaptureCurrentAsync(fixture.Target, fixture.Id1, Key("one"), default));
        Check(fixture.Bridge.ReplaceCalls == 0, "running user jobs are never terminated or overwritten");
    }

    private static async Task CrashAndRotationRecoveryAsync()
    {
        using var fixture = new Fixture();
        await fixture.BindBothAsync();
        var pending = await fixture.Service.PrepareAsync(fixture.Target, fixture.Id2, default);
        fixture.Service.Checkpoint = phase => { if (phase == "wsl-auth-replaced") throw new IOException("synthetic crash"); };
        await Fails(() => fixture.Service.ApplyAsync(pending, default));
        Check(fixture.Service.HasPendingRecovery, "crash after Linux replacement preserves a durable journal");
        var latest = Auth("two", "target-rotated-after-crash");
        fixture.Bridge.Live = latest.ToArray();
        fixture.Service.Checkpoint = null;
        var recovered = await fixture.Service.RecoverAsync(fixture.Target, default);
        Check(recovered.Verified, "recovery verifies the intended account");
        Equal(fixture.Bridge.Live, latest, "recovery cannot roll back a rotated live token");
        Check(fixture.Bridge.ReplaceCalls == 1, "recovery on the target performs no replacement");
    }

    private static async Task JointPartialRecoveryAsync()
    {
        using var fixture = new Fixture();
        await fixture.BindBothAsync();
        var windows = fixture.Id1;
        var writes = 0;
        var coordinator = new WslCodexSwitchCoordinator(fixture.Root, fixture.Service);
        fixture.Bridge.Verify = false;
        var result = await coordinator.SwitchAsync(fixture.Id2, [fixture.Target], () => windows,
            id => { writes++; windows = id; }, default);
        Check(!result.Completed && result.WindowsCompleted && coordinator.HasPendingRecovery, "partial switch is visible and resumable");
        fixture.Bridge.Verify = true;
        var recovered = await coordinator.RecoverAsync(() => windows, id => { writes++; windows = id; }, default);
        Check(recovered.Completed && recovered.Targets.Single().Verified && writes == 1, "recovery does not replay completed Windows writes");
        Check(!coordinator.HasPendingRecovery && !fixture.Service.HasPendingRecovery, "all verified journals are closed");

        // A failure after a prepared target has committed but before the coordinator records it.
        fixture.Bridge.Live = Auth("one", "one-next");
        windows = fixture.Id1;
        coordinator.Checkpoint = phase => { if (phase == "joint-wsl-verified") throw new IOException("synthetic coordinator crash"); };
        result = await coordinator.SwitchAsync(fixture.Id2, [fixture.Target], () => windows, id => windows = id, default);
        Check(!result.Completed && coordinator.HasPendingRecovery && !fixture.Service.HasPendingRecovery, "coordinator survives a completed child journal");
        coordinator.Checkpoint = null;
        recovered = await coordinator.RecoverAsync(() => windows, id => windows = id, default);
        Check(recovered.Completed, "already-committed WSL state is reverified instead of overwritten");
    }

    private static async Task ThirdIdentityAndCasAsync()
    {
        using var fixture = new Fixture();
        await fixture.BindBothAsync();
        var pending = await fixture.Service.PrepareAsync(fixture.Target, fixture.Id2, default);
        var newer = Auth("one", "source-concurrent-refresh");
        fixture.Bridge.Live = newer.ToArray();
        await Fails(() => fixture.Service.ApplyAsync(pending, default));
        Equal(fixture.Bridge.Live, newer, "compare-and-swap refuses a changed source snapshot");
        var third = Auth("three", "independent-login");
        fixture.Bridge.Live = third.ToArray();
        await Fails(() => fixture.Service.RecoverAsync(fixture.Target, default));
        Equal(fixture.Bridge.Live, third, "recovery never overwrites a third account");
        fixture.Bridge.Live = newer.ToArray();
        await fixture.Service.RecoverAsync(fixture.Target, default);
        var reverse = await fixture.Service.PrepareAsync(fixture.Target, fixture.Id1, default);
        await fixture.Service.ApplyAsync(reverse, default);
        await fixture.Service.VerifyAndCommitAsync(reverse, default);
        Equal(fixture.Bridge.Live, newer, "explicit recovery first saves the source's newer refresh token");
    }

    private static async Task LoginIdentityAndSecrecyAsync()
    {
        using var fixture = new Fixture();
        await fixture.Service.CaptureCurrentAsync(fixture.Target, fixture.Id1, Key("one"), default);
        fixture.Bridge.LoginAuth = Auth("two", "new-official-login");
        await Fails(() => fixture.Service.LoginAndBindAsync(fixture.Target, fixture.Id2, Key("one"), default));
        Check(fixture.Service.GetBindings(fixture.Target).Count == 1, "wrong browser account cannot attach to a selected account");
        await fixture.Service.LoginAndBindAsync(fixture.Target, fixture.Id2, Key("two"), default);
        Equal(fixture.Bridge.Live, Auth("one", "linux-one"), "isolated official login preserves the active WSL login");
        foreach (var path in Directory.EnumerateFiles(fixture.Root, "*.dpapi", SearchOption.AllDirectories))
            Check(!Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("synthetic-refresh", StringComparison.Ordinal), "vault has no plaintext refresh token");
        Check(fixture.Service.GetConnectedTargets().Single().Key == fixture.Target.Key, "environment binding survives reload");
        var otherUid = fixture.Target with { UserId = 2000 };
        Check(otherUid.Key != fixture.Target.Key, "distribution alone is insufficient to identify a credential store");
    }

    private static async Task StagingLoginWhileBusyAsync()
    {
        using var fixture = new Fixture();
        fixture.Bridge.Availability = WslCodexAvailability.Busy;
        fixture.Bridge.LoginAuth = Auth("two", "isolated-busy-login");
        var live = fixture.Bridge.Live.ToArray();
        Uri? loginUrl = null;
        await fixture.Service.LoginAndBindAsync(fixture.Target, fixture.Id2, Key("two"), default, url => loginUrl = url);
        Check(loginUrl is not null && WslCodexBridge.IsAuthorizationUrl(loginUrl), "isolated login forwards temporary browser progress to its caller");
        Check(fixture.Service.GetBindings(fixture.Target).Single().AccountId == fixture.Id2,
            "supported busy environments can bind an isolated official login");
        Check(fixture.Bridge.LoginCalls == 1 && fixture.Bridge.ReadCalls == 0 && fixture.Bridge.ReplaceCalls == 0,
            "staging login never reads or replaces live authentication");
        Equal(fixture.Bridge.Live, live, "existing jobs keep their authentication during staging login");
        await Fails(() => fixture.Service.CaptureCurrentAsync(fixture.Target, fixture.Id1, Key("one"), default));
        fixture.Bridge.Availability = WslCodexAvailability.MissingCli;
        await Fails(() => fixture.Service.LoginAndBindAsync(fixture.Target, fixture.Id2, Key("two"), default));
        Check(fixture.Bridge.LoginCalls == 1, "an unsupported or missing CLI cannot start official login");
    }

    private static async Task OfflinePreparedCancellationAsync()
    {
        using var fixture = new Fixture();
        await fixture.BindBothAsync();
        var handle = await fixture.Service.PrepareAsync(fixture.Target, fixture.Id2, default);
        fixture.Bridge.ThrowOnAnyCall = true;
        await fixture.Service.AbandonPreparedAsync(handle, default);
        await fixture.Service.AbandonPreparedAsync(handle, default);
        Check(!fixture.Service.HasPendingRecovery && fixture.Bridge.ReplaceCalls == 0,
            "known-not-applied preparation closes offline and repeated cancellation is harmless");
        Check(Directory.GetFiles(Path.Combine(fixture.Root, "wsl"), "archived-switch-*.dpapi").Length == 1,
            "preparation keeps an encrypted archival record");
        using var interrupted = new Fixture();
        await interrupted.BindBothAsync();
        var coordinator = new WslCodexSwitchCoordinator(interrupted.Root, interrupted.Service);
        var writes = 0;
        interrupted.Service.Checkpoint = phase =>
        { if (phase == "wsl-prepared") interrupted.Bridge.Availability = WslCodexAvailability.Busy; };
        await Fails(() => coordinator.SwitchAsync(interrupted.Id2, [interrupted.Target], () => interrupted.Id1, _ => writes++, default));
        Check(writes == 0 && !coordinator.HasPendingRecovery && !interrupted.Service.HasPendingRecovery,
            "a daemon starting during preparation cancels offline without leaving a global recovery blocker");
    }

    private static async Task AuthenticationFreeAbandonAsync()
    {
        foreach (var crash in new[] { "none", "joint-abandon-recorded", "joint-abandon-target-archived", "joint-abandon-journal-archived" })
        {
            using var fixture = new Fixture();
            await fixture.BindBothAsync();
            var windows = fixture.Id1;
            var coordinator = new WslCodexSwitchCoordinator(fixture.Root, fixture.Service);
            fixture.Bridge.Verify = false;
            var partial = await coordinator.SwitchAsync(fixture.Id2, [fixture.Target], () => windows, id => windows = id, default);
            Check(!partial.Completed && partial.WindowsCompleted, "fixture must first create a partial joint operation");
            fixture.Bridge.Live = Auth("three", "separate-login-after-failure");
            var third = fixture.Bridge.Live.ToArray();
            windows = Guid.NewGuid().ToString("N");
            var independentWindows = windows;
            fixture.Bridge.ThrowOnAnyCall = true; // Covers removed distro/CLI as well as a third login.
            var targets = coordinator.GetPendingTargets();
            Check(targets.Single().Key == fixture.Target.Key, "offline metadata identifies what the UI must disable");
            await Fails(() => coordinator.AbandonRecoveryAsync([], default));
            coordinator.Checkpoint = phase => { if (phase == crash) throw new IOException("synthetic archival interruption"); };
            if (crash == "none")
            {
                var abandoned = await coordinator.AbandonRecoveryAsync([fixture.Target.Key], default);
                Check(abandoned.ArchivedJournalCount == 2, "both DPAPI journals are archived");
            }
            else
            {
                await Fails(() => coordinator.AbandonRecoveryAsync([fixture.Target.Key], default));
                Check(coordinator.HasPendingRecovery, "interrupted abandonment preserves a durable marker");
                coordinator.Checkpoint = null;
                var recovered = await coordinator.RecoverAsync(
                    () => throw new Exception("abandon must not inspect Windows auth"),
                    _ => throw new Exception("abandon must not write Windows auth"), default);
                Check(recovered.Completed && recovered.ReasonCode == "link-disconnected", "restart only finishes archiving");
            }
            Check(!coordinator.HasPendingRecovery && !fixture.Service.HasPendingRecovery,
                "disconnection closes all blockers without a working distro");
            Equal(fixture.Bridge.Live, third, "disconnection leaves independent Linux login byte-for-byte intact");
            Check(windows == independentWindows, "disconnection leaves independent Windows login untouched");
            var again = await coordinator.AbandonRecoveryAsync([fixture.Target.Key], default);
            Check(again.ArchivedJournalCount == 0 && !await fixture.Service.AbandonRecoveryAsync(fixture.Target, default),
                "repeated disconnection is idempotent");
            var archives = Directory.GetFiles(fixture.Root, "archived-*.dpapi", SearchOption.AllDirectories);
            Check(archives.Length == 2 && archives.All(path => !Encoding.UTF8.GetString(File.ReadAllBytes(path)).Contains("synthetic-refresh", StringComparison.Ordinal)),
                "archives contain ciphertext only, including across crash boundaries");
        }
    }

    private static async Task OrphanRecoveryNeverAppliesAsync()
    {
        using (var fixture = new Fixture())
        {
            await fixture.BindBothAsync();
            await fixture.Service.PrepareAsync(fixture.Target, fixture.Id2, default);
            fixture.Bridge.ThrowOnAnyCall = true;
            var result = await fixture.Service.RecoverOrphanAsync(fixture.Target, default);
            Check(!result.Verified && result.ReasonCode == "preparation-cancelled" && fixture.Bridge.ReplaceCalls == 0,
                "unlinked preparations cancel without inspecting or applying Linux auth");
        }
        foreach (var crash in new[] { "wsl-apply-started", "wsl-auth-replaced" })
        {
            using var fixture = new Fixture();
            await fixture.BindBothAsync();
            var handle = await fixture.Service.PrepareAsync(fixture.Target, fixture.Id2, default);
            fixture.Service.Checkpoint = phase => { if (phase == crash) throw new IOException("synthetic uncertain apply"); };
            await Fails(() => fixture.Service.ApplyAsync(handle, default));
            var replacements = fixture.Bridge.ReplaceCalls;
            var identity = CodexAccountStore.ParseIdentity(fixture.Bridge.Live).Key;
            var live = Auth(identity == Key("one") ? "one" : "two", "live-rotation-after-orphan");
            fixture.Bridge.Live = live.ToArray();
            fixture.Service.Checkpoint = null;
            var result = await fixture.Service.RecoverOrphanAsync(fixture.Target, default);
            Check(fixture.Bridge.ReplaceCalls == replacements && !fixture.Service.HasPendingRecovery,
                "uncertain/applied orphans reconcile without replaying replacement");
            Equal(fixture.Bridge.Live, live, "orphan reconciliation preserves the latest active rotation");
            Check(result.ReasonCode == (identity == Key("one") ? "source-preserved" : "fresh-cli-verified"),
                "orphan result describes the account actually retained");
            if (identity == Key("one")) Equal(ReadStoredAuth(fixture, fixture.Id1), live,
                "archiving an orphan cannot downgrade its freshly reconciled source vault entry");
        }
        using var legacy = new Fixture();
        await legacy.BindBothAsync();
        await legacy.Service.PrepareAsync(legacy.Target, legacy.Id2, default);
        var files = new WslCodexProtectedFiles(Path.Combine(legacy.Root, "wsl"));
        using (files.Acquire())
        {
            var name = files.Names("switch-*.dpapi").Single();
            using var oldJournal = files.Read<WslCodexService.Journal>(name);
            oldJournal.Version = 1;
            files.Write(name, oldJournal);
        }
        var reads = legacy.Bridge.ReadCalls;
        var legacyResult = await legacy.Service.RecoverOrphanAsync(legacy.Target, default);
        Check(legacyResult.ReasonCode == "source-preserved" && legacy.Bridge.ReadCalls > reads && legacy.Bridge.ReplaceCalls == 0,
            "legacy Applied=false is treated as uncertain, never as proof that no replacement was attempted");
    }

    private static async Task RotatedSourceSurvivesAbandonAsync()
    {
        foreach (var crashBeforeVaultSave in new[] { false, true })
        {
            using var fixture = new Fixture();
            await fixture.BindBothAsync();
            await fixture.Service.PrepareAsync(fixture.Target, fixture.Id2, default);
            var rotated = Auth("one", "rotated-while-joint-recovery-pending");
            fixture.Bridge.Live = rotated.ToArray();
            fixture.Bridge.Verify = false;
            if (crashBeforeVaultSave)
                fixture.Service.Checkpoint = phase => { if (phase == "wsl-recovery-source-observed") throw new IOException("synthetic journal/vault gap"); };
            await Fails(() => fixture.Service.RecoverAsync(fixture.Target, default));
            Check(fixture.Bridge.ReplaceCalls == (crashBeforeVaultSave ? 0 : 1), "fixture reaches the intended recovery boundary");
            fixture.Service.Checkpoint = null;
            fixture.Bridge.ThrowOnAnyCall = true;
            await fixture.Service.AbandonRecoveryAsync(fixture.Target, default);
            Equal(ReadStoredAuth(fixture, fixture.Id1), rotated,
                "recover rotation survives failed verification and offline abandon in the usable vault");
        }
        using var newer = new Fixture();
        await newer.BindBothAsync();
        await newer.Service.PrepareAsync(newer.Target, newer.Id2, default);
        var latest = Auth("one", "newer-vault-snapshot");
        var files = new WslCodexProtectedFiles(Path.Combine(newer.Root, "wsl"));
        using (files.Acquire())
        {
            var name = files.Names("accounts-*.dpapi").Single();
            using var vault = files.Read<WslCodexService.Vault>(name);
            vault.Accounts.Single(a => a.AccountId == newer.Id1).ReplaceAuth(latest);
            files.Write(name, vault);
        }
        newer.Bridge.ThrowOnAnyCall = true;
        await newer.Service.AbandonRecoveryAsync(newer.Target, default);
        Equal(ReadStoredAuth(newer, newer.Id1), latest, "a journal snapshot cannot overwrite a newer vault generation");
    }

    private static byte[] ReadStoredAuth(Fixture fixture, string id)
    {
        var files = new WslCodexProtectedFiles(Path.Combine(fixture.Root, "wsl"));
        using var lease = files.Acquire();
        using var vault = files.Read<WslCodexService.Vault>(files.Names("accounts-*.dpapi").Single());
        return vault.Accounts.Single(a => a.AccountId == id).Auth.ToArray();
    }

    private static void TransportHasNoCredentials()
    {
        var start = WslCodexBridge.CreateHelperStartInfo("Example distro", "example");
        Check(!start.UseShellExecute && start.CreateNoWindow && start.RedirectStandardInput, "helper is hidden with a private input pipe");
        Check(start.ArgumentList.Contains("/usr/bin/python3") && !start.ArgumentList.Contains("sh"), "request values are not shell interpolation");
        Check(start.ArgumentList[^1] == WslCodexLinuxHelper.Source && !start.ArgumentList.Any(s => s.Contains("synthetic-refresh")), "only fixed helper source is in arguments");
        var unsupported = new WslCodexTarget("Example", 1000, "example", "/home/example", "/home/example/.codex", "/mnt/c/codex.exe", "codex 1");
        try { unsupported.Validate(); throw new Exception("interop executable must fail"); }
        catch (WslCodexException) { }
    }

    private static byte[] Auth(string user, string revision)
    {
        var claims = JsonSerializer.SerializeToUtf8Bytes(new Dictionary<string, object>
        {
            ["sub"] = user, ["email"] = user + "@example.invalid",
            ["https://api.openai.com/auth"] = new { chatgpt_account_id = "fixture-workspace", chatgpt_user_id = user }
        });
        var payload = Convert.ToBase64String(claims).TrimEnd('=').Replace('+', '-').Replace('/', '_');
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            auth_mode = "chatgpt", tokens = new { id_token = "fixture." + payload + ".fixture", account_id = "fixture-workspace",
                access_token = "synthetic-access-" + revision, refresh_token = "synthetic-refresh-" + revision }
        });
    }
    private static string Key(string user) => CodexAccountStore.ParseIdentity(Auth(user, "id-only")).Key;
    private static void Check(bool valid, string message) { if (!valid) throw new Exception("WSL: " + message); }
    private static void Equal(byte[] a, byte[] b, string message) => Check(a.SequenceEqual(b), message);
    private static async Task Fails(Func<Task> operation)
    {
        try { await operation(); }
        catch (Exception e) when (e is InvalidOperationException or IOException) { return; }
        throw new Exception("WSL: expected guarded operation to fail");
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "WeeklyUsageIndicator.Tests", "wsl-" + Guid.NewGuid().ToString("N"));
        internal WslCodexTarget Target { get; } = new("Fixture", 1000, "fixture", "/home/fixture", "/home/fixture/.codex", "/usr/local/bin/codex", "codex-cli 0.153.2");
        internal string Id1 { get; } = Guid.NewGuid().ToString("N");
        internal string Id2 { get; } = Guid.NewGuid().ToString("N");
        internal FakeBridge Bridge { get; }
        internal WslCodexService Service { get; }
        internal Fixture() { Bridge = new(Target) { Live = Auth("one", "linux-one") }; Service = new(Root, Bridge); }
        internal async Task BindBothAsync()
        {
            await Service.CaptureCurrentAsync(Target, Id1, Key("one"), default);
            Bridge.Live = Auth("two", "linux-two");
            await Service.CaptureCurrentAsync(Target, Id2, Key("two"), default);
            Bridge.Live = Auth("one", "linux-one");
        }
        public void Dispose()
        {
            CodexAccountStore.RejectReparsePath(Root);
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class FakeBridge(WslCodexTarget target) : IWslCodexBridge
    {
        internal byte[] Live { get; set; } = [];
        internal byte[] LoginAuth { get; set; } = [];
        internal WslCodexAvailability Availability { get; set; } = WslCodexAvailability.Ready;
        internal bool Verify { get; set; } = true;
        internal bool ThrowOnAnyCall { get; set; }
        internal int VerifyCalls, ReplaceCalls, ReadCalls, LoginCalls;
        private void CheckCall() { if (ThrowOnAnyCall) throw new Exception("Offline archival/cancellation must not call a Linux bridge"); }
        public Task<IReadOnlyList<string>> ListRunningDistributionsAsync(CancellationToken token)
        { CheckCall(); return Task.FromResult<IReadOnlyList<string>>([target.Distribution]); }
        public Task<WslCodexProbe> ProbeAsync(string distribution, WslCodexTarget? expected, CancellationToken token)
        {
            CheckCall();
            return Task.FromResult(new WslCodexProbe(distribution, Availability, Availability == WslCodexAvailability.MissingCli ? null : target,
                Availability == WslCodexAvailability.Busy ? 2 : 0, Availability == WslCodexAvailability.MissingCli ? "missing-cli" : "writers-running"));
        }
        public Task<byte[]> ReadAuthAsync(WslCodexTarget target, CancellationToken token)
        { CheckCall(); ReadCalls++; return Task.FromResult(Live.ToArray()); }
        public Task<byte[]> LoginAsync(WslCodexTarget target, CancellationToken token, Action<Uri>? onAuthorizationUrl = null)
        { CheckCall(); LoginCalls++; onAuthorizationUrl?.Invoke(new Uri("https://auth.openai.com/oauth/authorize?state=fixture-only")); return Task.FromResult(LoginAuth.ToArray()); }
        public Task ReplaceAuthAsync(WslCodexTarget target, string expectedDigest, byte[] auth, CancellationToken token)
        {
            CheckCall();
            if (Convert.ToHexString(SHA256.HashData(Live)) != expectedDigest) throw new WslCodexException("auth-changed", "fixture CAS");
            Live = auth.ToArray(); ReplaceCalls++; return Task.CompletedTask;
        }
        public Task<bool> VerifyOfficialClientAsync(WslCodexTarget target, CancellationToken token)
        { CheckCall(); VerifyCalls++; return Task.FromResult(Verify); }
    }
}
