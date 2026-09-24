using System.Text;

namespace ScimStudio.Core.Http;

/// <summary>
/// An exchange's request as a command to paste into a terminal. The token is a variable rather than the secret, so the command can go into a
/// ticket or a chat as it is; <c>SCIM_TOKEN</c> is set once in the shell it runs in.
/// </summary>
public static class ShellCommand {
    public const string TOKEN_VARIABLE = "SCIM_TOKEN";

    /// <summary>The request as curl for bash, zsh and friends.</summary>
    /// <param name="exchange">The exchange whose request is repeated.</param>
    public static string Curl(HttpExchange exchange) {
        ArgumentNullException.ThrowIfNull(exchange);

        var command = new StringBuilder("curl");
        if (exchange.Method != "GET") {
            command.Append(" -X ").Append(exchange.Method);
        }

        command.Append(' ').Append(Single(exchange.Url.AbsoluteUri));

        foreach (var (name, value) in Forwarded(exchange)) {
            var header = IsAuthorization(name) ? $"\"Authorization: Bearer ${TOKEN_VARIABLE}\"" : Single($"{name}: {value}");
            command.Append(" \\\n  -H ").Append(header);
        }

        if (!string.IsNullOrEmpty(exchange.RequestBody)) {
            command.Append(" \\\n  --data-raw ").Append(Single(exchange.RequestBody));
        }

        return command.ToString();
    }

    /// <summary>The request as Invoke-RestMethod for PowerShell 7.</summary>
    /// <param name="exchange">The exchange whose request is repeated.</param>
    public static string PowerShell(HttpExchange exchange) {
        ArgumentNullException.ThrowIfNull(exchange);

        var headers = Forwarded(exchange)
            .Where(header => !string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase))
            .Select(header => IsAuthorization(header.Key)
                ? $"  Authorization = \"Bearer $env:{TOKEN_VARIABLE}\""
                : $"  {Quoted(header.Key)} = {Quoted(header.Value)}");

        var command = new StringBuilder();
        command.Append("$headers = @{\n").AppendJoin('\n', headers).Append("\n}\n");
        command.Append("Invoke-RestMethod -Method ").Append(exchange.Method)
            .Append(" -Uri ").Append(Quoted(exchange.Url.AbsoluteUri))
            .Append(" -Headers $headers");

        if (!string.IsNullOrEmpty(exchange.RequestBody)) {
            var contentType = exchange.RequestHeaders
                .FirstOrDefault(header => string.Equals(header.Key, "Content-Type", StringComparison.OrdinalIgnoreCase)).Value;
            command.Append(" -ContentType ").Append(Quoted(contentType ?? "application/scim+json"))
                .Append(" -Body ").Append(Quoted(exchange.RequestBody));
        }

        return command.ToString();
    }

    /// <summary>The headers worth repeating: the ones the request was written with, not the ones the transport adds.</summary>
    /// <param name="exchange">The exchange.</param>
    private static IEnumerable<KeyValuePair<string, string>> Forwarded(HttpExchange exchange) {
        return exchange.RequestHeaders.Where(header =>
            !string.Equals(header.Key, "Content-Length", StringComparison.OrdinalIgnoreCase)
            && !string.Equals(header.Key, "User-Agent", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsAuthorization(string name) {
        return string.Equals(name, "Authorization", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>POSIX single quotes: nothing inside is special, and a quote itself is closed, escaped and reopened.</summary>
    /// <param name="text">The text.</param>
    private static string Single(string text) {
        return $"'{text.Replace("'", "'\\''", StringComparison.Ordinal)}'";
    }

    /// <summary>PowerShell single quotes, in which a quote is doubled and nothing else is special.</summary>
    /// <param name="text">The text.</param>
    private static string Quoted(string text) {
        return $"'{text.Replace("'", "''", StringComparison.Ordinal)}'";
    }
}
