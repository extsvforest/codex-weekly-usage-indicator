using System.ComponentModel;
using System.Diagnostics;
using System.Net.Mail;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace WeeklyUsageIndicator;

internal sealed record ClaudeProfileCommandResult(int ExitCode, string StandardOutput);
internal sealed class ClaudeLoginRequiredException() : UnauthorizedAccessException("Claude Code 로그인이 필요합니다.");
internal sealed class ClaudeIdentityChangedException() : UnauthorizedAccessException("저장된 Claude 계정과 다른 계정이 로그인되어 있습니다. 원래 계정으로 다시 로그인하세요.");

internal interface IClaudeProfileCommandRunner
{
    Task<ClaudeProfileCommandResult> RunStatusAsync(ProcessStartInfo start, CancellationToken cancellationToken);
    Task<int> RunInteractiveAsync(ProcessStartInfo start, CancellationToken cancellationToken);
    Task<int> RunQuietAsync(ProcessStartInfo start, CancellationToken cancellationToken);
}

/// <summary>
/// Runs the unmodified official CLI for sign-in, identity checks and usage. Authentication files and token refresh
/// belong exclusively to Claude. The utility never starts an interactive session with a profile: such a writer would
/// be invisible to the Desktop app's own "in use" check.
/// </summary>
internal sealed class ClaudeProfileRuntime
{
    private readonly ClaudeProfileStore _store;
    private readonly IClaudeProfileCommandRunner _runner;
    private readonly string? _executablePath;
    // Folders that earlier builds linked into each utility profile from the original home, for the interactive
    // sessions they ran there (gone since 0568ef0). Print-mode /usage builds its "What's contributing" report from every
    // conversation under <CLAUDE_CONFIG_DIR>\projects, so the link made each check take about twenty seconds
    // (anthropics/claude-code#77018). Only these names are examined, and only a junction among them is removed.
    private static readonly string[] RetiredLinkNames =
        ["projects", "skills", "agents", "commands", "memory", "agent-memory", "rules", "output-styles",
         "workflows", "hooks", "scripts", "file-history", "plans", "tasks", "paste-cache",
         Path.Combine("plugins", "cache"), Path.Combine("plugins", "marketplaces")];
    private static volatile bool s_retirementHalted;

    public ClaudeProfileRuntime(ClaudeProfileStore store, IClaudeProfileCommandRunner? runner = null,
        string? executablePath = null)
    {
        _store = store;
        _runner = runner ?? new ClaudeProfileProcessRunner();
        _executablePath = executablePath;
    }

