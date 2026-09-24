using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScimStudio.App.Localization;
using ScimStudio.Core.Checks;
using ScimStudio.Core.Http;
using ScimStudio.Core.Scim;

namespace ScimStudio.App.Services;

/// <summary>A run of the checks as a report tells it: where and when it ran, what the server said it offers, and every verdict.</summary>
public sealed record CheckRun {
    public required Uri BaseUrl { get; init; }

    public required DateTimeOffset StartedAt { get; init; }

    public TimeSpan Elapsed { get; init; }

    /// <summary>Whether the run was stopped before its end; the checks it did not reach are still pending.</summary>
    public bool Cancelled { get; init; }

    /// <summary>Whether the connection accepted any certificate.</summary>
    public bool AcceptInvalidCertificates { get; init; }

    /// <summary>What the server said it supports when the session connected, if it said.</summary>
    public ServiceProviderConfig? Configuration { get; init; }

    /// <summary>Every check in the order they run, pending where the run did not get to it.</summary>
    public required IReadOnlyList<CheckResult> Results { get; init; }
}

/// <summary>
/// Writes a run of the checks down: as Markdown for a person - a ticket, an issue, a mail - and as JSON with every request and answer, for a
/// tool or a later comparison. Both in the language the interface shows, and neither with the token: the log only ever kept it masked.
/// </summary>
public static class CheckReport {
    private static Localizer L => Localizer.Instance;

    /// <summary>How long something took, as the checks page and the report say it: milliseconds below a second, seconds above.</summary>
    /// <param name="span">The time taken.</param>
    public static string Duration(TimeSpan span) {
        return span.TotalSeconds < 1
            ? string.Create(L.Culture, $"{span.TotalMilliseconds:0} ms")
            : string.Create(L.Culture, $"{span.TotalSeconds:0.0} s");
    }

