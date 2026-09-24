using System.Text.Json.Nodes;
using ScimStudio.DemoServer.Schema;
using ScimStudio.DemoServer.Store;

namespace ScimStudio.DemoServer;

/// <summary>Sample users and groups, so a fresh server has something to list, filter, sort and page through.</summary>
internal static class SeedData {
    // Oldest first: the store lists resources in the order they were created.
    private static readonly Person[] People = [
        new("Anna", "Schmidt", "anna.schmidt", "Engineering Manager", "de-DE", "Europe/Berlin") {
            EmployeeNumber = "1001",
            Department = "Engineering",
            Phone = "+49 30 5550 1001",
        },
        new("Lukas", "Weber", "lukas.weber", "Software Engineer", "de-DE", "Europe/Berlin") {
            EmployeeNumber = "1002",
            Department = "Engineering",
            Manager = "anna.schmidt",
        },
        new("Sofia", "Rossi", "sofia.rossi", "Account Executive", "it-IT", "Europe/Rome") {
            EmployeeNumber = "1003",
            Department = "Sales",
            Phone = "+39 02 5550 1003",
        },
        new("Yuki", "Tanaka", "yuki.tanaka", "Site Reliability Engineer", "ja-JP", "Asia/Tokyo"),
        new("Omar", "Haddad", "omar.haddad", "Support Lead", "en-GB", "Europe/London") {
            EmployeeNumber = "1005",
            Department = "Support",
            Phone = "+44 20 5550 1005",
        },
        new("Lena", "Müller", "lena.mueller", "Sales Representative", "de-DE", "Europe/Berlin"),
        new("Jonas", "Becker", "jonas.becker", "Support Engineer", "de-DE", "Europe/Berlin") { Active = false },
        new("Mei", "Chen", "mei.chen", "Platform Architect", "en-SG", "Asia/Singapore") {
            EmployeeNumber = "1008",
            Department = "Engineering",
            Manager = "anna.schmidt",
        },
        new("Mateo", "García", "mateo.garcia", "Sales Manager", "es-ES", "Europe/Madrid"),
        new("Aisha", "Okafor", "aisha.okafor", "Customer Success Manager", "en-NG", "Africa/Lagos"),
        new("Felix", "Wagner", "felix.wagner", "Financial Controller", "de-DE", "Europe/Berlin") {
            EmployeeNumber = "1011",
            Department = "Finance",
        },
        new("Ingrid", "Larsen", "ingrid.larsen", "Sales Representative", "nb-NO", "Europe/Oslo") { Active = false },
        new("Arjun", "Patel", "arjun.patel", "Data Engineer", "en-IN", "Asia/Kolkata"),
        new("Emma", "Fischer", "emma.fischer", "Support Engineer", "de-AT", "Europe/Vienna"),
        new("Tomasz", "Nowak", "tomasz.nowak", "QA Engineer", "pl-PL", "Europe/Warsaw") { Active = false, UserType = "Contractor" },
        new("Chloé", "Dubois", "chloe.dubois", "Sales Director", "fr-FR", "Europe/Paris"),
    ];

    // Some users belong to two groups; Felix Wagner belongs to none.
    private static readonly Team[] Teams = [
        new("Engineering", ["anna.schmidt", "lukas.weber", "yuki.tanaka", "mei.chen", "arjun.patel", "tomasz.nowak"]),
        new("Sales", ["sofia.rossi", "lena.mueller", "mateo.garcia", "ingrid.larsen", "chloe.dubois"]),
        new("Support", ["omar.haddad", "jonas.becker", "aisha.okafor", "emma.fischer"]),
        new("Administrators", ["anna.schmidt", "mei.chen", "omar.haddad"]),
    ];

    public static void Populate(ScimStore store, DateTimeOffset now) {
        var users = new Dictionary<string, (string Id, DateTimeOffset Created)>(StringComparer.Ordinal);
        for (var index = 0; index < People.Length; index++) {
            var person = People[index];
            // Spread over the last six months, so that sorting by meta.created means something
            var created = now.AddDays(-180 + (index * 10)).AddHours(-(index * 7 % 12));
            // Edited a few days after creation, or deactivated some weeks after
            var lastModified = created.AddDays(person.Active ? 2 : 21);
            var id = Guid.NewGuid().ToString();
            var managerId = person.Manager is null ? null : users[person.Manager].Id;
            users[person.Handle] = (id, created);
            var user = User(person, id, managerId);
            user["meta"] = Meta("User", created, lastModified < now ? lastModified : now);
            store.Import(ResourceType.User, user);
        }

        for (var index = 0; index < Teams.Length; index++) {
            var team = Teams[index];
            var created = now.AddDays(-190).AddHours(index);
            var members = team.Members.Select(handle => users[handle]).ToList();
            store.Import(ResourceType.Group, new JsonObject {
                ["id"] = Guid.NewGuid().ToString(),
                ["displayName"] = team.Name,
                ["members"] = new JsonArray([.. members.Select(member => new JsonObject { ["value"] = member.Id })]),
                ["meta"] = Meta("Group", created, members.Max(member => member.Created)),
            });
        }
    }

    private static JsonObject User(Person person, string id, string? managerId) {
        var displayName = $"{person.GivenName} {person.FamilyName}";
        var userName = $"{person.Handle}@example.com";
        var user = new JsonObject {
            ["id"] = id,
            ["externalId"] = Guid.NewGuid().ToString(),
            ["userName"] = userName,
            ["name"] = new JsonObject {
                ["formatted"] = displayName,
                ["familyName"] = person.FamilyName,
                ["givenName"] = person.GivenName,
            },
            ["displayName"] = displayName,
            ["title"] = person.Title,
            ["userType"] = person.UserType,
            ["preferredLanguage"] = person.Locale,
            ["locale"] = person.Locale,
            ["timezone"] = person.TimeZone,
            ["active"] = person.Active,
            ["emails"] = new JsonArray(new JsonObject { ["value"] = userName, ["type"] = "work", ["primary"] = true }),
        };
        if (person.Phone is not null) {
            user["phoneNumbers"] = new JsonArray(new JsonObject { ["value"] = person.Phone, ["type"] = "work", ["primary"] = true });
        }
        if (person.EmployeeNumber is not null) {
            var enterprise = new JsonObject {
                ["employeeNumber"] = person.EmployeeNumber,
                ["organization"] = "Example Corp",
                ["department"] = person.Department,
            };
            if (managerId is not null) {
                enterprise["manager"] = new JsonObject { ["value"] = managerId };
            }
            user[ScimUrns.ENTERPRISE_USER] = enterprise;
        }
        return user;
    }

    private static JsonObject Meta(string resourceType, DateTimeOffset created, DateTimeOffset lastModified) {
        return new JsonObject {
            ["resourceType"] = resourceType,
            ["created"] = ScimStore.Timestamp(created),
            ["lastModified"] = ScimStore.Timestamp(lastModified),
        };
    }

    private sealed record Person(string GivenName, string FamilyName, string Handle, string Title, string Locale, string TimeZone) {
        public bool Active { get; init; } = true;
        public string UserType { get; init; } = "Employee";
        public string? EmployeeNumber { get; init; }
        public string? Department { get; init; }
        /// <summary>The handle of the manager, who has to come earlier in the list.</summary>
        public string? Manager { get; init; }
        public string? Phone { get; init; }
    }

    private sealed record Team(string Name, IReadOnlyList<string> Members);
}
