using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScimStudio.App.Services;
using ScimStudio.Core.Http;
using ScimStudio.Core.Scim;

namespace ScimStudio.App.ViewModels;

/// <summary>A member of the group being edited, or a user that could become one.</summary>
/// <param name="id">The user's id.</param>
/// <param name="name">The user's name, as the group or the search gave it.</param>
/// <param name="detail">A second line: the userName, where it is known.</param>
public sealed class MemberViewModel(string id, string name, string? detail) {
    public string Id { get; } = id;

    public string Name { get; } = name;

    public string? Detail { get; } = detail;

    public string Initials => Monogram.Initials(Name);

    public IBrush Brush => Monogram.Brush(Id);
}

/// <summary>
/// One group, new or as the server has it. A new group collects its members and is created with them; an existing one sends each added or
/// removed member at once, through the session's dialect, the way a provider pushes membership.
/// </summary>
public sealed partial class GroupEditorViewModel : ViewModelBase {
    private readonly AppServices _services;
    private readonly Session _session;
    private readonly ShellViewModel _shell;
    private readonly GroupsViewModel _owner;
    private readonly DispatcherTimer _debounce;
    private ScimGroup? _group;

    private GroupEditorViewModel(AppServices services, Session session, ShellViewModel shell, GroupsViewModel owner) {
        _services = services;
        _session = session;
        _shell = shell;
        _owner = owner;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) => {
            _debounce.Stop();
            _ = SearchCandidatesAsync();
        };

