using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ScimStudio.Core.Scim;
using ScimStudio.Core.Text;
using static ScimStudio.Core.Checks.CheckContext;

namespace ScimStudio.Core.Checks;

/// <summary>Users: creating, reading and refusing them, and changing them with PATCH and PUT.</summary>
public sealed partial class ConformanceSuite {
    private const string EARLY = "2000-01-01T00:00:00Z";

    private async Task CreateUsersAsync(CheckContext c) {
        var alice = Track(await c.SendAsync(HttpMethod.Post, "/Users", UserBody("alice", "Alice", "Tester")), _users);
        Expect(alice, 201);
        c.ExpectMediaType(alice);

        var created = ScimUser.Read(Body(alice));
        if (string.IsNullOrEmpty(created.Id)) {
            Fail("note.absent", "id");
        }

        Equal("userName", created.UserName, UserName("alice"), ignoreCase: true);
        if (alice.Location is null) {
            c.Warn(Message.Of("note.header", "Location"));
        }

        if (!string.Equals(ScimJson.Text(ScimJson.Get(ScimJson.Get(created.Resource, "meta"), "resourceType")), "User", StringComparison.Ordinal)) {
            c.Warn(Message.Of("note.meta", "meta.resourceType"));
        }

        _alice = created;
        _aliceLocation = alice.Location;

        var bob = Track(await c.SendAsync(HttpMethod.Post, "/Users", UserBody("bob", "Bob", "Tester")), _users);
        _bob = ScimUser.Read(Body(Expect(bob, 201)));
    }

    private async Task GetUserAsync(CheckContext c) {
        var user = await ReadUserAsync(c, Alice.Id);
        Equal("id", user.Id, Alice.Id);
        Equal("userName", user.UserName, Alice.UserName, ignoreCase: true);

        if (user.Created is null) {
            c.Warn(Message.Of("note.meta", "meta.created"));
        }

        if (user.Location is null) {
            c.Warn(Message.Of("note.meta", "meta.location"));
        }
    }

    /// <summary>What RFC 7643 section 3.1 puts on every resource, and that where it lives is said the same way twice.</summary>
    /// <param name="c">The check.</param>
    private async Task UserRepresentationAsync(CheckContext c) {
        var user = await ReadUserAsync(c, Alice.Id);
        if (!Schemas(user.Resource).Contains(ScimSchemas.USER)) {
            c.Warn(Message.Of("note.schemas", ScimSchemas.USER));
        }

        if (user.LastModified is null) {
            c.Warn(Message.Of("note.meta", "meta.lastModified"));
        } else if (user.Created is { } created && user.LastModified < created) {
            c.Warn(Message.Of("note.modifiedBeforeCreated"));
        }

        if (user.Location is { } location) {
            if (!Uri.IsWellFormedUriString(location, UriKind.Absolute)) {
                c.Warn(Message.Of("note.locationRelative", location));
            } else if (!PointsTo(location, ScimClient.UserPath(Alice.Id), "/Users", Alice.Id)) {
                c.Warn(Message.Of("note.locationWrong", location, ScimClient.UserPath(Alice.Id)));
            }

            var header = _aliceLocation is { IsAbsoluteUri: true } absolute ? absolute.AbsoluteUri : _aliceLocation?.OriginalString;
            if (header is not null && !string.Equals(header.TrimEnd('/'), location.TrimEnd('/'), StringComparison.OrdinalIgnoreCase)) {
                c.Warn(Message.Of("note.locationMismatch", header, location));
            }
        }
    }

    private async Task DuplicateUserAsync(CheckContext c) {
        var response = Track(await c.SendAsync(HttpMethod.Post, "/Users", UserBody("alice", "Alice", "Again")), _users);
        if (response.IsSuccess) {
            await DiscardAsync(c, response);
        }

        Expect(response, 409);
        c.ExpectScimType(response, "uniqueness");
    }

