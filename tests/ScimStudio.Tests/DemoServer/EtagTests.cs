using System.Net;

namespace ScimStudio.Tests.DemoServer;

public sealed class EtagTests(DemoServerFixture fixture) : IClassFixture<DemoServerFixture> {
    private readonly ScimTestClient _client = fixture.Client;

    [Fact]
    public async Task Version_starts_at_1_and_counts_only_writes_that_change_the_resource() {
        var created = await _client.PostAsync("Users", """{ "userName": "count@example.com", "title": "Old" }""");
        var id = (string)created.Body["id"]!;

        var samePatch = await _client.PatchAsync($"Users/{id}", PatchOps.Replace("title", "\"Old\""));
        var samePut = await _client.PutAsync($"Users/{id}", """{ "userName": "count@example.com", "title": "Old" }""");
        var changedPatch = await _client.PatchAsync($"Users/{id}", PatchOps.Replace("title", "\"New\""));
        var changedPut = await _client.PutAsync($"Users/{id}", """{ "userName": "count@example.com", "title": "Newer" }""");

        Assert.Equal("W/\"1\"", Version(created));
        Assert.Equal("W/\"1\"", Version(samePatch));
        Assert.Equal("W/\"1\"", Version(samePut));
        Assert.Equal("W/\"2\"", Version(changedPatch));
        Assert.Equal("W/\"3\"", Version(changedPut));
        Assert.Equal("W/\"3\"", Version(await _client.GetAsync($"Users/{id}")));
    }

    [Fact]
    public async Task Every_answer_with_one_resource_carries_its_version_as_ETag() {
        var created = await _client.PostAsync("Users", """{ "userName": "etag@example.com" }""");
        var id = (string)created.Body["id"]!;

        var read = await _client.GetAsync($"Users/{id}");
        var replaced = await _client.PutAsync($"Users/{id}", """{ "userName": "etag@example.com", "title": "Tagged" }""");
        var patched = await _client.PatchAsync($"Users/{id}", PatchOps.Replace("title", "\"Retagged\""));
        var trimmed = await _client.GetAsync($"Users/{id}?attributes=userName");
        var versionOnly = await _client.GetAsync($"Users/{id}?attributes=meta.version");

        Assert.All([created, read, replaced, patched], response => Assert.Equal(Version(response), ETag(response)));
        Assert.Equal("W/\"3\"", ETag(patched));
        Assert.False(trimmed.Body.ContainsKey("meta"));
        Assert.Equal("W/\"3\"", ETag(trimmed));
        Assert.Equal(["version"], versionOnly.Body["meta"]!.AsObject().Select(property => property.Key));
    }

    [Fact]
    public async Task Patched_group_answers_204_with_its_new_version_as_ETag() {
        var ann = await _client.CreateUserAsync("""{ "userName": "ann.etag@example.com" }""");
        var groupId = await _client.CreateGroupAsync("""{ "displayName": "Tagged Group" }""");

        var patched = await _client.PatchAsync($"Groups/{groupId}", $$"""
            { "Operations": [{ "op": "add", "path": "members", "value": [{ "value": "{{ann}}" }] }] }
            """);

        Assert.Equal(HttpStatusCode.NoContent, patched.Status);
        Assert.Empty(patched.Text);
        Assert.Equal("W/\"2\"", ETag(patched));
        Assert.Equal("W/\"2\"", Version(await _client.GetAsync($"Groups/{groupId}")));
    }

    [Fact]
    public async Task Version_follows_what_is_stored_so_a_group_losing_a_member_counts_but_derived_groups_do_not() {
        var ann = await _client.CreateUserAsync("""{ "userName": "ann.derived@example.com" }""");
        var bob = await _client.CreateUserAsync("""{ "userName": "bob.derived@example.com" }""");
        var groupId = await _client.CreateGroupAsync($$"""
            { "displayName": "Derived", "members": [{ "value": "{{ann}}" }, { "value": "{{bob}}" }] }
            """);

        await _client.DeleteAsync($"Users/{bob}");

        var user = await _client.GetAsync($"Users/{ann}");
        Assert.Equal(groupId, (string?)Assert.Single(user.Body["groups"]!.AsArray())!["value"]);
        Assert.Equal("W/\"1\"", Version(user));
        Assert.Equal("W/\"2\"", Version(await _client.GetAsync($"Groups/{groupId}")));
    }

    [Theory]
    [InlineData("W/\"1\"")]
    [InlineData("\"1\"")]
    [InlineData("W/\"7\", W/\"1\"")]
    [InlineData("*")]
    public async Task Get_with_If_None_Match_naming_the_version_answers_304_without_a_body(string condition) {
        var userId = await _client.CreateUserAsync($$"""{ "userName": "{{Guid.NewGuid()}}@example.com" }""");
        var groupId = await _client.CreateGroupAsync($$"""{ "displayName": "{{Guid.NewGuid()}}" }""");

        var user = await _client.SendAsync(HttpMethod.Get, $"Users/{userId}", ("If-None-Match", condition));
        var group = await _client.SendAsync(HttpMethod.Get, $"Groups/{groupId}", ("If-None-Match", condition));

        Assert.All([user, group], response => {
            Assert.Equal(HttpStatusCode.NotModified, response.Status);
            Assert.Empty(response.Text);
            Assert.Equal("W/\"1\"", ETag(response));
        });
    }

