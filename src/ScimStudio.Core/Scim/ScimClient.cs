using System.Net;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using ScimStudio.Core.Http;

namespace ScimStudio.Core.Scim;

/// <summary>Where a SCIM service provider is and how to get in.</summary>
public sealed record ScimConnection {
    /// <summary>The base URL every endpoint is relative to, e.g. <c>https://host/scim/v2</c>.</summary>
    public required Uri BaseUrl { get; init; }

    public string Token { get; init; } = string.Empty;

    /// <summary>Accepts any server certificate - for a development server with a self-signed one, never for anything else.</summary>
    public bool AcceptInvalidCertificates { get; init; }

    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(30);
}

/// <summary>
/// A request as the checks write it when an ordinary one will not do: another token or none, other media types, extra headers, a body that
/// need not be JSON.
/// </summary>
public sealed record ScimRequest {
    public required HttpMethod Method { get; init; }

    /// <summary>The path under the base URL, query included.</summary>
    public required string Path { get; init; }

    /// <summary>The body as text, or null for none.</summary>
    public string? Body { get; init; }

    /// <summary>The media type the body is declared as.</summary>
    public string ContentType { get; init; } = ScimSchemas.MEDIA_TYPE;

    /// <summary>The Accept header: SCIM's media type and then plain JSON when null, no header at all when empty.</summary>
    public string? Accept { get; init; }

    /// <summary>The Authorization header as a whole, e.g. <c>Basic …</c>: the connection's bearer token when null, none when empty.</summary>
    public string? Authorization { get; init; }

    /// <summary>Further headers, e.g. <c>If-Match</c>.</summary>
    public IReadOnlyList<KeyValuePair<string, string>> Headers { get; init; } = [];
}

/// <summary>
/// Speaks SCIM 2.0 to one service provider. Every request goes through the <see cref="RecordingHandler"/>, so the log shows what was sent.
/// The typed methods throw a <see cref="ScimException"/> for a failed answer; the two <c>SendAsync</c> return whatever came back, for the
/// checks that expect a failure.
/// </summary>
public sealed class ScimClient : IDisposable {
    private readonly HttpClient _http;
    private readonly string _root;

    /// <summary>A client for one connection, recording into the sink.</summary>
    /// <param name="connection">Where the server is, and the token.</param>
    /// <param name="sink">Where every exchange is recorded.</param>
    public ScimClient(ScimConnection connection, IExchangeSink sink) : this(connection, sink, Transport(connection)) {
    }

    /// <summary>A client over transport of the caller's, for tests that stand in for a server.</summary>
    /// <param name="connection">Where the server is, and the token.</param>
    /// <param name="sink">Where every exchange is recorded.</param>
    /// <param name="transport">What sends the requests.</param>
    internal ScimClient(ScimConnection connection, IExchangeSink sink, HttpMessageHandler transport) {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(sink);

        Connection = connection;
        _root = connection.BaseUrl.AbsoluteUri.TrimEnd('/');
        _http = new HttpClient(new RecordingHandler(sink) { InnerHandler = transport }) { Timeout = connection.Timeout };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ScimStudio", Version));
    }

    public ScimConnection Connection { get; }

    /// <summary>SCIM Studio's version as MinVer took it from the tag: what the User-Agent names, the settings show and a report records.</summary>
    public static string Version => typeof(ScimClient).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()
        ?.InformationalVersion.Split('+')[0] ?? "0.0.0";

    /// <summary>The absolute address of a path under the base URL.</summary>
    /// <param name="path">The path, e.g. <c>/Users/42</c>.</param>
    public Uri Resolve(string path) {
        ArgumentNullException.ThrowIfNull(path);
        return new Uri(_root + (path.StartsWith('/') ? path : $"/{path}"));
    }

