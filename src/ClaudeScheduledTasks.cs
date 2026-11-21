using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32.SafeHandles;

namespace WeeklyUsageIndicator;

internal enum ClaudeScheduledProblemKind { None, Unreviewed, Unreadable, Failed, TooLarge, Conflict, RemovedHere, Held }

/// <summary>Why scheduled tasks are not being aligned, or what aligning left for the user to settle (see
/// <see cref="ClaudeShelfService"/>).</summary>
internal sealed record ClaudeScheduledProblem(ClaudeScheduledProblemKind Kind, string? Detail = null);

internal enum ClaudeScheduledReadStatus { Missing, Ok, Unreadable }

/// <summary>One account's scheduled-task list as read now. <see cref="Entries"/> are the entries with ids the app keeps,
/// by id; <see cref="Bytes"/> the file as read (null when missing); <see cref="Stamp"/> its size and last write as read;
/// <see cref="FileId"/> and <see cref="FolderId"/> what the file and its folder are on disk.</summary>
internal sealed record ClaudeScheduledFile(string Key, string Path, ClaudeScheduledReadStatus Status, byte[]? Bytes,
    JsonObject? Root, IReadOnlyDictionary<string, JsonObject> Entries, DateTimeOffset? WrittenAt, string? Problem = null,
    string? Stamp = null, string? FileId = null, string? FolderId = null)
{
    internal bool Readable => Status != ClaudeScheduledReadStatus.Unreadable;
}

/// <summary>What the app's log says of its scheduled-task manager in this run (<see cref="ClaudeDesktopLogWatcher"/>):
/// whether every line of the run was read (<see cref="Known"/>), the list it has loaded (null: none, or one it is loading
/// while <see cref="Pending"/>), when its last line was, and, per list it loaded, when that load began. <see cref="Mark"/>
/// counts its lines.</summary>
internal sealed record ClaudeScheduledLogView(bool Known, string? Loaded, bool Pending, DateTimeOffset? LastLine,
    IReadOnlyDictionary<string, DateTimeOffset> Loads, long Mark)
{
    internal IEnumerable<string> Initialized => Loads.Keys;
}

/// <summary>Collects what the app's log says of its scheduled-task manager (6.28899: a <c>Reset</c> line when it lets
/// its list go, an <c>Initialized</c> line naming the list it loaded), for <see cref="ClaudeScheduledLogView"/>. After
/// a reset, and at the start of a run, it holds no list until the next load, which began reading after that reset.</summary>
internal sealed class ClaudeScheduledLog
{
    private readonly Dictionary<string, DateTimeOffset> _loads = new(StringComparer.Ordinal);
    private string? _loaded;
    private bool _pending;
    private DateTimeOffset? _boundary, _last;
    private long _mark;

    /// <summary>A run of the app read from its start.</summary>
    internal void Start(DateTimeOffset at) { _loaded = null; _pending = true; _boundary = at; _last = null; }

    /// <summary>The app is closed: it holds no list. The loads seen stay.</summary>
    internal void Close() { _loaded = null; _pending = false; _boundary = null; }

    internal void Reset(DateTimeOffset at) { _loaded = null; _pending = true; _boundary = at; _last = at; _mark++; }

    internal void Initialized(string key, DateTimeOffset at)
    {
        _loaded = key; _pending = false; _last = at; _mark++;
        if (_boundary is { } began && (!_loads.TryGetValue(key, out var known) || began > known)) _loads[key] = began;
    }

    internal ClaudeScheduledLogView View(bool known) =>
        new(known, _loaded, _pending, _last, new Dictionary<string, DateTimeOffset>(_loads, StringComparer.Ordinal), _mark);
}

/// <summary>A command asked of the running widget: <c>seed</c> (fill the tasks the accounts agree on into the ones
/// lacking them) or <c>remove</c> (delete tasks from every account), for <see cref="Tasks"/> (none: every task, seed only).</summary>
internal sealed record ClaudeScheduledRequest(string Op, IReadOnlyList<string> Tasks);

/// <summary>What a path is on disk with every link in it followed (its volume and file index), how many names it has,
/// and its size and last write, read through one handle.</summary>
internal static class ClaudeFileIdentity
{
    internal sealed record Info(string Id, uint Links, long Length, DateTime LastWriteUtc);

    internal static Info? Of(string path)
    {
        try
        {
            // FILE_READ_ATTRIBUTES, every share mode, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS (folders too).
            using var handle = CreateFile(Extended(path), 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);
            return handle.IsInvalid ? null : Of(handle);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    internal static Info? Of(SafeFileHandle handle)
    {
        if (!GetFileInformationByHandle(handle, out var information)) return null;
        var write = (long)((ulong)(uint)information.LastWriteTime.dwHighDateTime << 32 | (uint)information.LastWriteTime.dwLowDateTime);
        try
        {
            return new($"{information.VolumeSerialNumber:x8}-{information.FileIndexHigh:x8}{information.FileIndexLow:x8}",
                information.NumberOfLinks, (long)((ulong)information.FileSizeHigh << 32 | information.FileSizeLow), DateTime.FromFileTimeUtc(write));
        }
        catch (ArgumentOutOfRangeException) { return null; }
    }

    private static string Extended(string path)
    {
        var full = Path.GetFullPath(path);
        return full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full
            : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "CreateFileW")]
    private static extern SafeFileHandle CreateFile(string name, uint access, uint share, IntPtr security, uint disposition,
        uint flags, IntPtr template);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle file, out FileInformation information);
}

/// <summary>
/// The Claude app's scheduled-task list of one account and organization
/// (<c>claude-code-sessions\account\org\scheduled-tasks.json</c>), as Claude 6.28899 reads it: at most 10 MiB, and the
/// whole file passes its schema or the app loads none of it (and its next save keeps only what is made after).
/// <see cref="IsValid"/> mirrors that schema; a file it refuses is never written or counted from. The definitions
/// (<c>CLAUDE_CONFIG_DIR\scheduled-tasks\id</c>) are shared by every account and are never read here.
/// </summary>
internal static class ClaudeScheduledRegistry
{
    internal const string FileName = "scheduled-tasks.json";
    // The app loads a list of at most this many bytes (6.28899: R3); a longer one loads as nothing.
    internal const long MaxBytes = 10_485_760;
    private static readonly Regex SafeId = new("^[a-z0-9_-]+$", RegexOptions.CultureInvariant);
    private static readonly Regex KeyPattern = new(
        "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}/[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$",
        RegexOptions.CultureInvariant);
    // What a task is, the same in every account: a change made in one account brings these to the others.
    internal static readonly string[] SyncedFields = ["displayName", "cronExpression", "fireAt", "enabled", "model", "createdAt",
        "userSelectedFolders", "userSelectedFiles", "userSelectedProjectUuids", "chromePermissionMode", "chromeAllowedDomains", "cwd",
        "place", "useWorktree", "sourceBranch", "permissionMode", "permissionModeCarried", "madeByClaude", "changedByClaudeAt",
        "approvedPermissions", "disableJitter", "notifySessionId", "promptWrittenBy", "promptFileHash", "dispatchSubscribed", "watcher"];
    // Each account's own, never copied nor compared: the definition path (the app sets it on load), an import's origin
    // (it brings the import rules with it), what the app observed of the runs there, and where the task moved from or to.
    internal static readonly string[] OwnFields = ["filePath", "importedFrom", "observedToolUse", "migratedFromRemote",
        "migratedToRemoteAt", "migratedFromSpaceId", "migratedToRemote", "spaceId"];
    // When a task last ran (see ClaudeScheduledSync.Plan).
    internal static readonly string[] RunFields = ["lastRunAt", "lastScheduledFor", "missedRunScanFloor"];
    // File-level markers of the two cron matcher upgrades: a list without them gets its affected tasks' slots raised.
    internal static readonly string[] MigrationStamps = ["sundayAliasBoundaryStamped", "dayFieldsOrBoundaryStamped"];
    private static readonly string[] Strings = ["displayName", "migratedToRemoteAt", "migratedFromSpaceId", "cronExpression",
        "model", "lastRunAt", "lastScheduledFor", "missedRunScanFloor", "spaceId", "cwd", "sourceBranch", "notifySessionId"];
    private static readonly string[] Booleans = ["migratedFromRemote", "useWorktree", "disableJitter", "dispatchSubscribed"];
    private static readonly string[] StringArrays = ["userSelectedFolders", "userSelectedFiles", "userSelectedProjectUuids",
        "chromeAllowedDomains"];
    private static readonly string[] ChromeModes = ["ask", "skip_all_permission_checks", "follow_a_plan"];
    private static readonly string[] PermissionModes = ["default", "acceptEdits", "plan", "bypassPermissions", "dontAsk", "auto"];
    private static readonly JsonSerializerOptions WriteOptions = new()
    { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal static readonly IReadOnlyDictionary<string, JsonObject> None = new Dictionary<string, JsonObject>(StringComparer.Ordinal);

    internal static bool IsSafeId(string? id) => id is not null && SafeId.IsMatch(id);

    /// <summary>An account/organization pair as this widget names it: both lowercase GUIDs.</summary>
    internal static bool IsKey(string? key) => key is not null && KeyPattern.IsMatch(key);

    internal static string PathFor(string sessionsRoot, string key)
    {
        var parts = key.Split('/');
        return System.IO.Path.Combine(sessionsRoot, parts[0], parts[1], FileName);
    }

    internal static ClaudeScheduledFile Unread(string key, string path, string problem, string? stamp = null) =>
        new(key, path, ClaudeScheduledReadStatus.Unreadable, null, null, None, null, problem, stamp ?? CheapStamp(path));

    /// <summary>Reads one list through one handle. A file being written, cut short, linked, too long for the app or not
    /// passing its schema reads as unreadable, never as empty.</summary>
    internal static ClaudeScheduledFile Read(string key, string path)
    {
        try
        {
            var info = new FileInfo(path);
            if (!info.Exists) return new(key, path, ClaudeScheduledReadStatus.Missing, null, null, None, null, Stamp: "-");
            // Taken before reading: a change made while the list is read shows as a newer stamp, and is read next time.
            var stamp = Stamp(info.Length, info.LastWriteTimeUtc);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return Unread(key, path, "redirected", stamp);
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (ClaudeFileIdentity.Of(stream.SafeFileHandle) is not { } identity) return Unread(key, path, "io", stamp);
            // A second name would carry what is written here to another list, or keep the old one there.
            if (identity.Links > 1) return Unread(key, path, "linked", stamp);
            if (identity.Length > MaxBytes) return Unread(key, path, "large", stamp);
            var bytes = new byte[identity.Length];
            stream.ReadExactly(bytes);
            // Still being written.
            if (stream.Length != identity.Length) return Unread(key, path, "io", stamp);
            return Parse(key, path, bytes, new DateTimeOffset(identity.LastWriteUtc, TimeSpan.Zero)) with { Stamp = stamp, FileId = identity.Id };
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return Unread(key, path, "io");
        }
    }

    internal static ClaudeScheduledFile Parse(string key, string path, byte[] bytes, DateTimeOffset? writtenAt)
    {
        ClaudeScheduledFile Fails(string why) => new(key, path, ClaudeScheduledReadStatus.Unreadable, bytes, null, None, writtenAt, why);
        if (bytes.LongLength > MaxBytes) return Fails("large");
        // The app parses the text as is: a byte order mark fails it.
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Fails("bom");
        JsonNode? root;
        try { root = JsonNode.Parse(bytes); }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        { return Fails("json"); }
        if (!IsValid(root)) return Fails("schema");
        var entries = new Dictionary<string, JsonObject>(StringComparer.Ordinal);
        foreach (var item in root!["scheduledTasks"]!.AsArray())
        {
            var entry = item!.AsObject();
            var id = entry["id"]!.GetValue<string>();
            if (!IsSafeId(id)) continue;
            // Two entries of one id: which one the app keeps is not this widget's to guess.
            if (!entries.TryAdd(id, entry)) return Fails("duplicate");
        }
        return new(key, path, ClaudeScheduledReadStatus.Ok, bytes, root.AsObject(), entries, writtenAt);
    }

    internal static string Stamp(long length, DateTime lastWriteUtc) => length.ToString(CultureInfo.InvariantCulture) + ":"
        + lastWriteUtc.Ticks.ToString(CultureInfo.InvariantCulture);

    /// <summary>The size and last write of a list without opening it: "-" when there is none, "?" when it cannot be told.</summary>
    internal static string CheapStamp(string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists ? Stamp(info.Length, info.LastWriteTimeUtc) : "-";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return "?"; }
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

    /// <summary>What a task is, the same in every account: its synced fields.</summary>
    internal static JsonObject Synced(JsonObject entry)
    {
        var content = new JsonObject();
        foreach (var name in SyncedFields)
            if (entry.TryGetPropertyValue(name, out var value)) content[name] = value?.DeepClone();
        return content;
    }

    /// <summary>The fields this widget does not know: kept as they are in every account, and copied with a task into a
    /// list it is made in.</summary>
    internal static JsonObject Extra(JsonObject entry)
    {
        var extra = new JsonObject();
        foreach (var (name, value) in entry)
            if (name != "id" && !SyncedFields.Contains(name) && !OwnFields.Contains(name) && !RunFields.Contains(name))
                extra[name] = value?.DeepClone();
        return extra;
    }

    internal static string HashOf(JsonObject entry) => Hash(Synced(entry));

    /// <summary>The permission mode a task runs in, and whether the app carries it over without asking again.</summary>
    internal static (string Mode, bool Carried) Permission(JsonObject entry) =>
        (Text(entry, "permissionMode") ?? "default", entry["permissionModeCarried"] is JsonValue carried && carried.GetValueKind() == JsonValueKind.True);

    /// <summary>A mode beyond asking (accept edits, auto, bypass) or one carried over: made in a list without the user
    /// seeing it chosen, it would run there with more than the user gave.</summary>
    internal static bool Raises((string Mode, bool Carried) permission) =>
        permission.Carried || permission.Mode is not ("default" or "dontAsk" or "plan");

    /// <summary>Which run of a task the run times belong to: made at <c>createdAt</c>, and for a one-time task due at
    /// <c>fireAt</c>. A new due time starts a new run, with no run times.</summary>
    internal static string Lineage(JsonObject entry) =>
        (entry["createdAt"]?.ToJsonString() ?? "") + "|" + (entry["fireAt"]?.ToJsonString() ?? "");

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

    /// <summary>Orders two times (ISO text): none, or one that does not parse, comes first.</summary>
    internal static int Compare(string? first, string? second)
    {
        static DateTimeOffset? Parsed(string? text) =>
            text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : null;
        var (a, b) = (Parsed(first), Parsed(second));
        return a is null ? b is null ? 0 : -1 : b is null ? 1 : a.Value.CompareTo(b.Value);
    }

    internal static bool IsTime(string? text) =>
        text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out _);

