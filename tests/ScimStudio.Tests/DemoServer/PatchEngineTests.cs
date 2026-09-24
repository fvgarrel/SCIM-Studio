using System.Text.Json.Nodes;
using ScimStudio.DemoServer;
using ScimStudio.DemoServer.Patching;
using ScimStudio.DemoServer.Schema;

namespace ScimStudio.Tests.DemoServer;

public sealed class PatchEngineTests {
    private const string ENTERPRISE = "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User";

    private const string USER = """
        {
          "id": "u-1",
          "userName": "ann@example.com",
          "name": { "givenName": "Ann", "familyName": "Smith" },
          "displayName": "Ann Smith",
          "active": true,
          "emails": [
            { "value": "ann@example.com", "type": "work", "primary": true },
            { "value": "ann@home.example.org", "type": "home" }
          ],
          "meta": { "resourceType": "User", "created": "2026-03-01T10:00:00Z", "lastModified": "2026-03-01T10:00:00Z" }
        }
        """;

    private const string GROUP = """
        { "id": "g-1", "displayName": "Sales", "members": [{ "value": "u-1" }, { "value": "u-2" }, { "value": "u-3" }] }
        """;

    [Fact]
    public void Replace_with_a_path_sets_an_attribute() {
        var user = PatchUser("""{ "op": "replace", "path": "displayName", "value": "Ann Jones" }""");

        Assert.Equal("Ann Jones", (string?)user["displayName"]);
    }

    [Fact]
    public void Add_with_a_sub_attribute_path_sets_only_that_sub_attribute() {
        var user = PatchUser("""{ "op": "add", "path": "name.middleName", "value": "Marie" }""");

        Assert.Equal("Marie", (string?)user["name"]!["middleName"]);
        Assert.Equal("Ann", (string?)user["name"]!["givenName"]);
    }

    [Fact]
    public void Replace_of_a_complex_attribute_merges_the_given_sub_attributes() {
        var user = PatchUser("""{ "op": "replace", "path": "name", "value": { "familyName": "Jones" } }""");

        Assert.Equal("Jones", (string?)user["name"]!["familyName"]);
        Assert.Equal("Ann", (string?)user["name"]!["givenName"]);
    }

    [Fact]
    public void Urn_prefixed_path_writes_into_the_extension() {
        var user = PatchUser($$"""{ "op": "replace", "path": "{{ENTERPRISE}}:department", "value": "Sales" }""");

        Assert.Equal("Sales", (string?)user[ENTERPRISE]!["department"]);
    }

    [Fact]
    public void Manager_sent_as_a_bare_id_becomes_its_value() {
        var user = PatchUser($$"""{ "op": "Add", "path": "{{ENTERPRISE}}:manager", "value": "u-9" }""");

        Assert.Equal("u-9", (string?)user[ENTERPRISE]!["manager"]!["value"]);
    }

    [Fact]
    public void Add_appends_to_a_multi_valued_attribute_without_duplicating_a_value() {
        var user = PatchUser("""
            {
              "op": "add",
              "path": "emails",
              "value": [{ "value": "ANN@EXAMPLE.COM", "type": "other" }, { "value": "ann@work.example.net", "type": "work" }]
            }
            """);

        var emails = user["emails"]!.AsArray();
        Assert.Equal(3, emails.Count);
        Assert.Equal("other", (string?)emails[0]!["type"]);
        Assert.Equal("ann@work.example.net", (string?)emails[2]!["value"]);
    }

    [Fact]
    public void Replace_of_a_multi_valued_attribute_replaces_the_whole_list() {
        var user = PatchUser("""{ "op": "replace", "path": "emails", "value": [{ "value": "new@example.com" }] }""");

        var email = Assert.Single(user["emails"]!.AsArray());
        Assert.Equal("new@example.com", (string?)email!["value"]);
    }

    [Fact]
    public void Filtered_path_with_a_sub_attribute_changes_only_matching_elements() {
        var user = PatchUser("""{ "op": "replace", "path": "emails[type eq \"work\"].value", "value": "ann@new.example.com" }""");

        var emails = user["emails"]!.AsArray();
        Assert.Equal("ann@new.example.com", (string?)emails[0]!["value"]);
        Assert.Equal("ann@home.example.org", (string?)emails[1]!["value"]);
    }

    [Fact]
    public void Filtered_path_without_a_match_creates_the_element_from_its_equalities() {
        var user = PatchUser("""{ "op": "Replace", "path": "phoneNumbers[type eq \"mobile\"].value", "value": "+49 170 5550100" }""");

        var phone = Assert.Single(user["phoneNumbers"]!.AsArray());
        Assert.Equal("mobile", (string?)phone!["type"]);
        Assert.Equal("+49 170 5550100", (string?)phone["value"]);
    }

