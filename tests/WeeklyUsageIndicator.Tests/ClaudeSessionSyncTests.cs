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

    internal static Task RunAsync()
    {
        TestCombinedUsage();
        TestPlan();
        TestOlderStates();
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
        var plan = ClaudeSessionSync.Compute(CurrentKey, "서브", ReferenceKey, "메인", current, reference, currentLayout, referenceLayout);
        Check(plan.Status == "ok" && plan.CreateGroups!.SequenceEqual(["B", "C"]), "groups the reference has are created by name, existing ones reused");
        Check(plan.Moves!.Count == 3 && plan.Moves.Single(move => move.Group == "B").Sessions.SequenceEqual(["local_c1"])
              && plan.Moves.Single(move => move.Group == "A").Sessions.SequenceEqual(["local_c3"])
              && plan.Moves.Single(move => move.Group is null).Sessions.SequenceEqual(["local_c5"]),
            "sessions follow the reference's group, a continuation included, and leave a group the reference does not use");
        Check(plan.Pins!.Select(pin => (pin.Session, pin.Pinned)).OrderBy(pin => pin.Session).SequenceEqual([("local_c1", true), ("local_c2", true)]),
            "pins follow the reference; one moved into a group is pinned again because the move unpins it");
        Check(plan.Renames!.Select(rename => (rename.Session, rename.Title)).OrderBy(rename => rename.Session)
                  .SequenceEqual([("local_c1", "새 이름"), ("local_c3", "원래"), ("local_c5", "앞서 이어감")]),
            "titles follow the reference, matched by transcript id or as a continuation either way");
        Check(plan.Matched == 4 && plan.Unmatched == 1 && plan.Pins!.All(pin => pin.Session != "local_c4")
              && plan.OnlyHere!.SequenceEqual(["한쪽에만"]) && plan.OnlyThere!.Count == 0,
            "a session only this account has is left alone and named");
        // A dangling group reference on either side leaves that session where it is.
        var dangling = ClaudeSessionSync.Compute(CurrentKey, null, ReferenceKey, null, current, reference, currentLayout,
            referenceLayout with { Assignments = new Dictionary<string, string> { ["local_r1"] = "gone", ["local_r3"] = "ra" } });
        Check(dangling.Moves!.All(move => !move.Sessions.Contains("local_c1")) && dangling.Pins!.All(pin => pin.Session != "local_c1"),
            "an assignment to a group the reference does not list moves nothing");
        // One reference record serves one session: an older state or a branch of a matched conversation is left alone.
        var older = ClaudeSessionSync.Compute(CurrentKey, null, ReferenceKey, null,
            [Record("local_now", "지금", false, "k2", "k"), Record("local_old", "옛 상태", false, "k"), Record("local_branch", "갈래", false, "b2", "k2")],
            [Record("local_ref", "기준", false, "k2", "k")], null, null);
        Check(older.Renames!.Select(rename => rename.Session).SequenceEqual(["local_now"]) && older.Matched == 1 && older.Unmatched == 2,
            "an older state or a branch of an already matched conversation is not renamed after it");
        var blind = ClaudeSessionSync.Compute(CurrentKey, null, ReferenceKey, null, current, reference, null, null, warnings: ["groups_unreadable"]);
        Check(blind.CreateGroups!.Count == 0 && blind.Moves!.Count == 0 && blind.Warnings!.Single() == "groups_unreadable"
              && blind.Pins!.Select(pin => pin.Session).SequenceEqual(["local_c2"]), "unreadable groups plan titles and pins only");
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
        var plan = ClaudeSessionSync.Compute(CurrentKey, "서브", ReferenceKey, "메인", current, reference, none, referenceLayout,
            lineage: lineage);
        Check(plan.CreateGroups!.SequenceEqual(["G"]) && plan.Moves!.Single() is { Group: "G" } move && move.Sessions.SequenceEqual(["local_new"])
              && plan.Pins!.Single() is { Session: "local_new", Pinned: true } && plan.Matched == 1 && plan.Renames!.Count == 0,
            "the reference's older state is seen through: its pin and group go to the newest state there and here");
        Check(plan.Archive!.Single() is { Session: "local_old", Pinned: true, Newest: "local_new", Ready: false },
            "an older state waits until its newest state holds what it keeps");
        var moved = new ClaudeSessionSync.Layout([new("g2", "G")], new Dictionary<string, string> { ["local_new"] = "g2" });
        var after = ClaudeSessionSync.Compute(CurrentKey, "서브", ReferenceKey, "메인",
            [current[0], current[1] with { Pinned = true }], reference, moved, referenceLayout, lineage: lineage);
        Check(after.Moves!.Count == 0 && after.Pins!.Count == 0 && after.Archive!.Single() is { Ready: true, Pinned: true },
            "once the newest state holds the pin and group, the older state is ready (to be unpinned, then archived)");

        // A conversation only this account lists keeps what its older state had.
        var ownLayout = new ClaudeSessionSync.Layout([new("h", "H")], new Dictionary<string, string> { ["local_c1"] = "h" });
        var alone = ClaudeSessionSync.Compute(CurrentKey, null, ReferenceKey, null,
            [R("local_c1", "여기만", true, "c1", [], 10), R("local_c2", "여기만", false, "c2", ["c1"], 20)],
            [R("local_z", "다른 것", true, "z", [])], ownLayout, none, lineage: lineage);
        Check(alone.Moves!.Single() is { Group: "H" } kept && kept.Sessions.SequenceEqual(["local_c2"])
              && alone.Pins!.Single() is { Session: "local_c2", Pinned: true } && alone.Archive!.Single() is { Ready: false }
              && alone.OnlyHere!.SequenceEqual(["여기만"]) && alone.Matched == 0,
            "an older state's pin and group go to its newest state even when the reference does not list the conversation");

        // The reference itself only clears its older states, handing their pin and group to the newest state first.
        var mine = new ClaudeSessionSync.Layout([new("g", "G")], new Dictionary<string, string> { ["local_old"] = "g" });
        var self = ClaudeSessionSync.Compute(CurrentKey, "서브", CurrentKey, "서브", current, current, mine, mine, lineage: lineage);
        Check(self is { Status: "is_reference", Matched: null, Unmatched: null, OnlyHere: null, OnlyThere: null }
              && self.Archive!.Single() is { Session: "local_old", Ready: false } && self.CreateGroups!.Count == 0
              && self.Moves!.Single() is { Group: "G" } handed && handed.Sessions.SequenceEqual(["local_new"])
              && self.Pins!.Single() is { Session: "local_new", Pinned: true } && self.Sidebars!.Count == 1,
            "the reference account keeps one record per conversation, the newest holding the pin and group");

        // A reference without a single pin looks wiped: pins are kept, a pinned session is not moved into a group, and
        // no older state goes.
        var wiped = ClaudeSessionSync.Compute(CurrentKey, null, ReferenceKey, null,
            [R("local_p", "고정", true, "p", []), R("local_q", "보통", false, "q", []), .. current],
            [R("local_rp", "고정", false, "p", []), R("local_rq", "보통", false, "q", []), R("local_r_new", "대화", false, "a2", ["a1"], 20)],
            none, new ClaudeSessionSync.Layout([new("g", "G")], new Dictionary<string, string> { ["local_rp"] = "g", ["local_rq"] = "g" }),
            lineage: lineage);
        Check(wiped.Pins!.Count == 0 && wiped.Warnings!.SequenceEqual(["reference_pins_empty"])
              && wiped.Moves!.Single() is { Group: "G" } stay && stay.Sessions.SequenceEqual(["local_q"])
              && wiped.Archive!.Single() is { Ready: false }, "an empty reference pin list unpins nothing and archives nothing");

        // Unreadable pins plan no pins and no groups (a move could unpin); unreadable groups hold archiving too.
        var blind = ClaudeSessionSync.Compute(CurrentKey, null, ReferenceKey, null, current, reference, none, referenceLayout,
            pinsKnown: false, lineage: lineage, warnings: ["pins_unreadable"]);
        Check(blind.Pins!.Count == 0 && blind.Moves!.Count == 0 && blind.CreateGroups!.Count == 0
              && blind.Archive!.Single() is { Pinned: false, Ready: false } && blind.Sidebars!.All(sidebar => sidebar.Pinned is null),
            "unknown pins leave pins, groups and older states alone");
        var ungrouped = ClaudeSessionSync.Compute(CurrentKey, null, ReferenceKey, null,
            [current[0], current[1] with { Pinned = true }], reference, null, null, lineage: lineage, warnings: ["groups_unreadable"]);
        Check(ungrouped.Pins!.Count == 0 && ungrouped.Archive!.Single() is { Ready: false },
            "unknown groups hold the older state: its group could not be handed on");
        // A group that cannot be resolved on either state leaves the hand-over unproven.
        var mineDangling = new ClaudeSessionSync.Layout([new("h", "H")],
            new Dictionary<string, string> { ["local_old"] = "h", ["local_new"] = "gone" });
        var dangling = ClaudeSessionSync.Compute(CurrentKey, null, CurrentKey, null,
            [current[0] with { Pinned = false }, current[1]], [current[0] with { Pinned = false }, current[1]],
            mineDangling, mineDangling, lineage: lineage);
        Check(dangling.Moves!.Count == 0 && dangling.Archive!.Single() is { Ready: false },
            "a newest state filed under a group the scope does not list is not taken as holding the older state's group");
        var oldDangling = new ClaudeSessionSync.Layout([new("h", "H")], new Dictionary<string, string> { ["local_old"] = "gone" });
        var unknownOld = ClaudeSessionSync.Compute(CurrentKey, null, CurrentKey, null,
            [current[0] with { Pinned = false }, current[1]], [current[0] with { Pinned = false }, current[1]],
            oldDangling, oldDangling, lineage: lineage);
        Check(unknownOld.Archive!.Single() is { Ready: false }, "an older state in a group the scope does not list is kept");

        // The sidebars read as the app shows them: pinned sessions under Pinned only, everything by title.
        var view = ClaudeSessionSync.Compute(CurrentKey, "서브", ReferenceKey, "메인",
            [R("local_1", "나중", true, "1", []), R("local_2", "가운데", false, "2", []), R("local_3", "처음", true, "3", [])],
            [], new ClaudeSessionSync.Layout([new("g2", "둘"), new("g1", "하나")],
                new Dictionary<string, string> { ["local_1"] = "g1", ["local_2"] = "g1" }), null);
        Check(view.Sidebars![0] is { Label: "서브", Pinned: { } pinnedTitles, Groups: { } groupViews }
              && pinnedTitles.SequenceEqual(["나중", "처음"]) && groupViews.Select(group => group.Name).SequenceEqual(["둘", "하나"])
              && groupViews[1].Sessions.SequenceEqual(["가운데"]) && view.Sidebars[1].Groups is null,
            "sidebars list pins and groups by title, a pinned session only under Pinned");
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
            // Record pins are stale copies and are ignored: the app's pin list says the reference's session is pinned.
            Write(CurrentAccount, CurrentOrg, "local_self", new() { ["title"] = "지금", ["cliSessionId"] = "AAA", ["isStarred"] = true });
            Write(CurrentAccount, CurrentOrg, "local_gone", new() { ["title"] = "보관됨", ["cliSessionId"] = "bbb", ["isArchived"] = true });
            Write(ReferenceAccount, ReferenceOrg, "local_ref", new() { ["title"] = "기준 이름", ["cliSessionId"] = "aaa", ["isStarred"] = false });
            Write(ReferenceAccount, ReferenceOrg, "local_ref_gone", new() { ["title"] = "보관된 기준", ["cliSessionId"] = "bbb" });
            var idb = CreateDatabase(Path.Combine(root, "idb"));
            AppendRaw(idb, 42, PinKey(5), PinValue(["local_ref"]));
            var missingStorage = Path.Combine(root, "no-local-storage");
            var sources = new ClaudeSessionSync.Sources(sessions, missingStorage, idb, Path.Combine(root, "projects"), ledger);
            Check(ClaudeSessionSync.Build(sources, "local_nowhere").Status == "unknown_session", "an unknown session is reported");
            var none = ClaudeSessionSync.Build(sources, "local_self");
            Check(none.Status == "no_reference" && none.Current == CurrentKey, "without a reference nothing is planned");
            var labels = new Dictionary<string, string> { [CurrentKey] = "서브", [ReferenceKey] = "메인" };
            new ClaudeSessionReference(CurrentKey, "서브", "p2", DateTimeOffset.UtcNow, labels).Save(ledger);
            Check(ClaudeSessionSync.Build(sources, "local_self") is { Status: "is_reference", CurrentLabel: "서브" } own
                  && own.Pins!.Count == 0 && own.Renames!.Count == 0 && own.Archive!.Count == 0,
                "the reference account itself has nothing to copy");
            new ClaudeSessionReference("0998b264-7315-4fdb-8634-000000000000/7f98dbbd-af39-46bf-9609-cfd3972b3633", "x", null,
                DateTimeOffset.UtcNow, labels).Save(ledger);
            Check(ClaudeSessionSync.Build(sources, "local_self").Status == "reference_missing",
                "a reference without a session list is reported");
            new ClaudeSessionReference(ReferenceKey.ToUpperInvariant(), "메인", "p1", DateTimeOffset.UtcNow, labels).Save(ledger);
            var loaded = ClaudeSessionReference.Load(ledger);
            Check(loaded is { Account: var account, Label: "메인", ProfileId: "p1" } && account == ReferenceKey && loaded.Labels[CurrentKey] == "서브",
                "the reference round-trips with its labels");
            var plan = ClaudeSessionSync.Build(sources, "local_self");
            Check(plan is { Status: "ok", CurrentLabel: "서브", ReferenceLabel: "메인", Matched: 1, Unmatched: 0 }
                  && plan.Renames!.Single() is { Session: "local_self", Title: "기준 이름", From: "지금" }
                  && plan.Pins!.Single() is { Session: "local_self", Pinned: true } && plan.Moves!.Count == 0
                  && plan.Warnings!.SequenceEqual(["groups_unreadable"]),
                "records are read case-insensitively, archived ones skipped, pins come from the app's pin list, and without local storage groups are reported unconfirmed");
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
            File.WriteAllText(Path.Combine(ledger, ClaudeSessionReference.FileName), "{\"account\":\"not-an-account\"}");
            Check(ClaudeSessionReference.Load(ledger) is null, "a malformed reference is ignored");
            ClaudeSessionReference.Clear(ledger);
            Check(!File.Exists(Path.Combine(ledger, ClaudeSessionReference.FileName)), "the reference can be cleared");
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
            new ClaudeSessionReference(ReferenceKey, "메인", null, DateTimeOffset.UtcNow, new Dictionary<string, string>()).Save(ledger);
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
            // A reference scope that is missing or unfamiliar is unconfirmed, never read as "no groups".
            JsonArray Groups() => new(new JsonObject { ["id"] = "cg-1", ["name"] = "대기" });
            foreach (var reference in new JsonNode?[] { null, new JsonObject(), new JsonObject { ["groups"] = "x", ["assignments"] = new JsonObject() },
                         new JsonObject { ["groups"] = new JsonArray(new JsonObject { ["id"] = "cg-1" }), ["assignments"] = new JsonObject() },
                         new JsonObject { ["groups"] = Groups() }, new JsonObject { ["groups"] = Groups(), ["assignments"] = null },
                         new JsonObject { ["assignments"] = new JsonObject() }, new JsonObject { ["groups"] = null, ["assignments"] = new JsonObject() } })
            {
                var other = (JsonObject)state.DeepClone();
                var scopes = other["state"]!["customGroupsByScope"]!.AsObject();
                scopes[CurrentKey] = new JsonObject
                {
                    ["groups"] = new JsonArray(new JsonObject { ["id"] = "cg-9", ["name"] = "내 그룹" }),
                    ["assignments"] = new JsonObject { ["code:local_c"] = "cg-9" }
                };
                if (reference is null) scopes.Remove(ReferenceKey); else scopes[ReferenceKey] = reference;
                Check(store.Write(store.Read(["dframe-store"]), new Dictionary<string, string> { ["dframe-store"] = other.ToJsonString() },
                    Path.Combine(root, "backups"), () => true), "the fixture sidebar state is replaced");
                var unconfirmed = ClaudeSessionSync.Build(sources, "local_c");
                Check(unconfirmed.Moves!.Count == 0 && unconfirmed.CreateGroups!.Count == 0 && unconfirmed.Warnings!.SequenceEqual(["groups_unreadable"]),
                    "an unconfirmed reference scope plans no group change");
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
