using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WeeklyUsageIndicator;

internal static class ClaudeSessionShelfTests
{
    private const string AccountA = "1a1a1a1a-0000-4000-8000-00000000001a", OrgA = "1b1b1b1b-0000-4000-8000-00000000001b";
    private const string AccountB = "4a4a4a4a-0000-4000-8000-00000000004a", OrgB = "4b4b4b4b-0000-4000-8000-00000000004b";
    private static readonly ClaudeShelfAccount A = new(AccountA, OrgA, "Main"), B = new(AccountB, OrgB, "Sub");

    internal static async Task RunAsync()
    {
        TestPlanningAndPublishing();
        TestExclusionsAndHolds();
        TestScansFollowChangedRecords();
        TestWorktreeSessions();
        TestFillingWhileRunning();
        TestServiceFillsWhileRunning();
        TestServiceStartedWhileSignedOut();
        TestWriterAndDesktopRaces();
        TestRecordFields();
        TestUndoAndPendingRecovery();
        TestAccountMapping();
        TestLogObservation();
        TestLogGapsAndRotation();
        TestContractGuard();
        TestWidgetGate();
        await TestFollowingTheAppAccount();
        TestRelaunchReservation();
        TestLayoutKeeping();
        TestConversationLineage();
        TestSidebarPrefs();
        TestLineageEdges();
        TestLocalStorage();
        TestTitlesFollowOriginals();
        TestAdvancingOlderStates();
        TestAdvanceHoldsAndRecovery();
        TestVerifiedContinuation();
        TestTranscriptInTwoFolders();
    }

    private static void TestPlanningAndPublishing()
    {
        using var fixture = new Fixture();
        var cli = fixture.Transcript();
        fixture.Record(A, "local_aaaaaaaa-0000-4000-8000-000000000001", cli, extra: record =>
        { record["permissionMode"] = "auto"; record["bridgeSessionIds"] = new JsonArray("session_x"); });
        fixture.Record(A, "local_aaaaaaaa-0000-4000-8000-000000000002", fixture.Transcript(), archived: true);
        fixture.Record(A, "local_aaaaaaaa-0000-4000-8000-000000000003", null);
        var shelf = fixture.Shelf();
        var plan = shelf.Plan([A, B]);
        Check(plan.Actions.Count == 1 && plan.PendingFor(B.Key) == 1 && plan.PendingFor(A.Key) == 0,
            "only an eligible session of another account is planned");
        Check(plan.Skipped.Values.Single()["archived"] == 1 && plan.Skipped.Values.Single()["no_current_id"] == 1,
            "archived and handle-less sessions are skipped with a reason");
        var result = shelf.Apply([A, B], () => true, "test");
        var created = fixture.Files(B).Single();
        var record = JsonNode.Parse(File.ReadAllText(created))!.AsObject();
        Check(result.Created == 1 && record["cliSessionId"]!.GetValue<string>() == cli
              && record["sessionId"]!.GetValue<string>() + ".json" == Path.GetFileName(created)
              && record["sessionId"]!.GetValue<string>() != "local_aaaaaaaa-0000-4000-8000-000000000001",
            "the new record points at the same transcript under a fresh app session id");
        Check(record["permissionMode"] is null && record["bridgeSessionIds"] is null,
            "permissions and account-bound connections are never carried");
        Check(fixture.Files(A).Count == 3 && fixture.Hashes(A).SequenceEqual(fixture.OriginalHashes),
            "the source account's records are untouched");
        Check(shelf.Apply([A, B], () => true, "test").Created == 0, "a second run finds the session already present");
        var ledger = JsonNode.Parse(File.ReadAllText(Path.Combine(fixture.Ledger, "ledger.json")))!;
        Check(ledger["created"]!.AsArray().Count == 1 && ledger["created"]![0]!["pending"] is null,
            "the ledger records one confirmed creation");
    }

    private static void TestExclusionsAndHolds()
    {
        using var fixture = new Fixture();
        fixture.Record(A, "local_bbbbbbbb-0000-4000-8000-000000000001", fixture.Transcript(), cwd: Path.Combine(fixture.Scratch, "s1"), createCwd: true);
        fixture.Record(A, "local_bbbbbbbb-0000-4000-8000-000000000002", fixture.Transcript(), cwd: Path.Combine(fixture.Root, "gone"), createCwd: false);
        fixture.Record(A, "local_bbbbbbbb-0000-4000-8000-000000000003", fixture.Transcript(), extra: r => r["sshConfig"] = new JsonObject());
        fixture.Record(A, "local_bbbbbbbb-0000-4000-8000-000000000004", fixture.Transcript(),
            extra: r => r["worktreeLazy"] = new JsonObject { ["baseBranch"] = "main" });
        fixture.Record(A, "local_bbbbbbbb-0000-4000-8000-000000000005", Guid.NewGuid().ToString("D"));
        var deletedCli = fixture.Transcript();
        fixture.Record(A, "local_bbbbbbbb-0000-4000-8000-000000000006", deletedCli);
        fixture.Record(A, "local_bbbbbbbb-0000-4000-8000-000000000007", fixture.Transcript(),
            extra: r => r["overflowCliSessionIds"] = new JsonArray(Guid.NewGuid().ToString("D")));
        Directory.CreateDirectory(fixture.ShelfPath(B));
        File.WriteAllText(Path.Combine(fixture.ShelfPath(B), "deleted_" + deletedCli), "");
        var plan = fixture.Shelf().Plan([A, B]);
        var reasons = plan.Skipped.Values.Single();
        Check(plan.Actions.Count == 0 && reasons["scratch"] == 1 && reasons["missing_cwd"] == 1 && reasons["remote"] == 1
              && reasons["worktree_lazy"] == 1 && reasons["missing_transcript"] == 1 && reasons["deleted_in_target"] == 1
              && reasons["overflow_lineage"] == 1,
            "scratch, missing folders, remote, a worktree not made yet, missing transcripts, target deletions and a lineage past what a copy carries are excluded");

        File.WriteAllText(Path.Combine(fixture.ShelfPath(B), "local_cccccccc-0000-4000-8000-000000000001.json.tmp"), "{}");
        Check(fixture.Shelf().Plan([A, B]).Held[B.Key] == "recovering", "a pending app recovery holds the target shelf");
        File.Delete(Path.Combine(fixture.ShelfPath(B), "local_cccccccc-0000-4000-8000-000000000001.json.tmp"));
        File.WriteAllText(Path.Combine(fixture.ShelfPath(B), "local_cccccccc-0000-4000-8000-000000000002.json"), "{broken");
        Check(fixture.Shelf().Plan([A, B]).Held[B.Key] == "unreadable", "an unreadable record holds the target shelf");
    }

    private static void TestScansFollowChangedRecords()
    {
        // One shelf plans every minute and keeps what it read of a record while its file stays as it was.
        using var fixture = new Fixture();
        const string Id = "local_c4c4c4c4-0000-4000-8000-000000000001";
        var path = Path.Combine(fixture.ShelfPath(A), Id + ".json");
        fixture.Record(A, Id, fixture.Transcript(), extra: r => r["title"] = "aaaa");
        var shelf = fixture.Shelf();
        // The file's stamp: its size and its creation and write times. Long unwritten, a read of it is kept.
        var old = DateTime.UtcNow.AddHours(-1);
        void Stamp(DateTime written) { File.SetCreationTimeUtc(path, old); File.SetLastWriteTimeUtc(path, written); }
        void Rewrite(string from, string to) => File.WriteAllText(path, File.ReadAllText(path).Replace(from, to));
        string Planned() => shelf.Plan([A, B]).Actions.Single().Record["title"]!.GetValue<string>();
        Stamp(old);
        Check(Planned() == "aaaa", "a record is planned");
        Rewrite("aaaa", "bbbb"); Stamp(old.AddSeconds(1));
        Check(Planned() == "bbbb", "a record written again with the same size is read again");
        // Two writes within one tick of the clock share a write time. A read made right after the file was written is
        // therefore not kept, so the second of them is seen.
        var recent = DateTime.UtcNow;
        Rewrite("bbbb", "cccc"); Stamp(recent);
        Check(Planned() == "cccc", "a record just written is read");
        Rewrite("cccc", "dddd"); Stamp(recent);
        Check(Planned() == "dddd", "a read made right after the write is not kept: the same stamp is read again");
        // Kept, a read answers for any file with its stamp, which does not name the content.
        Stamp(old.AddSeconds(2));
        Check(Planned() == "dddd", "a record long unwritten is read once");
        Rewrite("dddd", "eeee"); Stamp(old.AddSeconds(2));
        Check(Planned() == "dddd", "a file with the stamp of a kept read is taken from that read");
        var valid = File.ReadAllText(path);
        var archived = Json(path);
        archived["isArchived"] = true;
        archived["note"] = "x";
        File.WriteAllText(path, archived.ToJsonString()); Stamp(old.AddSeconds(2));
        Check(shelf.Plan([A, B]).Actions.Count == 0, "a record of another size is read again, whatever its times");
        File.WriteAllText(path, "{broken");
        Check(shelf.Plan([A, B]).Held.GetValueOrDefault(A.Key) == "unreadable",
            "a record that no longer parses is not taken from an earlier read");
        // The plans in between may rest on a kept read; the plan that is published reads every record from its file.
        File.WriteAllText(path, valid); Stamp(old.AddSeconds(3));
        Check(Planned() == "eeee", "the record is planned again");
        Rewrite("eeee", "ffff"); Stamp(old.AddSeconds(3));
        Check(Planned() == "eeee", "the kept read still answers");
        MustThrow<IOException>(() => shelf.Apply([A, B], () => true, "test"));
        Check(!Directory.Exists(fixture.ShelfPath(B)) || fixture.Files(B).Count == 0,
            "a list that is not as an earlier read had it publishes nothing");
        Check(shelf.Apply([A, B], () => true, "test").Created == 1
              && Json(fixture.Files(B).Single())["title"]!.GetValue<string>() == "ffff",
            "the next run starts from what was read then, and publishes the record as its file has it");
    }

    private static void TestWorktreeSessions()
    {
        // A session in a worktree is filled as the app's own import does: in its folder, without the worktree, which
        // stays with the account that made it.
        using var fixture = new Fixture();
        var origin = Path.Combine(fixture.Root, "repo");
        var folder = Path.Combine(origin, ".claude", "worktrees", "feature");
        fixture.Record(A, "local_babababa-0000-4000-8000-000000000001", fixture.Transcript(), cwd: folder, extra: r =>
        { r["worktreePath"] = folder; r["worktreeName"] = "feature"; r["branch"] = "claude/feature"; r["originCwd"] = origin; });
        var result = fixture.Shelf().Apply([A, B], () => true, "test");
        var copied = Json(fixture.Files(B).Single());
        Check(result.Created == 1 && copied["cwd"]!.GetValue<string>() == folder && copied["originCwd"]!.GetValue<string>() == origin
              && copied["worktreePath"] is null && copied["worktreeName"] is null && copied["branch"] is null,
            "a session in a worktree is filled in its folder, without the worktree the other account owns");
    }

    private static void TestFillingWhileRunning()
    {
        // The app shows A: only the lists it leaves alone are written; what A lacks is counted for when it closes.
        using var fixture = new Fixture();
        fixture.Record(A, "local_f0f0f0f0-0000-4000-8000-000000000001", fixture.Transcript());
        fixture.Record(B, "local_f0f0f0f0-0000-4000-8000-000000000002", fixture.Transcript());
        var shelf = fixture.Shelf();
        static bool LeavesA(string? target) => target != A.Key;
        var result = shelf.Apply([A, B], LeavesA, "test", running: true);
        Check(result.Created == 1 && fixture.Files(B).Count == 2 && fixture.Files(A).Count == 1
              && result.Targets!.SetEquals([B.Key]) && result.Plan.PendingFor(A.Key) == 1,
            "while the app runs, only the lists it leaves alone are written; what the shown one lacks is counted");
        var ledgerPath = Path.Combine(fixture.Ledger, "ledger.json");
        Check(Json(ledgerPath)["runs"]!.AsArray()[^1]!["mode"]!.GetValue<string>() == "apply-running",
            "a run that wrote is kept as one made while the app ran");
        var kept = File.ReadAllBytes(ledgerPath);
        Check(shelf.Apply([A, B], LeavesA, "test", running: true).Written == 0 && File.ReadAllBytes(ledgerPath).SequenceEqual(kept),
            "a pass that writes nothing leaves the ledger as it was: one comes every minute");

        // The app comes to B while its files are written: the rest wait for the next pass.
        fixture.Record(A, "local_f0f0f0f0-0000-4000-8000-000000000003", fixture.Transcript());
        fixture.Record(A, "local_f0f0f0f0-0000-4000-8000-000000000004", fixture.Transcript());
        var askedForB = 0;
        var cut = MustThrow<ClaudeDesktopRunningException>(() => shelf.Apply([A, B],
            target => target is null || target == B.Key && ++askedForB <= 2, "test", running: true));
        Check(cut.Created == 1 && fixture.Files(B).Count == 3 && fixture.Files(A).Count == 3,
            "the app coming to a list mid-run stops its remaining files, and the list it shows is never written");

        // The list the app shows keeps changing: only the lists written are compared between the two plans.
        using var busy = new Fixture();
        busy.Record(A, "local_f1f1f1f1-0000-4000-8000-000000000001", busy.Transcript());
        var scans = 0;
        var moving = busy.Shelf(() =>
        {
            if (++scans == 1) busy.Record(A, "local_f1f1f1f1-0000-4000-8000-000000000002", busy.Transcript());
            return new ClaudeLiveSessionScan(new HashSet<string>(), false);
        });
        Check(moving.Apply([A, B], target => target != A.Key, "test", running: true).Created == 2,
            "a change in the list the app shows does not stop the others from being filled");

        // The user deletes a conversation in the list the app shows after the plan: it is not shared. The app may remove
        // its transcript, as no other list holds it yet.
        foreach (var removeTranscript in new[] { false, true })
        {
            using var deleting = new Fixture();
            var gone = deleting.Transcript();
            deleting.Record(A, "local_f2f2f2f2-0000-4000-8000-000000000001", gone);
            deleting.Record(A, "local_f2f2f2f2-0000-4000-8000-000000000002", deleting.Transcript());
            var asked = 0;
            var shared = deleting.Shelf().Apply([A, B], target =>
            {
                // The second question for B comes right before its first file.
                if (target == B.Key && ++asked == 2)
                {
                    if (removeTranscript) File.Delete(Path.Combine(deleting.Projects, "E--work", gone + ".jsonl"));
                    else File.Delete(Path.Combine(deleting.ShelfPath(A), "local_f2f2f2f2-0000-4000-8000-000000000001.json"));
                }
                return target != A.Key;
            }, "test", running: true);
            Check(shared.Created == 1 && deleting.Files(B).Select(Json).All(record => record["cliSessionId"]!.GetValue<string>() != gone)
                  && Json(Path.Combine(deleting.Ledger, "ledger.json"))["created"]!.AsArray().Count == 1,
                removeTranscript ? "a conversation whose transcript went after the plan is not shared"
                    : "a conversation deleted after the plan is not shared, and the ledger keeps no intent for it");
        }
    }

    private static void TestServiceFillsWhileRunning()
    {
        using var fixture = new Fixture();
        // Accounts of their own: the widget's events and mailboxes are per account, and a running widget must not hear this test.
        var g = new ClaudeShelfAccount("90909090-0000-4000-8000-000000000009", "91919191-0000-4000-8000-000000000009", "G", "p-g");
        var h = new ClaudeShelfAccount("80808080-0000-4000-8000-000000000008", "81818181-0000-4000-8000-000000000008", "H", "p-h");
        var logs = Directory.CreateDirectory(Path.Combine(fixture.Root, "logs")).FullName;
        var main = Path.Combine(logs, "main.log");
        var start = new DateTime(2026, 10, 1, 2, 0, 0);
        var second = 0;
        string Line(string text) => start.AddSeconds(++second).ToString("yyyy-MM-dd HH:mm:ss") + " [info] " + text;
        string Init(ClaudeShelfAccount account) => Line(
            $"[LocalSessionManager] Initialization succeeded — accountId={account.AccountUuid}, orgId={account.OrgUuid}, existingSessions=0");
        string Transition(ClaudeShelfAccount? from, ClaudeShelfAccount? to) => Line(
            $"[account] Login-state transition (loggedOut: {(from is null ? "true" : "false")} → {(to is null ? "true" : "false")}, "
            + $"uuid: {from?.AccountUuid ?? "<none>"} → {to?.AccountUuid ?? "<none>"}), clearing oauth cache");
        var now = DateTimeOffset.Now;
        var contract = ClaudeDesktopContract.Reviewed;
        var package = new ClaudeDesktopPackage("Claude_9.9.9.9_x64__pzs8sxrjxfjjc", new Version(9, 9, 9, 9), fixture.Root, "Claude_pzs8sxrjxfjjc");
        var runtime = new ClaudeDesktopRuntime(ClaudeDesktopRunState.Running, start);
        var environment = new ClaudeShelfEnvironment(() => runtime, () => package, _ => contract, _ => { }, () => now);
        var sources = new ClaudeSessionSync.Sources(fixture.Sessions, Path.Combine(fixture.Root, "Local Storage", "leveldb"),
            Path.Combine(fixture.Root, "IndexedDB", "https_claude.ai_0.indexeddb.leveldb"), fixture.Projects, fixture.Ledger);
        using var owner = new System.Windows.Forms.Control();
        using var service = new ClaudeShelfService(owner, () => [], fixture.Shelf(), environment, new ClaudeDesktopLogWatcher(logs), sources);
        service.SetAccountsForTest([g, h]);
        // One pass, after the given time and with the given lines added to the app's log.
        ClaudeShelfStatus After(TimeSpan wait, params string[] lines)
        {
            File.AppendAllLines(main, lines);
            now += wait;
            service.Accept(service.Work([], service.ActiveReservation()));
            return service.Status;
        }
        var minute = TimeSpan.FromSeconds(60);
        var moment = TimeSpan.FromSeconds(3);

        fixture.Record(g, "local_9a9a9a9a-0000-4000-8000-000000000001", fixture.Transcript());
        fixture.Record(h, "local_9a9a9a9a-0000-4000-8000-000000000002", fixture.Transcript());
        var shown = After(TimeSpan.Zero, Init(g));
        Check(shown is { AppProfileId: "p-g", PendingForApp: 1 } && fixture.Files(h).Count == 1,
            "the app shows G: what G lacks is counted, and nothing is written before its log was watched two minutes");
        After(minute + TimeSpan.FromSeconds(1));
        Check(fixture.Files(h).Count == 1, "nor a minute later");
        var filled = After(minute);
        Check(fixture.Files(h).Count == 2 && fixture.Files(g).Count == 1 && filled.PendingForApp == 1 && filled.LastFillAt == now,
            "watched two minutes, the list the app leaves alone is filled while it runs; the one it shows waits for it to close");

        // A sign-out fills the lists the app leaves alone at once, before it reads the next account. The account it showed
        // is still its own until it shows another.
        fixture.Record(g, "local_9a9a9a9a-0000-4000-8000-000000000003", fixture.Transcript());
        var signedOut = After(moment, Transition(g, null));
        Check(signedOut.App.Status == ClaudeDesktopAccountStatus.SignedOut && fixture.Files(h).Count == 3 && fixture.Files(g).Count == 2,
            "a sign-out fills the other lists at once and leaves the one the app showed");
        fixture.Record(g, "local_9a9a9a9a-0000-4000-8000-000000000004", fixture.Transcript());
        var signingIn = After(moment, Transition(null, h));
        Check(!signingIn.App.Confirmed && fixture.Files(h).Count == 3, "the account the app signs in to is left alone from the first line naming it");
        var arrived = After(moment, Init(h));
        Check(arrived is { AppProfileId: "p-h", PendingForApp: 1 } && fixture.Files(g).Count == 3,
            "the account the app left is left alone: it ends that account's sessions some seconds after showing the next");
        After(minute + TimeSpan.FromSeconds(1));
        Check(fixture.Files(g).Count == 3, "still a minute later");
        After(minute);
        Check(fixture.Files(g).Count == 4, "two minutes after the app left it, that list is filled while the app runs");

        // An app that cannot be inspected: what it showed meanwhile is not known, so the two minutes start again.
        fixture.Record(h, "local_9a9a9a9a-0000-4000-8000-000000000005", fixture.Transcript());
        runtime = new ClaudeDesktopRuntime(ClaudeDesktopRunState.Unknown, null);
        After(moment);
        runtime = new ClaudeDesktopRuntime(ClaudeDesktopRunState.Running, start);
        After(moment);
        After(minute + TimeSpan.FromSeconds(1));
        Check(fixture.Files(g).Count == 4, "after an app it could not inspect, nothing is written until it was watched two minutes again");
        After(minute);
        Check(fixture.Files(g).Count == 5, "then the list is filled");

        // A record that does not parse may count for nothing with the app's delete protection: reported once it has
        // stayed two minutes, and no longer once the list reads again.
        var broken = Path.Combine(fixture.ShelfPath(g), "local_9a9a9a9a-0000-4000-8000-0000000000ff.json");
        File.WriteAllText(broken, "{\"sessionId\":\"local_9a9a");
        Check(After(minute + moment).Damaged is null && After(minute + moment).Damaged is null,
            "a record that does not parse may be one being written");
        Check(After(minute + moment).Damaged == "G", "one that stays two minutes is reported with its account");
        File.Delete(broken);
        Check(After(minute + moment) is { Damaged: null, Problem: ClaudeShelfProblem.None }, "and no longer once the list reads again");

        // A version not reviewed yet goes on filling, with the warning; an incompatible one fills nothing.
        contract = ClaudeDesktopContract.Unreviewed;
        fixture.Record(h, "local_9a9a9a9a-0000-4000-8000-000000000006", fixture.Transcript());
        var warned = After(minute);
        Check(warned is { Unreviewed: "9.9.9.9", Problem: ClaudeShelfProblem.None } && fixture.Files(g).Count == 6,
            "a version not reviewed yet goes on filling, and is reported for the warning");
        contract = ClaudeDesktopContract.Incompatible;
        fixture.Record(h, "local_9a9a9a9a-0000-4000-8000-000000000007", fixture.Transcript());
        var stopped = After(minute);
        Check(stopped is { Unreviewed: null, Problem: ClaudeShelfProblem.Incompatible } && fixture.Files(g).Count == 6,
            "an incompatible version fills nothing");

        // Lost log lines could hide a switch: the two minutes start again.
        contract = ClaudeDesktopContract.Reviewed;
        File.Delete(main);
        After(minute);
        File.WriteAllLines(main, [Init(h)]);
        After(minute + TimeSpan.FromSeconds(1));
        Check(fixture.Files(g).Count == 6, "after lost log lines, nothing is written until the log was watched two minutes again");
        After(minute);
        Check(fixture.Files(g).Count == 7, "then the list is filled");
    }

    private static void TestServiceStartedWhileSignedOut()
    {
        // A widget started while the app is signed out: the app still holds the lists of the account it signed out of until
        // it shows the next one, and the log names that account, so it is left alone however long the app stays signed out.
        using var fixture = new Fixture();
        var g = new ClaudeShelfAccount("90909090-0000-4000-8000-00000000000a", "91919191-0000-4000-8000-00000000000a", "G", "p-g2");
        var h = new ClaudeShelfAccount("80808080-0000-4000-8000-00000000000b", "81818181-0000-4000-8000-00000000000b", "H", "p-h2");
        var logs = Directory.CreateDirectory(Path.Combine(fixture.Root, "logs")).FullName;
        var main = Path.Combine(logs, "main.log");
        var start = new DateTime(2026, 10, 1, 3, 0, 0);
        var second = 0;
        string Line(string text) => start.AddSeconds(++second).ToString("yyyy-MM-dd HH:mm:ss") + " [info] " + text;
        string Init(ClaudeShelfAccount account) => Line(
            $"[LocalSessionManager] Initialization succeeded — accountId={account.AccountUuid}, orgId={account.OrgUuid}, existingSessions=0");
        string Transition(ClaudeShelfAccount? from, ClaudeShelfAccount? to) => Line(
            $"[account] Login-state transition (loggedOut: {(from is null ? "true" : "false")} → {(to is null ? "true" : "false")}, "
            + $"uuid: {from?.AccountUuid ?? "<none>"} → {to?.AccountUuid ?? "<none>"}), clearing oauth cache");
        var now = DateTimeOffset.Now;
        var package = new ClaudeDesktopPackage("Claude_9.9.9.9_x64__pzs8sxrjxfjjc", new Version(9, 9, 9, 9), fixture.Root, "Claude_pzs8sxrjxfjjc");
        var runtime = new ClaudeDesktopRuntime(ClaudeDesktopRunState.Running, start);
        var environment = new ClaudeShelfEnvironment(() => runtime, () => package, _ => ClaudeDesktopContract.Reviewed, _ => { }, () => now);
        var sources = new ClaudeSessionSync.Sources(fixture.Sessions, Path.Combine(fixture.Root, "Local Storage", "leveldb"),
            Path.Combine(fixture.Root, "IndexedDB", "https_claude.ai_0.indexeddb.leveldb"), fixture.Projects, fixture.Ledger);
        File.WriteAllLines(main, [Init(g), Transition(g, null)]);
        fixture.Record(g, "local_9b9b9b9b-0000-4000-8000-000000000001", fixture.Transcript());
        fixture.Record(h, "local_9b9b9b9b-0000-4000-8000-000000000002", fixture.Transcript());
        using var owner = new System.Windows.Forms.Control();
        using var service = new ClaudeShelfService(owner, () => [], fixture.Shelf(), environment, new ClaudeDesktopLogWatcher(logs), sources);
        service.SetAccountsForTest([g, h]);
        void After(TimeSpan wait, params string[] lines)
        {
            File.AppendAllLines(main, lines);
            now += wait;
            service.Accept(service.Work([], service.ActiveReservation()));
        }
        var minute = TimeSpan.FromSeconds(60);
        var moment = TimeSpan.FromSeconds(3);
        After(TimeSpan.Zero);
        After(minute);
        After(minute + TimeSpan.FromSeconds(1));
        After(minute * 5);
        Check(fixture.Files(h).Count == 2 && fixture.Files(g).Count == 1,
            "a widget started while the app is signed out fills the other lists, never the one the app signed out of");
        After(moment, Transition(null, h));
        After(moment, Init(h));
        After(minute + TimeSpan.FromSeconds(1));
        Check(fixture.Files(g).Count == 1, "nor in the two minutes after the app shows the next account");
        After(minute);
        Check(fixture.Files(g).Count == 2, "then that list is filled");
    }

