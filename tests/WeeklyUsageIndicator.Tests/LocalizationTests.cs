using System.Drawing;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using WeeklyUsageIndicator;

internal static class LocalizationTests
{
    private static readonly Regex Placeholder = new(
        @"(?<!\{)\{(?<index>\d+)(?:,[^}:]+)?(?::[^}]+)?\}(?!\})",
        RegexOptions.CultureInvariant);

    internal static Task RunAsync()
    {
        var originalCulture = CultureInfo.CurrentCulture;
        var originalUiCulture = CultureInfo.CurrentUICulture;
        try
        {
            CheckCatalogs();
            CheckSourceLiteralCoverage();
            CheckMessages();
            CheckSnapshotAndTooltip();
            CheckSettings();
            return Task.CompletedTask;
        }
        finally
        {
            CultureInfo.CurrentCulture = originalCulture;
            CultureInfo.CurrentUICulture = originalUiCulture;
            // Existing UI and usage tests intentionally exercise the original Korean UI.
            UiText.SetLanguage("ko");
        }
    }

    private static void CheckCatalogs()
    {
        Check(UiText.Catalog("en").Count > 200,
            "English resources must be embedded in the main assembly, not lost in a culture satellite");
        Check(UiText.Catalog("ko").Count >= 29, "Korean provider-error translations must be embedded");
        foreach (var language in new[] { "ko", "en" })
        {
            UiText.SetLanguage(language);
            foreach (var (source, target) in UiText.Catalog(language))
            {
                var sourceFormat = CompositeFormat.Parse(source);
                var targetFormat = CompositeFormat.Parse(target);
                var sourceIndices = Indices(source);
                var targetIndices = Indices(target);
                Check(sourceIndices.SequenceEqual(targetIndices), $"placeholder indices differ in {language}: {source}");
                Check(sourceFormat.MinimumArgumentCount == targetFormat.MinimumArgumentCount,
                    $"argument requirements differ in {language}: {source}");
                var arguments = Enumerable.Range(0, sourceFormat.MinimumArgumentCount)
                    .Select(index => (object)new CatalogArgument(index)).ToArray();
                // IFormattable fixtures support dates, counts, and any future format specifier
                // without guessing types from the translation's wording.
                var original = string.Format(UiText.Culture, source, arguments);
                var translated = string.Format(UiText.Culture, target, arguments);
                Check(UiText.T(source, arguments) == translated, $"catalog entry did not render: {source}");
                foreach (var index in sourceIndices)
                {
                    var token = CatalogArgument.Token(index);
                    Check(original.Contains(token, StringComparison.Ordinal) && translated.Contains(token, StringComparison.Ordinal),
                        $"formatted argument {index} was lost in {language}: {source}");
                }
            }
        }
    }

    private static int[] Indices(string template) => Placeholder.Matches(template).Cast<Match>()
        .Select(match => int.Parse(match.Groups["index"].Value, CultureInfo.InvariantCulture))
        .Distinct().Order().ToArray();

