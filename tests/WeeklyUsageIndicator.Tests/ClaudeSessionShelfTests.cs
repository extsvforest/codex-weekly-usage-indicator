using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using WeeklyUsageIndicator;

internal static class ClaudeSessionShelfTests
{
    private const string AccountA = "0998b264-7315-4fdb-8634-fc7f3f827316", OrgA = "7f98dbbd-af39-46bf-9609-cfd3972b3633";
    private const string AccountB = "46411433-0040-4598-a1bd-a5444a5eb7b4", OrgB = "a8f42351-0c23-4928-87ef-ecb3e3bf2b10";
    private static readonly ClaudeShelfAccount A = new(AccountA, OrgA, "Main"), B = new(AccountB, OrgB, "Sub");

    internal static async Task RunAsync()
    {
        TestPlanningAndPublishing();
        TestExclusionsAndHolds();
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
        TestConversationLineage();
        TestSidebarPrefs();
        TestLineageEdges();
        TestLocalStorage();
        TestTitlesFollowOriginals();
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
        fixture.Record(A, "local_bbbbbbbb-0000-4000-8000-000000000004", fixture.Transcript(), extra: r => r["worktreePath"] = "C:\\wt");
        fixture.Record(A, "local_bbbbbbbb-0000-4000-8000-000000000005", Guid.NewGuid().ToString("D"));
        var deletedCli = fixture.Transcript();
        fixture.Record(A, "local_bbbbbbbb-0000-4000-8000-000000000006", deletedCli);
        Directory.CreateDirectory(fixture.ShelfPath(B));
        File.WriteAllText(Path.Combine(fixture.ShelfPath(B), "deleted_" + deletedCli), "");
        var plan = fixture.Shelf().Plan([A, B]);
        var reasons = plan.Skipped.Values.Single();
        Check(plan.Actions.Count == 0 && reasons["scratch"] == 1 && reasons["missing_cwd"] == 1 && reasons["remote"] == 1
              && reasons["worktree"] == 1 && reasons["missing_transcript"] == 1 && reasons["deleted_in_target"] == 1,
            "scratch, missing folders, remote, worktree, missing transcripts and target deletions are excluded");

        File.WriteAllText(Path.Combine(fixture.ShelfPath(B), "local_cccccccc-0000-4000-8000-000000000001.json.tmp"), "{}");
        Check(fixture.Shelf().Plan([A, B]).Held[B.Key] == "recovering", "a pending app recovery holds the target shelf");
        File.Delete(Path.Combine(fixture.ShelfPath(B), "local_cccccccc-0000-4000-8000-000000000001.json.tmp"));
        File.WriteAllText(Path.Combine(fixture.ShelfPath(B), "local_cccccccc-0000-4000-8000-000000000002.json"), "{broken");
        Check(fixture.Shelf().Plan([A, B]).Held[B.Key] == "unreadable", "an unreadable record holds the target shelf");
    }

    private static void TestWriterAndDesktopRaces()
    {
        using var fixture = new Fixture();
        var first = fixture.Transcript(); var second = fixture.Transcript();
        fixture.Record(A, "local_dddddddd-0000-4000-8000-000000000001", first);
        fixture.Record(A, "local_dddddddd-0000-4000-8000-000000000002", second);

        MustThrow<ClaudeDesktopRunningException>(() => fixture.Shelf().Apply([A, B], () => false));
        Check(fixture.Files(B).Count == 0, "nothing is written while Desktop runs");

        // A writer appears between the two plans while the shelves stay identical: publish the final plan.
        var calls = 0;
        var shelf = fixture.Shelf(() => new ClaudeLiveSessionScan(
            ++calls >= 2 ? new HashSet<string> { first } : new HashSet<string>(), false));
        var result = shelf.Apply([A, B], () => true);
        Check(result.Created == 1 && JsonNode.Parse(File.ReadAllText(fixture.Files(B).Single()))!["cliSessionId"]!
            .GetValue<string>() == second, "a writer that appears just before publishing keeps its session out");
        Check(result.Plan.HasTransientSkips && !result.Plan.IsComplete, "a writer skip leaves the run incomplete for retry");

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
        store.Remove(main.Id);
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

        using var fixture = new Fixture();
        var logs = Directory.CreateDirectory(Path.Combine(fixture.Root, "logs")).FullName;
        File.WriteAllLines(Path.Combine(logs, "main1.log"), lines.Take(3));
        File.WriteAllLines(Path.Combine(logs, "main.log"), lines.Skip(3).Take(3));
        var watcher = new ClaudeDesktopLogWatcher(logs);
        Check(watcher.Update(start, registered).Status == ClaudeDesktopAccountStatus.SignedIn,
            "a new run replays the rotated file and the current file in order");
        File.AppendAllLines(Path.Combine(logs, "main.log"), [lines[6]]);
        Check(watcher.Update(start, registered).Confirmed, "later lines are read incrementally");
        Check(watcher.Update(null, registered).Status == ClaudeDesktopAccountStatus.Closed, "a closed app has no current account");
    }

