using System.Collections.Concurrent;
using System.Diagnostics;
using WpfStudio.Contracts;
using WpfStudio.Contracts.Diagnostics;

namespace WpfStudio.Workspace.Tests;

/// <summary>The performance measurements of a real worker, as they reach the client over the workspace connection.</summary>
public sealed class WorkerDiagnosticsTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WpfStudio-WorkerDiagnostics-" + Guid.NewGuid().ToString("N"));
    private string Project => Path.Combine(_directory, "Library.csproj");
    private string Source => Path.Combine(_directory, "Orders.cs");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Source, "namespace Shop;\npublic sealed class Order { public string Title { get; set; } = \"\"; public void Send() { Title. } }\n");
        using var restore = Process.Start(new ProcessStartInfo("dotnet", "restore --nologo -v q") { WorkingDirectory = _directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        await restore.WaitForExitAsync();
        Assert.Equal(0, restore.ExitCode);
    }

    public Task DisposeAsync() { try { Directory.Delete(_directory, true); } catch (IOException) { } return Task.CompletedTask; }

    [Fact]
    public async Task RequestsAreTimedOnBothSidesAndTheWorkerReportsItsSlowOnesToTheClient()
    {
        var perf = new PerfMonitor();
        var reported = new ConcurrentQueue<SlowOperation>();
        perf.Slow += reported.Enqueue;
        await using var client = new WorkspaceClient(perf: perf);
        Assert.Null(await client.GetWorkerPerformanceReportAsync());

        await client.LoadAsync(new LoadWorkspaceRequest(Project));
        var text = await File.ReadAllTextAsync(Source);
        Assert.True((await client.UpdateDocumentAsync(new(Source, text, 1, Analyze: false))).Accepted);
        var completions = await client.GetCompletionsAsync(new DocumentPositionRequest(Source, text.IndexOf("Title. ", StringComparison.Ordinal) + 6, 1));
        Assert.Contains(completions.Items, item => item.DisplayText == "Length");

        // As the client saw them, from the request being sent to its answer.
        var calls = perf.Snapshot().Where(stat => stat.Area == PerfAreas.WorkerCall).ToDictionary(stat => stat.Name);
        Assert.Equal(["GetCompletionsAsync", "LoadAsync", "UpdateDocumentAsync"], calls.Keys.Order(StringComparer.Ordinal));
        Assert.All(calls.Values, stat => Assert.Equal(1, stat.Count));

        // As the worker saw them. Loading a project through MSBuild takes long enough for the worker to report it by itself.
        var waited = Stopwatch.StartNew();
        while (!reported.Any(operation => operation.Source == "worker") && waited.Elapsed < TimeSpan.FromSeconds(30)) await Task.Delay(20);
        var load = Assert.Single(reported, operation => operation is { Source: "worker", Area: PerfAreas.Worker, Name: "LoadAsync" });
        Assert.InRange(load.Milliseconds, 250, calls["LoadAsync"].MaxMilliseconds);

        var report = await client.GetWorkerPerformanceReportAsync(reset: true);
        Assert.NotNull(report);
        Assert.StartsWith("Language worker, last ", report);
        Assert.Matches(@"(?m)^\s+worker\s+1\s.*\sLoadAsync\r?$", report);
        Assert.Matches(@"(?m)^\s+worker\s+1\s.*\sUpdateDocumentAsync\r?$", report);
        Assert.Matches(@"(?m)^\s+worker\s+1\s.*\sGetCompletionsAsync\r?$", report);
        Assert.Matches(@"(?m)^\s+pool wait\s+\d+\s.*\sthread pool\r?$", report);

        // The report started the worker's counting again, so the next one covers only what follows it.
        var next = await client.GetWorkerPerformanceReportAsync();
        Assert.NotNull(next);
        Assert.DoesNotMatch(@"(?m)\sLoadAsync\r?$", next);
        Assert.Matches(@"(?m)^\s+worker\s+1\s.*\sGetPerformanceReportAsync\r?$", next);
    }

    [Fact]
    public async Task ARequestThatWaitsForAnotherOnTheConnectionIsMeasuredAndNamesWhatHeldIt()
    {
        var perf = new PerfMonitor();
        await using var client = new WorkspaceClient(perf: perf);
        // A load holds the connection for as long as MSBuild takes; an edit made meanwhile has to wait for it.
        var loading = client.LoadAsync(new LoadWorkspaceRequest(Project));
        var reported = new ConcurrentQueue<SlowOperation>();
        perf.Slow += reported.Enqueue;
        var text = await File.ReadAllTextAsync(Source);
        var update = client.UpdateDocumentAsync(new(Source, text, 1, Analyze: false));
        await loading;
        await update;
        var wait = Assert.Single(perf.Snapshot(), stat => stat.Area == PerfAreas.Wait);
        Assert.Equal("UpdateDocumentAsync, for the worker connection", wait.Name);
        if (wait.MaxMilliseconds >= PerfAreas.SlowAfter(PerfAreas.Wait).TotalMilliseconds)
            Assert.Equal("held by LoadAsync", Assert.Single(reported, operation => operation.Area == PerfAreas.Wait).Detail);
    }
}
