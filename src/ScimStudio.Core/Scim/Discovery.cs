using System.Text.Json.Nodes;

namespace ScimStudio.Core.Scim;

/// <summary>An authentication scheme the service provider says it accepts.</summary>
/// <param name="Type">The scheme's type, e.g. <c>oauthbearertoken</c>.</param>
/// <param name="Name">Its name.</param>
/// <param name="Description">What the server says about it.</param>
public sealed record AuthenticationScheme(string? Type, string? Name, string? Description);

/// <summary>What a service provider says it supports (RFC 7643 section 5).</summary>
public sealed record ServiceProviderConfig {
    public bool PatchSupported { get; init; }

    public bool BulkSupported { get; init; }

    public int? BulkMaxOperations { get; init; }

    public int? BulkMaxPayloadSize { get; init; }

    public bool FilterSupported { get; init; }

    public int? FilterMaxResults { get; init; }

    public bool ChangePasswordSupported { get; init; }

    public bool SortSupported { get; init; }

    public bool EtagSupported { get; init; }

    public string? DocumentationUri { get; init; }

    public IReadOnlyList<AuthenticationScheme> AuthenticationSchemes { get; init; } = [];

    public required JsonObject Document { get; init; }

    /// <summary>Reads the configuration document.</summary>
    /// <param name="document">The document as the server sent it.</param>
    public static ServiceProviderConfig Read(JsonObject document) {
        ArgumentNullException.ThrowIfNull(document);

        var bulk = ScimJson.Get(document, "bulk");
        var filter = ScimJson.Get(document, "filter");

        return new ServiceProviderConfig {
            PatchSupported = Supported(document, "patch"),
            BulkSupported = Supported(document, "bulk"),
            BulkMaxOperations = ScimJson.Number(ScimJson.Get(bulk, "maxOperations")),
            BulkMaxPayloadSize = ScimJson.Number(ScimJson.Get(bulk, "maxPayloadSize")),
            FilterSupported = Supported(document, "filter"),
            FilterMaxResults = ScimJson.Number(ScimJson.Get(filter, "maxResults")),
            ChangePasswordSupported = Supported(document, "changePassword"),
            SortSupported = Supported(document, "sort"),
            EtagSupported = Supported(document, "etag"),
            DocumentationUri = ScimJson.Text(ScimJson.Get(document, "documentationUri")),
            AuthenticationSchemes = [.. ScimJson.Items(ScimJson.Get(document, "authenticationSchemes")).Select(s => new AuthenticationScheme(
                ScimJson.Text(ScimJson.Get(s, "type")), ScimJson.Text(ScimJson.Get(s, "name")), ScimJson.Text(ScimJson.Get(s, "description"))))],
            Document = document,
        };
    }

    private static bool Supported(JsonObject document, string feature) {
        return ScimJson.Flag(ScimJson.Get(ScimJson.Get(document, feature), "supported")) == true;
    }
}

/// <summary>A resource type (RFC 7643 section 6): where its resources live and which schemas describe them.</summary>
public sealed record ResourceTypeInfo {
    public string Id { get; init; } = string.Empty;

    public string Name { get; init; } = string.Empty;

    public string Endpoint { get; init; } = string.Empty;

    public string? Description { get; init; }

    public string Schema { get; init; } = string.Empty;

    public IReadOnlyList<string> SchemaExtensions { get; init; } = [];

    public required JsonObject Document { get; init; }

    /// <summary>Reads a ResourceType document.</summary>
    /// <param name="document">The document as the server sent it.</param>
    public static ResourceTypeInfo Read(JsonObject document) {
        ArgumentNullException.ThrowIfNull(document);

        return new ResourceTypeInfo {
            Id = ScimJson.Text(ScimJson.Get(document, "id")) ?? string.Empty,
            Name = ScimJson.Text(ScimJson.Get(document, "name")) ?? string.Empty,
            Endpoint = ScimJson.Text(ScimJson.Get(document, "endpoint")) ?? string.Empty,
            Description = ScimJson.Text(ScimJson.Get(document, "description")),
            Schema = ScimJson.Text(ScimJson.Get(document, "schema")) ?? string.Empty,
            SchemaExtensions = [.. ScimJson.Items(ScimJson.Get(document, "schemaExtensions"))
                .Select(e => ScimJson.Text(ScimJson.Get(e, "schema")))
                .OfType<string>()],
            Document = document,
        };
    }
}

/// <summary>An attribute as a schema describes it (RFC 7643 section 7).</summary>
public sealed record AttributeDefinition {
    public string Name { get; init; } = string.Empty;

    public string Type { get; init; } = "string";

    public bool MultiValued { get; init; }

    public bool Required { get; init; }

    public bool CaseExact { get; init; }

    public string Mutability { get; init; } = "readWrite";

    public string Returned { get; init; } = "default";

    public string Uniqueness { get; init; } = "none";

    public string? Description { get; init; }

    public IReadOnlyList<string> CanonicalValues { get; init; } = [];

    public IReadOnlyList<AttributeDefinition> SubAttributes { get; init; } = [];

    /// <summary>Reads an attribute definition, its sub-attributes included.</summary>
    /// <param name="node">The definition as the server sent it.</param>
    public static AttributeDefinition Read(JsonNode? node) {
        return new AttributeDefinition {
            Name = ScimJson.Text(ScimJson.Get(node, "name")) ?? string.Empty,
            Type = ScimJson.Text(ScimJson.Get(node, "type")) ?? "string",
            MultiValued = ScimJson.Flag(ScimJson.Get(node, "multiValued")) == true,
            Required = ScimJson.Flag(ScimJson.Get(node, "required")) == true,
            CaseExact = ScimJson.Flag(ScimJson.Get(node, "caseExact")) == true,
            Mutability = ScimJson.Text(ScimJson.Get(node, "mutability")) ?? "readWrite",
            Returned = ScimJson.Text(ScimJson.Get(node, "returned")) ?? "default",
            Uniqueness = ScimJson.Text(ScimJson.Get(node, "uniqueness")) ?? "none",
            Description = ScimJson.Text(ScimJson.Get(node, "description")),
            CanonicalValues = [.. ScimJson.Items(ScimJson.Get(node, "canonicalValues")).Select(ScimJson.Text).OfType<string>()],
            SubAttributes = [.. ScimJson.Items(ScimJson.Get(node, "subAttributes")).Select(Read)],
        };
    }
}

/// <summary>A schema (RFC 7643 section 7): the attributes a resource type or an extension has.</summary>
public sealed record SchemaDefinition {
    public string Id { get; init; } = string.Empty;

    public string? Name { get; init; }

    public string? Description { get; init; }

    public IReadOnlyList<AttributeDefinition> Attributes { get; init; } = [];

    public required JsonObject Document { get; init; }

    /// <summary>Reads a Schema document.</summary>
    /// <param name="document">The document as the server sent it.</param>
    public static SchemaDefinition Read(JsonObject document) {
        ArgumentNullException.ThrowIfNull(document);

        return new SchemaDefinition {
            Id = ScimJson.Text(ScimJson.Get(document, "id")) ?? string.Empty,
            Name = ScimJson.Text(ScimJson.Get(document, "name")),
            Description = ScimJson.Text(ScimJson.Get(document, "description")),
            Attributes = [.. ScimJson.Items(ScimJson.Get(document, "attributes")).Select(AttributeDefinition.Read)],
            Document = document,
        };
    }
}
