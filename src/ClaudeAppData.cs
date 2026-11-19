using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WeeklyUsageIndicator;

/// <summary>
/// The Claude app's profile folder on this PC: its session lists (<c>claude-code-sessions</c>), its web-UI stores and
/// the Code CLIs it runs. Where it is depends on how the app came to the PC (Windows, reviewed with 5.11492):
/// <list type="bullet">
/// <item>an install that predates the packaged app keeps it in <c>%APPDATA%\Claude</c>, which the package then uses in
/// place;</item>
/// <item>the packaged app otherwise starts it inside its package (<c>%LOCALAPPDATA%\Packages\Claude_…\LocalCache\Roaming\Claude</c>)
/// and at a later launch moves it out to <c>%LOCALAPPDATA%\Claude-Data\claude_…</c>, named for the package, which its
/// manifest leaves out of file virtualization. A move can leave a copy behind.</item>
/// </list>
/// Usually one of them holds session lists, and that is it. When more do, the app's log decides: it names the folder it
/// reads each time it loads an account's list (the app sees <c>%APPDATA%</c> through its package, so a roaming folder
/// without lists from outside stands for the package's own). Without such a line the folder whose lists were written
/// last wins (the app keeps rewriting the records of open sessions), ties going to the moved one. The logs stay in
/// <c>%LOCALAPPDATA%\Claude\logs</c> either way.
/// </summary>
internal static class ClaudeAppData
{
    internal const string SessionsFolder = "claude-code-sessions";
    internal const string CommandFlag = "--claude-data";
    private const string PackageFamily = "Claude_pzs8sxrjxfjjc";
    // The log is searched from its end, a chunk at a time, overlapping by more than a line, up to a bound.
    private const int LogChunk = 1024 * 1024;
    private const int LogOverlap = 8 * 1024;
    private const long LogScan = 24L * 1024 * 1024;
    private static readonly Regex Loaded = new(
        @"\[info\] (?:Loaded \d+ persisted sessions from|\[LocalSessionManager\] Session storage directory does not exist yet, skipping load:) (?<root>.+?)[\\/]claude-code-sessions[\\/][0-9a-fA-F-]{36}[\\/][0-9a-fA-F-]{36}",
        RegexOptions.CultureInvariant);

    internal static string Roaming => Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
    internal static string Local => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    internal static string LogDirectory => Path.Combine(Local, "Claude", "Logs");

    /// <summary>The profile folder the app uses now.</summary>
    internal static string Root() => Resolve(Roaming, Local, LogDirectory).Root;

    /// <summary>Where the Code CLIs the app runs live, one per candidate folder, each ending with a separator.</summary>
    internal static IReadOnlyList<string> CliRoots() =>
        Candidates(Roaming, Local).Select(root => Path.Combine(root, "claude-code") + Path.DirectorySeparatorChar).ToList();

    internal sealed record Choice(string Root, string Reason, IReadOnlyList<string> Candidates, string? Logged);

    internal static Choice Resolve(string roaming, string local, string logDirectory)
    {
        var candidates = Candidates(roaming, local);
        var listed = candidates.Where(candidate => Directory.Exists(Path.Combine(candidate, SessionsFolder))).ToList();
        if (listed.Count == 1) return new(listed[0], "only lists", candidates, null);
        string? logged = null;
        if (listed.Count > 1 && (logged = LoggedRoot(logDirectory)) is not null)
        {
            if (listed.FirstOrDefault(candidate => Same(candidate, logged)) is { } named) return new(named, "log", candidates, logged);
            // The app sees %APPDATA% through its package: a roaming folder without lists from outside is the package's own.
            if (Same(logged, Path.Combine(roaming, "Claude"))
                && listed.FirstOrDefault(candidate => IsPackaged(candidate, local)) is { } packaged)
                return new(packaged, "log, in package", candidates, logged);
        }
        return new(Pick(candidates, Path.Combine(roaming, "Claude")), listed.Count > 1 ? "newest list" : "no lists", candidates, logged);
    }

