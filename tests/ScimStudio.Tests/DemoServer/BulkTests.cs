using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScimStudio.DemoServer;

namespace ScimStudio.Tests.DemoServer;

public sealed class BulkTests(DemoServerFixture fixture) : IClassFixture<DemoServerFixture> {
    private readonly DemoScimServer _server = fixture.Server;
    private readonly ScimTestClient _client = fixture.Client;

    [Fact]
    public async Task Bulk_creates_a_user_and_a_group_that_takes_it_as_a_member_by_bulkId() {
        var response = await _client.PostAsync("Bulk", """
            {
              "schemas": ["urn:ietf:params:scim:api:messages:2.0:BulkRequest"],
              "Operations": [
                {
                  "method": "POST",
                  "path": "/Users",
                  "bulkId": "ann",
                  "data": { "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"], "userName": "ann.bulk@example.com" }
                },
                {
                  "method": "POST",
                  "path": "/Groups",
                  "bulkId": "team",
                  "data": { "displayName": "Bulk Team", "members": [{ "type": "User", "value": "bulkId:ann" }] }
                }
              ]
            }
            """);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal("application/scim+json", response.MediaType);
        Assert.Equal("urn:ietf:params:scim:api:messages:2.0:BulkResponse", (string?)response.Body["schemas"]![0]);
        var (user, team) = (Entry(response.Body, 0), Entry(response.Body, 1));
        var userId = (string)(await _client.GetAsync($"Users?filter=userName eq \"ann.bulk@example.com\"")).Body["Resources"]![0]!["id"]!;
        AssertSucceeded(user, "POST", "201", $"{_server.BaseUrl}/Users/{userId}", "W/\"1\"");
        Assert.Equal("ann", (string?)user["bulkId"]);
        Assert.Equal(JsonValueKind.String, user["status"]!.GetValueKind());
        Assert.Equal("team", (string?)team["bulkId"]);
        Assert.Equal("201", (string?)team["status"]);
        var group = (await _client.GetAsync((string)team["location"]!)).Body;
        Assert.Equal("Bulk Team", (string?)group["displayName"]);
        Assert.Equal(userId, (string?)Assert.Single(group["members"]!.AsArray())!["value"]);
    }

