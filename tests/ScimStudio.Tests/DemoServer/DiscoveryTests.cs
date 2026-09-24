using System.Net;
using System.Text.Json.Nodes;
using ScimStudio.DemoServer;

namespace ScimStudio.Tests.DemoServer;

public sealed class DiscoveryTests(DemoServerFixture fixture) : IClassFixture<DemoServerFixture> {
    private const string ENTERPRISE = "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User";

    private readonly DemoScimServer _server = fixture.Server;
    private readonly ScimTestClient _client = fixture.Client;

    [Fact]
    public async Task ServiceProviderConfig_describes_what_the_server_supports() {
        var response = await _client.GetAsync("ServiceProviderConfig");

        Assert.Equal(HttpStatusCode.OK, response.Status);
        Assert.Equal("application/scim+json", response.MediaType);
        var config = response.Body;
        Assert.Equal("urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig", (string?)config["schemas"]![0]);
        Assert.True((bool)config["patch"]!["supported"]!);
        Assert.True((bool)config["bulk"]!["supported"]!);
        Assert.Equal(1000, (int)config["bulk"]!["maxOperations"]!);
        Assert.Equal(1048576, (int)config["bulk"]!["maxPayloadSize"]!);
        Assert.True((bool)config["filter"]!["supported"]!);
        Assert.Equal(200, (int)config["filter"]!["maxResults"]!);
        Assert.False((bool)config["changePassword"]!["supported"]!);
        Assert.True((bool)config["sort"]!["supported"]!);
        Assert.True((bool)config["etag"]!["supported"]!);
        var scheme = Assert.Single(config["authenticationSchemes"]!.AsArray())!;
        Assert.Equal("oauthbearertoken", (string?)scheme["type"]);
        Assert.Equal("Bearer token", (string?)scheme["name"]);
        Assert.True((bool)scheme["primary"]!);
        Assert.Equal("ServiceProviderConfig", (string?)config["meta"]!["resourceType"]);
        Assert.Equal($"{_server.BaseUrl}/ServiceProviderConfig", (string?)config["meta"]!["location"]);
    }

    [Fact]
    public async Task ResourceTypes_lists_users_with_the_enterprise_extension_and_groups() {
        var list = (await _client.GetAsync("ResourceTypes")).Body;

        Assert.Equal(2, (int)list["totalResults"]!);
        var resources = list["Resources"]!.AsArray();
        var user = resources[0]!;
        Assert.Equal("User", (string?)user["name"]);
        Assert.Equal("/Users", (string?)user["endpoint"]);
        Assert.Equal("urn:ietf:params:scim:schemas:core:2.0:User", (string?)user["schema"]);
        var extension = Assert.Single(user["schemaExtensions"]!.AsArray())!;
        Assert.Equal(ENTERPRISE, (string?)extension["schema"]);
        Assert.False((bool)extension["required"]!);
        Assert.Equal($"{_server.BaseUrl}/ResourceTypes/User", (string?)user["meta"]!["location"]);
        Assert.Equal("/Groups", (string?)resources[1]!["endpoint"]);
    }

    [Fact]
    public async Task ResourceType_is_found_by_name() {
        var group = await _client.GetAsync("ResourceTypes/Group");
        var unknown = await _client.GetAsync("ResourceTypes/Device");

        Assert.Equal(HttpStatusCode.OK, group.Status);
        Assert.Equal("urn:ietf:params:scim:schemas:core:2.0:Group", (string?)group.Body["schema"]);
        Assert.Equal(HttpStatusCode.NotFound, unknown.Status);
    }

    [Fact]
    public async Task Schemas_lists_user_enterprise_user_and_group() {
        var list = (await _client.GetAsync("Schemas")).Body;

        var ids = list["Resources"]!.AsArray().Select(schema => (string?)schema!["id"]);
        Assert.Equal(["urn:ietf:params:scim:schemas:core:2.0:User", ENTERPRISE, "urn:ietf:params:scim:schemas:core:2.0:Group"], ids);
        Assert.Equal(3, (int)list["totalResults"]!);
    }

    [Theory]
    [InlineData("ResourceTypes?filter=name eq \"User\"")]
    [InlineData("Schemas?filter=id pr&count=1")]
    public async Task Filter_on_the_list_of_resource_types_or_schemas_answers_403(string query) {
        var response = await _client.GetAsync(query);

        Assert.Equal(HttpStatusCode.Forbidden, response.Status);
        Assert.Equal("application/scim+json", response.MediaType);
        Assert.Equal("403", (string?)response.Body["status"]);
    }

