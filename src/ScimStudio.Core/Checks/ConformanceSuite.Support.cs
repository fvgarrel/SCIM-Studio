using System.Text.Json.Nodes;
using ScimStudio.Core.Scim;
using static ScimStudio.Core.Checks.CheckContext;

namespace ScimStudio.Core.Checks;

/// <summary>What the checks share: names that carry the marker, the bodies they send, and the ways they read an answer.</summary>
public sealed partial class ConformanceSuite {
    /// <summary>An attribute no schema declares, for the checks of what a server does with one.</summary>
    private const string UNKNOWN_ATTRIBUTE = "scimstudioUnknown";

    // Plain text a check can write without the server reading a format into it, as it would into a language tag or a time zone - in the
    // order they are tried. RFC 7643 makes every one optional, so a check takes the first the server's schema declares.
    private static readonly string[] TextAttributes = ["nickName", "title", "displayName"];

    // Those of them the users a run creates are never given.
    private static readonly string[] UnsetAttributes = ["nickName", "title"];

    private void RequireFilter() {
        if (_config is { FilterSupported: false }) {
            Unsupported("note.unsupported", "filter");
        }
    }

    private void RequirePatch() {
        if (_config is { PatchSupported: false }) {
            Unsupported("note.unsupported", "PATCH");
        }
    }

    private void RequireSort() {
        if (_config is { SortSupported: false }) {
            Unsupported("note.unsupported", "sort");
        }
    }

