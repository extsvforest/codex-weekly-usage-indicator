using System.Globalization;
using System.Text;
using WeeklyUsageIndicator;

/// <summary>What the scheduled-task alignment reads of the app, in a fake log folder only (GFS_ACCOUNT_TEST_ROOT): lines
/// the app's log lost leave the list it holds unknown until its next load; the running app is the package whose bundle
/// was checked; and the bundle's fixed points, read in memory against the installed 6.28899.6.0 (skipped otherwise).
/// Accounts, folders and log lines are made up.</summary>
internal static class ClaudeScheduledProbeTests
{
    internal static Task RunAsync()
    {
        TestMissingLinesWarning();
        TestUnfinishedLastLine();
        TestRotationBetweenReads();
        TestRunStartUndecided();
        TestRunsFrom();
        TestFixedPoints();
        return Task.CompletedTask;
    }

    // The app's logger, after a write it could not make, writes this before the next line it can (6.28899).
    private const string Warning = "\r\n[warn] Lines are missing here: this log could not be written (ENOSPC)\r\n";

    private static void TestMissingLinesWarning()
    {
        using var logs = new Logs();
        var (a, b) = (Account("A"), Account("B"));
        var watcher = new ClaudeDesktopLogWatcher(logs.Root);
        logs.Append(logs.Reset(1), logs.Initialized(2, a), logs.Line(5, "unrelated"));
        watcher.Update(logs.Start, [a, b]);
        Check(watcher.Scheduled is { Known: true, Pending: false, Loaded: { } loaded } && loaded == a.Key, "the fixture run has A's list loaded");
        // The warning opens the next read: whatever the app could not write between the two may have been a switch.
        logs.Write(Warning + logs.Line(9, "unrelated") + "\r\n");
        watcher.Update(logs.Start, [a, b]);
        var mark = watcher.Scheduled.Mark;
        Check(watcher.Scheduled is { Known: true, Pending: true, Loaded: null } && !watcher.LostLines,
            "after lines the app could not write, the list it holds is unknown");
        logs.Append(logs.Line(10, "unrelated"));
        watcher.Update(logs.Start, [a, b]);
        Check(watcher.Scheduled is { Pending: true, Loaded: null } && watcher.Scheduled.Mark == mark,
            "it stays unknown over later lines, the warning counted once");
        logs.Append(logs.Initialized(12, b));
        watcher.Update(logs.Start, [a, b]);
        var view = watcher.Scheduled;
        Check(view is { Known: true, Pending: false, Loaded: { } next } && next == b.Key, "the next load names the list again");
        // The line read as it stands, the same in one read through Apply.
        var log = new ClaudeScheduledLog();
        log.Start();
        ClaudeDesktopLogWatcher.Apply(ClaudeDesktopAccountState.Unknown,
            [logs.Initialized(2, a), "", "[warn] Lines are missing here: this log could not be written (EIO)", logs.Line(4, "unrelated")],
            logs.Start, [a, b], null, log);
        Check(log.View(true) is { Pending: true, Loaded: null, Mark: 2, LastLine: { } last } && last == new DateTimeOffset(logs.Start.AddSeconds(2)),
            "an undated warning between dated lines leaves the list unknown, dated as the line before it");
    }

