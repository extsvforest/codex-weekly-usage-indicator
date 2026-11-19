using System.Security.Cryptography;
using System.Text;

namespace WeeklyUsageIndicator;

internal enum WslCodexAvailability { Ready, NotRunning, MissingCli, Unsupported, Busy, Unavailable }

// This identity addresses a Linux user's auth store, not merely a distro display name.
internal sealed record WslCodexTarget(string Distribution, int UserId, string UserName,
    string HomeDirectory, string CodexHome, string Executable, string Version)
{
    public string Key => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        System.Text.Json.JsonSerializer.Serialize(new[] { Distribution, UserId.ToString(System.Globalization.CultureInfo.InvariantCulture), CodexHome }))));
    public string DisplayName => $"{Distribution} · {UserName}";

    internal void Validate()
    {
        static bool Safe(string s, int maximum) => !string.IsNullOrWhiteSpace(s) && s.Length <= maximum && !s.Any(char.IsControl);
        static bool LinuxPath(string s) => Safe(s, 2048) && s.StartsWith('/') && !s.StartsWith("/mnt/", StringComparison.Ordinal)
            && !s.Contains("/../", StringComparison.Ordinal) && !s.Contains("/./", StringComparison.Ordinal)
            && !s.EndsWith("/..", StringComparison.Ordinal) && !s.EndsWith("/.", StringComparison.Ordinal);
        if (!Safe(Distribution, 128) || UserId <= 0 || !Safe(UserName, 128) ||
            !LinuxPath(HomeDirectory) || !LinuxPath(CodexHome) || !LinuxPath(Executable) ||
            Executable.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) || !Safe(Version, 128))
            throw new WslCodexException("unsupported", "지원되는 WSL Codex 환경인지 확인하지 못했습니다.");
    }
}

internal sealed record WslCodexProbe(string Distribution, WslCodexAvailability Availability,
    WslCodexTarget? Target, int WriterCount, string ReasonCode)
{
    public bool CanSwitch => Availability == WslCodexAvailability.Ready && Target is not null && WriterCount == 0;
}

internal sealed record WslCodexBinding(string AccountId, string IdentityKey, string IdentityHint);
internal sealed record WslCodexPreparedSwitch(string TransactionId, WslCodexTarget Target,
    string SourceAccountId, string TargetAccountId, string TargetIdentityKey);
internal sealed record WslCodexSwitchResult(WslCodexTarget Target, string AccountId, bool Verified,
    string ReasonCode);
internal sealed record WslCodexJointResult(bool WindowsCompleted, bool Completed,
    IReadOnlyList<WslCodexSwitchResult> Targets, string ReasonCode = "completed");
internal sealed record WslCodexAbandonResult(IReadOnlyList<WslCodexTarget> Targets, int ArchivedJournalCount);

internal sealed class WslCodexException(string code, string message) : InvalidOperationException(message)
{
    public string Code { get; } = code;
}

// Implementations must never put auth bytes in arguments, diagnostics, or exception messages.
// ReadAuth/Login return sensitive bytes only to the encrypted store; callers clear them.
internal interface IWslCodexBridge
{
    Task<IReadOnlyList<string>> ListRunningDistributionsAsync(CancellationToken token);
    Task<WslCodexProbe> ProbeAsync(string distribution, WslCodexTarget? expected, CancellationToken token);
    Task<byte[]> ReadAuthAsync(WslCodexTarget target, CancellationToken token);
    Task ReplaceAuthAsync(WslCodexTarget target, string expectedDigest, byte[] auth, CancellationToken token);
    Task<bool> VerifyOfficialClientAsync(WslCodexTarget target, CancellationToken token);
    Task<byte[]> LoginAsync(WslCodexTarget target, CancellationToken token, Action<Uri>? onAuthorizationUrl = null);
}
