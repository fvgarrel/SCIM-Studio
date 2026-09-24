using System.Text.Json.Nodes;
using ScimStudio.Core.Scim;
using ScimStudio.Core.Text;
using static ScimStudio.Core.Checks.CheckContext;

namespace ScimStudio.Core.Checks;

/// <summary>
/// Groups and their members. The first group follows the way identity providers use one; a second group, created empty, takes the
/// membership changes that would otherwise disturb it.
/// </summary>
public sealed partial class ConformanceSuite {
    private async Task CreateGroupAsync(CheckContext c) {
        var body = GroupBody(GroupName("group"), [Alice.Id]);
        var response = Track(await c.SendAsync(HttpMethod.Post, "/Groups", body), _groups);
        var group = ScimGroup.Read(Body(Expect(response, 201)));

        Equal("displayName", group.DisplayName, GroupName("group"));
        if (group.HasMembers && !group.Members.Any(m => m.Id == Alice.Id)) {
            Fail("note.notMember", Alice.UserName);
        }

        _group = group;
    }

    private async Task GroupRepresentationAsync(CheckContext c) {
        var group = await ReadGroupAsync(c, Group.Id);
        if (!Schemas(group.Resource).Contains(ScimSchemas.GROUP)) {
            c.Warn(Message.Of("note.schemas", ScimSchemas.GROUP));
        }

        if (!string.Equals(ScimJson.Text(ScimJson.Get(ScimJson.Get(group.Resource, "meta"), "resourceType")), "Group", StringComparison.Ordinal)) {
            c.Warn(Message.Of("note.meta", "meta.resourceType"));
        }

        if (group.Location is null) {
            c.Warn(Message.Of("note.meta", "meta.location"));
        } else if (!PointsTo(group.Location, ScimClient.GroupPath(Group.Id), "/Groups", Group.Id)) {
            c.Warn(Message.Of("note.locationWrong", group.Location, ScimClient.GroupPath(Group.Id)));
        }
    }

    private async Task GetGroupAsync(CheckContext c) {
        var group = await ReadGroupAsync(c, Group.Id);
        Member(group, Alice, expected: true);
    }

    private async Task FilterGroupAsync(CheckContext c) {
        RequireFilter();
        var query = new ScimQuery { Filter = ScimFilterText.Eq("displayName", Group.DisplayName), ExcludedAttributes = ["members"] };
        var page = ScimPage.Read(Body(Expect(await c.SendAsync(HttpMethod.Get, $"/Groups{query.ToQueryString()}"), 200)), ScimGroup.Read);

        Count(page, 1);
        Equal("id", page.Resources[0].Id, Group.Id);
        if (page.Resources[0].Members.Count > 0) {
            c.Warn(Message.Of("note.present", "members"));
        }
    }

    private async Task GroupsByMemberAsync(CheckContext c) {
        RequireFilter();
        var filter = ScimFilterText.Eq("members.value", Alice.Id);
        var response = await c.SendAsync(HttpMethod.Get, $"/Groups{new ScimQuery { Filter = filter }.ToQueryString()}");
        Declined(response, $"filter={filter}", 400, 501);

        var page = ScimPage.Read(Body(Expect(response, 200)), ScimGroup.Read);
        if (!page.Resources.Any(g => g.Id == Group.Id)) {
            Fail("note.notFound", Group.DisplayName);
        }
    }

    private async Task SearchGroupsAsync(CheckContext c) {
        RequireFilter();
        var search = new ScimQuery { Filter = ScimFilterText.Eq("displayName", Group.DisplayName) }.ToSearchRequest();
        var response = await c.SendAsync(HttpMethod.Post, "/Groups/.search", search);
        Declined(response, "POST /Groups/.search", 404, 405, 501);

        var page = ScimPage.Read(Body(Expect(response, 200)), ScimGroup.Read);
        Count(page, 1);
        Equal("id", page.Resources[0].Id, Group.Id);
    }

    private static async Task UnknownGroupAsync(CheckContext c) {
        Expect(await c.SendAsync(HttpMethod.Get, ScimClient.GroupPath(Guid.NewGuid().ToString())), 404);
    }

    private async Task PatchUnknownGroupAsync(CheckContext c) {
        RequirePatch();
        var path = ScimClient.GroupPath(Guid.NewGuid().ToString());
        Expect(await TryPatchAsync(c, path, ScimPatch.Operation("replace", "displayName", GroupName("nobody"))), 404);
    }

    /// <summary>displayName is required of a group (RFC 7643 section 4.2).</summary>
    /// <param name="c">The check.</param>
    private async Task CreateInvalidGroupAsync(CheckContext c) {
        var body = new JsonObject { ["schemas"] = new JsonArray(ScimSchemas.GROUP), ["externalId"] = ExternalId("group-invalid") };
        var response = Track(await c.SendAsync(HttpMethod.Post, "/Groups", body), _groups);
        Expect(response, 400);
        c.ExpectScimType(response, "invalidValue");
    }

