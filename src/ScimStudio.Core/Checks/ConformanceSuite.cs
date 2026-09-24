using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using ScimStudio.Core.Http;
using ScimStudio.Core.Scim;
using ScimStudio.Core.Text;

namespace ScimStudio.Core.Checks;

/// <summary>
/// Checks a service provider against RFC 7643 and RFC 7644, and against what Entra ID and Okta send, one step after another: each check stands
/// on what the earlier ones created and is skipped when one it stands on did not pass - or marked unsupported, when that one was. Everything
/// it creates carries a random marker in its name and is deleted at the end, whatever happened on the way - a cancelled run included.
/// </summary>
public sealed partial class ConformanceSuite {
    public const string DISCOVERY = "discovery";
    public const string AUTH = "auth";
    public const string USERS = "users";
    public const string FILTER = "filter";
    public const string LISTING = "listing";
    public const string GROUPS = "groups";
    public const string ENTERPRISE = "enterprise";
    public const string COMPAT = "compat";
    public const string PROTOCOL = "protocol";
    public const string OPTIONAL = "optional";
    public const string CLEANUP = "cleanup";

    private const string CONFIG = "discovery.config";
    private const string RESOURCE_TYPES = "discovery.resourceTypes";
    private const string SCHEMAS = "discovery.schemas";
    private const string CREATE_USERS = "users.create";
    private const string CREATE_GROUP = "groups.create";
    private const string CREATE_EMPTY = "groups.createEmpty";
    private const string EXTENSION = "enterprise.declared";
    private const string ETAG = "optional.etag";
    private const string BULK = "optional.bulk";
    private const string ENTRA = "Microsoft Entra ID";
    private const string OKTA = "Okta";

