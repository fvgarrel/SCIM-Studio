using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Documents;
using Avalonia.Media;

namespace ScimStudio.App.Controls;

/// <summary>
/// JSON shown with its keys, strings, numbers and literals told apart, and selectable. Text that is not JSON is shown as it is, and a body
/// too long to colour quickly is shown plain, since a page of two hundred users is several thousand runs.
/// </summary>
public sealed class JsonText : SelectableTextBlock {
    public static readonly StyledProperty<string?> JsonProperty = AvaloniaProperty.Register<JsonText, string?>(nameof(Json));

    private const int COLOURED = 200_000;

    public JsonText() {
        TextWrapping = TextWrapping.Wrap;
        ActualThemeVariantChanged += (_, _) => Render();
    }

    public string? Json {
        get => GetValue(JsonProperty);
        set => SetValue(JsonProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);

        if (change.Property == JsonProperty) {
            Render();
        }
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e) {
        base.OnAttachedToVisualTree(e);
        Render();
    }

    private void Render() {
        var json = Json ?? string.Empty;
        Inlines?.Clear();
        Text = null;

        if (json.Length > COLOURED || !(json.StartsWith('{') || json.StartsWith('['))) {
            Text = json;
            return;
        }

        var inlines = new InlineCollection();
        foreach (var (text, kind) in JsonTokens.Read(json)) {
            var run = new Run(text);
            if (Brush(kind) is { } brush) {
                run.Foreground = brush;
            }

            inlines.Add(run);
        }

        Inlines = inlines;
    }

    private IBrush? Brush(JsonTokenKind kind) {
        var key = kind switch {
            JsonTokenKind.Key => "JsonKeyBrush",
            JsonTokenKind.Text => "JsonStringBrush",
            JsonTokenKind.Number => "JsonNumberBrush",
            JsonTokenKind.Literal => "JsonLiteralBrush",
            JsonTokenKind.Punctuation => "JsonPunctuationBrush",
            _ => null,
        };

        return key is not null && this.TryFindResource(key, ActualThemeVariant, out var found) ? found as IBrush : null;
    }
}

public enum JsonTokenKind {
    Plain,
    Key,
    Text,
    Number,
    Literal,
    Punctuation,
}

/// <summary>Cuts JSON text into the pieces <see cref="JsonText"/> colours, whitespace included, so the pieces put together are the text.</summary>
public static class JsonTokens {
    public static IEnumerable<(string Text, JsonTokenKind Kind)> Read(string json) {
        ArgumentNullException.ThrowIfNull(json);

        var i = 0;
        while (i < json.Length) {
            var start = i;
            var character = json[i];

            if (char.IsWhiteSpace(character)) {
                while (i < json.Length && char.IsWhiteSpace(json[i])) {
                    i++;
                }

                yield return (json[start..i], JsonTokenKind.Plain);
            } else if (character == '"') {
                i++;
                while (i < json.Length && json[i] != '"') {
                    i += json[i] == '\\' ? 2 : 1;
                }

                i = Math.Min(i + 1, json.Length);
                yield return (json[start..i], IsKey(json, i) ? JsonTokenKind.Key : JsonTokenKind.Text);
            } else if (character is '{' or '}' or '[' or ']' or ',' or ':') {
                i++;
                yield return (json[start..i], JsonTokenKind.Punctuation);
            } else if (character == '-' || char.IsDigit(character)) {
                while (i < json.Length && (char.IsDigit(json[i]) || json[i] is '-' or '+' or '.' or 'e' or 'E')) {
                    i++;
                }

                yield return (json[start..i], JsonTokenKind.Number);
            } else if (char.IsLetter(character)) {
                while (i < json.Length && char.IsLetter(json[i])) {
                    i++;
                }

                yield return (json[start..i], JsonTokenKind.Literal);
            } else {
                i++;
                yield return (json[start..i], JsonTokenKind.Plain);
            }
        }
    }

    /// <summary>Whether the string that ended just before this position is a key: the next thing after it is a colon.</summary>
    /// <param name="json">The text.</param>
    /// <param name="position">The position after the closing quote.</param>
    private static bool IsKey(string json, int position) {
        while (position < json.Length && char.IsWhiteSpace(json[position])) {
            position++;
        }

        return position < json.Length && json[position] == ':';
    }
}
