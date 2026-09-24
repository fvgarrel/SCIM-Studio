using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Http;
using ScimStudio.DemoServer.Filtering;
using ScimStudio.DemoServer.Schema;

namespace ScimStudio.DemoServer.Http;

/// <summary>
/// A list request (RFC 7644 section 3.4.2) from a query string or a SearchRequest body: filter, sort, page and trim the
/// resources of one type.
/// </summary>
internal sealed class ListQuery {
    private readonly FilterNode? _filter;
    private readonly AttributePath? _sortBy;
    private readonly bool _descending;
    private readonly int _startIndex;
    private readonly int _count;
    private readonly Projection _projection;

    private ListQuery(ResourceType type, int maxResults, Func<string, string?> parameter) {
        var filter = parameter("filter");
        _filter = string.IsNullOrWhiteSpace(filter) ? null : FilterParser.Parse(filter, type);
        _sortBy = ParseSortBy(type, parameter("sortBy"));
        _descending = ParseSortOrder(parameter("sortOrder"));
        // Out-of-range paging is clamped rather than refused, as RFC 7644 section 3.4.2.4 asks
        _startIndex = Math.Max(1, ParseInteger("startIndex", parameter("startIndex")) ?? 1);
        _count = Math.Clamp(ParseInteger("count", parameter("count")) ?? maxResults, 0, maxResults);
        _projection = Projection.Parse(type, parameter("attributes"), parameter("excludedAttributes"));
    }

    public static ListQuery FromQueryString(IQueryCollection query, ResourceType type, int maxResults) {
        return new ListQuery(type, maxResults, name => query.TryGetValue(name, out var values) ? values.ToString() : null);
    }

    /// <summary>Reads a SearchRequest body; numbers may come as JSON numbers or strings, attribute lists as arrays or strings.</summary>
    /// <param name="body">The body of a POST to <c>.search</c>.</param>
    /// <param name="type">The type of the resources to list.</param>
    /// <param name="maxResults">The most resources one page holds.</param>
    public static ListQuery FromSearchRequest(JsonObject body, ResourceType type, int maxResults) {
        return new ListQuery(type, maxResults, name => Text(name, JsonNodes.Find(body, name)));
    }

    public JsonObject Execute(IReadOnlyList<JsonObject> resources) {
        IReadOnlyList<JsonObject> matches = _filter is null ? resources : [.. resources.Where(_filter.Matches)];
        if (_sortBy is not null) {
            var comparer = new SortKeyComparer(_sortBy.Leaf!);
            matches = _descending ? [.. matches.OrderByDescending(SortKey, comparer)] : [.. matches.OrderBy(SortKey, comparer)];
        }
        var page = matches.Skip(_startIndex - 1).Take(_count).Select(_projection.Apply).ToList();
        return ScimResponses.ListResponse(page, matches.Count, _startIndex);
    }

    /// <summary>The value a resource sorts by; for a multi-valued attribute that of the primary element, or else the first.</summary>
    private JsonNode? SortKey(JsonObject resource) {
        var items = JsonNodes.Items(_sortBy!.Node(resource)).ToList();
        if (!_sortBy.Attribute.IsComplex) {
            return items.FirstOrDefault();
        }
        var elements = items.OfType<JsonObject>().ToList();
        var element = elements.FirstOrDefault(candidate => JsonNodes.IsTrue(candidate["primary"])) ?? elements.FirstOrDefault();
        return element?[_sortBy.Leaf!.Name];
    }

    private static AttributePath? ParseSortBy(ResourceType type, string? sortBy) {
        if (string.IsNullOrWhiteSpace(sortBy)) {
            return null;
        }
        var path = type.Resolve(sortBy.Trim())
            ?? throw ScimException.BadRequest(ScimException.INVALID_VALUE, $"Cannot sort by \"{sortBy}\": {type.Name} has no such attribute.");
        if (path.Leaf is null or { IsComplex: true }) {
            throw ScimException.BadRequest(
                ScimException.INVALID_VALUE, $"Cannot sort by \"{sortBy}\": name a simple attribute or a sub-attribute.");
        }
        return path;
    }

    private static bool ParseSortOrder(string? sortOrder) {
        if (string.IsNullOrWhiteSpace(sortOrder) || string.Equals(sortOrder, "ascending", StringComparison.OrdinalIgnoreCase)) {
            return false;
        }
        if (string.Equals(sortOrder, "descending", StringComparison.OrdinalIgnoreCase)) {
            return true;
        }
        throw ScimException.BadRequest(ScimException.INVALID_VALUE, $"sortOrder is \"ascending\" or \"descending\", not \"{sortOrder}\".");
    }

    private static int? ParseInteger(string name, string? text) {
        if (string.IsNullOrWhiteSpace(text)) {
            return null;
        }
        if (decimal.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number) && number == decimal.Truncate(number)) {
            return (int)Math.Clamp(number, int.MinValue, int.MaxValue);
        }
        throw ScimException.BadRequest(ScimException.INVALID_VALUE, $"{name} must be an integer, not \"{text}\".");
    }

    private static string? Text(string name, JsonNode? node) {
        return node switch {
            null => null,
            JsonArray array => string.Join(',', array.Select(JsonNodes.AsString)),
            JsonValue value when value.GetValueKind() == JsonValueKind.String => value.GetValue<string>(),
            JsonValue value when value.GetValueKind() == JsonValueKind.Number => value.ToJsonString(),
            _ => throw ScimException.BadRequest(ScimException.INVALID_VALUE, $"\"{name}\" has a value of the wrong type."),
        };
    }

    /// <summary>Orders sort keys by the attribute's type. A resource without a value sorts after the others in ascending order.</summary>
    private sealed class SortKeyComparer(ScimAttribute attribute) : IComparer<JsonNode?> {
        public int Compare(JsonNode? x, JsonNode? y) {
            if (x is null || y is null) {
                return (x is null ? 1 : 0) - (y is null ? 1 : 0);
            }
            if (attribute.Type == AttributeType.Boolean) {
                return JsonNodes.IsTrue(x).CompareTo(JsonNodes.IsTrue(y));
            }
            if (attribute.Type == AttributeType.DateTime && JsonNodes.TryGetInstant(x, out var left) && JsonNodes.TryGetInstant(y, out var right)) {
                return left.CompareTo(right);
            }
            return string.Compare(JsonNodes.AsString(x) ?? x.ToJsonString(), JsonNodes.AsString(y) ?? y.ToJsonString(), attribute.Comparison);
        }
    }
}
