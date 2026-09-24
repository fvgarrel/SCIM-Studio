using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json.Nodes;
using ScimStudio.DemoServer;

namespace ScimStudio.Tests.DemoServer;

/// <summary>A demo server of its own for one test class, empty unless <see cref="Options"/> says otherwise.</summary>
public class DemoServerFixture : IAsyncLifetime {
    public DemoScimServer Server { get; private set; } = null!;
    public ScimTestClient Client { get; private set; } = null!;

    protected virtual DemoServerOptions Options => new() { Seed = false };

    public async ValueTask InitializeAsync() {
        Server = await DemoScimServer.StartAsync(Options, TestContext.Current.CancellationToken);
        Client = new ScimTestClient(Server);
        await PopulateAsync(Client);
    }

    public async ValueTask DisposeAsync() {
        Client.Dispose();
        await Server.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    /// <summary>Creates what the tests of a class read, once, before the first of them runs.</summary>
    /// <param name="client">A client with the server's token.</param>
    protected virtual Task PopulateAsync(ScimTestClient client) {
        return Task.CompletedTask;
    }
}

/// <summary>PatchOp bodies (RFC 7644 section 3.5.2) for the tests that send one.</summary>
public static class PatchOps {
    /// <summary>A PatchOp that replaces one attribute.</summary>
    /// <param name="path">The path of the attribute.</param>
    /// <param name="value">The new value, as JSON.</param>
    public static string Replace(string path, string value) {
        return $$"""
            {
              "schemas": ["urn:ietf:params:scim:api:messages:2.0:PatchOp"],
              "Operations": [{ "op": "replace", "path": "{{path}}", "value": {{value}} }]
            }
            """;
    }
}

/// <summary>A response read in full, so a test can assert on it without holding the connection.</summary>
public sealed record ScimResponse(HttpStatusCode Status, HttpResponseMessage Message, string Text) {
    public JsonObject Body => JsonNode.Parse(Text)!.AsObject();
    public string? MediaType => Message.Content.Headers.ContentType?.MediaType;
    public string? CharSet => Message.Content.Headers.ContentType?.CharSet;

    /// <summary>The <c>scimType</c> of an error response, or null.</summary>
    public string? ScimType => (string?)Body["scimType"];
}

/// <summary>An HTTP client for the demo server that sends the token and SCIM JSON.</summary>
public sealed class ScimTestClient : IDisposable {
    private readonly HttpClient _http;

    public ScimTestClient(DemoScimServer server, string? token = null) {
        _http = new HttpClient { BaseAddress = new Uri(server.BaseUrl + "/") };
        _http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token ?? server.Token);
    }

    public Task<ScimResponse> GetAsync(string path) {
        return SendAsync(HttpMethod.Get, path);
    }

    public Task<ScimResponse> PostAsync(string path, string json) {
        return SendAsync(HttpMethod.Post, path, json);
    }

    public Task<ScimResponse> PutAsync(string path, string json) {
        return SendAsync(HttpMethod.Put, path, json);
    }

    public Task<ScimResponse> PatchAsync(string path, string json) {
        return SendAsync(HttpMethod.Patch, path, json);
    }

    public Task<ScimResponse> DeleteAsync(string path) {
        return SendAsync(HttpMethod.Delete, path);
    }

    public Task<ScimResponse> SendAsync(HttpMethod method, string path, string? json = null, string mediaType = "application/scim+json") {
        return SendAsync(method, path, json, mediaType, null);
    }

    /// <summary>Sends a request with one more header, such as If-Match, which goes out as it is, without validation.</summary>
    /// <param name="method">The method.</param>
    /// <param name="path">The path, relative to the base URL.</param>
    /// <param name="header">The name and the value of the header.</param>
    /// <param name="json">The body, or null for none.</param>
    public Task<ScimResponse> SendAsync(HttpMethod method, string path, (string Name, string Value) header, string? json = null) {
        return SendAsync(method, path, json, "application/scim+json", header);
    }

    private async Task<ScimResponse> SendAsync(
        HttpMethod method, string path, string? json, string mediaType, (string Name, string Value)? header) {
        using var request = new HttpRequestMessage(method, path);
        if (json is not null) {
            request.Content = new StringContent(json, Encoding.UTF8, mediaType);
        }
        if (header is { } extra) {
            request.Headers.TryAddWithoutValidation(extra.Name, extra.Value);
        }
        var response = await _http.SendAsync(request, TestContext.Current.CancellationToken);
        var text = await response.Content.ReadAsStringAsync(TestContext.Current.CancellationToken);
        return new ScimResponse(response.StatusCode, response, text);
    }

    /// <summary>Creates a user and answers its id.</summary>
    /// <param name="json">The body of the POST.</param>
    public async Task<string> CreateUserAsync(string json) {
        var response = await PostAsync("Users", json);
        Assert.Equal(HttpStatusCode.Created, response.Status);
        return (string)response.Body["id"]!;
    }

    /// <summary>Creates a group and answers its id.</summary>
    /// <param name="json">The body of the POST.</param>
    public async Task<string> CreateGroupAsync(string json) {
        var response = await PostAsync("Groups", json);
        Assert.Equal(HttpStatusCode.Created, response.Status);
        return (string)response.Body["id"]!;
    }

    public void Dispose() {
        _http.Dispose();
    }
}
