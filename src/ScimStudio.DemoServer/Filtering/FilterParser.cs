using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using ScimStudio.DemoServer.Schema;

namespace ScimStudio.DemoServer.Filtering;

/// <summary>
/// Parses a filter (RFC 7644 section 3.4.2.2) and binds its attribute paths to a resource type's schema. Precedence runs
/// <c>not</c>, <c>and</c>, <c>or</c>; keywords and operators ignore case.
/// </summary>
internal sealed class FilterParser {
    private readonly List<Token> _tokens;
    private readonly ResourceType _type;
    // Inside brackets, names are sub-attributes of this attribute.
    private AttributePath? _scope;
    private int _next;

    private FilterParser(string filter, ResourceType type, AttributePath? scope) {
        _tokens = FilterTokenizer.Tokenize(filter);
        _type = type;
        _scope = scope;
    }

    public static FilterNode Parse(string filter, ResourceType type) {
        return new FilterParser(filter, type, null).ParseFilter();
    }

    /// <summary>Parses the filter between the brackets of a PATCH path, whose names are sub-attributes of <paramref name="attribute"/>.</summary>
    /// <param name="filter">The text between the brackets.</param>
    /// <param name="type">The resource type the path belongs to.</param>
    /// <param name="attribute">The multi-valued attribute in front of the brackets.</param>
    public static FilterNode ParseValueFilter(string filter, ResourceType type, AttributePath attribute) {
        return new FilterParser(filter, type, attribute).ParseFilter();
    }

    public static ScimException Error(string detail) {
        return ScimException.BadRequest(ScimException.INVALID_FILTER, detail);
    }

    private FilterNode ParseFilter() {
        var filter = ParseOr();
        if (Peek().Kind != TokenKind.End) {
            throw Error($"Unexpected {Peek()}.");
        }
        return filter;
    }

    private FilterNode ParseOr() {
        var filter = ParseAnd();
        while (IsKeyword(Peek(), "or")) {
            _next++;
            filter = new OrFilter(filter, ParseAnd());
        }
        return filter;
    }

    private FilterNode ParseAnd() {
        var filter = ParseUnary();
        while (IsKeyword(Peek(), "and")) {
            _next++;
            filter = new AndFilter(filter, ParseUnary());
        }
        return filter;
    }

    private FilterNode ParseUnary() {
        if (IsKeyword(Peek(), "not")) {
            _next++;
            Expect(TokenKind.OpenParenthesis, "'(' after 'not'");
            var inner = ParseOr();
            Expect(TokenKind.CloseParenthesis, "')'");
            return new NotFilter(inner);
        }
        if (Peek().Kind == TokenKind.OpenParenthesis) {
            _next++;
            var inner = ParseOr();
            Expect(TokenKind.CloseParenthesis, "')'");
            return inner;
        }
        return ParseAttributeExpression();
    }

    private FilterNode ParseAttributeExpression() {
        var name = Expect(TokenKind.Word, "an attribute name");
        var path = Resolve(name);
        if (Peek().Kind == TokenKind.OpenBracket) {
            var valuePath = ParseValuePath(name, path);
            return Peek() is { Kind: TokenKind.Word } next && next.Text.StartsWith('.') ? ParseValuePathLeaf(valuePath) : valuePath;
        }
        return ParseComparison(name, path);
    }

    /// <summary>
    /// <c>emails[type eq "work"].value eq "…"</c>: outside RFC 7644's grammar, but how Microsoft Entra ID looks people up by
    /// their work address. An element has to match both the brackets and the comparison.
    /// </summary>
    /// <param name="valuePath">The attribute and the filter in brackets, read up to the closing bracket.</param>
    private ValuePathFilter ParseValuePathLeaf(ValuePathFilter valuePath) {
        var name = Next();
        var subAttribute = valuePath.Path.Attribute.FindSubAttribute(name.Text[1..])
            ?? throw Error($"'{valuePath.Path.Attribute.Name}' has no sub-attribute '{name.Text[1..]}' (position {name.Position}).");
        var comparison = ParseComparison(name, new AttributePath(null, subAttribute, null));
        return valuePath with { Inner = new AndFilter(valuePath.Inner, comparison) };
    }

    /// <summary><c>pr</c>, or an operator and its value, after an attribute path.</summary>
    /// <param name="name">The path as written, for the messages.</param>
    /// <param name="path">The path resolved.</param>
    private FilterNode ParseComparison(Token name, AttributePath path) {
        var operatorToken = Expect(TokenKind.Word, $"an operator after '{name.Text}'");
        if (string.Equals(operatorToken.Text, "pr", StringComparison.OrdinalIgnoreCase)) {
            return new PresentFilter(path);
        }
        var compareOperator = ParseOperator(operatorToken.Text)
            ?? throw Error($"Unknown operator {operatorToken}; use eq, ne, co, sw, ew, gt, ge, lt, le or pr.");
        var value = ParseValue();
        Check(path, compareOperator, value, name.Text, operatorToken.Text);
        return new CompareFilter(path, compareOperator, value);
    }

