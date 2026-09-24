using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using ScimStudio.DemoServer.Schema;

namespace ScimStudio.DemoServer.Http;

/// <summary>Writes SCIM responses, errors included, as <c>application/scim+json</c>.</summary>
internal static class ScimResponses {
    public const string CONTENT_TYPE = "application/scim+json; charset=utf-8";

    // Names like "Müller" stay readable in the tool's request log instead of turning into ü escapes.
    private static readonly JsonSerializerOptions Options = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static Task WriteAsync(HttpContext context, int status, JsonNode body) {
        context.Response.StatusCode = status;
        context.Response.ContentType = CONTENT_TYPE;
        return context.Response.WriteAsync(body.ToJsonString(Options), context.RequestAborted);
    }

    public static Task WriteErrorAsync(HttpContext context, ScimException error) {
        return WriteAsync(context, error.Status, Error(error));
    }

    /// <summary>The body of an error response (RFC 7644 section 3.12), which a bulk response also holds for a failed operation.</summary>
    /// <param name="error">The error.</param>
    public static JsonObject Error(ScimException error) {
        var body = new JsonObject {
            ["schemas"] = new JsonArray(ScimUrns.ERROR),
            ["status"] = error.Status.ToString(CultureInfo.InvariantCulture),
        };
        if (error.ScimType is not null) {
            body["scimType"] = error.ScimType;
        }
        body["detail"] = error.Message;
        return body;
    }

    public static JsonObject ListResponse(IReadOnlyList<JsonObject> page, int totalResults, int startIndex) {
        return new JsonObject {
            ["schemas"] = new JsonArray(ScimUrns.LIST_RESPONSE),
            ["totalResults"] = totalResults,
            ["startIndex"] = startIndex,
            ["itemsPerPage"] = page.Count,
            ["Resources"] = new JsonArray([.. page]),
        };
    }
}