    /// <summary>userName is compared without case (RFC 7643 section 4.1.1), so the same name in capitals is the same name.</summary>
    /// <param name="c">The check.</param>
    private async Task DuplicateCaseAsync(CheckContext c) {
        var body = UserBody("alice-upper", "Alice", "Upper");
        body["userName"] = Alice.UserName.ToUpperInvariant();

        var response = Track(await c.SendAsync(HttpMethod.Post, "/Users", body), _users);
        if (response.IsSuccess) {
            await DiscardAsync(c, response);
            Fail("note.duplicateCase", Alice.UserName.ToUpperInvariant());
        }

        Expect(response, 409);
        c.ExpectScimType(response, "uniqueness");
    }

    private async Task InvalidUserAsync(CheckContext c) {
        var body = new JsonObject { ["schemas"] = new JsonArray(ScimSchemas.USER), ["displayName"] = $"{Prefix}{_marker} without userName" };
        var response = Track(await c.SendAsync(HttpMethod.Post, "/Users", body), _users);
        Expect(response, 400);
        c.ExpectScimType(response, "invalidValue");
    }

    /// <summary>RFC 7643 section 4.1.1: every user MUST have a userName that is not empty.</summary>
    /// <param name="c">The check.</param>
    private async Task EmptyUserNameAsync(CheckContext c) {
        var body = UserBody("empty", "Empty", "Name");
        body["userName"] = string.Empty;

        var response = Track(await c.SendAsync(HttpMethod.Post, "/Users", body), _users);
        Expect(response, 400);
        c.ExpectScimType(response, "invalidValue");
    }

    private async Task InvalidJsonAsync(CheckContext c) {
        var request = new ScimRequest {
            Method = HttpMethod.Post,
            Path = "/Users",
            Body = $"{{\"schemas\":[\"{ScimSchemas.USER}\"],\"userName\":\"{UserName("json")}\"",
        };

        var response = Track(await c.SendAsync(request), _users);
        Expect(response, 400);
        c.ExpectScimType(response, "invalidSyntax");
    }

    private async Task WrongTypeAsync(CheckContext c) {
        var body = UserBody("type", "Wrong", "Type");
        body["active"] = "maybe";

        var response = Track(await c.SendAsync(HttpMethod.Post, "/Users", body), _users);
        if (response.IsSuccess) {
            c.Warn(Message.Of("note.accepted", "\"active\": \"maybe\""));
            return;
        }

        Expect(response, 400);
        c.ExpectScimType(response, "invalidValue", "invalidSyntax");
    }

    /// <summary>RFC 7644 section 3.3: read-only values in a POST SHALL be ignored - the id is the server's to give.</summary>
    /// <param name="c">The check.</param>
    private async Task ReadOnlyIgnoredAsync(CheckContext c) {
        var chosen = ExternalId("chosen-id");
        var body = UserBody("read-only", "Read", "Only");
        body["id"] = chosen;
        body["meta"] = new JsonObject { ["resourceType"] = "User", ["created"] = EARLY, ["lastModified"] = EARLY };

        var response = Track(await c.SendAsync(HttpMethod.Post, "/Users", body), _users);
        if (response.StatusCode == 400) {
            c.Warn(Message.Of("note.readOnlyRefused", response.StatusCode));
            return;
        }

        var user = ScimUser.Read(Body(Expect(response, 201)));
        if (user.Id == chosen) {
            Fail("note.readOnlyApplied", "id");
        }

        if (user.Created?.Year == 2000) {
            Fail("note.readOnlyApplied", "meta.created");
        }
    }

    private static async Task UnknownUserAsync(CheckContext c) {
        Expect(await c.SendAsync(HttpMethod.Get, ScimClient.UserPath(Guid.NewGuid().ToString())), 404);
    }

    /// <summary>RFC 7644 section 3.5.1: PUT MUST NOT create a resource.</summary>
    /// <param name="c">The check.</param>
    private async Task PutUnknownAsync(CheckContext c) {
        var body = UserBody("put-unknown", "Put", "Unknown");
        Expect(Track(await c.SendAsync(HttpMethod.Put, ScimClient.UserPath(Guid.NewGuid().ToString()), body), _users), 404);
    }

    private async Task PatchUnknownAsync(CheckContext c) {
        RequirePatch();
        var path = ScimClient.UserPath(Guid.NewGuid().ToString());
        Expect(await TryPatchAsync(c, path, ScimPatch.Operation("replace", "displayName", "Nobody")), 404);
    }

