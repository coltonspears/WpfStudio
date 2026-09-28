using WpfStudio.Contracts;

namespace WpfStudio.Shell.Tests;

public sealed class XamlDiagnosticsTests
{
    [Fact]
    public async Task SavingAndClosingAFixedXamlFileDoesNotRestoreAnOldIndexError()
    {
        await using var test = new ShellTestContext();
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
        Assert.DoesNotContain(test.Shell.WpfIssues, issue => issue.Id == "XAML001");
    }

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
        Assert.Contains(issue, test.Shell.WpfIssues);
        Assert.True(await test.Shell.CloseDocumentAsync(editor));
        Assert.DoesNotContain(issue, test.Shell.Diagnostics);
        Assert.DoesNotContain(issue, test.Shell.WpfIssues);
    }
}
