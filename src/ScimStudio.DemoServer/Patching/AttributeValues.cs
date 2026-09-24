using System.Text.Json;
using System.Text.Json.Nodes;
using ScimStudio.DemoServer.Schema;

namespace ScimStudio.DemoServer.Patching;

/// <summary>
/// Turns a value a client sent into the form the store keeps: canonical names, real booleans, and nothing read-only or unknown.
/// Leniency lives here, since identity providers stretch the RFC in well-known ways.
/// </summary>
internal static class AttributeValues {
    /// <summary>Converts the value of a whole attribute. Null when the value leaves the attribute unassigned.</summary>
    /// <param name="attribute">The attribute the value is for.</param>
    /// <param name="value">The value as the client sent it; a single element for a multi-valued attribute is taken as a list of one.</param>
    public static JsonNode? Convert(ScimAttribute attribute, JsonNode? value) {
        if (!attribute.MultiValued) {
            return ConvertSingle(attribute, value);
        }
        var elements = new JsonArray();
        foreach (var item in JsonNodes.Items(value)) {
            if (ConvertSingle(attribute, item) is { } element) {
                elements.Add(element);
            }
        }
        return elements.Count == 0 ? null : elements;
    }

    /// <summary>Converts the elements of a multi-valued complex attribute, dropping those left empty.</summary>
    /// <param name="attribute">The attribute the elements are for.</param>
    /// <param name="value">The elements as the client sent them; a single element is taken as a list of one.</param>
    public static List<JsonObject> ConvertElements(ScimAttribute attribute, JsonNode? value) {
        var elements = JsonNodes.Items(value).Select(item => ConvertSingle(attribute, item)).OfType<JsonObject>();
        return [.. elements.Where(element => element.Count > 0)];
    }

    /// <summary>Converts one value: the value of a single-valued attribute or one element of a multi-valued one.</summary>
    /// <param name="attribute">The attribute the value is for.</param>
    /// <param name="value">The value as the client sent it.</param>
    public static JsonNode? ConvertSingle(ScimAttribute attribute, JsonNode? value) {
        if (value is null) {
            return null;
        }
        if (attribute.IsComplex) {
            return ConvertComplex(attribute, value);
        }
        if (value is not JsonValue) {
            throw Invalid(attribute, value);
        }
        var kind = value.GetValueKind();
        if (attribute.Type == AttributeType.Boolean) {
            if (kind is JsonValueKind.True or JsonValueKind.False) {
                return JsonValue.Create(kind == JsonValueKind.True);
            }
            // Microsoft Entra ID sends booleans as "True" and "False"
            if (kind == JsonValueKind.String && bool.TryParse(value.GetValue<string>(), out var flag)) {
                return JsonValue.Create(flag);
            }
            throw Invalid(attribute, value);
        }
        return kind switch {
            JsonValueKind.String => JsonValue.Create(value.GetValue<string>()),
            JsonValueKind.Number => JsonValue.Create(value.ToJsonString()),
            JsonValueKind.Null => null,
            _ => throw Invalid(attribute, value),
        };
    }

    private static JsonObject ConvertComplex(ScimAttribute attribute, JsonNode value) {
        if (value is not JsonObject input) {
            // A bare value stands for the `value` sub-attribute; Microsoft Entra ID sends a manager as the manager's id
            if (value is not JsonValue || attribute.FindSubAttribute(ScimAttribute.VALUE) is null) {
                throw Invalid(attribute, value);
            }
            input = new JsonObject { [ScimAttribute.VALUE] = value.DeepClone() };
        }
        var result = new JsonObject();
        foreach (var subAttribute in attribute.SubAttributes) {
            if (!subAttribute.IsReadOnly && Convert(subAttribute, JsonNodes.Find(input, subAttribute.Name)) is { } converted) {
                result[subAttribute.Name] = converted;
            }
        }
        return result;
    }

    private static ScimException Invalid(ScimAttribute attribute, JsonNode value) {
        var expected = attribute.Type switch {
            AttributeType.Boolean => "true or false",
            AttributeType.Complex => "an object",
            _ => "a string",
        };
        return ScimException.BadRequest(ScimException.INVALID_VALUE, $"'{attribute.Name}' expects {expected}, not {value.ToJsonString()}.");
    }
}