    private static void TestUnfinishedLastLine()
    {
        using var logs = new Logs();
        var (a, b) = (Account("A"), Account("B"));
        var watcher = new ClaudeDesktopLogWatcher(logs.Root);
        logs.Append(logs.Reset(1), logs.Initialized(2, a));
        watcher.Update(logs.Start, [a, b]);
        Check(watcher.Scheduled is { Pending: false, Loaded: { } loaded } && loaded == a.Key, "the fixture run has A's list loaded");
        // A last line cut before its newline may be a reset the app could not finish writing.
        logs.Write(logs.Line(6, "[CCDScheduledTasks] Res"));
        watcher.Update(logs.Start, [a, b]);
        var mark = watcher.Scheduled.Mark;
        Check(watcher.Scheduled is { Known: true, Pending: true, Loaded: null } && !watcher.LostLines,
            "an unfinished last line leaves the list unknown");
        watcher.Update(logs.Start, [a, b]);
        Check(watcher.Scheduled.Mark == mark, "the same unfinished line counts once");
        // The app writes on after it: the cut line never reads as a reset or a load, and the list stays unknown.
        logs.Write(logs.Line(8, "unrelated") + "\r\n");
        watcher.Update(logs.Start, [a, b]);
        Check(watcher.Scheduled is { Pending: true, Loaded: null }, "a cut line finished by the next write leaves it unknown");
        logs.Append(logs.Initialized(9, b));
        watcher.Update(logs.Start, [a, b]);
        Check(watcher.Scheduled is { Pending: false, Loaded: { } next } && next == b.Key, "the next load names the list again");
        // A load caught while the app writes it is unknown until its newline, and then names the list.
        logs.Append(logs.Reset(20));
        logs.Write(logs.Initialized(21, a));
        watcher.Update(logs.Start, [a, b]);
        Check(watcher.Scheduled is { Pending: true, Loaded: null }, "a load not finished yet names no list");
        logs.Write("\r\n");
        watcher.Update(logs.Start, [a, b]);
        Check(watcher.Scheduled is { Pending: false, Loaded: { } again } && again == a.Key, "finished, it is the next load");
    }

    private static void TestRotationBetweenReads()
    {
        using var logs = new Logs();
        var (a, b) = (Account("A"), Account("B"));
        var watcher = new ClaudeDesktopLogWatcher(logs.Root);
        logs.Append(logs.Reset(1), logs.Initialized(2, a));
        watcher.Update(logs.Start, [a, b]);
        // An ordinary rotation: the rest of the file read so far, then the new one, nothing lost.
        logs.Append(logs.Line(4, "unrelated"));
        logs.Rotate(logs.Line(5, "unrelated"));
        watcher.Update(logs.Start, [a, b]);
        Check(!watcher.LostLines && watcher.Scheduled is { Known: true, Pending: false, Loaded: { } loaded } && loaded == a.Key,
            "a rotation between updates loses nothing");
        // The app rotates once more while the watcher reads: the file that was main.log meanwhile (a switch to B) is never
        // read, and main1.log is neither the file finished nor the one read after it.
        logs.Rotate(logs.Reset(10), logs.Initialized(11, b));
        watcher.BetweenFilesForTest = () => { watcher.BetweenFilesForTest = null; logs.Rotate(logs.Reset(13)); };
        watcher.Update(logs.Start, [a, b]);
        Check(watcher.LostLines && watcher.Scheduled is { Known: false }, "a rotation between the two reads loses lines: unknown");
        watcher.Update(logs.Start, [a, b]);
        Check(watcher.Scheduled is { Known: true, Pending: true, Loaded: null }, "replayed, the lines in sight decide again");
        // The same when a widget starts: the replay of both files is cut by a rotation between them.
        var started = new ClaudeDesktopLogWatcher(logs.Root);
        started.BetweenFilesForTest = () => { started.BetweenFilesForTest = null; logs.Rotate(logs.Line(15, "unrelated")); };
        started.Update(logs.Start, [a, b]);
        Check(started.LostLines && started.Scheduled is { Known: false }, "a widget starting across a rotation knows no list");
        started.Update(logs.Start, [a, b]);
        Check(started.Scheduled is { Known: true, Pending: true, Loaded: null }, "the reset it would have missed is read on the replay");
        // A first rotation of the run between the two reads (no main1.log before) is a gap as well.
        using var first = new Logs();
        first.Append(first.Reset(1), first.Initialized(2, a));
        var early = new ClaudeDesktopLogWatcher(first.Root);
        early.BetweenFilesForTest = () => { early.BetweenFilesForTest = null; first.Rotate(first.Line(3, "unrelated")); };
        early.Update(first.Start, [a, b]);
        Check(early.LostLines && early.Scheduled is { Known: false }, "the run's first rotation between the reads loses the file read meanwhile");
    }

