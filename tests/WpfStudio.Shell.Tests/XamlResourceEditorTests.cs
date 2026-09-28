using System.Collections.Concurrent;
using WpfStudio.App.Services;
using WpfStudio.App.ViewModels;
using WpfStudio.Contracts;
using WpfStudio.Core.Documents;
using WpfStudio.Workspace;

namespace WpfStudio.Shell.Tests;

public sealed class XamlResourceEditorTests
{
    private const string Models = "namespace Fixture; public sealed class Customer { public string Name { get; set; } = \"\"; } public sealed class Supplier { public string Code { get; set; } = \"\"; } public sealed class Order { public string Title { get; set; } = \"\"; }";

    [Fact]
    public async Task OpeningEditingSavingAndDiscardingDictionaryInvalidateUnchangedConsumer()
    {
        await using var test = new ShellTestContext();
        string view = await test.CreateFileAsync("View.xaml", View("Name"));
        string data = await test.CreateFileAsync("Data.xaml", Dictionary("Customer"));
        await test.Shell.OpenDocumentAsync(view);
        var consumer = test.Shell.ActiveDocument!;
        long version = consumer.State.Version, revision = consumer.XamlContextRevision;

        // A hidden transaction prerequisite is not a user-open resource overlay.
        await test.Store.OpenAsync(data);
        Assert.Equal(revision, consumer.XamlContextRevision);
        await test.Shell.OpenDocumentAsync(data);
        var dictionary = test.Shell.ActiveDocument!;
        Assert.True(consumer.XamlContextRevision > revision);
        revision = consumer.XamlContextRevision;
        consumer.Diagnostics.Add(new("old", "old resource type", "Error", view, 1, 1, 0, 1));
        dictionary.State.Content = Dictionary("Order");
        Assert.Empty(consumer.Diagnostics);
        Assert.True(consumer.XamlContextRevision > revision);
        Assert.Throws<InvalidOperationException>(() => consumer.XamlCompletionEdit(new("old", "Name", "Name", null, []), 0, 0, revision));

        revision = consumer.XamlContextRevision;
        await test.Shell.SaveCommand.ExecuteAsync(null);
        Assert.True(consumer.XamlContextRevision > revision);
        Assert.Equal(Dictionary("Order"), await File.ReadAllTextAsync(data));
        dictionary.State.Content = Dictionary("Customer");
        revision = consumer.XamlContextRevision;
        test.Dialogs.SaveDecisions.Enqueue(SaveDecision.Discard);
        Assert.True(await test.Shell.CloseDocumentAsync(dictionary));
        Assert.True(consumer.XamlContextRevision > revision);
        Assert.Equal(version, consumer.State.Version);
        Assert.Equal(View("Name"), consumer.State.Content);
        Assert.Null(test.Store.Find(data));
        Assert.Empty(test.Dialogs.Errors);
    }

