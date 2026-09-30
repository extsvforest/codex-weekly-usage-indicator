using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace WeeklyUsageIndicator;

/// <summary>A Claude Desktop account/organization pair whose Code session list ("shelf") the utility fills.</summary>
internal sealed record ClaudeShelfAccount(string AccountUuid, string OrgUuid, string Label, string ProfileId = "")
{
    internal string Key => AccountUuid.ToLowerInvariant() + "/" + OrgUuid.ToLowerInvariant();
}

internal sealed record ClaudeShelfPaths(string SessionsRoot, string ProjectsRoot, string ScratchRoot,
    IReadOnlyList<string> RegistryDirectories, string LedgerDirectory)
{
    internal static ClaudeShelfPaths CreateDefault(string? claudeConfigDirectory = null)
    {
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var config = claudeConfigDirectory ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        // Official CLIs register each running session under their own config directory. Utility profiles are
        // included: a CLI started with one of them is invisible to the app's own "in use" check.
        var registries = new List<string> { Path.Combine(config, "sessions") };
        var profiles = Path.Combine(local, "CodexWeeklyUsageIndicator.ClaudeProfiles", "profiles");
        if (Directory.Exists(profiles))
            registries.AddRange(Directory.EnumerateDirectories(profiles).Select(path => Path.Combine(path, "sessions")));
        return new(Path.Combine(roaming, "Claude", "claude-code-sessions"), Path.Combine(config, "projects"),
            Path.Combine(roaming, "Claude", "scratch-workspaces"), registries,
            Path.Combine(local, "CodexWeeklyUsageIndicator.SessionShelf"));
    }
}

internal sealed record ClaudeShelfAction(string Source, string SourceFile, string Target, string CliSessionId,
    JsonObject Record);

internal sealed class ClaudeShelfPlan
{
    // Skips that clear up on their own; a plan containing them is retried rather than reported as done.
    internal static readonly string[] TransientReasons = ["live_writer"];
    internal List<ClaudeShelfAction> Actions { get; } = [];
    internal SortedDictionary<string, SortedDictionary<string, int>> Skipped { get; } = new(StringComparer.Ordinal);
    internal SortedDictionary<string, string> Held { get; } = new(StringComparer.Ordinal);
    internal SortedDictionary<string, SortedDictionary<string, string>> Fingerprints { get; } = new(StringComparer.Ordinal);
    internal int PendingFor(string targetKey) => Actions.Count(action => action.Target == targetKey);
    internal bool HasTransientSkips => Skipped.Values.Any(reasons => TransientReasons.Any(reasons.ContainsKey));
    internal bool IsComplete => Held.Count == 0 && !HasTransientSkips;

    internal bool SameShelvesAs(ClaudeShelfPlan other) =>
        Fingerprints.Count == other.Fingerprints.Count && Fingerprints.All(pair =>
            other.Fingerprints.TryGetValue(pair.Key, out var files) && files.Count == pair.Value.Count
            && pair.Value.All(file => files.TryGetValue(file.Key, out var hash) && hash == file.Value));

    internal void Skip(string pair, string reason)
    {
        if (!Skipped.TryGetValue(pair, out var reasons)) Skipped[pair] = reasons = new(StringComparer.Ordinal);
        reasons[reason] = reasons.GetValueOrDefault(reason) + 1;
    }
}

internal sealed record ClaudeShelfResult(int Created, ClaudeShelfPlan Plan);

internal sealed class ClaudeDesktopRunningException(int created = 0)
    : InvalidOperationException(created == 0 ? "Claude 앱이 실행 중이라 세션 목록을 채우지 않았습니다."
        : "Claude 앱이 켜져 세션 목록 채우기를 중간에 멈췄습니다.")
{
    internal int Created { get; } = created;
}

/// <summary>
/// Adds minimal session records to other accounts' Claude Desktop shelves so every registered account lists this
/// PC's Code sessions. A new record points at the same shared transcript (cliSessionId); the app continues it with
/// the signed-in account. Invariants: write only while Desktop and its Code CLIs are fully closed; add only — never
/// modify or delete a record the app wrote; never read or touch authentication data.
/// </summary>
internal sealed class ClaudeSessionShelf
{
    private const long MaxRecordBytes = 10 * 1024 * 1024;
    private const string PartialSuffix = ".shelf-fill.partial";
    private static readonly string[] CopiedFields =
        ["title", "titleSource", "createdAt", "lastActivityAt", "indexedAt", "completedTurns"];
    // The app protects a transcript from deletion per id, so carry every id this session still claims.
    private static readonly string[] LineageFields = ["priorCliSessionIds", "rewindEdges", "transcriptCuts"];
    private static readonly string[] EffortLevels = ["low", "medium", "high", "xhigh", "max"];
    private static readonly JsonSerializerOptions RecordJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly SemaphoreSlim RunGate = new(1, 1);
    private readonly ClaudeShelfPaths _paths;
    private readonly Func<ClaudeLiveSessionScan> _liveWriters;
    private readonly Func<IDisposable> _acquireLease;
    private readonly TimeProvider _time;
    internal ClaudeShelfPaths Paths => _paths;