    [Fact]
    public async Task Other_list_parameters_on_resource_types_and_schemas_are_ignored() {
        var types = (await _client.GetAsync("ResourceTypes?sortBy=name&sortOrder=descending&startIndex=2&count=1&filter=")).Body;
        var schemas = (await _client.GetAsync("Schemas?attributes=name&count=0")).Body;

        Assert.Equal(["User", "Group"], types["Resources"]!.AsArray().Select(type => (string?)type!["name"]));
        Assert.Equal(1, (int)types["startIndex"]!);
        Assert.Equal(3, (int)schemas["totalResults"]!);
        Assert.All(schemas["Resources"]!.AsArray(), schema => Assert.NotNull(schema!["attributes"]));
    }

    [Fact]
    public async Task Schema_describes_every_attribute_in_full() {
        var schema = (await _client.GetAsync("Schemas/urn:ietf:params:scim:schemas:core:2.0:User")).Body;

        Assert.Equal("urn:ietf:params:scim:schemas:core:2.0:Schema", (string?)schema["schemas"]![0]);
        Assert.Equal("User", (string?)schema["name"]);
        Assert.Equal($"{_server.BaseUrl}/Schemas/urn:ietf:params:scim:schemas:core:2.0:User", (string?)schema["meta"]!["location"]);
        var userName = Attribute(schema, "userName");
        Assert.True(JsonNode.DeepEquals(JsonNode.Parse("""
            {
              "name": "userName",
              "type": "string",
              "multiValued": false,
              "description": "Unique identifier of the user, typically what the user signs in with.",
              "required": true,
              "caseExact": false,
              "mutability": "readWrite",
              "returned": "default",
              "uniqueness": "server"
            }
            """), userName));
        var emails = Attribute(schema, "emails");
        Assert.Equal("complex", (string?)emails["type"]);
        Assert.True((bool)emails["multiValued"]!);
        var type = emails["subAttributes"]!.AsArray().Single(subAttribute => (string?)subAttribute!["name"] == "type")!;
        Assert.Equal(["work", "home", "other"], type["canonicalValues"]!.AsArray().Select(value => (string?)value));
        var groups = Attribute(schema, "groups");
        Assert.Equal("readOnly", (string?)groups["mutability"]);
        var reference = groups["subAttributes"]!.AsArray().Single(subAttribute => (string?)subAttribute!["name"] == "$ref")!;
        Assert.Equal("reference", (string?)reference["type"]);
        Assert.Equal(["User", "Group"], reference["referenceTypes"]!.AsArray().Select(value => (string?)value));
        Assert.Equal("boolean", (string?)Attribute(schema, "active")["type"]);
    }

    [Fact]
    public async Task Schema_of_groups_marks_member_values_immutable() {
        var schema = (await _client.GetAsync("Schemas/urn:ietf:params:scim:schemas:core:2.0:Group")).Body;

        var members = Attribute(schema, "members")["subAttributes"]!.AsArray();
        Assert.Equal("immutable", (string?)members.Single(subAttribute => (string?)subAttribute!["name"] == "value")!["mutability"]);
        Assert.Equal("readOnly", (string?)members.Single(subAttribute => (string?)subAttribute!["name"] == "display")!["mutability"]);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("Schemas/urn:example:unknown")).Status);
    }

    [Fact]
    public async Task Every_published_attribute_can_be_filtered_and_sorted() {
        // The schema documents come from the model filters resolve against, so each attribute they list must work in a filter
        foreach (var schema in (await _client.GetAsync("Schemas")).Body["Resources"]!.AsArray()) {
            var id = (string)schema!["id"]!;
            var endpoint = id.EndsWith(":Group", StringComparison.Ordinal) ? "Groups" : "Users";
            foreach (var attribute in schema["attributes"]!.AsArray()) {
                var path = $"{id}:{attribute!["name"]}";
                var filtered = await _client.GetAsync($"{endpoint}?filter={Uri.EscapeDataString(path + " pr")}");
                Assert.True(filtered.Status == HttpStatusCode.OK, $"filter on {path} answered {filtered.Status}: {filtered.Text}");
                var subAttributes = attribute["subAttributes"]?.AsArray() ?? [];
                if (subAttributes.Count == 0 || subAttributes.Any(subAttribute => (string?)subAttribute!["name"] == "value")) {
                    var sorted = await _client.GetAsync($"{endpoint}?sortBy={Uri.EscapeDataString(path)}");
                    Assert.True(sorted.Status == HttpStatusCode.OK, $"sortBy {path} answered {sorted.Status}: {sorted.Text}");
                }
            }
        }
    }

    private static JsonObject Attribute(JsonObject schema, string name) {
        return schema["attributes"]!.AsArray().Single(attribute => (string?)attribute!["name"] == name)!.AsObject();
    }
}
