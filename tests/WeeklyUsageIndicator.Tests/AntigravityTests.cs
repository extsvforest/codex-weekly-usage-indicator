using System.Diagnostics;
using System.Globalization;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using WeeklyUsageIndicator;

// Antigravity usage: what `agy -p "/usage" --output-format json` prints, whose run it was, the accounts kept, where agy
// is found, and whole reads against a stand-in for agy (this program, started the way the reader starts agy). No real
// agy runs here; --antigravity-live-check runs it once. Every address and account below is made up.
internal static class AntigravityTests
{
    private const string Address = "river.fox@example.test", OtherAddress = "Moss.Wren@example.test";
    private const string PlanFile = "fake-agy.json", RunsFile = "fake-agy-runs.txt";

    // The shape agy printed on 2026-11-21 (consumer plan), with made-up values.
    private const string Sample = """
        {"status":"SUCCESS","response":"Gemini Models\tWeekly Limit Remaining\t64%\t2026-11-26T08:15:00Z\n","duration_seconds":0,
         "num_turns":0,"usage":{"input_tokens":0,"output_tokens":0,"thinking_tokens":0,"cache_read_tokens":0,"total_tokens":0},
         "command":{"name":"usage","data":{"description":"Within each group, models share a weekly limit and a 5-hour limit.",
          "groups":[
           {"name":"Gemini Models","description":"Models within this group: two made-up models","buckets":[
            {"id":"gemini-weekly","name":"Weekly Limit Remaining","window":"weekly","remaining_fraction":0.6449999809265137,"reset_time":"2026-11-26T08:15:00Z"},
            {"id":"gemini-5h","name":"Five Hour Limit Remaining","window":"5h","remaining_fraction":0.125,"reset_time":"2026-11-21T20:40:00+09:00"}]},
           {"name":"Claude and GPT models","buckets":[
            {"id":"3p-weekly","name":"Weekly Limit Remaining","window":"weekly","remaining_fraction":1,"reset_time":"1970-01-01T00:00:00Z"},
            {"id":"3p-5h","name":"Five Hour Limit Remaining","window":"5h","remaining_fraction":1,"reset_time":"1970-01-01T00:00:00Z"}]}]}}}
        """;

    internal static async Task RunAsync()
    {
        CheckReport();
        CheckIdentity();
        CheckLocate();
        CheckStartInfo();
        CheckMessages();
        var root = Path.Combine(TestBase(), "antigravity-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            CheckStore(Path.Combine(root, "store"));
            await CheckReaderAsync(Path.Combine(root, "reader"));
        }
        finally { TryDelete(root); }
    }