    private async Task PatchReplaceAsync(CheckContext c) {
        RequirePatch();
        await PatchAsync(c, ScimClient.UserPath(Alice.Id),
            ScimPatch.Operation("replace", "name.givenName", "Alicia"),
            ScimPatch.Operation("replace", "displayName", "Alicia Tester"));

        var user = await ReadUserAsync(c, Alice.Id);
        Equal("name.givenName", user.GivenName, "Alicia");
        Equal("displayName", user.DisplayName, "Alicia Tester");
    }

    private async Task PatchNoPathAsync(CheckContext c) {
        RequirePatch();
        var value = new JsonObject { ["displayName"] = "Alicia NoPath" };
        await PatchAsync(c, ScimClient.UserPath(Alice.Id), ScimPatch.Operation("replace", null, value));

        var user = await ReadUserAsync(c, Alice.Id);
        Equal("displayName", user.DisplayName, "Alicia NoPath");
    }

    private async Task PatchFilterAsync(CheckContext c) {
        RequirePatch();
        var address = $"{Prefix}{_marker}-alicia@example.org";
        await PatchAsync(c, ScimClient.UserPath(Alice.Id), ScimPatch.Operation("replace", "emails[type eq \"work\"].value", address));

        var user = await ReadUserAsync(c, Alice.Id);
        Equal("emails[type eq \"work\"].value", user.Email, address, ignoreCase: true);
    }

    private async Task PatchRemoveAsync(CheckContext c) {
        RequirePatch();
        await PatchAsync(c, ScimClient.UserPath(Alice.Id), ScimPatch.Operation("remove", "name.givenName", null));

        var user = await ReadUserAsync(c, Alice.Id);
        if (user.GivenName is not null) {
            Fail("note.stillThere", "name.givenName", user.GivenName);
        }
    }

    /// <summary>
    /// An add of a single value, to an attribute the server's schema declares: a check of PATCH should not fail over an optional attribute the
    /// server never offered. Without a schema to ask, displayName, which servers keep.
    /// </summary>
    /// <param name="c">The check.</param>
    private async Task PatchAddAsync(CheckContext c) {
        RequirePatch();
        var attribute = _userSchema is null ? "displayName" : Declared(TextAttributes);
        if (attribute is null) {
            Unsupported("note.noAttribute", string.Join(", ", TextAttributes));
        }

        await PatchAsync(c, ScimClient.UserPath(Alice.Id), ScimPatch.Operation("add", attribute, "Alicia Added"));

        var user = await ReadUserAsync(c, Alice.Id);
        Equal(attribute, ScimJson.Text(ScimJson.Get(user.Resource, attribute)), "Alicia Added");
    }

    /// <summary>
    /// RFC 7644 section 3.5.2: an operation the schema does not allow is refused - a path to an attribute no schema declares with
    /// invalidPath. Many servers take the value and drop it instead, so an identity provider's default mapping does not fail: a warning,
    /// since then no one learns that a mapped value goes nowhere.
    /// </summary>
    /// <param name="c">The check.</param>
    private async Task PatchUnknownAttributeAsync(CheckContext c) {
        RequirePatch();
        var response = await TryPatchAsync(c, ScimClient.UserPath(Alice.Id), ScimPatch.Operation("add", UNKNOWN_ATTRIBUTE, "x"));
        if (response.IsSuccess) {
            var stored = ScimJson.Has((await ReadUserAsync(c, Alice.Id)).Resource, UNKNOWN_ATTRIBUTE);
            c.Warn(Message.Of(stored ? "note.unknownStored" : "note.unknownDropped", UNKNOWN_ATTRIBUTE));
            return;
        }

        Expect(response, 400);
        c.ExpectScimType(response, "invalidPath");
    }