    [Fact]
    public async Task UnsavedDictionaryDrivesEveryEditorRequestAndCloseRestoresDiskEvidence()
    {
        await using var test = new ShellTestContext();
        var files = await LoadAsync(test);
        await test.Shell.OpenDocumentAsync(files.View);
        var consumer = test.Shell.ActiveDocument!;
        int position = Position(consumer.State.Content);
        Assert.Contains((await consumer.CompleteAsync(position)).Items, item => item.DisplayText == "Name");
        Assert.Contains("Customer", Assert.IsType<XamlHoverInfo>(await consumer.HoverAsync(position + 1)).Text);
        Assert.Equal(files.Model, Assert.Single(await consumer.XamlDefinitionAsync(position + 1)).Path);
        await test.Shell.OpenDocumentAsync(files.Data);
        var dictionary = test.Shell.ActiveDocument!;
        long consumerVersion = consumer.State.Version;

        dictionary.State.Content = Dictionary("Order");

        // The consumer was not edited or explicitly refreshed. Its debounce is
        // rescheduled by the resource overlay notification.
        await UntilAsync(() => consumer.Diagnostics.Any(item => item.Id == "XAMLBIND001"));
        Assert.Equal(consumerVersion, consumer.State.Version);
        var completion = await consumer.CompleteAsync(position);
        Assert.Contains(completion.Items, item => item.DisplayText == "Title");
        Assert.DoesNotContain(completion.Items, item => item.DisplayText == "Name");
        consumer.State.Content = View("Title");
        position = Position(consumer.State.Content);
        Assert.Contains("Order", Assert.IsType<XamlHoverInfo>(await consumer.HoverAsync(position + 1)).Text);
        var definition = Assert.Single(await consumer.XamlDefinitionAsync(position + 1));
        Assert.Equal("Title", Models.Substring(definition.Start, definition.Length));

        consumer.State.Content = View("Ttile");
        consumer.State.CaretOffset = position + 1;
        await consumer.RefreshQuickFixesAsync(position + 1);
        var fix = Assert.Single(consumer.QuickFixes);
        Assert.Contains(fix.Action.Edit.Edits, edit => edit.NewText == "Title");
        string typo = consumer.State.Content;
        dictionary.State.Content = Dictionary("Customer");
        Assert.Empty(consumer.QuickFixes);
        await fix.ApplyCommand.ExecuteAsync(fix.Action);
        Assert.Equal(typo, consumer.State.Content);

        consumer.State.Content = View("Name");
        position = Position(consumer.State.Content);
        // Each call captures Customer, then a different editor changes before
        // the worker replies. The consumer's own version is unchanged.
        Task<CompletionResult> pendingCompletion = consumer.CompleteAsync(position);
        Task<XamlHoverInfo?> pendingHover = consumer.HoverAsync(position + 1);
        Task<IReadOnlyList<SourceLocation>> pendingDefinition = consumer.XamlDefinitionAsync(position + 1);
        dictionary.State.Content = Dictionary("Order");
        Assert.Empty((await pendingCompletion).Items);
        Assert.Null(await pendingHover);
        Assert.Empty(await pendingDefinition);

        consumer.State.Content = View("Ttile");
        Task pendingActions = consumer.RefreshQuickFixesAsync(position + 1);
        dictionary.State.Content = Dictionary("Customer");
        await pendingActions;
        Assert.Empty(consumer.QuickFixes);

        dictionary.State.Content = Dictionary("Order");
        consumer.State.Content = View("Name");
        await consumer.RefreshAnalysisAsync();
        Assert.Contains(consumer.Diagnostics, item => item.Id == "XAMLBIND001");
        long unchangedVersion = consumer.State.Version;
        test.Dialogs.SaveDecisions.Enqueue(SaveDecision.Discard);
        Assert.True(await test.Shell.CloseDocumentAsync(dictionary));
        await consumer.RefreshAnalysisAsync();
        Assert.Empty(consumer.Diagnostics);
        Assert.Contains((await consumer.CompleteAsync(position)).Items, item => item.DisplayText == "Name");
        Assert.Equal(unchangedVersion, consumer.State.Version);
        Assert.Equal(Dictionary("Customer"), await File.ReadAllTextAsync(files.Data));
        Assert.Null(test.Store.Find(files.Data));
        Assert.Empty(test.Dialogs.Errors);
    }

    [Fact]
    public async Task ClosedDictionaryDiskChangesReanalyzeConsumerEvenWithUnchangedTimestampAndLength()
    {
        await using var test = new ShellTestContext();
        var files = await LoadAsync(test);
        await test.Shell.OpenDocumentAsync(files.View);
        var consumer = test.Shell.ActiveDocument!;
        await consumer.RefreshAnalysisAsync();
        Assert.Empty(consumer.Diagnostics);
        long version = consumer.State.Version;
        DateTime timestamp = File.GetLastWriteTimeUtc(files.Data);
        string changed = Dictionary("Supplier");
        Assert.Equal(Dictionary("Customer").Length, changed.Length);

        await File.WriteAllTextAsync(files.Data, changed);
        File.SetLastWriteTimeUtc(files.Data, timestamp);

        await UntilAsync(() => consumer.Diagnostics.Any(item => item.Id == "XAMLBIND001"));
        Assert.Equal(version, consumer.State.Version);
        var completion = await consumer.CompleteAsync(Position(consumer.State.Content));
        Assert.Contains(completion.Items, item => item.DisplayText == "Code");
        Assert.DoesNotContain(completion.Items, item => item.DisplayText == "Name");
        Assert.Null(test.Store.Find(files.Data));
        await File.WriteAllTextAsync(files.Data, Dictionary("Customer"));
        File.SetLastWriteTimeUtc(files.Data, timestamp);
        await UntilAsync(() => consumer.Diagnostics.Count == 0);
        // Clearing is immediate; wait for the current semantic observation too.
        await consumer.RefreshAnalysisAsync();
        Assert.Empty(consumer.Diagnostics);
        Assert.Contains((await consumer.CompleteAsync(Position(consumer.State.Content))).Items, item => item.DisplayText == "Name");
    }

