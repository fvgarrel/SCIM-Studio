using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace ScimStudio.App.Controls;

/// <summary>
/// One of the <see cref="Icons"/>, stroked in the foreground colour. The 24-unit grid is scaled as a whole, stroke included, so every icon
/// keeps the proportions it was drawn with at any size.
/// </summary>
public sealed class Icon : TemplatedControl {
    public static readonly StyledProperty<Geometry?> DataProperty = AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<double> ThicknessProperty = AvaloniaProperty.Register<Icon, double>(nameof(Thickness), 2);

    public Geometry? Data {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double Thickness {
        get => GetValue(ThicknessProperty);
        set => SetValue(ThicknessProperty, value);
    }
}
