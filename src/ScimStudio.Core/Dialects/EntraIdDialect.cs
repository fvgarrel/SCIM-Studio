using System.Globalization;
using System.Text.Json.Nodes;
using ScimStudio.Core.Scim;

namespace ScimStudio.Core.Dialects;

/// <summary>
/// Microsoft Entra ID as its provisioning service writes requests: a lookup by name before every create, capitalised operations, booleans as
/// the strings <c>"True"</c> and <c>"False"</c>, the work address through a filtered path, new name parts as dotted keys without a path, a
/// new group created empty and filled afterwards, and members removed by naming them in the value.
/// </summary>
public sealed class EntraIdDialect : ScimDialect {
    /// <summary>The schema Entra ID lists on every group it creates; a server is expected to ignore a schema it does not know.</summary>
    public const string GROUP_SCHEMA = "http://schemas.microsoft.com/2006/11/ResourceManagement/ADSCIM/2.0/Group";

    private const string WORK_EMAIL = "emails[type eq \"work\"].value";

    public override DialectKind Kind => DialectKind.EntraId;

    protected override bool LooksUpFirst => true;

    protected override bool FillsGroupsAfterwards => true;

    public override JsonObject UserResource(UserDraft draft) {
        ArgumentNullException.ThrowIfNull(draft);

        // Entra ID always has an id of its own to send: the object id of the user in the tenant.
        var resource = new JsonObject {
            ["schemas"] = new JsonArray(ScimSchemas.USER, ScimSchemas.ENTERPRISE_USER),
            ["externalId"] = draft.ExternalId ?? Guid.NewGuid().ToString(),
            ["userName"] = draft.UserName,
            ["active"] = draft.Active,
        };

        if (draft.DisplayName is not null) {
            resource["displayName"] = draft.DisplayName;
        }

        if (draft.Email is not null) {
            resource["emails"] = WorkEmail(draft.Email);
        }

        resource["meta"] = new JsonObject { ["resourceType"] = "User" };
        if (Name(draft, formatted: true) is { } name) {
            resource["name"] = name;
        }

        resource["roles"] = new JsonArray();
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
        Change(operations, "displayName", before.DisplayName, after.DisplayName);

        // A part the user did not have yet arrives as a key in a value without a path; a changed one through its path.
        var added = new JsonObject();
        NamePart(operations, added, "name.givenName", before.GivenName, after.GivenName);
        NamePart(operations, added, "name.familyName", before.FamilyName, after.FamilyName);
        if (added.Count > 0) {
            operations.Add(ScimPatch.Operation("Add", null, added));
        }

        if (!string.Equals(before.Email, after.Email, StringComparison.Ordinal)) {
            operations.Add(after.Email is null
                ? ScimPatch.Operation("Remove", "emails[type eq \"work\"]", null)
                : ScimPatch.Operation(before.Email is null ? "Add" : "Replace", WORK_EMAIL, after.Email));
        }

        if (before.Active != after.Active) {
            operations.Add(ScimPatch.Operation("Replace", "active", Flag(after.Active)));
        }

        return operations.Count == 0 ? current : await client.PatchUserAsync(current.Id, ScimPatch.Request(operations), cancellationToken);
    }

    public override Task<ScimUser> SetActiveAsync(ScimClient client, ScimUser user, bool active, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(user);
        return client.PatchUserAsync(user.Id, ScimPatch.Request(ScimPatch.Operation("Replace", "active", Flag(active))), cancellationToken);
    }

    public override JsonObject GroupResource(GroupDraft draft) {
        ArgumentNullException.ThrowIfNull(draft);

        return new JsonObject {
            ["schemas"] = new JsonArray(ScimSchemas.GROUP, GROUP_SCHEMA),
            ["externalId"] = draft.ExternalId ?? Guid.NewGuid().ToString(),
            ["displayName"] = draft.DisplayName,
            ["meta"] = new JsonObject { ["resourceType"] = "Group" },
        };
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
        await client.PatchGroupAsync(group.Id, ScimPatch.Request(ScimPatch.Operation("Add", "members", value)), cancellationToken);
    }

    public override async Task RemoveMembersAsync(
        ScimClient client, ScimGroup group, IReadOnlyList<string> memberIds, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(group);
        ArgumentNullException.ThrowIfNull(memberIds);

        var value = new JsonArray([.. memberIds.Select(id => new JsonObject { ["value"] = id })]);
        await client.PatchGroupAsync(group.Id, ScimPatch.Request(ScimPatch.Operation("Remove", "members", value)), cancellationToken);
    }

    /// <summary>A boolean the way Entra ID has long sent one in a PATCH: as .NET writes it, in a string.</summary>
    /// <param name="value">The value.</param>
    private static string Flag(bool value) {
        return value.ToString(CultureInfo.InvariantCulture);
    }

    private static void Change(List<JsonObject> operations, string path, string? before, string? after) {
        if (string.Equals(before, after, StringComparison.Ordinal)) {
            return;
        }

        operations.Add(after is null
            ? ScimPatch.Operation("Remove", path, null)
            : ScimPatch.Operation(before is null ? "Add" : "Replace", path, after));
    }

    private static void NamePart(List<JsonObject> operations, JsonObject added, string path, string? before, string? after) {
        if (string.Equals(before, after, StringComparison.Ordinal)) {
            return;
        }

        if (before is null) {
            added[path] = after;
        } else if (after is null) {
            operations.Add(ScimPatch.Operation("Remove", path, null));
        } else {
            operations.Add(ScimPatch.Operation("Replace", path, after));
        }
    }
}