    private static readonly IReadOnlyList<Step> Steps = [
        new(new(CONFIG, DISCOVERY, "RFC 7644 §4"), [], (s, c) => s.ConfigAsync(c)),
        new(new("discovery.configComplete", DISCOVERY, "RFC 7643 §5"), [CONFIG], (s, c) => s.ConfigCompleteAsync(c)),
        new(new(RESOURCE_TYPES, DISCOVERY, "RFC 7644 §4"), [], (s, c) => s.ResourceTypesAsync(c)),
        new(new("discovery.resourceType", DISCOVERY, "RFC 7644 §4"), [RESOURCE_TYPES], (s, c) => s.ResourceTypeAsync(c)),
        new(new(SCHEMAS, DISCOVERY, "RFC 7644 §4"), [], (s, c) => s.SchemasAsync(c)),
        new(new("discovery.schema", DISCOVERY, "RFC 7644 §4"), [SCHEMAS], (_, c) => SchemaAsync(c)),
        new(new("discovery.userName", DISCOVERY, "RFC 7643 §4.1.1"), [SCHEMAS], (s, c) => s.UserNameDefinitionAsync(c)),
        new(new("discovery.filterRefused", DISCOVERY, "RFC 7644 §4"), [], (_, c) => FilterRefusedAsync(c)),

        new(new("auth.missingToken", AUTH, "RFC 7644 §2"), [], (_, c) => MissingTokenAsync(c)),
        new(new("auth.wrongToken", AUTH, "RFC 7644 §2"), [], (s, c) => s.WrongTokenAsync(c)),
        new(new("auth.wrongScheme", AUTH, "RFC 7644 §2"), [], (s, c) => s.WrongSchemeAsync(c)),
        new(new("auth.schemeCase", AUTH, "RFC 7235 §2.1"), [], (_, c) => SchemeCaseAsync(c)),

        new(new(CREATE_USERS, USERS, "RFC 7644 §3.3"), [], (s, c) => s.CreateUsersAsync(c)),
        new(new("users.get", USERS, "RFC 7644 §3.4.1"), [CREATE_USERS], (s, c) => s.GetUserAsync(c)),
        new(new("users.representation", USERS, "RFC 7643 §3.1"), [CREATE_USERS], (s, c) => s.UserRepresentationAsync(c)),
        new(new("users.duplicate", USERS, "RFC 7644 §3.3"), [CREATE_USERS], (s, c) => s.DuplicateUserAsync(c)),
        new(new("users.duplicateCase", USERS, "RFC 7643 §4.1.1"), [CREATE_USERS], (s, c) => s.DuplicateCaseAsync(c)),
        new(new("users.invalid", USERS, "RFC 7644 §3.12"), [], (s, c) => s.InvalidUserAsync(c)),
        new(new("users.emptyUserName", USERS, "RFC 7643 §4.1.1"), [], (s, c) => s.EmptyUserNameAsync(c)),
        new(new("users.invalidJson", USERS, "RFC 7644 §3.12"), [], (s, c) => s.InvalidJsonAsync(c)),
        new(new("users.wrongType", USERS, "RFC 7644 §3.12"), [], (s, c) => s.WrongTypeAsync(c)),
        new(new("users.readOnlyIgnored", USERS, "RFC 7644 §3.3"), [], (s, c) => s.ReadOnlyIgnoredAsync(c)),
        new(new("users.unknown", USERS, "RFC 7644 §3.4.1"), [], (_, c) => UnknownUserAsync(c)),
        new(new("users.putUnknown", USERS, "RFC 7644 §3.5.1"), [], (s, c) => s.PutUnknownAsync(c)),
        new(new("users.patchUnknown", USERS, "RFC 7644 §3.5.2"), [], (s, c) => s.PatchUnknownAsync(c)),

        new(new("filter.eq", FILTER, "RFC 7644 §3.4.2.2"), [CREATE_USERS], (s, c) => s.FilterEqAsync(c)),
        new(new("filter.caseInsensitive", FILTER, "RFC 7643 §2.2"), [CREATE_USERS], (s, c) => s.FilterCaseAsync(c)),
        new(new("filter.externalId", FILTER, "RFC 7644 §3.4.2.2"), [CREATE_USERS], (s, c) => s.FilterExternalIdAsync(c)),
        new(new("filter.subAttribute", FILTER, "RFC 7644 §3.4.2.2"), [CREATE_USERS], (s, c) => s.FilterSubAttributeAsync(c)),
        new(new("filter.logical", FILTER, "RFC 7644 §3.4.2.2"), [CREATE_USERS], (s, c) => s.FilterLogicalAsync(c)),
        new(new("filter.co", FILTER, "RFC 7644 §3.4.2.2"), [CREATE_USERS], (s, c) => s.FilterContainsAsync(c)),
        new(new("filter.sw", FILTER, "RFC 7644 §3.4.2.2"), [CREATE_USERS], (s, c) => s.FilterStartsWithAsync(c)),
        new(new("filter.ew", FILTER, "RFC 7644 §3.4.2.2"), [CREATE_USERS], (s, c) => s.FilterEndsWithAsync(c)),
        new(new("filter.ne", FILTER, "RFC 7644 §3.4.2.2"), [CREATE_USERS], (s, c) => s.FilterNotEqualAsync(c)),
        new(new("filter.pr", FILTER, "RFC 7644 §3.4.2.2"), [CREATE_USERS], (s, c) => s.FilterPresentAsync(c)),
        new(new("filter.not", FILTER, "RFC 7644 §3.4.2.2"), [CREATE_USERS], (s, c) => s.FilterNotAsync(c)),
        new(new("filter.precedence", FILTER, "RFC 7644 §3.4.2.2"), [CREATE_USERS], (s, c) => s.FilterPrecedenceAsync(c)),
        new(new("filter.dateTime", FILTER, "RFC 7644 §3.4.2.2"), [CREATE_USERS], (s, c) => s.FilterDateTimeAsync(c)),
        new(new("filter.valuePath", FILTER, "RFC 7644 §3.4.2.2"), [CREATE_USERS], (s, c) => s.FilterValuePathAsync(c)),
        new(new("filter.urn", FILTER, "RFC 7644 §3.10"), [CREATE_USERS], (s, c) => s.FilterUrnAsync(c)),
        new(new("filter.operatorCase", FILTER, "RFC 7644 §3.4.2.2"), [CREATE_USERS], (s, c) => s.FilterOperatorCaseAsync(c)),
        new(new("filter.attributeCase", FILTER, "RFC 7644 §3.4.2.2"), [CREATE_USERS], (s, c) => s.FilterAttributeCaseAsync(c)),
        new(new("filter.caseExact", FILTER, "RFC 7643 §3.1"), [CREATE_USERS], (s, c) => s.FilterCaseExactAsync(c)),
        new(new("filter.schemas", FILTER, "RFC 7644 §3.4.2.2"), [CREATE_USERS], (s, c) => s.FilterSchemasAsync(c)),
        new(new("filter.noMatch", FILTER, "RFC 7644 §3.4.2"), [], (s, c) => s.FilterNoMatchAsync(c)),
        new(new("filter.invalid", FILTER, "RFC 7644 §3.12"), [], (s, c) => s.FilterInvalidAsync(c)),
        new(new("filter.unknownOperator", FILTER, "RFC 7644 §3.4.2.2"), [], (s, c) => s.FilterUnknownOperatorAsync(c)),
        new(new("filter.unknownAttribute", FILTER, "RFC 7644 §3.4.2.2"), [CREATE_USERS], (s, c) => s.FilterUnknownAttributeAsync(c)),
        new(new("filter.booleanOrder", FILTER, "RFC 7644 §3.4.2.2"), [CREATE_USERS], (s, c) => s.FilterBooleanOrderAsync(c)),
        new(new("filter.search", FILTER, "RFC 7644 §3.4.3"), [CREATE_USERS], (s, c) => s.SearchAsync(c)),
        new(new("filter.searchRoot", FILTER, "RFC 7644 §3.4.3"), [CREATE_USERS], (s, c) => s.SearchRootAsync(c)),

        new(new("listing.unfiltered", LISTING, "RFC 7644 §3.4.2"), [CREATE_USERS], (_, c) => UnfilteredAsync(c)),
        new(new("listing.listResponse", LISTING, "RFC 7644 §3.4.2"), [CREATE_USERS], (s, c) => s.ListResponseAsync(c)),
        new(new("listing.paging", LISTING, "RFC 7644 §3.4.2.4"), [CREATE_USERS], (s, c) => s.PagingAsync(c)),
        new(new("listing.countZero", LISTING, "RFC 7644 §3.4.2.4"), [CREATE_USERS], (s, c) => s.CountZeroAsync(c)),
        new(new("listing.startIndexZero", LISTING, "RFC 7644 §3.4.2.4"), [CREATE_USERS], (s, c) => s.StartIndexZeroAsync(c)),
        new(new("listing.startIndexBeyond", LISTING, "RFC 7644 §3.4.2.4"), [CREATE_USERS], (s, c) => s.StartIndexBeyondAsync(c)),
        new(new("listing.negativeCount", LISTING, "RFC 7644 §3.4.2.4"), [CREATE_USERS], (s, c) => s.NegativeCountAsync(c)),
        new(new("listing.largeCount", LISTING, "RFC 7644 §3.4.2.4"), [CREATE_USERS], (s, c) => s.LargeCountAsync(c)),
        new(new("listing.sort", LISTING, "RFC 7644 §3.4.2.3"), [CREATE_USERS], (s, c) => s.SortAsync(c)),
        new(new("listing.sortAscending", LISTING, "RFC 7644 §3.4.2.3"), [CREATE_USERS], (s, c) => s.SortAscendingAsync(c)),
        new(new("listing.attributes", LISTING, "RFC 7644 §3.9"), [CREATE_USERS], (s, c) => s.AttributesAsync(c)),
        new(new("listing.attributesList", LISTING, "RFC 7644 §3.9"), [CREATE_USERS], (s, c) => s.AttributesListAsync(c)),
        new(new("listing.attributesSub", LISTING, "RFC 7644 §3.9"), [CREATE_USERS], (s, c) => s.AttributesSubAsync(c)),
        new(new("listing.attributesUrn", LISTING, "RFC 7644 §3.10"), [CREATE_USERS], (s, c) => s.AttributesUrnAsync(c)),
        new(new("listing.excludedAttributes", LISTING, "RFC 7644 §3.9"), [CREATE_USERS], (s, c) => s.ExcludedAttributesAsync(c)),
        new(new("listing.excludedAlways", LISTING, "RFC 7643 §2.2"), [CREATE_USERS], (s, c) => s.ExcludedAlwaysAsync(c)),

        new(new("users.patchReplace", USERS, "RFC 7644 §3.5.2.3"), [CREATE_USERS], (s, c) => s.PatchReplaceAsync(c)),
        new(new("users.patchNoPath", USERS, "RFC 7644 §3.5.2.3"), [CREATE_USERS], (s, c) => s.PatchNoPathAsync(c)),
        new(new("users.patchFilter", USERS, "RFC 7644 §3.5.2"), [CREATE_USERS], (s, c) => s.PatchFilterAsync(c)),
        new(new("users.patchRemove", USERS, "RFC 7644 §3.5.2.2"), [CREATE_USERS], (s, c) => s.PatchRemoveAsync(c)),
        new(new("users.patchAdd", USERS, "RFC 7644 §3.5.2.1"), [CREATE_USERS], (s, c) => s.PatchAddAsync(c)),
        new(new("users.patchUnknownAttribute", USERS, "RFC 7644 §3.5.2"), [CREATE_USERS], (s, c) => s.PatchUnknownAttributeAsync(c)),
        new(new("users.patchAddValue", USERS, "RFC 7644 §3.5.2.1"), [CREATE_USERS], (s, c) => s.PatchAddValueAsync(c)),
        new(new("users.patchComplex", USERS, "RFC 7644 §3.5.2.3"), [CREATE_USERS], (s, c) => s.PatchComplexAsync(c)),
        new(new("users.patchReplaceAll", USERS, "RFC 7644 §3.5.2.3"), [CREATE_USERS], (s, c) => s.PatchReplaceAllAsync(c)),
        new(new("users.patchRemoveValue", USERS, "RFC 7644 §3.5.2.2"), [CREATE_USERS], (s, c) => s.PatchRemoveValueAsync(c)),
        new(new("users.patchPrimary", USERS, "RFC 7644 §3.5.2"), [CREATE_USERS], (s, c) => s.PatchPrimaryAsync(c)),
        new(new("users.patchReplaceNoMatch", USERS, "RFC 7644 §3.5.2.3"), [CREATE_USERS], (s, c) => s.PatchReplaceNoMatchAsync(c)),
        new(new("users.patchUrnPath", USERS, "RFC 7644 §3.10"), [CREATE_USERS], (s, c) => s.PatchUrnPathAsync(c)),
        new(new("users.patchAttributes", USERS, "RFC 7644 §3.5.2"), [CREATE_USERS], (s, c) => s.PatchAttributesAsync(c)),
        new(new("users.patchAtomic", USERS, "RFC 7644 §3.5.2"), [CREATE_USERS], (s, c) => s.PatchAtomicAsync(c)),
        new(new("users.patchNoTarget", USERS, "RFC 7644 §3.5.2.2"), [CREATE_USERS], (s, c) => s.PatchNoTargetAsync(c)),
        new(new("users.patchInvalidOp", USERS, "RFC 7644 §3.5.2"), [CREATE_USERS], (s, c) => s.PatchInvalidOpAsync(c)),
        new(new("users.patchInvalidPath", USERS, "RFC 7644 §3.5.2"), [CREATE_USERS], (s, c) => s.PatchInvalidPathAsync(c)),
        new(new("users.patchReadOnly", USERS, "RFC 7644 §3.5.2"), [CREATE_USERS], (s, c) => s.PatchReadOnlyAsync(c)),
        new(new("users.patchRemoveRequired", USERS, "RFC 7644 §3.5.2.2"), [CREATE_USERS], (s, c) => s.PatchRemoveRequiredAsync(c)),
        new(new("users.patchEmptyOperations", USERS, "RFC 7644 §3.5.2"), [CREATE_USERS], (s, c) => s.PatchEmptyOperationsAsync(c)),
        new(new("users.deactivate", USERS, "RFC 7643 §4.1.1"), [CREATE_USERS], (s, c) => s.SetActiveAsync(c, false)),
        new(new("users.reactivate", USERS, "RFC 7643 §4.1.1"), [CREATE_USERS], (s, c) => s.SetActiveAsync(c, true)),
        new(new("users.userNameChange", USERS, "RFC 7643 §4.1.1"), [CREATE_USERS], (s, c) => s.UserNameChangeAsync(c)),
        new(new("users.userNameConflict", USERS, "RFC 7644 §3.5.2"), [CREATE_USERS], (s, c) => s.UserNameConflictAsync(c)),
        new(new("users.externalIdChange", USERS, "RFC 7643 §3.1"), [CREATE_USERS], (s, c) => s.ExternalIdChangeAsync(c)),
        new(new("users.put", USERS, "RFC 7644 §3.5.1"), [CREATE_USERS], (s, c) => s.PutUserAsync(c)),
        new(new("users.putReadOnly", USERS, "RFC 7644 §3.5.1"), [CREATE_USERS], (s, c) => s.PutReadOnlyAsync(c)),
        new(new("users.putRequired", USERS, "RFC 7644 §3.5.1"), [CREATE_USERS], (s, c) => s.PutRequiredAsync(c)),
        new(new("users.putAttributes", USERS, "RFC 7644 §3.9"), [CREATE_USERS], (s, c) => s.PutAttributesAsync(c)),
        new(new("users.password", USERS, "RFC 7643 §4.1.1"), [CREATE_USERS], (s, c) => s.PasswordAsync(c)),

        new(new(CREATE_GROUP, GROUPS, "RFC 7644 §3.3"), [CREATE_USERS], (s, c) => s.CreateGroupAsync(c)),
        new(new("groups.representation", GROUPS, "RFC 7643 §3.1"), [CREATE_GROUP], (s, c) => s.GroupRepresentationAsync(c)),
        new(new("groups.get", GROUPS, "RFC 7644 §3.4.1"), [CREATE_GROUP], (s, c) => s.GetGroupAsync(c)),
        new(new("groups.filter", GROUPS, "RFC 7644 §3.4.2.2"), [CREATE_GROUP], (s, c) => s.FilterGroupAsync(c)),
        new(new("groups.byMember", GROUPS, "RFC 7644 §3.4.2.2"), [CREATE_GROUP], (s, c) => s.GroupsByMemberAsync(c)),
        new(new("groups.search", GROUPS, "RFC 7644 §3.4.3"), [CREATE_GROUP], (s, c) => s.SearchGroupsAsync(c)),
        new(new("groups.unknown", GROUPS, "RFC 7644 §3.4.1"), [], (_, c) => UnknownGroupAsync(c)),
        new(new("groups.patchUnknown", GROUPS, "RFC 7644 §3.5.2"), [], (s, c) => s.PatchUnknownGroupAsync(c)),
        new(new("groups.createInvalid", GROUPS, "RFC 7643 §4.2"), [], (s, c) => s.CreateInvalidGroupAsync(c)),
        new(new(CREATE_EMPTY, GROUPS, "RFC 7643 §4.2"), [CREATE_USERS], (s, c) => s.CreateEmptyGroupAsync(c)),
        new(new("groups.addMember", GROUPS, "RFC 7644 §3.5.2.1"), [CREATE_GROUP], (s, c) => s.AddMemberAsync(c)),
        new(new("groups.addMembers", GROUPS, "RFC 7644 §3.5.2.1"), [CREATE_EMPTY], (s, c) => s.AddMembersAsync(c)),
        new(new("groups.addDuplicateMember", GROUPS, "RFC 7644 §3.5.2.1"), [CREATE_EMPTY], (s, c) => s.AddDuplicateMemberAsync(c)),
        new(new("groups.memberAttributes", GROUPS, "RFC 7643 §4.2"), [CREATE_GROUP], (s, c) => s.MemberAttributesAsync(c)),
        new(new("groups.removeMember", GROUPS, "RFC 7644 §3.5.2.2"), [CREATE_GROUP], (s, c) => s.RemoveMemberAsync(c)),
        new(new("groups.userGroupsAfterRemove", GROUPS, "RFC 7643 §4.1.2"), [CREATE_GROUP], (s, c) => s.UserGroupsAfterRemoveAsync(c)),
        new(new("groups.removeNonMember", GROUPS, "RFC 7644 §3.5.2.2"), [CREATE_GROUP], (s, c) => s.RemoveNonMemberAsync(c)),
        new(new("groups.replaceMembers", GROUPS, "RFC 7644 §3.5.2.3"), [CREATE_EMPTY], (s, c) => s.ReplaceMembersAsync(c)),
        new(new("groups.removeAllMembers", GROUPS, "RFC 7644 §3.5.2.2"), [CREATE_EMPTY], (s, c) => s.RemoveAllMembersAsync(c)),
        new(new("groups.memberUnknown", GROUPS, "RFC 7644 §3.5.2.1"), [CREATE_EMPTY], (s, c) => s.MemberUnknownAsync(c)),
        new(new("groups.rename", GROUPS, "RFC 7644 §3.5.2.3"), [CREATE_GROUP], (s, c) => s.RenameGroupAsync(c)),
        new(new("groups.put", GROUPS, "RFC 7644 §3.5.1"), [CREATE_GROUP], (s, c) => s.PutGroupAsync(c)),
        new(new("groups.userGroups", GROUPS, "RFC 7643 §4.1.2"), [CREATE_GROUP], (s, c) => s.UserGroupsAsync(c)),
        new(new("groups.nested", GROUPS, "RFC 7643 §4.2"), [CREATE_GROUP, CREATE_EMPTY], (s, c) => s.NestedGroupAsync(c)),
        new(new("groups.readOnlyGroups", GROUPS, "RFC 7643 §4.1.2"), [CREATE_EMPTY], (s, c) => s.ReadOnlyGroupsAsync(c)),

        new(new(EXTENSION, ENTERPRISE, "RFC 7643 §4.3"), [RESOURCE_TYPES], (s, c) => s.ExtensionDeclaredAsync(c)),
        new(new("enterprise.create", ENTERPRISE, "RFC 7643 §4.3"), [EXTENSION, CREATE_USERS], (s, c) => s.ExtensionCreateAsync(c)),
        new(new("enterprise.write", ENTERPRISE, "RFC 7644 §3.5.2"), [EXTENSION, CREATE_USERS], (s, c) => s.ExtensionWriteAsync(c)),
        new(new("enterprise.filter", ENTERPRISE, "RFC 7644 §3.10"), ["enterprise.write"], (s, c) => s.ExtensionFilterAsync(c)),
        new(new("enterprise.manager", ENTERPRISE, "RFC 7643 §4.3"), [EXTENSION, CREATE_USERS], (s, c) => s.ManagerAsync(c)),
        new(new("enterprise.removeManager", ENTERPRISE, "RFC 7644 §3.5.2.2"), ["enterprise.manager"], (s, c) => s.RemoveManagerAsync(c)),

        new(new("compat.entraUserCreate", COMPAT, ENTRA), [CREATE_USERS], (s, c) => s.EntraUserCreateAsync(c)),
        new(new("compat.entraCasing", COMPAT, ENTRA), [CREATE_USERS], (s, c) => s.EntraCasingAsync(c)),
        new(new("compat.entraBooleans", COMPAT, ENTRA), [CREATE_USERS], (s, c) => s.EntraBooleansAsync(c)),
        new(new("compat.entraFilteredAdd", COMPAT, ENTRA), [CREATE_USERS], (s, c) => s.EntraFilteredAddAsync(c)),
        new(new("compat.entraDottedKeys", COMPAT, ENTRA), [CREATE_USERS], (s, c) => s.EntraDottedKeysAsync(c)),
        new(new("compat.entraUrnKeys", COMPAT, ENTRA), [EXTENSION, CREATE_USERS], (s, c) => s.EntraUrnKeysAsync(c)),
        new(new("compat.entraEmailFilter", COMPAT, ENTRA), [CREATE_USERS], (s, c) => s.EntraEmailFilterAsync(c)),
        new(new("compat.entraInactiveVisible", COMPAT, ENTRA), [CREATE_USERS], (s, c) => s.EntraInactiveVisibleAsync(c)),
        new(new("compat.entraRemoveByValue", COMPAT, ENTRA), [CREATE_GROUP], (s, c) => s.EntraRemoveByValueAsync(c)),
        new(new("compat.entraGroupSchema", COMPAT, ENTRA), [], (s, c) => s.EntraGroupSchemaAsync(c)),
        new(new("compat.entraGroupNoMatch", COMPAT, ENTRA), [], (s, c) => s.EntraGroupNoMatchAsync(c)),
        new(new("compat.entraGroupUniqueName", COMPAT, ENTRA), [CREATE_GROUP], (s, c) => s.EntraGroupUniqueNameAsync(c)),
        new(new("compat.oktaActiveNoPath", COMPAT, OKTA), [CREATE_USERS], (s, c) => s.OktaActiveAsync(c)),
        new(new("compat.oktaRenameWithId", COMPAT, OKTA), [CREATE_GROUP], (s, c) => s.OktaRenameAsync(c)),
        new(new("compat.oktaPutReadOnly", COMPAT, OKTA), [CREATE_USERS], (s, c) => s.OktaPutAsync(c)),

        new(new("protocol.tls", PROTOCOL, "RFC 7644 §7.2"), [], (_, c) => TlsAsync(c)),
        new(new("protocol.errorFormat", PROTOCOL, "RFC 7644 §3.12"), [], (_, c) => ErrorFormatAsync(c)),
        new(new("protocol.unknownEndpoint", PROTOCOL, "RFC 7644 §3.12"), [], (s, c) => s.UnknownEndpointAsync(c)),
        new(new("protocol.mediaType", PROTOCOL, "RFC 7644 §3.1"), [CREATE_USERS], (s, c) => s.MediaTypeAsync(c)),
        new(new("protocol.acceptScim", PROTOCOL, "RFC 7644 §3.8"), [CREATE_USERS], (s, c) => s.AcceptAsync(c, ScimSchemas.MEDIA_TYPE)),
        new(new("protocol.acceptJson", PROTOCOL, "RFC 7644 §3.8"), [CREATE_USERS], (s, c) => s.AcceptAsync(c, "application/json")),
        new(new("protocol.noAccept", PROTOCOL, "RFC 7644 §3.8"), [CREATE_USERS], (s, c) => s.AcceptAsync(c, string.Empty)),
        new(new("protocol.contentTypeJson", PROTOCOL, "RFC 7644 §3.8"), [CREATE_USERS], (s, c) => s.ContentTypeJsonAsync(c)),
        new(new("protocol.attributeCase", PROTOCOL, "RFC 7643 §2.1"), [CREATE_USERS], (s, c) => s.AttributeCaseAsync(c)),
        new(new("protocol.pathCase", PROTOCOL, "RFC 7644 §3.10"), [CREATE_USERS], (s, c) => s.PathCaseAsync(c)),
        new(new("protocol.unicode", PROTOCOL, "RFC 7644 §3.8"), [CREATE_USERS], (s, c) => s.UnicodeAsync(c)),

        new(new(ETAG, OPTIONAL, "RFC 7644 §3.14"), [CONFIG, CREATE_USERS], (s, c) => s.EtagAsync(c)),
        new(new("optional.etagNotModified", OPTIONAL, "RFC 7644 §3.14"), [ETAG], (s, c) => s.EtagNotModifiedAsync(c)),
        new(new("optional.etagPrecondition", OPTIONAL, "RFC 7644 §3.14"), [ETAG], (s, c) => s.EtagPreconditionAsync(c)),
        new(new(BULK, OPTIONAL, "RFC 7644 §3.7"), [CONFIG, CREATE_USERS], (s, c) => s.BulkAsync(c)),
        new(new("optional.bulkFailOnErrors", OPTIONAL, "RFC 7644 §3.7.3"), [BULK], (_, c) => BulkFailOnErrorsAsync(c)),
        new(new("optional.bulkMaxOperations", OPTIONAL, "RFC 7644 §3.7.4"), [BULK], (s, c) => s.BulkMaxOperationsAsync(c)),
        new(new("optional.bulkMaxPayload", OPTIONAL, "RFC 7644 §3.7.4"), [BULK], (s, c) => s.BulkMaxPayloadAsync(c)),
        new(new("optional.changePassword", OPTIONAL, "RFC 7643 §5"), [CONFIG, CREATE_USERS], (s, c) => s.ChangePasswordAsync(c)),
        new(new("optional.me", OPTIONAL, "RFC 7644 §3.11"), [], (_, c) => MeAsync(c)),

        new(new("cleanup.deleteGroup", CLEANUP, "RFC 7644 §3.6"), [CREATE_GROUP], (s, c) => s.DeleteGroupAsync(c)),
        new(new("cleanup.groupDeleteKeepsUsers", CLEANUP, "RFC 7644 §3.6"), ["cleanup.deleteGroup"], (s, c) => s.GroupDeleteKeepsUsersAsync(c)),
        new(new("cleanup.deleteUser", CLEANUP, "RFC 7644 §3.6"), [CREATE_USERS], (s, c) => s.DeleteUserAsync(c)),
        new(new("cleanup.deletedGone", CLEANUP, "RFC 7644 §3.6"), ["cleanup.deleteUser"], (s, c) => s.DeletedGoneAsync(c)),
        new(new("cleanup.userDeleteLeavesGroups", CLEANUP, "RFC 7644 §3.6"), [CREATE_EMPTY], (s, c) => s.UserDeleteLeavesGroupsAsync(c)),
        new(new("cleanup.deleteUnknown", CLEANUP, "RFC 7644 §3.6"), [], (_, c) => DeleteUnknownAsync(c)),
        new(new("cleanup.remaining", CLEANUP, "—"), [], (s, c) => s.RemainingAsync(c)),
    ];

