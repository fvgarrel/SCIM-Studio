namespace ScimStudio.DemoServer.Schema;

/// <summary>
/// The schemas the server keeps. They are what /Schemas publishes and what filters, sorting, projection and PATCH resolve names
/// against, so what the server claims and what it does cannot drift apart.
/// </summary>
internal static class ScimSchemas {
    /// <summary>
    /// <c>schemas</c>, for filters only: clients MAY query by schema (RFC 7644 section 3.4.2.2), but it belongs to no schema,
    /// and the renderer writes it rather than the store keeping it.
    /// </summary>
    public static readonly ScimAttribute SchemasAttribute = new() {
        Name = "schemas",
        Type = AttributeType.Reference,
        MultiValued = true,
        Description = "The schemas the resource conforms to.",
        Mutability = Mutability.ReadOnly,
        Returned = Returned.Always,
    };

    /// <summary>
    /// The attributes every resource carries (RFC 7643 section 3.1). They belong to no schema, so /Schemas leaves them out.
    /// </summary>
    public static readonly IReadOnlyList<ScimAttribute> Common = [
        new() {
            Name = "id",
            Description = "Identifier the service provider assigned to the resource.",
            CaseExact = true,
            Mutability = Mutability.ReadOnly,
            Returned = Returned.Always,
            Uniqueness = Uniqueness.Server,
        },
        new() { Name = "externalId", Description = "Identifier the provisioning client knows the resource by.", CaseExact = true },
        new() {
            Name = "meta",
            Type = AttributeType.Complex,
            Description = "Metadata the service provider keeps about the resource.",
            Mutability = Mutability.ReadOnly,
            SubAttributes = [
                new() {
                    Name = "resourceType",
                    Description = "Name of the resource type.",
                    CaseExact = true,
                    Mutability = Mutability.ReadOnly,
                },
                new() {
                    Name = "created",
                    Type = AttributeType.DateTime,
                    Description = "When the resource was added.",
                    Mutability = Mutability.ReadOnly,
                },
                new() {
                    Name = "lastModified",
                    Type = AttributeType.DateTime,
                    Description = "When the resource last changed.",
                    Mutability = Mutability.ReadOnly,
                },
                new() {
                    Name = "location",
                    Type = AttributeType.Reference,
                    ReferenceTypes = ["uri"],
                    Description = "URI of the resource.",
                    CaseExact = true,
                    Mutability = Mutability.ReadOnly,
                },
                new() {
                    Name = "version",
                    Description = "Version of the resource, the same as its ETag.",
                    CaseExact = true,
                    Mutability = Mutability.ReadOnly,
                },
            ],
        },
    ];

