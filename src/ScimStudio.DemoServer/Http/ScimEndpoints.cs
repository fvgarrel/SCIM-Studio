using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ScimStudio.DemoServer.Patching;
using ScimStudio.DemoServer.Schema;
using ScimStudio.DemoServer.Store;

namespace ScimStudio.DemoServer.Http;

/// <summary>The SCIM endpoints under <see cref="PREFIX"/>, and the middleware that authenticates and answers errors.</summary>
internal sealed class ScimEndpoints(ScimStore store, string token, int maxResults) {
    public const string PREFIX = "/scim/v2";

    // A body naming an attribute twice is refused rather than one of the two values silently winning.
    private static readonly JsonDocumentOptions StrictJson = new() { AllowDuplicateProperties = false };

    private readonly byte[] _token = Encoding.UTF8.GetBytes(token);

    public void Map(WebApplication app) {
        app.Use(HandleAsync);

        app.MapGet($"{PREFIX}/ServiceProviderConfig", ServiceProviderConfigAsync);
        app.MapGet($"{PREFIX}/ResourceTypes", ResourceTypesAsync);
        app.MapGet($"{PREFIX}/ResourceTypes/{{id}}", ResourceTypeAsync);
        app.MapGet($"{PREFIX}/Schemas", SchemasAsync);
        app.MapGet($"{PREFIX}/Schemas/{{id}}", SchemaAsync);
        foreach (var type in ResourceType.All) {
            var endpoint = PREFIX + type.Endpoint;
            app.MapGet(endpoint, context => ListAsync(context, type));
            app.MapPost($"{endpoint}/.search", context => SearchAsync(context, type));
            app.MapPost(endpoint, context => WriteAsync(context, type));
            app.MapGet($"{endpoint}/{{id}}", context => GetAsync(context, type));
            app.MapPut($"{endpoint}/{{id}}", context => WriteAsync(context, type));
            app.MapPatch($"{endpoint}/{{id}}", context => WriteAsync(context, type));
            app.MapDelete($"{endpoint}/{{id}}", context => WriteAsync(context, type));
        }
        app.MapPost($"{PREFIX}/Bulk", BulkAsync);
        // Routing prefers an endpoint for the method to one for any method, so this one answers every method but POST
        app.Map($"{PREFIX}/Bulk", NotImplemented("A bulk request is sent with POST."));
        app.Map($"{PREFIX}/Me", NotImplemented("/Me is not supported: the server does not tie a token to a user."));
    }

    /// <summary>
    /// Runs around every request: refuses one without the token, and turns errors into SCIM error responses, including the
    /// bodiless 404 and 405 that routing answers with on its own.
    /// </summary>
    private async Task HandleAsync(HttpContext context, RequestDelegate next) {
        try {
            if (!IsAuthorized(context.Request)) {
                context.Response.Headers.WWWAuthenticate = "Bearer";
                throw new ScimException(StatusCodes.Status401Unauthorized, null, "Send the server's token as \"Authorization: Bearer <token>\".");
            }
            await next(context);
            var status = context.Response.StatusCode;
            if (!context.Response.HasStarted && status >= StatusCodes.Status400BadRequest) {
                var detail = status == StatusCodes.Status405MethodNotAllowed
                    ? $"{context.Request.Method} is not supported on {context.Request.Path}."
                    : $"There is no endpoint at {context.Request.Path}.";
                await ScimResponses.WriteErrorAsync(context, new ScimException(status, null, detail));
            }
        } catch (ScimException error) when (!context.Response.HasStarted) {
            await ScimResponses.WriteErrorAsync(context, error);
        } catch (Exception error) when (!context.Response.HasStarted && !context.RequestAborted.IsCancellationRequested) {
            // A defect of the demo server should reach the client as a SCIM error it can show, not as an empty 500
            await ScimResponses.WriteErrorAsync(context, new ScimException(StatusCodes.Status500InternalServerError, null, error.Message));
        }
    }