    /// <summary>An add to a multi-valued attribute adds a value; the ones there stay.</summary>
    /// <param name="c">The check.</param>
    private async Task PatchAddValueAsync(CheckContext c) {
        RequirePatch();
        var spare = await SpareAsync(c, "add-value");
        var home = UserName("add-value-home");
        await PatchAsync(c, ScimClient.UserPath(spare.Id), ScimPatch.Operation("add", "emails", new JsonArray(Email(home, "home"))));

        var emails = Emails(await ReadUserAsync(c, spare.Id));
        foreach (var address in new[] { home, UserName("add-value") }) {
            if (!emails.Contains(address, StringComparer.OrdinalIgnoreCase)) {
                Fail("note.valueMissing", "emails", address);
            }
        }
    }

    /// <summary>A replace of a complex attribute changes the sub-attributes it names and leaves the others (RFC 7644 section 3.5.2.3).</summary>
    /// <param name="c">The check.</param>
    private async Task PatchComplexAsync(CheckContext c) {
        RequirePatch();
        var spare = await SpareAsync(c, "complex");
        await PatchAsync(c, ScimClient.UserPath(spare.Id), ScimPatch.Operation("replace", "name", new JsonObject { ["givenName"] = "Complex" }));

        var user = await ReadUserAsync(c, spare.Id);
        Equal("name.givenName", user.GivenName, "Complex");
        Equal("name.familyName", user.FamilyName, "Tester");
    }

    private async Task PatchReplaceAllAsync(CheckContext c) {
        RequirePatch();
        var spare = await SpareAsync(c, "replace-all");
        var address = UserName("replace-all-new");
        var value = new JsonArray(Email(address, "work", primary: true));
        await PatchAsync(c, ScimClient.UserPath(spare.Id), ScimPatch.Operation("replace", "emails", value));

        var emails = Emails(await ReadUserAsync(c, spare.Id));
        if (emails.Count != 1 || !string.Equals(emails[0], address, StringComparison.OrdinalIgnoreCase)) {
            Fail("note.value", "emails", string.Join(", ", emails), address);
        }
    }

    private async Task PatchRemoveValueAsync(CheckContext c) {
        RequirePatch();
        var work = UserName("remove-value");
        var home = UserName("remove-value-home");
        var both = new JsonArray(Email(work, "work", primary: true), Email(home, "home"));
        var spare = await SpareAsync(c, "remove-value", body => body["emails"] = both);

        await PatchAsync(c, ScimClient.UserPath(spare.Id), ScimPatch.Operation("remove", "emails[type eq \"home\"]", null));

        var emails = Emails(await ReadUserAsync(c, spare.Id));
        if (emails.Contains(home, StringComparer.OrdinalIgnoreCase)) {
            Fail("note.stillThere", "emails[type eq \"home\"]", home);
        }

        if (!emails.Contains(work, StringComparer.OrdinalIgnoreCase)) {
            Fail("note.valueMissing", "emails", work);
        }
    }

    /// <summary>RFC 7644 section 3.5.2: a value made primary takes the mark from the others - one primary at most.</summary>
    /// <param name="c">The check.</param>
    private async Task PatchPrimaryAsync(CheckContext c) {
        RequirePatch();
        var spare = await SpareAsync(c, "primary");
        var other = UserName("primary-other");
        await PatchAsync(c, ScimClient.UserPath(spare.Id), ScimPatch.Operation("add", "emails", new JsonArray(Email(other, "home", primary: true))));

        var user = await ReadUserAsync(c, spare.Id);
        var primaries = ScimJson.Items(ScimJson.Get(user.Resource, "emails"))
            .Where(e => ScimJson.Flag(ScimJson.Get(e, "primary")) == true)
            .Select(e => ScimJson.Text(ScimJson.Get(e, "value")) ?? "—")
            .ToList();

        if (primaries.Count != 1) {
            Fail("note.primaries", primaries.Count);
        }

        Equal("emails[primary eq true].value", primaries[0], other, ignoreCase: true);
    }

