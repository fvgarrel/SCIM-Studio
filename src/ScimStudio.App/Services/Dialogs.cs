using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ScimStudio.App.Services;

/// <summary>A question the window asks over everything else, answered by one of two buttons or Escape.</summary>
/// <param name="title">The question.</param>
/// <param name="message">What answering yes does.</param>
/// <param name="confirm">The label of the button that says yes.</param>
/// <param name="danger">Whether yes destroys something, which colours the button.</param>
public sealed partial class ConfirmDialogViewModel(string title, string message, string confirm, bool danger) : ObservableObject {
    private readonly TaskCompletionSource<bool> _answer = new();

    public string Title { get; } = title;

    public string Message { get; } = message;

    public string ConfirmLabel { get; } = confirm;

    public bool IsDanger { get; } = danger;

    public Task<bool> Answer => _answer.Task;

    [RelayCommand]
    private void Confirm() {
        _answer.TrySetResult(true);
    }

    [RelayCommand]
    private void Cancel() {
        _answer.TrySetResult(false);
    }
}

/// <summary>Asks questions over the window, one at a time.</summary>
public sealed partial class Dialogs : ObservableObject {
    [ObservableProperty]
    public partial ConfirmDialogViewModel? Current { get; set; }

    public async Task<bool> ConfirmAsync(string title, string message, string confirm, bool danger = false) {
        var dialog = new ConfirmDialogViewModel(title, message, confirm, danger);
        Current = dialog;
        try {
            return await dialog.Answer;
        } finally {
            Current = null;
        }
    }
}