    private readonly ScimClient _client;
    private readonly string _marker = RandomNumberGenerator.GetString("abcdefghijklmnopqrstuvwxyz0123456789", 6);
    private readonly List<string> _users = [];
    private readonly List<string> _groups = [];
    private ServiceProviderConfig? _config;
    private IReadOnlyList<ResourceTypeInfo> _resourceTypes = [];
    private HashSet<string> _schemaIds = new(StringComparer.OrdinalIgnoreCase);
    private JsonObject? _userSchema;
    private ScimUser? _alice;
    private ScimUser? _bob;
    private Uri? _aliceLocation;
    private ScimGroup? _group;
    private ScimGroup? _empty;

    private ConformanceSuite(ScimClient client) {
        _client = client;
    }

    /// <summary>Every check, in the order they run.</summary>
    public static IReadOnlyList<CheckInfo> Checks { get; } = [.. Steps.Select(s => s.Info)];

    /// <summary>The prefix every resource a run creates starts with, so one left behind can be recognised.</summary>
    public static string Prefix => "scimstudio-check-";

    /// <summary>What each check stands on, by id - for the test that holds every requirement to run before the check that needs it.</summary>
    internal static IReadOnlyList<KeyValuePair<string, IReadOnlyList<string>>> Requirements { get; } =
        [.. Steps.Select(s => new KeyValuePair<string, IReadOnlyList<string>>(s.Info.Id, s.Requires))];

