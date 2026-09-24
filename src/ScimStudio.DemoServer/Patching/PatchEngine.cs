using System.Text.Json.Nodes;
using ScimStudio.DemoServer.Filtering;
using ScimStudio.DemoServer.Schema;

namespace ScimStudio.DemoServer.Patching;

/// <summary>
/// Applies PATCH operations (RFC 7644 section 3.5.2) to a resource in the form the store keeps. POST and PUT write through it
/// as well, as a replace of the whole resource.
/// </summary>
internal static class PatchEngine {
    private const string PRIMARY = "primary";

    /// <summary>
    /// Applies the operations in order and throws at the first that fails, leaving the resource half-changed; the store patches
    /// a copy, so a failed request changes nothing.
    /// </summary>
    /// <param name="type">The type of the resource.</param>
    /// <param name="resource">The resource in stored form.</param>
    /// <param name="operations">The operations of the request.</param>
    public static void Apply(ResourceType type, JsonObject resource, IEnumerable<PatchOperation> operations) {
        foreach (var operation in operations) {
            Apply(type, resource, operation);
        }
        JsonNodes.RemoveEmpty(resource);
    }

    /// <summary>Writes the attributes of a representation a client sent with POST or PUT.</summary>
    /// <param name="type">The type of the resource.</param>
    /// <param name="resource">The resource to write into, in stored form.</param>
    /// <param name="representation">The body of the request.</param>
    public static void Write(ResourceType type, JsonObject resource, JsonObject representation) {
        ApplyWithoutPath(type, resource, PatchOp.Replace, representation);
        JsonNodes.RemoveEmpty(resource);
    }

    private static void Apply(ResourceType type, JsonObject resource, PatchOperation operation) {
        if (string.IsNullOrWhiteSpace(operation.Path)) {
            if (operation.Op == PatchOp.Remove) {
                throw ScimException.BadRequest(ScimException.NO_TARGET, "A remove operation needs a path.");
            }
            if (operation.Value is not JsonObject values) {
                throw ScimException.BadRequest(ScimException.INVALID_VALUE, "An operation without a path needs an object as its value.");
            }
            ApplyWithoutPath(type, resource, operation.Op, values);
            return;
        }
        // An attribute the server does not keep is ignored: identity providers send whatever their mapping lists
        if (PatchPath.Parse(type, operation.Path) is not { } path) {
            return;
        }
        if (path.IsReadOnly) {
            throw ScimException.BadRequest(ScimException.MUTABILITY, $"\"{operation.Path}\" is read-only.");
        }
        Apply(resource, operation.Op, path, operation.Value);
    }

    /// <summary>
    /// Each key of the value is an attribute name or a path, since Microsoft Entra ID sends keys like "name.givenName". Keys naming
    /// read-only or unknown attributes are skipped: Okta renames a group by sending its id along with the new name.
    /// </summary>
    private static void ApplyWithoutPath(ResourceType type, JsonObject resource, PatchOp op, JsonObject values) {
        foreach (var (key, value) in values) {
            PatchPath? path;
            try {
                path = PatchPath.Parse(type, key);
            } catch (ScimException) {
                continue;
            }
            if (path is not null && !path.IsReadOnly) {
                Apply(resource, op, path, value);
            }
        }
    }

    private static void Apply(JsonObject resource, PatchOp op, PatchPath path, JsonNode? value) {
        if (path.Attribute.MultiValued && path.Attribute.IsComplex) {
            if (path.SelectsElements) {
                ApplyToElements(resource, op, path, value);
            } else {
                ApplyToList(resource, op, path.Target, value);
            }
        } else if (path.Target.SubAttribute is { } subAttribute) {
            ApplyToSubAttribute(resource, op, path.Target, subAttribute, value);
        } else {
            ApplyToAttribute(resource, op, path.Target, value);
        }
    }

    private static void ApplyToAttribute(JsonObject resource, PatchOp op, AttributePath target, JsonNode? value) {
        var attribute = target.Attribute;
        if (op == PatchOp.Remove) {
            if (attribute.Required) {
                throw ScimException.BadRequest(ScimException.MUTABILITY, $"'{attribute.Name}' is required and cannot be removed.");
            }
            target.Container(resource)?.Remove(attribute.Name);
            return;
        }
        var converted = AttributeValues.Convert(attribute, value);
        var container = target.ContainerForWrite(resource);
        // add and replace both merge into a complex attribute; sub-attributes the value leaves out stay (RFC 7644 3.5.2.1, 3.5.2.3)
        if (converted is JsonObject values && container[attribute.Name] is JsonObject existing) {
            Merge(existing, values);
        } else {
            container[attribute.Name] = converted;
        }
    }

    private static void ApplyToSubAttribute(
        JsonObject resource, PatchOp op, AttributePath target, ScimAttribute subAttribute, JsonNode? value) {
        var name = target.Attribute.Name;
        if (op == PatchOp.Remove) {
            (target.Container(resource)?[name] as JsonObject)?.Remove(subAttribute.Name);
            return;
        }
        var container = target.ContainerForWrite(resource);
        if (container[name] is not JsonObject parent) {
            parent = new JsonObject();
            container[name] = parent;
        }
        parent[subAttribute.Name] = AttributeValues.Convert(subAttribute, value);
    }

