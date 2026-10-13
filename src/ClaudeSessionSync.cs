using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace WeeklyUsageIndicator;

/// <summary>
/// The old session layout reference: the account whose sidebar the others used to copy, chosen on the account page. It
/// is read only when the widget first keeps <see cref="ClaudeSessionLayout"/>: its layout then becomes what every other
/// account last agreed on, so each one differs from it only by what it changed itself.
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
/// What the accounts' Code sidebars last agreed on, per pair of accounts and per conversation: the pin, the group and
/// the title both showed alike. A plan reads it to tell a change another account made (a value that differs from what
/// the two last agreed on) from a value that account merely never received. It also keeps the mapped accounts' labels
/// and when the app last left each one, which settles changes that disagree. Only the widget writes it: after a plan ran
/// in an account (<see cref="ClaudeSessionSync.PlannedEventName"/>) it records what that account now shows alike with
/// each other one, and the first time it starts from the old reference (<see cref="ClaudeSessionReference"/>).
/// </summary>
internal sealed class ClaudeSessionLayout
{
    internal const string FileName = "session-layout.json";
    private const long MaxBytes = 32 * 1024 * 1024;

    /// <summary>One side's agreed values for one conversation. A null field, or <c>HasGroup</c> false, was never agreed;
    /// <c>Group</c> null with <c>HasGroup</c> is "in no group".</summary>
    internal sealed record Agreed(bool? Pinned = null, bool HasGroup = false, string? Group = null, string? Title = null)
    {
        /// <summary>This, with the fields it lacks taken from <paramref name="older"/>.</summary>
        internal Agreed Over(Agreed? older) => older is null ? this
            : new(Pinned ?? older.Pinned, HasGroup || older.HasGroup, HasGroup ? Group : older.Group, Title ?? older.Title);
    }

    internal sealed record Account(string Label, DateTimeOffset? LeftAt);

    /// <summary>The mapped accounts by account/organization key.</summary>
    internal Dictionary<string, Account> Accounts { get; } = new(StringComparer.Ordinal);
    // "a|b" (in ordinal order) → account → its session id → agreed values.
    private readonly Dictionary<string, Dictionary<string, Dictionary<string, Agreed>>> _pairs = new(StringComparer.Ordinal);

    private static string PairKey(string a, string b) => string.CompareOrdinal(a, b) <= 0 ? a + "|" + b : b + "|" + a;

    /// <summary><paramref name="account"/>'s side of what it agreed on with <paramref name="with"/>, by its own session
    /// ids; null when the two never agreed on anything.</summary>
    internal IReadOnlyDictionary<string, Agreed>? Side(string account, string with) =>
        _pairs.TryGetValue(PairKey(account, with), out var pair) && pair.TryGetValue(account, out var side) ? side : null;

