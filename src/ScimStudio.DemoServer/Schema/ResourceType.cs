namespace ScimStudio.DemoServer.Schema;

/// <summary>A kind of resource the server keeps: its endpoint, its schemas, and how attribute paths resolve against them.</summary>
internal sealed class ResourceType {
    public static readonly ResourceType User = new("User", "/Users", "User Account", ScimSchemas.User, [ScimSchemas.EnterpriseUser]);
    public static readonly ResourceType Group = new("Group", "/Groups", "Group", ScimSchemas.Group, []);
    public static readonly IReadOnlyList<ResourceType> All = [User, Group];

    private readonly List<ScimAttribute> _extensionRoots;

    private ResourceType(string name, string endpoint, string description, ScimSchema schema, IReadOnlyList<ScimSchema> extensions) {
        Name = name;
        Endpoint = endpoint;
        Description = description;
        Schema = schema;
        Extensions = extensions;
        // An extension's attributes live in an object under its URN, so the URN itself behaves like a complex attribute:
        // `urn:...:enterprise:2.0:User` can be a PATCH path, a key of a PATCH value or an entry of `attributes`.
        _extensionRoots = [
            .. extensions.Select(extension => new ScimAttribute {
                Name = extension.Id,
                Type = AttributeType.Complex,
                Description = extension.Description,
                SubAttributes = extension.Attributes,
            }),
        ];
    }

    public string Name { get; }
    public string Endpoint { get; }
    public string Description { get; }
    public ScimSchema Schema { get; }
    public IReadOnlyList<ScimSchema> Extensions { get; }

    /// <summary>The URL of a resource of this type, which meta.location, the Location header and a bulk response carry.</summary>
    /// <param name="baseUrl">The SCIM base URL.</param>
    /// <param name="id">The id of the resource.</param>
    public string Location(string baseUrl, string id) {
        return $"{baseUrl}{Endpoint}/{id}";
    }

    /// <summary>
    /// Resolves <c>[schema URN:]attribute[.subAttribute]</c>, ignoring case (RFC 7643 section 2.1). Null when the resource
    /// type has no such attribute.
    /// </summary>
    public AttributePath? Resolve(string path) {
        var rest = path;
        ScimSchema? schema = null;
        if (path.StartsWith("urn:", StringComparison.OrdinalIgnoreCase)) {
            foreach (var extensionRoot in _extensionRoots) {
                if (string.Equals(path, extensionRoot.Name, StringComparison.OrdinalIgnoreCase)) {
                    return new AttributePath(null, extensionRoot, null);
                }
            }
            foreach (var candidate in Extensions.Prepend(Schema)) {
                if (path.Length > candidate.Id.Length && path[candidate.Id.Length] == ':'
                    && path.StartsWith(candidate.Id, StringComparison.OrdinalIgnoreCase)) {
                    schema = candidate;
                    rest = path[(candidate.Id.Length + 1)..];
                    break;
                }
            }
            if (schema is null) {
                return null;
            }
        }

        var dot = rest.IndexOf('.', StringComparison.Ordinal);
        var name = dot < 0 ? rest : rest[..dot];
        var extension = schema is null || schema == Schema ? null : schema.Id;
        var attribute = extension is null ? FindCommon(name) ?? Schema.FindAttribute(name) : schema!.FindAttribute(name);
        if (attribute is null) {
            return null;
        }
        if (dot < 0) {
            return new AttributePath(extension, attribute, null);
        }
        var subAttribute = attribute.FindSubAttribute(rest[(dot + 1)..]);
        return subAttribute is null ? null : new AttributePath(extension, attribute, subAttribute);
    }

    private static ScimAttribute? FindCommon(string name) {
        foreach (var attribute in ScimSchemas.Common) {
            if (string.Equals(attribute.Name, name, StringComparison.OrdinalIgnoreCase)) {
                return attribute;
            }
        }
        return null;
    }
}
