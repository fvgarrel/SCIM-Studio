using System.Text;
using System.Text.Json.Nodes;
using ScimStudio.Core.Scim;
using ScimStudio.Core.Text;
using static ScimStudio.Core.Checks.CheckContext;

namespace ScimStudio.Core.Checks;

/// <summary>Discovery and authentication: what the server says about itself, and whom it lets in.</summary>
public sealed partial class ConformanceSuite {
    private async Task ConfigAsync(CheckContext c) {
        var response = Expect(await c.SendAsync(HttpMethod.Get, "/ServiceProviderConfig"), 200);
        c.ExpectMediaType(response);

        var document = Body(response);
        if (!Schemas(document).Contains(ScimSchemas.SERVICE_PROVIDER_CONFIG)) {
            c.Warn(Message.Of("note.schemas", ScimSchemas.SERVICE_PROVIDER_CONFIG));
        }

        _config = ServiceProviderConfig.Read(document);
    }

    /// <summary>RFC 7643 section 5 makes every feature and its limits required: a client reading a missing one learns nothing.</summary>
    /// <param name="c">The check.</param>
    private Task ConfigCompleteAsync(CheckContext c) {
        var document = Config.Document;
        var missing = new List<string>();

        foreach (var feature in new[] { "patch", "bulk", "filter", "changePassword", "sort", "etag" }) {
            if (ScimJson.Flag(ScimJson.Get(ScimJson.Get(document, feature), "supported")) is null) {
                missing.Add($"{feature}.supported");
            }
        }

        foreach (var (feature, limit) in new[] { ("bulk", "maxOperations"), ("bulk", "maxPayloadSize"), ("filter", "maxResults") }) {
            if (ScimJson.Number(ScimJson.Get(ScimJson.Get(document, feature), limit)) is null) {
                missing.Add($"{feature}.{limit}");
            }
        }

        var schemes = ScimJson.Items(ScimJson.Get(document, "authenticationSchemes")).ToList();
        if (schemes.Count == 0) {
            missing.Add("authenticationSchemes");
        }

        foreach (var attribute in new[] { "type", "name", "description" }) {
            if (schemes.Exists(s => ScimJson.Text(ScimJson.Get(s, attribute)) is null)) {
                missing.Add($"authenticationSchemes.{attribute}");
            }
        }

        if (missing.Count > 0) {
            c.Warn(Message.Of("note.configMissing", string.Join(", ", missing)));
        }

        return Task.CompletedTask;
    }

    private async Task ResourceTypesAsync(CheckContext c) {
        var response = Expect(await c.SendAsync(HttpMethod.Get, "/ResourceTypes"), 200);
        ExpectListResponse(c, response);

        var types = Documents(response).Select(ResourceTypeInfo.Read).ToList();
        if (!types.Exists(t => string.Equals(t.Endpoint, "/Users", StringComparison.OrdinalIgnoreCase))) {
            Fail("note.resourceType", "User", "/Users");
        }

        if (!types.Exists(t => string.Equals(t.Endpoint, "/Groups", StringComparison.OrdinalIgnoreCase))) {
            c.Warn(Message.Of("note.resourceType", "Group", "/Groups"));
        }

        _resourceTypes = types;
    }

    private async Task ResourceTypeAsync(CheckContext c) {
        var users = _resourceTypes.First(t => string.Equals(t.Endpoint, "/Users", StringComparison.OrdinalIgnoreCase));
        var id = users.Id.Length > 0 ? users.Id : users.Name;

        var response = await c.SendAsync(HttpMethod.Get, $"/ResourceTypes/{Uri.EscapeDataString(id)}");
        Declined(response, $"GET /ResourceTypes/{id}", 404, 405, 501);

        var type = ResourceTypeInfo.Read(Body(Expect(response, 200)));
        Equal("endpoint", type.Endpoint, "/Users", ignoreCase: true);
        Equal("schema", type.Schema, ScimSchemas.USER, ignoreCase: true);
    }

    private async Task SchemasAsync(CheckContext c) {
        var response = Expect(await c.SendAsync(HttpMethod.Get, "/Schemas"), 200);
        ExpectListResponse(c, response);

        var documents = Documents(response).ToList();
        var ids = documents.Select(d => ScimJson.Text(ScimJson.Get(d, "id"))).OfType<string>().ToHashSet(StringComparer.OrdinalIgnoreCase);

        if (!ids.Contains(ScimSchemas.USER)) {
            Fail("note.schema", ScimSchemas.USER);
        }

        if (!ids.Contains(ScimSchemas.GROUP)) {
            c.Warn(Message.Of("note.schema", ScimSchemas.GROUP));
        }

        _schemaIds = ids;
        _userSchema = documents.Find(d => string.Equals(ScimJson.Text(ScimJson.Get(d, "id")), ScimSchemas.USER, StringComparison.OrdinalIgnoreCase));
    }

