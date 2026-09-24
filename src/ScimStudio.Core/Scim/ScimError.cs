using System.Text.Json.Nodes;
using ScimStudio.Core.Http;

namespace ScimStudio.Core.Scim;

/// <summary>
/// A failed answer as RFC 7644 section 3.12 describes it: the status, the <c>scimType</c> a client can act on, and the detail. Read leniently,
/// since a proxy's HTML page or a problem+json document arrives where a SCIM error was expected more often than one would like.
/// </summary>
/// <param name="Status">The HTTP status.</param>
/// <param name="ScimType">The SCIM error keyword, when the server sent one.</param>
/// <param name="Detail">What the server said went wrong, or the start of whatever it sent instead.</param>
public sealed record ScimError(int Status, string? ScimType, string? Detail) {
    private const int EXCERPT = 300;

    /// <summary>Reads the error out of a failed answer.</summary>
    /// <param name="status">The HTTP status.</param>
    /// <param name="body">The body as text, if there was one.</param>
    public static ScimError Read(int status, string? body) {
        if (ScimJson.ParseObject(body) is { } error) {
            var detail = ScimJson.Text(ScimJson.Get(error, "detail"))
                ?? ScimJson.Text(ScimJson.Get(error, "title"))
                ?? ScimJson.Text(ScimJson.Get(error, "message"));
            return new ScimError(status, ScimJson.Text(ScimJson.Get(error, "scimType")), detail);
        }

        var text = body?.Trim();
        if (string.IsNullOrEmpty(text)) {
            return new ScimError(status, null, null);
        }

        return new ScimError(status, null, text.Length > EXCERPT ? $"{text[..EXCERPT]}…" : text);
    }

    public override string ToString() {
        var type = ScimType is null ? string.Empty : $" {ScimType}";
        var detail = Detail is null ? string.Empty : $": {Detail}";
        return $"HTTP {Status}{type}{detail}";
    }
}

/// <summary>A SCIM request the server answered with a failure.</summary>
public sealed class ScimException : Exception {
    public ScimException() : this(new ScimError(0, null, null), null) {
    }

    public ScimException(string message) : this(new ScimError(0, null, message), null) {
    }

    public ScimException(string message, Exception innerException) : base(message, innerException) {
        Error = new ScimError(0, null, message);
    }

    /// <summary>A failure the server answered with.</summary>
    /// <param name="error">The error as the server described it.</param>
    /// <param name="exchange">The exchange it came from, for linking to the log.</param>
    public ScimException(ScimError error, HttpExchange? exchange) : base((error ?? throw new ArgumentNullException(nameof(error))).ToString()) {
        Error = error;
        Exchange = exchange;
    }

    public ScimError Error { get; }

    public HttpExchange? Exchange { get; }
}

/// <summary>An answer as the client read it: its status, its body both as text and as JSON, and the exchange it came from.</summary>
public sealed record ScimResponse {
    public required int StatusCode { get; init; }

    public string? Text { get; init; }

    /// <summary>The body as JSON, or null when it was empty or not JSON.</summary>
    public JsonNode? Body { get; init; }

    /// <summary>The resource's address: <c>Location</c> on a 201, <c>Content-Location</c> otherwise.</summary>
    public Uri? Location { get; init; }

    public string? ContentType { get; init; }

    public HttpExchange? Exchange { get; init; }

    public bool IsSuccess => StatusCode is >= 200 and < 300;

    /// <summary>The body as a JSON object; the failure when the answer was one, and an error when there was no object to read.</summary>
    public JsonObject Document() {
        EnsureSuccess();
        return Body as JsonObject
            ?? throw new ScimException(new ScimError(StatusCode, null, "The server answered without a JSON object."), Exchange);
    }

    /// <summary>Throws the failure the answer carries, if it carries one.</summary>
    public ScimResponse EnsureSuccess() {
        if (!IsSuccess) {
            throw new ScimException(ScimError.Read(StatusCode, Text), Exchange);
        }

        return this;
    }
}
