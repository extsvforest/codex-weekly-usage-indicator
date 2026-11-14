using System.Buffers;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WeeklyUsageIndicator;

/// <summary>
/// The old session layout reference: the account whose sidebar the others used to copy, chosen on the account page. It
/// is read only when the widget first keeps <see cref="ClaudeSessionLayout"/>: the account it names is taken as the one
/// the app left last, when it was chosen.
/// </summary>
internal sealed record ClaudeSessionReference(string Account, string Label, string? ProfileId, DateTimeOffset SetAt,
    IReadOnlyDictionary<string, string> Labels)
{
    internal const string FileName = "session-reference.json";

    internal static ClaudeSessionReference? Load(string ledgerDirectory) => Read(ledgerDirectory).Reference;

    /// <summary>The old reference, if any. <c>Known</c> is true when the file is absent (no reference) or read; one
    /// that is there but cannot be opened or read as a reference is unknown, so nothing starts without it.</summary>
    internal static (ClaudeSessionReference? Reference, bool Known) Read(string ledgerDirectory)
    {
        string text;
        try
        {
            using var stream = new FileStream(Path.Combine(ledgerDirectory, FileName), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > 1024 * 1024) return (null, false);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            text = reader.ReadToEnd();
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return (null, true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return (null, false); }
        try
        {
            if (JsonNode.Parse(text) is not JsonObject root
                || Text(root, "account") is not { } account || !ClaudeSessionSync.IsAccountKey(account)) return (null, false);
            var labels = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root["labels"] is JsonObject map)
                foreach (var (key, value) in map)
                    if (ClaudeSessionSync.IsAccountKey(key) && value is JsonValue node && node.TryGetValue<string>(out var label))
                        labels[key.ToLowerInvariant()] = label;
            var setAt = DateTimeOffset.TryParse(Text(root, "setAt"), out var parsed) ? parsed : DateTimeOffset.MinValue;
            return (new(account.ToLowerInvariant(), Text(root, "label") ?? "", Text(root, "profileId"), setAt, labels), true);
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException) { return (null, false); }
    }

    /// <summary>Writes it as the account page did; fixtures only.</summary>
    internal void Save(string ledgerDirectory)
    {
        Directory.CreateDirectory(ledgerDirectory);
        var labels = new JsonObject();
        foreach (var (key, label) in Labels.OrderBy(pair => pair.Key, StringComparer.Ordinal)) labels[key] = label;
        var root = new JsonObject
        {
            ["account"] = Account, ["label"] = Label, ["profileId"] = ProfileId, ["setAt"] = SetAt.ToString("O"), ["labels"] = labels
        };
        File.WriteAllText(Path.Combine(ledgerDirectory, FileName), root.ToJsonString(), new UTF8Encoding(false));
    }

    private static string? Text(JsonObject record, string field) =>
        record[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

/// <summary>
/// What the widget keeps about each mapped account for the <c>claude-session-sync</c> plan: its label, its stays (when
/// the app last came to it and last left it), what its Code sidebar showed at its last checkpoint, the newest change it
/// made to each value — a conversation's pin, group and title, and its group list in sidebar order — and what the last
/// plan there set out to change. A plan takes, value by value, the newest change any account made, so every sidebar
/// follows what was changed last, wherever that was. Only the widget writes it: it checkpoints the account the app
/// comes to, and the account a plan runs in (see <see cref="ClaudeSessionSync.PlannedEventName"/>), recording what
/// changed there since the checkpoint before; what a plan brought in keeps the time of the change it came from.
/// </summary>
internal sealed class ClaudeSessionLayout
{
    internal const string FileName = "session-layout.json";
    private const long MaxBytes = 32 * 1024 * 1024;

    /// <param name="LeftAt">When the app last left the account: what changed there after its last checkpoint was
    /// changed by then.</param>
    /// <param name="ArrivedAt">When the app last came to it.</param>
    /// <param name="Stay">Counts the app's arrivals: a checkpoint or a plan of another stay is past.</param>
    internal sealed record Account(string Label, DateTimeOffset? LeftAt = null, DateTimeOffset? ArrivedAt = null, int Stay = 0);

    /// <summary>What a conversation showed. A null <c>Pinned</c> is unknown and a null <c>Title</c> none;
    /// <c>HasGroup</c> false is an unknown group, and <c>Group</c> null with it "in no group".</summary>
    /// <param name="Written">For the widget's copy, the title its ledger said the widget had written there.</param>
    /// <param name="Model">Its model, effort and permission mode as <see cref="ClaudeSessionSync.Record"/> reads them: null
    /// unknown (a checkpoint an earlier widget took, or a session passing through plan mode), empty none.</param>
    internal sealed record Shown(bool? Pinned = null, bool HasGroup = false, string? Group = null, string? Title = null,
        string? Written = null, string? Model = null, string? Effort = null, string? Mode = null);

    /// <param name="By">The account the change came from when a plan brought it in; null for the account's own.</param>
    internal sealed record Stamp<T>(T Value, DateTimeOffset At, string? By = null);

    /// <summary>The newest change an account made to each value of one conversation; null where it changed nothing.</summary>
    internal sealed record Changes(Stamp<bool>? Pinned = null, Stamp<string?>? Group = null, Stamp<string>? Title = null,
        Stamp<string>? Model = null, Stamp<string>? Effort = null, Stamp<string>? Mode = null)
    {
        internal bool IsEmpty => Pinned is null && Group is null && Title is null && Model is null && Effort is null && Mode is null;

        /// <summary>Value by value the later change of this and <paramref name="older"/>, this one on a tie.</summary>
        internal Changes Over(Changes? older) => older is null ? this
            : new(Later(Pinned, older.Pinned), Later(Group, older.Group), Later(Title, older.Title), Later(Model, older.Model),
                Later(Effort, older.Effort), Later(Mode, older.Mode));

        private static Stamp<T>? Later<T>(Stamp<T>? mine, Stamp<T>? theirs) =>
            mine is null ? theirs : theirs is null || mine.At >= theirs.At ? mine : theirs;
    }

    internal sealed record Named(string Id, string Name);

    /// <param name="Stay">The stay it was taken in.</param>
    /// <param name="Groups">The groups in sidebar order; null when they were not known.</param>
    /// <param name="Nested">For each session nested then, what it would show once out of its family: pinned where it or
    /// its family showed pinned (the app pins one it moves to top level from there), and the group it is filed under
    /// itself. Null in a checkpoint an earlier widget took.</param>
    internal sealed record Checkpoint(DateTimeOffset At, int Stay, IReadOnlyList<Named>? Groups,
        IReadOnlyDictionary<string, Shown> Sessions, IReadOnlyDictionary<string, Shown>? Nested = null);

    /// <summary>A change of an account's group list: the names in sidebar order, and the groups it renamed (old name →
    /// new name) so that what was filed under an old name follows.</summary>
    internal sealed record GroupChange(IReadOnlyList<string> Order, IReadOnlyDictionary<string, string> Renamed,
        DateTimeOffset At, string? By = null);

    /// <summary>What a plan sets out to give one session, each value with the change it comes from.</summary>
    /// <param name="Filed">The plan moves it into a group, which unpins it until the plan pins it again.</param>
    internal sealed record Target(Stamp<bool>? Pinned = null, Stamp<string?>? Group = null, Stamp<string>? Title = null,
        bool Filed = false, Stamp<string>? Model = null, Stamp<string>? Effort = null, Stamp<string>? Mode = null);

    /// <summary>What the last plan in an account set out to change, until a checkpoint finds it done or changed
    /// otherwise: in the meantime neither a value it has not reached yet nor what its moves do on the way is anyone's
    /// choice.</summary>
    /// <param name="Renamed">The groups it renames out of the way, by id: the name each had and the one it is given.</param>
    internal sealed record Pending(int Stay, IReadOnlyDictionary<string, Target> Sessions, GroupChange? Groups,
        IReadOnlyDictionary<string, Moved> Renamed);

    /// <summary>A group a plan renames out of the way: it is aside only while it bears the name given.</summary>
    internal sealed record Moved(string From, string To);

    /// <summary>A group renamed (<c>From</c> to <c>To</c>), added (no <c>From</c>) or removed (no <c>To</c>) by hand:
    /// a group list older than the edit takes it, however the list moves meanwhile, and what was filed under an old name
    /// follows a renaming.</summary>
    internal sealed record GroupRenaming(string? From, string? To, DateTimeOffset At);

    /// <summary>Kept from the first format: a session there that is known to have shown a pin or a group value, so its
    /// "no pin" or "no group" counts as a value rather than as a copy's untouched default.</summary>
    internal sealed record Evidence(bool Pinned, bool Group);

    /// <summary>The mapped accounts by account/organization key.</summary>
    internal Dictionary<string, Account> Accounts { get; } = new(StringComparer.Ordinal);
    /// <summary>The account the app showed when the widget last confirmed one, so a switch made while the widget was
    /// not watching is still told.</summary>
    internal string? Active { get; set; }
    internal Dictionary<string, Checkpoint> Checkpoints { get; } = new(StringComparer.Ordinal);
    /// <summary>Per account, its newest change of each conversation's values, by its own session ids.</summary>
    internal Dictionary<string, Dictionary<string, Changes>> Changed { get; } = new(StringComparer.Ordinal);
    /// <summary>Per account, its newest change of its group list.</summary>
    internal Dictionary<string, GroupChange> GroupsChanged { get; } = new(StringComparer.Ordinal);
    internal Dictionary<string, Pending> Pendings { get; } = new(StringComparer.Ordinal);
    /// <summary>Per account and session id, until the account's first checkpoint.</summary>
    internal Dictionary<string, Dictionary<string, Evidence>> Evidences { get; } = new(StringComparer.Ordinal);
    /// <summary>Per account, the groups it renamed, added or removed by hand, oldest first, the latest kept.</summary>
    internal Dictionary<string, List<GroupRenaming>> Renamings { get; } = new(StringComparer.Ordinal);
    internal const int MaxRenamings = 200;
    /// <summary>Read from the first format, which kept per-pair agreements instead: its accounts carried over, and what
    /// its agreements tell as evidence.</summary>
    internal bool Upgraded { get; private set; }

    /// <summary>Forgets an account that is no longer mapped.</summary>
    /// <summary>Moves what <paramref name="account"/> changed by <paramref name="by"/> (its own changes, not what a plan
    /// brought in from another account) to <paramref name="to"/>.</summary>
    internal void Redate(string account, DateTimeOffset by, DateTimeOffset to)
    {
        Stamp<T>? Moved<T>(Stamp<T>? stamp) => stamp is { By: null } && stamp.At <= by ? stamp with { At = to } : stamp;
        if (Changed.TryGetValue(account, out var changed))
            foreach (var id in changed.Keys.ToArray())
            {
                var change = changed[id];
                changed[id] = new(Moved(change.Pinned), Moved(change.Group), Moved(change.Title), Moved(change.Model),
                    Moved(change.Effort), Moved(change.Mode));
            }
        if (GroupsChanged.TryGetValue(account, out var groups) && groups is { By: null } && groups.At <= by)
            GroupsChanged[account] = groups with { At = to };
        if (Renamings.TryGetValue(account, out var renamings))
            for (var index = 0; index < renamings.Count; index++)
                if (renamings[index].At <= by) renamings[index] = renamings[index] with { At = to };
    }

    internal void Forget(string account)
    {
        Accounts.Remove(account); Checkpoints.Remove(account); Changed.Remove(account); GroupsChanged.Remove(account);
        Pendings.Remove(account); Evidences.Remove(account); Renamings.Remove(account);
        if (Active == account) Active = null;
    }

    /// <summary>The kept layout. <c>Exists</c> is false when there is none yet; <c>Layout</c> is null when one is there
    /// but cannot be read whole.</summary>
    internal static (ClaudeSessionLayout? Layout, bool Exists) Load(string ledgerDirectory)
    {
        try
        {
            using var stream = new FileStream(Path.Combine(ledgerDirectory, FileName), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
            if (stream.Length > MaxBytes || JsonNode.Parse(stream) is not JsonObject root
                || root["accounts"] is not JsonObject accounts || root["version"] is not JsonValue versionValue
                || !versionValue.TryGetValue<int>(out var version) || version is not (1 or 2)) return (null, true);
            var layout = new ClaudeSessionLayout { Upgraded = version == 1 };
            if (version == 2 && root["active"] is { } active)
            {
                if (active is not JsonValue activeValue || !activeValue.TryGetValue<string>(out var key)
                    || !ClaudeSessionSync.IsAccountKey(key)) return (null, true);
                layout.Active = key.ToLowerInvariant();
            }
            foreach (var (key, node) in accounts)
            {
                if (!ClaudeSessionSync.IsAccountKey(key) || node is not JsonObject account || Text(account, "label") is not { } label
                    || !TryTime(account["leftAt"], out var leftAt) || !TryTime(account["arrivedAt"], out var arrivedAt)
                    || !TryCount(account["stay"], out var stay)) return (null, true);
                var owner = key.ToLowerInvariant();
                layout.Accounts[owner] = new(label, leftAt, arrivedAt, stay);
                if (version == 1) continue;
                if (account["checkpoint"] is { } checkpointNode)
                {
                    if (checkpointNode is not JsonObject checkpoint || ReadCheckpoint(checkpoint) is not { } read) return (null, true);
                    layout.Checkpoints[owner] = read;
                }
                if (account["groupsChanged"] is { } groupsNode)
                {
                    if (groupsNode is not JsonObject groups || ReadGroupChange(groups) is not { } read) return (null, true);
                    layout.GroupsChanged[owner] = read;
                }
                if (account["changed"] is { } changedNode)
                {
                    if (changedNode is not JsonObject changed) return (null, true);
                    var entries = new Dictionary<string, Changes>(StringComparer.Ordinal);
                    foreach (var (id, entry) in changed)
                    {
                        if (entry is not JsonObject fields || ReadChanges(fields) is not { } read) return (null, true);
                        entries[id] = read;
                    }
                    layout.Changed[owner] = entries;
                }
                if (account["pending"] is { } pendingNode)
                {
                    if (pendingNode is not JsonObject pending || ReadPending(pending) is not { } read) return (null, true);
                    layout.Pendings[owner] = read;
                }
                if (account["renamings"] is { } renamingsNode)
                {
                    if (renamingsNode is not JsonArray list) return (null, true);
                    var entries = new List<GroupRenaming>();
                    foreach (var item in list)
                    {
                        if (item is not JsonObject renaming || renaming.Count != 3 || !renaming.ContainsKey("from") || !renaming.ContainsKey("to")
                            || !TryName(renaming["from"], out var from) || !TryName(renaming["to"], out var to) || from is null && to is null
                            || !TryTime(renaming["at"], out var at) || at is null) return (null, true);
                        entries.Add(new(from, to, at.Value));
                    }
                    layout.Renamings[owner] = entries;
                }
                if (account["evidence"] is { } evidenceNode)
                {
                    if (evidenceNode is not JsonObject evidence) return (null, true);
                    var entries = new Dictionary<string, Evidence>(StringComparer.Ordinal);
                    foreach (var (id, entry) in evidence)
                    {
                        if (entry is not JsonObject fields || !TryFlag(fields["pinned"], out var pinned)
                            || !TryFlag(fields["group"], out var group)) return (null, true);
                        entries[id] = new(pinned, group);
                    }
                    layout.Evidences[owner] = entries;
                }
            }
            if (version == 1 && root["pairs"] is JsonObject pairs && ReadAgreements(pairs, layout) is false) return (null, true);
            if (layout.Active is { } current && !layout.Accounts.ContainsKey(current)) layout.Active = null;
            return (layout, true);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return (null, false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                                              or InvalidOperationException or FormatException)
        { return (null, true); }
    }

    /// <summary>The first format's per-pair agreements as evidence: a session one side agreed on a pin or a group for
    /// had shown that value there.</summary>
    private static bool ReadAgreements(JsonObject pairs, ClaudeSessionLayout layout)
    {
        foreach (var (_, node) in pairs)
        {
            if (node is not JsonObject sides) return false;
            foreach (var (account, sideNode) in sides)
            {
                if (sideNode is not JsonObject entries) return false;
                var owner = account.ToLowerInvariant();
                if (!layout.Accounts.ContainsKey(owner)) continue;
                if (!layout.Evidences.TryGetValue(owner, out var evidence)) layout.Evidences[owner] = evidence = new(StringComparer.Ordinal);
                foreach (var (id, entryNode) in entries)
                {
                    if (entryNode is not JsonObject entry) return false;
                    var known = evidence.GetValueOrDefault(id) ?? new(false, false);
                    evidence[id] = new(known.Pinned || entry["pinned"] is JsonValue, known.Group || entry.ContainsKey("group"));
                }
            }
        }
        return true;
    }

    private static Checkpoint? ReadCheckpoint(JsonObject node)
    {
        if (!TryTime(node["at"], out var at) || at is null || !TryCount(node["stay"], out var stay)
            || node["sessions"] is not JsonObject sessions) return null;
        List<Named>? groups = null;
        if (node["groups"] is { } groupsNode)
        {
            if (groupsNode is not JsonArray list) return null;
            groups = [];
            foreach (var item in list)
            {
                if (item is not JsonObject group || Text(group, "id") is not { } id || Text(group, "name") is not { } name) return null;
                groups.Add(new(id, name));
            }
        }
        if (ReadSessions(sessions) is not { } shown) return null;
        Dictionary<string, Shown>? nested = null;
        if (node["nested"] is { } nestedNode && (nestedNode is not JsonObject entries || (nested = ReadSessions(entries)) is null)) return null;
        return new(at.Value, stay, groups, shown, nested);
    }

    private static Dictionary<string, Shown>? ReadSessions(JsonObject sessions)
    {
        var shown = new Dictionary<string, Shown>(StringComparer.Ordinal);
        foreach (var (id, entry) in sessions)
        {
            if (entry is not JsonObject fields || ReadShown(fields) is not { } read) return null;
            shown[id] = read;
        }
        return shown;
    }

    private static Shown? ReadShown(JsonObject entry)
    {
        bool? pinned = null; var hasGroup = false; string? group = null; string? title = null; string? written = null;
        string? model = null, effort = null, mode = null;
        foreach (var (field, value) in entry)
        {
            if (field == "pinned" && value is JsonValue pin && pin.TryGetValue<bool>(out var isPinned)) pinned = isPinned;
            else if (field == "group" && value is null) hasGroup = true;
            else if (field == "group" && value is JsonValue name && name.TryGetValue<string>(out var groupName))
            { hasGroup = true; group = groupName; }
            else if (field == "title" && value is JsonValue text && text.TryGetValue<string>(out var titleText)) title = titleText;
            else if (field == "written" && value is JsonValue mark && mark.TryGetValue<string>(out var writtenText)) written = writtenText;
            else if (field == "model" && value is JsonValue modelValue && modelValue.TryGetValue<string>(out var modelText)) model = modelText;
            else if (field == "effort" && value is JsonValue effortValue && effortValue.TryGetValue<string>(out var effortText)) effort = effortText;
            else if (field == "mode" && value is JsonValue modeValue && modeValue.TryGetValue<string>(out var modeText)) mode = modeText;
            else return null;
        }
        return new(pinned, hasGroup, group, title, written, model, effort, mode);
    }

    private static GroupChange? ReadGroupChange(JsonObject node)
    {
        if (ReadNames(node["order"]) is not { } order || node["renamed"] is not JsonObject renamedNode
            || !TryTime(node["at"], out var at) || at is null || !TryAccount(node["by"], out var by)) return null;
        var renamed = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (from, to) in renamedNode)
            if (to is JsonValue value && value.TryGetValue<string>(out var name)) renamed[from] = name;
            else return null;
        return new(order, renamed, at.Value, by);
    }

    private static IReadOnlyList<string>? ReadNames(JsonNode? node)
    {
        if (node is not JsonArray list) return null;
        var names = new List<string>();
        foreach (var item in list)
            if (item is JsonValue value && value.TryGetValue<string>(out var name)) names.Add(name);
            else return null;
        return names;
    }

    private static Changes? ReadChanges(JsonObject entry)
    {
        Stamp<bool>? pinned = null; Stamp<string?>? group = null; Stamp<string>? title = null;
        Stamp<string>? model = null, effort = null, mode = null;
        foreach (var (field, node) in entry)
        {
            if (node is not JsonObject stamp || !ReadStampParts(stamp, out var value, out var at, out var by)) return null;
            var text = value is JsonValue textValue && textValue.TryGetValue<string>(out var read) ? read : null;
            if (field == "pinned" && value is JsonValue pin && pin.TryGetValue<bool>(out var isPinned)) pinned = new(isPinned, at, by);
            else if (field == "group" && value is null) group = new(null, at, by);
            else if (field == "group" && text is not null) group = new(text, at, by);
            else if (field == "title" && text is not null) title = new(text, at, by);
            else if (field == "model" && text is not null) model = new(text, at, by);
            else if (field == "effort" && text is not null) effort = new(text, at, by);
            else if (field == "mode" && text is not null) mode = new(text, at, by);
            else return null;
        }
        return new(pinned, group, title, model, effort, mode);
    }

    private static Pending? ReadPending(JsonObject node)
    {
        if (!TryCount(node["stay"], out var stay) || node["sessions"] is not JsonObject sessions
            || node["renamed"] is not JsonObject renamedNode) return null;
        var targets = new Dictionary<string, Target>(StringComparer.Ordinal);
        foreach (var (id, entryNode) in sessions)
        {
            if (entryNode is not JsonObject entry || !TryFlag(entry["filed"], out var filed)) return null;
            var fields = new JsonObject();
            foreach (var (field, value) in entry)
                if (field != "filed") fields[field] = value?.DeepClone();
            if (ReadChanges(fields) is not { } read) return null;
            targets[id] = new(read.Pinned, read.Group, read.Title, filed, read.Model, read.Effort, read.Mode);
        }
        GroupChange? groups = null;
        if (node["groups"] is { } groupsNode && (groupsNode is not JsonObject groupsObject || (groups = ReadGroupChange(groupsObject)) is null))
            return null;
        var renamed = new Dictionary<string, Moved>(StringComparer.Ordinal);
        foreach (var (id, renaming) in renamedNode)
            if (renaming is JsonObject names && Text(names, "from") is { } from && Text(names, "to") is { } to && names.Count == 2)
                renamed[id] = new(from, to);
            else return null;
        return new(stay, targets, groups, renamed);
    }

    private static bool ReadStampParts(JsonObject stamp, out JsonNode? value, out DateTimeOffset at, out string? by)
    {
        value = null; at = default; by = null;
        if (!stamp.ContainsKey("value") || stamp.Count > 3 || !TryTime(stamp["at"], out var time) || time is null
            || !TryAccount(stamp["by"], out by) || stamp.Count == 3 && !stamp.ContainsKey("by")) return false;
        value = stamp["value"];
        at = time.Value;
        return true;
    }

    /// <summary>A null name is none; anything else must be a string.</summary>
    private static bool TryName(JsonNode? node, out string? name)
    {
        name = null;
        return node is null || node is JsonValue value && value.TryGetValue(out name);
    }

    /// <summary>An absent or null time is none; anything else must be one.</summary>
    private static bool TryTime(JsonNode? node, out DateTimeOffset? at)
    {
        at = null;
        if (node is null) return true;
        if (node is not JsonValue value || !value.TryGetValue<string>(out var text)
            || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed)) return false;
        at = parsed;
        return true;
    }

    private static bool TryCount(JsonNode? node, out int count)
    {
        count = 0;
        return node is null || node is JsonValue value && value.TryGetValue(out count) && count >= 0;
    }

    private static bool TryFlag(JsonNode? node, out bool flag)
    {
        flag = false;
        return node is null || node is JsonValue value && value.TryGetValue(out flag);
    }

    private static bool TryAccount(JsonNode? node, out string? account)
    {
        account = null;
        if (node is null) return true;
        if (node is not JsonValue value || !value.TryGetValue<string>(out var key) || !ClaudeSessionSync.IsAccountKey(key)) return false;
        account = key.ToLowerInvariant();
        return true;
    }

    internal void Save(string ledgerDirectory)
    {
        Directory.CreateDirectory(ledgerDirectory);
        var accounts = new JsonObject();
        foreach (var (key, account) in Accounts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var entry = new JsonObject
            {
                ["label"] = account.Label, ["leftAt"] = Time(account.LeftAt), ["arrivedAt"] = Time(account.ArrivedAt),
                ["stay"] = account.Stay
            };
            if (Checkpoints.TryGetValue(key, out var checkpoint))
            {
                static JsonObject Sessions(IReadOnlyDictionary<string, Shown> shownBy)
                {
                    var sessions = new JsonObject();
                    foreach (var (id, shown) in shownBy.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    {
                        var fields = new JsonObject();
                        if (shown.Pinned is { } pinned) fields["pinned"] = pinned;
                        if (shown.HasGroup) fields["group"] = shown.Group;
                        if (shown.Title is { } title) fields["title"] = title;
                        if (shown.Written is { } written) fields["written"] = written;
                        if (shown.Model is { } model) fields["model"] = model;
                        if (shown.Effort is { } effort) fields["effort"] = effort;
                        if (shown.Mode is { } mode) fields["mode"] = mode;
                        sessions[id] = fields;
                    }
                    return sessions;
                }
                var point = new JsonObject
                {
                    ["at"] = Time(checkpoint.At), ["stay"] = checkpoint.Stay,
                    ["groups"] = checkpoint.Groups is null ? null
                        : new JsonArray(checkpoint.Groups.Select(group => (JsonNode?)new JsonObject { ["id"] = group.Id, ["name"] = group.Name }).ToArray()),
                    ["sessions"] = Sessions(checkpoint.Sessions)
                };
                if (checkpoint.Nested is { } nested) point["nested"] = Sessions(nested);
                entry["checkpoint"] = point;
            }
            if (GroupsChanged.TryGetValue(key, out var groups)) entry["groupsChanged"] = Write(groups);
            if (Changed.TryGetValue(key, out var changed) && changed.Count > 0)
            {
                var entries = new JsonObject();
                foreach (var (id, change) in changed.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    entries[id] = Write(change);
                entry["changed"] = entries;
            }
            if (Pendings.TryGetValue(key, out var pending))
            {
                var sessions = new JsonObject();
                foreach (var (id, target) in pending.Sessions.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    var fields = Write(new Changes(target.Pinned, target.Group, target.Title, target.Model, target.Effort, target.Mode));
                    if (target.Filed) fields["filed"] = true;
                    sessions[id] = fields;
                }
                var renamed = new JsonObject();
                foreach (var (id, moved) in pending.Renamed.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    renamed[id] = new JsonObject { ["from"] = moved.From, ["to"] = moved.To };
                entry["pending"] = new JsonObject
                {
                    ["stay"] = pending.Stay, ["sessions"] = sessions,
                    ["groups"] = pending.Groups is { } order ? Write(order) : null, ["renamed"] = renamed
                };
            }
            if (Renamings.TryGetValue(key, out var renamings) && renamings.Count > 0)
                entry["renamings"] = new JsonArray(renamings.Select(renaming => (JsonNode?)new JsonObject
                    { ["from"] = renaming.From, ["to"] = renaming.To, ["at"] = Time(renaming.At) }).ToArray());
            if (Evidences.TryGetValue(key, out var evidence) && evidence.Count > 0)
            {
                var entries = new JsonObject();
                foreach (var (id, known) in evidence.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    var fields = new JsonObject();
                    if (known.Pinned) fields["pinned"] = true;
                    if (known.Group) fields["group"] = true;
                    entries[id] = fields;
                }
                entry["evidence"] = entries;
            }
            accounts[key] = entry;
        }
        var root = new JsonObject { ["version"] = 2, ["active"] = Active, ["accounts"] = accounts };
        var path = Path.Combine(ledgerDirectory, FileName);
        var partial = path + ".partial";
        File.WriteAllText(partial, root.ToJsonString(new JsonSerializerOptions
        { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
        File.Move(partial, path, overwrite: true);
    }

    private static JsonObject Write(Changes change)
    {
        var fields = new JsonObject();
        if (change.Pinned is { } pinned) fields["pinned"] = Write(pinned.Value, pinned.At, pinned.By);
        if (change.Group is { } group) fields["group"] = Write(group.Value, group.At, group.By);
        foreach (var (field, stamp) in new[] { ("title", change.Title), ("model", change.Model), ("effort", change.Effort), ("mode", change.Mode) })
            if (stamp is not null) fields[field] = Write(stamp.Value, stamp.At, stamp.By);
        return fields;
    }

    private static JsonObject Write(JsonNode? value, DateTimeOffset at, string? by)
    {
        var stamp = new JsonObject { ["value"] = value, ["at"] = Time(at) };
        if (by is not null) stamp["by"] = by;
        return stamp;
    }

    private static JsonObject Write(GroupChange change)
    {
        var renamed = new JsonObject();
        foreach (var (from, to) in change.Renamed.OrderBy(pair => pair.Key, StringComparer.Ordinal)) renamed[from] = to;
        var node = new JsonObject
        {
            ["order"] = new JsonArray(change.Order.Select(name => (JsonNode?)name).ToArray()), ["renamed"] = renamed,
            ["at"] = Time(change.At)
        };
        if (change.By is not null) node["by"] = change.By;
        return node;
    }

    private static string? Time(DateTimeOffset? at) => at?.ToString("O", CultureInfo.InvariantCulture);

    private static string? Text(JsonObject record, string field) =>
        record[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
}

/// <summary>
/// The Code sidebar's pins as the app shows them. The web UI keeps one pin list for every account in IndexedDB, under
/// the key <c>store:pin-state:dframe-starred-code</c> (a persisted store holding <c>{"state":{"starredIds":[…]}}</c>).
/// Session records carry an <c>isStarred</c> copy that does not follow it: the app pushes pins into records but leaves
/// stale ones behind, and an app update has rewritten records without their pins. Records are never read for pins.
/// </summary>
internal static class ClaudeSidebarPins
{
    internal const string StoreKey = "store:pin-state:dframe-starred-code";
    internal static readonly byte[] EncodedKey = EncodeStringKey(StoreKey);

    /// <summary>The pinned session ids. Throws unless exactly one complete, familiar pin list is found; a log the app
    /// is writing is retried.</summary>
    internal static IReadOnlySet<string> Read(string indexedDbDirectory)
    {
        if (!Directory.Exists(indexedDbDirectory)) throw new DirectoryNotFoundException("No app IndexedDB.");
        var store = new ClaudeLocalStorage(indexedDbDirectory);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var (entries, clean) = store.ReadMatching(IsPinListRow, whileOpen: true);
                if (!clean) throw new InvalidDataException("The app is writing its IndexedDB.");
                // Deleting a database deletes its rows, so a second live list is unexpected.
                if (entries is not [var entry])
                    throw new InvalidDataException(entries.Count == 0 ? "No pin list." : "More than one pin list.");
                return Parse(entry.Value);
            }
            catch (Exception exception) when (attempt < 2 && exception is IOException or InvalidDataException)
            { Thread.Sleep(300); }
        }
    }

    /// <summary>An object store's data row (index id 1) under the pin list's key. IndexedDB starts every key with the
    /// database, object store and index ids, each little-endian in as many bytes as the first byte gives.</summary>
    internal static bool IsPinListRow(byte[] key)
    {
        if (key.Length == 0) return false;
        int database = (key[0] >> 5) + 1, objectStore = ((key[0] >> 2) & 7) + 1, index = (key[0] & 3) + 1;
        var prefix = 1 + database + objectStore + index;
        if (key.Length != prefix + EncodedKey.Length) return false;
        ulong Id(int offset, int count)
        {
            ulong value = 0;
            for (var at = offset + count - 1; at >= offset; at--) value = value << 8 | key[at];
            return value;
        }
        // Database id 0 holds the store's own metadata.
        return Id(1, database) != 0 && Id(prefix - index, index) == 1 && key.AsSpan(prefix).SequenceEqual(EncodedKey);
    }

    /// <summary>The ids of a stored pin list: IndexedDB's record version, then Blink's envelope (with a trailer
    /// offset from version 21) around V8's, which holds the persisted store as one JSON string.</summary>
    internal static IReadOnlySet<string> Parse(byte[] value)
    {
        var position = 0;
        byte Next() => position < value.Length ? value[position++] : throw new InvalidDataException("Truncated pin list.");
        ulong Varint()
        {
            ulong result = 0;
            for (var shift = 0; shift < 64; shift += 7)
            {
                var part = Next();
                result |= (ulong)(part & 0x7f) << shift;
                if (part < 0x80) return result;
            }
            throw new InvalidDataException("Bad varint.");
        }
        Varint(); // record version
        if (Next() != 0xFF) throw new InvalidDataException("Not a serialized value.");
        Varint(); // Blink version
        if (position < value.Length && value[position] == 0xFE) position += 1 + 8 + 4; // trailer offset and size
        if (Next() != 0xFF) throw new InvalidDataException("Not a V8 value.");
        Varint(); // V8 version
        byte tag;
        while ((tag = Next()) == 0) { } // padding
        var length = Varint();
        if (length > (ulong)(value.Length - position)) throw new InvalidDataException("Truncated pin list.");
        var text = tag switch
        {
            (byte)'"' => Encoding.Latin1.GetString(value, position, (int)length),
            (byte)'c' => Encoding.Unicode.GetString(value, position, (int)length),
            _ => throw new InvalidDataException("The pin list is not a string.")
        };
        if (JsonNode.Parse(text) is not JsonObject root || root["state"] is not JsonObject state
            || state["starredIds"] is not JsonArray ids) throw new InvalidDataException("Unfamiliar pin list.");
        var pinned = new HashSet<string>(StringComparer.Ordinal);
        foreach (var id in ids)
            pinned.Add(id is JsonValue item && item.TryGetValue<string>(out var session) ? session
                : throw new InvalidDataException("Unfamiliar pin list."));
        return pinned;
    }

    /// <summary>An IndexedDB string key: type 1, its length in UTF-16 code units as a varint, then UTF-16BE.</summary>
    private static byte[] EncodeStringKey(string text)
    {
        var bytes = new List<byte> { 1 };
        var length = (ulong)text.Length;
        while (length >= 0x80) { bytes.Add((byte)(length | 0x80)); length >>= 7; }
        bytes.Add((byte)length);
        bytes.AddRange(Encoding.BigEndianUnicode.GetBytes(text));
        return bytes.ToArray();
    }
}

/// <summary>
/// Plans how to bring the signed-in account's Code sidebar up to date with the other accounts, as the app shows them:
/// the titles, pins and groups of the conversations the lists share, and the groups themselves in sidebar order. Value
/// by value it takes the newest change any account made (see <see cref="ClaudeSessionLayout"/>), so a sidebar follows
/// what was changed last, wherever it was changed; a value nobody changed stays. Everything is read from disk,
/// read-only, while the app runs: titles from each account's session records, pins from the web UI's pin list (see
/// <see cref="ClaudeSidebarPins"/>), groups, their order and folding from its <c>dframe-store</c> in localStorage
/// (<c>customGroupsByScope</c> and <c>codeSidebarByScope</c>, keyed by account/organization), and the changes from the
/// widget. A conversation is matched by its current transcript id, or as a continuation of the other side's.
/// <para>A conversation continued in another account can come back as a second record beside its older state. The plan
/// lists such older states for archiving (reversible); their changes count for the newest state. A conversation another
/// account archived after anyone last talked in it is listed for archiving here too. Nothing is deleted but groups left
/// empty; sessions only one side has are otherwise left alone.</para>
/// <para>A session the app nests under another (a side session under the session that started it, say) shows the group
/// of the session at the top of its family and moves only with it (see <see cref="Nests"/>). Its group is therefore that
/// session's: never a change of its own and never moved by itself. The widget's copies carry no such link, so elsewhere
/// the conversation is a session of its own, which follows what its account did to the family's group, not what a plan
/// brought to the top session there from another account.</para>
/// </summary>
internal static class ClaudeSessionSync
{
    internal const string CommandFlag = "--claude-session-plan";
    private const long MaxRecordBytes = 10 * 1024 * 1024;
    private const int TranscriptTailBytes = 4 * 1024 * 1024;
    private const long MaxTranscriptBytes = 512L * 1024 * 1024;
    // What a whole transcript is read through; it grows only for a longer line.
    private const int TranscriptChunkBytes = 1024 * 1024;
    private const int MaxSettleSeconds = 120;
    // How long a plan waits for the widget to checkpoint the account before it is made.
    private static readonly TimeSpan CheckpointWait = TimeSpan.FromSeconds(30);
    // An entry this close after the next transcript began is still the hand-over, not a branch.
    private static readonly TimeSpan HandOverSlack = TimeSpan.FromSeconds(2);
    private const string SidebarKey = "dframe-store";
    /// <summary>Models, efforts and permission modes are planned only for a conversation used this recently in any account,
    /// or pinned here: each one the app's tools set is a step the sync takes, and a raised mode asks the user.</summary>
    internal static readonly TimeSpan SettingsWindow = TimeSpan.FromDays(7);
    /// <summary>Marks a group on its way out while the plan puts the groups in order: the app adds a new group at the
    /// end, so a group out of place is made anew under its name, and the old one, renamed with this mark, is emptied and
    /// deleted, as is a group the list lacks. The plan in progress tells such groups by id; the mark tells them when it
    /// is not known.</summary>
    internal const string MovingMark = " (옮기는 중";
    private static readonly JsonSerializerOptions PlanJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true
    };

    /// <param name="Branch">A fork or a session spawned from another: a conversation of its own, never a newer state.</param>
    /// <param name="Renamed">The app recorded an earlier title (<c>previousTitles</c>): someone renamed it in this list.</param>
    /// <param name="Parent">The session its lineage links it to (see <see cref="ParentOf"/>), under which the app may nest it.</param>
    /// <param name="Detached">The user moved it to top level (<c>lineageDetached</c>).</param>
    /// <param name="Written">When its record was last written.</param>
    /// <param name="Former">The session its lineage links it to even once moved to top level: the family it left.</param>
    /// <param name="Model">Its model, effort and permission mode, each null when unknown and empty for none (as
    /// <c>ReadRecords</c> reads them).</param>
    /// <param name="Scheduled">A run a scheduled task started (<c>scheduledTaskId</c>) that the user has not gone on with
    /// (<c>scheduledRunContinued</c>).</param>
    internal sealed record Record(string SessionId, string? Title, bool Pinned, string? Cli, IReadOnlyList<string> Prior,
        long LastActivityAt, bool Branch = false, bool Renamed = false, string? Parent = null, bool Detached = false,
        DateTimeOffset? Written = null, string? Former = null, string? Model = null, string? Effort = null, string? Mode = null,
        bool Scheduled = false);
    internal sealed record Group(string Id, string Name, bool Collapsed = false);
    /// <param name="Groups">In sidebar order.</param>
    /// <param name="Nested">The sessions the app nests under another, each with the session at the top of its family, whose
    /// group it shows (see <see cref="Nests"/>); its own assignment is ignored.</param>
    internal sealed record Layout(IReadOnlyList<Group> Groups, IReadOnlyDictionary<string, string> Assignments,
        IReadOnlyDictionary<string, string>? Nested = null);
    /// <param name="GroupId">The group's id where it already stands; null for a group the plan makes (found by name
    /// once made) and for no group.</param>
    internal sealed record Move(string? Group, IReadOnlyList<string> Sessions, string? GroupId = null);
    /// <param name="By">The account whose change this is; null for this account's own (an older state's, say).</param>
    internal sealed record Pin(string Session, bool Pinned, string? Title, string? By = null);
    internal sealed record Rename(string Session, string Title, string? From, string? By = null);
    /// <summary>A model, effort or permission mode to set with the app's own tool. <c>From</c> is the value now, null for
    /// none; <c>By</c> the account the change or starting value came from, null for this account's own.</summary>
    internal sealed record SettingModel(string Session, string? Title, string Model, string? From, string? By = null);
    internal sealed record SettingEffort(string Session, string? Title, string Effort, string? From, string? By = null);
    /// <param name="Raises">The mode ranks above the one now (none as <c>default</c>): the app asks the user first.</param>
    /// <param name="Self">The calling session's own mode.</param>
    internal sealed record SettingMode(string Session, string? Title, string Mode, string? From, bool Raises, string? By = null,
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)] bool Self = false);
    /// <summary>The calling session's model and effort, which the app refuses a session to change for itself.</summary>
    internal sealed record SelfSetting(string? Model = null, string? ModelFrom = null, string? ModelBy = null,
        string? Effort = null, string? EffortFrom = null, string? EffortBy = null);
    /// <summary>A group renamed out of the way before its name is made anew, or before it is deleted.</summary>
    internal sealed record GroupRename(string Id, string From, string To);
    /// <summary>A group to delete once the moves have emptied it of every session a list here shows, by the name it then
    /// has: it goes once no more than <c>Hidden</c> sessions are left in it. <c>Remade</c>: its name is made anew
    /// elsewhere, where those sessions go; otherwise it is a group the list lacks. <c>Hidden</c> counts the sessions filed
    /// under it that no list here shows (archived, say): the app moves no archived session, so they leave the group,
    /// nothing else.</summary>
    internal sealed record GroupDelete(string Id, string Name, int Hidden = 0, bool Remade = false);
    /// <summary>A session to archive once <c>Ready</c>, of two kinds. An older state names the newest state that stays
    /// (<c>Newest</c>), which must first hold the pin and group it keeps (the plan's moves and pins); <c>LastWritten</c> is
    /// its last entry, <c>MovedOn</c> when the conversation went on in a new transcript. A conversation another account
    /// archived after anyone last talked in it (see <see cref="Gone"/>) names that account (<c>ArchivedIn</c>) and when
    /// (<c>ArchivedAt</c>); <c>LastWritten</c> is its last activity, and its older states here go with it. A pinned one is
    /// unpinned just before archiving, since the app does not archive pinned sessions.</summary>
    internal sealed record Retire(string Session, string? Title, bool Pinned, string? Newest, string? NewestTitle, bool Ready,
        DateTimeOffset LastWritten, DateTimeOffset? MovedOn, string? ArchivedIn = null, DateTimeOffset? ArchivedAt = null);
    /// <summary>Where and when a conversation was last archived in another account, and its last activity anywhere.</summary>
    internal sealed record Away(string Account, DateTimeOffset At, DateTimeOffset LastActive);
    internal sealed record GroupView(string Name, IReadOnlyList<string> Sessions, bool Collapsed = false);
    /// <summary>A session nested under another, by title: it shows that one's group and moves only with it.</summary>
    internal sealed record NestedView(string Title, string Under);
    /// <summary>An account's sidebar as the app shows it, by title: the pinned sessions, then each group's others, in
    /// sidebar order, a nested session in the group of its family. A null list could not be read. <c>Nested</c> lists
    /// the nested sessions; null when there are none.</summary>
    internal sealed record Sidebar(string Account, string? Label, IReadOnlyList<string>? Pinned, IReadOnlyList<GroupView>? Groups,
        IReadOnlyList<NestedView>? Nested = null);
    /// <summary>When a transcript file was created and last written, and its newest turn (null when it has none; see
    /// <c>TryEntry</c>). A file created after it was last written was copied or restored, so its creation time dates
    /// nothing.</summary>
    internal sealed record TranscriptSpan(DateTimeOffset Created, DateTimeOffset Written, DateTimeOffset? LastEntry)
    {
        internal bool Dated => Created <= Written + HandOverSlack;
    }
    /// <summary>What decides whether a record may go as an older state: the calling session stays, <c>Transcript</c>
    /// finds a transcript's span by id (null when it is missing), and <c>HandOver</c> dates when a conversation went on
    /// from one transcript in the next (null when it cannot).</summary>
    internal sealed record Lineage(string? Self, Func<string, TranscriptSpan?> Transcript,
        Func<string, string, DateTimeOffset?> HandOver);
    /// <summary>The newest state of an older state's conversation, and the times showing nothing was written to the
    /// older state after the conversation moved on.</summary>
    internal sealed record Older(Record Newest, DateTimeOffset LastWritten, DateTimeOffset MovedOn);

    internal sealed record Sources(string SessionsRoot, string LocalStorage, string IndexedDb, string ProjectsRoot, string Ledger)
    {
        internal static Sources Default() => For(ClaudeShelfPaths.CreateDefault());

        /// <summary>The app's own stores beside the given lists and ledger.</summary>
        internal static Sources For(ClaudeShelfPaths paths)
        {
            var app = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
            return new(paths.SessionsRoot, Path.Combine(app, "Local Storage", "leveldb"),
                Path.Combine(app, "IndexedDB", "https_claude.ai_0.indexeddb.leveldb"), paths.ProjectsRoot, paths.LedgerDirectory);
        }
    }

    /// <summary>Another account: its list and groups (null when they cannot be confirmed), when the app last left it, and
    /// the widget's copies in its list with the title last written to each (null when the ledger cannot be read).</summary>
    /// <param name="Writes">The records of its list the widget wrote, each with when it last did (see
    /// <see cref="LedgerWrites"/>).</param>
    internal sealed record Other(string Account, string? Label, DateTimeOffset? LeftAt, IReadOnlyList<Record> Records,
        Layout? Layout, IReadOnlyDictionary<string, string?>? CopyTitles, IReadOnlyList<Record>? Archived = null,
        IReadOnlyDictionary<string, DateTimeOffset>? Writes = null);

    /// <summary>An account the plan took changes from: how many conversations here it lists, and the titles of those
    /// only it lists.</summary>
    internal sealed record OtherView(string Account, string? Label, DateTimeOffset? LeftAt, int Matched, IReadOnlyList<string> OnlyThere);

    /// <summary>Status is <c>ok</c> when a plan was made (possibly empty); otherwise it says why nothing was planned.
    /// <c>GroupOrder</c> is the group list the plan puts in place and <c>GroupOrderBy</c> the account whose change it
    /// follows (none for this one's own), both only when the plan changes the groups. <c>OnlyHere</c> are the titles of
    /// conversations no other account lists. <c>Checkpointed</c> says whether the widget recorded this account first.
    /// <c>Pending</c> is what the plan sets out to change, for the widget. <c>Models</c>, <c>Efforts</c> and
    /// <c>Permissions</c> are the conversations used within <see cref="SettingsWindow"/> or pinned here whose values it
    /// changes, the most recently used first; <c>SelfSettings</c> the calling session's model and effort, and
    /// <c>SettingsBeyond</c> how many conversations out of that scope it would have changed.</summary>
    internal sealed record Plan(string Status, string? Current = null, string? CurrentLabel = null,
        IReadOnlyList<OtherView>? Others = null, IReadOnlyList<Retire>? Archive = null,
        IReadOnlyList<GroupRename>? RenameGroups = null, IReadOnlyList<string>? CreateGroups = null,
        IReadOnlyList<Move>? Moves = null, IReadOnlyList<Pin>? Pins = null, IReadOnlyList<Rename>? Renames = null,
        IReadOnlyList<GroupDelete>? DeleteGroups = null, IReadOnlyList<string>? GroupOrder = null, string? GroupOrderBy = null,
        IReadOnlyList<string>? OnlyHere = null, IReadOnlyList<Sidebar>? Sidebars = null, IReadOnlyList<string>? Warnings = null,
        bool? Checkpointed = null, [property: JsonIgnore] ClaudeSessionLayout.Pending? Pending = null,
        IReadOnlyList<SettingModel>? Models = null, IReadOnlyList<SettingEffort>? Efforts = null,
        IReadOnlyList<SettingMode>? Permissions = null, SelfSetting? SelfSettings = null, int? SettingsBeyond = null);

    /// <summary>What a plan reads, once, so a checkpoint and the plan made from it see the same: the pin list, the sidebar
    /// state, and each account's list.</summary>
    internal sealed record Snapshot(IReadOnlySet<string>? Pinned, JsonObject? Sidebar, IReadOnlyDictionary<string, Listing> Lists);

    /// <param name="Whole">False when a record there could not be read now.</param>
    /// <param name="CopyTitles">The widget's copies there with the title written to each; null when the ledger cannot be
    /// read.</param>
    /// <param name="Writes">The records there the widget wrote, with when it last did; null when the ledger cannot be read.</param>
    internal sealed record Listing(string Directory, IReadOnlyList<Record> Records, bool Whole, IReadOnlyList<Record> Archived,
        IReadOnlyDictionary<string, string?>? CopyTitles, IReadOnlyDictionary<string, DateTimeOffset>? Writes = null);

    /// <summary>Reads what a plan needs, the given accounts' lists included (an account without a list here has none).</summary>
    internal static Snapshot Read(Sources sources, IEnumerable<string> accounts)
    {
        var pinned = TryReadPins(sources.IndexedDb);
        var sidebar = TryReadSidebar(sources.LocalStorage);
        var lists = new Dictionary<string, Listing>(StringComparer.Ordinal);
        foreach (var account in accounts.Distinct(StringComparer.Ordinal))
            if (AccountDirectory(sources.SessionsRoot, account) is { } directory)
            {
                var (copyTitles, writes) = ReadLedger(sources.Ledger, account);
                lists[account] = new(directory, ReadRecords(directory, pinned, out var whole, out var archived), whole, archived,
                    copyTitles, writes);
            }
        return new(pinned, sidebar, lists);
    }

    /// <summary>An account's sidebar as read now: its records (pins from the pin list), its groups (null when unknown),
    /// whether its pins count, and the widget's copies there with the title written to each; its nested sessions when its
    /// groups do not say (see <see cref="Layout.Nested"/>).</summary>
    /// <param name="Writes">The records there the widget wrote, with when it last did (see <see cref="LedgerWrites"/>); null
    /// when not given.</param>
    internal sealed record State(IReadOnlyList<Record> Records, Layout? Layout, bool PinsCount,
        IReadOnlyDictionary<string, string?> CopyTitles, IReadOnlyDictionary<string, string>? Nests = null,
        IReadOnlyDictionary<string, DateTimeOffset>? Writes = null);

    internal static bool IsAccountKey(string? key) =>
        key?.Split('/') is [var account, var org] && Guid.TryParse(account, out _) && Guid.TryParse(org, out _);

    /// <summary><c>--claude-session-plan --session local_… [--only account/org] [--settle seconds]</c>: prints the plan as
    /// UTF-8 JSON. <c>--settle</c> waits first, for checking changes the app has not written to disk yet. <c>--only</c>
    /// takes changes from that account alone, for diagnosis, and tells the widget nothing.</summary>
    internal static int RunCommand(string[] args)
    {
        string? session = null, only = null;
        var settle = 0;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--session" && index + 1 < args.Length) session = args[++index];
            else if (args[index] == "--only" && index + 1 < args.Length) only = args[++index];
            else if (args[index] == "--settle" && index + 1 < args.Length
                     && int.TryParse(args[++index], NumberStyles.None, CultureInfo.InvariantCulture, out settle)
                     && settle <= MaxSettleSeconds) { }
            else return Print(new Plan("bad_arguments"), 2);
        }
        if (session is null || !session.StartsWith("local_", StringComparison.Ordinal)
            || session.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || only is not null && !IsAccountKey(only))
            return Print(new Plan("bad_arguments"), 2);
        // Pins reach the disk within a second of a change; groups within seconds, up to about a minute when the app
        // writes its local storage often.
        if (settle > 0) Thread.Sleep(TimeSpan.FromSeconds(settle));
        var sources = Sources.Default();
        // The widget makes the plan to apply: it first records what this account shows, with what the plan in progress
        // has done of what it set out to, and keeps what this plan sets out to change. One made here without it is for
        // viewing only. A plan limited to one account is for diagnosis and asks the widget nothing.
        if (only is null && FindAccount(sources.SessionsRoot, session) is { Key: var key } && Ask(key, session) is { } answer)
            return Print(answer, 0);
        var plan = Build(sources, session, only?.ToLowerInvariant());
        return Print(only is null && plan.Status == "ok" ? plan with { Checkpointed = false } : plan, 0);
    }

    private static string Suffix(string accountKey) => accountKey.ToLowerInvariant().Replace('/', '_');

    /// <summary>The widget's event for a plan asked for in an account (see <see cref="Ask"/>).</summary>
    internal static string PlannedEventName(string accountKey) => @"Local\CodexWeeklyUsageIndicator.SessionsPlanned." + Suffix(accountKey);

    /// <summary>The widget's event once it has answered.</summary>
    internal static string CheckpointedEventName(string accountKey) =>
        @"Local\CodexWeeklyUsageIndicator.SessionsCheckpointed." + Suffix(accountKey);

    /// <summary>The widget's shared memory for one account's plan requests: a request at the start, the answer at
    /// <see cref="AnswerOffset"/>, each a length and UTF-8 text. Named kernel objects reach the widget from a process
    /// the app runs, where files it writes under the local app data could be virtualized.</summary>
    internal static string MailboxName(string accountKey) => @"Local\CodexWeeklyUsageIndicator.SessionsPlanMailbox." + Suffix(accountKey);

    /// <summary>Lets one plan request at a time use an account's mailbox.</summary>
    internal static string PlanLockName(string accountKey) => @"Local\CodexWeeklyUsageIndicator.SessionsPlanLock." + Suffix(accountKey);

    internal const long MailboxBytes = 16 * 1024 * 1024;
    internal const int AnswerOffset = 64 * 1024;

    /// <summary>Asks the running widget for the plan: the request carries an id and the calling session, the answer the
    /// same id and the plan as the widget made it. An answer to an earlier request that ran out of time is passed over.
    /// Null without a widget, or without an answer in time.</summary>
    private static string? Ask(string account, string self)
    {
        try
        {
            using var gate = new Mutex(false, PlanLockName(account));
            bool owned;
            try { owned = gate.WaitOne(CheckpointWait); }
            catch (AbandonedMutexException) { owned = true; }
            if (!owned) return null;
            try
            {
                if (!EventWaitHandle.TryOpenExisting(PlannedEventName(account), out var planned)) return null;
                using (planned)
                {
                    if (!EventWaitHandle.TryOpenExisting(CheckpointedEventName(account), out var answered)) return null;
                    using (answered)
                    {
                        using var mailbox = MemoryMappedFile.OpenExisting(MailboxName(account), MemoryMappedFileRights.ReadWrite);
                        using var view = mailbox.CreateViewAccessor(0, MailboxBytes);
                        var id = Guid.NewGuid().ToString("N");
                        WriteSlot(view, 0, AnswerOffset, JsonSerializer.SerializeToUtf8Bytes(new JsonObject { ["id"] = id, ["self"] = self }));
                        answered.Reset();
                        planned.Set();
                        var deadline = DateTime.UtcNow + CheckpointWait;
                        while (deadline - DateTime.UtcNow is { Ticks: > 0 } left && answered.WaitOne(left))
                            if (ReadSlot(view, AnswerOffset, MailboxBytes - AnswerOffset) is { } text
                                && text.StartsWith(id + "\n", StringComparison.Ordinal))
                                return text[(id.Length + 1)..];
                        return null;
                    }
                }
            }
            finally { gate.ReleaseMutex(); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException
                                              or WaitHandleCannotBeOpenedException) { return null; }
    }

    /// <summary>Writes one length-prefixed text into a mailbox slot.</summary>
    internal static void WriteSlot(MemoryMappedViewAccessor view, long offset, long capacity, byte[] text)
    {
        if (text.Length + 4 > capacity) throw new IOException("Too large for the mailbox.");
        view.WriteArray(offset + 4, text, 0, text.Length);
        view.Write(offset, text.Length);
    }

    /// <summary>Reads one length-prefixed text from a mailbox slot; null when it holds none.</summary>
    internal static string? ReadSlot(MemoryMappedViewAccessor view, long offset, long capacity)
    {
        var length = view.ReadInt32(offset);
        if (length <= 0 || length + 4L > capacity) return null;
        var text = new byte[length];
        view.ReadArray(offset + 4, text, 0, length);
        return Encoding.UTF8.GetString(text);
    }

    /// <summary>The plan as the command prints it.</summary>
    internal static string Serialize(Plan plan) => JsonSerializer.Serialize(plan, PlanJson);

    internal static Plan Build(Sources sources, string sessionId, string? only = null)
    {
        var current = FindAccount(sources.SessionsRoot, sessionId);
        if (current is null) return new("unknown_session");
        var key = current.Value.Key;
        // What each account changed is the widget's to keep; before it has started there is nothing to tell a change by.
        var (kept, exists) = ClaudeSessionLayout.Load(sources.Ledger);
        if (!exists) return new("widget_not_ready", key);
        if (kept is null) return new("layout_unreadable", key);
        return BuildFor(sources, key, kept, sessionId, Read(sources, kept.Accounts.Keys.Prepend(key)), only);
    }

    /// <summary>The plan for one account against a kept layout, from one snapshot; the widget makes it, to keep what a
    /// plan sets out to change. <paramref name="self"/> is the calling session, which never goes as an older state.</summary>
    internal static Plan BuildFor(Sources sources, string key, ClaudeSessionLayout kept, string? self, Snapshot snapshot,
        string? only = null, DateTimeOffset? now = null)
    {
        if (!snapshot.Lists.TryGetValue(key, out var mine)) return new("unknown_session");
        string? Label(string account) => kept.Accounts.TryGetValue(account, out var known) ? known.Label : null;
        var warnings = new List<string>();
        var sidebar = snapshot.Sidebar;
        // This account having no scope yet only means it has no groups, and planning for it only adds.
        var currentLayout = LayoutOf(sidebar, key, missingIsEmpty: true);
        if (currentLayout is null) warnings.Add("groups_unreadable");
        var pinned = snapshot.Pinned;
        if (pinned is null) warnings.Add("pins_unreadable");
        var lineage = new Lineage(self, cli => Transcript(sources.ProjectsRoot, cli),
            (older, next) => HandOver(sources.ProjectsRoot, older, next));
        var others = new List<Other>();
        var groupsUnread = false;
        var listsWhole = mine.Whole;
        foreach (var account in kept.Accounts.Keys.Where(account => account != key && (only is null || account == only))
                     .Order(StringComparer.Ordinal))
        {
            // An account that never used the app here has no list to take changes from.
            if (!snapshot.Lists.TryGetValue(account, out var theirs)) continue;
            listsWhole &= theirs.Whole;
            // Another account's groups must be confirmed: read as empty, they would look like sessions taken out of
            // groups. One that never had a group has no scope and changed none; one whose scope cannot be read holds
            // every group change, since it may hold the newest.
            var layout = LayoutOf(sidebar, account, missingIsEmpty: false);
            if (layout is null && HasScope(sidebar, account)) groupsUnread = true;
            others.Add(new(account, Label(account), kept.Accounts[account].LeftAt, theirs.Records, layout, theirs.CopyTitles,
                theirs.Archived, theirs.Writes));
        }
        if (others.Count == 0) return new("no_other_accounts", key, Label(key));
        if (groupsUnread && currentLayout is not null) warnings.Add("groups_unreadable");
        if (mine.CopyTitles is null || others.Any(other => other.CopyTitles is null)) warnings.Add("ledger_unreadable");
        // A record that cannot be read now would look gone: here its group would look empty and be deleted, and anywhere
        // an older change could pass for the newest. Nothing is decided until every list reads whole.
        if (!listsWhole) warnings.Add("list_incomplete");
        return Compute(key, Label(key), mine.Records, currentLayout, others, kept, pinned is not null, lineage, warnings,
            groupsKnown: !groupsUnread, currentCopyTitles: mine.CopyTitles, pinsLost: PinsLost(pinned, kept),
            archived: mine.Archived, listsWhole: listsWhole, currentWrites: mine.Writes, now: now);
    }

    /// <summary>The app's pin list, one for every account, holds no pin at all where a checkpoint showed some: it was
    /// lost (an app update has wiped it) rather than emptied pin by pin. Emptied by hand in every account it looks the
    /// same; that cannot be told apart.</summary>
    private static bool PinsLost(IReadOnlySet<string>? pinned, ClaudeSessionLayout kept) =>
        pinned is { Count: 0 } && kept.Checkpoints.Values.Any(checkpoint => checkpoint.Sessions.Values.Any(shown => shown.Pinned == true));

    /// <summary>Records the widget copied into an account's list, by session id, from its ledger. No ledger means no
    /// copies; one that cannot be read whole is null.</summary>
    internal static IReadOnlySet<string>? Copies(string ledgerDirectory, string account) =>
        CopyTitles(ledgerDirectory, account)?.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>The same copies with the title the widget last wrote to each (null when the ledger does not track it).
    /// The app's rename history cannot tell a renamed copy: it records neither a first name nor a change of case or
    /// spacing, so only a title exactly as written proves it untouched.</summary>
    internal static IReadOnlyDictionary<string, string?>? CopyTitles(string ledgerDirectory, string account) =>
        ReadLedger(ledgerDirectory, account).CopyTitles;

    /// <summary>The records the widget wrote in an account's list, by session id, with when it last wrote each: a copy it
    /// made (not archived) when it made it, a record it advanced when that advance was done. An advance gives the record
    /// the source's activity time, earlier than the advance, so only activity after the write is the account's own.</summary>
    internal static IReadOnlyDictionary<string, DateTimeOffset>? LedgerWrites(string ledgerDirectory, string account) =>
        ReadLedger(ledgerDirectory, account).Writes;

    /// <summary>The copies with their titles (see <see cref="CopyTitles"/>) and the widget's writes (see
    /// <see cref="LedgerWrites"/>) from one read of the ledger; both null when it cannot be read whole.</summary>
    private static (IReadOnlyDictionary<string, string?>? CopyTitles, IReadOnlyDictionary<string, DateTimeOffset>? Writes) ReadLedger(
        string ledgerDirectory, string account)
    {
        var path = Path.Combine(ledgerDirectory, "ledger.json");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (JsonNode.Parse(stream) is not JsonObject { } root || root["created"] is not JsonArray created) return (null, null);
            var copies = new Dictionary<string, string?>(StringComparer.Ordinal);
            var writes = new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal);
            void Wrote(string id, DateTimeOffset at) { if (!writes.TryGetValue(id, out var known) || at > known) writes[id] = at; }
            static bool IsRecordFile(string file) =>
                file.StartsWith("local_", StringComparison.Ordinal) && file.EndsWith(".json", StringComparison.Ordinal);
            foreach (var item in created)
            {
                if (item is not JsonObject entry || Text(entry, "dst") is not { } target || Text(entry, "file") is not { } file
                    || !IsRecordFile(file)) return (null, null);
                if (!string.Equals(target, account, StringComparison.OrdinalIgnoreCase) || entry["retiredAt"] is not null) continue;
                var id = file[..^".json".Length];
                copies[id] = Text(entry, "title");
                Wrote(id, WriteTime(Text(entry, "createdAt")));
            }
            if (root["advanced"] is { } advancedNode)
            {
                if (advancedNode is not JsonArray advanced) return (null, null);
                foreach (var item in advanced)
                {
                    if (item is not JsonObject entry || Text(entry, "dst") is not { } target || Text(entry, "file") is not { } file
                        || !IsRecordFile(file) || Text(entry, "state") is not { } state) return (null, null);
                    if (!string.Equals(target, account, StringComparison.OrdinalIgnoreCase)
                        || !string.Equals(state, "done", StringComparison.OrdinalIgnoreCase)) continue;
                    Wrote(file[..^".json".Length], WriteTime(Text(entry, "at")));
                }
            }
            return (copies, writes);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        { return (new Dictionary<string, string?>(StringComparer.Ordinal), new Dictionary<string, DateTimeOffset>(StringComparer.Ordinal)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { return (null, null); }
    }

    /// <summary>When the ledger says the widget wrote: one it does not date is taken as written after any activity, so
    /// the record owns nothing.</summary>
    private static DateTimeOffset WriteTime(string? text) =>
        DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var at) ? at : DateTimeOffset.MaxValue;

    /// <summary>
    /// The changes, in the order they must run: rename the groups on their way out, make the groups the list needs in
    /// its order, move sessions (moving a pinned session into a group unpins it, so pins are planned after the moves),
    /// set pins, rename sessions, delete the groups the moves emptied, and only then archive what is ready: older states,
    /// and conversations another account archived after anyone last talked in them, which the plan otherwise leaves
    /// alone. Value by value a conversation here takes the newest change any account made to it — this account's own
    /// included, and above all what it changed in this stay; a value nobody changed stays. The group list follows the
    /// newest change likewise and is final: the longest start of it already in order stays, the rest is made anew at the
    /// end, a group it lacks goes, and a session filed under a group the list lacks follows that group's renaming or
    /// leaves for no group — unless this stay filed it there, which keeps the group. Groups are planned only when the
    /// pins, every account's groups and this list could be read whole; pins only when the pins could be read; nothing
    /// without the widget's copies. An older state is ready when its newest state already holds what it keeps, so
    /// stopping part way never loses a pin or a group. A conversation's model, effort and permission mode follow the newest
    /// change likewise, or where nobody recorded one the explicit value of the account that last really used it; only for
    /// a conversation used within <see cref="SettingsWindow"/> or pinned here, and only as items the app's own tools apply:
    /// the widget never writes them into a record.
    /// </summary>
    /// <param name="groupsKnown">False when another account's groups are there but cannot be read (that account may hold
    /// the newest) or this list could not be read whole: then no group changes.</param>
    /// <param name="currentCopyTitles">The widget's copies in this account's list, with the titles it wrote. The ledger
    /// that tells them is the one that tells the other accounts' copies.</param>
    /// <param name="pinsLost">The pin list was lost: no pin counts as a change, and the newest changes bring them back.</param>
    /// <param name="currentWrites">The records here the widget wrote, with when it last did (see <see cref="LedgerWrites"/>).</param>
    /// <param name="now">What "used within <see cref="SettingsWindow"/>" counts from; the current time when not given.</param>
    internal static Plan Compute(string current, string? currentLabel, IReadOnlyList<Record> currentRecords,
        Layout? currentLayout, IReadOnlyList<Other> others, ClaudeSessionLayout kept, bool pinsKnown = true,
        Lineage? lineage = null, IReadOnlyList<string>? warnings = null, bool groupsKnown = true,
        IReadOnlyDictionary<string, string?>? currentCopyTitles = null, bool pinsLost = false,
        IReadOnlyList<Record>? archived = null, bool listsWhole = true,
        IReadOnlyDictionary<string, DateTimeOffset>? currentWrites = null, DateTimeOffset? now = null)
    {
        var notes = warnings?.ToList() ?? [];
        var usedSince = ((now ?? DateTimeOffset.UtcNow) - SettingsWindow).ToUnixTimeMilliseconds();
        string? Label(string? account) => account is null ? null
            : account == current ? currentLabel : kept.Accounts.TryGetValue(account, out var known) ? known.Label : null;
        // A nested session shows the group of the session at the top of its family and moves only with it.
        var nests = Nests(currentRecords, archived);
        if (currentLayout is not null) currentLayout = currentLayout with { Nested = nests };
        // This account's older states are archived and the others' are seen through: either way what was changed on
        // them counts for the newest state of the conversation, as does what was changed on an older state already
        // archived — found again here, so an archived branch never counts for another conversation.
        var older = lineage is null ? new Dictionary<string, Older>(StringComparer.Ordinal) : OlderStates(currentRecords, lineage, archived);
        var byId = currentRecords.ToDictionary(record => record.SessionId, StringComparer.Ordinal);
        var retiring = older.Where(pair => byId.ContainsKey(pair.Key)).ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        var activity = currentRecords.Concat(archived ?? []).ToDictionary(record => record.SessionId, record => record.LastActivityAt, StringComparer.Ordinal);
        var ownOlderOf = older.OrderByDescending(pair => activity.GetValueOrDefault(pair.Key))
            .ToLookup(pair => pair.Value.Newest.SessionId, pair => (Id: pair.Key, pair.Value.MovedOn), StringComparer.Ordinal);
        var newest = currentRecords.Where(record => !retiring.ContainsKey(record.SessionId))
            .OrderByDescending(record => record.LastActivityAt).ToArray();
        // Without knowing which records are the widget's copies, a copy would be taken for a choice: only older states
        // are cleared then.
        var copiesKnown = currentCopyTitles is not null && others.All(other => other.CopyTitles is not null);
        // A list that could not be read whole decides nothing either.
        var decidable = copiesKnown && listsWhole;
        var pinPlan = pinsKnown && decidable;
        // Moves can unpin, so groups wait for readable pins too.
        var groups = pinPlan && currentLayout is not null && groupsKnown;
        var pinsCount = pinsKnown && !pinsLost;
        if (pinsKnown && pinsLost) notes.Add("pins_lost");
        var me = kept.Accounts.GetValueOrDefault(current);
        // This stay began when the app last came to this account (or, not known, after it last left it).
        var stayStart = me?.ArrivedAt ?? me?.LeftAt ?? DateTimeOffset.MinValue;
        var checkpoint = kept.Checkpoints.GetValueOrDefault(current);
        var thisStay = checkpoint is not null && checkpoint.Stay == (me?.Stay ?? 0);
        // What this account changed since its checkpoint: in this stay its newest choice of all, which stays; found
        // against a checkpoint of an earlier stay, or without one, it was changed by the time the app last left it.
        // What the plan in progress has not reached yet, or what its moves did on the way, is nobody's choice.
        var since = thisStay ? DateTimeOffset.MaxValue : me?.LeftAt ?? DateTimeOffset.MinValue;
        var observed = copiesKnown
            ? Observe(new(currentRecords, currentLayout, pinsCount, currentCopyTitles!, nests, currentWrites), checkpoint, since,
                kept.Pendings.GetValueOrDefault(current), kept.Evidences.GetValueOrDefault(current),
                // As the widget's checkpoint looks, so a plan made for viewing shows the same.
                writtenSince: thisStay ? me?.ArrivedAt : null)
            : null;
        var mine = Merge(kept.Changed.GetValueOrDefault(current), observed?.Changes ?? NoChanges());
        // The others ranked by when the app last left them, the latest first: the rank settles changes dated alike.
        var seen = others.OrderByDescending(other => other.LeftAt ?? DateTimeOffset.MinValue)
            .ThenBy(other => other.Account, StringComparer.Ordinal)
            .Select((other, index) => Seen.Of(other, index + 1, kept, newest, lineage, pinsCount, copiesKnown))
            .ToArray();
        // A conversation archived elsewhere after anyone last talked in it goes, with its older states here; the rest is
        // planned. A list not read whole could hide where it is still live, so then none goes.
        var gone = decidable ? Gone(newest, seen, lineage?.Self, currentRecords) : new Dictionary<string, Away>(StringComparer.Ordinal);
        var staying = newest.Where(record => !gone.ContainsKey(record.SessionId)).ToArray();
        var leaving = gone.Keys.Concat(retiring.Where(pair => gone.ContainsKey(pair.Value.Newest.SessionId)).Select(pair => pair.Key))
            .ToHashSet(StringComparer.Ordinal);
        // Groups the plan in progress here renamed out of the way, by id, with the names they stand for.
        var renamedHere = kept.Pendings.GetValueOrDefault(current)?.Renamed ?? new Dictionary<string, ClaudeSessionLayout.Moved>();
        var renames = new List<Rename>();
        // What each session here should end with: its pin and the change it comes from, and its group (Known false
        // when it cannot be resolved) with the change it comes from.
        var targets = new Dictionary<string, (Offer<bool> Pin, (bool Known, string? Name) Group, Offer<string?>? GroupFrom)>(StringComparer.Ordinal);
        var titleFrom = new Dictionary<string, Offer<string>>(StringComparer.Ordinal);
        // The model, effort and mode each session here should have, with the change or starting value each comes from,
        // its latest activity in any account, and whether it is in scope (used within the window, or pinned here).
        var settingsFrom = new Dictionary<string, (Offer<string>? Model, Offer<string>? Effort, Offer<string>? Mode, long Used, bool InScope)>(
            StringComparer.Ordinal);
        // Groups this stay filed sessions under: the group list keeps them.
        var filedHere = new HashSet<string>(StringComparer.Ordinal);
        var onlyHere = new List<Record>();
        foreach (var record in staying)
        {
            var found = seen.Where(side => side.Matches.ContainsKey(record.SessionId)).ToArray();
            if (found.Length == 0) onlyHere.Add(record);
            // Every change made to the conversation: this account's, its older states' and archived records' included,
            // then each other account's.
            var offers = new List<(ClaudeSessionLayout.Changes Change, int Rank, string Account)>();
            foreach (var change in Fold(record.SessionId, ownOlderOf[record.SessionId], mine)) offers.Add((change, 0, current));
            foreach (var side in found)
                foreach (var change in side.Fold(side.Matches[record.SessionId]))
                    offers.Add((change, side.Rank, side.Other.Account));
            if (decidable && Newest(offers, change => change.Title) is { } title && !string.IsNullOrWhiteSpace(title.Value)
                && title.Value != record.Title)
            {
                renames.Add(new(record.SessionId, title.Value, record.Title, Label(title.By == current ? null : title.By)));
                titleFrom[record.SessionId] = title;
            }
            var pin = pinPlan ? Newest(offers, change => change.Pinned) : null;
            // A nested session keeps the group of its family: nothing here moves it.
            var group = groups && !nests.ContainsKey(record.SessionId) ? Newest(offers, change => change.Group) : null;
            // Filed by hand in this stay: that group stays even where the list lacks it.
            if (group is { Value: { } filedUnder, By: var filer, At: var filedAt } && filer == current
                && (filedAt == DateTimeOffset.MaxValue || filedAt > stayStart)) filedHere.Add(filedUnder);
            var groupNow = currentLayout is null ? (Known: false, Name: (string?)null) : GroupNameOf(currentLayout, record.SessionId, renamedHere);
            targets[record.SessionId] = (pin ?? new(record.Pinned, DateTimeOffset.MinValue, null),
                group is { } chosen ? (true, chosen.Value) : groupNow, group);
            // A scheduled run keeps what its task set: nobody chose that for the session, and nobody goes back to such a run.
            if (!decidable || record.Scheduled || found.Any(side => side.Matches[record.SessionId].Scheduled)) continue;
            // A model, effort and mode follow the newest change too; where nobody recorded one, the account that last
            // really used the conversation gives its explicit value. Own activity is what a record has after the widget
            // last wrote it: an advance or a copy carries the source's activity time.
            var owner = found.Select(side => (Record: side.Matches[record.SessionId], side.Rank, side.Other.Account, side.Other.Writes))
                .Prepend((Record: record, Rank: 0, Account: current, Writes: currentWrites))
                .Select(candidate => (candidate.Record, candidate.Rank, candidate.Account, Active: OwnActivity(candidate.Record, candidate.Writes)))
                .Where(candidate => candidate.Active is not null)
                .OrderByDescending(candidate => candidate.Active).ThenBy(candidate => candidate.Rank).FirstOrDefault();
            Offer<string>? Starting(string? value, bool mode = false) =>
                owner.Record is null || owner.Account == current || value is not { Length: > 0 } || mode && value == DefaultMode ? null
                : new(value, owner.Active!.Value, owner.Account);
            var used = found.Select(side => side.Matches[record.SessionId].LastActivityAt).Append(record.LastActivityAt).Max();
            settingsFrom[record.SessionId] = (Newest(offers, change => change.Model) ?? Starting(owner.Record?.Model),
                Newest(offers, change => change.Effort) ?? Starting(owner.Record?.Effort),
                Newest(offers, change => change.Mode) ?? Starting(owner.Record?.Mode, mode: true),
                used, used >= usedSince || pinsKnown && record.Pinned);
        }
        // The group list follows the newest change too: this account's own in this stay first, else the newest any
        // account made. A list mid-move is nobody's choice; with no change at all the groups here stand as they are.
        var here = currentLayout?.Groups ?? [];
        // A group the plan in progress renamed out of the way, while it bears the name given, or one showing the mark, is
        // aside: it stands for the name it had. One renamed otherwise since is the user's.
        bool Aside(Group group) => Standing(group, renamedHere) is not null;
        string Stands(Group group) => Standing(group, renamedHere) ?? group.Name;
        var order = here.Where(group => !Aside(group)).Select(group => group.Name).Distinct(StringComparer.Ordinal).ToList();
        ClaudeSessionLayout.GroupChange? orderFrom = null;
        string? orderBy = null;
        var renamings = new Dictionary<string, string>(StringComparer.Ordinal);
        var renameGroups = new List<GroupRename>();
        var create = new List<string>();
        var moves = new List<Move>();
        var deleteGroups = new List<GroupDelete>();
        // Sessions the plan moves, where to, and those it moves into a group (which unpins them).
        var moved = new HashSet<string>(StringComparer.Ordinal);
        var placed = new Dictionary<string, string?>(StringComparer.Ordinal);
        var filed = new HashSet<string>(StringComparer.Ordinal);
        // Sessions the plan archives that leave a group it deletes.
        var dropped = new HashSet<string>(StringComparer.Ordinal);
        var unresolved = false;
        if (groups)
        {
            var listed = new List<(ClaudeSessionLayout.GroupChange Change, int Rank, string Account)>();
            if (observed?.Groups is { } changed) listed.Add((changed, 0, current));
            if (kept.GroupsChanged.GetValueOrDefault(current) is { } stored) listed.Add((stored, 0, current));
            foreach (var side in seen)
                if (side.GroupsChanged is { } theirs) listed.Add((theirs, side.Rank, side.Other.Account));
            var usable = listed.Where(item => !item.Change.Order.Any(IsMoving)).ToArray();
            // Groups renamed by hand anywhere, this account's own in this stay the newest.
            var renamed = (kept.Renamings.GetValueOrDefault(current) ?? [])
                .Concat((observed?.Renamings ?? []).Select(renaming => renaming with { At = since }))
                .Concat(seen.SelectMany(side => side.Renamings)).OrderBy(renaming => renaming.At).ToArray();
            // A group renamed anywhere passes what was filed under its old name on, the later renaming over the earlier.
            foreach (var (change, _, _) in usable.OrderBy(item => item.Change.At))
                foreach (var (from, to) in change.Renamed) renamings[from] = to;
            foreach (var renaming in renamed.Where(renaming => renaming is { From: not null, To: not null }))
                renamings[renaming.From!] = renaming.To!;
            var listedAt = DateTimeOffset.MinValue;
            if (usable.OrderByDescending(item => item.Change.At).ThenBy(item => item.Rank).FirstOrDefault() is { Change: { } best } chosen)
            {
                order = best.Order.Distinct(StringComparer.Ordinal).ToList();
                orderFrom = best;
                orderBy = best.By ?? chosen.Account;
                if (orderBy == current) orderBy = null;
                listedAt = best.At;
            }
            // A group renamed, added or removed by hand after the list was made takes effect there, whatever moved meanwhile.
            foreach (var edit in renamed.Where(edit => edit.At > listedAt))
            {
                if (edit is { From: { } from, To: { } to }) { if (order.IndexOf(from) is var at and >= 0 && !order.Contains(to)) order[at] = to; }
                else if (edit is { To: { } added }) { if (!order.Contains(added)) order.Add(added); }
                else if (edit.From is { } removed) order.Remove(removed);
            }
            // A group this stay filed a session under stays, after the rest.
            foreach (var name in filedHere.Where(name => !order.Contains(name)).OrderBy(name => name, StringComparer.Ordinal))
                order.Add(name);
            var listedNames = order.ToHashSet(StringComparer.Ordinal);
            // A name the list lacks: where its renaming leads, if the list has that; otherwise no group.
            string? Settle(string? name)
            {
                for (var step = 0; name is not null && step < 16; step++)
                {
                    if (listedNames.Contains(name)) return name;
                    if (!renamings.TryGetValue(name, out var next)) break;
                    name = next;
                }
                return name is not null && listedNames.Contains(name) ? name : null;
            }
            // The longest start of the list that the groups here already show in order stays; the rest is made anew at
            // the end, where the app adds new groups.
            var keep = new Dictionary<string, Group>(StringComparer.Ordinal);
            var next = 0;
            foreach (var group in here)
                if (next < order.Count && !Aside(group) && group.Name == order[next]) keep[order[next++]] = group;
            create.AddRange(order.Skip(next));
            var keptIds = keep.Values.Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
            var into = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            var ungroup = new List<string>();
            void Place(string session, string? name)
            {
                placed[session] = name;
                var at = currentLayout!.Assignments.GetValueOrDefault(session);
                if (name is null)
                {
                    if (at is null) return;
                    ungroup.Add(session);
                }
                else
                {
                    if (keep.TryGetValue(name, out var stays) && stays.Id == at) return;
                    if (!into.TryGetValue(name, out var sessions)) into[name] = sessions = [];
                    sessions.Add(session);
                    filed.Add(session);
                }
                moved.Add(session);
            }
            // A nested session moves with the session at the top of its family, never by itself: the app refuses that.
            foreach (var record in staying.Where(record => !nests.ContainsKey(record.SessionId)))
            {
                var (pin, want, from) = targets[record.SessionId];
                if (!want.Known || GroupNameOf(currentLayout!, record.SessionId, renamedHere) is not { Known: true }) { unresolved = true; continue; }
                var goal = Settle(want.Name);
                Place(record.SessionId, goal);
                targets[record.SessionId] = (pin, (true, goal), from);
            }
            // An older state follows its group when that is made anew or renamed, and its newest state when its group goes.
            foreach (var record in currentRecords.Where(record => retiring.ContainsKey(record.SessionId) && !leaving.Contains(record.SessionId)
                         && !nests.ContainsKey(record.SessionId)))
            {
                if (GroupNameOf(currentLayout!, record.SessionId, renamedHere) is not { Known: true, Name: { } name }
                    || keptIds.Contains(currentLayout!.Assignments[record.SessionId])) continue;
                if (Settle(name) is { } heir) Place(record.SessionId, heir);
                else if (targets.TryGetValue(retiring[record.SessionId].Newest.SessionId, out var head) && head.Group.Known)
                    Place(record.SessionId, head.Group.Name);
                else Place(record.SessionId, null);
            }
            // A group out of place steps aside before its name is made anew, and every session filed under it that a list
            // here shows follows to the group of that name. A group the list lacks is renamed out of the way too, so one
            // left over shows its mark. Either goes once the moves empty it of every session a list here shows. Archived
            // sessions filed under it leave the group: the app moves no archived session (Claude 4.23118 refuses the whole
            // move with "archived sessions aren't shown in the sidebar's groups or pins"), so they are counted, not moved;
            // so are the sessions the plan archives, which are not moved either.
            var taken = here.Select(group => group.Name).Concat(order).ToHashSet(StringComparer.Ordinal);
            bool Stays(string session) => byId.ContainsKey(session) && !leaving.Contains(session);
            foreach (var group in here.Where(group => !keptIds.Contains(group.Id)))
            {
                // A nested session's own assignment counts for nothing: it leaves with its family.
                var members = currentLayout!.Assignments.Where(pair => pair.Value == group.Id && !nests.ContainsKey(pair.Key))
                    .Select(pair => pair.Key).ToArray();
                // Made anew: its own name, or where its renaming leads, is on the list.
                var remade = Settle(Stands(group)) is not null;
                var emptied = members.Where(Stays).All(moved.Contains);
                var marked = Aside(group);
                if (!emptied && !create.Contains(group.Name)) continue;
                var mark = marked ? group.Name : MovingName(group.Name, taken);
                if (mark != group.Name) renameGroups.Add(new(group.Id, group.Name, mark));
                if (!emptied) continue;
                deleteGroups.Add(new(group.Id, mark, members.Count(member => !Stays(member)), remade));
                dropped.UnionWith(members.Where(leaving.Contains));
            }
            moves.AddRange(into.OrderBy(pair => order.IndexOf(pair.Key))
                .Select(pair => new Move(pair.Key, pair.Value, keep.TryGetValue(pair.Key, out var stays) ? stays.Id : null)));
            if (ungroup.Count > 0) moves.Add(new Move(null, ungroup));
        }
        // A group the scope does not list leaves that session where it is, and nothing about it is settled.
        if (unresolved) notes.Add("groups_unresolved");
        var titles = renames.ToDictionary(rename => rename.Session, rename => rename.Title, StringComparer.Ordinal);
        var pins = new List<Pin>();
        if (pinPlan)
            foreach (var record in staying)
            {
                var target = targets[record.SessionId];
                var pinned = !filed.Contains(record.SessionId) && record.Pinned;
                if (target.Pin.Value != pinned)
                    pins.Add(new(record.SessionId, target.Pin.Value, titles.GetValueOrDefault(record.SessionId) ?? record.Title,
                        Label(target.Pin.By == current ? null : target.Pin.By)));
            }
        var pinning = pins.Select(pin => pin.Session).ToHashSet(StringComparer.Ordinal);
        // A model, effort or mode differing from what the record has is set with the app's tools (none differs; unknown,
        // a session in plan mode, is left alone), the most recently used conversation first. The calling session cannot
        // change its own model or effort: those it is told instead, and they are not set out as in progress.
        var models = new List<SettingModel>();
        var efforts = new List<SettingEffort>();
        var permissions = new List<SettingMode>();
        SelfSetting? selfSetting = null;
        var beyond = 0;
        var settingTargets = new Dictionary<string, (Offer<string>? Model, Offer<string>? Effort, Offer<string>? Mode)>(StringComparer.Ordinal);
        static Offer<string>? Differing(Offer<string>? want, string? has) => want is not null && has is not null && want.Value != has ? want : null;
        static string? From(string? has) => string.IsNullOrEmpty(has) ? null : has;
        string? By(Offer<string> offer) => Label(offer.By == current ? null : offer.By);
        var self = lineage?.Self;
        foreach (var record in staying.Where(record => settingsFrom.ContainsKey(record.SessionId))
                     .OrderByDescending(record => settingsFrom[record.SessionId].Used))
        {
            var (modelFrom, effortFrom, modeFrom, _, inScope) = settingsFrom[record.SessionId];
            var model = Differing(modelFrom, record.Model);
            var effort = Differing(effortFrom, record.Effort);
            var mode = Differing(modeFrom, record.Mode);
            if (model is null && effort is null && mode is null) continue;
            if (!inScope) { beyond++; continue; }
            var title = titles.GetValueOrDefault(record.SessionId) ?? record.Title;
            var isSelf = record.SessionId == self;
            if (isSelf)
            {
                if (model is not null || effort is not null)
                    selfSetting = new(model?.Value, model is null ? null : From(record.Model), model is null ? null : By(model),
                        effort?.Value, effort is null ? null : From(record.Effort), effort is null ? null : By(effort));
                model = effort = null;
            }
            if (model is not null) models.Add(new(record.SessionId, title, model.Value, From(record.Model), By(model)));
            if (effort is not null) efforts.Add(new(record.SessionId, title, effort.Value, From(record.Effort), By(effort)));
            if (mode is not null)
                permissions.Add(new(record.SessionId, title, mode.Value, From(record.Mode), ModeRank(mode.Value) > ModeRank(record.Mode),
                    By(mode), isSelf));
            if (model is not null || effort is not null || mode is not null) settingTargets[record.SessionId] = (model, effort, mode);
        }
        // An older state goes only once what it leaves is known and already in place on its newest state: its own group,
        // the group and pin the newest state should have, and the newest state's own, all read, matching and not about
        // to change.
        bool Ready(Record old, Record head) =>
            pinPlan && groups && targets.TryGetValue(head.SessionId, out var target)
            && GroupNameOf(currentLayout!, old.SessionId, renamedHere).Known
            && target.Group is { Known: true, Name: var want }
            && GroupNameOf(currentLayout!, head.SessionId, renamedHere) is { Known: true, Name: var have } && want == have
            && target.Pin.Value == head.Pinned && !moved.Contains(head.SessionId) && !pinning.Contains(head.SessionId);
        // A conversation archived elsewhere has nothing to hand on: it is ready once its pin can be read.
        var archive = currentRecords.Where(record => retiring.ContainsKey(record.SessionId) || leaving.Contains(record.SessionId))
            .Select(record =>
            {
                if (!leaving.Contains(record.SessionId))
                {
                    var older = retiring[record.SessionId];
                    return new Retire(record.SessionId, record.Title, pinsKnown && record.Pinned, older.Newest.SessionId,
                        older.Newest.Title, Ready(record, byId[older.Newest.SessionId]), older.LastWritten, older.MovedOn);
                }
                var own = gone.TryGetValue(record.SessionId, out var itself);
                var away = own ? itself! : gone[retiring[record.SessionId].Newest.SessionId];
                return new Retire(record.SessionId, record.Title, pinsKnown && record.Pinned, null, null, pinPlan,
                    own ? away.LastActive : retiring[record.SessionId].LastWritten, null, Label(away.Account) ?? away.Account, away.At);
            })
            .OrderBy(retire => retire.Title, StringComparer.Ordinal).ToList();
        var sidebars = new List<Sidebar> { View(current, currentLabel, currentRecords, currentLayout, nests, pinsKnown) };
        sidebars.AddRange(others.Select(other => View(other.Account, other.Label, other.Records, other.Layout,
            Nests(other.Records, other.Archived), pinsKnown)));
        var views = seen.OrderBy(side => side.Other.Account, StringComparer.Ordinal).Select(side => new OtherView(side.Other.Account,
                side.Other.Label, side.Other.LeftAt, side.Matches.Count,
                side.View.Where(record => !side.Claimed.Contains(record.SessionId)).Select(Name).Order(StringComparer.Ordinal).ToArray()))
            .ToList();
        var regrouped = renameGroups.Count > 0 || create.Count > 0 || deleteGroups.Count > 0;
        // What the plan sets out to change, each value with the change it comes from: what it reaches keeps that change's
        // time; what its moves do on the way, and the unpinning of an older state before archiving, is nobody's choice.
        var intent = new Dictionary<string, ClaudeSessionLayout.Target>(StringComparer.Ordinal);
        ClaudeSessionLayout.Stamp<T> Stamp<T>(Offer<T> offer) => new(offer.Value, offer.At, offer.By == current ? null : offer.By);
        foreach (var record in staying)
        {
            var target = targets[record.SessionId];
            var pin = pinning.Contains(record.SessionId) || filed.Contains(record.SessionId) && record.Pinned ? Stamp(target.Pin) : null;
            var group = moved.Contains(record.SessionId) && target.Group.Known
                ? new ClaudeSessionLayout.Stamp<string?>(target.Group.Name, target.GroupFrom?.At ?? DateTimeOffset.MinValue,
                    target.GroupFrom?.By is { } by && by != current ? by : null)
                : null;
            var title = titleFrom.TryGetValue(record.SessionId, out var from) ? Stamp(from) : null;
            var (model, effort, mode) = settingTargets.GetValueOrDefault(record.SessionId);
            if (pin is not null || group is not null || title is not null || model is not null || effort is not null || mode is not null)
                intent[record.SessionId] = new(pin, group, title, filed.Contains(record.SessionId), model is null ? null : Stamp(model),
                    effort is null ? null : Stamp(effort), mode is null ? null : Stamp(mode));
        }
        // What happens on the way to archiving is nobody's choice: an older state's move, the group a session leaves with a
        // group deleted, and a pin undone by a move or before archiving.
        foreach (var record in currentRecords.Where(record => retiring.ContainsKey(record.SessionId) || leaving.Contains(record.SessionId)))
        {
            var moving = moved.Contains(record.SessionId);
            var leaves = dropped.Contains(record.SessionId);
            if (!record.Pinned && !moving && !leaves) continue;
            intent[record.SessionId] = new(record.Pinned ? new(false, DateTimeOffset.MinValue) : null,
                moving ? new(placed.GetValueOrDefault(record.SessionId), DateTimeOffset.MinValue)
                : leaves ? new(null, DateTimeOffset.MinValue) : null, null, filed.Contains(record.SessionId));
        }
        var renamedIntent = renameGroups.ToDictionary(rename => rename.Id, rename => new ClaudeSessionLayout.Moved(rename.From, rename.To),
            StringComparer.Ordinal);
        foreach (var (id, moving) in renamedHere)
            if (here.Any(group => group.Id == id && group.Name == moving.To)) renamedIntent.TryAdd(id, moving);
        var pending = new ClaudeSessionLayout.Pending(me?.Stay ?? 0, intent,
            regrouped ? new(order, orderFrom?.Renamed ?? new Dictionary<string, string>(), orderFrom?.At ?? DateTimeOffset.MinValue,
                orderBy) : null, renamedIntent);
        return new("ok", current, currentLabel, views, archive, renameGroups, create, moves, pins, renames, deleteGroups,
            regrouped ? order : null, regrouped ? Label(orderBy) : null, onlyHere.Select(Name).Order(StringComparer.Ordinal).ToArray(),
            sidebars, notes is { Count: > 0 } ? notes : null, Pending: pending, Models: models, Efforts: efforts,
            Permissions: permissions, SelfSettings: selfSetting, SettingsBeyond: beyond > 0 ? beyond : null);
    }

    /// <summary>When a record was last used in its own list: its activity, unless the widget wrote it since (a copy, or an
    /// advance, carries the source's activity time). Null without activity of its own.</summary>
    private static DateTimeOffset? OwnActivity(Record record, IReadOnlyDictionary<string, DateTimeOffset>? writes)
    {
        if (record.LastActivityAt <= 0) return null;
        var active = DateTimeOffset.FromUnixTimeMilliseconds(record.LastActivityAt);
        return writes is not null && writes.TryGetValue(record.SessionId, out var wrote) && active <= wrote ? null : active;
    }

    /// <summary>A value one account changed: when, and the account the change came from.</summary>
    private sealed record Offer<T>(T Value, DateTimeOffset At, string? By);

    /// <summary>The newest change of one value among the offers, with the account it came from; on a tie this account's,
    /// then the one the app left last. Null when nobody changed it.</summary>
    private static Offer<T>? Newest<T>(IReadOnlyList<(ClaudeSessionLayout.Changes Change, int Rank, string Account)> offers,
        Func<ClaudeSessionLayout.Changes, ClaudeSessionLayout.Stamp<T>?> field)
    {
        ClaudeSessionLayout.Stamp<T>? best = null;
        var rank = 0;
        string? by = null;
        foreach (var offer in offers)
            if (field(offer.Change) is { } stamp && (best is null || stamp.At > best.At || stamp.At == best.At && offer.Rank < rank))
            { best = stamp; rank = offer.Rank; by = stamp.By ?? offer.Account; }
        return best is null ? null : new(best.Value, best.At, by);
    }

    /// <summary>The changes that count for a record: its own, and its older states' (the most recently active first),
    /// archived ones included — each found again as an older state of this record, so a branch keeps its changes to
    /// itself. A pin or a group taken off an older state after the conversation moved on only put that line away, so it
    /// says nothing about the conversation.</summary>
    private static IEnumerable<ClaudeSessionLayout.Changes> Fold(string sessionId,
        IEnumerable<(string Id, DateTimeOffset MovedOn)> olderStates, IReadOnlyDictionary<string, ClaudeSessionLayout.Changes> changes)
    {
        if (changes.TryGetValue(sessionId, out var own)) yield return own;
        foreach (var (id, movedOn) in olderStates)
            if (changes.TryGetValue(id, out var change))
                yield return change with
                {
                    Pinned = change.Pinned is { Value: false } unpinned && unpinned.At > movedOn ? null : change.Pinned,
                    Group = change.Group is { Value: null } ungrouped && ungrouped.At > movedOn ? null : change.Group
                };
    }

    private static Dictionary<string, ClaudeSessionLayout.Changes> NoChanges() => new(StringComparer.Ordinal);

    /// <summary>Recorded changes with later ones over them, value by value.</summary>
    private static Dictionary<string, ClaudeSessionLayout.Changes> Merge(
        IReadOnlyDictionary<string, ClaudeSessionLayout.Changes>? recorded, IReadOnlyDictionary<string, ClaudeSessionLayout.Changes> later)
    {
        var merged = NoChanges();
        if (recorded is not null) foreach (var (id, change) in recorded) merged[id] = change;
        foreach (var (id, change) in later) merged[id] = change.Over(merged.GetValueOrDefault(id));
        return merged;
    }

    /// <summary>Another account as seen from here: its records with older states seen through, which of them serves
    /// each session here, and the newest change it made to each value and to its group list — what the widget recorded,
    /// with what it changed since its last checkpoint over it, dated to when the app left it.</summary>
    private sealed class Seen
    {
        internal required Other Other { get; init; }
        internal required int Rank { get; init; }
        internal required IReadOnlyList<Record> View { get; init; }
        internal required Dictionary<string, Record> Matches { get; init; }
        internal required HashSet<string> Claimed { get; init; }
        // A newest state's id → its older states, the most recently active first.
        internal required ILookup<string, (string Id, DateTimeOffset MovedOn)> OlderOf { get; init; }
        internal required IReadOnlyDictionary<string, ClaudeSessionLayout.Changes> Changes { get; init; }
        internal required ClaudeSessionLayout.GroupChange? GroupsChanged { get; init; }
        // The groups it renamed by hand, recorded and since its last checkpoint (dated to when the app left it).
        internal required IReadOnlyList<ClaudeSessionLayout.GroupRenaming> Renamings { get; init; }
        // Its nested sessions, each with the session at the top of its family.
        internal required IReadOnlyDictionary<string, string> Nested { get; init; }

        /// <summary>The changes that count for one of its records (see <see cref="ClaudeSessionSync.Fold"/>). A nested one's
        /// group is its family's, whatever was recorded for it: the newest change of the top session's group, when this
        /// account made it. One a plan brought there was another account's change to that session, which is a session of
        /// its own there, as this conversation is.</summary>
        internal IEnumerable<ClaudeSessionLayout.Changes> Fold(Record record)
        {
            var changes = ClaudeSessionSync.Fold(record.SessionId, OlderOf[record.SessionId], Changes);
            if (!Nested.TryGetValue(record.SessionId, out var top)) return changes;
            var family = ClaudeSessionSync.Fold(top, OlderOf[top], Changes).Select(change => change.Group)
                .OfType<ClaudeSessionLayout.Stamp<string?>>().MaxBy(stamp => stamp.At);
            return changes.Select(change => change with { Group = null })
                .Append(new ClaudeSessionLayout.Changes(Group: family is { By: null } ? family : null));
        }

        internal static Seen Of(Other other, int rank, ClaudeSessionLayout kept, IReadOnlyList<Record> newest,
            Lineage? lineage, bool pinsCount, bool copiesKnown)
        {
            var older = lineage is null ? new Dictionary<string, Older>(StringComparer.Ordinal)
                : OlderStates(other.Records, lineage, other.Archived);
            var activity = other.Records.Concat(other.Archived ?? []).ToDictionary(record => record.SessionId, record => record.LastActivityAt,
                StringComparer.Ordinal);
            var view = other.Records.Where(record => !older.ContainsKey(record.SessionId)).ToArray();
            var (matches, claimed) = Match(newest, view);
            var nested = Nests(other.Records, other.Archived);
            var observed = copiesKnown && other.CopyTitles is { } copyTitles
                ? Observe(new(other.Records, other.Layout is { } layout ? layout with { Nested = nested } : null, pinsCount, copyTitles, nested),
                    kept.Checkpoints.GetValueOrDefault(other.Account), other.LeftAt ?? DateTimeOffset.MinValue,
                    kept.Pendings.GetValueOrDefault(other.Account), kept.Evidences.GetValueOrDefault(other.Account))
                : null;
            var groups = kept.GroupsChanged.GetValueOrDefault(other.Account);
            if (observed?.Groups is { } order && (groups is null || order.At >= groups.At)) groups = order;
            return new()
            {
                Other = other, Rank = rank, View = view, Matches = matches, Claimed = claimed,
                OlderOf = older.OrderByDescending(pair => activity.GetValueOrDefault(pair.Key))
                    .ToLookup(pair => pair.Value.Newest.SessionId, pair => (Id: pair.Key, pair.Value.MovedOn), StringComparer.Ordinal),
                Changes = Merge(kept.Changed.GetValueOrDefault(other.Account), observed?.Changes ?? NoChanges()),
                GroupsChanged = groups,
                Renamings = (kept.Renamings.GetValueOrDefault(other.Account) ?? []).Concat(observed?.Renamings ?? []).ToArray(),
                Nested = nested
            };
        }
    }

    /// <summary>One record there serves one session here. The same transcript id is matched first; a continuation either
    /// way (one side lists the other's current transcript as earlier) only takes a record still free, so an older state
    /// or a branch of a conversation that is already matched is left alone.</summary>
    private static (Dictionary<string, Record> Matches, HashSet<string> Claimed) Match(IReadOnlyList<Record> newest,
        IReadOnlyList<Record> sources)
    {
        var byCli = sources.Where(record => record.Cli is not null).ToLookup(record => record.Cli!, StringComparer.Ordinal);
        var byPrior = sources.SelectMany(record => record.Prior.Select(id => (Id: id, Record: record)))
            .ToLookup(pair => pair.Id, pair => pair.Record, StringComparer.Ordinal);
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
        return (matches, claimed);
    }

    // Known is false for an assignment to a group the scope does not list: that session is not moved.
    private static (bool Known, string? Name) GroupOf(Layout layout, string sessionId) =>
        !layout.Assignments.TryGetValue(sessionId, out var id) ? (true, null)
        : layout.Groups.FirstOrDefault(group => group.Id == id) is { } group ? (true, group.Name) : (false, null);

    /// <summary>A session's group by the name it stands for (see <see cref="Standing"/>); a nested session's is the group of
    /// the session at the top of its family, as the app shows it.</summary>
    private static (bool Known, string? Name) GroupNameOf(Layout layout, string sessionId,
        IReadOnlyDictionary<string, ClaudeSessionLayout.Moved>? renamed) =>
        FiledUnder(layout, layout.Nested?.GetValueOrDefault(sessionId) ?? sessionId, renamed);

    /// <summary>The group a session is filed under itself, by the name it stands for: a nested session's own assignment,
    /// which shows once it is out of its family.</summary>
    private static (bool Known, string? Name) FiledUnder(Layout layout, string sessionId,
        IReadOnlyDictionary<string, ClaudeSessionLayout.Moved>? renamed)
    {
        if (!layout.Assignments.TryGetValue(sessionId, out var id)) return (true, null);
        if (layout.Groups.FirstOrDefault(group => group.Id == id) is not { } group) return (false, null);
        return (true, Standing(group, renamed) ?? group.Name);
    }

    /// <summary>The name a group aside stands for: a group the plan in progress renamed out of the way, while it bears the
    /// name it was given, stands for the name it had; without that plan, a name with the mark stands for itself without
    /// it. Null for a group that is not aside (one the user renamed since included).</summary>
    private static string? Standing(Group group, IReadOnlyDictionary<string, ClaudeSessionLayout.Moved>? renamed) =>
        renamed is not null && renamed.TryGetValue(group.Id, out var moved)
            ? group.Name == moved.To ? moved.From : null
            : IsMoving(group.Name) ? Base(group.Name) : null;

    /// <summary>What a record shows: its pin when pins count, its group when it resolves (see <see cref="GroupNameOf"/>),
    /// its title when it has one, and for the widget's copy the title the widget wrote.</summary>
    private static ClaudeSessionLayout.Shown ShownOf(Record record, Layout? layout, bool pins,
        IReadOnlyDictionary<string, string?> copyTitles, IReadOnlyDictionary<string, ClaudeSessionLayout.Moved>? renamed)
    {
        var group = layout is null ? (Known: false, Name: (string?)null) : GroupNameOf(layout, record.SessionId, renamed);
        return new(pins ? record.Pinned : null, group.Known, group.Known ? group.Name : null,
            string.IsNullOrWhiteSpace(record.Title) ? null : record.Title, copyTitles.GetValueOrDefault(record.SessionId),
            record.Model, record.Effort, record.Mode);
    }

    /// <summary>Whether a record's title was chosen in its list: always for a record of its own; for the widget's copy
    /// only once it differs from the title the widget wrote there (where that is not tracked, once the app recorded a
    /// rename).</summary>
    private static bool OwnTitle(Record record, IReadOnlyDictionary<string, string?> copyTitles) =>
        !copyTitles.TryGetValue(record.SessionId, out var written) || (written is not null ? record.Title != written : record.Renamed);

    /// <summary>What a record gives without a checkpoint to compare with: everything of its own, of its model, effort and
    /// permission mode only what is explicit (a mode other than <c>default</c>); of the widget's copy only what was chosen
    /// there — its group once filed, its pin once pinned or filed (filing unpins, so its pin was chosen since), its title
    /// once its own, none of the three settings (it carries the source's) — and what the first format shows it had
    /// received.</summary>
    private static ClaudeSessionLayout.Shown Gives(Record record, ClaudeSessionLayout.Shown shown,
        IReadOnlyDictionary<string, string?> copyTitles, ClaudeSessionLayout.Evidence? evidence)
    {
        if (!copyTitles.ContainsKey(record.SessionId))
            return shown with
            {
                Model = shown.Model is { Length: > 0 } ? shown.Model : null, Effort = shown.Effort is { Length: > 0 } ? shown.Effort : null,
                Mode = shown.Mode is { Length: > 0 } and not DefaultMode ? shown.Mode : null
            };
        var filed = shown is { HasGroup: true, Group: not null };
        var group = filed || evidence?.Group == true && shown.HasGroup;
        return new(shown.Pinned is true || filed || evidence?.Pinned == true ? shown.Pinned : null, group, group ? shown.Group : null,
            OwnTitle(record, copyTitles) ? shown.Title : null);
    }

    /// <summary>A model, effort or mode changed since the checkpoint: known on both sides, different, and not none (the
    /// app's tools cannot take a value away). The mode the app fills into a record it loads without one (none to
    /// <c>default</c>) was nobody's choice.</summary>
    private static string? Setting(string? before, string? now, bool mode = false) =>
        before is null || now is null || now == before || now.Length == 0 || mode && before.Length == 0 && now == DefaultMode ? null : now;

    private const string DefaultMode = "default";

    /// <summary>A permission mode's rank, for whether a plan raises it: none ranks as <c>default</c>.</summary>
    private static int ModeRank(string? mode) => mode switch
    {
        "plan" => 0, "acceptEdits" => 2, "auto" => 3, "bypassPermissions" => 4, _ => 1
    };

    /// <summary>What the app gave a session that has left its family since <paramref name="checkpoint"/> — moved to top
    /// level, or its family archived — as the checkpoint foresaw it (see <see cref="ClaudeSessionLayout.Checkpoint"/>);
    /// <see cref="Observe"/> adds what its family shows now. A checkpoint an earlier widget took foresaw nothing: a
    /// session moved to top level since then (its record written after it) may show a pin and any group the app gave it.
    /// Null for one that has not left a family.</summary>
    private static ClaudeSessionLayout.Shown? Left(Record record, ClaudeSessionLayout.Checkpoint checkpoint) =>
        checkpoint.Nested is { } nested ? nested.GetValueOrDefault(record.SessionId)
        : record.Detached && record.Written > checkpoint.At ? new(Pinned: true) : null;

    /// <summary>What an account shows against its checkpoint, and the plan in progress there.</summary>
    /// <param name="Changes">What it changed, dated as asked, and what the plan reached, dated to the change it came from.</param>
    /// <param name="Groups">Its group list where it changed (as asked or as the plan reached it); null otherwise.</param>
    /// <param name="Open">What the plan has not reached yet, or its moves undid on the way: still in progress.</param>
    /// <param name="GroupsMoving">The group list is mid-move: nobody's choice.</param>
    /// <param name="Renamings">Groups renamed, added or removed by hand since the checkpoint, dated as asked; seen mid-move
    /// too, since another group's move in progress must not hide them.</param>
    /// <param name="GroupsAside">Some group stands aside, mid-move or left so once the rest of the plan was done.</param>
    internal sealed record Observation(Dictionary<string, ClaudeSessionLayout.Changes> Changes, ClaudeSessionLayout.GroupChange? Groups,
        IReadOnlyDictionary<string, ClaudeSessionLayout.Target> Open, bool GroupsMoving,
        IReadOnlyList<ClaudeSessionLayout.GroupRenaming> Renamings, bool GroupsAside = false);

    /// <summary>
    /// What an account changed since <paramref name="checkpoint"/>, each change dated <paramref name="at"/>: a value that
    /// differs from the checkpoint's, and what a conversation new since then gives (see <see cref="Gives"/>); without a
    /// checkpoint, everything it gives. A value unknown on either side is no change, nor is a copy's title the widget
    /// wrote there since the checkpoint. A value the plan in progress set out to give is no change of its own: reached,
    /// it counts as the change it came from; not reached yet, or a pin its move undid on the way, it stays in progress;
    /// anything else is a change. A nested session's group is its family's (see <see cref="Nests"/>), never a change of its
    /// own, nor is what the app gave one that has left its family since (see <see cref="Left"/>). The group list is the
    /// groups not aside: mid-move (groups aside, and the plan in progress has not made every
    /// group of its list yet, or no plan is known) it is no change; one the plan reached counts as the change it came from;
    /// with groups renamed (same id, new name) the change carries the renaming. A model, effort or permission mode changes
    /// only from one known value to another that is not none (see <see cref="Setting"/>), and is settled against the plan
    /// in progress as a title is. With <paramref name="writtenSince"/> (when the app came to the account, for the stay it
    /// is in), a value the checkpoint does not know yet is this stay's choice where it is explicit and the app wrote the
    /// record since then and the widget did not.
    /// </summary>
    internal static Observation Observe(State state, ClaudeSessionLayout.Checkpoint? checkpoint, DateTimeOffset at,
        ClaudeSessionLayout.Pending? pending = null, IReadOnlyDictionary<string, ClaudeSessionLayout.Evidence>? evidence = null,
        DateTimeOffset? writtenSince = null)
    {
        var changes = NoChanges();
        var open = new Dictionary<string, ClaudeSessionLayout.Target>(StringComparer.Ordinal);
        var renamed = pending?.Renamed;
        // What a plan reached counts as the change it came from: an operational one, dated the earliest, as none, and
        // this stay's own (dated the latest while a plan is made) as of now.
        ClaudeSessionLayout.Stamp<T>? Reached<T>(ClaudeSessionLayout.Stamp<T> want) =>
            want.At == DateTimeOffset.MinValue ? null : want.At == DateTimeOffset.MaxValue ? want with { At = at, By = null } : want;
        var nests = state.Layout?.Nested ?? state.Nests;
        var live = state.Records.ToDictionary(record => record.SessionId, StringComparer.Ordinal);
        // The family a session left, by its top as it shows now (above archived sessions, as the app nests); null when no
        // live session heads it.
        ClaudeSessionLayout.Shown? FamilyOf(Record record) =>
            record.Former is { } former && live.GetValueOrDefault(nests!.GetValueOrDefault(former) ?? former) is { } top
                ? ShownOf(top, state.Layout, state.PinsCount, state.CopyTitles, renamed) : null;
        foreach (var record in state.Records)
        {
            var nested = nests?.ContainsKey(record.SessionId) == true;
            var shown = ShownOf(record, state.Layout, state.PinsCount, state.CopyTitles, renamed);
            var before = checkpoint?.Sessions.GetValueOrDefault(record.SessionId);
            ClaudeSessionLayout.Shown changed;
            if (before is not null)
            {
                // What the app gave a session that has left its family since was chosen nowhere (see Left): a pin where its
                // family showed pinned then and shows it now, its own group, and none where its family moved since (the app
                // drops the own assignment of a session its family takes along). Where that cannot tell the app's doing from
                // the user's (the family pinned or unpinned since, moved and back, or not known), it is the user's.
                var left = nests is null || nested ? null : Left(record, checkpoint!);
                var family = left is null ? null : FamilyOf(record);
                var appPin = left?.Pinned == true && family?.Pinned == true;
                var group = shown.HasGroup && before.HasGroup && shown.Group != before.Group
                    && !(left is not null && (!left.HasGroup || shown.Group == left.Group
                        || shown.Group is null && family is { HasGroup: true } && family.Group != before.Group));
                // A copy's title the widget wrote since the checkpoint (following the original) was chosen nowhere here.
                var widget = state.CopyTitles.GetValueOrDefault(record.SessionId) is { } written && written != before.Written
                    && shown.Title == written;
                // The first look at this stay's models, efforts and modes (a checkpoint taken before it held none): a record the
                // app wrote since it came to the account, and the widget has not written since, shows this stay's choices; a
                // tool that sets one leaves lastActivityAt as it was, so nothing else would tell them.
                var fresh = writtenSince is { } arrived && record.Written > arrived
                    && !(state.Writes is not null && state.Writes.TryGetValue(record.SessionId, out var wrote) && wrote > arrived);
                string? Chosen(string? was, string? now, bool mode = false) =>
                    was is null && fresh && now is { Length: > 0 } && !(mode && now == DefaultMode) ? now : Setting(was, now, mode);
                changed = new(shown.Pinned is { } pinned && before.Pinned is { } was && pinned != was && !(pinned && appPin) ? pinned : null,
                    group, group ? shown.Group : null,
                    shown.Title is { } title && title != before.Title && !widget ? title : null,
                    Model: Chosen(before.Model, shown.Model), Effort: Chosen(before.Effort, shown.Effort),
                    Mode: Chosen(before.Mode, shown.Mode, mode: true));
            }
            else changed = Gives(record, shown, state.CopyTitles, checkpoint is null ? evidence?.GetValueOrDefault(record.SessionId) : null);
            ClaudeSessionLayout.Stamp<bool>? pin = changed.Pinned is { } pinnedNow ? new(pinnedNow, at) : null;
            ClaudeSessionLayout.Stamp<string?>? filing = changed.HasGroup && !nested ? new(changed.Group, at) : null;
            ClaudeSessionLayout.Stamp<string>? naming = changed.Title is { } titleNow ? new(titleNow, at) : null;
            ClaudeSessionLayout.Stamp<string>? model = changed.Model is { Length: > 0 } modelNow ? new(modelNow, at) : null;
            ClaudeSessionLayout.Stamp<string>? effort = changed.Effort is { Length: > 0 } effortNow ? new(effortNow, at) : null;
            ClaudeSessionLayout.Stamp<string>? mode = changed.Mode is { Length: > 0 } modeNow ? new(modeNow, at) : null;
            if (pending?.Sessions.GetValueOrDefault(record.SessionId) is { } target)
            {
                // Reached: the change it came from (an operational one, dated the earliest, is none). Not reached yet, or a
                // pin its move undid: still in progress. Anything else: a change of this account's own.
                ClaudeSessionLayout.Stamp<bool>? openPin = null;
                ClaudeSessionLayout.Stamp<string?>? openGroup = null;
                ClaudeSessionLayout.Stamp<string>? openTitle = null;
                if (target.Pinned is { } wantPin && shown.Pinned is { } hasPin)
                {
                    // A pin the plan's move undid: the checkpoint takes it as it is, and the plan pins it again.
                    var undone = target.Filed && !hasPin && wantPin.Value;
                    if (hasPin == wantPin.Value) pin = Reached(wantPin);
                    else if (before?.Pinned == hasPin || undone) { pin = null; openPin = wantPin; }
                }
                if (target.Group is { } wantGroup && shown.HasGroup && !nested)
                {
                    if (shown.Group == wantGroup.Value) filing = Reached(wantGroup);
                    else if (before is { HasGroup: true } && before.Group == shown.Group) { filing = null; openGroup = wantGroup; }
                }
                if (target.Title is { } wantTitle && shown.Title is { } hasTitle)
                {
                    if (hasTitle == wantTitle.Value) naming = Reached(wantTitle);
                    else if (before?.Title == hasTitle) { naming = null; openTitle = wantTitle; }
                }
                // A model, effort or mode as for titles: one still as in the checkpoint (an approval declined, say) stays
                // in progress; one passing through plan mode is unknown, so its target is dropped, neither reached nor kept open, as an
                // unknown title's is.
                ClaudeSessionLayout.Stamp<string>? Settle(ClaudeSessionLayout.Stamp<string>? want, string? has, string? was,
                    ref ClaudeSessionLayout.Stamp<string>? stamp)
                {
                    if (want is null || has is null) return null;
                    if (has == want.Value) { stamp = Reached(want); return null; }
                    if (was == has) { stamp = null; return want; }
                    return null;
                }
                var openModel = Settle(target.Model, shown.Model, before?.Model, ref model);
                var openEffort = Settle(target.Effort, shown.Effort, before?.Effort, ref effort);
                var openMode = Settle(target.Mode, shown.Mode, before?.Mode, ref mode);
                if (openPin is not null || openGroup is not null || openTitle is not null || openModel is not null
                    || openEffort is not null || openMode is not null)
                    open[record.SessionId] = new(openPin, openGroup, openTitle, target.Filed, openModel, openEffort, openMode);
            }
            var stamped = new ClaudeSessionLayout.Changes(pin, filing, naming, model, effort, mode);
            if (!stamped.IsEmpty) changes[record.SessionId] = stamped;
        }
        // The group list: mid-move it is nobody's choice; reached it counts as the change it came from.
        ClaudeSessionLayout.GroupChange? groups = null;
        var moving = false;
        var aside = false;
        var renamings = new List<ClaudeSessionLayout.GroupRenaming>();
        if (state.Layout is { } layout)
        {
            // A group aside stands for one made anew or about to go, so the list is the groups not aside. It is mid-move
            // while groups are aside and the plan in progress has not made every group of its list yet (or no plan is
            // known); once they all show, what the groups not aside show counts, though one the app would not empty
            // (archived sessions) may stay aside long.
            var standing = layout.Groups.Where(group => Standing(group, renamed) is null).ToArray();
            var names = standing.Select(group => group.Name).ToArray();
            aside = standing.Length < layout.Groups.Count;
            moving = aside && (pending?.Groups is not { } goal || !goal.Order.All(names.Contains));
            var present = layout.Groups.Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
            var asideIds = layout.Groups.Where(group => Standing(group, renamed) is not null).Select(group => group.Id)
                .ToHashSet(StringComparer.Ordinal);
            // A group the plan in progress renames out of the way that is gone went as the plan meant only where the plan
            // would have deleted it: once the group of its name was made anew, or when its name leaves the plan's list.
            // Gone before its name was made anew, the user removed it.
            var shownNames = names.ToHashSet(StringComparer.Ordinal);
            bool GoneAsPlanned(string id) => renamed is not null && renamed.TryGetValue(id, out var moved)
                && (shownNames.Contains(moved.From) || pending?.Groups is { } list && !list.Order.Contains(moved.From, StringComparer.Ordinal));
            // A group renamed, added or removed by hand is seen whatever else moves: a group with an id the checkpoint
            // knows under another name (not a plan's rename out of the way), one it does not know that no plan in progress
            // makes, and one it knows that is gone other than as the plan meant.
            if (checkpoint?.Groups is { } before)
            {
                var was = before.ToDictionary(group => group.Id, group => group.Name, StringComparer.Ordinal);
                var made = pending?.Groups?.Order.ToHashSet(StringComparer.Ordinal) ?? new HashSet<string>(StringComparer.Ordinal);
                foreach (var group in layout.Groups.Where(group => Standing(group, renamed) is null))
                    if (!was.TryGetValue(group.Id, out var old)) { if (!made.Contains(group.Name)) renamings.Add(new(null, group.Name, at)); }
                    else if (old != group.Name) renamings.Add(new(old, group.Name, at));
                foreach (var group in before.Where(group => !present.Contains(group.Id) && !GoneAsPlanned(group.Id)))
                    renamings.Add(new(group.Name, null, at));
            }
            if (!moving)
            {
                if (pending?.Groups is { } target && names.SequenceEqual(target.Order, StringComparer.Ordinal))
                    groups = target.At == DateTimeOffset.MinValue ? null
                        : target.At == DateTimeOffset.MaxValue ? target with { At = at, By = null } : target;
                else if (checkpoint is null) groups = names.Length > 0 ? new(names, new Dictionary<string, string>(), at) : null;
                // Against the checkpoint's list as it stands now: a group aside, or gone as the plan meant, is in neither
                // list, so a plan's rename out of the way or deletion never passes for the user's reordering.
                else if (checkpoint.Groups is { } listed && !names.SequenceEqual(listed
                             .Where(group => !asideIds.Contains(group.Id) && (present.Contains(group.Id) || !GoneAsPlanned(group.Id)))
                             .Select(group => group.Name), StringComparer.Ordinal))
                    groups = new(names, renamings.Where(renaming => renaming is { From: not null, To: not null })
                        .ToDictionary(renaming => renaming.From!, renaming => renaming.To!, StringComparer.Ordinal), at);
            }
        }
        return new(changes, groups, open, moving, renamings, aside);
    }

    /// <summary>For the widget: records in <paramref name="layout"/> what <paramref name="account"/> changed since its last
    /// checkpoint, settles the plan in progress there, and checkpoints what it shows now. A first checkpoint, and the first
    /// of a stay, date what they find to when the app last left the account (and end a plan of an earlier stay); later
    /// ones date it <paramref name="now"/>. A lost pin list and a group list mid-move are not taken: the checkpoint keeps
    /// what it had. A nested session shows the group of its family, which is never recorded as its change. False when
    /// something cannot be read whole now (a later pass tries again) or the account has no list here.</summary>
    internal static bool Checkpoint(ClaudeSessionLayout layout, Sources sources, string account, DateTimeOffset now) =>
        Checkpoint(layout, Read(sources, [account]), account, now);

    /// <summary>The same from a snapshot already read.</summary>
    internal static bool Checkpoint(ClaudeSessionLayout layout, Snapshot snapshot, string account, DateTimeOffset now)
    {
        if (!snapshot.Lists.TryGetValue(account, out var listing) || snapshot.Pinned is not { } pinned
            || listing.CopyTitles is not { } copyTitles || LayoutOf(snapshot.Sidebar, account, missingIsEmpty: true) is not { } groups)
            return false;
        // A record that could not be read now would look gone, and its changes would be forgotten.
        if (!listing.Whole) return false;
        var directory = listing.Directory;
        var records = listing.Records;
        groups = groups with { Nested = Nests(records, listing.Archived) };
        var known = layout.Accounts.GetValueOrDefault(account);
        // The widget started (or came from the first format) while the app showed this account, so it did not see the app
        // come: that was when the app last left another account. What the account shows then may hold that stay's own
        // choices, which no time can be told for; the account the user is in is given priority: what a first look finds
        // dates to just after that arrival (what a first look dated to when the app had left the account before is moved
        // there), and the arrival is kept. The price: a value left stale there since the arrival counts as new too.
        DateTimeOffset? unseenStart = null;
        if (known is { ArrivedAt: null } && layout.Active == account
            && layout.Accounts.Where(pair => pair.Key != account).Max(pair => pair.Value.LeftAt) is { } came
            && came > (known.LeftAt ?? DateTimeOffset.MinValue))
        {
            unseenStart = came.AddTicks(1);
            layout.Redate(account, known.LeftAt ?? DateTimeOffset.MinValue, unseenStart.Value);
            layout.Accounts[account] = known = known with { ArrivedAt = came };
        }
        var stay = known?.Stay ?? 0;
        var previous = layout.Checkpoints.GetValueOrDefault(account);
        var thisStay = previous is not null && previous.Stay == stay;
        var at = thisStay ? now : unseenStart ?? known?.LeftAt ?? DateTimeOffset.MinValue;
        var pinsCount = !PinsLost(pinned, layout);
        var pending = layout.Pendings.GetValueOrDefault(account);
        // At the first look in the stay the app is in, what the app wrote since it came shows this stay's choices.
        var observed = Observe(new(records, groups, pinsCount, copyTitles, Writes: listing.Writes), previous, at, pending,
            previous is null ? layout.Evidences.GetValueOrDefault(account) : null, writtenSince: thisStay ? known?.ArrivedAt : null);
        if (!layout.Changed.TryGetValue(account, out var changed)) layout.Changed[account] = changed = new(StringComparer.Ordinal);
        foreach (var (id, change) in observed.Changes) changed[id] = change.Over(changed.GetValueOrDefault(id));
        // A nested session's group is its family's: a group an earlier look recorded as its own goes.
        foreach (var id in groups.Nested!.Keys.Where(id => changed.GetValueOrDefault(id)?.Group is not null).ToArray())
        {
            var own = changed[id] with { Group = null };
            if (own.IsEmpty) changed.Remove(id); else changed[id] = own;
        }
        // A session whose record is gone for good has nothing left to change; an archived one keeps its changes for the
        // conversation it belongs to.
        var shown = records.Select(record => record.SessionId).ToHashSet(StringComparer.Ordinal);
        foreach (var id in changed.Keys.Where(id => !shown.Contains(id) && !File.Exists(Path.Combine(directory, id + ".json"))).ToArray())
            changed.Remove(id);
        if (observed.Renamings.Count > 0)
        {
            if (!layout.Renamings.TryGetValue(account, out var renamings)) layout.Renamings[account] = renamings = [];
            renamings.AddRange(observed.Renamings);
            if (renamings.Count > ClaudeSessionLayout.MaxRenamings) renamings.RemoveRange(0, renamings.Count - ClaudeSessionLayout.MaxRenamings);
        }
        // A group renamed earlier still passes on what was filed under its old name after later changes of the list.
        if (observed.Groups is { } order)
        {
            var renamings = new Dictionary<string, string>(layout.GroupsChanged.GetValueOrDefault(account)?.Renamed
                ?? new Dictionary<string, string>(), StringComparer.Ordinal);
            foreach (var (from, to) in order.Renamed) renamings[from] = to;
            layout.GroupsChanged[account] = order with { Renamed = renamings };
        }
        // The checkpoint takes what the account shows, a pin the plan's move undid included (the plan pins it again);
        // only a lost pin list keeps the pins it had.
        var sessions = new Dictionary<string, ClaudeSessionLayout.Shown>(StringComparer.Ordinal);
        foreach (var record in records)
        {
            var shownNow = ShownOf(record, groups, pinsCount, copyTitles, pending?.Renamed);
            sessions[record.SessionId] = pinsCount ? shownNow
                : shownNow with { Pinned = previous?.Sessions.GetValueOrDefault(record.SessionId)?.Pinned };
        }
        // The checkpoint keeps the groups not aside; mid-move it keeps the list it had, with what was done by hand since
        // (renamed, added, removed), so each edit is seen once.
        IReadOnlyList<ClaudeSessionLayout.Named>? listed = groups.Groups.Where(group => Standing(group, pending?.Renamed) is null)
            .Select(group => new ClaudeSessionLayout.Named(group.Id, group.Name)).ToArray();
        if (observed.GroupsMoving && previous?.Groups is { } kept)
        {
            var named = groups.Groups.Where(group => Standing(group, pending?.Renamed) is null)
                .ToDictionary(group => group.Id, group => group.Name, StringComparer.Ordinal);
            var present = groups.Groups.Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
            var knownIds = kept.Select(group => group.Id).ToHashSet(StringComparer.Ordinal);
            // A group gone since — removed by hand (seen above) or deleted as the plan meant — leaves the list; the plan's
            // own record of it goes at the same time below, so the next look never takes the plan's deletion for the user's.
            // A group new since joins it, whether the user added it (seen above) or the plan made it (no one's change), so
            // what the user does to it later is seen too.
            listed = kept.Where(group => present.Contains(group.Id))
                .Select(group => named.TryGetValue(group.Id, out var name) ? group with { Name = name } : group)
                .Concat(groups.Groups.Where(group => !knownIds.Contains(group.Id) && named.ContainsKey(group.Id))
                    .Select(group => new ClaudeSessionLayout.Named(group.Id, group.Name)))
                .ToArray();
        }
        else if (observed.GroupsMoving) listed = previous?.Groups;
        // What each nested session would show once out of its family, so the next look tells what the app gave it then
        // from what the user chose.
        var leaving = new Dictionary<string, ClaudeSessionLayout.Shown>(StringComparer.Ordinal);
        foreach (var (id, top) in groups.Nested!)
            if (sessions.TryGetValue(id, out var nestedNow))
            {
                var own = FiledUnder(groups, id, pending?.Renamed);
                leaving[id] = new(nestedNow.Pinned is { } self && sessions.GetValueOrDefault(top)?.Pinned is { } family ? self || family : null,
                    own.Known, own.Name);
            }
        layout.Checkpoints[account] = new(now, stay, listed, sessions, leaving);
        // A plan of this stay stays in progress with what it has not reached; of one of an earlier stay only the groups it
        // renamed out of the way, and its list, stay known while groups stand aside. What it did not reach is set out again
        // from the changes.
        if (pending is not null && (observed.GroupsAside || thisStay && pending.Stay == stay && observed.Open.Count > 0))
            layout.Pendings[account] = pending with
            {
                Sessions = thisStay && pending.Stay == stay ? observed.Open : new Dictionary<string, ClaudeSessionLayout.Target>(),
                Groups = observed.GroupsAside ? pending.Groups : null,
                // A rename not done yet, or still borne, stays known; one the user changed since is the user's.
                Renamed = pending.Renamed.Where(pair => groups.Groups.Any(group => group.Id == pair.Key
                        && (group.Name == pair.Value.From || group.Name == pair.Value.To)))
                    .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal)
            };
        else layout.Pendings.Remove(account);
        // Evidence from the first format has served once its values were seen; pins that look lost wait for the next.
        if (pinsCount) layout.Evidences.Remove(account);
        else if (layout.Evidences.TryGetValue(account, out var evidence))
            foreach (var id in evidence.Keys.ToArray())
                if (evidence[id] is { Pinned: true } seen) evidence[id] = seen with { Group = false }; else evidence.Remove(id);
        return true;
    }

    /// <summary>For the widget, asked for a plan in <paramref name="account"/> by the session <paramref name="self"/>:
    /// when the app shows that account, checkpoints it and makes the plan from what that one read saw, keeping what the
    /// plan sets out to change; otherwise only makes the plan, for viewing. The plan says which.</summary>
    internal static Plan Serve(ClaudeSessionLayout layout, Sources sources, string account, string? self, bool active, DateTimeOffset now)
    {
        var snapshot = Read(sources, layout.Accounts.Keys.Prepend(account));
        var checkpointed = active && Checkpoint(layout, snapshot, account, now);
        var plan = BuildFor(sources, account, layout, self, snapshot, now: now);
        if (!checkpointed || plan is not { Status: "ok", Pending: { } pending }) return plan with { Checkpointed = false };
        layout.Pendings[account] = pending;
        return plan with { Checkpointed = true };
    }

    private static JsonObject? TryReadSidebar(string localStorageDirectory)
    {
        try { return ReadSidebar(localStorageDirectory); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                                              or JsonException or InvalidOperationException)
        { return null; }
    }

    private static IReadOnlySet<string>? TryReadPins(string indexedDbDirectory)
    {
        try { return ClaudeSidebarPins.Read(indexedDbDirectory); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                                              or JsonException or InvalidOperationException)
        { return null; }
    }

    private static bool HasScope(JsonObject? sidebar, string account) =>
        sidebar?["customGroupsByScope"] is JsonObject scopes
        && scopes.Any(pair => string.Equals(pair.Key, account, StringComparison.OrdinalIgnoreCase));

    internal static bool IsMoving(string name) => name.Contains(MovingMark, StringComparison.Ordinal);

    /// <summary>A group's name without the mark of a move in progress.</summary>
    private static string Base(string name) =>
        name.IndexOf(MovingMark, StringComparison.Ordinal) is var at and >= 0 ? name[..at] : name;

    private static string MovingName(string name, ISet<string> taken)
    {
        for (var number = 1; ; number++)
        {
            var candidate = Base(name) + MovingMark + (number == 1 ? ")" : " " + number.ToString(CultureInfo.InvariantCulture) + ")");
            if (taken.Add(candidate)) return candidate;
        }
    }

    /// <summary>
    /// Records that are an older state of a conversation another record of the same list continues, each with the
    /// newest state that conversation reached. The other record lists this one's transcript as earlier (and is not
    /// listed back) and is no fork. The conversation went on in the transcript every continuing record lists right
    /// after this one's, which must also be the first created after it; it went on when that transcript's first entry
    /// this one does not hold was written (a continuation starts with copies of the earlier entries, ids and times
    /// kept), and the file must have existed by then. An older state has no entry after that; one written to afterwards
    /// is a branch the user went on with and stays, however the other side continues. The calling session stays too,
    /// and so does any record unless every transcript of its own, of the continuing lineage and of the newest state is
    /// there and dated. An open session may go: archiving stops its process, and the app refuses one that is working.
    /// </summary>
    /// <param name="archived">Archived records, each found as an older state the same way; never taken to continue one.</param>
    internal static Dictionary<string, Older> OlderStates(IReadOnlyList<Record> records, Lineage lineage,
        IReadOnlyList<Record>? archived = null)
    {
        var listers = records.Where(record => !record.Branch)
            .SelectMany(record => record.Prior.Distinct(StringComparer.Ordinal).Select(prior => (Prior: prior, Record: record)))
            .ToLookup(pair => pair.Prior, pair => pair.Record, StringComparer.Ordinal);
        IReadOnlyList<Record> Continuations(Record record) => record.Cli is not { } cli ? []
            : listers[cli].Where(other => other.SessionId != record.SessionId
                && (other.Cli is not { } next || !record.Prior.Contains(next, StringComparer.Ordinal))).ToArray();
        var spans = new Dictionary<string, TranscriptSpan?>(StringComparer.Ordinal);
        TranscriptSpan? Span(string cli) => spans.TryGetValue(cli, out var span) ? span : spans[cli] = lineage.Transcript(cli);
        var older = new Dictionary<string, Older>(StringComparer.Ordinal);
        foreach (var record in records.Concat(archived ?? []))
        {
            if (record.SessionId == lineage.Self || record.Cli is not { } cli || Continuations(record) is not { Count: > 0 } next
                || Span(cli) is not { LastEntry: { } lastWritten, Dated: true } own) continue;
            var onward = next.SelectMany(other => other.Prior.Append(other.Cli)).OfType<string>()
                .Where(id => id != cli).Distinct(StringComparer.Ordinal).ToDictionary(id => id, Span, StringComparer.Ordinal);
            if (onward.Values.Any(span => span is not { Dated: true })) continue;
            var listedNext = next.Select(other => NextAfter(other, cli)).Distinct(StringComparer.Ordinal).ToArray();
            var createdNext = onward.Where(pair => pair.Value!.Created > own.Created).OrderBy(pair => pair.Value!.Created)
                .Select(pair => pair.Key).FirstOrDefault();
            if (listedNext is not [{ } nextId] || createdNext != nextId || lineage.HandOver(cli, nextId) is not { } handOver
                || onward[nextId]!.Created > handOver + HandOverSlack || lastWritten > handOver + HandOverSlack) continue;
            var newest = next.MaxBy(other => other.LastActivityAt)!;
            var seen = new HashSet<string>(StringComparer.Ordinal) { record.SessionId, newest.SessionId };
            while (Continuations(newest).MaxBy(other => other.LastActivityAt) is { } step && seen.Add(step.SessionId)) newest = step;
            if (newest.Cli is not { } newestCli || Span(newestCli) is not { Dated: true }) continue;
            older[record.SessionId] = new(newest, lastWritten, handOver);
        }
        // Lineage that loops has no newest state to keep: none of it goes.
        foreach (var key in older.Where(pair => older.ContainsKey(pair.Value.Newest.SessionId)).Select(pair => pair.Key).ToArray())
            older.Remove(key);
        return older;
    }

    /// <summary>
    /// The conversations here that another account archived after anyone last talked in them, each with that account (the
    /// one that archived it last), when, and its last activity. No list there shows the conversation live, and a record
    /// archived there holds it: the same transcript, or one continued from it (not a branch of it). It was archived when
    /// that record was last written, since the app keeps no time of archiving; that must come after the conversation's
    /// last activity in every list that has it, so one talked in since, anywhere, stays. The calling session stays, as does
    /// a session another one staying here is nested under: archiving it would take the family apart.
    /// </summary>
    private static Dictionary<string, Away> Gone(IReadOnlyList<Record> newest, IReadOnlyList<Seen> seen, string? self,
        IReadOnlyList<Record> records)
    {
        var gone = new Dictionary<string, Away>(StringComparer.Ordinal);
        foreach (var record in newest)
        {
            if (record.SessionId == self || record.Cli is not { } cli || record.LastActivityAt <= 0) continue;
            var lastActive = record.LastActivityAt;
            (string Account, DateTimeOffset At)? last = null;
            foreach (var side in seen)
            {
                if (side.Matches.TryGetValue(record.SessionId, out var live))
                {
                    lastActive = Math.Max(lastActive, live.LastActivityAt);
                    continue;
                }
                foreach (var shelved in side.Other.Archived ?? [])
                {
                    if (shelved.Written is not { } at || shelved.Cli != cli && (shelved.Branch || !shelved.Prior.Contains(cli, StringComparer.Ordinal)))
                        continue;
                    lastActive = Math.Max(lastActive, shelved.LastActivityAt);
                    if (last is null || at > last.Value.At) last = (side.Other.Account, at);
                }
            }
            var active = DateTimeOffset.FromUnixTimeMilliseconds(lastActive);
            if (last is { } away && away.At > active) gone[record.SessionId] = new(away.Account, away.At, active);
        }
        for (var held = true; held;)
        {
            held = false;
            foreach (var record in records)
                if (!gone.ContainsKey(record.SessionId) && record.Parent is { } parent && gone.Remove(parent)) held = true;
        }
        return gone;
    }

    /// <summary>The transcript a record lists right after <paramref name="cli"/>, its current one last.</summary>
    private static string? NextAfter(Record record, string cli)
    {
        var order = record.Prior.Append(record.Cli).OfType<string>().ToList();
        var at = order.IndexOf(cli);
        return at >= 0 && at + 1 < order.Count ? order[at + 1] : null;
    }

    /// <summary>
    /// The sessions the app nests under another in one list, each with the session at the top of its family, as its
    /// sidebar reads them (Claude 3.1623): a session sits under its parent (<see cref="Record.Parent"/>) when that is in the
    /// list and live, or archived along with it; a live one whose parent is archived sits under the nearest live session
    /// above it, or under none; and a family climbs to its top. A nested session shows the top session's group whatever
    /// its own assignment says, and moves only with it: the app refuses to move it alone.
    /// </summary>
    /// <param name="archived">The archived records of the list: parents too, and nested themselves.</param>
    internal static Dictionary<string, string> Nests(IReadOnlyList<Record> records, IReadOnlyList<Record>? archived = null)
    {
        var all = new Dictionary<string, (Record Record, bool Shelved)>(StringComparer.Ordinal);
        foreach (var record in records) all[record.SessionId] = (record, false);
        foreach (var record in archived ?? []) all.TryAdd(record.SessionId, (record, true));
        string? Above(string id)
        {
            var (record, shelved) = all[id];
            if (record.Parent is not { } parent || parent == id || !all.TryGetValue(parent, out var up)) return null;
            if (shelved || !up.Shelved) return parent;
            var passed = new HashSet<string>(StringComparer.Ordinal) { id };
            for (string? at = parent; at is not null && passed.Add(at) && all.TryGetValue(at, out var step); at = step.Record.Parent)
                if (!step.Shelved) return at;
            return null;
        }
        var nested = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var id in all.Keys)
        {
            if (Above(id) is not { } top) continue;
            var family = new HashSet<string>(StringComparer.Ordinal) { id, top };
            while (Above(top) is { } higher && family.Add(higher)) top = higher;
            nested[id] = top;
        }
        return nested;
    }

    private static string Name(Record record) => string.IsNullOrWhiteSpace(record.Title) ? record.SessionId : record.Title;

    private static Sidebar View(string account, string? label, IReadOnlyList<Record> records, Layout? layout,
        IReadOnlyDictionary<string, string> nests, bool pinsKnown)
    {
        var pinned = pinsKnown ? records.Where(record => record.Pinned).Select(Name).Order(StringComparer.Ordinal).ToArray() : null;
        var byId = records.ToDictionary(record => record.SessionId, StringComparer.Ordinal);
        var nested = records.Where(record => nests.ContainsKey(record.SessionId))
            .Select(record => new NestedView(Name(record), byId.TryGetValue(nests[record.SessionId], out var top) ? Name(top) : nests[record.SessionId]))
            .OrderBy(view => view.Title, StringComparer.Ordinal).ToArray() is { Length: > 0 } listed ? listed : null;
        // A pinned session keeps its group but is shown under Pinned only; a nested one shows its family's.
        if (layout is null) return new(account, label, pinned, null, nested);
        var groups = layout.Groups.Select(group => new GroupView(group.Name, records
                .Where(record => !(pinsKnown && record.Pinned)
                                 && layout.Assignments.TryGetValue(nests.GetValueOrDefault(record.SessionId) ?? record.SessionId, out var id)
                                 && id == group.Id)
                .Select(Name).Order(StringComparer.Ordinal).ToArray(), group.Collapsed))
            .ToArray();
        return new(account, label, pinned, groups, nested);
    }

    /// <summary>One account's groups as the app stores them, in sidebar order with their folding; null when they cannot
    /// be confirmed (no store, an unfamiliar shape, or a missing scope unless <paramref name="missingIsEmpty"/>). The
    /// sidebar's sections give the order; a group they do not place follows the rest in stored order.</summary>
    internal static Layout? LayoutOf(JsonObject? sidebar, string account, bool missingIsEmpty)
    {
        if (sidebar?["customGroupsByScope"] is not JsonObject scopes) return null;
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
        if (Sections(sidebar, entry.Key) is not { } sections) return null;
        var ordered = groups.Select((group, index) => (Group: group, Index: index))
            .OrderBy(item => sections.TryGetValue(item.Group.Id, out var section) ? section.Order : double.MaxValue)
            .ThenBy(item => item.Index)
            .Select(item => item.Group with { Collapsed = sections.TryGetValue(item.Group.Id, out var section) && section.Collapsed })
            .ToArray();
        var assignments = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in map)
        {
            if (value is not JsonValue text || !text.TryGetValue<string>(out var id)) return null;
            if (key.StartsWith("code:", StringComparison.Ordinal)) assignments[key["code:".Length..]] = id;
        }
        return new(ordered, assignments);
    }

    /// <summary>A scope's sidebar sections by id (a custom group's section has the group's id): their place and whether
    /// they are folded. Empty when the scope has none yet; null when they have an unfamiliar shape.</summary>
    private static Dictionary<string, (double Order, bool Collapsed)>? Sections(JsonObject sidebar, string scope)
    {
        var sections = new Dictionary<string, (double Order, bool Collapsed)>(StringComparer.Ordinal);
        if (sidebar["codeSidebarByScope"] is not { } byScope) return sections;
        if (byScope is not JsonObject scopes) return null;
        var entry = scopes.FirstOrDefault(pair => string.Equals(pair.Key, scope, StringComparison.OrdinalIgnoreCase));
        if (entry.Key is null) return sections;
        if (entry.Value is not JsonObject value || value["sections"] is not JsonArray list) return null;
        foreach (var node in list)
        {
            if (node is not JsonObject section || Text(section, "id") is not { } id || section["order"] is not JsonValue order
                || !order.TryGetValue<double>(out var place)) return null;
            sections[id] = (place, section["collapsed"] is JsonValue folded && folded.TryGetValue<bool>(out var collapsed) && collapsed);
        }
        return sections;
    }

    /// <summary>The app's sidebar state for every account. Throws unless a complete snapshot holds it: the app may be
    /// writing (a log ending inside a record) or compacting (a table replaced underneath), so either is retried.</summary>
    private static JsonObject ReadSidebar(string localStorageDirectory)
    {
        if (!Directory.Exists(localStorageDirectory)) throw new DirectoryNotFoundException("No app local storage.");
        var store = new ClaudeLocalStorage(localStorageDirectory);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                var snapshot = store.Read([SidebarKey], whileOpen: true);
                if (!snapshot.Clean) throw new InvalidDataException("The app is writing its local storage.");
                var text = snapshot.Values[SidebarKey] ?? throw new InvalidDataException("No sidebar state.");
                return (JsonNode.Parse(text) as JsonObject)?["state"] as JsonObject
                       ?? throw new InvalidDataException("The app's sidebar state has an unfamiliar shape.");
            }
            catch (Exception exception) when (attempt < 2 && exception is IOException or InvalidDataException)
            { Thread.Sleep(300); }
        }
    }

    /// <summary>When a transcript was created and last written, and its newest entry (read near its end); null when the
    /// transcript is missing or unreadable, or a line there is broken: a lost line could be the newest entry.</summary>
    internal static TranscriptSpan? Transcript(string projectsRoot, string cli)
    {
        if (Locate(projectsRoot, cli) is not { } path) return null;
        try
        {
            var created = new DateTimeOffset(File.GetCreationTimeUtc(path));
            var written = new DateTimeOffset(File.GetLastWriteTimeUtc(path));
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = (int)Math.Min(stream.Length, TranscriptTailBytes);
            // Pooled: every plan pass reads many tails, and a fresh 4 MB array each time is garbage the GC must collect.
            var tail = ArrayPool<byte>.Shared.Rent(length);
            try
            {
                stream.Seek(-length, SeekOrigin.End);
                stream.ReadExactly(tail, 0, length);
                if (Entries(tail.AsSpan(0, length), partialStart: length < stream.Length) is not { } entries) return null;
                return new(created, written, entries.Count == 0 ? null : entries.Max(entry => entry.At));
            }
            finally { ArrayPool<byte>.Shared.Return(tail); }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>When the conversation in transcript <paramref name="older"/> went on in <paramref name="next"/>: the oldest
    /// entry of the later transcript that the earlier one does not hold. Null when either cannot be read, has a broken
    /// line, or the later one holds nothing new. A line of nothing but NUL bytes in the earlier one (what a crash leaves)
    /// is not broken: what it held is lost to the app too, it was written before the lines after it, whose time
    /// <see cref="Transcript"/> dates, and an entry of it the later one holds only makes the hand-over earlier, which
    /// keeps the older state. In the later one such a line could be the first new entry, so it stays broken.</summary>
    internal static DateTimeOffset? HandOver(string projectsRoot, string older, string next)
    {
        if (Locate(projectsRoot, older) is not { } olderPath || Locate(projectsRoot, next) is not { } nextPath) return null;
        try
        {
            // Streamed: transcripts reach hundreds of megabytes, and only the earlier one's ids and the later one's oldest
            // new entry are needed.
            var known = new HashSet<string>(StringComparer.Ordinal);
            if (!EachEntry(olderPath, skipLost: true, entry => { if (entry.Id is { } id) known.Add(id); })) return null;
            DateTimeOffset? first = null;
            if (!EachEntry(nextPath, skipLost: false, entry =>
                {
                    if (entry.Id is { } id && !known.Contains(id) && (first is null || entry.At < first)) first = entry.At;
                })) return null;
            return first;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        { return null; }
    }

    private static string? Locate(string projectsRoot, string cli) =>
        !Guid.TryParse(cli, out _) || !Directory.Exists(projectsRoot) ? null
        : Directory.EnumerateDirectories(projectsRoot).Select(project => Path.Combine(project, cli + ".jsonl")).FirstOrDefault(File.Exists);

    /// <summary>Hands each entry of a whole transcript, as it was long when opened, to <paramref name="found"/> (see
    /// <see cref="Entries"/>, which reads the same lines from memory); false when a line is broken. It reads through a
    /// bounded buffer that grows only to the longest line.</summary>
    private static bool EachEntry(string path, bool skipLost, Action<(string? Id, DateTimeOffset At)> found)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1,
            FileOptions.SequentialScan);
        if (stream.Length > MaxTranscriptBytes) throw new InvalidDataException("Transcript too large to compare.");
        var remaining = stream.Length;
        var pooled = ArrayPool<byte>.Shared.Rent(TranscriptChunkBytes);
        var buffer = pooled;
        try
        {
            var filled = 0;
            while (true)
            {
                var read = remaining == 0 ? 0 : stream.Read(buffer, filled, (int)Math.Min(buffer.Length - filled, remaining));
                // The file shrank since it was opened.
                if (read == 0 && remaining > 0) throw new EndOfStreamException();
                remaining -= read;
                filled += read;
                var start = 0;
                for (int end; (end = buffer.AsSpan(start, filled - start).IndexOf((byte)'\n')) >= 0; start += end + 1)
                    if (!Line(buffer.AsSpan(start, end))) return false;
                if (remaining == 0) return start == filled || Line(buffer.AsSpan(start, filled - start));
                // Keep the unfinished line; a line longer than the buffer grows it (only for this read).
                filled -= start;
                if (filled == buffer.Length)
                {
                    var larger = new byte[buffer.Length * 2L > Array.MaxLength ? Array.MaxLength : buffer.Length * 2];
                    buffer.AsSpan(0, filled).CopyTo(larger);
                    buffer = larger;
                }
                else buffer.AsSpan(start, filled).CopyTo(buffer);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(pooled); }

        bool Line(ReadOnlySpan<byte> line)
        {
            if (skipLost && line.Trim(" \t\r\0"u8).IsEmpty) return true;
            if (!TryEntry(line, out var entry)) return false;
            if (entry is { } value) found(value);
            return true;
        }
    }

    /// <summary>Each line's own id and time (top-level <c>uuid</c> and <c>timestamp</c>; nested values do not count).
    /// Blank lines and complete lines without a time are skipped; any other line that is not one complete JSON object
    /// makes the whole read null. Only the fragment a tail read cuts off at the start (<paramref name="partialStart"/>)
    /// is dropped unread, and with <paramref name="skipLost"/> a line of nothing but NUL bytes.</summary>
    private static List<(string? Id, DateTimeOffset At)>? Entries(ReadOnlySpan<byte> data, bool partialStart, bool skipLost = false)
    {
        var entries = new List<(string? Id, DateTimeOffset At)>();
        var start = 0;
        if (partialStart) start = data.IndexOf((byte)'\n') is var first and >= 0 ? first + 1 : data.Length;
        while (start < data.Length)
        {
            var end = data[start..].IndexOf((byte)'\n') is var at and >= 0 ? start + at : data.Length;
            var line = data[start..end];
            start = end + 1;
            if (skipLost && line.Trim(" \t\r\0"u8).IsEmpty) continue;
            if (!TryEntry(line, out var entry)) return null;
            if (entry is { } found) entries.Add(found);
        }
        return entries;
    }

    /// <summary>A line's id and time when it is a turn of the conversation: a message the user wrote, or a model's reply.
    /// What the app or the harness adds on its own — a notice that a background task ended (often when an older state is
    /// merely opened again), the stand-in reply to it (model <c>&lt;synthetic&gt;</c>), meta messages, queue and title
    /// records, attachments — is skipped, so it never makes an older state look continued.</summary>
    private static bool TryEntry(ReadOnlySpan<byte> line, out (string? Id, DateTimeOffset At)? entry)
    {
        entry = null;
        if (line.Trim(" \t\r"u8).IsEmpty) return true;
        try
        {
            var reader = new Utf8JsonReader(line);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
            string? id = null, type = null, origin = null, model = null;
            var meta = false;
            DateTimeOffset? at = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var name = reader.GetString();
                if (!reader.Read()) return false;
                if (name == "uuid" && reader.TokenType == JsonTokenType.String) id = reader.GetString();
                else if (name == "timestamp" && reader.TokenType == JsonTokenType.String && DateTimeOffset.TryParse(reader.GetString(),
                             CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)) at = time;
                else if (name == "type" && reader.TokenType == JsonTokenType.String) type = reader.GetString();
                else if (name == "isMeta" && reader.TokenType == JsonTokenType.True) meta = true;
                else if (name == "origin" && reader.TokenType == JsonTokenType.StartObject) origin = Field(ref reader, "kind"u8);
                else if (name == "message" && reader.TokenType == JsonTokenType.StartObject) model = Field(ref reader, "model"u8);
                else reader.Skip();
            }
            // One complete object and nothing after it.
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read()) return false;
            var turn = type == "user" ? !meta && origin != "task-notification" : type == "assistant" && model != "<synthetic>";
            if (turn && at is { } stamp) entry = (id, stamp);
            return true;
        }
        catch (JsonException) { return false; }
    }

    /// <summary>One string field of the object the reader stands at the start of, leaving it at that object's end.</summary>
    private static string? Field(ref Utf8JsonReader reader, ReadOnlySpan<byte> field)
    {
        string? value = null;
        while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
        {
            var wanted = reader.ValueTextEquals(field);
            if (!reader.Read()) throw new JsonException("Truncated.");
            if (wanted && reader.TokenType == JsonTokenType.String) value = reader.GetString();
            else reader.Skip();
        }
        if (reader.TokenType != JsonTokenType.EndObject) throw new JsonException("Unfinished object.");
        return value;
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

    /// <summary>The unarchived records of one list; <c>Pinned</c> comes from the app's pin list when given.</summary>
    internal static IReadOnlyList<Record> ReadRecords(string directory, IReadOnlySet<string>? pinned = null) =>
        ReadRecords(directory, pinned, out _);

    /// <param name="whole">False when a record file could not be read now (being written, locked or malformed).</param>
    internal static IReadOnlyList<Record> ReadRecords(string directory, IReadOnlySet<string>? pinned, out bool whole) =>
        ReadRecords(directory, pinned, out whole, out _);

    /// <param name="archived">The archived records there.</param>
    internal static IReadOnlyList<Record> ReadRecords(string directory, IReadOnlySet<string>? pinned, out bool whole,
        out IReadOnlyList<Record> archived)
    {
        var shelved = new List<Record>();
        archived = shelved;
        var records = new List<Record>();
        whole = true;
        foreach (var path in Directory.EnumerateFiles(directory, "local_*.json"))
        {
            JsonObject? record;
            DateTime written;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length is <= 0 or > MaxRecordBytes) { whole = false; continue; }
                written = File.GetLastWriteTimeUtc(stream.SafeFileHandle);
                record = JsonNode.Parse(stream) as JsonObject;
            }
            catch (Exception exception) when (exception is FileNotFoundException) { continue; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            { whole = false; continue; }
            if (record is null) { whole = false; continue; }
            var isShelved = record["isArchived"] is JsonValue archivedValue && archivedValue.TryGetValue<bool>(out var isArchived) && isArchived;
            var prior = new List<string>();
            if (record["priorCliSessionIds"] is JsonArray ids)
                foreach (var node in ids)
                    if (node is JsonValue value && value.TryGetValue<string>(out var id)) prior.Add(id.ToLowerInvariant());
            var sessionId = Path.GetFileNameWithoutExtension(path);
            (isShelved ? shelved : records).Add(new(sessionId, Text(record, "title"), pinned?.Contains(sessionId) == true,
                Text(record, "cliSessionId")?.ToLowerInvariant(), prior,
                record["lastActivityAt"] is JsonValue at && at.TryGetValue<long>(out var activity) ? activity : 0,
                Branch: !string.IsNullOrEmpty(Text(record, "forkedFromSessionId")) || record["spawnedFrom"] is JsonObject,
                Renamed: record["previousTitles"] is JsonArray { Count: > 0 }, Parent: ParentOf(record, sessionId),
                Detached: Flag(record, "lineageDetached"), Written: new DateTimeOffset(written, TimeSpan.Zero),
                Former: ParentOf(record, sessionId, detachedToo: true), Model: ModelOf(record), Effort: EffortOf(record),
                Mode: ModeOf(record),
                Scheduled: !string.IsNullOrEmpty(Text(record, "scheduledTaskId")) && !Flag(record, "scheduledRunContinued")));
        }
        return records;
    }

    /// <summary>A record's model as a copy carries it (see <see cref="ClaudeSessionShelf.IsModelCharacter"/>); none
    /// otherwise.</summary>
    private static string ModelOf(JsonObject record) =>
        Text(record, "model") is { Length: > 0 and <= 80 } model && model.All(ClaudeSessionShelf.IsModelCharacter) ? model : "";

    /// <summary>A record's own effort level; none when it is the default's (<c>effortInherited</c>) or unfamiliar.</summary>
    private static string EffortOf(JsonObject record) =>
        !Flag(record, "effortInherited") && Text(record, "effort") is { } effort && ClaudeSessionShelf.EffortLevels.Contains(effort) ? effort : "";

    /// <summary>A record's permission mode: unknown while it passes through plan mode, none when absent or unfamiliar.</summary>
    private static string? ModeOf(JsonObject record) => Text(record, "permissionMode") switch
    {
        "plan" => null,
        { } mode when mode is DefaultMode or "acceptEdits" or "auto" or "bypassPermissions" => mode,
        _ => ""
    };

    /// <summary>The session a record's lineage links it to, as the app reads it (Claude 3.1623): none once the user moved
    /// it to top level (<c>lineageDetached</c>); else the session that started it, when Claude started it as a side
    /// session and it was not forked from another; else the session it was forked from; else the session that started it
    /// (from a suggestion, say). A link to itself is none. The widget's copies carry no link. With
    /// <paramref name="detachedToo"/>, the link it had before the user moved it to top level.</summary>
    private static string? ParentOf(JsonObject record, string sessionId, bool detachedToo = false)
    {
        if (!detachedToo && Flag(record, "lineageDetached")) return null;
        var spawned = record["spawnedFrom"] as JsonObject;
        var starter = spawned is null ? null : Text(spawned, "sessionId");
        if (string.IsNullOrEmpty(starter) || starter == sessionId) starter = null;
        var forked = Text(record, "forkedFromSessionId");
        if (string.IsNullOrEmpty(forked) || forked == sessionId) forked = null;
        // A side session that took its starter's conversation is also forked from it; when that starter is deleted, the app
        // moves it under the next session up, and the fork still names the deleted one.
        if (starter is not null && Flag(spawned!, "sideSession") && !Flag(spawned!, "userFork")
            && (forked is null || forked == starter || spawned!["deletedParent"] is JsonObject deleted && forked == Text(deleted, "sessionId")))
            return starter;
        return forked ?? starter;
    }

    private static string? Text(JsonObject record, string field) =>
        record[field] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static bool Flag(JsonObject record, string field) =>
        record[field] is JsonValue value && value.TryGetValue<bool>(out var flag) && flag;

    private static int Print(string plan, int exitCode)
    {
        using var output = Console.OpenStandardOutput();
        output.Write(Encoding.UTF8.GetBytes(plan));
        output.Write("\n"u8);
        return exitCode;
    }

    private static int Print(Plan plan, int exitCode)
    {
        using var output = Console.OpenStandardOutput();
        output.Write(JsonSerializer.SerializeToUtf8Bytes(plan, PlanJson));
        output.Write("\n"u8);
        return exitCode;
    }
}
