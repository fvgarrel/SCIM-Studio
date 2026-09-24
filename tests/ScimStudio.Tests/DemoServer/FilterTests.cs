using System.Text.Json.Nodes;
using ScimStudio.DemoServer;
using ScimStudio.DemoServer.Filtering;
using ScimStudio.DemoServer.Schema;

namespace ScimStudio.Tests.DemoServer;

public sealed class FilterTests {
    private static readonly JsonObject Ann = JsonNode.Parse("""
        {
          "schemas": ["urn:ietf:params:scim:schemas:core:2.0:User"],
          "id": "2819c223-7f76-453a-919d-413861904646",
          "externalId": "Ext-1",
          "userName": "ann.smith@example.com",
          "name": { "givenName": "Ann", "familyName": "Smith" },
          "displayName": "Ann \"The Hammer\" Smith",
          "title": "Engineer",
          "active": true,
          "emails": [
            { "value": "ann@example.com", "type": "work", "primary": true },
            { "value": "ann@home.example.org", "type": "home" }
          ],
          "groups": [{ "value": "g-1", "display": "Engineering", "type": "direct" }],
          "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User": { "department": "Engineering" },
          "meta": { "resourceType": "User", "created": "2026-03-01T10:00:00Z", "lastModified": "2026-06-01T10:00:00Z" }
        }
        """)!.AsObject();

    [Theory]
    [InlineData("userName eq \"ann.smith@example.com\"", true)]
    [InlineData("userName eq \"bob@example.com\"", false)]
    [InlineData("userName ne \"bob@example.com\"", true)]
    [InlineData("userName ne \"ann.smith@example.com\"", false)]
    [InlineData("userName co \"smith@\"", true)]
    [InlineData("userName co \"jones\"", false)]
    [InlineData("userName sw \"ann.\"", true)]
    [InlineData("userName sw \"smith\"", false)]
    [InlineData("userName ew \"@example.com\"", true)]
    [InlineData("userName ew \"ann\"", false)]
    [InlineData("userName gt \"ann\"", true)]
    [InlineData("userName gt \"bob\"", false)]
    [InlineData("userName ge \"ann.smith@example.com\"", true)]
    [InlineData("userName lt \"bob\"", true)]
    [InlineData("userName lt \"ann\"", false)]
    [InlineData("userName le \"ann.smith@example.com\"", true)]
    [InlineData("title pr", true)]
    [InlineData("nickName pr", false)]
    public void Filter_supports_every_operator(string filter, bool expected) {
        Assert.Equal(expected, Matches(filter));
    }

    [Theory]
    [InlineData("userName eq \"ANN.SMITH@EXAMPLE.COM\"", true)]
    [InlineData("emails.value eq \"ANN@EXAMPLE.COM\"", true)]
    [InlineData("externalId eq \"ext-1\"", false)]
    [InlineData("externalId eq \"Ext-1\"", true)]
    [InlineData("id eq \"2819C223-7F76-453A-919D-413861904646\"", false)]
    public void Filter_honours_caseExact_of_the_schema(string filter, bool expected) {
        Assert.Equal(expected, Matches(filter));
    }

    [Theory]
    [InlineData("USERNAME EQ \"ann.smith@example.com\"")]
    [InlineData("name.FAMILYNAME Eq \"Smith\"")]
    [InlineData("userName sw \"ann\" AND title PR")]
    [InlineData("NOT (active eq false)")]
    [InlineData("urn:ietf:params:scim:schemas:core:2.0:User:userName sw \"ann\"")]
    [InlineData("URN:IETF:PARAMS:SCIM:SCHEMAS:EXTENSION:ENTERPRISE:2.0:USER:department eq \"Engineering\"")]
    public void Filter_ignores_the_case_of_names_operators_and_keywords(string filter) {
        Assert.True(Matches(filter));
    }

    [Theory]
    [InlineData("emails co \"home.example\"", true)]
    [InlineData("emails.type eq \"home\"", true)]
    [InlineData("emails.value eq \"ann@home.example.org\"", true)]
    [InlineData("emails.type eq \"other\"", false)]
    [InlineData("emails ne \"ann@example.com\"", false)]
    [InlineData("groups eq \"g-1\"", true)]
    [InlineData("groups.display eq \"engineering\"", true)]
    public void Filter_matches_any_email(string filter, bool expected) {
        Assert.Equal(expected, Matches(filter));
    }

    [Theory]
    [InlineData("emails[type eq \"work\" and value co \"@example.com\"]", true)]
    [InlineData("emails[type eq \"home\" and value co \"@example.com\"]", false)]
    [InlineData("emails[primary eq true]", true)]
    [InlineData("emails[not (type eq \"work\")]", true)]
    [InlineData("emails[type eq \"other\"] or title eq \"Engineer\"", true)]
    [InlineData("name[givenName eq \"Ann\"]", true)]
    public void Filter_with_a_value_path_needs_one_element_to_match_the_whole_inner_filter(string filter, bool expected) {
        Assert.Equal(expected, Matches(filter));
    }

    [Theory]
    [InlineData("emails[type eq \"work\"].value eq \"ann@example.com\"", true)]
    [InlineData("emails[type eq \"work\"].value eq \"ann@home.example.org\"", false)]
    [InlineData("emails[type eq \"home\"].value co \"home.example\"", true)]
    [InlineData("emails[type eq \"other\"].value pr", false)]
    [InlineData("emails[type eq \"home\"].primary pr", false)]
    public void Filter_takes_a_sub_attribute_after_the_brackets_as_Entra_ID_sends_it(string filter, bool expected) {
        Assert.Equal(expected, Matches(filter));
    }

