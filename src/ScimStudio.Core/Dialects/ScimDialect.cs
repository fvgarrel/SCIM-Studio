using System.Text.Json.Nodes;
using ScimStudio.Core.Scim;

namespace ScimStudio.Core.Dialects;

/// <summary>The identity providers whose way of writing requests the tool can imitate.</summary>
public enum DialectKind {
    Rfc7644,
    EntraId,
    Okta,
}

/// <summary>A resource an identity provider provisioned: created, or found under the same name and taken as it was.</summary>
/// <typeparam name="T">The resource type.</typeparam>
/// <param name="Resource">The resource.</param>
/// <param name="Matched">Whether it already existed and was matched rather than created.</param>
public sealed record Provisioned<T>(T Resource, bool Matched);

/// <summary>
/// How requests are written. Servers are tested against what identity providers actually send, and they do not agree: casing of the
/// operation, booleans as strings, a PUT where another would PATCH, paths with filters or no path at all. A dialect writes every change the
/// interface offers the way one of them does, so a server can be tried against each without the provider at hand.
/// </summary>
public abstract class ScimDialect {
    public static readonly IReadOnlyList<ScimDialect> All = [new Rfc7644Dialect(), new EntraIdDialect(), new OktaDialect()];

    public abstract DialectKind Kind { get; }

    /// <summary>Whether it looks a resource up by name before creating it, as identity providers do so as not to create it twice.</summary>
    protected virtual bool LooksUpFirst => false;

    /// <summary>Whether a new group is created empty and filled by a PATCH afterwards, rather than with its members in the POST.</summary>
    protected virtual bool FillsGroupsAfterwards => false;

    public static ScimDialect For(DialectKind kind) {
        return All.First(dialect => dialect.Kind == kind);
    }

    /// <summary>Creates a user - or matches one of the same userName, for a dialect that looks first.</summary>
    /// <param name="client">The client.</param>
    /// <param name="draft">The user as filled in.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    public async Task<Provisioned<ScimUser>> CreateUserAsync(ScimClient client, UserDraft draft, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(draft);

        var user = draft.Normalized();
        if (LooksUpFirst && await FindUserAsync(client, user.UserName, cancellationToken) is { } existing) {
            return new Provisioned<ScimUser>(existing, Matched: true);
        }

