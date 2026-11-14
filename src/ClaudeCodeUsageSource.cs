using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WeeklyUsageIndicator;

internal interface IClaudeUsageSource
{
    Task<ClaudeUsageSnapshot> ReadAsync(CancellationToken cancellationToken);
}

internal sealed record ClaudeCommandResult(int ExitCode, string StandardOutput, string StandardError);

internal interface IClaudeCommandRunner
{
    Task<ClaudeCommandResult> RunUsageAsync(CancellationToken cancellationToken);
}

internal sealed class ClaudeCodeUsageSource : IClaudeUsageSource
{
    private readonly IClaudeCommandRunner _commandRunner;
    private readonly TimeProvider _timeProvider;

    public ClaudeCodeUsageSource()
        : this(new ClaudeCodeProcessRunner(), TimeProvider.System)
    {
    }

    internal ClaudeCodeUsageSource(IClaudeCommandRunner commandRunner, TimeProvider timeProvider)
    {
        _commandRunner = commandRunner;
        _timeProvider = timeProvider;
    }

    public async Task<ClaudeUsageSnapshot> ReadAsync(CancellationToken cancellationToken)
    {
        var result = await _commandRunner.RunUsageAsync(cancellationToken);
        if (result.ExitCode != 0)
        {
            if (LooksLikeAuthenticationFailure(result.StandardError) ||
                LooksLikeAuthenticationFailure(result.StandardOutput))
            {
                throw new ClaudeLoginRequiredException();
            }

            throw new IOException($"Claude Code /usage exited with code {result.ExitCode}.");
        }

        return ParseEnvelope(result.StandardOutput, _timeProvider.GetUtcNow());
    }

    internal static ClaudeUsageSnapshot ParseEnvelope(string json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        JsonElement? usageReport = null;
        var verbose = root.ValueKind == JsonValueKind.Array;
        if (verbose)
        {
            JsonElement? finalResult = null;
            foreach (var entry in root.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object ||
                    !TryReadString(entry, "type", out var type)) continue;
                if (type == "result")
                {
                    if (finalResult is not null)
                        throw new InvalidDataException("Claude Code returned multiple /usage results.");
                    finalResult = entry;
                }
                else if (type == "assistant" && entry.TryGetProperty("usage_report", out var report))
                {
                    if (usageReport is not null)
                        throw new InvalidDataException("Claude Code returned multiple /usage reports.");
                    usageReport = report;
                }
            }
            root = finalResult ?? throw new InvalidDataException("Claude Code returned no /usage result.");
        }
        if (root.ValueKind != JsonValueKind.Object ||
            !TryReadBoolean(root, "is_error", out var isError) ||
            !TryReadInt32(root, "num_turns", out var numTurns) ||
            !TryReadDouble(root, "total_cost_usd", out var totalCost) ||
            !TryReadString(root, "result", out var resultText))
        {
            throw new InvalidDataException("Claude Code returned an invalid /usage response.");
        }

        if (isError)
        {
            if (LooksLikeAuthenticationFailure(resultText))
                throw new ClaudeLoginRequiredException();
            throw new IOException("Claude Code could not retrieve /usage.");
        }

        // /usage is a local built-in command. Refuse any response that consumed a model turn or money.
        if (numTurns != 0 || Math.Abs(totalCost) > double.Epsilon)
            throw new InvalidDataException("Claude Code /usage unexpectedly consumed a model turn.");

