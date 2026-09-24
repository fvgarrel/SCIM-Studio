using System.Text.Json.Nodes;

namespace ScimStudio.DemoServer.Schema;

/// <summary>
/// An attribute path resolved against a resource type: the extension object the attribute lives in (null for the resource
/// itself), the attribute, and the sub-attribute when one is named.
/// </summary>
internal sealed record AttributePath(string? Extension, ScimAttribute Attribute, ScimAttribute? SubAttribute) {
    /// <summary>
    /// What a comparison reads: the sub-attribute, or the <c>value</c> sub-attribute of a complex attribute named alone
    /// (RFC 7644 section 3.4.2.2: <c>emails co "x"</c> means <c>emails.value co "x"</c>). Null when there is nothing to compare.
    /// </summary>
    public ScimAttribute? Leaf => SubAttribute ?? (Attribute.IsComplex ? Attribute.FindSubAttribute(ScimAttribute.VALUE) : Attribute);

    public JsonObject? Container(JsonObject resource) {
        return Extension is null ? resource : resource[Extension] as JsonObject;
    }

    /// <summary>The object the attribute lives in, creating the extension object when it is missing.</summary>
    public JsonObject ContainerForWrite(JsonObject resource) {
        if (Extension is null) {
            return resource;
        }
        if (resource[Extension] is not JsonObject extension) {
            extension = new JsonObject();
            resource[Extension] = extension;
        }
        return extension;
    }

    public JsonNode? Node(JsonObject resource) {
        return Container(resource)?[Attribute.Name];
    }

    /// <summary>Every value the path reaches: one per element of a multi-valued attribute.</summary>
    public IEnumerable<JsonNode> Values(JsonObject resource) {
        var items = JsonNodes.Items(Node(resource));
        if (!Attribute.IsComplex) {
            return items;
        }
        var leaf = Leaf;
        return leaf is null ? [] : items.OfType<JsonObject>().Select(element => element[leaf.Name]).OfType<JsonNode>();
    }

    public bool IsPresent(JsonObject resource) {
        if (Attribute.IsComplex && SubAttribute is null) {
            return JsonNodes.HasValue(Node(resource));
        }
        return Values(resource).Any(JsonNodes.HasValue);
    }

    public override string ToString() {
        var name = SubAttribute is null ? Attribute.Name : $"{Attribute.Name}.{SubAttribute.Name}";
        return Extension is null ? name : $"{Extension}:{name}";
    }
}
