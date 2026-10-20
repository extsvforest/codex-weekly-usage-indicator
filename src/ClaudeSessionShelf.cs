using System.Globalization;
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
    IReadOnlyList<string> RegistryDirectories, string LedgerDirectory, string? DesktopConfig = null)
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
            Path.Combine(local, "CodexWeeklyUsageIndicator.SessionShelf"),
            // Automatic sidebar writes stay off: holding the web UI store's lock while Desktop starts can make Chromium
            // fail to open it and recreate it. Writes run only as a controlled step (see ClaudeLocalStorage).
            DesktopConfig: null);
    }
}

internal sealed record ClaudeShelfAction(string Source, string SourceFile, string Target, string CliSessionId,
    JsonObject Record);

/// <summary>A record of a list that holds an older state of a conversation another account continued, to be rewritten in
/// place to that conversation's newest state (<see cref="ClaudeSessionShelf.AdvanceRecord"/>), so the list keeps one
/// entry with its id, name and settings. It is written only while it still hashes to <c>Fingerprint</c>, everything the
/// decision read is as it read it (the source records in <c>Sources</c> by hash, the transcripts in <c>Transcripts</c>
/// by size and times), and none of the transcripts in <c>Lineage</c> has a writer.</summary>
internal sealed record ClaudeShelfAdvance(string Target, string File, string Fingerprint, string Source, string SourceFile,
    string FromCliSessionId, string CliSessionId, IReadOnlyList<string> Lineage, JsonObject Record,
    IReadOnlyDictionary<string, string> Sources, IReadOnlyDictionary<string, string> Transcripts);

/// <summary>A copy this utility made that a newer record of the same conversation in that list superseded. It is
/// archived, not deleted, so the user can bring it back, and only while the transcripts the decision read
/// (<c>Transcripts</c>, by size and times) are as it read them.</summary>
internal sealed record ClaudeShelfRetirement(string Target, string File, string CliSessionId, string SuccessorFile,
    IReadOnlyDictionary<string, string>? Transcripts = null);