    internal static string? Later(string? first, string? second) => Compare(first, second) >= 0 ? first ?? second : second;

    internal static string? Text(JsonObject entry, string name) =>
        entry[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    internal static byte[] Serialize(JsonObject root) => Encoding.UTF8.GetBytes(root.ToJsonString(WriteOptions));

    internal static string? Sha(byte[]? bytes) => bytes is null ? null : Convert.ToHexString(SHA256.HashData(bytes));
}

/// <summary>
/// What this widget keeps to align scheduled tasks, in its own folder (owner-only), tied to the profile folder it read:
/// per account the tasks it last saw there (or wrote), the ones removed there alone and the ones it wrote that the app
/// has not loaded yet; per task the newest change any account made, a conflict two accounts made, a deletion from every
/// account, a fill asked for, and the newest run; and per account a write begun. Written whole and atomically, and read
/// strictly: a record that does not read stops every write. The tasks' prompts live in their definitions and are never
/// kept.
/// </summary>
internal sealed class ClaudeScheduledState
{
    private const int Version = 2;
    internal string? Root { get; set; }
    internal string? DefinitionsRoot { get; set; }
    internal SortedSet<string> DefinitionsSeen { get; set; } = new(StringComparer.Ordinal);
    internal SortedDictionary<string, AccountState> Accounts { get; } = new(StringComparer.Ordinal);
    internal SortedDictionary<string, Change> Changes { get; } = new(StringComparer.Ordinal);
    internal SortedDictionary<string, Conflict> Conflicts { get; } = new(StringComparer.Ordinal);
    internal SortedDictionary<string, Deletion> Deleted { get; } = new(StringComparer.Ordinal);
    internal SortedDictionary<string, Seeding> Seeds { get; } = new(StringComparer.Ordinal);
    internal SortedDictionary<string, SortedDictionary<string, Run>> Runs { get; } = new(StringComparer.Ordinal);
    internal SortedDictionary<string, Intent> Intents { get; } = new(StringComparer.Ordinal);
    // Lists the app's log showed it load: aligned even before they hold a file.
    internal SortedSet<string> Known { get; } = new(StringComparer.Ordinal);
    internal SortedDictionary<string, string> Labels { get; } = new(StringComparer.Ordinal);

    internal sealed class AccountState
    {
        // When this widget first saw the account: changes made before are not brought to it.
        internal DateTimeOffset Since { get; set; }
        internal DateTimeOffset Seen { get; set; }
        internal SortedDictionary<string, Entry> Entries { get; set; } = new(StringComparer.Ordinal);
        internal SortedDictionary<string, DateTimeOffset> Removed { get; } = new(StringComparer.Ordinal);
        internal SortedDictionary<string, Written> Writes { get; } = new(StringComparer.Ordinal);
    }

    /// <summary>A task as an account holds it: its synced fields' hash and its permission mode.</summary>
    internal sealed record Entry(string Hash, string Mode, bool Carried);

    /// <summary>This widget's write of a task to an account the app has not loaded since: when, what the task was there
    /// before the first such write (null: absent), and what it wrote.</summary>
    internal sealed record Written(DateTimeOffset At, string? Before, string After);

    /// <summary>The newest change to a task: made in account <c>By</c> after <c>Lower</c> and by <c>Upper</c>; what it
    /// is now (its synced fields, hashed as <c>Hash</c>); the fields this widget does not know (copied into a list it is
    /// made in); its definition path; whether its permission mode was seen chosen; and the migration stamps of its list.</summary>
    internal sealed record Change(DateTimeOffset Lower, DateTimeOffset Upper, string By, string Hash, JsonObject Content,
        JsonObject Extra, string? FilePath, bool PermissionObserved, string[] Stamps);

    /// <summary>Changes to one task in several accounts whose order cannot be told, found at <c>At</c>.</summary>
    internal sealed record Conflict(DateTimeOffset At, string[] Accounts);

    /// <summary>A task deleted from every account (by <c>definitions</c>: its definition folder went; by <c>command</c>:
    /// asked for), and the accounts still to remove it from.</summary>
    internal sealed record Deletion(DateTimeOffset At, string By, SortedSet<string> Pending);

    /// <summary>A task a seed fills into the accounts still lacking it (<c>Pending</c>), as account <c>Source</c> held it.</summary>
    internal sealed record Seeding(DateTimeOffset At, string Source, string Hash, JsonObject Content, JsonObject Extra,
        string? FilePath, string[] Stamps, SortedSet<string> Pending);

    /// <summary>A run of a task: its newest run times, as one account held them, the latest scan floor, and the newest
    /// run an account finished without a retry pending.</summary>
    internal sealed record Run(string? LastRunAt, string? LastScheduledFor, string? Floor, string? Completed);

    /// <summary>A write begun: its list is moved into place after this is kept. <c>Partial</c> is the new list beside it
    /// while it is not; <c>Before</c> and <c>After</c> the file's hashes before and after.</summary>
    internal sealed record Intent(DateTimeOffset At, string Partial, string? Before, string After,
        SortedDictionary<string, Entry> Entries, SortedDictionary<string, Written> Writes);

    internal ClaudeScheduledState Clone() => Load(Save()) ?? throw new InvalidDataException();

    private static string Time(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);
    private static JsonArray Strings(IEnumerable<string> items) => new(items.Select(item => (JsonNode)item).ToArray());

    private static JsonObject EntriesJson(IEnumerable<KeyValuePair<string, Entry>> entries)
    {
        var json = new JsonObject();
        foreach (var (id, entry) in entries) json[id] = new JsonObject { ["hash"] = entry.Hash, ["mode"] = entry.Mode, ["carried"] = entry.Carried };
        return json;
    }

    private static JsonObject WritesJson(IEnumerable<KeyValuePair<string, Written>> writes)
    {
        var json = new JsonObject();
        foreach (var (id, write) in writes) json[id] = new JsonObject { ["at"] = Time(write.At), ["before"] = write.Before, ["after"] = write.After };
        return json;
    }

    internal JsonObject Save()
    {
        var accounts = new JsonObject();
        foreach (var (key, account) in Accounts)
        {
            var removed = new JsonObject();
            foreach (var (id, at) in account.Removed) removed[id] = Time(at);
            accounts[key] = new JsonObject
            {
                ["since"] = Time(account.Since), ["seen"] = Time(account.Seen), ["entries"] = EntriesJson(account.Entries),
                ["removed"] = removed, ["writes"] = WritesJson(account.Writes)
            };
        }
        var changes = new JsonObject();
        foreach (var (id, change) in Changes)
            changes[id] = new JsonObject
            {
                ["lower"] = Time(change.Lower), ["upper"] = Time(change.Upper), ["by"] = change.By, ["hash"] = change.Hash,
                ["content"] = change.Content.DeepClone(), ["extra"] = change.Extra.DeepClone(), ["filePath"] = change.FilePath,
                ["permissionObserved"] = change.PermissionObserved, ["stamps"] = Strings(change.Stamps)
            };
        var conflicts = new JsonObject();
        foreach (var (id, conflict) in Conflicts) conflicts[id] = new JsonObject { ["at"] = Time(conflict.At), ["accounts"] = Strings(conflict.Accounts) };
        var deleted = new JsonObject();
        foreach (var (id, deletion) in Deleted)
            deleted[id] = new JsonObject { ["at"] = Time(deletion.At), ["by"] = deletion.By, ["pending"] = Strings(deletion.Pending) };
        var seeds = new JsonObject();
        foreach (var (id, seeding) in Seeds)
            seeds[id] = new JsonObject
            {
                ["at"] = Time(seeding.At), ["source"] = seeding.Source, ["hash"] = seeding.Hash, ["content"] = seeding.Content.DeepClone(),
                ["extra"] = seeding.Extra.DeepClone(), ["filePath"] = seeding.FilePath, ["stamps"] = Strings(seeding.Stamps),
                ["pending"] = Strings(seeding.Pending)
            };
        var runs = new JsonObject();
        foreach (var (id, lineages) in Runs)
        {
            var json = new JsonObject();
            foreach (var (lineage, run) in lineages)
                json[lineage] = new JsonObject { ["lastRunAt"] = run.LastRunAt, ["lastScheduledFor"] = run.LastScheduledFor, ["floor"] = run.Floor, ["completed"] = run.Completed };
            runs[id] = json;
        }
        var intents = new JsonObject();
        foreach (var (key, intent) in Intents)
            intents[key] = new JsonObject
            {
                ["at"] = Time(intent.At), ["partial"] = intent.Partial, ["before"] = intent.Before, ["after"] = intent.After,
                ["entries"] = EntriesJson(intent.Entries), ["writes"] = WritesJson(intent.Writes)
            };
        var labels = new JsonObject();
        foreach (var (key, label) in Labels) labels[key] = label;
        return new JsonObject
        {
            ["version"] = Version, ["root"] = Root, ["definitionsRoot"] = DefinitionsRoot, ["definitionsSeen"] = Strings(DefinitionsSeen),
            ["accounts"] = accounts, ["changes"] = changes, ["conflicts"] = conflicts, ["deleted"] = deleted, ["seeds"] = seeds,
            ["runs"] = runs, ["intents"] = intents, ["known"] = Strings(Known), ["labels"] = labels
        };
    }

