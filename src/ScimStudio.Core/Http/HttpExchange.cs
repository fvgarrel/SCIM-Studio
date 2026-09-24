namespace ScimStudio.Core.Http;

/// <summary>
/// One request and what came back, as the wire saw them. The bearer token is never kept: the header is recorded with all but its last four
/// characters masked, so a log can be copied into a ticket as it is.
/// </summary>
public sealed record HttpExchange {
    /// <summary>The order the request was sent in, across everything this process sent.</summary>
    public required long Sequence { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public required string Method { get; init; }

    public required Uri Url { get; init; }

    public IReadOnlyList<KeyValuePair<string, string>> RequestHeaders { get; init; } = [];

    public string? RequestBody { get; init; }

    /// <summary>The status code, or null when no answer arrived.</summary>
    public int? StatusCode { get; init; }

    public string? ReasonPhrase { get; init; }

    public IReadOnlyList<KeyValuePair<string, string>> ResponseHeaders { get; init; } = [];

    public string? ResponseBody { get; init; }

    public TimeSpan Duration { get; init; }

    /// <summary>Why no answer arrived: the connection, TLS, a timeout. Null when one did.</summary>
    public string? Failure { get; init; }

    /// <summary>What the request was sent for - a user action, a check, the generator - as the <see cref="ExchangeScope"/> named it.</summary>
    public string? Origin { get; init; }

    public bool IsSuccess => StatusCode is >= 200 and < 300;
}