    internal ClaudeSessionShelf(ClaudeShelfPaths? paths = null, Func<ClaudeLiveSessionScan>? liveWriters = null,
        Func<IDisposable>? acquireLease = null, TimeProvider? time = null)
    {
        _paths = paths ?? ClaudeShelfPaths.CreateDefault();
        _liveWriters = liveWriters ?? (() => ClaudeLiveSessions.Read(_paths.RegistryDirectories));
        // Lifecycle scripts take the same lease before replacing the installed executable.
        _acquireLease = acquireLease ?? (() => AccountOperationLease.Acquire());
        _time = time ?? TimeProvider.System;
    }

    internal string ShelfDirectory(ClaudeShelfAccount account) =>
        Path.Combine(_paths.SessionsRoot, account.AccountUuid, account.OrgUuid);

    internal ClaudeShelfPlan Plan(IReadOnlyList<ClaudeShelfAccount> accounts) =>
        Plan(accounts, ClaudeShelfLedger.Load(_paths.LedgerDirectory, _paths.SessionsRoot));

    private ClaudeShelfPlan Plan(IReadOnlyList<ClaudeShelfAccount> accounts, ClaudeShelfLedger ledger)
    {
        var plan = new ClaudeShelfPlan();
        var distinct = accounts.DistinctBy(account => account.Key).ToArray();
        var shelves = distinct.ToDictionary(account => account.Key, account => ScanShelf(ShelfDirectory(account)));
        foreach (var (key, shelf) in shelves) plan.Fingerprints[key] = shelf.Fingerprint;
        if (distinct.Length < 2) return plan;
        var live = _liveWriters();
        // An unreadable registration could name any session: hold everything rather than guess.
        if (live.Unknown) { foreach (var account in distinct) plan.Held[account.Key] = "writer_unknown"; return plan; }
        var transcripts = TranscriptIds();
        foreach (var target in distinct)
        {
            var shelf = shelves[target.Key];
            if (shelf.Temporaries.Count > 0) { plan.Held[target.Key] = "recovering"; continue; }
            if (shelf.Records.Values.Any(record => record is null)) { plan.Held[target.Key] = "unreadable"; continue; }
            var present = shelf.Records.Values.Select(record => Text(record!, "cliSessionId"))
                .OfType<string>().Select(id => id.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            var deleted = shelf.Deleted.Select(DeletedId).ToHashSet(StringComparer.Ordinal);
            // A record this utility created and the user later removed there stays removed for that account.
            var removed = ledger.Created.Where(entry => entry.Target == target.Key
                    && !File.Exists(Path.Combine(ShelfDirectory(target), entry.File)))
                .Select(entry => entry.CliSessionId.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            var planned = new HashSet<string>(StringComparer.Ordinal);
            foreach (var source in distinct.Where(source => source.Key != target.Key))
            {
                var pair = source.Key[..8] + "->" + target.Key[..8];
                foreach (var (file, record) in shelves[source.Key].Records)
                {
                    if (record is null) { plan.Skip(pair, "unreadable"); continue; }
                    var reason = Ineligible(record, transcripts);
                    var cli = Text(record, "cliSessionId")?.ToLowerInvariant();
                    if (reason is null && live.Ids.Contains(cli!)) reason = "live_writer";
                    if (reason is null && (present.Contains(cli!) || planned.Contains(cli!))) reason = "already_present";
                    if (reason is null && (deleted.Contains(cli!) || removed.Contains(cli!))) reason = "deleted_in_target";
                    if (reason is not null) { plan.Skip(pair, reason); continue; }
                    planned.Add(cli!);
                    plan.Actions.Add(new ClaudeShelfAction(source.Key, file, target.Key, Text(record, "cliSessionId")!,
                        BuildRecord(record)));
                }
            }
        }
        return plan;
    }

    /// <summary>Publishes the plan made immediately before writing. <paramref name="desktopClosed"/> must return
    /// false when the app's state cannot be determined; it is checked again before every file.</summary>
    internal ClaudeShelfResult Apply(IReadOnlyList<ClaudeShelfAccount> accounts, Func<bool> desktopClosed,
        string? appVersion = null)
    {
        if (!RunGate.Wait(0)) throw new InvalidOperationException("세션 목록 채우기가 이미 진행 중입니다.");
        try
        {
            using var lease = _acquireLease();
            if (!desktopClosed()) throw new ClaudeDesktopRunningException();
            var ledger = ClaudeShelfLedger.Load(_paths.LedgerDirectory, _paths.SessionsRoot);
            var first = Plan(accounts, ledger);
            if (!desktopClosed()) throw new ClaudeDesktopRunningException();
            // Re-plan and publish that final plan: writers or eligibility can change without a shelf change.
            var plan = Plan(accounts, ledger);
            if (!first.SameShelvesAs(plan)) throw new IOException("세션 목록이 준비 중에 바뀌어 아무것도 쓰지 않았습니다.");
            var targets = accounts.DistinctBy(account => account.Key).ToDictionary(account => account.Key);
            var prepared = plan.Actions.Select(action =>
            {
                var bytes = JsonSerializer.SerializeToUtf8Bytes(action.Record, RecordJson);
                var name = action.Record["sessionId"]!.GetValue<string>() + ".json";
                return (Action: action, Name: name, Bytes: bytes, Entry: new ClaudeShelfLedger.Entry(action.Target, name,
                    Convert.ToHexString(SHA256.HashData(bytes)), action.CliSessionId, action.Source, action.SourceFile,
                    _time.GetUtcNow(), appVersion, Pending: true));
            }).ToArray();
            // Record intent first: after a crash the next load keeps only files whose content matches.
            ledger.Created.AddRange(prepared.Select(item => item.Entry));
            if (prepared.Length > 0) ledger.Save(_paths.LedgerDirectory);
            var created = 0;
            try
            {
                foreach (var item in prepared)
                {
                    if (!desktopClosed()) throw new ClaudeDesktopRunningException(created);
                    var directory = ShelfDirectory(targets[item.Action.Target]);
                    Directory.CreateDirectory(directory);
                    RemoveStalePartials(directory);
                    var destination = Path.Combine(directory, item.Name);
                    var partial = Path.Combine(directory, "." + item.Name + PartialSuffix);
                    try
                    {
                        using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                   4096, FileOptions.WriteThrough))
                        { stream.Write(item.Bytes); stream.Flush(flushToDisk: true); }
                        File.Move(partial, destination, overwrite: false);
                    }
                    finally { if (File.Exists(partial)) File.Delete(partial); }
                    ledger.Confirm(item.Entry);
                    created++;
                }
            }
            finally
            {
                ledger.SettlePending(_paths.SessionsRoot);
                ledger.AddRun(_time.GetUtcNow(), "apply", appVersion, created, plan);
                ledger.Save(_paths.LedgerDirectory);
            }
            return new ClaudeShelfResult(created, plan);
        }
        finally { RunGate.Release(); }
    }

