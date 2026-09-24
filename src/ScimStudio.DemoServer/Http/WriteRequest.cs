using System.Text.Json.Nodes;
using ScimStudio.DemoServer.Schema;

namespace ScimStudio.DemoServer.Http;

/// <summary>A write to one resource: a request to the resource's endpoint, or an operation of a bulk request.</summary>
/// <param name="Method">POST, PUT, PATCH or DELETE.</param>
/// <param name="Type">The type of the resource.</param>
/// <param name="Id">The id of the resource; a POST, which creates one, ignores it.</param>
/// <param name="Body">The resource, or the PatchOp of a PATCH; a DELETE ignores it.</param>
/// <param name="IfMatch">The versions the resource has to be at, as If-Match lists them; null for any.</param>
internal sealed record WriteRequest(string Method, ResourceType Type, string Id, JsonObject Body, string? IfMatch);

/// <summary>What a write answers.</summary>
/// <param name="Status">The status code.</param>
/// <param name="Resource">The resource afterwards, rendered; null once deleted.</param>
internal sealed record WriteResult(int Status, JsonObject? Resource);
