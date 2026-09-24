using System.Text.Json.Nodes;
using ScimStudio.Core.Dialects;
using ScimStudio.Core.Scim;

namespace ScimStudio.Tests.Core;

/// <summary>Every dialect has to get a user and a group through their whole life on a server that follows the RFC and its providers.</summary>
public sealed class DialectTests : DemoServerTest {
    [Theory]
    [InlineData(DialectKind.Rfc7644)]
    [InlineData(DialectKind.EntraId)]
    [InlineData(DialectKind.Okta)]
    public async Task A_user_is_created_changed_switched_off_and_deleted(DialectKind kind) {
        var dialect = ScimDialect.For(kind);
        var draft = new UserDraft { UserName = "ada@example.com", GivenName = "Ada", FamilyName = "Lovelace", Email = "ada@example.com" };

        var created = await dialect.CreateUserAsync(Client, draft, Token);
        Assert.False(created.Matched);

        var edited = UserDraft.From(created.Resource) with {
            GivenName = "Augusta",
            DisplayName = "Augusta Ada King",
            Email = "augusta@example.com",
        };
        await dialect.UpdateUserAsync(Client, created.Resource, edited, Token);

        var read = await Client.GetUserAsync(created.Resource.Id, Token);
        Assert.Equal("Augusta", read.GivenName);
        Assert.Equal("Lovelace", read.FamilyName);
        Assert.Equal("Augusta Ada King", read.DisplayName);
        Assert.Equal("augusta@example.com", read.Email);

        await dialect.SetActiveAsync(Client, read, active: false, Token);
        Assert.False((await Client.GetUserAsync(read.Id, Token)).Active);

        await dialect.SetActiveAsync(Client, read, active: true, Token);
        Assert.True((await Client.GetUserAsync(read.Id, Token)).Active);

        await dialect.DeleteUserAsync(Client, read, Token);
        var gone = await Assert.ThrowsAsync<ScimException>(() => Client.GetUserAsync(read.Id, Token));
        Assert.Equal(404, gone.Error.Status);
    }

    [Theory]
    [InlineData(DialectKind.Rfc7644)]
    [InlineData(DialectKind.EntraId)]
    [InlineData(DialectKind.Okta)]
    public async Task A_group_is_created_filled_emptied_renamed_and_deleted(DialectKind kind) {
        var dialect = ScimDialect.For(kind);
        var ada = await CreateUserAsync("ada@example.com");
        var alan = await CreateUserAsync("alan@example.com");

        var created = await dialect.CreateGroupAsync(Client, new GroupDraft { DisplayName = "Analysts", Members = [Member(ada)] }, Token);
        Assert.Equal(Ids(ada), await MemberIdsAsync(created.Resource.Id));

        await dialect.AddMembersAsync(Client, created.Resource, [Member(alan)], Token);
        Assert.Equal(Ids(ada, alan), await MemberIdsAsync(created.Resource.Id));

        await dialect.RemoveMembersAsync(Client, created.Resource, [ada.Id], Token);
        Assert.Equal(Ids(alan), await MemberIdsAsync(created.Resource.Id));

        var group = await Client.GetGroupAsync(created.Resource.Id, Token);
        await dialect.UpdateGroupAsync(Client, group, GroupDraft.From(group) with { DisplayName = "Engines" }, Token);
        Assert.Equal("Engines", (await Client.GetGroupAsync(group.Id, Token)).DisplayName);

        await dialect.DeleteGroupAsync(Client, group, Token);
        var gone = await Assert.ThrowsAsync<ScimException>(() => Client.GetGroupAsync(group.Id, Token));
        Assert.Equal(404, gone.Error.Status);
    }

    [Fact]
    public async Task Rfc7644_creates_blindly_and_is_refused_a_duplicate() {
        var dialect = ScimDialect.For(DialectKind.Rfc7644);
        await dialect.CreateUserAsync(Client, new UserDraft { UserName = "ada@example.com" }, Token);

        var again = new UserDraft { UserName = "ada@example.com" };
        var refused = await Assert.ThrowsAsync<ScimException>(() => dialect.CreateUserAsync(Client, again, Token));
        Assert.Equal(409, refused.Error.Status);
    }