    /// <summary>Null unless every record reads whole, with the types, keys and ids this widget writes.</summary>
    internal static ClaudeScheduledState? Load(JsonObject root)
    {
        try
        {
            if (Number(root["version"]) != Version) return null;
            var state = new ClaudeScheduledState { Root = Optional(root["root"]), DefinitionsRoot = Optional(root["definitionsRoot"]) };
            foreach (var id in Array(root["definitionsSeen"])) state.DefinitionsSeen.Add(Id(String(id)));
            foreach (var (key, node) in Object(root["accounts"]))
            {
                var value = Object(node);
                var account = new AccountState { Since = Moment(value["since"]), Seen = Moment(value["seen"]), Entries = Entries(value["entries"]) };
                foreach (var (id, at) in Object(value["removed"])) account.Removed[Id(id)] = Moment(at);
                foreach (var (id, write) in Writes(value["writes"])) account.Writes[id] = write;
                state.Accounts[Key(key)] = account;
            }
            foreach (var (id, node) in Object(root["changes"]))
            {
                var value = Object(node);
                state.Changes[Id(id)] = new(Moment(value["lower"]), Moment(value["upper"]), Key(String(value["by"])), String(value["hash"]),
                    Content(value["content"]), Extra(value["extra"]), Optional(value["filePath"]), Boolean(value["permissionObserved"]),
                    Stamps(value["stamps"]));
            }
            foreach (var (id, node) in Object(root["conflicts"]))
            {
                var value = Object(node);
                state.Conflicts[Id(id)] = new(Moment(value["at"]), Array(value["accounts"]).Select(key => Key(String(key))).ToArray());
            }
            foreach (var (id, node) in Object(root["deleted"]))
            {
                var value = Object(node);
                var by = String(value["by"]);
                if (by is not ("definitions" or "command")) return null;
                state.Deleted[Id(id)] = new(Moment(value["at"]), by, Keys(value["pending"]));
            }
            foreach (var (id, node) in Object(root["seeds"]))
            {
                var value = Object(node);
                state.Seeds[Id(id)] = new(Moment(value["at"]), Key(String(value["source"])), String(value["hash"]), Content(value["content"]),
                    Extra(value["extra"]), Optional(value["filePath"]), Stamps(value["stamps"]), Keys(value["pending"]));
            }
            foreach (var (id, node) in Object(root["runs"]))
            {
                var lineages = new SortedDictionary<string, Run>(StringComparer.Ordinal);
                foreach (var (lineage, run) in Object(node))
                {
                    var value = Object(run);
                    lineages[lineage] = new(Optional(value["lastRunAt"]), Optional(value["lastScheduledFor"]), Optional(value["floor"]),
                        Optional(value["completed"]));
                }
                state.Runs[Id(id)] = lineages;
            }
            foreach (var (key, node) in Object(root["intents"]))
            {
                var value = Object(node);
                var writes = new SortedDictionary<string, Written>(StringComparer.Ordinal);
                foreach (var (id, write) in Writes(value["writes"])) writes[id] = write;
                state.Intents[Key(key)] = new(Moment(value["at"]), String(value["partial"]), Optional(value["before"]), String(value["after"]),
                    Entries(value["entries"]), writes);
            }
            foreach (var key in Array(root["known"])) state.Known.Add(Key(String(key)));
            foreach (var (key, label) in Object(root["labels"])) state.Labels[Key(key)] = String(label);
            return state;
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or FormatException
                                              or JsonException or KeyNotFoundException or ArgumentException)
        { return null; }
    }

    private static JsonObject Object(JsonNode? node) => node as JsonObject ?? throw new InvalidDataException();
    private static JsonArray Array(JsonNode? node) => node as JsonArray ?? throw new InvalidDataException();

