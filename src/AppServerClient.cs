using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;

namespace WeeklyUsageIndicator;

// account/read exposes no workspace/user ID. Email corroborates a session; it is not a full identity proof.
internal sealed record CodexAccountUsage(UsageSnapshot Usage, string? Email, string? PlanType, bool IsChatGpt);
internal sealed class CodexUsageAuthenticationException : IOException;

/// <summary>Owns one serialized, cancellable app-server session. No session owns another session's state.</summary>
internal sealed class AppServerClient : IDisposable
{
    private readonly object _stateLock = new();
    private readonly SemaphoreSlim _operationGate = new(1, 1);
    private readonly Func<ProcessStartInfo> _startInfoFactory;
    private readonly bool _ownJob;
    private CancellationTokenSource _generation = new();
    private Session? _session;
    private bool _suspended;
    private bool _disposed;
    private int _shutdowns;

    public AppServerClient() : this(CreateStartInfo) { }

    // Explicit isolated home in production; fake stdio server in tests.
    internal AppServerClient(Func<ProcessStartInfo> startInfoFactory, bool ownJob = false)
    { _startInfoFactory = startInfoFactory; _ownJob = ownJob; }

    public async Task<UsageSnapshot> GetWeeklyUsageAsync(CancellationToken cancellationToken) =>
        (await ReadUsageAsync(includeAccount: false, cancellationToken).ConfigureAwait(false)).Usage;

    public Task<CodexAccountUsage> GetWeeklyUsageWithAccountAsync(CancellationToken cancellationToken) =>
        ReadUsageAsync(includeAccount: true, cancellationToken);