    /// <summary>
    /// The first of the given User attributes the server's schema declares as a single text a client writes and reads back; null when it
    /// declares none of them, or there was no schema to ask.
    /// </summary>
    /// <param name="candidates">The attributes, in the order they are wanted.</param>
    private string? Declared(IEnumerable<string> candidates) {
        var attributes = ScimJson.Items(ScimJson.Get(_userSchema, "attributes")).Select(AttributeDefinition.Read).ToList();
        return candidates.FirstOrDefault(name => attributes.Exists(a =>
            string.Equals(a.Name, name, StringComparison.OrdinalIgnoreCase)
            && string.Equals(a.Type, "string", StringComparison.OrdinalIgnoreCase)
            && !a.MultiValued
            && string.Equals(a.Mutability, "readWrite", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(a.Returned, "never", StringComparison.OrdinalIgnoreCase)));
    }

    private string UserName(string who) {
        return $"{Prefix}{_marker}-{who}@example.com";
    }

    private string ExternalId(string what) {
        return $"{Prefix}{_marker}-{what}";
    }

    private string GroupName(string what) {
        return $"{Prefix}{_marker} {what}";
    }

    private string Both() {
        return ScimFilterText.Any(ScimFilterText.Eq("userName", UserName("alice")), ScimFilterText.Eq("userName", UserName("bob")));
    }

    private JsonObject UserBody(string who, string givenName, string familyName) {
        return new JsonObject {
            ["schemas"] = new JsonArray(ScimSchemas.USER),
            ["externalId"] = ExternalId(who),
            ["userName"] = UserName(who),
            ["name"] = new JsonObject { ["givenName"] = givenName, ["familyName"] = familyName },
            ["displayName"] = $"{givenName} {familyName}",
            ["emails"] = new JsonArray(Email(UserName(who), "work", primary: true)),
            ["active"] = true,
        };
    }

    /// <summary>A group's body; the externalId is the first group's unless given, so a PUT sends back what the POST sent.</summary>
    /// <param name="displayName">The name.</param>
    /// <param name="memberIds">The members' ids.</param>
    /// <param name="externalId">The externalId, when not the first group's.</param>
    private JsonObject GroupBody(string displayName, IEnumerable<string> memberIds, string? externalId = null) {
        return new JsonObject {
            ["schemas"] = new JsonArray(ScimSchemas.GROUP),
            ["externalId"] = externalId ?? ExternalId("group"),
            ["displayName"] = displayName,
            ["members"] = new JsonArray([.. memberIds.Select(id => new JsonObject { ["value"] = id })]),
        };
    }

    private static JsonObject Email(string address, string type, bool primary = false) {
        var email = new JsonObject { ["value"] = address, ["type"] = type };
        if (primary) {
            email["primary"] = true;
        }

        return email;
    }

    /// <summary>
    /// A user of the check's own, for what might leave a user in a state the next check would trip over - a changed id, a lost userName.
    /// It is removed with the rest at the end.
    /// </summary>
    /// <param name="c">The check.</param>
    /// <param name="who">What the user is for; part of its userName.</param>
    /// <param name="shape">Changes the body before it is sent.</param>
    private async Task<ScimUser> SpareAsync(CheckContext c, string who, Action<JsonObject>? shape = null) {
        var body = UserBody(who, "Spare", "Tester");
        shape?.Invoke(body);

        var response = Track(await c.SendAsync(HttpMethod.Post, "/Users", body), _users);
        return ScimUser.Read(Body(Expect(response, 201)));
    }

    /// <summary>Keeps the id of a resource the answer created, so it is removed at the end even when the check then fails.</summary>
    /// <param name="response">The answer to a POST or PUT.</param>
    /// <param name="created">The ids to remove at the end.</param>
    private static ScimResponse Track(ScimResponse response, List<string> created) {
        if (response.IsSuccess && ScimJson.Text(ScimJson.Get(response.Body, "id")) is { Length: > 0 } id && !created.Contains(id)) {
            created.Add(id);
        }

        return response;
    }

    /// <summary>Removes a user a server should not have created, at once, so the users the other checks count stay two.</summary>
    /// <param name="c">The check.</param>
    /// <param name="response">The answer that created it.</param>
    private async Task DiscardAsync(CheckContext c, ScimResponse response) {
        if (ScimJson.Text(ScimJson.Get(response.Body, "id")) is { Length: > 0 } id) {
            var deleted = await c.SendAsync(HttpMethod.Delete, ScimClient.UserPath(id));
            if (deleted.IsSuccess) {
                _users.Remove(id);
            }
        }
    }

    /// <summary>Takes one of the given answers as the server's way of saying it does not offer what was asked for.</summary>
    /// <param name="response">The answer.</param>
    /// <param name="what">What was asked for, as it went over the wire.</param>
    /// <param name="statuses">The statuses that mean "not here".</param>
    private static void Declined(ScimResponse response, string what, params int[] statuses) {
        if (statuses.Contains(response.StatusCode)) {
            Unsupported("note.declined", what, response.StatusCode, Detail(response));
        }
    }

    private static string Detail(ScimResponse response) {
        return ScimError.Read(response.StatusCode, response.Text).Detail ?? string.Empty;
    }

    private static Task<ScimResponse> TryPatchAsync(CheckContext c, string path, params JsonObject[] operations) {
        return c.SendAsync(HttpMethod.Patch, path, ScimPatch.Request(operations));
    }

    private static async Task PatchAsync(CheckContext c, string path, params JsonObject[] operations) {
        Expect(await TryPatchAsync(c, path, operations), 200, 204);
    }

    private static async Task<ScimUser> ReadUserAsync(CheckContext c, string id) {
        return ScimUser.Read(Body(Expect(await c.SendAsync(HttpMethod.Get, ScimClient.UserPath(id)), 200)));
    }

    private static async Task<ScimGroup> ReadGroupAsync(CheckContext c, string id) {
        return ScimGroup.Read(Body(Expect(await c.SendAsync(HttpMethod.Get, ScimClient.GroupPath(id)), 200)));
    }

    private static async Task<ScimPage<ScimUser>> ListUsersAsync(CheckContext c, ScimQuery query) {
        return ScimPage.Read(Body(Expect(await c.SendAsync(HttpMethod.Get, $"/Users{query.ToQueryString()}"), 200)), ScimUser.Read);
    }

    /// <summary>The users a filter finds. Filtering is optional, and so is each operator: a server that refuses one does not offer it.</summary>
    /// <param name="c">The check.</param>
    /// <param name="filter">The filter.</param>
    private static async Task<ScimPage<ScimUser>> FindAsync(CheckContext c, string filter) {
        var response = await c.SendAsync(HttpMethod.Get, $"/Users{new ScimQuery { Filter = filter }.ToQueryString()}");
        Declined(response, $"filter={filter}", 400, 501);
        return ScimPage.Read(Body(Expect(response, 200)), ScimUser.Read);
    }

    private static async Task ExpectActiveAsync(CheckContext c, string id, bool active) {
        var user = await ReadUserAsync(c, id);
        if (user.Active != active) {
            Fail("note.value", "active", Literal(user.Active), Literal(active));
        }
    }

    private static string Literal(bool? value) {
        return value switch {
            true => "true",
            false => "false",
            null => "—",
        };
    }

    private static void Count<T>(ScimPage<T> page, int expected) {
        if (page.TotalResults != expected || page.Resources.Count != expected) {
            Fail("note.count", page.Resources.Count, expected);
        }
    }

    private static void Member(ScimGroup group, ScimUser user, bool expected) {
        if (group.Members.Any(m => m.Id == user.Id) != expected) {
            Fail(expected ? "note.notMember" : "note.stillMember", user.UserName);
        }
    }

    private static List<string> Emails(ScimUser user) {
        return [.. ScimJson.Items(ScimJson.Get(user.Resource, "emails")).Select(e => ScimJson.Text(ScimJson.Get(e, "value"))).OfType<string>()];
    }

    private static string? Enterprise(ScimUser user, string attribute) {
        return ScimJson.Text(ScimJson.Get(ScimJson.Get(user.Resource, ScimSchemas.ENTERPRISE_USER), attribute));
    }

    /// <summary>The schema URNs a document lists, compared without case like every URN in SCIM.</summary>
    /// <param name="document">The document.</param>
    private static HashSet<string> Schemas(JsonObject document) {
        return new(ScimJson.Items(ScimJson.Get(document, "schemas")).Select(ScimJson.Text).OfType<string>(), StringComparer.OrdinalIgnoreCase);
    }

    private static string? Header(ScimResponse response, string name) {
        return response.Exchange?.ResponseHeaders.FirstOrDefault(h => string.Equals(h.Key, name, StringComparison.OrdinalIgnoreCase)).Value;
    }

    private static IEnumerable<JsonObject> Documents(ScimResponse response) {
        return response.Body switch {
            JsonArray list => list.OfType<JsonObject>(),
            JsonObject document => ScimJson.Items(ScimJson.Get(document, "Resources")).OfType<JsonObject>(),
            _ => [],
        };
    }

    /// <summary>Whether a resource's address ends in its endpoint and id, escaped or not.</summary>
    /// <param name="location">The address.</param>
    /// <param name="path">The resource's path, as <see cref="ScimClient.UserPath"/> writes it.</param>
    /// <param name="endpoint">The endpoint, e.g. <c>/Users</c>.</param>
    /// <param name="id">The resource's id.</param>
    private static bool PointsTo(string location, string path, string endpoint, string id) {
        var trimmed = location.TrimEnd('/');
        return trimmed.EndsWith(path, StringComparison.OrdinalIgnoreCase) || trimmed.EndsWith($"{endpoint}/{id}", StringComparison.OrdinalIgnoreCase);
    }
}