    [Fact]
    public void Filtered_path_that_matches_nothing_and_describes_no_element_fails_with_noTarget() {
        var error = Assert.Throws<ScimException>(() => PatchUser(
            """{ "op": "replace", "path": "emails[value co \"@nowhere\"].type", "value": "other" }"""));

        Assert.Equal(ScimException.NO_TARGET, error.ScimType);
    }

    [Fact]
    public void Setting_primary_on_one_element_takes_it_from_the_others() {
        var user = PatchUser("""{ "op": "replace", "path": "emails[type eq \"home\"].primary", "value": true }""");

        var emails = user["emails"]!.AsArray();
        Assert.False((bool)emails[0]!["primary"]!);
        Assert.True((bool)emails[1]!["primary"]!);
    }

    [Fact]
    public void Remove_with_a_filter_removes_the_matching_members() {
        var group = PatchGroup("""{ "op": "remove", "path": "members[value eq \"u-2\"]" }""");

        Assert.Equal(["u-1", "u-3"], MemberIds(group));
    }

    [Fact]
    public void Remove_of_members_with_a_value_list_removes_only_those() {
        var group = PatchGroup("""{ "op": "Remove", "path": "members", "value": [{ "value": "u-1" }, { "value": "u-3" }] }""");

        Assert.Equal(["u-2"], MemberIds(group));
    }

    [Fact]
    public void Remove_of_members_without_a_value_removes_them_all() {
        var group = PatchGroup("""{ "op": "remove", "path": "members" }""");

        Assert.False(group.ContainsKey("members"));
    }

    [Fact]
    public void Add_of_members_skips_read_only_sub_attributes() {
        var group = PatchGroup("""{ "op": "add", "path": "members", "value": [{ "value": "u-4", "display": "Dan" }] }""");

        var added = group["members"]!.AsArray()[3]!.AsObject();
        Assert.Equal("u-4", (string?)added["value"]);
        Assert.False(added.ContainsKey("display"));
    }

    [Fact]
    public void Changing_the_value_of_a_member_fails_with_mutability() {
        var error = Assert.Throws<ScimException>(() => PatchGroup(
            """{ "op": "replace", "path": "members[value eq \"u-1\"].value", "value": "u-9" }"""));

        Assert.Equal(ScimException.MUTABILITY, error.ScimType);
    }

    [Fact]
    public void Remove_without_a_path_fails_with_noTarget() {
        var error = Assert.Throws<ScimException>(() => PatchUser("""{ "op": "remove" }"""));

        Assert.Equal(ScimException.NO_TARGET, error.ScimType);
    }

    [Fact]
    public void Capitalized_ops_and_keys_are_accepted() {
        var user = Patch(ResourceType.User, USER, """
            {
              "Schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
              "operations": [
                { "OP": "Replace", "Path": "title", "Value": "Engineer" },
                { "op": "ADD", "path": "nickName", "value": "Annie" },
                { "op": "Remove", "path": "displayName" }
              ]
            }
            """);

        Assert.Equal("Engineer", (string?)user["title"]);
        Assert.Equal("Annie", (string?)user["nickName"]);
        Assert.False(user.ContainsKey("displayName"));
    }

    [Theory]
    [InlineData("\"False\"", false)]
    [InlineData("\"True\"", true)]
    [InlineData("\"false\"", false)]
    [InlineData("false", false)]
    public void Boolean_sent_as_a_string_becomes_a_boolean(string value, bool expected) {
        var user = PatchUser($$"""{ "op": "Replace", "path": "active", "value": {{value}} }""");

        Assert.Equal(expected, (bool)user["active"]!);
    }

    [Fact]
    public void Boolean_attribute_refuses_other_strings_with_invalidValue() {
        var error = Assert.Throws<ScimException>(() => PatchUser("""{ "op": "replace", "path": "active", "value": "maybe" }"""));

        Assert.Equal(ScimException.INVALID_VALUE, error.ScimType);
    }

    [Fact]
    public void Value_without_a_path_takes_dotted_and_urn_prefixed_keys() {
        var user = PatchUser($$"""
            {
              "op": "Replace",
              "value": {
                "name.givenName": "Anna",
                "active": "False",
                "{{ENTERPRISE}}:department": "Finance",
                "emails[type eq \"work\"].value": "anna@example.com"
              }
            }
            """);

        Assert.Equal("Anna", (string?)user["name"]!["givenName"]);
        Assert.False((bool)user["active"]!);
        Assert.Equal("Finance", (string?)user[ENTERPRISE]!["department"]);
        Assert.Equal("anna@example.com", (string?)user["emails"]![0]!["value"]);
    }

