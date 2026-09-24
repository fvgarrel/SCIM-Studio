using System.Collections.ObjectModel;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScimStudio.App.Services;
using ScimStudio.Core.Http;
using ScimStudio.Core.Scim;

namespace ScimStudio.App.ViewModels;

/// <summary>An attribute the lists can be sorted by, as the server names it.</summary>
/// <param name="attribute">The attribute path, or null for the server's own order.</param>
/// <param name="key">The catalogue key of its label.</param>
public sealed class SortOption(string? attribute, string key) : ViewModelBase {
    public string? Attribute { get; } = attribute;

    public string Label => L.Get(key);
}

/// <summary>The two colours a monogram is drawn in, picked by the id so a resource keeps its colour from list to list.</summary>
public static class Monogram {
    private static readonly IBrush[] Palette = [
        new ImmutableSolidColorBrush(Color.Parse("#5B7CFA")),
        new ImmutableSolidColorBrush(Color.Parse("#2E9E83")),
        new ImmutableSolidColorBrush(Color.Parse("#C2703D")),
        new ImmutableSolidColorBrush(Color.Parse("#9A5BD6")),
        new ImmutableSolidColorBrush(Color.Parse("#D0578A")),
        new ImmutableSolidColorBrush(Color.Parse("#3F95C9")),
        new ImmutableSolidColorBrush(Color.Parse("#7E8B3A")),
        new ImmutableSolidColorBrush(Color.Parse("#B8524F")),
    ];

    public static IBrush Brush(string id) {
        ArgumentNullException.ThrowIfNull(id);

        var hash = 17;
        foreach (var character in id) {
            hash = unchecked((hash * 31) + character);
        }

        return Palette[(hash & int.MaxValue) % Palette.Length];
    }

    public static string Initials(string label) {
        ArgumentNullException.ThrowIfNull(label);

        var words = label.Split([' ', '.', '@', '-', '_'], StringSplitOptions.RemoveEmptyEntries);
        var initials = string.Concat(words.Take(2).Select(w => char.ToUpperInvariant(w[0])));
        return initials.Length > 0 ? initials : "?";
    }
}

/// <summary>A user as a row of the list.</summary>
public sealed partial class UserItemViewModel : ViewModelBase {
    public UserItemViewModel(ScimUser user) {
        User = user;
    }

    [ObservableProperty]
    public partial ScimUser User { get; set; }

    public string Id => User.Id;

    public string Label => User.Label;

    public string Secondary => User.Email is { } email && !string.Equals(email, User.UserName, StringComparison.OrdinalIgnoreCase)
        ? $"{User.UserName} · {email}"
        : User.UserName;

    public string Initials => Monogram.Initials(Label);

    public IBrush Brush => Monogram.Brush(Id);

    public bool IsInactive => User.Active == false;

