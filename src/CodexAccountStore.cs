using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WeeklyUsageIndicator;

internal sealed record AccountIdentity(string Key, string Hint);
internal sealed record SavedCodexAccount(string Id, string Label, string IdentityHint, bool IsActive,
    UsageSnapshot? Usage, DateTimeOffset? ObservedAt);

/// <summary>
/// Opt-in, local-only account storage. Live auth.json is authoritative for the active account.
/// This class never refreshes a token, starts Codex, changes config, or reads Claude credentials.
/// </summary>
internal sealed class CodexAccountStore
{
    internal const string TransactionMutexName = @"Local\CodexWeeklyUsageIndicator.AccountTransaction";
    private const int MaxAuthBytes = 1024 * 1024;
    private const int MaxStoreBytes = 16 * 1024 * 1024;
    private const int MaxAccounts = 20;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("WeeklyUsageIndicator.Accounts.v1");
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = false };
    private readonly string _vaultPath;
    private readonly string _journalPath;
    private string AuthPath => Path.Combine(CodexHome, "auth.json");
    private string AuthTempPath => Path.Combine(CodexHome, ".gfs-account-auth.tmp");
    public string RootPath { get; }
    public string CodexHome { get; }
    public bool IsEnabled => File.Exists(_vaultPath);
    public bool HasPendingRecovery => File.Exists(_journalPath);

    // Internal deterministic crash seam. Production never supplies a callback.
    internal Action<string>? Checkpoint { get; set; }

    public CodexAccountStore(string? root = null, string? codexHome = null)
    {
        RootPath = Path.GetFullPath(root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexWeeklyUsageIndicator.Accounts"));
        var configuredHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        CodexHome = Path.GetFullPath(codexHome ?? (string.IsNullOrWhiteSpace(configuredHome)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".codex")
            : configuredHome));
        _vaultPath = Path.Combine(RootPath, "accounts.dpapi");
        _journalPath = Path.Combine(RootPath, "switch.dpapi");
    }

    public AccountIdentity GetCurrentIdentity()
    {
        CheckSupportedStore();
        return ParseIdentity(ReadBounded(AuthPath, MaxAuthBytes));
    }

    public IReadOnlyList<SavedCodexAccount> ListAccounts()
    {
        if (!IsEnabled && !HasPendingRecovery) return Array.Empty<SavedCodexAccount>();
        using var gate = AcquireLock();
        var vault = LoadVault();
        // Missing/logged-out auth is shown as no active account; invalid auth must remain visible as an error.
        var key = File.Exists(AuthPath) ? GetCurrentIdentity().Key : null;
        return vault.Accounts.Select(a => new SavedCodexAccount(a.Id, a.Label, a.Hint,
            a.Key == key, a.Usage, a.ObservedAt)).ToArray();
    }

    public SavedCodexAccount RegisterCurrent(string label)
    {
        CheckSupportedStore();
        using var gate = AcquireLock();
        RequireNoRecovery();
        var bytes = ReadBounded(AuthPath, MaxAuthBytes);
        var identity = ParseIdentity(bytes);
        var vault = LoadVault();
        var entry = Upsert(vault, bytes, identity, label);
        WriteEncrypted(_vaultPath, vault);
        return PublicEntry(entry, true);
    }

    public SavedCodexAccount ImportLoginFile(string path, string label)
    {
        CheckSupportedStore();
        using var gate = AcquireLock();
        RequireNoRecovery();
        var bytes = ReadBounded(Path.GetFullPath(path), MaxAuthBytes);
        var identity = ParseIdentity(bytes);
        var current = File.Exists(AuthPath) ? GetCurrentIdentity() : null;
        // Never replace a live account's newer credentials with an isolated login's snapshot.
        if (current?.Key == identity.Key)
            throw new InvalidOperationException("현재 사용 중인 계정입니다. 현재 계정 등록을 사용하세요.");
        var vault = LoadVault();
        var entry = Upsert(vault, bytes, identity, label);
        WriteEncrypted(_vaultPath, vault);
        return PublicEntry(entry, false);
    }

    public void SwitchTo(string id, Action assertStopped)
    {
        ArgumentNullException.ThrowIfNull(assertStopped);
        CheckSupportedStore();
        using var gate = AcquireLock();
        RequireNoRecovery();
        assertStopped();
        var beforeAuth = ReadBounded(AuthPath, MaxAuthBytes);
        var source = ParseIdentity(beforeAuth);
        var before = LoadVault();
        var target = before.Accounts.SingleOrDefault(a => a.Id == id)
            ?? throw new InvalidOperationException("저장된 계정을 찾을 수 없습니다.");
        if (source.Key == target.Key) return;
        if (!before.Accounts.Any(a => a.Key == source.Key))
            throw new InvalidOperationException("현재 로그인한 계정을 먼저 등록하세요.");
        var targetIdentity = ParseIdentity(target.Auth);
        if (targetIdentity.Key != target.Key) throw CorruptStore();
        var after = Clone(before);
        UpdateAuth(after, source.Key, beforeAuth);
        var transaction = new SwitchJournal(1, source.Key, target.Key, beforeAuth,
            target.Auth, Digest(beforeAuth), Digest(target.Auth), before, after);
        // Journal is durable before either vault or active auth changes. No plaintext backups are made.
        WriteEncrypted(_journalPath, transaction);
        Checkpoint?.Invoke("journal-written");
        WriteEncrypted(_vaultPath, after);
        Checkpoint?.Invoke("source-saved");
        assertStopped();
        CheckSupportedStore();
        RequireSameAuth(beforeAuth);
        WriteAuth(target.Auth);
        Checkpoint?.Invoke("auth-replaced");
        var actual = ReadBounded(AuthPath, MaxAuthBytes);
        if (ParseIdentity(actual).Key != target.Key)
            throw new InvalidOperationException("교체 중 다른 로그인이 발견되어 복구 기록을 보존했습니다.");
        UpdateAuth(after, target.Key, actual);
        WriteEncrypted(_vaultPath, after);
        Checkpoint?.Invoke("vault-committed");
        DeleteChecked(_journalPath);
    }

    public void Recover(Action assertStopped)
    {
        ArgumentNullException.ThrowIfNull(assertStopped);
        CheckSupportedStore();
        using var gate = AcquireLock();
        if (!HasPendingRecovery) return;
        assertStopped();
        var journal = ReadEncrypted<SwitchJournal>(_journalPath);
        ValidateJournal(journal);
        if (!File.Exists(AuthPath))
            throw new InvalidOperationException("로그인 파일이 없어 자동 복구를 멈췄습니다. 원래 계정으로 로그인한 뒤 복구하세요.");
        var live = ReadBounded(AuthPath, MaxAuthBytes);
        var identity = ParseIdentity(live);
        Vault result;
        if (identity.Key == journal.SourceKey)
            result = Clone(journal.Before);
        else if (identity.Key == journal.TargetKey)
            result = Clone(journal.After);
        else
            throw new InvalidOperationException("교체 대상과 다른 계정이 로그인되어 있습니다. 현재 로그인은 보존했고 자동 복구를 멈췄습니다.");
        // Identity chooses rollback/complete; changed digest means the live token rotated and MUST win.
        UpdateAuth(result, identity.Key, live);
        assertStopped();
        RequireSameAuth(live);
        WriteEncrypted(_vaultPath, result);
        Checkpoint?.Invoke("recovery-saved");
        DeleteChecked(AuthTempPath);
        DeleteChecked(_journalPath);
    }

    public void Rename(string id, string label)
    {
        using var gate = AcquireLock();
        RequireNoRecovery();
        label = NormalizeLabel(label);
        var vault = LoadVault();
        var entry = vault.Accounts.SingleOrDefault(a => a.Id == id)
            ?? throw new InvalidOperationException("저장된 계정을 찾을 수 없습니다.");
        // Metadata-only: do not inspect or replace live auth, even for the active account.
        if (entry.Label == label) return;
        entry.Label = label;
        WriteEncrypted(_vaultPath, vault);
    }

    public void Remove(string id)
    {
        using var gate = AcquireLock();
        RequireNoRecovery();
        var current = File.Exists(AuthPath) ? GetCurrentIdentity().Key : null;
        var vault = LoadVault();
        var entry = vault.Accounts.SingleOrDefault(a => a.Id == id)
            ?? throw new InvalidOperationException("저장된 계정을 찾을 수 없습니다.");
        if (entry.Key == current) throw new InvalidOperationException("현재 사용 중인 계정은 삭제할 수 없습니다.");
        vault.Accounts.Remove(entry);
        WriteEncrypted(_vaultPath, vault);
    }

    public void SaveUsage(string identityKey, UsageSnapshot snapshot)
    {
        if (!IsEnabled || HasPendingRecovery) return;
        using var gate = AcquireLock();
        RequireNoRecovery();
        if (GetCurrentIdentity().Key != identityKey) return;
        var vault = LoadVault();
        var entry = vault.Accounts.SingleOrDefault(a => a.Key == identityKey);
        if (entry is null) return;
        if (snapshot.UsedPercent is < 0 or > 100 || snapshot.LimitId.Length > 200)
            throw new InvalidOperationException("사용량 값이 올바르지 않습니다.");
        entry.Usage = snapshot;
        entry.ObservedAt = DateTimeOffset.UtcNow;
        WriteEncrypted(_vaultPath, vault);
    }

    private static SavedCodexAccount PublicEntry(Entry entry, bool active) =>
        new(entry.Id, entry.Label, entry.Hint, active, entry.Usage, entry.ObservedAt);

    private static Entry Upsert(Vault vault, byte[] bytes, AccountIdentity identity, string label)
    {
        label = NormalizeLabel(label);
        var entry = vault.Accounts.SingleOrDefault(a => a.Key == identity.Key);
        if (entry is null)
        {
            if (vault.Accounts.Count >= MaxAccounts) throw new InvalidOperationException("최대 20개 계정까지 저장할 수 있습니다.");
            entry = new Entry { Id = Guid.NewGuid().ToString("N"), Key = identity.Key };
            vault.Accounts.Add(entry);
        }
        entry.Label = label;
        entry.Hint = identity.Hint;
        entry.Auth = bytes;
        return entry;
    }

    private static string NormalizeLabel(string label)
    {
        label = label?.Trim() ?? "";
        if (label.Length is < 1 or > 40 || label.Any(char.IsControl))
            throw new InvalidOperationException("계정 이름은 제어 문자 없이 1~40자로 입력하세요.");
        return label;
    }

    private static void UpdateAuth(Vault vault, string key, byte[] bytes)
    {
        var entry = vault.Accounts.SingleOrDefault(a => a.Key == key) ?? throw CorruptStore();
        entry.Auth = bytes;
    }

    private Vault LoadVault()
    {
        if (!File.Exists(_vaultPath)) return new Vault();
        var result = ReadEncrypted<Vault>(_vaultPath);
        ValidateVault(result);
        return result;
    }

    private static void ValidateVault(Vault vault)
    {
        if (vault.Version != 1 || vault.Accounts is null || vault.Accounts.Count > MaxAccounts ||
            vault.Accounts.Select(a => a.Id).Distinct().Count() != vault.Accounts.Count ||
            vault.Accounts.Select(a => a.Key).Distinct().Count() != vault.Accounts.Count)
            throw CorruptStore();
        foreach (var entry in vault.Accounts)
        {
            if (!Guid.TryParseExact(entry.Id, "N", out _) || string.IsNullOrWhiteSpace(entry.Label) ||
                entry.Label.Length > 40 || entry.Label.Any(char.IsControl) || entry.Auth is null ||
                ParseIdentity(entry.Auth).Key != entry.Key)
                throw CorruptStore();
        }
    }

    private static void ValidateJournal(SwitchJournal journal)
    {
        if (journal.Version != 1 || journal.SourceKey == journal.TargetKey ||
            ParseIdentity(journal.BeforeAuth).Key != journal.SourceKey ||
            ParseIdentity(journal.AfterAuth).Key != journal.TargetKey ||
            Digest(journal.BeforeAuth) != journal.BeforeDigest || Digest(journal.AfterAuth) != journal.AfterDigest)
            throw CorruptStore();
        ValidateVault(journal.Before);
        ValidateVault(journal.After);
        if (!journal.Before.Accounts.Any(a => a.Key == journal.SourceKey) ||
            !journal.After.Accounts.Any(a => a.Key == journal.TargetKey)) throw CorruptStore();
    }

    private static Vault Clone(Vault vault) => JsonSerializer.Deserialize<Vault>(
        JsonSerializer.SerializeToUtf8Bytes(vault, JsonOptions), JsonOptions) ?? throw CorruptStore();

    private void RequireNoRecovery()
    {
        if (HasPendingRecovery) throw new InvalidOperationException("미완료 계정 교체를 먼저 복구하세요.");
    }

    private void RequireSameAuth(byte[] expected)
    {
        if (!CryptographicOperations.FixedTimeEquals(expected, ReadBounded(AuthPath, MaxAuthBytes)))
            throw new InvalidOperationException("로그인 정보가 작업 중 변경되어 교체를 멈췄습니다. 복구 후 다시 시도하세요.");
    }

    private void CheckSupportedStore()
    {
        // Machine requirements can override the apparent user config and route to a shared keyring.
        // Support only the personal unmanaged file-store case; never rewrite a managed setting.
        var commonData = Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData);
        foreach (var name in new[] { "requirements.toml", "config.toml" })
        {
            var path = Path.Combine(commonData, "OpenAI", "Codex", name);
            try
            {
                _ = File.GetAttributes(path);
                throw new InvalidOperationException("관리형 Codex 설정이 있어 계정 교체를 지원하지 않습니다. 설정은 변경하지 않았습니다.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch (UnauthorizedAccessException)
            { throw new InvalidOperationException("Codex 관리 설정을 확인할 수 없어 계정 교체를 중단했습니다."); }
        }
        RejectReparsePath(CodexHome);
        if (!Directory.Exists(CodexHome)) throw new InvalidOperationException("Codex 사용자 폴더를 찾을 수 없습니다.");
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("CODEX_API_KEY")))
            throw new InvalidOperationException("CODEX_API_KEY가 설정된 환경에서는 계정 교체를 지원하지 않습니다.");
        var config = Path.Combine(CodexHome, "config.toml");
        if (!File.Exists(config)) return;
        var text = Encoding.UTF8.GetString(ReadBounded(config, MaxAuthBytes));
        // Be deliberately conservative about TOML syntax: only an unambiguous file store is supported.
        foreach (var rawLine in text.Split('\n'))
        {
            var line = rawLine.Trim();
            if (line.StartsWith('#')) continue;
            if (line.Contains("cli_auth_credentials_store", StringComparison.Ordinal) &&
                !Regex.IsMatch(line, "^cli_auth_credentials_store\\s*=\\s*([\\\"'])file\\1\\s*(#.*)?$"))
                throw new InvalidOperationException("파일 방식 이외의 인증 저장 설정은 지원하지 않습니다. 설정은 변경하지 않았습니다.");
            if (Regex.IsMatch(line, "^[\\\"']?(forced_chatgpt_workspace_id|forced_login_method)[\\\"']?\\s*="))
                throw new InvalidOperationException("로그인 또는 워크스페이스 제한이 있는 환경은 자동 교체를 지원하지 않습니다.");
        }
    }

    internal static AccountIdentity ParseIdentity(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaxAuthBytes) throw InvalidAuth();
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            RejectDuplicateProperties(root);
            if (root.TryGetProperty("auth_mode", out var mode) && mode.ValueKind != JsonValueKind.Null &&
                (mode.ValueKind != JsonValueKind.String || mode.GetString() != "chatgpt")) throw InvalidAuth();
            // Codex's managed ChatGPT legacy format omits auth_mode (or uses null). Accept it only
            // when token data is complete and there is no competing credential mode to infer.
            foreach (var field in new[] { "OPENAI_API_KEY", "personal_access_token", "agent_identity", "bedrock_api_key", "bedrock_access_keys" })
                if (root.TryGetProperty(field, out var credential) && credential.ValueKind != JsonValueKind.Null)
                    throw InvalidAuth();
            if (!root.TryGetProperty("tokens", out var tokens) || tokens.ValueKind != JsonValueKind.Object)
                throw InvalidAuth();
            var idToken = Required(tokens, "id_token");
            _ = Required(tokens, "access_token");
            _ = Required(tokens, "refresh_token");
            var account = Required(tokens, "account_id");
            var pieces = idToken.Split('.');
            if (pieces.Length != 3 || pieces[1].Length > MaxAuthBytes) throw InvalidAuth();
            var payload = pieces[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var claimsDocument = JsonDocument.Parse(Convert.FromBase64String(payload));
            var claims = claimsDocument.RootElement;
            RejectDuplicateProperties(claims);
            var auth = claims.TryGetProperty("https://api.openai.com/auth", out var namespaced)
                ? namespaced : default;
            var claimAccount = StringValue(auth, "chatgpt_account_id");
            if (claimAccount is not null && claimAccount != account) throw InvalidAuth();
            var user = StringValue(auth, "chatgpt_user_id") ?? StringValue(auth, "user_id") ?? StringValue(claims, "sub");
            if (string.IsNullOrWhiteSpace(user) || user.Length > 512 || account.Length > 512) throw InvalidAuth();
            var key = Digest(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new[] { user, account })));
            var email = StringValue(claims, "email");
            var hint = string.IsNullOrEmpty(email) || email.Any(char.IsControl)
                ? "계정 · " + key[..8]
                : email[..1] + "*** · " + key[..8];
            return new AccountIdentity(key, hint);
        }
        catch (Exception ex) when (ex is JsonException or FormatException or InvalidOperationException or ArgumentException)
        {
            throw InvalidAuth();
        }
    }

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw InvalidAuth();
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
            foreach (var item in element.EnumerateArray()) RejectDuplicateProperties(item);
    }

    private static string? StringValue(JsonElement element, string property) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.String ? value.GetString() : null;
    private static string Required(JsonElement element, string property) =>
        StringValue(element, property) is { Length: > 0 } value && !string.IsNullOrWhiteSpace(value)
            ? value : throw InvalidAuth();
    private static string Digest(byte[] bytes) => Convert.ToHexString(SHA256.HashData(bytes));
    private static InvalidOperationException InvalidAuth() =>
        new("지원되는 ChatGPT 로그인 파일이 아닙니다. 공식 Codex 로그인으로 다시 등록하세요.");
    private static InvalidOperationException CorruptStore() =>
        new("계정 보관함 또는 복구 기록을 읽을 수 없습니다. 원본 파일을 보존한 채 중단했습니다.");

    private IDisposable AcquireLock()
    {
        var mutex = new Mutex(false, TransactionMutexName);
        bool acquired;
        try { acquired = mutex.WaitOne(0); }
        catch (AbandonedMutexException) { acquired = true; }
        if (!acquired)
        {
            mutex.Dispose();
            throw new InvalidOperationException("다른 계정 관리 작업이 진행 중입니다. 잠시 후 다시 시도하세요.");
        }
        try
        {
        RejectReparsePath(RootPath);
        var directory = new DirectoryInfo(RootPath);
        if (!directory.Exists) FileSystemAclExtensions.Create(directory, PrivateDirectorySecurity());
        FileSystemAclExtensions.SetAccessControl(directory, PrivateDirectorySecurity());
        var lockPath = Path.Combine(RootPath, "store.lock");
        RejectReparsePath(lockPath);
        try
        {
            if (!File.Exists(lockPath))
            {
                using var created = CreatePrivateFile(lockPath);
            }
            FileSystemAclExtensions.SetAccessControl(new FileInfo(lockPath), PrivateFileSecurity());
            return new StoreLock(mutex, new FileStream(lockPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        }
        catch (IOException)
        {
            throw new InvalidOperationException("다른 계정 관리 작업이 진행 중입니다. 잠시 후 다시 시도하세요.");
        }
        }
        catch
        {
            mutex.ReleaseMutex();
            mutex.Dispose();
            throw;
        }
    }

    private sealed class StoreLock(Mutex mutex, FileStream stream) : IDisposable
    {
        public void Dispose()
        {
            stream.Dispose();
            mutex.ReleaseMutex();
            mutex.Dispose();
        }
    }

    private static SecurityIdentifier CurrentSid => WindowsIdentity.GetCurrent().User
        ?? throw new InvalidOperationException("Windows 사용자 SID를 확인할 수 없습니다.");
    private static FileSecurity PrivateFileSecurity()
    {
        var security = new FileSecurity();
        security.SetOwner(CurrentSid);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentSid, FileSystemRights.FullControl, AccessControlType.Allow));
        return security;
    }
    private static DirectorySecurity PrivateDirectorySecurity()
    {
        var security = new DirectorySecurity();
        security.SetOwner(CurrentSid);
        security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(CurrentSid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        return security;
    }
    private static FileStream CreatePrivateFile(string path) => FileSystemAclExtensions.Create(
        new FileInfo(path), FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None,
        4096, FileOptions.WriteThrough, PrivateFileSecurity());

    internal static void RejectReparsePath(string path)
    {
        var cursor = Path.GetFullPath(path);
        while (!string.IsNullOrEmpty(cursor))
        {
            try
            {
                if ((File.GetAttributes(cursor) & FileAttributes.ReparsePoint) != 0)
                    throw new InvalidOperationException("링크 또는 리파스 경로의 인증 파일은 지원하지 않습니다.");
            }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            cursor = Path.GetDirectoryName(cursor);
        }
    }

    private static byte[] ReadBounded(string path, int maximum)
    {
        RejectReparsePath(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is <= 0 || stream.Length > maximum) throw CorruptStore();
        var bytes = new byte[(int)stream.Length];
        stream.ReadExactly(bytes);
        if (stream.ReadByte() != -1) throw CorruptStore();
        return bytes;
    }

    private T ReadEncrypted<T>(string path)
    {
        var encrypted = ReadBounded(path, MaxStoreBytes);
        byte[]? plaintext = null;
        try
        {
            plaintext = Dpapi(encrypted, false);
            return JsonSerializer.Deserialize<T>(plaintext, JsonOptions) ?? throw CorruptStore();
        }
        catch (Exception ex) when (ex is JsonException or Win32Exception or CryptographicException)
        {
            throw CorruptStore();
        }
        finally { if (plaintext is not null) CryptographicOperations.ZeroMemory(plaintext); }
    }

    private void WriteEncrypted<T>(string path, T value)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions);
        try
        {
            var encrypted = Dpapi(plain, true);
            if (encrypted.Length > MaxStoreBytes) throw new InvalidOperationException("계정 보관함 크기 제한을 초과했습니다.");
            WriteAtomic(path, encrypted);
        }
        finally { CryptographicOperations.ZeroMemory(plain); }
    }

    private void WriteAuth(byte[] bytes)
    {
        _ = ParseIdentity(bytes);
        WriteAtomic(AuthPath, bytes);
    }

    private void WriteAtomic(string path, byte[] bytes)
    {
        RejectReparsePath(path);
        var temporary = path.Equals(AuthPath, StringComparison.OrdinalIgnoreCase) ? AuthTempPath :
            Path.Combine(Path.GetDirectoryName(path)!, ".gfs-account-" + Guid.NewGuid().ToString("N") + ".tmp");
        RejectReparsePath(temporary);
        if (File.Exists(temporary))
            throw new InvalidOperationException("이전 인증 교체의 임시 파일이 있습니다. 복구를 먼저 실행하세요.");
        try
        {
            using (var stream = CreatePrivateFile(temporary))
            {
                stream.Write(bytes);
                stream.Flush(true);
            }
            Checkpoint?.Invoke(Path.GetFileName(path) + "-temp-flushed");
            RejectReparsePath(path);
            if (File.Exists(path))
            {
                FileSystemAclExtensions.SetAccessControl(new FileInfo(path), PrivateFileSecurity());
                File.Replace(temporary, path, null, ignoreMetadataErrors: false);
            }
            else File.Move(temporary, path);
        }
        finally
        {
            // Auth replacement requires a private, short-lived plaintext temp on the same volume.
            // It is never retained as a backup; encryption applies to all inactive/journal copies.
            if (File.Exists(temporary)) DeleteChecked(temporary);
        }
    }

    private static void DeleteChecked(string path)
    {
        RejectReparsePath(path);
        File.Delete(path);
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct DataBlob { public int Length; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref DataBlob input, string? description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("crypt32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref DataBlob input, IntPtr description, ref DataBlob entropy,
        IntPtr reserved, IntPtr prompt, int flags, out DataBlob output);
    [DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr memory);

    private static byte[] Dpapi(byte[] bytes, bool protect)
    {
        var input = new DataBlob { Length = bytes.Length, Data = Marshal.AllocHGlobal(bytes.Length) };
        var entropy = new DataBlob { Length = Entropy.Length, Data = Marshal.AllocHGlobal(Entropy.Length) };
        var output = new DataBlob();
        try
        {
            Marshal.Copy(bytes, 0, input.Data, bytes.Length);
            Marshal.Copy(Entropy, 0, entropy.Data, Entropy.Length);
            // CRYPTPROTECT_UI_FORBIDDEN; omitting LOCAL_MACHINE binds to CurrentUser.
            var success = protect
                ? CryptProtectData(ref input, null, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, ref entropy, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!success) throw new Win32Exception(Marshal.GetLastWin32Error(), "Windows 계정 암호화 작업에 실패했습니다.");
            var result = new byte[output.Length];
            Marshal.Copy(output.Data, result, 0, result.Length);
            return result;
        }
        finally
        {
            ZeroUnmanaged(input.Data, input.Length);
            Marshal.FreeHGlobal(input.Data);
            Marshal.FreeHGlobal(entropy.Data);
            if (output.Data != IntPtr.Zero)
            {
                ZeroUnmanaged(output.Data, output.Length);
                LocalFree(output.Data);
            }
        }
    }
    private static void ZeroUnmanaged(IntPtr pointer, int length)
    {
        var zeros = new byte[Math.Min(length, 4096)];
        for (var offset = 0; offset < length; offset += zeros.Length)
            Marshal.Copy(zeros, 0, pointer + offset, Math.Min(zeros.Length, length - offset));
    }

    internal sealed class Vault
    {
        public int Version { get; set; } = 1;
        public List<Entry> Accounts { get; set; } = new();
    }
    internal sealed class Entry
    {
        public string Id { get; set; } = "";
        public string Key { get; set; } = "";
        public string Label { get; set; } = "";
        public string Hint { get; set; } = "";
        public byte[] Auth { get; set; } = Array.Empty<byte>();
        public UsageSnapshot? Usage { get; set; }
        public DateTimeOffset? ObservedAt { get; set; }
    }
    internal sealed record SwitchJournal(int Version, string SourceKey, string TargetKey, byte[] BeforeAuth,
        byte[] AfterAuth, string BeforeDigest, string AfterDigest, Vault Before, Vault After);
}
