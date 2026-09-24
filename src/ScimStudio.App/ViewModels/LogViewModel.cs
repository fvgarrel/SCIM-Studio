using System.Collections.ObjectModel;
using System.Text;
using Avalonia.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScimStudio.App.Services;
using ScimStudio.Core.Http;
using ScimStudio.Core.Scim;

namespace ScimStudio.App.ViewModels;

public enum ExchangeOutcome {
    Success,
    ClientError,
    ServerError,
    Failure,
}

/// <summary>An exchange as a row of the log and as the detail beside it.</summary>
/// <param name="exchange">The exchange.</param>
/// <param name="baseUrl">The session's base URL, which the row leaves off the path.</param>
public sealed class ExchangeItemViewModel(HttpExchange exchange, string baseUrl) : ViewModelBase {
    public HttpExchange Exchange { get; } = exchange;

    public string Method => Exchange.Method;

    public string Path {
        get {
            var url = Uri.UnescapeDataString(Exchange.Url.AbsoluteUri);
            return url.StartsWith(baseUrl, StringComparison.OrdinalIgnoreCase) ? url[baseUrl.Length..] : url;
        }
    }

    public string Url => Exchange.Url.AbsoluteUri;

    public string Status => Exchange.StatusCode?.ToString(L.Culture) ?? "—";

    public string StatusLine => Exchange.StatusCode is { } code ? $"{code} {Exchange.ReasonPhrase}" : L.Get("log.noAnswer");

    public ExchangeOutcome Outcome => Exchange.StatusCode switch {
        null => ExchangeOutcome.Failure,
        >= 500 => ExchangeOutcome.ServerError,
        >= 400 => ExchangeOutcome.ClientError,
        _ => ExchangeOutcome.Success,
    };

    public bool IsSuccess => Outcome == ExchangeOutcome.Success;

    public bool IsClientError => Outcome == ExchangeOutcome.ClientError;

    public bool IsServerError => Outcome is ExchangeOutcome.ServerError or ExchangeOutcome.Failure;

    public string Duration => $"{Exchange.Duration.TotalMilliseconds:0} ms";

    public string Time => Exchange.StartedAt.ToLocalTime().ToString("HH:mm:ss.fff", L.Culture);

    public string Origin => Describe(Exchange.Origin);

    public string? Failure => Exchange.Failure;

    public string RequestHeaders => Headers(Exchange.RequestHeaders);

    public string ResponseHeaders => Headers(Exchange.ResponseHeaders);

    public string RequestBody => ScimJson.Pretty(Exchange.RequestBody) ?? string.Empty;

    public string ResponseBody => ScimJson.Pretty(Exchange.ResponseBody) ?? string.Empty;

    public bool HasRequestBody => !string.IsNullOrEmpty(Exchange.RequestBody);

    public bool HasResponseBody => !string.IsNullOrEmpty(Exchange.ResponseBody);

    /// <summary>What a request was sent for, as the log names it: the action a person took, a check by its title, the generator.</summary>
    /// <param name="origin">The origin as the scope recorded it.</param>
    public static string Describe(string? origin) {
        if (string.IsNullOrEmpty(origin)) {
            return L.Get("origin.none");
        }

        var colon = origin.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0) {
            return origin;
        }

        var kind = origin[..colon];
        var name = origin[(colon + 1)..];
        return kind switch {
            "check" => L.Format("origin.check", L.Get($"check.{name}")),
            _ => L.Get($"origin.{kind}.{name}"),
        };
    }

    public bool Matches(string text) {
        return Path.Contains(text, StringComparison.OrdinalIgnoreCase)
            || Method.Contains(text, StringComparison.OrdinalIgnoreCase)
            || Status.Contains(text, StringComparison.OrdinalIgnoreCase)
            || Origin.Contains(text, StringComparison.OrdinalIgnoreCase);
    }

    private static string Headers(IReadOnlyList<KeyValuePair<string, string>> headers) {
        var text = new StringBuilder();
        foreach (var (name, value) in headers) {
            text.Append(name).Append(": ").Append(value).Append('\n');
        }

        return text.ToString().TrimEnd();
    }
}

/// <summary>
/// The log page: every exchange of the session, newest first, as it happened. Exchanges arrive from whatever thread sent them and are taken
/// in a few times a second, so a generator run of thousands does not queue a thousand redraws.
/// </summary>
public sealed partial class LogViewModel : ViewModelBase, IDisposable {
    private readonly AppServices _services;
    private readonly string _baseUrl;
    private readonly List<ExchangeItemViewModel> _all = [];
    private readonly Queue<HttpExchange> _incoming = new();
    private readonly Lock _gate = new();
    private readonly DispatcherTimer _flush;

