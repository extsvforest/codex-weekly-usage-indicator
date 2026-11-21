using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace WeeklyUsageIndicator;

internal enum ClaudeScheduledProblemKind { None, Unreviewed, Unreadable, Failed }

/// <summary>Why scheduled tasks are not being aligned, once that has lasted (see <see cref="ClaudeShelfService"/>).</summary>
internal sealed record ClaudeScheduledProblem(ClaudeScheduledProblemKind Kind, string? Detail = null);

internal enum ClaudeScheduledReadStatus { Missing, Ok, Unreadable }

/// <summary>One account's scheduled-task list as read now. <see cref="Entries"/> are the entries with ids the app keeps,
/// by id; <see cref="Bytes"/> the file as read (null when missing).</summary>
internal sealed record ClaudeScheduledFile(string Key, string Path, ClaudeScheduledReadStatus Status, byte[]? Bytes,
    JsonObject? Root, IReadOnlyDictionary<string, JsonObject> Entries, DateTimeOffset? WrittenAt, string? Problem = null)
{
    internal bool Readable => Status != ClaudeScheduledReadStatus.Unreadable;
}

/// <summary>
/// The Claude app's scheduled-task list of one account and organization
/// (<c>claude-code-sessions\account\org\scheduled-tasks.json</c>), as Claude 6.28899 reads it: the whole file passes its
/// schema or the app loads none of it (and its next save keeps only what is made after). <see cref="IsValid"/> mirrors
/// that schema; a file it refuses is never written or counted from. The definitions (<c>~/.claude/scheduled-tasks/id</c>)
/// are shared by every account and are never read here.
/// </summary>
internal static class ClaudeScheduledRegistry
{
    internal const string FileName = "scheduled-tasks.json";
    private const long MaxBytes = 16 * 1024 * 1024;
    private static readonly Regex SafeId = new("^[a-z0-9_-]+$", RegexOptions.CultureInvariant);
    // When a task last ran, kept as the newest any account saw, so a slot run in one account is not caught up in another.
    internal static readonly string[] RunFields = ["lastRunAt", "lastScheduledFor"];
    // Kept per account and never copied: the definition path (the app sets it on load), an import's origin (it brings
    // the import rules with it) and what the app observed of the runs there.
    internal static readonly string[] OwnFields = ["filePath", "importedFrom", "observedToolUse"];
    private static readonly string[] Strings = ["displayName", "migratedToRemoteAt", "migratedFromSpaceId", "cronExpression",
        "model", "lastRunAt", "lastScheduledFor", "missedRunScanFloor", "spaceId", "cwd", "sourceBranch", "notifySessionId"];
    private static readonly string[] Booleans = ["migratedFromRemote", "useWorktree", "disableJitter", "dispatchSubscribed"];
    private static readonly string[] StringArrays = ["userSelectedFolders", "userSelectedFiles", "userSelectedProjectUuids",
        "chromeAllowedDomains"];
    private static readonly string[] ChromeModes = ["ask", "skip_all_permission_checks", "follow_a_plan"];
    private static readonly string[] PermissionModes = ["default", "acceptEdits", "plan", "bypassPermissions", "dontAsk", "auto"];
    private static readonly JsonSerializerOptions WriteOptions = new()
    { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    internal static bool IsSafeId(string? id) => id is not null && SafeId.IsMatch(id);

    internal static string PathFor(string sessionsRoot, string key)
    {
        var parts = key.Split('/');
        return System.IO.Path.Combine(sessionsRoot, parts[0], parts[1], FileName);
    }

    /// <summary>Reads one list. A file being written, cut short or not passing the app's schema reads as unreadable,
    /// never as empty.</summary>
    internal static ClaudeScheduledFile Read(string key, string path)
    {
        var none = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return new(key, path, ClaudeScheduledReadStatus.Missing, null, null, none, null);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0 || info.Length > MaxBytes)
                return new(key, path, ClaudeScheduledReadStatus.Unreadable, null, null, none, null, "redirected_or_large");
            byte[] bytes;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                if (stream.Length > MaxBytes) return new(key, path, ClaudeScheduledReadStatus.Unreadable, null, null, none, null, "large");
                bytes = new byte[stream.Length];
                stream.ReadExactly(bytes);
            }
            return Parse(key, path, bytes, new DateTimeOffset(info.LastWriteTimeUtc, TimeSpan.Zero));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return new(key, path, ClaudeScheduledReadStatus.Unreadable, null, null, none, null, "io");
        }
    }

    internal static ClaudeScheduledFile Parse(string key, string path, byte[] bytes, DateTimeOffset? writtenAt)
    {
        var none = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        ClaudeScheduledFile Unreadable(string why) => new(key, path, ClaudeScheduledReadStatus.Unreadable, bytes, null, none, writtenAt, why);
        // The app parses the text as is: a byte order mark fails it.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Unreadable("bom");
        JsonNode? root;
        try { root = JsonNode.Parse(bytes); }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        { return Unreadable("json"); }
        if (!IsValid(root)) return Unreadable("schema");
        var entries = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var item in root!["scheduledTasks"]!.AsArray())
        {
            var entry = item!.AsObject();
            var id = entry["id"]!.GetValue<string>();
            if (!IsSafeId(id)) continue;
            // Two entries of one id: which one the app keeps is not this widget's to guess.
            if (!entries.TryAdd(id, entry)) return Unreadable("duplicate");
        }
        return new(key, path, ClaudeScheduledReadStatus.Ok, bytes, root.AsObject(), entries, writtenAt);
    }

    /// <summary>The app's file schema: an object whose <c>scheduledTasks</c> is an array of valid entries. Its other
    /// fields fall back to nothing when they do not parse, so they never fail the file.</summary>
    internal static bool IsValid(JsonNode? root) =>
        root is JsonObject file && file["scheduledTasks"] is JsonArray tasks && tasks.All(IsValidEntry);

    /// <summary>The app's entry schema (Claude 6.28899). Fields that fall back to nothing on a bad value are not
    /// checked; a JSON null is a bad value for every optional field.</summary>
    internal static bool IsValidEntry(JsonNode? node)
    {
        if (node is not JsonObject entry) return false;
        if (!IsString(entry, "id", required: true) || !IsBoolean(entry, "enabled", required: true)
            || !IsString(entry, "filePath", required: true) || !IsNumber(entry, "createdAt", required: true)) return false;
        if (!Strings.All(name => IsString(entry, name)) || !Booleans.All(name => IsBoolean(entry, name))
            || !StringArrays.All(name => IsStringArray(entry, name)) || !IsNumber(entry, "fireAt")
            || !IsOneOf(entry, "chromePermissionMode", ChromeModes) || !IsOneOf(entry, "permissionMode", PermissionModes))
            return false;
        if (entry.TryGetPropertyValue("approvedPermissions", out var approved))
        {
            if (approved is not JsonArray permissions) return false;
            foreach (var item in permissions)
                if (item is not JsonObject permission || !IsString(permission, "toolName", required: true)
                    || !IsString(permission, "ruleContent")) return false;
        }
        if (entry.TryGetPropertyValue("watcher", out var watching))
        {
            if (watching is not JsonObject watcher || !IsString(watcher, "scriptPath", required: true)
                || !IsStringArray(watcher, "mcpTools", required: true) || !IsString(watcher, "scriptSha256")
                || !IsString(watcher, "description") || !IsNumber(watcher, "fireThreshold")) return false;
        }
        return true;
    }

    private static bool IsString(JsonObject entry, string name, bool required = false) =>
        entry.TryGetPropertyValue(name, out var value) ? value is JsonValue text && text.GetValueKind() == JsonValueKind.String : !required;

    private static bool IsNumber(JsonObject entry, string name, bool required = false) =>
        entry.TryGetPropertyValue(name, out var value) ? value is JsonValue number && number.GetValueKind() == JsonValueKind.Number : !required;

    private static bool IsBoolean(JsonObject entry, string name, bool required = false) =>
        entry.TryGetPropertyValue(name, out var value)
            ? value is JsonValue flag && flag.GetValueKind() is JsonValueKind.True or JsonValueKind.False : !required;

    private static bool IsStringArray(JsonObject entry, string name, bool required = false) =>
        entry.TryGetPropertyValue(name, out var value)
            ? value is JsonArray items && items.All(item => item is JsonValue text && text.GetValueKind() == JsonValueKind.String)
            : !required;

    private static bool IsOneOf(JsonObject entry, string name, string[] allowed) =>
        !entry.TryGetPropertyValue(name, out var value)
        || value is JsonValue text && text.GetValueKind() == JsonValueKind.String && allowed.Contains(text.GetValue<string>());

    /// <summary>What a task is, the same in every account: the entry without its run times and its own fields.</summary>
    internal static JsonObject Content(JsonObject entry)
    {
        var content = new JsonObject();
        foreach (var (name, value) in entry)
            if (!RunFields.Contains(name) && !OwnFields.Contains(name)) content[name] = value?.DeepClone();
        return content;
    }

    /// <summary>A fingerprint of a JSON value that ignores the order of object members.</summary>
    internal static string Hash(JsonNode? node)
    {
        var text = new StringBuilder();
        Canonical(node, text);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())))[..32];
    }

    private static void Canonical(JsonNode? node, StringBuilder text)
    {
        switch (node)
        {
            case null: text.Append("null"); break;
            case JsonObject members:
                text.Append('{');
                var first = true;
                foreach (var (name, value) in members.OrderBy(member => member.Key, StringComparer.Ordinal))
                {
                    if (!first) text.Append(',');
                    first = false;
                    text.Append(JsonSerializer.Serialize(name)).Append(':');
                    Canonical(value, text);
                }
                text.Append('}');
                break;
            case JsonArray items:
                text.Append('[');
                for (var index = 0; index < items.Count; index++)
                {
                    if (index > 0) text.Append(',');
                    Canonical(items[index], text);
                }
                text.Append(']');
                break;
            default: text.Append(node.ToJsonString()); break;
        }
    }

    /// <summary>The newer of two run times (ISO text); the other when one does not parse.</summary>
    internal static string? Newer(string? first, string? second)
    {
        if (first is null) return second;
        if (second is null) return first;
        var a = DateTimeOffset.TryParse(first, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at);
        var b = DateTimeOffset.TryParse(second, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var bt);
        if (!a) return b ? second : first;
        if (!b) return first;
        return bt > at ? second : first;
    }

    internal static string? Text(JsonObject entry, string name) =>
        entry[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    internal static byte[] Serialize(JsonObject root) => Encoding.UTF8.GetBytes(root.ToJsonString(WriteOptions));
}

