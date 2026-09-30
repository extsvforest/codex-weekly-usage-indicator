using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace WeeklyUsageIndicator;

internal sealed record ClaudeProfileIdentity(string Key, string Hint, string? SubscriptionType);

internal sealed record ClaudeProfile(string Id, string Label, string ConfigDirectory, bool IsDefault,
    ClaudeProfileIdentity? Identity, DateTimeOffset? VerifiedAt, bool IsSelected = false,
    bool UseDefaultHome = false)
{
    public string IdentityHint => Identity?.Hint ?? string.Empty;
    public bool IsVerified => Identity is not null && VerifiedAt is not null;
}

/// <summary>Stores profile names, paths and verified public identity only. Claude owns every login file.</summary>
internal sealed class ClaudeProfileStore
{
    private const int MaxProfiles = 20;
    private const int MaxMetadataBytes = 256 * 1024;
    private readonly string _metadataPath;
    private readonly bool _defaultUsesHome;
    private readonly string _mutexName;
    public string RootPath { get; }
    public string DefaultConfigDirectory { get; }
    public string ProfilesDirectory => Path.Combine(RootPath, "profiles");
    public ClaudeProfile DefaultProfile => new("default", "Claude Code", DefaultConfigDirectory, true,
        null, null, false, _defaultUsesHome);
    public string? SelectedProfileId => ReadLocked().SelectedProfileId;
    public ClaudeProfile? SelectedProfile => ListProfiles().SingleOrDefault(profile => profile.IsSelected);

