using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using WeeklyUsageIndicator;

/// <summary>Aligning the accounts' scheduled-task lists, in a fake profile folder only (GFS_ACCOUNT_TEST_ROOT): fake lists,
/// definitions, logs and app. Task ids, accounts, models and paths are made up. Each finding of the review of d845517
/// (P0-n, P1-n) has a test that sets up its case first.</summary>
internal static class ClaudeScheduledTasksTests
{
    internal static Task RunAsync()
    {
        TestFirstPassTakesStartingPointOnly();
        TestCreationSpreads();
        TestUnreadableListsNeitherWrittenNorCounted();
        TestLockedOrVanishedListIsNoDeletion();
        TestWritesAreAtomic();
        TestBackupsKept();
        TestMissingDefinitionNotCopied();
        TestPlanWritesNothing();
        TestTooLargeIsNeitherWrittenNorRead();
        TestSeedOnlyAdds();
        TestLoadFailureIsNoDeletion();
        TestDefinitionDeletionSpreads();
        TestRemoveCommand();
        TestNamedSeedRestores();
        TestDeletedIdMadeAgain();
        TestRevertIsNoChange();
        TestUnorderedChangesConflict();
        TestUpdateKeepsTargetFields();
        TestChromeAndApprovalsRaise();
        TestCreateNeverOverwrites();
        TestStoppedWriteSettles();
        TestRootChangeTakesStartingPoint();
        TestMalformedStateStopsWrites();
        TestRedirectedListsRefused();
        TestRunPairCarried();
        TestNewDueTimeClearsRunTimes();
        TestProcessedOnlyAsRead();
        TestNewListStamped();
        TestRetryOfFinishedSlotDropped();
        TestUnregisteredFoldersAligned();
        TestDefinitionsFollowConfigDir();
        TestCommandLine();
        TestLogLines();
        TestServiceLeavesLoadedListAlone();
        TestServiceRunsAfterSwitch();
        TestServiceSeedAndRemoveFiveAccounts();
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
            ["filePath"] = @"X:\made-up\scheduled-tasks\" + id + @"\SKILL.md", ["createdAt"] = 1763600000000
        };
        extra?.Invoke(entry);
        return entry;
    }

    private static void Bypass(JsonObject entry) { entry["permissionMode"] = "bypassPermissions"; entry["permissionModeCarried"] = true; }

    private static bool Raised(JsonObject entry) => ClaudeScheduledRegistry.Raises(ClaudeScheduledRegistry.Permission(entry));

    private static JsonObject List(params JsonObject[] entries) => new() { ["scheduledTasks"] = new JsonArray(entries) };

    private static ClaudeScheduledPass Pass(ClaudeScheduledSync sync, Fixture fixture, DateTimeOffset now,
        Func<string, bool>? mayWrite = null, ClaudeScheduledLogView? log = null, ClaudeScheduledRequest? request = null) =>
        sync.Pass(fixture.Accounts, log, mayWrite ?? (_ => true), now, request);

    /// <summary>A log read whole that shows these lists loaded, each load begun at its time.</summary>
    private static ClaudeScheduledLogView Loads(params (string Key, DateTimeOffset At)[] loads) =>
        new(true, null, false, null, loads.ToDictionary(load => load.Key, load => load.At, StringComparer.Ordinal), 0);

    private static string Cron(Fixture fixture, ClaudeShelfAccount account, string id) =>
        fixture.Task(account, id)?["cronExpression"]?.GetValue<string>() ?? "";

    private static bool Are(IEnumerable<string>? items, params string[] expected) => (items ?? []).SequenceEqual(expected);

    // Sorted: accounts are listed in the order of their (random) keys.
    private static string[] Strings(JsonNode? node) =>
        node is JsonArray items ? items.Select(item => item!.GetValue<string>()).Order(StringComparer.Ordinal).ToArray() : [];

    private static string[] Ids(JsonNode? node) => node is JsonArray items ? items.Select(item => item!["id"]!.GetValue<string>()).ToArray() : [];

    private static string? Status(JsonObject? answer) => answer?["status"]?.GetValue<string>();

    private static string? Skipped(JsonObject answer, string id) =>
        answer["skipped"]!.AsArray().FirstOrDefault(item => item!["id"]!.GetValue<string>() == id)?["reason"]?.GetValue<string>();

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    /// <summary>Stands for the widget stopping where a test hook throws it.</summary>
    private sealed class Stopped : Exception;

    private static void TestFirstPassTakesStartingPointOnly()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Define("only-a");
        fixture.Write(a, List(Entry("only-a")));
        fixture.Write(b, List());
        var before = fixture.Bytes(b);
        var sync = fixture.Sync();
        var first = Pass(sync, fixture, T0);
        Check(first.First && first.Written == 0 && fixture.Bytes(b).SequenceEqual(before) && File.Exists(fixture.StatePath),
            "the first pass only takes what is there as the starting point");
        var second = Pass(sync, fixture, T0.AddMinutes(1));
        Check(!second.First && second.Written == 0 && fixture.Bytes(b).SequenceEqual(before),
            "a task one account held before the starting point is not spread by the next pass either");
        var kept = File.ReadAllBytes(fixture.StatePath);
        Pass(sync, fixture, T0.AddMinutes(2));
        Check(File.ReadAllBytes(fixture.StatePath).SequenceEqual(kept), "a pass that finds nothing new leaves the kept state as it is");
        Pass(sync, fixture, T0.AddMinutes(40));
        Check(!File.ReadAllBytes(fixture.StatePath).SequenceEqual(kept), "until the times it keeps get old");
    }

    private static void TestCreationSpreads()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief", "kept-c");
        fixture.Write(a, List()); fixture.Write(b, List()); fixture.Write(c, List(Entry("kept-c")));
        var sync = fixture.Sync();
        Pass(sync, fixture, T0);
        fixture.Write(a, List(Entry("brief")));
        var pass = Pass(sync, fixture, T0.AddMinutes(1));
        Check(pass.Written == 2 && fixture.Task(b, "brief") is not null && Are(fixture.Ids(c), "kept-c", "brief")
              && Cron(fixture, b, "brief") == "0 9 * * *",
            "a task made in A after the starting point reaches B and C, and C keeps its own task");
        Check(Pass(sync, fixture, T0.AddMinutes(2)).Written == 0, "the lists written are not taken for changes of their own");
    }

    private static void TestUnreadableListsNeitherWrittenNorCounted()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief", "other");
        fixture.Write(a, List(Entry("other"))); fixture.Write(b, List(Entry("other"))); fixture.Write(c, List(Entry("other")));
        var sync = fixture.Sync();
        Pass(sync, fixture, T0);
        // One entry the app would refuse fails the whole list for it: that list is neither written nor read for changes.
        File.WriteAllText(fixture.PathOf(c), "{\"scheduledTasks\":[{\"id\":\"other\",\"enabled\":\"yes\",\"filePath\":\"x\",\"createdAt\":1}]}");
        var broken = fixture.Bytes(c);
        fixture.Write(a, List(Entry("other"), Entry("brief")));
        var pass = Pass(sync, fixture, T0.AddMinutes(1));
        Check(Are(pass.Unreadable, "C") && fixture.Bytes(c).SequenceEqual(broken) && fixture.Task(b, "brief") is not null
              && fixture.Task(a, "other") is not null && fixture.Task(b, "other") is not null,
            "a list that does not read is not written, and what it lacks is not taken for removed");
        foreach (var text in new[] { "{\"scheduledTasks\":[]", "\uFEFF{\"scheduledTasks\":[]}", "{\"scheduledTasks\":[{\"id\":\"x\",\"enabled\":true,\"filePath\":\"x\",\"createdAt\":1,\"cwd\":null}]}",
                     "{\"scheduledTasks\":[{\"id\":\"x\",\"enabled\":true,\"filePath\":\"x\",\"createdAt\":1,\"permissionMode\":\"yolo\"}]}",
                     "{\"scheduledTasks\":[{\"id\":\"x\",\"enabled\":true,\"filePath\":\"x\",\"createdAt\":1},{\"id\":\"x\",\"enabled\":true,\"filePath\":\"x\",\"createdAt\":1}]}" })
            Check(ClaudeScheduledRegistry.Parse("k", "p", Encoding.UTF8.GetBytes(text), null).Status == ClaudeScheduledReadStatus.Unreadable,
                "unreadable for the app as well (or which entry it keeps is not ours to guess): " + text[..Math.Min(40, text.Length)]);
        Check(ClaudeScheduledRegistry.Parse("k", "p", Encoding.UTF8.GetBytes(
                "{\"scheduledTasks\":[{\"id\":\"x\",\"enabled\":true,\"filePath\":\"x\",\"createdAt\":1,\"place\":5,\"importedFrom\":null}],\"runRetries\":7}"), null)
                  .Status == ClaudeScheduledReadStatus.Ok, "fields the app lets fall back to nothing do not fail the list");
        fixture.Write(c, List(Entry("other")));
        Pass(sync, fixture, T0.AddMinutes(2));
        Check(Are(fixture.Ids(c), "other", "brief"), "once it reads again it gets what it missed");
    }

    // P0-3: a list that cannot be read for now, or is gone, is no removal of its tasks.
    private static void TestLockedOrVanishedListIsNoDeletion()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Define("brief", "later");
        fixture.Write(a, List(Entry("brief"))); fixture.Write(b, List(Entry("brief")));
        var sync = fixture.Sync();
        Pass(sync, fixture, T0);
        ClaudeScheduledPass pass;
        // The app is writing the list: it cannot be read now.
        using (new FileStream(fixture.PathOf(a), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            pass = Pass(sync, fixture, T0.AddMinutes(1));
        Check(Are(pass.Unreadable, "A") && fixture.Task(b, "brief") is not null, "a list that cannot be read now removes nothing");
        Check(Pass(sync, fixture, T0.AddMinutes(2)) is { Written: 0, Unreadable.Count: 0 } && fixture.Task(b, "brief") is not null,
            "and the next pass reads it again as it was");
        fixture.Write(a, List(Entry("brief"), Entry("later")));
        using (new FileStream(fixture.PathOf(a), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Pass(sync, fixture, T0.AddMinutes(3));
        Pass(sync, fixture, T0.AddMinutes(4));
        Check(fixture.Task(b, "later") is not null, "a read that failed for now keeps the starting point: a task A made meanwhile still spreads");
        File.Delete(fixture.PathOf(a));
        pass = Pass(sync, fixture, T0.AddMinutes(5));
        Check(Are(pass.Unreadable, "A") && Are(fixture.Ids(b), "brief", "later") && !File.Exists(fixture.PathOf(a)),
            "a list that was there and is gone is not taken for its tasks removed, and is not made again");
    }

    private static void TestWritesAreAtomic()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Define("brief");
        fixture.Write(a, List()); fixture.Write(b, List());
        var sync = fixture.Sync();
        Pass(sync, fixture, T0);
        fixture.Write(a, List(Entry("brief")));
        var before = fixture.Bytes(b);
        var asked = 0; var besideFirst = false;
        bool Leaves(string key)
        {
            if (key != b.Key) return true;
            if (++asked == 2)
                besideFirst = Directory.GetFiles(fixture.FolderOf(b), ".scheduled-tasks.*.widget-sync.partial").Length == 1;
            return asked < 2;
        }
        var refused = Pass(sync, fixture, T0.AddMinutes(1), Leaves);
        Check(refused.Written == 0 && Are(refused.Waiting, "B") && besideFirst && fixture.Bytes(b).SequenceEqual(before)
              && Directory.GetFiles(fixture.FolderOf(b)).Length == 1,
            "the new list is written beside the old one and moved over it only while writing is still allowed; refused, the old one stays and no partial is left");
        File.WriteAllText(fixture.PathOf(b), "{\"scheduledTasks\":[]}");
        var changed = 0;
        var raced = Pass(sync, fixture, T0.AddMinutes(2), key =>
        {
            if (key == b.Key && ++changed == 2) File.WriteAllText(fixture.PathOf(b), "{\"scheduledTasks\":[],\"x\":1}");
            return true;
        });
        Check(raced.Written == 0 && fixture.Read(b)["x"]!.GetValue<int>() == 1, "a list that changed after it was read is left as the app wrote it");
        Pass(sync, fixture, T0.AddMinutes(3));
        Check(fixture.Task(b, "brief") is not null && Directory.GetFiles(fixture.FolderOf(b)).Length == 1,
            "the next pass writes it, leaving only the list");
    }

    private static void TestBackupsKept()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Define("brief");
        fixture.Write(a, List(Entry("brief"))); fixture.Write(b, List(Entry("brief")));
        var sync = fixture.Sync();
        Pass(sync, fixture, T0);
        byte[] last = [];
        for (var round = 1; round <= 7; round++)
        {
            fixture.Write(a, List(Entry("brief", $"0 {round} * * *")));
            last = fixture.Bytes(b);
            Pass(sync, fixture, T0.AddMinutes(round));
        }
        var backups = Directory.GetFiles(sync.BackupFolder(b.Key)).Order(StringComparer.Ordinal).ToArray();
        Check(backups.Length == 5 && File.ReadAllBytes(backups[^1]).SequenceEqual(last) && Cron(fixture, b, "brief") == "0 7 * * *",
            "each list's previous content is kept before it is replaced, the newest five");
    }

    private static void TestMissingDefinitionNotCopied()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Write(a, List()); fixture.Write(b, List());
        var sync = fixture.Sync();
        Pass(sync, fixture, T0);
        fixture.Write(a, List(Entry("undefined")));
        Check(Pass(sync, fixture, T0.AddMinutes(1)).Written == 0 && fixture.Ids(b).Length == 0,
            "a task whose definition folder is missing is not copied");
        Check(!new ClaudeScheduledSync(fixture.Sessions, fixture.Store, null, () => new MemoryStream()).HasDefinition("undefined"),
            "without a definitions folder nothing counts as defined");
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
        Pass(sync, fixture, T0);
        var state = File.ReadAllBytes(fixture.StatePath);
        (kept, exists) = sync.LoadState();
        plan = ClaudeScheduledCommand.Describe(sync, fixture.Accounts, kept, exists, T0.AddMinutes(1));
        Check(!plan["firstPass"]!.GetValue<bool>() && plan["changes"]!.AsArray().Count == 0 && plan["writes"]!.AsArray().Count == 0
              && plan["seedWrites"]![0]!["create"]![0]!.GetValue<string>() == "brief" && plan["seedWrites"]![0]!["account"]!.GetValue<string>() == "B",
            "after it, with nothing changed, the next pass would write nothing and a seed would make the task in B");
        fixture.Write(a, List(Entry("brief", "0 3 * * *")));
        plan = ClaudeScheduledCommand.Describe(sync, fixture.Accounts, kept, exists, T0.AddMinutes(2));
        Check(plan["changes"]![0]!["by"]!.GetValue<string>() == "A" && plan["writes"]![0]!["create"]![0]!.GetValue<string>() == "brief"
              && File.ReadAllBytes(fixture.StatePath).SequenceEqual(state) && fixture.Ids(b).Length == 0,
            "a change in A shows as A's, with what the next pass would write, and the plan still writes nothing");
        Check(Status(ClaudeScheduledCommand.Describe(sync, fixture.Accounts, null, true, T0.AddMinutes(3))) == "store_unreadable",
            "a kept state that does not read is said so");
    }

    // P0-1: the app reads a list of at most 10 MiB; a longer one loads as nothing.
    private static void TestTooLargeIsNeitherWrittenNorRead()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("big");
        fixture.Write(a, List()); fixture.Write(c, List());
        // B's list is one the app reads, a little under the limit.
        File.WriteAllText(fixture.PathOf(b), "{\"scheduledTasks\":[],\"padding\":\"" + new string('x', 10_400_000) + "\"}");
        var before = fixture.Bytes(b);
        var sync = fixture.Sync();
        Pass(sync, fixture, T0);
        fixture.Write(a, List(Entry("big", extra: entry => entry["displayName"] = new string('y', 100_000))));
        var pass = Pass(sync, fixture, T0.AddMinutes(1));
        Check(Are(pass.TooLarge, "B") && fixture.Bytes(b).SequenceEqual(before) && fixture.Task(c, "big") is not null && pass.Written == 1,
            "a list the merge would make longer than the app reads is left as it is and reported; the others are written");
        var empty = "{\"scheduledTasks\":[],\"padding\":\"\"}".Length;
        File.WriteAllText(fixture.PathOf(c), "{\"scheduledTasks\":[],\"padding\":\""
            + new string('x', (int)ClaudeScheduledRegistry.MaxBytes - empty + 1) + "\"}");
        Check(new FileInfo(fixture.PathOf(c)).Length == ClaudeScheduledRegistry.MaxBytes + 1
              && ClaudeScheduledRegistry.Read(c.Key, fixture.PathOf(c)) is { Status: ClaudeScheduledReadStatus.Unreadable, Problem: "large" },
            "a list one byte over 10 MiB reads as unreadable");
        var large = Pass(sync, fixture, T0.AddMinutes(2));
        Check(large.Unreadable.Contains("C") && fixture.Task(a, "big") is not null && fixture.Task(b, "big") is null,
            "and what it lacks is not taken for removed");
    }

    // P0-2: a seed only adds, and only what the accounts holding a task agree on.
    private static void TestSeedOnlyAdds()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("fill", "mixed", "gone", "edited");
        fixture.Write(a, List(Entry("fill"), Entry("mixed", "0 1 * * *"), Entry("gone"), Entry("edited")));
        fixture.Write(b, List(Entry("fill"), Entry("mixed", "0 1 * * *"), Entry("gone"), Entry("edited")));
        fixture.Write(c, List(Entry("edited")));
        var sync = fixture.Sync();
        Pass(sync, fixture, T0);
        // While nothing may be written: A changes two tasks, and a third is deleted (its definition folder goes).
        fixture.Write(a, List(Entry("fill"), Entry("mixed", "0 5 * * *"), Entry("gone"), Entry("edited", "0 3 * * *")));
        fixture.Undefine("gone");
        Pass(sync, fixture, T0.AddMinutes(1), _ => false);
        var seeded = Pass(sync, fixture, T0.AddMinutes(2), request: new("seed", []));
        var answer = seeded.Answer!;
        Check(Status(answer) == "ok" && Are(Ids(answer["fill"]), "fill") && Are(Strings(answer["fill"]![0]!["into"]), "C")
              && Ids(answer["conflicts"]).Contains("mixed") && Skipped(answer, "gone") == "deleted",
            "the seed fills the task the accounts agree on, lists the one they hold differently, and skips the one deleted");
        Check(Are(fixture.Ids(a), "fill", "mixed", "gone", "edited") && Are(fixture.Ids(b), "fill", "mixed", "gone", "edited")
              && Are(fixture.Ids(c), "edited", "fill") && Cron(fixture, b, "mixed") == "0 1 * * *"
              && Cron(fixture, b, "edited") == "0 9 * * *" && Cron(fixture, c, "edited") == "0 9 * * *" && seeded.Written == 1,
            "and only adds: nothing is deleted (the deletion still to do) and nothing is changed (A's edits still to bring) by it");
        Check(Status(Pass(sync, fixture, T0.AddMinutes(3), request: new("seed", ["mixed"])).Answer) == "incomplete"
              && Pass(sync, fixture, T0.AddMinutes(4), request: new("seed", ["fill"])).Answer is { } filled
              && Status(filled) == "ok" && Are(Strings(filled["alreadyEverywhere"]), "fill"),
            "a task asked for by --task that cannot be filled makes the answer incomplete; one every account holds is ok");
        Pass(sync, fixture, T0.AddMinutes(5));
        Check(Are(fixture.Ids(a), "fill", "mixed", "edited") && !fixture.Ids(b).Contains("gone")
              && Cron(fixture, b, "mixed") == "0 5 * * *" && Cron(fixture, c, "mixed") == "0 5 * * *"
              && Cron(fixture, b, "edited") == "0 3 * * *" && Cron(fixture, c, "edited") == "0 3 * * *",
            "the next pass does what the seed left: the deletion and A's edits");
    }

    // P0-3 and the removals: a task gone from one list is removed there alone.
    private static void TestLoadFailureIsNoDeletion()
    {
        using (var fixture = new Fixture("A", "B", "C"))
        {
            var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
            fixture.Define("brief", "other", "later");
            foreach (var account in fixture.Accounts) fixture.Write(account, List(Entry("brief"), Entry("other")));
            var sync = fixture.Sync();
            Pass(sync, fixture, T0);
            // The app could not load A's list: it held nothing, and its next save there keeps only what is made after.
            // Whether this widget saw the failure or not, the tasks gone from A are A's alone.
            fixture.Write(a, List(Entry("later")));
            var pass = Pass(sync, fixture, T0.AddMinutes(1));
            Check(Are(fixture.Ids(b), "brief", "other", "later") && Are(fixture.Ids(c), "brief", "other", "later")
                  && Are(fixture.Ids(a), "later") && Are(pass.RemovedHere, "brief", "other"),
                "tasks gone from one list are removed there only, and the task made there still spreads");
            Pass(sync, fixture, T0.AddMinutes(2));
            Check(Are(fixture.Ids(a), "later"), "a removal stays: the tasks are not made there again");
            var (kept, exists) = sync.LoadState();
            var removed = ClaudeScheduledCommand.Describe(sync, fixture.Accounts, kept, exists, T0.AddMinutes(3))["removedInOneAccountOnly"]!.AsArray();
            Check(removed.Count == 2 && removed.All(item => Are(Strings(item!["removedIn"]), "A") && Are(Strings(item!["stillIn"]), "B", "C")),
                "the plan lists them as removed in one account only, with where they still are");
            var seed = Pass(sync, fixture, T0.AddMinutes(3), request: new("seed", [])).Answer!;
            Check(Skipped(seed, "brief") == "removed_in_one_account" && seed["removedInOneAccountOnly"]!.AsArray().Count == 2
                  && Are(fixture.Ids(a), "later"),
                "so does the seed, which does not fill them back");
            var status = ClaudeShelfStatus.Initial with { MappedAccounts = 3, Scheduled = new(ClaudeScheduledProblemKind.RemovedHere, "brief, other") };
            var footer = ClaudeAccountPanel.ShelfFooter(status, null, DateTimeOffset.Now);
            Check(footer.Warning && footer.Text.Contains("brief, other"), "and the status line names them");
            // Made there again, the task is A's again.
            fixture.Write(a, List(Entry("later"), Entry("brief")));
            Check(Are(Pass(sync, fixture, T0.AddMinutes(4)).RemovedHere, "other"), "a task made again where it was removed is no longer listed");
        }
        // The app writing A's list as it is read: cut short, then whole with an edit.
        using (var fixture = new Fixture("A", "B"))
        {
            var (a, b) = (fixture[0], fixture[1]);
            fixture.Define("brief");
            fixture.Write(a, List(Entry("brief"))); fixture.Write(b, List(Entry("brief")));
            var sync = fixture.Sync();
            Pass(sync, fixture, T0);
            File.WriteAllText(fixture.PathOf(a), "{\"scheduledTasks\":[{\"id\":\"brief\",");
            Check(Are(Pass(sync, fixture, T0.AddMinutes(1)).Unreadable, "A"), "a list read cut short does not read");
            fixture.Write(a, List(Entry("brief", "0 7 * * *")));
            Pass(sync, fixture, T0.AddMinutes(2));
            Pass(sync, fixture, T0.AddMinutes(3));
            Check(Cron(fixture, b, "brief") == "0 7 * * *" && Cron(fixture, a, "brief") == "0 7 * * *",
                "the edit the whole list then holds is A's change: it spreads, and nothing takes it back");
        }
    }

    // A task is deleted from every account when its definition folder goes (the app's delete takes it with it).
    private static void TestDefinitionDeletionSpreads()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("old", "brief", "keep");
        foreach (var account in fixture.Accounts) fixture.Write(account, List(Entry("old"), Entry("brief"), Entry("keep")));
        var sync = fixture.Sync();
        Pass(sync, fixture, T0);
        fixture.Write(b, List(Entry("brief"), Entry("keep")));
        fixture.Undefine("old");
        var pass = Pass(sync, fixture, T0.AddMinutes(1), key => key != a.Key);
        Check(Are(fixture.Ids(b), "brief", "keep") && Are(fixture.Ids(c), "brief", "keep") && Are(fixture.Ids(a), "old", "brief", "keep")
              && Are(pass.Waiting, "A") && Are(pass.RemovedHere),
            "a task whose definition folder went is deleted from every account (the one not written now waits)");
        Pass(sync, fixture, T0.AddMinutes(2));
        Check(Are(fixture.Ids(a), "brief", "keep"), "and from that one once it may be written");
        // A copy made before the deletion turns up in C (the app saving a list it read before): removed there too.
        fixture.Define("old");
        fixture.Write(c, List(Entry("brief"), Entry("keep"), Entry("old", "0 5 * * *")));
        Pass(sync, fixture, T0.AddMinutes(3));
        Check(fixture.Task(a, "old") is null && fixture.Task(b, "old") is null && fixture.Task(c, "old") is null,
            "a copy of a deleted task that turns up is removed again, and filled nowhere");
        // Every definition folder going at once is the folder replaced (another config folder, a restore): none is deleted.
        foreach (var id in new[] { "old", "brief", "keep" }) fixture.Undefine(id);
        Pass(sync, fixture, T0.AddMinutes(4));
        Check(Are(fixture.Ids(a), "brief", "keep") && Are(fixture.Ids(b), "brief", "keep"), "while every definition folder going at once deletes nothing");
        Directory.Delete(fixture.Definitions, recursive: true);
        Pass(sync, fixture, T0.AddMinutes(5));
        Check(Are(fixture.Ids(a), "brief", "keep"), "nor does the definitions folder going");
    }

    // --claude-scheduled-remove: deleted from every account, the loaded one once the app closes, never filled again.
    private static void TestRemoveCommand()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("old", "brief");
        foreach (var account in fixture.Accounts) fixture.Write(account, List(Entry("old"), Entry("brief")));
        var sync = fixture.Sync();
        Pass(sync, fixture, T0);
        var before = fixture.Bytes(b);
        var pass = Pass(sync, fixture, T0.AddMinutes(1), key => key != a.Key, request: new("remove", ["old", "never-was"]));
        var answer = pass.Answer!;
        Check(Are(fixture.Ids(b), "brief") && Are(fixture.Ids(c), "brief") && Are(fixture.Ids(a), "old", "brief")
              && Status(answer) == "incomplete" && Are(Ids(answer["removed"]), "old") && Are(Strings(answer["removed"]![0]!["from"]), "A", "B", "C")
              && Skipped(answer, "never-was") == "not_found" && Are(Strings(answer["waiting"]), "A") && answer["written"]!.GetValue<int>() == 2,
            "remove deletes a task from every account but the one the app has loaded, and says what it did");
        Check(Directory.GetFiles(sync.BackupFolder(b.Key)).Any(path => File.ReadAllBytes(path).SequenceEqual(before)),
            "keeping each list's previous content");
        Pass(sync, fixture, T0.AddMinutes(2));
        Check(Are(fixture.Ids(a), "brief"), "the loaded list is done once it may be written");
        // A copy made before the removal turns up in C.
        fixture.Write(c, List(Entry("brief"), Entry("old", "0 4 * * *")));
        var seed = Pass(sync, fixture, T0.AddMinutes(3), request: new("seed", [])).Answer!;
        Check(Skipped(seed, "old") == "deleted" && Are(fixture.Ids(a), "brief") && Are(fixture.Ids(b), "brief"),
            "a seed of every task skips a removed one");
        Pass(sync, fixture, T0.AddMinutes(4));
        Check(Are(fixture.Ids(a), "brief") && Are(fixture.Ids(b), "brief") && Are(fixture.Ids(c), "brief"),
            "and the next pass removes the copy again: a removed task is never filled again");
        Check(Status(Pass(sync, fixture, T0.AddMinutes(5), request: new("remove", ["brief"])).Answer) == "ok"
              && fixture.Ids(a).Length == 0 && fixture.Ids(b).Length == 0 && fixture.Ids(c).Length == 0,
            "a removal that does all it was asked is ok");
    }

    // A list the app lost (it loaded none, and saved only what came after): the tasks gone from it are removed there
    // alone, until `seed --task` names one.
    private static void TestNamedSeedRestores()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief", "other", "raised");
        foreach (var account in fixture.Accounts) fixture.Write(account, List(Entry("brief"), Entry("other"), Entry("raised", extra: Bypass)));
        var sync = fixture.Sync();
        Pass(sync, fixture, T0);
        fixture.Write(a, List());
        Check(Are(Pass(sync, fixture, T0.AddMinutes(1)).RemovedHere, "brief", "other", "raised") && fixture.Ids(a).Length == 0,
            "the tasks gone from the lost list are removed there alone");
        var all = Pass(sync, fixture, T0.AddMinutes(2), request: new("seed", [])).Answer!;
        Check(Skipped(all, "brief") == "removed_in_one_account" && all["cleared"]!.AsArray().Count == 0 && fixture.Ids(a).Length == 0,
            "a seed of every task keeps the removals");
        var named = Pass(sync, fixture, T0.AddMinutes(3), request: new("seed", ["brief", "raised"]));
        var answer = named.Answer!;
        var cleared = answer["cleared"]!.AsArray();
        Check(Are(fixture.Ids(a), "brief") && Are(Ids(answer["fill"]), "brief") && Are(Strings(answer["fill"]![0]!["into"]), "A")
              && Are(Ids(cleared), "brief", "raised") && cleared.All(item => Are(Strings(item!["removedIn"]), "A") && item!["deletion"] is null)
              && Skipped(answer, "raised") == "raises_permission" && Status(answer) == "incomplete" && Are(named.RemovedHere, "other"),
            "a seed naming tasks clears their removals in every account and fills them as usual (not one running with more than asking)");
        Check(Are(Pass(sync, fixture, T0.AddMinutes(4)).RemovedHere, "other") && Are(fixture.Ids(a), "brief")
              && Are(fixture.Ids(b), "brief", "other", "raised") && Are(fixture.Ids(c), "brief", "other", "raised"),
            "and a task not named stays removed there");
    }

    // A deleted task's id that turns up: made after the deletion (createdAt), a new task; made before, a copy of the
    // task deleted. `seed --task` brings back a task removed by command while its definition is there.
    private static void TestDeletedIdMadeAgain()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief", "other");
        foreach (var account in fixture.Accounts) fixture.Write(account, List(Entry("brief"), Entry("other")));
        var sync = fixture.Sync();
        Pass(sync, fixture, T0);
        Pass(sync, fixture, T0.AddMinutes(1), request: new("remove", ["brief"]));
        Check(fixture.Accounts.All(account => Are(fixture.Ids(account), "other")), "removed from every account");
        // B's app saves the list it read before the removal.
        fixture.Write(b, List(Entry("brief"), Entry("other")));
        Pass(sync, fixture, T0.AddMinutes(2));
        Check(fixture.Accounts.All(account => Are(fixture.Ids(account), "other")), "a copy of the task deleted is removed again, and filled nowhere");
        // The user makes a task with that id in C after the removal.
        var made = T0.AddMinutes(3).ToUnixTimeMilliseconds();
        fixture.Write(c, List(Entry("other"), Entry("brief", "0 6 * * *", entry => entry["createdAt"] = made)));
        Pass(sync, fixture, T0.AddMinutes(4));
        Check(fixture.Accounts.All(account => Cron(fixture, account, "brief") == "0 6 * * *"
                  && fixture.Task(account, "brief")!["createdAt"]!.GetValue<long>() == made),
            "a task made with the id after the deletion is a new task, made in every account");
        fixture.Edit(a, "brief", entry => entry["cronExpression"] = "0 7 * * *");
        Pass(sync, fixture, T0.AddMinutes(5));
        Check(fixture.Accounts.All(account => Cron(fixture, account, "brief") == "0 7 * * *"), "the deletion is over: its edits go too");
        // Removed by command while A may not be written: A keeps it, and a seed naming it brings it back.
        Pass(sync, fixture, T0.AddMinutes(6), key => key != a.Key, request: new("remove", ["other"]));
        Check(Are(fixture.Ids(a), "other", "brief") && Are(fixture.Ids(b), "brief") && Are(fixture.Ids(c), "brief"),
            "removed but from the list not written");
        var restored = Pass(sync, fixture, T0.AddMinutes(7), key => key != a.Key, request: new("seed", ["other"])).Answer!;
        var cleared = restored["cleared"]!.AsArray();
        Check(cleared.Count == 1 && cleared[0]!["deletion"]!.GetValue<string>() == "command" && Status(restored) == "ok"
              && Are(Strings(restored["fill"]![0]!["into"]), "B", "C") && Are(fixture.Ids(b), "brief", "other") && Are(fixture.Ids(c), "brief", "other"),
            "a seed naming a task removed by command ends the deletion and fills it from the account still holding it");
        Pass(sync, fixture, T0.AddMinutes(8));
        Check(Are(fixture.Ids(a), "other", "brief") && Are(fixture.Ids(b), "brief", "other"), "and nothing removes it after");
    }

    // P0-5: a task back to what it held before this widget's write is not spread, nor written again.
    private static void TestRevertIsNoChange()
    {
        var wrote = T0.AddMinutes(1).AddSeconds(30);
        using (var fixture = new Fixture("A", "B"))
        {
            var (a, b) = (fixture[0], fixture[1]);
            fixture.Define("brief", "fresh");
            fixture.Write(a, List(Entry("brief"))); fixture.Write(b, List(Entry("brief")));
            var sync = fixture.Sync(() => wrote);
            Pass(sync, fixture, T0);
            // A's user raises the task's mode there, and makes another task.
            fixture.Write(a, List(Entry("brief", extra: Bypass), Entry("fresh")));
            Pass(sync, fixture, T0.AddMinutes(1));
            Check(fixture.Task(b, "brief") is { } raised && raised["permissionMode"]!.GetValue<string>() == "bypassPermissions"
                  && fixture.Task(b, "fresh") is not null, "a mode chosen in A, and a task made there, reach B");
            // B goes back to what it held before the write, and the app's load of B began within a second of it: the user
            // undoing it, or the app saving a copy it read before. Neither is spread, and nothing is written again.
            fixture.Write(b, List(Entry("brief")));
            var pass = Pass(sync, fixture, T0.AddMinutes(2), log: Loads((b.Key, wrote.AddMilliseconds(500))));
            Pass(sync, fixture, T0.AddMinutes(3));
            Check(Are(pass.Conflicts, "brief") && Are(pass.RemovedHere, "fresh") && fixture.Task(b, "brief") is { } lowered
                  && !lowered.ContainsKey("permissionMode") && fixture.Task(b, "fresh") is null
                  && fixture.Task(a, "brief")!["permissionMode"]!.GetValue<string>() == "bypassPermissions" && fixture.Task(a, "fresh") is not null,
                "a changed task back as it was is in conflict, a made one removed there alone, and B keeps the lower mode");
            // The next edit in one account settles it, without raising B again: the mode was not chosen in that edit.
            fixture.Edit(a, "brief", entry => entry["cronExpression"] = "0 6 * * *");
            var settled = Pass(sync, fixture, T0.AddMinutes(4));
            Check(Cron(fixture, b, "brief") == "0 6 * * *" && !fixture.Task(b, "brief")!.ContainsKey("permissionMode") && Are(settled.Conflicts)
                  && Pass(sync, fixture, T0.AddMinutes(5)).Written == 0,
                "the next edit wins, each account keeping the mode it has");
        }
        // Once the app's log shows its load of B begin after the write, what B holds then is B's own.
        using (var fixture = new Fixture("A", "B"))
        {
            var (a, b) = (fixture[0], fixture[1]);
            fixture.Define("brief");
            fixture.Write(a, List(Entry("brief"))); fixture.Write(b, List(Entry("brief")));
            var sync = fixture.Sync(() => wrote);
            Pass(sync, fixture, T0);
            fixture.Write(a, List(Entry("brief", extra: Bypass)));
            Pass(sync, fixture, T0.AddMinutes(1));
            var loaded = Loads((b.Key, wrote.AddSeconds(5)));
            Pass(sync, fixture, T0.AddMinutes(2), log: loaded);
            fixture.Write(b, List(Entry("brief")));
            var lowered = Pass(sync, fixture, T0.AddMinutes(3), log: loaded);
            Check(Are(lowered.Conflicts) && !fixture.Task(a, "brief")!.ContainsKey("permissionMode") && !fixture.Task(a, "brief")!.ContainsKey("permissionModeCarried"),
                "a mode lowered after the app loaded the list written is the user's choice, and goes to A");
        }
    }

    // P0-6: two accounts changing one task in one pass, in an order the files cannot tell, is a conflict.
    private static void TestUnorderedChangesConflict()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief", "other");
        foreach (var account in fixture.Accounts) fixture.Write(account, List(Entry("brief"), Entry("other")), T0.AddMinutes(-1));
        var sync = fixture.Sync();
        Pass(sync, fixture, T0);
        // While the widget is off: A edits the brief at 09:00, B edits it again at 10:00 (another model), and at 11:00
        // another task runs in A, which writes A's whole list again.
        var ran = T0.AddHours(3).UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CultureInfo.InvariantCulture);
        fixture.Write(b, List(Entry("brief", "0 8 * * *", entry => entry["model"] = "made-up-model-b"), Entry("other")), T0.AddHours(2));
        fixture.Write(a, List(Entry("brief", "0 7 * * *"), Entry("other", extra: entry => entry["lastRunAt"] = ran)), T0.AddHours(3));
        var pass = Pass(sync, fixture, T0.AddHours(4));
        Check(Are(pass.Conflicts, "brief") && Cron(fixture, a, "brief") == "0 7 * * *" && Cron(fixture, b, "brief") == "0 8 * * *"
              && Cron(fixture, c, "brief") == "0 9 * * *" && fixture.Task(b, "brief")!["model"]!.GetValue<string>() == "made-up-model-b",
            "the task is in conflict and written nowhere");
        Check(fixture.Task(b, "other")?["lastRunAt"]?.GetValue<string>() == ran && fixture.Task(c, "other")?["lastRunAt"]?.GetValue<string>() == ran,
            "the other task's run still goes");
        fixture.Edit(b, "brief", entry => entry["cronExpression"] = "0 6 * * *", T0.AddHours(5));
        var settled = Pass(sync, fixture, T0.AddHours(6));
        Check(new[] { a, b, c }.All(account => Cron(fixture, account, "brief") == "0 6 * * *" && fixture.Task(account, "brief")!["model"]?.GetValue<string>() == "made-up-model-b")
              && Are(settled.Conflicts),
            "the next edit, in one account, wins");
    }

    // P0-7: a change is applied to the entry each account holds, field by field.
    private static void TestUpdateKeepsTargetFields()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief", "made");
        fixture.Write(a, List(Entry("brief", extra: entry => entry["laterField"] = new JsonObject { ["x"] = 2 })));
        fixture.Write(b, List(Entry("brief", extra: entry =>
        {
            entry["laterField"] = new JsonObject { ["x"] = 1, ["nested"] = new JsonArray(1, 2) };
            entry["importedFrom"] = "made-up-origin";
            entry["observedToolUse"] = new JsonObject { ["promptHash"] = "b" };
            entry["permissionMode"] = "acceptEdits";
        })));
        var skips = new JsonObject { ["brief"] = new JsonArray(new JsonObject { ["at"] = 1, ["reason"] = "per_task_limit" }) };
        fixture.Write(c, new JsonObject
        {
            ["scheduledTasks"] = new JsonArray(Entry("brief")), ["recordedSkips"] = skips.DeepClone(),
            ["runRetries"] = new JsonObject { ["brief"] = new JsonObject { ["slot"] = "s", ["attempts"] = 1, ["notBefore"] = null } },
            ["laterTopLevel"] = new JsonArray(1, 2)
        });
        var sync = fixture.Sync();
        Pass(sync, fixture, T0);
        fixture.Edit(a, "brief", entry => entry["cronExpression"] = "0 4 * * *");
        Pass(sync, fixture, T0.AddMinutes(1));
        var brief = fixture.Task(b, "brief")!;
        Check(brief["cronExpression"]!.GetValue<string>() == "0 4 * * *" && brief["laterField"]!["x"]!.GetValue<int>() == 1
              && brief["laterField"]!["nested"]!.AsArray().Count == 2 && brief["importedFrom"]!.GetValue<string>() == "made-up-origin"
              && brief["observedToolUse"]!["promptHash"]!.GetValue<string>() == "b" && brief["permissionMode"]!.GetValue<string>() == "acceptEdits",
            "a change brings what it changed to the entry each account holds: its unknown and own fields, and a mode not chosen in that change, stay");
        var listC = fixture.Read(c);
        Check(Cron(fixture, c, "brief") == "0 4 * * *" && JsonNode.DeepEquals(listC["recordedSkips"], skips)
              && listC["runRetries"]!["brief"]!["slot"]!.GetValue<string>() == "s" && listC["laterTopLevel"]!.AsArray().Count == 2,
            "the list's own fields stay with it");
        Check(Pass(sync, fixture, T0.AddMinutes(2)).Written == 0 && Pass(sync, fixture, T0.AddMinutes(3)).Written == 0,
            "and what each account keeps of its own is not written over again and again");
        fixture.Write(a, new JsonObject
        {
            ["scheduledTasks"] = new JsonArray(fixture.Task(a, "brief")!.DeepClone(), Entry("made", extra: entry =>
            {
                entry["newerEntryField"] = "kept"; entry["importedFrom"] = "a-origin"; entry["observedToolUse"] = new JsonObject { ["promptHash"] = "a" };
            })),
            ["runRetries"] = new JsonObject { ["made"] = new JsonObject { ["slot"] = "x", ["attempts"] = 2, ["notBefore"] = null } }
        });
        Pass(sync, fixture, T0.AddMinutes(4));
        var made = fixture.Task(b, "made")!;
        Check(made["newerEntryField"]!.GetValue<string>() == "kept" && !made.ContainsKey("importedFrom") && !made.ContainsKey("observedToolUse")
              && !fixture.Read(b).ContainsKey("runRetries"),
            "a task made in a list brings the fields this widget does not know, not the account's own fields or its retries");
    }

    // A Chrome mode with fewer checks, a site and an approval are raises: each reaches another account only from an edit
    // seen choosing it. Lowering and removals go as any change.
    private static void TestChromeAndApprovalsRaise()
    {
        static JsonObject Approval(string tool, string? rule = null)
        {
            var approval = new JsonObject { ["toolName"] = tool };
            if (rule is not null) approval["ruleContent"] = rule;
            return approval;
        }
        static string[] Tools(JsonObject? entry) => entry?["approvedPermissions"] is JsonArray items
            ? items.Select(item => item!["toolName"]!.GetValue<string>() + (item["ruleContent"] is { } rule ? "(" + rule.GetValue<string>() + ")" : "")).ToArray()
            : [];
        static string? Chrome(JsonObject? entry) => entry?["chromePermissionMode"]?.GetValue<string>();
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief", "made");
        var made = Entry("made", extra: entry => entry["approvedPermissions"] = new JsonArray(Approval("Read")));
        // At the starting point A's copy allows more than B's: Chrome without checks, a site, and another approval.
        fixture.Write(a, List(Entry("brief", extra: entry =>
        {
            entry["chromePermissionMode"] = "skip_all_permission_checks";
            entry["chromeAllowedDomains"] = new JsonArray("example.org");
            entry["approvedPermissions"] = new JsonArray(Approval("Read"), Approval("Bash", "npm test"));
        }), (JsonObject)made.DeepClone()));
        fixture.Write(b, List(Entry("brief", extra: entry =>
        {
            entry["chromePermissionMode"] = "ask";
            entry["approvedPermissions"] = new JsonArray(Approval("Read"));
        }), (JsonObject)made.DeepClone()));
        fixture.Write(c, List());
        var sync = fixture.Sync();
        Pass(sync, fixture, T0);
        // An edit in A that chose none of that: B gets the schedule, not A's permissions, and C, lacking the task, is not
        // given it.
        fixture.Edit(a, "brief", entry => entry["cronExpression"] = "0 5 * * *");
        var pass = Pass(sync, fixture, T0.AddMinutes(1));
        var kept = fixture.Task(b, "brief");
        Check(Cron(fixture, b, "brief") == "0 5 * * *" && Chrome(kept) == "ask" && kept!["chromeAllowedDomains"] is null
              && Are(Tools(kept), "Read") && fixture.Task(c, "brief") is null && Are(pass.Held, "brief"),
            "a Chrome mode with fewer checks, a site and an approval no edit chose do not reach another account");
        // A adds an approval: that one goes, the one A had before does not.
        fixture.Edit(a, "brief", entry => entry["approvedPermissions"]!.AsArray().Add(Approval("Write")));
        Pass(sync, fixture, T0.AddMinutes(2));
        Check(Are(Tools(fixture.Task(b, "brief")), "Read", "Write") && Chrome(fixture.Task(b, "brief")) == "ask",
            "an approval seen added reaches the others");
        // An edit in B that chose nothing: A's Chrome mode is lowered to B's, and the site and approval B lacks go.
        fixture.Edit(b, "brief", entry => entry["cronExpression"] = "0 6 * * *");
        Pass(sync, fixture, T0.AddMinutes(3));
        var lowered = fixture.Task(a, "brief");
        Check(Cron(fixture, a, "brief") == "0 6 * * *" && Chrome(lowered) == "ask" && lowered!["chromeAllowedDomains"] is null
              && Are(Tools(lowered), "Read", "Write"),
            "lowering and removals go as any change");
        // B chooses a Chrome mode with fewer checks: seen chosen, it goes.
        fixture.Edit(b, "brief", entry => entry["chromePermissionMode"] = "follow_a_plan");
        Pass(sync, fixture, T0.AddMinutes(4));
        Check(Chrome(fixture.Task(a, "brief")) == "follow_a_plan" && Pass(sync, fixture, T0.AddMinutes(5)).Written == 0,
            "a Chrome mode seen chosen reaches the others, and what each keeps of its own is not written again");
        var seed = Pass(sync, fixture, T0.AddMinutes(6), request: new("seed", ["made"])).Answer!;
        Check(Skipped(seed, "made") == "raises_permission" && fixture.Task(c, "made") is null, "a seed never makes a task with an approval");
    }

    // P0-8: a list made where there was none never replaces one the app made meanwhile.
    private static void TestCreateNeverOverwrites()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Define("brief", "app-made");
        fixture.Write(a, List());
        var sync = fixture.Sync();
        Pass(sync, fixture, T0);
        fixture.Write(a, List(Entry("brief")));
        var made = List(Entry("app-made")).ToJsonString();
        sync.BeforeMoveForTest = path => { if (path == fixture.PathOf(b)) File.WriteAllText(path, made); };
        var pass = Pass(sync, fixture, T0.AddMinutes(1));
        sync.BeforeMoveForTest = null;
        Check(pass.Written == 0 && Are(pass.Waiting, "B") && File.ReadAllText(fixture.PathOf(b)) == made
              && Directory.GetFiles(fixture.FolderOf(b)).Length == 1,
            "the app's first list, made between the last check and the move, stays");
        Pass(sync, fixture, T0.AddMinutes(2));
        Check(Are(fixture.Ids(b), "app-made", "brief") && fixture.Task(a, "app-made") is not null,
            "the next pass reads it as B's and writes B then");
    }

    // P0-9: a write is kept as begun before its list is moved into place, and settled at the next start.
    private static void TestStoppedWriteSettles()
    {
        ClaudeScheduledSync Stopping(Fixture fixture, ClaudeShelfAccount a)
        {
            fixture.Define("brief");
            fixture.Write(a, List()); fixture.Write(fixture[1], List());
            var sync = fixture.Sync();
            Pass(sync, fixture, T0);
            fixture.Write(a, List(Entry("brief")));
            return sync;
        }
        // Stopped right after the move: the next start finds the list as written.
        using (var fixture = new Fixture("A", "B"))
        {
            var (a, b) = (fixture[0], fixture[1]);
            var sync = Stopping(fixture, a);
            sync.AfterMoveForTest = _ => throw new Stopped();
            Check(Throws<Stopped>(() => Pass(sync, fixture, T0.AddMinutes(1))) && fixture.Task(b, "brief") is not null, "B written, then the widget stops");
            var restarted = fixture.Sync();
            Check(Pass(restarted, fixture, T0.AddMinutes(2)).Written == 0 && fixture.Task(b, "brief") is not null, "the next start counts it as written");
            fixture.Write(b, List());
            Pass(restarted, fixture, T0.AddMinutes(3));
            Check(fixture.Task(b, "brief") is null && fixture.Task(a, "brief") is not null, "and a removal there after it is B's alone");
        }
        // Stopped right after the move, and the user removes the task from B before the widget starts again.
        using (var fixture = new Fixture("A", "B"))
        {
            var (a, b) = (fixture[0], fixture[1]);
            var sync = Stopping(fixture, a);
            sync.AfterMoveForTest = _ => throw new Stopped();
            Throws<Stopped>(() => Pass(sync, fixture, T0.AddMinutes(1)));
            fixture.Write(b, List());
            var restarted = fixture.Sync();
            var pass = Pass(restarted, fixture, T0.AddMinutes(2));
            Pass(restarted, fixture, T0.AddMinutes(3));
            Check(fixture.Task(b, "brief") is null && fixture.Task(a, "brief") is not null && Are(pass.RemovedHere, "brief"),
                "the task removed meanwhile is not made there again, nor deleted elsewhere");
        }
        // Stopped before the move: the new list is still beside the old one, so the write never happened.
        using (var fixture = new Fixture("A", "B"))
        {
            var (a, b) = (fixture[0], fixture[1]);
            var sync = Stopping(fixture, a);
            sync.BeforeMoveForTest = _ => throw new Stopped();
            Check(Throws<Stopped>(() => Pass(sync, fixture, T0.AddMinutes(1))) && fixture.Task(b, "brief") is null
                  && Directory.GetFiles(fixture.FolderOf(b), "*.partial").Length == 1, "the widget stops before the move");
            Pass(fixture.Sync(), fixture, T0.AddMinutes(2));
            Check(fixture.Task(b, "brief") is not null && Directory.GetFiles(fixture.FolderOf(b)).Length == 1,
                "the next start writes it again, leaving only the list");
        }
    }

    // P0-10: a new profile folder is a new starting point.
    private static void TestRootChangeTakesStartingPoint()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Define("brief", "old");
        fixture.Write(a, List(Entry("brief"), Entry("old"))); fixture.Write(b, List(Entry("brief"), Entry("old")));
        var sync = fixture.Sync();
        Pass(sync, fixture, T0);
        // The app's profile folder moved, and its copy of A's list lacks the brief.
        var moved = Path.Combine(fixture.Root, "moved-profile", "claude-code-sessions");
        string MovedPath(ClaudeShelfAccount account) => Path.Combine(Directory.CreateDirectory(Path.Combine(moved, account.AccountUuid, account.OrgUuid)).FullName,
            ClaudeScheduledRegistry.FileName);
        File.WriteAllText(MovedPath(a), List(Entry("old")).ToJsonString());
        File.WriteAllText(MovedPath(b), List(Entry("brief"), Entry("old")).ToJsonString());
        var bBefore = File.ReadAllBytes(MovedPath(b));
        var there = new ClaudeScheduledSync(moved, fixture.Store, fixture.Definitions, () => new MemoryStream());
        var first = there.Pass(fixture.Accounts, null, _ => true, T0.AddMinutes(1));
        var next = there.Pass(fixture.Accounts, null, _ => true, T0.AddMinutes(2));
        Check(first.First && first.Written == 0 && next.Written == 0 && File.ReadAllBytes(MovedPath(b)).SequenceEqual(bBefore)
              && Are(next.RemovedHere),
            "what the lists of a new profile folder hold differently is taken as they are, not as changes or removals");
        Check(sync.Pass(fixture.Accounts, null, _ => true, T0.AddMinutes(3)) is { First: true, Written: 0 },
            "and going back is a new starting point again");
    }

    // P0-11: a kept state that does not read whole stops every write.
    private static void TestMalformedStateStopsWrites()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Define("brief", "other");
        fixture.Write(a, List(Entry("brief"), Entry("other"))); fixture.Write(b, List(Entry("brief"), Entry("other")));
        var sync = fixture.Sync();
        Pass(sync, fixture, T0);
        fixture.Write(a, List(Entry("brief", "0 5 * * *"), Entry("other")));
        var good = File.ReadAllText(fixture.StatePath);
        var at = T0.ToString("O", CultureInfo.InvariantCulture);
        foreach (var (what, damage) in new (string, Action<JsonObject>)[]
                 {
                     ("a deletion without its accounts", state => state["deleted"] = new JsonObject { ["other"] = new JsonObject { ["at"] = at, ["by"] = "definitions" } }),
                     ("a deletion by an unknown cause", state => state["deleted"] = new JsonObject { ["other"] = new JsonObject { ["at"] = at, ["by"] = "guess", ["pending"] = new JsonArray() } }),
                     ("an account's tasks as an array", state => state["accounts"]![a.Key]!["entries"] = new JsonArray()),
                     ("an id the app does not keep", state => state["accounts"]![a.Key]!["removed"] = new JsonObject { ["Not An Id"] = at }),
                     ("an account that is no account", state => state["accounts"]!["not-a-key"] = state["accounts"]![a.Key]!.DeepClone()),
                     ("a record missing", state => state.Remove("conflicts")),
                     ("another version", state => state["version"] = 3)
                 })
        {
            var state = JsonNode.Parse(good)!.AsObject();
            damage(state);
            File.WriteAllText(fixture.StatePath, state.ToJsonString());
            var pass = Pass(sync, fixture, T0.AddMinutes(1));
            var asked = Pass(sync, fixture, T0.AddMinutes(1), request: new("remove", ["brief"]));
            Check(pass is { Failure: "store_unreadable", Written: 0 } && Status(asked.Answer) == "store_unreadable"
                  && Are(fixture.Ids(b), "brief", "other") && Cron(fixture, b, "brief") == "0 9 * * *",
                "a kept state with " + what + " stops every write");
        }
        File.WriteAllText(fixture.StatePath, good);
        Pass(sync, fixture, T0.AddMinutes(2));
        Check(Cron(fixture, b, "brief") == "0 5 * * *" && fixture.Task(b, "other") is not null, "the whole state read again, the change goes");
    }

    // P0-12: a list reached through a link, or by two names, is neither written nor read.
    private static void TestRedirectedListsRefused()
    {
        var org = Guid.NewGuid().ToString("D");
        using (var fixture = new Fixture(new ClaudeShelfAccount[] { new(Guid.NewGuid().ToString("D"), org, "A"), new(Guid.NewGuid().ToString("D"), org, "B"),
                   new(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), "C") }))
        {
            var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
            fixture.Define("brief");
            fixture.Write(a, List()); fixture.Write(c, List());
            // B's account folder is a junction to A's, of the same organization: B's list is A's file under another name.
            Directory.Delete(Path.Combine(fixture.Sessions, b.AccountUuid), recursive: true);
            Link("/J", Path.Combine(fixture.Sessions, b.AccountUuid), Path.Combine(fixture.Sessions, a.AccountUuid));
            var shown = fixture.Bytes(a);
            var sync = fixture.Sync();
            Pass(sync, fixture, T0, key => key != a.Key);
            fixture.Write(c, List(Entry("brief")));
            var pass = Pass(sync, fixture, T0.AddMinutes(1), key => key != a.Key);
            Check(fixture.Bytes(a).SequenceEqual(shown) && pass.Unreadable.Contains("A") && pass.Unreadable.Contains("B") && sync.SameFolder(b.Key, a.Key),
                "a list reached through an account folder that is a junction is neither written nor read, nor is the list it reaches");
        }
        using (var fixture = new Fixture("D", "E", "F", "G", "H"))
        {
            var (d, e, f, g, h) = (fixture[0], fixture[1], fixture[2], fixture[3], fixture[4]);
            fixture.Define("brief");
            fixture.Write(d, List()); fixture.Write(f, List()); fixture.Write(h, List());
            // E's organization folder is a junction to D's; G's list is a second name of F's.
            Directory.Delete(fixture.FolderOf(e));
            Link("/J", fixture.FolderOf(e), fixture.FolderOf(d));
            Link("/H", fixture.PathOf(g), fixture.PathOf(f));
            var (dBefore, fBefore) = (fixture.Bytes(d), fixture.Bytes(f));
            var sync = fixture.Sync();
            Pass(sync, fixture, T0);
            fixture.Write(h, List(Entry("brief")));
            var pass = Pass(sync, fixture, T0.AddMinutes(1));
            Check(pass.Unreadable.Order(StringComparer.Ordinal).SequenceEqual(["D", "E", "F", "G"]) && pass.Written == 0
                  && fixture.Bytes(d).SequenceEqual(dBefore) && fixture.Bytes(f).SequenceEqual(fBefore)
                  && ClaudeScheduledRegistry.Read(g.Key, fixture.PathOf(g)).Problem == "linked",
                "an organization folder that is a junction, and a list with a second name, are neither written nor read, under either name");
        }
    }

    private static void Link(string kind, string link, string target)
    {
        using var process = Process.Start(new ProcessStartInfo("cmd.exe", $"/c mklink {kind} \"{link}\" \"{target}\"")
        { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true })!;
        process.StandardOutput.ReadToEnd();
        process.WaitForExit();
        Check(process.ExitCode == 0 && (Directory.Exists(link) || File.Exists(link)), $"mklink {kind} made {link}");
    }

    // P1-1: the run times go as a pair, from the account furthest on.
    private static void TestRunPairCarried()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief");
        // A ran today's 09:00 slot, then once by hand at 09:05 (a run by hand clears the slot); B last ran yesterday's.
        fixture.Write(a, List(Entry("brief", extra: entry => entry["lastRunAt"] = "2026-11-20T00:05:00.000Z")));
        fixture.Write(b, List(Entry("brief", extra: entry => { entry["lastRunAt"] = "2026-11-19T00:00:04.000Z"; entry["lastScheduledFor"] = "2026-11-19T00:00:00.000Z"; })));
        fixture.Write(c, List(Entry("brief")));
        var sync = fixture.Sync();
        Pass(sync, fixture, T0);
        // Then A cannot be read: what it ran is kept.
        ClaudeScheduledPass pass;
        using (new FileStream(fixture.PathOf(a), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            pass = Pass(sync, fixture, T0.AddMinutes(1));
        Check(pass.Written == 2 && new[] { b, c }.All(account => fixture.Task(account, "brief") is { } entry
                  && entry["lastRunAt"]?.GetValue<string>() == "2026-11-20T00:05:00.000Z" && !entry.ContainsKey("lastScheduledFor")),
            "the run times go as the pair of the account the app would catch up from furthest on, from what was kept");
    }

    // P1-2: a one-time task given a new time runs then.
    private static void TestNewDueTimeClearsRunTimes()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Define("once");
        JsonObject Once(long fireAt, bool ran) => Entry("once", extra: entry =>
        {
            entry.Remove("cronExpression");
            entry["fireAt"] = fireAt;
            if (ran) entry["lastRunAt"] = "2026-11-19T03:00:02.000Z";
        });
        fixture.Write(a, List(Once(1763521200000, ran: true))); fixture.Write(b, List(Once(1763521200000, ran: true)));
        var sync = fixture.Sync();
        Pass(sync, fixture, T0);
        // A sets a new time; the app clears the last run there.
        fixture.Write(a, List(Once(1764000000000, ran: false)));
        Pass(sync, fixture, T0.AddMinutes(1));
        Pass(sync, fixture, T0.AddMinutes(2));
        Check(fixture.Task(b, "once") is { } once && once["fireAt"]!.GetValue<long>() == 1764000000000 && !once.ContainsKey("lastRunAt")
              && !fixture.Task(a, "once")!.ContainsKey("lastRunAt"),
            "a new due time goes without the run times of the old one, and they never come back");
    }

    // P1-3: a list is marked as processed only as it was read or written.
    private static void TestProcessedOnlyAsRead()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Define("brief");
        fixture.Write(a, List(Entry("brief"))); fixture.Write(b, List(Entry("brief")));
        var sync = fixture.Sync();
        Pass(sync, fixture, T0);
        fixture.Edit(a, "brief", entry => entry["cronExpression"] = "0 7 * * *");
        // The brief runs in A while the pass writes B.
        var pass = Pass(sync, fixture, T0.AddMinutes(1), key =>
        {
            if (key == b.Key) fixture.Edit(a, "brief", entry => entry["lastRunAt"] = "2026-11-21T00:00:01.000Z");
            return true;
        });
        Check(pass.Processed![a.Key] != ClaudeScheduledRegistry.CheapStamp(fixture.PathOf(a))
              && pass.Processed[b.Key] == ClaudeScheduledRegistry.CheapStamp(fixture.PathOf(b)) && Cron(fixture, b, "brief") == "0 7 * * *",
            "a list changed after it was read is not marked as processed (the next look finds it changed); one written is, as written");
        Pass(sync, fixture, T0.AddMinutes(2));
        Check(fixture.Task(b, "brief")?["lastRunAt"]?.GetValue<string>() == "2026-11-21T00:00:01.000Z", "and the next pass brings that run");
    }

    // P1-4: a list made here gets the cron matcher stamps of the list its task came from.
    private static void TestNewListStamped()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("monthly");
        JsonObject Stamped(params JsonObject[] entries) => new()
        { ["scheduledTasks"] = new JsonArray(entries), ["sundayAliasBoundaryStamped"] = true, ["dayFieldsOrBoundaryStamped"] = true };
        fixture.Write(a, Stamped());
        fixture.Write(c, List());
        var sync = fixture.Sync();
        Pass(sync, fixture, T0);
        fixture.Write(a, Stamped(Entry("monthly", "0 9 1 * 1")));
        Pass(sync, fixture, T0.AddMinutes(1));
        var made = fixture.Read(b);
        Check(made["sundayAliasBoundaryStamped"]?.GetValue<bool>() == true && made["dayFieldsOrBoundaryStamped"]?.GetValue<bool>() == true
              && fixture.Task(b, "monthly") is not null && fixture.Task(c, "monthly") is not null && !fixture.Read(c).ContainsKey("sundayAliasBoundaryStamped"),
            "a list made where there was none is stamped as its task's list was; a list there keeps its own stamps");
    }

    // P1-5: a retry of a slot another account finished is dropped; one of a later slot stays.
    private static void TestRetryOfFinishedSlotDropped()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief");
        JsonObject Retrying(string slot, JsonObject entry) => new()
        {
            ["scheduledTasks"] = new JsonArray(entry),
            ["runRetries"] = new JsonObject { ["brief"] = new JsonObject { ["slot"] = slot, ["attempts"] = 1, ["notBefore"] = null } }
        };
        JsonObject Ran(string at, string slot) => Entry("brief", extra: entry => { entry["lastRunAt"] = at; entry["lastScheduledFor"] = slot; });
        // A finished the 09:00 slot; B failed it and waits to retry it; C failed the 10:00 slot.
        fixture.Write(a, List(Ran("2026-11-21T00:00:05.000Z", "2026-11-21T00:00:00.000Z")));
        fixture.Write(b, Retrying("2026-11-21T00:00:00.000Z", Ran("2026-11-21T00:00:03.000Z", "2026-11-21T00:00:00.000Z")));
        fixture.Write(c, Retrying("2026-11-21T01:00:00.000Z", Ran("2026-11-21T01:00:02.000Z", "2026-11-21T01:00:00.000Z")));
        var sync = fixture.Sync();
        Pass(sync, fixture, T0);
        Pass(sync, fixture, T0.AddMinutes(1));
        Check(!fixture.Read(b).ContainsKey("runRetries") && fixture.Read(c)["runRetries"]?["brief"]?["slot"]?.GetValue<string>() == "2026-11-21T01:00:00.000Z"
              && new[] { a, b, c }.All(account => fixture.Task(account, "brief")!["lastScheduledFor"]!.GetValue<string>() == "2026-11-21T01:00:00.000Z"),
            "B's retry of the slot A finished goes; C's of a slot no account finished stays");
    }

    // P1-7: every account folder holding a list is aligned, registered or not.
    private static void TestUnregisteredFoldersAligned()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        var used = fixture.Folder();
        var empty = fixture.Folder();
        var loaded = fixture.Folder();
        fixture.Define("brief");
        fixture.Write(a, List()); fixture.Write(b, List()); fixture.Write(used, List());
        var log = Loads((loaded.Key, T0));
        var sync = fixture.Sync();
        Pass(sync, fixture, T0, log: log);
        fixture.Write(a, List(Entry("brief")));
        Pass(sync, fixture, T0.AddMinutes(1), log: log);
        Check(fixture.Task(b, "brief") is not null && fixture.Task(used, "brief") is not null && fixture.Task(loaded, "brief") is not null
              && !File.Exists(fixture.PathOf(empty)),
            "a folder holding a list, or one the app's log shows loaded, is aligned without being registered; a folder with neither is not given a list");
    }

    // P1-8: the definitions folder is where the app keeps it.
    private static void TestDefinitionsFollowConfigDir()
    {
        var home = @"X:\made-up-home";
        Check(ClaudeShelfPaths.AppConfigDirectory(null, home) == @"X:\made-up-home\.claude"
              && ClaudeShelfPaths.AppConfigDirectory("", home) == @"X:\made-up-home\.claude"
              && ClaudeShelfPaths.AppConfigDirectory("~", home) == home
              && ClaudeShelfPaths.AppConfigDirectory("~/made-up-config", home) == @"X:\made-up-home\made-up-config"
              && ClaudeShelfPaths.AppConfigDirectory(@"~\made-up-config", home) == @"X:\made-up-home\made-up-config"
              && ClaudeShelfPaths.AppConfigDirectory(@"Y:\made-up-config", home) == @"Y:\made-up-config"
              && ClaudeShelfPaths.AppConfigDirectory("made-up-config", home) is null,
            "CLAUDE_CONFIG_DIR (~ standing for the home folder), else ~\\.claude; a relative one names no folder");
        using var fixture = new Fixture();
        var saved = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
        try
        {
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", Path.Combine(fixture.Root, "made-up-config"));
            var paths = ClaudeShelfPaths.CreateDefault(appRoot: Path.Combine(fixture.Root, "app"));
            Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", "made-up-config");
            var relative = ClaudeShelfPaths.CreateDefault(appRoot: Path.Combine(fixture.Root, "app"));
            Check(paths.ScheduledTasksRoot == Path.Combine(fixture.Root, "made-up-config", "scheduled-tasks") && relative.ScheduledTasksRoot is null,
                "the widget and its plan command look for the definitions there");
        }
        finally { Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", saved); }
    }

    // P1-9 and the command line: --task, repeatable; an exit code of 0 only for an answer that reads ok.
    private static void TestCommandLine()
    {
        Check(Are(ClaudeScheduledCommand.Tasks(["--task", "a", "--task", "b"], bare: false), "a", "b")
              && Are(ClaudeScheduledCommand.Tasks(["a", "--task", "b"], bare: true), "a", "b")
              && ClaudeScheduledCommand.Tasks(["a"], bare: false) is null && ClaudeScheduledCommand.Tasks(["--task"], bare: false) is null
              && ClaudeScheduledCommand.Tasks(["--other"], bare: true) is null,
            "--task names a task, repeatable; remove also takes bare ids");
        Check(ClaudeScheduledCommand.Exit("{\"status\":\"ok\"}") == 0 && ClaudeScheduledCommand.Exit("{\"status\":\"incomplete\"}") == 1
              && ClaudeScheduledCommand.Exit("{\"status\":\"store_unwritable\"}") == 1 && ClaudeScheduledCommand.Exit("{\"status\":1}") == 1
              && ClaudeScheduledCommand.Exit("not json") == 1,
            "the commands exit 0 only for an answer that reads ok");
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Define("brief");
        fixture.Write(a, List(Entry("brief"))); fixture.Write(b, List());
        // The store cannot be written: a folder stands where its state goes.
        Directory.CreateDirectory(fixture.StatePath);
        var pass = Pass(fixture.Sync(), fixture, T0, request: new("seed", ["brief"]));
        Check(Status(pass.Answer) == "store_unwritable" && fixture.Ids(b).Length == 0 && pass.Written == 0
              && ClaudeScheduledCommand.Exit(pass.Answer!.ToJsonString()) == 1,
            "a seed that could not be kept says so, writes nothing, and its command exits non-zero");
    }

    // The app's scheduled-task lines, as the log watcher reads them.
    private static void TestLogLines()
    {
        var since = DateTime.Now.AddHours(-1);
        since = new DateTime(since.Year, since.Month, since.Day, since.Hour, since.Minute, since.Second);
        string Line(int second, string text) => since.AddSeconds(second).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " [info] " + text;
        var (account, org) = (Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));
        var log = new ClaudeScheduledLog();
        log.Start(new DateTimeOffset(since));
        void Read(params string[] lines) => ClaudeDesktopLogWatcher.Apply(ClaudeDesktopAccountState.Unknown, lines, since, [], null, log);
        Check(log.View(true) is { Loaded: null, Pending: true }, "at the start of a run no list is known loaded");
        Read(Line(1, "[CCDScheduledTasks] Reset"),
            Line(2, $"[CCDScheduledTasks] Initialized {{ accountId: '{account.ToUpperInvariant()}', orgId: '{org}' }}"));
        var view = log.View(true);
        Check(view is { Loaded: { } loaded, Pending: false } && loaded == account + "/" + org && view.Loads[loaded] == new DateTimeOffset(since.AddSeconds(1))
              && view.LastLine == new DateTimeOffset(since.AddSeconds(2)),
            "the log names the list the manager loaded, whoever's it is, when that load began and when its last line was");
        Read(Line(3, "[CCDScheduledTasks] Reset"));
        Check(log.View(true) is { Loaded: null, Pending: true }, "after a reset no list is loaded until the next load");
        Read(Line(4, "[CCDScheduledTasks] Initialized { accountId: 'made-up', orgId: 'made-up' }"));
        Check(log.View(true) is { Loaded: null, Pending: true }, "a load line that does not read leaves it unknown, and nothing is written");
        log.Close();
        Check(log.View(false) is { Known: false, Loaded: null, Pending: false } && log.View(false).Loads.ContainsKey(account + "/" + org),
            "a closed app holds no list; the loads seen stay");
    }

    // P0-4: while the app runs, the list its log shows loaded is never written, registered or not.
    private static void TestServiceLeavesLoadedListAlone()
    {
        using var fixture = new Fixture("G", "H");
        var (g, h) = (fixture[0], fixture[1]);
        var u = fixture.Folder("U");
        fixture.Define("brief");
        fixture.Write(g, List()); fixture.Write(h, List()); fixture.Write(u, List());
        using var app = new FakeApp(fixture);
        // The app shows U, an account the widget does not know.
        app.After(TimeSpan.Zero, app.Loads(u));
        app.After(TimeSpan.FromSeconds(7));
        fixture.Write(h, List(Entry("brief")));
        app.After(TimeSpan.FromSeconds(3));
        Check(fixture.Task(g, "brief") is not null && fixture.Ids(u).Length == 0, "the list the app has loaded is left alone, registered or not");
        app.Register(g, h, u);
        fixture.Edit(h, "brief", entry => entry["cronExpression"] = "0 7 * * *");
        app.After(TimeSpan.FromSeconds(3));
        Check(Cron(fixture, g, "brief") == "0 7 * * *" && fixture.Ids(u).Length == 0, "registered later, it is still left alone");
        // The app lets the list go: until its next load is a few seconds old, nothing is written.
        app.After(TimeSpan.Zero, app.Reset());
        fixture.Edit(h, "brief", entry => entry["cronExpression"] = "0 6 * * *");
        app.After(TimeSpan.FromSeconds(7));
        app.After(TimeSpan.FromSeconds(60));
        Check(Cron(fixture, g, "brief") == "0 7 * * *" && fixture.Ids(u).Length == 0, "between a reset and the next load nothing is written");
        app.After(TimeSpan.Zero, app.Initialized(g));
        app.After(TimeSpan.FromSeconds(3));
        Check(fixture.Ids(u).Length == 0, "nor in the seconds after the load");
        app.After(TimeSpan.FromSeconds(4));
        Check(Cron(fixture, u, "brief") == "0 6 * * *" && Cron(fixture, g, "brief") == "0 7 * * *",
            "then the list it left gets what it lacks, and the one it loaded is left alone");
        app.Runtime = new ClaudeDesktopRuntime(ClaudeDesktopRunState.Closed, null);
        app.After(TimeSpan.FromSeconds(2));
        Check(Cron(fixture, g, "brief") == "0 7 * * *", "nor in the moment the app is closing");
        app.After(TimeSpan.FromSeconds(6));
        Check(Cron(fixture, g, "brief") == "0 6 * * *", "once the app has been closed for a moment, that list gets it too");
    }

    // P1-3: the alignment runs a few seconds after the app's account changes, not minutes later.
    private static void TestServiceRunsAfterSwitch()
    {
        using var fixture = new Fixture("G", "H");
        var (g, h) = (fixture[0], fixture[1]);
        fixture.Define("brief");
        JsonObject Ran(string at) => Entry("brief", extra: entry => { entry["lastRunAt"] = at; entry["lastScheduledFor"] = at; });
        fixture.Write(g, List(Ran("2026-11-20T00:00:00.000Z"))); fixture.Write(h, List(Ran("2026-11-20T00:00:00.000Z")));
        using var app = new FakeApp(fixture);
        app.After(TimeSpan.Zero, app.Loads(h));
        app.After(TimeSpan.FromSeconds(7));
        // The user goes to G just before nine; the brief runs there at nine.
        app.After(TimeSpan.FromMinutes(5));
        app.After(TimeSpan.Zero, app.Loads(g));
        app.After(TimeSpan.FromSeconds(7));
        fixture.Edit(g, "brief", entry => { entry["lastRunAt"] = "2026-11-21T00:00:04.000Z"; entry["lastScheduledFor"] = "2026-11-21T00:00:00.000Z"; });
        app.After(TimeSpan.FromSeconds(3));
        Check(fixture.Task(h, "brief")?["lastScheduledFor"]?.GetValue<string>() == "2026-11-21T00:00:00.000Z",
            "a run in the account the app shows reaches the account it left at the next tick, before the user can go back");
        // An edit in G, and at once a switch to H: H loads before it is written, and waits.
        fixture.Edit(g, "brief", entry => entry["cronExpression"] = "0 7 * * *");
        app.After(TimeSpan.Zero, app.Loads(h));
        app.After(TimeSpan.FromSeconds(7));
        app.After(TimeSpan.FromSeconds(10));
        Check(Cron(fixture, h, "brief") == "0 9 * * *", "the list the app loaded meanwhile waits");
        app.After(TimeSpan.Zero, app.Loads(g));
        app.After(TimeSpan.FromSeconds(7));
        Check(Cron(fixture, h, "brief") == "0 7 * * *", "and is written a few seconds after the app lets it go");
    }

    // The end-to-end case: five accounts, three briefs (two running with the mode carried), the app showing an empty list.
    private static void TestServiceSeedAndRemoveFiveAccounts()
    {
        using var fixture = new Fixture("A", "B", "C", "D", "E");
        var (a, b, c, d, e) = (fixture[0], fixture[1], fixture[2], fixture[3], fixture[4]);
        // Each brief's model is in its definition's frontmatter (Define), none in the entries.
        fixture.Define("made-up-brief-1", "made-up-brief-2", "made-up-brief-3");
        fixture.Write(a, List(Entry("made-up-brief-1", extra: Bypass)));
        fixture.Write(b, List(Entry("made-up-brief-2", extra: Bypass)));
        fixture.Write(c, List(Entry("made-up-brief-3")));
        fixture.Write(d, List()); fixture.Write(e, List());
        var had = fixture.Accounts.ToDictionary(account => account.Key,
            account => fixture.Ids(account).Where(id => Raised(fixture.Task(account, id)!)).ToHashSet(StringComparer.Ordinal));
        void NoNewRaised(string when) => Check(fixture.Accounts.All(account =>
                fixture.Ids(account).Where(id => Raised(fixture.Task(account, id)!)).All(had[account.Key].Contains)),
            "no account gets a task with a raised mode it did not have: " + when);
        using var app = new FakeApp(fixture);
        app.After(TimeSpan.Zero, app.Loads(d));
        app.After(TimeSpan.FromSeconds(7));
        NoNewRaised("after the starting point");
        var seed = JsonNode.Parse(app.Ask(new("seed", ["made-up-brief-3"])) ?? "{}")!.AsObject();
        Check(Status(seed) == "ok" && Are(Ids(seed["fill"]), "made-up-brief-3")
              && Strings(seed["fill"]![0]!["into"]).Order(StringComparer.Ordinal).SequenceEqual(["A", "B", "D", "E"]) && Are(Strings(seed["waiting"]), "D"),
            "seed --task fills that task alone into every account lacking it, the one the app shows waiting");
        Check(new[] { a, b, e }.All(account => fixture.Task(account, "made-up-brief-3") is { } brief && !brief.ContainsKey("permissionMode")
                  && !brief.ContainsKey("permissionModeCarried") && !brief.ContainsKey("model"))
              && fixture.Ids(d).Length == 0 && Are(fixture.Ids(a), "made-up-brief-1", "made-up-brief-3") && Are(fixture.Ids(b), "made-up-brief-2", "made-up-brief-3"),
            "with no permission field added, and nothing else filled");
        NoNewRaised("after the seed");
        var (aBefore, bBefore) = (fixture.Bytes(a), fixture.Bytes(b));
        var removed = JsonNode.Parse(app.Ask(new("remove", ["made-up-brief-1", "made-up-brief-2"])) ?? "{}")!.AsObject();
        Check(Status(removed) == "ok" && Are(Ids(removed["removed"]), "made-up-brief-1", "made-up-brief-2")
              && Are(fixture.Ids(a), "made-up-brief-3") && Are(fixture.Ids(b), "made-up-brief-3"),
            "remove clears the other two from the accounts that held them");
        var sync = new ClaudeScheduledSync(fixture.Sessions, fixture.Store, fixture.Definitions, () => new MemoryStream());
        Check(Directory.GetFiles(sync.BackupFolder(a.Key)).Any(path => File.ReadAllBytes(path).SequenceEqual(aBefore))
              && Directory.GetFiles(sync.BackupFolder(b.Key)).Any(path => File.ReadAllBytes(path).SequenceEqual(bBefore)),
            "keeping what they held");
        app.After(TimeSpan.FromMinutes(11));
        app.After(TimeSpan.FromSeconds(3));
        app.Runtime = new ClaudeDesktopRuntime(ClaudeDesktopRunState.Closed, null);
        app.After(TimeSpan.FromSeconds(2));
        app.After(TimeSpan.FromSeconds(6));
        Check(fixture.Task(d, "made-up-brief-3") is { } shown && !shown.ContainsKey("permissionMode") && !shown.ContainsKey("permissionModeCarried")
              && fixture.Accounts.All(account => Are(fixture.Ids(account), "made-up-brief-3")),
            "the account the app showed gets the fill once it closes, and the removed two do not come back");
        NoNewRaised("after the app closed");
        app.After(TimeSpan.FromMinutes(11));
        Check(fixture.Accounts.All(account => Are(fixture.Ids(account), "made-up-brief-3")), "nor later");
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
        Check(status.Scheduled?.Kind == ClaudeScheduledProblemKind.Unreviewed && !File.Exists(fixture.StatePath)
              && fixture.Ids(g).Length == 0 && ClaudeAccountPanel.ShelfFooter(status, null, DateTimeOffset.Now).Warning,
            "an app version whose way of keeping the lists is not reviewed stops this feature alone, and says so in the status line");
        app.Scheduled = ClaudeDesktopContract.Reviewed;
        app.After(TimeSpan.FromMinutes(11));
        Check(app.After(TimeSpan.FromSeconds(3)).Scheduled is null && File.Exists(fixture.StatePath),
            "a reviewed version resumes it (with a new starting point)");
    }

    // P1-6: the probe reads every part of the app the alignment depends on, the nested schemas, the account hooks and
    // the log lines included.
    private static void TestProbe()
    {
        var package = ClaudeDesktopProbe.FindPackage();
        var verdict = package is null ? (ClaudeDesktopContract?)null : ClaudeDesktopProbe.ScheduledContract(package.AsarPath);
        // The version the alignment was reviewed against has to read as reviewed; another one may read either way.
        if (package?.Version == new Version(6, 28899, 6, 0))
            Check(verdict == ClaudeDesktopContract.Reviewed, $"6.28899.6.0 keeps its scheduled lists the reviewed way (read {verdict}: "
                + string.Join(", ", ClaudeDesktopProbe.ScheduledUnmatched(File.ReadAllBytes(package.AsarPath))) + ")");
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
        Mutated("\"cursors may have at most 32 keys\"", "\"cursors may have at most 31 keys\"", ClaudeDesktopContract.Unreviewed,
            "a changed schema the entry's is made of is unreviewed");
        Mutated("e!==this._initCounter||t!==this._scheduledTasksFilePath", "e!==this._initCounter||t!=this._scheduledTasksFilePath ",
            ClaudeDesktopContract.Unreviewed, "a save that no longer checks its path is unreviewed");
        Mutated("this._scheduledTasksFilePath=null,this._taskFilesDir=null", "this._scheduledTasksFilePath=null,this._taskFilesDir=nul_",
            ClaudeDesktopContract.Unreviewed, "a reset that reads differently is unreviewed");
        Mutated("} Reset`", "} ReSet`", ClaudeDesktopContract.Unreviewed, "a reset logged differently is unreviewed");
        Mutated("} Initialized`", "} Initialised`", ClaudeDesktopContract.Unreviewed, "a load logged differently is unreviewed");
        Mutated("onAccountOrgChanged(", "onAccountOrgChangeD(", ClaudeDesktopContract.Unreviewed, "a reset called from another hook is unreviewed");
        // The account-change hook loops over a name of its own (for(let t of ...)) before it resets the manager through the
        // file's t: once the loop reaches the call, or the hook sets t, the reset may be another object's.
        Mutated("||e.delete(t);this.pluginReloadCoordinator", "||e.delete(t),this.pluginReloadCoordinator", ClaudeDesktopContract.Unreviewed,
            "a reset called through a loop's own name is unreviewed");
        Mutated("this.customGroups.clear(),e.accountId!==this", "this.customGroups.clear(),t=e.accountId!=thi", ClaudeDesktopContract.Unreviewed,
            "a reset called through a name the hook sets is unreviewed");
        Mutated("getTaskFilesDir:", "getTaskFilesDiR:", ClaudeDesktopContract.Unreviewed, "a manager made differently is unreviewed");
        Mutated("=process.env.CLAUDE_CONFIG_DIR;return ", "=process.env.CLAUDE_CONFIG_DIX;return ", ClaudeDesktopContract.Unreviewed,
            "definitions kept elsewhere are unreviewed");
        Check(ClaudeDesktopProbe.ScheduledContract(bundle) == ClaudeDesktopContract.Reviewed, "the copy reads as reviewed again once restored");
    }

    /// <summary>The widget's service over a fake app: its run state, its log and the scheduled-list contract.</summary>
    private sealed class FakeApp : IDisposable
    {
        private readonly string _log, _ledger;
        private readonly DateTime _start;
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
            _ledger = fixture.Ledger;
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

        private string Line(string text) => _now.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " [info] " + text;

        /// <summary>The app's scheduled-task manager letting its list go, now.</summary>
        internal string Reset() => Line("[CCDScheduledTasks] Reset");

        /// <summary>The manager loading this account's list, now.</summary>
        internal string Initialized(ClaudeShelfAccount account) =>
            Line($"[CCDScheduledTasks] Initialized {{ accountId: '{account.AccountUuid}', orgId: '{account.OrgUuid}' }}");

        /// <summary>The app coming to this account: a reset, then the load.</summary>
        internal string[] Loads(ClaudeShelfAccount account) => [Reset(), Initialized(account)];

        internal void Register(params ClaudeShelfAccount[] accounts) => _service.SetAccountsForTest(accounts);

        internal ClaudeShelfStatus After(TimeSpan wait, params string[] lines)
        {
            File.AppendAllLines(_log, lines);
            _now += wait;
            _service.Accept(_service.Work([], _service.ActiveReservation()));
            return _service.Status;
        }

        /// <summary>Asks the widget through its channel, as the commands do, while it runs a tick every (fake) second.</summary>
        internal string? Ask(ClaudeScheduledRequest request)
        {
            var asking = Task.Run(() => ClaudeScheduledSeedChannel.Ask(_ledger, TimeSpan.FromSeconds(30), request));
            for (var round = 0; round < 400 && !asking.IsCompleted; round++) { Thread.Sleep(25); After(TimeSpan.FromSeconds(1)); }
            return asking.Result;
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
        internal string StatePath => Path.Combine(Store, "state.json");
        // Accounts of their own: a running widget listens on per-account names and must not hear a test.
        internal ClaudeShelfAccount[] Accounts { get; }

        internal Fixture(params string[] labels) : this(labels.Select(label =>
            new ClaudeShelfAccount(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), label, "p-" + label)).ToArray())
        { }

        internal Fixture(ClaudeShelfAccount[] accounts)
        {
            Accounts = accounts;
            foreach (var account in Accounts) Directory.CreateDirectory(FolderOf(account));
            Directory.CreateDirectory(Projects);
        }

        internal ClaudeShelfAccount this[int index] => Accounts[index];

        /// <summary>An account folder the widget has no profile for (the app alone used it).</summary>
        internal ClaudeShelfAccount Folder(string label = "unregistered")
        {
            var account = new ClaudeShelfAccount(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), label);
            Directory.CreateDirectory(FolderOf(account));
            return account;
        }

        internal string FolderOf(ClaudeShelfAccount account) => Path.Combine(Sessions, account.AccountUuid, account.OrgUuid);
        internal string PathOf(ClaudeShelfAccount account) => Path.Combine(FolderOf(account), ClaudeScheduledRegistry.FileName);

        /// <summary>Definition folders as the app makes them, the model in the frontmatter.</summary>
        internal void Define(params string[] ids)
        {
            foreach (var id in ids)
                File.WriteAllText(Path.Combine(Directory.CreateDirectory(Path.Combine(Definitions, id)).FullName, "SKILL.md"),
                    $"---\nname: {id}\ndescription: made up\nmodel: made-up-model\n---\nMade up.\n");
        }

        internal void Undefine(string id) => Directory.Delete(Path.Combine(Definitions, id), recursive: true);

        internal void Write(ClaudeShelfAccount account, JsonObject list, DateTimeOffset? at = null)
        {
            File.WriteAllText(PathOf(account), list.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            if (at is { } time) File.SetLastWriteTimeUtc(PathOf(account), time.UtcDateTime);
        }

        /// <summary>Changes one entry of a list, as the app saves it.</summary>
        internal void Edit(ClaudeShelfAccount account, string id, Action<JsonObject> change, DateTimeOffset? at = null)
        {
            var list = Read(account);
            change(list["scheduledTasks"]!.AsArray().First(entry => entry!["id"]!.GetValue<string>() == id)!.AsObject());
            Write(account, list, at);
        }

        internal byte[] Bytes(ClaudeShelfAccount account) => File.ReadAllBytes(PathOf(account));
        internal JsonObject Read(ClaudeShelfAccount account) => JsonNode.Parse(Bytes(account))!.AsObject();
        internal string[] Ids(ClaudeShelfAccount account) => Read(account)["scheduledTasks"]!.AsArray().Select(entry => entry!["id"]!.GetValue<string>()).ToArray();
        internal JsonObject? Task(ClaudeShelfAccount account, string id) =>
            Read(account)["scheduledTasks"]!.AsArray().FirstOrDefault(entry => entry!["id"]!.GetValue<string>() == id)?.AsObject();
        internal ClaudeScheduledSync Sync(Func<DateTimeOffset>? clock = null) => new(Sessions, Store, Definitions, () => new MemoryStream(), clock);
        internal ClaudeSessionShelf Shelf() => new(new ClaudeShelfPaths(Sessions, Projects, Path.Combine(Root, "scratch-workspaces"), [], Ledger,
                ScheduledTasksRoot: Definitions),
            () => new ClaudeLiveSessionScan(new HashSet<string>(), false), () => new MemoryStream());
        public void Dispose() { try { Directory.Delete(Root, recursive: true); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
