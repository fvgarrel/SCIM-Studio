using ScimStudio.Core.Http;
using ScimStudio.Core.Scim;

namespace ScimStudio.Tests.Core;

/// <summary>What the client records and how it reads a server - the parts the log and the connection test stand on.</summary>
public sealed class ClientTests : DemoServerTest {
    [Fact]
    public async Task The_log_masks_the_token_and_names_what_a_request_was_for() {
        using (ExchangeScope.Begin("ui:users.list")) {
            await Client.ListUsersAsync(null, Token);
        }

        var exchange = Assert.Single(Log.Snapshot());
        var authorization = exchange.RequestHeaders.Single(h => h.Key == "Authorization").Value;
        Assert.Equal($"Bearer ••••{Server.Token[^4..]}", authorization);
        Assert.DoesNotContain(Server.Token, string.Join('\n', exchange.RequestHeaders.Select(h => h.Value)), StringComparison.Ordinal);
        Assert.Equal("ui:users.list", exchange.Origin);
        Assert.Equal(200, exchange.StatusCode);
        Assert.Contains("ListResponse", exchange.ResponseBody, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_scope_collects_the_requests_sent_inside_it_and_an_outer_one_sees_them_too() {
        using var outer = ExchangeScope.Begin("outer");
        using (var inner = ExchangeScope.Begin("inner")) {
            await Client.ListUsersAsync(null, Token);
            Assert.Single(inner.Exchanges);
        }

        await Client.ListGroupsAsync(null, Token);
        Assert.Equal(["inner", "outer"], outer.Exchanges.Select(e => e.Origin));
    }

    [Fact]
    public async Task A_request_can_go_without_the_token_and_with_headers_of_its_own() {
        var request = new ScimRequest {
            Method = HttpMethod.Get,
            Path = "/Users",
            Accept = "application/json",
            Authorization = string.Empty,
            Headers = [new("If-None-Match", "W/\"1\"")],
        };

        var response = await Client.SendAsync(request, Token);

        Assert.Equal(401, response.StatusCode);
        var headers = Assert.Single(Log.Snapshot()).RequestHeaders;
        Assert.DoesNotContain(headers, h => h.Key == "Authorization");
        Assert.Contains(headers, h => h.Key == "Accept" && h.Value == "application/json");
        Assert.Contains(headers, h => h.Key == "If-None-Match" && h.Value == "W/\"1\"");
    }

    [Fact]
    public async Task A_request_body_beyond_what_the_log_keeps_is_recorded_as_its_beginning() {
        var body = new string('x', 300_000);

        await Client.SendAsync(new ScimRequest { Method = HttpMethod.Post, Path = "/Users", Body = body }, Token);

        var recorded = Assert.Single(Log.Snapshot()).RequestBody!;
        Assert.StartsWith(body[..1000], recorded, StringComparison.Ordinal);
        Assert.EndsWith($"… (+{300_000 - (256 * 1024)})", recorded, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_refused_connection_is_recorded_as_a_failure() {
        using var client = new ScimClient(new ScimConnection { BaseUrl = new Uri("http://127.0.0.1:1/scim/v2"), Token = "t" }, Log);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.ListUsersAsync(null, Token));

        var exchange = Assert.Single(Log.Snapshot());
        Assert.Null(exchange.StatusCode);
        Assert.NotNull(exchange.Failure);
    }

    [Fact]
    public async Task The_connection_test_reports_what_the_server_offers() {
        await CreateUserAsync("ada@example.com");

        var report = await ConnectionProbe.RunAsync(Client, Token);

        Assert.True(report.Succeeded);
        Assert.Equal((1, 0), (report.UserCount, report.GroupCount));
        Assert.True(report.Configuration?.PatchSupported);
        Assert.Equal(200, report.Configuration?.FilterMaxResults);
    }

    [Fact]
    public async Task The_connection_test_tells_a_wrong_token_from_a_server_that_is_not_there() {
        using var wrong = new ScimClient(new ScimConnection { BaseUrl = Server.BaseUrl, Token = "wrong" }, Log);
        using var nowhere = new ScimClient(new ScimConnection { BaseUrl = new Uri("http://127.0.0.1:1/scim/v2"), Token = "t" }, Log);

        var refused = await ConnectionProbe.RunAsync(wrong, Token);
        var unreachable = await ConnectionProbe.RunAsync(nowhere, Token);

        Assert.Equal((true, false, "probe.unauthorized"), (refused.Reachable, refused.Authenticated, refused.Problems[0].Key));
        Assert.Equal((false, "probe.unreachable"), (unreachable.Reachable, unreachable.Problems[0].Key));
    }

    [Fact]
    public async Task A_failure_carries_the_scim_error_the_server_sent() {
        var failure = await Assert.ThrowsAsync<ScimException>(() => Client.GetUserAsync("nobody", Token));

        Assert.Equal(404, failure.Error.Status);
        Assert.NotNull(failure.Error.Detail);
        Assert.NotNull(failure.Exchange);
    }

    [Fact]
    public async Task Pages_are_read_until_everything_is_there() {
        for (var i = 0; i < 7; i++) {
            await CreateUserAsync($"user{i}@example.com");
        }

        var all = await ScimClient.ReadAllAsync(
            (query, ct) => Client.ListUsersAsync(query with { Count = 3 }, ct), new ScimQuery(), 100, Token);

        Assert.Equal(7, all.Select(u => u.Id).Distinct().Count());
    }
}