    private async Task<CodexAccountUsage> ReadUsageAsync(bool includeAccount, CancellationToken cancellationToken)
    {
        CancellationToken generation;
        lock (_stateLock)
        {
            ThrowIfUnavailable();
            generation = _generation.Token;
        }

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, generation);
        await _operationGate.WaitAsync(linked.Token).ConfigureAwait(false);
        try
        {
            linked.Token.ThrowIfCancellationRequested();
            var session = await EnsureStartedAsync(linked.Token).ConfigureAwait(false);
            (string? Email, string? PlanType, bool IsChatGpt) account = (null, null, false);
            if (includeAccount)
                account = ParseAccount(await CallCoreAsync(session, "account/read", new { refreshToken = false }, linked.Token).ConfigureAwait(false));
            var result = await CallCoreAsync(session, "account/rateLimits/read", new { }, linked.Token).ConfigureAwait(false);
            if (includeAccount)
            {
                var after = ParseAccount(await CallCoreAsync(session, "account/read", new { refreshToken = false }, linked.Token).ConfigureAwait(false));
                if (account != after) throw new IOException("The Codex helper account changed during the usage read. Refresh again.");
            }
            linked.Token.ThrowIfCancellationRequested();
            return new CodexAccountUsage(ParseWeeklyUsage(result), account.Email, account.PlanType, account.IsChatGpt);
        }
        finally { _operationGate.Release(); }
    }

    private static (string? Email, string? PlanType, bool IsChatGpt) ParseAccount(JsonElement response)
    {
        if (response.ValueKind != JsonValueKind.Object || !response.TryGetProperty("account", out var account) ||
            account.ValueKind != JsonValueKind.Object) return (null, null, false);
        static string? ReadString(JsonElement value, string key) =>
            value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String && field.GetString() is { Length: <= 320 } text
                ? text : null;
        return (ReadString(account, "email"), ReadString(account, "planType"), ReadString(account, "type") == "chatgpt");
    }

    private async Task<Session> EnsureStartedAsync(CancellationToken token)
    {
        if (_session is { Initialized: true } existing && !existing.Process.HasExited && !existing.Disconnected)
            return existing;

        await StopSessionAsync().ConfigureAwait(false);
        Session session;
        lock (_stateLock)
        {
            // Starting the child and publishing ownership are atomic with respect to suspension.
            ThrowIfUnavailable();
            token.ThrowIfCancellationRequested();
            var process = new Process { StartInfo = _startInfoFactory(), EnableRaisingEvents = true };
            CodexLoginJob? job = _ownJob ? new CodexLoginJob(allowChildBreakaway: false) : null;
            var started = false;
            try
            {
                if (!process.Start()) throw new IOException("Codex app-server could not be started.");
                started = true;
                job?.Attach(process);
                session = new Session(process, job);
                _session = session;
            }
            catch
            {
                job?.Dispose();
                try
                {
                    if (started && !process.HasExited)
                    {
                        process.Kill(entireProcessTree: true);
                        if (!process.WaitForExit(8000)) throw new UsageHelperShutdownException();
                    }
                }
                catch { throw new UsageHelperShutdownException(); }
                finally { process.Dispose(); }
                throw;
            }
        }

        session.Reader = ReadLoopAsync(session);
        session.ErrorReader = DrainErrorAsync(session);
        try
        {
            await CallCoreAsync(session, "initialize", new
            {
                clientInfo = new { name = "weekly-usage-indicator", title = "Weekly Usage Indicator", version = "1.6.0" },
                capabilities = new { experimentalApi = true }
            }, token).ConfigureAwait(false);
            await SendLineAsync(session, JsonSerializer.Serialize(new { method = "initialized" }), token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            session.Initialized = true;
            return session;
        }
        catch
        {
            await StopSessionAsync().ConfigureAwait(false);
            throw;
        }
    }

    private static ProcessStartInfo CreateStartInfo()
    {
        var path = LocateCodexExecutable()
            ?? throw new FileNotFoundException("codex.exe was not found. Install or open Codex Desktop first.");
        return new ProcessStartInfo
        {
            FileName = path,
            Arguments = "app-server --stdio",
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)
        };
    }

    private static async Task<JsonElement> CallCoreAsync(Session session, string method, object parameters, CancellationToken token)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(token, session.Lifetime.Token);
        var id = ++session.NextRequestId;
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!session.Pending.TryAdd(id, completion)) throw new IOException("Could not register a Codex request.");
        try
        {
            await SendLineAsync(session, JsonSerializer.Serialize(new { id, method, @params = parameters }), linked.Token).ConfigureAwait(false);
            return await completion.Task.WaitAsync(linked.Token).ConfigureAwait(false);
        }
        finally { session.Pending.TryRemove(id, out _); }
    }

    private static async Task SendLineAsync(Session session, string line, CancellationToken token)
    {
        if (session.Disconnected) throw new IOException("Codex app-server disconnected.");
        await session.Process.StandardInput.WriteLineAsync(line.AsMemory(), token).ConfigureAwait(false);
        await session.Process.StandardInput.FlushAsync(token).ConfigureAwait(false);
    }

    private static async Task ReadLoopAsync(Session session)
    {
        try
        {
            while (!session.Lifetime.IsCancellationRequested)
            {
                var line = await session.Process.StandardOutput.ReadLineAsync(session.Lifetime.Token).ConfigureAwait(false);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line)) continue;
                try
                {
                    using var document = JsonDocument.Parse(line);
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object ||
                        !root.TryGetProperty("id", out var idElement) || idElement.ValueKind != JsonValueKind.Number || !idElement.TryGetInt32(out var id) ||
                        !session.Pending.TryGetValue(id, out var completion)) continue;
                    if (root.TryGetProperty("error", out var error))
                    {
                        // Protocol errors can include sensitive server context. Never surface the raw payload.
                        var message = error.ValueKind == JsonValueKind.Object && error.TryGetProperty("message", out var errorMessage)
                            && errorMessage.ValueKind == JsonValueKind.String ? errorMessage.GetString() ?? "" : "";
                        var authFailure = new[] { "401", "unauthorized", "authentication required", "refresh_token", "token expired" }
                            .Any(term => message.Contains(term, StringComparison.OrdinalIgnoreCase));
                        completion.TrySetException(authFailure ? new CodexUsageAuthenticationException() :
                            new IOException("Codex could not read account usage. Check the account login."));
                    }
                    else if (root.TryGetProperty("result", out var result))
                        completion.TrySetResult(result.Clone());
                    else completion.TrySetException(new InvalidDataException("Codex returned an incomplete response."));
                }
                catch (JsonException) { /* Ignore non-protocol diagnostics. */ }
            }
        }
        catch (OperationCanceledException) when (session.Lifetime.IsCancellationRequested) { }
        catch { /* The sanitized disconnect below is sufficient for callers. */ }
        finally
        {
            session.Disconnected = true;
            foreach (var completion in session.Pending.Values)
                completion.TrySetException(new IOException("Codex app-server disconnected."));
        }
    }

    private static async Task DrainErrorAsync(Session session)
    {
        try
        {
            while (await session.Process.StandardError.ReadLineAsync(session.Lifetime.Token).ConfigureAwait(false) is not null) { }
        }
        catch { /* Never retain or display stderr, which may contain account context. */ }
    }

    /// <summary>Blocks starts immediately and returns only once the owned writer has exited.</summary>
    public async Task SuspendAsync()
    {
        CancellationTokenSource generation;
        lock (_stateLock)
        {
            _suspended = true;
            _shutdowns++;
            generation = _generation;
        }
        generation.Cancel();
        await _operationGate.WaitAsync().ConfigureAwait(false);
        try { await StopSessionAsync().ConfigureAwait(false); }
        finally
        {
            lock (_stateLock) { _shutdowns--; }
            _operationGate.Release();
        }
    }

    public void Resume()
    {
        lock (_stateLock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_shutdowns != 0) throw new InvalidOperationException("Codex helper shutdown is still in progress.");
            if (!_suspended) return;
            if (_session is not null) throw new IOException("The previous Codex helper has not stopped.");
            _generation.Dispose();
            _generation = new CancellationTokenSource();
            _suspended = false;
        }
    }

    private async Task StopSessionAsync()
    {
        var session = _session;
        if (session is null) return;
        session.Lifetime.Cancel();
        session.Job?.Dispose();
        try { session.Process.StandardInput.Close(); } catch { }
        try
        {
            if (!session.Process.HasExited) session.Process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException) when (session.Process.HasExited) { }

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        try { await session.Process.WaitForExitAsync(timeout.Token).ConfigureAwait(false); }
        catch (OperationCanceledException)
        {
            // Keep ownership and remain suspended. The caller must not change credentials after this failure.
            throw new IOException("Codex helper did not exit. Account switching is blocked.");
        }
        await Task.WhenAll(session.Reader, session.ErrorReader).WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false);
        session.Process.Dispose();
        session.Lifetime.Dispose();
        _session = null;
    }

    private void ThrowIfUnavailable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_suspended) throw new OperationCanceledException("Codex usage is paused during account switching.");
    }

    public void Pause() => SuspendAsync().GetAwaiter().GetResult();

    public void Dispose()
    {
        lock (_stateLock)
        {
            if (_disposed) return;
            _disposed = true;
        }
        try { SuspendAsync().GetAwaiter().GetResult(); }
        catch { /* OS ownership checks also guard switching; Dispose must not tear down the UI with an exception. */ }
        // Keep gates and generation valid for callers already unwinding their canceled operation.
    }

    private sealed class Session(Process process, CodexLoginJob? job)
    {
        internal readonly Process Process = process;
        internal readonly CodexLoginJob? Job = job;
        internal readonly CancellationTokenSource Lifetime = new();
        internal readonly ConcurrentDictionary<int, TaskCompletionSource<JsonElement>> Pending = new();
        internal Task Reader = Task.CompletedTask;
        internal Task ErrorReader = Task.CompletedTask;
        internal int NextRequestId;
        internal bool Initialized;
        internal volatile bool Disconnected;
    }
    private static UsageSnapshot ParseWeeklyUsage(JsonElement response)
    {
        var snapshot = SelectCoreSnapshot(response);
        var limitId = snapshot.TryGetProperty("limitId", out var idElement) && idElement.ValueKind == JsonValueKind.String
            ? idElement.GetString() ?? "codex"
            : "codex";

        var windows = new List<(int Used, long? Duration, long? ResetsAt, string Name)>();
        AddWindow(snapshot, "primary", windows);
        AddWindow(snapshot, "secondary", windows);

        if (windows.Count == 0)
            throw new InvalidDataException("Codex did not return a usage window.");

        var weekly = windows
            .OrderBy(window => WeeklyDistance(window.Duration))
            .ThenByDescending(window => window.Duration ?? 0)
            .First();

        DateTimeOffset? resetsAt = null;
        if (weekly.ResetsAt is > 0)
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(weekly.ResetsAt.Value).ToLocalTime();

        return new UsageSnapshot(
            Math.Clamp(weekly.Used, 0, 100),
            resetsAt,
            weekly.Duration,
            limitId,
            windows.Where(w => w.Duration == 300 && w.Used is >= 0 and <= 100).Select(w => new UsageWindow(
                w.Used, w.ResetsAt is > 0 ? DateTimeOffset.FromUnixTimeSeconds(w.ResetsAt.Value) : null, w.Duration)).FirstOrDefault());
    }

    private static JsonElement SelectCoreSnapshot(JsonElement response)
    {
        if (response.TryGetProperty("rateLimitsByLimitId", out var byId) && byId.ValueKind == JsonValueKind.Object)
        {
            if (byId.TryGetProperty("codex", out var codex) && codex.ValueKind == JsonValueKind.Object)
                return codex;

            foreach (var property in byId.EnumerateObject())
            {
                if (property.Value.ValueKind != JsonValueKind.Object) continue;
                if (!property.Value.TryGetProperty("limitName", out var name) || name.ValueKind == JsonValueKind.Null)
                    return property.Value;
            }
        }

        if (response.TryGetProperty("rateLimits", out var legacy) && legacy.ValueKind == JsonValueKind.Object)
            return legacy;

        throw new InvalidDataException("Codex did not return rate-limit data.");
    }

    private static void AddWindow(
        JsonElement snapshot,
        string propertyName,
        ICollection<(int Used, long? Duration, long? ResetsAt, string Name)> windows)
    {
        if (!snapshot.TryGetProperty(propertyName, out var window) || window.ValueKind != JsonValueKind.Object)
            return;
        if (!window.TryGetProperty("usedPercent", out var usedElement) || !usedElement.TryGetInt32(out var used))
            return;

        long? duration = null;
        if (window.TryGetProperty("windowDurationMins", out var durationElement) &&
            durationElement.ValueKind == JsonValueKind.Number &&
            durationElement.TryGetInt64(out var durationValue))
        {
            duration = durationValue;
        }

        long? resetsAt = null;
        if (window.TryGetProperty("resetsAt", out var resetElement) &&
            resetElement.ValueKind == JsonValueKind.Number &&
            resetElement.TryGetInt64(out var resetValue))
        {
            resetsAt = resetValue;
        }

        windows.Add((used, duration, resetsAt, propertyName));
    }

    private static long WeeklyDistance(long? durationMinutes)
    {
        const long weekMinutes = 7 * 24 * 60;
        return durationMinutes is null
            ? long.MaxValue / 2
            : Math.Abs(durationMinutes.Value - weekMinutes);
    }

    internal static string? LocateCodexExecutable()
    {
        var configured = Environment.GetEnvironmentVariable("CODEX_WEEKLY_INDICATOR_CODEX_PATH");
        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured))
            return configured;

        var localBin = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OpenAI",
            "Codex",
            "bin");

        try
        {
            if (Directory.Exists(localBin))
            {
                var localCodex = Directory
                    .EnumerateFiles(localBin, "codex.exe", SearchOption.AllDirectories)
                    .Select(path => new FileInfo(path))
                    .OrderByDescending(file => file.LastWriteTimeUtc)
                    .FirstOrDefault();
                if (localCodex is not null) return localCodex.FullName;
            }
        }
        catch
        {
            // Continue to PATH lookup.
        }

        var pathValue = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in pathValue.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim().Trim('"'), "codex.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

}
