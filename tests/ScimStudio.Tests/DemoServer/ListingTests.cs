using System.Net;
using System.Text.Json.Nodes;
using ScimStudio.DemoServer;

namespace ScimStudio.Tests.DemoServer;

/// <summary>
/// Twelve users, user01 to user12, created in that order: family names run backwards from Zimmer to Otto, the odd users are
/// active, users 1 to 6 have a title that sorts the other way round, and users 1 to 3 have a primary home address.
/// </summary>
public sealed class ListingFixture : DemoServerFixture {
    private static readonly string[] FamilyNames =
        ["Zimmer", "Young", "Xu", "Weber", "Vogel", "Urban", "Thomas", "Schmidt", "Richter", "Quinn", "Peters", "Otto"];

    protected override DemoServerOptions Options => new() { Seed = false, MaxResults = 10 };

    protected override async Task PopulateAsync(ScimTestClient client) {
        for (var number = 1; number <= FamilyNames.Length; number++) {
            var title = number <= 6 ? $"\"title\": \"Title {13 - number:00}\"," : "";
            var home = number <= 3 ? $$""", { "value": "a{{number}}@home.example.org", "type": "home", "primary": true }""" : "";
            await client.CreateUserAsync($$"""
                {
                  "userName": "user{{number:00}}@example.com",
                  "name": { "givenName": "Given{{number}}", "familyName": "{{FamilyNames[number - 1]}}" },
                  {{title}}
                  "active": {{(number % 2 == 1 ? "true" : "false")}},
                  "emails": [{ "value": "user{{number:00}}@example.com", "type": "work" }{{home}}]
                }
                """);
        }
    }
}

public sealed class ListingTests(ListingFixture fixture) : IClassFixture<ListingFixture> {
    private readonly ScimTestClient _client = fixture.Client;

    [Fact]
    public async Task List_answers_a_list_response_in_creation_order() {
        var response = await _client.GetAsync("Users");

        Assert.Equal(HttpStatusCode.OK, response.Status);
        var list = response.Body;
        Assert.Equal("urn:ietf:params:scim:api:messages:2.0:ListResponse", (string?)list["schemas"]![0]);
        Assert.Equal(12, (int)list["totalResults"]!);
        Assert.Equal(1, (int)list["startIndex"]!);
        Assert.Equal(10, (int)list["itemsPerPage"]!);
        Assert.Equal(Names(Range(1, 10)), UserNames(list));
    }

    [Theory]
    [InlineData("count=50", 1, 1, 10)]
    [InlineData("startIndex=11&count=5", 11, 11, 12)]
    [InlineData("startIndex=4&count=2", 4, 4, 5)]
    [InlineData("startIndex=0&count=3", 1, 1, 3)]
    [InlineData("startIndex=-5&count=1", 1, 1, 1)]
    public async Task Paging_follows_startIndex_and_count(string query, int startIndex, int first, int last) {
        var list = (await _client.GetAsync($"Users?{query}")).Body;

        Assert.Equal(12, (int)list["totalResults"]!);
        Assert.Equal(startIndex, (int)list["startIndex"]!);
        Assert.Equal(last - first + 1, (int)list["itemsPerPage"]!);
        Assert.Equal(Names(Range(first, last)), UserNames(list));
    }

    [Theory]
    [InlineData("count=0")]
    [InlineData("count=-3")]
    [InlineData("startIndex=13")]
    public async Task Page_beyond_the_end_or_without_room_answers_only_the_total(string query) {
        var list = (await _client.GetAsync($"Users?{query}")).Body;

        Assert.Equal(12, (int)list["totalResults"]!);
        Assert.Equal(0, (int)list["itemsPerPage"]!);
        Assert.Empty(list["Resources"]!.AsArray());
    }

    [Fact]
    public async Task Filter_narrows_the_total() {
        var list = (await _client.GetAsync("Users?filter=" + Uri.EscapeDataString("active eq true and name.familyName sw \"v\""))).Body;

        Assert.Equal(1, (int)list["totalResults"]!);
        Assert.Equal(Names(5), UserNames(list));
    }

    [Theory]
    [InlineData("filter=userName eq", "invalidFilter")]
    [InlineData("filter=shoeSize gt 42", "invalidFilter")]
    [InlineData("sortBy=shoeSize", "invalidValue")]
    [InlineData("sortBy=name", "invalidValue")]
    [InlineData("sortBy=userName&sortOrder=sideways", "invalidValue")]
    [InlineData("count=many", "invalidValue")]
    public async Task Invalid_list_parameters_answer_400(string query, string scimType) {
        var response = await _client.GetAsync($"Users?{query}");

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal(scimType, response.ScimType);
    }

    [Fact]
    public async Task Sort_by_a_sub_attribute_in_both_orders() {
        var ascending = (await _client.GetAsync("Users?sortBy=name.familyName&count=3")).Body;
        var descending = (await _client.GetAsync(
            "Users?sortBy=urn:ietf:params:scim:schemas:core:2.0:User:name.familyName&sortOrder=DESCENDING&count=3")).Body;

        Assert.Equal(Names(12, 11, 10), UserNames(ascending));
        Assert.Equal(Names(1, 2, 3), UserNames(descending));
    }

