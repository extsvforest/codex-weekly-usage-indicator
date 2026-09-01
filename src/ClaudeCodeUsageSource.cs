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
                throw new UnauthorizedAccessException("Claude Code login is required.");
            }

            throw new IOException($"Claude Code /usage exited with code {result.ExitCode}.");
        }

        return ParseEnvelope(result.StandardOutput, _timeProvider.GetUtcNow());
    }

    internal static ClaudeUsageSnapshot ParseEnvelope(string json, DateTimeOffset now)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
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
                throw new UnauthorizedAccessException("Claude Code login is required.");
            throw new IOException("Claude Code could not retrieve /usage.");
        }

        // /usage is a local built-in command. Refuse any response that consumed a model turn or money.
        if (numTurns != 0 || Math.Abs(totalCost) > double.Epsilon)
            throw new InvalidDataException("Claude Code /usage unexpectedly consumed a model turn.");

        return ParseUsageText(resultText, now);
    }

    internal static ClaudeUsageSnapshot ParseUsageText(string text, DateTimeOffset now)
    {
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

        if (fiveHour is null || weekly is null || fable is null)
            throw new InvalidDataException("Claude Code /usage did not return all expected limits.");

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
            throw new InvalidDataException($"Claude Code returned a malformed {label} limit.");

        if (!double.TryParse(
                match.Groups["percent"].Value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var usedPercent) ||
            !double.IsFinite(usedPercent) ||
            usedPercent is < 0d or > 100d)
        {
            throw new InvalidDataException($"Claude Code returned an invalid {label} percentage.");
        }

        var resetsAt = ParseResetTime(
            match.Groups["reset"].Value,
            match.Groups["timezone"].Value,
            now);
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
        @"^(?<label>Current (?:session|week \(all models\)|week \(Fable\))):\s*(?<percent>\d+(?:\.\d+)?)%\s+used\s+·\s+resets\s+(?<reset>.+?)\s+\((?<timezone>[^)]+)\)\s*$",
        RegexOptions.CultureInvariant);
}

internal sealed class ClaudeCodeProcessRunner : IClaudeCommandRunner
{
    internal static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(20);
    private const int MaximumOutputCharacters = 128 * 1024;

    public async Task<ClaudeCommandResult> RunUsageAsync(CancellationToken cancellationToken)
    {
        var executablePath = FindClaudeExecutable();
        var startInfo = CreateStartInfo(executablePath);
        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start()) throw new IOException("Claude Code could not be started.");
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

    internal static ProcessStartInfo CreateStartInfo(string executablePath)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            UseShellExecute = false,
            CreateNoWindow = true,
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
            "--max-turns",
            "0"
        })
        {
            startInfo.ArgumentList.Add(argument);
        }
        return startInfo;
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
