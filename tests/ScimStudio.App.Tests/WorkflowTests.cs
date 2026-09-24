using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using ScimStudio.App.Localization;
using ScimStudio.App.Services;
using ScimStudio.App.Settings;
using ScimStudio.App.ViewModels;
using ScimStudio.Core.Dialects;
using ScimStudio.Core.Scim;
using ScimStudio.DemoServer;

namespace ScimStudio.App.Tests;

/// <summary>
/// What a person does on the users and groups pages, done through the view models against the demo server in every dialect - and checked
/// on the server, not in the view models, since the point of the tool is what arrives there.
/// </summary>
public sealed class WorkflowTests {
    [AvaloniaTheory]
    [InlineData(DialectKind.Rfc7644)]
    [InlineData(DialectKind.EntraId)]
    [InlineData(DialectKind.Okta)]
    public async Task A_user_is_created_changed_switched_off_and_deleted(DialectKind dialect) {
        await using var world = await World.StartAsync(dialect);
        var users = world.Shell.Users;
        await Settle(() => !users.IsLoading);

        users.NewCommand.Execute(null);
        var editor = users.Editor!;
        editor.UserName = "grace@example.com";
        editor.GivenName = "Grace";
        editor.FamilyName = "Hopper";
        editor.Email = "grace@example.com";
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Null(editor.Error);
        var created = Assert.Single((await world.Client.ListUsersAsync()).Resources);
        Assert.Equal(("Grace", "Hopper"), (created.GivenName, created.FamilyName));
        Assert.Contains(users.Items, item => item.Id == created.Id);

        editor = users.Editor!;
        editor.DisplayName = "Rear Admiral Hopper";
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Rear Admiral Hopper", (await world.Client.GetUserAsync(created.Id)).DisplayName);

        await editor.ToggleActiveCommand.ExecuteAsync(null);
        Assert.False((await world.Client.GetUserAsync(created.Id)).Active);
        Assert.True(users.Items.Single(item => item.Id == created.Id).IsInactive);

        await Confirm(world.Services, editor.DeleteCommand.ExecuteAsync(null));
        Assert.Equal(0, (await world.Client.ListUsersAsync()).TotalResults);
        Assert.DoesNotContain(users.Items, item => item.Id == created.Id);
    }

    [AvaloniaTheory]
    [InlineData(DialectKind.Rfc7644)]
    [InlineData(DialectKind.EntraId)]
    [InlineData(DialectKind.Okta)]
    public async Task A_group_is_created_with_members_changed_and_deleted(DialectKind dialect) {
        await using var world = await World.StartAsync(dialect);
        var ada = await world.CreateUserAsync("ada@example.com");
        var alan = await world.CreateUserAsync("alan@example.com");

        world.Shell.Selected = world.Shell.Navigation[1];
        var groups = world.Shell.Groups;
        await Settle(() => !groups.IsLoading);

        groups.NewCommand.Execute(null);
        var editor = groups.Editor!;
        editor.DisplayName = "Pioneers";
        editor.CandidateSearch = "ada";
        await Settle(() => editor.Candidates.Count > 0);
        await editor.AddMemberCommand.ExecuteAsync(editor.Candidates.Single());
        await editor.SaveCommand.ExecuteAsync(null);

        Assert.Null(editor.Error);
        var created = Assert.Single((await world.Client.ListGroupsAsync()).Resources);
        Assert.Equal([ada.Id], (await world.Client.GetGroupAsync(created.Id)).Members.Select(m => m.Id));

        editor = groups.Editor!;
        editor.CandidateSearch = "alan";
        await Settle(() => editor.Candidates.Count > 0);
        await editor.AddMemberCommand.ExecuteAsync(editor.Candidates.Single());
        await editor.RemoveMemberCommand.ExecuteAsync(editor.Members.Single(m => m.Id == ada.Id));
        Assert.Equal([alan.Id], (await world.Client.GetGroupAsync(created.Id)).Members.Select(m => m.Id));

        editor.DisplayName = "Engines";
        await editor.SaveCommand.ExecuteAsync(null);
        Assert.Equal("Engines", (await world.Client.GetGroupAsync(created.Id)).DisplayName);

        await Confirm(world.Services, editor.DeleteCommand.ExecuteAsync(null));
        Assert.Equal(0, (await world.Client.ListGroupsAsync()).TotalResults);
    }