    // Shapes of the reviewed 2.16120.0.0 bundle. Minified names change between builds; the flows must not.
    private const string CollectIds =
        "for(let n of[i.cliSessionId,i.unarchivedCliSessionId])typeof n==\"string\"&&(!a?.localOnly||t.WQ(n))&&e.add(n);for(let t of n.En(i.priorCliSessionIds)??[])e.add(t)}catch(n){}";
    private const string Reap =
        "async mayReapTranscript(e,n,r,i,a){let o=r!==void 0,s=(t,n)=>pF(e,t,n,o);let c=1;let l=await this.cliSessionIdsClaimedOnDisk();return l===null?s(\"records_unreadable\",\"could not be checked against the session records on disk\"):l.has(e)?s(\"claimed_elsewhere\",\"is claimed by a session of another account or profile on this computer\"):!0}";
    private const string Claims =
        "cliSessionIdsClaimedOnDisk(){let i={failClosed:!0,localOnly:!0,recordsOnly:!0,skipOwnRecordsOf:new Set},a=(async()=>{await Wj(e,{},(()=>this.getAccountIds()),i)})}";
    private const string Release =
        "vR(this.diskTranscript,C.filter((e=>!t.has(e))),d,\"delete\",{mayRelease:async e=>!((await this.cliSessionIdsClaimedOnDisk())?.has(e)??!1),reapIf:async(t,r)=>x!==void 0&&!await x?pF(t,\"own_process_running\",\"may still be written by this session's Claude Code process, whose exit could not be confirmed\",n!==void 0):this.mayReapTranscript(t,r,n,e,uF(d)),keptInDoubt:e=>mF(e,n!==void 0)})";
    private const string Remove =
        "async function vR(e,n,r,i,a={}){let o=[],s;for(let c of new Set(n))!c||!oR(c)||o.push(cR(c,(async()=>{try{if(a.mayRelease&&!await a.mayRelease(c))return}catch{return}let{resolved:n,dirs:o}=await hR(e,c,r,(()=>s??=pR())),l=[],u=!1;for(let e of o){try{await t.mQ(e,{v:1,reason:i}),l.push(e),u||=e===n}catch(e){}}if(n===null||!u||!a.reapIf)return;let d=!1;try{let e=await _R(mR(n,c));if(e!==null){d=await a.reapIf(c,{mtimeMs:e.mtimeMs})}}catch(e){t.qZ.debug(\"desktop-released marker: reap check failed\",{cliSessionId:c,err:e});try{a.keptInDoubt?.(c)}catch{}}if(d)for(let e of l){await J.default.promises.rm(mR(e,c),{force:!0})}})))}";

    private static byte[] Bundle(string collect = CollectIds, string reap = Reap, string release = Release, string remove = Remove) =>
        Encoding.UTF8.GetBytes(string.Join("\n", ClaudeDesktopProbe.ContractMarkers.Select(spellings => spellings[0])) + "\n" + collect
            + "\n" + reap + "\n" + Claims + "\n" + release + "\n" + remove);

