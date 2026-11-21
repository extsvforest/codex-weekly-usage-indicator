using System.Diagnostics;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using WeeklyUsageIndicator;

/// <summary>Mirroring the scheduled tasks a person chose across the accounts' lists, in a fake profile folder only
/// (GFS_ACCOUNT_TEST_ROOT): fake lists, definitions, logs and app. Task ids, accounts, models and paths are made up. Each
/// decision of the mirror design (D1-D17) and each finding of its review (P0-n, P1-n) has a test that sets up its case.</summary>
internal static class ClaudeScheduledTasksTests
{
    internal static Task RunAsync()
    {
        TestNoAdoptionHoldsDifferences();
        TestPermissionByEffect();
        TestPlanPermissionPerTarget();
        TestChangedListStopsTheRound();
        TestServiceWritesOnlyListsTheAppLeft();
        TestServiceNeedsReviewedRunningApp();
        TestWholeListKept();
        TestTooLargeHeld();
        TestIdentityHolds();
        TestDefinitionIdentityHolds();
        TestStateVerified();
        TestFloorFromConsumedSlots();
        TestApplyRaisesFloor();
        TestRecurringLocalOnly();
        TestRetryMapsHoldInserts();
        TestRemoveAndUnmirror();
        TestReplaces();
        TestPreparation();
        TestTokenBindsPlan();
        TestFields();
        TestCommandChannel();
        TestWritesAtomicAndBackedUp();
        TestRedirectedListsRefused();
        TestDefinitionsFollowConfigDir();
        TestCommandLine();
        TestLogLines();
        TestServiceFiveAccounts();
        TestProbe();
        return Task.CompletedTask;
    }

    private static readonly DateTimeOffset T0 = new(2026, 11, 20, 0, 30, 0, TimeSpan.Zero);

    private static JsonObject Entry(string id, string cron = "0 9 * * *", Action<JsonObject>? extra = null)
    {
        var entry = new JsonObject
        {
            ["id"] = id, ["displayName"] = "Task " + id, ["cronExpression"] = cron, ["enabled"] = true,
            ["filePath"] = @"X:\made-up\scheduled-tasks\" + id + @"\SKILL.md", ["createdAt"] = 1763600000000, ["cwd"] = @"X:\made-up\work"
        };
        extra?.Invoke(entry);
        return entry;
    }

    private static void Bypass(JsonObject entry) { entry["permissionMode"] = "bypassPermissions"; entry["permissionModeCarried"] = true; }

    private static JsonObject List(params JsonObject[] entries) => new() { ["scheduledTasks"] = new JsonArray(entries) };

    private static ClaudeScheduledLogView Shows(ClaudeShelfAccount account) => new(true, account.Key, false, null, 1);

    private static ClaudeScheduledScene Scene(ClaudeScheduledSync sync, Fixture fixture, ClaudeShelfAccount? loaded = null,
        IReadOnlyList<ClaudeShelfAccount>? registered = null) =>
        sync.Scene(registered ?? fixture.Accounts, loaded is null ? null : Shows(loaded), running: loaded is not null)!;

    private static Func<string, bool> Leaving(ClaudeShelfAccount? loaded) => key => loaded is null || key != loaded.Key;

    private static JsonObject Ask(ClaudeScheduledSync sync, Fixture fixture, ClaudeScheduledRequest request, DateTimeOffset now,
        ClaudeShelfAccount? loaded = null, bool reviewed = true, IReadOnlyList<ClaudeShelfAccount>? registered = null) =>
        sync.Serve(request, Scene(sync, fixture, loaded, registered), Leaving(loaded), now, reviewed);

    /// <summary>The plan, then its apply with the plan's token.</summary>
    private static JsonObject Apply(ClaudeScheduledSync sync, Fixture fixture, ClaudeScheduledRequest request, DateTimeOffset now,
        ClaudeShelfAccount? loaded = null)
    {
        var plan = Ask(sync, fixture, request, now, loaded);
        Check(Status(plan) == "plan", "a plan comes first: " + plan.ToJsonString());
        return Ask(sync, fixture, request with { Apply = true, Expect = Token(plan) }, now, loaded);
    }

    private static ClaudeScheduledRequest Mirror(string task, ClaudeShelfAccount from, params string[] replaces) =>
        new("mirror", task, From(from), Replaces: replaces);

    private static string From(ClaudeShelfAccount account) => account.AccountUuid + ":" + account.OrgUuid;

    private static ClaudeScheduledPass Pass(ClaudeScheduledSync sync, Fixture fixture, DateTimeOffset now, ClaudeShelfAccount? loaded = null) =>
        sync.Pass(Scene(sync, fixture, loaded), Leaving(loaded), now);

    private static string? Status(JsonObject? answer) => answer?["status"]?.GetValue<string>();
    private static string? Token(JsonObject answer) => answer["token"]?.GetValue<string>();
    private static string? Text(JsonNode? node) => node is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;

    private static JsonObject Target(JsonObject answer, ClaudeShelfAccount account) =>
        answer["targets"]!.AsArray().First(target => target!["key"]!.GetValue<string>() == account.Key)!.AsObject();

    private static string? Action(JsonObject answer, ClaudeShelfAccount account) => Text(Target(answer, account)["action"]);

    private static string[] Fields(JsonObject answer, ClaudeShelfAccount account) =>
        Target(answer, account)["fields"] is JsonArray fields ? fields.Select(field => field!["name"]!.GetValue<string>()).ToArray() : [];

    private static string Cron(Fixture fixture, ClaudeShelfAccount account, string id) =>
        fixture.Task(account, id)?["cronExpression"]?.GetValue<string>() ?? "";

    private static bool Are(IEnumerable<string?>? items, params string[] expected) => (items ?? []).SequenceEqual(expected);

    private static bool Throws<T>(Action action) where T : Exception
    {
        try { action(); return false; }
        catch (T) { return true; }
    }

