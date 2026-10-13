using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using WeeklyUsageIndicator;

internal static class ClaudeSessionSyncTests
{
    private const string CurrentAccount = "46411433-0040-4598-a1bd-a5444a5eb7b4", CurrentOrg = "a8f42351-0c23-4928-87ef-ecb3e3bf2b10";
    private const string ReferenceAccount = "0998b264-7315-4fdb-8634-fc7f3f827316", ReferenceOrg = "7f98dbbd-af39-46bf-9609-cfd3972b3633";
    private static string CurrentKey => CurrentAccount + "/" + CurrentOrg;
    private static string ReferenceKey => ReferenceAccount + "/" + ReferenceOrg;
    private const string ThirdAccount = "7a7a7a7a-0000-4000-8000-00000000007a", ThirdOrg = "7b7b7b7b-0000-4000-8000-00000000007b";
    private static string ThirdKey => ThirdAccount + "/" + ThirdOrg;

    internal static Task RunAsync()
    {
        TestCombinedUsage();
        TestPlan();
        TestOlderStates();
        TestMerge();
        TestLayoutStore();
        TestPinList();
        TestTranscript();
        TestBuildFromDisk();
        TestGroupsFromLocalStorage();
        return Task.CompletedTask;
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

    private static ClaudeSessionSync.Other Other(string account, string? label, IReadOnlyList<ClaudeSessionSync.Record> records,
        ClaudeSessionSync.Layout? layout, IReadOnlySet<string>? copies = null,
        IReadOnlyDictionary<string, ClaudeSessionLayout.Agreed>? agreed = null, DateTimeOffset? leftAt = null,
        IReadOnlyList<IReadOnlyDictionary<string, ClaudeSessionLayout.Agreed>>? elsewhere = null,
        IReadOnlyDictionary<string, ClaudeSessionLayout.Agreed>? mine = null) =>
        new(account, label, leftAt, records, layout, copies ?? new HashSet<string>(), agreed, elsewhere, mine);

    private static ClaudeSessionLayout.Agreed A(bool? pinned = null, bool hasGroup = false, string? group = null, string? title = null) =>
        new(pinned, hasGroup, group, title);

    private static void TestPlan()
    {
        ClaudeSessionSync.Record Record(string id, string? title, bool pinned, string? cli, params string[] prior) =>
            new(id, title, pinned, cli, prior, 0);
        var current = new[]
        {
            Record("local_c1", "옛 이름", true, "x"), Record("local_c2", "같음", false, "y"),
            Record("local_c3", "이어감", false, "z2", "z"), Record("local_c4", "한쪽에만", true, "w"), Record("local_c5", "뒤", false, "v")
        };
        var reference = new[]
        {
            Record("local_r1", "새 이름", true, "x"), Record("local_r2", "같음", true, "y"),
            Record("local_r3", "원래", false, "z"), Record("local_r5", "앞서 이어감", false, "v2", "v")
        };
        var currentLayout = new ClaudeSessionSync.Layout([new("ga", "A")], new Dictionary<string, string> { ["local_c1"] = "ga", ["local_c5"] = "ga" });
        var referenceLayout = new ClaudeSessionSync.Layout([new("ra", "A"), new("rb", "B"), new("rc", "C")],
            new Dictionary<string, string> { ["local_r1"] = "rb", ["local_r3"] = "ra" });
        // Two accounts that never agreed on anything: the other account's own records offer what they show.
        var plan = ClaudeSessionSync.Compute(CurrentKey, "서브", current, currentLayout, [Other(ReferenceKey, "메인", reference, referenceLayout)]);
        Check(plan.Status == "ok" && plan.CreateGroups!.SequenceEqual(["B"]), "a group a move needs is created by name, an existing one reused");
        Check(plan.Moves!.Count == 3 && plan.Moves.Single(move => move.Group == "B").Sessions.SequenceEqual(["local_c1"])
              && plan.Moves.Single(move => move.Group == "A").Sessions.SequenceEqual(["local_c3"])
              && plan.Moves.Single(move => move.Group is null).Sessions.SequenceEqual(["local_c5"]),
            "sessions take the other account's group, a continuation included, and leave a group it does not use");
        Check(plan.Pins!.Select(pin => (pin.Session, pin.Pinned)).OrderBy(pin => pin.Session).SequenceEqual([("local_c1", true), ("local_c2", true)])
              && plan.Pins!.Single(pin => pin.Session == "local_c2").By == "메인",
            "pins follow; one moved into a group is pinned again because the move unpins it");
        Check(plan.Renames!.Select(rename => (rename.Session, rename.Title)).OrderBy(rename => rename.Session)
                  .SequenceEqual([("local_c1", "새 이름"), ("local_c3", "원래"), ("local_c5", "앞서 이어감")]),
            "titles follow, matched by transcript id or as a continuation either way");
        Check(plan.Others!.Single() is { Label: "메인", Matched: 4, OnlyThere.Count: 0 } && plan.Pins!.All(pin => pin.Session != "local_c4")
              && plan.OnlyHere!.SequenceEqual(["한쪽에만"]),
            "a session only this account has is left alone and named");
        // A dangling group reference on either side leaves that session where it is.
        var dangling = ClaudeSessionSync.Compute(CurrentKey, null, current, currentLayout, [Other(ReferenceKey, null, reference,
            referenceLayout with { Assignments = new Dictionary<string, string> { ["local_r1"] = "gone", ["local_r3"] = "ra" } })]);
        Check(dangling.Moves!.All(move => !move.Sessions.Contains("local_c1")) && dangling.Pins!.All(pin => pin.Session != "local_c1")
              && dangling.Warnings!.SequenceEqual(["groups_unresolved"]),
            "an assignment to a group the other account does not list moves nothing");
        // One record there serves one session here: an older state or a branch of a matched conversation is left alone.
        var older = ClaudeSessionSync.Compute(CurrentKey, null,
            [Record("local_now", "지금", false, "k2", "k"), Record("local_old", "옛 상태", false, "k"), Record("local_branch", "갈래", false, "b2", "k2")],
            null, [Other(ReferenceKey, null, [Record("local_ref", "기준", false, "k2", "k")], null)]);
        Check(older.Renames!.Select(rename => rename.Session).SequenceEqual(["local_now"]) && older.Others!.Single().Matched == 1
              && older.OnlyHere!.Count == 2,
            "an older state or a branch of an already matched conversation is not renamed after it");
        var blind = ClaudeSessionSync.Compute(CurrentKey, null, current, null, [Other(ReferenceKey, null, reference, null)],
            warnings: ["groups_unreadable"]);
        Check(blind.CreateGroups!.Count == 0 && blind.Moves!.Count == 0 && blind.Warnings!.Single() == "groups_unreadable"
              && blind.Pins!.Select(pin => pin.Session).SequenceEqual(["local_c2"]), "unreadable groups plan titles and pins only");
        var held = ClaudeSessionSync.Compute(CurrentKey, null, current, currentLayout, [Other(ReferenceKey, null, reference, referenceLayout)],
            groupsKnown: false);
        Check(held.Moves!.Count == 0 && held.CreateGroups!.Count == 0 && held.Pins!.Single().Session == "local_c2",
            "another account's unreadable groups hold every group change");
    }

    private static void TestOlderStates()
    {
        ClaudeSessionSync.Record R(string id, string title, bool pinned, string cli, string[] prior, long activity = 0,
            bool branch = false) => new(id, title, pinned, cli, prior, activity, branch);
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

        var none = new ClaudeSessionSync.Layout([], new Dictionary<string, string>());
        var current = new[] { R("local_old", "대화", true, "a1", [], 10), R("local_new", "대화", false, "a2", ["a1"], 20) };
        var reference = new[] { R("local_r_old", "대화", true, "a1", [], 10), R("local_r_new", "대화", false, "a2", ["a1"], 20) };
        var referenceLayout = new ClaudeSessionSync.Layout([new("g", "G")], new Dictionary<string, string> { ["local_r_old"] = "g" });
        var plan = ClaudeSessionSync.Compute(CurrentKey, "서브", current, none, [Other(ReferenceKey, "메인", reference, referenceLayout)],
            lineage: lineage);
        Check(plan.CreateGroups!.SequenceEqual(["G"]) && plan.Moves!.Single() is { Group: "G" } move && move.Sessions.SequenceEqual(["local_new"])
              && plan.Pins!.Single() is { Session: "local_new", Pinned: true } && plan.Others!.Single().Matched == 1 && plan.Renames!.Count == 0,
            "another account's older state is seen through: its pin and group go to the newest state there and here");
        Check(plan.Archive!.Single() is { Session: "local_old", Pinned: true, Newest: "local_new", Ready: false },
            "an older state waits until its newest state holds what it keeps");
        var moved = new ClaudeSessionSync.Layout([new("g2", "G")], new Dictionary<string, string> { ["local_new"] = "g2" });
        var after = ClaudeSessionSync.Compute(CurrentKey, "서브", [current[0], current[1] with { Pinned = true }], moved,
            [Other(ReferenceKey, "메인", reference, referenceLayout)], lineage: lineage);
        Check(after.Moves!.Count == 0 && after.Pins!.Count == 0 && after.Archive!.Single() is { Ready: true, Pinned: true },
            "once the newest state holds the pin and group, the older state is ready (to be unpinned, then archived)");

        // A conversation no other account lists keeps what its older state had.
        var ownLayout = new ClaudeSessionSync.Layout([new("h", "H")], new Dictionary<string, string> { ["local_c1"] = "h" });
        var alone = ClaudeSessionSync.Compute(CurrentKey, null,
            [R("local_c1", "여기만", true, "c1", [], 10), R("local_c2", "여기만", false, "c2", ["c1"], 20)], ownLayout,
            [Other(ReferenceKey, null, [R("local_z", "다른 것", true, "z", [])], none)], lineage: lineage);
        Check(alone.Moves!.Single() is { Group: "H" } kept && kept.Sessions.SequenceEqual(["local_c2"])
              && alone.Pins!.Single() is { Session: "local_c2", Pinned: true, By: null } && alone.Archive!.Single() is { Ready: false }
              && alone.OnlyHere!.SequenceEqual(["여기만"]) && alone.Others!.Single().Matched == 0,
            "an older state's pin and group go to its newest state even when no other account lists the conversation");

        // With nothing to take from, an account only clears its older states, handing their pin and group on first.
        var mine = new ClaudeSessionSync.Layout([new("g", "G")], new Dictionary<string, string> { ["local_old"] = "g" });
        var self = ClaudeSessionSync.Compute(CurrentKey, "서브", current, mine, [], lineage: lineage);
        Check(self is { Status: "ok", Others.Count: 0 } && self.Archive!.Single() is { Session: "local_old", Ready: false }
              && self.CreateGroups!.Count == 0 && self.Moves!.Single() is { Group: "G" } handed && handed.Sessions.SequenceEqual(["local_new"])
              && self.Pins!.Single() is { Session: "local_new", Pinned: true } && self.Sidebars!.Count == 1,
            "one record per conversation is kept, the newest holding the pin and group");

        // An account without a single pin while this one has some looks wiped: it offers no pins, so this account's
        // pins stay (a session moved into a group is pinned again).
        var wiped = ClaudeSessionSync.Compute(CurrentKey, null,
            [R("local_p", "고정", true, "p", []), R("local_q", "보통", false, "q", []), .. current], none,
            [Other(ReferenceKey, null,
                [R("local_rp", "고정", false, "p", []), R("local_rq", "보통", false, "q", []), R("local_r_new", "대화", false, "a2", ["a1"], 20)],
                new ClaudeSessionSync.Layout([new("g", "G")], new Dictionary<string, string> { ["local_rp"] = "g", ["local_rq"] = "g" }))],
            lineage: lineage);
        Check(wiped.Warnings!.SequenceEqual(["other_pins_empty"]) && wiped.Pins!.All(pin => pin.Pinned)
              && wiped.Pins!.Select(pin => pin.Session).Order(StringComparer.Ordinal).SequenceEqual(["local_new", "local_p"])
              && wiped.Moves!.Single() is { Group: "G" } stay && stay.Sessions.Order(StringComparer.Ordinal).SequenceEqual(["local_p", "local_q"])
              && wiped.Archive!.Single() is { Ready: false }, "an account with an empty pin list unpins nothing");

        // Unreadable pins plan no pins and no groups (a move could unpin); unreadable groups hold archiving too.
        var blind = ClaudeSessionSync.Compute(CurrentKey, null, current, none, [Other(ReferenceKey, null, reference, referenceLayout)],
            pinsKnown: false, lineage: lineage, warnings: ["pins_unreadable"]);
        Check(blind.Pins!.Count == 0 && blind.Moves!.Count == 0 && blind.CreateGroups!.Count == 0
              && blind.Archive!.Single() is { Pinned: false, Ready: false } && blind.Sidebars!.All(sidebar => sidebar.Pinned is null),
            "unknown pins leave pins, groups and older states alone");
        var ungrouped = ClaudeSessionSync.Compute(CurrentKey, null, [current[0], current[1] with { Pinned = true }], null,
            [Other(ReferenceKey, null, reference, null)], lineage: lineage, warnings: ["groups_unreadable"]);
        Check(ungrouped.Pins!.Count == 0 && ungrouped.Archive!.Single() is { Ready: false },
            "unknown groups hold the older state: its group could not be handed on");
        // A group that cannot be resolved on either state leaves the hand-over unproven.
        var mineDangling = new ClaudeSessionSync.Layout([new("h", "H")],
            new Dictionary<string, string> { ["local_old"] = "h", ["local_new"] = "gone" });
        var dangling = ClaudeSessionSync.Compute(CurrentKey, null, [current[0] with { Pinned = false }, current[1]], mineDangling, [],
            lineage: lineage);
        Check(dangling.Moves!.Count == 0 && dangling.Archive!.Single() is { Ready: false },
            "a newest state filed under a group the scope does not list is not taken as holding the older state's group");
        var oldDangling = new ClaudeSessionSync.Layout([new("h", "H")], new Dictionary<string, string> { ["local_old"] = "gone" });
        var unknownOld = ClaudeSessionSync.Compute(CurrentKey, null, [current[0] with { Pinned = false }, current[1]], oldDangling, [],
            lineage: lineage);
        Check(unknownOld.Archive!.Single() is { Ready: false }, "an older state in a group the scope does not list is kept");

        // An older state's agreement there stands for its newest state: what the two agreed on before the conversation went
        // on is not taken for a change made since.
        var agreedBefore = new Dictionary<string, ClaudeSessionLayout.Agreed> { ["local_r_old"] = A(false, true, null, "대화") };
        var seen = ClaudeSessionSync.Compute(CurrentKey, null, [R("local_c", "대화", true, "a2", ["a1"], 20)], none,
            [Other(ReferenceKey, null, [reference[0] with { Pinned = false }, reference[1], R("local_r_x", "다른", true, "x9", [])], none,
                agreed: agreedBefore)], lineage: lineage);
        Check(seen.Pins!.Count == 0 && seen.Moves!.Count == 0, "an older state's agreement fills in for its newest state");

        // The sidebars read as the app shows them: pinned sessions under Pinned only, everything by title.
        var view = ClaudeSessionSync.Compute(CurrentKey, "서브",
            [R("local_1", "나중", true, "1", []), R("local_2", "가운데", false, "2", []), R("local_3", "처음", true, "3", [])],
            new ClaudeSessionSync.Layout([new("g2", "둘"), new("g1", "하나")],
                new Dictionary<string, string> { ["local_1"] = "g1", ["local_2"] = "g1" }), [Other(ReferenceKey, "메인", [], null)]);
        Check(view.Sidebars![0] is { Label: "서브", Pinned: { } pinnedTitles, Groups: { } groupViews }
              && pinnedTitles.SequenceEqual(["나중", "처음"]) && groupViews.Select(group => group.Name).SequenceEqual(["둘", "하나"])
              && groupViews[1].Sessions.SequenceEqual(["가운데"]) && view.Sidebars[1].Groups is null,
            "sidebars list pins and groups by title, a pinned session only under Pinned");
    }

    private static void TestMerge()
    {
        ClaudeSessionSync.Record R(string id, string title, bool pinned, string cli, bool renamed = false) =>
            new(id, title, pinned, cli, [], 0, Renamed: renamed);
        var groups = new ClaudeSessionSync.Layout([new("g", "G"), new("h", "H")], new Dictionary<string, string>());
        ClaudeSessionSync.Layout In(string session, string group) =>
            groups with { Assignments = new Dictionary<string, string> { [session] = group } };
        var t0 = DateTimeOffset.Parse("2026-10-13T00:00:00Z");

        // Without agreements, a copy the widget made there gives only what was chosen there.
        var copies = new HashSet<string> { "local_copy" };
        var mine = new[] { R("local_mine", "내 이름", true, "a") };
        var untouched = ClaudeSessionSync.Compute(CurrentKey, null, mine, groups,
            [Other(ReferenceKey, null, [R("local_copy", "옛 이름", false, "a"), R("local_other", "다른 것", true, "z")], groups, copies)]);
        Check(untouched.Pins!.Count == 0 && untouched.Renames!.Count == 0 && untouched.Moves!.Count == 0,
            "a copy the widget made there, untouched there, unpins and renames nothing");
        var renamed = ClaudeSessionSync.Compute(CurrentKey, null, mine, groups,
            [Other(ReferenceKey, null, [R("local_copy", "거기서 바꾼 이름", false, "a", renamed: true)], groups, copies)]);
        Check(renamed.Renames!.Single() is { Title: "거기서 바꾼 이름" } && renamed.Pins!.Count == 0, "a copy renamed there gives its title, still no pin");
        // Where the ledger tracks the title the widget wrote, any difference is a rename there, history or not.
        var recasedThere = ClaudeSessionSync.Compute(CurrentKey, null, mine, groups,
            [Other(ReferenceKey, null, [R("local_copy", "옛 이름 V2", false, "a")], groups, copies) with
                { CopyTitles = new Dictionary<string, string?> { ["local_copy"] = "옛 이름 v2" } }]);
        Check(recasedThere.Renames!.Single().Title == "옛 이름 V2", "a copy recased there gives its title");
        var asWritten = ClaudeSessionSync.Compute(CurrentKey, null, mine, groups,
            [Other(ReferenceKey, null, [R("local_copy", "옛 이름", false, "a", renamed: true)], groups, copies) with
                { CopyTitles = new Dictionary<string, string?> { ["local_copy"] = "옛 이름" } }]);
        Check(asWritten.Renames!.Count == 0, "a copy still showing the title the widget wrote gives none, whatever its history");
        var filedCopy = ClaudeSessionSync.Compute(CurrentKey, null, mine, groups,
            [Other(ReferenceKey, null, [R("local_copy", "내 이름", false, "a"), R("local_other", "다른 것", true, "z")], In("local_copy", "g"), copies)]);
        Check(filedCopy.Moves!.Single() is { Group: "G" } && filedCopy.Pins!.Count == 0,
            "a copy filed there gives its group, and the pin it has since: filing it there unpinned it");
        var filedPinned = ClaudeSessionSync.Compute(CurrentKey, null, mine, groups,
            [Other(ReferenceKey, null, [R("local_copy", "내 이름", true, "a")], In("local_copy", "g"), copies)]);
        Check(filedPinned.Moves!.Single() is { Group: "G" } && filedPinned.Pins!.Single() is { Session: "local_mine", Pinned: true },
            "a copy filed there and pinned again is moved, then pinned again here");
        var pinnedCopy = ClaudeSessionSync.Compute(CurrentKey, null, mine, In("local_mine", "h"),
            [Other(ReferenceKey, null, [R("local_copy", "내 이름", true, "a")], groups, copies)]);
        Check(pinnedCopy.Moves!.Count == 0 && pinnedCopy.Pins!.Count == 0, "a pinned copy without a group keeps this account's group");
        var original = ClaudeSessionSync.Compute(CurrentKey, null, mine, groups,
            [Other(ReferenceKey, null, [R("local_copy", "옛 이름", false, "a"), R("local_other", "다른 것", true, "z")], groups)]);
        Check(original.Pins!.Single() is { Pinned: false } && original.Renames!.Single().Title == "옛 이름",
            "a record the app wrote there offers its pin and title as they are");

        // With agreements: what the other account did not change since the two agreed is not offered, so what changed
        // here stays (an account the app only passed through overwrites nothing).
        var agreed = new Dictionary<string, ClaudeSessionLayout.Agreed> { ["local_r"] = A(false, true, null, "옛 제목") };
        var passedThrough = ClaudeSessionSync.Compute(CurrentKey, null, [R("local_c", "새 제목", true, "a")], In("local_c", "h"),
            [Other(ReferenceKey, null, [R("local_r", "옛 제목", false, "a"), R("local_x", "다른 것", true, "x")], groups, agreed: agreed,
                leftAt: t0)]);
        Check(passedThrough.Pins!.Count == 0 && passedThrough.Renames!.Count == 0 && passedThrough.Moves!.Count == 0,
            "an account that changed nothing since the two agreed leaves this account's changes alone");
        // What it did change is taken, field by field, even over a change made here.
        var changedThere = ClaudeSessionSync.Compute(CurrentKey, null, [R("local_c", "여기 제목", false, "a")], In("local_c", "h"),
            [Other(ReferenceKey, "메인", [R("local_r", "그쪽 제목", true, "a")], In("local_r", "g"), agreed: agreed)]);
        Check(changedThere.Renames!.Single() is { Title: "그쪽 제목", By: "메인" } && changedThere.Moves!.Single() is { Group: "G" }
              && changedThere.Pins!.Single() is { Pinned: true, By: "메인" }, "a change made there is taken");
        var partly = ClaudeSessionSync.Compute(CurrentKey, null, [R("local_c", "여기 제목", false, "a")], In("local_c", "h"),
            [Other(ReferenceKey, null, [R("local_r", "옛 제목", true, "a")], groups, agreed: agreed)]);
        Check(partly.Pins!.Single() is { Pinned: true } && partly.Renames!.Count == 0 && partly.Moves!.Count == 0,
            "only the fields changed there are taken");

        // Three accounts. Changes that disagree go to the account the app left last.
        var start = new Dictionary<string, ClaudeSessionLayout.Agreed> { ["local_m"] = A(title: "처음"), ["local_t"] = A(title: "처음") };
        var main = Other(ReferenceKey, "메인", [R("local_m", "메인에서", false, "a")], groups, agreed: start, leftAt: t0.AddHours(1));
        var third = Other(ThirdKey, "셋째", [R("local_t", "셋째에서", false, "a")], groups, agreed: start, leftAt: t0.AddHours(2));
        var recent = ClaudeSessionSync.Compute(CurrentKey, null, [R("local_c", "처음", false, "a")], groups, [main, third]);
        Check(recent.Renames!.Single() is { Title: "셋째에서", By: "셋째" } && recent.Others!.Count == 2,
            "changes that disagree go to the account the app left last");
        var oneChanged = ClaudeSessionSync.Compute(CurrentKey, null, [R("local_c", "처음", false, "a")], groups,
            [main, third with { Records = [R("local_t", "처음", false, "a")] }]);
        Check(oneChanged.Renames!.Single() is { Title: "메인에서", By: "메인" }, "a change in any other account is taken, wherever it was made");
        // An account this one never agreed with offers what it shows, but a value it merely still holds as agreed with a
        // third yields to one changed since, though that account was left later.
        var fresh = ClaudeSessionSync.Compute(CurrentKey, null, [R("local_n", "제목", false, "a")], groups,
        [
            Other(ReferenceKey, "메인", [R("local_m", "제목", false, "a")], groups, leftAt: t0.AddHours(2),
                elsewhere: [new Dictionary<string, ClaudeSessionLayout.Agreed> { ["local_m"] = A(false) }]),
            Other(ThirdKey, "셋째", [R("local_t", "제목", true, "a")], groups, leftAt: t0.AddHours(1),
                elsewhere: [new Dictionary<string, ClaudeSessionLayout.Agreed> { ["local_t"] = A(false) }])
        ]);
        Check(fresh.Pins!.Single() is { Pinned: true, By: "셋째" }, "a value still as agreed elsewhere yields to a fresh change");
        // A stale offer alone fills only a copy the widget made here; this account's own record keeps its value.
        var staleOnly = Other(ReferenceKey, "메인", [R("local_m", "제목", true, "a")], groups,
            elsewhere: [new Dictionary<string, ClaudeSessionLayout.Agreed> { ["local_m"] = A(true) }]);
        var ownRecord = ClaudeSessionSync.Compute(CurrentKey, null, [R("local_n", "제목", false, "a")], groups, [staleOnly]);
        Check(ownRecord.Pins!.Count == 0, "a value nobody changed since agreeing elsewhere does not undo this account's own");
        var copyHere = ClaudeSessionSync.Compute(CurrentKey, null, [R("local_n", "제목", false, "a")], groups, [staleOnly],
            currentCopies: new HashSet<string> { "local_n" });
        Check(copyHere.Pins!.Single() is { Pinned: true }, "the widget's copy here takes it, holding no choice of its own");
        // A copy here the user changed keeps the change. A and C, and B and C, agreed on "옛 이름"; A and B never agreed.
        var thirdAgreed = new Dictionary<string, ClaudeSessionLayout.Agreed> { ["local_t"] = A(true, true, null, "옛 이름") };
        var staleB = Other(ReferenceKey, "B", [R("local_b", "옛 이름", true, "a")], groups, leftAt: t0.AddHours(2),
            elsewhere: [new Dictionary<string, ClaudeSessionLayout.Agreed> { ["local_b"] = A(true, true, null, "옛 이름") }]);
        var steadyC = Other(ThirdKey, "C", [R("local_t", "옛 이름", true, "a")], groups, agreed: thirdAgreed, leftAt: t0.AddHours(1),
            mine: new Dictionary<string, ClaudeSessionLayout.Agreed> { ["local_n"] = A(true, true, null, "옛 이름") });
        var copies2 = new HashSet<string> { "local_n" };
        // The ledger holds the title the widget wrote to the copy: only a copy showing exactly that has an untouched title.
        var written = new Dictionary<string, string?> { ["local_n"] = "처음 이름" };
        var renamedHere = ClaudeSessionSync.Compute(CurrentKey, null, [R("local_n", "새 이름", true, "a", renamed: true)], groups,
            [staleB, steadyC with { Mine = null }], currentCopies: copies2, currentCopyTitles: written);
        Check(renamedHere.Renames!.Count == 0, "a copy renamed here keeps its name against a stale one");
        var recased = ClaudeSessionSync.Compute(CurrentKey, null, [R("local_n", "PROJECT", true, "a")], groups,
            [staleB, steadyC with { Mine = null }], currentCopies: copies2,
            currentCopyTitles: new Dictionary<string, string?> { ["local_n"] = "Project" });
        Check(recased.Renames!.Count == 0, "a copy renamed here without a recorded history (case only) keeps its name too");
        var untracked = ClaudeSessionSync.Compute(CurrentKey, null, [R("local_n", "처음 이름", true, "a")], groups,
            [staleB, steadyC with { Mine = null }], currentCopies: copies2);
        Check(untracked.Renames!.Count == 0, "a copy whose written title the ledger does not track keeps its name");
        var untouchedHere = ClaudeSessionSync.Compute(CurrentKey, null, [R("local_n", "처음 이름", true, "a")], groups,
            [staleB, steadyC with { Mine = null }], currentCopies: copies2, currentCopyTitles: written);
        Check(untouchedHere.Renames!.Single().Title == "옛 이름", "an untouched copy that never agreed on its name takes the stale one");
        var agreedHere = ClaudeSessionSync.Compute(CurrentKey, null, [R("local_n", "처음 이름", true, "a")], groups,
            [staleB, steadyC], currentCopies: copies2, currentCopyTitles: written);
        Check(agreedHere.Renames!.Count == 0, "a name this account agreed on before is not undone by a stale one");
        var unpinnedOffer = Other(ReferenceKey, "B", [R("local_b", "제목", false, "a"), R("local_x", "다른 것", true, "x")], groups,
            elsewhere: [new Dictionary<string, ClaudeSessionLayout.Agreed> { ["local_b"] = A(false, true, null) }]);
        var pinnedHere = ClaudeSessionSync.Compute(CurrentKey, null, [R("local_n", "제목", true, "a")], groups, [unpinnedOffer],
            currentCopies: copies2);
        Check(pinnedHere.Pins!.Count == 0, "a copy pinned here stays pinned against a stale unpin");
        var filedHere = ClaudeSessionSync.Compute(CurrentKey, null, [R("local_n", "제목", false, "a")], In("local_n", "h"), [unpinnedOffer],
            currentCopies: copies2);
        Check(filedHere.Moves!.Count == 0, "a copy filed here stays in its group against a stale one");
    }

    private static void TestLayoutStore()
    {
        Check(ClaudeSessionSync.PlannedEventName(CurrentKey.ToUpperInvariant())
              == @"Local\CodexWeeklyUsageIndicator.SessionsPlanned." + CurrentAccount + "_" + CurrentOrg,
            "the event names the account the plan ran in");
        var root = Directory.CreateTempSubdirectory("gfs-session-layout-").FullName;
        try
        {
            Check(ClaudeSessionLayout.Load(root) is (null, false), "no layout yet");
            var left = DateTimeOffset.Parse("2026-10-13T01:02:03.4567891+09:00", CultureInfo.InvariantCulture);
            var layout = new ClaudeSessionLayout();
            layout.Accounts[CurrentKey] = new("서브", null);
            layout.Accounts[ReferenceKey] = new("메인", left);
            layout.Record(new(CurrentKey, ReferenceKey,
                new Dictionary<string, ClaudeSessionLayout.Agreed> { ["local_c"] = A(true, true, null, "제목"), ["local_gone"] = A(false) },
                new Dictionary<string, ClaudeSessionLayout.Agreed> { ["local_r"] = A(true, true, null, "제목") },
                new HashSet<string> { "local_c", "local_gone", "local_open" }, new HashSet<string> { "local_r" }));
            layout.Record(new(ReferenceKey, CurrentKey,
                new Dictionary<string, ClaudeSessionLayout.Agreed> { ["local_r"] = A(hasGroup: true, group: "대기") },
                new Dictionary<string, ClaudeSessionLayout.Agreed> { ["local_c"] = A(hasGroup: true, group: "대기"), ["local_open"] = A(hasGroup: true) },
                new HashSet<string> { "local_r" }, new HashSet<string> { "local_c", "local_open" }));
            layout.Save(root);
            var (loaded, exists) = ClaudeSessionLayout.Load(root);
            Check(exists && loaded is not null && loaded.Accounts[ReferenceKey] == new ClaudeSessionLayout.Account("메인", left)
                  && loaded.Accounts[CurrentKey].LeftAt is null, "accounts round-trip with their labels and when the app left them");
            var side = loaded!.Side(CurrentKey, ReferenceKey)!;
            Check(side["local_c"] == A(true, true, "대기", "제목") && loaded.Side(ReferenceKey, CurrentKey)!["local_r"] == A(true, true, "대기", "제목")
                  && !side.ContainsKey("local_gone") && side["local_open"] == A(hasGroup: true) && loaded.Side(CurrentKey, ThirdKey) is null,
                "agreements merge field by field over earlier ones, keep \"in no group\", forget sessions no longer listed, and stay per pair");
            File.WriteAllText(Path.Combine(root, ClaudeSessionLayout.FileName), "{\"accounts\":{\"x\":{}},\"pairs\":{}}");
            Check(ClaudeSessionLayout.Load(root) is (null, true), "a layout that cannot be read whole is reported, not taken as empty");
            File.WriteAllText(Path.Combine(root, ClaudeSessionLayout.FileName), "{\"accounts\":{},\"pairs\":{\"" + CurrentKey + "|" + ReferenceKey
                + "\":{\"" + CurrentKey + "\":{\"local_c\":{\"pinned\":\"yes\"}}}}}");
            Check(ClaudeSessionLayout.Load(root) is (null, true), "an agreement of an unfamiliar shape is not guessed at");
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

        var root = Directory.CreateTempSubdirectory("gfs-session-sync-pins-").FullName;
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
        var root = Directory.CreateTempSubdirectory("gfs-session-sync-transcripts-").FullName;
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
            Check(ClaudeSessionSync.Transcript(root, "..\\x") is null, "only transcript ids are looked up");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void Keep(string ledger, params (string Key, string Label)[] accounts)
    {
        var layout = new ClaudeSessionLayout();
        foreach (var (key, label) in accounts) layout.Accounts[key] = new(label, null);
        layout.Save(ledger);
    }

    private static void TestBuildFromDisk()
    {
        var root = Directory.CreateTempSubdirectory("gfs-session-sync-").FullName;
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
            var missingStorage = Path.Combine(root, "no-local-storage");
            var sources = new ClaudeSessionSync.Sources(sessions, missingStorage, idb, Path.Combine(root, "projects"), ledger);
            Check(ClaudeSessionSync.Build(sources, "local_nowhere").Status == "unknown_session", "an unknown session is reported");
            Check(ClaudeSessionSync.Build(sources, "local_self") is { Status: "widget_not_ready", Current: var current } && current == CurrentKey,
                "without what the accounts agreed on nothing is planned");
            File.WriteAllText(Path.Combine(ledger, ClaudeSessionLayout.FileName), "{\"accounts\":");
            Check(ClaudeSessionSync.Build(sources, "local_self").Status == "layout_unreadable", "a layout that cannot be read is reported");
            Keep(ledger, (CurrentKey, "서브"));
            Check(ClaudeSessionSync.Build(sources, "local_self") is { Status: "no_other_accounts", CurrentLabel: "서브" },
                "with no other account there is nothing to take");
            Keep(ledger, (CurrentKey, "서브"), (ThirdKey, "셋째"));
            Check(ClaudeSessionSync.Build(sources, "local_self").Status == "no_other_accounts",
                "an account without a session list here is passed over");
            Keep(ledger, (CurrentKey, "서브"), (ReferenceKey.ToUpperInvariant(), "메인"), (ThirdKey, "셋째"));
            var plan = ClaudeSessionSync.Build(sources, "local_self");
            Check(plan is { Status: "ok", CurrentLabel: "서브", Others: [{ Label: "메인", Matched: 1 }] }
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
            Check(ClaudeSessionSync.Build(sources, "local_self") is { Renames.Count: 0 } copied
                  && copied.Pins!.Single() is { Session: "local_self", Pinned: true },
                "the widget's copy in the other account gives the pin it has there, not its title");
            File.Delete(Path.Combine(ledger, "ledger.json"));
            var unpinned = ClaudeSessionSync.Build(sources with { IndexedDb = Path.Combine(root, "no-idb") }, "local_self");
            Check(unpinned.Pins!.Count == 0 && unpinned.Renames!.Count == 1 && unpinned.Warnings!.SequenceEqual(["groups_unreadable", "pins_unreadable"]),
                "without the pin list no pin is planned, never one taken from records");
            // An older state beside its continuation: listed when nothing was written to it after the next transcript began.
            var projects = Directory.CreateDirectory(Path.Combine(root, "projects", "E--p")).FullName;
            string Transcript(string created, params string[] entries)
            {
                var id = Guid.NewGuid().ToString("D");
                var path = Path.Combine(projects, id + ".jsonl");
                File.WriteAllLines(path, entries.Select(entry => entry.Split('@') is [var uuid, var at]
                    ? "{\"uuid\":\"" + uuid + "\",\"timestamp\":\"" + at + "\"}" : entry));
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
            File.AppendAllText(Path.Combine(projects, earlier + ".jsonl"), "{\"uuid\":\"00000000-0000-0000-0000-0000000000a3\",\"timestamp\":\"2026-10-03");
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
            Check(ClaudeSessionSync.Build(sources, "local_self").Status == "reference_unreadable",
                "until the widget can start from it, a plan says the old reference cannot be read");
        }
        finally { Directory.Delete(root, true); }
    }

    private static void TestGroupsFromLocalStorage()
    {
        var root = Directory.CreateTempSubdirectory("gfs-session-sync-ls-").FullName;
        try
        {
            var sessions = Path.Combine(root, "sessions"); var ledger = Directory.CreateDirectory(Path.Combine(root, "ledger")).FullName;
            void Write(string account, string org, string id, string cli)
            {
                var directory = Directory.CreateDirectory(Path.Combine(sessions, account, org)).FullName;
                File.WriteAllText(Path.Combine(directory, id + ".json"), new JsonObject { ["sessionId"] = id, ["title"] = "t", ["cliSessionId"] = cli }.ToJsonString());
            }
            Write(CurrentAccount, CurrentOrg, "local_c", "same"); Write(ReferenceAccount, ReferenceOrg, "local_r", "same");
            Keep(ledger, (CurrentKey, "서브"), (ReferenceKey, "메인"));
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
            var store = new ClaudeLocalStorage(db);
            Check(store.Write(store.Read(["dframe-store"]), new Dictionary<string, string> { ["dframe-store"] = state.ToJsonString() },
                Path.Combine(root, "backups"), () => true), "the fixture sidebar state is stored");
            var log = Path.Combine(db, "000005.log");
            ClaudeSessionSync.Plan plan;
            // The app holds its log open for writing while it runs.
            using (new FileStream(log, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite))
                plan = ClaudeSessionSync.Build(sources, "local_c");
            Check(plan.Status == "ok" && plan.CreateGroups!.SequenceEqual(["대기"]) && plan.Moves!.Single() is { Group: "대기" } move
                  && move.Sessions.SequenceEqual(["local_c"]) && plan.Warnings is null,
                "groups are read from the app's local storage while it is open, and a missing group is created");

            // After a plan, the widget learns what the two show alike; the first time, it starts from the old reference.
            var agreements = ClaudeSessionSync.Agreements(sources, CurrentKey, [ReferenceKey, ThirdKey]);
            Check(agreements is [{ Account: var mine, Other: var theirs } alike] && mine == CurrentKey && theirs == ReferenceKey
                  && alike.Mine["local_c"] == A(false, false, null, "t") && alike.Theirs["local_r"] == A(false, false, null, "t")
                  && alike.MyIds.SetEquals(["local_c"]) && alike.TheirIds.SetEquals(["local_r"]),
                "only what both show alike is agreed: the pin and title, not the group one of them changed");
            var baseline = ClaudeSessionSync.Baseline(sources, ReferenceKey, [CurrentKey, ReferenceKey]);
            Check(baseline is [{ Account: var account, Other: var reference } start] && account == CurrentKey && reference == ReferenceKey
                  && start.Mine["local_c"] == A(false, true, null, "t") && start.Theirs["local_r"] == A(false, true, null, "t"),
                "the first agreements take each other account's values, so what the old reference shows differently is its change");
            File.WriteAllText(Path.Combine(ledger, "ledger.json"), "{\"created\":[{\"dst\":\"" + ReferenceKey + "\",\"file\":\"local_r.json\"}]}");
            Check(ClaudeSessionSync.Baseline(sources, ReferenceKey, [CurrentKey]) is [{ Theirs.Count: 0 } copied] && copied.Mine.Count == 1,
                "a copy the widget made in the old reference gets no agreement there: the copies rule tells what it offers");
            File.WriteAllText(Path.Combine(ledger, "ledger.json"), "{\"created\":");
            Check(ClaudeSessionSync.Baseline(sources, ReferenceKey, [CurrentKey]) is null, "without the ledger nothing is started");
            File.Delete(Path.Combine(ledger, "ledger.json"));
            Check(ClaudeSessionSync.Agreements(sources with { IndexedDb = Path.Combine(root, "no-idb") }, CurrentKey, [ReferenceKey]) is null,
                "without the pin list nothing is learned");
            // A record that cannot be read now would look gone: nothing is learned from a list not read whole.
            var theirRecord = Path.Combine(sessions, ReferenceAccount, ReferenceOrg, "local_r.json");
            using (new FileStream(theirRecord, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                Check(ClaudeSessionSync.Agreements(sources, CurrentKey, [ReferenceKey]) is { Count: 0 },
                    "a pair whose other list cannot be read whole is passed over, its agreements kept");
                Check(ClaudeSessionSync.Baseline(sources, ReferenceKey, [CurrentKey]) is null, "nothing is started from a list not read whole");
            }
            using (new FileStream(Path.Combine(sessions, CurrentAccount, CurrentOrg, "local_c.json"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                Check(ClaudeSessionSync.Agreements(sources, CurrentKey, [ReferenceKey]) is null, "nothing is learned while this account's list is not read whole");
            File.WriteAllText(Path.Combine(sessions, ReferenceAccount, ReferenceOrg, "local_half.json"), "");
            Check(ClaudeSessionSync.Agreements(sources, CurrentKey, [ReferenceKey]) is { Count: 0 }, "a record being written is not taken as gone");
            File.Delete(Path.Combine(sessions, ReferenceAccount, ReferenceOrg, "local_half.json"));

            // Another account's scope that is missing offers no groups; one there but unfamiliar holds every group change.
            JsonArray Groups() => new(new JsonObject { ["id"] = "cg-1", ["name"] = "대기" });
            foreach (var other in new JsonNode?[] { null, new JsonObject(), new JsonObject { ["groups"] = "x", ["assignments"] = new JsonObject() },
                         new JsonObject { ["groups"] = new JsonArray(new JsonObject { ["id"] = "cg-1" }), ["assignments"] = new JsonObject() },
                         new JsonObject { ["groups"] = Groups() }, new JsonObject { ["groups"] = Groups(), ["assignments"] = null },
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
                Check(store.Write(store.Read(["dframe-store"]), new Dictionary<string, string> { ["dframe-store"] = changed.ToJsonString() },
                    Path.Combine(root, "backups"), () => true), "the fixture sidebar state is replaced");
                var unconfirmed = ClaudeSessionSync.Build(sources, "local_c");
                Check(unconfirmed.Moves!.Count == 0 && unconfirmed.CreateGroups!.Count == 0
                      && (other is null ? unconfirmed.Warnings is null : unconfirmed.Warnings!.SequenceEqual(["groups_unreadable"])),
                    "an unconfirmed scope of another account plans no group change");
            }
            // A log that ends inside a record (the app mid-write) is retried, then reported unconfirmed.
            // Header of a 100-byte full record, then only two of its bytes.
            using (var stream = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                stream.Write([0x10, 0x20, 0x30, 0x40, 0x64, 0x00, 0x01, 0x05, 0x06]);
            Check(!store.Read(["dframe-store"]).Clean, "the fixture log ends inside a record");
            var torn = ClaudeSessionSync.Build(sources, "local_c");
            Check(torn.Moves!.Count == 0 && torn.Warnings!.SequenceEqual(["groups_unreadable"]),
                "a torn log is not taken as the app's current groups");
        }
        finally { Directory.Delete(root, true); }
    }

    /// <summary>A minimal Chromium localStorage database: a manifest naming log 5 and a VERSION entry.</summary>
    private static string CreateDatabase(string directory)
    {
        var db = Directory.CreateDirectory(directory).FullName;
        static byte[] Varint(ulong value) { var bytes = new List<byte>(); while (value >= 0x80) { bytes.Add((byte)(value | 0x80)); value >>= 7; } bytes.Add((byte)value); return bytes.ToArray(); }
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

    /// <summary>The Code tab's pin list row of an IndexedDB database (object store 1, data index 1).</summary>
    private static byte[] PinKey(byte database) => [0x00, database, 0x01, 0x01, .. ClaudeSidebarPins.EncodedKey];

    /// <summary>A pin list as the app stores it: record version, Blink's envelope, then V8's string.</summary>
    private static byte[] PinValue(IEnumerable<string> ids, bool twoByte = false, bool trailer = true, string? json = null)
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
    private static void AppendRaw(string db, ulong sequence, byte[] key, byte[]? value)
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
