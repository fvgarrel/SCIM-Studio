using Avalonia.Data.Converters;
using ScimStudio.App.Services;
using ScimStudio.Core.Checks;

namespace ScimStudio.App.ViewModels;

/// <summary>The comparisons views make against a view model's state, each named once here rather than as a converter per view.</summary>
public static class Is {
    public static readonly IValueConverter Success = new FuncValueConverter<ToastKind, bool>(kind => kind == ToastKind.Success);

    public static readonly IValueConverter Warning = new FuncValueConverter<ToastKind, bool>(kind => kind == ToastKind.Warning);

    public static readonly IValueConverter Error = new FuncValueConverter<ToastKind, bool>(kind => kind == ToastKind.Error);

    public static readonly IValueConverter Info = new FuncValueConverter<ToastKind, bool>(kind => kind == ToastKind.Info);

    public static readonly IValueConverter Pending = new FuncValueConverter<CheckStatus, bool>(status => status == CheckStatus.Pending);

    public static readonly IValueConverter Zero = new FuncValueConverter<int, bool>(count => count == 0);

    public static readonly IValueConverter Positive = new FuncValueConverter<int, bool>(count => count > 0);
}
