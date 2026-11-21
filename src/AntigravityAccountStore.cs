using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace WeeklyUsageIndicator;

/// <summary>Keeps the Antigravity accounts the widget has read: per account an opaque key, the user's name for it, a
/// masked hint and the last usage seen. Nothing here signs in or holds a credential, and no address is kept, so the file
/// is plain JSON in a folder only the user (and SYSTEM) can open. The CLI's own temporary log, which names the account
/// for the few seconds of a read, is written to its <c>run</c> folder.</summary>
internal sealed class AntigravityAccountStore
{
    internal const int MaxAccounts = 20;
    private const int MaxMetadataBytes = 256 * 1024;
    internal const string Invalid = "Antigravity 계정 목록이 올바르지 않습니다. 파일은 그대로 두었습니다.";
    internal const string Busy = "다른 Antigravity 계정 작업이 진행 중입니다.";
    internal const string Full = "Antigravity 계정은 20개까지 저장할 수 있습니다.";
    internal const string NotFound = "저장된 Antigravity 계정을 찾을 수 없습니다.";
    internal const string BadName = "이름을 1~40자로 입력해 주세요. 줄바꿈은 사용할 수 없습니다.";
    internal const string Redirected = "Antigravity 계정 폴더가 다른 위치를 가리키고 있습니다.";
    private readonly string _metadataPath;
    private readonly string _mutexName;

    internal string RootPath { get; }
    internal string RunDirectory => Path.Combine(RootPath, "run");