internal sealed class ClaudeShelfPlan
{
    // Skips that clear up on their own; a plan containing them is retried rather than reported as done.
    internal static readonly string[] TransientReasons = ["live_writer"];
    internal List<ClaudeShelfAction> Actions { get; } = [];
    internal List<ClaudeShelfAdvance> Advances { get; } = [];
    internal List<ClaudeShelfRetirement> Retirements { get; } = [];
    internal SortedDictionary<string, SortedDictionary<string, int>> Skipped { get; } = new(StringComparer.Ordinal);
    internal SortedDictionary<string, string> Held { get; } = new(StringComparer.Ordinal);
    internal SortedDictionary<string, SortedDictionary<string, string>> Fingerprints { get; } = new(StringComparer.Ordinal);
    /// <summary>Transcript ids live CLIs were writing when the plan was made.</summary>
    internal IReadOnlySet<string> LiveIds { get; set; } = new HashSet<string>();
    /// <summary>Entries the next fill adds to a list or brings up to date there.</summary>
    internal int PendingFor(string targetKey) =>
        Actions.Count(action => action.Target == targetKey) + Advances.Count(advance => advance.Target == targetKey);
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

internal sealed record ClaudeShelfResult(int Created, ClaudeShelfPlan Plan, int Retired = 0,
    ClaudeSidebarPrefs.Outcome? Prefs = null, int Retitled = 0, int Advanced = 0);

/// <param name="created">Records the run had written (added or advanced) when the app started.</param>
internal sealed class ClaudeDesktopRunningException(int created = 0)
    : InvalidOperationException(created == 0 ? "Claude 앱이 실행 중이라 세션 목록을 채우지 않았습니다."
        : "Claude 앱이 켜져 세션 목록 채우기를 중간에 멈췄습니다.")
{
    internal int Created { get; } = created;
}

/// <summary>
/// Fills other accounts' Claude Desktop shelves so every registered account lists this PC's Code sessions. A new record
/// points at the same shared transcript (cliSessionId); the app continues it with the signed-in account. When another
/// account continued a conversation a list holds in an older state, that record is brought to the newest state in place
/// instead, as the app does when a conversation goes on within one account, so the list keeps one entry. Invariants:
/// add records, or advance an older state of a conversation another account continued to that conversation's newest
/// state in place; never delete; write only while Desktop and its Code CLIs are fully closed; never read or touch
/// authentication data.
/// </summary>
internal sealed class ClaudeSessionShelf
{
    private const long MaxRecordBytes = 10 * 1024 * 1024;
    private const string PartialSuffix = ".shelf-fill.partial";
    // An entry this close after the next transcript began is still the hand-over, as in the session plan.
    private static readonly TimeSpan HandOverSlack = TimeSpan.FromSeconds(2);
    private const int MaxRememberedHandOvers = 256;
    private static readonly string[] CopiedFields =
        ["title", "titleSource", "createdAt", "lastActivityAt", "indexedAt", "completedTurns"];
    // The app protects a transcript from deletion per id, so carry every id this session still claims.
    private static readonly string[] LineageFields = ["priorCliSessionIds", "rewindEdges", "transcriptCuts"];
    private static readonly string[] EffortLevels = ["low", "medium", "high", "xhigh", "max"];
    private static readonly string[] RemoteFields = ["sshConfig", "wslConfig", "cloudSessionId", "movedToCloud"];
    private static readonly string[] WorktreeFields = ["worktreePath", "worktreeName", "worktreeLazy"];
    // Only an ordinary local session is advanced (fields read from Claude 2.42304's record loader, its fork of a session
    // and its own transcript-id switches). These name a transcript besides the current one: an import's staged file
    // (confirming the import moves it over the current transcript id, removing what is there if a move fails),
    // unarchived, history-only or remote ids. Or a session of a kind of its own: fork, spawn, schedule, dispatch,
    // remote-control spawn, cloud VM, worktree state. Or work still pending: a first start, a queued reminder, notes,
    // messages or report, an interrupted, stopped or cut turn the app may resume or report, work armed at quit, a
    // transcript found missing. An import not yet confirmed (importedFrom without resumeConfirmed) holds too.
    private static readonly string[] HoldingFields =
    [
        "stagedTranscriptPath", "unarchivedCliSessionId", "historyOnlyCliSessionId", "priorRemoteCliSessionIds",
        "sshRemoteTranscriptPath", "forkedFromSessionId", "spawnedFrom", "scheduledTaskId", "dispatchParentId",
        "dispatchParentOrigin", "remoteControlSpawn", "violinBow", "branch", "folderWorktrees", "worktreePinned",
        "keptDirtyWorktree", "keptWorktreeLeftover", "pendingFirstStart", "pendingSystemReminder", "queuedSideSessionNotes",
        "sideSessionReportOwed", "peerInbound", "interruptedByQuitAt", "interruptedUnseenResume",
        "interruptedAwaitingPerson", "stoppedUntilPersonSends", "cutTurn", "cutTurnChain", "cutTurnStoppedAt",
        "lastTurnCutShort", "armedWorkAtQuit", "transcriptUnavailable"
    ];
    // What an advanced record keeps of its own: its place and name in this account, its folder and trust anchors, and the
    // choices made there that the app itself carries to a fork of a session (model and effort, permission mode and
    // grants, connectors, environment and session settings). Everything else describes the conversation or its last
    // turn: it comes from the newest state the way a new record does, or goes.
    private static readonly string[] KeptFields =
    [
        "title", "titleSource", "previousTitles", "titleFromPr", "titleSuggestionsOff", "isStarred", "createdAt",
        "lastFocusedAt", "color", "cwd", "originCwd", "gitAnchors", "gitAnchorsLookupOnly", "gitAnchorsFolderRealpath",
        "model", "effort", "effortInherited", "agent", "sessionSettings", "permissionMode", "bypassChosenInApp",
        "autoChosenInApp", "alwaysAllowedReasons", "sessionPermissionUpdates", "chromePermissionMode",
        "chromeAllowedDomains", "chromeTabGroupId", "cuAllowedApps", "cuGrantFlags", "cuFlagsGrantedAt",
        "cuSelectedDisplayId", "enabledMcpTools", "remoteMcpServersConfig", "withheldConnectorHosts",
        "cliMcpAppServerNames", "emailAddress", "envScopeId", "spaceId", "startedFromEnvironmentId",
        "classifierSummaryEnabled", "reportFindingsCard", "publishedArtifacts", "lastSpawnRootDetected",
        "autoArchiveExempt", "autoArchiveOnPrClose", "autoModeServerFallbackPrompt", "lanyardOfferPrompt",
        "violinBowPrompts", "violinBowPromptKinds"
    ];
    private static readonly JsonSerializerOptions RecordJson = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };
    private static readonly SemaphoreSlim RunGate = new(1, 1);
    private readonly ClaudeShelfPaths _paths;
    private readonly Func<ClaudeLiveSessionScan> _liveWriters;
    private readonly Func<IDisposable> _acquireLease;
    private readonly TimeProvider _time;
    // Plans repeat every minute while the app runs and a hand-over reads two transcripts whole: remember each verdict per
    // state of the two files.
    private readonly Dictionary<string, bool> _handOvers = new(StringComparer.Ordinal);
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
        plan.LiveIds = live.Ids;
        var transcripts = TranscriptIds();
        // Continuing a conversation gives it a new transcript id and lists the earlier ones. A record whose current id
        // another record lists as earlier is an older state of that conversation only when the transcripts show it went
        // on from there with nothing written to it since (see WentOnFrom); one written to afterwards is a branch.
        var lineage = shelves.ToDictionary(pair => pair.Key, pair => new Lineage(pair.Value.Records));
        foreach (var target in distinct)
        {
            var shelf = shelves[target.Key];
            if (shelf.Temporaries.Count > 0) { plan.Held[target.Key] = "recovering"; continue; }
            if (shelf.Records.Values.Any(record => record is null)) { plan.Held[target.Key] = "unreadable"; continue; }
            var present = shelf.Records.Values.Select(record => Text(record!, "cliSessionId"))
                .OfType<string>().Select(id => id.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            var deleted = shelf.Deleted.Select(DeletedId).ToHashSet(StringComparer.Ordinal);
            // A record this utility created and the user later removed there stays removed for that account.
            var removed = ledger.Created.Where(entry => entry.Target == target.Key && !entry.Retired
                    && !File.Exists(Path.Combine(ShelfDirectory(target), entry.File)))
                .Select(entry => entry.CliSessionId.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
            // So does a record it advanced there, at either state.
            removed.UnionWith(ledger.Advanced.Where(entry => entry.Target == target.Key && !shelf.Records.ContainsKey(entry.File))
                .SelectMany(entry => new[] { entry.FromCliSessionId, entry.CliSessionId }).Select(id => id.ToLowerInvariant()));
            var planned = new HashSet<string>(StringComparer.Ordinal);
            var offers = new List<Offer>();
            foreach (var source in distinct.Where(source => source.Key != target.Key))
            {
                var pair = source.Key[..8] + "->" + target.Key[..8];
                var from = shelves[source.Key];
                foreach (var (file, record) in from.Records)
                {
                    if (record is null) { plan.Skip(pair, "unreadable"); continue; }
                    var reason = Ineligible(record, transcripts);
                    var cli = Text(record, "cliSessionId")?.ToLowerInvariant();
                    // An older state of a conversation its own list holds further on is never offered.
                    if (reason is null && lineage[source.Key].SupersededBy(file, record)
                            .Any(newer => from.Records[newer] is { } later && WentOnFrom(record, later, transcripts)))
                        reason = "superseded";
                    if (reason is null && live.Ids.Contains(cli!)) reason = "live_writer";
                    // Nor is a conversation this list holds at that state or further on.
                    if (reason is null && (present.Contains(cli!) || planned.Contains(cli!)
                            || lineage[target.Key].ListersOf(cli!).Any(later => WentOnFrom(record, later, transcripts))))
                        reason = "already_present";
                    if (reason is null && (deleted.Contains(cli!) || removed.Contains(cli!))) reason = "deleted_in_target";
                    if (reason is not null) { plan.Skip(pair, reason); continue; }
                    planned.Add(cli!);
                    offers.Add(new Offer(source.Key, ShelfDirectory(source), file, record, pair, from.Fingerprint[file]));
                }
            }
            var taken = PlanAdvances(plan, target.Key, shelf, lineage[target.Key], offers, live, transcripts);
            foreach (var offer in offers.Where(offer => !taken.Contains(offer)))
                plan.Actions.Add(new ClaudeShelfAction(offer.Source, offer.File, target.Key, Text(offer.Record, "cliSessionId")!,
                    BuildRecord(offer.Record)));
            PlanRetirements(plan, ledger, target.Key, shelf, live, transcripts);
        }
        return plan;
    }

    /// <summary>A record of another account that a list lacks: where it is, its hash when planned, and the counter its
    /// skips go to.</summary>
    private sealed record Offer(string Source, string Directory, string File, JsonObject Record, string Pair, string Hash);

    /// <summary>
    /// Brings the older states a list holds to the newest state another account reached, and returns the offers that
    /// takes up. Offers are grouped by the record holding the latest earlier state of their conversation. Within a group
    /// an offer whose state another one holds further on (see <see cref="WentOnFrom"/>) drops out; when exactly one is
    /// left and it continues the record as one line, every step proven (see <see cref="Continues"/>), that newest state
    /// advances the record, once, whatever order the accounts came in. Two or more left are branches: like a record that
    /// may not be advanced, they leave the group to be added as before. A group touching a transcript being written
    /// waits. The transcripts are stamped before the decision reads them, and the advance carries those stamps and the
    /// source records' hashes, so it is written only while all of it is as it was read.
    /// </summary>
    private HashSet<Offer> PlanAdvances(ClaudeShelfPlan plan, string target, Shelf shelf, Lineage lineage,
        IReadOnlyList<Offer> offers, ClaudeLiveSessionScan live, IReadOnlyDictionary<string, string> transcripts)
    {
        var taken = new HashSet<Offer>();
        foreach (var group in offers.Select(offer => (Offer: offer, Older: OlderStateOf(shelf, offer.Record)))
                     .Where(item => item.Older is not null).GroupBy(item => item.Older!, StringComparer.Ordinal))
        {
            var file = group.Key;
            var older = shelf.Records[file]!;
            var members = group.Select(item => item.Offer).ToArray();
            if (!MayAdvance(file, older, lineage, transcripts)) continue;
            // The transcripts the decision reads: from the older state's current one on, in every offer's lineage.
            var from = Text(older, "cliSessionId")!.ToLowerInvariant();
            var ids = members.SelectMany(member => Chain(member.Record)!.SkipWhile(id => id != from))
                .Distinct(StringComparer.Ordinal).ToArray();
            if (ids.Any(live.Ids.Contains))
            {
                foreach (var member in members) plan.Skip(member.Pair, "live_writer");
                taken.UnionWith(members);
                continue;
            }
            if (Stamps(ids, transcripts) is not { } stamps) continue;
            var newest = members.Where(member => !members.Any(other => other != member
                && WentOnFrom(member.Record, other.Record, transcripts))).ToArray();
            if (newest is not [var head] || !SameFolder(older, head.Record) || !KeepsLineage(older, head.Record)
                || !Continues(older, head.Record, transcripts)) continue;
            var lineageIds = Chain(head.Record)!.SkipWhile(id => id != from).ToArray();
            plan.Advances.Add(new ClaudeShelfAdvance(target, file, shelf.Fingerprint[file], head.Source, head.File,
                Text(older, "cliSessionId")!, Text(head.Record, "cliSessionId")!, lineageIds, AdvanceRecord(older, head.Record),
                members.ToDictionary(member => Path.Combine(member.Directory, member.File), member => member.Hash,
                    StringComparer.OrdinalIgnoreCase),
                lineageIds.Select(id => transcripts[id]).ToDictionary(path => path, path => stamps[path], StringComparer.OrdinalIgnoreCase)));
            taken.UnionWith(members);
            foreach (var member in members.Where(member => member != head)) plan.Skip(member.Pair, "superseded");
        }
        return taken;
    }

    /// <summary>Each transcript's file with its size and times now; null when one is missing or cannot be read.</summary>
    private static Dictionary<string, string>? Stamps(IEnumerable<string> ids, IReadOnlyDictionary<string, string> transcripts)
    {
        var stamps = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in ids)
        {
            if (!transcripts.TryGetValue(id, out var path)) return null;
            try { stamps[path] = Stamp(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
        }
        return stamps;
    }

    /// <summary>Whether every transcript is still as stamped (<see cref="Stamp"/>) and every record still hashes as planned,
    /// or as this run wrote it (<paramref name="written"/>: another list's record this run advanced).</summary>
    private static bool Unchanged(IReadOnlyDictionary<string, string>? transcripts, IReadOnlyDictionary<string, string>? records = null,
        IReadOnlyDictionary<string, string>? written = null)
    {
        try
        {
            return (transcripts ?? new Dictionary<string, string>()).All(pair => Stamp(pair.Key) == pair.Value)
                && (records ?? new Dictionary<string, string>()).All(pair => File.Exists(pair.Key) && Hash(pair.Key) is var hash
                    && (hash == pair.Value || written?.GetValueOrDefault(pair.Key) == hash));
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
    }

    /// <summary>The record of a list holding the latest earlier state of <paramref name="newer"/>'s conversation: the one
    /// whose current transcript comes last among those the newer state lists as earlier. Null when none or several do.</summary>
    private static string? OlderStateOf(Shelf shelf, JsonObject newer)
    {
        if (Chain(newer) is not { } chain) return null;
        var holders = shelf.Records
            .Select(pair => (File: pair.Key, At: pair.Value is { } record && !Bool(record, "isArchived")
                && Text(record, "cliSessionId")?.ToLowerInvariant() is { } id ? chain.IndexOf(id) : -1))
            .Where(holder => holder.At >= 0 && holder.At < chain.Count - 1).ToArray();
        if (holders.Length == 0) return null;
        var latest = holders.Max(holder => holder.At);
        return holders.Where(holder => holder.At == latest).ToArray() is [var only] ? only.File : null;
    }

    /// <summary>Whether a record of a list may be rewritten to a newer state: an ordinary local session as a source must be
    /// (<see cref="Ineligible"/>), stored under its own id, holding none of <see cref="HoldingFields"/>, and the newest
    /// state its list has (no other record there continues it).</summary>
    private bool MayAdvance(string file, JsonObject record, Lineage lineage, IReadOnlyDictionary<string, string> transcripts) =>
        Text(record, "sessionId") + ".json" == file && Ineligible(record, transcripts) is null && !HoldsState(record)
        && lineage.SupersededBy(file, record).Count == 0;

    /// <summary>Whether <paramref name="later"/> holds the conversation of <paramref name="earlier"/> further on, as far as
    /// the transcripts show: the conversation went on from the earlier current transcript (see <see cref="Steps"/>) with
    /// nothing written to it since (see <see cref="WentOn"/>), so the earlier record is an older state and nothing it holds
    /// is missing further on. One written to afterwards (a CLI can resume a transcript under the same id) is a branch;
    /// what cannot be proven is not taken for an older state either.</summary>
    private bool WentOnFrom(JsonObject earlier, JsonObject later, IReadOnlyDictionary<string, string> transcripts) =>
        Steps(earlier, later) is { } steps && WentOn(steps[0], steps[1], transcripts);

    /// <summary>Whether <paramref name="later"/> continues <paramref name="earlier"/> as one line: every hand-over from the
    /// earlier current transcript to the later one proven (see <see cref="WentOn"/>), the steps a later state skips
    /// included. A transcript written to after the conversation went on from it, at any step, makes a branch there, and
    /// the earlier record could then stand for either line.</summary>
    private bool Continues(JsonObject earlier, JsonObject later, IReadOnlyDictionary<string, string> transcripts) =>
        Steps(earlier, later) is { } steps && steps.Zip(steps.Skip(1)).All(step => WentOn(step.First, step.Second, transcripts));

    /// <summary>The transcripts from the earlier record's current one to the later record's, when the later record (no
    /// fork or spawn) has a plain lineage that begins with the earlier record's; null otherwise.</summary>
    private static string[]? Steps(JsonObject earlier, JsonObject later)
    {
        if (IsBranch(later) || Chain(earlier) is not { } mine || Chain(later) is not { } theirs || mine.Count >= theirs.Count
            || !theirs.Take(mine.Count).SequenceEqual(mine, StringComparer.Ordinal)) return null;
        return theirs.Skip(mine.Count - 1).ToArray();
    }

    /// <summary>A record's transcript ids, earliest first and its current one last; null unless that order is plain: a
    /// current id, earlier ids that are all strings, none listed twice and the current one not among them (a restored
    /// conversation lists it).</summary>
    private static List<string>? Chain(JsonObject record)
    {
        if (Text(record, "cliSessionId")?.ToLowerInvariant() is not { } cli) return null;
        var chain = new List<string>();
        switch (record["priorCliSessionIds"])
        {
            case null: break;
            case JsonArray priors:
                foreach (var item in priors)
                {
                    if (item is not JsonValue value || !value.TryGetValue<string>(out var id)) return null;
                    chain.Add(id.ToLowerInvariant());
                }
                break;
            default: return null;
        }
        chain.Add(cli);
        return chain.Distinct(StringComparer.Ordinal).Count() == chain.Count ? chain : null;
    }

    /// <summary>Whether <paramref name="later"/> keeps what the app protects and restores per transcript in
    /// <paramref name="earlier"/>: the earlier rewind edges first and in order, unchanged, every edge of either running
    /// between transcripts of its own lineage, and each earlier transcript cut.</summary>
    private static bool KeepsLineage(JsonObject earlier, JsonObject later)
    {
        if (Chain(earlier) is not { } mine || Chain(later) is not { } theirs
            || Edges(earlier, mine) is not { } myEdges || Edges(later, theirs) is not { } theirEdges
            || myEdges.Count > theirEdges.Count || !myEdges.Zip(theirEdges).All(pair => JsonNode.DeepEquals(pair.First, pair.Second)))
            return false;
        return Cuts(earlier, mine) is { } myCuts && Cuts(later, theirs) is { } theirCuts
            && myCuts.All(cut => theirCuts.TryGetPropertyValue(cut.Key, out var kept) && JsonNode.DeepEquals(cut.Value, kept));
    }

    private static List<JsonNode?>? Edges(JsonObject record, IReadOnlyList<string> chain)
    {
        if (record["rewindEdges"] is null) return [];
        if (record["rewindEdges"] is not JsonArray edges) return null;
        foreach (var edge in edges)
            if (edge is not JsonObject link || Text(link, "parent")?.ToLowerInvariant() is not { } parent
                || Text(link, "child")?.ToLowerInvariant() is not { } child || !chain.Contains(parent) || !chain.Contains(child))
                return null;
        return [.. edges];
    }

    // The app keeps cuts as an object keyed by transcript id.
    private static JsonObject? Cuts(JsonObject record, IReadOnlyList<string> chain) => record["transcriptCuts"] switch
    {
        null or JsonArray { Count: 0 } => new JsonObject(),
        JsonObject cuts when cuts.All(cut => chain.Contains(cut.Key.ToLowerInvariant())) => cuts,
        _ => null
    };

    // A fork or a session spawned from another is a conversation of its own, never a newer state (as in the session plan).
    private static bool IsBranch(JsonObject record) =>
        !string.IsNullOrEmpty(Text(record, "forkedFromSessionId")) || record["spawnedFrom"] is JsonObject;

    private static bool HoldsState(JsonObject record) =>
        HoldingFields.Any(field => Holds(record[field])) || record["importedFrom"] is not null && !Bool(record, "resumeConfirmed");

    /// <summary>Whether a field says anything: present, and not null, false or empty.</summary>
    private static bool Holds(JsonNode? value) => value switch
    {
        null => false,
        JsonArray array => array.Count > 0,
        JsonObject map => map.Count > 0,
        JsonValue scalar when scalar.TryGetValue<bool>(out var flag) => flag,
        JsonValue scalar when scalar.TryGetValue<string>(out var text) => text.Length > 0,
        _ => true
    };

    private static bool SameFolder(JsonObject older, JsonObject newer) =>
        SamePath(Text(older, "cwd"), Text(newer, "cwd"))
        && (Text(older, "originCwd") is not { Length: > 0 } origin
            || SamePath(origin, Text(newer, "originCwd") is { Length: > 0 } theirs ? theirs : Text(newer, "cwd")));

    private static bool SamePath(string? one, string? other)
    {
        if (one is null || other is null || !Path.IsPathFullyQualified(one) || !Path.IsPathFullyQualified(other)) return false;
        try
        {
            return string.Equals(Path.TrimEndingDirectorySeparator(Path.GetFullPath(one)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(other)), StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        { return false; }
    }

    /// <summary>
    /// Whether the conversation went on from transcript <paramref name="older"/> in <paramref name="next"/> with nothing
    /// written to the older one since; otherwise that is a branch the user went on with. As in the session plan, both must
    /// be there and dated, the next one created after the older one and by the hand-over (its oldest entry the older one
    /// does not hold), and the older one's newest entry no later (2 s slack). Remembered per state of the two files.
    /// </summary>
    private bool WentOn(string older, string next, IReadOnlyDictionary<string, string> transcripts)
    {
        if (!transcripts.TryGetValue(older, out var olderPath) || !transcripts.TryGetValue(next, out var nextPath)) return false;
        string key;
        try { key = Stamp(olderPath) + "|" + Stamp(nextPath); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
        lock (_handOvers) if (_handOvers.TryGetValue(key, out var known)) return known;
        var went = ClaudeSessionSync.Transcript(_paths.ProjectsRoot, older) is { LastEntry: { } last, Dated: true } own
            && ClaudeSessionSync.Transcript(_paths.ProjectsRoot, next) is { Dated: true } onward && onward.Created > own.Created
            && ClaudeSessionSync.HandOver(_paths.ProjectsRoot, older, next) is { } handOver
            && onward.Created <= handOver + HandOverSlack && last <= handOver + HandOverSlack;
        lock (_handOvers)
        {
            if (_handOvers.Count >= MaxRememberedHandOvers) _handOvers.Clear();
            _handOvers[key] = went;
        }
        return went;
    }

    private static string Stamp(string path)
    {
        var file = new FileInfo(path);
        return path + ":" + file.Length + ":" + file.CreationTimeUtc.Ticks + ":" + file.LastWriteTimeUtc.Ticks;
    }

    /// <summary>
    /// An older state's record brought to the newest state, built as a new record of that state would be
    /// (<see cref="BuildRecord"/>, with the newest per-transcript models) and carrying the older record's id and what it
    /// keeps of its own (<see cref="KeptFields"/>). It shows the later of the two activity times.
    /// </summary>
    internal static JsonObject AdvanceRecord(JsonObject older, JsonObject newest)
    {
        var record = BuildRecord(newest);
        record["sessionId"] = Text(older, "sessionId");
        foreach (var field in KeptFields)
            if (older[field] is { } value) record[field] = value.DeepClone();
            else record.Remove(field);
        if (newest["transcriptModelStates"] is JsonObject states) record["transcriptModelStates"] = states.DeepClone();
        if (Number(older, "lastActivityAt") > Number(record, "lastActivityAt"))
            record["lastActivityAt"] = older["lastActivityAt"]!.DeepClone();
        return record;
    }

    /// <summary>A copy this utility made is retired once a newer record of the same conversation is in that list,
    /// or is about to be: it shows an older state and continuing it would fork the conversation. Only unarchived
    /// copies the app has not continued (same current transcript id) qualify, and only when the transcripts show the
    /// conversation went on from the copy with nothing written to it since (<see cref="WentOnFrom"/>): a CLI can resume a
    /// copy's transcript under the same id, which makes it a branch. Records the app wrote are left alone, and so is a
    /// record being advanced. That list is seen as the planned writes will leave it.</summary>
    private void PlanRetirements(ClaudeShelfPlan plan, ClaudeShelfLedger ledger, string target, Shelf shelf,
        ClaudeLiveSessionScan live, IReadOnlyDictionary<string, string> transcripts)
    {
        var records = new Dictionary<string, JsonObject?>(shelf.Records, StringComparer.Ordinal);
        foreach (var action in plan.Actions.Where(action => action.Target == target))
            records[action.Record["sessionId"]!.GetValue<string>() + ".json"] = action.Record;
        var advancing = plan.Advances.Where(advance => advance.Target == target).Select(advance => advance.File)
            .ToHashSet(StringComparer.Ordinal);
        foreach (var advance in plan.Advances.Where(advance => advance.Target == target)) records[advance.File] = advance.Record;
        var lineage = new Lineage(records);
        foreach (var entry in ledger.Created.Where(entry => entry.Target == target && !entry.Pending && !entry.Retired
                     && !advancing.Contains(entry.File)))
        {
            var cli = entry.CliSessionId.ToLowerInvariant();
            // Only a copy exactly as this utility wrote it: an edited copy may be a branch the user is working in,
            // even under the same transcript id (a plain resume keeps the id).
            if (!shelf.Records.TryGetValue(entry.File, out var record) || record is null || Bool(record, "isArchived")
                || !string.Equals(Text(record, "cliSessionId"), entry.CliSessionId, StringComparison.OrdinalIgnoreCase)
                || shelf.Fingerprint.GetValueOrDefault(entry.File) != entry.Sha256 || live.Ids.Contains(cli)) continue;
            // Exactly one newer record, which is itself the newest state; anything else is left for the user.
            if (lineage.SupersededBy(entry.File, record) is not [var successor]
                || lineage.SupersededBy(successor, records[successor]!).Count > 0) continue;
            // The copy's transcript and the next, stamped before the decision reads them: it is archived only while they are
            // still so.
            var later = records[successor]!;
            if (Steps(record, later) is not { } steps || Stamps(steps.Take(2), transcripts) is not { } stamps
                || !WentOnFrom(record, later, transcripts)) continue;
            plan.Retirements.Add(new ClaudeShelfRetirement(target, entry.File, entry.CliSessionId, successor, stamps));
        }
    }

    /// <summary>Which records of one list list which transcript ids as earlier.</summary>
    private sealed class Lineage
    {
        private readonly Dictionary<string, List<(string File, JsonObject Record)>> _listers = new(StringComparer.Ordinal);

        internal Lineage(IReadOnlyDictionary<string, JsonObject?> records)
        {
            foreach (var (file, record) in records)
            {
                if (record is null) continue;
                foreach (var prior in Priors(record).Distinct(StringComparer.Ordinal))
                {
                    if (!_listers.TryGetValue(prior, out var listers)) _listers[prior] = listers = [];
                    listers.Add((file, record));
                }
            }
        }

        /// <summary>The records that list <paramref name="id"/> as earlier.</summary>
        internal IEnumerable<JsonObject> ListersOf(string id) =>
            _listers.TryGetValue(id, out var listers) ? listers.Select(lister => lister.Record) : [];

        /// <summary>The other records that list this record's current id as earlier and are not themselves listed
        /// as earlier by it. A record listing its own id (a restored conversation) is not superseded by itself.</summary>
        internal IReadOnlyList<string> SupersededBy(string file, JsonObject record)
        {
            if (Text(record, "cliSessionId")?.ToLowerInvariant() is not { } current
                || !_listers.TryGetValue(current, out var listers)) return [];
            var mine = Priors(record).ToHashSet(StringComparer.Ordinal);
            return listers.Where(lister => lister.File != file
                    && Text(lister.Record, "cliSessionId")?.ToLowerInvariant() is { } other && !mine.Contains(other))
                .Select(lister => lister.File).Distinct(StringComparer.Ordinal).ToArray();
        }
    }

    private static IEnumerable<string> Priors(JsonObject record) =>
        record["priorCliSessionIds"] is JsonArray priors
            ? priors.Select(item => item is JsonValue value && value.TryGetValue<string>(out var id) ? id.ToLowerInvariant() : null)
                .OfType<string>()
            : [];

    /// <summary>Settles archive intents an interrupted run left: an archived copy is confirmed (its pin and group move
    /// still pending), anything else is cancelled and planned again from the files.</summary>
    private bool ReconcileArchives(ClaudeShelfLedger ledger)
    {
        var decisions = new List<(ClaudeShelfLedger.Entry Entry, bool Archived)>();
        foreach (var entry in ledger.Created.Where(entry => entry.MovedTo is not null && !entry.Retired))
        {
            var path = ClaudeShelfLedger.EntryPath(_paths.SessionsRoot, entry);
            if (path is null) { decisions.Add((entry, false)); continue; }
            // Only a file confirmed missing cancels the intent. File.Exists also says false when access fails, so read
            // directly; anything that cannot be read says nothing: keep the intent and try again later.
            JsonObject? record;
            try { record = JsonNode.Parse(File.ReadAllBytes(path)) as JsonObject; }
            catch (Exception exception) when (exception is FileNotFoundException or DirectoryNotFoundException)
            { decisions.Add((entry, false)); continue; }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
            { throw new IOException("세션 기록을 읽을 수 없어 이번에는 쓰지 않았습니다.", exception); }
            if (record is null) throw new IOException("세션 기록을 읽을 수 없어 이번에는 쓰지 않았습니다.");
            decisions.Add((entry, Bool(record, "isArchived")
                && string.Equals(Text(record, "cliSessionId"), entry.CliSessionId, StringComparison.OrdinalIgnoreCase)));
        }
        foreach (var (entry, archived) in decisions)
            if (archived) ledger.Retire(entry.Target, entry.File, _time.GetUtcNow());
            else ledger.CancelIntent(entry.Target, entry.File);
        return decisions.Count > 0;
    }

    /// <summary>
    /// Keeps the title of each copy this utility made in step with its original, so a conversation has one name in
    /// every account. A copy is left alone once it is that account's own conversation: continued under a new
    /// transcript id, used there more recently than the original, or renamed there since its title was last set.
    /// Only the title fields change; the copy stays as recognisable as before (its ledger hash follows when it was
    /// still exactly as written).
    /// </summary>
    private int FollowTitles(ClaudeShelfLedger ledger, ClaudeShelfPlan plan, Func<bool> desktopClosed, int created)
    {
        var retitled = 0;
        foreach (var entry in ledger.Created.Where(entry => !entry.Pending && !entry.Retired).ToArray())
        {
            // The same holds as for filling: only lists in this run, none held (unknown writers, recovery files,
            // unreadable records), and no conversation a live CLI is writing.
            if (!plan.Fingerprints.ContainsKey(entry.Target) || !plan.Fingerprints.ContainsKey(entry.Source)
                || plan.Held.ContainsKey(entry.Target) || plan.LiveIds.Contains(entry.CliSessionId.ToLowerInvariant())
                || entry.Source.Split('/') is not [var account, var org] || !Guid.TryParse(account, out _) || !Guid.TryParse(org, out _)
                || !entry.SourceFile.StartsWith("local_", StringComparison.Ordinal) || entry.SourceFile.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || ClaudeShelfLedger.EntryPath(_paths.SessionsRoot, entry) is not { } path
                || ReadRecord(Path.Combine(_paths.SessionsRoot, account, org, entry.SourceFile)) is not { } source) continue;
            byte[] before;
            try { before = File.ReadAllBytes(path); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { continue; }
            JsonObject? copy;
            try { copy = JsonNode.Parse(before) as JsonObject; }
            catch (JsonException) { continue; }
            if (copy is null) continue;
            var current = Text(copy, "title");
            var untouched = Convert.ToHexString(SHA256.HashData(before)) == entry.Sha256;
            if (Bool(copy, "isArchived")
                || !string.Equals(Text(copy, "cliSessionId"), entry.CliSessionId, StringComparison.OrdinalIgnoreCase)
                || Number(copy, "lastActivityAt") > Number(source, "lastActivityAt")) continue;
            var title = Text(source, "title");
            if (entry.Title is null)
            {
                // An older entry starts tracking once the copy shows the original's title. Until then only a copy still
                // exactly as written proves its name was never changed there; the app's rename history cannot, as it
                // records neither a first name nor a change of case or spacing.
                if (current is not null && current == title) { ledger.Retitle(entry, current, entry.Sha256); continue; }
                if (!untouched) continue;
            }
            else if (current != entry.Title) continue;
            if (string.IsNullOrWhiteSpace(title) || title == current) continue;
            copy["title"] = title;
            if (source["titleSource"] is JsonValue titleSource) copy["titleSource"] = titleSource.DeepClone();
            var after = JsonSerializer.SerializeToUtf8Bytes(copy, RecordJson);
            if (!desktopClosed()) throw new ClaudeDesktopRunningException(created);
            if (!Replace(path, before, after, desktopClosed))
            {
                // The old name is still showing: leave the run unfinished so it is retried, not reported as done.
                plan.Held[entry.Target] = "retitle_failed";
                continue;
            }
            // Only the bytes written here become this utility's; a copy the app had changed stays changed.
            ledger.Retitle(entry, title, untouched ? Convert.ToHexString(SHA256.HashData(after)) : entry.Sha256);
            retitled++;
        }
        return retitled;
    }

    /// <summary>Replaces a record with <paramref name="after"/> only if it still holds exactly <paramref name="before"/>
    /// and Desktop is still closed at the last moment. The swap itself is atomic, but nothing locks the app out between
    /// that last check and the move: an app started in that instant could still read the old record, or write over the
    /// new one.</summary>
    private static bool Replace(string path, byte[] before, byte[] after, Func<bool> desktopClosed)
    {
        var partial = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + PartialSuffix);
        try
        {
            using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                       FileOptions.WriteThrough))
            { stream.Write(after); stream.Flush(flushToDisk: true); }
            if (!desktopClosed() || !File.ReadAllBytes(path).AsSpan().SequenceEqual(before)) return false;
            File.Move(partial, path, overwrite: true);
            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
        finally { if (File.Exists(partial)) File.Delete(partial); }
    }

    private static long Number(JsonObject record, string field) =>
        record[field] is JsonValue value && value.TryGetValue<long>(out var number) ? number : 0;

    /// <summary>Archives a superseded copy in place, the way the app archives a session; nothing is deleted.</summary>
    private static bool Archive(string path) => Rewrite(path, record => record["isArchived"] = true);

    /// <summary>Rewrites one record in place through a temporary file, changing only what <paramref name="change"/> sets.</summary>
    private static bool Rewrite(string path, Action<JsonObject> change)
    {
        if (ReadRecord(path) is not { } record) return false;
        change(record);
        var partial = Path.Combine(Path.GetDirectoryName(path)!, "." + Path.GetFileName(path) + PartialSuffix);
        try
        {
            using (var stream = new FileStream(partial, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096,
                       FileOptions.WriteThrough))
            { stream.Write(JsonSerializer.SerializeToUtf8Bytes(record, RecordJson)); stream.Flush(flushToDisk: true); }
            File.Move(partial, path, overwrite: true);
            return true;
        }
        finally { if (File.Exists(partial)) File.Delete(partial); }
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
            if (ReconcileArchives(ledger)) ledger.Save(_paths.LedgerDirectory);
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
                    _time.GetUtcNow(), appVersion, Pending: true, Title: Text(action.Record, "title")));
            }).ToArray();
            // Record intent first: after a crash the next load keeps only files whose content matches.
            ledger.Created.AddRange(prepared.Select(item => item.Entry));
            if (prepared.Length > 0) ledger.Save(_paths.LedgerDirectory);
            var created = 0;
            var advanced = 0;
            List<ClaudeShelfRetirement> retired = [];
            ClaudeSidebarPrefs.Outcome? prefs = null;
            var retitled = 0;
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
                // Records that did not take their newest state: nothing is retired in their favour.
                var unadvanced = new HashSet<(string Target, string File)>();
                var written = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var advance in plan.Advances)
                {
                    if (!desktopClosed()) throw new ClaudeDesktopRunningException(created + advanced);
                    if (AdvanceInPlace(ledger, advance, targets[advance.Target], desktopClosed, appVersion, written)) { advanced++; continue; }
                    // The older state still shows: leave the run unfinished so it is retried, not reported as done.
                    unadvanced.Add((advance.Target, advance.File));
                    plan.Held[advance.Target] = "advance_failed";
                }
                foreach (var retirement in plan.Retirements)
                {
                    if (!desktopClosed()) throw new ClaudeDesktopRunningException(created + advanced);
                    if (unadvanced.Contains((retirement.Target, retirement.SuccessorFile))) continue;
                    var directory = ShelfDirectory(targets[retirement.Target]);
                    var path = Path.Combine(directory, retirement.File);
                    // The newer record must be there, the copy still exactly as written and the transcripts as the plan read
                    // them, before it is put away.
                    if (!File.Exists(Path.Combine(directory, retirement.SuccessorFile)) || !File.Exists(path)
                        || Hash(path) != plan.Fingerprints[retirement.Target].GetValueOrDefault(retirement.File)
                        || !Unchanged(retirement.Transcripts)) continue;
                    // Record the intent first: the next run either confirms the archive from the file or cancels it.
                    ledger.Intend(retirement.Target, retirement.File, retirement.SuccessorFile);
                    ledger.Save(_paths.LedgerDirectory);
                    bool archived;
                    try { archived = Archive(path); }
                    catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { archived = false; }
                    if (!archived)
                    {
                        // The duplicate is still listed: report the list as held so the run is retried, not done.
                        ledger.CancelIntent(retirement.Target, retirement.File);
                        plan.Held[retirement.Target] = "archive_failed";
                        continue;
                    }
                    ledger.Retire(retirement.Target, retirement.File, _time.GetUtcNow());
                    retired.Add(retirement);
                }
                retitled = FollowTitles(ledger, plan, desktopClosed, created + advanced);
                if (_paths.DesktopConfig is { } config)
                {
                    if (!desktopClosed()) throw new ClaudeDesktopRunningException(created + advanced);
                    var moves = ledger.PendingMoves();
                    ClaudeSidebarPrefs.ISource source = config.EndsWith(".json", StringComparison.OrdinalIgnoreCase)
                        ? new ClaudeSidebarPrefs.SettingsFileSource(config, _paths.LedgerDirectory)
                        : new ClaudeSidebarPrefs.LocalStorageSource(new ClaudeLocalStorage(config), _paths.LedgerDirectory, _time.GetUtcNow);
                    prefs = ClaudeSidebarPrefs.Sync(source, _paths.LedgerDirectory, ClaudeSidebarPrefs.Links(ledger, _paths.SessionsRoot),
                        moves.ToDictionary(entry => ClaudeSidebarPrefs.ListId(entry.File), entry => ClaudeSidebarPrefs.ListId(entry.MovedTo!)),
                        desktopClosed);
                    if (prefs.Complete) foreach (var entry in moves) ledger.FinishMove(entry);
                }
            }
            finally
            {
                ledger.SettlePending(_paths.SessionsRoot);
                ledger.AddRun(_time.GetUtcNow(), "apply", appVersion, created, plan, retired.Count, prefs, retitled, advanced);
                ledger.Save(_paths.LedgerDirectory);
            }
            return new ClaudeShelfResult(created, plan, retired.Count, prefs, retitled, advanced);
        }
        finally { RunGate.Release(); }
    }

    /// <summary>
    /// Writes one planned advance, only while the record still holds exactly the bytes the plan read and, at the last
    /// moment, Desktop is still closed, everything the decision read is as it read it (a CLI may have resumed a transcript
    /// and finished since the plan) and no transcript of the lineage has a writer. Otherwise the record is left for the
    /// next run to plan again. The record as it was is saved beside the ledger and the intent recorded before the record
    /// is replaced, so the next run settles an interrupted one from the file. The ledger's copies are left as they are:
    /// an advanced copy no longer holds the transcript the ledger names, so it is no longer this utility's to retitle,
    /// retire or undo.
    /// </summary>
    private bool AdvanceInPlace(ClaudeShelfLedger ledger, ClaudeShelfAdvance advance, ClaudeShelfAccount target,
        Func<bool> desktopClosed, string? appVersion, Dictionary<string, string> written)
    {
        var path = Path.Combine(ShelfDirectory(target), advance.File);
        byte[] before;
        try { before = File.ReadAllBytes(path); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
        if (Convert.ToHexString(SHA256.HashData(before)) != advance.Fingerprint) return false;
        var after = JsonSerializer.SerializeToUtf8Bytes(advance.Record, RecordJson);
        var afterHash = Convert.ToHexString(SHA256.HashData(after));
        var at = _time.GetUtcNow();
        string backup;
        try { backup = ClaudeShelfLedger.KeepBackup(_paths.LedgerDirectory, advance.File, at, before); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return false; }
        var intent = ledger.IntendAdvance(advance, afterHash, backup, at, appVersion);
        ledger.Save(_paths.LedgerDirectory);
        var done = Replace(path, before, after, () => desktopClosed() && Unchanged(advance.Transcripts, advance.Sources, written)
            && _liveWriters() is { Unknown: false } writers && !advance.Lineage.Any(writers.Ids.Contains));
        ledger.SettleAdvance(intent, done);
        if (done) written[path] = afterHash;
        return done;
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
            foreach (var entry in ledger.Created.Where(entry => !entry.Retired).ToArray())
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

    private string? Ineligible(JsonObject record, IReadOnlyDictionary<string, string> transcripts)
    {
        if (Bool(record, "isArchived")) return "archived";
        var cli = Text(record, "cliSessionId");
        if (cli is null || !Guid.TryParse(cli, out _)) return "no_current_id";
        if (RemoteFields.Any(field => record[field] is not null)) return "remote";
        if (WorktreeFields.Any(field => record[field] is not null)) return "worktree";
        var cwd = Text(record, "cwd");
        if (string.IsNullOrWhiteSpace(cwd) || !Path.IsPathFullyQualified(cwd)) return "no_cwd";
        // The app deletes a folderless session's scratch folder without checking other accounts.
        if (IsWithin(cwd, _paths.ScratchRoot)) return "scratch";
        if (!Directory.Exists(cwd)) return "missing_cwd";
        return transcripts.ContainsKey(cli.ToLowerInvariant()) ? null : "missing_transcript";
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

    /// <summary>Every transcript by id, at the first project folder holding it (where the session plan finds it too).</summary>
    private IReadOnlyDictionary<string, string> TranscriptIds()
    {
        var ids = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!Directory.Exists(_paths.ProjectsRoot)) return ids;
        foreach (var project in Directory.EnumerateDirectories(_paths.ProjectsRoot))
            foreach (var file in Directory.EnumerateFiles(project, "*.jsonl"))
                ids.TryAdd(Path.GetFileNameWithoutExtension(file).ToLowerInvariant(), file);
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

internal enum ClaudeShelfAdvanceState { Pending, Done, Unconfirmed }

/// <summary>Tool-owned record of what it created and advanced. Compatible with the 2026-09-30 spike ledger. Creation and
/// advance history is kept indefinitely: it is the evidence that a record the user removed must not come back.</summary>
internal sealed class ClaudeShelfLedger
{
    private const int MaxRuns = 50;
    private const int MaxBackups = 50;
    /// <summary>Beside the ledger: the newest advanced records as they were before.</summary>
    internal const string BackupFolder = "advanced";
    internal sealed record Entry(string Target, string File, string Sha256, string CliSessionId, string Source,
        string SourceFile, DateTimeOffset CreatedAt, string? AppVersion, bool Pending = false,
        DateTimeOffset? RetiredAt = null, string? MovedTo = null, bool MoveDone = false, string? Title = null)
    {
        internal bool Retired => RetiredAt is not null;
    }
    /// <summary>A record this utility brought from one transcript to its conversation's newer one, with the record's hash
    /// before and after and, for the newest ones, the name of its copy as it was in <see cref="BackupFolder"/>. Pending
    /// until the rewrite is confirmed; one an interrupted run left whose record now holds neither state is unconfirmed.</summary>
    internal sealed record AdvanceEntry(string Target, string File, string FromCliSessionId, string CliSessionId,
        string Before, string After, string? Backup, DateTimeOffset At, string? AppVersion, ClaudeShelfAdvanceState State);
    internal List<Entry> Created { get; } = [];
    internal List<AdvanceEntry> Advanced { get; } = [];
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
                Read("srcFile") ?? "", created, Read("appVersion"),
                RetiredAt: DateTimeOffset.TryParse(Read("retiredAt"), out var retiredAt) ? retiredAt : null,
                MovedTo: Read("movedTo"), MoveDone: entry["moveDone"] is JsonValue done && done.TryGetValue<bool>(out var finished) && finished,
                Title: Read("title"));
            // An interrupted run: keep the intent only when the published file has exactly the intended content.
            if (pending && (EntryPath(sessionsRoot, loaded) is not { } published || !File.Exists(published)
                            || ClaudeSessionShelf.Hash(published) != loaded.Sha256)) continue;
            ledger.Created.Add(loaded);
        }
        foreach (var item in root["advanced"] as JsonArray ?? [])
        {
            if (item is not JsonObject entry) continue;
            string? Read(string name) => entry[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
            if (Read("dst") is not { } target || Read("file") is not { } file || Read("from") is not { } from
                || Read("to") is not { } to || Read("before") is not { } before || Read("after") is not { } after
                || !Enum.TryParse<ClaudeShelfAdvanceState>(Read("state"), ignoreCase: true, out var state)) continue;
            DateTimeOffset.TryParse(Read("at"), out var at);
            var loaded = new AdvanceEntry(target.ToLowerInvariant(), file, from, to, before.ToUpperInvariant(),
                after.ToUpperInvariant(), Read("backup"), at, Read("appVersion"), state);
            if (state == ClaudeShelfAdvanceState.Pending)
            {
                // An interrupted run: the record shows whether it was rewritten. One still as it was changed nothing.
                var hash = EntryPath(sessionsRoot, loaded.Target, loaded.File) is { } advanced && File.Exists(advanced)
                    ? ClaudeSessionShelf.Hash(advanced) : null;
                if (hash == loaded.Before) continue;
                loaded = loaded with
                { State = hash == loaded.After ? ClaudeShelfAdvanceState.Done : ClaudeShelfAdvanceState.Unconfirmed };
            }
            ledger.Advanced.Add(loaded);
        }
        foreach (var run in root["runs"] as JsonArray ?? [])
            if (run is JsonObject value) ledger._runs.Add((JsonObject)value.DeepClone());
        return ledger;
    }

    internal static string? EntryPath(string sessionsRoot, Entry entry) => EntryPath(sessionsRoot, entry.Target, entry.File);

    internal static string? EntryPath(string sessionsRoot, string target, string file)
    {
        var parts = target.Split('/');
        if (parts.Length != 2 || !Guid.TryParse(parts[0], out _) || !Guid.TryParse(parts[1], out _)
            || file.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || !file.StartsWith("local_", StringComparison.Ordinal)) return null;
        return Path.Combine(sessionsRoot, parts[0], parts[1], file);
    }

    /// <summary>Saves a record as it was before it is advanced, beside the ledger; returns the copy's name.</summary>
    internal static string KeepBackup(string directory, string file, DateTimeOffset at, byte[] bytes)
    {
        var folder = Directory.CreateDirectory(Path.Combine(directory, BackupFolder)).FullName;
        var name = at.UtcDateTime.ToString("yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture) + "_" + file;
        using var stream = new FileStream(Path.Combine(folder, name), FileMode.Create, FileAccess.Write, FileShare.None, 4096,
            FileOptions.WriteThrough);
        stream.Write(bytes);
        stream.Flush(flushToDisk: true);
        return name;
    }

    /// <summary>Records an advance before its record is rewritten.</summary>
    internal AdvanceEntry IntendAdvance(ClaudeShelfAdvance advance, string after, string backup, DateTimeOffset at,
        string? appVersion)
    {
        var entry = new AdvanceEntry(advance.Target, advance.File, advance.FromCliSessionId, advance.CliSessionId,
            advance.Fingerprint, after, backup, at, appVersion, ClaudeShelfAdvanceState.Pending);
        Advanced.Add(entry);
        return entry;
    }

    /// <summary>Marks an advance done once its record holds the new state; one that changed nothing is forgotten.</summary>
    internal void SettleAdvance(AdvanceEntry entry, bool done)
    {
        var index = Advanced.IndexOf(entry);
        if (index < 0) return;
        if (done) Advanced[index] = entry with { State = ClaudeShelfAdvanceState.Done };
        else Advanced.RemoveAt(index);
    }

    /// <summary>Records which newer record will replace a copy, before the copy is archived.</summary>
    internal void Intend(string target, string file, string successor)
    {
        var index = Created.FindIndex(entry => entry.Target == target && entry.File == file && !entry.Retired);
        if (index >= 0) Created[index] = Created[index] with { MovedTo = successor };
    }

    internal void CancelIntent(string target, string file)
    {
        var index = Created.FindIndex(entry => entry.Target == target && entry.File == file && !entry.Retired);
        if (index >= 0) Created[index] = Created[index] with { MovedTo = null };
    }

    /// <summary>Marks a copy archived once the archived file is in place.</summary>
    internal void Retire(string target, string file, DateTimeOffset at)
    {
        var index = Created.FindIndex(entry => entry.Target == target && entry.File == file && !entry.Retired);
        if (index >= 0) Created[index] = Created[index] with { RetiredAt = at };
    }

    /// <summary>Archived copies whose pins and groups have not reached their successor yet.</summary>
    internal IReadOnlyList<Entry> PendingMoves() => Created.Where(entry => entry.Retired && entry.MovedTo is not null && !entry.MoveDone).ToArray();

    /// <summary>Records the title last set on a copy, and its new hash when the copy was still exactly as written.</summary>
    internal void Retitle(Entry entry, string title, string sha256)
    {
        var index = Created.IndexOf(entry);
        if (index >= 0) Created[index] = entry with { Title = title, Sha256 = sha256 };
    }

    internal void FinishMove(Entry entry)
    {
        var index = Created.IndexOf(entry);
        if (index >= 0) Created[index] = entry with { MoveDone = true };
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

    internal void AddRun(DateTimeOffset at, string mode, string? appVersion, int count, ClaudeShelfPlan? plan,
        int retired = 0, ClaudeSidebarPrefs.Outcome? prefs = null, int retitled = 0, int advanced = 0)
    {
        var run = new JsonObject { ["at"] = at.ToString("O"), ["mode"] = mode, ["appVersion"] = appVersion, ["count"] = count };
        if (advanced > 0) run["advanced"] = advanced;
        if (retired > 0) run["archived"] = retired;
        if (retitled > 0) run["retitled"] = retitled;
        if (prefs is not null)
            run["sidebar"] = new JsonObject { ["pinned"] = prefs.Pinned, ["unpinned"] = prefs.Unpinned, ["grouped"] = prefs.Grouped,
                ["ungrouped"] = prefs.Ungrouped, ["moved"] = prefs.Moved, ["skipped"] = prefs.Skipped };
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
        // Only the newest records as they were are kept, and every one an unsettled advance still names.
        var backups = Advanced.Where(entry => entry.Backup is not null).TakeLast(MaxBackups)
            .Concat(Advanced.Where(entry => entry.State == ClaudeShelfAdvanceState.Pending && entry.Backup is not null))
            .Select(entry => entry.Backup!).ToHashSet(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < Advanced.Count; index++)
            if (Advanced[index].Backup is { } backup && !backups.Contains(backup))
                Advanced[index] = Advanced[index] with { Backup = null };
        var root = new JsonObject
        {
            ["version"] = 1,
            ["created"] = new JsonArray(Created.Select(entry => (JsonNode?)new JsonObject
            {
                ["dst"] = entry.Target, ["file"] = entry.File, ["sha256"] = entry.Sha256,
                ["cliSessionId"] = entry.CliSessionId, ["src"] = entry.Source, ["srcFile"] = entry.SourceFile,
                ["createdAt"] = entry.CreatedAt.ToString("O"), ["appVersion"] = entry.AppVersion,
                ["pending"] = entry.Pending ? true : null, ["retiredAt"] = entry.RetiredAt?.ToString("O"),
                ["movedTo"] = entry.MovedTo, ["moveDone"] = entry.MoveDone ? true : null, ["title"] = entry.Title
            }).ToArray()),
            ["advanced"] = new JsonArray(Advanced.Select(entry => (JsonNode?)new JsonObject
            {
                ["dst"] = entry.Target, ["file"] = entry.File, ["from"] = entry.FromCliSessionId, ["to"] = entry.CliSessionId,
                ["before"] = entry.Before, ["after"] = entry.After, ["backup"] = entry.Backup, ["at"] = entry.At.ToString("O"),
                ["appVersion"] = entry.AppVersion, ["state"] = entry.State.ToString().ToLowerInvariant()
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
        // Copies no entry names any more go once the ledger that let them go is written.
        var folder = Path.Combine(directory, BackupFolder);
        if (!Directory.Exists(folder)) return;
        foreach (var backup in Directory.EnumerateFiles(folder).Where(backup => !backups.Contains(Path.GetFileName(backup))))
            try { File.Delete(backup); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
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
