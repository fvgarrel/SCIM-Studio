using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScimStudio.App.Localization;
using ScimStudio.App.Services;
using ScimStudio.App.Settings;
using ScimStudio.Core.Dialects;
using ScimStudio.Core.Http;
using ScimStudio.Core.Scim;

namespace ScimStudio.App.ViewModels;

/// <summary>A dialect as the interface offers it: its name and what it does differently.</summary>
/// <param name="kind">The dialect.</param>
public sealed class DialectOption(DialectKind kind) : ViewModelBase {
    public static IReadOnlyList<DialectOption> All { get; } = [.. ScimDialect.All.Select(d => new DialectOption(d.Kind))];

    public DialectKind Kind { get; } = kind;

    public string Name => L.Get($"dialect.{Key}");

    public string About => L.Get($"dialect.{Key}.about");

    private string Key => Kind switch {
        DialectKind.EntraId => "entra",
        DialectKind.Okta => "okta",
        _ => "rfc",
    };

    public static DialectOption For(DialectKind kind) {
        return All.First(option => option.Kind == kind);
    }
}

/// <summary>A profile being edited on the start page. Changes stay here until they are applied, which connecting or testing does.</summary>
public sealed partial class ProfileViewModel : ViewModelBase {
    public ProfileViewModel(ConnectionProfile profile) {
        ArgumentNullException.ThrowIfNull(profile);

        Profile = profile;
        Name = profile.Name;
        BaseUrl = profile.BaseUrl;
        Token = TokenVault.Unprotect(profile.StoredToken) ?? string.Empty;
        RememberToken = profile.RememberToken;
        Dialect = DialectOption.For(profile.Dialect);
        AcceptInvalidCertificates = profile.AcceptInvalidCertificates;
        TimeoutSeconds = profile.TimeoutSeconds;
    }

    public ConnectionProfile Profile { get; }

    public IReadOnlyList<DialectOption> Dialects => DialectOption.All;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title))]
    public partial string Name { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(Title), nameof(Host), nameof(UrlProblem), nameof(IsValid))]
    public partial string BaseUrl { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IsValid))]
    public partial string Token { get; set; }

    [ObservableProperty]
    public partial bool RememberToken { get; set; }

    [ObservableProperty]
    public partial DialectOption Dialect { get; set; }

    [ObservableProperty]
    public partial bool AcceptInvalidCertificates { get; set; }

    [ObservableProperty]
    public partial decimal? TimeoutSeconds { get; set; }

    public string Title => string.IsNullOrWhiteSpace(Name) ? Host : Name;

    public string Host => Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri) ? uri.Authority : BaseUrl;

    public bool IsValid => UrlProblem is null && !string.IsNullOrWhiteSpace(Token);

    /// <summary>What is wrong with the address, or null when nothing is.</summary>
    public string? UrlProblem {
        get {
            if (string.IsNullOrWhiteSpace(BaseUrl)) {
                return null;
            }

            var web = Uri.TryCreate(BaseUrl.Trim(), UriKind.Absolute, out var uri)
                && (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);
            return web ? null : L.Get("connect.urlInvalid");
        }
    }

    /// <summary>Writes the edits into the profile, the token through the vault or not at all.</summary>
    public void Apply() {
        Profile.Name = Name.Trim();
        Profile.BaseUrl = BaseUrl.Trim();
        Profile.RememberToken = RememberToken;
        Profile.StoredToken = RememberToken && Token.Length > 0 ? TokenVault.Protect(Token.Trim()) : null;
        Profile.Dialect = Dialect.Kind;
        Profile.AcceptInvalidCertificates = AcceptInvalidCertificates;
        Profile.TimeoutSeconds = (int)(TimeoutSeconds ?? 30);
    }
}

/// <summary>A connection test's outcome as the start page shows it.</summary>
/// <param name="report">What the test found.</param>
public sealed class ProbeResultViewModel(ProbeReport report) : ViewModelBase {
    public ProbeReport Report { get; } = report;

