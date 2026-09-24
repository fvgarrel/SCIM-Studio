using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;

namespace ScimStudio.Core.Http;

/// <summary>
/// Records every request that passes through together with its answer, so the log shows exactly what went over the wire - including what a
/// dialect sent before the one request the user asked for. Bodies are buffered; SCIM bodies are small.
/// </summary>
/// <param name="sink">Where finished exchanges go.</param>
public sealed class RecordingHandler(IExchangeSink sink) : DelegatingHandler {
    /// <summary>Where the exchange is left on the request, for the caller that wants to link its result to the log.</summary>
    public static readonly HttpRequestOptionsKey<HttpExchange> ExchangeKey = new("ScimStudio.Exchange");

    // A request body beyond this is kept as its beginning. Only a check sends one that large - a bulk request over the server's
    // size limit, on purpose - and a megabyte of it would slow the log down without telling anyone more.
    private const int KEPT_REQUEST = 256 * 1024;

    private static long _sequence;

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) {
        ArgumentNullException.ThrowIfNull(request);

        var scope = ExchangeScope.Current;
        var exchange = new HttpExchange {
            Sequence = Interlocked.Increment(ref _sequence),
            StartedAt = DateTimeOffset.Now,
            Method = request.Method.Method,
            Url = request.RequestUri ?? new Uri("about:blank"),
            RequestHeaders = Headers(request.Headers, request.Content?.Headers),
            RequestBody = request.Content is null ? null : Keep(await request.Content.ReadAsStringAsync(cancellationToken)),
            Origin = scope?.Origin,
        };

        var started = Stopwatch.GetTimestamp();
        try {
            var response = await base.SendAsync(request, cancellationToken);
            await response.Content.LoadIntoBufferAsync(cancellationToken);

            Finish(request, scope, exchange with {
                StatusCode = (int)response.StatusCode,
                ReasonPhrase = response.ReasonPhrase,
                ResponseHeaders = Headers(response.Headers, response.Content.Headers),
                ResponseBody = await response.Content.ReadAsStringAsync(cancellationToken),
                Duration = Stopwatch.GetElapsedTime(started),
            });

            return response;
        } catch (HttpRequestException failure) {
            Finish(request, scope, exchange with { Failure = Describe(failure), Duration = Stopwatch.GetElapsedTime(started) });
            throw;
        } catch (OperationCanceledException) {
            Finish(request, scope, exchange with { Failure = "Canceled or timed out.", Duration = Stopwatch.GetElapsedTime(started) });
            throw;
        }
    }

    /// <summary>A token with all but its last four characters masked, as the log and the interface show it.</summary>
    /// <param name="secret">The token.</param>
    public static string Mask(string secret) {
        ArgumentNullException.ThrowIfNull(secret);
        return secret.Length <= 8 ? "••••" : $"••••{secret[^4..]}";
    }

    /// <summary>The body as the log keeps it: whole, or its beginning and how many characters were left out.</summary>
    /// <param name="body">The body as sent.</param>
    private static string Keep(string body) {
        return body.Length <= KEPT_REQUEST
            ? body
            : string.Create(CultureInfo.InvariantCulture, $"{body[..KEPT_REQUEST]}… (+{body.Length - KEPT_REQUEST})");
    }

    private void Finish(HttpRequestMessage request, ExchangeScope? scope, HttpExchange exchange) {
        request.Options.Set(ExchangeKey, exchange);
        scope?.Add(exchange);
        sink.Record(exchange);
    }

    private static List<KeyValuePair<string, string>> Headers(HttpHeaders headers, HttpHeaders? contentHeaders) {
        var all = headers.AsEnumerable();
        if (contentHeaders is not null) {
            all = all.Concat(contentHeaders);
        }

        return [.. all.Select(header => new KeyValuePair<string, string>(header.Key, Redact(header.Key, string.Join(", ", header.Value))))];
    }

    private static string Redact(string name, string value) {
        if (!string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase)) {
            return value;
        }

        var space = value.IndexOf(' ', StringComparison.Ordinal);
        return space < 0 ? Mask(value) : $"{value[..space]} {Mask(value[(space + 1)..])}";
    }

    /// <summary>The innermost message, which is where .NET puts what actually went wrong: the refused connection, the certificate.</summary>
    /// <param name="failure">The failure.</param>
    private static string Describe(Exception failure) {
        var messages = new List<string>();
        for (var current = failure; current is not null; current = current.InnerException) {
            if (!messages.Contains(current.Message)) {
                messages.Add(current.Message);
            }
        }

        return string.Join(" → ", messages);
    }
}
