using System.Diagnostics;
using System.Text;
using Avalonia.Platform.Storage;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScimStudio.App.Services;
using ScimStudio.Core.Checks;
using ScimStudio.Core.Http;
using ScimStudio.Core.Text;

namespace ScimStudio.App.ViewModels;

/// <summary>A check as a row: its verdict, why, and the requests it sent - and the ways to copy them.</summary>
/// <param name="info">The check.</param>
/// <param name="page">The checks page, which knows the run a copy tells of.</param>
public sealed partial class CheckItemViewModel(CheckInfo info, ChecksViewModel page) : ViewModelBase {
    private IReadOnlyList<Message> _notes = [];

    public CheckInfo Info { get; } = info;

    /// <summary>The verdict as the suite reported it, which a copy or a report is written from.</summary>
    public CheckResult Result { get; private set; } = new() { Check = info, Status = CheckStatus.Pending };

    public string Title => L.Get($"check.{Info.Id}");

    public string Reference => Info.Reference;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(StatusLabel), nameof(IsPassed), nameof(IsFailed), nameof(IsWarning), nameof(IsSkipped), nameof(IsRunning))]
    [NotifyPropertyChangedFor(nameof(IsUnsupported), nameof(IsProblem), nameof(IsDone))]
    public partial CheckStatus Status { get; set; }

    /// <summary>Whether the row is in the list, which can be narrowed to the problems.</summary>
    [ObservableProperty]
    public partial bool IsShown { get; set; } = true;

    [ObservableProperty]
    public partial string? Duration { get; set; }

    [ObservableProperty]
    public partial bool IsSelected { get; set; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasExchanges))]
    public partial IReadOnlyList<HttpExchange> Exchanges { get; set; } = [];

    public bool HasExchanges => Exchanges.Count > 0;

    public IReadOnlyList<string> Notes => [.. _notes.Select(L.Format)];

    public bool HasNotes => _notes.Count > 0;

    public string? FirstNote => _notes.Count > 0 ? L.Format(_notes[0]) : null;

    public string StatusLabel => L.Get($"status.{Status.ToString().ToLowerInvariant()}");

    public bool IsPassed => Status == CheckStatus.Passed;

    public bool IsFailed => Status == CheckStatus.Failed;

    public bool IsWarning => Status == CheckStatus.Warning;

    public bool IsSkipped => Status == CheckStatus.Skipped;

    public bool IsUnsupported => Status == CheckStatus.Unsupported;

    public bool IsRunning => Status == CheckStatus.Running;

    /// <summary>A failure or a warning: something to look at on the server.</summary>
    public bool IsProblem => Status is CheckStatus.Failed or CheckStatus.Warning;

    /// <summary>Whether the check has a verdict to copy.</summary>
    public bool IsDone => Status is not (CheckStatus.Pending or CheckStatus.Running);

    public void Apply(CheckResult result) {
        ArgumentNullException.ThrowIfNull(result);

        Result = result;
        _notes = result.Notes;
        Status = result.Status;
        Exchanges = result.Exchanges;
        Duration = result.Status is CheckStatus.Running or CheckStatus.Pending ? null : CheckReport.Duration(result.Duration);
        OnPropertyChanged(nameof(Notes));
        OnPropertyChanged(nameof(HasNotes));
        OnPropertyChanged(nameof(FirstNote));
    }

    public void Reset() {
        Apply(new CheckResult { Check = Info, Status = CheckStatus.Pending });
    }

    [RelayCommand]
    private Task CopyResultAsync() {
        return page.CopyAsync(CheckReport.Markdown(page.Snapshot(), Result));
    }

    [RelayCommand]
    private Task CopyJsonAsync() {
        return page.CopyAsync(CheckReport.Json(page.Snapshot(), Result));
    }

    [RelayCommand]
    private Task CopyCurlAsync() {
        return page.CopyAsync(CheckReport.Commands(Result, ShellCommand.Curl));
    }

    [RelayCommand]
    private Task CopyPowerShellAsync() {
        return page.CopyAsync(CheckReport.Commands(Result, ShellCommand.PowerShell));
    }
}

/// <summary>The checks of one category, under its heading.</summary>
/// <param name="key">The category.</param>
/// <param name="items">Its checks.</param>
public sealed partial class CheckCategoryViewModel(string key, IReadOnlyList<CheckItemViewModel> items) : ViewModelBase {
    public string Title => L.Get($"category.{key}");

    public IReadOnlyList<CheckItemViewModel> Items { get; } = items;