    [Theory]
    [InlineData("schemas eq \"urn:ietf:params:scim:schemas:core:2.0:User\"", true)]
    [InlineData("SCHEMAS eq \"URN:IETF:PARAMS:SCIM:SCHEMAS:CORE:2.0:USER\"", true)]
    [InlineData("schemas eq \"urn:ietf:params:scim:schemas:extension:enterprise:2.0:User\"", false)]
    public void Filter_queries_by_schema(string filter, bool expected) {
        Assert.Equal(expected, Matches(filter));
    }

    [Theory]
    [InlineData("meta.created gt \"2026-02-28T00:00:00Z\"", true)]
    [InlineData("meta.created lt \"2026-02-28T00:00:00Z\"", false)]
    [InlineData("meta.created eq \"2026-03-01T12:00:00+02:00\"", true)]
    [InlineData("meta.created ge \"2026-03-01T10:00:00Z\"", true)]
    [InlineData("meta.created le \"2026-03-01T09:59:59Z\"", false)]
    [InlineData("meta.lastModified gt \"2026-05-31\"", true)]
    public void Filter_compares_dates_as_instants(string filter, bool expected) {
        Assert.Equal(expected, Matches(filter));
    }

    [Theory]
    [InlineData("active eq true", true)]
    [InlineData("active eq false", false)]
    [InlineData("active ne false", true)]
    [InlineData("nickName eq null", true)]
    [InlineData("title eq null", false)]
    [InlineData("title ne null", true)]
    [InlineData("displayName eq \"Ann \\\"The Hammer\\\" Smith\"", true)]
    public void Filter_compares_booleans_nulls_and_escaped_strings(string filter, bool expected) {
        Assert.Equal(expected, Matches(filter));
    }

    [Theory]
    [InlineData("userName sw \"ann\" or active eq false and title eq \"Nobody\"", true)]
    [InlineData("active eq false and title eq \"Engineer\" or userName sw \"ann\"", true)]
    [InlineData("not (userName sw \"ann\") or title eq \"Engineer\"", true)]
    [InlineData("not (userName sw \"ann\" or title eq \"Engineer\")", false)]
    [InlineData("(userName sw \"ann\" or active eq false) and title eq \"Nobody\"", false)]
    public void Filter_binds_not_before_and_before_or(string filter, bool expected) {
        Assert.Equal(expected, Matches(filter));
    }

    [Fact]
    public void Filter_parses_or_of_ands_into_a_tree() {
        var filter = FilterParser.Parse("title pr or userName pr and active eq true", ResourceType.User);

        var or = Assert.IsType<OrFilter>(filter);
        Assert.IsType<PresentFilter>(or.Left);
        Assert.IsType<AndFilter>(or.Right);
    }

    [Fact]
    public void Filter_on_groups_resolves_against_the_group_schema() {
        var group = JsonNode.Parse("""{ "displayName": "Sales", "members": [{ "value": "u-1" }, { "value": "u-2" }] }""")!.AsObject();

        Assert.True(FilterParser.Parse("members[value eq \"u-2\"]", ResourceType.Group).Matches(group));
        Assert.True(FilterParser.Parse("displayName eq \"sales\"", ResourceType.Group).Matches(group));
        Assert.Throws<ScimException>(() => FilterParser.Parse("userName eq \"x\"", ResourceType.Group));
    }

    [Theory]
    [InlineData("")]
    [InlineData("userName")]
    [InlineData("userName eq")]
    [InlineData("userName eq \"unterminated")]
    [InlineData("userName eq \"bad \\q escape\"")]
    [InlineData("userName xx \"a\"")]
    [InlineData("userName eq ann")]
    [InlineData("(userName eq \"a\"")]
    [InlineData("userName eq \"a\")")]
    [InlineData("userName eq \"a\" and")]
    [InlineData("not userName eq \"a\"")]
    [InlineData("emails[type eq \"work\"")]
    [InlineData("emails[type[value eq \"x\"] eq \"y\"]")]
    [InlineData("userName[value eq \"x\"]")]
    [InlineData("unknownAttribute eq \"a\"")]
    [InlineData("name.unknown eq \"a\"")]
    [InlineData("emails[unknown eq \"a\"]")]
    [InlineData("emails[type eq \"work\"].unknown eq \"a\"")]
    [InlineData("emails[type eq \"work\"].value")]
    [InlineData("urn:example:unknown:schema:attribute eq \"a\"")]
    [InlineData("active gt true")]
    [InlineData("active co \"t\"")]
    [InlineData("active eq \"true\"")]
    [InlineData("name eq \"Ann\"")]
    [InlineData("meta eq \"x\"")]
    [InlineData("meta.created gt \"yesterday\"")]
    [InlineData("meta.created co \"2026\"")]
    [InlineData("userName eq 42")]
    [InlineData("userName gt null")]
    public void Invalid_filter_is_rejected_with_invalidFilter(string filter) {
        var error = Assert.Throws<ScimException>(() => FilterParser.Parse(filter, ResourceType.User));

        Assert.Equal(400, error.Status);
        Assert.Equal(ScimException.INVALID_FILTER, error.ScimType);
    }

    private static bool Matches(string filter) {
        return FilterParser.Parse(filter, ResourceType.User).Matches(Ann);
    }
}
