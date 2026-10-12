using System.Globalization;
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
/// Plans how to make the signed-in account's Code sidebar match the reference account's, as the app shows them: the
/// same titles, pins and groups for every conversation both lists hold. Everything is read from disk, read-only, while
/// the app runs: titles from each account's session records, pins from the web UI's pin list (see
/// <see cref="ClaudeSidebarPins"/>), groups from its <c>dframe-store</c> in localStorage (<c>customGroupsByScope</c>,
/// keyed by account/organization). A conversation is matched by its current transcript id, or as a continuation of the
/// other side's.
/// <para>A conversation continued in another account comes back as a second record beside its older state. The plan
/// lists such older states for archiving (reversible) and gives their pins and groups to the newest state. Nothing is
/// deleted; sessions and groups only one side has are otherwise left alone.</para>
/// </summary>
internal static class ClaudeSessionSync
{
    internal const string CommandFlag = "--claude-session-plan";
    private const long MaxRecordBytes = 10 * 1024 * 1024;
    private const int TranscriptTailBytes = 4 * 1024 * 1024;
    private const long MaxTranscriptBytes = 512L * 1024 * 1024;
    private const int MaxSettleSeconds = 120;
    // An entry this close after the next transcript began is still the hand-over, not a branch.
    private static readonly TimeSpan HandOverSlack = TimeSpan.FromSeconds(2);
    private const string GroupsKey = "dframe-store";
    private static readonly JsonSerializerOptions PlanJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping, WriteIndented = true
    };

    /// <param name="Branch">A fork or a session spawned from another: a conversation of its own, never a newer state.</param>
    internal sealed record Record(string SessionId, string? Title, bool Pinned, string? Cli, IReadOnlyList<string> Prior,
        long LastActivityAt, bool Branch = false);
    internal sealed record Group(string Id, string Name);
    internal sealed record Layout(IReadOnlyList<Group> Groups, IReadOnlyDictionary<string, string> Assignments);
    internal sealed record Move(string? Group, IReadOnlyList<string> Sessions);
    internal sealed record Pin(string Session, bool Pinned, string? Title);
    internal sealed record Rename(string Session, string Title, string? From);
    /// <summary>An older state to archive once <c>Ready</c>: first its newest state must hold the pin and group it keeps
    /// (the plan's moves and pins). A pinned one is unpinned just before archiving, since the app does not archive pinned
    /// sessions. <c>LastWritten</c> is its last entry, <c>MovedOn</c> when the conversation went on in a new transcript.</summary>
    internal sealed record Retire(string Session, string? Title, bool Pinned, string Newest, string? NewestTitle, bool Ready,
        DateTimeOffset LastWritten, DateTimeOffset MovedOn);
    internal sealed record GroupView(string Name, IReadOnlyList<string> Sessions);
    /// <summary>An account's sidebar as the app shows it, by title: the pinned sessions, then each group's others.
    /// A null list could not be read.</summary>
    internal sealed record Sidebar(string Account, string? Label, IReadOnlyList<string>? Pinned, IReadOnlyList<GroupView>? Groups);
    /// <summary>When a transcript file was created and last written, and its newest entry (null when it has none). A
    /// file created after it was last written was copied or restored, so its creation time dates nothing.</summary>
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
        internal static Sources Default()
        {
            var paths = ClaudeShelfPaths.CreateDefault();
            var app = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
            return new(paths.SessionsRoot, Path.Combine(app, "Local Storage", "leveldb"),
                Path.Combine(app, "IndexedDB", "https_claude.ai_0.indexeddb.leveldb"), paths.ProjectsRoot, paths.LedgerDirectory);
        }
    }

    /// <summary>Status is <c>ok</c> when a plan was made (possibly empty), <c>is_reference</c> when this account is the
    /// reference and the plan only clears older states; otherwise it says why nothing was planned. <c>OnlyHere</c> and
    /// <c>OnlyThere</c> are the titles of conversations only this account or only the reference lists.</summary>
    internal sealed record Plan(string Status, string? Current = null, string? CurrentLabel = null, string? Reference = null,
        string? ReferenceLabel = null, IReadOnlyList<Retire>? Archive = null, IReadOnlyList<string>? CreateGroups = null,
        IReadOnlyList<Move>? Moves = null, IReadOnlyList<Pin>? Pins = null, IReadOnlyList<Rename>? Renames = null,
        int? Matched = null, int? Unmatched = null, IReadOnlyList<string>? OnlyHere = null, IReadOnlyList<string>? OnlyThere = null,
        IReadOnlyList<Sidebar>? Sidebars = null, IReadOnlyList<string>? Warnings = null);

    internal static bool IsAccountKey(string? key) =>
        key?.Split('/') is [var account, var org] && Guid.TryParse(account, out _) && Guid.TryParse(org, out _);

    /// <summary><c>--claude-session-plan --session local_… [--reference account/org] [--settle seconds]</c>: prints the
    /// plan as UTF-8 JSON. <c>--settle</c> waits first, for checking changes the app has not written to disk yet.</summary>
    internal static int RunCommand(string[] args)
    {
        string? session = null, reference = null;
        var settle = 0;
        for (var index = 0; index < args.Length; index++)
        {
            if (args[index] == "--session" && index + 1 < args.Length) session = args[++index];
            else if (args[index] == "--reference" && index + 1 < args.Length) reference = args[++index];
            else if (args[index] == "--settle" && index + 1 < args.Length
                     && int.TryParse(args[++index], NumberStyles.None, CultureInfo.InvariantCulture, out settle)
                     && settle <= MaxSettleSeconds) { }
            else return Print(new Plan("bad_arguments"), 2);
        }
        if (session is null || !session.StartsWith("local_", StringComparison.Ordinal)
            || session.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || reference is not null && !IsAccountKey(reference))
            return Print(new Plan("bad_arguments"), 2);
        // Pins reach the disk within a second of a change; groups within seconds, up to about a minute when the app
        // writes its local storage often.
        if (settle > 0) Thread.Sleep(TimeSpan.FromSeconds(settle));
        return Print(Build(Sources.Default(), session, reference), 0);
    }

    internal static Plan Build(Sources sources, string sessionId, string? referenceOverride = null)
    {
        var marker = ClaudeSessionReference.Load(sources.Ledger);
        var labels = marker?.Labels ?? new Dictionary<string, string>();
        string? Label(string? key) => key is not null && labels.TryGetValue(key, out var label) ? label : null;
        var current = FindAccount(sources.SessionsRoot, sessionId);
        if (current is null) return new("unknown_session");
        var reference = (referenceOverride ?? marker?.Account)?.ToLowerInvariant();
        if (reference is null) return new("no_reference", current.Value.Key, Label(current.Value.Key));
        var isReference = reference == current.Value.Key;
        if ((isReference ? current.Value.Directory : AccountDirectory(sources.SessionsRoot, reference)) is not { } referenceDirectory)
            return new("reference_missing", current.Value.Key, Label(current.Value.Key), reference, Label(reference));
        var warnings = new List<string>();
        JsonObject? scopes = null;
        try { scopes = ReadGroupScopes(sources.LocalStorage); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                                              or JsonException or InvalidOperationException)
        { scopes = null; }
        // The reference's groups must be confirmed: read as empty, they would ungroup this account. This account
        // having no scope yet only means it has no groups, and planning for it only adds.
        var currentLayout = LayoutOf(scopes, current.Value.Key, missingIsEmpty: true);
        var referenceLayout = isReference ? currentLayout : LayoutOf(scopes, reference, missingIsEmpty: false);
        if (currentLayout is null || referenceLayout is null) { currentLayout = referenceLayout = null; warnings.Add("groups_unreadable"); }
        IReadOnlySet<string>? pinned = null;
        try { pinned = ClaudeSidebarPins.Read(sources.IndexedDb); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException
                                              or JsonException or InvalidOperationException)
        { warnings.Add("pins_unreadable"); }
        var lineage = new Lineage(sessionId, cli => Transcript(sources.ProjectsRoot, cli),
            (older, next) => HandOver(sources.ProjectsRoot, older, next));
        var currentRecords = ReadRecords(current.Value.Directory, pinned);
        return Compute(current.Value.Key, Label(current.Value.Key), reference, Label(reference), currentRecords,
            isReference ? currentRecords : ReadRecords(referenceDirectory, pinned), currentLayout, referenceLayout,
            pinned is not null, lineage, warnings);
    }

    /// <summary>
    /// The changes, in the order they must run: create the missing groups, move sessions (moving a pinned session into a
    /// group unpins it, so pins are planned after the moves), set pins, rename, and only then archive the older states that
    /// are ready. Each conversation follows the reference as its sidebar shows it, older states folded into the newest;
    /// one the reference does not list keeps what its own older states here had. Groups are planned only when the pins
    /// and both accounts' groups could be read; pins only when the pins could be read, and not while the reference has
    /// none but this account has some. An older state is ready when all that is known and its newest state already holds
    /// what it keeps, so stopping part way never loses a pin or a group. In the reference account itself only its older
    /// states are cleared.
    /// </summary>
    internal static Plan Compute(string current, string? currentLabel, string reference, string? referenceLabel,
        IReadOnlyList<Record> currentRecords, IReadOnlyList<Record> referenceRecords, Layout? currentLayout,
        Layout? referenceLayout, bool pinsKnown = true, Lineage? lineage = null, IReadOnlyList<string>? warnings = null)
    {
        var notes = warnings?.ToList() ?? [];
        var self = reference == current;
        // This account's older states are archived and the reference's are seen through: either way the newest state
        // of the conversation holds their pin and group.
        var retiring = lineage is null ? new Dictionary<string, Older>(StringComparer.Ordinal) : OlderStates(currentRecords, lineage);
        var seenThrough = self || lineage is null ? retiring : OlderStates(referenceRecords, lineage);
        var (sources, sourceLayout) = Inherit(referenceRecords, referenceLayout, seenThrough);
        var (own, ownLayout) = Inherit(currentRecords, currentLayout, retiring);
        var ownById = own.ToDictionary(record => record.SessionId, StringComparer.Ordinal);
        var heads = retiring.Values.Select(older => older.Newest.SessionId).ToHashSet(StringComparer.Ordinal);
        var candidates = currentRecords.Where(record => !retiring.ContainsKey(record.SessionId)).ToArray();

        var byCli = sources.Where(record => record.Cli is not null).ToLookup(record => record.Cli!, StringComparer.Ordinal);
        var byPrior = sources.SelectMany(record => record.Prior.Select(id => (Id: id, Record: record)))
            .ToLookup(pair => pair.Id, pair => pair.Record, StringComparer.Ordinal);
        // One reference record serves one session here. The same transcript id is matched first; a continuation
        // either way (one side lists the other's current transcript as earlier) only takes a record still free,
        // so an older state or a branch of a conversation that is already matched is left alone.
        var newest = candidates.OrderByDescending(record => record.LastActivityAt).ToArray();
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

        // A reference without a single pin while this account has some looks like a wiped pin list: keep the pins.
        var pinPlan = pinsKnown && (sources.Any(record => record.Pinned) || !candidates.Any(record => record.Pinned));
        if (pinsKnown && !pinPlan) notes.Add("reference_pins_empty");
        // Moves can unpin, so groups wait for readable pins too.
        var groups = pinsKnown && currentLayout is not null && sourceLayout is not null;
        var existing = currentLayout?.Groups.Select(group => group.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        var create = groups ? sourceLayout!.Groups.Select(group => group.Name)
            .Where(name => !existing.Contains(name)).Distinct(StringComparer.Ordinal).ToList() : [];
        var into = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var ungroup = new List<string>();
        var pins = new List<Pin>();
        var renames = new List<Rename>();
        var targets = new Dictionary<string, (Record Source, Layout? Layout)>(StringComparer.Ordinal);
        int matched = 0, unmatched = 0;
        foreach (var record in newest)
        {
            Record source;
            Layout? layout;
            var isMatch = matches.TryGetValue(record.SessionId, out var match);
            if (isMatch) { matched++; (source, layout) = (match!, sourceLayout); }
            else
            {
                unmatched++;
                // A conversation the reference does not list keeps what its older states here had.
                if (!heads.Contains(record.SessionId)) continue;
                (source, layout) = (ownById[record.SessionId], ownLayout);
            }
            targets[record.SessionId] = (source, layout);
            var pinned = record.Pinned;
            if (groups && GroupOf(layout!, source.SessionId) is { Known: true, Name: var target }
                && GroupOf(currentLayout!, record.SessionId) is { Known: true, Name: var now } && target != now
                // Without a pin plan to restore it, a pinned session is not moved into a group.
                && (target is null || !record.Pinned || pinPlan))
            {
                if (target is null) ungroup.Add(record.SessionId);
                else
                {
                    if (!into.TryGetValue(target, out var sessions)) into[target] = sessions = [];
                    sessions.Add(record.SessionId);
                    pinned = false;
                }
            }
            if (pinPlan && source.Pinned != pinned) pins.Add(new(record.SessionId, source.Pinned, source.Title ?? record.Title));
            if (isMatch && !string.IsNullOrWhiteSpace(source.Title) && source.Title != record.Title)
                renames.Add(new(record.SessionId, source.Title, record.Title));
        }
        var moves = into.Select(pair => new Move(pair.Key, pair.Value)).ToList();
        if (ungroup.Count > 0) moves.Add(new Move(null, ungroup));
        // An older state goes only once what it leaves is known and already in place on its newest state: its own group,
        // the group and pin the newest state should have, and the newest state's own, all read and matching.
        bool Ready(Record old, Record head) =>
            pinPlan && groups && targets.TryGetValue(head.SessionId, out var target) && target.Layout is { } layout
            && GroupOf(currentLayout!, old.SessionId).Known
            && GroupOf(layout, target.Source.SessionId) is { Known: true, Name: var want }
            && GroupOf(currentLayout!, head.SessionId) is { Known: true, Name: var have } && want == have
            && target.Source.Pinned == head.Pinned;
        var byId = currentRecords.ToDictionary(record => record.SessionId, StringComparer.Ordinal);
        var archive = currentRecords.Where(record => retiring.ContainsKey(record.SessionId)).Select(record =>
            {
                var older = retiring[record.SessionId];
                return new Retire(record.SessionId, record.Title, pinsKnown && record.Pinned, older.Newest.SessionId,
                    older.Newest.Title, Ready(record, byId[older.Newest.SessionId]), older.LastWritten, older.MovedOn);
            })
            .OrderBy(retire => retire.Title, StringComparer.Ordinal).ToList();
        var sidebars = new List<Sidebar> { View(current, currentLabel, currentRecords, currentLayout, pinsKnown) };
        if (!self) sidebars.Add(View(reference, referenceLabel, referenceRecords, referenceLayout, pinsKnown));
        // Matching an account to itself has nothing worth reporting.
        IReadOnlyList<string>? Titles(IEnumerable<Record> records) =>
            self ? null : records.Select(Name).Order(StringComparer.Ordinal).ToArray();
        return new(self ? "is_reference" : "ok", current, currentLabel, reference, referenceLabel, archive, create, moves,
            pins, renames, self ? null : matched, self ? null : unmatched,
            Titles(newest.Where(record => !matches.ContainsKey(record.SessionId))),
            Titles(sources.Where(record => !claimed.Contains(record.SessionId))), sidebars, notes is { Count: > 0 } ? notes : null);
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
    internal static Dictionary<string, Older> OlderStates(IReadOnlyList<Record> records, Lineage lineage)
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
        foreach (var record in records)
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

    /// <summary>The transcript a record lists right after <paramref name="cli"/>, its current one last.</summary>
    private static string? NextAfter(Record record, string cli)
    {
        var order = record.Prior.Append(record.Cli).OfType<string>().ToList();
        var at = order.IndexOf(cli);
        return at >= 0 && at + 1 < order.Count ? order[at + 1] : null;
    }

    /// <summary>The records without their older states. The newest state takes an older state's pin, and its group
    /// when it has none (the most recently active older state first).</summary>
    private static (IReadOnlyList<Record> Records, Layout? Layout) Inherit(IReadOnlyList<Record> records, Layout? layout,
        IReadOnlyDictionary<string, Older> older)
    {
        if (older.Count == 0) return (records, layout);
        var byId = records.ToDictionary(record => record.SessionId, StringComparer.Ordinal);
        var pairs = older.Select(pair => (Old: byId[pair.Key], Newest: pair.Value.Newest))
            .OrderByDescending(pair => pair.Old.LastActivityAt).ToArray();
        var pinned = pairs.Where(pair => pair.Old.Pinned).Select(pair => pair.Newest.SessionId).ToHashSet(StringComparer.Ordinal);
        var kept = records.Where(record => !older.ContainsKey(record.SessionId))
            .Select(record => !record.Pinned && pinned.Contains(record.SessionId) ? record with { Pinned = true } : record).ToArray();
        if (layout is null) return (kept, null);
        var assignments = layout.Assignments.Where(pair => !older.ContainsKey(pair.Key))
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        foreach (var (old, newest) in pairs)
            if (!assignments.ContainsKey(newest.SessionId) && layout.Assignments.TryGetValue(old.SessionId, out var group))
                assignments[newest.SessionId] = group;
        return (kept, layout with { Assignments = assignments });
    }

    private static string Name(Record record) => string.IsNullOrWhiteSpace(record.Title) ? record.SessionId : record.Title;

    private static Sidebar View(string account, string? label, IReadOnlyList<Record> records, Layout? layout, bool pinsKnown)
    {
        var pinned = pinsKnown ? records.Where(record => record.Pinned).Select(Name).Order(StringComparer.Ordinal).ToArray() : null;
        // A pinned session keeps its group but is shown under Pinned only.
        if (layout is null) return new(account, label, pinned, null);
        var groups = layout.Groups.Select(group => new GroupView(group.Name, records
                .Where(record => !(pinsKnown && record.Pinned)
                                 && layout.Assignments.TryGetValue(record.SessionId, out var id) && id == group.Id)
                .Select(Name).Order(StringComparer.Ordinal).ToArray()))
            .OrderBy(group => group.Name, StringComparer.Ordinal).ToArray();
        return new(account, label, pinned, groups);
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
            var tail = new byte[(int)Math.Min(stream.Length, TranscriptTailBytes)];
            stream.Seek(-tail.Length, SeekOrigin.End);
            stream.ReadExactly(tail);
            if (Entries(tail, partialStart: tail.Length < stream.Length) is not { } entries) return null;
            return new(created, written, entries.Count == 0 ? null : entries.Max(entry => entry.At));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    /// <summary>When the conversation in transcript <paramref name="older"/> went on in <paramref name="next"/>: the oldest
    /// entry of the later transcript that the earlier one does not hold. Null when either cannot be read, has a broken
    /// line, or the later one holds nothing new.</summary>
    internal static DateTimeOffset? HandOver(string projectsRoot, string older, string next)
    {
        if (Locate(projectsRoot, older) is not { } olderPath || Locate(projectsRoot, next) is not { } nextPath) return null;
        try
        {
            if (Entries(ReadWhole(olderPath), partialStart: false) is not { } earlier
                || Entries(ReadWhole(nextPath), partialStart: false) is not { } later) return null;
            var known = earlier.Select(entry => entry.Id).OfType<string>().ToHashSet(StringComparer.Ordinal);
            return later.Where(entry => entry.Id is { } id && !known.Contains(id)).Select(entry => (DateTimeOffset?)entry.At).Min();
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        { return null; }
    }

    private static string? Locate(string projectsRoot, string cli) =>
        !Guid.TryParse(cli, out _) || !Directory.Exists(projectsRoot) ? null
        : Directory.EnumerateDirectories(projectsRoot).Select(project => Path.Combine(project, cli + ".jsonl")).FirstOrDefault(File.Exists);

    private static byte[] ReadWhole(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (stream.Length > MaxTranscriptBytes) throw new InvalidDataException("Transcript too large to compare.");
        var bytes = new byte[stream.Length];
        stream.ReadExactly(bytes);
        return bytes;
    }

    /// <summary>Each line's own id and time (top-level <c>uuid</c> and <c>timestamp</c>; nested values do not count).
    /// Blank lines and complete lines without a time are skipped; any other line that is not one complete JSON object
    /// makes the whole read null. Only the fragment a tail read cuts off at the start (<paramref name="partialStart"/>)
    /// is dropped unread.</summary>
    private static List<(string? Id, DateTimeOffset At)>? Entries(byte[] data, bool partialStart)
    {
        var entries = new List<(string? Id, DateTimeOffset At)>();
        var start = 0;
        if (partialStart) start = Array.IndexOf(data, (byte)'\n') is var first and >= 0 ? first + 1 : data.Length;
        while (start < data.Length)
        {
            var end = Array.IndexOf(data, (byte)'\n', start);
            if (end < 0) end = data.Length;
            if (!TryEntry(data.AsSpan(start, end - start), out var entry)) return null;
            if (entry is { } found) entries.Add(found);
            start = end + 1;
        }
        return entries;
    }

    private static bool TryEntry(ReadOnlySpan<byte> line, out (string? Id, DateTimeOffset At)? entry)
    {
        entry = null;
        if (line.Trim(" \t\r"u8).IsEmpty) return true;
        try
        {
            var reader = new Utf8JsonReader(line);
            if (!reader.Read() || reader.TokenType != JsonTokenType.StartObject) return false;
            string? id = null;
            DateTimeOffset? at = null;
            while (reader.Read() && reader.TokenType == JsonTokenType.PropertyName)
            {
                var isId = reader.ValueTextEquals("uuid"u8);
                var isAt = reader.ValueTextEquals("timestamp"u8);
                if (!reader.Read()) return false;
                if (isId && reader.TokenType == JsonTokenType.String) id = reader.GetString();
                else if (isAt && reader.TokenType == JsonTokenType.String && DateTimeOffset.TryParse(reader.GetString(),
                             CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time)) at = time;
                else reader.Skip();
            }
            // One complete object and nothing after it.
            if (reader.TokenType != JsonTokenType.EndObject || reader.Read()) return false;
            if (at is { } stamp) entry = (id, stamp);
            return true;
        }
        catch (JsonException) { return false; }
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
    internal static IReadOnlyList<Record> ReadRecords(string directory, IReadOnlySet<string>? pinned = null)
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
            var sessionId = Path.GetFileNameWithoutExtension(path);
            records.Add(new(sessionId, Text(record, "title"), pinned?.Contains(sessionId) == true,
                Text(record, "cliSessionId")?.ToLowerInvariant(), prior,
                record["lastActivityAt"] is JsonValue at && at.TryGetValue<long>(out var activity) ? activity : 0,
                Branch: !string.IsNullOrEmpty(Text(record, "forkedFromSessionId")) || record["spawnedFrom"] is JsonObject));
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
