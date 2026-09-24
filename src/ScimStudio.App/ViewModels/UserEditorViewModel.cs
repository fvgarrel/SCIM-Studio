using System.Collections.ObjectModel;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScimStudio.App.Services;
using ScimStudio.Core.Generation;
using ScimStudio.Core.Http;
using ScimStudio.Core.Scim;

namespace ScimStudio.App.ViewModels;

/// <summary>
/// A group as a chip: one the user is in, or one the user can be added to. It carries what clicking it does, since a chip in a flyout sits
/// in a tree of its own, where a binding climbing to the editor would find nothing.
/// </summary>
/// <param name="id">The group's id.</param>
/// <param name="name">The group's name.</param>
/// <param name="command">What clicking the chip does, given the chip.</param>
/// <param name="remove">What its cross does, given the chip; null for a chip without one.</param>
public sealed class GroupChipViewModel(string id, string name, ICommand command, ICommand? remove = null) {
    public string Id { get; } = id;

    public string Name { get; } = name;

    public ICommand Command { get; } = command;

    public ICommand? Remove { get; } = remove;
}

/// <summary>
/// One user, new or as the server has it. Every change goes out through the session's dialect, so the same form writes RFC 7644, Entra ID or
/// Okta requests; the server's answer replaces what the form shows.
/// </summary>
public sealed partial class UserEditorViewModel : ViewModelBase {
    private readonly AppServices _services;
    private readonly Session _session;
    private readonly ShellViewModel _shell;
    private readonly UsersViewModel _owner;
    private ScimUser? _user;

    private UserEditorViewModel(AppServices services, Session session, ShellViewModel shell, UsersViewModel owner) {
        _services = services;
        _session = session;
        _shell = shell;
        _owner = owner;
    }

    public static UserEditorViewModel New(AppServices services, Session session, ShellViewModel shell, UsersViewModel owner) {
        return new UserEditorViewModel(services, session, shell, owner) { Active = true };
    }

    /// <summary>An editor for a user, filled from the row at once and then from a fresh read, which brings the groups.</summary>
    /// <param name="services">What the interface shares.</param>
    /// <param name="session">The session.</param>
    /// <param name="shell">The session's pages.</param>
    /// <param name="owner">The list it belongs to.</param>
    /// <param name="user">The user as listed.</param>
    public static UserEditorViewModel Existing(AppServices services, Session session, ShellViewModel shell, UsersViewModel owner, ScimUser user) {
        var editor = new UserEditorViewModel(services, session, shell, owner);
        editor.Show(user);
        _ = editor.ReloadAsync();
        return editor;
    }

    public bool IsNew => _user is null;

    public bool IsExisting => _user is not null;

    public string Title => _user?.Label ?? L.Get("user.new");

    public string? Id => _user?.Id;

    public string Created => Moment(_user?.Created);

    public string Modified => Moment(_user?.LastModified);

    public string? Location => _user?.Location;

    public string Json => _user?.Resource.ToJsonString(ScimJson.Readable) ?? string.Empty;

    public bool IsActiveOnServer => _user?.Active != false;

    public string Initials => Monogram.Initials(Title);

    public Avalonia.Media.IBrush? Brush => _user is null ? null : Monogram.Brush(_user.Id);

    public string SaveLabel => L.Get(IsNew ? "user.create" : "common.save");

    public string ToggleLabel => L.Get(IsActiveOnServer ? "user.deactivate" : "user.activate");

    public string StateLabel => L.Get(IsActiveOnServer ? "user.active" : "user.inactive");

    public ObservableCollection<GroupChipViewModel> Groups { get; } = [];

    public ObservableCollection<GroupChipViewModel> JoinableGroups { get; } = [];

