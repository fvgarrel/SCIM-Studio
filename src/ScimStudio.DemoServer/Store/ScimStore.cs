using System.Globalization;
using System.Text.Json.Nodes;
using ScimStudio.DemoServer.Patching;
using ScimStudio.DemoServer.Schema;

namespace ScimStudio.DemoServer.Store;

/// <summary>
/// The users and groups the server keeps, in memory and in creation order. One lock guards all of it; a demo server has no
/// load worth spreading. What leaves the store is a rendered copy, so callers never share a node with it.
/// </summary>
internal sealed class ScimStore(TimeProvider time) {
    private const string MEMBERS = "members";

    // The stored meta.version is the revision: 1 on creation, one more with each change to what the store keeps. Derived
    // attributes are not kept, so a user whose groups change keeps its version.
    private const string VERSION = "version";

    private readonly Lock _lock = new();
    private readonly OrderedDictionary<string, JsonObject> _users = new(StringComparer.Ordinal);
    private readonly OrderedDictionary<string, JsonObject> _groups = new(StringComparer.Ordinal);

    public static string Timestamp(DateTimeOffset instant) {
        return instant.UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
    }

    /// <summary>The entity tag of a stored resource, which its meta.version shows and If-Match is compared with.</summary>
    /// <param name="resource">The resource in stored form.</param>
    public static string Version(JsonObject resource) {
        return EntityTag.Weak(resource["meta"]![VERSION]!.GetValue<int>());
    }

    public JsonObject Create(ResourceType type, JsonObject representation, string baseUrl) {
        var resource = new JsonObject();
        PatchEngine.Write(type, resource, representation);
        lock (_lock) {
            var id = Guid.NewGuid().ToString();
            var now = Timestamp(time.GetUtcNow());
            resource["id"] = id;
            resource["meta"] = new JsonObject { ["resourceType"] = type.Name, ["created"] = now, ["lastModified"] = now, [VERSION] = 1 };
            Validate(type, id, resource);
            Resources(type).Add(id, resource);
            return Renderer(baseUrl).Render(type, resource);
        }
    }

    public JsonObject Get(ResourceType type, string id, string baseUrl) {
        lock (_lock) {
            return Renderer(baseUrl).Render(type, Find(type, id));
        }
    }

    public List<JsonObject> GetAll(ResourceType type, string baseUrl) {
        lock (_lock) {
            var renderer = Renderer(baseUrl);
            return [.. Resources(type).Values.Select(resource => renderer.Render(type, resource))];
        }
    }

    /// <summary>PUT: the representation replaces every attribute a client can write; id and meta.created stay.</summary>
    /// <param name="type">The type of the resource.</param>
    /// <param name="id">The id of the resource.</param>
    /// <param name="representation">The body of the request.</param>
    /// <param name="ifMatch">The versions the resource has to be at, as If-Match lists them; null for any.</param>
    /// <param name="baseUrl">The SCIM base URL, for the locations in the answer.</param>
    public JsonObject Replace(ResourceType type, string id, JsonObject representation, string? ifMatch, string baseUrl) {
        var replacement = new JsonObject();
        PatchEngine.Write(type, replacement, representation);
        lock (_lock) {
            var current = Find(type, id);
            CheckVersion(type, id, current, ifMatch);
            replacement["id"] = id;
            replacement["meta"] = current["meta"]!.DeepClone();
            return Commit(type, id, current, replacement, baseUrl);
        }
    }

    /// <summary>PATCH: applies the operations to a copy, which replaces the resource only when all of them succeed.</summary>
    /// <param name="type">The type of the resource.</param>
    /// <param name="id">The id of the resource.</param>
    /// <param name="operations">The operations of the request.</param>
    /// <param name="ifMatch">The versions the resource has to be at, as If-Match lists them; null for any.</param>
    /// <param name="baseUrl">The SCIM base URL, for the locations in the answer.</param>
    public JsonObject Patch(ResourceType type, string id, IReadOnlyList<PatchOperation> operations, string? ifMatch, string baseUrl) {
        lock (_lock) {
            var current = Find(type, id);
            CheckVersion(type, id, current, ifMatch);
            var patched = current.DeepClone().AsObject();
            PatchEngine.Apply(type, patched, operations);
            return Commit(type, id, current, patched, baseUrl);
        }
    }

    /// <summary>Deletes a resource; a deleted user also leaves every group it was a member of.</summary>
    /// <param name="type">The type of the resource.</param>
    /// <param name="id">The id of the resource.</param>
    /// <param name="ifMatch">The versions the resource has to be at, as If-Match lists them; null for any.</param>
    public void Delete(ResourceType type, string id, string? ifMatch) {
        lock (_lock) {
            CheckVersion(type, id, Find(type, id), ifMatch);
            Resources(type).Remove(id);
            if (type != ResourceType.User) {
                return;
            }
            foreach (var group in _groups.Values) {
                if (group[MEMBERS] is not JsonArray members) {
                    continue;
                }
                var removed = members.OfType<JsonObject>().Where(member => JsonNodes.AsString(member[ScimAttribute.VALUE]) == id).ToList();
                foreach (var member in removed) {
                    members.Remove(member);
                }
                if (removed.Count > 0) {
                    JsonNodes.RemoveEmpty(group);
                    Touch(group);
                }
            }
        }
    }