    private static void CheckReport()
    {
        var usage = AntigravityUsageReport.Parse(Sample) ?? throw Fail("the sample report reads as usage");
        Check(usage.GeminiWeekly is { RemainingPercent: 64 } weekly && weekly.RemainingFraction == 0.6449999809265137 &&
              weekly.ResetsAt == new DateTimeOffset(2026, 11, 26, 8, 15, 0, TimeSpan.Zero),
            "the Gemini weekly window keeps its share and its reset");
        Check(usage.GeminiFiveHour is { RemainingPercent: 13, ResetsAt: { } fiveHour } &&
              fiveHour == new DateTimeOffset(2026, 11, 21, 11, 40, 0, TimeSpan.Zero) && fiveHour.Offset == TimeSpan.Zero,
            "a share is rounded half away from zero and a reset is kept in UTC");
        Check(usage.ThirdPartyWeekly == new AntigravityWindow(1, null) && usage.ThirdPartyFiveHour == new AntigravityWindow(1, null),
            "a window not used yet (its reset is 1970) is whole and has no reset");
        Check(AntigravityUsageReport.Parse("\uFEFF" + Sample + "\r\n") == usage, "a byte-order mark and a last line break are ignored");
        Check(AntigravityUsageReport.Parse(Edited(report => Groups(report).RemoveAt(1))) is
                { GeminiWeekly: not null, GeminiFiveHour: not null, ThirdPartyWeekly: null, ThirdPartyFiveHour: null },
            "a group the CLI does not report leaves its windows unknown");
        Check(AntigravityUsageReport.Parse(Edited(report => Bucket(report, "gemini-5h").Remove("remaining_fraction")))
                ?.GeminiFiveHour?.RemainingFraction == 0,
            "a window with no remaining_fraction is used up: the CLI leaves a zero out");
        Check(AntigravityUsageReport.Parse(Edited(report => Bucket(report, "gemini-weekly")["reset_time"] = "next week")) is
                { GeminiWeekly: { ResetsAt: null, RemainingPercent: 64 } },
            "a reset that is not a time is unknown, and the share still stands");
        Check(AntigravityUsageReport.Parse(Edited(report => Bucket(report, "gemini-weekly").Remove("reset_time")))?.GeminiWeekly
                is { ResetsAt: null },
            "a window without a reset time has no reset");
        Check(AntigravityUsageReport.Parse(Edited(report =>
            {
                Groups(report).Add(new JsonObject
                {
                    ["name"] = "Later models",
                    ["buckets"] = new JsonArray(new JsonObject { ["id"] = "later-weekly", ["remaining_fraction"] = 2 })
                });
                Groups(report).Add(new JsonObject { ["name"] = "No buckets" });
            })) == usage,
            "a group or a window the widget does not know is passed over");
        Check(AntigravityUsageReport.Parse(Edited(report => report["status"] = "ERROR")) is null, "a report that did not succeed has no usage");
        Check(AntigravityUsageReport.Parse("Error: something went wrong") is null && AntigravityUsageReport.Parse("") is null &&
              AntigravityUsageReport.Parse("[1, 2]") is null,
            "output that is not a JSON report has no usage");
        foreach (var (what, report, message) in new (string, string, string)[]
                 {
                     ("a model turn", Edited(report => report["num_turns"] = 1), AntigravityUsageReport.TurnRan),
                     ("tokens used", Edited(report => report["usage"]!["total_tokens"] = 1520), AntigravityUsageReport.TurnRan),
                     ("another command", Edited(report => report["command"]!["name"] = "help"), AntigravityUsageReport.NotUsage),
                     ("no command", Edited(report => report.AsObject().Remove("command")), AntigravityUsageReport.NotUsage),
                     ("groups that are not a list", Edited(report => report["command"]!["data"]!["groups"] = "none"),
                         AntigravityUsageReport.NotUsage),
                     ("buckets that are not a list", Edited(report => Groups(report)[0]!["buckets"] = 3), AntigravityUsageReport.NotUsage),
                     ("no window it knows", Edited(report =>
                     {
                         foreach (var id in new[] { "gemini-weekly", "gemini-5h", "3p-weekly", "3p-5h" }) Bucket(report, id)["id"] = id + "-v2";
                     }), AntigravityUsageReport.NotUsage),
                     ("one window twice", Edited(report => Bucket(report, "3p-5h")["id"] = "gemini-weekly"), AntigravityUsageReport.NotUsage),
                     ("a share above one", Edited(report => Bucket(report, "gemini-5h")["remaining_fraction"] = 1.5), AntigravityUsageReport.NotUsage),
                     ("a share below zero", Edited(report => Bucket(report, "gemini-5h")["remaining_fraction"] = -0.25), AntigravityUsageReport.NotUsage),
                     ("a share in words", Edited(report => Bucket(report, "gemini-5h")["remaining_fraction"] = "half"), AntigravityUsageReport.NotUsage)
                 })
            CheckThrows(() => AntigravityUsageReport.Parse(report), message, $"a successful report with {what} is refused");

        Check(AntigravityUsageReport.SaysSignedOut("Error: You are not logged into Antigravity.") &&
              AntigravityUsageReport.SaysSignedOut("PLEASE SIGN IN to continue") &&
              !AntigravityUsageReport.SaysSignedOut("quota service unavailable"),
            "a signed-out CLI is recognised by what it says");
        Check(AntigravityUsageReport.Excerpt($$$"""{"status":"ERROR","error":{"message":"quota\tservice unavailable for {{{Address}}}"}}""") ==
              "quota service unavailable for ***",
            "what the CLI said is shown on one line, without the address");
        Check(AntigravityUsageReport.Excerpt($"first line\r\n\r\n  second line for <{Address}>  ") == "first line second line for <***>",
            "plain output is joined into one line, without the address");
        Check(AntigravityUsageReport.Excerpt(new string('x', 400)) is { Length: 161 } cut && cut.EndsWith('…'), "a long message is cut");
    }

    private static void CheckIdentity()
    {
        var identity = AntigravityIdentity.FromLog(["I1121 14:02:10.000001  4242 main.go:52] starting", LogLine(Address),
            "I1121 14:02:12.500000  4242 usage.go:90] done"]) ?? throw Fail("the log line names the account");
        Check(identity.Key == AntigravityIdentity.Key(Address) && Regex.IsMatch(identity.Key, "^[0-9A-F]{64}$"),
            "the account's key is an opaque SHA-256 in hex");
        Check(identity.Hint == "r***@example.test", "the hint is the first character and the domain");
        Check(AntigravityIdentity.Key("RIVER.FOX@Example.Test") == identity.Key && AntigravityIdentity.Key("  " + Address + " ") == identity.Key,
            "the key ignores case and spaces round the address");
        Check(AntigravityIdentity.Key(OtherAddress) != identity.Key, "another account has another key");
        Check(AntigravityIdentity.FromLog([LogLine(Address), LogLine(Address.ToUpperInvariant())]) == identity,
            "the same account named twice is one account");
        CheckThrows(() => AntigravityIdentity.FromLog([LogLine(Address), LogLine(OtherAddress)]), AntigravityIdentity.Ambiguous,
            "a log naming two accounts is refused");
        Check(AntigravityIdentity.FromLog([
                  "I1121 main.go:52] starting", "applyAuthResult: email=, authMethod=consumer",
                  "applyAuthResult: email=not-an-address, authMethod=consumer", "applyauthresult: email=" + Address
              ]) is null && AntigravityIdentity.FromLog([]) is null,
            "a log that names no account gives none");
        Check(AntigravityIdentity.Hint(OtherAddress) == "m***@example.test", "the hint is in lower case");
        Check(AntigravityIdentity.Hint("_ops@example.test") == "****@example.test", "a first character that is not a letter or digit is masked too");
        var longDomain = "a@" + new string('x', 58) + ".test";
        Check(AntigravityIdentity.Hint(longDomain) == "a*** · " + AntigravityIdentity.Key(longDomain)[..8],
            "a domain too long to show gives way to the start of the key");
        Check(AntigravityIdentity.Hint("user@odd_name.test") == "u*** · " + AntigravityIdentity.Key("user@odd_name.test")[..8],
            "a domain with characters a domain does not have gives way to the start of the key");
    }