        Members.CollectionChanged += (_, _) => {
            OnPropertyChanged(nameof(MemberCount));
            OnPropertyChanged(nameof(HasMembers));
        };
    }

    public static GroupEditorViewModel New(AppServices services, Session session, ShellViewModel shell, GroupsViewModel owner) {
        return new GroupEditorViewModel(services, session, shell, owner);
    }

    public static GroupEditorViewModel Existing(AppServices services, Session session, ShellViewModel shell, GroupsViewModel owner, ScimGroup group) {
        var editor = new GroupEditorViewModel(services, session, shell, owner);
        editor.Show(group);
        _ = editor.ReloadAsync();
        return editor;
    }

    public bool IsNew => _group is null;

    public bool IsExisting => _group is not null;

    public string Title => _group?.DisplayName ?? L.Get("group.new");

    public string? Id => _group?.Id;

    public string Created => Moment(_group?.Created);

    public string Modified => Moment(_group?.LastModified);

    public string Json => _group?.Resource.ToJsonString(ScimJson.Readable) ?? string.Empty;

    public ObservableCollection<MemberViewModel> Members { get; } = [];

    public ObservableCollection<MemberViewModel> Candidates { get; } = [];

    public string MemberCount => L.Format("group.memberCount", Members.Count);

    public bool HasMembers => Members.Count > 0;

    public string Initials => Monogram.Initials(Title);

    public IBrush? Brush => _group is null ? null : Monogram.Brush(_group.Id);

    public string SaveLabel => L.Get(IsNew ? "group.create" : "common.save");

    public string MembersHint => L.Get(IsNew ? "group.membersHintNew" : "group.membersHint");

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand))]
    [NotifyPropertyChangedFor(nameof(NameMissing))]
    public partial string DisplayName { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string ExternalId { get; set; } = string.Empty;

    [ObservableProperty]
    public partial string CandidateSearch { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SaveCommand), nameof(DeleteCommand))]
    public partial bool IsBusy { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool NameMissing => string.IsNullOrWhiteSpace(DisplayName);

    partial void OnCandidateSearchChanged(string value) {
        _debounce.Stop();
        _debounce.Start();
    }

    private bool CanSave() {
        return !IsBusy && !NameMissing;
    }

    private bool CanChange() {
        return !IsBusy && _group is not null;
    }

    [RelayCommand(CanExecute = nameof(CanSave))]
    private async Task SaveAsync() {
        var draft = new GroupDraft {
            DisplayName = DisplayName,
            ExternalId = ExternalId,
            Members = [.. Members.Select(m => new ScimMember(m.Id, m.Name, "User"))],
        };

        await RunAsync(IsNew ? "ui:group.create" : "ui:group.update", async () => {
            if (_group is null) {
                var result = await _session.Dialect.CreateGroupAsync(_session.Client, draft, CancellationToken.None);
                Show(result.Resource);
                _owner.Saved(result.Resource, created: true);

                if (result.Matched) {
                    _services.Notifier.Warning(L.Get("group.matched"), L.Get("group.matchedDetail"));
                } else {
                    _services.Notifier.Success(L.Format("group.created", result.Resource.DisplayName));
                }

                await ReloadAsync();
                return;
            }

            await _session.Dialect.UpdateGroupAsync(_session.Client, _group, draft, CancellationToken.None);
            _services.Notifier.Success(L.Format("group.saved", draft.DisplayName.Trim()));
            await ReloadAsync();
        });
    }

    [RelayCommand]
    private void Revert() {
        if (_group is null) {
            DisplayName = ExternalId = string.Empty;
            Members.Clear();
        } else {
            Show(_group);
        }

        Error = null;
    }

    [RelayCommand(CanExecute = nameof(CanChange))]
    private async Task DeleteAsync() {
        if (_group is not { } group) {
            return;
        }

        var confirmed = await _services.Dialogs.ConfirmAsync(
            L.Get("group.deleteTitle"), L.Format("group.deleteMessage", group.DisplayName), L.Get("common.delete"), danger: true);
        if (!confirmed) {
            return;
        }

        await RunAsync("ui:group.delete", async () => {
            await _session.Dialect.DeleteGroupAsync(_session.Client, group, CancellationToken.None);
            _owner.Deleted(group.Id);
            _services.Notifier.Success(L.Format("group.deleted", group.DisplayName));
        });
    }

    [RelayCommand]
    private void GenerateExternalId() {
        ExternalId = Guid.NewGuid().ToString();
    }

    [RelayCommand]
    private async Task CopyIdAsync() {
        if (_group is not null) {
            await _services.CopyAsync(_group.Id);
        }
    }

    [RelayCommand]
    private async Task CopyJsonAsync() {
        await _services.CopyAsync(Json);
    }

    [RelayCommand]
    private void OpenMember(MemberViewModel member) {
        _shell.ShowUser(member.Id);
    }

    [RelayCommand]
    private async Task AddMemberAsync(MemberViewModel candidate) {
        if (Members.Any(m => m.Id == candidate.Id)) {
            return;
        }

        if (_group is not { } group) {
            Members.Add(candidate);
            Candidates.Remove(candidate);
            return;
        }

        await RunAsync("ui:group.addMember", async () => {
            var member = new ScimMember(candidate.Id, candidate.Name, "User");
            await _session.Dialect.AddMembersAsync(_session.Client, group, [member], CancellationToken.None);
            Candidates.Remove(candidate);
            _services.Notifier.Success(L.Format("user.joined", candidate.Name, group.DisplayName));
            await ReloadAsync();
        });
    }

    [RelayCommand]
    private async Task RemoveMemberAsync(MemberViewModel member) {
        if (_group is not { } group) {
            Members.Remove(member);
            return;
        }

        await RunAsync("ui:group.removeMember", async () => {
            await _session.Dialect.RemoveMembersAsync(_session.Client, group, [member.Id], CancellationToken.None);
            _services.Notifier.Success(L.Format("user.left", member.Name, group.DisplayName));
            await ReloadAsync();
        });
    }

    private async Task SearchCandidatesAsync() {
        var text = CandidateSearch.Trim();
        Candidates.Clear();
        if (text.Length == 0) {
            return;
        }

        try {
            using var scope = ExchangeScope.Begin("ui:users.search");
            var filter = ScimFilterText.Any(ScimFilterText.Co("userName", text), ScimFilterText.Co("displayName", text));
            var page = await _session.Client.ListUsersAsync(new ScimQuery { Filter = filter, Count = 20 });
            foreach (var user in page.Resources.Where(u => Members.All(m => m.Id != u.Id))) {
                Candidates.Add(new MemberViewModel(user.Id, user.Label, user.UserName));
            }
        } catch (Exception failure) when (failure is ScimException or HttpRequestException) {
            Error = Describe(failure);
        }
    }

    private async Task ReloadAsync() {
        if (_group is not { } group) {
            return;
        }

        try {
            using var scope = ExchangeScope.Begin("ui:group.get");
            var fresh = await _session.Client.GetGroupAsync(group.Id);
            Show(fresh);
            _owner.Saved(fresh, created: false);
        } catch (Exception failure) when (failure is ScimException or HttpRequestException) {
            Error = Describe(failure);
        }
    }

    private void Show(ScimGroup group) {
        _group = group;
        DisplayName = group.DisplayName;
        ExternalId = group.ExternalId ?? string.Empty;

        Members.Clear();
        foreach (var member in group.Members) {
            Members.Add(new MemberViewModel(member.Id, member.Display ?? member.Id, member.Type));
        }

        OnPropertyChanged(string.Empty);
        DeleteCommand.NotifyCanExecuteChanged();
    }

    private async Task RunAsync(string origin, Func<Task> change) {
        IsBusy = true;
        Error = null;
        try {
            using var scope = ExchangeScope.Begin(origin);
            await change();
        } catch (Exception failure) when (failure is ScimException or HttpRequestException or TaskCanceledException) {
            Error = Describe(failure);
            _services.Notifier.Error(L.Get("group.failed"), Error);
        } finally {
            IsBusy = false;
        }
    }
}
