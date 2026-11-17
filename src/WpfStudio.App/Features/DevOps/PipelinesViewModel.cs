using System.Collections.ObjectModel;
using System.Net.Http;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using WpfStudio.App.Features.Git;
using WpfStudio.Contracts;
using WpfStudio.Core.DevOps;

namespace WpfStudio.App.Features.DevOps;

public enum PipelineView { Recent, Mine, CurrentBranch, Pipelines, Approvals }

public sealed record PipelineViewChoice(PipelineView View, string Label);

/// <summary>
/// The Pipelines tab (Actions on GitHub): recent runs, my runs, this branch's runs, the repository's pipelines and pending approvals,
/// the selected run's stages, jobs and tasks, and each one's log. A run that is still going is read again every few seconds.
/// </summary>
public sealed partial class PipelinesViewModel(IDevOpsHost host) : ObservableObject, IDisposable
{
    private const int ListSize = 50;
    private readonly LatestLoad _list = new(), _run = new(), _log = new();
    private CancellationTokenSource? _poll;
    private int _loadedVersion = -1;
    private bool _quiet;
    private string _rawLog = "";

    public ObservableCollection<PipelineViewChoice> Views { get; } = [];
    public ObservableCollection<DevOpsRun> Runs { get; } = [];
    public ObservableCollection<DevOpsPipeline> Definitions { get; } = [];
    public ObservableCollection<DevOpsApproval> Approvals { get; } = [];
    public ObservableCollection<TimelineNodeViewModel> Timeline { get; } = [];

