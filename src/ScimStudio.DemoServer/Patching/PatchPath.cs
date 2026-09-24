using System.Text.RegularExpressions;
using ScimStudio.DemoServer.Filtering;
using ScimStudio.DemoServer.Schema;

namespace ScimStudio.DemoServer.Patching;

/// <summary>
/// The target of a PATCH operation (RFC 7644 section 3.5.2): an attribute, a sub-attribute of a single complex attribute,
/// or elements of a multi-valued attribute picked by a filter, optionally narrowed to one of their sub-attributes.
/// </summary>
/// <param name="Target">The attribute; its sub-attribute is set only for a single complex attribute (<c>name.givenName</c>).</param>
/// <param name="Filter">The filter in brackets; null picks every element.</param>
/// <param name="ElementAttribute">The sub-attribute of the picked elements (<c>emails[type eq "work"].value</c>, <c>emails.value</c>).</param>
internal sealed partial record PatchPath(AttributePath Target, FilterNode? Filter, ScimAttribute? ElementAttribute) {
    public ScimAttribute Attribute => Target.Attribute;

    /// <summary>Whether the path names the elements of a multi-valued attribute rather than the attribute as a whole.</summary>
    public bool SelectsElements => Filter is not null || ElementAttribute is not null;

    public bool IsReadOnly => Attribute.IsReadOnly || Target.SubAttribute?.IsReadOnly == true || ElementAttribute?.IsReadOnly == true;

    /// <summary>
    /// Parses a path. Null when it names an attribute the resource type does not have, which callers ignore since IdPs send
    /// whatever their mapping lists; a path that does not parse is an <c>invalidPath</c> error.
    /// </summary>
    /// <param name="type">The resource type the path belongs to.</param>
    /// <param name="path">The path as the client sent it.</param>
    public static PatchPath? Parse(ResourceType type, string path) {
        var text = path.Trim();
        var bracket = text.IndexOf('[', StringComparison.Ordinal);
        if (bracket < 0) {
            if (!AttributeSyntax().IsMatch(text)) {
                throw Invalid(path);
            }
            var resolved = type.Resolve(text);
            if (resolved?.Attribute.MultiValued == true && resolved.SubAttribute is { } elementAttribute) {
                return new PatchPath(resolved with { SubAttribute = null }, null, elementAttribute);
            }
            return resolved is null ? null : new PatchPath(resolved, null, null);
        }

        var close = ClosingBracket(text, bracket);
        var attributeText = text[..bracket];
        var rest = close < 0 ? "" : text[(close + 1)..];
        if (close < 0 || !AttributeSyntax().IsMatch(attributeText) || (rest.Length > 0 && !SubAttributeSyntax().IsMatch(rest))) {
            throw Invalid(path);
        }
        var attribute = type.Resolve(attributeText);
        if (attribute is null) {
            return null;
        }
        if (!attribute.Attribute.MultiValued || !attribute.Attribute.IsComplex || attribute.SubAttribute is not null) {
            throw ScimException.BadRequest(
                ScimException.INVALID_PATH, $"The path \"{path}\" filters '{attributeText}', which is not a multi-valued complex attribute.");
        }
        FilterNode filter;
        try {
            filter = FilterParser.ParseValueFilter(text[(bracket + 1)..close], type, attribute);
        } catch (ScimException error) {
            throw ScimException.BadRequest(ScimException.INVALID_PATH, $"The filter in the path \"{path}\" is invalid: {error.Message}");
        }
        if (rest.Length == 0) {
            return new PatchPath(attribute, filter, null);
        }
        var subAttribute = attribute.Attribute.FindSubAttribute(rest[1..]);
        return subAttribute is null ? null : new PatchPath(attribute, filter, subAttribute);
    }

    /// <summary>The <c>]</c> that closes the bracket at <paramref name="open"/>, skipping string literals; -1 when there is none.</summary>
    /// <param name="text">The path.</param>
    /// <param name="open">The position of the <c>[</c>.</param>
    private static int ClosingBracket(string text, int open) {
        var inString = false;
        for (var position = open + 1; position < text.Length; position++) {
            var character = text[position];
            if (inString && character == '\\') {
                position++;
            } else if (character == '"') {
                inString = !inString;
            } else if (!inString && character == ']') {
                return position;
            }
        }
        return -1;
    }

    private static ScimException Invalid(string path) {
        return ScimException.BadRequest(ScimException.INVALID_PATH, $"The path \"{path}\" is not a valid attribute path.");
    }

    // [schema URN:]attribute[.subAttribute]; the URN may contain dots ("2.0"), the attribute names cannot.
    [GeneratedRegex(@"^(?:urn:[a-z0-9.:_-]+:)?[a-z$][a-z0-9_$-]*(?:\.[a-z$][a-z0-9_$-]*)?$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex AttributeSyntax();

    [GeneratedRegex(@"^\.[a-z$][a-z0-9_$-]*$", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex SubAttributeSyntax();
}
