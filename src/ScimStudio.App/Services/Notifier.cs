using System.Collections.ObjectModel;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ScimStudio.App.Services;

public enum ToastKind {
    Info,
    Success,
    Warning,
    Error,
}

/// <summary>A notice in the corner that goes away by itself - an error later than the rest, since it is the one worth reading.</summary>
/// <param name="kind">What kind of notice it is.</param>
/// <param name="title">What happened.</param>
/// <param name="detail">More, if there is more.</param>
/// <param name="dismiss">Takes the toast away.</param>
public sealed partial class ToastViewModel(ToastKind kind, string title, string? detail, Action<ToastViewModel> dismiss) : ObservableObject {
    public ToastKind Kind { get; } = kind;

    public string Title { get; } = title;

    public string? Detail { get; } = detail;

    public bool HasDetail => !string.IsNullOrWhiteSpace(Detail);

    [RelayCommand]
    private void Dismiss() {
        dismiss(this);
    }
}

/// <summary>Shows toasts. Called from the UI thread; a call from anywhere else is moved onto it.</summary>
public sealed class Notifier {
    private const int LIMIT = 4;

    public ObservableCollection<ToastViewModel> Toasts { get; } = [];

    public void Info(string title, string? detail = null) {
        Show(ToastKind.Info, title, detail);
    }

    public void Success(string title, string? detail = null) {
        Show(ToastKind.Success, title, detail);
    }

    public void Warning(string title, string? detail = null) {
        Show(ToastKind.Warning, title, detail);
    }

    public void Error(string title, string? detail = null) {
        Show(ToastKind.Error, title, detail);
    }

    private void Show(ToastKind kind, string title, string? detail) {
        if (!Dispatcher.UIThread.CheckAccess()) {
            Dispatcher.UIThread.Post(() => Show(kind, title, detail));
            return;
        }

        var toast = new ToastViewModel(kind, title, detail, Remove);
        Toasts.Add(toast);
        while (Toasts.Count > LIMIT) {
            Toasts.RemoveAt(0);
        }

        var lifetime = kind is ToastKind.Error or ToastKind.Warning ? TimeSpan.FromSeconds(9) : TimeSpan.FromSeconds(4);
        DispatcherTimer.RunOnce(() => Remove(toast), lifetime);
    }

    private void Remove(ToastViewModel toast) {
        Toasts.Remove(toast);
    }
}