    /// <summary>Removes only records this utility created that the app never changed. Everything else stays.</summary>
    internal (int Removed, int Kept) Undo(Func<bool> desktopClosed)
    {
        if (!RunGate.Wait(0)) throw new InvalidOperationException("세션 목록 채우기가 이미 진행 중입니다.");
        try
        {
            using var lease = _acquireLease();
            if (!desktopClosed()) throw new ClaudeDesktopRunningException();
            var ledger = ClaudeShelfLedger.Load(_paths.LedgerDirectory, _paths.SessionsRoot);
            int removed = 0, kept = 0;
            foreach (var entry in ledger.Created.ToArray())
            {
                var path = ClaudeShelfLedger.EntryPath(_paths.SessionsRoot, entry);
                if (path is not null && File.Exists(path) && Hash(path) == entry.Sha256)
                { File.Delete(path); ledger.Created.Remove(entry); removed++; }
                else kept++;
            }
            ledger.AddRun(_time.GetUtcNow(), "undo", null, removed, null);
            ledger.Save(_paths.LedgerDirectory);
            return (removed, kept);
        }
        finally { RunGate.Release(); }
    }

    private string? Ineligible(JsonObject record, IReadOnlySet<string> transcripts)
    {
        if (Bool(record, "isArchived")) return "archived";
        var cli = Text(record, "cliSessionId");
        if (cli is null || !Guid.TryParse(cli, out _)) return "no_current_id";
        if (record["sshConfig"] is not null || record["wslConfig"] is not null
            || record["cloudSessionId"] is not null || record["movedToCloud"] is not null) return "remote";
        if (record["worktreePath"] is not null || record["worktreeName"] is not null
            || record["worktreeLazy"] is not null) return "worktree";
        var cwd = Text(record, "cwd");
        if (string.IsNullOrWhiteSpace(cwd) || !Path.IsPathFullyQualified(cwd)) return "no_cwd";
        // The app deletes a folderless session's scratch folder without checking other accounts.
        if (IsWithin(cwd, _paths.ScratchRoot)) return "scratch";
        if (!Directory.Exists(cwd)) return "missing_cwd";
        return transcripts.Contains(cli.ToLowerInvariant()) ? null : "missing_transcript";
    }

