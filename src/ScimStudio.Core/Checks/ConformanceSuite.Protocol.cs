using System.Text.Json;
using System.Text.Json.Nodes;
using ScimStudio.Core.Scim;
using ScimStudio.Core.Text;
using static ScimStudio.Core.Checks.CheckContext;

namespace ScimStudio.Core.Checks;

/// <summary>The protocol around the resources: transport, errors, media types, how names are spelled, and text beyond ASCII.</summary>
public sealed partial class ConformanceSuite {
    private const string UNICODE_NAME = "Zoë Ångström-Łukasiewicz 山田 🙂";

    /// <summary>RFC 7644 section 7.2: SCIM travels over TLS. Plain HTTP to the machine itself stays on it and passes.</summary>
    /// <param name="c">The check.</param>
    private static Task TlsAsync(CheckContext c) {
        var connection = c.Client.Connection;
        if (connection.BaseUrl.Scheme != Uri.UriSchemeHttps && !connection.BaseUrl.IsLoopback) {
            Fail("note.plainHttp", connection.BaseUrl.Host);
        }

        if (connection.BaseUrl.Scheme == Uri.UriSchemeHttps && connection.AcceptInvalidCertificates) {
            c.Warn(Message.Of("note.certificateUnchecked"));
        }

        return Task.CompletedTask;
    }

    /// <summary>RFC 7644 section 3.12: an error is a JSON document with the Error schema and the status as a string.</summary>
    /// <param name="c">The check.</param>
    private static async Task ErrorFormatAsync(CheckContext c) {
        var response = Expect(await c.SendAsync(HttpMethod.Get, ScimClient.UserPath(Guid.NewGuid().ToString())), 404);
        var error = response.Body as JsonObject;
        if (error is null) {
            Fail("note.errorNotJson");
        }

        if (!Schemas(error).Contains(ScimSchemas.ERROR)) {
            c.Warn(Message.Of("note.schemas", ScimSchemas.ERROR));
        }

        var status = ScimJson.Get(error, "status");
        if (status is null) {
            Fail("note.absent", "status");
        }

        Equal("status", ScimJson.Text(status), "404");
        if (status.GetValueKind() != JsonValueKind.String) {
            c.Warn(Message.Of("note.statusNumber"));
        }
    }

    private async Task UnknownEndpointAsync(CheckContext c) {
        Expect(await c.SendAsync(HttpMethod.Get, $"/ScimStudio{_marker}Nowhere"), 404);
    }

    private async Task MediaTypeAsync(CheckContext c) {
        c.ExpectMediaType(Expect(await c.SendAsync(HttpMethod.Get, ScimClient.UserPath(Alice.Id)), 200));
    }

    /// <summary>
    /// RFC 7644 section 3.8: application/scim+json MUST be accepted, application/json SHOULD, and without an Accept header the answer is
    /// SCIM's JSON.
    /// </summary>
    /// <param name="c">The check.</param>
    /// <param name="accept">The Accept header, or empty for none.</param>
    private async Task AcceptAsync(CheckContext c, string accept) {
        var response = await c.SendAsync(new ScimRequest { Method = HttpMethod.Get, Path = ScimClient.UserPath(Alice.Id), Accept = accept });
        if (response.StatusCode == 406 && accept == "application/json") {
            c.Warn(Message.Of("note.acceptRefused", accept));
            return;
        }

        Equal("id", ScimJson.Text(ScimJson.Get(Body(Expect(response, 200)), "id")), Alice.Id);
    }

    /// <summary>Scripts and many clients send application/json; a server that refuses it only because of the label is hard to talk to.</summary>
    /// <param name="c">The check.</param>
    private async Task ContentTypeJsonAsync(CheckContext c) {
        RequirePatch();
        var patch = ScimPatch.Request(ScimPatch.Operation("replace", "displayName", "Alicia Json"));
        var request = new ScimRequest {
            Method = HttpMethod.Patch,
            Path = ScimClient.UserPath(Alice.Id),
            Body = patch.ToJsonString(ScimJson.Compact),
            ContentType = "application/json",
        };

        var response = await c.SendAsync(request);
        if (response.StatusCode is 400 or 415) {
            c.Warn(Message.Of("note.contentTypeRefused", request.ContentType, response.StatusCode));
            return;
        }

        Expect(response, 200, 204);
        Equal("displayName", (await ReadUserAsync(c, Alice.Id)).DisplayName, "Alicia Json");
    }

    /// <summary>RFC 7643 section 2.1: attribute names are case-insensitive - in a body as much as anywhere.</summary>
    /// <param name="c">The check.</param>
    private async Task AttributeCaseAsync(CheckContext c) {
        var body = new JsonObject {
            ["SCHEMAS"] = new JsonArray(ScimSchemas.USER),
            ["ExternalID"] = ExternalId("case"),
            ["USERNAME"] = UserName("case"),
            ["Name"] = new JsonObject { ["GIVENNAME"] = "Case", ["familyname"] = "Tester" },
            ["Active"] = true,
        };

        var user = ScimUser.Read(Body(Expect(Track(await c.SendAsync(HttpMethod.Post, "/Users", body), _users), 201)));
        Equal("userName", user.UserName, UserName("case"), ignoreCase: true);
        Equal("name.givenName", user.GivenName, "Case");
    }

    private async Task PathCaseAsync(CheckContext c) {
        RequirePatch();
        await PatchAsync(c, ScimClient.UserPath(Alice.Id), ScimPatch.Operation("replace", "NAME.GIVENNAME", "Upper"));
        Equal("name.givenName", (await ReadUserAsync(c, Alice.Id)).GivenName, "Upper");
    }

    /// <summary>RFC 7644 section 3.8: UTF-8 throughout - accents, other scripts and emoji come back as they went in.</summary>
    /// <param name="c">The check.</param>
    private async Task UnicodeAsync(CheckContext c) {
        var spare = await SpareAsync(c, "unicode", body => body["displayName"] = UNICODE_NAME);
        Equal("displayName", (await ReadUserAsync(c, spare.Id)).DisplayName, UNICODE_NAME);
    }
}