    internal AntigravityAccountStore(string? root = null)
    {
        RootPath = Path.GetFullPath(root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexWeeklyUsageIndicator.Antigravity"));
        _metadataPath = Path.Combine(RootPath, "accounts.json");
        _mutexName = @"Local\CodexWeeklyUsageIndicator.Antigravity."
            + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RootPath.ToUpperInvariant())))[..20];
    }

    internal IReadOnlyList<AntigravityAccount> ListAccounts()
    {
        using var gate = AcquireLock();
        return Read().Accounts.ToArray();
    }

    /// <summary>Records what a read saw for an account, adding the account the first time. Its name stays as it is.</summary>
    internal (AntigravityAccount Account, bool FirstSeen) RecordObservation(string identityKey, string hint,
        AntigravityUsage usage, DateTimeOffset observedAt)
    {
        using var gate = AcquireLock();
        var metadata = Read();
        var index = metadata.Accounts.FindIndex(account => account.IdentityKey == identityKey);
        var firstSeen = index < 0;
        if (firstSeen && metadata.Accounts.Count >= MaxAccounts) throw new InvalidOperationException(Full);
        var account = (firstSeen ? new AntigravityAccount(identityKey, null, hint, null, null) : metadata.Accounts[index]) with
        {
            Hint = hint, Usage = usage, ObservedAt = observedAt
        };
        ValidateAccount(account);
        if (firstSeen) metadata.Accounts.Add(account);
        else metadata.Accounts[index] = account;
        Write(metadata);
        return (account, firstSeen);
    }

    internal AntigravityAccount Rename(string identityKey, string label)
    {
        label = (label ?? "").Trim();
        // No parameter name: it would be added to the message, which is shown to the user.
        if (!ValidLabel(label)) throw new ArgumentException(BadName);
        using var gate = AcquireLock();
        var metadata = Read();
        var index = Find(metadata, identityKey);
        var account = metadata.Accounts[index] with { Label = label };
        metadata.Accounts[index] = account;
        Write(metadata);
        return account;
    }

    /// <summary>Forgets an account: its row only. Antigravity's own sign-in is never touched.</summary>
    internal void Remove(string identityKey)
    {
        using var gate = AcquireLock();
        var metadata = Read();
        metadata.Accounts.RemoveAt(Find(metadata, identityKey));
        Write(metadata);
    }

    /// <summary>The private folder a read runs in and writes the CLI's temporary log to.</summary>
    internal string PrepareRunDirectory()
    {
        CreatePrivateDirectory(RootPath);
        CreatePrivateDirectory(RunDirectory);
        return RunDirectory;
    }

    private Metadata Read()
    {
        if (!File.Exists(_metadataPath)) return new Metadata();
        try
        {
            if (new FileInfo(_metadataPath).Length is <= 0 or > MaxMetadataBytes) throw InvalidMetadata();
            if ((File.GetAttributes(_metadataPath) & FileAttributes.ReparsePoint) != 0) throw InvalidMetadata();
            using var stream = File.OpenRead(_metadataPath);
            var metadata = JsonSerializer.Deserialize<Metadata>(stream) ?? throw InvalidMetadata();
            if (metadata.Version != 1 || metadata.Accounts is null || metadata.Accounts.Count > MaxAccounts ||
                metadata.Accounts.Any(account => account is null) ||
                metadata.Accounts.Select(account => account.IdentityKey).Distinct(StringComparer.Ordinal).Count() != metadata.Accounts.Count)
                throw InvalidMetadata();
            foreach (var account in metadata.Accounts) ValidateAccount(account);
            return metadata;
        }
        catch (JsonException) { throw InvalidMetadata(); }
    }

    private void Write(Metadata metadata)
    {
        CreatePrivateDirectory(RootPath);
        var temporary = Path.Combine(RootPath, ".accounts-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                       FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, metadata);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(_metadataPath)) File.Replace(temporary, _metadataPath, null);
            else File.Move(temporary, _metadataPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private static void ValidateAccount(AntigravityAccount account)
    {
        if (account.IdentityKey is not { Length: 64 } key || key.Any(character => !Uri.IsHexDigit(character) || char.IsLower(character)) ||
            account.Hint is null || account.Hint.Length is < 1 or > 80 || account.Hint.Any(char.IsControl) ||
            account.Label is not null && !ValidLabel(account.Label) ||
            account.Usage is { } usage && new[] { usage.GeminiWeekly, usage.GeminiFiveHour, usage.ThirdPartyWeekly, usage.ThirdPartyFiveHour }
                .Any(window => window is not null && (!double.IsFinite(window.RemainingFraction) || window.RemainingFraction is < 0 or > 1)) ||
            (account.Usage is null) != (account.ObservedAt is null))
            throw InvalidMetadata();
    }

    private static bool ValidLabel(string label) => label.Length is >= 1 and <= 40 && !label.Any(char.IsControl) &&
        label == label.Trim();

    private static int Find(Metadata metadata, string identityKey)
    {
        var index = metadata.Accounts.FindIndex(account => account.IdentityKey == identityKey);
        return index >= 0 ? index : throw new InvalidOperationException(NotFound);
    }

    private static void CreatePrivateDirectory(string path)
    {
        var directory = Directory.CreateDirectory(path);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException(Redirected);
        if (!OperatingSystem.IsWindows()) return;
        var owner = WindowsIdentity.GetCurrent().User
            ?? throw new InvalidOperationException("현재 Windows 사용자를 확인할 수 없습니다.");
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.SetOwner(owner);
        foreach (var identity in new[] { owner, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            security.AddAccessRule(new FileSystemAccessRule(identity, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None,
                AccessControlType.Allow));
        directory.SetAccessControl(security);
    }

    private IDisposable AcquireLock()
    {
        var mutex = new Mutex(false, _mutexName);
        try
        {
            bool acquired;
            try { acquired = mutex.WaitOne(TimeSpan.FromSeconds(5)); }
            catch (AbandonedMutexException) { acquired = true; }
            if (!acquired) throw new InvalidOperationException(Busy);
            return new Gate(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    private static InvalidDataException InvalidMetadata() => new(Invalid);

    private sealed class Metadata
    {
        public int Version { get; set; } = 1;
        public List<AntigravityAccount> Accounts { get; set; } = [];
    }

    private sealed class Gate(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
}
