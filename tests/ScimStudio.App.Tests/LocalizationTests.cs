using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using ScimStudio.App.Localization;
using ScimStudio.Core.Checks;

namespace ScimStudio.App.Tests;

/// <summary>
/// English defines the keys and every other language mirrors them, placeholders included; and every key the code names exists. A key that
/// is missing shows as itself, so these are the only place a gap is caught before a person sees it.
/// </summary>
public sealed partial class LocalizationTests {
    private static readonly IReadOnlyDictionary<string, string> English = Localizer.Load(Localizer.ENGLISH);

    [Fact]
    public void Every_language_has_exactly_the_english_keys() {
        foreach (var language in Localizer.Languages) {
            var catalogue = Localizer.Load(language);

            Assert.Empty(English.Keys.Except(catalogue.Keys));
            Assert.Empty(catalogue.Keys.Except(English.Keys));
            Assert.All(catalogue, entry => Assert.False(string.IsNullOrWhiteSpace(entry.Value), $"{language}: {entry.Key} is empty"));
        }
    }

    [Fact]
    public void Every_translation_fills_the_placeholders_english_has() {
        foreach (var language in Localizer.Languages) {
            var catalogue = Localizer.Load(language);
            foreach (var (key, text) in English) {
                Assert.True(Placeholders(text).SetEquals(Placeholders(catalogue[key])), $"{language}: {key} has other placeholders");
            }
        }
    }

    [Fact]
    public void Every_check_category_and_status_has_its_text() {
        var keys = ConformanceSuite.Checks.Select(c => $"check.{c.Id}")
            .Concat(ConformanceSuite.Checks.Select(c => $"category.{c.Category}"))
            .Concat(Enum.GetNames<CheckStatus>().Select(s => $"status.{s.ToLowerInvariant()}"));

        Missing(keys);
    }

    [Fact]
    public void Every_key_the_code_names_exists() {
        var root = Root();
        var named = Directory.EnumerateFiles(Path.Combine(root, "src"), "*.*", SearchOption.AllDirectories)
            .Where(path => path.EndsWith(".cs", StringComparison.Ordinal) || path.EndsWith(".axaml", StringComparison.Ordinal))
            .Where(path => !path.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal))
            .SelectMany(path => KeyPattern().Matches(File.ReadAllText(path)).Select(m => m.Groups["key"].Value))
            .Distinct()
            .ToList();

        Assert.NotEmpty(named);
        Missing(named);
    }

    private static void Missing(IEnumerable<string> keys) {
        var missing = keys.Where(key => !English.ContainsKey(key)).Distinct().ToList();
        Assert.True(missing.Count == 0, $"Not in the catalogue: {string.Join(", ", missing)}");
    }

    [Fact]
    public void A_message_is_written_in_the_language_showing() {
        var localizer = Localizer.Instance;
        var language = localizer.Language;
        try {
            localizer.Language = Localizer.GERMAN;
            Assert.Equal("7 von 9", localizer.Format("list.count", 7, 9));

            localizer.Language = Localizer.ENGLISH;
            Assert.Equal("7 of 9", localizer.Format("list.count", 7, 9));
        } finally {
            localizer.Language = language;
        }
    }

    /// <summary>The keys named in code and XAML: a markup extension, a lookup or a message with a literal key.</summary>
    [GeneratedRegex("""
        \{l:Tr\ (?<key>[a-zA-Z0-9._]+)\}
        | (?:L|Localizer\.Instance)\.(?:Get|Format)\("(?<key>[a-zA-Z0-9._]+)"
        | Message\.Of\("(?<key>[a-zA-Z0-9._]+)"
        | (?:Fail|Skip)\("(?<key>[a-zA-Z0-9._]+)"
        """, RegexOptions.IgnorePatternWhitespace)]
    private static partial Regex KeyPattern();

    [GeneratedRegex(@"\{(\d+)[^}]*\}")]
    private static partial Regex PlaceholderPattern();

    private static HashSet<string> Placeholders(string text) {
        return [.. PlaceholderPattern().Matches(text).Select(m => m.Groups[1].Value)];
    }

    private static string Root([CallerFilePath] string source = "") {
        var directory = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ScimStudio.slnx"))) {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new InvalidOperationException("The repository root was not found.");
    }
}
