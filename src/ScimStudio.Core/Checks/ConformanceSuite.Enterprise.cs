using System.Text.Json.Nodes;
using ScimStudio.Core.Scim;
using ScimStudio.Core.Text;
using static ScimStudio.Core.Checks.CheckContext;

namespace ScimStudio.Core.Checks;

/// <summary>
/// The enterprise user extension (RFC 7643 section 4.3) - department, employee number, manager - which Entra ID and Okta map by default.
/// Optional: a server whose User resource type does not list it does not offer it.
/// </summary>
public sealed partial class ConformanceSuite {
    private const string DEPARTMENT = "Conformance";

    private Task ExtensionDeclaredAsync(CheckContext c) {
        var users = _resourceTypes.First(t => string.Equals(t.Endpoint, "/Users", StringComparison.OrdinalIgnoreCase));
        if (!users.SchemaExtensions.Contains(ScimSchemas.ENTERPRISE_USER, StringComparer.OrdinalIgnoreCase)) {
            Unsupported("note.extensionMissing", ScimSchemas.ENTERPRISE_USER);
        }

        if (_schemaIds.Count > 0 && !_schemaIds.Contains(ScimSchemas.ENTERPRISE_USER)) {
            c.Warn(Message.Of("note.schema", ScimSchemas.ENTERPRISE_USER));
        }

        return Task.CompletedTask;
    }

    private async Task ExtensionCreateAsync(CheckContext c) {
        var spare = await SpareAsync(c, "enterprise", body => {
            body["schemas"] = new JsonArray(ScimSchemas.USER, ScimSchemas.ENTERPRISE_USER);
            body[ScimSchemas.ENTERPRISE_USER] = new JsonObject { ["employeeNumber"] = "4711", ["department"] = DEPARTMENT };
        });

        var user = await ReadUserAsync(c, spare.Id);
        Equal("employeeNumber", Enterprise(user, "employeeNumber"), "4711");
        Equal("department", Enterprise(user, "department"), DEPARTMENT);
        if (!Schemas(user.Resource).Contains(ScimSchemas.ENTERPRISE_USER)) {
            c.Warn(Message.Of("note.schemas", ScimSchemas.ENTERPRISE_USER));
        }
    }

    /// <summary>
    /// An extension attribute is written by its full name; RFC 7644 section 3.5.2 has the extension's URN join the resource's schemas as it
    /// does.
    /// </summary>
    /// <param name="c">The check.</param>
    private async Task ExtensionWriteAsync(CheckContext c) {
        RequirePatch();
        await PatchAsync(c, ScimClient.UserPath(Alice.Id), ScimPatch.Operation("add", $"{ScimSchemas.ENTERPRISE_USER}:department", DEPARTMENT));

        var user = await ReadUserAsync(c, Alice.Id);
        Equal("department", Enterprise(user, "department"), DEPARTMENT);
        if (!Schemas(user.Resource).Contains(ScimSchemas.ENTERPRISE_USER)) {
            c.Warn(Message.Of("note.schemas", ScimSchemas.ENTERPRISE_USER));
        }
    }

    private async Task ExtensionFilterAsync(CheckContext c) {
        RequireFilter();
        await FindOnlyAsync(c, $"({Both()}) and {ScimFilterText.Eq($"{ScimSchemas.ENTERPRISE_USER}:department", DEPARTMENT)}", Alice);
    }

    private async Task ManagerAsync(CheckContext c) {
        RequirePatch();
        var value = new JsonObject { ["value"] = Bob.Id };
        await PatchAsync(c, ScimClient.UserPath(Alice.Id), ScimPatch.Operation("replace", $"{ScimSchemas.ENTERPRISE_USER}:manager", value));

        var user = await ReadUserAsync(c, Alice.Id);
        Equal("manager.value", Manager(user), Bob.Id);
    }

    private async Task RemoveManagerAsync(CheckContext c) {
        RequirePatch();
        await PatchAsync(c, ScimClient.UserPath(Alice.Id), ScimPatch.Operation("remove", $"{ScimSchemas.ENTERPRISE_USER}:manager", null));

        if (Manager(await ReadUserAsync(c, Alice.Id)) is { Length: > 0 } manager) {
            Fail("note.stillThere", "manager", manager);
        }
    }

    private static string? Manager(ScimUser user) {
        var manager = ScimJson.Get(ScimJson.Get(user.Resource, ScimSchemas.ENTERPRISE_USER), "manager");
        return ScimJson.Text(manager) ?? ScimJson.Text(ScimJson.Get(manager, "value"));
    }
}
