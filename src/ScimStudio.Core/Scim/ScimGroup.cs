using System.Text.Json.Nodes;

namespace ScimStudio.Core.Scim;

/// <summary>A member of a group as the group lists it.</summary>
/// <param name="Id">The member's id.</param>
/// <param name="Display">The member's name, if the server sent it.</param>
/// <param name="Type">What kind of member it is, <c>User</c> or <c>Group</c>, if the server said.</param>
public sealed record ScimMember(string Id, string? Display, string? Type);

/// <summary>A Group resource as far as this tool edits one, with the resource itself beside it.</summary>
public sealed record ScimGroup {
    public required string Id { get; init; }

    public string? ExternalId { get; init; }

    public string DisplayName { get; init; } = string.Empty;

    public IReadOnlyList<ScimMember> Members { get; init; } = [];

    /// <summary>Whether the answer carried <c>members</c> at all; a list read with them excluded says nothing about who is in the group.</summary>
    public bool HasMembers { get; init; }

    public DateTimeOffset? Created { get; init; }

    public DateTimeOffset? LastModified { get; init; }

    public string? Location { get; init; }

    public required JsonObject Resource { get; init; }

    /// <summary>Reads a Group resource.</summary>
    /// <param name="resource">The resource as the server sent it.</param>
    public static ScimGroup Read(JsonObject resource) {
        ArgumentNullException.ThrowIfNull(resource);

        var meta = ScimJson.Get(resource, "meta");
        var members = ScimJson.Get(resource, "members");

        return new ScimGroup {
            Id = ScimJson.Text(ScimJson.Get(resource, "id")) ?? string.Empty,
            ExternalId = ScimJson.Text(ScimJson.Get(resource, "externalId")),
            DisplayName = ScimJson.Text(ScimJson.Get(resource, "displayName")) ?? string.Empty,
            Members = [.. ScimJson.Items(members)
                .Select(m => (Id: ScimJson.Text(ScimJson.Get(m, "value")), Node: m))
                .Where(m => m.Id is not null)
                .Select(m => new ScimMember(m.Id!, ScimJson.Text(ScimJson.Get(m.Node, "display")), ScimJson.Text(ScimJson.Get(m.Node, "type"))))],
            HasMembers = ScimJson.Has(resource, "members"),
            Created = ScimJson.Date(ScimJson.Get(meta, "created")),
            LastModified = ScimJson.Date(ScimJson.Get(meta, "lastModified")),
            Location = ScimJson.Text(ScimJson.Get(meta, "location")),
            Resource = resource,
        };
    }
}

/// <summary>What a person fills in to create a group or rename one.</summary>
public sealed record GroupDraft {
    public string DisplayName { get; init; } = string.Empty;

    public string? ExternalId { get; init; }

    /// <summary>The users a new group starts with. Changing an existing group's members goes through its own requests.</summary>
    public IReadOnlyList<ScimMember> Members { get; init; } = [];

    /// <summary>The draft a group is edited from.</summary>
    /// <param name="group">The group as the server has it.</param>
    public static GroupDraft From(ScimGroup group) {
        ArgumentNullException.ThrowIfNull(group);
        return new GroupDraft { DisplayName = group.DisplayName, ExternalId = group.ExternalId, Members = group.Members };
    }

    /// <summary>The draft with its text trimmed and empty text as null.</summary>
    public GroupDraft Normalized() {
        return this with { DisplayName = DisplayName.Trim(), ExternalId = UserDraft.Clean(ExternalId) };
    }
}

/// <summary>A page of resources (RFC 7644 section 3.4.2).</summary>
/// <typeparam name="T">The resource type.</typeparam>
/// <param name="TotalResults">How many resources match, across all pages.</param>
/// <param name="StartIndex">The 1-based index of the first resource on this page.</param>
/// <param name="ItemsPerPage">How many are on this page.</param>
/// <param name="Resources">The resources on this page.</param>
public sealed record ScimPage<T>(int TotalResults, int StartIndex, int ItemsPerPage, IReadOnlyList<T> Resources) {
    /// <summary>The first resource on the page, or the default when there is none.</summary>
    public T? First => Resources.Count > 0 ? Resources[0] : default;
}

/// <summary>Reads ListResponses into pages.</summary>
public static class ScimPage {
    /// <summary>Reads a ListResponse. A server that leaves out the counts is taken at what it sent.</summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="list">The ListResponse.</param>
    /// <param name="read">Reads one resource.</param>
    public static ScimPage<T> Read<T>(JsonObject list, Func<JsonObject, T> read) {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(read);

        var resources = ScimJson.Items(ScimJson.Get(list, "Resources")).OfType<JsonObject>().Select(read).ToList();
        return new ScimPage<T>(
            ScimJson.Number(ScimJson.Get(list, "totalResults")) ?? resources.Count,
            ScimJson.Number(ScimJson.Get(list, "startIndex")) ?? 1,
            ScimJson.Number(ScimJson.Get(list, "itemsPerPage")) ?? resources.Count,
            resources);
    }
}
