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
        }
        finally { if (Directory.Exists(scratch)) Directory.Delete(scratch, recursive: true); }
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

        internal ClaudeSessionShelf Shelf(Func<ClaudeLiveSessionScan>? writers = null) => new(
            new ClaudeShelfPaths(Sessions, Projects, Scratch, [], Ledger),
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