    [Fact]
    public async Task Put_patch_and_delete_answer_what_their_own_requests_would() {
        var id = await _client.CreateUserAsync("""{ "userName": "bob.bulk@example.com" }""");
        var groupId = await _client.CreateGroupAsync("""{ "displayName": "Bulk Patched" }""");

        var body = (await BulkAsync($$"""
            { "method": "PUT", "path": "/Users/{{id}}", "data": { "userName": "bob.bulk@example.com", "title": "Put" } },
            { "method": "patch", "path": "/Users/{{id}}", "data": {{PatchOps.Replace("title", "\"Patched\"")}} },
            { "method": "PATCH", "path": "/Groups/{{groupId}}", "version": "W/\"1\"", "data": {{PatchOps.Replace("displayName", "\"Renamed\"")}} },
            { "method": "DELETE", "path": "/Users/{{id}}", "version": "W/\"3\"" }
            """)).Body;

        var location = $"{_server.BaseUrl}/Users/{id}";
        AssertSucceeded(Entry(body, 0), "PUT", "200", location, "W/\"2\"");
        AssertSucceeded(Entry(body, 1), "PATCH", "200", location, "W/\"3\"");
        AssertSucceeded(Entry(body, 2), "PATCH", "204", $"{_server.BaseUrl}/Groups/{groupId}", "W/\"2\"");
        AssertSucceeded(Entry(body, 3), "DELETE", "204", location, null);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"Users/{id}")).Status);
        Assert.Equal("Renamed", (string?)(await _client.GetAsync($"Groups/{groupId}")).Body["displayName"]);
    }

    [Fact]
    public async Task Path_of_a_later_operation_can_name_a_resource_by_bulkId() {
        var body = (await BulkAsync("""
            { "method": "POST", "path": "/Users", "bulkId": "eve", "data": { "userName": "eve.bulk@example.com" } },
            { "method": "POST", "path": "/Groups", "bulkId": "crew", "data": { "displayName": "Crew" } },
            {
              "method": "PATCH",
              "path": "/Groups/bulkId:crew",
              "data": { "Operations": [{ "op": "add", "path": "members", "value": [{ "value": "bulkId:eve" }] }] }
            }
            """)).Body;

        var crew = (string)Entry(body, 1)["location"]!;
        AssertSucceeded(Entry(body, 2), "PATCH", "204", crew, "W/\"2\"");
        var members = (await _client.GetAsync(crew)).Body["members"]!.AsArray();
        Assert.Equal("eve.bulk@example.com", (string?)Assert.Single(members)!["display"]);
    }

    [Fact]
    public async Task Failed_operations_answer_with_their_error_and_the_rest_still_run() {
        var id = await _client.CreateUserAsync("""{ "userName": "carl.bulk@example.com" }""");

        var body = (await BulkAsync($$"""
            { "method": "POST", "path": "/Users", "bulkId": "nameless", "data": { "displayName": "No user name" } },
            { "method": "PUT", "path": "/Users/no-such-user", "data": { "userName": "ghost.bulk@example.com" } },
            { "method": "DELETE", "path": "/Users/{{id}}", "version": "W/\"7\"" },
            { "method": "POST", "path": "/Users", "bulkId": "dora", "data": { "userName": "dora.bulk@example.com" } }
            """)).Body;

        var nameless = Entry(body, 0);
        Assert.Equal("POST", (string?)nameless["method"]);
        Assert.Equal("nameless", (string?)nameless["bulkId"]);
        Assert.Equal("400", (string?)nameless["status"]);
        Assert.False(nameless.ContainsKey("location"));
        var error = nameless["response"]!;
        Assert.Equal("urn:ietf:params:scim:api:messages:2.0:Error", (string?)error["schemas"]![0]);
        Assert.Equal("400", (string?)error["status"]);
        Assert.Equal("invalidValue", (string?)error["scimType"]);
        Assert.NotNull((string?)error["detail"]);
        var missing = Entry(body, 1);
        Assert.Equal("404", (string?)missing["status"]);
        Assert.Equal($"{_server.BaseUrl}/Users/no-such-user", (string?)missing["location"]);
        Assert.Equal("404", (string?)missing["response"]!["status"]);
        var stale = Entry(body, 2);
        Assert.Equal("412", (string?)stale["status"]);
        Assert.Equal("412", (string?)stale["response"]!["status"]);
        Assert.False(stale.ContainsKey("version"));
        Assert.Equal("201", (string?)Entry(body, 3)["status"]);
        Assert.Equal(HttpStatusCode.OK, (await _client.GetAsync($"Users/{id}")).Status);
    }

    [Fact]
    public async Task Reference_to_a_bulkId_that_no_earlier_POST_created_fails_with_409() {
        var body = (await BulkAsync("""
            {
              "method": "POST",
              "path": "/Groups",
              "bulkId": "early",
              "data": { "displayName": "Too Early", "members": [{ "value": "bulkId:late" }] }
            },
            { "method": "POST", "path": "/Users", "bulkId": "late", "data": { "userName": "late.bulk@example.com" } },
            { "method": "DELETE", "path": "/Groups/bulkId:early" }
            """)).Body;

        var early = Entry(body, 0);
        Assert.Equal("409", (string?)early["status"]);
        Assert.Contains("\"late\"", (string?)early["response"]!["detail"], StringComparison.Ordinal);
        Assert.Equal("201", (string?)Entry(body, 1)["status"]);
        var delete = Entry(body, 2);
        Assert.Equal("409", (string?)delete["status"]);
        Assert.Contains("\"early\"", (string?)delete["response"]!["detail"], StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync("Groups", "displayName", "Too Early"));
    }

    [Fact]
    public async Task FailOnErrors_ends_the_request_after_that_many_errors() {
        var body = (await BulkAsync("""
            { "method": "DELETE", "path": "/Users/first-miss" },
            { "method": "POST", "path": "/Users", "bulkId": "kept", "data": { "userName": "kept.bulk@example.com" } },
            { "method": "DELETE", "path": "/Users/second-miss" },
            { "method": "POST", "path": "/Users", "bulkId": "skipped", "data": { "userName": "skipped.bulk@example.com" } }
            """, failOnErrors: 2)).Body;

        Assert.Equal(["404", "201", "404"], Statuses(body));
        Assert.Equal(1, await CountAsync("Users", "userName", "kept.bulk@example.com"));
        Assert.Equal(0, await CountAsync("Users", "userName", "skipped.bulk@example.com"));
    }

    [Fact]
    public async Task BulkId_is_taken_once_per_request() {
        var body = (await BulkAsync("""
            { "method": "POST", "path": "/Users", "bulkId": "twin", "data": { "userName": "twin.one@example.com" } },
            { "method": "POST", "path": "/Users", "bulkId": "twin", "data": { "userName": "twin.two@example.com" } }
            """)).Body;

        Assert.Equal(["201", "400"], Statuses(body));
        Assert.Equal(0, await CountAsync("Users", "userName", "twin.two@example.com"));
    }

    [Theory]
    [InlineData("""{ "method": "POST", "path": "/Users", "data": { "userName": "no.bulkid@example.com" } }""")]
    [InlineData("""{ "method": "GET", "path": "/Users/x" }""")]
    [InlineData("""{ "path": "/Users/x" }""")]
    [InlineData("""{ "method": "POST", "bulkId": "nowhere", "data": { "userName": "nowhere@example.com" } }""")]
    [InlineData("""{ "method": "POST", "path": "/Widgets", "bulkId": "widget", "data": {} }""")]
    [InlineData("""{ "method": "POST", "path": "/Users/x", "bulkId": "x", "data": { "userName": "x.bulk@example.com" } }""")]
    [InlineData("""{ "method": "DELETE", "path": "/Users" }""")]
    [InlineData("""{ "method": "DELETE", "path": "/Users/x/y" }""")]
    [InlineData("""{ "method": "PUT", "path": "/Users/x" }""")]
    [InlineData("""{ "method": "PATCH", "path": "/Users/x", "data": [] }""")]
    [InlineData("""{ "method": "DELETE", "path": "/Users/x", "version": 3 }""")]
    public async Task Operation_that_breaks_the_form_of_a_bulk_request_fails_with_400(string operation) {
        var body = (await BulkAsync(operation)).Body;

        var entry = Entry(body, 0);
        Assert.Equal("400", (string?)entry["status"]);
        Assert.Equal("invalidSyntax", (string?)entry["response"]!["scimType"]);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("""{ "Operations": { "method": "POST" } }""")]
    [InlineData("""{ "Operations": [1] }""")]
    [InlineData("""{ "Operations": [], "failOnErrors": "one" }""")]
    [InlineData("""{ "Operations": [], "failOnErrors": 0 }""")]
    [InlineData("[]")]
    [InlineData("{ not json")]
    public async Task Body_that_is_not_a_bulk_request_answers_400_invalidSyntax(string body) {
        var response = await _client.PostAsync("Bulk", body);

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal("invalidSyntax", response.ScimType);
    }

    [Fact]
    public async Task More_than_1000_operations_answer_413_and_none_of_them_runs() {
        var operations = Enumerable.Range(1, 1001).Select(number => $$"""
            { "method": "POST", "path": "/Users", "bulkId": "many{{number}}", "data": { "userName": "many{{number}}@example.com" } }
            """);

        var response = await BulkAsync(string.Join(',', operations));

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.Status);
        Assert.Equal("413", (string?)response.Body["status"]);
        var detail = (string)response.Body["detail"]!;
        Assert.Contains("maxOperations", detail, StringComparison.Ordinal);
        Assert.Contains("1000", detail, StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync("Users", "userName", "many1@example.com"));
    }

    [Fact]
    public async Task Exactly_1000_operations_all_run() {
        var response = await BulkAsync(string.Join(',', Enumerable.Repeat("""{ "method": "DELETE", "path": "/Groups/none" }""", 1000)));

        Assert.Equal(HttpStatusCode.OK, response.Status);
        var statuses = Statuses(response.Body);
        Assert.Equal(1000, statuses.Count);
        Assert.All(statuses, status => Assert.Equal("404", status));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Body_over_1048576_bytes_answers_413_whether_or_not_it_announces_its_length(bool chunked) {
        var title = new string('x', 1024 * 1024);
        var json = $$"""
            {
              "Operations": [
                { "method": "POST", "path": "/Users", "bulkId": "big", "data": { "userName": "big.bulk@example.com", "title": "{{title}}" } }
              ]
            }
            """;

        var response = chunked
            ? await _client.SendAsync(HttpMethod.Post, "Bulk", ("Transfer-Encoding", "chunked"), json)
            : await _client.PostAsync("Bulk", json);

        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.Status);
        var detail = (string)response.Body["detail"]!;
        Assert.Contains("maxPayloadSize", detail, StringComparison.Ordinal);
        Assert.Contains("1048576", detail, StringComparison.Ordinal);
        Assert.Equal(0, await CountAsync("Users", "userName", "big.bulk@example.com"));
    }

    private Task<ScimResponse> BulkAsync(string operations, int? failOnErrors = null) {
        var failOn = failOnErrors is null ? "" : $"\"failOnErrors\": {failOnErrors},";
        return _client.PostAsync("Bulk", $$"""
            {
              "schemas": ["urn:ietf:params:scim:api:messages:2.0:BulkRequest"],
              {{failOn}}
              "Operations": [{{operations}}]
            }
            """);
    }

    private async Task<int> CountAsync(string endpoint, string attribute, string value) {
        var list = (await _client.GetAsync($"{endpoint}?filter={Uri.EscapeDataString($"{attribute} eq \"{value}\"")}")).Body;
        return (int)list["totalResults"]!;
    }

    private static JsonObject Entry(JsonObject response, int index) {
        return response["Operations"]![index]!.AsObject();
    }

    private static List<string?> Statuses(JsonObject response) {
        return [.. response["Operations"]!.AsArray().Select(entry => (string?)entry!["status"])];
    }

    private static void AssertSucceeded(JsonObject entry, string method, string status, string location, string? version) {
        Assert.Equal(method, (string?)entry["method"]);
        Assert.Equal(status, (string?)entry["status"]);
        Assert.Equal(location, (string?)entry["location"]);
        Assert.Equal(version, (string?)entry["version"]);
        Assert.False(entry.ContainsKey("response"));
    }
}
