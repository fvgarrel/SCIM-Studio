using System.Text.Json.Nodes;
using ScimStudio.Core.Dialects;
using ScimStudio.Core.Scim;
using ScimStudio.Core.Text;
using static ScimStudio.Core.Checks.CheckContext;

namespace ScimStudio.Core.Checks;

/// <summary>
/// What Microsoft Entra ID and Okta send and expect, as their documentation shows it - which is not always what RFC 7644 has. A server
/// that follows the RFC to the letter can still fail here, and an identity provider with it.
/// </summary>
public sealed partial class ConformanceSuite {
    /// <summary>Entra ID's create: both schemas even without extension values, meta.resourceType, an empty roles list, name.formatted.</summary>
    /// <param name="c">The check.</param>
    private async Task EntraUserCreateAsync(CheckContext c) {
        var body = new JsonObject {
            ["schemas"] = new JsonArray(ScimSchemas.USER, ScimSchemas.ENTERPRISE_USER),
            ["externalId"] = ExternalId("entra-user"),
            ["userName"] = UserName("entra-user"),
            ["active"] = true,
            ["emails"] = new JsonArray(Email(UserName("entra-user"), "work", primary: true)),
            ["meta"] = new JsonObject { ["resourceType"] = "User" },
            ["name"] = new JsonObject { ["formatted"] = "Entra User", ["familyName"] = "User", ["givenName"] = "Entra" },
            ["roles"] = new JsonArray(),
        };

        Expect(Track(await c.SendAsync(HttpMethod.Post, "/Users", body), _users), 201);
    }

    private async Task EntraCasingAsync(CheckContext c) {
        await PatchAsync(c, ScimClient.UserPath(Alice.Id), ScimPatch.Operation("Replace", "displayName", "Alicia Entra"));

        var user = await ReadUserAsync(c, Alice.Id);
        Equal("displayName", user.DisplayName, "Alicia Entra");
    }

    private async Task EntraBooleansAsync(CheckContext c) {
        await PatchAsync(c, ScimClient.UserPath(Alice.Id), ScimPatch.Operation("Replace", "active", "False"));
        await ExpectActiveAsync(c, Alice.Id, false);

        await PatchAsync(c, ScimClient.UserPath(Alice.Id), ScimPatch.Operation("Replace", "active", "True"));
        await ExpectActiveAsync(c, Alice.Id, true);
    }

    private async Task EntraFilteredAddAsync(CheckContext c) {
        // Without an address first, so the filter matches nothing and the server has to create the element it describes. A server that
        // insists on an address keeps the old one, and the add then replaces it - which passes as well.
        await c.SendAsync(HttpMethod.Patch, ScimClient.UserPath(Bob.Id), ScimPatch.Request(ScimPatch.Operation("remove", "emails", null)));

        var address = $"{Prefix}{_marker}-bob-entra@example.org";
        await PatchAsync(c, ScimClient.UserPath(Bob.Id), ScimPatch.Operation("Add", "emails[type eq \"work\"].value", address));

        var user = await ReadUserAsync(c, Bob.Id);
        Equal("emails[type eq \"work\"].value", user.Email, address, ignoreCase: true);
    }

    private async Task EntraDottedKeysAsync(CheckContext c) {
        await PatchAsync(c, ScimClient.UserPath(Alice.Id), ScimPatch.Operation("Add", null, new JsonObject { ["name.givenName"] = "Dotted" }));

        var user = await ReadUserAsync(c, Alice.Id);
        Equal("name.givenName", user.GivenName, "Dotted");
    }

    /// <summary>Entra ID's replace without a path names extension attributes by their full URN inside the value.</summary>
    /// <param name="c">The check.</param>
    private async Task EntraUrnKeysAsync(CheckContext c) {
        var value = new JsonObject {
            ["displayName"] = "Alicia Keys",
            [$"{ScimSchemas.ENTERPRISE_USER}:employeeNumber"] = "0815",
        };
        await PatchAsync(c, ScimClient.UserPath(Alice.Id), ScimPatch.Operation("replace", null, value));

        var user = await ReadUserAsync(c, Alice.Id);
        Equal("displayName", user.DisplayName, "Alicia Keys");
        Equal("employeeNumber", Enterprise(user, "employeeNumber"), "0815");
    }

    /// <summary>
    /// Entra ID matches people by userName or by their work address, and asks for the latter as
    /// <c>emails[type eq "work"].value eq "…"</c>.
    /// </summary>
    /// <param name="c">The check.</param>
    private async Task EntraEmailFilterAsync(CheckContext c) {
        RequireFilter();
        var user = await ReadUserAsync(c, Alice.Id);
        var work = ScimJson.Items(ScimJson.Get(user.Resource, "emails"))
            .Where(e => string.Equals(ScimJson.Text(ScimJson.Get(e, "type")), "work", StringComparison.OrdinalIgnoreCase))
            .Select(e => ScimJson.Text(ScimJson.Get(e, "value")))
            .FirstOrDefault();
        if (work is null) {
            Fail("note.absent", "emails[type eq \"work\"]");
        }

        await FindOnlyAsync(c, ScimFilterText.Eq("emails[type eq \"work\"].value", work), Alice);
    }