    [Fact]
    public void Value_without_a_path_takes_the_extension_urn_as_a_key() {
        var user = PatchUser($$"""{ "op": "add", "value": { "{{ENTERPRISE}}": { "employeeNumber": "42", "costCenter": "CC-1" } } }""");

        Assert.Equal("42", (string?)user[ENTERPRISE]!["employeeNumber"]);
        Assert.Equal("CC-1", (string?)user[ENTERPRISE]!["costCenter"]);
    }

    [Fact]
    public void Value_without_a_path_skips_read_only_and_unknown_attributes() {
        var group = PatchGroup("""
            { "op": "replace", "value": { "id": "g-other", "displayName": "Sales EMEA", "meta": {}, "customThing": 1 } }
            """);

        Assert.Equal("g-1", (string?)group["id"]);
        Assert.Equal("Sales EMEA", (string?)group["displayName"]);
        Assert.False(group.ContainsKey("customThing"));
    }

    [Theory]
    [InlineData("id")]
    [InlineData("meta.lastModified")]
    [InlineData("groups")]
    public void Path_to_a_read_only_attribute_fails_with_mutability(string path) {
        var error = Assert.Throws<ScimException>(() => PatchUser($$"""{ "op": "replace", "path": "{{path}}", "value": "x" }"""));

        Assert.Equal(ScimException.MUTABILITY, error.ScimType);
    }

    [Fact]
    public void Removing_a_required_attribute_fails_with_mutability() {
        var error = Assert.Throws<ScimException>(() => PatchUser("""{ "op": "remove", "path": "userName" }"""));

        Assert.Equal(ScimException.MUTABILITY, error.ScimType);
    }

    [Theory]
    [InlineData("favoriteColor")]
    [InlineData("name.nickname")]
    [InlineData("urn:example:custom:2.0:User:shoeSize")]
    public void Path_to_an_unknown_attribute_is_ignored(string path) {
        var user = PatchUser($$"""{ "op": "replace", "path": "{{path}}", "value": "x" }""");

        Assert.True(JsonNode.DeepEquals(JsonNode.Parse(USER), user));
    }

    [Theory]
    [InlineData("emails[type eq \\\"work\\\"")]
    [InlineData("emails[type eq]")]
    [InlineData("name..givenName")]
    [InlineData("name.givenName.first")]
    [InlineData("display name")]
    [InlineData("emails[type eq \\\"work\\\"]value")]
    [InlineData("name[givenName eq \\\"Ann\\\"]")]
    public void Malformed_path_fails_with_invalidPath(string path) {
        var error = Assert.Throws<ScimException>(() => PatchUser($$"""{ "op": "replace", "path": "{{path}}", "value": "x" }"""));

        Assert.Equal(ScimException.INVALID_PATH, error.ScimType);
    }

    [Theory]
    [InlineData("""{ "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchOp"] }""")]
    [InlineData("""{ "Operations": [] }""")]
    [InlineData("""{ "Operations": [{ "op": "move", "path": "title" }] }""")]
    [InlineData("""{ "Operations": ["replace"] }""")]
    public void Request_without_valid_operations_fails_with_invalidSyntax(string body) {
        var error = Assert.Throws<ScimException>(() => PatchOperation.ReadAll(JsonNode.Parse(body)!.AsObject()));

        Assert.Equal(ScimException.INVALID_SYNTAX, error.ScimType);
    }

    [Fact]
    public void Remove_of_the_extension_urn_removes_the_extension() {
        var user = Patch(ResourceType.User, USER, $$"""
            {
              "Operations": [
                { "op": "add", "path": "{{ENTERPRISE}}:department", "value": "Sales" },
                { "op": "remove", "path": "{{ENTERPRISE}}" }
              ]
            }
            """);

        Assert.False(user.ContainsKey(ENTERPRISE));
    }

    private static JsonObject PatchUser(string operation) {
        return Patch(ResourceType.User, USER, $$"""{ "Operations": [{{operation}}] }""");
    }

    private static JsonObject PatchGroup(string operation) {
        return Patch(ResourceType.Group, GROUP, $$"""{ "Operations": [{{operation}}] }""");
    }

    private static JsonObject Patch(ResourceType type, string resource, string request) {
        var patched = JsonNode.Parse(resource)!.AsObject();
        PatchEngine.Apply(type, patched, PatchOperation.ReadAll(JsonNode.Parse(request)!.AsObject()));
        return patched;
    }

    private static List<string?> MemberIds(JsonObject group) {
        return [.. group["members"]!.AsArray().Select(member => (string?)member!["value"])];
    }
}
