using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using ScimStudio.Core.Checks;
using ScimStudio.Core.Http;
using ScimStudio.Core.Scim;

namespace ScimStudio.Tests.Core;

/// <summary>The suite has to pass a server that does it right, fail one that does not, and leave neither with anything it created.</summary>
public sealed class ConformanceSuiteTests : DemoServerTest {
    // What the demo server does not offer, and where it bends RFC 7644 on purpose, so identity providers do not fail: a replace without a
    // match adds, and a value for an attribute it does not know is dropped.
    private static readonly Dictionary<string, CheckStatus> DemoServerExceptions = new() {
        ["filter.searchRoot"] = CheckStatus.Unsupported,
        ["users.patchUnknownAttribute"] = CheckStatus.Warning,
        ["users.patchReplaceNoMatch"] = CheckStatus.Warning,
        ["groups.nested"] = CheckStatus.Unsupported,
        ["optional.changePassword"] = CheckStatus.Unsupported,
        ["optional.me"] = CheckStatus.Unsupported,
    };

    [Fact]
    public async Task Every_check_passes_against_the_demo_server_but_what_it_does_not_offer() {
        var results = await ConformanceSuite.RunAsync(Client, null, Token);

        Assert.Equal(ConformanceSuite.Checks.Count, results.Count);
        Assert.All(results, result => {
            var expected = DemoServerExceptions.GetValueOrDefault(result.Check.Id, CheckStatus.Passed);
            Assert.True(result.Status == expected, $"{result.Check.Id}: {result.Status}, expected {expected} {Notes(result)}");
        });
    }

    [Fact]
    public void Every_requirement_runs_before_the_check_that_needs_it() {
        var position = ConformanceSuite.Checks.Select((check, index) => (check.Id, index)).ToDictionary(p => p.Id, p => p.index);

        Assert.All(ConformanceSuite.Requirements, check => Assert.All(check.Value, required => Assert.True(
            position.TryGetValue(required, out var index) && index < position[check.Key], $"{check.Key} stands on {required}")));
    }

    [Fact]
    public async Task A_check_standing_on_an_unsupported_one_is_unsupported_too() {
        using var client = new ScimClient(
            new ScimConnection { BaseUrl = Server.BaseUrl, Token = Server.Token }, new ExchangeLog(), new WithoutVersions());

        var results = (await ConformanceSuite.RunAsync(client, null, Token)).ToDictionary(r => r.Check.Id);

        Assert.Equal(CheckStatus.Unsupported, results["optional.etag"].Status);
        Assert.Equal("note.unsupported", results["optional.etag"].Notes[0].Key);
        Assert.Equal(CheckStatus.Unsupported, results["optional.etagPrecondition"].Status);
        Assert.Equal("note.dependencyUnsupported", results["optional.etagPrecondition"].Notes[0].Key);
    }

    [Fact]
    public async Task A_check_writes_only_attributes_the_schema_declares() {
        using var client = new ScimClient(
            new ScimConnection { BaseUrl = Server.BaseUrl, Token = Server.Token }, new ExchangeLog(), new WithoutAttributes("nickName", "title"));

        var results = (await ConformanceSuite.RunAsync(client, null, Token)).ToDictionary(r => r.Check.Id);

        var add = results["users.patchAdd"];
        Assert.Equal(CheckStatus.Passed, add.Status);
        Assert.Contains("\"path\":\"displayName\"", add.Exchanges.Single(e => e.Method == "PATCH").RequestBody, StringComparison.Ordinal);

        // With neither attribute declared there is none both users lack, and pr is checked on the one they have.
        Assert.Equal(CheckStatus.Passed, results["filter.pr"].Status);
        Assert.Single(results["filter.pr"].Exchanges);

        Assert.Equal("note.unknownDropped", results["users.patchUnknownAttribute"].Notes[0].Key);
    }

    [Fact]
    public async Task What_the_checks_create_is_gone_afterwards() {
        await ConformanceSuite.RunAsync(Client, null, Token);

        Assert.Equal(0, (await Client.ListUsersAsync(null, Token)).TotalResults);
        Assert.Equal(0, (await Client.ListGroupsAsync(null, Token)).TotalResults);
    }

