using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScimStudio.App.Controls;
using ScimStudio.App.Services;
using ScimStudio.Core.Dialects;
using ScimStudio.Core.Http;

namespace ScimStudio.App.ViewModels;

/// <summary>An entry of the navigation: where it leads, and a count beside it where one helps.</summary>
/// <param name="key">The section's key, which names it in the catalogues.</param>
/// <param name="icon">The icon.</param>
/// <param name="page">The page it opens.</param>
public sealed partial class NavItemViewModel(string key, Geometry icon, ViewModelBase page) : ViewModelBase {
    public string Key { get; } = key;

    public Geometry Icon { get; } = icon;

    public ViewModelBase Page { get; } = page;

    public string Title => L.Get($"nav.{Key}");

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasBadge))]
    public partial string? Badge { get; set; }

    public bool HasBadge => !string.IsNullOrEmpty(Badge);
}

/// <summary>A session: the navigation, the page open, and the bar above that says where one is connected and in which dialect.</summary>
public sealed partial class ShellViewModel : ViewModelBase, IDisposable {
    private readonly AppServices _services;
    private readonly Action _close;
    private readonly NavItemViewModel _logItem;

    /// <summary>Opens a session's pages.</summary>
    /// <param name="services">What the interface shares.</param>
    /// <param name="session">The session.</param>
    /// <param name="close">Returns to the start page.</param>
    public ShellViewModel(AppServices services, Session session, Action close) {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(session);

        _services = services;
        _close = close;
        Session = session;

        Log = new LogViewModel(services, session);
        Users = new UsersViewModel(services, session, this);
        Groups = new GroupsViewModel(services, session, this);
        Server = new ServerViewModel(session);
        Checks = new ChecksViewModel(services, session, this);
        Generator = new GeneratorViewModel(services, session);
        Preferences = new SettingsViewModel(services);

        _logItem = new NavItemViewModel("log", Icons.Activity, Log);
        Navigation = [
            new NavItemViewModel("users", Icons.User, Users),
            new NavItemViewModel("groups", Icons.Users, Groups),
            new NavItemViewModel("server", Icons.Server, Server),
            new NavItemViewModel("checks", Icons.ShieldCheck, Checks),
            new NavItemViewModel("generator", Icons.Sparkles, Generator),
            _logItem,
        ];
        Footer = [new NavItemViewModel("settings", Icons.Sliders, Preferences)];

        SelectedDialect = DialectOption.For(session.Dialect.Kind);
        Selected = Navigation[0];

        Log.CountChanged += OnLogCountChanged;
        OnLogCountChanged(this, EventArgs.Empty);
    }

    public Session Session { get; }

    public IReadOnlyList<NavItemViewModel> Navigation { get; }

    public IReadOnlyList<NavItemViewModel> Footer { get; }

    public UsersViewModel Users { get; }

    public GroupsViewModel Groups { get; }

    public ServerViewModel Server { get; }

    public ChecksViewModel Checks { get; }

    public GeneratorViewModel Generator { get; }

    public LogViewModel Log { get; }

    public SettingsViewModel Preferences { get; }

    public IReadOnlyList<DialectOption> Dialects => DialectOption.All;

    public string ProfileName => Session.Profile.Name;

    public string BaseUrl => Session.Client.Connection.BaseUrl.AbsoluteUri;

    public bool IsDemo => Session.IsDemo;

    [ObservableProperty]
    public partial NavItemViewModel? Selected { get; set; }

    /// <summary>The same entry picked from the footer - held apart so the two lists never both show a selection.</summary>
    [ObservableProperty]
    public partial NavItemViewModel? SelectedFooter { get; set; }

    [ObservableProperty]
    public partial ViewModelBase? Page { get; set; }

    [ObservableProperty]
    public partial DialectOption SelectedDialect { get; set; }

    partial void OnSelectedChanged(NavItemViewModel? value) {
        if (value is null) {
            return;
        }

        SelectedFooter = null;
        Page = value.Page;
        (value.Page as IActivatable)?.Activate();
    }

    partial void OnSelectedFooterChanged(NavItemViewModel? value) {
        if (value is null) {
            return;
        }

        Selected = null;
        Page = value.Page;
    }

    partial void OnSelectedDialectChanged(DialectOption value) {
        Session.Dialect = ScimDialect.For(value.Kind);
        Generator.Relocalize();
        if (!Session.IsDemo && Session.Profile.Dialect != value.Kind) {
            Session.Profile.Dialect = value.Kind;
            _services.Save();
        }
    }

    /// <summary>Opens the log at one exchange - from a check, or an error.</summary>
    /// <param name="exchange">The exchange.</param>
    public void ShowExchange(HttpExchange exchange) {
        Selected = _logItem;
        Log.Reveal(exchange);
    }

    public void ShowUser(string id) {
        Selected = Navigation[0];
        _ = Users.OpenAsync(id);
    }

    public void ShowGroup(string id) {
        Selected = Navigation[1];
        _ = Groups.OpenAsync(id);
    }

    [RelayCommand]
    private void Disconnect() {
        _close();
    }

    private void OnLogCountChanged(object? sender, EventArgs e) {
        _logItem.Badge = Log.Count == 0 ? null : Log.Count.ToString(L.Culture);
    }

    public void Dispose() {
        Log.CountChanged -= OnLogCountChanged;
        Log.Dispose();
        Users.Dispose();
        Groups.Dispose();
        Checks.Dispose();
        Generator.Dispose();
        Session.Dispose();
    }
}

/// <summary>A page that loads when it is first shown, rather than when the session opens.</summary>
public interface IActivatable {
    /// <summary>Called each time the page is shown.</summary>
    void Activate();
}
