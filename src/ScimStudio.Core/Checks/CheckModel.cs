using System.Diagnostics.CodeAnalysis;
using System.Text.Json.Nodes;
using ScimStudio.Core.Http;
using ScimStudio.Core.Scim;
using ScimStudio.Core.Text;

namespace ScimStudio.Core.Checks;

public enum CheckStatus {
    Pending,
    Running,
    Passed,
    Warning,
    Failed,

    /// <summary>The server does not offer what the check needs - by its own configuration, or by how it answered.</summary>
    Unsupported,

    /// <summary>The check could not run here, mostly because one it stands on did not pass.</summary>
    Skipped,
}

/// <summary>A check as the suite lists it before it runs: its catalogue key, where it belongs, and what it checks against.</summary>
/// <param name="Id">The check's id, which is also its catalogue key under <c>check.</c>.</param>
/// <param name="Category">The group it is shown in.</param>
/// <param name="Reference">The section of the RFCs, or the identity provider, whose behaviour it checks.</param>
public sealed record CheckInfo(string Id, string Category, string Reference);

/// <summary>What a check found, and the requests it sent to find out.</summary>
public sealed record CheckResult {
    public required CheckInfo Check { get; init; }

    public CheckStatus Status { get; init; }

    /// <summary>Why it failed, what it warns about, or why it was skipped.</summary>
    public IReadOnlyList<Message> Notes { get; init; } = [];

    public TimeSpan Duration { get; init; }

    public IReadOnlyList<HttpExchange> Exchanges { get; init; } = [];
}

/// <summary>Ends a check early with a verdict: a failure, something the server does not support, or something the check cannot test here.</summary>
public sealed class CheckStoppedException : Exception {
    // Inside an exception, Message is the exception's own text; the note type is named in full.
    public CheckStoppedException() : this(CheckStatus.Failed, Text.Message.Of("note.text", string.Empty)) {
    }

    public CheckStoppedException(string message) : base(message) {
        Status = CheckStatus.Failed;
        Note = Text.Message.Of("note.text", message);
    }

    public CheckStoppedException(string message, Exception innerException) : base(message, innerException) {
        Status = CheckStatus.Failed;
        Note = Text.Message.Of("note.text", message);
    }

    /// <summary>A verdict with the note that explains it.</summary>
    /// <param name="status">Failed, unsupported or skipped.</param>
    /// <param name="note">Why.</param>
    public CheckStoppedException(CheckStatus status, Message note) : base(note?.Key) {
        ArgumentNullException.ThrowIfNull(note);
        Status = status;
        Note = note;
    }

    public CheckStatus Status { get; }

    public Message Note { get; }
}

/// <summary>What a running check has to hand: the client, the notes it has made, and the ways to reach a verdict.</summary>
/// <param name="client">The client.</param>
/// <param name="cancellationToken">Cancels the run.</param>
internal sealed class CheckContext(ScimClient client, CancellationToken cancellationToken) {
    public ScimClient Client { get; } = client;

    public CancellationToken CancellationToken { get; } = cancellationToken;

    public List<Message> Notes { get; } = [];

    public bool Warned { get; private set; }

    public void Warn(Message note) {
        Notes.Add(note);
        Warned = true;
    }

    [DoesNotReturn]
    public static void Fail(string key, params object?[] args) {
        throw new CheckStoppedException(CheckStatus.Failed, Message.Of(key, args));
    }

    [DoesNotReturn]
    public static void Skip(string key, params object?[] args) {
        throw new CheckStoppedException(CheckStatus.Skipped, Message.Of(key, args));
    }

    [DoesNotReturn]
    public static void Unsupported(string key, params object?[] args) {
        throw new CheckStoppedException(CheckStatus.Unsupported, Message.Of(key, args));
    }

    public Task<ScimResponse> SendAsync(HttpMethod method, string path, JsonNode? body = null) {
        return Client.SendAsync(method, path, body, CancellationToken);
    }

    public Task<ScimResponse> SendAsync(ScimRequest request) {
        return Client.SendAsync(request, CancellationToken);
    }

    /// <summary>The answer, when its status is one of those expected; otherwise the check fails, naming what came back instead.</summary>
    /// <param name="response">The answer.</param>
    /// <param name="expected">The statuses that pass.</param>
    public static ScimResponse Expect(ScimResponse response, params int[] expected) {
        ArgumentNullException.ThrowIfNull(response);

        if (!expected.Contains(response.StatusCode)) {
            var error = ScimError.Read(response.StatusCode, response.Text);
            Fail("note.status", string.Join(" / ", expected), response.StatusCode, error.Detail ?? string.Empty);
        }

        return response;
    }

    /// <summary>Notes a failure answer without the <c>scimType</c> RFC 7644 section 3.12 gives a client to act on.</summary>
    /// <param name="response">The failure.</param>
    /// <param name="scimTypes">The keywords that fit; the first is the one named.</param>
    public void ExpectScimType(ScimResponse response, params string[] scimTypes) {
        ArgumentNullException.ThrowIfNull(response);

        var sent = ScimError.Read(response.StatusCode, response.Text).ScimType;
        if (!scimTypes.Contains(sent, StringComparer.Ordinal)) {
            Warn(Message.Of("note.scimType", string.Join(" / ", scimTypes), sent ?? "—"));
        }
    }

    public void ExpectMediaType(ScimResponse response) {
        ArgumentNullException.ThrowIfNull(response);

        if (!string.Equals(response.ContentType, ScimSchemas.MEDIA_TYPE, StringComparison.OrdinalIgnoreCase)) {
            Warn(Message.Of("note.mediaType", response.ContentType ?? "—"));
        }
    }

    /// <summary>The answer's body as a resource or list; the check fails when there is none.</summary>
    /// <param name="response">The answer.</param>
    public static JsonObject Body(ScimResponse response) {
        ArgumentNullException.ThrowIfNull(response);
        return response.Body as JsonObject ?? throw new CheckStoppedException(CheckStatus.Failed, Message.Of("note.noBody", response.StatusCode));
    }

    public static void Equal(string attribute, string? actual, string? expected, bool ignoreCase = false) {
        var comparison = ignoreCase ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!string.Equals(actual, expected, comparison)) {
            Fail("note.value", attribute, actual ?? "—", expected ?? "—");
        }
    }
}
