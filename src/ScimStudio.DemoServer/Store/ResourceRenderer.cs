using System.Text.Json.Nodes;
using ScimStudio.DemoServer.Schema;

namespace ScimStudio.DemoServer.Store;

/// <summary>
/// Builds what a client sees from what the store keeps: attributes in schema order, <c>schemas</c>, <c>meta.location</c> and
/// <c>meta.version</c>, and the attributes derived from other resources, which are a user's groups, a group's member details
/// and a manager's name.
/// </summary>
internal sealed class ResourceRenderer(string baseUrl, IReadOnlyDictionary<string, JsonObject> users, IEnumerable<JsonObject> groups) {
    private Dictionary<string, List<JsonObject>>? _groupsByMember;

    public JsonObject Render(ResourceType type, JsonObject resource) {
        var id = resource["id"]!.GetValue<string>();
        var schemas = new JsonArray(type.Schema.Id);
        foreach (var extension in type.Extensions) {
            if (resource.ContainsKey(extension.Id)) {
                // JsonValue.Create, because Add<string> needs reflection metadata a trimmed host may not have
                schemas.Add(JsonValue.Create(extension.Id));
            }
        }

        var result = new JsonObject { ["schemas"] = schemas };
        foreach (var attribute in ScimSchemas.Common.Concat(type.Schema.Attributes)) {
            var value = attribute.Name switch {
                "meta" => null,
                "groups" => Groups(id),
                "members" => Members(resource["members"]),
                _ => Copy(attribute, resource[attribute.Name]),
            };
            if (value is not null) {
                result[attribute.Name] = value;
            }
        }
        foreach (var extension in type.Extensions) {
            if (resource[extension.Id] is not JsonObject stored) {
                continue;
            }
            var rendered = new JsonObject();
            foreach (var attribute in extension.Attributes) {
                var value = attribute.Name == "manager" ? Manager(attribute, stored[attribute.Name]) : Copy(attribute, stored[attribute.Name]);
                if (value is not null) {
                    rendered[attribute.Name] = value;
                }
            }
            result[extension.Id] = rendered;
        }

        var meta = resource["meta"]!;
        result["meta"] = new JsonObject {
            ["resourceType"] = type.Name,
            ["created"] = meta["created"]?.DeepClone(),
            ["lastModified"] = meta["lastModified"]?.DeepClone(),
            ["location"] = Location(type, id),
            ["version"] = ScimStore.Version(resource),
        };
        return result;
    }

    private string Location(ResourceType type, string id) {
        return type.Location(baseUrl, id);
    }

    /// <summary>A deep copy with sub-attributes in schema order, however PATCH operations happened to add them.</summary>
    private static JsonNode? Copy(ScimAttribute attribute, JsonNode? value) {
        if (value is JsonArray array) {
            return new JsonArray([.. array.Select(element => CopySingle(attribute, element))]);
        }
        return CopySingle(attribute, value);
    }

    private static JsonNode? CopySingle(ScimAttribute attribute, JsonNode? value) {
        if (!attribute.IsComplex || value is not JsonObject complex) {
            return value?.DeepClone();
        }
        var copy = new JsonObject();
        foreach (var subAttribute in attribute.SubAttributes) {
            if (complex[subAttribute.Name] is { } subValue) {
                copy[subAttribute.Name] = subValue.DeepClone();
            }
        }
        return copy;
    }

    private JsonArray? Groups(string userId) {
        _groupsByMember ??= IndexGroupsByMember();
        if (!_groupsByMember.TryGetValue(userId, out var memberOf)) {
            return null;
        }
        var result = new JsonArray();
        foreach (var group in memberOf) {
            var groupId = group["id"]!.GetValue<string>();
            result.Add(new JsonObject {
                ["value"] = groupId,
                ["$ref"] = Location(ResourceType.Group, groupId),
                ["display"] = group["displayName"]?.DeepClone(),
                ["type"] = "direct",
            });
        }
        return result;
    }

    private Dictionary<string, List<JsonObject>> IndexGroupsByMember() {
        var index = new Dictionary<string, List<JsonObject>>(StringComparer.Ordinal);
        foreach (var group in groups) {
            foreach (var member in JsonNodes.Items(group["members"]).OfType<JsonObject>()) {
                if (JsonNodes.AsString(member[ScimAttribute.VALUE]) is not { } userId) {
                    continue;
                }
                if (!index.TryGetValue(userId, out var memberOf)) {
                    memberOf = [];
                    index[userId] = memberOf;
                }
                memberOf.Add(group);
            }
        }
        return index;
    }

    private JsonArray? Members(JsonNode? stored) {
        var result = new JsonArray();
        foreach (var member in JsonNodes.Items(stored).OfType<JsonObject>()) {
            if (JsonNodes.AsString(member[ScimAttribute.VALUE]) is { } userId && users.TryGetValue(userId, out var user)) {
                result.Add(new JsonObject {
                    ["value"] = userId,
                    ["$ref"] = Location(ResourceType.User, userId),
                    ["display"] = DisplayName(user),
                    ["type"] = "User",
                });
            }
        }
        return result.Count == 0 ? null : result;
    }

    /// <summary>The manager, with <c>$ref</c> and <c>displayName</c> filled in when it names a user the server knows.</summary>
    private JsonObject? Manager(ScimAttribute attribute, JsonNode? stored) {
        if (CopySingle(attribute, stored) is not JsonObject manager) {
            return null;
        }
        if (JsonNodes.AsString(manager[ScimAttribute.VALUE]) is { } managerId && users.TryGetValue(managerId, out var user)) {
            manager["$ref"] = Location(ResourceType.User, managerId);
            manager["displayName"] = DisplayName(user);
        }
        return manager;
    }

    private static string? DisplayName(JsonObject user) {
        var displayName = JsonNodes.AsString(user["displayName"]);
        return string.IsNullOrEmpty(displayName) ? JsonNodes.AsString(user["userName"]) : displayName;
    }
}