    public ClaudeProfileStore(string? root = null, string? defaultConfigDirectory = null,
        bool? defaultUsesHome = null)
    {
        RootPath = Path.GetFullPath(root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexWeeklyUsageIndicator.ClaudeProfiles"));
        _metadataPath = Path.Combine(RootPath, "profiles.json");
        // A managed user-level route is the selected account, never the original home.
        // Existing v1 default rows already contain the information needed to pin that home.
        var original = ReadOriginalLocation();
        var configuredDirectory = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        var suppliedDirectory = defaultConfigDirectory ?? original?.Directory ?? configuredDirectory;
        _defaultUsesHome = defaultUsesHome ?? (defaultConfigDirectory is null ? original?.UsesHome : null)
            ?? string.IsNullOrWhiteSpace(suppliedDirectory);
        DefaultConfigDirectory = Path.GetFullPath(string.IsNullOrWhiteSpace(suppliedDirectory)
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude")
            : suppliedDirectory);
        _mutexName = @"Local\CodexWeeklyUsageIndicator.ClaudeProfiles."
            + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(RootPath.ToUpperInvariant())))[..20];
        if (IsWithin(DefaultConfigDirectory, RootPath) || IsWithin(RootPath, DefaultConfigDirectory))
            throw new ArgumentException("Claude 프로필 저장 위치와 기존 작업 폴더는 분리해야 합니다.");
    }

    public IReadOnlyList<ClaudeProfile> ListProfiles()
    {
        var metadata = ReadLocked();
        return metadata.Profiles.Select(profile => profile with
        {
            IsSelected = profile.Id == metadata.SelectedProfileId
        }).ToArray();
    }

    internal void PinOriginalLocation()
    {
        using var gate = AcquireLock();
        var metadata = Read();
        if (metadata.OriginalConfigDirectory is null || metadata.OriginalUsesHome is null)
            Write(metadata);
    }

    public ClaudeProfile RegisterDefault(string label, ClaudeProfileIdentity identity,
        DateTimeOffset? verifiedAt = null)
    {
        ValidateIdentity(identity);
        using var gate = AcquireLock();
        var metadata = Read();
        var profile = DefaultProfile with { Label = ValidateLabel(label), Identity = identity,
            VerifiedAt = verifiedAt ?? DateTimeOffset.UtcNow };
        var existing = metadata.Profiles.FindIndex(value => value.IsDefault);
        if (existing >= 0) metadata.Profiles[existing] = profile;
        else
        {
            RequireCapacity(metadata);
            metadata.Profiles.Insert(0, profile);
        }
        metadata.SelectedProfileId ??= profile.Id;
        Write(metadata);
        return profile with { IsSelected = metadata.SelectedProfileId == profile.Id };
    }

    public ClaudeProfile CreateProfile(string label)
    {
        using var gate = AcquireLock();
        var metadata = Read();
        RequireCapacity(metadata);
        var id = Guid.NewGuid().ToString("N");
        var profile = new ClaudeProfile(id, ValidateLabel(label), Path.Combine(ProfilesDirectory, id),
            false, null, null);
        // Never seed an authentication file. The official login command creates its own state later.
        CreatePrivateDirectory(RootPath);
        CreatePrivateDirectory(ProfilesDirectory);
        CreatePrivateDirectory(profile.ConfigDirectory);
        metadata.Profiles.Add(profile);
        Write(metadata);
        return profile;
    }

    public ClaudeProfile RecordVerifiedIdentity(string id, ClaudeProfileIdentity identity,
        DateTimeOffset? verifiedAt = null)
    {
        ValidateIdentity(identity);
        using var gate = AcquireLock();
        var metadata = Read();
        var index = Find(metadata, id);
        var before = metadata.Profiles[index];
        if (before.Identity is not null && before.Identity.Key != identity.Key)
            throw new InvalidOperationException("저장된 Claude 계정과 현재 로그인이 다릅니다. 계정을 새로 등록하세요.");
        var profile = before with { Identity = identity, VerifiedAt = verifiedAt ?? DateTimeOffset.UtcNow };
        metadata.Profiles[index] = profile;
        Write(metadata);
        return profile with { IsSelected = metadata.SelectedProfileId == id };
    }

    public void Select(string id, ClaudeProfileIdentity verifiedIdentity)
    {
        ValidateIdentity(verifiedIdentity);
        using var gate = AcquireLock();
        var metadata = Read();
        var profile = metadata.Profiles[Find(metadata, id)];
        if (!profile.IsVerified || profile.Identity!.Key != verifiedIdentity.Key)
            throw new InvalidOperationException("Claude 로그인을 확인한 뒤 선택하세요.");
        metadata.SelectedProfileId = id;
        Write(metadata);
    }

    public void Rename(string id, string label)
    {
        label = ValidateLabel(label);
        using var gate = AcquireLock();
        var metadata = Read();
        var index = Find(metadata, id);
        metadata.Profiles[index] = metadata.Profiles[index] with { Label = label };
        Write(metadata);
    }

    public void MarkLoggedOut(string id)
    {
        using var gate = AcquireLock();
        var metadata = Read();
        var index = Find(metadata, id);
        metadata.Profiles[index] = metadata.Profiles[index] with { VerifiedAt = null };
        if (metadata.SelectedProfileId == id) metadata.SelectedProfileId = null;
        Write(metadata);
    }

    public void Remove(string id)
    {
        using var gate = AcquireLock();
        var metadata = Read();
        metadata.Profiles.RemoveAt(Find(metadata, id));
        if (metadata.SelectedProfileId == id) metadata.SelectedProfileId = null;
        // Removing a row must never delete a Claude folder, linked conversations or an official login.
        Write(metadata);
    }

    internal void ValidateProfile(ClaudeProfile profile)
    {
        if (profile.Id == "default" && profile.IsDefault)
        {
            if (!PathsEqual(profile.ConfigDirectory, DefaultConfigDirectory)
                || profile.UseDefaultHome != _defaultUsesHome) throw InvalidMetadata();
        }
        else if (profile.IsDefault || !Guid.TryParseExact(profile.Id, "N", out _)
                 || profile.UseDefaultHome
                 || !PathsEqual(profile.ConfigDirectory, Path.Combine(ProfilesDirectory, profile.Id)))
            throw InvalidMetadata();
        _ = ValidateLabel(profile.Label);
        if (profile.Identity is not null) ValidateIdentity(profile.Identity);
        if (profile.VerifiedAt is not null && profile.Identity is null) throw InvalidMetadata();
    }

    private Metadata ReadLocked()
    {
        using var gate = AcquireLock();
        return Read();
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
            if (metadata.Version != 1 || metadata.Profiles is null || metadata.Profiles.Count > MaxProfiles
                || metadata.Profiles.Any(profile => profile is null)
                || metadata.Profiles.Select(profile => profile.Id).Distinct().Count() != metadata.Profiles.Count
                || metadata.Profiles.Count(profile => profile.IsDefault) > 1) throw InvalidMetadata();
            foreach (var profile in metadata.Profiles) ValidateProfile(profile);
            if (metadata.SelectedProfileId is not null
                && !metadata.Profiles.Any(profile => profile.Id == metadata.SelectedProfileId && profile.IsVerified))
                throw InvalidMetadata();
            return metadata;
        }
        catch (JsonException) { throw InvalidMetadata(); }
    }

    private void Write(Metadata metadata)
    {
        metadata.OriginalConfigDirectory = DefaultConfigDirectory;
        metadata.OriginalUsesHome = _defaultUsesHome;
        CreatePrivateDirectory(RootPath);
        var temporary = Path.Combine(RootPath, ".profiles-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                       4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, metadata);
                stream.Flush(flushToDisk: true);
            }
            if (File.Exists(_metadataPath)) File.Replace(temporary, _metadataPath, null);
            else File.Move(temporary, _metadataPath);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private (string Directory, bool UsesHome)? ReadOriginalLocation()
    {
        if (!File.Exists(_metadataPath)) return null;
        if (new FileInfo(_metadataPath).Length is <= 0 or > MaxMetadataBytes
            || (File.GetAttributes(_metadataPath) & FileAttributes.ReparsePoint) != 0) throw InvalidMetadata();
        try
        {
            using var stream = File.OpenRead(_metadataPath);
            var metadata = JsonSerializer.Deserialize<Metadata>(stream) ?? throw InvalidMetadata();
            if (metadata.Version != 1 || metadata.Profiles is null) throw InvalidMetadata();
            var defaults = metadata.Profiles.Where(profile => profile?.IsDefault == true).ToArray();
            if (defaults.Length > 1) throw InvalidMetadata();
            var directory = metadata.OriginalConfigDirectory ?? defaults.FirstOrDefault()?.ConfigDirectory;
            var usesHome = metadata.OriginalUsesHome ?? defaults.FirstOrDefault()?.UseDefaultHome;
            if (directory is null && usesHome is null) return null;
            if (directory is null || usesHome is null || !Path.IsPathFullyQualified(directory)) throw InvalidMetadata();
            if (defaults.FirstOrDefault() is { } original
                && (!PathsEqual(original.ConfigDirectory, directory) || original.UseDefaultHome != usesHome))
                throw InvalidMetadata();
            return (directory, usesHome.Value);
        }
        catch (JsonException) { throw InvalidMetadata(); }
    }

    internal static void CreatePrivateDirectory(string path)
    {
        var directory = Directory.CreateDirectory(path);
        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0)
            throw new IOException("Claude 프로필 폴더가 다른 위치를 가리키고 있습니다.");
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
            if (!acquired) throw new InvalidOperationException("다른 Claude 계정 작업이 진행 중입니다.");
            return new Gate(mutex);
        }
        catch { mutex.Dispose(); throw; }
    }

    internal static bool PathsEqual(string left, string right) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)), StringComparison.OrdinalIgnoreCase);

    private static bool IsWithin(string path, string parent) => PathsEqual(path, parent)
        || Path.GetFullPath(path).StartsWith(Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent))
            + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    private static int Find(Metadata metadata, string id)
    {
        var index = metadata.Profiles.FindIndex(profile => profile.Id == id);
        return index >= 0 ? index : throw new InvalidOperationException("저장된 Claude 계정을 찾을 수 없습니다.");
    }
    private static void RequireCapacity(Metadata metadata)
    {
        if (metadata.Profiles.Count >= MaxProfiles) throw new InvalidOperationException("Claude 계정은 20개까지 등록할 수 있습니다.");
    }
    private static string ValidateLabel(string label)
    {
        label = (label ?? string.Empty).Trim();
        if (label.Length is < 1 or > 48 || label.Any(char.IsControl))
            throw new ArgumentException("별칭은 1~48자로 입력하세요.");
        return label;
    }
    private static void ValidateIdentity(ClaudeProfileIdentity identity)
    {
        if (identity.Key is null || identity.Key.Length != 64 || identity.Key.Any(character => !Uri.IsHexDigit(character))
            || identity.Hint is null || identity.Hint.Length is < 1 or > 80 || identity.Hint.Any(char.IsControl)
            || identity.SubscriptionType is { Length: > 32 }) throw InvalidMetadata();
    }
    private static InvalidDataException InvalidMetadata() => new("Claude 계정 목록이 올바르지 않습니다. 기존 로그인은 보존했습니다.");
    private sealed class Metadata
    {
        public int Version { get; set; } = 1;
        public string? OriginalConfigDirectory { get; set; }
        public bool? OriginalUsesHome { get; set; }
        public string? SelectedProfileId { get; set; }
        public List<ClaudeProfile> Profiles { get; set; } = [];
    }
    private sealed class Gate(Mutex mutex) : IDisposable
    {
        public void Dispose() { mutex.ReleaseMutex(); mutex.Dispose(); }
    }
}
