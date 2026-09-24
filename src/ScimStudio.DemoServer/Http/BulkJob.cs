using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using ScimStudio.DemoServer.Schema;

namespace ScimStudio.DemoServer.Http;

/// <summary>
/// Runs a BulkRequest (RFC 7644 section 3.7). The operations run in order, each as the request to its endpoint would, and
/// <c>bulkId:x</c> in the path or data of one stands for the id of the resource an earlier POST with the bulkId x created.
/// </summary>
internal sealed class BulkJob {
    public const int MAX_OPERATIONS = 1000;
    public const int MAX_PAYLOAD_SIZE = 1048576;

    private const string REFERENCE = "bulkId:";

    // The methods an operation may have (RFC 7644 section 3.7), in the canonical form HttpMethods gives them.
    private static readonly string[] Methods = [HttpMethods.Post, HttpMethods.Put, HttpMethods.Patch, HttpMethods.Delete];

    private readonly string _baseUrl;
    private readonly Func<WriteRequest, WriteResult> _execute;
    // The ids the POSTs of this request created, by bulkId. A POST that failed leaves null, which keeps its bulkId taken.
    private readonly Dictionary<string, string?> _created = new(StringComparer.Ordinal);

    private BulkJob(string baseUrl, Func<WriteRequest, WriteResult> execute) {
        _baseUrl = baseUrl;
        _execute = execute;
    }

    /// <summary>Runs the operations of a bulk request and answers its BulkResponse; throws when the request as a whole is refused.</summary>
    /// <param name="request">The body of the request.</param>
    /// <param name="baseUrl">The SCIM base URL, for the locations in the response.</param>
    /// <param name="execute">Carries out a write as a request to the resource's endpoint does.</param>
    public static JsonObject Run(JsonObject request, string baseUrl, Func<WriteRequest, WriteResult> execute) {
        if (JsonNodes.Find(request, "Operations") is not JsonArray operations) {
            throw Invalid("A bulk request needs an \"Operations\" array.");
        }
        if (operations.Count > MAX_OPERATIONS) {
            var detail = $"The request has {operations.Count} operations, more than maxOperations allows: {MAX_OPERATIONS}.";
            throw new ScimException(StatusCodes.Status413PayloadTooLarge, null, detail);
        }
        if (operations.Any(operation => operation is not JsonObject)) {
            throw Invalid("Each entry of \"Operations\" must be an object.");
        }
        var failOnErrors = FailOnErrors(JsonNodes.Find(request, "failOnErrors"));

        var job = new BulkJob(baseUrl, execute);
        var results = new JsonArray();
        var errors = 0;
        foreach (var operation in operations.OfType<JsonObject>()) {
            var (result, failed) = job.RunOperation(operation);
            results.Add(result);
            if (failed && ++errors == failOnErrors) {
                break;
            }
        }
        return new JsonObject { ["schemas"] = new JsonArray(ScimUrns.BULK_RESPONSE), ["Operations"] = results };
    }

    /// <summary>The answer to a body over maxPayloadSize, which RFC 7644 section 3.7.4 has name the limit.</summary>
    public static ScimException PayloadTooLarge() {
        var detail = $"The request is larger than maxPayloadSize allows: {MAX_PAYLOAD_SIZE} bytes.";
        return new ScimException(StatusCodes.Status413PayloadTooLarge, null, detail);
    }

    /// <summary>Runs one operation, and answers its entry of the BulkResponse and whether it failed.</summary>
    private (JsonObject Result, bool Failed) RunOperation(JsonObject operation) {
        // Taken in any case, as the op of a PATCH is; the entry names the method in capitals
        var given = JsonNodes.AsString(JsonNodes.Find(operation, "method"));
        var method = given is null ? null : HttpMethods.GetCanonicalizedValue(given);
        var bulkId = JsonNodes.AsString(JsonNodes.Find(operation, "bulkId"));
        // RFC 7644 section 3.7.3 has every entry name the resource, but that of a POST that failed, which created none
        string? location = null;
        try {
            if (method is null || !Methods.Contains(method)) {
                var shown = method is null ? "A missing method" : $"The method \"{method}\"";
                throw Invalid($"{shown} is not one of POST, PUT, PATCH and DELETE.");
            }
            var isPost = HttpMethods.IsPost(method);
            if (isPost) {
                Reserve(bulkId);
            }
            var (type, id) = Path(method, JsonNodes.AsString(JsonNodes.Find(operation, "path")));
            if (!isPost) {
                location = type.Location(_baseUrl, id);
            }
            var body = HttpMethods.IsDelete(method) ? new JsonObject() : Data(method, operation);
            var result = _execute(new WriteRequest(method, type, id, body, Version(operation)));
            if (isPost) {
                var created = result.Resource!["id"]!.GetValue<string>();
                _created[bulkId!] = created;
                location = type.Location(_baseUrl, created);
            }
            var version = JsonNodes.AsString(result.Resource?["meta"]?["version"]);
            return (Entry(location, method, bulkId, version, result.Status, null), false);
        } catch (ScimException error) {
            return (Entry(location, method, bulkId, null, error.Status, ScimResponses.Error(error)), true);
        }
    }

