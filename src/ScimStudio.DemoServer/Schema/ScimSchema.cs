namespace ScimStudio.DemoServer.Schema;

/// <summary>A schema the server publishes under /Schemas and resolves attribute names against.</summary>
internal sealed class ScimSchema {
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Description { get; init; }
    public required IReadOnlyList<ScimAttribute> Attributes { get; init; }

    public ScimAttribute? FindAttribute(string name) {
        foreach (var attribute in Attributes) {
            if (string.Equals(attribute.Name, name, StringComparison.OrdinalIgnoreCase)) {
                return attribute;
            }
        }
        return null;
    }
}
