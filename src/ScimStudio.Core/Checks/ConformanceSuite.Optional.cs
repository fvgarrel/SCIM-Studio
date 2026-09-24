using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using ScimStudio.Core.Scim;
using ScimStudio.Core.Text;
using static ScimStudio.Core.Checks.CheckContext;

namespace ScimStudio.Core.Checks;

/// <summary>
/// What RFC 7644 leaves to the server: versions, bulk requests, password changes and /Me. What the configuration says is not offered is
/// not tried; what it says is offered has to work.
/// </summary>
public sealed partial class ConformanceSuite {
    // Beyond these a limit is a payload no check should send.
    private const int MOST_BULK_OPERATIONS = 10_000;
    private const int MOST_BULK_BYTES = 4 * 1024 * 1024;

    /// <summary>RFC 7644 section 3.14: with versions on, every resource comes with an ETag header, and SHOULD carry it as meta.version.</summary>
    /// <param name="c">The check.</param>
    private async Task EtagAsync(CheckContext c) {
        if (!Config.EtagSupported) {
            Unsupported("note.unsupported", "etag");
        }

        var response = Expect(await c.SendAsync(HttpMethod.Get, ScimClient.UserPath(Alice.Id)), 200);
        var etag = Header(response, "ETag");
        if (etag is null) {
            Fail("note.header", "ETag");
        }

        var version = ScimJson.Text(ScimJson.Get(ScimJson.Get(response.Body, "meta"), "version"));
        if (version is null) {
            c.Warn(Message.Of("note.meta", "meta.version"));
        } else if (version != etag) {
            c.Warn(Message.Of("note.versionMismatch", version, etag));
        }
    }

    private async Task EtagNotModifiedAsync(CheckContext c) {
        var request = new ScimRequest {
            Method = HttpMethod.Get,
            Path = ScimClient.UserPath(Alice.Id),
            Headers = [new("If-None-Match", await EtagOfAsync(c))],
        };

        var response = Expect(await c.SendAsync(request), 304, 200);
        if (response.StatusCode == 200) {
            c.Warn(Message.Of("note.notModifiedIgnored"));
        }
    }

    /// <summary>A change against an old version is refused with 412; against the current one it goes through.</summary>
    /// <param name="c">The check.</param>
    private async Task EtagPreconditionAsync(CheckContext c) {
        RequirePatch();
        var body = ScimPatch.Request(ScimPatch.Operation("replace", "displayName", "Alicia Versioned")).ToJsonString(ScimJson.Compact);
        var patch = new ScimRequest { Method = HttpMethod.Patch, Path = ScimClient.UserPath(Alice.Id), Body = body };

        Expect(await c.SendAsync(patch with { Headers = [new("If-Match", "W/\"scimstudio-stale\"")] }), 412);
        Expect(await c.SendAsync(patch with { Headers = [new("If-Match", await EtagOfAsync(c))] }), 200, 204);
    }

    /// <summary>
    /// A user and a group in one bulk request, the group naming the user by the bulkId it does not have an id for yet (RFC 7644 section
    /// 3.7.2). Whatever the answer created is removed at the end.
    /// </summary>
    /// <param name="c">The check.</param>
    private async Task BulkAsync(CheckContext c) {
        if (!Config.BulkSupported) {
            Unsupported("note.unsupported", "bulk");
        }

        var request = BulkRequest(null,
            new JsonObject { ["method"] = "POST", ["path"] = "/Users", ["bulkId"] = "user", ["data"] = UserBody("bulk", "Bulk", "Tester") },
            new JsonObject {
                ["method"] = "POST",
                ["path"] = "/Groups",
                ["bulkId"] = "group",
                ["data"] = GroupBody(GroupName("bulk"), ["bulkId:user"], ExternalId("group-bulk")),
            });

        var answer = Body(Expect(await c.SendAsync(HttpMethod.Post, "/Bulk", request), 200));
        var operations = ScimJson.Items(ScimJson.Get(answer, "Operations")).OfType<JsonObject>().ToList();
        foreach (var operation in operations) {
            if (OperationStatus(operation) is >= 200 and < 300 && ScimJson.Text(ScimJson.Get(operation, "location")) is { } location) {
                (location.Contains("/Groups/", StringComparison.OrdinalIgnoreCase) ? _groups : _users).Add(LastSegment(location));
            }
        }

        if (!Schemas(answer).Contains(ScimSchemas.BULK_RESPONSE)) {
            c.Warn(Message.Of("note.schemas", ScimSchemas.BULK_RESPONSE));
        }

        if (operations.Count != 2) {
            Fail("note.count", operations.Count, 2);
        }

        var user = BulkResult(operations, "user");
        var group = BulkResult(operations, "group");
        if (!(await ReadGroupAsync(c, group)).Members.Any(m => m.Id == user)) {
            Fail("note.bulkReference");
        }
    }

    /// <summary>With failOnErrors 1 the server stops after the first error: of two failing operations, one is answered.</summary>
    /// <param name="c">The check.</param>
    private static async Task BulkFailOnErrorsAsync(CheckContext c) {
        var request = BulkRequest(1, UnknownDeletion(), UnknownDeletion());
        var answer = Body(Expect(await c.SendAsync(HttpMethod.Post, "/Bulk", request), 200));

        var answered = ScimJson.Items(ScimJson.Get(answer, "Operations")).Count();
        if (answered != 1) {
            c.Warn(Message.Of("note.failOnErrors", answered));
        }
    }