    private async Task CreateEmptyGroupAsync(CheckContext c) {
        var body = GroupBody(GroupName("empty"), [], ExternalId("group-empty"));
        var group = ScimGroup.Read(Body(Expect(Track(await c.SendAsync(HttpMethod.Post, "/Groups", body), _groups), 201)));
        _empty = group;

        if ((await ReadGroupAsync(c, group.Id)).Members.Count > 0) {
            Fail("note.value", "members", "…", "[]");
        }
    }

    private async Task AddMemberAsync(CheckContext c) {
        RequirePatch();
        var value = new JsonArray(new JsonObject { ["value"] = Bob.Id });
        await PatchAsync(c, ScimClient.GroupPath(Group.Id), ScimPatch.Operation("add", "members", value));

        var group = await ReadGroupAsync(c, Group.Id);
        Member(group, Alice, expected: true);
        Member(group, Bob, expected: true);
    }

    private async Task AddMembersAsync(CheckContext c) {
        RequirePatch();
        var value = new JsonArray(new JsonObject { ["value"] = Alice.Id }, new JsonObject { ["value"] = Bob.Id });
        await PatchAsync(c, ScimClient.GroupPath(Empty.Id), ScimPatch.Operation("add", "members", value));

        var group = await ReadGroupAsync(c, Empty.Id);
        Member(group, Alice, expected: true);
        Member(group, Bob, expected: true);
    }

    /// <summary>
    /// RFC 7644 section 3.5.2.1: adding a value that is already there changes nothing - it SHOULD succeed, and SHALL NOT touch the modify
    /// timestamp. Identity providers send such adds when they retry.
    /// </summary>
    /// <param name="c">The check.</param>
    private async Task AddDuplicateMemberAsync(CheckContext c) {
        RequirePatch();
        var path = ScimClient.GroupPath(Empty.Id);
        var value = new JsonArray(new JsonObject { ["value"] = Alice.Id });
        await PatchAsync(c, path, ScimPatch.Operation("add", "members", value));
        var before = await ReadGroupAsync(c, Empty.Id);

        var response = await TryPatchAsync(c, path, ScimPatch.Operation("add", "members", value.DeepClone()));
        if (response.StatusCode is 400 or 409) {
            c.Warn(Message.Of("note.duplicateMemberRefused", response.StatusCode));
            return;
        }

        Expect(response, 200, 204);
        var after = await ReadGroupAsync(c, Empty.Id);
        var times = after.Members.Count(m => m.Id == Alice.Id);
        if (times != 1) {
            Fail("note.memberTwice", Alice.UserName, times);
        }

        if (before.LastModified is { } earlier && after.LastModified is { } later && later != earlier) {
            c.Warn(Message.Of("note.modifiedByNothing"));
        }
    }

    /// <summary>A member's type and $ref, where the server sends them, name what the member is (RFC 7643 section 4.2).</summary>
    /// <param name="c">The check.</param>
    private async Task MemberAttributesAsync(CheckContext c) {
        var group = await ReadGroupAsync(c, Group.Id);
        var member = ScimJson.Items(ScimJson.Get(group.Resource, "members"))
            .FirstOrDefault(m => ScimJson.Text(ScimJson.Get(m, "value")) == Alice.Id);
        if (member is null) {
            Fail("note.notMember", Alice.UserName);
        }

        if (ScimJson.Text(ScimJson.Get(member, "type")) is { } type && !string.Equals(type, "User", StringComparison.OrdinalIgnoreCase)) {
            Fail("note.value", "members.type", type, "User");
        }

        if (ScimJson.Text(ScimJson.Get(member, "$ref")) is { } reference && !PointsTo(reference, ScimClient.UserPath(Alice.Id), "/Users", Alice.Id)) {
            c.Warn(Message.Of("note.locationWrong", reference, ScimClient.UserPath(Alice.Id)));
        }
    }

    private async Task RemoveMemberAsync(CheckContext c) {
        RequirePatch();
        var path = $"members[value eq {ScimFilterText.Quote(Alice.Id)}]";
        await PatchAsync(c, ScimClient.GroupPath(Group.Id), ScimPatch.Operation("remove", path, null));

        var group = await ReadGroupAsync(c, Group.Id);
        Member(group, Alice, expected: false);
        Member(group, Bob, expected: true);
    }

    private async Task UserGroupsAfterRemoveAsync(CheckContext c) {
        var user = await ReadUserAsync(c, Alice.Id);
        if (user.Groups.Any(g => g.Id == Group.Id)) {
            c.Warn(Message.Of("note.userGroupsStale"));
        }
    }

    /// <summary>RFC 7644 section 3.5.2.2: removing someone who is not a member changes nothing and succeeds.</summary>
    /// <param name="c">The check.</param>
    private async Task RemoveNonMemberAsync(CheckContext c) {
        RequirePatch();
        var path = $"members[value eq {ScimFilterText.Quote(Guid.NewGuid().ToString())}]";
        var response = await TryPatchAsync(c, ScimClient.GroupPath(Group.Id), ScimPatch.Operation("remove", path, null));
        if (response.StatusCode == 400) {
            c.Warn(Message.Of("note.removeNonMember", response.StatusCode));
            return;
        }

        Expect(response, 200, 204);
    }