    private static void CheckLocate()
    {
        const string profile = @"X:\profile", local = @"X:\local";
        const string installed = @"X:\profile\.gemini\antigravity-cli\bin\agy.exe", program = @"X:\local\agy\bin\agy.exe";
        static Func<string, bool> Present(params string[] files) => file => files.Contains(file, StringComparer.OrdinalIgnoreCase);
        string? Locate(string? configured, string? path, Func<string, bool> exists) =>
            AntigravityCli.Locate(configured, profile, local, path, exists);
        Check(Locate(@"Y:\tools\agy.exe", @"Z:\bin", Present(@"Y:\tools\agy.exe", installed, program, @"Z:\bin\agy.exe")) == @"Y:\tools\agy.exe",
            "AGY_EXE comes first");
        Check(Locate(null, @"Z:\bin", Present(installed, program, @"Z:\bin\agy.exe")) == installed, "then the installer's place in the profile");
        Check(Locate(null, @"Z:\bin", Present(program, @"Z:\bin\agy.exe")) == program, "then the per-user program folder");
        Check(Locate(null, @"Z:\bin", Present(@"Z:\bin\agy.exe")) == @"Z:\bin\agy.exe", "then the PATH");
        Check(Locate(@"Y:\tools\agy.exe", null, Present(installed)) == installed, "an AGY_EXE that is not there is passed over");
        Check(Locate(@"""Y:\tools\agy.exe""", null, Present(@"Y:\tools\agy.exe", installed)) == @"Y:\tools\agy.exe", "a quoted AGY_EXE is unquoted");
        Check(Locate(@"Y:\tools\agy.cmd", null, Present(@"Y:\tools\agy.cmd", installed)) == installed,
            "a script is never run: it would need a shell");
        Check(Locate("agy.exe", null, Present("agy.exe", installed)) == installed, "a relative AGY_EXE is passed over");
        Check(Locate(null, @"bin;.;""Z:\quoted dir"";W:\other",
                  Present(@"bin\agy.exe", @".\agy.exe", @"Z:\quoted dir\agy.exe", @"W:\other\agy.exe")) == @"Z:\quoted dir\agy.exe",
            "relative PATH folders are skipped and quotes are taken off, in the PATH's order");
        Check(Locate(null, @"Z:\bin", _ => false) is null, "no agy anywhere is none");
    }

    private static void CheckStartInfo()
    {
        var info = AntigravityCli.CreateStartInfo(@"X:\agy\agy.exe", @"X:\run\usage-1.log", @"X:\run");
        Check(!info.UseShellExecute && info.CreateNoWindow && info.WindowStyle == ProcessWindowStyle.Hidden,
            "agy is started directly, with no shell and no window");
        Check(info.FileName == @"X:\agy\agy.exe" && info.WorkingDirectory == @"X:\run" && info.Arguments.Length == 0 &&
              info.ArgumentList.SequenceEqual(["-p", "/usage", "--output-format", "json", "--log-file", @"X:\run\usage-1.log"]),
            "only the usage command, as a list of arguments");
        Check(info.Environment["AGY_CLI_DISABLE_AUTO_UPDATE"] == "true", "a read never updates the CLI");
        Check(info.RedirectStandardInput && info.RedirectStandardOutput && info.RedirectStandardError,
            "its input is closed and what it says is read");
    }

    private static void CheckMessages()
    {
        var language = UiText.Language;
        UiText.SetLanguage("en");
        try
        {
            foreach (var message in new[]
                     {
                         AntigravityUsageReader.Missing, AntigravityUsageReader.SignedOut, AntigravityUsageReader.Late,
                         AntigravityUsageReader.Unidentified, AntigravityUsageReader.FailedPlain, AntigravityUsageReport.NotUsage,
                         AntigravityUsageReport.TurnRan, AntigravityIdentity.Ambiguous, AntigravityAccountStore.Invalid,
                         AntigravityAccountStore.Busy, AntigravityAccountStore.Full, AntigravityAccountStore.NotFound,
                         AntigravityAccountStore.BadName, AntigravityAccountStore.Redirected, "Antigravity CLI를 시작하지 못했습니다.",
                         string.Format(CultureInfo.InvariantCulture, AntigravityUsageReader.FailedWith, "quota service unavailable")
                     })
            {
                var shown = UiText.Message(message);
                Check(shown != message && !shown.Any(character => character is >= '가' and <= '힣'), $"\"{message}\" is shown in English");
            }
        }
        finally { UiText.SetLanguage(language); }
    }