    [Fact]
    public async Task A_cancelled_run_still_removes_what_it_created() {
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var progress = new Stop(cancel, afterCheck: "groups.create");

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ConformanceSuite.RunAsync(Client, progress, cancel.Token));

        Assert.Equal(0, (await Client.ListUsersAsync(null, Token)).TotalResults);
        Assert.Equal(0, (await Client.ListGroupsAsync(null, Token)).TotalResults);
    }

    [Fact]
    public async Task Checks_fail_against_a_server_that_answers_anything_with_an_empty_object() {
        using var client = new ScimClient(
            new ScimConnection { BaseUrl = new Uri("http://scim.invalid/scim/v2"), Token = "token" }, new ExchangeLog(), new Nonsense());

        var results = (await ConformanceSuite.RunAsync(client, null, Token)).ToDictionary(r => r.Check.Id);

        Assert.Equal(CheckStatus.Failed, results["auth.missingToken"].Status);
        Assert.Equal(CheckStatus.Failed, results["users.create"].Status);
        Assert.Equal(CheckStatus.Failed, results["discovery.resourceTypes"].Status);
        Assert.Equal(CheckStatus.Skipped, results["groups.create"].Status);
        Assert.Equal("note.dependency", results["users.get"].Notes[0].Key);
    }

    private static string Notes(CheckResult result) {
        return string.Join(" | ", result.Notes.Select(n => $"{n.Key}({string.Join(", ", n.Args)})"));
    }

    /// <summary>A server that answers every request with 200 and an empty object, as a misconfigured proxy might.</summary>
    private sealed class Nonsense : HttpMessageHandler {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) {
                Content = new StringContent("{}", Encoding.UTF8, "application/json"),
            });
        }
    }

    /// <summary>The demo server with versions switched off in its configuration, as a server without them describes itself.</summary>
    private sealed class WithoutVersions() : DelegatingHandler(new SocketsHttpHandler()) {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.RequestUri!.AbsolutePath.EndsWith("/ServiceProviderConfig", StringComparison.Ordinal)) {
                var config = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!.AsObject();
                config["etag"] = new JsonObject { ["supported"] = false };
                response.Content = new StringContent(config.ToJsonString(), Encoding.UTF8, ScimSchemas.MEDIA_TYPE);
            }

            return response;
        }
    }

    /// <summary>The demo server with User attributes left out of what /Schemas says, as a server that keeps fewer describes itself.</summary>
    /// <param name="names">The attributes left out.</param>
    private sealed class WithoutAttributes(params string[] names) : DelegatingHandler(new SocketsHttpHandler()) {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
            var response = await base.SendAsync(request, cancellationToken);
            if (request.Method != HttpMethod.Get || !request.RequestUri!.AbsolutePath.EndsWith("/Schemas", StringComparison.Ordinal)) {
                return response;
            }

            var list = JsonNode.Parse(await response.Content.ReadAsStringAsync(cancellationToken))!;
            foreach (var schema in list["Resources"]!.AsArray().Where(s => s!["id"]!.GetValue<string>() == ScimSchemas.USER)) {
                var attributes = schema!["attributes"]!.AsArray();
                foreach (var left in attributes.Where(a => names.Contains(a!["name"]!.GetValue<string>())).ToList()) {
                    attributes.Remove(left);
                }
            }

            response.Content = new StringContent(list.ToJsonString(), Encoding.UTF8, ScimSchemas.MEDIA_TYPE);
            return response;
        }
    }

    /// <summary>Cancels the run once a given check has reported its verdict.</summary>
    /// <param name="cancel">The run's cancellation.</param>
    /// <param name="afterCheck">The check after which to stop.</param>
    private sealed class Stop(CancellationTokenSource cancel, string afterCheck) : IProgress<CheckResult> {
        public void Report(CheckResult value) {
            if (value.Check.Id == afterCheck && value.Status != CheckStatus.Running) {
                cancel.Cancel();
            }
        }
    }
}