    private ServiceProviderConfig Config => _config ?? throw new InvalidOperationException("No configuration.");

    private ScimUser Alice => _alice ?? throw new InvalidOperationException("No first user.");

    private ScimUser Bob => _bob ?? throw new InvalidOperationException("No second user.");

    private ScimGroup Group => _group ?? throw new InvalidOperationException("No group.");

    private ScimGroup Empty => _empty ?? throw new InvalidOperationException("No second group.");

    /// <summary>Runs every check against the client's server.</summary>
    /// <param name="client">The client.</param>
    /// <param name="progress">Told when a check starts, as running, and when it ends, with its verdict.</param>
    /// <param name="cancellationToken">Stops the run after the check under way; what was created is still removed.</param>
    public static async Task<IReadOnlyList<CheckResult>> RunAsync(
        ScimClient client, IProgress<CheckResult>? progress = null, CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(client);

        var suite = new ConformanceSuite(client);
        var results = new List<CheckResult>();
        try {
            foreach (var step in Steps) {
                cancellationToken.ThrowIfCancellationRequested();
                progress?.Report(new CheckResult { Check = step.Info, Status = CheckStatus.Running });

                var result = await suite.RunStepAsync(step, results, cancellationToken);
                results.Add(result);
                progress?.Report(result);
            }
        } finally {
            await suite.RemoveQuietlyAsync();
        }

        return results;
    }