    /// <summary>The log page.</summary>
    /// <param name="services">What the interface shares, the log among it.</param>
    /// <param name="session">The session, whose base URL the rows leave off.</param>
    public LogViewModel(AppServices services, Session session) {
        _services = services;
        _baseUrl = session.Client.Connection.BaseUrl.AbsoluteUri.TrimEnd('/');
        _flush = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _flush.Tick += (_, _) => Flush();
        _flush.Start();

        services.Log.Recorded += OnRecorded;
        services.Log.Cleared += OnCleared;
    }

    public event EventHandler? CountChanged;

    public ObservableCollection<ExchangeItemViewModel> Items { get; } = [];

    public int Count => _all.Count;

    [ObservableProperty]
    public partial ExchangeItemViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial string Search { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool ErrorsOnly { get; set; }

    [ObservableProperty]
    public partial int DetailTab { get; set; } = 1;

    public bool IsEmpty => Items.Count == 0;

    partial void OnSearchChanged(string value) {
        Refilter();
    }

    partial void OnErrorsOnlyChanged(bool value) {
        Refilter();
    }

    /// <summary>Selects an exchange, clearing a search that would hide it.</summary>
    /// <param name="exchange">The exchange.</param>
    public void Reveal(HttpExchange exchange) {
        Flush();

        var item = _all.Find(i => i.Exchange.Sequence == exchange.Sequence);
        if (item is null) {
            return;
        }

        if (!Items.Contains(item)) {
            Search = string.Empty;
            ErrorsOnly = false;
        }

        Selected = item;
        DetailTab = 1;
    }

    [RelayCommand]
    private void Clear() {
        _services.Log.Clear();
    }

    [RelayCommand]
    private async Task CopyCurlAsync() {
        if (Selected is not null) {
            await _services.CopyAsync(ShellCommand.Curl(Selected.Exchange));
        }
    }

    [RelayCommand]
    private async Task CopyPowerShellAsync() {
        if (Selected is not null) {
            await _services.CopyAsync(ShellCommand.PowerShell(Selected.Exchange));
        }
    }

    [RelayCommand]
    private async Task CopyResponseAsync() {
        if (Selected is not null) {
            await _services.CopyAsync(Selected.ResponseBody);
        }
    }

    [RelayCommand]
    private async Task CopyUrlAsync() {
        if (Selected is not null) {
            await _services.CopyAsync(Selected.Url);
        }
    }

    private void OnRecorded(object? sender, HttpExchange exchange) {
        lock (_gate) {
            _incoming.Enqueue(exchange);
        }
    }

    private void OnCleared(object? sender, EventArgs e) {
        Dispatcher.UIThread.Post(() => {
            lock (_gate) {
                _incoming.Clear();
            }

            _all.Clear();
            Items.Clear();
            Selected = null;
            OnPropertyChanged(nameof(IsEmpty));
            CountChanged?.Invoke(this, EventArgs.Empty);
        });
    }

    private void Flush() {
        List<HttpExchange> arrived;
        lock (_gate) {
            if (_incoming.Count == 0) {
                return;
            }

            arrived = [.. _incoming];
            _incoming.Clear();
        }

        foreach (var exchange in arrived) {
            var item = new ExchangeItemViewModel(exchange, _baseUrl);
            _all.Add(item);
            if (Accepts(item)) {
                Items.Insert(0, item);
            }
        }

        var overflow = _all.Count - ExchangeLog.CAPACITY;
        if (overflow > 0) {
            foreach (var old in _all.Take(overflow)) {
                Items.Remove(old);
            }

            _all.RemoveRange(0, overflow);
        }

        OnPropertyChanged(nameof(IsEmpty));
        CountChanged?.Invoke(this, EventArgs.Empty);
    }

    private void Refilter() {
        Items.Clear();
        for (var i = _all.Count - 1; i >= 0; i--) {
            if (Accepts(_all[i])) {
                Items.Add(_all[i]);
            }
        }

        OnPropertyChanged(nameof(IsEmpty));
    }

    private bool Accepts(ExchangeItemViewModel item) {
        if (ErrorsOnly && item.IsSuccess) {
            return false;
        }

        var text = Search.Trim();
        return text.Length == 0 || item.Matches(text);
    }

    public void Dispose() {
        _flush.Stop();
        _services.Log.Recorded -= OnRecorded;
        _services.Log.Cleared -= OnCleared;
    }
}
