using System.Text.Json.Nodes;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Input.Platform;
using Avalonia.Threading;
using ScimStudio.App.Localization;
using ScimStudio.App.Services;
using ScimStudio.App.Settings;
using ScimStudio.App.ViewModels;
using ScimStudio.Core.Checks;
using ScimStudio.Core.Http;
using ScimStudio.DemoServer;

namespace ScimStudio.App.Tests;

/// <summary>
/// What an export and a copy of one check hold, from a real run against the demo server: what a reader needs to act on a problem, in the
/// language of the interface - and never the token.
/// </summary>
public sealed class ReportTests {
    // One of the demo server's warnings: there it bends RFC 7644 on purpose, for Entra ID.
    private const string WARNING = "users.patchReplaceNoMatch";

    [AvaloniaFact]
    public async Task The_markdown_report_leads_with_the_problems_and_details_what_did_not_pass() {
        await using var world = await World.RunAsync();

        var report = CheckReport.Markdown(world.Checks.Snapshot());

        Assert.StartsWith("# SCIM conformance report", report, StringComparison.Ordinal);
        Assert.Contains(world.Server.BaseUrl.AbsoluteUri, report, StringComparison.Ordinal);
        Assert.Contains($"passed: {world.Checks.Passed} · warnings: {world.Checks.Warnings} · failed: 0", report, StringComparison.Ordinal);
        Assert.All(ConformanceSuite.Checks, check => Assert.Contains(Title(check.Id), report, StringComparison.Ordinal));

        var problems = Section(report, "## Problems", "## Checks");
        Assert.Contains(Title(WARNING), problems, StringComparison.Ordinal);

        var details = Section(report, "## Details", null);
        Assert.Contains($"### ⚠️ {Title(WARNING)}", details, StringComparison.Ordinal);
        Assert.Contains($"PATCH {world.Server.BaseUrl.AbsoluteUri.TrimEnd('/')}/Users/", details, StringComparison.Ordinal);
        Assert.Contains("`optional.me`", details, StringComparison.Ordinal);
        Assert.DoesNotContain("`discovery.config`", details, StringComparison.Ordinal);
        Assert.DoesNotContain(world.Server.Token, report, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task The_json_report_holds_every_check_with_every_request_and_answer() {
        await using var world = await World.RunAsync();

        var text = CheckReport.Json(world.Checks.Snapshot());
        var report = JsonNode.Parse(text)!.AsObject();

        Assert.DoesNotContain(world.Server.Token, text, StringComparison.Ordinal);
        Assert.Equal(world.Server.BaseUrl.AbsoluteUri, report["server"]!["baseUrl"]!.GetValue<string>());
        Assert.True(report["server"]!["configuration"]!["patch"]!["supported"]!.GetValue<bool>());
        Assert.Equal(world.Checks.Passed, report["summary"]!["passed"]!.GetValue<int>());
        Assert.Equal(world.Checks.Unsupported, report["summary"]!["unsupported"]!.GetValue<int>());

        var checks = report["checks"]!.AsArray();
        Assert.Equal(ConformanceSuite.Checks.Select(c => c.Id), checks.Select(c => c!["id"]!.GetValue<string>()));

        var warning = checks.Single(c => c!["id"]!.GetValue<string>() == WARNING)!;
        Assert.Equal("warning", warning["status"]!.GetValue<string>());
        Assert.Equal("note.noTargetAccepted", warning["notes"]![0]!["key"]!.GetValue<string>());

        var patch = warning["requests"]!.AsArray().Single(r => r!["method"]!.GetValue<string>() == "PATCH")!;
        Assert.Equal(200, patch["status"]!.GetValue<int>());
        Assert.StartsWith("Bearer ••••", patch["requestHeaders"]!["Authorization"]!.GetValue<string>(), StringComparison.Ordinal);
        Assert.Equal("replace", patch["requestBody"]!["Operations"]![0]!["op"]!.GetValue<string>());
        Assert.IsType<JsonObject>(patch["responseBody"]);
    }

    [AvaloniaFact]
    public async Task A_check_is_copied_as_markdown_json_curl_and_powershell() {
        await using var world = await World.RunAsync(withWindow: true);
        var check = world.Checks.Categories.SelectMany(c => c.Items).Single(i => i.Info.Id == WARNING);

        await check.CopyResultCommand.ExecuteAsync(null);
        var markdown = await world.ClipboardAsync();
        Assert.StartsWith($"## ⚠️ {Title(WARNING)}", markdown, StringComparison.Ordinal);
        Assert.Contains(check.FirstNote!, markdown, StringComparison.Ordinal);
        Assert.Contains("```http", markdown, StringComparison.Ordinal);

        await check.CopyJsonCommand.ExecuteAsync(null);
        var json = await world.ClipboardAsync();
        var document = JsonNode.Parse(json)!;
        Assert.Equal(WARNING, Assert.Single(document["checks"]!.AsArray())!["id"]!.GetValue<string>());
        Assert.Null(document["summary"]);

        await check.CopyCurlCommand.ExecuteAsync(null);
        var curl = await world.ClipboardAsync();
        Assert.Equal(check.Exchanges.Count, curl.Split("\ncurl").Length - 1);
        Assert.Contains($"${ShellCommand.TOKEN_VARIABLE}", curl, StringComparison.Ordinal);

        await check.CopyPowerShellCommand.ExecuteAsync(null);
        var powerShell = await world.ClipboardAsync();
        Assert.Contains("Invoke-RestMethod -Method PATCH", powerShell, StringComparison.Ordinal);

        foreach (var copy in new[] { markdown, json, curl, powerShell }) {
            Assert.DoesNotContain(world.Server.Token, copy, StringComparison.Ordinal);
        }
    }

    [AvaloniaFact]
    public async Task The_report_speaks_the_language_of_the_interface() {
        await using var world = await World.RunAsync();
        try {
            Localizer.Instance.Language = Localizer.GERMAN;

            var report = CheckReport.Markdown(world.Checks.Snapshot());

            Assert.StartsWith("# SCIM-Konformitätsbericht", report, StringComparison.Ordinal);
            Assert.Contains("## Probleme", report, StringComparison.Ordinal);
            Assert.Contains(Title(WARNING), report, StringComparison.Ordinal);
        } finally {
            Localizer.Instance.Language = Localizer.ENGLISH;
        }
    }

    [AvaloniaFact]
    public async Task Exporting_waits_for_a_finished_run_and_names_the_file_after_the_server_and_the_time() {
        await using var world = await World.StartAsync(withWindow: false);
        Assert.False(world.Checks.ExportCommand.CanExecute(null));

        await world.Checks.RunCommand.ExecuteAsync(null);
        Assert.True(world.Checks.ExportCommand.CanExecute(null));

        var run = world.Checks.Snapshot();
        Assert.Equal($"scim-checks-127.0.0.1-{run.StartedAt:yyyy-MM-dd-HHmm}", CheckReport.FileName(run));
    }

    [Fact]
    public void A_body_that_holds_backticks_gets_a_longer_fence() {
        var exchange = new HttpExchange {
            Sequence = 1,
            StartedAt = DateTimeOffset.Now,
            Method = "GET",
            Url = new Uri("https://scim.example.com/scim/v2/Users/1"),
            StatusCode = 200,
            ResponseBody = "{\"displayName\": \"```\"}",
        };
        var result = new CheckResult {
            Check = ConformanceSuite.Checks[0],
            Status = CheckStatus.Failed,
            Exchanges = [exchange],
        };
        var run = new CheckRun { BaseUrl = exchange.Url, StartedAt = DateTimeOffset.Now, Results = [result] };

        var copy = CheckReport.Markdown(run, result);

        Assert.Contains("````http", copy, StringComparison.Ordinal);
    }

    private static string Title(string id) {
        return Localizer.Instance.Get($"check.{id}");
    }

    private static string Section(string report, string start, string? end) {
        var from = report.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, $"No {start}.");
        return end is null ? report[from..] : report[from..report.IndexOf(end, from, StringComparison.Ordinal)];
    }

    /// <summary>A demo server, and the checks page of a session on it - in a window when the clipboard is needed.</summary>
    private sealed class World : IAsyncDisposable {
        private readonly Window? _window;

        private World(DemoScimServer server, AppServices services, ShellViewModel shell, Window? window) {
            Server = server;
            Services = services;
            Shell = shell;
            _window = window;
        }

        public DemoScimServer Server { get; }

        public AppServices Services { get; }

        public ShellViewModel Shell { get; }

        public ChecksViewModel Checks => Shell.Checks;

        public static async Task<World> StartAsync(bool withWindow) {
            var server = await DemoScimServer.StartAsync(new DemoServerOptions { Seed = false });
            var path = Path.Combine(Path.GetTempPath(), $"scimstudio-{Guid.NewGuid():N}", "settings.json");
            var services = new AppServices(new SettingsStore(path));
            Localizer.Instance.Language = Localizer.ENGLISH;

            var profile = new ConnectionProfile { Name = "Demo", BaseUrl = server.BaseUrl.AbsoluteUri };
            var session = new Session(profile, server.Token, services.Log, isDemo: true);
            session.Configuration = await session.Client.GetServiceProviderConfigAsync();

            Window? window = null;
            if (withWindow) {
                window = new Window();
                window.Show();
                services.TopLevel = window;
            }

            return new World(server, services, new ShellViewModel(services, session, () => { }), window);
        }

        /// <summary>A world whose checks have run once.</summary>
        /// <param name="withWindow">Whether to open a window, for the clipboard.</param>
        public static async Task<World> RunAsync(bool withWindow = false) {
            var world = await StartAsync(withWindow);
            await world.Checks.RunCommand.ExecuteAsync(null);
            return world;
        }

        public async Task<string> ClipboardAsync() {
            Dispatcher.UIThread.RunJobs();
            return await _window!.Clipboard!.TryGetTextAsync() ?? string.Empty;
        }

        public async ValueTask DisposeAsync() {
            _window?.Close();
            Shell.Dispose();
            await Services.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}
