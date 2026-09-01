using System.Text.Json;

namespace WeeklyUsageIndicator;

internal sealed record ClaudeUsageCacheEntry(
    ClaudeUsageSnapshot Snapshot,
    DateTimeOffset LastUpdatedAt);

internal interface IClaudeUsageCacheStore
{
    ClaudeUsageCacheEntry? Load();

    void Save(ClaudeUsageCacheEntry entry);

    void Clear();
}

internal sealed class ClaudeUsageFileCacheStore(string cachePath) : IClaudeUsageCacheStore
{
    internal const string CacheFileName = "claude-usage-cache.json";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _cachePath = cachePath;

    public static ClaudeUsageFileCacheStore CreateDefault()
    {
        var installDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexWeeklyUsageIndicator");
        return new ClaudeUsageFileCacheStore(Path.Combine(installDirectory, CacheFileName));
    }

    public ClaudeUsageCacheEntry? Load()
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(_cachePath));
            if (TryReadEntry(document.RootElement, out var entry)) return entry;

            Clear();
            return null;
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return null;
        }
        catch (Exception exception) when (IsRecoverableFileError(exception))
        {
            Clear();
            return null;
        }
    }

    public void Clear()
    {
        try
        {
            if (File.Exists(_cachePath)) File.Delete(_cachePath);
        }
        catch (Exception exception) when (IsRecoverableFileError(exception))
        {
            // An unreadable cache is ignored even if Windows refuses its cleanup.
        }
    }

    private static bool TryReadEntry(JsonElement root, out ClaudeUsageCacheEntry? entry)
    {
        entry = null;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("Snapshot", out var snapshotElement) ||
            snapshotElement.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("LastUpdatedAt", out var updatedElement) ||
            !updatedElement.TryGetDateTimeOffset(out var lastUpdatedAt) ||
            !TryReadWindow(snapshotElement, "FiveHour", allowNull: true, out var fiveHour) ||
            !TryReadWindow(snapshotElement, "Weekly", allowNull: true, out var weekly) ||
            !TryReadWindow(snapshotElement, "Fable", allowNull: false, out var fable))
        {
            return false;
        }

        entry = new ClaudeUsageCacheEntry(
            new ClaudeUsageSnapshot(fiveHour, weekly, fable),
            lastUpdatedAt);
        return true;
    }

    private static bool TryReadWindow(
        JsonElement snapshot,
        string propertyName,
        bool allowNull,
        out ClaudeUsageWindow? window)
    {
        window = null;
        if (!snapshot.TryGetProperty(propertyName, out var element)) return false;
        if (element.ValueKind == JsonValueKind.Null) return allowNull;
        if (element.ValueKind != JsonValueKind.Object ||
            !element.TryGetProperty("UsedPercent", out var usedElement) ||
            !usedElement.TryGetDouble(out var usedPercent) ||
            !double.IsFinite(usedPercent) ||
            usedPercent is < 0d or > 100d ||
            !element.TryGetProperty("ResetsAt", out var resetElement))
        {
            return false;
        }

        DateTimeOffset? resetsAt = null;
        if (resetElement.ValueKind != JsonValueKind.Null)
        {
            if (!resetElement.TryGetDateTimeOffset(out var parsedReset)) return false;
            resetsAt = parsedReset;
        }

        window = new ClaudeUsageWindow(usedPercent, resetsAt);
        return true;
    }

    public void Save(ClaudeUsageCacheEntry entry)
    {
        var temporaryPath = $"{_cachePath}.{Environment.ProcessId}.tmp";
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (string.IsNullOrWhiteSpace(directory)) return;

            Directory.CreateDirectory(directory);
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(entry, JsonOptions));
            File.Move(temporaryPath, _cachePath, overwrite: true);
        }
        catch (Exception exception) when (IsRecoverableFileError(exception))
        {
            // The in-memory result is still usable when persistence is unavailable.
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
            }
            catch (Exception exception) when (IsRecoverableFileError(exception))
            {
                // A leftover temporary file is harmless and contains no credentials.
            }
        }
    }

    private static bool IsRecoverableFileError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException;
}

internal sealed class ClaudeUsageNullCacheStore : IClaudeUsageCacheStore
{
    public static ClaudeUsageNullCacheStore Instance { get; } = new();

    private ClaudeUsageNullCacheStore()
    {
    }

    public ClaudeUsageCacheEntry? Load() => null;

    public void Save(ClaudeUsageCacheEntry entry)
    {
    }

    public void Clear()
    {
    }
}
