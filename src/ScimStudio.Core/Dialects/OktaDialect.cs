using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ScimStudio.Core.Scim;

namespace ScimStudio.Core.Dialects;

/// <summary>
/// Okta as its SCIM 2.0 integration writes requests: a lookup by name before every create, a change of profile as a PUT of the whole user -
/// read-only <c>groups</c> and <c>meta</c> included - the switch as a PATCH without a path, a group renamed by sending it back with its id,
/// and members removed one filtered path at a time.
/// </summary>
public sealed class OktaDialect : ScimDialect {
    private const string ALPHABET = "0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";

    public override DialectKind Kind => DialectKind.Okta;

    protected override bool LooksUpFirst => true;

    protected override bool FillsGroupsAfterwards => true;

    public override JsonObject UserResource(UserDraft draft) {
        ArgumentNullException.ThrowIfNull(draft);

        var resource = new JsonObject {
            ["schemas"] = new JsonArray(ScimSchemas.USER),
            ["userName"] = draft.UserName,
        };

        if (Name(draft) is { } name) {
            resource["name"] = name;
        }

        if (draft.Email is not null) {
            resource["emails"] = WorkEmail(draft.Email);
        }

        if (draft.DisplayName is not null) {
            resource["displayName"] = draft.DisplayName;
        }

        resource["locale"] = "en-US";
        resource["externalId"] = draft.ExternalId ?? OktaId("00u");
        resource["groups"] = new JsonArray();
        resource["active"] = draft.Active;
        return resource;
    }

    public override async Task<ScimUser> UpdateUserAsync(ScimClient client, ScimUser current, UserDraft draft, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(draft);

        var before = UserDraft.From(current).Normalized();
        var edited = draft.Normalized();
        var after = edited with { ExternalId = edited.ExternalId ?? current.ExternalId };

        if (after == before) {
            return current;
        }

        // The switch alone is a PATCH; anything more restates the whole user.
        if (after with { Active = before.Active } == before) {
            return await SetActiveAsync(client, current, after.Active, cancellationToken);
        }

        var resource = UserResource(after);
        resource["id"] = current.Id;
        resource["meta"] = new JsonObject { ["resourceType"] = "User" };
        return await client.ReplaceUserAsync(current.Id, resource, cancellationToken);
    }

    public override Task<ScimUser> SetActiveAsync(ScimClient client, ScimUser user, bool active, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(user);

        var patch = ScimPatch.Request(ScimPatch.Operation("replace", null, new JsonObject { ["active"] = active }));
        return client.PatchUserAsync(user.Id, patch, cancellationToken);
    }

    public override JsonObject GroupResource(GroupDraft draft) {
        ArgumentNullException.ThrowIfNull(draft);

        var resource = new JsonObject {
            ["schemas"] = new JsonArray(ScimSchemas.GROUP),
            ["displayName"] = draft.DisplayName,
        };

        if (draft.ExternalId is not null) {
            resource["externalId"] = draft.ExternalId;
        }

        resource["members"] = new JsonArray([.. draft.Members.Select(m => Member(m, withDisplay: true))]);
        return resource;
    }

    public override async Task UpdateGroupAsync(ScimClient client, ScimGroup current, GroupDraft draft, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(draft);

        var before = GroupDraft.From(current).Normalized();
        var after = draft.Normalized();
        if (before.DisplayName == after.DisplayName && before.ExternalId == after.ExternalId) {
            return;
        }

        var value = new JsonObject { ["id"] = current.Id, ["displayName"] = after.DisplayName };
        if (before.ExternalId != after.ExternalId && after.ExternalId is not null) {
            value["externalId"] = after.ExternalId;
        }

        await client.PatchGroupAsync(current.Id, ScimPatch.Request(ScimPatch.Operation("replace", null, value)), cancellationToken);
    }

    public override async Task AddMembersAsync(
        ScimClient client, ScimGroup group, IReadOnlyList<ScimMember> members, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(members);

        var value = new JsonArray([.. members.Select(m => Member(m, withDisplay: true))]);
        await client.PatchGroupAsync(group.Id, ScimPatch.Request(ScimPatch.Operation("add", "members", value)), cancellationToken);
    }

    public override async Task RemoveMembersAsync(
        ScimClient client, ScimGroup group, IReadOnlyList<string> memberIds, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(memberIds);

        var operations = memberIds.Select(id => ScimPatch.Operation("remove", $"members[value eq {ScimFilterText.Quote(id)}]", null));
        await client.PatchGroupAsync(group.Id, ScimPatch.Request(operations), cancellationToken);
    }

    protected override async Task<ScimUser?> FindUserAsync(ScimClient client, string userName, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(client);

        var query = new ScimQuery { Filter = ScimFilterText.Eq("userName", userName), StartIndex = 1, Count = 100 };
        var found = await client.ListUsersAsync(query, cancellationToken);
        return found.First;
    }

    /// <summary>An id shaped like Okta's own - a prefix naming the kind of object and seventeen letters and digits.</summary>
    /// <param name="prefix">The prefix: <c>00u</c> for a user, <c>00g</c> for a group.</param>
    private static string OktaId(string prefix) {
        return prefix + RandomNumberGenerator.GetString(ALPHABET, 17);
    }
}