    /// <summary>
    /// RFC 7644 section 3.5.2.3 answers a replace whose filter matches nothing with 400 noTarget. Some servers add instead, on purpose,
    /// because Entra ID sends replace where it means add - hence a warning, not a failure.
    /// </summary>
    /// <param name="c">The check.</param>
    private async Task PatchReplaceNoMatchAsync(CheckContext c) {
        RequirePatch();
        var spare = await SpareAsync(c, "no-match");
        var operation = ScimPatch.Operation("replace", "emails[type eq \"scimstudio-none\"].value", UserName("no-match-other"));

        var response = await TryPatchAsync(c, ScimClient.UserPath(spare.Id), operation);
        if (response.IsSuccess) {
            c.Warn(Message.Of("note.noTargetAccepted"));
            return;
        }

        Expect(response, 400);
        c.ExpectScimType(response, "noTarget");
    }

    private async Task PatchUrnPathAsync(CheckContext c) {
        RequirePatch();
        await PatchAsync(c, ScimClient.UserPath(Alice.Id), ScimPatch.Operation("replace", $"{ScimSchemas.USER}:displayName", "Alicia Urn"));

        var user = await ReadUserAsync(c, Alice.Id);
        Equal("displayName", user.DisplayName, "Alicia Urn");
    }

    /// <summary>RFC 7644 section 3.5.2: with attributes asked for, a PATCH MUST answer 200 with them - not 204.</summary>
    /// <param name="c">The check.</param>
    private async Task PatchAttributesAsync(CheckContext c) {
        RequirePatch();
        var operation = ScimPatch.Operation("replace", "displayName", "Alicia Attributes");
        var response = Expect(await TryPatchAsync(c, $"{ScimClient.UserPath(Alice.Id)}?attributes=userName", operation), 200);

        var body = Body(response);
        if (!ScimJson.Has(body, "userName")) {
            Fail("note.absent", "userName");
        }

        if (ScimJson.Has(body, "emails") || ScimJson.Has(body, "name")) {
            c.Warn(Message.Of("note.attributesIgnored"));
        }
    }

    /// <summary>RFC 7644 section 3.5.2: a PATCH is atomic - when one operation fails, none of the others may stay applied.</summary>
    /// <param name="c">The check.</param>
    private async Task PatchAtomicAsync(CheckContext c) {
        RequirePatch();
        var spare = await SpareAsync(c, "atomic");
        var response = await TryPatchAsync(c, ScimClient.UserPath(spare.Id),
            ScimPatch.Operation("replace", "displayName", "Atomic"),
            ScimPatch.Operation("remove", null, null));

        if (response.IsSuccess) {
            Fail("note.noError", response.StatusCode);
        }

        Expect(response, 400);
        var user = await ReadUserAsync(c, spare.Id);
        if (user.DisplayName == "Atomic") {
            Fail("note.notAtomic");
        }
    }

    private async Task PatchNoTargetAsync(CheckContext c) {
        RequirePatch();
        var spare = await SpareAsync(c, "no-target");
        var response = Expect(await TryPatchAsync(c, ScimClient.UserPath(spare.Id), ScimPatch.Operation("remove", null, null)), 400);
        c.ExpectScimType(response, "noTarget");
    }

    private async Task PatchInvalidOpAsync(CheckContext c) {
        RequirePatch();
        var spare = await SpareAsync(c, "invalid-op");
        var operation = ScimPatch.Operation("scimstudio", "displayName", "Invalid");
        var response = Expect(await TryPatchAsync(c, ScimClient.UserPath(spare.Id), operation), 400);
        c.ExpectScimType(response, "invalidSyntax", "invalidValue");
    }

    private async Task PatchInvalidPathAsync(CheckContext c) {
        RequirePatch();
        var spare = await SpareAsync(c, "invalid-path");
        var operation = ScimPatch.Operation("replace", "emails[type eq \"work\"", "Invalid");
        var response = Expect(await TryPatchAsync(c, ScimClient.UserPath(spare.Id), operation), 400);
        c.ExpectScimType(response, "invalidPath", "invalidFilter");
    }

