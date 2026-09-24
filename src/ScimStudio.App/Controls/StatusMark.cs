using Avalonia;
using Avalonia.Controls;
using ScimStudio.Core.Checks;

namespace ScimStudio.App.Controls;

/// <summary>A check's verdict as a mark: never colour alone, each state has its own shape as well.</summary>
public sealed class StatusMark : Decorator {
    public static readonly StyledProperty<CheckStatus> StatusProperty = AvaloniaProperty.Register<StatusMark, CheckStatus>(nameof(Status));

    private readonly Icon _icon = new() { Width = 16, Height = 16, Thickness = 2.4 };

    public StatusMark() {
        Child = _icon;
        Update();
    }

    public CheckStatus Status {
        get => GetValue(StatusProperty);
        set => SetValue(StatusProperty, value);
    }

    protected override void OnPropertyChanged(AvaloniaPropertyChangedEventArgs change) {
        base.OnPropertyChanged(change);

        if (change.Property == StatusProperty) {
            Update();
        }
    }

    private void Update() {
        var (data, brush) = Status switch {
            CheckStatus.Passed => (Icons.Check, "SuccessBrush"),
            CheckStatus.Warning => (Icons.Alert, "WarningBrush"),
            CheckStatus.Failed => (Icons.Close, "DangerBrush"),
            CheckStatus.Unsupported => (Icons.Ban, "MutedTextBrush"),
            CheckStatus.Skipped => (Icons.Minus, "FaintTextBrush"),
            CheckStatus.Running => (Icons.Refresh, "AccentBrush"),
            _ => (Icons.Dot, "FaintTextBrush"),
        };

        _icon.Data = data;
        _icon.Classes.Set("spin", Status == CheckStatus.Running);
        _icon.Bind(Icon.ForegroundProperty, this.GetResourceObservable(brush));
    }
}
