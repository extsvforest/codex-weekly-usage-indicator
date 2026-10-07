using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WeeklyUsageIndicator;

/// <summary>
/// The Claude account whose session titles, pins and groups the other accounts copy when the user asks. It is chosen
/// on the account page and read by the <c>claude-session-sync</c> skill. The widget never changes a sidebar itself:
/// the skill applies the plan with the Claude app's own session tools, so the app keeps what it writes.
/// </summary>
internal sealed record ClaudeSessionReference(string Account, string Label, string? ProfileId, DateTimeOffset SetAt,
    IReadOnlyDictionary<string, string> Labels)
{
    internal const string FileName = "session-reference.json";

    internal static ClaudeSessionReference? Load(string ledgerDirectory)
    {
        try
        {
            var path = Path.Combine(ledgerDirectory, FileName);
            if (!File.Exists(path) || new FileInfo(path).Length > 1024 * 1024) return null;
            if (JsonNode.Parse(File.ReadAllText(path, Encoding.UTF8)) is not JsonObject root
                || Text(root, "account") is not { } account || !ClaudeSessionSync.IsAccountKey(account)) return null;
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root["labels"] is JsonObject map)
                foreach (var (key, value) in map)
                    if (ClaudeSessionSync.IsAccountKey(key) && value is JsonValue text && text.TryGetValue<string>(out var label))
                        labels[key.ToLowerInvariant()] = label;
            var setAt = DateTimeOffset.TryParse(Text(root, "setAt"), out var parsed) ? parsed : DateTimeOffset.MinValue;
            return new(account.ToLowerInvariant(), Text(root, "label") ?? "", Text(root, "profileId"), setAt, labels);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { return null; }
    }

    internal void Save(string ledgerDirectory)
    {
        Directory.CreateDirectory(ledgerDirectory);
        var labels = new JsonObject();
        foreach (var (key, label) in Labels.OrderBy(pair => pair.Key, StringComparer.Ordinal)) labels[key] = label;
        var root = new JsonObject
        {
            ["account"] = Account, ["label"] = Label, ["profileId"] = ProfileId, ["setAt"] = SetAt.ToString("O"), ["labels"] = labels
        };
        var path = Path.Combine(ledgerDirectory, FileName);
        var partial = path + ".partial";
        File.WriteAllText(partial, root.ToJsonString(new JsonSerializerOptions
        { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
        File.Move(partial, path, overwrite: true);
    }

    internal static void Clear(string ledgerDirectory)
    {
        var path = Path.Combine(ledgerDirectory, FileName);
        if (File.Exists(path)) File.Delete(path);
    }

    private static string? Text(JsonObject record, string field) =>
        record[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

/// <summary>
/// Plans how to make the signed-in account's Code sidebar match the reference account: the same titles, pins and
/// groups for every conversation both lists hold. Everything is read from disk, read-only, while the app runs:
/// titles and pins (<c>isStarred</c>) from each account's session records, groups from the app's
/// <c>dframe-store</c> in localStorage (<c>customGroupsByScope</c>, keyed by account/organization). A conversation
/// is matched by its current transcript id, or as a continuation of the other side's. Nothing is deleted: sessions
/// and groups only one side has are left alone.
/// </summary>
internal static class ClaudeSessionSync
{
    internal const string CommandFlag = "--claude-session-plan";
    private const long MaxRecordBytes = 10 * 1024 * 1024;
    private const string GroupsKey = "dframe-store";
    private static readonly JsonSerializerOptions PlanJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true
    };

    internal sealed record Record(string SessionId, string? Title, bool Pinned, string? Cli, IReadOnlyList<string> Prior,
        long LastActivityAt);
    internal sealed record Group(string Id, string Name);
    internal sealed record Layout(IReadOnlyList<Group> Groups, IReadOnlyDictionary<string, string> Assignments);
    internal sealed record Move(string? Group, IReadOnlyList<string> Sessions);
    internal sealed record Pin(string Session, bool Pinned, string? Title);
    internal sealed record Rename(string Session, string Title, string? From);

    /// <summary>Status is <c>ok</c> when a plan was made (possibly empty); otherwise it says why not.</summary>
    internal sealed record Plan(string Status, string? Current = null, string? CurrentLabel = null, string? Reference = null,
        string? ReferenceLabel = null, IReadOnlyList<string>? CreateGroups = null, IReadOnlyList<Move>? Moves = null,
        IReadOnlyList<Pin>? Pins = null, IReadOnlyList<Rename>? Renames = null, int? Matched = null, int? Unmatched = null,
        IReadOnlyList<string>? Warnings = null);

    internal static bool IsAccountKey(string? key) =>
        key?.Split('/') is [var account, var org] && Guid.TryParse(account, out _) && Guid.TryParse(org, out _);

    /// <summary><c>--claude-session-plan --session local_… [--reference account/org]</c>: prints the plan as UTF-8 JSON.</summary>
    internal static int RunCommand(string[] args)
    {
        string? session = null, reference = null;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--session" && index + 1 < args.Length) session = args[++index];
            else if (args[index] == "--reference" && index + 1 < args.Length) reference = args[++index];
            else return Print(new Plan("bad_arguments"), 2);
        }
        if (session is null || !session.StartsWith("local_", StringComparison.Ordinal)
            || session.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || reference is not null && !IsAccountKey(reference))
            return Print(new Plan("bad_arguments"), 2);
        var paths = ClaudeShelfPaths.CreateDefault();
        var localStorage = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Claude", "Local Storage", "leveldb");
        return Print(Build(paths.SessionsRoot, localStorage, paths.LedgerDirectory, session, reference), 0);
    }

    internal static Plan Build(string sessionsRoot, string localStorageDirectory, string ledgerDirectory, string sessionId,
        string? referenceOverride = null)
    {
        var marker = ClaudeSessionReference.Load(ledgerDirectory);
        var labels = marker?.Labels ?? new Dictionary<string, string>();
        string? Label(string? key) => key is not null && labels.TryGetValue(key, out var label) ? label : null;
        var current = FindAccount(sessionsRoot, sessionId);
        if (current is null) return new("unknown_session");
        var reference = (referenceOverride ?? marker?.Account)?.ToLowerInvariant();
        if (reference is null) return new("no_reference", current.Value.Key, Label(current.Value.Key));
        if (reference == current.Value.Key)
            return new("is_reference", current.Value.Key, Label(current.Value.Key), reference, Label(reference));
        if (AccountDirectory(sessionsRoot, reference) is not { } referenceDirectory)
            return new("reference_missing", current.Value.Key, Label(current.Value.Key), reference, Label(reference));
        var warnings = new List<string>();
        JsonObject? scopes = null;
        try { scopes = ReadGroupScopes(localStorageDirectory); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                                              or JsonException or InvalidOperationException)
        { scopes = null; }
        // The reference's groups must be confirmed: read as empty, they would ungroup this account. This account
        // having no scope yet only means it has no groups, and planning for it only adds.
        var currentLayout = LayoutOf(scopes, current.Value.Key, missingIsEmpty: true);
        var referenceLayout = LayoutOf(scopes, reference, missingIsEmpty: false);
        if (currentLayout is null || referenceLayout is null) { currentLayout = referenceLayout = null; warnings.Add("groups_unreadable"); }
        return Compute(current.Value.Key, Label(current.Value.Key), reference, Label(reference),
            ReadRecords(current.Value.Directory), ReadRecords(referenceDirectory), currentLayout, referenceLayout, warnings);
    }

    /// <summary>
    /// The changes, in the order they must run: create the missing groups, move sessions (moving a pinned session into
    /// a group unpins it, so pins are planned after the moves), set pins, then titles. Groups are planned only when
    /// both accounts' groups could be read.
    /// </summary>
    internal static Plan Compute(string current, string? currentLabel, string reference, string? referenceLabel,
        IReadOnlyList<Record> currentRecords, IReadOnlyList<Record> referenceRecords, Layout? currentLayout,
        Layout? referenceLayout, IReadOnlyList<string>? warnings = null)
    {
        var byCli = referenceRecords.Where(record => record.Cli is not null).ToLookup(record => record.Cli!, StringComparer.Ordinal);
        var byPrior = referenceRecords.SelectMany(record => record.Prior.Select(id => (Id: id, Record: record)))
            .ToLookup(pair => pair.Id, pair => pair.Record, StringComparer.Ordinal);
        // One reference record serves one session here. The same transcript id is matched first; a continuation
        // either way (one side lists the other's current transcript as earlier) only takes a record still free,
        // so an older state or a branch of a conversation that is already matched is left alone.
        var newest = currentRecords.OrderByDescending(record => record.LastActivityAt).ToArray();
        var claimed = new HashSet<string>(StringComparer.Ordinal);
        var matches = new Dictionary<string, Record>(StringComparer.Ordinal);
        foreach (var record in newest)
            if (record.Cli is { } cli && byCli[cli].Where(candidate => !claimed.Contains(candidate.SessionId))
                    .MaxBy(candidate => candidate.LastActivityAt) is { } same)
            { matches[record.SessionId] = same; claimed.Add(same.SessionId); }
        foreach (var record in newest.Where(record => !matches.ContainsKey(record.SessionId)))
            if ((record.Cli is { } id ? byPrior[id] : []).Concat(record.Prior.SelectMany(prior => byCli[prior]))
                    .Where(candidate => !claimed.Contains(candidate.SessionId)).MaxBy(candidate => candidate.LastActivityAt) is { } continued)
            { matches[record.SessionId] = continued; claimed.Add(continued.SessionId); }
        // Known is false for an assignment to a group the scope does not list: that session is not moved.
        static (bool Known, string? Name) GroupOf(Layout layout, string sessionId) =>
            !layout.Assignments.TryGetValue(sessionId, out var id) ? (true, null)
            : layout.Groups.FirstOrDefault(group => group.Id == id) is { } group ? (true, group.Name) : (false, null);

        var groups = currentLayout is not null && referenceLayout is not null;
        var existing = currentLayout?.Groups.Select(group => group.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        var create = groups ? referenceLayout!.Groups.Select(group => group.Name)
            .Where(name => !existing.Contains(name)).Distinct(StringComparer.Ordinal).ToList() : [];
        var into = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var ungroup = new List<string>();
        var pins = new List<Pin>();
        var renames = new List<Rename>();
        int matched = 0, unmatched = 0;
        foreach (var record in newest)
        {
            if (!matches.TryGetValue(record.SessionId, out var source)) { unmatched++; continue; }
            matched++;
            var pinned = record.Pinned;
            if (groups && GroupOf(referenceLayout!, source.SessionId) is { Known: true, Name: var target }
                && GroupOf(currentLayout!, record.SessionId) is { Known: true, Name: var now } && target != now)
            {
                if (target is null) ungroup.Add(record.SessionId);
                else
                {
                    if (!into.TryGetValue(target, out var sessions)) into[target] = sessions = [];
                    sessions.Add(record.SessionId);
                    pinned = false;
                }
            }
            if (source.Pinned != pinned) pins.Add(new(record.SessionId, source.Pinned, source.Title ?? record.Title));
            if (!string.IsNullOrWhiteSpace(source.Title) && source.Title != record.Title)
                renames.Add(new(record.SessionId, source.Title, record.Title));
        }
        var moves = into.Select(pair => new Move(pair.Key, pair.Value)).ToList();
        if (ungroup.Count > 0) moves.Add(new Move(null, ungroup));
        return new("ok", current, currentLabel, reference, referenceLabel, create, moves, pins, renames, matched, unmatched,
            warnings is { Count: > 0 } ? warnings : null);
    }

    /// <summary>One account's groups as the app stores them; null when they cannot be confirmed (no store, an
    /// unfamiliar shape, or a missing scope unless <paramref name="missingIsEmpty"/>).</summary>
    internal static Layout? LayoutOf(JsonObject? scopes, string account, bool missingIsEmpty)
    {
        if (scopes is null) return null;
        var entry = scopes.FirstOrDefault(pair => string.Equals(pair.Key, account, StringComparison.OrdinalIgnoreCase));
        if (entry.Key is null) return missingIsEmpty ? new([], new Dictionary<string, string>()) : null;
        if (entry.Value is not JsonObject scope) return null;
        // Only an explicit list and map confirm "no groups": a missing or null field is unconfirmed.
        if (scope["groups"] is not JsonArray list || scope["assignments"] is not JsonObject map) return null;
        var groups = new List<Group>();
        foreach (var node in list)
        {
            if (node is not JsonObject group || Text(group, "id") is not { } id || Text(group, "name") is not { } name) return null;
            groups.Add(new(id, name));
        }
        var assignments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in map)
        {
            if (value is not JsonValue text || !text.TryGetValue<string>(out var id)) return null;
            if (key.StartsWith("code:", StringComparison.Ordinal)) assignments[key["code:".Length..]] = id;
        }
        return new(groups, assignments);
    }

    /// <summary>The app's groups of every account. Throws unless a complete snapshot holds them: the app may be
    /// writing (a log ending inside a record) or compacting (a table replaced underneath), so either is retried.</summary>
    private static JsonObject ReadGroupScopes(string localStorageDirectory)
    {
        if (!Directory.Exists(localStorageDirectory)) throw new DirectoryNotFoundException("No app local storage.");
        var store = new ClaudeLocalStorage(localStorageDirectory);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var snapshot = store.Read([GroupsKey], whileOpen: true);
                if (!snapshot.Clean) throw new InvalidDataException("The app is writing its local storage.");
                var text = snapshot.Values[GroupsKey] ?? throw new InvalidDataException("No sidebar state.");
                return (JsonNode.Parse(text) as JsonObject)?["state"]?["customGroupsByScope"] as JsonObject
                       ?? throw new InvalidDataException("The app's sidebar state has an unfamiliar shape.");
            }
            catch (Exception exception) when (attempt < 2 && exception is IOException or InvalidDataException)
            { Thread.Sleep(300); }
        }
    }

    private static (string Key, string Directory)? FindAccount(string sessionsRoot, string sessionId)
    {
        if (!Directory.Exists(sessionsRoot)) return null;
        foreach (var account in Directory.EnumerateDirectories(sessionsRoot))
            foreach (var org in Directory.EnumerateDirectories(account))
            {
                var key = (Path.GetFileName(account) + "/" + Path.GetFileName(org)).ToLowerInvariant();
                if (IsAccountKey(key) && File.Exists(Path.Combine(org, sessionId + ".json"))) return (key, org);
            }
        return null;
    }

    private static string? AccountDirectory(string sessionsRoot, string key)
    {
        if (!Directory.Exists(sessionsRoot) || key.Split('/') is not [var account, var org]) return null;
        var accountDirectory = Directory.EnumerateDirectories(sessionsRoot)
            .FirstOrDefault(path => string.Equals(Path.GetFileName(path), account, StringComparison.OrdinalIgnoreCase));
        return accountDirectory is null ? null : Directory.EnumerateDirectories(accountDirectory)
            .FirstOrDefault(path => string.Equals(Path.GetFileName(path), org, StringComparison.OrdinalIgnoreCase));
    }

    internal static IReadOnlyList<Record> ReadRecords(string directory)
    {
        var records = new List<Record>();
        foreach (var path in Directory.EnumerateFiles(directory, "local_*.json"))
        {
            JsonObject? record;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length is <= 0 or > MaxRecordBytes) continue;
                record = JsonNode.Parse(stream) as JsonObject;
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException) { continue; }
            if (record is null || record["isArchived"] is JsonValue archived && archived.TryGetValue<bool>(out var isArchived) && isArchived)
                continue;
            var prior = new List<string>();
            if (record["priorCliSessionIds"] is JsonArray ids)
                foreach (var node in ids)
                    if (node is JsonValue value && value.TryGetValue<string>(out var id)) prior.Add(id.ToLowerInvariant());
            records.Add(new(Path.GetFileNameWithoutExtension(path), Text(record, "title"),
                record["isStarred"] is JsonValue starred && starred.TryGetValue<bool>(out var isStarred) && isStarred,
                Text(record, "cliSessionId")?.ToLowerInvariant(), prior,
                record["lastActivityAt"] is JsonValue at && at.TryGetValue<long>(out var activity) ? activity : 0));
        }
        return records;
    }

    private static string? Text(JsonObject record, string field) =>
        record[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static int Print(Plan plan, int exitCode)
    {
        using var output = Console.OpenStandardOutput();
        output.Write(JsonSerializer.SerializeToUtf8Bytes(plan, PlanJson));
        output.Write("\n"u8);
        return exitCode;
    }
}