    /// <summary>Whether any of its checks is in the list; a heading over nothing is left out.</summary>
    [ObservableProperty]
    public partial bool IsShown { get; set; } = true;
}

/// <summary>The checks page: the conformance suite run against the session's server, its verdicts as they come in.</summary>
public sealed partial class ChecksViewModel : ViewModelBase, IDisposable {
    private readonly AppServices _services;
    private readonly Session _session;
    private readonly ShellViewModel _shell;
    private readonly Dictionary<string, CheckItemViewModel> _items;
    private CancellationTokenSource? _run;
    private DateTimeOffset _startedAt = DateTimeOffset.Now;
    private TimeSpan _elapsed;
    private bool _cancelled;

    /// <summary>The checks page.</summary>
    /// <param name="services">What the interface shares.</param>
    /// <param name="session">The session.</param>
    /// <param name="shell">The session's pages, for opening a check's requests in the log.</param>
    public ChecksViewModel(AppServices services, Session session, ShellViewModel shell) {
        _services = services;
        _session = session;
        _shell = shell;

        _items = ConformanceSuite.Checks.ToDictionary(check => check.Id, check => new CheckItemViewModel(check, this));
        Categories = [.. ConformanceSuite.Checks
            .GroupBy(check => check.Category)
            .Select(group => new CheckCategoryViewModel(group.Key, [.. group.Select(check => _items[check.Id])]))];

        foreach (var item in _items.Values) {
            item.Reset();
        }
    }

    public IReadOnlyList<CheckCategoryViewModel> Categories { get; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(RunCommand), nameof(CancelCommand), nameof(ExportCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial CheckItemViewModel? Selected { get; set; }

    [ObservableProperty]
    public partial int Passed { get; set; }

    [ObservableProperty]
    public partial int Warnings { get; set; }

    [ObservableProperty]
    public partial int Failed { get; set; }

    [ObservableProperty]
    public partial int Unsupported { get; set; }

    [ObservableProperty]
    public partial int Skipped { get; set; }

    /// <summary>Narrows the list to failures and warnings.</summary>
    [ObservableProperty]
    public partial bool OnlyProblems { get; set; }

    /// <summary>The list is narrowed to problems and a finished run found none.</summary>
    [ObservableProperty]
    public partial bool NothingShown { get; set; }

    [ObservableProperty]
    public partial int Completed { get; set; }

    [ObservableProperty]
    public partial string? Elapsed { get; set; }

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(ExportCommand))]
    public partial bool HasRun { get; set; }

    public int Total => _items.Count;

    public double Progress => Total == 0 ? 0 : (double)Completed / Total;

    public string About => L.Format("checks.about", Total);

    partial void OnCompletedChanged(int value) {
        OnPropertyChanged(nameof(Progress));
    }

    partial void OnOnlyProblemsChanged(bool value) {
        Refilter();
    }

    partial void OnIsRunningChanged(bool value) {
        Refilter();
    }

    partial void OnSelectedChanged(CheckItemViewModel? oldValue, CheckItemViewModel? newValue) {
        if (oldValue is not null) {
            oldValue.IsSelected = false;
        }

        if (newValue is not null) {
            newValue.IsSelected = true;
        }
    }

    [RelayCommand]
    private void Select(CheckItemViewModel item) {
        Selected = item;
    }

    private bool CanRun() {
        return !IsRunning;
    }

    private bool CanCancel() {
        return IsRunning;
    }

    private bool CanExport() {
        return HasRun && !IsRunning;
    }

    [RelayCommand(CanExecute = nameof(CanRun))]
    private async Task RunAsync() {
        foreach (var item in _items.Values) {
            item.Reset();
        }

        Passed = Warnings = Failed = Unsupported = Skipped = Completed = 0;
        HasRun = true;
        IsRunning = true;
        Elapsed = null;
        _startedAt = DateTimeOffset.Now;
        _cancelled = false;

        _run = new CancellationTokenSource();
        var started = Stopwatch.GetTimestamp();
        var progress = new Progress<CheckResult>(Report);
        try {
            await Task.Run(() => ConformanceSuite.RunAsync(_session.Client, progress, _run.Token));
            _services.Notifier.Info(L.Format("checks.finished", Passed, Total, Failed, Unsupported));
        } catch (OperationCanceledException) {
            _cancelled = true;
            _services.Notifier.Info(L.Get("checks.cancelled"));
        } finally {
            _elapsed = Stopwatch.GetElapsedTime(started);
            Elapsed = CheckReport.Duration(_elapsed);
            IsRunning = false;
            _run.Dispose();
            _run = null;
        }
    }

