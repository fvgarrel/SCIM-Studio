using System.Globalization;
using System.Text.Json.Nodes;
using ScimStudio.DemoServer;

namespace ScimStudio.Tests.DemoServer;

public sealed class SeededServerFixture : DemoServerFixture {
    protected override DemoServerOptions Options => new() { Seed = true };
}

public sealed class SeedDataTests(SeededServerFixture fixture) : IClassFixture<SeededServerFixture> {
    private const string ENTERPRISE = "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User";

    private readonly ScimTestClient _client = fixture.Client;

    [Fact]
    public async Task Seed_has_sixteen_users_with_example_com_user_names() {
        var users = await ResourcesAsync("Users");

        Assert.Equal(16, users.Count);
        Assert.All(users, user => {
            var userName = (string)user["userName"]!;
            Assert.EndsWith("@example.com", userName, StringComparison.Ordinal);
            Assert.Equal(userName, (string?)Assert.Single(user["emails"]!.AsArray())!["value"]);
            Assert.True(Guid.TryParse((string?)user["externalId"], out _));
            Assert.Equal($"{(string?)user["name"]!["givenName"]} {(string?)user["name"]!["familyName"]}", (string?)user["displayName"]);
        });
        Assert.Contains(users, user => (string?)user["displayName"] == "Anna Schmidt");
        Assert.Contains(users, user => (string?)user["userName"] == "lena.mueller@example.com");
    }

    [Fact]
    public async Task Seed_has_a_few_inactive_users_and_some_with_the_enterprise_extension() {
        var users = await ResourcesAsync("Users");

        var inactive = users.Count(user => !(bool)user["active"]!);
        Assert.InRange(inactive, 2, 3);
        var enterprise = users.Where(user => user.ContainsKey(ENTERPRISE)).ToList();
        Assert.InRange(enterprise.Count, 3, 8);
        Assert.All(enterprise, user => Assert.NotNull(user[ENTERPRISE]!["department"]));
        var lukas = users.Single(user => (string?)user["userName"] == "lukas.weber@example.com");
        Assert.Equal("Anna Schmidt", (string?)lukas[ENTERPRISE]!["manager"]!["displayName"]);
    }

    [Fact]
    public async Task Seed_has_four_groups_whose_members_see_them() {
        var groups = await ResourcesAsync("Groups");
        var users = await ResourcesAsync("Users");

        Assert.Equal(["Engineering", "Sales", "Support", "Administrators"], groups.Select(group => (string?)group["displayName"]));
        var memberships = users.ToDictionary(user => (string)user["id"]!, user => user["groups"]?.AsArray().Count ?? 0);
        Assert.Equal(1, memberships.Values.Count(count => count == 0));
        Assert.Contains(memberships.Values, count => count == 2);
        foreach (var group in groups) {
            Assert.All(group["members"]!.AsArray(), member => Assert.True(memberships[(string)member!["value"]!] > 0));
        }
    }

    [Fact]
    public async Task Seed_spreads_creation_times_over_months_in_creation_order() {
        var users = await ResourcesAsync("Users");

        var created = users.Select(user => DateTimeOffset.Parse((string)user["meta"]!["created"]!, CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(created.Order(), created);
        Assert.True(created[^1] - created[0] > TimeSpan.FromDays(90));
        Assert.True(created[^1] < DateTimeOffset.UtcNow);
    }

    private async Task<List<JsonObject>> ResourcesAsync(string endpoint) {
        var list = (await _client.GetAsync(endpoint)).Body;
        return [.. list["Resources"]!.AsArray().Select(resource => resource!.AsObject())];
    }
}
