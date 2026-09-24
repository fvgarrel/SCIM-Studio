using Avalonia;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Styling;
using ScimStudio.App.Localization;
using ScimStudio.App.Settings;
using ScimStudio.Core.Http;

namespace ScimStudio.App.Services;

/// <summary>What every part of the interface shares: the settings, the log, the notices, the dialogs and the demo server.</summary>
public sealed class AppServices : IAsyncDisposable {
    /// <summary>Loads the settings and applies the language and theme they name.</summary>
    /// <param name="store">Where the settings live.</param>
    public AppServices(SettingsStore store) {
        ArgumentNullException.ThrowIfNull(store);

        Store = store;
        Settings = store.Load();
        Localizer.Instance.Language = Settings.Language;
    }

    public SettingsStore Store { get; }

    public AppSettings Settings { get; }

    public ExchangeLog Log { get; } = new();

    public Notifier Notifier { get; } = new();

    public Dialogs Dialogs { get; } = new();

    public DemoServerHost Demo { get; } = new();

    /// <summary>The window, for what needs a top level: the clipboard. Null until it exists, and in tests.</summary>
    public TopLevel? TopLevel { get; set; }

    public void Save() {
        try {
            Store.Save(Settings);
        } catch (IOException failure) {
            Notifier.Error(Localizer.Instance.Get("settings.saveFailed"), failure.Message);
        } catch (UnauthorizedAccessException failure) {
            Notifier.Error(Localizer.Instance.Get("settings.saveFailed"), failure.Message);
        }
    }

    public async Task CopyAsync(string text) {
        if (TopLevel?.Clipboard is { } clipboard) {
            await clipboard.SetTextAsync(text);
            Notifier.Info(Localizer.Instance.Get("common.copied"));
        }
    }

    /// <summary>Asks where to save a file: null when the person cancels, and where there is no window to ask from.</summary>
    /// <param name="options">What the dialog suggests and offers.</param>
    public async Task<SaveFilePickerResult?> PickSaveFileAsync(FilePickerSaveOptions options) {
        if (TopLevel?.StorageProvider is not { CanSave: true } storage) {
            return null;
        }

        return await storage.SaveFilePickerWithResultAsync(options);
    }

    public static void ApplyTheme(ThemeChoice theme) {
        if (Application.Current is { } application) {
            application.RequestedThemeVariant = theme switch {
                ThemeChoice.Light => ThemeVariant.Light,
                ThemeChoice.Dark => ThemeVariant.Dark,
                _ => ThemeVariant.Default,
            };
        }
    }

    public async ValueTask DisposeAsync() {
        await Demo.DisposeAsync();
    }
}
