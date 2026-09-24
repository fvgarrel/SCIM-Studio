using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScimStudio.Core.Scim;

/// <summary>
/// Reads SCIM JSON the way RFC 7643 section 2.1 has it read: attribute names without case. Servers answer with whatever casing they like, so
/// nothing here looks a member up by its exact name.
/// </summary>
public static class ScimJson {
    /// <summary>Indented, and without escaping what a person reads anyway - an umlaut stays an umlaut in the log.</summary>
    public static readonly JsonSerializerOptions Readable = new() {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>What request bodies are written with: compact, and with text as UTF-8 rather than escapes, as identity providers send it.</summary>
    public static readonly JsonSerializerOptions Compact = new() {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>The member of that name, matched without case, or null.</summary>
    /// <param name="node">The object to look in; anything else has no members.</param>
    /// <param name="name">The member's name.</param>
    public static JsonNode? Get(JsonNode? node, string name) {
        if (node is not JsonObject found) {
            return null;
        }

        foreach (var (key, member) in found) {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase)) {
                return member;
            }
        }

        return null;
    }

    /// <summary>Whether the object has a member of that name, matched without case, even one set to null.</summary>
    /// <param name="node">The object to look in.</param>
    /// <param name="name">The member's name.</param>
    public static bool Has(JsonNode? node, string name) {
        return node is JsonObject found && found.Any(member => string.Equals(member.Key, name, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>A string value, or the text of a number; null for anything else.</summary>
    /// <param name="node">The value.</param>
    public static string? Text(JsonNode? node) {
        if (node is not JsonValue value) {
            return null;
        }

        if (value.TryGetValue<string>(out var text)) {
            return text;
        }

        return value.TryGetValue<decimal>(out var number) ? number.ToString(CultureInfo.InvariantCulture) : null;
    }

    /// <summary>A boolean, including one sent as a string as some identity providers do; null for anything else.</summary>
    /// <param name="node">The value.</param>
    public static bool? Flag(JsonNode? node) {
        if (node is not JsonValue value) {
            return null;
        }

        if (value.TryGetValue<bool>(out var flag)) {
            return flag;
        }

        return value.TryGetValue<string>(out var text) && bool.TryParse(text.Trim(), out var parsed) ? parsed : null;
    }

    /// <summary>A whole number, including one sent as a string; null for anything else.</summary>
    /// <param name="node">The value.</param>
    public static int? Number(JsonNode? node) {
        if (node is not JsonValue value) {
            return null;
        }

        if (value.TryGetValue<int>(out var number)) {
            return number;
        }

        return value.TryGetValue<string>(out var text) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed)
            ? parsed
            : null;
    }

    /// <summary>A point in time, as RFC 7643 writes a dateTime; null for anything else.</summary>
    /// <param name="node">The value.</param>
    public static DateTimeOffset? Date(JsonNode? node) {
        return Text(node) is { } text && DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var date)
            ? date
            : null;
    }

    /// <summary>The elements of a multi-valued value; a single value where a list belongs counts as a list of one.</summary>
    /// <param name="node">The value.</param>
    public static IEnumerable<JsonNode?> Items(JsonNode? node) {
        if (node is null) {
            return [];
        }

        return node is JsonArray list ? list : [node];
    }

    /// <summary>The body as a JSON object, or null when it is empty, not JSON, or JSON of another shape.</summary>
    /// <param name="text">The text.</param>
    public static JsonObject? ParseObject(string? text) {
        return Parse(text) as JsonObject;
    }

    /// <summary>The text as JSON, or null when it is empty or not JSON.</summary>
    /// <param name="text">The text.</param>
    public static JsonNode? Parse(string? text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return null;
        }

        try {
            return JsonNode.Parse(text);
        } catch (JsonException) {
            return null;
        }
    }

    /// <summary>The text indented for reading when it is JSON, and as it was otherwise.</summary>
    /// <param name="text">The text.</param>
    public static string? Pretty(string? text) {
        return Parse(text) is { } node ? node.ToJsonString(Readable) : text;
    }
}