    private static void CheckStore(string root)
    {
        var store = new AntigravityAccountStore(root);
        Check(store.ListAccounts().Count == 0, "a new store has no accounts");
        var key = AntigravityIdentity.Key(Address);
        var hint = AntigravityIdentity.Hint(Address);
        var usage = new AntigravityUsage(new(0.64, new DateTimeOffset(2026, 11, 26, 8, 15, 0, TimeSpan.Zero)),
            new(0.125, new DateTimeOffset(2026, 11, 21, 11, 40, 0, TimeSpan.Zero)), new(1, null), null);
        var seen = new DateTimeOffset(2026, 11, 21, 5, 30, 0, TimeSpan.Zero);
        var (first, firstSeen) = store.RecordObservation(key, hint, usage, seen);
        Check(firstSeen && first == new AntigravityAccount(key, null, hint, usage, seen), "an account read for the first time is added, unnamed");
        Check(new AntigravityAccountStore(root).ListAccounts().SequenceEqual([first]), "an account reads back as it was kept");
        var file = Path.Combine(root, "accounts.json");
        var text = File.ReadAllText(file);
        Check(!text.Contains("river", StringComparison.OrdinalIgnoreCase) && !Regex.IsMatch(text, @"[A-Za-z0-9._%+-]@"),
            "the file holds the masked hint, never the address");

        var security = new DirectoryInfo(root).GetAccessControl();
        var rules = security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToList();
        var owner = WindowsIdentity.GetCurrent().User!;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        Check(security.AreAccessRulesProtected && rules.Count > 0 && rules.All(rule => rule.AccessControlType == AccessControlType.Allow &&
                  (rule.IdentityReference.Equals(owner) || rule.IdentityReference.Equals(system))) &&
              rules.Any(rule => rule.IdentityReference.Equals(owner)),
            "the folder is the user's own (and SYSTEM's), with nothing inherited");

        Check(store.Rename(key, "  Work  ").Label == "Work", "a name is kept without the spaces round it");
        var later = usage with { GeminiWeekly = new(0.5, usage.GeminiWeekly!.ResetsAt) };
        var (again, firstAgain) = store.RecordObservation(key, "r***@example.test", later, seen.AddMinutes(5));
        Check(!firstAgain && again == new AntigravityAccount(key, "Work", hint, later, seen.AddMinutes(5)),
            "a later read replaces the usage and keeps the name");
        foreach (var name in new[] { "", "   ", new string('가', 41), "two\nlines", "tab\there" })
            CheckThrows(() => store.Rename(key, name), AntigravityAccountStore.BadName, $"the name \"{name}\" is refused");
        Check(store.Rename(key, new string('가', 40)).Label!.Length == 40, "a name of 40 characters is kept");
        CheckThrows(() => store.Rename(AntigravityIdentity.Key(OtherAddress), "Other"), AntigravityAccountStore.NotFound,
            "an account never read cannot be named");

        // A file that is not the store's stays as it is, and nothing is written over it.
        var good = File.ReadAllBytes(file);
        var json = Encoding.UTF8.GetString(good);
        string Changed(Action<JsonNode> change) { var node = JsonNode.Parse(json)!; change(node); return node.ToJsonString(); }
        foreach (var (what, broken) in new (string, string)[]
                 {
                     ("cut short", json[..(json.Length / 2)]),
                     ("another version", Changed(node => node["Version"] = 2)),
                     ("a key in lower case", json.Replace(key, key.ToLowerInvariant(), StringComparison.Ordinal)),
                     ("one account twice", Changed(node => node["Accounts"]!.AsArray().Add(node["Accounts"]![0]!.DeepClone()))),
                     ("usage without its time", Changed(node => node["Accounts"]![0]!["ObservedAt"] = null)),
                     ("a share above one", Changed(node => node["Accounts"]![0]!["Usage"]!["GeminiWeekly"]!["RemainingFraction"] = 1.5)),
                     ("a hint with a line break", Changed(node => node["Accounts"]![0]!["Hint"] = "r***\n@example.test"))
                 })
        {
            File.WriteAllText(file, broken);
            var before = File.ReadAllBytes(file);
            CheckThrows<InvalidDataException>(() => store.ListAccounts(), AntigravityAccountStore.Invalid, $"a file {what} is refused");
            CheckThrows<InvalidDataException>(() => store.RecordObservation(key, hint, usage, seen), AntigravityAccountStore.Invalid,
                $"a file {what} is not written over");
            Check(File.ReadAllBytes(file).SequenceEqual(before), $"a file {what} is left as it was");
        }
        File.WriteAllBytes(file, good);

        for (var index = 1; index < AntigravityAccountStore.MaxAccounts; index++)
        {
            var address = $"user{index}@example.test";
            Check(store.RecordObservation(AntigravityIdentity.Key(address), AntigravityIdentity.Hint(address), usage, seen).FirstSeen,
                "each new account is added");
        }
        CheckThrows(() => store.RecordObservation(AntigravityIdentity.Key(OtherAddress), AntigravityIdentity.Hint(OtherAddress), usage, seen),
            AntigravityAccountStore.Full, "a twenty-first account is not added");
        Check(!store.RecordObservation(key, hint, usage, seen.AddHours(1)).FirstSeen && store.ListAccounts().Count == AntigravityAccountStore.MaxAccounts,
            "a full store still records the accounts it has");
        store.Remove(key);
        Check(store.ListAccounts() is { Count: AntigravityAccountStore.MaxAccounts - 1 } left && left.All(account => account.IdentityKey != key),
            "removing an account forgets that row only");
        CheckThrows(() => store.Remove(key), AntigravityAccountStore.NotFound, "an account removed is not there to remove again");
        Check(store.RecordObservation(key, hint, usage, seen) is { FirstSeen: true, Account.Label: null },
            "an account read again after it was removed comes back new, unnamed");
        Check(!Directory.EnumerateFiles(root, ".accounts-*.tmp").Any(), "no temporary file is left behind");
    }

