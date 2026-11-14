using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace WeeklyUsageIndicator;

internal sealed record ClaudeDesktopPackage(string FullName, Version Version, string InstallDirectory, string FamilyName)
{
    internal string AppUserModelId => FamilyName + "!Claude";
    internal string AsarPath => Path.Combine(InstallDirectory, "app", "resources", "app.asar");
}

internal enum ClaudeDesktopRunState { Closed, Running, Unknown }

/// <summary>How the installed app reads against the shelf design. <see cref="Reviewed"/>: everything it relies on is
/// there. <see cref="Unreviewed"/>: the stores and log lines the widget follows are there, but the deletion protection
/// reads differently (a version not reviewed yet). Filling goes on: the copies already made rely on that protection
/// whether or not more are added, so the user is told to archive rather than delete until it is reviewed.
/// <see cref="Incompatible"/>: a store or log line the widget follows is missing, and nothing is written.</summary>
internal enum ClaudeDesktopContract { Reviewed, Unreviewed, Incompatible }

/// <summary>Current process generation of the packaged app. The earliest host process start separates app runs.</summary>
internal sealed record ClaudeDesktopRuntime(ClaudeDesktopRunState State, DateTime? HostStartedAt);

internal enum ClaudeDesktopAccountStatus { Closed, Unknown, SignedOut, Unregistered, SignedIn }

/// <summary>What the running app reported in its own log since it started. <see cref="Confirmed"/> requires the
/// app to have initialized a registered account/organization pair in this run. Hints only, never authentication.</summary>
internal sealed record ClaudeDesktopAccountState(ClaudeDesktopAccountStatus Status, string? AccountUuid = null,
    string? OrgUuid = null, int? LoadedSessions = null, DateTime? ObservedAt = null)
{
    internal static readonly ClaudeDesktopAccountState Closed = new(ClaudeDesktopAccountStatus.Closed);
    internal static readonly ClaudeDesktopAccountState Unknown = new(ClaudeDesktopAccountStatus.Unknown);
    internal bool Confirmed => Status == ClaudeDesktopAccountStatus.SignedIn && OrgUuid is not null;
    internal string? Key => Confirmed ? AccountUuid + "/" + OrgUuid : null;
}

