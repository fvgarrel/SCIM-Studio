using CommunityToolkit.Mvvm.ComponentModel;
using ScimStudio.App.Services;
using ScimStudio.Core.Scim;

namespace ScimStudio.App.ViewModels;

/// <summary>The window: the start page or a session, with toasts and dialogs over both.</summary>
public sealed partial class MainWindowViewModel : ViewModelBase {
    private readonly AppServices _services;

    public MainWindowViewModel(AppServices services) {
        ArgumentNullException.ThrowIfNull(services);

        _services = services;
        Connect = new ConnectViewModel(services, Open);
        Page = Connect;
    }

    public ConnectViewModel Connect { get; }

    [ObservableProperty]
    public partial ViewModelBase Page { get; set; }

    public Notifier Notifier => _services.Notifier;

    public Dialogs Dialogs => _services.Dialogs;

    private void Open(Session session, ProbeReport? probe) {
        Page = new ShellViewModel(_services, session, probe, Close);
    }

    private void Close() {
        if (Page is ShellViewModel shell) {
            shell.Dispose();
        }

        Page = Connect;
    }
}
