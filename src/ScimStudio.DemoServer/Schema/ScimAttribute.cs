namespace ScimStudio.DemoServer.Schema;

internal enum AttributeType {
    String,
    Boolean,
    DateTime,
    Reference,
    Complex,
}

internal enum Mutability {
    ReadOnly,
    ReadWrite,
    Immutable,
}

internal enum Returned {
    Always,
    Default,
}

internal enum Uniqueness {
    None,
    Server,
}

/// <summary>An attribute or sub-attribute of a schema, with the characteristics RFC 7643 section 7 lists.</summary>
internal sealed class ScimAttribute {
    public const string VALUE = "value";

    public required string Name { get; init; }
    public AttributeType Type { get; init; } = AttributeType.String;
    public bool MultiValued { get; init; }
    public string Description { get; init; } = "";
    public bool Required { get; init; }
    public bool CaseExact { get; init; }
    public Mutability Mutability { get; init; } = Mutability.ReadWrite;
    public Returned Returned { get; init; } = Returned.Default;
    public Uniqueness Uniqueness { get; init; } = Uniqueness.None;
    public IReadOnlyList<string> CanonicalValues { get; init; } = [];
    public IReadOnlyList<string> ReferenceTypes { get; init; } = [];
    public IReadOnlyList<ScimAttribute> SubAttributes { get; init; } = [];

    public bool IsComplex => Type == AttributeType.Complex;
    public bool IsReadOnly => Mutability == Mutability.ReadOnly;

    /// <summary>How two values of this attribute compare as strings.</summary>
    public StringComparison Comparison => CaseExact ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;

    public ScimAttribute? FindSubAttribute(string name) {
        foreach (var subAttribute in SubAttributes) {
            if (string.Equals(subAttribute.Name, name, StringComparison.OrdinalIgnoreCase)) {
                return subAttribute;
            }
        }
        return null;
    }
}