    /// <summary>RFC 7644 section 3.5.2: an operation on a read-only attribute is refused, with scimType mutability.</summary>
    /// <param name="c">The check.</param>
    private async Task PatchReadOnlyAsync(CheckContext c) {
        RequirePatch();
        var spare = await SpareAsync(c, "patch-read-only");
        var chosen = ExternalId("new-id");

        var response = await TryPatchAsync(c, ScimClient.UserPath(spare.Id), ScimPatch.Operation("replace", "id", chosen));
        if (response.IsSuccess) {
            if ((await c.SendAsync(HttpMethod.Get, ScimClient.UserPath(spare.Id))).StatusCode == 404) {
                _users.Add(chosen);
                Fail("note.readOnlyApplied", "id");
            }

            c.Warn(Message.Of("note.readOnlyIgnored", "id"));
            return;
        }

        Expect(response, 400);
        c.ExpectScimType(response, "mutability");
    }

    /// <summary>RFC 7644 section 3.5.2.2: removing a required attribute is refused.</summary>
    /// <param name="c">The check.</param>
    private async Task PatchRemoveRequiredAsync(CheckContext c) {
        RequirePatch();
        var spare = await SpareAsync(c, "remove-required");

        var response = await TryPatchAsync(c, ScimClient.UserPath(spare.Id), ScimPatch.Operation("remove", "userName", null));
        if (response.IsSuccess) {
            await RequiredStillThereAsync(c, spare.Id);
            return;
        }

        Expect(response, 400);
        c.ExpectScimType(response, "mutability", "invalidValue");
    }

    private async Task PatchEmptyOperationsAsync(CheckContext c) {
        RequirePatch();
        var spare = await SpareAsync(c, "empty-operations");

        var response = await c.SendAsync(HttpMethod.Patch, ScimClient.UserPath(spare.Id), ScimPatch.Request());
        if (response.IsSuccess) {
            c.Warn(Message.Of("note.accepted", "\"Operations\": []"));
            return;
        }

        Expect(response, 400);
    }

    private async Task SetActiveAsync(CheckContext c, bool active) {
        RequirePatch();
        await PatchAsync(c, ScimClient.UserPath(Alice.Id), ScimPatch.Operation("replace", "active", active));
        await ExpectActiveAsync(c, Alice.Id, active);
    }

    /// <summary>userName is read-write; a server that keeps it fixed does not offer renaming, which identity providers do.</summary>
    /// <param name="c">The check.</param>
    private async Task UserNameChangeAsync(CheckContext c) {
        RequirePatch();
        var spare = await SpareAsync(c, "rename");
        var renamed = UserName("renamed");

        var response = await TryPatchAsync(c, ScimClient.UserPath(spare.Id), ScimPatch.Operation("replace", "userName", renamed));
        Declined(response, "replace userName", 400);
        Expect(response, 200, 204);

        var user = await ReadUserAsync(c, spare.Id);
        Equal("userName", user.UserName, renamed, ignoreCase: true);
    }

    private async Task UserNameConflictAsync(CheckContext c) {
        RequirePatch();
        var spare = await SpareAsync(c, "conflict");
        var path = ScimClient.UserPath(spare.Id);

        var response = await TryPatchAsync(c, path, ScimPatch.Operation("replace", "userName", Alice.UserName));
        if (response.IsSuccess) {
            // Give the spare its own name back, so Alice stays the only one with hers.
            await TryPatchAsync(c, path, ScimPatch.Operation("replace", "userName", spare.UserName));
            Fail("note.conflictAccepted", Alice.UserName);
        }

        Declined(response, "replace userName", 400);
        Expect(response, 409);
        c.ExpectScimType(response, "uniqueness");
    }

    private async Task ExternalIdChangeAsync(CheckContext c) {
        RequirePatch();
        var spare = await SpareAsync(c, "external-id");
        var changed = ExternalId("external-id-changed");

        var response = await TryPatchAsync(c, ScimClient.UserPath(spare.Id), ScimPatch.Operation("replace", "externalId", changed));
        Declined(response, "replace externalId", 400);
        Expect(response, 200, 204);

        var user = await ReadUserAsync(c, spare.Id);
        Equal("externalId", user.ExternalId, changed);
    }

    private async Task PutUserAsync(CheckContext c) {
        var body = UserBody("alice", "Alicia", "Replaced");
        var user = ScimUser.Read(Body(Expect(await c.SendAsync(HttpMethod.Put, ScimClient.UserPath(Alice.Id), body), 200)));
        Equal("name.familyName", user.FamilyName, "Replaced");
        Equal("displayName", user.DisplayName, "Alicia Replaced");
    }

