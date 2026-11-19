using System.Text.Json;

namespace WeeklyUsageIndicator;

// Explicit opt-in only. Discovering an installed distro never enables switching.
internal sealed class WslCodexConnections(string root)
{
    private readonly string _path = Path.Combine(root, "wsl-targets.json");
    internal IReadOnlySet<string> EnabledKeys()
    {
        if (!File.Exists(_path)) return new HashSet<string>(StringComparer.Ordinal);
        if (new FileInfo(_path).Length > 16 * 1024 || (File.GetAttributes(_path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("WSL 연결 설정을 확인할 수 없습니다.");
        var keys = JsonSerializer.Deserialize<string[]>(File.ReadAllText(_path)) ?? [];
        if (keys.Length > 32 || keys.Any(key => key.Length != 64 || !key.All(Uri.IsHexDigit)))
            throw new InvalidDataException("WSL 연결 설정을 확인할 수 없습니다.");
        return keys.ToHashSet(StringComparer.Ordinal);
    }

    internal void SetEnabled(WslCodexTarget target, bool enabled)
    {
        target.Validate();
        var keys = EnabledKeys().ToHashSet(StringComparer.Ordinal);
        if (enabled) keys.Add(target.Key); else keys.Remove(target.Key);
        CodexAccountRuntime.AssertNoReparsePoints(root);
        Directory.CreateDirectory(root);
        var temporary = _path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, JsonSerializer.Serialize(keys.Order().ToArray())); File.Move(temporary, _path, true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