    [Fact]
    public async Task ResourceInvalidationRejectsQueuedAnalysisBeforeDispatcherNotification()
    {
        await using var test = new ShellTestContext();
        var files = await LoadAsync(test, useShellWorkspace: false);
        var state = await test.Store.OpenAsync(files.View);
        var resource = await test.Store.OpenAsync(files.Data);
        state.Content = View("Nmae");
        var resources = new XamlResourceContext();
        resources.SetOpenDocuments([state, resource]);
        var dispatcher = new HeldDispatcher();
        using var editor = new EditorViewModel(state, test.Workspace, test.Xaml, dispatcher, _ => { }, resources);
        long version = state.Version, context = editor.XamlContextRevision;
        Task pending = editor.RefreshAnalysisAsync();
        await dispatcher.InvocationReady.Task.WaitAsync(TimeSpan.FromSeconds(20));
        var old = resources.Capture();
        resource.Content = Dictionary("Order");

        resources.SetOpenDocuments([state, resource]);

        Assert.Equal(Dictionary("Customer"), Assert.Single(old.Overlays, item => item.Path == files.Data).Text);
        Assert.False(resources.IsCurrent(old.Generation));
        Assert.True(editor.XamlContextRevision > context); // Before Post is executed.
        Assert.Equal(version, state.Version);
        dispatcher.ReleaseInvocation();
        await pending;
        Assert.Empty(editor.Diagnostics); // The completed Customer typo result never publishes.
        long disposedContext = editor.XamlContextRevision;
        editor.Dispose();
        resources.Invalidate();
        Assert.Equal(disposedContext, editor.XamlContextRevision);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task SpellingFixValidatesResourcePrerequisiteWithoutOpeningEditingOrUndoingIt(bool alreadyOpen)
    {
        await using var test = new ShellTestContext();
        string view = await test.CreateFileAsync("View.xaml", View("Nmae"));
        string data = await test.CreateFileAsync("Data.xaml", Dictionary("Customer"));
        if (alreadyOpen) await test.Shell.OpenDocumentAsync(data);
        var resource = test.Store.Find(data);
        await test.Shell.OpenDocumentAsync(view);
        var editor = test.Shell.ActiveDocument!;
        var action = SpellingFix(editor, data, Dictionary("Customer"), resource?.Version ?? 0);
        // Read-only evidence may establish a fix; it is never a mutation target.
        File.SetAttributes(data, File.GetAttributes(data) | FileAttributes.ReadOnly);
        try { await test.Shell.ApplyXamlCodeActionAsync(editor, action); }
        finally { File.SetAttributes(data, File.GetAttributes(data) & ~FileAttributes.ReadOnly); }

        Assert.Equal(View("Name"), editor.State.Content);
        Assert.Same(editor, test.Shell.ActiveDocument);
        Assert.Equal(alreadyOpen ? 2 : 1, test.Shell.Documents.Count);
        Assert.Same(resource, test.Store.Find(data)); // No hidden clean cache after apply.
        Assert.Equal(Dictionary("Customer"), await File.ReadAllTextAsync(data));
        if (resource is not null) resource.Content = Dictionary("Order");
        else await File.WriteAllTextAsync(data, Dictionary("Order"));
        await test.Shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        Assert.Equal(View("Nmae"), editor.State.Content);
        Assert.Equal(Dictionary("Order"), resource?.Content ?? await File.ReadAllTextAsync(data));
        Assert.Empty(test.Dialogs.Errors);
    }

    [Theory]
    [InlineData("closed-disk")]
    [InlineData("open-disk")]
    [InlineData("open-version")]
    [InlineData("missing-hash")]
    [InlineData("resource-edit")]
    public async Task ChangedOrUnverifiedResourcePrerequisiteRejectsSpellingFix(string change)
    {
        await using var test = new ShellTestContext();
        string view = await test.CreateFileAsync("View.xaml", View("Nmae"));
        string data = await test.CreateFileAsync("Data.xaml", Dictionary("Customer"));
        if (change.StartsWith("open-", StringComparison.Ordinal)) await test.Shell.OpenDocumentAsync(data);
        var resource = test.Store.Find(data);
        await test.Shell.OpenDocumentAsync(view);
        var editor = test.Shell.ActiveDocument!;
        var action = SpellingFix(editor, data, Dictionary("Customer"), resource?.Version ?? 0);
        switch (change)
        {
            case "closed-disk": case "open-disk": await File.WriteAllTextAsync(data, Dictionary("Order")); break;
            case "open-version": resource!.Content += " "; resource.Content = Dictionary("Customer"); break;
            case "missing-hash": action = action with { AdditionalEdits = [action.AdditionalEdits![0] with { ExpectedTextHash = null }] }; break;
            case "resource-edit": action = action with { AdditionalEdits = [action.AdditionalEdits![0] with { Edits = [new(0, 0, "<!-- injected -->")] }] }; break;
        }

        await Assert.ThrowsAnyAsync<Exception>(() => test.Shell.ApplyXamlCodeActionAsync(editor, action));

        Assert.Equal(View("Nmae"), editor.State.Content);
        Assert.Same(resource, test.Store.Find(data)); // Rejected closed reads do not linger either.
        await test.Shell.UndoWorkspaceEditCommand.ExecuteAsync(null);
        Assert.Equal(View("Nmae"), editor.State.Content);
    }

    private static XamlCodeAction SpellingFix(EditorViewModel editor, string dictionary, string text, long version) => new("Use 'Name'",
        new(editor.State.Path, editor.State.Version, [new(Position(editor.State.Content), 4, "Name")], Hash(editor.State.Content)),
        [new(dictionary, version, [], Hash(text))]);

    private static string Hash(string text) => DocumentStore.Hash(System.Text.Encoding.UTF8.GetBytes(text));

    private static async Task<(string View, string Data, string Model)> LoadAsync(ShellTestContext test, bool useShellWorkspace = true)
    {
        string project = await test.CreateFileAsync("Fixture.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0-windows</TargetFramework><UseWPF>true</UseWPF></PropertyGroup></Project>");
        string model = await test.CreateFileAsync("Models.cs", Models);
        string view = await test.CreateFileAsync("View.xaml", View("Name"));
        string data = await test.CreateFileAsync("Data.xaml", Dictionary("Customer"));
        Assert.Equal(0, (await new BuildService().RunAsync(new(project, BuildOperation.Restore, "Release"))).ExitCode);
        var workspace = await test.Workspace.LoadAsync(new(project, "Release"));
        Assert.DoesNotContain(workspace.Issues, issue => issue.Severity == "Error");
        if (useShellWorkspace) test.Shell.Workspace = workspace;
        return (view, data, model);
    }

    private static int Position(string text) => text.IndexOf("Path=", StringComparison.Ordinal) + 5;
    private static string Dictionary(string type) => $$"""
        <ResourceDictionary xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
            xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml" xmlns:m="clr-namespace:Fixture">
            <m:{{type}} x:Key="Current" />
        </ResourceDictionary>
        """;
    private static string View(string member) => $$"""
        <UserControl xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
            <UserControl.Resources><ResourceDictionary><ResourceDictionary.MergedDictionaries>
                <ResourceDictionary Source="Data.xaml" />
            </ResourceDictionary.MergedDictionaries></ResourceDictionary></UserControl.Resources>
            <TextBlock Text="{Binding Source={StaticResource Current}, Path={{member}}}" />
        </UserControl>
        """;

    private static async Task UntilAsync(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        while (!condition()) await Task.Delay(30, timeout.Token);
    }

    private sealed class HeldDispatcher : IUiDispatcher
    {
        private readonly ConcurrentQueue<(Action Action, TaskCompletionSource Completion)> _invocations = new();
        public TaskCompletionSource InvocationReady { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public void Post(Action action) { } // Deliberately hold invalidation notifications until after publication.
        public Task InvokeAsync(Action action, CancellationToken cancellationToken = default)
        {
            var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            _invocations.Enqueue((action, completion));
            InvocationReady.TrySetResult();
            return completion.Task;
        }
        public void ReleaseInvocation()
        {
            Assert.True(_invocations.TryDequeue(out var invocation));
            invocation.Action();
            invocation.Completion.SetResult();
        }
    }
}