    // D1, review P0-1: no automatic adoption. A difference in any account holds the whole task until a person mirrors
    // again; between mirrors nothing that is there is changed.
    private static void TestNoAdoptionHoldsDifferences()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief");
        fixture.Write(a, List(Entry("brief")));
        fixture.Write(b, List(Entry("brief", "0 7 * * *", entry => entry["displayName"] = "Old name")));
        fixture.Write(c, List());
        var before = fixture.Accounts.Select(fixture.Bytes).ToArray();
        var sync = fixture.Sync();
        var plan = Ask(sync, fixture, Mirror("brief", a), T0);
        Check(Status(plan) == "plan" && Action(plan, a) == "source" && Action(plan, b) == "fix" && Action(plan, c) == "insert"
              && Are(Fields(plan, b), "displayName", "cronExpression") && plan["raises"]!.GetValue<int>() == 0 && Token(plan) is { Length: 16 },
            "a mirror's plan shows each account's action and the fields that would change, with a token");
        Check(fixture.Accounts.Select(fixture.Bytes).Zip(before).All(pair => pair.First.SequenceEqual(pair.Second)) && !File.Exists(fixture.StatePath),
            "a plan writes nothing, and keeps nothing");
        var applied = Ask(sync, fixture, Mirror("brief", a) with { Apply = true, Expect = Token(plan) }, T0);
        Check(Status(applied) == "mirrored" && Cron(fixture, b, "brief") == "0 9 * * *"
              && fixture.Task(b, "brief")!["displayName"]!.GetValue<string>() == "Task brief" && fixture.Task(c, "brief") is not null,
            "its apply writes that plan");
        // The app saves an old copy into B, and C loses the task: the mirror is held, nothing is written anywhere.
        fixture.Edit(b, "brief", Bypass);
        fixture.Write(c, List());
        var (aHeld, bHeld) = (fixture.Bytes(a), fixture.Bytes(b));
        var pass = Pass(sync, fixture, T0.AddMinutes(1));
        Check(pass.Notice is { Kind: ClaudeScheduledProblemKind.Differs, Detail: "brief: B permissionMode/permissionModeCarried" }
              && pass.Written == 0 && fixture.Ids(c).Length == 0 && fixture.Bytes(a).SequenceEqual(aHeld) && fixture.Bytes(b).SequenceEqual(bHeld),
            "an account that differs is reported by account and field, and the task is held everywhere: B's mode is not taken, nor undone, and C is not filled");
        fixture.Edit(b, "brief", entry => { entry.Remove("permissionMode"); entry.Remove("permissionModeCarried"); });
        pass = Pass(sync, fixture, T0.AddMinutes(2));
        Check(pass.Notice?.Kind == ClaudeScheduledProblemKind.Differs && fixture.Ids(c).Length == 0,
            "the hold lasts until a person mirrors again, even once the accounts agree again");
        applied = Apply(sync, fixture, Mirror("brief", a), T0.AddMinutes(3));
        pass = Pass(sync, fixture, T0.AddMinutes(4));
        Check(Status(applied) == "mirrored" && fixture.Task(c, "brief") is not null && pass.Notice is null, "mirroring again lifts it");
    }

    // D2, review P0-2: the start's mode as the app decides it, with the kill switch on and off, and the import rules.
    private static void TestPermissionByEffect()
    {
        static ClaudeScheduledAccess Of(string? mode, bool carried = false, bool imported = false) => new(mode, carried, imported);
        static bool Raises(ClaudeScheduledAccess before, ClaudeScheduledAccess after) => ClaudeScheduledPermission.Raises(before, after);
        Check(Raises(Of("plan"), Of("default")), "plan to default raises, though default is the lowest of the rest");
        Check(Raises(Of("auto", carried: true), Of("auto")), "auto losing its carried mark raises: the kill switch no longer starts it in default");
        Check(Raises(Of("bypassPermissions", carried: true), Of("bypassPermissions")), "so does bypass");
        Check(!Raises(Of("bypassPermissions", carried: true), Of("default")) && !Raises(Of("auto"), Of("auto", carried: true)),
            "lowering the mode, or adding the carried mark, does not");
        Check(Raises(Of(null), Of("default")) && Raises(Of("default"), Of(null)) && !Raises(Of(null), Of(null)),
            "a mode set where none was, or none where one was, may raise: the session's own mode is not known");
        Check(Raises(Of("auto", carried: true, imported: true), Of("auto", carried: true)) && Raises(Of(null, imported: true), Of(null)),
            "a copy of an imported task raises: the import rules do not hold for it");
        Check(ClaudeScheduledPermission.StartName(Of("bypassPermissions", carried: true), killSwitch: true) == "default"
              && ClaudeScheduledPermission.StartName(Of("bypassPermissions", carried: true), killSwitch: false) == "bypassPermissions"
              && ClaudeScheduledPermission.StartName(Of(null), killSwitch: false) == "(the session's own)",
            "a plan shows the mode a run starts in either way");
    }

    // D2, review P0-2: each target is weighed by its own effect; an allowance is bound to the targets the plan read.
    private static void TestPlanPermissionPerTarget()
    {
        using var fixture = new Fixture("A", "B", "C", "D");
        var (a, b, c, d) = (fixture[0], fixture[1], fixture[2], fixture[3]);
        fixture.Define("brief");
        var imported = Entry("brief", extra: entry =>
        {
            entry["importedFrom"] = new JsonObject { ["kind"] = "made-up" };
            entry["permissionMode"] = "auto"; entry["permissionModeCarried"] = true;
        });
        fixture.Write(a, List(imported));
        fixture.Write(b, List());
        File.WriteAllText(fixture.PathOf(c), "not json");
        fixture.Write(d, List(Entry("brief", extra: entry => { entry["permissionMode"] = "plan"; })));
        var sync = fixture.Sync();
        var plan = Ask(sync, fixture, Mirror("brief", a), T0);
        Check(Target(plan, b)["permission"]?["raise"]?.GetValue<bool>() == true && Target(plan, d)["permission"]?["raise"]?.GetValue<bool>() == true
              && Action(plan, c) == "unreadable" && plan["raises"]!.GetValue<int>() == 2,
            "a copy of an imported auto task, and plan made auto, both raise; a list not read is left out");
        var refused = Ask(sync, fixture, Mirror("brief", a) with { Apply = true, Expect = Token(plan) }, T0);
        Check(Status(refused) == "raise_needs_allow" && fixture.Ids(b).Length == 0 && !File.Exists(fixture.StatePath),
            "without --allow-raise an apply that raises writes nothing");
        var allowed = Ask(sync, fixture, Mirror("brief", a) with { Apply = true, Expect = Token(plan), AllowRaise = true }, T0);
        Check(Status(allowed) == "mirrored" && fixture.Task(b, "brief") is { } copy && copy["permissionMode"]?.GetValue<string>() == "auto"
              && copy["permissionModeCarried"]?.GetValue<bool>() == true && !copy.ContainsKey("importedFrom")
              && fixture.Task(d, "brief")?["permissionMode"]?.GetValue<string>() == "auto",
            "with it, the plan's targets are written as the plan showed them");
        // C reads now: the allowance did not cover it, so it is not filled.
        fixture.Write(c, List());
        var pass = Pass(sync, fixture, T0.AddMinutes(1));
        Check(fixture.Ids(c).Length == 0 && pass.Notice is { Kind: ClaudeScheduledProblemKind.Held, Detail: "brief" },
            "a list the plan did not read is not filled with a copy that raises; it is reported held");
        // B loses its copy: the allowance was given for B, so the pass makes it again.
        fixture.Write(b, List());
        Pass(sync, fixture, T0.AddMinutes(2));
        Check(fixture.Task(b, "brief") is not null, "a list the allowance covered gets its copy back");
        // Switching a switched-off import back on is no update the app's own tool makes: it counts as a raise.
        using var other = new Fixture("E", "F");
        var (e, f) = (other[0], other[1]);
        other.Define("brief");
        other.Write(e, List(Entry("brief")));
        other.Write(f, List(Entry("brief", extra: entry => { entry["enabled"] = false; entry["importedFrom"] = new JsonObject(); })));
        var on = Ask(other.Sync(), other, Mirror("brief", e), T0);
        Check(Action(on, f) == "fix" && Target(on, f)["permission"]?["raise"]?.GetValue<bool>() == true
              && Target(on, f)["permission"]?["switchesOnImport"]?.GetValue<bool>() == true,
            "a fix that switches an imported task back on needs --allow-raise");
    }

    // D3, review P0-3: the list is read again just before it is replaced; a list changed since it was read stops the round.
    private static void TestChangedListStopsTheRound()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief");
        fixture.Write(a, List(Entry("brief")));
        fixture.Write(b, List(Entry("brief")));
        fixture.Write(c, List(Entry("brief")));
        var sync = fixture.Sync();
        Apply(sync, fixture, Mirror("brief", a), T0);
        fixture.Write(b, List()); fixture.Write(c, List());
        string? first = null;
        sync.BeforeMoveForTest = path =>
        {
            if (first is not null) return;
            first = path;
            File.WriteAllText(path, "{\"scheduledTasks\":[],\"saved\":1}");
        };
        var pass = Pass(sync, fixture, T0.AddMinutes(1));
        sync.BeforeMoveForTest = null;
        var (changed, other) = first == fixture.PathOf(b) ? (b, c) : (c, b);
        Check(pass.Written == 0 && fixture.Read(changed)["saved"]?.GetValue<int>() == 1 && fixture.Ids(changed).Length == 0
              && fixture.Ids(other).Length == 0 && pass.Waiting.Count == 2,
            "a list the app saved after it was read is left as the app saved it, and the round stops there");
        Pass(sync, fixture, T0.AddMinutes(2));
        Check(fixture.Task(b, "brief") is not null && fixture.Task(c, "brief") is not null && fixture.Read(changed)["saved"]?.GetValue<int>() == 1,
            "the next pass reads it again and fills both, keeping what the app saved");
    }

    // D3, review P0-3/P0-4: while the app runs only a list other than the one its log shows loaded is written, and none
    // from a reset until a load a few seconds old.
    private static void TestServiceWritesOnlyListsTheAppLeft()
    {
        using var fixture = new Fixture("G", "H");
        var (g, h) = (fixture[0], fixture[1]);
        var u = fixture.Folder("U");
        fixture.Define("brief");
        fixture.Write(g, List(Entry("brief"))); fixture.Write(h, List()); fixture.Write(u, List());
        using var app = new FakeApp(fixture);
        // The app shows U, an account the widget does not know.
        app.After(TimeSpan.Zero, app.Loads(u));
        app.After(TimeSpan.FromSeconds(7));
        var plan = JsonNode.Parse(app.Ask(Mirror("brief", g)) ?? "{}")!.AsObject();
        Check(Status(plan) == "plan" && Action(plan, u) == "insert" && Target(plan, u)["loaded"]?.GetValue<bool>() == true
              && plan["reviewed"]?.GetValue<bool>() == true,
            "the widget answers a plan, marking the list the app has loaded");
        var applied = JsonNode.Parse(app.Ask(Mirror("brief", g) with { Apply = true, Expect = Token(plan) }) ?? "{}")!.AsObject();
        Check(Status(applied) == "mirrored" && fixture.Task(h, "brief") is not null && fixture.Ids(u).Length == 0
              && Text(Target(applied, u)["result"]) == "not written now",
            "an apply writes the lists the app has not loaded, and leaves the loaded one, registered or not");
        // The app lets the list go: from the reset until its next load is a few seconds old, nothing is written.
        app.After(TimeSpan.Zero, app.Reset());
        app.After(TimeSpan.FromSeconds(7));
        app.After(TimeSpan.FromSeconds(61));
        Check(fixture.Ids(u).Length == 0, "between a reset and the next load nothing is written");
        app.After(TimeSpan.Zero, app.Initialized(g));
        app.After(TimeSpan.FromSeconds(3));
        Check(fixture.Ids(u).Length == 0, "nor in the seconds after the load");
        app.After(TimeSpan.FromSeconds(4));
        Check(fixture.Task(u, "brief") is not null, "then the list it left gets the task");
        // G, loaded now, loses the task: it is made there only once the app has been closed for a moment.
        fixture.Write(g, List());
        app.After(TimeSpan.FromSeconds(3));
        Check(fixture.Ids(g).Length == 0, "the loaded list is not written");
        app.Runtime = new ClaudeDesktopRuntime(ClaudeDesktopRunState.Closed, null);
        app.After(TimeSpan.FromSeconds(2));
        Check(fixture.Ids(g).Length == 0, "nor in the moment the app is closing");
        app.After(TimeSpan.FromSeconds(6));
        Check(fixture.Task(g, "brief") is not null, "once the app has been closed for a moment, that list gets it too");
    }

    // D4, review P0-4: nothing is written unless the installed version reads as reviewed and the app running is that
    // installation; a plan is still answered.
    private static void TestServiceNeedsReviewedRunningApp()
    {
        using var fixture = new Fixture("G", "H");
        var (g, h) = (fixture[0], fixture[1]);
        fixture.Define("brief");
        fixture.Write(g, List(Entry("brief"))); fixture.Write(h, List());
        Apply(fixture.Sync(), fixture, Mirror("brief", g), T0, loaded: h);
        Check(fixture.Ids(h).Length == 0, "the mirror is chosen, H not filled yet");
        using var app = new FakeApp(fixture) { Scheduled = ClaudeDesktopContract.Unreviewed };
        app.Runtime = new ClaudeDesktopRuntime(ClaudeDesktopRunState.Closed, null);
        app.After(TimeSpan.Zero);
        var status = app.After(TimeSpan.FromSeconds(6));
        Check(status.Scheduled?.Kind == ClaudeScheduledProblemKind.Unreviewed && fixture.Ids(h).Length == 0
              && ClaudeAccountPanel.ShelfFooter(status, null, DateTimeOffset.Now).Warning,
            "an app version whose way of keeping the lists is not reviewed stops the mirror, and says so in the status line");
        var plan = JsonNode.Parse(app.Ask(Mirror("brief", g)) ?? "{}")!.AsObject();
        var apply = JsonNode.Parse(app.Ask(Mirror("brief", g) with { Apply = true, Expect = Token(plan) }) ?? "{}")!.AsObject();
        Check(Status(plan) == "plan" && plan["reviewed"]?.GetValue<bool>() == false && Status(apply) == "unreviewed_app_version"
              && fixture.Ids(h).Length == 0,
            "a plan is answered, and its apply refused");
        // Reviewed, but the app running is another installation than the one read.
        app.Scheduled = ClaudeDesktopContract.Reviewed;
        app.Runtime = app.Running([@"X:\made-up\other-install\claude.exe"]);
        app.After(TimeSpan.Zero, app.Loads(g));
        status = app.After(TimeSpan.FromSeconds(7));
        app.After(TimeSpan.FromMinutes(11));
        Check(status.Scheduled?.Kind == ClaudeScheduledProblemKind.Unreviewed && fixture.Ids(h).Length == 0,
            "an app running from another installation than the one reviewed is not written for");
        app.Runtime = app.Running();
        app.After(TimeSpan.FromMinutes(11));
        Check(fixture.Task(h, "brief") is not null && app.After(TimeSpan.FromSeconds(3)).Scheduled is null,
            "the reviewed installation running, the mirror resumes");
    }

    // D5, review P0-5: a list is changed whole as read; only the mirrored entries change. Numbers the app reads as
    // infinite fail the file, as in the app.
    private static void TestWholeListKept()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief");
        fixture.Write(a, List(Entry("brief")));
        var kept = List(Entry("other", "30 8 * * 1", entry =>
        {
            entry["approvedPermissions"] = new JsonArray(new JsonObject { ["toolName"] = "made-up", ["ruleContent"] = "x" });
            entry["watcherless"] = new JsonObject { ["nested"] = new JsonArray(1, 2.5, "three", null) };
            entry["lastRunAt"] = "2026-11-19T23:30:00.000Z";
        }));
        kept["runRetries"] = new JsonObject { ["other"] = new JsonObject { ["attempt"] = 1 } };
        kept["recordedSkips"] = new JsonObject { ["other"] = new JsonArray("2026-11-18T23:30:00.000Z") };
        kept["sundayAliasBoundaryStamped"] = true;
        kept["dayFieldsOrBoundaryStamped"] = true;
        kept["unknownTopLevel"] = new JsonObject { ["x"] = 12345678901234567890.0 };
        fixture.Write(b, kept);
        File.WriteAllText(fixture.PathOf(c), "{\"scheduledTasks\":[{\"id\":\"x\",\"enabled\":true,\"filePath\":\"x\",\"createdAt\":1,\"fireAt\":1e400}]}");
        Check(ClaudeScheduledRegistry.Read(c.Key, fixture.PathOf(c)) is { Status: ClaudeScheduledReadStatus.Unreadable, Problem: "schema" },
            "a number the app reads as infinite fails the list, as the app's schema does");
        var cBefore = fixture.Bytes(c);
        var sync = fixture.Sync();
        var applied = Apply(sync, fixture, Mirror("brief", a), T0);
        var after = fixture.Read(b);
        var rest = after.DeepClone().AsObject();
        ClaudeScheduledRegistry.Drop(rest, entry => ClaudeScheduledRegistry.Text(entry, "id") == "brief");
        Check(Status(applied) == "mirrored" && fixture.Task(b, "brief") is not null
              && ClaudeScheduledRegistry.Hash(rest) == ClaudeScheduledRegistry.Hash(JsonNode.Parse(kept.ToJsonString())),
            "every other entry, nested value, file-level map, stamp and unknown field stays as it was");
        Check(fixture.Bytes(c).SequenceEqual(cBefore) && Action(applied, c) == "unreadable", "a list that does not read is not written");
    }

    // D5: a list the change would make longer than the app reads is held and reported.
    private static void TestTooLargeHeld()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief");
        fixture.Write(a, List(Entry("brief", extra: entry => entry["displayName"] = new string('y', 100_000))));
        File.WriteAllText(fixture.PathOf(b), "{\"scheduledTasks\":[],\"padding\":\"" + new string('x', 10_400_000) + "\"}");
        fixture.Write(c, List());
        var before = fixture.Bytes(b);
        var sync = fixture.Sync();
        var applied = Apply(sync, fixture, Mirror("brief", a), T0);
        Check(Text(Target(applied, b)["reason"]) == "too_large" && fixture.Bytes(b).SequenceEqual(before) && fixture.Task(c, "brief") is not null,
            "the apply leaves a list it would make too long, and writes the others");
        var pass = Pass(sync, fixture, T0.AddMinutes(1));
        Check(pass.Notice is { Kind: ClaudeScheduledProblemKind.TooLarge, Detail: "B" } && fixture.Bytes(b).SequenceEqual(before),
            "the pass reports it");
    }

    // D6, review P0-6: another task under the mirrored id holds the mirror; mirroring again replaces it, its approvals
    // not taken.
    private static void TestIdentityHolds()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief");
        fixture.Write(a, List(Entry("brief")));
        fixture.Write(b, List()); fixture.Write(c, List());
        var sync = fixture.Sync();
        Apply(sync, fixture, Mirror("brief", a), T0);
        Check(fixture.Task(b, "brief")?["createdAt"]?.ToJsonString() == "1763600000000", "a copy takes the source's createdAt");
        // A new task made in B under the same id, with approvals of its own.
        fixture.Write(b, List(Entry("brief", extra: entry =>
        {
            entry["createdAt"] = 1763700000000;
            entry["approvedPermissions"] = new JsonArray(new JsonObject { ["toolName"] = "made-up" });
        })));
        fixture.Write(c, List());
        var pass = Pass(sync, fixture, T0.AddMinutes(1));
        Check(pass.Notice is { Kind: ClaudeScheduledProblemKind.Stopped, Detail: "brief: B createdAt" } && fixture.Ids(c).Length == 0,
            "another task under the id holds the mirror everywhere");
        var plan = Ask(sync, fixture, Mirror("brief", a), T0.AddMinutes(2));
        Check(Action(plan, b) == "replace" && Target(plan, b)["replacing"]?["createdAt"]?.ToJsonString() == "1763700000000",
            "mirroring again shows that task replaced");
        Ask(sync, fixture, Mirror("brief", a) with { Apply = true, Expect = Token(plan) }, T0.AddMinutes(2));
        Check(fixture.Task(b, "brief") is { } copy && copy["createdAt"]!.ToJsonString() == "1763600000000" && !copy.ContainsKey("approvedPermissions")
              && fixture.Task(c, "brief") is not null,
            "and replaces it with the copy, whose approvals are not the other task's");
    }

    // D6, review P0-6: a definition gone, or gone and back, holds the mirror until it is mirrored again.
    private static void TestDefinitionIdentityHolds()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Define("brief");
        fixture.Write(a, List(Entry("brief"))); fixture.Write(b, List());
        var sync = fixture.Sync();
        Apply(sync, fixture, Mirror("brief", a), T0);
        fixture.Undefine("brief");
        fixture.Write(b, List());
        var pass = Pass(sync, fixture, T0.AddMinutes(1));
        Check(pass.Notice is { Kind: ClaudeScheduledProblemKind.Stopped, Detail: "brief: definition" } && fixture.Ids(b).Length == 0,
            "a definition gone holds the mirror");
        fixture.Define("brief");
        Pass(sync, fixture, T0.AddMinutes(2));
        Check(fixture.Ids(b).Length == 0, "and it stays held when a definition comes back");
        Apply(sync, fixture, Mirror("brief", a), T0.AddMinutes(3));
        Check(fixture.Task(b, "brief") is not null, "until it is mirrored again");
        // Made again between two passes: another folder.
        fixture.Undefine("brief");
        fixture.Define("brief");
        fixture.Write(b, List());
        pass = Pass(sync, fixture, T0.AddMinutes(4));
        Check(pass.Notice?.Kind == ClaudeScheduledProblemKind.Stopped && fixture.Ids(b).Length == 0,
            "a definition made again, unseen in between, holds it too");
    }

    // D7, review P0-7: the kept mirror verifies against its fingerprint; one that does not is frozen. The first
    // version's state is never taken.
    private static void TestStateVerified()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Define("brief");
        fixture.Write(a, List(Entry("brief", extra: entry => { entry["permissionMode"] = "auto"; entry["permissionModeCarried"] = true; })));
        fixture.Write(b, List());
        var sync = fixture.Sync();
        Apply(sync, fixture, Mirror("brief", a), T0);
        var state = JsonNode.Parse(File.ReadAllText(fixture.StatePath))!.AsObject();
        Check(state["format"]?.GetValue<string>() == "gfs-scheduled-mirror/2"
              && state["mirrors"]!["brief"]!["body"]!["settings"]!["permissionModeCarried"]?.GetValue<bool>() == true,
            "the mirror keeps its settings, the carried mark included, under a fingerprint");
        state["mirrors"]!["brief"]!["body"]!["settings"]!.AsObject().Remove("permissionModeCarried");
        File.WriteAllText(fixture.StatePath, state.ToJsonString());
        fixture.Write(b, List());
        var pass = Pass(sync, fixture, T0.AddMinutes(1));
        Check(pass.Notice is { Kind: ClaudeScheduledProblemKind.Stopped, Detail: "brief: record" } && fixture.Ids(b).Length == 0,
            "a body that does not match its fingerprint freezes the task: nothing is written for it");
        Check(Status(Ask(sync, fixture, new("remove", "brief"), T0)) == "refused", "nor is it removed while frozen");
        Check(Status(Ask(sync, fixture, new("unmirror", "brief"), T0)) == "ok" && Pass(sync, fixture, T0.AddMinutes(2)).Notice is null,
            "stopping the mirror clears it");
        // A state of the first version stays where it is and is not read.
        File.Delete(fixture.StatePath);
        File.WriteAllText(Path.Combine(fixture.Store, "state.json"), "{\"version\":1,\"tasks\":{\"brief\":{}},\"seeds\":{\"brief\":[\"x\"]}}");
        pass = Pass(sync, fixture, T0.AddMinutes(3));
        Check(pass.Failure is null && pass.Report!.Count == 0 && fixture.Ids(b).Length == 0, "the first version's state is not taken");
        File.WriteAllText(fixture.StatePath, "{\"format\":\"gfs-scheduled-mirror/3\",\"mirrors\":{}}");
        pass = Pass(sync, fixture, T0.AddMinutes(4));
        Check(pass.Failure == "store_unreadable" && Status(Ask(sync, fixture, Mirror("brief", a), T0)) == "refused",
            "a state of another format stops the mirror");
    }

    // D8, review P1-1/P1-2: the scan floor rises just past the newest slot an account consumed of the task itself, on
    // its cron, switched on; a run started by hand is no slot. The widget never writes run times or retries.
    private static void TestFloorFromConsumedSlots()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief");
        fixture.Write(a, List(Entry("brief"))); fixture.Write(b, List()); fixture.Write(c, List());
        var sync = fixture.Sync();
        Apply(sync, fixture, Mirror("brief", a), T0);
        Check(fixture.Task(b, "brief")?["missedRunScanFloor"]?.GetValue<string>() == ClaudeScheduledRegistry.Iso(T0),
            "a copy's floor is when it was made");
        var now = new DateTimeOffset(2026, 11, 20, 9, 5, 0, TimeSpan.Zero);
        fixture.Edit(a, "brief", entry => { entry["lastScheduledFor"] = "2026-11-20T09:00:00.000Z"; entry["lastRunAt"] = "2026-11-20T09:00:04.000Z"; });
        var pass = Pass(sync, fixture, now);
        Check(new[] { b, c }.All(account => fixture.Task(account, "brief") is { } copy
                  && copy["missedRunScanFloor"]?.GetValue<string>() == "2026-11-20T09:00:00.001Z"
                  && !copy.ContainsKey("lastRunAt") && !copy.ContainsKey("lastScheduledFor"))
              && !fixture.Task(a, "brief")!.ContainsKey("missedRunScanFloor") && pass.Written == 2,
            "the floor goes just past the slot A ran, not its start time; A, which ran it, is left alone; no run time is written");
        // A run by hand in B, later: no slot.
        fixture.Edit(b, "brief", entry => entry["lastRunAt"] = "2026-11-20T10:00:00.000Z");
        var (aHand, cHand) = (fixture.Bytes(a), fixture.Bytes(c));
        pass = Pass(sync, fixture, now.AddHours(1));
        Check(pass.Written == 0 && fixture.Bytes(a).SequenceEqual(aHand) && fixture.Bytes(c).SequenceEqual(cHand), "a run started by hand moves no floor");
        // A slot recorded ahead of now (a clock gone forward) is not taken.
        fixture.Edit(a, "brief", entry => entry["lastScheduledFor"] = "2026-11-21T18:00:00.000Z");
        var (bAhead, cAhead) = (fixture.Bytes(b), fixture.Bytes(c));
        pass = Pass(sync, fixture, now.AddHours(2));
        Check(pass.Written == 0 && fixture.Bytes(b).SequenceEqual(bAhead) && fixture.Bytes(c).SequenceEqual(cAhead), "a slot ahead of now counts for nothing");
        // A floor already past the newest slot is not lowered.
        fixture.Edit(a, "brief", entry => entry["lastScheduledFor"] = "2026-11-20T11:00:00.000Z");
        fixture.Edit(c, "brief", entry => entry["missedRunScanFloor"] = "2026-11-20T12:00:00.000Z");
        cAhead = fixture.Bytes(c);
        pass = Pass(sync, fixture, now.AddHours(2));
        Check(pass.Written == 1 && fixture.Task(b, "brief")?["missedRunScanFloor"]?.GetValue<string>() == "2026-11-20T11:00:00.001Z"
              && fixture.Bytes(c).SequenceEqual(cAhead),
            "a floor is only ever raised");
        // Retries and skips the lists keep stay theirs.
        var listed = fixture.Read(b);
        listed["runRetries"] = new JsonObject { ["brief"] = new JsonObject { ["attempt"] = 1 } };
        fixture.Write(b, listed);
        fixture.Edit(a, "brief", entry => entry["lastScheduledFor"] = "2026-11-20T18:00:00.000Z");
        Pass(sync, fixture, new DateTimeOffset(2026, 11, 20, 18, 10, 0, TimeSpan.Zero));
        Check(fixture.Read(b)["runRetries"]?["brief"]?["attempt"]?.GetValue<int>() == 1
              && fixture.Task(b, "brief")?["missedRunScanFloor"]?.GetValue<string>() == "2026-11-20T18:00:00.001Z",
            "a retry the list keeps is left to the app");
    }

    // D8, review P1-3: an apply that changes the cron or switches a task on raises that list's floor to the apply's time,
    // and a slot of the old cron is no candidate.
    private static void TestApplyRaisesFloor()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Define("brief");
        fixture.Write(a, List(Entry("brief", "0 11 * * *")));
        fixture.Write(b, List(Entry("brief", "0 11 * * *", entry =>
        {
            entry["missedRunScanFloor"] = "2026-11-19T00:00:00.000Z"; entry["lastScheduledFor"] = "2026-11-19T11:00:00.000Z";
        })));
        var sync = fixture.Sync();
        Apply(sync, fixture, Mirror("brief", a), T0);
        fixture.Edit(a, "brief", entry => entry["cronExpression"] = "0 9 * * *");
        var at = new DateTimeOffset(2026, 11, 20, 8, 0, 0, TimeSpan.Zero);
        var plan = Ask(sync, fixture, Mirror("brief", a), at);
        Ask(sync, fixture, Mirror("brief", a) with { Apply = true, Expect = Token(plan) }, at);
        Check(Text(Target(plan, b)["floor"]) is not null && Cron(fixture, b, "brief") == "0 9 * * *"
              && fixture.Task(b, "brief")?["missedRunScanFloor"]?.GetValue<string>() == ClaudeScheduledRegistry.Iso(at)
              && fixture.Task(b, "brief")?["lastScheduledFor"]?.GetValue<string>() == "2026-11-19T11:00:00.000Z",
            "a changed cron raises the floor to the apply's time, the run times left");
        // B ran 11:00 on the old cron; A has no slot of the new one: B's slot is no candidate for A.
        var aBefore = fixture.Bytes(a);
        Pass(sync, fixture, new DateTimeOffset(2026, 11, 20, 8, 30, 0, TimeSpan.Zero));
        Check(fixture.Bytes(a).SequenceEqual(aBefore), "a slot of the old cron is no candidate");
        fixture.Edit(a, "brief", entry => entry["enabled"] = false);
        Apply(sync, fixture, Mirror("brief", a), at.AddHours(1));
        fixture.Edit(a, "brief", entry => entry["enabled"] = true);
        var on = at.AddHours(5);
        Apply(sync, fixture, Mirror("brief", a), on);
        Check(fixture.Task(b, "brief")?["missedRunScanFloor"]?.GetValue<string>() == ClaudeScheduledRegistry.Iso(on),
            "switching a task on raises it too");
    }

    // D9, review P1-4: recurring cron tasks run here, in a folder, only.
    private static void TestRecurringLocalOnly()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Define("brief");
        var sync = fixture.Sync();
        string? Refusal(JsonObject source, JsonObject? target = null)
        {
            fixture.Write(a, List(source)); fixture.Write(b, target is null ? List() : List(target));
            var plan = Ask(sync, fixture, Mirror("brief", a), T0);
            return Status(plan) == "refused" ? Text(plan["reason"]) + ":" + Text(plan["detail"]) : null;
        }
        Check(Refusal(Entry("brief", extra: entry => entry["fireAt"] = 1763700000000)) == "not_recurring_local:one_time"
              && Refusal(Entry("brief", extra: entry => entry["place"] = new JsonObject { ["cwd"] = "made-up" })) == "not_recurring_local:remote"
              && Refusal(Entry("brief", extra: entry => entry.Remove("cwd"))) == "not_recurring_local:no_folder"
              && Refusal(Entry("brief"), Entry("brief", extra: entry => entry["place"] = new JsonObject())) == "target_not_recurring_local:B: remote",
            "a one-time task, a remote one or one without a folder is not mirrored, nor one an account holds as such");
        fixture.Write(a, List(Entry("brief"))); fixture.Write(b, List(Entry("brief")));
        Apply(sync, fixture, Mirror("brief", a), T0);
        fixture.Edit(b, "brief", entry => entry["fireAt"] = 1763700000000);
        var pass = Pass(sync, fixture, T0.AddMinutes(1));
        Check(pass.Notice is { Kind: ClaudeScheduledProblemKind.Stopped, Detail: "brief: B one_time" }, "a mirrored task turned one-time holds the mirror");
    }

    // D10, review P1-5: a list still keeping retries or skips of a task under the id gets no copy; those of an entry
    // there are only shown.
    private static void TestRetryMapsHoldInserts()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief");
        fixture.Write(a, List(Entry("brief")));
        var retries = List();
        retries["runRetries"] = new JsonObject { ["brief"] = new JsonObject { ["attempt"] = 2 } };
        fixture.Write(b, retries);
        var skips = List(Entry("brief"));
        skips["recordedSkips"] = new JsonObject { ["brief"] = new JsonArray("2026-11-19T09:00:00.000Z") };
        fixture.Write(c, skips);
        var sync = fixture.Sync();
        var plan = Ask(sync, fixture, Mirror("brief", a), T0);
        Check(Action(plan, b) == "held" && Text(Target(plan, b)["reason"]) == "retries_or_skips_left"
              && Are(Target(plan, c)["pending"]!.AsArray().Select(Text)!, "recordedSkips"),
            "the plan holds a copy where retries of a task under the id are kept, and shows those of an entry there");
        Ask(sync, fixture, Mirror("brief", a) with { Apply = true, Expect = Token(plan) }, T0);
        var pass = Pass(sync, fixture, T0.AddMinutes(1));
        Check(fixture.Ids(b).Length == 0 && pass.Notice is { Kind: ClaudeScheduledProblemKind.Unfilled, Detail: "brief: B" },
            "nor does a pass make it there");
    }

    // D11, review P1-6: no deferred removal. A removal removes the entries its plan showed from the lists the app has not
    // loaded, says where the task remains, and is safe to run again; a mirrored task is refused until it is unmirrored.
    private static void TestRemoveAndUnmirror()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief", "old");
        fixture.Write(a, List(Entry("old"), Entry("brief"))); fixture.Write(b, List(Entry("old"))); fixture.Write(c, List(Entry("old")));
        var sync = fixture.Sync();
        var (aBefore, bBefore) = (fixture.Bytes(a), fixture.Bytes(b));
        var removed = Apply(sync, fixture, new("remove", "old"), T0, loaded: c);
        Check(Status(removed) == "removed" && Are(fixture.Ids(a), "brief") && fixture.Ids(b).Length == 0 && Are(fixture.Ids(c), "old")
              && Are(removed["remains"]!.AsArray().Select(Text)!, "C"),
            "a removal removes it from the lists the app has not loaded and says where it remains");
        Check(Directory.GetFiles(sync.BackupFolder(a.Key)).Any(path => File.ReadAllBytes(path).SequenceEqual(aBefore))
              && Directory.GetFiles(sync.BackupFolder(b.Key)).Any(path => File.ReadAllBytes(path).SequenceEqual(bBefore)),
            "keeping what each held");
        removed = Apply(sync, fixture, new("remove", "old"), T0.AddMinutes(1));
        Check(Status(removed) == "removed" && fixture.Ids(c).Length == 0 && removed["remains"]!.AsArray().Count == 0, "run again, it finishes");
        removed = Apply(sync, fixture, new("remove", "old"), T0.AddMinutes(2));
        Check(Status(removed) == "removed" && removed["targets"]!.AsArray().Count == 0, "and again changes nothing");
        // Only the task the plan showed: another made under the id after the plan is not removed.
        fixture.Write(b, List(Entry("old")));
        var plan = Ask(sync, fixture, new("remove", "old"), T0.AddMinutes(3));
        fixture.Write(b, List(Entry("old", extra: entry => entry["createdAt"] = 1763700000000)));
        var stale = Ask(sync, fixture, new ClaudeScheduledRequest("remove", "old", Apply: true, Expect: Token(plan)), T0.AddMinutes(3));
        Check(Status(stale) == "token_mismatch" && Are(fixture.Ids(b), "old"), "a removal applies only to the entries its plan showed");
        Apply(sync, fixture, Mirror("brief", a), T0.AddMinutes(4));
        var mirrored = Ask(sync, fixture, new("remove", "brief"), T0.AddMinutes(4));
        Check(Status(mirrored) == "refused" && Text(mirrored["reason"]) == "mirrored", "a mirrored task is not removed");
        Check(Status(Ask(sync, fixture, new("unmirror", "brief"), T0.AddMinutes(5))) == "ok"
              && Status(Ask(sync, fixture, new("unmirror", "brief"), T0.AddMinutes(5))) == "not_mirrored",
            "unmirror forgets the mirror first");
        removed = Apply(sync, fixture, new("remove", "brief"), T0.AddMinutes(6));
        Check(Status(removed) == "removed" && fixture.Accounts.All(account => !fixture.Ids(account).Contains("brief")), "then it can be removed");
        fixture.Write(b, List());
        Pass(sync, fixture, T0.AddMinutes(7));
        Check(fixture.Accounts.All(account => !fixture.Ids(account).Contains("brief")), "and nothing brings it back");
    }

    // D12, review P1-7: the tasks a mirror replaces are removed in the same write that gives a list the mirror.
    private static void TestReplaces()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief-1", "brief-2", "brief-3");
        fixture.Write(a, List(Entry("brief-1")));
        fixture.Write(b, List(Entry("brief-2", extra: Bypass)));
        fixture.Write(c, List(Entry("brief-1"), Entry("brief-3")));
        var sync = fixture.Sync();
        Check(Text(Ask(sync, fixture, Mirror("brief-1", a, "brief-1"), T0)["reason"]) == "invalid_replaces", "a mirror does not replace itself");
        var plan = Ask(sync, fixture, Mirror("brief-1", a, "brief-2", "brief-3"), T0);
        var removes = Target(plan, b)["removes"]!.AsArray();
        Check(Action(plan, b) == "insert" && removes.Count == 1 && Text(removes[0]!["id"]) == "brief-2"
              && Text(removes[0]!["permission"]!["mode"]) == "bypassPermissions"
              && Action(plan, c) == "same" && Text(Target(plan, c)["removes"]![0]!["id"]) == "brief-3",
            "the plan shows what each list's mirror replaces, with its mode");
        var aBefore = fixture.Bytes(a);
        var applied = Ask(sync, fixture, Mirror("brief-1", a, "brief-2", "brief-3") with { Apply = true, Expect = Token(plan) }, T0);
        Check(Status(applied) == "mirrored" && Are(fixture.Ids(b), "brief-1") && Are(fixture.Ids(c), "brief-1") && fixture.Bytes(a).SequenceEqual(aBefore),
            "the apply gives each list the mirror and removes what it replaces, in one write");
        // B gets an older save back: the pass makes the mirror there again and removes what it replaces with it.
        fixture.Write(b, List(Entry("brief-2", extra: Bypass)));
        Pass(sync, fixture, T0.AddMinutes(1));
        Check(Are(fixture.Ids(b), "brief-1"), "a copy made again replaces in the same write");
        Check(Text(Ask(sync, fixture, Mirror("brief-2", b, "brief-1"), T0)["reason"]) == "replaces_mirrored", "a mirrored task is not replaced");
    }

    // D13, review P1-8: a list not made yet is reported, the definitions have to be where the app reads them, and
    // SKILL.md a file that reads.
    private static void TestPreparation()
    {
        using var fixture = new Fixture("A", "B", "D");
        var (a, b, d) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief");
        fixture.Write(a, List(Entry("brief"))); fixture.Write(b, List());
        var sync = fixture.Sync();
        var plan = Ask(sync, fixture, Mirror("brief", a), T0);
        Check(Action(plan, d) == "no_list" && Text(Target(plan, d)["note"])!.Contains("open this account"), "a registered account without a list is shown");
        Ask(sync, fixture, Mirror("brief", a) with { Apply = true, Expect = Token(plan) }, T0);
        var pass = Pass(sync, fixture, T0.AddMinutes(1));
        Check(pass.Notice is { Kind: ClaudeScheduledProblemKind.Unfilled, Detail: "brief: D" } && !File.Exists(fixture.PathOf(d)),
            "and reported: no list is made for it");
        fixture.Write(d, List());
        Pass(sync, fixture, T0.AddMinutes(2));
        Check(fixture.Task(d, "brief") is not null, "once the app has made it, it is filled");
        // The app reads the definitions elsewhere.
        var elsewhere = new ClaudeScheduledSync(fixture.Sessions, fixture.Store, fixture.Definitions, Path.Combine(fixture.Root, "other"), () => new MemoryStream());
        fixture.Write(b, List());
        Check(Text(Ask(elsewhere, fixture, Mirror("brief", a), T0)["reason"]) == "definition_root"
              && Pass(elsewhere, fixture, T0.AddMinutes(3)).Notice is { Kind: ClaudeScheduledProblemKind.Stopped, Detail: "brief: definition_root" }
              && fixture.Ids(b).Length == 0,
            "while the app reads the definitions from another folder, nothing is written");
        string? Problem() => Text(Ask(sync, fixture, Mirror("brief", a), T0)["reason"]);
        var skill = Path.Combine(fixture.Definitions, "brief", "SKILL.md");
        File.Delete(skill);
        var missing = Problem();
        Directory.CreateDirectory(skill);
        var folder = Problem();
        Directory.Delete(skill);
        Directory.Delete(Path.Combine(fixture.Definitions, "brief"));
        var real = Directory.CreateDirectory(Path.Combine(fixture.Root, "real-brief")).FullName;
        File.WriteAllText(Path.Combine(real, "SKILL.md"), "---\nname: brief\n---\nMade up.\n");
        Link("/J", Path.Combine(fixture.Definitions, "brief"), real);
        var junction = Problem();
        Check(missing == "definition_unready" && folder == "definition_unready" && junction == "definition_unready",
            "a definition folder without SKILL.md, with a folder in its place, or behind a junction is not ready");
    }

    // D14, review P1-9: an apply writes only the plan whose token it names; --from names one list.
    private static void TestTokenBindsPlan()
    {
        var org = Guid.NewGuid().ToString("D");
        using var fixture = new Fixture(new ClaudeShelfAccount[]
        {
            new(Guid.NewGuid().ToString("D"), org, "A"), new(Guid.NewGuid().ToString("D"), org, "Same"),
            new(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), "Same")
        });
        var (a, s1, s2) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief");
        fixture.Write(a, List(Entry("brief"))); fixture.Write(s1, List(Entry("brief", extra: entry => entry["enabled"] = false))); fixture.Write(s2, List());
        var sync = fixture.Sync();
        var plan = Ask(sync, fixture, new("mirror", "brief", "loaded"), T0, loaded: a);
        Check(Status(plan) == "plan" && Text(plan["from"]!["key"]) == a.Key, "--from loaded takes the list the app shows");
        // The source is switched off before the apply: the plan is not the one shown.
        fixture.Edit(a, "brief", entry => entry["enabled"] = false);
        var stale = Ask(sync, fixture, new ClaudeScheduledRequest("mirror", "brief", "loaded", Apply: true, Expect: Token(plan)), T0, loaded: a);
        Check(Status(stale) == "token_mismatch" && Token(stale) != Token(plan) && fixture.Ids(s2).Length == 0 && !File.Exists(fixture.StatePath),
            "an apply whose plan changed shows the new plan and writes nothing");
        Check(Text(Ask(sync, fixture, new("mirror", "brief", "Same"), T0)["reason"]) == "from_ambiguous"
              && Text(Ask(sync, fixture, new("mirror", "brief", "Nobody"), T0)["reason"]) == "from_unknown"
              && Text(Ask(sync, fixture, new("mirror", "brief", "loaded"), T0)["reason"]) == "app_closed"
              && Status(Ask(sync, fixture, new("mirror", "brief", "a"), T0, loaded: a)) == "plan",
            "a label naming two lists is refused, an unknown one too; a label naming one is taken, whatever its case");
        var unknown = sync.Serve(new("mirror", "brief", "loaded"), sync.Scene(fixture.Accounts,
            new ClaudeScheduledLogView(false, null, false, null, 0), running: true)!, _ => false, T0, true);
        Check(Text(unknown["reason"]) == "loaded_unknown", "--from loaded is refused while the loaded list is unknown");
    }

    // D15: what a mirror takes and leaves.
    private static void TestFields()
    {
        using var fixture = new Fixture("A", "B", "C");
        var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
        fixture.Define("brief");
        fixture.Write(a, List(Entry("brief", extra: entry =>
        {
            entry["useWorktree"] = true; entry["sourceBranch"] = "made-up-branch";
            entry["promptFileHash"] = "made-up-hash-a"; entry["promptWrittenBy"] = new JsonArray("made-up-session");
            entry["approvedPermissions"] = new JsonArray(new JsonObject { ["toolName"] = "made-up" });
            entry["chromePermissionMode"] = "ask"; entry["chromeAllowedDomains"] = new JsonArray("example.invalid");
            entry["observedToolUse"] = new JsonObject(); entry["notifySessionId"] = "made-up-session";
            entry["madeByClaude"] = true; entry["lastRunAt"] = "2026-11-19T09:00:04.000Z";
        })));
        fixture.Write(b, List(Entry("brief", extra: entry =>
        {
            entry["useWorktree"] = true; entry["sourceBranch"] = "made-up-branch";
            entry["model"] = "made-up-model-x"; entry["displayName"] = "Old name"; entry["madeByClaude"] = false;
            entry["promptFileHash"] = "made-up-hash-b"; entry["approvedPermissions"] = new JsonArray(new JsonObject { ["toolName"] = "kept" });
        })));
        fixture.Write(c, List());
        var sync = fixture.Sync();
        var plan = Ask(sync, fixture, Mirror("brief", a), T0);
        var model = Target(plan, b)["fields"]!.AsArray().First(field => Text(field!["name"]) == "model")!;
        Check(Are(Fields(plan, b), "displayName", "model") && Text(model["before"]) == "made-up-model-x" && Text(model["after"]) == "(none: the definition's)"
              && Text(plan["settings"]!["model"]) == "(none: the definition's)",
            "a model set and none set (the definition's) are told apart; the prompt's hash is not compared");
        Check(plan["notCopied"]!.AsArray().Select(Text).Order(StringComparer.Ordinal).SequenceEqual(new[]
              { "approvedPermissions", "chromeAllowedDomains", "chromePermissionMode", "madeByClaude", "notifySessionId", "observedToolUse",
                "promptFileHash", "promptWrittenBy" }),
            "the plan lists what is not taken");
        Ask(sync, fixture, Mirror("brief", a) with { Apply = true, Expect = Token(plan) }, T0);
        var copy = fixture.Task(c, "brief")!;
        Check(copy.Select(member => member.Key).Order(StringComparer.Ordinal).SequenceEqual(new[]
              { "createdAt", "cronExpression", "cwd", "displayName", "enabled", "filePath", "id", "missedRunScanFloor", "sourceBranch", "useWorktree" })
              && copy["filePath"]!.GetValue<string>() == Path.Combine(fixture.Definitions, "brief", "SKILL.md"),
            "a copy has the settings, the source's createdAt, its definition and a floor: no prompt record, approval, Chrome site, session or origin");
        var fixedEntry = fixture.Task(b, "brief")!;
        Check(!fixedEntry.ContainsKey("model") && fixedEntry["displayName"]!.GetValue<string>() == "Task brief"
              && fixedEntry["madeByClaude"]!.GetValue<bool>() == false && fixedEntry["promptFileHash"]!.GetValue<string>() == "made-up-hash-b"
              && fixedEntry["approvedPermissions"]![0]!["toolName"]!.GetValue<string>() == "kept",
            "a fixed entry gets the settings exactly, a model none set removed, and keeps its own record, approvals and origin");
    }

    // D16: the commands go by a channel of their version; one on the first version's is refused.
    private static void TestCommandChannel()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Define("brief");
        fixture.Write(a, List(Entry("brief"))); fixture.Write(b, List());
        using var app = new FakeApp(fixture);
        app.Runtime = new ClaudeDesktopRuntime(ClaudeDesktopRunState.Closed, null);
        app.After(TimeSpan.Zero);
        var suffix = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(fixture.Ledger)).ToLowerInvariant())))[..16];
        using var requested = EventWaitHandle.OpenExisting(@"Local\CodexWeeklyUsageIndicator.ScheduledSeed." + suffix);
        using var answered = EventWaitHandle.OpenExisting(@"Local\CodexWeeklyUsageIndicator.ScheduledSeeded." + suffix);
        using var mailbox = MemoryMappedFile.OpenExisting(@"Local\CodexWeeklyUsageIndicator.ScheduledSeedMailbox." + suffix);
        using var view = mailbox.CreateViewAccessor(0, 1024 * 1024);
        ClaudeSessionSync.WriteSlot(view, 0, 65536, Encoding.UTF8.GetBytes("{\"id\":\"made-up-1\",\"op\":\"seed\",\"tasks\":[]}"));
        answered.Reset();
        requested.Set();
        app.After(TimeSpan.FromSeconds(1));
        var text = ClaudeSessionSync.ReadSlot(view, 65536, 1024 * 1024 - 65536) ?? "";
        Check(text.StartsWith("made-up-1\n", StringComparison.Ordinal)
              && JsonNode.Parse(text[10..])?["reason"]?.GetValue<string>() == "old_command_version" && fixture.Ids(b).Length == 0,
            "a command of the first version is answered with a refusal, and does nothing");
        Check(Status(JsonNode.Parse(app.Ask(Mirror("brief", a)) ?? "{}")!.AsObject()) == "plan", "this version's is answered");
        Check(ClaudeScheduledChannel.Ask(Path.Combine(fixture.Root, "no-widget"), TimeSpan.FromSeconds(2), Mirror("brief", a)) is null,
            "without a widget of this version a command gets no answer");
    }

    // D5, D17: the new list is written beside the old one and moved over it only while writing is still allowed; the
    // old one is backed up first, the newest five kept.
    private static void TestWritesAtomicAndBackedUp()
    {
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Define("brief");
        fixture.Write(a, List(Entry("brief"))); fixture.Write(b, List());
        var sync = fixture.Sync();
        var plan = Ask(sync, fixture, Mirror("brief", a), T0);
        var before = fixture.Bytes(b);
        var asked = 0; var besideFirst = false;
        bool Leaves(string key)
        {
            if (key != b.Key) return true;
            if (++asked == 2) besideFirst = Directory.GetFiles(fixture.FolderOf(b), ".scheduled-tasks.*.widget-mirror.partial").Length == 1;
            return asked < 2;
        }
        var refused = sync.Serve(Mirror("brief", a) with { Apply = true, Expect = Token(plan) }, Scene(sync, fixture), Leaves, T0, true);
        Check(Status(refused) == "mirrored" && Text(Target(refused, b)["result"]) == "not written now" && besideFirst
              && fixture.Bytes(b).SequenceEqual(before) && Directory.GetFiles(fixture.FolderOf(b)).Length == 1,
            "refused at the last moment, the old list stays and no partial is left");
        Pass(sync, fixture, T0.AddMinutes(1));
        Check(fixture.Task(b, "brief") is not null && Directory.GetFiles(sync.BackupFolder(b.Key)).Any(path => File.ReadAllBytes(path).SequenceEqual(before)),
            "the next pass writes it, the old one kept");
        for (var round = 1; round <= 6; round++)
        {
            fixture.Write(b, List(Entry("other", extra: entry => entry["displayName"] = "round " + round)));
            Pass(sync, fixture, T0.AddMinutes(1 + round));
        }
        Check(Directory.GetFiles(sync.BackupFolder(b.Key)).Length == 5, "the newest five backups are kept");
    }

    // D17 kept: a list reached through a link, or by two names, is neither written nor read.
    private static void TestRedirectedListsRefused()
    {
        var org = Guid.NewGuid().ToString("D");
        using (var fixture = new Fixture(new ClaudeShelfAccount[] { new(Guid.NewGuid().ToString("D"), org, "A"), new(Guid.NewGuid().ToString("D"), org, "B"),
                   new(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), "C") }))
        {
            var (a, b, c) = (fixture[0], fixture[1], fixture[2]);
            fixture.Define("brief");
            fixture.Write(a, List()); fixture.Write(c, List(Entry("brief")));
            // B's account folder is a junction to A's, of the same organization: B's list is A's file under another name.
            Directory.Delete(Path.Combine(fixture.Sessions, b.AccountUuid), recursive: true);
            Link("/J", Path.Combine(fixture.Sessions, b.AccountUuid), Path.Combine(fixture.Sessions, a.AccountUuid));
            var shown = fixture.Bytes(a);
            var sync = fixture.Sync();
            var applied = Apply(sync, fixture, Mirror("brief", c), T0);
            var pass = Pass(sync, fixture, T0.AddMinutes(1));
            Check(fixture.Bytes(a).SequenceEqual(shown) && Action(applied, a) == "unreadable" && Action(applied, b) == "unreadable"
                  && pass.Unreadable.Contains("A") && pass.Unreadable.Contains("B") && sync.SameFolder(b.Key, a.Key),
                "a list reached through an account folder that is a junction is neither written nor read, nor is the list it reaches");
        }
        using (var fixture = new Fixture("D", "E", "F", "G", "H"))
        {
            var (d, e, f, g, h) = (fixture[0], fixture[1], fixture[2], fixture[3], fixture[4]);
            fixture.Define("brief");
            fixture.Write(d, List()); fixture.Write(f, List()); fixture.Write(h, List(Entry("brief")));
            // E's organization folder is a junction to D's; G's list is a second name of F's.
            Directory.Delete(fixture.FolderOf(e));
            Link("/J", fixture.FolderOf(e), fixture.FolderOf(d));
            Link("/H", fixture.PathOf(g), fixture.PathOf(f));
            var (dBefore, fBefore) = (fixture.Bytes(d), fixture.Bytes(f));
            var sync = fixture.Sync();
            Apply(sync, fixture, Mirror("brief", h), T0);
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

    // D13: the definitions as this process finds them, and as the app does.
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
            Check(paths.ScheduledTasksRoot == Path.Combine(fixture.Root, "made-up-config", "scheduled-tasks")
                  && paths.AppScheduledTasksRoot == ClaudeScheduledSync.AppDefinitionsRoot(),
                "this process looks where its own CLAUDE_CONFIG_DIR says, and takes the app's from the user's environment");
            var explicitPaths = ClaudeShelfPaths.CreateDefault(Path.Combine(fixture.Root, "named"), Path.Combine(fixture.Root, "app"));
            Check(explicitPaths.ScheduledTasksRoot == explicitPaths.AppScheduledTasksRoot, "a folder named for both is both");
        }
        finally { Environment.SetEnvironmentVariable("CLAUDE_CONFIG_DIR", saved); }
    }

    // The command line, and a plan made outside the widget, which cannot write.
    private static void TestCommandLine()
    {
        Check(ClaudeScheduledCommand.Parse("mirror", ["brief", "--from", "a:b", "--replaces", "x", "y", "--apply", "--expect", "t", "--allow-raise"]) is
              { Task: "brief", From: "a:b", Apply: true, Expect: "t", AllowRaise: true } mirror && Are(mirror.Replaces, "x", "y")
              && ClaudeScheduledCommand.Parse("mirror", ["brief"]) is null && ClaudeScheduledCommand.Parse("mirror", ["brief", "--from", "a", "--apply"]) is null
              && ClaudeScheduledCommand.Parse("remove", ["old", "--apply", "--expect", "t"]) is { Apply: true }
              && ClaudeScheduledCommand.Parse("remove", ["old", "--from", "a"]) is null && ClaudeScheduledCommand.Parse("unmirror", ["brief", "--apply"]) is null
              && ClaudeScheduledCommand.Parse("unmirror", []) is null,
            "a mirror names its source; an apply needs its token; a removal and an unmirror take no source");
        Check(ClaudeScheduledCommand.Exit("{\"status\":\"plan\"}") == 0 && ClaudeScheduledCommand.Exit("{\"status\":\"mirrored\"}") == 0
              && ClaudeScheduledCommand.Exit("{\"status\":\"token_mismatch\"}") == 1 && ClaudeScheduledCommand.Exit("not json") == 1,
            "the commands exit 0 for a plan or what was asked done");
        // The widget is a window program, started with no console of its own: its answer must still reach a shell. Only
        // the usage answer is asked for, which reads nothing.
        foreach (var flag in new[] { ClaudeScheduledCommand.MirrorFlag, ClaudeScheduledCommand.RemoveFlag, ClaudeScheduledCommand.UnmirrorFlag })
        {
            var start = new ProcessStartInfo(Path.Combine(AppContext.BaseDirectory, "WeeklyUsageIndicator.exe"))
            { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add(flag);
            using var process = Process.Start(start) ?? throw new InvalidOperationException("The widget did not start.");
            var output = process.StandardOutput.ReadToEndAsync();
            var errors = process.StandardError.ReadToEndAsync();
            var ended = process.WaitForExit(60_000);
            if (!ended) process.Kill(entireProcessTree: true);
            var answer = ended && output.Result.TrimStart().StartsWith('{') ? JsonNode.Parse(output.Result) as JsonObject : null;
            Check(ended && process.ExitCode == 2 && Text(answer?["status"]) == "usage" && errors.Result.Length == 0,
                flag + " prints its usage to a shell from the window program");
        }
        using var fixture = new Fixture("A", "B");
        var (a, b) = (fixture[0], fixture[1]);
        fixture.Define("brief");
        fixture.Write(a, List(Entry("brief"))); fixture.Write(b, List());
        var readOnly = fixture.Sync(readOnly: true);
        var calls = ClaudeScheduledSync.WriteCalls;
        var plan = Ask(readOnly, fixture, Mirror("brief", a), T0);
        Check(Status(plan) == "plan" && ClaudeScheduledSync.WriteCalls == calls && !Directory.Exists(fixture.Store),
            "a plan made outside the widget enters nothing that writes");
        Apply(fixture.Sync(), fixture, Mirror("brief", a), T0, loaded: b);
        calls = ClaudeScheduledSync.WriteCalls;
        var preview = readOnly.Pass(Scene(readOnly, fixture), _ => true, T0.AddMinutes(1), preview: true);
        Check(preview.Report![0]!["accounts"]!.AsArray().Any(item => Text(item!["action"]) == "make") && ClaudeScheduledSync.WriteCalls == calls
              && fixture.Ids(b).Length == 0,
            "nor does a preview of the next pass, which shows what it would make");
        Check(Throws<InvalidOperationException>(() => readOnly.Serve(Mirror("brief", a) with { Apply = true, Expect = Token(plan) },
                  Scene(readOnly, fixture), _ => true, T0, true)) && fixture.Ids(b).Length == 0,
            "an apply there throws before anything is written");
        // The store cannot be written: a folder stands where its state goes.
        using var stuck = new Fixture("C", "D");
        var (c, d) = (stuck[0], stuck[1]);
        stuck.Define("brief");
        stuck.Write(c, List(Entry("brief"))); stuck.Write(d, List());
        Directory.CreateDirectory(stuck.StatePath);
        var unkept = Apply(stuck.Sync(), stuck, Mirror("brief", c), T0);
        Check(Status(unkept) == "store_unwritable" && stuck.Ids(d).Length == 0, "a mirror that cannot be kept writes nothing");
    }

    // D3: the app's scheduled-task lines, as the log watcher reads them.
    private static void TestLogLines()
    {
        var since = DateTime.Now.AddHours(-1);
        since = new DateTime(since.Year, since.Month, since.Day, since.Hour, since.Minute, since.Second);
        string Line(int second, string text) => since.AddSeconds(second).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " [info] " + text;
        var (account, org) = (Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));
        var log = new ClaudeScheduledLog();
        log.Start();
        void Read(params string[] lines) => ClaudeDesktopLogWatcher.Apply(ClaudeDesktopAccountState.Unknown, lines, since, [], null, log);
        Check(log.View(true) is { Loaded: null, Pending: true }, "at the start of a run no list is known loaded");
        Read(Line(1, "[CCDScheduledTasks] Reset"),
            Line(2, $"[CCDScheduledTasks] Initialized {{ accountId: '{account.ToUpperInvariant()}', orgId: '{org}' }}"));
        var view = log.View(true);
        Check(view is { Loaded: { } loaded, Pending: false } && loaded == account + "/" + org && view.LastLine == new DateTimeOffset(since.AddSeconds(2)),
            "the log names the list the manager loaded, whoever's it is, and when its last line was");
        Read(Line(3, "[CCDScheduledTasks] Reset"));
        Check(log.View(true) is { Loaded: null, Pending: true }, "after a reset no list is loaded until the next load");
        Read(Line(4, "[CCDScheduledTasks] Initialized { accountId: 'made-up', orgId: 'made-up' }"));
        Check(log.View(true) is { Loaded: null, Pending: true }, "a load line that does not read leaves it unknown, and nothing is written");
        log.Close();
        Check(log.View(false) is { Known: false, Loaded: null, Pending: false }, "a closed app holds no list");
    }

    // The real use: five accounts, three briefs under three ids (two carrying bypassPermissions), the app showing one of
    // the accounts lacking any. One brief is mirrored and replaces the other two; no account gets a mode it did not have.
    private static void TestServiceFiveAccounts()
    {
        using var fixture = new Fixture("A", "B", "C", "D", "E");
        var (a, b, c, d, e) = (fixture[0], fixture[1], fixture[2], fixture[3], fixture[4]);
        // Each brief's model is in its definition's frontmatter (Define), none in the entries.
        fixture.Define("made-up-brief-1", "made-up-brief-2", "made-up-brief-3");
        fixture.Write(a, List(Entry("made-up-brief-1", "0 9,18 * * *", Bypass)));
        fixture.Write(b, List(Entry("made-up-brief-2", "0 9,18 * * *", Bypass)));
        fixture.Write(c, List(Entry("made-up-brief-3", "0 9,18 * * *")));
        fixture.Write(d, List()); fixture.Write(e, List());
        bool Raised(JsonObject entry) => entry.ContainsKey("permissionMode") || entry.ContainsKey("permissionModeCarried");
        var had = fixture.Accounts.ToDictionary(account => account.Key,
            account => fixture.Ids(account).Where(id => Raised(fixture.Task(account, id)!)).ToHashSet(StringComparer.Ordinal));
        void NoNewModes(string when) => Check(fixture.Accounts.All(account =>
                fixture.Ids(account).Where(id => Raised(fixture.Task(account, id)!)).All(had[account.Key].Contains)),
            "no account gets a task with a mode it did not have: " + when);
        using var app = new FakeApp(fixture);
        app.After(TimeSpan.Zero, app.Loads(d));
        app.After(TimeSpan.FromSeconds(7));
        NoNewModes("before anything is mirrored");
        Check(fixture.Ids(e).Length == 0, "nothing is mirrored before a person chooses");
        var request = Mirror("made-up-brief-3", c, "made-up-brief-1", "made-up-brief-2");
        var plan = JsonNode.Parse(app.Ask(request) ?? "{}")!.AsObject();
        Check(Status(plan) == "plan" && plan["raises"]!.GetValue<int>() == 0 && Action(plan, a) == "insert" && Action(plan, b) == "insert"
              && Action(plan, d) == "insert" && Action(plan, e) == "insert" && Action(plan, c) == "source"
              && Text(Target(plan, a)["removes"]![0]!["permission"]!["mode"]) == "bypassPermissions"
              && Text(Target(plan, a)["removes"]![0]!["permission"]!["startsAs"]!["killSwitchOn"]) == "default",
            "the plan fills four accounts with the chosen brief, replacing the two others, whose modes it shows");
        var applied = JsonNode.Parse(app.Ask(request with { Apply = true, Expect = Token(plan) }) ?? "{}")!.AsObject();
        Check(Status(applied) == "mirrored" && Are(fixture.Ids(a), "made-up-brief-3") && Are(fixture.Ids(b), "made-up-brief-3")
              && Are(fixture.Ids(e), "made-up-brief-3") && fixture.Ids(d).Length == 0 && Text(applied["next"]) is not null,
            "the apply writes the lists the app has not loaded, and says the loaded one comes later");
        Check(new[] { a, b, e }.All(account => fixture.Task(account, "made-up-brief-3") is { } brief && !Raised(brief) && !brief.ContainsKey("model")),
            "with no permission field and no model added");
        NoNewModes("after the apply");
        app.After(TimeSpan.Zero, app.Loads(e));
        app.After(TimeSpan.FromSeconds(7));
        Check(Are(fixture.Ids(d), "made-up-brief-3"), "the list the app showed gets it once the app leaves it");
        app.Runtime = new ClaudeDesktopRuntime(ClaudeDesktopRunState.Closed, null);
        app.After(TimeSpan.FromSeconds(2));
        app.After(TimeSpan.FromSeconds(6));
        app.After(TimeSpan.FromMinutes(11));
        Check(fixture.Accounts.All(account => Are(fixture.Ids(account), "made-up-brief-3")) && app.After(TimeSpan.FromSeconds(3)).Scheduled is null,
            "every account holds the one brief, the two replaced do not come back, and nothing is reported");
        NoNewModes("after the app closed");
    }

    // D4: the probe reads every part of the app the mirror depends on, the nested schemas, the account hooks and the
    // log lines included (the fixed points read by value are ClaudeScheduledProbeTests').
    private static void TestProbe()
    {
        var package = ClaudeDesktopProbe.FindPackage();
        var verdict = package is null ? (ClaudeDesktopContract?)null : ClaudeDesktopProbe.ScheduledContract(package.AsarPath);
        // The version the mirror was reviewed against has to read as reviewed; another one may read either way.
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

    /// <summary>The widget's service over a fake app: its run state (running from the fixture's installation), its log
    /// and the scheduled-list contract.</summary>
    private sealed class FakeApp : IDisposable
    {
        private readonly string _log, _ledger, _image;
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
            _image = Path.Combine(fixture.Root, "app", "claude.exe");
            var start = DateTime.Now.AddHours(-1);
            _start = new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute, start.Second);
            _now = new DateTimeOffset(_start).AddMinutes(1);
            Runtime = Running();
            var package = new ClaudeDesktopPackage("Claude_9.9.9.9_x64__pzs8sxrjxfjjc", new Version(9, 9, 9, 9), fixture.Root, "Claude_pzs8sxrjxfjjc");
            var environment = new ClaudeShelfEnvironment(() => Runtime, () => package, _ => ClaudeDesktopContract.Reviewed, _ => { },
                () => _now, _ => Scheduled);
            var sources = new ClaudeSessionSync.Sources(fixture.Sessions, Path.Combine(fixture.Root, "Local Storage", "leveldb"),
                Path.Combine(fixture.Root, "IndexedDB", "https_claude.ai_0.indexeddb.leveldb"), fixture.Projects, fixture.Ledger);
            _service = new ClaudeShelfService(_owner, () => [], fixture.Shelf(), environment, new ClaudeDesktopLogWatcher(logs), sources);
            _service.SetAccountsForTest(fixture.Accounts);
        }

        /// <summary>The app running since this one's start, from the fixture's installation unless images are named.</summary>
        internal ClaudeDesktopRuntime Running(IReadOnlyList<string>? images = null) =>
            new(ClaudeDesktopRunState.Running, _start, images ?? [_image]);

        private string Line(string text) => _now.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " [info] " + text;

        /// <summary>The app's scheduled-task manager letting its list go, now.</summary>
        internal string Reset() => Line("[CCDScheduledTasks] Reset");

        /// <summary>The manager loading this account's list, now.</summary>
        internal string Initialized(ClaudeShelfAccount account) =>
            Line($"[CCDScheduledTasks] Initialized {{ accountId: '{account.AccountUuid}', orgId: '{account.OrgUuid}' }}");

        /// <summary>The app coming to this account: a reset, then the load.</summary>
        internal string[] Loads(ClaudeShelfAccount account) => [Reset(), Initialized(account)];

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
            var asking = Task.Run(() => ClaudeScheduledChannel.Ask(_ledger, TimeSpan.FromSeconds(30), request));
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
        internal string StatePath => Path.Combine(Store, ClaudeScheduledSync.StateFile);
        // Accounts of their own: a running widget listens on names of its own ledger and must not hear a test.
        internal ClaudeShelfAccount[] Accounts { get; }

        internal Fixture(params string[] labels) : this(labels.Select(label =>
            new ClaudeShelfAccount(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), label, "p-" + label)).ToArray())
        { }

        internal Fixture(ClaudeShelfAccount[] accounts)
        {
            Accounts = accounts;
            foreach (var account in Accounts) Directory.CreateDirectory(FolderOf(account));
            Directory.CreateDirectory(Projects);
            Directory.CreateDirectory(Definitions);
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

        internal void Write(ClaudeShelfAccount account, JsonObject list) =>
            File.WriteAllText(PathOf(account), list.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));

        /// <summary>Changes one entry of a list, as the app saves it.</summary>
        internal void Edit(ClaudeShelfAccount account, string id, Action<JsonObject> change)
        {
            var list = Read(account);
            change(list["scheduledTasks"]!.AsArray().First(entry => entry!["id"]!.GetValue<string>() == id)!.AsObject());
            Write(account, list);
        }

        internal byte[] Bytes(ClaudeShelfAccount account) => File.ReadAllBytes(PathOf(account));
        internal JsonObject Read(ClaudeShelfAccount account) => JsonNode.Parse(Bytes(account))!.AsObject();
        internal string[] Ids(ClaudeShelfAccount account) => Read(account)["scheduledTasks"]!.AsArray().Select(entry => entry!["id"]!.GetValue<string>()).ToArray();
        internal JsonObject? Task(ClaudeShelfAccount account, string id) =>
            Read(account)["scheduledTasks"]!.AsArray().FirstOrDefault(entry => entry!["id"]!.GetValue<string>() == id)?.AsObject();
        internal ClaudeScheduledSync Sync(bool readOnly = false) => new(Sessions, Store, Definitions, Definitions, () => new MemoryStream(), readOnly);
        internal ClaudeSessionShelf Shelf() => new(new ClaudeShelfPaths(Sessions, Projects, Path.Combine(Root, "scratch-workspaces"), [], Ledger,
                ScheduledTasksRoot: Definitions, AppScheduledTasksRoot: Definitions),
            () => new ClaudeLiveSessionScan(new HashSet<string>(), false), () => new MemoryStream());
        public void Dispose() { try { Directory.Delete(Root, recursive: true); } catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { } }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