    [ObservableProperty, NotifyPropertyChangedFor(nameof(ShowsRuns), nameof(ShowsDefinitions), nameof(ShowsApprovals))] public partial PipelineViewChoice? SelectedView { get; set; }
    /// <summary>When set, the run list holds this pipeline's runs.</summary>
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasPipelineFilter), nameof(ShowsRuns), nameof(ShowsDefinitions))] public partial DevOpsPipeline? PipelineFilter { get; private set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasRun))] public partial DevOpsRun? SelectedRun { get; set; }
    [ObservableProperty] public partial DevOpsPipeline? SelectedDefinition { get; set; }
    [ObservableProperty, NotifyPropertyChangedFor(nameof(HasRecord))] public partial TimelineNodeViewModel? SelectedNode { get; private set; }
    [ObservableProperty] public partial string Log { get; private set; } = "";
    [ObservableProperty] public partial bool KeepTimestamps { get; set; }
    [ObservableProperty] public partial bool IsLoading { get; private set; }
    [ObservableProperty] public partial bool IsRunLoading { get; private set; }
    [ObservableProperty] public partial bool IsLogLoading { get; private set; }
    [ObservableProperty] public partial string ListHeading { get; private set; } = "";

    public bool ShowsRuns => PipelineFilter is not null || SelectedView?.View is PipelineView.Recent or PipelineView.Mine or PipelineView.CurrentBranch;
    public bool ShowsDefinitions => PipelineFilter is null && SelectedView?.View == PipelineView.Pipelines;
    public bool ShowsApprovals => PipelineFilter is null && SelectedView?.View == PipelineView.Approvals;
    public bool HasPipelineFilter => PipelineFilter is not null;
    public bool HasRun => SelectedRun is not null;
    public bool HasRecord => SelectedNode is not null;
    public string PipelinesName => host.Provider?.Capabilities.PipelinesName ?? "Pipelines";
    public bool CanRetryFailedJobs => host.Provider?.Capabilities.RetryFailedJobs == true;
    public string EmptyText => IsLoading ? "Loading…" : ShowsApprovals ? "Nothing is waiting for an approval." : ShowsDefinitions ? "This repository has no pipelines." : "No runs here.";

    partial void OnSelectedViewChanged(PipelineViewChoice? value)
    {
        if (_quiet) return;
        PipelineFilter = null;
        LoadList();
    }

    partial void OnSelectedRunChanged(DevOpsRun? value) { if (!_quiet) LoadRun(); }
    partial void OnKeepTimestampsChanged(bool value) => Log = _rawLog.Length == 0 ? Log : Displayed(DevOpsText.FormatLog(_rawLog, value));

    /// <summary>The characters of a log the text view shows: its end, where a failure is, for a log too long to show whole.</summary>
    internal const int LogDisplayLimit = 1_000_000;

    internal static string Displayed(string log)
    {
        if (log.Length <= LogDisplayLimit) return log;
        var start = log.IndexOf('\n', log.Length - LogDisplayLimit) + 1;
        return $"… the first {start:N0} characters are left out; open the run on the web for the whole log.\n" + log[start..];
    }
    partial void OnIsLoadingChanged(bool value) => OnPropertyChanged(nameof(EmptyText));

    internal void EnsureLoaded()
    {
        if (_loadedVersion == host.Version || host.Provider is not { } provider) return;
        _loadedVersion = host.Version;
        KeepTimestamps = host.Settings.KeepLogTimestamps;
        var github = provider.Target.Kind == DevOpsProviderKind.GitHub;
        Quietly(() =>
        {
            var views = new List<PipelineViewChoice>
            {
                new(PipelineView.Recent, "Recent runs"), new(PipelineView.Mine, "My runs"), new(PipelineView.CurrentBranch, "This branch"),
                new(PipelineView.Pipelines, github ? "Workflows" : "Pipelines"),
            };
            if (provider.Capabilities.Approvals) views.Add(new(PipelineView.Approvals, "Pending approvals"));
            GitViewModel.Replace(Views, views);
            SelectedView = Views.FirstOrDefault();
        });
        foreach (var name in new[] { nameof(PipelinesName), nameof(CanRetryFailedJobs), nameof(ShowsRuns), nameof(ShowsDefinitions), nameof(ShowsApprovals) }) OnPropertyChanged(name);
        LoadList();
    }

    internal void Reload()
    {
        if (_loadedVersion != host.Version) EnsureLoaded();
        else
        {
            LoadList();
            if (SelectedRun is not null) LoadRun();
        }
    }

    internal void Reset()
    {
        _list.Cancel();
        _run.Cancel();
        _log.Cancel();
        StopPolling();
        _loadedVersion = -1;
        Quietly(() =>
        {
            Views.Clear();
            SelectedView = null;
            PipelineFilter = null;
            Runs.Clear();
            Definitions.Clear();
            Approvals.Clear();
            SelectedRun = null;
            SelectedDefinition = null;
        });
        ClearRun();
        ListHeading = "";
    }

    internal Task WhenLoadedAsync() => Task.WhenAll(_list.Task, _run.Task, _log.Task);

    /// <summary>The Git pane switched branches: this branch's runs are another branch's now.</summary>
    internal void BranchChanged()
    {
        if (_loadedVersion == host.Version && PipelineFilter is null && SelectedView?.View == PipelineView.CurrentBranch) LoadList();
    }

    private void LoadList()
    {
        if (host.Provider is not { } provider || host.User is not { } me || SelectedView is not { } view) return;
        var filter = PipelineFilter;
        var branch = DevOpsViewModel.IsBranchName(host.Git.Branch) ? host.Git.Branch : null;
        ListHeading = filter is not null ? $"Runs of {filter.Name}" : view.View == PipelineView.CurrentBranch ? $"Runs of {branch ?? "no branch"}" : view.Label;
        _list.Start(async token =>
        {
            if (filter is null && view.View == PipelineView.Pipelines)
            {
                var definitions = await provider.ListPipelinesAsync(token);
                token.ThrowIfCancellationRequested();
                GitViewModel.Replace(Definitions, definitions);
                return;
            }
            if (filter is null && view.View == PipelineView.Approvals)
            {
                var approvals = await provider.ListApprovalsAsync(token);
                token.ThrowIfCancellationRequested();
                GitViewModel.Replace(Approvals, approvals);
                return;
            }
            if (filter is null && view.View == PipelineView.CurrentBranch && branch is null)
            {
                Runs.Clear();
                return;
            }
            var list = view.View switch { PipelineView.Mine => RunList.Mine, PipelineView.CurrentBranch => RunList.CurrentBranch, _ => RunList.Recent };
            var runs = await provider.ListRunsAsync(filter is null ? list : RunList.Recent, me, branch, filter?.Id, ListSize, token);
            token.ThrowIfCancellationRequested();
            var selected = SelectedRun;
            Quietly(() =>
            {
                GitViewModel.Replace(Runs, runs);
                SelectedRun = selected is null ? null : Runs.FirstOrDefault(run => run.Id == selected.Id) ?? selected;
            });
            if (SelectedRun is null) SelectedRun = Runs.FirstOrDefault();
        }, host.ReportError, loading => IsLoading = loading);
    }

    /// <summary>Reads the selected run and its timeline, opening a failed run on the step that failed, and keeps reading it while it runs.</summary>
    private void LoadRun()
    {
        StopPolling();
        _log.Cancel();
        if (host.Provider is not { } provider || SelectedRun is not { } run)
        {
            _run.Cancel();
            ClearRun();
            return;
        }
        ClearRun();
        _run.Start(async token =>
        {
            var records = await provider.GetTimelineAsync(run, token);
            token.ThrowIfCancellationRequested();
            ApplyTimeline(records, selectFailure: true);
            if (run.IsActive || PipelineTimeline.IsActive(records)) StartPolling(provider, run);
        }, host.ReportError, loading => IsRunLoading = loading);
    }

    private void ApplyTimeline(IReadOnlyList<DevOpsTimelineRecord> records, bool selectFailure)
    {
        // Rebuilding keeps what the reader opened and the step they are reading.
        var expanded = new HashSet<string>(Flatten(Timeline).Where(node => node.IsExpanded).Select(node => node.Record.Id), StringComparer.Ordinal);
        var selectedId = SelectedNode?.Record.Id;
        var tree = PipelineTimeline.Build(records);
        var first = Timeline.Count == 0;
        Quietly(() =>
        {
            GitViewModel.Replace(Timeline, tree.Select(node => new TimelineNodeViewModel(node, this, expanded, first)));
            SelectedNode = selectedId is null ? null : Flatten(Timeline).FirstOrDefault(node => node.Record.Id == selectedId);
            if (SelectedNode is not null) SelectedNode.IsSelected = true;
        });
        if (SelectedNode is null && selectFailure && PipelineTimeline.FirstFailure(tree) is { } failure)
        {
            var node = Flatten(Timeline).First(candidate => candidate.Record.Id == failure.Record.Id);
            foreach (var ancestor in Flatten(Timeline).Where(candidate => candidate.Contains(node))) ancestor.IsExpanded = true;
            node.IsSelected = true;
        }
    }

    internal static IEnumerable<TimelineNodeViewModel> Flatten(IEnumerable<TimelineNodeViewModel> nodes) =>
        nodes.SelectMany(node => new[] { node }.Concat(Flatten(node.Children)));

    /// <summary>Called by a node the tree view selected.</summary>
    internal void Select(TimelineNodeViewModel node)
    {
        if (_quiet || ReferenceEquals(SelectedNode, node)) return;
        if (SelectedNode is { } previous && !ReferenceEquals(previous, node)) previous.IsSelected = false;
        SelectedNode = node;
        LoadLog();
    }

    private void LoadLog()
    {
        if (host.Provider is not { } provider || SelectedRun is not { } run || SelectedNode?.Record is not { HasLog: true } record)
        {
            _log.Cancel();
            _rawLog = "";
            Log = SelectedNode is null ? "" : "This step has no log yet.";
            return;
        }
        _log.Start(async token =>
        {
            var text = await provider.GetLogAsync(run, record, token);
            token.ThrowIfCancellationRequested();
            _rawLog = text;
            Log = text.Length == 0 ? "The log is empty." : Displayed(DevOpsText.FormatLog(text, KeepTimestamps));
        }, ex => Log = "The log could not be read: " + GitDiagnosticSanitizer.Redact(ex.Message), loading => IsLogLoading = loading);
    }

    private void ClearRun()
    {
        Timeline.Clear();
        SelectedNode = null;
        _rawLog = "";
        Log = "";
    }

    // ------------------------------------------------------------------ Live refresh

    private void StartPolling(IDevOpsProvider provider, DevOpsRun run)
    {
        StopPolling();
        var seconds = host.Settings.RefreshSeconds;
        if (seconds <= 0) return;
        var poll = _poll = new CancellationTokenSource();
        PollAsync(provider, run, TimeSpan.FromSeconds(Math.Max(5, seconds)), poll.Token).Forget(host.ReportError);
    }

    private void StopPolling()
    {
        _poll?.Cancel();
        _poll?.Dispose();
        _poll = null;
    }

    private async Task PollAsync(IDevOpsProvider provider, DevOpsRun run, TimeSpan interval, CancellationToken token)
    {
        try
        {
            while (!token.IsCancellationRequested)
            {
                await Task.Delay(interval, token);
                var freshTask = provider.GetRunAsync(run, token);
                var recordsTask = provider.GetTimelineAsync(run, token);
                await Task.WhenAll(freshTask, recordsTask);
                token.ThrowIfCancellationRequested();
                run = await freshTask;
                var records = await recordsTask;
                Quietly(() =>
                {
                    if (Runs.FirstOrDefault(item => item.Id == run.Id) is { } listed) Runs[Runs.IndexOf(listed)] = run;
                    SelectedRun = run;
                });
                var readingRunningStep = SelectedNode?.Record.State is RunStatus.Running or RunStatus.Queued;
                ApplyTimeline(records, selectFailure: false);
                // A step still writing its log is read again; a finished one keeps the reader's scroll position.
                if (readingRunningStep) LoadLog();
                if (!run.IsActive && !PipelineTimeline.IsActive(records))
                {
                    host.ReportStatus($"{run.Title} {run.StatusText.ToLowerInvariant()}");
                    return;
                }
            }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        // Live refresh is a convenience: a failed read stops it, and Refresh starts it again.
        catch (DevOpsException) { }
        catch (HttpRequestException) { }
    }

    // ------------------------------------------------------------------ Actions

    private bool CanUseRun() => host.CanAct && SelectedRun is not null;
    private bool CanCancelRun() => CanUseRun() && SelectedRun!.IsActive;
    private bool CanRetryRun() => CanUseRun() && CanRetryFailedJobs && SelectedRun!.Status == RunStatus.Completed && SelectedRun.Result is RunResult.Failed or RunResult.PartiallySucceeded or RunResult.Canceled;
    private bool CanRunPipeline(DevOpsPipeline? pipeline) => host.CanAct && (pipeline ?? SelectedDefinition ?? PipelineFilter) is not null;

    [RelayCommand(CanExecute = nameof(CanRunPipeline))]
    private async Task RunPipelineAsync(DevOpsPipeline? pipeline)
    {
        if ((pipeline ?? SelectedDefinition ?? PipelineFilter) is not { } target) return;
        var branch = await host.PromptAsync($"Run {target.Name}", $"The branch to run {target.Name} on.", DevOpsViewModel.IsBranchName(host.Git.Branch) ? host.Git.Branch : "main");
        if (string.IsNullOrWhiteSpace(branch)) return;
        await QueueAsync(target.Id, target.Name, branch.Trim());
    }

    [RelayCommand(CanExecute = nameof(CanUseRun))]
    private Task RunAgainAsync() => SelectedRun is not { } run ? Task.CompletedTask : QueueAsync(run.PipelineId, run.PipelineName, run.Branch ?? host.Git.Branch);

    private Task QueueAsync(string pipelineId, string name, string branch) => host.RunAsync($"Run {name} on {branch}", async (provider, _, token) =>
    {
        var queued = await provider.QueueRunAsync(pipelineId, branch, token);
        if (queued is null)
        {
            host.ReportStatus($"{name} was started on {branch}. It appears in the list in a moment; refresh to see it.");
            return;
        }
        host.ReportStatus($"Queued {queued.Title}");
        Quietly(() =>
        {
            if (SelectedView?.View == PipelineView.Pipelines && PipelineFilter is null) SelectedView = Views.FirstOrDefault();
            Runs.Insert(0, queued);
        });
        SelectedRun = queued;
        if (ShowsRuns) LoadList();
    });

    [RelayCommand(CanExecute = nameof(CanCancelRun))]
    private async Task CancelRunAsync()
    {
        if (SelectedRun is not { } run || !await host.ConfirmAsync("Cancel run", $"Cancel {run.Title}?")) return;
        await host.RunAsync($"Cancel {run.Title}", async (provider, _, token) =>
        {
            await provider.CancelRunAsync(run, token);
            LoadRun();
        });
    }

    [RelayCommand(CanExecute = nameof(CanRetryRun))]
    private Task RetryRunAsync() => SelectedRun is not { } run ? Task.CompletedTask : host.RunAsync($"Re-run failed jobs of {run.Title}", async (provider, _, token) =>
    {
        await provider.RetryRunAsync(run, token);
        var fresh = await provider.GetRunAsync(run, token);
        Quietly(() =>
        {
            if (Runs.FirstOrDefault(item => item.Id == fresh.Id) is { } listed) Runs[Runs.IndexOf(listed)] = fresh;
            SelectedRun = fresh;
        });
        LoadRun();
    });

    [RelayCommand]
    private void ShowRuns(DevOpsPipeline? pipeline)
    {
        if ((pipeline ?? SelectedDefinition) is not { } target) return;
        PipelineFilter = target;
        Quietly(() => SelectedRun = null);
        ClearRun();
        LoadList();
    }

    [RelayCommand]
    private void ClearPipelineFilter()
    {
        PipelineFilter = null;
        LoadList();
    }

    [RelayCommand]
    private void OpenInBrowser(object? item)
    {
        var url = item switch { DevOpsRun run => run.WebUrl, DevOpsPipeline pipeline => pipeline.WebUrl, DevOpsApproval approval => approval.WebUrl, _ => SelectedRun?.WebUrl };
        if (url is not null) host.OpenUrl(url);
    }

    [RelayCommand]
    private void CopyLog()
    {
        if (Log.Length > 0) host.Copy(Log, "the log");
    }

    private bool CanRespond(DevOpsApproval? approval) => host.CanAct && approval is not null;

    [RelayCommand(CanExecute = nameof(CanRespond))]
    private Task ApproveAsync(DevOpsApproval? approval) => RespondAsync(approval, approve: true);

    [RelayCommand(CanExecute = nameof(CanRespond))]
    private Task RejectAsync(DevOpsApproval? approval) => RespondAsync(approval, approve: false);

    private async Task RespondAsync(DevOpsApproval? approval, bool approve)
    {
        if (approval is null) return;
        var verb = approve ? "Approve" : "Reject";
        var comment = await host.PromptAsync($"{verb} {approval.PipelineName}", approval.Instructions is { Length: > 0 } instructions
            ? $"{instructions}\n\nA comment for the record (optional)." : "A comment for the record (optional).");
        if (comment is null) return;
        await host.RunAsync($"{verb} {approval.PipelineName}", async (provider, _, token) =>
        {
            await provider.RespondToApprovalAsync(approval, approve, comment, token);
            Approvals.Remove(approval);
        });
    }

    internal void NotifyCommands()
    {
        foreach (var command in new IRelayCommand[] { RunPipelineCommand, RunAgainCommand, CancelRunCommand, RetryRunCommand, ApproveCommand, RejectCommand })
            command.NotifyCanExecuteChanged();
    }

    partial void OnSelectedDefinitionChanged(DevOpsPipeline? value) => RunPipelineCommand.NotifyCanExecuteChanged();
    partial void OnPipelineFilterChanged(DevOpsPipeline? value) => RunPipelineCommand.NotifyCanExecuteChanged();

    private void Quietly(Action action)
    {
        var previous = _quiet;
        _quiet = true;
        try { action(); }
        finally { _quiet = previous; }
    }

    public void Dispose()
    {
        _list.Cancel();
        _run.Cancel();
        _log.Cancel();
        StopPolling();
    }
}

