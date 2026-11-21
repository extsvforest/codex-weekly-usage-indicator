using System.Text;
using System.Text.Json.Nodes;
using WeeklyUsageIndicator;

/// <summary>Aligning the accounts' scheduled-task lists, in a fake profile folder only (GFS_ACCOUNT_TEST_ROOT). Task ids,
/// accounts and paths are made up.</summary>
internal static class ClaudeScheduledTasksTests
{
    internal static Task RunAsync()
    {
        TestFirstPassTakesStartingPointOnly();
        TestCreationSpreads();
        TestNewestChangeWins();
        TestDeletionSpreads();
        TestRunTimesCarryNewest();
        TestPermissionsAndAccountFieldsStay();
        TestUnknownFieldsKept();
        TestUnreadableListsNeitherWrittenNorCounted();
        TestFailedReadIsNoDeletion();
        TestVanishedListIsNoDeletion();
        TestListTheAppCouldNotLoadIsNoDeletion();
        TestWritesAreAtomic();
        TestBackupsKept();
        TestMissingDefinitionNotCopied();
        TestLostWriteIsNoChange();
        TestSeed();
        TestPlanWritesNothing();
        TestServiceWritesShownListOnlyAfterClose();
        TestServiceSeedDefersShownList();
        TestServiceStopsOnUnreviewedVersion();
        TestProbe();
        return Task.CompletedTask;
    }

    private static readonly DateTimeOffset T0 = DateTimeOffset.UtcNow;

    private static JsonObject Entry(string id, string cron = "0 9 * * *", Action<JsonObject>? extra = null)
    {
        var entry = new JsonObject
        {
            ["id"] = id, ["displayName"] = "Task " + id, ["cronExpression"] = cron, ["enabled"] = true,
            ["filePath"] = @"X:\made-up\scheduled-tasks\" + id + @"\SKILL.md", ["createdAt"] = 1763600000000,
            ["model"] = "made-up-model", ["permissionMode"] = "bypassPermissions", ["permissionModeCarried"] = true
        };
        extra?.Invoke(entry);
        return entry;
    }

    private static JsonObject List(params JsonObject[] entries) => new() { ["scheduledTasks"] = new JsonArray(entries) };