    public static string GetCliPath()
    {
        var nativePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".local", "bin", "claude.exe");
        if (File.Exists(nativePath)) return nativePath;
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
                     .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                var candidate = Path.GetFullPath(Path.Combine(directory.Trim('"'), "claude.exe"));
                if (File.Exists(candidate)) return candidate;
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException) { }
        }
        throw new FileNotFoundException("Claude Code 실행 파일을 찾을 수 없습니다.");
    }

    /// <summary>Makes a utility profile's private folder ready for the official login. A profile is only an official
    /// sign-in and the CLI's own state: nothing from the original home is linked or copied into it (settings are passed
    /// by path, see <see cref="AddSharedSettingsArguments"/>). Links an earlier build left there are retired first;
    /// returns their names.</summary>
    public IReadOnlyList<string> PrepareProfile(ClaudeProfile profile)
    {
        _store.ValidateProfile(profile);
        if (profile.IsDefault) return [];
        ClaudeProfileStore.CreatePrivateDirectory(_store.RootPath);
        ClaudeProfileStore.CreatePrivateDirectory(_store.ProfilesDirectory);
        ClaudeProfileStore.CreatePrivateDirectory(profile.ConfigDirectory);
        return RetireSharedLinks(profile);
    }

    /// <summary>Removes the folder junctions an earlier build made in a utility profile (<see cref="RetiredLinkNames"/>).
    /// Only a junction to a folder on a drive is removed, and only the link itself: <c>RemoveDirectory</c> on a junction
    /// never enters or empties its target, and nothing here deletes recursively. A real folder, a file, a symbolic link
    /// or a volume mount point stays as it is, and so does a link that cannot be removed now (it is tried again later).
    /// The folder a removed link pointed to must still be there afterwards; otherwise retirement stops for this
    /// process and an <see cref="IOException"/> says so. Returns the names removed.</summary>
    public IReadOnlyList<string> RetireSharedLinks(ClaudeProfile profile)
    {
        _store.ValidateProfile(profile);
        if (profile.IsDefault || profile.UseDefaultHome || s_retirementHalted) return [];
        var removed = new List<string>();
        foreach (var name in RetiredLinkNames)
        {
            var link = Path.Combine(profile.ConfigDirectory, name);
            string target;
            bool targetExisted;
            try
            {
                if (!TryReadJunctionTarget(link, out target)) continue;
                targetExisted = Directory.Exists(target);
                if (!RemoveDirectory(link)) continue;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or Win32Exception)
            { continue; }
            if (targetExisted && !Directory.Exists(target))
            {
                s_retirementHalted = true;
                throw new IOException("Claude 프로필의 옛 연결을 정리한 뒤 원래 작업 폴더가 보이지 않아 정리를 멈췄습니다.");
            }
            removed.Add(name);
        }
        return removed;
    }

    public async Task<ClaudeProfileIdentity> VerifyAsync(ClaudeProfile profile, CancellationToken cancellationToken,
        string? workingDirectory = null, bool safeMode = false)
    {
        _store.ValidateProfile(profile);
        var start = CreateStartInfo(profile, workingDirectory, visible: false, safeMode);
        foreach (var argument in new[] { "auth", "status", "--json" }) start.ArgumentList.Add(argument);
        var result = await _runner.RunStatusAsync(start, cancellationToken).ConfigureAwait(false);
        var identity = ParseStatus(result, profile);
        if (profile.Identity is not null && identity.Key != profile.Identity.Key)
            throw new ClaudeIdentityChangedException();
        return identity;
    }

    public async Task<ClaudeProfileIdentity> LoginAsync(ClaudeProfile profile, CancellationToken cancellationToken)
    {
        PrepareProfile(profile);
        var start = CreateStartInfo(profile, null, visible: true, safeMode: true);
        foreach (var argument in new[] { "auth", "login", "--claudeai" }) start.ArgumentList.Add(argument);
        var exitCode = await _runner.RunInteractiveAsync(start, cancellationToken).ConfigureAwait(false);
        if (exitCode != 0) throw new InvalidOperationException("Claude 로그인이 완료되지 않았습니다.");
        // The settings used by an ordinary managed session must resolve to this same subscription.
        // This also rejects a user setting that changes auth back to an API key or another provider.
        return await VerifyAsync(profile, cancellationToken).ConfigureAwait(false);
    }

    public async Task LogoutAsync(ClaudeProfile profile, CancellationToken cancellationToken)
    {
        _store.ValidateProfile(profile);
        var start = CreateStartInfo(profile, null, visible: false, safeMode: true);
        foreach (var argument in new[] { "auth", "logout" }) start.ArgumentList.Add(argument);
        if (await _runner.RunQuietAsync(start, cancellationToken).ConfigureAwait(false) != 0)
            throw new InvalidOperationException("Claude 로그아웃을 완료하지 못했습니다.");
        var status = CreateStartInfo(profile, null, visible: false, safeMode: true);
        foreach (var argument in new[] { "auth", "status", "--json" }) status.ArgumentList.Add(argument);
        var result = await _runner.RunStatusAsync(status, cancellationToken).ConfigureAwait(false);
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            if (!document.RootElement.TryGetProperty("loggedIn", out var loggedIn)
                || loggedIn.ValueKind != JsonValueKind.False)
                throw new InvalidOperationException("Claude 로그아웃 상태를 확인하지 못했습니다.");
        }
        catch (JsonException) { throw new InvalidOperationException("Claude 로그아웃 상태를 확인하지 못했습니다."); }
    }

    internal ProcessStartInfo CreateStartInfo(ClaudeProfile profile, string? workingDirectory,
        bool visible, bool safeMode)
    {
        _store.ValidateProfile(profile);
        var directory = Path.GetFullPath(workingDirectory ?? profile.ConfigDirectory);
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("Claude 작업 폴더를 찾을 수 없습니다.");
        var start = new ProcessStartInfo
        {
            FileName = _executablePath ?? GetCliPath(), UseShellExecute = false,
            CreateNoWindow = !visible, WindowStyle = visible ? ProcessWindowStyle.Normal : ProcessWindowStyle.Hidden,
            WorkingDirectory = directory
        };
        ApplyProfileEnvironment(start, profile);
        if (safeMode) start.ArgumentList.Add("--safe-mode");
        else AddSharedSettingsArguments(start, profile);
        return start;
    }

    public void AddSharedSettingsArguments(ProcessStartInfo start, ClaudeProfile profile)
    {
        _store.ValidateProfile(profile);
        var settingsPath = Path.Combine(_store.DefaultConfigDirectory, "settings.json");
        if (!profile.IsDefault && File.Exists(settingsPath))
        {
            // Pass the path to Claude. The utility does not read or duplicate settings, including env secrets.
            start.ArgumentList.Add("--settings");
            start.ArgumentList.Add(settingsPath);
        }
    }

    public static void ApplyProfileEnvironment(ProcessStartInfo start, ClaudeProfile profile)
    {
        // Strip by key only. Never inspect the values of an inherited credential or routing override.
        foreach (var name in start.Environment.Keys.ToArray())
        {
            if (name.StartsWith("ANTHROPIC_", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("CLAUDE_CODE_OAUTH", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("CLAUDE_CODE_USE_", StringComparison.OrdinalIgnoreCase)
                || name.Equals("CLAUDE_CONFIG_DIR", StringComparison.OrdinalIgnoreCase)
                || name.Equals("CLAUDE_CODE_API_KEY_HELPER_TTL_MS", StringComparison.OrdinalIgnoreCase)
                || name.Equals("CLAUDE_CODE_SESSION_ACCESS_TOKEN", StringComparison.OrdinalIgnoreCase)
                || name.Equals("CLAUDE_CODE_WEBSOCKET_AUTH_FILE_DESCRIPTOR", StringComparison.OrdinalIgnoreCase)
                || name.Equals("CLAUDE_CODE_API_KEY_FILE_DESCRIPTOR", StringComparison.OrdinalIgnoreCase)
                || name.Equals("CLAUDE_CODE_OAUTH_TOKEN_FILE_DESCRIPTOR", StringComparison.OrdinalIgnoreCase)
                || name.Equals("CLAUDE_CODE_ENTRYPOINT", StringComparison.OrdinalIgnoreCase)
                || name.Equals("CLAUDE_CODE_SIMPLE", StringComparison.OrdinalIgnoreCase)
                || name.Equals("CLAUDE_CODE_SAFE_MODE", StringComparison.OrdinalIgnoreCase)
                || name.Equals("CLAUDECODE", StringComparison.OrdinalIgnoreCase))
                start.Environment.Remove(name);
        }
        if (!profile.UseDefaultHome) start.Environment["CLAUDE_CONFIG_DIR"] = Path.GetFullPath(profile.ConfigDirectory);
        // Recent official CLIs classify /usage as nonessential traffic. The blanket
        // switch can therefore block the read this utility was started to perform.
        // Disable background reporting and updates individually instead; preserve
        // any blanket restriction explicitly inherited from the user's environment.
        start.Environment["DISABLE_TELEMETRY"] = "1";
        start.Environment["DISABLE_ERROR_REPORTING"] = "1";
        start.Environment["DISABLE_AUTOUPDATER"] = "1";
    }

    internal static ClaudeProfileIdentity ParseStatus(ClaudeProfileCommandResult result, ClaudeProfile profile)
    {
        if (result.StandardOutput.Length > 64 * 1024) throw InvalidStatus();
        try
        {
            using var document = JsonDocument.Parse(result.StandardOutput);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) throw InvalidStatus();
            if (!root.TryGetProperty("loggedIn", out var loggedIn)
                || loggedIn.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw InvalidStatus();
            if (loggedIn.ValueKind == JsonValueKind.False) throw new ClaudeLoginRequiredException();
            if (result.ExitCode != 0) throw InvalidStatus();
            if (ReadSafeString(root, "authMethod", 32) != "claude.ai"
                || ReadSafeString(root, "apiProvider", 32) is { } provider && provider != "firstParty")
                throw new UnauthorizedAccessException("이 프로필은 Claude 구독 로그인을 사용하지 않습니다. API 키나 외부 제공자 설정을 확인하세요.");
            if (ReadSafeString(root, "configDirectory", 4096) is { } configured
                && !ClaudeProfileStore.PathsEqual(configured, profile.ConfigDirectory)) throw InvalidStatus();
            var email = ReadSafeString(root, "email", 320);
            if (string.IsNullOrWhiteSpace(email) || !MailAddress.TryCreate(email, out var address)
                || !address.Address.Equals(email, StringComparison.OrdinalIgnoreCase)) throw InvalidStatus();
            var organization = ReadSafeString(root, "orgId", 128) ?? string.Empty;
            if (organization.Length > 0 && !Guid.TryParse(organization, out _)) throw InvalidStatus();
            var key = IdentityKey(email, organization);
            var first = char.IsLetterOrDigit(email[0]) ? email[0].ToString() : "*";
            var subscription = ReadSafeString(root, "subscriptionType", 32);
            if (subscription is not null && subscription.Any(character => !char.IsLetterOrDigit(character)
                    && character is not '_' and not '-')) subscription = null;
            return new ClaudeProfileIdentity(key, first + "*** · " + key[..8], subscription);
        }
        catch (JsonException) { throw InvalidStatus(); }
    }

    /// <summary>The persisted identity digest. Shared with the session-shelf account mapping.</summary>
    internal static string IdentityKey(string email, string organization) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            email.Trim().ToLowerInvariant() + "\n" + organization.ToLowerInvariant())));

    private static string? ReadSafeString(JsonElement element, string property, int maxLength)
    {
        if (!element.TryGetProperty(property, out var value) || value.ValueKind == JsonValueKind.Null) return null;
        if (value.ValueKind != JsonValueKind.String) throw InvalidStatus();
        var text = value.GetString()!;
        if (text.Length > maxLength || text.Any(char.IsControl)) throw InvalidStatus();
        return text;
    }

    private static InvalidDataException InvalidStatus() => new("Claude Code의 계정 확인 응답을 읽을 수 없습니다.");

    /// <summary>True when <paramref name="path"/> itself is a directory junction to a folder on a drive: a mount-point
    /// reparse point whose substitute name is <c>\??\X:\…</c>. A symbolic link, a volume mount point
    /// (<c>\??\Volume{…}</c>) or anything else is not. The link is read, never followed.</summary>
    internal static bool TryReadJunctionTarget(string path, out string target)
    {
        const FileAttributes junctionLike = FileAttributes.Directory | FileAttributes.ReparsePoint;
        target = string.Empty;
        if (!OperatingSystem.IsWindows()) return false;
        var attributes = new FileInfo(path).Attributes; // the entry's own attributes; -1 when it is absent
        if ((int)attributes == -1 || (attributes & junctionLike) != junctionLike) return false;
        // Reading reparse data needs no access rights. Open the link itself, not what it points to.
        using var handle = CreateFile(path, 0, 7, IntPtr.Zero, 3, 0x02200000, IntPtr.Zero);
        if (handle.IsInvalid) throw new Win32Exception(Marshal.GetLastWin32Error());
        var buffer = new byte[16 * 1024];
        if (!DeviceIoControl(handle, 0x000900A8, IntPtr.Zero, 0, buffer, buffer.Length, out var length, IntPtr.Zero))
            throw new Win32Exception(Marshal.GetLastWin32Error());
        if (length < 16 || BitConverter.ToUInt32(buffer, 0) != 0xA0000003u) return false;
        int offset = BitConverter.ToUInt16(buffer, 8), size = BitConverter.ToUInt16(buffer, 10);
        if (size == 0 || 16 + offset + size > length) return false;
        var substitute = Encoding.Unicode.GetString(buffer, 16 + offset, size);
        if (!substitute.StartsWith(@"\??\", StringComparison.Ordinal)) return false;
        var folder = substitute[4..];
        if (folder.Length < 3 || !char.IsAsciiLetter(folder[0]) || folder[1] != ':' || folder[2] != '\\') return false;
        target = folder;
        return true;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFile(string fileName, uint access, uint share, IntPtr security,
        uint creation, uint flags, IntPtr template);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeviceIoControl(SafeFileHandle handle, uint controlCode, IntPtr input,
        int inputLength, byte[] output, int outputLength, out int bytesReturned, IntPtr overlapped);
    // Removes an empty folder or a junction itself; it neither recurses nor follows the link.
    [DllImport("kernel32.dll", EntryPoint = "RemoveDirectoryW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveDirectory(string path);
}

internal sealed class ClaudeProfileProcessRunner : IClaudeProfileCommandRunner
{
    public async Task<ClaudeProfileCommandResult> RunStatusAsync(ProcessStartInfo start,
        CancellationToken cancellationToken)
    {
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = true;
        start.StandardOutputEncoding = Encoding.UTF8;
        start.StandardErrorEncoding = Encoding.UTF8;
        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (!process.Start()) throw new IOException("Claude Code를 시작하지 못했습니다.");
            process.StandardInput.Close();
            var output = ReadBoundedAsync(process.StandardOutput, capture: true, timeout.Token);
            var errors = ReadBoundedAsync(process.StandardError, capture: false, timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, errors).ConfigureAwait(false);
            return new ClaudeProfileCommandResult(process.ExitCode, await output.ConfigureAwait(false));
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Claude 계정 확인 시간이 초과되었습니다."); }
        finally { TryKillOwned(process); }
    }

    public async Task<int> RunInteractiveAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        // Login URLs and one-time codes go directly between the user and the official terminal.
        using var process = new Process { StartInfo = start };
        try
        {
            if (!process.Start()) throw new IOException("Claude Code를 시작하지 못했습니다.");
            await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
            return process.ExitCode;
        }
        finally { TryKillOwned(process); }
    }

    public async Task<int> RunQuietAsync(ProcessStartInfo start, CancellationToken cancellationToken)
    {
        start.RedirectStandardOutput = true;
        start.RedirectStandardError = true;
        start.RedirectStandardInput = true;
        using var process = new Process { StartInfo = start };
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            if (!process.Start()) throw new IOException("Claude Code를 시작하지 못했습니다.");
            process.StandardInput.Close();
            var output = ReadBoundedAsync(process.StandardOutput, capture: false, timeout.Token);
            var errors = ReadBoundedAsync(process.StandardError, capture: false, timeout.Token);
            await Task.WhenAll(process.WaitForExitAsync(timeout.Token), output, errors).ConfigureAwait(false);
            return process.ExitCode;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { throw new TimeoutException("Claude 명령 실행 시간이 초과되었습니다."); }
        finally { TryKillOwned(process); }
    }

    private static async Task<string> ReadBoundedAsync(StreamReader reader, bool capture,
        CancellationToken cancellationToken)
    {
        var buffer = new char[2048];
        var text = capture ? new StringBuilder() : null;
        var readTotal = 0;
        while (true)
        {
            var count = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (count == 0) return text?.ToString() ?? string.Empty;
            readTotal += count;
            if (readTotal > 64 * 1024) throw new InvalidDataException("Claude 명령 응답이 너무 큽니다.");
            text?.Append(buffer, 0, count);
        }
    }

    private static void TryKillOwned(Process process)
    {
        try { if (!process.HasExited) process.Kill(entireProcessTree: false); }
        catch (Exception exception) when (exception is InvalidOperationException or Win32Exception) { }
    }
}
