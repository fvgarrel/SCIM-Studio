using System.Text.Json.Nodes;
using ScimStudio.DemoServer.Http;

namespace ScimStudio.DemoServer.Schema;

/// <summary>The documents a client reads to learn what the server supports (RFC 7643 sections 5 to 7).</summary>
internal static class DiscoveryDocuments {
    public static JsonObject ServiceProviderConfig(string baseUrl, int maxResults) {
        return new JsonObject {
            ["schemas"] = new JsonArray(ScimUrns.SERVICE_PROVIDER_CONFIG),
            ["patch"] = new JsonObject { ["supported"] = true },
            ["bulk"] = new JsonObject {
                ["supported"] = true,
                ["maxOperations"] = BulkJob.MAX_OPERATIONS,
                ["maxPayloadSize"] = BulkJob.MAX_PAYLOAD_SIZE,
            },
            ["filter"] = new JsonObject { ["supported"] = true, ["maxResults"] = maxResults },
            ["changePassword"] = new JsonObject { ["supported"] = false },
            ["sort"] = new JsonObject { ["supported"] = true },
            ["etag"] = new JsonObject { ["supported"] = true },
            ["authenticationSchemes"] = new JsonArray(new JsonObject {
                ["type"] = "oauthbearertoken",
                ["name"] = "Bearer token",
                ["description"] = "The token the demo server was started with, sent as \"Authorization: Bearer <token>\".",
                ["specUri"] = "https://www.rfc-editor.org/info/rfc6750",
                ["primary"] = true,
            }),
            ["meta"] = Meta("ServiceProviderConfig", $"{baseUrl}/ServiceProviderConfig"),
        };
    }

    public static JsonObject ResourceType(ResourceType type, string baseUrl) {
        var document = new JsonObject {
            ["schemas"] = new JsonArray(ScimUrns.RESOURCE_TYPE),
            ["id"] = type.Name,
            ["name"] = type.Name,
            ["endpoint"] = type.Endpoint,
            ["description"] = type.Description,
            ["schema"] = type.Schema.Id,
        };
        if (type.Extensions.Count > 0) {
            var extensions = type.Extensions.Select(extension => new JsonObject { ["schema"] = extension.Id, ["required"] = false });
            document["schemaExtensions"] = new JsonArray([.. extensions]);
        }
        document["meta"] = Meta("ResourceType", $"{baseUrl}/ResourceTypes/{type.Name}");
        return document;
    }

    public static JsonObject Schema(ScimSchema schema, string baseUrl) {
        return new JsonObject {
            ["schemas"] = new JsonArray(ScimUrns.SCHEMA),
            ["id"] = schema.Id,
            ["name"] = schema.Name,
            ["description"] = schema.Description,
            ["attributes"] = new JsonArray([.. schema.Attributes.Select(Attribute)]),
            ["meta"] = Meta("Schema", $"{baseUrl}/Schemas/{schema.Id}"),
        };
    }

    private static JsonObject Attribute(ScimAttribute attribute) {
        var document = new JsonObject {
            ["name"] = attribute.Name,
            ["type"] = TypeName(attribute.Type),
            ["multiValued"] = attribute.MultiValued,
            ["description"] = attribute.Description,
            ["required"] = attribute.Required,
        };
        if (attribute.CanonicalValues.Count > 0) {
            document["canonicalValues"] = new JsonArray([.. attribute.CanonicalValues.Select(value => (JsonNode)value)]);
        }
        if (!attribute.IsComplex) {
            document["caseExact"] = attribute.CaseExact;
        }
        document["mutability"] = attribute.Mutability switch {
            Mutability.ReadOnly => "readOnly",
            Mutability.Immutable => "immutable",
            _ => "readWrite",
        };
        document["returned"] = attribute.Returned == Returned.Always ? "always" : "default";
        document["uniqueness"] = attribute.Uniqueness == Uniqueness.Server ? "server" : "none";
        if (attribute.ReferenceTypes.Count > 0) {
            document["referenceTypes"] = new JsonArray([.. attribute.ReferenceTypes.Select(value => (JsonNode)value)]);
        }
        if (attribute.IsComplex) {
            document["subAttributes"] = new JsonArray([.. attribute.SubAttributes.Select(Attribute)]);
        }
        return document;
    }

    private static string TypeName(AttributeType type) {
        return type switch {
            AttributeType.Boolean => "boolean",
            AttributeType.DateTime => "dateTime",
            AttributeType.Reference => "reference",
            AttributeType.Complex => "complex",
            _ => "string",
        };
    }

    private static JsonObject Meta(string resourceType, string location) {
        return new JsonObject { ["resourceType"] = resourceType, ["location"] = location };
    }
}