    private static void TestFirstPassTakesStartingPointOnly()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Define("only-a");
        fixture.Write(a, List(Entry("only-a")));
        fixture.Write(b, List());
        var before = fixture.Bytes(b);
        var sync = fixture.Sync();
        var first = sync.Pass(fixture.Accounts, _ => true, T0);
        Check(first.First && first.Written == 0 && fixture.Bytes(b).SequenceEqual(before) && File.Exists(Path.Combine(fixture.Store, "state.json")),
            "the first pass only takes what is there as the starting point");
        var second = sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(1));
        Check(!second.First && second.Written == 0 && fixture.Bytes(b).SequenceEqual(before),
            "a task one account held before the starting point is not spread by the next pass either");
        var state = Path.Combine(fixture.Store, "state.json");
        var kept = File.ReadAllBytes(state);
        sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(2));
        Check(File.ReadAllBytes(state).SequenceEqual(kept), "a pass that finds nothing new leaves the kept state as it is");
        sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(40));
        Check(!File.ReadAllBytes(state).SequenceEqual(kept), "until the times it keeps get old");
    }

    private static void TestCreationSpreads()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief");
        fixture.Write(a, List()); fixture.Write(b, List()); fixture.Write(c, List(Entry("kept-c")));
        fixture.Define("kept-c");
        var sync = fixture.Sync();
        sync.Pass(fixture.Accounts, _ => true, T0);
        fixture.Write(a, List(Entry("brief")));
        var pass = sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(1));
        var inB = fixture.Task(b, "brief"); var inC = fixture.Task(c, "brief");
        Check(pass.Written == 2 && inB is not null && inC is not null && fixture.Ids(c).SequenceEqual(["kept-c", "brief"])
              && inB["cronExpression"]!.GetValue<string>() == "0 9 * * *",
            "a task made in A after the starting point reaches B and C, and C keeps its own task");
        Check(sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(2)).Written == 0, "the lists written are not taken for changes of their own");
    }

    private static void TestNewestChangeWins()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief");
        foreach (var account in fixture.Accounts) fixture.Write(account, List(Entry("brief")));
        var sync = fixture.Sync();
        sync.Pass(fixture.Accounts, _ => true, T0);
        fixture.Write(a, List(Entry("brief", "0 7 * * *")), T0.AddSeconds(10));
        fixture.Write(b, List(Entry("brief", "0 8 * * *")), T0.AddSeconds(20));
        sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(1));
        Check(new[] { a, b, c }.All(account => fixture.Task(account, "brief")!["cronExpression"]!.GetValue<string>() == "0 8 * * *"),
            "two accounts changed the same task: the newer change (B) goes to every account");
        fixture.Write(c, List(Entry("brief", "0 6 * * *")), T0.AddSeconds(70));
        sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(2));
        Check(new[] { a, b, c }.All(account => fixture.Task(account, "brief")!["cronExpression"]!.GetValue<string>() == "0 6 * * *"),
            "and a later change in C after that");
    }

    private static void TestDeletionSpreads()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("old", "brief");
        foreach (var account in fixture.Accounts) fixture.Write(account, List(Entry("old"), Entry("brief")));
        var sync = fixture.Sync();
        sync.Pass(fixture.Accounts, _ => true, T0);
        fixture.Write(b, List(Entry("brief")));
        sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(1));
        Check(new[] { a, b, c }.All(account => fixture.Ids(account).SequenceEqual(["brief"])), "a deletion in B removes the task everywhere");
        fixture.Write(c, List(Entry("brief"), Entry("old", "0 5 * * *")), T0.AddSeconds(90));
        sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(2));
        Check(new[] { a, b }.All(account => fixture.Task(account, "old")?["cronExpression"]?.GetValue<string>() == "0 5 * * *"),
            "made again later, the task comes back everywhere (the newest change wins over the deletion)");
    }

    private static void TestRunTimesCarryNewest()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief");
        fixture.Write(a, List(Entry("brief", extra: entry => { entry["lastRunAt"] = "2026-11-19T00:00:05.000Z"; entry["lastScheduledFor"] = "2026-11-19T00:00:00.000Z"; })));
        fixture.Write(b, List(Entry("brief", extra: entry => { entry["lastRunAt"] = "2026-11-20T12:00:07.000Z"; entry["lastScheduledFor"] = "2026-11-20T12:00:00.000Z"; })));
        fixture.Write(c, List(Entry("brief")));
        var sync = fixture.Sync();
        sync.Pass(fixture.Accounts, _ => true, T0);
        var pass = sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(1));
        Check(pass.Written == 2 && new[] { a, b, c }.All(account => fixture.Task(account, "brief") is { } entry
                  && entry["lastRunAt"]!.GetValue<string>() == "2026-11-20T12:00:07.000Z"
                  && entry["lastScheduledFor"]!.GetValue<string>() == "2026-11-20T12:00:00.000Z"),
            "the newest run times reach every account (so a slot run in B is not caught up in A or C), and B is not written");
        fixture.Write(c, List(Entry("brief", extra: entry => { entry["lastRunAt"] = "2026-11-21T00:00:03.000Z"; entry["lastScheduledFor"] = "2026-11-21T00:00:00.000Z"; })));
        sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(2));
        Check(fixture.Task(a, "brief")!["lastScheduledFor"]!.GetValue<string>() == "2026-11-21T00:00:00.000Z"
              && fixture.Task(b, "brief")!["lastScheduledFor"]!.GetValue<string>() == "2026-11-21T00:00:00.000Z",
            "a run in C moves them on, and no run time goes back");
    }

    private static void TestPermissionsAndAccountFieldsStay()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("carried", "plain");
        fixture.Write(a, List());
        fixture.Write(b, List(Entry("plain", extra: entry => { entry["importedFrom"] = "made-up-origin"; entry["observedToolUse"] = new JsonObject { ["promptHash"] = "b" }; })));
        var skips = new JsonObject { ["plain"] = new JsonArray(new JsonObject { ["at"] = 1, ["reason"] = "per_task_limit" }) };
        fixture.Write(c, new JsonObject
        {
            ["scheduledTasks"] = new JsonArray(Entry("plain")), ["recordedSkips"] = skips,
            ["runRetries"] = new JsonObject { ["plain"] = new JsonObject { ["slot"] = "s", ["attempts"] = 1, ["notBefore"] = null } }
        });
        var sync = fixture.Sync();
        sync.Pass(fixture.Accounts, _ => true, T0);
        var made = new JsonObject
        {
            ["scheduledTasks"] = new JsonArray(
                Entry("carried", extra: entry => { entry["importedFrom"] = "a-origin"; entry["observedToolUse"] = new JsonObject { ["promptHash"] = "a" }; }),
                Entry("plain", "0 4 * * *", entry => { entry["permissionMode"] = "acceptEdits"; entry.Remove("permissionModeCarried"); })),
            ["recordedSkips"] = new JsonObject { ["carried"] = new JsonArray(new JsonObject { ["at"] = 2, ["reason"] = "global_limit" }) },
            ["runRetries"] = new JsonObject { ["carried"] = new JsonObject { ["slot"] = "x", ["attempts"] = 2, ["notBefore"] = null } }
        };
        fixture.Write(a, made);
        sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(1));
        var carried = fixture.Task(b, "carried")!; var plainB = fixture.Task(b, "plain")!; var listC = fixture.Read(c);
        Check(carried["permissionMode"]!.GetValue<string>() == "bypassPermissions" && carried["permissionModeCarried"]!.GetValue<bool>()
              && plainB["permissionMode"]!.GetValue<string>() == "acceptEdits" && !plainB.ContainsKey("permissionModeCarried"),
            "both permission fields go as they are: together when carried, without the carried mark when the change had none");
        Check(!carried.ContainsKey("importedFrom") && !carried.ContainsKey("observedToolUse")
              && plainB["importedFrom"]!.GetValue<string>() == "made-up-origin" && plainB["observedToolUse"]!["promptHash"]!.GetValue<string>() == "b",
            "an import origin and run observations are not copied, and each account keeps its own");
        Check(!fixture.Read(b).ContainsKey("runRetries") && !fixture.Read(b).ContainsKey("recordedSkips")
              && JsonNode.DeepEquals(listC["recordedSkips"], skips) && listC["runRetries"]!["plain"]!["slot"]!.GetValue<string>() == "s"
              && listC["runRetries"]!.AsObject().Count == 1,
            "retries and recorded skips stay with the account they belong to");
    }

    private static void TestUnknownFieldsKept()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Define("brief", "own");
        fixture.Write(a, List());
        fixture.Write(b, new JsonObject
        {
            ["scheduledTasks"] = new JsonArray(Entry("own", extra: entry => entry["laterField"] = new JsonObject { ["x"] = 1 })),
            ["sundayAliasBoundaryStamped"] = true, ["dayFieldsOrBoundaryStamped"] = true, ["laterTopLevel"] = new JsonArray(1, 2)
        });
        var sync = fixture.Sync();
        sync.Pass(fixture.Accounts, _ => true, T0);
        fixture.Write(a, List(Entry("brief", extra: entry => entry["newerEntryField"] = "kept")));
        sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(1));
        var list = fixture.Read(b);
        Check(list["sundayAliasBoundaryStamped"]!.GetValue<bool>() && list["dayFieldsOrBoundaryStamped"]!.GetValue<bool>()
              && list["laterTopLevel"]!.AsArray().Count == 2 && fixture.Task(b, "own")!["laterField"]!["x"]!.GetValue<int>() == 1
              && fixture.Task(b, "brief")!["newerEntryField"]!.GetValue<string>() == "kept",
            "the list's other fields and fields this widget does not know stay, in the list and in entries");
    }

    private static void TestUnreadableListsNeitherWrittenNorCounted()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief", "other");
        fixture.Write(a, List(Entry("other"))); fixture.Write(b, List(Entry("other"))); fixture.Write(c, List(Entry("other")));
        var sync = fixture.Sync();
        sync.Pass(fixture.Accounts, _ => true, T0);
        // One entry the app would refuse fails the whole list for it: that list is neither written nor read for changes.
        File.WriteAllText(fixture.PathOf(c), "{\"scheduledTasks\":[{\"id\":\"other\",\"enabled\":\"yes\",\"filePath\":\"x\",\"createdAt\":1}]}");
        var broken = fixture.Bytes(c);
        fixture.Write(a, List(Entry("other"), Entry("brief")));
        var pass = sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(1));
        Check(pass.Unreadable.SequenceEqual(["C"]) && fixture.Bytes(c).SequenceEqual(broken) && fixture.Task(b, "brief") is not null
              && fixture.Task(a, "other") is not null && fixture.Task(b, "other") is not null,
            "a list that does not read is not written, and what it lacks is not taken for deleted");
        foreach (var text in new[] { "{\"scheduledTasks\":[]", "\uFEFF{\"scheduledTasks\":[]}", "{\"scheduledTasks\":[{\"id\":\"x\",\"enabled\":true,\"filePath\":\"x\",\"createdAt\":1,\"cwd\":null}]}",
                     "{\"scheduledTasks\":[{\"id\":\"x\",\"enabled\":true,\"filePath\":\"x\",\"createdAt\":1,\"permissionMode\":\"yolo\"}]}" })
            Check(ClaudeScheduledRegistry.Parse("k", "p", Encoding.UTF8.GetBytes(text), null).Status == ClaudeScheduledReadStatus.Unreadable,
                "unreadable for the app as well: " + text[..Math.Min(40, text.Length)]);
        Check(ClaudeScheduledRegistry.Parse("k", "p", Encoding.UTF8.GetBytes(
                "{\"scheduledTasks\":[{\"id\":\"x\",\"enabled\":true,\"filePath\":\"x\",\"createdAt\":1,\"place\":5,\"importedFrom\":null}],\"runRetries\":7}"), null)
                  .Status == ClaudeScheduledReadStatus.Ok, "fields the app lets fall back to nothing do not fail the list");
        fixture.Write(c, List(Entry("other")));
        sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(2));
        Check(fixture.Ids(c).SequenceEqual(["other", "brief"]), "once it reads again it gets what it missed");
    }

    private static void TestFailedReadIsNoDeletion()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Define("brief");
        fixture.Write(a, List(Entry("brief"))); fixture.Write(b, List(Entry("brief")));
        var sync = fixture.Sync();
        sync.Pass(fixture.Accounts, _ => true, T0);
        ClaudeScheduledPass pass;
        // The app is writing the list: it cannot be read now.
        using (new FileStream(fixture.PathOf(a), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            pass = sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(1));
        Check(pass.Unreadable.SequenceEqual(["A"]) && fixture.Task(b, "brief") is not null, "a list that cannot be read now deletes nothing");
        Check(sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(2)) is { Written: 0, Unreadable.Count: 0 } && fixture.Task(b, "brief") is not null,
            "and the next pass reads it again as it was");
        fixture.Define("later");
        fixture.Write(a, List(Entry("brief"), Entry("later")));
        using (new FileStream(fixture.PathOf(a), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(3));
        sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(4));
        Check(fixture.Task(b, "later") is not null, "a read that failed for now keeps the starting point: a task A made meanwhile still spreads");
    }

    private static void TestVanishedListIsNoDeletion()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Define("brief");
        fixture.Write(a, List(Entry("brief"))); fixture.Write(b, List(Entry("brief")));
        var sync = fixture.Sync();
        sync.Pass(fixture.Accounts, _ => true, T0);
        File.Delete(fixture.PathOf(a));
        var pass = sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(1));
        Check(pass.Unreadable.SequenceEqual(["A"]) && fixture.Task(b, "brief") is not null && !File.Exists(fixture.PathOf(a)),
            "a list that was there and is gone is not taken for its tasks deleted");
    }

    private static void TestListTheAppCouldNotLoadIsNoDeletion()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Define("brief", "other", "later");
        fixture.Write(a, List(Entry("brief"), Entry("other"))); fixture.Write(b, List(Entry("brief"), Entry("other")));
        var sync = fixture.Sync();
        sync.Pass(fixture.Accounts, _ => true, T0);
        // A list the app cannot load either: its next save there keeps only what is made after.
        File.WriteAllText(fixture.PathOf(a), "{\"scheduledTasks\":[{\"id\":\"brief\"}]}");
        Check(sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(1)).Unreadable.SequenceEqual(["A"]), "A's list does not read");
        fixture.Write(a, List(Entry("later")));
        sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(2));
        Check(fixture.Ids(b).SequenceEqual(["brief", "other"]) && fixture.Ids(a).SequenceEqual(["later"]),
            "what comes back after a list the app could not load is a new starting point: the tasks it lost are not deleted elsewhere");
    }

    private static void TestWritesAreAtomic()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Define("brief");
        fixture.Write(a, List()); fixture.Write(b, List());
        var sync = fixture.Sync();
        sync.Pass(fixture.Accounts, _ => true, T0);
        fixture.Write(a, List(Entry("brief")));
        var before = fixture.Bytes(b);
        var asked = 0; var besideFirst = false;
        bool Leaves(string key)
        {
            if (key != b.Key) return true;
            if (++asked == 2)
                besideFirst = Directory.GetFiles(Path.GetDirectoryName(fixture.PathOf(b))!, ".scheduled-tasks.*.widget-sync.partial").Length == 1;
            return asked < 2;
        }
        var refused = sync.Pass(fixture.Accounts, Leaves, T0.AddMinutes(1));
        Check(refused.Written == 0 && refused.Waiting.SequenceEqual(["B"]) && besideFirst && fixture.Bytes(b).SequenceEqual(before)
              && Directory.GetFiles(Path.GetDirectoryName(fixture.PathOf(b))!).Length == 1,
            "the new list is written beside the old one and moved over it only while writing is still allowed; refused, the old one stays and no partial is left");
        File.WriteAllText(fixture.PathOf(b), "{\"scheduledTasks\":[]}");
        var changed = 0;
        var raced = sync.Pass(fixture.Accounts, key =>
        {
            if (key == b.Key && ++changed == 2) File.WriteAllText(fixture.PathOf(b), "{\"scheduledTasks\":[],\"x\":1}");
            return true;
        }, T0.AddMinutes(2));
        Check(raced.Written == 0 && fixture.Read(b)["x"]!.GetValue<int>() == 1, "a list that changed after it was read is left as the app wrote it");
        sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(3));
        Check(fixture.Task(b, "brief") is not null && Directory.GetFiles(Path.GetDirectoryName(fixture.PathOf(b))!).Length == 1,
            "the next pass writes it, leaving only the list");
    }

    private static void TestBackupsKept()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Define("brief");
        fixture.Write(a, List(Entry("brief"))); fixture.Write(b, List(Entry("brief")));
        var sync = fixture.Sync();
        sync.Pass(fixture.Accounts, _ => true, T0);
        byte[] last = [];
        for (var round = 1; round <= 7; round++)
        {
            fixture.Write(a, List(Entry("brief", $"0 {round} * * *")));
            last = fixture.Bytes(b);
            sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(round));
        }
        var backups = Directory.GetFiles(sync.BackupFolder(b.Key)).Order(StringComparer.Ordinal).ToArray();
        Check(backups.Length == 5 && File.ReadAllBytes(backups[^1]).SequenceEqual(last)
              && fixture.Task(b, "brief")!["cronExpression"]!.GetValue<string>() == "0 7 * * *",
            "each list's previous content is kept before it is replaced, the newest five");
    }

    private static void TestMissingDefinitionNotCopied()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Write(a, List()); fixture.Write(b, List());
        var sync = fixture.Sync();
        sync.Pass(fixture.Accounts, _ => true, T0);
        fixture.Write(a, List(Entry("undefined")));
        Check(sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(1)).Written == 0 && fixture.Ids(b).Length == 0,
            "a task whose definition folder is missing is not copied");
        Check(new ClaudeScheduledSync(fixture.Sessions, fixture.Store, null, () => new MemoryStream()).HasDefinition("undefined") == false,
            "without a definitions folder nothing counts as defined");
    }

    private static void TestLostWriteIsNoChange()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Define("brief");
        fixture.Write(a, List()); fixture.Write(b, List());
        var sync = fixture.Sync();
        sync.Pass(fixture.Accounts, _ => true, T0);
        fixture.Write(a, List(Entry("brief")));
        sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(1));
        Check(fixture.Task(b, "brief") is not null, "B got the task");
        // The app came to B moments after, so it may have read B's list before the write; it then saves its older copy.
        sync.NoteLoaded(b.Key, T0.AddMinutes(1).AddSeconds(4));
        fixture.Write(b, List());
        var pass = sync.Pass(fixture.Accounts, key => key != b.Key, T0.AddMinutes(2));
        Check(fixture.Task(a, "brief") is not null && pass.Waiting.SequenceEqual(["B"]),
            "the task found gone from B is taken for the app's older copy, not deleted everywhere, and B is written again once allowed");
        sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(3));
        Check(fixture.Task(b, "brief") is not null, "B has it again");
        sync.NoteLoaded(b.Key, T0.AddHours(1));
        fixture.Write(b, List());
        sync.Pass(fixture.Accounts, _ => true, T0.AddHours(2));
        Check(fixture.Task(a, "brief") is null, "the app came to B well after the write: a deletion there is a deletion");
    }

    private static void TestSeed()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief", "mixed", "gone");
        fixture.Write(a, List(Entry("brief"), Entry("mixed", "0 1 * * *"), Entry("undefined")));
        fixture.Write(b, List(Entry("mixed", "0 2 * * *"), Entry("gone")));
        fixture.Write(c, List(Entry("gone")));
        var sync = fixture.Sync();
        sync.Pass(fixture.Accounts, _ => true, T0);
        fixture.Write(c, List());
        sync.Pass(fixture.Accounts, key => key == c.Key, T0.AddMinutes(1));
        var pass = sync.Pass(fixture.Accounts, key => key != a.Key, T0.AddMinutes(2), seed: true);
        var seed = pass.Seed!;
        Check(seed.Status == "ok" && seed.Filled.Single().Id == "brief" && seed.Filled.Single().Into.Order().SequenceEqual(["B", "C"])
              && seed.Conflicts.Single() is ("mixed", var holders) && holders.Order().SequenceEqual(["A", "B"])
              && seed.Skipped.Contains(("undefined", "no_definition")) && seed.Skipped.Contains(("gone", "deleted")),
            "the seed fills a task into the accounts lacking it, and skips one held differently, one deleted and one undefined");
        Check(fixture.Task(b, "brief") is not null && fixture.Task(c, "brief") is not null && fixture.Task(c, "mixed") is null
              && fixture.Task(b, "mixed")!["cronExpression"]!.GetValue<string>() == "0 2 * * *" && fixture.Task(a, "gone") is null,
            "only the accounts lacking it get it; nothing is chosen for the task held differently and nothing is deleted by the seed");
        File.WriteAllText(fixture.PathOf(c), "{");
        Check(sync.Pass(fixture.Accounts, _ => true, T0.AddMinutes(3), seed: true).Seed!.Status == "unreadable", "no seed while a list does not read");
    }

    private static void TestPlanWritesNothing()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Define("brief");
        fixture.Write(a, List(Entry("brief"))); fixture.Write(b, List());
        var sync = fixture.Sync();
        var (kept, exists) = sync.LoadState();
        var plan = ClaudeScheduledCommand.Describe(sync, fixture.Accounts, kept, exists, T0);
        Check(plan["firstPass"]!.GetValue<bool>() && plan["notSpreadByFirstPass"]![0]!["id"]!.GetValue<string>() == "brief"
              && plan["seed"]!["fill"]![0]!["into"]![0]!.GetValue<string>() == "B" && !Directory.Exists(fixture.Store),
            "before the first pass the plan lists the tasks it would leave where they are, and what a seed fills, writing nothing");
        sync.Pass(fixture.Accounts, _ => true, T0);
        var state = File.ReadAllBytes(Path.Combine(fixture.Store, "state.json"));
        (kept, exists) = sync.LoadState();
        plan = ClaudeScheduledCommand.Describe(sync, fixture.Accounts, kept, exists, T0.AddMinutes(1));
        Check(!plan["firstPass"]!.GetValue<bool>() && plan["changes"]!.AsArray().Count == 0 && plan["writes"]!.AsArray().Count == 0
              && plan["seedWrites"]![0]!["create"]![0]!.GetValue<string>() == "brief" && plan["seedWrites"]![0]!["account"]!.GetValue<string>() == "B",
            "after it, with nothing changed, the next pass would write nothing and a seed would make the task in B");
        fixture.Write(a, List(Entry("brief", "0 3 * * *")));
        plan = ClaudeScheduledCommand.Describe(sync, fixture.Accounts, kept, exists, T0.AddMinutes(2));
        Check(plan["changes"]![0]!["by"]!.GetValue<string>() == "A" && plan["writes"]![0]!["create"]![0]!.GetValue<string>() == "brief"
              && File.ReadAllBytes(Path.Combine(fixture.Store, "state.json")).SequenceEqual(state) && fixture.Ids(b).Length == 0,
            "a change in A shows as A's, with what the next pass would write, and the plan still writes nothing");
    }

    // The service: the list the app shows is written only once the app is closed.
    private static void TestServiceWritesShownListOnlyAfterClose()
    {
        using var fixture = new Fixture("G", "H");
        var (g, h) = (fixture[0], fixture[1]);
        fixture.Define("brief");
        fixture.Write(g, List()); fixture.Write(h, List());
        using var app = new FakeApp(fixture);
        app.After(TimeSpan.Zero, app.Init(g));
        fixture.Write(h, List(Entry("brief")));
        var shown = fixture.Bytes(g);
        app.After(TimeSpan.FromSeconds(3));
        app.After(TimeSpan.FromMinutes(3));
        Check(fixture.Bytes(g).SequenceEqual(shown), "while the app runs, the list of the account it shows is not written");
        app.Runtime = new ClaudeDesktopRuntime(ClaudeDesktopRunState.Closed, null);
        app.After(TimeSpan.FromSeconds(2));
        Check(fixture.Bytes(g).SequenceEqual(shown), "nor in the moment the app is closing");
        app.After(TimeSpan.FromSeconds(6));
        Check(fixture.Task(g, "brief") is { } brief && brief["permissionModeCarried"]!.GetValue<bool>(),
            "once the app has been closed for a moment, that list gets the task");
    }

    private static void TestServiceSeedDefersShownList()
    {
        using var fixture = new Fixture("G", "H", "I");
        var (g, h, i) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief");
        fixture.Write(g, List()); fixture.Write(h, List(Entry("brief"))); fixture.Write(i, List());
        using var app = new FakeApp(fixture);
        app.After(TimeSpan.Zero, app.Init(g));
        app.After(TimeSpan.FromMinutes(3));
        var shown = fixture.Bytes(g);
        var asking = Task.Run(() => ClaudeScheduledSeedChannel.Ask(fixture.Ledger, TimeSpan.FromSeconds(30)));
        for (var round = 0; round < 200 && !asking.IsCompleted; round++) { Thread.Sleep(50); app.After(TimeSpan.FromSeconds(1)); }
        var answer = JsonNode.Parse(asking.Result ?? "{}")!;
        Check(answer["status"]?.GetValue<string>() == "ok" && answer["fill"]![0]!["id"]!.GetValue<string>() == "brief"
              && answer["waiting"]!.AsArray().Select(node => node!.GetValue<string>()).SequenceEqual(["G"])
              && fixture.Task(i, "brief") is not null && fixture.Bytes(g).SequenceEqual(shown),
            "a seed asked of the widget fills the other account now and leaves the one the app shows waiting");
        app.Runtime = new ClaudeDesktopRuntime(ClaudeDesktopRunState.Closed, null);
        app.After(TimeSpan.FromSeconds(2));
        app.After(TimeSpan.FromSeconds(6));
        Check(fixture.Task(g, "brief") is not null, "it gets the task once the app is closed");
    }

    private static void TestServiceStopsOnUnreviewedVersion()
    {
        using var fixture = new Fixture("G", "H");
        var (g, h) = (fixture[0], fixture[1]);
        fixture.Define("brief");
        fixture.Write(g, List()); fixture.Write(h, List());
        using var app = new FakeApp(fixture) { Scheduled = ClaudeDesktopContract.Unreviewed };
        app.Runtime = new ClaudeDesktopRuntime(ClaudeDesktopRunState.Closed, null);
        app.After(TimeSpan.Zero);
        app.After(TimeSpan.FromSeconds(6));
        fixture.Write(h, List(Entry("brief")));
        var status = app.After(TimeSpan.FromSeconds(3));
        Check(status.Scheduled?.Kind == ClaudeScheduledProblemKind.Unreviewed && !File.Exists(Path.Combine(fixture.Store, "state.json"))
              && fixture.Ids(g).Length == 0 && ClaudeAccountPanel.ShelfFooter(status, null, DateTimeOffset.Now).Warning,
            "an app version whose way of keeping the lists is not reviewed stops this feature alone, and says so in the status line");
        app.Scheduled = ClaudeDesktopContract.Reviewed;
        app.After(TimeSpan.FromMinutes(11));
        Check(app.After(TimeSpan.FromSeconds(3)).Scheduled is null && File.Exists(Path.Combine(fixture.Store, "state.json")),
            "a reviewed version resumes it (with a new starting point)");
    }

    private static void TestProbe()
    {
        var package = ClaudeDesktopProbe.FindPackage();
        var verdict = package is null ? (ClaudeDesktopContract?)null : ClaudeDesktopProbe.ScheduledContract(package.AsarPath);
        // The version the alignment was reviewed against has to read as reviewed; another one may read either way.
        if (package?.Version == new Version(6, 28899, 6, 0))
            Check(verdict == ClaudeDesktopContract.Reviewed, $"6.28899.6.0 keeps its scheduled lists the reviewed way (read {verdict})");
        if (package is null || verdict != ClaudeDesktopContract.Reviewed)
        {
            Console.WriteLine($"SKIP: scheduled-list contract against the installed app ({package?.Version.ToString() ?? "none"}: {verdict})");
            return;
        }
        // Read-only: mutations change a copy in memory, never the installed bundle.
        var bundle = File.ReadAllBytes(package.AsarPath);
        Check(ClaudeDesktopProbe.ScheduledContract(bundle) == ClaudeDesktopContract.Reviewed, "the installed app's scheduled lists read as reviewed");
        // Every place the text is in, so a marker found elsewhere too is gone as well.
        void Mutated(string from, string to, ClaudeDesktopContract expected, string what)
        {
            var (original, changed) = (Encoding.UTF8.GetBytes(from), Encoding.UTF8.GetBytes(to));
            var places = new List<int>();
            for (int offset = 0, found; (found = bundle.AsSpan(offset).IndexOf(original)) >= 0; offset += found + original.Length)
                places.Add(offset + found);
            Check(places.Count > 0 && original.Length == changed.Length, "mutation found: " + what);
            foreach (var at in places) changed.CopyTo(bundle, at);
            try { Check(ClaudeDesktopProbe.ScheduledContract(bundle) == expected, what); }
            finally { foreach (var at in places) original.CopyTo(bundle, at); }
        }
        Mutated("\"scheduled-tasks.json\"", "\"scheduled-tasks.jsom\"", ClaudeDesktopContract.Incompatible, "a moved list stops the feature");
        Mutated("{scheduledTasks:[],loadFailed:!0}", "{scheduledTasks:[],loadFailed:!1}", ClaudeDesktopContract.Unreviewed,
            "a failed load that no longer reads as failed is unreviewed");
        Mutated("lstatWalkRoot:!1}),", "lstatWalkRoot:!0}),", ClaudeDesktopContract.Unreviewed, "a load that reads differently is unreviewed");
        Mutated("missedRunScanFloor:", "missedRunScanFlooR:", ClaudeDesktopContract.Unreviewed, "a changed entry schema is unreviewed");
        Mutated("e!==this._initCounter||t!==this._scheduledTasksFilePath", "e!==this._initCounter||t!=this._scheduledTasksFilePath ",
            ClaudeDesktopContract.Unreviewed, "a save that no longer checks its path is unreviewed");
        Mutated("this._scheduledTasksFilePath=null,this._taskFilesDir=null", "this._scheduledTasksFilePath=null,this._taskFilesDir=nul_",
            ClaudeDesktopContract.Unreviewed, "a reset that reads differently is unreviewed");
        Check(ClaudeDesktopProbe.ScheduledContract(bundle) == ClaudeDesktopContract.Reviewed, "the copy reads as reviewed again once restored");
    }

    /// <summary>The widget's service over a fake app: its run state, its log and the scheduled-list contract.</summary>
    private sealed class FakeApp : IDisposable
    {
        private readonly string _log;
        private readonly DateTime _start;
        private int _second;
        private DateTimeOffset _now;
        private readonly System.Windows.Forms.Control _owner;
        private readonly ClaudeShelfService _service;
        internal ClaudeDesktopRuntime Runtime { get; set; }
        internal ClaudeDesktopContract Scheduled { get; set; } = ClaudeDesktopContract.Reviewed;

        internal FakeApp(Fixture fixture)
        {
            // A control installs the WinForms synchronization context on the thread that makes it. Left there, the
            // awaits of the suites run after this one on the same thread would wait for a message loop that never runs.
            var context = SynchronizationContext.Current;
            _owner = new System.Windows.Forms.Control();
            SynchronizationContext.SetSynchronizationContext(context);
            var logs = Directory.CreateDirectory(Path.Combine(fixture.Root, "logs")).FullName;
            _log = Path.Combine(logs, "main.log");
            var start = DateTime.Now.AddHours(-1);
            _start = new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute, start.Second);
            _now = new DateTimeOffset(_start).AddMinutes(1);
            Runtime = new ClaudeDesktopRuntime(ClaudeDesktopRunState.Running, _start);
            var package = new ClaudeDesktopPackage("Claude_9.9.9.9_x64__pzs8sxrjxfjjc", new Version(9, 9, 9, 9), fixture.Root, "Claude_pzs8sxrjxfjjc");
            var environment = new ClaudeShelfEnvironment(() => Runtime, () => package, _ => ClaudeDesktopContract.Reviewed, _ => { },
                () => _now, _ => Scheduled);
            var sources = new ClaudeSessionSync.Sources(fixture.Sessions, Path.Combine(fixture.Root, "Local Storage", "leveldb"),
                Path.Combine(fixture.Root, "IndexedDB", "https_claude.ai_0.indexeddb.leveldb"), fixture.Projects, fixture.Ledger);
            _service = new ClaudeShelfService(_owner, () => [], fixture.Shelf(), environment, new ClaudeDesktopLogWatcher(logs), sources);
            _service.SetAccountsForTest(fixture.Accounts);
        }

        internal string Init(ClaudeShelfAccount account) => _start.AddSeconds(++_second).ToString("yyyy-MM-dd HH:mm:ss")
            + $" [info] [LocalSessionManager] Initialization succeeded — accountId={account.AccountUuid}, orgId={account.OrgUuid}, existingSessions=0";

        internal ClaudeShelfStatus After(TimeSpan wait, params string[] lines)
        {
            File.AppendAllLines(_log, lines);
            _now += wait;
            _service.Accept(_service.Work([], _service.ActiveReservation()));
            return _service.Status;
        }

        public void Dispose() { _service.Dispose(); _owner.Dispose(); }
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.GetFullPath(Path.Combine(
            Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT") ?? Path.Combine(Path.GetTempPath(), "gfs-agent", "claude-scheduled-tests"),
            "scheduled-" + Guid.NewGuid().ToString("N")));
        internal string Sessions => Path.Combine(Root, "claude-code-sessions");
        internal string Definitions => Path.Combine(Root, "definitions");
        internal string Projects => Path.Combine(Root, "projects");
        internal string Ledger => Path.Combine(Root, "ledger");
        internal string Store => Path.Combine(Ledger, ClaudeScheduledSync.StoreFolder);
        // Accounts of their own: a running widget listens on per-account names and must not hear a test.
        internal ClaudeShelfAccount[] Accounts { get; }

        internal Fixture(params string[] labels)
        {
            Accounts = labels.Select(label => new ClaudeShelfAccount(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), label, "p-" + label)).ToArray();
            foreach (var account in Accounts) Directory.CreateDirectory(Path.Combine(Sessions, account.AccountUuid, account.OrgUuid));
            Directory.CreateDirectory(Projects);
        }

        internal ClaudeShelfAccount this[int index] => Accounts[index];
        internal string PathOf(ClaudeShelfAccount account) => Path.Combine(Sessions, account.AccountUuid, account.OrgUuid, ClaudeScheduledRegistry.FileName);
        internal void Define(params string[] ids) { foreach (var id in ids) File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(Definitions, id)).FullName, "SKILL.md"), "made up"); }
        internal void Write(ClaudeShelfAccount account, JsonObject list, DateTimeOffset? at = null)
        {
            File.WriteAllText(PathOf(account), list.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            if (at is { } time) File.SetLastWriteTimeUtc(PathOf(account), time.UtcDateTime);
        }
        internal byte[] Bytes(ClaudeShelfAccount account) => File.ReadAllBytes(PathOf(account));
        internal JsonObject Read(ClaudeShelfAccount account) => JsonNode.Parse(Bytes(account))!.AsObject();
        internal string[] Ids(ClaudeShelfAccount account) => Read(account)["scheduledTasks"]!.AsArray().Select(entry => entry!["id"]!.GetValue<string>()).ToArray();
        internal JsonObject? Task(ClaudeShelfAccount account, string id) =>
            Read(account)["scheduledTasks"]!.AsArray().FirstOrDefault(entry => entry!["id"]!.GetValue<string>() == id)?.AsObject();
        internal ClaudeScheduledSync Sync() => new(Sessions, Store, Definitions, () => new MemoryStream());
        internal ClaudeSessionShelf Shelf() => new(new ClaudeShelfPaths(Sessions, Projects, Path.Combine(Root, "scratch-workspaces"), [], Ledger,
                ScheduledTasksRoot: Definitions),
            () => new ClaudeLiveSessionScan(new HashSet<string>(), false), () => new MemoryStream());
        public void Dispose() { try { Directory.Delete(Root, recursive: true); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
