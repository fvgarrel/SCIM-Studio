using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace ScimStudio.App.Controls;

/// <summary>The tool's mark: two people on the accent, as on the start page and in the bar of a session.</summary>
public sealed class Logo : Border {
    public static readonly StyledProperty<double> SizeProperty = AvaloniaProperty.Register<Logo, double>(nameof(Size), 32);

    private readonly Icon _glyph = new() { Data = Icons.Users, Foreground = Brushes.White, Thickness = 2.2 };

    public Logo() {
        Child = _glyph;
        Bind(BackgroundProperty, this.GetResourceObservable("AccentBrush"));
        Resize();
    }

    public double Size {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);

        if (change.Property == SizeProperty) {
            Resize();
        }
    }

    private void Resize() {
        Width = Height = Size;
        CornerRadius = new CornerRadius(Size * 0.28);
        _glyph.Width = _glyph.Height = Size * 0.55;
    }
}

/// <summary>A feature of the server as a badge: ticked and green when it is there, a dash when not - never red, since absent is no fault.</summary>
public sealed class Capability : Border {
    public static readonly StyledProperty<string?> LabelProperty = AvaloniaProperty.Register<Capability, string?>(nameof(Label));

    public static readonly StyledProperty<bool> SupportedProperty = AvaloniaProperty.Register<Capability, bool>(nameof(Supported));

    public static readonly StyledProperty<string?> DetailProperty = AvaloniaProperty.Register<Capability, string?>(nameof(Detail));

    private readonly Icon _icon = new() { Width = 12, Height = 12, Thickness = 2.6 };
    private readonly TextBlock _text = new() { FontSize = 12, FontWeight = FontWeight.Medium };

    public Capability() {
        Classes.Add("badge");
        Padding = new Thickness(8, 3);
        Child = new StackPanel {
            Orientation = Orientation.Horizontal,
            Spacing = 5,
            Children = { _icon, _text },
        };

        Update();
    }

    public string? Label {
        get => GetValue(LabelProperty);
        set => SetValue(LabelProperty, value);
    }

    public bool Supported {
        get => GetValue(SupportedProperty);
        set => SetValue(SupportedProperty, value);
    }

    public string? Detail {
        get => GetValue(DetailProperty);
        set => SetValue(DetailProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);

        if (change.Property == LabelProperty || change.Property == SupportedProperty || change.Property == DetailProperty) {
            Update();
        }
    }

    private void Update() {
        Classes.Set("success", Supported);
        _icon.Data = Supported ? Icons.Check : Icons.Minus;
        _text.Text = Detail is null ? Label : $"{Label} · {Detail}";
    }
}