    public bool HasGroups => Groups.Count > 0;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(UserNameMissing))]
    public partial string UserName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ExternalId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string GivenName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string FamilyName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string Email { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool Active { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DeleteCommand), nameof(ToggleActiveCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool UserNameMissing => string.IsNullOrWhiteSpace(UserName);

    private UserDraft Draft => new() {
        UserName = UserName,
        ExternalId = ExternalId,
        GivenName = GivenName,
        FamilyName = FamilyName,
        DisplayName = DisplayName,
        Email = Email,
        Active = Active,
    };

    private bool CanSave() {
        return !IsBusy && !UserNameMissing;
    }

    private bool CanChange() {
        return !IsBusy && _user is not null;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync() {
        await RunAsync(IsNew ? "ui:user.create" : "ui:user.update", async () => {
            if (_user is null) {
                var result = await _session.Dialect.CreateUserAsync(_session.Client, Draft, CancellationToken.None);
                Show(result.Resource);
                _owner.Saved(result.Resource, created: true);

                if (result.Matched) {
                    _services.Notifier.Warning(L.Get("user.matched"), L.Get("user.matchedDetail"));
                } else {
                    _services.Notifier.Success(L.Format("user.created", result.Resource.Label));
                }

                await ReloadAsync();
                return;
            }

            var updated = await _session.Dialect.UpdateUserAsync(_session.Client, _user, Draft, CancellationToken.None);
            Show(updated);
            _owner.Saved(updated, created: false);
            _services.Notifier.Success(L.Format("user.saved", updated.Label));
        });
    }

    [RelayCommand]
    private void Revert() {
        if (_user is null) {
            UserName = ExternalId = GivenName = FamilyName = DisplayName = Email = string.Empty;
            Active = true;
        } else {
            Show(_user);
        }

        Error = null;
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task ToggleActiveAsync() {
        if (_user is not { } user) {
            return;
        }

        var active = user.Active == false;
        await RunAsync(active ? "ui:user.activate" : "ui:user.deactivate", async () => {
            var switched = await _session.Dialect.SetActiveAsync(_session.Client, user, active, CancellationToken.None);
            Show(switched);
            _owner.Saved(switched, created: false);
            _services.Notifier.Success(L.Format(active ? "user.activated" : "user.deactivated", switched.Label));
        });
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task DeleteAsync() {
        if (_user is not { } user) {
            return;
        }

        var confirmed = await _services.Dialogs.ConfirmAsync(
            L.Get("user.deleteTitle"), L.Format("user.deleteMessage", user.Label), L.Get("common.delete"), danger: true);
        if (!confirmed) {
            return;
        }

        await RunAsync("ui:user.delete", async () => {
            await _session.Dialect.DeleteUserAsync(_session.Client, user, CancellationToken.None);
            _owner.Deleted(user.Id);
            _services.Notifier.Success(L.Format("user.deleted", user.Label));
        });
    }

    [RelayCommand]
    private void GenerateExternalId() {
        ExternalId = Guid.NewGuid().ToString();
    }

    [RelayCommand]
    private void FillRandom() {
        var person = TestDataGenerator.Person(Random.Shared);
        UserName = person.UserName;
        GivenName = person.GivenName ?? string.Empty;
        FamilyName = person.FamilyName ?? string.Empty;
        DisplayName = person.DisplayName ?? string.Empty;
        Email = person.Email ?? string.Empty;
        if (string.IsNullOrWhiteSpace(ExternalId)) {
            GenerateExternalId();
        }
    }

    [RelayCommand]
    private async Task CopyIdAsync() {
        if (_user is not null) {
            await _services.CopyAsync(_user.Id);
        }
    }

    [RelayCommand]
    private async Task CopyJsonAsync() {
        await _services.CopyAsync(Json);
    }

    [RelayCommand]
    private void OpenGroup(GroupChipViewModel chip) {
        _shell.ShowGroup(chip.Id);
    }

    /// <summary>Lists the groups the user is not in, for the menu that adds the user to one.</summary>
    [RelayCommand]
    private async Task LoadJoinableGroupsAsync() {
        try {
            using var scope = ExchangeScope.Begin("ui:groups.list");
            var page = await _session.Client.ListGroupsAsync(new ScimQuery { Count = 200, ExcludedAttributes = ["members"] });
            JoinableGroups.Clear();
            var joinable = page.Resources
                .Where(g => Groups.All(member => member.Id != g.Id))
                .OrderBy(g => g.DisplayName, StringComparer.CurrentCulture);
            foreach (var group in joinable) {
                JoinableGroups.Add(new GroupChipViewModel(group.Id, group.DisplayName, JoinCommand));
            }
        } catch (Exception failure) when (failure is ScimException or HttpRequestException) {
            Error = Describe(failure);
        }
    }

    [RelayCommand]
    private async Task JoinAsync(GroupChipViewModel chip) {
        if (_user is not { } user) {
            return;
        }

        await RunAsync("ui:group.addMember", async () => {
            var group = new ScimGroup { Id = chip.Id, DisplayName = chip.Name, Resource = [] };
            await _session.Dialect.AddMembersAsync(_session.Client, group, [new ScimMember(user.Id, user.Label, "User")], CancellationToken.None);
            _services.Notifier.Success(L.Format("user.joined", user.Label, chip.Name));
            await ReloadAsync();
        });
    }

    [RelayCommand]
    private async Task LeaveAsync(GroupChipViewModel chip) {
        if (_user is not { } user) {
            return;
        }

        await RunAsync("ui:group.removeMember", async () => {
            var group = new ScimGroup { Id = chip.Id, DisplayName = chip.Name, Resource = [] };
            await _session.Dialect.RemoveMembersAsync(_session.Client, group, [user.Id], CancellationToken.None);
            _services.Notifier.Success(L.Format("user.left", user.Label, chip.Name));
            await ReloadAsync();
        });
    }

    /// <summary>Reads the user again; a server answering a list without groups answers a single read with them.</summary>
    private async Task ReloadAsync() {
        if (_user is not { } user) {
            return;
        }

        try {
            using var scope = ExchangeScope.Begin("ui:user.get");
            var fresh = await _session.Client.GetUserAsync(user.Id);
            Show(fresh);
            _owner.Saved(fresh, created: false);
        } catch (Exception failure) when (failure is ScimException or HttpRequestException) {
            Error = Describe(failure);
        }
    }

    private void Show(ScimUser user) {
        _user = user;
        UserName = user.UserName;
        ExternalId = user.ExternalId ?? string.Empty;
        GivenName = user.GivenName ?? string.Empty;
        FamilyName = user.FamilyName ?? string.Empty;
        DisplayName = user.DisplayName ?? string.Empty;
        Email = user.Email ?? string.Empty;
        Active = user.Active ?? true;

        Groups.Clear();
        foreach (var group in user.Groups) {
            Groups.Add(new GroupChipViewModel(group.Id, group.Display ?? group.Id, OpenGroupCommand, LeaveCommand));
        }

        OnPropertyChanged(string.Empty);
        ToggleActiveCommand.NotifyCanExecuteChanged();
        DeleteCommand.NotifyCanExecuteChanged();
    }

    /// <summary>Runs a change, turning a failure into the error the editor shows and a toast that leads to the request in the log.</summary>
    /// <param name="origin">What the requests are for, as the log names them.</param>
    /// <param name="change">The change.</param>
    private async Task RunAsync(string origin, Func<Task> change) {
        IsBusy = true;
        Error = null;
        try {
            using var scope = ExchangeScope.Begin(origin);
            await change();
        } catch (Exception failure) when (failure is ScimException or HttpRequestException or TaskCanceledException) {
            Error = Describe(failure);
            _services.Notifier.Error(L.Get("user.failed"), Error);
        } finally {
            IsBusy = false;
        }
    }
}