    /// <summary>Takes the bulkId of a POST, which RFC 7644 section 3.7 requires and wants unique within the request.</summary>
    private void Reserve(string? bulkId) {
        if (string.IsNullOrEmpty(bulkId)) {
            throw Invalid("A POST needs a \"bulkId\".");
        }
        if (!_created.TryAdd(bulkId, null)) {
            throw Invalid($"The bulkId \"{bulkId}\" is taken by an earlier operation of the request.");
        }
    }

    /// <summary>
    /// Reads the path of an operation: /Users or /Groups for a POST, /Users/{id} or /Groups/{id} for the other methods, where
    /// the id may be a bulkId reference. Endpoints ignore case, as the routing of a request of their own does.
    /// </summary>
    private (ResourceType Type, string Id) Path(string method, string? path) {
        var isPost = HttpMethods.IsPost(method);
        var expected = isPost ? "/Users or /Groups" : "/Users/{id} or /Groups/{id}";
        if (path is null) {
            throw Invalid($"A {method} needs a \"path\": {expected}.");
        }
        var trimmed = path.TrimEnd('/');
        foreach (var type in ResourceType.All) {
            if (!trimmed.StartsWith(type.Endpoint, StringComparison.OrdinalIgnoreCase)) {
                continue;
            }
            var rest = trimmed[type.Endpoint.Length..];
            if (isPost && rest.Length == 0) {
                return (type, "");
            }
            if (!isPost && rest.Length > 1 && rest[0] == '/' && !rest[1..].Contains('/', StringComparison.Ordinal)) {
                var id = rest[1..];
                return (type, Reference(id) ?? id);
            }
        }
        throw Invalid($"A {method} goes to {expected}, not \"{path}\".");
    }

    /// <summary>The data of a POST, PUT or PATCH, with every bulkId reference in it replaced by the id it stands for.</summary>
    private JsonObject Data(string method, JsonObject operation) {
        if (JsonNodes.Find(operation, "data") is not JsonObject data) {
            throw Invalid($"A {method} needs \"data\", a JSON object.");
        }
        Resolve(data);
        return data;
    }

    /// <summary>Replaces the bulkId references within an object or an array, all the way down.</summary>
    private void Resolve(JsonNode node) {
        if (node is JsonObject complex) {
            foreach (var (key, value) in complex.ToList()) {
                if (Reference(JsonNodes.AsString(value)) is { } id) {
                    complex[key] = id;
                } else if (value is not null) {
                    Resolve(value);
                }
            }
        } else if (node is JsonArray array) {
            for (var index = 0; index < array.Count; index++) {
                if (Reference(JsonNodes.AsString(array[index])) is { } id) {
                    array[index] = id;
                } else if (array[index] is { } item) {
                    Resolve(item);
                }
            }
        }
    }

    /// <summary>
    /// The id a reference such as <c>bulkId:x</c> stands for; null for text that is no reference. A reference to a bulkId no
    /// earlier POST created a resource for fails the operation with 409, as RFC 7644 section 3.7.2 allows.
    /// </summary>
    private string? Reference(string? text) {
        if (text is null || !text.StartsWith(REFERENCE, StringComparison.Ordinal)) {
            return null;
        }
        var bulkId = text[REFERENCE.Length..];
        var detail = $"No operation before this one created a resource with the bulkId \"{bulkId}\".";
        return _created.GetValueOrDefault(bulkId) ?? throw new ScimException(StatusCodes.Status409Conflict, null, detail);
    }

    /// <summary>The version of an operation, which works as the If-Match of a request of its own; null for any.</summary>
    private static string? Version(JsonObject operation) {
        var version = JsonNodes.Find(operation, "version");
        if (version is null) {
            return null;
        }
        return JsonNodes.AsString(version) ?? throw Invalid("\"version\" must be a string, such as W/\"1\".");
    }

    /// <summary>How many failed operations end the request (RFC 7644 section 3.7.3); null lets every operation run.</summary>
    private static int? FailOnErrors(JsonNode? node) {
        if (node is null) {
            return null;
        }
        if (node is JsonValue value && value.GetValueKind() == JsonValueKind.Number && value.TryGetValue<int>(out var count) && count > 0) {
            return count;
        }
        throw Invalid($"\"failOnErrors\" must be a positive integer, not {node.ToJsonString()}.");
    }

    /// <summary>An entry of the BulkResponse, with the members of RFC 7644 section 3.7.3 that apply to it.</summary>
    private static JsonObject Entry(string? location, string? method, string? bulkId, string? version, int status, JsonObject? response) {
        var entry = new JsonObject {
            ["location"] = location,
            ["method"] = method,
            ["bulkId"] = bulkId,
            ["version"] = version,
            ["status"] = status.ToString(CultureInfo.InvariantCulture),
            ["response"] = response,
        };
        // The members that do not apply are null, which leaves them out
        JsonNodes.RemoveEmpty(entry);
        return entry;
    }

    private static ScimException Invalid(string detail) {
        return ScimException.BadRequest(ScimException.INVALID_SYNTAX, detail);
    }
}
