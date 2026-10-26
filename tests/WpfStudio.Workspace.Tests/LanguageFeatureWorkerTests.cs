using System.Diagnostics;
using WpfStudio.Contracts;

namespace WpfStudio.Workspace.Tests;

/// <summary>End-to-end worker requests against a real restored class library loaded through MSBuild.</summary>
public sealed class LanguageFeatureWorkerTests : IAsyncLifetime
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "WpfStudio-LanguageFeatures-" + Guid.NewGuid().ToString("N"));
    private string Project => Path.Combine(_directory, "Library.csproj");
    private string Source => Path.Combine(_directory, "Orders.cs");

    public async Task InitializeAsync()
    {
        Directory.CreateDirectory(_directory);
        await File.WriteAllTextAsync(Project, "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework><Nullable>enable</Nullable></PropertyGroup></Project>");
        await File.WriteAllTextAsync(Source, "namespace Shop;\npublic interface IOrderStore { void Save(); }\npublic sealed class OrderStore : IOrderStore { public void Save() { } }\npublic class Report { StringBuilder text = new(); }\n");
        using var restore = Process.Start(new ProcessStartInfo("dotnet", "restore --nologo -v q") { WorkingDirectory = _directory, UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true })!;
        await restore.WaitForExitAsync();
        Assert.Equal(0, restore.ExitCode);
    }

    public Task DisposeAsync() { try { Directory.Delete(_directory, true); } catch (IOException) { } return Task.CompletedTask; }

    [Fact]
    public async Task QuickActionsListAndComputeReviewedEdits()
    {
        await using var engine = new WorkspaceClient();
        await engine.LoadAsync(new LoadWorkspaceRequest(Project));
        var text = await File.ReadAllTextAsync(Source);
        Assert.True((await engine.UpdateDocumentAsync(new(Source, text, 1, Analyze: false))).Accepted);
        var caret = text.IndexOf("StringBuilder", StringComparison.Ordinal) + 3;
        var actions = await engine.GetCodeActionsAsync(new(Source, caret, 0, 1));
        var addUsing = Assert.Single(actions.Actions, action => action.Title == "using System.Text;");
        Assert.Equal("Fix", addUsing.Kind);
        var edit = await engine.ApplyCodeActionAsync(new(Source, 1, addUsing.Id));
        var document = Assert.Single(edit.Documents);
        Assert.Equal(1, document.Version);
        Assert.StartsWith("using System.Text;", Apply(text, document.Edits));

        // A newer buffer invalidates the listed actions.
        await engine.UpdateDocumentAsync(new(Source, text + " ", 2, Analyze: false));
        var stale = await Assert.ThrowsAnyAsync<Exception>(() => engine.ApplyCodeActionAsync(new(Source, 2, addUsing.Id)));
        Assert.Contains("no longer available", stale.Message);
    }

    private static string Apply(string text, IReadOnlyList<TextEdit> edits) =>
        edits.OrderByDescending(edit => edit.Start).Aggregate(text, (current, edit) => current.Remove(edit.Start, edit.Length).Insert(edit.Start, edit.NewText));

    [Fact]
    public async Task SymbolsAndImplementationsResolveAcrossTheLoadedProject()
    {
        await using var engine = new WorkspaceClient();
        await engine.LoadAsync(new LoadWorkspaceRequest(Project));
        var text = await File.ReadAllTextAsync(Source);
        await engine.UpdateDocumentAsync(new(Source, text, 1, Analyze: false));
        var symbols = await engine.SearchSymbolsAsync(new("OrdSto"));
        Assert.Contains(symbols, symbol => symbol.Name == "OrderStore" && symbol.Kind == "Class" && symbol.Container == "Shop" && symbol.Location.Line == 3);
        var implementations = await engine.FindImplementationsAsync(new(Source, text.IndexOf("void Save", StringComparison.Ordinal) + 6, 1));
        var location = Assert.Single(implementations);
        Assert.Equal(3, location.Line);
    }

    [Fact]
    public async Task DecorationsDescribeTheSynchronizedVersion()
    {
        await using var engine = new WorkspaceClient();
        await engine.LoadAsync(new LoadWorkspaceRequest(Project));
        var text = await File.ReadAllTextAsync(Source) + "public static class Use { public static void Run(OrderStore store) { store.Save(); var count = System.Math.Max(1, 2); } }\n";
        await engine.UpdateDocumentAsync(new(Source, text, 1, Analyze: false));
        var decorations = await engine.GetEditorDecorationsAsync(new(Source, 1));
        Assert.Equal(1, decorations.Version);
        Assert.Contains(decorations.Spans, span => span.Kind == "Interface" && text.Substring(span.Start, span.Length) == "IOrderStore");
        Assert.Contains(decorations.Spans, span => span.Kind == "Method" && text.Substring(span.Start, span.Length) == "Save");
        Assert.Contains(decorations.Hints, hint => hint.Text == ": int" && text[..hint.Position].EndsWith("var count", StringComparison.Ordinal));
        var stale = await Assert.ThrowsAnyAsync<Exception>(() => engine.GetEditorDecorationsAsync(new(Source, 7)));
        Assert.Contains("stale", stale.Message);
    }
}
