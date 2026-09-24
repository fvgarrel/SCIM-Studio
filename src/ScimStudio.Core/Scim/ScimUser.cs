using System.Text.Json.Nodes;

namespace ScimStudio.Core.Scim;

/// <summary>A group a user is in, as the user's read-only <c>groups</c> attribute lists it.</summary>
/// <param name="Id">The group's id.</param>
/// <param name="Display">The group's name, if the server sent it.</param>
public sealed record ScimGroupReference(string Id, string? Display);

/// <summary>
/// A User resource as far as this tool edits one, with the resource itself beside it for everything else. Of the emails, the one shown is the
/// primary, else the work one, else the first - the one an identity provider would have set.
/// </summary>
public sealed record ScimUser {
    public required string Id { get; init; }

    public string? ExternalId { get; init; }

    public string UserName { get; init; } = string.Empty;

    public string? GivenName { get; init; }

    public string? FamilyName { get; init; }

    public string? DisplayName { get; init; }

    public string? Email { get; init; }

    /// <summary>Whether the account is switched on; null when the server did not say.</summary>
    public bool? Active { get; init; }

    public IReadOnlyList<ScimGroupReference> Groups { get; init; } = [];

    public DateTimeOffset? Created { get; init; }

    public DateTimeOffset? LastModified { get; init; }

    public string? Location { get; init; }

    public required JsonObject Resource { get; init; }

    /// <summary>The name to show: the display name, else the name's parts, else the userName.</summary>
    public string Label {
        get {
            if (!string.IsNullOrWhiteSpace(DisplayName)) {
                return DisplayName;
            }

            var parts = string.Join(' ', new[] { GivenName, FamilyName }.Where(p => !string.IsNullOrWhiteSpace(p)));
            return parts.Length > 0 ? parts : UserName;
        }
    }

    /// <summary>Reads a User resource.</summary>
    /// <param name="resource">The resource as the server sent it.</param>
    public static ScimUser Read(JsonObject resource) {
        ArgumentNullException.ThrowIfNull(resource);

        var name = ScimJson.Get(resource, "name");
        var meta = ScimJson.Get(resource, "meta");

        return new ScimUser {
            Id = ScimJson.Text(ScimJson.Get(resource, "id")) ?? string.Empty,
            ExternalId = ScimJson.Text(ScimJson.Get(resource, "externalId")),
            UserName = ScimJson.Text(ScimJson.Get(resource, "userName")) ?? string.Empty,
            GivenName = ScimJson.Text(ScimJson.Get(name, "givenName")),
            FamilyName = ScimJson.Text(ScimJson.Get(name, "familyName")),
            DisplayName = ScimJson.Text(ScimJson.Get(resource, "displayName")),
            Email = PrimaryEmail(resource),
            Active = ScimJson.Flag(ScimJson.Get(resource, "active")),
            Groups = [.. ScimJson.Items(ScimJson.Get(resource, "groups"))
                .Select(g => (Id: ScimJson.Text(ScimJson.Get(g, "value")), Display: ScimJson.Text(ScimJson.Get(g, "display"))))
                .Where(g => g.Id is not null)
                .Select(g => new ScimGroupReference(g.Id!, g.Display))],
            Created = ScimJson.Date(ScimJson.Get(meta, "created")),
            LastModified = ScimJson.Date(ScimJson.Get(meta, "lastModified")),
            Location = ScimJson.Text(ScimJson.Get(meta, "location")),
            Resource = resource,
        };
    }

    private static string? PrimaryEmail(JsonObject resource) {
        var emails = ScimJson.Items(ScimJson.Get(resource, "emails")).OfType<JsonObject>().ToList();
        var chosen = emails.Find(e => ScimJson.Flag(ScimJson.Get(e, "primary")) == true)
            ?? emails.Find(e => string.Equals(ScimJson.Text(ScimJson.Get(e, "type")), "work", StringComparison.OrdinalIgnoreCase))
            ?? emails.FirstOrDefault();

        return ScimJson.Text(ScimJson.Get(chosen, "value"));
    }
}

/// <summary>What a person fills in to create or change a user; empty text counts as not set.</summary>
public sealed record UserDraft {
    public string UserName { get; init; } = string.Empty;

    public string? ExternalId { get; init; }

    public string? GivenName { get; init; }

    public string? FamilyName { get; init; }

    public string? DisplayName { get; init; }

    public string? Email { get; init; }

    public bool Active { get; init; } = true;

    /// <summary>The draft a user is edited from.</summary>
    /// <param name="user">The user as the server has it.</param>
    public static UserDraft From(ScimUser user) {
        ArgumentNullException.ThrowIfNull(user);

        return new UserDraft {
            UserName = user.UserName,
            ExternalId = user.ExternalId,
            GivenName = user.GivenName,
            FamilyName = user.FamilyName,
            DisplayName = user.DisplayName,
            Email = user.Email,
            Active = user.Active ?? true,
        };
    }

    /// <summary>The draft with its text trimmed and empty text as null, which is what is compared and sent.</summary>
    public UserDraft Normalized() {
        return this with {
            UserName = UserName.Trim(),
            ExternalId = Clean(ExternalId),
            GivenName = Clean(GivenName),
            FamilyName = Clean(FamilyName),
            DisplayName = Clean(DisplayName),
            Email = Clean(Email),
        };
    }

    internal static string? Clean(string? text) {
        return string.IsNullOrWhiteSpace(text) ? null : text.Trim();
    }
}