    /// <summary>A name for the report's file, without its extension: the server's host and when the run began.</summary>
    /// <param name="run">The run.</param>
    public static string FileName(CheckRun run) {
        ArgumentNullException.ThrowIfNull(run);

        var host = new string([.. run.BaseUrl.Host.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '-' ? c : '-')]);
        return string.Create(CultureInfo.InvariantCulture, $"scim-checks-{host}-{run.StartedAt:yyyy-MM-dd-HHmm}");
    }

    /// <summary>
    /// The whole run for reading: where and when, the problems first, every check in a table, and the details of every check that did not
    /// pass - its notes, and each request and answer in full.
    /// </summary>
    /// <param name="run">The run.</param>
    public static string Markdown(CheckRun run) {
        ArgumentNullException.ThrowIfNull(run);

        var text = new StringBuilder();
        text.Append("# ").AppendLine(L.Get("report.title")).AppendLine();
        Context(text, run);

        var problems = run.Results.Where(r => r.Status is CheckStatus.Failed or CheckStatus.Warning).ToList();
        if (problems.Count > 0) {
            text.AppendLine().Append("## ").AppendLine(L.Get("report.problems")).AppendLine();
            foreach (var result in problems) {
                text.Append("- ").Append(Mark(result.Status)).Append(" **").Append(Inline(Title(result))).Append("** · ")
                    .Append(result.Check.Reference);
                if (result.Notes.Count > 0) {
                    text.Append(" · ").Append(Inline(L.Format(result.Notes[0])));
                }

                text.AppendLine();
            }
        }

        text.AppendLine().Append("## ").AppendLine(L.Get("report.checks"));
        foreach (var category in run.Results.GroupBy(r => r.Check.Category)) {
            text.AppendLine().Append("### ").AppendLine(L.Get($"category.{category.Key}")).AppendLine();
            text.Append("| | ").Append(L.Get("report.check")).Append(" | ").Append(L.Get("report.reference")).Append(" | ")
                .Append(L.Get("report.result")).AppendLine(" |");
            text.AppendLine("| --- | --- | --- | --- |");
            foreach (var result in category) {
                text.Append("| ").Append(Mark(result.Status)).Append(" | ").Append(Cell(Title(result))).Append(" | ")
                    .Append(Cell(result.Check.Reference)).Append(" | ").Append(Status(result.Status)).AppendLine(" |");
            }
        }

        var detailed = run.Results.Where(r => r.Status is not (CheckStatus.Passed or CheckStatus.Pending or CheckStatus.Running)).ToList();
        if (detailed.Count > 0) {
            text.AppendLine().Append("## ").AppendLine(L.Get("report.details"));
            foreach (var result in detailed) {
                text.AppendLine();
                Details(text, result, "###");
            }
        }

        return text.ToString();
    }

    /// <summary>One check to paste somewhere: its verdict, notes and every request and answer, and the run it came from.</summary>
    /// <param name="run">The run.</param>
    /// <param name="result">The check's verdict.</param>
    public static string Markdown(CheckRun run, CheckResult result) {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(result);

        var text = new StringBuilder();
        Details(text, result, "##");
        text.AppendLine().Append(L.Get("report.server")).Append(": ").Append(run.BaseUrl.AbsoluteUri).Append(" · ")
            .Append(Moment(run.StartedAt)).Append(" · SCIM Studio ").AppendLine(ScimClient.Version);
        return text.ToString();
    }

    /// <summary>The whole run with nothing left out: every check with its notes, and every request and answer with headers and bodies.</summary>
    /// <param name="run">The run.</param>
    public static string Json(CheckRun run) {
        ArgumentNullException.ThrowIfNull(run);
        return Document(run, run.Results, summary: true).ToJsonString(ScimJson.Readable);
    }

    /// <summary>One check in the shape of the whole report, without the summary a single check has no use for.</summary>
    /// <param name="run">The run.</param>
    /// <param name="result">The check's verdict.</param>
    public static string Json(CheckRun run, CheckResult result) {
        ArgumentNullException.ThrowIfNull(run);
        ArgumentNullException.ThrowIfNull(result);
        return Document(run, [result], summary: false).ToJsonString(ScimJson.Readable);
    }

    /// <summary>A check's requests as commands to run one after another, each after a comment saying what came back.</summary>
    /// <param name="result">The check's verdict.</param>
    /// <param name="command">Writes one request as a command, e.g. <see cref="ShellCommand.Curl"/>.</param>
    public static string Commands(CheckResult result, Func<HttpExchange, string> command) {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(command);

        var text = new StringBuilder();
        text.Append("# SCIM Studio · ").Append(Title(result)).Append(" (").Append(result.Check.Id).AppendLine(")");
        text.Append("# ").AppendLine(L.Get("report.replay"));
        text.Append("# ").AppendLine(L.Format("report.token", ShellCommand.TOKEN_VARIABLE));

        var number = 0;
        foreach (var exchange in result.Exchanges) {
            text.AppendLine().Append("# ").Append(++number).Append(" · ").Append(exchange.Method).Append(' ').Append(exchange.Url.PathAndQuery)
                .Append(" → ").AppendLine(Answer(exchange));
            text.AppendLine(command(exchange));
        }

        return text.ToString();
    }

    private static void Context(StringBuilder text, CheckRun run) {
        text.Append("- **").Append(L.Get("report.server")).Append(":** ").Append(run.BaseUrl.AbsoluteUri);
        if (run.AcceptInvalidCertificates) {
            text.Append(" (").Append(L.Get("report.certificatesUnchecked")).Append(')');
        }

        text.AppendLine();
        text.Append("- **").Append(L.Get("report.run")).Append(":** ").Append(Moment(run.StartedAt)).Append(" · ").Append(Duration(run.Elapsed))
            .Append(" · SCIM Studio ").Append(ScimClient.Version);
        if (run.Cancelled) {
            text.Append(" · ").Append(L.Get("report.cancelled"));
        }

        text.AppendLine();
        text.Append("- **").Append(L.Get("report.result")).Append(":** ").AppendLine(Summary(run));
        if (run.Configuration is { } configuration) {
            text.Append("- **").Append(L.Get("report.configuration")).Append(":** ").AppendLine(Offers(configuration));
        }
    }

    /// <summary>How many checks ended how, in the words of the checks page - the count after the word, which is then right for any count.</summary>
    /// <param name="run">The run.</param>
    private static string Summary(CheckRun run) {
        var counts = new List<string>();
        foreach (var (status, key) in new[] {
            (CheckStatus.Passed, "summary.passed"),
            (CheckStatus.Warning, "summary.warnings"),
            (CheckStatus.Failed, "summary.failed"),
            (CheckStatus.Unsupported, "summary.unsupported"),
            (CheckStatus.Skipped, "summary.skipped"),
            (CheckStatus.Pending, "summary.pending"),
        }) {
            var count = run.Results.Count(r => r.Status == status);
            if (count > 0 || status != CheckStatus.Pending) {
                counts.Add(string.Create(L.Culture, $"{L.Get(key)}: {count}"));
            }
        }

        return string.Join(" · ", counts);
    }

    private static string Offers(ServiceProviderConfig configuration) {
        var features = new (string Name, bool Offered)[] {
            ("PATCH", configuration.PatchSupported),
            ("bulk", configuration.BulkSupported),
            ("filter", configuration.FilterSupported),
            ("sort", configuration.SortSupported),
            ("ETag", configuration.EtagSupported),
            ("changePassword", configuration.ChangePasswordSupported),
        };

        var offered = string.Join(", ", features.Where(f => f.Offered).Select(f => f.Name));
        var missing = string.Join(", ", features.Where(f => !f.Offered).Select(f => f.Name));
        return L.Format("report.offers", offered.Length > 0 ? offered : "—", missing.Length > 0 ? missing : "—");
    }

    /// <summary>A check in full: heading, verdict, notes, and each request with its answer.</summary>
    /// <param name="text">What the check is written to.</param>
    /// <param name="result">The check's verdict.</param>
    /// <param name="heading">The Markdown heading the check starts with; its requests go one level below.</param>
    private static void Details(StringBuilder text, CheckResult result, string heading) {
        text.Append(heading).Append(' ').Append(Mark(result.Status)).Append(' ').AppendLine(Inline(Title(result))).AppendLine();
        text.Append(Status(result.Status)).Append(" · ").Append(result.Check.Reference).Append(" · `").Append(result.Check.Id).Append('`');
        if (result.Status is not (CheckStatus.Pending or CheckStatus.Running or CheckStatus.Skipped)) {
            text.Append(" · ").Append(Duration(result.Duration));
        }

        text.AppendLine();
        foreach (var note in result.Notes) {
            text.AppendLine().Append("> ").AppendLine(Inline(L.Format(note)));
        }

        var number = 0;
        foreach (var exchange in result.Exchanges) {
            text.AppendLine().Append(heading).Append("# ").Append(++number).Append(". `").Append(exchange.Method).Append(' ')
                .Append(exchange.Url.PathAndQuery).Append("` → ").AppendLine(Answer(exchange)).AppendLine();
            Fenced(text, Request(exchange));
            text.AppendLine();
            Fenced(text, Response(exchange));
        }
    }

    private static string Request(HttpExchange exchange) {
        var request = new StringBuilder().Append(exchange.Method).Append(' ').AppendLine(exchange.Url.AbsoluteUri);
        foreach (var (name, value) in exchange.RequestHeaders) {
            request.Append(name).Append(": ").AppendLine(value);
        }

        if (!string.IsNullOrEmpty(exchange.RequestBody)) {
            request.AppendLine().AppendLine(ScimJson.Pretty(exchange.RequestBody));
        }

        return request.ToString();
    }

    private static string Response(HttpExchange exchange) {
        if (exchange.StatusCode is not { } status) {
            return L.Format("note.transport", exchange.Failure ?? string.Empty);
        }

        var response = new StringBuilder().Append("HTTP ").Append(status.ToString(CultureInfo.InvariantCulture)).Append(' ')
            .AppendLine(exchange.ReasonPhrase);
        foreach (var (name, value) in exchange.ResponseHeaders) {
            response.Append(name).Append(": ").AppendLine(value);
        }

        if (!string.IsNullOrEmpty(exchange.ResponseBody)) {
            response.AppendLine().AppendLine(ScimJson.Pretty(exchange.ResponseBody));
        }

        return response.ToString();
    }

    /// <summary>A code block whose fence is longer than any run of backticks inside, so no body can end it early.</summary>
    /// <param name="text">What the block is written to.</param>
    /// <param name="content">What goes in the block.</param>
    private static void Fenced(StringBuilder text, string content) {
        var longest = 0;
        var run = 0;
        foreach (var character in content) {
            run = character == '`' ? run + 1 : 0;
            longest = Math.Max(longest, run);
        }

        var fence = new string('`', Math.Max(3, longest + 1));
        text.Append(fence).AppendLine("http").AppendLine(content.TrimEnd()).AppendLine(fence);
    }

    private static JsonObject Document(CheckRun run, IEnumerable<CheckResult> results, bool summary) {
        var document = new JsonObject {
            ["tool"] = new JsonObject { ["name"] = "SCIM Studio", ["version"] = ScimClient.Version },
            ["run"] = new JsonObject {
                ["startedAt"] = run.StartedAt.ToString("O", CultureInfo.InvariantCulture),
                ["durationMs"] = Milliseconds(run.Elapsed),
                ["cancelled"] = run.Cancelled,
                ["language"] = L.Language,
            },
            ["server"] = new JsonObject {
                ["baseUrl"] = run.BaseUrl.AbsoluteUri,
                ["acceptInvalidCertificates"] = run.AcceptInvalidCertificates,
                ["configuration"] = run.Configuration?.Document.DeepClone(),
            },
        };

        if (summary) {
            var counts = new JsonObject { ["total"] = run.Results.Count };
            foreach (var status in Enum.GetValues<CheckStatus>().Where(s => s != CheckStatus.Running)) {
                counts[Name(status)] = run.Results.Count(r => r.Status == status);
            }

            document["summary"] = counts;
        }

        document["checks"] = new JsonArray([.. results.Select(Check)]);
        return document;
    }

    private static JsonObject Check(CheckResult result) {
        return new JsonObject {
            ["id"] = result.Check.Id,
            ["category"] = result.Check.Category,
            ["title"] = Title(result),
            ["reference"] = result.Check.Reference,
            ["status"] = Name(result.Status),
            ["durationMs"] = Milliseconds(result.Duration),
            ["notes"] = new JsonArray([.. result.Notes.Select(n => new JsonObject { ["key"] = n.Key, ["text"] = L.Format(n) })]),
            ["requests"] = new JsonArray([.. result.Exchanges.Select(Exchange)]),
        };
    }

    private static JsonObject Exchange(HttpExchange exchange) {
        return new JsonObject {
            ["method"] = exchange.Method,
            ["url"] = exchange.Url.AbsoluteUri,
            ["startedAt"] = exchange.StartedAt.ToString("O", CultureInfo.InvariantCulture),
            ["durationMs"] = Milliseconds(exchange.Duration),
            ["status"] = exchange.StatusCode,
            ["reason"] = exchange.ReasonPhrase,
            ["failure"] = exchange.Failure,
            ["requestHeaders"] = Headers(exchange.RequestHeaders),
            ["requestBody"] = Body(exchange.RequestBody),
            ["responseHeaders"] = Headers(exchange.ResponseHeaders),
            ["responseBody"] = Body(exchange.ResponseBody),
        };
    }

    private static JsonObject Headers(IReadOnlyList<KeyValuePair<string, string>> headers) {
        var all = new JsonObject();
        foreach (var (name, value) in headers) {
            all[name] = all[name] is { } earlier ? $"{earlier.GetValue<string>()}, {value}" : value;
        }

        return all;
    }

    /// <summary>A body as JSON where it is JSON, so the report reads like the answer did; as text where it is not.</summary>
    /// <param name="body">The body as sent.</param>
    private static JsonNode? Body(string? body) {
        return string.IsNullOrEmpty(body) ? null : ScimJson.Parse(body) ?? JsonValue.Create(body);
    }

    private static double Milliseconds(TimeSpan span) {
        return Math.Round(span.TotalMilliseconds, 1);
    }

    private static string Name(CheckStatus status) {
        return JsonNamingPolicy.CamelCase.ConvertName(status.ToString());
    }

    private static string Title(CheckResult result) {
        return L.Get($"check.{result.Check.Id}");
    }

    private static string Status(CheckStatus status) {
        return L.Get($"status.{Name(status)}");
    }

    private static string Answer(HttpExchange exchange) {
        return exchange.StatusCode?.ToString(CultureInfo.InvariantCulture) ?? "—";
    }

    private static string Moment(DateTimeOffset moment) {
        return moment.ToLocalTime().ToString("G", L.Culture);
    }

    /// <summary>The verdict as a sign that reads the same in every language, in the order of the marks on the checks page.</summary>
    /// <param name="status">The verdict.</param>
    private static string Mark(CheckStatus status) {
        return status switch {
            CheckStatus.Passed => "✅",
            CheckStatus.Warning => "⚠️",
            CheckStatus.Failed => "❌",
            CheckStatus.Unsupported => "🚫",
            CheckStatus.Skipped => "➖",
            _ => "⚪",
        };
    }

    /// <summary>
    /// Text from a server - a note quotes its errors - on one line, with <c>&lt;</c> escaped, so an HTML error page shows as text and does
    /// not vanish into the rendering.
    /// </summary>
    /// <param name="text">The text.</param>
    private static string Inline(string text) {
        return text.ReplaceLineEndings(" ").Replace("<", "\\<", StringComparison.Ordinal);
    }

    private static string Cell(string text) {
        return Inline(text).Replace("|", "\\|", StringComparison.Ordinal);
    }
}
