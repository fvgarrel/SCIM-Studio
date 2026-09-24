using ScimStudio.Core.Dialects;
using ScimStudio.Core.Generation;
using ScimStudio.Core.Scim;

namespace ScimStudio.Tests.Core;

public sealed class GeneratorTests : DemoServerTest {
    [Theory]
    [InlineData(DialectKind.Rfc7644)]
    [InlineData(DialectKind.EntraId)]
    [InlineData(DialectKind.Okta)]
    public async Task Generated_people_and_groups_arrive_marked_and_leave_without_touching_anyone_else(DialectKind kind) {
        var bystander = await CreateUserAsync("bystander@example.com");
        var generator = new TestDataGenerator(Client, ScimDialect.For(kind), new Random(7));

        var made = await generator.GenerateAsync(new GeneratorOptions { Users = 12, Groups = 2, MembersPerGroup = 3, Parallelism = 3 }, null, Token);

        Assert.Equal((12, 2, 0), (made.UsersCreated, made.GroupsCreated, made.Failed));
        var users = await ScimClient.ReadAllAsync(Client.ListUsersAsync, new ScimQuery(), 100, Token);
        Assert.Equal(13, users.Count);
        Assert.Equal(12, users.Count(u => u.ExternalId?.StartsWith(TestDataGenerator.MARKER, StringComparison.Ordinal) == true));

        foreach (var group in (await Client.ListGroupsAsync(null, Token)).Resources) {
            Assert.Equal(3, (await Client.GetGroupAsync(group.Id, Token)).Members.Count);
        }

        var removed = await generator.RemoveAsync(3, null, Token);

        Assert.Equal((14, 0), (removed.Removed, removed.Failed));
        var left = Assert.Single((await Client.ListUsersAsync(null, Token)).Resources);
        Assert.Equal(bystander.Id, left.Id);
        Assert.Equal(0, (await Client.ListGroupsAsync(null, Token)).TotalResults);
    }

    [Theory]
    [InlineData("Müller", "mueller")]
    [InlineData("Weiß", "weiss")]
    [InlineData("García", "garcia")]
    [InlineData("O'Brien", "obrien")]
    [InlineData("Chloé", "chloe")]
    public void Names_become_addresses_every_mail_system_takes(string name, string local) {
        Assert.Equal(local, TestDataGenerator.Ascii(name));
    }

    [Fact]
    public void A_person_made_twice_from_the_same_name_gets_a_number() {
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var first = TestDataGenerator.Person(new Random(3), "example.com", taken);
        var second = TestDataGenerator.Person(new Random(3), "example.com", taken);

        Assert.NotEqual(first.UserName, second.UserName);
        Assert.StartsWith(first.UserName.Split('@')[0], second.UserName, StringComparison.Ordinal);
    }
}