    /// <summary>Every folder the app may use, the moved ones first: <c>Claude-Data</c>'s folder for the package, its other
    /// folders and itself, the roaming folder, then the package's own.</summary>
    internal static IReadOnlyList<string> Candidates(string roaming, string local)
    {
        var moved = Path.Combine(local, "Claude-Data");
        var candidates = new List<string> { Path.Combine(moved, PackageFamily.ToLowerInvariant()) };
        candidates.AddRange(Folders(moved, "*"));
        candidates.Add(moved);
        candidates.Add(Path.Combine(roaming, "Claude"));
        candidates.AddRange(Folders(Path.Combine(local, "Packages"), "Claude_*")
            .Select(package => Path.Combine(package, "LocalCache", "Roaming", "Claude")));
        return candidates.Select(Full).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    /// <summary>The candidate whose lists were written last; a tie, or lists with no record yet, go to the earlier one.
    /// With no lists anywhere, the first that holds the app's web-UI stores, else <paramref name="fallback"/>.</summary>
    internal static string Pick(IReadOnlyList<string> candidates, string fallback)
    {
        string? newest = null, listed = null, used = null;
        var newestAt = DateTime.MinValue;
        foreach (var candidate in candidates)
        {
            var sessions = Path.Combine(candidate, SessionsFolder);
            if (!Directory.Exists(sessions))
            {
                if (used is null && Directory.Exists(Path.Combine(candidate, "Local Storage"))) used = candidate;
                continue;
            }
            listed ??= candidate;
            var at = NewestRecord(sessions);
            if (at > newestAt) { newest = candidate; newestAt = at; }
        }
        return Full(newest ?? listed ?? used ?? fallback);
    }

    /// <summary>When a record of any list (<c>&lt;account&gt;\&lt;org&gt;\local_*.json</c>) was last written. Only names
    /// and times are read.</summary>
    internal static DateTime NewestRecord(string sessionsRoot)
    {
        var newest = DateTime.MinValue;
        try
        {
            foreach (var account in new DirectoryInfo(sessionsRoot).EnumerateDirectories())
            foreach (var org in account.EnumerateDirectories())
            foreach (var record in org.EnumerateFiles("local_*.json"))
                if (record.LastWriteTimeUtc > newest) newest = record.LastWriteTimeUtc;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        return newest;
    }

    /// <summary>The folder named by the app's last list load in its current log, or the previous one.</summary>
    internal static string? LoggedRoot(string logDirectory)
    {
        foreach (var name in new[] { "main.log", "main1.log" })
            if (LastMatch(Path.Combine(logDirectory, name)) is { } root) return root;
        return null;
    }

    /// <summary>The last line of the file naming a list's folder, found from the end a chunk at a time (a chunk overlaps
    /// the next by more than a line, so no line is missed at a boundary).</summary>
    private static string? LastMatch(string path)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            var length = stream.Length;
            var buffer = new byte[LogChunk + LogOverlap];
            for (var end = length; end > 0 && length - end < LogScan;)
            {
                var start = Math.Max(0, end - LogChunk);
                var stop = Math.Min(length, end + LogOverlap);
                stream.Seek(start, SeekOrigin.Begin);
                var read = 0;
                while (read < stop - start && stream.Read(buffer, read, (int)(stop - start) - read) is var count and > 0) read += count;
                string? root = null;
                foreach (Match match in Loaded.Matches(Encoding.UTF8.GetString(buffer, 0, read))) root = match.Groups["root"].Value.Trim();
                if (root is { Length: > 0 }) return Path.IsPathFullyQualified(root) ? root : null;
                end = start;
            }
            return null;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private static IEnumerable<string> Folders(string parent, string pattern)
    {
        try { return Directory.Exists(parent) ? Directory.EnumerateDirectories(parent, pattern).Order(StringComparer.OrdinalIgnoreCase).ToList() : []; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return []; }
    }

    private static bool IsPackaged(string candidate, string local) =>
        Full(candidate).StartsWith(Full(Path.Combine(local, "Packages")) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);

    private static bool Same(string left, string right) => string.Equals(Full(left), Full(right), StringComparison.OrdinalIgnoreCase);

    private static string Full(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    /// <summary><c>--claude-data</c>: prints, as UTF-8 JSON, which profile folder the widget uses and why, with only
    /// names, counts and times (no file is opened but the app's log, read for the folder it names, and its package,
    /// checked against the reviewed design), and the installed app's version and how it reads against that design.</summary>
    internal static int RunCommand()
    {
        var choice = Resolve(Roaming, Local, LogDirectory);
        var sessions = Path.Combine(choice.Root, SessionsFolder);
        int accounts = 0, lists = 0, records = 0;
        try
        {
            foreach (var account in new DirectoryInfo(sessions).EnumerateDirectories())
            {
                accounts++;
                foreach (var org in account.EnumerateDirectories()) { lists++; records += org.EnumerateFiles("local_*.json").Count(); }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
        var newest = Directory.Exists(sessions) ? NewestRecord(sessions) : DateTime.MinValue;
        var package = ClaudeDesktopProbe.FindPackage();
        var report = new
        {
            app = package?.Version.ToString(),
            // Reviewed: everything the session lists rely on is there. Unreviewed: filling goes on, archive rather than
            // delete until reviewed. Incompatible: nothing is written.
            contract = package is null ? null : ClaudeDesktopProbe.Contract(package.AsarPath).ToString(),
            root = choice.Root,
            reason = choice.Reason,
            logged = choice.Logged,
            sessions = Directory.Exists(sessions),
            accounts,
            lists,
            records,
            newest = newest == DateTime.MinValue ? null : (DateTimeOffset?)new DateTimeOffset(newest).ToLocalTime(),
            logs = LogDirectory,
            log = File.Exists(Path.Combine(LogDirectory, "main.log")),
            localStorage = Directory.Exists(Path.Combine(choice.Root, "Local Storage", "leveldb")),
            indexedDb = Directory.Exists(Path.Combine(choice.Root, "IndexedDB", "https_claude.ai_0.indexeddb.leveldb")),
            candidates = choice.Candidates.Where(Directory.Exists).Select(candidate => new
            {
                path = candidate,
                sessions = Directory.Exists(Path.Combine(candidate, SessionsFolder)),
            }),
        };
        using var output = Console.OpenStandardOutput();
        output.Write(JsonSerializer.SerializeToUtf8Bytes(report, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        }));
        output.Write("\n"u8);
        return 0;
    }
}