    public bool Succeeded => Report.Succeeded;

    public bool Failed => !Report.Reachable || !Report.Authenticated;

    public bool Partial => !Succeeded && !Failed;

    public string Headline {
        get {
            if (!Report.Reachable) {
                return L.Get("probe.headline.unreachable");
            }

            if (!Report.Authenticated) {
                return L.Get("probe.headline.refused");
            }

            return L.Format(Report.Problems.Count == 0 ? "probe.headline.ok" : "probe.headline.partial", (int)Report.Duration.TotalMilliseconds);
        }
    }

    public string? Counts => Report.UserCount is null && Report.GroupCount is null
        ? null
        : L.Format("probe.counts", Report.UserCount?.ToString(L.Culture) ?? "?", Report.GroupCount?.ToString(L.Culture) ?? "?");

    public IReadOnlyList<string> Problems => [.. Report.Problems.Select(L.Format)];

    public IReadOnlyList<CapabilityViewModel> Capabilities => CapabilityViewModel.Of(Report.Configuration);
}

/// <summary>One feature of the service provider's configuration, and whether it is there.</summary>
/// <param name="name">The feature.</param>
/// <param name="supported">Whether the server supports it.</param>
/// <param name="detail">A limit that goes with it, if any.</param>
public sealed class CapabilityViewModel(string name, bool supported, string? detail) {
    public string Name { get; } = name;

    public bool Supported { get; } = supported;

    public string? Detail { get; } = detail;

    public static IReadOnlyList<CapabilityViewModel> Of(ServiceProviderConfig? config) {
        if (config is null) {
            return [];
        }

        var culture = Localizer.Instance.Culture;
        return [
            new("PATCH", config.PatchSupported, null),
            new("Filter", config.FilterSupported, config.FilterMaxResults is { } max ? $"max {max.ToString(culture)}" : null),
            new("Sort", config.SortSupported, null),
            new("Bulk", config.BulkSupported, config.BulkSupported && config.BulkMaxOperations is { } ops ? $"max {ops.ToString(culture)}" : null),
            new("ETag", config.EtagSupported, null),
            new("Password", config.ChangePasswordSupported, null),
        ];
    }
}

/// <summary>The start page: the profiles, the one being edited, and the ways in - a server of one's own, or the demo.</summary>
public sealed partial class ConnectViewModel : ViewModelBase {
    private readonly AppServices _services;
    private readonly Action<Session, ProbeReport?> _open;

