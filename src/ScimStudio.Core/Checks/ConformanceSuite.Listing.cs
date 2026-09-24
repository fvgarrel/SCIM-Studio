using ScimStudio.Core.Scim;
using ScimStudio.Core.Text;
using static ScimStudio.Core.Checks.CheckContext;

namespace ScimStudio.Core.Checks;

/// <summary>Listing: the ListResponse, paging, sorting, and asking for part of a resource.</summary>
public sealed partial class ConformanceSuite {
    /// <summary>
    /// Without a filter the server lists everyone - the two users of the run among them - but never more than count asks for. RFC 7644
    /// section 3.12 lets it refuse a list that large with tooMany.
    /// </summary>
    /// <param name="c">The check.</param>
    private static async Task UnfilteredAsync(CheckContext c) {
        var response = await c.SendAsync(HttpMethod.Get, "/Users?count=1");
        if (response.StatusCode == 400 && ScimError.Read(response.StatusCode, response.Text).ScimType == "tooMany") {
            Unsupported("note.declined", "GET /Users?count=1", response.StatusCode, Detail(response));
        }

        var list = Body(Expect(response, 200));
        var resources = ScimJson.Items(ScimJson.Get(list, "Resources")).Count();
        if (resources != 1) {
            Fail("note.count", resources, 1);
        }

        var total = ScimJson.Number(ScimJson.Get(list, "totalResults"));
        if (total is null) {
            Fail("note.absent", "totalResults");
        }

        if (total < 2) {
            Fail("note.totalAtLeast", total, 2);
        }
    }

    private async Task ListResponseAsync(CheckContext c) {
        RequireFilter();
        var response = Expect(await c.SendAsync(HttpMethod.Get, $"/Users{new ScimQuery { Filter = Both(), Count = 1 }.ToQueryString()}"), 200);
        c.ExpectMediaType(response);

        var list = Body(response);
        if (!Schemas(list).Contains(ScimSchemas.LIST_RESPONSE)) {
            c.Warn(Message.Of("note.schemas", ScimSchemas.LIST_RESPONSE));
        }

        foreach (var required in new[] { "totalResults", "Resources" }) {
            if (!ScimJson.Has(list, required)) {
                Fail("note.absent", required);
            }
        }

        foreach (var paging in new[] { "startIndex", "itemsPerPage" }) {
            if (ScimJson.Number(ScimJson.Get(list, paging)) is null) {
                c.Warn(Message.Of("note.absent", paging));
            }
        }

        var resources = ScimJson.Items(ScimJson.Get(list, "Resources")).Count();
        if (ScimJson.Number(ScimJson.Get(list, "itemsPerPage")) is { } perPage && perPage != resources) {
            c.Warn(Message.Of("note.itemsPerPage", perPage, resources));
        }
    }

    private async Task PagingAsync(CheckContext c) {
        RequireFilter();
        var both = new ScimQuery { Filter = Both() };

        var first = await ListUsersAsync(c, both with { StartIndex = 1, Count = 1 });
        if (first.Resources.Count != 1) {
            Fail("note.count", first.Resources.Count, 1);
        }

        if (first.TotalResults != 2) {
            Fail("note.total", first.TotalResults, 2);
        }

        var second = await ListUsersAsync(c, both with { StartIndex = 2, Count = 1 });
        if (second.Resources.Count != 1) {
            Fail("note.count", second.Resources.Count, 1);
        }

        if (second.Resources[0].Id == first.Resources[0].Id) {
            Fail("note.samePage");
        }
    }

    private async Task CountZeroAsync(CheckContext c) {
        RequireFilter();
        var page = await ListUsersAsync(c, new ScimQuery { Filter = Both(), Count = 0 });
        if (page.Resources.Count != 0) {
            c.Warn(Message.Of("note.countZero", page.Resources.Count));
        }

        if (page.TotalResults != 2) {
            Fail("note.total", page.TotalResults, 2);
        }
    }

    /// <summary>RFC 7644 section 3.4.2.4: a startIndex below 1 SHALL be taken as 1.</summary>
    /// <param name="c">The check.</param>
    private async Task StartIndexZeroAsync(CheckContext c) {
        RequireFilter();
        var page = await ListUsersAsync(c, new ScimQuery { Filter = Both(), StartIndex = 0, Count = 1 });
        if (page.Resources.Count != 1) {
            Fail("note.count", page.Resources.Count, 1);
        }

        if (page.StartIndex != 1) {
            c.Warn(Message.Of("note.value", "startIndex", page.StartIndex, 1));
        }
    }

    private async Task StartIndexBeyondAsync(CheckContext c) {
        RequireFilter();
        var page = await ListUsersAsync(c, new ScimQuery { Filter = Both(), StartIndex = 100, Count = 10 });
        if (page.Resources.Count != 0) {
            Fail("note.count", page.Resources.Count, 0);
        }

        if (page.TotalResults != 2) {
            Fail("note.total", page.TotalResults, 2);
        }
    }

    /// <summary>RFC 7644 section 3.4.2.4: a negative count SHALL be taken as 0 - no resources, only the total.</summary>
    /// <param name="c">The check.</param>
    private async Task NegativeCountAsync(CheckContext c) {
        RequireFilter();
        var page = await ListUsersAsync(c, new ScimQuery { Filter = Both(), Count = -1 });
        if (page.Resources.Count != 0) {
            Fail("note.count", page.Resources.Count, 0);
        }

        if (page.TotalResults != 2) {
            Fail("note.total", page.TotalResults, 2);
        }
    }

