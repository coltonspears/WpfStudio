using System.Collections.ObjectModel;
using WpfStudio.App.Features.Workbench;
using WpfStudio.Contracts;

namespace WpfStudio.Shell.Tests;

/// <summary>The Problems pane view model against a fake host, without a shell.</summary>
public sealed class ProblemsViewModelTests
{
    private static WorkspaceDiagnostic Diagnostic(string id, string message, string severity, string path, string project = "Shop") =>
        new(id, message, severity, path, 1, 1, 0, 1, ProjectName: project);

    [Fact]
    public void CountsAndVisibleRowsFollowSeverityScopeAndText()
    {
        var all = new ObservableCollection<WorkspaceDiagnostic>();
        var host = new Host { OpenPaths = [@"C:\Shop\Order.cs"], Active = (@"C:\Shop\Order.cs", 1) };
        var problems = new ProblemsViewModel(host, all);
        all.Add(Diagnostic("CS0103", "Missing name", "Error", @"C:\Shop\Order.cs"));
        all.Add(Diagnostic("CS0168", "Unused variable", "Warning", @"C:\Shop\Invoice.cs"));
        all.Add(Diagnostic("IDE0005", "Unnecessary using", "Info", @"C:\Shop\Invoice.cs"));
        problems.Refresh();

        Assert.Equal((1, 1, 1), (problems.ErrorCount, problems.WarningCount, problems.MessageCount));
        Assert.Equal(3, problems.Visible.Count);
        Assert.False(problems.IsFilterActive);
        Assert.Equal("", problems.FilterSummary);

        problems.ShowWarnings = false;
        Assert.Equal(["CS0103", "IDE0005"], problems.Visible.Select(d => d.Id));
        Assert.Equal("2 of 3 shown", problems.FilterSummary);

        problems.ShowWarnings = true; problems.Scope = ProblemsViewModel.OpenFilesScope;
        Assert.Equal("CS0103", Assert.Single(problems.Visible).Id);
        host.OpenPaths = [@"C:\Shop\Order.cs", @"C:\Shop\Invoice.cs"]; host.ChangeDocuments();
        Assert.Equal(3, problems.Visible.Count);

        problems.Scope = ProblemsViewModel.CurrentFileScope; problems.Filter = "-cs0103";
        Assert.Empty(problems.Visible);
        problems.ClearFilterCommand.Execute(null);
        Assert.Equal(3, problems.Visible.Count);
        Assert.False(problems.IsFilterActive);
    }

    [Fact]
    public void ASelectionThatLeavesTheVisibleRowsIsCleared()
    {
        var all = new ObservableCollection<WorkspaceDiagnostic> { Diagnostic("CS0103", "Missing name", "Error", @"C:\Shop\Order.cs") };
        var problems = new ProblemsViewModel(new Host(), all);
        problems.Refresh();
        problems.Selected = all[0];
        problems.ShowErrors = false;
        Assert.Null(problems.Selected);
    }

    private sealed class Host : IWorkbenchHost
    {
        public List<string> OpenPaths { get; set; } = [];
        public (string Path, int Line)? Active { get; set; }
        public event Action? DocumentsChanged;
        public void ChangeDocuments() => DocumentsChanged?.Invoke();
        public string? WorkspacePath => @"C:\Shop\Shop.sln";
        public IReadOnlyList<string> WorkspaceFiles => [];
        public IReadOnlyList<string> OpenDocumentPaths => OpenPaths;
        public IReadOnlyDictionary<string, string> OpenDocumentTexts() => new Dictionary<string, string>();
        public (string Path, int Line)? ActiveLocation => Active;
        public string? SelectedExplorerFile => null;
        public IUserDialogService Dialogs => new FakeDialogs();
        public CancellationToken Lifetime => CancellationToken.None;
        public Task GuardAsync(Func<Task> action) => action();
        public void ReportStatus(string status) { }
        public Task NavigateAsync(string path, int line, int column) => Task.CompletedTask;
        public Task OpenDocumentAsync(string path) => Task.CompletedTask;
        public Task<bool> CloseDocumentAsync(string path) => Task.FromResult(true);
    }
}