    private static async Task CheckReaderAsync(string root)
    {
        var store = new AntigravityAccountStore(root);
        var run = store.PrepareRunDirectory();
        var agy = StandInPath;
        var time = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var account = AntigravityIdentity.Key(Address);
        using var reader = new AntigravityUsageReader(store, () => agy, TimeSpan.FromSeconds(30), time);

        // A read left behind by a run that never finished is cleared; one that may still be running is not.
        var old = Path.Combine(run, "usage-" + new string('0', 32) + ".log");
        var fresh = Path.Combine(run, "usage-" + new string('1', 32) + ".log");
        File.WriteAllText(old, LogLine(OtherAddress)); File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddMinutes(-10));
        File.WriteAllText(fresh, "");
        Plan(run, new AgyPlan(Sample, Log: ["I1121 main.go:52] starting", LogLine(Address)]));
        var read = await reader.ReadAsync();
        Check(read.Kind == AntigravityReadKind.Success && read.FirstSeen && read.Detail is null &&
              read.Account == new AntigravityAccount(account, null, "r***@example.test", AntigravityUsageReport.Parse(Sample), time.GetUtcNow()),
            $"a read records the account its log names, with what /usage reported ({read})");
        var runs = Runs(run);
        Check(runs.Count == 1, "one read starts agy once");
        var started = runs[0];
        Check(started.Args is ["-p", "/usage", "--output-format", "json", "--log-file", var log] &&
              string.Equals(Path.GetDirectoryName(log), run, StringComparison.OrdinalIgnoreCase) &&
              Regex.IsMatch(Path.GetFileName(log), "^usage-[0-9a-f]{32}\\.log$") &&
              string.Equals(Path.TrimEndingDirectorySeparator(started.Directory), run, StringComparison.OrdinalIgnoreCase),
            "agy is given the usage command and a log of its own in the private run folder");
        Check(started.AutoUpdate == "true" && started.Input == 0, "agy is told not to update itself, and its input is closed");
        Check(!File.Exists(started.Args[5]) && !File.Exists(old) && File.Exists(fresh),
            "the log that named the account is gone, and so is one left behind");
        File.Delete(fresh);
        Check(!File.ReadAllText(Path.Combine(root, "accounts.json")).Contains("river", StringComparison.OrdinalIgnoreCase),
            "the store never holds the address");

        store.Rename(account, "Main");
        Plan(run, new AgyPlan(Sample, Log: [LogLine(Address.ToUpperInvariant())]));
        read = await reader.ReadAsync();
        Check(read is { Kind: AntigravityReadKind.Success, FirstSeen: false, Account.Label: "Main" }, "a later read of the same account keeps its name");
        Plan(run, new AgyPlan(Sample, Log: [LogLine(OtherAddress)]));
        read = await reader.ReadAsync();
        Check(read is { Kind: AntigravityReadKind.Success, FirstSeen: true, Account.Hint: "m***@example.test" } &&
              store.ListAccounts().Count == 2, "another account signed in is a second account");