    /// <summary>Entra ID soft-deletes with active = false and expects the user to be found afterwards, or it creates them again.</summary>
    /// <param name="c">The check.</param>
    private async Task EntraInactiveVisibleAsync(CheckContext c) {
        RequirePatch();
        RequireFilter();

        var path = ScimClient.UserPath(Bob.Id);
        await PatchAsync(c, path, ScimPatch.Operation("replace", "active", false));
        try {
            var page = await ListUsersAsync(c, new ScimQuery { Filter = ScimFilterText.Eq("userName", Bob.UserName) });
            if (page.TotalResults != 1) {
                Fail("note.inactiveHidden", page.TotalResults);
            }

            Expect(await c.SendAsync(HttpMethod.Get, path), 200);
        } finally {
            await TryPatchAsync(c, path, ScimPatch.Operation("replace", "active", true));
        }
    }

    private async Task EntraRemoveByValueAsync(CheckContext c) {
        var path = ScimClient.GroupPath(Group.Id);
        await PatchAsync(c, path, ScimPatch.Operation("add", "members", new JsonArray(new JsonObject { ["value"] = Bob.Id })));
        await PatchAsync(c, path, ScimPatch.Operation("Remove", "members", new JsonArray(new JsonObject { ["value"] = Bob.Id })));

        var group = await ReadGroupAsync(c, Group.Id);
        Member(group, Bob, expected: false);
        Member(group, Alice, expected: true);
    }

    private async Task EntraGroupSchemaAsync(CheckContext c) {
        var body = new JsonObject {
            ["schemas"] = new JsonArray(ScimSchemas.GROUP, EntraIdDialect.GROUP_SCHEMA),
            ["externalId"] = ExternalId("entra-group"),
            ["displayName"] = GroupName("entra"),
            ["meta"] = new JsonObject { ["resourceType"] = "Group" },
        };

        Expect(Track(await c.SendAsync(HttpMethod.Post, "/Groups", body), _groups), 201);
    }

    /// <summary>Before it creates a group, Entra ID looks it up by name, members left out; a group that is not there is an empty list.</summary>
    /// <param name="c">The check.</param>
    private async Task EntraGroupNoMatchAsync(CheckContext c) {
        RequireFilter();
        var query = new ScimQuery { Filter = ScimFilterText.Eq("displayName", GroupName("nobody")), ExcludedAttributes = ["members"] };
        var page = ScimPage.Read(Body(Expect(await c.SendAsync(HttpMethod.Get, $"/Groups{query.ToQueryString()}"), 200)), ScimGroup.Read);
        Count(page, 0);
    }

    /// <summary>
    /// RFC 7643 lets two groups share a name; Entra ID matches groups by displayName and needs it unique. A second group of the same name
    /// is answered with 409 by a server Entra ID can work with.
    /// </summary>
    /// <param name="c">The check.</param>
    private async Task EntraGroupUniqueNameAsync(CheckContext c) {
        var name = (await ReadGroupAsync(c, Group.Id)).DisplayName;
        var response = Track(await c.SendAsync(HttpMethod.Post, "/Groups", GroupBody(name, [], ExternalId("group-twin"))), _groups);
        if (response.IsSuccess) {
            c.Warn(Message.Of("note.groupNameTwice", name));
            return;
        }

        Expect(response, 409);
    }

    private async Task OktaActiveAsync(CheckContext c) {
        var path = ScimClient.UserPath(Alice.Id);
        await PatchAsync(c, path, ScimPatch.Operation("replace", null, new JsonObject { ["active"] = false }));
        await ExpectActiveAsync(c, Alice.Id, false);

        await PatchAsync(c, path, ScimPatch.Operation("replace", null, new JsonObject { ["active"] = true }));
        await ExpectActiveAsync(c, Alice.Id, true);
    }

    private async Task OktaRenameAsync(CheckContext c) {
        var value = new JsonObject { ["id"] = Group.Id, ["displayName"] = GroupName("okta") };
        await PatchAsync(c, ScimClient.GroupPath(Group.Id), ScimPatch.Operation("replace", null, value));

        var group = await ReadGroupAsync(c, Group.Id);
        Equal("displayName", group.DisplayName, GroupName("okta"));
    }

    private async Task OktaPutAsync(CheckContext c) {
        var body = UserBody("alice", "Alicia", "Okta");
        body["id"] = Alice.Id;
        body["locale"] = "en-US";
        body["groups"] = new JsonArray();
        body["meta"] = new JsonObject { ["resourceType"] = "User" };

        var user = ScimUser.Read(Body(Expect(await c.SendAsync(HttpMethod.Put, ScimClient.UserPath(Alice.Id), body), 200)));
        Equal("name.familyName", user.FamilyName, "Okta");
    }
}
