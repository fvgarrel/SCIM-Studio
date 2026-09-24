using System.Globalization;
using System.Net;
using System.Text.Json.Nodes;
using ScimStudio.DemoServer;

namespace ScimStudio.Tests.DemoServer;

public sealed class UserEndpointTests(DemoServerFixture fixture) : IClassFixture<DemoServerFixture> {
    private readonly DemoScimServer _server = fixture.Server;
    private readonly ScimTestClient _client = fixture.Client;

    [Fact]
    public async Task Request_without_a_token_is_refused_with_a_bearer_challenge() {
        using var anonymous = new HttpClient();
        using var response = await anonymous.GetAsync(new Uri(_server.BaseUrl + "/Users"), TestContext.Current.CancellationToken);
        var body = JsonNode.Parse(await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken))!;

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        Assert.Equal("application/scim+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("urn:ietf:params:scim:api:messages:2.0:Error", (string?)body["schemas"]![0]);
        Assert.Equal("401", (string?)body["status"]);
    }

    [Fact]
    public async Task Request_with_a_wrong_token_is_refused() {
        using var intruder = new ScimTestClient(_server, "not-the-token");

        var response = await intruder.GetAsync("ServiceProviderConfig");

        Assert.Equal(HttpStatusCode.Unauthorized, response.Status);
    }

    [Fact]
    public async Task Bearer_scheme_is_matched_regardless_of_case() {
        using var http = new HttpClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(_server.BaseUrl + "/ServiceProviderConfig"));
        request.Headers.TryAddWithoutValidation("Authorization", "bearer " + _server.Token);

        using var response = await http.SendAsync(request, TestContext.Current.CancellationToken);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Create_answers_201_with_location_and_the_full_resource() {
        var response = await _client.PostAsync("Users", """
            {
              "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
              "id": "client-chosen",
              "userName": "create@example.com",
              "name": { "givenName": "Cora", "familyName": "Reate" },
              "emails": [{ "value": "create@example.com", "type": "work", "primary": true }],
              "groups": [{ "value": "some-group" }],
              "meta": { "created": "2000-01-01T00:00:00Z" },
              "shoeSize": 42
            }
            """);

        Assert.Equal(HttpStatusCode.Created, response.Status);
        var user = response.Body;
        var id = (string)user["id"]!;
        Assert.True(Guid.TryParse(id, out _));
        Assert.Equal(new Uri($"{_server.BaseUrl}/Users/{id}"), response.Message.Headers.Location);
        Assert.Equal($"{_server.BaseUrl}/Users/{id}", (string?)user["meta"]!["location"]);
        Assert.Equal("User", (string?)user["meta"]!["resourceType"]);
        Assert.Equal((string?)user["meta"]!["created"], (string?)user["meta"]!["lastModified"]);
        Assert.True(DateTimeOffset.Parse((string)user["meta"]!["created"]!, CultureInfo.InvariantCulture) > DateTimeOffset.UtcNow.AddMinutes(-5));
        Assert.True((bool)user["active"]!);
        Assert.Equal("Cora", (string?)user["name"]!["givenName"]);
        Assert.False(user.ContainsKey("groups"));
        Assert.False(user.ContainsKey("shoeSize"));
        Assert.Equal(["urn:ietf:params:scim:schemas:core:2.0:User"], user["schemas"]!.AsArray().Select(schema => (string?)schema));
    }

    [Fact]
    public async Task Create_with_the_enterprise_extension_lists_it_in_schemas() {
        var response = await _client.PostAsync("Users", """
            {
              "userName": "extended@example.com",
              "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User": { "employeeNumber": "701", "department": "Tax" }
            }
            """);

        var user = response.Body;
        Assert.Contains("urn:ietf:params:scim:schemas:extension:enterprise:2.0:User", user["schemas"]!.AsArray().Select(schema => (string?)schema));
        Assert.Equal("Tax", (string?)user["urn:ietf:params:scim:schemas:extension:enterprise:2.0:User"]!["department"]);
    }

    [Fact]
    public async Task Create_accepts_booleans_sent_as_strings_and_plain_json_bodies() {
        const string BODY = """{ "userName": "strings@example.com", "active": "False" }""";

        var response = await _client.SendAsync(HttpMethod.Post, "Users", BODY, "application/json");

        Assert.Equal(HttpStatusCode.Created, response.Status);
        Assert.False((bool)response.Body["active"]!);
    }

