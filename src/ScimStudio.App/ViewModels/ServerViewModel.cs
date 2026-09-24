using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScimStudio.App.Services;
using ScimStudio.Core.Http;
using ScimStudio.Core.Scim;

namespace ScimStudio.App.ViewModels;

/// <summary>An attribute of a schema as a row of the table, its sub-attributes indented beneath it.</summary>
/// <param name="attribute">The attribute.</param>
/// <param name="depth">How deep it is nested: 0 for an attribute, 1 for a sub-attribute.</param>
public sealed class AttributeRowViewModel(AttributeDefinition attribute, int depth) {
    public string Name { get; } = attribute.Name;

    public string Type => attribute.MultiValued ? $"{attribute.Type}[]" : attribute.Type;

    public string Mutability { get; } = attribute.Mutability;

    public string Returned { get; } = attribute.Returned;

    public string Uniqueness { get; } = attribute.Uniqueness;

    public bool Required { get; } = attribute.Required;

    public bool CaseExact { get; } = attribute.CaseExact;

    public string? Description { get; } = attribute.Description;

    public string? CanonicalValues => attribute.CanonicalValues.Count == 0 ? null : string.Join(", ", attribute.CanonicalValues);

    public bool IsSub => depth > 0;

    public Avalonia.Thickness Indent => new(depth * 18, 0, 0, 0);
}

/// <summary>A schema the server describes, with its attributes flattened into rows.</summary>
/// <param name="schema">The schema.</param>
public sealed class SchemaViewModel(SchemaDefinition schema) {
    public string Id { get; } = schema.Id;

    public string Name { get; } = schema.Name ?? schema.Id;

    public string? Description { get; } = schema.Description;

    public IReadOnlyList<AttributeRowViewModel> Attributes { get; } = [.. Flatten(schema.Attributes, 0)];

    public string Json { get; } = schema.Document.ToJsonString(ScimJson.Readable);

    private static IEnumerable<AttributeRowViewModel> Flatten(IEnumerable<AttributeDefinition> attributes, int depth) {
        foreach (var attribute in attributes) {
            yield return new AttributeRowViewModel(attribute, depth);
            foreach (var sub in Flatten(attribute.SubAttributes, depth + 1)) {
                yield return sub;
            }
        }
    }
}

/// <summary>The server page: what the service provider says about itself (RFC 7644 section 4).</summary>
/// <param name="session">The session.</param>
public sealed partial class ServerViewModel(Session session) : ViewModelBase, IActivatable {
    private bool _loaded;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Capabilities), nameof(ConfigJson), nameof(AuthenticationSchemes), nameof(DocumentationUri))]
    public partial ServiceProviderConfig? Configuration { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<ResourceTypeInfo> ResourceTypes { get; set; } = [];

    [ObservableProperty]
    public partial IReadOnlyList<SchemaViewModel> Schemas { get; set; } = [];

    [ObservableProperty]
    public partial SchemaViewModel? SelectedSchema { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public string BaseUrl => session.Client.Connection.BaseUrl.AbsoluteUri;

    public IReadOnlyList<CapabilityViewModel> Capabilities => CapabilityViewModel.Of(Configuration);

    public IReadOnlyList<string> AuthenticationSchemes => Configuration is null
        ? []
        : [.. Configuration.AuthenticationSchemes.Select(s => s.Name ?? s.Type ?? "—")];

    public string? DocumentationUri => Configuration?.DocumentationUri;

    public string ConfigJson => Configuration?.Document.ToJsonString(ScimJson.Readable) ?? string.Empty;

    public void Activate() {
        if (!_loaded) {
            _loaded = true;
            _ = LoadAsync();
        }
    }

    [RelayCommand]
    private async Task LoadAsync() {
        IsLoading = true;
        Error = null;
        try {
            using var scope = ExchangeScope.Begin("ui:discovery");
            Configuration = await session.Client.GetServiceProviderConfigAsync();
            session.Configuration = Configuration;
            ResourceTypes = await session.Client.GetResourceTypesAsync();

            var selected = SelectedSchema?.Id;
            Schemas = [.. (await session.Client.GetSchemasAsync()).Select(s => new SchemaViewModel(s))];
            SelectedSchema = Schemas.FirstOrDefault(s => s.Id == selected) ?? (Schemas.Count > 0 ? Schemas[0] : null);
        } catch (Exception failure) when (failure is ScimException or HttpRequestException or TaskCanceledException) {
            Error = Describe(failure);
        } finally {
            IsLoading = false;
        }
    }
}