    public ConnectViewModel(AppServices services, Action<Session, ProbeReport?> open) {
        ArgumentNullException.ThrowIfNull(services);

        _services = services;
        _open = open;
        Profiles = [.. services.Settings.Profiles.Select(p => new ProfileViewModel(p))];
        Selected = Profiles.FirstOrDefault(p => p.Profile.Id == services.Settings.LastProfileId) ?? Profiles.FirstOrDefault();
        Profiles.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasProfiles));
    }

    public ObservableCollection<ProfileViewModel> Profiles { get; }

    public bool HasProfiles => Profiles.Count > 0;

    public bool HasSelection => Selected is not null;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelection))]
    [NotifyCanExecuteChangedFor(nameof(TestCommand), nameof(ConnectCommand), nameof(DeleteProfileCommand))]
    public partial ProfileViewModel? Selected { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(TestCommand), nameof(ConnectCommand), nameof(StartDemoCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial ProbeResultViewModel? Probe { get; set; }

    public SettingsViewModel Preferences => field ??= new SettingsViewModel(_services);

    partial void OnSelectedChanged(ProfileViewModel? oldValue, ProfileViewModel? newValue) {
        Probe = null;

        if (oldValue is not null) {
            oldValue.PropertyChanged -= OnProfileEdited;
        }

        if (newValue is not null) {
            newValue.PropertyChanged += OnProfileEdited;
        }
    }

    private void OnProfileEdited(object? sender, PropertyChangedEventArgs e) {
        if (e.PropertyName is nameof(ProfileViewModel.IsValid) or nameof(ProfileViewModel.BaseUrl) or nameof(ProfileViewModel.Token)) {
            TestCommand.NotifyCanExecuteChanged();
            ConnectCommand.NotifyCanExecuteChanged();
        }
    }

    private bool CanUseSelected() {
        return !IsBusy && Selected is { IsValid: true, BaseUrl.Length: > 0 };
    }

    private bool CanStartDemo() {
        return !IsBusy;
    }

    [RelayCommand]
    private void NewProfile() {
        var profile = new ProfileViewModel(new ConnectionProfile { Name = L.Get("connect.newProfile"), BaseUrl = "https://" });
        Profiles.Add(profile);
        Selected = profile;
    }

    [RelayCommand(CanExecute = nameof(HasSelection))]
    private async Task DeleteProfileAsync() {
        if (Selected is not { } profile) {
            return;
        }

        var confirmed = await _services.Dialogs.ConfirmAsync(
            L.Get("connect.deleteTitle"), L.Format("connect.deleteMessage", profile.Title), L.Get("common.delete"), danger: true);
        if (!confirmed) {
            return;
        }

        var index = Profiles.IndexOf(profile);
        Profiles.Remove(profile);
        _services.Settings.Profiles.Remove(profile.Profile);
        _services.Save();
        Selected = Profiles.Count == 0 ? null : Profiles[Math.Min(index, Profiles.Count - 1)];
    }

    [RelayCommand(CanExecute = nameof(CanUseSelected))]
    private async Task TestAsync() {
        if (Store() is not { } profile) {
            return;
        }

        IsBusy = true;
        try {
            using var client = new ScimClient(Session.Connection(profile.Profile, profile.Token.Trim()), _services.Log);
            using var scope = ExchangeScope.Begin("ui:probe");
            Probe = new ProbeResultViewModel(await ConnectionProbe.RunAsync(client));
        } finally {
            IsBusy = false;
        }
    }

    [RelayCommand(CanExecute = nameof(CanUseSelected))]
    private async Task ConnectAsync() {
        if (Store() is not { } profile) {
            return;
        }

        await OpenAsync(new Session(profile.Profile, profile.Token.Trim(), _services.Log));
    }

    [RelayCommand(CanExecute = nameof(CanStartDemo))]
    private async Task StartDemoAsync() {
        IsBusy = true;
        try {
            var server = await _services.Demo.StartAsync();
            var profile = new ConnectionProfile { Name = L.Get("demo.name"), BaseUrl = server.BaseUrl.AbsoluteUri, Dialect = DialectKind.Rfc7644 };
            await OpenAsync(new Session(profile, server.Token, _services.Log, isDemo: true));
        } catch (IOException failure) {
            _services.Notifier.Error(L.Get("demo.failed"), failure.Message);
        } finally {
            IsBusy = false;
        }
    }

    /// <summary>Tests the session's connection and opens it when the server answers and takes the token; otherwise shows why not.</summary>
    /// <param name="session">The session.</param>
    private async Task OpenAsync(Session session) {
        IsBusy = true;
        try {
            using var scope = ExchangeScope.Begin("ui:probe");
            var report = await ConnectionProbe.RunAsync(session.Client);
            if (!report.Reachable || !report.Authenticated) {
                Probe = new ProbeResultViewModel(report);
                session.Dispose();
                return;
            }

            session.Configuration = report.Configuration;
            Probe = null;
            _open(session, report);
        } finally {
            IsBusy = false;
        }
    }

    /// <summary>Applies the edits and saves them, remembering this profile as the one to start with next time.</summary>
    private ProfileViewModel? Store() {
        if (Selected is not { } profile) {
            return null;
        }

        profile.Apply();
        if (!_services.Settings.Profiles.Contains(profile.Profile)) {
            _services.Settings.Profiles.Add(profile.Profile);
        }

        _services.Settings.LastProfileId = profile.Profile.Id;
        _services.Save();
        return profile;
    }
}