    private static string String(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : throw new InvalidDataException();

    private static string? Optional(JsonNode? node) => node is null ? null : String(node);

    private static bool Boolean(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? value.GetValue<bool>() : throw new InvalidDataException();

    private static int Number(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<int>(out var number) ? number : -1;

    private static DateTimeOffset Moment(JsonNode? node) =>
        DateTimeOffset.ParseExact(String(node), "O", CultureInfo.InvariantCulture, DateTimeStyles.None);

    private static string Key(string key) => ClaudeScheduledRegistry.IsKey(key) ? key : throw new InvalidDataException();
    private static string Id(string id) => ClaudeScheduledRegistry.IsSafeId(id) ? id : throw new InvalidDataException();

    private static SortedSet<string> Keys(JsonNode? node) => new(Array(node).Select(key => Key(String(key))), StringComparer.Ordinal);

    private static string[] Stamps(JsonNode? node) =>
        Array(node).Select(stamp => String(stamp) is var name && ClaudeScheduledRegistry.MigrationStamps.Contains(name) ? name : throw new InvalidDataException()).ToArray();

    private static SortedDictionary<string, Entry> Entries(JsonNode? node)
    {
        var entries = new SortedDictionary<string, Entry>(StringComparer.Ordinal);
        foreach (var (id, entry) in Object(node))
        {
            var value = Object(entry);
            entries[Id(id)] = new(String(value["hash"]), String(value["mode"]), Boolean(value["carried"]));
        }
        return entries;
    }

    private static IEnumerable<(string Id, Written Write)> Writes(JsonNode? node)
    {
        var writes = new List<(string, Written)>();
        foreach (var (id, write) in Object(node))
        {
            var value = Object(write);
            writes.Add((Id(id), new(Moment(value["at"]), Optional(value["before"]), String(value["after"]))));
        }
        return writes;
    }

    private static JsonObject Content(JsonNode? node)
    {
        var content = (JsonObject)Object(node).DeepClone();
        foreach (var (name, _) in content) if (!ClaudeScheduledRegistry.SyncedFields.Contains(name)) throw new InvalidDataException();
        return content;
    }

    private static JsonObject Extra(JsonNode? node)
    {
        var extra = (JsonObject)Object(node).DeepClone();
        foreach (var (name, _) in extra)
            if (name == "id" || ClaudeScheduledRegistry.SyncedFields.Contains(name) || ClaudeScheduledRegistry.OwnFields.Contains(name)
                || ClaudeScheduledRegistry.RunFields.Contains(name)) throw new InvalidDataException();
        return extra;
    }
}

/// <summary>What aligning would write to one account: the list as it should be, which tasks it creates, changes,
/// removes or only brings the run times of, and the tasks it then holds and the ones it wrote.</summary>
internal sealed record ClaudeScheduledChange(string Key, ClaudeScheduledFile File, JsonObject Root, byte[] Bytes,
    IReadOnlyList<string> Created, IReadOnlyList<string> Updated, IReadOnlyList<string> Deleted, IReadOnlyList<string> Runs,
    SortedDictionary<string, ClaudeScheduledState.Entry> Entries, SortedDictionary<string, ClaudeScheduledState.Written> Writes);

/// <summary>What a pass would write, the tasks it would not make in an account because they would run there with more
/// permission than anyone chose there, and the lists the result would make too long for the app.</summary>
internal sealed record ClaudeScheduledPlan(IReadOnlyList<ClaudeScheduledChange> Changes, IReadOnlyList<(string Key, string Id)> Held,
    IReadOnlyList<string> TooLarge);

/// <summary>What one pass did and found: lists written, unreadable (labels), waiting, too long for the app; the tasks
/// in conflict, removed in one account only and held; the answer to a request; and per list the stamp it was read or
/// written with.</summary>
internal sealed record ClaudeScheduledPass(bool First, int Written, IReadOnlyList<string> Unreadable, IReadOnlyList<string> Waiting,
    string? Failure, JsonObject? Answer = null, IReadOnlyList<string>? TooLarge = null, IReadOnlyList<string>? Conflicts = null,
    IReadOnlyList<string>? RemovedHere = null, IReadOnlyList<string>? Held = null, IReadOnlyDictionary<string, string>? Processed = null);

/// <summary>
/// Aligns the scheduled-task lists of the accounts on this PC (every account/organization folder holding a list, one
/// the app's log showed it load, and the registered ones). Each pass reads every list and records what changed in each
/// since it was last seen: a task made or changed there is that account's change (the newest one wins, when the order
/// can be told; when it cannot, the task is in conflict and left alone until one account changes it again). A task gone
/// from a list is removed there only: it is never made there again nor deleted elsewhere. A task is deleted from every
/// account only when its definition folder goes, or when the remove command asks. The lists the caller lets it write
/// (another account's while the app runs, the one it shows only once it is closed) get every change as made, field by
/// field, and the newest run times; a task is not made in a list where it would run with more permission than anyone
/// chose. The first pass, and the first after the profile folder moved, only take what is there as the starting point.
/// Lists that do not read are neither written nor counted from.
/// </summary>
internal sealed class ClaudeScheduledSync
{
    // How old the kept times the lists were last looked at may get before a pass that changed nothing saves them.
    private static readonly TimeSpan SeenRefresh = TimeSpan.FromMinutes(30);
    // How long after this widget's write the app's log has to show it begin loading that list before the list counts as
    // the app's again (the log tells whole seconds).
    internal static readonly TimeSpan LoadMargin = TimeSpan.FromSeconds(1);
    private const int Backups = 5;
    internal const string StoreFolder = "scheduled-tasks";
    private const string PartialPattern = ".scheduled-tasks.*.widget-sync.partial";
    private readonly string _sessionsRoot;
    private readonly string _store;
    private readonly string? _definitions;
    private readonly Func<IDisposable> _lease;
    private readonly Func<DateTimeOffset> _clock;

    internal ClaudeScheduledSync(string sessionsRoot, string storeDirectory, string? definitionsRoot, Func<IDisposable> lease,
        Func<DateTimeOffset>? clock = null)
    {
        _sessionsRoot = sessionsRoot; _store = storeDirectory; _definitions = definitionsRoot; _lease = lease;
        _clock = clock ?? (() => DateTimeOffset.Now);
    }

    internal string SessionsRoot => _sessionsRoot;
    private string StatePath => Path.Combine(_store, "state.json");
    // For tests: runs just before a list is moved into place, and just after (a throw there stands for the widget stopping).
    internal Action<string>? BeforeMoveForTest { get; set; }
    internal Action<string>? AfterMoveForTest { get; set; }

    private string FolderOf(string key)
    {
        var parts = key.Split('/');
        return Path.Combine(_sessionsRoot, parts[0], parts[1]);
    }

    /// <summary>The account/organization folders aligned (each holding a list, and each of <paramref name="wanted"/> that
    /// is there), by key; null when the profile folder cannot be listed. Folders are never made.</summary>
    internal SortedDictionary<string, string>? Discover(IEnumerable<string> wanted)
    {
        var want = wanted.Where(ClaudeScheduledRegistry.IsKey).ToHashSet(StringComparer.Ordinal);
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (!Directory.Exists(_sessionsRoot)) return found;
            foreach (var account in Directory.GetDirectories(_sessionsRoot))
            {
                if (!Guid.TryParseExact(Path.GetFileName(account), "D", out _)) continue;
                string[] orgs;
                try { orgs = Directory.GetDirectories(account); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
                foreach (var org in orgs)
                {
                    if (!Guid.TryParseExact(Path.GetFileName(org), "D", out _)) continue;
                    var key = (Path.GetFileName(account) + "/" + Path.GetFileName(org)).ToLowerInvariant();
                    if (want.Contains(key) || File.Exists(Path.Combine(org, ClaudeScheduledRegistry.FileName))) found.TryAdd(key, org);
                }
            }
            return found;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>Each aligned list as read now (null when the profile folder cannot be listed). A list behind a link (its
    /// account folder, its organization folder or the file), or reached by two keys, reads as unreadable; one that does
    /// not read in a folder not aligned before is not taken for an account's.</summary>
    internal IReadOnlyList<ClaudeScheduledFile>? Observe(IEnumerable<string> wanted)
    {
        var want = wanted.ToHashSet(StringComparer.Ordinal);
        if (Discover(want) is not { } folders) return null;
        var files = new List<ClaudeScheduledFile>();
        foreach (var (key, folder) in folders)
        {
            var path = Path.Combine(folder, ClaudeScheduledRegistry.FileName);
            ClaudeScheduledFile file;
            try
            {
                file = (new DirectoryInfo(Path.GetDirectoryName(folder)!).Attributes & FileAttributes.ReparsePoint) != 0
                       || (new DirectoryInfo(folder).Attributes & FileAttributes.ReparsePoint) != 0
                    ? ClaudeScheduledRegistry.Unread(key, path, "redirected")
                    : ClaudeScheduledRegistry.Read(key, path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { file = ClaudeScheduledRegistry.Unread(key, path, "io"); }
            if (!file.Readable && !want.Contains(key)) continue;
            var folderId = ClaudeFileIdentity.Of(folder)?.Id;
            files.Add(folderId is null && file.Readable ? ClaudeScheduledRegistry.Unread(key, path, "io") : file with { FolderId = folderId });
        }
        var shared = files.Where(file => file.FolderId is not null).GroupBy(file => file.FolderId)
            .Concat(files.Where(file => file.FileId is not null).GroupBy(file => file.FileId))
            .Where(group => group.Count() > 1).SelectMany(group => group).Select(file => file.Key).ToHashSet(StringComparer.Ordinal);
        return files.Select(file => shared.Contains(file.Key) ? ClaudeScheduledRegistry.Unread(file.Key, file.Path, "shared") with { FolderId = file.FolderId } : file).ToArray();
    }

    /// <summary>Whether two keys' folders are one on disk, or that cannot be told: a list is never written under
    /// another name for the one the app has loaded.</summary>
    internal bool SameFolder(string key, string other)
    {
        if (key == other) return true;
        var (folder, otherFolder) = (FolderOf(key), FolderOf(other));
        try { if (!Directory.Exists(otherFolder)) return false; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return true; }
        var (a, b) = (ClaudeFileIdentity.Of(folder), ClaudeFileIdentity.Of(otherFolder));
        return a is null || b is null || a.Id == b.Id;
    }

    /// <summary>The size and last write of each of these lists without reading them, to tell when one changed since it
    /// was read or written.</summary>
    internal IReadOnlyDictionary<string, string> Stamps(IEnumerable<string> keys) =>
        keys.Where(ClaudeScheduledRegistry.IsKey).Distinct(StringComparer.Ordinal).ToDictionary(key => key,
            key => ClaudeScheduledRegistry.CheapStamp(Path.Combine(FolderOf(key), ClaudeScheduledRegistry.FileName)), StringComparer.Ordinal);

    /// <summary>The profile folder this widget aligns, by path and by what it is on disk; null when it is not there.</summary>
    private string? RootIdentity() => ClaudeFileIdentity.Of(_sessionsRoot) is { } info
        ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(_sessionsRoot)).ToLowerInvariant() + "|" + info.Id : null;

    /// <summary>The kept state: none yet (a first pass), or null when it cannot be read. One of the first version starts
    /// over: it could not tell a task removed in one account from one deleted.</summary>
    internal (ClaudeScheduledState? State, bool Exists) LoadState()
    {
        try
        {
            if (!File.Exists(StatePath)) return (new ClaudeScheduledState(), false);
            var root = JsonNode.Parse(File.ReadAllBytes(StatePath)) as JsonObject;
            if (root?["version"] is JsonValue version && version.GetValueKind() == JsonValueKind.Number && version.TryGetValue<int>(out var number)
                && number == 1) return (new ClaudeScheduledState(), false);
            return (root is null ? null : ClaudeScheduledState.Load(root), true);
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
            finally { TryDelete(temporary); }
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

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>Forgets what was seen of the lists (a new profile folder): the next pass takes the starting point again.
    /// Deletions stay deleted, with nothing left to do in the old folder.</summary>
    private static void Rebase(ClaudeScheduledState state)
    {
        state.Accounts.Clear(); state.Changes.Clear(); state.Conflicts.Clear(); state.Seeds.Clear(); state.Runs.Clear();
        state.Intents.Clear();
        foreach (var deletion in state.Deleted.Values) deletion.Pending.Clear();
    }

    private static SortedDictionary<string, ClaudeScheduledState.Entry> Current(IReadOnlyDictionary<string, JsonObject> entries)
    {
        var current = new SortedDictionary<string, ClaudeScheduledState.Entry>(StringComparer.Ordinal);
        foreach (var (id, entry) in entries)
        {
            var (mode, carried) = ClaudeScheduledRegistry.Permission(entry);
            current[id] = new(ClaudeScheduledRegistry.HashOf(entry), mode, carried);
        }
        return current;
    }

    private static string[] StampsOf(JsonObject? root) => root is null ? []
        : ClaudeScheduledRegistry.MigrationStamps.Where(name => root[name] is JsonValue value && value.GetValueKind() == JsonValueKind.True).ToArray();

    /// <summary>Settles the writes a stopped widget began. A write whose new list is still beside the old one never
    /// happened (only the move takes it away). One whose list holds what it wrote did. Any other list was written and
    /// changed since, even back to what it held before (the user deleting what was made there): what it holds now is its
    /// starting point, a task gone from it counts as removed there, and nothing in it counts as a change. A list that does
    /// not read keeps its write begun until it does.</summary>
    internal static void Recover(ClaudeScheduledState state, IReadOnlyList<ClaudeScheduledFile> files, DateTimeOffset now, bool readOnly = false)
    {
        var byKey = files.ToDictionary(file => file.Key, StringComparer.Ordinal);
        foreach (var (key, intent) in state.Intents.ToArray())
        {
            if (!byKey.TryGetValue(key, out var file) || !state.Accounts.TryGetValue(key, out var account))
            {
                state.Intents.Remove(key);
                continue;
            }
            if (File.Exists(intent.Partial))
            {
                if (!readOnly) TryDelete(intent.Partial);
                state.Intents.Remove(key);
                continue;
            }
            if (!file.Readable) continue;
            if (ClaudeScheduledRegistry.Sha(file.Bytes) == intent.After)
            {
                account.Entries = new(intent.Entries, StringComparer.Ordinal);
                foreach (var (id, write) in intent.Writes) account.Writes[id] = write;
                account.Seen = intent.At;
            }
            else
            {
                var current = Current(file.Entries);
                foreach (var id in account.Entries.Keys.Concat(intent.Entries.Keys).Distinct().ToArray())
                    if (!current.ContainsKey(id) && !state.Deleted.ContainsKey(id)) account.Removed[id] = now;
                account.Entries = current;
                account.Writes.Clear();
                account.Seen = now;
            }
            state.Intents.Remove(key);
        }
    }

    /// <summary>A list that was aligned and is gone, its folder still there, reads as unreadable: the app, finding none,
    /// would save only what is made after, so what it held is kept as it was seen. An account whose folder is gone goes.</summary>
    internal static IReadOnlyList<ClaudeScheduledFile> Settle(ClaudeScheduledState state, IReadOnlyList<ClaudeScheduledFile> files)
    {
        var present = files.Select(file => file.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var key in state.Accounts.Keys.Where(key => !present.Contains(key)).ToArray()) state.Accounts.Remove(key);
        return files.Select(file => file.Status == ClaudeScheduledReadStatus.Missing && state.Accounts.TryGetValue(file.Key, out var account)
            && account.Entries.Count > 0 ? file with { Status = ClaudeScheduledReadStatus.Unreadable, Problem = "gone" } : file).ToArray();
    }

    /// <summary>The definition folders there now, by id; null when the folder is not there or cannot be listed.</summary>
    private SortedSet<string>? Definitions()
    {
        if (_definitions is null) return null;
        try
        {
            if (!Directory.Exists(_definitions)) return null;
            return new(Directory.GetDirectories(_definitions).Select(Path.GetFileName).Where(ClaudeScheduledRegistry.IsSafeId)!, StringComparer.Ordinal);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private string? DefinitionsName => _definitions is null ? null
        : Path.TrimEndingDirectorySeparator(Path.GetFullPath(_definitions)).ToLowerInvariant();

    /// <summary>A definition folder seen before that is gone, in a definitions folder that lists and is the one seen
    /// before, deletes its task from every account (the app's delete takes the folder with it). Every folder gone at
    /// once reads as the folder being replaced, and deletes nothing.</summary>
    internal void ObserveDefinitions(ClaudeScheduledState state, IReadOnlyList<ClaudeScheduledFile> files, DateTimeOffset now, bool first)
    {
        if (Definitions() is not { } present) return;
        if (state.DefinitionsRoot != DefinitionsName || first)
        {
            state.DefinitionsRoot = DefinitionsName;
            state.DefinitionsSeen = present;
            return;
        }
        var gone = state.DefinitionsSeen.Where(id => !present.Contains(id)).ToArray();
        if (gone.Length > 0 && !(gone.Length > 1 && gone.Length == state.DefinitionsSeen.Count))
            foreach (var id in gone)
                if (!state.Deleted.ContainsKey(id) && Tracked(state, files, id)) Delete(state, files, id, "definitions", now);
        state.DefinitionsSeen = present;
    }

    private static bool Tracked(ClaudeScheduledState state, IReadOnlyList<ClaudeScheduledFile> files, string id) =>
        files.Any(file => file.Entries.ContainsKey(id)) || state.Accounts.Values.Any(account => account.Entries.ContainsKey(id))
        || state.Changes.ContainsKey(id) || state.Seeds.ContainsKey(id);

    /// <summary>Deletes a task from every account: each list holding it, and each that does not read (it may), is
    /// still to remove it; nothing brings it back.</summary>
    private static void Delete(ClaudeScheduledState state, IReadOnlyList<ClaudeScheduledFile> files, string id, string by, DateTimeOffset now)
    {
        var pending = files.Where(file => !file.Readable || file.Entries.ContainsKey(id)).Select(file => file.Key);
        if (state.Deleted.TryGetValue(id, out var known)) known.Pending.UnionWith(pending);
        else state.Deleted[id] = new(now, by, new(pending, StringComparer.Ordinal));
        state.Changes.Remove(id); state.Seeds.Remove(id); state.Conflicts.Remove(id);
        foreach (var account in state.Accounts.Values) account.Writes.Remove(id);
    }

    private sealed record Candidate(string Key, DateTimeOffset Lower, DateTimeOffset Upper, string Hash, JsonObject Content,
        JsonObject Extra, string? FilePath, bool PermissionChanged, string[] Stamps);

    /// <summary>Records, for each list that reads, what changed since it was last seen (a list seen for the first time
    /// is only taken as it is). A task gone from a list is removed there only. A task back as it was before this
    /// widget's write, which the app has not loaded since, may be the user undoing it or the app saving a copy it read
    /// before: it is neither spread nor written again (a task this widget made counts as removed there, a changed one as
    /// in conflict).</summary>
    internal static void Record(ClaudeScheduledState state, IReadOnlyList<ClaudeScheduledFile> files, DateTimeOffset now,
        ClaudeScheduledLogView? log)
    {
        var candidates = new SortedDictionary<string, List<Candidate>>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            if (!file.Readable) continue;
            var current = Current(file.Entries);
            if (!state.Accounts.TryGetValue(file.Key, out var account))
            {
                state.Accounts[file.Key] = new ClaudeScheduledState.AccountState { Since = now, Seen = now, Entries = current };
                continue;
            }
            var lower = account.Seen;
            var upper = file.WrittenAt is { } written ? written < lower ? lower : written > now ? now : written : now;
            foreach (var id in current.Keys.Union(account.Entries.Keys).ToArray())
            {
                var held = current.GetValueOrDefault(id);
                var was = account.Entries.GetValueOrDefault(id);
                if (account.Writes.TryGetValue(id, out var write) && held?.Hash == write.After && LoadedSince(log, file.Key, write.At))
                    account.Writes.Remove(id);
                if (held?.Hash == was?.Hash) continue;
                if (state.Deleted.ContainsKey(id)) continue;
                if (account.Writes.Remove(id, out write) && held?.Hash == write.Before)
                {
                    if (held is null) account.Removed[id] = now;
                    else Conflict(state, id, [file.Key], now);
                    continue;
                }
                if (held is null)
                {
                    account.Removed[id] = now;
                    continue;
                }
                account.Removed.Remove(id);
                var entry = file.Entries[id];
                if (!candidates.TryGetValue(id, out var list)) candidates[id] = list = [];
                list.Add(new(file.Key, lower, upper, held.Hash, ClaudeScheduledRegistry.Synced(entry), ClaudeScheduledRegistry.Extra(entry),
                    ClaudeScheduledRegistry.Text(entry, "filePath"), was is null || was.Mode != held.Mode || was.Carried != held.Carried,
                    StampsOf(file.Root)));
            }
            account.Entries = current;
            account.Seen = now;
        }
        foreach (var (id, list) in candidates) Resolve(state, id, list, now);
    }

    /// <summary>Whether the app's log shows it begin loading this list after <paramref name="at"/>.</summary>
    private static bool LoadedSince(ClaudeScheduledLogView? log, string key, DateTimeOffset at) =>
        log is not null && log.Loads.TryGetValue(key, out var started) && started - at >= LoadMargin;

    /// <summary>The change that wins among those seen in one pass: the one every other change (with other content) was
    /// provably made before. Against the change known, the newer wins; when neither is provably newer, or no change
    /// wins, the task is in conflict.</summary>
    private static void Resolve(ClaudeScheduledState state, string id, List<Candidate> list, DateTimeOffset now)
    {
        var winners = list.GroupBy(candidate => candidate.Hash).Select(group => group.MaxBy(candidate => candidate.Lower)!)
            .Where(latest => list.All(candidate => candidate.Hash == latest.Hash || candidate.Upper <= latest.Lower)).ToArray();
        var winner = winners.Length == 1 ? winners[0] : null;
        state.Changes.TryGetValue(id, out var known);
        if (winner is not null && known is not null && known.Hash != winner.Hash && winner.Upper <= known.Lower
            && !state.Conflicts.ContainsKey(id))
            return; // Made before the change known: that one is brought to this account too.
        if (winner is null || known is not null && known.Hash != winner.Hash && known.Upper > winner.Lower)
        {
            Conflict(state, id, list.Select(candidate => candidate.Key).Concat(known is null ? [] : [known.By]).ToArray(), now);
            return;
        }
        if (state.Conflicts.TryGetValue(id, out var conflict))
        {
            // Made before the conflict was found, it does not settle it.
            if (winner.Lower < conflict.At) return;
            state.Conflicts.Remove(id);
        }
        // A mode seen chosen stays seen while a later change keeps it.
        var observed = winner.PermissionChanged || known is not null && known.PermissionObserved
            && ClaudeScheduledRegistry.Permission(known.Content) == ClaudeScheduledRegistry.Permission(winner.Content);
        state.Changes[id] = new(winner.Lower, winner.Upper, winner.Key, winner.Hash, winner.Content, winner.Extra, winner.FilePath,
            observed, winner.Stamps);
    }

    private static void Conflict(ClaudeScheduledState state, string id, string[] accounts, DateTimeOffset now)
    {
        state.Changes.Remove(id);
        var all = state.Conflicts.TryGetValue(id, out var known) ? known.Accounts.Concat(accounts) : accounts;
        state.Conflicts[id] = new(now, all.Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray());
    }

    /// <summary>After recording: what is left to remove and fill, the conflicts nothing is left to settle (every list
    /// holds the task alike), and the newest run of every task the lists hold.</summary>
    internal static void Track(ClaudeScheduledState state, IReadOnlyList<ClaudeScheduledFile> files)
    {
        var byKey = files.ToDictionary(file => file.Key, StringComparer.Ordinal);
        bool Lacks(string key, string id) => byKey.TryGetValue(key, out var file) && file.Readable && !file.Entries.ContainsKey(id);
        foreach (var (id, deletion) in state.Deleted)
            deletion.Pending.RemoveWhere(key => !byKey.ContainsKey(key) || Lacks(key, id));
        foreach (var (id, seeding) in state.Seeds.ToArray())
        {
            seeding.Pending.RemoveWhere(key => !byKey.TryGetValue(key, out var file)
                || file.Readable && (file.Entries.ContainsKey(id) || state.Accounts.TryGetValue(key, out var account) && account.Removed.ContainsKey(id)));
            if (seeding.Pending.Count == 0) state.Seeds.Remove(id);
        }
        if (files.All(file => file.Readable))
            foreach (var id in state.Conflicts.Keys.ToArray())
                if (files.Where(file => file.Entries.ContainsKey(id)).Select(file => ClaudeScheduledRegistry.HashOf(file.Entries[id]))
                        .Distinct().Count() <= 1) state.Conflicts.Remove(id);
        var lineages = new HashSet<(string, string)>();
        foreach (var file in files.Where(file => file.Readable))
            foreach (var (id, entry) in file.Entries)
            {
                var lineage = ClaudeScheduledRegistry.Lineage(entry);
                lineages.Add((id, lineage));
                if (!state.Runs.TryGetValue(id, out var runs)) state.Runs[id] = runs = new(StringComparer.Ordinal);
                var known = runs.GetValueOrDefault(lineage);
                var (ran, slot) = (ClaudeScheduledRegistry.Text(entry, "lastRunAt"), ClaudeScheduledRegistry.Text(entry, "lastScheduledFor"));
                var at = slot ?? ran;
                // The app catches up from the slot it ran last, or its last run: the pair that is furthest on goes together.
                var newer = known is null || ClaudeScheduledRegistry.Compare(at, known.LastScheduledFor ?? known.LastRunAt) > 0;
                var retrying = file.Root?["runRetries"] is JsonObject retries && retries.ContainsKey(id);
                runs[lineage] = new(newer ? ran : known!.LastRunAt, newer ? slot : known!.LastScheduledFor,
                    ClaudeScheduledRegistry.Later(ClaudeScheduledRegistry.Text(entry, "missedRunScanFloor"), known?.Floor),
                    retrying ? known?.Completed : ClaudeScheduledRegistry.Later(at, known?.Completed));
            }
        foreach (var change in state.Changes) lineages.Add((change.Key, ClaudeScheduledRegistry.Lineage(change.Value.Content)));
        foreach (var seeding in state.Seeds) lineages.Add((seeding.Key, ClaudeScheduledRegistry.Lineage(seeding.Value.Content)));
        foreach (var (id, runs) in state.Runs.ToArray())
        {
            foreach (var lineage in runs.Keys.Where(lineage => !lineages.Contains((id, lineage))).ToArray()) runs.Remove(lineage);
            if (runs.Count == 0) state.Runs.Remove(id);
        }
    }

    internal bool HasDefinition(string id)
    {
        if (_definitions is null || !ClaudeScheduledRegistry.IsSafeId(id)) return false;
        try { return Directory.Exists(Path.Combine(_definitions, id)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>
    /// What each list that reads should become (<paramref name="op"/>: null for a normal pass, <c>seed</c> for the fill
    /// alone, <c>remove</c> for the deletions alone): the tasks deleted removed; every change as made, field by field on
    /// the entry there (its own and unknown fields kept, the permission mode only when the change was seen choosing it),
    /// in a list seen before the change; the tasks it lacks made, unless removed there, without a definition folder, or
    /// running with more permission than anyone chose; and the newest run of each task it holds. Nothing for a task in
    /// conflict. Other entries, their order and the file's other fields stay as they are.
    /// </summary>
    internal ClaudeScheduledPlan Plan(ClaudeScheduledState state, IReadOnlyList<ClaudeScheduledFile> files, string? op, DateTimeOffset now)
    {
        var present = Definitions();
        var definitions = new Dictionary<string, bool>(StringComparer.Ordinal);
        bool Defined(string id) => definitions.TryGetValue(id, out var found) ? found
            : definitions[id] = present?.Contains(id) == true || present is null && HasDefinition(id);
        var changes = new List<ClaudeScheduledChange>();
        var held = new List<(string, string)>();
        var tooLarge = new List<string>();
        foreach (var file in files)
        {
            if (!file.Readable || !state.Accounts.TryGetValue(file.Key, out var account)) continue;
            var root = file.Root is { } existing ? (JsonObject)existing.DeepClone() : new JsonObject { ["scheduledTasks"] = new JsonArray() };
            var tasks = root["scheduledTasks"]!.AsArray();
            var at = new Dictionary<string, int>(StringComparer.Ordinal);
            for (var index = 0; index < tasks.Count; index++)
                if (ClaudeScheduledRegistry.Text(tasks[index]!.AsObject(), "id") is { } id && ClaudeScheduledRegistry.IsSafeId(id)) at.TryAdd(id, index);
            List<string> created = [], updated = [], deleted = [], ran = [];
            var writes = new SortedDictionary<string, ClaudeScheduledState.Written>(StringComparer.Ordinal);
            var sources = new List<string[]>();
            string? Before(string id, string? hash) => account.Writes.TryGetValue(id, out var write) ? write.Before : hash;
            void Create(string id, JsonObject content, JsonObject extra, string? filePath, bool observed, string[] stamps)
            {
                if (at.ContainsKey(id) || account.Removed.ContainsKey(id) || !Defined(id)) return;
                if (ClaudeScheduledRegistry.Raises(ClaudeScheduledRegistry.Permission(content)) && !observed)
                {
                    held.Add((file.Key, id));
                    return;
                }
                var entry = new JsonObject { ["id"] = id };
                foreach (var (name, value) in content) entry[name] = value?.DeepClone();
                foreach (var (name, value) in extra) entry[name] = value?.DeepClone();
                entry["filePath"] = filePath ?? Path.Combine(_definitions!, id, "SKILL.md");
                tasks.Add(entry);
                at[id] = tasks.Count - 1;
                created.Add(id);
                writes[id] = new(now, Before(id, null), ClaudeScheduledRegistry.HashOf(entry));
                sources.Add(stamps);
            }
            if (op != "seed")
                foreach (var (id, deletion) in state.Deleted)
                    if (deletion.Pending.Contains(file.Key) && at.TryGetValue(id, out var index) && tasks[index] is not null)
                    {
                        tasks[index] = null;
                        deleted.Add(id);
                    }
            if (op is null)
                foreach (var (id, change) in state.Changes)
                {
                    if (state.Conflicts.ContainsKey(id) || state.Deleted.ContainsKey(id) || account.Since > change.Lower) continue;
                    if (!at.TryGetValue(id, out var index))
                    {
                        Create(id, change.Content, change.Extra, change.FilePath, change.PermissionObserved, change.Stamps);
                        continue;
                    }
                    if (tasks[index] is not JsonObject entry) continue;
                    var hash = ClaudeScheduledRegistry.HashOf(entry);
                    if (hash == change.Hash) continue;
                    var next = Apply(entry, change);
                    var nextHash = ClaudeScheduledRegistry.HashOf(next);
                    if (nextHash == hash) continue;
                    tasks[index] = next;
                    updated.Add(id);
                    writes[id] = new(now, Before(id, hash), nextHash);
                }
            if (op is null or "seed")
                foreach (var (id, seeding) in state.Seeds)
                {
                    if (!seeding.Pending.Contains(file.Key) || state.Conflicts.ContainsKey(id) || state.Deleted.ContainsKey(id)) continue;
                    // A change made since the fill was asked for is what is made.
                    if (state.Changes.TryGetValue(id, out var change) && change.Upper > seeding.At)
                        Create(id, change.Content, change.Extra, change.FilePath, change.PermissionObserved, change.Stamps);
                    else Create(id, seeding.Content, seeding.Extra, seeding.FilePath, false, seeding.Stamps);
                }
            if (op != "remove")
                for (var index = 0; index < tasks.Count; index++)
                {
                    if (tasks[index] is not JsonObject entry || ClaudeScheduledRegistry.Text(entry, "id") is not { } id
                        || !ClaudeScheduledRegistry.IsSafeId(id) || state.Conflicts.ContainsKey(id) || state.Deleted.ContainsKey(id)
                        || op == "seed" && !created.Contains(id) || !Defined(id)
                        || state.Runs.GetValueOrDefault(id)?.GetValueOrDefault(ClaudeScheduledRegistry.Lineage(entry)) is not { } run) continue;
                    if (Carry(root, entry, id, run) && !created.Contains(id) && !updated.Contains(id)) ran.Add(id);
                }
            for (var index = tasks.Count - 1; index >= 0; index--) if (tasks[index] is null) tasks.RemoveAt(index);
            if (created.Count + updated.Count + deleted.Count + ran.Count == 0) continue;
            // A list made here: the cron matcher upgrades the lists its tasks came from had are not done again on them.
            if (file.Root is null && sources.Count > 0)
                foreach (var stamp in ClaudeScheduledRegistry.MigrationStamps)
                    if (sources.All(source => source.Contains(stamp))) root[stamp] = true;
            var bytes = ClaudeScheduledRegistry.Serialize(root);
            if (bytes.LongLength > ClaudeScheduledRegistry.MaxBytes)
            {
                tooLarge.Add(file.Key);
                continue;
            }
            var after = new SortedDictionary<string, ClaudeScheduledState.Entry>(StringComparer.Ordinal);
            foreach (var (id, entry) in Current(tasks.Select(item => item!.AsObject())
                         .Where(item => ClaudeScheduledRegistry.Text(item, "id") is { } id && ClaudeScheduledRegistry.IsSafeId(id))
                         .GroupBy(item => ClaudeScheduledRegistry.Text(item, "id")!).ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal)))
                after[id] = entry;
            changes.Add(new(file.Key, file, root, bytes, created, updated, deleted, ran, after, writes));
        }
        return new(changes, held, tooLarge);
    }

    /// <summary>A change applied to the entry an account holds: its synced fields set or removed as the change has them
    /// (its permission mode only when the change was seen choosing it); a new run (a new due time) without the run times
    /// of the one before.</summary>
    private static JsonObject Apply(JsonObject entry, ClaudeScheduledState.Change change)
    {
        var next = (JsonObject)entry.DeepClone();
        foreach (var name in ClaudeScheduledRegistry.SyncedFields)
        {
            if (!change.PermissionObserved && name is "permissionMode" or "permissionModeCarried") continue;
            if (change.Content.TryGetPropertyValue(name, out var value)) next[name] = value?.DeepClone();
            else next.Remove(name);
        }
        if (ClaudeScheduledRegistry.Lineage(next) != ClaudeScheduledRegistry.Lineage(entry))
            foreach (var name in ClaudeScheduledRegistry.RunFields) next.Remove(name);
        return next;
    }

    /// <summary>Brings a run's newest run times to an entry of it: the pair furthest on (so the app catches up from where
    /// that account did), the latest scan floor, and no retry of a slot an account already finished.</summary>
    private static bool Carry(JsonObject root, JsonObject entry, string id, ClaudeScheduledState.Run run)
    {
        var moved = false;
        var mine = ClaudeScheduledRegistry.Text(entry, "lastScheduledFor") ?? ClaudeScheduledRegistry.Text(entry, "lastRunAt");
        if (ClaudeScheduledRegistry.Compare(run.LastScheduledFor ?? run.LastRunAt, mine) > 0)
        {
            foreach (var (name, value) in new[] { ("lastRunAt", run.LastRunAt), ("lastScheduledFor", run.LastScheduledFor) })
                if (value is null) entry.Remove(name);
                else entry[name] = value;
            moved = true;
        }
        if (run.Floor is { } floor && ClaudeScheduledRegistry.Compare(floor, ClaudeScheduledRegistry.Text(entry, "missedRunScanFloor")) > 0)
        {
            entry["missedRunScanFloor"] = floor;
            moved = true;
        }
        // The app drops a retry only once a later slot ran: one for a slot another account finished would run it again.
        if (run.Completed is { } done && root["runRetries"] is JsonObject retries && retries[id] is JsonObject retry
            && ClaudeScheduledRegistry.Text(retry, "slot") is { } slot && ClaudeScheduledRegistry.IsTime(slot)
            && ClaudeScheduledRegistry.Compare(slot, done) <= 0)
        {
            retries.Remove(id);
            if (retries.Count == 0) root.Remove("runRetries");
            moved = true;
        }
        return moved;
    }

    /// <summary>A fill asked for: every task (or each of the request's) that the lists holding it hold alike, with its
    /// definition folder, not deleted, removed nowhere, in no conflict, and not running with more permission than asking,
    /// is made in the lists lacking it (the one the app shows once it is closed). Nothing is deleted or changed.
    /// Refused while a list does not read.</summary>
    internal JsonObject Seed(ClaudeScheduledState state, IReadOnlyList<ClaudeScheduledFile> files, ClaudeScheduledRequest request,
        DateTimeOffset now, Func<string, string> label)
    {
        var unreadable = files.Where(file => !file.Readable).Select(file => label(file.Key)).ToArray();
        JsonArray filled = [], conflicts = [], skipped = [], everywhere = [];
        if (unreadable.Length > 0)
            return new JsonObject
            {
                ["status"] = "unreadable", ["fill"] = filled, ["conflicts"] = conflicts, ["skipped"] = skipped,
                ["alreadyEverywhere"] = everywhere, ["unreadable"] = Labels(unreadable)
            };
        var asked = request.Tasks.Count > 0;
        var ids = asked ? request.Tasks.Distinct(StringComparer.Ordinal).ToArray()
            : files.SelectMany(file => file.Entries.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        var incomplete = false;
        void Skip(string id, string reason, IEnumerable<string>? accounts = null)
        {
            var item = new JsonObject { ["id"] = id, ["reason"] = reason };
            if (accounts is not null) item["accounts"] = Labels(accounts.Select(label));
            skipped.Add(item);
            incomplete |= asked;
        }
        foreach (var id in ids)
        {
            if (!ClaudeScheduledRegistry.IsSafeId(id)) { Skip(id, "invalid"); continue; }
            var holders = files.Where(file => file.Entries.ContainsKey(id)).ToArray();
            var lacking = files.Where(file => !file.Entries.ContainsKey(id)).Select(file => file.Key).ToArray();
            if (holders.Length == 0) { Skip(id, "not_found"); continue; }
            if (lacking.Length == 0) { if (asked) everywhere.Add(id); continue; }
            if (state.Deleted.ContainsKey(id)) { Skip(id, "deleted"); continue; }
            var removedIn = state.Accounts.Where(pair => pair.Value.Removed.ContainsKey(id)).Select(pair => pair.Key).ToArray();
            if (removedIn.Length > 0) { Skip(id, "removed_in_one_account", removedIn); continue; }
            if (!HasDefinition(id)) { Skip(id, "no_definition"); continue; }
            var hashes = holders.Select(file => ClaudeScheduledRegistry.HashOf(file.Entries[id])).Distinct().ToArray();
            if (state.Conflicts.ContainsKey(id) || hashes.Length > 1)
            {
                conflicts.Add(new JsonObject { ["id"] = id, ["accounts"] = Labels(holders.Select(file => label(file.Key))) });
                incomplete |= asked;
                continue;
            }
            var source = holders[0];
            var entry = source.Entries[id];
            if (ClaudeScheduledRegistry.Raises(ClaudeScheduledRegistry.Permission(entry))) { Skip(id, "raises_permission"); continue; }
            state.Seeds[id] = new(now, source.Key, hashes[0], ClaudeScheduledRegistry.Synced(entry), ClaudeScheduledRegistry.Extra(entry),
                ClaudeScheduledRegistry.Text(entry, "filePath"), StampsOf(source.Root), new(lacking, StringComparer.Ordinal));
            filled.Add(new JsonObject { ["id"] = id, ["into"] = Labels(lacking.Select(label)) });
        }
        return new JsonObject
        {
            ["status"] = incomplete ? "incomplete" : "ok", ["fill"] = filled, ["conflicts"] = conflicts, ["skipped"] = skipped,
            ["alreadyEverywhere"] = everywhere, ["unreadable"] = new JsonArray()
        };
    }

    /// <summary>A removal asked for: each task is deleted from every account (the one the app shows once it is closed),
    /// with the previous lists kept beside the state, and never filled again.</summary>
    internal static JsonObject Remove(ClaudeScheduledState state, IReadOnlyList<ClaudeScheduledFile> files, ClaudeScheduledRequest request,
        DateTimeOffset now, Func<string, string> label)
    {
        JsonArray removed = [], skipped = [];
        var incomplete = request.Tasks.Count == 0;
        foreach (var id in request.Tasks.Distinct(StringComparer.Ordinal))
        {
            if (!ClaudeScheduledRegistry.IsSafeId(id))
            {
                skipped.Add(new JsonObject { ["id"] = id, ["reason"] = "invalid" });
                incomplete = true;
                continue;
            }
            var holders = files.Where(file => file.Entries.ContainsKey(id)).Select(file => file.Key).ToArray();
            if (holders.Length == 0 && !files.Any(file => !file.Readable) && !Tracked(state, files, id))
            {
                skipped.Add(new JsonObject { ["id"] = id, ["reason"] = "not_found" });
                incomplete = true;
                continue;
            }
            Delete(state, files, id, "command", now);
            removed.Add(new JsonObject { ["id"] = id, ["from"] = Labels(holders.Select(label)) });
        }
        return new JsonObject
        {
            ["status"] = incomplete ? "incomplete" : "ok", ["removed"] = removed, ["skipped"] = skipped,
            ["unreadable"] = Labels(files.Where(file => !file.Readable).Select(file => label(file.Key)))
        };
    }

    private static JsonArray Labels(IEnumerable<string> labels) => new(labels.Select(text => (JsonNode)text).ToArray());

    /// <summary>The tasks removed in some accounts alone that others still hold, each with where it is gone and where
    /// it is not.</summary>
    internal static IReadOnlyList<(string Id, string[] RemovedIn, string[] StillIn)> RemovedHere(ClaudeScheduledState state,
        IReadOnlyList<ClaudeScheduledFile> files) =>
        state.Accounts.SelectMany(pair => pair.Value.Removed.Keys).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal)
            .Where(id => !state.Deleted.ContainsKey(id))
            .Select(id => (id, state.Accounts.Where(pair => pair.Value.Removed.ContainsKey(id)).Select(pair => pair.Key).ToArray(),
                files.Where(file => file.Entries.ContainsKey(id)).Select(file => file.Key).ToArray()))
            .Where(item => item.Item3.Length > 0).ToArray();

    /// <summary>
    /// One pass: reads every list, settles writes a stopped widget began, records what changed, answers a request, and
    /// writes each list <paramref name="mayWrite"/> allows (asked again just before each file), under the account lease.
    /// A list that changed after it was read is left for the next pass, and one made meanwhile is not replaced. Before a
    /// list is replaced, its previous content is kept beside the state, and the write is kept as begun first.
    /// </summary>
    internal ClaudeScheduledPass Pass(IReadOnlyList<ClaudeShelfAccount> registered, ClaudeScheduledLogView? log, Func<string, bool> mayWrite,
        DateTimeOffset now, ClaudeScheduledRequest? request = null)
    {
        var labels = registered.Where(account => ClaudeScheduledRegistry.IsKey(account.Key))
            .DistinctBy(account => account.Key).ToDictionary(account => account.Key, account => account.Label, StringComparer.Ordinal);
        var (state, exists) = LoadState();
        JsonObject Answer(string status) => new() { ["status"] = status };
        if (state is null)
            return new(false, 0, [], [], "store_unreadable", request is null ? null : Answer("store_unreadable"));
        string Label(string key) => labels.GetValueOrDefault(key) ?? state.Labels.GetValueOrDefault(key) ?? key[..8];
        var kept = Shape(state);
        var keptAt = state.Accounts.Values.Select(account => (DateTimeOffset?)account.Seen).Max();
        var first = !exists;
        if (RootIdentity() is not { } root)
            return new(false, 0, [], [], null, request is null ? null : Answer("no_lists"));
        if (state.Root is { } before && before != root)
        {
            Rebase(state);
            first = true;
        }
        state.Root = root;
        foreach (var key in log?.Initialized ?? []) if (ClaudeScheduledRegistry.IsKey(key)) state.Known.Add(key);
        if (Observe(labels.Keys.Concat(state.Known).Concat(state.Accounts.Keys)) is not { } observed)
            return new(false, 0, [], [], "sessions_unlisted", request is null ? null : Answer("sessions_unlisted"));
        Recover(state, observed, now);
        var files = Settle(state, observed);
        ObserveDefinitions(state, files, now, first);
        Record(state, files, now, log);
        Track(state, files);
        foreach (var (key, label) in labels) state.Labels[key] = label;
        var answer = request switch
        {
            null => null,
            { Op: "seed" } => Seed(state, files, request, now, Label),
            { Op: "remove" } => Remove(state, files, request, now, Label),
            _ => Answer("invalid_request")
        };
        var unreadable = files.Where(file => !file.Readable).Select(file => Label(file.Key)).ToArray();
        var processed = files.ToDictionary(file => file.Key, file => file.Stamp ?? "?", StringComparer.Ordinal);
        IReadOnlyList<string> Conflicts() => state.Conflicts.Keys.ToArray();
        IReadOnlyList<string> Removed() => RemovedHere(state, files).Select(item => item.Id).ToArray();
        // Only when something besides the times the lists were last looked at changed, or those are getting old.
        var save = first || Shape(state) != kept || keptAt is not { } at || now - at >= SeenRefresh || at > now;
        if (save && !SaveState(state))
        {
            if (answer is not null) answer["status"] = "store_unwritable";
            return new(first, 0, unreadable, [], "store_unwritable", answer, [], Conflicts(), Removed(), [], processed);
        }
        // The first pass takes what is there as the starting point and changes nothing unless asked to.
        if (first && request is null) return new(true, 0, unreadable, [], null, null, [], Conflicts(), Removed(), [], processed);
        var plan = Plan(state, files, request?.Op, now);
        var written = 0;
        string? failure = null;
        List<string> waiting = [];
        if (plan.Changes.Count > 0)
        {
            IDisposable? lease = null;
            try
            {
                lease = _lease();
                foreach (var change in plan.Changes)
                {
                    if (!mayWrite(change.Key)) { waiting.Add(Label(change.Key)); continue; }
                    try
                    {
                        if (Write(state, change, () => mayWrite(change.Key), now) is { } stamp)
                        {
                            written++;
                            processed[change.Key] = stamp;
                        }
                        else waiting.Add(Label(change.Key));
                    }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                    {
                        failure = exception.Message;
                        waiting.Add(Label(change.Key));
                    }
                }
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
                                                  or TimeoutException)
            {
                // The installer or an account operation holds the lease: every list waits for the next pass.
                failure = exception.Message;
                waiting.AddRange(plan.Changes.Select(change => Label(change.Key)));
            }
            finally { lease?.Dispose(); }
            if (written > 0 && !SaveState(state)) failure ??= "store_unwritable";
        }
        if (answer is not null)
        {
            if (failure is not null && answer["status"]?.GetValue<string>() == "ok") answer["failure"] = failure;
            answer["written"] = written;
            // Lists not written in this pass: the one the app shows (written once it closes), or one it is loading.
            answer["waiting"] = Labels(waiting.Distinct());
            if (plan.Held.Count > 0) answer["held"] = Labels(plan.Held.Select(item => item.Id).Distinct());
            if (plan.TooLarge.Count > 0) answer["tooLarge"] = Labels(plan.TooLarge.Select(Label));
            answer["removedInOneAccountOnly"] = RemovedJson(RemovedHere(state, files), Label);
        }
        return new(first, written, unreadable, waiting.Distinct().ToArray(), failure, answer, plan.TooLarge.Select(Label).ToArray(),
            Conflicts(), Removed(), plan.Held.Select(item => item.Id).Distinct().ToArray(), processed);
    }

    internal static JsonArray RemovedJson(IEnumerable<(string Id, string[] RemovedIn, string[] StillIn)> items, Func<string, string> label) =>
        new(items.Select(item => (JsonNode)new JsonObject
        {
            ["id"] = item.Id, ["removedIn"] = Labels(item.RemovedIn.Select(label)), ["stillIn"] = Labels(item.StillIn.Select(label))
        }).ToArray());

    /// <summary>Replaces or makes one list: the new list is written beside it, the write is kept as begun, and only then
    /// is it moved into place, while the list still holds what was read and <paramref name="mayWrite"/> still allows it
    /// (a list that was not there is never replaced by one made meanwhile). The stamp of the list written, or null when
    /// it changed or may no longer be written.</summary>
    private string? Write(ClaudeScheduledState state, ClaudeScheduledChange change, Func<bool> mayWrite, DateTimeOffset now)
    {
        // What is written must read as the app reads it, or the app would load none of that account's tasks.
        if (!ClaudeScheduledRegistry.IsValid(JsonNode.Parse(change.Bytes)) || change.Bytes.LongLength > ClaudeScheduledRegistry.MaxBytes)
            throw new IOException("The aligned list does not pass the app's schema.");
        var path = change.File.Path;
        var folder = Path.GetDirectoryName(path)!;
        if (!Same(path, change.File.Bytes)) return null;
        // A partial a stopped widget left behind, once its write is settled.
        foreach (var stale in Directory.GetFiles(folder, PartialPattern))
            try { if (File.GetLastWriteTimeUtc(stale) < now.UtcDateTime.AddHours(-1) && state.Intents.Values.All(intent => intent.Partial != stale)) File.Delete(stale); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        if (change.File.Bytes is { } previous) KeepBackup(change.Key, previous, now);
        // Not a session record, tombstone or temporary record name the app's recovery looks at.
        var partial = Path.Combine(folder, ".scheduled-tasks." + Guid.NewGuid().ToString("N") + ".widget-sync.partial");
        var keepPartial = false;
        try
        {
            Commit(partial, change.Bytes);
            var stamp = ClaudeScheduledRegistry.CheapStamp(partial);
            if (!mayWrite() || !Same(path, change.File.Bytes)) return null;
            var at = _clock();
            var writes = new SortedDictionary<string, ClaudeScheduledState.Written>(StringComparer.Ordinal);
            foreach (var (id, write) in change.Writes) writes[id] = write with { At = at };
            state.Intents[change.Key] = new(at, partial, ClaudeScheduledRegistry.Sha(change.File.Bytes), ClaudeScheduledRegistry.Sha(change.Bytes)!,
                change.Entries, writes);
            if (!SaveState(state))
            {
                state.Intents.Remove(change.Key);
                throw new IOException("store_unwritable");
            }
            // From here the write kept as begun names the partial: while it does, the partial stays to tell it was not
            // moved (a widget stopped here leaves both).
            keepPartial = true;
            BeforeMoveForTest?.Invoke(path);
            try { File.Move(partial, path, overwrite: change.File.Bytes is not null); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // Not moved: the write did not happen. Kept as begun when that cannot be saved, the partial tells so.
                state.Intents.Remove(change.Key);
                keepPartial = !SaveState(state);
                if (change.File.Bytes is null && File.Exists(path)) return null;
                throw;
            }
            keepPartial = false;
            AfterMoveForTest?.Invoke(path);
            state.Intents.Remove(change.Key);
            var account = state.Accounts[change.Key];
            account.Entries = new(change.Entries, StringComparer.Ordinal);
            foreach (var id in change.Deleted) account.Writes.Remove(id);
            foreach (var (id, write) in writes) account.Writes[id] = write;
            account.Seen = now;
            return stamp;
        }
        finally { if (!keepPartial && File.Exists(partial)) TryDelete(partial); }
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
            TryDelete(old);
    }

    internal string BackupFolder(string key) => Path.Combine(_store, "backups", key.Replace('/', '_'));

    /// <summary>What the next pass would find and do, on a copy of the kept state: nothing is written.</summary>
    internal (ClaudeScheduledState State, IReadOnlyList<ClaudeScheduledFile> Files, bool First)? Preview(ClaudeScheduledState kept, bool exists,
        IReadOnlyList<ClaudeShelfAccount> registered, ClaudeScheduledLogView? log, DateTimeOffset now)
    {
        var state = kept.Clone();
        var first = !exists;
        if (RootIdentity() is not { } root) return null;
        if (state.Root is { } before && before != root) { Rebase(state); first = true; }
        state.Root = root;
        foreach (var key in log?.Initialized ?? []) if (ClaudeScheduledRegistry.IsKey(key)) state.Known.Add(key);
        if (Observe(registered.Select(account => account.Key).Concat(state.Known).Concat(state.Accounts.Keys)) is not { } observed) return null;
        Recover(state, observed, now, readOnly: true);
        var files = Settle(state, observed);
        ObserveDefinitions(state, files, now, first);
        Record(state, files, now, log);
        Track(state, files);
        return (state, files, first);
    }
}

/// <summary>Where <c>--claude-scheduled-seed</c> and <c>--claude-scheduled-remove</c> ask the running widget: a request
/// and an answer event and the shared memory carrying both, named after the widget's ledger folder (a test's folder
/// names its own; the path reads the same inside a packaged app). Named kernel objects reach the widget from a process
/// the app runs, where files it writes could be virtualized.</summary>
internal sealed class ClaudeScheduledSeedChannel : IDisposable
{
    private const long MailboxBytes = 1024 * 1024;
    private const int AnswerOffset = 65536;
    private const int MaxTasks = 256;
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

    /// <summary>A request waiting, if any: its id and what it asks (one that does not read asks for nothing valid).</summary>
    internal (string Id, ClaudeScheduledRequest Request)? Request()
    {
        if (!_requested.WaitOne(0)) return null;
        try
        {
            if (ClaudeSessionSync.ReadSlot(_view, 0, AnswerOffset) is not { } text || JsonNode.Parse(text) is not JsonObject request
                || request["id"] is not JsonValue value || !value.TryGetValue<string>(out var id) || id.Length is 0 or > 64) return null;
            var op = !request.TryGetPropertyValue("op", out var named) ? "seed"
                : named is JsonValue value2 && value2.TryGetValue<string>(out var asked) ? asked : "invalid";
            var tasks = !request.TryGetPropertyValue("tasks", out var listed) ? []
                : listed is JsonArray items ? items.Select(item => item is JsonValue task && task.TryGetValue<string>(out var name) ? name : null).ToArray()
                : [null];
            return op is "seed" or "remove" && tasks.Length <= MaxTasks && tasks.All(task => task is not null)
                ? (id, new ClaudeScheduledRequest(op, tasks.Select(task => task!).ToArray())) : (id, new ClaudeScheduledRequest("invalid", []));
        }
        catch (JsonException) { return null; }
    }

    internal void Answer(string id, string answer)
    {
        ClaudeSessionSync.WriteSlot(_view, AnswerOffset, MailboxBytes - AnswerOffset, Encoding.UTF8.GetBytes(id + "\n" + answer));
        _answered.Set();
    }

    /// <summary>Asks the running widget and waits for its answer; null without a widget or an answer in time.</summary>
    internal static string? Ask(string ledgerDirectory, TimeSpan wait, ClaudeScheduledRequest? request = null)
    {
        request ??= new("seed", []);
        if (request.Tasks.Count > MaxTasks) return null;
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
                        ClaudeSessionSync.WriteSlot(view, 0, AnswerOffset, JsonSerializer.SerializeToUtf8Bytes(new JsonObject
                        {
                            ["id"] = id, ["op"] = request.Op,
                            ["tasks"] = new JsonArray(request.Tasks.Select(task => (JsonNode)task).ToArray())
                        }));
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

/// <summary>The scheduled-task commands: a read-only plan, and a seed and a removal the running widget performs.</summary>
internal static class ClaudeScheduledCommand
{
    internal const string PlanFlag = "--claude-scheduled-plan";
    internal const string SeedFlag = "--claude-scheduled-seed";
    internal const string RemoveFlag = "--claude-scheduled-remove";
    private static readonly JsonSerializerOptions Output = new()
    { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Prints, as UTF-8 JSON and without writing anything: each list, whether a first pass is still to come (it
    /// would only take what is there, so the tasks held by some accounts alone are listed), the changes, conflicts,
    /// removals in one account only and deletions it finds, what the next pass would write to each list, and what a
    /// seed would fill and skip.</summary>
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
        ClaudeScheduledState? kept, bool exists, DateTimeOffset now, ClaudeScheduledLogView? log = null)
    {
        if (kept is null) return new JsonObject { ["status"] = "store_unreadable" };
        if (sync.Preview(kept, exists, accounts, log, now) is not { } preview) return new JsonObject { ["status"] = "no_lists" };
        var (state, files, first) = preview;
        var labels = accounts.DistinctBy(account => account.Key).ToDictionary(account => account.Key, account => account.Label, StringComparer.Ordinal);
        string Label(string key) => labels.GetValueOrDefault(key) ?? kept.Labels.GetValueOrDefault(key) ?? key[..8];
        JsonArray Names(IEnumerable<string> keys) => new(keys.Select(key => (JsonNode)Label(key)).ToArray());
        var result = new JsonObject
        {
            ["status"] = files.Count == 0 ? "no_lists" : "ok",
            ["firstPass"] = first,
            ["lists"] = new JsonArray(files.Select(file => (JsonNode)new JsonObject
            {
                ["account"] = Label(file.Key), ["key"] = file.Key, ["registered"] = labels.ContainsKey(file.Key),
                ["read"] = file.Status switch
                {
                    ClaudeScheduledReadStatus.Ok => "ok", ClaudeScheduledReadStatus.Missing => "missing",
                    _ => "unreadable:" + file.Problem
                },
                ["tasks"] = new JsonArray(file.Entries.Keys.Order(StringComparer.Ordinal).Select(id => (JsonNode)id).ToArray()),
                ["known"] = kept.Accounts.ContainsKey(file.Key)
            }).ToArray()),
            ["conflicts"] = new JsonArray(state.Conflicts.Select(pair => (JsonNode)new JsonObject { ["id"] = pair.Key, ["accounts"] = Names(pair.Value.Accounts) }).ToArray()),
            ["removedInOneAccountOnly"] = ClaudeScheduledSync.RemovedJson(ClaudeScheduledSync.RemovedHere(state, files), Label),
            ["deleted"] = new JsonArray(state.Deleted.Where(pair => pair.Value.Pending.Count > 0).Select(pair => (JsonNode)new JsonObject
            { ["id"] = pair.Key, ["by"] = pair.Value.By, ["pending"] = Names(pair.Value.Pending) }).ToArray()),
            ["fillsPending"] = new JsonArray(state.Seeds.Select(pair => (JsonNode)new JsonObject { ["id"] = pair.Key, ["into"] = Names(pair.Value.Pending) }).ToArray())
        };
        if (first)
        {
            // What a first pass leaves where it is: tasks some lists hold and others lack.
            var readable = files.Where(file => file.Readable).ToArray();
            result["notSpreadByFirstPass"] = new JsonArray(readable.SelectMany(file => file.Entries.Keys).Distinct().Order(StringComparer.Ordinal)
                .Where(id => readable.Any(file => !file.Entries.ContainsKey(id)))
                .Select(id => (JsonNode)new JsonObject
                {
                    ["id"] = id,
                    ["in"] = Names(readable.Where(file => file.Entries.ContainsKey(id)).Select(file => file.Key)),
                    ["notIn"] = Names(readable.Where(file => !file.Entries.ContainsKey(id)).Select(file => file.Key))
                }).ToArray());
        }
        else
        {
            result["changes"] = new JsonArray(state.Changes.Where(pair => !kept.Changes.TryGetValue(pair.Key, out var before)
                    || before.Hash != pair.Value.Hash || before.By != pair.Value.By)
                .Select(pair => (JsonNode)new JsonObject
                {
                    ["id"] = pair.Key, ["by"] = Label(pair.Value.By), ["at"] = pair.Value.Upper.ToString("O", CultureInfo.InvariantCulture)
                }).ToArray());
            var plan = sync.Plan(state, files, null, now);
            result["writes"] = Writes(plan, Label);
            result["held"] = new JsonArray(plan.Held.Select(item => (JsonNode)new JsonObject { ["id"] = item.Id, ["account"] = Label(item.Key) }).ToArray());
            result["tooLarge"] = Names(plan.TooLarge);
        }
        var seeded = state.Clone();
        result["seed"] = sync.Seed(seeded, files, new("seed", []), now, Label);
        result["seedWrites"] = Writes(sync.Plan(seeded, files, "seed", now), Label);
        return result;
    }

    private static JsonArray Writes(ClaudeScheduledPlan plan, Func<string, string> label) =>
        new(plan.Changes.Select(change => (JsonNode)new JsonObject
        {
            ["account"] = label(change.Key), ["key"] = change.Key,
            ["create"] = Ids(change.Created), ["update"] = Ids(change.Updated), ["delete"] = Ids(change.Deleted), ["runTimes"] = Ids(change.Runs)
        }).ToArray());

    private static JsonArray Ids(IEnumerable<string> ids) => new(ids.Select(id => (JsonNode)id).ToArray());

    /// <summary>The task ids after <c>--task</c> (and, with <paramref name="bare"/>, any other argument); null when an
    /// argument does not read.</summary>
    internal static IReadOnlyList<string>? Tasks(string[] args, bool bare)
    {
        var tasks = new List<string>();
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index].Equals("--task", StringComparison.OrdinalIgnoreCase))
            {
                if (++index >= args.Length) return null;
                tasks.Add(args[index]);
            }
            else if (bare && !args[index].StartsWith("--", StringComparison.Ordinal)) tasks.Add(args[index]);
            else return null;
        }
        return tasks;
    }

    /// <summary>Asks the running widget to seed: every task (or each <c>--task id</c>) the accounts holding it agree on
    /// is filled into the ones lacking it (the account the app shows once it is closed); nothing is deleted or changed.
    /// Exits 0 only when the fill was kept.</summary>
    internal static int RunSeed(string[] args) => Ask(args, "seed", bare: false);

    /// <summary>Asks the running widget to delete tasks (<c>id ...</c> or <c>--task id</c>) from every account (the
    /// account the app shows once it is closed), keeping the previous lists, and never fill them again.</summary>
    internal static int RunRemove(string[] args) => Ask(args, "remove", bare: true);

    private static int Ask(string[] args, string op, bool bare)
    {
        if (Tasks(args, bare) is not { } tasks || op == "remove" && tasks.Count == 0)
            return Print(new JsonObject { ["status"] = "usage" }, 2);
        var paths = ClaudeShelfPaths.CreateDefault();
        var answer = ClaudeScheduledSeedChannel.Ask(paths.LedgerDirectory, TimeSpan.FromSeconds(60), new(op, tasks));
        if (answer is null) return Print(new JsonObject { ["status"] = "widget_not_answering" }, 1);
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine(answer);
        return Exit(answer);
    }

    /// <summary>The exit code for the widget's answer: 0 only when it reads <c>ok</c> (everything asked was done or
    /// kept to be done).</summary>
    internal static int Exit(string answer)
    {
        try { return JsonNode.Parse(answer)?["status"] is JsonValue status && status.TryGetValue<string>(out var text) && text == "ok" ? 0 : 1; }
        catch (JsonException) { return 1; }
    }

    private static int Print(JsonObject value, int code)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine(value.ToJsonString(Output));
        return code;
    }
}