    [Fact]
    public async Task Resources_without_the_sort_value_come_last_ascending_and_first_descending() {
        var ascending = (await _client.GetAsync("Users?sortBy=title")).Body;
        var descending = (await _client.GetAsync("Users?sortBy=title&sortOrder=descending")).Body;

        Assert.Equal(Names([.. Range(6, 1), .. Range(7, 10)]), UserNames(ascending));
        Assert.Equal(Names([.. Range(7, 12), .. Range(1, 4)]), UserNames(descending));
    }

    [Fact]
    public async Task Sort_by_a_multi_valued_attribute_uses_the_primary_value() {
        var list = (await _client.GetAsync("Users?sortBy=emails&count=4")).Body;

        Assert.Equal(Names(1, 2, 3, 4), UserNames(list));
        Assert.Equal("a1@home.example.org", (string?)list["Resources"]![0]!["emails"]![1]!["value"]);
    }

    [Fact]
    public async Task Sort_by_creation_time_descending_reverses_creation_order() {
        var list = (await _client.GetAsync("Users?sortBy=meta.created&sortOrder=descending&startIndex=9")).Body;

        Assert.Equal(Names(Range(4, 1)), UserNames(list));
    }

    [Fact]
    public async Task Sort_by_a_boolean_puts_false_first() {
        var list = (await _client.GetAsync("Users?sortBy=active&count=6")).Body;

        Assert.Equal(Names(2, 4, 6, 8, 10, 12), UserNames(list));
    }

    [Fact]
    public async Task Attributes_return_only_what_is_asked_plus_id_and_schemas() {
        var list = (await _client.GetAsync("Users?attributes=userName,name.givenName,emails.type&count=1")).Body;

        var user = Assert.Single(list["Resources"]!.AsArray())!.AsObject();
        Assert.Equal(["schemas", "id", "userName", "name", "emails"], Keys(user));
        Assert.Equal(["givenName"], Keys(user["name"]!));
        Assert.Equal(["type"], Keys(user["emails"]![0]!));
    }

    [Fact]
    public async Task Excluded_attributes_are_left_out_but_id_stays() {
        var list = (await _client.GetAsync("Users?excludedAttributes=id,emails,name.familyName,meta&count=1")).Body;

        var user = Assert.Single(list["Resources"]!.AsArray())!.AsObject();
        Assert.True(user.ContainsKey("id"));
        Assert.True(user.ContainsKey("userName"));
        Assert.False(user.ContainsKey("emails"));
        Assert.False(user.ContainsKey("meta"));
        Assert.Equal(["givenName"], Keys(user["name"]!));
    }

    [Fact]
    public async Task Search_takes_its_parameters_from_the_body() {
        var response = await _client.PostAsync("Users/.search", """
            {
              "schemas": ["urn:ietf:params:scim:api:messages:2.0:SearchRequest"],
              "filter": "active eq false",
              "sortBy": "userName",
              "sortOrder": "descending",
              "startIndex": "2",
              "count": 2,
              "attributes": ["userName", "active"]
            }
            """);

        Assert.Equal(HttpStatusCode.OK, response.Status);
        var list = response.Body;
        Assert.Equal(6, (int)list["totalResults"]!);
        Assert.Equal(2, (int)list["startIndex"]!);
        Assert.Equal(Names(10, 8), UserNames(list));
        Assert.Equal(["schemas", "id", "userName", "active"], Keys(list["Resources"]![0]!));
    }

    [Fact]
    public async Task Search_accepts_attributes_as_a_comma_separated_string() {
        var response = await _client.PostAsync("Users/.search", """{ "attributes": "userName, meta.created", "count": "1" }""");

        var user = Assert.Single(response.Body["Resources"]!.AsArray())!;
        Assert.Equal(["schemas", "id", "userName", "meta"], Keys(user));
        Assert.Equal(["created"], Keys(user["meta"]!));
    }

    [Fact]
    public async Task Search_with_an_invalid_filter_answers_400() {
        var response = await _client.PostAsync("Users/.search", """{ "filter": "userName zz \"a\"" }""");

        Assert.Equal(HttpStatusCode.BadRequest, response.Status);
        Assert.Equal("invalidFilter", response.ScimType);
    }

    private static int[] Range(int first, int last) {
        var step = first <= last ? 1 : -1;
        return [.. Enumerable.Range(0, Math.Abs(last - first) + 1).Select(offset => first + (offset * step))];
    }

    private static List<string> Names(params int[] numbers) {
        return [.. numbers.Select(number => $"user{number:00}@example.com")];
    }

    private static List<string> UserNames(JsonObject list) {
        return [.. list["Resources"]!.AsArray().Select(user => (string)user!["userName"]!)];
    }

    private static List<string> Keys(JsonNode node) {
        return [.. node.AsObject().Select(property => property.Key)];
    }
}
