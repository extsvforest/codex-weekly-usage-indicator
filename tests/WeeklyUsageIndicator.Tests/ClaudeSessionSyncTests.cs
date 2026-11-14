using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using WeeklyUsageIndicator;
using Changes = WeeklyUsageIndicator.ClaudeSessionLayout.Changes;
using OtherAccount = WeeklyUsageIndicator.ClaudeSessionSync.Other;
using Record = WeeklyUsageIndicator.ClaudeSessionSync.Record;
using Shown = WeeklyUsageIndicator.ClaudeSessionLayout.Shown;
using SyncLayout = WeeklyUsageIndicator.ClaudeSessionSync.Layout;
using SyncPlan = WeeklyUsageIndicator.ClaudeSessionSync.Plan;
using Target = WeeklyUsageIndicator.ClaudeSessionLayout.Target;

internal static class ClaudeSessionSyncTests
{
    private const string CurrentAccount = "46411433-0040-4598-a1bd-a5444a5eb7b4", CurrentOrg = "a8f42351-0c23-4928-87ef-ecb3e3bf2b10";
    private const string ReferenceAccount = "0998b264-7315-4fdb-8634-fc7f3f827316", ReferenceOrg = "7f98dbbd-af39-46bf-9609-cfd3972b3633";
    private static string CurrentKey => CurrentAccount + "/" + CurrentOrg;
    private static string ReferenceKey => ReferenceAccount + "/" + ReferenceOrg;
    private const string ThirdAccount = "7a7a7a7a-0000-4000-8000-00000000007a", ThirdOrg = "7b7b7b7b-0000-4000-8000-00000000007b";
    private static string ThirdKey => ThirdAccount + "/" + ThirdOrg;
    // The three accounts of the stories: 계정1 holds the records the app wrote, 계정2 and 계정3 the widget's copies.
    private static string K1 => ReferenceKey;
    private static string K2 => CurrentKey;
    private static string K3 => ThirdKey;
    /// <summary>The fixtures' clock: when accounts changed things and when the app came to and left them, in minutes.</summary>
    private static readonly DateTimeOffset Morning = new(2026, 10, 20, 9, 0, 0, TimeSpan.FromHours(9));
    private static readonly IReadOnlyDictionary<string, string?> NoCopies = new Dictionary<string, string?>(StringComparer.Ordinal);

    internal static Task RunAsync()
    {
        TestCombinedUsage();
        TestPlan();
        TestOlderStates();
        TestCopies();
        TestNewestChange();
        TestGroupOrder();
        TestSidebarSections();
        TestLayoutStore();
        TestPinList();
        TestTranscript();
        TestBuildFromDisk();
        TestGroupsFromLocalStorage();
        TestObserve();
        TestCheckpoint();
        TestUndoneAfterPlan();
        TestPassedThrough();
        TestPartlyApplied();
        TestSortingResumed();
        TestSortingOnDisk();
        TestLastPin();
        TestFirstFormatEvidence();
        TestOlderStateFollowsRename();
        TestReopenedOlderState();
        TestRenamedWhileAside();
        TestEditedWhileAside();
        TestDeletedAsPlanned();
        TestReorderedWithLeftover();
        TestRetargetedWhileAside();
        TestRemovedBeforeApplied();
        TestUnseenStay();
        TestNestedLinks();
        TestNestedPlan();
        TestNestedOnDisk();
        TestLeftFamilyOnDisk();
        TestLeftFamilyBeforeUpgrade();
        TestLeftFamilyChanged();
        TestLeftFamilyUnclear();
        TestArchivedInRemadeGroup();
        TestArchivedElsewhere();
        TestSettingsFollowNewest();
        TestSettingsNoChange();
        TestSettingsStartingValue();
        TestSettingsPlanInProgress();
        TestSettingsScope();
        TestSettingsCallingSession();
        TestSettingsLayoutStore();
        TestSettingsNewRecord();
        TestSettingsRead();
        return Task.CompletedTask;
    }

    private static DateTimeOffset At(int minutes) => Morning.AddMinutes(minutes);

    private static string Cli(int number) => "00000000-0000-4000-8000-" + number.ToString("x12", CultureInfo.InvariantCulture);

    private static Record R(string id, string? title, bool pinned, string? cli, string[]? prior = null, long activity = 0,
        bool branch = false, bool renamed = false) => new(id, title, pinned, cli, prior ?? [], activity, branch, renamed);

    /// <summary>Groups in sidebar order, each written "id=name", and the sessions filed in them.</summary>
    private static SyncLayout Groups(string[] groups, params (string Session, string Group)[] filed) =>
        new(groups.Select(group => group.Split('=', 2) is [var id, var name] ? new ClaudeSessionSync.Group(id, name)
                : throw new ArgumentException(group)).ToArray(),
            filed.ToDictionary(pair => pair.Session, pair => pair.Group, StringComparer.Ordinal));

    /// <summary>The widget's copies in a list, each with the title the ledger says it wrote (null: not tracked).</summary>
    private static Dictionary<string, string?> Copies(params (string Id, string? Written)[] copies) =>
        copies.ToDictionary(copy => copy.Id, copy => copy.Written, StringComparer.Ordinal);

    private static OtherAccount Other(string account, string? label, IReadOnlyList<Record> records, SyncLayout? layout,
        DateTimeOffset? leftAt = null, IReadOnlyDictionary<string, string?>? copies = null, IReadOnlyList<Record>? archived = null,
        IReadOnlyDictionary<string, DateTimeOffset>? writes = null) =>
        new(account, label, leftAt, records, layout, copies ?? NoCopies, archived, writes);

    /// <summary>A plan for the current account with the widget's copies known (none unless given) and nothing kept unless
    /// <paramref name="kept"/> holds it. Plans name accounts by the labels the widget keeps.</summary>
    private static SyncPlan Compute(IReadOnlyList<Record> records, SyncLayout? layout, IReadOnlyList<OtherAccount> others,
        ClaudeSessionLayout? kept = null, IReadOnlyDictionary<string, string?>? copies = null, bool pinsKnown = true,
        bool groupsKnown = true, ClaudeSessionSync.Lineage? lineage = null, IReadOnlyList<string>? warnings = null,
        string? label = null, bool ledgerKnown = true, bool pinsLost = false, IReadOnlyList<Record>? archived = null,
        bool listsWhole = true, IReadOnlyDictionary<string, DateTimeOffset>? writes = null, DateTimeOffset? now = null)
    {
        kept ??= new ClaudeSessionLayout();
        foreach (var other in others)
            if (!kept.Accounts.ContainsKey(other.Account)) kept.Accounts[other.Account] = new(other.Label ?? "");
        return ClaudeSessionSync.Compute(CurrentKey, label, records, layout, others, kept, pinsKnown, lineage, warnings, groupsKnown,
            ledgerKnown ? copies ?? NoCopies : null, pinsLost, archived, listsWhole, writes, now ?? Morning);
    }

    /// <summary>What a record shows, as a checkpoint keeps it.</summary>
    private static Shown ShownOf(Record record, SyncLayout? layout, string? written = null)
    {
        var title = string.IsNullOrWhiteSpace(record.Title) ? null : record.Title;
        var shown = new Shown(record.Pinned, false, null, title, written, record.Model, record.Effort, record.Mode);
        if (layout is null) return shown;
        if (!layout.Assignments.TryGetValue(record.SessionId, out var id)) return shown with { HasGroup = true };
        return layout.Groups.FirstOrDefault(group => group.Id == id) is { } group ? shown with { HasGroup = true, Group = group.Name } : shown;
    }

    /// <summary>A conversation's record with its model, effort and permission mode (null unknown, empty none), last used
    /// <paramref name="used"/> minutes into the fixtures' clock.</summary>
    private static Record S(string id, string title, int cli, int used, string? model = null, string? effort = null, string? mode = null,
        bool pinned = false) =>
        new(id, title, pinned, Cli(cli), [], At(used).ToUnixTimeMilliseconds(), Model: model, Effort: effort, Mode: mode);

    /// <summary>A checkpoint of what <paramref name="records"/> show in <paramref name="layout"/>.</summary>
    private static ClaudeSessionLayout.Checkpoint PointOf(IReadOnlyList<Record> records, SyncLayout? layout, DateTimeOffset at,
        int stay = 0, IReadOnlyDictionary<string, string?>? copies = null) =>
        new(at, stay, layout?.Groups.Select(group => new ClaudeSessionLayout.Named(group.Id, group.Name)).ToArray(),
            records.ToDictionary(record => record.SessionId, record => ShownOf(record, layout, copies?.GetValueOrDefault(record.SessionId)),
                StringComparer.Ordinal));

    /// <summary>Keeps an account checkpointed showing what it shows now in a stay before the one it is in, so nothing
    /// changed there since; the app last left it at <paramref name="leftAt"/>.</summary>
    private static void Steady(ClaudeSessionLayout kept, string account, string label, IReadOnlyList<Record> records, SyncLayout? layout,
        DateTimeOffset? leftAt, IReadOnlyDictionary<string, string?>? copies = null)
    {
        kept.Accounts[account] = new(label, leftAt, null, 1);
        kept.Checkpoints[account] = PointOf(records, layout, (leftAt ?? Morning).AddMinutes(-5), 0, copies);
    }

    private static void Changed(ClaudeSessionLayout kept, string account, string session, Changes change)
    {
        if (!kept.Changed.TryGetValue(account, out var changes)) kept.Changed[account] = changes = new(StringComparer.Ordinal);
        changes[session] = change;
    }

    /// <summary>A change of one or more values of a conversation, all at <paramref name="at"/> (combine with <c>Over</c>
    /// for different times).</summary>
    private static Changes Change(DateTimeOffset at, bool? pinned = null, string? group = null, bool ungrouped = false,
        string? title = null, string? by = null) =>
        new(pinned is { } pin ? new ClaudeSessionLayout.Stamp<bool>(pin, at, by) : null,
            group is not null || ungrouped ? new ClaudeSessionLayout.Stamp<string?>(group, at, by) : null,
            title is null ? null : new ClaudeSessionLayout.Stamp<string>(title, at, by));

    private static ClaudeSessionLayout.GroupChange ListChange(string[] order, DateTimeOffset at, string? by = null,
        params (string From, string To)[] renamed) =>
        new(order, renamed.ToDictionary(pair => pair.From, pair => pair.To, StringComparer.Ordinal), at, by);

    /// <summary>Groups a plan in progress renames out of the way, by id: each from its name to that name with the mark.</summary>
    private static Dictionary<string, ClaudeSessionLayout.Moved> Aside(params (string Id, string From)[] groups) =>
        groups.ToDictionary(group => group.Id, group => new ClaudeSessionLayout.Moved(group.From, group.From + ClaudeSessionSync.MovingMark + ")"),
            StringComparer.Ordinal);