        var kept = store.ListAccounts();
        foreach (var (what, plan, kind, detail) in new (string, AgyPlan, AntigravityReadKind, string)[]
                 {
                     ("a CLI that is not signed in", new AgyPlan(Stderr: "Error: You are not logged into Antigravity.\n", Exit: 1),
                         AntigravityReadKind.NotSignedIn, AntigravityUsageReader.SignedOut),
                     ("a sign-in it cannot start", new AgyPlan("""{"status":"ERROR","error":{"message":"Cannot complete interactive login without a terminal."}}""", Exit: 1),
                         AntigravityReadKind.NotSignedIn, AntigravityUsageReader.SignedOut),
                     ("usage with no account in the log", new AgyPlan(Sample, Log: ["I1121 main.go:52] starting"]),
                         AntigravityReadKind.Malformed, AntigravityUsageReader.Unidentified),
                     ("a log naming two accounts", new AgyPlan(Sample, Log: [LogLine(Address), LogLine(OtherAddress)]),
                         AntigravityReadKind.Malformed, AntigravityIdentity.Ambiguous),
                     ("a model turn", new AgyPlan(Sample.Replace("\"num_turns\":0", "\"num_turns\":1", StringComparison.Ordinal), Log: [LogLine(Address)]),
                         AntigravityReadKind.Malformed, AntigravityUsageReport.TurnRan),
                     ("output past the cap", new AgyPlan(Sample, Log: [LogLine(Address)], Pad: AntigravityCli.MaxOutputChars),
                         AntigravityReadKind.Malformed, AntigravityUsageReport.NotUsage),
                     ("an error it explains", new AgyPlan($$$"""{"status":"ERROR","error":{"message":"quota service unavailable for {{{Address}}}"}}""", Exit: 1, Log: [LogLine(Address)]),
                         AntigravityReadKind.Failed, string.Format(CultureInfo.InvariantCulture, AntigravityUsageReader.FailedWith, "quota service unavailable for ***")),
                     ("an exit with nothing said", new AgyPlan(Exit: 2),
                         AntigravityReadKind.Failed, AntigravityUsageReader.FailedPlain)
                 })
        {
            Plan(run, plan);
            read = await reader.ReadAsync();
            Check(read.Kind == kind && read.Detail == detail && read.Account is null, $"{what} reads as {kind} ({read})");
            Check(store.ListAccounts().SequenceEqual(kept), $"{what} leaves the accounts as they were");
            Check(!Directory.EnumerateFiles(run, "usage-*.log").Any(), $"{what} leaves no log behind");
        }

        foreach (var missing in new Func<string?>[] { () => null, () => Path.Combine(run, "nowhere", "agy.exe") })
        {
            using var without = new AntigravityUsageReader(store, missing);
            Check(await without.ReadAsync() is { Kind: AntigravityReadKind.ExecutableMissing, Detail: AntigravityUsageReader.Missing },
                "no agy is a missing CLI");
        }

        // Presses while a read runs wait for that read; one that stops waiting leaves it running.
        var before = Runs(run).Count;
        Plan(run, new AgyPlan(Sample, Log: [LogLine(Address)], SleepMs: 1500));
        var one = reader.ReadAsync();
        var two = reader.ReadAsync();
        Check(ReferenceEquals(one, two) && reader.IsReading, "a second press joins the read under way");
        using (var impatient = new CancellationTokenSource(TimeSpan.FromMilliseconds(300)))
        {
            try { await reader.ReadAsync(impatient.Token); throw Fail("a caller that stops waiting sees its wait cancelled"); }
            catch (OperationCanceledException) { }
        }
        Check(reader.IsReading, "a caller that stops waiting leaves the read running");
        Check((await one).Kind == AntigravityReadKind.Success && ReferenceEquals(await one, await two) && !reader.IsReading,
            "every press gets the one read's result");
        Check(Runs(run).Count == before + 1, "three presses during one read started agy once");

        // Whatever agy starts ends with the read: when agy has answered, when it is late, and when the widget closes.
        Plan(run, new AgyPlan(Sample, Log: [LogLine(Address)], Child: true));
        read = await reader.ReadAsync();
        Check(read.Kind == AntigravityReadKind.Success && Runs(run)[^1] is { Child: { } helper } && Gone(helper),
            "a process agy left running is ended with the read");
        using (var slow = new AntigravityUsageReader(store, () => agy, TimeSpan.FromSeconds(3), time))
        {
            Plan(run, new AgyPlan(Sample, Log: [LogLine(Address)], SleepMs: 30_000, Child: true));
            var watch = Stopwatch.StartNew();
            read = await slow.ReadAsync();
            Check(read is { Kind: AntigravityReadKind.TimedOut, Detail: AntigravityUsageReader.Late } && watch.Elapsed < TimeSpan.FromSeconds(15),
                $"a read that does not answer in time is late ({read}, {watch.Elapsed.TotalSeconds:0.0} s)");
            Check(Runs(run)[^1] is { Child: { } child } last && Gone(last.Pid) && Gone(child), "a late agy and what it started are ended");
            Check(!Directory.EnumerateFiles(run, "usage-*.log").Any(), "a late read leaves no log behind");
        }
        var closing = new AntigravityUsageReader(store, () => agy, TimeSpan.FromSeconds(60), time);
        before = Runs(run).Count;
        var pending = closing.ReadAsync();
        var wait = Stopwatch.StartNew();
        while (Runs(run).Count == before && wait.Elapsed < TimeSpan.FromSeconds(15)) await Task.Delay(50);
        closing.Dispose();
        try { await pending.WaitAsync(TimeSpan.FromSeconds(15)); throw Fail("a read under way ends when the reader is disposed"); }
        catch (OperationCanceledException) { }
        Check(Runs(run)[^1] is { Child: { } orphan } ended && Gone(ended.Pid) && Gone(orphan) &&
              !Directory.EnumerateFiles(run, "usage-*.log").Any(),
            "closing the widget during a read ends agy, what it started and its log");
        try { _ = closing.ReadAsync(); throw Fail("a disposed reader starts no read"); }
        catch (ObjectDisposedException) { }