    public static readonly ScimSchema User = new() {
        Id = ScimUrns.USER,
        Name = "User",
        Description = "User Account",
        Attributes = [
            new() {
                Name = "userName",
                Description = "Unique identifier of the user, typically what the user signs in with.",
                Required = true,
                Uniqueness = Uniqueness.Server,
            },
            new() {
                Name = "name",
                Type = AttributeType.Complex,
                Description = "Components of the user's name.",
                SubAttributes = [
                    new() { Name = "formatted", Description = "Full name, formatted for display." },
                    new() { Name = "familyName", Description = "Family name, the last name in most Western languages." },
                    new() { Name = "givenName", Description = "Given name, the first name in most Western languages." },
                    new() { Name = "middleName", Description = "Middle names." },
                    new() { Name = "honorificPrefix", Description = "Honorific prefix or title, such as \"Dr.\"." },
                    new() { Name = "honorificSuffix", Description = "Honorific suffix, such as \"Jr.\"." },
                ],
            },
            new() { Name = "displayName", Description = "Name of the user, suitable for display." },
            new() { Name = "nickName", Description = "Casual name of the user." },
            new() { Name = "title", Description = "Job title, such as \"Vice President\"." },
            new() { Name = "userType", Description = "Relation of the user to the organization, such as \"Employee\" or \"Contractor\"." },
            new() { Name = "preferredLanguage", Description = "Preferred written or spoken language, as in an Accept-Language header." },
            new() { Name = "locale", Description = "Location for formatting currencies, dates and numbers, such as \"en-US\"." },
            new() { Name = "timezone", Description = "Time zone in IANA format, such as \"Europe/Berlin\"." },
            new() { Name = "active", Type = AttributeType.Boolean, Description = "Whether the user may sign in." },
            new() {
                Name = "emails",
                Type = AttributeType.Complex,
                MultiValued = true,
                Description = "Email addresses of the user.",
                SubAttributes = [
                    new() { Name = "value", Description = "Email address." },
                    new() { Name = "display", Description = "Email address as displayed." },
                    new() { Name = "type", Description = "Kind of address.", CanonicalValues = ["work", "home", "other"] },
                    new() { Name = "primary", Type = AttributeType.Boolean, Description = "Whether this is the preferred address." },
                ],
            },
            new() {
                Name = "phoneNumbers",
                Type = AttributeType.Complex,
                MultiValued = true,
                Description = "Phone numbers of the user.",
                SubAttributes = [
                    new() { Name = "value", Description = "Phone number." },
                    new() {
                        Name = "type",
                        Description = "Kind of phone number.",
                        CanonicalValues = ["work", "home", "mobile", "fax", "pager", "other"],
                    },
                    new() { Name = "primary", Type = AttributeType.Boolean, Description = "Whether this is the preferred number." },
                ],
            },
            new() {
                Name = "groups",
                Type = AttributeType.Complex,
                MultiValued = true,
                Description = "Groups the user is a member of. The server derives them from the groups' members.",
                Mutability = Mutability.ReadOnly,
                SubAttributes = [
                    new() { Name = "value", Description = "Identifier of the group.", CaseExact = true, Mutability = Mutability.ReadOnly },
                    new() {
                        Name = "$ref",
                        Type = AttributeType.Reference,
                        ReferenceTypes = ["User", "Group"],
                        Description = "URI of the group.",
                        CaseExact = true,
                        Mutability = Mutability.ReadOnly,
                    },
                    new() { Name = "display", Description = "Name of the group.", Mutability = Mutability.ReadOnly },
                    new() {
                        Name = "type",
                        Description = "Whether the user belongs to the group directly or through another group.",
                        CanonicalValues = ["direct", "indirect"],
                        Mutability = Mutability.ReadOnly,
                    },
                ],
            },
        ],
    };

    public static readonly ScimSchema EnterpriseUser = new() {
        Id = ScimUrns.ENTERPRISE_USER,
        Name = "EnterpriseUser",
        Description = "Enterprise User",
        Attributes = [
            new() { Name = "employeeNumber", Description = "Identifier the organization assigned to the user." },
            new() { Name = "costCenter", Description = "Name of a cost center." },
            new() { Name = "organization", Description = "Name of an organization." },
            new() { Name = "division", Description = "Name of a division." },
            new() { Name = "department", Description = "Name of a department." },
            new() {
                Name = "manager",
                Type = AttributeType.Complex,
                Description = "The user's manager.",
                SubAttributes = [
                    new() { Name = "value", Description = "Identifier of the manager's user resource.", CaseExact = true },
                    new() {
                        Name = "$ref",
                        Type = AttributeType.Reference,
                        ReferenceTypes = ["User"],
                        Description = "URI of the manager's user resource.",
                        CaseExact = true,
                    },
                    new() { Name = "displayName", Description = "Display name of the manager.", Mutability = Mutability.ReadOnly },
                ],
            },
        ],
    };

    public static readonly ScimSchema Group = new() {
        Id = ScimUrns.GROUP,
        Name = "Group",
        Description = "Group",
        Attributes = [
            new() { Name = "displayName", Description = "Name of the group.", Required = true, Uniqueness = Uniqueness.Server },
            new() {
                Name = "members",
                Type = AttributeType.Complex,
                MultiValued = true,
                Description = "Members of the group.",
                SubAttributes = [
                    new() {
                        Name = "value",
                        Description = "Identifier of the member.",
                        CaseExact = true,
                        Mutability = Mutability.Immutable,
                    },
                    new() {
                        Name = "$ref",
                        Type = AttributeType.Reference,
                        ReferenceTypes = ["User", "Group"],
                        Description = "URI of the member.",
                        CaseExact = true,
                        Mutability = Mutability.Immutable,
                    },
                    new() { Name = "display", Description = "Name of the member.", Mutability = Mutability.ReadOnly },
                    new() {
                        Name = "type",
                        Description = "Kind of member.",
                        CanonicalValues = ["User", "Group"],
                        Mutability = Mutability.Immutable,
                    },
                ],
            },
        ],
    };

    public static readonly IReadOnlyList<ScimSchema> All = [User, EnterpriseUser, Group];
}
