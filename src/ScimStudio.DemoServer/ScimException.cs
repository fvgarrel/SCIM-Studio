namespace ScimStudio.DemoServer;

/// <summary>A request the server refuses, carrying what the SCIM error response (RFC 7644 section 3.12) reports.</summary>
internal sealed class ScimException(int status, string? scimType, string detail) : Exception(detail) {
    public const string INVALID_FILTER = "invalidFilter";
    public const string INVALID_SYNTAX = "invalidSyntax";
    public const string INVALID_PATH = "invalidPath";
    public const string NO_TARGET = "noTarget";
    public const string INVALID_VALUE = "invalidValue";
    public const string MUTABILITY = "mutability";
    public const string UNIQUENESS = "uniqueness";

    public int Status { get; } = status;

    /// <summary>The <c>scimType</c> of the error; null where RFC 7644 defines none.</summary>
    public string? ScimType { get; } = scimType;

    public static ScimException BadRequest(string scimType, string detail) {
        return new ScimException(400, scimType, detail);
    }

    public static ScimException NotFound(string detail) {
        return new ScimException(404, null, detail);
    }

    public static ScimException Conflict(string detail) {
        return new ScimException(409, UNIQUENESS, detail);
    }

    /// <summary>A write whose If-Match names another version than the resource has (RFC 7644 section 3.14).</summary>
    /// <param name="detail">What the client is told.</param>
    public static ScimException PreconditionFailed(string detail) {
        return new ScimException(412, null, detail);
    }
}