/// <summary>Observes the packaged Claude Desktop app without opening its stores or automating its UI.</summary>
internal static class ClaudeDesktopProbe
{
    private const string PublisherSuffix = "__pzs8sxrjxfjjc";
    private const int ErrorInvalidParameter = 87;
    // Stable (non-minified) strings the shelf design depends on; each entry lists accepted spellings (bundled
    // sources may escape non-ASCII punctuation). A missing marker pauses writes; the windows and flows below only
    // report a version as unreviewed.
    internal static readonly string[][] ContractMarkers =
    [
        ["\"claude-code-sessions\""],
        ["this.userDataPath,this.baseDir,this.currentAccountId,this.currentOrgId"],
        ["persisted sessions from"],
        ["Session storage directory does not exist yet"],
        ["Recovered orphaned session temp"],
        ["adoptedFromOtherSurface===!0||void 0"],
        ["isFirstTurn:!"],
        [@"Initialization succeeded \u2014 accountId=", "Initialization succeeded — accountId="],
        ["Login-state transition (loggedOut:"],
        ["transcriptCuts"]
    ];
    // Behaviors that must stay connected: transcript deletion consults every account's records (failing closed),
    // and those claims include each record's earlier transcript ids.
    internal static readonly (string Anchor, int Window, string[] Required)[] ContractWindows =
    [
        ("this.reapChecks={", 200, ["claimedOnDisk:()=>this.cliSessionIdsClaimedOnDisk()"]),
        ("cliSessionIdsClaimedOnDisk(){", 900, ["failClosed:!0,localOnly:!0,recordsOnly:!0", "this.getAccountIds()"]),
        ("unarchivedCliSessionId])typeof", 300, ["priorCliSessionIds)??[]"])
    ];
    // Data flow the deletion protection depends on, checked with back-references so renamed variables still match
    // but a changed flow does not: (1) every record's current and earlier transcript ids go into the claims set,
    // (2) the reap decision fails closed on unreadable records and refuses ids in that set, with nothing allowing
    // deletion before that test, (3) the checks a session delete uses release only ids outside that set, and their reap
    // check is that decision, alone or behind a guard whose other branch is a keep call, with nothing applied to the
    // result, (4) the session delete makes those checks from the manager's and hands them, unchanged, to the removal,
    // (5) transcripts are removed only when the reap check returned true: nothing reassigns the result before the
    // removal, and a failed check only logs and keeps, (6) the walk behind that set takes every account's folder, failing
    // closed on one it cannot list: the current account is told apart only to skip records it holds in memory, and every
    // other account's record counts: on the way to the id loop that flag appears nowhere else but as an argument and in
    // claiming its own stem, (7) a delete owed to Remote Control sets transcripts aside under those same checks and later
    // finishes under a check that keeps on anything claimed or unreadable, (8) setting aside moves nothing until the
    // release test and the reap decision allow it, and any refusal or failure keeps them all, (9) finishing removes only
    // when its check allowed it, a failed check keeping, and puts the transcript back otherwise, (10) the claims never
    // narrow that walk to the current organization. A shared group (see SharedGroups) names one function in every flow
    // it appears in, across bundled files too (see Function). What the keep call returns is not checked; this is a
    // drift guard, not a proof.
    internal static readonly (string Anchor, int Before, int After, Regex Flow)[] ContractFlows =
    [
        // (1) Earlier ids are iterated directly (2.16120–2.19675) or spread first into an array that may add more ids,
        // such as remote earlier ids (2.26454); either way each one enters the same set.
        ("unarchivedCliSessionId])typeof", 120, 320, new Regex(
            @"for\(let (?<v>[\w$]+) of\[(?<r>[\w$]+)\.cliSessionId,\k<r>\.unarchivedCliSessionId\]\)typeof \k<v>==""string""&&[^;]{0,80}?(?<set>[\w$]+)\.add\(\k<v>\);for\(let (?<p>[\w$]+) of(?: [\w$.]+\(\k<r>\.priorCliSessionIds\)\?\?\[\]|\[\.\.\.[\w$.]+\(\k<r>\.priorCliSessionIds\)\?\?\[\](?:,\.\.\.[^;{}]{0,160}?)?\])\)\k<set>\.add\(\k<p>\)",
            RegexOptions.CultureInvariant)),
        // (2) A method until 2.x; since 4.29 a function given the manager's checks, which adds refusals around this test.
        ("\"claimed_elsewhere\",\"is claimed by a session of another account or profile on this computer\"", 1100, 100, new Regex(
            @"async function (?<link>[\w$]+)\((?<c>[\w$]+),(?<id>[\w$]+),(?:(?!return!0).){0,1000}?let (?<l>[\w$]+)=await \k<c>\.claimedOnDisk\(\);return \k<l>===null\?[\w$]+\(""records_unreadable""[^)]*\):\k<l>\.has\(\k<id>\)\?[\w$]+\(""claimed_elsewhere""",
            RegexOptions.CultureInvariant | RegexOptions.Singleline)),
        // (3) Inline in the session delete until 4.29; since 4.4212 a function given the manager's checks makes them.
        // Since 5.11492 the checks also carry the session's own process exit (`reapAfter`) for the removal to wait on
        // first; the reap check still refuses on its own when that exit is not confirmed.
        ("mayRelease:async ", 200, 600, new Regex(
            @"function (?<maker>[\w$]+)\((?<c>[\w$]+),[^()]*\)\{(?:let [^;{}]{0,120};)?return\{mayRelease:async (?<e>[\w$]+)=>!\(\(await \k<c>\.claimedOnDisk\(\)\)\?\.has\(\k<e>\)(?:\?\?![01])?\),(?:reapAfter:[\w$]+,)?reapIf:async\((?<t>[\w$]+),(?<r>[\w$]+)\)=>(?:[^;{}?:]{1,200}\?[\w$]+\(\k<t>,""[\w-]+"",""[^""\\]*"",[^;{}?:()]{0,60}\):)?(?<link>[\w$]+)\(\k<c>,\k<t>,\k<r>(?:,(?:[\w$.]+|[\w$]+\([\w$]*(?:,[\w$]+)*\)))*\)(?:,[\w$]+:|\})",
            RegexOptions.CultureInvariant)),
        // (4) Since 5.11492 the removal and the setting aside are imports from the chunk that holds them (`Gt.f`).
        ("(this.reapChecks,", 60, 2400, new Regex(
            @"let (?<v>[\w$]+)=(?<maker>[\w$]+)\(this\.reapChecks,[^;]{0,400}?(?<![\w$.])(?<aside>[\w$]+)\(this\.reapChecks,\k<v>,(?:(?!(?<![\w$.])\k<v>=(?!=)).){0,2300}?(?<![\w$.])(?<remover>(?:[\w$]+\.)?[\w$]+)\(this\.diskTranscript,[^;]{0,120},""delete"",\k<v>\)",
            RegexOptions.CultureInvariant | RegexOptions.Singleline)),
        // (5) Since 5.11492 the removal first waits, out of the shared limit of removals at once, for the checks'
        // `reapAfter` (a wait that swallows its failure and returns nothing), then asks the reap check as before.
        ("desktop-released marker: reap check failed", 1600, 200, new Regex(
            @"async function (?<remover>[\w$]+)\([^()]*\)\{[^{}]{0,160}?async(?:\(\)| (?<w>[\w$]+))=>\{try\{if\((?<m>[\w$]+)\.mayRelease&&!await \k<m>\.mayRelease\((?<c>[\w$]+)\)\)return\}catch\{return\}.{0,900}?if\([\w$]+===null\|\|![\w$]+\|\|!\k<m>\.reapIf\)return;(?:await \k<w>\(\k<m>\.reapAfter\);)?let (?<d>[\w$]+)=!1;try\{(?:(?!(?<![\w$.])\k<d>=(?!=)).){0,700}?(?<![\w$.])\k<d>=await \k<m>\.reapIf\(\k<c>,\{[^{}]*\}\)\}+catch\([\w$]+\)\{[\w$.]+\.debug\(""desktop-released marker: reap check failed"",\{[^{}]*\}\);try\{\k<m>\.keptInDoubt\?\.\(\k<c>\)\}catch\{\}\}(?:if\(\k<d>\)for\(|if\(!\k<d>\)return;)",
            RegexOptions.CultureInvariant | RegexOptions.Singleline)),
        // (6) Since 5.11492 the walk can also be asked for the current organization only (`currentOrgOnly`, for the
        // recovery index); flow (10) checks that the claims never ask it.
        ("skipOwnRecordsOf?.has(", 1500, 1300, new Regex(
            @"for\(let (?<a>[\w$]+) of [\w$]+\)\{if\(!\k<a>\.isDirectory\(\)(?:\|\|(?<only>[\w$]+)\?\.currentOrgOnly&&\k<a>\.name!==[\w$]+\(\)\.accountId)?\)continue;let (?<ad>[\w$]+)=\(0,[\w$]+\.join\)\([\w$]+,\k<a>\.name\),(?<os>[\w$]+);try\{\k<os>=await [\w$.]+\.readdir\(\k<ad>,\{withFileTypes:!0\}\)\}catch\([\w$]+\)\{if\([\w$]+\?\.failClosed\)throw [\w$]+;.{0,200}?continue\}for\(let (?<o>[\w$]+) of \k<os>\)\{if\(!\k<o>\.isDirectory\(\)\)continue;let (?<od>[\w$]+)=\(0,[\w$]+\.join\)\(\k<ad>,\k<o>\.name\),(?<own>[\w$]+)=[\w$]+===void 0&&\k<a>\.name===[\w$]+\(\)\.accountId&&\k<o>\.name===[\w$]+\(\)\.orgId(?:,|;if\(\k<only>\?\.currentOrgOnly&&!\k<own>\)continue;let )(?<fs>[\w$]+);try\{\k<fs>=await [\w$.]+\.readdir\(\k<od>,\{withFileTypes:!0\}\)\}catch\([\w$]+\)\{if\([\w$]+\?\.failClosed\)throw [\w$]+;.{0,200}?continue\}for\(let [\w$]+ of \k<fs>\)\{(?:(?!(?<![\w$.])\k<own>(?![\w$])|accountId).){0,700}?\|\|\k<own>&&[\w$]+\?\.skipOwnRecordsOf\?\.has\([^;]{0,60}\)continue;let (?<s>[\w$]+)=[^;]{0,60};if\(!\k<own>\)[\w$]+\.add\(\k<s>\);else if\((?:(?<=,)\k<own>,|(?<=if\()\k<own>&&[\w$]+\.add\(\k<s>\),|(?!(?<![\w$.])\k<own>(?![\w$])|accountId).){0,1200}?for\(let [\w$]+ of\[[\w$]+\.cliSessionId,",
            RegexOptions.CultureInvariant | RegexOptions.Singleline)),
        // (7) Since 4.4212. The removal after finishing is given only the release test, no reap check, so it only
        // writes markers: a transcript kept is back under its name by then.
        ("setAside:(", 200, 700, new Regex(
            @"function (?<aside>[\w$]+)\((?<c>[\w$]+),(?<l>[\w$]+),(?<s>[\w$]+),(?<o>[\w$]+)\)\{let (?<x>[\w$]+)=(?<y>[\w$]+)=>\k<c>\.claims\(\)\.localIdsClaimedByOthers\(\k<s>,\{adopting:!0\}\)\.has\(\k<y>\);return\{setAside:\((?<a>[\w$]+),(?<b>[\w$]+)\)=>\(\k<o>\.beforeSetAside\(\),(?<setter>(?:[\w$]+\.)?[\w$]+)\(\k<o>\.resolver,\k<o>\.ids,\k<o>\.hints,\{\.\.\.\k<l>,nonce:\k<a>,notWrittenSince:\k<b>,mayRelease:async (?<m>[\w$]+)=>!\k<x>\(\k<m>\)&&await \k<l>\.mayRelease\(\k<m>\)\}\)\),finish:async\((?<p>[\w$]+),(?<q>[\w$]+)\)=>\{let (?<k>[\w$]+)=await (?<finisher>(?:[\w$]+\.)?[\w$]+)\(\k<p>,\k<q>,\(async (?<z>[\w$]+)=>\k<x>\(\k<z>\)\|\|await \k<c>\.claimedOnDisk\(\)===null\|\|!await \k<l>\.mayRelease\(\k<z>\)\)\)(?:,[\w$]+=\k<c>\.claims\(\)\.localIdsClaimedByOthers\(\k<s>,\{adopting:!0\}\))?;return await (?<remover>(?:[\w$]+\.)?[\w$]+)\(\k<o>\.resolver,\k<o>\.ids\.filter\([^;]{0,60}?\),\k<o>\.hints,""delete"",\{mayRelease:\k<l>\.mayRelease\}\),\k<k>\}",
            RegexOptions.CultureInvariant)),
        // (8) Since 4.4212. A moved transcript is named by the nonce, so nothing before the decision may use it.
        (".notWrittenSince||", 1200, 900, new Regex(
            @"async function (?<setter>[\w$]+)\([^()]*\)\{.{0,240}?try\{if\(![\w$]+\((?<t>[\w$]+)\)\|\|!await (?<r>[\w$]+)\.mayRelease\(\k<t>\)\)\{(?<a>[\w$]+)=!0;return\}(?:(?!\k<r>\.nonce|(?<![\w$.])\k<a>=!1).){0,900}?if\([\w$]+>\k<r>\.notWrittenSince\|\|!await \k<r>\.reapIf\(\k<t>,\{.{0,240}?\}\)\.catch\((?:\(\(\)=>\(\k<r>\.keptInDoubt\?\.\(\k<t>\),!1\)\)|\(\)=>\(\k<r>\.keptInDoubt\?\.\(\k<t>\),!1\))\)\)\{\k<a>=!0;return\}(?:(?!(?<![\w$.])\k<a>=!1).){0,400}?\}catch\{\k<a>=!0\}finally\{[^{}]{0,60}\}.{0,40}?,!\k<a>\)return [\w$]+;",
            RegexOptions.CultureInvariant | RegexOptions.Singleline)),
        // (9) Since 4.4212. 5.11492 writes arrow arguments without the extra parentheses.
        (".asidePath", 500, 600, new Regex(
            @"async function (?<finisher>[\w$]+)\([\w$]+,[\w$]+,(?<r>[\w$]+)\)\{let (?<i>[\w$]+)=!1,.{0,200}?let (?<o>[\w$]+)=await \k<r>\([\w$]+\)\.catch\((?:\(\(\)=>!0\)|\(\)=>!0)\),.{0,160}?if\(!\(!\k<o>&&[^{}]{0,300}\)\)\{\k<i>=!0,await [\w$]+\([\w$]+\.asidePath,[^;{}]{0,40}\);continue\}(?:(?!(?<![\w$.])\k<o>(?![\w$])).){0,300}?\.rm\([\w$]+\.asidePath,",
            RegexOptions.CultureInvariant | RegexOptions.Singleline)),
        // (10) The claims ask the walk with a literal of their own options, and nothing in them narrows it to the
        // current organization (5.11492's `currentOrgOnly`).
        ("cliSessionIdsClaimedOnDisk(){", 0, 1600, new Regex(
            @"^cliSessionIdsClaimedOnDisk\(\)\{(?!.*currentOrgOnly).{0,400}?let [\w$]+=\{failClosed:!0,localOnly:!0,recordsOnly:!0(?:,skipOwnRecordsOf:new Set(?:\([^;]{0,200}?\))?)?\}[,;]",
            RegexOptions.CultureInvariant | RegexOptions.Singleline))
    ];
    private static readonly string[] SharedGroups = ["link", "maker", "remover", "aside", "setter", "finisher"];
    private static readonly object ContractGate = new();
    private static (string Key, ClaudeDesktopContract Contract)? _contract;

