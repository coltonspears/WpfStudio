using WpfStudio.App.Features.Designer;
using WpfStudio.App.Features.Inspection;
using WpfStudio.Contracts;

namespace WpfStudio.Shell.Tests;

public sealed partial class DesignerTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ASelectionChangeDuringCaptureReplacesOnlyTheUpdateMessage(bool newerStatus)
    {
        var client = new FakePreview();
        var pending = new TaskCompletionSource<PreviewSnapshot>();
        client.Capture = _ => pending.Task;
        await using var model = new DesignerViewModel(client, new InlineDispatcher()) { AutoRefresh = false };
        await model.OpenAsync(Document("view.xaml"));
        var update = model.UpdateSnapshotCommand.ExecuteAsync(null);
        var version = client.Captures[0].Version;
        Assert.Contains("Updating snapshot", model.Status);
        model.SelectedNode = model.Tree[0];
        if (newerStatus) model.Status = "Newer status";
        pending.SetResult(Snapshot(version, "Capture"));
        await update;
        if (newerStatus) Assert.Equal("Newer status", model.Status);
        else Assert.Contains("selection changed during capture", model.Status);
    }
}

public sealed partial class InspectionViewModelTests
{
    [Fact]
    public async Task PausingDuringSourceVerificationCancelsItsMessage()
    {
        var ready = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        model.StubHost().Source = (_, _) => ready.Task;
        await model.AttachAsync(SourceSession(), debugging: true);
        model.SelectedNode = model.Tree[0];
        var pending = model.ShowSourceCommand.ExecuteAsync(null);
        Assert.Contains("Verifying compiled XAML source", model.SourceDescription);
        model.SetDebuggerState(true);
        Assert.Contains("cancelled because the inspection context changed", model.SourceDescription);
        ready.SetResult("Opened stale XAML");
        await pending;
        Assert.Contains("cancelled because the inspection context changed", model.SourceDescription);
    }

    [Fact]
    public async Task ThePausedBindingMessageLastsOnlyWhileExecutionIsPaused()
    {
        var session = BindingSourceSession();
        await using var model = new InspectionViewModel(new InlineDispatcher()) { AutoRefresh = false };
        model.StubHost().BindingSource = (_, _) => Task.FromResult("Unexpected");
        await model.AttachAsync(session, debugging: true);
        model.SelectedNode = model.Tree[0];
        model.SetDebuggerState(true);
        Assert.Contains("Debugger paused", model.BindingDeclarations.Status);
        model.SetDebuggerState(false);
        Assert.DoesNotContain("Debugger paused", model.BindingDeclarations.Status);
    }
}
