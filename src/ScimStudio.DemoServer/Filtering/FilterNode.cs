using System.Text.Json.Nodes;
using ScimStudio.DemoServer.Schema;

namespace ScimStudio.DemoServer.Filtering;

internal enum CompareOperator {
    Eq,
    Ne,
    Co,
    Sw,
    Ew,
    Gt,
    Ge,
    Lt,
    Le,
}

/// <summary>
/// A filter (RFC 7644 section 3.4.2.2) bound to a schema. It tests a resource, or inside brackets an element of a
/// multi-valued attribute.
/// </summary>
internal abstract record FilterNode {
    public abstract bool Matches(JsonObject target);
}

internal sealed record AndFilter(FilterNode Left, FilterNode Right) : FilterNode {
    public override bool Matches(JsonObject target) {
        return Left.Matches(target) && Right.Matches(target);
    }
}

internal sealed record OrFilter(FilterNode Left, FilterNode Right) : FilterNode {
    public override bool Matches(JsonObject target) {
        return Left.Matches(target) || Right.Matches(target);
    }
}

internal sealed record NotFilter(FilterNode Inner) : FilterNode {
    public override bool Matches(JsonObject target) {
        return !Inner.Matches(target);
    }
}

internal sealed record PresentFilter(AttributePath Path) : FilterNode {
    public override bool Matches(JsonObject target) {
        return Path.IsPresent(target);
    }
}

/// <summary><c>emails[type eq "work" and value co "@example.com"]</c>: one element has to match the whole inner filter.</summary>
internal sealed record ValuePathFilter(AttributePath Path, FilterNode Inner) : FilterNode {
    public override bool Matches(JsonObject target) {
        return JsonNodes.Items(Path.Node(target)).OfType<JsonObject>().Any(Inner.Matches);
    }
}

/// <summary>
/// <c>attrPath op value</c>. The parser has checked that the operator and the value suit the attribute's type, and a null
/// <see cref="Value"/> is the literal <c>null</c>.
/// </summary>
internal sealed record CompareFilter(AttributePath Path, CompareOperator Operator, JsonValue? Value) : FilterNode {
    public override bool Matches(JsonObject target) {
        if (Value is null) {
            // null and unassigned are the same state (RFC 7643 section 2.5)
            return Operator == CompareOperator.Eq ? !Path.IsPresent(target) : Path.IsPresent(target);
        }
        // `ne` holds when no value is equal, so a user whose emails include x does not match `emails ne "x"`
        if (Operator == CompareOperator.Ne) {
            return !Path.Values(target).Any(value => Compare(value, CompareOperator.Eq));
        }
        return Path.Values(target).Any(value => Compare(value, Operator));
    }

    private bool Compare(JsonNode actual, CompareOperator compareOperator) {
        var leaf = Path.Leaf!;
        var expected = Value!;
        switch (leaf.Type) {
            case AttributeType.Boolean:
                return actual.GetValueKind() == expected.GetValueKind();
            case AttributeType.DateTime:
                return JsonNodes.TryGetInstant(actual, out var instant) && JsonNodes.TryGetInstant(expected, out var other)
                    && Order(instant.CompareTo(other), compareOperator);
            default:
                if (JsonNodes.AsString(actual) is not { } text) {
                    return false;
                }
                var comparison = leaf.Comparison;
                var operand = expected.GetValue<string>();
                return compareOperator switch {
                    CompareOperator.Co => text.Contains(operand, comparison),
                    CompareOperator.Sw => text.StartsWith(operand, comparison),
                    CompareOperator.Ew => text.EndsWith(operand, comparison),
                    _ => Order(string.Compare(text, operand, comparison), compareOperator),
                };
        }
    }

    private static bool Order(int order, CompareOperator compareOperator) {
        return compareOperator switch {
            CompareOperator.Eq => order == 0,
            CompareOperator.Gt => order > 0,
            CompareOperator.Ge => order >= 0,
            CompareOperator.Lt => order < 0,
            CompareOperator.Le => order <= 0,
            _ => false,
        };
    }
}