    internal static string LogDirectory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Claude", "Logs");

    /// <summary>The packaged app or one of its Code CLIs counts as running. Terminal CLIs are not Desktop writers.
    /// A process whose image cannot be inspected makes the answer unknown, never "closed".</summary>
    internal static ClaudeDesktopRuntime Observe()
    {
        var cliRoot = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Claude", "claude-code") + Path.DirectorySeparatorChar;
        var running = false; var unknown = false; DateTime? host = null;
        foreach (var process in Process.GetProcessesByName("claude"))
        {
            using (process)
            {
                var path = ImagePath(process.Id, out var created, out var missing);
                if (missing) continue;
                if (path is null) { unknown = true; continue; }
                if (IsDesktopImage(path))
                {
                    running = true;
                    if (created is { } start && (host is null || start < host)) host = start;
                }
                else if (path.StartsWith(cliRoot, StringComparison.OrdinalIgnoreCase)) running = true;
            }
        }
        return new ClaudeDesktopRuntime(running ? ClaudeDesktopRunState.Running
            : unknown ? ClaudeDesktopRunState.Unknown : ClaudeDesktopRunState.Closed, host);
    }

    internal static bool IsDesktopImage(string path) =>
        path.Contains(@"\WindowsApps\Claude_", StringComparison.OrdinalIgnoreCase)
        && path.EndsWith(@"\app\claude.exe", StringComparison.OrdinalIgnoreCase);

