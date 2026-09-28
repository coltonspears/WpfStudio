using WpfStudio.Contracts;
using WpfStudio.Workspace;

namespace WpfStudio.Shell.Tests;

public sealed class XamlProjectContextTests
{
    private const string Markup = "<View xmlns:vm=\"clr-namespace:Demo\" xmlns:d=\"http://schemas.microsoft.com/expression/blend/2008\" d:DataContext=\"{d:DesignInstance vm:Model}\"><Label Text=\"{Binding Name}\" /></View>";

    [Fact]
    public async Task LinkedXamlContextControlsDiagnosticsCompletionHoverDefinitionsAndFixes()
    {
        await using var test = new ShellTestContext();
        var view = await test.CreateFileAsync("Shared.xaml", Markup);
        var solution = await test.CreateFileAsync("Fixture.slnx", "<Solution><Project Path=\"First/First.csproj\"/><Project Path=\"Second/Second.csproj\"/></Solution>");
        var projects = new List<string>();
        foreach (var name in new[] { "First", "Second" })
        {
            var project = await test.CreateFileAsync($"{name}/{name}.csproj", "<Project Sdk=\"Microsoft.NET.Sdk\"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup><ItemGroup><None Include=\"../Shared.xaml\" Link=\"Shared.xaml\" /></ItemGroup></Project>");
            projects.Add(Path.GetFullPath(project));
            await test.CreateFileAsync($"{name}/Model.cs", $"namespace Demo; public class Model {{ public string {(name == "First" ? "Name" : "Title")} => \"value\"; }}");
            Assert.Equal(0, (await new BuildService().RunAsync(new(project, BuildOperation.Restore))).ExitCode);
        }
        test.Shell.Workspace = await test.Workspace.LoadAsync(new(solution));
        await test.Shell.OpenDocumentAsync(view);
        var editor = test.Shell.ActiveDocument!;
        Assert.Equal(2, editor.XamlProjects.Count);
        Assert.Null(editor.XamlProject);
        await editor.RefreshAnalysisAsync();
        Assert.Empty(editor.Diagnostics);
        Assert.Contains("multiple project", editor.LanguageStatus);
        int member = Markup.IndexOf("Name}", StringComparison.Ordinal);

        editor.XamlProject = editor.XamlProjects.Single(project => project.Name == "First");
        await editor.RefreshAnalysisAsync();
        Assert.Empty(editor.Diagnostics);
        Assert.Contains("Name", (await editor.HoverAsync(member + 1))!.Text);
        var definition = Assert.Single(await editor.XamlDefinitionAsync(member + 1));
        Assert.Equal(Path.Combine(test.Root, "First", "Model.cs"), definition.Path);

        editor.XamlProject = editor.XamlProjects.Single(project => project.Name == "Second");
        await editor.RefreshAnalysisAsync();
        var issue = Assert.Single(editor.Diagnostics);
        Assert.Equal(projects[1], issue.ProjectPath);
        Assert.Equal("Second", issue.ProjectName);
        var completions = await editor.CompleteAsync(member);
        Assert.Contains(completions.Items, item => item.DisplayText == "Title");
        Assert.DoesNotContain(completions.Items, item => item.DisplayText == "Name");
        Assert.Empty(await editor.XamlDefinitionAsync(member + 1));
        long completionContext = editor.XamlContextRevision;
        var titleCompletion = completions.Items.Single(item => item.DisplayText == "Title");
        Assert.Equal("Title", editor.XamlCompletionEdit(titleCompletion, member, 4, completionContext).NewText);

        editor.XamlProject = editor.XamlProjects.Single(project => project.Name == "First");
        editor.XamlProject = editor.XamlProjects.Single(project => project.Name == "Second");
        Assert.Throws<InvalidOperationException>(() => editor.XamlCompletionEdit(titleCompletion, member, 4, completionContext));
        editor.XamlProject = editor.XamlProjects.Single(project => project.Name == "First");
        editor.State.Content = Markup.Replace("Name}", "Nmae}", StringComparison.Ordinal);
        editor.State.CaretOffset = member;
        await editor.RefreshQuickFixesAsync(member + 1);
        var oldFix = Assert.Single(editor.QuickFixes);
        var hover = editor.HoverAsync(member + 1);
        editor.XamlProject = editor.XamlProjects.Single(project => project.Name == "Second");
        Assert.Empty(editor.QuickFixes);
        Assert.Empty(editor.Diagnostics);
        await oldFix.ApplyCommand.ExecuteAsync(oldFix.Action);
        Assert.Contains("Nmae}", editor.State.Content);
        Assert.Null(await hover);

        // Project-qualified issue navigation selects the exact owner in an
        // already-open editor; it does not reuse the prior linked-file context.
        editor.State.Content = Markup;
        await test.Shell.NavigateDiagnosticCommand.ExecuteAsync(issue);
        Assert.Equal(projects[1], editor.XamlProjectPath);
        Assert.Equal(issue.Start, editor.State.CaretOffset);
        editor.XamlProject = editor.XamlProjects.Single(project => project.Name == "First");
        await test.Shell.NavigateDiagnosticCommand.ExecuteAsync(issue);
        Assert.Equal(projects[1], editor.XamlProjectPath);
    }

    [Fact]
    public async Task ProjectChoicesPreserveSelectionAndInvalidateRemovedOrAmbiguousContexts()
    {
        await using var test = new ShellTestContext();
        var path = await test.CreateFileAsync("View.xaml", "<Grid />");
        await test.Shell.OpenDocumentAsync(path);
        var editor = test.Shell.ActiveDocument!;
        WorkspaceProject first = new("first", "First", Path.Combine(test.Root, "First.csproj"), "net10.0", null, false, []);
        WorkspaceProject second = new("second", "Second", Path.Combine(test.Root, "Second.csproj"), "net10.0", null, false, []);
        editor.SetXamlProjects([first]);
        Assert.Same(first, editor.XamlProject);
        long revision = editor.XamlContextRevision;
        editor.SetXamlProjects([first with { }, second]);
        Assert.Equal(first.ProjectPath, editor.XamlProjectPath);
        Assert.True(editor.XamlContextRevision > revision);
        editor.SetXamlContextUnavailable("Reload required");
        Assert.Equal("Reload required", editor.LanguageStatus);
        Assert.Empty((await editor.CompleteAsync(1)).Items);
        Assert.Null(await editor.HoverAsync(1));
        Assert.Empty(await editor.XamlDefinitionAsync(1));
        await editor.RefreshQuickFixesAsync(1);
        Assert.Empty(editor.QuickFixes);
        await editor.RefreshAnalysisAsync();
        Assert.Equal("Reload required", editor.LanguageStatus);
        editor.SetXamlContextUnavailable(null);
        editor.SetXamlProjects([second, first with { TargetFramework = "net9.0" }]);
        Assert.Null(editor.XamlProject);
        editor.SetXamlProjects([]);
        Assert.False(editor.HasXamlProjects);
        Assert.Null(editor.XamlProjectPath);
    }
}