    private static void CheckSourceLiteralCoverage()
    {
        var root = FindRepositoryRoot(Environment.CurrentDirectory) ?? FindRepositoryRoot(AppContext.BaseDirectory);
        if (root is null) return; // Packaged test binaries may be run without their source checkout.
        var calls = new Regex("UiText\\.T\\(\\s*\"(?<text>(?:\\\\.|[^\"\\\\])*)\"", RegexOptions.CultureInvariant);
        var english = UiText.Catalog("en");
        foreach (var path in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.TopDirectoryOnly))
        {
            foreach (Match match in calls.Matches(File.ReadAllText(path, Encoding.UTF8)))
            {
                var literal = Regex.Unescape(match.Groups["text"].Value);
                if (!Regex.IsMatch(literal, "[가-힣]", RegexOptions.CultureInvariant)) continue;
                Check(english.ContainsKey(literal), $"Korean UiText.T literal lacks an English entry in {Path.GetFileName(path)}: {literal}");
            }
        }
    }

    private static string? FindRepositoryRoot(string start)
    {
        for (var directory = new DirectoryInfo(start); directory is not null; directory = directory.Parent)
            if (File.Exists(Path.Combine(directory.FullName, "src", "UiText.cs")) &&
                File.Exists(Path.Combine(directory.FullName, "src", "WeeklyUsageIndicator.csproj"))) return directory.FullName;
        return null;
    }

    private static void CheckMessages()
    {
        const string canonicalKorean = "다른 계정 관리 작업이 진행 중입니다. 잠시 후 다시 시도하세요.";
        const string translatedEnglish = "Another account operation is in progress. Try again shortly.";
        UiText.SetLanguage("en");
        Check(UiText.Message(canonicalKorean) == translatedEnglish, "canonical Korean error translates to English");
        UiText.SetLanguage("ko");
        Check(UiText.Message(canonicalKorean) == canonicalKorean && UiText.Message(translatedEnglish) == canonicalKorean,
            "canonical and cached English errors return to Korean");

        const string canonicalEnglish = "Codex helper did not exit. Account switching is blocked.";
        const string translatedKorean = "Codex 보조 프로세스가 종료되지 않아 계정 전환을 차단했습니다.";
        Check(UiText.Message(canonicalEnglish) == translatedKorean, "provider error translates without changing its stop instruction");
        UiText.SetLanguage("en");
        Check(UiText.Message(canonicalEnglish) == canonicalEnglish && UiText.Message(translatedKorean) == canonicalEnglish,
            "canonical and cached Korean provider errors return to English");

        const string alias = "갱신 필요 · A1B2C3D4 {token} ‘x’";
        var canonicalAccount = $"‘{alias}’ 사용량을 확인했습니다.";
        var englishAccount = $"Usage checked for ‘{alias}’.";
        Check(UiText.F($"‘{alias}’ 사용량을 확인했습니다.") == englishAccount,
            "template formatting preserves user aliases, braces, quotes, and IDs");
        Check(UiText.Message(canonicalAccount) == englishAccount,
            "cached dynamic message preserves all captured account data");
        UiText.SetLanguage("ko");
        Check(UiText.Message(englishAccount) == canonicalAccount, "dynamic account message survives language round trip");

        const string exitEnglish = "Claude Code /usage exited with code -1073741510.";
        const string exitKorean = "Claude Code /usage가 종료 코드 -1073741510로 종료되었습니다.";
        Check(UiText.Message(exitEnglish) == exitKorean, "dynamic provider error preserves the signed exit code");
        UiText.SetLanguage("en");
        Check(UiText.Message(exitKorean) == exitEnglish, "dynamic provider error returns to canonical English");

        const string hint = "계정 · A1B2C3D4";
        Check(UiText.Message(hint) == "Account · A1B2C3D4", "known fallback hint preserves its identity suffix");
        UiText.SetLanguage("ko");
        Check(UiText.Message("Account · A1B2C3D4") == hint, "fallback hint returns to Korean");

        const string cleanupKorean = "파일 사용 중 · 0x80070020";
        const string cleanupEnglish = "File in use · 0x80070020";
        UiText.SetLanguage("en");
        Check(UiText.Message(cleanupKorean) == cleanupEnglish, "cleanup category translates without changing the hex code");
        UiText.SetLanguage("ko");
        Check(UiText.Message(cleanupEnglish) == cleanupKorean, "cached cleanup category returns to Korean with its code intact");

        const string unmatched = "UNKNOWN_DIAGNOSTIC {opaque} [A1B2C3D4] 0xDEADBEEF 한글";
        foreach (var language in new[] { "ko", "en" })
        {
            UiText.SetLanguage(language);
            Check(UiText.Message(unmatched) == unmatched, "unknown diagnostics are not rewritten by partial template matches");
            Check(UiText.Message("prefix " + canonicalKorean + " suffix") == "prefix " + canonicalKorean + " suffix",
                "known error matching is anchored to the whole message");
        }
    }

    private static void CheckSnapshotAndTooltip()
    {
        var now = DateTimeOffset.UtcNow;
        // One alias is deliberately also a translation key. Aliases are data, not UI copy.
        var first = new SavedCodexAccount("A1B2C3D4", "갱신 필요", "synthetic-private-hint", true,
            new UsageSnapshot(37, now.AddDays(2), 10080, "codex", new UsageWindow(9, now.AddHours(2), 300)), now);
        var second = first with { Id = "E5F60718", Label = "My ‘계정’ {0} · A1B2C3D4", IsActive = false,
            Usage = first.Usage! with { UsedPercent = 19, ResetsAt = now.AddDays(4) } };
        var set = new CombinedUsageSnapshot();
        set.Initialize(new[] { first, second }, batch: true);
        set.SetResult(first.Id, first, CombinedReadState.Success);
        set.SetResult(second.Id, second, CombinedReadState.Success);
        set.CompletedAt = now;
        var claude = new ClaudeUsageResult(new ClaudeUsageSnapshot(new(15, now.AddHours(1)),
            new(25, now.AddDays(3)), new(45, now.AddDays(3))), now, null, IsStale: false);
        UiText.SetLanguage("en");
        var english = UsageIndicatorForm.BuildTooltipText(first.Usage, claude, null, null, true, set, first.Label);
        Check(english.Contains("CODEX · " + first.Label, StringComparison.Ordinal) &&
            english.Contains(first.Label + " [Active]", StringComparison.Ordinal) &&
            english.Contains(second.Label, StringComparison.Ordinal), "tooltip keeps translated-key aliases and mixed-script account names verbatim");
        Check(english.Contains("All accounts · Weekly remaining 144% / 200%", StringComparison.Ordinal) &&
            english.Contains("Weekly: 63% remaining", StringComparison.Ordinal) &&
            english.Contains("5-hour: 91% remaining", StringComparison.Ordinal) &&
            english.Contains("Fable: 55% remaining", StringComparison.Ordinal) &&
            english.Contains("Right-click", StringComparison.Ordinal), "English tooltip labels, totals, provider windows, and instructions are translated");
        Check(!english.Contains(first.IdentityHint, StringComparison.Ordinal), "account identity hints never enter tooltip text");
        Check(set.Accounts.All(row => row.Status(now) == "Checked"), "account snapshot status uses the selected display language");

        const string canonicalError = "미완료 계정 교체를 먼저 복구하세요.";
        set.SetResult(second.Id, second, CombinedReadState.Failed, canonicalError);
        Check(set.Accounts[1].Status(now) == "Recover the unfinished account switch first.",
            "cached account error translates at display time");
        var partial = UsageIndicatorForm.BuildTooltipText(first.Usage, null, canonicalError, null, false, set, first.Label);
        Check(partial.Contains("All accounts · Refresh total", StringComparison.Ordinal) &&
            partial.Contains(second.Label + "  Previous 81% · Check failed", StringComparison.Ordinal) &&
            partial.Contains("Check required: Recover the unfinished account switch first.", StringComparison.Ordinal),
            "partial results translate while retaining the previous value and recovery instruction");
        UiText.SetLanguage("ko");
        Check(set.Accounts[1].Status(now) == canonicalError && set.Accounts[1].Error == canonicalError,
            "language changes preserve the canonical account error in memory");
        var korean = UsageIndicatorForm.BuildTooltipText(first.Usage, null, null, null, false, set, first.Label);
        Check(korean.Contains(first.Label + " [사용 중]", StringComparison.Ordinal) &&
            korean.Contains(second.Label + "  이전 81% · 조회 실패", StringComparison.Ordinal),
            "Korean tooltip returns without changing account labels or usage");
        Check(set.Accounts[0].Account == first && set.Accounts[1].Account == second && set.ConfirmedRemaining(now) == 63,
            "localization does not mutate snapshots, identifiers, or confirmed usage");
    }

    private static void CheckSettings()
    {
        var parent = Path.GetFullPath(Environment.GetEnvironmentVariable("GFS_ACCOUNT_TEST_ROOT")
            ?? Path.Combine(Path.GetTempPath(), "gfs-agent", "indicator-tests"));
        var root = Path.GetFullPath(Path.Combine(parent, "localization-" + Guid.NewGuid().ToString("N")));
        Check(string.Equals(Path.GetDirectoryName(root), parent.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase), "settings fixture must be an immediate child of the assigned test root");
        Directory.CreateDirectory(root);
        try
        {
            var legacy = Path.Combine(root, "legacy.json");
            File.WriteAllText(legacy, "{\"X\":-240,\"Y\":315,\"ShowClaude\":false}");
            CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo("en-US");
            Check(IndicatorSettingsStore.LoadLanguage(legacy) == "ko", "legacy installs retain Korean even on English Windows");
            IndicatorSettingsStore.SaveLanguage("en", legacy);
            Check(IndicatorSettingsStore.LoadLanguage(legacy) == "en" &&
                IndicatorSettingsStore.LoadPosition(legacy) == new Point(-240, 315) && !IndicatorSettingsStore.LoadShowClaude(legacy),
                "saving language preserves off-screen-capable coordinates and Claude visibility");
            IndicatorSettingsStore.SavePosition(new Point(620, -40), legacy);
            Check(IndicatorSettingsStore.LoadPosition(legacy) == new Point(620, -40) &&
                IndicatorSettingsStore.LoadLanguage(legacy) == "en" && !IndicatorSettingsStore.LoadShowClaude(legacy),
                "saving position preserves selected language and visibility");
            IndicatorSettingsStore.SaveShowClaude(true, legacy);
            Check(IndicatorSettingsStore.LoadShowClaude(legacy) && IndicatorSettingsStore.LoadLanguage(legacy) == "en" &&
                IndicatorSettingsStore.LoadPosition(legacy) == new Point(620, -40), "saving visibility preserves selected language and position");
            IndicatorSettingsStore.SaveLanguage("ko", legacy);
            Check(IndicatorSettingsStore.LoadLanguage(legacy) == "ko" && IndicatorSettingsStore.LoadShowClaude(legacy) &&
                IndicatorSettingsStore.LoadPosition(legacy) == new Point(620, -40), "switching language back preserves the other settings");
            using (var saved = JsonDocument.Parse(File.ReadAllText(legacy)))
                Check(saved.RootElement.GetProperty("Language").GetString() == "ko", "selected language is persisted in the fixture file");

            foreach (var (culture, expected) in new[] { ("ko-KR", "ko"), ("en-US", "en"), ("fr-FR", "en") })
            {
                CultureInfo.CurrentUICulture = CultureInfo.GetCultureInfo(culture);
                var fresh = Path.Combine(root, "fresh-" + culture + ".json");
                Check(IndicatorSettingsStore.LoadLanguage(fresh) == expected &&
                    IndicatorSettingsStore.LoadPosition(fresh) is null && IndicatorSettingsStore.LoadShowClaude(fresh),
                    $"fresh {culture} settings use the supported system language and standard defaults");
                Check(!File.Exists(fresh), "loading default preferences does not create a settings file");
                IndicatorSettingsStore.SavePosition(new Point(31, 47), fresh);
                Check(IndicatorSettingsStore.LoadLanguage(fresh) == expected,
                    "the first position save must not turn a fresh English install into a legacy Korean install");
                var freshVisibility = Path.Combine(root, "fresh-visibility-" + culture + ".json");
                IndicatorSettingsStore.SaveShowClaude(false, freshVisibility);
                Check(IndicatorSettingsStore.LoadLanguage(freshVisibility) == expected &&
                    IndicatorSettingsStore.LoadPosition(freshVisibility) is null && !IndicatorSettingsStore.LoadShowClaude(freshVisibility),
                    "the first visibility save preserves the fresh install's system-language default");
            }

            var before = File.ReadAllText(legacy);
            try
            {
                IndicatorSettingsStore.SaveLanguage("fr", legacy);
                throw new InvalidOperationException("Localization: unsupported language should be rejected");
            }
            catch (ArgumentException) { }
            Check(File.ReadAllText(legacy) == before, "unsupported language does not overwrite preferences");
        }
        finally
        {
            // This fixture owns only flat JSON files in a fresh, checked child directory.
            // Avoid a recursive delete that could follow an unexpected directory boundary.
            foreach (var path in Directory.GetFiles(root)) File.Delete(path);
            Directory.Delete(root, recursive: false);
        }
    }

    private sealed class CatalogArgument(int index) : IFormattable
    {
        internal static string Token(int index) => "[[argument-" + index + "]]";
        public string ToString(string? format, IFormatProvider? formatProvider) => Token(index);
        public override string ToString() => Token(index);
    }

    private static void Check(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException("Localization: " + message);
    }
}
