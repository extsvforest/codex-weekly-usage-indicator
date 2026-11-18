using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WeeklyUsageIndicator;

// Korean source templates are translation keys. Only explicit UI/message calls
// are translated: never walk controls or rewrite account aliases and input text.
internal static class UiText
{
    private static readonly Dictionary<string, string> English = ReadCatalog("en");
    private static readonly Dictionary<string, string> Korean = ReadCatalog("ko");
    private static readonly Regex Placeholder = new(@"\{(\d+)(?:,[^}:]+)?(?::[^}]+)?\}", RegexOptions.CultureInvariant);
    private static readonly Lazy<TranslationPattern[]> Patterns = new(BuildPatterns);
    private static string _language = "ko";
    internal static string Language => _language;
    internal static bool IsEnglish => _language == "en";
    internal static CultureInfo Culture => CultureInfo.GetCultureInfo(IsEnglish ? "en-US" : "ko-KR");
    internal static string SystemLanguage => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ko" ? "ko" : "en";
    internal static bool IsSupported(string? language) => language is "ko" or "en";
    internal static void SetLanguage(string language)
    {
        if (!IsSupported(language)) throw new ArgumentException("Unsupported UI language.", nameof(language));
        _language = language;
    }
    internal static string T(string template, params object?[] args)
    {
        var map = IsEnglish ? English : Korean;
        var translated = map.GetValueOrDefault(template, template);
        return args.Length == 0 ? translated : string.Format(Culture, translated, args);
    }
    internal static string F(FormattableString text) => T(text.Format, text.GetArguments());

    // Cached failure messages remain canonical in the account/query layer.
    // Recognize complete known application templates, preserving captured data.
    internal static string Message(string message)
    {
        var exact = T(message);
        if (exact != message) return exact;
        foreach (var pattern in Patterns.Value)
        {
            if (pattern.Language != Language || pattern.Match(message) is not { } values) continue;
            return string.Format(Culture, pattern.Target, values);
        }
        // Cleanup summaries carry one known category and an unchanged error code.
        var separator = message.IndexOf(" · 0x", StringComparison.Ordinal);
        if (separator > 0) return Message(message[..separator]) + message[separator..];
        return message;
    }
    internal static IReadOnlyDictionary<string, string> Catalog(string language) => language == "en" ? English : Korean;
    internal static IReadOnlyList<TranslationPattern> Templates => Patterns.Value;
    private static Dictionary<string, string> ReadCatalog(string language)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var assembly = typeof(UiText).Assembly;
        foreach (var name in assembly.GetManifestResourceNames().Where(n => n.EndsWith("." + language + ".json", StringComparison.Ordinal)).Order())
        {
            using var stream = assembly.GetManifestResourceStream(name)!;
            foreach (var entry in JsonSerializer.Deserialize<Dictionary<string, string>>(stream)!)
            {
                if (result.TryGetValue(entry.Key, out var previous) && previous != entry.Value)
                    throw new InvalidOperationException("Conflicting UI translation: " + entry.Key);
                result[entry.Key] = entry.Value;
            }
        }
        return result;
    }
    private static TranslationPattern[] BuildPatterns()
    {
        var result = new List<TranslationPattern>();
        Add(English, "en"); Add(Korean, "ko");
        // Static translated errors may have been cached before the language changed.
        foreach (var pair in English) if (!Korean.ContainsKey(pair.Value)) AddOne(pair.Value, pair.Key, "ko");
        foreach (var pair in Korean) if (!English.ContainsKey(pair.Value)) AddOne(pair.Value, pair.Key, "en");
        return result.ToArray();
        void Add(Dictionary<string, string> map, string language)
        { foreach (var pair in map) AddOne(pair.Key, pair.Value, language); }
        void AddOne(string source, string target, string language)
        {
            var literals = new List<string>(); var indices = new List<int>(); var start = 0; var count = 0;
            foreach (Match match in Placeholder.Matches(source))
            {
                literals.Add(source[start..match.Index]);
                var index = int.Parse(match.Groups[1].Value, CultureInfo.InvariantCulture); count = Math.Max(count, index + 1);
                indices.Add(index); start = match.Index + match.Length;
            }
            literals.Add(source[start..]);
            result.Add(new(language, literals.ToArray(), indices.ToArray(), target, count));
        }
    }

    /// <summary>
    /// A known template as its literal parts in order, a value between each two (<see cref="Indices"/> says which). It is
    /// matched by plain ordinal search, not a regular expression: a thousand compiled patterns held the widget near a
    /// gigabyte once the first unknown message came. The matches are those of the pattern
    /// <c>\A part (?&lt;p0&gt;.*?) part … part \z</c> (single-line): the whole message, each value as short as the parts
    /// after it allow, which is each next part at its earliest place. A value repeated keeps its last place; one the
    /// template lacks is empty.
    /// </summary>
    internal sealed record TranslationPattern(string Language, string[] Literals, int[] Indices, string Target, int ArgumentCount)
    {
        internal object[]? Match(string message)
        {
            if (Indices.Length == 0) return message == Literals[0] ? [] : null;
            string first = Literals[0], last = Literals[^1];
            if (message.Length < first.Length + last.Length || !message.StartsWith(first, StringComparison.Ordinal)
                || !message.EndsWith(last, StringComparison.Ordinal)) return null;
            var values = new object[ArgumentCount];
            Array.Fill(values, "");
            int position = first.Length, end = message.Length - last.Length;
            for (var i = 0; i < Indices.Length; i++)
            {
                var next = end;
                if (i + 1 < Indices.Length)
                {
                    next = message.IndexOf(Literals[i + 1], position, end - position, StringComparison.Ordinal);
                    if (next < 0) return null;
                }
                values[Indices[i]] = message[position..next];
                position = next + Literals[i + 1].Length;
            }
            return values;
        }
    }
}