    partial void OnUserChanged(ScimUser value) {
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>
/// The users page: a list read from the server a page at a time - searched there too, by a filter it can see under the search box - and the
/// editor of the one selected.
/// </summary>
public sealed partial class UsersViewModel : ViewModelBase, IActivatable, IDisposable {
    private const int PAGE_SIZE = 50;

    private readonly AppServices _services;
    private readonly Session _session;
    private readonly ShellViewModel _shell;
    private readonly DispatcherTimer _debounce;
    private CancellationTokenSource? _loading;
    private bool _loaded;

    /// <summary>The users page.</summary>
    /// <param name="services">What the interface shares.</param>
    /// <param name="session">The session.</param>
    /// <param name="shell">The session's pages, for moving between them.</param>
    public UsersViewModel(AppServices services, Session session, ShellViewModel shell) {
        _services = services;
        _session = session;
        _shell = shell;
        _debounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _debounce.Tick += (_, _) => {
            _debounce.Stop();
            _ = ReloadAsync();
        };

        SortOptions = [
            new SortOption(null, "sort.server"),
            new SortOption("userName", "sort.userName"),
            new SortOption("displayName", "sort.displayName"),
            new SortOption("meta.created", "sort.created"),
            new SortOption("meta.lastModified", "sort.modified"),
        ];
        Sort = SortOptions[0];
    }

    public ObservableCollection<UserItemViewModel> Items { get; } = [];

    public IReadOnlyList<SortOption> SortOptions { get; }

    public bool CanSort => _session.Configuration?.SortSupported != false;

    [ObservableProperty]
    public partial string Search { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool RawFilter { get; set; }

    [ObservableProperty]
    public partial SortOption Sort { get; set; }

    [ObservableProperty]
    public partial bool Descending { get; set; }

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
    public partial UserItemViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial UserEditorViewModel? Editor { get; set; }

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

    partial void OnSortChanged(SortOption value) {
        if (_loaded) {
            _ = ReloadAsync();
        }
    }

    partial void OnDescendingChanged(bool value) {
        if (_loaded && Sort.Attribute is not null) {
            _ = ReloadAsync();
        }
    }

    partial void OnIsLoadingChanged(bool value) {
        OnPropertyChanged(nameof(IsEmpty));
    }

    partial void OnSelectedChanged(UserItemViewModel? value) {
        if (value is not null) {
            Editor = UserEditorViewModel.Existing(_services, _session, _shell, this, value.User);
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
        Editor = UserEditorViewModel.New(_services, _session, _shell, this);
    }

    /// <summary>Selects a user, reading it from the server when it is not among the rows loaded.</summary>
    /// <param name="id">The user's id.</param>
    public async Task OpenAsync(string id) {
        Activate();

        if (Items.FirstOrDefault(item => item.Id == id) is { } row) {
            Selected = row;
            return;
        }

        try {
            using var scope = ExchangeScope.Begin("ui:user.open");
            var user = await _session.Client.GetUserAsync(id);
            Selected = null;
            Editor = UserEditorViewModel.Existing(_services, _session, _shell, this, user);
        } catch (Exception failure) when (failure is ScimException or HttpRequestException) {
            _services.Notifier.Error(L.Get("user.openFailed"), Describe(failure));
        }
    }

    /// <summary>Takes a saved user into the list: a new one on top, a changed one where it was.</summary>
    /// <param name="user">The user as the server answered.</param>
    /// <param name="created">Whether it was created rather than changed.</param>
    internal void Saved(ScimUser user, bool created) {
        if (Items.FirstOrDefault(item => item.Id == user.Id) is { } row) {
            row.User = user;
            return;
        }

        if (created) {
            var item = new UserItemViewModel(user);
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
        var query = new ScimQuery {
            Filter = Filter(),
            StartIndex = start,
            Count = PAGE_SIZE,
            SortBy = CanSort ? Sort.Attribute : null,
            SortOrder = CanSort && Sort.Attribute is not null ? (Descending ? "descending" : "ascending") : null,
        };

        AppliedFilter = query.Filter;
        IsLoading = true;
        Error = null;
        try {
            using var scope = ExchangeScope.Begin("ui:users.list");
            var page = await _session.Client.ListUsersAsync(query, cancellationToken);
            if (cancellationToken.IsCancellationRequested) {
                return;
            }

            foreach (var user in page.Resources) {
                Items.Add(new UserItemViewModel(user));
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

    public void Dispose() {
        _debounce.Stop();
        _loading?.Cancel();
        _loading?.Dispose();
    }

    /// <summary>The search as a filter: typed in as one, or looked for in the name, the userName and the address.</summary>
    private string? Filter() {
        var text = Search.Trim();
        if (text.Length == 0) {
            return null;
        }

        if (RawFilter) {
            return text;
        }

        return ScimFilterText.Any(
            ScimFilterText.Co("userName", text), ScimFilterText.Co("displayName", text), ScimFilterText.Co("emails.value", text));
    }
}