    /// <summary>Sends a request with the connection's token and returns the answer, whatever its status.</summary>
    /// <param name="method">The method.</param>
    /// <param name="path">The path under the base URL, query included.</param>
    /// <param name="body">The body, or null for none.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public Task<ScimResponse> SendAsync(HttpMethod method, string path, JsonNode? body = null, CancellationToken cancellationToken = default) {
        return SendAsync(new ScimRequest { Method = method, Path = path, Body = body?.ToJsonString(ScimJson.Compact) }, cancellationToken);
    }

    /// <summary>Sends a request exactly as described, whatever it departs from, and returns the answer, whatever its status.</summary>
    /// <param name="scimRequest">The request.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<ScimResponse> SendAsync(ScimRequest scimRequest, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(scimRequest);

        using var request = new HttpRequestMessage(scimRequest.Method, Resolve(scimRequest.Path));
        if (scimRequest.Accept is null) {
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(ScimSchemas.MEDIA_TYPE));
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json", 0.9));
        } else if (scimRequest.Accept.Length > 0) {
            request.Headers.TryAddWithoutValidation("Accept", scimRequest.Accept);
        }

        if (scimRequest.Authorization is null) {
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", Connection.Token);
        } else if (scimRequest.Authorization.Length > 0) {
            request.Headers.TryAddWithoutValidation("Authorization", scimRequest.Authorization);
        }

        foreach (var (name, value) in scimRequest.Headers) {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (scimRequest.Body is not null) {
            request.Content = new StringContent(scimRequest.Body, Encoding.UTF8, scimRequest.ContentType);
        }

        using var response = await _http.SendAsync(request, cancellationToken);
        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        request.Options.TryGetValue(RecordingHandler.ExchangeKey, out var exchange);

        return new ScimResponse {
            StatusCode = (int)response.StatusCode,
            Text = text,
            Body = ScimJson.Parse(text),
            Location = response.Headers.Location ?? response.Content.Headers.ContentLocation,
            ContentType = response.Content.Headers.ContentType?.MediaType,
            Exchange = exchange,
        };
    }

    public async Task<ServiceProviderConfig> GetServiceProviderConfigAsync(CancellationToken cancellationToken = default) {
        var response = await SendAsync(HttpMethod.Get, "/ServiceProviderConfig", null, cancellationToken);
        return ServiceProviderConfig.Read(response.Document());
    }

    public async Task<IReadOnlyList<ResourceTypeInfo>> GetResourceTypesAsync(CancellationToken cancellationToken = default) {
        var response = await SendAsync(HttpMethod.Get, "/ResourceTypes", null, cancellationToken);
        return [.. Documents(response).Select(ResourceTypeInfo.Read)];
    }

    public async Task<IReadOnlyList<SchemaDefinition>> GetSchemasAsync(CancellationToken cancellationToken = default) {
        var response = await SendAsync(HttpMethod.Get, "/Schemas", null, cancellationToken);
        return [.. Documents(response).Select(SchemaDefinition.Read)];
    }

    public async Task<ScimPage<ScimUser>> ListUsersAsync(ScimQuery? query = null, CancellationToken cancellationToken = default) {
        var response = await SendAsync(HttpMethod.Get, $"/Users{query?.ToQueryString()}", null, cancellationToken);
        return ScimPage.Read(response.Document(), ScimUser.Read);
    }

    /// <summary>Lists users through <c>POST /Users/.search</c>, which keeps the filter out of the URL.</summary>
    /// <param name="query">What to list.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<ScimPage<ScimUser>> SearchUsersAsync(ScimQuery query, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(query);
        var response = await SendAsync(HttpMethod.Post, "/Users/.search", query.ToSearchRequest(), cancellationToken);
        return ScimPage.Read(response.Document(), ScimUser.Read);
    }

    public async Task<ScimUser> GetUserAsync(string id, CancellationToken cancellationToken = default) {
        var response = await SendAsync(HttpMethod.Get, UserPath(id), null, cancellationToken);
        return ScimUser.Read(response.Document());
    }

    public async Task<ScimUser> CreateUserAsync(JsonObject resource, CancellationToken cancellationToken = default) {
        var response = await SendAsync(HttpMethod.Post, "/Users", resource, cancellationToken);
        return ScimUser.Read(response.Document());
    }

    public async Task<ScimUser> ReplaceUserAsync(string id, JsonObject resource, CancellationToken cancellationToken = default) {
        var response = await SendAsync(HttpMethod.Put, UserPath(id), resource, cancellationToken);
        return ScimUser.Read(response.Document());
    }

    /// <summary>Patches a user, and reads it back when the server answered 204 rather than with the resource.</summary>
    /// <param name="id">The user's id.</param>
    /// <param name="patch">The PatchOp request.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    public async Task<ScimUser> PatchUserAsync(string id, JsonObject patch, CancellationToken cancellationToken = default) {
        var response = (await SendAsync(HttpMethod.Patch, UserPath(id), patch, cancellationToken)).EnsureSuccess();
        return response.Body is JsonObject resource ? ScimUser.Read(resource) : await GetUserAsync(id, cancellationToken);
    }

    public async Task DeleteUserAsync(string id, CancellationToken cancellationToken = default) {
        (await SendAsync(HttpMethod.Delete, UserPath(id), null, cancellationToken)).EnsureSuccess();
    }

    public async Task<ScimPage<ScimGroup>> ListGroupsAsync(ScimQuery? query = null, CancellationToken cancellationToken = default) {
        var response = await SendAsync(HttpMethod.Get, $"/Groups{query?.ToQueryString()}", null, cancellationToken);
        return ScimPage.Read(response.Document(), ScimGroup.Read);
    }

    public async Task<ScimGroup> GetGroupAsync(string id, CancellationToken cancellationToken = default) {
        var response = await SendAsync(HttpMethod.Get, GroupPath(id), null, cancellationToken);
        return ScimGroup.Read(response.Document());
    }

    public async Task<ScimGroup> CreateGroupAsync(JsonObject resource, CancellationToken cancellationToken = default) {
        var response = await SendAsync(HttpMethod.Post, "/Groups", resource, cancellationToken);
        return ScimGroup.Read(response.Document());
    }

    public async Task<ScimGroup> ReplaceGroupAsync(string id, JsonObject resource, CancellationToken cancellationToken = default) {
        var response = await SendAsync(HttpMethod.Put, GroupPath(id), resource, cancellationToken);
        return ScimGroup.Read(response.Document());
    }

    /// <summary>Patches a group. Many servers answer 204 here, since a group's members can be thousands; then the result is null.</summary>
    /// <param name="id">The group's id.</param>
    /// <param name="patch">The PatchOp request.</param>
    /// <param name="cancellationToken">Cancels the request.</param>
    public async Task<ScimGroup?> PatchGroupAsync(string id, JsonObject patch, CancellationToken cancellationToken = default) {
        var response = (await SendAsync(HttpMethod.Patch, GroupPath(id), patch, cancellationToken)).EnsureSuccess();
        return response.Body is JsonObject resource ? ScimGroup.Read(resource) : null;
    }

    public async Task DeleteGroupAsync(string id, CancellationToken cancellationToken = default) {
        (await SendAsync(HttpMethod.Delete, GroupPath(id), null, cancellationToken)).EnsureSuccess();
    }

    /// <summary>Reads page after page until every match is read or the limit is reached, as far as the server lets a page be.</summary>
    /// <typeparam name="T">The resource type.</typeparam>
    /// <param name="list">Reads one page.</param>
    /// <param name="query">What to list; its start index and count are the pager's.</param>
    /// <param name="limit">The most resources to read.</param>
    /// <param name="cancellationToken">Cancels the requests.</param>
    public static async Task<IReadOnlyList<T>> ReadAllAsync<T>(
        Func<ScimQuery, CancellationToken, Task<ScimPage<T>>> list, ScimQuery query, int limit, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(list);
        ArgumentNullException.ThrowIfNull(query);

        var all = new List<T>();
        while (all.Count < limit) {
            var page = await list(query with { StartIndex = all.Count + 1, Count = Math.Min(100, limit - all.Count) }, cancellationToken);
            all.AddRange(page.Resources);

            if (page.Resources.Count == 0 || all.Count >= page.TotalResults) {
                break;
            }
        }

        return all;
    }

    public static string UserPath(string id) {
        ArgumentNullException.ThrowIfNull(id);
        return $"/Users/{Uri.EscapeDataString(id)}";
    }

    public static string GroupPath(string id) {
        ArgumentNullException.ThrowIfNull(id);
        return $"/Groups/{Uri.EscapeDataString(id)}";
    }

    public void Dispose() {
        _http.Dispose();
    }

    private static SocketsHttpHandler Transport(ScimConnection connection) {
        ArgumentNullException.ThrowIfNull(connection);

        // A redirect followed in here would be a request the log never sees - without its Authorization header, which .NET drops on
        // the way. The 3xx itself is what a person needs to see.
        var transport = new SocketsHttpHandler {
            AutomaticDecompression = DecompressionMethods.All,
            PooledConnectionLifetime = TimeSpan.FromMinutes(2),
            AllowAutoRedirect = false,
        };

        if (connection.AcceptInvalidCertificates) {
            // Asked for per connection, for development servers with self-signed certificates; off unless a person turns it on.
#pragma warning disable CA5359
            transport.SslOptions.RemoteCertificateValidationCallback = (_, _, _, _) => true;
#pragma warning restore CA5359
        }

        return transport;
    }

    /// <summary>The documents of a discovery list: a ListResponse, or the bare array some older servers answer with.</summary>
    /// <param name="response">The answer.</param>
    private static IEnumerable<JsonObject> Documents(ScimResponse response) {
        response.EnsureSuccess();

        return response.Body switch {
            JsonArray list => list.OfType<JsonObject>(),
            JsonObject document when ScimJson.Get(document, "Resources") is { } resources => ScimJson.Items(resources).OfType<JsonObject>(),
            JsonObject document => [document],
            _ => [],
        };
    }
}

/// <summary>Writes PatchOp requests (RFC 7644 section 3.5.2).</summary>
public static class ScimPatch {
    /// <summary>A PatchOp request of the given operations.</summary>
    /// <param name="operations">The operations, in order.</param>
    public static JsonObject Request(params IEnumerable<JsonObject> operations) {
        return new JsonObject {
            ["schemas"] = new JsonArray(ScimSchemas.PATCH_OP),
            ["Operations"] = new JsonArray([.. operations]),
        };
    }

    /// <summary>One operation; the path and the value are left out when null.</summary>
    /// <param name="op">The operation as it is spelled on the wire - identity providers disagree on the casing.</param>
    /// <param name="path">The path, or null.</param>
    /// <param name="value">The value, or null.</param>
    public static JsonObject Operation(string op, string? path, JsonNode? value) {
        var operation = new JsonObject { ["op"] = op };
        if (path is not null) {
            operation["path"] = path;
        }

        if (value is not null) {
            operation["value"] = value;
        }

        return operation;
    }
}