    private static void TestRunStartUndecided()
    {
        using var logs = new Logs();
        var (a, b) = (Account("A"), Account("B"));
        // The host started within the second the last lines of main1.log are dated: a load dated then may be the
        // previous run's, and this run's log alone cannot tell which list its manager holds.
        var started = logs.Start.AddMilliseconds(600);
        File.WriteAllText(logs.Previous, logs.Initialized(-3600, b) + "\r\n" + logs.Initialized(0, a) + "\r\n");
        logs.Append(logs.Line(3, "unrelated"));
        var watcher = new ClaudeDesktopLogWatcher(logs.Root);
        watcher.Update(started, [a, b]);
        Check(watcher.Scheduled is { Known: true, Pending: true, Loaded: null }, "a load dated before the run's start is unknown");
        logs.Append(logs.Initialized(8, b));
        watcher.Update(started, [a, b]);
        Check(watcher.Scheduled is { Pending: false, Loaded: { } loaded } && loaded == b.Key, "this run's next load names the list");
        // A widget started long after the run, its logs no longer reaching back to the run's start, knows none until a
        // load is in sight.
        using var later = new Logs();
        File.WriteAllText(later.Previous, later.Line(0, "unrelated") + "\r\n");
        later.Append(later.Line(30, "unrelated"));
        var late = new ClaudeDesktopLogWatcher(later.Root);
        late.Update(later.Start.AddHours(-2), [a, b]);
        Check(late.Scheduled is { Known: true, Pending: true, Loaded: null }, "a run whose start is out of sight knows no list loaded");
        later.Append(later.Initialized(40, a));
        late.Update(later.Start.AddHours(-2), [a, b]);
        Check(late.Scheduled is { Pending: false, Loaded: { } seen } && seen == a.Key, "a load in sight names it");
    }

