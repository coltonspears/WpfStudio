using System.Collections.Concurrent;
using Microsoft.Extensions.DependencyInjection;
using WpfStudio.App.Services;
using WpfStudio.Contracts;

namespace WpfStudio.Shell.Tests;

public sealed class XamlDiagnosticsTests
{
    [Fact]
    public Task SavingAndClosingAFixedXamlFileDoesNotRestoreAnOldIndexError() => ShellTestContext.OnUiThreadAsync(async test =>
    {
        var path = await test.CreateFileAsync("View.xaml", "<Grid><");
        var project = await test.CreateFileAsync("Fixture.csproj", "<Project />");
        test.Shell.Workspace = new WorkspaceSnapshot(project, "Test", [new("fixture", "Fixture", project, "net10.0-windows", null, false, [new(path, "View.xaml", "Page")])], []);
        await test.Shell.RefreshWpfCommand.ExecuteAsync(null);
        Assert.Contains(test.Shell.Diagnostics, issue => issue.Id == "XAML001");
        await test.Shell.OpenDocumentAsync(path);
        var editor = test.Shell.ActiveDocument!;
        editor.State.Content = "<Grid />";
        await test.Shell.SaveCommand.ExecuteAsync(null);

        Assert.True(await test.Shell.CloseDocumentAsync(editor));

        Assert.DoesNotContain(test.Shell.Diagnostics, issue => issue.Id == "XAML001");
        Assert.DoesNotContain(test.Shell.WpfExplorer.Issues, issue => issue.Id == "XAML001");
    });

    [Fact]
    public Task AnOldIndexErrorStaysHiddenWhenTheWatcherHasNotReportedTheSaveYet() => ShellTestContext.OnUiThreadAsync(async test =>
    {
        var path = await test.CreateFileAsync("View.xaml", "<Grid><");
        var project = await test.CreateFileAsync("Fixture.csproj", "<Project />");
        test.Shell.Workspace = new WorkspaceSnapshot(project, "Test", [new("fixture", "Fixture", project, "net10.0-windows", null, false, [new(path, "View.xaml", "Page")])], []);
        await test.Shell.RefreshWpfCommand.ExecuteAsync(null);
        await test.Shell.OpenDocumentAsync(path);
        var editor = test.Shell.ActiveDocument!;
        editor.State.Content = "<Grid />";
        await test.Shell.SaveCommand.ExecuteAsync(null);
        bool exposed = false;
        test.Shell.Diagnostics.CollectionChanged += (_, args) => exposed |= args.NewItems?.Cast<WorkspaceDiagnostic>().Any(issue => issue.Id == "XAML001") == true;

        Assert.True(await test.Shell.CloseDocumentAsync(editor));

        Assert.False(exposed);
        Assert.DoesNotContain(test.Shell.Diagnostics, issue => issue.Id == "XAML001");
        Assert.Equal("<Grid />", test.Shell.WpfExplorer.Index!.Texts[path]);
    }, services =>
    {
        services.AddSingleton<KnownSourceWatcherFactory>((_, _, _, _, _) => new SilentWatcher());
        services.AddSingleton<OpenDocumentWatcherFactory>(_ => new SilentWatcher());
    });

    [Fact]
    public Task AWatchedFileChangeRewritesTheIssueListsOnlyOnTheUiThread() => ShellTestContext.OnUiThreadAsync(async test =>
    {
        var path = await test.CreateFileAsync("View.xaml", "<Grid />");
        var project = await test.CreateFileAsync("Fixture.csproj", "<Project />");
        test.Shell.Workspace = new WorkspaceSnapshot(project, "Test", [new("fixture", "Fixture", project, "net10.0-windows", null, false, [new(path, "View.xaml", "Page")])], []);
        int uiThread = Environment.CurrentManagedThreadId, writes = 0;
        var otherThreads = new ConcurrentBag<int>();
        void Record()
        {
            Interlocked.Increment(ref writes);
            if (Environment.CurrentManagedThreadId != uiThread) otherThreads.Add(Environment.CurrentManagedThreadId);
        }
        test.Shell.Diagnostics.CollectionChanged += (_, _) => Record();
        test.Shell.WpfExplorer.Issues.CollectionChanged += (_, _) => Record();

        // The watcher reports this from its own thread; the lists are bound to the UI, so the rewrite must not happen there.
        await File.WriteAllTextAsync(path, "<Grid><");

        using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10)))
            while (Volatile.Read(ref writes) == 0) await Task.Delay(10, deadline.Token);
        Assert.Empty(otherThreads);
    });

    [Fact]
    public async Task LiveBindingIssuesReachBothIssueListsAndLeaveWhenTheDocumentCloses()
    {
        await using var test = new ShellTestContext();
        var path = await test.CreateFileAsync("View.xaml", "<TextBlock Text=\"{Binding Nmae}\" />");
        await test.Shell.OpenDocumentAsync(path);
        var editor = test.Shell.ActiveDocument!;
        var issue = new WorkspaceDiagnostic("XAMLBIND001", "Property Nmae is missing", "Warning", path, 1, 27, 26, 4);

        editor.Diagnostics.Add(issue);

        Assert.Contains(issue, test.Shell.Diagnostics);
        Assert.Contains(issue, test.Shell.WpfExplorer.Issues);
        Assert.True(await test.Shell.CloseDocumentAsync(editor));
        Assert.DoesNotContain(issue, test.Shell.Diagnostics);
        Assert.DoesNotContain(issue, test.Shell.WpfExplorer.Issues);
    }

    private sealed class SilentWatcher : IKnownSourceWatcher, IOpenDocumentWatcher
    {
        public string? Status => null;
        public void Watch(IEnumerable<string> paths) { }
        public void Dispose() { }
    }
}
