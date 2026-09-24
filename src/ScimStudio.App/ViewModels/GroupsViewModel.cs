using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScimStudio.App.Services;
using ScimStudio.Core.Http;
using ScimStudio.Core.Scim;

namespace ScimStudio.App.ViewModels;

/// <summary>A group as a row of the list. Listed without members, as identity providers list them.</summary>
public sealed partial class GroupItemViewModel : ViewModelBase {
    public GroupItemViewModel(ScimGroup group) {
        Group = group;
    }

    [ObservableProperty]
    public partial ScimGroup Group { get; set; }

    public string Id => Group.Id;

    public string Name => Group.DisplayName;

    public string? Secondary => Group.ExternalId;

    public string Initials => Monogram.Initials(Name);

    public IBrush Brush => Monogram.Brush(Id);

    partial void OnGroupChanged(ScimGroup value) {
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>The groups page, read like the users page: from the server, a page at a time, searched there.</summary>
public sealed partial class GroupsViewModel : ViewModelBase, IActivatable, IDisposable {
    private const int PAGE_SIZE = 50;

    private readonly AppServices _services;
    private readonly Session _session;
    private readonly ShellViewModel _shell;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _loading;
    private bool _loaded;

    /// <summary>The groups page.</summary>
    /// <param name="services">What the interface shares.</param>
    /// <param name="session">The session.</param>
    /// <param name="shell">The session's pages, for moving between them.</param>
    public GroupsViewModel(AppServices services, Session session, ShellViewModel shell) {
        _services = services;
        _session = session;
        _shell = shell;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _debounce.Tick += (_, _) => {
            _debounce.Stop();
            _ = ReloadAsync();
        };
    }

    public ObservableCollection<GroupItemViewModel> Items { get; } = [];

    [ObservableProperty]
    public partial string Search { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool RawFilter { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(CountText), nameof(CanLoadMore))]
    public partial int Total { get; set; }

    [ObservableProperty]
    public partial bool IsLoading { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    [ObservableProperty]
    public partial string? AppliedFilter { get; set; }

    [ObservableProperty]
    public partial GroupItemViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial GroupEditorViewModel? Editor { get; set; }

    public bool CanLoadMore => Items.Count < Total;

    public string CountText => L.Format("list.count", Items.Count, Total);

    public bool IsEmpty => !IsLoading && Error is null && Items.Count == 0;

    public void Activate() {
        if (!_loaded) {
            _loaded = true;
            _ = ReloadAsync();
        }
    }

    partial void OnSearchChanged(string value) {
        _debounce.Stop();
        _debounce.Start();
    }

    partial void OnRawFilterChanged(bool value) {
        if (!string.IsNullOrWhiteSpace(Search)) {
            _ = ReloadAsync();
        }
    }

    partial void OnIsLoadingChanged(bool value) {
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnSelectedChanged(GroupItemViewModel? value) {
        if (value is not null) {
            Editor = GroupEditorViewModel.Existing(_services, _session, _shell, this, value.Group);
        }
    }

    [RelayCommand]
    private Task RefreshAsync() {
        return ReloadAsync();
    }

    [RelayCommand]
    private async Task LoadMoreAsync() {
        await LoadAsync(Items.Count + 1, _loading?.Token ?? CancellationToken.None);
    }

    [RelayCommand]
    private void New() {
        Selected = null;
        Editor = GroupEditorViewModel.New(_services, _session, _shell, this);
    }

    public async Task OpenAsync(string id) {
        Activate();

        if (Items.FirstOrDefault(item => item.Id == id) is { } row) {
            Selected = row;
            return;
        }

        try {
            using var scope = ExchangeScope.Begin("ui:group.open");
            var group = await _session.Client.GetGroupAsync(id);
            Selected = null;
            Editor = GroupEditorViewModel.Existing(_services, _session, _shell, this, group);
        } catch (Exception failure) when (failure is ScimException or HttpRequestException) {
            _services.Notifier.Error(L.Get("group.openFailed"), Describe(failure));
        }
    }

    internal void Saved(ScimGroup group, bool created) {
        if (Items.FirstOrDefault(item => item.Id == group.Id) is { } row) {
            row.Group = group;
            return;
        }

        if (created) {
            var item = new GroupItemViewModel(group);
            Items.Insert(0, item);
            Total++;
            Selected = item;
        }
    }

    internal void Deleted(string id) {
        if (Items.FirstOrDefault(item => item.Id == id) is { } row) {
            Items.Remove(row);
            Total = Math.Max(0, Total - 1);
        }

        Selected = null;
        Editor = null;
        OnPropertyChanged(nameof(IsEmpty));
    }

    public void Dispose() {
        _debounce.Stop();
        _loading?.Cancel();
        _loading?.Dispose();
    }

    private async Task ReloadAsync() {
        if (_loading is not null) {
            await _loading.CancelAsync();
        }

        _loading = new CancellationTokenSource();
        Items.Clear();
        Total = 0;
        await LoadAsync(1, _loading.Token);
    }

    private async Task LoadAsync(int start, CancellationToken cancellationToken) {
        var text = Search.Trim();
        var query = new ScimQuery {
            Filter = text.Length == 0 ? null : RawFilter ? text : ScimFilterText.Co("displayName", text),
            StartIndex = start,
            Count = PAGE_SIZE,
            ExcludedAttributes = ["members"],
        };

        AppliedFilter = query.Filter;
        IsLoading = true;
        Error = null;
        try {
            using var scope = ExchangeScope.Begin("ui:groups.list");
            var page = await _session.Client.ListGroupsAsync(query, cancellationToken);
            if (cancellationToken.IsCancellationRequested) {
                return;
            }

            foreach (var group in page.Resources) {
                Items.Add(new GroupItemViewModel(group));
            }

            Total = Math.Max(page.TotalResults, Items.Count);
        } catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            // A newer search took over.
        } catch (Exception failure) when (failure is ScimException or HttpRequestException or TaskCanceledException) {
            Error = Describe(failure);
        } finally {
            // A load a newer one replaced leaves the indicator to the newer one.
            if (!cancellationToken.IsCancellationRequested) {
                IsLoading = false;
            }

            OnPropertyChanged(nameof(CountText));
            OnPropertyChanged(nameof(CanLoadMore));
        }
    }
}