    /// <summary>Adds a resource as it is, with its own id and timestamps, at the first revision; for seed data.</summary>
    /// <param name="type">The type of the resource.</param>
    /// <param name="resource">The resource in stored form.</param>
    public void Import(ResourceType type, JsonObject resource) {
        resource["meta"]![VERSION] = 1;
        lock (_lock) {
            Resources(type).Add(resource["id"]!.GetValue<string>(), resource);
        }
    }

    private JsonObject Commit(ResourceType type, string id, JsonObject current, JsonObject updated, string baseUrl) {
        Validate(type, id, updated);
        if (!JsonNode.DeepEquals(current, updated)) {
            Touch(updated);
            Resources(type)[id] = updated;
        }
        return Renderer(baseUrl).Render(type, Resources(type)[id]);
    }

    /// <summary>Records a change to a resource: a new lastModified and the next revision.</summary>
    private void Touch(JsonObject resource) {
        var meta = resource["meta"]!;
        meta["lastModified"] = Timestamp(time.GetUtcNow());
        meta[VERSION] = meta[VERSION]!.GetValue<int>() + 1;
    }

    /// <summary>
    /// Refuses a write whose If-Match does not name the version the resource is at (RFC 7232 section 3.1). The check runs under
    /// the lock, so no other write can come between it and the change.
    /// </summary>
    private static void CheckVersion(ResourceType type, string id, JsonObject current, string? ifMatch) {
        if (ifMatch is null) {
            return;
        }
        var version = Version(current);
        if (!EntityTag.Matches(ifMatch, version)) {
            throw ScimException.PreconditionFailed($"The version of {type.Name} {id} is {version}, not {ifMatch}.");
        }
    }

    /// <summary>
    /// Holds a resource about to be stored to what its schema asks: required attributes set, unique ones unique. Fills in the
    /// defaults a client may leave out and, for a group, keeps only members that exist.
    /// </summary>
    private void Validate(ResourceType type, string id, JsonObject resource) {
        foreach (var attribute in type.Schema.Attributes) {
            var value = JsonNodes.AsString(resource[attribute.Name]);
            // The required attributes (userName, displayName) are strings, and blank counts as missing
            if (attribute.Required && string.IsNullOrWhiteSpace(value)) {
                throw ScimException.BadRequest(ScimException.INVALID_VALUE, $"'{attribute.Name}' is required.");
            }
            if (attribute.Uniqueness != Uniqueness.Server || value is null) {
                continue;
            }
            foreach (var (otherId, other) in Resources(type)) {
                if (otherId != id && string.Equals(JsonNodes.AsString(other[attribute.Name]), value, attribute.Comparison)) {
                    throw ScimException.Conflict($"{type.Name} {otherId} already has the {attribute.Name} \"{value}\".");
                }
            }
        }
        if (type == ResourceType.User && resource["active"] is null) {
            resource["active"] = true;
        }
        if (type == ResourceType.Group) {
            KeepKnownMembers(resource);
        }
    }

    /// <summary>
    /// Keeps the members that are users the server knows, each once, as bare references; the rest of a member is derived when
    /// the group is read. Unknown members are dropped rather than refused, as real providers do.
    /// </summary>
    private void KeepKnownMembers(JsonObject group) {
        var ids = JsonNodes.Items(group[MEMBERS])
            .OfType<JsonObject>()
            .Select(member => JsonNodes.AsString(member[ScimAttribute.VALUE]))
            .OfType<string>()
            .Where(_users.ContainsKey)
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (ids.Count == 0) {
            group.Remove(MEMBERS);
        } else {
            group[MEMBERS] = new JsonArray([.. ids.Select(memberId => new JsonObject { [ScimAttribute.VALUE] = memberId })]);
        }
    }

    private OrderedDictionary<string, JsonObject> Resources(ResourceType type) {
        return type == ResourceType.User ? _users : _groups;
    }

    private JsonObject Find(ResourceType type, string id) {
        return Resources(type).TryGetValue(id, out var resource) ? resource : throw NotFound(type, id);
    }

    private ResourceRenderer Renderer(string baseUrl) {
        return new ResourceRenderer(baseUrl, _users, _groups.Values);
    }

    private static ScimException NotFound(ResourceType type, string id) {
        return ScimException.NotFound($"There is no {type.Name} with the id \"{id}\".");
    }
}