    /// <summary>Saves the run as a Markdown report or, with every request and answer, as JSON - whichever file type the person picks.</summary>
    [RelayCommand(CanExecute = nameof(CanExport))]
    private async Task ExportAsync() {
        var run = Snapshot();
        var markdown = new FilePickerFileType(L.Get("checks.exportMarkdown")) {
            Patterns = ["*.md"],
            MimeTypes = ["text/markdown"],
            AppleUniformTypeIdentifiers = ["net.daringfireball.markdown"],
        };
        var json = new FilePickerFileType(L.Get("checks.exportJson")) {
            Patterns = ["*.json"],
            MimeTypes = ["application/json"],
            AppleUniformTypeIdentifiers = ["public.json"],
        };

        var picked = await _services.PickSaveFileAsync(new FilePickerSaveOptions {
            Title = L.Get("checks.exportTitle"),
            SuggestedFileName = CheckReport.FileName(run),
            DefaultExtension = "md",
            FileTypeChoices = [markdown, json],
            SuggestedFileType = markdown,
            ShowOverwritePrompt = true,
        });
        if (picked?.File is not { } file) {
            return;
        }

        using (file) {
            // The extension decides what goes into the file; a name without a known one goes by the type picked.
            var asJson = Path.GetExtension(file.Name).ToUpperInvariant() switch {
                ".JSON" => true,
                ".MD" => false,
                _ => picked.Value.SelectedFileType == json,
            };

            try {
                await using var stream = await file.OpenWriteAsync();
                if (stream.CanSeek) {
                    // A file written over is not always cut to the new length by the platform.
                    stream.SetLength(0);
                }
                await using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
                await writer.WriteAsync(asJson ? CheckReport.Json(run) : CheckReport.Markdown(run));
            } catch (IOException failure) {
                _services.Notifier.Error(L.Get("checks.exportFailed"), failure.Message);
                return;
            } catch (UnauthorizedAccessException failure) {
                _services.Notifier.Error(L.Get("checks.exportFailed"), failure.Message);
                return;
            }

            _services.Notifier.Success(L.Get("checks.exported"), file.TryGetLocalPath() ?? file.Name);
        }
    }

    /// <summary>The run as it stands, for a report or a copy: every check with its verdict, pending where the run did not get to it.</summary>
    internal CheckRun Snapshot() {
        var connection = _session.Client.Connection;
        return new CheckRun {
            BaseUrl = connection.BaseUrl,
            AcceptInvalidCertificates = connection.AcceptInvalidCertificates,
            Configuration = _session.Configuration,
            StartedAt = _startedAt,
            Elapsed = _elapsed,
            Cancelled = _cancelled,
            Results = [.. ConformanceSuite.Checks.Select(check => _items[check.Id].Result)],
        };
    }

    internal Task CopyAsync(string text) {
        return _services.CopyAsync(text);
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() {
        _run?.Cancel();
    }

    [RelayCommand]
    private void ShowExchange(HttpExchange exchange) {
        _shell.ShowExchange(exchange);
    }

    private void Report(CheckResult result) {
        if (!_items.TryGetValue(result.Check.Id, out var item)) {
            return;
        }

        item.Apply(result);
        if (result.Status == CheckStatus.Running) {
            return;
        }

        Completed++;
        switch (result.Status) {
            case CheckStatus.Passed:
                Passed++;
                break;
            case CheckStatus.Warning:
                Warnings++;
                break;
            case CheckStatus.Failed:
                Failed++;
                break;
            case CheckStatus.Unsupported:
                Unsupported++;
                break;
            case CheckStatus.Skipped:
                Skipped++;
                break;
        }

        if (Selected is null && result.Status == CheckStatus.Failed) {
            Selected = item;
        }

        if (OnlyProblems) {
            Refilter();
        }
    }

    /// <summary>Shows every row, or only the problems - and a heading only over rows that are shown.</summary>
    private void Refilter() {
        foreach (var item in _items.Values) {
            item.IsShown = !OnlyProblems || item.IsProblem;
        }

        foreach (var category in Categories) {
            category.IsShown = category.Items.Any(item => item.IsShown);
        }

        NothingShown = OnlyProblems && !IsRunning && !Categories.Any(category => category.IsShown);
    }

    public void Dispose() {
        _run?.Cancel();
    }
}