    private bool IsAuthorized(HttpRequest request) {
        const string BEARER = "Bearer ";
        var header = request.Headers.Authorization.ToString();
        return header.StartsWith(BEARER, StringComparison.OrdinalIgnoreCase)
            && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(header[BEARER.Length..].Trim()), _token);
    }

    private Task ServiceProviderConfigAsync(HttpContext context) {
        return ScimResponses.WriteAsync(context, StatusCodes.Status200OK, DiscoveryDocuments.ServiceProviderConfig(BaseUrl(context), maxResults));
    }

    private static Task ResourceTypesAsync(HttpContext context) {
        RefuseFilter(context);
        var documents = ResourceType.All.Select(type => DiscoveryDocuments.ResourceType(type, BaseUrl(context))).ToList();
        return ScimResponses.WriteAsync(context, StatusCodes.Status200OK, ScimResponses.ListResponse(documents, documents.Count, 1));
    }

    private static Task ResourceTypeAsync(HttpContext context) {
        var name = Id(context);
        var type = ResourceType.All.FirstOrDefault(candidate => string.Equals(candidate.Name, name, StringComparison.OrdinalIgnoreCase))
            ?? throw ScimException.NotFound($"There is no resource type \"{name}\".");
        return ScimResponses.WriteAsync(context, StatusCodes.Status200OK, DiscoveryDocuments.ResourceType(type, BaseUrl(context)));
    }

    private static Task SchemasAsync(HttpContext context) {
        RefuseFilter(context);
        var documents = ScimSchemas.All.Select(schema => DiscoveryDocuments.Schema(schema, BaseUrl(context))).ToList();
        return ScimResponses.WriteAsync(context, StatusCodes.Status200OK, ScimResponses.ListResponse(documents, documents.Count, 1));
    }

    private static Task SchemaAsync(HttpContext context) {
        var id = Id(context);
        var schema = ScimSchemas.All.FirstOrDefault(candidate => string.Equals(candidate.Id, id, StringComparison.OrdinalIgnoreCase))
            ?? throw ScimException.NotFound($"There is no schema \"{id}\".");
        return ScimResponses.WriteAsync(context, StatusCodes.Status200OK, DiscoveryDocuments.Schema(schema, BaseUrl(context)));
    }

    /// <summary>
    /// Refuses a filter on the lists of resource types and schemas with 403, as RFC 7644 section 4 advises, so that no client
    /// takes the whole list for the entries its filter matches. The other list parameters are ignored there, as it demands.
    /// </summary>
    private static void RefuseFilter(HttpContext context) {
        if (!string.IsNullOrWhiteSpace(context.Request.Query["filter"].ToString())) {
            throw new ScimException(StatusCodes.Status403Forbidden, null, $"{context.Request.Path} cannot be filtered (RFC 7644 section 4).");
        }
    }

    private Task ListAsync(HttpContext context, ResourceType type) {
        var query = ListQuery.FromQueryString(context.Request.Query, type, maxResults);
        return ScimResponses.WriteAsync(context, StatusCodes.Status200OK, query.Execute(store.GetAll(type, BaseUrl(context))));
    }

    private async Task SearchAsync(HttpContext context, ResourceType type) {
        var query = ListQuery.FromSearchRequest(await ReadBodyAsync(context), type, maxResults);
        await ScimResponses.WriteAsync(context, StatusCodes.Status200OK, query.Execute(store.GetAll(type, BaseUrl(context))));
    }

    private Task GetAsync(HttpContext context, ResourceType type) {
        var resource = store.Get(type, Id(context), BaseUrl(context));
        var ifNoneMatch = context.Request.Headers.IfNoneMatch;
        if (ifNoneMatch.Count > 0 && EntityTag.Matches(ifNoneMatch.ToString(), Version(resource))) {
            // The client has this version already: the headers a 200 would carry, and no body (RFC 7232 section 4.1)
            SetHeaders(context, resource);
            context.Response.StatusCode = StatusCodes.Status304NotModified;
            return Task.CompletedTask;
        }
        return WriteResourceAsync(context, type, StatusCodes.Status200OK, resource);
    }

    /// <summary>A POST, PUT, PATCH or DELETE to a resource's endpoint, which If-Match makes conditional (RFC 7644 section 3.14).</summary>
    private async Task WriteAsync(HttpContext context, ResourceType type) {
        var request = context.Request;
        var body = HttpMethods.IsDelete(request.Method) ? new JsonObject() : await ReadBodyAsync(context);
        var ifMatch = request.Headers.IfMatch.Count > 0 ? request.Headers.IfMatch.ToString() : null;
        var result = Execute(new WriteRequest(request.Method, type, Id(context), body, ifMatch), BaseUrl(context));
        if (result.Status != StatusCodes.Status204NoContent) {
            await WriteResourceAsync(context, type, result.Status, result.Resource!);
            return;
        }
        if (result.Resource is not null) {
            // A patched group has no body to carry its new version, which the client needs for its next If-Match
            context.Response.Headers.ETag = Version(result.Resource);
        }
        context.Response.StatusCode = StatusCodes.Status204NoContent;
    }

    private async Task BulkAsync(HttpContext context) {
        var body = await ReadBulkBodyAsync(context);
        var baseUrl = BaseUrl(context);
        var response = BulkJob.Run(body, baseUrl, request => Execute(request, baseUrl));
        await ScimResponses.WriteAsync(context, StatusCodes.Status200OK, response);
    }

    /// <summary>
    /// Carries out a write. A request to a resource's endpoint and an operation of a bulk request both come here, so the
    /// operation answers what the request of its own would.
    /// </summary>
    /// <param name="request">The write.</param>
    /// <param name="baseUrl">The SCIM base URL, for the locations in the resource.</param>
    private WriteResult Execute(WriteRequest request, string baseUrl) {
        var (method, type, id, body, ifMatch) = request;
        if (HttpMethods.IsPost(method)) {
            return new WriteResult(StatusCodes.Status201Created, store.Create(type, body, baseUrl));
        }
        if (HttpMethods.IsPut(method)) {
            return new WriteResult(StatusCodes.Status200OK, store.Replace(type, id, body, ifMatch, baseUrl));
        }
        if (HttpMethods.IsPatch(method)) {
            var patched = store.Patch(type, id, PatchOperation.ReadAll(body), ifMatch, baseUrl);
            // Member lists can be long, so a group answers without a body, which RFC 7644 section 3.5.2 allows
            return new WriteResult(type == ResourceType.Group ? StatusCodes.Status204NoContent : StatusCodes.Status200OK, patched);
        }
        if (HttpMethods.IsDelete(method)) {
            store.Delete(type, id, ifMatch);
            return new WriteResult(StatusCodes.Status204NoContent, null);
        }
        throw new ArgumentException($"{method} is not a write.", nameof(request));
    }

    /// <summary>Answers with one resource, trimmed by the <c>attributes</c> and <c>excludedAttributes</c> of the query string.</summary>
    private static Task WriteResourceAsync(HttpContext context, ResourceType type, int status, JsonObject resource) {
        SetHeaders(context, resource);
        var query = context.Request.Query;
        var projection = Projection.Parse(type, query["attributes"].ToString(), query["excludedAttributes"].ToString());
        return ScimResponses.WriteAsync(context, status, projection.Apply(resource));
    }

    /// <summary>
    /// The headers of an answer about one resource. Location goes on every answer, not only on 201, because the examples of
    /// RFC 7644 (sections 3.4.1, 3.5.1) show it there; ETag carries meta.version, which <c>attributes</c> may trim away.
    /// </summary>
    private static void SetHeaders(HttpContext context, JsonObject resource) {
        var location = resource["meta"]!["location"]!.GetValue<string>();
        context.Response.Headers.Location = location;
        context.Response.Headers.ContentLocation = location;
        context.Response.Headers.ETag = Version(resource);
    }

    /// <summary>Reads the body as JSON whatever its content type says: clients send application/json and application/scim+json alike.</summary>
    private static Task<JsonObject> ReadBodyAsync(HttpContext context) {
        return ParseAsync(context.Request.Body, context.RequestAborted);
    }

    /// <summary>
    /// Reads the body of a bulk request, which may not exceed maxPayloadSize (RFC 7644 section 3.7.4): judged by Content-Length
    /// when the client sends it, and by counting while reading when it does not.
    /// </summary>
    private static async Task<JsonObject> ReadBulkBodyAsync(HttpContext context) {
        if (context.Request.ContentLength > BulkJob.MAX_PAYLOAD_SIZE) {
            throw BulkJob.PayloadTooLarge();
        }
        using var buffer = new MemoryStream();
        var chunk = new byte[16384];
        int read;
        while ((read = await context.Request.Body.ReadAsync(chunk, context.RequestAborted)) > 0) {
            if (buffer.Length + read > BulkJob.MAX_PAYLOAD_SIZE) {
                throw BulkJob.PayloadTooLarge();
            }
            buffer.Write(chunk, 0, read);
        }
        buffer.Position = 0;
        return await ParseAsync(buffer, context.RequestAborted);
    }

    private static async Task<JsonObject> ParseAsync(Stream body, CancellationToken cancellationToken) {
        JsonNode? node;
        try {
            node = await JsonNode.ParseAsync(body, null, StrictJson, cancellationToken);
        } catch (JsonException error) {
            throw ScimException.BadRequest(ScimException.INVALID_SYNTAX, $"The body is not valid JSON: {error.Message}");
        }
        return node as JsonObject ?? throw ScimException.BadRequest(ScimException.INVALID_SYNTAX, "The body must be a JSON object.");
    }

    private static RequestDelegate NotImplemented(string detail) {
        return _ => throw new ScimException(StatusCodes.Status501NotImplemented, null, detail);
    }

    private static string Id(HttpContext context) {
        return context.GetRouteValue("id") as string ?? "";
    }

    private static string Version(JsonObject resource) {
        return resource["meta"]!["version"]!.GetValue<string>();
    }

    /// <summary>
    /// The base URL as the connection reached it. The server listens on 127.0.0.1 alone, so this is the <c>BaseUrl</c> the
    /// server reports, and it is right even for a port the operating system picked.
    /// </summary>
    private static string BaseUrl(HttpContext context) {
        var connection = context.Connection;
        return string.Create(CultureInfo.InvariantCulture, $"http://{connection.LocalIpAddress}:{connection.LocalPort}{PREFIX}");
    }
}