        if (verbose) RequireCurrentLimits(usageReport);
        return ParseUsageText(resultText, now);
    }

    private static void RequireCurrentLimits(JsonElement? report)
    {
        if (report is not { ValueKind: JsonValueKind.Object } usage ||
            !usage.TryGetProperty("rate_limits", out var rateLimits))
            throw new InvalidDataException("Claude Code returned no structured /usage report.");

        // Current CLIs render old fallback percentages without a last-known label,
        // but omit their structured limits. Reject that fallback. An official
        // successful snapshot up to 60 seconds old may still be returned here;
        // this is a check time, not proof of a new network request.
        if (rateLimits.ValueKind == JsonValueKind.Null)
            throw new IOException("Claude Code could not retrieve current usage limits.");
        if (rateLimits.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Claude Code returned invalid structured usage limits.");
        if (!rateLimits.TryGetProperty("limits", out var limits))
            throw new IOException("Claude Code returned last-known usage instead of current limits.");
        if (limits.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("Claude Code returned invalid structured usage limits.");
        if (limits.GetArrayLength() == 0)
            throw new IOException("Claude Code did not return current usage limits.");
    }

    internal static ClaudeUsageSnapshot ParseUsageText(string text, DateTimeOffset now)
    {
        // The official CLI can fall back to its own cached output. Its observation
        // time is unavailable here, so do not label that response as a fresh check.
        if (text.Contains("Showing last-known usage", StringComparison.OrdinalIgnoreCase))
            throw new IOException("Claude Code returned last-known usage instead of a fresh check.");

        ClaudeUsageWindow? fiveHour = null;
        ClaudeUsageWindow? weekly = null;
        ClaudeUsageWindow? fable = null;

        foreach (var line in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (TryParseWindow(line, "Current session", now, out var window))
                fiveHour = window;
            else if (TryParseWindow(line, "Current week (all models)", now, out window))
                weekly = window;
            else if (TryParseWindow(line, "Current week (Fable)", now, out window))
                fable = window;
        }

        // Each limit is independent. Plans without a model-scoped window still
        // have an all-model weekly window; never substitute one for another.
        if (fiveHour is null && weekly is null && fable is null)
            throw new InvalidDataException("Claude Code /usage did not return a valid usage limit.");

        return new ClaudeUsageSnapshot(fiveHour, weekly, fable);
    }

    private static bool TryParseWindow(
        string line,
        string label,
        DateTimeOffset now,
        out ClaudeUsageWindow? window)
    {
        window = null;
        if (!line.StartsWith(label + ":", StringComparison.Ordinal)) return false;

        var match = UsageLinePattern.Match(line);
        if (!match.Success || !match.Groups["label"].Value.Equals(label, StringComparison.Ordinal))
            return true;

        if (!double.TryParse(
                match.Groups["percent"].Value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var usedPercent) ||
            !double.IsFinite(usedPercent) ||
            usedPercent is < 0d or > 100d)
        {
            return true;
        }

        DateTimeOffset? resetsAt = null;
        var resetMatch = ResetPattern.Match(match.Groups["resetSuffix"].Value);
        if (resetMatch.Success)
        {
            try
            {
                resetsAt = ParseResetTime(
                    resetMatch.Groups["reset"].Value,
                    resetMatch.Groups["timezone"].Value,
                    now);
            }
            catch (InvalidDataException)
            {
                // The percentage is independently valid; unknown dates stay unknown.
            }
        }
        window = new ClaudeUsageWindow(usedPercent, resetsAt);
        return true;
    }

    private static DateTimeOffset ParseResetTime(string rawReset, string timeZoneId, DateTimeOffset now)
    {
        TimeZoneInfo timeZone;
        try
        {
            timeZone = TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (TimeZoneNotFoundException exception)
        {
            throw new InvalidDataException("Claude Code returned an unknown reset time zone.", exception);
        }
        catch (InvalidTimeZoneException exception)
        {
            throw new InvalidDataException("Claude Code returned an invalid reset time zone.", exception);
        }

        var localNow = TimeZoneInfo.ConvertTime(now, timeZone);
        if (!DateTime.TryParseExact(
                $"{rawReset} {localNow.Year}",
                ["MMM d, h:mmtt yyyy", "MMM d, htt yyyy"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var localReset))
        {
            throw new InvalidDataException("Claude Code returned an invalid reset time.");
        }

        localReset = DateTime.SpecifyKind(localReset, DateTimeKind.Unspecified);
        if (localReset < localNow.DateTime.AddDays(-30)) localReset = localReset.AddYears(1);
        if (timeZone.IsInvalidTime(localReset))
            throw new InvalidDataException("Claude Code returned a reset time in a daylight-saving gap.");

        var offset = timeZone.GetUtcOffset(localReset);
        return new DateTimeOffset(localReset, offset);
    }

    private static bool LooksLikeAuthenticationFailure(string text) =>
        text.Contains("not logged", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("login required", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("please login", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("oauth token", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("authentication", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("invalid api key", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("unauthorized", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("account has been disabled", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("organization has been disabled", StringComparison.OrdinalIgnoreCase);

    private static bool TryReadBoolean(JsonElement root, string name, out bool value)
    {
        value = false;
        if (!root.TryGetProperty(name, out var property) ||
            property.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
        {
            return false;
        }

        value = property.GetBoolean();
        return true;
    }

    private static bool TryReadInt32(JsonElement root, string name, out int value)
    {
        value = 0;
        return root.TryGetProperty(name, out var property) && property.TryGetInt32(out value);
    }

    private static bool TryReadDouble(JsonElement root, string name, out double value)
    {
        value = 0;
        return root.TryGetProperty(name, out var property) &&
            property.TryGetDouble(out value) && double.IsFinite(value);
    }

    private static bool TryReadString(JsonElement root, string name, out string value)
    {
        value = string.Empty;
        if (!root.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.String)
            return false;
        value = property.GetString() ?? string.Empty;
        return value.Length > 0;
    }

    private static readonly Regex UsageLinePattern = new(
        @"^(?<label>Current (?:session|week \(all models\)|week \(Fable\))):\s*(?<percent>\d+(?:\.\d+)?)%\s+used(?:\s+·\s+resets\s+(?<resetSuffix>.+?))?\s*$",
        RegexOptions.CultureInvariant);

    private static readonly Regex ResetPattern = new(
        @"^(?<reset>.+?)\s+\((?<timezone>[^)]+)\)$",
        RegexOptions.CultureInvariant);
}

internal sealed class ClaudeCodeProcessRunner : IClaudeCommandRunner
{
    // Claude Code 2.1.321 computes "What's contributing to your usage?" from this machine's
    // transcripts alongside the limits fetch and answers only when both finish. Observed
    // 2026-11-14: 27 s and 50 s for one week of shared transcripts, so 20 s timed out the
    // usual read. The ten-minute interval spaces requests, not this wait.
    internal static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(2);
    private const int MaximumOutputCharacters = 128 * 1024;
    private readonly string? _configDirectory;
    private readonly Action<ProcessStartInfo>? _configureProcess;

    internal ClaudeCodeProcessRunner(string? configDirectory = null, Action<ProcessStartInfo>? configureProcess = null)
    {
        _configDirectory = configDirectory;
        _configureProcess = configureProcess;
    }

    public async Task<ClaudeCommandResult> RunUsageAsync(CancellationToken cancellationToken)
    {
        var executablePath = FindClaudeExecutable();
        var startInfo = CreateStartInfo(executablePath, _configDirectory);
        _configureProcess?.Invoke(startInfo);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new IOException("Claude Code could not be started.");
            // The prompt is an argument. Close stdin so a print-mode CLI never waits for piped input.
            try { process.StandardInput.Close(); }
            catch (IOException) { /* An early exit already closed the pipe; its result follows. */ }
        }
        catch (Win32Exception exception)
        {
            throw new IOException("Claude Code could not be started.", exception);
        }

        using var timeout = new CancellationTokenSource(CommandTimeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, timeout.Token);
        try
        {
            var outputTask = process.StandardOutput.ReadToEndAsync(linked.Token);
            var errorTask = process.StandardError.ReadToEndAsync(linked.Token);
            await process.WaitForExitAsync(linked.Token);
            var output = await outputTask;
            var error = await errorTask;
            if (output.Length > MaximumOutputCharacters || error.Length > MaximumOutputCharacters)
                throw new InvalidDataException("Claude Code returned too much /usage output.");
            return new ClaudeCommandResult(process.ExitCode, output, error);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested && !cancellationToken.IsCancellationRequested)
        {
            TryKill(process);
            throw new TimeoutException("Claude Code /usage timed out.");
        }
        catch
        {
            TryKill(process);
            throw;
        }
    }

    internal static ProcessStartInfo CreateStartInfo(string executablePath, string? configDirectory = null)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardErrorEncoding = System.Text.Encoding.UTF8
        };
        foreach (var argument in new[]
        {
            "-p",
            "--safe-mode",
            "--no-session-persistence",
            "--no-chrome",
            "/usage",
            "--output-format",
            "json",
            "--verbose",
            "--max-turns",
            "0"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }
        if (configDirectory is not null)
        {
            ConfigureProfileEnvironment(startInfo, configDirectory);
        }
        return startInfo;
    }

    internal static void ConfigureProfileEnvironment(ProcessStartInfo startInfo, string configDirectory)
    {
        if (!Path.IsPathFullyQualified(configDirectory))
            throw new ArgumentException("Claude profile directory must be an absolute path.", nameof(configDirectory));
        startInfo.Environment["CLAUDE_CONFIG_DIR"] = configDirectory;
        foreach (var variable in new[]
        {
            "ANTHROPIC_API_KEY", "ANTHROPIC_AUTH_TOKEN", "CLAUDE_CODE_OAUTH_TOKEN",
            "CLAUDE_CODE_OAUTH_TOKEN_FILE_DESCRIPTOR", "ANTHROPIC_PROFILE", "ANTHROPIC_BASE_URL",
            "CLAUDE_CODE_USE_BEDROCK", "CLAUDE_CODE_USE_VERTEX", "CLAUDE_CODE_USE_FOUNDRY",
            "CLAUDE_CODE_SKIP_BEDROCK_AUTH", "CLAUDE_CODE_SKIP_VERTEX_AUTH", "CLAUDE_CODE_SKIP_FOUNDRY_AUTH"
        }) startInfo.Environment.Remove(variable);
    }

    private static string FindClaudeExecutable()
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var nativePath = Path.Combine(userProfile, ".local", "bin", "claude.exe");
        if (File.Exists(nativePath)) return nativePath;

        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.Combine(directory.Trim('"'), "claude.exe");
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
            {
                // Ignore malformed PATH entries and keep searching.
            }
        }

        throw new FileNotFoundException("Claude Code executable was not found.");
    }

    private static void TryKill(Process process)
    {
        try
        {
            if (!process.HasExited) process.Kill(entireProcessTree: true);
        }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception)
        {
            // The process already exited or Windows refused a best-effort cleanup.
        }
    }
}
