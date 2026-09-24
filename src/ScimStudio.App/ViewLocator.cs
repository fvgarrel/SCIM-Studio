using Avalonia.Controls;
using Avalonia.Controls.Templates;
using ScimStudio.App.ViewModels;
using ScimStudio.App.Views;

namespace ScimStudio.App;

/// <summary>Which view shows which view model. Written out rather than found by name, so trimming keeps every view that is used.</summary>
public sealed class ViewLocator : IDataTemplate {
    public Control? Build(object? param) {
        return param switch {
            ConnectViewModel => new ConnectView(),
            ShellViewModel => new ShellView(),
            UsersViewModel => new UsersView(),
            GroupsViewModel => new GroupsView(),
            ServerViewModel => new ServerView(),
            ChecksViewModel => new ChecksView(),
            GeneratorViewModel => new GeneratorView(),
            LogViewModel => new LogView(),
            SettingsViewModel => new SettingsView(),
            UserEditorViewModel => new UserEditorView(),
            GroupEditorViewModel => new GroupEditorView(),
            _ => new TextBlock { Text = param?.GetType().Name },
        };
    }

    public bool Match(object? data) {
        return data is ViewModelBase;
    }
}
