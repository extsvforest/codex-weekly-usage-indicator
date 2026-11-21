using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Net.Mail;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WeeklyUsageIndicator;

/// <summary>One of Antigravity's usage windows: the share that remains and when it refills. A window not used yet has
/// no reset time.</summary>
internal sealed record AntigravityWindow(double RemainingFraction, DateTimeOffset? ResetsAt)
{
    internal int RemainingPercent => (int)Math.Round(RemainingFraction * 100, MidpointRounding.AwayFromZero);
}

/// <summary>What <c>/usage</c> reports: a weekly and a five-hour window for the Gemini models and for the Claude and
/// GPT models (the CLI's "3p" group). A window the CLI did not report is null.</summary>
internal sealed record AntigravityUsage(AntigravityWindow? GeminiWeekly, AntigravityWindow? GeminiFiveHour,
    AntigravityWindow? ThirdPartyWeekly, AntigravityWindow? ThirdPartyFiveHour);

/// <summary>An Antigravity account as the widget keeps it: an opaque key, the name the user gave it (none until then),
/// a masked hint and the last usage seen. Never the address itself.</summary>
internal sealed record AntigravityAccount(string IdentityKey, string? Label, string Hint, AntigravityUsage? Usage,
    DateTimeOffset? ObservedAt);

internal enum AntigravityReadKind { Success, NotSignedIn, ExecutableMissing, TimedOut, Malformed, Failed }

/// <summary>How one read ended. <c>Account</c> and <c>FirstSeen</c> (an account the widget had not seen before) come with
/// success; <c>Detail</c> is a canonical message for the other outcomes, translated where it is shown.</summary>
internal sealed record AntigravityReadResult(AntigravityReadKind Kind, AntigravityAccount? Account = null,
    bool FirstSeen = false, string? Detail = null);

/// <summary>A report that says it succeeded but cannot be read as usage. The message is canonical and holds no account.</summary>
internal sealed class AntigravityOutputException(string message) : Exception(message);

/// <summary>Reads the JSON <c>agy -p "/usage" --output-format json</c> prints.</summary>
internal static class AntigravityUsageReport
{
    internal const string NotUsage = "Antigravity 사용량 응답의 형식을 알아보지 못했습니다.";
    internal const string TurnRan = "Antigravity가 사용량 명령 대신 대화를 실행했습니다. 사용량이 차감되었을 수 있습니다.";

    // An unused window reports the Unix epoch as its reset.
    private static readonly DateTimeOffset NeverUsed = new(1971, 1, 1, 0, 0, 0, TimeSpan.Zero);