        // A store that cannot take the account says why, and keeps its file.
        var file = Path.Combine(root, "accounts.json");
        var good = File.ReadAllBytes(file);
        File.WriteAllText(file, "{");
        Plan(run, new AgyPlan(Sample, Log: [LogLine(Address)]));
        read = await reader.ReadAsync();
        Check(read is { Kind: AntigravityReadKind.Failed, Detail: AntigravityAccountStore.Invalid } && File.ReadAllText(file) == "{",
            "a store file that cannot be read is reported and kept");
        File.WriteAllBytes(file, good);
        var usage = AntigravityUsageReport.Parse(Sample)!;
        for (var index = store.ListAccounts().Count; index < AntigravityAccountStore.MaxAccounts; index++)
            store.RecordObservation(AntigravityIdentity.Key($"user{index}@example.test"), AntigravityIdentity.Hint($"user{index}@example.test"),
                usage, time.GetUtcNow());
        Plan(run, new AgyPlan(Sample, Log: [LogLine("sky.lark@example.test")]));
        Check(await reader.ReadAsync() is { Kind: AntigravityReadKind.Failed, Detail: AntigravityAccountStore.Full },
            "a twenty-first account is reported, not added");
    }

    /// <summary>One real read of the signed-in account, from a store under the test root that is deleted afterwards. What
    /// it prints names the account only in its masked form.</summary>
    internal static async Task LiveCheckAsync()
    {
        var root = Path.Combine(TestBase(), "antigravity-live-" + Guid.NewGuid().ToString("N")[..8]);
        try
        {
            var store = new AntigravityAccountStore(root);
            var executable = AntigravityCli.Locate();
            Console.WriteLine("agy: " + (executable is null ? "not found" : "found at " + Where(executable)));
            using var reader = new AntigravityUsageReader(store);
            var watch = Stopwatch.StartNew();
            var read = await reader.ReadAsync();
            Console.WriteLine($"kind: {read.Kind} · {watch.Elapsed.TotalSeconds:0.0} s · first seen: {read.FirstSeen}");
            if (read.Detail is { } detail) Console.WriteLine("detail: " + detail);
            if (read.Account is { } account)
            {
                Console.WriteLine($"account: {account.Hint} · key {account.IdentityKey[..8]}…");
                foreach (var (name, window) in new[]
                         {
                             ("Gemini weekly", account.Usage?.GeminiWeekly), ("Gemini 5 h", account.Usage?.GeminiFiveHour),
                             ("Claude and GPT weekly", account.Usage?.ThirdPartyWeekly), ("Claude and GPT 5 h", account.Usage?.ThirdPartyFiveHour)
                         })
                    Console.WriteLine($"  {name}: " + (window is null ? "not reported"
                        : $"{window.RemainingPercent} % left · " + (window.ResetsAt is { } at ? "resets " + at.ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture) + " UTC" : "not used yet")));
            }
            var left = Directory.Exists(store.RunDirectory)
                ? Directory.EnumerateFiles(store.RunDirectory).Select(Path.GetFileName).ToArray()
                : Array.Empty<string?>();
            Console.WriteLine("run folder after the read: " + (left.Length == 0 ? "empty" : string.Join(", ", left)));
            var file = Path.Combine(root, "accounts.json");
            Console.WriteLine("store holds an address: " +
                (File.Exists(file) ? Regex.IsMatch(File.ReadAllText(file), @"[A-Za-z0-9._%+-]@") ? "yes" : "no" : "no store written"));
        }
        finally { TryDelete(root); }
    }

    private static string Where(string executable)
    {
        bool Is(string? path) => path is not null && string.Equals(executable, path.Trim().Trim('"'), StringComparison.OrdinalIgnoreCase);
        if (Is(Environment.GetEnvironmentVariable(AntigravityCli.ExecutableVariable))) return "AGY_EXE";
        if (Is(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".gemini", "antigravity-cli", "bin", "agy.exe")))
            return @"%USERPROFILE%\.gemini\antigravity-cli\bin\agy.exe";
        if (Is(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "agy", "bin", "agy.exe")))
            return @"%LOCALAPPDATA%\agy\bin\agy.exe";
        return "a folder on the PATH";
    }

    /// <summary>When the reader under test starts this program as agy: it does what the plan in its working folder says
    /// and notes how it was started. Null when this program was started as anything else.</summary>
    internal static int? StandIn(string[] args)
    {
        if (args is ["--fake-agy-child"]) { Thread.Sleep(TimeSpan.FromSeconds(60)); return 0; }
        if (args is not ["-p", "/usage", "--output-format", "json", "--log-file", var log]) return null;
        var directory = Environment.CurrentDirectory;
        var planPath = Path.Combine(directory, PlanFile);
        if (!File.Exists(planPath)) return null;
        var plan = JsonSerializer.Deserialize<AgyPlan>(File.ReadAllText(planPath))!;
        // The reader closes the input once agy belongs to its job, so whatever starts after this is in the job too.
        var input = Console.In.ReadToEnd();
        int? child = null;
        if (plan.Child)
        {
            using var helper = Process.Start(new ProcessStartInfo(StandInPath)
            {
                UseShellExecute = false, CreateNoWindow = true, ArgumentList = { "--fake-agy-child" }
            })!;
            child = helper.Id;
        }
        File.AppendAllText(Path.Combine(directory, RunsFile), JsonSerializer.Serialize(new AgyRun(Environment.ProcessId, args,
            Environment.GetEnvironmentVariable("AGY_CLI_DISABLE_AUTO_UPDATE"), input.Length, directory, child)) + "\n");
        if (plan.Log is { } lines) File.WriteAllLines(log, lines);
        if (plan.SleepMs > 0) Thread.Sleep(plan.SleepMs);
        using (var output = Console.OpenStandardOutput()) output.Write(Encoding.UTF8.GetBytes(plan.Stdout + new string(' ', plan.Pad)));
        using (var error = Console.OpenStandardError()) error.Write(Encoding.UTF8.GetBytes(plan.Stderr));
        return plan.Exit;
    }

    // This program's own executable, however the tests were started (dotnet run starts it; dotnet <dll> would not).
    private static string StandInPath => Path.Combine(AppContext.BaseDirectory, "WeeklyUsageIndicator.Tests.exe");

    internal sealed record AgyPlan(string Stdout = "", string Stderr = "", int Exit = 0, string[]? Log = null, int SleepMs = 0,
        bool Child = false, int Pad = 0);

    internal sealed record AgyRun(int Pid, string[] Args, string? AutoUpdate, int Input, string Directory, int? Child);

    private static void Plan(string run, AgyPlan plan) => File.WriteAllText(Path.Combine(run, PlanFile), JsonSerializer.Serialize(plan));

    private static List<AgyRun> Runs(string run)
    {
        var path = Path.Combine(run, RunsFile);
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                if (!File.Exists(path)) return [];
                using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                using var reader = new StreamReader(stream, Encoding.UTF8);
                return reader.ReadToEnd().Split('\n', StringSplitOptions.RemoveEmptyEntries)
                    .Select(line => JsonSerializer.Deserialize<AgyRun>(line)!).ToList();
            }
            // The stand-in may be writing its line just then.
            catch (Exception exception) when (exception is IOException or JsonException && attempt < 40) { Thread.Sleep(25); }
        }
    }

    private static bool Gone(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.HasExited;
        }
        catch (ArgumentException) { return true; }
        catch (InvalidOperationException) { return true; }
    }

    private static string LogLine(string email) =>
        $"I1121 14:02:11.123456  4242 auth_manager.go:311] applyAuthResult: email={email}, authMethod=consumer";

    private static string Edited(Action<JsonNode> change)
    {
        var report = JsonNode.Parse(Sample)!;
        change(report);
        return report.ToJsonString();
    }

    private static JsonArray Groups(JsonNode report) => report["command"]!["data"]!["groups"]!.AsArray();

    private static JsonObject Bucket(JsonNode report, string id) => Groups(report)
        .Where(group => group?["buckets"] is JsonArray)
        .SelectMany(group => group!["buckets"]!.AsArray())
        .Single(bucket => (string?)bucket?["id"] == id)!.AsObject();

    /// <summary>Deletes a fixture; a failure to delete never hides the failure that ended the test.</summary>
    private static void TryDelete(string root)
    {
        try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        { Console.WriteLine($"note: the fixture {root} was left behind ({exception.Message})"); }
    }

    private static string TestBase() => Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT")
        ?? Path.Combine(Path.GetTempPath(), "gfs-agent", "antigravity-tests");

    private static void CheckThrows(Action action, string message, string what) => CheckThrows<Exception>(action, message, what);

    private static void CheckThrows<TException>(Action action, string message, string what) where TException : Exception
    {
        try { action(); }
        catch (TException exception) when (exception.Message == message) { return; }
        catch (Exception exception) { throw Fail($"{what}: {exception.GetType().Name} \"{exception.Message}\""); }
        throw Fail(what + ": nothing was thrown");
    }

    private static InvalidOperationException Fail(string message) => new("Antigravity: " + message);

    private static void Check(bool condition, string message) { if (!condition) throw Fail(message); }
}