    private static void TestContractGuard()
    {
        Check(ClaudeDesktopProbe.IsCompatible(Bundle()), "the reviewed deletion protection is compatible");
        Check(ClaudeDesktopProbe.IsCompatible(Bundle(collect: CollectIds.Replace("let n ", "let q ").Replace("typeof n", "typeof q")
                .Replace("WQ(n)", "WQ(q)").Replace("e.add(n)", "z.add(q)").Replace("e.add(t)", "z.add(t)"))),
            "renamed minified variables stay compatible");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(collect: CollectIds.Replace("&&e.add(n);", "&&e.add(o);"))),
            "claiming something other than the record's current transcript id pauses writes");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(collect: CollectIds.Replace("e.add(t)}", "void t}"))),
            "no longer claiming earlier transcript ids pauses writes");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(reap: Reap.Replace("l.has(e)?", "l.has(n)?"))),
            "a reap check that no longer tests the transcript being deleted pauses writes");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(reap: Reap.Replace("\"records_unreadable\"", "\"records_ignored\""))),
            "a deletion path that no longer fails closed pauses writes");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(release: Release.Replace("?.has(e)", "?.has(t)"))),
            "a release decision that stops consulting the claims pauses writes");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(release: Release.Replace(":this.mayReapTranscript(t,r,n,e,uF(d))", ":!0"))),
            "a session delete that no longer asks the reap decision pauses writes");
        Check(ClaudeDesktopProbe.IsCompatible(Bundle(release: Release[..Release.IndexOf("reapIf:", StringComparison.Ordinal)]
                + "reapIf:async(t,r)=>this.mayReapTranscript(t,r)})")),
            "a simpler delete call that still asks the reap decision stays compatible");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(remove: Remove.Replace("if(d)for(", "if(!0)for("))),
            "removing transcripts regardless of the reap decision pauses writes");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(remove: Remove.Replace("catch(e){t.qZ.debug(", "catch(e){d=!0;t.qZ.debug("))),
            "a failed reap check that removes anyway pauses writes");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(remove: Remove.Replace("err:e});try{", "err:e});d=!0;try{"))),
            "setting the decision after logging a failed check pauses writes");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(remove: Remove.Replace("{mtimeMs:e.mtimeMs})}", "{mtimeMs:e.mtimeMs});d=!0}"))),
            "overriding the reap decision after it returns pauses writes");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(remove: Remove.Replace("let d=!1;try{", "let d=!1;try{d=!0;"))),
            "setting the decision before the reap check pauses writes");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(release: Release.Replace("uF(d)),keptInDoubt", "uF(d))||!0,keptInDoubt"))),
            "changing the reap check's result pauses writes");
        var keep = Release[Release.IndexOf("?pF(", StringComparison.Ordinal)..(Release.IndexOf("):this.mayReapTranscript", StringComparison.Ordinal) + 2)];
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(release: Release.Replace(keep, "?!0:"))),
            "a guard branch that allows deletion without the reap check pauses writes");
        Check(!ClaudeDesktopProbe.IsCompatible(Bundle(reap: Reap.Replace("async mayReapTranscript(e,n,r,i,a){",
            "async mayReapTranscript(e,n,r,i,a){" + new string(' ', 4000)))), "protection detached from its function pauses writes");
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
    }

    private static void TestRelaunchReservation()
    {
        using var fixture = new Fixture();
        var now = DateTimeOffset.Now; // RelaunchArmed compares with the real clock
        var state = ClaudeDesktopRunState.Closed;
        var launches = 0; var failLaunch = false;
        var package = new ClaudeDesktopPackage("Claude_9.9.9.9_x64__pzs8sxrjxfjjc", new Version(9, 9, 9, 9), fixture.Root, "Claude_pzs8sxrjxfjjc");
        var environment = new ClaudeShelfEnvironment(() => new ClaudeDesktopRuntime(state, null), () => package, _ => true,
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

    /// <summary>Read-only report of the real environment: nothing is written outside a temporary cache.</summary>
    internal static void LiveProbe()
    {
        var package = ClaudeDesktopProbe.FindPackage();
        Console.WriteLine($"package: {package?.FullName ?? "(none)"}");
        if (package is not null) Console.WriteLine($"compatible: {ClaudeDesktopProbe.IsCompatible(package.AsarPath)}");
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
                // The real store, on a copy kept for an independent reader to check.
                var real = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Claude", "Local Storage", "leveldb");
                var trial = Path.Combine(Path.GetTempPath(), "gfs-agent", "260930_claude-acct-switch", "ls-trial");
                if (Directory.Exists(trial)) Directory.Delete(trial, recursive: true);
                Directory.CreateDirectory(trial);
                foreach (var file in Directory.EnumerateFiles(real))
                    if (!Path.GetFileName(file).Equals("LOCK", StringComparison.OrdinalIgnoreCase)) File.Copy(file, Path.Combine(trial, Path.GetFileName(file)));
                File.WriteAllText(Path.Combine(trial, "LOCK"), "");
                var trialState = trial + "-state";
                if (Directory.Exists(trialState)) Directory.Delete(trialState, recursive: true);
                var trialOutcome = ClaudeSidebarPrefs.Sync(new ClaudeSidebarPrefs.LocalStorageSource(new ClaudeLocalStorage(trial), trialState, () => DateTimeOffset.UtcNow),
                    trialState, links, moved, () => true);
                Console.WriteLine($"sidebar trial on a copy of the real store: {trialOutcome} at {trial}");
            }
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
    }

    private static void TestConversationLineage()
    {
        using var fixture = new Fixture();
        // A conversation continued in A: its newer record lists the earlier transcript id.
        var old = fixture.Transcript(); var newer = fixture.Transcript();
        fixture.Record(A, "local_a1a1a1a1-0000-4000-8000-000000000001", old);
        fixture.Record(A, "local_a1a1a1a1-0000-4000-8000-000000000002", newer, extra: r => r["priorCliSessionIds"] = new JsonArray(old));
        var plan = fixture.Shelf().Plan([A, B]);
        Check(plan.Actions.Count == 1 && plan.Actions[0].CliSessionId == newer && plan.Skipped.Values.Single()["superseded"] == 1,
            "an older state of a conversation is never copied when a newer one is in the same list");

        // B already has the newer state: an older id is present through its lineage.
        fixture.Record(B, "local_b2b2b2b2-0000-4000-8000-000000000001", newer, extra: r => r["priorCliSessionIds"] = new JsonArray(old));
        Check(fixture.Shelf().Plan([A, B]).Actions.Count == 0, "a conversation already listed in a newer state is present");

        // A copy made before the conversation moved on is archived, not deleted, once the newer state is copied.
        using var moved = new Fixture();
        var first = moved.Transcript(); var second = moved.Transcript();
        moved.Record(A, "local_c3c3c3c3-0000-4000-8000-000000000001", first);
        var shelf = moved.Shelf();
        Check(shelf.Apply([A, B], () => true).Created == 1, "the first state is copied to B");
        var stale = moved.Files(B).Single();
        File.Delete(moved.Files(A).Single());
        moved.Record(A, "local_c3c3c3c3-0000-4000-8000-000000000002", second, extra: r => r["priorCliSessionIds"] = new JsonArray(first));
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
        var one = moving.Transcript(); var two = moving.Transcript();
        moving.Record(A, "local_b8b8b8b8-0000-4000-8000-000000000001", one);
        var pinning = moving.Shelf();
        pinning.Apply([A, B], () => true);
        var stale = Path.GetFileNameWithoutExtension(moving.Files(B).Single());
        File.WriteAllText(moving.Config, new JsonObject { ["preferences"] = new JsonObject { ["epitaxyPrefs"] = new JsonObject
            { ["dframe-local-slice"] = new JsonObject { ["pinnedOrder"] = new JsonArray("code:" + stale) } } } }.ToJsonString());
        File.Delete(moving.Files(A).Single());
        moving.Record(A, "local_b8b8b8b8-0000-4000-8000-000000000002", two, extra: r => r["priorCliSessionIds"] = new JsonArray(one));
        // The app reopens after the copy is archived and before the settings are written.
        var checks = 0;
        MustThrow<ClaudeDesktopRunningException>(() => pinning.Apply([A, B], () => ++checks <= 4));
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
        var h1 = halted.Transcript(); var h2 = halted.Transcript();
        halted.Record(A, "local_d0d0d0d0-0000-4000-8000-000000000001", h1);
        var haltedShelf = halted.Shelf();
        haltedShelf.Apply([A, B], () => true);
        var haltedCopy = halted.Files(B).Single();
        var haltedId = Path.GetFileNameWithoutExtension(haltedCopy);
        File.WriteAllText(halted.Config, new JsonObject { ["preferences"] = new JsonObject { ["epitaxyPrefs"] = new JsonObject
            { ["dframe-local-slice"] = new JsonObject { ["pinnedOrder"] = new JsonArray("code:" + haltedId) } } } }.ToJsonString());
        File.Delete(halted.Files(A).Single());
        halted.Record(A, "local_d0d0d0d0-0000-4000-8000-000000000002", h2, extra: r => r["priorCliSessionIds"] = new JsonArray(h1));
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
        var o1 = odd.Transcript(); var o2 = odd.Transcript();
        odd.Record(A, "local_e1e1e1e1-0000-4000-8000-000000000001", o1);
        var oddShelf = odd.Shelf();
        oddShelf.Apply([A, B], () => true);
        var oddCopy = Path.GetFileNameWithoutExtension(odd.Files(B).Single());
        File.Delete(odd.Files(A).Single());
        odd.Record(A, "local_e1e1e1e1-0000-4000-8000-000000000002", o2, extra: r => r["priorCliSessionIds"] = new JsonArray(o1));
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
        var s1 = stuck.Transcript(); var s2 = stuck.Transcript();
        stuck.Record(A, "local_f2f2f2f2-0000-4000-8000-000000000001", s1);
        var stuckShelf = stuck.Shelf();
        stuckShelf.Apply([A, B], () => true);
        var stuckCopy = stuck.Files(B).Single();
        File.Delete(stuck.Files(A).Single());
        stuck.Record(A, "local_f2f2f2f2-0000-4000-8000-000000000002", s2, extra: r => r["priorCliSessionIds"] = new JsonArray(s1));
        File.SetAttributes(stuckCopy, FileAttributes.ReadOnly);
        var failed = stuckShelf.Apply([A, B], () => true);
        File.SetAttributes(stuckCopy, FileAttributes.Normal);
        Check(failed.Retired == 0 && !failed.Plan.IsComplete && failed.Plan.Held[B.Key] == "archive_failed"
              && !JsonNode.Parse(File.ReadAllText(stuckCopy))!["isArchived"]!.GetValue<bool>(), "a failed archive leaves the run unfinished");
        Check(stuckShelf.Apply([A, B], () => true).Retired == 1, "the archive succeeds on the retry");

        // Archived, but the run stopped before the ledger said so; the next run cannot read the copy at first.
        using var locked = new Fixture();
        locked.Config = Path.Combine(locked.Root, "claude_desktop_config.json");
        var l1 = locked.Transcript(); var l2 = locked.Transcript();
        locked.Record(A, "local_f3f3f3f3-0000-4000-8000-000000000001", l1);
        var lockedShelf = locked.Shelf();
        lockedShelf.Apply([A, B], () => true);
        var lockedCopy = locked.Files(B).Single();
        var lockedId = Path.GetFileNameWithoutExtension(lockedCopy);
        File.Delete(locked.Files(A).Single());
        locked.Record(A, "local_f3f3f3f3-0000-4000-8000-000000000002", l2, extra: r => r["priorCliSessionIds"] = new JsonArray(l1));
        lockedShelf.Apply([A, B], () => true); // copies the newer state to B (no sidebar file yet: nothing to move)
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
        var b1 = busy.Transcript(); var b2 = busy.Transcript();
        busy.Record(A, "local_f4f4f4f4-0000-4000-8000-000000000001", b1);
        var busyShelf = busy.Shelf();
        busyShelf.Apply([A, B], () => true);
        var busyCopy = Path.GetFileNameWithoutExtension(busy.Files(B).Single());
        File.WriteAllText(busy.Config, new JsonObject { ["preferences"] = new JsonObject { ["epitaxyPrefs"] = new JsonObject
            { ["dframe-local-slice"] = new JsonObject { ["pinnedOrder"] = new JsonArray("code:" + busyCopy) } } } }.ToJsonString());
        File.Delete(busy.Files(A).Single());
        busy.Record(A, "local_f4f4f4f4-0000-4000-8000-000000000002", b2, extra: r => r["priorCliSessionIds"] = new JsonArray(b1));
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
        // Held lists and live writers are left alone.
        File.WriteAllText(Path.Combine(clean.ShelfPath(B), "local_cccccccc-0000-4000-8000-000000000009.json.tmp"), "{}");
        Check(cleanShelf.Apply([A, B], () => true).Retitled == 0, "a list the app is recovering is not retitled");
        File.Delete(Path.Combine(clean.ShelfPath(B), "local_cccccccc-0000-4000-8000-000000000009.json.tmp"));
        var unknownWriters = clean.Shelf(() => new ClaudeLiveSessionScan(new HashSet<string>(), true));
        Check(unknownWriters.Apply([A, B], () => true).Retitled == 0, "unknown writers hold retitling");
        var writing = clean.Shelf(() => new ClaudeLiveSessionScan(new HashSet<string> { cleanCli.ToLowerInvariant() }, false));
        Check(writing.Apply([A, B], () => true).Retitled == 0, "a conversation being written is not retitled");
        ClaudeShelfResult blocked;
        using (new FileStream(cleanCopy, FileMode.Open, FileAccess.Read, FileShare.Read)) blocked = cleanShelf.Apply([A, B], () => true);
        Check(blocked.Retitled == 0 && !blocked.Plan.IsComplete && blocked.Plan.Held[B.Key] == "retitle_failed" && Title(cleanCopy) != "원본 새 이름",
            "a rename that cannot be written leaves the run unfinished");
        Check(cleanShelf.Apply([A, B], () => true).Retitled == 1 && Title(cleanCopy) == "원본 새 이름", "an untouched copy without a recorded title is renamed");
        // The ledger owns exactly the bytes written; a later change by the app keeps the copy out of undo.
        Set(cleanCopy, r => r["completedTurns"] = 7);
        Check(cleanShelf.Undo(() => true).Removed == 0 && File.Exists(cleanCopy), "a copy changed after retitling is kept by undo");
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
