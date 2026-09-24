using ScimStudio.Core.Scim;
using ScimStudio.Core.Text;
using static ScimStudio.Core.Checks.CheckContext;

namespace ScimStudio.Core.Checks;

/// <summary>
/// Filters (RFC 7644 section 3.4.2.2), always narrowed to the two users the run created, so other people on the server change no count. An
/// operator the server refuses is one it does not offer; one that finds the wrong people fails.
/// </summary>
public sealed partial class ConformanceSuite {
    private async Task FilterEqAsync(CheckContext c) {
        RequireFilter();
        var page = await ListUsersAsync(c, new ScimQuery { Filter = ScimFilterText.Eq("userName", Alice.UserName) });
        Count(page, 1);
        Equal("id", page.Resources[0].Id, Alice.Id);
    }

    private async Task FilterCaseAsync(CheckContext c) {
        RequireFilter();
        var page = await ListUsersAsync(c, new ScimQuery { Filter = ScimFilterText.Eq("userName", Alice.UserName.ToUpperInvariant()) });
        if (page.TotalResults != 1) {
            c.Warn(Message.Of("note.caseSensitive", page.TotalResults));
        }
    }

    private async Task FilterExternalIdAsync(CheckContext c) {
        RequireFilter();
        var page = await ListUsersAsync(c, new ScimQuery { Filter = ScimFilterText.Eq("externalId", ExternalId("alice")) });
        Count(page, 1);
        Equal("id", page.Resources[0].Id, Alice.Id);
    }

    private async Task FilterSubAttributeAsync(CheckContext c) {
        RequireFilter();
        var page = await ListUsersAsync(c, new ScimQuery { Filter = ScimFilterText.Eq("emails.value", UserName("bob")) });
        Count(page, 1);
        Equal("id", page.Resources[0].Id, Bob.Id);
    }

    private async Task FilterLogicalAsync(CheckContext c) {
        RequireFilter();
        var either = ScimFilterText.Any(ScimFilterText.Eq("userName", Alice.UserName), ScimFilterText.Eq("userName", Bob.UserName));
        var page = await ListUsersAsync(c, new ScimQuery { Filter = $"({either}) and active eq true" });
        Count(page, 2);
    }

    private async Task FilterContainsAsync(CheckContext c) {
        RequireFilter();
        await FindOnlyAsync(c, ScimFilterText.Co("userName", $"{_marker}-alice@"), Alice);
    }

    private async Task FilterStartsWithAsync(CheckContext c) {
        RequireFilter();
        await FindOnlyAsync(c, ScimFilterText.Sw("userName", $"{Prefix}{_marker}-bob"), Bob);
    }

    private async Task FilterEndsWithAsync(CheckContext c) {
        RequireFilter();
        await FindOnlyAsync(c, $"userName ew {ScimFilterText.Quote($"{_marker}-alice@example.com")}", Alice);
    }

    private async Task FilterNotEqualAsync(CheckContext c) {
        RequireFilter();
        await FindOnlyAsync(c, $"({Both()}) and userName ne {ScimFilterText.Quote(UserName("alice"))}", Bob);
    }

    /// <summary>
    /// <c>pr</c> both ways: an attribute both users have, and one neither has - where the schema declares one. An attribute the server does
    /// not know would test something else, which the checks of unknown attributes do.
    /// </summary>
    /// <param name="c">The check.</param>
    private async Task FilterPresentAsync(CheckContext c) {
        RequireFilter();
        Count(await FindAsync(c, $"({Both()}) and externalId pr"), 2);

        if (Declared(UnsetAttributes) is { } absent) {
            Count(await FindAsync(c, $"({Both()}) and {absent} pr"), 0);
        }
    }

    private async Task FilterNotAsync(CheckContext c) {
        RequireFilter();
        await FindOnlyAsync(c, $"({Both()}) and not ({ScimFilterText.Eq("userName", UserName("alice"))})", Bob);
    }

    /// <summary>"and" binds tighter than "or": a or b and nobody is a or (b and nobody) - Alice alone, not no one.</summary>
    /// <param name="c">The check.</param>
    private async Task FilterPrecedenceAsync(CheckContext c) {
        RequireFilter();
        var filter = $"{ScimFilterText.Eq("userName", UserName("alice"))} or {ScimFilterText.Eq("userName", UserName("bob"))} "
            + $"and {ScimFilterText.Eq("userName", UserName("nobody"))}";
        await FindOnlyAsync(c, filter, Alice);
    }

    private async Task FilterDateTimeAsync(CheckContext c) {
        RequireFilter();
        Count(await FindAsync(c, $"({Both()}) and meta.created gt \"{EARLY}\""), 2);
        Count(await FindAsync(c, $"({Both()}) and meta.lastModified lt \"{EARLY}\""), 0);
    }

    private async Task FilterValuePathAsync(CheckContext c) {
        RequireFilter();
        await FindOnlyAsync(c, $"emails[type eq \"work\" and value eq {ScimFilterText.Quote(UserName("alice"))}]", Alice);
    }

    private async Task FilterUrnAsync(CheckContext c) {
        RequireFilter();
        await FindOnlyAsync(c, ScimFilterText.Eq($"{ScimSchemas.USER}:userName", UserName("alice")), Alice);
    }

    /// <summary>Operators are compared without case (RFC 7644 section 3.4.2.2) - a server that filters at all has to take "EQ".</summary>
    /// <param name="c">The check.</param>
    private async Task FilterOperatorCaseAsync(CheckContext c) {
        RequireFilter();
        var page = await ListUsersAsync(c, new ScimQuery { Filter = $"userName EQ {ScimFilterText.Quote(UserName("alice"))}" });
        Count(page, 1);
        Equal("id", page.Resources[0].Id, Alice.Id);
    }