    /// <summary>The usage of a report that succeeded, or null when the CLI reported no success (an error, or no JSON).
    /// A success that does not read as the usage command's answer throws <see cref="AntigravityOutputException"/>.</summary>
    internal static AntigravityUsage? Parse(string output)
    {
        JsonDocument document;
        try { document = JsonDocument.Parse(output.TrimStart('﻿').Trim(), new JsonDocumentOptions { MaxDepth = 32 }); }
        catch (JsonException) { return null; }
        using (document)
        {
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("status", out var status) ||
                status.ValueKind != JsonValueKind.String || status.GetString() != "SUCCESS") return null;
            // A slash command answers without a turn. A turn means the prompt went to a model, which uses quota.
            if (root.TryGetProperty("num_turns", out var turns) && (turns.ValueKind != JsonValueKind.Number || turns.GetDouble() != 0) ||
                root.TryGetProperty("usage", out var tokens) && tokens.ValueKind == JsonValueKind.Object &&
                tokens.TryGetProperty("total_tokens", out var total) && (total.ValueKind != JsonValueKind.Number || total.GetDouble() != 0))
                throw new AntigravityOutputException(TurnRan);
            if (!root.TryGetProperty("command", out var command) || command.ValueKind != JsonValueKind.Object ||
                !command.TryGetProperty("name", out var name) || name.ValueKind != JsonValueKind.String || name.GetString() != "usage" ||
                !command.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Object ||
                !data.TryGetProperty("groups", out var groups) || groups.ValueKind != JsonValueKind.Array)
                throw new AntigravityOutputException(NotUsage);
            var windows = new Dictionary<string, AntigravityWindow>(StringComparer.Ordinal);
            foreach (var group in groups.EnumerateArray())
            {
                if (group.ValueKind != JsonValueKind.Object || !group.TryGetProperty("buckets", out var buckets)) continue;
                if (buckets.ValueKind != JsonValueKind.Array) throw new AntigravityOutputException(NotUsage);
                foreach (var bucket in buckets.EnumerateArray())
                {
                    if (bucket.ValueKind != JsonValueKind.Object || !bucket.TryGetProperty("id", out var id) ||
                        id.ValueKind != JsonValueKind.String || id.GetString() is not ("gemini-weekly" or "gemini-5h" or "3p-weekly" or "3p-5h"))
                        continue;
                    if (!windows.TryAdd(id.GetString()!, Window(bucket))) throw new AntigravityOutputException(NotUsage);
                }
            }
            if (windows.Count == 0) throw new AntigravityOutputException(NotUsage);
            return new AntigravityUsage(windows.GetValueOrDefault("gemini-weekly"), windows.GetValueOrDefault("gemini-5h"),
                windows.GetValueOrDefault("3p-weekly"), windows.GetValueOrDefault("3p-5h"));
        }
    }

    private static AntigravityWindow Window(JsonElement bucket)
    {
        // The CLI leaves a zero out of its JSON, so a window used up has no remaining_fraction.
        var fraction = 0d;
        if (bucket.TryGetProperty("remaining_fraction", out var remaining))
        {
            if (remaining.ValueKind != JsonValueKind.Number || !remaining.TryGetDouble(out fraction) ||
                !double.IsFinite(fraction) || fraction is < 0 or > 1) throw new AntigravityOutputException(NotUsage);
        }
        DateTimeOffset? resets = null;
        // A reset that does not read as a time is not known; the share still stands.
        if (bucket.TryGetProperty("reset_time", out var reset) && reset.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(reset.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var at) &&
            at >= NeverUsed) resets = at.ToUniversalTime();
        return new AntigravityWindow(fraction, resets);
    }

    /// <summary>Whether what the CLI wrote says it has no signed-in account: in print mode without a terminal it stops
    /// rather than start its sign-in.</summary>
    internal static bool SaysSignedOut(string text) =>
        new[] { "not logged in", "not logged into antigravity", "currently not signed in", "please sign in", "cannot complete interactive login" }
            .Any(phrase => text.Contains(phrase, StringComparison.OrdinalIgnoreCase));

    private static readonly Regex Address = new(@"[^\s""'<>(),;:@]+@[^\s""'<>(),;:@]+", RegexOptions.CultureInvariant);
    private static readonly Regex Spacing = new(@"[\s\p{Cc}]+", RegexOptions.CultureInvariant);

    /// <summary>A short line of what the CLI said, for the user: addresses masked, line breaks and control characters
    /// turned into single spaces.</summary>
    internal static string Excerpt(string text)
    {
        var line = text;
        try
        {
            using var document = JsonDocument.Parse(text.TrimStart('﻿').Trim());
            foreach (var field in new[] { "error", "message", "response" })
                if (document.RootElement.ValueKind == JsonValueKind.Object && document.RootElement.TryGetProperty(field, out var value))
                {
                    line = value.ValueKind == JsonValueKind.String ? value.GetString() ?? line
                        : value.ValueKind == JsonValueKind.Object && value.TryGetProperty("message", out var message) &&
                          message.ValueKind == JsonValueKind.String ? message.GetString() ?? line : line;
                    break;
                }
        }
        catch (JsonException) { }
        line = Address.Replace(Spacing.Replace(line, " "), "***").Trim();
        return line.Length > 160 ? line[..160] + "…" : line;
    }
}

/// <summary>Who a run belongs to. The CLI prints no account; its log has one line naming the address. The address is
/// turned at once into an opaque key and a masked hint and kept nowhere.</summary>
internal static class AntigravityIdentity
{
    internal const string Ambiguous = "Antigravity 로그에 계정이 둘 이상 나와 어느 계정인지 알 수 없습니다.";
    private static readonly Regex Line = new(@"applyAuthResult: email=(?<email>[^,\s]*)", RegexOptions.CultureInvariant);

