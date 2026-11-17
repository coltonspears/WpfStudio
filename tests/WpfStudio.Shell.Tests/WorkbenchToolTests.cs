using CommunityToolkit.Mvvm.Messaging;
using WpfStudio.App.Features.Workbench;
using WpfStudio.App.Services;
using WpfStudio.Contracts;
using WpfStudio.Core;

namespace WpfStudio.Shell.Tests;

/// <summary>The Task List and file groups view models against a fake host, without a shell.</summary>
public sealed class WorkbenchToolTests
{
    [Fact]
    public async Task TaskListScansWorkspaceFilesPrefersOpenBuffersAndFilters()
    {
        var root = NewDirectory();
        try
        {
            var onDisk = Path.Combine(root, "Disk.cs"); await File.WriteAllTextAsync(onDisk, "// TODO disk work\nclass Disk { }");
            var open = Path.Combine(root, "Open.cs"); await File.WriteAllTextAsync(open, "// TODO stale text on disk");
            var host = new FakeHost(root) { Files = [onDisk, open], Buffers = { [open] = "// HACK buffered text" } };
            var tasks = new TaskListViewModel(host, new WorkspaceStateStore<UserTaskItem>("tasks", root), new WeakReferenceMessenger());

            await tasks.RefreshCommand.ExecuteAsync(null);

            Assert.Equal(["HACK", "TODO"], tasks.Comments.Select(c => c.Token));
            Assert.Contains("2 comments in 2 files", tasks.Status);
            tasks.Filter = "disk";
            Assert.Equal("disk work", Assert.Single(tasks.Comments).Text);
            tasks.UpdateComments(onDisk, "class Disk { }");
            Assert.Empty(tasks.Comments);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ARefreshDuringAScanReplacesItWithoutReportingAFailure()
    {
        var root = NewDirectory();
        try
        {
            var host = new FakeHost(root);
            for (var index = 0; index < 5000; index++)
            {
                var path = Path.Combine(root, $"File{index}.cs");
                host.Files.Add(path);
                host.Buffers[path] = "// TODO one";
            }
            var tasks = new TaskListViewModel(host, new WorkspaceStateStore<UserTaskItem>("tasks", root), new WeakReferenceMessenger());

            var superseded = tasks.RefreshCommand.ExecuteAsync(null);
            var latest = tasks.RefreshCommand.ExecuteAsync(null);
            await latest;
            await superseded;

            Assert.Equal(5000, tasks.Comments.Count);
            Assert.Contains("5000 comments", tasks.Status);
            Assert.Equal("", host.Status);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task UserTasksLinkToTheCaretWithAnAtPrefixAndPersistPerWorkspace()
    {
        var root = NewDirectory();
        try
        {
            var host = new FakeHost(root) { Active = (Path.Combine(root, "Main.xaml"), 12) };
            var store = new WorkspaceStateStore<UserTaskItem>("tasks", root);
            var tasks = new TaskListViewModel(host, store, new WeakReferenceMessenger());
            tasks.NewTaskText = "@ wire the button"; await tasks.AddCommand.ExecuteAsync(null);
            tasks.NewTaskText = "write docs"; await tasks.AddCommand.ExecuteAsync(null);
            Assert.Equal("", tasks.NewTaskText);
            Assert.Equal("Main.xaml:12", tasks.UserTasks[0].Location);
            Assert.False(tasks.UserTasks[1].HasLocation);

            tasks.UserTasks[1].IsDone = true;
            await tasks.ClearCompletedCommand.ExecuteAsync(null);
            var reloaded = new TaskListViewModel(host, store, new WeakReferenceMessenger());
            await reloaded.LoadUserTasksAsync();
            Assert.Equal("wire the button", Assert.Single(reloaded.UserTasks).Text);
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task AddingATaskWithoutAWorkspaceReportsThroughTheHost()
    {
        var host = new FakeHost(null);
        var tasks = new TaskListViewModel(host, new WorkspaceStateStore<UserTaskItem>("tasks", Path.GetTempPath()), new WeakReferenceMessenger());
        tasks.NewTaskText = "anything"; await tasks.AddCommand.ExecuteAsync(null);
        Assert.Empty(tasks.UserTasks);
        Assert.Equal("Open a solution to keep a task list for it", host.Status);
    }

    [Fact]
    public async Task FileGroupsPinOpenTabsThenOpenAndSwitchThroughTheHost()
    {
        var root = NewDirectory();
        try
        {
            var view = Path.Combine(root, "InvoiceView.xaml"); await File.WriteAllTextAsync(view, "<Grid />");
            var model = Path.Combine(root, "InvoiceViewModel.cs"); await File.WriteAllTextAsync(model, "class InvoiceViewModel { }");
            var other = Path.Combine(root, "Other.cs"); await File.WriteAllTextAsync(other, "class Other { }");
            var host = new FakeHost(root) { OpenPaths = [view, model] };
            var messenger = new WeakReferenceMessenger();
            string? shownTool = null;
            messenger.Register<ShowToolMessage>(this, (_, message) => shownTool = message.ToolId);
            var groups = new FileGroupsViewModel(host, new WorkspaceStateStore<FileGroup>("file-groups", root), messenger);

            groups.AddOpenTabsCommand.Execute(null);
            Assert.True(groups.Picker.IsOpen);
            Assert.Equal(["InvoiceView.xaml", "InvoiceViewModel.cs"], groups.Picker.Files.Select(f => f.Name));
            Assert.True(Assert.Single(groups.Picker.Choices).IsNew);
            Assert.Empty(groups.Groups);
            groups.Picker.NewGroupName = "Invoices";
            await groups.Picker.AddCommand.ExecuteAsync(null);
            Assert.False(groups.Picker.IsOpen);
            var group = Assert.Single(groups.Groups);
            Assert.Equal("Invoices", group.Name);
            Assert.Equal("2 files", group.Summary);
            Assert.Equal(Workbenches.FileGroups, shownTool);

            host.OpenPaths = [other];
            await groups.SwitchToGroupCommand.ExecuteAsync(group);
            Assert.Equal([other], host.Closed);
            Assert.Equal([view, model], host.Opened);
            Assert.Equal("Opened Invoices", host.Status);

            var reloaded = new FileGroupsViewModel(host, new WorkspaceStateStore<FileGroup>("file-groups", root), messenger);
            await reloaded.LoadAsync();
            Assert.Equal(["InvoiceView.xaml", "InvoiceViewModel.cs"], Assert.Single(reloaded.Groups).Files.Select(f => f.Name));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task ThePickerListsTheGroupsAndAddsToTheChosenOneOrANewOne()
    {
        var root = NewDirectory();
        try
        {
            var a = Path.Combine(root, "A.cs");
            var b = Path.Combine(root, "B.cs");
            var host = new FakeHost(root) { OpenPaths = [a, b], Active = (b, 1) };
            var groups = new FileGroupsViewModel(host, new WorkspaceStateStore<FileGroup>("file-groups", root), new WeakReferenceMessenger());
            groups.Groups.Add(new("Orders", [a]));
            groups.Groups.Add(new("Billing", []));
            var picker = groups.Picker;

            groups.AddOpenTabsCommand.Execute(null);
            Assert.Equal(["Orders", "Billing", "New group"], picker.Choices.Select(c => c.Name));
            Assert.Equal(["1 file · has 1 of these", "0 files", "Name it below"], picker.Choices.Select(c => c.Detail));
            Assert.True(picker.IsNewGroup);
            Assert.True(picker.FocusNewName);
            await picker.AddCommand.ExecuteAsync(null);
            Assert.Equal("Name the new group.", picker.Error);
            Assert.True(picker.IsOpen);

            picker.Selected = picker.Choices[0];
            Assert.Equal("", picker.Error);
            Assert.Equal("Add to Orders", picker.AddLabel);
            await picker.AddCommand.ExecuteAsync(null);
            Assert.False(picker.IsOpen);
            Assert.Equal(["A.cs", "B.cs"], groups.Groups[0].Files.Select(f => f.Name));
            Assert.Equal("Orders: 2 files", host.Status);

            // The group used last is chosen the next time; typing a name chooses a new group instead.
            groups.AddActiveCommand.Execute(null);
            Assert.Same(groups.Groups[0], picker.Selected?.Group);
            Assert.Equal(("1 file", "2 files · has all of these"), (picker.FilesSummary, picker.Choices[0].Detail));
            picker.NewGroupName = "Reports";
            Assert.True(picker.IsNewGroup);
            Assert.Equal("Create group", picker.AddLabel);
            picker.CancelCommand.Execute(null);
            Assert.False(picker.IsOpen);
            Assert.Equal(["Orders", "Billing"], groups.Groups.Select(g => g.Name));

            // A new name that matches a group adds to that group instead of making a second one.
            groups.AddActiveCommand.Execute(null);
            Assert.Equal("", picker.NewGroupName);
            picker.NewGroupName = "billing";
            await picker.AddCommand.ExecuteAsync(null);
            Assert.Equal(["Orders", "Billing"], groups.Groups.Select(g => g.Name));
            Assert.Equal(["B.cs"], groups.Groups[1].Files.Select(f => f.Name));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task RenamingAGroupToAnExistingNameIsRefused()
    {
        var root = NewDirectory();
        try
        {
            var host = new FakeHost(root);
            var groups = new FileGroupsViewModel(host, new WorkspaceStateStore<FileGroup>("file-groups", root), new WeakReferenceMessenger());
            groups.Groups.Add(new("One", [Path.Combine(root, "a.cs")]));
            groups.Groups.Add(new("Two", [Path.Combine(root, "b.cs")]));
            host.Dialogs.Prompts.Enqueue("one");
            await groups.RenameGroupCommand.ExecuteAsync(groups.Groups[1]);
            Assert.Equal("Two", groups.Groups[1].Name);
            Assert.Equal("A group named one already exists", host.Status);
        }
        finally { Directory.Delete(root, true); }
    }

    private static string NewDirectory() => Directory.CreateTempSubdirectory("WpfStudio-Workbench-").FullName;

    private sealed class FakeHost(string? workspaceRoot) : IWorkbenchHost
    {
        public List<string> Files { get; set; } = [];
        public List<string> OpenPaths { get; set; } = [];
        public Dictionary<string, string> Buffers { get; } = new(StringComparer.OrdinalIgnoreCase);
        public (string Path, int Line)? Active { get; set; }
        public string? SelectedFile { get; set; }
        public FakeDialogs Dialogs { get; } = new();
        public string Status { get; private set; } = "";
        public List<string> Opened { get; } = [];
        public List<string> Closed { get; } = [];
        public List<(string Path, int Line, int Column)> Navigations { get; } = [];

        public string? WorkspacePath => workspaceRoot is null ? null : Path.Combine(workspaceRoot, "App.sln");
        public IReadOnlyList<string> WorkspaceFiles => Files;
        public IReadOnlyList<string> OpenDocumentPaths => OpenPaths;
        public event Action? DocumentsChanged;
        public void ChangeDocuments() => DocumentsChanged?.Invoke();
        public IReadOnlyDictionary<string, string> OpenDocumentTexts() => Buffers;
        public (string Path, int Line)? ActiveLocation => Active;
        public string? SelectedExplorerFile => SelectedFile;
        IUserDialogService IWorkbenchHost.Dialogs => Dialogs;
        public CancellationToken Lifetime => CancellationToken.None;
        public Task GuardAsync(Func<Task> action) => action();
        public void ReportStatus(string status) => Status = status;
        public Task NavigateAsync(string path, int line, int column) { Navigations.Add((path, line, column)); return Task.CompletedTask; }
        public Task OpenDocumentAsync(string path) { Opened.Add(path); return Task.CompletedTask; }
        public Task<bool> CloseDocumentAsync(string path) { Closed.Add(path); OpenPaths.Remove(path); return Task.FromResult(true); }
    }
}
