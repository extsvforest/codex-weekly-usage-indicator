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

/// <summary>What the scheduled-task mirror left for the user (see <see cref="ClaudeShelfService"/>). <c>Conflict</c>
/// and <c>RemovedHere</c> are lines of the account panel from before the mirror; it never reports them.</summary>
internal enum ClaudeScheduledProblemKind { None, Unreviewed, Unreadable, Failed, TooLarge, Conflict, RemovedHere, Held, Differs, Stopped, Unfilled }

/// <summary>Why scheduled tasks are not being mirrored, or what mirroring left for the user to settle.
/// <see cref="Detail"/> names the tasks and accounts.</summary>
internal sealed record ClaudeScheduledProblem(ClaudeScheduledProblemKind Kind, string? Detail = null);

internal enum ClaudeScheduledReadStatus { Missing, Ok, Unreadable }

/// <summary>One account's scheduled-task list as read now. <see cref="Entries"/> are the entries with ids the app keeps,
/// by id, inside <see cref="Root"/>; <see cref="Bytes"/> the file as read (null when missing); <see cref="Stamp"/> its
/// size and last write as read; <see cref="FileId"/> and <see cref="FolderId"/> what the file and its folder are on disk.</summary>
internal sealed record ClaudeScheduledFile(string Key, string Path, ClaudeScheduledReadStatus Status, byte[]? Bytes,
    JsonObject? Root, IReadOnlyDictionary<string, JsonObject> Entries, DateTimeOffset? WrittenAt, string? Problem = null,
    string? Stamp = null, string? FileId = null, string? FolderId = null)
{
    internal bool Readable => Status != ClaudeScheduledReadStatus.Unreadable;
}

/// <summary>What the app's log says of its scheduled-task manager in this run (<see cref="ClaudeDesktopLogWatcher"/>):
/// whether every line of the run was read (<see cref="Known"/>), the list it has loaded (null: none, or one it is loading
/// while <see cref="Pending"/>) and when its last line was. <see cref="Mark"/> counts its lines.</summary>
internal sealed record ClaudeScheduledLogView(bool Known, string? Loaded, bool Pending, DateTimeOffset? LastLine, long Mark);

/// <summary>Collects what the app's log says of its scheduled-task manager (6.28899: a <c>Reset</c> line when it lets
/// its list go, an <c>Initialized</c> line naming the list it loaded), for <see cref="ClaudeScheduledLogView"/>. After
/// a reset, and at the start of a run, it holds no list until the next load.</summary>
internal sealed class ClaudeScheduledLog
{
    private string? _loaded;
    private bool _pending;
    private DateTimeOffset? _last;
    private long _mark;

    /// <summary>A run of the app read from its start.</summary>
    internal void Start() { _loaded = null; _pending = true; _last = null; }

    /// <summary>The app is closed: it holds no list.</summary>
    internal void Close() { _loaded = null; _pending = false; }

    internal void Reset(DateTimeOffset at) { _loaded = null; _pending = true; _last = at; _mark++; }

    internal void Initialized(string key, DateTimeOffset at) { _loaded = key; _pending = false; _last = at; _mark++; }

    internal ClaudeScheduledLogView View(bool known) => new(known, _loaded, _pending, _last, _mark);
}

/// <summary>A command the running widget performs (<see cref="ClaudeScheduledChannel"/>): <c>mirror</c> a task from one
/// account to every account (its plan; with <see cref="Apply"/> the plan whose token is <see cref="Expect"/>),
/// <c>unmirror</c> it, or <c>remove</c> a task that is not mirrored.</summary>
internal sealed record ClaudeScheduledRequest(string Op, string Task, string? From = null, bool Apply = false,
    string? Expect = null, bool AllowRaise = false, IReadOnlyList<string>? Replaces = null);

/// <summary>What a path is on disk with every link in it followed (its volume and file index, and its final path), how
/// many names it has, and its size and last write, read through one handle.</summary>
internal static class ClaudeFileIdentity
{
    internal sealed record Info(string Id, uint Links, long Length, DateTime LastWriteUtc);

    internal static Info? Of(string path)
    {
        try
        {
            using var handle = Open(path);
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

    /// <summary>Where a folder or file really is, every link followed; null when that cannot be told.</summary>
    internal static string? FinalPath(string path)
    {
        try
        {
            using var handle = Open(path);
            if (handle.IsInvalid) return null;
            var buffer = new StringBuilder(1024);
            var length = GetFinalPathNameByHandle(handle, buffer, buffer.Capacity, 0);
            if (length == 0 || length >= buffer.Capacity) return null;
            var final = buffer.ToString();
            return final.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase) ? @"\\" + final[8..]
                : final.StartsWith(@"\\?\", StringComparison.Ordinal) ? final[4..] : final;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or ArgumentException) { return null; }
    }

    // FILE_READ_ATTRIBUTES, every share mode, OPEN_EXISTING, FILE_FLAG_BACKUP_SEMANTICS (folders too).
    private static SafeFileHandle Open(string path) => CreateFile(Extended(path), 0x80, 7, IntPtr.Zero, 3, 0x02000000, IntPtr.Zero);

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "GetFinalPathNameByHandleW")]
    private static extern int GetFinalPathNameByHandle(SafeFileHandle file, StringBuilder path, int length, uint flags);
}

/// <summary>
/// The Claude app's scheduled-task list of one account and organization
/// (<c>claude-code-sessions\account\org\scheduled-tasks.json</c>), as Claude 6.28899 reads it: at most 10 MiB, and the
/// whole file passes its schema or the app loads none of it (and its next save keeps only what is made after).
/// <see cref="IsValid"/> mirrors that schema; a file it refuses is never written or counted from. A list is changed
/// whole as read: every other entry, nested value and file-level field stays as it is.
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
    /// <summary>The settings a mirror keeps the same in every account (the permission pair included). The other fields of
    /// an entry are each account's own; the run times are the app's.</summary>
    internal static readonly string[] Settings = ["displayName", "cronExpression", "enabled", "cwd", "model", "useWorktree",
        "sourceBranch", "permissionMode", "permissionModeCarried"];
    /// <summary>The file-level maps the app keeps by task id (6.28899): a task made under an id one of them still holds
    /// would take what is there (a retry run, skips).</summary>
    internal static readonly string[] TaskMaps = ["runRetries", "recordedSkips", "recordedPlaceSkips"];
    private static readonly string[] Strings = ["displayName", "migratedToRemoteAt", "migratedFromSpaceId", "cronExpression",
        "model", "lastRunAt", "lastScheduledFor", "missedRunScanFloor", "spaceId", "cwd", "sourceBranch", "notifySessionId"];
    private static readonly string[] Booleans = ["migratedFromRemote", "useWorktree", "disableJitter", "dispatchSubscribed"];
    private static readonly string[] StringArrays = ["userSelectedFolders", "userSelectedFiles", "userSelectedProjectUuids",
        "chromeAllowedDomains"];
    private static readonly string[] ChromeModes = ["ask", "skip_all_permission_checks", "follow_a_plan"];
    internal static readonly string[] PermissionModes = ["default", "acceptEdits", "plan", "bypassPermissions", "dontAsk", "auto"];
    private static readonly JsonSerializerOptions WriteOptions = new()
    { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    internal static readonly IReadOnlyDictionary<string, JsonObject> None = new Dictionary<string, JsonObject>(StringComparer.Ordinal);

    internal static bool IsSafeId(string? id) => id is not null && SafeId.IsMatch(id);

    /// <summary>An account/organization pair as this widget names it: both lowercase GUIDs.</summary>
    internal static bool IsKey(string? key) => key is not null && KeyPattern.IsMatch(key);

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
        try { root = JsonNode.Parse(bytes); if (!IsValid(root)) return Fails("schema"); }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException)
        { return Fails("json"); }
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
    /// checked; a JSON null is a bad value for every optional field, and a number has to be finite.</summary>
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

    internal static bool IsString(JsonObject entry, string name, bool required = false) =>
        entry.TryGetPropertyValue(name, out var value) ? value is JsonValue text && text.GetValueKind() == JsonValueKind.String : !required;

    // The app reads a JSON number as a double: 1e400 is Infinity there, which its schema refuses.
    internal static bool IsNumber(JsonObject entry, string name, bool required = false) =>
        entry.TryGetPropertyValue(name, out var value) ? IsFinite(value) : !required;

    internal static bool IsFinite(JsonNode? node) => Number(node) is not null;

    /// <summary>A JSON number as the app reads it (a double), read from its text so that a number made here reads as one
    /// parsed from a file; null when the node is no number or not a finite one.</summary>
    internal static double? Number(JsonNode? node) =>
        node is JsonValue number && number.GetValueKind() == JsonValueKind.Number
        && double.TryParse(number.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) && double.IsFinite(parsed)
            ? parsed : null;

    internal static bool IsBoolean(JsonObject entry, string name, bool required = false) =>
        entry.TryGetPropertyValue(name, out var value)
            ? value is JsonValue flag && flag.GetValueKind() is JsonValueKind.True or JsonValueKind.False : !required;

    private static bool IsStringArray(JsonObject entry, string name, bool required = false) =>
        entry.TryGetPropertyValue(name, out var value)
            ? value is JsonArray items && items.All(item => item is JsonValue text && text.GetValueKind() == JsonValueKind.String)
            : !required;

    private static bool IsOneOf(JsonObject entry, string name, string[] allowed) =>
        !entry.TryGetPropertyValue(name, out var value)
        || value is JsonValue text && text.GetValueKind() == JsonValueKind.String && allowed.Contains(text.GetValue<string>());

    /// <summary>Why a task is not one the mirror keeps (a recurring cron task run on this PC, in a folder of it), or null.
    /// A one-time task's runs and its switching off after one are the app's; a remote one runs elsewhere.</summary>
    internal static string? KindProblem(JsonObject entry) =>
        Text(entry, "cronExpression") is not { Length: > 0 } ? "not_cron"
        : entry.ContainsKey("fireAt") ? "one_time"
        : entry.ContainsKey("place") || entry["migratedToRemote"] is JsonValue moved && moved.GetValueKind() == JsonValueKind.True ? "remote"
        : entry.ContainsKey("watcher") ? "watcher"
        : Text(entry, "cwd") is not { Length: > 0 } ? "no_folder"
        : null;

    /// <summary>One setting of an entry as the mirror compares it: absent is null, and a carried mode is only a true
    /// <c>permissionModeCarried</c> (the app reads nothing else as carried).</summary>
    internal static JsonNode? Setting(JsonObject entry, string name) => name == "permissionModeCarried"
        ? entry[name] is JsonValue carried && carried.GetValueKind() == JsonValueKind.True ? JsonValue.Create(true) : null
        : entry[name]?.DeepClone();

    internal static JsonObject SettingsOf(JsonObject entry)
    {
        var settings = new JsonObject();
        foreach (var name in Settings)
            if (Setting(entry, name) is { } value) settings[name] = value;
        return settings;
    }

    /// <summary>The settings in which an entry differs from the mirror's: a field set on one side alone differs too.</summary>
    internal static string[] Differences(JsonObject entry, JsonObject settings) =>
        Settings.Where(name => Hash(Setting(entry, name)) != Hash(settings[name])).ToArray();

