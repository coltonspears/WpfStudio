using System.Reflection;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Workspace;

namespace WpfStudio.Shell.Tests;

public sealed class ProjectDiagnosticsShellTests
{
    private const string Source = "namespace Shop;\npublic class Report\n{\n    private int _unused;\n    public int Bad() => \"text\";\n}\n";

    [Fact]
    public async Task APushRefreshesDiagnosticsInPlaceWithoutInvalidatingSymbolOperationsOrLenses()
    {
        await using var test = new ShellTestContext();
        string project = await test.CreateFileAsync("Library.csproj", "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        string path = await test.CreateFileAsync("Report.cs", Source);
        Assert.Equal(0, (await new BuildService().RunAsync(new(project, BuildOperation.Restore))).ExitCode);
        test.Shell.Workspace = await test.Workspace.LoadAsync(new(project));

        // Handlers run in subscription order, so these two surround the editor's and see what it does synchronously.
        EditorViewModel? editor = null;
        Func<bool>? symbolContext = null;
        int before = -1, after = -1, pushes = 0, analyses = 0;
        var symbolContextHeld = false;
        void Before(object? sender, EventArgs args)
        {
            if (editor is null) return;
            before = editor.Diagnostics.Count;
            symbolContext = CaptureSymbolContext(test.Shell, editor);
        }
        void After(object? sender, EventArgs args)
        {
            if (editor is null) return;
            after = editor.Diagnostics.Count;
            symbolContextHeld = symbolContext!();
            Interlocked.Increment(ref pushes);
        }
        test.Workspace.ProjectDiagnosticsChanged += Before;
        test.Workspace.DiagnosticsReceived += (_, _) => Interlocked.Increment(ref analyses);
        await test.Shell.OpenDocumentAsync(path);
        editor = test.Shell.ActiveDocument!;
        test.Workspace.ProjectDiagnosticsChanged += After;

        // A real push completes the first reply, which could not include the unused field.
        await WaitForAsync(() => Ids(editor).Contains("CS0029") && Ids(editor).Contains("CS0169"));
        Assert.True(Volatile.Read(ref pushes) >= 1);

        // A second one, with results on screen: they stay until the new ones replace them.
        before = after = -1;
        editor.State.Content = Source.Replace("private int _unused;", "private int _unused; private int _second;", StringComparison.Ordinal);
        await WaitForAsync(() => Ids(editor).Count(id => id == "CS0169") == 2);
        Assert.True(before > 0, "The push did not arrive with diagnostics on screen.");
        Assert.Equal(before, after);
        Assert.True(symbolContextHeld, "The push invalidated a symbol operation in progress.");

        // Settled, with lenses current. A push must leave those lenses and every revision alone, and must not loop.
        await WaitForAsync(() => editor.CodeLensVersion == editor.State.Version);
        var diagnostics = editor.Diagnostics.ToArray();
        var lensRequest = Field(editor, "_codeLens");
        var lensChanges = 0;
        editor.CodeLensChanged += () => Interlocked.Increment(ref lensChanges);
        var decorated = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        editor.DecorationsChanged += () => decorated.TrySetResult();
        var semanticRevision = Field(editor, "_semanticRevision");
        var symbolRevision = Field(test.Shell, "_symbolSemanticRevision");
        var sourceEpoch = test.Shell.XamlAnalysis.SourceEpoch;
        var heldContext = CaptureSymbolContext(test.Shell, editor);
        var pushesBefore = Volatile.Read(ref pushes);
        var analysesBefore = Volatile.Read(ref analyses);
        RaiseProjectDiagnosticsChanged(test.Workspace);
        await decorated.Task.WaitAsync(TimeSpan.FromSeconds(60));
        await Task.Delay(TimeSpan.FromSeconds(3));
        Assert.Equal(diagnostics, editor.Diagnostics.ToArray());
        Assert.Equal(semanticRevision, Field(editor, "_semanticRevision"));
        Assert.Equal(symbolRevision, Field(test.Shell, "_symbolSemanticRevision"));
        Assert.Equal(sourceEpoch, test.Shell.XamlAnalysis.SourceEpoch);
        Assert.True(heldContext());
        Assert.Same(lensRequest, Field(editor, "_codeLens"));
        Assert.Equal(0, Volatile.Read(ref lensChanges));
        Assert.Equal(editor.State.Version, editor.CodeLensVersion);
        Assert.Equal(pushesBefore + 1, Volatile.Read(ref pushes));
        Assert.Equal(analysesBefore + 1, Volatile.Read(ref analyses));
    }

    [Fact]
    public async Task OnlyCSharpEditorsAnalyseAgainAndAClosedOneStops()
    {
        await using var test = new ShellTestContext();
        string project = await test.CreateFileAsync("Library.csproj", "<Project Sdk='Microsoft.NET.Sdk'><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>");
        string notes = await test.CreateFileAsync("Notes.txt", "notes");
        string path = await test.CreateFileAsync("Report.cs", "namespace Shop;\npublic class Report { }\n");
        Assert.Equal(0, (await new BuildService().RunAsync(new(project, BuildOperation.Restore))).ExitCode);
        test.Shell.Workspace = await test.Workspace.LoadAsync(new(project));
        var analyses = 0;
        test.Workspace.DiagnosticsReceived += (_, _) => Interlocked.Increment(ref analyses);

        await test.Shell.OpenDocumentAsync(notes);
        RaiseProjectDiagnosticsChanged(test.Workspace);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(0, Volatile.Read(ref analyses));

        await test.Shell.OpenDocumentAsync(path);
        var editor = test.Shell.ActiveDocument!;
        await WaitForAsync(() => Volatile.Read(ref analyses) == 1);
        RaiseProjectDiagnosticsChanged(test.Workspace);
        await WaitForAsync(() => Volatile.Read(ref analyses) == 2);

        editor.Dispose();
        RaiseProjectDiagnosticsChanged(test.Workspace);
        await Task.Delay(TimeSpan.FromSeconds(1));
        Assert.Equal(2, Volatile.Read(ref analyses));
    }

    private static string[] Ids(EditorViewModel editor)
    {
        // The worker's replies arrive on another thread here, because the test dispatcher runs work inline.
        try { return editor.Diagnostics.Select(diagnostic => diagnostic.Id).ToArray(); }
        catch (InvalidOperationException) { return []; }
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        while (!condition()) await Task.Delay(20, timeout.Token);
    }

    private static Func<bool> CaptureSymbolContext(ShellViewModel shell, EditorViewModel editor) =>
        (Func<bool>)typeof(ShellViewModel).GetMethod("CaptureSymbolContext", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(shell, [editor, true])!;

    private static object? Field(object owner, string name) => owner.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(owner);

    private static void RaiseProjectDiagnosticsChanged(WorkspaceClient client) =>
        ((EventHandler?)typeof(WorkspaceClient).GetField(nameof(WorkspaceClient.ProjectDiagnosticsChanged), BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(client))?.Invoke(client, EventArgs.Empty);
}
