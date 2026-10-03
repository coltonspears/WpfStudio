using WpfStudio.App.Features.Profiling;
using WpfStudio.Contracts;
using WpfStudio.Contracts.Profiling;

namespace WpfStudio.Shell.Tests;

public sealed class MemoryProfilerViewModelTests
{
    [Fact]
    public async Task CaptureLoadsObjectGraphAndEstimateWithBaselineIncludingDisappearedTypes()
    {
        var first = new Session(Summary([Type("Page", 2, 20), Type("Removed", 3, 60)]));
        var second = new Session(Summary([Type("Page", 4, 40)]));
        var profiler = new Profiler(); profiler.Sessions.Enqueue(first); profiler.Sessions.Enqueue(second);
        await using var model = new MemoryProfilerViewModel(profiler, new Files());
        await model.OpenDumpCommand.ExecuteAsync(null);
        Assert.True(model.HasCapture); Assert.False(model.IsBusy);
        Assert.NotNull(model.Details); Assert.NotNull(model.Graph);
        model.SetBaselineCommand.Execute(null);
        await model.OpenDumpCommand.ExecuteAsync(null);
        Assert.True(first.Disposed);
        var grown = Assert.Single(model.Types, t => t.Key == "Page");
        Assert.Equal(20, grown.BytesDelta); Assert.Equal("+2", grown.CountGrowth);
        var removed = Assert.Single(model.Types, t => t.Key == "Removed");
        Assert.Equal(-60, removed.BytesDelta); Assert.Equal(0, removed.Count);
        model.SelectedReference = second.Reference;
        await model.EstimateReferenceCommand.ExecuteAsync(null);
        Assert.Equal(second.Reference.Id, second.LastRelease!.ReferenceId);
        Assert.Equal(3, model.InspectorTab); Assert.NotNull(model.ReleaseEstimate);
        await model.EstimateObjectCommand.ExecuteAsync(null);
        Assert.Null(second.LastRelease.ReferenceId);
        model.ClearBaselineCommand.Execute(null);
        Assert.False(model.HasBaseline); Assert.DoesNotContain(model.Types, t => t.Key == "Removed");
        await model.CloseCaptureCommand.ExecuteAsync(null);
        Assert.True(second.Disposed); Assert.False(model.HasCapture); Assert.Null(model.Graph);
    }

    [Fact]
    public async Task CancellationAndFailurePreservePreviousCaptureAndBaseline()
    {
        var first = new Session(Summary([Type("Page", 1, 10)]));
        var profiler = new Profiler(); profiler.Sessions.Enqueue(first);
        await using var model = new MemoryProfilerViewModel(profiler, new Files());
        await model.OpenDumpCommand.ExecuteAsync(null); model.SetBaselineCommand.Execute(null);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        profiler.Open = async token => { started.SetResult(); await Task.Delay(Timeout.Infinite, token); throw new InvalidOperationException(); };
        var opening = model.OpenDumpCommand.ExecuteAsync(null); await started.Task;
        Assert.True(model.IsBusy); Assert.False(model.CaptureCommand.CanExecute(null));
        model.OpenDumpCancelCommand.Execute(null); await opening;
        Assert.Same(first.Summary, model.Summary); Assert.True(model.HasBaseline); Assert.False(first.Disposed);
        Assert.False(model.IsBusy); Assert.Contains("cancelled", model.Status);
        profiler.Open = _ => Task.FromException<IMemorySession>(new InvalidDataException("Full heap data missing"));
        await model.OpenDumpCommand.ExecuteAsync(null);
        Assert.Same(first.Summary, model.Summary); Assert.Contains("Full heap data missing", model.Status);
        Assert.False(model.IsBusy);
    }

    [Fact]
    public async Task OlderInspectionCannotOverwriteNewerObjectSelection()
    {
        var session = new Session(Summary([Type("Page", 2, 20)]));
        var profiler = new Profiler(); profiler.Sessions.Enqueue(session);
        await using var model = new MemoryProfilerViewModel(profiler, new Files());
        await model.OpenDumpCommand.ExecuteAsync(null);
        var pending = new TaskCompletionSource<MemoryObjectDetails>(TaskCreationOptions.RunContinuationsAsynchronously);
        session.Inspection = id => id == 1 ? pending.Task : Task.FromResult(session.Detail(id));
        var older = model.InspectObjectCommand.ExecuteAsync(1);
        await model.OpenReferenceTargetCommand.ExecuteAsync(session.Reference with { ToId = 2 });
        Assert.Equal(2, model.Details!.Object.Id);
        pending.SetResult(session.Detail(1)); await older;
        Assert.Equal(2, model.Details.Object.Id); Assert.False(model.IsInspecting);
    }