    internal static JsonObject BuildRecord(JsonObject source)
    {
        var cwd = Text(source, "cwd")!;
        var record = new JsonObject
        {
            ["sessionId"] = "local_" + Guid.NewGuid().ToString("D"),
            ["cliSessionId"] = Text(source, "cliSessionId"),
            ["cwd"] = cwd,
            ["originCwd"] = Text(source, "originCwd") is { Length: > 0 } origin ? origin : cwd
        };
        foreach (var field in CopiedFields)
            if (source[field] is { } value) record[field] = value.DeepClone();
        // Carry an explicit model choice only. An inherited effort stays inherited; unknown values fall back to
        // the app default instead of failing the session.
        if (Text(source, "model") is { Length: > 0 and <= 80 } model && model.All(IsModelCharacter)) record["model"] = model;
        if (!Bool(source, "effortInherited") && Text(source, "effort") is { } effort && EffortLevels.Contains(effort))
            record["effort"] = effort;
        record["isArchived"] = false;
        if (Bool(source, "isStarred")) record["isStarred"] = true;
        foreach (var field in LineageFields)
            if (source[field] is { } value && value is not JsonArray { Count: 0 }) record[field] = value.DeepClone();
        // True means the transcript began outside the Code tab; the app then swaps its prompt append for a notice.
        if (Bool(source, "adoptedFromOtherSurface")) record["adoptedFromOtherSurface"] = true;
        return record;
    }

    private Shelf ScanShelf(string directory)
    {
        var shelf = new Shelf();
        if (!Directory.Exists(directory)) return shelf;
        foreach (var path in Directory.EnumerateFiles(directory).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (name.StartsWith("local_", StringComparison.Ordinal) && name.EndsWith(".json.tmp", StringComparison.Ordinal))
                shelf.Temporaries.Add(name);
            else if (name.StartsWith("deleted_", StringComparison.Ordinal)) shelf.Deleted.Add(name);
            else if (name.StartsWith("local_", StringComparison.Ordinal) && name.EndsWith(".json", StringComparison.Ordinal))
                shelf.Records[name] = ReadRecord(path);
            else continue;
            shelf.Fingerprint[name] = Hash(path);
        }
        return shelf;
    }

