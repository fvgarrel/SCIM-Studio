using System.Net;
using System.Text.Json.Nodes;
using ScimStudio.DemoServer;

namespace ScimStudio.Tests.DemoServer;

public sealed class GroupEndpointTests(DemoServerFixture fixture) : IClassFixture<DemoServerFixture> {
    private readonly DemoScimServer _server = fixture.Server;
    private readonly ScimTestClient _client = fixture.Client;

    [Fact]
    public async Task Create_keeps_existing_members_and_fills_in_their_details() {
        var ann = await _client.CreateUserAsync("""{ "userName": "ann.members@example.com", "displayName": "Ann Members" }""");
        var bob = await _client.CreateUserAsync("""{ "userName": "bob.members@example.com" }""");

        var response = await _client.PostAsync("Groups", $$"""
            {
              "schemas": ["urn:ietf:params:scim:schemas:core:2.0:Group"],
              "displayName": "Members",
              "members": [
                { "value": "{{ann}}", "display": "Wrong name", "type": "Group" },
                { "value": "{{bob}}" },
                { "value": "{{ann}}" },
                { "value": "no-such-user" }
              ]
            }
            """);

        Assert.Equal(HttpStatusCode.Created, response.Status);
        var group = response.Body;
        Assert.Equal(new Uri($"{_server.BaseUrl}/Groups/{group["id"]}"), response.Message.Headers.Location);
        Assert.Equal("Group", (string?)group["meta"]!["resourceType"]);
        var members = group["members"]!.AsArray();
        Assert.Equal(2, members.Count);
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse($$"""
            {
              "value": "{{ann}}",
              "$ref": "{{_server.BaseUrl}}/Users/{{ann}}",
              "display": "Ann Members",
              "type": "User"
            }
            """), members[0]));
        Assert.Equal("bob.members@example.com", (string?)members[1]!["display"]);
    }

    [Fact]
    public async Task Create_without_displayName_fails_and_a_taken_one_conflicts() {
        await _client.CreateGroupAsync("""{ "displayName": "Taken Group" }""");

        var missing = await _client.PostAsync("Groups", """{ "members": [] }""");
        var taken = await _client.PostAsync("Groups", """{ "displayName": "TAKEN group" }""");

        Assert.Equal(HttpStatusCode.BadRequest, missing.Status);
        Assert.Equal("invalidValue", missing.ScimType);
        Assert.Equal(HttpStatusCode.Conflict, taken.Status);
        Assert.Equal("uniqueness", taken.ScimType);
    }

    [Fact]
    public async Task Patch_answers_204_and_changes_the_members() {
        var ann = await _client.CreateUserAsync("""{ "userName": "ann.patch@example.com" }""");
        var bob = await _client.CreateUserAsync("""{ "userName": "bob.patch@example.com" }""");
        var groupId = await _client.CreateGroupAsync($$"""{ "displayName": "Patched", "members": [{ "value": "{{ann}}" }] }""");

        var add = await _client.PatchAsync($"Groups/{groupId}", $$"""
            { "Operations": [{ "op": "Add", "path": "members", "value": [{ "value": "{{bob}}" }, { "value": "no-such-user" }] }] }
            """);
        var afterAdd = await MemberIdsAsync(groupId);
        var remove = await _client.PatchAsync($"Groups/{groupId}", $$"""
            { "Operations": [{ "op": "Remove", "path": "members", "value": [{ "value": "{{ann}}" }] }] }
            """);

        Assert.Equal(HttpStatusCode.NoContent, add.Status);
        Assert.Empty(add.Text);
        Assert.Equal([ann, bob], afterAdd);
        Assert.Equal(HttpStatusCode.NoContent, remove.Status);
        Assert.Equal([bob], await MemberIdsAsync(groupId));
    }

    [Fact]
    public async Task Patch_renames_a_group_the_way_Okta_does() {
        var groupId = await _client.CreateGroupAsync("""{ "displayName": "Old Name" }""");

        var response = await _client.PatchAsync($"Groups/{groupId}", $$"""
            {
              "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
              "Operations": [{ "op": "replace", "value": { "id": "{{groupId}}", "displayName": "New Name" } }]
            }
            """);

        Assert.Equal(HttpStatusCode.NoContent, response.Status);
        Assert.Equal("New Name", (string?)(await _client.GetAsync($"Groups/{groupId}")).Body["displayName"]);
    }

    [Fact]
    public async Task Put_replaces_the_members() {
        var ann = await _client.CreateUserAsync("""{ "userName": "ann.put@example.com" }""");
        var bob = await _client.CreateUserAsync("""{ "userName": "bob.put@example.com" }""");
        var groupId = await _client.CreateGroupAsync($$"""{ "displayName": "Put Group", "members": [{ "value": "{{ann}}" }] }""");

        var response = await _client.PutAsync($"Groups/{groupId}", $$"""{ "displayName": "Put Group", "members": [{ "value": "{{bob}}" }] }""");

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal(bob, (string?)Assert.Single(response.Body["members"]!.AsArray())!["value"]);
    }

    [Fact]
    public async Task User_lists_the_groups_it_belongs_to() {
        var ann = await _client.CreateUserAsync("""{ "userName": "ann.groups@example.com" }""");
        var first = await _client.CreateGroupAsync($$"""{ "displayName": "First of Ann", "members": [{ "value": "{{ann}}" }] }""");
        var second = await _client.CreateGroupAsync($$"""{ "displayName": "Second of Ann", "members": [{ "value": "{{ann}}" }] }""");

        var user = (await _client.GetAsync($"Users/{ann}")).Body;

        var groups = user["groups"]!.AsArray();
        Assert.Equal([first, second], groups.Select(group => (string?)group!["value"]));
        Assert.Equal("First of Ann", (string?)groups[0]!["display"]);
        Assert.Equal($"{_server.BaseUrl}/Groups/{first}", (string?)groups[0]!["$ref"]);
        Assert.Equal("direct", (string?)groups[0]!["type"]);
        var filtered = (await _client.GetAsync($"Users?filter=groups.value eq \"{second}\"")).Body;
        Assert.Equal(ann, (string?)Assert.Single(filtered["Resources"]!.AsArray())!["id"]);
    }

    [Fact]
    public async Task Deleting_a_user_takes_it_out_of_its_groups() {
        var ann = await _client.CreateUserAsync("""{ "userName": "ann.delete@example.com" }""");
        var bob = await _client.CreateUserAsync("""{ "userName": "bob.delete@example.com" }""");
        var groupId = await _client.CreateGroupAsync($$"""
            { "displayName": "Leavers", "members": [{ "value": "{{ann}}" }, { "value": "{{bob}}" }] }
            """);
        var before = (await _client.GetAsync($"Groups/{groupId}")).Body;

        await _client.DeleteAsync($"Users/{ann}");

        var after = (await _client.GetAsync($"Groups/{groupId}")).Body;
        Assert.Equal([bob], after["members"]!.AsArray().Select(member => (string?)member!["value"]));
        Assert.NotEqual((string?)before["meta"]!["lastModified"], (string?)after["meta"]!["lastModified"]);
    }

    [Fact]
    public async Task Groups_can_be_filtered_by_member_and_listed_without_members() {
        var ann = await _client.CreateUserAsync("""{ "userName": "ann.filter@example.com" }""");
        var groupId = await _client.CreateGroupAsync($$"""{ "displayName": "Filtered", "members": [{ "value": "{{ann}}" }] }""");

        var response = await _client.GetAsync($"Groups?filter=members[value eq \"{ann}\"]&excludedAttributes=members");

        var group = Assert.Single(response.Body["Resources"]!.AsArray())!.AsObject();
        Assert.Equal(groupId, (string?)group["id"]);
        Assert.False(group.ContainsKey("members"));
    }

    [Fact]
    public async Task Delete_answers_204_and_unknown_groups_404() {
        var groupId = await _client.CreateGroupAsync("""{ "displayName": "Short-lived" }""");

        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"Groups/{groupId}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"Groups/{groupId}")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"Groups/{groupId}")).Status);
    }

    private async Task<List<string?>> MemberIdsAsync(string groupId) {
        var group = (await _client.GetAsync($"Groups/{groupId}")).Body;
        return [.. group["members"]!.AsArray().Select(member => (string?)member!["value"])];
    }
}
