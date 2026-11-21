using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
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
        // 6.28899 gives the list of ids the delete writes tombstones for the checks' name, declared in the block that
        // writes them, which closes before the removal: that declaration, up to its block's end, is passed over, and
        // nothing else may assign to the name.
        ("(this.reapChecks,", 60, 2400, new Regex(
            @"let (?<v>[\w$]+)=(?<maker>[\w$]+)\(this\.reapChecks,[^;]{0,400}?(?<![\w$.])(?<aside>[\w$]+)\(this\.reapChecks,\k<v>,(?:(?<![\w$.])let [\w$]+=this\.transcriptIdClaims\.cliSessionIdsClaimedByLoadedSessions\(\{[^{}]*\}\),\k<v>=\[\.\.\.new Set\(\[[^;{}]*?\]\.filter\([^;{}]*?\)\)\];await [\w$]+\.[\w$]+\(\(0,[\w$]+\.dirname\)\([\w$]+\),\k<v>,Date\.now\(\)\)(?:,[\w$]+\.[\w$]+\(\))?\}|(?!(?<![\w$.])\k<v>=(?!=)).){0,2300}?(?<![\w$.])(?<remover>(?:[\w$]+\.)?[\w$]+)\(this\.diskTranscript,[^;]{0,120},""delete"",\k<v>\)",
            RegexOptions.CultureInvariant | RegexOptions.Singleline)),
        // (5) Since 5.11492 the removal first waits, out of the shared limit of removals at once, for the checks'
        // `reapAfter` (a wait that swallows its failure and returns nothing), then asks the reap check as before.
        // 6.28899 splits it in two, joined by the function the chunk exports: a first pass asks the release test and
        // writes markers, removing nothing (a transcript refused or not tested keeps no `resolved` folder), and a
        // second pass, given the same checks, asks the reap check of each marked transcript.
        ("desktop-released marker: reap check failed", 1600, 1100, new Regex(
            @"(?:async function (?<remover>[\w$]+)\([^()]*\)\{[^{}]{0,160}?async(?:\(\)| (?<w>[\w$]+))=>\{try\{if\((?<m>[\w$]+)\.mayRelease&&!await \k<m>\.mayRelease\((?<c>[\w$]+)\)\)return\}catch\{return\}.{0,900}?if\([\w$]+===null\|\|![\w$]+\|\|!\k<m>\.reapIf\)return;(?:await \k<w>\(\k<m>\.reapAfter\);)?let (?<d>[\w$]+)=!1;try\{(?:(?!(?<![\w$.])\k<d>=(?!=)).){0,700}?(?<![\w$.])\k<d>=await \k<m>\.reapIf\(\k<c>,\{[^{}]*\}\)\}+catch\([\w$]+\)\{[\w$.]+\.debug\(""desktop-released marker: reap check failed"",\{[^{}]*\}\);try\{\k<m>\.keptInDoubt\?\.\(\k<c>\)\}catch\{\}\}(?:if\(\k<d>\)for\(|if\(!\k<d>\)return;)"
            + @"|async function (?<mark>[\w$]+)\([\w$]+,(?<ids>[\w$]+),[\w$]+,[\w$]+,(?<a>[\w$]+)=\{\}\)\{let [\w$]+=\[\],(?<all>[\w$]+)=\[\],[\w$]+;for\(let (?<l>[\w$]+) of new Set\(\k<ids>\)\)\{if\(!\k<l>\|\|![\w$]+\(\k<l>\)\)continue;let (?<st>[\w$]+)=\{cliSessionId:\k<l>,declined:!1,resolved:null,dirs:\[\],marked:\[\],complete:!1\};\k<all>\.push\(\k<st>\),[\w$]+\.push\([\w$]+\(\k<l>,async(?:\(\)| [\w$]+)=>\{try\{if\(\k<a>\.mayRelease&&!await \k<a>\.mayRelease\(\k<l>\)\)\{\k<st>\.declined=!0;return\}\}catch\{return\}(?:(?!\.(?:rm|rmdir|unlink|rename)\().){0,800}?\}\)\)\}return await Promise\.all\([\w$]+\),\k<all>\}"
            + @"async function (?<reap>[\w$]+)\((?<st2>[\w$]+),(?<m>[\w$]+)\)\{let (?<q>[\w$]+)=\[\],[\w$]+=\[\],[\w$]+;for\(let\{cliSessionId:(?<c>[\w$]+),resolved:(?<s>[\w$]+),dirs:[\w$]+,marked:(?<mk>[\w$]+),complete:[\w$]+\}of \k<st2>\)\k<q>\.push\([\w$]+\(\k<c>,async (?<w>[\w$]+)=>\{if\(![\w$]+\(\k<c>\)\|\|\k<s>===null\|\|!\k<mk>\.includes\(\k<s>\)\|\|!\k<m>\.reapIf\)return;(?:await \k<w>\(\k<m>\.reapAfter\);)?let (?<d>[\w$]+)=!1;try\{(?:(?!(?<![\w$.])\k<d>=(?!=)|(?<![\w$.])(?:\k<m>|\k<c>)=(?![=>])|\.(?:rm|rmdir|unlink|rename)\().){0,700}?(?<![\w$.])\k<d>=await \k<m>\.reapIf\(\k<c>,\{[^{}]*\}\)\}+catch\([\w$]+\)\{[\w$.]+\.debug\(""desktop-released marker: reap check failed"",\{[^{}]*\}\);try\{\k<m>\.keptInDoubt\?\.\(\k<c>\)\}catch\{\}\}if\(!\k<d>\)return;(?:(?!async function ).){0,1000}?\}"
            + @"async function (?<remover>[\w$]+)\((?<o1>[\w$]+),(?<o2>[\w$]+),(?<o3>[\w$]+),(?<o4>[\w$]+),(?<o5>[\w$]+)=\{\}\)\{return \k<reap>\(await \k<mark>\(\k<o1>,\k<o2>,\k<o3>,\k<o4>,\k<o5>\),\k<o5>\)\})",
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

    internal static string LogDirectory => ClaudeAppData.LogDirectory;

    /// <summary>The packaged app or one of its Code CLIs counts as running. Terminal CLIs are not Desktop writers.
    /// A process whose image cannot be inspected makes the answer unknown, never "closed".</summary>
    internal static ClaudeDesktopRuntime Observe()
    {
        // The app runs its Code CLIs from its profile folder, wherever that is (ClaudeAppData); found when first needed.
        IReadOnlyList<string>? cliRoots = null;
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
                else if ((cliRoots ??= ClaudeAppData.CliRoots()).Any(root => path.StartsWith(root, StringComparison.OrdinalIgnoreCase)))
                    running = true;
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

    // The scheduled-task alignment (ClaudeScheduledSync) depends on the app keeping each account's list the way 6.28899
    // does, apart from the session lists: a missing marker or a flow or schema that reads differently stops that
    // feature alone. (1) the list is userData\claude-code-sessions\account\org\scheduled-tasks.json, (2) for the Code
    // tasks' manager, whose definitions are CLAUDE_CONFIG_DIR (a leading ~ standing for the home folder) or ~\.claude,
    // then scheduled-tasks (8-10), (3) read in one place, parsed whole with one schema, a failure loading none of it,
    // (4) called only by initialize, which runs at start and after a reset (an account change), with the path it has just
    // stored, and logs the list it loaded (7), (5) a save writes the whole list from memory, and only to the path loaded,
    // while no reset came between, (6) which a reset stops, and logs. Only the account hooks reset and initialize the
    // manager (ScheduledHooks). The list's own schema, with every schema it is made of, must read as reviewed too
    // (ScheduledSchemaHash), since a list the widget writes has to pass it. Nothing else reads or writes that list (one
    // call of the load, one of the writer).
    internal static readonly string[][] ScheduledMarkers =
    [
        ["\"scheduled-tasks.json\""],
        ["logPrefix:\"[CCDScheduledTasks]\""],
        ["Failed to load scheduled tasks:"],
        [@"persistToDisk aborted \u2014 superseded by reset()", "persistToDisk aborted — superseded by reset()"]
    ];
    internal static readonly (string Anchor, int Before, int After, Regex Flow)[] ScheduledFlows =
    [
        (",\"scheduled-tasks.json\")}", 220, 30, new Regex(
            @"function (?<path>[\w$]+)\((?<a>[\w$]+),(?<o>[\w$]+)\)\{return [\w$]+\.default\.join\([\w$]+\.app\.getPath\(""userData""\),(?<root>[\w$]+),\k<a>,\k<o>,""scheduled-tasks\.json""\)\}",
            RegexOptions.CultureInvariant)),
        ("=\"claude-code-sessions\"", 40, 30, new Regex(@"(?<![\w$.])(?<root>[\w$]+)=""claude-code-sessions""[,;]", RegexOptions.CultureInvariant)),
        ("logPrefix:\"[CCDScheduledTasks]\"", 120, 1600, new Regex(
            @"(?<![\w$.])(?<manager>[\w$]+)=new [\w$]+\(\{logPrefix:""\[CCDScheduledTasks\]"",[^;]{0,1600}?getStorageFilePath:(?<path>[\w$]+),getTaskFilesDir:(?<defs>[\w$]+),",
            RegexOptions.CultureInvariant)),
        ("async loadScheduledTasksFromDisk(", 0, 600, new Regex(
            @"^async loadScheduledTasksFromDisk\((?<p>[\w$]+)\)\{try\{let (?<t>[\w$]+)=await [\w$]+\(\k<p>,[\w$]+,\{walkRoot:\(0,[\w$]+\.dirname\)\(\k<p>\),lstatWalkRoot:!1\}\),(?<r>[\w$]+)=JSON\.parse\(\k<t>\);return (?<file>[\w$]+)\.parse\(\k<r>\)\}catch\((?<x>[\w$]+)\)\{return \k<x>\.code===""ENOENT""\?\{scheduledTasks:\[\]\}:\((?<log>[\w$]+)\.error\(`\$\{this\.config\.logPrefix\} Failed to load scheduled tasks:`,[\w$]+\(\k<x>\)\),\{scheduledTasks:\[\],loadFailed:!0\}\)\}\}",
            RegexOptions.CultureInvariant)),
        (".loadScheduledTasksFromDisk(", 1500, 40, new Regex(
            @"initialize\((?<a>[\w$]+),(?<o>[\w$]+)\)\{if\(this\._initializePromise\)\{[^;]{0,120}?Already initialized`\);return\}[^;]{0,160}?;let (?<p>[\w$]+)=this\.config\.getStorageFilePath\(\k<a>,\k<o>\);this\._scheduledTasksFilePath=\k<p>;(?:(?!this\._scheduledTasksFilePath=|(?<![\w$.])\k<p>=(?!=)).){0,1400}?this\.loadScheduledTasksFromDisk\(\k<p>\)",
            RegexOptions.CultureInvariant | RegexOptions.Singleline)),
        ("persistToDisk(){let ", 0, 1500, new Regex(
            @"^persistToDisk\(\)\{let (?<c>[\w$]+)=this\._initCounter,(?<p>[\w$]+)=this\._scheduledTasksFilePath;return this\._persistMutex\.runExclusive\(\(\(\)=>this\.writeSnapshotToDisk\(\k<c>,\k<p>\)\)\)\}async writeSnapshotToDisk\((?<c2>[\w$]+),(?<p2>[\w$]+)\)\{if\(!\k<p2>\)return!1;if\(\k<c2>!==this\._initCounter\)return [^;]{0,160}?,!1;try\{if\(await this\.ensureStorageDir\(\k<p2>\),\k<c2>!==this\._initCounter\|\|\k<p2>!==this\._scheduledTasksFilePath\)return [^;]{0,160}?,!1;(?:(?!this\._scheduledTasksFilePath|(?<![\w$.])\k<p2>=(?!=)).){0,800}?return await [\w$]+\(\k<p2>,\{scheduledTasks:Array\.from\(this\._scheduledTasks\.values\(\)\),recordedSkips:",
            RegexOptions.CultureInvariant | RegexOptions.Singleline)),
        ("reset(){this._initCounter++,", 0, 2200, new Regex(
            @"^reset\(\)\{this\._initCounter\+\+,.{0,2000}?this\._scheduledTasksFilePath=null,this\._taskFilesDir=null,this\._initializePromise=null(?:(?!this\._loadedFor=).){0,1800}?this\._loadedFor=null,(?<log>[\w$]+)\.info\(`\$\{this\.config\.logPrefix\} Reset`\)\}",
            RegexOptions.CultureInvariant | RegexOptions.Singleline)),
        // The line ClaudeDesktopLogWatcher reads for the list the manager loaded, logged by initialize with its arguments.
        ("} Initialized`", 3600, 120, new Regex(
            @"initialize\((?<a>[\w$]+),(?<o>[\w$]+)\)\{if\(this\._initializePromise\)\{(?:(?!initialize\(|reset\(\)\{).){0,3500}?(?<log>[\w$]+)\.info\(`\$\{this\.config\.logPrefix\} Initialized`,\{accountId:\k<a>,orgId:\k<o>\}\)",
            RegexOptions.CultureInvariant | RegexOptions.Singleline)),
        ("(),\"scheduled-tasks\")}", 120, 30, new Regex(
            @"function (?<defs>[\w$]+)\(\)\{return\(0,[\w$]+\.join\)\((?<config>[\w$]+)\(\),""scheduled-tasks""\)\}",
            RegexOptions.CultureInvariant)),
        ("=process.env.CLAUDE_CONFIG_DIR;return ", 40, 140, new Regex(
            @"function (?<config>[\w$]+)\(\)\{let (?<e>[\w$]+)=process\.env\.CLAUDE_CONFIG_DIR;return \k<e>\?(?<tilde>[\w$]+)\(\k<e>\):\(0,[\w$]+\.join\)\(\(0,[\w$]+\.homedir\)\(\),""\.claude""\)\}",
            RegexOptions.CultureInvariant)),
        ("===\"~\"?(0,", 40, 200, new Regex(
            @"function (?<tilde>[\w$]+)\((?<e>[\w$]+)\)\{return \k<e>===""~""\?\(0,[\w$]+\.homedir\)\(\):\k<e>\.startsWith\(""~/""\)\|\|\k<e>\.startsWith\(""~\\\\""\)\?\(0,[\w$]+\.join\)\(\(0,[\w$]+\.homedir\)\(\),\k<e>\.slice\(2\)\):\k<e>\}",
            RegexOptions.CultureInvariant))
    ];
    private const int ScheduledManagerFlow = 2;
    private static readonly string[] ScheduledShared = ["path", "root", "file", "log", "manager", "defs", "config", "tilde"];
    private static readonly Regex ScheduledFile = new(
        @"(?<![\w$.])(?<file>[\w$]+)=(?<k>[\w$]+)\(\{scheduledTasks:[\w$]+\((?<entry>[\w$]+)\),recordedSkips:", RegexOptions.CultureInvariant);
    // The list's file and entry schemas as reviewed (6.28899) with the schemas they are made of, their minified names
    // numbered in order (ScheduledSchema).
    private const string ScheduledSchemaHash = "BAE3807C12AA3E25EC851E175490F4E09C9C53285233A4B105EEDA2172931ED6";
    private static (string Key, ClaudeDesktopContract Contract)? _scheduledContract;

    /// <summary>Whether the installed app keeps scheduled-task lists the way the alignment was designed for; cached per
    /// bundle like <see cref="Contract(string)"/>.</summary>
    internal static ClaudeDesktopContract ScheduledContract(string asarPath)
    {
        FileInfo info;
        try { info = new FileInfo(asarPath); if (!info.Exists) return ClaudeDesktopContract.Incompatible; }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return ClaudeDesktopContract.Incompatible; }
        var key = asarPath + "|" + info.Length + "|" + info.LastWriteTimeUtc.Ticks;
        lock (ContractGate) if (_scheduledContract is { } cached && cached.Key == key) return cached.Contract;
        ClaudeDesktopContract contract;
        try { contract = ScheduledContract(File.ReadAllBytes(asarPath)); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { contract = ClaudeDesktopContract.Incompatible; }
        lock (ContractGate) _scheduledContract = (key, contract);
        return contract;
    }

    internal static ClaudeDesktopContract ScheduledContract(ReadOnlySpan<byte> bundle)
    {
        foreach (var spellings in ScheduledMarkers)
        {
            var found = false;
            foreach (var spelling in spellings) found |= bundle.IndexOf(Encoding.UTF8.GetBytes(spelling)) >= 0;
            if (!found) return ClaudeDesktopContract.Incompatible;
        }
        return ScheduledUnmatched(bundle, firstOnly: true).Count == 0 ? ClaudeDesktopContract.Reviewed : ClaudeDesktopContract.Unreviewed;
    }

    /// <summary>Which scheduled-list flows (by index), counts, hooks and the schema do not read as reviewed. Read-only.</summary>
    internal static IReadOnlyList<string> ScheduledUnmatched(ReadOnlySpan<byte> bundle, bool firstOnly = false)
    {
        var failed = new List<string>();
        var shared = new Dictionary<string, string>(StringComparer.Ordinal);
        var places = new int[ScheduledFlows.Length];
        for (var index = 0; index < ScheduledFlows.Length; index++)
        {
            var (anchor, before, after, flow) = ScheduledFlows[index];
            var anchorBytes = Encoding.UTF8.GetBytes(anchor);
            var satisfied = false;
            for (int offset = 0, found; !satisfied && (found = bundle[offset..].IndexOf(anchorBytes)) >= 0;
                 offset += found + anchorBytes.Length)
            {
                var start = Math.Max(0, offset + found - before);
                var end = Math.Min(bundle.Length, offset + found + after);
                var match = flow.Match(Encoding.UTF8.GetString(bundle[start..end]));
                if (!match.Success) continue;
                // A name another flow read stands for the same thing here, or this place is not the one.
                if (ScheduledShared.Any(name => match.Groups[name] is { Success: true } named
                        && shared.TryGetValue(name, out var known) && known != named.Value)) continue;
                foreach (var name in ScheduledShared)
                    if (match.Groups[name] is { Success: true } named) shared[name] = named.Value;
                places[index] = offset + found;
                satisfied = true;
            }
            if (!satisfied) { failed.Add($"scheduled flow {index} ({anchor})"); if (firstOnly) return failed; }
        }
        if (Count(bundle, ".loadScheduledTasksFromDisk("u8) != 1 || Count(bundle, "writeSnapshotToDisk("u8) != 2)
        {
            failed.Add("scheduled readers or writers");
            if (firstOnly) return failed;
        }
        if (!shared.TryGetValue("manager", out var manager) || !ScheduledHooks(bundle, manager, places[ScheduledManagerFlow]))
        {
            failed.Add("scheduled hooks");
            if (firstOnly) return failed;
        }
        var schema = shared.TryGetValue("file", out var file) ? ScheduledSchema(bundle, file) : null;
        if (schema is null || !Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(schema)))
                .Equals(ScheduledSchemaHash, StringComparison.OrdinalIgnoreCase))
            failed.Add("scheduled schema");
        return failed;
    }

    private static int Count(ReadOnlySpan<byte> bundle, ReadOnlySpan<byte> text)
    {
        var count = 0;
        for (int offset = 0, found; (found = bundle[offset..].IndexOf(text)) >= 0; offset += found + text.Length) count++;
        return count;
    }

    /// <summary>
    /// Whether only the app's account hooks reset and load the scheduled-task manager the flows read (at
    /// <paramref name="at"/>), so the list it holds changes only where its log says (Reset, Initialized). Its file
    /// exports it once, as a defined property, and sets neither that export nor the manager again; the bundle calls its
    /// reset and its initialize through that export once each, in one file that binds the manager's file to one name in
    /// its header and never sets the export on it: reset in the account-change hook, initialize in the initialized hook
    /// with that hook's own arguments, neither hook binding the name again before the call. The namespace object that
    /// file also exports the manager in is read nowhere by the manager's name there.
    /// </summary>
    internal static bool ScheduledHooks(ReadOnlySpan<byte> bundle, string manager, int at)
    {
        if (FileAt(bundle, at) is not { Name: { } managerFile } managerSpan) return false;
        var file = bundle[managerSpan.Start..managerSpan.End];
        var getter = Encoding.UTF8.GetBytes(",{enumerable:!0,get:function(){return " + manager + "}})");
        var exportAt = file.IndexOf(getter);
        if (exportAt < 0 || file[(exportAt + getter.Length)..].IndexOf(getter) >= 0) return false;
        var define = "Object.defineProperty(exports,\""u8;
        var defineAt = file[..exportAt].LastIndexOf(define);
        if (defineAt < 0) return false;
        var quoted = Encoding.UTF8.GetString(file[(defineAt + define.Length)..exportAt]);
        if (!Regex.IsMatch(quoted, @"^[\w$]+""$", RegexOptions.CultureInvariant)) return false;
        var export = quoted[..^1];
        if (Sets(file, "exports." + export, ownName: false) != 0 || Sets(file, "module.exports", ownName: false) != 0
            || Sets(file, manager, ownName: true) != 1) return false;
        if (Count(bundle, Encoding.UTF8.GetBytes("{ccdScheduledTasks:()=>" + manager + ",")) != 1
            || Count(bundle, "{ccdScheduledTasks:"u8) != 1 || Count(bundle, ".ccdScheduledTasks"u8) != 0
            || Count(bundle, ",ccdScheduledTasks:"u8) != 0 || Count(bundle, "ccdScheduledTasks}"u8) != 0
            || Count(bundle, "\"ccdScheduledTasks\""u8) != 0) return false;
        var reset = Encoding.UTF8.GetBytes("." + export + ".reset()");
        var initialize = Encoding.UTF8.GetBytes("." + export + ".initialize(");
        if (Count(bundle, reset) != 1 || Count(bundle, initialize) != 1) return false;
        var (resetAt, initializeAt) = (bundle.IndexOf(reset), bundle.IndexOf(initialize));
        if (FileAt(bundle, resetAt) is not { } hooks || FileAt(bundle, initializeAt)?.Start != hooks.Start) return false;
        var name = Identifier(bundle, resetAt);
        if (name is null || Identifier(bundle, initializeAt) != name) return false;
        if (Hook(bundle, hooks.Start, resetAt - name.Length, @"onAccountOrgChanged\((?<a>[\w$]+)\)\{", name) is null
            || Hook(bundle, hooks.Start, initializeAt - name.Length, @"async onInitialized\((?<a>[\w$]+),(?<o>[\w$]+)\)\{", name)
                is not { } arguments
            || !bundle[(initializeAt + initialize.Length)..].StartsWith(Encoding.UTF8.GetBytes(arguments + ")"))) return false;
        var header = Encoding.UTF8.GetString(bundle.Slice(hooks.Start, Math.Min(HeaderBytes, hooks.End - hooks.Start)));
        var required = Regex.Matches(header, @"(?<![\w$.])" + Regex.Escape(name) + @"=require\(""\./(?<file>[\w.-]+\.js)""\)",
            RegexOptions.CultureInvariant);
        return required.Count == 1 && required[0].Groups["file"].Value == managerFile
            && Sets(bundle[hooks.Start..hooks.End], name + "." + export, ownName: true) == 0;
    }

    /// <summary>The bundled file around <paramref name="at"/>: from the end of the source map comment before it to its own,
    /// and the file that comment names (null at the bundle's end).</summary>
    private static (int Start, int End, string? Name)? FileAt(ReadOnlySpan<byte> bundle, int at)
    {
        if (at < 0 || at >= bundle.Length) return null;
        var previous = bundle[..at].LastIndexOf(SourceMapTrailer);
        var start = previous < 0 ? 0 : previous + SourceMapTrailer.Length;
        var trailer = bundle[at..].IndexOf(SourceMapTrailer);
        if (trailer < 0) return (start, bundle.Length, null);
        var end = at + trailer;
        var after = bundle[(end + SourceMapTrailer.Length)..];
        var mapEnd = after.IndexOf(".map"u8);
        return (start, end, mapEnd < 0 ? null : Encoding.UTF8.GetString(after[..mapEnd]));
    }

    /// <summary>The name ending just before <paramref name="at"/>; null when none does.</summary>
    private static string? Identifier(ReadOnlySpan<byte> bundle, int at)
    {
        var start = at;
        while (start > 0 && bundle[start - 1] is var c && (char.IsAsciiLetterOrDigit((char)c) || c is (byte)'_' or (byte)'$')) start--;
        return start == at || char.IsAsciiDigit((char)bundle[start]) ? null : Encoding.UTF8.GetString(bundle[start..at]);
    }

    /// <summary>The arguments of the last method <paramref name="header"/> opens before <paramref name="call"/> (joined by
    /// commas), when the call is in its body itself (not in a function or block within it, not inside parentheses), no
    /// argument is <paramref name="name"/> and the body does not bind <paramref name="name"/> before the call (its own
    /// statements, or a <c>var</c> anywhere in it; a loop's own <c>let</c> or <c>const</c> binds it only within the loop,
    /// so one that ends before the call does not count); null otherwise.</summary>
    private static string? Hook(ReadOnlySpan<byte> bundle, int fileStart, int call, string header, string name)
    {
        var from = Math.Max(fileStart, call - 200_000);
        var text = Encoding.UTF8.GetString(bundle[from..call]);
        var methods = Regex.Matches(text, header, RegexOptions.CultureInvariant);
        if (methods.Count == 0) return null;
        var method = methods[^1];
        var arguments = new[] { "a", "o" }.Select(group => method.Groups[group]).Where(group => group.Success)
            .Select(group => group.Value).ToArray();
        if (arguments.Contains(name)) return null;
        var body = text[(method.Index + method.Length)..];
        // The body's own statements, without strings, each block within it shown as {}.
        var own = new StringBuilder();
        int braces = 1, parentheses = 0;
        char? quote = null;
        for (var index = 0; index < body.Length; index++)
        {
            var c = body[index];
            if (quote is not null)
            {
                if (c == '\\') index++;
                else if (c == quote) quote = null;
                continue;
            }
            if (c is '"' or '\'' or '`') { quote = c; continue; }
            if (c == '{') { if (braces++ == 1) own.Append("{}"); continue; }
            if (c == '}') { if (--braces == 0) return null; continue; }
            if (c == '(') parentheses++;
            else if (c == ')') parentheses--;
            if (braces == 1) own.Append(c);
        }
        var bound = Regex.Escape(name) + @"(?![\w$])";
        if (braces != 1 || parentheses != 0
            || Regex.IsMatch(WithoutEndedLoops(own.ToString(), name),
                @"(?<![\w$.])(?:(?:let|const|var)\s+" + bound + "|" + Regex.Escape(name) + @"\s?=(?![=>]))", RegexOptions.CultureInvariant)
            || Regex.IsMatch(body, @"(?<![\w$.])var\s+" + bound, RegexOptions.CultureInvariant)) return null;
        return string.Join(",", arguments);
    }

    // A loop of a method body's own statements binding one name of its own (for(let name of ...), for(const name in ...)).
    private static readonly Regex OwnLoop = new(@"(?<![\w$.])for\((?:let|const)\s+(?<name>[\w$]+)\s+(?:of|in)(?![\w$])",
        RegexOptions.CultureInvariant);

    /// <summary>A body's own statements (<see cref="Hook"/>) without the loops binding <paramref name="name"/> of their own
    /// that end before the end of the text (the call): within them the name is the loop's.</summary>
    private static string WithoutEndedLoops(string own, string name)
    {
        var kept = new StringBuilder();
        var index = 0;
        for (Match loop; (loop = OwnLoop.Match(own, index)).Success;)
        {
            var end = loop.Groups["name"].Value == name ? LoopEnd(own, loop.Index + "for".Length) : -1;
            kept.Append(own, index, (end < 0 ? loop.Index + loop.Length : loop.Index) - index);
            index = end < 0 ? loop.Index + loop.Length : end;
        }
        return kept.Append(own, index, own.Length - index).ToString();
    }

    /// <summary>Just past the loop whose head opens at <paramref name="open"/> in a body's own statements (blocks shown as
    /// {}): its body a block, a loop of the same kind or one expression ended by <c>;</c>. -1 when it may not end there.</summary>
    private static int LoopEnd(string own, int open)
    {
        var depth = 0;
        var close = -1;
        for (var index = open; index < own.Length && close < 0; index++)
        {
            if (own[index] == '(') depth++;
            else if (own[index] == ')' && --depth == 0) close = index;
        }
        if (close < 0) return -1;
        var at = close + 1;
        if (own.AsSpan(at).StartsWith("{}")) return at + 2;
        if (own.AsSpan(at).StartsWith("for(")) return LoopEnd(own, at + "for".Length);
        if (Regex.IsMatch(own[at..], @"^(?:if|else|do|while|switch|try|with|function|class|let|const|var|return|throw)(?![\w$])",
                RegexOptions.CultureInvariant)) return -1;
        depth = 0;
        for (var index = at; index < own.Length; index++)
        {
            var c = own[index];
            if (c is '(' or '[') depth++;
            else if (c is ')' or ']') depth--;
            else if (c == ';' && depth == 0) return index + 1;
        }
        return -1;
    }

    /// <summary>The scheduled list's entry and file schemas as text, with every schema and constant they are made of in
    /// their file (each defined as <c>name=</c>, the last definition before the entry schema or else the first after),
    /// every name that is not a field or a member numbered by first appearance, so renamed minified names read the same
    /// and any other change does not; null when they cannot be found. <paramref name="file"/> is the file schema's name
    /// the load uses.</summary>
    internal static string? ScheduledSchema(ReadOnlySpan<byte> bundle, string file)
    {
        var anchor = "({scheduledTasks:"u8;
        for (int offset = 0, found; (found = bundle[offset..].IndexOf(anchor)) >= 0; offset += found + anchor.Length)
        {
            var at = offset + found;
            var start = Math.Max(0, at - 60);
            var head = Encoding.UTF8.GetString(bundle[start..Math.Min(bundle.Length, at + 200)]);
            Match? match = null;
            foreach (Match candidate in ScheduledFile.Matches(head))
                if (candidate.Groups["file"].Value == file && start + Encoding.UTF8.GetByteCount(head.AsSpan(0, candidate.Index))
                        + Encoding.UTF8.GetByteCount(file + "=" + candidate.Groups["k"].Value) == at) match = candidate;
            if (match is null) continue;
            if (FileAt(bundle, at) is not { } span) return null;
            var entry = match.Groups["entry"].Value;
            var declaration = Encoding.UTF8.GetBytes(entry + "=" + match.Groups["k"].Value + "({id:");
            var entryAt = bundle[span.Start..at].LastIndexOf(declaration);
            if (entryAt < 0) return null;
            var entryStart = span.Start + entryAt + declaration.Length - "({id:".Length;
            if (Balanced(bundle, entryStart) is not { } entryText || Balanced(bundle, at) is not { } fileText) return null;
            var names = new Dictionary<string, string>(StringComparer.Ordinal);
            var order = new List<string>();
            var parts = new List<string> { Renumber(entryText, names, order), Renumber(fileText, names, order) };
            var defined = new HashSet<string>(StringComparer.Ordinal) { entry, file };
            for (var index = 0; index < order.Count; index++)
            {
                var name = order[index];
                if (name.Length < 2 || !defined.Add(name)) continue;
                if (Definition(bundle, span.Start, span.End, entryStart, name) is { } definition)
                    parts.Add(names[name] + "=" + Renumber(definition, names, order));
            }
            return string.Join("\n", parts);
        }
        return null;
    }

    /// <summary>What <paramref name="name"/> is defined as in its file (<c>name=</c> not part of a longer name or a
    /// member, not a comparison or an arrow): the last definition before <paramref name="anchor"/>, else the first after;
    /// null when there is none or it does not end within 16 KB.</summary>
    private static string? Definition(ReadOnlySpan<byte> bundle, int fileStart, int fileEnd, int anchor, string name)
    {
        var needle = Encoding.UTF8.GetBytes(name + "=");
        var file = bundle[fileStart..fileEnd];
        int? before = null, after = null;
        for (int offset = 0, found; (found = file[offset..].IndexOf(needle)) >= 0; offset += found + needle.Length)
        {
            var at = offset + found;
            var end = at + needle.Length;
            if (at > 0 && file[at - 1] is var previous && (char.IsAsciiLetterOrDigit((char)previous) || previous is (byte)'_' or (byte)'$' or (byte)'.'))
                continue;
            if (end < file.Length && file[end] is (byte)'=' or (byte)'>') continue;
            if (fileStart + at < anchor) before = fileStart + at;
            else { after = fileStart + at; break; }
        }
        return (before ?? after) is { } place ? Expression(bundle, place + needle.Length) : null;
    }

    /// <summary>The expression starting at <paramref name="start"/>, up to the comma or semicolon that ends it, or the
    /// bracket that closes around it; strings skipped. Null when it does not end within 16 KB.</summary>
    private static string? Expression(ReadOnlySpan<byte> bundle, int start)
    {
        var text = Encoding.UTF8.GetString(bundle.Slice(start, Math.Min(16384, bundle.Length - start)));
        var depth = 0;
        char? quote = null;
        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            if (quote is not null)
            {
                if (c == '\\') { index++; continue; }
                if (c == quote) quote = null;
            }
            else if (c is '"' or '\'' or '`') quote = c;
            else if (c is '(' or '[' or '{') depth++;
            else if (c is ')' or ']' or '}') { if (--depth < 0) return text[..index]; }
            else if (c is ',' or ';' && depth == 0) return text[..index];
        }
        return null;
    }

    /// <summary>The parenthesized text opening at <paramref name="start"/>, strings skipped; null when it does not close
    /// within 16 KB.</summary>
    private static string? Balanced(ReadOnlySpan<byte> bundle, int start)
    {
        var text = Encoding.UTF8.GetString(bundle.Slice(start, Math.Min(16384, bundle.Length - start)));
        var depth = 0;
        char? quote = null;
        for (var index = 0; index < text.Length; index++)
        {
            var c = text[index];
            if (quote is not null)
            {
                if (c == '\\') { index++; continue; }
                if (c == quote) quote = null;
            }
            else if (c is '"' or '\'' or '`') quote = c;
            else if (c == '(') depth++;
            else if (c == ')' && --depth == 0) return text[..(index + 1)];
        }
        return null;
    }

    private static bool IsWord(char c) => char.IsAsciiLetterOrDigit(c) || c is '_' or '$';

    /// <summary>The text with every name that is not a field or a member numbered by first appearance (<paramref name="names"/>,
    /// shared across texts); names met for the first time are added to <paramref name="order"/>.</summary>
    private static string Renumber(string text, Dictionary<string, string> names, List<string>? order = null)
    {
        var result = new StringBuilder(text.Length);
        char? quote = null;
        for (var index = 0; index < text.Length;)
        {
            var c = text[index];
            if (quote is not null)
            {
                result.Append(c);
                if (c == '\\' && index + 1 < text.Length) { result.Append(text[index + 1]); index += 2; continue; }
                if (c == quote) quote = null;
                index++;
                continue;
            }
            if (c is '"' or '\'' or '`') { quote = c; result.Append(c); index++; continue; }
            if ((char.IsAsciiLetter(c) || c is '_' or '$') && (index == 0 || !IsWord(text[index - 1])))
            {
                var end = index + 1;
                while (end < text.Length && IsWord(text[end])) end++;
                var word = text[index..end];
                var member = index > 0 && text[index - 1] == '.' && (index < 3 || text[(index - 3)..index] != "...");
                if (member || end < text.Length && text[end] == ':') result.Append(word);
                else
                {
                    if (!names.TryGetValue(word, out var number))
                    {
                        names[word] = number = "$" + names.Count.ToString(CultureInfo.InvariantCulture);
                        order?.Add(word);
                    }
                    result.Append(number);
                }
                index = end;
                continue;
            }
            result.Append(c);
            index++;
        }
        return result.ToString();
    }

    private static readonly byte[] SourceMapTrailer = "//# sourceMappingURL="u8.ToArray();
    private const int HeaderBytes = 65536, ExportTailBytes = 8192;

    /// <summary>The function a shared name at <paramref name="at"/> stands for, so flows in different bundled files
    /// still have to name one function (since 5.11492 the transcript removal lives in a chunk of its own). Each bundled
    /// file ends with its source map comment, which names the file. An import (<c>Gt.f</c>) is the export of the file
    /// the caller's file requires under that name in its header; a function its own file exports at the end is that
    /// export; any other name is that file's local function (itself, in a bundle not split into files). An export stands
    /// for the function a flow read only while its file sets it exactly once and by assignment, and an import only while
    /// its file binds the name to one required file and never assigns to the member: set again later, the name would
    /// call something no flow read. Null when a name cannot be resolved to one function.</summary>
    private static string? Function(ReadOnlySpan<byte> bundle, string name, int at)
    {
        var previous = bundle[..at].LastIndexOf(SourceMapTrailer);
        var fileStart = previous < 0 ? 0 : previous + SourceMapTrailer.Length;
        var trailer = bundle[at..].IndexOf(SourceMapTrailer);
        var fileEnd = trailer < 0 ? bundle.Length : at + trailer;
        var dot = name.IndexOf('.');
        if (dot >= 0)
        {
            var header = Encoding.UTF8.GetString(bundle.Slice(fileStart, Math.Min(HeaderBytes, at - fileStart)));
            var required = Regex.Matches(header, @"(?<![\w$.])" + Regex.Escape(name[..dot])
                + @"=require\(""\./(?<file>[\w.-]+\.js)""\)", RegexOptions.CultureInvariant);
            if (required.Count != 1) return null;
            var target = required[0].Groups["file"].Value;
            var member = name[(dot + 1)..];
            if (Sets(bundle[fileStart..fileEnd], name, ownName: true) != 0 || FileNamed(bundle, target) is not { } span
                || !ExportedOnce(bundle[span], member)) return null;
            return target + "#" + member;
        }
        if (trailer < 0) return name;
        var after = bundle[(fileEnd + SourceMapTrailer.Length)..];
        var mapEnd = after.IndexOf(".map"u8);
        if (mapEnd < 0) return name;
        var file = Encoding.UTF8.GetString(after[..mapEnd]);
        var tailStart = Math.Max(at, fileEnd - ExportTailBytes);
        var exported = Regex.Match(Encoding.UTF8.GetString(bundle[tailStart..fileEnd]),
            @"(?<![\w$.])exports\.(?<export>[\w$]+)=" + Regex.Escape(name) + "(?![\\w$])", RegexOptions.CultureInvariant);
        if (!exported.Success) return file + ":" + name;
        return ExportedOnce(bundle[fileStart..fileEnd], exported.Groups["export"].Value)
            ? file + "#" + exported.Groups["export"].Value : null;
    }

    /// <summary>The bundled file of this name, without its source map comment: null unless exactly one ends with it.</summary>
    private static Range? FileNamed(ReadOnlySpan<byte> bundle, string file)
    {
        var ending = Encoding.UTF8.GetBytes("//# sourceMappingURL=" + file + ".map");
        var end = bundle.IndexOf(ending);
        if (end < 0 || bundle[(end + ending.Length)..].IndexOf(ending) >= 0) return null;
        var previous = bundle[..end].LastIndexOf(SourceMapTrailer);
        return (previous < 0 ? 0 : previous + SourceMapTrailer.Length)..end;
    }

    /// <summary>Whether the file sets this export exactly once, by a plain assignment: not a second time, not as a
    /// defined property, and not by replacing its exports whole.</summary>
    private static bool ExportedOnce(ReadOnlySpan<byte> file, string export) =>
        Sets(file, "exports." + export, ownName: false) == 1
        && file.IndexOf(Encoding.UTF8.GetBytes("exports,\"" + export + "\"")) < 0
        && Sets(file, "module.exports", ownName: false) == 0;

    /// <summary>How often the file assigns to this name (<c>=</c>, not a comparison). With <paramref name="ownName"/>
    /// the name must stand on its own, not end a longer name or be another object's member.</summary>
    private static int Sets(ReadOnlySpan<byte> file, string name, bool ownName)
    {
        var count = 0;
        foreach (var spelling in new[] { name + "=", name + " =" })
        {
            var needle = Encoding.UTF8.GetBytes(spelling);
            for (int offset = 0, found; (found = file[offset..].IndexOf(needle)) >= 0; offset += found + needle.Length)
            {
                int start = offset + found, end = start + needle.Length;
                if (end < file.Length && file[end] == (byte)'=') continue;
                if (ownName && start > 0 && file[start - 1] is var before
                    && (char.IsAsciiLetterOrDigit((char)before) || before is (byte)'_' or (byte)'$' or (byte)'.')) continue;
                count++;
            }
        }
        return count;
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
    // The scheduled-task manager's lines (ClaudeScheduledLog), whose format ScheduledFlows reviews.
    private static readonly Regex ScheduledReset = new(
        @"^(?<at>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) \[info\] \[CCDScheduledTasks\] Reset\s*$", RegexOptions.CultureInvariant);
    private static readonly Regex ScheduledInitialized = new(
        @"^(?<at>\d{4}-\d\d-\d\d \d\d:\d\d:\d\d) \[info\] \[CCDScheduledTasks\] Initialized \{ accountId: '(?<account>[0-9a-fA-F-]{36})', orgId: '(?<org>[0-9a-fA-F-]{36})' \}\s*$",
        RegexOptions.CultureInvariant);
    private readonly string _directory = directory ?? ClaudeDesktopProbe.LogDirectory;
    private readonly ClaudeScheduledLog _scheduled = new();
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
    /// <summary>What the lines read say of the app's scheduled-task manager: known while every line of this run was read.</summary>
    internal ClaudeScheduledLogView Scheduled => _scheduled.View(_generation is not null);

    internal ClaudeDesktopAccountState Update(DateTime? hostStartedAt, IReadOnlyCollection<ClaudeShelfAccount> registered)
    {
        LostLines = false;
        if (hostStartedAt is null)
        {
            _generation = null; _held.Clear(); _scheduled.Close();
            return _state = ClaudeDesktopAccountState.Closed;
        }
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
            _scheduled.Start(new DateTimeOffset(hostStartedAt.Value));
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
        return _state = Apply(_state, lines, hostStartedAt.Value, registered, _held, _scheduled);
    }

    /// <param name="held">Collects the accounts the lines leave, until one confirms an account (see <see cref="Held"/>).</param>
    /// <param name="scheduled">Collects what the lines say of the scheduled-task manager (see <see cref="Scheduled"/>).</param>
    internal static ClaudeDesktopAccountState Apply(ClaudeDesktopAccountState state, IEnumerable<string> lines,
        DateTime since, IReadOnlyCollection<ClaudeShelfAccount> registered, ISet<string>? held = null,
        ClaudeScheduledLog? scheduled = null)
    {
        var accounts = registered.Select(account => account.AccountUuid.ToLowerInvariant()).ToHashSet(StringComparer.Ordinal);
        var pairs = registered.Select(account => account.Key).ToHashSet(StringComparer.Ordinal);
        foreach (var line in lines)
        {
            if (line.Length < 20 || !DateTime.TryParseExact(line[..19], "yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture,
                    DateTimeStyles.AssumeLocal, out var at) || at < since - ClockSkew) continue;
            if (line.Contains("[CCDScheduledTasks]", StringComparison.Ordinal))
            {
                // Whichever account it is, registered or not: the list loaded is the one not to write.
                if (ScheduledReset.IsMatch(line)) scheduled?.Reset(new DateTimeOffset(at));
                else if (ScheduledInitialized.Match(line) is { Success: true } began)
                    scheduled?.Initialized(began.Groups["account"].Value.ToLowerInvariant() + "/"
                        + began.Groups["org"].Value.ToLowerInvariant(), new DateTimeOffset(at));
                continue;
            }
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
        string? homeDirectory = null, bool keepCache = true)
    {
        var cachePath = Path.Combine(ledgerDirectory, "accounts.json");
        var cache = ReadCache(cachePath);
        var accounts = new List<ClaudeShelfAccount>();
        var current = new Dictionary<string, CacheEntry>(StringComparer.Ordinal);
        // When a profile of its own and the default home both map to one account, the profile of its own is the
        // account's target: the default home follows whatever the terminal signs in to.
        foreach (var profile in profiles.Where(profile => profile.IsVerified).OrderBy(profile => profile.IsDefault))
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
        if (keepCache && !current.Values.OrderBy(entry => entry.ProfileId).SequenceEqual(cache.Values.OrderBy(entry => entry.ProfileId)))
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