    private static async Task SchemaAsync(CheckContext c) {
        // A URN's colons are fine in a path segment; escaping them would test the server's decoding instead.
        var response = await c.SendAsync(HttpMethod.Get, $"/Schemas/{ScimSchemas.USER}");
        Declined(response, $"GET /Schemas/{ScimSchemas.USER}", 404, 405, 501);

        var schema = SchemaDefinition.Read(Body(Expect(response, 200)));
        Equal("id", schema.Id, ScimSchemas.USER, ignoreCase: true);
        if (schema.Attributes.Count == 0) {
            Fail("note.absent", "attributes");
        }
    }

    /// <summary>Clients match people by userName; the schema has to say it is required, unique and compared without case.</summary>
    /// <param name="c">The check.</param>
    private Task UserNameDefinitionAsync(CheckContext c) {
        var attribute = ScimJson.Items(ScimJson.Get(_userSchema, "attributes"))
            .Select(AttributeDefinition.Read)
            .FirstOrDefault(a => string.Equals(a.Name, "userName", StringComparison.OrdinalIgnoreCase));
        if (attribute is null) {
            Fail("note.absent", "userName");
        }

        if (!attribute.Required) {
            c.Warn(Message.Of("note.definition", "userName", "required", "false", "true"));
        }

        if (attribute.CaseExact) {
            c.Warn(Message.Of("note.definition", "userName", "caseExact", "true", "false"));
        }

        if (!string.Equals(attribute.Uniqueness, "server", StringComparison.OrdinalIgnoreCase)) {
            c.Warn(Message.Of("note.definition", "userName", "uniqueness", attribute.Uniqueness, "server"));
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// RFC 7644 section 4: discovery ignores query parameters, and a filter SHOULD be refused with 403, so a client cannot take one as
    /// applied.
    /// </summary>
    /// <param name="c">The check.</param>
    private static async Task FilterRefusedAsync(CheckContext c) {
        var query = new ScimQuery { Filter = ScimFilterText.Eq("name", "User") }.ToQueryString();
        foreach (var endpoint in new[] { "/ResourceTypes", "/Schemas" }) {
            var response = Expect(await c.SendAsync(HttpMethod.Get, $"{endpoint}{query}"), 403, 200, 400);
            if (response.StatusCode != 403) {
                c.Warn(Message.Of("note.filterNotRefused", endpoint, response.StatusCode));
            }
        }
    }

    private static async Task MissingTokenAsync(CheckContext c) {
        var request = new ScimRequest { Method = HttpMethod.Get, Path = "/Users?count=1", Authorization = string.Empty };
        var response = Expect(await c.SendAsync(request), 401);
        if (Header(response, "WWW-Authenticate") is null) {
            c.Warn(Message.Of("note.header", "WWW-Authenticate"));
        }
    }

    private async Task WrongTokenAsync(CheckContext c) {
        await RefusedAsync(c, $"Bearer scimstudio-wrong-{_marker}");
    }

    /// <summary>Made-up Basic credentials: a server that lets them in takes anything.</summary>
    /// <param name="c">The check.</param>
    private async Task WrongSchemeAsync(CheckContext c) {
        await RefusedAsync(c, $"Basic {Convert.ToBase64String(Encoding.UTF8.GetBytes($"scimstudio:{_marker}"))}");
    }

    private static async Task RefusedAsync(CheckContext c, string authorization) {
        var response = await c.SendAsync(new ScimRequest { Method = HttpMethod.Get, Path = "/Users?count=1", Authorization = authorization });
        if (response.StatusCode == 403) {
            c.Warn(Message.Of("note.forbidden"));
            return;
        }

        Expect(response, 401);
    }

    /// <summary>RFC 7235 compares the scheme without case; the right token as <c>bearer</c> has to get in.</summary>
    /// <param name="c">The check.</param>
    private static async Task SchemeCaseAsync(CheckContext c) {
        var request = new ScimRequest { Method = HttpMethod.Get, Path = "/Users?count=1", Authorization = $"bearer {c.Client.Connection.Token}" };
        var response = await c.SendAsync(request);
        if (response.StatusCode is 401 or 403) {
            c.Warn(Message.Of("note.schemeCase", response.StatusCode));
            return;
        }

        Expect(response, 200);
    }

    /// <summary>RFC 7644 section 3.4.2: a list comes as a ListResponse, even from discovery.</summary>
    /// <param name="c">The check.</param>
    /// <param name="response">The list.</param>
    private static void ExpectListResponse(CheckContext c, ScimResponse response) {
        if (response.Body is not JsonObject list || !Schemas(list).Contains(ScimSchemas.LIST_RESPONSE)) {
            c.Warn(Message.Of("note.listResponse"));
        }
    }
}