    /// <summary>Gives an entry the mirror's settings exactly, absent ones removed; nothing else changes.</summary>
    internal static void SetSettings(JsonObject entry, JsonObject settings)
    {
        foreach (var name in Settings)
            if (settings[name] is { } value) entry[name] = value.DeepClone();
            else entry.Remove(name);
    }

    internal static JsonArray Tasks(JsonObject root) => root["scheduledTasks"]!.AsArray();

    internal static JsonObject? Find(JsonObject root, string id) =>
        Tasks(root).OfType<JsonObject>().FirstOrDefault(entry => Text(entry, "id") == id);

    internal static void Drop(JsonObject root, Func<JsonObject, bool> match)
    {
        var tasks = Tasks(root);
        for (var index = tasks.Count - 1; index >= 0; index--)
            if (tasks[index] is JsonObject entry && match(entry)) tasks.RemoveAt(index);
    }

    /// <summary>A task's <c>createdAt</c> as written (the JSON number), or null.</summary>
    internal static string? Created(JsonObject entry) => IsFinite(entry["createdAt"]) ? entry["createdAt"]!.ToJsonString() : null;

    /// <summary>Whether an entry is the task made at <paramref name="createdAt"/> (a JSON number), as the app compares it.</summary>
    internal static bool SameCreated(JsonObject entry, string createdAt) =>
        Number(entry["createdAt"]) is { } at
        && double.TryParse(createdAt, NumberStyles.Float, CultureInfo.InvariantCulture, out var expected) && at == expected;

    internal static bool HasTaskMap(JsonObject root, string id) =>
        TaskMaps.Any(name => root[name] is JsonObject map && map.ContainsKey(id));

    internal static DateTimeOffset? Time(string? text) =>
        text is not null && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var at) ? at : null;

    /// <summary>A time as the app writes one (<c>Date.toISOString</c>).</summary>
    internal static string Iso(DateTimeOffset at) => at.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);

    /// <summary>A fingerprint of a JSON value that ignores the order of object members.</summary>
    internal static string Hash(JsonNode? node)
    {
        var text = new StringBuilder();
        Canonical(node, text);
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text.ToString())));
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

    internal static string? Text(JsonObject entry, string name) =>
        entry[name] is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;

    internal static byte[] Serialize(JsonObject root) => Encoding.UTF8.GetBytes(root.ToJsonString(WriteOptions));
}

/// <summary>What a task may start with, as an entry holds it: its permission mode (null: none set, so the session's own),
/// whether the app carries it over (<c>permissionModeCarried</c>) and whether it was imported (<c>importedFrom</c>).</summary>
internal readonly record struct ClaudeScheduledAccess(string? Mode, bool Carried, bool Imported)
{
    internal static ClaudeScheduledAccess Of(JsonObject entry) => new(ClaudeScheduledRegistry.Text(entry, "permissionMode"),
        ClaudeScheduledRegistry.Setting(entry, "permissionModeCarried") is not null, entry["importedFrom"] is not null);

    internal JsonObject Json() => new() { ["mode"] = Mode, ["carried"] = Carried, ["imported"] = Imported };

    /// <summary>As a plan shows it: the fields, and the mode a run starts in with the kill switch on and off.</summary>
    internal JsonObject Shown()
    {
        var shown = Json();
        shown["startsAs"] = new JsonObject
        {
            ["killSwitchOn"] = ClaudeScheduledPermission.StartName(this, killSwitch: true),
            ["killSwitchOff"] = ClaudeScheduledPermission.StartName(this, killSwitch: false)
        };
        return shown;
    }

    internal static ClaudeScheduledAccess? Read(JsonNode? node) =>
        node is JsonObject access && access.Count == 3 && access["mode"] is var mode
        && (mode is null || mode is JsonValue text && text.GetValueKind() == JsonValueKind.String
            && ClaudeScheduledRegistry.PermissionModes.Contains(text.GetValue<string>()))
        && ClaudeScheduledRegistry.IsBoolean(access, "carried", required: true) && ClaudeScheduledRegistry.IsBoolean(access, "imported", required: true)
        && access.ContainsKey("mode")
            ? new(mode?.GetValue<string>(), access["carried"]!.GetValue<bool>(), access["imported"]!.GetValue<bool>()) : null;
}

/// <summary>
/// The mode a scheduled run starts in (Claude 6.28899, <c>normalizeStartOptionsAtIngress</c>), for one entry, with the
/// kill switch for carried modes (flags not loaded, or gate 396417011 on) on and off. With it on, a carried mode above
/// Default, or none, starts as Default (<c>ur</c>); otherwise the run starts in the mode set, or with none set in the
/// session's own (null: unknown). An imported entry is under the import rules besides (<c>dr</c>: an imported, carried
/// Auto task starts in nothing above Default but Auto; and the update tool does not switch a switched-off import back
/// on), which a copy, never imported, is not.
/// </summary>
internal static class ClaudeScheduledPermission
{
    // 6.28899 nR.
    private static int Rank(string mode) => mode switch
    {
        "plan" => 0, "default" or "dontAsk" => 1, "acceptEdits" => 2, "auto" => 3, _ => 4
    };

    /// <summary>The mode a run of the entry starts in, ranked (null: the session's own, so unknown).</summary>
    internal static int? Start(ClaudeScheduledAccess access, bool killSwitch)
    {
        int? rank = access.Mode is null ? null : Rank(access.Mode);
        return killSwitch && access.Carried && rank is null or > 1 ? 1 : rank;
    }

    /// <summary>The start's mode by name, for a plan.</summary>
    internal static string StartName(ClaudeScheduledAccess access, bool killSwitch) =>
        killSwitch && access.Carried && (access.Mode is null || Rank(access.Mode) > 1) ? "default" : access.Mode ?? "(the session's own)";

    /// <summary>Whether a task held as <paramref name="before"/> may start with more as <paramref name="after"/>: the
    /// import rules lost, or with the kill switch on or off a higher start (the session's own counting as anything: above
    /// a set mode but the highest, below one but the lowest).</summary>
    internal static bool Raises(ClaudeScheduledAccess before, ClaudeScheduledAccess after)
    {
        if (before == after) return false;
        if (before.Imported && !after.Imported) return true;
        foreach (var killSwitch in new[] { false, true })
        {
            var higher = (Start(before, killSwitch), Start(after, killSwitch)) switch
            {
                (null, null) => false,
                (null, { } set) => set > 0,
                ({ } set, null) => set < 4,
                ({ } from, { } to) => to > from
            };
            if (higher) return true;
        }
        return false;
    }
}

/// <summary>A task's definition as the app runs it: the definitions folder (as named, and where it really is), its
/// <c>SKILL.md</c>, and what its own folder is on disk (a folder made again is another).</summary>
internal sealed record ClaudeScheduledDefinition(string Root, string RealRoot, string Skill, string Folder);

/// <summary>A mode change a person allowed for one account when mirroring (<c>--allow-raise</c>).</summary>
internal sealed record ClaudeScheduledAllowed(string Key, ClaudeScheduledAccess Before, ClaudeScheduledAccess After);

/// <summary>Why a mirror stopped until a person mirrors it again: <c>differs</c> (an account's settings), <c>identity</c>
/// (another task under its id), <c>kind</c> (no longer a recurring local task), <c>definition_missing</c> or
/// <c>definition_replaced</c>; with the accounts and fields.</summary>
internal sealed record ClaudeScheduledHold(string Reason, IReadOnlyList<(string Key, string[] Fields)> Accounts, DateTimeOffset Since);

