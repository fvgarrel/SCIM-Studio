using System.Text.Json.Nodes;
using ScimStudio.DemoServer.Schema;

namespace ScimStudio.DemoServer.Http;

/// <summary>
/// The <c>attributes</c> and <c>excludedAttributes</c> of a request (RFC 7644 section 3.4.2.5), applied to every resource an
/// answer holds.
/// </summary>
internal sealed class Projection {
    private readonly ResourceType _type;
    private readonly List<AttributePath> _attributes;
    private readonly List<AttributePath> _excludedAttributes;

    private Projection(ResourceType type, List<AttributePath> attributes, List<AttributePath> excludedAttributes) {
        _type = type;
        _attributes = attributes;
        _excludedAttributes = excludedAttributes;
    }

    /// <summary>Reads both comma-separated lists; names the resource type does not have are ignored.</summary>
    /// <param name="type">The type of the resources to trim.</param>
    /// <param name="attributes">The attributes to return, or null for all.</param>
    /// <param name="excludedAttributes">The attributes to leave out, or null for none.</param>
    public static Projection Parse(ResourceType type, string? attributes, string? excludedAttributes) {
        return new Projection(type, Resolve(type, attributes), Resolve(type, excludedAttributes));
    }

    /// <summary>Trims a resource in place, which is a copy rendered for this answer.</summary>
    /// <param name="resource">The resource as a client would see it in full.</param>
    public JsonObject Apply(JsonObject resource) {
        if (_attributes.Count > 0) {
            Prune(resource, null, true, _attributes);
        }
        if (_excludedAttributes.Count > 0) {
            Prune(resource, null, false, _excludedAttributes);
        }
        return resource;
    }

    /// <summary>
    /// Keeps only what the paths name (<paramref name="keep"/>) or removes what they name. <c>schemas</c> and attributes
    /// returned always (<c>id</c>) stay either way; <c>meta</c> is returned only when asked for, like any other attribute.
    /// </summary>
    private void Prune(JsonObject container, string? extension, bool keep, List<AttributePath> paths) {
        foreach (var (key, value) in container.ToList()) {
            var path = _type.Resolve(extension is null ? key : $"{extension}:{key}");
            if (path is null || path.Attribute.Returned == Returned.Always) {
                continue;
            }
            var attribute = path.Attribute;
            if (paths.Any(candidate => candidate.Extension == extension && candidate.Attribute == attribute && candidate.SubAttribute is null)) {
                if (!keep) {
                    container.Remove(key);
                }
                continue;
            }
            if (extension is null && value is JsonObject extensionObject && _type.Extensions.Any(schema => schema.Id == key)) {
                Prune(extensionObject, key, keep, paths);
                continue;
            }
            var subAttributes = paths
                .Where(candidate => candidate.Extension == extension && candidate.Attribute == attribute && candidate.SubAttribute is not null)
                .Select(candidate => candidate.SubAttribute!.Name)
                .ToHashSet(StringComparer.Ordinal);
            if (subAttributes.Count == 0) {
                if (keep) {
                    container.Remove(key);
                }
                continue;
            }
            foreach (var element in JsonNodes.Items(value).OfType<JsonObject>()) {
                foreach (var (name, _) in element.ToList()) {
                    if (subAttributes.Contains(name) != keep) {
                        element.Remove(name);
                    }
                }
            }
        }
        JsonNodes.RemoveEmpty(container);
    }

    private static List<AttributePath> Resolve(ResourceType type, string? names) {
        if (string.IsNullOrWhiteSpace(names)) {
            return [];
        }
        var entries = names.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        return [.. entries.Select(type.Resolve).OfType<AttributePath>()];
    }
}