    /// <summary>RFC 7644 section 3.5.1: read-only values in a PUT SHALL be ignored - an id that differs from the address included.</summary>
    /// <param name="c">The check.</param>
    private async Task PutReadOnlyAsync(CheckContext c) {
        var spare = await SpareAsync(c, "put-read-only");
        var body = UserBody("put-read-only", "Spare", "Replaced");
        body["id"] = ExternalId("other-id");
        body["meta"] = new JsonObject { ["resourceType"] = "User", ["created"] = EARLY };

        var response = await c.SendAsync(HttpMethod.Put, ScimClient.UserPath(spare.Id), body);
        if (response.StatusCode == 400) {
            c.Warn(Message.Of("note.readOnlyRefused", response.StatusCode));
            return;
        }

        var user = ScimUser.Read(Body(Expect(response, 200)));
        if (user.Id != spare.Id) {
            Track(response, _users);
            Fail("note.readOnlyApplied", "id");
        }

        if (user.Created?.Year == 2000) {
            Fail("note.readOnlyApplied", "meta.created");
        }

        Equal("name.familyName", user.FamilyName, "Replaced");
    }

    /// <summary>RFC 7644 section 3.5.1: a required attribute has to be in a PUT; one without it is refused.</summary>
    /// <param name="c">The check.</param>
    private async Task PutRequiredAsync(CheckContext c) {
        var spare = await SpareAsync(c, "put-required");
        var body = UserBody("put-required", "Spare", "Tester");
        body.Remove("userName");

        var response = await c.SendAsync(HttpMethod.Put, ScimClient.UserPath(spare.Id), body);
        if (response.IsSuccess) {
            await RequiredStillThereAsync(c, spare.Id);
            return;
        }

        Expect(response, 400);
    }

    /// <summary>RFC 7644 section 3.9: any operation that answers with a resource can be asked for part of it.</summary>
    /// <param name="c">The check.</param>
    private async Task PutAttributesAsync(CheckContext c) {
        var spare = await SpareAsync(c, "put-attributes");
        var body = UserBody("put-attributes", "Spare", "Attributes");

        var response = Expect(await c.SendAsync(HttpMethod.Put, $"{ScimClient.UserPath(spare.Id)}?attributes=userName", body), 200);
        var answer = Body(response);
        if (!ScimJson.Has(answer, "userName")) {
            Fail("note.absent", "userName");
        }

        if (ScimJson.Has(answer, "emails") || ScimJson.Has(answer, "name")) {
            c.Warn(Message.Of("note.attributesIgnored"));
        }
    }

    /// <summary>A password is written, never read: RFC 7643 section 4.1.1 has it returned "never".</summary>
    /// <param name="c">The check.</param>
    private async Task PasswordAsync(CheckContext c) {
        var body = UserBody("password", "Spare", "Password");
        body["password"] = Password();

        var response = Track(await c.SendAsync(HttpMethod.Post, "/Users", body), _users);
        Declined(response, "password", 400);

        var created = ScimUser.Read(Body(Expect(response, 201)));
        if (ScimJson.Has(created.Resource, "password") || ScimJson.Has((await ReadUserAsync(c, created.Id)).Resource, "password")) {
            Fail("note.passwordReturned");
        }
    }

    /// <summary>After a request that should have been refused went through: failed if the required userName is gone, a warning if not.</summary>
    /// <param name="c">The check.</param>
    /// <param name="id">The user.</param>
    private static async Task RequiredStillThereAsync(CheckContext c, string id) {
        var user = await ReadUserAsync(c, id);
        if (string.IsNullOrEmpty(user.UserName)) {
            Fail("note.requiredRemoved", "userName");
        }

        c.Warn(Message.Of("note.requiredKept", "userName"));
    }

    /// <summary>A password for a user of the checks' own: long and mixed, so a policy does not refuse it, and random, so it is no one's.</summary>
    private string Password() {
        return $"Sc1m-{_marker}-{RandomNumberGenerator.GetHexString(12)}!";
    }
}