    /// <summary>A fresh fixture folder under the test root (GFS_ACCOUNT_TEST_ROOT, else the temporary work area).</summary>
    private static string TempRoot(string name) => Directory.CreateDirectory(Path.Combine(Path.GetFullPath(
            Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT") ?? Path.Combine(Path.GetTempPath(), "gfs-agent", "claude-sync-tests")),
        name + "-" + Guid.NewGuid().ToString("N"))).FullName;

    /// <summary>The widget's layout with these accounts, each with when the app last left it; nothing checkpointed.</summary>
    private static void Keep(string ledger, params (string Key, string Label, DateTimeOffset? LeftAt)[] accounts)
    {
        var layout = new ClaudeSessionLayout();
        foreach (var (key, label, leftAt) in accounts) layout.Accounts[key] = new(label, leftAt);
        layout.Save(ledger);
    }

    private static void TestCombinedUsage()
    {
        var now = DateTimeOffset.UtcNow;
        ClaudeProfile Profile(string id, string? identity) => new(id, id, @"C:\fixture\" + id, false,
            identity is null ? null : new(identity, id + "@example.test", "max"), identity is null ? null : now);
        ClaudeUsageResult Usage(double used, DateTimeOffset? reset, DateTimeOffset at, bool stale = false) =>
            new(new(null, new(used, reset), null), at, null, stale);
        var a = new ClaudeSummaryRow(Profile("a", "id-a"), Usage(20, now.AddDays(2), now.AddMinutes(-5)), null);
        var b = new ClaudeSummaryRow(Profile("b", "id-b"), Usage(50, now.AddDays(1), now.AddMinutes(-1)), null);
        var total = ClaudeCombinedUsage.From([a, b], now);
        Check(total.Complete && total.Accounts == 2 && total.Remaining == 130 && total.Next?.Profile.Id == "b"
              && total.LatestReset == a.Usage!.Snapshot.Weekly!.ResetsAt && total.OldestObservation == a.Usage.LastUpdatedAt,
            "two fresh accounts are totalled at 100% each, with the soonest and latest resets");
        total = ClaudeCombinedUsage.From([a, b, new ClaudeSummaryRow(Profile("a-again", "id-a"), null, null)], now);
        Check(total.Complete && total.Accounts == 2 && total.Remaining == 130, "two profiles signed in to one account count once");
        var stale = b with { Usage = b.Usage! with { IsStale = true } };
        total = ClaudeCombinedUsage.From([a, stale], now);
        Check(!total.Complete && total.Confirmed == 1 && total.Remaining == 80 && total.Uncertain?.Profile.Id == "b",
            "a previous value withholds the total, and its earlier reset leaves the next reset uncertain");
        total = ClaudeCombinedUsage.From([a, b, new ClaudeSummaryRow(Profile("c", null), null, new ClaudeLoginRequiredException())], now);
        Check(!total.Complete && total.Accounts == 3 && total.Confirmed == 2 && total.Uncertain?.Profile.Id == "c",
            "an account that needs a sign-in withholds the total");
        var failed = b with { Failure = new IOException("fixture") };
        Check(!ClaudeCombinedUsage.From([a, failed], now).Complete, "a failed refresh withholds the total");
        var expired = b with { Usage = Usage(50, now.AddMinutes(-1), now.AddMinutes(-3)) };
        Check(ClaudeCombinedUsage.From([a, expired], now) is { Complete: false, Confirmed: 1 }, "a window past its reset is not counted");
        Check(ClaudeCombinedUsage.From([], now) is { Accounts: 0, Complete: false }, "no accounts, no total");
    }

    private static void TestPlan()
    {
        var current = new[]
        {
            R("local_c1", "옛 이름", true, "x"), R("local_c2", "같음", false, "y"), R("local_c3", "이어감", false, "z2", ["z"]),
            R("local_c4", "한쪽에만", true, "w"), R("local_c5", "뒤", false, "v")
        };
        var reference = new[]
        {
            R("local_r1", "새 이름", true, "x"), R("local_r2", "같음", true, "y"), R("local_r3", "원래", false, "z"),
            R("local_r5", "앞서 이어감", false, "v2", ["v"])
        };
        var currentLayout = Groups(["ga=A"], ("local_c1", "ga"), ("local_c5", "ga"));
        var referenceLayout = Groups(["ra=A", "rb=B", "rc=C"], ("local_r1", "rb"), ("local_r3", "ra"));
        // Nothing kept yet: each account offers all its own records show, as of when the app last left it, so the account
        // the app left later wins.
        var plan = Compute(current, currentLayout, [Other(ReferenceKey, "메인", reference, referenceLayout, At(0))], label: "서브");
        Check(plan.Status == "ok" && plan.CreateGroups!.SequenceEqual(["B", "C"]) && plan.RenameGroups!.Count == 0
              && plan.DeleteGroups!.Count == 0 && plan.GroupOrder!.SequenceEqual(["A", "B", "C"]) && plan.GroupOrderBy == "메인",
            "the group list follows the newer account's: the start already in place stays and the rest is made at the end");
        Check(plan.Moves!.Count == 3 && plan.Moves[0] is { Group: "A", GroupId: "ga" } toA && toA.Sessions.SequenceEqual(["local_c3"])
              && plan.Moves[1] is { Group: "B", GroupId: null } toB && toB.Sessions.SequenceEqual(["local_c1"])
              && plan.Moves[2] is { Group: null } outOf && outOf.Sessions.SequenceEqual(["local_c5"]),
            "sessions take the newer group, a continuation included: a group that stays by its id, one made anew by name, in list order");
        Check(plan.Pins!.Select(pin => (pin.Session, pin.Pinned, pin.By)).SequenceEqual([("local_c1", true, "메인"), ("local_c2", true, "메인")]),
            "pins follow; one moved into a group is pinned again, since the move unpins it");
        Check(plan.Renames!.Select(rename => (rename.Session, rename.Title)).SequenceEqual(
                  [("local_c1", "새 이름"), ("local_c3", "원래"), ("local_c5", "앞서 이어감")])
              && plan.Renames![0] is { From: "옛 이름", By: "메인" },
            "titles follow, matched by transcript id or as a continuation either way");
        Check(plan.Others!.Single() is { Label: "메인", Matched: 4, OnlyThere.Count: 0 } && plan.OnlyHere!.SequenceEqual(["한쪽에만"])
              && plan.Pins!.All(pin => pin.Session != "local_c4") && plan.Sidebars!.Count == 2 && plan.Warnings is null,
            "a session only this account has is left alone and named");
        // Left alike: this account's own values stay.
        var tie = Compute(current, currentLayout, [Other(ReferenceKey, "메인", reference, referenceLayout)]);
        Check(tie is { Moves.Count: 0, Pins.Count: 0, Renames.Count: 0, CreateGroups.Count: 0, GroupOrder: null },
            "what another account offers as of no later than this one's own values changes nothing");
        // An assignment to a group its account does not list offers no group: that session stays where it is.
        var danglingThere = Compute(current, currentLayout, [Other(ReferenceKey, null, reference,
            referenceLayout with { Assignments = new Dictionary<string, string> { ["local_r1"] = "gone", ["local_r3"] = "ra" } }, At(0))]);
        Check(danglingThere.Moves!.All(move => !move.Sessions.Contains("local_c1")) && danglingThere.Pins!.All(pin => pin.Session != "local_c1")
              && danglingThere.Warnings is null, "an assignment to a group the other account does not list moves nothing");
        var danglingHere = Compute(current, currentLayout with
            {
                Assignments = new Dictionary<string, string> { ["local_c1"] = "gone", ["local_c5"] = "ga" }
            },
            [Other(ReferenceKey, null, reference, referenceLayout, At(0))]);
        Check(danglingHere.Moves!.All(move => !move.Sessions.Contains("local_c1")) && danglingHere.Warnings!.SequenceEqual(["groups_unresolved"]),
            "a session here filed under a group this account does not list is not moved, and that is said");
        // One record there serves one session here: an older state or a branch of a matched conversation is left alone.
        var older = Compute(
            [R("local_now", "지금", false, "k2", ["k"]), R("local_old", "옛 상태", false, "k"), R("local_branch", "갈래", false, "b2", ["k2"])],
            null, [Other(ReferenceKey, null, [R("local_ref", "기준", false, "k2", ["k"])], null, At(0))]);
        Check(older.Renames!.Select(rename => rename.Session).SequenceEqual(["local_now"]) && older.Others!.Single().Matched == 1
              && older.OnlyHere!.Count == 2, "an older state or a branch of an already matched conversation is not renamed after it");
        var blind = Compute(current, null, [Other(ReferenceKey, null, reference, null, At(0))], warnings: ["groups_unreadable"]);
        Check(blind.CreateGroups!.Count == 0 && blind.Moves!.Count == 0 && blind.Warnings!.SequenceEqual(["groups_unreadable"])
              && blind.Pins!.Select(pin => pin.Session).SequenceEqual(["local_c2"]), "unreadable groups plan titles and pins only");
        var held = Compute(current, currentLayout, [Other(ReferenceKey, null, reference, referenceLayout, At(0))], groupsKnown: false);
        Check(held is { Moves.Count: 0, CreateGroups.Count: 0, GroupOrder: null } && held.Pins!.Single().Session == "local_c2",
            "another account's unreadable groups hold every group change");
        var partial = Compute(current, currentLayout, [Other(ReferenceKey, "메인", reference, referenceLayout, At(0))], listsWhole: false,
            warnings: ["list_incomplete"]);
        Check(partial is { Moves.Count: 0, Pins.Count: 0, Renames.Count: 0, CreateGroups.Count: 0 },
            "a list that could not be read whole decides no title, pin or group");
    }

    private static void TestOlderStates()
    {
        var origin = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        // Each transcript: when its file was created, and its last entry (hours after origin).
        ClaudeSessionSync.TranscriptSpan S(double createdHours, double lastHours) =>
            new(origin.AddHours(createdHours), origin.AddHours(lastHours), origin.AddHours(lastHours));
        var spans = new Dictionary<string, ClaudeSessionSync.TranscriptSpan>
        {
            ["a1"] = S(0, 1), ["a2"] = S(2, 24), ["b1"] = S(0, 5), ["b2"] = S(2, 24), ["s1"] = S(0, 1), ["s2"] = S(2, 24),
            ["x1"] = S(0, 1), ["f2"] = S(2, 24), ["u2"] = S(2, 24), ["h1"] = S(0, 2 + 1 / 3600.0), ["h2"] = S(2, 24),
            ["c1"] = S(0, 1), ["c2"] = S(2, 24),
            // m1 moved on to m2 at 2h, went on at 5h; m3 (11h) lists both, and m2 cannot be read.
            ["m1"] = S(0, 5), ["m3"] = S(11, 24),
            // n1 hands over to n2, whose newest state n3 is missing.
            ["n1"] = S(0, 1), ["n2"] = S(2, 3),
            // k2 was copied back: created after it was last written, so its creation dates nothing.
            ["k1"] = S(0, 1), ["k2"] = new(origin.AddHours(30), origin.AddHours(24), origin.AddHours(24)),
            // r1 went on in r2 at 2h and was used again at 5h; r2 was restored at 30h and written at 31h.
            ["r1"] = S(0, 5), ["r2"] = S(30, 31), ["q1"] = S(0, 1), ["q2"] = S(30, 31),
            // o1 lists o2 next, but o3 was created first.
            ["o1"] = S(0, 1), ["o2"] = S(3, 24), ["o3"] = S(2, 24)
        };
        // A transcript's first entry the earlier one does not hold: when it was created, unless restored since.
        var handOvers = new Dictionary<(string, string), DateTimeOffset> { [("r1", "r2")] = origin.AddHours(2), [("q1", "q2")] = origin.AddHours(2) };
        var lineage = new ClaudeSessionSync.Lineage("local_self", cli => spans.TryGetValue(cli, out var span) ? span : null,
            (earlier, next) => handOvers.TryGetValue((earlier, next), out var at) ? at : spans.TryGetValue(next, out var span) ? span.Created : null);
        var mixed = new[]
        {
            R("local_old", "대화", true, "a1", [], 10), R("local_new", "대화", false, "a2", ["a1"], 20),
            // Gone on with after the conversation moved on (5h > 2h), though the other side was written to later still.
            R("local_went_on", "갈래", false, "b1", [], 5), R("local_moved_on", "갈래", false, "b2", ["b1"], 30),
            R("local_self", "나", false, "s1", []), R("local_self_next", "나", false, "s2", ["s1"]),
            R("local_parent", "원본", false, "x1", []), R("local_fork", "포크", false, "f2", ["x1"], branch: true),
            R("local_undated", "날짜 없음", false, "u1", []), R("local_dated", "날짜 없음", false, "u2", ["u1"]),
            R("local_handing", "넘김", false, "h1", []), R("local_handed", "넘김", false, "h2", ["h1"]),
            R("local_hidden_turn", "가림", false, "m1", []), R("local_hiding", "가림", false, "m3", ["m1", "m2"]),
            R("local_ends_unseen", "끝 모름", false, "n1", []), R("local_middle", "끝 모름", false, "n2", ["n1"]),
            R("local_end", "끝 모름", false, "n3", ["n2"], 40),
            R("local_restored_from", "복원", false, "k1", []), R("local_restored", "복원", false, "k2", ["k1"]),
            R("local_used_again", "다시 씀", false, "r1", []), R("local_restored_next", "다시 씀", false, "r2", ["r1"]),
            R("local_quiet", "복원만", false, "q1", []), R("local_restored_only", "복원만", false, "q2", ["q1"]),
            R("local_listed", "순서", false, "o1", []), R("local_out_of_order", "순서", false, "o3", ["o1", "o2"])
        };
        var older = ClaudeSessionSync.OlderStates(mixed, lineage);
        Check(older.Keys.Order(StringComparer.Ordinal).SequenceEqual(["local_handing", "local_old"])
              && older["local_old"] is { Newest.SessionId: "local_new" } found && found.LastWritten == origin.AddHours(1)
              && found.MovedOn == origin.AddHours(2),
            "an older state has no entry after its conversation moved on; a branch written later, the calling session, a fork's origin, "
            + "an undated one, one whose lineage or newest state cannot be read, one dated only by a copied or restored file, "
            + "and one whose listed and created next transcripts disagree stay");
        Check(ClaudeSessionSync.OlderStates([mixed[1]], lineage, archived: [mixed[0]]) is { Count: 1 } shelved
              && shelved["local_old"].Newest.SessionId == "local_new"
              && ClaudeSessionSync.OlderStates([mixed[0]], lineage, archived: [mixed[1]]).Count == 0
              && ClaudeSessionSync.OlderStates([mixed[3]], lineage, archived: [mixed[2]]).Count == 0,
            "an archived record is found as an older state the same way (a branch is not), and never taken to continue one");

        var none = Groups([]);
        var current = new[] { R("local_old", "대화", true, "a1", [], 10), R("local_new", "대화", false, "a2", ["a1"], 20) };
        var reference = new[] { R("local_r_old", "대화", true, "a1", [], 10), R("local_r_new", "대화", false, "a2", ["a1"], 20) };
        var referenceLayout = Groups(["g=G"], ("local_r_old", "g"));
        // The newest states are the widget's copies, untouched: what the conversation shows is its older states'.
        var copiedHere = Copies(("local_new", "대화"));
        var copiedThere = Copies(("local_r_new", "대화"));
        var plan = Compute(current, none, [Other(ReferenceKey, "메인", reference, referenceLayout, At(0), copiedThere)], copies: copiedHere,
            lineage: lineage, label: "서브");
        Check(plan.CreateGroups!.SequenceEqual(["G"]) && plan.Moves!.Single() is { Group: "G" } move && move.Sessions.SequenceEqual(["local_new"])
              && plan.Pins!.Single() is { Session: "local_new", Pinned: true, By: "메인" } && plan.Others!.Single().Matched == 1
              && plan.Renames!.Count == 0,
            "another account's older state is seen through: its pin and group go to the newest state there and here");
        Check(plan.Archive!.Single() is { Session: "local_old", Pinned: true, Newest: "local_new", Ready: false }
              && plan.Pending!.Sessions["local_old"] is { Pinned: { Value: false } unpin } && unpin.At == DateTimeOffset.MinValue,
            "an older state waits until its newest state holds what it keeps; unpinning it on the way is nobody's choice");
        var moved = Groups(["g2=G"], ("local_new", "g2"));
        var after = Compute([current[0], current[1] with { Pinned = true }], moved,
            [Other(ReferenceKey, "메인", reference, referenceLayout, At(0), copiedThere)], copies: copiedHere, lineage: lineage, label: "서브");
        Check(after.Moves!.Count == 0 && after.Pins!.Count == 0 && after.Archive!.Single() is { Ready: true, Pinned: true },
            "once the newest state holds the pin and group, the older state is ready (to be unpinned, then archived)");

        // A conversation no other account lists keeps what its older state had.
        var ownLayout = Groups(["h=H"], ("local_c1", "h"));
        var alone = Compute([R("local_c1", "여기만", true, "c1", [], 10), R("local_c2", "여기만", false, "c2", ["c1"], 20)], ownLayout,
            [Other(ReferenceKey, null, [R("local_z", "다른 것", true, "z")], none)], copies: Copies(("local_c2", "여기만")), lineage: lineage);
        Check(alone.Moves!.Single() is { Group: "H", GroupId: "h" } stays && stays.Sessions.SequenceEqual(["local_c2"])
              && alone.Pins!.Single() is { Session: "local_c2", Pinned: true, By: null } && alone.Archive!.Single() is { Ready: false }
              && alone.OnlyHere!.SequenceEqual(["여기만"]) && alone.Others!.Single().Matched == 0,
            "an older state's pin and group go to its newest state even when no other account lists the conversation");

        // With nothing to take from, an account only clears its older states, handing their pin and group on first.
        var mine = Groups(["g=G"], ("local_old", "g"));
        var self = Compute(current, mine, [], copies: copiedHere, lineage: lineage, label: "서브");
        Check(self is { Status: "ok", Others.Count: 0 } && self.Archive!.Single() is { Session: "local_old", Ready: false }
              && self.CreateGroups!.Count == 0 && self.Moves!.Single() is { Group: "G", GroupId: "g" } handed
              && handed.Sessions.SequenceEqual(["local_new"]) && self.Pins!.Single() is { Session: "local_new", Pinned: true }
              && self.Sidebars!.Count == 1,
            "one record per conversation is kept, the newest holding the pin and group");

        // Unreadable pins plan no pins and no groups (a move could unpin); unreadable groups hold archiving too.
        var blind = Compute(current, none, [Other(ReferenceKey, null, reference, referenceLayout, At(0), copiedThere)], copies: copiedHere,
            pinsKnown: false, lineage: lineage, warnings: ["pins_unreadable"]);
        Check(blind.Pins!.Count == 0 && blind.Moves!.Count == 0 && blind.CreateGroups!.Count == 0
              && blind.Archive!.Single() is { Pinned: false, Ready: false } && blind.Sidebars!.All(sidebar => sidebar.Pinned is null),
            "unknown pins leave pins, groups and older states alone");
        var ungrouped = Compute([current[0], current[1] with { Pinned = true }], null,
            [Other(ReferenceKey, null, reference, null, At(0), copiedThere)], copies: copiedHere, lineage: lineage,
            warnings: ["groups_unreadable"]);
        Check(ungrouped.Pins!.Count == 0 && ungrouped.Archive!.Single() is { Ready: false },
            "unknown groups hold the older state: its group could not be handed on");
        // A group that cannot be resolved on either state leaves the hand-over unproven.
        var mineDangling = Groups(["h=H"], ("local_old", "h"), ("local_new", "gone"));
        var dangling = Compute([current[0] with { Pinned = false }, current[1]], mineDangling, [], copies: copiedHere, lineage: lineage);
        Check(dangling.Moves!.Count == 0 && dangling.Archive!.Single() is { Ready: false } && dangling.Warnings!.SequenceEqual(["groups_unresolved"]),
            "a newest state filed under a group the scope does not list is not taken as holding the older state's group");
        var oldDangling = Groups(["h=H"], ("local_old", "gone"));
        var unknownOld = Compute([current[0] with { Pinned = false }, current[1]], oldDangling, [], copies: copiedHere, lineage: lineage);
        Check(unknownOld.Archive!.Single() is { Ready: false }, "an older state in a group the scope does not list is kept");

        // Archived older states: what was changed on one still counts for the newest state of its conversation, found again
        // from the transcripts; a branch archived counts for nothing else, though its transcript is listed as earlier.
        var archivedOld = R("local_r_old", "옛 이름", false, "a1", [], 10);
        var there = new[] { R("local_r_new", "대화", false, "a2", ["a1"], 20) };
        ClaudeSessionLayout Renamed()
        {
            var kept = new ClaudeSessionLayout();
            Steady(kept, ReferenceKey, "메인", there, none, At(30));
            Changed(kept, ReferenceKey, "local_r_old", Change(At(20), title: "보관 전에 바꾼 이름"));
            return kept;
        }
        var folded = Compute([R("local_c", "대화", false, "a2", ["a1"], 20)], none,
            [Other(ReferenceKey, "메인", there, none, At(30), archived: [archivedOld])], Renamed(), lineage: lineage);
        Check(folded.Renames!.Single() is { Session: "local_c", Title: "보관 전에 바꾼 이름", By: "메인" },
            "an archived older state's change there counts for its newest state");
        var branchSpans = new Dictionary<string, ClaudeSessionSync.TranscriptSpan>(spans) { ["a1"] = S(0, 5) };
        var branchLineage = lineage with { Transcript = cli => branchSpans.TryGetValue(cli, out var span) ? span : null };
        var branched = Compute([R("local_c", "대화", false, "a2", ["a1"], 20)], none,
            [Other(ReferenceKey, "메인", there, none, At(30), archived: [archivedOld])], Renamed(), lineage: branchLineage);
        Check(branched.Renames!.Count == 0, "an archived branch's change does not count for the conversation it branched from");
        var ownKept = new ClaudeSessionLayout();
        Steady(ownKept, CurrentKey, "서브", [current[1]], none, At(30), copiedHere);
        Changed(ownKept, CurrentKey, "local_old", Change(At(20), pinned: true));
        var ownFolded = Compute([current[1]], none, [], ownKept, copiedHere, lineage: lineage, archived: [current[0] with { Pinned = false }]);
        Check(ownFolded.Pins!.Single() is { Session: "local_new", Pinned: true, By: null } && ownFolded.Archive!.Count == 0,
            "this account's own archived older state hands its change on too, and is not listed for archiving again");

        // Taking a pin or a group off an older state after the conversation moved on only puts that line away.
        var putAway = new ClaudeSessionLayout();
        var heldHere = new[] { current[0] with { Pinned = false }, current[1] with { Pinned = true } };
        var heldLayout = Groups(["g=G"], ("local_new", "g"));
        Steady(putAway, CurrentKey, "서브", heldHere, heldLayout, At(30), copiedHere);
        var movedOnAt = origin.AddHours(2);
        Changed(putAway, CurrentKey, "local_old", Change(movedOnAt.AddHours(1), pinned: false, ungrouped: true));
        Changed(putAway, CurrentKey, "local_new", Change(movedOnAt.AddHours(-1), pinned: true, group: "G"));
        var clearedHere = Compute(heldHere, heldLayout, [], putAway, copiedHere, lineage: lineage, label: "서브");
        Check(clearedHere.Pins!.Count == 0 && clearedHere.Moves!.Count == 0 && clearedHere.Archive!.Single() is { Session: "local_old", Ready: true },
            "a pin and a group taken off an older state after the conversation moved on leave its newest state as it is");
        var theirs = new[] { reference[0] with { Pinned = false }, reference[1] };
        var putAwayThere = new ClaudeSessionLayout();
        Steady(putAwayThere, CurrentKey, "서브", heldHere[1..], heldLayout, At(40), copiedHere);
        Steady(putAwayThere, ReferenceKey, "메인", theirs, none, At(30), copiedThere);
        Changed(putAwayThere, CurrentKey, "local_new", Change(movedOnAt.AddHours(-1), pinned: true, group: "G"));
        Changed(putAwayThere, ReferenceKey, "local_r_old", Change(movedOnAt.AddHours(1), pinned: false, ungrouped: true));
        var clearedThere = Compute(heldHere[1..], heldLayout, [Other(ReferenceKey, "메인", theirs, none, At(30), copiedThere)], putAwayThere,
            copiedHere, lineage: lineage, label: "서브");
        Check(clearedThere.Pins!.Count == 0 && clearedThere.Moves!.Count == 0,
            "the same on another account's older state");
        var before = new ClaudeSessionLayout();
        Steady(before, CurrentKey, "서브", heldHere, heldLayout, At(30), copiedHere);
        Changed(before, CurrentKey, "local_old", Change(movedOnAt.AddHours(-1), pinned: false, ungrouped: true));
        Changed(before, CurrentKey, "local_new", Change(movedOnAt.AddHours(-2), pinned: true, group: "G"));
        var clearedBefore = Compute(heldHere, heldLayout, [], before, copiedHere, lineage: lineage, label: "서브");
        Check(clearedBefore.Pins!.Single() is { Session: "local_new", Pinned: false } && clearedBefore.Moves!.Single() is { Group: null },
            "taken off while it was still the conversation, a pin and a group count for it");
        var pinnedLate = new ClaudeSessionLayout();
        var twoGroups = Groups(["g=G", "h=H"], ("local_new", "g"));
        Steady(pinnedLate, CurrentKey, "서브", heldHere, twoGroups, At(30), copiedHere);
        Changed(pinnedLate, CurrentKey, "local_old", Change(movedOnAt.AddHours(1), group: "H", title: "늦게 바꾼 이름"));
        Changed(pinnedLate, CurrentKey, "local_new", Change(movedOnAt.AddHours(-1), group: "G"));
        var setLate = Compute(heldHere, twoGroups, [], pinnedLate, copiedHere, lineage: lineage, label: "서브");
        Check(setLate.Moves!.Single() is { Group: "H" } && setLate.Renames!.Single() is { Title: "늦게 바꾼 이름" },
            "what is set on an older state, not taken off, still counts for the conversation");

        // The sidebars read as the app shows them: pinned sessions under Pinned only, everything by title.
        var view = Compute([R("local_1", "나중", true, "1"), R("local_2", "가운데", false, "2"), R("local_3", "처음", true, "3")],
            Groups(["g2=둘", "g1=하나"], ("local_1", "g1"), ("local_2", "g1")), [Other(ReferenceKey, "메인", [], null)], label: "서브");
        Check(view.Sidebars![0] is { Label: "서브", Pinned: { } pinnedTitles, Groups: { } groupViews }
              && pinnedTitles.SequenceEqual(["나중", "처음"]) && groupViews.Select(group => group.Name).SequenceEqual(["둘", "하나"])
              && groupViews[1].Sessions.SequenceEqual(["가운데"]) && view.Sidebars[1].Groups is null,
            "sidebars list pins and groups by title, a pinned session only under Pinned");
    }

    /// <summary>What a record gives without a checkpoint to compare with: a record of the account's own everything it
    /// shows; the widget's copy only what was chosen there.</summary>
    private static void TestCopies()
    {
        var groups = Groups(["g=G", "h=H"]);
        SyncLayout In(string session, string group) => groups with { Assignments = new Dictionary<string, string> { [session] = group } };
        var mine = new[] { R("local_mine", "내 이름", true, "a") };
        // Nothing kept: the other account's records give what they give as of a later time than this one's.
        SyncPlan Against(IReadOnlyList<Record> theirs, SyncLayout layout, IReadOnlyDictionary<string, string?>? copies, SyncLayout? here = null) =>
            Compute(mine, here ?? groups, [Other(ReferenceKey, null, theirs, layout, At(0), copies)]);
        var untracked = Copies(("local_copy", null));
        var untouched = Against([R("local_copy", "옛 이름", false, "a"), R("local_other", "다른 것", true, "z")], groups, untracked);
        Check(untouched is { Pins.Count: 0, Renames.Count: 0, Moves.Count: 0, GroupOrder: null },
            "a copy the widget made there, untouched there, gives nothing");
        var renamed = Against([R("local_copy", "거기서 바꾼 이름", false, "a", renamed: true), R("local_other", "다른 것", true, "z")], groups,
            untracked);
        Check(renamed.Renames!.Single() is { Title: "거기서 바꾼 이름" } && renamed.Pins!.Count == 0,
            "a copy renamed there gives its title (where the ledger does not track it, by the app's rename history), still no pin");
        var recased = Against([R("local_copy", "옛 이름 V2", false, "a"), R("local_other", "다른 것", true, "z")], groups,
            Copies(("local_copy", "옛 이름 v2")));
        Check(recased.Renames!.Single().Title == "옛 이름 V2", "where the ledger tracks the title the widget wrote, any difference is a rename there");
        var asWritten = Against([R("local_copy", "옛 이름", false, "a", renamed: true), R("local_other", "다른 것", true, "z")], groups,
            Copies(("local_copy", "옛 이름")));
        Check(asWritten.Renames!.Count == 0, "a copy still showing the title the widget wrote gives none, whatever its history");
        var filed = Against([R("local_copy", "내 이름", false, "a"), R("local_other", "다른 것", true, "z")], In("local_copy", "g"), untracked);
        Check(filed.Moves!.Single() is { Group: "G", GroupId: "g" } && filed.Pins!.Count == 0,
            "a copy filed there gives its group, and the pin it has since: filing it there unpinned it");
        var filedPinned = Against([R("local_copy", "내 이름", true, "a")], In("local_copy", "g"), untracked);
        Check(filedPinned.Moves!.Single() is { Group: "G" } && filedPinned.Pins!.Single() is { Session: "local_mine", Pinned: true },
            "a copy filed there and pinned again is moved, then pinned again here");
        var pinnedCopy = Against([R("local_copy", "내 이름", true, "a")], groups, untracked, In("local_mine", "h"));
        Check(pinnedCopy.Moves!.Count == 0 && pinnedCopy.Pins!.Count == 0, "a pinned copy without a group keeps this account's group");
        var original = Against([R("local_copy", "옛 이름", false, "a"), R("local_other", "다른 것", true, "z")], groups, null);
        Check(original.Pins!.Single() is { Pinned: false } && original.Renames!.Single().Title == "옛 이름",
            "a record the app wrote there offers its pin and title as they are");

        // This account's records give the same way: the widget's untouched copy here holds no choice of its own, so even an
        // older change elsewhere fills it, while a record of this account's own keeps the value it gave later.
        var kept = new ClaudeSessionLayout();
        kept.Accounts[CurrentKey] = new("서브", At(50));
        var theirs = new[] { R("local_m", "제목", true, "n") };
        var copyHere = Compute([R("local_n", "제목", false, "n")], groups, [Other(ReferenceKey, "메인", theirs, groups, At(10))], kept,
            copies: Copies(("local_n", "제목")));
        Check(copyHere.Pins!.Single() is { Session: "local_n", Pinned: true, By: "메인" },
            "the widget's untouched copy here takes what another account gives, however long ago");
        var ownHere = Compute([R("local_n", "제목", false, "n")], groups, [Other(ReferenceKey, "메인", theirs, groups, At(10))], kept);
        Check(ownHere.Pins!.Count == 0, "this account's own record keeps the value it gave later");
        var filedHere = Compute([R("local_n", "제목", false, "n")], In("local_n", "h"), [Other(ReferenceKey, "메인", theirs, groups, At(10))],
            kept, copies: Copies(("local_n", "제목")));
        Check(filedHere is { Moves.Count: 0, Pins.Count: 0 }, "a copy filed here gives its group and, filing having unpinned it, its pin");
    }

    /// <summary>Value by value the newest change wins: what each account changed is recorded with its time, and a value
    /// nobody changed stays.</summary>
    private static void TestNewestChange()
    {
        var groups = Groups(["g=G"]);
        var here = new[] { R("local_c", "처음", false, "a"), R("local_c2", "다른 것", true, "b") };
        var main = new[] { R("local_m", "메인에서", true, "a") };
        var mainGroups = Groups(["gm=G"]);
        var third = new[] { R("local_t", "셋째에서", false, "a") };
        var thirdGroups = Groups(["gt=G"], ("local_t", "gt"));
        // Every account was checkpointed showing what it shows now, in a stay before; what it changed before that is
        // recorded with when it changed it.
        ClaudeSessionLayout Keeping(params (string Account, string Session, Changes Change)[] changes)
        {
            var kept = new ClaudeSessionLayout();
            Steady(kept, CurrentKey, "서브", here, groups, At(0));
            Steady(kept, ReferenceKey, "메인", main, mainGroups, At(40), Copies(("local_m", "처음")));
            Steady(kept, ThirdKey, "셋째", third, thirdGroups, At(35), Copies(("local_t", "처음")));
            foreach (var (account, session, change) in changes) Changed(kept, account, session, change);
            return kept;
        }
        OtherAccount[] Others(DateTimeOffset? mainLeft = null, DateTimeOffset? thirdLeft = null) =>
        [
            Other(ReferenceKey, "메인", main, mainGroups, mainLeft ?? At(40), Copies(("local_m", "처음"))),
            Other(ThirdKey, "셋째", third, thirdGroups, thirdLeft ?? At(35), Copies(("local_t", "처음")))
        ];
        var mainChanges = Change(At(30), pinned: true).Over(Change(At(10), title: "메인에서"));
        var thirdChanges = Change(At(25), group: "G").Over(Change(At(20), title: "셋째에서"));
        var changed = Keeping((ReferenceKey, "local_m", mainChanges), (ThirdKey, "local_t", thirdChanges));
        var plan = Compute(here, groups, Others(), changed);
        Check(plan.Renames!.Single() is { Session: "local_c", Title: "셋째에서", By: "셋째" }
              && plan.Moves!.Single() is { Group: "G", GroupId: "g" } move && move.Sessions.SequenceEqual(["local_c"])
              && plan.Pins!.Single() is { Session: "local_c", Pinned: true, By: "메인", Title: "셋째에서" } && plan.GroupOrder is null,
            "value by value the newest change wins, wherever it was made: the title and group from one account, the pin from another");
        Check(plan.OnlyHere!.SequenceEqual(["다른 것"]) && plan.Others!.All(other => other.Matched == 1) && plan.Warnings is null,
            "a conversation only this account lists keeps what it shows");
        Check(plan.Pending is { Stay: 1 } pending && pending.Sessions["local_c"] is { Filed: true } target
              && target.Pinned == new ClaudeSessionLayout.Stamp<bool>(true, At(30), ReferenceKey)
              && target.Group == new ClaudeSessionLayout.Stamp<string?>("G", At(25), ThirdKey)
              && target.Title == new ClaudeSessionLayout.Stamp<string>("셋째에서", At(20), ThirdKey),
            "the plan keeps each value it sets out to give with the change it comes from");
        Check(Compute(here, groups, Others(), Keeping()) is { Renames.Count: 0, Moves.Count: 0, Pins.Count: 0 },
            "a value shown otherwise elsewhere that nobody changed since the checkpoints stays as it is here");
        // A change a plan brought into an account counts as the account it came from.
        var relayed = Keeping((ThirdKey, "local_t", Change(At(25), pinned: false, by: ReferenceKey)));
        Check(Compute([here[0] with { Pinned = true }, here[1]], groups, Others(), relayed).Pins!.Single() is { Pinned: false, By: "메인" },
            "a change one account took from another in a plan is offered as that other account's");

        // Changes made at the same time go to this account, then to the account the app left last.
        var alike = Keeping((ReferenceKey, "local_m", Change(At(10), title: "메인에서")), (ThirdKey, "local_t", Change(At(10), title: "셋째에서")));
        Check(Compute(here, groups, Others(), alike).Renames!.Single() is { Title: "메인에서", By: "메인" }
              && Compute(here, groups, Others(thirdLeft: At(45)), alike).Renames!.Single() is { Title: "셋째에서", By: "셋째" },
            "changes made at the same time go to the account the app left last");
        var ownAlike = Keeping((ReferenceKey, "local_m", Change(At(10), title: "메인에서")), (CurrentKey, "local_c", Change(At(10), title: "처음")));
        Check(Compute(here, groups, Others(), ownAlike).Renames!.Count == 0, "this account's own change made at the same time stays");

        // Without the widget's copies known, a copy could pass for a choice: nothing is planned.
        var noLedger = Compute(here, groups, Others(), changed, ledgerKnown: false);
        var noLedgerThere = Compute(here, groups, [Others()[0], Others()[1] with { CopyTitles = null }], changed);
        Check(noLedger is { Renames.Count: 0, Pins.Count: 0, Moves.Count: 0, CreateGroups.Count: 0 }
              && noLedgerThere is { Renames.Count: 0, Pins.Count: 0, Moves.Count: 0, CreateGroups.Count: 0 },
            "a ledger that cannot be read, here or there, plans no pin, title or group");

        // The group list follows its newest change too.
        var lists = Keeping();
        lists.GroupsChanged[ReferenceKey] = ListChange(["G", "K"], At(10));
        lists.GroupsChanged[ThirdKey] = ListChange(["K", "G"], At(20));
        var listed = Compute(here, groups, Others(), lists);
        Check(listed.GroupOrder!.SequenceEqual(["K", "G"]) && listed.GroupOrderBy == "셋째" && listed.CreateGroups!.SequenceEqual(["K", "G"]),
            "the group list follows the newest change of it");
        lists.GroupsChanged[CurrentKey] = ListChange(["G"], At(20));
        Check(Compute(here, groups, Others(), lists) is { GroupOrder: null, CreateGroups.Count: 0, RenameGroups.Count: 0 },
            "this account's own group list changed at the same time stays");
    }

    /// <summary>Putting the groups in the newest list's order with what the app's tools can do: new groups go at the end
    /// and no tool reorders, so the longest start already in order stays and the rest is made anew; the list is final.</summary>
    private static void TestGroupOrder()
    {
        // 계정3's sidebar, whose group list is the newest, and 계정2 here with the same groups in another order.
        string[] tiers = ["티어 2", "티어 3 (보관)", "스킬 생태계", "태림 오리지널 콘텐츠", "도구"];
        var origin = DateTimeOffset.Parse("2026-10-01T00:00:00Z");
        var spans = new Dictionary<string, ClaudeSessionSync.TranscriptSpan>
        {
            ["c3-old"] = new(origin, origin.AddHours(1), origin.AddHours(1)),
            ["c3"] = new(origin.AddHours(2), origin.AddHours(24), origin.AddHours(24))
        };
        var lineage = new ClaudeSessionSync.Lineage(null, cli => spans.GetValueOrDefault(cli),
            (_, next) => spans.TryGetValue(next, out var span) ? span.Created : null);
        var here = new[]
        {
            R("local_s1", "하나", false, "c1"), R("local_s2", "둘", true, "c2"), R("local_s3", "셋", false, "c3", ["c3-old"], 20),
            R("local_s3_old", "셋", false, "c3-old", [], 10), R("local_s4", "넷", false, "c4"), R("local_s5", "다섯", false, "c5")
        };
        var hereLayout = Groups(["g-t2=티어 2", "g-tool=도구", "g-skill=스킬 생태계", "g-t3=티어 3 (보관)", "g-orig=태림 오리지널 콘텐츠"],
            ("local_s1", "g-t2"), ("local_s2", "g-tool"), ("local_s3", "g-skill"), ("local_s3_old", "g-skill"), ("local_s4", "g-t3"),
            ("local_s5", "g-t2"));
        var third = new[]
        {
            R("local_t1", "하나", false, "c1"), R("local_t2", "둘", true, "c2"), R("local_t3", "셋", false, "c3"), R("local_t4", "넷", false, "c4"),
            R("local_t5", "다섯", false, "c5")
        };
        var thirdLayout = Groups(["h-t2=티어 2", "h-t3=티어 3 (보관)", "h-skill=스킬 생태계", "h-orig=태림 오리지널 콘텐츠", "h-tool=도구"],
            ("local_t1", "h-t2"), ("local_t2", "h-tool"), ("local_t3", "h-skill"), ("local_t4", "h-t3"), ("local_t5", "h-t3"));
        var kept = new ClaudeSessionLayout();
        Steady(kept, CurrentKey, "계정2", here, hereLayout, At(0));
        Steady(kept, ThirdKey, "계정3", third, thirdLayout, At(50));
        kept.GroupsChanged[ThirdKey] = ListChange(tiers, At(40));
        Changed(kept, ThirdKey, "local_t5", Change(At(40), group: "티어 3 (보관)"));
        OtherAccount[] others = [Other(ThirdKey, "계정3", third, thirdLayout, At(50))];
        var plan = Compute(here, hereLayout, others, kept, lineage: lineage, label: "계정2");
        Check(plan.GroupOrder!.SequenceEqual(tiers) && plan.GroupOrderBy == "계정3"
              && plan.CreateGroups!.SequenceEqual(["스킬 생태계", "태림 오리지널 콘텐츠", "도구"]),
            "the start of the newest list already in order here stays (티어 2, 티어 3 (보관)); the rest is made anew at the end, in order");
        Check(plan.RenameGroups!.Select(rename => (rename.Id, rename.From, rename.To)).SequenceEqual(
                  [("g-tool", "도구", "도구 (옮기는 중)"), ("g-skill", "스킬 생태계", "스킬 생태계 (옮기는 중)"),
                   ("g-orig", "태림 오리지널 콘텐츠", "태림 오리지널 콘텐츠 (옮기는 중)")])
              && plan.DeleteGroups!.Select(group => (group.Id, group.Name, group.Remade)).SequenceEqual(
                  [("g-tool", "도구 (옮기는 중)", true), ("g-skill", "스킬 생태계 (옮기는 중)", true), ("g-orig", "태림 오리지널 콘텐츠 (옮기는 중)", true)]),
            "a group out of place steps aside under the moving mark before its name is made anew, and goes once the moves empty it");
        Check(plan.Moves!.Select(move => (move.Group, move.GroupId, string.Join(",", move.Sessions))).SequenceEqual(
                  [("티어 3 (보관)", "g-t3", "local_s5"), ("스킬 생태계", null, "local_s3,local_s3_old"), ("도구", null, "local_s2")]),
            "a session goes to a group that stays by its id and to one made anew by name; an older state follows its group");
        Check(plan.Pins!.Single() is { Session: "local_s2", Pinned: true, By: null }, "a pinned session moved into a group is pinned again after the move");
        Check(plan.Pending is { Groups: { At: var listAt, By: var listBy } } && listAt == At(40) && listBy == ThirdKey
              && plan.Pending.Renamed.Keys.Order(StringComparer.Ordinal).SequenceEqual(["g-orig", "g-skill", "g-tool"])
              && plan.Pending.Sessions["local_s2"].Group is { At: var remadeAt } && remadeAt == DateTimeOffset.MinValue,
            "the plan keeps the list it follows with its change, the groups it renames out of the way, and moves into a group made anew "
            + "as nobody's choice");

        var steady = new ClaudeSessionLayout();
        Steady(steady, CurrentKey, "계정2", here, hereLayout, At(0));
        Steady(steady, ThirdKey, "계정3", third, thirdLayout, At(50));
        Check(Compute(here, hereLayout, others, steady, lineage: lineage) is
                  { RenameGroups.Count: 0, CreateGroups.Count: 0, DeleteGroups.Count: 0, Moves.Count: 0, GroupOrder: null },
            "with no change of the group list anywhere, the groups here stand in their order");

        // Two groups of one name (an older app made them) become one: the second's sessions join the first, and it goes.
        var twins = Groups(["g-a=A", "g-b1=B", "g-b2=B"], ("local_x", "g-b1"), ("local_y", "g-b2"));
        var pair = new[] { R("local_x", "엑스", false, "x"), R("local_y", "와이", false, "y") };
        var merged = Compute(pair, twins, []);
        Check(merged.Moves!.Single() is { Group: "B", GroupId: "g-b1" } join && join.Sessions.SequenceEqual(["local_y"])
              && merged.RenameGroups!.Single() is { Id: "g-b2", From: "B", To: "B (옮기는 중)" }
              && merged.DeleteGroups!.Single() is { Id: "g-b2", Name: "B (옮기는 중)" } && merged.CreateGroups!.Count == 0
              && merged.GroupOrder!.SequenceEqual(["A", "B"]),
            "two groups of one name become one");

        // A plan stopped part way: "C" stepped aside and was made anew at the end, its sessions not moved yet. The list
        // here, mid-move, is nobody's choice; planning again carries the sorting on.
        var halfway = Groups(["g-a=A", "g-c-old=C (옮기는 중)", "g-b=B", "g-c=C"], ("local_x", "g-c-old"), ("local_y", "g-b"));
        var halfKept = new ClaudeSessionLayout();
        Steady(halfKept, CurrentKey, "계정2", pair, Groups(["g-a=A", "g-c-old=C", "g-b=B"], ("local_x", "g-c-old"), ("local_y", "g-b")), At(0));
        halfKept.GroupsChanged[ThirdKey] = ListChange(["A", "B", "C"], At(30));
        var resumed = Compute(pair, halfway, [Other(ThirdKey, "계정3", [], null, At(50))], halfKept);
        Check(resumed.Moves!.Single() is { Group: "C", GroupId: "g-c" } carry && carry.Sessions.SequenceEqual(["local_x"])
              && resumed.RenameGroups!.Count == 0 && resumed.CreateGroups!.Count == 0
              && resumed.DeleteGroups!.Single() is { Id: "g-c-old", Name: "C (옮기는 중)", Remade: true } && resumed.GroupOrder!.SequenceEqual(["A", "B", "C"]),
            "a plan again after one stopped part way empties the group under the moving mark into the new one, which then goes");
        // A rename the plan in progress set out to do but that was not done yet is planned again.
        var notRenamed = Groups(["g-a=A", "g-c=C", "g-b=B"], ("local_x", "g-c"), ("local_y", "g-b"));
        var unapplied = new ClaudeSessionLayout();
        Steady(unapplied, CurrentKey, "계정2", pair, notRenamed, At(0));
        unapplied.Pendings[CurrentKey] = new(1, new Dictionary<string, Target>(), null, Aside(("g-c", "C")));
        unapplied.GroupsChanged[ThirdKey] = ListChange(["A", "B", "C"], At(30));
        Check(Compute(pair, notRenamed, [Other(ThirdKey, "계정3", [], null, At(50))], unapplied) is
                  { RenameGroups: [{ Id: "g-c", To: "C (옮기는 중)" }], CreateGroups: ["C"] } again
              && again.Moves!.Single() is { Group: "C", GroupId: null },
            "a group the plan in progress was to rename out of the way, still under its name, is not taken as aside");

        // The list is final: a group it lacks goes, and its sessions follow the group's renaming, or else leave for no group.
        var chainHere = new[] { R("local_x", "엑스", false, "x") };
        var chainLayout = Groups(["g=G"], ("local_x", "g"), ("local_z", "g"));
        var chainKept = new ClaudeSessionLayout();
        Steady(chainKept, CurrentKey, "계정2", chainHere, chainLayout, At(0));
        chainKept.GroupsChanged[ReferenceKey] = ListChange(["H"], At(10), null, ("G", "H"));
        chainKept.GroupsChanged[ThirdKey] = ListChange(["I"], At(20), null, ("H", "I"));
        var shelved = new[] { R("local_z", "보관된 것", false, "z") };
        var chain = Compute(chainHere, chainLayout, [Other(ReferenceKey, "계정1", [], null, At(10)), Other(ThirdKey, "계정3", [], null, At(20))],
            chainKept, archived: shelved);
        Check(chain.GroupOrder!.SequenceEqual(["I"]) && chain.GroupOrderBy == "계정3" && chain.CreateGroups!.SequenceEqual(["I"])
              && chain.Moves!.Single() is { Group: "I" } toI && toI.Sessions.SequenceEqual(["local_x"])
              && chain.DeleteGroups!.Single() is { Id: "g", Remade: true, Hidden: 1 },
            "a group renamed G → H in one account and H → I in another passes what was filed under G to I; its archived sessions leave the group");
        var dropped = new ClaudeSessionLayout();
        Steady(dropped, CurrentKey, "계정2", chainHere, chainLayout, At(0));
        dropped.GroupsChanged[ThirdKey] = ListChange(["I"], At(20));
        var gone = Compute(chainHere, chainLayout, [Other(ThirdKey, "계정3", [], null, At(20))], dropped, archived: shelved);
        Check(gone.Moves!.Single() is { Group: null } outOf && outOf.Sessions.SequenceEqual(["local_x"])
              && gone.DeleteGroups!.Single() is { Id: "g", Name: "G (옮기는 중)", Remade: false, Hidden: 1 },
            "a group the newest list lacks goes: its sessions leave for no group, and the archived ones filed there are counted");
        // Remaking a group for its order leaves its archived sessions out: the app moves none.
        var reorder = Groups(["g=G", "h=H"], ("local_x", "g"), ("local_z", "g"));
        var reordered = new ClaudeSessionLayout();
        Steady(reordered, CurrentKey, "계정2", chainHere, reorder, At(0));
        reordered.GroupsChanged[ThirdKey] = ListChange(["H", "G"], At(20));
        var remade = Compute(chainHere, reorder, [Other(ThirdKey, "계정3", [], null, At(20))], reordered, archived: shelved);
        Check(remade.CreateGroups!.SequenceEqual(["G"]) && remade.Moves!.Single() is { Group: "G", GroupId: null } toG
              && toG.Sessions.SequenceEqual(["local_x"])
              && remade.DeleteGroups!.Single() is { Id: "g", Remade: true, Hidden: 1 },
            "a group made anew for its order takes every session a list shows; the archived ones filed under it are counted");
        // A group this stay filed a session under stays on the list, though the newest list lacks it.
        var filedLayout = Groups(["g=G", "z=Z"], ("local_x", "z"));
        var filedNow = new ClaudeSessionLayout();
        filedNow.Accounts[CurrentKey] = new("계정2", At(0), At(30), 2);
        filedNow.Checkpoints[CurrentKey] = PointOf([R("local_x", "엑스", false, "x")], Groups(["g=G", "z=Z"]), At(31), 2);
        filedNow.GroupsChanged[ThirdKey] = ListChange(["G"], At(20));
        var keptZ = Compute(chainHere, filedLayout, [Other(ThirdKey, "계정3", [], null, At(20))], filedNow);
        Check(keptZ is { Moves.Count: 0, DeleteGroups.Count: 0 } && keptZ.GroupOrder is null,
            "a group the user filed a session under in this stay stays on the list");
        filedNow.Checkpoints[CurrentKey] = PointOf([R("local_x", "엑스", false, "x")], filedLayout, At(31), 2);
        Changed(filedNow, CurrentKey, "local_x", Change(At(32), group: "Z"));
        Check(Compute(chainHere, filedLayout, [Other(ThirdKey, "계정3", [], null, At(20))], filedNow) is { Moves.Count: 0, DeleteGroups.Count: 0 },
            "and still after a checkpoint recorded that filing in this stay");
        filedNow.Accounts[CurrentKey] = new("계정2", At(0), At(40), 3);
        var later = Compute(chainHere, filedLayout, [Other(ThirdKey, "계정3", [], null, At(35))], filedNow);
        Check(later.Moves!.Single() is { Group: null } && later.DeleteGroups!.Single() is { Id: "z", Remade: false },
            "but a filing of an earlier stay yields to a newer list that lacks its group");
    }

    /// <summary>The groups' order and folding come from the sidebar's sections (<c>codeSidebarByScope</c>); the stored group
    /// list is in no particular order.</summary>
    private static void TestSidebarSections()
    {
        static JsonObject Parse(string json) => (JsonObject)JsonNode.Parse(json)!;
        var key = CurrentKey;
        const string Three = "[{\"id\":\"g1\",\"name\":\"하나\"},{\"id\":\"g2\",\"name\":\"둘\"},{\"id\":\"g3\",\"name\":\"셋\"}]";
        const string Filed = "{\"code:local_a\":\"g1\",\"code:local_b\":\"g3\",\"cowork:local_x\":\"g2\"}";
        const string Scope = "{\"groups\":" + Three + ",\"assignments\":" + Filed + ",\"order\":{}}";
        const string Sections = "{\"sections\":[{\"id\":\"pinned\",\"kind\":\"pinned\",\"order\":0},"
            + "{\"id\":\"g3\",\"kind\":\"manual\",\"name\":\"셋\",\"order\":1,\"collapsed\":true,\"shown\":true},"
            + "{\"id\":\"g1\",\"kind\":\"manual\",\"name\":\"하나\",\"order\":2.5,\"collapsed\":false},"
            + "{\"id\":\"sessions\",\"kind\":\"sessions\",\"order\":3}]}";
        JsonObject State(string scope = Scope, string? sidebar = Sections, string? sidebars = null) =>
            Parse("{\"customGroupsByScope\":{\"" + key + "\":" + scope + "}"
                  + (sidebars ?? (sidebar is null ? "" : ",\"codeSidebarByScope\":{\"" + key + "\":" + sidebar + "}")) + "}");
        var layout = ClaudeSessionSync.LayoutOf(State(), key, missingIsEmpty: false);
        Check(layout is { Groups: [{ Name: "셋", Collapsed: true }, { Name: "하나", Collapsed: false }, { Name: "둘", Collapsed: false }] }
              && layout.Assignments.Count == 2 && layout.Assignments["local_a"] == "g1" && layout.Assignments["local_b"] == "g3",
            "the sections give the groups' order and folding; a group they do not place follows the rest; only Code sessions count");
        Check(ClaudeSessionSync.LayoutOf(State(), key.ToUpperInvariant(), missingIsEmpty: false)?.Groups.Select(group => group.Name)
                  .SequenceEqual(["셋", "하나", "둘"]) == true, "a scope is found whatever the case of its key");
        Check(ClaudeSessionSync.LayoutOf(State(sidebar: null), key, false)?.Groups.Select(group => (group.Name, group.Collapsed))
                  .SequenceEqual([("하나", false), ("둘", false), ("셋", false)]) == true
              && ClaudeSessionSync.LayoutOf(State(sidebars: ",\"codeSidebarByScope\":{}"), key, false)?.Groups[0].Name == "하나",
            "a scope without sections keeps its groups in their stored order");
        foreach (var (state, what) in new (JsonObject, string)[]
                 {
                     (State(sidebars: ",\"codeSidebarByScope\":\"x\""), "sections of another kind"),
                     (State(sidebar: "\"x\""), "a scope's sidebar that is no object"),
                     (State(sidebar: "{\"sections\":{}}"), "sections that are no list"),
                     (State(sidebar: "{\"sections\":[{\"order\":1}]}"), "a section without an id"),
                     (State(sidebar: "{\"sections\":[{\"id\":\"g1\",\"order\":\"1\"}]}"), "a section placed by text"),
                     (State(scope: "{\"groups\":[{\"id\":\"g1\"}],\"assignments\":{}}"), "a group without a name"),
                     (State(scope: "{\"groups\":{},\"assignments\":{}}"), "groups that are no list"),
                     (State(scope: "{\"groups\":[],\"assignments\":null}"), "assignments that are null"),
                     (State(scope: "{\"groups\":[],\"assignments\":{\"code:local_a\":1}}"), "an assignment that is no id"),
                     (State(scope: "[]"), "a scope that is no object")
                 })
            Check(ClaudeSessionSync.LayoutOf(state, key, missingIsEmpty: true) is null, "an unfamiliar shape is not guessed at: " + what);
        Check(ClaudeSessionSync.LayoutOf(State(), ThirdKey, missingIsEmpty: true) is { Groups.Count: 0, Assignments.Count: 0 }
              && ClaudeSessionSync.LayoutOf(State(), ThirdKey, missingIsEmpty: false) is null && ClaudeSessionSync.LayoutOf(null, key, true) is null,
            "a missing scope is no groups only where that may be assumed; no sidebar state at all is unknown");
        // A plan's sidebars show what the app shows: pinned sessions under Pinned only, groups in order with their folding.
        var plan = Compute([R("local_a", "가", false, "a"), R("local_b", "나", true, "b"), R("local_c", "다", false, "c")], layout,
            [Other(ReferenceKey, "메인", [], null)], label: "서브");
        Check(plan.Sidebars![0] is
              {
                  Label: "서브", Pinned: ["나"],
                  Groups: [{ Name: "셋", Collapsed: true, Sessions.Count: 0 }, { Name: "하나", Collapsed: false, Sessions: ["가"] }, { Name: "둘" }]
              },
            "a plan's sidebars list the groups in sidebar order with their folding");
        // Read from the app's store the same way: the fixture stores the groups out of order, as the app may.
        using var world = new SessionWorld("sections");
        world.Record(CurrentKey, "local_a", "가", Cli(1));
        world.Record(ReferenceKey, "local_b", "가", Cli(1));
        foreach (var (name, collapsed) in new[] { ("첫째", false), ("둘째", true), ("셋째", false) }) world.Group(CurrentKey, name, collapsed);
        world.Put(CurrentKey, "local_a", world.GroupId(CurrentKey, "둘째"));
        world.Flush();
        Keep(world.Ledger, (CurrentKey, "서브", null), (ReferenceKey, "메인", null));
        Check(ClaudeSessionSync.Build(world.Sources, "local_a") is
              {
                  Status: "ok",
                  Sidebars: [{ Groups: [{ Name: "첫째", Collapsed: false }, { Name: "둘째", Collapsed: true, Sessions: ["가"] }, { Name: "셋째" }] },
                      { Groups: null }]
              },
            "a plan reads the groups' order and folding from the app's sidebar sections");
    }

    private static void TestLayoutStore()
    {
        Check(ClaudeSessionSync.PlannedEventName(CurrentKey.ToUpperInvariant())
              == @"Local\CodexWeeklyUsageIndicator.SessionsPlanned." + CurrentAccount + "_" + CurrentOrg
              && ClaudeSessionSync.CheckpointedEventName(CurrentKey).EndsWith("SessionsCheckpointed." + CurrentAccount + "_" + CurrentOrg, StringComparison.Ordinal)
              && ClaudeSessionSync.MailboxName(CurrentKey).EndsWith("SessionsPlanMailbox." + CurrentAccount + "_" + CurrentOrg, StringComparison.Ordinal)
              && ClaudeSessionSync.PlanLockName(CurrentKey).EndsWith("SessionsPlanLock." + CurrentAccount + "_" + CurrentOrg, StringComparison.Ordinal),
            "the plan request's kernel objects name the account it is for");
        var root = TempRoot("layout");
        try
        {
            var path = Path.Combine(root, ClaudeSessionLayout.FileName);
            Check(ClaudeSessionLayout.Load(root) is (null, false), "no layout yet");
            var left = DateTimeOffset.Parse("2026-10-13T01:02:03.4567891+09:00", CultureInfo.InvariantCulture);
            var layout = new ClaudeSessionLayout { Active = CurrentKey };
            layout.Accounts[CurrentKey] = new("서브", left, At(30), 3);
            layout.Accounts[ReferenceKey] = new("메인");
            layout.Checkpoints[CurrentKey] = new(At(31), 3, [new("g-1", "티어 2"), new("g-2", "도구")], new Dictionary<string, Shown>
            {
                ["local_a"] = new(true, true, "도구", "제목"), ["local_b"] = new(false, true, null, null, "쓴 제목"),
                ["local_c"] = new(null, false, null, "이름만")
            }, new Dictionary<string, Shown> { ["local_b"] = new(true, true, null), ["local_c"] = new(false, true, "티어 2") });
            layout.Checkpoints[ReferenceKey] = new(At(5), 0, null, new Dictionary<string, Shown>());
            layout.Changed[CurrentKey] = new(StringComparer.Ordinal)
            {
                ["local_a"] = new(new(true, At(1)), new("도구", At(2), ReferenceKey), new("제목", At(3))), ["local_b"] = new(Group: new(null, At(4)))
            };
            layout.Changed[ReferenceKey] = new(StringComparer.Ordinal);
            layout.GroupsChanged[CurrentKey] = ListChange(["티어 2", "도구"], At(6), ReferenceKey, ("티어 1", "티어 2"));
            layout.Pendings[CurrentKey] = new(3, new Dictionary<string, Target>
            {
                ["local_a"] = new(new(false, DateTimeOffset.MinValue), new("티어 2", At(7), ThirdKey), Filed: true),
                ["local_b"] = new(Title: new("새 이름", At(8)))
            }, ListChange(["도구", "티어 2"], At(9), ThirdKey), Aside(("g-2", "도구")));
            layout.Evidences[ReferenceKey] = new(StringComparer.Ordinal) { ["local_r"] = new(true, false), ["local_s"] = new(false, true) };
            layout.Renamings[CurrentKey] = [new("티어 1", "티어 2", At(6)), new("임시", "도구", At(7))];
            layout.Save(root);
            var text = File.ReadAllText(path);
            var (loaded, exists) = ClaudeSessionLayout.Load(root);
            Check(exists && loaded is { Upgraded: false, Active: var active } && active == CurrentKey
                  && loaded.Accounts[CurrentKey] == new ClaudeSessionLayout.Account("서브", left, At(30), 3)
                  && loaded.Accounts[ReferenceKey] == new ClaudeSessionLayout.Account("메인"),
                "accounts round-trip with their labels, stays and when the app last left and came to them, with the account it shows");
            var point = loaded!.Checkpoints[CurrentKey];
            Check(point.At == At(31) && point.Stay == 3 && point.Groups!.SequenceEqual([new("g-1", "티어 2"), new("g-2", "도구")])
                  && point.Sessions.Count == 3 && point.Sessions["local_a"] == new Shown(true, true, "도구", "제목")
                  && point.Sessions["local_b"] == new Shown(false, true, null, null, "쓴 제목") && point.Sessions["local_c"] == new Shown(null, false, null, "이름만")
                  && loaded.Checkpoints[ReferenceKey] is { Groups: null, Sessions.Count: 0 } empty && empty.At == At(5),
                "a checkpoint keeps its time, stay, groups by id and name in order (or none known), and each session's pin, group, title and written title");
            Check(point.Nested is { Count: 2 } nested && nested["local_b"] == new Shown(true, true, null) && nested["local_c"] == new Shown(false, true, "티어 2")
                  && loaded.Checkpoints[ReferenceKey].Nested is null,
                "it keeps what each session nested then would show once out of its family; one an earlier widget took knows none");
            Check(loaded.Changed[CurrentKey]["local_a"] == new Changes(new(true, At(1)), new("도구", At(2), ReferenceKey), new("제목", At(3)))
                  && loaded.Changed[CurrentKey]["local_b"] == new Changes(Group: new(null, At(4))) && !loaded.Changed.ContainsKey(ReferenceKey)
                  && loaded.GroupsChanged[CurrentKey] is { Order: ["티어 2", "도구"], By: var listBy } groups && groups.At == At(6)
                  && listBy == ReferenceKey && groups.Renamed["티어 1"] == "티어 2",
                "each value's newest change keeps its value, time and the account it came from; the group list keeps its renamings");
            Check(loaded.Pendings[CurrentKey] is { Stay: 3 } pending && pending.Sessions["local_a"] == layout.Pendings[CurrentKey].Sessions["local_a"]
                  && pending.Sessions["local_b"] == new Target(Title: new("새 이름", At(8))) && pending.Groups is { Order: ["도구", "티어 2"] }
                  && pending.Renamed["g-2"] == new ClaudeSessionLayout.Moved("도구", "도구 (옮기는 중)")
                  && loaded.Evidences[ReferenceKey]["local_r"] == new ClaudeSessionLayout.Evidence(true, false)
                  && loaded.Evidences[ReferenceKey]["local_s"] == new ClaudeSessionLayout.Evidence(false, true)
                  && loaded.Renamings[CurrentKey].SequenceEqual([new("티어 1", "티어 2", At(6)), new("임시", "도구", At(7))])
                  && !loaded.Renamings.ContainsKey(ReferenceKey),
                "the plan in progress, the groups renamed by hand and the first format's evidence round-trip");
            loaded.Save(root);
            Check(File.ReadAllText(path) == text, "a layout read and saved again is the same");

            // The first format kept per-pair agreements: its accounts carry over, and what an agreement showed a pin or group for is evidence.
            File.WriteAllText(path, "{\"version\":1,\"accounts\":{\"" + CurrentKey + "\":{\"label\":\"서브\",\"leftAt\":null},\""
                + ReferenceKey.ToUpperInvariant() + "\":{\"label\":\"메인\",\"leftAt\":\"2026-10-13T01:02:03.4567891+09:00\"}},\"pairs\":{\""
                + CurrentKey + "|" + ReferenceKey + "\":{\"" + CurrentKey + "\":{\"local_c\":{\"pinned\":false,\"group\":null,\"title\":\"제목\"},"
                + "\"local_t\":{\"title\":\"제목만\"}},\"" + ReferenceKey + "\":{\"local_r\":{\"group\":\"대기\"}}},\""
                + CurrentKey + "|" + ThirdKey + "\":{\"" + ThirdKey + "\":{\"local_x\":{\"pinned\":true}}}}}");
            var (upgraded, _) = ClaudeSessionLayout.Load(root);
            Check(upgraded is { Upgraded: true, Active: null } && upgraded.Accounts.Count == 2
                  && upgraded.Accounts[CurrentKey] == new ClaudeSessionLayout.Account("서브")
                  && upgraded.Accounts[ReferenceKey] == new ClaudeSessionLayout.Account("메인", left)
                  && upgraded.Checkpoints.Count == 0 && upgraded.Changed.Count == 0 && upgraded.GroupsChanged.Count == 0
                  && upgraded.Evidences[CurrentKey]["local_c"] == new ClaudeSessionLayout.Evidence(true, true)
                  && upgraded.Evidences[CurrentKey]["local_t"] == new ClaudeSessionLayout.Evidence(false, false)
                  && upgraded.Evidences[ReferenceKey]["local_r"] == new ClaudeSessionLayout.Evidence(false, true)
                  && !upgraded.Evidences.ContainsKey(ThirdKey),
                "the first format's accounts carry over with when the app left them, and its agreements as evidence of a shown pin or group");
            upgraded!.Save(root);
            Check(ClaudeSessionLayout.Load(root) is ({ Upgraded: false } again, true) && again.Accounts[ReferenceKey].LeftAt == left
                  && File.ReadAllText(path).Contains("\"version\": 2", StringComparison.Ordinal) && !File.ReadAllText(path).Contains("pairs", StringComparison.Ordinal),
                "saved again it is in the current format");
            File.WriteAllText(path, "{\"version\":1,\"accounts\":{},\"pairs\":{\"x\":5}}");
            Check(ClaudeSessionLayout.Load(root) is (null, true), "first-format agreements of an unfamiliar shape are not guessed at");

            var at = "\"" + At(0).ToString("O", CultureInfo.InvariantCulture) + "\"";
            string Account(string fields) => "{\"version\":2,\"accounts\":{\"" + CurrentKey + "\":{\"label\":\"서브\"" + fields + "}}}";
            foreach (var (bad, what) in new[]
                     {
                         ("{broken", "a torn file"),
                         ("{\"accounts\":{}}", "no version"),
                         ("{\"version\":3,\"accounts\":{}}", "a later version"),
                         ("{\"version\":2,\"accounts\":[]}", "accounts that are no map"),
                         ("{\"version\":2,\"accounts\":{\"x\":{\"label\":\"a\"}}}", "an account that is no account/organization key"),
                         ("{\"version\":2,\"accounts\":{\"" + CurrentKey + "\":{}}}", "an account without a label"),
                         (Account(",\"leftAt\":\"어제\""), "a time that is no time"),
                         (Account(",\"arrivedAt\":5"), "a time that is a number"),
                         (Account(",\"stay\":-1"), "a stay below zero"),
                         (Account(",\"stay\":\"2\""), "a stay that is text"),
                         ("{\"version\":2,\"active\":\"x\",\"accounts\":{}}", "an active account that is no key"),
                         ("{\"version\":2,\"active\":5,\"accounts\":{}}", "an active account that is no text"),
                         (Account(",\"checkpoint\":{\"sessions\":{}}"), "a checkpoint without its time"),
                         (Account(",\"checkpoint\":{\"at\":" + at + "}"), "a checkpoint without sessions"),
                         (Account(",\"checkpoint\":{\"at\":" + at + ",\"groups\":[\"티어 2\"],\"sessions\":{}}"), "groups without ids"),
                         (Account(",\"checkpoint\":{\"at\":" + at + ",\"sessions\":{\"local_a\":{\"pinned\":\"yes\"}}}"), "a pin that is text"),
                         (Account(",\"checkpoint\":{\"at\":" + at + ",\"sessions\":{\"local_a\":{\"written\":5}}}"), "a written title that is a number"),
                         (Account(",\"checkpoint\":{\"at\":" + at + ",\"sessions\":{\"local_a\":{\"color\":\"red\"}}}"), "an unknown field"),
                         (Account(",\"checkpoint\":[]"), "a checkpoint that is no object"),
                         (Account(",\"groupsChanged\":{\"order\":[\"A\"],\"renamed\":{}}"), "a group-list change without its time"),
                         (Account(",\"groupsChanged\":{\"order\":[\"A\"],\"at\":" + at + "}"), "a group-list change without its renamings"),
                         (Account(",\"groupsChanged\":{\"order\":[\"A\"],\"renamed\":{\"A\":1},\"at\":" + at + "}"), "a renaming that is no name"),
                         (Account(",\"groupsChanged\":{\"order\":[\"A\"],\"renamed\":{},\"at\":" + at + ",\"by\":\"x\"}"), "a change by no account"),
                         (Account(",\"changed\":[]"), "changes that are no map"),
                         (Account(",\"changed\":{\"local_a\":[]}"), "a session's changes that are no object"),
                         (Account(",\"changed\":{\"local_a\":{\"pinned\":{\"value\":true}}}"), "a change without its time"),
                         (Account(",\"changed\":{\"local_a\":{\"pinned\":{\"value\":\"yes\",\"at\":" + at + "}}}"), "a pin change that is text"),
                         (Account(",\"changed\":{\"local_a\":{\"color\":{\"value\":1,\"at\":" + at + "}}}"), "a change of an unknown field"),
                         (Account(",\"changed\":{\"local_a\":{\"title\":{\"value\":\"t\",\"at\":" + at + ",\"from\":\"x\"}}}"), "a change with more to it"),
                         (Account(",\"pending\":{\"stay\":1,\"sessions\":{}}"), "a plan in progress without its renamed groups"),
                         (Account(",\"pending\":{\"stay\":1,\"sessions\":{\"local_a\":{\"filed\":\"yes\"}},\"renamed\":{}}"), "a filing that is text"),
                         (Account(",\"pending\":{\"stay\":1,\"sessions\":{},\"groups\":[],\"renamed\":{}}"), "a list target that is no change"),
                         (Account(",\"evidence\":{\"local_a\":{\"pinned\":\"yes\"}}"), "evidence that is text"),
                         (Account(",\"pending\":{\"stay\":1,\"sessions\":{},\"renamed\":{\"g\":\"G\"}}"), "a group set aside without where it went"),
                         (Account(",\"renamings\":{}"), "renamings that are no list"),
                         (Account(",\"renamings\":[{\"from\":\"G\",\"to\":\"H\"}]"), "a renaming without its time"),
                         (Account(",\"renamings\":[{\"from\":\"G\",\"to\":5,\"at\":" + at + "}]"), "a renaming to no name")
                     })
            {
                File.WriteAllText(path, bad);
                Check(ClaudeSessionLayout.Load(root) is (null, true), "a layout that cannot be read whole is reported, not taken as empty: " + what);
            }
            File.WriteAllText(path, "{\"version\":2,\"active\":\"" + ReferenceKey + "\",\"accounts\":{\"" + CurrentKey + "\":{\"label\":\"서브\"}}}");
            Check(ClaudeSessionLayout.Load(root) is ({ Active: null } unmapped, true) && unmapped.Accounts.Count == 1,
                "an active account no longer kept is let go");

            var forgetting = new ClaudeSessionLayout { Active = ReferenceKey };
            forgetting.Accounts[ReferenceKey] = new("메인");
            forgetting.Checkpoints[ReferenceKey] = new(At(0), 0, [], new Dictionary<string, Shown>());
            forgetting.Changed[ReferenceKey] = new(StringComparer.Ordinal);
            forgetting.GroupsChanged[ReferenceKey] = ListChange([], At(0));
            forgetting.Pendings[ReferenceKey] = new(0, new Dictionary<string, Target>(), null, Aside());
            forgetting.Evidences[ReferenceKey] = new(StringComparer.Ordinal);
            forgetting.Renamings[ReferenceKey] = [new("G", "H", At(0))];
            forgetting.Forget(ReferenceKey);
            Check(forgetting is { Active: null, Accounts.Count: 0, Checkpoints.Count: 0, Changed.Count: 0, GroupsChanged.Count: 0, Pendings.Count: 0,
                      Evidences.Count: 0, Renamings.Count: 0 },
                "an account no longer mapped is forgotten whole");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void TestPinList()
    {
        var key = PinKey(5);
        Check(ClaudeSidebarPins.IsPinListRow(key), "the pin list's data row is recognised");
        Check(!ClaudeSidebarPins.IsPinListRow([0x00, 0x05, 0x01, 0x02, .. ClaudeSidebarPins.EncodedKey]), "its exists row is not data");
        Check(!ClaudeSidebarPins.IsPinListRow([0x00, 0x00, 0x00, 0x01, .. ClaudeSidebarPins.EncodedKey]), "metadata is not data");
        Check(ClaudeSidebarPins.IsPinListRow([0x20, 0x2C, 0x01, 0x01, 0x01, .. ClaudeSidebarPins.EncodedKey]),
            "a database id of two bytes is read from the first byte");
        var routines = Encoding.BigEndianUnicode.GetBytes("store:pin-state:dframe-starred-routines");
        Check(!ClaudeSidebarPins.IsPinListRow([0x00, 0x05, 0x01, 0x01, 0x01, (byte)(routines.Length / 2), .. routines]),
            "another pin list is not the Code tab's");
        Check(ClaudeSidebarPins.Parse(PinValue(["local_a", "local_b"])).SetEquals(["local_a", "local_b"]),
            "a one-byte string behind the trailer offset is read");
        Check(ClaudeSidebarPins.Parse(PinValue(["local_한"], twoByte: true, trailer: false)).SetEquals(["local_한"]),
            "a two-byte string after padding, without a trailer, is read");
        foreach (var (bad, why) in new (byte[], string)[]
                 {
                     ([0x6f, 0xFF, 0x15, 0xFF, 0x10, (byte)'o', 0x00], "an object"),
                     (PinValue([], json: "{\"state\":{}}"), "no starred ids"),
                     (PinValue(["local_a"])[..^3], "a truncated value"),
                     ([0x6f, 0x00], "no envelope")
                 })
        {
            try { ClaudeSidebarPins.Parse(bad); }
            catch (Exception exception) when (exception is InvalidDataException or System.Text.Json.JsonException) { continue; }
            throw new InvalidOperationException("accepted " + why);
        }

        var root = TempRoot("pins");
        try
        {
            var db = CreateDatabase(Path.Combine(root, "idb"));
            AppendRaw(db, 42, key, PinValue(["local_a"]));
            AppendRaw(db, 43, [0x00, 0x05, 0x01, 0x02, .. ClaudeSidebarPins.EncodedKey], [0x6f]);
            AppendRaw(db, 44, key, PinValue(["local_a", "local_c"]));
            Check(ClaudeSidebarPins.Read(db).SetEquals(["local_a", "local_c"]), "the newest pin list is read from the app's IndexedDB");
            AppendRaw(db, 45, PinKey(6), PinValue(["local_z"]));
            Rejects(() => ClaudeSidebarPins.Read(db), "two live pin lists");
            AppendRaw(db, 46, PinKey(6), null);
            AppendRaw(db, 47, key, null);
            Rejects(() => ClaudeSidebarPins.Read(db), "a deleted pin list");
            AppendRaw(db, 48, key, PinValue([]));
            Check(ClaudeSidebarPins.Read(db).Count == 0, "an empty pin list is read as empty");
            using (var stream = new FileStream(Path.Combine(db, "000005.log"), FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                stream.Write([0x10, 0x20, 0x30, 0x40, 0x64, 0x00, 0x01, 0x05, 0x06]);
            Rejects(() => ClaudeSidebarPins.Read(db), "a log ending inside a record");
            Rejects(() => ClaudeSidebarPins.Read(Path.Combine(root, "missing")), "a missing IndexedDB");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void TestTranscript()
    {
        var root = TempRoot("transcripts");
        try
        {
            var id = Guid.NewGuid().ToString("D");
            var project = Directory.CreateDirectory(Path.Combine(root, "E--project")).FullName;
            var path = Path.Combine(project, id + ".jsonl");
            File.WriteAllLines(path,
            [
                "{\"type\":\"user\",\"timestamp\":\"2026-10-02T05:04:41.426Z\"}",
                "{\"type\":\"assistant\",\"timestamp\":\"2026-10-02T05:05:00.000Z\"}",
                "{\"type\":\"history-suppression\",\"ts\":\"2026-10-12T11:37:00.000Z\"}",
                "{\"type\":\"cost-state\"}"
            ]);
            File.SetCreationTimeUtc(path, new DateTime(2026, 10, 1, 2, 1, 9, DateTimeKind.Utc));
            File.SetLastWriteTimeUtc(path, new DateTime(2026, 10, 2, 5, 5, 1, DateTimeKind.Utc));
            Check(ClaudeSessionSync.Transcript(root, id) is { Dated: true } span && span.Created == DateTimeOffset.Parse("2026-10-01T02:01:09Z")
                  && span.Written == DateTimeOffset.Parse("2026-10-02T05:05:01Z") && span.LastEntry == DateTimeOffset.Parse("2026-10-02T05:05:00Z"),
                "a transcript spans from its file's creation to its newest entry timestamp; bookkeeping lines do not count");
            File.SetCreationTimeUtc(path, new DateTime(2026, 10, 12, 0, 0, 0, DateTimeKind.Utc));
            Check(ClaudeSessionSync.Transcript(root, id) is { Dated: false }, "a file created after it was last written is not dated by its creation");
            Check(ClaudeSessionSync.Transcript(root, Guid.NewGuid().ToString("D")) is null, "a missing transcript cannot be dated");
            // A continuation starts with copies of the earlier entries; the first entry of its own dates the hand-over.
            var next = Guid.NewGuid().ToString("D");
            File.WriteAllLines(Path.Combine(project, next + ".jsonl"),
            [
                "{\"type\":\"user\",\"uuid\":\"00000000-0000-0000-0000-000000000001\",\"timestamp\":\"2026-10-02T05:04:41.426Z\"}",
                "{\"type\":\"assistant\",\"toolUseResult\":{\"uuid\":\"00000000-0000-0000-0000-00000000000f\",\"timestamp\":\"2026-09-01T00:00:00Z\"},\"uuid\":\"00000000-0000-0000-0000-000000000003\",\"timestamp\":\"2026-10-02T05:07:07.340Z\"}",
                "{\"type\":\"user\",\"uuid\":\"00000000-0000-0000-0000-000000000004\",\"timestamp\":\"2026-10-02T06:00:00Z\"}"
            ]);
            File.WriteAllLines(path,
            [
                "{\"type\":\"user\",\"uuid\":\"00000000-0000-0000-0000-000000000001\",\"timestamp\":\"2026-10-02T05:04:41.426Z\"}",
                "{\"type\":\"cost-state\"}"
            ]);
            Check(ClaudeSessionSync.HandOver(root, id, next) == DateTimeOffset.Parse("2026-10-02T05:07:07.340Z"),
                "the hand-over is the oldest entry the earlier transcript does not hold, by each line's own id and time");
            Check(ClaudeSessionSync.HandOver(root, next, id) is null && ClaudeSessionSync.HandOver(root, id, Guid.NewGuid().ToString("D")) is null,
                "nothing new, or a missing transcript, dates no hand-over");
            // A broken line could be the entry that matters (a later turn here, the first new entry there): nothing is dated.
            File.AppendAllText(path, "{\"type\":\"user\",\"uuid\":\"00000000-0000-0000-0000-000000000009\",\"timestamp\":\"2026-10-02T09:00");
            Check(ClaudeSessionSync.Transcript(root, id) is null && ClaudeSessionSync.HandOver(root, id, next) is null,
                "a torn last line leaves the transcript undated and no hand-over");
            File.WriteAllLines(path, ["{\"type\":\"user\",\"uuid\":\"00000000-0000-0000-0000-000000000001\",\"timestamp\":\"2026-10-02T05:04:41.426Z\"}"]);
            File.WriteAllText(Path.Combine(project, next + ".jsonl"),
                "{\"type\":\"user\",\"uuid\":\"00000000-0000-0000-0000-000000000001\",\"timestamp\":\"2026-10-02T05:04:41.426Z\"}\n"
                + "{\"type\":\"user\",\"uuid\":\"00000000-0000-0000-0000-000000000003\",\"timestamp\":\"2026-10-02T05:07:07Z\"}{\"type\":\"x\"}\n"
                + "{\"type\":\"user\",\"uuid\":\"00000000-0000-0000-0000-000000000004\",\"timestamp\":\"2026-10-02T06:00:00Z\"}\n");
            Check(ClaudeSessionSync.HandOver(root, id, next) is null && ClaudeSessionSync.Transcript(root, next) is null,
                "a broken first new entry dates no hand-over, rather than a later one");
            // What a crash leaves in an earlier transcript: lost lines of NUL bytes, later lines intact (a real 86 MiB case).
            const string Kept = "{\"type\":\"user\",\"uuid\":\"00000000-0000-0000-0000-000000000001\",\"timestamp\":\"2026-10-02T05:04:41.426Z\"}\n";
            const string Later = "{\"type\":\"assistant\",\"uuid\":\"00000000-0000-0000-0000-000000000002\",\"timestamp\":\"2026-10-02T05:05:00Z\"}\n";
            const string New = "{\"type\":\"user\",\"uuid\":\"00000000-0000-0000-0000-000000000003\",\"timestamp\":\"2026-10-02T05:07:07Z\"}\n";
            var lost = new string('\0', 10158);
            File.WriteAllText(Path.Combine(project, next + ".jsonl"), Kept + Later + New);
            File.WriteAllText(path, Kept + lost + "\n" + Later);
            Check(ClaudeSessionSync.HandOver(root, id, next) == DateTimeOffset.Parse("2026-10-02T05:07:07Z"),
                "a lost line of NUL bytes in the earlier transcript still dates the hand-over");
            File.WriteAllText(path, Kept + lost + "\r\n" + Later);
            Check(ClaudeSessionSync.HandOver(root, id, next) == DateTimeOffset.Parse("2026-10-02T05:07:07Z"),
                "a lost line ending in a carriage return is lost all the same");
            File.WriteAllText(path, Kept + lost + "\n");
            Check(ClaudeSessionSync.HandOver(root, id, next) == DateTimeOffset.Parse("2026-10-02T05:05:00Z"),
                "an entry only the lost line held makes the hand-over earlier, never later");
            Check(ClaudeSessionSync.Transcript(root, id) is null,
                "a lost line in a transcript's tail still leaves it undated: what it held may be its newest entry");
            File.WriteAllText(path, Kept + lost + "{\"type\":\"x\"}\n" + Later);
            Check(ClaudeSessionSync.HandOver(root, id, next) is null, "NUL bytes before a line's content are a broken line");
            File.WriteAllText(path, Kept + "\0{\"type\":\"user\",\"timestamp\":\"2026-10-02T09:00:00Z\"}\n");
            Check(ClaudeSessionSync.HandOver(root, id, next) is null, "a single NUL before an entry is a broken line, not a lost one");
            File.WriteAllText(path, Kept + Later);
            File.WriteAllText(Path.Combine(project, next + ".jsonl"), Kept + Later + lost + "\n" + New);
            Check(ClaudeSessionSync.HandOver(root, id, next) is null,
                "a lost line in the later transcript could be its first new entry: no hand-over, rather than a later one");
            Check(ClaudeSessionSync.Transcript(root, "..\\x") is null, "only transcript ids are looked up");
            // Only turns of the conversation count: what the app or the harness adds on its own does not.
            var noted = Guid.NewGuid().ToString("D");
            File.WriteAllLines(Path.Combine(project, noted + ".jsonl"),
            [
                "{\"type\":\"user\",\"uuid\":\"10000000-0000-0000-0000-000000000001\",\"timestamp\":\"2026-10-02T05:00:00Z\"}",
                "{\"type\":\"assistant\",\"uuid\":\"10000000-0000-0000-0000-000000000002\",\"timestamp\":\"2026-10-02T05:01:00Z\",\"message\":{\"model\":\"claude-opus\",\"content\":[]}}",
                "{\"type\":\"user\",\"uuid\":\"10000000-0000-0000-0000-000000000003\",\"timestamp\":\"2026-10-05T00:00:00Z\",\"origin\":{\"kind\":\"task-notification\"}}",
                "{\"type\":\"assistant\",\"uuid\":\"10000000-0000-0000-0000-000000000004\",\"timestamp\":\"2026-10-05T00:00:01Z\",\"message\":{\"model\":\"<synthetic>\"}}",
                "{\"type\":\"user\",\"uuid\":\"10000000-0000-0000-0000-000000000005\",\"timestamp\":\"2026-10-05T00:00:02Z\",\"isMeta\":true}",
                "{\"type\":\"queue-operation\",\"timestamp\":\"2026-10-05T00:00:03Z\"}",
                "{\"type\":\"summary\",\"uuid\":\"10000000-0000-0000-0000-000000000006\",\"timestamp\":\"2026-10-05T00:00:04Z\"}"
            ]);
            Check(ClaudeSessionSync.Transcript(root, noted)?.LastEntry == DateTimeOffset.Parse("2026-10-02T05:01:00Z"),
                "a transcript's newest entry is its newest turn: a background task's notice, its stand-in reply, meta messages and records do not count");
            var goneOn = Guid.NewGuid().ToString("D");
            File.WriteAllLines(Path.Combine(project, goneOn + ".jsonl"),
            [
                "{\"type\":\"user\",\"uuid\":\"10000000-0000-0000-0000-000000000001\",\"timestamp\":\"2026-10-02T05:00:00Z\"}",
                "{\"type\":\"user\",\"uuid\":\"20000000-0000-0000-0000-000000000001\",\"timestamp\":\"2026-10-02T05:30:00Z\",\"origin\":{\"kind\":\"task-notification\"}}",
                "{\"type\":\"user\",\"uuid\":\"20000000-0000-0000-0000-000000000002\",\"timestamp\":\"2026-10-02T06:00:00Z\"}"
            ]);
            Check(ClaudeSessionSync.HandOver(root, noted, goneOn) == DateTimeOffset.Parse("2026-10-02T06:00:00Z"),
                "the hand-over is dated by the later transcript's first turn of its own");
            // Whole transcripts are streamed: lines longer than the read buffer, and lines across its edges, read the same.
            var bulk = "{\"type\":\"attachment\",\"data\":\"" + new string('x', 3 * 1024 * 1024) + "\"}\n";
            var filler = string.Concat(Enumerable.Range(0, 40000).Select(index =>
                "{\"type\":\"progress\",\"n\":" + index.ToString(System.Globalization.CultureInfo.InvariantCulture) + "}\n"));
            File.WriteAllText(path, Kept + bulk + filler + Later);
            File.WriteAllText(Path.Combine(project, next + ".jsonl"), Kept + filler + bulk + Later + filler + New + bulk);
            Check(ClaudeSessionSync.HandOver(root, id, next) == DateTimeOffset.Parse("2026-10-02T05:07:07Z"),
                "a hand-over is dated the same across lines longer than the read buffer");
            File.WriteAllText(Path.Combine(project, next + ".jsonl"), Kept + filler + bulk[..^2] + "\n" + New);
            Check(ClaudeSessionSync.HandOver(root, id, next) is null, "a broken long line still dates no hand-over");
            File.WriteAllText(Path.Combine(project, next + ".jsonl"), Kept + bulk + New.TrimEnd('\n'));
            Check(ClaudeSessionSync.HandOver(root, id, next) == DateTimeOffset.Parse("2026-10-02T05:07:07Z"),
                "a last line without a line end is read too");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void TestBuildFromDisk()
    {
        var root = TempRoot("build");
        try
        {
            var sessions = Path.Combine(root, "sessions"); var ledger = Directory.CreateDirectory(Path.Combine(root, "ledger")).FullName;
            void Write(string account, string org, string id, JsonObject record)
            {
                var directory = Directory.CreateDirectory(Path.Combine(sessions, account, org)).FullName;
                record["sessionId"] = id; File.WriteAllText(Path.Combine(directory, id + ".json"), record.ToJsonString());
            }
            // Record pins are stale copies and are ignored: the app's pin list says the other account's session is pinned.
            Write(CurrentAccount, CurrentOrg, "local_self", new() { ["title"] = "지금", ["cliSessionId"] = "AAA", ["isStarred"] = true });
            Write(CurrentAccount, CurrentOrg, "local_gone", new() { ["title"] = "보관됨", ["cliSessionId"] = "bbb", ["isArchived"] = true });
            Write(ReferenceAccount, ReferenceOrg, "local_ref", new() { ["title"] = "기준 이름", ["cliSessionId"] = "aaa", ["isStarred"] = false });
            Write(ReferenceAccount, ReferenceOrg, "local_ref_gone", new() { ["title"] = "보관된 기준", ["cliSessionId"] = "bbb" });
            var idb = CreateDatabase(Path.Combine(root, "idb"));
            AppendRaw(idb, 42, PinKey(5), PinValue(["local_ref"]));
            var sources = new ClaudeSessionSync.Sources(sessions, Path.Combine(root, "no-local-storage"), idb, Path.Combine(root, "projects"), ledger);
            Check(ClaudeSessionSync.Build(sources, "local_nowhere").Status == "unknown_session", "an unknown session is reported");
            Check(ClaudeSessionSync.Build(sources, "local_self") is { Status: "widget_not_ready", Current: var current } && current == CurrentKey,
                "before the widget keeps what the accounts changed, nothing is planned");
            File.WriteAllText(Path.Combine(ledger, ClaudeSessionLayout.FileName), "{\"accounts\":");
            Check(ClaudeSessionSync.Build(sources, "local_self").Status == "layout_unreadable", "a layout that cannot be read is reported");
            Keep(ledger, (CurrentKey, "서브", null));
            Check(ClaudeSessionSync.Build(sources, "local_self") is { Status: "no_other_accounts", CurrentLabel: "서브" },
                "with no other account there is nothing to take");
            Keep(ledger, (CurrentKey, "서브", null), (ThirdKey, "셋째", At(0)));
            Check(ClaudeSessionSync.Build(sources, "local_self").Status == "no_other_accounts", "an account without a session list here is passed over");
            // Nothing checkpointed yet: the account the app left later gives what its own records show.
            Keep(ledger, (CurrentKey, "서브", null), (ReferenceKey.ToUpperInvariant(), "메인", At(0)), (ThirdKey, "셋째", null));
            var plan = ClaudeSessionSync.Build(sources, "local_self");
            Check(plan is { Status: "ok", CurrentLabel: "서브", Checkpointed: null, Others: [{ Label: "메인", Matched: 1, OnlyThere: ["보관된 기준"] }] }
                  && plan.Renames!.Single() is { Session: "local_self", Title: "기준 이름", From: "지금", By: "메인" }
                  && plan.Pins!.Single() is { Session: "local_self", Pinned: true } && plan.Moves!.Count == 0
                  && plan.Warnings!.SequenceEqual(["groups_unreadable"]),
                "records are read case-insensitively, archived ones skipped, pins come from the app's pin list, and without local storage groups are reported unconfirmed");
            Check(ClaudeSessionSync.Build(sources, "local_self", only: ThirdKey).Status == "no_other_accounts",
                "a plan limited to one account takes from that account alone");
            File.WriteAllText(Path.Combine(ledger, "ledger.json"), "{\"created\":[]}");
            using (new FileStream(Path.Combine(ledger, "ledger.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                var locked = ClaudeSessionSync.Build(sources, "local_self");
                Check(locked.Pins!.Count == 0 && locked.Renames!.Count == 0 && locked.Warnings!.Contains("ledger_unreadable"),
                    "a ledger that is there but cannot be opened is unknown, not empty");
            }
            File.WriteAllText(Path.Combine(ledger, "ledger.json"), "{\"created\":[{\"dst\":");
            var unknownCopies = ClaudeSessionSync.Build(sources, "local_self");
            Check(unknownCopies.Pins!.Count == 0 && unknownCopies.Renames!.Count == 0 && unknownCopies.Warnings!.Contains("ledger_unreadable"),
                "a ledger that cannot be read leaves pins and names alone, rather than take copies for choices");
            File.WriteAllText(Path.Combine(ledger, "ledger.json"), "{\"created\":[{\"dst\":\"" + ReferenceKey + "\",\"file\":\"local_ref.json\"}]}");
            Check(ClaudeSessionSync.Build(sources, "local_self") is { Renames.Count: 0 } copied && copied.Pins!.Single() is { Session: "local_self", Pinned: true },
                "the widget's copy in the other account gives the pin it has there, not its title");
            File.Delete(Path.Combine(ledger, "ledger.json"));
            var unpinned = ClaudeSessionSync.Build(sources with { IndexedDb = Path.Combine(root, "no-idb") }, "local_self");
            Check(unpinned.Pins!.Count == 0 && unpinned.Renames!.Count == 1 && unpinned.Warnings!.SequenceEqual(["groups_unreadable", "pins_unreadable"]),
                "without the pin list no pin is planned, never one taken from records");
            // A record that cannot be read now, here or in another list, could be the newest: nothing is decided.
            foreach (var (account, org, id) in new[] { (CurrentAccount, CurrentOrg, "local_gone"), (ReferenceAccount, ReferenceOrg, "local_ref_gone") })
                using (new FileStream(Path.Combine(sessions, account, org, id + ".json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    var partial = ClaudeSessionSync.Build(sources, "local_self");
                    Check(partial is { Status: "ok", Pins.Count: 0, Renames.Count: 0, Moves.Count: 0 } && partial.Warnings!.Contains("list_incomplete"),
                        "a list that cannot be read whole plans no title, pin or group");
                }
            // An older state beside its continuation: listed when nothing was written to it after the next transcript began.
            var projects = Directory.CreateDirectory(Path.Combine(root, "projects", "E--p")).FullName;
            string Transcript(string created, params string[] entries)
            {
                var id = Guid.NewGuid().ToString("D");
                var path = Path.Combine(projects, id + ".jsonl");
                File.WriteAllLines(path, entries.Select(entry => entry.Split('@') is [var uuid, var at]
                    ? "{\"type\":\"user\",\"uuid\":\"" + uuid + "\",\"timestamp\":\"" + at + "\"}" : entry));
                File.SetCreationTimeUtc(path, DateTime.Parse(created, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal));
                return id;
            }
            const string First = "00000000-0000-0000-0000-0000000000a1", Second = "00000000-0000-0000-0000-0000000000a2";
            var earlier = Transcript("2026-10-01T02:01:09Z", First + "@2026-10-02T05:04:41Z");
            var later = Transcript("2026-10-02T05:07:07Z", First + "@2026-10-02T05:04:41Z", Second + "@2026-10-02T05:07:07Z");
            Write(CurrentAccount, CurrentOrg, "local_was", new() { ["title"] = "둘", ["cliSessionId"] = earlier });
            Write(CurrentAccount, CurrentOrg, "local_is", new()
            {
                ["title"] = "둘", ["cliSessionId"] = later, ["priorCliSessionIds"] = new JsonArray(earlier.ToUpperInvariant())
            });
            var listed = ClaudeSessionSync.Build(sources, "local_self").Archive!.Single();
            Check(ClaudeSessionSync.Build(sources, "local_was").Archive!.Count == 0, "the calling session is never listed as an older state");
            File.AppendAllText(Path.Combine(projects, earlier + ".jsonl"), "{\"type\":\"user\",\"uuid\":\"00000000-0000-0000-0000-0000000000a3\",\"timestamp\":\"2026-10-03");
            Check(ClaudeSessionSync.Build(sources, "local_self").Archive!.Count == 0, "an older state whose transcript ends in a broken line stays");
            Check(listed is { Session: "local_was", Newest: "local_is", Ready: false },
                "an older state is dated from the transcripts and listed for archiving");
            Check(ClaudeSessionReference.Read(ledger) is (null, true), "no old reference is known as none");
            new ClaudeSessionReference(ReferenceKey, "메인", null, DateTimeOffset.UtcNow, new Dictionary<string, string>()).Save(ledger);
            using (new FileStream(Path.Combine(ledger, ClaudeSessionReference.FileName), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(ClaudeSessionReference.Read(ledger) is (null, false), "an old reference that cannot be opened now is unknown, not none");
            Check(ClaudeSessionReference.Read(ledger) is ({ Account: var kept }, true) && kept == ReferenceKey, "it is read once it can be opened");
            File.WriteAllText(Path.Combine(ledger, ClaudeSessionReference.FileName), "{\"account\":\"not-an-account\"}");
            Check(ClaudeSessionReference.Read(ledger) is (null, false), "a malformed old reference is unknown, never taken for none");
            File.Delete(Path.Combine(ledger, ClaudeSessionLayout.FileName));
            Check(ClaudeSessionSync.Build(sources, "local_self").Status == "widget_not_ready",
                "a plan waits for the widget's layout whatever the old reference holds");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void TestGroupsFromLocalStorage()
    {
        var root = TempRoot("groups");
        try
        {
            var sessions = Path.Combine(root, "sessions"); var ledger = Directory.CreateDirectory(Path.Combine(root, "ledger")).FullName;
            void Write(string account, string org, string id, string cli)
            {
                var directory = Directory.CreateDirectory(Path.Combine(sessions, account, org)).FullName;
                File.WriteAllText(Path.Combine(directory, id + ".json"), new JsonObject { ["sessionId"] = id, ["title"] = "t", ["cliSessionId"] = cli }.ToJsonString());
            }
            Write(CurrentAccount, CurrentOrg, "local_c", "same"); Write(ReferenceAccount, ReferenceOrg, "local_r", "same");
            // The other account was left later, so what it shows is newer.
            Keep(ledger, (CurrentKey, "서브", null), (ReferenceKey, "메인", At(0)));
            var db = CreateDatabase(Path.Combine(root, "leveldb"));
            var idb = CreateDatabase(Path.Combine(root, "idb"));
            AppendRaw(idb, 42, PinKey(5), PinValue([]));
            var sources = new ClaudeSessionSync.Sources(sessions, db, idb, Path.Combine(root, "projects"), ledger);
            var state = new JsonObject
            {
                ["state"] = new JsonObject
                {
                    ["customGroupsByScope"] = new JsonObject
                    {
                        [CurrentKey] = new JsonObject { ["groups"] = new JsonArray(), ["assignments"] = new JsonObject() },
                        [ReferenceKey] = new JsonObject
                        {
                            ["groups"] = new JsonArray(new JsonObject { ["id"] = "cg-1", ["name"] = "대기" }),
                            ["assignments"] = new JsonObject { ["code:local_r"] = "cg-1" }
                        }
                    }
                },
                ["version"] = 1
            };
            var backups = Path.Combine(root, "backups");
            Check(WriteSidebar(db, state, backups), "the fixture sidebar state is stored");
            var log = Path.Combine(db, "000005.log");
            ClaudeSessionSync.Plan plan;
            // The app holds its log open for writing while it runs.
            using (new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                plan = ClaudeSessionSync.Build(sources, "local_c");
            Check(plan.Status == "ok" && plan.CreateGroups!.SequenceEqual(["대기"]) && plan.Moves!.Single() is { Group: "대기", GroupId: null } move
                  && move.Sessions.SequenceEqual(["local_c"]) && plan.Warnings is null,
                "groups are read from the app's local storage while it is open, and a missing group is created");

            // Another account's scope that is missing offers no groups; one there but unfamiliar holds every group change.
            JsonArray Waiting() => new(new JsonObject { ["id"] = "cg-1", ["name"] = "대기" });
            foreach (var other in new JsonNode?[] { null, new JsonObject(), new JsonObject { ["groups"] = "x", ["assignments"] = new JsonObject() },
                         new JsonObject { ["groups"] = new JsonArray(new JsonObject { ["id"] = "cg-1" }), ["assignments"] = new JsonObject() },
                         new JsonObject { ["groups"] = Waiting() }, new JsonObject { ["groups"] = Waiting(), ["assignments"] = null },
                         new JsonObject { ["assignments"] = new JsonObject() }, new JsonObject { ["groups"] = null, ["assignments"] = new JsonObject() } })
            {
                var changed = (JsonObject)state.DeepClone();
                var scopes = changed["state"]!["customGroupsByScope"]!.AsObject();
                scopes[CurrentKey] = new JsonObject
                {
                    ["groups"] = new JsonArray(new JsonObject { ["id"] = "cg-9", ["name"] = "내 그룹" }),
                    ["assignments"] = new JsonObject { ["code:local_c"] = "cg-9" }
                };
                if (other is null) scopes.Remove(ReferenceKey); else scopes[ReferenceKey] = other;
                Check(WriteSidebar(db, changed, backups), "the fixture sidebar state is replaced");
                var unconfirmed = ClaudeSessionSync.Build(sources, "local_c");
                Check(unconfirmed.Moves!.Count == 0 && unconfirmed.CreateGroups!.Count == 0
                      && (other is null ? unconfirmed.Warnings is null : unconfirmed.Warnings!.SequenceEqual(["groups_unreadable"])),
                    "an unconfirmed scope of another account plans no group change");
            }
            // A log that ends inside a record (the app mid-write) is retried, then reported unconfirmed.
            // Header of a 100-byte full record, then only two of its bytes.
            using (var stream = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                stream.Write([0x10, 0x20, 0x30, 0x40, 0x64, 0x00, 0x01, 0x05, 0x06]);
            Check(!new ClaudeLocalStorage(db).Read(["dframe-store"]).Clean, "the fixture log ends inside a record");
            var torn = ClaudeSessionSync.Build(sources, "local_c");
            Check(torn.Moves!.Count == 0 && torn.Warnings!.SequenceEqual(["groups_unreadable"]),
                "a torn log is not taken as the app's current groups");
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>What an account changed since its checkpoint, told apart from what the plan in progress there set out to
    /// do: what it reached is the change it came from, what it has not reached yet (or a pin its move undid) is no change,
    /// and anything else is the account's own.</summary>
    private static void TestObserve()
    {
        var layout = Groups(["g=G", "h=H"], ("local_b", "g"));
        var records = new[] { R("local_a", "가", true, "a"), R("local_b", "나", false, "b"), R("local_c", "다", false, "c"), R("local_d", "라", false, "d") };
        ClaudeSessionSync.State State(IReadOnlyList<Record> shown, SyncLayout? groups = null, IReadOnlyDictionary<string, string?>? copies = null) =>
            new(shown, groups ?? layout, true, copies ?? NoCopies);
        var before = PointOf(records, layout, At(0), 1);
        var moved = layout with { Assignments = new Dictionary<string, string> { ["local_b"] = "h" } };
        var plain = ClaudeSessionSync.Observe(State([records[0] with { Pinned = false }, records[1], records[2] with { Title = "다다" }, records[3]], moved),
            before, At(10));
        Check(plain.Changes["local_a"] == new Changes(Pinned: new(false, At(10))) && plain.Changes["local_b"] == new Changes(Group: new("H", At(10)))
              && plain.Changes["local_c"] == new Changes(Title: new("다다", At(10))) && !plain.Changes.ContainsKey("local_d")
              && plain.Groups is null && plain.Open.Count == 0 && !plain.GroupsMoving,
            "a value that differs from the checkpoint's is a change, dated as asked");

        // The plan in progress set out to pin 라 (메인's change at 5), to file 다 under H and pin it (its move unpins it), to
        // rename 나 (셋째's change at 7), and to unpin 가 on its way to the archive (nobody's choice).
        var pending = new ClaudeSessionLayout.Pending(1, new Dictionary<string, Target>
        {
            ["local_d"] = new(Pinned: new(true, At(5), ReferenceKey)),
            ["local_c"] = new(Pinned: new(true, At(6), ReferenceKey), Group: new("H", At(6), ReferenceKey), Filed: true),
            ["local_b"] = new(Title: new("나나", At(7), ThirdKey)),
            ["local_a"] = new(Pinned: new(false, DateTimeOffset.MinValue))
        }, null, Aside());
        var cInH = layout with { Assignments = new Dictionary<string, string> { ["local_b"] = "g", ["local_c"] = "h" } };
        var done = ClaudeSessionSync.Observe(State([records[0] with { Pinned = false }, records[1] with { Title = "나나" },
            records[2] with { Pinned = true }, records[3] with { Pinned = true }], cInH), before, At(10), pending);
        Check(done.Changes["local_d"] == new Changes(Pinned: new(true, At(5), ReferenceKey))
              && done.Changes["local_c"] == new Changes(new(true, At(6), ReferenceKey), new("H", At(6), ReferenceKey))
              && done.Changes["local_b"] == new Changes(Title: new("나나", At(7), ThirdKey)) && !done.Changes.ContainsKey("local_a")
              && done.Open.Count == 0,
            "what the plan reached counts as the change it came from, at its time and from its account; an operational value as none");
        var untouched = ClaudeSessionSync.Observe(State(records), before, At(10), pending);
        Check(untouched.Changes.Count == 0 && untouched.Open.Keys.Order(StringComparer.Ordinal).SequenceEqual(["local_a", "local_b", "local_c", "local_d"])
              && untouched.Open["local_c"] is { Filed: true, Pinned: not null, Group: not null },
            "what the plan has not reached yet is no change, and stays in progress");
        var halfway = ClaudeSessionSync.Observe(State(records, cInH), before, At(10), pending);
        Check(halfway.Changes["local_c"] == new Changes(Group: new("H", At(6), ReferenceKey)) && halfway.Open["local_c"] is { Pinned: not null, Group: null },
            "a pin its move into a group undid is no unpinning: it stays in progress while the group counts as reached");
        var otherwise = ClaudeSessionSync.Observe(State([records[0], records[1] with { Title = "딴 이름" }, records[2], records[3]]), before, At(10), pending);
        Check(otherwise.Changes["local_b"] == new Changes(Title: new("딴 이름", At(10))) && !otherwise.Open.ContainsKey("local_b"),
            "anything else than the target or the checkpoint's value is the account's own change");
        var ownTarget = pending with
        {
            Sessions = new Dictionary<string, Target> { ["local_d"] = new(Pinned: new(true, DateTimeOffset.MaxValue)) }
        };
        Check(ClaudeSessionSync.Observe(State([records[0], records[1], records[2], records[3] with { Pinned = true }]), before, At(10), ownTarget)
                  .Changes["local_d"] == new Changes(Pinned: new(true, At(10))),
            "a target this account chose in its own stay, reached, is its change as of now");

        // The group list.
        var list = new ClaudeSessionLayout.Pending(1, new Dictionary<string, Target>(), ListChange(["H", "G"], At(8), ReferenceKey),
            Aside(("g", "G")));
        var aside = ClaudeSessionSync.Observe(State(records, Groups(["g=G (옮기는 중)", "h=H"], ("local_b", "g"))), before, At(10), list);
        Check(aside is { Groups: null, GroupsMoving: true, GroupsAside: true, Renamings.Count: 0 } && !aside.Changes.ContainsKey("local_b"),
            "a list holding a group the plan renamed out of the way, before the plan made it anew, is mid-move, no change; its sessions keep the name it stood for");
        var leftAside = ClaudeSessionSync.Observe(State(records, Groups(["g=G (옮기는 중)", "h=H", "g2=G"], ("local_b", "g"))), before, At(10), list);
        Check(leftAside is { GroupsMoving: false, GroupsAside: true, Renamings.Count: 0 } && leftAside.Groups is { Order: ["H", "G"] } leftList
              && leftList.At == At(8) && !leftAside.Changes.ContainsKey("local_b"),
            "once the plan made every group of its list, the groups not aside are the list (reached here), a group still aside apart");
        var renamedMidMove = ClaudeSessionSync.Observe(State(records, Groups(["g=G (옮기는 중)", "h=H2"], ("local_b", "g"))), before, At(10), list);
        Check(renamedMidMove is { Groups: null, GroupsMoving: true } && renamedMidMove.Renamings.SequenceEqual([new("H", "H2", At(10))]),
            "a group renamed by hand is seen even while the list is mid-move");
        var notYet = ClaudeSessionSync.Observe(State(records, Groups(["g=G", "h=H"], ("local_b", "g"))), before, At(10), list);
        Check(notYet is { Groups: null, GroupsMoving: false }, "a rename the plan did not do yet is not mid-move");
        var marked = ClaudeSessionSync.Observe(State(records, Groups(["g=G (옮기는 중)", "h=H"], ("local_b", "g"))), before, At(10));
        Check(marked is { Groups: null, GroupsMoving: true } && !marked.Changes.ContainsKey("local_b"),
            "without the plan in progress the moving mark tells a group mid-move");
        var reached = ClaudeSessionSync.Observe(State(records, Groups(["h=H", "g2=G"], ("local_b", "g2"))), before, At(10), list);
        Check(reached.Groups is { Order: ["H", "G"], By: var reachedBy } reachedList && reachedList.At == At(8) && reachedBy == ReferenceKey,
            "a list the plan reached counts as the change it came from");
        var renamedInPlace = ClaudeSessionSync.Observe(State(records, Groups(["g=G2", "h=H"], ("local_b", "g"))), before, At(10));
        Check(renamedInPlace.Groups is { Order: ["G2", "H"] } renaming && renaming.Renamed["G"] == "G2" && renaming.At == At(10)
              && renamedInPlace.Renamings.SequenceEqual([new("G", "G2", At(10))])
              && renamedInPlace.Changes["local_b"] == new Changes(Group: new("G2", At(10))),
            "a group renamed in place (same id) is a list change that carries the renaming");
        Check(ClaudeSessionSync.Observe(State(records), null, At(10)).Groups is { Order: ["G", "H"] } seeded && seeded.At == At(10)
              && ClaudeSessionSync.Observe(State(records, Groups([])), null, At(10)).Groups is null,
            "without a checkpoint a list that has groups is given; an empty one gives nothing");

        // Titles of the widget's copies.
        var copy = new[] { R("local_x", "새 원본 이름", false, "x") };
        var written = PointOf([R("local_x", "원본 이름", false, "x")], layout, At(0), 1, Copies(("local_x", "원본 이름")));
        Check(ClaudeSessionSync.Observe(State(copy, copies: Copies(("local_x", "새 원본 이름"))), written, At(10)).Changes.Count == 0,
            "a copy's title the widget wrote since the checkpoint, following its original, is no change");
        var renamedByUser = PointOf([R("local_x", "사용자 이름", false, "x")], layout, At(0), 1, Copies(("local_x", "원본 이름")));
        Check(ClaudeSessionSync.Observe(State([R("local_x", "원본 이름", false, "x")], copies: Copies(("local_x", "원본 이름"))), renamedByUser, At(10))
                  .Changes["local_x"] == new Changes(Title: new("원본 이름", At(10))),
            "a copy renamed back to the title the widget wrote (A → B → A) is the user's change");

        // Evidence from the first format.
        var shown = new[] { R("local_y", "와이", false, "y") };
        var evidence = new Dictionary<string, ClaudeSessionLayout.Evidence> { ["local_y"] = new(true, true) };
        Check(ClaudeSessionSync.Observe(State(shown, copies: Copies(("local_y", "와이"))), null, At(10), null, evidence).Changes["local_y"]
                  == new Changes(new(false, At(10)), new(null, At(10)))
              && !ClaudeSessionSync.Observe(State(shown, copies: Copies(("local_y", "와이"))), null, At(10)).Changes.ContainsKey("local_y"),
            "a copy the first format showed with a pin and a group gives its no pin and no group; without that evidence it gives nothing");
    }

    /// <summary>The widget's checkpoint, read from the stores: when it can be taken, how it dates what changed, and what it
    /// keeps of a plan in progress and of the first format.</summary>
    private static void TestCheckpoint()
    {
        using var world = new SessionWorld("checkpoint");
        var layout = new ClaudeSessionLayout();
        layout.Accounts[K1] = new("계정1", At(0));
        world.Record(K1, "local_a", "가", Cli(1));
        world.Record(K1, "local_b", "나", Cli(2));
        world.Record(K1, "local_c", "다", Cli(3));
        world.Copied(K1, "local_c", "다");
        world.Record(K1, "local_x", "엑스", Cli(5));
        world.Record(K2, "local_z", "제트", Cli(4));
        world.Pin("local_a");
        world.Pin("local_z");
        world.Put(K1, "local_b", world.Group(K1, "G"));
        world.Flush();
        bool Take(DateTimeOffset now, ClaudeSessionLayout? into = null, string? account = null) =>
            ClaudeSessionSync.Checkpoint(into ?? layout, world.Sources, account ?? K1, now);
        Check(!Take(At(5), account: K3), "an account without a list here is not checkpointed");
        Check(!ClaudeSessionSync.Checkpoint(layout, world.Sources with { IndexedDb = Path.Combine(world.Root, "no-idb") }, K1, At(5))
              && !ClaudeSessionSync.Checkpoint(layout, world.Sources with { LocalStorage = Path.Combine(world.Root, "no-ls") }, K1, At(5))
              && layout.Checkpoints.Count == 0, "without the pin list or the sidebar nothing is checkpointed");
        using (new FileStream(world.RecordPath(K1, "local_c"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(!Take(At(5)), "nor while a record cannot be read: it would look gone");
        File.WriteAllText(Path.Combine(world.Ledger, "ledger.json"), "{\"created\":");
        Check(!Take(At(5)), "nor while the ledger cannot be read: a copy would pass for a choice");
        world.Copied(K1, "local_c", "다");
        Check(Take(At(5)) && layout.Checkpoints[K1] is { Stay: 0 } first && first.At == At(5)
              && first.Groups!.Select(group => group.Name).SequenceEqual(["G"])
              && layout.Changed[K1]["local_a"] == new Changes(new(true, At(0)), new(null, At(0)), new("가", At(0)), new("fable", At(0)))
              && layout.Changed[K1]["local_b"] == new Changes(new(false, At(0)), new("G", At(0)), new("나", At(0)), new("fable", At(0)))
              && !layout.Changed[K1].ContainsKey("local_c") && layout.GroupsChanged[K1] is { Order: ["G"] } seeded && seeded.At == At(0),
            "a first checkpoint seeds what each record gives (a copy only what was chosen there), dated when the app last left the account");
        world.Retitle(K1, "local_a", "가가");
        Check(Take(At(7)) && layout.Changed[K1]["local_a"].Title == new ClaudeSessionLayout.Stamp<string>("가가", At(7)),
            "a checkpoint in the same stay dates what changed since to now");
        world.Pin("local_a", false);
        world.Flush();
        layout.Accounts[K1] = layout.Accounts[K1] with { LeftAt = At(9), ArrivedAt = At(20), Stay = 1 };
        Check(Take(At(21)) && layout.Changed[K1]["local_a"].Pinned == new ClaudeSessionLayout.Stamp<bool>(false, At(9))
              && layout.Checkpoints[K1].Stay == 1,
            "the first checkpoint of a stay dates what changed since the last one to when the app left the account");
        // The app's one pin list for every account loses every pin at once (an app update did).
        world.Pin("local_b");
        world.Flush();
        Check(Take(At(22)) && layout.Changed[K1]["local_b"].Pinned == new ClaudeSessionLayout.Stamp<bool>(true, At(22)), "a pin is a change");
        world.ClearPins();
        world.Flush();
        Check(Take(At(23)) && layout.Changed[K1]["local_b"].Pinned == new ClaudeSessionLayout.Stamp<bool>(true, At(22))
              && layout.Checkpoints[K1].Sessions["local_b"].Pinned == true,
            "while the one pin list is empty though a checkpoint showed pins, no unpinning is taken and the checkpoint keeps its pins");
        world.Pin("local_b"); world.Pin("local_z");
        world.Flush();
        // An archived session keeps its changes for its conversation; one whose record is gone for good loses them.
        world.Archive(K1, "local_a");
        File.Delete(world.RecordPath(K1, "local_b"));
        Check(Take(At(24)) && layout.Changed[K1].ContainsKey("local_a") && !layout.Changed[K1].ContainsKey("local_b"),
            "an archived session keeps its changes; one whose record is gone loses them");
        // A group renamed in place is a renaming, kept through later changes of the list.
        world.RenameGroup(K1, world.GroupId(K1, "G"), "H");
        world.Flush();
        Check(Take(At(25)) && layout.GroupsChanged[K1] is { Order: ["H"] } renamed && renamed.Renamed["G"] == "H",
            "a group renamed in place (same id) is a renaming");
        world.Group(K1, "K");
        world.Flush();
        Check(Take(At(26)) && layout.GroupsChanged[K1] is { Order: ["H", "K"] } longer && longer.Renamed["G"] == "H" && longer.At == At(26),
            "a renaming stays known through later changes of the list");
        // A plan in progress: what it has not reached stays; reached, it counts as the change it came from (newer than what
        // this account recorded for it, or the plan would not have set out to give it) and is done.
        layout.Pendings[K1] = new(1, new Dictionary<string, Target> { ["local_x"] = new(Title: new("새 이름", At(1), K2)) }, null, Aside());
        Check(Take(At(28)) && layout.Pendings[K1].Sessions["local_x"].Title is { Value: "새 이름" },
            "a plan of this stay stays in progress with what it has not reached yet");
        world.Retitle(K1, "local_x", "새 이름");
        Check(Take(At(29)) && !layout.Pendings.ContainsKey(K1)
              && layout.Changed[K1]["local_x"].Title == new ClaudeSessionLayout.Stamp<string>("새 이름", At(1), K2),
            "reached, it counts as the change it came from, and the plan is done");
        var h = world.GroupId(K1, "H");
        layout.Pendings[K1] = new(1, new Dictionary<string, Target> { ["local_x"] = new(Title: new("또", At(2), K2)) }, ListChange(["K", "H"], At(2), K2),
            Aside((h, "H")));
        world.RenameGroup(K1, h, "H (옮기는 중)");
        world.Flush();
        layout.Accounts[K1] = layout.Accounts[K1] with { LeftAt = At(30), ArrivedAt = At(40), Stay = 2 };
        Check(Take(At(41)) && layout.Pendings[K1] is { Sessions.Count: 0, Groups: not null } carried && carried.Renamed[h].From == "H"
              && layout.Checkpoints[K1].Groups!.Select(group => group.Name).SequenceEqual(["H", "K"]),
            "a plan of an earlier stay ends, but a group it renamed out of the way stays known while it is there, and the list mid-move is not taken");
        world.DeleteGroup(K1, h);
        world.Flush();
        Check(Take(At(42)) && !layout.Pendings.ContainsKey(K1), "once that group is gone, so is the plan");

        // Evidence from the first format is spent by the first checkpoint, or kept for pins while the pin list is lost.
        world.Record(K3, "local_e", "이", Cli(6));
        world.Copied(K3, "local_e", "이");
        world.Flush();
        var fresh = new ClaudeSessionLayout();
        fresh.Accounts[K3] = new("계정3", At(0));
        fresh.Evidences[K3] = new(StringComparer.Ordinal) { ["local_e"] = new(true, true) };
        Check(Take(At(5), fresh, K3) && fresh.Changed[K3]["local_e"] == new Changes(new(false, At(0)), new(null, At(0)))
              && !fresh.Evidences.ContainsKey(K3),
            "a copy the first format showed with a pin and a group gives its no pin and no group, and the evidence is spent");
        var wiped = new ClaudeSessionLayout();
        wiped.Accounts[K3] = new("계정3", At(0));
        wiped.Evidences[K3] = new(StringComparer.Ordinal) { ["local_e"] = new(true, true) };
        wiped.Checkpoints[K2] = PointOf([R("local_z", "제트", true, Cli(4))], null, At(-5));
        world.ClearPins();
        world.Flush();
        Check(Take(At(5), wiped, K3) && wiped.Evidences[K3]["local_e"] == new ClaudeSessionLayout.Evidence(true, false),
            "while the pin list is lost, the pin evidence waits for the next checkpoint");
    }

    /// <summary>The 10/20 failure, step by step through the widget: 계정3 took 계정1's pin and group in a plan, then the user
    /// undid both there; 계정2, planned next, follows 계정3 (with per-pair agreements 계정1 won), and so does 계정1.</summary>
    private static void TestUndoneAfterPlan()
    {
        var (world, widget) = Accounts3("aba", ("p", "P"), ("q", "Q"));
        using var _ = world;
        widget.Start(K2, At(-60)); widget.Switch(K3, At(-50)); widget.Switch(K1, At(-40));
        world.Pin("local_p1");
        world.Put(K1, "local_q1", world.Group(K1, "G"));
        widget.Switch(K3, At(10));
        var plan = widget.Plan(At(15));
        Check(plan is { Checkpointed: true } && plan.Pins!.Single() is { Session: "local_p3", Pinned: true, By: "계정1" }
              && plan.CreateGroups!.SequenceEqual(["G"]) && plan.Moves!.Single() is { Group: "G" } toG && toG.Sessions.SequenceEqual(["local_q3"])
              && plan.GroupOrderBy == "계정1", "계정3 takes what 계정1 changed");
        world.Apply(K3, plan);
        Check(widget.Plan(At(16)) is { Checkpointed: true, Pins.Count: 0, Moves.Count: 0, CreateGroups.Count: 0, GroupOrder: null },
            "planned again once applied, nothing is left to do");
        Check(widget.Layout.Changed[K3]["local_p3"].Pinned == new ClaudeSessionLayout.Stamp<bool>(true, At(10), K1)
              && widget.Layout.Changed[K3]["local_q3"].Group == new ClaudeSessionLayout.Stamp<string?>("G", At(10), K1)
              && widget.Layout.GroupsChanged[K3] is { At: var listedAt, By: var listedBy } && listedAt == At(10) && listedBy == K1,
            "what a plan brought in keeps the time and the account of the change it came from, not the time it was applied");
        world.Pin("local_p3", false);
        world.Put(K3, "local_q3", null);
        world.DeleteGroup(K3, world.GroupId(K3, "G"));
        widget.Switch(K2, At(40));
        Check(widget.Plan(At(45)) is { Checkpointed: true, Pins.Count: 0, Moves.Count: 0, CreateGroups.Count: 0, GroupOrder: null },
            "계정2 follows what the user did last, in 계정3: P stays unpinned, Q in no group, and G is not made");
        widget.Switch(K1, At(60));
        var back = widget.Plan(At(65));
        Check(back.Pins!.Single() is { Session: "local_p1", Pinned: false, By: "계정3" }
              && back.Moves!.Single() is { Group: null } outOf && outOf.Sessions.SequenceEqual(["local_q1"])
              && back.DeleteGroups!.Single() is { Name: "G (옮기는 중)", Remade: false } && back.GroupOrder is { Count: 0 }
              && back.GroupOrderBy == "계정3", "계정1, where it began, follows too, and its G goes");
        world.Apply(K1, back);
        Check(world.Shows(K1) == world.Shows(K3) && world.Shows(K2) == world.Shows(K3), "the three accounts look alike");
        // A plan asked for in an account the app does not show is for viewing only: nothing is checkpointed or kept.
        var shown = widget.Layout.Checkpoints[K3];
        var pending = widget.Layout.Pendings.GetValueOrDefault(K3);
        var view = widget.Plan(At(70), K3);
        Check(view is { Status: "ok", Checkpointed: false } && ReferenceEquals(widget.Layout.Checkpoints[K3], shown)
              && ReferenceEquals(widget.Layout.Pendings.GetValueOrDefault(K3), pending),
            "a plan for an account the app does not show is for viewing only: nothing is checkpointed or kept");
    }

    /// <summary>An account the app only passed through overwrites nothing, and what the user changes in a stay, before or
    /// after a plan, stays.</summary>
    private static void TestPassedThrough()
    {
        var (world, widget) = Accounts3("passed", ("p", "P"));
        using var _ = world;
        widget.Start(K1, At(-60)); widget.Switch(K2, At(-50)); widget.Switch(K3, At(-40)); widget.Switch(K1, At(-30));
        world.Pin("local_p1");
        widget.Switch(K2, At(0));
        widget.Switch(K3, At(10));
        var plan = widget.Plan(At(15));
        Check(plan.Pins!.Single() is { Session: "local_p3", Pinned: true, By: "계정1" },
            "an account only passed through, though left later, overwrites nothing: its stale value is no change");
        world.Apply(K3, plan);
        widget.Plan(At(16));
        world.Pin("local_p3", false);
        Check(widget.Plan(At(20)) is { Pins.Count: 0 }, "what the user changed after a plan in the same stay stays at the next plan");
        widget.Switch(K1, At(30));
        world.Put(K1, "local_p1", world.Group(K1, "H"));
        Check(widget.Plan(At(35)) is { Pins.Count: 0, Moves.Count: 0, CreateGroups.Count: 0, RenameGroups.Count: 0, DeleteGroups.Count: 0 },
            "what the user changed in this stay before planning stays, and the group they filed it under stays on the list");
    }

    /// <summary>R1/R2: a plan stopped part way. The move into a group unpinned the session before it was pinned again; that
    /// is nobody's choice, neither here nor seen from another account, in this stay or the next.</summary>
    private static void TestPartlyApplied()
    {
        var (world, widget) = Accounts3("partly", ("p", "P"));
        using var _ = world;
        widget.Start(K2, At(-60)); widget.Switch(K3, At(-50)); widget.Switch(K1, At(-40));
        world.Put(K1, "local_p1", world.Group(K1, "H"));
        world.Pin("local_p1");
        widget.Switch(K3, At(0));
        var plan = widget.Plan(At(5));
        Check(plan.Moves!.Single() is { Group: "H" } && plan.Pins!.Single() is { Session: "local_p3", Pinned: true }, "계정3 is to file P under H and pin it");
        // Stopped after the move.
        world.Put(K3, "local_p3", world.Group(K3, "H"));
        widget.Switch(K2, At(10));
        var there = widget.Plan(At(15));
        Check(there.Pins!.Single() is { Session: "local_p2", Pinned: true, By: "계정1" } && there.Moves!.Single() is { Group: "H" },
            "a plan stopped part way in 계정3 does not spread: 계정2 still gets the pin the move undid there");
        widget.Switch(K3, At(20));
        var again = widget.Plan(At(25));
        Check(again.Pins!.Single() is { Session: "local_p3", Pinned: true } && again.Moves!.Count == 0
              && widget.Layout.Changed[K3]["local_p3"].Pinned is null or { Value: true },
            "back in 계정3, in a new stay, the pin its move undid is pinned again and was never taken for the user's unpinning");
        world.Apply(K3, again);
        Check(widget.Plan(At(26)) is { Pins.Count: 0 } && widget.Layout.Changed[K3]["local_p3"].Pinned == new ClaudeSessionLayout.Stamp<bool>(true, At(0), K1),
            "confirmed, the pin counts as the change it came from");
        world.Pin("local_p3", false);
        widget.Plan(At(27));
        widget.Switch(K2, At(30));
        Check(widget.Plan(At(35)).Pins!.Count == 0 && widget.Layout.Changed[K3]["local_p3"].Pinned is { Value: false },
            "once the pin was reached, the user's unpinning afterwards is theirs and reaches the next account");
    }

    /// <summary>R2: sorting the groups stopped part way, then a new stay: the group renamed out of the way is still told by
    /// its id, and the plan carries the sorting on.</summary>
    private static void TestSortingResumed()
    {
        var (world, widget) = Accounts3("resume", ("p", "P"), ("q", "Q"));
        using var _ = world;
        foreach (var account in new[] { K1, K3 }) { world.Group(account, "B"); world.Group(account, "A"); }
        world.Put(K1, "local_p1", world.GroupId(K1, "B"));
        world.Put(K3, "local_p3", world.GroupId(K3, "B"));
        widget.Start(K2, At(-60)); widget.Switch(K3, At(-50)); widget.Switch(K1, At(-40));
        world.Reorder(K1, "A", "B");
        widget.Switch(K3, At(0));
        var plan = widget.Plan(At(5));
        var oldB = world.GroupId(K3, "B");
        Check(plan.RenameGroups!.Single() is { From: "B", To: "B (옮기는 중)" } && plan.CreateGroups!.SequenceEqual(["B"])
              && plan.Moves!.Single() is { Group: "B", GroupId: null } && plan.DeleteGroups!.Single() is { Remade: true },
            "B steps aside and is made anew after A");
        // Stopped after making the new B: P is still in the old one.
        world.RenameGroup(K3, oldB, "B (옮기는 중)");
        world.Group(K3, "B");
        widget.Switch(K2, At(10));
        widget.Switch(K3, At(20));
        var resumed = widget.Plan(At(25));
        Check(resumed.RenameGroups!.Count == 0 && resumed.CreateGroups!.Count == 0
              && resumed.Moves!.Single() is { Group: "B", GroupId: { } into } carry && into != oldB && carry.Sessions.SequenceEqual(["local_p3"])
              && resumed.DeleteGroups!.Single() is { Id: var gone, Remade: true } && gone == oldB,
            "in a later stay the sorting carries on: P goes to the new B and the old one goes");
        world.Apply(K3, resumed);
        Check(world.Shows(K3) == world.Shows(K1), "and 계정3 shows what 계정1 shows");
    }

    /// <summary>11/7: Claude 4.23118 moves no archived session. 계정1 put A after B; in 계정3 A is made anew at the end, and
    /// what the lists show moves there. The archived session filed under the old A stays out of the moves and leaves the
    /// group as it goes, so nothing is left aside.</summary>
    private static void TestArchivedInRemadeGroup()
    {
        var (world, widget) = Accounts3("archived-remade", ("p", "P"), ("q", "Q"));
        using var _ = world;
        foreach (var account in new[] { K1, K3 }) { world.Group(account, "A"); world.Group(account, "B"); }
        world.Put(K1, "local_p1", world.GroupId(K1, "A"));
        world.Put(K3, "local_p3", world.GroupId(K3, "A"));
        world.Put(K3, "local_q3", world.GroupId(K3, "A"));
        foreach (var (account, suffix) in new[] { (K1, "1"), (K2, "2"), (K3, "3") }) world.Archive(account, "local_q" + suffix);
        widget.Start(K2, At(-60)); widget.Switch(K3, At(-50)); widget.Switch(K1, At(-40));
        world.Reorder(K1, "B", "A");
        widget.Switch(K3, At(0));
        var plan = widget.Plan(At(5));
        var oldA = world.GroupId(K3, "A");
        Check(plan.CreateGroups!.SequenceEqual(["A"]) && plan.Moves!.Single() is { Group: "A" } move && move.Sessions.SequenceEqual(["local_p3"])
              && plan.DeleteGroups!.Single() is { Remade: true, Hidden: 1 } gone && gone.Id == oldA,
            "A is made anew after B; P moves, the archived Q is counted, not moved");
        world.Apply(K3, plan);
        Check(world.Shows(K3) == world.Shows(K1) && world.Of(K3).Groups.All(group => group.Id != oldA)
              && widget.Plan(At(6)) is { Moves.Count: 0, DeleteGroups.Count: 0, RenameGroups.Count: 0 },
            "applied, the old A is gone with nothing left aside, and nothing is left to do");
    }

    /// <summary>A conversation another account archived after anyone last talked in it goes here too: 「일요일 고성 이동
    /// 계획」 stayed pinned in 계정3 after 계정2 had archived it (11/8). It is listed for archiving, ready once pins are
    /// read, and planned no further. One talked in since, one still live where it was archived, an older line of one that
    /// went on here, a branch, the calling session and a session another one is nested under stay.</summary>
    private static void TestArchivedElsewhere()
    {
        long Ms(double minutes) => At(0).AddMinutes(minutes).ToUnixTimeMilliseconds();
        Record Shelved(string id, string cli, double active, double archivedAt, string[]? prior = null, bool branch = false) =>
            R(id, id, false, cli, prior, Ms(active), branch) with { Written = At(0).AddMinutes(archivedAt) };
        var here = new[]
        {
            R("local_x", "고성", true, "x", activity: Ms(10)),
            R("local_y", "다시 씀", false, "y", activity: Ms(30)),
            R("local_w", "둘 다", false, "w", activity: Ms(10)),
            R("local_v", "한쪽만", false, "v", activity: Ms(10)),
            R("local_c", "여기서 이음", false, "c2", ["c1"], Ms(10)),
            R("local_d", "거기서 이음", false, "d1", activity: Ms(10)),
            R("local_e", "곁가지 보관", false, "e1", activity: Ms(10)),
            R("local_s", "지금", false, "s", activity: Ms(10)),
            R("local_p", "부모", false, "p", activity: Ms(10)),
            R("local_n", "곁가지", false, "n", activity: Ms(10)) with { Parent = "local_p" }
        };
        OtherAccount[] Others() =>
        [
            Other(ReferenceKey, "계정1", [R("local_w1", "둘 다", false, "w", activity: Ms(10)), R("local_v1", "한쪽만", false, "v", activity: Ms(10))],
                null, At(40), archived:
                [
                    Shelved("local_x1", "x", 12, 20), Shelved("local_y1", "y", 5, 20), Shelved("local_w2", "w", 10, 20),
                    Shelved("local_c1", "c1", 8, 20), Shelved("local_s1", "s", 10, 20), Shelved("local_p1", "p", 10, 20)
                ]),
            Other(ThirdKey, "계정3", [], null, At(50), archived:
                [Shelved("local_v3", "v", 10, 25), Shelved("local_d3", "d2", 15, 25, ["d1"]), Shelved("local_e3", "f", 10, 25, ["e1"], branch: true)])
        ];
        var self = new ClaudeSessionSync.Lineage("local_s", _ => null, (_, _) => null);
        var plan = Compute(here, null, Others(), lineage: self);
        var items = plan.Archive!.ToDictionary(item => item.Session, StringComparer.Ordinal);
        Check(items.Keys.Order(StringComparer.Ordinal).SequenceEqual(["local_d", "local_v", "local_x"])
              && items["local_x"] is { ArchivedIn: "계정1", Newest: null, MovedOn: null, Pinned: true, Ready: true } x
              && x.ArchivedAt == At(20) && x.LastWritten == At(12)
              && items["local_d"] is { ArchivedIn: "계정3", Ready: true } d && d.ArchivedAt == At(25) && d.LastWritten == At(15)
              && items["local_v"] is { ArchivedIn: "계정3", Ready: true },
            "archived elsewhere after its last activity anywhere, the same transcript or one continued from it, it goes here too, "
            + "though an account that never archived it still lists it");
        Check(plan.Pins!.All(pin => pin.Session != "local_x") && plan.OnlyHere is { } onlyHere && !onlyHere.Contains("고성")
              && !onlyHere.Contains("거기서 이음") && onlyHere.Contains("다시 씀")
              && plan.Pending!.Sessions["local_x"] is { Pinned: { Value: false } unpin, Group: null } && unpin.At == DateTimeOffset.MinValue,
            "it is planned no further, and unpinning it before archiving is nobody's choice");
        Check(Compute(here, null, Others(), lineage: self, pinsKnown: false).Archive!.Single(item => item.Session == "local_x") is { Ready: false, Pinned: false }
              && Compute(here, null, Others(), lineage: self, listsWhole: false).Archive!.Count == 0,
            "it waits while pins cannot be read, and none goes while a list cannot be read whole");

        // A group it leaves goes without moving it: it is counted with the archived ones, and leaving is nobody's choice.
        var grouped = new[] { R("local_x", "고성", false, "x", activity: Ms(10)), R("local_y", "다시 씀", false, "y", activity: Ms(30)) };
        var groupsHere = Groups(["g=G"], ("local_x", "g"), ("local_y", "g"));
        var lists = new ClaudeSessionLayout();
        Steady(lists, CurrentKey, "계정2", grouped, groupsHere, At(0));
        lists.GroupsChanged[ThirdKey] = ListChange(["I"], At(20));
        var regroup = Compute(grouped, groupsHere,
            [Other(ReferenceKey, "계정1", [], null, At(10), archived: [Shelved("local_x1", "x", 12, 20)]), Other(ThirdKey, "계정3", [], null, At(20))], lists);
        Check(regroup.Moves!.Single() is { Group: null } outOf && outOf.Sessions.SequenceEqual(["local_y"])
              && regroup.DeleteGroups!.Single() is { Id: "g", Hidden: 1, Remade: false }
              && regroup.Pending!.Sessions["local_x"] is { Group: { Value: null } left, Pinned: null } && left.At == DateTimeOffset.MinValue
              && regroup.Archive!.Single() is { Session: "local_x", Ready: true },
            "the group it leaves is deleted once the others are moved, counting it with the archived ones");

        // Its older state here goes with it.
        var origin = At(0);
        ClaudeSessionSync.TranscriptSpan Span(double created, double last) =>
            new(origin.AddHours(created), origin.AddHours(last), origin.AddHours(last));
        var spans = new Dictionary<string, ClaudeSessionSync.TranscriptSpan> { ["a1"] = Span(0, 1), ["a2"] = Span(2, 24) };
        var lineage = new ClaudeSessionSync.Lineage("local_s", cli => spans.TryGetValue(cli, out var span) ? span : null,
            (_, next) => spans.TryGetValue(next, out var span) ? span.Created : null);
        var pair = new[] { R("local_old", "대화", true, "a1", [], Ms(60)), R("local_new", "대화", false, "a2", ["a1"], Ms(24 * 60)) };
        Check(Compute(pair, null, [Other(ReferenceKey, "계정1", [], null, At(40), archived: [Shelved("local_n1", "a2", 24 * 60, 25 * 60, ["a1"])])],
                  lineage: lineage).Archive is { Count: 2 } both
              && both.All(item => item is { ArchivedIn: "계정1", Newest: null, Ready: true })
              && both.Single(item => item.Session == "local_old") is { Pinned: true } old && old.LastWritten == origin.AddHours(1),
            "its older state here goes with it");
        Check(Compute(pair, null, [Other(ReferenceKey, "계정1", [], null, At(40))], lineage: lineage).Archive!.Single() is
                { Session: "local_old", Newest: "local_new", ArchivedIn: null },
            "without that archiving, the older state is an older state as before");
    }

    /// <summary>The user's expectation of 10/20 on disk: 계정3, used last, put its groups in a new order and moved a session;
    /// planned and applied in 계정2, the sidebar looks like 계정3's, and planning again finds nothing to do.</summary>
    private static void TestSortingOnDisk()
    {
        using var world = new SessionWorld("sorting");
        var widget = new Widget(world);
        string[] tiers = ["티어 2", "티어 3 (보관)", "스킬 생태계", "태림 오리지널 콘텐츠", "도구"];
        string[] before = ["티어 2", "도구", "스킬 생태계", "티어 3 (보관)", "태림 오리지널 콘텐츠"];
        string[] titles = ["하나", "둘", "셋", "넷", "다섯"];
        string[] homes = ["티어 2", "도구", "스킬 생태계", "티어 3 (보관)", "티어 2"];
        foreach (var (account, label, prefix) in new[] { (K2, "계정2", "local_c"), (K3, "계정3", "local_t") })
        {
            widget.Map(account, label);
            foreach (var name in before) world.Group(account, name);
            for (var index = 0; index < titles.Length; index++)
            {
                var id = prefix + (index + 1).ToString(CultureInfo.InvariantCulture);
                world.Record(account, id, titles[index], Cli(index + 1));
                world.Put(account, id, world.GroupId(account, homes[index]));
            }
            world.Pin(prefix + "2");
        }
        widget.Start(K2, At(-60));
        widget.Switch(K3, At(-50));
        world.Reorder(K3, tiers);
        world.Put(K3, "local_t5", world.GroupId(K3, "티어 3 (보관)"));
        widget.Switch(K2, At(0));
        var plan = widget.Plan(At(5));
        Check(plan is { Status: "ok", Checkpointed: true, GroupOrderBy: "계정3" } && plan.GroupOrder!.SequenceEqual(tiers),
            "the plan follows 계정3's group list, the newest");
        world.Apply(K2, plan);
        Check(world.Shows(K2) == world.Shows(K3),
            "applied, this account shows what 계정3 shows: the same pins, the groups in the same order with the same sessions");
        Check(widget.Plan(At(6)) is { RenameGroups.Count: 0, CreateGroups.Count: 0, DeleteGroups.Count: 0, Moves.Count: 0, Pins.Count: 0, Renames.Count: 0 },
            "a plan made again once it is applied finds nothing left to do");
    }

    /// <summary>R1: the last pin of one account is a value like any other; only the whole pin list emptied at once is taken
    /// for lost, and then the pins are planned back from the changes.</summary>
    private static void TestLastPin()
    {
        var (world, widget) = Accounts3("pins", ("p", "P"), ("y", "Y"));
        using var _ = world;
        world.Pin("local_p1"); world.Pin("local_p2"); world.Pin("local_p3"); world.Pin("local_y2");
        widget.Start(K2, At(-60)); widget.Switch(K3, At(-50)); widget.Switch(K1, At(-40));
        world.Pin("local_p1", false);
        widget.Switch(K2, At(0));
        var plan = widget.Plan(At(5));
        Check(plan.Pins!.Single() is { Session: "local_p2", Pinned: false, By: "계정1" } && plan.Warnings is null,
            "an account's last pin, unpinned, is a change like any other and reaches the others");
        world.Apply(K2, plan);
        widget.Plan(At(6));
        world.ClearPins();
        var lost = widget.Plan(At(10));
        Check(lost.Warnings!.Contains("pins_lost") && lost.Pins!.Select(pin => (pin.Session, pin.Pinned)).SequenceEqual([("local_y2", true)]),
            "the one pin list emptied at once is taken as lost: the pins are planned back from the changes, nothing unpinned");
    }

    /// <summary>R1: upgrading from the first format, a copy that had received a pin and a group (its agreement says so) and
    /// was since unpinned and taken out of the group gives that, rather than letting an older pin and group come back.</summary>
    private static void TestFirstFormatEvidence()
    {
        using var world = new SessionWorld("evidence");
        foreach (var (account, id) in new[] { (K1, "local_p1"), (K2, "local_p2"), (K3, "local_p3") })
        {
            world.Record(account, id, "P", Cli(1));
            if (account != K1) world.Copied(account, id, "P");
        }
        world.Put(K1, "local_p1", world.Group(K1, "G"));
        world.Pin("local_p1");
        // 계정3 still has G; the user took P out of it there.
        world.Group(K3, "G");
        world.Flush();
        string Entry(string key, string label, DateTimeOffset? leftAt) =>
            "\"" + key + "\":{\"label\":\"" + label + "\",\"leftAt\":" + (leftAt is { } left ? "\"" + left.ToString("O", CultureInfo.InvariantCulture) + "\"" : "null") + "}";
        string FirstFormat(bool agreed) =>
            "{\"version\":1,\"accounts\":{" + Entry(K1, "계정1", At(0)) + "," + Entry(K2, "계정2", null) + "," + Entry(K3, "계정3", At(10)) + "},\"pairs\":{"
            + (agreed ? "\"" + K1 + "|" + K3 + "\":{\"" + K3 + "\":{\"local_p3\":{\"pinned\":true,\"group\":\"G\",\"title\":\"P\"}}}" : "") + "}}";
        File.WriteAllText(Path.Combine(world.Ledger, ClaudeSessionLayout.FileName), FirstFormat(agreed: true));
        Check(ClaudeSessionSync.Build(world.Sources, "local_p2") is { Status: "ok", Pins.Count: 0, Moves.Count: 0 },
            "the copy in 계정3, left last, gives its no pin and no group: the first format shows it had received them");
        File.WriteAllText(Path.Combine(world.Ledger, ClaudeSessionLayout.FileName), FirstFormat(agreed: false));
        Check(ClaudeSessionSync.Build(world.Sources, "local_p2") is { Status: "ok" } without
              && without.Pins!.Single() is { Session: "local_p2", Pinned: true } && without.Moves!.Single() is { Group: "G" },
            "without that evidence a copy's defaults give nothing, and 계정1's older pin and group come");
    }

    /// <summary>R4: an older state moved with its group (G, renamed H in another account) is the plan's doing, not the
    /// user's: planned again once applied, the newest state stays in the group the user chose for it.</summary>
    private static void TestOlderStateFollowsRename()
    {
        using var world = new SessionWorld("older-move");
        var widget = new Widget(world);
        widget.Map(K1, "계정1");
        widget.Map(K2, "계정2");
        const string First = "30000000-0000-0000-0000-000000000001", Second = "30000000-0000-0000-0000-000000000002";
        var r = world.Transcript("2026-10-01T00:00:00Z", First + "@2026-10-01T00:00:10Z");
        var n = world.Transcript("2026-10-01T02:00:00Z", First + "@2026-10-01T00:00:10Z", Second + "@2026-10-01T02:00:00Z");
        world.Record(K2, "local_r", "대화", r, activity: 10);
        world.Record(K2, "local_n", "대화", n, [r], activity: 20);
        world.Record(K1, "local_k", "다른 것", Cli(9));
        foreach (var account in new[] { K1, K2 }) { world.Group(account, "J"); world.Group(account, "G"); }
        world.Put(K2, "local_r", world.GroupId(K2, "G"));
        world.Put(K2, "local_n", world.GroupId(K2, "J"));
        widget.Start(K2, At(-60));
        widget.Switch(K1, At(-50));
        world.RenameGroup(K1, world.GroupId(K1, "G"), "H");
        widget.Switch(K2, At(0));
        var plan = widget.Plan(At(5));
        Check(plan.GroupOrder!.SequenceEqual(["J", "H"]) && plan.Moves!.Single() is { Group: "H" } toH && toH.Sessions.SequenceEqual(["local_r"])
              && plan.Archive!.Single() is { Session: "local_r", Newest: "local_n" }
              && plan.Pending!.Sessions["local_r"].Group is { Value: "H" } move && move.At == DateTimeOffset.MinValue,
            "the older state follows its group to H, a move that is nobody's choice");
        world.Apply(K2, plan);
        var confirm = widget.Plan(At(6));
        Check(confirm is { Moves.Count: 0, Pins.Count: 0, CreateGroups.Count: 0 } && confirm.Archive!.Single() is { Session: "local_r", Ready: true }
              && !widget.Layout.Changed[K2].TryGetValue("local_r", out var olderChange) | olderChange?.Group is not { Value: "H" },
            "planned again, the newest state stays in J, where the user put it, and the older state is ready to go");
    }

    /// <summary>An older state the app opened again gets a notice that a background task ended and a stand-in reply
    /// appended: that is not the user going on with it, so it is still an older state. A turn of the user's is.</summary>
    private static void TestReopenedOlderState()
    {
        using var world = new SessionWorld("reopened");
        const string First = "40000000-0000-0000-0000-000000000001", Second = "40000000-0000-0000-0000-000000000002";
        var r = world.Transcript("2026-10-01T00:00:00Z", First + "@2026-10-01T00:00:10Z",
            "{\"type\":\"user\",\"uuid\":\"40000000-0000-0000-0000-000000000003\",\"timestamp\":\"2026-10-03T00:00:00Z\",\"origin\":{\"kind\":\"task-notification\"}}",
            "{\"type\":\"assistant\",\"uuid\":\"40000000-0000-0000-0000-000000000004\",\"timestamp\":\"2026-10-03T00:00:01Z\",\"message\":{\"model\":\"<synthetic>\"}}");
        var n = world.Transcript("2026-10-01T02:00:00Z", First + "@2026-10-01T00:00:10Z", Second + "@2026-10-01T02:00:00Z");
        world.Record(K2, "local_r", "대화", r, activity: 10);
        world.Record(K2, "local_n", "대화", n, [r], activity: 20);
        world.Record(K1, "local_k", "다른 것", Cli(9));
        world.Flush();
        Keep(world.Ledger, (K2, "계정2", null), (K1, "계정1", At(0)));
        Check(ClaudeSessionSync.Build(world.Sources, "local_n").Archive!.Single() is { Session: "local_r", Newest: "local_n" },
            "an older state the app opened again, with a background task's notice and a stand-in reply appended, is still an older state");
        File.AppendAllLines(Path.Combine(world.Projects, "E--work", r + ".jsonl"),
            ["{\"type\":\"user\",\"uuid\":\"40000000-0000-0000-0000-000000000005\",\"timestamp\":\"2026-10-03T00:10:00Z\"}"]);
        Check(ClaudeSessionSync.Build(world.Sources, "local_n").Archive!.Count == 0, "one the user went on with is a branch and stays");
    }

    /// <summary>R5: a plan set G and K aside to make them anew in order; only K was set aside when the user renamed the
    /// empty G to X. Planned again, X stays (G is not made again) and K's move carries on.</summary>
    private static void TestRenamedWhileAside()
    {
        using var world = new SessionWorld("renamed-aside");
        var widget = new Widget(world);
        widget.Map(K1, "계정1");
        widget.Map(K3, "계정3");
        foreach (var (account, id) in new[] { (K1, "local_k1"), (K3, "local_k3") })
        {
            world.Record(account, id, "케이", Cli(1));
            foreach (var name in new[] { "G", "K", "A" }) world.Group(account, name);
            world.Put(account, id, world.GroupId(account, "K"));
        }
        world.Copied(K3, "local_k3", "케이");
        widget.Start(K3, At(-60));
        widget.Switch(K1, At(-50));
        world.Reorder(K1, "A", "G", "K");
        widget.Switch(K3, At(0));
        var plan = widget.Plan(At(5));
        var g = world.GroupId(K3, "G");
        var k = world.GroupId(K3, "K");
        Check(plan.GroupOrder!.SequenceEqual(["A", "G", "K"]) && plan.CreateGroups!.SequenceEqual(["G", "K"])
              && plan.RenameGroups!.Select(rename => rename.Id).Order(StringComparer.Ordinal).SequenceEqual(new[] { g, k }.Order(StringComparer.Ordinal)),
            "G and K step aside to be made anew after A");
        world.RenameGroup(K3, k, "K (옮기는 중)");
        world.RenameGroup(K3, g, "X");
        var again = widget.Plan(At(10));
        Check(again.GroupOrder!.SequenceEqual(["A", "X", "K"]) && !again.CreateGroups!.Contains("G")
              && again.Moves!.Single() is { Group: "K", GroupId: null } toK && toK.Sessions.SequenceEqual(["local_k3"])
              && again.DeleteGroups!.Any(group => group.Id == k && group.Remade) && again.RenameGroups!.All(rename => rename.Id != k),
            "the user's renaming of G to X stands in the list, G is not made again, and K's move carries on");
        world.Apply(K3, again);
        Check(world.Of(K3).Groups.Select(group => group.Name).SequenceEqual(["A", "X", "K"]) && world.GroupOf(K3, "local_k3") == "K",
            "applied, 계정3 has A, X and K in order, with its session in K");
    }

    /// <summary>R6: while one group is aside mid-move, a group the user adds by hand stays, and one the user removes by
    /// hand is not made again.</summary>
    private static void TestEditedWhileAside()
    {
        foreach (var removing in new[] { false, true })
        {
            using var world = new SessionWorld(removing ? "removed-aside" : "added-aside");
            var widget = new Widget(world);
            widget.Map(K1, "계정1");
            widget.Map(K3, "계정3");
            foreach (var (account, id) in new[] { (K1, "local_k1"), (K3, "local_k3") })
            {
                world.Record(account, id, "케이", Cli(1));
                foreach (var name in new[] { "G", "K", "A" }) world.Group(account, name);
                world.Put(account, id, world.GroupId(account, "K"));
            }
            world.Copied(K3, "local_k3", "케이");
            widget.Start(K3, At(-60));
            widget.Switch(K1, At(-50));
            world.Reorder(K1, "A", "G", "K");
            widget.Switch(K3, At(0));
            var plan = widget.Plan(At(5));
            Check(plan.GroupOrder!.SequenceEqual(["A", "G", "K"]), "the plan puts A first");
            var k = world.GroupId(K3, "K");
            world.RenameGroup(K3, k, "K (옮기는 중)");
            if (removing) world.DeleteGroup(K3, world.GroupId(K3, "A"));
            else world.Group(K3, "X");
            var again = widget.Plan(At(10));
            if (removing)
                Check(again.GroupOrder is null || !again.GroupOrder.Contains("A") && !again.CreateGroups!.Contains("A"),
                    "a group the user removed while another was aside is not made again");
            else
                Check(again.GroupOrder!.SequenceEqual(["A", "G", "K", "X"]) && again.DeleteGroups!.All(group => !group.Name.StartsWith("X", StringComparison.Ordinal) || group.Remade),
                    "an empty group the user added while another was aside stays in the list");
            world.Apply(K3, again);
            var names = world.Of(K3).Groups.Select(group => group.Name).ToArray();
            Check(removing ? names.SequenceEqual(["G", "K"]) : names.SequenceEqual(["A", "G", "K", "X"]),
                removing ? "applied, 계정3 keeps G and K without A" : "applied, 계정3 has A, G, K and X in order");
        }
    }

    /// <summary>R7: a group the plan deleted as it meant, while another it renamed out of the way is still there, is not
    /// taken for the user's deletion: asked again, the plan keeps the list and the group made anew. R8: what the user
    /// then does to the group made anew (removes it, renames it) is seen, and the plan does not make the old one again.</summary>
    private static void TestDeletedAsPlanned()
    {
        foreach (var edit in new[] { "none", "removed", "renamed" })
        {
            using var world = new SessionWorld("deleted-as-planned-" + edit);
            var widget = new Widget(world);
            widget.Map(K1, "계정1");
            widget.Map(K3, "계정3");
            foreach (var (account, id) in new[] { (K1, "local_k1"), (K3, "local_k3") })
            {
                world.Record(account, id, "케이", Cli(1));
                foreach (var name in new[] { "G", "K", "A" }) world.Group(account, name);
                world.Put(account, id, world.GroupId(account, "K"));
            }
            world.Copied(K3, "local_k3", "케이");
            widget.Start(K3, At(-60));
            widget.Switch(K1, At(-50));
            world.Reorder(K1, "A", "G", "K");
            widget.Switch(K3, At(0));
            var plan = widget.Plan(At(5));
            Check(plan.GroupOrder!.SequenceEqual(["A", "G", "K"]), "the plan puts A first");
            // Applied all but the last deletion: the old K is still there, aside.
            var oldG = world.GroupId(K3, "G");
            var oldK = world.GroupId(K3, "K");
            world.RenameGroup(K3, oldG, "G (옮기는 중)");
            world.RenameGroup(K3, oldK, "K (옮기는 중)");
            var newG = world.Group(K3, "G");
            var newK = world.Group(K3, "K");
            world.Put(K3, "local_k3", newK);
            world.DeleteGroup(K3, oldG);
            foreach (var at in new[] { At(10), At(15) })
            {
                var again = widget.Plan(at);
                Check(again.CreateGroups!.Count == 0 && again.DeleteGroups!.Select(group => group.Id).SequenceEqual([oldK])
                      && again.DeleteGroups!.All(group => group.Id != newG) && (again.GroupOrder is null || again.GroupOrder.SequenceEqual(["A", "G", "K"])),
                    "asked again, the plan only deletes the old K, keeping the G made anew");
            }
            // The stalled sync reported, the user then removes or renames the G made anew, and syncs again.
            if (edit == "removed") world.DeleteGroup(K3, newG);
            else if (edit == "renamed") world.RenameGroup(K3, newG, "X");
            string[] want = edit switch { "removed" => ["A", "K"], "renamed" => ["A", "X", "K"], _ => ["A", "G", "K"] };
            var last = widget.Plan(At(20));
            Check(last.CreateGroups!.Count == 0 && last.DeleteGroups!.Select(group => group.Id).SequenceEqual([oldK])
                  && (last.GroupOrder is null || last.GroupOrder.SequenceEqual(want)),
                edit == "none" ? "asked once more, the plan still only deletes the old K"
                    : "a group made anew that the user " + edit + " is not made again, and only the old K goes");
            world.Apply(K3, last);
            Check(world.Of(K3).Groups.Select(group => group.Name).SequenceEqual(want) && world.GroupOf(K3, "local_k3") == "K"
                  && (edit == "removed" || world.GroupId(K3, want[1]) == newG),
                edit == "none" ? "applied, 계정3 has A, G and K with the G made anew kept"
                    : "applied, 계정3 keeps what the user did to the G made anew");
        }
    }

    /// <summary>A group the app would not empty (archived sessions) may stay aside long after the rest of the plan is
    /// done: the list then is the groups not aside, so the user's reordering by hand meanwhile counts and is not undone.</summary>
    private static void TestReorderedWithLeftover()
    {
        using var world = new SessionWorld("reordered-with-leftover");
        var widget = new Widget(world);
        widget.Map(K1, "계정1");
        widget.Map(K3, "계정3");
        foreach (var (account, id) in new[] { (K1, "local_k1"), (K3, "local_k3") })
        {
            world.Record(account, id, "케이", Cli(1));
            foreach (var name in new[] { "G", "K", "A" }) world.Group(account, name);
            world.Put(account, id, world.GroupId(account, "K"));
        }
        world.Copied(K3, "local_k3", "케이");
        widget.Start(K3, At(-60));
        widget.Switch(K1, At(-50));
        world.Reorder(K1, "A", "G", "K");
        widget.Switch(K3, At(0));
        Check(widget.Plan(At(5)).GroupOrder!.SequenceEqual(["A", "G", "K"]), "the plan puts A first");
        // Applied all but the last deletion, which the app refused: the old K stays aside.
        var oldG = world.GroupId(K3, "G");
        var oldK = world.GroupId(K3, "K");
        world.RenameGroup(K3, oldG, "G (옮기는 중)");
        world.RenameGroup(K3, oldK, "K (옮기는 중)");
        world.Group(K3, "G");
        var newK = world.Group(K3, "K");
        world.Put(K3, "local_k3", newK);
        world.DeleteGroup(K3, oldG);
        var checking = widget.Plan(At(10));
        Check(checking.CreateGroups!.Count == 0 && checking.DeleteGroups!.Select(group => group.Id).SequenceEqual([oldK]),
            "checked, the plan only deletes the old K left aside");
        // The user goes to 계정1 and back, then puts K first by hand, and syncs again.
        widget.Switch(K1, At(12));
        widget.Switch(K3, At(14));
        world.Reorder(K3, "K", "A", "G");
        var again = widget.Plan(At(20));
        Check(again.CreateGroups!.Count == 0 && again.RenameGroups!.Count == 0
              && again.DeleteGroups!.Select(group => group.Id).SequenceEqual([oldK])
              && (again.GroupOrder is null || again.GroupOrder.SequenceEqual(["K", "A", "G"])),
            "the user's order made while the old K stayed aside is kept, and only the old K goes");
        world.Apply(K3, again);
        Check(world.Of(K3).Groups.Select(group => group.Name).SequenceEqual(["K", "A", "G"]) && world.GroupOf(K3, "local_k3") == "K",
            "applied, 계정3 keeps K, A and G in the user's order");
    }

    /// <summary>R9: a sync stopped with K aside, and another account then removed K: asked twice without applying, the
    /// plan keeps the newer list from that account, and the order shown here (K aside left out) is no change of this
    /// account's own.</summary>
    private static void TestRetargetedWhileAside()
    {
        using var world = new SessionWorld("retargeted-aside");
        var widget = new Widget(world);
        widget.Map(K1, "계정1");
        widget.Map(K3, "계정3");
        foreach (var (account, id) in new[] { (K1, "local_k1"), (K3, "local_k3") })
        {
            world.Record(account, id, "케이", Cli(1));
            foreach (var name in new[] { "G", "K", "A" }) world.Group(account, name);
            world.Put(account, id, world.GroupId(account, "K"));
        }
        world.Copied(K3, "local_k3", "케이");
        widget.Start(K3, At(-60));
        widget.Switch(K1, At(-50));
        world.Reorder(K1, "A", "G", "K");
        widget.Switch(K3, At(0));
        Check(widget.Plan(At(5)).GroupOrder!.SequenceEqual(["A", "G", "K"]), "the plan puts A first");
        // Only K was put aside before the sync stopped.
        world.RenameGroup(K3, world.GroupId(K3, "K"), "K (옮기는 중)");
        widget.Plan(At(10));
        // 계정1 then removes K, and the user comes back.
        widget.Switch(K1, At(20));
        world.DeleteGroup(K1, world.GroupId(K1, "K"));
        widget.Switch(K3, At(30));
        var first = widget.Plan(At(35));
        Check(first.GroupOrder!.SequenceEqual(["A", "G"]) && first.GroupOrderBy == "계정1", "back in 계정3, the plan follows 계정1's newer list");
        var stored = widget.Layout.GroupsChanged.GetValueOrDefault(K3);
        var again = widget.Plan(At(40));
        Check(again.GroupOrder!.SequenceEqual(["A", "G"]) && again.GroupOrderBy == "계정1"
              && widget.Layout.GroupsChanged.GetValueOrDefault(K3) == stored,
            "asked again without applying, the plan still follows 계정1, and 계정3's order is no change of its own");
        world.Apply(K3, again);
        Check(world.Of(K3).Groups.Select(group => group.Name).SequenceEqual(["A", "G"]) && world.GroupOf(K3, "local_k3") is null,
            "applied, 계정3 has A and G, and the session leaves the group 계정1 removed");
    }

    /// <summary>R10: a group the plan set out to put aside and make anew, removed by the user before the plan got to it (the
    /// sync stopped first), is the user's removal: the next plan does not make it again — with no other group aside (the
    /// list shows it) and with another still aside (the edit by id shows it).</summary>
    private static void TestRemovedBeforeApplied()
    {
        foreach (var otherAside in new[] { false, true })
        {
            using var world = new SessionWorld(otherAside ? "removed-before-applied-aside" : "removed-before-applied");
            var widget = new Widget(world);
            widget.Map(K1, "계정1");
            widget.Map(K3, "계정3");
            string[] start = otherAside ? ["G", "K", "A"] : ["G", "A"];
            foreach (var (account, id) in new[] { (K1, "local_k1"), (K3, "local_k3") })
            {
                world.Record(account, id, "에이", Cli(1));
                foreach (var name in start) world.Group(account, name);
                world.Put(account, id, world.GroupId(account, "A"));
            }
            world.Copied(K3, "local_k3", "에이");
            widget.Start(K3, At(-60));
            widget.Switch(K1, At(-50));
            if (otherAside) world.Reorder(K1, "A", "G", "K"); else world.Reorder(K1, "A", "G");
            widget.Switch(K3, At(0));
            var plan = widget.Plan(At(5));
            Check(plan.RenameGroups!.Any(group => group.From == "G") && plan.CreateGroups!.Contains("G"),
                "the plan puts G aside to make it anew after A");
            // The sync stops: nothing done, or only K put aside. Checked, then the user removes G by hand and syncs again.
            if (otherAside) world.RenameGroup(K3, world.GroupId(K3, "K"), "K (옮기는 중)");
            widget.Plan(At(10));
            world.DeleteGroup(K3, world.GroupId(K3, "G"));
            var again = widget.Plan(At(20));
            Check(!again.CreateGroups!.Contains("G") && (again.GroupOrder is null || !again.GroupOrder.Contains("G")),
                otherAside ? "with K still aside, the G the user removed is not made again"
                    : "the G the user removed before the plan got to it is not made again");
            world.Apply(K3, again);
            Check(world.Of(K3).Groups.Select(group => group.Name).SequenceEqual(otherAside ? ["A", "K"] : ["A"]) && world.GroupOf(K3, "local_k3") == "A",
                otherAside ? "applied, 계정3 has A and K, without G" : "applied, 계정3 has A, without G");
        }
    }

    /// <summary>The widget starts (here: comes from the first format) while the app already shows 계정2, having come from
    /// 계정3 without the widget seeing it: what 계정2 shows dates to just after it came, so the user's rename and pin there
    /// are the newest — they stay, and 계정3 gets them. A first look that dated them to when the app had left 계정2 before
    /// (2026-10-21 install) is moved there too.</summary>
    private static void TestUnseenStay()
    {
        foreach (var earlierLook in new[] { false, true })
        {
            using var world = new SessionWorld(earlierLook ? "unseen-stay-redated" : "unseen-stay");
            var widget = new Widget(world);
            widget.Map(K2, "계정2");
            widget.Map(K3, "계정3");
            world.Record(K3, "local_x3", "개발", Cli(1));
            world.Record(K2, "local_x2", "개발", Cli(1));
            world.Copied(K2, "local_x2", "개발");
            // The app left 계정2 long ago; it shows 계정2 again, and the user renamed and pinned the conversation there.
            widget.Layout.Accounts[K2] = widget.Layout.Accounts[K2] with { LeftAt = At(-100) };
            widget.Layout.Active = K2;
            world.Retitle(K2, "local_x2", "언리얼 개발");
            world.Pin("local_x2");
            if (earlierLook)
            {
                // A first look the old way, before 계정3's leaving was known: it dated what it found to At(-100).
                world.Flush();
                Check(ClaudeSessionSync.Checkpoint(widget.Layout, world.Sources, K2, At(-5)), "the earlier first look is taken");
                Check(widget.Layout.Changed[K2]["local_x2"].Title is { At: var dated } && dated == At(-100), "it dated the rename to when the app had left 계정2");
            }
            // The app came to 계정2 when it left 계정3.
            widget.Layout.Accounts[K3] = widget.Layout.Accounts[K3] with { LeftAt = At(-10) };
            var here = widget.Plan(At(0));
            Check(here.Renames!.Count == 0 && here.Pins!.Count == 0 && widget.Layout.Accounts[K2].ArrivedAt == At(-10),
                earlierLook ? "after the earlier first look, 계정2's rename and pin still stay" : "in 계정2 the user's rename and pin stay");
            widget.Switch(K3, At(10));
            var there = widget.Plan(At(15));
            Check(there.Renames!.Any(rename => rename is { Session: "local_x3", Title: "언리얼 개발", By: "계정2" })
                  && there.Pins!.Any(pin => pin is { Session: "local_x3", Pinned: true, By: "계정2" }),
                "in 계정3 the plan brings 계정2's rename and pin");
        }
        // What is moved: only the account's own changes dated by then, its group list and its group edits; what a plan
        // brought in from another account keeps its time.
        var layout = new ClaudeSessionLayout();
        layout.Changed[K2] = new(StringComparer.Ordinal)
        {
            ["local_a"] = new(new(true, At(-100)), new("G", At(-150), ThirdKey), new("가", At(-120))),
            ["local_b"] = new(Title: new("나", At(5)))
        };
        layout.GroupsChanged[K2] = new(["G"], new Dictionary<string, string>(), At(-100));
        layout.GroupsChanged[K3] = new(["H"], new Dictionary<string, string>(), At(-100));
        layout.Renamings[K2] = [new("G", "G2", At(-100)), new(null, "X", At(5))];
        layout.Redate(K2, At(-100), At(-10));
        Check(layout.Changed[K2]["local_a"] == new Changes(new(true, At(-10)), new("G", At(-150), ThirdKey), new("가", At(-10)))
              && layout.Changed[K2]["local_b"] == new Changes(Title: new("나", At(5)))
              && layout.GroupsChanged[K2].At == At(-10) && layout.GroupsChanged[K3].At == At(-100)
              && layout.Renamings[K2][0].At == At(-10) && layout.Renamings[K2][1].At == At(5),
            "only the account's own changes dated by then move; what a plan brought in, later changes and other accounts keep theirs");
    }

    /// <summary>Where the app nests a session, read from its record as the app reads it: a suggested task, Claude's side
    /// session and a fork under the session they came from, none once moved to top level; then each family's top in a
    /// list, archived sessions included.</summary>
    private static void TestNestedLinks()
    {
        using var world = new SessionWorld("nested-links");
        static JsonObject Started(string by, bool side = false, bool userFork = false, string? deleted = null)
        {
            var spawned = new JsonObject { ["sessionId"] = by, ["taskId"] = "task_1", ["title"] = "일" };
            if (side) spawned["sideSession"] = true;
            if (userFork) spawned["userFork"] = true;
            if (deleted is not null) spawned["deletedParent"] = new JsonObject { ["sessionId"] = deleted };
            return spawned;
        }
        world.Record(K1, "local_task", "제안에서", Cli(1), extra: record => record["spawnedFrom"] = Started("local_top"));
        world.Record(K1, "local_side", "곁가지", Cli(2), extra: record => record["spawnedFrom"] = Started("local_top", side: true));
        world.Record(K1, "local_forked", "갈래", Cli(3), extra: record => record["forkedFromSessionId"] = "local_top");
        world.Record(K1, "local_btw", "옆 대화", Cli(4), extra: record =>
        {
            record["spawnedFrom"] = Started("local_top", side: true, userFork: true);
            record["forkedFromSessionId"] = "local_other";
        });
        world.Record(K1, "local_moved", "옮겨 붙음", Cli(5), extra: record =>
        {
            record["spawnedFrom"] = Started("local_top", side: true, deleted: "local_gone");
            record["forkedFromSessionId"] = "local_gone";
        });
        world.Record(K1, "local_refork", "다른 데서 갈래", Cli(6), extra: record =>
        {
            record["spawnedFrom"] = Started("local_top", side: true);
            record["forkedFromSessionId"] = "local_other";
        });
        world.Record(K1, "local_detached", "떼어 냄", Cli(7), extra: record =>
        {
            record["spawnedFrom"] = Started("local_top", side: true);
            record["lineageDetached"] = true;
        });
        world.Record(K1, "local_self", "제 자신", Cli(8), extra: record => record["spawnedFrom"] = Started("local_self"));
        world.Record(K1, "local_top", "맨 위", Cli(9));
        var parents = world.Records(K1).ToDictionary(record => record.SessionId, record => record.Parent, StringComparer.Ordinal);
        Check(parents["local_task"] == "local_top" && parents["local_side"] == "local_top" && parents["local_forked"] == "local_top",
            "a suggested task, Claude's side session and a fork link to the session they came from");
        Check(parents["local_btw"] == "local_other" && parents["local_refork"] == "local_other" && parents["local_moved"] == "local_top",
            "a side session forked from another session links to that one, unless the fork is its deleted starter's");
        Check(parents["local_detached"] is null && parents["local_self"] is null && parents["local_top"] is null,
            "one moved to top level, one linked to itself and one with no link have none");
        static Record Under(string id, string? parent) => new(id, id, false, null, [], 0, Parent: parent);
        var nests = ClaudeSessionSync.Nests(
            [Under("local_top", null), Under("local_mid", "local_top"), Under("local_leaf", "local_mid"), Under("local_lost", "local_gone"),
             Under("local_through", "local_shelf"), Under("local_stranded", "local_shut"), Under("local_a", "local_b"), Under("local_b", "local_a")],
            [Under("local_shelf", "local_top"), Under("local_shut", null), Under("local_kept", "local_shut")]);
        Check(nests["local_mid"] == "local_top" && nests["local_leaf"] == "local_top" && !nests.ContainsKey("local_top")
              && !nests.ContainsKey("local_lost"), "a family climbs to its top, and a parent the list lacks nests nothing");
        Check(nests["local_through"] == "local_top" && !nests.ContainsKey("local_stranded")
              && nests["local_shelf"] == "local_top" && nests["local_kept"] == "local_shut",
            "a live session whose parent is archived sits under the nearest live one above, or under none; an archived one stays under its parent");
        Check(nests.ContainsKey("local_a") && nests.ContainsKey("local_b"), "a family that loops still ends");
    }

    /// <summary>A nested session in a plan: never moved, its own assignment counts for nothing, and another account's nested
    /// session offers the group of its family's top rather than its own stale filing.</summary>
    private static void TestNestedPlan()
    {
        var parent = R("local_p", "부모", false, Cli(1));
        var child = R("local_c", "곁가지", false, Cli(2)) with { Parent = "local_p" };
        // 메인 keeps only A, where it files both; here the nested session's own assignment says B.
        var here = Compute([parent, child], Groups(["g1=A", "g2=B"], ("local_p", "g1"), ("local_c", "g2")),
            [Other(ReferenceKey, "메인", [R("local_q", "부모", false, Cli(1)), R("local_d", "곁가지", false, Cli(2))],
                Groups(["h1=A"], ("local_q", "h1"), ("local_d", "h1")), At(0))]);
        Check(here.Moves!.Count == 0 && here.GroupOrder!.SequenceEqual(["A"])
              && here.DeleteGroups!.Single() is { Name: "B (옮기는 중)", Hidden: 0, Remade: false }
              && here.Sidebars![0].Groups!.Single(group => group.Name == "A").Sessions.SequenceEqual(["곁가지", "부모"])
              && here.Sidebars[0].Nested!.Single() == new ClaudeSessionSync.NestedView("곁가지", "부모") && here.Sidebars[1].Nested is null,
            "a nested session is not moved: it shows its family's group, and B, holding only its own assignment, is empty");
        // The same conversation nested in 메인 under 부모, which is filed under A; its own stale filing says B.
        var there = Compute([R("local_p", "부모", false, Cli(1)), R("local_c", "곁가지", false, Cli(2))], Groups(["g1=A"], ("local_p", "g1")),
            [Other(ReferenceKey, "메인", [R("local_q", "부모", false, Cli(1)), R("local_d", "곁가지", false, Cli(2)) with { Parent = "local_q" }],
                Groups(["h1=A", "h2=B"], ("local_q", "h1"), ("local_d", "h2")), At(0))]);
        Check(there.Moves!.Single() is { Group: "A", GroupId: "g1" } toA && toA.Sessions.SequenceEqual(["local_c"])
              && there.Sidebars![1].Nested!.Single().Under == "부모",
            "another account's nested session offers its family's group, not its own stale filing");
        // What 메인 did to the family's group it offers for the nested session; what a plan brought 부모 there from another
        // account was that account's change to 부모 alone, and offers nothing for it.
        foreach (var brought in new[] { false, true })
        {
            var kept = new ClaudeSessionLayout();
            var theirs = new[] { R("local_q", "부모", false, Cli(1)), R("local_d", "곁가지", false, Cli(2)) with { Parent = "local_q" } };
            var layout = Groups(["h1=A"], ("local_q", "h1"));
            Steady(kept, ReferenceKey, "메인", theirs, layout, At(0));
            Changed(kept, ReferenceKey, "local_q", Change(At(10), group: "A", by: brought ? ThirdKey : null));
            var plan = Compute([R("local_p", "부모", false, Cli(1)), R("local_c", "곁가지", false, Cli(2))], Groups(["g1=A"], ("local_p", "g1")),
                [Other(ReferenceKey, "메인", theirs, layout, At(0))], kept);
            Check(brought ? plan.Moves!.Count == 0 : plan.Moves!.Single() is { Group: "A" } move && move.Sessions.SequenceEqual(["local_c"]),
                brought ? "a family's group a plan brought from another account is offered for no nested session"
                    : "the family's group 메인 chose itself is offered for its nested session");
        }
    }

    /// <summary>10/24: in 계정1 a session started from a suggestion is nested under the session that made it, while 계정2 and
    /// 계정3 hold copies of both as sessions of their own. The plan never moves the nested session, which the app refuses,
    /// nor takes its group for a change of its own; elsewhere its copy follows its family, and a filing the copy got by
    /// itself stays.</summary>
    private static void TestNestedOnDisk()
    {
        var (world, widget) = Accounts3("nested", ("p", "부모"), ("c", "곁가지"));
        using var _ = world;
        world.Record(K1, "local_c1", "곁가지", Cli(2), extra: record =>
            record["spawnedFrom"] = new JsonObject { ["sessionId"] = "local_p1", ["taskId"] = "task_1", ["title"] = "곁가지" });
        foreach (var (account, suffix) in new[] { (K1, "1"), (K2, "2"), (K3, "3") })
        {
            world.Group(account, "스킬 생태계");
            var tools = world.Group(account, "도구");
            world.Put(account, "local_p" + suffix, tools);
            // In 계정1 an assignment from before the session was nested, which the app ignores.
            world.Put(account, "local_c" + suffix, tools);
        }
        widget.Start(K1, At(-60)); widget.Switch(K3, At(-50)); widget.Switch(K2, At(-40));
        world.Put(K2, "local_p2", world.GroupId(K2, "스킬 생태계"));
        world.Put(K2, "local_c2", world.GroupId(K2, "스킬 생태계"));
        widget.Switch(K1, At(0));
        var plan = widget.Plan(At(5));
        Check(plan.Moves!.Single() is { Group: "스킬 생태계" } toSkills && toSkills.Sessions.SequenceEqual(["local_p1"])
              && plan.Sidebars![0].Nested!.Single() == new ClaudeSessionSync.NestedView("곁가지", "부모"),
            "in 계정1 the plan moves 부모, whose family goes with it, never the nested session by itself");
        world.Apply(K1, plan);
        Check(widget.Plan(At(6)) is { Moves.Count: 0, Pins.Count: 0 } && world.Shows(K1) == world.Shows(K2)
              && widget.Layout.Changed[K1].GetValueOrDefault("local_c1")?.Group is null,
            "applied, 계정1 shows what 계정2 shows, and the nested session's group is no change of its own");
        // What the earlier widget recorded on 10/24: the nested session taken out of its group, as 계정1's own change.
        Changed(widget.Layout, K1, "local_c1", Change(At(7), ungrouped: true));
        widget.Switch(K3, At(10));
        var there = widget.Plan(At(15));
        Check(there.Moves!.Single() is { Group: "스킬 생태계" } intoSkills
              && intoSkills.Sessions.Order(StringComparer.Ordinal).SequenceEqual(["local_c3", "local_p3"]),
            "in 계정3 both follow 계정2: what was recorded for the nested session in 계정1 takes nothing out of its group");
        world.Apply(K3, there);
        widget.Plan(At(16));
        Check(world.Shows(K3) == world.Shows(K1), "and 계정3 shows what 계정1 shows");
        widget.Switch(K1, At(20));
        Check(widget.Layout.Changed[K1].GetValueOrDefault("local_c1")?.Group is null,
            "back in 계정1, the group recorded for the nested session is gone");
        // The user moves 부모 in 계정1, its nested session with it: elsewhere the copy follows.
        world.Put(K1, "local_p1", world.GroupId(K1, "도구"));
        widget.Switch(K2, At(30));
        var follow = widget.Plan(At(35));
        Check(follow.Moves!.Single() is { Group: "도구" } intoTools
              && intoTools.Sessions.Order(StringComparer.Ordinal).SequenceEqual(["local_c2", "local_p2"]),
            "in 계정2 the copy of the nested session follows its family's move");
        world.Apply(K2, follow);
        widget.Plan(At(36));
        Check(world.Shows(K2) == world.Shows(K1), "and 계정2 shows what 계정1 shows");
        // The user files the copy by itself in 계정2: 계정1 cannot follow, 계정3 does.
        world.Put(K2, "local_c2", world.GroupId(K2, "스킬 생태계"));
        widget.Switch(K1, At(40));
        Check(widget.Plan(At(45)) is { Moves.Count: 0 } stays && stays.Sidebars![0].Nested!.Single().Under == "부모",
            "in 계정1 the nested session stays with its family, and the plan says why");
        widget.Switch(K3, At(50));
        var apart = widget.Plan(At(55));
        Check(apart.Moves!.Single() is { Group: "도구" } back && back.Sessions.SequenceEqual(["local_p3"]),
            "in 계정3 the copy keeps the filing 계정2 gave it by itself, and 부모 follows 계정1");
    }

    /// <summary>11/6: in 계정1 the side sessions of 부모 (pinned, in 보관함) and of 프로젝트 (in 도구) are nested under them,
    /// their own assignments dropped when their families moved; 계정2 and 계정3 hold copies filed as their families show.
    /// <paramref name="extra"/> names more side sessions of 프로젝트.</summary>
    private static (SessionWorld World, Widget Widget) SideSessions(string name, params (string Name, string Title)[] extra)
    {
        (string Name, string Title)[] conversations = [("p", "부모"), ("c", "곁가지1"), ("d", "곁가지2"), ("q", "프로젝트"), ("e", "프롬프트"), .. extra];
        var (world, widget) = Accounts3(name, conversations);
        for (var index = 0; index < conversations.Length; index++)
            if (conversations[index].Name is not ("p" or "q") and var side)
                world.Record(K1, "local_" + side + "1", conversations[index].Title, Cli(index + 1),
                    extra: record => record["spawnedFrom"] = SideOf(side switch { "c" => "local_p1", "d" => "local_c1", _ => "local_q1" }));
        foreach (var (account, suffix) in new[] { (K1, "1"), (K2, "2"), (K3, "3") })
        {
            var shelf = world.Group(account, "보관함");
            var tools = world.Group(account, "도구");
            world.Put(account, "local_p" + suffix, shelf);
            world.Put(account, "local_q" + suffix, tools);
            world.Pin("local_p" + suffix);
            if (account == K1) continue;
            foreach (var (side, _) in conversations.Where(conversation => conversation.Name is not ("p" or "q")))
                world.Put(account, "local_" + side + suffix, side is "c" or "d" ? shelf : tools);
        }
        return (world, widget);
    }

    private static JsonObject SideOf(string by) => new() { ["sessionId"] = by, ["taskId"] = "task_1", ["title"] = "일", ["sideSession"] = true };

    /// <summary>The user moves a side session in 계정1 to top level, as the app records it.</summary>
    private static void Detach(SessionWorld world, string id)
    {
        var record = world.Load(K1, id);
        record["lineageDetached"] = true;
        File.WriteAllText(world.RecordPath(K1, id), record.ToJsonString());
    }

    /// <summary>11/6: the user moved side sessions to top level in 계정1. The app pinned those whose family showed pinned and
    /// showed the others' own (dropped) assignment, which no one chose: the plan there takes them back to what the other
    /// accounts show, and nothing goes elsewhere. What the user did after moving one is the user's, and goes.</summary>
    private static void TestLeftFamilyOnDisk()
    {
        var (world, widget) = SideSessions("left-family", ("g", "곁가지3"));
        using var _ = world;
        widget.Start(K2, At(-60)); widget.Switch(K3, At(-50)); widget.Switch(K1, At(-40));
        Check(widget.Plan(At(-35)) is { Moves.Count: 0, Pins.Count: 0 }
              && widget.Layout.Checkpoints[K1].Nested is { } foreseen
              && foreseen.Keys.Order(StringComparer.Ordinal).SequenceEqual(["local_c1", "local_d1", "local_e1", "local_g1"])
              && foreseen["local_c1"] == new Shown(true, true, null) && foreseen["local_d1"] == new Shown(true, true, null)
              && foreseen["local_e1"] == new Shown(false, true, null),
            "the checkpoint foresees what each nested session would show out of its family: pinned under the pinned 부모, its own group");
        foreach (var side in new[] { "local_c1", "local_d1", "local_e1", "local_g1" }) Detach(world, side);
        world.Pin("local_c1"); world.Pin("local_d1");
        // Then the user files 곁가지3 under 보관함 and pins it.
        world.Put(K1, "local_g1", world.GroupId(K1, "보관함"));
        world.Pin("local_g1");
        var plan = widget.Plan(At(0));
        Check(plan.Moves!.Count == 2 && plan.Moves[0] is { Group: "보관함" } toShelf
              && toShelf.Sessions.Order(StringComparer.Ordinal).SequenceEqual(["local_c1", "local_d1"])
              && plan.Moves[1] is { Group: "도구" } toTools && toTools.Sessions.SequenceEqual(["local_e1"]) && plan.Pins!.Count == 0
              && widget.Layout.Changed[K1]["local_g1"] is { Pinned.Value: true, Group.Value: "보관함" }
              && widget.Layout.Changed[K1].GetValueOrDefault("local_c1")?.Pinned?.Value != true,
            "in 계정1 the plan takes the pins and the dropped group the app gave back to what the others show, and keeps what the user did");
        world.Apply(K1, plan);
        Check(widget.Plan(At(1)) is { Moves.Count: 0, Pins.Count: 0 }, "applied, nothing is left in 계정1");
        widget.Switch(K2, At(10));
        var there = widget.Plan(At(15));
        Check(there.Moves!.Single() is { Group: "보관함" } filed && filed.Sessions.SequenceEqual(["local_g2"])
              && there.Pins!.Single() is { Session: "local_g2", Pinned: true },
            "in 계정2 only what the user did goes: 곁가지3 filed under 보관함 and pinned, no pin from the app's");
        world.Apply(K2, there);
        widget.Plan(At(16));
        Check(world.Shows(K2) == world.Shows(K1), "and 계정2 shows what 계정1 shows");
    }

    /// <summary>The same with the checkpoint an earlier widget took, which foresaw nothing: a side session moved to top level
    /// since (its record written after it) shows what the app gave it; one moved there before keeps what the user does.</summary>
    private static void TestLeftFamilyBeforeUpgrade()
    {
        var (world, widget) = SideSessions("left-family-upgrade", ("f", "옛 곁가지"));
        using var _ = world;
        // 옛 곁가지 was moved to top level long before and filed by itself.
        Detach(world, "local_f1");
        File.SetLastWriteTimeUtc(world.RecordPath(K1, "local_f1"), At(-100).UtcDateTime);
        world.Put(K1, "local_f1", world.GroupId(K1, "도구"));
        widget.Start(K2, At(-60)); widget.Switch(K3, At(-50)); widget.Switch(K1, At(-40));
        Check(widget.Plan(At(-35)) is { Moves.Count: 0, Pins.Count: 0 }, "nothing to do before");
        widget.Layout.Checkpoints[K1] = widget.Layout.Checkpoints[K1] with { Nested = null };
        foreach (var side in new[] { "local_c1", "local_d1", "local_e1" }) Detach(world, side);
        world.Pin("local_c1"); world.Pin("local_d1");
        world.Put(K1, "local_f1", world.GroupId(K1, "보관함"));
        var plan = widget.Plan(At(0));
        Check(plan.Moves!.Count == 2 && plan.Moves[0] is { Group: "보관함" } toShelf
              && toShelf.Sessions.Order(StringComparer.Ordinal).SequenceEqual(["local_c1", "local_d1"])
              && plan.Moves[1] is { Group: "도구" } toTools && toTools.Sessions.SequenceEqual(["local_e1"]) && plan.Pins!.Count == 0
              && widget.Layout.Changed[K1]["local_f1"].Group?.Value == "보관함" && widget.Layout.Checkpoints[K1].Nested is { Count: 0 },
            "in 계정1 what the app gave goes back, the move of 옛 곁가지 is the user's, and the new checkpoint foresees again");
        world.Apply(K1, plan);
        widget.Switch(K2, At(10));
        var there = widget.Plan(At(15));
        Check(there.Moves!.Single() is { Group: "보관함" } filed && filed.Sessions.SequenceEqual(["local_f2"]) && there.Pins!.Count == 0,
            "in 계정2 only 옛 곁가지's move goes");
        world.Apply(K2, there);
        widget.Plan(At(16));
        Check(world.Shows(K2) == world.Shows(K1), "and 계정2 shows what 계정1 shows");
    }

    /// <summary>What the family did since the checkpoint counts too. 프로젝트 was moved, which drops 프롬프트's own filing;
    /// 프롬프트 was moved out, then the user pinned 프로젝트 and 프롬프트. Its lack of a group is no one's, its pin the user's:
    /// had 프로젝트 been pinned first the app would have pinned it, but nothing tells the two apart, and the plan must not
    /// undo a pin the user made. 곁가지1, filed by itself where its family never went, the user took out of every group
    /// after moving it out (or its family went away and back, which drops its filing too: the same on disk): the user's.</summary>
    private static void TestLeftFamilyChanged()
    {
        var (world, widget) = SideSessions("left-family-changed");
        using var _ = world;
        world.Put(K1, "local_c1", world.GroupId(K1, "도구"));
        world.Put(K1, "local_e1", world.GroupId(K1, "보관함"));
        widget.Start(K2, At(-60)); widget.Switch(K3, At(-50)); widget.Switch(K1, At(-40));
        Check(widget.Plan(At(-35)) is { Moves.Count: 0, Pins.Count: 0 } && widget.Layout.Checkpoints[K1].Nested is { } foreseen
              && foreseen["local_c1"] == new Shown(true, true, "도구") && foreseen["local_e1"] == new Shown(false, true, "보관함"),
            "the checkpoint foresees the filings of their own the family hides");
        world.Put(K1, "local_q1", world.GroupId(K1, "보관함"));
        world.Put(K1, "local_e1", null);
        Detach(world, "local_e1");
        world.Pin("local_q1");
        world.Pin("local_e1");
        Detach(world, "local_c1");
        world.Pin("local_c1");
        world.Put(K1, "local_c1", null);
        var plan = widget.Plan(At(0));
        var mine = widget.Layout.Changed[K1];
        Check(mine["local_c1"] is { Group: { Value: null } } c && c.Pinned?.Value != true
              && mine["local_e1"] is { Pinned.Value: true, Group: null }
              && mine["local_q1"] is { Pinned.Value: true, Group.Value: "보관함" },
            "the pin and the dropped filing the app gave are no one's; 프롬프트's pin and taking 곁가지1 out of its group are the user's");
        Check(plan.Moves!.Single() is { Group: "도구" } back && back.Sessions.SequenceEqual(["local_e1"])
              && plan.Pins!.Count == 2 && plan.Pins.Any(pin => pin is { Session: "local_c1", Pinned: false })
              && plan.Pins.Any(pin => pin is { Session: "local_e1", Pinned: true }),
            "in 계정1 the app's pin goes and 프롬프트 goes where the others show it, pinned again after the move");
        world.Apply(K1, plan);
        Check(widget.Plan(At(1)) is { Moves.Count: 0, Pins.Count: 0 }, "applied, nothing is left in 계정1");
        widget.Switch(K2, At(10));
        var there = widget.Plan(At(15));
        Check(there.Moves!.Count == 2 && there.Moves.Single(move => move.Group == "보관함").Sessions.SequenceEqual(["local_q2"])
              && there.Moves.Single(move => move.Group is null).Sessions.Order(StringComparer.Ordinal).SequenceEqual(["local_c2", "local_d2"])
              && there.Pins!.Count == 2 && there.Pins.Any(pin => pin is { Session: "local_q2", Pinned: true })
              && there.Pins.Any(pin => pin is { Session: "local_e2", Pinned: true }),
            "in 계정2 what the user did goes, 곁가지2 with its family, and no pin of the app's");
        world.Apply(K2, there);
        widget.Plan(At(16));
        Check(world.Shows(K2) == world.Shows(K1), "and 계정2 shows what 계정1 shows");
    }

    /// <summary>Whose a pin is follows the family as it shows now. 곁가지2 sits under 부모 above its archived parent
    /// 곁가지1; moved out, the app pinned it, as 부모 shows pinned then and now: no one's. 프로젝트 was pinned then, and the
    /// user unpinned it, moved 프롬프트 out and pinned that: had the app pinned it before 프로젝트 was unpinned, nothing tells
    /// the two apart, so it is the user's.</summary>
    private static void TestLeftFamilyUnclear()
    {
        var (world, widget) = SideSessions("left-family-unclear");
        using var _ = world;
        foreach (var suffix in new[] { "1", "2", "3" }) world.Pin("local_q" + suffix);
        foreach (var (account, suffix) in new[] { (K1, "1"), (K2, "2"), (K3, "3") }) world.Archive(account, "local_c" + suffix);
        widget.Start(K2, At(-60)); widget.Switch(K3, At(-50)); widget.Switch(K1, At(-40));
        Check(widget.Plan(At(-35)) is { Moves.Count: 0, Pins.Count: 0 } && widget.Layout.Checkpoints[K1].Nested is { } foreseen
              && foreseen["local_d1"] == new Shown(true, true, null) && foreseen["local_e1"] == new Shown(true, true, null),
            "the checkpoint foresees both pinned, under their pinned families");
        Detach(world, "local_d1");
        world.Pin("local_d1");
        world.Pin("local_q1", false);
        Detach(world, "local_e1");
        world.Pin("local_e1");
        var plan = widget.Plan(At(0));
        var mine = widget.Layout.Changed[K1];
        Check(mine.GetValueOrDefault("local_d1")?.Pinned?.Value != true && mine["local_e1"] is { Pinned.Value: true }
              && mine["local_q1"] is { Pinned.Value: false },
            "곁가지2's pin is no one's, 프롬프트's the user's");
        world.Apply(K1, plan);
        Check(widget.Plan(At(1)) is { Moves.Count: 0, Pins.Count: 0 } && !world.IsPinned("local_d1") && world.IsPinned("local_e1"),
            "applied in 계정1: 곁가지2 is unpinned, 프롬프트 stays pinned");
        widget.Switch(K2, At(10));
        var there = widget.Plan(At(15));
        Check(there.Moves!.Count == 0 && there.Pins!.Count == 2
              && there.Pins.Any(pin => pin is { Session: "local_q2", Pinned: false }) && there.Pins.Any(pin => pin is { Session: "local_e2", Pinned: true }),
            "in 계정2 the user's pins go, no pin of the app's");
        world.Apply(K2, there);
        widget.Plan(At(16));
        Check(world.Shows(K2) == world.Shows(K1), "and 계정2 shows what 계정1 shows");
    }

    /// <summary>A model, effort or permission mode another account changed since its checkpoint is planned here with the
    /// account it came from; this account's own later change stays. Whether a mode raises follows the rank order
    /// (plan &lt; default &lt; acceptEdits &lt; auto &lt; bypassPermissions, none as default).</summary>
    private static void TestSettingsFollowNewest()
    {
        Record[] Conversations(string prefix, params (string? Model, string? Effort, string? Mode)[] values) =>
            values.Select((value, index) => S(prefix + (index + 1), "대화" + (index + 1), index + 1, -30 - 10 * index,
                value.Model, value.Effort, value.Mode)).ToArray();
        var here = Conversations("local_c", ("m-old", "high", "default"), (null, null, "auto"), (null, null, ""),
            (null, null, "bypassPermissions"), ("m-mine", null, null), (null, null, ""));
        var before = Conversations("local_r", ("m-old", "high", "default"), (null, null, "auto"), (null, null, ""),
            (null, null, "bypassPermissions"), ("m-old", null, null), (null, null, "acceptEdits"));
        var there = Conversations("local_r", ("m-new", "max", "bypassPermissions"), (null, null, "acceptEdits"), (null, null, "acceptEdits"),
            (null, null, "auto"), ("m-new", null, null), (null, null, "default"));
        var kept = new ClaudeSessionLayout();
        Steady(kept, K1, "계정1", before, null, At(-10));
        Steady(kept, K2, "계정2", here, null, At(-20));
        // 계정2 chose its own model for the fifth after 계정1 changed it.
        Changed(kept, K2, "local_c5", new Changes(Model: new("m-mine", At(-5))));
        var plan = Compute(here, null, [Other(K1, "계정1", there, null, At(-10))], kept);
        Check(plan.Models!.Single() is { Session: "local_c1", Title: "대화1", Model: "m-new", From: "m-old", By: "계정1" }
              && plan.Efforts!.Single() is { Session: "local_c1", Effort: "max", From: "high", By: "계정1" },
            "a model and an effort another account changed since its checkpoint are planned here with its label; this account's own later change stays");
        Check(plan.Permissions!.Select(item => (item.Session, item.Mode, item.From, item.Raises, item.By, item.Self)).SequenceEqual([
                  ("local_c1", "bypassPermissions", "default", true, "계정1", false), ("local_c2", "acceptEdits", "auto", false, "계정1", false),
                  ("local_c3", "acceptEdits", null, true, "계정1", false), ("local_c4", "auto", "bypassPermissions", false, "계정1", false),
                  ("local_c6", "default", null, false, "계정1", false)]),
            "a higher mode raises, a lower one does not, none ranks as default, and default is planned from a recorded change; the most recently used first");
        Check(plan.Pending!.Sessions["local_c1"] is { Pinned: null, Group: null, Title: null } target
              && target.Model == new ClaudeSessionLayout.Stamp<string>("m-new", At(-10), K1)
              && target.Mode == new ClaudeSessionLayout.Stamp<string>("bypassPermissions", At(-10), K1)
              && !plan.Pending.Sessions.ContainsKey("local_c5"),
            "each planned value is set out with the time and account of the change it comes from");
        var json = JsonNode.Parse(ClaudeSessionSync.Serialize(plan))!;
        Check(json["models"] is JsonArray { Count: 1 } && json["permissions"]![1]!["raises"]!.GetValue<bool>() == false
              && json["permissions"]![1] is JsonObject item && !item.ContainsKey("self") && item["from"]!.GetValue<string>() == "auto"
              && json["permissions"]![2] is JsonObject fromNone && !fromNone.ContainsKey("from")
              && json is JsonObject root && !root.ContainsKey("selfSettings") && !root.ContainsKey("settingsBeyond"),
            "the plan writes raises always, self only when true, and the calling session's settings and the out-of-scope count only when there is something to say");
        var nothing = Compute(here, null, [Other(K1, "계정1", before, null, At(-10))], kept);
        Check(nothing is { Models.Count: 0, Efforts.Count: 0, Permissions.Count: 0 }
              && JsonNode.Parse(ClaudeSessionSync.Serialize(nothing)) is JsonObject empty && empty["permissions"] is JsonArray { Count: 0 },
            "with nothing to set the three lists are there and empty");
        Check(Compute(here, null, [Other(K1, "계정1", there, null, At(-10))], kept, listsWhole: false, warnings: ["list_incomplete"])
                  is { Models.Count: 0, Efforts.Count: 0, Permissions.Count: 0 },
            "nothing is planned for them when the plan is not decidable");
    }

    /// <summary>The app's fill (none to default) is no change, plan mode is neither a change nor planned, and a value going
    /// to none is no change.</summary>
    private static void TestSettingsNoChange()
    {
        var before = new[]
        {
            S("local_f", "채움", 1, -10, mode: ""), S("local_p", "계획", 2, -10, mode: "acceptEdits"),
            S("local_n", "없음", 3, -10, model: "m", effort: "high")
        };
        var now = new[]
        {
            S("local_f", "채움", 1, -10, mode: "default"), S("local_p", "계획", 2, -10, mode: null),
            S("local_n", "없음", 3, -10, model: "", effort: "")
        };
        Check(ClaudeSessionSync.Observe(new(now, null, true, NoCopies), PointOf(before, null, At(0)), At(10)).Changes.Count == 0,
            "none to default is the app's fill, plan mode is unknown, and a value taken away is none: no change");
        var here = new[]
        {
            S("local_a", "가", 1, -10, mode: "acceptEdits"), S("local_b", "나", 2, -10, mode: null), S("local_d", "다", 3, -10, model: "m-here"),
            S("local_e", "라", 4, -10, mode: "acceptEdits")
        };
        var was = new[]
        {
            S("local_a1", "가", 1, -10, mode: ""), S("local_b1", "나", 2, -10, mode: "default"), S("local_d1", "다", 3, -10, model: "m"),
            S("local_e1", "라", 4, -10, mode: "default")
        };
        var there = new[]
        {
            S("local_a1", "가", 1, -10, mode: "default"), S("local_b1", "나", 2, -10, mode: "auto"), S("local_d1", "다", 3, -10, model: ""),
            S("local_e1", "라", 4, -10, mode: null)
        };
        var kept = new ClaudeSessionLayout();
        Steady(kept, K1, "계정1", was, null, At(-10));
        Steady(kept, K2, "계정2", here, null, At(-20));
        var plan = Compute(here, null, [Other(K1, "계정1", there, null, At(-10))], kept);
        Check(plan is { Models.Count: 0, Efforts.Count: 0, Permissions.Count: 0 } && plan.Pending!.Sessions.Count == 0,
            "nothing is planned: not the app's fill, not a mode changed for a session in plan mode here, not a value taken away");
    }

    /// <summary>Where no account recorded a change, the account that last really used the conversation gives its explicit
    /// value; a record the widget wrote and nobody used since owns nothing.</summary>
    private static void TestSettingsStartingValue()
    {
        var here = new[]
        {
            S("local_a1", "가", 1, -100, "m-here", "low", "default"), S("local_a2", "나", 2, -110, "m-here"),
            S("local_a3", "다", 3, -120, "m-here", "low", "acceptEdits"), S("local_a4", "라", 4, -130, "m-here")
        };
        var there = new[]
        {
            S("local_b1", "가", 1, -30, "m-there", "max", "auto"), S("local_b2", "나", 2, -40, "m-there"),
            S("local_b3", "다", 3, -50, "", "", "default"), S("local_b4", "라", 4, -60, "m-there")
        };
        var kept = new ClaudeSessionLayout();
        Steady(kept, K1, "계정1", there, null, At(-10));
        Steady(kept, K2, "계정2", here, null, At(-20));
        Changed(kept, K1, "local_b4", new Changes(Model: new("m-rec", At(-500))));
        // The widget advanced 계정1's second record after its last use: its activity is the source's.
        var plan = Compute(here, null, [Other(K1, "계정1", there, null, At(-10), writes: new Dictionary<string, DateTimeOffset> { ["local_b2"] = At(-20) })],
            kept);
        Check(plan.Models!.Select(item => (item.Session, item.Model, item.From, item.By)).SequenceEqual([
                  ("local_a1", "m-there", "m-here", "계정1"), ("local_a4", "m-rec", "m-here", "계정1")])
              && plan.Efforts!.Single() is { Session: "local_a1", Effort: "max", From: "low", By: "계정1" }
              && plan.Permissions!.Single() is { Session: "local_a1", Mode: "auto", From: "default", Raises: true, By: "계정1" },
            "the account that last used a conversation gives its explicit values; one the widget wrote since owns nothing; a recorded change beats any starting value");
        Check(plan.Pending!.Sessions["local_a1"].Model == new ClaudeSessionLayout.Stamp<string>("m-there", At(-30), K1)
              && plan.Pending.Sessions["local_a4"].Model == new ClaudeSessionLayout.Stamp<string>("m-rec", At(-500), K1),
            "a starting value is dated its owner's own activity, by the owner");
        Check(plan.Permissions.All(item => item.Session != "local_a3") && plan.Efforts.All(item => item.Session != "local_a3")
              && plan.Models.All(item => item.Session != "local_a3"),
            "an owner without an explicit value gives nothing, and default is never a starting value");
        var mine = Compute(here.Select(record => record with { LastActivityAt = At(-1).ToUnixTimeMilliseconds() }).ToArray(), null,
            [Other(K1, "계정1", there, null, At(-10))], kept);
        Check(mine.Models!.Single().Session == "local_a4" && mine is { Efforts.Count: 0, Permissions.Count: 0 },
            "where this account used it last, its values stay");
    }

    /// <summary>What a plan set out to give: reached, it is recorded with the time and account of the change it came from;
    /// not reached (an approval declined), it is planned again; changed to something else, it is this account's own.</summary>
    private static void TestSettingsPlanInProgress()
    {
        using var world = new SessionWorld("settings-pending");
        var widget = new Widget(world);
        widget.Map(K1, "계정1");
        widget.Map(K3, "계정3");
        var used = At(-100).ToUnixTimeMilliseconds();
        foreach (var (account, suffix) in new[] { (K1, "1"), (K3, "3") })
        {
            world.Record(account, "local_p" + suffix, "피", Cli(1), activity: used, extra: record => record["permissionMode"] = "default");
            world.Record(account, "local_q" + suffix, "큐", Cli(2), activity: used, extra: record => record["permissionMode"] = "default");
            if (account == K1) continue;
            world.Copied(account, "local_p" + suffix, "피");
            world.Copied(account, "local_q" + suffix, "큐");
        }
        widget.Start(K1, At(-60)); widget.Switch(K3, At(-50)); widget.Switch(K1, At(-40));
        world.Set(K1, "local_p1", "permissionMode", "bypassPermissions");
        world.Set(K1, "local_q1", "effort", "max");
        widget.Switch(K3, At(10));
        var plan = widget.Plan(At(15));
        Check(plan is { Checkpointed: true, Models.Count: 0, Pins.Count: 0, Renames.Count: 0 }
              && plan.Permissions!.Single() is { Session: "local_p3", Mode: "bypassPermissions", From: "default", Raises: true, By: "계정1" }
              && plan.Efforts!.Single() is { Session: "local_q3", Effort: "max", From: null, By: "계정1" }
              && widget.Layout.Pendings[K3].Sessions["local_p3"] is { Pinned: null, Group: null, Title: null } target
              && target.Mode == new ClaudeSessionLayout.Stamp<string>("bypassPermissions", At(10), K1),
            "계정3 takes the mode and effort 계정1 changed, kept as in progress though they are its only targets");
        // The user declines raising the mode; the effort is not set yet either.
        Check(widget.Plan(At(16)) is { Permissions: [{ Session: "local_p3", By: "계정1" }], Efforts: [{ Session: "local_q3" }] }
              && widget.Layout.Changed[K3].GetValueOrDefault("local_p3")?.Mode is null,
            "a value not reached is no change here and is planned again");
        world.Set(K3, "local_p3", "permissionMode", "bypassPermissions");
        world.Set(K3, "local_q3", "effort", "low");
        Check(widget.Plan(At(17)) is { Permissions.Count: 0, Efforts.Count: 0 }
              && widget.Layout.Changed[K3]["local_p3"].Mode == new ClaudeSessionLayout.Stamp<string>("bypassPermissions", At(10), K1)
              && widget.Layout.Changed[K3]["local_q3"].Effort == new ClaudeSessionLayout.Stamp<string>("low", At(17)),
            "reached, a value counts as the change it came from; set to something else, it is this account's own choice");
        Check(world.Load(K3, "local_p3")["permissionMode"]!.GetValue<string>() == "bypassPermissions" && world.Load(K1, "local_q1")["effort"]!.GetValue<string>() == "max"
              && !world.Load(K3, "local_q3").ContainsKey("bypassChosenInApp"),
            "the records hold only what the app set");
    }

    /// <summary>Only a conversation used within the window or pinned here is planned; the others are counted.</summary>
    private static void TestSettingsScope()
    {
        const int eightDays = -60 * 24 * 8;
        var here = new[] { S("local_o", "오래됨", 1, eightDays, "m-old"), S("local_k", "고정", 2, eightDays, "m-old", pinned: true) };
        var before = new[] { S("local_o1", "오래됨", 1, eightDays, "m-old"), S("local_k1", "고정", 2, eightDays, "m-old") };
        var there = new[] { S("local_o1", "오래됨", 1, eightDays, "m-new"), S("local_k1", "고정", 2, eightDays, "m-new") };
        var kept = new ClaudeSessionLayout();
        Steady(kept, K1, "계정1", before, null, At(-10));
        Steady(kept, K2, "계정2", here, null, At(-20));
        var plan = Compute(here, null, [Other(K1, "계정1", there, null, At(-10))], kept);
        Check(plan is { SettingsBeyond: 1 } && plan.Models!.Single().Session == "local_k"
              && plan.Pending!.Sessions.GetValueOrDefault("local_o")?.Model is null && plan.Pending.Sessions["local_k"].Model is not null
              && JsonNode.Parse(ClaudeSessionSync.Serialize(plan))!["settingsBeyond"]!.GetValue<int>() == 1,
            "a conversation not used for a week and not pinned here is counted, not listed and not set out; one pinned here is listed");
        Check(Compute(here, null, [Other(K1, "계정1", there, null, At(-10))], kept, now: At(eightDays + 60)) is { SettingsBeyond: null } recent
              && recent.Models!.Count == 2, "used within the week it is planned");
    }

    /// <summary>The app refuses a session changing its own model or effort: the calling session's go to selfSettings; its
    /// mode is planned as any other's.</summary>
    private static void TestSettingsCallingSession()
    {
        var here = new[] { S("local_s", "나", 1, -10, "m-old", "high", "default"), S("local_t", "너", 2, -20, "m-old") };
        var before = new[] { S("local_s1", "나", 1, -10, "m-old", "high", "default"), S("local_t1", "너", 2, -20, "m-old") };
        var there = new[] { S("local_s1", "나", 1, -10, "m-new", "max", "auto"), S("local_t1", "너", 2, -20, "m-new") };
        var kept = new ClaudeSessionLayout();
        Steady(kept, K1, "계정1", before, null, At(-10));
        Steady(kept, K2, "계정2", here, null, At(-20));
        var plan = Compute(here, null, [Other(K1, "계정1", there, null, At(-10))], kept,
            lineage: new ClaudeSessionSync.Lineage("local_s", _ => null, (_, _) => null));
        Check(plan.SelfSettings == new ClaudeSessionSync.SelfSetting("m-new", "m-old", "계정1", "max", "high", "계정1")
              && plan.Models!.Single().Session == "local_t" && plan.Efforts!.Count == 0
              && plan.Permissions!.Single() is { Session: "local_s", Mode: "auto", Raises: true, Self: true }
              && plan.Pending!.Sessions["local_s"] is { Model: null, Effort: null, Mode.Value: "auto" },
            "the calling session's model and effort are told, not listed or set out; its mode is listed, marked self");
        var json = JsonNode.Parse(ClaudeSessionSync.Serialize(plan))!;
        Check(json["permissions"]![0]!["self"]!.GetValue<bool>() && json["selfSettings"]!["modelBy"]!.GetValue<string>() == "계정1"
              && json["selfSettings"]!["effortFrom"]!.GetValue<string>() == "high",
            "the plan writes them as the skill reads them");
    }

    /// <summary>The layout file keeps the three values in checkpoints, changes and plans in progress; one written before
    /// them loads with them unknown, and the first checkpoint after it finds no change of them.</summary>
    private static void TestSettingsLayoutStore()
    {
        var root = TempRoot("settings-layout");
        var layout = new ClaudeSessionLayout();
        layout.Accounts[K1] = new("계정1", At(0), At(1), 2);
        layout.Checkpoints[K1] = new(At(2), 2, [], new Dictionary<string, Shown>
            { ["local_a"] = new(false, true, null, "가", Model: "m", Effort: "", Mode: null) });
        layout.Changed[K1] = new(StringComparer.Ordinal)
            { ["local_a"] = new Changes(Model: new("m", At(3)), Effort: new("max", At(4), K2), Mode: new("auto", At(5))) };
        layout.Pendings[K1] = new(2, new Dictionary<string, Target> { ["local_a"] = new(Mode: new("bypassPermissions", At(6), K2)) }, null,
            new Dictionary<string, ClaudeSessionLayout.Moved>());
        layout.Save(root);
        var (loaded, _) = ClaudeSessionLayout.Load(root);
        Check(loaded!.Checkpoints[K1].Sessions["local_a"] is { Model: "m", Effort: "", Mode: null }
              && loaded.Changed[K1]["local_a"] == layout.Changed[K1]["local_a"]
              && loaded.Pendings[K1].Sessions["local_a"] == layout.Pendings[K1].Sessions["local_a"],
            "a round trip keeps the three values, none and unknown apart");
        var text = File.ReadAllText(Path.Combine(root, ClaudeSessionLayout.FileName));
        File.WriteAllText(Path.Combine(root, ClaudeSessionLayout.FileName), text.Replace("\"model\": \"m\"", "\"model\": 5"));
        Check(ClaudeSessionLayout.Load(root) is { Layout: null, Exists: true }, "reading stays strict about what a field holds");

        using var world = new SessionWorld("settings-upgrade");
        world.Record(K1, "local_a", "가", Cli(1), extra: record => { record["effort"] = "high"; record["permissionMode"] = "acceptEdits"; });
        world.Flush();
        var earlier = new JsonObject
        {
            ["version"] = 2,
            ["accounts"] = new JsonObject
            {
                [K1] = new JsonObject
                {
                    ["label"] = "계정1", ["stay"] = 0,
                    ["checkpoint"] = new JsonObject
                    {
                        ["at"] = At(0).ToString("O"), ["stay"] = 0, ["groups"] = new JsonArray(),
                        ["sessions"] = new JsonObject { ["local_a"] = new JsonObject { ["pinned"] = false, ["group"] = null, ["title"] = "가" } }
                    }
                }
            }
        };
        File.WriteAllText(Path.Combine(world.Ledger, ClaudeSessionLayout.FileName), earlier.ToJsonString());
        var (upgraded, _) = ClaudeSessionLayout.Load(world.Ledger);
        Check(upgraded!.Checkpoints[K1].Sessions["local_a"] is { Model: null, Effort: null, Mode: null },
            "a file an earlier widget wrote loads with the values unknown");
        Check(ClaudeSessionSync.Checkpoint(upgraded, world.Sources, K1, At(5)) && upgraded.Changed[K1].GetValueOrDefault("local_a") is null
              && upgraded.Checkpoints[K1].Sessions["local_a"] is { Model: "fable", Effort: "high", Mode: "acceptEdits" },
            "the first checkpoint after it records the values and finds no change of them");
    }

    /// <summary>A record new since the checkpoint: the account's own gives its explicit values, the widget's copy none.</summary>
    private static void TestSettingsNewRecord()
    {
        var records = new[]
        {
            S("local_o", "오", 1, -10, "m", "high", "default"), S("local_m", "엠", 2, -10, "", "", "auto"),
            S("local_c", "씨", 3, -10, "m", "max", "bypassPermissions")
        };
        var state = new ClaudeSessionSync.State(records, null, true, Copies(("local_c", "씨")));
        foreach (var point in new ClaudeSessionLayout.Checkpoint?[] { new(At(0), 0, null, new Dictionary<string, Shown>()), null })
        {
            var observed = ClaudeSessionSync.Observe(state, point, At(10));
            Check(observed.Changes["local_o"] is { Model.Value: "m", Effort.Value: "high", Mode: null }
                  && observed.Changes["local_m"] is { Model: null, Effort: null, Mode.Value: "auto" }
                  && observed.Changes.GetValueOrDefault("local_c") is null or { Model: null, Effort: null, Mode: null },
                "a record of the account's own gives its explicit values; the widget's copy gives none of the three");
        }
    }

    /// <summary>How a record's model, effort and permission mode read, and when the ledger says the widget wrote a record.</summary>
    private static void TestSettingsRead()
    {
        using var world = new SessionWorld("settings-read");
        void Make(string id, params (string Field, string? Value)[] fields) =>
            world.Record(K1, id, id, Cli(1), extra: record => { foreach (var (field, value) in fields) record[field] = value; });
        world.Record(K1, "local_i", "local_i", Cli(1), extra: record => { record["effortInherited"] = true; record["effort"] = "high"; });
        Make("local_u", ("effort", "ultra"));
        Make("local_x", ("model", "claude opus"));
        Make("local_l", ("model", new string('a', 81)));
        Make("local_v", ("model", "claude-opus-5-5[1m]"), ("effort", "max"), ("permissionMode", "bypassPermissions"));
        Make("local_p", ("permissionMode", "plan"));
        Make("local_w", ("permissionMode", "weird"));
        Make("local_n", ("model", null));
        var read = world.Records(K1).ToDictionary(record => record.SessionId, StringComparer.Ordinal);
        Check(read["local_i"].Effort == "" && read["local_u"].Effort == "" && read["local_x"].Model == "" && read["local_l"].Model == ""
              && read["local_n"].Model == "",
            "an inherited effort, an unknown effort, a model with other characters or over 80 characters read as none");
        Check(read["local_v"] is { Model: "claude-opus-5-5[1m]", Effort: "max", Mode: "bypassPermissions" }
              && read["local_p"].Mode is null && read["local_w"].Mode == "" && read["local_n"].Mode == "",
            "a familiar model, effort and mode read as they are; plan mode is unknown, and an unfamiliar or missing mode none");

        var ledger = TempRoot("settings-ledger");
        File.WriteAllText(Path.Combine(ledger, "ledger.json"), new JsonObject
        {
            ["created"] = new JsonArray(
                new JsonObject { ["dst"] = K1, ["file"] = "local_a.json", ["createdAt"] = At(1).ToString("O") },
                new JsonObject { ["dst"] = K1, ["file"] = "local_r.json", ["createdAt"] = At(1).ToString("O"), ["retiredAt"] = At(2).ToString("O") },
                new JsonObject { ["dst"] = K1, ["file"] = "local_t.json" },
                new JsonObject { ["dst"] = K2, ["file"] = "local_o.json", ["createdAt"] = At(1).ToString("O") }),
            ["advanced"] = new JsonArray(
                new JsonObject { ["dst"] = K1, ["file"] = "local_b.json", ["state"] = "done", ["at"] = At(3).ToString("O") },
                new JsonObject { ["dst"] = K1, ["file"] = "local_c.json", ["state"] = "unconfirmed", ["at"] = At(4).ToString("O") })
        }.ToJsonString());
        Check(ClaudeSessionSync.LedgerWrites(ledger, K1) is { Count: 3 } writes && writes["local_a"] == At(1) && writes["local_b"] == At(3)
              && writes["local_t"] == DateTimeOffset.MaxValue,
            "the widget wrote a copy it made (not archived) when it made it, and a record it advanced when the advance was done; one undated owns nothing");
    }

    /// <summary>Three accounts the widget maps, each listing the given conversations: 계정1 holds the records the app wrote,
    /// 계정2 and 계정3 the widget's copies of them.</summary>
    private static (SessionWorld World, Widget Widget) Accounts3(string name, params (string Name, string Title)[] conversations)
    {
        var world = new SessionWorld(name);
        var widget = new Widget(world);
        foreach (var (account, label, suffix) in new[] { (K1, "계정1", "1"), (K2, "계정2", "2"), (K3, "계정3", "3") })
        {
            widget.Map(account, label);
            for (var index = 0; index < conversations.Length; index++)
            {
                var id = "local_" + conversations[index].Name + suffix;
                world.Record(account, id, conversations[index].Title, Cli(index + 1));
                if (account != K1) world.Copied(account, id, conversations[index].Title);
            }
        }
        return (world, widget);
    }

    /// <summary>What the widget does with the layout, without its timer and log: a switch it watched dates leaving and
    /// arriving, starts a stay and checkpoints the account it comes to; a plan asked for in the account the app shows is
    /// served from a checkpoint and kept as the plan in progress.</summary>
    private sealed class Widget(SessionWorld world)
    {
        internal ClaudeSessionLayout Layout { get; } = new();

        internal void Map(string account, string label) => Layout.Accounts[account] = new(label);

        internal void Start(string account, DateTimeOffset at)
        {
            world.Flush();
            Layout.Active = account;
            Check(ClaudeSessionSync.Checkpoint(Layout, world.Sources, account, at), "the account the app shows first is checkpointed");
        }

        internal void Switch(string to, DateTimeOffset at)
        {
            world.Flush();
            var from = Layout.Active!;
            Layout.Accounts[from] = Layout.Accounts[from] with { LeftAt = at };
            var coming = Layout.Accounts[to];
            Layout.Accounts[to] = coming with { ArrivedAt = at, Stay = coming.Stay + 1 };
            Layout.Active = to;
            Check(ClaudeSessionSync.Checkpoint(Layout, world.Sources, to, at), "the account the app comes to is checkpointed");
        }

        /// <summary>A plan asked for in <paramref name="account"/>, the one the app shows unless named.</summary>
        internal SyncPlan Plan(DateTimeOffset now, string? account = null)
        {
            world.Flush();
            var asked = account ?? Layout.Active!;
            return ClaudeSessionSync.Serve(Layout, world.Sources, asked, null, active: asked == Layout.Active, now);
        }
    }

    /// <summary>
    /// A disk fixture of what the plan and the widget read, written the way the app writes it: each account's session
    /// records, the app's one pin list for every account (IndexedDB), its sidebar state (localStorage <c>dframe-store</c>:
    /// each scope's groups and assignments, and the sections that give the groups' order and folding), transcripts, and
    /// the widget's ledger of copies. Pins and the sidebar are kept here and written by <see cref="Flush"/>.
    /// </summary>
    internal sealed class SessionWorld : IDisposable
    {
        private readonly Dictionary<string, Sidebar> _sidebars = new(StringComparer.Ordinal);
        private readonly HashSet<string> _pins = new(StringComparer.Ordinal);
        private readonly List<JsonObject> _created = [];
        private ulong _pinSequence = 41;
        private int _groupNumber;
        private bool _pinsChanged = true, _sidebarChanged = true;

        /// <summary>One scope's groups in sidebar order, and where each session is filed.</summary>
        internal sealed class Sidebar
        {
            internal List<(string Id, string Name, bool Collapsed)> Groups { get; } = [];
            internal Dictionary<string, string> Assignments { get; } = new(StringComparer.Ordinal);
        }

        internal SessionWorld(string name)
        {
            Root = TempRoot(name);
            foreach (var folder in new[] { Sessions, Path.Combine(Projects, "E--work"), Ledger }) Directory.CreateDirectory(folder);
            CreateDatabase(Storage);
            CreateDatabase(Idb);
        }

        internal string Root { get; }
        internal string Sessions => Path.Combine(Root, "claude-code-sessions");
        internal string Storage => Path.Combine(Root, "Local Storage", "leveldb");
        internal string Idb => Path.Combine(Root, "IndexedDB", "https_claude.ai_0.indexeddb.leveldb");
        internal string Projects => Path.Combine(Root, "projects");
        internal string Ledger => Path.Combine(Root, "ledger");
        internal ClaudeSessionSync.Sources Sources => new(Sessions, Storage, Idb, Projects, Ledger);
        private string Backups => Path.Combine(Root, "backups");

        internal string ListOf(string account) => Path.Combine(Sessions, account.Split('/')[0], account.Split('/')[1]);

        internal string RecordPath(string account, string id) => Path.Combine(ListOf(account), id + ".json");

        /// <summary>Writes a session record as the app does, with the fields the plan reads and a few it does not.</summary>
        internal void Record(string account, string id, string? title, string? cli, string[]? prior = null, long activity = 1,
            bool archived = false, Action<JsonObject>? extra = null)
        {
            var record = new JsonObject
            {
                ["sessionId"] = id, ["cliSessionId"] = cli, ["title"] = title, ["cwd"] = @"C:\work", ["createdAt"] = 1,
                ["lastActivityAt"] = activity, ["isArchived"] = archived, ["model"] = "fable"
            };
            if (prior is { Length: > 0 }) record["priorCliSessionIds"] = new JsonArray(prior.Select(item => (JsonNode?)item).ToArray());
            extra?.Invoke(record);
            Directory.CreateDirectory(ListOf(account));
            Save(account, id, record);
        }

        internal JsonObject Load(string account, string id) => (JsonObject)JsonNode.Parse(File.ReadAllText(RecordPath(account, id)))!;

        private void Save(string account, string id, JsonObject record) => File.WriteAllText(RecordPath(account, id), record.ToJsonString());

        /// <summary>A rename in the app: the new title, with the old one among the record's earlier titles.</summary>
        internal void Retitle(string account, string id, string title)
        {
            var record = Load(account, id);
            if (record["previousTitles"] is not JsonArray earlier) record["previousTitles"] = earlier = new JsonArray();
            earlier.Add((string?)record["title"]);
            record["title"] = title;
            Save(account, id, record);
        }

        internal void Archive(string account, string id)
        {
            var record = Load(account, id);
            record["isArchived"] = true;
            Save(account, id, record);
        }

        /// <summary>The app sets one field of a record (its model, effort or permission mode, say); null removes it.</summary>
        internal void Set(string account, string id, string field, string? value)
        {
            var record = Load(account, id);
            if (value is null) record.Remove(field); else record[field] = value;
            Save(account, id, record);
        }

        /// <summary>The account's unarchived records, pinned as the pin list here says.</summary>
        internal IReadOnlyList<ClaudeSessionSync.Record> Records(string account) => ClaudeSessionSync.ReadRecords(ListOf(account), _pins);

        /// <summary>The account's nested sessions, each with the session at the top of its family, as the app nests them.</summary>
        internal IReadOnlyDictionary<string, string> NestsOf(string account)
        {
            var records = ClaudeSessionSync.ReadRecords(ListOf(account), _pins, out _, out var archived);
            return ClaudeSessionSync.Nests(records, archived);
        }

        /// <summary>The widget copied a session into <paramref name="account"/>'s list, writing <paramref name="written"/> as its
        /// title (null: a ledger entry from before titles were kept).</summary>
        internal void Copied(string account, string id, string? written)
        {
            var entry = new JsonObject { ["dst"] = account, ["file"] = id + ".json" };
            if (written is not null) entry["title"] = written;
            _created.Add(entry);
            WriteLedger();
        }

        /// <summary>The fill renamed its copy after the original: the record, and the title the ledger says it wrote.</summary>
        internal void Followed(string account, string id, string title)
        {
            var record = Load(account, id);
            record["title"] = title;
            Save(account, id, record);
            foreach (var entry in _created.Where(entry => (string?)entry["dst"] == account && (string?)entry["file"] == id + ".json"))
                entry["title"] = title;
            WriteLedger();
        }

        private void WriteLedger() => File.WriteAllText(Path.Combine(Ledger, "ledger.json"),
            new JsonObject { ["created"] = new JsonArray(_created.Select(entry => entry.DeepClone()).ToArray()) }.ToJsonString());

        internal bool IsPinned(string id) => _pins.Contains(id);

        /// <summary>Pins or unpins a session in the app's one pin list (every account's sessions are in it).</summary>
        internal void Pin(string id, bool pinned = true)
        {
            if (pinned ? _pins.Add(id) : _pins.Remove(id)) _pinsChanged = true;
        }

        /// <summary>The pin list emptied, as an app update once did.</summary>
        internal void ClearPins() { _pins.Clear(); _pinsChanged = true; }

        internal Sidebar Of(string account) => _sidebars.TryGetValue(account, out var sidebar) ? sidebar : _sidebars[account] = new();

        /// <summary>Makes a group at the end of the sidebar as the app does: a name already there gets that group back,
        /// unless <paramref name="twin"/> makes a second one (as an older app did).</summary>
        internal string Group(string account, string name, bool collapsed = false, bool twin = false)
        {
            var groups = Of(account).Groups;
            if (!twin && groups.FindIndex(group => group.Name == name) is var at and >= 0) return groups[at].Id;
            var id = "cg-" + (++_groupNumber).ToString("D3", CultureInfo.InvariantCulture);
            groups.Add((id, name, collapsed));
            _sidebarChanged = true;
            return id;
        }

        internal string GroupId(string account, string name) => Of(account).Groups.First(group => group.Name == name).Id;

        internal string? GroupOf(string account, string session) =>
            Of(account).Assignments.TryGetValue(session, out var id) ? Of(account).Groups.First(group => group.Id == id).Name : null;

        /// <summary>Files a session in a group, or in none; filing it in a group unpins it, as the app does.</summary>
        internal void Put(string account, string session, string? groupId)
        {
            var assignments = Of(account).Assignments;
            if (groupId is null) assignments.Remove(session);
            else { assignments[session] = groupId; Pin(session, false); }
            _sidebarChanged = true;
        }

        internal void RenameGroup(string account, string id, string name)
        {
            var groups = Of(account).Groups;
            var at = groups.FindIndex(group => group.Id == id);
            groups[at] = (id, name, groups[at].Collapsed);
            _sidebarChanged = true;
        }

        /// <summary>Deletes a group as the app does: its sessions are left in no group.</summary>
        internal void DeleteGroup(string account, string id)
        {
            var sidebar = Of(account);
            sidebar.Groups.RemoveAll(group => group.Id == id);
            foreach (var session in sidebar.Assignments.Where(pair => pair.Value == id).Select(pair => pair.Key).ToArray())
                sidebar.Assignments.Remove(session);
            _sidebarChanged = true;
        }

        /// <summary>The user drags the groups into this order; groups it does not name keep theirs after them.</summary>
        internal void Reorder(string account, params string[] names)
        {
            var groups = Of(account).Groups;
            var ordered = groups.Select((group, index) => (Group: group, Index: index))
                .OrderBy(item => Array.IndexOf(names, item.Group.Name) is var at and >= 0 ? at : names.Length + item.Index)
                .Select(item => item.Group).ToList();
            groups.Clear();
            groups.AddRange(ordered);
            _sidebarChanged = true;
        }

        /// <summary>Writes the pin list and the sidebar state where they changed, as the app would.</summary>
        internal void Flush()
        {
            if (_pinsChanged) AppendRaw(Idb, ++_pinSequence, PinKey(5), PinValue(_pins.Order(StringComparer.Ordinal)));
            if (_sidebarChanged) Check(WriteSidebar(Storage, SidebarState(), Backups), "the fixture sidebar is stored");
            _pinsChanged = _sidebarChanged = false;
        }

        /// <summary>The sidebar state the app keeps: per scope its groups (stored in another order than the sidebar's,
        /// which the sections give) and assignments, and its sections with their order and folding.</summary>
        internal JsonObject SidebarState()
        {
            var scopes = new JsonObject();
            var sidebars = new JsonObject();
            foreach (var (account, sidebar) in _sidebars.OrderBy(pair => pair.Key, StringComparer.Ordinal))
            {
                var assignments = new JsonObject();
                foreach (var (session, group) in sidebar.Assignments.OrderBy(pair => pair.Key, StringComparer.Ordinal))
                    assignments["code:" + session] = group;
                scopes[account] = new JsonObject
                {
                    ["groups"] = new JsonArray(sidebar.Groups.AsEnumerable().Reverse()
                        .Select(group => (JsonNode?)new JsonObject { ["id"] = group.Id, ["name"] = group.Name }).ToArray()),
                    ["assignments"] = assignments,
                    ["order"] = new JsonObject()
                };
                var sections = new JsonArray(new JsonObject
                {
                    ["id"] = "pinned", ["kind"] = "pinned", ["order"] = 0, ["collapsed"] = false, ["shown"] = true
                });
                for (var index = 0; index < sidebar.Groups.Count; index++)
                {
                    var (id, name, collapsed) = sidebar.Groups[index];
                    sections.Add(new JsonObject
                    {
                        ["id"] = id, ["kind"] = "manual", ["name"] = name, ["order"] = index + 1, ["collapsed"] = collapsed, ["shown"] = true
                    });
                }
                sections.Add(new JsonObject
                {
                    ["id"] = "sessions", ["kind"] = "sessions", ["order"] = sidebar.Groups.Count + 1, ["collapsed"] = false, ["shown"] = true
                });
                sidebars[account] = new JsonObject { ["sections"] = sections };
            }
            return new JsonObject
            {
                ["state"] = new JsonObject { ["customGroupsByScope"] = scopes, ["codeSidebarByScope"] = sidebars }, ["version"] = 1
            };
        }

        /// <summary>An account's groups as a plan reads them from the stored state.</summary>
        internal ClaudeSessionSync.Layout? LayoutOf(string account) =>
            ClaudeSessionSync.LayoutOf(JsonNode.Parse(SidebarState().ToJsonString())!["state"] as JsonObject, account, missingIsEmpty: true);

        /// <summary>A transcript: lines given as "uuid@time" (a message the user wrote; others as they are), its file created
        /// at <paramref name="created"/>.</summary>
        internal string Transcript(string created, params string[] entries)
        {
            var id = Guid.NewGuid().ToString("D");
            var path = Path.Combine(Projects, "E--work", id + ".jsonl");
            File.WriteAllLines(path, entries.Select(entry => entry.Split('@') is [var uuid, var at]
                ? "{\"type\":\"user\",\"uuid\":\"" + uuid + "\",\"timestamp\":\"" + at + "\"}" : entry));
            File.SetCreationTimeUtc(path, DateTime.Parse(created, CultureInfo.InvariantCulture, DateTimeStyles.AdjustToUniversal));
            return id;
        }

        /// <summary>Applies a plan in <paramref name="account"/> the way the skill does it with the app's tools: groups renamed
        /// out of the way, made at the end (a name already there gets that group back), sessions moved (into a group
        /// unpins), pins, titles, then the groups the moves emptied deleted. Older states wait for the user's consent. As
        /// the app does, a move naming a nested session without the session at the top of its family is refused whole, and
        /// one naming both files the family by that session, dropping the nested one's own assignment.</summary>
        internal void Apply(string account, ClaudeSessionSync.Plan plan)
        {
            foreach (var rename in plan.RenameGroups ?? []) RenameGroup(account, rename.Id, rename.To);
            foreach (var name in plan.CreateGroups ?? []) Group(account, name);
            foreach (var move in plan.Moves ?? [])
            {
                var nests = NestsOf(account);
                if (move.Sessions.FirstOrDefault(session => nests.TryGetValue(session, out var top) && !move.Sessions.Contains(top)) is { } nested)
                    throw new InvalidOperationException("Session " + nested + " is nested under " + nests[nested]
                        + ", and nested sessions move with their parent. Nothing was moved.");
                // Claude 4.23118 refuses the whole move when one session in it is archived.
                if (move.Sessions.FirstOrDefault(session => File.Exists(RecordPath(account, session))
                        && Load(account, session)["isArchived"] is JsonValue flag && flag.TryGetValue<bool>(out var shelved) && shelved) is { } archived)
                    throw new InvalidOperationException("Session " + archived
                        + " is archived: archived sessions aren't shown in the sidebar's groups or pins. Nothing was moved.");
                var group = move.Group is null ? null : move.GroupId ?? GroupId(account, move.Group);
                foreach (var session in move.Sessions)
                    if (nests.TryGetValue(session, out var top))
                    {
                        Put(account, top, group);
                        Of(account).Assignments.Remove(session);
                        if (group is not null) Pin(session, false);
                    }
                    else Put(account, session, group);
            }
            foreach (var pin in plan.Pins ?? []) Pin(pin.Session, pin.Pinned);
            foreach (var rename in plan.Renames ?? []) Retitle(account, rename.Session, rename.Title);
            foreach (var group in plan.DeleteGroups ?? []) DeleteGroup(account, group.Id);
            Flush();
        }

        /// <summary>What the app shows for an account, by title: its pinned sessions (with the group each keeps), each group
        /// with its other sessions in sidebar order, then the sessions in no group. A nested session shows the group of the
        /// session at the top of its family.</summary>
        internal string Shows(string account)
        {
            var records = Records(account);
            var sidebar = Of(account);
            var nests = NestsOf(account);
            string Home(string session) => nests.GetValueOrDefault(session) ?? session;
            static string Titles(IEnumerable<ClaudeSessionSync.Record> listed) =>
                string.Join(", ", listed.Select(record => record.Title ?? record.SessionId).Order(StringComparer.Ordinal));
            var shown = new List<string>
            {
                "고정: " + string.Join(", ", records.Where(record => record.Pinned)
                    .Select(record => record.Title + "(" + (GroupOf(account, Home(record.SessionId)) ?? "-") + ")").Order(StringComparer.Ordinal))
            };
            foreach (var group in sidebar.Groups)
                shown.Add(group.Name + ": " + Titles(records.Where(record => !record.Pinned
                    && sidebar.Assignments.GetValueOrDefault(Home(record.SessionId)) == group.Id)));
            shown.Add("그룹 없음: " + Titles(records.Where(record => !record.Pinned && !sidebar.Assignments.ContainsKey(Home(record.SessionId)))));
            return string.Join(" | ", shown);
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (IOException) { }
        }
    }

    /// <summary>A minimal Chromium localStorage database: a manifest naming log 5 and a VERSION entry.</summary>
    internal static string CreateDatabase(string directory)
    {
        var db = Directory.CreateDirectory(directory).FullName;
        var edit = new List<byte>();
        edit.AddRange(Varint(2)); edit.AddRange(Varint(5)); edit.AddRange(Varint(3)); edit.AddRange(Varint(7));
        edit.AddRange(Varint(4)); edit.AddRange(Varint(41));
        File.WriteAllBytes(Path.Combine(db, "MANIFEST-000003"), ClaudeLocalStorage.Frame(edit.ToArray(), 0));
        File.WriteAllText(Path.Combine(db, "CURRENT"), "MANIFEST-000003\n");
        File.WriteAllText(Path.Combine(db, "LOCK"), "");
        var version = new List<byte>(); version.AddRange(BitConverter.GetBytes(41UL)); version.AddRange(BitConverter.GetBytes(1));
        version.Add(1); version.AddRange(Varint(7)); version.AddRange(Encoding.ASCII.GetBytes("VERSION")); version.AddRange(Varint(1)); version.Add((byte)'1');
        File.WriteAllBytes(Path.Combine(db, "000005.log"), ClaudeLocalStorage.Frame(version.ToArray(), 0));
        return db;
    }

    /// <summary>Stores the app's sidebar state in a fixture localStorage as the app would (one appended batch).</summary>
    internal static bool WriteSidebar(string db, JsonObject state, string backups)
    {
        var store = new ClaudeLocalStorage(db);
        return store.Write(store.Read(["dframe-store"]), new Dictionary<string, string> { ["dframe-store"] = state.ToJsonString() }, backups,
            () => true);
    }

    /// <summary>The Code tab's pin list row of an IndexedDB database (object store 1, data index 1).</summary>
    internal static byte[] PinKey(byte database) => [0x00, database, 0x01, 0x01, .. ClaudeSidebarPins.EncodedKey];

    /// <summary>A pin list as the app stores it: record version, Blink's envelope, then V8's string.</summary>
    internal static byte[] PinValue(IEnumerable<string> ids, bool twoByte = false, bool trailer = true, string? json = null)
    {
        json ??= new JsonObject
        {
            ["state"] = new JsonObject { ["starredIds"] = new JsonArray(ids.Select(id => (JsonNode?)id).ToArray()) },
            ["version"] = 0, ["updatedAt"] = 1791741622224
        }.ToJsonString(new System.Text.Json.JsonSerializerOptions { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
        var bytes = new List<byte> { 0x6f, 0xFF, 0x15 };
        if (trailer) { bytes.Add(0xFE); bytes.AddRange(new byte[12]); }
        bytes.AddRange([0xFF, 0x10]);
        var text = twoByte ? Encoding.Unicode.GetBytes(json) : Encoding.Latin1.GetBytes(json);
        if (twoByte) bytes.Add(0x00);
        bytes.Add(twoByte ? (byte)'c' : (byte)'"');
        bytes.AddRange(Varint((ulong)text.Length));
        bytes.AddRange(text);
        return bytes.ToArray();
    }

    /// <summary>Appends one batch of a single put (or, with no value, a delete) to the fixture's live log.</summary>
    internal static void AppendRaw(string db, ulong sequence, byte[] key, byte[]? value)
    {
        var batch = new List<byte>();
        batch.AddRange(BitConverter.GetBytes(sequence)); batch.AddRange(BitConverter.GetBytes(1));
        batch.Add(value is null ? (byte)0 : (byte)1);
        batch.AddRange(Varint((ulong)key.Length)); batch.AddRange(key);
        if (value is not null) { batch.AddRange(Varint((ulong)value.Length)); batch.AddRange(value); }
        var log = Path.Combine(db, "000005.log");
        var framed = ClaudeLocalStorage.Frame(batch.ToArray(), new FileInfo(log).Length);
        using var stream = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        stream.Write(framed);
    }

    private static byte[] Varint(ulong value)
    {
        var bytes = new List<byte>();
        while (value >= 0x80) { bytes.Add((byte)(value | 0x80)); value >>= 7; }
        bytes.Add((byte)value);
        return bytes.ToArray();
    }

    private static void Rejects(Action action, string what)
    {
        try { action(); }
        catch (Exception exception) when (exception is IOException or InvalidDataException) { return; }
        throw new InvalidOperationException("accepted " + what);
    }

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