    /// <summary>RFC 7644 section 3.7.4: one operation too many is refused with 413, naming the limit. The operations delete no one.</summary>
    /// <param name="c">The check.</param>
    private async Task BulkMaxOperationsAsync(CheckContext c) {
        var limit = Config.BulkMaxOperations ?? 0;
        if (limit <= 0) {
            Skip("note.bulkLimitUnknown", "maxOperations");
        }

        if (limit > MOST_BULK_OPERATIONS) {
            Skip("note.bulkLimitLarge", "maxOperations", limit);
        }

        var request = BulkRequest(null, [.. Enumerable.Range(0, limit + 1).Select(_ => UnknownDeletion())]);
        if (Config.BulkMaxPayloadSize is { } size && Encoding.UTF8.GetByteCount(request.ToJsonString(ScimJson.Compact)) > size) {
            Skip("note.bulkLimitsOverlap");
        }

        await ExpectTooLargeAsync(c, request, "maxOperations", limit);
    }

    private async Task BulkMaxPayloadAsync(CheckContext c) {
        var size = Config.BulkMaxPayloadSize ?? 0;
        if (size <= 0) {
            Skip("note.bulkLimitUnknown", "maxPayloadSize");
        }

        if (size > MOST_BULK_BYTES) {
            Skip("note.bulkLimitLarge", "maxPayloadSize", size);
        }

        // One harmless operation, and a member no server reads to carry the bytes.
        var request = BulkRequest(null, UnknownDeletion());
        request["scimstudioPadding"] = new string('x', size + 1024);
        await ExpectTooLargeAsync(c, request, "maxPayloadSize", size);
    }

    private async Task ChangePasswordAsync(CheckContext c) {
        if (!Config.ChangePasswordSupported) {
            Unsupported("note.unsupported", "changePassword");
        }

        RequirePatch();
        var spare = await SpareAsync(c, "change-password");
        var operation = ScimPatch.Operation("replace", "password", Password());
        var response = Expect(await TryPatchAsync(c, ScimClient.UserPath(spare.Id), operation), 200, 204);

        if (ScimJson.Has(response.Body, "password") || ScimJson.Has((await ReadUserAsync(c, spare.Id)).Resource, "password")) {
            Fail("note.passwordReturned");
        }
    }

    /// <summary>
    /// RFC 7644 section 3.11: /Me is the user behind the token - answered, redirected with 308, or not offered. Only read: a write to /Me
    /// would change whoever the token belongs to.
    /// </summary>
    /// <param name="c">The check.</param>
    private static async Task MeAsync(CheckContext c) {
        var response = await c.SendAsync(HttpMethod.Get, "/Me");
        Declined(response, "GET /Me", 400, 401, 403, 404, 405, 501);

        if (Expect(response, 200, 308).StatusCode == 308) {
            if (response.Location is null) {
                c.Warn(Message.Of("note.header", "Location"));
            }

            return;
        }

        if (Header(response, "Location") is null) {
            c.Warn(Message.Of("note.header", "Location"));
        }
    }

    private async Task<string> EtagOfAsync(CheckContext c) {
        var etag = Header(Expect(await c.SendAsync(HttpMethod.Get, ScimClient.UserPath(Alice.Id)), 200), "ETag");
        if (etag is null) {
            Fail("note.header", "ETag");
        }

        return etag;
    }

    private static async Task ExpectTooLargeAsync(CheckContext c, JsonObject request, string limit, int value) {
        var response = Expect(await c.SendAsync(HttpMethod.Post, "/Bulk", request), 413);

        // RFC 7644 section 3.7.4: the error MUST name the limit; by its name or by its value will do.
        var detail = Detail(response);
        var named = detail.Contains(limit, StringComparison.OrdinalIgnoreCase)
            || detail.Contains(value.ToString(CultureInfo.InvariantCulture), StringComparison.Ordinal);
        if (!named) {
            c.Warn(Message.Of("note.limitNotNamed", limit));
        }
    }

    private static JsonObject BulkRequest(int? failOnErrors, params JsonObject[] operations) {
        var request = new JsonObject { ["schemas"] = new JsonArray(ScimSchemas.BULK_REQUEST) };
        if (failOnErrors is { } errors) {
            request["failOnErrors"] = errors;
        }

        request["Operations"] = new JsonArray([.. operations]);
        return request;
    }

    /// <summary>A deletion of a user that does not exist: an operation that fails without touching anything.</summary>
    private static JsonObject UnknownDeletion() {
        return new JsonObject { ["method"] = "DELETE", ["path"] = ScimClient.UserPath(Guid.NewGuid().ToString()) };
    }

    /// <summary>The id a bulk operation created; RFC 7644 section 3.7 has the server answer each with the bulkId it was sent.</summary>
    /// <param name="operations">The answered operations.</param>
    /// <param name="bulkId">The bulkId sent.</param>
    private static string BulkResult(List<JsonObject> operations, string bulkId) {
        var operation = operations.Find(o => ScimJson.Text(ScimJson.Get(o, "bulkId")) == bulkId);
        if (operation is null) {
            Fail("note.bulkIdMissing", bulkId);
        }

        if (OperationStatus(operation) != 201) {
            Fail("note.bulkStatus", bulkId, ScimJson.Text(ScimJson.Get(operation, "status")) ?? "—");
        }

        var location = ScimJson.Text(ScimJson.Get(operation, "location"));
        if (location is null) {
            Fail("note.absent", "location");
        }

        return LastSegment(location);
    }

    /// <summary>An operation's status: a string as RFC 7644 writes it, a number, or the code inside an object as its prose describes.</summary>
    /// <param name="operation">The answered operation.</param>
    private static int? OperationStatus(JsonObject operation) {
        var status = ScimJson.Get(operation, "status");
        return ScimJson.Number(status is JsonObject detail ? ScimJson.Get(detail, "code") : status);
    }

    private static string LastSegment(string location) {
        var trimmed = location.TrimEnd('/');
        return Uri.UnescapeDataString(trimmed[(trimmed.LastIndexOf('/') + 1)..]);
    }
}