    private static JsonObject? ReadRecord(string path)
    {
        try
        {
            if (new FileInfo(path).Length is <= 0 or > MaxRecordBytes) return null;
            return JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        { return null; }
    }

    private IReadOnlySet<string> TranscriptIds()
    {
        var ids = new HashSet<string>(StringComparer.Ordinal);
        if (!Directory.Exists(_paths.ProjectsRoot)) return ids;
        foreach (var project in Directory.EnumerateDirectories(_paths.ProjectsRoot))
            foreach (var file in Directory.EnumerateFiles(project, "*.jsonl"))
                ids.Add(Path.GetFileNameWithoutExtension(file).ToLowerInvariant());
        return ids;
    }

    private static void RemoveStalePartials(string directory)
    {
        foreach (var path in Directory.EnumerateFiles(directory, ".local_*" + PartialSuffix))
            try { File.Delete(path); } catch (IOException) { }
    }

    private static string DeletedId(string name)
    {
        var id = name["deleted_".Length..];
        if (id.EndsWith(".json", StringComparison.Ordinal)) id = id[..^5];
        if (id.StartsWith("local_", StringComparison.Ordinal)) id = id["local_".Length..];
        return id.ToLowerInvariant();
    }

    internal static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
    private static bool IsModelCharacter(char character) => char.IsAsciiLetterOrDigit(character) || character is '-' or '.' or '_' or '[' or ']';
    private static string? Text(JsonObject record, string field) =>
        record[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
    private static bool Bool(JsonObject record, string field) =>
        record[field] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;
    private static bool IsWithin(string path, string parent)
    {
        var full = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(parent));
        return full.Equals(root, StringComparison.OrdinalIgnoreCase)
            || full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class Shelf
    {
        internal Dictionary<string, JsonObject?> Records { get; } = new(StringComparer.Ordinal);
        internal List<string> Temporaries { get; } = [];
        internal List<string> Deleted { get; } = [];
        internal SortedDictionary<string, string> Fingerprint { get; } = new(StringComparer.Ordinal);
    }
}

/// <summary>Tool-owned record of what it created. Compatible with the 2026-09-30 spike ledger. Creation history is
/// kept indefinitely: it is the evidence that a record the user removed must not come back.</summary>
internal sealed class ClaudeShelfLedger
{
    private const int MaxRuns = 50;
    internal sealed record Entry(string Target, string File, string Sha256, string CliSessionId, string Source,
        string SourceFile, DateTimeOffset CreatedAt, string? AppVersion, bool Pending = false);
    internal List<Entry> Created { get; } = [];
    private readonly List<JsonObject> _runs = [];

    internal static ClaudeShelfLedger Load(string directory, string sessionsRoot)
    {
        var ledger = new ClaudeShelfLedger();
        var path = Path.Combine(directory, "ledger.json");
        if (!File.Exists(path)) return ledger;
        var root = JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject
            ?? throw new InvalidDataException("세션 목록 장부를 읽을 수 없습니다.");
        foreach (var item in root["created"] as JsonArray ?? [])
        {
            if (item is not JsonObject entry) continue;
            string? Read(string name) => entry[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            if (Read("dst") is not { } target || Read("file") is not { } file || Read("sha256") is not { } hash
                || Read("cliSessionId") is not { } cli) continue;
            DateTimeOffset.TryParse(Read("createdAt"), out var created);
            var pending = entry["pending"] is JsonValue flag && flag.TryGetValue<bool>(out var value) && value;
            var loaded = new Entry(target.ToLowerInvariant(), file, hash.ToUpperInvariant(), cli, Read("src") ?? "",
                Read("srcFile") ?? "", created, Read("appVersion"));
            // An interrupted run: keep the intent only when the published file has exactly the intended content.
            if (pending && (EntryPath(sessionsRoot, loaded) is not { } published || !File.Exists(published)
                            || ClaudeSessionShelf.Hash(published) != loaded.Sha256)) continue;
            ledger.Created.Add(loaded);
        }
        foreach (var run in root["runs"] as JsonArray ?? [])
            if (run is JsonObject value) ledger._runs.Add((JsonObject)value.DeepClone());
        return ledger;
    }

    internal static string? EntryPath(string sessionsRoot, Entry entry)
    {
        var parts = entry.Target.Split('/');
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out _) || !Guid.TryParse(parts[1], out _)
            || entry.File.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !entry.File.StartsWith("local_", StringComparison.Ordinal)) return null;
        return Path.Combine(sessionsRoot, parts[0], parts[1], entry.File);
    }

    internal void Confirm(Entry entry)
    {
        var index = Created.IndexOf(entry);
        if (index >= 0) Created[index] = entry with { Pending = false };
    }

    /// <summary>Keeps an unconfirmed intent only when its file was published with exactly the intended content.</summary>
    internal void SettlePending(string sessionsRoot)
    {
        for (var index = Created.Count - 1; index >= 0; index--)
        {
            var entry = Created[index];
            if (!entry.Pending) continue;
            if (EntryPath(sessionsRoot, entry) is { } path && File.Exists(path) && ClaudeSessionShelf.Hash(path) == entry.Sha256)
                Created[index] = entry with { Pending = false };
            else Created.RemoveAt(index);
        }
    }

    internal void AddRun(DateTimeOffset at, string mode, string? appVersion, int count, ClaudeShelfPlan? plan)
    {
        var run = new JsonObject { ["at"] = at.ToString("O"), ["mode"] = mode, ["appVersion"] = appVersion, ["count"] = count };
        if (plan is not null)
        {
            var skipped = new JsonObject();
            foreach (var (pair, reasons) in plan.Skipped)
                skipped[pair] = new JsonObject(reasons.Select(reason => KeyValuePair.Create(reason.Key, (JsonNode?)reason.Value)));
            run["skipped"] = skipped;
            run["held"] = new JsonObject(plan.Held.Select(held => KeyValuePair.Create(held.Key, (JsonNode?)held.Value)));
        }
        _runs.Add(run);
        if (_runs.Count > MaxRuns) _runs.RemoveRange(0, _runs.Count - MaxRuns);
    }

    internal void Save(string directory)
    {
        Directory.CreateDirectory(directory);
        var root = new JsonObject
        {
            ["version"] = 1,
            ["created"] = new JsonArray(Created.Select(entry => (JsonNode?)new JsonObject
            {
                ["dst"] = entry.Target, ["file"] = entry.File, ["sha256"] = entry.Sha256,
                ["cliSessionId"] = entry.CliSessionId, ["src"] = entry.Source, ["srcFile"] = entry.SourceFile,
                ["createdAt"] = entry.CreatedAt.ToString("O"), ["appVersion"] = entry.AppVersion,
                ["pending"] = entry.Pending ? true : null
            }).ToArray()),
            ["runs"] = new JsonArray(_runs.Select(run => (JsonNode?)run.DeepClone()).ToArray())
        };
        var path = Path.Combine(directory, "ledger.json");
        var temporary = Path.Combine(directory, ".ledger-" + Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                       FileOptions.WriteThrough))
            {
                stream.Write(JsonSerializer.SerializeToUtf8Bytes(root,
                    new JsonSerializerOptions { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }));
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}

internal sealed record ClaudeLiveSessionScan(IReadOnlySet<string> Ids, bool Unknown);

/// <summary>Official Claude CLIs register running sessions as <c>&lt;pid&gt;.json</c> with {pid, sessionId, procStart}.
/// The start time is matched so a reused pid cannot keep a stale registration alive; a process that cannot be
/// inspected counts as live. A registration that cannot be read is ignored only when the process its file name
/// names has exited; otherwise the writer is unknown.</summary>
internal static class ClaudeLiveSessions
{
    private const int ErrorInvalidParameter = 87;

    internal static ClaudeLiveSessionScan Read(IEnumerable<string> registryDirectories)
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        var unknown = false;
        foreach (var directory in registryDirectories.Where(Directory.Exists))
            foreach (var path in Directory.EnumerateFiles(directory, "*.json"))
            {
                // Official registrations are named after their process id; other JSON files are not registrations.
                if (!int.TryParse(Path.GetFileNameWithoutExtension(path), out var namedPid) || namedPid <= 0) continue;
                try
                {
                    if (new FileInfo(path).Length > 64 * 1024) { unknown |= !HasExited(namedPid); continue; }
                    using var document = JsonDocument.Parse(File.ReadAllBytes(path));
                    var root = document.RootElement;
                    if (!root.TryGetProperty("pid", out var pid) || pid.ValueKind != JsonValueKind.Number || pid.GetInt32() != namedPid
                        || !root.TryGetProperty("sessionId", out var session) || session.ValueKind != JsonValueKind.String)
                    { unknown |= !HasExited(namedPid); continue; }
                    var started = ProcessStartFileTime(pid.GetInt32(), out var missing);
                    if (missing) continue; // the registered process has exited
                    if (started is not null && root.TryGetProperty("procStart", out var recorded)
                        && recorded.ValueKind == JsonValueKind.Number
                        && Math.Abs(recorded.GetInt64() - started.Value) >= TimeSpan.TicksPerSecond) continue; // reused pid
                    live.Add(session.GetString()!.ToLowerInvariant());
                }
                catch (Exception exception) when (exception is JsonException or FormatException or InvalidOperationException)
                { unknown |= !HasExited(namedPid); }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { unknown = true; }
            }
        return new ClaudeLiveSessionScan(live, unknown);
    }

    private static bool HasExited(int processId)
    {
        ProcessStartFileTime(processId, out var missing);
        return missing;
    }

    /// <summary>Returns the creation FILETIME, or null when the process exists but cannot be inspected.</summary>
    internal static long? ProcessStartFileTime(int processId, out bool missing)
    {
        missing = false;
        using var handle = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle.IsInvalid) { missing = Marshal.GetLastWin32Error() == ErrorInvalidParameter; return null; }
        return GetProcessTimes(handle, out var created, out _, out _, out _) ? created : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(Microsoft.Win32.SafeHandles.SafeProcessHandle process, out long creation,
        out long exit, out long kernel, out long user);
}