/// <summary>A task a person chose to mirror: from which account's entry (its id and <c>createdAt</c>), its definition,
/// its settings as taken, whether that entry was imported, the mode changes allowed, the tasks it replaces, and a hold.</summary>
internal sealed record ClaudeScheduledMirror(string Id, string SourceKey, string SourceCreatedAt, ClaudeScheduledDefinition Definition,
    JsonObject Settings, bool SourceImported, IReadOnlyList<ClaudeScheduledAllowed> Allowed, IReadOnlyList<(string Id, string CreatedAt)> Replaces,
    DateTimeOffset ChosenAt, ClaudeScheduledHold? Hold)
{
    internal ClaudeScheduledAccess SourceAccess => new(ClaudeScheduledRegistry.Text(Settings, "permissionMode"),
        Settings["permissionModeCarried"] is not null, SourceImported);
    /// <summary>A copy made in another list: the same settings, never imported.</summary>
    internal ClaudeScheduledAccess CopyAccess => SourceAccess with { Imported = false };

    internal bool Allows(string key, ClaudeScheduledAccess before, ClaudeScheduledAccess after) =>
        Allowed.Any(allowed => allowed.Key == key && allowed.Before == before && allowed.After == after);

    /// <summary>The entry made in a list lacking it: the settings, the source's <c>createdAt</c>, the definition and a
    /// scan floor of now (as the app's own imports). No run times, approvals, Chrome sites, sessions or prompt record.</summary>
    internal JsonObject Copy(DateTimeOffset now)
    {
        var entry = new JsonObject { ["id"] = Id };
        ClaudeScheduledRegistry.SetSettings(entry, Settings);
        entry["filePath"] = Definition.Skill;
        entry["createdAt"] = JsonNode.Parse(SourceCreatedAt);
        entry["missedRunScanFloor"] = ClaudeScheduledRegistry.Iso(now);
        return entry;
    }

    private static string Time(DateTimeOffset at) => at.ToString("O", CultureInfo.InvariantCulture);

    internal JsonObject Body() => new()
    {
        ["id"] = Id,
        ["source"] = new JsonObject { ["key"] = SourceKey, ["createdAt"] = JsonNode.Parse(SourceCreatedAt) },
        ["definition"] = new JsonObject
        { ["root"] = Definition.Root, ["realRoot"] = Definition.RealRoot, ["skill"] = Definition.Skill, ["folder"] = Definition.Folder },
        ["settings"] = Settings.DeepClone(),
        ["sourceImported"] = SourceImported,
        ["allowed"] = new JsonArray(Allowed.Select(allowed => (JsonNode)new JsonObject
        { ["key"] = allowed.Key, ["before"] = allowed.Before.Json(), ["after"] = allowed.After.Json() }).ToArray()),
        ["replaces"] = new JsonArray(Replaces.Select(replaced => (JsonNode)new JsonObject
        { ["id"] = replaced.Id, ["createdAt"] = JsonNode.Parse(replaced.CreatedAt) }).ToArray()),
        ["chosenAt"] = Time(ChosenAt),
        ["hold"] = Hold is null ? null : new JsonObject
        {
            ["reason"] = Hold.Reason, ["since"] = Time(Hold.Since),
            ["accounts"] = new JsonArray(Hold.Accounts.Select(account => (JsonNode)new JsonObject
            { ["key"] = account.Key, ["fields"] = new JsonArray(account.Fields.Select(field => (JsonNode)field).ToArray()) }).ToArray())
        }
    };

    /// <summary>A body as <see cref="Body"/> writes it, read strictly: anything missing, added or of another type reads
    /// as null.</summary>
    internal static ClaudeScheduledMirror? Read(JsonNode? node)
    {
        try
        {
            var body = Members(node, "id", "source", "definition", "settings", "sourceImported", "allowed", "replaces", "chosenAt", "hold");
            var source = Members(body["source"], "key", "createdAt");
            var definition = Members(body["definition"], "root", "realRoot", "skill", "folder");
            var settings = body["settings"] as JsonObject ?? throw new InvalidDataException();
            var probe = new JsonObject { ["id"] = "x", ["enabled"] = true, ["filePath"] = "x", ["createdAt"] = 0 };
            foreach (var (name, value) in settings)
            {
                if (!ClaudeScheduledRegistry.Settings.Contains(name) || name == "enabled") continue;
                probe[name] = value?.DeepClone();
            }
            if (settings.Any(member => !ClaudeScheduledRegistry.Settings.Contains(member.Key))
                || !ClaudeScheduledRegistry.IsValidEntry(probe) || !ClaudeScheduledRegistry.IsBoolean(settings, "enabled", required: true)
                || !ClaudeScheduledRegistry.IsString(settings, "cronExpression", required: true)
                || settings.ContainsKey("permissionModeCarried") && ClaudeScheduledRegistry.Setting(settings, "permissionModeCarried") is null)
                throw new InvalidDataException();
            ClaudeScheduledHold? hold = null;
            if (body["hold"] is { } held)
            {
                var members = Members(held, "reason", "since", "accounts");
                hold = new(Text(members["reason"]), Array(members["accounts"]).Select(item =>
                {
                    var account = Members(item, "key", "fields");
                    return (Key(Text(account["key"])), Array(account["fields"]).Select(Text).ToArray());
                }).ToArray(), Moment(members["since"]));
            }
            else if (!body.ContainsKey("hold")) throw new InvalidDataException();
            var id = Text(body["id"]);
            return new(ClaudeScheduledRegistry.IsSafeId(id) ? id : throw new InvalidDataException(), Key(Text(source["key"])),
                Number(source["createdAt"]),
                new(Text(definition["root"]), Text(definition["realRoot"]), Text(definition["skill"]), Text(definition["folder"])),
                settings.DeepClone().AsObject(), Flag(body["sourceImported"]),
                Array(body["allowed"]).Select(item =>
                {
                    var allowed = Members(item, "key", "before", "after");
                    return new ClaudeScheduledAllowed(Key(Text(allowed["key"])),
                        ClaudeScheduledAccess.Read(allowed["before"]) ?? throw new InvalidDataException(),
                        ClaudeScheduledAccess.Read(allowed["after"]) ?? throw new InvalidDataException());
                }).ToArray(),
                Array(body["replaces"]).Select(item =>
                {
                    var replaced = Members(item, "id", "createdAt");
                    var replacedId = Text(replaced["id"]);
                    return (ClaudeScheduledRegistry.IsSafeId(replacedId) ? replacedId : throw new InvalidDataException(), Number(replaced["createdAt"]));
                }).ToArray(),
                Moment(body["chosenAt"]), hold);
        }
        catch (Exception exception) when (exception is InvalidDataException or InvalidOperationException or FormatException) { return null; }
    }

    private static JsonObject Members(JsonNode? node, params string[] names) =>
        node is JsonObject members && members.Count == names.Length && names.All(members.ContainsKey) ? members : throw new InvalidDataException();

    private static JsonArray Array(JsonNode? node) => node as JsonArray ?? throw new InvalidDataException();

    private static string Text(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : throw new InvalidDataException();

    private static string Key(string key) => ClaudeScheduledRegistry.IsKey(key) ? key : throw new InvalidDataException();

    private static bool Flag(JsonNode? node) =>
        node is JsonValue value && value.GetValueKind() is JsonValueKind.True or JsonValueKind.False ? value.GetValue<bool>() : throw new InvalidDataException();

    private static string Number(JsonNode? node) => ClaudeScheduledRegistry.IsFinite(node) ? node!.ToJsonString() : throw new InvalidDataException();

    private static DateTimeOffset Moment(JsonNode? node) =>
        DateTimeOffset.ParseExact(Text(node), "O", CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind);
}

/// <summary>
/// What this widget keeps to mirror scheduled tasks (<see cref="ClaudeScheduledSync.StateFile"/>, in its own folder): the
/// mirrors chosen, each with a fingerprint of its body. A body whose fingerprint does not match, or that does not read,
/// is kept as it is and its task frozen. A file of another format is not read at all (that of the first version is left
/// where it is, never taken).
/// </summary>
internal sealed class ClaudeScheduledState
{
    internal const string Format = "gfs-scheduled-mirror/2";
    internal SortedDictionary<string, ClaudeScheduledMirror> Mirrors { get; } = new(StringComparer.Ordinal);
    internal SortedDictionary<string, JsonNode?> Frozen { get; } = new(StringComparer.Ordinal);

    internal static ClaudeScheduledState? Read(byte[] bytes)
    {
        try
        {
            if (JsonNode.Parse(bytes) is not JsonObject root || root.Count != 2 || root["format"] is not JsonValue format
                || format.GetValueKind() != JsonValueKind.String || format.GetValue<string>() != Format || root["mirrors"] is not JsonObject mirrors)
                return null;
            var state = new ClaudeScheduledState();
            foreach (var (id, record) in mirrors)
            {
                if (!ClaudeScheduledRegistry.IsSafeId(id)) return null;
                var mirror = record is JsonObject { Count: 2 } kept && kept["sha256"] is JsonValue hash && hash.GetValueKind() == JsonValueKind.String
                             && string.Equals(hash.GetValue<string>(), ClaudeScheduledRegistry.Hash(kept["body"]), StringComparison.OrdinalIgnoreCase)
                    ? ClaudeScheduledMirror.Read(kept["body"]) : null;
                if (mirror is not null && mirror.Id == id) state.Mirrors[id] = mirror;
                else state.Frozen[id] = record?.DeepClone();
            }
            return state;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException or InvalidOperationException) { return null; }
    }

    internal JsonObject Save()
    {
        var mirrors = new JsonObject();
        foreach (var (id, record) in Frozen) mirrors[id] = record?.DeepClone();
        foreach (var (id, mirror) in Mirrors)
        {
            var body = mirror.Body();
            mirrors[id] = new JsonObject { ["body"] = body, ["sha256"] = ClaudeScheduledRegistry.Hash(body) };
        }
        return new JsonObject { ["format"] = Format, ["mirrors"] = mirrors };
    }
}

/// <summary>What one look at the lists found: each list (the registered accounts' among them, read or not), their
/// labels, the list the app's log shows loaded (null: none, or unknown while <see cref="Known"/> is false), and the
/// kept state (null, and why, when it does not read).</summary>
internal sealed record ClaudeScheduledScene(IReadOnlyList<ClaudeScheduledFile> Files, IReadOnlyList<ClaudeShelfAccount> Registered,
    bool Running, string? Loaded, bool Known, ClaudeScheduledState? State, string? StateProblem)
{
    internal string Label(string key) => Registered.FirstOrDefault(account => account.Key == key)?.Label ?? key[..8];
}

/// <summary>A plan for one command: its answer, its token, and per list what would be written (null: nothing).</summary>
internal sealed record ClaudeScheduledPlan(JsonObject Answer, string? Token, IReadOnlyList<ClaudeScheduledChange> Changes,
    ClaudeScheduledMirror? Mirror = null, IReadOnlyList<ClaudeScheduledAllowed>? Raising = null)
{
    internal bool Refused => Token is null;
}

internal sealed record ClaudeScheduledChange(ClaudeScheduledFile File, JsonObject Root, JsonObject Target);

internal sealed record ClaudeScheduledPass(int Written, IReadOnlyList<string> Waiting, IReadOnlyList<string> Unreadable,
    string? Failure, ClaudeScheduledProblem? Notice, IReadOnlyDictionary<string, string>? Processed = null, JsonArray? Report = null);

/// <summary>
/// Mirrors the scheduled tasks a person chose (<c>--claude-scheduled-mirror</c>) into the lists of the accounts on this
/// PC. A mirror takes one account's entry as it is, and only an explicit apply of its plan changes an entry that is
/// there. Between applies, each pass only makes the task in a list lacking it, raises the scan floor of a copy past the
/// slots another account ran, and reports what differs: once an account's entry differs from the mirror (or is another
/// task under the id, or no longer a recurring local one, or the definition went), the task is held everywhere until
/// it is mirrored again. Lists that do not read are neither written nor counted from, a list is written whole as read,
/// and only a list the app has not loaded (<see cref="Pass"/>'s <c>mayWrite</c>).
/// </summary>
internal sealed class ClaudeScheduledSync
{
    internal const string StoreFolder = "scheduled-tasks";
    internal const string StateFile = "mirror-v2.json";
    private const int Backups = 5;
    private static int s_writeCalls;
    private readonly string _sessionsRoot;
    private readonly string _store;
    private readonly string? _definitions, _appDefinitions;
    private readonly Func<IDisposable> _lease;
    private readonly bool _readOnly;

    /// <param name="definitionsRoot">The definitions folder as this widget finds it.</param>
    /// <param name="appDefinitionsRoot">The one the app uses (the user's <c>CLAUDE_CONFIG_DIR</c>): nothing is
    /// written while the two differ.</param>
    /// <param name="readOnly">A plan made outside the widget: every write throws.</param>
    internal ClaudeScheduledSync(string sessionsRoot, string storeDirectory, string? definitionsRoot, string? appDefinitionsRoot,
        Func<IDisposable> lease, bool readOnly = false)
    {
        _sessionsRoot = sessionsRoot; _store = storeDirectory; _definitions = definitionsRoot; _appDefinitions = appDefinitionsRoot;
        _lease = lease; _readOnly = readOnly;
    }

    /// <summary>How many times this process entered a function that writes (a list, a backup, the state).</summary>
    internal static int WriteCalls => Volatile.Read(ref s_writeCalls);
    internal string StatePath => Path.Combine(_store, StateFile);
    internal string BackupFolder(string key) => Path.Combine(_store, "backups", key.Replace('/', '_'));
    // For tests: runs just before a list is moved into place.
    internal Action<string>? BeforeMoveForTest { get; set; }

    /// <summary>The definitions folder the app uses on this PC: the user's (or the machine's) <c>CLAUDE_CONFIG_DIR</c>,
    /// else <c>~\.claude</c>.</summary>
    internal static string? AppDefinitionsRoot()
    {
        var configured = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR", EnvironmentVariableTarget.User)
                         ?? Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR", EnvironmentVariableTarget.Machine);
        return ClaudeShelfPaths.AppConfigDirectory(configured, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile)) is { } config
            ? Path.Combine(config, "scheduled-tasks") : null;
    }

    private void Writing()
    {
        Interlocked.Increment(ref s_writeCalls);
        if (_readOnly) throw new InvalidOperationException("A plan made outside the widget writes nothing.");
    }

    private string FolderOf(string key)
    {
        var parts = key.Split('/');
        return Path.Combine(_sessionsRoot, parts[0], parts[1]);
    }

    private static bool IsLink(string folder)
    {
        var info = new DirectoryInfo(folder);
        return info.Exists && (info.Attributes & FileAttributes.ReparsePoint) != 0;
    }

    /// <summary>Each list as read now: every account/organization folder holding one, and each of
    /// <paramref name="wanted"/> (missing when it holds none); null when the profile folder cannot be listed. A list
    /// behind a link, or reached by two keys, reads as unreadable. Folders are never made.</summary>
    internal IReadOnlyList<ClaudeScheduledFile>? Observe(IEnumerable<string> wanted)
    {
        var want = wanted.Where(ClaudeScheduledRegistry.IsKey).ToHashSet(StringComparer.Ordinal);
        var folders = new SortedDictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (Directory.Exists(_sessionsRoot))
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
                        if (want.Contains(key) || File.Exists(Path.Combine(org, ClaudeScheduledRegistry.FileName))) folders.TryAdd(key, org);
                    }
                }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
        foreach (var key in want) folders.TryAdd(key, FolderOf(key));
        var files = new List<ClaudeScheduledFile>();
        foreach (var (key, folder) in folders)
        {
            var path = Path.Combine(folder, ClaudeScheduledRegistry.FileName);
            ClaudeScheduledFile file;
            try
            {
                file = IsLink(Path.GetDirectoryName(folder)!) || IsLink(folder)
                    ? ClaudeScheduledRegistry.Unread(key, path, "redirected") : ClaudeScheduledRegistry.Read(key, path);
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            { file = ClaudeScheduledRegistry.Unread(key, path, "io"); }
            if (file.Status == ClaudeScheduledReadStatus.Missing) { files.Add(file); continue; }
            var folderId = ClaudeFileIdentity.Of(folder)?.Id;
            files.Add(folderId is null && file.Readable ? ClaudeScheduledRegistry.Unread(key, path, "io") : file with { FolderId = folderId });
        }
        var shared = files.Where(file => file.FolderId is not null).GroupBy(file => file.FolderId)
            .Concat(files.Where(file => file.FileId is not null).GroupBy(file => file.FileId))
            .Where(group => group.Count() > 1).SelectMany(group => group).Select(file => file.Key).ToHashSet(StringComparer.Ordinal);
        return files.Select(file => shared.Contains(file.Key)
            ? ClaudeScheduledRegistry.Unread(file.Key, file.Path, "shared") with { FolderId = file.FolderId } : file).ToArray();
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

    /// <summary>The lists, the app's loaded one (from its log while it runs) and the kept state; null when the profile
    /// folder cannot be listed.</summary>
    internal ClaudeScheduledScene? Scene(IReadOnlyList<ClaudeShelfAccount> registered, ClaudeScheduledLogView? log, bool running)
    {
        var known = running && log is { Known: true, Pending: false, Loaded: not null };
        var loaded = known ? log!.Loaded : null;
        var accounts = registered.Where(account => ClaudeScheduledRegistry.IsKey(account.Key)).DistinctBy(account => account.Key).ToArray();
        if (Observe(accounts.Select(account => account.Key).Concat(loaded is null ? [] : [loaded])) is not { } files) return null;
        var (state, problem) = LoadState();
        return new(files, accounts, running, loaded, known || !running, state, problem);
    }

    internal (ClaudeScheduledState? State, string? Problem) LoadState()
    {
        try
        {
            var info = new FileInfo(StatePath);
            if (!info.Exists) return (new ClaudeScheduledState(), null);
            if ((info.Attributes & FileAttributes.ReparsePoint) != 0) return (null, "store_unreadable");
            return ClaudeScheduledState.Read(File.ReadAllBytes(StatePath)) is { } state ? (state, null) : (null, "store_unreadable");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return (null, "store_unreadable"); }
    }

    internal bool SaveState(ClaudeScheduledState state)
    {
        Writing();
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
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
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

    private static bool SamePath(string first, string second) => string.Equals(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(first)), Path.TrimEndingDirectorySeparator(Path.GetFullPath(second)),
        StringComparison.OrdinalIgnoreCase);

    /// <summary>A task's definition as the app would run it, or why not: <c>root</c> (the app uses another definitions
    /// folder, or none is known), <c>unlisted</c> (the folder cannot be looked at now), <c>missing</c> (no folder for the
    /// task), <c>unready</c> (the task's folder or its SKILL.md is a link, or SKILL.md is not a file that reads).</summary>
    internal (ClaudeScheduledDefinition? Definition, string? Problem) Definition(string id)
    {
        if (string.IsNullOrEmpty(_definitions) || string.IsNullOrEmpty(_appDefinitions) || !SamePath(_definitions, _appDefinitions))
            return (null, "root");
        try
        {
            if (!Directory.Exists(_definitions) || ClaudeFileIdentity.FinalPath(_definitions) is not { } real) return (null, "unlisted");
            var folder = Path.Combine(_definitions, id);
            var folderInfo = new DirectoryInfo(folder);
            if (!folderInfo.Exists) return File.Exists(folder) ? (null, "unready") : (null, "missing");
            var skill = Path.Combine(folder, "SKILL.md");
            var skillInfo = new FileInfo(skill);
            if ((folderInfo.Attributes & FileAttributes.ReparsePoint) != 0 || !skillInfo.Exists
                || (skillInfo.Attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0) return (null, "unready");
            using (new FileStream(skill, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete)) { }
            return ClaudeFileIdentity.Of(folder)?.Id is { } identity ? (new(_definitions, real, skill, identity), null) : (null, "unlisted");
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return (null, "unready"); }
    }

    /// <summary>Whether a mirror's definition is still the one chosen: null when it is; else a lasting hold (gone, or
    /// made again or moved), or a passing reason to wait (<see cref="Definition"/>'s other problems).</summary>
    private (ClaudeScheduledHold? Hold, string? Wait) DefinitionStill(ClaudeScheduledMirror mirror, DateTimeOffset now)
    {
        var (definition, problem) = Definition(mirror.Id);
        if (problem == "missing") return (new("definition_missing", [], now), null);
        if (definition is null) return (null, "definition_" + problem);
        return !SamePath(definition.RealRoot, mirror.Definition.RealRoot) || definition.Folder != mirror.Definition.Folder
            || !SamePath(definition.Skill, mirror.Definition.Skill) ? (new("definition_replaced", [], now), null) : (null, null);
    }

    /// <summary>What holds a mirror until it is mirrored again, as the readable lists show it: another task under its id
    /// (another <c>createdAt</c>), one no longer recurring and local, or settings that differ, in any account, the one
    /// the app has loaded included.</summary>
    private static ClaudeScheduledHold? Differing(ClaudeScheduledMirror mirror, ClaudeScheduledScene scene, DateTimeOffset now)
    {
        List<(string, string[])> identity = [], kind = [], differs = [];
        foreach (var file in scene.Files.Where(file => file.Status == ClaudeScheduledReadStatus.Ok))
        {
            if (!file.Entries.TryGetValue(mirror.Id, out var entry)) continue;
            if (!ClaudeScheduledRegistry.SameCreated(entry, mirror.SourceCreatedAt)) identity.Add((file.Key, ["createdAt"]));
            else if (ClaudeScheduledRegistry.KindProblem(entry) is { } problem) kind.Add((file.Key, [problem]));
            else if (ClaudeScheduledRegistry.Differences(entry, mirror.Settings) is { Length: > 0 } fields) differs.Add((file.Key, fields));
        }
        return identity.Count > 0 ? new("identity", identity, now) : kind.Count > 0 ? new("kind", kind, now)
            : differs.Count > 0 ? new("differs", differs, now) : null;
    }

    /// <summary>The newest slot of a mirror an account ran: the <c>lastScheduledFor</c> of the entries of the task itself
    /// (its <c>createdAt</c>) on its cron, switched on, not ahead of now, and not below that entry's own scan floor (a
    /// slot from before its cron was changed or it was switched on, which both raise its floor). A run started by hand
    /// leaves none.</summary>
    private static DateTimeOffset? Ran(ClaudeScheduledMirror mirror, ClaudeScheduledScene scene, DateTimeOffset now) =>
        scene.Files.Where(file => file.Status == ClaudeScheduledReadStatus.Ok)
            .Select(file => file.Entries.GetValueOrDefault(mirror.Id))
            .Where(entry => entry is not null && ClaudeScheduledRegistry.SameCreated(entry, mirror.SourceCreatedAt)
                            && ClaudeScheduledRegistry.Text(entry, "cronExpression") == ClaudeScheduledRegistry.Text(mirror.Settings, "cronExpression")
                            && entry["enabled"] is JsonValue on && on.GetValueKind() == JsonValueKind.True)
            .Select(entry => (Slot: ClaudeScheduledRegistry.Time(ClaudeScheduledRegistry.Text(entry!, "lastScheduledFor")),
                Floor: ClaudeScheduledRegistry.Time(ClaudeScheduledRegistry.Text(entry!, "missedRunScanFloor"))))
            .Where(ran => ran.Slot is { } slot && slot <= now && (ran.Floor is not { } floor || slot >= floor))
            .Select(ran => ran.Slot).Max();

    private static JsonObject Labelled(ClaudeScheduledScene scene, string key) => new() { ["account"] = scene.Label(key), ["key"] = key };

    private static JsonArray Names(IEnumerable<string> names) => new(names.Select(name => (JsonNode)name).ToArray());

    private static JsonNode HoldJson(ClaudeScheduledHold hold, ClaudeScheduledScene scene) => new JsonObject
    {
        ["reason"] = hold.Reason, ["since"] = hold.Since.ToString("O", CultureInfo.InvariantCulture),
        ["accounts"] = new JsonArray(hold.Accounts.Select(account =>
        {
            var item = Labelled(scene, account.Key);
            item["fields"] = Names(account.Fields);
            return (JsonNode)item;
        }).ToArray())
    };

    /// <summary>How the plan shows a setting: its value, or that none is set (a model then comes from the definition).</summary>
    private static JsonNode? Shown(JsonNode? value, string name) =>
        value?.DeepClone() ?? (name == "model" ? "(none: the definition's)" : "(none)");

    private static string TokenOf(JsonNode parts) => ClaudeScheduledRegistry.Hash(parts)[..16].ToLowerInvariant();

    /// <summary>
    /// The plan for mirroring <see cref="ClaudeScheduledRequest.Task"/> from the account <see cref="ClaudeScheduledRequest.From"/>
    /// names (an <c>account:org</c> key, a label naming one key, or <c>loaded</c>): every readable list gets that entry's
    /// settings (a missing entry is made, another task under the id replaced, one that differs fixed), and the tasks it
    /// replaces removed in the same write. Its token covers the source, the settings, the replaced tasks and every
    /// list's entry before and after; <c>--apply</c> writes only the plan with that token.
    /// </summary>
    internal ClaudeScheduledPlan PlanMirror(ClaudeScheduledRequest request, ClaudeScheduledScene scene, DateTimeOffset now)
    {
        var answer = new JsonObject { ["status"] = "plan", ["task"] = request.Task };
        ClaudeScheduledPlan Refuse(string reason, string? detail = null)
        {
            answer["status"] = "refused"; answer["reason"] = reason;
            if (detail is not null) answer["detail"] = detail;
            return new(answer, null, []);
        }
        if (!ClaudeScheduledRegistry.IsSafeId(request.Task)) return Refuse("invalid_task");
        var replaces = (request.Replaces ?? []).Distinct(StringComparer.Ordinal).ToArray();
        if (replaces.FirstOrDefault(id => !ClaudeScheduledRegistry.IsSafeId(id) || id == request.Task) is { } bad) return Refuse("invalid_replaces", bad);
        if (scene.State is not { } state) return Refuse(scene.StateProblem ?? "store_unreadable");
        if (replaces.FirstOrDefault(id => state.Mirrors.ContainsKey(id) || state.Frozen.ContainsKey(id)) is { } mirrored)
            return Refuse("replaces_mirrored", mirrored);
        var (sourceKey, fromProblem) = From(request.From, scene);
        if (sourceKey is null) return Refuse(fromProblem!, request.From);
        answer["from"] = Labelled(scene, sourceKey);
        var source = scene.Files.FirstOrDefault(file => file.Key == sourceKey);
        if (source is not { Status: ClaudeScheduledReadStatus.Ok }) return Refuse(source is null || source.Status == ClaudeScheduledReadStatus.Missing ? "source_missing" : "source_unreadable");
        if (!source.Entries.TryGetValue(request.Task, out var sourceEntry)) return Refuse("not_in_source");
        if (ClaudeScheduledRegistry.KindProblem(sourceEntry) is { } kind) return Refuse("not_recurring_local", kind);
        var (definition, definitionProblem) = Definition(request.Task);
        if (definition is null) return Refuse("definition_" + definitionProblem);
        foreach (var file in scene.Files.Where(file => file.Status == ClaudeScheduledReadStatus.Ok))
            if (file.Entries.TryGetValue(request.Task, out var other) && ClaudeScheduledRegistry.KindProblem(other) is { } otherKind)
                return Refuse("target_not_recurring_local", scene.Label(file.Key) + ": " + otherKind);
        var created = ClaudeScheduledRegistry.Created(sourceEntry)!;
        var replaced = scene.Files.Where(file => file.Status == ClaudeScheduledReadStatus.Ok)
            .SelectMany(file => replaces.Select(id => file.Entries.GetValueOrDefault(id)))
            .Where(entry => entry is not null).Select(entry => (ClaudeScheduledRegistry.Text(entry!, "id")!, ClaudeScheduledRegistry.Created(entry!)!))
            .Distinct().OrderBy(pair => pair.Item1, StringComparer.Ordinal).ThenBy(pair => pair.Item2, StringComparer.Ordinal).ToArray();
        var mirror = new ClaudeScheduledMirror(request.Task, sourceKey, created, definition, ClaudeScheduledRegistry.SettingsOf(sourceEntry),
            sourceEntry["importedFrom"] is not null, [], replaced, now, null);
        var settingsShown = new JsonObject();
        foreach (var name in ClaudeScheduledRegistry.Settings) settingsShown[name] = Shown(mirror.Settings[name], name);
        answer["settings"] = settingsShown;
        answer["source"] = new JsonObject { ["createdAt"] = JsonNode.Parse(created), ["permission"] = mirror.SourceAccess.Shown() };
        answer["notCopied"] = Names(sourceEntry.Select(member => member.Key).Where(name => name is not ("id" or "filePath" or "createdAt"
            or "lastRunAt" or "lastScheduledFor" or "missedRunScanFloor") && !ClaudeScheduledRegistry.Settings.Contains(name)).Order(StringComparer.Ordinal));
        answer["replaces"] = new JsonArray(replaced.Select(pair => (JsonNode)new JsonObject { ["id"] = pair.Item1, ["createdAt"] = JsonNode.Parse(pair.Item2) }).ToArray());
        answer["app"] = !scene.Running ? "closed" : scene.Loaded is { } loaded ? "shows " + scene.Label(loaded) : "loaded list unknown";
        var targets = new JsonArray();
        var tokens = new JsonArray();
        var changes = new List<ClaudeScheduledChange>();
        var raising = new List<ClaudeScheduledAllowed>();
        foreach (var file in scene.Files)
        {
            var target = Labelled(scene, file.Key);
            targets.Add(target);
            if (scene.Loaded is { } shown && (file.Key == shown || SameFolder(file.Key, shown))) target["loaded"] = true;
            if (file.Status != ClaudeScheduledReadStatus.Ok)
            {
                target["action"] = file.Status == ClaudeScheduledReadStatus.Missing ? "no_list" : "unreadable";
                target["note"] = file.Status == ClaudeScheduledReadStatus.Missing
                    ? "no list yet: open this account in the app once, then mirror again" : "not read (" + file.Problem + "): left out";
                tokens.Add(new JsonObject { ["key"] = file.Key, ["action"] = target["action"]!.GetValue<string>() });
                continue;
            }
            var root = file.Root!.DeepClone().AsObject();
            var existing = file.Entries.GetValueOrDefault(request.Task);
            var removals = new JsonArray();
            foreach (var (id, at) in replaced)
                if (file.Entries.GetValueOrDefault(id) is { } old && ClaudeScheduledRegistry.SameCreated(old, at))
                    removals.Add(new JsonObject
                    {
                        ["id"] = id, ["createdAt"] = JsonNode.Parse(at), ["enabled"] = old["enabled"]?.DeepClone(),
                        ["permission"] = ClaudeScheduledAccess.Of(old).Shown(), ["hash"] = ClaudeScheduledRegistry.Hash(old)
                    });
            ClaudeScheduledRegistry.Drop(root, entry => replaced.Any(pair => ClaudeScheduledRegistry.Text(entry, "id") == pair.Item1
                && ClaudeScheduledRegistry.SameCreated(entry, pair.Item2)));
            var floor = false;
            var raise = false;
            string action;
            if (existing is null || !ClaudeScheduledRegistry.SameCreated(existing, created))
            {
                action = existing is null ? "insert" : "replace";
                if (ClaudeScheduledRegistry.HasTaskMap(file.Root!, request.Task))
                {
                    target["action"] = "held";
                    target["reason"] = "retries_or_skips_left";
                    target["note"] = "the list still keeps runs of a task under this id: not made here until the app has settled them";
                    tokens.Add(new JsonObject { ["key"] = file.Key, ["action"] = "held", ["before"] = existing is null ? "-" : ClaudeScheduledRegistry.Hash(existing) });
                    continue;
                }
                // A copy is weighed against the source entry, and against the entry it replaces.
                raise = ClaudeScheduledPermission.Raises(mirror.SourceAccess, mirror.CopyAccess)
                        || existing is not null && ClaudeScheduledPermission.Raises(ClaudeScheduledAccess.Of(existing), mirror.CopyAccess);
                target["permission"] = new JsonObject
                {
                    ["before"] = existing is null ? "(no entry here)" : ClaudeScheduledAccess.Of(existing).Shown(),
                    ["source"] = mirror.SourceAccess.Shown(), ["after"] = mirror.CopyAccess.Shown(), ["raise"] = raise
                };
                if (raise) raising.Add(new(file.Key, mirror.SourceAccess, mirror.CopyAccess));
                if (existing is not null)
                {
                    target["replacing"] = new JsonObject { ["createdAt"] = existing["createdAt"]?.DeepClone(), ["enabled"] = existing["enabled"]?.DeepClone() };
                    ClaudeScheduledRegistry.Drop(root, entry => ClaudeScheduledRegistry.Text(entry, "id") == request.Task);
                }
                ClaudeScheduledRegistry.Tasks(root).Add(mirror.Copy(now));
            }
            else
            {
                var fields = ClaudeScheduledRegistry.Differences(existing, mirror.Settings);
                action = file.Key == sourceKey ? "source" : fields.Length > 0 ? "fix" : "same";
                var before = ClaudeScheduledAccess.Of(existing);
                if (fields.Length > 0)
                {
                    target["fields"] = new JsonArray(fields.Select(name => (JsonNode)new JsonObject
                    {
                        ["name"] = name, ["before"] = Shown(ClaudeScheduledRegistry.Setting(existing, name), name),
                        ["after"] = Shown(mirror.Settings[name], name)
                    }).ToArray());
                    var after = before with { Mode = mirror.SourceAccess.Mode, Carried = mirror.SourceAccess.Carried };
                    var switchedOn = existing["enabled"] is JsonValue was && was.GetValueKind() == JsonValueKind.False
                                     && mirror.Settings["enabled"] is JsonValue on && on.GetValueKind() == JsonValueKind.True;
                    // The app's update tool does not switch a switched-off import back on (a person does, in the app).
                    raise = ClaudeScheduledPermission.Raises(before, after) || switchedOn && before.Imported;
                    target["permission"] = new JsonObject { ["before"] = before.Shown(), ["after"] = after.Shown(), ["raise"] = raise };
                    if (switchedOn && before.Imported) target["permission"]!["switchesOnImport"] = true;
                    if (raise) raising.Add(new(file.Key, before, after));
                    var entry = ClaudeScheduledRegistry.Find(root, request.Task)!;
                    floor = switchedOn || fields.Contains("cronExpression");
                    ClaudeScheduledRegistry.SetSettings(entry, mirror.Settings);
                    // As the app's own edit does: the slots before the change are not run.
                    if (floor && !(ClaudeScheduledRegistry.Time(ClaudeScheduledRegistry.Text(entry, "missedRunScanFloor")) is { } kept && kept >= now))
                        entry["missedRunScanFloor"] = ClaudeScheduledRegistry.Iso(now);
                }
                else target["permission"] = new JsonObject { ["now"] = before.Shown() };
                // A retry or skip the app keeps of this task here runs or counts on its own.
                if (ClaudeScheduledRegistry.TaskMaps.Where(name => file.Root![name] is JsonObject map && map.ContainsKey(request.Task)).ToArray() is { Length: > 0 } maps)
                    target["pending"] = Names(maps);
            }
            target["action"] = action;
            if (removals.Count > 0) target["removes"] = removals;
            if (floor) target["floor"] = "raised to the time of the apply";
            var changed = action is "insert" or "replace" or "fix" || removals.Count > 0;
            var afterEntry = ClaudeScheduledRegistry.Find(root, request.Task)?.DeepClone().AsObject();
            afterEntry?.Remove("missedRunScanFloor");
            tokens.Add(new JsonObject
            {
                ["key"] = file.Key, ["action"] = action, ["before"] = existing is null ? "-" : ClaudeScheduledRegistry.Hash(existing),
                ["after"] = changed && afterEntry is not null ? ClaudeScheduledRegistry.Hash(afterEntry) : "-",
                ["removes"] = removals.DeepClone(), ["permission"] = target["permission"]?.DeepClone(), ["floor"] = floor
            });
            if (!changed) continue;
            if (ClaudeScheduledRegistry.Serialize(root).LongLength > ClaudeScheduledRegistry.MaxBytes)
            {
                target["action"] = "held";
                target["reason"] = "too_large";
                tokens[^1]!["action"] = "held";
                continue;
            }
            changes.Add(new(file, root, target));
        }
        answer["targets"] = targets;
        answer["raises"] = raising.Count;
        var token = TokenOf(new JsonObject
        {
            ["op"] = "mirror", ["task"] = request.Task, ["source"] = sourceKey, ["createdAt"] = JsonNode.Parse(created),
            ["settings"] = mirror.Settings.DeepClone(), ["imported"] = mirror.SourceImported,
            ["replaces"] = answer["replaces"]!.DeepClone(), ["targets"] = tokens
        });
        answer["token"] = token;
        answer["next"] = raising.Count > 0
            ? $"some accounts would run it with more permission: to allow that, run again with --apply --expect {token} --allow-raise"
            : $"to write this, run again with --apply --expect {token}";
        return new(answer, token, changes, mirror, raising);
    }

    /// <summary>Which list <c>--from</c> names: <c>loaded</c> (the app's, when its log tells), an <c>account:org</c>
    /// key, or a label that names exactly one key.</summary>
    private static (string? Key, string? Problem) From(string? from, ClaudeScheduledScene scene)
    {
        if (string.IsNullOrWhiteSpace(from)) return (null, "from_missing");
        if (from.Equals("loaded", StringComparison.OrdinalIgnoreCase))
            return scene.Loaded is { } loaded ? (loaded, null) : (null, scene.Running ? "loaded_unknown" : "app_closed");
        var parts = from.Split(':');
        if (parts.Length == 2 && Guid.TryParseExact(parts[0], "D", out _) && Guid.TryParseExact(parts[1], "D", out _))
            return ((parts[0] + "/" + parts[1]).ToLowerInvariant(), null);
        var keys = scene.Registered.Where(account => string.Equals(account.Label, from, StringComparison.OrdinalIgnoreCase))
            .Select(account => account.Key).Distinct(StringComparer.Ordinal).ToArray();
        return keys.Length == 1 ? (keys[0], null) : (null, keys.Length == 0 ? "from_unknown" : "from_ambiguous");
    }

    /// <summary>The plan for removing a task that is not mirrored from every list holding it (each entry as the plan
    /// shows it: its <c>createdAt</c> and the entry itself).</summary>
    internal ClaudeScheduledPlan PlanRemove(ClaudeScheduledRequest request, ClaudeScheduledScene scene)
    {
        var answer = new JsonObject { ["status"] = "plan", ["task"] = request.Task };
        if (!ClaudeScheduledRegistry.IsSafeId(request.Task)) { answer["status"] = "refused"; answer["reason"] = "invalid_task"; return new(answer, null, []); }
        if (scene.State is not { } state) { answer["status"] = "refused"; answer["reason"] = scene.StateProblem ?? "store_unreadable"; return new(answer, null, []); }
        if (state.Mirrors.ContainsKey(request.Task) || state.Frozen.ContainsKey(request.Task))
        {
            answer["status"] = "refused"; answer["reason"] = "mirrored";
            answer["next"] = "stop mirroring it first: --claude-scheduled-unmirror " + request.Task;
            return new(answer, null, []);
        }
        var targets = new JsonArray();
        var tokens = new JsonArray();
        var changes = new List<ClaudeScheduledChange>();
        foreach (var file in scene.Files)
        {
            if (file.Status == ClaudeScheduledReadStatus.Unreadable)
            {
                var unread = Labelled(scene, file.Key);
                unread["action"] = "unreadable";
                unread["note"] = "not read (" + file.Problem + "): it may still hold the task";
                targets.Add(unread);
                tokens.Add(new JsonObject { ["key"] = file.Key, ["action"] = "unreadable" });
                continue;
            }
            if (file.Entries.GetValueOrDefault(request.Task) is not { } entry) continue;
            var target = Labelled(scene, file.Key);
            target["action"] = "remove";
            target["createdAt"] = entry["createdAt"]?.DeepClone();
            target["enabled"] = entry["enabled"]?.DeepClone();
            target["permission"] = ClaudeScheduledAccess.Of(entry).Shown();
            if (scene.Loaded is { } shown && (file.Key == shown || SameFolder(file.Key, shown))) target["loaded"] = true;
            targets.Add(target);
            tokens.Add(new JsonObject { ["key"] = file.Key, ["action"] = "remove", ["before"] = ClaudeScheduledRegistry.Hash(entry) });
            var root = file.Root!.DeepClone().AsObject();
            var created = ClaudeScheduledRegistry.Created(entry)!;
            ClaudeScheduledRegistry.Drop(root, other => ClaudeScheduledRegistry.Text(other, "id") == request.Task
                && ClaudeScheduledRegistry.SameCreated(other, created));
            changes.Add(new(file, root, target));
        }
        answer["targets"] = targets;
        var token = TokenOf(new JsonObject { ["op"] = "remove", ["task"] = request.Task, ["targets"] = tokens });
        answer["token"] = token;
        answer["next"] = changes.Count == 0 ? "no list holds it" : $"to remove it, run again with --apply --expect {token}";
        return new(answer, token, changes);
    }

    /// <summary>
    /// Performs a command: a plan (read only, whatever the app's version), or, when <paramref name="reviewed"/>, the apply
    /// of a plan whose token matches the plan made now: a mirror is kept first (so a stop midway leaves it chosen), then
    /// each list it changes is written while <paramref name="mayWrite"/> allows it; a list changed since it was read
    /// stops the writes there. A removal removes the entries the plan showed. Stopping a mirror only forgets it.
    /// </summary>
    internal JsonObject Serve(ClaudeScheduledRequest request, ClaudeScheduledScene scene, Func<string, bool> mayWrite,
        DateTimeOffset now, bool reviewed)
    {
        switch (request.Op)
        {
            case "unmirror":
            {
                if (scene.State is not { } state) return new() { ["status"] = scene.StateProblem ?? "store_unreadable" };
                if (!state.Mirrors.Remove(request.Task) & !state.Frozen.Remove(request.Task))
                    return new() { ["status"] = "not_mirrored", ["task"] = request.Task };
                return SaveState(state) ? new() { ["status"] = "ok", ["task"] = request.Task, ["next"] = "the lists keep the task: --claude-scheduled-remove "
                    + request.Task + " removes it" } : new() { ["status"] = "store_unwritable" };
            }
            case "mirror" or "remove":
            {
                var plan = request.Op == "mirror" ? PlanMirror(request, scene, now) : PlanRemove(request, scene);
                var answer = plan.Answer;
                answer["reviewed"] = reviewed;
                if (!request.Apply || plan.Refused) return answer;
                if (!reviewed) { answer["status"] = "unreviewed_app_version"; return answer; }
                if (!string.Equals(request.Expect, plan.Token, StringComparison.OrdinalIgnoreCase))
                {
                    answer["status"] = "token_mismatch";
                    answer["next"] = "the lists changed since that plan: this is the plan now; " + answer["next"]!.GetValue<string>();
                    return answer;
                }
                if (plan.Raising is { Count: > 0 } && !request.AllowRaise)
                {
                    answer["status"] = "raise_needs_allow";
                    return answer;
                }
                if (plan.Mirror is { } chosen)
                {
                    var state = scene.State!;
                    state.Frozen.Remove(chosen.Id);
                    state.Mirrors[chosen.Id] = chosen with { Allowed = request.AllowRaise ? plan.Raising ?? [] : [], ChosenAt = now };
                    if (!SaveState(state)) { answer["status"] = "store_unwritable"; return answer; }
                }
                var (written, waiting, failure) = WriteChanges(plan.Changes.Select(change => (change.File, change.Root)).ToArray(), mayWrite, now);
                foreach (var change in plan.Changes)
                    change.Target["result"] = written.Contains(change.File.Key) ? "written" : waiting.Contains(change.File.Key) ? "not written now" : "not written";
                answer["status"] = request.Op == "mirror" ? "mirrored" : "removed";
                if (failure is not null) answer["failure"] = failure;
                var left = plan.Changes.Where(change => !written.Contains(change.File.Key)).Select(change => scene.Label(change.File.Key));
                if (request.Op == "remove")
                    answer["remains"] = Names(left.Concat(scene.Files.Where(file => file.Status == ClaudeScheduledReadStatus.Unreadable)
                        .Select(file => scene.Label(file.Key))).Distinct());
                if (left.Any())
                    answer["next"] = request.Op == "mirror"
                        ? "a list was not written (the app shows it, or it changed): a missing task is made there later; an entry to fix or replace needs the same mirror once the app has left it"
                        : "a list was not written (the app shows it, or it changed): the task stays there; run the same removal once the app has left it";
                else answer.Remove("next");
                return answer;
            }
            default: return new() { ["status"] = "invalid_request" };
        }
    }

    /// <summary>
    /// One pass between commands, for each mirror: a lasting hold is kept and reported; else, when an account's entry
    /// differs (in its settings, its <c>createdAt</c> or its kind) or the definition went, the mirror is held from now
    /// on. Otherwise a list lacking the task gets it (with the tasks it replaces removed), unless the list still keeps
    /// runs of a task under its id, the copy would run with more permission than the source and that was not allowed,
    /// or the list has no file yet; and a copy whose scan floor is below the newest slot another account ran gets it
    /// raised just past that slot. With <paramref name="preview"/> nothing is written and holds are only reported.
    /// </summary>
    internal ClaudeScheduledPass Pass(ClaudeScheduledScene scene, Func<string, bool> mayWrite, DateTimeOffset now, bool preview = false)
    {
        var unreadable = scene.Files.Where(file => !file.Readable).Select(file => scene.Label(file.Key)).ToArray();
        var processed = scene.Files.ToDictionary(file => file.Key, file => file.Stamp ?? "?", StringComparer.Ordinal);
        var report = new JsonArray();
        if (scene.State is not { } state) return new(0, [], unreadable, scene.StateProblem ?? "store_unreadable", null, processed, report);
        var notices = new List<(int Rank, ClaudeScheduledProblemKind Kind, string Detail)>();
        void Notice(ClaudeScheduledProblemKind kind, string detail) => notices.Add((kind switch
        {
            ClaudeScheduledProblemKind.Differs => 0, ClaudeScheduledProblemKind.Stopped => 1, ClaudeScheduledProblemKind.Held => 2,
            ClaudeScheduledProblemKind.TooLarge => 3, _ => 4
        }, kind, detail));
        var planned = new SortedDictionary<string, (ClaudeScheduledFile File, JsonObject Root)>(StringComparer.Ordinal);
        var holds = false;
        foreach (var id in state.Frozen.Keys)
        {
            report.Add(new JsonObject { ["task"] = id, ["status"] = "frozen", ["note"] = "its record does not verify: mirror it again" });
            Notice(ClaudeScheduledProblemKind.Stopped, id + ": record");
        }
        foreach (var mirror in state.Mirrors.Values.ToArray())
        {
            var outcome = new JsonObject { ["task"] = mirror.Id, ["from"] = Labelled(scene, mirror.SourceKey) };
            report.Add(outcome);
            var (lost, wait) = mirror.Hold is null ? DefinitionStill(mirror, now) : (null, null);
            var hold = mirror.Hold ?? lost ?? Differing(mirror, scene, now);
            if (hold is not null)
            {
                if (mirror.Hold is null)
                {
                    state.Mirrors[mirror.Id] = mirror with { Hold = hold };
                    holds = true;
                }
                outcome["status"] = "held";
                outcome["hold"] = HoldJson(hold, scene);
                outcome["next"] = "mirror it again to choose the entry to keep";
                var where = hold.Accounts.Count == 0 ? "definition"
                    : string.Join(", ", hold.Accounts.Select(account => scene.Label(account.Key) + " " + string.Join("/", account.Fields)));
                Notice(hold.Reason == "differs" ? ClaudeScheduledProblemKind.Differs : ClaudeScheduledProblemKind.Stopped, mirror.Id + ": " + where);
                continue;
            }
            if (wait is not null)
            {
                outcome["status"] = "waiting";
                outcome["reason"] = wait;
                Notice(ClaudeScheduledProblemKind.Stopped, mirror.Id + ": " + wait);
                continue;
            }
            outcome["status"] = "ok";
            var accounts = new JsonArray();
            outcome["accounts"] = accounts;
            var slot = Ran(mirror, scene, now);
            if (slot is { } ran) outcome["newestSlotRun"] = ClaudeScheduledRegistry.Iso(ran);
            var raises = ClaudeScheduledPermission.Raises(mirror.SourceAccess, mirror.CopyAccess);
            foreach (var file in scene.Files)
            {
                JsonObject Account(string action, string? note = null)
                {
                    var item = Labelled(scene, file.Key);
                    item["action"] = action;
                    if (note is not null) item["note"] = note;
                    accounts.Add(item);
                    return item;
                }
                if (file.Status == ClaudeScheduledReadStatus.Missing)
                {
                    Account("no_list", "open this account in the app once: the task is made in its list then");
                    Notice(ClaudeScheduledProblemKind.Unfilled, mirror.Id + ": " + scene.Label(file.Key));
                    continue;
                }
                if (file.Status != ClaudeScheduledReadStatus.Ok) { Account("unreadable", file.Problem); continue; }
                (ClaudeScheduledFile, JsonObject) Root() => planned.TryGetValue(file.Key, out var kept) ? kept : (file, file.Root!.DeepClone().AsObject());
                if (file.Entries.GetValueOrDefault(mirror.Id) is not { } entry)
                {
                    if (ClaudeScheduledRegistry.HasTaskMap(file.Root!, mirror.Id))
                    {
                        Account("held", "the list still keeps runs of a task under this id");
                        Notice(ClaudeScheduledProblemKind.Unfilled, mirror.Id + ": " + scene.Label(file.Key));
                        continue;
                    }
                    if (raises && !mirror.Allows(file.Key, mirror.SourceAccess, mirror.CopyAccess))
                    {
                        Account("held", "a copy here would run with more permission than the source: mirror again with --allow-raise");
                        Notice(ClaudeScheduledProblemKind.Held, mirror.Id);
                        continue;
                    }
                    var (_, root) = Root();
                    ClaudeScheduledRegistry.Drop(root, other => mirror.Replaces.Any(replaced => ClaudeScheduledRegistry.Text(other, "id") == replaced.Id
                        && ClaudeScheduledRegistry.SameCreated(other, replaced.CreatedAt)));
                    ClaudeScheduledRegistry.Tasks(root).Add(mirror.Copy(now));
                    planned[file.Key] = (file, root);
                    Account("make");
                    continue;
                }
                // The scan floor: just past the newest slot an account ran (the app scans from the floor on, inclusive),
                // unless this one ran it; only ever raised.
                var own = ClaudeScheduledRegistry.Time(ClaudeScheduledRegistry.Text(entry, "lastScheduledFor"));
                var floor = ClaudeScheduledRegistry.Time(ClaudeScheduledRegistry.Text(entry, "missedRunScanFloor"));
                if (slot is { } newest && (own is null || own < newest) && (floor is null || floor < newest.AddMilliseconds(1)))
                {
                    var (_, root) = Root();
                    ClaudeScheduledRegistry.Find(root, mirror.Id)!["missedRunScanFloor"] = ClaudeScheduledRegistry.Iso(newest.AddMilliseconds(1));
                    planned[file.Key] = (file, root);
                    Account("floor", ClaudeScheduledRegistry.Iso(newest.AddMilliseconds(1)));
                    continue;
                }
                Account("same");
            }
        }
        var waiting = new List<string>();
        var written = 0;
        string? failure = null;
        var writes = new List<(ClaudeScheduledFile File, JsonObject Root)>();
        foreach (var (key, (file, root)) in planned)
        {
            if (ClaudeScheduledRegistry.Serialize(root).LongLength > ClaudeScheduledRegistry.MaxBytes)
                Notice(ClaudeScheduledProblemKind.TooLarge, scene.Label(key));
            else writes.Add((file, root));
        }
        if (!preview && writes.Count > 0)
        {
            var (done, later, failed) = WriteChanges(writes, mayWrite, now);
            written = done.Count;
            waiting.AddRange(later.Select(scene.Label));
            failure = failed;
            foreach (var key in done) processed[key] = ClaudeScheduledRegistry.CheapStamp(writes.First(write => write.File.Key == key).File.Path);
        }
        else waiting.AddRange(writes.Select(write => scene.Label(write.File.Key)));
        if (holds && !preview && !SaveState(state)) failure ??= "store_unwritable";
        ClaudeScheduledProblem? notice = null;
        if (notices.Count > 0)
        {
            var top = notices.Min(item => item.Rank);
            var details = notices.Where(item => item.Rank == top).Select(item => item.Detail).Distinct().ToArray();
            notice = new(notices.First(item => item.Rank == top).Kind,
                string.Join(", ", details.Take(3)) + (details.Length > 3 ? $" +{details.Length - 3}" : ""));
        }
        return new(written, waiting.Distinct().ToArray(), unreadable, failure, notice, processed, report);
    }

    /// <summary>Writes each list (its new whole content), under the lease, while <paramref name="mayWrite"/> allows it;
    /// a list found changed since it was read stops the writes. The keys written and not written now, and a failure.</summary>
    private (HashSet<string> Written, HashSet<string> Waiting, string? Failure) WriteChanges(
        IReadOnlyList<(ClaudeScheduledFile File, JsonObject Root)> changes, Func<string, bool> mayWrite, DateTimeOffset now)
    {
        var (written, waiting) = (new HashSet<string>(StringComparer.Ordinal), new HashSet<string>(StringComparer.Ordinal));
        string? failure = null;
        if (changes.Count == 0) return (written, waiting, null);
        IDisposable? lease = null;
        try
        {
            lease = _lease();
            var stopped = false;
            foreach (var (file, root) in changes)
            {
                if (stopped || !mayWrite(file.Key)) { waiting.Add(file.Key); continue; }
                try
                {
                    switch (Replace(file, ClaudeScheduledRegistry.Serialize(root), () => mayWrite(file.Key), now))
                    {
                        case true: written.Add(file.Key); break;
                        case false: waiting.Add(file.Key); break;
                        case null: waiting.Add(file.Key); stopped = true; break;
                    }
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
                {
                    failure = exception.Message;
                    waiting.Add(file.Key);
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
                                              or TimeoutException)
        {
            // The installer or an account operation holds the lease: every list waits for the next pass.
            failure = exception.Message;
            foreach (var (file, _) in changes) if (!written.Contains(file.Key)) waiting.Add(file.Key);
        }
        finally { lease?.Dispose(); }
        return (written, waiting, failure);
    }

    /// <summary>Replaces one list with <paramref name="bytes"/>: the old one is backed up, the new one written beside it
    /// and moved into place only while the list still holds exactly what was read (read again just before) and
    /// <paramref name="mayWrite"/> still allows it. True when written; false when it may not be written now; null when
    /// the list changed since it was read. A list that was not there is never made.</summary>
    private bool? Replace(ClaudeScheduledFile file, byte[] bytes, Func<bool> mayWrite, DateTimeOffset now)
    {
        Writing();
        // What is written must read as the app reads it, or the app would load none of that account's tasks.
        if (bytes.LongLength > ClaudeScheduledRegistry.MaxBytes || !ClaudeScheduledRegistry.IsValid(JsonNode.Parse(bytes)))
            throw new IOException("The list to write does not pass the app's schema.");
        if (file.Bytes is not { } previous) return false;
        var path = file.Path;
        var folder = Path.GetDirectoryName(path)!;
        if (!Same(path, previous)) return null;
        // A partial a stopped widget left behind.
        foreach (var stale in Directory.GetFiles(folder, ".scheduled-tasks.*.widget-*.partial"))
            try { if (File.GetLastWriteTimeUtc(stale) < now.UtcDateTime.AddHours(-1)) File.Delete(stale); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        KeepBackup(file.Key, previous, now);
        // Not a session record, tombstone or temporary record name the app's recovery looks at.
        var partial = Path.Combine(folder, ".scheduled-tasks." + Guid.NewGuid().ToString("N") + ".widget-mirror.partial");
        try
        {
            Commit(partial, bytes);
            if (!mayWrite()) return false;
            BeforeMoveForTest?.Invoke(path);
            if (!Same(path, previous)) return null;
            File.Move(partial, path, overwrite: true);
            return true;
        }
        finally { if (File.Exists(partial)) TryDelete(partial); }
    }

    private static bool Same(string path, byte[] expected)
    {
        try { return File.Exists(path) && File.ReadAllBytes(path).AsSpan().SequenceEqual(expected); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    private void KeepBackup(string key, byte[] bytes, DateTimeOffset now)
    {
        Writing();
        EnsureStore();
        var folder = Directory.CreateDirectory(BackupFolder(key)).FullName;
        var name = now.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture) + "_" + Guid.NewGuid().ToString("N")[..6] + ".json";
        Commit(Path.Combine(folder, name), bytes);
        foreach (var old in Directory.GetFiles(folder, "*.json").Order(StringComparer.Ordinal).SkipLast(Backups))
            TryDelete(old);
    }
}

/// <summary>Where the scheduled-task commands ask the running widget (<see cref="Version"/> 2: the mirror): a request
/// and an answer event and the shared memory carrying both, named after the widget's ledger folder (a test's folder
/// names its own). Named kernel objects reach the widget from a process the app runs, where files it writes could be
/// virtualized. A request on the names of the first version is answered with a refusal.</summary>
internal sealed class ClaudeScheduledChannel : IDisposable
{
    internal const int Version = 2;
    private const long MailboxBytes = 1024 * 1024;
    private const int AnswerOffset = 65536;
    private const int MaxReplaces = 64;
    private readonly Box _box;
    private readonly Box? _old;

    private sealed class Box(EventWaitHandle requested, EventWaitHandle answered, MemoryMappedFile mailbox) : IDisposable
    {
        internal EventWaitHandle Requested { get; } = requested;
        internal EventWaitHandle Answered { get; } = answered;
        internal MemoryMappedViewAccessor View { get; } = mailbox.CreateViewAccessor(0, MailboxBytes);

        internal static Box? Open((string Requested, string Answered, string Mailbox, string Lock) names)
        {
            EventWaitHandle? requested = null, answered = null;
            MemoryMappedFile? mailbox = null;
            try
            {
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

        /// <summary>A request waiting: its id and text.</summary>
        internal (string Id, JsonObject Request)? Take()
        {
            if (!Requested.WaitOne(0)) return null;
            try
            {
                return ClaudeSessionSync.ReadSlot(View, 0, AnswerOffset) is { } text && JsonNode.Parse(text) is JsonObject request
                       && request["id"] is JsonValue value && value.TryGetValue<string>(out var id) && id.Length is > 0 and <= 64
                    ? (id, request) : null;
            }
            catch (JsonException) { return null; }
        }

        internal void Answer(string id, string answer)
        {
            ClaudeSessionSync.WriteSlot(View, AnswerOffset, MailboxBytes - AnswerOffset, Encoding.UTF8.GetBytes(id + "\n" + answer));
            Answered.Set();
        }

        public void Dispose() { View.Dispose(); mailbox.Dispose(); Requested.Dispose(); Answered.Dispose(); }
    }

    private ClaudeScheduledChannel(Box box, Box? old) { _box = box; _old = old; }

    private static string Suffix(string ledgerDirectory) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(ledgerDirectory)).ToLowerInvariant())))[..16];

    private static (string Requested, string Answered, string Mailbox, string Lock) Names(string ledgerDirectory)
    {
        var prefix = @"Local\CodexWeeklyUsageIndicator.ScheduledMirror" + Version.ToString(CultureInfo.InvariantCulture) + ".";
        var suffix = Suffix(ledgerDirectory);
        return (prefix + "Request." + suffix, prefix + "Answer." + suffix, prefix + "Mailbox." + suffix, prefix + "Lock." + suffix);
    }

    // The first version's names (seed and remove), answered with a refusal only.
    private static (string, string, string, string) OldNames(string ledgerDirectory)
    {
        var suffix = Suffix(ledgerDirectory);
        return (@"Local\CodexWeeklyUsageIndicator.ScheduledSeed." + suffix, @"Local\CodexWeeklyUsageIndicator.ScheduledSeeded." + suffix,
            @"Local\CodexWeeklyUsageIndicator.ScheduledSeedMailbox." + suffix, @"Local\CodexWeeklyUsageIndicator.ScheduledSeedLock." + suffix);
    }

    internal static ClaudeScheduledChannel? Open(string ledgerDirectory) =>
        Box.Open(Names(ledgerDirectory)) is { } box ? new(box, Box.Open(OldNames(ledgerDirectory))) : null;

    /// <summary>A request waiting, if any: its id and what it asks (null when it does not read as this version's).</summary>
    internal (string Id, ClaudeScheduledRequest? Request)? Request()
    {
        if (_old?.Take() is { } old)
            _old.Answer(old.Id, new JsonObject { ["status"] = "refused", ["reason"] = "old_command_version" }.ToJsonString());
        return _box.Take() is { } taken ? (taken.Id, Parse(taken.Request)) : null;
    }

    private static ClaudeScheduledRequest? Parse(JsonObject request)
    {
        static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
        static bool Flag(JsonNode? node) => node is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
        if (request["v"] is not JsonValue version || !version.TryGetValue<int>(out var number) || number != Version) return null;
        if (Text(request["op"]) is not ("mirror" or "unmirror" or "remove") || Text(request["task"]) is not { } task) return null;
        var replaces = request["replaces"] is JsonArray items ? items.Select(Text).ToArray() : [];
        if (replaces.Length > MaxReplaces || replaces.Any(id => id is null)) return null;
        return new(Text(request["op"])!, task, Text(request["from"]), Flag(request["apply"]), Text(request["expect"]),
            Flag(request["allowRaise"]), replaces.Select(id => id!).ToArray());
    }

    internal void Answer(string id, string answer) => _box.Answer(id, answer);

    /// <summary>Asks the running widget and waits for its answer; null without a widget of this version, or without an
    /// answer in time.</summary>
    internal static string? Ask(string ledgerDirectory, TimeSpan wait, ClaudeScheduledRequest request)
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
                        ClaudeSessionSync.WriteSlot(view, 0, AnswerOffset, JsonSerializer.SerializeToUtf8Bytes(new JsonObject
                        {
                            ["id"] = id, ["v"] = Version, ["op"] = request.Op, ["task"] = request.Task, ["from"] = request.From,
                            ["apply"] = request.Apply, ["expect"] = request.Expect, ["allowRaise"] = request.AllowRaise,
                            ["replaces"] = new JsonArray((request.Replaces ?? []).Select(task => (JsonNode)task).ToArray())
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

    public void Dispose() { _box.Dispose(); _old?.Dispose(); }
}

/// <summary>The scheduled-task commands. A mirror or a removal shows its plan; with <c>--apply --expect token</c> the
/// running widget writes that plan. Without a widget of this version running, a plan is made here, reading only.</summary>
internal static class ClaudeScheduledCommand
{
    internal const string PlanFlag = "--claude-scheduled-plan";
    internal const string MirrorFlag = "--claude-scheduled-mirror";
    internal const string UnmirrorFlag = "--claude-scheduled-unmirror";
    internal const string RemoveFlag = "--claude-scheduled-remove";
    private static readonly JsonSerializerOptions Output = new()
    { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>The request the arguments after a flag ask for; null when they do not read.</summary>
    internal static ClaudeScheduledRequest? Parse(string op, string[] args)
    {
        if (args.Length == 0 || args[0].StartsWith("--", StringComparison.Ordinal)) return null;
        var task = args[0];
        string? from = null, expect = null;
        bool apply = false, allowRaise = false;
        var replaces = new List<string>();
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            if (op == "mirror" && argument == "--from" && index + 1 < args.Length) from = args[++index];
            else if (op != "unmirror" && argument == "--apply") apply = true;
            else if (op != "unmirror" && argument == "--expect" && index + 1 < args.Length) expect = args[++index];
            else if (op == "mirror" && argument == "--allow-raise") allowRaise = true;
            else if (op == "mirror" && argument == "--replaces")
            {
                while (index + 1 < args.Length && !args[index + 1].StartsWith("--", StringComparison.Ordinal)) replaces.Add(args[++index]);
            }
            else return null;
        }
        if (apply && expect is null || op == "mirror" && from is null) return null;
        return new(op, task, from, apply, expect, allowRaise, replaces);
    }

    internal static int Run(string op, string[] args)
    {
        if (Parse(op, args) is not { } request)
            return Print(new JsonObject
            {
                ["status"] = "usage",
                ["usage"] = op switch
                {
                    "mirror" => MirrorFlag + " <id> --from <account:org | label | loaded> [--replaces <id> ...] [--apply --expect <token>] [--allow-raise]",
                    "remove" => RemoveFlag + " <id> [--apply --expect <token>]",
                    _ => UnmirrorFlag + " <id>"
                }
            }, 2);
        var paths = ClaudeShelfPaths.CreateDefault();
        if (ClaudeScheduledChannel.Ask(paths.LedgerDirectory, TimeSpan.FromSeconds(60), request) is { } answer)
        {
            Write(answer);
            return Exit(answer);
        }
        // Writing is the widget's: without it only a plan is made, here.
        if (request.Apply || request.Op == "unmirror") return Print(new JsonObject { ["status"] = "widget_not_answering" }, 1);
        var plan = Local(paths, scene => (request.Op == "mirror" ? Sync(paths).PlanMirror(request, scene, DateTimeOffset.Now)
            : Sync(paths).PlanRemove(request, scene)).Answer);
        return Print(plan, plan["status"]?.GetValue<string>() == "plan" ? 0 : 1);
    }

    /// <summary>What the next pass would do with each mirror, reading only.</summary>
    internal static int RunPlan()
    {
        var paths = ClaudeShelfPaths.CreateDefault();
        var answer = Local(paths, scene =>
        {
            var pass = Sync(paths).Pass(scene, _ => false, DateTimeOffset.Now, preview: true);
            return new JsonObject
            {
                ["status"] = pass.Failure ?? "ok", ["mirrors"] = pass.Report, ["notice"] = pass.Notice is { } notice ? notice.Kind + ": " + notice.Detail : null,
                ["unreadable"] = new JsonArray(pass.Unreadable.Select(name => (JsonNode)name).ToArray())
            };
        });
        return Print(answer, answer["status"]?.GetValue<string>() == "ok" ? 0 : 1);
    }

    private static ClaudeScheduledSync Sync(ClaudeShelfPaths paths) =>
        new(paths.SessionsRoot, Path.Combine(paths.LedgerDirectory, ClaudeScheduledSync.StoreFolder), paths.ScheduledTasksRoot,
            paths.AppScheduledTasksRoot, () => new MemoryStream(), readOnly: true);

    /// <summary>A plan made in this process: the lists, the accounts registered and the app's log as it is now. The
    /// answer says so, with how many times anything that writes was entered (none).</summary>
    private static JsonObject Local(ClaudeShelfPaths paths, Func<ClaudeScheduledScene, JsonObject> plan)
    {
        IReadOnlyList<ClaudeShelfAccount> accounts;
        try { accounts = ClaudeShelfAccounts.Resolve(new ClaudeProfileStore().ListProfiles(), paths.LedgerDirectory, keepCache: false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidOperationException
                                              or JsonException)
        { accounts = []; }
        var runtime = ClaudeDesktopProbe.Observe();
        var running = runtime.State == ClaudeDesktopRunState.Running;
        var log = new ClaudeDesktopLogWatcher();
        log.Update(running ? runtime.HostStartedAt : null, accounts);
        var scene = Sync(paths).Scene(accounts, running ? log.Scheduled : null, running);
        var answer = scene is null ? new JsonObject { ["status"] = "sessions_unlisted" } : plan(scene);
        // Whether the widget would write an apply of it now: the installed version reviewed, and the app running that one.
        var package = ClaudeDesktopProbe.FindPackage();
        answer["reviewed"] = package is not null && ClaudeDesktopProbe.ScheduledContract(package.AsarPath) == ClaudeDesktopContract.Reviewed
                             && ClaudeDesktopProbe.RunsFrom(runtime, package);
        answer["madeBy"] = "this command (no widget of this version answered): nothing was written";
        answer["writeCalls"] = ClaudeScheduledSync.WriteCalls;
        return answer;
    }

    /// <summary>The exit code for an answer: 0 when it is a plan or what was asked was done.</summary>
    internal static int Exit(string answer)
    {
        try
        {
            return JsonNode.Parse(answer)?["status"] is JsonValue status && status.TryGetValue<string>(out var text)
                   && text is "plan" or "ok" or "mirrored" or "removed" ? 0 : 1;
        }
        catch (JsonException) { return 1; }
    }

    private static int Print(JsonObject value, int code)
    {
        Write(value.ToJsonString(Output));
        return code;
    }

    /// <summary>UTF-8 to standard output as bytes: the widget is a window program with no console of its own, where
    /// setting the console's encoding fails.</summary>
    private static void Write(string text)
    {
        using var output = Console.OpenStandardOutput();
        output.Write(Encoding.UTF8.GetBytes(text));
        output.Write("\n"u8);
    }
}