    /// <summary>The account of a run from its log lines, or null when no line names one.</summary>
    internal static (string Key, string Hint)? FromLog(IEnumerable<string> lines)
    {
        string? found = null;
        foreach (var line in lines)
        {
            if (!line.Contains("applyAuthResult", StringComparison.Ordinal)) continue;
            foreach (Match match in Line.Matches(line))
            {
                var email = match.Groups["email"].Value.Trim();
                if (!MailAddress.TryCreate(email, out var address) || address.Address != email) continue;
                email = email.ToLowerInvariant();
                if (found is not null && found != email) throw new AntigravityOutputException(Ambiguous);
                found = email;
            }
        }
        return found is null ? null : (Key(found), Hint(found));
    }

    internal static string Key(string email) => Convert.ToHexString(SHA256.HashData(
        Encoding.UTF8.GetBytes("antigravity\n" + email.Trim().ToLowerInvariant())));

    /// <summary>The first character and the domain, as <c>a***@example.com</c>; with a domain too long to show, the start
    /// of the key instead.</summary>
    internal static string Hint(string email)
    {
        email = email.Trim().ToLowerInvariant();
        var at = email.LastIndexOf('@');
        var first = email.Length > 0 && char.IsAsciiLetterOrDigit(email[0]) ? email[0].ToString() : "*";
        var domain = at >= 0 ? email[(at + 1)..] : "";
        return domain.Length is > 0 and <= 60 && domain.All(character => char.IsAsciiLetterOrDigit(character) || character is '.' or '-')
            ? first + "***@" + domain
            : first + "*** · " + Key(email)[..8];
    }
}

/// <summary>Finds and runs the official Antigravity CLI. Only its <c>/usage</c> command, started directly with an
/// argument list (no shell, which could turn <c>/usage</c> into a path and send it to a model as a prompt).</summary>
internal static class AntigravityCli
{
    internal const string ExecutableVariable = "AGY_EXE";
    internal const int MaxOutputChars = 256 * 1024;

    internal sealed record Run(int? ExitCode, string Output, string Error, bool TimedOut, bool Truncated);

    /// <summary>Where agy is: <c>AGY_EXE</c>, the installer's place in the user profile, the per-user program folder,
    /// then the PATH. Only absolute paths to an <c>.exe</c>: a script would need a shell.</summary>
    internal static string? Locate(string? configured, string userProfile, string localAppData, string? path,
        Func<string, bool> exists)
    {
        var candidates = new List<string?>
        {
            configured,
            Path.Combine(userProfile, ".gemini", "antigravity-cli", "bin", "agy.exe"),
            Path.Combine(localAppData, "agy", "bin", "agy.exe")
        };
        foreach (var directory in (path ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var unquoted = directory.Trim('"');
            if (unquoted.Length > 0 && Path.IsPathFullyQualified(unquoted)) candidates.Add(Path.Combine(unquoted, "agy.exe"));
        }
        foreach (var candidate in candidates)
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var trimmed = candidate.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(trimmed) || !trimmed.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) continue;
            if (exists(trimmed)) return trimmed;
        }
        return null;
    }

    internal static string? Locate() => Locate(Environment.GetEnvironmentVariable(ExecutableVariable),
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        Environment.GetEnvironmentVariable("PATH"), File.Exists);

    internal static ProcessStartInfo CreateStartInfo(string executable, string logPath, string workingDirectory)
    {
        var info = new ProcessStartInfo(executable)
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false), StandardErrorEncoding = new UTF8Encoding(false),
            WorkingDirectory = workingDirectory
        };
        foreach (var argument in new[] { "-p", "/usage", "--output-format", "json", "--log-file", logPath })
            info.ArgumentList.Add(argument);
        // Only the lowercase value is honoured. A read must not update the CLI.
        info.Environment["AGY_CLI_DISABLE_AUTO_UPDATE"] = "true";
        return info;
    }

    /// <summary>Runs <c>/usage</c> once. Everything the CLI starts belongs to one job, which ends when the run does, so
    /// nothing it started outlives the read (and nothing keeps its log open).</summary>
    internal static async Task<Run> RunAsync(string executable, string logPath, string workingDirectory, TimeSpan timeout,
        CancellationToken token)
    {
        using var job = new CodexLoginJob(allowChildBreakaway: false);
        using var process = new Process { StartInfo = CreateStartInfo(executable, logPath, workingDirectory) };
        if (!process.Start()) throw new IOException("Antigravity CLI를 시작하지 못했습니다.");
        try { job.Attach(process); }
        catch
        {
            TryKill(process);
            throw;
        }
        process.StandardInput.Close();
        var output = ReadAsync(process.StandardOutput);
        var error = ReadAsync(process.StandardError);
        var timedOut = false;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
            deadline.CancelAfter(timeout);
            try { await process.WaitForExitAsync(deadline.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!token.IsCancellationRequested) { timedOut = true; }
        }
        finally
        {
            try { await job.TerminateAndWaitAsync().ConfigureAwait(false); }
            catch (IOException) { TryKill(process); }
        }
        token.ThrowIfCancellationRequested();
        var (stdout, cutOut) = await output.ConfigureAwait(false);
        var (stderr, cutError) = await error.ConfigureAwait(false);
        return new Run(timedOut ? null : process.ExitCode, stdout, stderr, timedOut, cutOut || cutError);
    }

    private static async Task<(string Text, bool Truncated)> ReadAsync(StreamReader reader)
    {
        var text = new StringBuilder();
        var buffer = new char[4096];
        var truncated = false;
        int read;
        while ((read = await reader.ReadAsync(buffer).ConfigureAwait(false)) > 0)
        {
            // Read to the end, so the CLI never blocks on a full pipe, but keep no more than the cap.
            var room = MaxOutputChars - text.Length;
            if (read > room) truncated = true;
            if (room > 0) text.Append(buffer, 0, Math.Min(read, room));
        }
        return (text.ToString(), truncated);
    }

    private static void TryKill(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception or NotSupportedException) { }
    }
}

