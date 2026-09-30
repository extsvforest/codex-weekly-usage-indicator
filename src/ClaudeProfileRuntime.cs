using System.ComponentModel;
using System.Diagnostics;
using System.Net.Mail;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Win32.SafeHandles;

namespace WeeklyUsageIndicator;

internal sealed record ClaudeProfileCommandResult(int ExitCode, string StandardOutput);
internal sealed class ClaudeLoginRequiredException() : UnauthorizedAccessException("Claude Code 로그인이 필요합니다.");
internal sealed class ClaudeIdentityChangedException() : UnauthorizedAccessException("저장된 Claude 계정과 다른 계정이 로그인되어 있습니다. 원래 계정으로 다시 로그인하세요.");
internal sealed record ClaudeProfileSharing(IReadOnlyList<string> SharedDirectories,
    bool PromptHistoryShared, bool InstructionsShared, bool InstructionsCopied);

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
    private static readonly string[] SharedDirectoryNames =
        ["projects", "skills", "agents", "commands", "memory", "agent-memory", "rules", "output-styles",
         "workflows", "hooks", "scripts", "file-history", "plans", "tasks", "paste-cache"];

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

    public ClaudeProfileSharing PrepareProfile(ClaudeProfile profile)
    {
        _store.ValidateProfile(profile);
        if (!Directory.Exists(_store.DefaultConfigDirectory))
            throw new DirectoryNotFoundException("기존 Claude 작업 폴더를 찾을 수 없습니다.");
        if (profile.IsDefault) return new ClaudeProfileSharing([], true, true, false);
        ClaudeProfileStore.CreatePrivateDirectory(_store.RootPath);
        ClaudeProfileStore.CreatePrivateDirectory(_store.ProfilesDirectory);
        ClaudeProfileStore.CreatePrivateDirectory(profile.ConfigDirectory);
        var shared = new List<string>();
        foreach (var name in SharedDirectoryNames)
        {
            var original = Path.Combine(_store.DefaultConfigDirectory, name);
            if (!Directory.Exists(original)) continue;
            var destination = Path.Combine(profile.ConfigDirectory, name);
            ShareDirectory(original, destination);
            shared.Add(name);
        }
        PrepareLocalPlugins(profile, shared);

        // Prompt recall is separate from projects/ (the conversation picker). It is optional on Windows
        // without developer-mode symlinks. Do not use hard links: a CLI rewrite can silently split them.
        var historyShared = TryShareFile(profile.ConfigDirectory, "history.jsonl", allowInitialCopy: false,
            out _);
        var instructionsShared = PrepareInstructions(profile.ConfigDirectory, out var instructionsCopied);
        return new ClaudeProfileSharing(shared, historyShared, instructionsShared, instructionsCopied);
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

    private bool TryShareFile(string profileDirectory, string name, bool allowInitialCopy, out bool copied)
    {
        copied = false;
        var original = Path.Combine(_store.DefaultConfigDirectory, name);
        var destination = Path.Combine(profileDirectory, name);
        if (!File.Exists(original)) return false;
        if (File.Exists(destination))
        {
            var linked = new FileInfo(destination).ResolveLinkTarget(returnFinalTarget: true);
            if (linked is not null)
            {
                var originalTarget = new FileInfo(original).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? original;
                if (!ClaudeProfileStore.PathsEqual(linked.FullName, originalTarget))
                    throw new IOException("Claude 프로필의 공유 파일이 다른 위치를 가리키고 있습니다.");
                return true;
            }
            return false;
        }
        try { File.CreateSymbolicLink(destination, original); return true; }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or PlatformNotSupportedException)
        {
            if (!allowInitialCopy) return false;
            // A one-time copy preserves instructions on systems without file symlinks. It never replaces
            // a later user edit. Directories holding conversations and skills remain live shared links.
            File.Copy(original, destination, overwrite: false);
            copied = true;
            return false;
        }
    }

    private void PrepareLocalPlugins(ClaudeProfile profile, List<string> shared)
    {
        var original = Path.Combine(_store.DefaultConfigDirectory, "plugins");
        if (!Directory.Exists(original)) return;
        var destination = Path.Combine(profile.ConfigDirectory, "plugins");
        ClaudeProfileStore.CreatePrivateDirectory(destination);
        foreach (var name in new[] { "cache", "marketplaces" })
        {
            if (!Directory.Exists(Path.Combine(original, name))) continue;
            ShareDirectory(Path.Combine(original, name), Path.Combine(destination, name));
            shared.Add("plugins/" + name);
        }
        foreach (var name in new[] { "installed_plugins.json", "known_marketplaces.json" })
        {
            var source = Path.Combine(original, name);
            var target = Path.Combine(destination, name);
            if (!File.Exists(source) || File.Exists(target)) continue;
            if (new FileInfo(source).Length > 1024 * 1024)
                throw new InvalidDataException("Claude 로컬 플러그인 목록이 너무 큽니다.");
            var bytes = SanitizePluginRegistry(name, File.ReadAllText(source, Encoding.UTF8), original);
            var temporary = Path.Combine(destination, ".registry-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                           4096, FileOptions.WriteThrough))
                { stream.Write(bytes); stream.Flush(flushToDisk: true); }
                // Compare-and-create: an official CLI or a user-owned registry always wins. These files
                // describe plugin installations only; credentials and account-synced plugins stay local.
                try { File.Move(temporary, target, overwrite: false); }
                catch (IOException) when (File.Exists(target)) { }
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
        }
    }

    internal static byte[] SanitizePluginRegistry(string fileName, string json, string originalPluginsDirectory)
    {
        try
        {
            var root = JsonNode.Parse(json) as JsonObject ?? throw new JsonException();
            var output = new JsonObject();
            if (fileName == "installed_plugins.json")
            {
                if (root["version"]?.GetValue<int>() != 2 || root["plugins"] is not JsonObject plugins)
                    throw new JsonException();
                var kept = new JsonObject();
                foreach (var pair in plugins)
                {
                    if (pair.Value is not JsonArray entries) throw new JsonException();
                    var retainedEntries = new JsonArray();
                    foreach (var item in entries)
                    {
                        if (item is not JsonObject entry || entry["installPath"]?.GetValue<string>() is not { } path)
                            throw new JsonException();
                        if (!Path.IsPathFullyQualified(path)) throw new JsonException();
                        if (IsAccountSyncedPlugin(path, originalPluginsDirectory)) continue;
                        var retained = new JsonObject();
                        foreach (var field in new[] { "scope", "projectPath", "installPath", "version", "installedAt",
                                     "lastUpdated", "gitCommitSha", "isLocal" })
                            CopyRegistryScalar(entry, retained, field, boolean: field == "isLocal");
                        retainedEntries.Add(retained);
                    }
                    if (retainedEntries.Count > 0) kept[pair.Key] = retainedEntries;
                }
                output["version"] = 2;
                output["plugins"] = kept;
            }
            else if (fileName == "known_marketplaces.json")
            {
                foreach (var pair in root)
                {
                    if (pair.Value is not JsonObject entry || entry["source"] is not JsonObject source
                        || entry["installLocation"]?.GetValue<string>() is not { } path
                        || !Path.IsPathFullyQualified(path)) throw new JsonException();
                    if (IsAccountSyncedPlugin(path, originalPluginsDirectory)) continue;
                    var kind = source["source"]?.GetValue<string>();
                    var retainedSource = new JsonObject { ["source"] = kind };
                    switch (kind)
                    {
                        case "github":
                            var repo = source["repo"]?.GetValue<string>() ?? throw new JsonException();
                            if (repo.Length > 200 || repo.Count(character => character == '/') != 1
                                || repo.Any(character => !char.IsAsciiLetterOrDigit(character)
                                    && character is not '/' and not '_' and not '-' and not '.')) throw new JsonException();
                            retainedSource["repo"] = repo;
                            CopyRegistryScalar(source, retainedSource, "ref");
                            break;
                        case "directory":
                            var directory = source["path"]?.GetValue<string>() ?? throw new JsonException();
                            if (!Path.IsPathFullyQualified(directory)) throw new JsonException();
                            retainedSource["path"] = directory;
                            break;
                        case "git":
                            var rawUrl = source["url"]?.GetValue<string>() ?? throw new JsonException();
                            if (!Uri.TryCreate(rawUrl, UriKind.Absolute, out var url)
                                || url.Scheme is not "https" and not "http"
                                || url.UserInfo.Length != 0 || url.Query.Length != 0 || url.Fragment.Length != 0)
                                throw new JsonException();
                            retainedSource["url"] = rawUrl;
                            CopyRegistryScalar(source, retainedSource, "ref");
                            break;
                        default:
                            throw new NotSupportedException("이 Claude 플러그인 등록 방식은 자동으로 공유할 수 없습니다. 기존 설정은 보존했습니다.");
                    }
                    var retained = new JsonObject { ["source"] = retainedSource, ["installLocation"] = path };
                    foreach (var field in new[] { "lastUpdated", "autoUpdate" })
                        CopyRegistryScalar(entry, retained, field, boolean: field == "autoUpdate");
                    output[pair.Key] = retained;
                }
            }
            else throw new ArgumentException("Unsupported plugin registry.", nameof(fileName));
            return JsonSerializer.SerializeToUtf8Bytes(output);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or FormatException)
        { throw new InvalidDataException("Claude 로컬 플러그인 목록을 읽을 수 없습니다."); }
    }

    private static void CopyRegistryScalar(JsonObject source, JsonObject destination, string field, bool boolean = false)
    {
        if (!source.TryGetPropertyValue(field, out var value) || value is null) return;
        if (boolean)
        {
            if (value.GetValueKind() is not JsonValueKind.True and not JsonValueKind.False) throw new JsonException();
        }
        else if (value.GetValueKind() != JsonValueKind.String || value.GetValue<string>().Length > 4096
                 || value.GetValue<string>().Any(char.IsControl)) throw new JsonException();
        destination[field] = value.DeepClone();
    }

    private static bool IsAccountSyncedPlugin(string path, string pluginsDirectory)
    {
        var synced = Path.GetFullPath(Path.Combine(pluginsDirectory, "synced"));
        return ClaudeProfileStore.PathsEqual(path, synced)
            || Path.GetFullPath(path).StartsWith(synced + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static void ShareDirectory(string original, string destination)
    {
        if (Directory.Exists(destination))
        {
            // An unexpected real directory may contain user data. Never replace or merge it silently.
            var target = new DirectoryInfo(destination).ResolveLinkTarget(returnFinalTarget: true);
            if (target is null || !ClaudeProfileStore.PathsEqual(target.FullName, ResolveDirectory(original)))
                throw new IOException("Claude 프로필에 별도 작업 자료가 있습니다. 기존 자료는 보존했습니다.");
        }
        else if (File.Exists(destination))
            throw new IOException("Claude 작업 폴더를 연결할 수 없습니다. 기존 파일은 보존했습니다.");
        else CreateDirectoryJunction(destination, original);
    }

    private bool PrepareInstructions(string profileDirectory, out bool copied)
    {
        copied = false;
        var original = Path.Combine(_store.DefaultConfigDirectory, "CLAUDE.md");
        var destination = Path.Combine(profileDirectory, "CLAUDE.md");
        if (!File.Exists(original)) return false;
        var importPath = original.Replace('\\', '/');
        // Official user-scope imports keep future edits in sync and need neither elevation nor a read
        // of the original text. Ambiguous import syntax falls back to an OS file link (or an initial copy).
        if (!importPath.Any(character => char.IsWhiteSpace(character) || character is '"' or '`' or '@'))
        {
            var import = "<!-- Shared user instructions; edit the original file. -->\n@" + importPath + "\n";
            if (!File.Exists(destination))
            {
                using var stream = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.Read);
                stream.Write(Encoding.UTF8.GetBytes(import));
                return true;
            }
            // This is our tiny generated reference, never the original instruction or a login file.
            if ((File.GetAttributes(destination) & FileAttributes.ReparsePoint) == 0
                && new FileInfo(destination).Length <= 8192 && File.ReadAllText(destination) == import) return true;
        }
        return TryShareFile(profileDirectory, "CLAUDE.md", allowInitialCopy: true, out copied);
    }

    private static string ResolveDirectory(string path) =>
        new DirectoryInfo(path).ResolveLinkTarget(returnFinalTarget: true)?.FullName ?? Path.GetFullPath(path);
    private static InvalidDataException InvalidStatus() => new("Claude Code의 계정 확인 응답을 읽을 수 없습니다.");

    internal static void CreateDirectoryJunction(string linkPath, string targetPath)
    {
        if (!OperatingSystem.IsWindows()) { Directory.CreateSymbolicLink(linkPath, targetPath); return; }
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(targetPath));
        if (target.StartsWith(@"\\", StringComparison.Ordinal))
            throw new IOException("Claude 작업 자료는 로컬 폴더에서만 공유할 수 있습니다.");
        var substitute = @"\??\" + target;
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
            if (handle.IsInvalid || !DeviceIoControl(handle, 0x000900A4, buffer, buffer.Length,
                    IntPtr.Zero, 0, out _, IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Claude 작업 폴더를 연결하지 못했습니다.");
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
