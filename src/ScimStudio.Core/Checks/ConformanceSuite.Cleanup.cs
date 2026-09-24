using System.Text.Json.Nodes;
using ScimStudio.Core.Scim;
using ScimStudio.Core.Text;
using static ScimStudio.Core.Checks.CheckContext;

namespace ScimStudio.Core.Checks;

/// <summary>Deleting (RFC 7644 section 3.6), what a deletion leaves behind, and removing whatever the run created.</summary>
public sealed partial class ConformanceSuite {
    private async Task DeleteGroupAsync(CheckContext c) {
        var deleted = Expect(await c.SendAsync(HttpMethod.Delete, ScimClient.GroupPath(Group.Id)), 204, 200);
        _groups.Remove(Group.Id);
        if (deleted.StatusCode == 200) {
            c.Warn(Message.Of("note.deleteStatus", 200));
        }

        Expect(await c.SendAsync(HttpMethod.Get, ScimClient.GroupPath(Group.Id)), 404);
    }

    /// <summary>A group is a list of people, not their owner: deleting it leaves its members.</summary>
    /// <param name="c">The check.</param>
    private async Task GroupDeleteKeepsUsersAsync(CheckContext c) {
        if ((await c.SendAsync(HttpMethod.Get, ScimClient.UserPath(Alice.Id))).StatusCode != 200) {
            Fail("note.memberDeleted", Alice.UserName);
        }
    }

    private async Task DeleteUserAsync(CheckContext c) {
        var deleted = Expect(await c.SendAsync(HttpMethod.Delete, ScimClient.UserPath(Alice.Id)), 204, 200);
        _users.Remove(Alice.Id);
        if (deleted.StatusCode == 200) {
            c.Warn(Message.Of("note.deleteStatus", 200));
        }

        Expect(await c.SendAsync(HttpMethod.Get, ScimClient.UserPath(Alice.Id)), 404);
    }

    /// <summary>RFC 7644 section 3.6: a deleted resource answers 404 to everything, and a list no longer holds it.</summary>
    /// <param name="c">The check.</param>
    private async Task DeletedGoneAsync(CheckContext c) {
        var path = ScimClient.UserPath(Alice.Id);
        Expect(await c.SendAsync(HttpMethod.Delete, path), 404);

        if (_config is not { PatchSupported: false }) {
            Expect(await TryPatchAsync(c, path, ScimPatch.Operation("replace", "displayName", "Gone")), 404);
        }

        if (_config is not { FilterSupported: false }) {
            Count(await ListUsersAsync(c, new ScimQuery { Filter = ScimFilterText.Eq("userName", Alice.UserName) }), 0);
        }
    }

    /// <summary>A deleted user leaves the groups it was in; a member that points nowhere is what a client has to clean up otherwise.</summary>
    /// <param name="c">The check.</param>
    private async Task UserDeleteLeavesGroupsAsync(CheckContext c) {
        RequirePatch();
        var member = await SpareAsync(c, "deleted-member");
        var value = new JsonArray(new JsonObject { ["value"] = member.Id });
        await PatchAsync(c, ScimClient.GroupPath(Empty.Id), ScimPatch.Operation("add", "members", value));

        Expect(await c.SendAsync(HttpMethod.Delete, ScimClient.UserPath(member.Id)), 204, 200);
        _users.Remove(member.Id);

        if ((await ReadGroupAsync(c, Empty.Id)).Members.Any(m => m.Id == member.Id)) {
            c.Warn(Message.Of("note.deletedMember", member.UserName));
        }
    }

    private static async Task DeleteUnknownAsync(CheckContext c) {
        Expect(await c.SendAsync(HttpMethod.Delete, ScimClient.UserPath(Guid.NewGuid().ToString())), 404);
    }

    private async Task RemainingAsync(CheckContext c) {
        await RemoveAsync(c, _groups, ScimClient.GroupPath);
        await RemoveAsync(c, _users, ScimClient.UserPath);
    }

    private static async Task RemoveAsync(CheckContext c, List<string> ids, Func<string, string> path) {
        foreach (var id in ids.ToList()) {
            var response = await c.SendAsync(HttpMethod.Delete, path(id));
            if (response.IsSuccess || response.StatusCode == 404) {
                ids.Remove(id);
            } else {
                c.Warn(Message.Of("note.cleanup", path(id), response.StatusCode));
            }
        }
    }
}