    [Theory]
    [InlineData(DialectKind.EntraId)]
    [InlineData(DialectKind.Okta)]
    public async Task Identity_providers_look_first_and_match_rather_than_duplicate(DialectKind kind) {
        var existing = await CreateUserAsync("ada@example.com");

        var matched = await ScimDialect.For(kind).CreateUserAsync(Client, new UserDraft { UserName = "ada@example.com" }, Token);

        Assert.True(matched.Matched);
        Assert.Equal(existing.Id, matched.Resource.Id);

        // The one POST is the test's own; the dialect only looked.
        Assert.Single(Log.Snapshot(), e => e.Method == "POST" && e.Url.AbsolutePath.EndsWith("/Users", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Rfc7644_patches_exactly_what_changed() {
        var dialect = ScimDialect.For(DialectKind.Rfc7644);
        var created = await dialect.CreateUserAsync(Client, new UserDraft { UserName = "ada@example.com", GivenName = "Ada" }, Token);

        await dialect.UpdateUserAsync(Client, created.Resource, UserDraft.From(created.Resource) with { GivenName = "Augusta" }, Token);

        var patch = Body(Log.Snapshot().Single(e => e.Method == "PATCH").RequestBody);
        var operation = Assert.Single(patch["Operations"]!.AsArray())!;
        Assert.Equal("replace", (string?)operation["op"]);
        Assert.Equal("name.givenName", (string?)operation["path"]);
        Assert.Equal("Augusta", (string?)operation["value"]);
    }

    [Fact]
    public async Task Entra_id_capitalises_its_operations_and_sends_booleans_as_strings() {
        var dialect = ScimDialect.For(DialectKind.EntraId);
        var created = await dialect.CreateUserAsync(Client, new UserDraft { UserName = "ada@example.com" }, Token);

        await dialect.SetActiveAsync(Client, created.Resource, active: false, Token);

        Assert.Contains(Log.Snapshot(), e => e.Method == "GET" && e.Url.Query.Contains("filter=userName", StringComparison.Ordinal));
        var operation = Body(Log.Snapshot().Single(e => e.Method == "PATCH").RequestBody)["Operations"]![0]!;
        Assert.Equal("Replace", (string?)operation["op"]);
        Assert.Equal("False", (string?)operation["value"]);
        Assert.False((await Client.GetUserAsync(created.Resource.Id, Token)).Active);
    }

    [Fact]
    public async Task Okta_restates_the_whole_user_and_switches_without_a_path() {
        var dialect = ScimDialect.For(DialectKind.Okta);
        var created = await dialect.CreateUserAsync(Client, new UserDraft { UserName = "ada@example.com", GivenName = "Ada" }, Token);

        await dialect.UpdateUserAsync(Client, created.Resource, UserDraft.From(created.Resource) with { GivenName = "Augusta" }, Token);
        await dialect.SetActiveAsync(Client, created.Resource, active: false, Token);

        var put = Body(Log.Snapshot().Single(e => e.Method == "PUT").RequestBody);
        Assert.Equal(created.Resource.Id, (string?)put["id"]);
        Assert.Empty(put["groups"]!.AsArray());

        var operation = Body(Log.Snapshot().Single(e => e.Method == "PATCH").RequestBody)["Operations"]![0]!.AsObject();
        Assert.False(operation.ContainsKey("path"));
        Assert.False((bool)operation["value"]!["active"]!);
    }

    private async Task<string[]> MemberIdsAsync(string groupId) {
        var group = await Client.GetGroupAsync(groupId, Token);
        return [.. group.Members.Select(m => m.Id).Order(StringComparer.Ordinal)];
    }

    private static string[] Ids(params ScimUser[] users) {
        return [.. users.Select(u => u.Id).Order(StringComparer.Ordinal)];
    }

    private static ScimMember Member(ScimUser user) {
        return new ScimMember(user.Id, user.Label, "User");
    }

    private static JsonObject Body(string? text) {
        return JsonNode.Parse(text!)!.AsObject();
    }
}
