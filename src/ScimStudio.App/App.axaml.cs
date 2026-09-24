using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using ScimStudio.App.Localization;
using ScimStudio.App.Services;
using ScimStudio.App.Settings;
using ScimStudio.App.ViewModels;
using ScimStudio.App.Views;

namespace ScimStudio.App;

public class App : Application {
    /// <summary>Where the settings are read from; tests point it somewhere of their own.</summary>
    public static string SettingsPath { get; set; } = SettingsStore.DefaultPath;

    public override void Initialize() {
        AvaloniaXamlLoader.Load(this);
    }

    public override void OnFrameworkInitializationCompleted() {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) {
            var services = new AppServices(new SettingsStore(SettingsPath));
            AppServices.ApplyTheme(services.Settings.Theme);

            var window = new MainWindow { DataContext = new MainWindowViewModel(services) };
            services.TopLevel = window;
            desktop.MainWindow = window;
            desktop.ShutdownRequested += async (_, _) => await services.DisposeAsync();

            // The tool talks to servers that answer anything; what a command did not expect is shown, not a reason to close the window.
            Dispatcher.UIThread.UnhandledException += (_, e) => {
                services.Notifier.Error(Localizer.Instance.Get("error.unexpected"), e.Exception.Message);
                e.Handled = true;
            };
        }

        base.OnFrameworkInitializationCompleted();
    }
}
