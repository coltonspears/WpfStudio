using System.Windows.Threading;
using WpfStudio.App.Controls;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Workspace;

namespace WpfStudio.Shell.Tests;

public sealed class EditorViewModelReportingTests
{
    private static EditorViewModel Model(WorkspaceClient workspace, string path, string text = "class C\n{\n}\n") =>
        new(new DocumentState(path, text), workspace, new XamlCompletionService(), new InlineDispatcher(), _ => { });

    private static WorkspaceDiagnostic Diagnostic(string id, string severity, int start, int length) => new(id, id, severity, null, 1, 1, start, length);

    [Fact]
    public async Task ReportCaretRecordsThePositionOnTheDocument()
    {
        await using var workspace = new WorkspaceClient();
        using var model = Model(workspace, Path.Combine(Path.GetTempPath(), "Caret.cs"));

        model.ReportCaret(2, 3, 9);

        Assert.Equal((9, 2, 3), (model.State.CaretOffset, model.State.CaretLine, model.State.CaretColumn));
    }

    [Fact]
    public async Task DiagnosticsAtReturnsThoseCoveringTheOffsetMostSevereFirst()
    {
        await using var workspace = new WorkspaceClient();
        using var model = Model(workspace, Path.Combine(Path.GetTempPath(), "Hover.cs"));
        foreach (var diagnostic in new[]
        {
            Diagnostic("info", "Info", 0, 10), Diagnostic("warning", "Warning", 0, 10), Diagnostic("error", "Error", 0, 10),
            Diagnostic("elsewhere", "Error", 20, 5), Diagnostic("zero-length", "Warning", 5, 0)
        }) model.Diagnostics.Add(diagnostic);

        Assert.Equal(["error", "warning", "zero-length", "info"], model.DiagnosticsAt(5).Select(d => d.Id));
        Assert.Equal(["error", "warning", "info"], model.DiagnosticsAt(0).Select(d => d.Id));
        Assert.Empty(model.DiagnosticsAt(10)); // The end of a span is outside it.
        Assert.Equal(["elsewhere"], model.DiagnosticsAt(24).Select(d => d.Id));
    }

    [Fact]
    public async Task DiagnosticsAtStopsAtWhatTheHoverCardHasRoomFor()
    {
        await using var workspace = new WorkspaceClient();
        using var model = Model(workspace, Path.Combine(Path.GetTempPath(), "Crowded.cs"));
        for (var index = 0; index < 6; index++) model.Diagnostics.Add(Diagnostic($"warning{index}", "Warning", 0, 4));
        model.Diagnostics.Add(Diagnostic("error", "Error", 0, 4));

        var shown = model.DiagnosticsAt(1);

        Assert.Equal(4, shown.Count);
        Assert.Equal("error", shown[0].Id);
    }

    [Fact]
    public Task TheEditorTakesItsReadOnlyStateFromTheViewModel() => OnDispatcherAsync(async () =>
    {
        await using var workspace = new WorkspaceClient();
        using var generated = Model(workspace, Path.Combine(Path.GetTempPath(), "WpfStudio", "GeneratedSources", "Generated.cs"));
        using var authored = Model(workspace, Path.Combine(Path.GetTempPath(), "Authored.cs"));
        Assert.True(generated.IsReadOnly);
        Assert.False(authored.IsReadOnly);

        var editor = new EditorSurface { ViewModel = generated };
        Assert.True(editor.IsReadOnly);
        editor.ViewModel = authored;
        Assert.False(editor.IsReadOnly);
    });

    [Fact]
    public Task TheEditorReportsItsCaretThroughTheViewModel() => OnDispatcherAsync(async () =>
    {
        await using var workspace = new WorkspaceClient();
        using var model = Model(workspace, Path.Combine(Path.GetTempPath(), "Moves.cs"), "first\nsecond\n");
        var editor = new EditorSurface { ViewModel = model };

        editor.CaretOffset = "first\nse".Length;

        Assert.Equal(("first\nse".Length, 2, 3), (model.State.CaretOffset, model.State.CaretLine, model.State.CaretColumn));

        // A restored selection reports where the caret ended up, including its line and column.
        model.RestoreSelection(2, 0, 2);
        Assert.Equal((2, 1, 3), (model.State.CaretOffset, model.State.CaretLine, model.State.CaretColumn));
    });

    private static Task OnDispatcherAsync(Func<Task> action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            var dispatcher = Dispatcher.CurrentDispatcher;
            SynchronizationContext.SetSynchronizationContext(new DispatcherSynchronizationContext(dispatcher));
            dispatcher.BeginInvoke(async () =>
            {
                try { await action(); completion.SetResult(); }
                catch (Exception exception) { completion.SetException(exception); }
                finally { dispatcher.BeginInvokeShutdown(DispatcherPriority.Background); }
            });
            Dispatcher.Run();
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }
}
