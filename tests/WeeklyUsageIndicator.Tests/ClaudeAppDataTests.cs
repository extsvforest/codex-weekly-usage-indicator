using WeeklyUsageIndicator;

/// <summary>Which Claude profile folder the widget uses: the one the app's log names, the package's own when the app
/// names a roaming folder that is not there from outside, else the one whose lists were written last.</summary>
internal static class ClaudeAppDataTests
{
    private const string Account = "11111111-1111-4111-8111-111111111111";
    private const string Org = "22222222-2222-4222-8222-222222222222";

    internal static Task RunAsync()
    {
        var testBase = Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT")
            ?? Path.Combine(Path.GetTempPath(), "gfs-agent", "claude-app-data-tests");
        var root = Path.GetFullPath(Path.Combine(testBase, "app-data-" + Guid.NewGuid().ToString("N")));
        try
        {
            var case_ = 0;
            (string Roaming, string Local, string Logs) Fresh(string name = "")
            {
                var dir = Path.Combine(root, (++case_).ToString() + name);
                var roaming = Directory.CreateDirectory(Path.Combine(dir, "Roaming")).FullName;
                var local = Directory.CreateDirectory(Path.Combine(dir, "Local")).FullName;
                return (roaming, local, Directory.CreateDirectory(Path.Combine(local, "Claude", "Logs")).FullName);
            }
            static string Legacy((string Roaming, string Local, string Logs) pc) => Path.Combine(pc.Roaming, "Claude");
            static string Moved((string Roaming, string Local, string Logs) pc) => Path.Combine(pc.Local, "Claude-Data", "claude_pzs8sxrjxfjjc");
            static string Packaged((string Roaming, string Local, string Logs) pc) =>
                Path.Combine(pc.Local, "Packages", "Claude_pzs8sxrjxfjjc", "LocalCache", "Roaming", "Claude");
            static void List(string profile, DateTime writtenUtc)
            {
                var org = Directory.CreateDirectory(Path.Combine(profile, ClaudeAppData.SessionsFolder, Account, Org)).FullName;
                var record = Path.Combine(org, "local_" + Guid.NewGuid().ToString("D") + ".json");
                File.WriteAllText(record, "{}");
                File.SetLastWriteTimeUtc(record, writtenUtc);
            }
            static void Log(string logs, params string[] profiles) => File.WriteAllLines(Path.Combine(logs, "main.log"), profiles.Select(
                (profile, index) => $"2026-11-19 21:0{index}:00 [info] Loaded 3 persisted sessions from {Path.Combine(profile, ClaudeAppData.SessionsFolder, Account, Org)}"));
            static string Resolve((string Roaming, string Local, string Logs) pc) => ClaudeAppData.Resolve(pc.Roaming, pc.Local, pc.Logs).Root;
            var earlier = new DateTime(2026, 11, 18, 0, 0, 0, DateTimeKind.Utc);
            var later = earlier.AddHours(5);

            // An install that predates the package: only the roaming folder.
            var pc = Fresh();
            List(Legacy(pc), later);
            Assert(Resolve(pc) == Legacy(pc) && ClaudeAppData.Resolve(pc.Roaming, pc.Local, pc.Logs).Reason == "only lists",
                "the roaming profile of an older install is used, without reading the log");
            Assert(ClaudeAppData.Pick(ClaudeAppData.Candidates(pc.Roaming, pc.Local), Legacy(pc)) == Legacy(pc),
                "an empty Claude-Data folder beside it does not count");

            // A profile the packaged app moved out of its package, a copy left in the package.
            pc = Fresh();
            List(Moved(pc), later);
            List(Packaged(pc), earlier);
            Assert(Resolve(pc) == Moved(pc), "the moved profile, written last, is used without a log");
            Log(pc.Logs, Moved(pc));
            Assert(Resolve(pc) == Moved(pc), "the moved profile the log names is used");

            // The log decides over write times: the app names what it reads.
            pc = Fresh();
            List(Moved(pc), earlier);
            List(Legacy(pc), later);
            Log(pc.Logs, Legacy(pc), Moved(pc));
            var choice = ClaudeAppData.Resolve(pc.Roaming, pc.Local, pc.Logs);
            Assert(choice.Root == Moved(pc) && choice.Reason == "log", "the last folder the log names wins over a newer write");

            // Ties go to the moved profile.
            pc = Fresh();
            List(Moved(pc), later);
            List(Legacy(pc), later);
            Assert(Resolve(pc) == Moved(pc), "a tie goes to the moved profile");

            // Inside the package, the app names %APPDATA%\Claude, which has no lists from outside.
            pc = Fresh();
            List(Packaged(pc), earlier);
            List(Moved(pc), later);
            Directory.CreateDirectory(Legacy(pc));
            Log(pc.Logs, Legacy(pc));
            choice = ClaudeAppData.Resolve(pc.Roaming, pc.Local, pc.Logs);
            Assert(choice.Root == Packaged(pc) && choice.Reason == "log, in package",
                "a logged roaming folder missing from outside stands for the package's own");

            // A log naming a folder that is gone falls back to write times; a rotated log still counts.
            pc = Fresh();
            List(Legacy(pc), earlier);
            List(Moved(pc), later);
            File.WriteAllText(Path.Combine(pc.Logs, "main.log"), "2026-11-19 21:00:00 [info] Starting app\n");
            File.WriteAllLines(Path.Combine(pc.Logs, "main1.log"),
                [$"2026-11-19 20:00:00 [info] Loaded 1 persisted sessions from {Path.Combine(pc.Roaming, "Gone", ClaudeAppData.SessionsFolder, Account, Org)}"]);
            choice = ClaudeAppData.Resolve(pc.Roaming, pc.Local, pc.Logs);
            Assert(choice.Root == Moved(pc) && choice.Reason == "newest list" && choice.Logged is not null,
                "a logged folder that is gone falls back to the newest list");
            Log(pc.Logs);
            File.WriteAllLines(Path.Combine(pc.Logs, "main1.log"),
                [$"2026-11-19 20:00:00 [info] [LocalSessionManager] Session storage directory does not exist yet, skipping load: {Path.Combine(Legacy(pc), ClaudeAppData.SessionsFolder, Account, Org)}"]);
            Assert(Resolve(pc) == Legacy(pc), "the previous log's line counts when the current one has none");

            // The log is searched from its end: a line far back, and one across the boundary of the last chunk, under a
            // user folder whose name is not ASCII.
            pc = Fresh("-사용자");
            List(Moved(pc), earlier);
            List(Legacy(pc), later);
            var line = $"2026-11-19 21:00:00 [info] Loaded 3 persisted sessions from {Path.Combine(Moved(pc), ClaudeAppData.SessionsFolder, Account, Org)}\n";
            var filler = string.Concat(Enumerable.Repeat("2026-11-19 21:00:01 [info] 다른 줄 another line of the app's own\n", 60_000));
            File.WriteAllText(Path.Combine(pc.Logs, "main.log"), line + filler);
            Assert(Resolve(pc) == Moved(pc), "a line some megabytes before the end of the log is found");
            var lineBytes = System.Text.Encoding.UTF8.GetByteCount(line);
            var tail = new string('x', 1024 * 1024 - lineBytes / 2 - 1) + "\n";
            File.WriteAllText(Path.Combine(pc.Logs, "main.log"), filler + line + tail);
            Assert(Resolve(pc) == Moved(pc), "a line across the last chunk's boundary is found");

            // A new PC whose app has not written a list yet: the folder holding its stores, else the roaming one.
            pc = Fresh();
            Directory.CreateDirectory(Path.Combine(Packaged(pc), "Local Storage", "leveldb"));
            Assert(Resolve(pc) == Packaged(pc), "with no list yet, the folder holding the app's stores is used");
            pc = Fresh();
            Assert(Resolve(pc) == Legacy(pc), "with nothing at all, the roaming folder is assumed");

            // The paths built on it, and the Code CLIs of every candidate.
            var paths = ClaudeShelfPaths.CreateDefault(Path.Combine(root, "home"), Moved(pc));
            Assert(paths.SessionsRoot == Path.Combine(Moved(pc), ClaudeAppData.SessionsFolder)
                && paths.ScratchRoot == Path.Combine(Moved(pc), "scratch-workspaces"), "lists and scratch folders follow the profile");
            var sources = ClaudeSessionSync.Sources.For(paths);
            Assert(sources.LocalStorage == Path.Combine(Moved(pc), "Local Storage", "leveldb")
                && sources.IndexedDb == Path.Combine(Moved(pc), "IndexedDB", "https_claude.ai_0.indexeddb.leveldb"),
                "the web-UI stores are read beside the lists");
            Assert(ClaudeAppData.Candidates(pc.Roaming, pc.Local).Contains(Moved(pc), StringComparer.OrdinalIgnoreCase)
                && ClaudeAppData.Candidates(pc.Roaming, pc.Local).Contains(Legacy(pc), StringComparer.OrdinalIgnoreCase),
                "every place a Code CLI may run from is a candidate");
        }
        finally
        {
            try { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); } catch (IOException) { }
        }
        return Task.CompletedTask;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Claude app data: " + message);
    }
}
