using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using ScimStudio.App.Services;
using ScimStudio.Core.Generation;

namespace ScimStudio.App.ViewModels;

/// <summary>The generator page: fills the server with marked test people and groups, and takes them away again.</summary>
/// <param name="services">What the interface shares.</param>
/// <param name="session">The session.</param>
public sealed partial class GeneratorViewModel(AppServices services, Session session) : ViewModelBase, IDisposable {
    private CancellationTokenSource? _run;

    [ObservableProperty]
    public partial decimal? Users { get; set; } = 25;

    [ObservableProperty]
    public partial decimal? Groups { get; set; } = 3;

    [ObservableProperty]
    public partial decimal? MembersPerGroup { get; set; } = 5;

    [ObservableProperty]
    public partial decimal? InactivePercent { get; set; } = 10;

    [ObservableProperty]
    public partial decimal? Parallelism { get; set; } = 4;

    [ObservableProperty]
    public partial string Domain { get; set; } = "example.com";

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(GenerateCommand), nameof(RemoveCommand), nameof(CancelCommand))]
    public partial bool IsRunning { get; set; }

    [ObservableProperty]
    public partial double Progress { get; set; }

    [ObservableProperty]
    public partial string? ProgressText { get; set; }

    [ObservableProperty]
    public partial string? Report { get; set; }

    [ObservableProperty]
    public partial IReadOnlyList<string> Problems { get; set; } = [];

    public string DialectName => DialectOption.For(session.Dialect.Kind).Name;

    public string Marker => TestDataGenerator.MARKER;

    public string DialectNote => L.Format("generator.dialectNote", DialectName, Marker);

    public bool HasOutcome => IsRunning || Report is not null || ProgressText is not null;

    partial void OnIsRunningChanged(bool value) {
        OnPropertyChanged(nameof(HasOutcome));
    }

    partial void OnReportChanged(string? value) {
        OnPropertyChanged(nameof(HasOutcome));
    }

    private bool CanStart() {
        return !IsRunning;
    }

    private bool CanCancel() {
        return IsRunning;
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task GenerateAsync() {
        var options = new GeneratorOptions {
            Users = (int)(Users ?? 0),
            Groups = (int)(Groups ?? 0),
            MembersPerGroup = (int)(MembersPerGroup ?? 0),
            InactiveShare = (double)(InactivePercent ?? 0) / 100,
            Parallelism = (int)(Parallelism ?? 1),
            Domain = string.IsNullOrWhiteSpace(Domain) ? "example.com" : Domain.Trim(),
        };

        await RunAsync(async (generator, progress, token) => {
            var report = await generator.GenerateAsync(options, progress, token);
            Report = L.Format("generator.created", report.UsersCreated, report.GroupsCreated, report.Failed, report.Elapsed.TotalSeconds);
            return report;
        });
    }

    [RelayCommand(CanExecute = nameof(CanStart))]
    private async Task RemoveAsync() {
        var confirmed = await services.Dialogs.ConfirmAsync(
            L.Get("generator.removeTitle"), L.Format("generator.removeMessage", Marker), L.Get("generator.remove"), danger: true);
        if (!confirmed) {
            return;
        }

        await RunAsync(async (generator, progress, token) => {
            var report = await generator.RemoveAsync((int)(Parallelism ?? 1), progress, token);
            Report = L.Format("generator.removed", report.Removed, report.Failed, report.Elapsed.TotalSeconds);
            return report;
        });
    }

    [RelayCommand(CanExecute = nameof(CanCancel))]
    private void Cancel() {
        _run?.Cancel();
    }

    private async Task RunAsync(Func<TestDataGenerator, IProgress<GeneratorProgress>, CancellationToken, Task<GeneratorReport>> work) {
        IsRunning = true;
        Report = null;
        Problems = [];
        Progress = 0;
        ProgressText = null;

        _run = new CancellationTokenSource();
        var progress = new Progress<GeneratorProgress>(step => {
            Progress = step.Total == 0 ? 1 : (double)step.Done / step.Total;
            ProgressText = L.Format("generator.progress", step.Done, step.Total, step.Failed, step.Rate);
        });

        try {
            var generator = new TestDataGenerator(session.Client, session.Dialect);
            var token = _run.Token;
            var report = await Task.Run(() => work(generator, progress, token), token);
            Problems = [.. report.Problems.Select(L.Format)];

            if (report.Failed > 0) {
                services.Notifier.Warning(L.Get("generator.doneWithFailures"), Report);
            } else {
                services.Notifier.Success(L.Get("generator.done"), Report);
            }
        } catch (OperationCanceledException) {
            Report = L.Get("generator.cancelled");
        } catch (Exception failure) when (failure is Core.Scim.ScimException or HttpRequestException) {
            Report = Describe(failure);
            services.Notifier.Error(L.Get("generator.failed"), Report);
        } finally {
            IsRunning = false;
            _run.Dispose();
            _run = null;
        }
    }

    public void Dispose() {
        _run?.Cancel();
    }
}