    /// <summary>The highest registered package version for this user. That is the build the next launch runs.</summary>
    internal static ClaudeDesktopPackage? FindPackage()
    {
        using var packages = Registry.CurrentUser.OpenSubKey(
            @"Software\Classes\Local Settings\Software\Microsoft\Windows\CurrentVersion\AppModel\Repository\Packages");
        if (packages is null) return null;
        ClaudeDesktopPackage? best = null;
        foreach (var name in packages.GetSubKeyNames())
        {
            if (ParsePackageName(name) is not { } parsed) continue;
            using var key = packages.OpenSubKey(name);
            var directory = key?.GetValue("PackageRootFolder") as string
                ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "WindowsApps", name);
            if (best is null || parsed.Version > best.Version) best = parsed with { InstallDirectory = directory };
        }
        return best;
    }

    internal static ClaudeDesktopPackage? ParsePackageName(string fullName)
    {
        // Claude_2.16120.0.0_x64__pzs8sxrjxfjjc
        if (!fullName.StartsWith("Claude_", StringComparison.Ordinal)
            || !fullName.EndsWith(PublisherSuffix, StringComparison.Ordinal)) return null;
        var parts = fullName.Split('_');
        if (parts.Length < 5 || !Version.TryParse(parts[1], out var version)) return null;
        return new ClaudeDesktopPackage(fullName, version, "", "Claude" + PublisherSuffix[1..]);
    }

    /// <summary>Checks that the installed app still contains the behaviors the shelf design relies on. This is a
    /// guard against silent drift, not a proof: it pauses writes when an update moves or renames the stores and log
    /// lines the widget follows, and reports a version whose deletion protection reads differently as unreviewed.</summary>
    internal static ClaudeDesktopContract Contract(string asarPath)
    {
        FileInfo info;
        try { info = new FileInfo(asarPath); if (!info.Exists) return ClaudeDesktopContract.Incompatible; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return ClaudeDesktopContract.Incompatible; }
        var key = asarPath + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        lock (ContractGate) if (_contract is { } cached && cached.Key == key) return cached.Contract;
        ClaudeDesktopContract contract;
        try { contract = Contract(File.ReadAllBytes(asarPath)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { contract = ClaudeDesktopContract.Incompatible; }
        lock (ContractGate) _contract = (key, contract);
        return contract;
    }

    internal static ClaudeDesktopContract Contract(ReadOnlySpan<byte> bundle)
    {
        foreach (var spellings in ContractMarkers)
        {
            var found = false;
            foreach (var spelling in spellings) found |= bundle.IndexOf(Encoding.UTF8.GetBytes(spelling)) >= 0;
            if (!found) return ClaudeDesktopContract.Incompatible;
        }
        return Protects(bundle) ? ClaudeDesktopContract.Reviewed : ClaudeDesktopContract.Unreviewed;
    }

    /// <summary>Whether the bundle reads as reviewed: every marker, connected window and data flow is there.</summary>
    internal static bool IsCompatible(ReadOnlySpan<byte> bundle) => Contract(bundle) == ClaudeDesktopContract.Reviewed;

    /// <summary>Whether the deletion protection reads as reviewed: the connected windows and data flows below.</summary>
    private static bool Protects(ReadOnlySpan<byte> bundle) => Unmatched(bundle, firstOnly: true).Count == 0;

    /// <summary>Which connected windows and data flows do not read as reviewed (by index, a flow's number in the
    /// comment above being its index plus one), so the next review starts where the shape moved. Read-only.</summary>
    internal static IReadOnlyList<string> Unmatched(ReadOnlySpan<byte> bundle, bool firstOnly = false)
    {
        var failed = new List<string>();
        for (var index = 0; index < ContractWindows.Length; index++)
        {
            var (anchor, window, required) = ContractWindows[index];
            var anchorBytes = Encoding.UTF8.GetBytes(anchor);
            var satisfied = false;
            for (int offset = 0, found; !satisfied && (found = bundle[offset..].IndexOf(anchorBytes)) >= 0;
                 offset += found + anchorBytes.Length)
            {
                var start = offset + found;
                var region = bundle.Slice(start, Math.Min(window, bundle.Length - start));
                satisfied = true;
                foreach (var text in required)
                    if (region.IndexOf(Encoding.UTF8.GetBytes(text)) < 0) { satisfied = false; break; }
            }
            if (!satisfied) { failed.Add($"window {index} ({anchor})"); if (firstOnly) return failed; }
        }
        var shared = new Dictionary<string, string>(StringComparer.Ordinal);
        for (var index = 0; index < ContractFlows.Length; index++)
        {
            var (anchor, before, after, flow) = ContractFlows[index];
            var anchorBytes = Encoding.UTF8.GetBytes(anchor);
            var satisfied = false;
            string? conflict = null;
            for (int offset = 0, found; !satisfied && (found = bundle[offset..].IndexOf(anchorBytes)) >= 0;
                 offset += found + anchorBytes.Length)
            {
                var start = Math.Max(0, offset + found - before);
                var end = Math.Min(bundle.Length, offset + found + after);
                var text = Encoding.UTF8.GetString(bundle[start..end]);
                var match = flow.Match(text);
                satisfied = match.Success;
                if (!satisfied) continue;
                foreach (var name in SharedGroups)
                {
                    if (match.Groups[name] is not { Success: true } named) continue;
                    var at = start + Encoding.UTF8.GetByteCount(text.AsSpan(0, named.Index));
                    // A name that cannot be resolved to one function links nothing.
                    if (Function(bundle, named.Value, at) is not { } function
                        || !shared.TryAdd(name, function) && shared[name] != function) conflict ??= name;
                }
                // A matching place that names another function than an earlier flow fails the flow outright.
                if (conflict is not null) break;
            }
            if (!satisfied || conflict is not null)
            {
                failed.Add($"flow {index}" + (conflict is null ? $" ({anchor})" : $" (shared {conflict})"));
                if (firstOnly) return failed;
            }
        }
        return failed;
    }

    private static readonly byte[] SourceMapTrailer = "//# sourceMappingURL="u8.ToArray();
    private const int HeaderBytes = 65536, ExportTailBytes = 8192;

    /// <summary>The function a shared name at <paramref name="at"/> stands for, so flows in different bundled files
    /// still have to name one function (since 5.11492 the transcript removal lives in a chunk of its own). Each bundled
    /// file ends with its source map comment, which names the file. An import (<c>Gt.f</c>) is the export of the file
    /// the caller's file requires under that name in its header; a function its own file exports at the end is that
    /// export; any other name is that file's local function (itself, in a bundle not split into files). Null when an
    /// import cannot be resolved.</summary>
    private static string? Function(ReadOnlySpan<byte> bundle, string name, int at)
    {
        var dot = name.IndexOf('.');
        if (dot >= 0)
        {
            var previous = bundle[..at].LastIndexOf(SourceMapTrailer);
            var fileStart = previous < 0 ? 0 : previous + SourceMapTrailer.Length;
            var header = Encoding.UTF8.GetString(bundle.Slice(fileStart, Math.Min(HeaderBytes, at - fileStart)));
            var required = Regex.Match(header, @"(?<![\w$.])" + Regex.Escape(name[..dot])
                + @"=require\(""\./(?<file>[\w.-]+\.js)""\)", RegexOptions.CultureInvariant);
            return required.Success ? required.Groups["file"].Value + "#" + name[(dot + 1)..] : null;
        }
        var trailer = bundle[at..].IndexOf(SourceMapTrailer);
        if (trailer < 0) return name;
        var fileEnd = at + trailer;
        var after = bundle[(fileEnd + SourceMapTrailer.Length)..];
        var mapEnd = after.IndexOf(".map"u8);
        if (mapEnd < 0) return name;
        var file = Encoding.UTF8.GetString(after[..mapEnd]);
        var tailStart = Math.Max(at, fileEnd - ExportTailBytes);
        var exported = Regex.Match(Encoding.UTF8.GetString(bundle[tailStart..fileEnd]),
            @"(?<![\w$.])exports\.(?<export>[\w$]+)=" + Regex.Escape(name) + "(?![\\w$])", RegexOptions.CultureInvariant);
        return exported.Success ? file + "#" + exported.Groups["export"].Value : file + ":" + name;
    }

    internal static void Launch(ClaudeDesktopPackage package) =>
        Process.Start(new ProcessStartInfo("shell:AppsFolder\\" + package.AppUserModelId) { UseShellExecute = true })?.Dispose();

    private static string? ImagePath(int processId, out DateTime? created, out bool missing)
    {
        created = null; missing = false;
        using var handle = OpenProcess(0x1000, false, processId); // PROCESS_QUERY_LIMITED_INFORMATION
        if (handle.IsInvalid) { missing = Marshal.GetLastWin32Error() == ErrorInvalidParameter; return null; }
        if (GetProcessTimes(handle, out var start, out _, out _, out _)) created = DateTime.FromFileTime(start);
        var buffer = new StringBuilder(32768);
        var size = buffer.Capacity;
        return QueryFullProcessImageName(handle, 0, buffer, ref size) ? buffer.ToString(0, size) : null;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern Microsoft.Win32.SafeHandles.SafeProcessHandle OpenProcess(uint access, bool inherit, int processId);
    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessTimes(Microsoft.Win32.SafeHandles.SafeProcessHandle process, out long creation,
        out long exit, out long kernel, out long user);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "QueryFullProcessImageNameW")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryFullProcessImageName(Microsoft.Win32.SafeHandles.SafeProcessHandle process, int flags,
        StringBuilder name, ref int size);
}

/// <summary>Follows the app's main log in order, from the start of the current app process only. A new run, a
/// sign-out or an unregistered account invalidates the previous observation.</summary>
internal sealed class ClaudeDesktopLogWatcher(string? directory = null)
{
    private const long MaxRead = 16 * 1024 * 1024;
    private static readonly TimeSpan ClockSkew = TimeSpan.FromSeconds(2);
    private static readonly Regex Transition = new(
        @"^(?<at>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) \[info\] \[account\] Login-state transition \(loggedOut: \w+ → \w+, uuid: (?<from>[0-9a-fA-F-]{36}|<none>) → (?<to>[0-9a-fA-F-]{36}|<none>)\)",
        RegexOptions.CultureInvariant);
    private static readonly Regex Initialized = new(
        @"^(?<at>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) \[info\] \[LocalSessionManager\] Initialization succeeded — accountId=(?<account>[0-9a-fA-F-]{36}), orgId=(?<org>[0-9a-fA-F-]{36})",
        RegexOptions.CultureInvariant);
    private static readonly Regex Loaded = new(
        @"^(?<at>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) \[info\] (?:Loaded (?<count>\d+) persisted sessions from|\[LocalSessionManager\] Session storage directory does not exist yet, skipping load:) (?<path>.+?claude-code-sessions[\\/](?<account>[0-9a-fA-F-]{36})[\\/](?<org>[0-9a-fA-F-]{36}))",
        RegexOptions.CultureInvariant);
    private readonly string _directory = directory ?? ClaudeDesktopProbe.LogDirectory;
    private DateTime? _generation;
    private long _offset;
    // The file behind main.log, by volume and file index. Creation time cannot tell: on NTFS a file created under a
    // name that was just renamed away inherits the old creation time, and the app's rotation does exactly that.
    private string? _file;
    private ClaudeDesktopAccountState _state = ClaudeDesktopAccountState.Closed;
    private readonly HashSet<string> _held = new(StringComparer.Ordinal);
    internal ClaudeDesktopAccountState State => _state;
    /// <summary>The last update found lines lost (a gap): anything could have happened in them, a switch included.</summary>
    internal bool LostLines { get; private set; }
    /// <summary>Accounts this run of the app left since it last confirmed one (signed out of, or went from to sign in to
    /// another): it ends an account's Code sessions only once it shows the next one, so it still holds their lists. They
    /// come from the log itself, so a replay (a widget started meanwhile, or after a gap) knows them as well.</summary>
    internal IReadOnlyCollection<string> Held => _held;

    internal ClaudeDesktopAccountState Update(DateTime? hostStartedAt, IReadOnlyCollection<ClaudeShelfAccount> registered)
    {
        LostLines = false;
        if (hostStartedAt is null) { _generation = null; _held.Clear(); return _state = ClaudeDesktopAccountState.Closed; }
        var main = Path.Combine(_directory, "main.log");
        var previous = Path.Combine(_directory, "main1.log");
        List<string> lines = [];
        LogRead result;
        long end = 0; string? file = null;
        var replay = _generation != hostStartedAt;
        if (replay)
        {
            // A new app run, or a replay after a gap: forget the previous observation and replay this run from both
            // log files. An unfinished last line in main1.log may have been a sign-out or switch, so nothing before
            // it may confirm an account; only the complete lines of main.log count then.
            _state = ClaudeDesktopAccountState.Unknown;
            result = File.Exists(previous) ? ReadFrom(previous, 0, null, true, lines, out _, out _) : LogRead.Ok;
            if (result == LogRead.Unfinished) result = LogRead.Ok;
            if (result == LogRead.Ok) result = ReadFrom(main, 0, null, false, lines, out end, out file);
        }
        else if ((result = ReadFrom(main, _offset, _file, false, lines, out end, out file)) == LogRead.OtherFile)
        {
            // Rotation renamed the file we were reading to main1.log. Finish it from where we stopped, then read the
            // new file. If main1.log is not that file, or its last line is unfinished, lines were lost.
            result = ReadFrom(previous, _offset, _file, true, lines, out _, out _);
            if (result == LogRead.Ok) result = ReadFrom(main, 0, null, false, lines, out end, out file);
        }
        if (result != LogRead.Ok)
        {
            // A gap in the log could hide a sign-out or switch: never keep a confirmation across it.
            _generation = null; LostLines = true;
            return _state = ClaudeDesktopAccountState.Unknown;
        }
        _generation = hostStartedAt; _offset = end; _file = file;
        // A replay reads the whole run again, the accounts it left included.
        if (replay) _held.Clear();
        return _state = Apply(_state, lines, hostStartedAt.Value, registered, _held);
    }

    /// <param name="held">Collects the accounts the lines leave, until one confirms an account (see <see cref="Held"/>).</param>
    internal static ClaudeDesktopAccountState Apply(ClaudeDesktopAccountState state, IEnumerable<string> lines,
        DateTime since, IReadOnlyCollection<ClaudeShelfAccount> registered, ISet<string>? held = null)
    {
        var accounts = registered.Select(account => account.AccountUuid.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var pairs = registered.Select(account => account.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (line.Length < 20 || !DateTime.TryParseExact(line[..19], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var at) || at < since - ClockSkew) continue;
            if (Transition.Match(line) is { Success: true } transition)
            {
                // The line names the account left even when what came before it was not read.
                var left = transition.Groups["from"].Value.ToLowerInvariant();
                if (left != "<none>") held?.Add(left);
                if (state.AccountUuid is { } before) held?.Add(before);
                var target = transition.Groups["to"].Value.ToLowerInvariant();
                state = target == "<none>" ? new(ClaudeDesktopAccountStatus.SignedOut, ObservedAt: at)
                    : accounts.Contains(target) ? new(ClaudeDesktopAccountStatus.SignedIn, target, ObservedAt: at)
                    : new(ClaudeDesktopAccountStatus.Unregistered, target, ObservedAt: at);
                continue;
            }
            if (Initialized.Match(line) is { Success: true } initialized)
            {
                var account = initialized.Groups["account"].Value.ToLowerInvariant();
                var org = initialized.Groups["org"].Value.ToLowerInvariant();
                // After sign-out, or for a different account, an initialization is a transitional pair.
                if (!pairs.Contains(account + "/" + org) || state.Status is ClaudeDesktopAccountStatus.SignedOut
                    or ClaudeDesktopAccountStatus.Unregistered
                    || state.AccountUuid is { } current && current != account) continue;
                state = new(ClaudeDesktopAccountStatus.SignedIn, account, org, ObservedAt: at);
                // The app shows this account now; the ones it left go as their sessions end.
                held?.Clear();
                continue;
            }
            if (Loaded.Match(line) is { Success: true } loaded && state.Confirmed
                && loaded.Groups["account"].Value.Equals(state.AccountUuid, StringComparison.OrdinalIgnoreCase)
                && loaded.Groups["org"].Value.Equals(state.OrgUuid, StringComparison.OrdinalIgnoreCase))
                state = state with { LoadedSessions = int.TryParse(loaded.Groups["count"].Value, out var count) ? count : 0 };
        }
        return state;
    }

    private enum LogRead { Ok, OtherFile, Unfinished, Failed }

    /// <summary>Appends the complete lines after <paramref name="offset"/> and identifies the file, both through one
    /// handle. With <paramref name="expectedFile"/>, another file now carrying the name reads nothing and returns
    /// OtherFile. A <paramref name="final"/> read (a rotated file) that ends mid-line reads nothing and returns
    /// Unfinished. An empty increment is a success; an unreadable file or a lost range is a failure.</summary>
    private static LogRead ReadFrom(string path, long offset, string? expectedFile, bool final, List<string> lines,
        out long end, out string? file)
    {
        end = offset; file = null;
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            if (!GetFileInformationByHandle(stream.SafeFileHandle, out var information)) return LogRead.Failed;
            var identity = $"{information.VolumeSerialNumber:x8}-{information.FileIndexHigh:x8}{information.FileIndexLow:x8}";
            if (expectedFile is not null && identity != expectedFile) return LogRead.OtherFile;
            var length = stream.Length;
            // Shrunk in place, or too far behind: replay the run instead.
            if (offset > length || length - offset > MaxRead) return LogRead.Failed;
            stream.Seek(offset, SeekOrigin.Begin);
            var bytes = new byte[length - offset];
            stream.ReadExactly(bytes);
            if (final && bytes.Length > 0 && bytes[^1] != (byte)'\n') return LogRead.Unfinished;
            file = identity;
            // Keep a trailing partial line for the next read.
            var last = bytes.Length == 0 ? -1 : Array.LastIndexOf(bytes, (byte)'\n');
            if (last < 0) return LogRead.Ok;
            end = offset + last + 1;
            lines.AddRange(Encoding.UTF8.GetString(bytes, 0, last + 1).Split('\n').Select(line => line.TrimEnd('\r')));
            return LogRead.Ok;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return LogRead.Failed; }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint FileAttributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime, LastAccessTime, LastWriteTime;
        public uint VolumeSerialNumber, FileSizeHigh, FileSizeLow, NumberOfLinks, FileIndexHigh, FileIndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(Microsoft.Win32.SafeHandles.SafeFileHandle file,
        out FileInformation information);
}

/// <summary>Maps registered Claude profiles to Desktop account/organization pairs using the non-secret
/// <c>oauthAccount</c> section of the official global config. Tokens live elsewhere and are never read.</summary>
internal static class ClaudeShelfAccounts
{
    private const long MaxConfigBytes = 32 * 1024 * 1024;

    internal static IReadOnlyList<ClaudeShelfAccount> Resolve(IEnumerable<ClaudeProfile> profiles, string ledgerDirectory,
        string? homeDirectory = null)
    {
        var cachePath = Path.Combine(ledgerDirectory, "accounts.json");
        var cache = ReadCache(cachePath);
        var accounts = new List<ClaudeShelfAccount>();
        var current = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        foreach (var profile in profiles.Where(profile => profile.IsVerified))
        {
            var configPath = GlobalConfigPath(profile, homeDirectory);
            var entry = ReadAccount(configPath, profile.Identity!.Key) is { } read
                ? new CacheEntry(profile.Id, profile.Identity.Key, configPath, read.Account, read.Org)
                // The default home follows the terminal login. Keep the last mapping verified for this same
                // profile, identity and config path; it names a folder, not a current login.
                : cache.TryGetValue(profile.Id, out var previous) && previous.IdentityKey == profile.Identity.Key
                  && ClaudeProfileStore.PathsEqual(previous.ConfigPath, configPath) ? previous : null;
            if (entry is null) continue;
            current[profile.Id] = entry;
            accounts.Add(new ClaudeShelfAccount(entry.Account, entry.Org, profile.Label, profile.Id));
        }
        // Removed profiles drop out of the cache; they never come back as fill targets.
        if (!current.Values.OrderBy(entry => entry.ProfileId).SequenceEqual(cache.Values.OrderBy(entry => entry.ProfileId)))
            WriteCache(cachePath, current);
        return accounts.DistinctBy(account => account.Key).ToArray();
    }

    internal static string GlobalConfigPath(ClaudeProfile profile, string? homeDirectory = null) => profile.UseDefaultHome
        ? Path.Combine(homeDirectory ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude.json")
        : Path.Combine(profile.ConfigDirectory, ".claude.json");

    internal static (string Account, string Org)? ReadAccount(string configPath, string identityKey)
    {
        try
        {
            var info = new FileInfo(configPath);
            if (!info.Exists || info.Length > MaxConfigBytes) return null;
            using var document = JsonDocument.Parse(File.ReadAllBytes(configPath),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (document.RootElement.ValueKind != JsonValueKind.Object) return null;
            // Duplicate keys are tolerated elsewhere in this file, but conflicting account sections are not.
            var sections = document.RootElement.EnumerateObject().Where(property => property.Name == "oauthAccount")
                .Select(property => property.Value).ToArray();
            if (sections.Length == 0 || sections.Select(section => section.GetRawText()).Distinct().Count() > 1) return null;
            var oauth = sections[0];
            if (oauth.ValueKind != JsonValueKind.Object) return null;
            string? Read(string name)
            {
                var values = oauth.EnumerateObject().Where(property => property.Name == name).ToArray();
                return values.Length == 1 && values[0].Value.ValueKind == JsonValueKind.String ? values[0].Value.GetString() : null;
            }
            var account = Read("accountUuid"); var org = Read("organizationUuid"); var email = Read("emailAddress");
            if (!Guid.TryParse(account, out _) || !Guid.TryParse(org, out _) || string.IsNullOrWhiteSpace(email)) return null;
            return ClaudeProfileRuntime.IdentityKey(email, org!) == identityKey
                ? (account!.ToLowerInvariant(), org!.ToLowerInvariant()) : null;
        }
        catch (Exception exception) when (exception is JsonException or IOException or UnauthorizedAccessException)
        { return null; }
    }

    private sealed record CacheEntry(string ProfileId, string IdentityKey, string ConfigPath, string Account, string Org);

    private static Dictionary<string, CacheEntry> ReadCache(string path)
    {
        var cache = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        try
        {
            if (!File.Exists(path) || JsonNode.Parse(File.ReadAllBytes(path))?["accounts"] is not JsonArray items) return cache;
            foreach (var item in items)
            {
                if (item is not JsonObject entry) continue;
                string? Read(string name) => entry[name] is JsonValue value && value.TryGetValue<string>(out var text) ? text : null;
                if (Read("profileId") is { Length: > 0 } profile && Read("identityKey") is { Length: 64 } key
                    && Read("configPath") is { Length: > 0 } config
                    && Guid.TryParse(Read("accountUuid"), out var account) && Guid.TryParse(Read("orgUuid"), out var org))
                    cache[profile] = new CacheEntry(profile, key, config, account.ToString("D"), org.ToString("D"));
            }
        }
        catch (Exception exception) when (exception is JsonException or IOException or InvalidOperationException) { }
        return cache;
    }

    private static void WriteCache(string path, Dictionary<string, CacheEntry> cache)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var root = new JsonObject
        {
            ["version"] = 1,
            ["accounts"] = new JsonArray(cache.Values.Select(entry => (JsonNode?)new JsonObject
            {
                ["profileId"] = entry.ProfileId, ["identityKey"] = entry.IdentityKey, ["configPath"] = entry.ConfigPath,
                ["accountUuid"] = entry.Account, ["orgUuid"] = entry.Org
            }).ToArray())
        };
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllText(temporary, root.ToJsonString()); File.Move(temporary, path, overwrite: true); }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
}