/// <summary>Reads Antigravity usage when the user asks, one read at a time: a press while a read is under way waits for
/// that read instead of starting another. The account the read names is recorded in the store.</summary>
internal sealed class AntigravityUsageReader : IDisposable
{
    internal static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(60);
    internal const string Missing = "Antigravity CLI(agy)를 찾지 못했습니다.";
    internal const string SignedOut = "Antigravity CLI에 로그인되어 있지 않습니다. agy를 직접 실행해 로그인해 주세요.";
    internal const string Late = "Antigravity 사용량 응답 시간이 초과되었습니다.";
    internal const string Unidentified = "Antigravity 계정을 확인하지 못했습니다.";
    internal const string FailedWith = "Antigravity 사용량을 읽지 못했습니다: {0}";
    internal const string FailedPlain = "Antigravity 사용량을 읽지 못했습니다.";

    // A log older than any run can be: left by a read that never finished.
    private static readonly TimeSpan Leftover = TimeSpan.FromMinutes(2);
    private readonly AntigravityAccountStore _store;
    private readonly Func<string?> _locate;
    private readonly TimeSpan _timeout;
    private readonly TimeProvider _time;
    private readonly CancellationTokenSource _lifetime = new();
    private readonly object _gate = new();
    private Task<AntigravityReadResult>? _read;
    private bool _disposed;

    internal AntigravityUsageReader(AntigravityAccountStore store, Func<string?>? locate = null, TimeSpan? timeout = null,
        TimeProvider? time = null)
    {
        _store = store;
        _locate = locate ?? AntigravityCli.Locate;
        _timeout = timeout ?? DefaultTimeout;
        _time = time ?? TimeProvider.System;
        // A widget that ended during a read (exit, crash, kill) can leave a log that names the account. Only files go;
        // no agy run starts here.
        if (Directory.Exists(_store.RunDirectory)) ClearLeftovers(_store.RunDirectory);
    }

    internal bool IsReading { get { lock (_gate) return _read is { IsCompleted: false }; } }