    private async Task<CheckResult> RunStepAsync(Step step, List<CheckResult> done, CancellationToken cancellationToken) {
        foreach (var id in step.Requires) {
            var required = done.Find(r => r.Check.Id == id);
            if (required is { Status: CheckStatus.Passed or CheckStatus.Warning }) {
                continue;
            }

            // What stands on something the server does not offer is not offered either; anything else just could not be tried.
            var unsupported = required is { Status: CheckStatus.Unsupported };
            var note = Message.Of(unsupported ? "note.dependencyUnsupported" : "note.dependency", Message.Of($"check.{id}"));
            return new CheckResult { Check = step.Info, Status = unsupported ? CheckStatus.Unsupported : CheckStatus.Skipped, Notes = [note] };
        }

        var context = new CheckContext(_client, cancellationToken);
        var started = Stopwatch.GetTimestamp();
        using var scope = ExchangeScope.Begin($"check:{step.Info.Id}");

        CheckStatus status;
        try {
            await step.Run(this, context);
            status = context.Warned ? CheckStatus.Warning : CheckStatus.Passed;
        } catch (CheckStoppedException stopped) {
            context.Notes.Insert(0, stopped.Note);
            status = stopped.Status;
        } catch (ScimException failure) {
            context.Notes.Insert(0, Message.Of("note.scim", failure.Error.ToString()));
            status = CheckStatus.Failed;
        } catch (HttpRequestException failure) {
            context.Notes.Insert(0, Message.Of("note.transport", failure.InnerException?.Message ?? failure.Message));
            status = CheckStatus.Failed;
        } catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) {
            context.Notes.Insert(0, Message.Of("note.timeout"));
            status = CheckStatus.Failed;
        } catch (Exception failure) when (failure is not OperationCanceledException) {
            // Servers answer with anything; what a check did not see coming ends that check, not the run and its clean-up.
            context.Notes.Insert(0, Message.Of("note.unexpected", failure.Message));
            status = CheckStatus.Failed;
        }

        return new CheckResult {
            Check = step.Info,
            Status = status,
            Notes = context.Notes,
            Duration = Stopwatch.GetElapsedTime(started),
            Exchanges = scope.Exchanges,
        };
    }

    /// <summary>Removes what a cancelled or broken run left behind, without a check to report to.</summary>
    private async Task RemoveQuietlyAsync() {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        foreach (var path in _groups.Select(ScimClient.GroupPath).Concat(_users.Select(ScimClient.UserPath)).ToList()) {
            try {
                await _client.SendAsync(HttpMethod.Delete, path, null, timeout.Token);
            } catch (HttpRequestException) {
                // Nothing to report to any more; the marker in the name tells a person what it was.
            } catch (OperationCanceledException) {
                return;
            }
        }

        _groups.Clear();
        _users.Clear();
    }

    /// <summary>A check with what it stands on and what it does.</summary>
    /// <param name="Info">The check.</param>
    /// <param name="Requires">The checks that must have passed for it to run.</param>
    /// <param name="Run">The check itself.</param>
    private sealed record Step(CheckInfo Info, IReadOnlyList<string> Requires, Func<ConformanceSuite, CheckContext, Task> Run);
}
