using ScimStudio.Core.Http;
using ScimStudio.Core.Scim;
using ScimStudio.DemoServer;

namespace ScimStudio.Tests.Core;

/// <summary>A test with an empty demo server of its own, and the tool's client pointed at it.</summary>
public abstract class DemoServerTest : IAsyncLifetime {
    protected DemoScimServer Server { get; private set; } = null!;

    protected ExchangeLog Log { get; } = new();

    protected ScimClient Client { get; private set; } = null!;

    protected static CancellationToken Token => TestContext.Current.CancellationToken;

    public async ValueTask InitializeAsync() {
        Server = await DemoScimServer.StartAsync(new DemoServerOptions { Seed = false }, Token);
        Client = new ScimClient(new ScimConnection { BaseUrl = Server.BaseUrl, Token = Server.Token }, Log);
    }

    public async ValueTask DisposeAsync() {
        Client.Dispose();
        await Server.DisposeAsync();
        GC.SuppressFinalize(this);
    }

    protected async Task<ScimUser> CreateUserAsync(string userName) {
        var body = new System.Text.Json.Nodes.JsonObject {
            ["schemas"] = new System.Text.Json.Nodes.JsonArray(ScimSchemas.USER),
            ["userName"] = userName,
            ["displayName"] = userName.Split('@')[0],
        };

        return await Client.CreateUserAsync(body, Token);
    }
}