    /// <summary>A count beyond the server's page size is a wish, not an error: the server answers with what it is willing to.</summary>
    /// <param name="c">The check.</param>
    private async Task LargeCountAsync(CheckContext c) {
        RequireFilter();
        var query = new ScimQuery { Filter = Both(), Count = 10000 };
        var response = Expect(await c.SendAsync(HttpMethod.Get, $"/Users{query.ToQueryString()}"), 200, 400);
        if (response.StatusCode == 400) {
            c.Warn(Message.Of("note.largeCount", Detail(response)));
            return;
        }

        Count(ScimPage.Read(Body(response), ScimUser.Read), 2);
    }

    private async Task SortAsync(CheckContext c) {
        RequireFilter();
        RequireSort();

        var page = await ListUsersAsync(c, new ScimQuery { Filter = Both(), SortBy = "userName", SortOrder = "descending" });
        var names = page.Resources.Select(u => u.UserName).ToList();
        if (names.Count != 2 || !names.SequenceEqual(names.OrderByDescending(n => n, StringComparer.OrdinalIgnoreCase))) {
            Fail("note.order", string.Join(", ", names));
        }
    }

    /// <summary>RFC 7644 section 3.4.2.3: sortBy without sortOrder SHALL sort ascending.</summary>
    /// <param name="c">The check.</param>
    private async Task SortAscendingAsync(CheckContext c) {
        RequireFilter();
        RequireSort();

        var page = await ListUsersAsync(c, new ScimQuery { Filter = Both(), SortBy = "userName" });
        var names = page.Resources.Select(u => u.UserName).ToList();
        if (names.Count != 2 || !names.SequenceEqual(names.OrderBy(n => n, StringComparer.OrdinalIgnoreCase))) {
            Fail("note.order", string.Join(", ", names));
        }
    }

    private async Task AttributesAsync(CheckContext c) {
        var body = Body(Expect(await c.SendAsync(HttpMethod.Get, $"{ScimClient.UserPath(Alice.Id)}?attributes=userName"), 200));
        foreach (var kept in new[] { "id", "userName" }) {
            if (!ScimJson.Has(body, kept)) {
                Fail("note.absent", kept);
            }
        }

        foreach (var left in new[] { "emails", "name", "displayName" }) {
            if (ScimJson.Has(body, left)) {
                Fail("note.present", left);
            }
        }
    }

    private async Task AttributesListAsync(CheckContext c) {
        RequireFilter();
        var page = await ListUsersAsync(c, new ScimQuery { Filter = Both(), Attributes = ["userName"] });
        Count(page, 2);

        foreach (var user in page.Resources) {
            if (string.IsNullOrEmpty(user.Id) || string.IsNullOrEmpty(user.UserName)) {
                Fail("note.absent", string.IsNullOrEmpty(user.Id) ? "id" : "userName");
            }

            if (ScimJson.Has(user.Resource, "emails") || ScimJson.Has(user.Resource, "name")) {
                Fail("note.present", ScimJson.Has(user.Resource, "emails") ? "emails" : "name");
            }
        }
    }

    /// <summary>A sub-attribute asked for brings its own value, not its siblings'.</summary>
    /// <param name="c">The check.</param>
    private async Task AttributesSubAsync(CheckContext c) {
        var user = ScimUser.Read(Body(Expect(await c.SendAsync(HttpMethod.Get, $"{ScimClient.UserPath(Alice.Id)}?attributes=name.givenName"), 200)));
        if (user.GivenName is null) {
            Fail("note.absent", "name.givenName");
        }

        if (user.FamilyName is not null) {
            Fail("note.present", "name.familyName");
        }

        if (ScimJson.Has(user.Resource, "emails")) {
            Fail("note.present", "emails");
        }
    }

    private async Task AttributesUrnAsync(CheckContext c) {
        var path = $"{ScimClient.UserPath(Alice.Id)}?attributes={Uri.EscapeDataString($"{ScimSchemas.USER}:userName")}";
        var body = Body(Expect(await c.SendAsync(HttpMethod.Get, path), 200));
        if (!ScimJson.Has(body, "userName")) {
            Fail("note.absent", "userName");
        }

        if (ScimJson.Has(body, "emails")) {
            Fail("note.present", "emails");
        }
    }

    private async Task ExcludedAttributesAsync(CheckContext c) {
        var body = Body(Expect(await c.SendAsync(HttpMethod.Get, $"{ScimClient.UserPath(Alice.Id)}?excludedAttributes=emails"), 200));
        if (ScimJson.Has(body, "emails")) {
            Fail("note.present", "emails");
        }

        if (!ScimJson.Has(body, "userName")) {
            Fail("note.absent", "userName");
        }
    }

    /// <summary>id is returned "always" (RFC 7643 section 3.1): excluding it changes nothing.</summary>
    /// <param name="c">The check.</param>
    private async Task ExcludedAlwaysAsync(CheckContext c) {
        var body = Body(Expect(await c.SendAsync(HttpMethod.Get, $"{ScimClient.UserPath(Alice.Id)}?excludedAttributes=id"), 200));
        if (!ScimJson.Has(body, "id")) {
            Fail("note.absent", "id");
        }
    }
}