/// <summary>A stage, job or task of a run in the timeline tree; selecting it shows its log.</summary>
public sealed partial class TimelineNodeViewModel : ObservableObject
{
    private readonly PipelinesViewModel _owner;

    internal TimelineNodeViewModel(DevOpsTimelineNode node, PipelinesViewModel owner, IReadOnlySet<string> expanded, bool expandByDefault)
    {
        _owner = owner;
        Record = node.Record;
        Children = [.. node.Children.Select(child => new TimelineNodeViewModel(child, owner, expanded, expandByDefault))];
        // Stages and jobs open the first time a run is shown, so its steps are in view; later reads keep what the reader chose.
        IsExpanded = expanded.Contains(Record.Id) || (expandByDefault && !Record.Type.Equals("Task", StringComparison.OrdinalIgnoreCase) && Children.Count > 0);
    }

    public DevOpsTimelineRecord Record { get; }
    public ObservableCollection<TimelineNodeViewModel> Children { get; }
    [ObservableProperty] public partial bool IsExpanded { get; set; }
    [ObservableProperty] public partial bool IsSelected { get; set; }

    partial void OnIsSelectedChanged(bool value)
    {
        if (value) _owner.Select(this);
    }

    internal bool Contains(TimelineNodeViewModel node) => Children.Any(child => ReferenceEquals(child, node) || child.Contains(node));
}
