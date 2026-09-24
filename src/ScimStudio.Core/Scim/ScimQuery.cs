using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace ScimStudio.Core.Scim;

/// <summary>The parameters of a list request (RFC 7644 section 3.4.2), as a query string or as the body of a <c>.search</c>.</summary>
public sealed record ScimQuery {
    public string? Filter { get; init; }

    public int? StartIndex { get; init; }

    public int? Count { get; init; }

    public string? SortBy { get; init; }

    /// <summary><c>ascending</c> or <c>descending</c>; the server's default when null.</summary>
    public string? SortOrder { get; init; }

    public IReadOnlyList<string>? Attributes { get; init; }

    public IReadOnlyList<string>? ExcludedAttributes { get; init; }

    /// <summary>The parameters as a query string, starting with <c>?</c>, or empty when there are none.</summary>
    public string ToQueryString() {
        var parameters = new List<string>();
        Add(parameters, "filter", Filter);
        Add(parameters, "startIndex", StartIndex?.ToString(CultureInfo.InvariantCulture));
        Add(parameters, "count", Count?.ToString(CultureInfo.InvariantCulture));
        Add(parameters, "sortBy", SortBy);
        Add(parameters, "sortOrder", SortOrder);
        Add(parameters, "attributes", Attributes is { Count: > 0 } ? string.Join(',', Attributes) : null);
        Add(parameters, "excludedAttributes", ExcludedAttributes is { Count: > 0 } ? string.Join(',', ExcludedAttributes) : null);

        return parameters.Count == 0 ? string.Empty : new StringBuilder("?").AppendJoin('&', parameters).ToString();
    }

    /// <summary>The parameters as a SearchRequest (RFC 7644 section 3.4.3).</summary>
    public JsonObject ToSearchRequest() {
        var search = new JsonObject { ["schemas"] = new JsonArray(ScimSchemas.SEARCH_REQUEST) };
        if (Filter is not null) {
            search["filter"] = Filter;
        }

        if (StartIndex is { } start) {
            search["startIndex"] = start;
        }

        if (Count is { } count) {
            search["count"] = count;
        }

        if (SortBy is not null) {
            search["sortBy"] = SortBy;
        }

        if (SortOrder is not null) {
            search["sortOrder"] = SortOrder;
        }

        if (Attributes is { Count: > 0 }) {
            search["attributes"] = new JsonArray([.. Attributes.Select(a => JsonValue.Create(a))]);
        }

        if (ExcludedAttributes is { Count: > 0 }) {
            search["excludedAttributes"] = new JsonArray([.. ExcludedAttributes.Select(a => JsonValue.Create(a))]);
        }

        return search;
    }

    private static void Add(List<string> parameters, string name, string? value) {
        if (!string.IsNullOrWhiteSpace(value)) {
            parameters.Add($"{name}={Uri.EscapeDataString(value)}");
        }
    }
}

/// <summary>Writes filter expressions (RFC 7644 section 3.4.2.2) with their values quoted as JSON strings, escapes included.</summary>
public static class ScimFilterText {
    /// <summary>
    /// A value as a filter's string literal. Only what JSON requires is escaped: an umlaut sent as <c>ü</c> is valid, but a server that
    /// reads its filters by hand may not decode it.
    /// </summary>
    /// <param name="value">The value.</param>
    public static string Quote(string value) {
        ArgumentNullException.ThrowIfNull(value);

        var quoted = new StringBuilder("\"");
        foreach (var character in value) {
            if (character is '"' or '\\') {
                quoted.Append('\\').Append(character);
            } else if (char.IsControl(character)) {
                quoted.Append(CultureInfo.InvariantCulture, $"\\u{(int)character:x4}");
            } else {
                quoted.Append(character);
            }
        }

        return quoted.Append('"').ToString();
    }

    public static string Eq(string attribute, string value) {
        return $"{attribute} eq {Quote(value)}";
    }

    public static string Co(string attribute, string value) {
        return $"{attribute} co {Quote(value)}";
    }

    public static string Sw(string attribute, string value) {
        return $"{attribute} sw {Quote(value)}";
    }

    /// <summary>The expressions joined with <c>or</c>.</summary>
    /// <param name="expressions">The expressions.</param>
    public static string Any(params IEnumerable<string> expressions) {
        return string.Join(" or ", expressions);
    }
}
