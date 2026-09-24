using System.Text.Json;
using System.Text.Json.Nodes;

namespace ScimStudio.DemoServer.Patching;

internal enum PatchOp {
    Add,
    Replace,
    Remove,
}

/// <summary>One entry of the <c>Operations</c> of a PatchOp request (RFC 7644 section 3.5.2).</summary>
internal sealed record PatchOperation(PatchOp Op, string? Path, JsonNode? Value) {
    /// <summary>
    /// Reads the operations of a PatchOp body. Keys and op names ignore case: Microsoft Entra ID sends "Add", "Replace" and
    /// "Remove".
    /// </summary>
    public static List<PatchOperation> ReadAll(JsonObject body) {
        if (JsonNodes.Find(body, "Operations") is not JsonArray { Count: > 0 } items) {
            throw ScimException.BadRequest(ScimException.INVALID_SYNTAX, "A PATCH request needs a non-empty \"Operations\" array.");
        }
        var operations = new List<PatchOperation>();
        foreach (var item in items) {
            if (item is not JsonObject operation) {
                throw ScimException.BadRequest(ScimException.INVALID_SYNTAX, "Each entry of \"Operations\" must be an object.");
            }
            var op = JsonNodes.AsString(JsonNodes.Find(operation, "op"));
            var path = JsonNodes.Find(operation, "path");
            if (path is not null && path.GetValueKind() != JsonValueKind.String) {
                throw ScimException.BadRequest(ScimException.INVALID_PATH, "\"path\" must be a string.");
            }
            operations.Add(new PatchOperation(ParseOp(op), JsonNodes.AsString(path), JsonNodes.Find(operation, "value")));
        }
        return operations;
    }

    private static PatchOp ParseOp(string? op) {
        if (string.Equals(op, "add", StringComparison.OrdinalIgnoreCase)) {
            return PatchOp.Add;
        }
        if (string.Equals(op, "replace", StringComparison.OrdinalIgnoreCase)) {
            return PatchOp.Replace;
        }
        if (string.Equals(op, "remove", StringComparison.OrdinalIgnoreCase)) {
            return PatchOp.Remove;
        }
        var shown = op is null ? "A missing op" : $"The op \"{op}\"";
        throw ScimException.BadRequest(ScimException.INVALID_SYNTAX, $"{shown} is not one of add, replace and remove.");
    }
}