    /// <summary>Records what two accounts now show alike, field by field over what they agreed on before, and forgets
    /// the sessions their lists no longer hold.</summary>
    internal void Record(ClaudeSessionSync.Agreement agreement)
    {
        var key = PairKey(agreement.Account, agreement.Other);
        if (!_pairs.TryGetValue(key, out var pair)) _pairs[key] = pair = new(StringComparer.Ordinal);
        Merge(agreement.Account, agreement.Mine, agreement.MyIds);
        Merge(agreement.Other, agreement.Theirs, agreement.TheirIds);

        void Merge(string account, IReadOnlyDictionary<string, Agreed> agreed, IReadOnlySet<string> ids)
        {
            if (!pair.TryGetValue(account, out var side)) pair[account] = side = new(StringComparer.Ordinal);
            foreach (var id in side.Keys.Where(id => !ids.Contains(id)).ToArray()) side.Remove(id);
            foreach (var (id, value) in agreed) side[id] = value.Over(side.GetValueOrDefault(id));
        }
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
                || root["accounts"] is not JsonObject accounts || root["pairs"] is not JsonObject pairs) return (null, true);
            var layout = new ClaudeSessionLayout();
            foreach (var (key, node) in accounts)
            {
                if (!ClaudeSessionSync.IsAccountKey(key) || node is not JsonObject account || Text(account, "label") is not { } label)
                    return (null, true);
                DateTimeOffset? leftAt = null;
                if (account["leftAt"] is not null)
                {
                    if (!DateTimeOffset.TryParse(Text(account, "leftAt"), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind,
                            out var at)) return (null, true);
                    leftAt = at;
                }
                layout.Accounts[key.ToLowerInvariant()] = new(label, leftAt);
            }
            foreach (var (key, node) in pairs)
            {
                if (key.Split('|') is not [var a, var b] || !ClaudeSessionSync.IsAccountKey(a) || !ClaudeSessionSync.IsAccountKey(b)
                    || node is not JsonObject sides) return (null, true);
                var pair = new Dictionary<string, Dictionary<string, Agreed>>(StringComparer.Ordinal);
                foreach (var (account, sideNode) in sides)
                {
                    var owner = account.ToLowerInvariant();
                    if (owner != a.ToLowerInvariant() && owner != b.ToLowerInvariant() || sideNode is not JsonObject entries)
                        return (null, true);
                    var side = new Dictionary<string, Agreed>(StringComparer.Ordinal);
                    foreach (var (id, entryNode) in entries)
                    {
                        if (entryNode is not JsonObject entry || ReadAgreed(entry) is not { } agreed) return (null, true);
                        side[id] = agreed;
                    }
                    pair[owner] = side;
                }
                layout._pairs[PairKey(a.ToLowerInvariant(), b.ToLowerInvariant())] = pair;
            }
            return (layout, true);
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException) { return (null, false); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException
                                              or InvalidOperationException or FormatException)
        { return (null, true); }
    }

    private static Agreed? ReadAgreed(JsonObject entry)
    {
        bool? pinned = null; var hasGroup = false; string? group = null; string? title = null;
        foreach (var (field, value) in entry)
        {
            if (field == "pinned" && value is JsonValue pin && pin.TryGetValue<bool>(out var isPinned)) pinned = isPinned;
            else if (field == "group" && value is null) hasGroup = true;
            else if (field == "group" && value is JsonValue name && name.TryGetValue<string>(out var groupName))
            { hasGroup = true; group = groupName; }
            else if (field == "title" && value is JsonValue text && text.TryGetValue<string>(out var titleText)) title = titleText;
            else return null;
        }
        return new(pinned, hasGroup, group, title);
    }

    internal void Save(string ledgerDirectory)
    {
        Directory.CreateDirectory(ledgerDirectory);
        var accounts = new JsonObject();
        foreach (var (key, account) in Accounts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            accounts[key] = new JsonObject
            {
                ["label"] = account.Label, ["leftAt"] = account.LeftAt?.ToString("O", CultureInfo.InvariantCulture)
            };
        var pairs = new JsonObject();
        foreach (var (key, pair) in _pairs.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            var sides = new JsonObject();
            foreach (var (account, side) in pair.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var entries = new JsonObject();
                foreach (var (id, agreed) in side.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                {
                    var entry = new JsonObject();
                    if (agreed.Pinned is { } pinned) entry["pinned"] = pinned;
                    if (agreed.HasGroup) entry["group"] = agreed.Group;
                    if (agreed.Title is { } title) entry["title"] = title;
                    entries[id] = entry;
                }
                sides[account] = entries;
            }
            pairs[key] = sides;
        }
        var root = new JsonObject { ["version"] = 1, ["accounts"] = accounts, ["pairs"] = pairs };
        var path = Path.Combine(ledgerDirectory, FileName);
        var partial = path + ".partial";
        File.WriteAllText(partial, root.ToJsonString(new JsonSerializerOptions
        { WriteIndented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }), new UTF8Encoding(false));
        File.Move(partial, path, overwrite: true);
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
/// Plans how to bring the signed-in account's Code sidebar up to date with what was changed in the other accounts, as
/// the app shows them: the titles, pins and groups of the conversations the lists share. Everything is read from disk,
/// read-only, while the app runs: titles from each account's session records, pins from the web UI's pin list (see
/// <see cref="ClaudeSidebarPins"/>), groups from its <c>dframe-store</c> in localStorage (<c>customGroupsByScope</c>,
/// keyed by account/organization), and what the accounts last agreed on from the widget (see
/// <see cref="ClaudeSessionLayout"/>). A conversation is matched by its current transcript id, or as a continuation of
/// the other side's.
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
    /// <param name="Renamed">The app recorded an earlier title (<c>previousTitles</c>): someone renamed it in this list.</param>
    internal sealed record Record(string SessionId, string? Title, bool Pinned, string? Cli, IReadOnlyList<string> Prior,
        long LastActivityAt, bool Branch = false, bool Renamed = false);
    internal sealed record Group(string Id, string Name);
    internal sealed record Layout(IReadOnlyList<Group> Groups, IReadOnlyDictionary<string, string> Assignments);
    internal sealed record Move(string? Group, IReadOnlyList<string> Sessions);
    /// <param name="By">The account whose change this is; null when the session only takes its older state's pin.</param>
    internal sealed record Pin(string Session, bool Pinned, string? Title, string? By = null);
    internal sealed record Rename(string Session, string Title, string? From, string? By = null);
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
        internal static Sources Default() => For(ClaudeShelfPaths.CreateDefault());

        /// <summary>The app's own stores beside the given lists and ledger.</summary>
        internal static Sources For(ClaudeShelfPaths paths)
        {
            var app = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude");
            return new(paths.SessionsRoot, Path.Combine(app, "Local Storage", "leveldb"),
                Path.Combine(app, "IndexedDB", "https_claude.ai_0.indexeddb.leveldb"), paths.ProjectsRoot, paths.LedgerDirectory);
        }
    }

    /// <summary>Another account to take changes from: its list and its groups (null when they cannot be confirmed), the
    /// widget's copies in its list (null when they cannot be told), its side of what it agreed on with this account (null
    /// when the two never agreed) and with each further account, and when the app last left it.</summary>
    /// <param name="Mine">This account's side of what the two agreed on (null when they never agreed).</param>
    /// <param name="CopyTitles">The title the widget last wrote to each of its copies there, where tracked.</param>
    internal sealed record Other(string Account, string? Label, DateTimeOffset? LeftAt, IReadOnlyList<Record> Records,
        Layout? Layout, IReadOnlySet<string>? Copies, IReadOnlyDictionary<string, ClaudeSessionLayout.Agreed>? Agreed,
        IReadOnlyList<IReadOnlyDictionary<string, ClaudeSessionLayout.Agreed>>? Elsewhere = null,
        IReadOnlyDictionary<string, ClaudeSessionLayout.Agreed>? Mine = null, IReadOnlyDictionary<string, string?>? CopyTitles = null);

    /// <summary>An account the plan took changes from: how many conversations here it lists, and the titles of those
    /// only it lists.</summary>
    internal sealed record OtherView(string Account, string? Label, DateTimeOffset? LeftAt, int Matched, IReadOnlyList<string> OnlyThere);

    /// <summary>Status is <c>ok</c> when a plan was made (possibly empty); otherwise it says why nothing was planned.
    /// <c>OnlyHere</c> are the titles of conversations no other account lists.</summary>
    internal sealed record Plan(string Status, string? Current = null, string? CurrentLabel = null,
        IReadOnlyList<OtherView>? Others = null, IReadOnlyList<Retire>? Archive = null, IReadOnlyList<string>? CreateGroups = null,
        IReadOnlyList<Move>? Moves = null, IReadOnlyList<Pin>? Pins = null, IReadOnlyList<Rename>? Renames = null,
        IReadOnlyList<string>? OnlyHere = null, IReadOnlyList<Sidebar>? Sidebars = null, IReadOnlyList<string>? Warnings = null);

    /// <summary>What <c>Account</c> and <c>Other</c> show alike, by each side's session ids, and every session id each
    /// list holds.</summary>
    internal sealed record Agreement(string Account, string Other, IReadOnlyDictionary<string, ClaudeSessionLayout.Agreed> Mine,
        IReadOnlyDictionary<string, ClaudeSessionLayout.Agreed> Theirs, IReadOnlySet<string> MyIds, IReadOnlySet<string> TheirIds);

    internal static bool IsAccountKey(string? key) =>
        key?.Split('/') is [var account, var org] && Guid.TryParse(account, out _) && Guid.TryParse(org, out _);

    /// <summary><c>--claude-session-plan --session local_… [--only account/org] [--settle seconds]</c>: prints the plan as
    /// UTF-8 JSON. <c>--settle</c> waits first, for checking changes the app has not written to disk yet. <c>--only</c>
    /// takes changes from that account alone, for diagnosis.</summary>
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
        var plan = Build(Sources.Default(), session, only?.ToLowerInvariant());
        // The widget then records what this account shows alike with the others. A plan limited to one account is for
        // diagnosis and tells it nothing.
        if (only is null && plan is { Status: "ok", Current: { } current }) SignalPlanned(current);
        return Print(plan, 0);
    }

    /// <summary>The widget's event for a plan that ran in an account: it then records what that account shows alike
    /// with each other one (see <see cref="ClaudeSessionLayout"/>).</summary>
    internal static string PlannedEventName(string accountKey) =>
        @"Local\CodexWeeklyUsageIndicator.SessionsPlanned." + accountKey.ToLowerInvariant().Replace('/', '_');

    /// <summary>Tells a running widget; without one there is nobody to tell.</summary>
    private static void SignalPlanned(string accountKey)
    {
        try
        {
            if (!EventWaitHandle.TryOpenExisting(PlannedEventName(accountKey), out var planned)) return;
            using (planned) planned.Set();
        }
        catch (Exception exception) when (exception is UnauthorizedAccessException or IOException) { }
    }

    internal static Plan Build(Sources sources, string sessionId, string? only = null)
    {
        var current = FindAccount(sources.SessionsRoot, sessionId);
        if (current is null) return new("unknown_session");
        var key = current.Value.Key;
        // Without what the accounts agreed on, a value another account never received would pass for its change.
        var (kept, exists) = ClaudeSessionLayout.Load(sources.Ledger);
        // The widget starts it from the old reference; one it cannot read holds the start until it is readable or removed.
        if (!exists) return new(ClaudeSessionReference.Read(sources.Ledger).Known ? "widget_not_ready" : "reference_unreadable", key);
        if (kept is null) return new("layout_unreadable", key);
        string? Label(string account) => kept.Accounts.TryGetValue(account, out var known) ? known.Label : null;
        var warnings = new List<string>();
        var scopes = TryReadGroupScopes(sources.LocalStorage);
        // This account having no scope yet only means it has no groups, and planning for it only adds.
        var currentLayout = LayoutOf(scopes, key, missingIsEmpty: true);
        if (currentLayout is null) warnings.Add("groups_unreadable");
        var pinned = TryReadPins(sources.IndexedDb);
        if (pinned is null) warnings.Add("pins_unreadable");
        var lineage = new Lineage(sessionId, cli => Transcript(sources.ProjectsRoot, cli),
            (older, next) => HandOver(sources.ProjectsRoot, older, next));
        var others = new List<Other>();
        var groupsUnread = false;
        foreach (var account in kept.Accounts.Keys.Where(account => account != key && (only is null || account == only))
                     .Order(StringComparer.Ordinal))
        {
            // An account that never used the app here has no list to take changes from.
            if (AccountDirectory(sources.SessionsRoot, account) is not { } directory) continue;
            // Another account's groups must be confirmed: read as empty, they would look like sessions taken out of
            // groups. One that never had a group has no scope and offers none; one whose scope cannot be read holds
            // every group change, since it may hold the newest.
            var layout = LayoutOf(scopes, account, missingIsEmpty: false);
            if (layout is null && scopes?.Any(pair => string.Equals(pair.Key, account, StringComparison.OrdinalIgnoreCase)) == true)
                groupsUnread = true;
            var copyTitles = CopyTitles(sources.Ledger, account);
            others.Add(new(account, Label(account), kept.Accounts[account].LeftAt, ReadRecords(directory, pinned), layout,
                copyTitles?.Keys.ToHashSet(StringComparer.Ordinal), kept.Side(account, key),
                kept.Accounts.Keys.Where(third => third != account && third != key).Select(third => kept.Side(account, third))
                    .OfType<IReadOnlyDictionary<string, ClaudeSessionLayout.Agreed>>().ToArray(), kept.Side(key, account),
                copyTitles));
        }
        if (others.Count == 0) return new("no_other_accounts", key, Label(key));
        if (groupsUnread && currentLayout is not null) warnings.Add("groups_unreadable");
        var currentCopies = CopyTitles(sources.Ledger, key);
        if (currentCopies is null || others.Any(other => other.Copies is null)) warnings.Add("ledger_unreadable");
        return Compute(key, Label(key), ReadRecords(current.Value.Directory, pinned), currentLayout, others, pinned is not null,
            lineage, warnings, groupsKnown: !groupsUnread, currentCopies: currentCopies?.Keys.ToHashSet(StringComparer.Ordinal),
            currentCopyTitles: currentCopies);
    }

    /// <summary>Records the widget copied into an account's list, by session id, from its ledger. No ledger means no
    /// copies; one that cannot be read whole is null.</summary>
    internal static IReadOnlySet<string>? Copies(string ledgerDirectory, string account) =>
        CopyTitles(ledgerDirectory, account)?.Keys.ToHashSet(StringComparer.Ordinal);

    /// <summary>The same copies with the title the widget last wrote to each (null when the ledger does not track it).
    /// The app's rename history cannot tell a renamed copy: it records neither a first name nor a change of case or
    /// spacing, so only a title exactly as written proves it untouched.</summary>
    internal static IReadOnlyDictionary<string, string?>? CopyTitles(string ledgerDirectory, string account)
    {
        var path = Path.Combine(ledgerDirectory, "ledger.json");
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (JsonNode.Parse(stream) is not JsonObject { } root || root["created"] is not JsonArray created) return null;
            var copies = new Dictionary<string, string?>(StringComparer.Ordinal);
            foreach (var item in created)
            {
                if (item is not JsonObject entry || Text(entry, "dst") is not { } target || Text(entry, "file") is not { } file
                    || !file.StartsWith("local_", StringComparison.Ordinal) || !file.EndsWith(".json", StringComparison.Ordinal))
                    return null;
                if (string.Equals(target, account, StringComparison.OrdinalIgnoreCase) && entry["retiredAt"] is null)
                    copies[file[..^".json".Length]] = Text(entry, "title");
            }
            return copies;
        }
        catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
        { return new Dictionary<string, string?>(StringComparer.Ordinal); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        { return null; }
    }

    /// <summary>
    /// The changes, in the order they must run: create the groups the moves need, move sessions (moving a pinned session
    /// into a group unpins it, so pins are planned after the moves), set pins, rename, and only then archive the older
    /// states that are ready. Field by field, a conversation here takes what another account changed: a value that
    /// differs from what the two last agreed on. A value they never agreed on is offered too, unless the record is the
    /// widget's copy and gives nothing of its own; it yields to a fresher offer when it still equals all that account
    /// agreed on with others. Offers that disagree go to the account the app left last. With nothing offered, a
    /// conversation keeps what it has here, an older state's pin and group included. Groups are planned only when the
    /// pins and this account's groups could be read; pins only when the pins could be read, and none from an account
    /// without a single pin while this one has some. An older state is ready when all that is known and its newest state
    /// already holds what it keeps, so stopping part way never loses a pin or a group.
    /// </summary>
    /// <param name="groupsKnown">False when another account's groups are there but cannot be read: then no group
    /// changes, since that account may hold the newest.</param>
    /// <param name="currentCopies">The widget's copies in this account's list. A stale offer fills only a field such a copy
    /// shows untouched and this account never agreed on: anything else may be the user's own choice. The ledger that
    /// tells them is the one that tells the other accounts' copies.</param>
    /// <param name="currentCopyTitles">The title the widget last wrote to each copy here, where tracked: only a copy still
    /// showing it exactly has an untouched title.</param>
    internal static Plan Compute(string current, string? currentLabel, IReadOnlyList<Record> currentRecords,
        Layout? currentLayout, IReadOnlyList<Other> others, bool pinsKnown = true, Lineage? lineage = null,
        IReadOnlyList<string>? warnings = null, bool groupsKnown = true, IReadOnlySet<string>? currentCopies = null,
        IReadOnlyDictionary<string, string?>? currentCopyTitles = null)
    {
        var notes = warnings?.ToList() ?? [];
        // This account's older states are archived and the others' are seen through: either way the newest state of the
        // conversation holds their pin and group.
        var retiring = lineage is null ? new Dictionary<string, Older>(StringComparer.Ordinal) : OlderStates(currentRecords, lineage);
        var (own, ownLayout) = Inherit(currentRecords, currentLayout, retiring);
        var ownById = own.ToDictionary(record => record.SessionId, StringComparer.Ordinal);
        var heads = retiring.Values.Select(older => older.Newest.SessionId).ToHashSet(StringComparer.Ordinal);
        var ownOlderOf = retiring.ToLookup(pair => pair.Value.Newest.SessionId, pair => pair.Key, StringComparer.Ordinal);
        var candidates = currentRecords.Where(record => !retiring.ContainsKey(record.SessionId)).ToArray();
        var newest = candidates.OrderByDescending(record => record.LastActivityAt).ToArray();
        // Without knowing which records are the widget's copies, a copy would be taken for a choice: only older states
        // are cleared then.
        var copiesKnown = others.All(other => other.Copies is not null);
        var pinPlan = pinsKnown && copiesKnown;
        // Moves can unpin, so groups wait for readable pins too.
        var groups = pinPlan && currentLayout is not null && groupsKnown;
        var seen = others.Select(other => Seen.Of(other, newest, candidates, lineage, pinPlan)).ToArray();
        if (pinPlan && seen.Any(side => !side.PinsCount)) notes.Add("other_pins_empty");
        var into = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        var ungroup = new List<string>();
        var pins = new List<Pin>();
        var renames = new List<Rename>();
        // What each session here should end with: its pin, and its group (Known false when it cannot be resolved).
        var targets = new Dictionary<string, (bool Pinned, (bool Known, string? Name) Group)>(StringComparer.Ordinal);
        var onlyHere = new List<Record>();
        var unresolved = false;
        foreach (var record in newest)
        {
            var found = seen.Where(side => side.Matches.ContainsKey(record.SessionId)).ToArray();
            if (found.Length == 0) onlyHere.Add(record);
            // A conversation no other account lists keeps what it has, an older state's pin and group included.
            if (found.Length == 0 && !heads.Contains(record.SessionId)) continue;
            var mine = ownById[record.SessionId];
            // A value nobody changed since agreeing elsewhere only fills what a copy the widget made here shows untouched,
            // and only a field this account never agreed on: anything else may be the user's own choice.
            var copyHere = currentCopies?.Contains(record.SessionId) == true;
            var ownIds = ownOlderOf[record.SessionId].Prepend(record.SessionId).ToArray();
            var ownAgreed = others.Select(other => other.Mine).OfType<IReadOnlyDictionary<string, ClaudeSessionLayout.Agreed>>()
                .SelectMany(side => ownIds.Select(id => side.GetValueOrDefault(id))).OfType<ClaudeSessionLayout.Agreed>().ToArray();
            var ownGroup = ownLayout is null ? (Known: false, Name: (string?)null) : GroupOf(ownLayout, record.SessionId);
            var staleTitle = copyHere && currentCopyTitles?.GetValueOrDefault(record.SessionId) is { } copiedTitle
                && mine.Title == copiedTitle && ownAgreed.All(entry => entry.Title is null);
            var stalePin = copyHere && !mine.Pinned && ownGroup is { Known: true, Name: null } && ownAgreed.All(entry => entry.Pinned is null);
            var staleGroup = copyHere && ownGroup is { Known: true, Name: null } && ownAgreed.All(entry => !entry.HasGroup);
            var titles = new List<Vote<string>>();
            var pinVotes = new List<Vote<bool>>();
            var groupVotes = new List<Vote<(bool Known, string? Name)>>();
            foreach (var side in found)
            {
                var match = side.Matches[record.SessionId];
                // A copy the widget made there shows the conversation as it was copied, not a choice made there: unless
                // the agreements tell, it gives a group only when filed there, a pin when pinned there or filed there (the
                // move unpinned it, so its pin was chosen since), and a title once it differs from the one the widget wrote
                // (renamed there, where the ledger does not track it).
                var copy = side.Other.Copies?.Contains(match.SessionId) == true;
                var (agreed, elsewhere) = side.AgreedOf(match);
                var given = side.Layout is null ? (Known: false, Name: (string?)null) : GroupOf(side.Layout, match.SessionId);
                if (copiesKnown && !string.IsNullOrWhiteSpace(match.Title))
                    Offer(titles, side, match.Title, agreed?.Title is { } agreedTitle ? match.Title != agreedTitle : null,
                        !copy || (side.Other.CopyTitles?.GetValueOrDefault(match.SessionId) is { } written
                            ? match.Title != written : match.Renamed),
                        elsewhere.Select(entry => entry.Title).OfType<string>());
                if (side.PinsCount)
                    Offer(pinVotes, side, match.Pinned, agreed?.Pinned is { } agreedPinned ? match.Pinned != agreedPinned : null,
                        !copy || match.Pinned || given is { Known: true, Name: not null },
                        elsewhere.Where(entry => entry.Pinned is not null).Select(entry => entry.Pinned!.Value));
                if (groups && side.Layout is not null)
                    Offer(groupVotes, side, given, agreed is { HasGroup: true } was ? !given.Known || given.Name != was.Group : null,
                        !copy || !given.Known || given.Name is not null,
                        elsewhere.Where(entry => entry.HasGroup).Select(entry => (true, entry.Group)));
            }
            var title = Decide(titles, staleTitle);
            if (title is not null && title.Value != record.Title)
                renames.Add(new(record.SessionId, title.Value, record.Title, title.From.Other.Label));
            var pinVote = Decide(pinVotes, stalePin);
            var pinTarget = pinVote?.Value ?? mine.Pinned;
            (bool Known, string? Name) groupTarget = (true, null);
            if (groups)
            {
                groupTarget = Decide(groupVotes, staleGroup)?.Value ?? GroupOf(ownLayout!, mine.SessionId);
                if (!groupTarget.Known || !GroupOf(currentLayout!, record.SessionId).Known) unresolved = true;
            }
            targets[record.SessionId] = (pinTarget, groupTarget);
            var pinned = record.Pinned;
            if (groups && groupTarget is { Known: true, Name: var target }
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
            if (pinPlan && pinTarget != pinned)
                pins.Add(new(record.SessionId, pinTarget, title?.Value ?? record.Title, pinVote?.From.Other.Label));
        }
        // A group the scope does not list leaves that session where it is, and nothing about it is settled.
        if (unresolved) notes.Add("groups_unresolved");
        var existing = currentLayout?.Groups.Select(group => group.Name).ToHashSet(StringComparer.Ordinal) ?? [];
        var create = into.Keys.Where(name => !existing.Contains(name)).Order(StringComparer.Ordinal).ToList();
        var moves = into.OrderBy(pair => pair.Key, StringComparer.Ordinal).Select(pair => new Move(pair.Key, pair.Value)).ToList();
        if (ungroup.Count > 0) moves.Add(new Move(null, ungroup));
        // An older state goes only once what it leaves is known and already in place on its newest state: its own group,
        // the group and pin the newest state should have, and the newest state's own, all read and matching.
        bool Ready(Record old, Record head) =>
            pinPlan && groups && targets.TryGetValue(head.SessionId, out var target)
            && GroupOf(currentLayout!, old.SessionId).Known
            && target.Group is { Known: true, Name: var want }
            && GroupOf(currentLayout!, head.SessionId) is { Known: true, Name: var have } && want == have
            && target.Pinned == head.Pinned;
        var byId = currentRecords.ToDictionary(record => record.SessionId, StringComparer.Ordinal);
        var archive = currentRecords.Where(record => retiring.ContainsKey(record.SessionId)).Select(record =>
            {
                var older = retiring[record.SessionId];
                return new Retire(record.SessionId, record.Title, pinsKnown && record.Pinned, older.Newest.SessionId,
                    older.Newest.Title, Ready(record, byId[older.Newest.SessionId]), older.LastWritten, older.MovedOn);
            })
            .OrderBy(retire => retire.Title, StringComparer.Ordinal).ToList();
        var sidebars = new List<Sidebar> { View(current, currentLabel, currentRecords, currentLayout, pinsKnown) };
        sidebars.AddRange(others.Select(other => View(other.Account, other.Label, other.Records, other.Layout, pinsKnown)));
        var views = seen.Select(side => new OtherView(side.Other.Account, side.Other.Label, side.Other.LeftAt, side.Matches.Count,
            side.View.Where(record => !side.Claimed.Contains(record.SessionId)).Select(Name).Order(StringComparer.Ordinal).ToArray()))
            .ToList();
        return new("ok", current, currentLabel, views, archive, create, moves, pins, renames,
            onlyHere.Select(Name).Order(StringComparer.Ordinal).ToArray(), sidebars, notes is { Count: > 0 } ? notes : null);
    }

    /// <summary>A value another account offers for one field: fresh unless it merely equals what that account agreed on
    /// with others.</summary>
    private sealed record Vote<T>(T Value, bool Fresh, Seen From);

    /// <summary>A value that differs from what the two accounts last agreed on (<paramref name="changed"/>) is a change
    /// and offered. One they never agreed on (null) is offered when the record gives it, stale when it still equals all
    /// that account agreed on with others.</summary>
    private static void Offer<T>(List<Vote<T>> votes, Seen from, T value, bool? changed, bool gives, IEnumerable<T> agreedElsewhere)
    {
        if (changed is { } isChange) { if (isChange) votes.Add(new(value, true, from)); return; }
        if (!gives) return;
        var elsewhere = agreedElsewhere.ToArray();
        votes.Add(new(value, elsewhere.Length == 0 || elsewhere.Any(agreed => !EqualityComparer<T>.Default.Equals(agreed, value)), from));
    }

    /// <summary>What a field becomes: the offered value; when offers disagree, the fresh one from the account the app
    /// left last. Stale offers count only for a record that <paramref name="takesStale"/>. Null when nothing is taken.</summary>
    private static Vote<T>? Decide<T>(List<Vote<T>> votes, bool takesStale) =>
        votes.Where(vote => vote.Fresh || takesStale).OrderByDescending(vote => vote.Fresh)
            .ThenByDescending(vote => vote.From.Other.LeftAt ?? DateTimeOffset.MinValue)
            .ThenBy(vote => vote.From.Other.Account, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>Another account as seen from here: its records and groups with older states seen through, whether its
    /// pins count, and which of its records serves each session here.</summary>
    private sealed class Seen
    {
        internal required Other Other { get; init; }
        internal required IReadOnlyList<Record> View { get; init; }
        internal required Layout? Layout { get; init; }
        internal required bool PinsCount { get; init; }
        internal required Dictionary<string, Record> Matches { get; init; }
        internal required HashSet<string> Claimed { get; init; }
        // A newest state's id → its older states' ids, the most recently active first.
        internal required ILookup<string, string> OlderOf { get; init; }

        /// <summary>What a record there agreed on with this account, and with each further account; its older states'
        /// agreements fill in what it never agreed on itself.</summary>
        internal (ClaudeSessionLayout.Agreed? Here, IReadOnlyList<ClaudeSessionLayout.Agreed> Elsewhere) AgreedOf(Record record)
        {
            var ids = OlderOf[record.SessionId].Prepend(record.SessionId).ToArray();
            ClaudeSessionLayout.Agreed? Fold(IReadOnlyDictionary<string, ClaudeSessionLayout.Agreed>? side) =>
                side is null ? null : ids.Select(id => side.GetValueOrDefault(id)).OfType<ClaudeSessionLayout.Agreed>()
                    .Aggregate((ClaudeSessionLayout.Agreed?)null, (folded, next) => folded is null ? next : folded.Over(next));
            return (Fold(Other.Agreed), (Other.Elsewhere ?? []).Select(Fold).OfType<ClaudeSessionLayout.Agreed>().ToArray());
        }

        internal static Seen Of(Other other, IReadOnlyList<Record> newest, IReadOnlyList<Record> candidates, Lineage? lineage,
            bool pinsKnown)
        {
            var older = lineage is null ? new Dictionary<string, Older>(StringComparer.Ordinal) : OlderStates(other.Records, lineage);
            var (view, layout) = Inherit(other.Records, other.Layout, older);
            var byId = other.Records.ToDictionary(record => record.SessionId, StringComparer.Ordinal);
            var (matches, claimed) = Match(newest, view);
            return new()
            {
                Other = other, View = view, Layout = layout, Matches = matches, Claimed = claimed,
                // An account without a single pin while this one has some looks like a wiped pin list: its pins do not count.
                PinsCount = pinsKnown && (other.Records.Any(record => record.Pinned) || !candidates.Any(record => record.Pinned)),
                OlderOf = older.OrderByDescending(pair => byId[pair.Key].LastActivityAt)
                    .ToLookup(pair => pair.Value.Newest.SessionId, pair => pair.Key, StringComparer.Ordinal)
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

    /// <summary>For the widget, after a plan ran in <paramref name="account"/>: per other account, the values each
    /// conversation the two lists share shows alike, and every session id each list holds. Null when the pins or groups
    /// cannot be read; an account without a list is skipped.</summary>
    internal static IReadOnlyList<Agreement>? Agreements(Sources sources, string account, IReadOnlyList<string> others) =>
        Pairings(sources, account, others, everyList: false, (_, _, mine, theirs) =>
        {
            var alike = Alike(mine, theirs);
            return (alike, alike);
        });

    /// <summary>The agreements to start from when the widget first keeps them. The old reference was the account the
    /// others copied, the one holding the newest layout, so each other account's values are taken as what it last agreed
    /// on with the reference: whatever the reference shows differently is its change, and the others take it. A record the
    /// widget copied into the reference gets no agreement on that side: it was never a choice, so the copies rule tells
    /// what it offers. Null when the pins, the groups or the ledger cannot be read.</summary>
    internal static IReadOnlyList<Agreement>? Baseline(Sources sources, string reference, IReadOnlyList<string> others)
    {
        if (Copies(sources.Ledger, reference) is not { } copies) return null;
        var result = new List<Agreement>();
        foreach (var account in others.Where(account => account != reference))
        {
            if (Pairings(sources, account, [reference], everyList: true, (_, there, mine, _) =>
                    (mine, copies.Contains(there.SessionId) ? new ClaudeSessionLayout.Agreed() : mine)) is not { } pairings)
                return null;
            result.AddRange(pairings);
        }
        return result;
    }

    /// <summary>Per other account, what to record on each side for each conversation the two lists share, as
    /// <paramref name="pick"/> chooses from the records on both sides and their known values. Only lists read whole
    /// count: a record that could not be read now would look gone, and its agreements would be forgotten. Null when this
    /// account's list, or with <paramref name="everyList"/> any list, cannot be read whole; otherwise such a pair is
    /// skipped.</summary>
    private static IReadOnlyList<Agreement>? Pairings(Sources sources, string account, IReadOnlyList<string> others,
        bool everyList, Func<Record, Record, ClaudeSessionLayout.Agreed, ClaudeSessionLayout.Agreed,
            (ClaudeSessionLayout.Agreed Mine, ClaudeSessionLayout.Agreed Theirs)> pick)
    {
        if (AccountDirectory(sources.SessionsRoot, account) is not { } directory) return [];
        var scopes = TryReadGroupScopes(sources.LocalStorage);
        var pinned = TryReadPins(sources.IndexedDb);
        if (LayoutOf(scopes, account, missingIsEmpty: true) is not { } layout || pinned is null) return null;
        var lineage = new Lineage(null, cli => Transcript(sources.ProjectsRoot, cli),
            (older, next) => HandOver(sources.ProjectsRoot, older, next));
        var mine = ReadRecords(directory, pinned, out var whole);
        if (!whole) return null;
        var retiring = OlderStates(mine, lineage);
        var (own, ownLayout) = Inherit(mine, layout, retiring);
        var ownById = own.ToDictionary(record => record.SessionId, StringComparer.Ordinal);
        var candidates = mine.Where(record => !retiring.ContainsKey(record.SessionId)).ToArray();
        var newest = candidates.OrderByDescending(record => record.LastActivityAt).ToArray();
        var myIds = mine.Select(record => record.SessionId).ToHashSet(StringComparer.Ordinal);
        var result = new List<Agreement>();
        foreach (var other in others.Where(other => other != account))
        {
            if (AccountDirectory(sources.SessionsRoot, other) is not { } theirs) continue;
            var records = ReadRecords(theirs, pinned, out var read);
            if (!read) { if (everyList) return null; continue; }
            var side = Seen.Of(new Other(other, null, null, records, LayoutOf(scopes, other, missingIsEmpty: false), null, null),
                newest, candidates, lineage, pinsKnown: true);
            var a = new Dictionary<string, ClaudeSessionLayout.Agreed>(StringComparer.Ordinal);
            var b = new Dictionary<string, ClaudeSessionLayout.Agreed>(StringComparer.Ordinal);
            foreach (var (here, there) in side.Matches)
            {
                var (mineAgreed, theirsAgreed) = pick(ownById[here], there, Values(ownById[here], ownLayout, pins: true),
                    Values(there, side.Layout, side.PinsCount));
                if (mineAgreed is not null && mineAgreed != new ClaudeSessionLayout.Agreed()) a[here] = mineAgreed;
                if (theirsAgreed is not null && theirsAgreed != new ClaudeSessionLayout.Agreed()) b[there.SessionId] = theirsAgreed;
            }
            result.Add(new(account, other, a, b, myIds, records.Select(record => record.SessionId).ToHashSet(StringComparer.Ordinal)));
        }
        return result;
    }

    /// <summary>A record's values as far as they are known: its pin when pins count, its group when it resolves, and its
    /// title when it has one.</summary>
    private static ClaudeSessionLayout.Agreed Values(Record record, Layout? layout, bool pins)
    {
        var group = layout is null ? (Known: false, Name: (string?)null) : GroupOf(layout, record.SessionId);
        return new(pins ? record.Pinned : null, group.Known, group.Known ? group.Name : null,
            string.IsNullOrWhiteSpace(record.Title) ? null : record.Title);
    }

    /// <summary>The fields two sides show alike.</summary>
    private static ClaudeSessionLayout.Agreed Alike(ClaudeSessionLayout.Agreed mine, ClaudeSessionLayout.Agreed theirs)
    {
        var group = mine.HasGroup && theirs.HasGroup && mine.Group == theirs.Group;
        return new(mine.Pinned is { } pin && theirs.Pinned == pin ? pin : null, group, group ? mine.Group : null,
            mine.Title is { } title && theirs.Title == title ? title : null);
    }

    private static JsonObject? TryReadGroupScopes(string localStorageDirectory)
    {
        try { return ReadGroupScopes(localStorageDirectory); }
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
    internal static IReadOnlyList<Record> ReadRecords(string directory, IReadOnlySet<string>? pinned = null) =>
        ReadRecords(directory, pinned, out _);

    /// <param name="whole">False when a record file could not be read now (being written, locked or malformed).</param>
    internal static IReadOnlyList<Record> ReadRecords(string directory, IReadOnlySet<string>? pinned, out bool whole)
    {
        var records = new List<Record>();
        whole = true;
        foreach (var path in Directory.EnumerateFiles(directory, "local_*.json"))
        {
            JsonObject? record;
            try
            {
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                if (stream.Length is <= 0 or > MaxRecordBytes) { whole = false; continue; }
                record = JsonNode.Parse(stream) as JsonObject;
            }
            catch (Exception exception) when (exception is FileNotFoundException) { continue; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            { whole = false; continue; }
            if (record is null) { whole = false; continue; }
            if (record["isArchived"] is JsonValue archived && archived.TryGetValue<bool>(out var isArchived) && isArchived)
                continue;
            var prior = new List<string>();
            if (record["priorCliSessionIds"] is JsonArray ids)
                foreach (var node in ids)
                    if (node is JsonValue value && value.TryGetValue<string>(out var id)) prior.Add(id.ToLowerInvariant());
            var sessionId = Path.GetFileNameWithoutExtension(path);
            records.Add(new(sessionId, Text(record, "title"), pinned?.Contains(sessionId) == true,
                Text(record, "cliSessionId")?.ToLowerInvariant(), prior,
                record["lastActivityAt"] is JsonValue at && at.TryGetValue<long>(out var activity) ? activity : 0,
                Branch: !string.IsNullOrEmpty(Text(record, "forkedFromSessionId")) || record["spawnedFrom"] is JsonObject,
                Renamed: record["previousTitles"] is JsonArray { Count: > 0 }));
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