    private static void TestRunsFrom()
    {
        var package = new ClaudeDesktopPackage("Claude_9.9.9.9_x64__madeup", new Version(9, 9, 9, 9), @"X:\Apps\Claude_9.9.9.9_x64__madeup",
            "Claude_madeup");
        var image = Path.Combine(package.InstallDirectory, "app", "claude.exe");
        var started = new DateTime(2026, 11, 1, 9, 0, 0);
        ClaudeDesktopRuntime Running(params string[] images) => new(ClaudeDesktopRunState.Running, started, images);
        Check(ClaudeDesktopProbe.RunsFrom(new(ClaudeDesktopRunState.Closed, null), package), "a closed app runs no other package");
        Check(ClaudeDesktopProbe.RunsFrom(Running(image, image.ToUpperInvariant()), package)
              && ClaudeDesktopProbe.RunsFrom(Running(image), package with { InstallDirectory = package.InstallDirectory + @"\" }),
            "hosts in the package's folder, however the case or the folder is spelled, run from it");
        Check(!ClaudeDesktopProbe.RunsFrom(Running(image, @"X:\Apps\Claude_9.9.10.0_x64__madeup\app\claude.exe"), package),
            "a host from another version's folder is another package");
        Check(!ClaudeDesktopProbe.RunsFrom(Running(@"X:\Apps\Claude_9.9.9.9_x64__madeup-old\app\claude.exe"), package),
            "a folder whose name only begins the same is another one");
        Check(!ClaudeDesktopProbe.RunsFrom(new(ClaudeDesktopRunState.Running, started), package)
              && !ClaudeDesktopProbe.RunsFrom(Running(), package) && !ClaudeDesktopProbe.RunsFrom(Running(@"app\claude.exe"), package),
            "a running app whose hosts are not known, or not by a full path, is not taken for the package");
        Check(!ClaudeDesktopProbe.RunsFrom(new(ClaudeDesktopRunState.Unknown, null, [image]), package)
              && !ClaudeDesktopProbe.RunsFrom(Running(image), package with { InstallDirectory = " " }),
            "an app that cannot be inspected, or a package without a folder, is not");
    }

    // The fixed points the alignment rests on, beyond the flows (ClaudeDesktopProbe.ScheduledPoints). Read-only: the
    // mutations change a copy in memory, never the installed bundle.
    private static void TestFixedPoints()
    {
        var package = ClaudeDesktopProbe.FindPackage();
        if (package?.Version != new Version(6, 28899, 6, 0) || !File.Exists(package.AsarPath))
        {
            Console.WriteLine($"SKIP: scheduled-list fixed points need the installed 6.28899.6.0 ({package?.Version.ToString() ?? "none"})");
            return;
        }
        var bundle = File.ReadAllBytes(package.AsarPath);
        var unmatched = ClaudeDesktopProbe.ScheduledUnmatched(bundle);
        Check(unmatched.Count == 0 && ClaudeDesktopProbe.ScheduledContract(bundle) == ClaudeDesktopContract.Reviewed,
            "6.28899.6.0 reads as reviewed, its fixed points included (read: " + string.Join(", ", unmatched) + ")");
        // Every place the text is in; the point named among what no longer reads as reviewed.
        void Mutated(string from, string to, string failure, string what)
        {
            var (original, changed) = (Encoding.UTF8.GetBytes(from), Encoding.UTF8.GetBytes(to));
            var places = new List<int>();
            for (int offset = 0, found; (found = bundle.AsSpan(offset).IndexOf(original)) >= 0; offset += found + original.Length)
                places.Add(offset + found);
            Check(places.Count > 0 && original.Length == changed.Length, "mutation found: " + what);
            foreach (var at in places) changed.CopyTo(bundle, at);
            try
            {
                var failed = ClaudeDesktopProbe.ScheduledUnmatched(bundle);
                Check(ClaudeDesktopProbe.ScheduledContract(bundle) == ClaudeDesktopContract.Unreviewed && failed.Contains(failure),
                    what + " (read: " + string.Join(", ", failed) + ")");
            }
            finally { foreach (var at in places) original.CopyTo(bundle, at); }
        }
        const string start = "scheduled start of a scheduled run";
        Mutated("$dt=\"local_\"", "Qdt=\"local_\"", "scheduled storage path", "a list folder defined again before the path is unreviewed");
        Mutated("R3=10485760", "R3=10485761", "scheduled size limit", "a size limit of another value is unreviewed");
        Mutated("if(!r.isFile()||r.size>t)throw", "if(!r.isFile()||r.size<t)throw", "scheduled size limit",
            "a reader that no longer refuses a file over the limit is unreviewed");
        Mutated("function T(e){return rie(Koe,e)}", "function T(e){return rie(bse,e)}", "scheduled schema",
            "a builder making another kind of schema (the type of the fields it makes) is unreviewed");
        Mutated("this.isInitialized()", "this.reset()        ", "scheduled own reset or load", "a manager resetting itself is unreviewed");
        Mutated("C8.on(\"scheduled_task_event\"", "C8.reset(heduled_task_event\"", "scheduled own reset or load",
            "a manager reset by its own file is unreviewed");
        Mutated("s=new Date(a.getTime()-6048e5)", "s=new Date(a.getTime()-6049e5)", "scheduled floor",
            "missed runs scanned back to another floor are unreviewed");
        Mutated("if(+t>=Math.max(e.createdAt,n))return t}", "if(+t<=Math.max(e.createdAt,n))return t}", "scheduled floor",
            "a due run counted from another floor is unreviewed");
        Mutated("dS(e,n)),ur(e,t.Ul),dr(e,t.Ul)}", "dS(e,n))&ur(e,t.Ul),dr(e,t.Ul)}", start + " (session start)",
            "a start that may skip the safeguards is unreviewed");
        Mutated("permissionModeCarried&&lr(", "permissionModeCarried||lr(", start + " (carried safeguard)",
            "a carried safeguard that reads differently is unreviewed");
        Mutated("t.TDt(\"396417011\")", "t.TDt(\"396417012\")", start + " (carried safeguard switch)",
            "a safeguard switched by another gate is unreviewed");
        Mutated("r.importedFrom===void 0||", "r.importedFrom!==void 0||", start + " (import limit)",
            "an import limit that reads differently is unreviewed");
        Mutated("var lr=e=>t.l4(e,t.GLt.Default)", "var lr=e=>t.l4(t,t.GLt.Default)", start + " (above Default)",
            "another test of a mode above Default is unreviewed");
        Mutated("nR={plan:0,default:1,dontAsk:1,acceptEdits:2,auto:3,", "nR={plan:0,default:1,dontAsk:1,acceptEdits:2,auto:1,",
            start + " (mode order)", "another order of the modes is unreviewed");
        Mutated("function fls(e){return e===\"auto\"||e===\"bypassPermissions\"}",
            "function fls(e){return e===\"auto\"||e!==\"bypassPermissions\"}", start + " (modes of a task not found)",
            "other modes refused to a task not found are unreviewed");
        Mutated("e.Default=\"default\",e.AcceptEdits", "e.Default=\"defaulT\",e.AcceptEdits", start + " (mode values)",
            "a Default mode of another value is unreviewed");
        Check(ClaudeDesktopProbe.ScheduledContract(bundle) == ClaudeDesktopContract.Reviewed, "the copy reads as reviewed again once restored");
    }

    private static ClaudeShelfAccount Account(string label) =>
        new(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"), label, "p-" + label);

    /// <summary>A log folder of its own under the test root, its lines dated from a whole second an hour ago.</summary>
    private sealed class Logs : IDisposable
    {
        internal string Root { get; } = Path.GetFullPath(Path.Combine(
            Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT") ?? Path.Combine(Path.GetTempPath(), "gfs-agent", "claude-scheduled-probe-tests"),
            "probe-" + Guid.NewGuid().ToString("N")));
        internal string Main => Path.Combine(Root, "main.log");
        internal string Previous => Path.Combine(Root, "main1.log");
        internal DateTime Start { get; }

        internal Logs()
        {
            Directory.CreateDirectory(Root);
            var start = DateTime.Now.AddHours(-1);
            Start = new DateTime(start.Year, start.Month, start.Day, start.Hour, start.Minute, start.Second);
        }

        internal string Line(int seconds, string text) =>
            Start.AddSeconds(seconds).ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture) + " [info] " + text;
        internal string Reset(int seconds) => Line(seconds, "[CCDScheduledTasks] Reset");
        internal string Initialized(int seconds, ClaudeShelfAccount account) =>
            Line(seconds, $"[CCDScheduledTasks] Initialized {{ accountId: '{account.AccountUuid}', orgId: '{account.OrgUuid}' }}");

        /// <summary>Lines as the app writes them, each ended by CRLF.</summary>
        internal void Append(params string[] lines) => Write(string.Concat(lines.Select(line => line + "\r\n")));
        internal void Write(string text) => File.AppendAllText(Main, text);

        /// <summary>The app's rotation: main1.log to main2.log, main.log to main1.log, then a new main.log.</summary>
        internal void Rotate(params string[] fresh)
        {
            if (File.Exists(Previous)) File.Move(Previous, Path.Combine(Root, "main2.log"), overwrite: true);
            File.Move(Main, Previous);
            File.WriteAllText(Main, string.Concat(fresh.Select(line => line + "\r\n")));
        }

        public void Dispose()
        {
            try { Directory.Delete(Root, recursive: true); }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        }
    }

    private static void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
}
