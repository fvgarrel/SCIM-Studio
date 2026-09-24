namespace ScimStudio.DemoServer.Schema;

/// <summary>The URNs RFC 7643 and RFC 7644 give schemas and protocol messages.</summary>
internal static class ScimUrns {
    public const string USER = "urn:ietf:params:scim:schemas:core:2.0:User";
    public const string ENTERPRISE_USER = "urn:ietf:params:scim:schemas:extension:enterprise:2.0:User";
    public const string GROUP = "urn:ietf:params:scim:schemas:core:2.0:Group";
    public const string SERVICE_PROVIDER_CONFIG = "urn:ietf:params:scim:schemas:core:2.0:ServiceProviderConfig";
    public const string RESOURCE_TYPE = "urn:ietf:params:scim:schemas:core:2.0:ResourceType";
    public const string SCHEMA = "urn:ietf:params:scim:schemas:core:2.0:Schema";
    public const string LIST_RESPONSE = "urn:ietf:params:scim:api:messages:2.0:ListResponse";
    public const string SEARCH_REQUEST = "urn:ietf:params:scim:api:messages:2.0:SearchRequest";
    public const string PATCH_OP = "urn:ietf:params:scim:api:messages:2.0:PatchOp";
    public const string BULK_REQUEST = "urn:ietf:params:scim:api:messages:2.0:BulkRequest";
    public const string BULK_RESPONSE = "urn:ietf:params:scim:api:messages:2.0:BulkResponse";
    public const string ERROR = "urn:ietf:params:scim:api:messages:2.0:Error";
}