    /// <summary>Reads usage once, or joins the read under way. Cancelling stops only this caller's wait.</summary>
    internal Task<AntigravityReadResult> ReadAsync(CancellationToken token = default)
    {
        Task<AntigravityReadResult> read;
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_read is not { IsCompleted: false }) _read = Task.Run(() => ReadOnceAsync(_lifetime.Token));
            read = _read;
        }
        return token.CanBeCanceled ? read.WaitAsync(token) : read;
    }

    private async Task<AntigravityReadResult> ReadOnceAsync(CancellationToken token)
    {
        var executable = _locate();
        if (executable is null || !File.Exists(executable)) return new(AntigravityReadKind.ExecutableMissing, Detail: Missing);
        string directory;
        try { directory = _store.PrepareRunDirectory(); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return new(AntigravityReadKind.Failed, Detail: exception.Message); }
        ClearLeftovers(directory);
        var log = Path.Combine(directory, "usage-" + Guid.NewGuid().ToString("N") + ".log");
        AntigravityCli.Run run;
        (string Key, string Hint)? identity;
        try
        {
            try { run = await AntigravityCli.RunAsync(executable, log, directory, _timeout, token).ConfigureAwait(false); }
            catch (Win32Exception) when (!File.Exists(executable)) { return new(AntigravityReadKind.ExecutableMissing, Detail: Missing); }
            catch (Win32Exception exception)
            { return new(AntigravityReadKind.Failed, Detail: string.Format(CultureInfo.InvariantCulture, FailedWith, exception.NativeErrorCode)); }
            catch (Exception exception) when (exception is IOException or InvalidOperationException)
            { return new(AntigravityReadKind.Failed, Detail: exception.Message); }
            if (run.TimedOut) return new(AntigravityReadKind.TimedOut, Detail: Late);
            try { identity = AntigravityIdentity.FromLog(ReadLog(log)); }
            catch (AntigravityOutputException exception) { return new(AntigravityReadKind.Malformed, Detail: exception.Message); }
        }
        finally { DeleteLog(log); }

        AntigravityUsage? usage;
        try { usage = run.Truncated ? throw new AntigravityOutputException(AntigravityUsageReport.NotUsage) : AntigravityUsageReport.Parse(run.Output); }
        catch (AntigravityOutputException exception) { return new(AntigravityReadKind.Malformed, Detail: exception.Message); }
        if (usage is not null)
        {
            if (identity is not { } account) return new(AntigravityReadKind.Malformed, Detail: Unidentified);
            try
            {
                var (saved, firstSeen) = _store.RecordObservation(account.Key, account.Hint, usage, _time.GetUtcNow());
                return new(AntigravityReadKind.Success, saved, firstSeen);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
                                                  or InvalidDataException)
            { return new(AntigravityReadKind.Failed, Detail: exception.Message); }
        }
        if (identity is null && AntigravityUsageReport.SaysSignedOut(run.Output + "\n" + run.Error))
            return new(AntigravityReadKind.NotSignedIn, Detail: SignedOut);
        var said = AntigravityUsageReport.Excerpt(run.Output.Trim().Length > 0 ? run.Output : run.Error);
        return new(AntigravityReadKind.Failed, Detail: said.Length > 0
            ? string.Format(CultureInfo.InvariantCulture, FailedWith, said) : FailedPlain);
    }

    /// <summary>Only the lines that can name the account; the log is read once, after the run has ended.</summary>
    private static List<string> ReadLog(string path)
    {
        var lines = new List<string>();
        if (!File.Exists(path)) return lines;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        var budget = 16L * 1024 * 1024;
        while (reader.ReadLine() is { } line && (budget -= line.Length) > 0)
            if (line.Contains("applyAuthResult", StringComparison.Ordinal)) lines.Add(line);
        return lines;
    }

    /// <summary>The log names the account: it goes as soon as the run has ended. What cannot be deleted is emptied.</summary>
    internal static void DeleteLog(string path)
    {
        for (var attempt = 0; attempt < 10; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return;
                File.Delete(path);
                if (!File.Exists(path)) return;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
            Thread.Sleep(50);
        }
        try { using var _ = new FileStream(path, FileMode.Truncate, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    private void ClearLeftovers(string directory)
    {
        try
        {
            foreach (var path in Directory.EnumerateFiles(directory, "usage-*.log"))
                if (_time.GetUtcNow().UtcDateTime - File.GetLastWriteTimeUtc(path) > Leftover) DeleteLog(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
        }
        // Ends a read under way, and everything its CLI started, then gives that read a bounded moment to delete its log.
        _lifetime.Cancel();
        Task? read;
        lock (_gate) read = _read;
        try { read?.Wait(TimeSpan.FromSeconds(10)); }
        catch (AggregateException) { }
    }
}