/// <summary>
/// What this widget keeps to align scheduled tasks, in its own folder (owner-only): per account the entries it last saw
/// there (or wrote), and per task the newest change any account made to it, a deletion included. Written whole and
/// atomically. The tasks' prompts live in their definitions and are never kept.
/// </summary>
internal sealed class ClaudeScheduledState
{
    internal Dictionary<string, AccountState> Accounts { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, TaskChange> Tasks { get; } = new(StringComparer.Ordinal);
    // The mapped accounts and their labels, for the plan command.
    internal Dictionary<string, string> Labels { get; } = new(StringComparer.Ordinal);

    internal sealed class AccountState
    {
        internal Dictionary<string, string> Entries { get; } = new(StringComparer.Ordinal);
        internal DateTimeOffset Seen { get; set; }
        internal LastWrite? Write { get; set; }
    }

    /// <summary>This widget's last write to an account: when, and what each task it changed there was before (null:
    /// absent). Uncertain once the app came to the account so soon after it that it may have read the list before.</summary>
    internal sealed record LastWrite(DateTimeOffset At, Dictionary<string, string?> Before, bool Uncertain = false);

    /// <summary>The newest change to a task: made by which account, when, and what it is now (null: deleted).</summary>
    internal sealed record TaskChange(DateTimeOffset At, string By, JsonObject? Content, string? FilePath)
    {
        internal bool Deleted => Content is null;
    }

    internal ClaudeScheduledState Clone() => Load(Save()) ?? throw new InvalidDataException();

    internal JsonObject Save()
    {
        var accounts = new JsonObject();
        foreach (var (key, account) in Accounts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var entries = new JsonObject();
            foreach (var (id, hash) in account.Entries.OrderBy(pair => pair.Key, StringComparer.Ordinal)) entries[id] = hash;
            var value = new JsonObject { ["seen"] = account.Seen.ToString("O", CultureInfo.InvariantCulture), ["entries"] = entries };
            if (account.Write is { } write)
            {
                var before = new JsonObject();
                foreach (var (id, hash) in write.Before.OrderBy(pair => pair.Key, StringComparer.Ordinal)) before[id] = hash;
                value["write"] = new JsonObject
                {
                    ["at"] = write.At.ToString("O", CultureInfo.InvariantCulture), ["before"] = before, ["uncertain"] = write.Uncertain
                };
            }
            accounts[key] = value;
        }
        var tasks = new JsonObject();
        foreach (var (id, change) in Tasks.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            tasks[id] = new JsonObject
            {
                ["at"] = change.At.ToString("O", CultureInfo.InvariantCulture), ["by"] = change.By,
                ["entry"] = change.Content?.DeepClone(), ["filePath"] = change.FilePath
            };
        var labels = new JsonObject();
        foreach (var (key, label) in Labels.OrderBy(pair => pair.Key, StringComparer.Ordinal)) labels[key] = label;
        return new JsonObject { ["version"] = 1, ["accounts"] = accounts, ["tasks"] = tasks, ["labels"] = labels };
    }

    /// <summary>Null when the kept state cannot be read: nothing is then written until it can.</summary>
    internal static ClaudeScheduledState? Load(JsonObject root)
    {
        try
        {
            if (root["version"]?.GetValue<int>() != 1) return null;
            var state = new ClaudeScheduledState();
            foreach (var (key, node) in root["accounts"]!.AsObject())
            {
                var value = node!.AsObject();
                var account = new AccountState { Seen = DateTimeOffset.Parse(value["seen"]!.GetValue<string>(), CultureInfo.InvariantCulture) };
                foreach (var (id, hash) in value["entries"]!.AsObject()) account.Entries[id] = hash!.GetValue<string>();
                if (value["write"] is JsonObject write)
                {
                    var before = new Dictionary<string, string?>(StringComparer.Ordinal);
                    foreach (var (id, hash) in write["before"]!.AsObject()) before[id] = hash?.GetValue<string>();
                    account.Write = new(DateTimeOffset.Parse(write["at"]!.GetValue<string>(), CultureInfo.InvariantCulture), before,
                        write["uncertain"]?.GetValue<bool>() == true);
                }
                state.Accounts[key] = account;
            }
            foreach (var (id, node) in root["tasks"]!.AsObject())
            {
                var value = node!.AsObject();
                state.Tasks[id] = new(DateTimeOffset.Parse(value["at"]!.GetValue<string>(), CultureInfo.InvariantCulture),
                    value["by"]!.GetValue<string>(), value["entry"] is JsonObject entry ? (JsonObject)entry.DeepClone() : null,
                    value["filePath"]?.GetValue<string>());
            }
            if (root["labels"] is JsonObject labels)
                foreach (var (key, label) in labels) state.Labels[key] = label!.GetValue<string>();
            return state;
        }
        catch (Exception exception) when (exception is InvalidOperationException or FormatException or NullReferenceException
                                              or JsonException or KeyNotFoundException)
        { return null; }
    }
}

/// <summary>What aligning would write to one account: the list as it should be, and which tasks it creates, changes,
/// removes or only brings the run times of.</summary>
internal sealed record ClaudeScheduledChange(string Key, ClaudeScheduledFile File, JsonObject Root,
    IReadOnlyList<string> Created, IReadOnlyList<string> Updated, IReadOnlyList<string> Deleted, IReadOnlyList<string> Runs,
    Dictionary<string, string?> Before, Dictionary<string, string> After);

/// <summary>What the seed fills, skips and could not tell apart.</summary>
internal sealed record ClaudeScheduledSeed(string Status, IReadOnlyList<(string Id, IReadOnlyList<string> Into)> Filled,
    IReadOnlyList<(string Id, IReadOnlyList<string> Accounts)> Conflicts, IReadOnlyList<(string Id, string Reason)> Skipped,
    IReadOnlyList<string> Unreadable);

/// <summary>What one pass did.</summary>
internal sealed record ClaudeScheduledPass(bool First, int Written, IReadOnlyList<string> Unreadable,
    IReadOnlyList<string> Waiting, string? Failure, ClaudeScheduledSeed? Seed);

/// <summary>
/// Aligns the scheduled-task lists of the mapped accounts. Each pass reads every list, records what changed in each since
/// it was last seen as that account's change (the newest change to a task wins, a deletion too), and writes each list the
/// caller lets it (another account's while the app runs, the one it shows only once it is closed) so it holds every task
/// as last changed, each with the newest run times any account saw. The first pass only takes what is there as the
/// starting point. Lists that do not read are neither written nor counted from; a task without its definition folder is
/// not copied.
/// </summary>
internal sealed class ClaudeScheduledSync
{
    // How close to this widget's last write to an account the app may come to it and still have read the list before.
    internal static readonly TimeSpan LoadMargin = TimeSpan.FromMinutes(2);
    // How old the kept times the lists were last looked at may get before a pass that changed nothing saves them.
    private static readonly TimeSpan SeenRefresh = TimeSpan.FromMinutes(30);
    private const int Backups = 5;
    internal const string StoreFolder = "scheduled-tasks";
    private readonly string _sessionsRoot;
    private readonly string _store;
    private readonly string? _definitions;
    private readonly Func<IDisposable> _lease;

    internal ClaudeScheduledSync(string sessionsRoot, string storeDirectory, string? definitionsRoot, Func<IDisposable> lease)
    {
        _sessionsRoot = sessionsRoot; _store = storeDirectory; _definitions = definitionsRoot; _lease = lease;
    }

    internal string SessionsRoot => _sessionsRoot;
    private string StatePath => Path.Combine(_store, "state.json");

    /// <summary>Each mapped account's list whose folder is there. Folders are never made.</summary>
    internal IReadOnlyList<ClaudeScheduledFile> Observe(IReadOnlyList<ClaudeShelfAccount> accounts)
    {
        var files = new List<ClaudeScheduledFile>();
        foreach (var account in accounts.DistinctBy(account => account.Key).OrderBy(account => account.Key, StringComparer.Ordinal))
        {
            var folder = Path.Combine(_sessionsRoot, account.AccountUuid, account.OrgUuid);
            try
            {
                var info = new DirectoryInfo(folder);
                if (!info.Exists) continue;
                if ((info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    files.Add(new(account.Key, Path.Combine(folder, ClaudeScheduledRegistry.FileName), ClaudeScheduledReadStatus.Unreadable,
                        null, null, new Dictionary<string, JsonObject>(), null, "redirected"));
                    continue;
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            files.Add(ClaudeScheduledRegistry.Read(account.Key, Path.Combine(folder, ClaudeScheduledRegistry.FileName)));
        }
        return files;
    }

    /// <summary>A cheap fingerprint of the lists on disk, so a pass runs as soon as one changes.</summary>
    internal string Stamps(IReadOnlyList<ClaudeShelfAccount> accounts)
    {
        var text = new StringBuilder();
        foreach (var account in accounts.DistinctBy(account => account.Key).OrderBy(account => account.Key, StringComparer.Ordinal))
        {
            try
            {
                var info = new FileInfo(Path.Combine(_sessionsRoot, account.AccountUuid, account.OrgUuid, ClaudeScheduledRegistry.FileName));
                text.Append(account.Key).Append('=').Append(info.Exists ? info.Length + ":" + info.LastWriteTimeUtc.Ticks : "-").Append(';');
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { text.Append(account.Key).Append("=?;"); }
        }
        return text.ToString();
    }

    /// <summary>The kept state: none yet (a first pass), or null when it cannot be read.</summary>
    internal (ClaudeScheduledState? State, bool Exists) LoadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return (new ClaudeScheduledState(), false);
            return (JsonNode.Parse(File.ReadAllBytes(StatePath)) is JsonObject root ? ClaudeScheduledState.Load(root) : null, true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                                              or ArgumentException or InvalidOperationException)
        { return (null, true); }
    }

    internal bool SaveState(ClaudeScheduledState state)
    {
        try
        {
            EnsureStore();
            var temporary = StatePath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                Commit(temporary, Encoding.UTF8.GetBytes(state.Save().ToJsonString(new JsonSerializerOptions { WriteIndented = true })));
                File.Move(temporary, StatePath, overwrite: true);
            }
            finally { try { File.Delete(temporary); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } }
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException)
        { return false; }
    }

    /// <summary>The kept state without the times each list was last looked at.</summary>
    private static string Shape(ClaudeScheduledState state)
    {
        var saved = state.Save();
        foreach (var (_, account) in saved["accounts"]!.AsObject()) account!.AsObject().Remove("seen");
        return saved.ToJsonString();
    }

    private void EnsureStore()
    {
        var info = new DirectoryInfo(_store);
        if (!info.Exists) AntigravityAccountStore.CreatePrivateDirectory(_store);
        else if ((info.Attributes & FileAttributes.ReparsePoint) != 0) throw new IOException("The scheduled-task store is redirected.");
    }

    private static void Commit(string path, byte[] bytes)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
    }

    /// <summary>The app came to an account at <paramref name="at"/>: when that was close to this widget's last write
    /// there, it may have read the list before the write, and then saves its older copy over it. Until the next write, a
    /// task found back as it was before that write is taken for that, not for a change made there.</summary>
    internal void NoteLoaded(string key, DateTimeOffset at)
    {
        var (state, exists) = LoadState();
        if (!exists || state is null || !state.Accounts.TryGetValue(key, out var account) || account.Write is not { } write
            || write.Uncertain || write.At < at - LoadMargin) return;
        account.Write = write with { Uncertain = true };
        SaveState(state);
    }

    /// <summary>A list that was there and is gone reads as unreadable, and its account starts over once it is back: what
    /// it held is not taken for deleted (the app, finding no list, would save only what is made after). So does a list
    /// the app could not load either (anything but a read that failed for now): its next save there keeps only what is
    /// made after, which is a new starting point, not deletions.</summary>
    internal static IReadOnlyList<ClaudeScheduledFile> Settle(ClaudeScheduledState state, IReadOnlyList<ClaudeScheduledFile> files) =>
        files.Select(file =>
        {
            if (!state.Accounts.TryGetValue(file.Key, out var account)) return file;
            if (file.Status == ClaudeScheduledReadStatus.Unreadable && file.Problem != "io") state.Accounts.Remove(file.Key);
            if (file.Status != ClaudeScheduledReadStatus.Missing || account.Entries.Count == 0) return file;
            state.Accounts.Remove(file.Key);
            return file with { Status = ClaudeScheduledReadStatus.Unreadable, Problem = "gone" };
        }).ToArray();

    /// <summary>Records, for each list that reads, what changed since it was last seen; a list seen for the first time
    /// is only taken as it is.</summary>
    internal static void Record(ClaudeScheduledState state, IReadOnlyList<ClaudeScheduledFile> files, DateTimeOffset now)
    {
        foreach (var file in files)
        {
            if (!file.Readable) continue;
            var current = file.Entries.ToDictionary(pair => pair.Key,
                pair => ClaudeScheduledRegistry.Hash(ClaudeScheduledRegistry.Content(pair.Value)), StringComparer.Ordinal);
            if (!state.Accounts.TryGetValue(file.Key, out var account))
            {
                account = new ClaudeScheduledState.AccountState();
                foreach (var (id, hash) in current) account.Entries[id] = hash;
                account.Seen = now;
                state.Accounts[file.Key] = account;
                continue;
            }
            var at = file.WrittenAt is { } written ? (written < account.Seen ? account.Seen : written > now ? now : written) : now;
            foreach (var id in current.Keys.Union(account.Entries.Keys).ToArray())
            {
                var hash = current.GetValueOrDefault(id);
                if (hash == account.Entries.GetValueOrDefault(id)) continue;
                // The app may have kept its copy from before this widget's write: back as before is not a change.
                if (account.Write is { Uncertain: true } write && write.Before.TryGetValue(id, out var before) && before == hash) continue;
                var change = new ClaudeScheduledState.TaskChange(at, file.Key,
                    hash is null ? null : ClaudeScheduledRegistry.Content(file.Entries[id]),
                    hash is null ? null : ClaudeScheduledRegistry.Text(file.Entries[id], "filePath"));
                if (!state.Tasks.TryGetValue(id, out var known) || at > known.At
                    || at == known.At && string.CompareOrdinal(file.Key, known.By) > 0)
                    state.Tasks[id] = change;
            }
            account.Entries.Clear();
            foreach (var (id, hash) in current) account.Entries[id] = hash;
            account.Seen = now;
        }
    }

    internal bool HasDefinition(string id)
    {
        if (_definitions is null || !ClaudeScheduledRegistry.IsSafeId(id)) return false;
        try { return Directory.Exists(Path.Combine(_definitions, id)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>What each list that reads should become: every recorded task as last changed (a task without its
    /// definition folder left alone), and the newest run times on every task it holds. Other entries, their order and
    /// the file's other fields stay as they are.</summary>
    internal IReadOnlyList<ClaudeScheduledChange> Plan(ClaudeScheduledState state, IReadOnlyList<ClaudeScheduledFile> files)
    {
        var readable = files.Where(file => file.Readable).ToArray();
        var runs = new Dictionary<string, (string? Ran, string? Slot)>(StringComparer.Ordinal);
        foreach (var file in readable.Where(file => file.Status == ClaudeScheduledReadStatus.Ok))
            foreach (var (id, entry) in file.Entries)
            {
                var known = runs.GetValueOrDefault(id);
                runs[id] = (ClaudeScheduledRegistry.Newer(known.Ran, ClaudeScheduledRegistry.Text(entry, "lastRunAt")),
                    ClaudeScheduledRegistry.Newer(known.Slot, ClaudeScheduledRegistry.Text(entry, "lastScheduledFor")));
            }
        var definitions = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool Defined(string id) => definitions.TryGetValue(id, out var found) ? found : definitions[id] = HasDefinition(id);
        var changes = new List<ClaudeScheduledChange>();
        foreach (var file in readable)
        {
            var root = file.Root is { } existing ? (JsonObject)existing.DeepClone() : new JsonObject { ["scheduledTasks"] = new JsonArray() };
            var tasks = root["scheduledTasks"]!.AsArray();
            var at = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < tasks.Count; index++)
                if (tasks[index]!["id"]!.GetValue<string>() is var id && ClaudeScheduledRegistry.IsSafeId(id)) at[id] = index;
            List<string> created = [], updated = [], deleted = [], ran = [];
            var before = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var (id, change) in state.Tasks.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                if (!ClaudeScheduledRegistry.IsSafeId(id)) continue;
                var has = at.TryGetValue(id, out var index);
                var hash = has ? ClaudeScheduledRegistry.Hash(ClaudeScheduledRegistry.Content(tasks[index]!.AsObject())) : null;
                if (change.Deleted)
                {
                    if (!has) continue;
                    before[id] = hash;
                    tasks[index] = null;
                    deleted.Add(id);
                    continue;
                }
                if (!Defined(id) || hash == ClaudeScheduledRegistry.Hash(change.Content)) continue;
                var entry = (JsonObject)change.Content!.DeepClone();
                if (has)
                {
                    // Each account keeps its own definition path, import origin and run observations, and run times that
                    // are newer than any other account's are brought below.
                    var old = tasks[index]!.AsObject();
                    foreach (var name in ClaudeScheduledRegistry.OwnFields.Concat(ClaudeScheduledRegistry.RunFields))
                        if (old[name] is { } value) entry[name] = value.DeepClone();
                    tasks[index] = entry;
                    updated.Add(id);
                }
                else
                {
                    entry["filePath"] = change.FilePath ?? Path.Combine(_definitions!, id, "SKILL.md");
                    tasks.Add(entry);
                    at[id] = tasks.Count - 1;
                    created.Add(id);
                }
                before[id] = hash;
            }
            for (var index = 0; index < tasks.Count; index++)
            {
                if (tasks[index] is not JsonObject entry || entry["id"]!.GetValue<string>() is not { } id
                    || !ClaudeScheduledRegistry.IsSafeId(id) || !runs.TryGetValue(id, out var newest) || !Defined(id)) continue;
                var moved = false;
                foreach (var (name, value) in new[] { ("lastRunAt", newest.Ran), ("lastScheduledFor", newest.Slot) })
                {
                    var own = ClaudeScheduledRegistry.Text(entry, name);
                    if (value is null || value == own || ClaudeScheduledRegistry.Newer(own, value) != value) continue;
                    entry[name] = value;
                    moved = true;
                }
                if (moved && !created.Contains(id) && !updated.Contains(id)) ran.Add(id);
            }
            for (var index = tasks.Count - 1; index >= 0; index--) if (tasks[index] is null) tasks.RemoveAt(index);
            if (created.Count + updated.Count + deleted.Count + ran.Count == 0) continue;
            var after = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var item in tasks)
                if (item!["id"]!.GetValue<string>() is var id && ClaudeScheduledRegistry.IsSafeId(id))
                    after[id] = ClaudeScheduledRegistry.Hash(ClaudeScheduledRegistry.Content(item.AsObject()));
            changes.Add(new(file.Key, file, root, created, updated, deleted, ran, before, after));
        }
        return changes;
    }

    /// <summary>Records, as made now, every task some accounts hold and others lack, so they all get it; a task held
    /// with differing contents, one deleted since, or one without its definition folder is skipped. It deletes nothing.
    /// Refused while a mapped list does not read.</summary>
    internal ClaudeScheduledSeed Seed(ClaudeScheduledState state, IReadOnlyList<ClaudeScheduledFile> files, DateTimeOffset now,
        IReadOnlyDictionary<string, string> labels)
    {
        string Label(string key) => labels.GetValueOrDefault(key) ?? key;
        var unreadable = files.Where(file => !file.Readable).Select(file => Label(file.Key)).ToArray();
        List<(string, IReadOnlyList<string>)> filled = [];
        List<(string, IReadOnlyList<string>)> conflicts = [];
        List<(string, string)> skipped = [];
        if (unreadable.Length > 0) return new("unreadable", filled, conflicts, skipped, unreadable);
        foreach (var id in files.SelectMany(file => file.Entries.Keys).Distinct().Order(StringComparer.Ordinal))
        {
            var holders = files.Where(file => file.Entries.ContainsKey(id)).ToArray();
            var lacking = files.Where(file => !file.Entries.ContainsKey(id)).Select(file => file.Key).ToArray();
            if (lacking.Length == 0) continue;
            if (state.Tasks.TryGetValue(id, out var known) && known.Deleted) { skipped.Add((id, "deleted")); continue; }
            if (!HasDefinition(id)) { skipped.Add((id, "no_definition")); continue; }
            var contents = holders.Select(file => ClaudeScheduledRegistry.Content(file.Entries[id])).ToArray();
            if (contents.Select(ClaudeScheduledRegistry.Hash).Distinct().Count() > 1)
            {
                conflicts.Add((id, holders.Select(file => Label(file.Key)).ToArray()));
                continue;
            }
            state.Tasks[id] = new(now, holders[0].Key, contents[0], ClaudeScheduledRegistry.Text(holders[0].Entries[id], "filePath"));
            filled.Add((id, lacking.Select(Label).ToArray()));
        }
        return new("ok", filled, conflicts, skipped, unreadable);
    }

    /// <summary>One pass: reads every list, records what changed, and writes each list <paramref name="mayWrite"/>
    /// allows (asked again just before each file), under the account lease. A list that changed after it was read is
    /// left for the next pass. Before a list is replaced, its previous content is kept beside the state.</summary>
    internal ClaudeScheduledPass Pass(IReadOnlyList<ClaudeShelfAccount> accounts, Func<string, bool> mayWrite, DateTimeOffset now,
        bool seed = false)
    {
        var labels = accounts.DistinctBy(account => account.Key).ToDictionary(account => account.Key, account => account.Label, StringComparer.Ordinal);
        var observed = Observe(accounts);
        var (state, exists) = LoadState();
        if (state is null)
            return new(false, 0, observed.Where(file => !file.Readable).Select(file => labels.GetValueOrDefault(file.Key) ?? file.Key).ToArray(),
                [], "store_unreadable", seed ? new("store_unreadable", [], [], [], []) : null);
        var kept = Shape(state);
        var keptAt = state.Accounts.Values.Select(account => (DateTimeOffset?)account.Seen).Max();
        var files = Settle(state, observed);
        var unreadable = files.Where(file => !file.Readable).Select(file => labels.GetValueOrDefault(file.Key) ?? file.Key).ToArray();
        Record(state, files, now);
        state.Labels.Clear();
        foreach (var (key, label) in labels) state.Labels[key] = label;
        var seeded = seed ? Seed(state, files, now, labels) : null;
        // Only when something besides the times the lists were last looked at changed, or those are getting old.
        var save = !exists || Shape(state) != kept || keptAt is not { } at || now - at >= SeenRefresh || at > now;
        if (save && !SaveState(state)) return new(!exists, 0, unreadable, [], "store_unwritable", seeded);
        // The first pass takes what is there as the starting point and changes nothing.
        if (!exists) return new(true, 0, unreadable, [], null, seeded);
        var written = 0;
        string? failure = null;
        List<string> waiting = [];
        var changes = Plan(state, files);
        if (changes.Count > 0)
        {
            IDisposable? lease = null;
            try
            {
                lease = _lease();
                foreach (var change in changes)
                {
                    if (!mayWrite(change.Key)) { waiting.Add(labels.GetValueOrDefault(change.Key) ?? change.Key); continue; }
                    try
                    {
                        if (Write(change, () => mayWrite(change.Key), now))
                        {
                            written++;
                            var account = state.Accounts[change.Key];
                            account.Entries.Clear();
                            foreach (var (id, hash) in change.After) account.Entries[id] = hash;
                            account.Seen = now;
                            account.Write = new(now, change.Before);
                        }
                        else waiting.Add(labels.GetValueOrDefault(change.Key) ?? change.Key);
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        failure = exception.Message;
                        waiting.Add(labels.GetValueOrDefault(change.Key) ?? change.Key);
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
                                                  or TimeoutException)
            {
                // The installer or an account operation holds the lease: every list waits for the next pass.
                failure = exception.Message;
                waiting.AddRange(changes.Select(change => labels.GetValueOrDefault(change.Key) ?? change.Key));
            }
            finally { lease?.Dispose(); }
            if (written > 0 && !SaveState(state)) failure ??= "store_unwritable";
        }
        return new(false, written, unreadable, waiting.Distinct().ToArray(), failure, seeded);
    }

    /// <summary>Replaces one list atomically: the new list is written beside it and moved over it only while it still
    /// holds what was read and <paramref name="mayWrite"/> still allows it. False when it changed or may no longer be
    /// written.</summary>
    private bool Write(ClaudeScheduledChange change, Func<bool> mayWrite, DateTimeOffset now)
    {
        var bytes = ClaudeScheduledRegistry.Serialize(change.Root);
        // What is written must read as the app reads it, or the app would load none of that account's tasks.
        if (!ClaudeScheduledRegistry.IsValid(JsonNode.Parse(bytes))) throw new IOException("The aligned list does not pass the app's schema.");
        var path = change.File.Path;
        var folder = Path.GetDirectoryName(path)!;
        if (!Same(path, change.File.Bytes)) return false;
        // A partial a stopped widget left behind.
        foreach (var stale in Directory.GetFiles(folder, ".scheduled-tasks.*.widget-sync.partial"))
            try { if (File.GetLastWriteTimeUtc(stale) < now.UtcDateTime.AddHours(-1)) File.Delete(stale); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        if (change.File.Bytes is { } previous) KeepBackup(change.Key, previous, now);
        // Not a session record, tombstone or temporary record name the app's recovery looks at.
        var partial = Path.Combine(folder, ".scheduled-tasks." + Guid.NewGuid().ToString("N") + ".widget-sync.partial");
        try
        {
            Commit(partial, bytes);
            if (!mayWrite() || !Same(path, change.File.Bytes)) return false;
            File.Move(partial, path, overwrite: true);
            return true;
        }
        finally { try { File.Delete(partial); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } }
    }

    private static bool Same(string path, byte[]? expected)
    {
        try
        {
            if (!File.Exists(path)) return expected is null;
            return expected is not null && File.ReadAllBytes(path).AsSpan().SequenceEqual(expected);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private void KeepBackup(string key, byte[] bytes, DateTimeOffset now)
    {
        EnsureStore();
        var folder = Directory.CreateDirectory(Path.Combine(_store, "backups", key.Replace('/', '_'))).FullName;
        var name = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..6] + ".json";
        Commit(Path.Combine(folder, name), bytes);
        foreach (var old in Directory.GetFiles(folder, "*.json").Order(StringComparer.Ordinal).SkipLast(Backups))
            try { File.Delete(old); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    internal string BackupFolder(string key) => Path.Combine(_store, "backups", key.Replace('/', '_'));
}

/// <summary>Where <c>--claude-scheduled-seed</c> asks the running widget to seed: a request and an answer event and the
/// shared memory carrying both, named after the widget's ledger folder (a test's folder names its own; the path reads
/// the same inside a packaged app). Named kernel objects reach the widget from a process the app runs, where files it
/// writes could be virtualized.</summary>
internal sealed class ClaudeScheduledSeedChannel : IDisposable
{
    private const long MailboxBytes = 1024 * 1024;
    private const int AnswerOffset = 4096;
    private readonly EventWaitHandle _requested, _answered;
    private readonly MemoryMappedFile _mailbox;
    private readonly MemoryMappedViewAccessor _view;

    private ClaudeScheduledSeedChannel(EventWaitHandle requested, EventWaitHandle answered, MemoryMappedFile mailbox)
    {
        _requested = requested; _answered = answered; _mailbox = mailbox; _view = mailbox.CreateViewAccessor(0, MailboxBytes);
    }

    private static string Suffix(string ledgerDirectory) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(ledgerDirectory)).ToLowerInvariant())))[..16];

    private static (string Requested, string Answered, string Mailbox, string Lock) Names(string ledgerDirectory)
    {
        var suffix = Suffix(ledgerDirectory);
        return (@"Local\CodexWeeklyUsageIndicator.ScheduledSeed." + suffix, @"Local\CodexWeeklyUsageIndicator.ScheduledSeeded." + suffix,
            @"Local\CodexWeeklyUsageIndicator.ScheduledSeedMailbox." + suffix, @"Local\CodexWeeklyUsageIndicator.ScheduledSeedLock." + suffix);
    }

    internal static ClaudeScheduledSeedChannel? Open(string ledgerDirectory)
    {
        EventWaitHandle? requested = null, answered = null;
        MemoryMappedFile? mailbox = null;
        try
        {
            var names = Names(ledgerDirectory);
            requested = new EventWaitHandle(false, EventResetMode.AutoReset, names.Requested);
            answered = new EventWaitHandle(false, EventResetMode.AutoReset, names.Answered);
            mailbox = MemoryMappedFile.CreateOrOpen(names.Mailbox, MailboxBytes, MemoryMappedFileAccess.ReadWrite,
                MemoryMappedFileOptions.DelayAllocatePages, HandleInheritability.None);
            return new(requested, answered, mailbox);
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException or WaitHandleCannotBeOpenedException)
        {
            requested?.Dispose(); answered?.Dispose(); mailbox?.Dispose();
            return null;
        }
    }

    /// <summary>The id of a seed request waiting, if any.</summary>
    internal string? Request()
    {
        if (!_requested.WaitOne(0)) return null;
        try
        {
            return ClaudeSessionSync.ReadSlot(_view, 0, AnswerOffset) is { } text && JsonNode.Parse(text) is JsonObject request
                   && request["id"] is JsonValue value && value.TryGetValue<string>(out var id) && id.Length is > 0 and <= 64 ? id : null;
        }
        catch (JsonException) { return null; }
    }

    internal void Answer(string id, string answer)
    {
        ClaudeSessionSync.WriteSlot(_view, AnswerOffset, MailboxBytes - AnswerOffset, Encoding.UTF8.GetBytes(id + "\n" + answer));
        _answered.Set();
    }

    /// <summary>Asks the running widget to seed and waits for its answer; null without a widget or an answer in time.</summary>
    internal static string? Ask(string ledgerDirectory, TimeSpan wait)
    {
        var names = Names(ledgerDirectory);
        try
        {
            using var gate = new Mutex(false, names.Lock);
            bool owned;
            try { owned = gate.WaitOne(wait); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) return null;
            try
            {
                if (!EventWaitHandle.TryOpenExisting(names.Requested, out var requested)) return null;
                using (requested)
                {
                    if (!EventWaitHandle.TryOpenExisting(names.Answered, out var answered)) return null;
                    using (answered)
                    {
                        using var mailbox = MemoryMappedFile.OpenExisting(names.Mailbox, MemoryMappedFileRights.ReadWrite);
                        using var view = mailbox.CreateViewAccessor(0, MailboxBytes);
                        var id = Guid.NewGuid().ToString("N");
                        ClaudeSessionSync.WriteSlot(view, 0, AnswerOffset, JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["id"] = id }));
                        answered.Reset();
                        requested.Set();
                        var deadline = DateTime.UtcNow + wait;
                        while (deadline - DateTime.UtcNow is { Ticks: > 0 } left && answered.WaitOne(left))
                            if (ClaudeSessionSync.ReadSlot(view, AnswerOffset, MailboxBytes - AnswerOffset) is { } text
                                && text.StartsWith(id + "\n", StringComparison.Ordinal))
                                return text[(id.Length + 1)..];
                        return null;
                    }
                }
            }
            finally { gate.ReleaseMutex(); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or WaitHandleCannotBeOpenedException)
        { return null; }
    }

    public void Dispose() { _view.Dispose(); _mailbox.Dispose(); _requested.Dispose(); _answered.Dispose(); }
}

/// <summary>The two scheduled-task commands: a read-only plan, and a seed the running widget performs.</summary>
internal static class ClaudeScheduledCommand
{
    internal const string PlanFlag = "--claude-scheduled-plan";
    internal const string SeedFlag = "--claude-scheduled-seed";
    private static readonly JsonSerializerOptions Output = new()
    { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Prints, as UTF-8 JSON and without writing anything: each mapped list, whether a first pass is still to
    /// come (it would only take what is there, so the tasks held by some accounts alone are listed), what the next pass
    /// would write to each list, and what a seed would fill and skip.</summary>
    internal static int RunPlan()
    {
        var paths = ClaudeShelfPaths.CreateDefault();
        var sync = new ClaudeScheduledSync(paths.SessionsRoot, Path.Combine(paths.LedgerDirectory, ClaudeScheduledSync.StoreFolder),
            paths.ScheduledTasksRoot, () => new MemoryStream());
        var (kept, exists) = sync.LoadState();
        IReadOnlyList<ClaudeShelfAccount> accounts;
        try { accounts = ClaudeShelfAccounts.Resolve(new ClaudeProfileStore().ListProfiles(), paths.LedgerDirectory, keepCache: false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
                                              or JsonException)
        { accounts = []; }
        return Print(Describe(sync, accounts, kept, exists, DateTimeOffset.Now), kept is null ? 1 : 0);
    }

    internal static JsonObject Describe(ClaudeScheduledSync sync, IReadOnlyList<ClaudeShelfAccount> accounts,
        ClaudeScheduledState? kept, bool exists, DateTimeOffset now)
    {
        if (kept is null) return new JsonObject { ["status"] = "store_unreadable" };
        var labels = accounts.DistinctBy(account => account.Key).ToDictionary(account => account.Key, account => account.Label, StringComparer.Ordinal);
        string Label(string key) => labels.GetValueOrDefault(key) ?? kept.Labels.GetValueOrDefault(key) ?? key;
        var state = kept.Clone();
        var files = ClaudeScheduledSync.Settle(state, sync.Observe(accounts));
        ClaudeScheduledSync.Record(state, files, now);
        var result = new JsonObject
        {
            ["status"] = accounts.Count == 0 ? "no_accounts" : "ok",
            ["firstPass"] = !exists,
            ["lists"] = new JsonArray(files.Select(file => (JsonNode)new JsonObject
            {
                ["account"] = Label(file.Key), ["key"] = file.Key,
                ["read"] = file.Status switch
                {
                    ClaudeScheduledReadStatus.Ok => "ok", ClaudeScheduledReadStatus.Missing => "missing",
                    _ => "unreadable:" + file.Problem
                },
                ["tasks"] = new JsonArray(file.Entries.Keys.Order(StringComparer.Ordinal).Select(id => (JsonNode)id).ToArray()),
                ["known"] = kept.Accounts.ContainsKey(file.Key)
            }).ToArray())
        };
        if (!exists)
        {
            // What a first pass leaves where it is: tasks some lists hold and others lack.
            var readable = files.Where(file => file.Readable).ToArray();
            result["notSpreadByFirstPass"] = new JsonArray(readable.SelectMany(file => file.Entries.Keys).Distinct().Order(StringComparer.Ordinal)
                .Where(id => readable.Any(file => !file.Entries.ContainsKey(id)))
                .Select(id => (JsonNode)new JsonObject
                {
                    ["id"] = id,
                    ["in"] = new JsonArray(readable.Where(file => file.Entries.ContainsKey(id)).Select(file => (JsonNode)Label(file.Key)).ToArray()),
                    ["notIn"] = new JsonArray(readable.Where(file => !file.Entries.ContainsKey(id)).Select(file => (JsonNode)Label(file.Key)).ToArray())
                }).ToArray());
        }
        else
        {
            result["changes"] = new JsonArray(state.Tasks.Where(pair => !kept.Tasks.TryGetValue(pair.Key, out var before)
                    || before.At != pair.Value.At || before.By != pair.Value.By)
                .Select(pair => (JsonNode)new JsonObject
                {
                    ["id"] = pair.Key, ["change"] = pair.Value.Deleted ? "deleted" : "set", ["by"] = Label(pair.Value.By),
                    ["at"] = pair.Value.At.ToString("O", CultureInfo.InvariantCulture)
                }).ToArray());
            result["writes"] = Writes(sync.Plan(state, files), Label);
        }
        var seeded = state.Clone();
        var seed = sync.Seed(seeded, files, now, labels);
        result["seed"] = SeedJson(seed);
        result["seedWrites"] = exists ? Writes(sync.Plan(seeded, files), Label) : null;
        return result;
    }

    private static JsonArray Writes(IReadOnlyList<ClaudeScheduledChange> changes, Func<string, string> label) =>
        new(changes.Select(change => (JsonNode)new JsonObject
        {
            ["account"] = label(change.Key), ["key"] = change.Key,
            ["create"] = Ids(change.Created), ["update"] = Ids(change.Updated), ["delete"] = Ids(change.Deleted), ["runTimes"] = Ids(change.Runs)
        }).ToArray());

    private static JsonArray Ids(IEnumerable<string> ids) => new(ids.Select(id => (JsonNode)id).ToArray());

    internal static JsonObject SeedJson(ClaudeScheduledSeed seed) => new()
    {
        ["status"] = seed.Status,
        ["fill"] = new JsonArray(seed.Filled.Select(item => (JsonNode)new JsonObject
        { ["id"] = item.Id, ["into"] = new JsonArray(item.Into.Select(label => (JsonNode)label).ToArray()) }).ToArray()),
        ["conflicts"] = new JsonArray(seed.Conflicts.Select(item => (JsonNode)new JsonObject
        { ["id"] = item.Id, ["accounts"] = new JsonArray(item.Accounts.Select(label => (JsonNode)label).ToArray()) }).ToArray()),
        ["skipped"] = new JsonArray(seed.Skipped.Select(item => (JsonNode)new JsonObject { ["id"] = item.Id, ["reason"] = item.Reason }).ToArray()),
        ["unreadable"] = new JsonArray(seed.Unreadable.Select(label => (JsonNode)label).ToArray())
    };

    /// <summary>Asks the running widget to seed: every task some mapped accounts hold and others lack is filled into
    /// those (the account the app shows once it is closed); nothing is deleted. Prints the widget's answer.</summary>
    internal static int RunSeed()
    {
        var paths = ClaudeShelfPaths.CreateDefault();
        var answer = ClaudeScheduledSeedChannel.Ask(paths.LedgerDirectory, TimeSpan.FromSeconds(60));
        if (answer is null) return Print(new JsonObject { ["status"] = "widget_not_answering" }, 1);
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine(answer);
        return JsonNode.Parse(answer)?["status"]?.GetValue<string>() == "ok" ? 0 : 1;
    }

    private static int Print(JsonObject value, int code)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine(value.ToJsonString(Output));
        return code;
    }
}
