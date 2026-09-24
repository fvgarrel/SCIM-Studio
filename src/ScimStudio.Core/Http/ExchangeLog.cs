namespace ScimStudio.Core.Http;

/// <summary>Takes every exchange the recording handler finishes.</summary>
public interface IExchangeSink {
    /// <summary>Takes a finished exchange; called on whichever thread the request completed on.</summary>
    /// <param name="exchange">The exchange.</param>
    void Record(HttpExchange exchange);
}

/// <summary>The exchanges of this session, newest last, bounded so a long generator run cannot grow it without end.</summary>
public sealed class ExchangeLog : IExchangeSink {
    public const int CAPACITY = 5000;

    private readonly Lock _gate = new();
    private readonly LinkedList<HttpExchange> _exchanges = [];

    /// <summary>Raised after an exchange was added, on the thread that recorded it.</summary>
    public event EventHandler<HttpExchange>? Recorded;

    /// <summary>Raised after the log was emptied.</summary>
    public event EventHandler? Cleared;

    public int Count {
        get {
            lock (_gate) {
                return _exchanges.Count;
            }
        }
    }

    public void Record(HttpExchange exchange) {
        ArgumentNullException.ThrowIfNull(exchange);

        lock (_gate) {
            _exchanges.AddLast(exchange);
            if (_exchanges.Count > CAPACITY) {
                _exchanges.RemoveFirst();
            }
        }

        Recorded?.Invoke(this, exchange);
    }

    /// <summary>Every exchange kept, oldest first.</summary>
    public IReadOnlyList<HttpExchange> Snapshot() {
        lock (_gate) {
            return [.. _exchanges];
        }
    }

    public void Clear() {
        lock (_gate) {
            _exchanges.Clear();
        }

        Cleared?.Invoke(this, EventArgs.Empty);
    }
}
