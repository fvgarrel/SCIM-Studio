using System.Buffers.Text;
using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ScimStudio.DemoServer.Http;
using ScimStudio.DemoServer.Store;

namespace ScimStudio.DemoServer;

/// <summary>An in-memory SCIM 2.0 service provider on the loopback interface, for trying SCIM Studio without a real server.</summary>
public sealed class DemoScimServer : IAsyncDisposable {
    private readonly WebApplication _app;
    private int _disposed;

    private DemoScimServer(WebApplication app, Uri baseUrl, string token) {
        _app = app;
        BaseUrl = baseUrl;
        Token = token;
    }

    /// <summary>The SCIM base URL, such as <c>http://127.0.0.1:53817/scim/v2</c>, without a trailing slash.</summary>
    public Uri BaseUrl { get; }

    /// <summary>The bearer token a client must send.</summary>
    public string Token { get; }

    /// <summary>Starts a server, which runs until it is stopped or disposed.</summary>
    /// <param name="options">How to start; null takes the defaults.</param>
    /// <param name="cancellationToken">Cancels the start.</param>
    public static async Task<DemoScimServer> StartAsync(DemoServerOptions? options = null, CancellationToken cancellationToken = default) {
        options ??= new DemoServerOptions();
        ArgumentOutOfRangeException.ThrowIfNegative(options.Port);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.Port, IPEndPoint.MaxPort);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(options.MaxResults);
        if (options.Token is not null) {
            ArgumentException.ThrowIfNullOrWhiteSpace(options.Token);
        }
        // 24 random bytes make 32 URL-safe characters
        var token = options.Token ?? Base64Url.EncodeToString(RandomNumberGenerator.GetBytes(24));

        var store = new ScimStore(TimeProvider.System);
        if (options.Seed) {
            SeedData.Populate(store, TimeProvider.System.GetUtcNow());
        }

        // The empty builder reads no configuration files or environment variables, and without providers nothing is logged:
        // the server runs inside a desktop app, where console output has no place.
        var builder = WebApplication.CreateEmptyBuilder(new WebApplicationOptions());
        builder.Logging.ClearProviders();
        builder.WebHost.UseKestrelCore().ConfigureKestrel(kestrel => kestrel.Listen(IPAddress.Loopback, options.Port));
        builder.Services.AddRoutingCore();
        var app = builder.Build();
        new ScimEndpoints(store, token, options.MaxResults).Map(app);
        try {
            await app.StartAsync(cancellationToken).ConfigureAwait(false);
        } catch {
            await app.DisposeAsync().ConfigureAwait(false);
            throw;
        }

        // For port 0 the operating system picked one, which Kestrel reports once it listens
        var port = new Uri(app.Urls.First()).Port;
        var baseUrl = new Uri(string.Create(CultureInfo.InvariantCulture, $"http://127.0.0.1:{port}{ScimEndpoints.PREFIX}"));
        return new DemoScimServer(app, baseUrl, token);
    }

    /// <summary>Stops listening; requests in flight are allowed to finish.</summary>
    /// <param name="cancellationToken">Ends the wait for requests in flight.</param>
    public Task StopAsync(CancellationToken cancellationToken = default) {
        return _app.StopAsync(cancellationToken);
    }

    public async ValueTask DisposeAsync() {
        if (Interlocked.Exchange(ref _disposed, 1) == 1) {
            return;
        }
        await _app.StopAsync().ConfigureAwait(false);
        await _app.DisposeAsync().ConfigureAwait(false);
    }
}
