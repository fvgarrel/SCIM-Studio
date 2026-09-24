using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScimStudio.DemoServer;

/// <summary>Helpers for the JSON the server reads and keeps.</summary>
internal static class JsonNodes {
    /// <summary>The property whose name matches regardless of case, as SCIM names do (RFC 7643 section 2.1).</summary>
    public static JsonNode? Find(JsonObject node, string name) {
        foreach (var (key, value) in node) {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) {
                return value;
            }
        }
        return null;
    }

    /// <summary>The values of an attribute: the elements of an array, or the value itself.</summary>
    public static IEnumerable<JsonNode> Items(JsonNode? node) {
        if (node is JsonArray array) {
            return array.OfType<JsonNode>();
        }
        return node is null ? [] : [node];
    }

    /// <summary>Whether a value counts as present for the <c>pr</c> operator: not null, and not an empty string, array or object.</summary>
    public static bool HasValue(JsonNode? node) {
        return node switch {
            null => false,
            JsonArray array => array.Any(HasValue),
            JsonObject complex => complex.Any(property => HasValue(property.Value)),
            _ => node.GetValueKind() != JsonValueKind.String || node.GetValue<string>().Length > 0,
        };
    }

    public static string? AsString(JsonNode? node) {
        return node is JsonValue value && value.GetValueKind() == JsonValueKind.String ? value.GetValue<string>() : null;
    }

    public static bool IsTrue(JsonNode? node) {
        return node is JsonValue value && value.GetValueKind() == JsonValueKind.True;
    }

    public static bool TryGetInstant(JsonNode? node, out DateTimeOffset instant) {
        instant = default;
        return AsString(node) is { } text
            && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out instant);
    }

    /// <summary>
    /// Drops nulls, empty objects and empty arrays, all the way down. RFC 7643 section 2.5 counts them as unassigned, and the store
    /// keeps an unassigned attribute by leaving it out.
    /// </summary>
    public static void RemoveEmpty(JsonObject node) {
        foreach (var (key, value) in node.ToList()) {
            RemoveEmptyWithin(value);
            if (value is null or JsonObject { Count: 0 } or JsonArray { Count: 0 }) {
                node.Remove(key);
            }
        }
    }

    private static void RemoveEmptyWithin(JsonNode? node) {
        if (node is JsonObject complex) {
            RemoveEmpty(complex);
        } else if (node is JsonArray array) {
            foreach (var item in array.ToList()) {
                RemoveEmptyWithin(item);
                if (item is null or JsonObject { Count: 0 }) {
                    array.Remove(item);
                }
            }
        }
    }
}
