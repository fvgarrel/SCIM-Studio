using CommunityToolkit.Mvvm.ComponentModel;
using ScimStudio.App.Localization;
using ScimStudio.App.Services;
using ScimStudio.App.Settings;
using ScimStudio.Core.Scim;

namespace ScimStudio.App.ViewModels;

/// <summary>A choice in a segmented control: its value and its label. Not generic, so a XAML template can name the type.</summary>
/// <param name="value">The value.</param>
/// <param name="key">The catalogue key of its label.</param>
public sealed class ChoiceViewModel(object value, string key) : ViewModelBase {
    public object Value { get; } = value;

    public string Label => L.Get(key);
}

/// <summary>The settings: language and theme, which apply at once, and where the settings are kept.</summary>
public sealed partial class SettingsViewModel : ViewModelBase {
    private readonly AppServices _services;

    public SettingsViewModel(AppServices services) {
        ArgumentNullException.ThrowIfNull(services);

        _services = services;
        Languages = [new(Localizer.ENGLISH, "language.en"), new(Localizer.GERMAN, "language.de")];
        Themes = [new(ThemeChoice.System, "theme.system"), new(ThemeChoice.Light, "theme.light"), new(ThemeChoice.Dark, "theme.dark")];
        Language = Languages.First(l => Equals(l.Value, Localizer.Instance.Language));
        Theme = Themes.First(t => Equals(t.Value, services.Settings.Theme));
    }

    public IReadOnlyList<ChoiceViewModel> Languages { get; }

    public IReadOnlyList<ChoiceViewModel> Themes { get; }

    [ObservableProperty]
    public partial ChoiceViewModel? Language { get; set; }

    [ObservableProperty]
    public partial ChoiceViewModel? Theme { get; set; }

    public string SettingsPath => _services.Store.Path;

    public string TokenStorage => L.Get(TokenVault.Encrypts ? "settings.tokenEncrypted" : "settings.tokenPlain");

    public string RememberLabel => L.Get(TokenVault.Encrypts ? "connect.rememberEncrypted" : "connect.rememberPlain");

    public string Version => ScimClient.Version;

    partial void OnLanguageChanged(ChoiceViewModel? value) {
        if (value?.Value is not string language) {
            return;
        }

        if (_services.Settings.Language == language && Localizer.Instance.Language == language) {
            return;
        }

        _services.Settings.Language = language;
        Localizer.Instance.Language = language;
        _services.Save();
    }

    partial void OnThemeChanged(ChoiceViewModel? value) {
        if (value?.Value is not ThemeChoice theme) {
            return;
        }

        AppServices.ApplyTheme(theme);
        if (_services.Settings.Theme != theme) {
            _services.Settings.Theme = theme;
            _services.Save();
        }
    }
}