    [Fact]
    public async Task EmptySearchClearsPreviousInspectionAndCleanupEstimate()
    {
        var session = new Session(Summary([Type("Page", 1, 10)]));
        var profiler = new Profiler(); profiler.Sessions.Enqueue(session);
        await using var model = new MemoryProfilerViewModel(profiler, new Files());
        await model.OpenDumpCommand.ExecuteAsync(null);
        model.SelectedIncomingReference = session.Reference;
        await model.EstimateObjectCommand.ExecuteAsync(null);
        Assert.NotNull(model.ReleaseEstimate);
        session.Query = _ => Task.FromResult(new MemoryObjectPage(0, []));
        model.ObjectFilter = "absent type";
        Assert.Empty(model.Objects); Assert.Null(model.SelectedObject);
        Assert.Null(model.Details); Assert.Null(model.Graph); Assert.Null(model.ReleaseEstimate);
        Assert.Null(model.SelectedReference); Assert.Null(model.SelectedIncomingReference);
        Assert.False(model.EstimateObjectCommand.CanExecute(null));
        Assert.False(model.EstimateReferenceCommand.CanExecute(null));
    }

    [Fact]
    public async Task ProcessSelectionCarriesIdentityAndUserAnalysisLimitsToCapture()
    {
        var session = new Session(Summary([Type("Page", 1, 10)]));
        var profiler = new Profiler(); profiler.Sessions.Enqueue(session);
        await using var model = new MemoryProfilerViewModel(profiler, new Files());
        await model.RefreshProcessesCommand.ExecuteAsync(null);
        model.MaxObjects = 250_000; model.MaxReferences = 2_000_000; model.RuntimeIndex = 1; model.DacPath = "C:\\runtime\\mscordacwks.dll";
        await model.CaptureCommand.ExecuteAsync(null);
        Assert.Equal(125, profiler.LastCapture!.ProcessId); Assert.Equal(55, profiler.LastCapture.ProcessStartTimeUtcTicks);
        Assert.Equal(250_000, profiler.LastCapture.MaxObjects); Assert.Equal(1, profiler.LastCapture.RuntimeIndex);
        Assert.Equal(model.DacPath, profiler.LastCapture.DacPath);
    }

    private static MemoryTypeSummary Type(string key, int count, long bytes) => new(key, key, "fixture", count, bytes, count, bytes);
    private static HeapSummary Summary(IReadOnlyList<MemoryTypeSummary> types) => new("fixture.dmp", ".NET 10", "X64", DateTimeOffset.UtcNow,
        types.Sum(t => t.Count), types.Sum(t => t.Bytes), types.Sum(t => t.Bytes), 0, 0, 1, 1, true, [], types);
    private sealed class Profiler : IMemoryProfiler
    {
        public Queue<IMemorySession> Sessions { get; } = [];
        public Func<CancellationToken, Task<IMemorySession>>? Open { get; set; }
        public HeapCaptureRequest? LastCapture { get; private set; }
        public Task<IMemorySession> OpenAsync(HeapCaptureRequest request, CancellationToken cancellationToken = default)
        { LastCapture = request; return Open?.Invoke(cancellationToken) ?? Task.FromResult(Sessions.Dequeue()); }
        public Task<IReadOnlyList<ProfileProcess>> GetProcessesAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<ProfileProcess>>([new(125, "fixture", 55, 100, ".NET")]);
    }
    private sealed class Session(HeapSummary summary) : IMemorySession
    {
        public HeapSummary Summary => summary;
        public bool Disposed { get; private set; }
        public MemoryReleaseRequest? LastRelease { get; private set; }
        public Func<int, Task<MemoryObjectDetails>>? Inspection { get; set; }
        public Func<MemoryObjectQuery, Task<MemoryObjectPage>>? Query { get; set; }
        public MemoryReferenceInfo Reference { get; } = new(3, 1, 0, "owner", "page", "field", "Strong", false, false);
        public MemoryObjectDetails Detail(int id) => new(Object(id), "value", [], [Reference], [], [], [], [], 1, 0, false, false);
        private MemoryObjectInfo Object(int id) => new(id, "0x123", "Page", "Page", "fixture", 10, 10, true, "Generation2", false);
        public Task<MemoryObjectPage> GetObjectsAsync(MemoryObjectQuery query, CancellationToken cancellationToken = default) => Query?.Invoke(query) ?? Task.FromResult(new MemoryObjectPage(1, [Object(0)]));
        public Task<MemoryObjectDetails> InspectAsync(int objectId, CancellationToken cancellationToken = default) => Inspection?.Invoke(objectId) ?? Task.FromResult(Detail(objectId));
        public Task<MemoryGraph> GetGraphAsync(MemoryGraphRequest request, CancellationToken cancellationToken = default) => Task.FromResult(new MemoryGraph([new(Object(request.ObjectId), 0, true, true)], [], false, "fixture"));
        public Task<MemoryReleaseEstimate> EstimateReleaseAsync(MemoryReleaseRequest request, CancellationToken cancellationToken = default)
        { LastRelease = request; return Task.FromResult(new MemoryReleaseEstimate(10, 1, true, false, "model", [], [0], null)); }
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }
    private sealed class Files : IFileDialogService
    {
        public Task<string?> OpenFileAsync(string title, string filter, string? initialDirectory = null) => Task.FromResult<string?>("fixture.dmp");
        public Task<string?> OpenFolderAsync(string title, string? initialDirectory = null) => Task.FromResult<string?>(null);
        public Task<string?> SaveFileAsync(string title, string filter, string? suggestedFileName = null) => Task.FromResult<string?>(null);
    }
}
