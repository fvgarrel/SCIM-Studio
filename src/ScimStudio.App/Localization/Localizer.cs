using System.ComponentModel;
using System.Globalization;
using System.Text.Json;
using ScimStudio.Core.Text;

namespace ScimStudio.App.Localization;

/// <summary>Something that shows text from the catalogues and has to redraw it when the language changes.</summary>
public interface ILocalizable {
    /// <summary>Called on the UI thread after the language changed.</summary>
    void Relocalize();
}

/// <summary>
/// The interface's languages and the text of every key in the one showing. English is the source: its catalogue defines the keys and every
/// other language mirrors it, which a test holds. A key missing from a translation falls back to English, and one missing from English shows
/// as itself, so a gap is visible rather than blank.
/// </summary>
public sealed class Localizer {
    public const string ENGLISH = "en";
    public const string GERMAN = "de";

    private readonly Dictionary<string, IReadOnlyDictionary<string, string>> _catalogs = new(StringComparer.Ordinal);
    private readonly Dictionary<string, LocalizedText> _entries = new(StringComparer.Ordinal);
    private readonly List<WeakReference<ILocalizable>> _listeners = [];
    private string _language = ENGLISH;

    private Localizer() {
        foreach (var language in Languages) {
            _catalogs[language] = Load(language);
        }
    }

    // Before Instance: static initialisers run in the order they are written, and the constructor reads the languages.
    public static IReadOnlyList<string> Languages { get; } = [ENGLISH, GERMAN];

    public static Localizer Instance { get; } = new();

    public string Language {
        get => _language;
        set {
            var language = Languages.Contains(value) ? value : ENGLISH;
            if (language == _language) {
                return;
            }

            _language = language;
            CultureInfo.CurrentUICulture = Culture;
            CultureInfo.CurrentCulture = Culture;

            foreach (var entry in _entries.Values) {
                entry.Refresh();
            }

            foreach (var listener in Listeners()) {
                listener.Relocalize();
            }
        }
    }

    /// <summary>The culture numbers and dates are written in, matching the language.</summary>
    public CultureInfo Culture => CultureInfo.GetCultureInfo(_language == GERMAN ? "de-DE" : "en-US");

    /// <summary>The language a first start shows: the system's, if it is one of ours.</summary>
    public static string SystemLanguage => CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == GERMAN ? GERMAN : ENGLISH;

    public string Get(string key) {
        ArgumentNullException.ThrowIfNull(key);

        if (_catalogs[_language].TryGetValue(key, out var text) || _catalogs[ENGLISH].TryGetValue(key, out text)) {
            return text;
        }

        return key;
    }

    /// <summary>The text of a key with its placeholders filled; a value that is itself a message is translated first.</summary>
    /// <param name="key">The key.</param>
    /// <param name="args">The values.</param>
    public string Format(string key, params object?[] args) {
        var template = Get(key);
        if (args.Length == 0) {
            return template;
        }

        var values = args.Select(arg => arg is Message nested ? Format(nested) : arg).ToArray();
        try {
            return string.Format(Culture, template, values);
        } catch (FormatException) {
            return template;
        }
    }

    public string Format(Message message) {
        ArgumentNullException.ThrowIfNull(message);
        return Format(message.Key, [.. message.Args]);
    }

    /// <summary>The key as something a binding can watch: its value changes when the language does.</summary>
    /// <param name="key">The key.</param>
    public LocalizedText Entry(string key) {
        if (!_entries.TryGetValue(key, out var entry)) {
            entry = new LocalizedText(key);
            _entries[key] = entry;
        }

        return entry;
    }

    /// <summary>Registers something to redraw on a change of language. Held weakly, so a view model that is gone is not kept alive.</summary>
    /// <param name="listener">The listener.</param>
    public void Register(ILocalizable listener) {
        _listeners.RemoveAll(reference => !reference.TryGetTarget(out _));
        _listeners.Add(new WeakReference<ILocalizable>(listener));
    }

    /// <summary>A catalogue as it is shipped, for the test that holds every language to the English key set.</summary>
    /// <param name="language">The language.</param>
    internal static IReadOnlyDictionary<string, string> Load(string language) {
        using var stream = typeof(Localizer).Assembly.GetManifestResourceStream($"ScimStudio.App.Localization.{language}.json")
            ?? throw new InvalidOperationException($"No catalogue for {language}.");

        return JsonSerializer.Deserialize<Dictionary<string, string>>(stream) ?? [];
    }

    private List<ILocalizable> Listeners() {
        var alive = new List<ILocalizable>();
        foreach (var reference in _listeners) {
            if (reference.TryGetTarget(out var listener)) {
                alive.Add(listener);
            }
        }

        return alive;
    }
}

/// <summary>One key's text in the language showing, for a binding to watch.</summary>
public sealed class LocalizedText : INotifyPropertyChanged {
    private static readonly PropertyChangedEventArgs ValueChanged = new(nameof(Value));

    internal LocalizedText(string key) {
        Key = key;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; }

    public string Value => Localizer.Instance.Get(Key);

    internal void Refresh() {
        PropertyChanged?.Invoke(this, ValueChanged);
    }
}