        return new Provisioned<ScimUser>(await client.CreateUserAsync(UserResource(user), cancellationToken), Matched: false);
    }

    /// <summary>The body of the POST that creates the user.</summary>
    /// <param name="draft">The user, normalized.</param>
    public abstract JsonObject UserResource(UserDraft draft);

    /// <summary>Changes a user to the draft, sending nothing when nothing changed.</summary>
    /// <param name="client">The client.</param>
    /// <param name="current">The user as the server has it.</param>
    /// <param name="draft">The user as edited.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    public abstract Task<ScimUser> UpdateUserAsync(ScimClient client, ScimUser current, UserDraft draft, CancellationToken cancellationToken);

    /// <summary>Switches a user on or off - the one change identity providers send most.</summary>
    /// <param name="client">The client.</param>
    /// <param name="user">The user.</param>
    /// <param name="active">Whether the user is to be active.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public abstract Task<ScimUser> SetActiveAsync(ScimClient client, ScimUser user, bool active, CancellationToken cancellationToken);

    public virtual Task DeleteUserAsync(ScimClient client, ScimUser user, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(user);
        return client.DeleteUserAsync(user.Id, cancellationToken);
    }

    /// <summary>Creates a group with its first members - or matches one of the same name, for a dialect that looks first.</summary>
    /// <param name="client">The client.</param>
    /// <param name="draft">The group as filled in.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    public async Task<Provisioned<ScimGroup>> CreateGroupAsync(ScimClient client, GroupDraft draft, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(draft);

        var group = draft.Normalized();
        if (LooksUpFirst && await FindGroupAsync(client, group.DisplayName, cancellationToken) is { } existing) {
            return new Provisioned<ScimGroup>(existing, Matched: true);
        }

        if (!FillsGroupsAfterwards || group.Members.Count == 0) {
            return new Provisioned<ScimGroup>(await client.CreateGroupAsync(GroupResource(group), cancellationToken), Matched: false);
        }

        var created = await client.CreateGroupAsync(GroupResource(group with { Members = [] }), cancellationToken);
        await AddMembersAsync(client, created, group.Members, cancellationToken);
        return new Provisioned<ScimGroup>(await client.GetGroupAsync(created.Id, cancellationToken), Matched: false);
    }

    /// <summary>The body of the POST that creates the group.</summary>
    /// <param name="draft">The group, normalized.</param>
    public abstract JsonObject GroupResource(GroupDraft draft);

    /// <summary>Renames a group or changes its externalId, sending nothing when neither changed.</summary>
    /// <param name="client">The client.</param>
    /// <param name="current">The group as the server has it.</param>
    /// <param name="draft">The group as edited.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public abstract Task UpdateGroupAsync(ScimClient client, ScimGroup current, GroupDraft draft, CancellationToken cancellationToken);

    /// <summary>Adds users to a group.</summary>
    /// <param name="client">The client.</param>
    /// <param name="group">The group.</param>
    /// <param name="members">The users to add.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public abstract Task AddMembersAsync(ScimClient client, ScimGroup group, IReadOnlyList<ScimMember> members, CancellationToken cancellationToken);

    /// <summary>Takes users out of a group.</summary>
    /// <param name="client">The client.</param>
    /// <param name="group">The group.</param>
    /// <param name="memberIds">The ids of the users to take out.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public abstract Task RemoveMembersAsync(ScimClient client, ScimGroup group, IReadOnlyList<string> memberIds, CancellationToken cancellationToken);

    public virtual Task DeleteGroupAsync(ScimClient client, ScimGroup group, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(group);
        return client.DeleteGroupAsync(group.Id, cancellationToken);
    }

    protected virtual async Task<ScimUser?> FindUserAsync(ScimClient client, string userName, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(client);
        var found = await client.ListUsersAsync(new ScimQuery { Filter = ScimFilterText.Eq("userName", userName) }, cancellationToken);
        return found.First;
    }

    protected virtual async Task<ScimGroup?> FindGroupAsync(ScimClient client, string displayName, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(client);
        var query = new ScimQuery { Filter = ScimFilterText.Eq("displayName", displayName), ExcludedAttributes = ["members"] };
        var found = await client.ListGroupsAsync(query, cancellationToken);
        return found.First;
    }

    /// <summary>The single work address every dialect writes; the primary one, since there is only one.</summary>
    /// <param name="email">The address.</param>
    protected static JsonArray WorkEmail(string email) {
        return [new JsonObject { ["value"] = email, ["type"] = "work", ["primary"] = true }];
    }

    /// <summary>The name's parts as a <c>name</c> object, or null when neither is set.</summary>
    /// <param name="draft">The user.</param>
    /// <param name="formatted">Whether to add <c>formatted</c>, as Entra ID does.</param>
    protected static JsonObject? Name(UserDraft draft, bool formatted = false) {
        ArgumentNullException.ThrowIfNull(draft);

        if (draft.GivenName is null && draft.FamilyName is null) {
            return null;
        }

        var name = new JsonObject();
        if (formatted) {
            name["formatted"] = string.Join(' ', new[] { draft.GivenName, draft.FamilyName }.OfType<string>());
        }

        if (draft.FamilyName is not null) {
            name["familyName"] = draft.FamilyName;
        }

        if (draft.GivenName is not null) {
            name["givenName"] = draft.GivenName;
        }

        return name;
    }

    /// <summary>A member as a PATCH value lists it: its id, and its name where the dialect sends one.</summary>
    /// <param name="member">The member.</param>
    /// <param name="withDisplay">Whether to send the name.</param>
    protected static JsonObject Member(ScimMember member, bool withDisplay = false) {
        ArgumentNullException.ThrowIfNull(member);

        var element = new JsonObject { ["value"] = member.Id };
        if (withDisplay && member.Display is not null) {
            element["display"] = member.Display;
        }

        return element;
    }
}
