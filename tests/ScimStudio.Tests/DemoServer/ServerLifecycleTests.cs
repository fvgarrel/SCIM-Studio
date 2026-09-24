using System.Net;
using System.Net.Sockets;
using ScimStudio.DemoServer;

namespace ScimStudio.Tests.DemoServer;

public sealed class ServerLifecycleTests {
    private static CancellationToken Cancellation => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Server_listens_on_loopback_and_reports_its_base_url() {
        await using var server = await DemoScimServer.StartAsync(new DemoServerOptions { Seed = false, Token = "secret" }, Cancellation);

        Assert.Equal("http", server.BaseUrl.Scheme);
        Assert.Equal("127.0.0.1", server.BaseUrl.Host);
        Assert.NotEqual(0, server.BaseUrl.Port);
        Assert.Equal("/scim/v2", server.BaseUrl.AbsolutePath);
        Assert.False(server.BaseUrl.ToString().EndsWith('/'));
        Assert.Equal("secret", server.Token);
        using var client = new ScimTestClient(server);
        Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("ServiceProviderConfig")).Status);
    }

    [Fact]
    public async Task Generated_token_is_url_safe_and_differs_per_server() {
        await using var first = await DemoScimServer.StartAsync(new DemoServerOptions { Seed = false }, Cancellation);
        await using var second = await DemoScimServer.StartAsync(new DemoServerOptions { Seed = false }, Cancellation);

        Assert.Equal(32, first.Token.Length);
        Assert.All(first.Token, character => Assert.True(char.IsAsciiLetterOrDigit(character) || character is '-' or '_'));
        Assert.NotEqual(first.Token, second.Token);
        Assert.NotEqual(first.BaseUrl.Port, second.BaseUrl.Port);
    }

    [Fact]
    public async Task Server_on_a_fixed_port_listens_there_and_the_port_cannot_be_taken_twice() {
        var port = FreePort();

        await using var server = await DemoScimServer.StartAsync(new DemoServerOptions { Seed = false, Port = port }, Cancellation);

        Assert.Equal(port, server.BaseUrl.Port);
        await Assert.ThrowsAnyAsync<IOException>(() => DemoScimServer.StartAsync(new DemoServerOptions { Port = port }, Cancellation));
    }

    [Fact]
    public async Task Stopped_server_no_longer_answers_and_disposing_twice_is_harmless() {
        var server = await DemoScimServer.StartAsync(new DemoServerOptions { Seed = false }, Cancellation);
        using var client = new ScimTestClient(server);

        await server.StopAsync(Cancellation);

        await Assert.ThrowsAsync<HttpRequestException>(() => client.GetAsync("ServiceProviderConfig"));
        await server.DisposeAsync();
        await server.DisposeAsync();
    }

    [Theory]
    [InlineData(-1, 200, null)]
    [InlineData(70000, 200, null)]
    [InlineData(0, 0, null)]
    [InlineData(0, 200, " ")]
    public async Task Invalid_options_are_refused(int port, int maxResults, string? token) {
        var options = new DemoServerOptions { Port = port, MaxResults = maxResults, Token = token, Seed = false };

        await Assert.ThrowsAnyAsync<ArgumentException>(() => DemoScimServer.StartAsync(options, Cancellation));
    }

    private static int FreePort() {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }
}