    /// <summary>An operation on a multi-valued attribute as a whole, such as <c>members</c>.</summary>
    private static void ApplyToList(JsonObject resource, PatchOp op, AttributePath target, JsonNode? value) {
        var attribute = target.Attribute;
        var container = target.ContainerForWrite(resource);
        if (op == PatchOp.Replace) {
            var replacement = new JsonArray([.. AttributeValues.ConvertElements(attribute, value)]);
            container[attribute.Name] = replacement;
            KeepOnePrimary(replacement, null);
            return;
        }
        if (container[attribute.Name] is not JsonArray elements) {
            elements = new JsonArray();
            container[attribute.Name] = elements;
        }
        if (op == PatchOp.Remove) {
            if (value is null) {
                container.Remove(attribute.Name);
                return;
            }
            // Microsoft Entra ID removes members by listing them in the value of a path without a filter
            var removals = AttributeValues.ConvertElements(attribute, value);
            var removed = elements.OfType<JsonObject>().Where(candidate => removals.Any(removal => SameValue(attribute, candidate, removal)));
            foreach (var element in removed.ToList()) {
                elements.Remove(element);
            }
            return;
        }
        // add appends; an element with the value of one already there replaces it instead of duplicating it
        var additions = AttributeValues.ConvertElements(attribute, value);
        foreach (var addition in additions) {
            var existing = elements.OfType<JsonObject>().FirstOrDefault(element => SameValue(attribute, element, addition));
            if (existing is null) {
                elements.Add(addition);
            } else {
                elements[elements.IndexOf(existing)] = addition;
            }
        }
        KeepOnePrimary(elements, additions.LastOrDefault(IsPrimary));
    }

    /// <summary>An operation on the elements a path picks: <c>emails[type eq "work"]</c>, <c>emails[type eq "work"].value</c>.</summary>
    private static void ApplyToElements(JsonObject resource, PatchOp op, PatchPath path, JsonNode? value) {
        var attribute = path.Attribute;
        var subAttribute = path.ElementAttribute;
        var container = path.Target.ContainerForWrite(resource);
        if (container[attribute.Name] is not JsonArray elements) {
            elements = new JsonArray();
            container[attribute.Name] = elements;
        }
        var matches = elements.OfType<JsonObject>().Where(element => path.Filter?.Matches(element) ?? true).ToList();
        if (op == PatchOp.Remove) {
            foreach (var element in matches) {
                if (subAttribute is null) {
                    elements.Remove(element);
                } else {
                    EnsureMutable(subAttribute, element);
                    element.Remove(subAttribute.Name);
                }
            }
            return;
        }

        var created = false;
        if (matches.Count == 0) {
            // Microsoft Entra ID sets emails[type eq "work"].value on a user who has no work address yet
            var element = new JsonObject();
            if (path.Filter is null || !Describe(path.Filter, element)) {
                throw ScimException.BadRequest(ScimException.NO_TARGET, $"No value of '{attribute.Name}' matches the path.");
            }
            elements.Add(element);
            matches.Add(element);
            created = true;
        }
        foreach (var element in matches) {
            if (subAttribute is null) {
                if (AttributeValues.ConvertSingle(attribute, value) is JsonObject values) {
                    Merge(element, values);
                }
            } else {
                if (!created) {
                    EnsureMutable(subAttribute, element);
                }
                element[subAttribute.Name] = AttributeValues.Convert(subAttribute, value);
            }
        }
        KeepOnePrimary(elements, matches.LastOrDefault(IsPrimary));
    }

    /// <summary>
    /// Fills an element from a filter made of equalities, so that <c>emails[type eq "work"].value</c> can create the work
    /// address. False for any other filter, which describes no element to create.
    /// </summary>
    private static bool Describe(FilterNode filter, JsonObject element) {
        switch (filter) {
            case AndFilter and:
                return Describe(and.Left, element) && Describe(and.Right, element);
            case CompareFilter { Operator: CompareOperator.Eq, Value: { } value } compare when !compare.Path.Attribute.IsReadOnly:
                element[compare.Path.Attribute.Name] = value.DeepClone();
                return true;
            default:
                return false;
        }
    }

    private static void EnsureMutable(ScimAttribute subAttribute, JsonObject element) {
        if (subAttribute.Mutability == Mutability.Immutable && element[subAttribute.Name] is not null) {
            throw ScimException.BadRequest(ScimException.MUTABILITY, $"'{subAttribute.Name}' cannot change once it is set.");
        }
    }

    private static bool SameValue(ScimAttribute attribute, JsonObject element, JsonObject other) {
        var valueAttribute = attribute.FindSubAttribute(ScimAttribute.VALUE);
        return valueAttribute is not null
            && JsonNodes.AsString(element[ScimAttribute.VALUE]) is { } value
            && string.Equals(value, JsonNodes.AsString(other[ScimAttribute.VALUE]), valueAttribute.Comparison);
    }

    private static void Merge(JsonObject target, JsonObject values) {
        foreach (var (name, value) in values.ToList()) {
            values.Remove(name);
            target[name] = value;
        }
    }

    /// <summary>Leaves at most one element primary (RFC 7643 section 2.4): the one just set, or else the last.</summary>
    private static void KeepOnePrimary(JsonArray elements, JsonObject? winner) {
        winner ??= elements.OfType<JsonObject>().LastOrDefault(IsPrimary);
        foreach (var element in elements.OfType<JsonObject>()) {
            if (element != winner && IsPrimary(element)) {
                element[PRIMARY] = false;
            }
        }
    }

    private static bool IsPrimary(JsonObject element) {
        return JsonNodes.IsTrue(element[PRIMARY]);
    }
}
