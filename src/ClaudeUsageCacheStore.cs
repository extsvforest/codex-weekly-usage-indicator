using System.Collections.Concurrent;
using System.Text.Json;

namespace WeeklyUsageIndicator;

internal sealed record ClaudeUsageCacheEntry(
    ClaudeUsageSnapshot Snapshot,
    DateTimeOffset LastUpdatedAt,
    string ProfileKey = ClaudeUsageCacheEntry.DefaultProfileKey)
{
    internal const string DefaultProfileKey = "default";
}

/// <summary>One account's recovery value, as <see cref="ClaudeUsageClient"/> keeps it.</summary>
internal interface IClaudeUsageCacheStore
{
    ClaudeUsageCacheEntry? Load();

    void Save(ClaudeUsageCacheEntry entry);

    void Clear();
}

/// <summary>
/// The last usage observed for each Claude account, under an opaque key (the profile ID and the verified identity
/// digest): percentages, reset times and check time, never a token, email or organization name. An entry serves two
/// readers with their own rules. The widget's recovery reads it through <see cref="For"/>: only after a transient live
/// failure, for 24 hours, ended by an authentication or schema failure (<see cref="ClaudeUsageClient"/> decides each).
/// A page opened without a query reads <see cref="Last"/>: the value and its check time for up to
/// <see cref="HistoryLifetime"/>, recovery or not. Ending recovery keeps the last value.
/// </summary>
internal sealed class ClaudeUsageFileCacheStore
{
    internal const string CacheFileName = "claude-usage-cache.json";
    internal static readonly TimeSpan HistoryLifetime = TimeSpan.FromDays(30);
    private const int FormatVersion = 2;
    private const int MaxEntries = 64;
    private const int MaxFileBytes = 1024 * 1024;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true
    };

    // Reads of several accounts end side by side, and each one rewrites this file.
    private static readonly ConcurrentDictionary<string, object> FileGates = new(StringComparer.OrdinalIgnoreCase);

    private readonly string _cachePath;
    private readonly TimeProvider _timeProvider;
    private readonly object _gate;

    internal ClaudeUsageFileCacheStore(string cachePath, TimeProvider? timeProvider = null)
    {
        _cachePath = Path.GetFullPath(cachePath);
        _timeProvider = timeProvider ?? TimeProvider.System;
        _gate = FileGates.GetOrAdd(_cachePath, _ => new object());
    }

    public static ClaudeUsageFileCacheStore CreateDefault(TimeProvider? timeProvider = null)
    {
        var installDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "CodexWeeklyUsageIndicator");
        return new ClaudeUsageFileCacheStore(Path.Combine(installDirectory, CacheFileName), timeProvider);
    }

    /// <summary>The recovery value of one profile key.</summary>
    internal IClaudeUsageCacheStore For(string profileKey)
    {
        if (string.IsNullOrWhiteSpace(profileKey)) throw new ArgumentException("A Claude profile key is required.", nameof(profileKey));
        return new RecoveryView(this, profileKey);
    }

    /// <summary>The last observation kept for <paramref name="profileKey"/>, at most <see cref="HistoryLifetime"/> old and
    /// never dated in the future. Its windows may have reset since.</summary>
    internal ClaudeUsageCacheEntry? Last(string profileKey)
    {
        lock (_gate) return Read().FirstOrDefault(stored => stored.Entry.ProfileKey == profileKey)?.Entry;
    }

    /// <summary>Ends recovery use of every key of <paramref name="profileId"/>; the last values stay for display.</summary>
    internal void EndRecovery(string profileId) => Update(entries =>
    {
        for (var index = 0; index < entries.Count; index++)
            if (entries[index].Entry.ProfileKey.StartsWith(profileId + ":", StringComparison.Ordinal))
                entries[index] = entries[index] with { Recovery = false };
    });

    /// <summary>Moves the value kept under <paramref name="fromKey"/>, recovery state included, to
    /// <paramref name="toKey"/> (a profile that takes over the same account).</summary>
    internal void Move(string fromKey, string toKey) => Update(entries =>
    {
        var index = entries.FindIndex(stored => stored.Entry.ProfileKey == fromKey);
        if (index < 0) return;
        var moved = entries[index] with { Entry = entries[index].Entry with { ProfileKey = toKey } };
        entries.RemoveAll(stored => stored.Entry.ProfileKey == fromKey || stored.Entry.ProfileKey == toKey);
        entries.Add(moved);
    });

    private ClaudeUsageCacheEntry? LoadRecovery(string profileKey)
    {
        lock (_gate)
            return Read().FirstOrDefault(stored => stored.Recovery && stored.Entry.ProfileKey == profileKey)?.Entry;
    }

    private void Save(ClaudeUsageCacheEntry entry) => Update(entries =>
    {
        entries.RemoveAll(stored => stored.Entry.ProfileKey == entry.ProfileKey);
        entries.Add(new StoredEntry(entry, Recovery: true));
    });

    private void EndRecoveryOf(string profileKey) => Update(entries =>
    {
        var index = entries.FindIndex(stored => stored.Entry.ProfileKey == profileKey);
        if (index >= 0) entries[index] = entries[index] with { Recovery = false };
    });

    private void Update(Action<List<StoredEntry>> change)
    {
        lock (_gate)
        {
            var entries = Read();
            var before = entries.ToArray();
            change(entries);
            entries = Kept(entries);
            if (!entries.SequenceEqual(before)) Write(entries);
        }
    }

    // Entries within the history lifetime, newest first, at most MaxEntries.
    private List<StoredEntry> Kept(IEnumerable<StoredEntry> entries)
    {
        var now = _timeProvider.GetUtcNow();
        return entries
            .Where(stored => stored.Entry.LastUpdatedAt <= now && now - stored.Entry.LastUpdatedAt <= HistoryLifetime)
            .OrderByDescending(stored => stored.Entry.LastUpdatedAt)
            .Take(MaxEntries)
            .ToList();
    }

    private List<StoredEntry> Read()
    {
        try
        {
            var info = new FileInfo(_cachePath);
            if (!info.Exists) return [];
            if (info.Length > MaxFileBytes)
            {
                Delete();
                return [];
            }
            using var document = JsonDocument.Parse(File.ReadAllText(_cachePath));
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("Version", out var version) &&
                version.ValueKind == JsonValueKind.Number &&
                version.TryGetInt32(out var number) && number == FormatVersion &&
                root.TryGetProperty("Entries", out var items) &&
                items.ValueKind == JsonValueKind.Array)
            {
                var entries = new List<StoredEntry>();
                foreach (var item in items.EnumerateArray())
                {
                    if (!TryReadEntry(item, out var entry) ||
                        !item.TryGetProperty("Recovery", out var recovery) ||
                        recovery.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) continue;
                    entries.RemoveAll(stored => stored.Entry.ProfileKey == entry!.ProfileKey);
                    entries.Add(new StoredEntry(entry!, recovery.ValueKind == JsonValueKind.True));
                }
                return Kept(entries);
            }

            // A single recovery snapshot written before values were kept per account.
            if (TryReadEntry(root, out var legacy)) return Kept([new StoredEntry(legacy!, Recovery: true)]);

            Delete();
            return [];
        }
        catch (Exception exception) when (
            exception is FileNotFoundException or DirectoryNotFoundException)
        {
            return [];
        }
        catch (Exception exception) when (IsRecoverableFileError(exception))
        {
            Delete();
            return [];
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
            !TryReadWindow(snapshotElement, "Fable", allowNull: true, out var fable) ||
            (fiveHour is null && weekly is null && fable is null))
        {
            return false;
        }

        var profileKey = ClaudeUsageCacheEntry.DefaultProfileKey;
        if (root.TryGetProperty("ProfileKey", out var profileElement))
        {
            if (profileElement.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(profileElement.GetString()) ||
                profileElement.GetString()!.Length > 256) return false;
            profileKey = profileElement.GetString()!;
        }
        entry = new ClaudeUsageCacheEntry(
            new ClaudeUsageSnapshot(fiveHour, weekly, fable),
            lastUpdatedAt, profileKey);
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

    private void Write(IReadOnlyList<StoredEntry> entries)
    {
        var temporaryPath = $"{_cachePath}.{Environment.ProcessId}.{Guid.NewGuid():N}.tmp";
        try
        {
            var directory = Path.GetDirectoryName(_cachePath);
            if (string.IsNullOrWhiteSpace(directory)) return;

            Directory.CreateDirectory(directory);
            var file = new CacheFile(FormatVersion, entries.Select(stored => new CacheFileEntry(
                stored.Entry.ProfileKey, stored.Entry.Snapshot, stored.Entry.LastUpdatedAt, stored.Recovery)).ToArray());
            File.WriteAllText(temporaryPath, JsonSerializer.Serialize(file, JsonOptions));
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

    private void Delete()
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

    private static bool IsRecoverableFileError(Exception exception) =>
        exception is IOException or UnauthorizedAccessException or JsonException;

    private sealed record StoredEntry(ClaudeUsageCacheEntry Entry, bool Recovery);

    private sealed record CacheFile(int Version, IReadOnlyList<CacheFileEntry> Entries);

    private sealed record CacheFileEntry(
        string ProfileKey,
        ClaudeUsageSnapshot Snapshot,
        DateTimeOffset LastUpdatedAt,
        bool Recovery);

    private sealed class RecoveryView(ClaudeUsageFileCacheStore store, string profileKey) : IClaudeUsageCacheStore
    {
        public ClaudeUsageCacheEntry? Load() => store.LoadRecovery(profileKey);

        // A client saves only its own account's observation.
        public void Save(ClaudeUsageCacheEntry entry) => store.Save(entry with { ProfileKey = profileKey });

        public void Clear() => store.EndRecoveryOf(profileKey);
    }
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
