using System.Text.Json.Nodes;
using ScimStudio.Core.Scim;

namespace ScimStudio.Core.Dialects;

/// <summary>
/// RFC 7644 as written: lowercase operations, real booleans, a path on every operation, and a new group's members in the POST that creates it.
/// A change of profile is a PATCH of exactly the attributes that changed.
/// </summary>
public sealed class Rfc7644Dialect : ScimDialect {
    public override DialectKind Kind => DialectKind.Rfc7644;

    public override JsonObject UserResource(UserDraft draft) {
        ArgumentNullException.ThrowIfNull(draft);

        var resource = new JsonObject { ["schemas"] = new JsonArray(ScimSchemas.USER) };
        if (draft.ExternalId is not null) {
            resource["externalId"] = draft.ExternalId;
        }

        resource["userName"] = draft.UserName;
        if (Name(draft) is { } name) {
            resource["name"] = name;
        }

        if (draft.DisplayName is not null) {
            resource["displayName"] = draft.DisplayName;
        }

        if (draft.Email is not null) {
            resource["emails"] = WorkEmail(draft.Email);
        }

        resource["active"] = draft.Active;
        return resource;
    }

    public override async Task<ScimUser> UpdateUserAsync(ScimClient client, ScimUser current, UserDraft draft, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(draft);

        var before = UserDraft.From(current).Normalized();
        var after = draft.Normalized();

        var operations = new List<JsonObject>();
        Change(operations, "userName", before.UserName, after.UserName);
        Change(operations, "externalId", before.ExternalId, after.ExternalId);
        Change(operations, "name.givenName", before.GivenName, after.GivenName);
        Change(operations, "name.familyName", before.FamilyName, after.FamilyName);
        Change(operations, "displayName", before.DisplayName, after.DisplayName);

        if (!string.Equals(before.Email, after.Email, StringComparison.Ordinal)) {
            operations.Add(after.Email is null
                ? ScimPatch.Operation("remove", "emails", null)
                : ScimPatch.Operation("replace", "emails", WorkEmail(after.Email)));
        }

        if (before.Active != after.Active) {
            operations.Add(ScimPatch.Operation("replace", "active", after.Active));
        }

        return operations.Count == 0 ? current : await client.PatchUserAsync(current.Id, ScimPatch.Request(operations), cancellationToken);
    }

    public override Task<ScimUser> SetActiveAsync(ScimClient client, ScimUser user, bool active, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(user);
        return client.PatchUserAsync(user.Id, ScimPatch.Request(ScimPatch.Operation("replace", "active", active)), cancellationToken);
    }

    public override JsonObject GroupResource(GroupDraft draft) {
        ArgumentNullException.ThrowIfNull(draft);

        var resource = new JsonObject { ["schemas"] = new JsonArray(ScimSchemas.GROUP) };
        if (draft.ExternalId is not null) {
            resource["externalId"] = draft.ExternalId;
        }

        resource["displayName"] = draft.DisplayName;
        resource["members"] = new JsonArray([.. draft.Members.Select(m => Member(m))]);
        return resource;
    }

    public override async Task UpdateGroupAsync(ScimClient client, ScimGroup current, GroupDraft draft, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(draft);

        var before = GroupDraft.From(current).Normalized();
        var after = draft.Normalized();

        var operations = new List<JsonObject>();
        Change(operations, "displayName", before.DisplayName, after.DisplayName);
        Change(operations, "externalId", before.ExternalId, after.ExternalId);

        if (operations.Count > 0) {
            await client.PatchGroupAsync(current.Id, ScimPatch.Request(operations), cancellationToken);
        }
    }

    public override async Task AddMembersAsync(
        ScimClient client, ScimGroup group, IReadOnlyList<ScimMember> members, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(members);

        var value = new JsonArray([.. members.Select(m => Member(m))]);
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

    private static void Change(List<JsonObject> operations, string path, string? before, string? after) {
        if (string.Equals(before, after, StringComparison.Ordinal)) {
            return;
        }

        operations.Add(after is null ? ScimPatch.Operation("remove", path, null) : ScimPatch.Operation("replace", path, after));
    }
}