    [Fact]
    public async Task Create_without_userName_fails_with_invalidValue() {
        var response = await _client.PostAsync("Users", """{ "displayName": "Nobody" }""");

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal("invalidValue", response.ScimType);
        Assert.Equal("400", (string?)response.Body["status"]);
    }

    [Fact]
    public async Task Create_with_a_taken_userName_in_other_case_fails_with_409_uniqueness() {
        await _client.CreateUserAsync("""{ "userName": "taken@example.com" }""");

        var response = await _client.PostAsync("Users", """{ "userName": "TAKEN@example.com" }""");

        Assert.Equal(HttpStatusCode.Conflict, response.Status);
        Assert.Equal("uniqueness", response.ScimType);
    }

    [Theory]
    [InlineData("[1, 2]")]
    [InlineData("\"just a string\"")]
    [InlineData("{ not json")]
    [InlineData("")]
    [InlineData("""{ "userName": "a@example.com", "userName": "b@example.com" }""")]
    public async Task Body_that_is_not_a_json_object_fails_with_invalidSyntax(string body) {
        var response = await _client.PostAsync("Users", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal("invalidSyntax", response.ScimType);
    }

    [Fact]
    public async Task Get_answers_the_resource_with_its_content_location() {
        var id = await _client.CreateUserAsync("""{ "userName": "get@example.com", "displayName": "Gert" }""");

        var response = await _client.GetAsync($"Users/{id}");

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal("application/scim+json", response.MediaType);
        Assert.Equal("utf-8", response.CharSet);
        Assert.Equal(new Uri($"{_server.BaseUrl}/Users/{id}"), response.Message.Content.Headers.ContentLocation);
        Assert.Equal(new Uri($"{_server.BaseUrl}/Users/{id}"), response.Message.Headers.Location);
        Assert.Equal("Gert", (string?)response.Body["displayName"]);
    }

    [Fact]
    public async Task Get_trims_the_resource_to_the_requested_attributes() {
        var id = await _client.CreateUserAsync("""{ "userName": "trim@example.com", "displayName": "Trim", "title": "Barber" }""");

        var only = (await _client.GetAsync($"Users/{id}?attributes=displayName")).Body;
        var without = (await _client.GetAsync($"Users/{id}?excludedAttributes=title,meta,id")).Body;

        Assert.Equal(["schemas", "id", "displayName"], only.Select(property => property.Key));
        Assert.True(without.ContainsKey("id"));
        Assert.False(without.ContainsKey("title"));
        Assert.False(without.ContainsKey("meta"));
        Assert.True(without.ContainsKey("displayName"));
    }

    [Fact]
    public async Task Unknown_user_answers_404() {
        var response = await _client.GetAsync("Users/does-not-exist");

        Assert.Equal(HttpStatusCode.NotFound, response.Status);
        Assert.Equal("404", (string?)response.Body["status"]);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PutAsync("Users/does-not-exist", """{ "userName": "x@example.com" }""")).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.PatchAsync("Users/does-not-exist", PatchOps.Replace("title", "\"x\""))).Status);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync("Users/does-not-exist")).Status);
    }

    [Fact]
    public async Task Put_replaces_every_writable_attribute_and_keeps_id_and_created() {
        var created = (await _client.PostAsync("Users", """
            { "userName": "put@example.com", "title": "Before", "nickName": "Puddy", "active": false }
            """)).Body;
        var id = (string)created["id"]!;

        var response = await _client.PutAsync($"Users/{id}", """{ "id": "ignored", "userName": "put@example.com", "title": "After" }""");

        Assert.Equal(HttpStatusCode.OK, response.Status);
        var user = response.Body;
        Assert.Equal(id, (string?)user["id"]);
        Assert.Equal("After", (string?)user["title"]);
        Assert.False(user.ContainsKey("nickName"));
        Assert.True((bool)user["active"]!);
        Assert.Equal((string?)created["meta"]!["created"], (string?)user["meta"]!["created"]);
        Assert.True(Instant(user, "lastModified") > Instant(created, "lastModified"));
    }

    [Fact]
    public async Task Put_enforces_required_and_unique_userName() {
        await _client.CreateUserAsync("""{ "userName": "put.first@example.com" }""");
        var id = await _client.CreateUserAsync("""{ "userName": "put.second@example.com" }""");

        var missing = await _client.PutAsync($"Users/{id}", """{ "displayName": "No user name" }""");
        var taken = await _client.PutAsync($"Users/{id}", """{ "userName": "Put.First@example.com" }""");
        var same = await _client.PutAsync($"Users/{id}", """{ "userName": "PUT.SECOND@example.com" }""");

        Assert.Equal("invalidValue", missing.ScimType);
        Assert.Equal(HttpStatusCode.Conflict, taken.Status);
        Assert.Equal(HttpStatusCode.OK, same.Status);
    }

    [Fact]
    public async Task Patch_answers_200_with_the_resource_and_touches_lastModified_only_on_change() {
        var created = (await _client.PostAsync("Users", """{ "userName": "patch@example.com", "title": "Old" }""")).Body;
        var id = (string)created["id"]!;

        var unchanged = await _client.PatchAsync($"Users/{id}", PatchOps.Replace("title", "\"Old\""));
        var changed = await _client.PatchAsync($"Users/{id}?attributes=title,meta.lastModified", PatchOps.Replace("title", "\"New\""));

        Assert.Equal(HttpStatusCode.OK, unchanged.Status);
        Assert.Equal((string?)created["meta"]!["lastModified"], (string?)unchanged.Body["meta"]!["lastModified"]);
        Assert.Equal(HttpStatusCode.OK, changed.Status);
        Assert.Equal("New", (string?)changed.Body["title"]);
        Assert.True(Instant(changed.Body, "lastModified") > Instant(created, "lastModified"));
        Assert.False(changed.Body.ContainsKey("userName"));
    }

    [Fact]
    public async Task Failed_patch_changes_nothing() {
        var id = await _client.CreateUserAsync("""{ "userName": "atomic@example.com", "title": "Kept" }""");

        var response = await _client.PatchAsync($"Users/{id}", """
            {
              "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
              "Operations": [
                { "op": "replace", "path": "title", "value": "Lost" },
                { "op": "replace", "path": "id", "value": "other" }
              ]
            }
            """);

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal("mutability", response.ScimType);
        Assert.Equal("Kept", (string?)(await _client.GetAsync($"Users/{id}")).Body["title"]);
    }

    [Fact]
    public async Task Patch_that_takes_a_userName_fails_with_409() {
        await _client.CreateUserAsync("""{ "userName": "patch.first@example.com" }""");
        var id = await _client.CreateUserAsync("""{ "userName": "patch.second@example.com" }""");

        var response = await _client.PatchAsync($"Users/{id}", PatchOps.Replace("userName", "\"PATCH.FIRST@example.com\""));

        Assert.Equal(HttpStatusCode.Conflict, response.Status);
        Assert.Equal("uniqueness", response.ScimType);
    }

    [Fact]
    public async Task Delete_answers_204_and_the_user_is_gone() {
        var id = await _client.CreateUserAsync("""{ "userName": "delete@example.com" }""");

        var response = await _client.DeleteAsync($"Users/{id}");

        Assert.Equal(HttpStatusCode.NoContent, response.Status);
        Assert.Empty(response.Text);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"Users/{id}")).Status);
    }

    [Theory]
    [InlineData("GET", "Bulk")]
    [InlineData("PUT", "Bulk")]
    [InlineData("GET", "Me")]
    [InlineData("PATCH", "Me")]
    public async Task Me_and_methods_other_than_POST_on_Bulk_answer_501(string method, string path) {
        var response = await _client.SendAsync(new HttpMethod(method), path, method == "GET" ? null : "{}");

        Assert.Equal(HttpStatusCode.NotImplemented, response.Status);
        Assert.Equal("501", (string?)response.Body["status"]);
    }

    [Fact]
    public async Task Unknown_path_answers_404_as_a_scim_error() {
        var response = await _client.GetAsync("Widgets/1.json");

        Assert.Equal(HttpStatusCode.NotFound, response.Status);
        Assert.Equal("application/scim+json", response.MediaType);
        Assert.Equal("404", (string?)response.Body["status"]);
    }

    [Fact]
    public async Task Unsupported_method_answers_405_as_a_scim_error() {
        var response = await _client.DeleteAsync("Users");

        Assert.Equal(HttpStatusCode.MethodNotAllowed, response.Status);
        Assert.Equal("405", (string?)response.Body["status"]);
    }

    private static DateTimeOffset Instant(JsonObject resource, string name) {
        return DateTimeOffset.Parse((string)resource["meta"]![name]!, CultureInfo.InvariantCulture);
    }
}