    private async Task FilterAttributeCaseAsync(CheckContext c) {
        RequireFilter();
        var page = await ListUsersAsync(c, new ScimQuery { Filter = ScimFilterText.Eq("USERNAME", UserName("alice")) });
        Count(page, 1);
        Equal("id", page.Resources[0].Id, Alice.Id);
    }

    /// <summary>externalId is case-exact (RFC 7643 section 3.1), so its value in capitals is another value.</summary>
    /// <param name="c">The check.</param>
    private async Task FilterCaseExactAsync(CheckContext c) {
        RequireFilter();
        var page = await ListUsersAsync(c, new ScimQuery { Filter = ScimFilterText.Eq("externalId", ExternalId("alice").ToUpperInvariant()) });
        if (page.TotalResults != 0) {
            c.Warn(Message.Of("note.caseExact", "externalId", page.TotalResults));
        }
    }

    private async Task FilterSchemasAsync(CheckContext c) {
        RequireFilter();
        Count(await FindAsync(c, $"({Both()}) and {ScimFilterText.Eq("schemas", ScimSchemas.USER)}"), 2);
    }

    private async Task FilterNoMatchAsync(CheckContext c) {
        RequireFilter();
        var page = await ListUsersAsync(c, new ScimQuery { Filter = ScimFilterText.Eq("userName", UserName("nobody")) });
        Count(page, 0);
    }

    private async Task FilterInvalidAsync(CheckContext c) {
        RequireFilter();
        var response = Expect(await c.SendAsync(HttpMethod.Get, $"/Users{new ScimQuery { Filter = "userName eq" }.ToQueryString()}"), 400);
        c.ExpectScimType(response, "invalidFilter");
    }

    /// <summary>RFC 7644 section 3.4.2.2: an operator the server does not know MUST be refused with 400 invalidFilter.</summary>
    /// <param name="c">The check.</param>
    private async Task FilterUnknownOperatorAsync(CheckContext c) {
        RequireFilter();
        var filter = $"userName regex {ScimFilterText.Quote(UserName("alice"))}";
        var response = Expect(await c.SendAsync(HttpMethod.Get, $"/Users{new ScimQuery { Filter = filter }.ToQueryString()}"), 400);
        c.ExpectScimType(response, "invalidFilter");
    }

    /// <summary>A filter on an attribute that does not exist matches no one - a server that drops the condition hands out everyone.</summary>
    /// <param name="c">The check.</param>
    private async Task FilterUnknownAttributeAsync(CheckContext c) {
        RequireFilter();
        var filter = $"({Both()}) and {UNKNOWN_ATTRIBUTE} eq \"x\"";
        var response = Expect(await c.SendAsync(HttpMethod.Get, $"/Users{new ScimQuery { Filter = filter }.ToQueryString()}"), 200, 400);
        if (response.StatusCode == 200 && ScimPage.Read(Body(response), ScimUser.Read) is { TotalResults: > 0 } page) {
            Fail("note.filterDropped", page.TotalResults);
        }
    }

    /// <summary>RFC 7644 section 3.4.2.2: gt, ge, lt and le on a boolean SHALL be refused with invalidFilter.</summary>
    /// <param name="c">The check.</param>
    private async Task FilterBooleanOrderAsync(CheckContext c) {
        RequireFilter();
        var filter = $"({Both()}) and active gt true";
        var response = Expect(await c.SendAsync(HttpMethod.Get, $"/Users{new ScimQuery { Filter = filter }.ToQueryString()}"), 200, 400);
        if (response.StatusCode == 200) {
            c.Warn(Message.Of("note.accepted", "active gt true"));
            return;
        }

        c.ExpectScimType(response, "invalidFilter");
    }

    private async Task SearchAsync(CheckContext c) {
        RequireFilter();
        var search = new ScimQuery { Filter = ScimFilterText.Eq("userName", Alice.UserName) }.ToSearchRequest();
        var response = await c.SendAsync(HttpMethod.Post, "/Users/.search", search);
        Declined(response, "POST /Users/.search", 404, 405, 501);

        var page = ScimPage.Read(Body(Expect(response, 200)), ScimUser.Read);
        Count(page, 1);
    }

    /// <summary>A search at the root looks through every resource type (RFC 7644 section 3.4.3); the answer has to hold Alice.</summary>
    /// <param name="c">The check.</param>
    private async Task SearchRootAsync(CheckContext c) {
        RequireFilter();
        var search = new ScimQuery { Filter = ScimFilterText.Eq("userName", Alice.UserName) }.ToSearchRequest();
        var response = await c.SendAsync(HttpMethod.Post, "/.search", search);
        Declined(response, "POST /.search", 400, 403, 404, 405, 501);

        var found = Documents(Expect(response, 200)).Select(d => ScimJson.Text(ScimJson.Get(d, "id")));
        if (!found.Contains(Alice.Id)) {
            Fail("note.notFound", Alice.UserName);
        }
    }

    /// <summary>The filter has to find exactly the one user.</summary>
    /// <param name="c">The check.</param>
    /// <param name="filter">The filter.</param>
    /// <param name="user">The user it describes.</param>
    private static async Task FindOnlyAsync(CheckContext c, string filter, ScimUser user) {
        var page = await FindAsync(c, filter);
        Count(page, 1);
        Equal("id", page.Resources[0].Id, user.Id);
    }
}
