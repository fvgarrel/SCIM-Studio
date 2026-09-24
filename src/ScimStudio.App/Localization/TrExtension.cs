using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace ScimStudio.App.Localization;

/// <summary>
/// <c>{l:Tr users.title}</c> in XAML: the key's text, kept current when the language changes. Bound to an object per key rather than through an
/// indexer path, since a key's dots would read as a path of their own.
/// </summary>
public sealed class TrExtension : MarkupExtension {
    public TrExtension() {
    }

    public TrExtension(string key) {
        Key = key;
    }

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) {
        return new Binding(nameof(LocalizedText.Value)) { Source = Localizer.Instance.Entry(Key), Mode = BindingMode.OneWay };
    }
}
