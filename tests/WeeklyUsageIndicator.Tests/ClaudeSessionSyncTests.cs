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
        Check(plan.Matched == 4 && plan.Unmatched == 1 && plan.Pins!.All(pin => pin.Session != "local_c4"),
            "a session only this account has is left alone");
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
        var blind = ClaudeSessionSync.Compute(CurrentKey, null, ReferenceKey, null, current, reference, null, null, ["groups_unreadable"]);
        Check(blind.CreateGroups!.Count == 0 && blind.Moves!.Count == 0 && blind.Warnings!.Single() == "groups_unreadable"
              && blind.Pins!.Select(pin => pin.Session).SequenceEqual(["local_c2"]), "unreadable groups plan titles and pins only");
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
            Write(CurrentAccount, CurrentOrg, "local_self", new() { ["title"] = "지금", ["cliSessionId"] = "AAA", ["isStarred"] = false });
            Write(CurrentAccount, CurrentOrg, "local_gone", new() { ["title"] = "보관됨", ["cliSessionId"] = "bbb", ["isArchived"] = true });
            Write(ReferenceAccount, ReferenceOrg, "local_ref", new() { ["title"] = "기준 이름", ["cliSessionId"] = "aaa", ["isStarred"] = true });
            Write(ReferenceAccount, ReferenceOrg, "local_ref_gone", new() { ["title"] = "보관된 기준", ["cliSessionId"] = "bbb" });
            var missingStorage = Path.Combine(root, "no-local-storage");
            Check(ClaudeSessionSync.Build(sessions, missingStorage, ledger, "local_nowhere").Status == "unknown_session", "an unknown session is reported");
            var none = ClaudeSessionSync.Build(sessions, missingStorage, ledger, "local_self");
            Check(none.Status == "no_reference" && none.Current == CurrentKey, "without a reference nothing is planned");
            var labels = new Dictionary<string, string> { [CurrentKey] = "서브", [ReferenceKey] = "메인" };
            new ClaudeSessionReference(CurrentKey, "서브", "p2", DateTimeOffset.UtcNow, labels).Save(ledger);
            Check(ClaudeSessionSync.Build(sessions, missingStorage, ledger, "local_self") is { Status: "is_reference", CurrentLabel: "서브" },
                "the reference account itself has nothing to copy");
            new ClaudeSessionReference("0998b264-7315-4fdb-8634-000000000000/7f98dbbd-af39-46bf-9609-cfd3972b3633", "x", null,
                DateTimeOffset.UtcNow, labels).Save(ledger);
            Check(ClaudeSessionSync.Build(sessions, missingStorage, ledger, "local_self").Status == "reference_missing",
                "a reference without a session list is reported");
            new ClaudeSessionReference(ReferenceKey.ToUpperInvariant(), "메인", "p1", DateTimeOffset.UtcNow, labels).Save(ledger);
            var loaded = ClaudeSessionReference.Load(ledger);
            Check(loaded is { Account: var account, Label: "메인", ProfileId: "p1" } && account == ReferenceKey && loaded.Labels[CurrentKey] == "서브",
                "the reference round-trips with its labels");
            var plan = ClaudeSessionSync.Build(sessions, missingStorage, ledger, "local_self");
            Check(plan is { Status: "ok", CurrentLabel: "서브", ReferenceLabel: "메인", Matched: 1, Unmatched: 0 }
                  && plan.Renames!.Single() is { Session: "local_self", Title: "기준 이름", From: "지금" }
                  && plan.Pins!.Single() is { Session: "local_self", Pinned: true } && plan.Moves!.Count == 0
                  && plan.Warnings!.SequenceEqual(["groups_unreadable"]),
                "records are read case-insensitively, archived ones skipped, and without local storage groups are reported unconfirmed");
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
                plan = ClaudeSessionSync.Build(sessions, db, ledger, "local_c");
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
                var unconfirmed = ClaudeSessionSync.Build(sessions, db, ledger, "local_c");
                Check(unconfirmed.Moves!.Count == 0 && unconfirmed.CreateGroups!.Count == 0 && unconfirmed.Warnings!.SequenceEqual(["groups_unreadable"]),
                    "an unconfirmed reference scope plans no group change");
            }
            // A log that ends inside a record (the app mid-write) is retried, then reported unconfirmed.
            // Header of a 100-byte full record, then only two of its bytes.
            using (var stream = new FileStream(log, FileMode.Append, FileAccess.Write, FileShare.ReadWrite))
                stream.Write([0x10, 0x20, 0x30, 0x40, 0x64, 0x00, 0x01, 0x05, 0x06]);
            Check(!store.Read(["dframe-store"]).Clean, "the fixture log ends inside a record");
            var torn = ClaudeSessionSync.Build(sessions, db, ledger, "local_c");
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

    private static void Check(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