    private ValuePathFilter ParseValuePath(Token name, AttributePath path) {
        if (_scope is not null) {
            throw Error($"Value filters cannot be nested ({Peek()}).");
        }
        if (!path.Attribute.IsComplex || path.SubAttribute is not null) {
            throw Error($"'{name.Text}' is not a complex attribute, so it takes no filter in brackets.");
        }
        _next++;
        _scope = path;
        var inner = ParseOr();
        _scope = null;
        Expect(TokenKind.CloseBracket, "']'");
        return new ValuePathFilter(path, inner);
    }

    private AttributePath Resolve(Token name) {
        if (_scope is null) {
            if (string.Equals(name.Text, ScimSchemas.SchemasAttribute.Name, StringComparison.OrdinalIgnoreCase)) {
                return new AttributePath(null, ScimSchemas.SchemasAttribute, null);
            }
            return _type.Resolve(name.Text) ?? throw Error($"Unknown attribute '{name.Text}' at position {name.Position}.");
        }
        var subAttribute = _scope.Attribute.FindSubAttribute(name.Text)
            ?? throw Error($"'{_scope.Attribute.Name}' has no sub-attribute '{name.Text}' (position {name.Position}).");
        return new AttributePath(null, subAttribute, null);
    }

    private JsonValue? ParseValue() {
        var token = Next();
        if (token.Kind == TokenKind.String) {
            return JsonValue.Create(token.Text);
        }
        if (token.Kind == TokenKind.Word) {
            if (string.Equals(token.Text, "null", StringComparison.OrdinalIgnoreCase)) {
                return null;
            }
            if (bool.TryParse(token.Text, out var flag)) {
                return JsonValue.Create(flag);
            }
            if (decimal.TryParse(token.Text, NumberStyles.Float, CultureInfo.InvariantCulture, out var number)) {
                return JsonValue.Create(number);
            }
        }
        throw Error($"Expected a value (a string in double quotes, a number, true, false or null), found {token}.");
    }

    /// <summary>Rejects comparisons the attribute's type cannot answer, such as <c>gt</c> on a boolean.</summary>
    private static void Check(AttributePath path, CompareOperator compareOperator, JsonValue? value, string name, string operatorName) {
        var leaf = path.Leaf;
        if (leaf is null || leaf.IsComplex) {
            throw Error($"'{name}' is a complex attribute; compare one of its sub-attributes.");
        }
        var equality = compareOperator is CompareOperator.Eq or CompareOperator.Ne;
        if (value is null) {
            if (!equality) {
                throw Error($"'{operatorName}' cannot compare with null; only eq and ne can.");
            }
            return;
        }
        var kind = value.GetValueKind();
        switch (leaf.Type) {
            case AttributeType.Boolean when !equality:
                throw Error($"'{name}' is a boolean and supports only eq, ne and pr.");
            case AttributeType.Boolean when kind is not (JsonValueKind.True or JsonValueKind.False):
                throw Error($"'{name}' is a boolean; compare it with true or false.");
            case AttributeType.DateTime when compareOperator is CompareOperator.Co or CompareOperator.Sw or CompareOperator.Ew:
                throw Error($"'{name}' is a dateTime and supports only eq, ne, gt, ge, lt, le and pr.");
            case AttributeType.DateTime when !JsonNodes.TryGetInstant(value, out _):
                throw Error($"'{name}' is a dateTime; compare it with a quoted date and time such as \"2026-01-31T09:30:00Z\".");
            case AttributeType.String or AttributeType.Reference when kind != JsonValueKind.String:
                throw Error($"'{name}' is a string; compare it with a value in double quotes.");
        }
    }

    private static CompareOperator? ParseOperator(string text) {
        return text.ToUpperInvariant() switch {
            "EQ" => CompareOperator.Eq,
            "NE" => CompareOperator.Ne,
            "CO" => CompareOperator.Co,
            "SW" => CompareOperator.Sw,
            "EW" => CompareOperator.Ew,
            "GT" => CompareOperator.Gt,
            "GE" => CompareOperator.Ge,
            "LT" => CompareOperator.Lt,
            "LE" => CompareOperator.Le,
            _ => null,
        };
    }

    private static bool IsKeyword(Token token, string keyword) {
        return token.Kind == TokenKind.Word && string.Equals(token.Text, keyword, StringComparison.OrdinalIgnoreCase);
    }

    private Token Peek() {
        return _tokens[_next];
    }

    private Token Next() {
        var token = _tokens[_next];
        if (token.Kind != TokenKind.End) {
            _next++;
        }
        return token;
    }

    private Token Expect(TokenKind kind, string description) {
        var token = Next();
        if (token.Kind != kind) {
            throw Error($"Expected {description}, found {token}.");
        }
        return token;
    }
}