    private static void TestWriterAndDesktopRaces()
    {
        using var fixture = new Fixture();
        var first = fixture.Transcript(); var second = fixture.Transcript();
        fixture.Record(A, "local_dddddddd-0000-4000-8000-000000000001", first);
        fixture.Record(A, "local_dddddddd-0000-4000-8000-000000000002", second);

        MustThrow<ClaudeDesktopRunningException>(() => fixture.Shelf().Apply([A, B], () => false));
        Check(fixture.Files(B).Count == 0, "nothing is written while Desktop runs");

        // A writer appears between the two plans: its session is filled all the same, the record pointing at the
        // transcript as it goes on.
        var calls = 0;
        var shelf = fixture.Shelf(() => new ClaudeLiveSessionScan(
            ++calls >= 2 ? new HashSet<string> { first } : new HashSet<string>(), false));
        var result = shelf.Apply([A, B], () => true);
        Check(result.Created == 2 && fixture.Files(B).Select(file => JsonNode.Parse(File.ReadAllText(file))!["cliSessionId"]!
                  .GetValue<string>()).Order().SequenceEqual(new[] { first, second }.Order()),
            "a session being written is filled like any other");
        Check(!result.Plan.HasTransientSkips && result.Plan.IsComplete, "and the run is complete");

        using var race = new Fixture();
        race.Record(A, "local_eeeeeeee-0000-4000-8000-000000000001", race.Transcript());
        race.Record(A, "local_eeeeeeee-0000-4000-8000-000000000002", race.Transcript());
        var checks = 0;
        var started = MustThrow<ClaudeDesktopRunningException>(() => race.Shelf().Apply([A, B], () => ++checks <= 3));
        Check(started.Created == 1 && race.Files(B).Count == 1, "Desktop starting mid-run stops the remaining files");
        var ledger = JsonNode.Parse(File.ReadAllText(Path.Combine(race.Ledger, "ledger.json")))!;
        Check(ledger["created"]!.AsArray().Count == 1 && ledger["created"]![0]!["pending"] is null,
            "only the published file stays in the ledger");

        using var changing = new Fixture();
        changing.Record(A, "local_ffffffff-0000-4000-8000-000000000001", changing.Transcript());
        var scans = 0;
        var moving = changing.Shelf(() =>
        {
            // Each plan scans the shelves before asking for writers: change the target after the first scan.
            if (++scans == 1) changing.Record(B, "local_ffffffff-0000-4000-8000-000000000009", changing.Transcript());
            return new ClaudeLiveSessionScan(new HashSet<string>(), false);
        });
        MustThrow<IOException>(() => moving.Apply([A, B], () => true));
        Check(changing.Files(B).Count == 1, "a shelf change during preparation writes nothing");

        var unknown = fixture.Shelf(() => new ClaudeLiveSessionScan(new HashSet<string>(), true)).Plan([A, B]);
        Check(unknown.Actions.Count == 0 && unknown.Held.Count == 2, "an unreadable writer registration holds every shelf");

        // Real registration format: a live process with a matching start time is a writer.
        var registry = Directory.CreateDirectory(Path.Combine(fixture.Root, "registry")).FullName;
        var self = Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(registry, self.Id + ".json"), JsonSerializer.Serialize(new
        { pid = self.Id, sessionId = first.ToUpperInvariant(), procStart = self.StartTime.ToFileTime() }));
        File.WriteAllText(Path.Combine(registry, "999999.json"), JsonSerializer.Serialize(new { pid = 999999, sessionId = second }));
        var scan = ClaudeLiveSessions.Read([registry]);
        Check(scan.Ids.Contains(first) && !scan.Ids.Contains(second) && !scan.Unknown,
            "a registered live process is a writer; an exited one is not");
        var own = Path.Combine(registry, self.Id + ".json");
        File.WriteAllText(own, "{");
        File.SetLastWriteTimeUtc(own, DateTime.UtcNow.AddDays(-30));
        Check(ClaudeLiveSessions.Read([registry]).Unknown, "an old malformed registration of a live process still makes writers unknown");
        File.WriteAllText(own, JsonSerializer.Serialize(new { pid = 999999, sessionId = first }));
        Check(ClaudeLiveSessions.Read([registry]).Unknown, "a registration whose pid disagrees with its name is malformed");
        File.Delete(own);
        File.WriteAllText(Path.Combine(registry, "999998.json"), "{");
        File.WriteAllText(Path.Combine(registry, "notes.json"), "{");
        Check(!ClaudeLiveSessions.Read([registry]).Unknown,
            "a malformed registration of an exited process, or a JSON file that is not a registration, is ignored");
    }

    private static void TestRecordFields()
    {
        var source = new JsonObject
        {
            ["sessionId"] = "local_x", ["cliSessionId"] = "11111111-2222-4333-8444-555555555555", ["cwd"] = @"C:\work",
            ["title"] = "제목", ["titleSource"] = "user", ["createdAt"] = 1, ["lastActivityAt"] = 2, ["isStarred"] = true,
            ["model"] = "claude-opus-5-5", ["effort"] = "max", ["priorCliSessionIds"] = new JsonArray("aaaa"),
            ["rewindEdges"] = new JsonArray(new JsonObject { ["to"] = "b" }), ["transcriptCuts"] = new JsonArray(),
            ["adoptedFromOtherSurface"] = true, ["enabledMcpTools"] = new JsonObject(), ["gitAnchors"] = new JsonObject()
        };
        var record = ClaudeSessionShelf.BuildRecord(source);
        Check(record["originCwd"]!.GetValue<string>() == @"C:\work" && record["title"]!.GetValue<string>() == "제목"
              && record["model"]!.GetValue<string>() == "claude-opus-5-5" && record["effort"]!.GetValue<string>() == "max"
              && record["isStarred"]!.GetValue<bool>() && record["isArchived"]!.GetValue<bool>() == false,
            "display, model and effort fields are carried to a new, unarchived record");
        Check(record["priorCliSessionIds"] is JsonArray && record["rewindEdges"] is JsonArray && record["transcriptCuts"] is null,
            "non-empty lineage is carried so every claimed transcript stays protected");
        Check(record["adoptedFromOtherSurface"]!.GetValue<bool>() && record["enabledMcpTools"] is null && record["gitAnchors"] is null,
            "the surface flag follows the source; connectors and trust anchors stay with the app");
        source["adoptedFromOtherSurface"] = false; source["effortInherited"] = true; source["model"] = "bad model!";
        record = ClaudeSessionShelf.BuildRecord(source);
        Check(record["adoptedFromOtherSurface"] is null && record["effort"] is null && record["model"] is null,
            "Code-tab sessions stay Code-tab sessions; inherited effort and invalid models fall back to defaults");
    }

    private static void TestUndoAndPendingRecovery()
    {
        using var fixture = new Fixture();
        fixture.Record(A, "local_12121212-0000-4000-8000-000000000001", fixture.Transcript());
        fixture.Record(A, "local_12121212-0000-4000-8000-000000000002", fixture.Transcript());
        var shelf = fixture.Shelf();
        Check(shelf.Apply([A, B], () => true).Created == 2, "fixture publishes two records");
        var changed = fixture.Files(B)[0];
        File.AppendAllText(changed, " ");
        var (removed, kept) = shelf.Undo(() => true);
        Check(removed == 1 && kept == 1 && File.Exists(changed), "undo removes only records the app never changed");

        // An interrupted run: a pending intent survives only with the exact published content.
        var ledgerPath = Path.Combine(fixture.Ledger, "ledger.json");
        var ledger = JsonNode.Parse(File.ReadAllText(ledgerPath))!.AsObject();
        var cli = fixture.Transcript();
        var published = Path.Combine(fixture.ShelfPath(B), "local_34343434-0000-4000-8000-000000000001.json");
        File.WriteAllText(published, "{\"sessionId\":\"local_34343434-0000-4000-8000-000000000001\"}");
        ledger["created"]!.AsArray().Add(new JsonObject { ["dst"] = B.Key, ["file"] = Path.GetFileName(published),
            ["sha256"] = ClaudeSessionShelf.Hash(published), ["cliSessionId"] = cli, ["pending"] = true });
        ledger["created"]!.AsArray().Add(new JsonObject { ["dst"] = B.Key, ["file"] = "local_34343434-0000-4000-8000-000000000002.json",
            ["sha256"] = "00", ["cliSessionId"] = Guid.NewGuid().ToString("D"), ["pending"] = true });
        File.WriteAllText(ledgerPath, ledger.ToJsonString());
        var loaded = ClaudeShelfLedger.Load(fixture.Ledger, fixture.Sessions);
        Check(loaded.Created.Any(entry => entry.CliSessionId == cli && !entry.Pending)
              && !loaded.Created.Any(entry => entry.File.EndsWith("0002.json", StringComparison.Ordinal)),
            "a published intent is confirmed and an unpublished one is dropped");
    }

    private static void TestAccountMapping()
    {
        using var fixture = new Fixture();
        var store = new ClaudeProfileStore(Path.Combine(fixture.Root, "profiles"), Directory.CreateDirectory(Path.Combine(fixture.Root, "home", ".claude")).FullName, defaultUsesHome: true);
        var key = ClaudeProfileRuntime.IdentityKey("main@example.test", OrgA);
        var main = store.RegisterDefault("Main", new ClaudeProfileIdentity(key, "m*** · x", "max"));
        var home = Path.Combine(fixture.Root, "home");
        void WriteConfig(string text) => File.WriteAllText(Path.Combine(home, ".claude.json"), text);
        static string Section(string account, string org, string email) =>
            "\"oauthAccount\":{\"accountUuid\":\"" + account + "\",\"organizationUuid\":\"" + org + "\",\"emailAddress\":\"" + email + "\"}";
        WriteConfig("{\"projects\":{\"a\":1,\"a\":2}," + Section(AccountA.ToUpperInvariant(), OrgA, " Main@Example.test ") + "}");
        var accounts = ClaudeShelfAccounts.Resolve(store.ListProfiles(), fixture.Ledger, home);
        Check(accounts.Single().Key == A.Key && accounts.Single().ProfileId == main.Id,
            "the verified identity maps to its account/organization pair despite unrelated duplicate keys");
        WriteConfig("{" + Section(AccountB, OrgB, "other@example.test") + "}");
        Check(ClaudeShelfAccounts.Resolve(store.ListProfiles(), fixture.Ledger, home).Single().Key == A.Key,
            "a changed terminal login keeps the mapping verified for this profile");
        Check(ClaudeShelfAccounts.ReadAccount(Path.Combine(home, ".claude.json"), key) is null,
            "another identity in the config is never accepted for this profile");
        WriteConfig("{" + Section(AccountA, OrgA, "main@example.test") + "," + Section(AccountB, OrgA, "main@example.test") + "}");
        Check(ClaudeShelfAccounts.ReadAccount(Path.Combine(home, ".claude.json"), key) is null,
            "conflicting account sections leave the mapping unresolved");
        // The main account moves off the default home: the official sign-in in its own profile writes the same
        // account there, and the moved row stays that account's target even if the default home is connected again.
        WriteConfig("{" + Section(AccountA, OrgA, "main@example.test") + "}");
        var target = store.NewDetachedProfile("Main");
        Directory.CreateDirectory(target.ConfigDirectory);
        File.WriteAllText(Path.Combine(target.ConfigDirectory, ".claude.json"),
            "{" + Section(AccountA, OrgA, "main@example.test") + "}");
        var moved = store.AdoptDefault(target, main.Identity!);
        var afterMove = ClaudeShelfAccounts.Resolve(store.ListProfiles(), fixture.Ledger, home);
        Check(afterMove.Single().Key == A.Key && afterMove.Single().ProfileId == moved.Id && afterMove.Single().Label == "Main",
            "the moved account keeps its account/organization pair through its own profile");
        var again = store.RegisterDefault("Terminal", main.Identity!);
        Check(store.ListProfiles()[0].Id == again.Id
              && ClaudeShelfAccounts.Resolve(store.ListProfiles(), fixture.Ledger, home).Single().ProfileId == moved.Id,
            "a default home connected again for the same account does not take the moved profile's place");
        store.Remove(again.Id);
        store.Remove(moved.Id);
        Check(ClaudeShelfAccounts.Resolve(store.ListProfiles(), fixture.Ledger, home).Count == 0
              && !File.ReadAllText(Path.Combine(fixture.Ledger, "accounts.json")).Contains(AccountA),
            "a removed profile never returns as a fill target");
        var text = File.ReadAllText(Path.Combine(fixture.Ledger, "accounts.json"));
        Check(!text.Contains("example.test", StringComparison.OrdinalIgnoreCase), "the mapping cache stores no email");
    }

    private static void TestLogObservation()
    {
        var start = new DateTime(2026, 10, 1, 0, 9, 32);
        string Line(int seconds, string text) => start.AddSeconds(seconds).ToString("yyyy-MM-dd HH:mm:ss") + " [info] " + text;
        var registered = new[] { A, B };
        var lines = new List<string>
        {
            Line(-3600, $"[LocalSessionManager] Initialization succeeded — accountId={AccountB}, orgId={OrgB}, existingSessions=0"),
            Line(2, $"[LocalSessionManager] Initialization succeeded — accountId={AccountA}, orgId={OrgA}, existingSessions=0"),
            Line(2, $"Loaded 95 persisted sessions from C:\\Users\\u\\AppData\\Roaming\\Claude\\claude-code-sessions\\{AccountA}\\{OrgA} (8 archived deferred)")
        };
        var state = ClaudeDesktopLogWatcher.Apply(ClaudeDesktopAccountState.Unknown, lines, start, registered);
        Check(state.Confirmed && state.AccountUuid == AccountA && state.LoadedSessions == 95,
            "a restart without a transition confirms the account initialized in this run only");
        lines.Add(Line(60, $"[account] Login-state transition (loggedOut: false → true, uuid: {AccountA} → <none>), clearing oauth cache"));
        lines.Add(Line(70, $"[LocalSessionManager] Initialization succeeded — accountId={AccountA}, orgId={OrgB}, existingSessions=0"));
        state = ClaudeDesktopLogWatcher.Apply(ClaudeDesktopAccountState.Unknown, lines, start, registered);
        Check(state.Status == ClaudeDesktopAccountStatus.SignedOut && !state.Confirmed,
            "sign-out and the transitional pair after it never keep the previous account");
        lines.Add(Line(80, $"[account] Login-state transition (loggedOut: true → false, uuid: <none> → {AccountB}), clearing oauth cache"));
        state = ClaudeDesktopLogWatcher.Apply(ClaudeDesktopAccountState.Unknown, lines, start, registered);
        Check(state.Status == ClaudeDesktopAccountStatus.SignedIn && !state.Confirmed && state.AccountUuid == AccountB,
            "a sign-in is not confirmed before the app initializes its organization");
        lines.Add(Line(80, $"[LocalSessionManager] Initialization succeeded — accountId={AccountB}, orgId={OrgB}, existingSessions=0"));
        state = ClaudeDesktopLogWatcher.Apply(ClaudeDesktopAccountState.Unknown, lines, start, registered);
        Check(state.Confirmed && state.Key == B.Key, "the new account is confirmed after its own initialization");
        lines.Add(Line(90, $"[account] Login-state transition (loggedOut: false → false, uuid: {AccountB} → 99999999-0000-4000-8000-000000000000), clearing oauth cache"));
        state = ClaudeDesktopLogWatcher.Apply(ClaudeDesktopAccountState.Unknown, lines, start, registered);
        Check(state.Status == ClaudeDesktopAccountStatus.Unregistered && !state.Confirmed, "an unregistered account is never shown as a registered one");
        var stale = ClaudeDesktopLogWatcher.Apply(ClaudeDesktopAccountState.Unknown, lines.Take(1), start, registered);
        Check(stale.Status == ClaudeDesktopAccountStatus.Unknown, "a previous run's initialization never describes the current run");
        // The accounts the app left stay held until it confirms another one.
        HashSet<string> Held(int count)
        {
            var held = new HashSet<string>();
            ClaudeDesktopLogWatcher.Apply(ClaudeDesktopAccountState.Unknown, lines.Take(count), start, registered, held);
            return held;
        }
        Check(Held(4).SetEquals([AccountA]) && Held(6).SetEquals([AccountA]) && Held(7).Count == 0 && Held(8).SetEquals([AccountB]),
            "the account signed out of is held through the next sign-in, until the app confirms an account");

        using var fixture = new Fixture();
        var logs = Directory.CreateDirectory(Path.Combine(fixture.Root, "logs")).FullName;
        File.WriteAllLines(Path.Combine(logs, "main1.log"), lines.Take(3));
        File.WriteAllLines(Path.Combine(logs, "main.log"), lines.Skip(3).Take(3));
        var watcher = new ClaudeDesktopLogWatcher(logs);
        Check(watcher.Update(start, registered).Status == ClaudeDesktopAccountStatus.SignedIn && watcher.Held.SequenceEqual([AccountA]),
            "a new run replays the rotated file and the current file in order, the account it left included");
        File.AppendAllLines(Path.Combine(logs, "main.log"), [lines[6]]);
        Check(watcher.Update(start, registered).Confirmed && watcher.Held.Count == 0, "later lines are read incrementally");
        File.AppendAllLines(Path.Combine(logs, "main.log"), [lines[7]]);
        Check(watcher.Update(start, registered).Status == ClaudeDesktopAccountStatus.Unregistered && watcher.Held.SequenceEqual([AccountB]),
            "an account left for one not registered is held");
        Check(watcher.Update(null, registered).Status == ClaudeDesktopAccountStatus.Closed && watcher.Held.Count == 0,
            "a closed app has no current account and holds none");
    }

    // Shapes of the reviewed 2.16120.0.0 bundle. Minified names change between builds; the flows must not.
    private const string CollectIds =
        "for(let n of[i.cliSessionId,i.unarchivedCliSessionId])typeof n==\"string\"&&(!a?.localOnly||t.WQ(n))&&e.add(n);for(let t of n.En(i.priorCliSessionIds)??[])e.add(t)}catch(n){}";
    // 2.26454.0.0: earlier ids are spread into one array with, for mirrors, remote earlier ids.
    private const string CollectIds26454 =
        "for(let n of[i.cliSessionId,i.unarchivedCliSessionId])typeof n==\"string\"&&(!a?.localOnly||t.m4(n))&&e.add(n);for(let t of[...n.hn(i.priorCliSessionIds)??[],...a?.mirrors&&n.hn(i.priorRemoteCliSessionIds)||[]])e.add(t)}catch(n){}";
    // 4.4212.0.0: the reap decision is a function given the manager's checks, with refusals around the test on disk.
    private const string Reap =
        "async function Q0(e,n,r,i,a,o){let s=i!==void 0,c=(e,t)=>X0(n,e,t,s);if(i!==void 0&&await J0(r,i))return c(\"written_since_idle_cutoff\",\"was written into after the cleanup's idle cutoff\");let l=await(await e.discovery()).liveOwnershipRefusal(n);if(l?.reason===t.G9.Running)return c(\"writer_running\",\"is in use by a Claude Code process\");if(l?.reason===t.G9.LivenessUnknown&&i===void 0)return c(\"liveness_unknown\",`could not be checked against running Claude Code processes (${l.detail})`);let u=await e.claimedOnDisk();return u===null?c(\"records_unreadable\",\"could not be checked against the session records on disk\"):u.has(n)?c(\"claimed_elsewhere\",\"is claimed by a session of another account or profile on this computer\"):!s&&t.Jk()&&await Y0(r,o+K0)?c(\"written_since_own_activity\",\"was written into, since this app last used it, by a process the app did not run\"):!e.claims().localIdsClaimedByOthers(a,{adopting:!0}).has(n)||c(\"claimed_elsewhere\",\"is claimed by another session\")}";
    // The folder walk of 4.29.0.0 up to the id loop, verbatim: every account, and only the current one told apart.
    private const string Walk =
        "for(let r of l){if(!r.isDirectory())continue;let l=(0,X.join)(c,r.name),u;try{u=await Z.default.promises.readdir(l,{withFileTypes:!0})}catch(e){if(a?.failClosed)throw e;s&&MW(e)&&(s.indexUnreadable=!0,t.Rst.warn(`${t.P9} session index account dir unreadable (${t._st(e)})`));continue}for(let c of u){if(!c.isDirectory())continue;let u=(0,X.join)(l,c.name),d=o===void 0&&r.name===i().accountId&&c.name===i().orgId,f;try{f=await Z.default.promises.readdir(u,{withFileTypes:!0})}catch(e){if(a?.failClosed)throw e;s&&MW(e)&&(s.indexUnreadable=!0,t.Rst.warn(`${t.P9} session index org dir unreadable (${t._st(e)})`));continue}for(let i of f){if(!i.isFile())continue;if(i.name.startsWith(\"deleted_\")){a?.recordsOnly||e.add(i.name.slice(_W.length)),s&&NW(s,await VW((0,X.join)(u,i.name),s),\"delete tombstone\");continue}if(s&&i.name===qW){for(let e of await HW((0,X.join)(u,i.name),s))NW(s,e,\"scheduled task\");continue}let o=a?.mirrors&&i.name.endsWith(\".json.tmp\")?\".json.tmp\":\".json\";if(!i.name.startsWith(\"local_\")||!i.name.endsWith(o)||d&&a?.skipOwnRecordsOf?.has(i.name.slice(0,-o.length)))continue;let l=i.name.slice(t.btt.length,-o.length);if(!d)e.add(l);else if(a?.foldedSessionStems?.has(l)){e.add(l);continue}let f=(0,X.join)(u,i.name),p=async(n,o,s)=>{let l=await UW(n,void 0,{failClosed:!0});e.add(o);for(let t of[...l.live,...l.lineage,...a?.mirrors?l.remoteLineage:[]])e.add(t);t.Rst.warn(`[CCD] session record ${r.name}/${c.name}/${i.name} ${s}; its ids kept as claims`)};try{let r=await Z.default.promises.stat(f);if(r.size>10485760){if(a?.failClosed){await p(f,l,`oversized (${r.size} bytes)`);continue}a?.localOnly||IW(await UW(f,s),e,d,s,l);continue}let i=JSON.parse(await Z.default.promises.readFile(f,\"utf-8\"));if(d&&e.add(l),(i.sshConfig||i.wslConfig)&&typeof i.lastAssistantUuid==\"string\"&&t.glt(i.lastAssistantUuid)&&a?.lastAnswers?.add(i.lastAssistantUuid),s&&FW(s,{...i,stem:l,priorCliSessionIds:n.wr(i.priorCliSessionIds)},\"session record\"),a?.localOnly&&(i.sshConfig||i.wslConfig||t.VE({violinBow:i.violinBow===!0})))continue;";
    private const string ReapChecks =
        "this.reapChecks={discovery:()=>this.getCliSessionDiscovery(),claimedOnDisk:()=>this.cliSessionIdsClaimedOnDisk(),claims:()=>this.transcriptIdClaims}";
    private const string Claims =
        "cliSessionIdsClaimedOnDisk(){let i={failClosed:!0,localOnly:!0,recordsOnly:!0,skipOwnRecordsOf:new Set},a=(async()=>{await Wj(e,{},(()=>this.getAccountIds()),i)})}";
    // 4.4212.0.0, verbatim: what a session delete releases and asks, and the removal.
    private const string Release =
        "function e2(e,t,n,r,i){let a=i!==void 0;return{mayRelease:async t=>!((await e.claimedOnDisk())?.has(t)??!1),reapIf:async(o,s)=>r!==void 0&&!await r?X0(o,\"own_process_running\",\"may still be written by this session's Claude Code process, whose exit could not be confirmed\",a):Q0(e,o,s,i,t,q0(n)),keptInDoubt:e=>Z0(e,a)}}";
    private const string Remove =
        "async function UG(e,n,r,i,a={}){let o=[],s=[],c;for(let l of new Set(n))!l||!lG(l)||o.push(dG(l,(async()=>{try{if(a.mayRelease&&!await a.mayRelease(l))return}catch{return}let{resolved:n,dirs:o,complete:u}=await vG(e,l,r,(()=>c??=gG())),d=[],f=!1;for(let e of o){let r=(0,X.join)(e,`${l}${sG}`);try{await t.Sft(r,{v:1,releasedAt:(new Date).toISOString(),reason:i}),d.push(e),f||=e===n}catch(e){t.Bdt.debug(\"desktop-released marker: write failed\",{cliSessionId:l,markerPath:r,err:e})}}if(n===null||!f||!a.reapIf)return;let p=!1;try{let e=await HG(_G(n,l)),t=(await Promise.all(d.filter((e=>e!==n)).map((e=>HG(_G(e,l)))))).filter((e=>e!==null));if(e!==null){let n=[e,...t],r=async e=>{let t=(await Promise.all(n.map(e))).filter((e=>e!==void 0));return t.length>0?Math.max(...t):void 0};p=await a.reapIf(l,{mtimeMs:Math.max(...n.map((e=>e.mtimeMs))),lastEntryMs:()=>r((e=>e.lastEntryMs())),lastConversationEntryMs:()=>r((e=>e.lastConversationEntryMs()))})}}catch(e){t.Bdt.debug(\"desktop-released marker: reap check failed\",{cliSessionId:l,err:e});try{a.keptInDoubt?.(l)}catch{}}if(!p)return;let m=u&&d.length===o.length;for(let e of d){let n=_G(e,l),r=!1,i=!1;for(let[a,o]of[[n,!1],[(0,X.join)(e,l),!0]])try{await t.gft((async()=>{if(await Z.default.promises.realpath(e).catch((()=>null))!==e)throw new VG;await Z.default.promises.rm(a,{force:!0,recursive:o})}),pL,o?[\"ENOTEMPTY\"]:[]),a===n&&(r=!0)}catch(e){if(i=e instanceof VG,t.Bdt.warn(i?\"desktop-released marker: project dir moved during the reap; nothing more removed there\":`desktop-released marker: transcript removal failed (${e.code??\"unknown\"}); left on disk for Claude Code's periodic cleanup`,{cliSessionId:l}),i)break}r&&!i&&(await yL(l,e),t.Bdt.info(\"desktop-released marker: transcript removed\",{cliSessionId:l})),m&&=r}m&&s.push(l)})));return await Promise.all(o),s}";
    // The session delete of 4.4212.0.0: the calls verbatim, what lies between them shortened.
    private const string Call =
        "let l=e2(this.reapChecks,e,g,D,a.reapTranscriptsIdleSince),u=this.transcriptIdClaims.localTranscriptIds(g),d=await(o?.owe(u,D,r&&!g.prewarmHidden&&O?.length===u.length?t2(this.reapChecks,l,e,{resolver:this.diskTranscript,ids:u,hints:XD(g),beforeSetAside:()=>this.clearOnDiskClaimsOlderThan(T)}):void 0));if(r){await Z.default.promises.rm(r,{force:!0}),s=!0}if(O&&!d){let t=this.transcriptIdClaims.localIdsClaimedByOthers(e);this.clearOnDiskClaimsOlderThan(T);let n=UG(this.diskTranscript,O.filter((e=>!t.has(e))),XD(g),\"delete\",l);o?.reaps(n)}";
    // 4.4212.0.0, verbatim: a delete owed to Remote Control sets transcripts aside, then finishes or puts them back.
    private const string Aside =
        "function t2(e,t,n,r){let i=t=>e.claims().localIdsClaimedByOthers(n,{adopting:!0}).has(t);return{setAside:(e,n)=>(r.beforeSetAside(),IG(r.resolver,r.ids,r.hints,{...t,nonce:e,notWrittenSince:n,mayRelease:async e=>!i(e)&&await t.mayRelease(e)})),finish:async(n,a)=>{let o=await RG(n,a,(async n=>i(n)||await e.claimedOnDisk()===null||!await t.mayRelease(n)));return await UG(r.resolver,r.ids.filter((e=>!i(e))),r.hints,\"delete\",{mayRelease:t.mayRelease}),o}}}";
    private const string SetAside =
        "async function IG(e,t,n,r){if(!xG.test(r.nonce))return null;let i,a=!1,o=[],s=[];if(await Promise.all([...new Set(t)].map((t=>dG(t,(async()=>{let c=[];try{if(!lG(t)||!await r.mayRelease(t)){a=!0;return}let{dirs:s,complete:l}=await vG(e,t,n,(()=>i??=gG())),u=await Promise.all(s.map((async e=>({dir:e,times:await HG(_G(e,t)),absent:await PG(_G(e,t))})))),d=u.flatMap((({dir:e,times:t})=>t?[{dir:e,times:t}]:[]));if(!l||u.some((e=>e.times===null&&!e.absent))){a=!0;return}if(d.length===0){a=!0;return}let f=async e=>{let t=(await Promise.all(d.map((({times:t})=>e(t))))).filter((e=>e!==void 0));return t.length>0?Math.max(...t):void 0},p=Math.max(...d.map((({times:e})=>e.mtimeMs)));if(p>r.notWrittenSince||!await r.reapIf(t,{mtimeMs:p,lastEntryMs:()=>f((e=>e.lastEntryMs())),lastConversationEntryMs:()=>f((e=>e.lastConversationEntryMs()))}).catch((()=>(r.keptInDoubt?.(t),!1)))){a=!0;return}for(let{dir:e}of d){let n=await FG(t,e,r.nonce);if(n===null||n===\"stuck\"){a=!0,n===\"stuck\"&&o.push(t);return}c.push(n)}}catch{a=!0}finally{s.push(...c)}}))))),!a)return s;if(!await LG(s)||o.length>0){let e=await Promise.all(s.map((async e=>await EG(e.asidePath)?e.id:null))),n=[...o,...e].find((e=>e!==null))??t[0];throw Error(`Could not finish deleting session ${n}: a transcript set aside during the delete could not be restored. It is still in its project folder (under ~/.claude/projects, or projects in CLAUDE_CONFIG_DIR when that is set) as ${n}.jsonl.desktop-deleted-${r.nonce}; renaming it to ${n}.jsonl restores it.`)}return null}";
    private const string Finish =
        "async function RG(e,n,r){let i=!1,a=new Map;for(let t of e)a.set(t.id,[...a.get(t.id)??[],t]);return await Promise.all([...a].map((([e,a])=>dG(e,(async()=>{let o=await r(e).catch((()=>!0)),s=[];for(let r of a){let a=_G(r.dir,e),c=await EG(r.asidePath);if(!(!o&&c!==null&&TG(c,r)&&c.nlink===1n&&Number(c.mtimeMs)<=n&&await Z.default.promises.realpath(r.dir).catch((()=>null))===r.dir&&await PG(a))){i=!0,await DG(r.asidePath,a,r);continue}await jG(r.dir,e)&&await t.gft((async()=>{if(!await MG(r.dir))throw NG();await Z.default.promises.rm(r.asidePath,{force:!0})}),pL).then((()=>!0),(()=>!1))?s.push(r.dir):i=!0}if(s.length===a.length&&await AG(e))for(let t of s)await OG(e,t)}))))),{kept:i}}";

    // 5.11492.5.0, verbatim: the main process is split into chunks, and the transcript removal, setting aside and
    // finishing live in a chunk of their own that the session manager's chunk requires as `Gt`. The walk can be asked
    // for the current organization only; the removal first waits for the checks' `reapAfter`.
    private const string Walk5 =
        "for(let n of c){if(!n.isDirectory()||i?.currentOrgOnly&&n.name!==r().accountId)continue;let c=(0,Z.join)(s,n.name),l;try{l=await Q.default.promises.readdir(c,{withFileTypes:!0})}catch(e){if(i?.failClosed)throw e;o&&hB(e)&&(o.indexUnreadable=!0,t.iDt.warn(`${t.r_t} session index account dir unreadable (${t.xEt(e)})`));continue}for(let s of l){if(!s.isDirectory())continue;let l=(0,Z.join)(c,s.name),u=a===void 0&&n.name===r().accountId&&s.name===r().orgId;if(i?.currentOrgOnly&&!u)continue;let d;try{d=await Q.default.promises.readdir(l,{withFileTypes:!0})}catch(e){if(i?.failClosed)throw e;o&&hB(e)&&(o.indexUnreadable=!0,t.iDt.warn(`${t.r_t} session index org dir unreadable (${t.xEt(e)})`));continue}for(let r of d){if(!r.isFile())continue;if(r.name.startsWith(\"deleted_\")){i?.recordsOnly||e.add(r.name.slice(ln.r.length)),o&&gB(o,await TB((0,Z.join)(l,r.name),o),\"delete tombstone\");continue}if(o&&r.name===jB){for(let e of await EB((0,Z.join)(l,r.name),o))gB(o,e,\"scheduled task\");continue}let a=i?.mirrors&&r.name.endsWith(\".json.tmp\")?\".json.tmp\":\".json\";if(!r.name.startsWith(\"local_\")||!r.name.endsWith(a)||u&&i?.skipOwnRecordsOf?.has(r.name.slice(0,-a.length)))continue;let c=r.name.slice(t.dyt.length,-a.length);if(!u)e.add(c);else if(i?.foldedSessionStems?.has(c)){e.add(c);continue}let d=(0,Z.join)(l,r.name),f=async(a,o,c)=>{let l=await DB(a,void 0,{failClosed:!0});e.add(o);for(let t of[...l.live,...l.lineage,...i?.mirrors?l.remoteLineage:[]])e.add(t);t.iDt.warn(`[CCD] session record ${n.name}/${s.name}/${r.name} ${c}; its ids kept as claims`)};try{let n=await Q.default.promises.stat(d);if(n.size>10485760){if(i?.failClosed){await f(d,c,`oversized (${n.size} bytes)`);continue}i?.localOnly||bB(await DB(d,o),e,u,o,c);continue}let r=JSON.parse(await Q.default.promises.readFile(d,\"utf-8\"));if(u&&e.add(c),(r.sshConfig||r.wslConfig)&&typeof r.lastAssistantUuid==\"string\"&&t.JOt(r.lastAssistantUuid)&&i?.lastAnswers?.add(r.lastAssistantUuid),o&&vB(o,{...r,stem:c,priorCliSessionIds:Fe.i(r.priorCliSessionIds)},\"session record\"),i?.localOnly&&(r.sshConfig||r.wslConfig||t.LP({violinBow:r.violinBow===!0})))continue;";
    private const string CollectIds5 =
        "for(let n of[r.cliSessionId,r.unarchivedCliSessionId])typeof n==\"string\"&&(!i?.localOnly||t.JOt(n))&&e.add(n);for(let t of[...Fe.i(r.priorCliSessionIds)??[],...Fe.i(r.overflowCliSessionIds,5e3)??[],...i?.mirrors&&[...Fe.i(r.priorRemoteCliSessionIds,1900)??[],...Fe.i(r.overflowRemoteCliSessionIds,5e3)??[]]||[]])e.add(t)}catch(a){}";
    private const string Reap5 =
        "async function BX(e,n,r,i,a,o){let s=i!==void 0,c=(e,t)=>RX(n,e,t,s);if(i!==void 0&&await IX(r,i))return c(\"written_since_idle_cutoff\",\"was written into after the cleanup's idle cutoff\");let l=await(await e.discovery()).liveOwnershipRefusal(n);if(l?.reason===t.Hft.Running)return c(\"writer_running\",\"is in use by a Claude Code process\");if(l?.reason===t.Hft.LivenessUnknown&&i===void 0)return c(\"liveness_unknown\",`could not be checked against running Claude Code processes (${l.detail})`);let u=await e.claimedOnDisk();return u===null?c(\"records_unreadable\",\"could not be checked against the session records on disk\"):u.has(n)?c(\"claimed_elsewhere\",\"is claimed by a session of another account or profile on this computer\"):!s&&t.KF()&&await LX(r,o+PX)?c(\"written_since_own_activity\",\"was written into, since this app last used it, by a process the app did not run\"):!e.claims().localIdsClaimedByOthers(a,{adopting:!0}).has(n)||c(\"claimed_elsewhere\",\"is claimed by another session\")}";
    private const string Release5 =
        "function HX(e,t,n,r,i){let a=i!==void 0;return{mayRelease:async t=>!((await e.claimedOnDisk())?.has(t)??!1),reapAfter:r,reapIf:async(o,s)=>r!==void 0&&!await r?RX(o,\"own_process_running\",\"may still be written by this session's Claude Code process, whose exit could not be confirmed\",a):BX(e,o,s,i,t,FX(n,o)),keptInDoubt:e=>zX(e,a)}}";
    private const string Aside5 =
        "function UX(e,t,n,r){let i=t=>e.claims().localIdsClaimedByOthers(n,{adopting:!0}).has(t);return{setAside:(e,n)=>(r.beforeSetAside(),Gt.d(r.resolver,r.ids,r.hints,{...t,nonce:e,notWrittenSince:n,mayRelease:async e=>!i(e)&&await t.mayRelease(e)})),finish:async(a,o)=>{let s=await Gt.i(a,o,(async n=>i(n)||await e.claimedOnDisk()===null||!await t.mayRelease(n))),c=e.claims().localIdsClaimedByOthers(n,{adopting:!0});return await Gt.f(r.resolver,r.ids.filter((e=>!c.has(e))),r.hints,\"delete\",{mayRelease:t.mayRelease}),s}}}";
    private const string Claims5 =
        "cliSessionIdsClaimedOnDisk(){let n=Date.now(),r=`${this.currentAccountId??\"\"}/${this.currentOrgId??\"\"}`;if(this.onDiskClaims&&this.onDiskClaims.scope===r&&n-this.onDiskClaims.at<e.ON_DISK_CLAIMS_TTL_MS)return this.onDiskClaims.ids;let i={failClosed:!0,localOnly:!0,recordsOnly:!0,skipOwnRecordsOf:new Set([...this.sessions.keys(),...this.deletingSessionIds.keys()])},a=(async()=>{let e=new Set;try{await NB(e,{userDataPath:this.userDataPath,baseDir:this.baseDir},(()=>this.getAccountIds()),i);for(let t of await OB({userDataPath:this.userDataPath,baseDir:this.baseDir},{refuseUnc:!0}))await NB(e,{userDataPath:this.userDataPath,baseDir:this.baseDir},(()=>this.getAccountIds()),{...i,foreignRootDir:t});return e}catch(e){return t.iDt.warn(`[CCD] session records on disk unreadable (${t.xEt(e)}); transcripts left on disk for Claude Code's periodic cleanup`),null}})();return this.onDiskClaims={at:n,scope:r,ids:a},a}";
    private const string Call5 =
        "let c=HX(this.reapChecks,e,g,D,r.reapTranscriptsIdleSince),l=this.transcriptIdClaims.localTranscriptIds(g),u=await(a?.owe(l,D,i&&!g.prewarmHidden&&O?.length===l.length?UX(this.reapChecks,c,e,{resolver:this.diskTranscript,ids:l,hints:Gt.x(g),beforeSetAside:()=>this.clearOnDiskClaimsOlderThan(E)}):void 0));if(i){await Q.default.promises.rm(`${i}.tmp`,{force:!0}),await Q.default.promises.rm(i,{force:!0}),s=!0,this.archivedIdsHint.forget((0,Z.dirname)(i),e);let n=this.getScratchScope();if(g.backend.kind===\"local\"&&!g.prewarmHidden&&n!==null){let e=t.rD(g.cwd),r=t.tD(n.accountId,n.orgId);e!==null&&e.startsWith(r+Z.sep)&&!this.listSessionCwds().some((n=>t.rD(n)===e))&&Gt.removeScratchWorkspace(e).catch((e=>t.iDt.warn(`[CCD] scratch workspace removal on delete failed: ${e.code??\"unknown\"}`)))}if(g.stagedTranscriptPath&&t.iOt(g.stagedTranscriptPath,(0,Z.join)((0,Z.dirname)(i),\"imported-staging\"))&&await Q.default.promises.rm(g.stagedTranscriptPath,{force:!0}).catch((e=>{t.iDt.warn(`[CCD] staged import transcript could not be removed on delete (${t.xEt(e)})`)})),g.prewarmHidden){o=!0;return}let r=this.transcriptIdClaims.cliSessionIdsClaimedByLoadedSessions({localOnly:!0,except:e}),a=[...new Set([e.startsWith(\"local_\")?e.slice(t.dyt.length):e,...this.transcriptIdClaims.localTranscriptIds(g)].filter((e=>!!e&&!r.has(e))))];await ln.c((0,Z.dirname)(i),a,Date.now()),_.n()}if(O&&!u){let t=this.transcriptIdClaims.localIdsClaimedByOthers(e);this.clearOnDiskClaimsOlderThan(E);let n=Gt.f(this.diskTranscript,O.filter((e=>!t.has(e))),Gt.x(g),\"delete\",c);a?.reaps(n)}";
    private const string Remove5 =
        "async function Ze(e,n,r,i,a={}){let o=[],s=[],c,l;for(let u of new Set(n))!u||!P(u)||o.push(L(u,async n=>{try{if(a.mayRelease&&!await a.mayRelease(u))return}catch{return}let{resolved:o,dirs:p,complete:m}=await V(e,u,r,()=>c??=z()),h=[],g=!1;for(let e of p){let n=(0,d.join)(e,`${u}${N}`);try{await t.WDt(n,{v:1,releasedAt:new Date().toISOString(),reason:i}),h.push(e),g||=e===o}catch(e){t.iDt.debug(\"desktop-released marker: write failed\",{cliSessionId:u,markerPath:n,err:e})}}if(o===null||!g||!a.reapIf)return;await n(a.reapAfter);let _=!1;try{let e=await Q(B(o,u)),t=(await Promise.all(h.filter(e=>e!==o).map(e=>Q(B(e,u))))).filter(e=>e!==null);if(e!==null){let n=[e,...t],r=async e=>{let t=(await Promise.all(n.map(e))).filter(e=>e!==void 0);return t.length>0?Math.max(...t):void 0};_=await a.reapIf(u,{mtimeMs:Math.max(...n.map(e=>e.mtimeMs)),lastEntryMs:()=>r(e=>e.lastEntryMs()),lastConversationEntryMs:()=>r(e=>e.lastConversationEntryMs())})}}catch(e){t.iDt.debug(\"desktop-released marker: reap check failed\",{cliSessionId:u,err:e});try{a.keptInDoubt?.(u)}catch{}}if(!_)return;let v=m&&h.length===p.length;for(let e of h){let n=B(e,u),r=!1,i=!1;for(let[a,o]of[[n,!1],[(0,d.join)(e,u),!0]])try{await t.RDt(async()=>{if(await f.default.promises.realpath(e).catch(()=>null)!==e)throw new Xe;await f.default.promises.rm(a,{force:!0,recursive:o})},O,o?[\"ENOTEMPTY\"]:[]),a===n&&(r=!0)}catch(e){if(i=e instanceof Xe,t.iDt.warn(i?\"desktop-released marker: project dir moved during the reap; nothing more removed there\":`desktop-released marker: transcript removal failed (${e.code??\"unknown\"}); left on disk for Claude Code's periodic cleanup`,{cliSessionId:u}),i)break}r&&!i&&(await Ce(u,e),t.iDt.info(\"desktop-released marker: transcript removed\",{cliSessionId:u})),v&&=r}v&&(s.push(u),await Be(u,()=>l??=j()))}));return await Promise.all(o),s}";
    private const string SetAside5 =
        "async function We(e,t,n,r){if(!U.test(r.nonce))return null;let i,a=!1,o=[],s=[];if(await Promise.all([...new Set(t)].map(t=>L(t,async()=>{let c=[];try{if(!P(t)||!await r.mayRelease(t)){a=!0;return}let{dirs:s,complete:l}=await V(e,t,n,()=>i??=z()),u=await Promise.all(s.map(async e=>({dir:e,times:await Q(B(e,t)),absent:await Z(B(e,t))}))),d=u.flatMap(({dir:e,times:t})=>t?[{dir:e,times:t}]:[]);if(!l||u.some(e=>e.times===null&&!e.absent)){a=!0;return}if(d.length===0){a=!0;return}let f=async e=>{let t=(await Promise.all(d.map(({times:t})=>e(t)))).filter(e=>e!==void 0);return t.length>0?Math.max(...t):void 0},p=Math.max(...d.map(({times:e})=>e.mtimeMs));if(p>r.notWrittenSince||!await r.reapIf(t,{mtimeMs:p,lastEntryMs:()=>f(e=>e.lastEntryMs()),lastConversationEntryMs:()=>f(e=>e.lastConversationEntryMs())}).catch(()=>(r.keptInDoubt?.(t),!1))){a=!0;return}for(let{dir:e}of d){let n=await Ue(t,e,r.nonce);if(n===null||n===\"stuck\"){a=!0,n===\"stuck\"&&o.push(t);return}c.push(n)}}catch{a=!0}finally{s.push(...c)}}))),!a)return s;if(!await Ge(s)||o.length>0){let e=await Promise.all(s.map(async e=>await K(e.asidePath)?e.id:null)),n=[...o,...e].find(e=>e!==null)??t[0];throw Error(`Could not finish deleting session ${n}: a transcript set aside during the delete could not be restored. It is still in its project folder (under ~/.claude/projects, or projects in CLAUDE_CONFIG_DIR when that is set) as ${n}.jsonl.desktop-deleted-${r.nonce}; renaming it to ${n}.jsonl restores it.`)}return null}";
    private const string Finish5 =
        "async function Ke(e,n,r){let i=!1,a,o=new Map;for(let t of e)o.set(t.id,[...o.get(t.id)??[],t]);return await Promise.all([...o].map(([e,o])=>L(e,async()=>{let s=await r(e).catch(()=>!0),c=[];for(let r of o){let a=B(r.dir,e),o=await K(r.asidePath);if(!(!s&&o!==null&&G(o,r)&&o.nlink===1n&&Number(o.mtimeMs)<=n&&await f.default.promises.realpath(r.dir).catch(()=>null)===r.dir&&await Z(a))){i=!0,await q(r.asidePath,a,r);continue}await He(r.dir,e)&&await t.RDt(async()=>{if(!await Y(r.dir))throw X();await f.default.promises.rm(r.asidePath,{force:!0})},O).then(()=>!0,()=>!1)?c.push(r.dir):i=!0}if(c.length===o.length&&await Ve(e)){for(let t of c)await Re(e,t);await Be(e,()=>a??=j())}}))),{kept:i}}";
    // The bundled files around them: each ends with its source map comment, the manager's requires the chunk in its
    // header and the chunk exports its functions at its end.
    private const string Header5 = "const e=require(\"./index.chunk-B2zFLoEJ.js\"),Gt=require(\"./index.chunk-Q2iYtP6L.js\"),Kt=require(\"./index.chunk-BNMstLw_.js\");";
    private const string Exports5 = "exports.c=Ge,exports.d=We,exports.f=Ze,exports.i=Ke,exports.u=tt;";
    private const string Other5 = "async function Qe(e,t,n){return null}";
    // 6.28899.6.0, verbatim: the removal is split into a first pass that writes markers (`it`), a second pass that
    // reaps (`at`) and the exported function that joins them (`ot`, exports.f); the session delete names a block-local
    // list like its checks. The other parts are the 5.11492 shapes under new minified names.
    private const string Walk6 =
        "for(let n of c){if(!n.isDirectory()||i?.currentOrgOnly&&n.name!==r().accountId)continue;let c=(0,Z.join)(s,n.name),l;try{l=await Q.default.promises.readdir(c,{withFileTypes:!0})}catch(e){if(i?.failClosed)throw e;o&&Ic(e)&&(o.indexUnreadable=!0,t.lWt.warn(`${t.$Mt} session index account dir unreadable (${t.LUt(e)})`));continue}for(let s of l){if(!s.isDirectory())continue;let l=(0,Z.join)(c,s.name),u=a===void 0&&n.name===r().accountId&&s.name===r().orgId;if(i?.currentOrgOnly&&!u)continue;let d;try{d=await Q.default.promises.readdir(l,{withFileTypes:!0})}catch(e){if(i?.failClosed)throw e;o&&Ic(e)&&(o.indexUnreadable=!0,t.lWt.warn(`${t.$Mt} session index org dir unreadable (${t.LUt(e)})`));continue}for(let r of d){if(!r.isFile())continue;if(r.name.startsWith(\"deleted_\")){i?.recordsOnly||e.add(r.name.slice(gn.r.length)),o&&Lc(o,await Kc((0,Z.join)(l,r.name),o),\"delete tombstone\");continue}if(o&&r.name===nl){for(let e of await qc((0,Z.join)(l,r.name),o))Lc(o,e,\"scheduled task\");continue}let a=i?.mirrors&&r.name.endsWith(\".json.tmp\")?\".json.tmp\":\".json\";if(!r.name.startsWith(\"local_\")||!r.name.endsWith(a)||u&&i?.skipOwnRecordsOf?.has(r.name.slice(0,-a.length)))continue;let c=r.name.slice(t.TFt.length,-a.length);if(!u)e.add(c);else if(i?.foldedSessionStems?.has(c)){e.add(c);continue}let d=(0,Z.join)(l,r.name),f=async(a,o,c)=>{let l=await Jc(a,void 0,{failClosed:!0});e.add(o);for(let t of[...l.live,...l.lineage,...i?.mirrors?l.remoteLineage:[]])e.add(t);t.lWt.warn(`[CCD] session record ${n.name}/${s.name}/${r.name} ${c}; its ids kept as claims`)};try{let n=await Q.default.promises.stat(d);if(n.size>10485760){if(i?.failClosed){await f(d,c,`oversized (${n.size} bytes)`);continue}i?.localOnly||Vc(await Jc(d,o),e,u,o,c);continue}let r=JSON.parse(await Q.default.promises.readFile(d,\"utf-8\"));if(u&&e.add(c),(r.sshConfig||r.wslConfig)&&typeof r.lastAssistantUuid==\"string\"&&t.XWt(r.lastAssistantUuid)&&i?.lastAnswers?.add(r.lastAssistantUuid),o&&zc(o,{...r,stem:c,priorCliSessionIds:We.i(r.priorCliSessionIds)},\"session record\"),i?.localOnly&&(r.sshConfig||r.wslConfig||t.LY({violinBow:r.violinBow===!0})))continue;";
    private const string CollectIds6 =
        "for(let n of[r.cliSessionId,r.unarchivedCliSessionId])typeof n==\"string\"&&(!i?.localOnly||t.XWt(n))&&e.add(n);for(let t of[...We.i(r.priorCliSessionIds)??[],...We.i(r.overflowCliSessionIds,5e3)??[],...i?.mirrors&&[...We.i(r.priorRemoteCliSessionIds,1900)??[],...We.i(r.overflowRemoteCliSessionIds,5e3)??[]]||[]])e.add(t)}catch(a){}";
    private const string Reap6 =
        "async function qie(e,n,r,i,a,o){let s=i!==void 0,c=(e,t)=>R5(n,e,t,s);if(i!==void 0&&await Wie(r,i))return c(\"written_since_idle_cutoff\",\"was written into after the cleanup's idle cutoff\");let l=await(await e.discovery()).liveOwnershipRefusal(n);if(l?.reason===t.YIt.Running)return c(\"writer_running\",\"is in use by a Claude Code process\");if(l?.reason===t.YIt.LivenessUnknown&&i===void 0)return c(\"liveness_unknown\",`could not be checked against running Claude Code processes (${l.detail})`);let u=await e.claimedOnDisk();return u===null?c(\"records_unreadable\",\"could not be checked against the session records on disk\"):u.has(n)?c(\"claimed_elsewhere\",\"is claimed by a session of another account or profile on this computer\"):!s&&t.hZ()&&await Gie(r,o+L5)?c(\"written_since_own_activity\",\"was written into, since this app last used it, by a process the app did not run\"):!e.claims().localIdsClaimedByOthers(a,{adopting:!0}).has(n)||c(\"claimed_elsewhere\",\"is claimed by another session\")}";
    private const string Claims6 =
        "cliSessionIdsClaimedOnDisk(){let n=Date.now(),r=`${this.currentAccountId??\"\"}/${this.currentOrgId??\"\"}`;if(this.onDiskClaims&&this.onDiskClaims.scope===r&&n-this.onDiskClaims.at<e.ON_DISK_CLAIMS_TTL_MS)return this.onDiskClaims.ids;let i={failClosed:!0,localOnly:!0,recordsOnly:!0,skipOwnRecordsOf:new Set([...this.sessions.keys(),...this.deletingSessionIds.keys()])},a=(async()=>{let e=new Set;try{await il(e,{userDataPath:this.userDataPath,baseDir:this.baseDir},(()=>this.getAccountIds()),i);for(let t of await Yc({userDataPath:this.userDataPath,baseDir:this.baseDir},{refuseUnc:!0}))await il(e,{userDataPath:this.userDataPath,baseDir:this.baseDir},(()=>this.getAccountIds()),{...i,foreignRootDir:t});return e}catch(e){return t.lWt.warn(`[CCD] session records on disk unreadable (${t.LUt(e)}); transcripts left on disk for Claude Code's periodic cleanup`),null}})();return this.onDiskClaims={at:n,scope:r,ids:a},a}";
    private const string Release6 =
        "function Jie(e,t,n,r,i){let a=i!==void 0;return{mayRelease:async t=>!((await e.claimedOnDisk())?.has(t)??!1),reapAfter:r,reapIf:async(o,s)=>r!==void 0&&!await r?R5(o,\"own_process_running\",\"may still be written by this session's Claude Code process, whose exit could not be confirmed\",a):qie(e,o,s,i,t,Uie(n,o)),keptInDoubt:e=>Kie(e,a)}}";
    private const string Call6 =
        "let i=Jie(this.reapChecks,e,m,k,r.reapTranscriptsIdleSince),l=this.transcriptIdClaims.localTranscriptIds(m),u=await(a?.owe(l,k,o&&!m.prewarmHidden&&A?.length===l.length?Yie(this.reapChecks,i,e,{resolver:this.diskTranscript,ids:l,hints:mn.C(m),beforeSetAside:()=>this.clearOnDiskClaimsOlderThan(D)}):void 0));if(o){await Q.default.promises.rm(`${o}.tmp`,{force:!0}),await Q.default.promises.rm(o,{force:!0}),c=!0,this.archivedIdsHint.forget((0,Z.dirname)(o),e);let n=this.getScratchScope();if(m.backend.kind===\"local\"&&!$c(m.cwd,this,this.changeCwdInFlight)&&!m.prewarmHidden&&n!==null){let e=t.N1(m.cwd),r=t.j1(n.accountId,n.orgId);e!==null&&e.startsWith(r+Z.sep)&&!this.listSessionCwds().some((n=>t.N1(n)===e))&&mn.removeScratchWorkspace(e).catch((e=>t.lWt.warn(`[CCD] scratch workspace removal on delete failed: ${e.code??\"unknown\"}`)))}if(m.stagedTranscriptPath&&t.sUt(m.stagedTranscriptPath,(0,Z.join)((0,Z.dirname)(o),\"imported-staging\"))&&await Q.default.promises.rm(m.stagedTranscriptPath,{force:!0}).catch((e=>{t.lWt.warn(`[CCD] staged import transcript could not be removed on delete (${t.LUt(e)})`)})),m.prewarmHidden){s=!0;return}let r=this.transcriptIdClaims.cliSessionIdsClaimedByLoadedSessions({localOnly:!0,except:e}),i=[...new Set([e.startsWith(\"local_\")?e.slice(t.TFt.length):e,...this.transcriptIdClaims.localTranscriptIds(m)].filter((e=>!!e&&!r.has(e))))];await gn.c((0,Z.dirname)(o),i,Date.now()),b.n()}if(A&&!u){let t=this.transcriptIdClaims.localIdsClaimedByOthers(e);this.clearOnDiskClaimsOlderThan(D);let n=mn.f(this.diskTranscript,A.filter((e=>!t.has(e))),mn.C(m),\"delete\",i);a?.reaps(n)}";
    private const string Aside6 =
        "function Yie(e,t,n,r){let i=t=>e.claims().localIdsClaimedByOthers(n,{adopting:!0}).has(t);return{setAside:(e,n)=>(r.beforeSetAside(),mn.d(r.resolver,r.ids,r.hints,{...t,nonce:e,notWrittenSince:n,mayRelease:async e=>!i(e)&&await t.mayRelease(e)})),finish:async(a,o)=>{let s=await mn.i(a,o,(async n=>i(n)||await e.claimedOnDisk()===null||!await t.mayRelease(n))),c=e.claims().localIdsClaimedByOthers(n,{adopting:!0});return await mn.f(r.resolver,r.ids.filter((e=>!c.has(e))),r.hints,\"delete\",{mayRelease:t.mayRelease}),s}}}";
    private const string Remove6 =
        "async function it(e,n,r,i,a={}){let o=[],s=[],c;for(let l of new Set(n)){if(!l||!I(l))continue;let n={cliSessionId:l,declined:!1,resolved:null,dirs:[],marked:[],complete:!1};s.push(n),o.push(z(l,async()=>{try{if(a.mayRelease&&!await a.mayRelease(l)){n.declined=!0;return}}catch{return}let{resolved:o,dirs:s,complete:u}=await U(e,l,r,()=>c??=V());Object.assign(n,{resolved:o,dirs:s,complete:u});for(let e of s){let r=Be(e,l);try{await t.JHt(r,{v:1,releasedAt:new Date().toISOString(),reason:i}),n.marked.push(e)}catch(e){t.lWt.debug(\"desktop-released marker: write failed\",{cliSessionId:l,markerPath:r,err:e})}}}))}return await Promise.all(o),s}async function at(e,n){let r=[],i=[],a;for(let{cliSessionId:o,resolved:s,dirs:c,marked:l,complete:u}of e)r.push(z(o,async e=>{if(!I(o)||s===null||!l.includes(s)||!n.reapIf)return;await e(n.reapAfter);let r=!1;try{let e=await $(H(s,o)),t=(await Promise.all(l.filter(e=>e!==s).map(e=>$(H(e,o))))).filter(e=>e!==null);if(e!==null){let i=[e,...t],a=async e=>{let t=(await Promise.all(i.map(e))).filter(e=>e!==void 0);return t.length>0?Math.max(...t):void 0};r=await n.reapIf(o,{mtimeMs:Math.max(...i.map(e=>e.mtimeMs)),lastEntryMs:()=>a(e=>e.lastEntryMs()),lastConversationEntryMs:()=>a(e=>e.lastConversationEntryMs())})}}catch(e){t.lWt.debug(\"desktop-released marker: reap check failed\",{cliSessionId:o,err:e});try{n.keptInDoubt?.(o)}catch{}}if(!r)return;let d=u&&l.length===c.length;for(let e of l){let n=H(e,o),r=!1,i=!1;for(let[a,s]of[[n,!1],[(0,f.join)(e,o),!0]])try{await t.VHt(async()=>{if(await p.default.promises.realpath(e).catch(()=>null)!==e)throw new rt;await p.default.promises.rm(a,{force:!0,recursive:s})},A,s?[\"ENOTEMPTY\"]:[]),a===n&&(r=!0)}catch(e){if(i=e instanceof rt,t.lWt.warn(i?\"desktop-released marker: project dir moved during the reap; nothing more removed there\":`desktop-released marker: transcript removal failed (${e.code??\"unknown\"}); left on disk for Claude Code's periodic cleanup`,{cliSessionId:o}),i)break}r&&!i&&(await Oe(o,e),t.lWt.info(\"desktop-released marker: transcript removed\",{cliSessionId:o})),d&&=r}d&&(i.push(o),await qe(o,()=>a??=N()))}));return await Promise.all(r),i}async function ot(e,t,n,r,i={}){return at(await it(e,t,n,r,i),i)}";
    private const string SetAside6 =
        "async function Ze(e,t,n,r){if(!W.test(r.nonce))return null;let i,a=!1,o=[],s=[];if(await Promise.all([...new Set(t)].map(t=>z(t,async()=>{let c=[];try{if(!I(t)||!await r.mayRelease(t)){a=!0;return}let{dirs:s,complete:l}=await U(e,t,n,()=>i??=V()),u=await Promise.all(s.map(async e=>({dir:e,times:await $(H(e,t)),absent:await Q(H(e,t))}))),d=u.flatMap(({dir:e,times:t})=>t?[{dir:e,times:t}]:[]);if(!l||u.some(e=>e.times===null&&!e.absent)){a=!0;return}if(d.length===0){a=!0;return}let f=async e=>{let t=(await Promise.all(d.map(({times:t})=>e(t)))).filter(e=>e!==void 0);return t.length>0?Math.max(...t):void 0},p=Math.max(...d.map(({times:e})=>e.mtimeMs));if(p>r.notWrittenSince||!await r.reapIf(t,{mtimeMs:p,lastEntryMs:()=>f(e=>e.lastEntryMs()),lastConversationEntryMs:()=>f(e=>e.lastConversationEntryMs())}).catch(()=>(r.keptInDoubt?.(t),!1))){a=!0;return}for(let{dir:e}of d){let n=await Xe(t,e,r.nonce);if(n===null||n===\"stuck\"){a=!0,n===\"stuck\"&&o.push(t);return}c.push(n)}}catch{a=!0}finally{s.push(...c)}}))),!a)return s;if(!await Qe(s)||o.length>0){let e=await Promise.all(s.map(async e=>await q(e.asidePath)?e.id:null)),n=[...o,...e].find(e=>e!==null)??t[0];throw Error(`Could not finish deleting session ${n}: a transcript set aside during the delete could not be restored. It is still in its project folder (under ~/.claude/projects, or projects in CLAUDE_CONFIG_DIR when that is set) as ${n}.jsonl.desktop-deleted-${r.nonce}; renaming it to ${n}.jsonl restores it.`)}return null}";
    private const string Finish6 =
        "async function $e(e,n,r){let i=!1,a,o=new Map;for(let t of e)o.set(t.id,[...o.get(t.id)??[],t]);return await Promise.all([...o].map(([e,o])=>z(e,async()=>{let s=await r(e).catch(()=>!0),c=[];for(let r of o){let a=H(r.dir,e),o=await q(r.asidePath);if(!(!s&&o!==null&&K(o,r)&&o.nlink===1n&&Number(o.mtimeMs)<=n&&await p.default.promises.realpath(r.dir).catch(()=>null)===r.dir&&await Q(a))){i=!0,await J(r.asidePath,a,r);continue}await Ye(r.dir,e)&&await t.VHt(async()=>{if(!await X(r.dir))throw Z();await p.default.promises.rm(r.asidePath,{force:!0})},A).then(()=>!0,()=>!1)?c.push(r.dir):i=!0}if(c.length===o.length&&await Je(e)){for(let t of c)await Ge(e,t);await qe(e,()=>a??=N())}}))),{kept:i}}";
    private const string Header6 = "const t=require(\"./index.chunk-CCkEi--u.js\"),mn=require(\"./index.chunk-cpsM356y.js\"),gn=require(\"./index.chunk-TEO440xf.js\");";
    private const string Exports6 = "exports.d=Ze,exports.f=ot,exports.i=$e,exports.u=ut;";
    private const string Other6 = "async function Qe(e,t,n){return null}";

    private static byte[] Bundle(string collect = CollectIds, string reap = Reap, string release = Release, string remove = Remove, string walk = Walk,
        string call = Call, string aside = Aside, string setAside = SetAside, string finish = Finish) =>
        Encoding.UTF8.GetBytes(string.Join("\n", ClaudeDesktopProbe.ContractMarkers.Select(spellings => spellings[0])) + "\n" + walk + collect
            + "\n" + reap + "\n" + ReapChecks + "\n" + Claims + "\n" + release + "\n" + call + "\n" + remove + "\n" + aside + "\n" + setAside + "\n" + finish);

    private static byte[] Bundle5(string header = Header5, string walk = Walk5, string collect = CollectIds5, string reap = Reap5,
        string claims = Claims5, string release = Release5, string call = Call5, string aside = Aside5, string remove = Remove5,
        string setAside = SetAside5, string finish = Finish5, string exports = Exports5) =>
        Encoding.UTF8.GetBytes(string.Join("\n", ClaudeDesktopProbe.ContractMarkers.Select(spellings => spellings[0])) + "\n" + header
            + "\n" + walk + collect + "\n" + reap + "\n" + ReapChecks + "\n" + claims + "\n" + release + "\n" + call + "\n" + aside
            + "\n//# sourceMappingURL=index.chunk-CjUF9KtU.js.map\"use strict\";" + Other5 + remove + "\n" + setAside + "\n" + finish
            + "\n" + exports + "\n//# sourceMappingURL=index.chunk-Q2iYtP6L.js.map\n\"use strict\";exports.f=Qe;\n//# sourceMappingURL=index.chunk-BNMstLw_.js.map");

    private static byte[] Bundle6(string header = Header6, string walk = Walk6, string collect = CollectIds6, string reap = Reap6,
        string claims = Claims6, string release = Release6, string call = Call6, string aside = Aside6, string setAside = SetAside6,
        string finish = Finish6, string remove = Remove6, string exports = Exports6) =>
        Encoding.UTF8.GetBytes(string.Join("\n", ClaudeDesktopProbe.ContractMarkers.Select(spellings => spellings[0])) + "\n" + header
            + "\n" + walk + collect + "\n" + reap + "\n" + ReapChecks + "\n" + claims + "\n" + release + "\n" + call + "\n" + aside
            + "\n//# sourceMappingURL=index.chunk-DvDp99eh.js.map\"use strict\";" + setAside + "\n" + finish + "\n" + Other6 + remove
            + "\n" + exports + "\n//# sourceMappingURL=index.chunk-cpsM356y.js.map\n\"use strict\";exports.f=Qe;\n//# sourceMappingURL=index.chunk-TEO440xf.js.map");

    private static void TestContractGuard()
    {
        Check(ClaudeDesktopProbe.IsCompatible(Bundle()), "the reviewed deletion protection is compatible");
        // Three answers: the stores and log lines the widget follows decide whether it may fill at all; the deletion
        // protection read differently only warns, and filling goes on.
        Check(ClaudeDesktopProbe.Contract(Bundle()) == ClaudeDesktopContract.Reviewed, "the reviewed shapes are reviewed");
        Check(ClaudeDesktopProbe.Contract(Bundle(reap: Reap.Replace("u.has(n)?", "u.has(r)?"))) == ClaudeDesktopContract.Unreviewed
              && ClaudeDesktopProbe.Contract(Bundle(walk: Walk.Replace("if(!r.isDirectory())continue;",
                  "if(!r.isDirectory()||r.name!==i().accountId)continue;"))) == ClaudeDesktopContract.Unreviewed,
            "a deletion protection that reads differently is unreviewed: filling goes on with a warning");
        var withoutMarker = ClaudeDesktopProbe.ContractMarkers[1].Aggregate(Encoding.UTF8.GetString(Bundle()),
            (bundle, spelling) => bundle.Replace(spelling, ""));
        Check(ClaudeDesktopProbe.Contract(Encoding.UTF8.GetBytes(withoutMarker)) == ClaudeDesktopContract.Incompatible
              && ClaudeDesktopProbe.Contract(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Bundle(reap: Reap.Replace("u.has(n)?", "u.has(r)?")))
                  .Replace(ClaudeDesktopProbe.ContractMarkers[0][0], ""))) == ClaudeDesktopContract.Incompatible,
            "a store or log line the widget follows that is gone stops filling, whatever the deletion protection reads");
        Check(ClaudeDesktopProbe.Contract(Path.Combine(Path.GetTempPath(), "gfs-agent", "no-such-" + Guid.NewGuid().ToString("N"), "app.asar"))
              == ClaudeDesktopContract.Incompatible, "an app package that cannot be read stops filling");
        Check(ClaudeDesktopProbe.IsCompatible(Bundle(collect: CollectIds.Replace("let n ", "let q ").Replace("typeof n", "typeof q")
                .Replace("WQ(n)", "WQ(q)").Replace("e.add(n)", "z.add(q)").Replace("e.add(t)", "z.add(t)"))),
            "renamed minified variables stay compatible");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(collect: CollectIds.Replace("&&e.add(n);", "&&e.add(o);"))),
            "claiming something other than the record's current transcript id is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(collect: CollectIds.Replace("e.add(t)}", "void t}"))),
            "no longer claiming earlier transcript ids is not reviewed");
        Check(ClaudeDesktopProbe.IsCompatible(Bundle(collect: CollectIds26454)), "the reviewed 2.26454 shape is compatible");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(collect: CollectIds26454.Replace("])e.add(t)}", "])void t}"))),
            "the 2.26454 shape that stops claiming earlier ids is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(collect: CollectIds26454.Replace("n.hn(i.priorCliSessionIds)??[],", ""))),
            "claiming only remote earlier ids is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(collect: CollectIds26454.Replace("])e.add(t)}", "])o.add(t)}"))),
            "earlier ids added to another set are not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(reap: Reap.Replace("u.has(n)?", "u.has(r)?"))),
            "a reap check that no longer tests the transcript being deleted is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(reap: Reap.Replace("\"records_unreadable\"", "\"records_ignored\""))),
            "a deletion path that no longer fails closed is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(release: Release.Replace("?.has(t)", "?.has(n)"))),
            "a release decision that stops consulting the claims is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(release: Release.Replace("await e.claimedOnDisk()", "await g.claimedOnDisk()"))),
            "a release decision that reads its claims from elsewhere is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(release: Release.Replace(":Q0(e,o,s,i,t,q0(n))", ":!0"))),
            "a session delete that no longer asks the reap decision is not reviewed");
        Check(ClaudeDesktopProbe.IsCompatible(Bundle(release: Release[..Release.IndexOf("reapIf:", StringComparison.Ordinal)]
                + "reapIf:async(o,s)=>Q0(e,o,s)}}")),
            "a simpler delete call that still asks the reap decision stays compatible");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(remove: Remove.Replace("if(!p)return;", ""))),
            "removing transcripts regardless of the reap decision is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(remove: Remove.Replace("catch(e){t.Bdt.debug(\"desktop-released marker: reap", "catch(e){p=!0;t.Bdt.debug(\"desktop-released marker: reap"))),
            "a failed reap check that removes anyway is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(remove: Remove.Replace("err:e});try{a.keptInDoubt", "err:e});p=!0;try{a.keptInDoubt"))),
            "setting the decision after logging a failed check is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(remove: Remove.Replace("lastConversationEntryMs()))})}}catch", "lastConversationEntryMs()))});p=!0}}catch"))),
            "overriding the reap decision after it returns is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(remove: Remove.Replace("let p=!1;try{", "let p=!1;try{p=!0;"))),
            "setting the decision before the reap check is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(release: Release.Replace("q0(n)),keptInDoubt", "q0(n))||!0,keptInDoubt"))),
            "changing the reap check's result is not reviewed");
        var keep = Release[Release.IndexOf("?X0(", StringComparison.Ordinal)..(Release.IndexOf("):Q0(e,o,s", StringComparison.Ordinal) + 2)];
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(release: Release.Replace(keep, "?!0:"))),
            "a guard branch that allows deletion without the reap check is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(reap: Reap.Replace("async function Q0(e,n,r,i,a,o){",
            "async function Q0(e,n,r,i,a,o){" + new string(' ', 4000)))), "protection detached from its function is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(reap: Reap.Replace("let u=await e.claimedOnDisk();", "return!0;let u=await e.claimedOnDisk();"))),
            "allowing deletion before the test on disk is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(reap: Reap.Replace("await e.claimedOnDisk()", "await o.claimedOnDisk()"))),
            "a decision that reads its claims from elsewhere is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(release: Release.Replace(":Q0(e,", ":g7(e,"))),
            "a session delete that asks another function than the reviewed decision is not reviewed");
        // The session delete hands the checks it made to the removal.
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(call: Call.Replace("=e2(this.reapChecks,", "=e2(this.otherChecks,"))),
            "checks made from anything but the manager's are not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(call: Call.Replace("=e2(this.reapChecks,", "=g7(this.reapChecks,"))),
            "a session delete that makes its checks with another function is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(call: Call.Replace("\"delete\",l)", "\"delete\",{})"))),
            "a removal not given those checks is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(call: Call.Replace("let t=this.transcriptIdClaims", "l={};let t=this.transcriptIdClaims"))),
            "replacing those checks before the removal is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(call: Call.Replace("=UG(this.diskTranscript,", "=g7(this.diskTranscript,"))),
            "removing through another function than the reviewed removal is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(call: Call.Replace("t2(this.reapChecks,l,", "t2(this.reapChecks,{},"))),
            "an owed delete not given those checks is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(call: Call.Replace("t2(this.reapChecks,l,", "g7(this.reapChecks,l,"))),
            "owing a delete through another function than the reviewed one is not reviewed");
        // A delete owed to Remote Control sets transcripts aside under those checks and finishes under a second check.
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(aside: Aside.Replace("{...t,nonce", "{nonce"))),
            "setting aside without the session delete's reap check is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(aside: Aside.Replace("&&await t.mayRelease(e)", ""))),
            "setting aside without the release test on disk is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(aside: Aside.Replace("async e=>!i(e)&&", "async e=>i(e)&&"))),
            "setting aside what another session holds in memory is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(aside: Aside.Replace("(r.beforeSetAside(),IG(", "(r.beforeSetAside(),g7("))),
            "setting aside through another function than the reviewed one is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(aside: Aside.Replace("await e.claimedOnDisk()===null||", ""))),
            "finishing that no longer keeps on unreadable records is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(aside: Aside.Replace("!await t.mayRelease(n)", "await t.mayRelease(n)"))),
            "finishing that keeps what is free and removes what is claimed is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(aside: Aside.Replace("=await RG(n,a,", "=await g7(n,a,"))),
            "finishing through another function than the reviewed one is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(setAside: SetAside.Replace("!await r.mayRelease(t)){a=!0;return}", "!await r.mayRelease(t)){return}"))),
            "a refused release that does not keep the rest is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(setAside: SetAside.Replace("!await r.reapIf(t,", "!await r.mayRelease(t,"))),
            "setting aside without the reap decision is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(setAside: SetAside.Replace(",!1)))){a=!0;return}", ",!0)))){a=!0;return}"))),
            "a failed reap check that sets aside anyway is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(setAside: SetAside.Replace("p>r.notWrittenSince||", ""))),
            "setting aside what was written since the delete began is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(setAside: SetAside.Replace("let{dirs:s,complete:l}=", "await FG(t,n,r.nonce);let{dirs:s,complete:l}="))),
            "moving a transcript before the reap decision is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(setAside: SetAside.Replace("{a=!0;return}for(let{dir:e}", "{a=!0;return}a=!1;for(let{dir:e}"))),
            "clearing a refusal is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(setAside: SetAside.Replace("}catch{a=!0}finally", "}catch{}finally"))),
            "a failure that does not keep the rest is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(setAside: SetAside.Replace(",!a)return s;", ",!0)return s;"))),
            "going on after a refusal is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(finish: Finish.Replace(".catch((()=>!0)),s=[]", ".catch((()=>!1)),s=[]"))),
            "a failed finishing check that removes anyway is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(finish: Finish.Replace("if(!(!o&&", "if(!(o&&"))),
            "removing when the finishing check keeps is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(finish: Finish.Replace("if(!(!o&&", "if(!("))),
            "removing without the finishing check is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(finish: Finish.Replace("i=!0,await DG(r.asidePath,a,r);continue}", "i=!0;continue}"))),
            "not putting a kept transcript back is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(finish: Finish.Replace("await DG(r.asidePath,a,r);continue}", "await DG(r.asidePath,a,r)}"))),
            "going on to remove a kept transcript is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Bundle())
                .Replace("claimedOnDisk:()=>this.cliSessionIdsClaimedOnDisk()", "claimedOnDisk:async()=>new Set"))),
            "checks that no longer reach the records on disk are not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(walk: Walk.Replace("continue}for(let i of f){", "continue}if(!d)continue;for(let i of f){"))),
            "a walk narrowed to the current account's folder is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(walk: Walk.Replace("if(!r.isDirectory())continue;", "if(!r.isDirectory()||r.name!==i().accountId)continue;"))),
            "a walk that leaves other accounts out is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(walk: Walk.Replace("||d&&a?.skipOwnRecordsOf", "||a?.skipOwnRecordsOf"))),
            "skipping records in every account's folder is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(walk: Walk.Replace("if(!d)e.add(l);else if(a?.foldedSessionStems?.has(l)){e.add(l);continue}",
                "if(!d)e.add(l);else if(a?.foldedSessionStems?.has(l)){e.add(l);continue}if(!d)continue;"))),
            "reading ids only from the current account's records is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(walk: Walk.Replace("if(!d)e.add(l);else if(a?.foldedSessionStems?.has(l)){e.add(l);continue}",
                "if(!d)e.add(l);else if(a?.foldedSessionStems?.has(l)){e.add(l);continue}if(!d&&a?.localOnly)continue;"))),
            "reading local ids only from the current account's records is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(walk: Walk.Replace("if(!d)e.add(l);else if(a?.foldedSessionStems?.has(l)){e.add(l);continue}",
                "if(!d)e.add(l);else if(a?.foldedSessionStems?.has(l)){e.add(l);continue}if(g.name!==h().accountId)continue;"))),
            "telling accounts apart a new way on the way to the ids is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(walk: Walk.Replace("if(a?.failClosed)throw e;s&&MW(e)&&(s.indexUnreadable=!0,t.Rst.warn(`${t.P9} session index org dir", "s&&MW(e)&&(s.indexUnreadable=!0,t.Rst.warn(`${t.P9} session index org dir"))),
            "an account folder that cannot be listed no longer failing closed is not reviewed");
        Check(ClaudeDesktopProbe.IsCompatible(Bundle(remove: Remove.Replace("if(!p)return;let m=u&&d.length===o.length;for(", "if(p)for("))),
            "a removal that loops only when the check allowed stays compatible");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(remove: Remove.Replace("if(!p)return;", "if(p)return;"))),
            "returning when the check allows is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(remove: Remove.Replace("if(!p)return;", "if(!0)return;"))),
            "ignoring the check's result is not reviewed");

        // 5.11492: the same protection, split across bundled files.
        Check(ClaudeDesktopProbe.IsCompatible(Bundle5()), "the reviewed 5.11492 shape is compatible");
        Check(ClaudeDesktopProbe.IsCompatible(Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(Bundle5())
                .Replace("Gt=require", "Hq=require").Replace("Gt.", "Hq.").Replace("function Ze(", "function Zq(").Replace("exports.f=Ze", "exports.f=Zq"))),
            "a renamed import and a renamed exported function stay compatible");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle5(call: Call5.Replace("=Gt.f(this.diskTranscript,", "=Kt.f(this.diskTranscript,"))),
            "removing through the same export name of another file is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle5(call: Call5.Replace("=Gt.f(this.diskTranscript,", "=Wt.f(this.diskTranscript,"))),
            "removing through an import the file does not require is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle5(exports: Exports5.Replace("exports.f=Ze", "exports.f=Qe"))),
            "an export that names another function than the reviewed removal is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle5(exports: Exports5.Replace("exports.f=Ze,", ""))),
            "a removal the chunk does not export is not reviewed");
        // The reviewed function may stay in place while the name that is called comes to mean another.
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle5(exports: Exports5 + "exports.f=Qe;"))
              && !ClaudeDesktopProbe.IsCompatible(Bundle5(exports: Exports5.Replace("exports.f=Ze,", "exports.f=Ze,exports.f=async(e,n)=>{},")))
              && !ClaudeDesktopProbe.IsCompatible(Bundle5(exports: "exports.f=void 0;" + Exports5)),
            "an export set a second time, after or before the reviewed function, is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle5(exports: Exports5 + "Object.defineProperty(exports,\"f\",{get:function(){return Qe}});"))
              && !ClaudeDesktopProbe.IsCompatible(Bundle5(exports: Exports5 + "module.exports={f:Qe};")),
            "nor one redefined as a property or replaced with the exports whole");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle5(header: Header5 + "Gt.f=Kt.f;"))
              && !ClaudeDesktopProbe.IsCompatible(Bundle5(call: "Gt.d = Kt.f;" + Call5)),
            "an import the calling file assigns to is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle5(header: Header5 + "var Gt=require(\"./index.chunk-BNMstLw_.js\");")),
            "nor a name the file binds to two required files");
        Check(ClaudeDesktopProbe.IsCompatible(Bundle5(header: Header5 + "if(Gt.f==null)throw Error();let xGt={};xGt.f=1;e.Gt.f=2;")),
            "a comparison, a longer name and another object's member are no assignment to the import");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle5(aside: Aside5.Replace("Gt.d(r.resolver", "Gt.c(r.resolver"))),
            "setting aside through another export than the reviewed one is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle5(aside: Aside5.Replace("await Gt.f(r.resolver", "await Kt.f(r.resolver"))),
            "finishing that removes through another function than the session delete is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle5(aside: Aside5.Replace("\"delete\",{mayRelease:t.mayRelease}", "\"delete\",{...t}"))),
            "a removal after finishing given the reap check is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle5(remove: Remove5.Replace("await n(a.reapAfter);", "await n(a.reapIf(u));"))),
            "waiting on anything but the checks' own process exit is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle5(remove: Remove5.Replace("await n(a.reapAfter);let _=!1;", "await n(a.reapAfter);let _=!0;"))),
            "a decision that starts allowed is not reviewed");
        var keep5 = Release5[Release5.IndexOf("?RX(", StringComparison.Ordinal)..(Release5.IndexOf("):BX(e,o,s", StringComparison.Ordinal) + 2)];
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle5(release: Release5.Replace(keep5, "?!0:"))),
            "a guard branch that allows deletion while the session's process may still write is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle5(release: Release5.Replace("FX(n,o)),keptInDoubt", "FX(n,o))||!0,keptInDoubt"))),
            "changing the 5.11492 reap check's result is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle5(claims: Claims5.Replace("recordsOnly:!0,skipOwnRecordsOf", "recordsOnly:!0,currentOrgOnly:!0,skipOwnRecordsOf"))),
            "claims narrowed to the current organization are not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle5(claims: Claims5.Replace("{...i,foreignRootDir:t}", "{...i,foreignRootDir:t,currentOrgOnly:!0}")))
              && ClaudeDesktopProbe.Unmatched(Bundle5(claims: Claims5.Replace("),i);", "),{...i,currentOrgOnly:!0});"))).Any(failure => failure.StartsWith("flow 9 ", StringComparison.Ordinal)),
            "claims that narrow the walk anywhere are not reviewed, and the probe names the flow");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle5(walk: Walk5.Replace("||i?.currentOrgOnly&&n.name!==r().accountId)", "||n.name!==r().accountId)"))),
            "a 5.11492 walk that leaves other accounts out is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle5(walk: Walk5.Replace("if(i?.currentOrgOnly&&!u)continue;", "if(!u)continue;"))),
            "a 5.11492 walk narrowed to the current organization is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle5(walk: Walk5.Replace("if(i?.currentOrgOnly&&!u)continue;", "if(g?.currentOrgOnly&&!u)continue;"))),
            "narrowing the walk on an option the claims do not set is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle5(setAside: SetAside5.Replace(".catch(()=>(r.keptInDoubt?.(t),!1))", ".catch(()=>(r.keptInDoubt?.(t),!0))"))),
            "a failed 5.11492 reap check that sets aside anyway is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle5(finish: Finish5.Replace(".catch(()=>!0),c=[]", ".catch(()=>!1),c=[]"))),
            "a failed 5.11492 finishing check that removes anyway is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle5(collect: CollectIds5.Replace("])e.add(t)}", "])void t}"))),
            "the 5.11492 shape that stops claiming earlier ids is not reviewed");
        // 6.28899: the removal is two passes joined by the function the chunk exports, and the session delete gives a
        // block-local list the checks' name.
        Check(ClaudeDesktopProbe.Contract(Bundle6()) == ClaudeDesktopContract.Reviewed, "the reviewed 6.28899 shape is reviewed");
        Check(ClaudeDesktopProbe.IsCompatible(Bundle6(call: Call6.Replace(",i=[...new Set([", ",q=[...new Set([")
                .Replace("(0,Z.dirname)(o),i,Date.now())", "(0,Z.dirname)(o),q,Date.now())"))),
            "the tombstones' list under another name stays compatible");
        var inside = Call6.Replace("b.n()}if(A&&!u){", "b.n();if(A&&!u){").Replace("a?.reaps(n)}", "a?.reaps(n)}}");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle6(call: inside))
              && ClaudeDesktopProbe.Unmatched(Bundle6(call: inside)).Any(failure => failure.StartsWith("flow 3 ", StringComparison.Ordinal)),
            "a removal inside the block whose list has the checks' name is not reviewed, and the probe names the flow");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle6(call: Call6.Replace("cliSessionIdsClaimedByLoadedSessions({localOnly:!0,except:e}),i=[",
                "cliSessionIdsClaimedByLoadedSessions({localOnly:!0,except:e});i=["))),
            "the delete's list assigned to the checks' name instead of declared is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle6(call: Call6.Replace("let t=this.transcriptIdClaims.localIdsClaimedByOthers(e);",
                "i={};let t=this.transcriptIdClaims.localIdsClaimedByOthers(e);"))),
            "the 6.28899 checks replaced before the removal are not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle6(call: Call6.Replace(",\"delete\",i)", ",\"delete\",{})"))),
            "a 6.28899 removal not given the checks is not reviewed");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle6(call: Call6.Replace("=mn.f(this.diskTranscript,", "=gn.f(this.diskTranscript,"))),
            "removing through the same export name of another required file is not reviewed");
        // The second pass removes only what the reap check allows, about the transcript it marked.
        var regardless = Remove6.Replace("if(!r)return;", "");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle6(remove: regardless))
              && ClaudeDesktopProbe.Unmatched(Bundle6(remove: regardless)).Any(failure => failure.StartsWith("flow 4 ", StringComparison.Ordinal)),
            "removing regardless of the reap check is not reviewed, and the probe names the flow");
        foreach (var (from, to, what) in new[]
        {
            ("if(!r)return;", "if(r)return;", "returning when the check allows"),
            ("let r=!1;try{", "let r=!0;try{", "a decision that starts allowed"),
            ("catch(e){t.lWt.debug(\"desktop-released marker: reap", "catch(e){r=!0;t.lWt.debug(\"desktop-released marker: reap",
                "a failed reap check that removes anyway"),
            ("lastConversationEntryMs())})}}catch", "lastConversationEntryMs())});r=!0}}catch", "overriding the reap decision after it returns"),
            ("let r=!1;try{", "let r=!1;try{r=!0;", "setting the decision before the reap check"),
            ("let r=!1;try{let e=", "let r=!1;try{let o=l[0],e=", "asking the reap check about another id"),
            ("let r=!1;try{let e=", "let r=!1;try{let n={reapIf:async()=>!0},e=", "asking another object's reap check"),
            ("await e(n.reapAfter);", "await e(n.reapIf(o));", "waiting on anything but the checks' own process exit"),
            ("||!n.reapIf)return;", ")return;", "reaping without the guard on a missing reap check"),
            ("||!l.includes(s)", "", "reaping a transcript no marker was written for"),
            ("||s===null", "", "reaping a transcript the first pass did not resolve"),
            // The joining function hands both passes the same checks.
            ("return at(await it(e,t,n,r,i),i)}", "return at(await it(e,t,n,r,i),{...i,reapIf:async()=>!0})}",
                "the joining function handing the second pass other checks"),
            ("await it(e,t,n,r,i),i)", "await it(e,t,n,r),i)", "the joining function skipping the release test"),
            // The first pass removes nothing, and a transcript it does not release stays unresolved.
            ("Object.assign(n,{resolved:o,dirs:s,complete:u});",
                "Object.assign(n,{resolved:o,dirs:s,complete:u});await p.default.promises.rm(H(o,l),{force:!0});", "removing in the first pass"),
            ("{n.declined=!0;return}", "{n.declined=!0}", "a refused release that goes on"),
            ("if(a.mayRelease&&!await a.mayRelease(l))", "if(a.mayRelease&&await a.mayRelease(l))", "a release test whose answer is inverted"),
            ("declined:!1,resolved:null,", "declined:!1,resolved:void 0,", "a first pass that starts resolved"),
            ("n.declined=!0;return}}catch{return}", "n.declined=!0;return}}catch{}", "a failed release test that goes on"),
        })
            Check(!ClaudeDesktopProbe.IsCompatible(Bundle6(remove: Remove6.Replace(from, to))), what + " is not reviewed");
        Check(ClaudeDesktopProbe.IsCompatible(Bundle6(remove: Remove6.Replace("async function it(", "async function iq(")
                .Replace("async function ot(e,t,n,r,i={}){return at(await it(", "async function ot(e,t,n,r,i={}){return at(await iq("))),
            "renamed passes stay compatible");
        Check(ClaudeDesktopProbe.IsCompatible(Bundle6(remove: Remove6.Replace("async function ot(", "async function oq("),
                exports: Exports6.Replace("exports.f=ot", "exports.f=oq"))),
            "a renamed joining function exported under the same name stays compatible");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle6(exports: Exports6.Replace("exports.f=ot", "exports.f=at")))
              && !ClaudeDesktopProbe.IsCompatible(Bundle6(exports: Exports6.Replace("exports.f=ot", "exports.f=Qe"))),
            "an export that names the second pass or another function instead of the joining one is not reviewed");
    }

    private static void TestWidgetGate()
    {
        const string selected = "p-main", other = "p-sub";
        var run = new DateTime(2026, 10, 1, 0, 17, 1);
        ClaudeShelfStatus Running(ClaudeDesktopAccountState app, string? profile, DateTime? host = null) =>
            ClaudeShelfStatus.Initial with { Desktop = ClaudeDesktopRunState.Running, App = app, AppProfileId = profile, HostStartedAt = host ?? run };
        var sub = new ClaudeDesktopAccountState(ClaudeDesktopAccountStatus.SignedIn, AccountB, OrgB);
        var checking = UiText.T("Claude 앱 계정을 확인하는 중입니다.");
        var unregistered = UiText.T("Claude 앱이 등록되지 않은 계정으로 로그인되어 있습니다.");
        var signedOut = UiText.T("Claude 앱이 로그아웃 상태입니다.");
        var failed = UiText.T("Claude 앱 계정의 사용량을 확인하지 못했습니다.");
        Check(UsageIndicatorForm.AppGate(Running(sub, selected), selected, false) is null, "the app account's own usage is shown");
        Check(UsageIndicatorForm.AppGate(Running(new(ClaudeDesktopAccountStatus.SignedIn, AccountB), null), selected, false) == checking,
            "a sign-in not yet initialized never shows the previous account's usage");
        Check(UsageIndicatorForm.AppGate(Running(new(ClaudeDesktopAccountStatus.SignedOut), null), selected, false) == signedOut,
            "a signed-out app shows no usage");
        Check(UsageIndicatorForm.AppGate(Running(new(ClaudeDesktopAccountStatus.Unregistered, AccountB), null), selected, false) == unregistered
              && UsageIndicatorForm.AppGate(Running(sub, null), selected, false) == unregistered,
            "an account without a registered profile shows no usage");
        Check(UsageIndicatorForm.AppGate(Running(sub, other), selected, true) == checking
              && UsageIndicatorForm.AppGate(Running(sub, other), selected, false) == failed,
            "until the app account's profile is selected, the widget explains instead of showing another account");
        Check(UsageIndicatorForm.AppGate(ClaudeShelfStatus.Initial with { Desktop = ClaudeDesktopRunState.Closed }, selected, false, failed, run) is null
              && UsageIndicatorForm.AppGate(Running(ClaudeDesktopAccountState.Unknown, null), selected, false) is null,
            "a closed app, or a run not observed yet, keeps the last selection's usage");
        Check(UsageIndicatorForm.AppGate(Running(ClaudeDesktopAccountState.Unknown, null), selected, false, failed, run) == failed,
            "a lapse in observing the same run never brings back another account's usage");
        Check(UsageIndicatorForm.AppGate(ClaudeShelfStatus.Initial with { Desktop = ClaudeDesktopRunState.Unknown }, selected, false, failed, run)
              == failed, "an app process that cannot be inspected keeps what was shown");
        Check(UsageIndicatorForm.AppGate(Running(ClaudeDesktopAccountState.Unknown, null, run.AddMinutes(5)), selected, false, signedOut, run) is null,
            "a new app run seen without a closed observation drops the previous run's reason");
    }

    private static async Task TestFollowingTheAppAccount()
    {
        var now = DateTimeOffset.Now;
        var selected = "p-main";
        var status = ClaudeShelfStatus.Initial;
        // Each selection waits until the test releases it, then behaves like the controller: a cancelled or no longer
        // wanted selection never lands.
        var requests = new List<(string Profile, TaskCompletionSource Gate, CancellationToken Token)>();
        using var follower = new ClaudeAppFollower(() => status, () => selected, async (profileId, stillWanted, token) =>
        {
            var gate = new TaskCompletionSource();
            requests.Add((profileId, gate, token));
            await gate.Task;
            token.ThrowIfCancellationRequested();
            if (!stillWanted()) throw new OperationCanceledException();
            selected = profileId;
        }, () => now, CancellationToken.None);
        void Confirm(string account, string org, string profile) => status = ClaudeShelfStatus.Initial with
        {
            Desktop = ClaudeDesktopRunState.Running, AppProfileId = profile,
            App = new ClaudeDesktopAccountState(ClaudeDesktopAccountStatus.SignedIn, account, org)
        };

        Confirm(AccountB, OrgB, "p-sub");
        follower.OnStatusChanged();
        requests[^1].Gate.SetException(new InvalidOperationException("needs sign-in"));
        Check(requests.Count == 1 && selected == "p-main" && follower.Following is null, "a failed selection keeps the previous selection");
        follower.OnStatusChanged(); follower.Ensure();
        Check(requests.Count == 1, "an unchanged app account is not retried at once");
        follower.OnUsageObserved("p-sub", succeeded: true);
        requests[^1].Gate.SetResult();
        Check(requests.Count == 2 && selected == "p-sub", "a successful sign-in or check of the app's account follows it at once");

        selected = "p-main"; now += TimeSpan.FromMinutes(1);
        follower.Ensure(); requests[^1].Gate.SetException(new InvalidOperationException("temporary"));
        now += TimeSpan.FromMinutes(1); follower.Ensure();
        Check(requests.Count == 3, "a failed selection waits before retrying");
        now += TimeSpan.FromMinutes(1.5); follower.Ensure(); requests[^1].Gate.SetResult();
        Check(requests.Count == 4 && selected == "p-sub", "a failed selection is retried after two minutes");

        // B → C → B while selections run: only the newest request owns the state (R3).
        selected = "p-main";
        follower.Ensure(); var b1 = requests[^1];
        Confirm(AccountA, OrgA, "p-third"); follower.OnStatusChanged(); var c1 = requests[^1];
        Confirm(AccountB, OrgB, "p-sub"); follower.OnStatusChanged(); var b2 = requests[^1];
        Check(requests.Count == 7 && b1.Token.IsCancellationRequested && c1.Token.IsCancellationRequested
              && !b2.Token.IsCancellationRequested && follower.Following == "p-sub", "a newer app account cancels the selections before it");
        b1.Gate.SetResult();
        Check(requests.Count == 7 && !b2.Token.IsCancellationRequested && follower.Following == "p-sub",
            "an older request for the same account ending late leaves the current one alone");
        c1.Gate.SetException(new InvalidOperationException("late failure"));
        b2.Gate.SetResult();
        Check(selected == "p-sub" && follower.Following is null, "the current request lands");
        selected = "p-main"; follower.Ensure();
        Check(requests.Count == 8, "a stale request's failure never delays the next selection");
        requests[^1].Gate.SetResult();
        await Task.CompletedTask;
    }

    private static void TestLogGapsAndRotation()
    {
        using var fixture = new Fixture();
        var logs = Directory.CreateDirectory(Path.Combine(fixture.Root, "logs")).FullName;
        var main = Path.Combine(logs, "main.log"); var previous = Path.Combine(logs, "main1.log");
        var start = new DateTime(2026, 10, 1, 1, 0, 0);
        string Line(int seconds, string text) => start.AddSeconds(seconds).ToString("yyyy-MM-dd HH:mm:ss") + " [info] " + text;
        var initA = Line(1, $"[LocalSessionManager] Initialization succeeded — accountId={AccountA}, orgId={OrgA}, existingSessions=0");
        string InitB(int seconds) => Line(seconds, $"[LocalSessionManager] Initialization succeeded — accountId={AccountB}, orgId={OrgB}, existingSessions=0");
        string SignOut(int seconds, string account) =>
            Line(seconds, $"[account] Login-state transition (loggedOut: false → true, uuid: {account} → <none>), clearing oauth cache");
        void Rotate(params string[] fresh) { File.Move(main, previous, overwrite: true); File.WriteAllLines(main, fresh); }

        File.WriteAllLines(main, [initA]);
        var watcher = new ClaudeDesktopLogWatcher(logs);
        Check(watcher.Update(start, [A, B]).Confirmed, "the fixture run confirms A");
        File.Delete(main);
        Check(watcher.Update(start, [A, B]).Status == ClaudeDesktopAccountStatus.Unknown, "a missing log never keeps the previous confirmation");
        File.WriteAllLines(main, [initA, SignOut(5, AccountA)]);
        Check(watcher.Update(start, [A, B]).Status == ClaudeDesktopAccountStatus.SignedOut, "a readable log is replayed after a gap");

        // Rotation renames main.log and starts a new file under the same name, which on NTFS inherits the old
        // creation time. The new file is also longer than the old read position, so only the file identity tells.
        File.WriteAllLines(main, [initA]);
        var rotating = new ClaudeDesktopLogWatcher(logs);
        Check(rotating.Update(start, [A, B]).Confirmed, "observation starts on A");
        File.AppendAllLines(main, [SignOut(10, AccountA)]);
        Rotate(Line(20, $"[account] Login-state transition (loggedOut: true → false, uuid: <none> → {AccountB}), clearing oauth cache"), InitB(20));
        var rotated = rotating.Update(start, [A, B]);
        Check(rotated.Confirmed && rotated.Key == B.Key, "a sign-out written just before rotation and the account after it are both seen");
        File.AppendAllLines(main, [Line(25, "later line")]);
        Check(rotating.Update(start, [A, B]).Key == B.Key, "the new file is then followed incrementally");

        // Two rotations between reads: main1.log is no longer the file that was being read, so lines may be lost.
        Rotate(Line(30, "filler one"));
        Rotate(Line(40, "filler two"));
        Check(rotating.Update(start, [A, B]).Status == ClaudeDesktopAccountStatus.Unknown, "lost log lines leave the account unknown");

        // A main.log that is still empty at the first observation is identified all the same: a switch written to it
        // before the next rotation is read from main1.log, not skipped (R3).
        File.WriteAllLines(previous, [initA]);
        File.WriteAllText(main, "");
        var fresh = new ClaudeDesktopLogWatcher(logs);
        Check(fresh.Update(start, [A, B]).Key == A.Key, "a run whose current log is still empty confirms A from main1.log");
        File.AppendAllLines(main, [Line(50, $"[account] Login-state transition (loggedOut: false → false, uuid: {AccountA} → {AccountB}), clearing oauth cache"), InitB(50)]);
        Rotate(Line(55, "unrelated"));
        Check(fresh.Update(start, [A, B]).Key == B.Key, "a switch in a file that was empty at first sight is not skipped");

        // A rotated file whose last line is unfinished is a gap. However often the run is replayed, no account is
        // confirmed from lines before it; the complete lines written after it can (R4).
        File.AppendAllLines(main, [InitB(56)]); // evidence a replay must not revive
        File.AppendAllText(main, SignOut(60, AccountB)); // cut off before its newline
        Rotate(Line(65, "unrelated"));
        Check(fresh.Update(start, [A, B]).Status == ClaudeDesktopAccountStatus.Unknown, "an unfinished last line in a rotated file is a gap");
        Check(fresh.Update(start, [A, B]).Status == ClaudeDesktopAccountStatus.Unknown
              && fresh.Update(start, [A, B]).Status == ClaudeDesktopAccountStatus.Unknown,
            "replaying never re-confirms the account from before the gap");
        File.AppendAllLines(main, [Line(70, $"[account] Login-state transition (loggedOut: true → false, uuid: <none> → {AccountB}), clearing oauth cache"), InitB(70)]);
        Check(fresh.Update(start, [A, B]).Key == B.Key, "complete account lines after the gap confirm again");
        var restarted = new ClaudeDesktopLogWatcher(logs);
        Check(restarted.Update(start, [A, B]).Key == B.Key, "a new observer skips main1.log's lines before its unfinished end");

        // A gap is reported as lost lines, so the widget never takes the next account it sees for a switch.
        Rotate(Line(80, "one"));
        Rotate(Line(81, "two"));
        fresh.Update(start, [A, B]);
        Check(fresh.LostLines, "lines lost to two rotations between reads are reported");
    }

    private static void TestRelaunchReservation()
    {
        using var fixture = new Fixture();
        var now = DateTimeOffset.Now; // RelaunchArmed compares with the real clock
        var state = ClaudeDesktopRunState.Closed;
        var launches = 0; var failLaunch = false;
        var package = new ClaudeDesktopPackage("Claude_9.9.9.9_x64__pzs8sxrjxfjjc", new Version(9, 9, 9, 9), fixture.Root, "Claude_pzs8sxrjxfjjc");
        var environment = new ClaudeShelfEnvironment(() => new ClaudeDesktopRuntime(state, null), () => package, _ => ClaudeDesktopContract.Reviewed,
            _ => { if (failLaunch) throw new IOException("launch failed"); launches++; }, () => now);
        using var owner = new System.Windows.Forms.Control();
        using var service = new ClaudeShelfService(owner, () => [], fixture.Shelf(), environment,
            new ClaudeDesktopLogWatcher(Path.Combine(fixture.Root, "no-logs")));
        // One timer tick, split where the UI thread hands the reservation to the background pass and takes it back.
        ClaudeShelfPass Observe() => service.Work([], service.ActiveReservation());
        void Tick() => service.Accept(Observe());
        void Quit() { state = ClaudeDesktopRunState.Running; Tick(); state = ClaudeDesktopRunState.Closed; Tick(); now += TimeSpan.FromSeconds(6); }

        service.ArmRelaunch(true);
        Tick();
        Check(launches == 0 && service.Status.RelaunchArmed, "the closure grace period does not consume the request");
        now += TimeSpan.FromSeconds(6);
        Tick();
        Check(launches == 1 && !service.Status.RelaunchArmed, "the next accepted closure reopens Claude once and consumes the request");
        now += TimeSpan.FromSeconds(60);
        Tick();
        Check(launches == 1, "a later pass of the same closure never reopens again");

        failLaunch = true;
        service.ArmRelaunch(true); Quit(); Tick();
        Check(!service.Status.RelaunchArmed && service.Status.Problem == ClaudeShelfProblem.Failed, "a failed reopen is reported and consumed");
        failLaunch = false;
        Quit(); Tick();
        Check(launches == 1 && service.Status.Problem == ClaudeShelfProblem.None,
            "a manual reopen clears the failure, and a later quit never reuses a consumed request");

        service.ArmRelaunch(true); Quit();
        var inFlight = Observe();
        service.ArmRelaunch(false); service.ArmRelaunch(true);
        var replacement = service.ActiveReservation();
        service.Accept(inFlight);
        Check(launches == 1 && service.Status.RelaunchArmed && service.ActiveReservation() == replacement,
            "a pass that accepted a cancelled request neither reopens nor consumes the new one");
        service.ArmRelaunch(false);
        Check(!service.Status.RelaunchArmed, "a request can be cancelled");

        // An incompatible version is reported while the app runs, an unreviewed one is told apart, and both clear once a
        // reviewed one is installed.
        var contract = ClaudeDesktopContract.Incompatible;
        var guarded = new ClaudeShelfService(owner, () => [], fixture.Shelf(),
            environment with { Observe = () => new ClaudeDesktopRuntime(ClaudeDesktopRunState.Running, null), Contract = _ => contract },
            new ClaudeDesktopLogWatcher(Path.Combine(fixture.Root, "no-logs")));
        guarded.Accept(guarded.Work([], null));
        Check(guarded.Status.Problem == ClaudeShelfProblem.Incompatible && guarded.Status.ProblemDetail == "9.9.9.9"
              && guarded.Status.Unreviewed is null, "a running app of an incompatible version is reported before it quits");
        contract = ClaudeDesktopContract.Unreviewed;
        guarded.Accept(guarded.Work([], null));
        Check(guarded.Status.Problem == ClaudeShelfProblem.None && guarded.Status.Unreviewed == "9.9.9.9",
            "an unreviewed version does not stop filling, and is reported for the warning");
        contract = ClaudeDesktopContract.Reviewed;
        guarded.Accept(guarded.Work([], null));
        Check(guarded.Status.Problem == ClaudeShelfProblem.None && guarded.Status.Unreviewed is null, "a reviewed version clears both");
        guarded.Dispose();
        var paused = ClaudeShelfStatus.Initial with
        {
            Desktop = ClaudeDesktopRunState.Running, MappedAccounts = 2, PendingForApp = 1, AppProfileId = "p",
            Problem = ClaudeShelfProblem.Incompatible, RelaunchArmedUntil = DateTimeOffset.Now.AddMinutes(10)
        };
        var footer = ClaudeAccountPanel.ShelfFooter(paused, "메인", DateTimeOffset.Now);
        Check(footer.Warning && footer.Text == UiText.T("세션 목록 채우기 멈춤 · 이 Claude 버전은 아직 확인되지 않았습니다.")
              && footer.Action == UiText.T("다시 열기 취소"), "a paused fill is said first and never promised, with the reopen still cancellable");
        var warned = paused with { Problem = ClaudeShelfProblem.None, Unreviewed = "9.9.9.9", RelaunchArmedUntil = null };
        var warning = ClaudeAccountPanel.ShelfFooter(warned, "메인", DateTimeOffset.Now);
        Check(warning.Warning && warning.Text == UiText.T("확인 전 Claude 버전 · 세션은 삭제 대신 보관해 주세요.") && warning.Action is null
              && ClaudeAccountPanel.ShelfFooter(warned with { RelaunchArmedUntil = DateTimeOffset.Now.AddMinutes(10) }, "메인",
                  DateTimeOffset.Now).Action == UiText.T("다시 열기 취소"),
            "an unreviewed version warns to archive rather than delete, with the reopen still cancellable");
        Check(!ClaudeAccountPanel.ShelfFooter(warned with { MappedAccounts = 1 }, "메인", DateTimeOffset.Now).Warning,
            "with one account nothing is filled, and nothing is warned");

        service.ArmRelaunch(true); Quit();
        var pass = Observe();
        state = ClaudeDesktopRunState.Running; // the user reopened Claude before the pass came back
        service.Accept(pass);
        Check(launches == 1 && !service.Status.RelaunchArmed, "an app the user already reopened is not launched again");

        service.ArmRelaunch(true); Quit();
        var late = Observe();
        now += TimeSpan.FromMinutes(31); // the request expires while the pass runs
        service.Accept(late);
        Check(launches == 1 && !service.Status.RelaunchArmed, "a request that expires while the pass runs never reopens");

        service.ArmRelaunch(true);
        now += TimeSpan.FromMinutes(31);
        Tick();
        Check(!service.Status.RelaunchArmed, "an unused request expires after 30 minutes");
    }

    private static void TestLayoutKeeping()
    {
        using var fixture = new Fixture();
        // Accounts of their own: the widget's events and mailboxes are per account, and a running widget must not hear this test.
        var c = new ClaudeShelfAccount("c0c0c0c0-0000-4000-8000-00000000000c", "c1c1c1c1-0000-4000-8000-00000000000c", "C", "p-c");
        var d = new ClaudeShelfAccount("d0d0d0d0-0000-4000-8000-00000000000d", "d1d1d1d1-0000-4000-8000-00000000000d", "D", "p-d");
        var logs = Directory.CreateDirectory(Path.Combine(fixture.Root, "logs")).FullName;
        var main = Path.Combine(logs, "main.log");
        var start = new DateTime(2026, 10, 1, 2, 0, 0);
        string Line(int seconds, string text) => start.AddSeconds(seconds).ToString("yyyy-MM-dd HH:mm:ss") + " [info] " + text;
        string Init(int seconds, ClaudeShelfAccount account) => Line(seconds,
            $"[LocalSessionManager] Initialization succeeded — accountId={account.AccountUuid}, orgId={account.OrgUuid}, existingSessions=0");
        string Switch(int seconds, ClaudeShelfAccount from, ClaudeShelfAccount to) => Line(seconds,
            $"[account] Login-state transition (loggedOut: true → false, uuid: {from.AccountUuid} → {to.AccountUuid}), clearing oauth cache");
        // When the app's log confirmed an account.
        DateTimeOffset Logged(int seconds) => new(start.AddSeconds(seconds));
        var now = DateTimeOffset.Now;
        var package = new ClaudeDesktopPackage("Claude_9.9.9.9_x64__pzs8sxrjxfjjc", new Version(9, 9, 9, 9), fixture.Root, "Claude_pzs8sxrjxfjjc");
        var runtime = new ClaudeDesktopRuntime(ClaudeDesktopRunState.Running, start);
        var environment = new ClaudeShelfEnvironment(() => runtime, () => package, _ => ClaudeDesktopContract.Reviewed, _ => { }, () => now);
        // The app's own stores under the fixture, not there at first.
        var storage = Path.Combine(fixture.Root, "Local Storage", "leveldb");
        var indexedDb = Path.Combine(fixture.Root, "IndexedDB", "https_claude.ai_0.indexeddb.leveldb");
        var sources = new ClaudeSessionSync.Sources(fixture.Sessions, storage, indexedDb, fixture.Projects, fixture.Ledger);
        using var owner = new System.Windows.Forms.Control();
        using var service = new ClaudeShelfService(owner, () => [], fixture.Shelf(), environment, new ClaudeDesktopLogWatcher(logs), sources);
        ClaudeSessionLayout? Kept() => ClaudeSessionLayout.Load(fixture.Ledger).Layout;
        service.Work([], null);
        Check(ClaudeSessionLayout.Load(fixture.Ledger) is (null, false), "nothing is kept before the mapped accounts are known");
        service.SetAccountsForTest([c, d]);
        // C has a session D lacks, so arriving at D leaves sessions to fill; both list another conversation.
        fixture.Record(c, "local_c1", fixture.Transcript());
        var shared = fixture.Transcript();
        fixture.Record(c, "local_c0", shared);
        fixture.Record(d, "local_d0", shared);
        ClaudeShelfPass Pass()
        {
            var pass = service.Work([], service.ActiveReservation());
            service.Accept(pass);
            return pass;
        }
        ClaudeShelfPass Step(params string[] lines)
        {
            File.AppendAllLines(main, lines);
            now += TimeSpan.FromSeconds(1); // well within the accounts refresh interval, so the test accounts stay
            return Pass();
        }

        var first = Step(Init(1, c));
        Check(first.SwitchedFrom is null && Kept() is { Active: var shown } kept && shown == c.Key && kept.Accounts.Count == 2
              && kept.Accounts[c.Key] == new ClaudeSessionLayout.Account("C") && kept.Checkpoints.Count == 0,
            "the layout starts with the mapped accounts and the account the app shows; without the app's stores nothing is checkpointed");
        // Opening either throws unless the widget made it.
        using (EventWaitHandle.OpenExisting(ClaudeSessionSync.PlannedEventName(c.Key)))
        using (System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(ClaudeSessionSync.MailboxName(d.Key))) { }
        // The stores appear: the account the app shows, never checkpointed, is checkpointed as soon as it reads whole.
        ClaudeSessionSyncTests.CreateDatabase(indexedDb);
        ClaudeSessionSyncTests.AppendRaw(indexedDb, 42, ClaudeSessionSyncTests.PinKey(5), ClaudeSessionSyncTests.PinValue([]));
        ClaudeSessionSyncTests.CreateDatabase(storage);
        Check(ClaudeSessionSyncTests.WriteSidebar(storage,
                new JsonObject { ["state"] = new JsonObject { ["customGroupsByScope"] = new JsonObject() }, ["version"] = 1 },
                Path.Combine(fixture.Root, "backups")), "the fixture sidebar state is stored");
        Pass();
        Check(Kept() is { } seeded && seeded.Checkpoints[c.Key] is { Stay: 0 } && seeded.Changed[c.Key].ContainsKey("local_c1"),
            "an account seen without a checkpoint is checkpointed once its stores read whole, seeding what it shows");
        // The user renames a session in C, then the app goes to D and back.
        var c1 = Path.Combine(fixture.ShelfPath(c), "local_c1.json");
        var record = JsonNode.Parse(File.ReadAllText(c1))!.AsObject();
        record["title"] = "새 이름";
        File.WriteAllText(c1, record.ToJsonString());
        var toD = Step(Switch(10, c, d), Init(11, d));
        Check(toD.SwitchedFrom == c.Key && Kept() is { Active: var arrived } switched && arrived == d.Key
              && switched.Accounts[c.Key].LeftAt == Logged(11) && switched.Accounts[d.Key] is { Stay: 1, ArrivedAt: var arrivedAt }
              && arrivedAt == Logged(11) && switched.Checkpoints[d.Key].Stay == 1,
            "a switch it watched dates leaving one account and coming to the other by the log line that confirmed it, starts a stay "
            + "and checkpoints the account it comes to");
        Check(service.Status is { AppProfileId: "p-d", PendingForApp: 1, RelaunchArmed: true },
            "a switch to an account that lacks sessions arms the reopen, so one quit fills the list and brings the app back");
        service.ArmRelaunch(false);
        var back = Step(Switch(20, d, c), Init(21, c));
        Check(back.SwitchedFrom == d.Key && Kept() is { } returned && returned.Accounts[d.Key].LeftAt == Logged(21)
              && returned.Accounts[c.Key] is { Stay: 1 } && returned.Checkpoints[c.Key].Stay == 1
              && returned.Changed[c.Key]["local_c1"].Title == new ClaudeSessionLayout.Stamp<string>("새 이름", Logged(11))
              && !service.Status.RelaunchArmed,
            "coming back, what changed there in its last stay is dated to when the app left it");

        // A plan command asks the widget through the account's mailbox and takes the answer to its own request.
        var asking = typeof(ClaudeSessionSync).GetMethod("Ask", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!;
        string? Ask(string account, string self) => (string?)asking.Invoke(null, [account, self]);
        JsonObject Answered(string account, string self)
        {
            var request = Task.Run(() => Ask(account, self));
            for (var tries = 0; !request.IsCompleted && tries < 400; tries++) { Pass(); Thread.Sleep(25); }
            Check(request.Wait(TimeSpan.FromSeconds(40)) && request.Result is not null, "the widget answers a plan request");
            return JsonNode.Parse(request.Result!)!.AsObject();
        }
        var planned = Answered(c.Key, "local_c1");
        Check(planned["status"]!.GetValue<string>() == "ok" && planned["checkpointed"]!.GetValue<bool>() && planned["current"]!.GetValue<string>() == c.Key
              && Kept() is { } served && served.Pendings.ContainsKey(c.Key) && served.Checkpoints[c.Key].At == now,
            "a plan asked for in the account the app shows is made from a checkpoint the widget keeps, and kept as the plan in progress");
        var viewed = Answered(d.Key, "local_d0");
        Check(viewed["status"]!.GetValue<string>() == "ok" && !viewed["checkpointed"]!.GetValue<bool>() && !Kept()!.Pendings.ContainsKey(d.Key),
            "one asked for in an account the app does not show is for viewing only");
        using (var planning = EventWaitHandle.OpenExisting(ClaudeSessionSync.PlannedEventName(c.Key)))
        using (var answering = EventWaitHandle.OpenExisting(ClaudeSessionSync.CheckpointedEventName(c.Key)))
        using (var mailbox = System.IO.MemoryMappedFiles.MemoryMappedFile.OpenExisting(ClaudeSessionSync.MailboxName(c.Key)))
        using (var view = mailbox.CreateViewAccessor(0, ClaudeSessionSync.MailboxBytes))
        {
            var request = Task.Run(() => Ask(c.Key, "local_c1"));
            Check(planning.WaitOne(TimeSpan.FromSeconds(10)), "the request is signalled");
            ClaudeSessionSync.WriteSlot(view, ClaudeSessionSync.AnswerOffset, ClaudeSessionSync.MailboxBytes - ClaudeSessionSync.AnswerOffset,
                Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef\n{\"status\":\"stale\"}"));
            answering.Set();
            Thread.Sleep(300);
            Check(!request.IsCompleted, "an answer to another request, one that ran out of time, is passed over");
            planning.Set();
            Pass();
            Check(request.Wait(TimeSpan.FromSeconds(10)) && JsonNode.Parse(request.Result!)!["status"]!.GetValue<string>() == "ok",
                "the answer to this request is taken");
        }
        Check(Ask("e0e0e0e0-0000-4000-8000-00000000000e/e1e1e1e1-0000-4000-8000-00000000000e", "local_e1") is null,
            "with no widget listening for an account, nobody answers: the command makes its own plan, for viewing only");

        // Labels follow the account page; an account no longer mapped is forgotten whole, and nothing listens for it.
        service.SetAccountsForTest([c with { Label = "C2" }]);
        Pass();
        Check(Kept() is { } relabelled && relabelled.Accounts.Keys.SequenceEqual([c.Key]) && relabelled.Accounts[c.Key].Label == "C2"
              && !relabelled.Checkpoints.ContainsKey(d.Key) && !EventWaitHandle.TryOpenExisting(ClaudeSessionSync.PlannedEventName(d.Key), out _),
            "a renamed account is kept under its new label; one no longer mapped is forgotten whole");
        service.SetAccountsForTest([c, d]);
        Pass();

        // A choice the user makes while a switch pass runs stands: the pass does not arm the reopen again.
        service.ArmRelaunch(false);
        File.AppendAllLines(main, [Switch(30, c, d), Init(31, d)]);
        now += TimeSpan.FromSeconds(1);
        var racing = service.Work([], service.ActiveReservation());
        service.ArmRelaunch(false);
        service.Accept(racing);
        Check(racing.SwitchedFrom == c.Key && racing.Status.PendingForApp > 0 && !service.Status.RelaunchArmed,
            "a reopen cancelled while the switch pass ran stays cancelled");
        Step(Switch(40, d, c), Init(41, c));

        // Lost log lines: a switch found after them is late. The account left keeps the time it had; the one come to starts a stay.
        File.Delete(main);
        Pass();
        File.WriteAllLines(main, [Init(50, d)]);
        var late = Pass();
        Check(late.SwitchedFrom is null && Kept() is { Active: var lateActive } after && lateActive == d.Key
              && after.Accounts[c.Key].LeftAt == Logged(31) && after.Accounts[d.Key] is { Stay: 2, ArrivedAt: var lateAt } && lateAt == Logged(50),
            "a switch found after lost log lines dates the arrival only: when the account left was left is unknown");
        // A widget started anew takes up from the account it last confirmed: a switch made meanwhile is late too.
        File.AppendAllLines(main, [Switch(60, d, c), Init(61, c)]);
        runtime = new ClaudeDesktopRuntime(ClaudeDesktopRunState.Closed, null);
        using (var restarted = new ClaudeShelfService(owner, () => [], fixture.Shelf(), environment, new ClaudeDesktopLogWatcher(logs), sources))
        {
            restarted.Work([], null);
            restarted.SetAccountsForTest([c, d]);
            runtime = new ClaudeDesktopRuntime(ClaudeDesktopRunState.Running, start);
            var resumed = restarted.Work([], null);
            Check(resumed.SwitchedFrom is null && Kept() is { Active: var resumedActive } took && resumedActive == c.Key
                  && took.Accounts[d.Key].LeftAt == Logged(41) && took.Accounts[c.Key] is { Stay: 3, ArrivedAt: var resumedAt }
                  && resumedAt == Logged(61),
                "a widget started anew takes up from the account it last confirmed, and a switch made meanwhile dates the arrival only");
        }

        // The first format is read and saved in the current one: its accounts carry over with the evidence of its agreements.
        var path = Path.Combine(fixture.Ledger, ClaudeSessionLayout.FileName);
        File.WriteAllText(path, "{\"version\":1,\"accounts\":{\"" + c.Key + "\":{\"label\":\"C\",\"leftAt\":\"2026-10-13T01:02:03+09:00\"},\"" + d.Key
            + "\":{\"label\":\"D\",\"leftAt\":null}},\"pairs\":{\"" + c.Key + "|" + d.Key + "\":{\"" + c.Key + "\":{\"local_c1\":{\"pinned\":true}}}}}");
        runtime = new ClaudeDesktopRuntime(ClaudeDesktopRunState.Closed, null);
        using (var upgrading = new ClaudeShelfService(owner, () => [], fixture.Shelf(), environment, new ClaudeDesktopLogWatcher(logs), sources))
        {
            upgrading.Work([], null);
            Check(File.ReadAllText(path).Contains("\"version\": 2", StringComparison.Ordinal) && Kept() is { Upgraded: false } upgraded
                  && upgraded.Accounts[c.Key].LeftAt == DateTimeOffset.Parse("2026-10-13T01:02:03+09:00", System.Globalization.CultureInfo.InvariantCulture)
                  && upgraded.Evidences[c.Key]["local_c1"] == new ClaudeSessionLayout.Evidence(true, false),
                "a layout of the first format is saved in the current one, its accounts and the evidence of its agreements carried over");
        }

        // A layout that is there but cannot be read is never replaced. Without one the widget starts it once the mapped
        // accounts are known: the account the old reference names was left when it was chosen.
        File.WriteAllText(path, "{broken");
        var referenceFile = Path.Combine(fixture.Ledger, ClaudeSessionReference.FileName);
        using (var later = new ClaudeShelfService(owner, () => [], fixture.Shelf(), environment, new ClaudeDesktopLogWatcher(logs), sources))
        {
            later.Work([], null);
            later.SetAccountsForTest([c, d]);
            later.Work([], null);
            Check(File.ReadAllText(path) == "{broken", "an unreadable layout is left as it is");
            File.Delete(path);
            var chosen = now.AddDays(-1);
            new ClaudeSessionReference(c.Key, "C", "p-c", chosen, new Dictionary<string, string>()).Save(fixture.Ledger);
            using (new FileStream(referenceFile, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            {
                later.Work([], null);
                Check(!File.Exists(path), "an old reference that cannot be opened now holds the start");
            }
            later.Work([], null);
            Check(Kept() is { } started && started.Accounts[c.Key].LeftAt == chosen && started.Accounts[d.Key].LeftAt is null,
                "the first layout takes the account the old reference names as the one the app left last, when it was chosen");
        }
        File.Delete(path);
        File.WriteAllText(referenceFile, "{broken");
        using (var again = new ClaudeShelfService(owner, () => [], fixture.Shelf(), environment, new ClaudeDesktopLogWatcher(logs), sources))
        {
            again.Work([], null);
            again.SetAccountsForTest([c, d]);
            again.Work([], null);
            Check(!File.Exists(path), "an old reference that cannot be read as one holds the start, never taken for none");
            File.Delete(referenceFile);
            again.Work([], null);
            Check(Kept() is { } plain && plain.Accounts.Count == 2 && plain.Accounts.Values.All(account => account.LeftAt is null),
                "with no old reference the layout starts at once");
        }
    }

    /// <summary>Read-only: the shape of the app's sidebar store, values elided except names and orders.</summary>
    internal static void SidebarStoreShape()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var sources = ClaudeSessionSync.Sources.Default();
        var store = new ClaudeLocalStorage(sources.LocalStorage).Read(["dframe-store"], whileOpen: true);
        var root = System.Text.Json.Nodes.JsonNode.Parse(store.Values["dframe-store"]!)!.AsObject();
        var state = root["state"]!.AsObject();
        foreach (var (key, value) in state)
        {
            var json = value?.ToJsonString() ?? "null";
            Console.WriteLine($"state.{key}: " + (value is System.Text.Json.Nodes.JsonObject o
                ? "keys=" + string.Join(",", o.Select(p => p.Key).Take(8)) : json.Length > 300 ? "len=" + json.Length : json));
        }
        if (state["customGroupsByScope"] is System.Text.Json.Nodes.JsonObject scopes)
            foreach (var (scope, node) in scopes)
            {
                var s = node!.AsObject();
                Console.WriteLine($"scope {scope}: keys=" + string.Join(",", s.Select(p => p.Key)));
                foreach (var g in s["groups"]?.AsArray() ?? [])
                    Console.WriteLine("   group " + string.Join(" ", g!.AsObject().Select(p => p.Key + "=" + p.Value?.ToJsonString())));
                foreach (var (k, v) in s.Where(p => p.Key is not "groups" and not "assignments"))
                    Console.WriteLine("   " + k + " = " + v?.ToJsonString());
            }
        if (state["codeSidebarByScope"] is System.Text.Json.Nodes.JsonObject sidebars)
            foreach (var (scope, node) in sidebars) Console.WriteLine($"codeSidebar {scope}: {node?.ToJsonString()}");
        Console.WriteLine("pinnedOrder: " + state["pinnedOrder"]?.ToJsonString());
        Console.WriteLine("collapsedGroups: " + state["collapsedGroups"]?.ToJsonString());
        Console.WriteLine("groupByByMode: " + state["groupByByMode"]?.ToJsonString() + " sortByByMode: " + state["sortByByMode"]?.ToJsonString());
    }

    /// <summary>Read-only: every conversation two accounts both list (same transcript) where their pin, group or title
    /// differs, with what the widget's last checkpoint of each showed there and the newest change it recorded.</summary>
    internal static void LayoutDiff(string labelA, string labelB)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var sources = ClaudeSessionSync.Sources.Default();
        var (kept, _) = ClaudeSessionLayout.Load(sources.Ledger);
        string Key(string label) => kept!.Accounts.First(pair => pair.Value.Label == label).Key;
        var a = Key(labelA); var b = Key(labelB);
        string Dir(string key) => Path.Combine(sources.SessionsRoot, key.Split('/')[0], key.Split('/')[1]);
        var pins = ClaudeSidebarPins.Read(sources.IndexedDb);
        var store = new ClaudeLocalStorage(sources.LocalStorage).Read(["dframe-store"], whileOpen: true);
        var state = (JsonNode.Parse(store.Values["dframe-store"]!) as JsonObject)?["state"] as JsonObject;
        var layoutA = ClaudeSessionSync.LayoutOf(state, a, true)!; var layoutB = ClaudeSessionSync.LayoutOf(state, b, true)!;
        string? Group(ClaudeSessionSync.Layout layout, string id) => layout.Assignments.TryGetValue(id, out var g)
            ? layout.Groups.FirstOrDefault(x => x.Id == g)?.Name ?? "(?" + g + ")" : null;
        var recA = ClaudeSessionSync.ReadRecords(Dir(a), pins); var recB = ClaudeSessionSync.ReadRecords(Dir(b), pins);
        var byCli = recB.Where(r => r.Cli is not null).GroupBy(r => r.Cli!).ToDictionary(g => g.Key, g => g.ToList());
        Console.WriteLine($"{labelA}: {recA.Count} records, {labelB}: {recB.Count}; groups {labelA}=[{string.Join(", ", layoutA.Groups.Select(g => g.Name))}] {labelB}=[{string.Join(", ", layoutB.Groups.Select(g => g.Name))}]");
        foreach (var (label, key) in new[] { (labelA, a), (labelB, b) })
        {
            var account = kept!.Accounts[key];
            var point = kept.Checkpoints.GetValueOrDefault(key);
            var listed = kept.GroupsChanged.GetValueOrDefault(key);
            Console.WriteLine($"  {label}: stay {account.Stay} arrived {account.ArrivedAt:O} left {account.LeftAt:O}; checkpoint "
                + (point is null ? "none" : $"{point.At:O} (stay {point.Stay}) groups=[{string.Join(", ", point.Groups?.Select(g => g.Name) ?? [])}]")
                + (listed is null ? "" : $"; group list changed to [{string.Join(", ", listed.Order)}] at {listed.At:O}" + (listed.By is null ? "" : " from " + listed.By))
                + (kept.Pendings.TryGetValue(key, out var pending) ? $"; plan in progress: {pending.Sessions.Count} sessions, {pending.Renamed.Count} groups aside" : ""));
        }
        foreach (var x in recA.OrderBy(r => r.Title))
        {
            if (x.Cli is null || !byCli.TryGetValue(x.Cli, out var ys)) continue;
            foreach (var y in ys)
            {
                var ga = Group(layoutA, x.SessionId); var gb = Group(layoutB, y.SessionId);
                if (x.Pinned == y.Pinned && ga == gb && x.Title == y.Title) continue;
                Console.WriteLine($"- {x.Title} | pin {x.Pinned}/{y.Pinned} group {ga ?? "-"}/{gb ?? "-"}" + (x.Title != y.Title ? $" title vs '{y.Title}'" : "")
                    + $" | {labelA}: {Show(a, x.SessionId)} {labelB}: {Show(b, y.SessionId)}");
            }
        }
        string Show(string key, string id)
        {
            var seen = kept!.Checkpoints.GetValueOrDefault(key)?.Sessions.GetValueOrDefault(id);
            var change = kept.Changed.GetValueOrDefault(key)?.GetValueOrDefault(id);
            static string Stamp<T>(string name, ClaudeSessionLayout.Stamp<T>? stamp) => stamp is null ? ""
                : $" {name}={stamp.Value?.ToString() ?? "-"}@{stamp.At:MM-dd HH:mm}" + (stamp.By is null ? "" : "<" + stamp.By[..8]);
            return "seen[" + (seen is null ? "none" : $"pin={seen.Pinned?.ToString() ?? "?"} group={(seen.HasGroup ? seen.Group ?? "-" : "?")}") + "]"
                + " changed[" + (change is null ? "none" : (Stamp("pin", change.Pinned) + Stamp("group", change.Group) + Stamp("title", change.Title)).Trim()) + "]";
        }
    }

    /// <summary>Read-only: what the widget would start from (the kept layout as this version reads it, or else the old
    /// reference), and what each account's plan against it would change. Only a scratch ledger is written.</summary>
    internal static void LayoutDryRun()
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        var scratch = Path.Combine(Path.GetTempPath(), "gfs-agent", "claude-layout-dry-run-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            var real = ClaudeSessionSync.Sources.Default();
            // The widget's mapping cache and ledger, copied: the scratch copies are all that is written.
            foreach (var name in new[] { "accounts.json", "ledger.json" })
                if (File.Exists(Path.Combine(real.Ledger, name))) File.Copy(Path.Combine(real.Ledger, name), Path.Combine(scratch, name));
            var sources = real with { Ledger = scratch };
            var (layout, exists) = ClaudeSessionLayout.Load(real.Ledger);
            if (exists && layout is null) { Console.WriteLine("layout: unreadable"); return; }
            if (layout is null)
            {
                var accounts = ClaudeShelfAccounts.Resolve(new ClaudeProfileStore().ListProfiles(), scratch);
                var reference = ClaudeSessionReference.Load(real.Ledger);
                Console.WriteLine($"no layout yet; old reference: {reference?.Account} {reference?.Label} {reference?.SetAt:O}");
                layout = new ClaudeSessionLayout();
                foreach (var account in accounts.DistinctBy(account => account.Key))
                    layout.Accounts[account.Key] = new(account.Label, reference?.Account == account.Key ? reference.SetAt : null);
            }
            else Console.WriteLine($"layout: {(layout.Upgraded ? "first format, read as the current one" : "current format")}, active {layout.Active}, "
                + $"{layout.Checkpoints.Count} checkpoints, evidence for {layout.Evidences.Sum(pair => pair.Value.Count)} sessions");
            layout.Save(scratch);
            foreach (var (account, known) in layout.Accounts.ToArray())
            {
                var directory = Directory.EnumerateDirectories(real.SessionsRoot, "*", SearchOption.AllDirectories)
                    .FirstOrDefault(path => path.Replace(Path.DirectorySeparatorChar, '/').EndsWith(account, StringComparison.OrdinalIgnoreCase));
                if (directory is null) { Console.WriteLine($"== {known.Label}: no list"); continue; }
                var titles = ClaudeSessionSync.ReadRecords(directory).ToDictionary(record => record.SessionId, record => record.Title ?? record.SessionId);
                if (titles.Count == 0) { Console.WriteLine($"== {known.Label}: empty list"); continue; }
                // Coming to another account is a new stay there, as the widget records it: what its last stay changed is
                // no longer this stay's own, and what the account shown now changed is dated to leaving it.
                var shown = layout.Active is { } active && active != account ? layout.Accounts.GetValueOrDefault(active) : null;
                if (shown is not null)
                {
                    layout.Accounts[account] = known with { ArrivedAt = DateTimeOffset.Now, Stay = known.Stay + 1 };
                    layout.Accounts[layout.Active!] = shown with { LeftAt = DateTimeOffset.Now };
                }
                layout.Save(scratch);
                var plan = ClaudeSessionSync.Build(sources, titles.Keys.First());
                if (shown is not null)
                {
                    layout.Accounts[account] = known;
                    layout.Accounts[layout.Active!] = shown;
                }
                string Title(string id) => titles.GetValueOrDefault(id) ?? id;
                Console.WriteLine($"== {known.Label}: {plan.Status} left {known.LeftAt:O} warnings=[{string.Join(",", plan.Warnings ?? [])}] archive={plan.Archive?.Count}");
                foreach (var rename in plan.RenameGroups ?? []) Console.WriteLine($"  set group {rename.From} aside as {rename.To}");
                foreach (var name in plan.CreateGroups ?? []) Console.WriteLine($"  create group {name}");
                foreach (var move in plan.Moves ?? [])
                    Console.WriteLine($"  move to {move.Group ?? "(none)"}: {string.Join(" | ", move.Sessions.Select(Title))}");
                foreach (var pin in plan.Pins ?? []) Console.WriteLine($"  {(pin.Pinned ? "pin" : "unpin")} {pin.Title} (by {pin.By})");
                foreach (var rename in plan.Renames ?? []) Console.WriteLine($"  rename {rename.From} -> {rename.Title} (by {rename.By})");
                foreach (var group in plan.DeleteGroups ?? [])
                    Console.WriteLine($"  delete group {group.Name}" + (group.Remade ? " (made anew)" : "") + (group.Hidden > 0 ? $" ({group.Hidden} archived leave it)" : ""));
                if (plan.GroupOrder is { } order) Console.WriteLine($"  group order [{string.Join(", ", order)}] from {plan.GroupOrderBy ?? "this account"}");
                foreach (var item in plan.Models ?? []) Console.WriteLine($"  model {item.Title}: {item.From ?? "(none)"} -> {item.Model} (by {item.By})");
                foreach (var item in plan.Efforts ?? []) Console.WriteLine($"  effort {item.Title}: {item.From ?? "(none)"} -> {item.Effort} (by {item.By})");
                foreach (var item in plan.Permissions ?? [])
                    Console.WriteLine($"  mode {item.Title}: {item.From ?? "(none)"} -> {item.Mode}{(item.Raises ? ", asks" : "")} (by {item.By})");
                if (plan.SelfSettings is { } own) Console.WriteLine($"  the calling session's own: model {own.Model} effort {own.Effort}");
                if (plan.SettingsBeyond is { } beyond) Console.WriteLine($"  {beyond} conversations out of scope keep their model, effort and mode");
                foreach (var item in plan.Archive ?? [])
                    Console.WriteLine($"  archive {item.Title}{(item.Ready ? "" : " (not ready)")}: "
                        + (item.ArchivedIn is { } elsewhere ? $"archived in {elsewhere}" : $"older state of {item.NewestTitle}, went on {item.MovedOn?.ToLocalTime():MM-dd HH:mm}"));
                foreach (var item in plan.Undecided ?? [])
                    Console.WriteLine($"  undecided {item.Title}: continued by {item.ContinuedBy}, {item.Why}");
            }
        }
        finally { Directory.Delete(scratch, true); }
    }

    /// <summary>Read-only report of the real environment: nothing is written outside a temporary cache.</summary>
    internal static void LiveProbe()
    {
        var package = ClaudeDesktopProbe.FindPackage();
        Console.WriteLine($"package: {package?.FullName ?? "(none)"}");
        if (package is not null && ClaudeDesktopProbe.Contract(package.AsarPath) is var contract)
        {
            Console.WriteLine($"contract: {contract}");
            // Where the shape moved, so a review of a new version starts at the right flow.
            if (contract == ClaudeDesktopContract.Unreviewed)
                foreach (var failure in ClaudeDesktopProbe.Unmatched(File.ReadAllBytes(package.AsarPath)))
                    Console.WriteLine($"  unmatched: {failure}");
            var scheduled = ClaudeDesktopProbe.ScheduledContract(package.AsarPath);
            Console.WriteLine($"scheduled lists: {scheduled}");
            if (scheduled == ClaudeDesktopContract.Unreviewed)
                foreach (var failure in ClaudeDesktopProbe.ScheduledUnmatched(File.ReadAllBytes(package.AsarPath)))
                    Console.WriteLine($"  unmatched: {failure}");
        }
        var runtime = ClaudeDesktopProbe.Observe();
        Console.WriteLine($"desktop: {runtime.State} host={runtime.HostStartedAt:yyyy-MM-dd HH:mm:ss}");
        var scratch = Path.Combine(Path.GetTempPath(), "gfs-agent", "claude-shelf-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            var accounts = ClaudeShelfAccounts.Resolve(new ClaudeProfileStore().ListProfiles(), scratch);
            foreach (var account in accounts) Console.WriteLine($"account: {account.Label} {account.Key}");
            var state = new ClaudeDesktopLogWatcher().Update(runtime.HostStartedAt, accounts);
            Console.WriteLine($"app account: {state.Status} {state.AccountUuid} {state.OrgUuid} loaded={state.LoadedSessions}");
            var plan = new ClaudeSessionShelf(liveWriters: null, acquireLease: () => new MemoryStream()).Plan(accounts);
            foreach (var account in accounts) Console.WriteLine($"pending for {account.Label}: {plan.PendingFor(account.Key)}");
            foreach (var (pair, reasons) in plan.Skipped)
                Console.WriteLine($"skipped {pair}: " + string.Join(", ", reasons.Select(reason => reason.Key + "=" + reason.Value)));
            foreach (var (key, reason) in plan.Held) Console.WriteLine($"held {key}: {reason}");
            foreach (var retirement in plan.Retirements)
                Console.WriteLine($"archive {retirement.Target[..8]} {retirement.File[..14]} -> successor {retirement.SuccessorFile[..14]}");
            // Sidebar dry run on a scratch copy of the settings file; the real file is not written.
            var paths = ClaudeShelfPaths.CreateDefault();
            if (Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "claude_desktop_config.json") is var config && File.Exists(config))
            {
                Directory.CreateDirectory(scratch);
                var copy = Path.Combine(scratch, "claude_desktop_config.json");
                File.Copy(config, copy);
                var mirror = Path.Combine(paths.LedgerDirectory, "sidebar-mirror.json");
                if (File.Exists(mirror)) File.Copy(mirror, Path.Combine(scratch, "sidebar-mirror.json"));
                var links = ClaudeSidebarPrefs.Links(ClaudeShelfLedger.Load(paths.LedgerDirectory, paths.SessionsRoot), paths.SessionsRoot);
                var moved = plan.Retirements.ToDictionary(r => ClaudeSidebarPrefs.ListId(r.File), r => ClaudeSidebarPrefs.ListId(r.SuccessorFile));
                var outcome = ClaudeSidebarPrefs.Sync(copy, scratch, links, moved, () => true);
                Console.WriteLine($"sidebar dry run (settings mirror): links={links.Count} {outcome}");
                // The real store, on a copy inside this probe's scratch folder (removed when the probe ends).
                var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "Local Storage", "leveldb");
                var trial = Path.Combine(scratch, "ls-trial");
                Directory.CreateDirectory(trial);
                foreach (var file in Directory.EnumerateFiles(real))
                    if (!Path.GetFileName(file).Equals("LOCK", StringComparison.OrdinalIgnoreCase)) File.Copy(file, Path.Combine(trial, Path.GetFileName(file)));
                File.WriteAllText(Path.Combine(trial, "LOCK"), "");
                var trialState = trial + "-state";
                var trialOutcome = ClaudeSidebarPrefs.Sync(new ClaudeSidebarPrefs.LocalStorageSource(new ClaudeLocalStorage(trial), trialState, () => DateTimeOffset.UtcNow),
                    trialState, links, moved, () => true);
                Console.WriteLine($"sidebar trial on a copy of the real store: {trialOutcome}");
            }
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
    }

    private static void TestConversationLineage()
    {
        using var fixture = new Fixture();
        // A conversation continued in A: its newer record lists the earlier transcript id, and the transcripts show the
        // conversation went on from there.
        var states = Conversation(fixture, 2);
        var old = states[0]; var newer = states[1];
        fixture.Record(A, "local_a1a1a1a1-0000-4000-8000-000000000001", old);
        fixture.Record(A, "local_a1a1a1a1-0000-4000-8000-000000000002", newer, extra: r => r["priorCliSessionIds"] = new JsonArray(old));
        var plan = fixture.Shelf().Plan([A, B]);
        Check(plan.Actions.Count == 1 && plan.Actions[0].CliSessionId == newer && plan.Skipped.Values.Single()["superseded"] == 1,
            "an older state of a conversation is never copied when a newer one is in the same list");

        // B already has the newer state: an older id it lists, and continues, is present.
        fixture.Record(B, "local_b2b2b2b2-0000-4000-8000-000000000001", newer, extra: r => r["priorCliSessionIds"] = new JsonArray(old));
        Check(fixture.Shelf().Plan([A, B]).Actions.Count == 0, "a conversation already listed in a newer state is present");

        // Transcripts that cannot show the conversation went on (no dated entries here): the earlier state may be a branch.
        using var undated = new Fixture();
        var u1 = undated.Transcript(); var u2 = undated.Transcript();
        undated.Record(A, "local_a1a1a1a1-0000-4000-8000-000000000003", u1);
        undated.Record(A, "local_a1a1a1a1-0000-4000-8000-000000000004", u2, extra: r => r["priorCliSessionIds"] = new JsonArray(u1));
        Check(undated.Shelf().Plan([A, B]).Actions.Count == 2, "an earlier state the transcripts cannot show as gone on from is offered too");

        // A copy made before the conversation moved on is archived, not deleted, once a newer record continuing it is in
        // that list (listed there some other way here: a newer state this utility brings advances the copy in place, see
        // TestAdvancingOlderStates).
        using var moved = new Fixture();
        var movedStates = Conversation(moved, 2);
        var first = movedStates[0]; var second = movedStates[1];
        moved.Record(A, "local_c3c3c3c3-0000-4000-8000-000000000001", first);
        var shelf = moved.Shelf();
        Check(shelf.Apply([A, B], () => true).Created == 1, "the first state is copied to B");
        var stale = moved.Files(B).Single();
        File.Delete(moved.Files(A).Single());
        moved.Record(B, "local_c3c3c3c3-0000-4000-8000-000000000002", second, extra: r => r["priorCliSessionIds"] = new JsonArray(first));
        var result = shelf.Apply([A, B], () => true);
        var archived = JsonNode.Parse(File.ReadAllText(stale))!.AsObject();
        Check(result.Created == 1 && result.Retired == 1 && File.Exists(stale) && archived["isArchived"]!.GetValue<bool>()
              && archived["cliSessionId"]!.GetValue<string>() == first,
            "the superseded copy stays on disk, archived, with its transcript reference intact");
        Check(shelf.Apply([A, B], () => true) is { Created: 0, Retired: 0 }, "a retired copy is neither recreated nor archived twice");
        var ledger = JsonNode.Parse(File.ReadAllText(Path.Combine(moved.Ledger, "ledger.json")))!;
        Check(ledger["created"]!.AsArray().Count(entry => entry!["retiredAt"] is not null) == 1, "the ledger records the retirement");

        // Records the app wrote, and copies the app continued, are left alone.
        using var native = new Fixture();
        var older = native.Transcript(); var newest = native.Transcript(); var continued = native.Transcript();
        native.Record(B, "local_d4d4d4d4-0000-4000-8000-000000000001", older);
        native.Record(A, "local_d4d4d4d4-0000-4000-8000-000000000002", newest, extra: r => r["priorCliSessionIds"] = new JsonArray(older));
        var nativeResult = native.Shelf().Apply([A, B], () => true);
        Check(nativeResult.Retired == 0 && JsonNode.Parse(File.ReadAllText(Path.Combine(native.ShelfPath(B), "local_d4d4d4d4-0000-4000-8000-000000000001.json")))!["isArchived"]!.GetValue<bool>() == false,
            "an older record the app wrote is never archived");
        // B received a copy of the newest state; the app then continued that copy under B.
        var copy = native.Files(B).Single(path => !path.EndsWith("0001.json", StringComparison.Ordinal));
        var record = JsonNode.Parse(File.ReadAllText(copy))!.AsObject();
        record["cliSessionId"] = continued; record["priorCliSessionIds"] = new JsonArray(older, newest);
        File.WriteAllText(copy, record.ToJsonString());
        Check(native.Shelf().Apply([A, B], () => true).Retired == 0 && !JsonNode.Parse(File.ReadAllText(copy))!["isArchived"]!.GetValue<bool>(),
            "a copy the app continued is the user's conversation now and is not archived");
    }

    private static void TestSidebarPrefs()
    {
        using var fixture = new Fixture();
        var config = Path.Combine(fixture.Root, "claude_desktop_config.json");
        var state = Path.Combine(fixture.Root, "state");
        const string idA = "code:local_aaaaaaaa-1111-4000-8000-000000000001", idB = "code:local_bbbbbbbb-1111-4000-8000-000000000001";
        const string otherA = "code:local_aaaaaaaa-1111-4000-8000-000000000009";
        var link = new ClaudeSidebarPrefs.Link(A.Key, idA, B.Key, idB, B.Key + "/local_bbbbbbbb-1111-4000-8000-000000000001.json");
        void Write(JsonObject prefs) => File.WriteAllText(config, new JsonObject
        { ["globalShortcut"] = "", ["preferences"] = new JsonObject { ["sidebarMode"] = "epitaxy", ["epitaxyPrefs"] = prefs.DeepClone() } }.ToJsonString());
        JsonObject Prefs() => JsonNode.Parse(File.ReadAllText(config))!["preferences"]!["epitaxyPrefs"]!.AsObject();
        ClaudeSidebarPrefs.Outcome Sync(IReadOnlyDictionary<string, string>? moved = null, Func<bool>? closed = null) =>
            ClaudeSidebarPrefs.Sync(config, state, [link], moved ?? new Dictionary<string, string>(), closed ?? (() => true));
        List<string> Pinned() => Prefs()["dframe-local-slice"]!["pinnedOrder"]!.AsArray().Select(item => item!.GetValue<string>()).ToList();
        string? GroupOf(string account, string id)
        {
            var scope = Prefs()["dframe-group-scopes"]?[account];
            var groupId = scope?["assignments"]?[id]?.GetValue<string>();
            return scope?["groups"]!.AsArray().FirstOrDefault(group => group!["id"]!.GetValue<string>() == groupId)?["name"]?.GetValue<string>();
        }

        Write(new JsonObject
        {
            ["dframe-local-slice"] = new JsonObject { ["pinnedOrder"] = new JsonArray(otherA, idA), ["homeProjectsPinnedOrder"] = new JsonArray() },
            ["starred-local-code-sessions"] = new JsonArray(idA["code:".Length..]),
            ["dframe-group-scopes"] = new JsonObject
            {
                [A.Key] = new JsonObject
                {
                    ["groups"] = new JsonArray(new JsonObject { ["id"] = "cg-1", ["name"] = "대기" }),
                    ["assignments"] = new JsonObject { [idA] = "cg-1" }, ["order"] = new JsonObject { ["cg-1"] = new JsonArray(idA) }
                }
            },
            ["fastMode"] = true
        });
        var first = Sync();
        Check(first.Pinned == 1 && Pinned().SequenceEqual([otherA, idA, idB]) && GroupOf(B.Key, idB) == "대기" && first.Grouped == 1,
            "a copy is pinned next to its original and put in a group of the same name in its account");
        Check(Prefs()["fastMode"]!.GetValue<bool>() && Prefs()["starred-local-code-sessions"]!.AsArray().Count == 2
              && !File.ReadAllText(config).Contains('\r') && File.ReadAllText(config).Contains("\"대기\""),
            "other settings, the starred mirror, line endings and Korean text are preserved");
        Check(Sync() is { Pinned: 0, Unpinned: 0, Grouped: 0 }, "an aligned pair changes nothing");

        // The user unpins the copy and moves it out of the group while signed in to B: that change wins.
        var prefs = Prefs();
        prefs["dframe-local-slice"]!["pinnedOrder"]!.AsArray().RemoveAt(2);
        prefs["dframe-group-scopes"]![B.Key]!["assignments"]!.AsObject().Remove(idB);
        Write(prefs);
        var second = Sync();
        Check(second.Unpinned == 1 && second.Ungrouped == 1 && !Pinned().Contains(idA) && GroupOf(A.Key, idA) is null,
            "an unpin or ungroup made on either side reaches the other side");

        // A copy that was archived hands its pin and group to the newer record that replaced it.
        const string newer = "code:local_bbbbbbbb-1111-4000-8000-000000000002";
        prefs = Prefs();
        prefs["dframe-local-slice"]!["pinnedOrder"]!.AsArray().Add(idB);
        Write(prefs);
        var moved = ClaudeSidebarPrefs.Sync(config, state, [], new Dictionary<string, string> { [idB] = newer }, () => true);
        Check(moved.Moved == 1 && Pinned().Contains(newer) && !Pinned().Contains(idB), "a retired copy's pin moves to its successor");

        // Unexpected shapes and concurrent changes write nothing.
        File.WriteAllText(config, "{\"preferences\":{\"epitaxyPrefs\":{\"dframe-local-slice\":{\"pinnedOrder\":\"oops\"},\"dframe-group-scopes\":[]}}}");
        var before = File.ReadAllBytes(config);
        Check(Sync().Skipped == "shape" && File.ReadAllBytes(config).SequenceEqual(before), "an unfamiliar settings shape is left untouched");
        Write(new JsonObject { ["dframe-local-slice"] = new JsonObject { ["pinnedOrder"] = new JsonArray(idA) } });
        File.Delete(Path.Combine(state, "sidebar-mirror.json"));
        before = File.ReadAllBytes(config);
        Check(Sync(closed: () => false).Skipped == "desktop_running" && File.ReadAllBytes(config).SequenceEqual(before),
            "nothing is written once the app is running again");
    }

    private static void TestLineageEdges()
    {
        // A conversation restored after a clear lists its own current id as earlier; it is still copied.
        using var restored = new Fixture();
        var current = restored.Transcript(); var cleared = restored.Transcript();
        restored.Record(A, "local_e5e5e5e5-0000-4000-8000-000000000001", current,
            extra: r => r["priorCliSessionIds"] = new JsonArray(current, cleared));
        Check(restored.Shelf().Plan([A, B]).Actions.Count == 1, "a record's own earlier-id list never hides it");
        // Two records that list each other are ambiguous: neither is treated as an older state.
        restored.Record(A, "local_e5e5e5e5-0000-4000-8000-000000000002", cleared, extra: r => r["priorCliSessionIds"] = new JsonArray(current));
        Check(restored.Shelf().Plan([A, B]).Actions.Count == 2, "mutually listed records are both kept");

        // A copy the app changed under the same id may be a branch the user is working in: never archived.
        using var edited = new Fixture();
        var first = edited.Transcript(); var second = edited.Transcript();
        edited.Record(A, "local_f6f6f6f6-0000-4000-8000-000000000001", first);
        var shelf = edited.Shelf();
        shelf.Apply([A, B], () => true);
        var copy = edited.Files(B).Single();
        var record = JsonNode.Parse(File.ReadAllText(copy))!.AsObject();
        record["completedTurns"] = 9; record["lastActivityAt"] = 99;
        File.WriteAllText(copy, record.ToJsonString());
        edited.Record(A, "local_f6f6f6f6-0000-4000-8000-000000000002", second, extra: r => r["priorCliSessionIds"] = new JsonArray(first));
        var result = shelf.Apply([A, B], () => true);
        Check(result.Retired == 0 && !JsonNode.Parse(File.ReadAllText(copy))!["isArchived"]!.GetValue<bool>(),
            "a copy resumed under the same id is left for the user");

        // Two newer branches of the same conversation: which one replaces the copy is unclear, so nothing is archived.
        using var branches = new Fixture();
        var root = branches.Transcript(); var left = branches.Transcript(); var right = branches.Transcript();
        branches.Record(A, "local_a7a7a7a7-0000-4000-8000-000000000001", root);
        var forked = branches.Shelf();
        forked.Apply([A, B], () => true);
        File.Delete(branches.Files(A).Single());
        branches.Record(A, "local_a7a7a7a7-0000-4000-8000-000000000002", left, extra: r => r["priorCliSessionIds"] = new JsonArray(root));
        branches.Record(A, "local_a7a7a7a7-0000-4000-8000-000000000003", right, extra: r => r["priorCliSessionIds"] = new JsonArray(root));
        Check(forked.Apply([A, B], () => true).Retired == 0, "a copy with two newer branches stays");

        // The pin of an archived copy reaches its successor even when the first sidebar sync could not run.
        using var moving = new Fixture();
        moving.Config = Path.Combine(moving.Root, "claude_desktop_config.json");
        var (one, two) = Two(moving);
        moving.Record(A, "local_b8b8b8b8-0000-4000-8000-000000000001", one);
        var pinning = moving.Shelf();
        pinning.Apply([A, B], () => true);
        var stale = Path.GetFileNameWithoutExtension(moving.Files(B).Single());
        File.WriteAllText(moving.Config, new JsonObject { ["preferences"] = new JsonObject { ["epitaxyPrefs"] = new JsonObject
            { ["dframe-local-slice"] = new JsonObject { ["pinnedOrder"] = new JsonArray("code:" + stale) } } } }.ToJsonString());
        File.Delete(moving.Files(A).Single());
        moving.Record(B, "local_b8b8b8b8-0000-4000-8000-000000000002", two, extra: r => r["priorCliSessionIds"] = new JsonArray(one));
        // The app reopens after the copy is archived and before the settings are written (the fifth check is the archive's
        // last one, right before the copy is replaced).
        var checks = 0;
        MustThrow<ClaudeDesktopRunningException>(() => pinning.Apply([A, B], () => ++checks <= 5));
        Check(moving.Files(B).Any(path => path.Contains(stale)) && JsonNode.Parse(File.ReadAllText(moving.Files(B).First(path => path.Contains(stale))))!["isArchived"]!.GetValue<bool>(),
            "the copy was archived before the interruption");
        pinning.Apply([A, B], () => true);
        var pinned = JsonNode.Parse(File.ReadAllText(moving.Config))!["preferences"]!["epitaxyPrefs"]!["dframe-local-slice"]!["pinnedOrder"]!.AsArray()
            .Select(item => item!.GetValue<string>()).ToArray();
        var successor = Path.GetFileNameWithoutExtension(moving.Files(B).Single(path => !path.Contains(stale)));
        Check(pinned.Contains("code:" + successor) && !pinned.Contains("code:" + stale), "the pending move is finished on the next run");

        // An archive that did not happen is not counted as one: the intent is settled from the file on the next run.
        using var halted = new Fixture();
        halted.Config = Path.Combine(halted.Root, "claude_desktop_config.json");
        var (h1, h2) = Two(halted);
        halted.Record(A, "local_d0d0d0d0-0000-4000-8000-000000000001", h1);
        var haltedShelf = halted.Shelf();
        haltedShelf.Apply([A, B], () => true);
        var haltedCopy = halted.Files(B).Single();
        var haltedId = Path.GetFileNameWithoutExtension(haltedCopy);
        File.WriteAllText(halted.Config, new JsonObject { ["preferences"] = new JsonObject { ["epitaxyPrefs"] = new JsonObject
            { ["dframe-local-slice"] = new JsonObject { ["pinnedOrder"] = new JsonArray("code:" + haltedId) } } } }.ToJsonString());
        File.Delete(halted.Files(A).Single());
        halted.Record(B, "local_d0d0d0d0-0000-4000-8000-000000000002", h2, extra: r => r["priorCliSessionIds"] = new JsonArray(h1));
        // A run stopped after recording its intent and before archiving: simulate with the ledger alone.
        var ledgerPath = Path.Combine(halted.Ledger, "ledger.json");
        var haltedLedger = JsonNode.Parse(File.ReadAllText(ledgerPath))!.AsObject();
        haltedLedger["created"]![0]!["movedTo"] = "local_d0d0d0d0-0000-4000-8000-0000000000ff.json";
        File.WriteAllText(ledgerPath, haltedLedger.ToJsonString());
        var resumed = haltedShelf.Apply([A, B], () => true);
        var pinnedNow = JsonNode.Parse(File.ReadAllText(halted.Config))!["preferences"]!["epitaxyPrefs"]!["dframe-local-slice"]!["pinnedOrder"]!.AsArray()
            .Select(item => item!.GetValue<string>()).ToArray();
        var haltedSuccessor = Path.GetFileNameWithoutExtension(halted.Files(B).Single(path => path != haltedCopy));
        Check(resumed.Retired == 1 && JsonNode.Parse(File.ReadAllText(haltedCopy))!["isArchived"]!.GetValue<bool>()
              && pinnedNow.Contains("code:" + haltedSuccessor) && !pinnedNow.Contains("code:" + haltedId),
            "an unarchived copy with a stale intent is archived again and its pin then moves");

        // A sidebar section of an unfamiliar shape leaves the move pending until it can be read.
        using var odd = new Fixture();
        odd.Config = Path.Combine(odd.Root, "claude_desktop_config.json");
        var (o1, o2) = Two(odd);
        odd.Record(A, "local_e1e1e1e1-0000-4000-8000-000000000001", o1);
        var oddShelf = odd.Shelf();
        oddShelf.Apply([A, B], () => true);
        var oddCopy = Path.GetFileNameWithoutExtension(odd.Files(B).Single());
        File.Delete(odd.Files(A).Single());
        odd.Record(B, "local_e1e1e1e1-0000-4000-8000-000000000002", o2, extra: r => r["priorCliSessionIds"] = new JsonArray(o1));
        File.WriteAllText(odd.Config, "{\"preferences\":{\"epitaxyPrefs\":[]}}");
        var oddResult = oddShelf.Apply([A, B], () => true);
        Check(oddResult.Retired == 1 && oddResult.Prefs is { Complete: false }, "an unfamiliar sidebar section is not reported as done");
        File.WriteAllText(odd.Config, new JsonObject { ["preferences"] = new JsonObject { ["epitaxyPrefs"] = new JsonObject
            { ["dframe-local-slice"] = new JsonObject { ["pinnedOrder"] = new JsonArray("code:" + oddCopy) } } } }.ToJsonString());
        oddShelf.Apply([A, B], () => true);
        var oddPinned = JsonNode.Parse(File.ReadAllText(odd.Config))!["preferences"]!["epitaxyPrefs"]!["dframe-local-slice"]!["pinnedOrder"]!.AsArray()
            .Select(item => item!.GetValue<string>()).ToArray();
        var oddSuccessor = Path.GetFileNameWithoutExtension(odd.Files(B).Single(path => !path.Contains(oddCopy)));
        Check(!oddPinned.Contains("code:" + oddCopy) && oddPinned.Contains("code:" + oddSuccessor), "the pending move completes once the section is readable");

        // An archive that cannot be written is reported as unfinished and retried; it is never counted as done.
        using var stuck = new Fixture();
        var (s1, s2) = Two(stuck);
        stuck.Record(A, "local_f2f2f2f2-0000-4000-8000-000000000001", s1);
        var stuckShelf = stuck.Shelf();
        stuckShelf.Apply([A, B], () => true);
        var stuckCopy = stuck.Files(B).Single();
        File.Delete(stuck.Files(A).Single());
        stuck.Record(B, "local_f2f2f2f2-0000-4000-8000-000000000002", s2, extra: r => r["priorCliSessionIds"] = new JsonArray(s1));
        File.SetAttributes(stuckCopy, FileAttributes.ReadOnly);
        var failed = stuckShelf.Apply([A, B], () => true);
        File.SetAttributes(stuckCopy, FileAttributes.Normal);
        Check(failed.Retired == 0 && !failed.Plan.IsComplete && failed.Plan.Held[B.Key] == "archive_failed"
              && !JsonNode.Parse(File.ReadAllText(stuckCopy))!["isArchived"]!.GetValue<bool>(), "a failed archive leaves the run unfinished");
        Check(stuckShelf.Apply([A, B], () => true).Retired == 1, "the archive succeeds on the retry");

        // While the app runs, it comes to the list at the last moment before an archive: the copy stays as it is.
        using var late = new Fixture();
        var (t1, t2) = Two(late);
        late.Record(A, "local_f4f4f4f4-0000-4000-8000-000000000001", t1);
        var lateShelf = late.Shelf();
        lateShelf.Apply([A, B], () => true);
        var lateCopy = late.Files(B).Single();
        File.Delete(late.Files(A).Single());
        late.Record(B, "local_f4f4f4f4-0000-4000-8000-000000000002", t2, extra: r => r["priorCliSessionIds"] = new JsonArray(t1));
        var before = File.ReadAllBytes(lateCopy);
        // Asked once the archived copy is staged beside the record: the last check before it replaces it.
        bool Staged() => Directory.EnumerateFiles(late.ShelfPath(B), ".local_*").Any();
        var cut = lateShelf.Apply([A, B], target => target != A.Key && !(target == B.Key && Staged()), "test", running: true);
        Check(cut.Retired == 0 && cut.Plan.Held[B.Key] == "archive_failed" && File.ReadAllBytes(lateCopy).SequenceEqual(before),
            "an app that comes to the list at the last moment finds the copy as it was, and the run is retried");
        Check(lateShelf.Apply([A, B], () => true).Retired == 1, "the archive goes ahead once the list is left alone");

        // Archived, but the run stopped before the ledger said so; the next run cannot read the copy at first.
        using var locked = new Fixture();
        locked.Config = Path.Combine(locked.Root, "claude_desktop_config.json");
        var (l1, l2) = Two(locked);
        locked.Record(A, "local_f3f3f3f3-0000-4000-8000-000000000001", l1);
        var lockedShelf = locked.Shelf();
        lockedShelf.Apply([A, B], () => true);
        var lockedCopy = locked.Files(B).Single();
        var lockedId = Path.GetFileNameWithoutExtension(lockedCopy);
        File.Delete(locked.Files(A).Single());
        locked.Record(B, "local_f3f3f3f3-0000-4000-8000-000000000002", l2, extra: r => r["priorCliSessionIds"] = new JsonArray(l1));
        lockedShelf.Apply([A, B], () => true); // archives the copy (no sidebar file yet: nothing to move)
        var lockedSuccessor = Path.GetFileNameWithoutExtension(locked.Files(B).Single(path => path != lockedCopy));
        // Rewind the ledger to "intent recorded, archive written, not yet confirmed" with a pin still on the copy.
        var lockedLedgerPath = Path.Combine(locked.Ledger, "ledger.json");
        var lockedLedger = JsonNode.Parse(File.ReadAllText(lockedLedgerPath))!.AsObject();
        var lockedEntry = lockedLedger["created"]!.AsArray().First(entry => entry!["file"]!.GetValue<string>() == lockedId + ".json")!.AsObject();
        lockedEntry["retiredAt"] = null; lockedEntry["moveDone"] = null; lockedEntry["movedTo"] = lockedSuccessor + ".json";
        File.WriteAllText(lockedLedgerPath, lockedLedger.ToJsonString());
        File.WriteAllText(locked.Config, new JsonObject { ["preferences"] = new JsonObject { ["epitaxyPrefs"] = new JsonObject
            { ["dframe-local-slice"] = new JsonObject { ["pinnedOrder"] = new JsonArray("code:" + lockedId) } } } }.ToJsonString());
        using (new FileStream(lockedCopy, FileMode.Open, FileAccess.Read, FileShare.None))
            MustThrow<IOException>(() => lockedShelf.Apply([A, B], () => true));
        Check(JsonNode.Parse(File.ReadAllText(lockedLedgerPath))!["created"]!.AsArray()
                .Any(entry => entry!["file"]!.GetValue<string>() == lockedId + ".json" && entry["movedTo"] is not null),
            "an unreadable copy keeps its pending move");
        lockedShelf.Apply([A, B], () => true);
        var lockedPins = JsonNode.Parse(File.ReadAllText(locked.Config))!["preferences"]!["epitaxyPrefs"]!["dframe-local-slice"]!["pinnedOrder"]!.AsArray()
            .Select(item => item!.GetValue<string>()).ToArray();
        Check(lockedPins.Contains("code:" + lockedSuccessor) && !lockedPins.Contains("code:" + lockedId),
            "once readable, the archive is confirmed and the pin moves");

        // A settings file that exists but cannot be opened is not "no settings": the move stays pending.
        using var busy = new Fixture();
        busy.Config = Path.Combine(busy.Root, "claude_desktop_config.json");
        var (b1, b2) = Two(busy);
        busy.Record(A, "local_f4f4f4f4-0000-4000-8000-000000000001", b1);
        var busyShelf = busy.Shelf();
        busyShelf.Apply([A, B], () => true);
        var busyCopy = Path.GetFileNameWithoutExtension(busy.Files(B).Single());
        File.WriteAllText(busy.Config, new JsonObject { ["preferences"] = new JsonObject { ["epitaxyPrefs"] = new JsonObject
            { ["dframe-local-slice"] = new JsonObject { ["pinnedOrder"] = new JsonArray("code:" + busyCopy) } } } }.ToJsonString());
        File.Delete(busy.Files(A).Single());
        busy.Record(B, "local_f4f4f4f4-0000-4000-8000-000000000002", b2, extra: r => r["priorCliSessionIds"] = new JsonArray(b1));
        ClaudeShelfResult busyResult;
        using (new FileStream(busy.Config, FileMode.Open, FileAccess.Read, FileShare.None)) busyResult = busyShelf.Apply([A, B], () => true);
        Check(busyResult.Retired == 1 && busyResult.Prefs is { Complete: false, Skipped: "unreadable" }, "an unopenable settings file leaves the move pending");
        busyShelf.Apply([A, B], () => true);
        var busySuccessor = Path.GetFileNameWithoutExtension(busy.Files(B).Single(path => !path.Contains(busyCopy)));
        var busyPins = JsonNode.Parse(File.ReadAllText(busy.Config))!["preferences"]!["epitaxyPrefs"]!["dframe-local-slice"]!["pinnedOrder"]!.AsArray()
            .Select(item => item!.GetValue<string>()).ToArray();
        Check(busyPins.Contains("code:" + busySuccessor) && !busyPins.Contains("code:" + busyCopy), "the move completes once the file opens");
        Check(ClaudeSidebarPrefs.Sync(Path.Combine(busy.Root, "missing", "c.json"), busy.Root, [], new Dictionary<string, string>(), () => true)
            is { Skipped: "no_config", Complete: true }, "only a confirmed missing file counts as no settings");

        // Both sides changed a group: the original's choice wins, including leaving the group.
        using var conflict = new Fixture();
        var config = Path.Combine(conflict.Root, "c.json"); var state = Path.Combine(conflict.Root, "s");
        const string a = "code:local_c9c9c9c9-0000-4000-8000-000000000001", b = "code:local_c9c9c9c9-0000-4000-8000-000000000002";
        var link = new ClaudeSidebarPrefs.Link(A.Key, a, B.Key, b, "k");
        JsonObject Scope(string id, string? group) => group is null ? new JsonObject()
            : new JsonObject { ["groups"] = new JsonArray(new JsonObject { ["id"] = "g-" + group, ["name"] = group }),
                ["assignments"] = new JsonObject { [id] = "g-" + group }, ["order"] = new JsonObject { ["g-" + group] = new JsonArray(id) } };
        void Write(string? groupA, string? groupB) => File.WriteAllText(config, new JsonObject { ["preferences"] = new JsonObject { ["epitaxyPrefs"] =
            new JsonObject { ["dframe-group-scopes"] = new JsonObject { [A.Key] = Scope(a, groupA), [B.Key] = Scope(b, groupB) } } } }.ToJsonString());
        Write("G0", "G0");
        ClaudeSidebarPrefs.Sync(config, state, [link], new Dictionary<string, string>(), () => true);
        Write(null, "G1");
        ClaudeSidebarPrefs.Sync(config, state, [link], new Dictionary<string, string>(), () => true);
        var scopes = JsonNode.Parse(File.ReadAllText(config))!["preferences"]!["epitaxyPrefs"]!["dframe-group-scopes"]!;
        Check(scopes[A.Key]!["assignments"]?[a] is null && scopes[B.Key]!["assignments"]?[b] is null,
            "when both sides changed, the original leaving its group wins");
    }

    private static void TestLocalStorage()
    {
        using var fixture = new Fixture();
        var db = Directory.CreateDirectory(Path.Combine(fixture.Root, "leveldb")).FullName;
        static byte[] Varint(ulong value) { var bytes = new List<byte>(); while (value >= 0x80) { bytes.Add((byte)(value | 0x80)); value >>= 7; } bytes.Add((byte)value); return bytes.ToArray(); }
        // A manifest naming log 5 and last sequence 41, and a log whose filler pushes the next record across a block.
        var edit = new List<byte>();
        edit.AddRange(Varint(2)); edit.AddRange(Varint(5)); edit.AddRange(Varint(3)); edit.AddRange(Varint(7));
        edit.AddRange(Varint(4)); edit.AddRange(Varint(41));
        File.WriteAllBytes(Path.Combine(db, "MANIFEST-000003"), ClaudeLocalStorage.Frame(edit.ToArray(), 0));
        File.WriteAllText(Path.Combine(db, "CURRENT"), "MANIFEST-000003\n");
        File.WriteAllText(Path.Combine(db, "LOCK"), "");
        byte[] Batch(ulong sequence, params (string Key, string Value)[] puts)
        {
            var bytes = new List<byte>(); bytes.AddRange(BitConverter.GetBytes(sequence)); bytes.AddRange(BitConverter.GetBytes(puts.Length));
            foreach (var (key, value) in puts)
            {
                var k = Encoding.UTF8.GetBytes("_https://claude.ai").Concat(new byte[] { 0 }).Concat(ClaudeLocalStorage.EncodeValue(key)).ToArray();
                var v = ClaudeLocalStorage.EncodeValue(value);
                bytes.Add(1); bytes.AddRange(Varint((ulong)k.Length)); bytes.AddRange(k); bytes.AddRange(Varint((ulong)v.Length)); bytes.AddRange(v);
            }
            return bytes.ToArray();
        }
        var version = new List<byte>(); version.AddRange(BitConverter.GetBytes(41UL)); version.AddRange(BitConverter.GetBytes(1));
        version.Add(1); version.AddRange(Varint(7)); version.AddRange(Encoding.ASCII.GetBytes("VERSION")); version.AddRange(Varint(1)); version.Add((byte)'1');
        var filler = Batch(42, ("LSS-persisted.other", new string('x', 32700)));
        var versionFrame = ClaudeLocalStorage.Frame(version.ToArray(), 0);
        var log = versionFrame.Concat(ClaudeLocalStorage.Frame(filler, versionFrame.Length)).ToArray();
        File.WriteAllBytes(Path.Combine(db, "000005.log"), log);
        var store = new ClaudeLocalStorage(db);
        var keys = new[] { "LSS-persisted.dframe-local-slice", "LSS-persisted.dframe-group-scopes" };
        var snapshot = store.Read(keys);
        Check(snapshot.LastSequence == 42 && snapshot.Values.All(pair => pair.Value is null), "the fixture database reads back");
        var values = new Dictionary<string, string> { [keys[0]] = "{\"value\":{\"pinnedOrder\":[]}}", [keys[1]] = "{\"value\":{\"그룹\":1}}" };
        var backups = Path.Combine(fixture.Root, "backups");
        Check(store.Write(snapshot, values, backups, () => true), "a batch is appended");
        var after = store.Read(keys);
        Check(after.LastSequence == 44 && after.Values[keys[0]] == values[keys[0]] && after.Values[keys[1]] == values[keys[1]]
              && ClaudeLocalStorage.ReadLog(File.ReadAllBytes(Path.Combine(db, "000005.log")), strict: true).Count() == 3,
            "the appended batch spans a block boundary, passes its checksums and is the newest value");
        Check(Directory.EnumerateDirectories(ClaudeLocalStorage.BackupsFolder(backups)).Count() == 1, "the directory was backed up first");
        Check(!store.Write(snapshot, values, backups, () => true), "a stale snapshot writes nothing");
        using (new FileStream(Path.Combine(db, "LOCK"), FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(!store.Write(after, values, backups, () => true), "a database in use writes nothing");
        Check(!store.Write(after, values, backups, () => false), "nothing is written once the app runs");
        // A failure after the append cuts the log back; no other file is touched.
        var logPath = Path.Combine(db, "000005.log");
        var bytesBefore = File.ReadAllBytes(logPath);
        var manifestBefore = File.ReadAllBytes(Path.Combine(db, "MANIFEST-000003"));
        store.AfterAppend = () => throw new IOException("injected");
        MustThrow<IOException>(() => store.Write(after, values, backups, () => true));
        store.AfterAppend = null;
        Check(File.ReadAllBytes(logPath).SequenceEqual(bytesBefore) && File.ReadAllBytes(Path.Combine(db, "MANIFEST-000003")).SequenceEqual(manifestBefore),
            "a failed write leaves the database exactly as it was");
        // A log ending inside a record is not written to.
        File.WriteAllBytes(logPath, bytesBefore.Concat(ClaudeLocalStorage.Frame(new byte[40000], bytesBefore.Length).Take(100)).ToArray());
        Check(!store.Read(keys).Clean && !store.Write(store.Read(keys), values, backups, () => true), "a torn log is not written to");
        File.WriteAllBytes(logPath, bytesBefore);
        // Corruption and an incomplete manifest are refused, as LevelDB refuses them.
        var corrupt = (byte[])bytesBefore.Clone(); corrupt[20] ^= 0xff;
        File.WriteAllBytes(logPath, corrupt);
        MustThrow<InvalidDataException>(() => store.Read(keys));
        File.WriteAllBytes(logPath, bytesBefore);
        var noNext = new List<byte>(); noNext.AddRange(Varint(2)); noNext.AddRange(Varint(5)); noNext.AddRange(Varint(4)); noNext.AddRange(Varint(41));
        File.WriteAllBytes(Path.Combine(db, "MANIFEST-000003"), ClaudeLocalStorage.Frame(noNext.ToArray(), 0));
        MustThrow<InvalidDataException>(() => store.Read(keys));
        var badManifest = (byte[])manifestBefore.Clone(); badManifest[8] ^= 0xff;
        File.WriteAllBytes(Path.Combine(db, "MANIFEST-000003"), badManifest);
        MustThrow<InvalidDataException>(() => store.Read(keys));
    }

    private static void TestTitlesFollowOriginals()
    {
        using var fixture = new Fixture();
        var cli = fixture.Transcript();
        fixture.Record(A, "local_b1b1b1b1-0000-4000-8000-000000000001", cli);
        var original = Path.Combine(fixture.ShelfPath(A), "local_b1b1b1b1-0000-4000-8000-000000000001.json");
        var shelf = fixture.Shelf();
        shelf.Apply([A, B], () => true);
        var copy = fixture.Files(B).Single();
        void Set(string path, Action<JsonObject> change)
        {
            var record = JsonNode.Parse(File.ReadAllText(path))!.AsObject(); change(record); File.WriteAllText(path, record.ToJsonString());
        }
        string Title(string path) => JsonNode.Parse(File.ReadAllText(path))!["title"]!.GetValue<string>();

        Set(original, r => { r["title"] = "새 이름"; r["titleSource"] = "user"; });
        Check(shelf.Apply([A, B], () => true).Retitled == 1 && Title(copy) == "새 이름", "a renamed original renames its copy");
        Check(shelf.Undo(() => true).Removed == 1, "a retitled copy is still recognised as written by this utility");

        // A copy renamed in its own account keeps that name.
        shelf.Apply([A, B], () => true);
        copy = fixture.Files(B).Single();
        Set(copy, r => r["title"] = "서브에서 바꾼 이름");
        Set(original, r => r["title"] = "또 바꾼 이름");
        Check(shelf.Apply([A, B], () => true).Retitled == 0 && Title(copy) == "서브에서 바꾼 이름", "a rename in the copy's account is respected");

        // A copy used in its own account more recently than the original, or continued under a new id, is left alone.
        using var used = new Fixture();
        var usedCli = used.Transcript();
        used.Record(A, "local_b2b2b2b2-0000-4000-8000-000000000001", usedCli);
        var usedOriginal = Path.Combine(used.ShelfPath(A), "local_b2b2b2b2-0000-4000-8000-000000000001.json");
        var usedShelf = used.Shelf();
        usedShelf.Apply([A, B], () => true);
        var usedCopy = used.Files(B).Single();
        Set(usedCopy, r => r["lastActivityAt"] = 999);
        Set(usedOriginal, r => r["title"] = "원본 새 이름");
        Check(usedShelf.Apply([A, B], () => true).Retitled == 0 && Title(usedCopy) != "원본 새 이름", "a copy used in its own account keeps its name");
        Set(usedCopy, r => { r["lastActivityAt"] = 1; r["cliSessionId"] = used.Transcript(); });
        Check(usedShelf.Apply([A, B], () => true).Retitled == 0, "a copy continued under a new id keeps its name");

        // An older ledger entry has no recorded title: only a copy still exactly as written is renamed.
        using var legacy = new Fixture();
        var legacyCli = legacy.Transcript();
        legacy.Record(A, "local_b3b3b3b3-0000-4000-8000-000000000001", legacyCli);
        var legacyOriginal = Path.Combine(legacy.ShelfPath(A), "local_b3b3b3b3-0000-4000-8000-000000000001.json");
        var legacyShelf = legacy.Shelf();
        legacyShelf.Apply([A, B], () => true);
        var legacyCopy = legacy.Files(B).Single();
        var ledgerPath = Path.Combine(legacy.Ledger, "ledger.json");
        void ForgetTitles() { var l = JsonNode.Parse(File.ReadAllText(ledgerPath))!.AsObject(); foreach (var e in l["created"]!.AsArray()) e!.AsObject().Remove("title"); File.WriteAllText(ledgerPath, l.ToJsonString()); }
        ForgetTitles();
        Set(legacyCopy, r => r["title"] = "서브에서 바꾼 옛 이름");
        Set(legacyOriginal, r => r["title"] = "원본 이름");
        Check(legacyShelf.Apply([A, B], () => true).Retitled == 0 && Title(legacyCopy) == "서브에서 바꾼 옛 이름",
            "a changed copy without a recorded title keeps its name");

        // A changed older copy starts tracking once it shows the original's title, then follows it.
        using var track = new Fixture();
        track.Record(A, "local_b5b5b5b5-0000-4000-8000-000000000001", track.Transcript(), extra: r => r["title"] = "처음 이름");
        var trackOriginal = Path.Combine(track.ShelfPath(A), "local_b5b5b5b5-0000-4000-8000-000000000001.json");
        var trackShelf = track.Shelf();
        trackShelf.Apply([A, B], () => true);
        var trackCopy = track.Files(B).Single();
        var trackLedger = Path.Combine(track.Ledger, "ledger.json");
        { var l = JsonNode.Parse(File.ReadAllText(trackLedger))!.AsObject(); foreach (var e in l["created"]!.AsArray()) e!.AsObject().Remove("title"); File.WriteAllText(trackLedger, l.ToJsonString()); }
        Set(trackCopy, r => r["lastFocusedAt"] = 5);
        Check(trackShelf.Apply([A, B], () => true).Retitled == 0
            && JsonNode.Parse(File.ReadAllText(trackLedger))!["created"]![0]!["title"]!.GetValue<string>() == "처음 이름",
            "a changed older copy showing the original's title starts tracking");
        Set(trackOriginal, r => { r["title"] = "원본에서 바꾼 이름"; r["previousTitles"] = new JsonArray("처음 이름"); });
        Check(trackShelf.Apply([A, B], () => true).Retitled == 1 && Title(trackCopy) == "원본에서 바꾼 이름", "a tracked older copy follows");
        Check(trackShelf.Undo(() => true).Removed == 0 && File.Exists(trackCopy), "a copy the app changed is kept by undo");

        // The app records no history for a first name or a change of case, so history cannot prove a changed copy kept its name.
        using var unproven = new Fixture();
        unproven.Record(A, "local_b6b6b6b6-0000-4000-8000-000000000001", unproven.Transcript(), extra: r => r.Remove("title"));
        var unprovenOriginal = Path.Combine(unproven.ShelfPath(A), "local_b6b6b6b6-0000-4000-8000-000000000001.json");
        var unprovenShelf = unproven.Shelf();
        unprovenShelf.Apply([A, B], () => true);
        var unprovenCopy = unproven.Files(B).Single();
        var unprovenLedger = Path.Combine(unproven.Ledger, "ledger.json");
        { var l = JsonNode.Parse(File.ReadAllText(unprovenLedger))!.AsObject(); foreach (var e in l["created"]!.AsArray()) e!.AsObject().Remove("title"); File.WriteAllText(unprovenLedger, l.ToJsonString()); }
        Set(unprovenCopy, r => { r["title"] = "검산"; r["titleSource"] = "user"; });
        Set(unprovenOriginal, r => { r["title"] = "검산 최종"; r["titleSource"] = "user"; r["previousTitles"] = new JsonArray("검산"); });
        Check(unprovenShelf.Apply([A, B], () => true).Retitled == 0 && Title(unprovenCopy) == "검산",
            "a name first given in the copy's account is kept even if the original once had it");

        using var clean = new Fixture();
        var cleanCli = clean.Transcript();
        clean.Record(A, "local_b4b4b4b4-0000-4000-8000-000000000001", cleanCli);
        var cleanOriginal = Path.Combine(clean.ShelfPath(A), "local_b4b4b4b4-0000-4000-8000-000000000001.json");
        var cleanShelf = clean.Shelf();
        cleanShelf.Apply([A, B], () => true);
        var cleanCopy = clean.Files(B).Single();
        var cleanLedger = Path.Combine(clean.Ledger, "ledger.json");
        { var l = JsonNode.Parse(File.ReadAllText(cleanLedger))!.AsObject(); foreach (var e in l["created"]!.AsArray()) e!.AsObject().Remove("title"); File.WriteAllText(cleanLedger, l.ToJsonString()); }
        Set(cleanOriginal, r => r["title"] = "원본 새 이름");
        // Held lists are left alone.
        File.WriteAllText(Path.Combine(clean.ShelfPath(B), "local_cccccccc-0000-4000-8000-000000000009.json.tmp"), "{}");
        Check(cleanShelf.Apply([A, B], () => true).Retitled == 0, "a list the app is recovering is not retitled");
        File.Delete(Path.Combine(clean.ShelfPath(B), "local_cccccccc-0000-4000-8000-000000000009.json.tmp"));
        var unknownWriters = clean.Shelf(() => new ClaudeLiveSessionScan(new HashSet<string>(), true));
        Check(unknownWriters.Apply([A, B], () => true).Retitled == 0, "unknown writers hold retitling");
        ClaudeShelfResult blocked;
        using (new FileStream(cleanCopy, FileMode.Open, FileAccess.Read, FileShare.Read)) blocked = cleanShelf.Apply([A, B], () => true);
        Check(blocked.Retitled == 0 && !blocked.Plan.IsComplete && blocked.Plan.Held[B.Key] == "retitle_failed" && Title(cleanCopy) != "원본 새 이름",
            "a rename that cannot be written leaves the run unfinished");
        Check(cleanShelf.Apply([A, B], () => true).Retitled == 1 && Title(cleanCopy) == "원본 새 이름", "an untouched copy without a recorded title is renamed");
        // The conversation being written is no reason to wait: the copy in another list is not what is written.
        var writing = clean.Shelf(() => new ClaudeLiveSessionScan(new HashSet<string> { cleanCli.ToLowerInvariant() }, false));
        Set(cleanOriginal, r => r["title"] = "쓰는 중에 바꾼 이름");
        Check(writing.Apply([A, B], () => true).Retitled == 1 && Title(cleanCopy) == "쓰는 중에 바꾼 이름",
            "a conversation being written is retitled all the same");
        // The ledger owns exactly the bytes written; a later change by the app keeps the copy out of undo.
        Set(cleanCopy, r => r["completedTurns"] = 7);
        Check(cleanShelf.Undo(() => true).Removed == 0 && File.Exists(cleanCopy), "a copy changed after retitling is kept by undo");
    }

    private static readonly ClaudeShelfAccount Third = new("c3c3c3c3-0000-4000-8000-00000000000c", "c4c4c4c4-0000-4000-8000-00000000000c", "Third");

    private static void TestAdvancingOlderStates()
    {
        // B's own record of a conversation A went on with: that entry takes the newest state in place.
        using var fixture = new Fixture();
        var ids = Conversation(fixture, 2);
        var edge = Edge(ids[0], ids[1]);
        const string Older = "local_ad000000-0000-4000-8000-000000000001", Newer = "local_ad000000-0000-4000-8000-000000000002";
        fixture.Record(B, Older, ids[0], extra: r =>
        {
            r["title"] = "서브 이름"; r["titleSource"] = "user"; r["previousTitles"] = new JsonArray("옛 이름"); r["isStarred"] = true;
            r["permissionMode"] = "bypassPermissions"; r["model"] = "claude-opus-5-5"; r["effort"] = "xhigh";
            r["sessionSettings"] = new JsonObject { ["ultracode"] = true }; r["enabledMcpTools"] = new JsonObject { ["tool"] = true };
            r["lastFocusedAt"] = 50; r["completedTurns"] = 3; r["lastActivityAt"] = 100;
            // Its last turn, and the error it ended on: a turn the conversation went past.
            r["lastAssistantUuid"] = Guid.NewGuid().ToString("D"); r["postTurnSummary"] = new JsonObject { ["status_category"] = "completed" };
            r["turnWrapUp"] = new JsonObject { ["description"] = "끝" }; r["error"] = "You've hit your weekly limit"; r["errorAt"] = 90;
            r["titleTurn"] = 3; r["interruptedAwaitingPerson"] = null; r["promptAppendSnapshot"] = new JsonObject { ["append"] = "x" };
            r["bridgeSessionIds"] = new JsonArray("session_x");
        });
        fixture.Record(A, Newer, ids[1], extra: r =>
        {
            r["title"] = "메인 이름"; r["createdAt"] = 22; r["priorCliSessionIds"] = new JsonArray(ids[0]);
            r["rewindEdges"] = new JsonArray(edge.DeepClone());
            r["transcriptModelStates"] = new JsonObject { [ids[0]] = new JsonObject { ["model"] = "m" }, [ids[1]] = new JsonObject { ["model"] = "m" } };
            r["completedTurns"] = 9; r["lastActivityAt"] = 200; r["model"] = "claude-fable"; r["permissionMode"] = "default";
        });
        var olderPath = Path.Combine(fixture.ShelfPath(B), Older + ".json");
        var originalHash = ClaudeSessionShelf.Hash(olderPath);
        var original = File.ReadAllBytes(olderPath);
        var shelf = fixture.Shelf();
        var plan = shelf.Plan([A, B]);
        Check(plan.Advances.SingleOrDefault() is { File: Older + ".json" } advance && advance.CliSessionId == ids[1]
              && plan.Actions.Count == 0 && plan.PendingFor(B.Key) == 1,
            "an older state another account went on from is planned to advance, not to get the newest state beside it");
        var result = shelf.Apply([A, B], () => true, "test");
        var record = Json(olderPath);
        Check(result is { Advanced: 1, Created: 0 } && fixture.Files(B).Count == 1, "the list keeps one entry");
        Check(record["cliSessionId"]!.GetValue<string>() == ids[1] && record["priorCliSessionIds"]!.AsArray().Single()!.GetValue<string>() == ids[0]
              && JsonNode.DeepEquals(record["rewindEdges"], new JsonArray(edge.DeepClone())) && record["transcriptModelStates"] is JsonObject { Count: 2 }
              && record["completedTurns"]!.GetValue<int>() == 9 && record["lastActivityAt"]!.GetValue<int>() == 200,
            "the conversation, its lineage and progress come from the newest state");
        Check(record["sessionId"]!.GetValue<string>() == Older && record["title"]!.GetValue<string>() == "서브 이름"
              && record["titleSource"]!.GetValue<string>() == "user" && record["previousTitles"] is JsonArray { Count: 1 }
              && record["isStarred"]!.GetValue<bool>() && record["permissionMode"]!.GetValue<string>() == "bypassPermissions"
              && record["model"]!.GetValue<string>() == "claude-opus-5-5" && record["effort"]!.GetValue<string>() == "xhigh"
              && record["sessionSettings"]!["ultracode"]!.GetValue<bool>() && record["enabledMcpTools"] is JsonObject { Count: 1 }
              && record["lastFocusedAt"]!.GetValue<int>() == 50 && record["createdAt"]!.GetValue<int>() == 1,
            "its id, name, pin and this account's settings stay");
        Check(new[] { "lastAssistantUuid", "postTurnSummary", "turnWrapUp", "error", "errorAt", "titleTurn", "interruptedAwaitingPerson",
                  "promptAppendSnapshot", "bridgeSessionIds" }.All(field => !record.ContainsKey(field)),
            "what described the older state's transcript and last turn goes");
        var ledger = Json(Path.Combine(fixture.Ledger, "ledger.json"));
        var entry = ledger["advanced"]!.AsArray().Single()!;
        Check(entry["dst"]!.GetValue<string>() == B.Key && entry["from"]!.GetValue<string>() == ids[0] && entry["to"]!.GetValue<string>() == ids[1]
              && entry["state"]!.GetValue<string>() == "done" && entry["before"]!.GetValue<string>() == originalHash
              && entry["after"]!.GetValue<string>() == ClaudeSessionShelf.Hash(olderPath)
              && File.ReadAllBytes(Path.Combine(fixture.Ledger, ClaudeShelfLedger.BackupFolder, entry["backup"]!.GetValue<string>())).SequenceEqual(original)
              && ledger["created"]!.AsArray().Count == 0 && ledger["runs"]!.AsArray()[^1]!["advanced"]!.GetValue<int>() == 1,
            "the ledger keeps the advance and the record as it was, and takes no ownership of the app's record");
        Check(fixture.Hashes(A).SequenceEqual(fixture.OriginalHashes), "the newest state's own record is untouched");
        Check(shelf.Apply([A, B], () => true) is { Advanced: 0, Created: 0 }, "an advanced entry holds the conversation");
        File.Delete(olderPath);
        Check(shelf.Apply([A, B], () => true) is { Advanced: 0, Created: 0 }, "an advanced entry the user removed does not come back");

        // A copy this utility made, then used in its account, is advanced as well, and stays the user's from then on.
        using var copies = new Fixture();
        var copyIds = Conversation(copies, 2);
        copies.Record(A, "local_ad100000-0000-4000-8000-000000000001", copyIds[0]);
        var source = Path.Combine(copies.ShelfPath(A), "local_ad100000-0000-4000-8000-000000000001.json");
        var copyShelf = copies.Shelf();
        copyShelf.Apply([A, B], () => true);
        var copy = copies.Files(B).Single();
        Change(copy, r => r["lastFocusedAt"] = 7);
        // The app moved the original to the next transcript as A went on.
        Change(source, r => { r["cliSessionId"] = copyIds[1]; r["priorCliSessionIds"] = new JsonArray(copyIds[0]); r["lastActivityAt"] = 300; });
        var written = Json(Path.Combine(copies.Ledger, "ledger.json"))["created"]!.AsArray().Single()!.DeepClone();
        var advancedCopy = copyShelf.Apply([A, B], () => true);
        Check(advancedCopy is { Advanced: 1, Created: 0, Retired: 0 } && copies.Files(B).Single() == copy
              && Json(copy)["cliSessionId"]!.GetValue<string>() == copyIds[1] && Json(copy)["lastFocusedAt"]!.GetValue<int>() == 7,
            "the copy itself takes the newest state");
        var kept = Json(Path.Combine(copies.Ledger, "ledger.json"))["created"]!.AsArray().Single()!;
        Check(kept["cliSessionId"]!.GetValue<string>() == written["cliSessionId"]!.GetValue<string>()
              && kept["sha256"]!.GetValue<string>() == written["sha256"]!.GetValue<string>(),
            "the ledger still names the copy as it was written: an advanced copy is never made the utility's again");
        Change(source, r => r["title"] = "원본 새 이름");
        Check(copyShelf.Apply([A, B], () => true).Retitled == 0 && Json(copy)["title"]!.GetValue<string>() != "원본 새 이름"
              && copyShelf.Undo(() => true).Removed == 0 && File.Exists(copy) && !Json(copy)["isArchived"]!.GetValue<bool>(),
            "nor retitled, archived or undone by it: the session plan keeps its name in step");

        // Three accounts on one line: r in A, then s in B, then t in the third. Each older state takes t once, whatever
        // order the accounts come in.
        foreach (var order in new ClaudeShelfAccount[][] { [A, B, Third], [Third, B, A] })
        {
            using var line = new Fixture();
            var states = Conversation(line, 3);
            var rs = Edge(states[0], states[1]); var st = Edge(states[1], states[2]);
            line.Record(A, "local_ad200000-0000-4000-8000-000000000001", states[0]);
            line.Record(B, "local_ad200000-0000-4000-8000-000000000002", states[1], extra: r =>
            { r["priorCliSessionIds"] = new JsonArray(states[0]); r["rewindEdges"] = new JsonArray(rs.DeepClone()); });
            line.Record(Third, "local_ad200000-0000-4000-8000-000000000003", states[2], extra: r =>
            { r["priorCliSessionIds"] = new JsonArray(states[0], states[1]); r["rewindEdges"] = new JsonArray(rs.DeepClone(), st.DeepClone()); });
            var linePlan = line.Shelf().Plan(order);
            Check(linePlan.Advances.Count == 2 && linePlan.Advances.All(planned => planned.CliSessionId == states[2])
                  && linePlan.Advances.Select(planned => planned.Target).Order().SequenceEqual(new[] { A.Key, B.Key }.Order())
                  && linePlan.Actions.Count == 0, "each older state is planned once, to the newest state");
            Check(line.Shelf().Apply(order, () => true) is { Advanced: 2, Created: 0 }
                  && new[] { A, B, Third }.All(account => Json(line.Files(account).Single())["cliSessionId"]!.GetValue<string>() == states[2]),
                "every account lists the conversation once, at its newest state");
        }

        // Two accounts went on from the same state in different ways: both branches get an entry, the state they share stays.
        using var fork = new Fixture();
        var root = Conversation(fork, 1)[0];
        var left = GoOn(fork, root, DateTime.UtcNow.Date.AddDays(-15));
        var right = GoOn(fork, root, DateTime.UtcNow.Date.AddDays(-14));
        fork.Record(B, "local_ad300000-0000-4000-8000-000000000001", root);
        fork.Record(A, "local_ad300000-0000-4000-8000-000000000002", left, extra: r => r["priorCliSessionIds"] = new JsonArray(root));
        fork.Record(Third, "local_ad300000-0000-4000-8000-000000000003", right, extra: r => r["priorCliSessionIds"] = new JsonArray(root));
        var rootPath = Path.Combine(fork.ShelfPath(B), "local_ad300000-0000-4000-8000-000000000001.json");
        var rootHash = ClaudeSessionShelf.Hash(rootPath);
        Check(fork.Shelf().Apply([A, B, Third], () => true).Advanced == 0 && ClaudeSessionShelf.Hash(rootPath) == rootHash
              && fork.Files(B).Count == 3, "two branches are kept apart and the state they went on from stays as it was");

        // A list holding two older states of the conversation: the latest takes the newest state; the earlier one is left
        // to the session plan's archiving.
        using var two = new Fixture();
        var steps = Conversation(two, 3);
        two.Record(B, "local_ad400000-0000-4000-8000-000000000001", steps[0]);
        two.Record(B, "local_ad400000-0000-4000-8000-000000000002", steps[1], extra: r => r["priorCliSessionIds"] = new JsonArray(steps[0]));
        two.Record(A, "local_ad400000-0000-4000-8000-000000000003", steps[2], extra: r => r["priorCliSessionIds"] = new JsonArray(steps[0], steps[1]));
        var earliest = Path.Combine(two.ShelfPath(B), "local_ad400000-0000-4000-8000-000000000001.json");
        var earliestHash = ClaudeSessionShelf.Hash(earliest);
        Check(two.Shelf().Apply([A, B], () => true) is { Advanced: 1, Created: 0 } && ClaudeSessionShelf.Hash(earliest) == earliestHash
              && Json(Path.Combine(two.ShelfPath(B), "local_ad400000-0000-4000-8000-000000000002.json"))["cliSessionId"]!.GetValue<string>() == steps[2],
            "only the latest older state takes the newest state");
    }

    private static void TestAdvanceHoldsAndRecovery()
    {
        const string Mine = "local_ad500000-0000-4000-8000-000000000001", Theirs = "local_ad500000-0000-4000-8000-000000000002";
        // An older state that must not be rewritten: it stays as it was and the newest state is added beside it, as before.
        // (Where the transcripts do not show the newer state continuing it, it also goes to the newer state's account.)
        static void Kept(Fixture fixture, string because)
        {
            var path = Path.Combine(fixture.ShelfPath(B), Mine + ".json");
            var hash = ClaudeSessionShelf.Hash(path);
            Check(fixture.Shelf().Apply([A, B], () => true).Advanced == 0 && ClaudeSessionShelf.Hash(path) == hash
                  && fixture.Files(B).Count == 2, because);
        }
        // Imports with a staged transcript (the app moves it over the current transcript id when the import is confirmed),
        // other transcript ids, sessions of a kind of their own, and pending or interrupted work.
        foreach (var (field, value) in new (string, JsonNode)[]
                 {
                     ("stagedTranscriptPath", @"C:\staged\import.jsonl"), ("importedFrom", "code1p"), ("interruptedByQuitAt", 5),
                     ("interruptedAwaitingPerson", true), ("unarchivedCliSessionId", Guid.NewGuid().ToString("D")),
                     ("pendingSystemReminder", "remind"), ("spawnedFrom", new JsonObject { ["sessionId"] = "local_parent" }),
                     // A worktree the app made for this account's session: an advance would lose it.
                     ("worktreePath", @"C:\wt\feature"), ("worktreeLazy", new JsonObject { ["baseBranch"] = "main" })
                 })
        {
            using var held = new Fixture();
            var ids = Conversation(held, 2);
            held.Record(B, Mine, ids[0], extra: r => r[field] = value.DeepClone());
            held.Record(A, Theirs, ids[1], extra: r => r["priorCliSessionIds"] = new JsonArray(ids[0]));
            Kept(held, $"a record holding {field} is left alone");
        }
        // Not one line: written to after the conversation went on elsewhere, a lineage the newer one does not start with
        // or one listing its own transcript, rewind edges out of order or reaching outside the lineage, a cut the newer
        // state lacks, another folder, or archived.
        using (var branch = new Fixture())
        {
            var ids = Conversation(branch, 2);
            branch.Record(B, Mine, ids[0]);
            branch.Record(A, Theirs, ids[1], extra: r => r["priorCliSessionIds"] = new JsonArray(ids[0]));
            WriteLater(branch, ids[0]);
            Kept(branch, "an older state written to after the conversation went on is a branch and stays");
        }
        using (var other = new Fixture())
        {
            var ids = Conversation(other, 2);
            other.Record(B, Mine, ids[0], extra: r => r["priorCliSessionIds"] = new JsonArray(other.Transcript()));
            other.Record(A, Theirs, ids[1], extra: r => r["priorCliSessionIds"] = new JsonArray(ids[0]));
            Kept(other, "a lineage the newer state does not start with stays");
        }
        using (var restored = new Fixture())
        {
            var ids = Conversation(restored, 2);
            restored.Record(B, Mine, ids[0], extra: r => r["priorCliSessionIds"] = new JsonArray(ids[0]));
            restored.Record(A, Theirs, ids[1], extra: r => r["priorCliSessionIds"] = new JsonArray(ids[0]));
            Kept(restored, "a restored state listing its own transcript as earlier stays");
        }
        using (var edges = new Fixture())
        {
            var ids = Conversation(edges, 3);
            var first = Edge(ids[0], ids[1]); var second = Edge(ids[1], ids[2]);
            edges.Record(B, Mine, ids[1], extra: r => { r["priorCliSessionIds"] = new JsonArray(ids[0]); r["rewindEdges"] = new JsonArray(first.DeepClone()); });
            edges.Record(A, Theirs, ids[2], extra: r =>
            { r["priorCliSessionIds"] = new JsonArray(ids[0], ids[1]); r["rewindEdges"] = new JsonArray(second.DeepClone(), first.DeepClone()); });
            Kept(edges, "rewind edges out of order stay");
        }
        using (var outside = new Fixture())
        {
            var ids = Conversation(outside, 2);
            outside.Record(B, Mine, ids[0]);
            outside.Record(A, Theirs, ids[1], extra: r =>
            { r["priorCliSessionIds"] = new JsonArray(ids[0]); r["rewindEdges"] = new JsonArray(Edge(ids[0], outside.Transcript())); });
            Kept(outside, "a rewind edge reaching outside the lineage stays");
        }
        using (var cut = new Fixture())
        {
            var ids = Conversation(cut, 2);
            cut.Record(B, Mine, ids[0], extra: r => r["transcriptCuts"] = new JsonObject { [ids[0]] = Guid.NewGuid().ToString("D") });
            cut.Record(A, Theirs, ids[1], extra: r => r["priorCliSessionIds"] = new JsonArray(ids[0]));
            Kept(cut, "a transcript cut the newer state does not keep stays");
        }
        using (var folder = new Fixture())
        {
            var ids = Conversation(folder, 2);
            folder.Record(B, Mine, ids[0], cwd: Path.Combine(folder.Root, "elsewhere"));
            folder.Record(A, Theirs, ids[1], extra: r => r["priorCliSessionIds"] = new JsonArray(ids[0]));
            Kept(folder, "an older state in another folder stays");
        }
        using (var shelved = new Fixture())
        {
            var ids = Conversation(shelved, 2);
            shelved.Record(B, Mine, ids[0], archived: true);
            shelved.Record(A, Theirs, ids[1], extra: r => r["priorCliSessionIds"] = new JsonArray(ids[0]));
            Kept(shelved, "an archived older state stays archived");
        }
        // A transcript of the lineage being written: nothing is planned there until the writer is gone.
        using (var writing = new Fixture())
        {
            var ids = Conversation(writing, 2);
            writing.Record(B, Mine, ids[0]);
            writing.Record(A, Theirs, ids[1], extra: r => r["priorCliSessionIds"] = new JsonArray(ids[0]));
            var busy = writing.Shelf(() => new ClaudeLiveSessionScan(new HashSet<string> { ids[0] }, false)).Apply([A, B], () => true);
            Check(busy is { Advanced: 0, Created: 0 } && busy.Plan.HasTransientSkips && writing.Files(B).Count == 1,
                "while the older state's transcript is written, neither an advance nor a copy is made, and the run is retried");
            Check(writing.Shelf().Apply([A, B], () => true).Advanced == 1, "once the writer is gone the older state advances");
        }
        // The newest transcript may be written meanwhile, and grow before the record is replaced: the older state takes it.
        using (var growing = new Fixture())
        {
            var ids = Conversation(growing, 2);
            growing.Record(B, Mine, ids[0]);
            growing.Record(A, Theirs, ids[1], extra: r => r["priorCliSessionIds"] = new JsonArray(ids[0]));
            var asks = 0;
            var grown = growing.Shelf(() => new ClaudeLiveSessionScan(new HashSet<string> { ids[1] }, false)).Apply([A, B], () =>
            {
                // After the final plan, before the record is replaced.
                if (++asks == 3) File.AppendAllLines(TranscriptPath(growing, ids[1]), [Entry(DateTime.UtcNow)]);
                return true;
            });
            Check(grown is { Advanced: 1, Created: 0 } && asks >= 3
                  && Json(Path.Combine(growing.ShelfPath(B), Mine + ".json"))["cliSessionId"]!.GetValue<string>() == ids[1],
                "the newest transcript being written, and growing, does not stop the advance");
        }

        using var race = new Fixture();
        var raceIds = Conversation(race, 2);
        race.Record(B, Mine, raceIds[0]);
        race.Record(A, Theirs, raceIds[1], extra: r => r["priorCliSessionIds"] = new JsonArray(raceIds[0]));
        var path = Path.Combine(race.ShelfPath(B), Mine + ".json");
        var ledgerPath = Path.Combine(race.Ledger, "ledger.json");
        var backups = Path.Combine(race.Ledger, ClaudeShelfLedger.BackupFolder);
        var hash = ClaudeSessionShelf.Hash(path);
        // Desktop starts at the very last moment, inside the replacement.
        var checks = 0;
        var late = race.Shelf().Apply([A, B], () => ++checks <= 3);
        Check(late.Advanced == 0 && ClaudeSessionShelf.Hash(path) == hash && late.Plan.Held[B.Key] == "advance_failed" && !late.Plan.IsComplete
              && Json(ledgerPath)["advanced"]!.AsArray().Count == 0 && !Directory.EnumerateFiles(backups).Any(),
            "Desktop starting at the last moment writes nothing, and leaves no advance or saved record behind");
        // The record changes after the final plan read it.
        var scans = 0;
        var changed = race.Shelf(() =>
        {
            if (++scans == 2) Change(path, r => r["lastFocusedAt"] = 9);
            return new ClaudeLiveSessionScan(new HashSet<string>(), false);
        }).Apply([A, B], () => true);
        Check(changed.Advanced == 0 && Json(path)["lastFocusedAt"]!.GetValue<int>() == 9 && changed.Plan.Held[B.Key] == "advance_failed",
            "a record that changed after the plan is not overwritten");
        // A writer starts on the older state's transcript after the plan: the conversation may branch there. (One on the
        // newest transcript is the conversation going on, and does not stop it.)
        var asked = 0;
        var started = race.Shelf(() => new ClaudeLiveSessionScan(++asked >= 3 ? new HashSet<string> { raceIds[0] } : new HashSet<string>(), false))
            .Apply([A, B], () => true);
        Check(started.Advanced == 0 && Json(path)["cliSessionId"]!.GetValue<string>() == raceIds[0] && started.Plan.Held[B.Key] == "advance_failed",
            "a writer that starts on the older state's transcript after the plan stops the advance");
        // The record as it was, and the intent, are in the ledger before the record is replaced.
        var before = File.ReadAllBytes(path);
        hash = ClaudeSessionShelf.Hash(path);
        string? seen = null;
        var moments = 0;
        var done = race.Shelf().Apply([A, B], () =>
        {
            if (++moments == 4) seen = File.ReadAllText(ledgerPath);
            return true;
        });
        var intent = JsonNode.Parse(seen!)!["advanced"]!.AsArray().Single()!;
        Check(done.Advanced == 1 && intent["state"]!.GetValue<string>() == "pending" && intent["before"]!.GetValue<string>() == hash
              && File.ReadAllBytes(Path.Combine(backups, intent["backup"]!.GetValue<string>())).SequenceEqual(before)
              && Json(ledgerPath)["advanced"]!.AsArray().Single()!["state"]!.GetValue<string>() == "done",
            "the advance and the record as it was are saved before the record is replaced, and confirmed after");
        // A run stopped after the intent: the next one settles it from the record.
        void Interrupt(Action? withRecord = null)
        {
            var stopped = Json(ledgerPath);
            stopped["advanced"]![0]!["state"] = "pending";
            File.WriteAllText(ledgerPath, stopped.ToJsonString());
            withRecord?.Invoke();
        }
        Interrupt();
        Check(ClaudeShelfLedger.Load(race.Ledger, race.Sessions).Advanced.Single().State == ClaudeShelfAdvanceState.Done,
            "a record holding the new state confirms the advance");
        Interrupt(() => File.WriteAllBytes(path, before));
        Check(ClaudeShelfLedger.Load(race.Ledger, race.Sessions).Advanced.Count == 0, "a record still as it was cancels it");
        Interrupt(() => Change(path, r => r["lastFocusedAt"] = 11));
        Check(ClaudeShelfLedger.Load(race.Ledger, race.Sessions).Advanced.Single() is { State: ClaudeShelfAdvanceState.Unconfirmed, Backup: not null },
            "a record holding neither keeps the advance on record, with the record as it was");
    }

    private static void TestVerifiedContinuation()
    {
        // B holds r; A holds both s (from r) and t (from s). After t began, s was resumed under its own id and written to
        // (a CLI can do that): s is a branch, so both reach B and B's r is not advanced. Without that write, s is an older
        // state of t and r takes t.
        foreach (var resumed in new[] { true, false })
        {
            using var fixture = new Fixture();
            var ids = Conversation(fixture, 3);
            fixture.Record(B, "local_af000000-0000-4000-8000-000000000001", ids[0]);
            fixture.Record(A, "local_af000000-0000-4000-8000-000000000002", ids[1], extra: r => r["priorCliSessionIds"] = new JsonArray(ids[0]));
            fixture.Record(A, "local_af000000-0000-4000-8000-000000000003", ids[2], extra: r => r["priorCliSessionIds"] = new JsonArray(ids[0], ids[1]));
            if (resumed) WriteLater(fixture, ids[1]);
            var older = Path.Combine(fixture.ShelfPath(B), "local_af000000-0000-4000-8000-000000000001.json");
            var hash = ClaudeSessionShelf.Hash(older);
            var result = fixture.Shelf().Apply([A, B], () => true);
            var listed = fixture.Files(B).Select(path => Json(path)["cliSessionId"]!.GetValue<string>()).Order().ToArray();
            if (resumed)
                Check(result is { Advanced: 0, Created: 2 } && ClaudeSessionShelf.Hash(older) == hash && listed.SequenceEqual(ids.Order()),
                    "a state resumed after the conversation went on is a branch: both branches reach the list, and r stays");
            else
                Check(result is { Advanced: 1, Created: 0 } && listed.SequenceEqual([ids[2]]), "without the resumed state, r takes t");
        }

        // Only t is listed anywhere, but s was resumed after t began: going from r to t skips a step that is not one line,
        // so r is not advanced; t is added beside it. (r itself was left as the conversation went on: A needs no copy.)
        using var skipped = new Fixture();
        var steps = Conversation(skipped, 3);
        skipped.Record(B, "local_af100000-0000-4000-8000-000000000001", steps[0]);
        skipped.Record(A, "local_af100000-0000-4000-8000-000000000002", steps[2], extra: r => r["priorCliSessionIds"] = new JsonArray(steps[0], steps[1]));
        WriteLater(skipped, steps[1]);
        var skippedPath = Path.Combine(skipped.ShelfPath(B), "local_af100000-0000-4000-8000-000000000001.json");
        var skippedHash = ClaudeSessionShelf.Hash(skippedPath);
        Check(skipped.Shelf().Apply([A, B], () => true) is { Advanced: 0, Created: 1 } && ClaudeSessionShelf.Hash(skippedPath) == skippedHash,
            "every step a later state skips is checked before an advance");

        // B lists both a copy this utility made of s and a newer record t. The copy is archived only while its transcript
        // was left when the conversation went on; resumed under the same id afterwards, it is a branch and stays.
        foreach (var resumed in new[] { true, false })
        {
            using var copies = new Fixture();
            var (s, t) = Two(copies);
            copies.Record(A, "local_af200000-0000-4000-8000-000000000001", s);
            var shelf = copies.Shelf();
            shelf.Apply([A, B], () => true);
            var copy = copies.Files(B).Single();
            copies.Record(B, "local_af200000-0000-4000-8000-000000000002", t, extra: r => r["priorCliSessionIds"] = new JsonArray(s));
            if (resumed) WriteLater(copies, s);
            var result = shelf.Apply([A, B], () => true);
            Check(result.Retired == (resumed ? 0 : 1) && Json(copy)["isArchived"]!.GetValue<bool>() == !resumed,
                resumed ? "a copy whose transcript was resumed after the conversation went on is not archived"
                    : "a copy the conversation went on from is archived");
        }

        // After the plan, a CLI resumes the older state's transcript, writes and exits: no writer is left at the swap, but
        // what the decision read has changed, so nothing is written and the next run plans again.
        using var late = new Fixture();
        var (r0, r1) = Two(late);
        late.Record(B, "local_af300000-0000-4000-8000-000000000001", r0);
        late.Record(A, "local_af300000-0000-4000-8000-000000000002", r1, extra: r => r["priorCliSessionIds"] = new JsonArray(r0));
        var latePath = Path.Combine(late.ShelfPath(B), "local_af300000-0000-4000-8000-000000000001.json");
        var lateHash = ClaudeSessionShelf.Hash(latePath);
        var checks = 0;
        var stale = late.Shelf().Apply([A, B], () =>
        {
            if (++checks == 3) WriteLater(late, r0); // between the final plan and the swap
            return true;
        });
        Check(stale.Advanced == 0 && ClaudeSessionShelf.Hash(latePath) == lateHash && stale.Plan.Held[B.Key] == "advance_failed"
              && !stale.Plan.IsComplete && Json(Path.Combine(late.Ledger, "ledger.json"))["advanced"]!.AsArray().Count == 0,
            "a transcript written after the plan stops the advance and leaves the run to be retried");
        Check(late.Shelf().Apply([A, B], () => true) is { Advanced: 0, Created: 2 } && ClaudeSessionShelf.Hash(latePath) == lateHash
              && late.Files(B).Count == 2 && late.Files(A).Count == 2,
            "planned again, the resumed older state is a branch: the newest state is added beside it, and it reaches A too");

        // The source record changes after the plan. What the app keeps updating while it shows the conversation (its title,
        // its activity) does not stop the advance.
        using var touched = new Fixture();
        var (t0, t1) = Two(touched);
        touched.Record(B, "local_af500000-0000-4000-8000-000000000001", t0);
        touched.Record(A, "local_af500000-0000-4000-8000-000000000002", t1, extra: r => r["priorCliSessionIds"] = new JsonArray(t0));
        var shown = Path.Combine(touched.ShelfPath(A), "local_af500000-0000-4000-8000-000000000002.json");
        var touches = 0;
        var retitled = touched.Shelf().Apply([A, B], () =>
        {
            if (++touches == 3) Change(shown, r => { r["title"] = "바뀐 이름"; r["lastActivityAt"] = 500; });
            return true;
        });
        Check(retitled.Advanced == 1, "a source record the app only retitled or used after the plan does not stop the advance");
        // What the decision read changes: nothing is written, and the next run plans again.
        using var moved = new Fixture();
        var (m0, m1) = Two(moved);
        moved.Record(B, "local_af400000-0000-4000-8000-000000000001", m0);
        moved.Record(A, "local_af400000-0000-4000-8000-000000000002", m1, extra: r => r["priorCliSessionIds"] = new JsonArray(m0));
        var source = Path.Combine(moved.ShelfPath(A), "local_af400000-0000-4000-8000-000000000002.json");
        var asked = 0;
        var changed = moved.Shelf().Apply([A, B], () =>
        {
            if (++asked == 3) Change(source, r => r["isArchived"] = true);
            return true;
        });
        Check(changed.Advanced == 0 && changed.Plan.Held[B.Key] == "advance_failed",
            "a source record whose decision fields changed after the plan stops the advance");
        Change(source, r => r["isArchived"] = false);
        Check(moved.Shelf().Apply([A, B], () => true).Advanced == 1, "planned again from the record as it is, the advance goes ahead");
    }

    /// <summary>B holds an older state whose transcript the app has in two project folders: begun in one whose name sorts
    /// first, copied to E--work when resumed there two hours later and gone on with in that copy, then in the next
    /// transcript a day later, which A holds. Judged from the copy the conversation is in, B's record takes the newest state
    /// in place (judged from the first folder's, the hand-over came before the next transcript was made, and the newest
    /// state was added beside it). With the copies gone two ways it is added beside, and the transcript still counts as
    /// there.</summary>
    private static void TestTranscriptInTwoFolders()
    {
        const string Mine = "local_ad600000-0000-4000-8000-000000000001", Theirs = "local_ad600000-0000-4000-8000-000000000002";
        foreach (var twoWays in new[] { false, true })
        {
            using var fixture = new Fixture();
            var start = DateTime.UtcNow.Date.AddDays(-20);
            var older = Guid.NewGuid().ToString("D");
            var first = Entry(start.AddMilliseconds(500));
            var stale = Path.Combine(Directory.CreateDirectory(Path.Combine(fixture.Projects, "A--elsewhere")).FullName, older + ".jsonl");
            File.WriteAllLines(stale, [first]);
            File.SetCreationTimeUtc(stale, start);
            File.WriteAllLines(TranscriptPath(fixture, older), [first, Entry(start.AddHours(3))]);
            File.SetCreationTimeUtc(TranscriptPath(fixture, older), start.AddHours(2));
            var next = GoOn(fixture, older, start.AddDays(1));
            // Written to after the other copy was made.
            if (twoWays) File.AppendAllLines(stale, [Entry(start.AddHours(5))]);
            fixture.Record(B, Mine, older);
            fixture.Record(A, Theirs, next, extra: r => r["priorCliSessionIds"] = new JsonArray(older));
            var path = Path.Combine(fixture.ShelfPath(B), Mine + ".json");
            var hash = ClaudeSessionShelf.Hash(path);
            var result = fixture.Shelf().Apply([A, B], () => true);
            if (!twoWays)
                Check(result is { Advanced: 1, Created: 0 } && Json(path)["cliSessionId"]!.GetValue<string>() == next && fixture.Files(B).Count == 1,
                    "an older state whose transcript has a stale copy in a folder sorting first takes the newest state in place");
            else
                Check(result is { Advanced: 0, Created: 2 } && ClaudeSessionShelf.Hash(path) == hash && fixture.Files(B).Count == 2
                      && result.Plan.Skipped.Values.All(reasons => !reasons.ContainsKey("missing_transcript")),
                    "with its copies gone two ways it is not advanced: the newest state is added beside it, and the conversation "
                    + "still counts as there");
        }
    }

    private static string TranscriptPath(Fixture fixture, string id) => Path.Combine(fixture.Projects, "E--work", id + ".jsonl");

    private static string Log(Fixture fixture, DateTime created, IEnumerable<string> lines)
    {
        var id = Guid.NewGuid().ToString("D");
        File.WriteAllLines(TranscriptPath(fixture, id), lines);
        File.SetCreationTimeUtc(TranscriptPath(fixture, id), created);
        return id;
    }

    private static string Entry(DateTime at) => "{\"type\":\"user\",\"uuid\":\"" + Guid.NewGuid().ToString("D") + "\",\"timestamp\":\""
        + at.ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", System.Globalization.CultureInfo.InvariantCulture) + "\"}";

    /// <summary>The conversation going on from <paramref name="from"/> in a new transcript, as the CLI writes one: copies of
    /// the earlier entries (ids and times kept), then one of its own half a second after the file was created.</summary>
    private static string GoOn(Fixture fixture, string from, DateTime created) =>
        Log(fixture, created, File.ReadAllLines(TranscriptPath(fixture, from)).Append(Entry(created.AddMilliseconds(500))));

    /// <summary>One conversation in <paramref name="states"/> transcripts, each going on from the one before a day later.</summary>
    private static string[] Conversation(Fixture fixture, int states)
    {
        var start = DateTime.UtcNow.Date.AddDays(-20);
        var ids = new List<string> { Log(fixture, start, [Entry(start.AddMilliseconds(500))]) };
        while (ids.Count < states) ids.Add(GoOn(fixture, ids[^1], start.AddDays(ids.Count)));
        return [.. ids];
    }

    private static (string Earlier, string Later) Two(Fixture fixture) => Conversation(fixture, 2) is [var earlier, var later]
        ? (earlier, later) : throw new InvalidOperationException();

    /// <summary>An entry written to a transcript long after the conversation went on elsewhere: a branch.</summary>
    private static void WriteLater(Fixture fixture, string id) =>
        File.AppendAllLines(TranscriptPath(fixture, id), [Entry(DateTime.UtcNow.Date.AddDays(-1))]);

    // A rewind edge as the app writes it: from one transcript to the next, at a message of the earlier one.
    private static JsonObject Edge(string parent, string child) => new()
    {
        ["id"] = Guid.NewGuid().ToString("D"), ["parent"] = parent, ["child"] = child, ["at"] = 1, ["cwd"] = @"C:\work",
        ["forkPoint"] = Guid.NewGuid().ToString("D")
    };

    private static JsonObject Json(string path) => JsonNode.Parse(File.ReadAllText(path))!.AsObject();

    private static void Change(string path, Action<JsonObject> change)
    {
        var record = Json(path);
        change(record);
        File.WriteAllText(path, record.ToJsonString());
    }

    private sealed class Fixture : IDisposable
    {
        internal string Root { get; } = Path.GetFullPath(Path.Combine(
            Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT") ?? Path.Combine(Path.GetTempPath(), "gfs-agent", "claude-shelf-tests"),
            "shelf-" + Guid.NewGuid().ToString("N")));
        internal string Sessions => Path.Combine(Root, "claude-code-sessions");
        internal string Projects => Path.Combine(Root, "projects");
        internal string Scratch => Path.Combine(Root, "scratch-workspaces");
        internal string Ledger => Path.Combine(Root, "ledger");
        internal List<string> OriginalHashes { get; } = [];
        private readonly string _work;

        internal Fixture()
        {
            Directory.CreateDirectory(Path.Combine(Projects, "E--work"));
            _work = Directory.CreateDirectory(Path.Combine(Root, "work")).FullName;
        }

        internal string? Config { get; set; }

        internal ClaudeSessionShelf Shelf(Func<ClaudeLiveSessionScan>? writers = null) => new(
            new ClaudeShelfPaths(Sessions, Projects, Scratch, [], Ledger, Config),
            writers ?? (() => new ClaudeLiveSessionScan(new HashSet<string>(), false)), () => new MemoryStream());

        internal string ShelfPath(ClaudeShelfAccount account) => Path.Combine(Sessions, account.AccountUuid, account.OrgUuid);

        internal string Transcript()
        {
            var id = Guid.NewGuid().ToString("D");
            File.WriteAllText(Path.Combine(Projects, "E--work", id + ".jsonl"), "{}\n");
            return id;
        }

        internal void Record(ClaudeShelfAccount account, string sessionId, string? cli, bool archived = false,
            string? cwd = null, bool createCwd = true, Action<JsonObject>? extra = null)
        {
            var directory = Directory.CreateDirectory(ShelfPath(account)).FullName;
            cwd ??= _work;
            if (createCwd) Directory.CreateDirectory(cwd);
            var record = new JsonObject { ["sessionId"] = sessionId, ["cliSessionId"] = cli, ["cwd"] = cwd,
                ["title"] = "세션 " + sessionId[^2..], ["createdAt"] = 1, ["lastActivityAt"] = 2, ["isArchived"] = archived };
            extra?.Invoke(record);
            var path = Path.Combine(directory, sessionId + ".json");
            File.WriteAllText(path, record.ToJsonString());
            if (account == A) OriginalHashes.Add(ClaudeSessionShelf.Hash(path));
        }

        internal List<string> Files(ClaudeShelfAccount account) => Directory.Exists(ShelfPath(account))
            ? Directory.EnumerateFiles(ShelfPath(account), "local_*.json").Order().ToList() : [];
        internal List<string> Hashes(ClaudeShelfAccount account) =>
            Files(account).Select(ClaudeSessionShelf.Hash).ToList();

        public void Dispose() { try { Directory.Delete(Root, recursive: true); } catch (IOException) { } }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
    private static T MustThrow<T>(Action action) where T : Exception
    {
        try { action(); }
        catch (T exception) { return exception; }
        throw new Exception($"Expected {typeof(T).Name}.");
    }
}