    private async Task ReplaceMembersAsync(CheckContext c) {
        RequirePatch();
        var value = new JsonArray(new JsonObject { ["value"] = Bob.Id });
        await PatchAsync(c, ScimClient.GroupPath(Empty.Id), ScimPatch.Operation("replace", "members", value));

        var group = await ReadGroupAsync(c, Empty.Id);
        Member(group, Bob, expected: true);
        Member(group, Alice, expected: false);
    }

    private async Task RemoveAllMembersAsync(CheckContext c) {
        RequirePatch();
        await PatchAsync(c, ScimClient.GroupPath(Empty.Id), ScimPatch.Operation("remove", "members", null));

        var group = await ReadGroupAsync(c, Empty.Id);
        if (group.Members.Count > 0) {
            Fail("note.stillMember", string.Join(", ", group.Members.Select(m => m.Display ?? m.Id)));
        }
    }

    /// <summary>A member that does not exist is best refused; kept, it points nowhere.</summary>
    /// <param name="c">The check.</param>
    private async Task MemberUnknownAsync(CheckContext c) {
        RequirePatch();
        var unknown = Guid.NewGuid().ToString();
        var value = new JsonArray(new JsonObject { ["value"] = unknown });

        var response = await TryPatchAsync(c, ScimClient.GroupPath(Empty.Id), ScimPatch.Operation("add", "members", value));
        if (response.StatusCode is 400 or 404) {
            return;
        }

        Expect(response, 200, 204);
        if ((await ReadGroupAsync(c, Empty.Id)).Members.Any(m => m.Id == unknown)) {
            c.Warn(Message.Of("note.danglingMember", unknown));
        }
    }

    private async Task RenameGroupAsync(CheckContext c) {
        RequirePatch();
        await PatchAsync(c, ScimClient.GroupPath(Group.Id), ScimPatch.Operation("replace", "displayName", GroupName("renamed")));

        var group = await ReadGroupAsync(c, Group.Id);
        Equal("displayName", group.DisplayName, GroupName("renamed"));
    }

    private async Task PutGroupAsync(CheckContext c) {
        var body = GroupBody(GroupName("replaced"), [Alice.Id]);
        Expect(await c.SendAsync(HttpMethod.Put, ScimClient.GroupPath(Group.Id), body), 200);

        var group = await ReadGroupAsync(c, Group.Id);
        Equal("displayName", group.DisplayName, GroupName("replaced"));
        Member(group, Alice, expected: true);
        Member(group, Bob, expected: false);
    }

    private async Task UserGroupsAsync(CheckContext c) {
        var user = await ReadUserAsync(c, Alice.Id);
        if (!user.Groups.Any(g => g.Id == Group.Id)) {
            c.Warn(Message.Of("note.userGroups"));
        }
    }

    /// <summary>A group can have groups as members (RFC 7643 section 4.2) - a server that refuses or drops one does not offer it.</summary>
    /// <param name="c">The check.</param>
    private async Task NestedGroupAsync(CheckContext c) {
        RequirePatch();
        var path = ScimClient.GroupPath(Group.Id);
        var value = new JsonArray(new JsonObject { ["value"] = Empty.Id, ["type"] = "Group" });

        var response = await TryPatchAsync(c, path, ScimPatch.Operation("add", "members", value));
        Declined(response, "members: [{\"type\": \"Group\"}]", 400, 404, 501);
        Expect(response, 200, 204);

        var nested = (await ReadGroupAsync(c, Group.Id)).Members.Any(m => m.Id == Empty.Id);

        // Out again, so the first group holds users only for what follows.
        await TryPatchAsync(c, path, ScimPatch.Operation("remove", $"members[value eq {ScimFilterText.Quote(Empty.Id)}]", null));
        if (!nested) {
            Unsupported("note.nestedDropped");
        }
    }

    /// <summary>A user's groups are read-only (RFC 7643 section 4.1.2): membership changes through the group, and a PATCH of it is refused.</summary>
    /// <param name="c">The check.</param>
    private async Task ReadOnlyGroupsAsync(CheckContext c) {
        RequirePatch();
        var spare = await SpareAsync(c, "groups");
        var value = new JsonArray(new JsonObject { ["value"] = Empty.Id });

        var response = await TryPatchAsync(c, ScimClient.UserPath(spare.Id), ScimPatch.Operation("add", "groups", value));
        if (response.IsSuccess) {
            var joined = (await ReadGroupAsync(c, Empty.Id)).Members.Any(m => m.Id == spare.Id);
            c.Warn(Message.Of(joined ? "note.readOnlyApplied" : "note.readOnlyIgnored", "groups"));
            return;
        }

        Expect(response, 400);
        c.ExpectScimType(response, "mutability");
    }
}
