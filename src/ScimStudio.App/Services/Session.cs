using CommunityToolkit.Mvvm.ComponentModel;
using ScimStudio.App.Settings;
using ScimStudio.Core.Dialects;
using ScimStudio.Core.Http;
using ScimStudio.Core.Scim;
using ScimStudio.DemoServer;

namespace ScimStudio.App.Services;

/// <summary>A connection in use: the profile it came from, the client, and the dialect requests are written in, which may change.</summary>
public sealed partial class Session : ObservableObject, IDisposable {
    /// <summary>Opens a session.</summary>
    /// <param name="profile">The profile connected with.</param>
    /// <param name="token">The token, which the profile may not remember.</param>
    /// <param name="log">Where the client records.</param>
    /// <param name="isDemo">Whether the server is the built-in demo server.</param>
    public Session(ConnectionProfile profile, string token, IExchangeSink log, bool isDemo = false) {
        ArgumentNullException.ThrowIfNull(profile);

        Profile = profile;
        IsDemo = isDemo;
        Client = new ScimClient(Connection(profile, token), log);
        Dialect = ScimDialect.For(profile.Dialect);
    }

    public ConnectionProfile Profile { get; }

    public ScimClient Client { get; }

    public bool IsDemo { get; }

    [ObservableProperty]
    public partial ScimDialect Dialect { get; set; }

    /// <summary>What the server said it supports, when it said.</summary>
    [ObservableProperty]
    public partial ServiceProviderConfig? Configuration { get; set; }

    public static ScimConnection Connection(ConnectionProfile profile, string token) {
        ArgumentNullException.ThrowIfNull(profile);

        return new ScimConnection {
            BaseUrl = new Uri(profile.BaseUrl.Trim()),
            Token = token,
            AcceptInvalidCertificates = profile.AcceptInvalidCertificates,
            Timeout = TimeSpan.FromSeconds(Math.Clamp(profile.TimeoutSeconds, 1, 600)),
        };
    }

    public void Dispose() {
        Client.Dispose();
    }
}

/// <summary>The built-in demo server, started once on first use and kept for the rest of the run, so what was changed on it stays.</summary>
public sealed class DemoServerHost : IAsyncDisposable {
    private readonly SemaphoreSlim _gate = new(1, 1);
    private DemoScimServer? _server;

    public async Task<DemoScimServer> StartAsync() {
        await _gate.WaitAsync();
        try {
            _server ??= await DemoScimServer.StartAsync(new DemoServerOptions { Seed = true });
            return _server;
        } finally {
            _gate.Release();
        }
    }

    public async ValueTask DisposeAsync() {
        if (_server is not null) {
            await _server.DisposeAsync();
            _server = null;
        }

        _gate.Dispose();
    }
}