    [Fact]
    public async Task Get_with_If_None_Match_naming_an_older_version_answers_200_with_the_resource() {
        var id = await _client.CreateUserAsync("""{ "userName": "stale.read@example.com", "title": "Old" }""");
        await _client.PatchAsync($"Users/{id}", PatchOps.Replace("title", "\"New\""));

        var response = await _client.SendAsync(HttpMethod.Get, $"Users/{id}", ("If-None-Match", "W/\"1\""));

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal("New", (string?)response.Body["title"]);
        Assert.Equal("W/\"2\"", ETag(response));
    }

    [Theory]
    [InlineData("W/\"1\"")]
    [InlineData("\"1\"")]
    [InlineData("W/\"1\", W/\"3\"")]
    [InlineData("2")]
    public async Task If_Match_naming_another_version_fails_put_patch_and_delete_with_412_and_changes_nothing(string condition) {
        var userName = $"{Guid.NewGuid()}@example.com";
        var id = await _client.CreateUserAsync($$"""{ "userName": "{{userName}}", "title": "First" }""");
        await _client.PatchAsync($"Users/{id}", PatchOps.Replace("title", "\"Current\""));
        var ifMatch = ("If-Match", condition);

        var put = await _client.SendAsync(HttpMethod.Put, $"Users/{id}", ifMatch, $$"""{ "userName": "{{userName}}", "title": "Lost" }""");
        var patch = await _client.SendAsync(HttpMethod.Patch, $"Users/{id}", ifMatch, PatchOps.Replace("title", "\"Lost\""));
        var delete = await _client.SendAsync(HttpMethod.Delete, $"Users/{id}", ifMatch);

        foreach (var response in new[] { put, patch, delete }) {
            Assert.Equal(HttpStatusCode.PreconditionFailed, response.Status);
            Assert.Equal("application/scim+json", response.MediaType);
            Assert.Equal("412", (string?)response.Body["status"]);
            Assert.Contains("W/\"2\"", (string?)response.Body["detail"], StringComparison.Ordinal);
        }
        var user = await _client.GetAsync($"Users/{id}");
        Assert.Equal("Current", (string?)user.Body["title"]);
        Assert.Equal("W/\"2\"", Version(user));
    }

    [Theory]
    [InlineData("W/\"2\"")]
    [InlineData("\"2\"")]
    [InlineData("W/\"1\", W/\"2\"")]
    [InlineData("*")]
    public async Task If_Match_naming_the_current_version_lets_the_write_through(string condition) {
        var id = await _client.CreateUserAsync($$"""{ "userName": "{{Guid.NewGuid()}}@example.com", "title": "First" }""");
        await _client.PatchAsync($"Users/{id}", PatchOps.Replace("title", "\"Second\""));

        var patch = await _client.SendAsync(HttpMethod.Patch, $"Users/{id}", ("If-Match", condition), PatchOps.Replace("title", "\"Third\""));
        var delete = await _client.SendAsync(HttpMethod.Delete, $"Users/{id}", ("If-Match", "W/\"3\""));

        Assert.Equal(HttpStatusCode.OK, patch.Status);
        Assert.Equal("Third", (string?)patch.Body["title"]);
        Assert.Equal("W/\"3\"", ETag(patch));
        Assert.Equal(HttpStatusCode.NoContent, delete.Status);
    }

    [Fact]
    public async Task If_Match_star_names_any_version_but_not_a_resource_that_is_gone() {
        var id = await _client.CreateUserAsync("""{ "userName": "star@example.com" }""");
        var any = ("If-Match", "*");

        var put = await _client.SendAsync(HttpMethod.Put, $"Users/{id}", any, """{ "userName": "star@example.com", "title": "Starred" }""");
        var delete = await _client.SendAsync(HttpMethod.Delete, $"Users/{id}", any);
        var gone = await _client.SendAsync(HttpMethod.Delete, $"Users/{id}", any);

        Assert.Equal(HttpStatusCode.OK, put.Status);
        Assert.Equal(HttpStatusCode.NoContent, delete.Status);
        Assert.Equal(HttpStatusCode.NotFound, gone.Status);
    }

    [Fact]
    public async Task Groups_take_If_Match_as_users_do() {
        var groupId = await _client.CreateGroupAsync("""{ "displayName": "Conditional" }""");

        var stale = await _client.SendAsync(
            HttpMethod.Patch, $"Groups/{groupId}", ("If-Match", "W/\"0\""), PatchOps.Replace("displayName", "\"Lost\""));
        var current = await _client.SendAsync(
            HttpMethod.Patch, $"Groups/{groupId}", ("If-Match", "W/\"1\""), PatchOps.Replace("displayName", "\"Renamed\""));

        Assert.Equal(HttpStatusCode.PreconditionFailed, stale.Status);
        Assert.Equal(HttpStatusCode.NoContent, current.Status);
        Assert.Equal("Renamed", (string?)(await _client.GetAsync($"Groups/{groupId}")).Body["displayName"]);
    }

    private static string? Version(ScimResponse response) {
        return (string?)response.Body["meta"]!["version"];
    }

    private static string? ETag(ScimResponse response) {
        return response.Message.Headers.ETag?.ToString();
    }
}
