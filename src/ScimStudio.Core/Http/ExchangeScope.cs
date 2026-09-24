namespace ScimStudio.Core.Http;

/// <summary>
/// Names what the requests sent inside it are for, and collects them - a check's own requests, or those of one click. Flows with the async
/// context, so every request awaited under it is caught without passing anything down; scopes nest, and an outer one sees the inner's requests.
/// </summary>
public sealed class ExchangeScope : IDisposable {
    private static readonly AsyncLocal<ExchangeScope?> Ambient = new();

    private readonly ExchangeScope? _outer;
    private readonly Lock _gate = new();
    private readonly List<HttpExchange> _exchanges = [];

    private ExchangeScope(string origin, ExchangeScope? outer) {
        Origin = origin;
        _outer = outer;
    }

    public static ExchangeScope? Current => Ambient.Value;

    public string Origin { get; }

    /// <summary>The requests sent inside this scope so far, in the order they finished.</summary>
    public IReadOnlyList<HttpExchange> Exchanges {
        get {
            lock (_gate) {
                return [.. _exchanges];
            }
        }
    }

    /// <summary>Opens a scope for the calling async flow. Dispose it on the same flow to close it.</summary>
    /// <param name="origin">What the requests are for, as the log shows it.</param>
    public static ExchangeScope Begin(string origin) {
        var scope = new ExchangeScope(origin, Ambient.Value);
        Ambient.Value = scope;
        return scope;
    }

    public void Dispose() {
        Ambient.Value = _outer;
    }

    internal void Add(HttpExchange exchange) {
        lock (_gate) {
            _exchanges.Add(exchange);
        }

        _outer?.Add(exchange);
    }
}