    [AvaloniaFact]
    public async Task A_user_joins_and_leaves_a_group_from_the_user_page() {
        await using var world = await World.StartAsync(DialectKind.Rfc7644);
        var ada = await world.CreateUserAsync("ada@example.com");
        var team = await world.Client.CreateGroupAsync(new System.Text.Json.Nodes.JsonObject {
            ["schemas"] = new System.Text.Json.Nodes.JsonArray(ScimSchemas.GROUP),
            ["displayName"] = "Analysts",
        });

        // The page listed the users when the session opened, before these existed; a person would refresh as well.
        var users = world.Shell.Users;
        await users.RefreshCommand.ExecuteAsync(null);
        users.Selected = users.Items.Single();
        var editor = users.Editor!;

        await editor.LoadJoinableGroupsCommand.ExecuteAsync(null);
        await editor.JoinCommand.ExecuteAsync(editor.JoinableGroups.Single());
        Assert.Equal([ada.Id], (await world.Client.GetGroupAsync(team.Id)).Members.Select(m => m.Id));
        Assert.Single(editor.Groups);

        await editor.LeaveCommand.ExecuteAsync(editor.Groups.Single());
        Assert.Empty((await world.Client.GetGroupAsync(team.Id)).Members);
        Assert.Empty(editor.Groups);
    }

    [AvaloniaFact]
    public async Task Changing_the_language_rewrites_what_the_pages_composed() {
        await using var world = await World.StartAsync(DialectKind.Rfc7644);
        try {
            Localizer.Instance.Language = Localizer.GERMAN;
            Assert.Equal("Benutzer", world.Shell.Navigation[0].Title);
            Assert.Equal("Microsoft Entra ID", DialectOption.For(DialectKind.EntraId).Name);

            Localizer.Instance.Language = Localizer.ENGLISH;
            Assert.Equal("Users", world.Shell.Navigation[0].Title);
            Assert.Equal("Log", Localizer.Instance.Entry("nav.log").Value);
        } finally {
            Localizer.Instance.Language = Localizer.ENGLISH;
        }
    }

    /// <summary>Answers yes to the question the command is about to ask, and waits for the command.</summary>
    /// <param name="services">Where the dialog appears.</param>
    /// <param name="command">The command, started.</param>
    private static async Task Confirm(AppServices services, Task command) {
        await Settle(() => services.Dialogs.Current is not null);
        services.Dialogs.Current!.ConfirmCommand.Execute(null);
        await command;
    }

    private static async Task Settle(Func<bool> done) {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (!done() && DateTime.UtcNow < deadline) {
            Dispatcher.UIThread.RunJobs();
            await Task.Delay(20);
        }

        Assert.True(done(), "The page did not get there in time.");
    }

    /// <summary>A demo server, and a session on it as the interface opens one.</summary>
    private sealed class World : IAsyncDisposable {
        private World(DemoScimServer server, AppServices services, ShellViewModel shell, ScimClient client) {
            Server = server;
            Services = services;
            Shell = shell;
            Client = client;
        }

        public DemoScimServer Server { get; }

        public AppServices Services { get; }

        public ShellViewModel Shell { get; }

        public ScimClient Client { get; }

        public static async Task<World> StartAsync(DialectKind dialect) {
            var server = await DemoScimServer.StartAsync(new DemoServerOptions { Seed = false });
            var path = Path.Combine(Path.GetTempPath(), $"scimstudio-{Guid.NewGuid():N}", "settings.json");
            var services = new AppServices(new SettingsStore(path));
            Localizer.Instance.Language = Localizer.ENGLISH;

            var profile = new ConnectionProfile { Name = "Demo", BaseUrl = server.BaseUrl.AbsoluteUri, Dialect = dialect };
            var session = new Session(profile, server.Token, services.Log, isDemo: true);
            var client = new ScimClient(Session.Connection(profile, server.Token), new Core.Http.ExchangeLog());
            return new World(server, services, new ShellViewModel(services, session, null, () => { }), client);
        }

        public async Task<ScimUser> CreateUserAsync(string userName) {
            return await Client.CreateUserAsync(new System.Text.Json.Nodes.JsonObject {
                ["schemas"] = new System.Text.Json.Nodes.JsonArray(ScimSchemas.USER),
                ["userName"] = userName,
            });
        }

        public async ValueTask DisposeAsync() {
            Shell.Dispose();
            Client.Dispose();
            await Services.DisposeAsync();
            await Server.DisposeAsync();
        }
    }
}
