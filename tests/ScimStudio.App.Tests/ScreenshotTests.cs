using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using ScimStudio.App.Localization;
using ScimStudio.App.Services;
using ScimStudio.App.Settings;
using ScimStudio.App.ViewModels;
using ScimStudio.App.Views;
using ScimStudio.Core.Checks;
using ScimStudio.Core.Http;
using ScimStudio.DemoServer;

namespace ScimStudio.App.Tests;

/// <summary>
/// Draws every page against the demo server and saves it under <c>artifacts/screenshots</c>, in both themes - for a person to look at, since
/// no assertion can say whether a layout is right. What the tests do assert is that each page builds and draws without failing.
/// </summary>
public sealed class ScreenshotTests {
    private static readonly string Folder = Path.Combine(Root(), "artifacts", "screenshots");

    [AvaloniaTheory]
    [InlineData(ThemeChoice.Light, Localizer.ENGLISH)]
    [InlineData(ThemeChoice.Dark, Localizer.ENGLISH)]
    [InlineData(ThemeChoice.Light, Localizer.GERMAN)]
    public async Task Every_page_draws(ThemeChoice theme, string language) {
        await using var server = await DemoScimServer.StartAsync();
        var services = Services(language, theme);
        services.Settings.Profiles.Add(new ConnectionProfile { Name = "Staging", BaseUrl = "https://idm.staging.example.com/scim/v2" });

        var main = new MainWindowViewModel(services);
        var window = new MainWindow { DataContext = main, Width = 1320, Height = 840 };
        services.TopLevel = window;
        window.Show();

        var suffix = $"{theme.ToString().ToLowerInvariant()}-{language}";
        main.Connect.Selected = main.Connect.Profiles[0];
        Save(window, $"01-connect-{suffix}");

        var profile = new ConnectionProfile { Name = "Demo server", BaseUrl = server.BaseUrl.AbsoluteUri };
        var session = new Session(profile, server.Token, services.Log, isDemo: true);
        session.Configuration = await session.Client.GetServiceProviderConfigAsync();
        var shell = new ShellViewModel(services, session, () => { });
        main.Page = shell;

        await Settle(() => shell.Users.Items.Count > 0);
        shell.Users.Selected = shell.Users.Items[1];
        await Settle(() => shell.Users.Editor?.Groups.Count > 0);
        Save(window, $"02-users-{suffix}");

        shell.Selected = shell.Navigation[1];
        await Settle(() => shell.Groups.Items.Count > 0);
        shell.Groups.Selected = shell.Groups.Items[0];
        await Settle(() => shell.Groups.Editor?.Members.Count > 0);
        Save(window, $"03-groups-{suffix}");

        shell.Selected = shell.Navigation[2];
        await Settle(() => shell.Server.Schemas.Count > 0);
        Save(window, $"04-server-{suffix}");

        shell.Selected = shell.Navigation[3];
        await shell.Checks.RunCommand.ExecuteAsync(null);
        await Settle(() => !shell.Checks.IsRunning);
        shell.Checks.Selected = shell.Checks.Categories[0].Items[0];
        Save(window, $"05-checks-{suffix}");
        SaveReports(shell.Checks.Snapshot(), language);

        // A right click on the row, as a person would, opens what a check can be copied as.
        var row = window.GetVisualDescendants().OfType<Button>().First(b => b.DataContext == shell.Checks.Selected && b.ContextMenu is not null);
        var point = row.TranslatePoint(new Point(40, 12), window)!.Value;
        window.MouseDown(point, MouseButton.Right);
        window.MouseUp(point, MouseButton.Right);
        Dispatcher.UIThread.RunJobs();
        Assert.True(row.ContextMenu!.IsOpen);
        Save(TopLevel.GetTopLevel(row.ContextMenu)!, $"05-checks-menu-{suffix}");
        row.ContextMenu.Close();

        shell.Checks.OnlyProblems = true;
        shell.Checks.Selected = shell.Checks.Categories.SelectMany(c => c.Items).First(i => i.IsProblem);
        Save(window, $"05-checks-problems-{suffix}");
        shell.Checks.OnlyProblems = false;

        shell.Selected = shell.Navigation[4];
        Save(window, $"06-generator-{suffix}");

        shell.Selected = shell.Navigation[5];
        await Settle(() => shell.Log.Items.Count > 0);
        shell.Log.Selected = shell.Log.Items.FirstOrDefault(i => i.Method == "PATCH") ?? shell.Log.Items[0];
        Save(window, $"07-log-{suffix}");

        shell.SelectedFooter = shell.Footer[0];
        Save(window, $"08-settings-{suffix}");

        Assert.Equal(0, shell.Checks.Failed);
        window.Close();
        shell.Dispose();
    }

    private static AppServices Services(string language, ThemeChoice theme) {
        var path = Path.Combine(Path.GetTempPath(), $"scimstudio-{Guid.NewGuid():N}", "settings.json");
        var services = new AppServices(new SettingsStore(path));
        services.Settings.Theme = theme;
        Localizer.Instance.Language = language;
        AppServices.ApplyTheme(theme);
        return services;
    }

    /// <summary>Lets the dispatcher run - requests complete, bindings update, layout settles - until the condition holds.</summary>
    /// <param name="done">What the page is waiting for.</param>
    private static async Task Settle(Func<bool> done) {
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (!done() && DateTime.UtcNow < deadline) {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(25);
        }

        await Task.Delay(250);
        Dispatcher.UIThread.RunJobs();
    }

    /// <summary>
    /// What the export writes, and what a copy of the first problem holds, under <c>artifacts/reports</c>: a report is looked at like a
    /// page, since it is read like one.
    /// </summary>
    /// <param name="run">The run to report.</param>
    /// <param name="language">The language it is written in.</param>
    private static void SaveReports(CheckRun run, string language) {
        var folder = Path.Combine(Root(), "artifacts", "reports");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, $"report-{language}.md"), CheckReport.Markdown(run));
        File.WriteAllText(Path.Combine(folder, $"report-{language}.json"), CheckReport.Json(run));

        var problem = run.Results.First(r => r.Status is CheckStatus.Failed or CheckStatus.Warning);
        File.WriteAllText(Path.Combine(folder, $"check-{language}.md"), CheckReport.Markdown(run, problem));
        File.WriteAllText(Path.Combine(folder, $"check-{language}.sh"), CheckReport.Commands(problem, ShellCommand.Curl));
        File.WriteAllText(Path.Combine(folder, $"check-{language}.ps1"), CheckReport.Commands(problem, ShellCommand.PowerShell));
    }

    private static void Save(TopLevel window, string name) {
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        var frame = window.CaptureRenderedFrame() ?? throw new InvalidOperationException("No frame was drawn.");

        Directory.CreateDirectory(Folder);
        frame.Save(Path.Combine(Folder, $"{name}.png"), PngBitmapEncoderOptions.Default);
    }

    private static string Root([CallerFilePath] string source = "") {
        var directory = new DirectoryInfo(Path.GetDirectoryName(source)!);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "ScimStudio.slnx"))) {
            directory = directory.Parent;
        }

        return directory?.FullName ?? AppContext.BaseDirectory;
    }
}
